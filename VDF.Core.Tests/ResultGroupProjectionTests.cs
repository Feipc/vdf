using VDF.Core.ViewModels;

namespace VDF.Core.Tests;

public sealed class ResultGroupProjectionTests {
	[Fact]
	public void Build_ExcludesReferenceFromSimilarityRange() {
		Guid groupId = Guid.NewGuid();
		var reference = Item(groupId, "/media/source.mp4", 100f, 300, isReference: true);
		var firstClip = Item(groupId, "/media/clip-a.mp4", 99f, 100);
		var secondClip = Item(groupId, "/media/clip-b.mp4", 93f, 200);

		ResultGroupView group = Assert.Single(
			ResultGroupProjection.Build([reference, firstClip, secondClip]));

		Assert.Equal(93f, group.MinimumSimilarity);
		Assert.Equal(99f, group.MaximumSimilarity);
		Assert.Equal(600, group.TotalSize);
		Assert.Equal(300, group.PotentialSavings);
	}

	[Theory]
	[InlineData(99.949f, false)]
	[InlineData(99.95f, true)]
	[InlineData(100f, true)]
	public void Build_HundredPercentMatchesDisplayedOneDecimalScore(
		float similarity,
		bool expected) {
		Guid groupId = Guid.NewGuid();
		ResultGroupView group = Assert.Single(ResultGroupProjection.Build([
			Item(groupId, "/media/a.mp4", 100f, 100),
			Item(groupId, "/media/b.mp4", similarity, 100)
		]));

		Assert.Equal(expected, group.IsDisplayedHundredPercent);
		Assert.Equal(
			expected,
			ResultPresentationUtils.FormatSimilarityScore(group.MinimumSimilarity) == "100.0");
	}

	[Fact]
	public void FilterAndSort_UsesSimilarityThenSavingsThenGroupId() {
		Guid lowSavings = Guid.Parse("00000000-0000-0000-0000-000000000001");
		Guid highSavings = Guid.Parse("00000000-0000-0000-0000-000000000002");
		Guid lowerSimilarity = Guid.Parse("00000000-0000-0000-0000-000000000003");
		var groups = ResultGroupProjection.Build([
			Item(lowSavings, "/media/a1.mp4", 99f, 100),
			Item(lowSavings, "/media/a2.mp4", 99f, 100),
			Item(highSavings, "/media/b1.mp4", 99f, 500),
			Item(highSavings, "/media/b2.mp4", 99f, 400),
			Item(lowerSimilarity, "/media/c1.mp4", 95f, 900),
			Item(lowerSimilarity, "/media/c2.mp4", 95f, 800)
		]);

		var descending = ResultGroupProjection.FilterAndSort(
			groups, string.Empty, null,
			ResultSimilarityFilter.All,
			ResultGroupSort.SimilarityDescending);
		var ascending = ResultGroupProjection.FilterAndSort(
			groups, string.Empty, null,
			ResultSimilarityFilter.All,
			ResultGroupSort.SimilarityAscending);

		Assert.Equal(new[] { highSavings, lowSavings, lowerSimilarity },
			descending.Select(group => group.GroupId));
		Assert.Equal(new[] { lowerSimilarity, highSavings, lowSavings },
			ascending.Select(group => group.GroupId));
	}

	[Fact]
	public void FilterAndSort_CombinesDirectorySearchAndHundredPercentFilter() {
		string root = Path.Combine(Path.GetTempPath(), "vdf-result-projection");
		string wantedFolder = Path.Combine(root, "wanted");
		string otherFolder = Path.Combine(root, "wanted-copy");
		Guid wanted = Guid.NewGuid();
		Guid wrongName = Guid.NewGuid();
		Guid wrongFolder = Guid.NewGuid();
		var groups = ResultGroupProjection.Build([
			Item(wanted, Path.Combine(wantedFolder, "holiday-a.mp4"), 100f, 100),
			Item(wanted, Path.Combine(wantedFolder, "holiday-b.mp4"), 99.95f, 90),
			Item(wrongName, Path.Combine(wantedFolder, "documentary-a.mp4"), 100f, 100),
			Item(wrongName, Path.Combine(wantedFolder, "documentary-b.mp4"), 100f, 90),
			Item(wrongFolder, Path.Combine(otherFolder, "holiday-c.mp4"), 100f, 100),
			Item(wrongFolder, Path.Combine(otherFolder, "holiday-d.mp4"), 100f, 90)
		]);

		var filtered = ResultGroupProjection.FilterAndSort(
			groups,
			"*holiday*",
			wantedFolder,
			ResultSimilarityFilter.DisplayedHundredPercent,
			ResultGroupSort.SimilarityDescending);

		ResultGroupView result = Assert.Single(filtered);
		Assert.Equal(wanted, result.GroupId);
		Assert.All(result.Items, item =>
			Assert.Equal(wantedFolder, Path.GetDirectoryName(item.Path)));
	}

	[Fact]
	public void FilterAndSort_HundredPercentScopeContainsNoItemsFromOtherGroups() {
		Guid hundredPercent = Guid.NewGuid();
		Guid lowerSimilarity = Guid.NewGuid();
		var groups = ResultGroupProjection.Build([
			Item(hundredPercent, "/media/exact-a.mp4", 100f, 100),
			Item(hundredPercent, "/media/exact-b.mp4", 99.95f, 90),
			Item(lowerSimilarity, "/media/lower-a.mp4", 100f, 100),
			Item(lowerSimilarity, "/media/lower-b.mp4", 99.94f, 90)
		]);

		var filtered = ResultGroupProjection.FilterAndSort(
			groups,
			string.Empty,
			null,
			ResultSimilarityFilter.DisplayedHundredPercent,
			ResultGroupSort.SimilarityDescending);
		var scopedItems = filtered.SelectMany(group => group.Items).ToList();

		Assert.Equal(2, scopedItems.Count);
		Assert.All(scopedItems, item => Assert.Equal(hundredPercent, item.GroupId));
	}

	[Fact]
	public void FilterAndSort_ExcludesExactDirectoryAndDropsNewSingletonGroups() {
		string root = Path.Combine(Path.GetTempPath(), "vdf-result-exclusions");
		string excluded = Path.Combine(root, "excluded");
		string excludedChild = Path.Combine(excluded, "child");
		string allowed = Path.Combine(root, "allowed");
		Guid dropped = Guid.NewGuid();
		Guid exactDirectoryOnly = Guid.NewGuid();
		Guid childDirectoryRemains = Guid.NewGuid();
		var groups = ResultGroupProjection.Build([
			Item(dropped, Path.Combine(excluded, "drop-a.mp4"), 100f, 100),
			Item(dropped, Path.Combine(allowed, "drop-b.mp4"), 100f, 90),
			Item(exactDirectoryOnly, Path.Combine(excluded, "exact-a.mp4"), 100f, 100),
			Item(exactDirectoryOnly, Path.Combine(excluded, "exact-b.mp4"), 100f, 90),
			Item(childDirectoryRemains, Path.Combine(excludedChild, "child-a.mp4"), 100f, 100),
			Item(childDirectoryRemains, Path.Combine(excludedChild, "child-b.mp4"), 100f, 90)
		]);

		var filtered = ResultGroupProjection.FilterAndSort(
			groups,
			string.Empty,
			null,
			ResultSimilarityFilter.All,
			ResultGroupSort.SimilarityDescending,
			[excluded]);

		ResultGroupView remaining = Assert.Single(filtered);
		Assert.Equal(childDirectoryRemains, remaining.GroupId);
	}

	[Fact]
	public void PageScopeContainsOnlyCurrentPageWhileAllScopeContainsEveryFilteredGroup() {
		var groups = Enumerable.Range(0, 60)
			.SelectMany(index => {
				Guid groupId = GuidFromInt(index);
				return new[] {
					Item(groupId, $"/media/{index}-a.mp4", 100f, 100),
					Item(groupId, $"/media/{index}-b.mp4", 100f, 90)
				};
			});
		var filtered = ResultGroupProjection.FilterAndSort(
			ResultGroupProjection.Build(groups),
			string.Empty,
			null,
			ResultSimilarityFilter.All,
			ResultGroupSort.SimilarityDescending);

		var pageItems = ResultGroupProjection.GetPage(filtered, 1, 50)
			.SelectMany(group => group.Items)
			.ToList();
		var allItems = filtered.SelectMany(group => group.Items).ToList();

		Assert.Equal(100, pageItems.Count);
		Assert.Equal(120, allItems.Count);
	}

	[Fact]
	public void Page_ClampsAfterResultCountShrinksAndPreservesItemInstances() {
		var groups = Enumerable.Range(0, 120)
			.SelectMany(index => {
				Guid groupId = GuidFromInt(index);
				return new[] {
					Item(groupId, $"/media/{index}-a.mp4", 100f, 100),
					Item(groupId, $"/media/{index}-b.mp4", 99f, 90)
				};
			});
		var built = ResultGroupProjection.Build(groups);

		Assert.Equal(3, ResultGroupProjection.GetPageCount(built.Count, 50));
		Assert.Equal(3, ResultGroupProjection.ClampPage(4, built.Count, 50));
		var lastPage = ResultGroupProjection.GetPage(built, 3, 50);
		Assert.Equal(20, lastPage.Count);
		Assert.Same(built[100], lastPage[0]);

		Assert.Equal(1, ResultGroupProjection.ClampPage(3, 40, 50));
	}

	static DuplicateItem Item(
		Guid groupId,
		string path,
		float similarity,
		long size,
		bool isReference = false) =>
		new() {
			GroupId = groupId,
			Path = path,
			Folder = Path.GetDirectoryName(path) ?? string.Empty,
			Similarity = similarity,
			SizeLong = size,
			IsSimilarityReference = isReference
		};

	static Guid GuidFromInt(int value) {
		byte[] bytes = new byte[16];
		BitConverter.GetBytes(value).CopyTo(bytes, 0);
		return new Guid(bytes);
	}
}

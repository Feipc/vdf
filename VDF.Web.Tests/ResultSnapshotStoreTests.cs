using VDF.Core;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests;

public sealed class ResultSnapshotStoreTests : IDisposable {
	readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		$"vdf-web-results-{Guid.NewGuid():N}");
	readonly string _path;

	public ResultSnapshotStoreTests() {
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "WebResults.json");
	}

	[Fact]
	public async Task SaveAndLoad_RoundTripsProtectionAndModificationDates() {
		using var store = new ResultSnapshotStore(_path);
		var item = Item(Guid.NewGuid(), Path.Combine(_directory, "keep", "a.mp4"), 100f);
		item.DateModified = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
		string folder = Path.GetDirectoryName(item.Path)!;
		await store.SaveAsync([item], [], [], protectedDirectories: [folder]);
		var restored = await store.LoadAsync();
		Assert.NotNull(restored);
		Assert.Equal(new[] { folder }, restored.ProtectedDirectories);
		Assert.Equal(item.DateModified, Assert.Single(restored.Items).DateModified);
	}

	[Fact]
	public async Task Load_LegacyResultsHaveNoModificationDateOrProtection() {
		await File.WriteAllTextAsync(_path, """
			{"Version":2,"Items":[{"Path":"/media/a.mp4"}],"SelectedPaths":[],"ExcludedDirectories":[]}
			""");
		using var store = new ResultSnapshotStore(_path);
		var restored = await store.LoadAsync();
		Assert.NotNull(restored);
		Assert.Empty(restored.ProtectedDirectories);
		Assert.Null(Assert.Single(restored.Items).DateModified);
	}

	[Fact]
	public async Task SaveAndLoad_RoundTripsResultSemanticsWithoutThumbnails() {
		Guid groupId = Guid.NewGuid();
		var reference = Item(groupId, "/media/source.mp4", 100f);
		reference.IsSimilarityReference = true;
		reference.ImageList.Add([1, 2, 3]);
		var clip = Item(groupId, "/media/clip.mp4", 97.25f);
		clip.Flags = DuplicateFlags.PartialClip;
		clip.PartialClipOffset = TimeSpan.FromSeconds(42);
		clip.SimilarityReferencePath = reference.Path;
		var store = new ResultSnapshotStore(_path);

		await store.SaveAsync(
			[reference, clip],
			[clip.Path],
			["/media/archive"]);
		WebResultSnapshot? snapshot = await store.LoadAsync();
		Assert.NotNull(snapshot);
		IReadOnlyList<DuplicateItem> restored = snapshot.Items;

		Assert.Equal(2, restored.Count);
		DuplicateItem restoredReference = restored.Single(item => item.Path == reference.Path);
		DuplicateItem restoredClip = restored.Single(item => item.Path == clip.Path);
		Assert.Equal(groupId, restoredReference.GroupId);
		Assert.True(restoredReference.IsSimilarityReference);
		Assert.Empty(restoredReference.ImageList);
		Assert.Equal(97.25f, restoredClip.Similarity);
		Assert.Equal(DuplicateFlags.PartialClip, restoredClip.Flags);
		Assert.Equal(TimeSpan.FromSeconds(42), restoredClip.PartialClipOffset);
		Assert.Equal(reference.Path, restoredClip.SimilarityReferencePath);
		Assert.Equal(new[] { clip.Path }, snapshot.SelectedPaths);
		Assert.Equal(new[] { "/media/archive" }, snapshot.ExcludedDirectories);
		Assert.False(File.Exists(_path + ".tmp"));
	}

	[Fact]
	public async Task SaveEmpty_DeletesPreviousSnapshot() {
		var store = new ResultSnapshotStore(_path);
		Guid groupId = Guid.NewGuid();
		await store.SaveAsync(
			[
				Item(groupId, "/media/a.mp4", 100f),
				Item(groupId, "/media/b.mp4", 99f)
			],
			[],
			[]);
		Assert.True(File.Exists(_path));

		await store.SaveAsync([], [], []);

		Assert.False(File.Exists(_path));
	}

	[Fact]
	public async Task Load_CorruptSnapshotIsQuarantinedAndReturnsNoResults() {
		await File.WriteAllTextAsync(_path, "{not-json");
		var store = new ResultSnapshotStore(_path);

		WebResultSnapshot? restored = await store.LoadAsync();

		Assert.Null(restored);
		Assert.False(File.Exists(_path));
		Assert.Single(Directory.GetFiles(
			_directory,
			"WebResults.json.corrupt-*"));
	}

	[Fact]
	public async Task Load_MissingSnapshotReturnsNoResults() {
		var store = new ResultSnapshotStore(_path);

		WebResultSnapshot? restored = await store.LoadAsync();

		Assert.Null(restored);
	}

	[Fact]
	public async Task Load_VersionOneSnapshotDefaultsReviewStateToEmpty() {
		Guid groupId = Guid.NewGuid();
		string json =
			$$"""{"Version":1,"Items":[{"GroupId":"{{groupId}}","Path":"/media/a.mp4","Folder":"/media","Similarity":100,"HdrFormat":""},{"GroupId":"{{groupId}}","Path":"/media/b.mp4","Folder":"/media","Similarity":100,"HdrFormat":""}]}""";
		await File.WriteAllTextAsync(_path, json);
		var store = new ResultSnapshotStore(_path);

		WebResultSnapshot? restored = await store.LoadAsync();

		Assert.NotNull(restored);
		Assert.Equal(2, restored.Items.Count);
		Assert.Empty(restored.SelectedPaths);
		Assert.Empty(restored.ExcludedDirectories);
	}

	static DuplicateItem Item(Guid groupId, string path, float similarity) =>
		new() {
			GroupId = groupId,
			Path = path,
			Folder = Path.GetDirectoryName(path) ?? string.Empty,
			Similarity = similarity,
			SizeLong = 1234,
			Duration = TimeSpan.FromMinutes(2),
			FrameSize = "1920x1080",
			FrameSizeInt = 1920 + 1080,
			Format = "h264",
			AudioFormat = "aac",
			HdrFormat = string.Empty
		};

	public void Dispose() {
		try { Directory.Delete(_directory, recursive: true); }
		catch { }
	}
}

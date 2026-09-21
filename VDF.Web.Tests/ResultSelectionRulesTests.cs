using VDF.Core;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests;

public sealed class ResultSelectionRulesTests {
	static readonly string Root = Path.Combine(Path.GetTempPath(), "vdf-selection-rules");
	static readonly string Protected = Path.Combine(Root, "keep");
	static DuplicateItem Item(string path, int createdMonth = 1, int modifiedMonth = 1, Guid? group = null) => new() {
		Path = Path.Combine(Root, path),
		GroupId = group ?? Guid.Empty,
		DateCreated = new DateTime(2026, createdMonth, 1, 0, 0, 0, DateTimeKind.Utc),
		DateModified = new DateTime(2026, modifiedMonth, 1, 0, 0, 0, DateTimeKind.Utc)
	};

	[Fact]
	public void FolderRule_ProtectsEveryMatchIncludingChildren_AndSkipsUnmatchedGroups() {
		var a = Item("keep/a.mp4");
		var b = Item("keep/sub/b.mp4");
		var c = Item("keep-other/c.mp4");
		Guid other = Guid.NewGuid();
		var items = new[] { a, b, c, Item("other/d.mp4", group: other), Item("other/e.mp4", group: other) };
		var selected = ResultSelectionRules.SelectForDeletion(items, items, [Protected], ResultSelectionRule.KeepProtectedFolders);
		Assert.Equal(new[] { c }, selected);
	}

	[Theory]
	[InlineData(ResultSelectionRule.KeepNewestCreated, "c.mp4")]
	[InlineData(ResultSelectionRule.KeepOldestCreated, "a.mp4")]
	[InlineData(ResultSelectionRule.KeepNewestModified, "a.mp4")]
	[InlineData(ResultSelectionRule.KeepOldestModified, "c.mp4")]
	public void DateRule_SelectsAllExceptRequestedExtreme(ResultSelectionRule rule, string keeper) {
		var items = new[] { Item("a.mp4", 1, 3), Item("b.mp4", 2, 2), Item("c.mp4", 3, 1) };
		var selected = ResultSelectionRules.SelectForDeletion(items, items, [], rule);
		Assert.Equal(2, selected.Count);
		Assert.DoesNotContain(selected, item => Path.GetFileName(item.Path) == keeper);
	}

	[Theory]
	[InlineData(ResultSelectionRule.KeepNewestCreated)]
	[InlineData(ResultSelectionRule.KeepOldestCreated)]
	[InlineData(ResultSelectionRule.KeepNewestModified)]
	[InlineData(ResultSelectionRule.KeepOldestModified)]
	public void DateRule_KeepsTiesAndProtectedFiles(ResultSelectionRule rule) {
		bool newest = rule is ResultSelectionRule.KeepNewestCreated or ResultSelectionRule.KeepNewestModified;
		int extreme = newest ? 3 : 1;
		int discarded = newest ? 1 : 3;
		var items = new[] {
			Item("a.mp4", extreme, extreme), Item("b.mp4", extreme, extreme),
			Item("keep/c.mp4", discarded, discarded), Item("d.mp4", discarded, discarded)
		};
		var selected = ResultSelectionRules.SelectForDeletion(items, items, [Protected], rule);
		Assert.Equal(new[] { items[3] }, selected);
	}

	[Fact]
	public void Scope_UsesCompleteGroupsButOnlySelectsScopedFiles() {
		var items = new[] { Item("keep/a.mp4", 3), Item("b.mp4", 1), Item("c.mp4", 2) };
		foreach (var rule in new[] { ResultSelectionRule.KeepProtectedFolders, ResultSelectionRule.KeepNewestCreated }) {
			var selected = ResultSelectionRules.SelectForDeletion(items, [items[1]], [Protected], rule);
			Assert.Equal(new[] { items[1] }, selected);
		}
	}

	[Fact]
	public void DateRule_ComparesGroupsIndependently() {
		Guid other = Guid.NewGuid();
		var items = new[] {
			Item("a.mp4", 1), Item("b.mp4", 2),
			Item("c.mp4", 3, group: other), Item("d.mp4", 4, group: other)
		};
		var selected = ResultSelectionRules.SelectForDeletion(items, items, [], ResultSelectionRule.KeepNewestCreated);
		Assert.Equal(2, selected.Count);
		Assert.Contains(items[0], selected);
		Assert.Contains(items[2], selected);
	}

	[Fact]
	public void MissingDates_SkipsEntireGroup_WithoutGuessing() {
		var items = new[] { Item("a.mp4"), Item("b.mp4", 3) };
		items[0].DateModified = null;
		items[0].DateCreated = default;
		foreach (var rule in Enum.GetValues<ResultSelectionRule>().Where(rule => rule != ResultSelectionRule.KeepProtectedFolders))
			Assert.Empty(ResultSelectionRules.SelectForDeletion(items, items, [], rule));
	}

	[Fact]
	public void InvalidPaths_CannotActAsFolderKeepers() {
		var items = new[] { Item("a.mp4"), Item("b.mp4") };
		items[0].Path = "\0invalid";
		Assert.Empty(ResultSelectionRules.SelectForDeletion(items, items, [Protected], ResultSelectionRule.KeepProtectedFolders));
	}

	[Fact]
	public void AllDatesEqual_NoProtectionMatches_AndSingletons_SelectNothing() {
		var items = new[] { Item("a.mp4"), Item("b.mp4"), Item("single.mp4", group: Guid.NewGuid()) };
		foreach (var rule in Enum.GetValues<ResultSelectionRule>())
			Assert.Empty(ResultSelectionRules.SelectForDeletion(items, items, [], rule));
	}

	[Fact]
	public void Protection_NormalizesPathsAndRespectsDirectoryBoundaries() {
		Assert.True(ResultSelectionRules.IsProtected(Path.Combine(Protected, "sub", "a.mp4"), [Protected + Path.DirectorySeparatorChar]));
		Assert.False(ResultSelectionRules.IsProtected(Path.Combine(Root, "keep-other", "a.mp4"), [Protected]));
		Assert.False(ResultSelectionRules.IsProtected(Path.Combine(Protected, "..", "a.mp4"), [Protected]));
		Assert.True(ResultSelectionRules.IsProtected(Path.Combine(Protected, "a.mp4"), [Path.GetPathRoot(Protected)!]));
		Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), ResultSelectionRules.IsProtected(Path.Combine(Protected.ToUpperInvariant(), "a.mp4"), [Protected]));
		Assert.Throws<ArgumentException>(() => ResultSelectionRules.NormalizeDirectory("relative-folder"));
	}

	[Fact]
	public void DuplicateItem_CopiesBothScanDates() {
		var entry = new FileEntry { Path = Path.Combine(Root, "a.mp4"), DateCreated = new DateTime(2026, 1, 1), DateModified = new DateTime(2026, 3, 1) };
		var item = new DuplicateItem(entry, 0, Guid.NewGuid(), default);
		Assert.Equal(entry.DateCreated, item.DateCreated);
		Assert.Equal(entry.DateModified, item.DateModified);
	}
}

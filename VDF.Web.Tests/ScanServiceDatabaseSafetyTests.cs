using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.Web.Services;

namespace VDF.Web.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebDatabaseTestCollection {
	public const string Name = "Web database state";
}

[Collection(WebDatabaseTestCollection.Name)]
public sealed class ScanServiceDatabaseSafetyTests {
	[Fact]
	public async Task ProtectedFolders_ClearExistingSelections_AndSurviveSnapshotRestore() {
		using var context = new TestContext();
		string first = context.CreateMediaFile("keep/first.mp4");
		string second = context.CreateMediaFile("second.mp4");
		context.SaveDatabase(first, second);
		await context.SaveResultsAsync(first, second);
		Assert.True(await context.Service.InitializeAsync());
		context.Service.UpdateResultReviewState([first, second], []);
		context.Service.UpdateProtectedResultDirectories([Path.GetDirectoryName(first)!]);
		Assert.Equal(new[] { second }, context.Service.SelectedResultPaths);
		// A stale client or manual selection cannot select a protected file again.
		context.Service.UpdateResultReviewState([first, second], []);
		Assert.Equal(new[] { second }, context.Service.SelectedResultPaths);
		await context.Service.FlushResultsSnapshotAsync();
		using var restored = new ScanService(new WebSettingsService(), context.ThumbnailService, context.SnapshotStore);
		restored.Settings.CustomDatabaseFolder = context.DatabaseDirectory;
		Assert.True(await restored.InitializeAsync());
		Assert.True(restored.IsResultProtected(first));
		Assert.False(restored.IsResultProtected(second));
		Assert.Equal(new[] { second }, restored.SelectedResultPaths);
		restored.Reset();
		await restored.FlushResultsSnapshotAsync();
		var cleared = await context.SnapshotStore.LoadAsync();
		Assert.NotNull(cleared);
		Assert.Empty(cleared.Items);
		Assert.Equal(new[] { Path.GetDirectoryName(first)! }, cleared.ProtectedDirectories);
	}

	[Theory]
	[InlineData("permanent")]
	[InlineData("trash")]
	[InlineData("move")]
	[InlineData("link")]
	public async Task ProtectedFolders_BlockFileOperationsEvenForDirectServiceCalls(string operation) {
		using var context = new TestContext();
		string target = context.CreateMediaFile("keep/child/target.mp4");
		context.Service.UpdateProtectedResultDirectories([Path.Combine(context.Root, "keep")]);
		var item = context.ResultItem(Guid.NewGuid(), target);
		byte[] original = File.ReadAllBytes(target);
		FileOpResult result = operation switch {
			"move" => await context.Service.MoveItemsAsync([item], Path.Combine(context.Root, "destination")),
			"link" => await context.Service.CreateLinksAsync([item], false),
			_ => await context.Service.DeleteItemsAsync([item], operation == "permanent")
		};
		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.Contains("Protected file", Assert.Single(result.Errors));
		Assert.Equal(original, File.ReadAllBytes(target));
	}

	[Fact]
	public async Task InitializeAsync_LoadsDatabaseBeforeRestoringResults() {
		using var context = new TestContext();
		string first = context.CreateMediaFile("first.mp4");
		string second = context.CreateMediaFile("second.mp4");
		context.SaveDatabase(first, second);
		await context.SaveResultsAsync(first, second);

		Assert.True(await context.Service.InitializeAsync());

		Assert.True(context.Service.DatabaseLoadedSuccessfully);
		Assert.Equal(2, context.Service.DatabaseEntryCount);
		Assert.Equal(2, context.Service.Duplicates.Count);
	}

	[Fact]
	public async Task InitializeAsync_WhenDatabaseLoadFails_DoesNotRestoreResults() {
		using var context = new TestContext();
		string first = context.CreateMediaFile("first.mp4");
		string second = context.CreateMediaFile("second.mp4");
		File.WriteAllText(context.MainDatabasePath, "corrupt");
		await context.SaveResultsAsync(first, second);

		Assert.False(await context.Service.InitializeAsync());

		Assert.False(context.Service.DatabaseLoadedSuccessfully);
		Assert.Empty(context.Service.Duplicates);
	}

	[Fact]
	public async Task InitializeAsync_WhenConfiguredDatabaseFolderIsMissing_DoesNotRestoreResults() {
		using var context = new TestContext();
		string first = context.CreateMediaFile("first.mp4");
		string second = context.CreateMediaFile("second.mp4");
		await context.SaveResultsAsync(first, second);
		context.Service.Settings.CustomDatabaseFolder =
			Path.Combine(context.Root, "missing-database-folder");

		Assert.False(await context.Service.InitializeAsync());

		Assert.False(context.Service.DatabaseLoadedSuccessfully);
		Assert.Empty(context.Service.Duplicates);
	}

	[Fact]
	public async Task DeleteAfterRestart_PreservesEveryUnrelatedDatabaseEntry() {
		using var context = new TestContext();
		string target = context.CreateMediaFile("target.mp4");
		string keeper = context.CreateMediaFile("keeper.mp4");
		string unrelated = context.CreateMediaFile("unrelated.mp4");
		context.SaveDatabase(target, keeper, unrelated);
		await context.SaveResultsAsync(target, keeper);
		Assert.True(await context.Service.InitializeAsync());
		DuplicateItem targetItem =
			context.Service.Duplicates.Single(item => item.Path == target);

		FileOpResult result = await context.Service.DeleteItemsAsync(
			[targetItem],
			permanent: true);

		Assert.Equal(1, result.Done);
		Assert.False(File.Exists(target));
		Assert.Equal(
			new[] { keeper, unrelated }.OrderBy(path => path),
			DatabaseUtils.Database.Select(entry => entry.Path).OrderBy(path => path));
	}

	[Fact]
	public async Task Delete_WhenDatabaseLoadFails_DoesNotMutateMediaOrResults() {
		using var context = new TestContext();
		string target = context.CreateMediaFile("target.mp4");
		File.WriteAllText(context.MainDatabasePath, "corrupt");
		DuplicateItem item = context.ResultItem(Guid.NewGuid(), target);

		FileOpResult result = await context.Service.DeleteItemsAsync(
			[item],
			permanent: true);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.True(File.Exists(target));
		Assert.Equal("corrupt", File.ReadAllText(context.MainDatabasePath));
	}

	[Fact]
	public async Task Move_WhenDatabaseLoadFails_DoesNotCreateDestinationOrMoveMedia() {
		using var context = new TestContext();
		string target = context.CreateMediaFile("target.mp4");
		string destination = Path.Combine(context.Root, "destination");
		File.WriteAllText(context.MainDatabasePath, "corrupt");
		DuplicateItem item = context.ResultItem(Guid.NewGuid(), target);

		FileOpResult result = await context.Service.MoveItemsAsync(
			[item],
			destination);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.True(File.Exists(target));
		Assert.False(Directory.Exists(destination));
		Assert.Equal("corrupt", File.ReadAllText(context.MainDatabasePath));
	}

	[Fact]
	public async Task Link_WhenDatabaseLoadFails_DoesNotReplaceMedia() {
		using var context = new TestContext();
		string target = context.CreateMediaFile("target.mp4");
		File.WriteAllText(context.MainDatabasePath, "corrupt");
		DuplicateItem item = context.ResultItem(Guid.NewGuid(), target);
		byte[] original = File.ReadAllBytes(target);

		FileOpResult result = await context.Service.CreateLinksAsync(
			[item],
			hardLinks: false);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.Equal(original, File.ReadAllBytes(target));
		Assert.Null(File.ResolveLinkTarget(target, returnFinalTarget: false));
		Assert.Equal("corrupt", File.ReadAllText(context.MainDatabasePath));
	}

	[Fact]
	public async Task Delete_WhenRequiredBackupFails_DoesNotMutateMediaOrResults() {
		using var context = new TestContext();
		string target = context.CreateMediaFile("target.mp4");
		string keeper = context.CreateMediaFile("keeper.mp4");
		context.SaveDatabase(target, keeper);
		await context.SaveResultsAsync(target, keeper);
		Assert.True(await context.Service.InitializeAsync());
		Directory.CreateDirectory(context.BackupTemporaryPath);
		DuplicateItem targetItem =
			context.Service.Duplicates.Single(item => item.Path == target);

		FileOpResult result = await context.Service.DeleteItemsAsync(
			[targetItem],
			permanent: true);

		Assert.Equal(0, result.Done);
		Assert.Equal(1, result.Failed);
		Assert.NotEmpty(result.Errors);
		Assert.True(File.Exists(target));
		Assert.Equal(2, context.Service.Duplicates.Count);
		Assert.Equal(2, context.Service.DatabaseEntryCount);
	}

	[Fact]
	public async Task StartScan_WhenRequiredBackupFails_LeavesRestoredResultsIntact() {
		using var context = new TestContext();
		string first = context.CreateMediaFile("first.mp4");
		string second = context.CreateMediaFile("second.mp4");
		context.SaveDatabase(first, second);
		await context.SaveResultsAsync(first, second);
		Assert.True(await context.Service.InitializeAsync());
		Directory.CreateDirectory(context.BackupTemporaryPath);

		await context.Service.StartScanAndCompare();

		Assert.Equal(ScanState.Error, context.Service.State);
		Assert.Contains("backup", context.Service.ErrorMessage!.ToLowerInvariant());
		Assert.Equal(2, context.Service.Duplicates.Count);
		Assert.True(File.Exists(first));
		Assert.True(File.Exists(second));
	}

	sealed class TestContext : IDisposable {
		public string Root { get; } = Path.Combine(
			Path.GetTempPath(),
			$"vdf-web-database-safety-{Guid.NewGuid():N}");
		public string DatabaseDirectory { get; }
		public string MainDatabasePath =>
			Path.Combine(DatabaseDirectory, "ScannedFiles.db");
		public string BackupTemporaryPath =>
			Path.Combine(DatabaseDirectory, "ScannedFiles.backup-new.db");
		public ResultSnapshotStore SnapshotStore { get; }
		public ResultThumbnailService ThumbnailService { get; }
		public ScanService Service { get; }

		public TestContext() {
			DatabaseDirectory = Path.Combine(Root, "database");
			Directory.CreateDirectory(DatabaseDirectory);
			DatabaseUtils.ConfigureDatabaseFolder(DatabaseDirectory);
			DatabaseUtils.Database.Clear();

			SnapshotStore = new ResultSnapshotStore(
				Path.Combine(Root, "WebResults.json"));
			ThumbnailService = new ResultThumbnailService(
				maxConcurrentExtractions: 1,
				maxCacheBytes: 1024);
			Service = new ScanService(
				new WebSettingsService(),
				ThumbnailService,
				SnapshotStore);
			Service.Settings.CustomDatabaseFolder = DatabaseDirectory;
		}

		public string CreateMediaFile(string name) {
			string path = Path.Combine(Root, name);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, [1, 2, 3, 4]);
			return path;
		}

		public void SaveDatabase(params string[] paths) {
			DatabaseUtils.Database.Clear();
			foreach (string path in paths)
				DatabaseUtils.Database.Add(new FileEntry(new FileInfo(path)));
			DatabaseUtils.SaveDatabase();
		}

		public async Task SaveResultsAsync(string first, string second) {
			Guid groupId = Guid.NewGuid();
			await SnapshotStore.SaveAsync(
				[
					ResultItem(groupId, first),
					ResultItem(groupId, second)
				],
				[],
				[]);
		}

		public DuplicateItem ResultItem(Guid groupId, string path) =>
			new() {
				GroupId = groupId,
				Path = path,
				Folder = Path.GetDirectoryName(path) ?? string.Empty,
				Similarity = 100f,
				SizeLong = File.Exists(path) ? new FileInfo(path).Length : 0
			};

		public void Dispose() {
			Service.Dispose();
			ThumbnailService.Dispose();
			SnapshotStore.Dispose();
			DatabaseUtils.Database.Clear();
			DatabaseUtils.ConfigureDatabaseFolder(null);
			try { Directory.Delete(Root, recursive: true); }
			catch { }
		}
	}
}

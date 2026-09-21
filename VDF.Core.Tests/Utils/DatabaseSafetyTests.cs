using MemoryPack;
using VDF.Core.Utils;

namespace VDF.Core.Tests.Utils;

[Collection(DatabaseTestCollection.Name)]
public sealed class DatabaseSafetyTests : IDisposable {
	readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		$"vdf-database-safety-{Guid.NewGuid():N}");

	public DatabaseSafetyTests() {
		Directory.CreateDirectory(_directory);
		DatabaseUtils.CustomDatabaseFolder = _directory;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.Database.Clear();
	}

	[Fact]
	public void CreateBackup_RotatesAndKeepsExactlyFiveNewestVersions() {
		for (int version = 1; version <= 7; version++) {
			SaveVersion(version);
			Assert.True(DatabaseUtils.CreateBackup());
		}

		for (int slot = 1; slot <= 5; slot++)
			Assert.True(File.Exists(BackupPath(slot)));
		Assert.False(File.Exists(BackupPath(6)));
		Assert.Equal("version-7.mp4", ReadOnlyEntryName(BackupPath(1)));
		Assert.Equal("version-6.mp4", ReadOnlyEntryName(BackupPath(2)));
		Assert.Equal("version-5.mp4", ReadOnlyEntryName(BackupPath(3)));
		Assert.Equal("version-4.mp4", ReadOnlyEntryName(BackupPath(4)));
		Assert.Equal("version-3.mp4", ReadOnlyEntryName(BackupPath(5)));
	}

	[Fact]
	public void CreateBackup_WhenMainDatabaseDoesNotExist_SucceedsWithoutCreatingBackup() {
		Assert.False(File.Exists(MainPath));

		Assert.True(DatabaseUtils.CreateBackup());

		Assert.Empty(Directory.GetFiles(
			_directory,
			"ScannedFiles.backup-*.db"));
	}

	[Fact]
	public void LoadDatabase_CorruptMain_RestoresNewestValidBackup() {
		SaveVersion(1);
		Assert.True(DatabaseUtils.CreateBackup());
		File.WriteAllText(MainPath, "corrupt-main");
		DatabaseUtils.Database.Clear();

		Assert.True(DatabaseUtils.LoadDatabase());

		Assert.Equal(
			"version-1.mp4",
			Path.GetFileName(Assert.Single(DatabaseUtils.Database).Path));
		Assert.Equal("version-1.mp4", ReadOnlyEntryName(MainPath));
	}

	[Fact]
	public void LoadDatabase_ValidTemporaryDatabase_IsPromotedBeforeBackup() {
		SaveVersion(2);
		string temporaryPath = Path.Combine(_directory, "ScannedFiles_new.db");
		File.Move(MainPath, temporaryPath);
		DatabaseUtils.Database.Clear();

		Assert.True(DatabaseUtils.LoadDatabase());
		Assert.True(File.Exists(MainPath));
		Assert.False(File.Exists(temporaryPath));
		Assert.True(DatabaseUtils.CreateBackup());
		Assert.Equal("version-2.mp4", ReadOnlyEntryName(BackupPath(1)));
	}

	[Fact]
	public void LoadDatabase_CorruptNewestBackup_FallsBackToNextValidSlot() {
		SaveVersion(1);
		Assert.True(DatabaseUtils.CreateBackup());
		SaveVersion(2);
		Assert.True(DatabaseUtils.CreateBackup());
		File.WriteAllText(BackupPath(1), "corrupt-newest-backup");
		File.WriteAllText(MainPath, "corrupt-main");
		DatabaseUtils.Database.Clear();

		Assert.True(DatabaseUtils.LoadDatabase());

		Assert.Equal(
			"version-1.mp4",
			Path.GetFileName(Assert.Single(DatabaseUtils.Database).Path));
		Assert.Equal("version-1.mp4", ReadOnlyEntryName(MainPath));
		Assert.Equal("corrupt-newest-backup", File.ReadAllText(BackupPath(1)));
	}

	[Fact]
	public void CreateBackup_WhenTemporaryBackupCannotBeCreated_ReturnsFalse() {
		SaveVersion(1);
		Directory.CreateDirectory(Path.Combine(
			_directory,
			"ScannedFiles.backup-new.db"));

		Assert.False(DatabaseUtils.CreateBackup());

		Assert.False(File.Exists(BackupPath(1)));
		Assert.True(File.Exists(MainPath));
	}

	void SaveVersion(int version) {
		DatabaseUtils.Database.Clear();
		DatabaseUtils.Database.Add(new FileEntry {
			Path = $"version-{version}.mp4"
		});
		DatabaseUtils.SaveDatabase();
	}

	string ReadOnlyEntryName(string path) {
		using var stream = File.OpenRead(path);
		Span<byte> header = stackalloc byte[8];
		stream.ReadExactly(header);
		Assert.Equal("VDFDB001"u8.ToArray(), header.ToArray());
		DatabaseWrapper wrapper =
			MemoryPackSerializer.DeserializeAsync<DatabaseWrapper>(stream)
				.AsTask().GetAwaiter().GetResult()!;
		return Path.GetFileName(Assert.Single(wrapper.Entries).Path);
	}

	string MainPath => Path.Combine(_directory, "ScannedFiles.db");
	string BackupPath(int slot) =>
		Path.Combine(_directory, $"ScannedFiles.backup-{slot}.db");

	public void Dispose() {
		DatabaseUtils.CustomDatabaseFolder = null;
		DatabaseUtils.InvalidateDatabaseFolder();
		DatabaseUtils.Database.Clear();
		try { Directory.Delete(_directory, recursive: true); }
		catch { }
	}
}

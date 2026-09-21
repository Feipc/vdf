// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MemoryPack;

namespace VDF.Core.Utils {
	static class DatabaseUtils {
		static DatabaseUtils() => MemoryPackRegistration.Register();
		const int BackupSlotCount = 5;
		static readonly object DatabaseIoLock = new();

		// New databases are MemoryPack payloads behind this magic header; files without
		// it are protobuf-net databases from 3.x / early 4.x, decoded by
		// LegacyDatabaseReader and migrated to the new format on the next save.
		static ReadOnlySpan<byte> FormatMagic => "VDFDB001"u8;

		internal static HashSet<FileEntry> Database => DbWrapper.Entries;
		internal static int DbVersion => DbWrapper.Version;
		static DatabaseWrapper DbWrapper = new();
		internal static string? CustomDatabaseFolder;
		static string? _resolvedDatabaseFolder;

		static string ResolveDatabaseFolder() => CoreUtils.ResolveDatabaseFolder(CustomDatabaseFolder);

		internal static void InvalidateDatabaseFolder() {
			lock (DatabaseIoLock)
				_resolvedDatabaseFolder = null;
		}

		static string DatabaseFolder => _resolvedDatabaseFolder ??= ResolveDatabaseFolder();

		static string CurrentDatabasePath => FileUtils.SafePathCombine(DatabaseFolder, "ScannedFiles.db");
		static string TempDatabasePath => FileUtils.SafePathCombine(DatabaseFolder, "ScannedFiles_new.db");
		static string BackupTempPath => FileUtils.SafePathCombine(DatabaseFolder, "ScannedFiles.backup-new.db");
		static string RestoreTempPath => FileUtils.SafePathCombine(DatabaseFolder, "ScannedFiles.restore-new.db");
		static string BackupPath(int slot) =>
			FileUtils.SafePathCombine(DatabaseFolder, $"ScannedFiles.backup-{slot}.db");

		internal static string ConfiguredDatabaseFolder {
			get {
				lock (DatabaseIoLock)
					return DatabaseFolder;
			}
		}

		internal static void ConfigureDatabaseFolder(string? folder) {
			lock (DatabaseIoLock) {
				CustomDatabaseFolder = folder;
				_resolvedDatabaseFolder = null;
			}
		}

		internal static bool LoadDatabase() {
			lock (DatabaseIoLock)
				return LoadDatabaseLocked();
		}

		static bool LoadDatabaseLocked() {
			var stopwatch = Stopwatch.StartNew();
			bool candidateFound = false;

			if (File.Exists(TempDatabasePath)) {
				candidateFound = true;
				if (TryReadDatabase(TempDatabasePath, out DatabaseWrapper? temporary, out Exception? error)) {
					if (!PromoteTemporaryDatabase(out Exception? promoteError)) {
						Logger.Instance.Info(
							$"Promoting the recovered temporary scan database failed: " +
							$"{promoteError?.Message ?? "unknown error"}");
						return false;
					}
					AcceptLoadedDatabase(temporary!, CurrentDatabasePath, stopwatch.Elapsed);
					return true;
				}
				LogLoadFailure("temporary database", TempDatabasePath, error);
				if (!DeleteInvalidTemporaryDatabase())
					return false;
			}

			if (File.Exists(CurrentDatabasePath)) {
				candidateFound = true;
				if (TryReadDatabase(CurrentDatabasePath, out DatabaseWrapper? current, out Exception? error)) {
					AcceptLoadedDatabase(current!, CurrentDatabasePath, stopwatch.Elapsed);
					return true;
				}
				LogLoadFailure("main database", CurrentDatabasePath, error);
				QuarantineDamagedMainDatabase();
			}

			for (int slot = 1; slot <= BackupSlotCount; slot++) {
				string backupPath = BackupPath(slot);
				if (!File.Exists(backupPath))
					continue;
				candidateFound = true;
				if (!TryReadDatabase(backupPath, out DatabaseWrapper? backup, out Exception? error)) {
					LogLoadFailure($"backup slot {slot}", backupPath, error);
					continue;
				}
				if (!RestoreBackupToMain(backupPath, out Exception? restoreError)) {
					Logger.Instance.Info(
						$"Restoring scan database from backup slot {slot} failed: " +
						$"{restoreError?.Message ?? "unknown error"}");
					return false;
				}

				DbWrapper = backup!;
				MigrateImageHashesIfNeeded();
				stopwatch.Stop();
				Logger.Instance.Info(
					$"Automatically recovered scan database from backup slot {slot}; " +
					$"{Database.Count:N0} entries restored in {stopwatch.Elapsed}.");
				return true;
			}

			if (!candidateFound) {
				DbWrapper = new DatabaseWrapper();
				MigrateImageHashesIfNeeded();
				Logger.Instance.Info(
					$"No scan database was found in '{DatabaseFolder}'; starting with an empty database.");
				return true;
			}

			Logger.Instance.Info(
				"Loading the scan database failed: no valid temporary, main, or backup database was available.");
			return false;
		}

		static bool TryReadDatabase(
			string path,
			out DatabaseWrapper? wrapper,
			out Exception? error) {
			wrapper = null;
			error = null;
			try {
				using var file = new FileStream(
					path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read);
				if (file.Length == 0)
					throw new InvalidDataException("The database file is empty.");

				Span<byte> header = stackalloc byte[8];
				int headerRead = file.Read(header);
				if (headerRead == FormatMagic.Length && header.SequenceEqual(FormatMagic)) {
					wrapper = MemoryPackSerializer.DeserializeAsync<DatabaseWrapper>(file)
						.AsTask().GetAwaiter().GetResult();
					if (wrapper == null)
						throw new InvalidDataException("The database payload is empty.");
				}
				else {
					file.Position = 0;
					if (file.Length > int.MaxValue)
						throw new InvalidDataException("The legacy database is too large to load.");
					byte[] raw = new byte[file.Length];
					file.ReadExactly(raw);
					wrapper = LegacyDatabaseReader.Read(raw);
					Logger.Instance.Info(
						"Legacy database format detected; it will be stored in the new format on the next save.");
				}
				if (wrapper?.Entries == null)
					throw new InvalidDataException("The database entry collection is missing.");
				return true;
			}
			catch (Exception ex) {
				error = ex;
				return false;
			}
		}

		static void AcceptLoadedDatabase(
			DatabaseWrapper wrapper,
			string sourcePath,
			TimeSpan elapsed) {
			DbWrapper = wrapper;
			MigrateImageHashesIfNeeded();
			Logger.Instance.Info(
				$"Scan database loaded from '{sourcePath}': {Database.Count:N0} entries in {elapsed}.");
		}

		static void LogLoadFailure(string source, string path, Exception? error) =>
			Logger.Instance.Info(
				$"Loading {source} '{path}' failed: {error?.Message ?? "unknown error"}");

		static void QuarantineDamagedMainDatabase() {
			if (!File.Exists(CurrentDatabasePath))
				return;
			string damagedPath = Path.ChangeExtension(CurrentDatabasePath, "_DAMAGED.db");
			try {
				File.Copy(CurrentDatabasePath, damagedPath, true);
				Logger.Instance.Info(
					$"Copied the damaged main scan database to '{damagedPath}' for diagnostics.");
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Could not quarantine damaged main scan database: {ex.Message}");
			}
		}

		static bool RestoreBackupToMain(string backupPath, out Exception? error) {
			error = null;
			try {
				Directory.CreateDirectory(DatabaseFolder);
				CopyFileDurably(backupPath, RestoreTempPath);
				File.Move(RestoreTempPath, CurrentDatabasePath, true);
				return true;
			}
			catch (Exception ex) {
				error = ex;
				return false;
			}
			finally {
				TryDelete(RestoreTempPath);
			}
		}

		static bool PromoteTemporaryDatabase(out Exception? error) {
			error = null;
			try {
				File.Move(TempDatabasePath, CurrentDatabasePath, true);
				Logger.Instance.Info(
					"Recovered the main scan database from a complete temporary database.");
				return true;
			}
			catch (Exception ex) {
				error = ex;
				return false;
			}
		}

		static bool DeleteInvalidTemporaryDatabase() {
			try {
				File.Delete(TempDatabasePath);
				return !File.Exists(TempDatabasePath);
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Could not remove invalid temporary scan database " +
					$"'{TempDatabasePath}': {ex.Message}");
				return false;
			}
		}

		internal static bool CreateBackup() {
			lock (DatabaseIoLock) {
				if (!File.Exists(CurrentDatabasePath))
					return true;

				try {
					Directory.CreateDirectory(DatabaseFolder);
					TryDelete(BackupPath(BackupSlotCount));
					for (int slot = BackupSlotCount - 1; slot >= 1; slot--) {
						string source = BackupPath(slot);
						if (File.Exists(source))
							File.Move(source, BackupPath(slot + 1), true);
					}

					CopyFileDurably(CurrentDatabasePath, BackupTempPath);
					File.Move(BackupTempPath, BackupPath(1), true);
					Logger.Instance.Info(
						$"Created scan database backup '{BackupPath(1)}'.");
					return true;
				}
				catch (Exception ex) {
					Logger.Instance.Info(
						$"Creating or rotating scan database backups failed: {ex.Message}");
					return false;
				}
				finally {
					TryDelete(BackupTempPath);
				}
			}
		}

		static void CopyFileDurably(string sourcePath, string destinationPath) {
			using var source = new FileStream(
				sourcePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			using var destination = new FileStream(
				destinationPath,
				FileMode.Create,
				FileAccess.Write,
				FileShare.None);
			source.CopyTo(destination);
			destination.Flush(flushToDisk: true);
		}

		static void TryDelete(string path) {
			try { File.Delete(path); }
			catch { }
		}

		/// <summary>
		/// One-time migration: image gray bytes/pHashes produced by the old ImageSharp
		/// pipeline are not comparable with the FFmpeg pipeline (different luma weights
		/// and resampler), so clear them and let the next scan recompute. Cheap — images
		/// re-hash at one decode per file. Videos are unaffected (always FFmpeg-hashed).
		/// </summary>
		internal const int CurrentImageHashPipeline = 1;
		static void MigrateImageHashesIfNeeded() {
			if (DbWrapper.ImageHashPipeline >= CurrentImageHashPipeline)
				return;
			int cleared = 0;
			foreach (FileEntry entry in DbWrapper.Entries) {
				if (!entry.IsImage)
					continue;
				if (entry.grayBytes.Count > 0 || entry.PHashes.Count > 0 || entry.Flags.Has(EntryFlags.TooDark)) {
					entry.grayBytes.Clear();
					entry.PHashes.Clear();
					entry.Flags.Set(EntryFlags.TooDark, false);
					cleared++;
				}
			}
			DbWrapper.ImageHashPipeline = CurrentImageHashPipeline;
			if (cleared > 0)
				Logger.Instance.Info($"Image hash migration: cleared cached hashes of {cleared:N0} image(s) — they will be re-hashed with the FFmpeg pipeline on the next scan.");
		}
		internal static void Create16x16Database() {
			DbWrapper.Version = 1;
			SaveDatabase();
		}
		internal static void CleanupDatabase() {
			int oldCount = Database.Count;
			var st = Stopwatch.StartNew();

			Database.RemoveWhere(a => !File.Exists(a.Path) || a.Flags.Any(EntryFlags.MetadataError | EntryFlags.ThumbnailError));

			st.Stop();
			Logger.Instance.Info(
				$"Database cleanup has finished in: {st.Elapsed}, {oldCount - Database.Count} entries have been removed");
			SaveDatabase();
		}
		internal static void SaveDatabase() {
			lock (DatabaseIoLock) {
				Logger.Instance.Info($"Save scanned files to disk ({Database.Count:N0} files).");
				Directory.CreateDirectory(DatabaseFolder);

				using (FileStream stream = new(TempDatabasePath, FileMode.Create)) {
					stream.Write(FormatMagic);
					MemoryPackSerializer.SerializeAsync(stream, DbWrapper).AsTask().GetAwaiter().GetResult();
					stream.Flush(flushToDisk: true);
				}
				//Reason: https://github.com/0x90d/videoduplicatefinder/issues/247
				File.Move(TempDatabasePath, CurrentDatabasePath, true);
			}
		}
		internal static void ClearDatabase() {
			Database.Clear();
			SaveDatabase();
		}
		internal static void BlacklistFileEntry(string filePath) {
			if (!Database.TryGetValue(new FileEntry(filePath), out FileEntry? actualValue))
				return;
			actualValue.Flags.Set(EntryFlags.ManuallyExcluded);
		}
		internal static void UpdateFilePath(string newPath, FileEntry dbEntry) {
			Database.Remove(dbEntry);
			dbEntry.Path = newPath;
			Database.Add(dbEntry);
		}
		// Typed JsonTypeInfo overloads only: the generic overloads carry
		// RequiresUnreferencedCode/RequiresDynamicCode and pollute Native AOT publish
		// logs even though metadata is source-generated. WriteIndented is the only
		// caller-supplied option that matters here; everything else is fixed by the
		// contexts (IncludeFields, case-insensitive names).
		internal static bool ExportDatabaseToJson(string jsonFile, JsonSerializerOptions options) {
			try {
				// File.Create, not OpenWrite: overwriting a previously larger export with
				// OpenWrite leaves trailing garbage that breaks re-import.
				using var stream = File.Create(jsonFile);
				JsonSerializer.Serialize(stream, DbWrapper, options.WriteIndented
					? CoreJsonPrettyContext.Default.DatabaseWrapper
					: CoreJsonContext.Default.DatabaseWrapper);
				stream.Close();
			}
			catch (JsonException e) {
				Logger.Instance.Info($"Failed to serialize database to json because: {e}");
				return false;
			}
			catch (Exception e) {
				Logger.Instance.Info($"Failed to export database to json because: {e}");
				return false;
			}
			return true;
		}
		/// <summary>
		/// Writes a privacy-preserving graybytes dump for bug reports: the 32x32 grayscale
		/// hashes and pHashes VDF computed for every entry, but <b>no file paths or names</b>
		/// (entries are anonymized to a running id). Lets maintainers diagnose extraction bugs
		/// (e.g. degenerate/duplicate graybytes producing false matches) from the actual stored
		/// data without the user having to hand over their library's paths. Written with
		/// Utf8JsonWriter so it stays AOT/trim safe.
		/// </summary>
		internal static bool ExportGrayBytesDiagnostic(string jsonFile) {
			try {
				using var stream = File.Create(jsonFile);
				using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
				w.WriteStartObject();
				w.WriteString("note", "Path-scrubbed VDF graybytes diagnostic. Contains NO file paths or names. " +
					"grayFrames are base64-encoded 32x32 grayscale buffers (1024 bytes each), ordered by sample position.");
				w.WriteNumber("entryCount", Database.Count);
				w.WriteStartArray("entries");
				int id = 0;
				foreach (FileEntry e in Database) {
					w.WriteStartObject();
					w.WriteNumber("id", id++);
					w.WriteBoolean("isImage", e.IsImage);
					var stream0 = e.mediaInfo?.Streams?.FirstOrDefault(s => s.Width > 0 && s.Height > 0);
					w.WriteNumber("width", stream0?.Width ?? 0);
					w.WriteNumber("height", stream0?.Height ?? 0);
					w.WriteNumber("durationSeconds", e.mediaInfo?.Duration.TotalSeconds ?? 0d);
					w.WriteBoolean("tooDark", e.IsTooDark);
					w.WriteBoolean("thumbnailError", e.HasThubmanilError);
					w.WriteStartArray("grayFrames");
					foreach (var kv in e.grayBytes.OrderBy(k => k.Key)) {
						if (kv.Value == null)
							w.WriteNullValue();
						else
							w.WriteBase64StringValue(kv.Value);
					}
					w.WriteEndArray();
					w.WriteStartArray("pHashes");
					foreach (var kv in e.PHashes.OrderBy(k => k.Key)) {
						if (kv.Value == null)
							w.WriteNullValue();
						else
							w.WriteNumberValue(kv.Value.Value);
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteEndObject();
				w.Flush();
				return true;
			}
			catch (Exception e) {
				Logger.Instance.Info($"Failed to export graybytes diagnostic: {e}");
				return false;
			}
		}

		internal static bool ImportDatabaseFromJson(string jsonFile, JsonSerializerOptions options) {
			try {
				using var stream = File.OpenRead(jsonFile);
				DbWrapper = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.DatabaseWrapper)!;
				stream.Close();
			}
			catch (JsonException e) {
				Logger.Instance.Info($"Failed to deserialize database from json because: {e}");
				return false;
			}
			catch (Exception e) {
				Logger.Instance.Info($"Failed to import database from json because: {e}");
				return false;
			}
			return true;
		}
	}
}

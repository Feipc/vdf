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

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MemoryPack;

namespace VDF.Core.Utils {
	static class DatabaseUtils {
		static DatabaseUtils() => MemoryPackRegistration.Register();
		const int BackupSlotCount = 5;
		static readonly object DatabaseIoLock = new();

		// Database on-disk formats, newest first:
		//   "VDFDB002" – streaming MemoryPack: header ints followed by one
		//               length-prefixed FileEntry payload at a time (see
		//               SerializeDatabaseStreaming). Bounds peak memory so saving a
		//               multi-million-entry library no longer OOM-kills the process
		//               mid-save (#814).
		//   "VDFDB001" – whole-graph MemoryPack payload (still read for existing DBs).
		//   no magic  – protobuf-net database from 3.x / early 4.x, decoded by
		//               LegacyDatabaseReader.
		// Any older format is migrated to the newest on the next save.
		static ReadOnlySpan<byte> FormatMagic => "VDFDB001"u8;
		static ReadOnlySpan<byte> FormatMagicStreaming => "VDFDB002"u8;

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

		/// <summary>Resolved folder the database (and its sidecar files) live in.</summary>
		internal static string GetDatabaseFolderPath() => DatabaseFolder;

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
						Logger.Instance.Error($"Promoting the recovered temporary scan database failed: {promoteError?.Message ?? "unknown error"}");
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
					Logger.Instance.Error($"Restoring scan database from backup slot {slot} failed: {restoreError?.Message ?? "unknown error"}");
					return false;
				}
				AcceptLoadedDatabase(backup!, BackupPath(slot), stopwatch.Elapsed);
				Logger.Instance.Info($"Automatically recovered scan database from backup slot {slot}.");
				return true;
			}
			if (!candidateFound) {
				DbWrapper = new DatabaseWrapper();
				MigrateImageHashesIfNeeded();
				Logger.Instance.Info($"No scan database was found in '{DatabaseFolder}'; starting with an empty database.");
				return true;
			}
			Logger.Instance.Error("Loading the scan database failed: no valid temporary, main, or backup database was available.");
			return false;
		}

		static bool TryReadDatabase(string path, out DatabaseWrapper? wrapper, out Exception? error) {
			wrapper = null;
			error = null;
			try {
				using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				if (file.Length == 0)
					throw new InvalidDataException("The database file is empty.");
				Span<byte> header = stackalloc byte[8];
				int headerRead = file.Read(header);
				if (headerRead == FormatMagicStreaming.Length && header.SequenceEqual(FormatMagicStreaming))
					wrapper = DeserializeDatabaseStreaming(file);
				else if (headerRead == FormatMagic.Length && header.SequenceEqual(FormatMagic))
					wrapper = MemoryPackSerializer.DeserializeAsync<DatabaseWrapper>(file)
						.AsTask().GetAwaiter().GetResult()
						?? throw new InvalidDataException("Database payload deserialized to null.");
				else {
					file.Position = 0;
					if (file.Length > int.MaxValue)
						throw new InvalidDataException("The legacy database is too large to load.");
					byte[] raw = new byte[file.Length];
					file.ReadExactly(raw);
					wrapper = LegacyDatabaseReader.Read(raw);
					Logger.Instance.Info("Legacy database format detected; it will be stored in the new format on the next save.");
				}
				if (wrapper.Entries == null)
					throw new InvalidDataException("The database entry collection is missing.");
				return true;
			}
			catch (Exception ex) { error = ex; return false; }
		}

		static void AcceptLoadedDatabase(DatabaseWrapper wrapper, string sourcePath, TimeSpan elapsed) {
			DbWrapper = wrapper;
			MigrateImageHashesIfNeeded();
			HealPoisonedFingerprintsIfNeeded();
			Logger.Instance.Info($"Scan database loaded from '{sourcePath}': {Database.Count:N0} entries in {elapsed}.");
		}

		static void LogLoadFailure(string source, string path, Exception? error) =>
			Logger.Instance.Error($"Loading {source} '{path}' failed: {error?.Message ?? "unknown error"}");

		static void QuarantineDamagedMainDatabase() {
			if (!File.Exists(CurrentDatabasePath)) return;
			try {
				string damagedPath = Path.ChangeExtension(CurrentDatabasePath, "_DAMAGED.db");
				File.Copy(CurrentDatabasePath, damagedPath, true);
				Logger.Instance.Info($"Copied the damaged main scan database to '{damagedPath}' for diagnostics.");
			}
			catch (Exception ex) { Logger.Instance.Warn($"Could not quarantine damaged main scan database: {ex.Message}"); }
		}

		static bool RestoreBackupToMain(string backupPath, out Exception? error) {
			error = null;
			try {
				Directory.CreateDirectory(DatabaseFolder);
				CopyFileDurably(backupPath, RestoreTempPath);
				File.Move(RestoreTempPath, CurrentDatabasePath, true);
				return true;
			}
			catch (Exception ex) { error = ex; return false; }
			finally { TryDelete(RestoreTempPath); }
		}

		static bool PromoteTemporaryDatabase(out Exception? error) {
			error = null;
			try {
				File.Move(TempDatabasePath, CurrentDatabasePath, true);
				Logger.Instance.Info("Recovered the main scan database from a complete temporary database.");
				return true;
			}
			catch (Exception ex) { error = ex; return false; }
		}

		static bool DeleteInvalidTemporaryDatabase() {
			try { File.Delete(TempDatabasePath); return !File.Exists(TempDatabasePath); }
			catch (Exception ex) {
				Logger.Instance.Error($"Could not remove invalid temporary scan database '{TempDatabasePath}': {ex.Message}");
				return false;
			}
		}

		internal static bool CreateBackup() {
			lock (DatabaseIoLock) {
				if (!File.Exists(CurrentDatabasePath)) return true;
				try {
					Directory.CreateDirectory(DatabaseFolder);
					TryDelete(BackupPath(BackupSlotCount));
					for (int slot = BackupSlotCount - 1; slot >= 1; slot--) {
						string source = BackupPath(slot);
						if (File.Exists(source)) File.Move(source, BackupPath(slot + 1), true);
					}
					CopyFileDurably(CurrentDatabasePath, BackupTempPath);
					File.Move(BackupTempPath, BackupPath(1), true);
					Logger.Instance.Info($"Created scan database backup '{BackupPath(1)}'.");
					return true;
				}
				catch (Exception ex) { Logger.Instance.Error($"Creating or rotating scan database backups failed: {ex.Message}"); return false; }
				finally { TryDelete(BackupTempPath); }
			}
		}

		static void CopyFileDurably(string sourcePath, string destinationPath) {
			using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
			source.CopyTo(destination);
			destination.Flush(flushToDisk: true);
		}

		static void TryDelete(string path) { try { File.Delete(path); } catch { } }

		// Stopping a scan on builds before the Stop-cancellation fix (fa902d3) could leave entries
		// "poisoned": AudioFingerprintError set plus an empty fingerprint although the file itself is
		// fine, permanently blocking every retry gate. That state is byte-identical to a genuine
		// extraction failure, so the two cannot be told apart per entry — instead every flagged entry
		// gets exactly ONE automatic retry: the flag is cleared once per database (tracked by a sidecar
		// marker), the next scan re-extracts, and genuinely broken files simply fail and re-flag once.
		static string FingerprintHealMarkerPath => FileUtils.SafePathCombine(DatabaseFolder, "ScannedFiles.fpheal1");
		static bool pendingFingerprintHealMarker;

		static void HealPoisonedFingerprintsIfNeeded() {
			if (File.Exists(FingerprintHealMarkerPath))
				return;
			int healed = 0;
			foreach (FileEntry entry in DbWrapper.Entries) {
				if (!entry.Flags.Has(EntryFlags.AudioFingerprintError))
					continue;
				entry.Flags.Set(EntryFlags.AudioFingerprintError, false);
				// The poisoned signature is an empty-but-not-null fingerprint; reset it to
				// "not yet extracted" unless another flag legitimately explains the emptiness.
				if (entry.AudioFingerprint is { Length: 0 } &&
					!entry.Flags.Any(EntryFlags.NoAudioTrack | EntryFlags.SilentAudioTrack))
					entry.AudioFingerprint = null;
				healed++;
			}
			// The marker is written by SaveDatabase once the cleared flags are persisted; writing it
			// here would strand the on-disk database unhealed if the app exits without saving.
			pendingFingerprintHealMarker = true;
			if (healed > 0)
				Logger.Instance.Info($"Audio fingerprint repair: cleared the error flag on {healed:N0} entries (possibly poisoned by stopping a scan in an older version) — they will be retried on the next scan.");
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
		internal static void CleanupDatabase(bool preserveDeletedContentMemory = false) {
			int oldCount = Database.Count;
			var st = Stopwatch.StartNew();

			if (!preserveDeletedContentMemory) {
				Database.RemoveWhere(a => !File.Exists(a.Path) || a.Flags.Any(EntryFlags.MetadataError | EntryFlags.ThumbnailError));
			}
			else {
				// Remember-deleted-content mode: a missing file on a mounted drive whose entry
				// still carries comparable data is a tombstone — the memory that recognizes
				// re-downloads — and a missing file on an unmounted drive is merely offline.
				// Neither is removed. What still goes: error-flagged entries (excluded from
				// comparison anyway, so they hold no re-download memory) and entries gone from
				// a mounted drive with no comparable data at all (can never match, never heal).
				var driveReady = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
				Database.RemoveWhere(a => {
					if (a.Flags.Any(EntryFlags.MetadataError | EntryFlags.ThumbnailError))
						return true;
					if (File.Exists(a.Path))
						return false;
					bool hasComparableData = a.AudioFingerprint != null;
					if (!hasComparableData && a.grayBytes != null)
						foreach (var v in a.grayBytes.Values)
							if (v != null) { hasComparableData = true; break; }
					if (hasComparableData)
						return false;                                   // tombstone (or offline) — keep
					string root = Path.GetPathRoot(a.Path) ?? string.Empty;
					if (!driveReady.TryGetValue(root, out bool ready))
						driveReady[root] = ready = ScanEngine.IsDriveReady(a.Path);
					return ready;                                       // ghost on a mounted drive — remove
				});
			}

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
					SerializeDatabaseStreaming(stream, DbWrapper);
					stream.Flush(flushToDisk: true);
				}
				//Reason: https://github.com/0x90d/videoduplicatefinder/issues/247
				File.Move(TempDatabasePath, CurrentDatabasePath, true);
				if (pendingFingerprintHealMarker) {
					try {
						File.WriteAllText(FingerprintHealMarkerPath, "Fingerprint error flags were reset once after the Stop-poisoning fix. Delete this file to run the reset again on next load.");
						pendingFingerprintHealMarker = false;
					}
					catch (Exception) { }
				}
			}
		}

		/// <summary>
		/// Writes the database one <see cref="FileEntry"/> at a time so peak memory stays
		/// at a single serialized entry regardless of how many entries there are. The old
		/// whole-graph <c>SerializeAsync(stream, wrapper)</c> buffered the entire payload
		/// (multiple GB for libraries past ~1M files) in memory on top of the live object
		/// graph, which got the process OOM-killed mid-save (#814).
		///
		/// Layout: magic | Version (int32 LE) | ImageHashPipeline (int32 LE) |
		///         repeated [ entryLength (int32 LE >= 0) | MemoryPack(FileEntry) ] |
		///         terminator (int32 LE == -1).
		/// </summary>
		static void SerializeDatabaseStreaming(Stream stream, DatabaseWrapper wrapper) {
			stream.Write(FormatMagicStreaming);
			Span<byte> intBuf = stackalloc byte[sizeof(int)];
			BinaryPrimitives.WriteInt32LittleEndian(intBuf, wrapper.Version);
			stream.Write(intBuf);
			BinaryPrimitives.WriteInt32LittleEndian(intBuf, wrapper.ImageHashPipeline);
			stream.Write(intBuf);

			// Snapshot the set: a checkpoint save runs while scan threads are still
			// populating per-entry hash dictionaries. The set itself is not structurally
			// modified during a scan, so ToArray is safe.
			var buffer = new ArrayBufferWriter<byte>(1 << 16);
			int skipped = 0;
			foreach (FileEntry entry in wrapper.Entries.ToArray()) {
				buffer.ResetWrittenCount();
				try {
					MemoryPackSerializer.Serialize(buffer, entry);
				}
				catch (Exception) {
					// A periodic checkpoint can catch an entry whose hash dictionary is
					// being mutated by a worker thread ("Collection was modified"). Skip it
					// for this snapshot — it stays in memory and lands in the next save. The
					// final end-of-scan save runs with no concurrency, so it never skips.
					skipped++;
					continue;
				}
				BinaryPrimitives.WriteInt32LittleEndian(intBuf, buffer.WrittenCount);
				stream.Write(intBuf);
				stream.Write(buffer.WrittenSpan);
			}
			BinaryPrimitives.WriteInt32LittleEndian(intBuf, -1);
			stream.Write(intBuf);

			if (skipped > 0)
				Logger.Instance.Info($"Database checkpoint skipped {skipped:N0} entries still being hashed; they will be saved on the next checkpoint.");
		}

		/// <summary>
		/// Reads the streaming format written by <see cref="SerializeDatabaseStreaming"/>.
		/// A truncated file (e.g. a crash mid-save) runs out of bytes before the terminator
		/// and throws, which the caller treats as a load failure — so a half-written temp
		/// file never masquerades as an empty database.
		/// </summary>
		static DatabaseWrapper DeserializeDatabaseStreaming(Stream stream) {
			var wrapper = new DatabaseWrapper { Entries = new() };
			Span<byte> intBuf = stackalloc byte[sizeof(int)];
			stream.ReadExactly(intBuf);
			wrapper.Version = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
			stream.ReadExactly(intBuf);
			wrapper.ImageHashPipeline = BinaryPrimitives.ReadInt32LittleEndian(intBuf);

			byte[] buffer = new byte[1 << 16];
			while (true) {
				stream.ReadExactly(intBuf);
				int length = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
				if (length == -1)
					break;
				if (length < 0)
					throw new InvalidDataException($"Corrupt database: negative entry length {length}.");
				if (length > buffer.Length)
					buffer = new byte[length];
				stream.ReadExactly(buffer.AsSpan(0, length));
				FileEntry entry = MemoryPackSerializer.Deserialize<FileEntry>(buffer.AsSpan(0, length))
					?? throw new InvalidDataException("Corrupt database: an entry deserialized to null.");
				wrapper.Entries.Add(entry);
			}
			return wrapper;
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
				Logger.Instance.Error($"Failed to serialize database to json because: {e}");
				return false;
			}
			catch (Exception e) {
				Logger.Instance.Error($"Failed to export database to json because: {e}");
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
				Logger.Instance.Error($"Failed to export graybytes diagnostic: {e}");
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
				Logger.Instance.Error($"Failed to deserialize database from json because: {e}");
				return false;
			}
			catch (Exception e) {
				Logger.Instance.Error($"Failed to import database from json because: {e}");
				return false;
			}
			return true;
		}
	}
}

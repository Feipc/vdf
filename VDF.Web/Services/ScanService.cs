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

using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services {
	public enum ScanState { Idle, Scanning, Comparing, Done, Aborted, Error }

	/// <summary>Outcome of a batch file operation (delete / move / link).</summary>
	public sealed class FileOpResult {
		public int Done;
		public int Failed;
		public long FreedBytes;
		public List<string> Errors { get; } = new();
	}

	public class ScanProgressArgs {
		public string CurrentFile { get; init; } = string.Empty;
		public int Current { get; init; }
		public int Max { get; init; }
		public TimeSpan Elapsed { get; init; }
		public TimeSpan Remaining { get; init; }
		public string CurrentStage { get; init; } = string.Empty;
		public int StageCurrent { get; init; }
		public int StageMax { get; init; }
	}

	public sealed class ComparisonProgressArgs {
		public ComparisonStage Stage { get; init; }
		public long Current { get; init; }
		public long Total { get; init; }
		public double ItemsPerSecond { get; init; }
		public TimeSpan Elapsed { get; init; }
		public TimeSpan Remaining { get; init; }
		public int ActiveWorkers { get; init; }
		public int EffectiveParallelism { get; init; }
	}

	/// <summary>
	/// Singleton service that owns the ScanEngine instance and exposes
	/// scan lifecycle operations to Blazor components via events and state.
	/// </summary>
	public sealed class ScanService : IDisposable {
		readonly ScanEngine _engine = new();
		readonly WebSettingsService _settingsService;
		readonly ResultThumbnailService _resultThumbnailService;
		readonly ResultSnapshotStore _resultSnapshotStore;
		bool _ownsResultServices;
		readonly SemaphoreSlim _databaseInitializationGate = new(1, 1);
		readonly object _snapshotLock = new();
		readonly object _reviewStateLock = new();
		readonly HashSet<string> _selectedResultPaths =
			new(PathComparer.ForCurrentPlatform);
		readonly HashSet<string> _excludedResultDirectories =
			new(PathComparer.ForCurrentPlatform);
		readonly HashSet<string> _protectedResultDirectories =
			new(PathComparer.ForCurrentPlatform);
		CancellationTokenSource _cts = new();
		CancellationTokenSource? _snapshotSaveCts;
		Task _pendingSnapshotSave = Task.CompletedTask;
		List<DuplicateItem>? _preScanResults;
		List<string>? _preScanSelectedPaths;
		bool _resultsArePersistable;
		bool _databaseLoadedSuccessfully;
		bool _savedResultsRestored;
		string? _loadedDatabaseFolder;
		int _scanStartPending;
		long _resultsRevision;

		public ScanState State { get; private set; } = ScanState.Idle;
		public string? ErrorMessage { get; private set; }
		public ScanProgressArgs? LastProgress { get; private set; }
		public ComparisonProgressArgs? LastComparisonProgress { get; private set; }
		/// <summary>Total files hashed (captured when BuildingHashesDone fires).</summary>
		public int FilesHashed { get; private set; }
		public IReadOnlyCollection<DuplicateItem> Duplicates => _engine.Duplicates;
		public Settings Settings => _engine.Settings;
		/// <summary>Test seam: lets tests seed results without driving a real scan.</summary>
		internal ScanEngine Engine => _engine;
		public bool DatabaseLoadedSuccessfully => _databaseLoadedSuccessfully;
		public long ResultsRevision => Volatile.Read(ref _resultsRevision);
		public IReadOnlyCollection<string> SelectedResultPaths {
			get {
				lock (_reviewStateLock)
					return _selectedResultPaths.ToArray();
			}
		}
		public IReadOnlyCollection<string> ExcludedResultDirectories {
			get {
				lock (_reviewStateLock)
					return _excludedResultDirectories.ToArray();
			}
		}

		public IReadOnlyCollection<string> ProtectedResultDirectories {
			get {
				lock (_reviewStateLock)
					return _protectedResultDirectories.ToArray();
			}
		}

		public bool IsResultProtected(string path) {
			lock (_reviewStateLock)
				return ResultSelectionRules.IsProtected(path, _protectedResultDirectories);
		}

		public void UpdateProtectedResultDirectories(IEnumerable<string> directories) {
			var normalized = directories.Select(ResultSelectionRules.NormalizeDirectory).ToArray();
			lock (_reviewStateLock) {
				if (FileOpRunning)
					throw new InvalidOperationException("Wait for the current file operation to finish.");
				_protectedResultDirectories.Clear();
				_protectedResultDirectories.UnionWith(normalized);
				PruneSelectedResultPathsLocked();
			}
			MarkResultsChanged();
			ScheduleResultsSnapshotSave();
			Notify();
		}

		/// <summary>Caches for the thumbnail endpoints — cleared whenever the results change wholesale.</summary>
		public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> FullThumbCache { get; } = new();

		void ClearThumbnailCaches() {
			_resultThumbnailService.Clear();
			FullThumbCache.Clear();
		}

		void MarkResultsChanged(bool clearThumbnailCaches = false) {
			Interlocked.Increment(ref _resultsRevision);
			if (clearThumbnailCaches)
				ClearThumbnailCaches();
		}

		public event Action? StateChanged;

		/// <summary>Compatibility constructor used by lightweight callers and UI tests.</summary>
		public ScanService(WebSettingsService settingsService)
			: this(settingsService, new ResultThumbnailService(), new ResultSnapshotStore()) {
			_ownsResultServices = true;
		}

		public ScanService(
			WebSettingsService settingsService,
			ResultThumbnailService resultThumbnailService,
			ResultSnapshotStore resultSnapshotStore) {
			_settingsService = settingsService;
			_resultThumbnailService = resultThumbnailService;
			_resultSnapshotStore = resultSnapshotStore;
			settingsService.Load(_engine.Settings);

			_engine.FilesEnumerated += (_, _) => Notify();
			_engine.BuildingHashesDone += (_, _) => {
				FilesHashed = LastProgress?.Max ?? 0;
				LastProgress = null;
				State = ScanState.Comparing;
				Notify();
			};
			_engine.Progress += (_, e) => {
				LastProgress = new ScanProgressArgs {
					CurrentFile = e.CurrentFile,
					Current = e.CurrentPosition,
					Max = e.MaxPosition,
					Elapsed = e.Elapsed,
					Remaining = e.Remaining,
					CurrentStage = e.CurrentStage ?? string.Empty,
					StageCurrent = e.StageCurrent,
					StageMax = e.StageMax,
				};
				Notify();
			};
			_engine.ComparisonProgress += (_, e) => {
				LastComparisonProgress = new ComparisonProgressArgs {
					Stage = e.Stage,
					Current = e.Current,
					Total = e.Total,
					ItemsPerSecond = e.ItemsPerSecond,
					Elapsed = e.Elapsed,
					Remaining = e.Remaining,
					ActiveWorkers = e.ActiveWorkers,
					EffectiveParallelism = e.EffectiveParallelism,
				};
				Notify();
			};
			_engine.ScanDone += (_, _) => {
				// Skip low-res thumbnail retrieval — WebUI loads HQ thumbnails on demand
				// via the /thumbnail/hq endpoint. This makes results available immediately.
				State = ScanState.Done;
				LastProgress = null;
				LastComparisonProgress = null;
				_preScanResults = null;
				_preScanSelectedPaths = null;
				lock (_reviewStateLock)
					_selectedResultPaths.Clear();
				_resultsArePersistable = true;
				MarkResultsChanged();
				ScheduleResultsSnapshotSave(immediate: true);
				Notify();
			};
			_engine.ScanAborted += (_, _) => {
				State = ScanState.Aborted;
				LastProgress = null;
				LastComparisonProgress = null;
				RestorePreScanResults();
				MarkResultsChanged();
				Notify();
			};
		}

		public async Task<bool> InitializeAsync(
			CancellationToken cancellationToken = default) {
			await _databaseInitializationGate.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
			try {
				if (!await EnsureDatabaseLoadedLockedAsync(cancellationToken)
					.ConfigureAwait(false))
					return false;

				if (!_savedResultsRestored) {
					await RestoreSavedResultsAsync(cancellationToken)
						.ConfigureAwait(false);
					_savedResultsRestored = true;
				}
				return true;
			}
			finally {
				_databaseInitializationGate.Release();
			}
		}

		async Task<bool> EnsureDatabaseLoadedAsync(
			CancellationToken cancellationToken = default) {
			await _databaseInitializationGate.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
			try {
				return await EnsureDatabaseLoadedLockedAsync(cancellationToken)
					.ConfigureAwait(false);
			}
			finally {
				_databaseInitializationGate.Release();
			}
		}

		async Task<bool> EnsureDatabaseLoadedLockedAsync(
			CancellationToken cancellationToken) {
			cancellationToken.ThrowIfCancellationRequested();
			string configuredFolder;
			try {
				if (!string.IsNullOrWhiteSpace(Settings.CustomDatabaseFolder)) {
					configuredFolder = Path.GetFullPath(Settings.CustomDatabaseFolder);
					if (!Directory.Exists(configuredFolder)) {
						_databaseLoadedSuccessfully = false;
						_loadedDatabaseFolder = configuredFolder;
						Logger.Instance.Info(
							$"Configured scan database folder '{configuredFolder}' " +
							"does not exist or is not mounted.");
						return false;
					}
				}
				else {
					configuredFolder = Path.GetFullPath(
						CoreUtils.ResolveDatabaseFolder(null));
				}
			}
			catch (Exception ex) {
				_databaseLoadedSuccessfully = false;
				_loadedDatabaseFolder = Settings.CustomDatabaseFolder;
				Logger.Instance.Info(
					$"Configured scan database folder is invalid: {ex.Message}");
				return false;
			}
			if (_databaseLoadedSuccessfully &&
				PathComparer.ForCurrentPlatform.Equals(
					_loadedDatabaseFolder,
					configuredFolder))
				return true;

			ScanEngine.ConfigureDatabaseFolder(Settings.CustomDatabaseFolder);
			bool loaded;
			try {
				loaded = await ScanEngine.LoadDatabase().ConfigureAwait(false);
			}
			catch (Exception ex) {
				_databaseLoadedSuccessfully = false;
				_loadedDatabaseFolder = configuredFolder;
				Logger.Instance.Info(
					$"Loading scan database from '{configuredFolder}' failed: {ex.Message}");
				return false;
			}

			_databaseLoadedSuccessfully = loaded;
			_loadedDatabaseFolder = configuredFolder;
			if (!loaded) {
				Logger.Instance.Info(
					$"Refusing database-dependent work because the scan database " +
					$"in '{configuredFolder}' could not be loaded.");
				return false;
			}

			Logger.Instance.Info(
				$"Configured scan database folder '{ScanEngine.ConfiguredDatabaseFolder}'; " +
				$"{DatabaseEntryCount:N0} entries loaded.");
			return true;
		}

		async Task<string?> PrepareDatabaseMutationAsync(string operation) {
			if (!await EnsureDatabaseLoadedAsync().ConfigureAwait(false)) {
				string error =
					$"Cannot {operation}: the scan database could not be loaded.";
				Logger.Instance.Info($"Refusing to {operation}: scan database load failed.");
				return error;
			}
			if (!await ScanEngine.CreateDatabaseBackup().ConfigureAwait(false)) {
				string error =
					$"Cannot {operation}: the required scan database backup failed.";
				Logger.Instance.Info($"Refusing to {operation}: required database backup failed.");
				return error;
			}
			return null;
		}

		async Task RestoreSavedResultsAsync(
			CancellationToken cancellationToken = default) {
			if (State == ScanState.Scanning || State == ScanState.Comparing ||
				_engine.Duplicates.Count > 0)
				return;

			WebResultSnapshot? snapshot =
				await _resultSnapshotStore.LoadAsync(cancellationToken);
			if (snapshot == null)
				return;

			_engine.Duplicates.Clear();
			foreach (DuplicateItem item in snapshot.Items)
				_engine.Duplicates.Add(item);
			DropSingletonGroups();
			lock (_reviewStateLock) {
				_protectedResultDirectories.Clear();
				foreach (string directory in snapshot.ProtectedDirectories)
					if (TryNormalizeDirectory(directory, out string normalizedProtected))
						_protectedResultDirectories.Add(normalizedProtected);
				_excludedResultDirectories.Clear();
				foreach (string directory in snapshot.ExcludedDirectories)
					if (TryNormalizeDirectory(directory, out string normalized))
						_excludedResultDirectories.Add(normalized);
				_selectedResultPaths.Clear();
				foreach (string path in snapshot.SelectedPaths)
					if (!IsPathExcludedLocked(path))
						_selectedResultPaths.Add(path);
				PruneSelectedResultPathsLocked();
			}

			_resultsArePersistable = true;
			if (_engine.Duplicates.Count > 0)
				State = ScanState.Done;
			MarkResultsChanged(clearThumbnailCaches: true);
			Logger.Instance.Info(
				$"Restored {_engine.Duplicates.Count:N0} Web result item(s), " +
				$"{_selectedResultPaths.Count:N0} selection(s), and " +
				$"{_excludedResultDirectories.Count:N0} excluded directorie(s) from the saved snapshot.");
			Notify();
		}

		public async Task StartScanAndCompare() {
			if (Interlocked.Exchange(ref _scanStartPending, 1) != 0)
				return;
			try {
				if (State == ScanState.Scanning || State == ScanState.Comparing)
					return;
				string? databaseError =
					await PrepareDatabaseMutationAsync("start the full scan")
						.ConfigureAwait(false);
				if (databaseError != null) {
					SetError(new InvalidOperationException(databaseError));
					return;
				}
				if (_resultsArePersistable) {
					await FlushResultsSnapshotAsync().ConfigureAwait(false);
					_preScanResults = _engine.Duplicates.ToList();
					lock (_reviewStateLock)
						_preScanSelectedPaths = _selectedResultPaths.ToList();
				}
				else {
					_preScanResults = null;
					_preScanSelectedPaths = null;
				}
				lock (_reviewStateLock)
					_selectedResultPaths.Clear();
				_resultsArePersistable = false;
				_cts = new CancellationTokenSource();
				State = ScanState.Scanning;
				ErrorMessage = null;
				LastProgress = null;
				LastComparisonProgress = null;
				FilesHashed = 0;
				_engine.Duplicates.Clear();
				MarkResultsChanged(clearThumbnailCaches: true);
				try {
					_engine.StartSearch();
				}
				catch (Exception ex) {
					SetError(ex);
					return;
				}
				Notify();
			}
			finally {
				Volatile.Write(ref _scanStartPending, 0);
			}
		}

		/// <summary>Called from global exception handlers to surface post-await async void exceptions.</summary>
		public void SetError(Exception ex) {
			bool restorePreviousResults =
				State == ScanState.Scanning || State == ScanState.Comparing;
			State = ScanState.Error;
			ErrorMessage = ex.Message;
			LastProgress = null;
			LastComparisonProgress = null;
			if (restorePreviousResults) {
				RestorePreScanResults();
				MarkResultsChanged();
			}
			Notify();
		}

		public void Pause() => _engine.Pause();
		public void Resume() => _engine.Resume();

		public void Stop() {
			_engine.Stop();
			_cts.Cancel();
		}

		public void DismissError() {
			if (State != ScanState.Error)
				return;
			ErrorMessage = null;
			State = _engine.Duplicates.Count > 0 ? ScanState.Done : ScanState.Idle;
			Notify();
		}

		public bool SaveSettings() => _settingsService.Save(_engine.Settings);

		void ClearSavedResults() {
			CancelScheduledSnapshotSave();
			if (ProtectedResultDirectories.Count == 0) {
				_resultSnapshotStore.Delete();
				return;
			}
			// Keep configured protection even when the user clears the result list.
			_resultsArePersistable = true;
			ScheduleResultsSnapshotSave(immediate: true);
		}

		public void Reset() {
			if (State == ScanState.Scanning || State == ScanState.Comparing) return;
			State = ScanState.Idle;
			ErrorMessage = null;
			LastProgress = null;
			LastComparisonProgress = null;
			FilesHashed = 0;
			_engine.Duplicates.Clear();
			_resultsArePersistable = false;
			_preScanResults = null;
			_preScanSelectedPaths = null;
			lock (_reviewStateLock) {
				_selectedResultPaths.Clear();
				_excludedResultDirectories.Clear();
			}
			ClearSavedResults();
			MarkResultsChanged(clearThumbnailCaches: true);
			// Keep IncludeList/BlackList — resetting scan results should not
			// throw away the paths the user configured.
			Notify();
		}

		/// <summary>Removes items from the results list without touching the files on disk.</summary>
		public void RemoveFromResults(IEnumerable<DuplicateItem> items) {
			foreach (var item in items.ToList())
				_engine.Duplicates.Remove(item);
			DropSingletonGroups();
			lock (_reviewStateLock)
				PruneSelectedResultPathsLocked();
			MarkResultsChanged();
			ScheduleResultsSnapshotSave();
			Notify();
		}

		/// <summary>Drops groups that have shrunk to a single item — a group of one is not a duplicate.</summary>
		void DropSingletonGroups() {
			var keep = _engine.Duplicates
				.GroupBy(d => d.GroupId)
				.Where(g => g.Count() > 1)
				.Select(g => g.Key)
				.ToHashSet();
			foreach (var d in _engine.Duplicates.ToList())
				if (!keep.Contains(d.GroupId))
					_engine.Duplicates.Remove(d);
		}

		void RestorePreScanResults() {
			if (_preScanResults == null) {
				_preScanSelectedPaths = null;
				_resultsArePersistable = false;
				return;
			}

			_engine.Duplicates.Clear();
			foreach (DuplicateItem item in _preScanResults)
				_engine.Duplicates.Add(item);
			_preScanResults = null;
			lock (_reviewStateLock) {
				_selectedResultPaths.Clear();
				if (_preScanSelectedPaths != null)
					foreach (string path in _preScanSelectedPaths)
						if (!IsPathExcludedLocked(path))
							_selectedResultPaths.Add(path);
				PruneSelectedResultPathsLocked();
			}
			_preScanSelectedPaths = null;
			_resultsArePersistable = true;
		}

		public void UpdateResultReviewState(
			IEnumerable<string> selectedPaths,
			IEnumerable<string> excludedDirectories) {
			ArgumentNullException.ThrowIfNull(selectedPaths);
			ArgumentNullException.ThrowIfNull(excludedDirectories);
			lock (_reviewStateLock) {
				_excludedResultDirectories.Clear();
				foreach (string directory in excludedDirectories)
					if (TryNormalizeDirectory(directory, out string normalized))
						_excludedResultDirectories.Add(normalized);

				_selectedResultPaths.Clear();
				foreach (string path in selectedPaths)
					if (!string.IsNullOrWhiteSpace(path) &&
						!IsPathExcludedLocked(path))
						_selectedResultPaths.Add(path);
				PruneSelectedResultPathsLocked();
			}
			ScheduleResultsSnapshotSave();
		}

		void PruneSelectedResultPaths() {
			lock (_reviewStateLock)
				PruneSelectedResultPathsLocked();
		}

		void PruneSelectedResultPathsLocked() {
			var resultPaths = new HashSet<string>(
				_engine.Duplicates.Select(item => item.Path),
				PathComparer.ForCurrentPlatform);
			_selectedResultPaths.RemoveWhere(path => !resultPaths.Contains(path) ||
				ResultSelectionRules.IsProtected(path, _protectedResultDirectories));
		}

		bool IsPathExcludedLocked(string path) =>
			_excludedResultDirectories.Any(directory =>
				ResultPresentationUtils.IsInDirectory(path, directory));

		static bool TryNormalizeDirectory(string directory, out string normalized) {
			try {
				normalized = Path.TrimEndingDirectorySeparator(
					Path.GetFullPath(directory));
				return !string.IsNullOrWhiteSpace(normalized);
			}
			catch {
				normalized = string.Empty;
				return false;
			}
		}

		(string[] SelectedPaths, string[] ExcludedDirectories, string[] ProtectedDirectories)
			GetReviewStateSnapshot() {
			lock (_reviewStateLock)
				return (
					_selectedResultPaths.ToArray(),
					_excludedResultDirectories.ToArray(),
					_protectedResultDirectories.ToArray());
		}

		void ScheduleResultsSnapshotSave(bool immediate = false) {
			if (!_resultsArePersistable)
				return;

			List<DuplicateItem> snapshot = _engine.Duplicates.ToList();
			var reviewState = GetReviewStateSnapshot();
			var next = new CancellationTokenSource();
			CancellationTokenSource? previous;
			lock (_snapshotLock) {
				previous = _snapshotSaveCts;
				_snapshotSaveCts = next;
				_pendingSnapshotSave = SaveSnapshotAfterDelayAsync(
					snapshot,
					reviewState.SelectedPaths,
					reviewState.ExcludedDirectories,
					reviewState.ProtectedDirectories,
					immediate ? TimeSpan.Zero : TimeSpan.FromSeconds(1),
					next);
			}
			TryCancel(previous);
		}

		async Task SaveSnapshotAfterDelayAsync(
			IReadOnlyCollection<DuplicateItem> snapshot,
			IReadOnlyCollection<string> selectedPaths,
			IReadOnlyCollection<string> excludedDirectories,
			IReadOnlyCollection<string> protectedDirectories,
			TimeSpan delay,
			CancellationTokenSource owner) {
			try {
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, owner.Token).ConfigureAwait(false);
				await _resultSnapshotStore.SaveAsync(
					snapshot,
					selectedPaths,
					excludedDirectories,
					owner.Token,
					protectedDirectories)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Could not save Web results snapshot: {ex.Message}");
			}
			finally {
				lock (_snapshotLock) {
					if (ReferenceEquals(_snapshotSaveCts, owner)) {
						_snapshotSaveCts = null;
						_pendingSnapshotSave = Task.CompletedTask;
					}
				}
				owner.Dispose();
			}
		}

		void CancelScheduledSnapshotSave() {
			CancellationTokenSource? cancellation;
			lock (_snapshotLock) {
				cancellation = _snapshotSaveCts;
				_snapshotSaveCts = null;
				_pendingSnapshotSave = Task.CompletedTask;
			}
			TryCancel(cancellation);
		}

		public async Task FlushResultsSnapshotAsync() {
			if (!_resultsArePersistable) {
				CancelScheduledSnapshotSave();
				return;
			}

			List<DuplicateItem> snapshot = _engine.Duplicates.ToList();
			var reviewState = GetReviewStateSnapshot();
			Task pending;
			CancellationTokenSource? cancellation;
			lock (_snapshotLock) {
				pending = _pendingSnapshotSave;
				cancellation = _snapshotSaveCts;
				_snapshotSaveCts = null;
				_pendingSnapshotSave = Task.CompletedTask;
			}
			TryCancel(cancellation);
			try { await pending.ConfigureAwait(false); }
			catch { /* scheduled saves log their own errors */ }

			try {
				await _resultSnapshotStore.SaveAsync(
					snapshot,
					reviewState.SelectedPaths,
					reviewState.ExcludedDirectories,
					protectedDirectories: reviewState.ProtectedDirectories).ConfigureAwait(false);
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Could not flush Web results snapshot: {ex.Message}");
			}
		}

		static void TryCancel(CancellationTokenSource? source) {
			try { source?.Cancel(); }
			catch (ObjectDisposedException) { }
		}

		// === Batch file operations (delete / move / link) ===

		/// <summary>True while a delete/move/link batch is running.</summary>
		public bool FileOpRunning { get; private set; }
		public string FileOpVerb { get; private set; } = string.Empty;
		public int FileOpCurrent { get; private set; }
		public int FileOpMax { get; private set; }

		bool TryBeginFileOp(string verb, int max) {
			lock (_reviewStateLock) {
				if (FileOpRunning || max == 0) return false;
				FileOpRunning = true;
				FileOpVerb = verb;
				FileOpCurrent = 0;
				FileOpMax = max;
			}
			Notify();
			return true;
		}

		void EndFileOp() {
			lock (_reviewStateLock) {
				FileOpRunning = false;
				FileOpVerb = string.Empty;
			}
			Notify();
		}

		List<DuplicateItem> RejectProtectedItems(IEnumerable<DuplicateItem> items, FileOpResult result) {
			var allowed = new List<DuplicateItem>();
			foreach (var item in items) {
				if (IsResultProtected(item.Path)) {
					result.Failed++;
					result.Errors.Add($"Protected file was skipped: {item.Path}");
				}
				else allowed.Add(item);
			}
			return allowed;
		}

		/// <summary>Deletes files from disk and removes them from results and the scan database.</summary>
		public async Task<FileOpResult> DeleteItemsAsync(IEnumerable<DuplicateItem> items, bool permanent) {
			var result = new FileOpResult();
			var list = RejectProtectedItems(items, result);
			if (list.Count == 0 || FileOpRunning)
				return result;
			string? databaseError = await PrepareDatabaseMutationAsync(
				permanent ? "delete files permanently" : "move files to trash")
				.ConfigureAwait(false);
			if (databaseError != null) {
				result.Errors.Add(databaseError);
				result.Failed += list.Count;
				return result;
			}
			if (!TryBeginFileOp(permanent ? "Deleting" : "Moving to trash", list.Count))
				return result;
			try {
				// Protection may have changed while database initialization was awaited.
				list = RejectProtectedItems(list, result);
				FileOpMax = list.Count;
				await Task.Run(() => {
					// Windows: recycle the whole batch in one shell call — one
					// SHFileOperation per file pays the full shell round-trip each time.
					var batchRecycled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					if (!permanent && OperatingSystem.IsWindows()) {
						var existing = list.Where(d => File.Exists(d.Path)).Select(d => d.Path).ToList();
						if (existing.Count > 0) {
							var fs = new FileUtils.SHFILEOPSTRUCT {
								wFunc = FileUtils.FileOperationType.FO_DELETE,
								pFrom = string.Join('\0', existing) + "\0\0",
								fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO |
										 FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
										 FileUtils.FileOperationFlags.FOF_NOERRORUI |
										 FileUtils.FileOperationFlags.FOF_SILENT
							};
							int shResult = FileUtils.SHFileOperation(ref fs);
							if (shResult != 0)
								Logger.Instance.Warn($"SHFileOperation returned {shResult:X} for a batch of {existing.Count} file(s); checking which files were actually recycled.");
							foreach (var p in existing)
								batchRecycled.Add(p);
						}
					}

					var sw = System.Diagnostics.Stopwatch.StartNew();
					foreach (var item in list) {
						try {
							bool exists = File.Exists(item.Path);
							if (!exists) {
								if (batchRecycled.Contains(item.Path))
									result.FreedBytes += Math.Max(0, item.SizeLong);
								// Already gone — still remove the entry and database record.
							}
							else if (permanent) {
								File.Delete(item.Path);
								result.FreedBytes += Math.Max(0, item.SizeLong);
							}
							else if (OperatingSystem.IsWindows()) {
								// The batch ran but this file is still there.
								throw new IOException("the shell did not move the file to the recycle bin");
							}
							else {
								// System trash, falling back to permanent delete (e.g.
								// cross-filesystem files where trashing means a full copy).
								if (!FileUtils.MoveToTrash(item.Path))
									File.Delete(item.Path);
								result.FreedBytes += Math.Max(0, item.SizeLong);
							}
							_engine.Duplicates.Remove(item);
							// Path-only entry — FileEntry(string) stats the file and throws once it's gone.
							ScanEngine.RemoveFromDatabase(new FileEntry { Path = item.Path });
							result.Done++;
						}
						catch (Exception ex) {
							result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
							result.Failed++;
						}
						finally {
							FileOpCurrent++;
							if (sw.ElapsedMilliseconds >= 100) { sw.Restart(); Notify(); }
						}
					}
					if (result.Done > 0)
						ScanEngine.SaveDatabase();
					DropSingletonGroups();
					if (result.Done > 0)
						MarkResultsChanged();
				});
			}
			finally { EndFileOp(); }
			if (result.Done > 0) {
				PruneSelectedResultPaths();
				ScheduleResultsSnapshotSave();
			}
			return result;
		}

		/// <summary>Moves files to a destination folder and updates the scan database paths.</summary>
		public async Task<FileOpResult> MoveItemsAsync(IEnumerable<DuplicateItem> items, string destinationFolder) {
			var result = new FileOpResult();
			var list = RejectProtectedItems(items, result);
			if (list.Count == 0 || FileOpRunning)
				return result;
			string? databaseError =
				await PrepareDatabaseMutationAsync("move files")
					.ConfigureAwait(false);
			if (databaseError != null) {
				result.Errors.Add(databaseError);
				result.Failed += list.Count;
				return result;
			}
			try { Directory.CreateDirectory(destinationFolder); }
			catch (Exception ex) {
				result.Errors.Add($"Cannot create destination folder: {ex.Message}");
				return result;
			}
			if (!TryBeginFileOp("Moving", list.Count))
				return result;
			try {
				list = RejectProtectedItems(list, result);
				FileOpMax = list.Count;
				await Task.Run(() => {
					var sw = System.Diagnostics.Stopwatch.StartNew();
					foreach (var item in list) {
						try {
							string dest = Path.Combine(destinationFolder, Path.GetFileName(item.Path));
							// Avoid overwriting existing files at destination
							int n = 1;
							string ext = Path.GetExtension(dest);
							string nameNoExt = Path.GetFileNameWithoutExtension(dest);
							while (File.Exists(dest))
								dest = Path.Combine(destinationFolder, $"{nameNoExt}_{n++}{ext}");
							File.Move(item.Path, dest);
							if (ScanEngine.GetFromDatabase(item.Path, out var dbEntry) && dbEntry != null)
								ScanEngine.UpdateFilePathInDatabase(dest, dbEntry);
							_engine.Duplicates.Remove(item);
							result.Done++;
						}
						catch (Exception ex) {
							result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
							result.Failed++;
						}
						finally {
							FileOpCurrent++;
							if (sw.ElapsedMilliseconds >= 100) { sw.Restart(); Notify(); }
						}
					}
					if (result.Done > 0)
						ScanEngine.SaveDatabase();
					DropSingletonGroups();
					if (result.Done > 0)
						MarkResultsChanged();
				});
			}
			finally { EndFileOp(); }
			if (result.Done > 0) {
				PruneSelectedResultPaths();
				ScheduleResultsSnapshotSave();
			}
			return result;
		}

		/// <summary>
		/// Replaces each selected file with a hardlink or symlink to the kept file of its
		/// group (the highest-similarity unselected member that still exists on disk).
		/// </summary>
		public async Task<FileOpResult> CreateLinksAsync(IEnumerable<DuplicateItem> items, bool hardLinks) {
			var result = new FileOpResult();
			var list = RejectProtectedItems(items, result);
			if (list.Count == 0 || FileOpRunning)
				return result;
			string? databaseError = await PrepareDatabaseMutationAsync(
				hardLinks ? "replace files with hardlinks" : "replace files with symlinks")
				.ConfigureAwait(false);
			if (databaseError != null) {
				result.Errors.Add(databaseError);
				result.Failed += list.Count;
				return result;
			}
			if (!TryBeginFileOp(hardLinks ? "Creating hardlinks" : "Creating symlinks", list.Count))
				return result;
			try {
				list = RejectProtectedItems(list, result);
				FileOpMax = list.Count;
				await Task.Run(() => {
					var selected = list.ToHashSet();
					var keeperByGroup = _engine.Duplicates
						.Where(d => !selected.Contains(d))
						.GroupBy(d => d.GroupId)
						.ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Similarity).FirstOrDefault(d => File.Exists(d.Path)));

					var sw = System.Diagnostics.Stopwatch.StartNew();
					foreach (var item in list) {
						try {
							if (!File.Exists(item.Path)) {
								// Already gone — still remove the entry and database record.
							}
							else {
								keeperByGroup.TryGetValue(item.GroupId, out var keeper);
								if (keeper == null)
									throw new IOException("no unselected file is left in this group to link to");
								long size = Math.Max(0, item.SizeLong);
								// The link target path must be free before the link can be created.
								File.Delete(item.Path);
								if (hardLinks)
									HardLinkUtils.CreateHardLink(item.Path, keeper.Path);
								else
									File.CreateSymbolicLink(item.Path, keeper.Path);
								result.FreedBytes += size;
							}
							_engine.Duplicates.Remove(item);
							ScanEngine.RemoveFromDatabase(new FileEntry { Path = item.Path });
							result.Done++;
						}
						catch (Exception ex) {
							result.Errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
							result.Failed++;
						}
						finally {
							FileOpCurrent++;
							if (sw.ElapsedMilliseconds >= 100) { sw.Restart(); Notify(); }
						}
					}
					if (result.Done > 0)
						ScanEngine.SaveDatabase();
					DropSingletonGroups();
					if (result.Done > 0)
						MarkResultsChanged();
				});
			}
			finally { EndFileOp(); }
			if (result.Done > 0) {
				PruneSelectedResultPaths();
				ScheduleResultsSnapshotSave();
			}
			return result;
		}

		/// <summary>Removes database entries for files that no longer exist or have errors.</summary>
		public async Task<int> CleanDatabaseAsync() {
			if (!await EnsureDatabaseLoadedAsync().ConfigureAwait(false))
				throw new InvalidOperationException(
					"Cannot clean the scan database because it could not be loaded.");
			int before = DatabaseEntryCount;
			await Task.Run(() => _engine.CleanupDatabase());
			return before - DatabaseEntryCount;
		}

		/// <summary>Wipes all entries from the scan database.</summary>
		public async Task ClearDatabaseAsync() {
			if (!await EnsureDatabaseLoadedAsync().ConfigureAwait(false))
				throw new InvalidOperationException(
					"Cannot clear the scan database because it could not be loaded.");
			ScanEngine.ClearDatabase();
			_engine.Duplicates.Clear();
			_resultsArePersistable = false;
			_preScanResults = null;
			_preScanSelectedPaths = null;
			lock (_reviewStateLock) {
				_selectedResultPaths.Clear();
				_excludedResultDirectories.Clear();
			}
			ClearSavedResults();
			MarkResultsChanged(clearThumbnailCaches: true);
			Notify();
		}

		/// <summary>Number of file entries currently stored in the scan database.</summary>
		public int DatabaseEntryCount => VDF.Core.Utils.DatabaseUtils.Database.Count;

		/// <summary>
		/// Runs the single-pair detection diagnostic with the current settings and
		/// returns the step-by-step report. See <see cref="ScanEngine.TestFilePairAsync"/>.
		/// </summary>
		public Task<string> TestFilePairAsync(string fileA, string fileB) {
			if (State == ScanState.Scanning || State == ScanState.Comparing)
				return Task.FromResult("A scan is currently running. Wait for it to finish before running the file pair test.");
			return _engine.TestFilePairAsync(fileA, fileB);
		}

		void Notify() => StateChanged?.Invoke();

		public void Dispose() {
			CancelScheduledSnapshotSave();
			_cts.Dispose();
			_databaseInitializationGate.Dispose();
			if (_ownsResultServices) {
				_resultThumbnailService.Dispose();
				_resultSnapshotStore.Dispose();
			}
		}
	}
}

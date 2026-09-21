// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Text.Json;
using System.Text.Json.Serialization;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services {
	internal sealed class WebResultSnapshot {
		internal const int CurrentVersion = 3;

		public int Version { get; set; } = CurrentVersion;
		public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
		public List<DuplicateItem> Items { get; set; } = new();
		public List<string> SelectedPaths { get; set; } = new();
		public List<string> ExcludedDirectories { get; set; } = new();
		public List<string> ProtectedDirectories { get; set; } = new();
	}

	[JsonSourceGenerationOptions(WriteIndented = false)]
	[JsonSerializable(typeof(WebResultSnapshot))]
	internal sealed partial class ResultSnapshotJsonContext : JsonSerializerContext { }

	/// <summary>
	/// Persists the Web result list separately from the scan/hash database.
	/// Thumbnails are excluded by DuplicateItem's JSON contract.
	/// </summary>
	public sealed class ResultSnapshotStore : IDisposable {
		readonly string _path;
		readonly SemaphoreSlim _ioGate = new(1, 1);

		public ResultSnapshotStore()
			: this(Path.Combine(CoreUtils.StateFolder, "WebResults.json")) { }

		internal ResultSnapshotStore(string path) {
			_path = path;
		}

		internal async Task<WebResultSnapshot?> LoadAsync(
			CancellationToken cancellationToken = default) {
			if (!File.Exists(_path))
				return null;

			await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				await using var stream = new FileStream(
					_path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					64 * 1024,
					useAsync: true);
				WebResultSnapshot? snapshot = await JsonSerializer.DeserializeAsync(
					stream,
					ResultSnapshotJsonContext.Default.WebResultSnapshot,
					cancellationToken).ConfigureAwait(false);
				if (snapshot == null)
					throw new JsonException("The Web results snapshot is empty.");
				if (snapshot.Version < 1 ||
					snapshot.Version > WebResultSnapshot.CurrentVersion)
					throw new JsonException(
						$"Unsupported Web results snapshot version {snapshot.Version}.");
				if (snapshot.Items == null)
					throw new JsonException("The Web results snapshot has no item list.");
				snapshot.SelectedPaths ??= new();
				snapshot.ExcludedDirectories ??= new();
				snapshot.ProtectedDirectories ??= new();

				snapshot.Items = snapshot.Items
					.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path))
					.ToList();
				return snapshot;
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch (Exception ex) when (
				ex is JsonException or NotSupportedException) {
				QuarantineCorruptSnapshot(ex);
				return null;
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Could not read Web results snapshot; it was left in place: {ex.Message}");
				return null;
			}
			finally {
				_ioGate.Release();
			}
		}

		internal async Task SaveAsync(
			IReadOnlyCollection<DuplicateItem> items,
			IReadOnlyCollection<string> selectedPaths,
			IReadOnlyCollection<string> excludedDirectories,
			CancellationToken cancellationToken = default,
			IReadOnlyCollection<string>? protectedDirectories = null) {
			ArgumentNullException.ThrowIfNull(items);
			ArgumentNullException.ThrowIfNull(selectedPaths);
			ArgumentNullException.ThrowIfNull(excludedDirectories);
			await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (items.Count == 0 &&
					selectedPaths.Count == 0 &&
					excludedDirectories.Count == 0 &&
					(protectedDirectories == null || protectedDirectories.Count == 0)) {
					File.Delete(_path);
					File.Delete(_path + ".tmp");
					return;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
				var snapshot = new WebResultSnapshot {
					Version = WebResultSnapshot.CurrentVersion,
					SavedAtUtc = DateTimeOffset.UtcNow,
					Items = items.ToList(),
					SelectedPaths = selectedPaths.ToList(),
					ExcludedDirectories = excludedDirectories.ToList(),
					ProtectedDirectories = protectedDirectories?.ToList() ?? new()
				};
				await AtomicJsonWriter.WriteAsync(
					_path,
					snapshot,
					ResultSnapshotJsonContext.Default.WebResultSnapshot,
					cancellationToken).ConfigureAwait(false);
			}
			finally {
				_ioGate.Release();
			}
		}

		internal void Delete() {
			_ioGate.Wait();
			try {
				File.Delete(_path);
				File.Delete(_path + ".tmp");
			}
			finally {
				_ioGate.Release();
			}
		}

		void QuarantineCorruptSnapshot(Exception exception) {
			string quarantinePath =
				$"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
			try {
				File.Move(_path, quarantinePath);
			}
			catch {
				try { File.Delete(_path); }
				catch { }
			}
			Logger.Instance.Info(
				$"Could not restore Web results; the snapshot was quarantined: {exception.Message}");
		}

		public void Dispose() => _ioGate.Dispose();
	}
}

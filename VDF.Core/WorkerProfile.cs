// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Linq;

namespace VDF.Core {
	public enum WorkerStage {
		Metadata,
		FrameHash,
		AudioHash,
		VisualCompare,
		PHashCompare,
		PartialIndex,
		PartialExact,
		PartialVisualSource,
		PartialVisualClip,
		Thumbnail,
	}

	public sealed class WorkerStageMeasurement {
		public WorkerStage Stage { get; set; }
		public int Workers { get; set; }
		public double ItemsPerSecond { get; set; }
		public double ElapsedSeconds { get; set; }
		public int SuccessCount { get; set; }
		public int FailureCount { get; set; }
		public string ResultChecksum { get; set; } = string.Empty;
		public bool Valid { get; set; }
		public string? Note { get; set; }
	}

	public sealed class WorkerProfile {
		public const int CurrentSchemaVersion = 1;

		public int SchemaVersion { get; set; } = CurrentSchemaVersion;
		public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
		public int VisibleCpuCount { get; set; }
		public string Preset { get; set; } = "standard";
		public bool Completed { get; set; }
		public int MetadataWorkers { get; set; }
		public int FrameHashWorkers { get; set; }
		public int AudioHashWorkers { get; set; }
		public int VisualCompareWorkers { get; set; }
		public int PHashCompareWorkers { get; set; }
		public int PartialIndexWorkers { get; set; }
		public int PartialExactWorkers { get; set; }
		public int PartialVisualSourceWorkers { get; set; }
		public int PartialVisualClipWorkers { get; set; }
		public int ThumbnailWorkers { get; set; }
		public List<WorkerStageMeasurement> Stages { get; set; } = new();

		public bool TryValidate(out string? error) {
			if (SchemaVersion != CurrentSchemaVersion) {
				error = $"Unsupported worker-profile schema {SchemaVersion}; expected schema {CurrentSchemaVersion}.";
				return false;
			}
			if (!Completed) {
				error = "The worker profile is incomplete.";
				return false;
			}
			if (VisibleCpuCount < 1) {
				error = "The worker profile has an invalid visible CPU count.";
				return false;
			}
			foreach ((string name, int value) in GetRecommendations()) {
				if (value < 1 || value > WorkerParallelism.MaximumConfiguredWorkers) {
					error = $"{name} must be between 1 and {WorkerParallelism.MaximumConfiguredWorkers}.";
					return false;
				}
			}
			error = null;
			return true;
		}

		public void ApplyTo(Settings settings) {
			if (!TryValidate(out string? error))
				throw new InvalidOperationException(error);
			settings.MetadataMaxDegreeOfParallelism = MetadataWorkers;
			settings.FrameHashMaxDegreeOfParallelism = FrameHashWorkers;
			settings.AudioHashMaxDegreeOfParallelism = AudioHashWorkers;
			settings.VisualCompareMaxDegreeOfParallelism = VisualCompareWorkers;
			settings.PHashCompareMaxDegreeOfParallelism = PHashCompareWorkers;
			settings.PartialIndexMaxDegreeOfParallelism = PartialIndexWorkers;
			settings.PartialExactMaxDegreeOfParallelism = PartialExactWorkers;
			settings.PartialClipVisualMaxDegreeOfParallelism = Math.Clamp(
				Math.Min(PartialVisualSourceWorkers, PartialVisualClipWorkers),
				1,
				16);
			settings.ThumbnailMaxDegreeOfParallelism = ThumbnailWorkers;
		}

		public IEnumerable<(string Name, int Value)> GetRecommendations() {
			yield return (nameof(MetadataWorkers), MetadataWorkers);
			yield return (nameof(FrameHashWorkers), FrameHashWorkers);
			yield return (nameof(AudioHashWorkers), AudioHashWorkers);
			yield return (nameof(VisualCompareWorkers), VisualCompareWorkers);
			yield return (nameof(PHashCompareWorkers), PHashCompareWorkers);
			yield return (nameof(PartialIndexWorkers), PartialIndexWorkers);
			yield return (nameof(PartialExactWorkers), PartialExactWorkers);
			yield return (nameof(PartialVisualSourceWorkers), PartialVisualSourceWorkers);
			yield return (nameof(PartialVisualClipWorkers), PartialVisualClipWorkers);
			yield return (nameof(ThumbnailWorkers), ThumbnailWorkers);
		}
	}

	public static class WorkerParallelism {
		public const int MaximumConfiguredWorkers = 256;

		public static int Resolve(int stageValue, int globalValue, int? visibleCpuCount = null) {
			int visible = Math.Max(1, visibleCpuCount ?? Environment.ProcessorCount);
			int configured = stageValue switch {
				0 => globalValue,
				-1 => -1,
				> 0 => stageValue,
				_ => globalValue,
			};
			return configured switch {
				-1 => visible,
				> 0 => Math.Min(configured, MaximumConfiguredWorkers),
				_ => 1,
			};
		}
	}

	public static class WorkerAutotune {
		public static int[] CreateSweep(int visibleCpuCount) {
			int visible = Math.Max(1, visibleCpuCount);
			int[] preferred = { 1, visible, 2, 20, 4, 16, 6, 12, 8 };
			return preferred
				.Where(value => value <= visible)
				.Distinct()
				.ToArray();
		}

		public static int Recommend(IReadOnlyList<WorkerStageMeasurement> measurements) {
			WorkerStageMeasurement? reference = measurements
				.Where(item => item.Valid && item.SuccessCount > 0 && item.ResultChecksum.Length > 0)
				.OrderBy(item => item.Workers)
				.FirstOrDefault();
			if (reference == null)
				return 0;

			int minimumSuccess = (int)Math.Ceiling(reference.SuccessCount * 0.80);
			WorkerStageMeasurement[] valid = measurements
				.Where(item =>
					item.Valid &&
					item.ItemsPerSecond > 0 &&
					item.SuccessCount >= minimumSuccess &&
					StringComparer.Ordinal.Equals(item.ResultChecksum, reference.ResultChecksum))
				.ToArray();
			if (valid.Length == 0)
				return 0;

			double maximumRate = valid.Max(item => item.ItemsPerSecond);
			double nearBestThreshold = maximumRate * 0.97;
			return valid
				.Where(item => item.ItemsPerSecond >= nearBestThreshold)
				.Min(item => item.Workers);
		}
	}
}

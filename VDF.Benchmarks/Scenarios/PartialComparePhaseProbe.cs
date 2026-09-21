// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Diagnostics;
using VDF.Core;
using VDF.Core.Utils;

namespace VDF.Benchmarks.Scenarios;

/// <summary>
/// Exact-versus-indexed partial-clip phase probe with a deterministic
/// mixed-duration corpus and injected known matches.
/// Run with:
///
///   dotnet run -c Release --project VDF.Benchmarks -- --probe-partial-compare [count]
/// </summary>
public static class PartialComparePhaseProbe {
	public static int Run(string[] args) {
		int count = args.Length > 1 && int.TryParse(args[1], out int parsed)
			? Math.Max(10, parsed)
			: 100;
		List<FileEntry> entries = BuildCorpus(count);
		DatabaseUtils.Database.Clear();
		foreach (FileEntry entry in entries)
			DatabaseUtils.Database.Add(entry);

		Console.WriteLine("== Partial compare phase probe ==");
		Console.WriteLine($"videos: {count:N0}, visible CPUs: {Environment.ProcessorCount}");
		try {
			var workerCounts = new[] { 1, Math.Min(4, Environment.ProcessorCount), Environment.ProcessorCount }
				.Distinct()
				.ToArray();
			HashSet<string>? exactReference = null;
			foreach (PartialClipSearchMode mode in new[] {
				PartialClipSearchMode.Exact,
				PartialClipSearchMode.FastBalanced,
			}) {
				foreach (int workers in workerCounts) {
					HashSet<string> results = RunOnce(workers, mode, count);
					exactReference ??= results;
					int missing = exactReference.Except(results).Count();
					int extra = results.Except(exactReference).Count();
					Console.WriteLine($"  delta vs first Exact: missing={missing:N0}, extra={extra:N0}");
				}
			}
		}
		finally {
			DatabaseUtils.Database.Clear();
		}
		return 0;
	}

	internal static List<FileEntry> BuildCorpus(int count) {
		var rng = new Random(24680);
		var entries = new List<FileEntry>(count);
		for (int i = 0; i < count; i++) {
			int fingerprintLength = (i % 3) switch {
				0 => rng.Next(10, 90),
				1 => rng.Next(120, 900),
				_ => rng.Next(1200, 3600),
			};
			var fingerprint = new uint[fingerprintLength];
			for (int j = 0; j < fingerprint.Length; j++)
				fingerprint[j] = (uint)rng.NextInt64(1, uint.MaxValue);

			entries.Add(new FileEntry {
				_Path = $@"/bench/video-{i:D6}.mp4",
				Folder = "/bench",
				mediaInfo = new MediaInfo {
					Duration = TimeSpan.FromSeconds(fingerprintLength),
				},
				AudioFingerprint = fingerprint,
				invalid = false,
			});
		}

		// Replace one pair per 100 videos with a known 180-second clip embedded at
		// 90 seconds.  These are deterministic recall sentinels for Fast Balanced.
		for (int sourceIndex = 0; sourceIndex + 1 < entries.Count; sourceIndex += 100) {
			int sourceLength = 600 + sourceIndex % 300;
			var sourceFingerprint = new uint[sourceLength];
			for (int i = 0; i < sourceFingerprint.Length; i++)
				sourceFingerprint[i] = (uint)rng.NextInt64(1, uint.MaxValue);
			uint[] clipFingerprint = sourceFingerprint[90..270].ToArray();
			for (int i = 0; i < clipFingerprint.Length; i += 11)
				clipFingerprint[i] ^= 1u << (i % 32);

			entries[sourceIndex].mediaInfo!.Duration = TimeSpan.FromSeconds(sourceLength);
			entries[sourceIndex].AudioFingerprint = sourceFingerprint;
			entries[sourceIndex + 1].mediaInfo!.Duration = TimeSpan.FromSeconds(clipFingerprint.Length);
			entries[sourceIndex + 1].AudioFingerprint = clipFingerprint;
		}
		return entries;
	}

	static HashSet<string> RunOnce(int workers, PartialClipSearchMode mode, int videoCount) {
		var engine = new ScanEngine();
		engine.Settings.MaxDegreeOfParallelism = workers;
		engine.Settings.PartialClipMinRatio = 0.10;
		engine.Settings.PartialClipSimilarityThreshold = 0.80;
		engine.Settings.PartialClipRequireVisualMatch = false;
		engine.Settings.PartialClipSearchMode = mode;
		engine.Settings.PartialClipIndexMemoryLimitMB = 8192;

		long plannedPairs = 0;
		engine.ComparisonProgress += (_, e) => {
			if (e.Stage == ComparisonStage.PartialExactVerification)
				plannedPairs = e.Total;
		};

		var stopwatch = Stopwatch.StartNew();
		engine.ScanForPartialDuplicates();
		stopwatch.Stop();

		var resultKeys = engine.Duplicates
			.Where(x => x.Flags.HasFlag(DuplicateFlags.PartialClip))
			.Select(x => $"{x.Path}|{x.PartialClipOffset.Ticks}")
			.ToHashSet(StringComparer.Ordinal);
		int injectedTotal = 0;
		int injectedFound = 0;
		for (int sourceIndex = 0; sourceIndex + 1 < videoCount; sourceIndex += 100) {
			injectedTotal++;
			string clipPath = $@"/bench/video-{sourceIndex + 1:D6}.mp4";
			if (engine.Duplicates.Any(x =>
				x.Path == clipPath &&
				x.Flags.HasFlag(DuplicateFlags.PartialClip) &&
				x.PartialClipOffset == TimeSpan.FromSeconds(90)))
				injectedFound++;
		}

		Console.WriteLine(
			$"mode={mode,-12} workers={workers,2}: {stopwatch.Elapsed.TotalSeconds,9:F2}s, " +
			$"planned pairs={plannedPairs,12:N0}, results={engine.Duplicates.Count,6:N0}, " +
			$"injected recall={injectedFound}/{injectedTotal}");
		return resultKeys;
	}
}

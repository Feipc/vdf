// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.Utils;

namespace VDF.Core.Tests;

[Collection(DatabaseTestCollection.Name)]
public class PartialComparisonPipelineTests {
	[Fact]
	public void ScanForPartialDuplicates_ProducesSameResultsAtDifferentParallelism() {
		var sourceFingerprint = Enumerable.Range(1, 20).Select(i => (uint)(i * 7919)).ToArray();
		var clipFingerprint = sourceFingerprint[7..13];
		var unrelatedFingerprint = Enumerable.Range(1, 8).Select(i => 0xF0000000u + (uint)i).ToArray();
		var entries = new[] {
			Entry("/media/source.mp4", 20, sourceFingerprint),
			Entry("/media/unrelated.mp4", 8, unrelatedFingerprint),
			Entry("/media/clip.mp4", 6, clipFingerprint),
		};

		var singleThreaded = Run(entries, parallelism: 1);
		var fourWorkers = Run(entries, parallelism: 4);

		Assert.Equal(singleThreaded, fourWorkers);
		Assert.Contains(singleThreaded, x =>
			x.Path == "/media/clip.mp4" &&
			x.IsPartialClip &&
			x.Offset == TimeSpan.FromSeconds(7));
	}

	[Fact]
	public void ScanForPartialDuplicates_SkipsMissingSilentAndNonShorterFingerprints() {
		var entries = new[] {
			Entry("/media/source.mp4", 20, Enumerable.Range(1, 10).Select(i => (uint)i).ToArray()),
			Entry("/media/non-shorter-fingerprint.mp4", 5, Enumerable.Range(1, 10).Select(i => (uint)i).ToArray()),
			Entry("/media/silent.mp4", 5, new uint[5]),
			Entry("/media/no-audio.mp4", 5, null),
		};

		Assert.Empty(Run(entries, parallelism: 4));
	}

	[Fact]
	public void ScanForPartialDuplicates_FastAndExactFindSameInjectedNoisyClip() {
		uint[] source = RandomFingerprint(240, 42);
		uint[] clip = source[60..180].ToArray();
		for (int i = 0; i < clip.Length; i += 7)
			clip[i] ^= 1u << (i % 32);
		var entries = new[] {
			Entry("/media/source.mp4", 240, source),
			Entry("/media/clip.mp4", 120, clip),
			Entry("/media/unrelated.mp4", 150, RandomFingerprint(150, 99)),
		};

		var exact = Run(entries, 4, PartialClipSearchMode.Exact, similarity: 0.99);
		var fast = Run(entries, 4, PartialClipSearchMode.FastBalanced, similarity: 0.99);

		Assert.Equal(exact, fast);
		Assert.Contains(fast, x =>
			x.Path == "/media/clip.mp4" &&
			x.IsPartialClip &&
			x.Offset == TimeSpan.FromSeconds(60));
	}

	[Fact]
	public void ScanForPartialDuplicates_RecordsSourceAsSimilarityReference() {
		uint[] source = RandomFingerprint(180, 4242);
		var entries = new[] {
			Entry("/media/source.mp4", 180, source),
			Entry("/media/clip.mp4", 100, source[40..140]),
		};
		DatabaseUtils.Database.Clear();
		foreach (FileEntry entry in entries)
			DatabaseUtils.Database.Add(entry);

		try {
			var engine = new ScanEngine();
			engine.Settings.PartialClipSearchMode = PartialClipSearchMode.Exact;
			engine.Settings.PartialClipMinRatio = 0.10;
			engine.Settings.PartialClipSimilarityThreshold = 1.0;
			engine.Settings.PartialClipRequireVisualMatch = false;

			engine.ScanForPartialDuplicates();

			var sourceResult = engine.Duplicates.Single(x => x.Path == "/media/source.mp4");
			var clipResult = engine.Duplicates.Single(x => x.Path == "/media/clip.mp4");
			Assert.True(sourceResult.IsSimilarityReference);
			Assert.Null(sourceResult.SimilarityReferencePath);
			Assert.False(clipResult.IsSimilarityReference);
			Assert.Equal(sourceResult.Path, clipResult.SimilarityReferencePath);
		}
		finally {
			DatabaseUtils.Database.Clear();
		}
	}

	static (string Path, bool IsPartialClip, TimeSpan Offset)[] Run(
		IEnumerable<FileEntry> entries,
		int parallelism,
		PartialClipSearchMode searchMode = PartialClipSearchMode.Exact,
		double similarity = 1.0) {
		DatabaseUtils.Database.Clear();
		foreach (FileEntry entry in entries)
			DatabaseUtils.Database.Add(entry);

		try {
			var engine = new ScanEngine();
			engine.Settings.MaxDegreeOfParallelism = parallelism;
			engine.Settings.PartialClipMinRatio = 0.10;
			engine.Settings.PartialClipSimilarityThreshold = similarity;
			engine.Settings.PartialClipRequireVisualMatch = false;
			engine.Settings.PartialClipSearchMode = searchMode;
			engine.Settings.PartialClipIndexMemoryLimitMB = 256;

			engine.ScanForPartialDuplicates();

			return engine.Duplicates
				.Select(d => (
					d.Path,
					d.Flags.HasFlag(DuplicateFlags.PartialClip),
					d.PartialClipOffset))
				.OrderBy(x => x.Path, StringComparer.Ordinal)
				.ToArray();
		}
		finally {
			DatabaseUtils.Database.Clear();
		}
	}

	static uint[] RandomFingerprint(int length, int seed) {
		var rng = new Random(seed);
		var result = new uint[length];
		for (int i = 0; i < length; i++)
			result[i] = (uint)rng.NextInt64(1, uint.MaxValue);
		return result;
	}

	static FileEntry Entry(string path, double durationSeconds, uint[]? fingerprint) =>
		new() {
			_Path = path,
			Folder = "/media",
			mediaInfo = new MediaInfo {
				Duration = TimeSpan.FromSeconds(durationSeconds),
			},
			AudioFingerprint = fingerprint,
			invalid = false,
		};
}

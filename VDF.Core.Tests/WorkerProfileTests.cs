// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Text.Json;
using VDF.Core.Utils;

namespace VDF.Core.Tests;

public class WorkerProfileTests {
	[Theory]
	[InlineData(0, -1, 24)]
	[InlineData(-1, 6, 24)]
	[InlineData(8, -1, 8)]
	[InlineData(-2, 12, 12)]
	[InlineData(300, 12, 256)]
	public void ResolveStageParallelism_UsesInheritanceAndAllCoreSemantics(
		int stage,
		int global,
		int expected) {
		Assert.Equal(expected, WorkerParallelism.Resolve(stage, global, 24));
	}

	[Fact]
	public void WorkerSweep_ForTwentyFourCpusUsesInterleavedOrder() {
		Assert.Equal(
			new[] { 1, 24, 2, 20, 4, 16, 6, 12, 8 },
			WorkerAutotune.CreateSweep(24));
	}

	[Fact]
	public void WorkerSweep_OmitsValuesAboveVisibleCpuCount() {
		Assert.Equal(new[] { 1, 4, 2 }, WorkerAutotune.CreateSweep(4));
	}

	[Fact]
	public void Recommendation_ChoosesLowestWorkerWithinThreePercent() {
		var measurements = new[] {
			Measurement(4, 96.9),
			Measurement(8, 100.0),
			Measurement(12, 101.0),
			Measurement(16, 100.5),
		};

		int recommendation = WorkerAutotune.Recommend(measurements);

		Assert.Equal(8, recommendation);
	}

	[Fact]
	public void Recommendation_RejectsMismatchedAndIncompleteRuns() {
		var measurements = new[] {
			Measurement(4, 80, checksum: "reference", success: 100),
			Measurement(8, 200, checksum: "wrong", success: 100),
			Measurement(12, 300, checksum: "reference", success: 79),
		};

		Assert.Equal(4, WorkerAutotune.Recommend(measurements));
	}

	[Fact]
	public void WorkerProfile_JsonRoundTripsAndValidatesSchema() {
		var profile = new WorkerProfile {
			Completed = true,
			VisibleCpuCount = 24,
			Preset = "standard",
			MetadataWorkers = 8,
			FrameHashWorkers = 16,
			AudioHashWorkers = 20,
			VisualCompareWorkers = 24,
			PHashCompareWorkers = 24,
			PartialIndexWorkers = 24,
			PartialExactWorkers = 24,
			PartialVisualSourceWorkers = 6,
			PartialVisualClipWorkers = 6,
			ThumbnailWorkers = 4,
		};

		string json = JsonSerializer.Serialize(profile, CoreJsonContext.Default.WorkerProfile);
		WorkerProfile loaded = JsonSerializer.Deserialize(
			json,
			CoreJsonContext.Default.WorkerProfile)!;

		Assert.True(loaded.TryValidate(out string? error), error);
		Assert.Equal(16, loaded.FrameHashWorkers);
		var settings = new Settings();
		loaded.ApplyTo(settings);
		Assert.Equal(8, settings.MetadataMaxDegreeOfParallelism);
		Assert.Equal(16, settings.FrameHashMaxDegreeOfParallelism);
		Assert.Equal(20, settings.AudioHashMaxDegreeOfParallelism);
		Assert.Equal(24, settings.VisualCompareMaxDegreeOfParallelism);
		Assert.Equal(24, settings.PartialExactMaxDegreeOfParallelism);
		Assert.Equal(6, settings.PartialClipVisualMaxDegreeOfParallelism);
		Assert.Equal(4, settings.ThumbnailMaxDegreeOfParallelism);
		loaded.SchemaVersion++;
		Assert.False(loaded.TryValidate(out error));
		Assert.Contains("schema", error!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void OldSettingsJson_InheritsGlobalForNewStages() {
		Settings settings = JsonSerializer.Deserialize(
			"""{"MaxDegreeOfParallelism":12}""",
			CoreJsonContext.Default.Settings)!;

		Assert.Equal(0, settings.MetadataMaxDegreeOfParallelism);
		Assert.Equal(0, settings.FrameHashMaxDegreeOfParallelism);
		Assert.Equal(0, settings.AudioHashMaxDegreeOfParallelism);
		Assert.Equal(12, WorkerParallelism.Resolve(
			settings.FrameHashMaxDegreeOfParallelism,
			settings.MaxDegreeOfParallelism,
			24));
	}

	[Fact]
	public void InvalidProfile_DoesNotPartiallyApply() {
		var settings = new Settings {
			MetadataMaxDegreeOfParallelism = 3,
			FrameHashMaxDegreeOfParallelism = 4,
		};
		var profile = new WorkerProfile {
			Completed = true,
			VisibleCpuCount = 24,
			MetadataWorkers = 8,
			FrameHashWorkers = 999,
			AudioHashWorkers = 8,
			VisualCompareWorkers = 8,
			PHashCompareWorkers = 8,
			PartialIndexWorkers = 8,
			PartialExactWorkers = 8,
			PartialVisualSourceWorkers = 6,
			PartialVisualClipWorkers = 6,
			ThumbnailWorkers = 4,
		};

		Assert.Throws<InvalidOperationException>(() => profile.ApplyTo(settings));
		Assert.Equal(3, settings.MetadataMaxDegreeOfParallelism);
		Assert.Equal(4, settings.FrameHashMaxDegreeOfParallelism);
	}

	static WorkerStageMeasurement Measurement(
		int workers,
		double rate,
		string checksum = "reference",
		int success = 100) =>
		new() {
			Stage = WorkerStage.Metadata,
			Workers = workers,
			ItemsPerSecond = rate,
			SuccessCount = success,
			ResultChecksum = checksum,
			Valid = true,
		};
}

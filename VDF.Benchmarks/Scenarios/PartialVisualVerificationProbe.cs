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
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Benchmarks.Scenarios;

/// <summary>
/// Direct probe for the partial-clip visual phase. It compares the former six
/// FFmpeg-process launches per assignment with grouped source prefetch plus one
/// batched clip launch. Timings are informational; no unstable speed assertion is
/// made. Run with:
///
/// dotnet run -c Release --project VDF.Benchmarks -- --probe-partial-visual 24
/// </summary>
public static class PartialVisualVerificationProbe {
	public static int Run(string[] args) {
		if (!VideoCorpus.FfmpegAvailable) {
			Console.Error.WriteLine("FFmpeg CLI not on PATH; cannot provision corpus.");
			return 1;
		}
		int assignmentCount = args.Length > 1 && int.TryParse(args[1], out int parsed)
			? Math.Clamp(parsed, 1, 200)
			: 24;
		bool verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
		if (verbose)
			Logger.Instance.LogItemAdded += Console.WriteLine;
		string? path = VideoCorpus.Ensure(
			new VideoCorpus.Spec(VideoCorpus.Codec.H264, 1280, 720, 60));
		if (path == null) {
			Console.Error.WriteLine("Could not provision H.264 benchmark video.");
			return 1;
		}

		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;
		double[] clipTimes = PartialVisualVerificationUtils.BuildClipTimes(12);
		double[][] sourceTimes = Enumerable.Range(0, assignmentCount)
			.Select(index => clipTimes.Select(time => time + (index % 32) * 1.37).ToArray())
			.ToArray();

		Console.WriteLine("== Partial visual confirmation probe ==");
		Console.WriteLine($"assignments: {assignmentCount}, samples/assignment: {clipTimes.Length}");
		Console.WriteLine();

		var oldTimer = Stopwatch.StartNew();
		float[] expected = new float[assignmentCount];
		for (int assignment = 0; assignment < assignmentCount; assignment++) {
			var sourceFrames = new byte[]?[clipTimes.Length];
			var candidateFrames = new byte[]?[clipTimes.Length];
			for (int frame = 0; frame < clipTimes.Length; frame++) {
				sourceFrames[frame] = ExtractOne(path, sourceTimes[assignment][frame]);
				candidateFrames[frame] = ExtractOne(path, clipTimes[frame]);
			}
			PartialVisualVerificationUtils.CompareFrames(
				sourceFrames, candidateFrames, false, 0, out expected[assignment]);
		}
		oldTimer.Stop();
		Console.WriteLine(
			$"old per-frame: {oldTimer.Elapsed.TotalSeconds,8:0.00}s, " +
			$"process starts~{assignmentCount * clipTimes.Length * 2:N0}");

		int[] workerValues = verbose ? new[] { 1 } : new[] { 1, 4, 6, 8 };
		foreach (int workers in workerValues) {
			var sourceStats = new GrayFrameExtractionStats();
			var clipStats = new GrayFrameExtractionStats();
			var timer = Stopwatch.StartNew();
			double[] uniqueSourceTimes = sourceTimes.SelectMany(times => times).Distinct().Order().ToArray();
			byte[]?[] uniqueSourceFrames = FfmpegEngine.GetGrayFrames(
				path, uniqueSourceTimes, verbose, sourceStats);
			var sourceFrameMap = new Dictionary<double, byte[]?>(uniqueSourceTimes.Length);
			for (int i = 0; i < uniqueSourceTimes.Length; i++)
				sourceFrameMap[uniqueSourceTimes[i]] = uniqueSourceFrames[i];

			float[] actual = new float[assignmentCount];
			Parallel.For(
				0,
				assignmentCount,
				new ParallelOptions { MaxDegreeOfParallelism = workers },
				assignment => {
					byte[]?[] prefetchedSourceFrames = sourceTimes[assignment]
						.Select(time => sourceFrameMap[time])
						.ToArray();
					byte[]?[] candidateFrames = FfmpegEngine.GetGrayFrames(
						path, clipTimes, verbose, clipStats);
					PartialVisualVerificationUtils.CompareFrames(
						prefetchedSourceFrames,
						candidateFrames,
						false,
						0,
						out actual[assignment]);
				});
			timer.Stop();

			bool identical = expected.SequenceEqual(actual);
			Console.WriteLine(
				$"workers={workers,2}: {timer.Elapsed.TotalSeconds,8:0.00}s, " +
				$"source batches/fallback={sourceStats.CliBatches:N0}/{sourceStats.FallbackFrames:N0}, " +
				$"clip batches/fallback={clipStats.CliBatches:N0}/{clipStats.FallbackFrames:N0}, " +
				$"results identical={identical}");
			if (!identical)
				return 2;
		}
		return 0;
	}

	static byte[]? ExtractOne(string path, double positionSeconds) =>
		FfmpegEngine.GetThumbnail(new FfmpegSettings {
			File = path,
			Position = TimeSpan.FromSeconds(positionSeconds),
			GrayScale = 1,
		}, extendedLogging: false);
}

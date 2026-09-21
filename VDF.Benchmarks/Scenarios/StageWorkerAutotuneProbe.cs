// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VDF.Core;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Benchmarks.Scenarios;

/// <summary>
/// Read-only hybrid worker autotuner. Real media is used for FFmpeg/FFprobe I/O
/// stages and deterministic in-memory corpora are used for comparison stages.
/// </summary>
public static class StageWorkerAutotuneProbe {
	sealed record Options(
		string MediaDirectory,
		string OutputPath,
		string Preset,
		int ThumbnailCount,
		int MaximumFiles);

	public static int Run(string[] args) {
		if (!TryParseOptions(args, out Options? options, out string? error)) {
			Console.Error.WriteLine(error);
			PrintUsage();
			return 2;
		}
		if (!Directory.Exists(options.MediaDirectory)) {
			Console.Error.WriteLine($"Media directory does not exist: {options.MediaDirectory}");
			return 2;
		}
		if (!VideoCorpus.FfmpegAvailable || string.IsNullOrEmpty(FFProbeEngine.FFprobePath)) {
			Console.Error.WriteLine("FFmpeg and FFprobe must be available in PATH.");
			return 2;
		}

		using var cancellation = new CancellationTokenSource();
		ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => {
			eventArgs.Cancel = true;
			cancellation.Cancel();
		};
		Console.CancelKeyPress += cancelHandler;
		try {
			return Run(options, cancellation.Token);
		}
		finally {
			Console.CancelKeyPress -= cancelHandler;
		}
	}

	static int Run(Options options, CancellationToken cancellationToken) {
		int visibleCpus = Math.Max(1, Environment.ProcessorCount);
		int[] workerSweep = WorkerAutotune.CreateSweep(visibleCpus);
		Console.WriteLine("== VDF stage worker autotuner ==");
		Console.WriteLine($"preset: {options.Preset}, visible CPUs: {visibleCpus}");
		Console.WriteLine($"worker order: {string.Join(", ", workerSweep)}");
		Console.WriteLine($"media: {options.MediaDirectory}");

		string[] mediaFiles = SelectMediaFiles(options.MediaDirectory, options.MaximumFiles);
		if (mediaFiles.Length == 0) {
			Console.Error.WriteLine("No supported video files were found.");
			return 2;
		}
		Console.WriteLine($"selected real-media files: {mediaFiles.Length:N0}");
		Console.WriteLine();

		FfmpegEngine.UseNativeBinding = false;
		FfmpegEngine.HardwareAccelerationMode = FFHardwareAccelerationMode.none;
		FfmpegEngine.CustomFFArguments = string.Empty;

		var allMeasurements = new List<WorkerStageMeasurement>();
		WorkerProfile profile = new() {
			VisibleCpuCount = visibleCpus,
			Preset = options.Preset,
			Completed = false,
		};

		try {
			List<WorkerStageMeasurement> metadata = RunRealMediaSweep(
				WorkerStage.Metadata,
				mediaFiles,
				workerSweep,
				path => MetadataSignature(FFProbeEngine.GetMediaInfo(path, false)),
				cancellationToken);
			allMeasurements.AddRange(metadata);
			profile.MetadataWorkers = RecommendAndPrint(WorkerStage.Metadata, metadata);

			var mediaInfo = new Dictionary<string, MediaInfo>(StringComparer.Ordinal);
			foreach (string path in mediaFiles) {
				cancellationToken.ThrowIfCancellationRequested();
				MediaInfo? info = FFProbeEngine.GetMediaInfo(path, false);
				if (info != null)
					mediaInfo[path] = info;
			}
			string[] decodable = mediaFiles.Where(mediaInfo.ContainsKey).ToArray();
			if (decodable.Length == 0)
				throw new InvalidOperationException("FFprobe could not read any selected video.");

			string[] frameFiles = TakeEvenly(decodable, Math.Min(decodable.Length, StageFileLimit(options.Preset, 48)));
			List<float> positions = BuildPositions(options.ThumbnailCount);
			List<WorkerStageMeasurement> frameHash = RunRealMediaSweep(
				WorkerStage.FrameHash,
				frameFiles,
				workerSweep,
				path => FrameHashSignature(path, mediaInfo[path], positions),
				cancellationToken);
			allMeasurements.AddRange(frameHash);
			profile.FrameHashWorkers = RecommendAndPrint(WorkerStage.FrameHash, frameHash);

			string[] audioFiles = TakeEvenly(decodable, Math.Min(decodable.Length, StageFileLimit(options.Preset, 12)));
			List<WorkerStageMeasurement> audioHash = RunRealMediaSweep(
				WorkerStage.AudioHash,
				audioFiles,
				workerSweep,
				path => FingerprintSignature(ChromaprintEngine.ExtractFingerprint(
					path, false, cancellationToken)),
				cancellationToken);
			allMeasurements.AddRange(audioHash);
			profile.AudioHashWorkers = RecommendAndPrint(WorkerStage.AudioHash, audioHash);

			string[] visualFiles = TakeEvenly(decodable, Math.Min(decodable.Length, StageFileLimit(options.Preset, 24)));
			List<WorkerStageMeasurement> visualSource = RunRealMediaSweep(
				WorkerStage.PartialVisualSource,
				visualFiles,
				workerSweep,
				path => GrayFrameSignature(path, mediaInfo[path], 12),
				cancellationToken);
			allMeasurements.AddRange(visualSource);
			profile.PartialVisualSourceWorkers = RecommendAndPrint(
				WorkerStage.PartialVisualSource, visualSource, maximum: 16);

			List<WorkerStageMeasurement> visualClip = RunRealMediaSweep(
				WorkerStage.PartialVisualClip,
				visualFiles,
				workerSweep,
				path => GrayFrameSignature(path, mediaInfo[path], 3),
				cancellationToken);
			allMeasurements.AddRange(visualClip);
			profile.PartialVisualClipWorkers = RecommendAndPrint(
				WorkerStage.PartialVisualClip, visualClip, maximum: 16);

			string[] thumbnailFiles = TakeEvenly(decodable, Math.Min(decodable.Length, StageFileLimit(options.Preset, 48)));
			List<WorkerStageMeasurement> thumbnail = RunRealMediaSweep(
				WorkerStage.Thumbnail,
				thumbnailFiles,
				workerSweep,
				path => ThumbnailSignature(path, mediaInfo[path]),
				cancellationToken);
			allMeasurements.AddRange(thumbnail);
			profile.ThumbnailWorkers = RecommendAndPrint(WorkerStage.Thumbnail, thumbnail);

			List<WorkerStageMeasurement> visualCompare = RunVisualCompareSweep(
				workerSweep, usePHash: false, cancellationToken);
			allMeasurements.AddRange(visualCompare);
			profile.VisualCompareWorkers = RecommendAndPrint(
				WorkerStage.VisualCompare, visualCompare);

			List<WorkerStageMeasurement> pHashCompare = RunVisualCompareSweep(
				workerSweep, usePHash: true, cancellationToken);
			allMeasurements.AddRange(pHashCompare);
			profile.PHashCompareWorkers = RecommendAndPrint(
				WorkerStage.PHashCompare, pHashCompare);

			List<WorkerStageMeasurement> partialIndex = RunPartialIndexSweep(
				workerSweep, cancellationToken);
			allMeasurements.AddRange(partialIndex);
			profile.PartialIndexWorkers = RecommendAndPrint(
				WorkerStage.PartialIndex, partialIndex);

			List<WorkerStageMeasurement> partialExact = RunPartialExactSweep(
				workerSweep, cancellationToken);
			allMeasurements.AddRange(partialExact);
			profile.PartialExactWorkers = RecommendAndPrint(
				WorkerStage.PartialExact, partialExact);

			profile.Stages = allMeasurements;
			profile.Completed = profile.GetRecommendations().All(item => item.Value > 0);
			WriteProfile(options.OutputPath, profile);
			PrintSummary(profile, options.OutputPath);
			return profile.Completed ? 0 : 3;
		}
		catch (OperationCanceledException) {
			profile.Stages = allMeasurements;
			profile.Completed = false;
			WriteProfile(options.OutputPath, profile);
			Console.Error.WriteLine("Autotune cancelled; a non-importable partial report was written.");
			return 130;
		}
		catch (Exception exception) {
			profile.Stages = allMeasurements;
			profile.Completed = false;
			try { WriteProfile(options.OutputPath, profile); } catch { }
			Console.Error.WriteLine($"Autotune failed: {exception}");
			return 3;
		}
	}

	static List<WorkerStageMeasurement> RunRealMediaSweep(
		WorkerStage stage,
		IReadOnlyList<string> paths,
		IReadOnlyList<int> workers,
		Func<string, string?> operation,
		CancellationToken cancellationToken) {
		var result = new List<WorkerStageMeasurement>(workers.Count);
		foreach (int workerCount in workers) {
			cancellationToken.ThrowIfCancellationRequested();
			var signatures = new string?[paths.Count];
			int successes = 0;
			int failures = 0;
			var stopwatch = Stopwatch.StartNew();
			Parallel.For(
				0,
				paths.Count,
				new ParallelOptions {
					MaxDegreeOfParallelism = workerCount,
					CancellationToken = cancellationToken,
				},
				index => {
					try {
						string? signature = operation(paths[index]);
						signatures[index] = signature;
						if (signature == null)
							Interlocked.Increment(ref failures);
						else
							Interlocked.Increment(ref successes);
					}
					catch (OperationCanceledException) {
						throw;
					}
					catch {
						Interlocked.Increment(ref failures);
					}
				});
			stopwatch.Stop();
			var measurement = new WorkerStageMeasurement {
				Stage = stage,
				Workers = workerCount,
				ItemsPerSecond = stopwatch.Elapsed.TotalSeconds > 0
					? successes / stopwatch.Elapsed.TotalSeconds
					: 0,
				ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
				SuccessCount = successes,
				FailureCount = failures,
				ResultChecksum = HashStrings(signatures),
				Valid = successes > 0,
			};
			result.Add(measurement);
			PrintMeasurement(measurement);
		}
		return result;
	}

	static List<WorkerStageMeasurement> RunVisualCompareSweep(
		IReadOnlyList<int> workers,
		bool usePHash,
		CancellationToken cancellationToken) {
		const int entryCount = 3000;
		var result = new List<WorkerStageMeasurement>(workers.Count);
		foreach (int workerCount in workers) {
			cancellationToken.ThrowIfCancellationRequested();
			List<FileEntry> entries = ComparePhaseProbe.BuildCorpus(
				entryCount, new Random(12345), 1200, 240);
			DatabaseUtils.Database.Clear();
			foreach (FileEntry entry in entries)
				DatabaseUtils.Database.Add(entry);
			var engine = new ScanEngine();
			engine.Settings.ThumbnailCount = 2;
			engine.Settings.Percent = 96;
			engine.Settings.MaxDegreeOfParallelism = workerCount;
			engine.Settings.UsePHashing = usePHash;
			engine.Settings.DatabaseCheckpointIntervalMinutes = 0;
			engine.EnsureThumbnailPositions();
			var stopwatch = Stopwatch.StartNew();
			engine.ScanForDuplicates();
			stopwatch.Stop();
			string signature = HashStrings(engine.Duplicates
				.Select(item => item.Path)
				.OrderBy(value => value, StringComparer.Ordinal)
				.Cast<string?>()
				.ToArray());
			var measurement = CreateSyntheticMeasurement(
				usePHash ? WorkerStage.PHashCompare : WorkerStage.VisualCompare,
				workerCount,
				entryCount,
				stopwatch.Elapsed,
				signature);
			result.Add(measurement);
			PrintMeasurement(measurement);
			DatabaseUtils.Database.Clear();
		}
		return result;
	}

	static List<WorkerStageMeasurement> RunPartialIndexSweep(
		IReadOnlyList<int> workers,
		CancellationToken cancellationToken) {
		List<FileEntry> files = PartialComparePhaseProbe.BuildCorpus(1000);
		PartialCompareEntry[] entries = files.Select((entry, index) =>
			new PartialCompareEntry(
				entry,
				entry.mediaInfo!.Duration.TotalSeconds,
				entry.AudioFingerprint!,
				index)).ToArray();
		var result = new List<WorkerStageMeasurement>(workers.Count);
		foreach (int workerCount in workers) {
			cancellationToken.ThrowIfCancellationRequested();
			var stopwatch = Stopwatch.StartNew();
			bool built = PartialClipFingerprintIndex.TryBuild(
				entries,
				8L * 1024 * 1024 * 1024,
				cancellationToken,
				out _,
				out PartialClipIndexBuildStats stats,
				maxDegreeOfParallelism: workerCount);
			stopwatch.Stop();
			string signature = $"{built}|{stats.FingerprintBlocks}|{stats.PostingCount}|{stats.EstimatedBytes}";
			var measurement = CreateSyntheticMeasurement(
				WorkerStage.PartialIndex,
				workerCount,
				entries.Length,
				stopwatch.Elapsed,
				signature,
				built);
			result.Add(measurement);
			PrintMeasurement(measurement);
		}
		return result;
	}

	static List<WorkerStageMeasurement> RunPartialExactSweep(
		IReadOnlyList<int> workers,
		CancellationToken cancellationToken) {
		const int entryCount = 1000;
		var result = new List<WorkerStageMeasurement>(workers.Count);
		foreach (int workerCount in workers) {
			cancellationToken.ThrowIfCancellationRequested();
			List<FileEntry> entries = PartialComparePhaseProbe.BuildCorpus(entryCount);
			DatabaseUtils.Database.Clear();
			foreach (FileEntry entry in entries)
				DatabaseUtils.Database.Add(entry);
			var engine = new ScanEngine();
			engine.Settings.MaxDegreeOfParallelism = workerCount;
			engine.Settings.EnablePartialClipDetection = true;
			engine.Settings.PartialClipSearchMode = PartialClipSearchMode.Exact;
			engine.Settings.PartialClipRequireVisualMatch = false;
			engine.Settings.PartialClipSimilarityThreshold = 0.80;
			engine.Settings.PartialClipMinRatio = 0.10;
			var stopwatch = Stopwatch.StartNew();
			engine.ScanForPartialDuplicates();
			stopwatch.Stop();
			string signature = HashStrings(engine.Duplicates
				.Where(item => item.Flags.HasFlag(DuplicateFlags.PartialClip))
				.Select(item => $"{item.Path}|{item.PartialClipOffset.Ticks}")
				.OrderBy(value => value, StringComparer.Ordinal)
				.Cast<string?>()
				.ToArray());
			var measurement = CreateSyntheticMeasurement(
				WorkerStage.PartialExact,
				workerCount,
				entryCount,
				stopwatch.Elapsed,
				signature);
			result.Add(measurement);
			PrintMeasurement(measurement);
			DatabaseUtils.Database.Clear();
		}
		return result;
	}

	static WorkerStageMeasurement CreateSyntheticMeasurement(
		WorkerStage stage,
		int workers,
		int items,
		TimeSpan elapsed,
		string signature,
		bool valid = true) =>
		new() {
			Stage = stage,
			Workers = workers,
			ItemsPerSecond = elapsed.TotalSeconds > 0 ? items / elapsed.TotalSeconds : 0,
			ElapsedSeconds = elapsed.TotalSeconds,
			SuccessCount = valid ? items : 0,
			FailureCount = valid ? 0 : items,
			ResultChecksum = signature,
			Valid = valid,
		};

	static string? MetadataSignature(MediaInfo? info) {
		if (info == null)
			return null;
		var builder = new StringBuilder();
		builder.Append(info.Duration.Ticks).Append('|').Append(info.Streams.Length);
		foreach (MediaInfo.StreamInfo stream in info.Streams) {
			builder.Append('|').Append(stream.CodecType)
				.Append(':').Append(stream.CodecName)
				.Append(':').Append(stream.Width)
				.Append('x').Append(stream.Height);
		}
		return HashText(builder.ToString());
	}

	static string? FrameHashSignature(
		string path,
		MediaInfo mediaInfo,
		List<float> positions) {
		var entry = new FileEntry {
			_Path = path,
			Folder = Path.GetDirectoryName(path) ?? string.Empty,
			FileSize = new FileInfo(path).Length,
			mediaInfo = mediaInfo,
		};
		if (!FfmpegEngine.GetGrayBytesFromVideo(entry, positions, 0, false))
			return null;
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (KeyValuePair<double, byte[]?> frame in entry.grayBytes.OrderBy(item => item.Key)) {
			if (frame.Value != null)
				hash.AppendData(frame.Value);
		}
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	static string? FingerprintSignature(uint[]? fingerprint) {
		if (fingerprint == null)
			return null;
		byte[] bytes = new byte[fingerprint.Length * sizeof(uint)];
		Buffer.BlockCopy(fingerprint, 0, bytes, 0, bytes.Length);
		return Convert.ToHexString(SHA256.HashData(bytes));
	}

	static string? GrayFrameSignature(string path, MediaInfo info, int frameCount) {
		double duration = info.Duration.TotalSeconds;
		if (duration <= 1)
			return null;
		double[] times = Enumerable.Range(1, frameCount)
			.Select(index => duration * index / (frameCount + 1))
			.ToArray();
		byte[]?[] frames = FfmpegEngine.GetGrayFrames(path, times, false);
		if (frames.All(frame => frame == null))
			return null;
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (byte[]? frame in frames)
			hash.AppendData(frame ?? Array.Empty<byte>());
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	static string? ThumbnailSignature(string path, MediaInfo info) {
		byte[]? jpeg = FfmpegEngine.ExtractThumbnailJpeg(
			path,
			TimeSpan.FromTicks(info.Duration.Ticks / 2),
			480,
			false,
			85);
		return jpeg == null
			? null
			: $"{jpeg.Length}:{Convert.ToHexString(SHA256.HashData(jpeg))}";
	}

	static List<float> BuildPositions(int count) {
		var result = new List<float>(count);
		for (int index = 1; index <= count; index++)
			result.Add((float)index / (count + 1));
		return result;
	}

	static string[] SelectMediaFiles(string root, int maximumFiles) {
		var files = new List<(string Path, long Length)>();
		try {
			foreach (string path in Directory.EnumerateFiles(
				root,
				"*",
				new EnumerationOptions {
					RecurseSubdirectories = true,
					IgnoreInaccessible = true,
					AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
				})) {
				if (!FileUtils.IsVideoExtension(Path.GetExtension(path)))
					continue;
				try {
					files.Add((path, new FileInfo(path).Length));
				}
				catch { }
			}
		}
		catch (Exception exception) {
			Console.Error.WriteLine($"Media enumeration warning: {exception.Message}");
		}
		return TakeEvenly(
			files.OrderBy(item => item.Length).ThenBy(item => item.Path, StringComparer.Ordinal)
				.Select(item => item.Path).ToArray(),
			Math.Min(maximumFiles, files.Count));
	}

	static string[] TakeEvenly(IReadOnlyList<string> values, int count) {
		if (count <= 0 || values.Count == 0)
			return Array.Empty<string>();
		if (count >= values.Count)
			return values.ToArray();
		if (count == 1)
			return new[] { values[values.Count / 2] };
		var result = new string[count];
		for (int index = 0; index < count; index++) {
			int sourceIndex = (int)Math.Round(
				index * (values.Count - 1d) / (count - 1d),
				MidpointRounding.AwayFromZero);
			result[index] = values[sourceIndex];
		}
		return result;
	}

	static int RecommendAndPrint(
		WorkerStage stage,
		IReadOnlyList<WorkerStageMeasurement> measurements,
		int maximum = WorkerParallelism.MaximumConfiguredWorkers) {
		int rawRecommendation = WorkerAutotune.Recommend(measurements);
		int recommended = rawRecommendation > 0
			? Math.Min(maximum, rawRecommendation)
			: 0;
		Console.WriteLine(recommended > 0
			? $"=> {stage}: recommend {recommended} worker(s)" +
				(rawRecommendation > maximum ? $" (runtime cap {maximum})" : string.Empty)
			: $"=> {stage}: no safe recommendation");
		Console.WriteLine();
		return recommended;
	}

	static void PrintMeasurement(WorkerStageMeasurement measurement) {
		Console.WriteLine(
			$"{measurement.Stage,-24} workers={measurement.Workers,2}: " +
			$"{measurement.ElapsedSeconds,8:F2}s, " +
			$"{measurement.ItemsPerSecond,9:F2} items/s, " +
			$"ok/fail={measurement.SuccessCount}/{measurement.FailureCount}");
	}

	static void PrintSummary(WorkerProfile profile, string outputPath) {
		Console.WriteLine("== Recommendations ==");
		foreach ((string name, int value) in profile.GetRecommendations())
			Console.WriteLine($"{name,-34} {value}");
		Console.WriteLine($"profile: {outputPath}");
		Console.WriteLine($"importable: {profile.Completed}");
	}

	static void WriteProfile(string outputPath, WorkerProfile profile) {
		string fullPath = Path.GetFullPath(outputPath);
		string? directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(
			fullPath,
			JsonSerializer.Serialize(profile, CoreJsonContext.Default.WorkerProfile));
	}

	static string HashStrings(IReadOnlyList<string?> values) =>
		HashText(string.Join("\n", values.Select(value => value ?? "<failed>")));

	static string HashText(string text) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	static int StageFileLimit(string preset, int standard) => preset switch {
		"quick" => Math.Max(8, standard / 3),
		"deep" => standard * 4,
		_ => standard,
	};

	static bool TryParseOptions(
		string[] args,
		out Options? options,
		out string? error) {
		string? mediaDirectory = GetOption(args, "--media-dir");
		string? outputPath = GetOption(args, "--output");
		string preset = (GetOption(args, "--preset") ?? "standard").ToLowerInvariant();
		int thumbnailCount = ParsePositiveInt(GetOption(args, "--thumbnail-count"), 5);
		int defaultMaximum = preset switch {
			"quick" => 48,
			"standard" => 144,
			"deep" => 512,
			_ => 0,
		};
		int maximumFiles = ParsePositiveInt(GetOption(args, "--max-files"), defaultMaximum);
		if (mediaDirectory == null) {
			options = null;
			error = "--media-dir is required.";
			return false;
		}
		if (outputPath == null) {
			options = null;
			error = "--output is required.";
			return false;
		}
		if (defaultMaximum == 0) {
			options = null;
			error = "--preset must be quick, standard, or deep.";
			return false;
		}
		options = new Options(
			mediaDirectory,
			outputPath,
			preset,
			Math.Clamp(thumbnailCount, 1, 20),
			Math.Max(8, maximumFiles));
		error = null;
		return true;
	}

	static string? GetOption(string[] args, string name) {
		for (int index = 0; index + 1 < args.Length; index++)
			if (StringComparer.OrdinalIgnoreCase.Equals(args[index], name))
				return args[index + 1];
		return null;
	}

	static int ParsePositiveInt(string? value, int fallback) =>
		int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
		parsed > 0
			? parsed
			: fallback;

	static void PrintUsage() {
		Console.Error.WriteLine(
			"Usage: --probe-stage-autotune --media-dir PATH --output FILE " +
			"[--preset quick|standard|deep] [--thumbnail-count N] [--max-files N]");
	}
}

// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using VDF.Core.Utils;

namespace VDF.Core {
	internal static class PartialVisualVerificationUtils {
		internal const int DefaultParallelism = 6;
		internal const int CliBatchSize = 24;

		internal static int ResolveParallelism(int effectiveParallelism, int configuredLimit) {
			int total = Math.Max(1, effectiveParallelism);
			int limit = configuredLimit > 0
				? configuredLimit
				: DefaultParallelism;
			return Math.Min(total, Math.Clamp(limit, 1, 16));
		}

		internal static double[] BuildClipTimes(double clipSeconds) {
			if (clipSeconds >= 9.0)
				return new[] { clipSeconds * 0.25, clipSeconds * 0.50, clipSeconds * 0.75 };
			if (clipSeconds >= 3.0)
				return new[] { clipSeconds * 0.33, clipSeconds * 0.66 };
			return new[] { clipSeconds * 0.5 };
		}

		internal static (int Start, int Count)[] GetBatches(int count, int batchSize) {
			if (count <= 0)
				return Array.Empty<(int, int)>();
			if (batchSize <= 0)
				throw new ArgumentOutOfRangeException(nameof(batchSize));
			var result = new List<(int, int)>((count + batchSize - 1) / batchSize);
			for (int start = 0; start < count; start += batchSize)
				result.Add((start, Math.Min(batchSize, count - start)));
			return result.ToArray();
		}

		internal static bool CompareFrames(
			IReadOnlyList<byte[]?> sourceFrames,
			IReadOnlyList<byte[]?> clipFrames,
			bool usePHash,
			double threshold,
			out float visualSimilarity) {
			int comparisons = 0;
			float similaritySum = 0;
			int count = Math.Min(sourceFrames.Count, clipFrames.Count);
			for (int i = 0; i < count; i++) {
				byte[]? sourceFrame = sourceFrames[i];
				byte[]? clipFrame = clipFrames[i];
				if (sourceFrame == null || clipFrame == null)
					continue;

				float pairSimilarity;
				if (usePHash) {
					ulong sourceHash = pHash.PerceptualHash.ComputePHashFromGray32x32(sourceFrame);
					ulong clipHash = pHash.PerceptualHash.ComputePHashFromGray32x32(clipFrame);
					pHash.PHashCompare.IsDuplicateByPercent(
						sourceHash,
						clipHash,
						out pairSimilarity,
						threshold,
						strict: true);
				}
				else {
					pairSimilarity = 1f - GrayBytesUtils.PercentageDifference(sourceFrame, clipFrame);
				}
				similaritySum += pairSimilarity;
				comparisons++;
			}

			if (comparisons == 0) {
				visualSimilarity = 0;
				return true;
			}
			visualSimilarity = similaritySum / comparisons;
			return visualSimilarity >= threshold;
		}
	}
}

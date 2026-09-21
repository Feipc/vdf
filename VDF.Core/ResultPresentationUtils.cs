// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Linq;
using System.Globalization;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Core {
	/// <summary>Shared, deterministic presentation rules used by the Web results UI.</summary>
	internal static class ResultPresentationUtils {
		internal static string FormatSimilarityScore(float score) {
			if (!float.IsFinite(score))
				return score.ToString(CultureInfo.InvariantCulture);
			decimal rounded = Math.Round(
				(decimal)score,
				1,
				MidpointRounding.AwayFromZero);
			return rounded.ToString("F1", CultureInfo.InvariantCulture);
		}

		internal static string FormatGroupSimilarity(IEnumerable<DuplicateItem> items) {
			List<DuplicateItem> group = items.ToList();
			float[] scores = group
				.Where(item => !IsSimilarityReference(item, group))
				.Select(item => item.Similarity)
				.ToArray();
			if (scores.Length == 0)
				scores = group.Select(item => item.Similarity).ToArray();
			if (scores.Length == 0)
				return "No match score";

			float minimum = scores.Min();
			float maximum = scores.Max();
			string minimumText = FormatSimilarityScore(minimum);
			string maximumText = FormatSimilarityScore(maximum);
			return minimumText == maximumText
				? $"Match score {minimumText}%"
				: $"Match scores {minimumText}\u2013{maximumText}%";
		}

		internal static bool IsSimilarityReference(
			DuplicateItem item,
			IReadOnlyCollection<DuplicateItem> group) =>
			item.IsSimilarityReference ||
			(group.Any(candidate => candidate.Flags.HasFlag(DuplicateFlags.PartialClip)) &&
				!item.Flags.HasFlag(DuplicateFlags.PartialClip));

		internal static bool IsInDirectory(string filePath, string directoryPath) {
			if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
				return false;
			try {
				string? parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
				if (parent == null)
					return false;
				string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
				string actual = Path.TrimEndingDirectorySeparator(parent);
				return string.Equals(
					actual,
					expected,
					CoreUtils.IsWindows
						? StringComparison.OrdinalIgnoreCase
						: StringComparison.Ordinal);
			}
			catch {
				return false;
			}
		}

		internal static bool PathMatchesFilter(string path, string needle) {
			if (needle.IndexOfAny(['*', '?']) < 0)
				return path.Contains(needle, StringComparison.OrdinalIgnoreCase);
			string pattern = needle;
			if (!pattern.StartsWith('*'))
				pattern = "*" + pattern;
			if (!pattern.EndsWith('*'))
				pattern += "*";
			return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, path);
		}

		internal static DuplicateItem? FindResultByPath(
			IEnumerable<DuplicateItem> items,
			string requestedPath,
			out string normalizedPath) {
			try {
				normalizedPath = Path.GetFullPath(requestedPath);
			}
			catch {
				normalizedPath = string.Empty;
				return null;
			}
			StringComparison comparison = CoreUtils.IsWindows
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			string targetPath = normalizedPath;
			return items.FirstOrDefault(item => {
				try {
					return string.Equals(
						Path.GetFullPath(item.Path),
						targetPath,
						comparison);
				}
				catch {
					return false;
				}
			});
		}
	}

	internal static class ThumbnailPositionResolver {
		internal static int GetFrameCount(DuplicateItem item, Settings settings) =>
			item.IsImage ? 1 : Math.Clamp(settings.ThumbnailCount, 1, 20);

		internal static bool TryGetPosition(
			DuplicateItem item,
			Settings settings,
			int index,
			out TimeSpan position) {
			int count = GetFrameCount(item, settings);
			if (index < 0 || index >= count) {
				position = TimeSpan.Zero;
				return false;
			}
			if (item.IsImage) {
				position = TimeSpan.Zero;
				return true;
			}
			if (index < item.ThumbnailTimestamps.Count) {
				position = item.ThumbnailTimestamps[index];
				return true;
			}

			double durationSeconds = Math.Max(0, item.Duration.TotalSeconds);
			if (settings.MaxSamplingDurationSeconds > 0)
				durationSeconds = Math.Min(durationSeconds, settings.MaxSamplingDurationSeconds);
			position = TimeSpan.FromSeconds(durationSeconds * (index + 1d) / (count + 1d));
			return true;
		}
	}
}

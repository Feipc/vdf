using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Web.Services;

public enum ResultSelectionRule {
	KeepProtectedFolders,
	KeepNewestCreated,
	KeepOldestCreated,
	KeepNewestModified,
	KeepOldestModified
}

/// <summary>Builds deletion selections without performing file operations.</summary>
public static class ResultSelectionRules {
	public static string NormalizeDirectory(string directory) {
		if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim()))
			throw new ArgumentException("Enter an absolute folder path on the server.");
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
	}

	public static bool IsProtected(string path, IEnumerable<string> directories) {
		try {
			string fullPath = Path.GetFullPath(path);
			var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
				? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			foreach (string directory in directories) {
				string root = NormalizeDirectory(directory);
				string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
				if (fullPath.StartsWith(prefix, comparison))
					return true;
			}
			return false;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			// Invalid paths cannot safely be selected for destructive operations.
			return true;
		}
	}

	public static HashSet<DuplicateItem> SelectForDeletion(
		IEnumerable<DuplicateItem> allItems,
		IEnumerable<DuplicateItem> scope,
		IReadOnlyCollection<string> protectedDirectories,
		ResultSelectionRule rule) {
		if (!Enum.IsDefined(rule))
			throw new ArgumentOutOfRangeException(nameof(rule));
		var scopedPaths = new HashSet<string>(scope.Select(item => item.Path), PathComparer.ForCurrentPlatform);
		var selected = new HashSet<DuplicateItem>();
		foreach (var group in allItems.GroupBy(item => item.GroupId)) {
			var items = group.ToList();
			if (items.Count < 2 || !items.Any(item => scopedPaths.Contains(item.Path)) ||
				items.Any(item => !HasValidPath(item.Path)))
				continue;
			IEnumerable<DuplicateItem> candidates;
			if (rule == ResultSelectionRule.KeepProtectedFolders) {
				if (!items.Any(item => IsProtected(item.Path, protectedDirectories)))
					continue;
				candidates = items;
			}
			else {
				bool created = rule is ResultSelectionRule.KeepNewestCreated or ResultSelectionRule.KeepOldestCreated;
				bool newest = rule is ResultSelectionRule.KeepNewestCreated or ResultSelectionRule.KeepNewestModified;
				DateTime? Date(DuplicateItem item) => created ? item.DateCreated : item.DateModified;
				// Old result snapshots may lack dates. Skip the whole group, never guess a keeper.
				if (items.Any(item => Date(item) is null || Date(item) == DateTime.MinValue))
					continue;
				DateTime? boundary = newest ? items.Max(Date) : items.Min(Date);
				candidates = items.Where(item => newest ? Date(item) < boundary : Date(item) > boundary);
			}
			foreach (var item in candidates)
				if (scopedPaths.Contains(item.Path) && !IsProtected(item.Path, protectedDirectories))
					selected.Add(item);
		}
		return selected;
	}

	static bool HasValidPath(string path) {
		try {
			Path.GetFullPath(path);
			return true;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
	}
}

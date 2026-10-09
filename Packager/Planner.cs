namespace Lustral.UnityLibraries;

/// <summary>A Unity version and the packages to build or rebuild for it.</summary>
internal sealed record WorkItem(UnityVersion Version, IReadOnlyList<string> Packages);

/// <summary>
/// Determines what a run builds: every final Unity release from the configured minimum onward, and for each, the packages
/// that its release does not yet contain or contains from an older definition. Packages whose module Unity does not offer
/// for a version are skipped, as are versions excluded in the configuration unless requested explicitly. Newer versions
/// are built first, because games are adopting them.
/// </summary>
internal static class Planner
{
    /// <param name="existing">Each existing release's marker, by tag.</param>
    /// <param name="only">Restricts the plan to these versions (with all packages if <paramref name="rebuild"/> is set).</param>
    public static IReadOnlyList<WorkItem> Plan(
        PackagesConfig config, IReadOnlyList<UnityRelease> releases, IReadOnlyDictionary<string, IReadOnlyDictionary<string, MarkerEntry>> existing,
        IReadOnlyCollection<UnityVersion>? only = null, bool rebuild = false, int limit = int.MaxValue)
    {
        var work = new List<WorkItem>();
        foreach (var release in releases.OrderByDescending(r => r.Version))
        {
            if (only is { Count: > 0 }
                    ? !only.Contains(release.Version)
                    : !release.Version.IsFinal || release.Version < config.Minimum || config.IsExcluded(release.Version))
                continue;
            var marker = existing.TryGetValue(release.Version.ToString(), out var found) ? found : new Dictionary<string, MarkerEntry>();
            var packages = config.Packages
                .Where(p => p.AppliesTo(release.Version, config) && release.Modules.ContainsKey(p.Module))
                .Where(p => rebuild || !marker.TryGetValue(p.Asset, out var entry) || entry.Definition != p.Definition)
                .Select(p => p.Asset)
                .ToList();
            if (packages.Count > 0)
                work.Add(new WorkItem(release.Version, packages));
            if (work.Count >= limit)
                break;
        }
        return work;
    }
}

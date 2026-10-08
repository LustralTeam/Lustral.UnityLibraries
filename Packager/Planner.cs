namespace Lustral.UnityLibraries;

/// <summary>A Unity version and the packages to (re)build for it.</summary>
internal sealed record WorkItem(UnityVersion Version, IReadOnlyList<string> Packages);

/// <summary>
/// Decides what a run builds: every final Unity release from the configured minimum on, and for each, the packages
/// its release doesn't have yet, or has from an older definition. A package whose module Unity doesn't offer for that
/// version is skipped. Newest versions first, since those are the ones games are moving to.
/// </summary>
internal static class Planner
{
    /// <param name="existing">Each existing release's marker, by tag.</param>
    /// <param name="only">Just these versions (and every package for them when <paramref name="rebuild"/>).</param>
    public static IReadOnlyList<WorkItem> Plan(
        PackagesConfig config, IReadOnlyList<UnityRelease> releases, IReadOnlyDictionary<string, IReadOnlyDictionary<string, MarkerEntry>> existing,
        IReadOnlyCollection<UnityVersion>? only = null, bool rebuild = false, int limit = int.MaxValue)
    {
        var work = new List<WorkItem>();
        foreach (var release in releases.OrderByDescending(r => r.Version))
        {
            if (only is { Count: > 0 } ? !only.Contains(release.Version) : !release.Version.IsFinal || release.Version < config.Minimum)
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

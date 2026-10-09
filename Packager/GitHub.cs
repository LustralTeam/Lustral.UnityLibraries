using System.Diagnostics;
using System.Text.Json;

namespace Lustral.UnityLibraries;

/// <summary>Accesses the repository's releases through the GitHub CLI (<c>gh</c>, available on all GitHub runners; it reads <c>GH_TOKEN</c>).</summary>
internal sealed class GitHub(string repository)
{
    /// <summary>Returns the marker of each release, by tag.</summary>
    public async Task<Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>> ReleaseMarkersAsync()
    {
        var output = await RunAsync("api", "--paginate", $"repos/{repository}/releases?per_page=100", "--jq", ".[] | {tag: .tag_name, body: .body} | @json");
        var markers = new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var release = JsonDocument.Parse(line);
            var tag = release.RootElement.GetProperty("tag").GetString();
            if (tag is not null)
                markers[tag] = Manifest.ReadMarker(release.RootElement.GetProperty("body").GetString());
        }
        return markers;
    }

    public async Task<bool> ReleaseExistsAsync(string tag) => (await TryRunAsync("release", "view", tag, "--repo", repository, "--json", "tagName")).Success;

    /// <summary>Returns the release's manifest, or null if it has none.</summary>
    public async Task<Manifest?> DownloadManifestAsync(string tag, string directory)
    {
        var result = await TryRunAsync("release", "download", tag, "--repo", repository, "--pattern", Manifest.FileName, "--dir", directory, "--clobber");
        var path = Path.Combine(directory, Manifest.FileName);
        return result.Success && File.Exists(path) ? Manifest.Read(path) : null;
    }

    public Task CreateReleaseAsync(string tag, string notesFile, string? target, IEnumerable<string> files)
    {
        List<string> arguments = ["release", "create", tag, .. files, "--repo", repository, "--title", tag, "--notes-file", notesFile];
        if (target is not null)
            arguments.AddRange(["--target", target]);
        return RunAsync([.. arguments]);
    }

    public Task UploadAsync(string tag, IEnumerable<string> files) => RunAsync(["release", "upload", tag, .. files, "--repo", repository, "--clobber"]);

    public Task EditNotesAsync(string tag, string notesFile) => RunAsync("release", "edit", tag, "--repo", repository, "--notes-file", notesFile);

    private static async Task<string> RunAsync(params string[] arguments)
    {
        var (success, output, error) = await TryRunAsync(arguments);
        return success ? output : throw new InvalidOperationException($"gh {arguments[0]} {arguments[1]} failed: {error.Trim()}");
    }

    private static async Task<(bool Success, string Output, string Error)> TryRunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start gh");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, await output, await error);
    }
}

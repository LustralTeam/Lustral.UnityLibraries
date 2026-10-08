using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lustral.UnityLibraries;

/// <summary>
/// <c>manifest.json</c>, an asset of every release: what each package holds and where it came from. Consumers read it
/// first, then download the package they need and check its SHA-256.
/// </summary>
internal sealed partial class Manifest
{
    public const string FileName = "manifest.json";
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public required string UnityVersion { get; init; }

    /// <summary>Unity's short changeset id for the version.</summary>
    public required string Revision { get; init; }

    /// <summary>By asset name.</summary>
    public SortedDictionary<string, PackageEntry> Packages { get; init; } = new(StringComparer.Ordinal);

    public static Manifest Read(string path) =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException($"{path} is empty");

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options) + "\n");

    /// <summary>Takes these packages' entries over, keeping the others: a release updated for one package keeps the rest.</summary>
    public Manifest Merge(Manifest newer)
    {
        if (newer.UnityVersion != UnityVersion)
            throw new ArgumentException($"Can't merge {newer.UnityVersion}'s manifest into {UnityVersion}'s");
        var packages = new SortedDictionary<string, PackageEntry>(Packages, StringComparer.Ordinal);
        foreach (var (asset, entry) in newer.Packages)
            packages[asset] = entry;
        return new Manifest { UnityVersion = UnityVersion, Revision = newer.Revision, Packages = packages };
    }

    /// <summary>The release's description: a table of the packages, and the marker the planner reads back.</summary>
    public string ReleaseNotes()
    {
        var text = new StringBuilder();
        text.Append("Unity's libraries for **Unity ").Append(UnityVersion).Append("** (changeset `").Append(Revision).AppendLine("`), from Unity's own installers.");
        text.AppendLine();
        text.AppendLine("| Package | Files | Size | SHA-256 |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (var (asset, entry) in Packages)
        {
            if (entry.State == PackageState.Built)
                text.Append("| `").Append(asset).Append("` | ").Append(entry.Files?.Count ?? 0).Append(" | ").Append(Size(entry.Size ?? 0))
                    .Append(" | `").Append(entry.Sha256).AppendLine("` |");
            else
                text.Append("| `").Append(asset).Append("` | not available: ").Append(entry.Reason ?? "unknown").AppendLine(" | | |");
        }
        text.AppendLine();
        text.AppendLine("`manifest.json` lists every file with its SHA-256. Unity's libraries are part of the Unity software and subject to Unity's terms.");
        text.AppendLine();
        var marker = Packages.ToDictionary(p => p.Key, p => new MarkerEntry(p.Value.Definition, p.Value.State));
        text.Append("<!-- ").Append(MarkerName).Append(' ').Append(JsonSerializer.Serialize(marker, Json.Compact)).AppendLine(" -->");
        return text.ToString();
    }

    private const string MarkerName = "lustral-unity-libraries";

    /// <summary>What a release's description says it holds, by asset; empty when it has no marker.</summary>
    public static IReadOnlyDictionary<string, MarkerEntry> ReadMarker(string? releaseNotes)
    {
        var match = releaseNotes is null ? Match.Empty : MarkerPattern().Match(releaseNotes);
        if (!match.Success)
            return new Dictionary<string, MarkerEntry>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, MarkerEntry>>(match.Groups[1].Value, Json.Options) ?? new Dictionary<string, MarkerEntry>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, MarkerEntry>();
        }
    }

    private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";

    [GeneratedRegex(@"<!--\s*" + MarkerName + @"\s+(\{.*?\})\s*-->", RegexOptions.Singleline)]
    private static partial Regex MarkerPattern();
}

internal enum PackageState
{
    Built,

    /// <summary>The version's module doesn't have the files (or Unity doesn't offer the module); not retried until the definition changes.</summary>
    Unavailable,
}

internal sealed record MarkerEntry(string Definition, PackageState State);

internal sealed class PackageEntry
{
    /// <summary><see cref="PackageDefinition.Definition"/> when it was built.</summary>
    public required string Definition { get; init; }

    public required PackageState State { get; init; }

    /// <summary>Why it's unavailable.</summary>
    public string? Reason { get; init; }

    public required string Module { get; init; }

    public string? ModuleUrl { get; init; }

    public string? ModuleMd5 { get; init; }

    /// <summary>The folder the files came from, relative to the module's install folder.</summary>
    public string? Source { get; init; }

    public long? Size { get; init; }

    public string? Sha256 { get; init; }

    public IReadOnlyList<FileEntry>? Files { get; init; }

    public DateTimeOffset Built { get; init; }
}

/// <param name="Name">Path inside the zip, with forward slashes.</param>
internal sealed record FileEntry(string Name, long Size, string Sha256);

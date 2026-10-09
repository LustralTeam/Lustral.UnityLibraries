using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lustral.UnityLibraries;

/// <summary><c>packages.json</c>: the package definitions for each release.</summary>
internal sealed class PackagesConfig
{
    public const string FileName = "packages.json";

    /// <summary>The earliest Unity version to publish.</summary>
    public required string MinimumVersion { get; init; }

    public required IReadOnlyList<PackageDefinition> Packages { get; init; }

    /// <summary>
    /// Unity versions that are not built, each with the reason, for example a module whose download does not match the
    /// hash in Unity's release API. A version requested explicitly (<c>plan --versions</c>) is built regardless.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExcludedVersions { get; init; } = new Dictionary<string, string>();

    [JsonIgnore]
    public UnityVersion Minimum => UnityVersion.Parse(MinimumVersion);

    public bool IsExcluded(UnityVersion version) => ExcludedVersions.Keys.Any(v => UnityVersion.Parse(v) == version);

    public PackageDefinition Package(string asset) =>
        Packages.FirstOrDefault(p => p.Asset == asset) ?? throw new ArgumentException($"{FileName} has no package {asset}");

    public static PackagesConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<PackagesConfig>(File.ReadAllText(path), Json.Options)
                     ?? throw new InvalidDataException($"{path} is empty");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        _ = Minimum;
        foreach (var (version, reason) in ExcludedVersions)
        {
            _ = UnityVersion.Parse(version);
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"{FileName}: excluded version {version} requires a reason");
        }
        if (Packages.Count == 0)
            throw new InvalidDataException($"{FileName} has no packages");
        foreach (var package in Packages)
        {
            if (!package.Asset.EndsWith(".zip", StringComparison.Ordinal) || package.Asset.Contains('/') || package.Asset.Contains('\\'))
                throw new InvalidDataException($"{FileName}: asset \"{package.Asset}\" must be a file name ending in .zip");
            if (string.IsNullOrWhiteSpace(package.Module))
                throw new InvalidDataException($"{FileName}: {package.Asset} requires the Unity module that contains its files (\"module\")");
            if (package.Sources.Count == 0 || package.Files.Count == 0)
                throw new InvalidDataException($"{FileName}: {package.Asset} requires \"sources\" and \"files\"");
            if (package.MinimumVersion is { } minimum)
                _ = UnityVersion.Parse(minimum);
        }
        if (Packages.GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidDataException($"{FileName}: {duplicate.Key} is defined twice");
    }
}

/// <summary>A zip asset included in every release, containing files from a single Unity module.</summary>
internal sealed class PackageDefinition
{
    /// <summary>The release asset's file name, for example <c>Managed-Windows.zip</c>.</summary>
    public required string Asset { get; init; }

    public string? Description { get; init; }

    /// <summary>The Unity module whose installer contains the files, by Unity's ID, for example <c>windows-il2cpp</c>.</summary>
    public required string Module { get; init; }

    /// <summary>
    /// Folders in the extracted module, relative to its installation folder (Unity's editor folder), tried in order. The
    /// first existing folder is used, because Unity renames them between versions.
    /// </summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>The files to include, relative to the source folder: <c>*</c> and <c>?</c> match within a name, <c>**</c> across folders.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>The earliest Unity version for which this package is built; defaults to the configuration's minimum.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>
    /// Identifies the package's composition. Changing the module, sources, files or minimum version changes this value, so
    /// that releases built from the previous definition have the asset rebuilt; changing the description does not.
    /// </summary>
    [JsonIgnore]
    public string Definition
    {
        get
        {
            var canonical = JsonSerializer.Serialize(new { Asset, Module, Sources, Files, MinimumVersion }, Json.Compact);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
        }
    }

    public bool AppliesTo(UnityVersion version, PackagesConfig config) =>
        version >= (MinimumVersion is { } minimum ? UnityVersion.Parse(minimum) : config.Minimum);
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

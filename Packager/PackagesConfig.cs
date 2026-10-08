using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lustral.UnityLibraries;

/// <summary><c>packages.json</c>: what each release holds.</summary>
internal sealed class PackagesConfig
{
    public const string FileName = "packages.json";

    /// <summary>The oldest Unity version to publish.</summary>
    public required string MinimumVersion { get; init; }

    public required IReadOnlyList<PackageDefinition> Packages { get; init; }

    [JsonIgnore]
    public UnityVersion Minimum => UnityVersion.Parse(MinimumVersion);

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
        if (Packages.Count == 0)
            throw new InvalidDataException($"{FileName} has no packages");
        foreach (var package in Packages)
        {
            if (!package.Asset.EndsWith(".zip", StringComparison.Ordinal) || package.Asset.Contains('/') || package.Asset.Contains('\\'))
                throw new InvalidDataException($"{FileName}: asset \"{package.Asset}\" must be a file name ending in .zip");
            if (string.IsNullOrWhiteSpace(package.Module))
                throw new InvalidDataException($"{FileName}: {package.Asset} needs the Unity module it comes from (\"module\")");
            if (package.Sources.Count == 0 || package.Files.Count == 0)
                throw new InvalidDataException($"{FileName}: {package.Asset} needs \"sources\" and \"files\"");
            if (package.MinimumVersion is { } minimum)
                _ = UnityVersion.Parse(minimum);
        }
        if (Packages.GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidDataException($"{FileName}: {duplicate.Key} is defined twice");
    }
}

/// <summary>One zip asset in every release: files from one Unity module.</summary>
internal sealed class PackageDefinition
{
    /// <summary>The release asset's file name, like <c>Managed-Windows.zip</c>.</summary>
    public required string Asset { get; init; }

    public string? Description { get; init; }

    /// <summary>The Unity module (Unity's id, like <c>windows-il2cpp</c>) whose installer the files come from.</summary>
    public required string Module { get; init; }

    /// <summary>
    /// Folders in the installed module, relative to its install folder (Unity's editor folder), tried in order: the first
    /// that exists is used, since Unity renames them between versions.
    /// </summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Which files, relative to the source folder: <c>*</c> and <c>?</c> within a name, <c>**</c> across folders.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>The oldest Unity version this package is built for; default: the config's.</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>
    /// Identifies what the package is made of. Changing the module, sources, files or minimum version changes it, so
    /// releases built from the old definition get the asset rebuilt; the description doesn't.
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

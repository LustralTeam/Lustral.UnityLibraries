using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lustral.UnityLibraries;

/// <summary>Builds a package's zip file from an extracted module, together with its manifest entry.</summary>
internal static class PackageBuilder
{
    // A fixed timestamp, so that rebuilding the same files produces identical entries.
    private static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="installed">The module's files, at their paths in its package.</param>
    /// <param name="contents">A description of the module's contents, used in the reason if the source folders are missing; defaults to its Variations folders.</param>
    public static PackageEntry Build(PackageDefinition package, UnityModule? module, string installed, string outputDirectory, string? contents = null)
    {
        var source = package.Sources.FirstOrDefault(s => ResolveDirectory(installed, s) is not null);
        if (source is null)
        {
            return Unavailable(package, module,
                $"none of the source folders is in the {package.Module} module ({string.Join(", ", package.Sources)}); it has {contents ?? Describe(installed)}");
        }
        var directory = ResolveDirectory(installed, source)!;
        var patterns = package.Files.Select(Glob).ToList();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .Where(file => patterns.Any(p => p.IsMatch(file.Name)))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return Unavailable(package, module, $"{source} has no files matching {string.Join(", ", package.Files)}");

        Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(outputDirectory, package.Asset);
        var entries = new List<FileEntry>();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (path, name) in files)
            {
                var bytes = File.ReadAllBytes(path);
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                entry.LastWriteTime = EntryTime;
                using (var stream = entry.Open())
                    stream.Write(bytes);
                entries.Add(new FileEntry(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
        }
        var zipBytes = File.ReadAllBytes(zipPath);
        return new PackageEntry
        {
            Definition = package.Definition,
            State = PackageState.Built,
            Module = package.Module,
            ModuleUrl = module?.Url.ToString(),
            ModuleMd5 = module?.Md5,
            Source = source,
            Size = zipBytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(zipBytes)),
            Files = entries,
            Built = DateTimeOffset.UtcNow,
        };
    }

    public static PackageEntry Unavailable(PackageDefinition package, UnityModule? module, string reason) => new()
    {
        Definition = package.Definition,
        State = PackageState.Unavailable,
        Reason = reason,
        Module = package.Module,
        ModuleUrl = module?.Url.ToString(),
        ModuleMd5 = module?.Md5,
        Built = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Returns the folder at <paramref name="relative"/>, matching each part case-insensitively. The case of Unity's folder
    /// names varies between versions (<c>WindowsStandaloneSupport</c>, <c>windowsstandalonesupport</c>), and Linux file
    /// systems are case-sensitive.
    /// </summary>
    internal static string? ResolveDirectory(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
                return null;
            var next = Directory.EnumerateDirectories(current).FirstOrDefault(d => string.Equals(Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            current = next;
        }
        return Directory.Exists(current) ? current : null;
    }

    /// <summary>
    /// Returns the folder that shows a package's layout for a path in it, if any: a player variation in a module
    /// (<c>Variations/win64_player_nondevelopment_mono</c>), or a class library profile in the editor
    /// (<c>Unity.app/Contents/MonoBleedingEdge/lib/mono/unityaot-win32</c>).
    /// </summary>
    internal static string? LayoutFolder(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > 2 && parts[0] == "Variations")
            return $"Variations/{parts[1]}";
        var mono = Array.IndexOf(parts, "MonoBleedingEdge");
        if (mono >= 0 && parts.Length > mono + 4 && parts[mono + 1] == "lib" && parts[mono + 2] == "mono")
            return string.Join('/', parts[..(mono + 4)]);
        return null;
    }

    /// <summary>Matches <c>*</c> and <c>?</c> within a path part and <c>**</c> across parts, case-insensitively.</summary>
    internal static Regex Glob(string pattern)
    {
        var regex = Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex($"^{regex}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Describes the existing Variations folders for the reason, as package definitions are most often incorrect there.
    private static string Describe(string installed)
    {
        var variations = Directory.Exists(installed)
            ? Directory.EnumerateDirectories(installed, "Variations", SearchOption.AllDirectories)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(d => Path.GetRelativePath(installed, d).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
        return variations.Count == 0 ? "no Variations folders" : string.Join(", ", variations);
    }
}

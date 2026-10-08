using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Lustral.UnityLibraries;

/// <summary>Builds one package's zip from an installed module, and its manifest entry.</summary>
internal static class PackageBuilder
{
    // Fixed, so a rebuild of the same files makes the same entries.
    private static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="installed">The module's install folder (or an editor folder that has the module).</param>
    public static PackageEntry Build(PackageDefinition package, UnityModule? module, string installed, string outputDirectory)
    {
        var source = package.Sources.FirstOrDefault(s => ResolveDirectory(installed, s) is not null);
        if (source is null)
        {
            return Unavailable(package, module,
                $"none of the source folders is in the {package.Module} module ({string.Join(", ", package.Sources)}); it has {Describe(installed)}");
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
    /// The folder at <paramref name="relative"/>, matching each part without regard to case: Unity's folder names vary in
    /// case between versions (<c>WindowsStandaloneSupport</c>, <c>windowsstandalonesupport</c>), and Linux file systems care.
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

    /// <summary><c>*</c> and <c>?</c> within one path part, <c>**</c> across parts; without regard to case.</summary>
    internal static Regex Glob(string pattern)
    {
        var regex = Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex($"^{regex}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // What's there instead, for the reason: the Variations folders, which is where package definitions usually go wrong.
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

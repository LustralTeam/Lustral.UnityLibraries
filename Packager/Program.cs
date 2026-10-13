using System.Text.Json;
using Lustral.UnityLibraries;

const string Usage = """
    Packager: builds the releases of Lustral.UnityLibraries, one per Unity version, from Unity's module packages.

    Usage:
      plan     [--repository owner/name] [--versions v1,v2] [--rebuild] [--limit n] [--github-output file]
               Lists the versions to build: final Unity releases from the minimum in packages.json onward whose release
               is missing a package or contains one from an older definition. Without --repository, assumes that no
               releases exist yet.
      build    --version <unity version> [--packages a.zip,b.zip] [--output dir] [--work dir]
               Downloads each package's Unity module (the macOS editor's .pkg for the module, or the editor's own for
               "editor", which can be read on any OS), extracts the files while downloading and writes the zip files to
               --output, together with a manifest.json.
      publish  --repository owner/name --version <unity version> --input dir [--target commit]
               Creates the version's release from the output of build, or updates the release's packages and manifest.

    Common option: --config <packages.json> (default: packages.json in the current folder).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return 0;
}

var options = Options.Parse(args.Skip(1));
var config = PackagesConfig.Load(options.Get("config") ?? PackagesConfig.FileName);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Lustral.UnityLibraries/1.0 (+https://github.com)");
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    switch (args[0])
    {
        case "plan":
            return await Plan();
        case "build":
            return await Build();
        case "publish":
            return await Publish();
        default:
            Console.Error.WriteLine($"Unknown command {args[0]}\n\n{Usage}");
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException or InvalidOperationException or NotSupportedException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

async Task<int> Plan()
{
    var releases = await UnityReleases.ListAsync(http, cancel.Token);
    Console.WriteLine($"Unity lists {releases.Count} releases; {releases.Count(r => r.Version.IsFinal && r.Version >= config.Minimum)} final ones from {config.MinimumVersion} on");
    var repository = options.Get("repository");
    var existing = repository is null
        ? new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>()
        : await new GitHub(repository).ReleaseMarkersAsync();
    Console.WriteLine($"{existing.Count} releases published");

    var only = options.List("versions").Select(UnityVersion.Parse).ToList();
    var unknown = only.Where(v => releases.All(r => r.Version != v)).ToList();
    if (unknown.Count > 0)
        throw new ArgumentException($"Unity does not list {string.Join(", ", unknown)}");
    var limit = int.TryParse(options.Get("limit"), out var n) && n > 0 ? n : int.MaxValue;
    var work = Planner.Plan(config, releases, existing, only, options.Has("rebuild"), limit);

    if (only.Count == 0)
    {
        foreach (var (version, reason) in config.ExcludedVersions)
            Console.WriteLine($"  {version}: excluded in {PackagesConfig.FileName}. {reason}");
    }

    foreach (var item in work)
        Console.WriteLine($"  {item.Version}: {string.Join(", ", item.Packages)}");
    Console.WriteLine(work.Count == 0 ? "Nothing to build" : $"{work.Count} version(s) to build");

    if (options.Get("github-output") is { } outputFile)
    {
        var matrix = new { include = work.Select(w => new { version = w.Version.ToString(), packages = string.Join(',', w.Packages) }) };
        await File.AppendAllTextAsync(outputFile, $"matrix={JsonSerializer.Serialize(matrix, Json.Compact)}\ncount={work.Count}\n");
    }
    return 0;
}

async Task<int> Build()
{
    var version = UnityVersion.Parse(options.Require("version"));
    var output = Path.GetFullPath(options.Get("output") ?? Path.Combine("out", version.ToString()));
    var work = Path.GetFullPath(options.Get("work") ?? "work");
    var packages = options.List("packages") is { Count: > 0 } names ? names.Select(config.Package).ToList() : config.Packages.ToList();

    var release = (await UnityReleases.ListAsync(http, cancel.Token)).FirstOrDefault(r => r.Version == version)
                  ?? throw new ArgumentException($"Unity does not list {version}");
    Console.WriteLine($"Building {version} (changeset {release.Revision}): {string.Join(", ", packages.Select(p => p.Asset))}");
    if (Directory.Exists(output))
        Directory.Delete(output, recursive: true);
    Directory.CreateDirectory(output);

    var manifest = new Manifest { UnityVersion = version.ToString(), Revision = release.Revision };
    foreach (var group in packages.GroupBy(p => p.Module))
    {
        if (!release.Modules.TryGetValue(group.Key, out var module))
        {
            foreach (var package in group)
                manifest.Packages[package.Asset] = PackageBuilder.Unavailable(package, null, $"Unity offers no {group.Key} module for {version}");
            continue;
        }

        var moduleWork = Path.Combine(work, version.ToString(), module.Id);
        if (Directory.Exists(moduleWork))
            Directory.Delete(moduleWork, recursive: true);

        // Only the files under the packages' source folders are written, while the package downloads; the folders that
        // show the package's layout are recorded for the reasons.
        var extracted = Path.Combine(moduleWork, "files");
        var sources = group.SelectMany(p => p.Sources).ToList();
        var layout = new SortedSet<string>(StringComparer.Ordinal);
        var count = await ModuleDownloader.ExtractAsync(http, module, extracted, path =>
        {
            if (PackageBuilder.LayoutFolder(path) is { } folder)
                layout.Add(folder);
            return sources.Any(source => PackageBuilder.IsInSource(path, source));
        }, cancel.Token);
        // A module without player variations, or an editor without class library profiles, does not have the expected
        // layout; the build fails so that it is investigated and retried.
        if (layout.Count == 0)
        {
            throw new InvalidDataException($"The {module.Id} package has no {(module.Id == UnityReleases.EditorModule ? "MonoBleedingEdge/lib/mono" : "Variations")} " +
                "folder; Unity may have changed its layout");
        }
        Console.WriteLine($"  {module.Id}: {count} files extracted; contains {string.Join(", ", layout)}");

        foreach (var package in group)
        {
            var entry = PackageBuilder.Build(package, module, extracted, output, string.Join(", ", layout));
            manifest.Packages[package.Asset] = entry;
            Console.WriteLine(entry.State == PackageState.Built
                ? $"  {package.Asset}: {entry.Files!.Count} files from {entry.Source}, {entry.Size / 1024} KB"
                : $"  {package.Asset}: not available ({entry.Reason})");
        }
        // Runners have limited disk space. Nothing is written if the package has none of the source folders.
        if (Directory.Exists(moduleWork))
            Directory.Delete(moduleWork, recursive: true);
    }
    manifest.Write(Path.Combine(output, Manifest.FileName));
    Console.WriteLine($"Wrote {output}");
    return 0;
}

async Task<int> Publish()
{
    var repository = options.Require("repository");
    var version = options.Require("version");
    var input = Path.GetFullPath(options.Require("input"));
    var built = Manifest.Read(Path.Combine(input, Manifest.FileName));
    if (built.UnityVersion != version)
        throw new ArgumentException($"{input} contains {built.UnityVersion}, not {version}");
    var github = new GitHub(repository);

    var download = Path.Combine(Path.GetTempPath(), $"unitylibraries-{version}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(download);
    var exists = await github.ReleaseExistsAsync(version);
    var manifest = exists && await github.DownloadManifestAsync(version, download) is { } current ? current.Merge(built) : built;

    var staging = Path.Combine(download, "upload");
    Directory.CreateDirectory(staging);
    var manifestPath = Path.Combine(staging, Manifest.FileName);
    manifest.Write(manifestPath);
    var notesPath = Path.Combine(download, "notes.md");
    await File.WriteAllTextAsync(notesPath, manifest.ReleaseNotes());
    var assets = built.Packages.Where(p => p.Value.State == PackageState.Built).Select(p => Path.Combine(input, p.Key)).Append(manifestPath).ToList();

    if (exists)
    {
        Console.WriteLine($"Updating release {version}: {string.Join(", ", assets.Select(Path.GetFileName))}");
        await github.UploadAsync(version, assets);
        await github.EditNotesAsync(version, notesPath);
    }
    else
    {
        // The description, which contains the marker that the planner relies on, is set last, so that a release whose
        // uploads failed partway has no marker and is built again by the next run.
        Console.WriteLine($"Creating release {version}: {string.Join(", ", assets.Select(Path.GetFileName))}");
        var placeholder = Path.Combine(download, "placeholder.md");
        await File.WriteAllTextAsync(placeholder, $"Unity {version}: uploading.");
        await github.CreateReleaseAsync(version, placeholder, options.Get("target"), assets);
        await github.EditNotesAsync(version, notesPath);
    }
    Directory.Delete(download, recursive: true);
    return 0;
}

/// <summary><c>--name value</c> and <c>--flag</c> arguments.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> arguments)
    {
        var options = new Options();
        var list = arguments.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument {list[i]}");
            var name = list[i][2..];
            var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
            options._values[name] = hasValue ? list[++i] : null;
        }
        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required");

    public IReadOnlyList<string> List(string name) =>
        Get(name)?.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

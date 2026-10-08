using System.Text.Json;
using Lustral.UnityLibraries;

const string Usage = """
    Packager: builds the releases of Lustral.UnityLibraries, one per Unity version, from Unity's own installers.

    Usage:
      plan     [--repository owner/name] [--versions v1,v2] [--rebuild] [--limit n] [--github-output file]
               What to build: final Unity releases from packages.json's minimum on whose release is missing a package, or
               has one from an older definition. Without --repository, as if there were no releases yet.
      build    --version <unity version> [--packages a.zip,b.zip] [--output dir] [--work dir] [--from-editor dir]
               Downloads and installs each package's Unity module (Windows only) and zips the files into --output, with a
               manifest.json for them. --from-editor reads an installed Unity editor folder instead, for trying it out.
      publish  --repository owner/name --version <unity version> --input dir [--target commit]
               Creates the version's release with what build wrote, or updates the release's packages and manifest.

    Options shared by all: --config <packages.json> (default: the one in the current folder).
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
catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
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
        throw new ArgumentException($"Unity doesn't list {string.Join(", ", unknown)}");
    var limit = int.TryParse(options.Get("limit"), out var n) && n > 0 ? n : int.MaxValue;
    var work = Planner.Plan(config, releases, existing, only, options.Has("rebuild"), limit);

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
    var fromEditor = options.Get("from-editor") is { } editor ? Path.GetFullPath(editor) : null;
    var packages = options.List("packages") is { Count: > 0 } names ? names.Select(config.Package).ToList() : config.Packages.ToList();

    var release = (await UnityReleases.ListAsync(http, cancel.Token)).FirstOrDefault(r => r.Version == version)
                  ?? throw new ArgumentException($"Unity doesn't list {version}");
    Console.WriteLine($"Building {version} (changeset {release.Revision}): {string.Join(", ", packages.Select(p => p.Asset))}");
    if (Directory.Exists(output))
        Directory.Delete(output, recursive: true);
    Directory.CreateDirectory(output);

    var manifest = new Manifest { UnityVersion = version.ToString(), Revision = release.Revision };
    foreach (var group in packages.GroupBy(p => p.Module))
    {
        release.Modules.TryGetValue(group.Key, out var module);
        string installed;
        if (fromEditor is not null)
            installed = fromEditor;
        else if (module is null)
        {
            foreach (var package in group)
                manifest.Packages[package.Asset] = PackageBuilder.Unavailable(package, null, $"Unity offers no {group.Key} module for {version}");
            continue;
        }
        else
        {
            installed = await ModuleInstaller.InstallAsync(http, module, Path.Combine(work, version.ToString()), cancel.Token);
            // An installer that put nothing in place failed, whatever its exit code said: that's not "unavailable".
            if (PackageBuilder.ResolveDirectory(installed, "Data/PlaybackEngines") is null)
                throw new InvalidOperationException($"{module.Id}'s installer exited normally but installed no Data/PlaybackEngines into {installed}");
        }

        foreach (var package in group)
        {
            var entry = PackageBuilder.Build(package, module, installed, output);
            manifest.Packages[package.Asset] = entry;
            Console.WriteLine(entry.State == PackageState.Built
                ? $"  {package.Asset}: {entry.Files!.Count} files from {entry.Source}, {entry.Size / 1024} KB"
                : $"  {package.Asset}: not available ({entry.Reason})");
        }
        if (fromEditor is null && Directory.Exists(installed))
            Directory.Delete(installed, recursive: true);
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
        throw new ArgumentException($"{input} holds {built.UnityVersion}, not {version}");
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
        // The description, which carries the marker the planner trusts, goes on last: a release whose uploads failed
        // halfway has none, so the next run builds it again.
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

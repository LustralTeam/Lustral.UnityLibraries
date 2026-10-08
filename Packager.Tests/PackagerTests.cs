using System.IO.Compression;
using System.Text.Json;

namespace Lustral.UnityLibraries.Tests;

public sealed class UnityVersionTests
{
    [Theory]
    [InlineData("6000.0.58f2", 6000, 0, 58, 'f', 2)]
    [InlineData("2019.4.0f1", 2019, 4, 0, 'f', 1)]
    [InlineData("6000.7.0b3", 6000, 7, 0, 'b', 3)]
    public void Parses_Unitys_versions(string text, int major, int minor, int patch, char type, int build) =>
        Assert.Equal(new UnityVersion(major, minor, patch, type, build), UnityVersion.Parse(text));

    [Theory]
    [InlineData("6000.0")]
    [InlineData("v6000.0.58f2")]
    [InlineData("6000.0.58")]
    public void Rejects_other_text(string text) => Assert.False(UnityVersion.TryParse(text, out _));

    [Fact]
    public void Orders_by_number_then_release_type_then_build()
    {
        string[] sorted = ["2019.4.40f1", "2022.3.62f1", "2022.3.62f2", "6000.0.0b9", "6000.0.0f1", "6000.0.58f2"];
        Assert.Equal(sorted, sorted.Reverse().Select(UnityVersion.Parse).Order().Select(v => v.ToString()));
    }
}

public sealed class UnityReleasesTests
{
    [Fact]
    public void Reads_the_md5_unity_gives_as_base64_of_its_hex_digest() =>
        Assert.Equal("dec48fe521eed3ed45017922f125feb6", UnityReleases.Md5("md5-ZGVjNDhmZTUyMWVlZDNlZDQ1MDE3OTIyZjEyNWZlYjY="));

    [Fact]
    public void Reads_an_md5_given_as_base64_of_its_bytes() =>
        Assert.Equal("dec48fe521eed3ed45017922f125feb6", UnityReleases.Md5("md5-3sSP5SHu0+1FAXki8SX+tg=="));

    [Theory]
    [InlineData(null)]
    [InlineData("sha256-abc")]
    [InlineData("md5-!!!")]
    public void Has_no_md5_without_one(string? integrity) => Assert.Null(UnityReleases.Md5(integrity));

    [Fact]
    public void Takes_the_macos_x64_editors_modules()
    {
        using var document = JsonDocument.Parse("""
            { "version": "2022.3.62f2", "shortRevision": "7670c08855a9", "downloads": [
              { "platform": "WINDOWS", "architecture": "X86_64", "modules": [ { "id": "windows-il2cpp", "url": "https://example.com/setup.exe", "type": "EXE" } ] },
              { "platform": "MAC_OS", "architecture": "X86_64", "modules": [
                { "id": "windows-mono", "url": "https://download.unity3d.com/x/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-2022.3.62f2.pkg",
                  "type": "PKG", "downloadSize": { "value": 182450165, "unit": "BYTE" }, "integrity": "md5-ZGVjNDhmZTUyMWVlZDNlZDQ1MDE3OTIyZjEyNWZlYjY=" } ] } ] }
            """);

        var release = UnityReleases.Parse(document.RootElement)!;

        Assert.Equal(("2022.3.62f2", "7670c08855a9"), (release.Version.ToString(), release.Revision));
        var module = Assert.Single(release.Modules).Value;
        Assert.Equal(("windows-mono", "PKG", 182450165L, "dec48fe521eed3ed45017922f125feb6"), (module.Id, module.Type, module.Size, module.Md5));
    }
}

public sealed class PlannerTests
{
    private static readonly PackagesConfig Config = new()
    {
        MinimumVersion = "2022.3.0f1",
        Packages =
        [
            new PackageDefinition { Asset = "Managed-Windows.zip", Module = "windows-il2cpp", Sources = ["a"], Files = ["*.dll"] },
            new PackageDefinition { Asset = "Managed-Linux.zip", Module = "linux-il2cpp", Sources = ["b"], Files = ["*.dll"] },
        ],
    };

    private static UnityRelease Release(string version, params string[] modules) => new(
        UnityVersion.Parse(version), "abc",
        modules.ToDictionary(m => m, m => new UnityModule(m, new Uri($"https://example.com/{m}.exe"), "EXE", 1, null)));

    private static IReadOnlyDictionary<string, MarkerEntry> Marker(params (string Asset, string Definition, PackageState State)[] entries) =>
        entries.ToDictionary(e => e.Asset, e => new MarkerEntry(e.Definition, e.State));

    private static string Windows => Config.Package("Managed-Windows.zip").Definition;

    private static string Linux => Config.Package("Managed-Linux.zip").Definition;

    [Fact]
    public void Builds_final_releases_from_the_minimum_on_newest_first()
    {
        var releases = new[]
        {
            Release("2021.3.45f1", "windows-il2cpp"), Release("2022.3.62f2", "windows-il2cpp"), Release("6000.0.58f2", "windows-il2cpp"),
            Release("6000.7.0b3", "windows-il2cpp"),
        };

        var work = Planner.Plan(Config, releases, new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>());

        Assert.Equal(["6000.0.58f2", "2022.3.62f2"], work.Select(w => w.Version.ToString()));
        Assert.All(work, w => Assert.Equal(["Managed-Windows.zip"], w.Packages)); // no linux-il2cpp module offered
    }

    [Fact]
    public void Builds_only_what_a_release_is_missing_or_has_from_an_older_definition()
    {
        var releases = new[] { Release("6000.0.58f2", "windows-il2cpp", "linux-il2cpp"), Release("6000.0.57f1", "windows-il2cpp", "linux-il2cpp"), Release("6000.0.56f1", "windows-il2cpp", "linux-il2cpp") };
        var existing = new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>
        {
            ["6000.0.58f2"] = Marker(("Managed-Windows.zip", Windows, PackageState.Built), ("Managed-Linux.zip", Linux, PackageState.Unavailable)),
            ["6000.0.57f1"] = Marker(("Managed-Windows.zip", Windows, PackageState.Built)),
            ["6000.0.56f1"] = Marker(("Managed-Windows.zip", "000000000000", PackageState.Built), ("Managed-Linux.zip", Linux, PackageState.Built)),
        };

        var work = Planner.Plan(Config, releases, existing);

        Assert.Equal(["6000.0.57f1: Managed-Linux.zip", "6000.0.56f1: Managed-Windows.zip"],
            work.Select(w => $"{w.Version}: {string.Join(", ", w.Packages)}"));
    }

    [Fact]
    public void Chosen_versions_can_be_rebuilt_whole_and_runs_can_be_limited()
    {
        var releases = new[] { Release("6000.0.58f2", "windows-il2cpp"), Release("6000.0.57f1", "windows-il2cpp"), Release("6000.0.56f1", "windows-il2cpp") };
        var existing = new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>
        {
            ["6000.0.57f1"] = Marker(("Managed-Windows.zip", Windows, PackageState.Built)),
        };

        Assert.Equal(["6000.0.57f1"], Planner.Plan(Config, releases, existing, [UnityVersion.Parse("6000.0.57f1")], rebuild: true).Select(w => w.Version.ToString()));
        Assert.Empty(Planner.Plan(Config, releases, existing, [UnityVersion.Parse("6000.0.57f1")]));
        Assert.Equal(["6000.0.58f2"], Planner.Plan(Config, releases, existing, limit: 1).Select(w => w.Version.ToString()));
    }

    [Fact]
    public void A_package_can_start_at_a_later_version()
    {
        var config = new PackagesConfig
        {
            MinimumVersion = "2022.3.0f1",
            Packages = [new PackageDefinition { Asset = "New.zip", Module = "windows-il2cpp", Sources = ["a"], Files = ["*"], MinimumVersion = "6000.0.0f1" }],
        };

        var work = Planner.Plan(config, [Release("6000.0.58f2", "windows-il2cpp"), Release("2022.3.62f2", "windows-il2cpp")],
            new Dictionary<string, IReadOnlyDictionary<string, MarkerEntry>>());

        Assert.Equal(["6000.0.58f2"], work.Select(w => w.Version.ToString()));
    }
}

public sealed class PackagesConfigTests
{
    [Fact]
    public void The_definition_changes_with_what_the_package_is_made_of_not_its_description()
    {
        var package = new PackageDefinition { Asset = "A.zip", Module = "m", Sources = ["s"], Files = ["*.dll"], Description = "one" };

        Assert.Equal(package.Definition, new PackageDefinition { Asset = "A.zip", Module = "m", Sources = ["s"], Files = ["*.dll"], Description = "two" }.Definition);
        Assert.NotEqual(package.Definition, new PackageDefinition { Asset = "A.zip", Module = "m", Sources = ["s", "t"], Files = ["*.dll"] }.Definition);
        Assert.NotEqual(package.Definition, new PackageDefinition { Asset = "A.zip", Module = "m", Sources = ["s"], Files = ["*"] }.Definition);
    }

    [Fact]
    public void The_repositorys_config_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", PackagesConfig.FileName);
        var config = PackagesConfig.Load(path);
        Assert.Contains(config.Packages, p => p.Asset == "Managed-Windows.zip");
        Assert.Contains(config.Packages, p => p.Asset == "Managed-Linux.zip");
    }

    [Theory]
    [InlineData("""{ "minimumVersion": "2019.4.0f1", "packages": [] }""")]
    [InlineData("""{ "minimumVersion": "2019.4", "packages": [ { "asset": "A.zip", "module": "m", "sources": ["s"], "files": ["*"] } ] }""")]
    [InlineData("""{ "minimumVersion": "2019.4.0f1", "packages": [ { "asset": "A.tar", "module": "m", "sources": ["s"], "files": ["*"] } ] }""")]
    [InlineData("""{ "minimumVersion": "2019.4.0f1", "packages": [ { "asset": "A.zip", "module": "m", "sources": ["s"], "files": ["*"] }, { "asset": "a.zip", "module": "m", "sources": ["s"], "files": ["*"] } ] }""")]
    public void Mistakes_are_refused(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            Assert.ThrowsAny<Exception>(() => PackagesConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ManifestTests
{
    private static Manifest Sample(string asset, PackageState state, string definition = "abc123abc123") => new()
    {
        UnityVersion = "6000.0.58f2",
        Revision = "92dee566b325",
        Packages = new SortedDictionary<string, PackageEntry>(StringComparer.Ordinal)
        {
            [asset] = new PackageEntry
            {
                Definition = definition, State = state, Module = "m", Size = 2048, Sha256 = "ff",
                Files = state == PackageState.Built ? [new FileEntry("A.dll", 1, "aa")] : null,
                Reason = state == PackageState.Built ? null : "no module",
            },
        },
    };

    [Fact]
    public void The_release_notes_carry_a_marker_the_planner_reads_back()
    {
        var notes = Sample("Managed-Windows.zip", PackageState.Built).Merge(Sample("Managed-Linux.zip", PackageState.Unavailable, "def456def456")).ReleaseNotes();

        var marker = Manifest.ReadMarker(notes);

        Assert.Equal(new MarkerEntry("abc123abc123", PackageState.Built), marker["Managed-Windows.zip"]);
        Assert.Equal(new MarkerEntry("def456def456", PackageState.Unavailable), marker["Managed-Linux.zip"]);
        Assert.Contains("not available: no module", notes);
        Assert.Empty(Manifest.ReadMarker("A release someone wrote by hand"));
    }

    [Fact]
    public void Merging_keeps_the_other_packages_and_replaces_rebuilt_ones()
    {
        var merged = Sample("Managed-Windows.zip", PackageState.Built, "old000old000")
            .Merge(Sample("Managed-Linux.zip", PackageState.Built))
            .Merge(Sample("Managed-Windows.zip", PackageState.Built, "new000new000"));

        Assert.Equal(["Managed-Linux.zip", "Managed-Windows.zip"], merged.Packages.Keys);
        Assert.Equal("new000new000", merged.Packages["Managed-Windows.zip"].Definition);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var path = Path.GetTempFileName();
        try
        {
            Sample("Managed-Windows.zip", PackageState.Built).Write(path);
            var read = Manifest.Read(path);
            Assert.Equal(("6000.0.58f2", PackageState.Built, "A.dll"), (read.UnityVersion, read.Packages["Managed-Windows.zip"].State, read.Packages["Managed-Windows.zip"].Files![0].Name));
            Assert.Contains("\"state\": \"built\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class PackageBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "unitylibraries-tests", Guid.NewGuid().ToString("N"));

    public PackageBuilderTests()
    {
        var managed = Path.Combine(_root, "install", "Data", "PlaybackEngines", "WindowsStandaloneSupport", "Variations", "win64_player_nondevelopment_il2cpp", "Data", "Managed");
        Directory.CreateDirectory(Path.Combine(managed, "Sub"));
        File.WriteAllText(Path.Combine(managed, "UnityEngine.CoreModule.dll"), "core");
        File.WriteAllText(Path.Combine(managed, "UnityEngine.AIModule.dll"), "ai");
        File.WriteAllText(Path.Combine(managed, "readme.txt"), "not a library");
        File.WriteAllText(Path.Combine(managed, "Sub", "Nested.dll"), "nested");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PackageDefinition Package(params string[] files) => new()
    {
        Asset = "Managed-Windows.zip",
        Module = "windows-il2cpp",
        Sources = ["Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment_il2cpp/Data/Managed", "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp/Data/Managed"],
        Files = files,
    };

    [Fact]
    public void Zips_the_matching_files_from_the_first_source_folder_that_exists_whatever_its_case()
    {
        var entry = PackageBuilder.Build(Package("*.dll"), null, Path.Combine(_root, "install"), Path.Combine(_root, "out"));

        Assert.Equal(PackageState.Built, entry.State);
        Assert.EndsWith("win64_player_nondevelopment_il2cpp/Data/Managed", entry.Source);
        Assert.Equal(["UnityEngine.AIModule.dll", "UnityEngine.CoreModule.dll"], entry.Files!.Select(f => f.Name));
        using var zip = ZipFile.OpenRead(Path.Combine(_root, "out", "Managed-Windows.zip"));
        Assert.Equal(["UnityEngine.AIModule.dll", "UnityEngine.CoreModule.dll"], zip.Entries.Select(e => e.FullName));
        Assert.All(zip.Entries, e => Assert.Equal(2000, e.LastWriteTime.Year));
    }

    [Fact]
    public void Double_star_reaches_into_folders() =>
        Assert.Equal(["Sub/Nested.dll", "UnityEngine.AIModule.dll", "UnityEngine.CoreModule.dll"],
            PackageBuilder.Build(Package("**/*.dll"), null, Path.Combine(_root, "install"), Path.Combine(_root, "out")).Files!.Select(f => f.Name));

    [Fact]
    public void Missing_files_make_the_package_unavailable_with_what_is_there()
    {
        var noFiles = PackageBuilder.Build(Package("*.so"), null, Path.Combine(_root, "install"), Path.Combine(_root, "out"));
        var noFolder = PackageBuilder.Build(new PackageDefinition { Asset = "Managed-Linux.zip", Module = "linux-il2cpp", Sources = ["Data/PlaybackEngines/LinuxStandaloneSupport/Variations/linux64/Data/Managed"], Files = ["*.dll"] },
            null, Path.Combine(_root, "install"), Path.Combine(_root, "out"));

        Assert.Equal(PackageState.Unavailable, noFiles.State);
        Assert.Contains("no files matching *.so", noFiles.Reason);
        Assert.Equal(PackageState.Unavailable, noFolder.State);
        Assert.Contains("it has Data/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_il2cpp", noFolder.Reason);
        Assert.False(File.Exists(Path.Combine(_root, "out", "Managed-Linux.zip")));
    }
}

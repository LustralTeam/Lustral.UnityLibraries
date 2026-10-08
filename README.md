# Lustral Unity Libraries

Unity's managed runtime libraries, one release per Unity version, for Lustral's interop
generation. IL2CPP games ship only the parts of Unity's API they use; Lustral's generator puts the rest back
("unstripping") from these libraries, so mods can use all of Unity's API on Windows and Linux IL2CPP games.

The releases are built automatically, by GitHub Actions, from Unity's own installers.

## Releases

Each release is tagged with the exact Unity version, like `6000.0.58f2`, and holds:

| Asset | What |
| --- | --- |
| `Managed-Windows.zip` | Unity's managed libraries (`UnityEngine.*.dll` and friends) from the Windows x64 IL2CPP player. |
| `Managed-Linux.zip` | The same from the Linux x64 IL2CPP player. |
| `manifest.json` | What each package holds and where it came from, with SHA-256 hashes. |

The zips hold the libraries at their root. A package Unity doesn't offer for a version (Linux IL2CPP isn't there for a
few older ones) is missing from that release, and `manifest.json` says why.

Download URLs follow the tag:

```
https://github.com/<owner>/Lustral.UnityLibraries/releases/download/<unity version>/manifest.json
https://github.com/<owner>/Lustral.UnityLibraries/releases/download/<unity version>/Managed-Windows.zip
```

### manifest.json

```json
{
  "format": 1,
  "unityVersion": "6000.0.58f2",
  "revision": "92dee566b325",
  "packages": {
    "Managed-Windows.zip": {
      "definition": "3f1a9c0e7b2d",
      "state": "built",
      "module": "windows-il2cpp",
      "moduleUrl": "https://download.unity3d.com/download_unity/92dee566b325/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-6000.0.58f2.exe",
      "moduleMd5": "…",
      "source": "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp/Data/Managed",
      "size": 4002086,
      "sha256": "…",
      "files": [ { "name": "UnityEngine.CoreModule.dll", "size": 1393584, "sha256": "…" } ],
      "built": "2026-10-08T12:00:00+00:00"
    },
    "Managed-Linux.zip": {
      "definition": "bc2fb47f7b19",
      "state": "unavailable",
      "reason": "Unity offers no linux-il2cpp module for …",
      "module": "linux-il2cpp"
    }
  }
}
```

Consumers read the manifest, download the package they need, and check its `sha256`.

## What gets built

[`packages.json`](packages.json) decides. Every final (`f`) Unity release from `minimumVersion` on gets a release, and
each package in the file is a zip in it:

```json
{
  "asset": "Managed-Windows.zip",
  "module": "windows-il2cpp",
  "sources": [ "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_player_nondevelopment_il2cpp/Data/Managed" ],
  "files": [ "*.dll" ]
}
```

- `module` is the Unity module the files come from, by the id Unity's release API uses (`windows-il2cpp`,
  `linux-il2cpp`, `mac-mono`, ...).
- `sources` are folders in the installed module, relative to the editor folder it installs into, tried in order: Unity
  renames them between versions.
- `files` picks files in the source folder: `*` and `?` within a name, `**` across folders.
- `minimumVersion` (optional) starts a package at a later Unity version than the rest.

**Adding a package, or changing one, updates the existing releases**: each package's definition has a hash, recorded in
the release, and the next run builds the package in every release that doesn't have it, or has it from another
definition, without touching the other packages. A package whose files aren't in a version's module is recorded as
unavailable, with the folders the module does have, and isn't retried until its definition changes.

## How it runs

[`.github/workflows/sync.yml`](.github/workflows/sync.yml) runs every 6 hours, when `packages.json` or the packager
changes, and by hand:

1. **Plan** (Ubuntu): asks Unity's release API (the one Unity Hub uses) for every release, compares with this
   repository's releases, and picks up to 40 versions to build, newest first.
2. **Build and publish** (Windows, a runner per version): downloads each needed module's installer from Unity's servers,
   checks it against the MD5 Unity publishes, installs it silently into a folder of its own, zips the package's files,
   and creates the release, or updates the packages that changed in an existing one.

Unity's Windows module installers are NSIS built for installers over 2 GB, which 7-Zip and other extractors can't open,
so they're run, as Unity Hub runs them, on a Windows runner.

To build or rebuild chosen versions, run the workflow by hand with their versions (and "rebuild" to replace what's
there).

## Running it locally

The packager is a .NET 10 console app:

```sh
dotnet run --project Packager -- plan --limit 5
dotnet run --project Packager -- build --version 6000.5.4f1 --from-editor "C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor"
dotnet run --project Packager.Tests
```

`--from-editor` reads an installed Unity editor instead of downloading and installing modules. `publish` needs the
GitHub CLI and a token (`GH_TOKEN`).

## Unity

Unity's libraries are part of the Unity software. Their use is subject to the
[Unity Terms of Service](https://unity.com/legal/terms-of-service) and the
[Unity Software Additional Terms](https://unity.com/legal/terms-of-service/software).

This repository is not sponsored by, affiliated with or endorsed by Unity Technologies or its affiliates. "Unity" is a
trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

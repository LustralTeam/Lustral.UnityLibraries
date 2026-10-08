# Lustral Unity Libraries

Unity's managed runtime libraries, one release per Unity version, for Lustral's interop
generation. IL2CPP games ship only the parts of Unity's API they use; Lustral's generator puts the rest back
("unstripping") from these libraries, so mods can use all of Unity's API on Windows and Linux IL2CPP games.

The releases are built automatically, by GitHub Actions, from Unity's own module packages, as
[MelonLoader.UnityDependencies](https://github.com/LavaGang/MelonLoader.UnityDependencies) does.

## Releases

Each release is tagged with the exact Unity version, like `6000.0.58f2`, and holds:

| Asset | What |
| --- | --- |
| `Managed-Windows.zip` | Unity's managed libraries (`UnityEngine.*.dll` and friends) from the Windows x64 player. |
| `Managed-Linux.zip` | The same from the Linux x64 IL2CPP player. |
| `manifest.json` | What each package holds and where it came from, with SHA-256 hashes. |

The zips hold the libraries at their root. A package Unity doesn't offer for a version (Linux IL2CPP isn't there for a
few older ones) is missing from that release, and `manifest.json` says why.

**About `Managed-Windows.zip`:** Unity publishes no Windows IL2CPP module for macOS, so the Windows libraries come from
its Windows Mono module: the `win64_player_nondevelopment_mono` player. For 2022.3.62f2 we compared them with the Windows
editor's own IL2CPP player: the managed code is the same apart from attribute order, a few inlining hints and one
equivalent field initializer, and 67 of the 68 files are byte-identical to the Windows editor's Mono player (the 68th,
`UnityEngine.dll`, has the same type forwards and module id in another order). That's a closer match than the Android
libraries MelonLoader uses.

Download URLs follow the tag:

```
https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download/<unity version>/manifest.json
https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download/<unity version>/Managed-Windows.zip
```

### manifest.json

```json
{
  "format": 1,
  "unityVersion": "2022.3.62f2",
  "revision": "7670c08855a9",
  "packages": {
    "Managed-Windows.zip": {
      "definition": "3f1a9c0e7b2d",
      "state": "built",
      "module": "windows-mono",
      "moduleUrl": "https://download.unity3d.com/download_unity/7670c08855a9/MacEditorTargetInstaller/UnitySetup-Windows-Mono-Support-for-Editor-2022.3.62f2.pkg",
      "moduleMd5": "…",
      "source": "Variations/win64_player_nondevelopment_mono/Data/Managed",
      "size": 2476525,
      "sha256": "…",
      "files": [ { "name": "UnityEngine.CoreModule.dll", "size": 1393576, "sha256": "…" } ],
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
  "asset": "Managed-Linux.zip",
  "module": "linux-il2cpp",
  "sources": [
    "Variations/linux64_player_nondevelopment_il2cpp/Data/Managed",
    "Variations/linux64_withgfx_nondevelopment_il2cpp/Data/Managed",
    "Variations/il2cpp/Managed"
  ],
  "files": [ "*.dll" ]
}
```

- `module` is the Unity module the files come from, by the id Unity's release API uses for the macOS editor's modules
  (`windows-mono`, `linux-il2cpp`, `linux-mono`, `android`, ...).
- `sources` are folders in the module's package, tried in order: Unity renames them between versions
  (`win64_player_nondevelopment_mono` was `win64_nondevelopment_mono` in 2019.4), and from 6000.6 keeps the libraries
  only in the shared `Variations/mono/Managed` and `Variations/il2cpp/Managed` folders, whose code is the player's.
- `files` picks files in the source folder: `*` and `?` within a name, `**` across folders.
- `minimumVersion` (optional) starts a package at a later Unity version than the rest.

**Adding a package, or changing one, updates the existing releases**: each package's definition has a hash, recorded in
the release, and the next run builds the package in every release that doesn't have it, or has it from another
definition, without touching the other packages. A package whose files aren't in a version's module is recorded as
unavailable, with the player variations the module does have, and isn't retried until its definition changes.

## How it runs

[`.github/workflows/sync.yml`](.github/workflows/sync.yml) runs every 6 hours, when `packages.json` or the packager
changes, and by hand, all on Ubuntu runners:

1. **Plan**: asks Unity's release API (the one Unity Hub uses) for every release, compares with this repository's
   releases, and picks up to 40 versions to build, newest first.
2. **Build and publish**, a runner per version: downloads each needed module's macOS package (`.pkg`) from Unity's
   servers, checks it against the MD5 Unity publishes, reads the files out of it, zips each package and creates the
   release, or updates the packages that changed in an existing one.

A `.pkg` is a xar archive whose `Payload` is a gzip-compressed cpio archive; the packager reads that itself
([`MacPackage.cs`](Packager/MacPackage.cs)), streaming the package once and writing only the folders the packages use.
Nothing Unity ships is run.

To build or rebuild chosen versions, run the workflow by hand with their versions (and "rebuild" to replace what's
there).

## Running it locally

The packager is a .NET 10 console app and runs on any OS:

```sh
dotnet run --project Packager -- plan --limit 5
dotnet run --project Packager -- build --version 2022.3.62f2
dotnet run --project Packager.Tests
```

`build` downloads the modules (a few hundred MB per version) into `work/` and writes the release's files to
`out/<version>/`. `publish` needs the GitHub CLI and a token (`GH_TOKEN`).

## Unity

Unity's libraries are part of the Unity software. Their use is subject to the
[Unity Terms of Service](https://unity.com/legal/terms-of-service) and the
[Unity Software Additional Terms](https://unity.com/legal/terms-of-service/software).

This repository is not sponsored by, affiliated with or endorsed by Unity Technologies or its affiliates. "Unity" is a
trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

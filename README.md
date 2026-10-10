# Lustral Unity Libraries

Unity's managed runtime libraries, published as one release per Unity version, for use by
[Lustral](https://github.com/LustralTeam/Lustral) during interop generation. IL2CPP builds include only the parts of
Unity's API that the game uses. Lustral's generator restores the remaining API from these libraries ("unstripping"),
so that mods can use Unity's complete API on Windows and Linux IL2CPP games.

Questions about Lustral can be asked on the [Lustral Discord server](https://discord.gg/3Y6Ete73E3).

Releases are built automatically by GitHub Actions from Unity's module packages, following the same approach as
[MelonLoader.UnityDependencies](https://github.com/LavaGang/MelonLoader.UnityDependencies).

## Release Contents

Each release is tagged with the exact Unity version, for example `6000.0.58f2`, and contains the following assets:

| Asset | Contents |
| --- | --- |
| `Managed-Windows.zip` | Unity's managed libraries (`UnityEngine.*.dll` and related assemblies) from the Windows x64 player. |
| `Managed-Linux.zip` | Unity's managed libraries from the Linux x64 IL2CPP player. |
| `manifest.json` | The contents and origin of each package, with SHA-256 hashes. |

The libraries are stored at the root of each zip file. If Unity does not provide a package's module for a version (the
Linux IL2CPP module is unavailable for some older versions), the package is omitted from that release and
`manifest.json` states the reason.

### Source of the Windows Libraries

Unity does not publish a Windows IL2CPP module for macOS, so the Windows libraries are taken from the Windows Mono
module (the `win64_player_nondevelopment_mono` player). For Unity 2022.3.62f2, these libraries were compared with
those of the Windows editor's IL2CPP player: the managed code is equivalent, differing only in attribute order, a few
inlining hints and one equivalent field initializer. In addition, 67 of the 68 files are byte-identical to those of the
Windows editor's Mono player; the remaining file, `UnityEngine.dll`, contains the same type forwards and module ID in a
different order. This is a closer match than the Android libraries used by MelonLoader.

### Download Addresses

Download addresses are derived from the release tag:

```
https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download/<unity version>/manifest.json
https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download/<unity version>/Managed-Windows.zip
```

### Manifest Format

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

Consumers read the manifest, download the required package and verify its `sha256` hash.

## Package Definitions

The packages are defined in [`packages.json`](packages.json). A release is created for every final (`f`) Unity
release from `minimumVersion` onward, and each package in the file is a zip asset of that release:

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

- `module`: the Unity module containing the files, identified by the ID that Unity's release API uses for the macOS
  editor's modules (`windows-mono`, `linux-il2cpp`, `linux-mono`, `android`, ...).
- `sources`: folders in the module's package, tried in order. Unity renames these folders between versions
  (`win64_player_nondevelopment_mono` was named `win64_nondevelopment_mono` up to 2019.4), and from Unity 6000.6 stores the
  libraries only in the shared `Variations/mono/Managed` and `Variations/il2cpp/Managed` folders, which contain the
  player's code.
- `files`: the files to include from the source folder. `*` and `?` match within a name, and `**` matches across
  folders.
- `minimumVersion` (optional): a later starting Unity version for a single package.

Unity versions that cannot be built are listed in `excludedVersions` at the top level of the file, each with the
reason, for example a module package that does not match the hash in Unity's release API:

```json
"excludedVersions": {
  "6000.0.11f1": "A module package downloaded from Unity does not match the MD5 hash in Unity's release API."
}
```

Scheduled runs skip these versions. A manual run that names an excluded version builds it, to check whether the
problem has been resolved; if it has, remove the entry.

**Adding or changing a package updates existing releases.** Each package definition has a hash that is recorded in the
release. The next run builds the package for every release that does not contain it or contains it from a different
definition, without affecting other packages. If a package's files are not present in a version's module, the package
is recorded as unavailable, together with the player variations the module contains, and is not retried until its
definition changes.

## Automated Builds

[`.github/workflows/sync.yml`](.github/workflows/sync.yml) runs every six hours, whenever `packages.json` or the
packager changes, and on manual dispatch. All jobs run on Ubuntu runners:

1. **Plan:** queries Unity's release API (the API used by Unity Hub) for all releases, compares them with this
   repository's releases, and selects up to 40 versions to build, newest first.
2. **Build and publish:** one runner per version downloads each required module's macOS package (`.pkg`) from Unity's
   servers, verifies it against the MD5 hash published by Unity, extracts the files, creates a zip file for each
   package, and creates the release or updates the changed packages of an existing release.

A `.pkg` file is a xar archive whose `Payload` is a gzip-compressed cpio archive. The packager reads this format directly
([`MacPackage.cs`](Packager/MacPackage.cs)), streaming each package once and writing only the folders that the packages
use. No software distributed by Unity is executed.

To build or rebuild specific versions, run the workflow manually with those versions, and select "rebuild" to replace
existing assets.

## Running Locally

The packager is a .NET 10 console application and runs on any operating system:

```sh
dotnet run --project Packager -- plan --limit 5
dotnet run --project Packager -- build --version 2022.3.62f2
dotnet run --project Packager.Tests
```

`build` downloads the modules (several hundred MB per version) to `work/` and writes the release files to
`out/<version>/`. `publish` requires the GitHub CLI and a token (`GH_TOKEN`).

## License

The packager's source code is licensed under the [GNU Lesser General Public License, version 3](COPYING.LESSER), which
supplements the [GNU General Public License, version 3](COPYING). The Unity libraries published in this repository's
releases are not covered by this license; they remain subject to Unity's terms (see below).

## Legal Notice

Unity's libraries are part of the Unity software. Their use is subject to the
[Unity Terms of Service](https://unity.com/legal/terms-of-service) and the
[Unity Software Additional Terms](https://unity.com/legal/terms-of-service/software).

This repository is not sponsored by, affiliated with or endorsed by Unity Technologies or its affiliates. "Unity" is a
trademark or registered trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.

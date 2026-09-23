# Building from source

The repository holds only source code. `build.ps1` compiles it into a release folder, and `package.ps1` packs
that folder into the zips that go on the Releases page.

## Repository layout

| Folder or file | What it is |
|---|---|
| `Patcher/` | The installer: WinForms, .NET Framework 4.8, patches `PlayMaker.dll` with Mono.Cecil |
| `Runtime/` | `PlayMakerTurbo.dll`, the code the patched `PlayMaker.dll` calls into (.NET 3.5) |
| `Addon/` | PlayMaker Turbo Addon, an MSCLoader mod with fixes in the game's own scripts (.NET 3.5) |
| `Profiler/` | FSM Profiler, an MSCLoader mod that measures FSMs, scripts and mods (.NET 3.5) |
| `docs/` | Settings reference, how it works, this page and the player guide |
| `build.ps1` | Builds everything into `dist\` |
| `package.ps1` | Packs `dist\` into the release zips in `release\` |

## What you need

- Windows with Visual Studio 2022 and the .NET desktop development workload, which brings MSBuild and the
  .NET SDK.
- The Unity Full v3.5 reference profile, which the runtime and both mods target. Visual Studio installs it with
  the Game development with Unity workload, the same setup the MSCLoader mod template asks for. Check for the
  folder
  `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v3.5\Profile\Unity Full v3.5`.
- My Winter Car with MSCLoader installed. The projects link against `UnityEngine.dll`, `PlayMaker.dll`,
  `Assembly-CSharp.dll`, `cInput.dll` and `0Harmony.dll` from the game's `Managed` folder, and the two mods
  also against `MSCLoader.dll`; the profiler needs `Assembly-CSharp-firstpass.dll` as well. These are game and
  loader files and are not part of this repository.

## Build

Run the build script from the repository folder:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

It finds the game through Steam. If your game is somewhere else, add `-GamePath "E:\Games\My Winter Car"`. The
finished release lands in `dist\`, and you install it from there like a downloaded zip.

The script reads the game files and never changes them. It goes in this order, because the runtime has to be
compiled against the patched `PlayMaker.dll`:

1. Build the installer (`Patcher/`, with Mono.Cecil from NuGet).
2. Copy the original `PlayMaker.dll` into `obj\playmaker` and patch the copy with
   `"PlayMakerTurbo Installer.exe" <folder> --patch-only`. If Turbo is already installed in the game, the
   original comes from `PlayMaker.dll.orig`.
3. Build the runtime (`Runtime/`) with MSBuild against that patched copy.
4. Build the profiler (`Profiler/`). It does not need the patched `PlayMaker.dll`.
5. Build the addon (`Addon/`).
6. Copy the installer, the runtime, the Mono.Cecil files and the documents into `dist\`, the profiler with its
   README into `dist\Profiler\` and the addon with its README into `dist\Addon\`.

If the environment variable `MWCMODSFOLDER` points at the game's `Mods` folder, the profiler and addon builds
also copy their DLLs there. Keep that in mind when a test needs one of the mods out of the game: move it out
again after every build.

## Package a release

```
powershell -ExecutionPolicy Bypass -File package.ps1 -Version 1.2.0-beta
```

It packs `dist\` into five zips in `release\`:

| Zip | Contents |
|---|---|
| `players` | Turbo installer, the addon and `docs/players.md` as `README.md` |
| `full` | all of `dist\` |
| `turbo` | Turbo installer with `README.md` and `docs\` |
| `addon` | the addon with its README |
| `profiler` | the profiler with its README |

Every zip carries `LICENSE`; the ones with the installer also carry `THIRD-PARTY-NOTICES.md` for Mono.Cecil.

## Versions

The installer's version is `<Version>` in `Patcher/Patcher.csproj`, the runtime's is
`AssemblyInformationalVersion` in `Runtime/Properties/AssemblyInfo.cs`. The runtime's `AssemblyVersion` stays
`1.0.0.0`, because the patched `PlayMaker.dll` references the runtime by that version. The mods keep their
version in `Version` of the mod class and in their `AssemblyInfo.cs`.

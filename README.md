# SDVKit

SDVKit is an agent-friendly Stardew Valley modding toolkit and isolated live test lab.

This repository is a clean greenfield rebuild. The public surface stays deliberately small; features are added through focused GitHub issues and reviewable pull requests.

## Product direction

SDVKit has two equal pillars:

- **Toolkit:** inspect, create, build, test, and package SMAPI mods and content packs.
- **Live lab:** launch Stardew through an isolated SMAPI mod group and keep one controlled singleplayer run active in the background. Later issues may add explicitly selected save copies and focused scenario tests on top of this lifecycle.

The default live path uses SMAPI's native `--mods-path` support. SDVKit does not require Mod Organizer 2 and does not automatically deploy into the normal or mod-manager-owned `Mods` directory.

## Non-goals

- No generic automation or evidence framework.
- No second MCP/runtime stack without a concrete missing capability.
- No broad save parser, save migration engine, multiplayer lab, or crash-recovery system before a real workflow requires it.
- No game binaries, proprietary assets, or personal saves in this repository.

## Build

Requirements: Windows and the .NET 8 SDK selected by `global.json`.

```powershell
dotnet restore SDVKit.sln
dotnet format SDVKit.sln --verify-no-changes --no-restore
dotnet build SDVKit.sln -c Release --no-restore
dotnet test SDVKit.sln -c Release --no-build
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe --help
```

## Isolated singleplayer live lab

Run the lab commands from the project root whose ignored `.sdvkit/` directory should own the generated mod group and runtime state:

```powershell
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab start --topology single --json
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab status --topology single --json
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab stop --topology single --json
```

`start` reuses the single ready Stardew + SMAPI installation detected by `doctor`. It builds the minimal SDVKit AlwaysOn mod against those local assemblies, installs only that mod below `.sdvkit/lab/single/mods`, and launches the detected `StardewModdingAPI.exe` directly with the absolute native `--mods-path` argument. The child process writes stdout and stderr directly to project-owned files below `.sdvkit/lab/single/runtime`; those handles do not keep the JSON command open.

The ownership record contains the exact PID, UTC process start time, and executable identity. `status` rechecks that identity and the matching game-side AlwaysOn marker. `stop` first rechecks the same process, then publishes that launch's single-purpose stop request below `.sdvkit`. AlwaysOn handles it on the game thread, restores and reads back the captured option, writes `exiting` only after confirmation, and asks the game to exit normally; the CLI waits on the exact process handle and clears ownership only after both confirmations. The normal stop path has no process-name search, UI automation, or kill fallback: an identity mismatch, unconfirmed restoration, or clean-stop timeout is reported and the process record is retained. If Windows cannot establish identity for a freshly created child at all, only that child is aborted through its original `CreateProcess` handle before `start` returns.

AlwaysOn transiently sets Stardew's `pauseWhenOutOfFocus` option to `false`, reasserts it while the controlled process runs, and restores the captured value during the normal game-exit event before Stardew persists options. A process crash or forced external termination cannot promise that restoration.

The native mod path isolates which SMAPI mods are loaded; it does **not** isolate Stardew's shared AppData preferences, saves, startup preferences, or standard SMAPI logs. This workflow does not enumerate, open, copy, select, or modify any save, and it never writes to the normal or mod-manager-owned `Mods` directory.

## Read-only foundation commands

```powershell
sdvkit doctor --json
sdvkit project inspect [path] --json
```

`doctor` checks the supported Windows custom-targets, Steam (including additional libraries), GOG, and Xbox locations. An installation is ready only when the Stardew Valley and SMAPI executables and assemblies are present together. Its versioned JSON reports `ready`, `ambiguous`, or `notFound` and lists only complete installations.

`project inspect` reads the selected directory, or the current directory when no path is given. It classifies manifests as `smapiMod` through `EntryDll`, or `contentPack` through `ContentPackFor.UniqueID`; a tree containing separate manifests of both kinds is `hybrid`. Required identity fields and the classification fields are checked, but this is not a replacement for SMAPI's complete manifest schema validation. Project and manifest paths within the result are relative and sorted. `bin`, `obj`, `.git`, and `.sdvkit` directories are ignored, and child directory links are not followed.

Both commands are read-only. They do not create `.sdvkit`, inspect saves or a normal `Mods` directory, deploy files, or launch the game. Exit code `0` means a ready or recognized result, `2` is a CLI usage error, and `3` is a controlled discovery or inspection outcome such as not found, ambiguous, or invalid.

## Minimal toolkit workflow

Create a buildable SMAPI C# mod or a minimal Content Patcher pack:

```powershell
sdvkit project create smapi-mod .\ExampleMod --name "Example Mod" --author Nana --unique-id Nana.ExampleMod --description "A minimal SMAPI mod." --json
sdvkit project create content-pack .\ExamplePack --name "Example Pack" --author Nana --unique-id Nana.ExamplePack --description "A minimal Content Patcher pack." --json
```

The SMAPI project contains only `.gitignore`, `<mod>.csproj`, `ModEntry.cs`, and `manifest.json`. It targets .NET 6 and references `Pathoschild.Stardew.ModBuildConfig` 4.4.0. The content pack contains only `.gitignore`, `manifest.json`, and a no-op `content.json` using Content Patcher format 2.9.0. Creation accepts a missing or empty destination and never overwrites existing content.

Build and package an inspected project:

```powershell
sdvkit project build .\ExampleMod --json
sdvkit project package .\ExampleMod --json
sdvkit project package .\ExamplePack --json
```

`project build` requires exactly one classified SMAPI code manifest, one C# project, and one ready Stardew Valley + SMAPI installation from `doctor`. It runs the normal `dotnet build` and official ModBuildConfig path in `Release`, while forcing `EnableModDeploy=false` and keeping build output and logs below the project's ignored `.sdvkit/` directory.

For C# mods, `project package` lets ModBuildConfig select the declared release output. For Content Patcher packs, it archives the selected manifest root while excluding source projects, build and repository state, game binaries, executables, and XNB files; a save marker or `Saves` directory rejects the package instead of producing a partial archive. Release ZIPs are written below `.sdvkit/packages` and validated to contain one relative top-level mod directory without traversal paths. Neither command writes to a normal or mod-manager-owned `Mods` directory, reads save contents, or launches the game.

All toolkit JSON uses relative paths for project-owned files and archives. Exit code `0` means success, `2` is a CLI usage error, and `3` is a controlled create, build, or package outcome; build diagnostics are kept in the reported `.sdvkit/logs` file.

## First milestones

1. Environment discovery and project inspection.
2. Minimal SMAPI mod/content-pack creation, build, and release packaging.
3. Isolated SMAPI launch through `--mods-path`.
4. A small always-on game bridge for controlled background runs.
5. One disposable test-save workflow and focused scenario smoke tests.

The issue tracker is the roadmap. A capability is added only when it serves a current workflow and can reuse no smaller existing path.

## Inspiration

The product shape is inspired by [skyrimvr-claude-toolkit](https://github.com/WingedGuardian/skyrimvr-claude-toolkit). The live-lab design also studies [StardewValley-MCP](https://github.com/luy-0/StardewValley-MCP) and the bootstrap approach of [stardew-valley-ai-modkit](https://github.com/liminalwarmth/stardew-valley-ai-modkit). No source from those projects is included in this initial scaffold.

## License

[MIT](LICENSE)

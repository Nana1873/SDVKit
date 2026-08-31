# SDVKit

SDVKit is an agent-friendly Stardew Valley modding toolkit and isolated live test lab.

This repository is a clean greenfield rebuild. The public surface stays deliberately small; features are added through focused GitHub issues and reviewable pull requests.

## Product direction

SDVKit has two equal pillars:

- **Toolkit:** inspect, create, build, test, and package SMAPI mods and content packs.
- **Live lab:** launch Stardew through isolated SMAPI mod groups, keep controlled runs active in the background, exercise one SDVKit-owned disposable test world, and prove one exact local host-plus-farmhand smoke without touching personal saves.

The default live path uses SMAPI's native `--mods-path` support. SDVKit does not require Mod Organizer 2 and does not automatically deploy into the normal or mod-manager-owned `Mods` directory.

## Non-goals

- No generic automation or evidence framework.
- No second MCP/runtime stack without a concrete missing capability.
- No broad save parser, save migration engine, generic multiplayer lab, or crash-recovery system before a real workflow requires it.
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

## Portable Windows-x64

Install the .NET 8 SDK, then download `SDVKit-0.1.0-win-x64.zip` and its `.sha256` file from the GitHub release assets. Extract and start the CLI without a repository checkout:

```powershell
Get-FileHash .\SDVKit-0.1.0-win-x64.zip -Algorithm SHA256
Expand-Archive .\SDVKit-0.1.0-win-x64.zip
& .\SDVKit-0.1.0-win-x64\sdvkit.exe --help
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

AlwaysOn transiently sets Stardew's `pauseWhenOutOfFocus` option to `false`, reasserts it while the controlled process runs, and restores plus reads back the captured value when the owned stop request takes the normal game-exit path. A manual window close, process crash, or forced external termination cannot promise that restoration.

Alongside the native mod path, each controlled lab role receives its own persistent Windows user-profile root below `.sdvkit/lab/profiles/`. Stardew therefore resolves preferences, saves, startup preferences, screenshots, and standard SMAPI logs below `.sdvkit/` too. AlwaysOn verifies the exact game-side Stardew data path before it activates. The ordinary `start`/`status`/`stop` lifecycle does not enumerate, open, copy, select, or modify any personal save, and it never writes to the normal or mod-manager-owned `Mods` directory. This is process-level data isolation, not a Windows sandbox: tested mods and external services such as Steam can still access shared machine resources.

Before updating a v0.1.0 checkout, cleanly stop any retained live-lab run with that version. This layout change intentionally does not migrate an active v0.1.0 fixture junction from the normal Stardew `Saves` directory.

### Disposable test world

Run the focused test-world workflow only while the regular lab is stopped:

```powershell
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab test-save --topology single --json
```

The first invocation registers one fixed SDVKit fixture below `.sdvkit/lab/single/test-save`, exposes only its exact save ID inside the single lab's project-owned `Saves` directory, and creates the world through Stardew's normal new-game and initial-save flow. After a confirmed clean stop, SDVKit removes and verifies removal of that exact junction, captures a byte-for-byte baseline, restores a work copy, and runs the same controlled `start`/`status`/`stop` lifecycle again. Later invocations begin from that baseline and need only the scenario run.

The scenario loads the exact fixture directly, verifies its save ID, unique game ID, player, farm, favorite thing, ownership markers, main-player status, and singleplayer status before any scenario mutation, shows one `SDVKit test-save smoke` HUD message, and observes 120 real game-update ticks. Completion requires the terminal game-side marker, normal AlwaysOn option restoration, exact process exit, junction removal, and another baseline reset. Its manifest, baseline, work tree, archived stdout/stderr/status/scenario logs, and any temporary reset data stay below `.sdvkit/`; it creates no separate lifecycle or generic scenario protocol.

The command never lists the normal `Saves` directory and never opens, copies, replaces, or deletes a personal save. A pre-existing entry at the exact generated fixture name inside SDVKit's project-owned data root blocks the workflow without touching that entry. An identity mismatch or unconfirmed cleanup blocks further fixture mutation; when the exact SDVKit junction can still be proven, cleanup removes it first. The test-save automation currently fails closed unless it finds the explicitly checked Stardew 1.6.15 (`1.6.15.24356`) and SMAPI 4.5.2 runtime contract.

### Local two-player smoke

Run the fixed multiplayer slice only while the regular lab is stopped, after the disposable test-world baseline above exists and `doctor` reports exactly one ready Stardew + SMAPI installation:

```powershell
sdvkit lab smoke --topology network-2 --json
```

The command reuses that baseline and the existing process lifecycle to start exactly one local host and one local farmhand, both minimized without foreground activation and with separate project-owned Stardew data roots. It builds the declared AlwaysOn mod once, verifies the same build ID on both sides before joining, performs a real host/join, and requires matching host and farmhand identities in the joined game. Each side must keep that exact pair connected for 120 consecutive game ticks while AlwaysOn is active, `pauseWhenOutOfFocus` is `false`, and Windows reports a different foreground-process identity. The result records each process's exact identity, foreground observation, game-side state, build ID, and separate logs below `.sdvkit/`.

On a normal completion, the farmhand and then the host exit through the existing clean-stop path, the previously relevant options on both sides are restored and confirmed, and the disposable fixture is reset byte-for-byte from its baseline. The command fails closed when joining, identity matching, background progress, option restoration, process exit, junction cleanup, or reset cannot be confirmed.

This is deliberately one local `network-2` smoke, not an N-player, remote-fleet, matchmaking, topology, or general multiplayer system. It never uses personal saves or the normal or mod-manager-owned `Mods` directory.

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

## End-to-end project smoke

Build, package, and load one current standalone SMAPI C# mod in the existing isolated live lab:

```powershell
sdvkit project smoke .\ExampleMod --topology single --json
sdvkit project smoke .\ExampleMod --topology network-2 --json
```

The optional project path selects the mod source; it defaults to the current directory. The live lab remains rooted at the command's current directory, so an explicitly selected project can reuse that lab's SDVKit-owned disposable world without copying a save or creating another lifecycle. Project build/package output stays below the selected source's `.sdvkit`; live staging, logs, fixture state, and reports stay below the lab root's `.sdvkit`.

V1 accepts exactly one standalone code-mod manifest paired with exactly one C# project. It calls the existing inspector and isolated Release builder, then stages only the release ZIP already produced and validated by `ProjectPackager`. Content packs, hybrid trees, ambiguous projects, the reserved `SDVKit.AlwaysOn` identity, and missing required runtime dependencies are controlled exit-`3` outcomes. Optional dependencies may be absent; SDVKit never downloads or installs Content Patcher or another mod automatically.

The package is revalidated before extraction. Its package SHA-256, declared manifest version, canonical SMAPI version, and complete staged file-set identity are recorded, and the package is extracted only within the selected isolated mod group as a sibling of AlwaysOn under `.sdvkit/lab/single/mods` or `.sdvkit/lab/network-2/{host,farmhand}/mods`. In JSON, `declaredVersion` preserves the package-manifest text while `version` is SMAPI's canonical semantic form (for example, `1.0` becomes `1.0.0`). Network-2 copies the same prepared package file set to both roles and requires matching identities. An unowned directory, another mod, drifted ownership state, or any retained earlier project-smoke staging blocks the next run. Even an exactly matching ownership marker is not auto-replaced, because a later invocation cannot prove that every child from an earlier failed launch stopped; normal confirmed cleanup removes it in the originating run.

AlwaysOn uses SMAPI's loaded-mod registry after `GameLaunched` to confirm the exact target `UniqueID` and manifest version on the single process or on both host and farmhand. The command then reuses the existing disposable-fixture load, 120-tick single or joined-pair smoke, exact-process clean stop, option restoration, junction cleanup, and byte-for-byte baseline reset. On a confirmed normal end, it removes only its own target staging; uncertain process ownership retains that staging and reports a blocked cleanup instead of mutating a possibly active mod group.

The resulting evidence means that a controlled package file set was staged and SMAPI reported the expected mod identity/version while the bounded smoke passed. The build identity is echoed through the launch-bound game-side marker; it is **not** a hash measured from a DLL in memory. A passed smoke also does not prove that every feature of the target mod is functionally correct. Target-related load failures are selected from the project-local captured SMAPI stdout/stderr logs and returned with those limits stated in JSON.

This command never deploys to the normal or mod-manager-owned `Mods` directory, enumerates personal saves, creates a permanent deployment, performs hot reload, or introduces another process/save/multiplayer state machine.

## Agent workflow

The repository-owned [`sdv-project-smoke`](.agents/skills/sdv-project-smoke/SKILL.md) skill guides agents through discovery, project inspection, the existing end-to-end smoke, and evidence-based JSON/log reporting. It uses `single` by default and `network-2` only when a user explicitly requests a multiplayer test.

## First milestones

1. Environment discovery and project inspection.
2. Minimal SMAPI mod/content-pack creation, build, and release packaging.
3. Isolated SMAPI launch through `--mods-path`.
4. A small always-on game bridge for controlled background runs.
5. One disposable test-save workflow and focused scenario smoke tests.
6. One exact local host-plus-farmhand multiplayer smoke.
7. One packaged SMAPI project loaded and smoke-tested end to end in either existing topology.

The issue tracker is the roadmap. A capability is added only when it serves a current workflow and can reuse no smaller existing path.

## Inspiration

The product shape is inspired by [skyrimvr-claude-toolkit](https://github.com/WingedGuardian/skyrimvr-claude-toolkit). The live-lab design also studies [StardewValley-MCP](https://github.com/luy-0/StardewValley-MCP) and the bootstrap approach of [stardew-valley-ai-modkit](https://github.com/liminalwarmth/stardew-valley-ai-modkit). No source from those projects is included in this initial scaffold.

## License

[MIT](LICENSE)

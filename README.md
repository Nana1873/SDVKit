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

Install the .NET 8 SDK, then download `SDVKit-0.7.0-win-x64.zip` and `SDVKit-0.7.0-win-x64.zip.sha256` from the GitHub release assets. Extract and start the CLI without a repository checkout:

```powershell
Get-FileHash .\SDVKit-0.7.0-win-x64.zip -Algorithm SHA256
Expand-Archive .\SDVKit-0.7.0-win-x64.zip
& .\SDVKit-0.7.0-win-x64\sdvkit.exe --help
```

Before replacing the program files, cleanly stop any active SDVKit lab or project review.

## What's new in v0.7.0

Since `v0.6.1`, exact owned reviews add bounded CLI-only introspection for canonical maps, final post-pipeline textures and diagnostic previews, audio metadata without playback, and observed conventional `Mods/<owner>/...` asset requests. These adapters do not expose bulk asset extraction and are not native MCP tools.

The native local-STDIO MCP server now binds to exact `single`, `host`, or `farmhand` roles and adds review and loaded-mod diagnostics, canonical Data reads for `single`, role-local map or viewport screenshots, and explicit opt-ins for process-local input and typed owned-fixture actions. Action tools remain absent by default; farmhands are limited to role-local status and navigation and cannot create buildings or animals or save; and no network listener, generic console, or arbitrary RPC surface is introduced. Owned review boundaries and default windowed profile preparation were tightened to support these workflows without selecting normal saves or the normal or mod-manager-owned `Mods` directory.

## Isolated singleplayer live lab

Run the lab commands from the project root whose ignored `.sdvkit/` directory should own the generated mod group and runtime state:

```powershell
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab start --topology single --json
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab status --topology single --json
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab stop --topology single --json
```

`start` reuses the single ready Stardew + SMAPI installation detected by `doctor`. It builds the minimal SDVKit AlwaysOn mod against those local assemblies, installs only that mod below `.sdvkit/lab/single/mods`, and launches the detected `StardewModdingAPI.exe` directly with the absolute native `--mods-path` argument. The child process writes stdout and stderr directly to project-owned files below `.sdvkit/lab/single/runtime`; those handles do not keep the JSON command open.

The ownership record contains the exact PID, UTC process start time, and executable identity. `status` rechecks that identity and the matching game-side AlwaysOn marker. `stop` first rechecks the same process, then publishes that launch's single-purpose stop request below `.sdvkit`. AlwaysOn handles it on the game thread, attempts to restore and read back the captured option, writes either `exiting` or the diagnostic `restoreFailed` terminal marker, and asks the game to exit normally in both cases. The CLI waits on the exact process handle and clears ownership only after exact exit and that launch-bound terminal marker; an unconfirmed option restore remains visible as a warning. The normal stop path has no process-name search, UI automation, or kill fallback: an identity mismatch, missing terminal marker, or clean-stop timeout is reported and the process record is retained. If Windows cannot establish identity for a freshly created child at all, only that child is aborted through its original `CreateProcess` handle before `start` returns.

AlwaysOn transiently sets Stardew's `pauseWhenOutOfFocus` option to `false`, reasserts it while the controlled process runs, and attempts to restore plus read back the captured value when the owned stop request takes the normal game-exit path. That option belongs to the isolated `.sdvkit` profile: a failed readback is diagnosed but does not hold the exact process open, and the next start applies the lab value deterministically again. A manual window close, process crash, or forced external termination cannot promise restoration.

Alongside the native mod path, the controlled `single`, `host`, and `farmhand` roles receive separate persistent Windows user-profile roots at `.sdvkit/lab/profiles/single`, `.sdvkit/lab/profiles/network-2/host`, and `.sdvkit/lab/profiles/network-2/farmhand`. Stardew and SMAPI AppData is redirected per controlled process, so preferences, saves, startup preferences, screenshots, and standard SMAPI logs resolve below `.sdvkit/` too. AlwaysOn verifies the exact game-side Stardew data path before it activates. The ordinary `start`/`status`/`stop` lifecycle does not enumerate, open, copy, select, or modify any personal save, and it never writes to the normal or mod-manager-owned `Mods` directory. This is process-level data isolation, not a Windows sandbox: tested mods and external services such as Steam can still access shared machine resources. Game binaries and content continue to be read from the detected real installation; SDVKit does not create a copied game folder.

The outer boundary remains fail-closed: normal saves and `Mods` directories, the real game installation, exact process and topology-role identity, canonical owned paths, and reparse-point checks are not relaxed. Inside the exact disposable staging and isolated profiles below `.sdvkit/`, routine mod output and option-restoration results are treated as owned lab state and evidence instead of as authority to select anything outside that boundary.

Before updating a v0.1.0 checkout, cleanly stop any retained live-lab run with that version. This layout change intentionally does not migrate an active v0.1.0 fixture junction from the normal Stardew `Saves` directory.

### Disposable test world

Run the focused test-world workflow only while the regular lab is stopped:

```powershell
& .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe lab test-save --topology single --json
```

The first invocation registers one fixed SDVKit fixture below `.sdvkit/lab/single/test-save`, exposes only its exact save ID inside the single lab's project-owned `Saves` directory, and creates the world through Stardew's normal new-game and initial-save flow. After a confirmed clean stop, SDVKit removes and verifies removal of that exact junction, captures a byte-for-byte baseline, restores a work copy, and runs the same controlled `start`/`status`/`stop` lifecycle again. Later invocations begin from that baseline and need only the scenario run.

The scenario loads the exact fixture directly, verifies its save ID, unique game ID, player, farm, favorite thing, ownership markers, main-player status, and singleplayer status before any scenario mutation, shows one `SDVKit test-save smoke` HUD message, and observes 120 real game-update ticks. Completion requires the terminal game-side marker, exact process exit, junction removal, and another baseline reset. AlwaysOn attempts option restoration before exit and reports an unconfirmed readback as a warning rather than failing the otherwise safe stop. The fixture manifest, baseline, work tree, archived stdout/stderr/status/scenario logs, and any temporary reset data stay below `.sdvkit/`; it creates no separate lifecycle or generic scenario protocol.

The command never lists the normal `Saves` directory and never opens, copies, replaces, or deletes a personal save. A pre-existing entry at the exact generated fixture name inside SDVKit's project-owned data root blocks the workflow without touching that entry. An identity mismatch or unconfirmed cleanup blocks further fixture mutation; when the exact SDVKit junction can still be proven, cleanup removes it first. Test-save automation accepts Stardew game versions `>=1.6.15` and `<1.7`, Stardew file versions `>=1.6.15.24356` and `<1.7`, and SMAPI versions `>=4.5.0` and `<5.0`. The version bands do not replace its existing required runtime capability and reflected-signature probes; missing or changed APIs still fail closed before automation proceeds.

### Local two-player smoke

Run the fixed multiplayer slice only while the regular lab is stopped, after the disposable test-world baseline above exists and `doctor` reports exactly one ready Stardew + SMAPI installation:

```powershell
sdvkit lab smoke --topology network-2 --json
```

The command reuses that baseline and the existing process lifecycle to start exactly one local host and one local farmhand, both minimized without foreground activation and with separate project-owned Stardew data roots. It builds the declared AlwaysOn mod once, verifies the same build ID on both sides before joining, performs a real host/join, and requires matching host and farmhand identities in the joined game. Each side must keep that exact pair connected for 120 consecutive game ticks while AlwaysOn is active, `pauseWhenOutOfFocus` is `false`, and Windows reports a different foreground-process identity. The result records each process's exact identity, foreground observation, game-side state, build ID, and separate logs below `.sdvkit/`.

On a normal completion, the farmhand and then the host exit through the existing clean-stop path, their isolated-profile options are restored and read back when possible, and the disposable fixture is reset byte-for-byte from its baseline. An unconfirmed option restore is retained as a warning and the next start reapplies the lab values; the command still fails closed when joining, identity matching, background progress, exact process exit, junction cleanup, or reset cannot be confirmed.

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

The package is revalidated before extraction. Its package SHA-256, declared manifest version, canonical SMAPI version, and complete staged file-set identity are recorded, and the package is extracted only within the selected isolated mod group as a sibling of AlwaysOn under `.sdvkit/lab/single/mods` or `.sdvkit/lab/network-2/{host,farmhand}/mods`. In JSON, `declaredVersion` preserves the package-manifest text while `version` is SMAPI's canonical semantic form (for example, `1.0` becomes `1.0.0`). Network-2 copies the same prepared package file set to both roles and requires matching identities. Before a run and for status or evidence, one newly created regular root `config.json` remains the only accepted code-mod file-set difference; every other changed, added, or missing regular file remains visible as build drift and can block a new run or an unqualified identity claim. After the exact owned process is confirmed stopped, that regular runtime-content drift no longer blocks cleanup: removal is authorized by the ownership record, exact canonical direct-child staging paths, and a reparse-free tree, not by current content identity. An unowned directory, another mod, ownership or path drift, a reparse point, an unreadable process, or an uncertain exit still blocks mutation. Even an exactly matching ownership marker is not auto-replaced, because a later invocation cannot prove that every child from an earlier failed launch stopped; normal confirmed cleanup removes it in the originating run.

AlwaysOn uses SMAPI's loaded-mod registry after `GameLaunched` to confirm the exact target `UniqueID` and manifest version on the single process or on both host and farmhand. The command then reuses the existing disposable-fixture load, 120-tick single or joined-pair smoke, exact-process clean stop, best-effort isolated-profile option restoration, junction cleanup, and byte-for-byte baseline reset. On a confirmed normal end, it removes only the target staging selected by its exact ownership record; uncertain process ownership or an unsafe owned path retains that staging and reports a blocked cleanup instead of mutating a possibly active or foreign mod group.

The resulting evidence means that a controlled package file set was staged and SMAPI reported the expected mod identity/version while the bounded smoke passed. The build identity is echoed through the launch-bound game-side marker; it is **not** a hash measured from a DLL in memory. A passed smoke also does not prove that every feature of the target mod is functionally correct. Target-related load failures are selected from the project-local captured SMAPI stdout/stderr logs and returned with those limits stated in JSON.

This command never deploys to the normal or mod-manager-owned `Mods` directory, enumerates personal saves, creates a permanent deployment, performs hot reload, or introduces another process/save/multiplayer state machine.

## Interactive project review

### Default singleplayer review

Run one target C# mod project or one ready root SMAPI content pack with only the local test companions and additional content packs named on the command line. Omitting `--topology` selects `single`:

```powershell
sdvkit project review start .\ExampleMod `
  --companion C:\local-mods\ReadyCompanion `
  --content-pack .\tests\fixtures\ExamplePack `
  --json
sdvkit project review status --json
sdvkit project review stop --json
```

For an inverted content-pack review, select the pack itself as the target and its provider explicitly as a companion:

```powershell
sdvkit project review start .\ExamplePack `
  --topology single `
  --companion .\ExampleProvider `
  --json
```

Run all review commands from the same lab-owning directory. The optional target path defaults to that current directory. `start` requires exactly one standalone SMAPI C# target project or one ready root content-pack directory. A content-pack target supports only `single`, and its `ContentPackFor.UniqueID` provider must be an explicitly selected local `--companion`; `network-2` rejects that target before launch or lab mutation. Every repeatable `--companion` value must explicitly name either another single code-mod project or a ready root mod directory, and every `--content-pack` value still explicitly names one additional ready root content-pack directory. SDVKit does not scan a `Mods` directory, search for dependencies, or download anything.

`command` clears an unsubmitted edited line without executing it, then writes exactly one complete, quoted SMAPI, target, companion, or AlwaysOn console line to the currently active exact review process; it does not discover or download anything. Pending raw input observed before dispatch fails closed. Concurrent manual console input is unsupported because it can race dispatch, so don't type in the interactive console while dispatching a command. A successful JSON result sets `commandWritten` to `true` only after the clear sequence and complete line were added to the Windows console input buffer; it does not confirm that SMAPI recognized or successfully executed the command.

### Read canonical Data definitions

While an exact `single` review is running and its target identity is load-confirmed, the `data` subcommands provide bounded read-only access to every canonical structured `Data/*` definition asset shipped by the installed Stardew version. The game-side reader discovers the installed `Content/Data` asset names independently instead of relying on a maintained allowlist, then loads each asset through SMAPI's live game-content pipeline. The returned values therefore include the content packs and edits active in that exact review process; localized physical siblings aren't treated as separate canonical identities.

```powershell
sdvkit project review data assets --offset 0 --limit 100 --topology single --json
sdvkit project review data keys "Data/Buildings" --offset 0 --limit 50 --topology single --json
sdvkit project review data get "Data/Buildings" "Barn" --topology single --json
```

`assets` reports the running game and file versions, each canonical asset name, loaded .NET data type, shape, and key kind. Its coverage object is complete only when every discovered asset is classified and safely queryable, with zero `unknown`, `unclassified`, and `unsupported` entries. Dictionaries use their canonical string or integer keys, lists use canonical zero-based indexes, and a singleton has the one explicit key `singleton`. `keys` returns those identities in deterministic order and never substitutes localized display values. `get` returns exactly one record with its canonical asset and key; object members are sorted deterministically in the JSON while source array order is retained.

Asset and string-key lookup accepts case differences plus small space, hyphen, underscore, slash, and backslash separator differences. Normalization collisions are ambiguous and fail closed; an exact canonical key remains selectable. A missing canonical `Data/*` name is reported as unavailable in the running game version, separately from a name outside that namespace. Load failures, unsupported key types, unsafe or oversized records, reparse points, stale responses, mismatched requests, and unknown or colliding identities are returned as bounded problems rather than guessed results.

Pages default to 50 entries and accept 1-100; offsets are non-negative. Asset and key tokens, one record, and the complete response are size-bounded. There is no unbounded full-dump default, no mutation operation, no network-topology variant, and no new public RPC or generic reflection surface. Each request reuses the existing exact review lock, process, staging, target-load, console-input, and cleanup checks.

### Inspect active map structure

The `map` subcommands inspect the canonical map assets visible through SMAPI's active content pipeline in the same exact, load-confirmed `single` review. They do not read through the normal `Mods` directory, export source XNBs, return a tile matrix, or mutate a map.

```powershell
sdvkit project review map assets --offset 0 --limit 100 --topology single --json
sdvkit project review map get "Maps/Town" --topology single --json
sdvkit project review map layers "Maps/Town" --offset 0 --limit 50 --topology single --json
sdvkit project review map layer "Maps/Town" "Buildings" --topology single --json
sdvkit project review map tilesheets "Maps/Town" --offset 0 --limit 50 --topology single --json
sdvkit project review map warps "Maps/Town" --offset 0 --limit 50 --topology single --json
sdvkit project review map tile "Maps/Town" "Back" 10 12 --topology single --json
sdvkit project review map property "Maps/Town" map "Outdoors" --topology single --json
```

Run `sdvkit project review map --help` for the exact layer, direct-tile, and tile-index property forms. Supply only property names and coordinates known to exist in the selected map; SDVKit deliberately does not guess them, and an absent selection returns a machine-checkable `blocked` result. If a map, layer, or property operand starts with `-` or has the same spelling as an option, put every CLI option before the `--` end-of-options marker. Every following token is then treated as an operand.

`assets` independently scans the installed `Content/Maps` XNB candidates without following reparse points, excludes locale siblings, and classifies each pipeline result as a supported xTile map, a known non-map candidate, or an explicit gap. Unsafe physical candidate names are represented only by deterministic `invalid-map-asset-NNNN` labels and are never echoed into the report. This inventory describes physical game candidates only; an exact canonical `Maps/*` name introduced by a loaded mod can still be inspected through SMAPI's active content pipeline. Require `coverage.complete=true` before claiming complete physical-inventory support. A canonical name unavailable through that pipeline is reported separately from a name outside the namespace; load failures, normalized identity collisions, malformed warps, unsafe shapes, and oversized structures fail closed.

List operations are paged from stable collection order, while `get`, `layer`, `tile`, and `property` return one bounded selection. Warp entries distinguish general `Warp` routes (`playerAndNpc`) from NPC-only `NPCWarp` routes (`npc`). Tile output identifies an empty, static, or animated tile and returns stable frame references and property counts, never the layer's tile matrix. Exact property reads preserve their JSON type and require an explicit map, layer, direct-tile, or tile-index scope. An animated tile-index property additionally requires its stable zero-based `--frame`; direct and tile-index properties are never merged. Map access is intentionally CLI-only here and is not added to MCP.

### Inspect canonical textures safely

During that same exact, target-load-confirmed `single` review, the `texture` subcommands measure the non-localized `Content/**/*.xnb` population. `assets` classifies the physical XNB root TypeReader without loading its object graph: an exact built-in `Texture2DReader` is a texture, a narrow set of built-in Stardew data, collection, map, font, and effect readers are known non-textures, and every malformed, custom, or unknown reader is a gap. It pages only the identities classified as textures while its coverage object records all candidates, textures, non-textures, and classification gaps.

```powershell
sdvkit project review texture assets --offset 0 --limit 100 --topology single --json
sdvkit project review texture get "LooseSprites/Cursors" --topology single --json
sdvkit project review texture preview "LooseSprites/Cursors" --topology single --json
```

The physical inventory bounds total traversed entries as well as candidate count, never follows reparse points, and uses SMAPI's parsed locale identity to exclude localized siblings without guessing from a filename regex. Classification reads at most the first 32 KiB output frame of each XNB through the already-loaded MonoGame decoder, with a 64 MiB aggregate input budget; it never instantiates or caches the asset. If an exact asset operand starts with `-` or matches an option name, put every CLI option before the `--` end-of-options marker; every following token is then an operand.

`get` loads only the selected canonical texture through the active content pipeline and returns its dimensions, runtime format, mip level count, running game versions, and availability. Exact reads accept an unambiguous case/separator-normalized token, return the canonical physical identity, and fail closed if multiple candidates collide after normalization. The supported public SMAPI API does not expose a reliable per-mod loader/editor chain for an arbitrary final texture, so the typed provenance object reports `final-post-pipeline` and explicitly marks detailed provider provenance unavailable. A changed final dimension or format can still prove a deliberately replaced live fixture without inventing which mod performed the edit.

`preview` reads back only that one selected texture after requiring the RGBA8 `Color` runtime format and rejecting source dimensions above 8192, source populations above 16,777,216 pixels, or invalid metadata. Unsupported compressed or differently packed formats remain metadata-readable through `get` but fail closed for preview instead of interpreting their bytes as RGBA. A supported preview preserves aspect ratio, never upscales, uses nearest-neighbor sampling, and writes at most one 512x512 diagnostic PNG with a 2 MiB encoded limit. The response contains only its GUID-derived path relative to `.sdvkit/lab/single/runtime`, output dimensions, byte count, and SHA-256; the PNG itself remains below ignored `.sdvkit` as review evidence. The cached game texture is not mutated or disposed.

Every response and preview target is create-new, regular-file and reparse checked, request-bound, size bounded, and never reused. Unknown, colliding, non-texture, unclassified, oversized, stale, mismatched, or unsafe requests fail closed. There is no bulk preview, raw-pixel/base64 response, crop API, source-XNB export, texture mutation, network-role variant, or texture MCP tool.

### Inspect active audio metadata

An exact active `single` review can inventory the bounded audio identities visible through the final `Data/AudioChanges` and `Data/JukeboxTracks` assets, or probe one exact cue through Stardew's public soundbank API. Every request reads the current final asset state through SMAPI's active content pipeline, so normal SMAPI cache and invalidation behavior still applies and no audio file is read directly.

```powershell
sdvkit project review audio cues --offset 0 --limit 100 --topology single --json
sdvkit project review audio cue "maintheme" --topology single --json
```

`cues` returns stable ordinal pages from the union of current `AudioCueData.Id` values, jukebox track keys, and effective jukebox alternative-unlock IDs. The `Data/AudioChanges` dictionary key is only the modification key and is never reported as a playable cue; if multiple entries declare the same exact `Id`, the later final-pipeline entry wins just as Stardew's soundbank update does. Source categories and jukebox relationships remain distinct: an `alternativeUnlock` reference means only that hearing the old ID can unlock a jukebox entry, never that the ID is a playable soundbank alias. Alternative IDs are matched globally with ordinal-ignore-case semantics, and a later track replaces an earlier mapping. An alternative which matches exactly one playable data or primary-track identity case-insensitively annotates that canonical identity instead of inventing a second cue; multiple playable matches fail closed, while an unmatched alternative keeps the later effective spelling. Coverage's `jukeboxAlternativeReferences` still counts every bounded raw source reference, while each returned alternative identity has only its one effective relation. Each returned identity is probed without playback and reports only current soundbank existence, definition availability and variant counts, plus the bounded category, stream, loop, and reverb fields for a current `AudioChanges` entry. A null/omitted category is reported as Stardew's effective `Default`, while an explicitly empty or otherwise unsafe category fails closed; an unspecified file list and an explicitly empty file list remain distinct as `null` and `0` data-variant counts.

The public soundbank API can check an exact cue but cannot enumerate the built-in XACT cue bank. Coverage therefore reports `builtInCueCount: null` and `builtInCueInventoryStatus: "unavailableByPublicApi"`; an exact built-in probe does not expand or imply a complete built-in inventory. `dataDefined` describes the current post-pipeline `AudioChanges` entry, while `sessionResident` describes the current soundbank. Those values intentionally remain independent because Stardew keeps an applied audio override resident for the game session after its Data entry is removed.

Cue IDs are case-sensitive. If a cue operand starts with `-` or matches an option name, put every CLI option before the `--` end-of-options marker. Unknown, case-mismatched, non-exact ambiguous, malformed, oversized, unsafe-ID, dummy-bank, disposed-bank, stale-response, and unsafe-response cases fail closed. Results never expose modification keys, audio file paths, `CustomFields`, raw banks, PCM or wave data; they never play, record, mutate, or bulk-export audio. Pages default to 50 identities, accept limits of 1-100, and reuse the exact owned-review transport and cleanup boundary. Native MCP exposure is intentionally not part of this capability.

### Inspect observed mod-owned asset namespaces

The `mod-assets` subcommands expose a bounded read-only catalogue of conventional `Mods/<owner>/...` asset requests observed after AlwaysOn subscribed in the same exact, target-load-confirmed `single` review. This is lifecycle evidence, not a filesystem scan or a complete inventory of assets which no loaded mod requested during that interval.

```powershell
sdvkit project review mod-assets assets --offset 0 --limit 100 --topology single --json
sdvkit project review mod-assets keys "Mods/Example.Mod/Words" --offset 0 --limit 50 --topology single --json
sdvkit project review mod-assets get "Mods/Example.Mod/Words" "Greeting" --topology single --json
```

`assets` reports the observed runtime type, resolved namespace-owner identity when it matches one loaded mod ID, supported adapter shape, request and ready counts, lifecycle generation, and whether the current generation is requested, ready, invalidated, or unavailable. SMAPI identity casing and slash direction are treated as equivalent and consolidated, while stable hyphen/underscore name collisions and multiple requested runtime types stay visible and fail closed for exact reads. Coverage is complete only when no conventional observed request was dropped by a malformed identity or the 2048-entry catalogue bound. Detailed loader/editor provider attribution remains explicitly unavailable because the supported public SMAPI API does not reliably expose it for an arbitrary final asset.

`keys` and `get` load only one already-observed exact asset through SMAPI's active content pipeline and only through six reviewed adapters: string-to-string, string-to-integer, integer-to-string, and integer-to-integer dictionaries, ordered string lists, and one string singleton. Dictionary keys are ordinal-sorted, list keys are zero-based indexes, and the singleton key is `singleton`. Pages default to 50 with limits from 1 through 100; `get` accepts no pagination and returns only one primitive string or 32-bit integer. Asset operands stay canonical `Mods/<owner>/...` paths, keys are capped at 480 UTF-16 code units, and both require well-formed text. If an asset or key resembles an option, put every CLI option before `--`; all following tokens are operands.

Every response uses a request-bound create-new regular file below the ignored review runtime, with a bounded exact JSON shape and no reused or foreign temporary-file cleanup. Unknown, removed, unsafe, colliding, type-changing, unsupported, stale, or mismatched requests fail closed. There is no arbitrary reflection, unknown-type serialization, bulk export, mutation, normal-`Mods` scan, network-role variant, or mod-asset MCP tool.

AlwaysOn provides two visual review actions. `sdvkit screenshot <label>` requests a full-map PNG through Stardew's native map-screenshot path and remains gated on a loaded world plus Stardew's map-screenshot capability. `sdvkit screenshot viewport <label>` captures the current rendered game viewport, including title and loading screens before `Context.IsWorldReady`, menus, and HUD. For example, transport `sdvkit screenshot viewport menu-open` through `project review command` to inspect a menu without desktop automation. Labels are limited to 1-64 ASCII letters, digits, `-`, or `_`; the fixed result name is `SDVKit-<label>.png`, and an existing target is never overwritten. Success is proven only when the AlwaysOn log reports the full path and that exact PNG exists below the role's isolated `StardewValley/Screenshots` directory; `commandWritten=true` still proves console delivery only.

Automated review mouse input is restricted to SDVKit's existing process-local virtual cursor after exact review ownership and topology-role verification. It fails closed and never uses global `SendInput`, moves the physical pointer, changes window focus, or delegates review mouse input to generic computer-use automation.

For input-driven reviews, transport `sdvkit input press <SButton>` to press one exact SMAPI button for one input tick, or `sdvkit input cursor <ui-x> <ui-y>` to enable a process-local virtual cursor at a coordinate inside the current UI viewport. Button and cursor dispatch are available on title and loading screens before `Context.IsWorldReady` as well as in a loaded world. A mouse-button press and the explicit `MouseWheelUp` or `MouseWheelDown` review tokens require the process-local virtual cursor to be set first; wheel input additionally requires an active menu. Every SDVKit-controlled lab start prepares only its isolated profile for an initial bordered 1280x720 game window; AlwaysOn applies and verifies that baseline once after Stardew's title-window initialization, then leaves resize and UI-scale testing alone. Interactive review also keeps the separate SMAPI terminal visible and asks Windows not to activate the new process; a transient terminal activation can still be terminal-host behavior. Automated network roles may start minimized, but their game display mode is still windowed rather than borderless or fullscreen. The virtual cursor overrides only the mouse coordinates Stardew reads inside the isolated game process; it does not focus a window or move the user's physical pointer. For a bounded four-tick interval around an injected SMAPI button press, AlwaysOn lets SMAPI complete its normal pressed-to-released input update and also lets Stardew's own menu-input path run while its window stays in the background. Outside that interval, both activity getters retain their original values. The commands fail closed unless that adapter is installed and ready. Use `sdvkit input cursor clear` to remove the coordinate override, and AlwaysOn also clears it on return to title and controlled exit. This supports keyboard, virtual mouse, and controller paths; unavailable adapter state, invalid button names, mouse input without a virtual cursor, wheel input without an active menu, and out-of-viewport cursor coordinates fail closed. Each successful result confirms only input injection, so verify the resulting UI or state separately.

For a fixture-backed `single` review or a `network-2` pair that has not joined yet, `project review command` relaxes scenario readiness only for the exact built-in input grammar above and `sdvkit screenshot viewport <label>`. It still requires the exact owned staging, state, process and target-load binding; network-2 also requires both retained roles to be exact running AlwaysOn processes and one explicit selected role. Map screenshots, fixture and Data operations, target or companion commands, malformed variants, and broad `sdvkit input` prefixes keep the full fixture or joined-pair gate.

Commands supplied by a target or companion stay owned by that mod. For example, explicitly pass a local ready SMAPI Console Commands directory through `--companion` at `start`, then use `sdvkit project review command "debug sleep" --json`; SDVKit transports that existing command but neither reimplements nor locates the companion.

Code projects reuse inspection, the isolated Release build, and validated packaging. During review, SDVKit redirects its build, intermediate, log, staging, and package state into the owned temporary `review-prepared` tree instead of writing that state below the selected source project. Ready mod directories, additional native content packs, and a content-pack target are copied through the same strict plain-tree preparation path because they are already runtime artifacts; source projects, saves, secrets, executables, archives, game assemblies, reparse points, and nested manifests are rejected. Before any launch, the complete explicit set is checked case-insensitively for valid non-reserved `UniqueID` values, unique staging names, required dependency and minimum-version satisfaction, content-pack providers, and existing mod-group collisions. A content-pack target's provider must be a named code-mod companion. Only the target, named companions, named packs, and SDVKit AlwaysOn count as available dependencies.

For `single`, the whole set is installed below `.sdvkit/lab/single/mods` under one atomic review ownership marker. Existing JSON reports identify each artifact's role, kind, `UniqueID`, declared version, provider, and file-set identity. The existing exact PID/start-time/executable lifecycle still controls the process, while AlwaysOn confirms the target C# mod or content pack by exact `UniqueID` and canonical version. `stop` removes only paths selected by the valid review ownership marker after the exact process is confirmed stopped; every remaining path must still be the expected canonical direct child and contain no reparse points. Regular runtime-content drift inside those paths does not broaden the cleanup selection and no longer blocks removal, while start, status, and evidence checks can continue to expose that drift. An ownership, path, or reparse mismatch, an unreadable process, or an uncertain exit retains state and staging. Any confirmed exit of the exact review process is finalized on the next `project review status`, `stop`, or `start`; an unreadable or uncertain process remains fail-closed.

Review launches the detected `StardewModdingAPI.exe` directly in a separate interactive Windows console, so existing SMAPI, target, companion, and AlwaysOn commands can be typed normally. It does not add an RPC, scenario, or automation protocol. That console owns its normal input/output instead of SDVKit's captured stdout/stderr files; SMAPI's standard log and Stardew screenshots still resolve below the persistent isolated profile at `.sdvkit/lab/profiles/single`. Saves created there remain across a real process stop/start for reload testing, while normal saves, the normal or mod-manager-owned `Mods`, and the real game installation remain untouched. Unless `--test-save` is selected explicitly, the `single` review does not use the disposable fixture. It never stops automatically after 120 ticks.

### Owned test save in single review

First prepare the existing disposable baseline while every lab role is stopped:

```powershell
sdvkit lab test-save --topology single --json
```

Then select that exact project-owned Work-Copy explicitly:

```powershell
sdvkit project review start .\ExampleMod --topology single --test-save --json
sdvkit project review status --topology single --json
sdvkit project review stop --topology single --json
sdvkit project review start .\ExampleMod --topology single --test-save --json
```

`--test-save` reuses the existing test-save fixture store and AlwaysOn loader. It mounts only the registered Work-Copy below `.sdvkit/lab/single/test-save`, loads only its exact Save ID, and fails closed with the preparation command above when the registered baseline is absent. The review report's `testSave` object exposes the Save ID, fixture ID, ownership verification, and load phase; accepted load proof is `state=ready`, `phase=passed`, and `identityVerified=true`.

Once that exact owned review world is loaded, AlwaysOn exposes this bounded fixture surface. Each `sdvkit fixture ...` value below is a game-console line: quote it as the `<text>` argument to `project review command`; it is not another top-level SDVKit CLI command.

```powershell
sdvkit project review command "sdvkit fixture status" --topology single --json
sdvkit project review command "sdvkit fixture building ensure <alias> <building-kind> <x> <y>" --topology single --json
sdvkit project review command "sdvkit fixture object ensure <alias-or-id> <qualified-item-id>" --topology single --json
sdvkit project review command "sdvkit fixture object clear-owned <alias-or-id>" --topology single --json
sdvkit project review command "sdvkit fixture animal ensure <alias-or-id> <animal-kind>" --topology single --json
sdvkit project review command "sdvkit fixture enter <alias-or-id>" --topology single --json
sdvkit project review command "sdvkit fixture enter greenhouse" --topology single --json
sdvkit project review command "sdvkit fixture farm" --topology single --json
```

Every fixture invocation freshly verifies the active review process, exact fixture and Save identities, ownership marker, load phase, and current role before it reads or changes the world. It is unavailable in a plain single review, a smoke run, or any unverified or foreign save. `building ensure`, `object ensure`, `object clear-owned`, and `animal ensure` mutate only from the singleplayer main role or the `network-2` host. `status`, `enter`, and `farm` are role-local and can also be addressed to the verified farmhand. `fixture farm` follows only a natural Farm exit from the current review FarmHouse, the exact Greenhouse, or an owned fixture interior; for `enter`, the exact `greenhouse` token selects the one loaded Greenhouse and its natural entry. Owned building mutations keep resolving their normal alias or GUID, including an existing `greenhouse` alias; use its GUID when entering that owned building. Neither navigation command is a general warp surface. For `network-2`, send every line through the same transport with exactly one `--role host` or `--role farmhand` as appropriate.

The ensure operations are idempotent for the same SDVKit-owned alias and exact requested state. Run `sdvkit fixture farm` before creating a new building; Stardew requires the main player to be on the Farm, and `building ensure` fails before instantiation, placement planning, or changed placement content otherwise. Building kinds resolve from the live `BuildingData` dictionary and animal kinds from the live canonical FarmAnimal data using stable internal data IDs, never localized display names. CLI tokens are case-insensitive and normalize the separators in those IDs, so the existing `deluxe-barn` and `white-cow` tokens remain compatible while kinds such as `coop` and `white-chicken` use the same resolver. Unknown or normalization-colliding tokens, building data which Stardew cannot instantiate or place, and animal/building combinations which Stardew marks incompatible fail closed before world mutation. The exact available kinds can vary with the Stardew version and data loaded for the isolated review world; errors show only a bounded set of canonical candidates instead of dumping the data dictionaries.

Before a new building is placed, `building ensure` derives the exact placement area from that resolved `BuildingData`: the footprint, every additional placement tile, and the human-door access tile. It checks map rules, existing buildings, players, characters, animals, large terrain features, and furniture removability before mutation. Only then, and only in that disposable placement area, it removes tile-bound Farm objects, ordinary terrain features, overlapping resource clumps, and furniture which Stardew reports as safely removable. Object identity and `modData` do not control this preparation; content outside the derived area is not selected. The result reports every removed category count, and a later Stardew placement rejection requires fixture reset before retrying. `animal ensure` checks the resolved canonical animal kind against the concrete target `AnimalHouse`; it never converts or moves an existing animal.

`object clear-owned` remains separate and removes only the fixture object carrying SDVKit's exact ownership markers; it does not clear a building's object collection or touch an unowned object. Fixture status and results report only generic SDVKit-owned world facts. They do not interpret a target mod's state, special-case StardewInteriorChanger, or enumerate or expose foreign `modData`.

The game-console fixture surface deliberately has no save or sleep command. Without the explicit MCP fixture-action opt-in, use an explicitly selected existing SMAPI, target, or companion command for those actions and verify its own completion evidence. The opted-in `stardew_fixture_save` tool described below is the sole fixture-save adapter and reuses the existing test-save iterator. For console fixture commands, `commandWritten=true` proves delivery only: require the matching AlwaysOn result plus direct world, log, persistence, or visual evidence for the intended effect.

A confirmed `stop` unmounts the exact fixture but deliberately preserves its Work-Copy. Repeating `start` with the same explicit target selection and `--test-save` launches a new process against that retained work state. After the final stop, reset only that verified fixture and any retained exact single-review staging:

```powershell
sdvkit project review reset --topology single --json
```

Single reset requires the single, host, and farmhand roles to be stopped and no retained `network-2` review. It restores the Work-Copy byte-for-byte from the registered baseline. Missing or mismatched fixture ownership, an active mount, process state, or staging ownership blocks mutation; normal saves and normal or mod-manager-owned Mods are never selected. A fixture-backed review is not complete until its final verified stop and topology-specific reset have succeeded.

### Bounded network-2 review

Select the existing `network-2` topology explicitly to run exactly one local host and one local farmhand for a standalone C# target project. Content-pack targets are singleplayer-only and fail before lab mutation. Both roles require SDVKit AlwaysOn; there is no supported review mode that disables it. The C# target, every `--companion`, and every additional `--content-pack` are still selected only on `start`, prepared through the existing review staging path, and copied as one identical explicit file set to the isolated host and farmhand mod groups. The same target/build identity must be confirmed in both roles.

```powershell
$target = "<absolute C# target-project path>"
sdvkit project review start $target `
  --topology network-2 `
  --json
sdvkit project review status --topology network-2 --json
sdvkit project review command "sdvkit fixture status" --topology network-2 --role host --json
sdvkit project review command "sdvkit fixture status" --topology network-2 --role farmhand --json
sdvkit project review command "sdvkit fixture building ensure <alias> <building-kind> <x> <y>" --topology network-2 --role host --json
sdvkit project review stop --topology network-2 --json
```

`network-2 command` requires exactly one `--role host` or `--role farmhand`; roles are rejected for `single`. It transports one existing SMAPI, target, companion, or AlwaysOn console line only to that exact owned role. It does not create a multiplayer protocol or broadcast a command to the pair. Before using the fixture surface, require the joined-pair and exact owned-fixture proof from both roles; every fixture invocation performs the same current-world and role verification again.

A confirmed clean `network-2 stop` stops the farmhand and host through their existing exact-process lifecycle, unmounts the owned review fixture, archives the role logs, and deliberately preserves both the work save and exact review staging. When those retained artifacts still reproduce their recorded build identities, running the same explicit `start --topology network-2` selection again exercises a real pair restart against the retained work state. Stop does not reset the fixture to baseline and does not remove the staged review set.

After both roles are confirmed stopped, perform the final cleanup explicitly:

```powershell
sdvkit project review reset --topology network-2 --json
```

The network reset requires the explicit `--topology network-2` and is invalid while either role or the single lab is active. It restores the owned work fixture byte-for-byte from its registered baseline and removes only the host/farmhand review paths selected by the valid ownership marker. A process-identity, fixture-ownership, staging-path, or reparse-point mismatch blocks mutation instead of risking normal saves or normal Mods; regular file drift inside those exact owned staging paths does not.

The fixture commands prepare only generic SDVKit-owned world state; target behavior must be exercised and verified separately through that target's public interface and direct visual or persistence evidence. A local two-role review does not claim compatibility for more players, remote play, or untested mod behavior, and matching logs or hashes do not replace visual comparison.

Screenshot capture remains the separately developed SDVKit AlwaysOn command, not a second screenshot implementation or framework in this slice. Address that same `sdvkit screenshot <label>` command through the existing role-specific transport and use distinct labels, for example:

```powershell
sdvkit project review command "sdvkit screenshot host-fixture" --topology network-2 --role host --json
sdvkit project review command "sdvkit screenshot farmhand-fixture" --topology network-2 --role farmhand --json
```

## Native MCP for an active review

Start the native STDIO server from the directory that owns the already-running
project review. A single-player review needs no role. A network-2 server must
select exactly one role, and separate client processes are required to inspect
both roles:

```powershell
sdvkit project review mcp serve
sdvkit project review mcp serve --topology single
sdvkit project review mcp serve --topology network-2 --role host
sdvkit project review mcp serve --topology network-2 --role farmhand
sdvkit project review mcp serve --topology single --allow-fixture-actions
sdvkit project review mcp serve --topology network-2 --role host --allow-fixture-actions
```

Omitting `--topology` selects `single`. A role is rejected for `single`, while
`network-2` requires exactly one `--role host` or `--role farmhand`. Duplicate,
missing, or unknown option values are usage errors. The command deliberately has
no `--json`, HTTP, TCP, relay, secret, or Python mode. Protocol frames are the
only stdout output; bounded startup diagnostics use stderr. Closing the client's
stdin ends the child server process. Input tools are absent by default. Add the
granular `--allow-input` startup flag only when that client is explicitly
authorized to exercise process-local review input; the flag does not authorize
fixture changes, arbitrary console text, or future action families.

`--allow-fixture-actions` is a granular capability grant, not a general action
or input switch. Without it, no `stardew_fixture_*` tool is advertised. With it,
startup requires the exact selected role to be bound to a fresh SDVKit-owned
disposable test save; a plain review or normal save is rejected. The flag never
selects a save and never grants access to the normal Stardew `Saves` directory.

The role is fixed when the server starts and cannot be selected or changed in a
tool call. `role` is `null` for `single` and exactly the configured `host` or
`farmhand` for `network-2`. Every server exposes three read-only observation tools:

- `stardew_runtime_get {}` returns matching structured JSON and compact JSON
  text with schema version, launch ID, topology, selected role, observation
  time, exact target `UniqueID`/version/build identity, optional verified
  review-fixture identity, and the selected role's runtime object. Before a
  world is ready, season/day/year/time/location/tile are explicitly `null`;
  `worldReady` and `menuOpen` remain available.
- `stardew_review_get {}` returns the exact active ownership projection: the
  launch and topology, fixed role, verified running process and fresh status,
  target identity and load state, optional verified fixture/save identity, and
  every exactly staged target, companion, and content-pack role with its kind,
  canonical version, provider where applicable, and build identity.
- `stardew_mods_list { "offset": 0, "limit": 50 }` reconciles that exact staged
  set with the selected role's public SMAPI loaded-mod snapshot. It returns the
  SDVKit support mod and each selected artifact with source category,
  expected/loaded kind, expected/loaded version, load status, and bounded
  warning/error arrays.
  Offset defaults to 0, limit defaults to 50, and limit remains within 1-100;
  follow `page.nextOffset` until it is `null`.

Every server also exposes one controlled evidence-capture tool:

- `stardew_screenshot_capture { "mode": "viewport", "label": "menu-open" }`
  accepts exactly `map` or `viewport` and a 1-64 character ASCII label made of
  letters, digits, `-`, or `_`. It reuses AlwaysOn's existing capture paths,
  creates `SDVKit-<label>.png` without overwriting, and returns compact
  launch/topology/role metadata followed by real `image/png` content. The PNG
  must be a fresh, complete, bounded 8-bit RGB or RGBA file at the exact selected role's
  isolated `StardewValley/Screenshots` path; path escapes, reparse points,
  stale or mismatched results, malformed PNGs, files over 16 MiB, and timed-out
  requests fail closed without retry. Map mode still requires a loaded world;
  viewport mode uses the current game backbuffer.

The mod diagnostics are fixed SDVKit messages for a selected mod that is not
loaded or whose loaded version or kind differs. They are not raw SMAPI loader
warnings, exception text, or log excerpts. An unexpected loaded identity or an
invalid, duplicate, oversized, stale, or mismatched snapshot fails closed
instead of being returned as untrusted inventory data.

A server bound to `single` additionally exposes these three canonical Data
tools:

- `stardew_data_assets_list` takes optional `offset` and `limit` values and maps
  directly to `project review data assets`. It returns the canonical inventory,
  page, and complete coverage counts.
- `stardew_data_keys_list` takes one required `asset` plus optional `offset` and
  `limit`, mapping to `project review data keys` and returning canonical asset
  metadata plus one stable-key page.
- `stardew_data_record_get` takes required `asset` and `key` strings, mapping to
  `project review data get` and returning exactly one deterministic canonical
  record.

Offsets are non-negative 32-bit integers, limits default to 50 and stay within
1-100, asset names are limited to 256 characters, and keys to 2,048. The record
value retains its canonical JSON shape but remains subject to the existing 4 MiB
record and 5 MiB response limits. All three tools return operation-specific closed
envelopes and identical compact JSON text. They are deliberately absent from a
`network-2` server; use the existing single-review CLI or MCP
surface rather than inferring one role's game-content pipeline from the other.

With `--allow-input`, and only for that server process, every topology also
exposes four typed action tools:

- `stardew_input_press { "button": "F8" }` injects one exact non-wheel SMAPI
  `SButton` for one input tick. Mouse buttons require a previously confirmed
  virtual cursor; they never fall back to the physical pointer position.
- `stardew_input_cursor_set { "x": 200, "y": 100 }` sets the existing
  process-local virtual cursor at one in-viewport UI coordinate without moving
  the physical pointer.
- `stardew_input_cursor_clear {}` clears the virtual cursor and transient
  background-input state.
- `stardew_input_wheel { "direction": "up" | "down" }` sends one wheel notch
  and requires both the virtual cursor and an active game menu.

Each call is bound to the role selected at server startup, takes a closed JSON
object, acquires the role-local cross-process action lock without queueing,
revalidates the exact review and published foreground-window identity before
dispatch, waits for one request-ID-bound, fresh, create-new AlwaysOn
acknowledgement, and accepts it only after a later AlwaysOn status timestamp and
game tick preserve that same binding. A request is never retried after console
delivery is possible. If cancellation arrives after a valid acknowledgement,
the result retains that acknowledgement, sets `cancellationRequested` to
`true`, reports an error, and still completes the post-action binding check.
Server EOF performs a bounded cursor/transient-input clear when this MCP session
may have dispatched input; an unconfirmed cleanup makes the server exit
nonzero. A successful acknowledgement proves only the bounded input operation;
verify the intended target-mod effect separately.

With the explicit `--allow-fixture-actions` opt-in, a `single` server or a
`network-2` host server additionally exposes exactly these six tools:

- `stardew_fixture_status_get {}` returns the role-local location, player and
  multiplayer state plus bounded stable identities for SDVKit-owned fixture
  buildings.
- `stardew_fixture_enter { "building": "<alias-or-guid>" }` enters one owned
  fixture building, or use the exact `greenhouse` token for the canonical
  greenhouse, through its natural warp.
- `stardew_fixture_farm {}` returns from an allowed review interior through its
  natural Farm warp.
- `stardew_fixture_building_ensure { "alias": "barn-a", "kind": "Deluxe Barn", "x": 16, "y": 20 }`
  reuses the canonical building resolver, complete placement preflight,
  ownership markers, idempotence and rollback path.
- `stardew_fixture_animal_ensure { "building": "barn-a", "kind": "White Cow" }`
  reuses canonical animal resolution, house compatibility, stable ownership,
  idempotence and rollback.
- `stardew_fixture_save {}` completes Stardew's existing supported save iterator
  and returns the exact Save ID and persistence time only after its completion
  signal.

A farmhand server advertises only `status_get`, `enter`, and `farm`; it cannot
discover or dispatch building, animal, or save mutations. Object creation and
clearing, arbitrary commands, generic RPC, unrestricted warps, and other world
editing are not MCP tools. Every call repeats the exact launch, role, process,
target, fixture and Save identity preflight immediately before dispatch. One
cross-process action lock rejects concurrent input or fixture work for that
role instead of queueing it. Response files are unique, create-new, bounded,
freshness checked, and bound to the request, launch, topology, role, fixture and
Save. Cancellation before dispatch writes nothing. After confirmed dispatch,
the action lock stays held while SDVKit drains and validates the acknowledgement
up to that operation's bound (the existing two-minute save bound plus a bounded
five-second acknowledgement grace), and the action is never retried. A missing or invalid acknowledgement returns
`mayHaveRun=true`; a validated acknowledgement returns the exact result with
`cancellationRequested=true` when cancellation was observed.

Ensure results carry canonical kinds and stable building or animal IDs plus a
`changed` flag, so an unchanged repeat is deterministic evidence of
idempotence. Navigation results carry the final location/tile and `changed`;
save results carry `saveId` and `persistedAtUtc`. A tool result never exposes
foreign `modData`, paths, peer state, or normal-save data. Prove actual restart
persistence by stopping and starting the same explicitly selected review, then
finish with the existing topology-specific `project review reset` lifecycle.

A project-local Codex configuration can keep the surface explicitly limited:

```toml
[mcp_servers.sdvkit_review]
command = "sdvkit"
args = ["project", "review", "mcp", "serve", "--topology", "single"]
cwd = "C:\\path\\to\\the\\lab-owning-project"
enabled_tools = [
  "stardew_runtime_get",
  "stardew_review_get",
  "stardew_mods_list",
  "stardew_screenshot_capture",
  "stardew_data_assets_list",
  "stardew_data_keys_list",
  "stardew_data_record_get",
]
```

For an explicitly authorized input session, add `"--allow-input"` to `args`
and independently allow only the needed names from
`stardew_input_press`, `stardew_input_cursor_set`,
`stardew_input_cursor_clear`, and `stardew_input_wheel`. Omitting the startup
flag keeps all four absent even if the client requests or allowlists them.

For example, bind a separate network-2 host client by changing only the server
name and arguments:

```toml
[mcp_servers.sdvkit_review_host]
command = "sdvkit"
args = ["project", "review", "mcp", "serve", "--topology", "network-2", "--role", "host"]
cwd = "C:\\path\\to\\the\\lab-owning-project"
enabled_tools = [
  "stardew_runtime_get",
  "stardew_review_get",
  "stardew_mods_list",
  "stardew_screenshot_capture",
]
```

The threat boundary is the existing project review, not a general game or
desktop API. The shared context revalidates the exact ownership marker,
topology, target build identity, PID/start-time/executable identity, fresh outer
AlwaysOn marker, optional fixture, and, for `network-2`, reciprocal joined-pair
proof under the existing short operation lock. `stardew_runtime_get` and every
Data call additionally require the target to be loaded and a valid fresh runtime
snapshot. The diagnostic tools deliberately allow a selected target to be
reported as not loaded or mismatched; instead, they require a valid role-local
loaded-mod snapshot captured through SMAPI's public mod registry. They never
infer loaded state by scanning a mod directory and only validate the exact
SDVKit-owned isolated staging tree. Each Data call then delegates to the same
#71 service used by the CLI, which revalidates the exact single review before
sending its bounded request to the existing game-side reader. There is no second
inventory, serializer, mailbox, or lifecycle. Network-2 additionally requires both exact
role states and processes, identical staged target/build/fixture/save bindings,
and returns only the role fixed at server startup. The lock is released before
MCP serialization.

A mismatch returns a controlled tool error and no stale payload; Data and
screenshot failures expose a bounded internal code rather than raw paths or
transport details. MCP
responses never expose peer runtime data, filesystem paths, PIDs, environment
values, raw SMAPI loader warnings or logs, menu CLR types, or arbitrary state.
The MCP server never reads the normal or mod-manager-owned `Mods` directory or
normal saves. It opens no listener and cannot start, stop, reset, transport
arbitrary console text, or mutate a review except through an explicitly enabled
typed action family. `--allow-input` wraps only the existing process-local
cursor, one-tick button, and one-notch wheel paths described above.
`--allow-fixture-actions` wraps only the closed typed operations above inside the
already active owned disposable fixture. Without either action opt-in, the
screenshot tool's only write is its named, create-new evidence PNG below the
selected role's ignored isolated profile.

## Agent workflow

The repository-owned [`sdv-project-smoke`](.agents/skills/sdv-project-smoke/SKILL.md) skill guides agents through discovery, project inspection, the existing end-to-end smoke, and evidence-based JSON/log reporting. It uses `single` by default and `network-2` only when a user explicitly requests a multiplayer test.

The repository-owned [`sdv-project-review`](.agents/skills/sdv-project-review/SKILL.md) skill guides interactive functional and visual `project review` work with explicit local targets, evidence-bounded commands, real restart checks, and fail-closed cleanup.

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

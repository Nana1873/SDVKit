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

Install the .NET 8 SDK, then download `SDVKit-0.5.3-win-x64.zip` and `SDVKit-0.5.3-win-x64.zip.sha256` from the GitHub release assets. Extract and start the CLI without a repository checkout:

```powershell
Get-FileHash .\SDVKit-0.5.3-win-x64.zip -Algorithm SHA256
Expand-Archive .\SDVKit-0.5.3-win-x64.zip
& .\SDVKit-0.5.3-win-x64\sdvkit.exe --help
```

Before replacing the program files, cleanly stop any active SDVKit lab or project review.

## What's new in v0.5.3

Since `v0.5.2`, `fixture building ensure` derives its exact placement area from Stardew's `BuildingData` and prepares tile-bound dynamic content there by category, without a vanilla object-ID allowlist. Content outside that derived area remains untouched.

Structural blockers and all save, fixture, role, and ownership boundaries remain fail-closed. `object clear-owned` remains separate and marker-strict.

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

Alongside the native mod path, the controlled `single`, `host`, and `farmhand` roles receive separate persistent Windows user-profile roots at `.sdvkit/lab/profiles/single`, `.sdvkit/lab/profiles/network-2/host`, and `.sdvkit/lab/profiles/network-2/farmhand`. Stardew and SMAPI AppData is redirected per controlled process, so preferences, saves, startup preferences, screenshots, and standard SMAPI logs resolve below `.sdvkit/` too. AlwaysOn verifies the exact game-side Stardew data path before it activates. The ordinary `start`/`status`/`stop` lifecycle does not enumerate, open, copy, select, or modify any personal save, and it never writes to the normal or mod-manager-owned `Mods` directory. This is process-level data isolation, not a Windows sandbox: tested mods and external services such as Steam can still access shared machine resources. Game binaries and content continue to be read from the detected real installation; SDVKit does not create a copied game folder.

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

The package is revalidated before extraction. Its package SHA-256, declared manifest version, canonical SMAPI version, and complete staged file-set identity are recorded, and the package is extracted only within the selected isolated mod group as a sibling of AlwaysOn under `.sdvkit/lab/single/mods` or `.sdvkit/lab/network-2/{host,farmhand}/mods`. In JSON, `declaredVersion` preserves the package-manifest text while `version` is SMAPI's canonical semantic form (for example, `1.0` becomes `1.0.0`). Network-2 copies the same prepared package file set to both roles and requires matching identities. For owned SMAPI code-mod artifacts in smoke and review, one newly created regular root `config.json` is the only accepted runtime file-set difference, and only when every remaining file still reproduces the original identity; content packs and every other change remain strict drift. An unowned directory, another mod, drifted ownership state, or any retained earlier project-smoke staging blocks the next run. Even an exactly matching ownership marker is not auto-replaced, because a later invocation cannot prove that every child from an earlier failed launch stopped; normal confirmed cleanup removes it in the originating run.

AlwaysOn uses SMAPI's loaded-mod registry after `GameLaunched` to confirm the exact target `UniqueID` and manifest version on the single process or on both host and farmhand. The command then reuses the existing disposable-fixture load, 120-tick single or joined-pair smoke, exact-process clean stop, option restoration, junction cleanup, and byte-for-byte baseline reset. On a confirmed normal end, it removes only its own target staging; uncertain process ownership retains that staging and reports a blocked cleanup instead of mutating a possibly active mod group.

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

`command` writes exactly one complete, quoted SMAPI, target, companion, or AlwaysOn console line to the currently active exact review process; it does not discover or download anything. Use it only while the interactive console prompt is idle, with no partially typed command or parallel manual console input. A successful JSON result sets `commandWritten` to `true` only after the complete line was added to the Windows console input buffer; it does not confirm that SMAPI recognized or successfully executed the command.

AlwaysOn provides two visual review actions. `sdvkit screenshot <label>` requests a full-map PNG through Stardew's native map-screenshot path; `sdvkit screenshot viewport <label>` captures the current rendered game viewport, including menus and HUD. For example, transport `sdvkit screenshot viewport menu-open` through `project review command` to inspect an in-game menu without desktop automation. Labels are limited to 1-64 ASCII letters, digits, `-`, or `_`; the fixed result name is `SDVKit-<label>.png`, and an existing target is never overwritten. Success is proven only when the AlwaysOn log reports the full path and that exact PNG exists below the role's isolated `StardewValley/Screenshots` directory; `commandWritten=true` still proves console delivery only.

For input-driven reviews, transport `sdvkit input press <SButton>` to press one exact SMAPI button for one input tick, or `sdvkit input cursor <ui-x> <ui-y>` to enable a process-local virtual cursor at a coordinate inside the current UI viewport. The virtual cursor overrides only the mouse coordinates Stardew reads inside the isolated game process; it does not focus a window or move the user's physical pointer. For a bounded four-tick interval around an injected press, AlwaysOn also lets Stardew's own menu-input path run while its window stays in the background. Use `sdvkit input cursor clear` to remove the coordinate override, and AlwaysOn also clears it on return to title and controlled exit. This supports keyboard, mouse, and controller button paths through SMAPI's input state; invalid button names, unloaded worlds, and out-of-viewport cursor coordinates fail closed. Each successful result confirms only input injection, so verify the resulting UI or state separately.

Commands supplied by a target or companion stay owned by that mod. For example, explicitly pass a local ready SMAPI Console Commands directory through `--companion` at `start`, then use `sdvkit project review command "debug sleep" --json`; SDVKit transports that existing command but neither reimplements nor locates the companion.

Code projects reuse inspection, the isolated Release build, and validated packaging. During review, SDVKit redirects its build, intermediate, log, staging, and package state into the owned temporary `review-prepared` tree instead of writing that state below the selected source project. Ready mod directories, additional native content packs, and a content-pack target are copied through the same strict plain-tree preparation path because they are already runtime artifacts; source projects, saves, secrets, executables, archives, game assemblies, reparse points, and nested manifests are rejected. Before any launch, the complete explicit set is checked case-insensitively for valid non-reserved `UniqueID` values, unique staging names, required dependency and minimum-version satisfaction, content-pack providers, and existing mod-group collisions. A content-pack target's provider must be a named code-mod companion. Only the target, named companions, named packs, and SDVKit AlwaysOn count as available dependencies.

For `single`, the whole set is installed below `.sdvkit/lab/single/mods` under one atomic review ownership marker. Existing JSON reports identify each artifact's role, kind, `UniqueID`, declared version, provider, and file-set identity. The existing exact PID/start-time/executable lifecycle still controls the process, while AlwaysOn confirms the target C# mod or content pack by exact `UniqueID` and canonical version. `stop` removes review directories only after the exact process is confirmed stopped and every owned directory still matches its recorded manifest and file-set identity under the narrow code-mod configuration rule above. An ownership mismatch, drift, unreadable process, or uncertain exit retains state and staging. Any confirmed exit of the exact review process is finalized on the next `project review status`, `stop`, or `start`; an unreadable or uncertain process remains fail-closed.

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

The fixture surface deliberately has no save or sleep command. Use an explicitly selected existing SMAPI, target, or companion command for those actions, and verify its own completion evidence. For fixture commands too, `commandWritten=true` proves console delivery only: require the matching AlwaysOn result plus direct world, log, persistence, or visual evidence for the intended effect.

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

A confirmed clean `network-2 stop` stops the farmhand and host through their existing exact-process lifecycle, unmounts the owned review fixture, archives the role logs, and deliberately preserves both the work save and exact review staging. Running the same explicit `start --topology network-2` selection again therefore exercises a real pair restart against the retained work state. Stop does not reset the fixture to baseline and does not remove the staged review set.

After both roles are confirmed stopped, perform the final cleanup explicitly:

```powershell
sdvkit project review reset --topology network-2 --json
```

The network reset requires the explicit `--topology network-2` and is invalid while either role or the single lab is active. It restores the owned work fixture byte-for-byte from its registered baseline and removes only the verified host/farmhand review staging. A process-identity, fixture-ownership, or staged-file mismatch blocks mutation instead of risking normal saves or normal Mods.

The fixture commands prepare only generic SDVKit-owned world state; target behavior must be exercised and verified separately through that target's public interface and direct visual or persistence evidence. A local two-role review does not claim compatibility for more players, remote play, or untested mod behavior, and matching logs or hashes do not replace visual comparison.

Screenshot capture remains the separately developed SDVKit AlwaysOn command, not a second screenshot implementation or framework in this slice. Address that same `sdvkit screenshot <label>` command through the existing role-specific transport and use distinct labels, for example:

```powershell
sdvkit project review command "sdvkit screenshot host-fixture" --topology network-2 --role host --json
sdvkit project review command "sdvkit screenshot farmhand-fixture" --topology network-2 --role farmhand --json
```

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

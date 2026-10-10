# Lab lifecycle reference

Use this page to interpret process, staging, fixture, and cleanup results. For commands in execution order, use the [live guide](live-review.md). See the [capability matrix](README.md#capability-matrix) for supported targets and roles.

## Process and profile lifecycle

SDVKit discovers one ready installation, builds GameSide (SDVKit's game-side SMAPI mod, called AlwaysOn before 2.0) against its local assemblies, and launches `StardewModdingAPI.exe` directly with SMAPI's absolute `--mods-path`. Game binaries/content remain in the installed game directory; SDVKit does not copy them.

| State | Location relative to the lab root |
| --- | --- |
| Single mod group | `.sdvkit/lab/single/mods` |
| Network mod groups | `.sdvkit/lab/network-2/{host,farmhand}/mods` |
| Single user profile | `.sdvkit/lab/profiles/single` |
| Network user profiles | `.sdvkit/lab/profiles/network-2/{host,farmhand}` |
| Registered disposable world | `.sdvkit/lab/single/test-save` |

Per-process profile redirection keeps Stardew/SMAPI preferences, saves, screenshots, and standard logs below these isolated profiles. GameSide verifies the exact game-side Stardew data path before activating. Normal saves and Mods are not automatically selected or read. This is process/data isolation, not an OS sandbox: arbitrary tested code and external services may access shared machine resources.

### Identity and exit

Ownership binds the exact PID, UTC process start time, executable identity, and matching game-side marker. Status rechecks that binding. Stop publishes that launch's owned stop request, waits for the exact process handle and terminal marker, and clears ownership only after confirmed exit. Unknown identity, missing terminal evidence, or a timeout retains the process record.

Status is a complete JSON snapshot, published by a same-directory atomic rename. An open reader can finish the old snapshot; later opens see the new one. Each read still validates launch/PID/start identity, nested role/build identity, bounded JSON and freshness; an active marker older than five seconds is stale. A partial or mismatched marker never establishes readiness. On Windows, publication uses `FileRenameInfoEx` with POSIX replacement semantics (Windows 10 version 1709 or later). Denied writes, read-only targets and filesystems that do not support the operation remain visible errors; no non-atomic fallback is used. GameSide logs the first error of each uninterrupted failure period, and a later successful tick can publish fresh status again.

### Command endpoint

Each launch gets a random token through its environment. The game-side mod listens on a random loopback port and publishes it as `httpPort` in its status. It accepts only `POST /v1/command` from `127.0.0.1` with that token, the exact `Host` and no `Origin` header. The CLI and native MCP send every review command line there instead of typing into the SMAPI console.

A command runs on the game thread of the screen selected by SMAPI's `screen=<id>` argument. If the game does not start it within 5 seconds, it is withdrawn and reported as not run. This also happens while the game saves or starts a new day, because SMAPI pauses the update events that run commands. A missing screen, an unknown subcommand or a failed screen binding is reported as rejected without running the command. More than eight simultaneous connections are refused. Every reply names the launch it came from, and the CLI rejects a reply for another launch.

A line that does not start with `sdvkit`, such as a console command registered by the reviewed mod or SMAPI's `help`, runs through SMAPI's own command manager on the selected screen, exactly as if typed. The isolated SMAPI log records the line. Such commands have no SDVKit screen-ownership or binding check, just like typing them into the console. A failing command is reported as `reviewCommandFailed`, and an unknown command as `reviewCommandRejected`.

Command results stay in the game process. The game-side mod keeps each response in a bounded in-memory mailbox, and the CLI takes it once through `GET /v1/response/<name>` with the same token. The mailbox holds at most 64 responses and 32 MiB, and drops a response after two minutes. Nothing is written to the runtime directory.

Each status read opens the file again. A missing marker reports `pending`; an unreadable or malformed marker reports `invalid`; a marker for another launch or process reports `mismatch`. A later readable snapshot can recover on the next invocation, but it must still pass identity and freshness checks. SDVKit does not substitute an older cached snapshot for a failed read.

GameSide disables `pauseWhenOutOfFocus` in its isolated profile while running. On controlled exit it attempts to restore and read back the captured option, publishes `exiting` or `restoreFailed`, and asks the game to exit normally in either case. A failed final publication is logged separately even if active updates were already failing; it does not hold the game open. Without a matching terminal marker, process exit alone returns `cleanStopNotConfirmed` and retains ownership for inspection. A published `restoreFailed` marker instead reports an unconfirmed restoration as a warning after otherwise safe exit; the next start reapplies the lab option. Crashes or manual/forced termination do not promise restoration.

Normal stop has no process-name search or kill fallback. Only when Windows cannot establish the identity of a freshly created child can that child be aborted through its original creation handle before start returns.

## Disposable baseline and smoke

Run `lab test-save --topology single` while all roles are stopped. The first invocation creates the registered world through Stardew's normal new-game/save flow. It exposes only the exact generated Save ID through a verified junction inside the isolated profile, confirms stop and unmount, captures the baseline, restores a work copy, and runs the scenario. Later runs reuse that baseline.

Before scenario mutation, the loader verifies Save ID, unique game ID, player/farm/favorite-thing identities, ownership markers, and the expected single/main-player state. The single scenario displays its HUD message and observes 120 real update ticks. Completion needs the game-side marker, exact exit, junction removal, and byte-for-byte baseline reset. A pre-existing entry at the generated fixture path blocks preparation; it is not replaced speculatively.

Automation uses the [supported version bands](../README.md#requirements) plus runtime capability/signature probes. Changed or missing required APIs reject automation before it proceeds.

### Local host and farmhand

`lab smoke --topology network-2` reuses the prepared baseline. It starts exactly one local host and farmhand, with separate profiles and the same GameSide build. The pair must really join, expose reciprocal identities, and remain connected for 120 consecutive verified ticks while running unfocused with GameSide active. Each result has its own process/foreground observations and logs.

Normal teardown stops farmhand then host, verifies exit/unmount, and resets the work fixture. Join, identity, background progress, exit, mount cleanup, or reset failure remains blocking. This proves that local two-role scenario only.

## Project smoke identity and teardown

`project smoke` selects one standalone C# manifest/project and uses the existing inspector, Release builder, and validated package. Unsupported project shapes, reserved `SDVKit.GameSide` identity, or missing required dependencies produce controlled failures; optional dependencies may be absent.

The selected source project's `.sdvkit/` owns build/package output. The command's current directory owns live state. The validated ZIP is rechecked before extraction and staged as a sibling of GameSide. Network roles receive the same prepared file set. Results retain package SHA-256, staged `buildIdentity`, manifest `declaredVersion`, and SMAPI's canonical `version` (for example, `1.0` becomes `1.0.0`).

After `GameLaunched`, SMAPI's loaded-mod registry must confirm the expected target ID/version in each role. The disposable-world/tick/teardown path then runs. Acceptance requires `state=passed`, matching role identities, sufficient ticks, `fixtureReset=true`, and `stagingRemoved=true`. This does not measure an in-memory DLL hash or prove all target features.

### Drift versus cleanup authority

During start, status, and evidence checks, one newly created regular root `config.json` is the accepted code-mod difference; other changes remain visible as drift and can block replacement or an unqualified build-identity claim.

After the exact process is confirmed stopped, regular file drift does not prevent cleanup of the owned staging selection. Authority comes from the ownership marker, canonical direct-child paths, and a reparse-free tree. Foreign directories, path/ownership drift, reparse points, unreadable processes, or uncertain exits still block mutation. A matching stale marker is not automatically replaced: a later invocation cannot assume all children of a previous failed launch stopped.

## Review staging and retained state

Review prepares C# builds/intermediates/packages under its temporary owned `review-prepared` tree. Ready mod directories and content packs use plain-tree validation instead. They reject source projects, nested manifests, saves, secrets, executables, archives, game assemblies, and reparse points. Explicit selected dependencies must satisfy identities, minimum versions, provider requirements, unique staging names, and collision checks before launch.

Reports identify target/companion/pack role, kind, ID, declared/canonical version, provider, and file-set identity. The exact-process binding controls cleanup. A confirmed exit may be finalized on the next status, stop, or start; an unreadable process remains uncertain.

Interactive review owns a separate Windows console rather than captured lab stdout/stderr. SMAPI logs and screenshots still belong to the isolated profile. Plain single review retains its own isolated saves; `--test-save` explicitly selects the registered work fixture. Review does not end automatically after 120 ticks.

See [finish or test persistence](live-review.md#finish-or-test-persistence): single fixture stop retains the work save; network stop retains both work and staging; final applicable reset restores the baseline and removes retained staging. Never reset between the halves of a persistence test.

## Fixture command reference

Each fixture operation is a native MCP tool. Call one from the command line with [`project review call`](mcp.md#call-a-tool-from-the-command-line) and `--allow-fixture-actions`; for network-2, add `--topology network-2` and one explicit `--role`. Arguments are in [opt-in fixture actions](mcp.md#opt-in-fixture-actions), together with the lab tools for time, weather, season, warp, zoom and window.

| Tool | Role / result |
| --- | --- |
| `stardew_fixture_status_get` | Any verified role; owned fixture state |
| `stardew_fixture_building_ensure` | Single/host; idempotent building preparation |
| `stardew_fixture_object_ensure` | Single/host; owned object |
| `stardew_fixture_object_clear` | Single/host; remove the exact owned object |
| `stardew_fixture_animal_ensure` | Single/host; compatible owned animal |
| `stardew_fixture_enter` | Any verified role; natural entry to an owned interior |
| `stardew_fixture_enter` with `greenhouse` | Any verified role; exact loaded Greenhouse's natural entry |
| `stardew_fixture_farm` | Any verified role; allowed natural Farm exit |
| `stardew_fixture_fishing_prepare` | Single/host; prepare a collision-free bank and selected rod in the current outdoor location |

Every invocation revalidates current review/process, load phase, fixture/Save identities, ownership, and role. The operations are unavailable in plain single reviews, smoke sessions, or foreign/unverified saves. Require the GameSide result and direct effect evidence.

### Fishing preparation

This fixture action replaces the bank-search and rod-preparation
helpers needed during Clearer Waters development. It requires the verified main
player/host, an idle movable player, no menu/event/fade, an outdoor map no larger
than 512x512, and enough empty inventory slots. It finds the standing position nearest to the player, or to an explicit search start given as `x` and `y`, using
the game's farmer collision check and natural fishable water two tiles away;
building fish ponds are excluded. It does not clear obstacles or change maps.

An already selected rod is retained, including its bait and tackle. Otherwise a
Bamboo Pole is placed in an empty slot and selected. One additional empty catch
slot is required. The player is positioned and faced toward the reported water.
No existing inventory item is overwritten. With `skipTutorial` only, an empty
fish collection gets one Sunfish entry to bypass the forced first-fish tutorial;
the result explicitly reports this synthetic prerequisite. No catch callback or
inventory fish is created. Omitting it preserves tutorial behavior.

```powershell
& $sdvkit project review call stardew_fixture_farm --allow-fixture-actions
& $sdvkit project review call stardew_fixture_fishing_prepare '{"skipTutorial":true}' --allow-fixture-actions
& $sdvkit project review status --topology single --json
& $sdvkit project review call stardew_input_press '{"button":"C"}' --allow-input
```

Require the tool's `Fishing prepared` result and
check the reported player location/selected rod before casting. Preparation is
not evidence of a successful cast or catch. Read the additive
[fishing snapshot](runtime-state.md#fishing-observations) during play. The normal
one-second sample can miss a short nibble; it is not an automatic bite responder.
Natural bite timing, selection, minigame, landing and inventory pickup still need
their own evidence. This command neither saves nor resets; use the normal final
stop/reset workflow. Farmhands are rejected for preparation but can read their
own fishing state. No new MCP fixture-mutation tool is added.

### Building and animal preparation

Run `fixture farm` before creating a building: the main player must be on the Farm. Kinds resolve from live canonical BuildingData/FarmAnimal IDs, not localized labels. Tokens are case-insensitive and separator-normalized; aliases such as `deluxe-barn` and `white-cow` remain compatible. Unknown or colliding kinds, unplaceable data, incompatible animal houses, and capacity failures reject the request before mutation.

The building placement area includes its footprint, additional placement tiles, and human-door access tile. Preflight checks map rules, buildings, players, characters, animals, large terrain features, and furniture removability. Only inside that disposable area does preparation remove tile-bound Farm objects, ordinary terrain features, overlapping resource clumps, and safely removable furniture. Object IDs and `modData` do not decide those removals; content outside the area is untouched. Results count removed categories. A later placement failure requires fixture reset before retrying.

Ensure operations reuse the same owned alias and requested state idempotently. Animal ensure does not convert or move an existing animal. `object clear-owned` is separately marker-strict; it never clears an entire building collection or removes an unowned object. Status reports generic fixture facts, not target-specific state or foreign `modData`.

### Navigation and save

Farm navigation follows a natural non-NPC exit from the review FarmHouse, exact Greenhouse, or an owned fixture interior. The exact `greenhouse` entry token selects the loaded canonical Greenhouse; use its GUID to enter an owned building whose alias is also `greenhouse`. Neither command is a general warp interface.

Console fixtures have no save/sleep command. Use a selected mod/companion's supported interface or the separately enabled [MCP fixture-save tool](mcp.md#opt-in-fixture-actions), and verify its completion. Saving alone does not prove persistence: test a real stop/restart when making that claim.

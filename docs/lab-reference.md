# Lab lifecycle reference

Use this page to interpret process, staging, fixture and cleanup results. For commands in order, use the [live guide](live-review.md). The [capability matrix](README.md#capability-matrix) lists supported targets and roles. The [glossary](glossary.md) defines terms such as owned, staging and test save.

## Process and profile lifecycle

SDVKit discovers one ready installation and builds GameSide (called AlwaysOn before 2.0) against its assemblies. It then launches `StardewModdingAPI.exe` directly, with SMAPI's absolute `--mods-path`. Game binaries and content stay in the installed game folder; SDVKit does not copy them.

| State | Location relative to the lab root |
| --- | --- |
| Single mod group | `.sdvkit/lab/single/mods` |
| Network mod groups | `.sdvkit/lab/network-2/{host,farmhand}/mods` |
| Single user profile | `.sdvkit/lab/profiles/single` |
| Network user profiles | `.sdvkit/lab/profiles/network-2/{host,farmhand}` |
| Registered test save | `.sdvkit/lab/single/test-save` |

Each process gets its own profile, which holds Stardew and SMAPI preferences, saves, screenshots and standard logs. GameSide checks the game's Stardew data path before it activates. Normal saves and Mods are not selected or read automatically.

This is process and data isolation, not an OS sandbox. Tested code and external services can still reach shared machine resources.

### Several lab roots on one PC

Each lab root holds its own profiles, mod groups, test saves, ownership records, status and stop files and its operation lock below its `.sdvkit/`. The command endpoint uses a dynamic loopback port with a per-launch token. Separate lab roots can therefore run reviews at the same time against the same read-only game folder, one owner per lab root; this was verified with two lab roots, a third partly overlapping. A second command in the same lab root while an operation runs there still reports `labBusy`; another lab root does not.

Verified on 2026-10-21 with Stardew 1.6.15 and SMAPI 4.5.2 from one Steam installation ([#322](https://github.com/Nana1873/SDVKit/issues/322)): two `single --test-save` reviews in two lab roots started, took interleaved input presses, game-menu opens and closes and runtime reads, and stopped and reset cleanly, without `inputForegroundChanged`; neither lab's status, `doctor` or results named the other lab, and the protected folders stayed unchanged. `shop.ps1` and `container.ps1` then passed concurrently in the two roots while a third lab root's review also ran. Not covered: `network-2` or split-screen next to another lab, sustained runs of more than two labs, slower machines, where CPU and GPU load can lengthen loading past the documented waits, other Steam client states (offline, updating, not running) and non-Steam installations.

### Identity and exit

Ownership binds the process ID, its UTC start time, the executable identity and a matching game-side marker. Status rechecks that binding on every read. Stop sends that launch's stop request and waits for the process handle and the terminal marker. It clears ownership only after a confirmed exit. With an unknown identity, a missing terminal marker or a timeout, the process record stays.

Each status read opens the marker again:

- A missing marker reports `pending`.
- An unreadable or malformed marker reports `invalid`.
- A marker for another launch or process reports `mismatch`.
- An active marker older than five seconds is stale. Loading a save blocks the game's main thread and can pause the heartbeat for several seconds; `lab status` and `project review status` then report `gameSideStale` until the next update, so poll again before treating it as a failure; `lab test-save` waits up to 30 seconds after the marker becomes stale, and so does a `network-2` smoke or review start while the host is still loading its exact fixture (`networkTwo.phase=waitingForFixture`, test-save phase not yet terminal).

A later readable snapshot can recover on the next call after the same identity and freshness checks. SDVKit does not substitute a cached snapshot for a failed read. A partial or mismatched marker does not count as ready.

GameSide turns off `pauseWhenOutOfFocus` in its isolated profile while it runs. On a controlled exit it tries to restore that option and read it back. It then publishes `exiting` or `restoreFailed` and asks the game to exit normally in both cases.

- Process exit without a matching terminal marker returns `cleanStopNotConfirmed`. Ownership stays for inspection.
- A published `restoreFailed` marker after an otherwise safe exit is a warning. The next start applies the lab option again.
- Crashes and manual or forced termination do not promise a restore.

Normal stop has no process-name search or kill fallback. One case differs: Windows may fail to establish the identity of a newly created child. Start then aborts that child through its original creation handle before it returns.

### Command endpoint

The CLI and native MCP send every review command line to a loopback endpoint in the game process, not to the SMAPI console.

A command runs on the game thread of the screen that SMAPI's `screen=<id>` argument selects. Results:

- If the game does not start the command within 5 seconds, it is withdrawn and reported as not run. This also happens while the game saves or starts a new day, because SMAPI pauses the update events that run commands.
- A missing screen, an unknown subcommand or a failed screen binding is rejected without running the command.
- More than eight simultaneous connections are refused.
- Every reply names its launch. The CLI rejects a reply from another launch.

A line that does not start with `sdvkit`, such as a mod console command or SMAPI's `help`, runs through SMAPI's own command manager on the selected screen, as if typed. The isolated SMAPI log records it. Like typed commands, it gets no SDVKit screen or binding check. A failing command reports `reviewCommandFailed`. An unknown command reports `reviewCommandRejected`.

Command results stay in the game process. Nothing is written to the runtime folder. [Reference details](#reference-details) covers the token and response mailbox.

## Disposable baseline and smoke

Run `lab test-save --topology single` while all roles are stopped. The first run creates the registered world through Stardew's normal new-game and save flow. Later runs reuse that baseline.

The first run:

1. Exposes only the generated Save ID inside the isolated profile, through a verified junction.
2. Confirms stop and unmount, then captures the baseline.
3. Restores a work copy and runs the scenario.

Before the scenario changes anything, the loader verifies the Save ID and unique game ID. It also checks the player, farm and favorite-thing identities, the ownership markers, and the expected single main-player state. The single scenario shows its HUD message and observes 120 real update ticks. Completion needs the game-side marker, a confirmed exit, junction removal and a byte-for-byte baseline reset. An existing entry at the generated fixture path blocks preparation. SDVKit does not replace it.

Automation uses the [supported version bands](../README.md#requirements) plus runtime capability and signature probes. A changed or missing required API stops automation before it proceeds.

### Local host and farmhand

`lab smoke --topology network-2` reuses the prepared baseline. It starts one local host and one farmhand, with separate profiles and the same GameSide build. A pass needs a real join, mutual identities, and 120 consecutive verified ticks while connected, unfocused and with GameSide active. Each role reports its own process and foreground observations and logs.

Teardown stops the farmhand, then the host, verifies exit and unmount, and resets the work fixture. A failure in join, identity, background progress, exit, mount cleanup or reset blocks. A pass proves that local two-role scenario only.

## Project smoke identity and teardown

`project smoke` selects one standalone C# manifest and project. It uses the existing inspector, Release builder and validated package. Unsupported project shapes, the reserved `SDVKit.GameSide` ID or missing required dependencies produce controlled failures. Optional dependencies may be absent.

The source project's `.sdvkit/` holds build and package output. The current folder holds live state. The validated ZIP is checked again before extraction and staged next to GameSide. Network roles receive the same file set. Results keep the package SHA-256, the staged `buildIdentity`, the manifest's `declaredVersion`, and SMAPI's normalized `version` (for example, `1.0` becomes `1.0.0`).

After `GameLaunched`, SMAPI's loaded-mod registry confirms the expected target ID and version in each role. The test-save, tick and teardown steps then run. Acceptance needs `state=passed`, matching role identities, enough ticks, `fixtureReset=true` and `stagingRemoved=true`. This does not measure the DLL in memory or prove all target features.

### Drift versus cleanup authority

During start, status and evidence checks, one difference is accepted for a code mod: a newly created regular root `config.json`. Other changes show as drift. Drift can block replacement or an unqualified build-identity claim.

After the process is confirmed stopped, regular file drift does not block cleanup of the staging selection. Cleanup authority comes from the ownership marker, the expected direct-child paths and a tree without reparse points. These still block changes: foreign folders, path or ownership drift, reparse points, unreadable processes or uncertain exits. A matching stale marker is not replaced automatically. A later call cannot assume that every child of an earlier failed launch has stopped.

## Review staging and retained state

Review prepares C# builds, intermediates and packages in its temporary `review-prepared` tree. Ready mod folders and content packs get plain-tree checks instead. These reject source projects, nested manifests, saves, secrets, executables, archives, game assemblies and reparse points. Before launch, selected dependencies need matching identities, minimum versions and providers, unique staging names and no collisions.

For a [ready bundle](live-review.md#select-a-ready-bundle), preparation also rejects unknown nested mods, unselected or missing members, source projects, links, unsafe paths, missing DLLs or providers, and duplicate IDs or staging names. Code and packs use separate artifact roles, file hashes and cleanup ownership. The target's source root binds the selected outer folder. Its staging path identifies only the code member. Packs are left out of that code copy and staged once each.

Reports name each artifact's role (target, companion or pack), kind, ID, declared and normalized version, provider and file-set identity. The process binding controls cleanup. A confirmed exit may be finalized on the next status, stop or start. An unreadable process stays uncertain.

An interactive review owns a separate Windows console instead of captured lab output. SMAPI logs and screenshots still belong to the isolated profile. A plain single review keeps its own isolated saves. `--test-save` selects the registered work fixture. A review does not end on its own after 120 ticks.

What stop and reset keep is listed in [finish or test persistence](live-review.md#finish-or-test-persistence). The reset contract for the isolated profile's options: a `single` or `network-2` reset sets `languageCode` in each of its profiles' `startup_preferences` back to `en`, the option the title-screen language change touches. A documented flow can also change Gamepad Mode on the game's Options page through review input; the reset does not restore that, only `languageCode`. A new profile has no `startup_preferences` until Stardew writes it; SDVKit assumes it starts in Stardew's default, `en`, as observed with Stardew 1.6.15. Every start forces the window options to windowed 1280x720. Other options stay as the game wrote them. The reset checks every profile's file and the fixture before it changes anything: a file that is not valid XML or lacks exactly one `languageCode` element blocks the reset with `reviewFixtureResetFailed`. The reset and `lab test-save` also remove the [stale fixture copy](live-review.md#stale-fixture-copy) that SDVKit 2.1.0 and earlier left in old labs.

### Retained selection

A review cannot switch to another `--project` in the same root while it is retained. Started or retained reviews also reject a changed outer bundle folder, companion or pack selection. Stop and reset the review before you change its selection. A `network-2` restart must repeat the same target, project, companions and content packs.

The ownership marker also records `gamePath`. On a `network-2` restart after stop, the installation has to match, whether selected with `--game-path` or discovered. Older retained staging without an installation binding stays readable for status, stop and reset. Reset it and build again before a restart.

## Fixture command reference

Each fixture operation is a native MCP tool. Call one from the command line with [`project review call`](mcp.md#call-a-tool-from-the-command-line) and `--allow-fixture-actions`. For `network-2`, add `--topology network-2` and one `--role`. Arguments are in [opt-in fixture actions](mcp.md#opt-in-fixture-actions), together with the lab tools for time, weather, season, warp, zoom and window.

| Tool | Role and result |
| --- | --- |
| `stardew_fixture_status_get` | Any verified role; fixture state |
| `stardew_fixture_building_ensure` | Single or host; idempotent building preparation |
| `stardew_fixture_object_ensure` | Single or host; fixture object |
| `stardew_fixture_object_clear` | Single or host; remove that fixture object |
| `stardew_fixture_animal_ensure` | Single or host; compatible fixture animal |
| `stardew_fixture_enter` | Any verified role; natural entry to a fixture interior |
| `stardew_fixture_enter` with `greenhouse` | Any verified role; natural entry to the loaded Greenhouse |
| `stardew_fixture_farm` | Any verified role; allowed natural Farm exit |
| `stardew_fixture_fishing_prepare` | Single or host; prepare a collision-free bank and a selected rod in the current outdoor location |
| `stardew_fixture_item_add` | Single or host; put one new item stack into the first empty backpack slot |
| `stardew_fixture_npc_place` | Single or host; warp one named villager to a free tile in the player's location |

Every call rechecks the review, process, load phase, fixture and Save identities, ownership and role. The operations are unavailable in plain single reviews, in smoke sessions, and in foreign or unverified saves. Require the GameSide result and direct evidence of the effect.

### Fishing preparation

This fixture action replaces the bank-search and rod-preparation helpers needed during Clearer Waters development. It requires:

- the verified main player or host;
- an idle player who can move, with no menu, event or fade;
- an outdoor map of at most 512x512;
- enough empty inventory slots.

It finds the standing position nearest to the player, or to a search start given as `x` and `y`, with natural fishable water both two and three tiles to its left or right. The position passes the game's farmer collision check and is centered on the bank tile; `minimumCastWater` reports the water two tiles out. Building fish ponds are excluded at both checked tiles. It does not clear obstacles or change maps.

The two checked water tiles cover unsteered side casts up to about `castingPower=0.9` at Fishing level 0 (roughly 55 ticks at 60 Hz on the initial increasing charge). Higher Fishing levels add distance and need a shorter charge; a tick count is not a fixed power guarantee. Longer casts can reach unchecked water or land. Down-facing banks are skipped because short casts sometimes spent stamina without starting fishing; the cause remains undetermined (#305). Up-facing casts share the vertical path that lands after the farmer's cast animation, so they are also excluded without live verification. If no side-facing bank has both water tiles, preparation fails without changing the fixture.

An already selected rod is kept, with its bait and tackle. Otherwise a Bamboo Pole is placed in an empty slot and selected. One more empty slot is required for the catch. The player is positioned and faced toward the reported water. No existing inventory item is overwritten.

With `skipTutorial`, an empty fish collection gets one Sunfish entry to bypass the forced first-fish tutorial. The result reports this synthetic entry. No catch callback runs and no fish enters the inventory. Without it, tutorial behavior is unchanged.

```powershell
& $sdvkit project review call stardew_fixture_farm --allow-fixture-actions
& $sdvkit project review call stardew_fixture_fishing_prepare '{"skipTutorial":true}' --allow-fixture-actions
& $sdvkit project review status --topology single --json
& $sdvkit project review call stardew_input_press '{"button":"C"}' --allow-input
```

Require the tool's `Fishing prepared` result. Check the reported player location and selected rod before casting. Preparation is not evidence of a cast or catch.

Read the additive [fishing snapshot](runtime-state.md#fishing-observations) during play. Its normal one-second sample can miss a short nibble; it does not respond to bites. Bite timing, fish selection, the minigame, landing and inventory pickup need their own evidence.

This command neither saves nor resets; finish with the normal stop and reset. Farmhands cannot prepare but can read their own fishing state.

### Item preparation

`stardew_fixture_item_add { "qualifiedItemId": "(O)24", "stack": 5, "quality": 2 }` gives the main player items to test with: food to eat, a gift, crafting ingredients, an object to place, a modded item for a machine. It requires:

- the verified main player or host;
- no open menu or event, no tool in use and no item held by the cursor;
- an item that exists in the loaded item data, including items added by mods;
- a stack of 1 to the item's own maximum (at most 999);
- a quality of 0 (normal, the default), 1 (silver), 2 (gold) or 4 (iridium); a quality above 0 only for objects, which are the items that keep one;
- a qualified ID without spaces;
- an empty backpack slot.

It creates a new stack and puts it into the first empty backpack slot. It never merges with or replaces an existing stack; every refusal, including a full backpack, leaves the backpack unchanged. The result reports `item.slot` and the `item.inventoryRevision` right after the change.

To act on the item, select its slot with its number key (toolbar slots 0 to 11 only; move an item from a lower row first). Selecting changes the revision, so read `stardew_inventory_get` again and pass that `inventoryRevision` to `stardew_world_act` or `stardew_world_interact`. The fixture is not idempotent: after a timeout that may have run, read the inventory before calling it again.

```powershell
& $sdvkit project review call stardew_fixture_item_add '{"qualifiedItemId":"(BC)130","stack":1}' --allow-fixture-actions
```

This is test setup, not evidence of game behaviour. The command neither saves nor resets; finish with the normal stop and reset.

### NPC placement

`stardew_fixture_npc_place { "name": "Lewis", "x": 65, "y": 18 }` brings a villager to a tile in the main player's current location, usually next to the player, so dialogue and gift tests do not depend on schedules. It requires:

- the verified main player or host;
- no open menu, event, festival or fade;
- the internal name of a villager that is in the world now and visible (`Game1.getCharacterFromName`, exact and case-sensitive), as listed in `npcs` of [runtime state](runtime-state.md#field-contract);
- a permanent location (not a mine or volcano level or another temporary location, which can unload with the villager in it);
- a tile that is not the player's own and where the game can spawn a character (`GameLocation.CanSpawnCharacterHere`). A tile that `stardew_world_area_get` shows empty can still be refused, because the area read has no passability or building-layer data. The refusal is `fixtureActionRejected` with the text "Tile x,y in Farm is not free for a character; nothing moved."; try a neighbouring tile. On the test farm, 65,16 was refused and 65,18 accepted.

It warps the villager with the game's own `Game1.warpCharacter`, stops its current path and square walk, and turns it toward the player. The result reports the location and tile in `navigation`. The villager can move again at any time, for example when its schedule continues, so read `npcs` before you act on it.

```powershell
& $sdvkit project review call stardew_fixture_npc_place '{"name":"Lewis","x":65,"y":18}' --allow-fixture-actions
```

To talk, select a tool or an empty slot and use `stardew_world_act` `check` on the villager's tile; the `DialogueBox` in `stardew_menu_get` and the friendship points from `stardew_progression_get` are the proof. To give a gift, select the item first (for example one from `stardew_fixture_item_add`). A `DialogueBox` closes by clicking it, not with Escape; each click finishes the text or turns the page.

This is test setup, not evidence of game behaviour. The command neither saves nor resets; finish with the normal stop and reset.

### Building and animal preparation

Run `fixture farm` before you create a building, so the main player is on the Farm. Kinds resolve from live BuildingData and FarmAnimal IDs, not localized labels. Tokens ignore case and separator style. Aliases such as `deluxe-barn` and `white-cow` still work. Unknown or colliding kinds, unplaceable data, incompatible animal houses and capacity failures reject the request before any change.

The building's placement area covers its footprint, extra placement tiles and the tile in front of its human door. A preflight checks map rules, buildings, players, characters, animals, large terrain features and whether furniture can be removed. Only inside that area does preparation remove tile-bound Farm objects, ordinary terrain features, overlapping resource clumps and removable furniture. Object IDs and `modData` do not decide these removals. Content outside the area is untouched. Results count the removed categories. After a later placement failure, reset the fixture before retrying.

Ensure operations reuse the same fixture alias and requested state, so repeating them is safe. Animal ensure does not convert or move an existing animal. `object clear-owned` checks its marker strictly. It removes only objects that carry SDVKit's fixture marker, never a whole building collection or any other object. Status reports generic fixture facts, not target-specific state or foreign `modData`.

### Navigation and save

Farm navigation follows a natural non-NPC exit from the review FarmHouse, the Greenhouse, or a fixture interior. The `greenhouse` entry token selects the loaded Greenhouse. To enter a fixture building whose alias is also `greenhouse`, use its GUID. Neither command is a general warp interface.

Console fixtures have no save or sleep command. Save through a selected mod's or companion's supported interface, or the separately enabled [MCP fixture-save tool](mcp.md#opt-in-fixture-actions). Verify its completion. Saving alone does not prove persistence: test a real stop and restart before you claim it.

## Reference details

**Status publication.** Status is a complete JSON snapshot, published by an atomic rename in the same folder. An open reader can finish the old snapshot; later opens see the new one. Each read still checks the launch, process ID and start time, nested role and build identity, JSON size and freshness. On Windows, publication uses `FileRenameInfoEx` with POSIX replacement semantics (Windows 10 version 1709 or later). Windows refuses that replacement with error 5 while a kernel-mode hold (observed as the System process; the holder's identity is not proven) keeps the previous snapshot open, and a delayed replacement leaves the name absent for tens of milliseconds. In that one case, when the destination is openable and not read-only, GameSide moves the held snapshot aside, renames the completed snapshot to the destination name and removes the aside name immediately, so the name is absent for a moment but every successful open yields a complete snapshot. The reader re-reads an absent or delete-pending name for up to 250 ms before it reports `pending`; every read of an absent status therefore takes up to 250 ms, and a network-2 status read with both role markers absent up to 500 ms. Denied writes, read-only targets, sharing conflicts and filesystems without that operation stay visible as errors; nothing is retried. If removing the aside name fails, the new snapshot stays published and a leftover `status.json.replaced` remains until the next fallback replaces it; ordinary publications are unaffected, and GameSide logs the leftover once per period as a warning. The first publication through the fallback in a game process is logged once at info level ("see #136"). GameSide logs the first error of each failure period, and a later successful tick can publish fresh status again. A failed final publication at exit is logged separately and does not hold the game open.

**Command transport.** Each launch gets a random token through its environment. GameSide listens on a random loopback port and publishes it as `httpPort` in its status. It accepts only `POST /v1/command` from `127.0.0.1` with that token, the matching `Host` header and no `Origin` header. GameSide keeps each response in an in-memory mailbox. The CLI takes it once through `GET /v1/response/<name>` with the same token. The mailbox holds at most 64 responses and 32 MiB, and drops a response after two minutes.

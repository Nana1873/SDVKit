# Changelog

User-visible changes are maintained here. GitHub release notes use the corresponding version section; detailed build, artifact, and live-test evidence belongs in the linked release acceptance. Dates are publication dates in UTC. Historical entries summarize what shipped then, not current limitations or pending-test status.

## [Unreleased]

### Added

- `scripts/acceptance/player-actions.ps1 -Topology network-2 -Role <host|farmhand>` and `-Screen <id>` run the input sections (walking, hotbar keys, game menu with tab switch, inventory move by two clicks, Escape) through the selected network role or split-screen screen (1 or higher), with every tool call and menu, inventory and world read bound to that selection and the runtime and inventory reads asserting the selected player (#299). Sections that need map reads or fixture tools the selection lacks are skipped with a console note, while a role run still uses the farm and warp fixture tools as setup; such a run is not full acceptance. Evidence goes to `player-actions-<role>` or `player-actions-screen<id>`, and the topology the run started is stopped and reset at the end. The shared helpers accept a role or screen selection and wait for a ready `network-2` pair.
- `project review act move <up|right|down|left> [--turn-only]` and the MCP tool `stardew_world_move` (opt-in `--allow-world-actions`, test save; roles and screens as below) walk the player exactly one tile in a direction, or only turn it, and report the reached `locationId`, `tileX`/`tileY`, `position` and `reached` (#293). SDVKit holds the game's own movement key through its process-local input until the player stands past the centre of the next tile, then releases it, so repeated moves land one tile apart where a timed chord moved 2 or 3 tiles. A target that is off the map or blocked for the player is refused with `worldMoveTargetBlocked` before any input; a warp on the target changes the location instead. There is no pathfinding.
- `scripts/acceptance/gmcm-checkbox.ps1` proves a GMCM checkbox live: the toggle stays unsaved, Save writes `Enabled=true`, `config-reconcile` accepts it and a restart with the saved config warps to Farm 64,15 (#284). It shares its GMCM steps with `gmcm-number.ps1` through new helpers in `SdvKitAcceptance.psm1`. `docs/gmcm-authoring.md` now keeps only the checkbox contract and API notes; the steps are in `docs/test-a-gmcm-option.md`.
- `scripts/acceptance/container.ps1` proves `stardew_container_transfer` and `stardew_container_get` live: a one-time reward, an exact deposit and withdrawal, a stale request refused with `containerTransferSelectionStale`, and the chest contents after a fixture save, stop and restart (#284). `docs/inventory-container-authoring.md` is replaced by `docs/test-a-container-transfer.md`.
- `scripts/acceptance/data-migration.ps1` proves a save-data migration live with the three builds of the data-migration sample: v1 saves, a broken v2 refuses and keeps the data, the corrected v2 migrates once without a second reward, and a restart loads the existing ledger (#284). `docs/test-a-data-migration.md` carries the steps; `docs/data-migration-authoring.md` keeps the schema and behaviour table and the manual missing/future-data cases.
- `stardew_world_interact`, `stardew_world_act`, `stardew_world_move` and `stardew_container_transfer` (opt-ins `--allow-world-actions` and `--allow-container-transfer`, test save) now also work for the `network-2` host and farmhand and for split-screen screens, acting as the player the MCP server or `project review call` is bound to (#297). The command names the role or screen, a screen-bound command carries the frozen farmer and context, and the game refuses it on any other process, role or screen or when the fixture is not verified for that player; results name the role that acted. Stardew forces gamepad controls on screens other than 0, so `stardew_world_act` is refused there with `worldActionCursorAimUnavailable`. The direct CLI commands stay single. `player-actions.ps1` gains `interact` and `transfer` sections and runs `move`, `act`, `interact` and `transfer` for a role (a farmhand run gets its chest, parsnips and planted plot from the host) and `move` plus the `act` and `interact` refusals on a screen.

- `scripts/acceptance/progression.ps1` proves NPC, mail and quest reads live through a reload (#284): the sample's wrong 750-point condition keeps both effects away from 500 seeded Leah points, the corrected threshold delivers one mailbox entry and one quest, the native mailbox action receives the letter, the objective completes the quest once, `stardew_fixture_npc_place` puts Leah next to the player and talking raises her friendship, and two restarts leave every progression read unchanged. `docs/test-a-progression-read.md` carries the steps; `docs/progression-authoring.md` keeps the read-only contract.

### Fixed

- The reader test for a transiently absent `status.json` name no longer races a 40 ms timer against the 250 ms window; it restores the name on the reading thread's third wait through a test-only hook, so a busy machine or CI runner cannot turn it into a false failure (#333). The reader contract is unchanged.
- GameSide now makes the held-destination status fallback (#136) visible (#328): the first publication through it in a game process is logged once at info level, and a failed removal of the `status.json.replaced` aside name (publication stays successful) is logged once per period as a warning instead of passing silently.
- `project review start --topology network-2` (and the network-2 smoke) no longer stops the host with `gameSideStale` when the host's fixture load blocks the game for more than five seconds (#325). While the host's exact status shows `networkTwo.phase=waitingForFixture` and a non-terminal test-save phase, the start keeps polling for up to 30 seconds after the marker becomes stale, as `lab test-save` does since #303. Any other problem, an identity mismatch, a terminal phase or a longer stall still fails as before.
- The sporadic status publication error 5 (#136) has a reproduced cause, and the reader's momentary `pending` or `invalid` report (#143) is a reproduced symptom attributed to the same cause: a kernel-mode hold (the System process; the holder's identity is not proven) on the previous `status.json` makes Windows deny the in-place replacement for up to several seconds, and a delayed replacement leaves the name absent for tens of milliseconds. When the in-place replacement is denied for a destination that is openable and not read-only, GameSide now moves the held snapshot aside, gives the completed snapshot its name and removes the aside name at once; open readers keep the old bytes and later opens see the new ones. The reader re-reads an absent or delete-pending name for up to 250 ms before reporting `pending`, so a read of an absent status now takes up to 250 ms. Read-only targets, sharing conflicts and other errors stay visible unchanged, and no publication is retried.

## [2.1.0] - 2026-10-21

### Added

- The local player runtime data has an optional `input` object: `gamepadControls`, `gamepadMode` (`auto`, `forceOn`, `forceOff`), `mouseCursorVisible` and `gamepadConnected`, so evidence states which input mode a test used (#271). It is absent in status from an older GameSide.
- World-area reads expose an optional per-tile `feature` for trees, fruit trees, grass, bushes, resource clumps, giant crops and furniture, including bounded tool-effect facts and the origin of multi-tile features (#295). A feature that cannot be read makes only its tile `unavailable`. Older responses without the field remain valid. Verified live: an axe hit lowers tree health and the scythe cuts grass blades; furniture, giant crops and boulders are not yet verified live.
- `scripts/acceptance/player-actions.ps1 -Only <sections>` runs the shared setup and only the named sections while developing SDVKit (#308). Partial runs keep their evidence under `player-actions-partial` and never count as acceptance.
- The local player runtime data has an optional `npcs` list: the visible villagers in the player's current location with internal and display name, tile and facing, at most 64 (#292). It is available to every role and screen, and absent in status from an older GameSide.
- `stardew_fixture_npc_place { name, x, y }` (opt-in `--allow-fixture-actions`, test save, single or network-2 host) warps one named villager to a free tile in the player's location, stops its path and turns it toward the player, so dialogue and gift tests do not depend on schedules (#292). It is test setup, not proof of game behaviour.
- `stardew_fixture_item_add { qualifiedItemId, stack, quality? }` (opt-in `--allow-fixture-actions`, test save, single or network-2 host) puts one new stack of an existing item, including modded items, into the main player's first empty backpack slot (#291). It never merges or replaces, refuses with a full backpack, and reports the slot and the new inventory revision. It is test setup, not proof of game behaviour.
- `project review act <check|useTool> <x> <y> <inventory-revision>` and the MCP tool `stardew_world_act` (opt-in `--allow-world-actions`, test save, single) press the action button or use the selected tool once on a tile next to the player (#290). SDVKit puts the virtual cursor on the tile, so the game starts there instead of on a tile picked from where inside its tile the player stands; Stardew's own fallbacks (nearby tiles, the held item, weapons and the rod acting in a direction) still apply. Targets that are not next to the player are refused with `worldActionTargetNotAdjacent`, and requests the game would not aim by cursor (gamepad controls, hidden mouse) with `worldActionCursorAimUnavailable`.
- `project review map actions <map>` and `stardew_map_actions_list` list bounded, paged `Action` and `TouchAction` tile properties so tests can discover property-backed signs, action doors and the town mailbox (`TownMailbox`) (#300). The Farm mailbox is handled by game code and is not discoverable through this map-property query. Entries preserve direct or tile-index/frame identity, raw strings and their first token; the read-only query is available in unbound single reviews.
- The local player runtime data has a `position` object with `facing` (`up`, `right`, `down`, `left`), the sub-tile `standingX`/`standingY`, Stardew's grab tile for the action button and the tile a tool would hit (`toolTileX`/`toolTileY`) (#289). It is optional, so status from an older GameSide stays readable.
- `docs/player-actions.md` lists the everyday keyboard-and-mouse player actions with the tool to perform each and the state read that proves it, and links each gap to its issue (#270). `scripts/acceptance/player-actions.ps1` verifies the supported ones live: walking and running, warps, hotbar keys, the game menu and an inventory move, hoe, pickaxe, scythe and a fishing cast. Acceptance scripts keep the game's SMAPI log of a failed run in their evidence.

### Fixed

- The lab starts the game window without giving it focus (SDL's `SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN`, set in the game's environment) (#311). Switching between other windows during a review or an acceptance run no longer makes the next input fail with `inputForegroundChanged`; the game window becoming the foreground window (a click into it, Alt+Tab, or Windows activating it after the window in front closes) still does.
- `lab test-save` no longer fails with `gameSideStale` when loading the test save blocks the game for more than five seconds: while the exact test save is still loading or waiting, it keeps polling for up to 30 seconds after the marker becomes stale (#303). The acceptance scripts' readiness wait does the same.
- Fishing preparation now requires natural fishable water both two and three tiles out from the nearest collision-free bank facing left or right. It skips vertical banks because down-facing short casts sometimes spent stamina without starting fishing; the cause remains undetermined. If no qualifying side-facing bank exists, the fixture stays unchanged (#305).
- `lab test-save` now accepts an exact, verified completed scenario from a stale active status marker and then requests its normal clean stop. A passing scenario no longer remains failed as `gameSideStale` after successful cleanup; incomplete or mismatching evidence and an unconfirmed or timed-out stop still fail (#303).

[Release acceptance and publication status](https://github.com/Nana1873/SDVKit/issues/320) · [Release](https://github.com/Nana1873/SDVKit/releases/tag/v2.1.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v2.0.0...v2.1.0)

## [2.0.0] - 2026-10-11

### Added

- `sdvkit lab init [<lab-root>] [--force] --json` prepares a lab root for Claude Code and Codex (#248). It writes `SDVKIT.md` (the lab guide), `KNOWLEDGEBASE.md` (sourced SMAPI 4, Stardew 1.6, Content Patcher and GMCM pitfalls), ten task skills in `.claude/skills/` and `.agents/skills/`, a Claude Code hook that blocks writes to the game folder (including its `Mods`), the normal `Saves` folder, `*.xnb` files and game binaries, a `sdvkit.cmd` launcher and an MCP template. It creates `CLAUDE.md`, `AGENTS.md`, `.claude/settings.json`, `.gitignore` and `global.json` only when missing. Run it again after updating SDVKit: unchanged files are updated and locally edited ones are kept unless `--force`. The README's agent setup prompt, also in `SETUP_PROMPT.txt`, runs it when the downloaded release offers it.
- Live acceptance scripts in `scripts/acceptance/` (#267): `shop.ps1`, `crop-machine.ps1` and `gmcm-number.ps1` run the shop purchase, machine rule and GMCM slider workflows end to end against the real game, stop at the first unmet check, always stop and reset, and keep their evidence below the lab root. Short guides replace the long recipes: [test a purchase in your shop](docs/test-a-shop-purchase.md), [test a machine rule](docs/test-a-machine-rule.md) and [change and verify a GMCM option](docs/test-a-gmcm-option.md). The sample mods now live in `docs/examples/`.
- `docs/glossary.md` defines the documentation's recurring terms (owned, role, screen, test save, opt-in, evidence levels) once (#249). The capability matrix in `docs/README.md` is split into toolkit, lifecycle, observation and action tables.

### Removed

- **Breaking (2.0):** the repository's `.agents/skills/sdv-project-review` and `sdv-project-smoke` skills are replaced by the task skills in `agent/skills/` that `lab init` installs (`sdv-live-review`, `sdv-project-smoke` and others).

### Changed

- GameSide now ships as `SdvKit.GameSide.dll` plus `SdvKit.Shared.dll`. The portable ZIP includes `src/SdvKit.Shared` for source builds. Build identities change because they include the shared assembly (#250).
- **Breaking (2.0):** SDVKit's game-side SMAPI mod is now called GameSide instead of AlwaysOn. SMAPI shows it as "SDVKit". Its mod ID is `SDVKit.GameSide`, its assembly `SdvKit.GameSide.dll`, and its source lives in `src/SdvKit.GameSide`. "Always on" now names only the background-run behavior it provides.
- **Breaking (2.0):** JSON output follows the new name. `lab status` and network-2 reports use `gameSide` instead of `alwaysOn`; problem codes such as `alwaysOnPending` are now `gameSidePending`; the mod-asset coverage scope is `observedRequestsSinceGameSideSubscribed`. Build identities change because the assembly file name is part of the hash.
- `SDVKit.AlwaysOn` stays reserved: user mods with that ID are still rejected.
- **Breaking (2.0):** review commands no longer reach the game by typing into the SMAPI console. Each launch now opens a token-protected loopback command endpoint in the game-side mod, and the CLI and native MCP send command lines there. A command reports `commandWritten=true` only after it ran on the game thread, `false` when it did not run, and no value when delivery is unknown. The console-delivery problem codes (`reviewConsoleInputBusy`, `reviewConsolePartialWrite`, `reviewConsoleWorker*` and similar) are replaced by `reviewCommandEndpointUnavailable`, `reviewCommandRejected`, `reviewCommandNotExecuted`, `reviewCommandScreenUnavailable`, `reviewCommandFailed` and `reviewCommandDeliveryUnknown`. Manual use of the SMAPI console window no longer interferes with SDVKit commands. Any SMAPI console command still works through `project review command`, including the reviewed mod's own commands; an unknown command is reported as rejected and does not run.
- Lab world-state tools for disposable-world testing (#247), behind `--allow-fixture-actions`: `stardew_lab_set_time`, `stardew_lab_set_weather` and `stardew_lab_set_season` for single-player or the network-2 host; `stardew_lab_warp`, `stardew_lab_set_zoom` and `stardew_lab_set_window` for any role. Each reports the observed time, season, weather, location, tile, zoom and window size. Fixture object placement and clearing and fishing preparation, so far console-only, are now also tools: `stardew_fixture_object_ensure`, `stardew_fixture_object_clear` and `stardew_fixture_fishing_prepare`. Fishing preparation now picks the bank nearest to the player or to an optional search start instead of the first bank from the map's top-left corner, so a warp plus a start tile selects a specific river, lake or ocean spot.
- `project review split-screen <join|leave|status> [--screen <id>] --json` and `project review network <leave|join|status> --role <host|farmhand> --json` replace the quoted `sdvkit split-screen ...` and `sdvkit network ...` console lines (#246).
- **Breaking (2.0):** `project review command` rejects SDVKit's own console lines (`sdvkit ...`) with exit `2` and names the replacement. The one exception is `sdvkit input ...` and `sdvkit screenshot viewport ...` while an owned test-save fixture is not loaded, such as at the title screen during a native language change; once the fixture is loaded, those lines are refused with `reviewConsoleUseTool`. Use `project review call <tool>` for input, screenshots, fixtures and lab state, and the lifecycle commands for split-screen and network. `project review command` runs the reviewed mod's and SMAPI's console commands. The guides and acceptance scripts call the input and screenshot tools (#246, #262).
- `project review call <tool> ['<json>']` runs one native MCP tool once from the command line, with the same selection, binding and opt-ins as `mcp serve`, and prints its structured result. Exit `3` reports a tool error or a tool that the selection and opt-ins do not provide.
- `project review mcp serve --http [--port <n>]` serves the same tools over Streamable HTTP on `127.0.0.1`, so clients such as Codex and Claude Code connect by URL. A per-server bearer token and the URL are written to a connection file in the selected role's runtime directory. The server rejects other hosts and browser origins, and stops when its review launch ends. The CLI now also needs the ASP.NET Core 8 runtime, which the required .NET 8 SDK already includes.
- Command results no longer pass through response files in the lab's runtime directory. The game-side mod keeps them in a bounded in-memory mailbox, and the CLI takes each one once through the same token-protected endpoint.

### Upgrade from 1.x

1. Stop and reset every lab with the 1.x executable first. The next start, smoke or review with 2.0 removes the former `SDVKit.AlwaysOn` folder from the lab's isolated mod group, but only when it is exactly the install SDVKit 1.x generated; anything else is left untouched.
2. In scripts and clients that read JSON, rename `alwaysOn` to `gameSide`, `alwaysOnPending` and similar problem codes to `gameSide...`, the mod-asset coverage scope `observedRequestsSinceAlwaysOnSubscribed` to `observedRequestsSinceGameSideSubscribed`, and the console-delivery problem codes to the `reviewCommand...` codes listed above. `schemaVersion` stays `1`: tell 1.x and 2.x reports apart by the SDVKit version, not by the schema version.
3. Replace `sdvkit ...` lines sent through `project review command` with `project review call <tool>` (input, screenshots, fixtures, lab state) or with `project review split-screen` and `project review network`.
4. Build identities changed. Evidence recorded with 1.x does not count for a 2.0 package.
5. A mod that declares a dependency on `SDVKit.AlwaysOn` must use `SDVKit.GameSide` instead; the old ID is reported as unavailable.
6. For Claude Code or Codex, run `sdvkit lab init` in your lab root. It replaces the skills that 1.x kept in the repository's `.agents/skills/`.

### Fixed

- A world action could use the tool on a neighbouring tile while still reporting `completed`. Stardew aims a mouse tool use at the previous update's cursor position, and that could still be the physical pointer. World actions now first complete one cursor-only game update and revalidate the target before pressing, like menu clicks (#265).
- A world action that SDVKit did not send after its cursor reached the target now reports `mayHaveRun` instead of `notDispatched` while action or tool input is held or queued, a tool is in use or the player cannot move, or when the target or backpack changed or could not be read. Physical input reaching the target in that window could previously run the action behind a `notDispatched` result (#264).

[Release acceptance and publication status](https://github.com/Nana1873/SDVKit/issues/286) · [Release](https://github.com/Nana1873/SDVKit/releases/tag/v2.0.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v1.0.0...v2.0.0)

## [1.0.0] - 2026-10-01

### Added

- Follow an original SMAPI save-data migration recipe with distinct v1, deliberately broken v2, and corrected v2 packages; preserve selected values through actual restart, verify repeated-load idempotence, and test missing data and future-schema refusal.
- Read one explicitly selected NPC friendship, mail ID and quest ID through `project review progression` and native MCP `stardew_progression_get` in the exact owned disposable single-player review. Fresh farmer/fixture/launch-bound observations expose raw friendship points, exact mail membership/count and current quest-log counts. An original rain-barrel recipe demonstrates diagnosis of a wrong friendship threshold, config correction, native mail/quest effects and explicit save/reload checks without inferring historical completion or paying the retained quest reward.
- Selected Content Patcher diagnosis can explain competing active patches on one canonical asset with `--order` or native MCP `order: true`. Bounded CP 2.9.1 reports preserve expected apply order, applied checkboxes and global definition positions; unrelated assets stay omitted and incomplete orders fail explicitly. An original two-patch recipe connects an unexpected winner to a source correction and separate final asset observation.
- Review an extracted C# and content-pack bundle in `single` by selecting its root and every direct pack child with `--content-pack`. Exactly one root/direct-child code mod and all declared pack members are frozen and staged separately; missing/unselected members, unsafe paths and identity collisions fail before launch. The original Bundle Arrival recipe covers existing build/package, explicit ready selection and separate code/pack effects.

### Fixed

- Offline `project check` now catches malformed reachable Content Patcher Include fragments and missing unconditional literal `FromFile` files, including nested Includes. Diagnostics identify the source field or included JSON; tokenized/conditional references and bounded Include coverage report explicit non-blocking warnings. Paths outside the selected mod root or through symbolic links/junctions are rejected before reading.
- Content Patcher diagnosis returns a controlled incomplete response when its owned log is replaced or its review binding changes during capture.

### Changed

- Establish the delivered modding toolkit and isolated live test lab as the 1.0 baseline. Document supported Windows/runtime and companion versions, capability limits, the 1.x public CLI/JSON/MCP/project compatibility policy, and stop/reset upgrade steps in the support guide. Root quickstarts and CLI help connect first use to diagnostics, observation and cleanup.

Upgrade from 0.x: stop and reset owned runs with the old executable, extract this release into a fresh directory, explicitly update CLI/MCP executable paths, and check version, doctor and inactive review status before launching again. Internal generated state is not a cross-version active-process adoption API; retain rejected inactive state for diagnosis and use a fresh lab if necessary. See [support and upgrades](docs/support.md), including unresolved status-file risks #136/#143 and the native controller menu-close limitation.

[Release acceptance and publication status](https://github.com/Nana1873/SDVKit/issues/243) · [Release](https://github.com/Nana1873/SDVKit/releases/tag/v1.0.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.10.2...v1.0.0)

## [0.10.2] - 2026-09-12

### Added

- Follow one reproducible GMCM 1.16.0 integer-slider recipe through explicit bounds and steps, native unsaved/saved UI behavior, configuration reconciliation, exact export/restaging, and the same packaged DLL's numeric effect after restart.
- A bounded neutral menu-close probe and live guidance document a native controller limitation: one synthetic B can close a root menu and then open inventory on disconnect. One Escape or observed close-button click closed the tested menus; child closes retained their parent. Runtime input is unchanged.

### Fixed

- Owned review mouse input also reaches native mod controls which read the parameterless XNA mouse getter during their player update, including GMCM numeric sliders. Click and drag prepare their cursor through a completed game update before pressing; drag also holds its endpoint for one update so controls using previous coordinates can reach it. Physical input remains available for overlap checks; cancellation removes owned coordinates/buttons and preserves the consumed wheel origin even before the next sample is published.
- Content Patcher diagnosis and refresh now inspect an already-published correlated log response before declaring its bounded wait expired.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a fresh directory and restart MCP clients against it. The native GMCM integer-slider workflow requires this version or a verified later package.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/227) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.10.1...v0.10.2)

## [0.10.1] - 2026-09-12

### Fixed

- Background MCP review input now tolerates foreground switches between unrelated applications while retaining exact review, player, and process checks. Acknowledgements flag observed external foreground changes; transitions involving either review process and uncertain completion still fail closed without replay.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a fresh directory and restart MCP clients against it.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/216) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.10.0...v0.10.1)

## [0.10.0] - 2026-09-09

### Added

- Inspect bounded vanilla dialogue/question and crafting menu text, choices, recipes, ingredients, outputs, and availability through the existing menu CLI and MCP read.
- Compare direct locale files with `default.json` during `project check`, including SMAPI-compatible placeholder names and non-blocking fallback warnings.
- Author an original multilingual SMAPI dialogue through a deliberate placeholder mismatch, exact packaging, native language and fallback observation, controller selection, and save/reload proof, with exact fixture revalidation after an intentional title-only setting change.
- Inspect every slot in the complete bounded player backpack through `project review inventory` and default single-review `stardew_inventory_get`, with explicit empty/unavailable slots and fresh capture/revision identities.
- Inspect a complete bounded current-location rectangle of live tilled soil, ordinary crops, and data-backed vanilla machines through `project review world` and default single-review `stardew_world_area_get`, with explicit missing, unsupported, unavailable, replacement, and state-change evidence.
- Inspect both inventory sides and the held item for the exact open regular vanilla chest through `project review container` and default single-review `stardew_container_get`, with menu/chest selection identity and fresh content revision.
- Dispatch one native adjacent watering, harvest, machine insertion, or machine collection through revision-bound `project review interact` and separately opted-in `stardew_world_interact`; completion remains distinct from freshly observed world and inventory effects.
- Transfer 1-99 items between one selected backpack slot and the open supported chest through native menu behavior, with strict stale-state refusal, explicit MCP opt-in, before/after conservation evidence, and no retry after uncertain execution.
- Prepare a natural fishing bank and rod in an owned disposable single/host review, with an explicit first-fish tutorial bypass. Runtime status and the existing MCP runtime tool now expose selected-rod cast, bite and catch observations without a mod-specific helper.
- Explicitly join and leave one native local farmhand inside an owned single fixture review. Screen-selected console input, menu/runtime observations, and viewport captures keep each local player's state separate and reuse normal save, restart, stop, and reset.
- Bind a native MCP server and CLI menu inspection to one exact observed local screen, farmer, and game context. Runtime, menu, cropped viewport, and explicitly enabled process-local input remain screen-local; leave/rejoin invalidates the old binding, and shared-window text input stays unavailable.
- A runnable one-time inventory reward recipe connects deliberate packaged-quantity diagnosis, corrected delivery, native chest deposit/withdrawal, stale-selection refusal, isolated save/reload persistence, and verified cleanup.
- Follow one reproducible Content Patcher crop-to-machine authoring recipe from a deliberate wrong item condition through native watering, harvest, Keg processing, MCP collection, chest quantities, save/reload, and exact isolated cleanup, with failed attempts and synthetic setup retained separately.
- Follow one reproducible per-player multiplayer recipe from a deliberate shared-player bug through distinct network-role and local-screen state, farmhand and screen replacement, exact selected-player chest reads, save/reload persistence, and verified cleanup.
- Explicitly leave and rejoin the exact network-2 farmhand while the host retains the owned fixture. An already-bound host MCP client retains only runtime observation while other pair-dependent reads fail closed; the farmhand process remains at title, and rejoin rotates its role session so old MCP clients fail closed.

### Fixed

- Report the native drawn bounds for normal, question, and transitioning dialogue boxes, and avoid counting the player's backpack twice in crafting availability.
- Keep an unfocused farmhand running when joining or client-options loading replaces Stardew's options before the next update. The background guard captures each replacement's original settings immediately so clean stop can still restore them.

### Changed

- Extend bounded backpack, selected-chest, current-world, and canonical Data reads to an exact owned network role or local screen. Player/menu/world responses remain selection-bound, while canonical Data remains process-shared; shop, asset inspection, Content Patcher, and mutation grants keep their existing single-only boundaries.
- The README leads with a copyable agent setup request that separates the portable install folder, mod workspace, and isolated lab data, and checks the selected release before a first review.
- Autonomous review guidance and MCP input descriptions prefer one Escape press or one observed close-button click for routine menu closing, followed by fresh menu evidence before further input. Controller input remains available for explicit controller tests; no runtime input behavior has changed.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a fresh directory and restart MCP clients against it. Bind each network-role or local-screen client to its exact current selection; create a fresh client after farmhand or screen replacement. Container transfers and world interactions require their separate startup opt-ins and fresh selections. Local split-screen reviews use an owned single test save; shared-window text input remains unavailable.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/211) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.9.0...v0.10.0)

## [0.9.0] - 2026-09-08

### Added

- Explicitly reconcile one selected staged mod or Content Patcher pack's root configuration save in the same owned single review, retaining the running process and code/content identity with an old/new config hash audit and isolated export.
- Inspect bounded map structure and texture metadata through single-review Native MCP; texture previews deliver a checked diagnostic PNG as image content.
- Inspect audio metadata and observed mod-owned asset catalogues and primitive values through single-review Native MCP with the same bounds and selections as the CLI.
- Diagnose a selected Content Patcher pack through Native MCP. Enable selected patch-JSON refresh independently with `--allow-cp-refresh`; its permission stays bound to the original review and retains incomplete-operation recovery evidence.
- Inspect bounded vanilla SeedShop Gold offers, money, inventory and held items through `project review shop` and single-review `stardew_shop_get`; unsupported shop or offer semantics are explicit.
- A runnable shop purchase authoring recipe connects one original SeedShop Gold offer, a deliberate configuration failure, exact packaged artifacts and process-local input to observed money, stock and inventory deltas.
- A focused GMCM 1.16.0 Boolean authoring recipe connects an original mod, owned UI editing, explicit config export/restaging, and the observed effect of the same packaged DLL after restart. Generic GMCM text input remains unsupported.

### Fixed

- Review honors an explicit `--project` when nested C# test or QA mods have their own manifests. Unselected mods are not staged automatically, and multi-mod packages remain rejected.
- Interactive review consoles start minimized, then appear without activation when SMAPI is ready, avoiding the terminal host taking focus during creation.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a fresh directory and restart MCP clients against it. Config reconciliation requires a newly staged single review with an existing root config file; it cannot be combined with CP patch refresh in the same review. It accepts saved configuration bytes, but does not reload a mod or rewrite source files.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/175) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.8.0...v0.9.0)

## [0.8.0] - 2026-09-06

### Added

- Check SMAPI manifests, Content Patcher content and direct i18n files offline with `project check`, using bundled official schemas and actionable file/field errors.
- Select a C# project with `--project` and a complete game installation with `--game-path` for build, package and review. Doctor now explains incomplete installations.
- Diagnose a selected Content Patcher pack and refresh explicitly selected patch JSON in a running single review, then inspect the resulting Data record.
- Review an extracted ready SMAPI mod directly, preserving its supplied files without rebuilding.
- Inspect an explicitly selected save through an isolated copy. Live review status and MCP also expose bounded local-player values and the selected inventory item.
- Read active inventory/shop menu geometry and supported public mod-menu controls through the CLI and `stardew_menu_get`.
- Use opt-in MCP button chords and bounded holds, clicks, scrolling, dragging and text entry. Text supports selected vanilla NamingMenu fields, including mod-created instances of that family; custom/GMCM fields and supplementary Unicode characters remain unsupported.
- Read selected-mod warnings and exceptions through the CLI and MCP, with bounded results and explicit attribution limits.

### Changed

- The README now leads with installation and two quickstarts; focused guides, a capability matrix and shorter CLI help provide the details. Portable packages include a short quickstart.
- Complete CP and SMAPI authoring recipes connect source changes, deliberate error diagnosis, observed in-game effects and packaging. An optional shared-lab workspace layout keeps mod sources separate.
- Release checks follow the changed capabilities and reuse accepted evidence for the same artifact and environment. Shared package verification and changelog-derived release notes keep distribution checks consistent.
- Failed native-launcher and concurrent-status tests retain bounded diagnostic evidence without weakening assertions or wait bounds.

### Fixed

- Virtual mouse input now reaches Stardew/SMAPI's shared input snapshot and normal wheel processing. Cancelled input drains concurrent dispatch safely; uncertain writes are not repeated.
- Ended or stale review status no longer exposes an old ready runtime snapshot. Failed final status publication remains visible, and Windows status replacement avoids the reproduced replacement-intermediate failure. The separate sporadic rename error 5 remains unresolved.
- Content Patcher source remains reviewable and refreshable after packaging, with build output excluded from staging.
- Inspection option handling, response-file ownership checks and failure diagnostics are consistent across adapters. An unconfirmed isolated-option restoration remains a warning when exact exit and cleanup succeeded.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a new directory and restart MCP clients against it. Input and fixture actions still require their separate startup opt-ins; input acknowledgements do not establish that a menu accepted or persisted a change.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/154) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.7.0...v0.8.0)

## [0.7.0] - 2026-09-04

### Added

- Inspect maps, textures and diagnostic previews, audio metadata, and observed mod-owned assets through the CLI in an active single review. Results reflect the final SMAPI content pipeline; these four adapters are CLI-only.
- Connect native MCP clients to single reviews or a fixed local host/farmhand role, with review/mod diagnostics and map or viewport screenshots. Canonical Data queries are also available through MCP for single reviews.
- Enable process-local input and owned-fixture actions independently through MCP. Actions are absent by default; farmhands can inspect/navigate their fixture but cannot create buildings/animals or save.

### Changed

- Reviews can send input and capture the viewport from the title screen, subject to the command's readiness checks.
- All lab sessions start windowed; later resizing and UI-scale testing remain under your control.
- Disposable-world automation accepts supported Stardew/SMAPI version ranges while retaining runtime API checks.

### Fixed

- Saving and restarting a network review now preserves the active farmhand's identity and fixture binding.
- Mods that create logs, caches, or databases no longer prevent cleanup after the exact owned game process stops. Drift remains visible during status and evidence checks.

Upgrade: stop active reviews and finish their required reset before replacing the package. Use separate MCP clients for host and farmhand; input and fixture actions require their respective startup opt-ins.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/108#issuecomment-5546516554) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.6.1...v0.7.0)

## [0.6.1] - 2026-09-03

### Added

- Read canonical structured Stardew Data through `project review data assets/keys/get` in a single review.
- Connect a native STDIO MCP client to inspect the single review's runtime snapshot.
- Send one mouse-wheel notch at the virtual cursor to an active menu.

### Fixed

- Button injection in background reviews now completes SMAPI's pressed/released/none lifecycle without focusing Stardew or moving the physical pointer.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.6.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.6.0...v0.6.1)

## [0.6.0] - 2026-09-03

### Added

- Review fixtures resolve building and animal kinds from live Stardew data, including coops and chickens; existing barn/cow aliases remain compatible.
- Background review input, a process-local virtual cursor, and non-overwriting viewport screenshots.

### Changed

- Interactive single, host, and farmhand reviews start in visible 1280x720 windows.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.6.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.3...v0.6.0)

## [0.5.3] - 2026-09-02

### Fixed

- Building fixtures prepare dynamic content within the placement area derived from Stardew's BuildingData, instead of depending on a vanilla object-ID list. Content outside that area is untouched; structural blockers still reject placement.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.3) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.2...v0.5.3)

## [0.5.2] - 2026-09-02

### Fixed

- Building fixture preparation recognizes unmodified vanilla Twig and Weeds debris in the requested footprint, fixing the natural-debris rejection found in v0.5.1.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.2) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.1...v0.5.2)

## [0.5.1] - 2026-09-02

### Fixed

- Fixture navigation can enter the loaded Greenhouse from the Farm and return through the natural Farm exit from the FarmHouse, Greenhouse, or an owned fixture interior.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.0...v0.5.1)

## [0.5.0] - 2026-09-01

### Added

- Mount the owned disposable save in single review with `--test-save`, retain work across stop/restart, and finish with a verified reset.
- Prepare owned barns, objects, and cows, inspect fixture state, and navigate between the Farm and owned interiors. In network review, mutations are host-only and observations/navigation are role-local.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.4.1...v0.5.0)

## [0.4.1] - 2026-09-01

### Fixed

- Code mods and companions may create their first regular root `config.json` during smoke/review without failing staged identity checks; other file changes remain checked.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.4.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.4.0...v0.4.1)

## [0.4.0] - 2026-09-01

### Added

- Select a ready root content pack as the single-review target, with its provider supplied explicitly as a local companion.
- A repository skill for interactive functional and visual reviews.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.4.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.3.0...v0.4.0)

## [0.3.0] - 2026-09-01

### Added

- Persistent interactive reviews with explicitly selected companions/content packs, role-addressed console commands, and native map screenshots.
- Local host/farmhand reviews with retained restart state and explicit final network reset.

### Fixed

- Resume the saved farmhand and retry a previously blocked reset through the owned lifecycle.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.3.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.2.0...v0.3.0)

## [0.2.0] - 2026-08-31

### Changed

- Single, host, and farmhand use independent persistent profiles below `.sdvkit/lab/profiles`, including per-process Stardew/SMAPI AppData.

Upgrade from v0.1.0: cleanly stop an active old lab first. The new layout does not migrate a retained v0.1.0 fixture junction.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.2.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.1.0...v0.2.0)

## [0.1.0] - 2026-08-31

### Added

- Portable Windows x64 toolkit for project inspection, creation, builds, and packaging, requiring the .NET 8 SDK.
- Isolated single/local host-farmhand lab and end-to-end smoke for standalone SMAPI C# mods.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.1.0)

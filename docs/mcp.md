# Native MCP for an active review

Connect an MCP client to an [already-running review](live-review.md#start-a-review). The CLI owns the lifecycle; MCP cannot start, stop or reset a review. First confirm the target and role with `project review status`. For split-screen, get screen IDs from the [screen-selected status command](live-review.md#local-split-screen-review). Examples use `$sdvkit` from [installation](../README.md#install). The server speaks STDIO, or [Streamable HTTP on loopback](#connect-over-http) with `--http`.

[Default tools](#default-observation-and-screenshots) · [Data](#game-data) · [Maps and textures](#maps-and-textures) · [Audio and mod assets](#audio-and-observed-mod-assets) · [Content Patcher](#content-patcher-diagnosis-and-opt-in-refresh) · [Input](#opt-in-input) · [Fixtures](#opt-in-fixture-actions) · [World interactions](#opt-in-world-interactions) · [Container transfer](#opt-in-container-transfer) · [Client configuration](#client-configuration) · [Command line](#call-a-tool-from-the-command-line) · [HTTP](#connect-over-http) · [Error contract](#binding-and-error-contract)

| Startup profile | single | local screen | host | farmhand |
| --- | --- | --- | --- | --- |
| Default observation/evidence | 31 tools | 12 tools | 12 tools | 12 tools |
| Add `--allow-input` | +9 | +8; text unavailable | +9 | +9 |
| Add `--allow-fixture-actions` | +15 | Unsupported | +15 | +6 |
| Add `--allow-world-actions` | +1 | Unsupported | Unsupported | Unsupported |
| Add `--allow-container-transfer` | +1 | Unsupported | Unsupported | Unsupported |
| Add `--allow-cp-refresh` | +1 for a ready root CP pack | Unsupported | Unsupported | Unsupported |

Counts describe these profiles, not a client allowlist. Enable only the [opt-in flags](glossary.md#commands-and-results) the task needs. No flag implies another.

## Start the server

Run the server from the lab root that owns the running review:

```powershell
& $sdvkit project review mcp serve
& $sdvkit project review mcp serve --topology network-2 --role host
& $sdvkit project review mcp serve --topology network-2 --role farmhand
& $sdvkit project review mcp serve --topology single --screen 1
& $sdvkit project review mcp serve --topology single --allow-fixture-actions
```

- `--topology` defaults to `single`, which takes no role.
- `network-2` needs one `--role host` or `--role farmhand`. Run one server per role.
- A split-screen test-save review takes one current `--screen` ID instead. Run one server per screen.
- Duplicate, missing or unknown option values are usage errors. There is no `--json` mode.
- In STDIO mode, stdout carries only MCP frames; diagnostics go to stderr. Closing stdin ends the server.

The role or screen is fixed at startup; see [bound and unbound](glossary.md#topology-roles-and-screens). Tools read and act only through that player, with no fallback to a peer.

A screen-bound server freezes the screen ID, farmer ID and game context. If the screen leaves or is replaced, the server stops working, even if the ID is reused. Start a new one for the new screen.

A network-role server freezes its role's session. While the farmhand leaves and rejoins through the [CLI network lifecycle commands](live-review.md#leave-and-rejoin-the-network-farmhand), an existing host client keeps observing. A new host client cannot start in that window, and input and fixture actions are refused until the pair passes again. When the farmhand returns to the title, its session changes and its client is rejected, even after rejoin. Start a new farmhand client once the pair passes again. MCP cannot leave or join.

## Default observation and screenshots

Every server has these tools. None sends input or changes the world.

| Tool | Returns |
| --- | --- |
| `stardew_runtime_get {}` | Launch, topology, role, target identity, optional fixture and the [runtime state](runtime-state.md). |
| `stardew_review_get {}` | The review: process, status, target load state, fixture/save, and every staged mod with kind, version, provider and build identity. |
| `stardew_mods_list { "offset": 0, "limit": 50 }` | The staged set compared with SMAPI's loaded mods. |
| `stardew_mod_diagnostics { "modId": "Example.Mod", "limit": 20 }` | One staged mod's warnings, errors and exceptions from the role's SMAPI log. |
| `stardew_menu_get {}` | [Menu geometry and controls](menu-inspection.md), with the `uiRevision` that input needs. |
| `stardew_inventory_get {}` | The [whole backpack](inventory-inspection.md), without opening a menu. |
| `stardew_container_get {}` | The [open vanilla chest](container-inspection.md), both sides and the held item. |
| `stardew_world_area_get { "x": 60, "y": 12, "width": 8, "height": 8 }` | Crops, soil and machines, as [`project review world`](world-inspection.md) returns them. |
| `stardew_screenshot_capture { "mode": "viewport", "label": "menu-open" }` | A fresh PNG: `viewport` is the player's view, `map` the loaded map. |

- **Runtime.** Before the world is ready, date, time, location and tile are `null`; `worldReady` and `menuOpen` stay set. `localPlayer` adds farmer identity, money, health, stamina and the selected item.
- **Mods list.** Each entry has source category, expected and loaded kind and version, load status, and warnings. `limit` defaults to 50 (1-100); follow `page.nextOffset` until `null`. Warnings are fixed SDVKit messages, not SMAPI log text. An invalid or stale snapshot fails rather than returning untrusted data.
- **Mod diagnostics.** It uses the CLI's [diagnostics projection and limits](live-review.md#diagnose-selected-mod-warnings-and-exceptions). Paths are not arguments. A missing or stale log returns an error without excerpts.
- **Inventory and container.** Results carry revisions, not durable item handles. Unsupported item data is marked. The chest, menu, player and location must match the selected player. The tool never opens, sorts or changes a chest.
- **Screenshot.** `mode` is `map` or `viewport`; map needs a loaded world. `label` is 1-64 ASCII letters, digits, `-` or `_`. The file is `SDVKit-<label>.png` in the role's isolated `StardewValley/Screenshots`, never overwritten. The result is metadata plus `image/png` content. A stale, malformed or over-16 MiB PNG, or a timeout, fails without retry.

Unbound single-review servers also have:

- `stardew_shop_get {}`: [Gold-shop offers, money and inventory](shop-inspection.md). It never buys.
- `stardew_progression_get` with `npcId`, `mailId` and `questId`: one [NPC/mail/quest observation](progression-authoring.md#read-only-contract). It needs a ready [test save](glossary.md#worlds-and-fixtures), no opt-in.
- The map, texture, audio, mod-asset and Content Patcher diagnosis tools below.

## Game Data

Every server has three Data tools, matching `project review data`:

| Tool | Arguments |
| --- | --- |
| `stardew_data_assets_list` | Optional `offset`, `limit` |
| `stardew_data_keys_list` | `asset`; optional `offset`, `limit` |
| `stardew_data_record_get` | `asset`, `key` |

Limits default to 50 (1-100). Asset names allow 256 characters, keys 2,048. Records keep their JSON shape within 4 MiB per record and 5 MiB per response. Data belongs to the whole game process, not to one player.

## Maps and textures

Unbound single-review servers expose [map and texture inspection](inspection.md) without an opt-in, with the CLI's limits.

| Tool | Arguments |
| --- | --- |
| `stardew_map_assets_list`, `stardew_texture_assets_list` | Optional `offset`, `limit` |
| `stardew_map_get`, `stardew_texture_get`, `stardew_texture_preview` | `asset` |
| `stardew_map_layers_list`, `stardew_map_tilesheets_list`, `stardew_map_warps_list`, `stardew_map_actions_list` | `asset`; optional `offset`, `limit` |
| `stardew_map_layer_get` | `asset`, `layer` |
| `stardew_map_tile_get` | `asset`, `layer`, `x`, `y` |
| `stardew_map_property_get` | `asset`, `property`, `scope`, `source`; `layer`, `x`, `y`, `frameIndex` as needed |

Pages default to limit 50 (1-100). `scope` is `map`, `layer` or `tile`. `source` is `direct`, or `tile-index` for tile-index properties; only `tile-index` takes `frameIndex`. An absent property is an error.

Example: `stardew_map_get {"asset":"Maps/Town"}`, then `stardew_texture_preview {"asset":"LooseSprites/Cursors"}`. A preview returns metadata and a verified `image/png` block, within 512x512 and 2 MiB. It is a diagnostic image, not a texture export.

Unknown, stale, unsupported and oversized selections fail. A failed listing is not complete coverage.

## Audio and observed mod assets

Unbound single-review servers expose [audio and observed mod-asset inspection](inspection.md#inspect-active-audio-metadata), read-only and with the CLI's limits.

| Tool | Arguments |
| --- | --- |
| `stardew_audio_cues_list`, `stardew_mod_assets_list` | Optional `offset`, `limit` |
| `stardew_audio_cue_get` | `cueId` |
| `stardew_mod_asset_keys_list` | `asset`; optional `offset`, `limit` |
| `stardew_mod_asset_record_get` | `asset`, `key` |

Cue IDs are case-sensitive. A cue from the data sources can return `sessionResident: false` with soundbank fields `null`. A cue in neither the data nor the soundbank fails with `audioCueUnknown`. No call plays or exports audio.

The mod-asset catalogue lists only `Mods/<owner>/...` requests seen since GameSide started. It is not a file scan; a missing entry proves nothing. An unsupported entry has `shape: null`; its keys and records fail. Asset names accept the CLI's case and separator normalization. Keys match exactly. A record is one string or 32-bit integer. There is no bulk export or change.

## Content Patcher diagnosis and opt-in refresh

Unbound single-review servers have read-only `stardew_cp_diagnose`:

```json
{"packId":"ExampleAuthor.SeasonalObjects","providerId":"Pathoschild.ContentPatcher","asset":"Data/Objects","parse":"{{Season}}"}
```

`packId` and `providerId` are required and name the staged pack and provider. Omit the optional `asset` to keep patches with unresolved targets; `parse` is optional too. The tool follows the [CP 2.9.1 diagnosis contract](cp-diagnosis.md). `reload` is `null`; diagnosis does not reload or inspect the asset. `state=ready` means CP output was recognized. Inspect the final asset separately.

Add `"order": true` with one [canonical asset name](glossary.md#assets) to get apply and definition order for that asset; see [selected order and interpretation](cp-diagnosis.md#explain-competing-patches-on-one-asset). Otherwise `orderAsset`, `applied` and `order` are `null`. These reports do not prove which C# code edited the asset.

`--allow-cp-refresh` adds `stardew_cp_refresh`. Startup needs a ready single review with a root CP pack and the CP 2.9.1 provider. The grant ends with that launch.

```powershell
& $sdvkit project review mcp serve --allow-cp-refresh
```

```json
{"packId":"ExampleAuthor.SeasonalObjects","providerId":"Pathoschild.ContentPatcher","files":["content.json"],"asset":"Data/Objects","key":"390"}
```

Each call lists its `files`. The source is the root pack recorded at startup, never a path from the call. The [selected JSON refresh limits](cp-refresh.md) apply. One call copies the files, reloads once, diagnoses and reads the record.

The result has `state`, `errorCode`, `recovery`, `launchId`, `process` (`processId` and `startTimeUtc`; no executable path), `launchBuildIdentity`, `refresh`, `filesReplaced`, `stagingRestored`, `diagnosis`, `observation` and `elapsedSeconds`. The record is at `structuredContent.observation.record`; compare the field yourself. `state=observed` does not say which mod set the value.

An incomplete call sets `isError=true` and keeps partial receipts. `refresh.commandWritten=null` or `commandMayHaveBeenWritten=true` means the reload may have run. After a cancel, check review status. A dispatched refresh finishes or records restart recovery, and is never retried. Restored copies can still need stop, reset and start. Follow the [authoring recipe](cp-authoring.md#use-an-mcp-client-for-the-same-edit-cycle).

## Opt-in input

`--allow-input` adds nine input tools. Input is [process-local](glossary.md#commands-and-results). The flag allows nothing else. Screen-bound servers omit `stardew_input_text`, because screens share one window.

Call `stardew_menu_get` first. Its `uiRevision` binds the menu, layout, screen size and UI scale. A stale `uiRevision` is rejected.

| Tool | Example and contract |
| --- | --- |
| `stardew_input_click` | `{ "x": 200, "y": 100, "button": "MouseLeft", "count": 2, "uiRevision": "...", "modifiers": ["LeftShift"] }`. One or two full clicks on a menu. Two clicks may differ from a native double-click. |
| `stardew_input_scroll` | `{ "x": 200, "y": 100, "notches": -3, "uiRevision": "..." }`. 1-20 notches; positive scrolls up. |
| `stardew_input_drag` | `{ "x": 200, "y": 100, "endX": 200, "endY": 300, "button": "MouseLeft", "durationTicks": 30, "uiRevision": "..." }`. Press, move for 1-120 updates, hold one update, release. |
| `stardew_input_text` | `{ "text": "Märchen Hof", "fieldId": 2, "uiRevision": "..." }`. Types into that `textField.id`. |
| `stardew_input_chord` | `{ "buttons": ["LeftShift", "F8"], "durationTicks": 5, "uiRevision": "..." }`. 1-8 SMAPI buttons held together for 1-120 updates. |
| `stardew_input_press` | `{ "button": "F8" }`. One non-wheel `SButton` for one tick. |
| `stardew_input_cursor_set` | `{ "x": 200, "y": 100 }`. Sets the virtual cursor. |
| `stardew_input_cursor_clear` | `{}`. Clears the cursor and held input. |
| `stardew_input_wheel` | `{ "direction": "up" }` or `"down"`. One notch; needs the cursor and a menu. |

- **Buttons.** Click and drag take `MouseLeft`, `MouseRight`, `MouseMiddle`, `MouseX1` or `MouseX2`, plus up to six Shift, Control or Alt modifiers. Mouse buttons in `press` and `chord` need the virtual cursor and never use the physical pointer position; `cursor_set` never moves the physical pointer. Chords reject `None`, wheel tokens, buttons already held, overlapping actions, and Ctrl+V. A key still held physically at release is not reported as released.
- **Gestures.** A change of menu or page stops a gesture. Changes the gesture itself causes are allowed, and the last step may close the menu. A viewport or scale change interrupts it.
- **Text.** Only an exact vanilla `NamingMenu` with an exact public `TextBox` works, including one a mod creates; subclasses, custom fields, GMCM and password fields do not. Text is 1-256 Unicode scalars from the Basic Multilingual Plane, without control characters; anything else rejects the whole request before typing. The game may still decline characters. Text is typed, never pasted. An unmodified one-tick `Back` (Backspace), `Enter` or `Tab` reaches the field as a text command. Delete and the arrow keys have no universal `TextBox` editing behavior, and Escape does not cancel a `NamingMenu` while its field is selected.
- **Results.** Gestures report `completedSteps`, `startTick`/`endTick`, `released`, and `finalX`/`finalY` when `cursorSet` is true. Chords report `buttons`, `durationTicks`, `startTick`, `endTick` and `released`. Text reports `deliveredScalars`: characters sent, **not text accepted**. Text never appears in results or errors. After a successful gesture, the cursor stays at its end point.
- **Failures.** A failed or canceled gesture stops and clears the cursor. Text stops when the field or `uiRevision` changes, on other keyboard input, after ten seconds, or on cancel; typed characters stay. A late cancel keeps the acknowledgement and sets `cancellationRequested: true`, and still returns an error. Never retry uncertain or partial input blindly.

Input success proves delivery only. For text, first check the field on a screenshot, then separately check the submitted or saved state.

**Desktop use alongside a review.** Using your mouse or other apps does not invalidate read-only tools. A focus change between two non-review apps reports `externalForegroundChanged: true`. A focus change involving a review process fails with `inputForegroundChanged`; a missing observation fails with `inputForegroundUnavailable`. When testing desktop isolation while someone uses the computer, record that activity and take a quiet baseline. Only evidence that SDVKit moved the pointer or took focus is an isolation failure. These sampled observations do not prove continuous focus stability or attribute a focus change to SDVKit.

## Opt-in fixture actions

`--allow-fixture-actions` adds fixture tools for a `single` server or a `network-2` host. The role must run a fresh SDVKit [test save](glossary.md#worlds-and-fixtures); other saves are rejected. The flag never picks a save or touches the normal `Saves` folder.

| Tool | Contract |
| --- | --- |
| `stardew_fixture_status_get {}` | Location, player, multiplayer state and fixture building IDs. |
| `stardew_fixture_enter { "building": "<alias-or-guid>" }` | Enters a fixture building, or `greenhouse`, through its warp. |
| `stardew_fixture_farm {}` | Returns to the Farm from a review interior. |
| `stardew_fixture_building_ensure { "alias": "barn-a", "kind": "Deluxe Barn", "x": 16, "y": 20 }` | Places a building with placement checks and rollback. |
| `stardew_fixture_animal_ensure { "building": "barn-a", "kind": "White Cow" }` | Adds an animal to a compatible building, with rollback. |
| `stardew_fixture_object_ensure { "building": "coop-a", "qualifiedItemId": "(O)388" }` | Places an object in a fixture building. |
| `stardew_fixture_object_clear { "building": "coop-a" }` | Removes SDVKit's objects from a fixture building. |
| `stardew_fixture_fishing_prepare { "skipTutorial": true, "x": 40, "y": 12 }` | [Fishing preparation](lab-reference.md#fishing-preparation) at the bank nearest the start point or player. |
| `stardew_fixture_save {}` | Saves and returns `saveId` and `persistedAtUtc`. |
| `stardew_lab_set_time { "time": 1930 }` | 600 to 2550, in ten-minute steps. Game time keeps running; from 2400 the player tires, so set an earlier time for longer checks. |
| `stardew_lab_set_season { "season": "winter" }` | Sets the season. |
| `stardew_lab_set_weather { "weather": "Rain" }` | `Sun`, `Rain`, `Storm`, `Snow`, `Wind` or `GreenRain` for the current location context. |
| `stardew_lab_warp { "location": "Town", "x": 20, "y": 30 }` | Warps the player; answers after arrival. |
| `stardew_lab_set_zoom { "zoom": 1.25 }` | 0.75 to 2. |
| `stardew_lab_set_window { "width": 1600, "height": 900 }` | Resizes the game window, shared by split-screen players. |

- **Time.** Light updates at the next ten-minute tick; tiles refresh at the next location change. Time keeps running: at 2600 the player passes out and the game saves.
- **Warp.** Only permanent locations; mine levels are refused. A given tile must be passable; otherwise the default arrival tile is used.
- **Results.** `stardew_lab_*` results have a `lab` object with time, season, weather, location, tile, zoom and window. Ensure and navigation results have a `changed` flag, so a repeat proves idempotence. Zoom and window stay in the isolated profile.
- **Farmhand.** Only `status_get`, `enter`, `farm`, `lab_warp`, `lab_set_zoom` and `lab_set_window`. No world changes.
- **Cancel.** A cancel before dispatch writes nothing. After dispatch, a missing acknowledgement returns `mayHaveRun=true`. Actions are never retried.

To prove persistence, stop and start the same review. Always finish with `project review reset`.

## Opt-in world interactions

`--allow-world-actions` adds `stardew_world_interact` on a ready single-player test save. Each call names a target and revision from `stardew_world_area_get` and the `inventoryRevision` from `stardew_inventory_get`.

- `water`: dry crop soil, watering can selected.
- `harvest`: a ready hand-harvest crop, empty hand. Scythe crops are rejected.
- `machineInsert`: an idle data-backed machine that accepts the selected object.
- `machineCollect`: a ready machine, empty hand.

The target must be the faced adjacent tile, visible on screen. SDVKit rechecks everything just before its one mouse sample. It never walks, teleports, grows crops, finishes machines, replays or rolls back.

The permission belongs to the review the server started with. After that review stops or is replaced, an old server's action returns `worldActionStartupBindingChanged` with `notDispatched`; start a new server. The tool is annotated destructive and not idempotent.

`state=completed` proves only that the input ran; read world and inventory again. `dispatchState=notDispatched` means SDVKit sent nothing: no input was held or queued, no tool was in use, and the target and backpack were unchanged. For `mayHaveRun`, check state before retrying by hand. A cancel after dispatch never retries.

## Opt-in container transfer

`--allow-container-transfer` adds `stardew_container_transfer` for the [native chest transfer](container-transfer.md). Only unbound single-review servers on a test save accept it.

## Client configuration

A project-local Codex configuration can limit the tools a client sees:

```toml
[mcp_servers.sdvkit_review]
# Replace with the absolute path to your extracted executable.
command = "C:\\path\\to\\sdvkit.exe"
args = ["project", "review", "mcp", "serve", "--topology", "single"]
cwd = "C:\\path\\to\\the\\lab-owning-project"
enabled_tools = [
  "stardew_runtime_get",
  "stardew_review_get",
  "stardew_mods_list",
  "stardew_mod_diagnostics",
  "stardew_screenshot_capture",
  "stardew_data_assets_list",
  "stardew_data_keys_list",
  "stardew_data_record_get",
]
```

For input, add `"--allow-input"` to `args` and allow only the input tools you need. Without the flag, input tools stay absent even if allowlisted. For a `network-2` host client, add a second server with `args` ending in `"--topology", "network-2", "--role", "host"`.

## Call a tool from the command line

`project review call` runs one tool once and prints its result as JSON. It takes the same selection and opt-in flags as `mcp serve`. Pass arguments as one JSON object:

```powershell
& $sdvkit project review call stardew_runtime_get
& $sdvkit project review call stardew_screenshot_capture '{"mode":"viewport","label":"before"}'
& $sdvkit project review call stardew_input_press '{"button":"Escape"}' --allow-input
& $sdvkit project review call stardew_inventory_get --topology network-2 --role farmhand
```

Exit `0` means success. Exit `3` means a tool error (its JSON is still printed), a startup or binding failure, an unavailable tool (the message lists available ones), or a protocol failure on stderr. A virtual cursor stays set until `stardew_input_cursor_clear`. Ctrl+C cancels pending input and never retries it.

## Connect over HTTP

`--http` serves the same tools over Streamable HTTP on `127.0.0.1` only. The port is free unless you pass `--port <1024-65535>`:

```powershell
& $sdvkit project review mcp serve --topology single --http --port 47321
$connection = Get-Content .\.sdvkit\lab\single\runtime\mcp-http.json -Raw | ConvertFrom-Json
$env:SDVKIT_MCP_TOKEN = $connection.token
```

The server writes a connection file and names it on stderr: `.sdvkit/lab/single/runtime/mcp-http.json`, or `mcp-http-screen-<id>.json` for a screen. It holds `url`, bearer `token` and `launchId`. Requests need the token, host `127.0.0.1:<port>` and no `Origin` header. The server stops with its launch, on review stop or on Ctrl+C, and deletes the file. Each start makes a new token, so keep it in the environment, not in shared configuration. A second server for the same launch and selection is refused with `mcpHttpAlreadyServing`.

Codex:

```toml
[mcp_servers.sdvkit_review]
url = "http://127.0.0.1:47321/mcp"
bearer_token_env_var = "SDVKIT_MCP_TOKEN"
```

Claude Code (`.mcp.json` expands environment variables):

```json
{
  "mcpServers": {
    "sdvkit-review": {
      "type": "http",
      "url": "http://127.0.0.1:47321/mcp",
      "headers": { "Authorization": "Bearer ${SDVKIT_MCP_TOKEN}" }
    }
  }
}
```

## Binding and error contract

The boundary is the [owned](glossary.md#reviews-and-ownership) review, not the game or desktop.

- **Limits.** The server cannot start, stop or reset a review or send console text. It changes the game only through an enabled action family. It never reads the normal or mod-manager `Mods` folder or normal saves.
- **Files.** Screenshots and texture previews are written below `.sdvkit/` without an opt-in, never overwriting.
- **Checks.** Every call confirms the startup review is still running. On a mismatch it returns an error and no stale payload. Errors carry a problem code, not raw paths.
- **Readiness.** Runtime and Data tools need the target loaded. The mod tools can report a mod as not loaded; they read SMAPI's registry, never a folder.
- **Network.** Both roles need identical staged mods, fixture and save. During a farmhand rejoin only `stardew_runtime_get` works on the host.
- **Privacy.** Responses never contain peer data, absolute paths, environment values, raw logs or CLR types. Log filtering is not full sanitization of mod text.
- **Uncertain actions.** After `mayHaveRun` or a similar error, read state before acting. SDVKit never replays an action.

## Reference details

- **Per-call checks.** Under a short lock, SDVKit checks the ownership marker, topology, build identity, process ID, start time and executable, a fresh GameSide marker, the fixture and Save, and the `network-2` pair. Screen calls recheck screen, farmer and context. The lock is released before serialization.
- **Data.** Data tools use the CLI's Data service and check the player before and after the request.
- **Action lock.** One lock per role covers input and fixture actions; a concurrent call is rejected, not queued.
- **Dispatch.** Each action waits for one fresh GameSide acknowledgement bound to its request ID, then confirms a later status tick. Once delivery is possible, it is never retried. A departed screen's pending input is released.
- **Gestures.** Click and drag first move the cursor in a separate update. Drag `durationTicks` excludes preparation, press, end hold and release.
- **Text.** One character enters `GameWindow.TextInput` per keyboard poll, rechecking the field each time. JSON with unpaired surrogates fails in the MCP SDK and sends nothing.
- **Fixtures.** Acknowledgements are read once and bound to request, launch, role, fixture and Save. The wait limit is two minutes for save plus five seconds of grace.
- **Exit.** On EOF after input, the server clears the cursor and held input; if that is unconfirmed, it exits nonzero.

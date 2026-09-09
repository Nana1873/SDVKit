# Native MCP for an active review

Connect a client to an [already-running review](live-review.md#start-a-review). First confirm its target and selected role with `project review status`. Examples use `$sdvkit` from [installation](../README.md#install). The protocol uses STDIO; lifecycle stays in the CLI.

[Default tools](#default-observation-and-screenshots) · [Data](#canonical-data) · [Maps and textures](#maps-and-textures) · [Audio and mod assets](#audio-and-observed-mod-assets) · [Content Patcher](#content-patcher-diagnosis-and-opt-in-refresh) · [Input](#opt-in-input) · [Fixtures](#opt-in-fixture-actions) · [World interactions](#opt-in-world-interactions) · [Client configuration](#client-configuration) · [Error contract](#binding-and-error-contract)

| Startup profile | single | host | farmhand |
| --- | --- | --- | --- |
| Default observation/evidence | 28 tools | 6 tools | 6 tools |
| Add `--allow-input` | +9 | +9 | +9 |
| Add `--allow-fixture-actions` | +6 | +6 | +3 |
| Add `--allow-world-actions` | +1 | Unsupported | Unsupported |
| Add `--allow-cp-refresh` | +1 for a ready root CP pack | Unsupported | Unsupported |

Counts describe these profiles, not a universal client allowlist. Enable only the authorized families needed for the task. On a controlled startup/tool error, check review status and the named code; never reuse a stale payload. On uncertain action completion (`mayHaveRun`), inspect current state before deciding whether another action is safe.

Start the native STDIO server from the directory that owns the already-running
project review. A single-player review needs no role. A network-2 server must
select exactly one role, and separate client processes are required to inspect
both roles:

```powershell
& $sdvkit project review mcp serve
& $sdvkit project review mcp serve --topology single
& $sdvkit project review mcp serve --topology network-2 --role host
& $sdvkit project review mcp serve --topology network-2 --role farmhand
& $sdvkit project review mcp serve --topology single --allow-fixture-actions
& $sdvkit project review mcp serve --topology single --allow-world-actions
& $sdvkit project review mcp serve --topology network-2 --role host --allow-fixture-actions
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

## Opt-in world interactions

`--allow-world-actions` advertises only `stardew_world_interact`, and startup
requires the exact ready SDVKit-owned disposable single-player test save. Input,
fixture, observation, and CP grants do not imply this permission. Each call must
provide an exact target instance/revision from `stardew_world_area_get` and the
complete current `inventoryRevision` from `stardew_inventory_get`.

The closed action set is `water` for dry ordinary crop soil with the selected
watering can, `harvest` for a ready ordinary hand-harvest crop with an empty hand
(scythe-required crops are rejected),
`machineInsert` for a natively accepted selected object and an idle ordinary
data-backed machine, and `machineCollect` for a ready machine with an empty hand.
The target must be the currently faced adjacent tile and visible in the current
viewport. Immediately before its single process-local mouse sample, SDVKit
rechecks ownership, player readiness, viewport/cursor binding, target identity
and revision, inventory revision, selected tool/item, resources, and capacity.
It never teleports, navigates, grows crops, completes machines, rewrites stacks,
replays, or rolls back a native action.

`state=completed` proves only that the one native input sample completed. Read
world and inventory again to establish the effect. `dispatchState=notDispatched`
is safe non-dispatch; `mayHaveRun` is uncertain and must be observed before any
manual retry. Cancellation before dispatch cannot act; cancellation after the
command write drains a retained response when possible and never retries.

## Content Patcher diagnosis and opt-in refresh

Single-review servers expose read-only `stardew_cp_diagnose` by default:

```json
{"packId":"ExampleAuthor.SeasonalObjects","providerId":"Pathoschild.ContentPatcher","asset":"Data/Objects","parse":"{{Season}}"}
```

`packId` and `providerId` are required exact staged selections. `asset` and `parse`
are optional; omit `asset` to retain patches with unresolved targets. The tool
uses the existing [CP 2.9.1 diagnosis contract](cp-diagnosis.md), including argument
bounds, owned-log correlation, disclosure limits and explicit incomplete results.
`structuredContent.summary` precedes optional `parse`; `reload` is null. Diagnosis
does not inspect or reload an asset. `state=ready` means recognized, correlated
CP output; compare patch states and then separately inspect the final asset.

Enable the independent refresh capability only for an authorized client:

```powershell
& $sdvkit project review mcp serve --allow-cp-refresh
```

Startup requires a ready owned single review with a root CP pack and the selected
CP 2.9.1 provider. The grant remains bound to that launch, exact process and source
selection. It cannot move to a restarted review. Input and fixture flags never
enable refresh; `stardew_cp_refresh` is absent without its own flag. Files remain
explicit on every call:

```json
{"packId":"ExampleAuthor.SeasonalObjects","providerId":"Pathoschild.ContentPatcher","files":["content.json"],"asset":"Data/Objects","key":"390"}
```

The source root is the root pack recorded at server startup, never a tool-supplied
filesystem path. All [selected JSON refresh limits](cp-refresh.md) apply. The
existing operation lock rechecks permission before any staged replacement, then
covers copies, one reload, diagnosis and the selected Data observation.

The result retains `state`, `errorCode`, `recovery`, launch/process identity,
`launchBuildIdentity`, `refresh`, `filesReplaced`, `stagingRestored`, `diagnosis`,
`observation` and `elapsedSeconds`. Process identity exposes `processId` and
`startTimeUtc`; it omits the local executable path. A validated final record is
at `structuredContent.observation.record`. Compare the expected field yourself;
`state=observed` does not assert which mod caused it or whether a UI rendered it.

Incomplete operations have `isError=true` and retain valid partial receipts and
diagnosis. In particular, `refresh.commandWritten=null` or a response's
`commandMayHaveBeenWritten=true` can mean reload ran. A canceled/disconnected client
must inspect review status: an already-dispatched refresh finishes its bounded
operation or records restart recovery, and is never retried automatically. Even
restored copies can require exact stop/reset/start. No source edit, packaging,
watcher, general console command or network refresh tool is added. Follow the
[authoring recipe](cp-authoring.md#use-an-mcp-client-for-the-same-edit-cycle).

## Default observation and screenshots

Single-review servers additionally expose `stardew_inventory_get {}` for a
fresh read-only capture of the [complete bounded backpack](inventory-inspection.md)
without opening a menu. It returns every slot plus request and visible-fact
revision identities; neither is a durable item-instance handle. Unsupported or
incomplete item data is explicit, and network servers do not advertise this
single-review capability.

Single-review servers additionally expose `stardew_container_get {}` for a
fresh read-only capture of the [selected supported vanilla chest](container-inspection.md),
both inventory sides, and any menu-held item. Its selection identity binds the
open menu and placed chest; its content revision also binds the exposed item
facts. It never opens, searches, sorts, or mutates a chest, and network servers
do not advertise it.

Single-review servers also expose `stardew_shop_get {}` for a fresh
read-only capture of [supported Gold-shop offers, money and inventory](shop-inspection.md).
It requires no input opt-in. Unsupported shop semantics are explicitly unavailable;
the tool neither purchases items nor infers a successful purchase from a price.
Network servers do not advertise this single-review capability.

The same default single-review profile exposes
`stardew_world_area_get { "x": 60, "y": 12, "width": 8, "height": 8 }`.
It returns the same complete bounded crop, tilled-soil, and ordinary-machine
capture as [`project review world`](world-inspection.md), with explicit missing,
unsupported, and unavailable states. It sends no input and grants no world
mutation. Network-role servers do not advertise it.

The role is fixed when the server starts and cannot be selected or changed in a
tool call. `role` is `null` for `single` and exactly the configured `host` or
`farmhand` for `network-2`. Every server exposes five read-only observation tools:

- `stardew_menu_get {}` captures [bounded active-menu geometry and public controls](menu-inspection.md), including supported dialogue/question choices and crafting recipe facts.
  on demand. Inventory/shop adapters and partial custom base coverage share the
  exact world-ready role binding; this surface never sends input.

- `stardew_runtime_get {}` returns matching structured JSON and compact JSON
  text with schema version, launch ID, topology, selected role, observation
  time, exact target `UniqueID`/version/build identity, optional verified
  review-fixture identity, and the selected role's runtime object. Before a
  world is ready, season/day/year/time/location/tile are explicitly `null`;
  `worldReady` and `menuOpen` remain available. The additive `localPlayer`
  slice supplies bounded farmer identity, money, health/stamina and one selected
  inventory item. See the [field and availability contract](runtime-state.md)
  for sources, bounds, nulls, versions and transition semantics.
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
- `stardew_mod_diagnostics { "modId": "Example.Mod", "limit": 20 }` reads
  bounded warning/error entries and recognized exception continuations for one
  staged identity from that role's exact isolated SMAPI log. It shares the CLI
  [diagnostics projection and limits](live-review.md#diagnose-selected-mod-warnings-and-exceptions),
  including attribution uncertainty, withheld context, scan/result counts and
  truncation. The selected role cannot be overridden, paths are not operands,
  and unavailable/stale/unbound logs return a controlled error without excerpts.

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

The `stardew_mods_list` diagnostics are fixed SDVKit messages for a selected mod that is not
loaded or whose loaded version or kind differs. They are not raw SMAPI loader
warnings, exception text, or log excerpts. An unexpected loaded identity or an
invalid, duplicate, oversized, stale, or mismatched snapshot fails closed
instead of being returned as untrusted inventory data.

## Canonical Data

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

## Maps and textures

Single-review servers expose the existing [map and texture inspection](inspection.md)
operations without an action opt-in. These calls use the final active SMAPI content
pipeline and the same bounds, exact selections and ownership checks as the CLI.
They are absent from network servers.

| Tool | Selection |
| --- | --- |
| `stardew_map_assets_list` | Optional `offset`, `limit` |
| `stardew_map_get` | `asset` |
| `stardew_map_layers_list`, `stardew_map_tilesheets_list`, `stardew_map_warps_list` | `asset`; optional `offset`, `limit` |
| `stardew_map_layer_get` | `asset`, `layer` |
| `stardew_map_tile_get` | `asset`, `layer`, non-negative `x`, `y` |
| `stardew_map_property_get` | `asset`, `property`, explicit `scope` and `source`; scope-specific `layer`, `x`, `y`, `frameIndex` |
| `stardew_texture_assets_list` | Optional `offset`, `limit` |
| `stardew_texture_get`, `stardew_texture_preview` | `asset` |

Pages default to offset 0 and limit 50, with limits of 1-100. Property scope is
`map`, `layer` or `tile`; source is `direct` or, for tile-index properties,
`tile-index`. Only tile-index selection accepts a frame index. Select property
names from observed content; an absent property remains a controlled error.

For example, call `stardew_map_get {"asset":"Maps/Town"}`, then
`stardew_texture_get {"asset":"LooseSprites/Cursors"}` and
`stardew_texture_preview {"asset":"LooseSprites/Cursors"}`.
Preview returns structured metadata with matching JSON text and an `image/png`
MCP image block. It reads and verifies the existing request-bound PNG before
returning those bytes, including its hash, dimensions and encoded size. A local
path alone is not the preview result. The same 512x512 and 2 MiB diagnostic limits
apply; this is not a raw texture or source-asset export.

Unknown, stale, unsafe, colliding, unsupported and oversized selections fail
closed. Inspect the named problem and review status before another request;
do not treat a failed inventory as complete coverage.

## Audio and observed mod assets

Single-review servers expose five read-only adapters over the existing
[audio and observed mod-asset inspection](inspection.md#inspect-active-audio-metadata).
They use the same typed queries, bounds, response validation and exact active-review
binding as the CLI, and are absent from network servers.

| Tool | Selection |
| --- | --- |
| `stardew_audio_cues_list` | Optional `offset`, `limit` |
| `stardew_audio_cue_get` | Required `cueId` |
| `stardew_mod_assets_list` | Optional `offset`, `limit` |
| `stardew_mod_asset_keys_list` | Required `asset`; optional `offset`, `limit` |
| `stardew_mod_asset_record_get` | Required `asset`, `key` |

Audio pages contain the same final-pipeline cue identities, sources, definition
metadata and explicitly bounded coverage as `project review audio`. Exact cue IDs
are case-sensitive. An exact cue discovered through the supported data sources
can validly return `sessionResident: false` with unavailable soundbank definition
fields represented as `null`. A cue absent from both the supported data-driven
population and the active soundbank fails closed with `audioCueUnknown`; a case
mismatch or response echo mismatch also fails closed. No call plays, records,
reads, or exports audio bytes or paths.

The mod-asset catalogue covers only conventional `Mods/<owner>/...` requests
observed since AlwaysOn subscribed. It is not a filesystem scan or a claim that
unrequested assets do not exist. Catalogue entries retain lifecycle, generation,
request/ready counts, collisions and adapter availability; an unsupported entry
therefore has `shape: null` and remains visible, while exact keys or records for
it fail closed. Asset selection accepts the same unambiguous case/separator
normalization as the CLI and returns the canonical identity. String keys remain
ordinal and exact. Exact records expose only one reviewed primitive string or
32-bit integer through the six existing adapters. There is no arbitrary object
serialization, bulk export, mutation, normal-`Mods` scan, or source/provider path.

## Opt-in input

With `--allow-input`, and only for that server process, every topology also
exposes nine typed action tools:

- `stardew_input_click { "x": 200, "y": 100, "button": "MouseLeft", "count": 2, "uiRevision": "<from stardew_menu_get>", "modifiers": ["LeftShift"] }`
  clicks an active menu once or twice, with a separate complete press/release
  edge for each click. A target may interpret two clicks differently from a
  native double-click feature.
- `stardew_input_scroll { "x": 200, "y": 100, "notches": -3, "uiRevision": "<from stardew_menu_get>" }`
  scrolls an active menu at the coordinate by 1-20 notches, one verified notch
  per input update. Positive counts scroll up; negative counts scroll down.
- `stardew_input_drag { "x": 200, "y": 100, "endX": 200, "endY": 300, "button": "MouseLeft", "durationTicks": 30, "uiRevision": "<from stardew_menu_get>" }`
  presses at the start, moves along one linear path for 1-120 movement updates,
  then releases at the exact endpoint. Duration excludes the initial press and
  final release: duration 1 still contains one genuine held movement update.
  Click and drag accept MouseLeft, MouseRight, MouseMiddle, MouseX1 or MouseX2,
  and an optional list of up to six distinct Left/Right Shift, Control or Alt
  modifiers. Omitted or null modifiers mean an empty list.
- `stardew_input_text { "text": "Märchen Hof", "fieldId": 2, "uiRevision": "<from stardew_menu_get>" }`
  delivers characters to the exact available `textField.id` in that snapshot.
- `stardew_input_chord { "buttons": ["LeftShift", "F8"], "durationTicks": 5, "uiRevision": "<from stardew_menu_get>" }`
  presses 1-8 distinct exact runtime SMAPI button names in the same input update,
  holds the complete set for 1-120 updates, then confirms release. Read
  `stardew_menu_get` first: its opaque `uiRevision` binds the current menu or
  no-menu lifetime, known page/component geometry, screen size and UI scale.
  Changes before the first input update reject the chord. Moving the world camera
  does not invalidate screen-local coordinates. Mouse members require the virtual
  cursor. None, wheel tokens, overlapping actions, physical/external button
  collisions and clipboard-triggering Ctrl+V combinations are rejected.
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

Text entry supports an exact vanilla `NamingMenu` containing an exact public
`TextBox`, including a mod-created instance of that same family. Subclasses,
custom fields, GMCM components and password fields are unavailable. The snapshot
binds the actual field, keyboard dispatcher and subscriber identities, selection,
menu lifetime and review role; it does not capture text contents.

Text must contain 1-256 Unicode scalar values. Empty text, malformed Unicode,
control characters and oversized input are rejected before delivery. Raw JSON with
unpaired surrogate escapes is rejected by the MCP SDK before tool invocation
(with a generic protocol error); no input is dispatched and the connection remains
usable. Supplementary
scalars are explicitly unsupported by the installed SDL path and reject the whole
request before its first character. Spaces and BMP characters such as German
umlauts pass unchanged to native field validation; native length/font/numeric
limits can still decline them. Text is encoded for transport, never pasted or
written directly into a field.

At most one character enters `GameWindow.TextInput` immediately before each native
keyboard-dispatcher poll. Each poll revalidates the exact field/subscriber and UI
revision. A changed target, lost selection or concurrent native text/keyboard
input stops the remainder. Delivery has a ten-second lifetime, checked even when
polling stops. Cancellation/EOF stops remaining characters; already delivered
characters are not undone, and no remainder is queued. `deliveredScalars` counts
completed event/dispatcher delivery, **not accepted text**. Verify the field with
screenshots or selected-mod state, then separately verify submitted/persisted state.
Never retry an uncertain or partial delivery blindly. Text contents are absent
from generic acknowledgements and error messages.

For the supported selected field, a one-tick unmodified `Back`, `Enter` or `Tab`
press/chord bridges its observed SButton edge into the native text-command event
queue. Existing native input prevents that bridge, avoiding duplicate delivery.
`Back` means Backspace; Delete and arrow keys have no universal TextBox editing
behavior, and NamingMenu Escape does not cancel while the field is selected.
Other buttons keep their existing native behavior. Ctrl+V remains unsupported.

The three mouse gestures validate the full UI revision before acceptance and
before their first input sample. While further actions remain, root/page/child
identity changes stop the gesture; component/value changes caused by scrolling
or dragging are allowed. The final requested action may change or close the
menu. Viewport/scale changes interrupt the gesture even during release; `released` still reports whether button release was actually observed.

Gesture responses add the requested action fields, `completedSteps`, observed
`startTick`/`endTick`, `released`, and `finalX`/`finalY` when `cursorSet` is true.
Steps mean complete clicks, verified notches, or drag movement updates. Success
retains the final virtual cursor. Cancellation/failure after acceptance stops
further steps and clears it after the release game-update opportunity; missing
completion callbacks report failure with unconfirmed release. Pre-acceptance
validation does not move the virtual cursor. Partial counts do not authorize an
automatic retry, and input completion does not prove the intended menu effect.

The chord acknowledgement adds `buttons` (canonical names), `durationTicks`,
`startTick`, `endTick` (the release input sample) and `released`. One tick keeps
single-press semantics; longer durations produce one Pressed transition followed
by Held samples. Start/end fields are absent when those samples were not observed.
A physical key still down at release cannot be reported as released and is never
suppressed. Cancellation sends one request-bound cancellation on the existing
owned console channel, drains the bounded action, and does not retry it.

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

## Opt-in fixture actions

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
persistence by stopping and starting the same explicitly selected review, when persistence is part of the test. Always
finish with the existing topology-specific `project review reset` lifecycle.

## Client configuration

A project-local Codex configuration can keep the surface explicitly limited:

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
  "stardew_audio_cues_list",
  "stardew_audio_cue_get",
  "stardew_mod_assets_list",
  "stardew_mod_asset_keys_list",
  "stardew_mod_asset_record_get",
]
```

For an explicitly authorized input session, add `"--allow-input"` to `args`
and independently allow only the needed names from
`stardew_input_click`, `stardew_input_scroll`, `stardew_input_drag`,
`stardew_input_text`, `stardew_input_chord`, `stardew_input_press`, `stardew_input_cursor_set`,
`stardew_input_cursor_clear`, and `stardew_input_wheel`. Omitting the startup
flag keeps all nine absent even if the client requests or allowlists them.

For example, bind a separate network-2 host client by changing only the server
name and arguments:

```toml
[mcp_servers.sdvkit_review_host]
# Replace with the absolute path to your extracted executable.
command = "C:\\path\\to\\sdvkit.exe"
args = ["project", "review", "mcp", "serve", "--topology", "network-2", "--role", "host"]
cwd = "C:\\path\\to\\the\\lab-owning-project"
enabled_tools = [
  "stardew_runtime_get",
  "stardew_review_get",
  "stardew_mods_list",
  "stardew_mod_diagnostics",
  "stardew_screenshot_capture",
]
```

## Binding and error contract

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
canonical Data service used by the CLI, which revalidates the exact single review before
sending its bounded request to the existing game-side reader. There is no second
inventory, serializer, mailbox, or lifecycle. Network-2 additionally requires both exact
role states and processes, identical staged target/build/fixture/save bindings,
and returns only the role fixed at server startup. The lock is released before
MCP serialization.

A mismatch returns a controlled tool error and no stale payload; Data and
screenshot failures expose a bounded internal code rather than raw paths or
transport details. MCP
responses never expose peer runtime data, private absolute paths, environment
values, complete raw logs, menu CLR types, or arbitrary state. Selected log
diagnostics preserve useful relative locations and filter recognized private
context; this is not complete sanitization of arbitrary mod-authored text.
CP refresh exposes only the owned process ID and start time for same-process
verification; valid incomplete receipts remain available as error evidence.
The MCP server never reads the normal or mod-manager-owned `Mods` directory or
normal saves. It opens no listener and cannot start, stop, reset, transport
arbitrary console text, or mutate a review except through an explicitly enabled
typed action family. `--allow-input` wraps only the existing process-local
cursor, one-tick button, and one-notch wheel paths described above.
`--allow-fixture-actions` wraps only the closed typed operations above inside the
already active owned disposable fixture. Screenshot capture and texture preview
create bounded, non-overwriting evidence PNGs below the selected role's ignored
profile or review runtime, respectively; neither requires an action opt-in.
`--allow-cp-refresh` separately wraps the selected root pack's bounded JSON
refresh and exposes only the owned process ID and start time for same-process
verification.
`--allow-world-actions` separately wraps only the four revision-bound adjacent
interactions documented above and is unsupported for network roles.

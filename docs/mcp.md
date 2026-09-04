# Native MCP for an active review

Connect a client to an [already-running review](live-review.md#start-a-review). First confirm its target and selected role with `project review status`. Examples use `$sdvkit` from [installation](../README.md#install). The protocol uses STDIO; lifecycle stays in the CLI.

[Default tools](#default-observation-and-screenshots) · [Data](#canonical-data) · [Input](#opt-in-input) · [Fixtures](#opt-in-fixture-actions) · [Client configuration](#client-configuration) · [Error contract](#binding-and-error-contract)

| Startup profile | single | host | farmhand |
| --- | --- | --- | --- |
| Default observation/evidence | 7 tools | 4 tools | 4 tools |
| Add `--allow-input` | +4 | +4 | +4 |
| Add `--allow-fixture-actions` | +6 | +6 | +3 |

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

## Default observation and screenshots

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

## Opt-in input

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
# Replace with the absolute path to your extracted executable.
command = "C:\\path\\to\\sdvkit.exe"
args = ["project", "review", "mcp", "serve", "--topology", "network-2", "--role", "host"]
cwd = "C:\\path\\to\\the\\lab-owning-project"
enabled_tools = [
  "stardew_runtime_get",
  "stardew_review_get",
  "stardew_mods_list",
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

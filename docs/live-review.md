# Test and review a mod in game

Use **smoke** to check that one standalone C# mod loads and completes bounded ticks. Use **review** to exercise a specific feature, inspect content, or collect visual/persistence evidence. See the [capability matrix](README.md#capability-matrix) before selecting content packs or multiplayer.

## Prepare the lab

Complete [installation](../README.md#install), retaining `$sdvkit` as the absolute executable path. Choose one current directory to own `.sdvkit/` and use it for every command in the session. Resolve the target and any companions to explicit local paths.

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect .\ExampleMod --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Continue only with one complete installation and a supported target. If discovery is ambiguous or the installation is outside discovery, validate `doctor --game-path <directory> --json`, then pass the same `--game-path <directory>` to each `lab start`, `lab test-save`, `lab smoke`, `project smoke`, or `project review start` that launches/builds against it. Status, stop and reset use retained ownership and do not accept a new installation selection. Identify the owner of any active lab/review; wait for another task's verified teardown instead of stopping its game. Only one task should operate a lab context at a time.

## Automated smoke

```powershell
& $sdvkit project smoke .\ExampleMod --topology single --json
```

For explicitly selected local multiplayer coverage, substitute `--topology network-2`. Smoke builds and packages the target itself: do not precede it with duplicate build/package commands just for the same proof. The source project's `.sdvkit/` owns build output; the current lab root's `.sdvkit/` owns runtime state.

Accept exit `0`, `state=passed`, exact target ID/version/build identity and sufficient ticks for every expected role, no load errors, plus `fixtureReset=true` and `stagingRemoved=true`. Logs are named in the result. A pass proves game-side loading and bounded execution, not all mod features or an in-memory DLL hash.

## Start a review

```powershell
& $sdvkit project review start .\ExampleMod --topology single --json
& $sdvkit project review status --topology single --json
```

To review one code project within an existing repository:

```powershell
& $sdvkit project review start .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --topology single --json
```

The root-relative selector follows the [toolkit selection rules](toolkit.md#create-build-and-package). It is carried through both the isolated build and package and recorded as `projectFile` in owned review artifacts. It applies only to the target; each `--companion` project directory must still resolve uniquely. The selected game installation is shared by target/companion builds, AlwaysOn, and the actual role launches. Review stages one standalone code-mod package per target or companion; hybrid/bundled-pack build/package support does not make a multi-manifest bundle reviewable. Supply separately supported ready packs through `--content-pack`. Both single and network-2 support the selected standalone C# target. `project smoke` retains its unique-project rule and accepts only installation selection.

A selected code project may have nested C# test projects and QA mods with their own
code manifests. For a root mod plus a separate live-test companion, select both
explicitly:

```powershell
& $sdvkit project review start .\ExampleMod --project 'ExampleMod.csproj' --companion '.\ExampleMod\tests\LiveHarness' --topology single --json
```

Only the selected project's validated standalone package becomes the target.
Nested test mods are not added automatically; omit `--companion` when the test mod
is not needed. The selected project must exclude test sources and unwanted files
from its normal compilation and package. A package containing another manifest
is still rejected, and omitting `--project` still requires a unique code project.

A retained review cannot switch explicit projects within the same root. Its ownership marker also retains `gamePath`, so both explicit and automatically discovered installations must match on network-2 restart after stop. Older retained staging without installation binding remains readable for status/stop/reset, but must be reset and rebuilt before restart. Stop/reset the exact owned review before changing its selection. Network-2 restart must repeat the exact target/project/companions/content-pack selection.

To review an already packaged code mod, extract it yourself and select the single mod directory containing `manifest.json` and its `EntryDll`:

```powershell
& $sdvkit project review start .\ExtractedMod --game-path $gamePath --topology single --json
```

With no C# project present, review copies the ready artifact unchanged and does not build or package it. This also supports `network-2`. Do not pass `--project` for a ready artifact. Select one root mod, not a ZIP or a multi-mod bundle; dependencies still require explicit companions. Missing or unsafe `EntryDll` paths fail preparation. SMAPI validates DLL loading and compatibility; check review status and selected-mod diagnostics for load failures. Supplied bytes and their staged build identity are preserved, but staging alone does not prove a valid or loaded mod.

Review stays running until stopped. It starts windowed at 1280x720, keeps the SMAPI terminal available, and permits subsequent resize/UI-scale testing. Confirm the expected target is loaded; a built/staged artifact is not load confirmation.

For selected dependencies, add repeatable `--companion .\ReadyCompanion` and `--content-pack .\ExamplePack`. A content-pack target itself needs its provider explicitly:

```powershell
& $sdvkit project review start .\ExamplePack --topology single --companion .\ContentPatcher --json
```

These paths are examples: select your actual local provider/companions. SDVKit does not search normal Mods or download dependencies. A content-pack target supports single only.

## Use the disposable world

While all lab roles are stopped, prepare the registered baseline if absent:

```powershell
& $sdvkit lab test-save --topology single --json
& $sdvkit project review start .\ExampleMod --topology single --test-save --json
& $sdvkit project review status --topology single --json
```

Require `testSave.state=ready`, `phase=passed`, the expected Save/fixture IDs, and `identityVerified=true` before fixture/world-dependent commands. An existing verified baseline can be reused; do not recreate it for every read. Plain single review without `--test-save` instead uses its own persistent isolated profile.

For a C# host/farmhand review, prepare that same baseline and start with `--topology network-2` (without `--test-save`). Confirm both exact roles, loaded target/build identity, and reciprocal joined-pair proof. Each role has a separate isolated profile.

## Local split-screen review

Start a single review with `--test-save` and require its exact fixture and target
to be ready. Then explicitly opt that running review into local split-screen:

```powershell
& $sdvkit project review command "sdvkit split-screen join" --topology single --json
& $sdvkit project review command "sdvkit split-screen status screen=0" --topology single --json
& $sdvkit project review command "sdvkit split-screen status screen=1" --topology single --json
```

The native game runner creates one additional SMAPI screen and joins only the
fixture's marked local farmhand. This reuses the single review's exact process,
staging, work save, and stop/reset ownership; `network-2` uses two processes and
cannot establish this behavior. Ordinary single reviews retain their single-player
guard. Do not run this alongside another native multiplayer lab on the same port.

Inspect the owned SMAPI log for the matching fixture, joined screen ID and farmer
ID. Review status additionally reports `testSave.localSplitScreen=true` once
selected. The `SDVKit local screen` log entry contains that screen's current runtime,
player, menu report, and active screen IDs. Screen IDs can change after leaving
and rejoining; use the reported IDs instead of assuming every farmhand is screen 1.
`commandWritten=true` still proves only delivery.

SMAPI's native `screen=<id>` command argument selects the screen for input,
fixture navigation, screenshots, and explicitly selected mod commands:

```powershell
& $sdvkit project review command "sdvkit input press K screen=1" --topology single --json
& $sdvkit project review command "sdvkit split-screen status screen=1" --topology single --json
& $sdvkit project review command "sdvkit screenshot viewport local-journal-1 screen=1" --topology single --json
```

`K` is only an example mod binding. Inspect the menu before acting. Cursor,
button/chord/gesture progress, and menu revisions are independent per screen.
Viewport screenshots contain the selected screen's rectangle; use unique labels
across both screens because they share the same isolated screenshot folder.
Native text-event injection is unavailable while split-screen is active because
the screens share a window. Typed CLI inspection and MCP remain bound to screen 0;
use the screen-selected console path to observe or operate the farmhand.

To close only the second screen, send `sdvkit split-screen leave` from screen 0.
Require the confirmed removal log and inspect the host's retained state. A later
`join` reuses the same marked farmhand. The host remains the only authority for
fixture save and world mutations. Save through normal game behavior or the
existing host fixture-save tool, confirm completion, stop, and restart the same
`single --test-save` review. Explicitly `join` again to verify both farmers after
reload. Finish with the usual single stop and reset; leaving one screen does not
save or reset the fixture.

## Exercise behavior and collect evidence

On Windows, the review console starts minimized and is shown without activation once SMAPI is ready. It remains available for manual commands.

Use an idle SMAPI console with no concurrent manual typing. Console delivery proves only delivery; verify the actual effect through matching logs, state, or images.

```powershell
& $sdvkit project review command "sdvkit screenshot viewport menu-before" --topology single --json
& $sdvkit project review command "sdvkit input cursor 200 100" --topology single --json
& $sdvkit project review command "sdvkit input press MouseLeft" --topology single --json
& $sdvkit project review command "sdvkit input cursor clear" --topology single --json
& $sdvkit project review command "sdvkit screenshot viewport menu-after" --topology single --json
```

Coordinates are an example, not a known button location: inspect the current viewport before choosing them. A screenshot succeeds only when AlwaysOn confirms the path and that exact PNG exists below the selected profile; inspect the image for visual acceptance. Use new labels, 1–64 ASCII letters/digits/`-`/`_`; captures never overwrite.

`sdvkit screenshot <label>` captures the loaded map; `sdvkit screenshot viewport <label>` captures the rendered viewport, including menus/title/loading. Input and viewport console commands can diagnose pre-world state only while their exact process, staging, target-load, and role bindings remain valid. Other commands retain fixture/join readiness gates.

Mouse input uses only the process-local virtual cursor. Set it before mouse-button or `MouseWheelUp`/`MouseWheelDown` presses; wheel input also needs an active menu. Never move the physical pointer, focus the game, or use desktop automation to substitute for review input. A successful injection still requires a separate check of the intended effect.

### Close a menu

For routine autonomous menu tests, prefer keyboard or process-local mouse input. Reserve controller buttons for tests explicitly exercising controller behavior: a synthetic controller press can temporarily report a connected controller when no physical controller is connected, then return to disconnected on a later sample.

1. Read the current menu through `project review menu` or `stardew_menu_get`. If inspection does not support that menu, inspect a fresh viewport screenshot instead. Do not send a close input when the intended menu is already closed.
2. Send one `Escape` press, or click the observed visible close button once through the process-local virtual cursor. Use current geometry and a fresh `uiRevision` for bounded clicks; do not guess coordinates or hold/repeat the close input.
3. Wait for input completion, then read the menu again (or inspect a fresh viewport screenshot). An input acknowledgement proves delivery/release, not that the intended menu closed.
4. Stop when the intended menu is closed. A returned parent menu is a separate state, not permission for another close press. If the same menu remains, inspect its supported close behavior before choosing another action. Do not blindly retry, cycle through controller buttons, or continue an uncertain action; an unexpected inventory menu is a result to record and investigate.

This is agent guidance, not a change to controller simulation or proof that menu-close regressions are fixed.

### Input behavior

The adapter supplies one mouse snapshot before SMAPI derives helper state and input events. SMAPI retains ownership of button transitions and event dispatch. UI coordinates are scaled to raw screen pixels; SMAPI's cursor properties use its normal zoom/world-coordinate conversion.

| Action | Stardew input/menu | SMAPI helper and events |
| --- | --- | --- |
| Keyboard or controller button | Supported `IInputHelper.Press` override for one input update | Normal `Pressed` → `Released` → `None` when the physical button is up; `ButtonsChanged`, `ButtonPressed`, `ButtonReleased` |
| Click / bounded scroll / drag | Active-menu coordinates, optional click/drag modifiers; one closed gesture | Separate click edges, one notch per verified sample, or initial press plus bounded held movement; final position retained through the game release update |
| Atomic chord / hold | 1-8 exact button names, reapplied together for 1-120 input updates | One `Pressed`, then `Held` for remaining updates, then `Released`; acknowledgement follows the release sample |
| Mouse button | Same override at the virtual cursor | Same button lifecycle; event cursor and `GetCursorPosition()` use the shared virtual coordinates |
| Cursor set/change | Scaled virtual position; unchanged coordinates do not create another movement | Normal `CursorMoved` old/new transition when SMAPI observes a changed position |
| Wheel up/down | One queued cumulative `+120`/`-120` delta through normal menu handling | One corresponding `MouseWheelScrolled` observation; no direct second menu call |
| Cursor clear, title, controlled exit, command exception | Stop owned reapplication before restoring physical coordinates; cancel an unconsumed wheel notch | Keep the consumed wheel origin to avoid an opposite notch; allow bounded updates to drain button state; subsequent physical wheel deltas remain visible |

Send actions sequentially and observe their effects before sending the next. A second wheel notch before consumption is rejected. Physical input, other mods' suppression, chat, loading, and saving retain SMAPI's normal rules; the adapter rejects overlapping physical/external button state and does not guarantee events SMAPI skips in those contexts. Cursor changes and wheel/button input use the existing bounded background activity window.

Wheel input requires a sampled raw counter. Counter overflow is rejected before changing the virtual offset. If physical wheel input changes the counter beyond its available range after queueing, the adapter cancels all pending owned input, clears the virtual cursor, and logs an error; observe the effect rather than retrying an acknowledged command blindly. Release is reported as unconfirmed if physical/external input remains down; physical input is never suppressed to manufacture a release. An unrepresentable consumed origin retains the last output and reports failure instead of emitting a synthetic reverse notch.

For a chord, first obtain `uiRevision` from `project review menu` or `stardew_menu_get`, then send `sdvkit input chord <1-120 ticks> <uiRevision> <1-8 button names>` through the existing review console command, or use `stardew_input_chord` with `--allow-input`. Changes to menu/root/page/known component geometry or UI scale before the first input update reject the request. Changes while more hold updates remain cancel reapplication; a menu opened by the final press does not invalidate its own release. Camera movement preserves the normalized screen-local revision. Ctrl+V is unsupported, including a physical modifier joining virtual input, because Stardew polls that combination for clipboard paste.

For bounded text entry, use `stardew_input_text` with `--allow-input` and the exact NamingMenu `textField.id` plus fresh `uiRevision`. See [opt-in input](mcp.md#opt-in-input) for Unicode limits, per-poll focus checks, cancellation, native command-key limits and separate accepted/persisted-state proof. The low-level console transport is `sdvkit input text <uiRevision> <field-id> <UTF-8-base64>`; it requires a world-ready review and does not accept plaintext or paste.

For active-menu mouse gestures, use `sdvkit input click <x> <y> <mouse-button> <1|2> <uiRevision> [modifiers...]`, `sdvkit input scroll <x> <y> <signed-notches> <uiRevision>`, or `sdvkit input drag <x> <y> <end-x> <end-y> <mouse-button> <movement-updates> <uiRevision> [modifiers...]` through `project review command`. MCP equivalents and complete examples are in [opt-in input](mcp.md#opt-in-input). Scroll accepts -20 through -1 (down) or 1 through 20 (up). Drag duration is 1-120 movement updates after the initial press; it produces duration+1 down samples before release. Click and drag accept five mouse buttons and distinct Left/Right Shift, Control or Alt modifiers.

Gestures check the full revision initially, then retain root/active-page/child and viewport/scale continuity while allowing their own component/value changes. The final requested down/movement action may switch or close a menu; viewport/scale must remain stable through release. Gesture completion waits until the game update after the final release sample, preserving the last virtual position for native release callbacks. Success retains that position. Cancellation clears it after the release opportunity; a skipped game callback uses a bounded common-input fallback and reports failure/unconfirmed release. Responses report partial `completedSteps` and final `cursorSet` plus coordinates only when still set. Validation before acceptance has no cursor effect. Wheel consumption stays inside the matching input sample and retains its cumulative origin even on failure, so cancellation never replays a notch or resets it in the opposite direction.

Both request-bound single presses and chords use the same completion path and acknowledge only after the release input update. The existing single-button JSON shape is unchanged. Chord responses additionally include canonical `buttons`, `durationTicks`, observed `startTick` and `endTick`, and `released`; unobserved tick fields are absent. Console delivery alone remains delivery evidence.

The adapter is bound to the exact input instance. Its SMAPI 4.5.2 integration reapplies public `IInputHelper.Press` immediately before `SInputState.TrueUpdate`. It reads `CustomPressedKeys` and the public `ButtonStates` property to verify queue consumption and a completed state derivation, including updates whose exceptions SMAPI catches internally. On a failed update it removes only queue entries proven absent before that exact prefix and added by its own calls; it never changes suppression state or dispatches private events. Installation validates the required method/field/property types and rolls back all input patches if unavailable. Cancellation stops reapplication and drains a no-injection sample through the existing owned transport and action lock. No input cleanup calls `Suppress`.

Focused offline tests cover coordinate and cumulative-delta bookkeeping. They do not prove SMAPI event dispatch or menu effects: acceptance requires a neutral isolated probe, separately identified physical observations, virtual observations, exact role binding, pointer/foreground stability, and final cleanup.

For network commands, add exactly one `--role host` or `--role farmhand`; do not infer one role's state from the other. Use distinct screenshot labels.

Choose the detailed surface for the task:

- [Inspection](inspection.md): Data, maps, textures, audio, observed mod assets.
- [Fixture reference](lab-reference.md#fixture-command-reference): owned buildings, objects, animals, and natural navigation.
- [MCP](mcp.md): typed observation, screenshots, and separately enabled input/fixture actions.

## Accept an intentional configuration change

A mod's GMCM Save action can change its staged root `config.json`. When that
configuration participated in the reviewed file set, subsequent inspection and
input reject the changed bytes until you explicitly reconcile the selected mod:

```powershell
& $sdvkit project review config-reconcile --mod Example.Mod --topology single --json
& $sdvkit project review menu --topology single --json
& $sdvkit project review diagnostics --mod Example.Mod --topology single --json
```

Use the exact staged `UniqueID` from the original review status. The selected
artifact may be the target, a companion code mod, or an explicitly staged Content
Patcher pack. Its root config must already have existed when staging began;
newly created config files are outside this operation. Reconcile checks the active
single review's launch, process, profile
and complete artifact set. Only the selected regular root `config.json` may differ;
malformed JSON, linked files or directories, changed assemblies/content/manifests,
and another artifact's drift are rejected. Config must be a JSON object of at most
1 MiB; SMAPI-compatible comments and trailing commas are accepted. `network-2` is unsupported.

`state=reconciled` records the previous and accepted config hashes, unchanged
non-config content identity, and an audit/export below the lab's ignored
`.sdvkit/`. Repeated intentional changes retain the preceding reconciliation ID.
`state=unchanged` means the selected config already matches the accepted files.
The original launch/build identity and running process remain the same. Source
files are never updated, and the operation does not rebuild, reload the mod, or
restart Stardew. Observe the intended menu or gameplay effect separately; accepting
saved JSON does not establish that the mod applied its settings in memory.

If `auditWarning=configReconcileAuditUnconfirmed` accompanies a confirmed
`reconciled` result, acceptance succeeded but the final audit write did not.
Rerun the same selection without another config edit to verify its retained export
and repair the receipt. A changed or missing export retains the warning; keep the
reported recovery evidence and finish through the normal owned stop/reset.

Config reconciliation and [CP patch refresh](cp-refresh.md) cannot be combined in
one review. Stop and start a new exact review when switching between them. Reconcile
is currently CLI-only; an already connected MCP client can continue its existing
inspection/input tools after successful reconciliation.

Retain the returned audit/export paths if you need the saved configuration later.
They do not automatically become the next review's input. Explicit export/restage
and restart remain the separate [GMCM persistence workflow](gmcm-authoring.md#validate-export-stop-and-restage-the-saved-config).
Finish with the normal [stop/reset sequence](#finish-or-test-persistence), including
after a rejected reconciliation; never edit ownership markers to accept drift.

## Diagnose selected-mod warnings and exceptions

For a selected Content Patcher change, use the [CP diagnosis recipe](cp-diagnosis.md)
to correlate its informational command replies before inspecting an asset.

Use the staged `UniqueID` from review status. This read-only query works even when
that target is reported as not loaded; it does not replace load/version diagnostics.

```powershell
& $sdvkit project review diagnostics --mod Example.Mod --limit 20 --json
& $sdvkit project review diagnostics --mod Example.Mod --topology network-2 --role host --json
```

The matching native tool is `stardew_mod_diagnostics { "modId": "Example.Mod", "limit": 20 }`.
Both surfaces return the same projection from the selected role's isolated
`StardewValley/ErrorLogs/SMAPI-latest.txt`. Only an active owned review with a fresh
status, exact process/staging identity, and matching AlwaysOn activation launch ID
in that log is accepted. Missing, stale, linked, replaced/unbound or rotated-away
logs return `state=unavailable` with a bounded `errorCode`; old logs are never searched.

`diagnostics` contains the latest matching WARN/ERROR/ALERT entries in file order,
including historical entries before a [CP refresh](cp-refresh.md). Its staged
build hash describes the currently owned files, not the generation which emitted
each older message or successful reload; status's refresh receipt reports pending recovery.
The entries include recognized exception/stack continuation lines. `attribution=logger`
means the SMAPI logger name matches one staged manifest name; `ambiguousLogger`
means that name is shared/reserved, and `sharedMention` means SMAPI or the pack's
selected provider mentioned the mod. None proves that the selected mod caused
the failure. `phase` is `loading`, `runtime`, or `unknown` based only on observed
SMAPI phase markers; time is the log's local clock without an inferred date.

The reader scans at most the last 4 MiB and validates activation in the first
256 KiB. `counts.total` counts recognized entries in that scan, `matching` counts
selected warning/error entries, and `returned` counts the result entries. If
`totalIsExact=false`, counts describe only the complete scanned portion, not the
whole file. The default result limit is 20 (1–100); each entry is limited to
32 lines of 1,024 characters. `truncated`, `source.scanTruncated`, and
`source.incompleteLineWithheld` identify result, scan, or partial-write limits.
A ready result with zero matches means none in the inspected portion.

Recognized absolute paths and secret-bearing lines are withheld; relative source
locations survive. Unrelated continuation text and lines naming other staged mods
are omitted. `withheldLines` counts lines wholly or partly withheld, with a
placeholder when no useful text remains. This is a bounded diagnostic projection,
not complete sanitization of arbitrary mod-authored text. No raw-log/path input,
automatic upload, or Content Patcher command execution is provided.

To investigate omitted context locally, resolve review status's selected-role
path against `labRoot`. For single, `persistentSavesPath` ends in `Saves`: take
its parent directory, then append `ErrorLogs/SMAPI-latest.txt`. For network-2,
append `ErrorLogs/SMAPI-latest.txt` directly to the resolved
`roles[].stardewDataPath` for the selected role. Keep that exact source and the
returned launch/role identity together; do not substitute a normal-player log.

## Finish or test persistence

A real restart is required when accepting save/reload, persistence, join/resume, or lifecycle behavior. It is not automatically required for every content query or visual check.

When persistence is in scope, save through the selected mod's supported interface or the authorized MCP `stardew_fixture_save` tool. Confirm completion, stop the exact process(es), then start the same target/topology/companions again and verify the saved state and identities. An acknowledgement of input or a serialized file alone does not prove reload behavior.

Always finish the owned session:

```powershell
& $sdvkit project review stop --topology single --json
# Required after a single --test-save review, once stopped:
& $sdvkit project review reset --topology single --json
```

For network-2:

```powershell
& $sdvkit project review stop --topology network-2 --json
& $sdvkit project review reset --topology network-2 --json
```

Single stop removes its owned review staging. A fixture-backed single stop preserves the work save for restart; reset restores its baseline. Network stop preserves the work save **and staging** for a real restart; final network reset restores the baseline and removes staging. Do not reset between the two halves of a persistence test.

An unconfirmed isolated-option restore after verified exit is a **warning**. Unknown process identity, unconfirmed exit, unsafe paths, or missing fixture/staging cleanup remain blocking. Let the existing stop/reset commands select owned paths; never delete ownership records manually or kill by process name. See [lifecycle details](lab-reference.md).

## Troubleshooting

| Symptom | Response |
| --- | --- |
| `testSaveBaselineMissing` | With all roles stopped, run `lab test-save --topology single`; then retry the intended start. |
| Missing provider/dependency | Pass its explicitly selected local path; do not install or guess it. |
| `commandWritten=true`, effect unclear | Inspect the matching game log, current state, or screenshot; do not treat console silence as success. |
| `inputBindingChanged` / disturbed observation | Inspect the binding and current state. Repeat only an understood, safe check; never weaken ownership/foreground checks. |
| Action `mayHaveRun=true` | Read current state before any repeat; do not blindly repeat a save, click, or mutation. |
| Stop/reset blocked | Retain the result and owned logs; resolve the first evidenced cause without manual destructive cleanup. |

Report build, packaging, SMAPI load, functional behavior, visual behavior, and cleanup separately. A local two-role result applies to that tested scenario, not all multiplayer or mod behavior.

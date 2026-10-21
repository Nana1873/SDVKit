# Test and review a mod in game

Use **smoke** to check that one standalone C# mod loads and runs its ticks. Use **review** to exercise a feature, inspect content, or collect visual or persistence evidence. Check the [capability matrix](README.md#capability-matrix) before you select content packs or multiplayer.

Terms such as [owned](glossary.md#reviews-and-ownership), [role](glossary.md#topology-roles-and-screens) and [test save](glossary.md#worlds-and-fixtures) are in the glossary. Process, staging and cleanup mechanics are in the [lab reference](lab-reference.md).

## Prepare the lab

1. Complete [installation](../README.md#install). Keep `$sdvkit` as the absolute path to the executable.
2. Pick one [lab root](glossary.md#places). Run every command of the session from it.
3. Resolve the target mod and any companions to local paths.
4. Check the installation, the target and the lab:

```powershell
& $sdvkit doctor --json
& $sdvkit project inspect .\ExampleMod --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Continue only with one complete installation and a supported target. If discovery is ambiguous, or the game is installed outside discovery, check it with `doctor --game-path <directory> --json`. Then pass the same `--game-path <directory>` to every command that builds or launches against it: `lab start`, `lab test-save`, `lab smoke`, `project smoke` and `project review start`. Status, stop and reset use the installation recorded at start and do not accept a new one.

Only one task may operate a lab root at a time. If another task owns an active lab or review in this lab root, wait for its verified stop. Do not stop its game. Tasks with separate lab roots can run reviews at the same time; see [several lab roots](lab-reference.md#several-lab-roots-on-one-pc).

## Automated smoke

```powershell
& $sdvkit project smoke .\ExampleMod --topology single --json
```

For local multiplayer coverage, use `--topology network-2` instead. Smoke builds and packages the target itself, so do not build or package it first. The mod workspace's `.sdvkit/` holds build output. The lab root's `.sdvkit/` holds runtime state.

A smoke passes when all of these hold:

- exit `0` and `state=passed`;
- the expected target ID, version and build identity in every role, each with enough ticks;
- no load errors;
- `fixtureReset=true` and `stagingRemoved=true`.

The result names the logs. A pass proves that the game loaded the mod and ran its ticks. It does not prove every mod feature or measure the DLL in memory.

## Start a review

```powershell
& $sdvkit project review start .\ExampleMod --topology single --json
& $sdvkit project review status --topology single --json
```

A review runs until you stop it. It starts windowed at 1280x720, keeps the SMAPI console available, and allows later resize and UI-scale tests. Confirm that the expected target loaded: a built or staged mod is not proof of loading.

**One project in a larger repository.** Select it with `--project`:

```powershell
& $sdvkit project review start .\ExistingRepository --project 'src\ChosenMod\ChosenMod.csproj' --game-path $gamePath --topology single --json
```

The selector is relative to the root and follows the [toolkit selection rules](toolkit.md#create-build-and-package). It applies to the target's build and package, and review artifacts record it as `projectFile`. Each `--companion` folder still has to resolve to one project by itself. Target, companions, GameSide and the launched games share the selected installation.

Review stages one standalone code-mod package per target or companion. Both topologies support a standalone C# target. `project smoke` still needs a unique project and accepts only `--game-path`. For a source bundle, build and package it, extract it, then [select the ready bundle](#select-a-ready-bundle).

**A test companion.** Nested C# test projects or QA mods with their own manifests are not added automatically. To stage a live-test companion next to the root mod, select both:

```powershell
& $sdvkit project review start .\ExampleMod --project 'ExampleMod.csproj' --companion '.\ExampleMod\tests\LiveHarness' --topology single --json
```

Only the selected project's package becomes the target. Its normal build and package should exclude test sources and unwanted files: a package that contains another manifest is rejected. Without `--project`, the folder needs a unique code project.

**A packaged code mod.** Extract it yourself. Select the one mod folder that contains `manifest.json` and its `EntryDll`:

```powershell
& $sdvkit project review start .\ExtractedMod --game-path $gamePath --topology single --json
```

With no C# project present, review copies the mod unchanged, without building or packaging. This works in both topologies. Do not pass `--project`. Select one root mod, not a ZIP or a multi-mod bundle. Dependencies still need `--companion`.

A missing or unsafe `EntryDll` path fails preparation. SMAPI checks DLL loading and compatibility, so read review status and [diagnostics](#diagnose-selected-mod-warnings-and-exceptions) for load failures. Staging keeps the supplied bytes and build identity, but does not prove a valid or loaded mod.

**Changing the selection.** Stop and reset a review before you change its target, project, companions, content packs or game path. A `network-2` restart repeats the same selection. See [retained selection](lab-reference.md#retained-selection).

### Select a ready bundle

For `single`, select the outer folder of an extracted bundle. Add every direct pack child with a repeatable `--content-pack`:

```powershell
& $sdvkit project review start .\ReadyBundle --content-pack .\ReadyBundle\ArrivalPack --game-path $gamePath --topology single --test-save --json
```

The folder holds one code mod, at its root or in a direct child folder, plus the selected pack folders. Any other manifest rejects preparation before launch; the [bundle checks](lab-reference.md#review-staging-and-retained-state) list the rest. Stop and reset before you change the outer folder, companions or packs.

ModBuildConfig's `ContentPacks` packaging produces this layout. Source bundles and `network-2` bundles are unsupported. The [Bundle Arrival recipe](bundle-authoring.md) covers build, package, selection, code and pack effects, and restart checks.

### Add dependencies

Add each dependency with a repeatable `--companion .\ReadyCompanion` or `--content-pack .\ExamplePack`. A content-pack target needs its provider as a companion:

```powershell
& $sdvkit project review start .\ExamplePack --topology single --companion .\ContentPatcher --json
```

These paths are examples; select your own local copies. SDVKit does not search your normal `Mods` folder or download dependencies. A content-pack target works only in `single`.

## Use the disposable world

While all lab roles are stopped, create the test save baseline if it does not exist yet:

```powershell
& $sdvkit lab test-save --topology single --json
& $sdvkit project review start .\ExampleMod --topology single --test-save --json
& $sdvkit project review status --topology single --json
```

A completed scenario can proceed to the normal clean-stop step even if its active heartbeat is older than five seconds: `lab test-save` requires the exact launch, process and fixture identity, `testSave.phase=passed`, `identityVerified=true` and at least 120 observed ticks. The workflow passes only after clean stop confirms exit and the final scenario result. Stale pending, failed or unverified scenarios still fail; missing or mismatching final markers remain `cleanStopNotConfirmed`, and a stop timeout remains `cleanStopTimedOut`. General `lab status` freshness checks are unchanged. Loading the save blocks the game's main thread, so the heartbeat can also pause while the test save is still loading or waiting; `lab test-save` then keeps polling for up to 30 seconds after the marker becomes stale (so a heartbeat up to about 35 seconds old) before it fails with `gameSideStale`.

Before fixture or world commands, require `testSave.state=ready`, `phase=passed`, the expected Save and fixture IDs, and `identityVerified=true`. Reuse an existing verified baseline. Do not recreate it for each read. A plain `single` review without `--test-save` uses its own persistent isolated profile instead.

For a C# host and farmhand review, prepare the same baseline. Start with `--topology network-2`, without `--test-save`. Confirm both roles, the loaded target and build identity, and that each role sees the other joined. Each role has its own isolated profile. The host's fixture load can pause its heartbeat; while the host still waits for its exact fixture, the start keeps polling for up to 30 seconds after the marker becomes stale before it fails with `gameSideStale`.

### Leave and rejoin the network farmhand

The farmhand can leave and rejoin while the host keeps running. Start from a passed pair with both players idle and no menu, event, fade or save in progress.

```powershell
& $sdvkit project review network leave --role farmhand --json
& $sdvkit project review network status --role host --json
& $sdvkit project review network status --role farmhand --json
& $sdvkit project review network join --role farmhand --json
```

1. `leave`: the host first acknowledges the current pair. The farmhand then returns to a clear title screen, and its process stays alive. The host continues only after it sees the real disconnect.
2. `status` for each role: while the farmhand is away, pair status is intentionally not ready.
3. `join`: reuses the existing loopback connection and the same saved farmhand. The pair passes again after both roles reach 120 verified unfocused ticks.

Commands time out instead of retrying silently. `stop` and `reset` work as usual.

While the farmhand is away:

- An MCP client already bound to the host can still call `stardew_runtime_get`. A new host client cannot bind.
- Other pair-dependent reads, input and fixture actions are refused until the pair passes again.
- The farmhand client cannot read.

Returning to title gives the farmhand a new role session. The old farmhand client stays rejected after rejoin, so start a new `--role farmhand` MCP client. The host session does not change.

Console delivery and phase text do not prove behavior. Verify host continuity, rejection of the old client, the newly bound farmhand, and the passed pair after rejoin.

## Local split-screen review

Start a `single` review with `--test-save`. Wait until its fixture and target are ready. Then opt that running review into local split-screen:

```powershell
& $sdvkit project review split-screen join --json
& $sdvkit project review split-screen status --screen 0 --json
& $sdvkit project review split-screen status --screen 1 --json
```

The game's own runner creates one more SMAPI screen and joins only the fixture's marked local farmhand. Split-screen reuses the single review's process, staging, work save, stop and reset. `network-2` uses two processes and cannot test this. Ordinary single reviews keep their single-player guard. Do not run another local multiplayer lab on the same port at the same time.

Check the SMAPI log for the matching fixture, joined screen ID and farmer ID. Review status then reports `testSave.localSplitScreen=true`. The `SDVKit local screen` log entry lists that screen's runtime, player, menu report and active screen IDs. Screen IDs can change after leave and rejoin. Use the reported IDs; do not assume a farmhand is screen 1. `commandWritten=true` still proves only that the command ran.

**Address a screen.** `--screen <id>` selects the screen for `project review call` tools and for lifecycle status. For a mod console command, use SMAPI's own `screen=<id>` argument.

```powershell
& $sdvkit project review call stardew_input_press '{"button":"K"}' --screen 1 --allow-input
& $sdvkit project review split-screen status --screen 1 --json
& $sdvkit project review call stardew_screenshot_capture '{"mode":"viewport","label":"local-journal-1"}' --screen 1
```

`K` is only an example key binding; inspect the menu before acting. Cursor, button, chord and gesture progress, and menu revisions are separate per screen. A viewport screenshot shows the selected screen's area. Both screens share one screenshot folder, so use unique labels. Text-event injection is unavailable during split-screen because the screens share a window.

For menu reads or a typed MCP client on one screen, use the same selector:

```powershell
& $sdvkit project review menu --topology single --screen 1 --json
& $sdvkit project review mcp serve --topology single --screen 1 --allow-input
```

The server reports its fixed `screenId`, `farmerId` and opaque `contextId` in `stardew_runtime_get`. It sends runtime, menu, viewport screenshot and permitted input calls to that screen. A screen-bound server has no text tool. Leave and rejoin invalidate the old client, even if Stardew reuses the numeric ID. Start a new client for the newly reported context.

**Close the second screen.** Run `project review split-screen leave --screen 0 --json` from the host screen. Require the confirmed removal log and check the host's state. A later `join` reuses the same marked farmhand.

**Test persistence.** Only the host saves the fixture or changes the world.

1. Save through normal game behavior or the host's fixture-save tool. Confirm completion.
2. Stop, then restart the same `single --test-save` review.
3. Run `join` again and verify both farmers after reload.
4. Finish with the usual single stop and reset. Leaving a screen neither saves nor resets the fixture.

## Exercise behavior and collect evidence

On Windows, the review console starts minimized and appears without taking focus once SMAPI is ready. SDVKit sends commands to the review's [loopback command endpoint](lab-reference.md#command-endpoint), not by typing into the console, so manual console use does not interfere. For a console command, `commandWritten=true` means it ran on the game thread. A tool called through `project review call` returns its own confirmed result. In both cases, verify the effect through logs, state or images.

```powershell
& $sdvkit project review call stardew_screenshot_capture '{"mode":"viewport","label":"menu-before"}'
& $sdvkit project review call stardew_input_cursor_set '{"x":200,"y":100}' --allow-input
& $sdvkit project review call stardew_input_press '{"button":"MouseLeft"}' --allow-input
& $sdvkit project review call stardew_input_cursor_clear --allow-input
& $sdvkit project review call stardew_screenshot_capture '{"mode":"viewport","label":"menu-after"}'
```

The coordinates are an example; inspect the current viewport before you choose them.

`project review command` runs console commands of the reviewed mod and SMAPI. It rejects SDVKit's own console lines (`sdvkit ...`); use the tools above. The title-screen case below is the only exception.

**Screenshots.** Mode `map` captures the loaded map. Mode `viewport` captures what is rendered, including menus, the title screen and loading. A capture succeeds only when GameSide confirms the path and the PNG exists below the selected profile. Inspect the image for visual acceptance. Labels are 1–64 ASCII letters, digits, `-` or `_`. Use a new label each time; captures do not overwrite.

**Title screen.** In a `--test-save` review, the tools need the fixture to be loaded. While it is not, for example at the title screen during a language change, a single review's `project review command` still accepts two kinds of line: `sdvkit input press|chord|cursor|click|scroll|drag ...` and `sdvkit screenshot viewport <label>`. Once the fixture loads again, it refuses them with `reviewConsoleUseTool`. Fixture and world actions keep their fixture and join readiness checks.

**Mouse input** uses only the process-local virtual cursor. Set the cursor before a mouse button or `MouseWheelUp`/`MouseWheelDown` press. Wheel input also needs an open menu. Never move the physical pointer, focus the game, or use desktop automation instead of review input. A successful injection still needs a separate check of its effect.

**Network reviews.** Add exactly one `--role host` or `--role farmhand` to each command. Do not infer one role's state from the other. Use distinct screenshot labels.

**Progression.** For one NPC friendship, mail ID and quest ID, use the read-only [progression read contract](progression-authoring.md) and its [live test guide](test-a-progression-read.md). It first supports an unbound single-player test-save review. It does not list people, infer past completion or edit state.

Choose the detailed surface for the task:

- [Inspection](inspection.md): Data, maps, textures, audio, observed mod assets.
- [Fixture reference](lab-reference.md#fixture-command-reference): buildings, objects, animals and natural navigation in the test save.
- [MCP](mcp.md): typed observation, screenshots, and separately enabled input and fixture actions.

### Close a menu

For routine menu tests, prefer keyboard or process-local mouse input. Use controller buttons only to test controller behavior. With no controller connected, a synthetic press can briefly report one as connected, then disconnected.

The [neutral single-review proof](examples/menu-close-probe/README.md#observed-behavior-2026-09-12) showed the consequence in native Auto mode with the raw controller disconnected. One B press closed the root menu and went `Pressed` → `Released` → `None`. Stardew then opened the inventory on the disconnect. With a parent menu remaining, the child closed and no inventory opened. One Escape, or one click on the visible close button, closed the tested menu without that controller transition. These observations do not fix controller close or cover other menus and controller setups.

1. Read the current menu through `project review menu` or `stardew_menu_get`. If inspection does not support that menu, inspect a fresh viewport screenshot instead. Do not send a close input when the menu is already closed.
2. Send one `Escape` press, or click the visible close button once with the virtual cursor. Use current geometry and a fresh `uiRevision` for clicks. Do not guess coordinates, hold the input or repeat it.
3. Wait for input completion. Then read the menu again or inspect a fresh viewport screenshot. An input acknowledgement proves delivery and release, not that the menu closed.
4. Stop when the menu is closed. A returned parent menu is a new state, not permission for another close press. If the same menu remains, check how it supports closing before you act again. Do not retry blindly, cycle controller buttons or continue an uncertain action. Record and investigate an unexpected inventory menu.

This guidance does not change controller simulation or prove menu-close regressions fixed. For a deeper investigation, the [neutral menu-close probe](examples/menu-close-probe/README.md) records native callbacks, root and child menus, input states and the following ticks in a single fixture.

### Input behavior

Send actions one at a time. Observe each effect before you send the next. GameSide feeds input in through SMAPI, so SMAPI keeps its normal button transitions and events. UI coordinates are scaled to raw screen pixels. SMAPI's cursor properties use its normal zoom and world-coordinate conversion.

| Action | Stardew input and menu | SMAPI helper and events |
| --- | --- | --- |
| Keyboard or controller button | Supported `IInputHelper.Press` override for one input update | Normal `Pressed` → `Released` → `None` when the physical button is up; `ButtonsChanged`, `ButtonPressed`, `ButtonReleased` |
| Click, scroll, drag | Active-menu coordinates, optional click or drag modifiers; one closed gesture | Separate click edges, one notch per verified sample, or one press plus held movement; final position kept through the game's release update |
| Chord or hold | 1–8 button names, applied together for 1–120 input updates | One `Pressed`, then `Held` for the remaining updates, then `Released`; acknowledged after the release sample |
| Mouse button | Same override at the virtual cursor | Same button lifecycle; event cursor and `GetCursorPosition()` use the virtual coordinates |
| Cursor set or change | Scaled virtual position; unchanged coordinates create no movement | Normal `CursorMoved` old/new transition when SMAPI sees a changed position |
| Wheel up or down | One queued cumulative `+120`/`-120` delta through normal menu handling | One matching `MouseWheelScrolled`; no second direct menu call |
| Cursor clear, title, controlled exit, command exception | Stop reapplying input before restoring physical coordinates; cancel an unconsumed wheel notch | Keep the consumed wheel origin to avoid a reverse notch; let button state drain over a few updates; later physical wheel input stays visible |

Rules for every action:

- A second wheel notch before the first is consumed is rejected.
- Physical input, other mods' suppression, chat, loading and saving follow SMAPI's normal rules. Overlapping physical or external button state is rejected. Events that SMAPI skips in those contexts are not guaranteed.
- Ctrl+V is unsupported, including a physical modifier combined with virtual input. Stardew polls that combination for clipboard paste.
- Cursor changes and wheel or button input use the existing background activity window.
- Native mod controls that read `Mouse.GetState()` during the player update also see the virtual cursor. See [mouse state for native controls](#mouse-state-for-native-controls). Verify the control's effect for your mod.

**Chord.** Read `uiRevision` from `project review menu` or `stardew_menu_get`. Then call `stardew_input_chord` with `--allow-input`. A change to the menu, root, page, known component geometry or UI scale before the first input update rejects the request. A change while hold updates remain cancels the rest. A menu opened by the final press does not cancel its own release. Camera movement keeps the screen-local revision.

**Text.** Use `stardew_input_text` with `--allow-input`, the NamingMenu `textField.id` and a fresh `uiRevision`. [Opt-in input](mcp.md#opt-in-input) covers Unicode limits, focus checks, cancellation, command-key limits and the separate proof of accepted and persisted state.

**Mouse gestures.** Call `stardew_input_click`, `stardew_input_scroll` or `stardew_input_drag` with `--allow-input` on an open menu. Arguments and examples are in [opt-in input](mcp.md#opt-in-input).

- Scroll accepts -20 to -1 (down) or 1 to 20 (up).
- Click and drag accept five mouse buttons and distinct Left/Right Shift, Control or Alt modifiers.
- Drag duration is 1–120 movement updates after the first press.
- Only the final down action (including a drag's endpoint hold) or the final scroll notch may switch or close a menu. Earlier menu changes cancel the rest. A viewport or scale change before or during release interrupts the gesture.
- Responses report partial `completedSteps`. They report the final `cursorSet` and coordinates only while the cursor is still set.

**Results.** Single presses and chords acknowledge only after the release update. The single-button JSON shape is unchanged. Chord responses add `buttons` (canonical names), `durationTicks`, the observed `startTick` and `endTick`, and `released`. Unobserved tick fields are absent. Console delivery alone is only delivery evidence.

Offline tests cover coordinate and wheel bookkeeping. They do not prove SMAPI event dispatch or menu effects. Acceptance needs a neutral isolated probe, separately identified physical and virtual observations, role binding, a stable pointer and foreground window, and final cleanup.

## Accept an intentional configuration change

A mod's GMCM Save action can change its staged root `config.json`. If that file was part of the reviewed file set, later inspection and input reject the changed bytes. Reconcile the mod to accept them:

```powershell
& $sdvkit project review config-reconcile --mod Example.Mod --topology single --json
& $sdvkit project review menu --topology single --json
& $sdvkit project review diagnostics --mod Example.Mod --topology single --json
```

- Use the staged `UniqueID` from the original review status.
- The mod may be the target, a companion code mod, or a staged Content Patcher pack.
- Its root config existed when staging began. Config files created later are out of scope.
- Only that root `config.json` may differ. It is a JSON object of at most 1 MiB. SMAPI-style comments and trailing commas are accepted.
- Only `single` is supported, and only from the CLI. An MCP client that is already connected can keep using its inspection and input tools afterwards.
- Config reconciliation and [CP patch refresh](cp-refresh.md) cannot share one review. Stop and start a new review to switch.

Results:

- `state=reconciled` records the previous and accepted config hashes and the unchanged identity of other content. It writes an audit and export below the lab's `.sdvkit/`. Later changes keep the earlier reconciliation ID.
- `state=unchanged` means the config already matches the accepted files.
- `auditWarning=configReconcileAuditUnconfirmed` with `reconciled` means acceptance succeeded but the final audit write did not. Rerun the same selection without another config edit. This verifies the kept export and repairs the receipt. If the export changed or is missing, the warning stays. Keep the reported recovery evidence and finish with the normal stop and reset.

Reconcile does not update source files, rebuild, reload the mod or restart Stardew. The launch, build identity and process stay the same. Observe the menu or gameplay effect separately: saved JSON does not prove the mod applied the settings in memory.

Keep the returned audit and export paths if you need the saved config later; they do not become the next review's input. Export, restage and restart form the separate [GMCM persistence workflow](test-a-gmcm-option.md#3-save-and-accept-the-change). Finish with the normal [stop and reset](#finish-or-test-persistence), also after a rejected reconcile. Never edit ownership markers to accept drift.

## Diagnose selected-mod warnings and exceptions

For a Content Patcher change, first use the [CP diagnosis recipe](cp-diagnosis.md). It correlates the pack's informational command replies before you inspect an asset.

Use the staged `UniqueID` from review status. This read-only query works even when the target is reported as not loaded. It does not replace load and version diagnostics.

```powershell
& $sdvkit project review diagnostics --mod Example.Mod --limit 20 --json
& $sdvkit project review diagnostics --mod Example.Mod --topology network-2 --role host --json
```

The matching MCP tool is `stardew_mod_diagnostics { "modId": "Example.Mod", "limit": 20 }`. Both read the selected role's isolated `StardewValley/ErrorLogs/SMAPI-latest.txt`. Only the log of the active review's current launch is accepted. Otherwise the result is `state=unavailable` with an `errorCode`; older logs are not searched.

How to read the result:

- Entries are the latest matching WARN, ERROR and ALERT entries in file order, with recognized exception and stack lines. They include entries from before a [CP refresh](cp-refresh.md).
- The staged build hash describes the current files. It does not say which generation wrote an older message or reload. Status's refresh receipt reports pending recovery.
- `attribution=logger`: the SMAPI logger name matches one staged manifest name. `ambiguousLogger`: that name is shared or reserved. `sharedMention`: SMAPI or the pack's provider mentioned the mod. None of these proves that the mod caused the failure.
- `phase` is `loading`, `runtime` or `unknown`, based only on SMAPI phase markers in the log. Times use the log's local clock, without a date.
- `counts.total` counts recognized entries in the scan, `matching` counts selected warning and error entries, and `returned` counts the entries in the result. With `totalIsExact=false`, counts cover only the scanned part of the file.
- The default limit is 20 entries (1–100). `truncated`, `source.scanTruncated` and `source.incompleteLineWithheld` flag the result, scan and partial-write limits.
- A ready result with zero matches means none in the inspected part.
- Absolute paths and secret-bearing lines are withheld; `withheldLines` counts them. This is not full sanitization of mod-authored text.

To read the full log locally, resolve review status's path for the selected role against `labRoot`:

- `single`: `persistentSavesPath` ends in `Saves`. Take its parent folder and append `ErrorLogs/SMAPI-latest.txt`.
- `network-2`: append `ErrorLogs/SMAPI-latest.txt` to the selected role's `roles[].stardewDataPath`.

Keep that source together with the returned launch and role identity. Do not substitute a normal-player log.

## Finish or test persistence

A real restart is needed to accept save and reload, persistence, join and resume, or lifecycle behavior. Content queries and visual checks do not need one.

To test persistence:

1. Save through the mod's own interface or the MCP `stardew_fixture_save` tool with `--allow-fixture-actions`. The tool drives Stardew's supported save iterator. The target mod should not assume that its `GameLoop.Saved` handler runs on this path. Keep the completed save result.
2. Stop the review's process or processes.
3. Start the same target, topology and companions again. Do not reset between stop and start.
4. After `SaveLoaded`, verify the saved state and the identities. An input acknowledgement or a written save file alone does not prove reload behavior.

**Return to title.** An intentional return to title suspends a passed single fixture review. It resumes only when Stardew loads the registered save again. That one reload is checked against the live world identity before fixture commands and MCP fixture actions work again. Loading any other save is a terminal failure. This allows title-only settings, such as language, without weakening the fixture boundary. Each return to title from a resumed review allows one reload again.

While the game is at the title screen, status reports `testSaveFailed` (phase `failed`, `identityVerified=false`) and fixture tools answer `reviewTestSaveNotReady`; after the reload it reports phase `passed` with the message `Exact fixture reloaded and revalidated for interactive review.`. The review's scenario log records the order: `returningToTitle`, `returnedToTitle`, `failed`, then `passed`. A refused reload records `The reload after the return to title was not the registered fixture: ...` and stays failed; stop and reset the review.

The Load menu should list only the fixture; older labs may also hold a [stale fixture copy](#stale-fixture-copy).

Always finish the review:

```powershell
& $sdvkit project review stop --topology single --json
# Required after a single --test-save review, once stopped:
& $sdvkit project review reset --topology single --json
```

For `network-2`:

```powershell
& $sdvkit project review stop --topology network-2 --json
& $sdvkit project review reset --topology network-2 --json
```

| Review | `stop` | `reset` |
| --- | --- | --- |
| `single` | Removes staging | Not needed |
| `single --test-save` | Removes staging; keeps the work save for a restart | Restores the baseline and the English language |
| `network-2` | Keeps the work save **and staging** for a restart | Restores the baseline and the English language, and removes staging |

The language is the isolated profile's `languageCode` in `startup_preferences`, which a title-screen language change sets and a stop keeps for the restart. Reset sets it back to `en`, Stardew's default and the language every new lab profile starts with. Other options in the isolated profile are kept; SDVKit sets the 1280x720 window options itself at every start.

An unconfirmed restore of the isolated game option after a verified exit is a **warning**. These block: unknown process identity, unconfirmed exit, unsafe paths, or missing fixture or staging cleanup. Let stop and reset choose the paths. Never delete ownership records by hand or kill the game by process name. The [lab reference](lab-reference.md#identity-and-exit) explains these results.

### Stale fixture copy

The Load menu should list only the fixture. A lab whose fixture was created by SDVKit 2.1.0 or earlier also keeps a stale `SDVKit2_<uniqueGameId>` save in the isolated profile's `Saves` folder: Stardew's own first serialization of the new fixture, with the same farm and markers. The Load menu shows the two rows in arbitrary order, and loading that copy is refused. `lab test-save` and `project review reset` remove it while the lab is stopped, but only that exact folder name, as a plain directory holding one Stardew save whose `SDVKit/FixtureId` and `SDVKit/WorkspaceOwnerId` markers match the registered fixture. Anything else under that name is left alone and refused with `testSaveStaleCopyRefused`. A fixture created now has Stardew's own `_old` backups (`SDVKit_<id>_old`, `SaveGameInfo_old`) in its baseline instead.

## Troubleshooting

| Symptom | Response |
| --- | --- |
| `testSaveBaselineMissing` | With all roles stopped, run `lab test-save --topology single`. Then retry the start. |
| Missing provider or dependency | Pass its local path. Do not install or guess it. |
| `commandWritten=true`, effect unclear | Inspect the matching game log, current state or screenshot. Console silence is not success. |
| `inputBindingChanged` or disturbed observation | Inspect the binding and current state. Repeat only a check you understand and know is safe. Never weaken ownership or foreground checks. |
| Action `mayHaveRun=true` | Read the current state before any repeat. Do not blindly repeat a save, click or change. |
| Stop or reset blocked | Keep the result and the review logs. Resolve the first evidenced cause without manual destructive cleanup. |

Report build, packaging, SMAPI load, functional behavior, visual behavior and cleanup separately. A local two-role result applies to that tested scenario, not to all multiplayer or mod behavior.

## Reference details

### Mouse state for native controls

In a world-ready review, native mod controls may call the parameterless XNA `Mouse.GetState()` during the same player update. They then receive the confirmed virtual coordinates, wheel counter and review mouse buttons. This includes update callbacks after SMAPI assembles input. The sample is bound to the review, player, input instance, local screen and update thread. It ends when that player update returns or throws. Hardware-overlap checks keep the original physical sample.

Cancellation and clear remove the virtual coordinates and buttons at once but keep the consumed neutral wheel offset. An unpublished sample in that scope does the same, so early cancellation cannot cause a reverse wheel jump. Reads outside that update scope, and other getter overloads, keep their original behavior.

### Gesture timing

Click and drag first complete one cursor-only game update. They then check the revision again before the first button-down. A drag holds its endpoint for one more update, so native controls can read historical coordinates before release. That gives duration + 2 down samples. Preparation and the endpoint hold do not count in `completedSteps`; `startTick` is the first down sample.

Gestures check the full revision at the start and after cursor preparation. After that they require the same root, active page, child menu, viewport and scale, but allow their own component and value changes. A gesture completes one game update after the final release sample. This keeps the last virtual position for native release callbacks. Success keeps that position. Cancellation clears it after the release opportunity.

If a game callback is skipped, a fallback through the common input path runs and reports failure or an unconfirmed release. Validation before acceptance does not touch the cursor. Wheel consumption stays inside the matching input sample and keeps its cumulative origin even on failure, so cancellation does not replay or reverse a notch.

### Input adapter

Wheel input needs a sampled raw counter. Counter overflow is rejected before the virtual offset changes. Physical wheel input may push the counter past its range after queueing. Then the adapter cancels all pending review input, clears the virtual cursor and logs an error. Observe the effect instead of retrying an acknowledged command. Release is reported as unconfirmed while physical or external input stays down; physical input is not suppressed to force a release. If a consumed origin cannot be represented, the last output stays and the action reports failure instead of a synthetic reverse notch.

The adapter is bound to one input instance. Its SMAPI 4.5.2 integration reapplies the public `IInputHelper.Press` immediately before `SInputState.TrueUpdate`. It reads `CustomPressedKeys` and the public `ButtonStates` property to verify queue consumption and a completed state derivation. This includes updates whose exceptions SMAPI catches internally. On a failed update, it removes only the queue entries that its own calls added and that were absent before that prefix. It does not change suppression state or dispatch private events.

Installation checks the required method, field and property types. If any is unavailable, it rolls back all input patches. Cancellation stops reapplication and drains one sample without injection, through the existing transport and action lock. No input cleanup calls `Suppress`.

### Configuration reconcile checks

Reconcile checks the active single review's launch, process, profile and complete artifact set. It rejects malformed JSON, linked files or folders, changed assemblies, content or manifests, and drift in another artifact.

### Diagnostics limits

The reader scans at most the last 4 MiB of the log. It looks for the launch's GameSide activation ID in the first 256 KiB. It accepts the log only with a fresh status, matching process and staging identity, and that activation ID. Missing, stale, linked, replaced, unbound or rotated logs return `state=unavailable`. Each entry is limited to 32 lines of 1,024 characters.

Relative source locations are kept. Unrelated continuation text and lines that name other staged mods are omitted. `withheldLines` counts lines that were fully or partly withheld; a placeholder stands in where no useful text remains. There is no raw-log or path input, no automatic upload, and no Content Patcher command execution.

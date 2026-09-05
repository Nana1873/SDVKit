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

Continue only with one ready installation and a supported target. Identify the owner of any active lab/review; wait for another task's verified teardown instead of stopping its game. Only one task should operate a lab context at a time.

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

## Exercise behavior and collect evidence

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

For network commands, add exactly one `--role host` or `--role farmhand`; do not infer one role's state from the other. Use distinct screenshot labels.

Choose the detailed surface for the task:

- [Inspection](inspection.md): Data, maps, textures, audio, observed mod assets.
- [Fixture reference](lab-reference.md#fixture-command-reference): owned buildings, objects, animals, and natural navigation.
- [MCP](mcp.md): typed observation, screenshots, and separately enabled input/fixture actions.

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

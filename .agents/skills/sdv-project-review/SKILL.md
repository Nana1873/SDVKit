---
name: sdv-project-review
description: Run and interpret SDVKit's existing interactive review for one explicit SMAPI C# mod or root content-pack target, including functional, visual, restart, and cleanup evidence. Use when asked to dogfood or interactively review a target with `sdvkit project review`; do not use for the bounded automated `project smoke`, deployment, or release.
---

# SDV project review

Use the existing `sdvkit project review` commands to conduct an interactive, functional, and visual review. Do not add a wrapper, modify the reviewed sources, or present this workflow as an automated tick smoke; use `sdv-project-smoke` for `sdvkit project smoke`.

## Establish the review contract

1. Read the repository-root `AGENTS.md` and applicable project instructions. Keep the working directory at the intended lab root whose ignored `.sdvkit/` owns the review.
2. Before `start`, identify the target as exactly one standalone SMAPI C# project or one ready root SMAPI content pack. State the expected feature behavior and the evidence that will accept or reject it.
3. Resolve the target and every selected provider, companion, and additional content pack to an exact local path. Pass only those paths through the target argument, `--companion`, or `--content-pack` as appropriate. Never search for mods or providers, download them, or install anything into a normal or mod-manager-owned `Mods` directory.
4. Keep normal saves outside the workflow. Review saves, logs, screenshots, staging, and ownership belong below the lab root's `.sdvkit/`. This is process isolation, not a sandbox.

Use `single` by default. Use `network-2` only when the user explicitly requests multiplayer evidence:

- A C# target supports `single` and `network-2`.
- A content-pack target supports only `single`; pass its `ContentPackFor.UniqueID` provider as an explicit local `--companion` and honor its minimum version.
- `--content-pack` remains for additional packs, not for replacing the target argument.

For read-only agent access to a currently active review, run the native STDIO entry point from the same lab-owning current directory. Use `sdvkit project review mcp serve [--topology single]` without a role for `single`. For `network-2`, start `sdvkit project review mcp serve --topology network-2 --role <host|farmhand>` and bind a separate server/client process for each role that must be inspected. The server must revalidate both exact role states, processes, target/build bindings, owned fixture, and reciprocal joined-pair proof before it returns only the selected role's runtime; never infer one role from its peer. It exposes only its documented typed tools. Do not add `--json`, network transport, or a relay, and continue to use the existing review commands for lifecycle operations.

## Start and confirm the load

For `single`, omit `--topology` or name it explicitly:

```text
sdvkit project review start "<absolute-target-path>" --topology single --companion "<absolute-companion-path>" --content-pack "<absolute-additional-pack-path>" --json
sdvkit project review status --topology single --json
```

A review start prepares only the role's isolated startup preferences for a visible 1280x720 game window and keeps SMAPI's separate interactive terminal available. Windows is asked not to activate the new process, but a transient terminal activation can still be terminal-host behavior. Treat a minimized game or a borderless/fullscreen game as a failed review start; do not substitute minimization for a renderable viewport.

When the review needs the registered SDVKit-owned disposable world, first run `sdvkit lab test-save --topology single --json` while all roles are stopped, then add the explicit flag:

```text
sdvkit project review start "<absolute-target-path>" --topology single --test-save --companion "<absolute-companion-path>" --content-pack "<absolute-additional-pack-path>" --json
```

Omit unused repeatable options. Without `--test-save`, the existing plain single-review behavior is unchanged. With it, require `testSave.state=ready`, `testSave.phase=passed`, the exact fixture and Save IDs, and `testSave.identityVerified=true` before sending feature commands.

For an explicitly requested C# multiplayer review:

```text
sdvkit project review start "<absolute-csharp-target-path>" --topology network-2 --companion "<absolute-companion-path>" --content-pack "<absolute-additional-pack-path>" --json
sdvkit project review status --topology network-2 --json
```

Omit unused repeatable options. Before sending feature commands, require a running exact process for every selected role and AlwaysOn confirmation of the target's exact `UniqueID` and canonical version. Check the reported artifact roles, kinds, identities, provider, build identity, problems, and warnings. Do not treat build or staging success as SMAPI-load proof.

## Send commands and prove their effects

Send a line only while the separate SMAPI console is idle, with no partially typed or concurrent manual input:

```text
sdvkit project review command "<existing-SMAPI-or-companion-command>" --topology single --json
```

For `network-2`, address exactly one role on every command:

```text
sdvkit project review command "<existing-host-command>" --topology network-2 --role host --json
sdvkit project review command "<existing-farmhand-command>" --topology network-2 --role farmhand --json
```

Do not pass `--role` for `single`. Companion or target commands are examples of that selected mod's capability, not SDVKit product commands.

`commandWritten=true` proves only delivery of one console line. The message `Sent debug command ... but there was no output` is neutral: it proves neither success nor failure. Confirm each intended effect through the most direct available evidence, such as a matching isolated log entry, a state change, verified save/reload behavior, or a visual result. Do not infer completion from silence.

### Inspect canonical Data definitions

Use the top-level read-only data surface only during an exact, target-load-confirmed `single` review:

```text
sdvkit project review data assets --offset 0 --limit 100 --topology single --json
sdvkit project review data keys "Data/Buildings" --offset 0 --limit 50 --topology single --json
sdvkit project review data get "Data/Buildings" "Barn" --topology single --json
```

This is not a line to pass through `project review command`. `assets` independently inventories the canonical structured `Data/*` definitions installed for the running game version, loads them through SMAPI's live content pipeline, and reports the loaded type, shape, key kind, and complete coverage counts. Require `coverage.complete=true` and zero unknown, unclassified, or unsupported assets before making a completeness claim. Page through both asset and key results using their reported `nextOffset`; never assume the first page is the full inventory.

Use dictionary keys, zero-based list indexes, or the explicit singleton key exactly as returned by `keys`. A `get` result is acceptable only when it identifies the running game version, canonical asset, canonical key, data type, shape, and one record value. Case and a small separator set normalize for convenient lookup, but collisions, missing version-specific assets, unsafe serialization, stale response files, and mismatched process or request state must remain fail-closed. Do not turn this bounded surface into a bulk dump, reflection explorer, mutation path, or network-role command.

### Drive review input without desktop automation

Use AlwaysOn's bounded input surface when a review needs real SMAPI input events without foreground interaction:

```text
sdvkit input press <SButton>
sdvkit input cursor <ui-x> <ui-y>
sdvkit input cursor clear
```

Transport one line at a time through `project review command`, with the required role for `network-2`. `press` accepts one exact SMAPI `SButton` name, such as `F8`, `Enter`, `MouseLeft`, `ControllerA`, or `DPadDown`, releases it on the next input tick, and permits both SMAPI's input update and Stardew's own background menu-input path only for the bounded dispatch interval. `MouseWheelUp` and `MouseWheelDown` are additional exact review tokens for one directional wheel notch; set the virtual cursor over an active menu before using either token. `cursor` enables a process-local virtual cursor only at a coordinate inside the current `Game1.uiViewport`; neither action may focus a window or move the user's physical pointer. Clear the override explicitly when the mouse path is complete. A successful AlwaysOn result proves the input was injected or the virtual coordinate was set; prove the intended target-mod effect separately through state, logs, or a viewport screenshot.

Every automated review mouse path, including future MCP action tools, must first verify the exact review ownership and topology role, use only this existing process-local SDVKit virtual cursor, and fail closed. Never use global `SendInput`, physical cursor movement, window-focus changes, or generic computer-use automation for review mouse input.

### Prepare generic state with the owned fixture surface

Use the AlwaysOn fixture surface only when the currently running role has freshly verified the exact SDVKit-owned review fixture and Save identity. For `single`, this requires the explicit `--test-save` selection and the accepted test-save status above. For `network-2`, require the exact joined-pair and owned-fixture proof from both roles first. Never use these commands against a plain single review, smoke run, personal save, or an unverified world.

These are game-console lines, not top-level CLI commands:

```text
sdvkit fixture status
sdvkit fixture building ensure <alias> <building-kind> <x> <y>
sdvkit fixture object ensure <alias-or-id> <qualified-item-id>
sdvkit fixture object clear-owned <alias-or-id>
sdvkit fixture animal ensure <alias-or-id> <animal-kind>
sdvkit fixture enter <alias-or-id>
sdvkit fixture enter greenhouse
sdvkit fixture farm
```

Transport one quoted line at a time through `project review command`. For example:

```text
sdvkit project review command "sdvkit fixture status" --topology single --json
sdvkit project review command "sdvkit fixture building ensure coop_a coop 16 20" --topology network-2 --role host --json
sdvkit project review command "sdvkit fixture animal ensure coop_a white-chicken" --topology network-2 --role host --json
sdvkit project review command "sdvkit fixture enter coop_a" --topology network-2 --role farmhand --json
```

World mutations (`building ensure`, `object ensure`, `object clear-owned`, and `animal ensure`) are valid only for the singleplayer main role or the `network-2` host. `status`, `enter`, and `farm` are role-local and may also target the verified farmhand; every `network-2` command still needs exactly one role. `farm` may follow only a natural Farm exit from the current review FarmHouse, exact Greenhouse, or owned fixture interior. For `enter`, the exact `greenhouse` token selects the one loaded Greenhouse and its natural entry; owned mutations still resolve their normal alias or GUID, and an existing owned `greenhouse` alias remains addressable for entry by GUID. These are bounded fixture transitions, not general warps.

Building and animal kinds resolve only from stable internal IDs in the canonical data loaded by the running Stardew version, never from localized display names. Kind tokens are case-insensitive and separator-normalized; legacy `deluxe-barn` and `white-cow` remain valid, and `coop` plus `white-chicken` use the same path. The exact available set can vary with the loaded game data. Unknown or colliding tokens, unplaceable building data, and incompatible animal-house pairs must fail before mutation. Ensure operations must be idempotent for the same SDVKit-owned alias and exact resolved kind, position, home, assignment, and fixture. Never convert or move an existing animal. `object clear-owned` may remove only the exact SDVKit-owned fixture object, never an entire object collection or an unowned object.

Treat the fixture result as generic world preparation and evidence only. Do not infer or report a target-mod selection from it, special-case StardewInteriorChanger, or inspect or expose foreign `modData`. The fixture surface has no save or sleep command; use an explicitly selected existing SMAPI, target, or companion command when a review requires either action, then prove that action independently. As with every transported line, `commandWritten=true` is not execution evidence: require the matching AlwaysOn result and direct state, log, persistence, or visual confirmation.

Request screenshots only through the existing AlwaysOn commands, with a unique label. Use the map form for world layout and the viewport form for menus or HUD:

```text
sdvkit project review command "sdvkit screenshot <unique-label>" --topology single --json
sdvkit project review command "sdvkit screenshot viewport <unique-label>" --topology single --json
```

For `network-2`, add `--role host` or `--role farmhand` and use distinct labels. A screenshot succeeds only when all three gates pass:

1. the matching AlwaysOn log confirms creation and gives the full path;
2. that concrete PNG exists below the isolated profile;
3. the image is actually opened and visually checked against the stated expectation.

File existence, a hash, or `commandWritten=true` does not replace visual inspection.

## Exercise a real restart and clean up

For `single`, save through an existing game, SMAPI, target, or companion command and verify save completion. Then:

1. `project review stop --topology single --json`; confirm the exact process stopped and owned review staging was removed.
2. Run `start` again with the exact same target and explicit selection, including `--test-save` when selected initially. The isolated profile persists; an owned test-save review additionally remounts the same retained Work-Copy even though staging is prepared again.
3. Reload the same isolated save through an existing command, then reconfirm state, target behavior, and required visual evidence.
4. Perform any planned restore and final save, then run and verify a final `stop`.

For a plain single review, no fixture reset is needed. After the final stop of a `--test-save` review, run `project review reset --topology single --json` and require `fixtureReset=true` plus `stagingRemoved=true`. Reset must remain blocked while any single, host, or farmhand process is retained, while a network-2 review is retained, or when exact fixture/staging ownership cannot be proven. A fixture-backed review is incomplete until this final reset succeeds.

For `network-2`, use role-specific commands for host and farmhand. A clean `stop` preserves the owned work fixture and exact staging for restart. The required lifecycle is:

```text
stop --topology network-2
start <the exact same explicit selection> --topology network-2
stop --topology network-2
reset --topology network-2
```

Between the second `start` and `stop`, reload or resume the retained work state and reconfirm both roles independently. Call `reset` only after both roles are confirmed stopped; require the fixture reset and verified host/farmhand staging removal. A fixture-backed network review is incomplete until this final reset succeeds.

If process identity is uncertain, staged files drift, ownership cannot be proven, or stop/reset/cleanup is unconfirmed, remain fail-closed. Do not kill by process name, delete markers or staging manually, retry destructive cleanup speculatively, or claim teardown success.

## Report the evidence

Report these separately, without inventing a new schema:

- **Build:** what C# build actually completed, or why it was not applicable.
- **Packaging:** what package or ready-directory validation actually completed, or why it was not applicable.
- **SMAPI load:** exact expected and loaded target ID/version, build identity, and per-role confirmation.
- **Functional behavior:** commands issued and the logs, state transitions, or save/reload results that prove their effects.
- **Visual behavior:** each accepted PNG, its AlwaysOn confirmation, and what the visual inspection showed.
- **Cleanup:** exact-process stop, ownership/staging result, network reset when applicable, and any remaining uncertainty.

Claim only the proof level actually observed. Record a newly discovered product limitation separately; do not expand a skill-following review into CLI, runtime, AlwaysOn, or lab implementation work.

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

## Start and confirm the load

For `single`, omit `--topology` or name it explicitly:

```text
sdvkit project review start "<absolute-target-path>" --topology single --companion "<absolute-companion-path>" --content-pack "<absolute-additional-pack-path>" --json
sdvkit project review status --topology single --json
```

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

Request screenshots only through the existing AlwaysOn command, with a unique label:

```text
sdvkit project review command "sdvkit screenshot <unique-label>" --topology single --json
```

For `network-2`, add `--role host` or `--role farmhand` and use distinct labels. A screenshot succeeds only when all three gates pass:

1. the matching AlwaysOn log confirms creation and gives the full path;
2. that concrete PNG exists below the isolated profile;
3. the image is actually opened and visually checked against the stated expectation.

File existence, a hash, or `commandWritten=true` does not replace visual inspection.

## Exercise a real restart and clean up

For `single`, save through an existing game, SMAPI, target, or companion command and verify save completion. Then:

1. `project review stop --topology single --json`; confirm the exact process stopped and owned review staging was removed.
2. Run `start` again with the exact same target and explicit selection. The isolated profile and its saves persist even though the staging is prepared again.
3. Reload the same isolated save through an existing command, then reconfirm state, target behavior, and required visual evidence.
4. Perform any planned restore and final save, then run and verify a final `stop`.

`single` has no `reset` command.

For `network-2`, use role-specific commands for host and farmhand. A clean `stop` preserves the owned work fixture and exact staging for restart. The required lifecycle is:

```text
stop --topology network-2
start <the exact same explicit selection> --topology network-2
stop --topology network-2
reset --topology network-2
```

Between the second `start` and `stop`, reload or resume the retained work state and reconfirm both roles independently. Call `reset` only after both roles are confirmed stopped; require the fixture reset and verified host/farmhand staging removal.

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

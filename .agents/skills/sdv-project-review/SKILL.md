---
name: sdv-project-review
description: Run and interpret an interactive SDVKit review of an explicit SMAPI C# mod or root content-pack target, with functional, visual, and task-relevant persistence evidence. Use for project review; not automated project smoke, deployment, or release.
---

# SDV project review

Use the existing CLI and [live-review guide](../../../docs/live-review.md). Do not modify the reviewed mod or create another runtime, wrapper, or evidence mechanism while following this skill.

## Select the test

Read repository/project instructions. Establish the intended behavior, acceptance evidence, lab-owning current directory, and explicit local target/companions. Identify any current lab owner before live work; never stop another task's game or modify its worktree.

Use single by default; network-2 requires multiplayer scope in the user's request. Content-pack targets are single-only and need an explicit local provider companion. Never search normal Mods for dependencies or download them. Use the [capability matrix](../../../docs/README.md#capability-matrix) to select the supported path.

Keep normal saves and Mods outside the workflow. Source projects can be selected explicitly; generated review state belongs below the lab's `.sdvkit/`. Isolation is not an OS sandbox.

Select only the evidence needed for the request. A real save/stop/restart is mandatory for persistence, reload, join/resume, or lifecycle claims; it is not mandatory for every query or visual inspection. Final stop and applicable fixture reset remain required for sessions this task owns.

## Start and verify

Use the guide's preparation/start commands from one lab directory. Omit unused companion options. For disposable-world work, check the registered baseline before start; if missing, prepare it through `lab test-save --topology single` while all roles are stopped.

Require the exact process/role, target ID, canonical version, build identity, and game-side load confirmation. A fixture-backed review additionally needs `testSave.state=ready`, `phase=passed`, matching Save/fixture IDs, and `identityVerified=true`. For network-2 require both exact roles and reciprocal joined-pair proof before world-dependent work.

Interactive reviews should be renderable/windowed, with SMAPI's terminal available. The initial 1280x720 baseline must not prevent later resizing/UI-scale tests. Never focus or minimize the game to manufacture background evidence.

## Use only the relevant reference

- [Inspection](../../../docs/inspection.md): bounded canonical Data, maps, textures, audio, and observed mod assets. Check canonical identities, coverage, pagination, and errors; do not infer unobserved assets or provenance.
- [MCP](../../../docs/mcp.md): bind a client to the already-running review and one immutable role. Default observation/evidence exposes eight tools for single, five per network role; opt-ins add their documented tools. Counts describe default profiles, not a universal allowlist. Enable only the authorized families needed for the task.
- [Input and screenshots](../../../docs/live-review.md#exercise-behavior-and-collect-evidence): use only the process-local cursor/button/wheel paths, never desktop automation, physical pointer movement, or window focus changes.
- [Fixtures](../../../docs/lab-reference.md#fixture-command-reference): use only the exact owned disposable world and permitted role. MCP input and fixture actions are separate opt-ins; neither authorizes arbitrary console commands or normal-save access.

Read only the reference relevant to the selected operation. Do not reproduce every capability test for an unrelated review.

## Accept effects, not delivery

Dispatch console lines only with an idle console and no concurrent manual typing. `commandWritten=true` proves delivery; console silence proves neither success nor failure. Verify effects through matching owned logs, fresh state, or the exact screenshot.

Use fresh non-overwriting screenshot labels. Accept only a confirmed PNG under the selected isolated profile; inspect the image to claim visual behavior. For MCP, retain its matching launch/role metadata and returned image/hash. Never use a peer role's image as proof for the selected role.

An acknowledged input operation still needs an effect check. Unknown action completion (`mayHaveRun`) or a timeout must not trigger a blind repeat. Inspect current state, preserve the first failure, and retry only the understood affected check when safe. An externally disturbed observation is inconclusive, not automatically a product defect.

## Finish and report

Follow [stop/restart/reset semantics](../../../docs/live-review.md#finish-or-test-persistence). Preserve the work save between the two halves of a persistence test. Confirm final stop and applicable reset before claiming completion; network stop deliberately retains staging until reset.

Unknown process identity, unsafe owned paths/reparse points, and unconfirmed exit or cleanup remain blocking. Do not kill by process name or delete markers/staging manually. Ordinary mod-created file drift does not authorize broader selection and need not block marker-selected cleanup after confirmed exit. An unconfirmed isolated-option restore is the emitted warning when exact exit and cleanup otherwise succeeded.

Report build, packaging, SMAPI load, functional behavior, visual behavior, and cleanup separately. Use result-owned evidence and distinguish staged file identity from an in-memory DLL hash. Record limitations without expanding the review into implementation work.

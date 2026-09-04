---
name: sdv-project-smoke
description: Run and interpret SDVKit's existing end-to-end project smoke for one standalone SMAPI C# mod in the isolated live lab. Use when asked to smoke-test a mod with `sdvkit project smoke`; do not use for content packs, deployment, or full functional verification.
---

# SDV project smoke

Use the existing SDVKit CLI to prove that one supported mod builds, is loaded by SMAPI, completes the bounded live smoke, and leaves the selected isolated topology clean. Do not add a wrapper or change the mod while following this workflow.

See the [live guide](../../../docs/live-review.md#automated-smoke) for user-facing examples and the [capability matrix](../../../docs/README.md#capability-matrix) for supported targets.

## Preflight

1. Read the repository-root `AGENTS.md` and every applicable project-specific instruction file. Treat those instructions as authoritative.
2. Identify the owner of any active lab and wait for another task's verified teardown before live work. Keep the working directory at the intended lab root whose ignored `.sdvkit/` directory owns the live-lab state. Use the user-selected mod project path exactly; do not substitute a plausible path.
3. Run discovery and retain both its JSON and process exit code:

   ```text
   sdvkit doctor --json
   ```

   Continue only when the exit code is `0`, `status` is `ready`, and exactly one installation is reported. On `ambiguous`, `notFound`, malformed output, or a nonzero exit, stop and report a **Discovery** failure. Do not choose an installation path yourself or install a missing dependency.
4. Inspect the selected project and retain its JSON and process exit code:

   ```text
   sdvkit project inspect [path] --json
   ```

   Continue only when the exit code is `0`, `problems` is empty, `kind` is `smapiMod`, and the result describes exactly one code-mod manifest with `entryDll` plus exactly one C# project file. Reject content packs, hybrid or unknown trees, multiple manifests or projects, and the reserved `SDVKit.AlwaysOn` identity. Report the first rejected condition as an **Inspection** failure; do not reshape the project or bypass the current smoke command's remaining manifest and dependency checks.

## Select and run one topology

Use `single` by default, including when the user does not mention multiplayer. Use `network-2` only when the user explicitly requests a multiplayer test. Do not invent another topology.

After the preflight, execute exactly one of these existing commands:

```text
sdvkit project smoke [path] --topology single --json
```

or, only for that explicit multiplayer request:

```text
sdvkit project smoke [path] --topology network-2 --json
```

Do not split the workflow into separate build, package, staging, launch, or lab commands. Capture the smoke process exit code and parse its JSON before opening any log. Do not automatically repeat a failed smoke before its first evidenced cause is understood.

## Evaluate the result

Start with the process exit code and top-level `state`. Then evaluate every field that is present:

- `problems[].code`, `path`, and `message`, plus `loadErrors` and `warnings`;
- `artifact.uniqueId`, `version`, `declaredVersion`, `packageHash`, `buildIdentity`, and the reported build/package logs;
- each role's `role`, `state`, `stagedBuildIdentity`, `loadConfirmed`, `loadedUniqueId`, `loadedVersion`, `requiredTicks`, `observedTicks`, and `logPaths`;
- `fixtureReset` and `stagingRemoved`.

A passed result requires exit code `0`, top-level `state: passed`, an artifact, and no reported problem or target load error. `single` must have one passing role; `network-2` must have passing host and farmhand roles. Every role must confirm the expected ID and canonical version, match the artifact build identity, and meet its required tick count. Both `fixtureReset` and `stagingRemoved` must be true.

The JSON has no dedicated failure-stage field. On failure, name the earliest stage that the exit code, JSON, or an allowed referenced log explicitly supports, and cite that evidence:

- **Discovery** — installation discovery did not produce one ready installation.
- **Inspection** — the project shape, manifest, identity, or required runtime dependency is unsupported or invalid.
- **Build** — the controlled Release build failed.
- **Package** — creation or validation of the release package failed.
- **Staging** — isolated staging, collision, or ownership validation failed before launch.
- **Launch** — an exact game process could not be started or established.
- **Mod-Load** — SMAPI did not confirm the expected ID/version, or target `loadErrors` were reported.
- **Multiplayer-Join** — host/farmhand joining or joined-pair identity failed for `network-2`.
- **Tick-Smoke** — loading was confirmed but a role did not reach `requiredTicks`.
- **Stop** — clean exit or exact-process stop could not be confirmed.
- **Cleanup** — owned fixture/staging cleanup was not confirmed, including `stagingRemoved: false`.
- **Reset** — the disposable fixture was not reset, including `fixtureReset: false` when no earlier cause explains it.

An unconfirmed isolated-profile option restore is a warning when exact exit and cleanup succeeded; report it without turning a passing smoke into a Stop failure.

Do not promote a later cleanup symptom over an earlier explicit cause. If the available evidence cannot distinguish adjacent stages, state that limit instead of guessing.

## Read only result-owned logs

Open a log only when the smoke JSON names that exact path and it resolves below `.sdvkit/`. `artifact.buildLog` and `artifact.packageLog` are source-root relative; role `logPaths` are lab-root relative. A `problems[].path` may be read only when it clearly names a `.sdvkit/` log. Do not enumerate log directories, inspect standard SMAPI logs, or open a nearby file that merely looks relevant. If the result names no eligible log, diagnose from the JSON alone.

Never enumerate, open, copy, or modify personal saves. Never use the normal or mod-manager-owned `Mods` directory, install dependencies, acquire missing mods, use commands from older SDVKit versions, or create another recovery/evidence mechanism.

## Report the proof level

Report these separately:

- **Build:** whether the controlled Release build/package was staged.
- **Automated checks:** the bounded live tick smoke that actually ran; do not imply that a repository test suite ran.
- **Game-side load:** expected and loaded mod ID/version, build identity, and per-role result.
- **Functional behavior:** verified only when a separate, explicit feature assertion was actually observed; otherwise say it was not verified.
- **Teardown:** stop, cleanup, and reset results.

A successful project smoke proves the controlled build was staged, the expected mod ID and version were loaded game-side, the existing bounded tick smoke passed, and the selected topology stopped and reset cleanly. It does not prove that every feature of the mod is functionally correct.

Treat `buildIdentity` as the hash of the controlled staged package file set echoed through the game-side marker, not as a DLL hash measured in memory.

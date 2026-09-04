# Release procedure

Publish one completely accepted CI artifact. Keep build success, portable-package verification, and actual in-game behavior separate. This procedure reuses the existing CLI, MCP tools, CI, and verification script; it does not introduce another runtime or test framework.

## Select the checks

Before starting, record the release's previous tag, candidate commit, changed capabilities, selected target/companions, game/SMAPI versions, lab directory/owner, and required checks in one release issue. Use this matrix to choose the live work; expand it when shared code affects other capabilities.

| Change | Required proof beyond the existing CI |
| --- | --- |
| Documentation/help | Links, examples and help routing; exercise changed executable examples without starting a game unless their behavior changed. |
| Toolkit or packaging | Fresh extracted ZIP; create/inspect plus affected build/package paths. |
| Game adapter, lifecycle, or MCP | Game-backed AlwaysOn build, live baseline, and affected features/roles. |
| Save, join, resume, or persistence | Actual save completion, exact host/farmhand stop and restart, retained identities/state, role restrictions, final reset. |
| Input | Actual UI effect and pressed/released cleanup while unfocused; virtual-cursor/foreground boundaries and MCP EOF cleanup when affected. |
| Isolation, ownership, or shared cleanup | Both supported topologies, verified exact exits, final staging/mount/reset checks, and unchanged protected-path evidence. |

Existing CI remains complete for every PR/main run: restore, format, build, all tests, packaging, and portable verification. Do not shard or omit the inexpensive suite just to reduce test counts. Full asset catalogues are also cheap; retain them when their adapter is covered.

For every product release, verify the exact extracted package and its game-backed AlwaysOn build. When product/runtime code changed, include a basic live single load/stop/reset plus the affected gates above. A documentation-only change does not itself require publishing a new product version.

Feature acceptance belongs with its feature PR. Release acceptance checks integration and the actual distribution. A new failure is not permission to weaken a mandatory gate.

## Prepare the candidate

1. Read current `main`, the previous tag, selected issues, and active PRs. Work on a focused `codex/` branch; do not change another task's checkout or lab.
2. Move the applicable `CHANGELOG.md` Unreleased entries into a dated version section. Keep Added/Changed/Fixed bullets about observable behavior and include relevant upgrade steps. Update the CLI project, AlwaysOn project/manifest, version-dependent tests, and README's version/download/tag links consistently.
3. Run the [contributor checks](../CONTRIBUTING.md#build-and-check). A local package is useful for checking packaging changes, but publication uses the final green main CI product.
4. Complete the release PR and require green CI for the exact resulting main commit. Select that run's `SDVKit-windows-x64` artifact once; retain its ZIP and sidecar under ignored `.sdvkit/`. Record commit, CI URL, sizes, and SHA-256. Do not rebuild it for publication.

If a defect needs a separate fix PR, update the candidate to the new green main artifact and reassess affected gates. Do not silently combine evidence from different ZIPs. Unrelated movement on main does not change the identity of a frozen candidate; deliberately choosing a new commit does.

## Verify the extracted package

From the checkout, substitute the selected artifact path and version:

```powershell
.\scripts\verify-windows-x64.ps1 -ArchivePath .\.sdvkit\release\SDVKit-0.7.0-win-x64.zip -ExpectedVersion 0.7.0 -ExpectedDoctorStatus ready
```

Use `notFound` only on a host without a ready game/SMAPI installation (as in CI). This script verifies the sidecar, archive paths/binaries, CLI version, doctor, inactive-review MCP startup, and create/inspect. It does not prove live MCP behavior or compile the game-bound adapter.

The verifier reports elapsed offline time and the retained fresh extraction directory. Extraction outside the checkout intentionally proves independence from repository files; this temporary directory is the documented exception to the usual `.sdvkit/` output rule. Do not point it at normal Saves/Mods or reuse an existing directory.

Set `$packageRoot` to that reported extracted package, then build its game-side source without starting Stardew:

```powershell
$sdvkit = Join-Path $packageRoot 'sdvkit.exe'
$doctor = & $sdvkit doctor --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $doctor.status -ne 'ready' -or @($doctor.installations).Count -ne 1) {
    throw 'One ready game and SMAPI installation is required.'
}
$gamePath = $doctor.installations[0].gamePath
dotnet build (Join-Path $packageRoot 'src\SdvKit.AlwaysOn\SdvKit.AlwaysOn.csproj') -c Release "-p:SdvGamePath=$gamePath" --artifacts-path (Join-Path $packageRoot '.sdvkit\alwayson-build')
if ($LASTEXITCODE -ne 0) { throw 'The extracted AlwaysOn build failed.' }
```

Keep the selected ZIP unchanged. Its archive identity and extracted build proof are separate: the latter checks that the distributed source compiles against the selected installed game.

## Run the selected live checks

Use the extracted `$sdvkit` from one explicitly selected lab directory. Identify the lab owner first; wait for another task's verified stop/reset. Resolve target/companions once. Run doctor and review status, and establish the owned disposable baseline before a fixture-backed start if it is absent.

Use the existing [live-review sequence](live-review.md#prepare-the-lab), [inspection calls](inspection.md), and [MCP tools](mcp.md). Reuse one active review for compatible read-only checks and feature assertions. Do not restart between independent Data/map/texture/audio queries.

For a persistence gate, save through the supported interface, confirm completion, stop, restart the same exact selection, and verify retained identities and values. For network-2, check host and farmhand separately. Preserve work state between those halves; reset only at the end. For input, observe actual UI effects, role binding, physical pointer and foreground stability, and relevant EOF cleanup.

Final cleanup is mandatory: exact process exits, owned staging/mailbox/mount state, and applicable fixture reset. Compare protected-path evidence for gates that require it. Report an isolated-option restoration warning separately from a blocked exit or cleanup. Do not publish with unknown ownership, uncertain action completion, or an unresolved protected-path change.

## Keep valid evidence and retry narrowly

Record each gate's result and local evidence paths in the release issue, with start/end time or elapsed time. Logs and transcripts stay below ignored `.sdvkit/` (or the verifier-owned external extraction). An ordinary Markdown table is sufficient:

| Gate | Artifact SHA / environment | Result | Elapsed | Evidence |
| --- | --- | --- | --- | --- |
| Selected check | Candidate identity and relevant target/fixture/runtime | Passed, failed, or inconclusive | Measured time | Existing output/log |

Record coordination, build, live checks, retries, and publication separately. File timestamps are not CPU-time measurements. Do not promise a faster duration until a clean release has been measured.

Reuse a passed gate only when the artifact, relevant environment, target/companions, baseline conditions, and binding assumptions still match. A planned restart intentionally changes launch IDs; verify their new exact bindings. Unresolved protected-path or cleanup evidence invalidates acceptance even if earlier feature checks passed.

An externally disturbed input observation is inconclusive; inspect state and repeat that bounded check in stable conditions. An action timeout or `mayHaveRun=true` may mean the action executed: never repeat blindly. Fix the evidenced cause and repeat the affected gates; broaden only when the fix or uncertainty affects other results.

## Publish and verify delivery

After every required gate is green, tag the exact accepted green main commit and publish only its unchanged CI ZIP and sidecar. Preserve established repository release protection and authorization. No rebuild, tag move, or artifact replacement is part of delivery.

Generate the user-facing body from the changelog:

```powershell
.\scripts\release-notes.ps1 -Version 0.7.0 | Set-Content -LiteralPath .\.sdvkit\release-notes.md -Encoding utf8
```

Use that file for the GitHub release body. The version section should link to the release acceptance and compare view. Keep detailed hashes, logs, and CI provenance in that linked acceptance; do not maintain a second editorial summary in README or a separate notes file. The helper only extracts text; it does not publish anything.

Download the published assets freshly without relying on an authenticated cached copy. Compare both files byte-for-byte with the accepted CI products, then run the same short portable verifier on the downloaded ZIP. With identical bytes and unchanged relevant environment, the accepted live proof remains valid; do not run the entire live suite again merely because the download URL changed.

If bytes differ, required evidence is missing, or the environment changed materially, stop and reassess before declaring completion. Close the release issue only after delivery verification and final cleanup are recorded. Historical published acceptance remains historical evidence; do not rewrite it to imply checks that were never performed.

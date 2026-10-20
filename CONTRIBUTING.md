# Contributing to SDVKit

Read [AGENTS.md](AGENTS.md). Keep changes tied to a concrete mod-development workflow, and reuse the existing CLI/lab paths. GitHub issues track maintenance and new feature requests; do not maintain a second local roadmap. The [initial roadmap](https://github.com/Nana1873/SDVKit/issues/84) becomes the archived delivery record after [verified 1.0 publication](https://github.com/Nana1873/SDVKit/issues/243).

Use English for code, CLI text, documentation, issues, and pull requests. Chat and progress follow the contributor's preferred language.

## Build and check

Windows and the .NET 8 SDK selected by `global.json` are required. Run from the repository root:

```powershell
dotnet restore SDVKit.sln
dotnet format SDVKit.sln --verify-no-changes --no-restore
dotnet build SDVKit.sln -c Release --no-restore
dotnet test SDVKit.sln -c Release --no-build
git diff --check
```

The solution builds the CLI and game-free tests. Tests reference `SdvKit.Shared`, but do **not** compile the game-bound `SdvKit.GameSide` implementation. A game-bound change also needs a GameSide build against the discovered local installation (see [checks before merge](#checks-before-merge)) and the affected real-game checks in the [release matrix](docs/releasing.md#select-the-checks).

Use `.sdvkit/` for generated reports, fixtures, screenshots, logs, and packages. Ordinary .NET `bin/obj` outputs are ignored. The portable verifier deliberately extracts to a fresh external temporary directory to prove checkout independence; that retained directory is its explicit exception and must never be a normal Saves/Mods directory.

## Checks before merge

CI does not compile the game-bound half of `src/SdvKit.GameSide`. Hosted runners cannot reference the proprietary game assemblies, and a self-hosted runner would run pull-request code on a maintainer's machine. For any change there, or to `src/SdvKit.Shared`, compile it against your local installation:

```powershell
.\scripts\build-gameside.ps1
```

The script uses `-GamePath`, `SDV_GAME_PATH`, or the single installation that `sdvkit doctor` finds, and builds only below `.sdvkit/gameside-build`, so a running lab's binaries stay untouched. Put its last line, which names the Stardew Valley and SMAPI versions, into the pull request. CI adds a warning to every pull request that touches `src/SdvKit.GameSide` as a reminder. The game runs .NET 6, so this also catches APIs that only exist in the .NET 8 test run.

A pull request that changes the command transport, lab lifecycle, input or isolation needs both topologies live before merge. Identify the lab owner first (rule 10 in [AGENTS.md](AGENTS.md)).

1. Run `project smoke --topology network-2` for a standalone C# mod.
2. Start a `network-2` review. For each role, send one command and one native MCP read, then have the farmhand leave and rejoin.
3. Start a single `--test-save` review. Join a local split-screen farmhand, send one `screen=<id>` command, run one screen-bound MCP read, then leave.
4. Stop and reset every topology you used, and confirm that no game process remains.

Documentation-only and offline toolkit changes need neither check. Record what ran in the pull request.

## Merging and branches

SDVKit has one maintainer, who merges their own pull requests. Do not wait for a second reviewer. A pull request merges when CI is green for its final commit, the checks above that apply have passed and are recorded in the pull request, and its review findings are fixed or answered. Agents merge only after the maintainer approves that pull request.

GitHub deletes a pull request's branch automatically when it merges. Keep a branch whose pull request was closed unmerged, or that gained commits after its merge, until its content is either merged or deliberately dropped.

Known risks whose cause is still open stay as issues with the `retained-risk` label rather than only as prose, so the backlog reflects reality.

## Source navigation

Command routing starts in `src/SdvKit.Cli/CliApplication.cs`; help is in `CliHelp.cs`, project commands in `ProjectCommand.cs` and `ProjectReviewCommand.cs`, and review queries in the matching `Review*Command.cs` partials.

The larger workflows stay in their existing components, with files named for the responsibility they contain:

| Workflow | Source files |
| --- | --- |
| Fixture review | GameSide's `ReviewFixtureArguments`, `ReviewFixtureTransportArguments`, `ReviewFixtureOperation`, `ReviewFixturePolicy`, and `ReviewFixtureKindResolver`; `ReviewFixtureCommand` and `ReviewFixtureTransportCommand` dispatch to the `StardewReviewFixture*` runtime partials for buildings, objects, animals, and navigation. |
| Map review | GameSide's `ReviewMapOperation`, `ReviewMapResolution`, and `ReviewMapValidation`, with `ReviewMapSource`, `ReviewMapFileInventory`, `StardewReviewMapSource`, and `ReviewMapCommand` at the adapter boundary. |
| Project review | `src/SdvKit.Cli/Projects/ProjectReviewService.cs` routes requests; `ProjectReviewSingle`, `ProjectReviewNetwork`, `ProjectReviewConsole`, and `ProjectReviewReports` contain the matching partial responsibilities. |
| Lab lifecycle | `src/SdvKit.Cli/LiveLab/LiveLabService.cs` owns dependencies and shared state; `LiveLabStart`, `LiveLabStatus`, `LiveLabStop`, `LiveLabTestSave`, `LiveLabProjectReview`, and `LiveLabNetwork` contain the matching partial responsibilities. |

Extend the file for the affected responsibility. Keep shared state in its existing partial type, and put game-free contracts and helpers in `src/SdvKit.Shared`, referenced by the CLI, GameSide, and tests.

## Documentation changes

- Keep the root README focused on installation and one example for each product pillar.
- Put command behavior in the relevant [guide/reference](docs/README.md); agent skills link to it and retain only workflow-specific decisions.
- Update the [capability matrix](docs/README.md#capability-matrix) when a supported target, topology, role, or opt-in changes.
- Add user-visible changes to `CHANGELOG.md` under `Unreleased`. Explain the trigger and resulting behavior; implementation/CI evidence belongs in the PR or release issue.
- Check relative links and anchors, run changed executable examples where practical, and inspect root/subcommand help. Distinguish live examples inspected statically from those actually run.

The existing CI runs restore, formatting, build, the complete tests, packaging, and portable verification. It does not launch Stardew. Offline checks may run independently; live work requires one known owner of the selected lab and verified teardown before handoff.

Two status-file stress tests are reported as skipped in ordinary local runs: `LiveLabStorageTests.EnsureDirectoriesToleratesAtomicStatusReplacementTempFiles` and `StatusWriterTests.ConcurrentReaderSeesCompleteExactSnapshotsWithContractFreshness`. The Windows rename denial they used to expose ([#136](https://github.com/Nana1873/SDVKit/issues/136)) has a reproduced cause and a correction in the publisher and reader; the opt-in stays until the owner lifts it. Their workloads and assertions remain unchanged. CI always runs them and verifies both passed. All other tests, including ordinary status publication, freshness, identity and deliberate-denial checks, remain in the default local suite.

To include both stress tests in a local run, set the process-local opt-in and restore its previous value afterwards:

```powershell
$previousStatusStress = $env:SDVKIT_RUN_STATUS_FILE_STRESS
try {
    $env:SDVKIT_RUN_STATUS_FILE_STRESS = '1'
    dotnet test SDVKit.sln -c Release --no-build
}
finally {
    $env:SDVKIT_RUN_STATUS_FILE_STRESS = $previousStatusStress
}
```

`CI=true` or `GITHUB_ACTIONS=true` also enables them, regardless of the opt-in value. These two tests retain failure context and bounded post-failure snapshots under `.sdvkit/test-failures/`; failing CI uploads that directory. Writer and reader/scanner failures remain visible together. A retained file may be newer than the failed read or rename, so it is not proof of the bytes or competing access at failure time.

The concurrent reader test also retains up to four original first-chance exceptions from its last synchronous read, restricted to that managed thread and the reader's caught exception families. On failure, it describes their HRESULTs and bounded messages/stacks after unsubscribing. An exception need not reach the reader's outer catch; an empty capture does not exclude pending, invalid-length, or mismatch reports. This test-only observation changes scheduling and cannot identify an external actor or explain a nonreproducing historical failure.

For native rename error 5 in those tests, test-only code also records the immediate thread-local native-status observation and old/new snapshots before publisher cleanup. The optional thread-local diagnostic callback is installed only by the test assembly; this is not a syscall trace or a recovery policy.

The native launcher environment/argument test also retains failure observations under `.sdvkit/test-failures/`: the captured process identity and wait result, actual elapsed phases, observed exact-child cleanup outcome, and up to 16 KiB from the beginning of each stdout/stderr file. Missing results and files are explicit. Snapshots are taken after cleanup and before temporary-file disposal; they may differ from output at the failed wait. Diagnostics do not explain a timeout or alter the 15-second wait and 5-second cleanup bounds, and diagnostic/cleanup errors do not replace the original assertion.

## Release

Follow the [release procedure](docs/releasing.md). It defines artifact identity, focused live acceptance, retry rules, and publication verification. Do not substitute build success for actual in-game evidence.

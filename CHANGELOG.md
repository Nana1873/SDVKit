# Changelog

User-visible changes are maintained here. GitHub release notes use the corresponding version section; detailed build, artifact, and live-test evidence belongs in the linked release acceptance. Dates are publication dates in UTC. Historical entries summarize what shipped then, not current limitations or pending-test status.

## [Unreleased]

### Documentation

- A SMAPI authoring recipe connects explicit project selection, an original event/config feature, actual runtime-exception diagnosis, corrected in-game observation, cleanup and ZIP identity, with focused lifecycle references.

- A complete Content Patcher authoring recipe connects creation and local checks to deliberate runtime diagnosis, selected JSON refresh, direct Data observations, a second authored change, cleanup and ZIP packaging, with focused version-aware agent references.

### Changed

- Stale or ended review status no longer retains an inner ready runtime payload; return to title immediately invalidates world/player values.
- Installation and two short quickstarts now lead the README; task guides, the capability matrix, and CLI/MCP references are separately navigable.
- CLI help now shows a short overview, with console grammar and MCP tool details in their own subcommand help.
- Agent reviews require a real restart when testing persistence or lifecycle behavior; unrelated inspections no longer require one. Final stop and applicable reset remain mandatory.
- Release checks now follow a change-based matrix and reuse fully passing evidence for the same artifact and environment. Public download verification does not repeat full live acceptance when the tested package is unchanged.

### Added

- `save inspect` copies one explicitly selected save or registered disposable fixture below the consuming project’s `.sdvkit/` before reading bounded Stardew 1.6 player, calendar and Farm facts, with byte identity, missing-field availability and actionable format/path limits.
- Review status and `stardew_runtime_get` expose the local farmer's stable save-local ID, money, health/stamina and one selected inventory item, with explicit availability and bounded typed values.
- Build, package, and review start accept `--project` to select one root-relative C# project and its colocated manifest. `--game-path` validates and selects one complete game/SMAPI directory for toolkit and live launch commands, preserving unique automatic defaults. Doctor reports incomplete candidates separately with missing requirements and corrective actions.
- `project review cp-refresh` checks explicitly selected root/Include patch JSON, updates only owned staged copies, reloads CP 2.9.1 and diagnoses/observes one selected Data record in the same single review. Launch and current staged identities remain distinct; partial copies and uncertain delivery retain a visible stop/reset/restart recovery requirement.
- `project review cp-diagnose` explains one selected Content Patcher 2.9.1 pack through correlated summary/parse replies, preserving enabled/condition/applied states and explicit incomplete results before separate asset inspection.
- `project check [path] [--json]` checks one mod root's manifest, Content Patcher 2.9.x content, and direct i18n files offline, with relative file/field errors and bundled official SMAPI schemas. It accepts comments and trailing commas without changing source files; runtime patch behavior and referenced assets still need separate checks.
- Selected-mod warnings and multiline exceptions are available through `project review diagnostics` and read-only `stardew_mod_diagnostics`, with exact review/role binding, visible attribution uncertainty, withheld private context, and bounded counts/truncation.
- A short `README.txt` inside newly built portable packages.
- A shared release-note extractor and this versioned changelog, including summaries of all previous releases.
- A reusable portable archive verifier shared by CI and local checks.

### Fixed

- A failed final lab-status update is reported even when earlier active updates were already failing. The game still exits normally, and a missing final marker remains an unconfirmed stop.
- Lab status publication avoids Windows replacement-intermediate failures while preserving complete snapshots for concurrent readers; denied writes remain visible and stale markers are still rejected.
- Content Patcher targets remain reviewable and refreshable from their original source after packaging. Root `.sdvkit` output is retained without entering the game; unsafe paths, unselected edits, companion drift and exact staged identities remain guarded.

- Inspection commands consistently reject unknown CLI options as usage errors before checking review ownership. Data operands beginning with '-' can be escaped after '--', like the other inspection commands.
- Review response publication uses the same file ownership checks for every command and never cleans up a temporary path when its creation failed.
- The installation example now uses an explicit extraction directory and the matching executable path.
- The smoke skill now reports an unconfirmed isolated-option restore as a warning when exact process exit and cleanup succeeded.

## [0.7.0] - 2026-09-04

### Added

- Inspect maps, textures and diagnostic previews, audio metadata, and observed mod-owned assets through the CLI in an active single review. Results reflect the final SMAPI content pipeline; these four adapters are CLI-only.
- Connect native MCP clients to single reviews or a fixed local host/farmhand role, with review/mod diagnostics and map or viewport screenshots. Canonical Data queries are also available through MCP for single reviews.
- Enable process-local input and owned-fixture actions independently through MCP. Actions are absent by default; farmhands can inspect/navigate their fixture but cannot create buildings/animals or save.

### Changed

- Reviews can send input and capture the viewport from the title screen, subject to the command's readiness checks.
- All lab sessions start windowed; later resizing and UI-scale testing remain under your control.
- Disposable-world automation accepts supported Stardew/SMAPI version ranges while retaining runtime API checks.

### Fixed

- Saving and restarting a network review now preserves the active farmhand's identity and fixture binding.
- Mods that create logs, caches, or databases no longer prevent cleanup after the exact owned game process stops. Drift remains visible during status and evidence checks.

Upgrade: stop active reviews and finish their required reset before replacing the package. Use separate MCP clients for host and farmhand; input and fixture actions require their respective startup opt-ins.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/108#issuecomment-5546516554) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.6.1...v0.7.0)

## [0.6.1] - 2026-09-03

### Added

- Read canonical structured Stardew Data through `project review data assets/keys/get` in a single review.
- Connect a native STDIO MCP client to inspect the single review's runtime snapshot.
- Send one mouse-wheel notch at the virtual cursor to an active menu.

### Fixed

- Button injection in background reviews now completes SMAPI's pressed/released/none lifecycle without focusing Stardew or moving the physical pointer.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.6.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.6.0...v0.6.1)

## [0.6.0] - 2026-09-03

### Added

- Review fixtures resolve building and animal kinds from live Stardew data, including coops and chickens; existing barn/cow aliases remain compatible.
- Background review input, a process-local virtual cursor, and non-overwriting viewport screenshots.

### Changed

- Interactive single, host, and farmhand reviews start in visible 1280x720 windows.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.6.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.3...v0.6.0)

## [0.5.3] - 2026-09-02

### Fixed

- Building fixtures prepare dynamic content within the placement area derived from Stardew's BuildingData, instead of depending on a vanilla object-ID list. Content outside that area is untouched; structural blockers still reject placement.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.3) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.2...v0.5.3)

## [0.5.2] - 2026-09-02

### Fixed

- Building fixture preparation recognizes unmodified vanilla Twig and Weeds debris in the requested footprint, fixing the natural-debris rejection found in v0.5.1.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.2) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.1...v0.5.2)

## [0.5.1] - 2026-09-02

### Fixed

- Fixture navigation can enter the loaded Greenhouse from the Farm and return through the natural Farm exit from the FarmHouse, Greenhouse, or an owned fixture interior.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.5.0...v0.5.1)

## [0.5.0] - 2026-09-01

### Added

- Mount the owned disposable save in single review with `--test-save`, retain work across stop/restart, and finish with a verified reset.
- Prepare owned barns, objects, and cows, inspect fixture state, and navigate between the Farm and owned interiors. In network review, mutations are host-only and observations/navigation are role-local.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.5.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.4.1...v0.5.0)

## [0.4.1] - 2026-09-01

### Fixed

- Code mods and companions may create their first regular root `config.json` during smoke/review without failing staged identity checks; other file changes remain checked.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.4.1) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.4.0...v0.4.1)

## [0.4.0] - 2026-09-01

### Added

- Select a ready root content pack as the single-review target, with its provider supplied explicitly as a local companion.
- A repository skill for interactive functional and visual reviews.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.4.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.3.0...v0.4.0)

## [0.3.0] - 2026-09-01

### Added

- Persistent interactive reviews with explicitly selected companions/content packs, role-addressed console commands, and native map screenshots.
- Local host/farmhand reviews with retained restart state and explicit final network reset.

### Fixed

- Resume the saved farmhand and retry a previously blocked reset through the owned lifecycle.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.3.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.2.0...v0.3.0)

## [0.2.0] - 2026-08-31

### Changed

- Single, host, and farmhand use independent persistent profiles below `.sdvkit/lab/profiles`, including per-process Stardew/SMAPI AppData.

Upgrade from v0.1.0: cleanly stop an active old lab first. The new layout does not migrate a retained v0.1.0 fixture junction.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.2.0) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.1.0...v0.2.0)

## [0.1.0] - 2026-08-31

### Added

- Portable Windows x64 toolkit for project inspection, creation, builds, and packaging, requiring the .NET 8 SDK.
- Isolated single/local host-farmhand lab and end-to-end smoke for standalone SMAPI C# mods.

[Release](https://github.com/Nana1873/SDVKit/releases/tag/v0.1.0)

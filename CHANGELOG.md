# Changelog

User-visible changes are maintained here. GitHub release notes use the corresponding version section; detailed build, artifact, and live-test evidence belongs in the linked release acceptance. Dates are publication dates in UTC. Historical entries summarize what shipped then, not current limitations or pending-test status.

## [Unreleased]

### Added

- Inspect bounded map structure and texture metadata through single-review Native MCP; texture previews deliver a checked diagnostic PNG as image content.
- Inspect audio metadata and observed mod-owned asset catalogues and primitive values through single-review Native MCP with the same bounds and selections as the CLI.
- Diagnose a selected Content Patcher pack through Native MCP. Enable selected patch-JSON refresh independently with `--allow-cp-refresh`; its permission stays bound to the original review and retains incomplete-operation recovery evidence.
- Inspect bounded vanilla SeedShop Gold offers, money, inventory and held items through `project review shop` and single-review `stardew_shop_get`; unsupported shop or offer semantics are explicit.
- A runnable shop purchase authoring recipe connects one original SeedShop Gold offer, a deliberate configuration failure, exact packaged artifacts and process-local input to observed money, stock and inventory deltas.
- A focused GMCM 1.16.0 Boolean authoring recipe connects an original mod, owned UI editing, explicit config export/restaging, and the observed effect of the same packaged DLL after restart. Generic GMCM text input remains unsupported.

### Fixed

- Review honors an explicit `--project` when nested C# test or QA mods have their own manifests. Unselected mods are not staged automatically, and multi-mod packages remain rejected.
- Interactive review consoles start minimized, then appear without activation when SMAPI is ready, avoiding the terminal host taking focus during creation.

## [0.8.0] - 2026-09-06

### Added

- Check SMAPI manifests, Content Patcher content and direct i18n files offline with `project check`, using bundled official schemas and actionable file/field errors.
- Select a C# project with `--project` and a complete game installation with `--game-path` for build, package and review. Doctor now explains incomplete installations.
- Diagnose a selected Content Patcher pack and refresh explicitly selected patch JSON in a running single review, then inspect the resulting Data record.
- Review an extracted ready SMAPI mod directly, preserving its supplied files without rebuilding.
- Inspect an explicitly selected save through an isolated copy. Live review status and MCP also expose bounded local-player values and the selected inventory item.
- Read active inventory/shop menu geometry and supported public mod-menu controls through the CLI and `stardew_menu_get`.
- Use opt-in MCP button chords and bounded holds, clicks, scrolling, dragging and text entry. Text supports selected vanilla NamingMenu fields, including mod-created instances of that family; custom/GMCM fields and supplementary Unicode characters remain unsupported.
- Read selected-mod warnings and exceptions through the CLI and MCP, with bounded results and explicit attribution limits.

### Changed

- The README now leads with installation and two quickstarts; focused guides, a capability matrix and shorter CLI help provide the details. Portable packages include a short quickstart.
- Complete CP and SMAPI authoring recipes connect source changes, deliberate error diagnosis, observed in-game effects and packaging. An optional shared-lab workspace layout keeps mod sources separate.
- Release checks follow the changed capabilities and reuse accepted evidence for the same artifact and environment. Shared package verification and changelog-derived release notes keep distribution checks consistent.
- Failed native-launcher and concurrent-status tests retain bounded diagnostic evidence without weakening assertions or wait bounds.

### Fixed

- Virtual mouse input now reaches Stardew/SMAPI's shared input snapshot and normal wheel processing. Cancelled input drains concurrent dispatch safely; uncertain writes are not repeated.
- Ended or stale review status no longer exposes an old ready runtime snapshot. Failed final status publication remains visible, and Windows status replacement avoids the reproduced replacement-intermediate failure. The separate sporadic rename error 5 remains unresolved.
- Content Patcher source remains reviewable and refreshable after packaging, with build output excluded from staging.
- Inspection option handling, response-file ownership checks and failure diagnostics are consistent across adapters. An unconfirmed isolated-option restoration remains a warning when exact exit and cleanup succeeded.

Upgrade: stop active reviews and finish their required reset before upgrading. Extract this version into a new directory and restart MCP clients against it. Input and fixture actions still require their separate startup opt-ins; input acknowledgements do not establish that a menu accepted or persisted a change.

[Release acceptance](https://github.com/Nana1873/SDVKit/issues/154) · [Changes](https://github.com/Nana1873/SDVKit/compare/v0.7.0...v0.8.0)

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

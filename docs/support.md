# SDVKit 2.x support and compatibility

SDVKit 2.0 supports two equal workflows: the [modding toolkit](toolkit.md) and the [isolated live test lab](live-review.md). [`lab init`](toolkit.md#set-up-the-agent-layer) adds an agent layer for Claude Code and Codex on top. Maintenance and feature requests use ordinary GitHub issues. A version bump or a green build alone is not live or release acceptance.

## Supported baseline

| Component | Requirement and verified scope |
| --- | --- |
| Platform | Windows x64; Windows 10 version 1709 or later for the live lab. There is no Linux, macOS, remote fleet or Windows ARM release. |
| SDK | .NET 8 SDK 8.0.419 or a later stable 8.0 SDK, selected by `global.json`. The portable CLI is framework-dependent and uses the .NET 8 and ASP.NET Core 8 runtimes from the SDK. C# mod builds and GameSide builds also need the SDK. GameSide targets the game's .NET 6 runtime and references the selected installation's assemblies. |
| Installation | A complete local Stardew Valley and SMAPI installation. `doctor --json` reports readiness. If several installations are found, select one with `--game-path` for build, start and smoke. Providers and companions must already exist locally, and you select them yourself. |
| Test-save runtime | Stardew game version `>=1.6.15, <1.7`, file version `>=1.6.15.24356, <1.7`, and SMAPI `>=4.5.0, <5.0`, plus runtime API checks. Earlier live acceptance used Stardew 1.6.15.24356 and SMAPI 4.5.2. The ranges do not mean every combination was tested; the release issue records the tested versions. |
| Content Patcher | Offline checks use bundled CP 2.9.x schemas and check file references. Live diagnosis, apply-order parsing and selected JSON refresh need exactly CP 2.9.1. Other provider versions return an unsupported result. |
| GMCM | The checkbox and integer-slider recipes were verified with Generic Mod Config Menu 1.16.0. Public-control inspection is partial. Custom menus and option types are not promised. |
| Topologies | `single`; `network-2` with one local host and one farmhand; one joined local split-screen farmhand inside a single review on the test save. Support for [roles and screens](glossary.md#topology-roles-and-screens) varies by capability. There is no general N-player, remote or matchmaking support. |

Creation, inspection and offline authoring checks need no game. C# builds and packages need the SDK and the selected game and SMAPI assemblies. Content packs need a provider you select for live review. Ready code/pack bundles are single-only. A bundle needs exactly one code mod, with every direct pack member selected. Bundles do not extend the standalone C# smoke contract.

The [capability matrix](README.md#capability-matrix) is the reference for each command's topology and prerequisites. Chest transfers, world actions, progression observations and CP refresh are single-only. Network and local-screen reads do not grant those actions. [MCP startup profiles](mcp.md) list each [opt-in](glossary.md#commands-and-results) action group and its role or screen binding. After an action reports success, observe its effect. Never replay an action whose completion is uncertain.

## First use and upgrade

Follow the [tagged installation and quickstarts](../README.md):

1. Verify the ZIP against its sidecar and extract it to a fresh versioned folder.
2. Run `version --json`, `--help` and `doctor --json`.
3. Create or inspect a project. Run `project check` before a C# build or package, or before packaging a content pack.
4. Start a review from the intended lab root. Diagnose the mod or CP patch and observe its effect.
5. Finish with a verified stop and a test-save reset.

The [recipes](README.md) separate earlier feature proof from current release acceptance. The [acceptance scripts](../scripts/acceptance/README.md) re-run the shop, machine and GMCM workflows against a new package.

Before upgrading from 1.x, 0.x or another 2.x release:

1. With the old executable, stop every lab and review it owns and finish the reset. Do not replace a running package, and do not let another version take over its active ownership records.
2. Keep mod source and the configurations you exported at their paths. Test-save work and staged configuration are test data; SDVKit does not keep them as project state. Export config changes you want to keep through the documented workflow before teardown.
3. Extract the new verified package to its own folder and use its tagged guides. Update the executable path in CLI wrappers and MCP client configuration. Check `version --json`, `doctor --json` and an inactive review status before you start a fresh review. Keep the old package for rollback until the new review has stopped and reset.
4. From 1.x, also follow the 2.0 upgrade steps in the [changelog](../CHANGELOG.md): the game-side mod, its JSON names and several console lines changed. Run `lab init` in your lab root for the agent layer.
5. If an older inactive generated profile or fixture is rejected as incompatible, keep its diagnostics and use a fresh lab folder. SDVKit does not promise to migrate generated internal state. It never copies a personal save you did not select. Very old v0.1.0 active fixture junctions must be cleaned up by v0.1.0 before upgrading.

## 2.x compatibility policy

2.0 broke parts of the 1.x surface on purpose; the [changelog](../CHANGELOG.md) lists each breaking change with its upgrade step. Within 2.x, this policy applies:

| Public surface | Policy within 2.x |
| --- | --- |
| CLI | Documented command names, required operands and options, and their meaning stay compatible. New commands and optional flags may be added. Exit behavior follows each command's documented contract. Do not parse human-readable help or log text. |
| JSON | Documented field meanings, types and state and problem semantics stay the same. New optional fields, capabilities and documented enum or problem values may appear. Consumers must tolerate unknown fields and handle unavailable or unsupported states; do not assume a fixed property count. Within a major version, an incompatible report layout gets a new `schemaVersion` and upgrade documentation; a new major version may change layouts with `schemaVersion` unchanged and lists them in the changelog. |
| Native MCP | Documented tool names, arguments, action opt-ins and binding and error semantics stay compatible. Optional inputs, fields and tools may be added. Do not hard-code a tool count. Clients must negotiate the protocol, respect returned schemas and opt-ins, and reconnect after a role or screen binding ends. Supported transports are local STDIO and Streamable HTTP on `127.0.0.1` with a per-server bearer token. |
| Projects | Manifest-based source and ready selection, and your project, installation, companion and pack choices, keep their documented meaning. SDVKit checks do not replace full SMAPI or CP validation and do not guarantee compatibility with every mod. Packaged user mod data belongs to that mod; use its own migration rules. |
| Lab agent kit | `lab init` keeps its managed files current and never overwrites a file you edited unless you pass `--force`. Skill and guide wording may change in any release; the safety rules they state do not weaken within 2.x. |
| Profiles and generated state | The lab root, the isolation boundaries, the ownership checks and the stop/reset workflow are contracts. Internal `.sdvkit` state files, mailbox formats, staging layout and fixture bytes are not public editing APIs. Stop and reset before upgrading. SDVKit does not promise that a new version can take over an active process or migrate internal state. |

Breaking public changes need a new major release with migration instructions. A maintenance release may refuse a newly found unsafe or unsupported runtime or provider combination, with a diagnostic and a release note. An accepted version range never replaces the adapter's runtime checks or the release evidence.

## Retained limitations and risks

Your normal `Saves` and your normal or mod-manager `Mods` stay outside automatic operations. A real save you select may be imported only as an isolated copy, and SDVKit never changes its source. Process and data isolation is not a Windows security sandbox for mod code or external services. Mod behavior, visual correctness and persistence each need their own in-game check.

Two issues stay open with unknown causes: the sporadic status-file rename error in [#136](https://github.com/Nana1873/SDVKit/issues/136) and the intermittent null reader report in [#143](https://github.com/Nana1873/SDVKit/issues/143). They are not fixed and are not the same issue. Two unchanged status-file stress tests are opt-in locally and required, guarded passes in full CI; [contributor guidance](../CONTRIBUTING.md#build-and-check) records this exception. Freshness, identity and failure reporting stay enforced. An exited process without a confirmed final receipt stays `cleanStopNotConfirmed`. A new failed required gate blocks a release; an earlier accepted risk does not waive it.

The [menu-close guidance](live-review.md#close-a-menu) keeps a native controller limitation ([#273](https://github.com/Nana1873/SDVKit/issues/273)): a synthetic B may close a root menu and open the inventory on disconnect. Prefer the documented Escape or close-button route where it applies. Gamepad handling of other menus is not guaranteed. Incomplete custom UI, tokenized or conditional CP file references, size-limited observations and unavailable data are reported as such, never as success. After a manual kill or crash, SDVKit cannot promise to restore isolated options. Process exit, staging and mount cleanup and the reset are still required, even when an option-restoration warning is reported.

Each release records its CI and artifact identity, runtime and companions, affected live gates, final cleanup and public-byte check in its release issue, for 2.0.0 [#286](https://github.com/Nana1873/SDVKit/issues/286) and for 1.0.0 [#243](https://github.com/Nana1873/SDVKit/issues/243), following the [release procedure](releasing.md). Earlier mod-development and recipe evidence belongs to the artifact and scope it tested. It is not acceptance of a later package.

# SDVKit 1.x support and compatibility

SDVKit 1.0 establishes the delivered [modding toolkit](toolkit.md) and [isolated live test lab](live-review.md) as equal supported workflows. The initial [roadmap](https://github.com/Nana1873/SDVKit/issues/84) concludes only after [verified 1.0 publication](https://github.com/Nana1873/SDVKit/issues/243). Maintenance and new feature requests then use ordinary GitHub issues. A version bump or green build alone does not establish live or release acceptance.

## Supported baseline

| Component | Requirement and verified scope |
| --- | --- |
| Platform | Windows x64; Windows 10 version 1709 or later for the native live lab. No Linux, macOS, remote fleet, or Windows ARM release is provided. |
| SDK | .NET 8 SDK 8.0.419 or a later stable 8.0 SDK, selected by `global.json`. The portable CLI is framework-dependent and uses the .NET 8 runtime supplied by the SDK; C# mod and distributed AlwaysOn builds also require the SDK. AlwaysOn targets the game's .NET 6 runtime and references the selected installation's assemblies. |
| Installation | A complete local Stardew Valley and SMAPI installation. `doctor --json` reports readiness; ambiguous installations require explicit supported `--game-path` selection for build/start/smoke. Providers and companions must already exist locally and be selected explicitly. |
| Disposable-world runtime | Stardew game version `>=1.6.15, <1.7`, file version `>=1.6.15.24356, <1.7`, and SMAPI `>=4.5.0, <5.0`, plus runtime API checks. Historical live acceptance used Stardew 1.6.15.24356 and SMAPI 4.5.2. Accepted ranges do not mean every combination has been tested; the release issue records the actual tested versions. |
| Content Patcher | Offline checks use bundled CP 2.9.x schemas with bounded file-reference checks. Live diagnosis, apply-order parsing and selected JSON refresh require exactly CP 2.9.1; other provider versions return an explicit unsupported result. |
| GMCM | Existing checkbox and integer-slider recipes were verified with Generic Mod Config Menu 1.16.0. Public-control inspection is partial; arbitrary custom menus and option types are not promised. |
| Topologies | `single`; `network-2` with exactly one local host and farmhand; one explicitly joined local split-screen farmhand within an owned disposable single review. Role/screen support varies by capability. No general N-player, remote or matchmaking support. |

Creation, inspection and offline authoring checks do not require a game. C# builds/packages require the SDK and complete selected game/SMAPI assemblies. Content packs need an explicit provider for live review. Ready code/pack bundles are single-only, require exactly one code mod and every direct pack member to be selected, and do not extend the standalone C# smoke contract.

The [capability matrix](README.md#capability-matrix) is the authoritative per-command topology/prerequisite summary. High-level chest transfers, world actions, progression observations and CP refresh remain bounded single-only workflows. Network/local-screen reads do not silently grant those actions. [MCP startup profiles](mcp.md) specify each separately enabled action family and exact role/screen binding. A successful action acknowledgement must be followed by observation of its effect; uncertain completion must never be blindly replayed.

## First use and upgrade

Follow the [tagged installation and quickstarts](../README.md): verify the ZIP and sidecar, extract a fresh versioned directory, run `version --json`, `--help` and `doctor --json`, then create or inspect a selected project. Run `project check` before C# build/package or content-pack packaging. Start a selected review from its intended lab root, diagnose the selected mod or CP patch, observe its effect, and finish with verified stop and disposable-fixture reset. Reuse the [existing recipes](README.md), which distinguish historical feature proof from current release acceptance.

Before upgrading from 0.x or another 1.x release:

1. Use the old executable to cleanly stop every owned lab/review and complete its required reset. Do not replace a running package or let another version adopt its active ownership records.
2. Keep mod source and explicitly retained/exported configurations at their selected paths. Disposable fixture work and staged configuration are test data, not automatically preserved project state. Export intentional config changes through the documented workflow before teardown.
3. Extract the new verified distribution separately, use its tagged guides, and update the executable path in CLI wrappers/MCP client configuration explicitly. Check `version --json`, `doctor --json` and inactive review status before starting a fresh review. Keep the old package available for rollback after the new review has stopped/reset.
4. If an older inactive generated profile/fixture is rejected as incompatible, retain its diagnostics and prepare a fresh lab directory. SDVKit does not promise automatic migration of generated internal state or copy a personal save without explicit selection. Very old v0.1.0 active fixture junctions must be cleaned up by that version before upgrading.

## 1.x compatibility policy

| Public surface | Policy within 1.x |
| --- | --- |
| CLI | Documented command names, required operands/options and their meaning remain compatible. New commands and optional flags may be added. Usage, controlled-outcome and successful exit behavior follow the command's documented contract; consumers must not parse human-readable help/log text. |
| JSON | Existing documented field meaning, types and state/problem semantics are preserved. Additive optional fields, capabilities and explicitly documented enum/problem values may appear; consumers must tolerate unknown fields and handle unavailable/unsupported states rather than assume a fixed property count. An incompatible report layout requires a distinct `schemaVersion` and explicit upgrade documentation. |
| Native MCP | Documented tool names, arguments, action grants and binding/error semantics remain compatible. Optional inputs, fields and tools may be added for supported profiles. Do not hard-code a universal tool count. Clients must negotiate the supported protocol, respect returned schemas and opt-ins, and reconnect after a retired role/screen binding. No transport other than local STDIO is supported. |
| Projects | Existing manifest-based source/ready selection and explicit project, installation, companion and pack choices retain their documented meaning. SDVKit checks do not replace full SMAPI/CP validation or guarantee arbitrary mod compatibility. Packaged user mod data remains owned by that mod; use its migration rules, not a toolkit-wide data migration. |
| Profiles and generated state | The documented lab root, isolation boundaries, exact ownership checks and stop/reset workflow remain contracts. Internal `.sdvkit` state files, mailbox formats, staging layout and fixture bytes are not public editing APIs. Stop/reset before upgrading; no cross-version active-process adoption or automatic internal-state migration is promised. |

Breaking public changes require a new major release with discoverable migration instructions. A newly discovered unsafe or unsupported runtime/provider combination may be refused in a maintenance release with an explicit diagnostic and release note. Version acceptance is never a substitute for the adapter's runtime checks or exact release evidence.

## Retained limitations and risks

Normal Saves and normal/mod-manager-owned Mods remain outside automatic operations. Explicitly selected real saves may be imported only as isolated copies; their sources are never automatically modified. Process/data isolation is not a Windows security sandbox for arbitrary mod code or external services. Mod behavior, visual correctness and persistence need their own actual in-game checks.

The sporadic status-file rename error in [#136](https://github.com/Nana1873/SDVKit/issues/136) and original intermittent null reader report in [#143](https://github.com/Nana1873/SDVKit/issues/143) were closed as not planned with unresolved causes. They were not fixed or equated. Exactly two unchanged status-file stress tests are opt-in locally and mandatory, guarded passes in complete CI; [contributor guidance](../CONTRIBUTING.md#build-and-check) records the exception. Freshness, identity and failure visibility remain enforced, and an exited process without a confirmed terminal receipt remains `cleanStopNotConfirmed`. A new failed mandatory gate blocks release; historical accepted risk does not waive it.

The [menu-close guidance](live-review.md#close-a-menu) and [GMCM slider recipe](gmcm-number-authoring.md) retain the native controller limitation: a synthetic B may close a root menu and open inventory on disconnect. Prefer the documented observed Escape/close-button route where applicable; arbitrary gamepad menus are not guaranteed. Incomplete custom UI, tokenized/conditional CP file references, bounded observations and unavailable data stay explicit rather than inferred as success. Manual termination/crash cannot promise isolated-option restoration. Exact exits, staging/mount cleanup and applicable reset are mandatory even when an option-restoration warning is reported separately.

Every release records exact CI/artifact identity, selected runtime/companions, affected live gates, final cleanup and public-byte verification in its [release acceptance](https://github.com/Nana1873/SDVKit/issues/243), following the [release procedure](releasing.md). Historical mod-development and recipe evidence remains attributed to its tested artifact and scope; it is not blanket acceptance of a later distribution.

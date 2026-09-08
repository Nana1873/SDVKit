# SDVKit documentation

Start with [installation and the two quickstarts](../README.md). These pages describe `main`; [published versions](https://github.com/Nana1873/SDVKit/releases) link to their corresponding tags.

| Task | Guide |
| --- | --- |
| Create, inspect, check, build, or package a mod | [Toolkit](toolkit.md) |
| Author a conditional CP change through live proof and ZIP | [CP authoring recipe](cp-authoring.md) |
| Implement a C# event/config feature and diagnose a runtime error | [SMAPI authoring recipe](smapi-authoring.md) |
| Author a bounded vanilla Gold-shop offer and prove one purchase | [Shop purchase authoring recipe](shop-authoring.md) |
| Author one GMCM checkbox and explicitly restage its saved config | [GMCM authoring recipe](gmcm-authoring.md) |
| Run a smoke, review a mod, or test persistence | [Live review](live-review.md) |
| Refresh selected CP patches during a review | [CP refresh](cp-refresh.md) |
| Accept an intentional staged config save without restarting | [Configuration reconciliation](live-review.md#accept-an-intentional-configuration-change) |
| Explain a selected Content Patcher change | [CP diagnosis](cp-diagnosis.md) |
| Read Data, maps, textures, audio, or observed mod assets | [Inspection reference](inspection.md) |
| Inspect an explicitly selected saved world offline | [Save inspection](save-inspection.md) |
| Inspect active inventory, shop, or public mod-menu controls | [Menu inspection](menu-inspection.md) |
| Observe supported Gold-shop offers, money and inventory | [Shop inspection](shop-inspection.md) |
| Connect an agent to a running review | [Native MCP](mcp.md) |
| Observe world and local-player values | [Runtime state](runtime-state.md) |
| Prepare a natural fishing test and observe its rod | [Fishing preparation](lab-reference.md#fishing-preparation) and [fishing state](runtime-state.md#fishing-observations) |
| Diagnose lifecycle, staging, and cleanup | [Lab reference](lab-reference.md) |
| Build or contribute to SDVKit | [Contributing](../CONTRIBUTING.md) |
| Verify and publish a version | [Release procedure](releasing.md) |
| Find changes since an earlier version | [Changelog](../CHANGELOG.md) |

PowerShell examples use `& $sdvkit`, the absolute executable path set during installation. Keep the current directory at the intended lab root for every live command. Placeholder operands such as `<asset>` must be replaced; examples naming companions require your explicit local mod selection.

## Capability matrix

| Capability | CLI | Native MCP | Topology / prerequisites |
| --- | --- | --- | --- |
| Create, inspect, package | `project` | No | C# mods or content packs; no live review |
| Offline save inspection | `save sections/inspect` | Deliberately deferred | Explicit local file or registered single fixture; isolated copy, Stardew 1.6 |
| Offline authoring check | `project check` | No | One C# mod or CP 2.9.x root; manifest, CP content, direct i18n; no game/network |
| Locale key and placeholder comparison | `project check` | No | Direct top-level i18n files; see [i18n authoring](i18n-authoring.md) |
| Build | `project build` | No | One C# project/manifest and complete game/SMAPI; unique defaults or explicit selectors |
| Automated project smoke | `project smoke` | No | Standalone C# target; single or network-2 |
| Local split-screen | Quoted `sdvkit split-screen join/leave/status` and native `screen=<id>` | Screen 0 only | Explicit opt-in within an owned single `--test-save` review; exactly one local farmhand; [workflow](live-review.md#local-split-screen-review) |
| Review lifecycle | `project review start/status/stop/reset` | No | Selected standalone C# source or extracted ready code mod in either topology; content-pack target single only, with explicit provider |
| Runtime and selected-mod diagnostics | `project review status` | Runtime, review, mods tools | Active single or fixed host/farmhand role |
| Active menu geometry and public controls | `project review menu` | `stardew_menu_get` | World-ready single or fixed host/farmhand; explicit bounded vanilla adapters and partial custom coverage |
| Supported Gold-shop offers and purchase observations | `project review shop` | `stardew_shop_get` | World-ready single; bounded vanilla SeedShop semantics and explicit unsupported offers; [contract](shop-inspection.md) |
| Original shop purchase authoring recipe | Existing check/build/package, ready review, shop/menu/diagnostics and process-local input | Optional matching observation and opt-in input tools | Single disposable world; original SeedShop Stone offer, deliberate config failure and observed money/stock/inventory deltas; [recipe and limits](shop-authoring.md) |
| Original GMCM Boolean authoring recipe | Existing build/package, ready review, screenshot/cursor/press and explicit config export/restage | Optional existing observation and opt-in input equivalents | Single; explicit GMCM 1.16.0, own Enabled checkbox; [observed workflow and limits](gmcm-authoring.md) |
| World/local farmer and selected inventory slot | `project review status` | `stardew_runtime_get` | Same active roles; bounded snapshot, explicit unavailable states |
| Selected Content Patcher diagnosis | `project review cp-diagnose` | `stardew_cp_diagnose` | Active single; explicit pack and CP 2.9.1 provider |
| Selected-mod warnings and exceptions | `project review diagnostics` | `stardew_mod_diagnostics` | Exact active role and staged mod ID; bounded isolated log |
| Refresh selected CP patch JSON | `project review cp-refresh` | `stardew_cp_refresh`, separate `--allow-cp-refresh` | Owned single root CP 2.9.1 target; startup-bound source, explicit files and Data observation |
| Reconcile one staged mod's intentional root config save | `project review config-reconcile --mod` | No; existing tools continue after reconciliation | Exact owned single; config-only drift, JSON validation, unchanged code/content and audit/export; separate from CP refresh; [contract](live-review.md#accept-an-intentional-configuration-change) |
| Canonical structured Data | `project review data` | Data tools | Active single review |
| Maps and textures | `project review map/texture` | Map/texture tools; preview includes PNG image content | Active single review |
| Audio and observed mod assets | `project review audio/mod-assets` | Audio/mod-asset tools | Active single review; CLI-parity bounds, observed-only asset coverage, no audio or asset export |
| Map / viewport screenshots | Quoted review console command | Screenshot tool | Active selected role; map needs loaded world; viewport can diagnose title/loading state through CLI |
| Button, chord, cursor, wheel, mouse gestures, and bounded text | Quoted review console command | Input tools | Active selected role; MCP requires `--allow-input`; gestures require an active menu and fresh UI revision; legacy mouse presses require a virtual cursor, legacy wheel also a menu; text requires an exact available NamingMenu field and fresh UI revision |
| Fixture status / navigation | Quoted review console command | Fixture tools | Owned disposable world; MCP requires `--allow-fixture-actions`; any role |
| Fixture building / animal ensure | Quoted review console command | Fixture tools | Owned disposable world; single or host only; MCP fixture opt-in |
| Fixture object ensure / clear | Quoted review console command | No | Owned disposable world; single or host only |
| Fishing preparation / rod observation | Quoted fixture command / runtime status | Observation via `stardew_runtime_get` | Preparation: owned disposable single/host. Observation: selected local player in the existing supported runtime roles. |
| Fixture save | No standalone fixture-save console command | `stardew_fixture_save` | Owned disposable world; single or host; MCP fixture opt-in |

`network-2` means exactly one local host and one farmhand. It does not establish general multiplayer compatibility. MCP role selection is fixed at server startup. Narrow CLI title/loading exceptions do not bypass MCP's own readiness checks.

## Read a result

- Exit `0`: the requested operation succeeded. Check its specific result fields before making a broader claim.
- Exit `2`: command syntax or arguments are invalid; use the matching `--help`.
- Exit `3`: a controlled discovery, build, runtime, or ownership failure; read `problems`, warnings, and result-named logs.
- `commandWritten=true`: a console line was delivered, not proof of the command's effect.
- Build identity describes the controlled staged file set, not an in-memory DLL measurement.

The [live guide](live-review.md#finish-or-test-persistence) distinguishes successful cleanup, retained state for restart, and warnings.

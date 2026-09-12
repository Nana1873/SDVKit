# SDVKit documentation

Start with [installation and the two quickstarts](../README.md). These pages describe `main`; [published versions](https://github.com/Nana1873/SDVKit/releases) link to their corresponding tags.

| Task | Guide |
| --- | --- |
| Create, inspect, check, build, or package a mod | [Toolkit](toolkit.md) |
| Author a conditional CP change through live proof and ZIP | [CP authoring recipe](cp-authoring.md) |
| Implement a C# event/config feature and diagnose a runtime error | [SMAPI authoring recipe](smapi-authoring.md) |
| Author a multilingual SMAPI dialogue through fallback, controller, and reload proof | [Multilingual UI authoring recipe](multilingual-ui-authoring.md) |
| Author a bounded vanilla Gold-shop offer and prove one purchase | [Shop purchase authoring recipe](shop-authoring.md) |
| Author one GMCM checkbox and explicitly restage its saved config | [GMCM authoring recipe](gmcm-authoring.md) |
| Author one bounded GMCM integer slider and prove its saved restart effect | [GMCM number-slider recipe](gmcm-number-authoring.md) |
| Author a one-time inventory reward and prove native chest persistence | [Inventory and chest authoring recipe](inventory-container-authoring.md) |
| Author a crop-to-machine CP rule and prove native effects through persistence | [Crop and machine authoring recipe](crop-machine-authoring.md) |
| Author a per-player multiplayer mod and prove distinct role and screen state | [Multiplayer authoring recipe](multiplayer-authoring.md) |
| Run a smoke, review a mod, or test persistence | [Live review](live-review.md) |
| Refresh selected CP patches during a review | [CP refresh](cp-refresh.md) |
| Accept an intentional staged config save without restarting | [Configuration reconciliation](live-review.md#accept-an-intentional-configuration-change) |
| Explain a selected Content Patcher change | [CP diagnosis](cp-diagnosis.md) |
| Read Data, maps, textures, audio, or observed mod assets | [Inspection reference](inspection.md) |
| Inspect an explicitly selected saved world offline | [Save inspection](save-inspection.md) |
| Inspect active inventory, shop, or public mod-menu controls | [Menu inspection](menu-inspection.md) |
| Observe supported Gold-shop offers, money and inventory | [Shop inspection](shop-inspection.md) |
| Inspect every bounded backpack slot without opening a menu | [Inventory inspection](inventory-inspection.md) |
| Inspect bounded live crops, soil, and machines | [World inspection](world-inspection.md) |
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
| Local split-screen | Quoted `sdvkit split-screen join/leave/status`, native `screen=<id>`, and `project review menu --screen <id>` | Exact startup `--screen <id>` for runtime/menu/viewport/input | Explicit opt-in within an owned single `--test-save` review; exactly one local farmhand; screen/farmer/context binding invalidates on leave or replacement; shared-window text input unavailable; [workflow](live-review.md#local-split-screen-review) |
| Network farmhand lifecycle | Quoted `sdvkit network leave/join/status` for the selected role | Observation only; host remains readable during coordinated absence, farmhand requires a new client after rejoin | Passed `network-2` pair; host-authorized farmhand departure, retained processes/fixture, explicit rejoin; [workflow](live-review.md#leave-and-rejoin-the-network-farmhand) |
| Review lifecycle | `project review start/status/stop/reset` | No | Selected standalone C# source or extracted ready code mod in either topology; content-pack target single only, with explicit provider |
| Runtime and selected-mod diagnostics | `project review status`; quoted split-screen status for local IDs | Runtime, review, mods tools | Active single, fixed host/farmhand role, or exact owned local screen where supported |
| Active menu geometry and public controls | `project review menu` | `stardew_menu_get` | World-ready single, fixed host/farmhand, or exact owned local screen; bounded inventory/shop/dialogue/question/crafting adapters and partial custom coverage |
| Supported Gold-shop offers and purchase observations | `project review shop` | `stardew_shop_get` | World-ready single; bounded vanilla SeedShop semantics and explicit unsupported offers; [contract](shop-inspection.md) |
| Complete bounded backpack | `project review inventory` | `stardew_inventory_get` | Exact world-ready single player, network role, or local screen; every empty/occupied/unavailable slot, no peer fallback, menu, or mutation; [contract](inventory-inspection.md) |
| Bounded current-location crops, soil, and machines | `project review world` | `stardew_world_area_get` | Exact single player, network role, or local screen; complete in-map rectangle of at most 256 tiles as observed by that selection; [contract](world-inspection.md) |
| Selected regular vanilla chest and both sides | `project review container` | `stardew_container_get` | Exact single player, network role, or local screen; selected open 36-slot wooden/stone chest, local player/chest slots and held item, no mutation; [contract](container-inspection.md) |
| Exact supported chest quantity transfer | `project review container-transfer` | `stardew_container_transfer` | Owned disposable unbound single world, screen 0; 1-99 stackable vanilla objects, fresh container identities; MCP requires `--allow-container-transfer`; [contract](container-transfer.md) |
| One adjacent crop/soil or machine interaction | `project review interact` | `stardew_world_interact`, separate `--allow-world-actions` | Exact owned disposable single-player review; fresh target and inventory revisions; completion is dispatch evidence only; [contract](world-interaction.md) |
| Original shop purchase authoring recipe | Existing check/build/package, ready review, shop/menu/diagnostics and process-local input | Optional matching observation and opt-in input tools | Single disposable world; original SeedShop Stone offer, deliberate config failure and observed money/stock/inventory deltas; [recipe and limits](shop-authoring.md) |
| Original GMCM Boolean authoring recipe | Existing build/package, ready review, screenshot/cursor/press and explicit config export/restage | Optional existing observation and opt-in input equivalents | Single; explicit GMCM 1.16.0, own Enabled checkbox; [observed workflow and limits](gmcm-authoring.md) |
| Original GMCM integer-slider authoring recipe | Existing build/package, ready review, screenshot/cursor/press, config reconciliation and explicit export/restage | Optional existing observation and opt-in input equivalents | Single; explicit GMCM 1.16.0, own ArrivalRow 15/17/19 slider; [workflow and limits](gmcm-number-authoring.md) |
| Original inventory/chest persistence recipe | Existing check/build/package, inventory/container reads, diagnostics, exact transfer and fixture save/restart | Default observations plus separately granted transfer, fixture, and input tools | Owned disposable unbound single; wrong packaged quantity 12, corrected 10, native deposit/withdrawal and save/reload; [recipe and limits](inventory-container-authoring.md) |
| Original crop-to-machine authoring recipe | Existing check/package, CP diagnosis, world/inventory/container reads, native world actions, fixture save and cleanup | Default observations plus separately granted world/fixture actions | Single/screen 0 disposable world; Strawberry harvest, corrected Keg output, two Cola and chest persistence; [recipe and retained limits](crop-machine-authoring.md) |
| Original per-player multiplayer recipe | Existing check/build/package, role/screen-selected inventory, container, world, Data, menu, lifecycle, and fixture save | Default observations plus separately granted input and host/authority fixture tools | One owned network-2 host/farmhand pair and one owned two-screen local process; deliberate shared-player bug, distinct state, stale-binding rejection, save/reload, and exact chest observation; [recipe and limits](multiplayer-authoring.md) |
| World/local farmer and selected inventory slot | `project review status` | `stardew_runtime_get` | Same active roles; bounded snapshot, explicit unavailable states |
| Selected Content Patcher diagnosis | `project review cp-diagnose` | `stardew_cp_diagnose` | Active single; explicit pack and CP 2.9.1 provider |
| Selected-mod warnings and exceptions | `project review diagnostics` | `stardew_mod_diagnostics` | Exact active role and staged mod ID; bounded isolated log |
| Refresh selected CP patch JSON | `project review cp-refresh` | `stardew_cp_refresh`, separate `--allow-cp-refresh` | Owned single root CP 2.9.1 target; startup-bound source, explicit files and Data observation |
| Reconcile one staged mod's intentional root config save | `project review config-reconcile --mod` | No; existing tools continue after reconciliation | Exact owned single; config-only drift, JSON validation, unchanged code/content and audit/export; separate from CP refresh; [contract](live-review.md#accept-an-intentional-configuration-change) |
| Canonical structured Data | `project review data` | Data tools | Exact active single player, network role, or local screen validates access to process-shared Data; no per-screen cache |
| Maps and textures | `project review map/texture` | Map/texture tools; preview includes PNG image content | Active single review |
| Audio and observed mod assets | `project review audio/mod-assets` | Audio/mod-asset tools | Active single review; CLI-parity bounds, observed-only asset coverage, no audio or asset export |
| Map / viewport screenshots | Quoted review console command | Screenshot tool | Active selected role or exact local screen; map needs loaded world; viewport is cropped to the selected local screen |
| Button, chord, cursor, wheel, mouse gestures, and bounded text | Quoted review console command | Input tools | Active selected role or exact local screen; MCP requires `--allow-input`; screen-bound MCP omits shared-window text; gestures require an active menu and fresh UI revision |
| Fixture status / navigation | Quoted review console command | Fixture tools | Owned disposable world; MCP requires `--allow-fixture-actions`; any role |
| Fixture building / animal ensure | Quoted review console command | Fixture tools | Owned disposable world; single or host only; MCP fixture opt-in |
| Fixture object ensure / clear | Quoted review console command | No | Owned disposable world; single or host only |
| Fishing preparation / rod observation | Quoted fixture command / runtime status | Observation via `stardew_runtime_get` | Preparation: owned disposable single/host. Observation: selected local player in the existing supported runtime roles. |
| Fixture save | No standalone fixture-save console command | `stardew_fixture_save` | Owned disposable world; single or host; MCP fixture opt-in |

`network-2` means exactly one local host and one farmhand. It does not establish general multiplayer compatibility. MCP role or local-screen selection is fixed at server startup. Narrow CLI title/loading exceptions do not bypass MCP's own readiness checks.

## Read a result

- Exit `0`: the requested operation succeeded. Check its specific result fields before making a broader claim.
- Exit `2`: command syntax or arguments are invalid; use the matching `--help`.
- Exit `3`: a controlled discovery, build, runtime, or ownership failure; read `problems`, warnings, and result-named logs.
- `commandWritten=true`: a console line was delivered, not proof of the command's effect.
- Build identity describes the controlled staged file set, not an in-memory DLL measurement.

The [live guide](live-review.md#finish-or-test-persistence) distinguishes successful cleanup, retained state for restart, and warnings.

# SDVKit documentation

Start with [installation and the two quickstarts](../README.md). The [glossary](glossary.md) defines the terms these pages use. These pages describe `main`; [published versions](https://github.com/Nana1873/SDVKit/releases) link to their corresponding tags.

| Task | Guide |
| --- | --- |
| Check supported runtimes, companions, limits, 2.x compatibility and upgrades | [Support policy](support.md) |
| Create, inspect, check, build, or package a mod | [Toolkit](toolkit.md) |
| Author a conditional CP change through live proof and ZIP | [CP authoring recipe](cp-authoring.md) |
| Implement a C# event/config feature and diagnose a runtime error | [SMAPI authoring recipe](smapi-authoring.md) |
| Build and review one explicit C# and associated content-pack bundle | [Bundle Arrival recipe](bundle-authoring.md) |
| Author a multilingual SMAPI dialogue through fallback, controller, and reload proof | [Multilingual UI authoring recipe](multilingual-ui-authoring.md) |
| Test a purchase in your shop | [Shop purchase guide](test-a-shop-purchase.md) |
| Change and verify a GMCM option (checkbox or slider) | [GMCM option guide](test-a-gmcm-option.md) |
| Check the GMCM contract and API interface | [GMCM contract](gmcm-authoring.md) |
| Transfer items to a chest and prove they persist | [Chest transfer guide](test-a-container-transfer.md) |
| Test a machine rule with native harvest, machine and chest actions | [Machine rule guide](test-a-machine-rule.md) |
| Author a per-player multiplayer mod and prove distinct role and screen state | [Multiplayer authoring recipe](multiplayer-authoring.md) |
| Test a save-data migration across restarts | [Data migration guide](test-a-data-migration.md) |
| Check the data migration sample's schema and behaviour table | [Data migration recipe](data-migration-authoring.md) |
| Run a smoke, review a mod, or test persistence | [Live review](live-review.md) |
| Refresh selected CP patches during a review | [CP refresh](cp-refresh.md) |
| Accept an intentional staged config save without restarting | [Configuration reconciliation](live-review.md#accept-an-intentional-configuration-change) |
| Explain a selected Content Patcher change | [CP diagnosis](cp-diagnosis.md) |
| Read Data, maps, textures, audio, or observed mod assets | [Inspection reference](inspection.md) |
| Inspect an explicitly selected saved world offline | [Save inspection](save-inspection.md) |
| Inspect active inventory, shop, or public mod-menu controls | [Menu inspection](menu-inspection.md) |
| Observe supported Gold-shop offers, money and inventory | [Shop inspection](shop-inspection.md) |
| Inspect every bounded backpack slot without opening a menu | [Inventory inspection](inventory-inspection.md) |
| Inspect bounded live soil, crops, machines, terrain features and furniture | [World inspection](world-inspection.md) |
| Observe selected NPC/mail/quest state and prove an original recipe through reload | [Progression authoring and observation](progression-authoring.md) |
| Connect an agent to a running review | [Native MCP](mcp.md) |
| Observe world and local-player values | [Runtime state](runtime-state.md) |
| Prepare a natural fishing test and observe its rod | [Fishing preparation](lab-reference.md#fishing-preparation) and [fishing state](runtime-state.md#fishing-observations) |
| Diagnose lifecycle, staging, and cleanup | [Lab reference](lab-reference.md) |
| Look up a term such as owned, role, screen or test save | [Glossary](glossary.md) |
| Prepare a lab root for Claude Code or Codex | [Set up the agent layer](toolkit.md#set-up-the-agent-layer) |
| Build or contribute to SDVKit | [Contributing](../CONTRIBUTING.md) |
| Walk, use tools, open menus and other player actions, with their proof | [Player actions](player-actions.md) |
| Run SDVKit's live acceptance scripts | [Acceptance scripts](../scripts/acceptance/README.md) |
| Verify and publish a version | [Release procedure](releasing.md) |
| Find changes since an earlier version | [Changelog](../CHANGELOG.md) |

PowerShell examples use `& $sdvkit`, the absolute executable path set during installation. They require PowerShell 7.3 or later: earlier versions, including Windows PowerShell 5.1, strip the double quotes inside JSON tool arguments such as `'{"button":"C"}'`. Keep the current directory at the intended lab root for every live command. Placeholder operands such as `<asset>` must be replaced; examples naming companions require your explicit local mod selection.

## Capability matrix

Terms such as owned, role, screen, test save and opt-in are defined in the [glossary](glossary.md). The task recipes above combine these capabilities; they are not listed again here.

### Toolkit: no game needed

| Capability | Command | Notes |
| --- | --- | --- |
| Create, inspect and package a mod | `project create/inspect/package` | C# mods and content packs |
| Offline authoring check | `project check` | Manifest, CP 2.9.x content, literal `Include`/`FromFile` targets and direct i18n files; tokenized references only warn |
| Translation keys and placeholders | `project check` | Direct top-level i18n files; see [i18n authoring](i18n-authoring.md) |
| Build | `project build` | One C# project against a complete game and SMAPI installation |
| Read a save offline | `save sections/inspect` | A selected local save file or the registered test save, read from an isolated copy; Stardew 1.6; not in MCP |
| Prepare a lab root for agents | `lab init` | See [set up the agent layer](toolkit.md#set-up-the-agent-layer) |

### Live lab: lifecycle

| Capability | CLI | Topology and prerequisites |
| --- | --- | --- |
| Automated smoke | `project smoke` | A standalone C# mod; `single` or `network-2` |
| Review lifecycle | `project review start/status/stop/reset` | C# source or a ready code mod in either topology. A content pack only in `single`, with its provider. A ready code and pack bundle only in `single`, with every pack selected |
| Local split-screen | `project review split-screen join/leave/status` | `single` with `--test-save`; one extra local player. Leaving or replacing a screen invalidates its bindings. See [workflow](live-review.md#local-split-screen-review) |
| Network farmhand leave and rejoin | `project review network leave/join/status --role` | A passed `network-2` pair. See [workflow](live-review.md#leave-and-rejoin-the-network-farmhand) |
| Accept an intentional config save | `project review config-reconcile --mod` | `single`; only `config.json` changed. See [contract](live-review.md#accept-an-intentional-configuration-change) |
| Refresh selected CP patch files | `project review cp-refresh` | `single`, a root CP 2.9.1 pack. See [CP refresh](cp-refresh.md) |

### Live lab: observation

Every read has a CLI command and a native MCP tool that need no opt-in. `project review call <tool>` runs any MCP tool once from the CLI.

| Capability | CLI | MCP tool | Where it works |
| --- | --- | --- | --- |
| World, local player, selected slot | `project review status` | `stardew_runtime_get` | Any role or screen |
| Selected-mod warnings and exceptions | `project review diagnostics` | `stardew_mod_diagnostics` | Any role or screen |
| Open menu geometry and controls | `project review menu` | `stardew_menu_get` | World ready; any role or screen. See [menu inspection](menu-inspection.md) |
| Backpack | `project review inventory` | `stardew_inventory_get` | World ready; any role or screen. See [inventory inspection](inventory-inspection.md) |
| Open vanilla chest and both sides | `project review container` | `stardew_container_get` | Any role or screen. See [container inspection](container-inspection.md) |
| Soil, crops, machines, trees, grass, bushes, clumps and furniture in an area | `project review world` | `stardew_world_area_get` | Any role or screen; at most 256 tiles. See [world inspection](world-inspection.md) |
| Gold-shop offers and purchases | `project review shop` | `stardew_shop_get` | `single`, unbound. See [shop inspection](shop-inspection.md) |
| NPC friendship, mail and quests | `project review progression` | `stardew_progression_get` | `single` test save, unbound. See [progression](progression-authoring.md) |
| Structured Data | `project review data` | Data tools | Any role or screen |
| Maps, textures, audio, observed mod assets | `project review map/texture/audio/mod-assets` | Map, texture, audio and mod-asset tools | `single`, unbound. See [inspection](inspection.md) |
| Content Patcher diagnosis and patch order | `project review cp-diagnose` | `stardew_cp_diagnose` | `single`, with the pack and CP 2.9.1. See [CP diagnosis](cp-diagnosis.md) |
| Map and viewport screenshots | `project review call stardew_screenshot_capture` | `stardew_screenshot_capture` | Any role or screen; map screenshots need a loaded world |

### Live lab: actions

MCP tools and `project review call` need the opt-in flag. The dedicated CLI commands `project review interact`, `act` and `container-transfer` need none.

| Capability | Tools | Opt-in | Where it works |
| --- | --- | --- | --- |
| Buttons, chords, cursor, wheel, mouse gestures, text | `stardew_input_*` | `--allow-input` | Any role or screen; screen-bound servers have no text input. Gestures need an open menu and a fresh `uiRevision`. See [input behavior](live-review.md#input-behavior) |
| Fixture status and navigation; warp, zoom, window | `stardew_fixture_*`, `stardew_lab_warp/set_zoom/set_window` | `--allow-fixture-actions` | Test save; any role, not with `--screen` |
| Buildings, animals and objects; time, weather, season; save | `stardew_fixture_*_ensure`, `stardew_fixture_object_clear`, `stardew_lab_set_time/weather/season`, `stardew_fixture_save` | `--allow-fixture-actions` | Test save; `single` or host |
| Fishing preparation | `stardew_fixture_fishing_prepare` | `--allow-fixture-actions` | Test save; `single` or host. See [fishing](lab-reference.md#fishing-preparation) |
| Give the player an item | `stardew_fixture_item_add` | `--allow-fixture-actions` | Test save; `single` or host. See [item preparation](lab-reference.md#item-preparation) |
| Bring a villager next to the player | `stardew_fixture_npc_place` | `--allow-fixture-actions` | Test save; `single` or host. See [NPC placement](lab-reference.md#npc-placement) |
| Water, harvest, machine insert and collect | `stardew_world_interact` (CLI: `project review interact`) | `--allow-world-actions` | Test save; single, role or screen ([roles and screens](world-interaction.md#roles-and-screens)). See [world interaction](world-interaction.md) |
| Action button or tool on a tile next to the player | `stardew_world_act` (CLI: `project review act`) | `--allow-world-actions` | Test save; single, role or screen, refused on gamepad-mode screens. See [world interaction](world-interaction.md#action-button-or-tool-on-a-tile-next-to-the-player) |
| Walk exactly one tile, or turn | `stardew_world_move` (CLI: `project review act move`) | `--allow-world-actions` | Test save; single, role or screen; no pathfinding. See [move exactly one tile](world-interaction.md#move-exactly-one-tile) |
| Move a quantity between backpack and chest | `stardew_container_transfer` (CLI: `project review container-transfer`) | `--allow-container-transfer` | Test save; single, role or screen. See [container transfer](container-transfer.md) |
| Refresh CP patches | `stardew_cp_refresh` | `--allow-cp-refresh` | `single`, a root CP 2.9.1 pack |

`project review command` runs the reviewed mod's and SMAPI's console commands. While the test save is not loaded yet, such as at the title screen, it also accepts `sdvkit input ...` and `sdvkit screenshot viewport ...`. MCP fixes its role or screen when the server starts.

## Read a result

- Exit `0`: the requested operation succeeded. Check its specific result fields before making a broader claim.
- Exit `2`: command syntax or arguments are invalid; use the matching `--help`.
- Exit `3`: a controlled discovery, build, runtime, or ownership failure; read `problems`, warnings, and result-named logs.
- `commandWritten=true`: the review ran the command on its game thread. This is not proof of the command's effect. `false` means it did not run; a missing value means delivery is unknown and the command must not be retried automatically.
- Build identity describes the controlled staged file set, not an in-memory DLL measurement.

The [live guide](live-review.md#finish-or-test-persistence) distinguishes successful cleanup, retained state for restart, and warnings.

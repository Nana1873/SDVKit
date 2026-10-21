# Player actions in the lab

This matrix lists the everyday keyboard-and-mouse player actions, how to perform each one in a review, and what state read proves its effect. An input result proves only that the input was delivered (see [evidence levels](glossary.md#evidence-levels)). Gaps link to their issues.

"Live" names where the action was verified in a game:
- **script:** [`scripts/acceptance/player-actions.ps1`](../scripts/acceptance/player-actions.ps1) or another [acceptance script](../scripts/acceptance/README.md), in a `single` review on the test save;
- **2.0 release:** the [2.0.0 acceptance](https://github.com/Nana1873/SDVKit/issues/286), where input on split-screen screen 1 opened a menu only on that screen;
- **host, farmhand, screen 1:** the same script with `-Topology network-2 -Role <host|farmhand>` or `-Screen 1` ([#299](https://github.com/Nana1873/SDVKit/issues/299)), which runs only the walking, hotbar and game-menu rows through that network role or split-screen screen.

All input uses SDVKit's [process-local](glossary.md#commands-and-results) input and needs `--allow-input`. A chord (`stardew_input_chord`) holds 1–8 buttons for 1–120 ticks and needs a `uiRevision` from `stardew_menu_get`; one is issued also when no menu is open.

## Movement

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Walk exactly one tile | `stardew_world_move` with `direction` (`--allow-world-actions`, test save); CLI `project review act move <direction>` | Its `reached`, `tileX`/`tileY`, `locationId` and `position`; `runtime.tileX`/`tileY` agree in a newer sample | script (four directions, a blocked target refused with `worldMoveTargetBlocked`) | Single only ([#297](https://github.com/Nana1873/SDVKit/issues/297)); no pathfinding; a warp on the target tile changes the location instead |
| Walk in a direction for a while | Chord `W`, `A`, `S` or `D` | `runtime.tileX`/`tileY` change; `position.facing` is the walking direction; `position.standingX`/`standingY` show the sub-tile position | script; host; farmhand; screen 1 | Equal tick counts can move 2 or 3 tiles; use `stardew_world_move` for an exact stop ([#293](https://github.com/Nana1873/SDVKit/issues/293)) |
| Walk instead of run | Chord `LeftShift` with the direction | Fewer tiles for the same ticks (about 1 instead of 2–3 for 30 ticks) | script; host; farmhand; screen 1 | No running state is reported |
| Stop | Release (end of the chord) | The tile is unchanged in a newer runtime sample | script; host; farmhand; screen 1 | — |
| Face a direction | `stardew_world_move` with `turnOnly: true`; or press the direction key once | `localPlayer.data.position.facing`; `toolTileX`/`toolTileY` is where a tool lands | script (both ways); host, farmhand and screen 1 (by key) | Both shift the player a few pixels inside its tile |
| Change location through a map warp | Chord into the warp tiles listed by `stardew_map_warps_list`, or `stardew_world_move` onto the warp tile; for action doors listed by `stardew_map_actions_list`, `stardew_world_act` with `check` on the door from a neighbouring tile | `runtime.locationId`; a move reports the new `locationId` with `reached: false` | script (Farm to BusStop by chord) | Doors through `check` and warps by `move` are not in the script yet |
| Warp for setup | `stardew_lab_warp {location,x,y}` (`--allow-fixture-actions`, test save) | Its result and `runtime` | script; host; farmhand | Not on split-screen screens ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |

## Interaction and tools

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Action button on a tile next to the player | `stardew_world_act` with `check` (`--allow-world-actions`, test save); or park the cursor away and press `X` | The effect, for example a menu | script (`check` on the Farm sign opens its `LetterViewerMenu`; `X` on a chest) | Single only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Open a chest | As above | `stardew_menu_get` shows `ItemGrabMenu` | script (crop-machine) | — |
| Hoe | Select the hoe, then `stardew_world_act` with `useTool`; or face the tile, park the cursor and press `C` | `stardew_world_area_get`: the tile gets `soil`; stamina drops | script (both ways) | — |
| Pickaxe on a stone | Same, with the pickaxe | The stone object becomes `missing`; stamina drops | script | — |
| Pickaxe on a boulder | Same, with the pickaxe | `stardew_world_area_get`: `feature.kind: resourceClump`, compare `state.health` or `feature: null` on all covered tiles after the animation | not yet verified live | Tool upgrade and target determine the native effect |
| Scythe on weeds | Same, with the scythe | The weeds object becomes `missing` (the scythe costs no stamina) | script | — |
| Scythe on grass | Same, with the scythe | `stardew_world_area_get`: grass `feature.state.numberOfWeeds` drops or `feature` becomes `null` | live check (4 to 1 blades), not in the script | Read the swing area; a scythe swings an arc |
| Axe on a tree | Same, with the axe | `stardew_world_area_get`: tree `feature.state.health` drops, `stump` becomes `true`, or `feature` becomes `null` after the animation | live check (health 10 to 9), not in the script | [Feature coverage and overlap limits](world-inspection.md#terrain-features-and-furniture) |
| Water, harvest, machine insert and collect | `stardew_world_interact` (`--allow-world-actions`) | `world_area_get` soil, crop and machine fields; inventory | script (crop-machine) | Single and screen 0 only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Get an item to test with | `stardew_fixture_item_add` (`--allow-fixture-actions`, test save) | Its `item.slot` and `inventoryRevision`; `stardew_inventory_get` | script (a chest) | Setup, not proof |
| Place an item or object | Select it, then `stardew_world_act` with `check` on the tile | `objectQualifiedItemId` on the tile; the slot empties | script (a chest from `stardew_fixture_item_add`) | — |
| Place furniture | Select it, then `stardew_world_act` with `check` on the tile | `stardew_world_area_get`: `feature.kind: furniture` with its ID and origin on the covered tiles; the slot empties | — | Not verified live |
| Eat or drink | Select it, `stardew_world_act` with `check` on a free neighbouring tile, click the Yes choice from `menu_get` | `localPlayer.data.stamina`/`health`, stack | — | Not in the script yet |
| Find villagers | `stardew_runtime_get` `localPlayer.data.npcs` (name, tile, facing) | The list itself | script | Villagers only; current location only |
| Bring a villager next to the player | `stardew_fixture_npc_place` (`--allow-fixture-actions`, test save) | Its `navigation` tile; `npcs` | script (Lewis) | Setup, not proof; its schedule can move it again |
| Talk to an NPC | Select a tool, `stardew_world_act` with `check` on the NPC's tile; close the dialogue by clicking it | `menu_get` `DialogueBox`; `stardew_progression_get` friendship rises | script (Lewis) | Progression reads are single only |
| Give a gift | Select the gift (for example from `stardew_fixture_item_add`), then `check` on the NPC's tile | Friendship rises; the slot empties | script (a parsnip to Lewis) | Progression reads are single only |
| Read a property-backed sign or the town mailbox | Find `Message` or `TownMailbox` with `stardew_map_actions_list`, then `stardew_world_act` with `check` on that tile from a neighbouring tile | `menu_get` dialogue or letter; progression mail | script (`check` on the Farm sign) | Single only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Read the Farm mailbox | `stardew_world_act` with `check` on its known position from a neighbouring tile | `menu_get` letter; progression mail | — | Handled by game code, not a map tile property, so `stardew_map_actions_list` does not list it |

## Items, menus and containers

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Select a hotbar slot | Press `D1`–`D9`, `D0`, `OemMinus`, `OemPlus` | `localPlayer.data.selectedSlot` | script; host; farmhand; screen 1 | The mouse wheel needs a menu and is refused with `inputMenuMissing` ([#294](https://github.com/Nana1873/SDVKit/issues/294)) |
| Open the game menu, switch tab | Press `E`; click a `tab` component from `menu_get` | `gameMenu` `currentTab` | script; host; farmhand; screen 1 | — |
| Close a menu | Press `Escape` (the close component is also listed by `menu_get`) | `menuOpen=false` | script (game menu, `Escape`); host; farmhand; screen 1 | A synthetic controller B has side effects ([#273](https://github.com/Nana1873/SDVKit/issues/273)) |
| Move items in the inventory | Click the source `inventorySlot` component (picks the item up), then the target one (places it); a drag only picks it up | `stardew_inventory_get` slots | script; host; farmhand; screen 1 | The held item is not reported by `inventory_get` |
| Chest and backpack transfer | Click slot components, or `stardew_container_transfer` (`--allow-container-transfer`) | `stardew_container_get` both sides | script (click, crop-machine) | The transfer tool has no script yet; single only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Buy in a shop | Click a sale row from `menu_get` | `stardew_shop_get` money, stock, held item | script (shop) | `SeedShop` Gold offers only |
| Craft | Give the ingredients with `stardew_fixture_item_add`, then click a recipe component of the crafting page | Inventory and crafting page | — | Not in the script yet |
| Change a GMCM option | Click or drag at measured screen positions | Mod state and saved config | script (gmcm-number, gmcm-checkbox) | GMCM exposes no components |

## Fishing

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Prepare and cast | `stardew_fixture_fishing_prepare`, park the cursor, chord `C` | `localPlayer.data.fishing.fishing=true` with the bobber tile | script | — |
| Hook and catch | — | — | — | [#296](https://github.com/Nana1873/SDVKit/issues/296) |

## Roles and screens

Input, menu, inventory, container and world reads are supported for the `network-2` host and farmhand and for split-screen screens ([MCP startup profiles](mcp.md)). Supported world actions, container transfer and most fixture tools are single-only ([#297](https://github.com/Nana1873/SDVKit/issues/297)). The rows above were verified live in `single` unless noted. The walking, hotbar and game-menu rows were also verified on the `network-2` host and farmhand and on split-screen screen 1 with the same script ([#299](https://github.com/Nana1873/SDVKit/issues/299)): every tool call and every menu, inventory and world read carries the `--role` or `--screen` selection, every runtime read asserts the selected role or screen, and inventory reads assert that player's ID (the recorded evidence of the live runs satisfies these assertions). Such a run proves only walking, hotbar and menu input on that player and is not full acceptance. A role run still uses `stardew_fixture_farm` and `stardew_lab_warp` as setup. A screen cannot use the fixture tools, so the local farmhand is not warped and the script walks it from its cabin bed, measuring the run and the Shift walk from the sub-tile position (about 2.5 and 1 tile for 30 ticks); the mouse wheel without a menu is refused there too, and `E` on screen 1 left screen 0 without a menu.

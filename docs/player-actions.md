# Player actions in the lab

This matrix lists the everyday keyboard-and-mouse player actions, how to perform each one in a review, and what state read proves its effect. An input result proves only that the input was delivered (see [evidence levels](glossary.md#evidence-levels)). Gaps link to their issues.

"Live" names where the action was verified in a game:
- **script:** [`scripts/acceptance/player-actions.ps1`](../scripts/acceptance/player-actions.ps1) or another [acceptance script](../scripts/acceptance/README.md), in a `single` review on the test save;
- **2.0 release:** the [2.0.0 acceptance](https://github.com/Nana1873/SDVKit/issues/286), where input on split-screen screen 1 opened a menu only on that screen.

All input uses SDVKit's [process-local](glossary.md#commands-and-results) input and needs `--allow-input`. A chord (`stardew_input_chord`) holds 1–8 buttons for 1–120 ticks and needs a `uiRevision` from `stardew_menu_get`; one is issued also when no menu is open.

## Movement

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Walk in a direction | Chord `W`, `A`, `S` or `D` | `runtime.tileX`/`tileY` change | script | Equal tick counts can move 2 or 3 tiles; no exact stop ([#293](https://github.com/Nana1873/SDVKit/issues/293)) |
| Walk instead of run | Chord `LeftShift` with the direction | Fewer tiles for the same ticks (about 1 instead of 2–3 for 30 ticks) | script | No running state is reported |
| Stop | Release (end of the chord) | The tile is unchanged in a newer runtime sample | script | — |
| Face a direction | Press the direction key once | Not reported | indirect only | [#289](https://github.com/Nana1873/SDVKit/issues/289) |
| Change location through a map warp | Chord into the warp tiles listed by `stardew_map_warps_list` | `runtime.locationId` | script (Farm to BusStop) | Doors that need the action button are not listed ([#300](https://github.com/Nana1873/SDVKit/issues/300)) |
| Warp for setup | `stardew_lab_warp {location,x,y}` (`--allow-fixture-actions`, test save) | Its result and `runtime` | script | Not on split-screen screens ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |

## Interaction and tools

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Action button on the faced tile | Park the cursor away from the player (`stardew_input_cursor_set` at `8,8`), then press `X` | The effect, for example a menu | script (chest) | Stardew aims `X` at the tile under the cursor when it is within one tile; a supported faced-tile action is missing ([#290](https://github.com/Nana1873/SDVKit/issues/290)) |
| Open a chest | As above | `stardew_menu_get` shows `ItemGrabMenu` | script (crop-machine) | SDVKit cannot place a chest outside fixture buildings ([#291](https://github.com/Nana1873/SDVKit/issues/291)) |
| Hoe | Select the hoe with its number key, face the tile, park the cursor, press `C` | `stardew_world_area_get`: the tile gets `soil`; stamina drops | script | [#290](https://github.com/Nana1873/SDVKit/issues/290) |
| Pickaxe on a stone | Same, with the pickaxe | The stone object becomes `missing`; stamina drops | script | Boulders are not observable ([#295](https://github.com/Nana1873/SDVKit/issues/295)) |
| Scythe on weeds | Same, with the scythe | The weeds object becomes `missing` (the scythe costs no stamina) | script | Grass is not observable ([#295](https://github.com/Nana1873/SDVKit/issues/295)) |
| Axe on a tree | Same, with the axe | Not observable | — | [#295](https://github.com/Nana1873/SDVKit/issues/295) |
| Water, harvest, machine insert and collect | `stardew_world_interact` (`--allow-world-actions`) | `world_area_get` soil, crop and machine fields; inventory | script (crop-machine) | Single and screen 0 only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Place an item or object | Select it, face the tile, press `X` | `objectQualifiedItemId` on the tile; stack drops | — | No way to give items ([#291](https://github.com/Nana1873/SDVKit/issues/291)); [#290](https://github.com/Nana1873/SDVKit/issues/290) |
| Eat or drink | Select it, press `X`, click the Yes choice from `menu_get` | `localPlayer.data.stamina`/`health`, stack | — | [#291](https://github.com/Nana1873/SDVKit/issues/291) |
| Talk to an NPC, give a gift | Face the NPC, press `X` | `menu_get` dialogue; `stardew_progression_get` friendship | — | No NPC positions ([#292](https://github.com/Nana1873/SDVKit/issues/292)); [#290](https://github.com/Nana1873/SDVKit/issues/290), [#291](https://github.com/Nana1873/SDVKit/issues/291) |
| Read a sign or the mailbox | Face it, press `X` | `menu_get` dialogue or letter; progression mail | — | Sign tiles are not listed ([#300](https://github.com/Nana1873/SDVKit/issues/300)); [#290](https://github.com/Nana1873/SDVKit/issues/290) |

## Items, menus and containers

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Select a hotbar slot | Press `D1`–`D9`, `D0`, `OemMinus`, `OemPlus` | `localPlayer.data.selectedSlot` | script | The mouse wheel needs a menu and is refused with `inputMenuMissing` ([#294](https://github.com/Nana1873/SDVKit/issues/294)) |
| Open the game menu, switch tab | Press `E`; click a `tab` component from `menu_get` | `gameMenu` `currentTab` | script | — |
| Close a menu | Press `Escape` (the close component is also listed by `menu_get`) | `menuOpen=false` | script (game menu, `Escape`) | A synthetic controller B has side effects ([#273](https://github.com/Nana1873/SDVKit/issues/273)) |
| Move items in the inventory | Click the source `inventorySlot` component (picks the item up), then the target one (places it); a drag only picks it up | `stardew_inventory_get` slots | script | The held item is not reported by `inventory_get` |
| Chest and backpack transfer | Click slot components, or `stardew_container_transfer` (`--allow-container-transfer`) | `stardew_container_get` both sides | script (click, crop-machine) | The transfer tool has no script yet; single only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) |
| Buy in a shop | Click a sale row from `menu_get` | `stardew_shop_get` money, stock, held item | script (shop) | `SeedShop` Gold offers only |
| Craft | Click a recipe component of the crafting page | Inventory and crafting page | — | Needs ingredients ([#291](https://github.com/Nana1873/SDVKit/issues/291)) |
| Change a GMCM option | Click or drag at measured screen positions | Mod state and saved config | script (gmcm-number) | GMCM exposes no components |

## Fishing

| Action | How | Proof | Live | Gap |
| --- | --- | --- | --- | --- |
| Prepare and cast | `stardew_fixture_fishing_prepare`, park the cursor, chord `C` | `localPlayer.data.fishing.fishing=true` with the bobber tile | script | — |
| Hook and catch | — | — | — | [#296](https://github.com/Nana1873/SDVKit/issues/296) |

## Roles and screens

Input, menu, inventory, container and world reads are supported for the `network-2` host and farmhand and for split-screen screens ([MCP startup profiles](mcp.md)). Supported world actions, container transfer and most fixture tools are single-only ([#297](https://github.com/Nana1873/SDVKit/issues/297)). The rows above were verified live in `single` unless noted; live checks of the input-only actions on the farmhand and on a second screen are tracked in [#299](https://github.com/Nana1873/SDVKit/issues/299).

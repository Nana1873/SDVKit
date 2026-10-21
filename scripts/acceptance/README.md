# Live acceptance scripts

Each script runs one SDVKit workflow end to end against the real game: build, package, start a review, act through SDVKit's tools, read the effect back, stop and reset. It stops at the first unmet check, always cleans up, and leaves its evidence below the lab root.

| Script | Proves | Extra input | Guide |
| --- | --- | --- | --- |
| [`shop.ps1`](shop.ps1) | A broken shop config is diagnosed; one click buys an offer (money, stock, held item), a second click moves it into the backpack. | none | [Test a purchase in your shop](../../docs/test-a-shop-purchase.md) |
| [`crop-machine.ps1`](crop-machine.ps1) | A wrong Content Patcher Keg rule yields Wine; the correct rule turns a natively watered and harvested Strawberry into two Joja Cola, refuses stale or unfaced actions, and the Cola survive a chest transfer, a save and a restart. | `-ContentPatcher` (2.9.1 folder) | [Test a machine rule](../../docs/test-a-machine-rule.md) |
| [`player-actions.ps1`](player-actions.ps1) | Walking in four directions and walking versus running; `stardew_world_move` (exactly one tile in each direction with the reported tile confirmed by a runtime read, a turn without a tile change, and a blocked target refused with `worldMoveTargetBlocked`); `lab_warp` and a location change through a map warp; hotbar number keys and the wheel refusal; game menu open, inventory move by two clicks, tab click and Escape; hoe, pickaxe and scythe on the faced tile; `stardew_world_act` (hoe by `useTool`, the Farm sign by `check`, refusal of a far tile); `stardew_fixture_item_add` and placing the added chest by `check`; `stardew_fixture_npc_place`, the runtime NPC list, talking to Lewis and giving him a gift (friendship rises each time); fishing preparation and cast. With `-Topology network-2 -Role <host\|farmhand>` or `-Screen <id>`, only the input-only sections (walking, hotbar keys, game menu with tab switch, inventory move by two clicks, Escape) run, through that network role or split-screen screen. | none | [Player actions](../../docs/player-actions.md) |
| [`gmcm-number.ps1`](gmcm-number.ps1) | GMCM slider moves stay unsaved; Save writes the new value, `config-reconcile` accepts it, and a restart with the saved config shows its effect. | `-Gmcm` (1.16.0 folder) | [Change and verify a GMCM option](../../docs/test-a-gmcm-option.md) |

## Run a script

Requirements: PowerShell 7.3 or later, one ready game and SMAPI installation (`sdvkit doctor`), a built or extracted `sdvkit.exe`, and the extra inputs above. The sample mods come from `docs/examples/`.

```powershell
pwsh scripts/acceptance/shop.ps1 -SdvKit .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
```

- `-LabRoot` is the lab every live command runs from. Use a dedicated folder with a short path; Windows limits path length below it. The scripts can share one lab root; run one at a time.
- It refuses a lab root that is, or is inside, the game folder or the normal Stardew data folder. Before starting, it requires both topologies to be stopped with nothing retained. It stops and resets only a review whose target lies in its own evidence folder, also when a start fails or the run fails between a stop and a planned restart. Like any `project review status` call, the preflight finalizes a review whose game has already exited. The lab root check compares paths as written and does not resolve junctions.
- It records a listing of the game folder's top level, the game's `Mods` folder and the normal Stardew data folder (saves, settings, logs) before and after, and fails when they differ. A listing compares names, sizes and write times; it does not hash files, and it does not cover a mod manager's own folders. Your own changes during the run count too. `-SkipProtectedCheck` turns this off.
- Exit code `0` means every check passed; `1` means a check failed or cleanup did not complete. The console names the failed check.
- The lab starts the game window without giving it focus, so you can keep using other windows during a run. Clicking into or switching to the game window while a script sends input (also when Windows activates it after you close or minimize the window in front) makes SDVKit report `inputForegroundChanged`: the input may have run, so the run stops as inconclusive. Leave the game window alone during a run, and rerun after such a stop.
- While developing SDVKit, `player-actions.ps1 -Only npc,item` runs the shared setup and then only the named sections (`walk`, `move`, `mapwarp`, `hotbar`, `menu`, `tools`, `act`, `item`, `npc`, `fishing`), always in that order. Its evidence goes to `player-actions-partial`. A partial run is never acceptance evidence: PRs and releases need a full run.
- `player-actions.ps1 -Topology network-2 -Role farmhand` (or `-Role host`) starts a `network-2` review on the test-save baseline and runs the `walk`, `hotbar` and `menu` sections through that role; `-Screen <id>` starts the single test-save review, opts it into split-screen and runs them on that screen. The other sections need fixture or world actions that are single-only ([#297](https://github.com/Nana1873/SDVKit/issues/297)) and are skipped with a console note. Every tool call and every menu, inventory and world read carries the `--role` or `--screen` selection. A screen cannot be warped, so its walking test starts in the local farmhand's cabin bed. Evidence goes to `player-actions-farmhand`, `player-actions-host` or `player-actions-screen<id>`; both topologies are stopped and reset at the end, also after a failure.
- Evidence goes to `<lab-root>/.sdvkit/acceptance/<script>/<timestamp>/`: `identity.json` (sdvkit version and SHA-256, game and SMAPI versions) and provider hashes, every SDVKit result as numbered JSON, screenshots, and the protected-folder listings.

The GMCM script clicks fixed screen positions, because GMCM's page exposes no menu components. They were measured for GMCM 1.16.0 in the default 1280x720 lab window, and the script refuses to click in another viewport. For other sizes, measure the slider and Save button on the screenshots and pass `-SliderMinX`, `-SliderMidX`, `-SliderMaxX`, `-SliderY`, `-SliderMaxDragX`, `-SaveX` and `-SaveY`. The checks prove that no move changes the value before Save and that the final drag reaches the maximum; the intermediate Row 17 is visible only on the screenshots.

## Shared helpers

[`SdvKitAcceptance.psm1`](SdvKitAcceptance.psm1) holds the shared steps: run sdvkit and keep its output, call a tool, wait for a ready review, click a menu component by its bounds, take a screenshot, stop and reset, and compare the protected folders. Write a new script for another workflow with these helpers, and add it to the table above and to the [release procedure](../../docs/releasing.md#run-the-selected-live-checks).

## Other recipes

The remaining recipes in `docs/` are guides. These are worth converting into scripts the same way:

| Recipe | Why |
| --- | --- |
| [GMCM checkbox](../../docs/gmcm-authoring.md) | Same flow as the slider; it could share most of `gmcm-number.ps1`. |
| [Inventory and chest](../../docs/inventory-container-authoring.md) | Native chest transfer and persistence; container transfer is a separate capability. |
| [Data migration](../../docs/data-migration-authoring.md) | Save data across stop and restart. |
| [Progression](../../docs/progression-authoring.md) | NPC, mail and quest reads through a reload. |
| [Multiplayer](../../docs/multiplayer-authoring.md) | The only end-to-end `network-2` and split-screen workflow. |
| [Multilingual UI](../../docs/multilingual-ui-authoring.md) | Title-screen language change and controller input. |

These stay guides only, because `project smoke` and the scripts above already cover their live steps: [CP authoring](../../docs/cp-authoring.md), [SMAPI authoring](../../docs/smapi-authoring.md), [Bundle Arrival](../../docs/bundle-authoring.md) and [CP patch order](../../docs/cp-patch-order-recipe.md).

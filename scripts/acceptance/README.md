# Live acceptance scripts

Each script runs one SDVKit workflow end to end against the real game: build, package, start a review, act through SDVKit's tools, read the effect back, stop and reset. It stops at the first unmet check, always cleans up, and leaves its evidence below the lab root.

| Script | Proves | Extra input | Guide |
| --- | --- | --- | --- |
| [`shop.ps1`](shop.ps1) | A broken shop config is diagnosed; one click buys an offer (money, stock, held item), a second click moves it into the backpack. | none | [Test a purchase in your shop](../../docs/test-a-shop-purchase.md) |
| [`crop-machine.ps1`](crop-machine.ps1) | A wrong Content Patcher Keg rule yields Wine; the correct rule turns a natively watered and harvested Strawberry into two Joja Cola, refuses stale or unfaced actions, and the Cola survive a chest transfer, a save and a restart. | `-ContentPatcher` (2.9.1 folder) | [Test a machine rule](../../docs/test-a-machine-rule.md) |
| [`gmcm-number.ps1`](gmcm-number.ps1) | GMCM slider moves stay unsaved; Save writes the new value, `config-reconcile` accepts it, and a restart with the saved config shows its effect. | `-Gmcm` (1.16.0 folder) | [Change and verify a GMCM option](../../docs/test-a-gmcm-option.md) |

## Run a script

Requirements: PowerShell 7.3 or later, one ready game and SMAPI installation (`sdvkit doctor`), a built or extracted `sdvkit.exe`, and the extra inputs above. The sample mods come from `docs/examples/`.

```powershell
pwsh scripts/acceptance/shop.ps1 -SdvKit .\src\SdvKit.Cli\bin\Release\net8.0\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
```

- `-LabRoot` is the lab every live command runs from. Use a dedicated folder with a short path; Windows limits path length below it. The scripts can share one lab root; run one at a time.
- Before starting, a script requires both topologies to be stopped. It never stops a review it did not start.
- It records a listing of the game's `Mods` folder and the normal Stardew data folder before and after, and fails when they differ. Your own changes during the run count too. `-SkipProtectedCheck` turns this off.
- Exit code `0` means every check passed; `1` means a check failed or cleanup did not complete. The console names the failed check.
- Evidence goes to `<lab-root>/.sdvkit/acceptance/<script>/<timestamp>/`: every SDVKit result as numbered JSON, screenshots, and the protected-folder listings.

The GMCM script clicks fixed screen positions, because GMCM's page exposes no menu components. They were measured for GMCM 1.16.0 in the default 1280x720 lab window, and the script refuses to click in another viewport. For other sizes, measure the slider and Save button on the screenshots and pass `-SliderMinX`, `-SliderMidX`, `-SliderMaxX`, `-SliderY`, `-SliderMaxDragX`, `-SaveX` and `-SaveY`.

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

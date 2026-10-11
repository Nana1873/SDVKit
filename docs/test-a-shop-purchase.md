# Test a purchase in your shop

Use this pattern when your mod adds or changes an offer in Pierre's shop (`SeedShop`) and you want to prove a purchase in game. The complete worked example is [`scripts/acceptance/shop.ps1`](../scripts/acceptance/shop.ps1) with its sample mod in [`docs/examples/shop-recipe`](examples/shop-recipe/ShopPurchaseProof/).

`project review shop` reads only the vanilla `ShopMenu` for `SeedShop`, with Gold and ordinary item offers. See [shop inspection](shop-inspection.md) for the exact boundary. Other shops, barter, other currencies and custom shop menus are not read.

## 1. Start a review on the test save

```powershell
& $sdvkit lab test-save --topology single --json
& $sdvkit project review start .\workspaces\MyMod --topology single --test-save --json
```

Wait until the review is ready, including `testSave.phase` `passed` (see [live review](live-review.md#start-a-review)).

## 2. Open the shop and read it

Open the shop the way a player would, or through a console command your mod provides for testing:

```powershell
& $sdvkit project review command 'my_mod_open_shop' --json
& $sdvkit project review shop --json
& $sdvkit project review menu --json
```

From `shop`, note your offer's index, `price`, `stock`, the player's `money` and the backpack. Check at least one empty backpack slot. From `menu`, keep the `uiRevision` and find the `saleRow` components of the `shopMenu`. Sort the visible sale rows by `bounds.y`. With `scrollIndex` 0, the top row is offer 0; component IDs are not offer IDs.

## 3. Click the offer once

Click the center of the sale row's `bounds`, bound to the menu's `uiRevision`:

```powershell
& $sdvkit project review call stardew_input_click '{"x":640,"y":91,"button":"MouseLeft","count":1,"uiRevision":"..."}' --allow-input
```

The result proves only that the click happened. Never repeat it automatically; read the shop first.

## 4. Prove the effect

Read `project review shop` again and compare it with step 2, using the same `launchId` and `playerId`:

- `money` dropped by the price;
- the offer's `stock` dropped by one (unless it is unlimited);
- the bought item is the `heldItem`. Stardew puts a purchase on the cursor first.

To move it into the backpack, read `menu`, find the `inventorySlot` component whose `controllerId` equals an empty slot number, click its center, and read `shop` once more: `heldItem` is null and the backpack holds one more item.

## 5. Finish

```powershell
& $sdvkit project review stop --json
& $sdvkit project review reset --topology single --json
```

To prove that a broken configuration is reported, start a second review with that configuration and read `project review diagnostics --mod <UniqueID> --json`, as the script does with a misspelled shop ID.

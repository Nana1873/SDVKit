# Inspect supported Gold-shop offers

Use `project review shop --json` or the MCP tool `stardew_shop_get {}` in a [world-ready](glossary.md#worlds-and-fixtures) single [review](live-review.md). The read captures current offers, the local player's money, inventory slots and the shop's held item together on the game thread. It sends no input and makes no purchase.

```powershell
& $sdvkit project review shop --topology single --json
```

The MCP tool is in the default single-review profile and needs no opt-in. Network reviews do not support it. For sale-row geometry and a fresh UI revision before any input, use [menu inspection](menu-inspection.md).

## Supported contract

The supported menu is the vanilla `ShopMenu` for `SeedShop`, priced in Gold, with ordinary object offers. Custom menu subclasses, other shops, non-Gold or barter pricing, purchase callbacks and special item behavior are reported as unavailable. SDVKit does not guess a Gold price for them. An unsupported offer is marked as such, and says nothing about how buying that row works.

A successful report has `state=ready`. Its `data` contains `shopId`, `currency`, `scrollIndex`, `offers`, `money`, `inventory` and `heldItem`, plus `identityScope` and `playerId` for comparisons.

- Each offer has `forSaleIndex`, `availability`, `reason`, `item`, `price`, `stock` and `unlimitedStock`.
- A supported item has `qualifiedItemId`, `stack` and `quality`.
- An unsupported offer leaves out item, price and stock and gives its `reason` instead.
- Inventory rows have a zero-based `slot` and a nullable `item`.
- `heldItem=null` means the shop cursor holds no item.

A supported offer is an ordinary `Object` with stack one. It has no recipe, big-craftable or lost-item behavior and no Stardrop or Qi Gem special action. Buyback, trade items, purchase actions and synchronized special stock are excluded. The price is the game's own Gold price for one purchase, after shop price modifiers. SDVKit does not calculate a second price.

Exit `3` or MCP `isError=true` means unavailable. Exit `0` still means you check each offer's `availability` before using its values. A truncated inventory is not reported as complete.

## Read the values correctly

- The offer index points into the captured sale list. It is not a stable item handle or a menu component ID. Check item identity and quality again when you compare reads.
- For a before/after purchase comparison, expect the same `launchId`, `data.identityScope` and `data.playerId`.
- Unlimited stock differs from a finite count. Buying from an unlimited offer may leave its stock unchanged.
- Values describe the shop at capture time. They are not a locked quote, and they do not say that a control can be clicked. The menu UI revision covers the menu and its input; it does not freeze prices or the offer list. SDVKit does not call a purchase callback to predict a result.

## Prove a purchase

1. Confirm the isolated review, the selected mod and the test save.
2. Before input, read the offer with its price and stock, the money, the matching inventory stacks and the held item.
3. Read the current sale-row geometry. Buy once with the opted-in process-local input. An input acknowledgement alone does not prove a purchase.
4. Read again and compare money and item quantities. A bought item may first sit on the shop cursor; report that apart from the inventory. Place it in the inventory and read once more before you claim an inventory increase.
5. Stop the review and reset its test save with the normal CLI. Keep the build identity and the protected-path comparison with your observations.

Do not repeat an uncertain purchase. Read the current state first. Offline tests and compilation prove the adapter contract; only the observed before/after game state proves a purchase.

## Reference details

The read uses the review's request/response transport and checks the review binding. Limits: 256 offers, 144 inventory slots and a 128 KiB response. A capture over a limit is unavailable. The response has five seconds to arrive and pass the final binding check; stale results or results from a changed review are unavailable.

# Inspect supported Gold-shop offers

Use `project review shop --json` or native `stardew_shop_get {}` in a
world-ready single [review](live-review.md). The read captures current offers,
local-player money, inventory slots and the shop's held item together on the
game thread. It reuses the owned request/response transport and verifies the
exact review binding. It sends no input and makes no purchase.

```powershell
& $sdvkit project review shop --topology single --json
```

The MCP tool is present in the default single-review profile, with no input
opt-in. Network reviews are outside this capability. Use
[menu inspection](menu-inspection.md) separately for sale-row geometry and a
fresh UI revision before any authorized input.

## Supported contract

The first supported family is the exact vanilla `ShopMenu` for `SeedShop`,
using Gold, with ordinary object offers. Custom menu subclasses, other shop
families, non-Gold/barter pricing, purchase callbacks and unsupported special
item behavior are unavailable rather than assigned a guessed Gold price.
An unsupported offer is explicitly identified and does not establish purchase
semantics for that row.

Successful reports contain `state=ready` and `data` with `shopId`, `currency`,
`scrollIndex`, `offers`, `money`, `inventory` and `heldItem`, with the captured
`identityScope` and `playerId` for comparisons. Each offer has
`forSaleIndex`, `availability`, `reason`, `item`, `price`, `stock` and
`unlimitedStock`. Supported item values contain `qualifiedItemId`, `stack` and
`quality`. Inventory rows contain a zero-based `slot` and nullable `item`;
`heldItem=null` means the shop cursor holds no item. Unsupported offers withhold
item/price/stock values and give their reason instead.

Supported offers have an exact ordinary `Object` with stack one, no recipe,
big-craftable or lost-item behavior, and no Stardrop or Qi Gem special action.
Buyback, trade items, purchase actions and synchronized special stock are
excluded. The exposed price is the materialized native Gold price for one
purchase, after shop price modifiers; no second price calculation is inferred.

Captures are bounded to 256 offers, 144 inventory slots and a 128 KiB response.
The response must remain within the five-second freshness window through the
final binding check; stale or changed-review results are unavailable.
Exceeding a capture bound is unavailable; a truncated inventory is never
presented as complete. Exit `3` / MCP `isError=true` means unavailable. Exit `0`
still requires checking each offer's availability before using its values.

The offer index identifies an entry in the captured sale list. It is not a
stable item handle or a menu component ID. Item identity and quality must be
checked again when comparing observations. Require the same `launchId`,
`data.identityScope` and `data.playerId` for a before/after purchase comparison.
Unlimited stock is distinct from a
finite count; buying from an unlimited offer need not reduce its exposed stock.

These are observations at capture time, not a locked quote or an assertion
that a control can be clicked. The menu UI revision covers the menu/input
contract; it does not freeze shop prices or the offer list. No hypothetical
purchase callback is invoked to predict its result.

## Prove a purchase

1. Confirm the exact isolated review, selected mod and disposable fixture.
2. Capture the supported offer and its price/stock, money, matching inventory
   stacks and held item before input.
3. Inspect current sale-row geometry and use the existing authorized
   process-local input to buy once. An input acknowledgement alone is not
   purchase proof.
4. Capture again and compare actual money and item quantities. A purchased
   item may initially be held by the shop cursor; report that separately from
   inventory. Place it in the inventory and capture once more before claiming
   an inventory increase.
5. Stop the owned review and reset its fixture through the normal CLI. Retain
   exact artifact identity and protected-path comparison with the observations.

Do not repeat an uncertain purchase blindly. Inspect the current state first.
Offline tests and compilation establish the adapter contract; only the observed
before/after game state proves a purchase in that environment.

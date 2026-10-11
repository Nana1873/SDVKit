# Inspect the selected vanilla chest

Use `project review container --json` or the MCP tool `stardew_container_get {}` while a supported chest menu is open for the selected player. It needs an [owned](glossary.md#reviews-and-ownership), [world-ready](glossary.md#worlds-and-fixtures) review; see [live review](live-review.md). The read captures the chest, both inventory sides and the menu's held item together on the game's main thread. It does not open a chest, send input, move or sort items, invoke callbacks, scan a location or read a save.

```powershell
& $sdvkit project review container --topology single --json
& $sdvkit project review container --topology network-2 --role farmhand --json
& $sdvkit project review container --topology single --screen 1 --json
```

The MCP tool is available on every single, network-role and local-screen server and needs no opt-in. It reads only the selected player's menu, location and chest. It does not fall back to another player or another open menu.

## Supported chests

The menu is a vanilla `ItemGrabMenu` for a placed `StardewValley.Objects.Chest`. Both the menu's `context` and `sourceItem` point to that chest. The menu's upper inventory is that chest's inventory for the selected player. The lower inventory is the player's current backpack.

The chest is a regular player chest at its current location and tile:

- vanilla wooden chest `(BC)130` or stone chest `(BC)232`, with 36 slots;
- no child menu, fridge, gift box, special chest type or global inventory ID.

Not supported: big chests, Junimo or shared storage, Mini-Shipping Bins, auto-loaders, fridges, dressers, shipping bins, mod containers, subclasses, copied lists, remote or closed chests and unplaced chest menus. The read does not search for another backing object.

## Identity, revision, and fields

A ready report contains:

- `captureId`: unique to this request.
- `identityScope`: the lifetime of the active menu, from [menu inspection](menu-inspection.md).
- `backingIdentity`: a token for the native chest reference within that menu lifetime.
- `selectionIdentity`: changes when you close and reopen the menu, the chest reference is replaced or you select another chest.
- `containerRevision`: changes when any player or chest slot or the held item changes, including stack or quality.
- `playerId`, `locationName`, `tileX`, `tileY` and `chestItemId`.
- `player` and `container` sides, each with `side`, `capacity` and every zero-based `slot` as `empty`, `occupied` or `unavailable`.
- `heldItem`, with the same three states.
- `complete`, and the only partial limitation `itemDataUnavailable`.

Occupied slots and held items use the [backpack item facts](inventory-inspection.md): `qualifiedItemId`, positive `stack` and nullable non-negative `quality`. A failing item getter makes only that slot unavailable. Same-ID stacks with different counts or quality produce different revisions.

Each non-empty slot also has `instanceIdentity` and `itemRevision` tokens. The instance token changes when the native item is replaced, even with identical facts. The item revision changes when the item's stacking behavior changes. Items from another assembly report `itemDataUnavailable`, because SDVKit does not assume how they stack.

All identities are comparison tokens, not object handles. They mean something only within the reported launch and menu lifetime. A read alone does not authorize a [container transfer](container-transfer.md); the transfer revalidates fresh tokens before it acts.

Exit `3` or MCP `isError=true` means no supported chest was found for the selected player.

## Compare a chest change

1. Confirm the review and its test save with `project review status`.
2. Open a known supported chest in the game.
3. Read the container and keep both identities and both sides.
4. Close it and open a different known chest. Read again. Expect a new selection identity, the expected location and tile, and the contents you know.
5. Finish with stop and reset. Keep evidence below the ignored `.sdvkit/`.

Use [menu inspection](menu-inspection.md) for the visible menu structure. Use [backpack inspection](inventory-inspection.md) when no chest menu is needed.

## Reference details

- `selectionIdentity` derives from the launch, menu lifetime, player, backing identity, location, tile and chest item ID. `containerRevision` derives from the selection identity plus every exposed slot and the held item.
- `itemRevision` hashes the inputs of the game's base `canStackWith` check: runtime type, maximum stack size, name, optional `ColoredObject.color` and optional `Object.orderData`. Quality and qualified ID are already in the content revision. This follows the installed Stardew 1.6.15 code, where vanilla types do not override that method.
- Runtime type, name and `orderData` are limited to 256 characters without control characters. A longer value makes the item unavailable; values are not truncated. Null and empty `orderData` stay distinct.
- Limits: at most 144 player slots, exactly 36 chest slots and a 96 KiB response. A report has five seconds to finish. Launch, process, target, build, fixture, player and world-ready state stay the same through the final status read, or the report is unavailable. Malformed, stale, oversized, ambiguous or unsupported responses are unavailable.

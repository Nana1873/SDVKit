# Inspect the selected vanilla chest

Use `project review container --json` or native `stardew_container_get {}` while
a supported chest menu is already open in a world-ready owned single
[review](live-review.md). The on-demand read captures the backing chest, both
inventory sides, and the menu-held item together on Stardew's main thread. It
does not open a chest, send input, move or sort items, invoke callbacks, scan a
location, or inspect a save.

```powershell
& $sdvkit project review container --topology single --json
```

The MCP tool is in the default single-review observation profile and needs no
input opt-in. Network roles and local screen IDs other than screen 0 are outside
this version.

## Exact supported family

This first contract supports only an exact vanilla `ItemGrabMenu` whose source
is the exact placed `StardewValley.Objects.Chest` referenced by both the menu's
public `context` and `sourceItem`. The source inventory must be the same native
inventory returned by that chest for the selected player, and the lower menu
inventory must be that player's current backpack.

The chest must be a regular local player chest at its current location/tile:
vanilla wooden chest `(BC)130` or stone chest `(BC)232`, 36 slots, no child
menu, fridge, gift box, special chest type, or global inventory ID. Big chests,
Junimo/shared storage, Mini-Shipping Bins, auto-loaders, fridges, dressers,
shipping bins, arbitrary mod containers, subclasses, copied lists, remote or
closed chests, and unplaced chest menus are explicitly unavailable. The read
never searches for an alternative backing object.

## Identity, revision, and fields

A ready report contains:

- `captureId`, unique to this request;
- `identityScope`, the active-menu lifetime from bounded menu inspection;
- `backingIdentity`, an opaque comparison token for the exact native chest
  reference within that menu lifetime;
- `selectionIdentity`, derived from the launch, menu lifetime, player,
  backing identity, location/tile, and supported chest item ID.
  Closing/reopening the menu, replacing the backing reference, or selecting
  another placed chest changes it;
- `containerRevision`, derived from the selection identity plus every exposed
  player/chest slot and the held item. Replacing an exposed item, changing its
  stack or quality, or changing either side changes it;
- `playerId`, `locationName`, `tileX`, `tileY`, and `chestItemId`;
- explicit `player` and `container` sides, each with `side`, `capacity`, and
  every zero-based `slot` represented as `empty`, `occupied`, or `unavailable`;
- `heldItem`, using the same three states independently of either side; and
- `complete` and the sole current partial limitation
  `itemDataUnavailable`.

Occupied slots and held items reuse the [backpack item facts](inventory-inspection.md):
bounded `qualifiedItemId`, positive `stack`, and nullable non-negative `quality`.
Invalid or throwing item getters make only that observation unavailable; the
other slots remain useful. Same-ID stacks with different stack counts or
quality produce different revisions.

Each non-empty observation additionally contains opaque `instanceIdentity` and
`itemRevision` comparison tokens. The instance token changes if the native Item
reference is replaced, even with identical public facts. For vanilla item types,
the item revision hashes the installed game's complete base `canStackWith`
comparison inputs: exact runtime type, maximum stack size, name, optional
`ColoredObject.color`, optional `Object.orderData`, plus the already exposed
quality and qualified ID in the enclosing content revision. This distinction is
based on the installed Stardew 1.6.15 implementation, whose vanilla types do not
override that method. Items implemented by another assembly are explicit
`itemDataUnavailable`, because their stacking semantics are not assumed.
Runtime type, name, and `orderData` are each bounded to 256 characters with no
control characters before hashing. Exceeding a bound makes that item
unavailable; values are never truncated. Native null and empty `orderData`
remain distinct.

All identities are comparison tokens, never native object handles. Opaque
continuity fields do not reveal object data and are meaningful only within the
reported launch/menu lifetime.
A future transfer action must re-resolve the exact native menu/chest references
and revalidate both fresh tokens immediately before mutation; a prior read never
authorizes a transfer.

The player side is limited to 144 slots, the supported chest has exactly 36,
and the response envelope is capped at 96 KiB. Reports must remain within five
seconds and the same launch, process, target/build, fixture, player, and
world-ready binding through the final status read. Malformed, stale, oversized,
ambiguous, unsupported, or out-of-bounds responses are unavailable. Exit `3`
or MCP `isError=true` means no selected supported chest was established.

## Compare a chest change

1. Confirm the exact owned review and disposable world with `project review status`.
2. Open a known supported chest through deliberate native game behavior.
3. Capture container state and retain both identities plus both sides.
4. Close it and open a different known chest. Capture again and require a new
   selection identity, the expected location/tile, and independently known contents.
5. Finish with the owned stop/reset and retain evidence below ignored `.sdvkit/`.

Use [menu inspection](menu-inspection.md) to establish visible menu structure.
Use [backpack inspection](inventory-inspection.md) when no chest menu is needed.

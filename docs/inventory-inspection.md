# Inspect the complete bounded backpack

Use `project review inventory --json` or native `stardew_inventory_get {}` in a
world-ready owned single [review](live-review.md). The read runs on Stardew's
main thread and captures every slot in the selected local player's current
backpack. It does not require or open a menu, send input, move an item, scan a
save, or add work to the periodic runtime status.

```powershell
& $sdvkit project review inventory --topology single --json
```

The MCP tool is present in the default single-review observation profile and
requires no action opt-in. Network roles and local screen IDs other than screen
0 are outside this version.

## Fields and bounds

A successful report has `state=ready`. Its `data` contains:

- `playerId`: `Farmer.UniqueMultiplayerID`, serialized as an exact decimal
  string and checked against the selected review runtime;
- `capacity`: the current `Farmer.Items.Count`; supported values are 1 through
  144, and the response contains exactly that many zero-based `slots`;
- `selectedSlot`: `Farmer.CurrentToolIndex`, or `null` when the game reports no
  selected slot;
- `captureId`: a unique request-scoped ID. It does not identify an item and is
  never reused for another capture;
- `inventoryRevision`: a `sha256:` comparison token derived from this launch,
  player, capacity, selection, slot states, and the supported visible item
  facts. A changed stack changes the revision. It is not an item-instance
  handle: replacing an item with another item having the same exposed facts can
  produce the same revision, so mutation workflows must revalidate their own
  native selection immediately before acting;
- `complete` and `limitations`: `complete=false` has the sole current
  limitation `itemDataUnavailable`; no truncation is returned as a complete
  backpack.

Each slot contains `slot`, `state`, `reason`, and `item`. `empty` has no item.
`occupied` exposes the same bounded item facts used by shop observation:
`qualifiedItemId` from `Item.QualifiedItemId`, positive `stack` from
`Item.Stack`, and nullable non-negative `quality`. Quality is read only for the
standard Stardew `Object` family; `null` means that this supported fact is not
applicable to that item type. `unavailable` with reason `itemDataUnavailable`
means an item occupied the slot but one of its supported getters failed, so no
partial item facts are returned for that slot.

Invalid IDs, stacks, qualities, player values, indices, inconsistent slot
counts, responses larger than 64 KiB, and capacities above 144 make the capture
unavailable. The response must also remain within the five-second freshness
window and the same launch, process, target/build, fixture, player, and
world-ready binding through the final status read. Exit `3` or MCP
`isError=true` means the result is unavailable; no last-known inventory is
substituted.

## Compare a quantity change

1. Confirm the exact target, owned single review, player, and disposable-world
   identity with `project review status`.
2. Capture the backpack and record `launchId`, `playerId`, `captureId`,
   `inventoryRevision`, and the relevant slot facts.
3. Cause the intended change through supported native game or mod behavior.
   Command delivery by itself is not proof of an item change.
4. Capture again. Require the same launch and player, a new capture ID, and the
   expected slot/stack change. A changed revision only says that exposed facts
   changed; inspect the slots to determine what changed.
5. Finish with the normal owned stop/reset and retain evidence below the lab's
   ignored `.sdvkit/` directory.

Use [shop inspection](shop-inspection.md) when price, stock, money, or the shop's
held item also matters. This backpack read never interprets equipment, modData,
arbitrary object fields, remote players, or container contents.

# Inspect the complete backpack

Use `project review inventory --json` or the MCP tool `stardew_inventory_get {}` in a [world-ready](glossary.md#worlds-and-fixtures) [owned](glossary.md#reviews-and-ownership) [review](live-review.md). The read runs on the game's main thread and captures every slot of the selected player's current backpack. It needs no menu and does not open one. It sends no input, moves no item, reads no save and adds no work to the periodic runtime status.

```powershell
& $sdvkit project review inventory --topology single --json
& $sdvkit project review inventory --topology network-2 --role host --json
& $sdvkit project review inventory --topology single --screen 1 --json
```

The MCP tool is available for every single, network-role and local-screen selection and needs no opt-in. It does not fall back to another player.

## Fields and bounds

A successful report has `state=ready`. Its `data` contains:

- `playerId`: `Farmer.UniqueMultiplayerID` as a decimal string, checked against the selected review.
- `capacity`: the current `Farmer.Items.Count`, from 1 to 144. The response has that many zero-based `slots`.
- `selectedSlot`: `Farmer.CurrentToolIndex`, or `null` when the game reports no selected slot.
- `captureId`: a unique ID for this request. It does not identify an item and is not reused.
- `inventoryRevision`: a `sha256:` [revision](glossary.md#commands-and-results) over the launch, player, capacity, selection, slot states and visible item facts. A changed stack changes it. It is not an item handle: replacing an item with one that has the same facts can keep the same revision. A workflow that changes items must revalidate its own selection right before it acts.
- `complete` and `limitations`: `complete=false` has one possible limitation, `itemDataUnavailable`. A truncated backpack is never reported as complete.

Each slot has `slot`, `state`, `reason` and `item`:

- `empty`: no item.
- `occupied`: the same item facts that shop inspection uses. `qualifiedItemId` comes from `Item.QualifiedItemId`, positive `stack` from `Item.Stack`, and `quality` is nullable and non-negative. Quality is read only for the standard `Object` family; `null` means it does not apply to that item type.
- `unavailable` with reason `itemDataUnavailable`: an item is in the slot, but a getter failed or returned an invalid ID, stack or quality. No partial facts are returned for that slot.

Exit `3` or MCP `isError=true` means the result is unavailable. SDVKit does not substitute a last-known inventory.

## Compare a quantity change

1. Confirm the target, review, player and test save with `project review status`.
2. Read the backpack. Record `launchId`, `playerId`, `captureId`, `inventoryRevision` and the relevant slot facts.
3. Cause the change through normal game or mod behavior. Delivering a command does not prove an item change.
4. Read again. Expect the same launch and player, a new capture ID and the expected slot or stack change. A changed revision only says that some fact changed; read the slots to see what.
5. Finish with stop and reset. Keep evidence below the lab's ignored `.sdvkit/`.

Use [shop inspection](shop-inspection.md) when price, stock, money or the shop's held item also matter. The backpack read does not interpret equipment, `modData`, other object fields, remote players or container contents.

## Reference details

SDVKit rejects a report as unavailable when it has invalid item facts, player values or indices, inconsistent slot counts, more than 64 KiB, or a capacity above 144. The response has five seconds to arrive. Launch, process, target, build, fixture, player and world-ready state stay the same through the final status read, or the result is unavailable.

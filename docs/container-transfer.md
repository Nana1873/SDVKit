# Transfer a chest quantity

`project review container-transfer` and the opt-in MCP tool `stardew_container_transfer` move one quantity between a backpack slot and the open chest. A `deposit` moves from the backpack to the chest; a `withdraw` moves from the chest to the backpack. The action works only in an [owned](glossary.md#reviews-and-ownership) review that uses the [test save](glossary.md#worlds-and-fixtures). The CLI command acts as the single review's main player; the MCP tool acts as the server's bound `network-2` role or split-screen screen, on the chest that player has open (see [roles and screens](world-interaction.md#roles-and-screens)). It does not open a chest, select an item, or touch your normal saves or `Mods` folder.

First read the chest with `project review container --json` (or `stardew_container_get {}`). Pass these values back unchanged: source slot, quantity, qualified item ID, selection identity, container revision, source instance identity and source item revision. `deposit` names a player slot; `withdraw` names a chest slot. The quantity is 1-99 and cannot exceed the source stack.

```powershell
& $sdvkit project review container-transfer deposit 0 2 '(O)390' `
  $capture.data.selectionIdentity $capture.data.containerRevision `
  $capture.data.player.slots[0].identity.instanceIdentity `
  $capture.data.player.slots[0].identity.itemRevision --json
```

For MCP, start the server with `--allow-container-transfer`. The read catalogue and the input, fixture and Content Patcher opt-ins do not expose transfer.

## Supported behavior

The item is a stackable base-game `StardewValley.Object`. The chest is a 36-slot wooden or stone chest (see [container inspection](container-inspection.md)). Recipes and special auto-consumed items are refused. The menu's held item has to be empty.

The installed game decides capacity. For a withdrawal, the player inventory has to accept the item. For a deposit, SDVKit tests native chest stacking on isolated item copies. Items with the same qualified ID but incompatible stacking properties do not merge; an empty destination slot still works.

SDVKit performs one native right-click per unit with the process-local virtual cursor. The physical cursor and window focus do not change. Where SDVKit can see the problem, it refuses before any click. Reasons include a changed menu or chest, a replaced slot, a full or incompatible destination, too small a stack, a stale token or an occupied cursor.

## Outcomes and recovery

- `completed`: fresh before and after reads show the source decreased, the destination increased by the same amount, the held item is empty and the total item count is unchanged.
- `refused`: no click was sent.
- `partial`: a smaller change was observed and is reported.
- `uncertain`: clicks may have been sent, but no complete after-read was possible.

Cancellation, client disconnect, timeout, input interruption or a mod callback never causes an automatic retry or rollback. After `partial` or `uncertain`, do not replay the request. Read the chest again and decide from the new identities and item totals.

A report proves only the observed state of the game process. It does not prove persistence until you save and reload. Always finish with review stop and reset.

## Reference details

Before the first click, SDVKit revalidates every supplied identity. Between clicks, these stay consistent or the transfer stops: player, chest, source object, stacking facts, unrelated source slots, incompatible destination objects, held state and viewport. So does the total item count across backpack, chest and held item.

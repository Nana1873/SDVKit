# Transfer an exact chest quantity

`project review container-transfer` and opt-in MCP tool
`stardew_container_transfer` request one exact deposit or withdrawal between a
selected backpack slot and the currently open supported regular vanilla chest.
The action is available only on screen 0 of an exact owned single review using
the SDVKit disposable test save. It never opens a chest, selects an item, or
operates on normal saves or Mods directories.

First call `project review container --json` (or `stardew_container_get {}`).
Pass the source slot, positive quantity, qualified item ID, selection identity,
container revision, source instance identity, and source item revision back
unchanged. `deposit` selects a player slot; `withdraw` selects a chest slot.
The quantity is 1-99 and cannot exceed the source stack.

```powershell
& $sdvkit project review container-transfer deposit 0 2 '(O)390' `
  $capture.data.selectionIdentity $capture.data.containerRevision `
  $capture.data.player.slots[0].identity.instanceIdentity `
  $capture.data.player.slots[0].identity.itemRevision --json
```

For MCP, start the server explicitly with `--allow-container-transfer`. The
default read catalogue and the unrelated input, fixture, and Content Patcher
grants never expose transfer.

## Supported behavior

The current family is a stackable base-game `StardewValley.Object` in an exact
36-slot wooden or stone chest. Recipes and special auto-consumed items are
refused. The held menu item must be empty. Capacity is checked through installed
game behavior: player acceptance for withdrawals and native chest stacking on
isolated item copies for deposits. Same qualified IDs with incompatible native
stacking properties do not merge; an empty destination slot is still valid.

Dispatch uses the existing process-local virtual cursor to perform one native
right-click transfer per unit. Physical cursor position and window focus are not
changed. Before the first click, every supplied identity is revalidated. While
more clicks remain, the exact player, chest, source object, stacking facts,
unrelated source slots, incompatible destination objects, held state, viewport,
and total observed item count must remain consistent. A changed menu/chest,
replaced slot, incompatible/full destination, insufficient stack, stale token,
or occupied cursor is refused before dispatch where observable.

## Outcomes and recovery

`completed` requires the requested source decrease, matching destination
increase, empty held item, and conserved total across backpack, chest, and held
state in fresh before/after captures. `refused` means no click was dispatched.
`partial` reports the observed smaller change. `uncertain` means dispatch may
have occurred but a complete after observation was unavailable. Cancellation,
client disconnection, timeout, input interruption, or a mod callback never
causes an automatic retry or invented rollback.

After `partial` or `uncertain`, do not replay the request. Read the current chest
again and decide manually from the new identities and item totals. Reports prove
only the observed owned process state; they do not prove persistence until the
normal save/reload workflow is completed. Always finish with exact review stop
and reset.

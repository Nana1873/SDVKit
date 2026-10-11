# Bounded world interaction

Use `project review interact` or the MCP tool `stardew_world_interact` (opt-in `--allow-world-actions`) to send one native interaction to one adjacent target you just read. It works only in a single [owned](glossary.md#reviews-and-ownership) review on the [test save](glossary.md#worlds-and-fixtures), on screen 0. It is not available for a normal save, a network role or another split-screen screen.

First read the one-tile target with [`project review world`](world-inspection.md) and the whole backpack with [`project review inventory`](inventory-inspection.md). Pass the target's `instanceId` and `revision`: the soil for `water`, the crop for `harvest`, or the machine. Also pass the full `inventoryRevision`:

```powershell
& $sdvkit project review interact water 64 15 <soil-instance> <soil-revision> <inventory-revision> --topology single --json
```

Only these cases are supported:

- `water`: a live, dry ordinary crop or soil, a selected watering can with water left. Sends one native left-mouse (tool use).
- `harvest`: a ready ordinary hand-harvest (`Grab`) crop, an empty selected slot, no item on the cursor and room in the inventory. Crops that need a scythe are rejected. Sends one native right-mouse (action).
- `machineInsert`: an idle ordinary data-backed machine and a selected object that the machine's own check accepts. Sends one native right-mouse.
- `machineCollect`: a ready ordinary data-backed machine, an empty selected slot and room in the inventory. Sends one native right-mouse.

SDVKit checks the target, inventory and player state before it queues the input and again when it presses. Any changed or unsupported fact fails closed. Input uses the process-local virtual cursor. SDVKit does not move the physical cursor, change focus, send global input, pathfind or navigate for you.

## Results

The result is one of `notDispatched`, `completed` or `mayHaveRun`.

- `notDispatched`: SDVKit sent nothing. Once its cursor is on the target, SDVKit reports this only if all of these hold:
  - no action or tool input is held or queued;
  - no tool is in use and the player can move;
  - a fresh read of the target and backpack shows no change.

  Physical input is outside this contract.
- `mayHaveRun`: anything else where SDVKit cannot rule out that input acted on the target.
- `completed`: the native input finished. It does not prove that watering, harvest, insertion or collection changed the world.

Always read the target and inventory again. Compare their fresh revisions, the selected slot, tool or item, and the slot facts. The preconditions reject an item on the cursor, so good before/after evidence also shows the cursor is still empty; do not assume a hidden output. Never replay a canceled, timed-out or uncertain action blindly.

## Reference details

The checks cover:

- launch, test save, screen 0, and world and player readiness;
- no menu, event, minigame, UI mode or tool use;
- the player faces an adjacent target tile, and the cursor maps visibly to that tile at the current zoom and UI scale;
- target instance and revision, and the full inventory revision;
- the selected item or tool, resources, machine acceptance and capacity.

Like menu clicks, the action first runs one game update that only moves the cursor, then revalidates the target before it presses. Stardew aims a mouse tool use at the previous update's cursor position.

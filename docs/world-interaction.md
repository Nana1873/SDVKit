# Bounded world interaction

Use `project review interact` or the MCP tool `stardew_world_interact` (opt-in `--allow-world-actions`) to send one native interaction to one adjacent target you just read. For the action button or a tool on any neighbouring tile, see [`act`](#action-button-or-tool-on-a-tile-next-to-the-player). It works only in a single [owned](glossary.md#reviews-and-ownership) review on the [test save](glossary.md#worlds-and-fixtures), on screen 0. It is not available for a normal save, a network role or another split-screen screen.

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

## Action button or tool on a tile next to the player

For everything else on a neighbouring tile, use `project review act` or the MCP tool `stardew_world_act` (same opt-in and limits). It needs no target identity, only a fresh `inventoryRevision`:

```powershell
& $sdvkit project review act check 8 7 <inventory-revision> --topology single --json
& $sdvkit project review act useTool 65 20 <inventory-revision> --topology single --json
```

- `check` presses the action button (right mouse) on the tile: open a chest, read a sign, talk to an NPC, give the selected item, eat or place it.
- `useTool` uses the selected tool (left mouse) on the tile: hoe, axe, pickaxe, scythe, watering can. It is refused with `worldActionWrongToolOrState` when no tool is selected.

The target must be one of the four tiles next to the player's tile; otherwise the result is `worldActionTargetNotAdjacent` with `notDispatched`. The player does not need to face it: SDVKit puts the virtual cursor on the tile, and the game aims both buttons at the tile under the cursor when it is that close. Without the cursor, the game would pick the tile from where inside its own tile the player stands (see `position` in [runtime state](runtime-state.md#field-contract)).

Verified live so far: `check` on the Farm sign and `useTool` with the hoe (`scripts/acceptance/player-actions.ps1`). The other uses rely on the game's own action button and tool behaviour; the [player-action matrix](player-actions.md) tracks which are verified.

There is no target revision, so `notDispatched` after the cursor was placed also requires an unchanged backpack and no open menu. Prove the effect with a fresh read: `project review menu` for a chest, sign or dialogue, `project review world` for tilled soil or removed objects, `project review inventory` for gifts, food or placed items.

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

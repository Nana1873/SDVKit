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
- `useTool` uses the selected tool (left mouse) on the tile: hoe, axe, pickaxe, watering can. The tool's effect lands when its animation ends. It is refused with `worldActionWrongToolOrState` when no tool is selected.

The target must be one of the four tiles next to the player's tile; otherwise the result is `worldActionTargetNotAdjacent` with `notDispatched`. The player does not need to face it: SDVKit puts the virtual cursor on the tile, and the game starts both buttons at the tile under the cursor when the mouse cursor is visible and that close. Without the cursor, the game would pick the tile from where inside its own tile the player stands (see `position` in [runtime state](runtime-state.md#field-contract)). When the game would ignore the cursor (gamepad controls, a faded or hidden mouse cursor, a held gamepad button), the request is refused with `worldActionCursorAimUnavailable`. SDVKit checks this before placing its cursor and again right before the press; a refusal at the press reports the same code with the dispatch state rules below. With the game's Gamepad mode option on Auto, gamepad mode ends when the game sees keyboard or mouse activity (not with Force On), so use the keyboard and mouse in the lab rather than a controller.

The cursor sets where the game starts, not everything it may touch. Stardew's own fallbacks still apply:

- `check`: the game first pets or inspects a farm animal under the cursor, then tries the tile. If nothing there reacts, it uses or places the held item; placement can pick a free tile nearby. Only when nothing was placed does it try the tiles just above and below the target (which can be diagonal to the player) and the player's own tile, then eat the held food or do a selected weapon's special move.
- `useTool`: when something on the tile can be grabbed (a ready crop, machine output), the game checks that tile instead of using the tool. Weapons (including the scythe) and the fishing rod turn the player toward the tile, then swing an arc or cast in that direction instead of acting on the tile.

`completed` therefore means the button reached the target tile, not where the effect landed.

Verified live so far: `check` on the Farm sign and `useTool` with the hoe (`scripts/acceptance/player-actions.ps1`). The other uses rely on the game's own action button and tool behaviour; the [player-action matrix](player-actions.md) tracks which are verified.

There is no target revision, so `notDispatched` after the cursor was placed also requires an unchanged backpack, no open menu, and an unchanged player (location, tile, facing, stamina, health) and 3x3 tiles around the player. Effects these reads do not cover, such as a toggled gate or a petted animal, can still slip through, so treat `notDispatched` as "SDVKit sent nothing" and read the state you care about. Prove the effect with a fresh read: `project review menu` for a chest, sign or dialogue, `project review world` for tilled soil, removed objects or [feature changes](world-inspection.md#terrain-features-and-furniture), `project review inventory` for gifts, food or placed items.

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

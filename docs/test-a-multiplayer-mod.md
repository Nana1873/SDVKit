# Test a per-player multiplayer mod

Use this pattern when your mod gives or tracks something per player and you want to prove that a host and a farmhand each get their own state, in the live game, through SDVKit's role-bound tools. The complete worked example is [`scripts/acceptance/multiplayer.ps1`](../scripts/acceptance/multiplayer.ps1) with the mods in [`docs/examples/multiplayer-recipe`](examples/multiplayer-recipe/): the sample gives one Stone to the player who presses F7 and shows that player's result on F8. Its design and the manual steps the script does not run are in [the multiplayer recipe](multiplayer-authoring.md).

Read [live review](live-review.md#leave-and-rejoin-the-network-farmhand) and [roles and screens](world-interaction.md#roles-and-screens) first. A `network-2` review starts two game processes on the test-save baseline; every command names `--topology network-2 --role host` or `--role farmhand`, and no read of one role stands in for the other. The script starts the network-2 review three times and a split-screen review once, so allow several minutes.

## 1. Prove the failure first

Start the broken target with the chest probe as a companion (`--companion`). Read both backpacks (0 and 0), press F7 as the host (host 1), then as the farmhand: the host reaches 2 and the farmhand stays at 0, and the farmhand's F8 dialog names the host's farmer ID. A request that lands on `Game1.MasterPlayer` is the failure to diagnose; stop and reset before the next target.

## 2. Prove one player's state per role

Start the corrected target. Press F7 once as the host and twice as the farmhand, one after the other (wait for the first result: the mod ignores an overlapping request). Require host 1 and farmhand 2, and read each player's F8 dialog from a fresh `project review menu --role <role>`: `farmer`, `eventFarmer`, `stone`, `actual` and `eventStone` must match that role, with `persisted=True`. Close each dialogue by clicking it; Escape does not close a `DialogueBox`.

## 3. Act as the farmhand, read as the host

The host prepares the probe's marked chest (`per-player-proof-chest prepare`, Stone 2) beside it on the Farm. The farmhand warps to the Farm, and its own world read must show the chest. Then the farmhand walks one tile with `stardew_world_move` (`--allow-world-actions`; require `reached` and the tile from a fresh runtime read), opens the chest with `stardew_world_act` `check`, reads both sides with `stardew_container_get` (its own player ID, backpack 2, chest 2), and withdraws one Stone with `stardew_container_transfer` (`--allow-container-transfer`) to 3/1. Close the chest on the farmhand before the host opens it (`per-player-proof-chest open`): the host's own read shows the chest at 1 and its own backpack unchanged at 1. Remove the chest with `per-player-proof-chest cleanup`.

## 4. Leave, rejoin, save and restart

`project review network leave --role farmhand`: the host stays loaded and active, and a host inventory read is refused while the farmhand is away. `network join --role farmhand` and wait for the passed pair: the host session ID is unchanged and the farmhand's is new, with the same farmer and Stone 3. Save with the host's `stardew_fixture_save` (`--allow-fixture-actions`), stop **without** reset, start the same packages again, and require new launch IDs, the same two farmer IDs, host 1, farmhand 3 and `persisted=True` in both F8 dialogs. Finish with stop and reset.

## 5. Split-screen, as far as the game allows

On a `single --test-save` review with `split-screen join`, F7 on screen 0 once and on screen 1 twice gives 1 and 2, each with its own F8 dialog (every command carries `--screen`). Screen 1 starts in its cabin, a generated `FarmHouse<guid>` location. Screens have no fixture tools (`--screen` with `--allow-fixture-actions` is a usage error) and an unbound `stardew_lab_warp` into that location is refused with "is not a permanent location", so the host cannot join it there. Screen 1 instead walks to the cabin door with `stardew_world_move` (the door tile itself is refused with `worldMoveTargetBlocked`) and leaves with a held `S` chord, which puts it on the Farm. The host warps next to it, prepares the probe chest, and screen 1 opens it with the probe command (`per-player-proof-chest open screen=1`), reads it with `stardew_container_get`, and withdraws one Stone with `stardew_container_transfer` (3/1); screen 0 then reads the chest at 1 and its own backpack at 1. This is the first live use of the transfer on a screen.

`stardew_world_act` is refused on screen 1 with `worldActionCursorAimUnavailable` before dispatch: Stardew forces gamepad controls on screens other than 0 (`localPlayer.data.input.gamepadControls` is `true`), and gamepad mode acts on the faced tile instead of the cursor tile. To act on a screen, face the target and press the button, as [world interaction](world-interaction.md#roles-and-screens) describes.

## Not covered by the script

- **Screen leave, replacement and persistence after a restart**, and the stale-MCP-client checks for the leaving farmhand: manual steps in [the multiplayer recipe](multiplayer-authoring.md#local-split-screen).
- Opening the chest on a screen with `stardew_world_act` (refused as above) or by walking to it and pressing the action button; the script opens it with the probe's console command.
- Controller input and internet multiplayer, and anything outside one local host with one farmhand.

A pass proves the per-player result of one sample mod for one `network-2` host and farmhand, and per-screen state on one split-screen review. It does not prove other mods, other networks or normal saves.

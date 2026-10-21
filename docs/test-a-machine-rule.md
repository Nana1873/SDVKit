# Test a machine rule

Use this pattern when your mod or content pack changes what a machine produces, and you want to prove it with native player actions: harvest the input, put it in the machine, wait, and collect the output. The complete worked example is [`scripts/acceptance/crop-machine.ps1`](../scripts/acceptance/crop-machine.ps1). It tests a Content Patcher rule in [`docs/examples/crop-machine-recipe`](examples/crop-machine-recipe/) that turns a Strawberry into two Joja Cola in a Keg.

Read [world inspection](world-inspection.md) and [world interaction](world-interaction.md) first. World actions work on the test save and need `--allow-world-actions` for MCP. This recipe uses a `single` review; for a network role or a local screen see [roles and screens](world-interaction.md#roles-and-screens).

## 1. Check the rule before playing

For a Content Patcher pack, confirm that the patch applies and that the final data says what you expect:

```powershell
& $sdvkit project review cp-diagnose --pack My.Pack --provider Pathoschild.ContentPatcher --asset Data/Machines --json
& $sdvkit project review data get Data/Machines '(BC)12' --json
```

`cp-diagnose` should report `loadedAndEnabled`, `conditionsMatch` and `applied`. Rules are tried in order, so check that yours comes before a vanilla rule that also matches.

## 2. Set up the world

Placing a crop, a machine or a chest is setup, not proof. Use a test-only helper mod for it, like the example's `RecipeProbe`, or prepare the test save by hand. Leave the FarmHouse first: `project review call stardew_fixture_farm --allow-fixture-actions`.

## 3. Act natively, bound to fresh reads

Each world action needs the target's `instanceId` and `revision` from a fresh `project review world` read, and the `inventoryRevision` from a fresh `project review inventory` read. The player must stand next to the target and face it, with the right item selected (toolbar keys `D1` to `D0` through `stardew_input_press`).

```powershell
& $sdvkit project review world 41 3 1 1 --json
& $sdvkit project review inventory --json
& $sdvkit project review interact machineInsert 41 3 <instanceId> <revision> <inventoryRevision> --json
```

The actions are `water`, `harvest`, `machineInsert` and `machineCollect`. `state` `completed` proves only that the input ran. If the result says `notDispatched`, nothing happened; read the reason in `errorCode`. Never repeat an action that may have run.

## 4. Prove each effect

Read `world` and `inventory` again after every action:

- after `harvest`: one more input item in the backpack;
- after `machineInsert`: the machine is `processing`, its `input` is your item and its `output` is the expected item and stack, with a positive `minutesUntilReady`;
- after waiting: `readyForHarvest` is true (about 7 real seconds per 10 game minutes);
- after `machineCollect`: the machine is idle and the backpack holds the output.

A wrong rule shows up here as a different output item. The script proves this with a deliberate wrong item ID.

## 5. Persistence and cleanup

To open a chest or another object, face it and press `X` with `stardew_input_press`. Stardew aims `X` at the tile under the mouse whenever the cursor is within one tile of the player, so first park the virtual cursor away from the player, for example `stardew_input_cursor_set` at `8,8`, and check with `project review menu` that the chest menu (`ItemGrabMenu`) opened. If no menu opened, read the state before pressing again: the player must still face the chest and the backpack must be unchanged.

To prove that the output survives a save, put it in a chest through the chest menu (click the `inventorySlot` component, as in [testing a shop purchase](test-a-shop-purchase.md)), save with `stardew_fixture_save`, stop without reset, start the same selection again and read `project review container`. Finish with stop and reset.

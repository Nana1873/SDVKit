# Test a chest transfer and its persistence

Use this pattern when your mod gives items, or you want to prove that items moved into a chest are still there after a save and a restart. The complete worked example is [`scripts/acceptance/container.ps1`](../scripts/acceptance/container.ps1) with the mod in [`docs/examples/container-recipe`](examples/container-recipe/InventoryReward/): it grants ten Stone once per save, places one marked wooden chest and opens it on command.

Read [container inspection](container-inspection.md) and [container transfer](container-transfer.md) first. The transfer works in an unbound `single` review on the test save and needs `--allow-container-transfer` (a separate grant from input and fixture actions).

The reward mod writes its grant marker only after the whole stack was accepted into the backpack, so a failed or partial grant is never marked done and a completed one never repeats. Keep that order in your own mod; the restart check below depends on it.

## 1. Read the start state

Start a `single` review with `--test-save` and read the backpack: the reward must give exactly ten Stone, once. Place and open the chest with a test-only helper (the example's `inventory_reward_chest` and `inventory_reward_open` console commands); a helper that opens the menu directly needs no player positioning. Then read both sides:

```powershell
& $sdvkit project review call stardew_container_get '{}'
```

Require an empty 36-slot chest, an empty held item and `complete: true`. Count Stone as player/chest/held: `10/0/0`.

## 2. Transfer exact quantities

Pass the fresh identities from the read back unchanged. Take the slot numbers from the read, not from an assumption:

```powershell
& $sdvkit project review call stardew_container_transfer '{"direction":"deposit","sourceSlot":5,"quantity":3,"qualifiedItemId":"(O)390","selectionIdentity":"...","containerRevision":"...","instanceIdentity":"...","itemRevision":"..."}' --allow-container-transfer
```

The result's `data.outcome` must be `completed` with `observedQuantity` equal to the request. Read again after each transfer: a deposit of three gives `7/3/0`, a withdrawal of one from the chest slot gives `8/2/0`. Never replay a `partial` or `uncertain` result.

## 3. Prove that stale requests are refused

Send the first request again with its old identities. It must fail with `containerTransferSelectionStale`, and a fresh read must still show `8/2/0`.

## 4. Save, restart and read again

Close the chest with `stardew_input_press {"button":"Escape"}` (needs `--allow-input`) while nothing is held, call `stardew_fixture_save` (needs `--allow-fixture-actions`), and stop **without** reset. Start the same package again, open the chest and read both sides: `8/2/0` proves that the chest persisted and that the one-time reward did not repeat (that would show 18). Finish with stop and reset.

What a pass proves: the native transfer of ordinary stackable items in one owned single-player test world, and persistence of a placed chest through a fixture save. It does not prove other chest types, mod containers, multiplayer or normal saves.

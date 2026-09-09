# Bounded world interaction

Use `project review interact` or the opt-in MCP `stardew_world_interact` tool to
dispatch one native interaction against one freshly inspected adjacent target in
the exact owned disposable single-player review. This capability is never
available for a normal save, network role, split-screen role, or screen other
than screen 0.

First read the one-tile target with [`project review world`](world-inspection.md)
and read the complete backpack with [`project review inventory`](inventory-inspection.md).
Pass the soil identity for `water`, the crop identity for `harvest`, or the
machine `instanceId` and `revision`, plus the complete
`inventoryRevision`:

```powershell
& $sdvkit project review interact water 64 15 <soil-instance> <soil-revision> <inventory-revision> --topology single --json
```

Supported cases are deliberately closed:

- `water`: a live, dry ordinary crop/soil target, selected watering can, and
  remaining water; sends one native left-mouse/tool-use sample.
- `harvest`: a ready ordinary hand-harvest (`Grab`) crop, empty selected slot,
  no cursor-held item, and inventory capacity; scythe-required crops are rejected;
  sends one native right-mouse/action sample.
- `machineInsert`: an idle ordinary data-backed machine and a selected object
  accepted by the machine's native probe; sends one native right-mouse/action sample.
- `machineCollect`: a ready ordinary data-backed machine, empty selected slot,
  and inventory capacity; sends one native right-mouse/action sample.

Before the sample is queued and again at its actual input edge, SDVKit verifies
the exact launch and test save, screen 0, world/player readiness, no menu/event/
minigame/UI mode/tool use, faced adjacency, visible zoom- and UI-scale-aware
viewport-to-tile cursor mapping, target
instance and revision, full inventory revision, relevant selected item/tool,
resource, machine acceptance, and capacity. Any changed or unsupported fact
fails closed. The process-local virtual cursor is used; physical cursor movement,
focus changes, global input, pathfinding, and hidden navigation are outside this
contract.

The result separates `notDispatched`, `completed`, and `mayHaveRun`. Completed
means the native input lifecycle finished, not that watering, harvest, insertion,
or collection changed the world. Always capture the target and inventory again
and compare their fresh revisions, selected slot/tool/item, and slot facts. The
precondition rejects a cursor-held item, so successful before/after evidence must
also retain that absence rather than assuming a hidden output. Never blindly replay a canceled,
timed-out, or uncertain action.

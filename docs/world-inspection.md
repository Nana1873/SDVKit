# Inspect live crops, soil, and machines

Use `project review world` or native `stardew_world_area_get` to capture one
explicit rectangle in the current location of an exact, world-ready `single`
review. Both surfaces are read-only and return the same typed report.

```powershell
& $sdvkit project review world 60 12 8 8 --topology single --json
```

The four operands are `x y width height`. Width and height are each 1-32 tiles,
their product is at most 256, and the complete rectangle must be inside the
current map's loaded `Back` layer. Negative, overflowing, out-of-map, or larger
requests fail without returning an apparently complete area. A successful
report contains exactly `width * height` row-major tile entries, including
empty tiles; `complete: true` means geometric coverage is complete, not that
every object family or property is supported.

## Supported observations

Each tile has an optional `soil` value and one explicit `objectState`:
`missing`, `machine`, `unsupported`, or `unavailable`. A missing object is
therefore distinct from an unsupported placed object and from one whose safe
properties couldn't be read. Unsupported and unavailable entries may still be
part of a complete rectangle because they are reported rather than omitted.

Soil is a live `HoeDirt` terrain feature. `watered` uses
`HoeDirt.isWatered()`, `needsWatering` uses `HoeDirt.needsWatering()`, and the
optional fertilizer is its qualified object ID. Crop state is separately
`missing`, `available`, `unsupported`, or `unavailable`. The initial available
family is an ordinary non-forage crop with valid native seed and harvest item
IDs. Its phase, day within phase, phase count, full-grown/dead flags,
`HoeDirt.readyForHarvest()`, and `Crop.RegrowsAfterHarvest()` are returned
without predicting future growth. Farmed forage crops are explicit unsupported
families; malformed or unavailable native crop facts are explicit unavailable
properties. Static `Data/Crops` alone is never treated as current world state.

A supported machine is an exact `StardewValley.Object`, is a big craftable, and
has current `Data/Machines` behavior. Subclasses, chests, furniture, sprinklers,
ordinary placed items, and objects without that machine data are not silently
interpreted. The report preserves `readyForHarvest` and
`minutesUntilReady` from the live object. Its derived state is narrowly:

- `idle`: no output, not ready, and the native countdown is `-1` or `0`;
- `processing`: an output exists, it is not ready, and the countdown is positive;
- `ready`: an output exists, the native ready flag is true, and the countdown is
  `-1` or `0`.

Contradictory or out-of-range combinations are `unavailable`, not normalized.
Input uses `lastInputItem`; output uses `heldObject`. Both reuse the shared
bounded item facts (`qualifiedItemId`, positive `stack`, and object `quality`
when applicable). A missing item and unreadable item facts are distinct.

## Freshness and identity

The response is accepted only when its launch, topology, role, current-location
name, player ID, request ID, response time, target build, process, and final
review binding still match. It records `Game1.ticks` for the capture and a
process-local location instance ID. Soil, crop, and machine entries each have a
process-local `instanceId` plus a `revision` derived only from their exposed
state. Compare the instance first to detect replacement at the same tile, then
the revision to detect a state change. These values are comparison tokens for
the same review process, not durable object handles and not mutation authority.

Responses are create-new request files below the ignored review runtime, must
arrive within five seconds, and are limited to 192 KiB. Stale, malformed,
mismatched, oversized, re-bound, or incomplete responses fail closed. The
query does not traverse maps, open containers, enumerate NPCs, inspect arbitrary
fields or mutate the world. Stop and reset the owned review normally when the
test is complete.

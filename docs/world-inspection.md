# Inspect live crops, soil, and machines

Use `project review world` or the MCP tool `stardew_world_area_get` to read one rectangle of the current location. The location is the one the selected player, network [role](glossary.md#topology-roles-and-screens) or local screen is in, and the world is [world ready](glossary.md#worlds-and-fixtures). Both surfaces are read-only and return the same report. They grant no interaction rights.

```powershell
& $sdvkit project review world 60 12 8 8 --topology single --json
& $sdvkit project review world 60 12 8 8 --topology network-2 --role host --json
& $sdvkit project review world 60 12 8 8 --topology single --screen 1 --json
```

The four operands are `x y width height`. Width and height are each 1-32 tiles, and their product is at most 256. The whole rectangle lies inside the current map's loaded `Back` layer. Negative, overflowing, out-of-map or larger requests fail; they do not return a partial area that looks complete.

A successful report has exactly `width * height` tile entries in row-major order, including empty tiles. `complete: true` means the whole rectangle is covered. It does not mean every object type or property is supported.

## Supported observations

Each tile has an optional `soil` value and one `objectState`: `missing`, `machine`, `unsupported` or `unavailable`. So "no object" differs from "an unsupported object" and from "an object whose properties could not be read". Unsupported and unavailable entries still count toward a complete rectangle.

### Soil and crops

Soil is a live `HoeDirt` terrain feature. `watered` comes from `HoeDirt.isWatered()` and `needsWatering` from `HoeDirt.needsWatering()`. The optional fertilizer is its qualified object ID.

Crop state is `missing`, `available`, `unsupported` or `unavailable`. An available crop is an ordinary non-forage crop with valid seed and harvest item IDs. The report returns its phase, day within phase, phase count, full-grown and dead flags, `HoeDirt.readyForHarvest()` and `Crop.RegrowsAfterHarvest()`. It does not predict future growth. Farmed forage crops are `unsupported`. Malformed or unreadable crop facts are `unavailable`. Static `Data/Crops` alone does not count as current world state.

### Machines

A supported machine is a `StardewValley.Object` that is a big craftable and has current `Data/Machines` behavior. Subclasses, chests, furniture, sprinklers, ordinary placed items and objects without machine data are not interpreted. The report keeps `readyForHarvest` and `minutesUntilReady` from the live object. The derived state is:

- `idle`: no output, not ready, and the countdown is `-1` or `0`.
- `processing`: an output exists, it is not ready, and the countdown is positive.
- `ready`: an output exists, the ready flag is true, and the countdown is `-1` or `0`.

Contradictory or out-of-range combinations are `unavailable`; SDVKit does not normalize them.

Input shows `lastInputItem`. This is the last input for which the machine produced output. It can remain while the machine is idle, so it is not the item being processed now. Output shows `heldObject`. Both use the shared item facts: `qualifiedItemId`, positive `stack` and object `quality` where it applies. A missing item differs from unreadable item facts.

## Freshness and identity

The report records `Game1.ticks` for the capture and a process-local location instance ID. Soil, crop and machine entries each have a process-local `instanceId` and a `revision` over their exposed state. Compare the instance first to detect a replaced object on the same tile. Then compare the revision to detect a state change. These tokens are valid only within the same review process. They are not durable handles and do not authorize changes.

The query does not walk maps, open containers, list NPCs, read arbitrary fields or change the world. Stop and reset the review normally when you finish.

## Reference details

SDVKit accepts a response only when its launch, topology, role, location name, player ID, request ID, response time, target build, process and final review binding still match. Each response is a new request-bound file below the ignored review runtime. Responses have five seconds to arrive and are limited to 192 KiB. Stale, malformed, mismatched, oversized, re-bound or incomplete responses fail closed.

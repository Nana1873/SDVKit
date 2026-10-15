# Inspect live world tiles

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

### Terrain features and furniture

The optional per-tile `feature` is `null` when no supported feature covers the tile, or an object with `kind`, `id` and `state`. Older GameSide responses may omit `feature`, which the CLI and MCP can reserialize as `null`. Neither absence nor `null` alone proves feature support: use a known updated GameSide build before treating `null` as removal evidence. The existing soil and object fields remain independent.

| `kind` | `id` | Required `state` fields |
| --- | --- | --- |
| `tree` | Native `Tree.treeType`, a `Data/WildTrees` key | `growthStage`, `stump`, `health` |
| `fruitTree` | Native `FruitTree.treeId`, a `Data/FruitTrees` key | `growthStage`, `stump`, `health` |
| `grass` | Native `Grass.grassType`, as a decimal string | `numberOfWeeds` (remaining blades) |
| `bush` | Native `Bush.size` (`0`–`4`), as a decimal string | `health` (local to this client: `Bush.health` is not synchronized in multiplayer), `originX`, `originY` |
| `resourceClump` | Native `ResourceClump.parentSheetIndex`, as a decimal string, e.g. `672` for a boulder; this is not a qualified item ID | `health`, `originX`, `originY` |
| `giantCrop` | Native `GiantCrop.Id`, a `Data/GiantCrops` key (giant crops are clumps whose sprite index is meaningless) | `health`, `originX`, `originY` |
| `unavailable` | The family whose facts could not be read: `tree`, `fruitTree`, `grass`, `bush`, `resourceClump`, `giantCrop`, `furniture` or `unknown` | none (empty object) |
| `furniture` | Qualified furniture item ID, e.g. `(F)1120` | `originX`, `originY` |

IDs have at most 64 characters and no control characters, which keeps a full 16x16 read within the response limit. Health is finite and between -100 and 1,000,000; the negative range preserves the game's removal sentinel. Tree growth is 0–1,000,000 (vanilla moss growth can advance it beyond the mature stage), fruit-tree growth is 0–4, grass type is 0–255 and blade count is 0–4. Origins are integer tile coordinates between -100,000 and 100,000, at or above and to the left of the covered tile. GameSide never sends a feature outside these limits: when a feature on a tile cannot be read or its facts fall outside them (for example a modded ID longer than 64 characters), only that tile reports `kind: unavailable` with the family, and the rest of the read, including world actions that use it, is unaffected. On the receiving side, unexpected kinds, missing or extra state fields, and invalid values reject the response instead of being normalized.

Clumps, furniture and large bushes repeat the same feature on every tile of their footprint within the requested rectangle, with the original top-left tile even when it lies outside the rectangle. Coverage comes from the game's own lookups: `ResourceClump.occupiesTile`, `GameLocation.GetFurnitureAt` and `GameLocation.getLargeTerrainFeatureAt`, so furniture uses its placement footprint, not the sprite's visual extent. Trees and grass describe their terrain tile, not their canopy or swing area. Only furniture in `location.furniture` is included; items held on tables or in containers are not separate placed furniture.

There is one feature slot per tile, filled in this order: resource clump or giant crop, furniture, large bush, single-tile terrain feature. A lower-priority feature on the same tile is hidden. For furniture, `GetFurnitureAt` prefers a solid piece over a passable one, so a table on a rug reports the table and hides the rug. Other terrain families are not interpreted. Feature facts have no instance or revision token; compare a fresh read at the same location and tile after the animation finishes, without treating a matching ID as proof of object identity.

These observations allow an axe test to compare tree health or `stump`, a scythe test to compare grass blades or `feature: null`, and a pickaxe test to compare a boulder's health or removal across its covered tiles. Verified live once on the test save's Farm: an axe hit lowered a tree's health from 10 to 9, a scythe cut grass from 4 to 1 blades, and a 2x2 stump was reported on all four tiles with its origin. Fruit trees, giant crops, furniture, a felled tree and a broken boulder are not yet verified live. Input delivery alone still does not prove any of these effects.

### Soil and crops

Soil is a live `HoeDirt` terrain feature. `watered` comes from `HoeDirt.isWatered()` and `needsWatering` from `HoeDirt.needsWatering()`. The optional fertilizer is its qualified object ID.

Crop state is `missing`, `available`, `unsupported` or `unavailable`. An available crop is an ordinary non-forage crop with valid seed and harvest item IDs. The report returns its phase, day within phase, phase count, full-grown and dead flags, `HoeDirt.readyForHarvest()` and `Crop.RegrowsAfterHarvest()`. It does not predict future growth. Farmed forage crops are `unsupported`. Malformed or unreadable crop facts are `unavailable`. Static `Data/Crops` alone does not count as current world state.

### Machines

A supported machine is a `StardewValley.Object` that is a big craftable and has current `Data/Machines` behavior. Subclasses, chests, furniture, sprinklers, ordinary placed items and objects without machine data are not interpreted as machines. Placed furniture is observed separately through `feature`. The report keeps `readyForHarvest` and `minutesUntilReady` from the live object. The derived state is:

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

The byte limit still applies to all fields together. Dense 16x16 tree/grass and machine captures fit with ordinary item facts; unusually long or heavily escaped IDs combined with machine or crop data can exceed it even when each field is valid. GameSide then returns `worldResponseLimit`, without a partial rectangle. Request a smaller area for that data.

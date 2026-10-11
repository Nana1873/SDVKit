# Read world and local-player state

Use `project review status --json` or the MCP tool `stardew_runtime_get {}` in an active [review](live-review.md). Both read GameSide's regular status. You need no console command, no opt-in and no direct game-object access.

For example, a reward mod test can compare money and the selected item's stack before and after its feature runs. Before you compare values, confirm the target and companions, the launch, the role and the observation time.

## Availability and compatibility

The runtime envelope keeps `schemaVersion: 1`. Its optional `localPlayer` member has its own `schemaVersion: 1`, plus `availability`, `reason` and `data`. Existing runtime fields keep their names and types. Output from older producers without the member stays readable; the reader reports `unavailable/notPublished` with null data. A consumer with a closed old output schema needs the new MCP schema before it accepts this member.

| Availability | Meaning |
| --- | --- |
| `available` | All selected values were captured and validated; `reason` is null. |
| `worldNotReady` | No ready world; `reason` and `data` are null. |
| `unavailable` | `notPublished` for an older producer; `selectionUnavailable` if the current slot cannot be read from the local inventory. No data. |
| `unsupportedVersion` | An unknown slice schema version with a readable envelope; `reason=unsupportedSchema`, no data. An incompatible JSON shape instead fails validation. |
| `error` | `captureFailed` for a game/mod getter exception, or `invalidValues` for values outside this contract. No partial or last-known data. |

These make the whole runtime invalid: a malformed payload, a missing required field, an invalid world or value combination, or a stale inner timestamp. In CLI status, a stale, exiting or restore-failed outer marker withholds **all** runtime values; it no longer keeps an inner `ready` view. MCP requires an active, fresh binding and returns an error without stale data. This also applies to older producers.

### Timing

GameSide captures the values on the game's main thread, normally once per second. It copies plain values and keeps no `Farmer`, `Item` or location objects. Returning to the title publishes the world-not-ready state at once. Until that arrives, a reader can see the last sample: this is a snapshot, not a synchronous call into the game. The inner `observedAtUtc` is valid for five seconds and may be at most one second in the future. Outer freshness, process and start identity, target, fixture and launch validation still apply.

### Roles

Single returns its local farmer. In `network-2`, the host and farmhand readers each return only their own farmer, and the player ID matches the role's verified network identity. Returning to the title or disconnecting a network role can break the joined-pair check; MCP then returns the binding error. There is no peer inventory and no other multiplayer topology.

### Versions

The values come from public members of the supported game build. SDVKit does not guess missing fields through reflection or substitute zero for an unavailable member. The test-save version gate still applies: Stardew 1.6.15 to <1.7 and SMAPI 4.5 to <5, including the file-version check. A slice schema version does not claim that another game version was tested.

## Fishing observations

The optional `localPlayer.data.fishing` object describes the selected `FishingRod`, also when the rod is idle. It is null when no rod is selected; older producers may leave it out. When present, all its fields are present. CLI status and `stardew_runtime_get` show the same data.

| Field | Meaning |
| --- | --- |
| `timingCast`, `casting`, `bobberInAir` | Current rod cast flags. |
| `fishing`, `nibbling`, `hit`, `pullingOut`, `catchReady` | Direct rod flags; they may overlap and are not a derived phase machine. |
| `fromFishPond` | The rod's fish-pond flag. |
| `bobberTileX`, `bobberTileY` | Finite bobber pixel position divided by 64; meaningful during the cast, not a promised valid fishing tile while idle. |
| `biteMilliseconds` | Current finite rod bite timer; zero/negative transitional values are retained. |
| `catchItemId`, `catchQuantity` | Qualified caught item and nonnegative quantity while `catchReady`; otherwise null and zero. A ready catch does not prove inventory pickup. |

These values follow the same role, timestamp and freshness rules. SDVKit does not hook, reroll, finish or collect a catch. The one-second publication can miss short events, especially the nibble window, so polling cannot give a full catch history. Fishing overhauls that replace the vanilla rod code may leave these fields idle or change their meaning; use their own APIs for such tests. Mod-specific counters, seeded probability checks and third-party internals belong to your mod test.

Use [fishing preparation](lab-reference.md#fishing-preparation) to place the main player of a [test save](glossary.md#worlds-and-fixtures) without a helper mod.

## Field contract

All `data` values describe **one observation** at the runtime's `observedAtUtc`. They can change on the next game tick. None proves persistence after saving.

| Field | Source and JSON type | Identity, bounds and null meaning |
| --- | --- | --- |
| `playerId` | `Game1.player.UniqueMultiplayerID`, decimal string | Nonzero signed 64-bit ID, at most 20 characters; stable for that farmer within a save, not a global account ID. |
| `money` | `Farmer.Money`, integer | Local farmer accessor; reflects the game's shared/separate-wallet rules. Signed 32-bit value, no vanilla cap. |
| `health`, `maxHealth` | `Farmer.health`, `Farmer.maxHealth`, integers | Signed 32-bit values; modified/negative values are retained, not clamped. |
| `stamina`, `maxStamina` | `Farmer.Stamina`, `Farmer.MaxStamina`, numbers | Finite single-precision values, including negative stamina and modded maxima; NaN/infinity are errors. |
| `selectedSlot` | `Farmer.CurrentToolIndex`, integer or null | Zero-based inventory position read against `Farmer.Items.Count`; nonnegative signed 32-bit index. The game's `-1` means no selection and is represented by null. Not a persistent item ID. |
| `selectedItem` | `Farmer.Items[selectedSlot]`, object or null | At most one item. Null for an empty slot or no selection. No collection traversal. |
| `selectedItem.qualifiedItemId` | `Item.QualifiedItemId`, string | Qualified type key `(type)id`, at most 256 characters without control characters. Identifies an item type, not a unique item instance. |
| `selectedItem.stack` | `Item.Stack`, integer | Signed 32-bit property value; no vanilla stack cap. |
| `selectedItem.quality` | `StardewValley.Object.Quality`, integer or null | Signed 32-bit value for Object-derived items; null for other item types, not an invented zero. |
| `position` | Object or absent | Absent from older GameSide versions. Null when the game reports no valid facing direction. |
| `position.facing` | `Farmer.FacingDirection`, string | `up`, `right`, `down` or `left`. |
| `position.standingX`, `position.standingY` | `Farmer.StandingPixel` divided by 64, numbers | Finite, within ±100000; their floor is the tile. Use them to see where inside a tile the player stands. |
| `position.grabTileX`, `position.grabTileY` | `Farmer.GetGrabTile()`, integers | The tile the action button checks first. Depending on where inside its tile the player stands, it can be the player's own tile. If nothing is there, the game tries the tiles directly below and above it (unless behind the player), then the player's own tile; it never tries the next tile further in the facing direction. |
| `position.toolTileX`, `position.toolTileY` | `Farmer.GetToolLocation(ignoreClick: true)` divided by 64, integers | The tile a tool used now would hit. |
| `npcs` | Array or absent | Villagers (`NPC.IsVillager`; no pets, horses or monsters) in the player's current location, sorted by `name`, at most 64. Absent from older GameSide versions; empty when none is there. A modded NPC with an unusable name is left out. Sampled about once per second, so a walking villager can already be elsewhere. |
| `npcs[].name` | `NPC.Name`, string | The internal name, as used by `stardew_fixture_npc_place` and `stardew_progression_get`. |
| `npcs[].displayName` | `NPC.displayName`, string | The localized name shown in game. |
| `npcs[].tileX`, `npcs[].tileY` | `NPC.TilePoint`, integers | Within ±100000. Target this tile with `stardew_world_act` `check` from a neighbouring tile to talk or give the selected item. |
| `npcs[].facing` | `NPC.FacingDirection`, string | `up`, `right`, `down` or `left`. |

The mouse cursor redirects the action and tool buttons when it is within one tile of the player; then neither tile above applies. Park SDVKit's virtual cursor away from the player (for example `stardew_input_cursor_set` at `8,8`) before keyboard actions.

The world fields stay available next to the player fields:

| Field | Source/type | Validation and identity |
| --- | --- | --- |
| `worldReady` | SMAPI `Context.IsWorldReady`, boolean | Always present in a valid runtime sample. |
| `season`, `dayOfMonth`, `year`, `timeOfDay` | `Game1.currentSeason/dayOfMonth/year/timeOfDay`, string/integers | Four season tokens, day 1–28, year >=1, time 0–2999. Null without a ready world. |
| `locationId` | `Farmer.currentLocation.NameOrUniqueName`, string | Nonempty, <=256 characters; the current location key, not a localized display name or stable cross-save building ID. Null without a world. |
| `tileX`, `tileY` | `Farmer.TilePoint`, integers | -100000–100000; role-local position, null without a world. |
| `menuOpen` | `Game1.activeClickableMenu is not null`, boolean | Coarse presence only; no menu CLR type or tree. |

The payload has a fixed shape, one item ID and no arrays. It fits within the **256 KiB total status limit** and adds no paging. Missing or malformed fields do not turn into zero values that look available.

## Scope

- In this status: world, time and location fields and the local-player fields above.
- Not in this status: full inventories, item instance identities, quests, relationships, skills and progression, and other players.
- Not included at all: names, arbitrary `modData`, private account data, paths and object dumps.

This is a mod-test observation, not save inspection or a way to change gameplay. For full backpacks use [inventory inspection](inventory-inspection.md). NPC friendship, mail flags and quest-log counts come from the separate [progression query](progression-authoring.md#read-only-contract), in a single review on the test save.

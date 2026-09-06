# Read world and local-player state

Use `project review status --json` or native `stardew_runtime_get {}` from the
[owned active review](live-review.md). Both read the existing AlwaysOn status;
no console command, input opt-in or arbitrary game-object access is needed.
For example, a reward mod test can compare money and the selected item's stack
before and after its own feature runs. Confirm the exact target/companion,
launch, role and observation time before comparing values.

## Availability and compatibility

The runtime envelope retains `schemaVersion: 1`. Its optional additive
`localPlayer` member has `schemaVersion: 1`, `availability`, `reason` and `data`.
Existing runtime fields keep their names and types. Older producers without
the member remain readable; the new reader reports `unavailable/notPublished`
with null data. Consumers with a closed old output schema must adopt the
advertised additive MCP schema before accepting the new member.

| Availability | Meaning |
| --- | --- |
| `available` | All selected values were captured and validated; `reason` is null. |
| `worldNotReady` | No ready world; `reason` and `data` are null. |
| `unavailable` | `notPublished` for an older producer; `selectionUnavailable` if the current slot cannot be read from the local inventory. No data. |
| `unsupportedVersion` | An unknown slice schema version with a readable envelope; `reason=unsupportedSchema`, no data. An incompatible JSON shape instead fails validation. |
| `error` | `captureFailed` for a game/mod getter exception, or `invalidValues` for values outside this contract. No partial or last-known data. |

A malformed known payload, missing required field, invalid world/value
combination or stale inner timestamp makes the runtime invalid. A stale,
exiting or restore-failed outer marker now also withholds **all** runtime values
in CLI status instead of retaining an inner `ready` projection. MCP already
requires an active fresh binding and returns an error without a stale payload.
This tightening applies to old producers too.

Capture uses supported game members on the existing Stardew main-thread status
path, normally once per second. It copies scalars, retaining no `Farmer`, `Item`
or location objects. `ReturnedToTitle` immediately republishes world-not-ready
state; the next normal tick continues that state. Readers may observe the last
published sample until the transition publication arrives; this is a snapshot,
not synchronous game execution. The inner `observedAtUtc` shares the existing
five-second freshness limit and one-second future tolerance. Outer freshness,
process/start identity, target, fixture and launch validation still apply.

Single returns its local farmer; fixed network-2 host and farmhand readers each
return only their own farmer. The player ID must also match the role's verified
network identity. Returning to title/disconnecting a network role can invalidate
the joined-pair gate, in which case MCP returns the existing binding error.
There is no peer inventory or additional multiplayer topology.

This uses the public members available in the supported game-bound build; it
does not infer missing fields through reflection or substitute zero for an
unavailable member. The existing disposable-world version/capability gate
remains in force (Stardew 1.6.15–<1.7 and SMAPI 4.5–<5, including its file-version
check). A slice schema version is not a declaration that another game version
has been tested.

## Field contract

All `data` values describe **one observation**, at runtime `observedAtUtc`.
They may change on the next game tick; none proves persistence after saving.

| Field | Source and JSON type | Identity, bounds and null meaning |
| --- | --- | --- |
| `playerId` | `Game1.player.UniqueMultiplayerID`, decimal string | Canonical nonzero signed 64-bit ID, at most 20 characters; stable for that farmer within a save, not a global account ID. |
| `money` | `Farmer.Money`, integer | Local farmer accessor; reflects the game's shared/separate-wallet rules. Signed 32-bit value, no vanilla cap. |
| `health`, `maxHealth` | `Farmer.health`, `Farmer.maxHealth`, integers | Signed 32-bit values; modified/negative values are retained, not clamped. |
| `stamina`, `maxStamina` | `Farmer.Stamina`, `Farmer.MaxStamina`, numbers | Finite single-precision values, including negative stamina and modded maxima; NaN/infinity are errors. |
| `selectedSlot` | `Farmer.CurrentToolIndex`, integer or null | Zero-based inventory position read against `Farmer.Items.Count`; nonnegative signed 32-bit index. The game's `-1` means no selection and is represented by null. Not a persistent item ID. |
| `selectedItem` | `Farmer.Items[selectedSlot]`, object or null | At most one item. Null for an empty slot or no selection. No collection traversal. |
| `selectedItem.qualifiedItemId` | `Item.QualifiedItemId`, string | Qualified type key `(type)id`, at most 256 characters without control characters. Identifies an item type, not a unique item instance. |
| `selectedItem.stack` | `Item.Stack`, integer | Exact signed 32-bit property value; no vanilla stack cap. |
| `selectedItem.quality` | `StardewValley.Object.Quality`, integer or null | Exact signed 32-bit value for Object-derived items; null for other item types, not an invented zero. |

The pre-existing world fields remain available alongside the new slice:

| Field | Source/type | Existing validation/identity |
| --- | --- | --- |
| `worldReady` | SMAPI `Context.IsWorldReady`, boolean | Always present in a valid runtime sample. |
| `season`, `dayOfMonth`, `year`, `timeOfDay` | `Game1.currentSeason/dayOfMonth/year/timeOfDay`, string/integers | Four season tokens, day 1–28, year >=1, time 0–2999. Null without a ready world. |
| `locationId` | `Farmer.currentLocation.NameOrUniqueName`, string | Nonempty, <=256 characters; exact current location key, not a localized display name or stable cross-save building ID. Null without a world. |
| `tileX`, `tileY` | `Farmer.TilePoint`, integers | -100000–100000; role-local position, null without a world. |
| `menuOpen` | `Game1.activeClickableMenu is not null`, boolean | Coarse presence only; no menu CLR type or tree. |

The payload has constant shape, one bounded item ID and no arrays. It stays
within the existing **256 KiB total status limit**; this change adds no pagination,
mailbox or transport. Missing or malformed fields are never defaulted into
apparently available zero values.

Supported in this slice: existing world/time/location and the local-player
fields above. Deferred: complete inventories, item instance identities, quests,
relationships, skill/progression state and other players. Excluded: names,
arbitrary `modData`, private account data, paths and object dumps. This is a
bounded mod-test observation, not universal save inspection or gameplay mutation.

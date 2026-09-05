# Inspect a selected save offline

From the consuming project's directory, select exactly one save payload file (the file inside a save slot, not `SaveGameInfo`), or that project's already registered single-player fixture:

```powershell
& $sdvkit save sections --json
& $sdvkit save inspect --source '<explicit-save-file>' --json
& $sdvkit save inspect --fixture baseline --json
& $sdvkit save inspect --fixture work --json
```

There is no automatic discovery or import of normal Saves. Selection authorizes source metadata and byte reads only. Inspection creates a fresh `.sdvkit/save-inspection/<id>/save.xml` below the current project, verifies byte identity, and parses only that copy. An exclusive read-sharing source handle refuses concurrent writers and replacement while copying. Changes after the copy do not change its snapshot; SHA-256 and byte length identify the inspected bytes, not the current source. The result includes only the generated relative copy path, never the source path, player name or save-slot name. Keep `.sdvkit/` ignored.

`--fixture` additionally verifies the existing registered fixture and payload marker, then checks the copied save's game ID and player fixture/owner markers. It neither creates a fixture nor launches, stops, resets or mounts the lab. Use a stopped fixture for a stable completed save; a file being saved can be unavailable. Explicit source selection does not imply SDVKit fixture ownership.

## Supported fields

Only Stardew `gameVersion` 1.6.x and the `SaveGame` root are supported. `schema` names the fixed known-field adapter; it does not mean whole-save or modded-schema validation. Missing numeric fields are `null`. Missing Farm and collection availability is explicit; an available empty collection has count zero. Invalid known numeric values fail instead of becoming zero.

| Section | Fields |
| --- | --- |
| Main player | `money`, `health`, `maxHealth`, `stamina`, `maxStamina`, `farmingLevel`, `miningLevel`, `combatLevel`, `foragingLevel`, `fishingLevel` |
| World | `year`, `dayOfMonth`, `whichFarm`, `currentSeason` (reported as `season`) |
| Exact location `Farm`, buildings | `buildingType` (reported as `type`), `tileX`/`tileY` (reported as `x`/`y`) |
| Exact location `Farm`, objects | Dictionary tile `X`/`Y`, `itemId`, `stack` |

Each collection returns its first 100 records sorted numerically by X then Y, plus its full count. The Farm tile pair identifies a record within that collection and snapshot; moving it changes the identity. Duplicate tiles or known scalar fields are rejected. No arbitrary record selector, object traversal, inventory, farmhand, animal, modData dump, or position interpretation is provided. Missing fields are availability limits, not proof of default gameplay values.

Reports are deterministic for the same copied bytes except the fresh generated copy path. Bounds are 32 MiB per file/XML character budget, 64 XML levels, one million reader nodes, 10,000 records per Farm collection, 128 characters per known scalar, and 100 returned records per collection. DTDs/external entities, unsupported versions, malformed data, ambiguous known records and exceeded limits fail closed. Unknown sections are not traversed for reporting. The XML adapter reads saved facts; it does not instantiate game objects or establish that arbitrary modded saves will load.

## Failure and retention

Exit 2 means incorrect syntax. Exit 3 returns a controlled problem such as `unsupportedVersion`, `versionUnavailable`, `schemaUnavailable`, `malformedXml`, `sizeLimit`, `xmlLimit`, `recordLimit`, `fixtureMismatch`, or `saveUnavailable`. Select a supported plain local save and address the reported limit; the tool never repairs or migrates it. Linked files (including hard links), reparse ancestors, UNC/device paths, alternate streams and traversal selectors are rejected. Filesystem exceptions omit personal paths.

Successful and failed copies are retained below `.sdvkit/save-inspection/` for diagnosis, including partial preparation directories. SDVKit does not write them again, provide a save cleanup command, or delete source files. After reviewing results, you may remove only the generated inspection directory. Review reset remains the separate live-lab operation and is not required for offline inspection.

Native MCP is deliberately deferred: this is an offline CLI workflow with no active-review dependency. Save editing, repair, migrations and cloud handling are outside the contract. Build/tests and parser results are separate from independently observed in-game evidence.

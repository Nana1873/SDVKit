# Inspect a selected save offline

Run these from the project's folder. Select one save payload file (the file inside a save slot, not `SaveGameInfo`), or the project's registered single-player fixture:

```powershell
& $sdvkit save sections --json
& $sdvkit save inspect --source '<save-file>' --json
& $sdvkit save inspect --fixture baseline --json
& $sdvkit save inspect --fixture work --json
```

SDVKit never finds or imports your normal saves by itself. Selecting a file lets SDVKit read its metadata and bytes, nothing more. Inspection copies the file to a fresh `.sdvkit/save-inspection/<id>/save.xml` below the current project and parses only that copy. Later changes to the source do not change the copy. SHA-256 and byte length identify the inspected bytes, not the current source. The result shows only the generated relative copy path. It never shows the source path, player name or save-slot name. Keep `.sdvkit/` ignored.

`--fixture` also checks that the copy belongs to the registered fixture: the fixture and payload markers, the game ID and the player markers. It does not create a fixture, and it does not launch, stop, reset or mount the lab. Inspect a stopped fixture for a stable, completed save; a file in the middle of saving can be unavailable. Selecting a file with `--source` does not make it an SDVKit fixture.

## Supported fields

Only Stardew `gameVersion` 1.6.x with the `SaveGame` root is supported. `schema` names the fixed adapter for known fields; it does not validate the whole save or modded parts.

| Section | Fields |
| --- | --- |
| Main player | `money`, `health`, `maxHealth`, `stamina`, `maxStamina`, `farmingLevel`, `miningLevel`, `combatLevel`, `foragingLevel`, `fishingLevel` |
| World | `year`, `dayOfMonth`, `whichFarm`, `currentSeason` (reported as `season`) |
| Location `Farm`, buildings | `buildingType` (reported as `type`), `tileX`/`tileY` (reported as `x`/`y`) |
| Location `Farm`, objects | Dictionary tile `X`/`Y`, `itemId`, `stack` |

- A missing numeric field is `null`. A missing value is an availability limit, not proof of a default gameplay value.
- The report states whether the Farm and each collection are available. An available empty collection has count zero.
- An invalid known number fails; it does not become zero. SDVKit imposes no vanilla gameplay caps.
- Each collection returns its first 100 records, sorted numerically by X then Y, plus its full count.
- The Farm tile pair identifies a record within that collection and snapshot. Moving the record changes its identity.
- Duplicate tiles or duplicate known fields are rejected.

There is no selector for arbitrary records, and no object traversal, inventory, farmhands, animals, `modData` dump or position interpretation. The adapter reads saved facts. It does not create game objects or prove that a modded save will load.

## Failure and retention

Exit 2 means incorrect syntax. Exit 3 returns a controlled problem such as `unsupportedVersion`, `versionUnavailable`, `schemaUnavailable`, `malformedXml`, `sizeLimit`, `xmlLimit`, `recordLimit`, `fixtureMismatch` or `saveUnavailable`. Select a supported plain local save and fix the reported limit; SDVKit does not repair or migrate a save. Linked files (including hard links), reparse-point parent folders, UNC and device paths, alternate streams and traversal selectors are rejected. Error messages leave out personal paths.

SDVKit keeps successful and failed copies below `.sdvkit/save-inspection/` for diagnosis, including partial preparation folders. It does not rewrite them, has no cleanup command and does not delete source files. After you review the results, you may delete only the generated inspection folder. Review reset is a separate live-lab operation and is not needed for offline inspection.

This is an offline CLI workflow with no MCP tool and no need for an active review. Save editing, repair, migration and cloud saves are out of scope. Build, tests and parser results are separate from evidence observed in the game.

## Reference details

- Inspection verifies that the copy is byte-identical to the source. While copying, SDVKit holds the source open with read-only sharing, so other writers and replacement are refused.
- Reports are deterministic for the same copied bytes, apart from the fresh copy path.
- `stamina` uses finite XML Single values, including scientific notation and the full finite Single range. All other player and world numbers use XML Int32. NaN and infinities are rejected. Object dictionary coordinates use XML Single with whole Int32 tile values.
- Size limits: 32 MiB per file and per XML character budget, 64 XML levels and one million reader nodes.
- Record limits: 10,000 records per Farm collection, 128 characters per known value and 100 returned records per collection.
- DTDs and external entities, unsupported versions, malformed data, ambiguous known records and exceeded limits fail closed. Unknown sections are not read for the report.

# Test a save-data migration

Use this pattern when a new version of your mod has to read data that an older version saved with `ReadSaveData`/`WriteSaveData`. The complete worked example is [`scripts/acceptance/data-migration.ps1`](../scripts/acceptance/data-migration.ps1). It reviews three builds of the sample in [`docs/recipes/data-migration`](recipes/data-migration/) (v1 `1.0.0`, a deliberately broken v2 `2.0.0-beta.1`, the corrected v2 `2.0.0`) against the same retained test save. The sample's schema, contract and behaviour table are in [the data migration recipe](data-migration-authoring.md).

[`scripts/prepare-data-migration-recipe.ps1`](../scripts/prepare-data-migration-recipe.ps1) builds and packages the three variants below the lab's `.sdvkit/`; it needs the lab root as the current directory and a fresh output folder.

## 1. Save the old data

Start the v1 build with `--test-save` and log the mod's own status (the sample's `migration-recipe status` console command). Require the exact values, for example `artifact=1.0.0; schema=1; credits=37; crop=(O)634; claimed=True; stone=5`. Save with `stardew_fixture_save` (`--allow-fixture-actions`), require `state=ready` and stop **without** `reset`: the test save and its mod data are what the next version reads.

## 2. Diagnose the broken upgrade

Start the broken v2 against the same save. It must report its refusal in the SMAPI log (`Broken v2 cannot migrate schema 1; original data was retained.`) and still show schema 1 with the original values and the same Stone. A refusal that keeps the old data is the right failure mode; do not reset the save to make the build pass.

## 3. Prove the migration and the restart

Stop, start the corrected v2 and require the migration log line and the new shape: `schema=2; balance=37; crop=(O)634; claimed=True; migrations=1; stone=5`, so the old values moved and no second reward was granted. Save, stop without reset and start the same build again. The log must say that the existing v2 ledger was loaded, with no new migration, and the status must be unchanged. Finish with stop and reset.

## Not covered by the script

The sample also has fixture operations for a missing ledger and a future schema 99 (`migration-recipe fixture missing`, `fixture future`). They are manual steps for the recipe, not part of the script: after sending one, save through the fixture tool, stop, restart the corrected v2 and read the status. Missing data must give explicit new defaults; schema 99 must be refused with the future values preserved. The offline tests cover the mapping and its rejection cases. A pass does not prove arbitrary schema recovery, multiplayer, real saves or crash-safe reward transactions.

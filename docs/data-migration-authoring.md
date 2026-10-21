# Upgrade original mod-owned save data

Build **Data Migration Recipe** as three original SMAPI code artifacts: v1
`1.0.0`, deliberately broken v2 `2.0.0-beta.1`, and corrected v2 `2.0.0`.
Keep their mod ID `SDVKit.DataMigrationRecipe` and data key `ledger` identical.
The artifact version and persisted `SchemaVersion` are separate: broken v2 must
still observe schema 1. This is a real stopped-process code upgrade, not a
configuration toggle or a generic migration service.

The bounded recipe targets Stardew Valley 1.6.15 / SMAPI 4.5.2 and one owned
single-player disposable fixture. Use the [toolkit](toolkit.md),
[ready-code review](live-review.md#start-a-review),
[persistence lifecycle](live-review.md#finish-or-test-persistence), and
[opt-in fixture save](mcp.md#opt-in-fixture-actions). Normal saves and Mods stay
outside this workflow.

## Source and API

The complete reusable sources are [Ledger.cs](recipes/data-migration/Ledger.cs)
and [ModEntry.cs](recipes/data-migration/ModEntry.cs). The
[preparation script](../scripts/prepare-data-migration-recipe.ps1) copies them to
three fresh generated mod projects below `.sdvkit/`, selects compile-time code
branches, checks and packages each project, freezes ZIPs, extracts ready mods,
and records SHA-256 identities. It never launches the game or automatically
deploys to normal Mods. Existing recipe output is rejected rather than replaced.

The [SMAPI 4.5.2 data helper](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Framework/ModHelpers/DataHelper.cs)
implements supported `ReadSaveData<T>` / `WriteSaveData<T>` in the save's
`CustomData`, namespaced by mod ID and key. `WriteSaveData` changes loaded save
data; it does not save the game. This recipe reads a nullable schema header
first and writes on `SaveLoaded`; the subsequent completed game save persists
it. It does not depend on the fixture-save path raising `GameLoop.Saved`.
Save-data APIs are host-computer APIs; this recipe deliberately excludes
multiplayer. Changing the unique mod ID would select a different data namespace.

The sample-specific guard requires the exact active SDVKit disposable save,
owner, fixture, farmer, and launch environment before reads or mutation. The two
`fixture` console operations modify only this mod's `ledger` key and are test
setup, not migration or product commands.

| Loaded data | Corrected v2 behavior | Selected observation |
| --- | --- | --- |
| v1 | Validate, then replace with v2 | `Credits=37` becomes `Balance=37`; `(O)634` crop becomes nested preference; claimed welcome reward stays true; migration count 1 |
| v2 | Read without rewriting or granting | Same balance, crop, claimed marker, migration count, and backpack |
| Missing | Create new v2 defaults | Balance 0, crop `(O)24`, claimed false, migration count 0; no welcome reward |
| Future schema 99 | Refuse; do not deserialize through a v2 model or write defaults | Balance 73 and `FutureOnly=preserve-this-value` remain on repeated restart |
| Invalid v1 | Refuse before replacement | Negative credits, missing crop, or wrong schema do not mutate the old model; covered offline |

v1 grants five Stone once when it creates the selected ledger. Neither v2
artifact grants a reward. Compare backpack Stone before and after upgrade and
repeated loads, alongside the carried claimed marker. That proves no duplicate
welcome reward in this recipe. It does not establish atomic transactions across
inventory insertion, JSON serialization failures, and a crashed game.

## Missing and future data (manual)

The live migration steps are in [Test a save-data migration](test-a-data-migration.md), run by
[`data-migration.ps1`](../scripts/acceptance/data-migration.ps1). These two cases are not part of that script.

In corrected v2, send `migration-recipe fixture missing`. Require its status to
report schema missing, save with `stardew_fixture_save`, stop without reset and
start the same v2. Require explicit new defaults from the table and unchanged
Stone. Save and restart those defaults once more; require unchanged values and
count 0.

Next send `migration-recipe fixture future`. Require schema 99, balance 73 and
`futureOnly=preserve-this-value`. Save, stop and restart v2; require the
attributed `Unsupported schema 99` error, unchanged future values and unchanged
inventory. Save and restart once more to prove the refusal did not rewrite unknown
fields to v2 defaults. These fixture operations intentionally replace the
current recipe ledger; do not use them as a migration strategy.

Offline tests validate the mapping and rejection boundaries; the game-bound build
proves compilation against the selected APIs. This does not prove arbitrary schema
recovery, multiplayer, real saves, or crash-safe reward transactions.

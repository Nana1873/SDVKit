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

## Prepare immutable artifacts offline

Select one exact SDVKit commit and portable ZIP using the
[portable procedure](releasing.md#verify-the-extracted-package). Build the
included AlwaysOn source against the selected game. Record the commit, ZIP hash,
extracted executable path, game/SMAPI versions, and lab owner. Keep the current
directory at the same lab root for every live command.

```powershell
$sdvkit = '<absolute selected portable sdvkit.exe>'
$gamePath = '<explicit complete Stardew and SMAPI installation>'
$checkout = '<checkout containing this recipe>'
$recipe = Join-Path $PWD '.sdvkit\data-migration-recipe'
& $sdvkit doctor --game-path $gamePath --json
& (Join-Path $checkout 'scripts\prepare-data-migration-recipe.ps1') `
  -Sdvkit $sdvkit -GamePath $gamePath -RecipeRoot $recipe
$artifacts = Get-Content (Join-Path $recipe 'artifacts.json') -Raw | ConvertFrom-Json
$v1 = ($artifacts | Where-Object variant -eq 'v1').target
$broken = ($artifacts | Where-Object variant -eq 'v2-broken').target
$v2 = ($artifacts | Where-Object variant -eq 'v2').target
$evidence = Join-Path $recipe 'evidence'
New-Item -ItemType Directory $evidence | Out-Null
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Require three distinct DLL hashes. Do not edit these extracted targets. Each
start stages only the selected immutable ready artifact; no companion is needed.
Before the first start, establish exclusive lab ownership and capture the
[protected-path baseline](releasing.md#run-the-selected-live-checks). If a different task
owns a review, wait for its verified teardown. Prepare the baseline with
`lab test-save --topology single --game-path $gamePath --json` only when absent;
keep its identity and passed bounded-tick result.

## Save v1 and diagnose the broken upgrade

```powershell
& $sdvkit project review start $v1 --game-path $gamePath --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-v1.json')
& $sdvkit project review command 'migration-recipe status' --topology single --json
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-v1.json')
& $sdvkit project review diagnostics --mod SDVKit.DataMigrationRecipe --limit 30 --json |
  Tee-Object (Join-Path $evidence 'diagnostics-v1.json')
```

Require selected target loaded, exact staged version/build identity, and
`artifact=1.0.0; schema=1; credits=37; crop=(O)634; claimed=True; stone=5`.
The baseline must have zero Stone for that exact total; otherwise retain and
compare the actual baseline plus five. Connect the existing native MCP client to
this exact unbound single review with `--allow-fixture-actions`:

```powershell
& $sdvkit project review mcp serve --topology single --allow-fixture-actions
```

Retain its catalogue, tool calls, results, and orderly EOF transcript. Call
`stardew_fixture_save {}` and require `state=completed` and saved identity.
Close the client by orderly EOF. Stop **without reset**, then start broken v2:

```powershell
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-v1.json')
& $sdvkit project review start $broken --game-path $gamePath --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-broken.json')
& $sdvkit project review diagnostics --mod SDVKit.DataMigrationRecipe --limit 30 --json |
  Tee-Object (Join-Path $evidence 'diagnostics-broken.json')
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-broken.json')
```

Require a new owned process/launch, version `2.0.0-beta.1`, the same fixture,
`Broken v2 cannot migrate schema 1; original data was retained.`, and the original
schema-1 selected values with unchanged Stone. Diagnosis is an attributed
selected-mod error plus observations, not console-delivery success. Do not
replace data with defaults or reset the save to make the broken package pass.

## Correct, save, and repeat v2 loads

```powershell
& $sdvkit project review stop --topology single --json
& $sdvkit project review start $v2 --game-path $gamePath --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-v2.json')
& $sdvkit project review diagnostics --mod SDVKit.DataMigrationRecipe --limit 30 --json |
  Tee-Object (Join-Path $evidence 'diagnostics-v2.json')
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-v2.json')
```

Require corrected artifact `2.0.0`, schema 2, balance 37, nested crop `(O)634`,
claimed true, migration count 1, unchanged Stone, and no broken-migration error.
Reconnect MCP to this new exact review, call fixture save, require completion,
close by EOF, stop without reset, and start `$v2` again. Repeat that save/stop/start
cycle once more. Each restart needs a new process/launch and fresh diagnostics
and inventory reads. Both repeated loads must show unchanged selected values,
count 1, and Stone five, with `Loaded existing v2 ledger` rather than another
migration. A serialized save or successful build alone is not restart proof.

Single stop removes owned staging and retains the work save. Therefore the next
single start can stage a different extracted code artifact against that same
fixture. Retained network-2 staging has stricter exact-selection requirements;
this recipe does not extend those contracts or use network-2.

## Missing and future data

In corrected v2, send `migration-recipe fixture missing`. Require its selected
status to report schema missing, then save through MCP, close by EOF, stop
without reset, and start the same `$v2`. Require explicit new defaults from the
table and unchanged Stone. Save and restart those defaults once more; require
unchanged values and count 0.

Next send `migration-recipe fixture future`. Require schema 99, balance 73,
and `futureOnly=preserve-this-value`. Save through MCP, close by EOF, stop and
restart `$v2`; require the attributed `Unsupported schema 99` error, unchanged
selected future values, and unchanged inventory. Save and restart once more to
prove refusal did not silently rewrite unknown fields to v2 defaults. Retain the
future fixture's before/after selected status and completed save results.
These fixture operations intentionally replace the current recipe ledger; do
not use them as a migration strategy.

## Evidence and cleanup

Retain elapsed wall time and result per phase: source/tests/build, immutable
packages, v1 save, broken diagnosis, corrected upgrade, two v2 reloads, missing
and future-data reloads, and final cleanup. Log status describes this recipe's
selected fields only; there is no general mod-data dump. Inventory observations
support the reward claim independently. Record manual intervention and failed
attempts before retrying; never reuse old clients across launches.

Finish the exact owned review, reset its disposable fixture, and read both
statuses:

```powershell
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-final.json')
& $sdvkit project review reset --topology single --json |
  Tee-Object (Join-Path $evidence 'reset-final.json')
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Require stopped topologies, removed staging, reset fixture, no owned game
processes or save reparse points, clean locks, and zero unexplained protected-path
deltas. Run the full [applicable local and CI matrix](releasing.md#select-the-checks).
Offline tests validate this mapping and rejection boundaries; game-bound build
proves compilation against selected APIs; live acceptance requires all fresh
restart observations above. This does not prove arbitrary schema recovery,
multiplayer, real saves, or crash-safe reward transactions.

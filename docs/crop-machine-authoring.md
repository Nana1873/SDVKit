# Author and verify a crop-to-machine recipe

Create **Observed Harvest Recipe**, a small Content Patcher pack that makes one
Strawberry placed in a Keg produce two Joja Cola after 20 in-game minutes. Start
with a deliberate item-ID mistake, prove the resulting native Wine fallback,
correct only that source value, then connect watering, harvest, Keg processing,
inventory, chest, and save/reload observations.

> **Observed workflow:** the live workflow was exercised on 2026-09-09 for
> [#190](https://github.com/Nana1873/SDVKit/issues/190) with Stardew Valley
> 1.6.15 build 24356, SMAPI 4.5.2, and Content Patcher 2.9.1. The exact SDVKit
> candidate was commit `8137496`; its Windows x64 ZIP SHA-256 was
> `d1c4a117b87fc23df92ecf4ffd190040423018591309a367598c1686853ad4b4`.
> The issue retains the complete identities, failed attempts, elapsed phases,
> and cleanup evidence; its integration record adds PR and main CI. Normal saves
> and the normal `Mods` directory
> were not modified; the explicitly selected Content Patcher provider was copied
> into the ignored lab staging area.

This is a bounded authoring recipe, not a general farm automation system. It
supports one ordinary Spring Strawberry crop, one vanilla Keg, and one regular
wooden chest in an owned disposable single-player review on screen 0. Preparing
the representative world is separate from the native actions under test: use
normal gameplay or an explicitly selected sample-specific helper, and label any
placement, growth, time acceleration, or repositioning as synthetic.

The complete reviewed helper source is kept as the focused
[`RecipeProbe` example](examples/crop-machine-recipe/RecipeProbe/). It adds only
the seven console commands used by this recipe; it is not part of SDVKit, the
pack, or a distributable mod. Copy it below the lab's ignored `.sdvkit/`, build
it there, and select that exact directory explicitly as a companion.

Read [world inspection](world-inspection.md),
[world interaction](world-interaction.md),
[inventory inspection](inventory-inspection.md), and
[container inspection](container-inspection.md) before changing this boundary.

## Select the installation and isolated lab

Keep the current directory at one SDVKit lab root. Put generated samples,
packages, logs, screenshots, and evidence below its ignored `.sdvkit/` directory.
Select the exact Content Patcher 2.9.1 ready directory explicitly; SDVKit never
searches normal or mod-manager-owned Mods directories for companions.

```powershell
$lab = $PWD.Path
$sdvkit = (Resolve-Path 'C:\path\to\SDVKit\sdvkit.exe').Path
$gamePath = 'C:\path\to\Stardew Valley'
$probeSource = (Resolve-Path `
    'C:\path\to\SDVKit\docs\examples\crop-machine-recipe\RecipeProbe').Path
$pack = Join-Path $lab '.sdvkit\crop-machine\ObservedHarvestRecipe'
$provider = Join-Path $lab '.sdvkit\crop-machine\ContentPatcher'
$probe = Join-Path $lab '.sdvkit\crop-machine\RecipeProbe'
$evidence = Join-Path $lab '.sdvkit\crop-machine\evidence'
New-Item -ItemType Directory -Force $pack, $evidence | Out-Null
if (Test-Path $probe) { throw "RecipeProbe destination must start absent." }
Copy-Item $probeSource $probe -Recurse

& $sdvkit doctor --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'doctor.json')
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'preflight-status.json')
```

Require one ready installation and an unowned, stopped lab before continuing.
The copied helper must contain only the reviewed `ModEntry.cs`, project, and
manifest from this recipe; do not deploy or run it from the documentation tree.
Record the selected SDVKit commit or ZIP hash, game/SMAPI/provider versions, test
save identity, and protected normal-data fingerprint. Follow
[lab preparation](live-review.md#prepare-the-lab); do not follow reparse points
while comparing protected paths.

## Write the original pack with one deliberate mistake

Create these two complete source files. `manifest.json`:

```json
{
  "Name": "Observed Harvest Recipe",
  "Author": "SDVKit",
  "Version": "1.0.0",
  "Description": "Turns one strawberry into two Joja Cola in a Keg.",
  "UniqueID": "SDVKit.ObservedHarvestRecipe",
  "ContentPackFor": {
    "UniqueID": "Pathoschild.ContentPatcher",
    "MinimumVersion": "2.9.1"
  },
  "UpdateKeys": []
}
```

`content.json` deliberately uses Spring Onion `(O)399` instead of Strawberry
`(O)400`:

```json
{
  "Format": "2.9.0",
  "Changes": [
    {
      "LogName": "Observed strawberry Keg output",
      "Action": "EditData",
      "Target": "Data/Machines",
      "TargetField": [ "(BC)12", "OutputRules" ],
      "Entries": {
        "SDVKit.ObservedHarvestRecipe_Strawberry": {
          "Id": "SDVKit.ObservedHarvestRecipe_Strawberry",
          "Triggers": [
            {
              "Trigger": "ItemPlacedInMachine",
              "RequiredItemId": "(O)399",
              "RequiredCount": 1
            }
          ],
          "OutputItem": [
            {
              "Id": "SDVKit.ObservedHarvestRecipe_Cola",
              "ItemId": "(O)167",
              "MinStack": 2,
              "MaxStack": 2
            }
          ],
          "MinutesUntilReady": 20
        }
      },
      "MoveEntries": [
        {
          "ID": "SDVKit.ObservedHarvestRecipe_Strawberry",
          "ToPosition": "Top"
        }
      ]
    }
  ]
}
```

Check and package this wrong source. Preserve its source and archive hash before
editing it; a schema pass proves file validity, not correct item semantics.

```powershell
& $sdvkit project check $pack --json |
    Tee-Object (Join-Path $evidence 'wrong-check.json')
& $sdvkit project package $pack --json |
    Tee-Object (Join-Path $evidence 'wrong-package.json')
```

## Prove the wrong native outcome

Start the pack as the selected target with the exact provider, reviewed helper,
and owned test save. The helper commands are `recipe_probe_prepare_crop`,
`recipe_probe_mature`, `recipe_probe_prepare_machine`, `recipe_probe_advance`,
`recipe_probe_prepare_chest`, `recipe_probe_bind_chest`, and
`recipe_probe_report`. Read each resulting report before continuing.

```powershell
& $sdvkit project build $probe --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'probe-build.json')
& $sdvkit project review start $pack --game-path $gamePath `
    --topology single --test-save --companion $provider --companion $probe --json |
    Tee-Object (Join-Path $evidence 'wrong-start.json')

& $sdvkit project review cp-diagnose `
    --pack SDVKit.ObservedHarvestRecipe `
    --provider Pathoschild.ContentPatcher --asset Data/Machines --json |
    Tee-Object (Join-Path $evidence 'wrong-cp-diagnose.json')
& $sdvkit project review data get Data/Machines '(BC)12' `
    --topology single --json |
    Tee-Object (Join-Path $evidence 'wrong-keg-record.json')
```

Require the patch to be loaded, its conditions to match, and the effective first
rule to require `(O)399`. Then prepare a dry `(O)745` Strawberry crop whose
harvest item is `(O)400`, verify the selected watering can and its remaining
water, and use fresh world and inventory revisions for each native action:

```powershell
& $sdvkit project review command "recipe_probe_prepare_crop" `
    --topology single --json
& $sdvkit project review command "recipe_probe_report" `
    --topology single --json

# Read the completed RecipeProbe report from the isolated SMAPI console.
$x = [int](Read-Host 'Fresh reported crop tile X')
$y = [int](Read-Host 'Fresh reported crop tile Y')
$world = & $sdvkit project review world $x $y 1 1 `
    --topology single --json | ConvertFrom-Json
$inventory = & $sdvkit project review inventory `
    --topology single --json | ConvertFrom-Json
$soil = $world.data.tiles[0].soil

& $sdvkit project review interact water $x $y `
    $soil.instanceId $soil.revision $inventory.data.inventoryRevision `
    --topology single --json
```

Read the world and backpack again. `completed` proves only that the bounded
native input lifecycle finished; require the same soil/crop to become watered.
The public world/inventory reports do not expose `WaterLeft`, so claim a water
resource decrease only when the selected sample helper report or separate UI
evidence observes it. Synthetically mature only that already-watered crop,
select an empty backpack slot through process-local input, and call
`harvest` with the fresh crop and inventory revisions. Require the same regrowing
crop to become not-ready and exactly one `(O)400` to appear in the backpack.

```powershell
& $sdvkit project review command "recipe_probe_mature" `
    --topology single --json
# Perform the revision-bound native harvest described above, then:
& $sdvkit project review command "recipe_probe_prepare_machine" `
    --topology single --json
& $sdvkit project review command "recipe_probe_report" `
    --topology single --json
```

Place an empty Keg separately, select that actually harvested Strawberry, and
call `machineInsert` with fresh machine and inventory revisions. With the wrong
rule, the observed acceptance run consumed the Strawberry but the same Keg used
Stardew's later default fruit rule: Wine `(O)348`, stack one, 10,000 minutes.
For the bounded shortcut, `recipe_probe_advance` calls native `minutesElapsed`
only on that exact marked processing Keg; label the time advance synthetic and
prove the resulting ready Wine through fresh public state. CP diagnosis alone is
not this outcome proof.

```powershell
& $sdvkit project review command "recipe_probe_advance" `
    --topology single --json
& $sdvkit project review command "recipe_probe_report" `
    --topology single --json
```

Stop and reset before switching source. Require exact PID absence, empty staged
artifacts/mailboxes, acquirable retained locks, no unexpected reparse point, and
an exact protected-path comparison.

## Correct the source and prove two Cola

Change only the trigger value from `(O)399` to `(O)400`, then check and package
again. Preserve the corrected source/archive hashes and compare the two source
trees so the one semantic edit is explicit.

Repeat the same diagnosis, effective `Data/Machines` record, native watering,
native harvest, and native Keg insertion sequence. The corrected run must show:

- the same Keg instance changing from idle to processing;
- the harvested Strawberry decreasing by one in the backpack;
- retained input `(O)400`;
- pending output `(O)167`, stack two; and
- a positive 20-minute countdown followed by a ready output through native time.

Also retain two safe refusal cases. Move the player through process-local input
until the target is observably outside the required faced-adjacent position, then
submit one action with otherwise fresh identities. Separately reuse an old target
revision with a fresh inventory revision after the machine changed. Both calls
must be `notDispatched`, and fresh world/inventory reads must be unchanged. Never
retry `mayHaveRun`, canceled, timed-out, partial, or uncertain work.

## Use a real MCP client

Start a default STDIO server and retain its initialize reply and `tools/list`.
`stardew_cp_diagnose`, `stardew_data_record_get`,
`stardew_world_area_get`, `stardew_inventory_get`, and
`stardew_container_get` are available; `stardew_world_interact` must be absent.
Close that client, then start the action client explicitly:

```powershell
& $sdvkit project review mcp serve --topology single `
    --allow-world-actions --allow-input --allow-fixture-actions
```

Serialize game-backed calls and wait for each matching response ID before sending
the next. Read the ready Keg and backpack, then call
`stardew_world_interact` once with `action=machineCollect` and those fresh
identities. Separate fresh reads must show the same Keg idle with no output and
the backpack changing from zero to two Cola. Retain the tool annotations:
world interaction is destructive and non-idempotent.

The permission is startup-bound. To test replacement safety without mutating the
new launch, keep the old action server open, stop and start a replacement review,
then submit one old action request with its retained old identities. Require
`worldActionStartupBindingChanged` and `notDispatched`. Read-only unbound single
tools may read the current owned single review; that does not extend the old
server's action permission.

## Transfer, save, reload, and clean up

Open one ordinary empty wooden chest through native process-local input. On
current `main`, prefer the bounded
[`container-transfer`](container-transfer.md) command or its separately opted-in
MCP equivalent. A native menu click using fresh observed geometry is also valid
when testing the input path. In either case, require complete container evidence:
two Cola in the chest, zero in the player backpack, an empty held item, and no
limitations.

The accepted #190 run tested the native menu path. Its helper placed the chest at
`(41,4)` and the player at `(40,4)`, but always use the freshly reported tiles.
Open it with one process-local `X` press, read `project review menu` and
`project review container`, find the public menu component whose `controllerId`
equals the observed Cola backpack slot, and click its center with the same fresh
`uiRevision`:

```powershell
& $sdvkit project review command "recipe_probe_prepare_chest" `
    --topology single --json
& $sdvkit project review command "recipe_probe_report" `
    --topology single --json
# Open the freshly reported chest with one process-local X press, then inspect it.
$menu = & $sdvkit project review menu --topology single --json | ConvertFrom-Json
$container = & $sdvkit project review container --topology single --json |
    ConvertFrom-Json
$sources = @($container.data.player.slots |
    Where-Object { $_.item.qualifiedItemId -eq '(O)167' -and $_.item.stack -eq 2 })
if ($sources.Count -ne 1) { throw "Expected exactly one Cola source stack." }
$components = @($menu.menus.components |
    Where-Object { $_.controllerId -eq $sources[0].slot `
        -and $_.visibleFlag -eq $true -and $_.intersectsViewport -eq $true })
if ($components.Count -ne 1) {
    throw "Expected exactly one visible, in-viewport Cola component."
}
$component = $components[0]
$clickX = $component.bounds.x + [int]($component.bounds.width / 2)
$clickY = $component.bounds.y + [int]($component.bounds.height / 2)
& $sdvkit project review command `
    "sdvkit input click $clickX $clickY MouseLeft 1 $($menu.uiRevision)" `
    --topology single --json
```

Read the container again rather than assuming click semantics. In the observed
1280x720 menu, player slot 9 mapped to `(844,476)` and the single click moved the
whole two-item stack directly into chest slot 0 with an empty held item.

Save through `stardew_fixture_save` on a server started with
`--allow-fixture-actions`. Require `state=ready`, the exact fixture/save IDs,
`mayHaveRun=false`, and a persisted timestamp. Stop without reset, restart the
same target/provider/helper selection and test save, rebind only the player
position to the existing marked chest, open it natively, and read it again through
CLI and MCP. The chest must still contain exactly two Cola before final reset.

```powershell
& $sdvkit project review command "recipe_probe_bind_chest" `
    --topology single --json
& $sdvkit project review command "recipe_probe_report" `
    --topology single --json
```

Finish with one close input if a menu remains open, selected-mod diagnostics,
`project review stop`, and `project review reset`. Repeat the preflight cleanup
and protected-path comparison. Report build/package success, native runtime
effects, persistence, and cleanup as separate results.

## Retain limitations and interventions

The #190 acceptance intentionally retained rather than hid these observations:

- one acknowledged corrected watering input left its freshly prepared soil dry;
  the cause was not established. A newly prepared crop plus explicit selection
  of a previously proven watering-can slot produced the required observed effect;
- the short 20-minute corrected recipe completed naturally before a later
  sample-helper time-advance command, so readiness is native elapsed-time proof,
  not a successful helper-advance claim;
- the first sample helper attempted to create `(BC)130` through `ItemRegistry`
  and explicitly refused because the result was not a `Chest`. Its ignored test
  source was changed to Stardew's public `Chest(true, tile, "130")` constructor,
  rebuilt, and restarted before chest evidence; this was a test-fixture change,
  not a pack or SDVKit runtime change; and
- an overnight transition occurred while preserving the owned fixture. Evidence
  records the resulting day/location and does not silently combine pre- and
  post-transition identities.

These limits do not weaken the separate successful observations. They also do
not authorize a release: complete PR CI, resulting-main CI, issue evidence, and
the applicable [release check matrix](releasing.md#select-the-checks) remain
mandatory.

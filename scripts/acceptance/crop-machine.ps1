#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a Content Patcher Keg rule turns one Strawberry into two Joja Cola; prove it through native
watering, harvest, Keg processing, chest transfer, save and restart.

.DESCRIPTION
Uses the pack in docs/examples/crop-machine-recipe/ObservedHarvestRecipe and the sample helper RecipeProbe, which
places a dry Strawberry crop, a Keg and a chest in the test save and reports their tiles in the SMAPI log. Its
placements and synthetic growth are setup, not proof; every effect under test is a native action read back through
public state.

1. Wrong rule ((O)399 instead of (O)400): the harvested Strawberry falls through to Stardew's default fruit rule and
   becomes Wine.
2. Correct rule: the same flow produces two Joja Cola after 20 in-game minutes; two refusal cases (not facing the
   Keg, stale revision) must not dispatch; collecting moves two Cola into the backpack.
3. A native click moves the Cola into a chest; after a fixture save and a restart the chest still holds them.

Guide: docs/test-a-machine-rule.md.

.EXAMPLE
pwsh scripts/acceptance/crop-machine.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -ContentPatcher D:\Mods\ContentPatcher
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    # A local Content Patcher 2.9.1 folder. It is copied into the lab; the original is not changed.
    [Parameter(Mandatory)] [string] $ContentPatcher,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'crop-machine' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$strawberry = '(O)400'
$cola = '(O)167'
$wine = '(O)348'

function Get-ProbeState {
    # Runs recipe_probe_report and parses the newest RECIPE_PROBE_STATE line from the review's SMAPI log.
    param([string] $Label)
    $before = (Get-Item -LiteralPath $script:smapiLog).Length
    Invoke-SdvKit "probe-report-$Label" @('project', 'review', 'command', 'recipe_probe_report', '--json') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $stream = [IO.File]::Open($script:smapiLog, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$stream.Seek($before, 'Begin')
            $text = [IO.StreamReader]::new($stream).ReadToEnd()
        }
        finally { $stream.Dispose() }
        $line = @($text -split "`n" | Where-Object { $_ -match 'RECIPE_PROBE_STATE' })[-1]
    } until ($line -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ([bool]$line) 'RecipeProbe did not report its state.'
    Assert-That ($text -notmatch 'RECIPE_PROBE_REFUSED') "RecipeProbe refused: $($text -split "`n" | Where-Object { $_ -match 'RECIPE_PROBE_REFUSED' })"
    $line | Set-Content -LiteralPath (Join-Path $context.Evidence "probe-$Label.txt") -Encoding utf8

    $tile = { param($name) if ($line -match "$name=\[tile=(\d+),(\d+)") { [pscustomobject]@{ X = [int]$Matches[1]; Y = [int]$Matches[2] } } }
    [pscustomobject]@{
        Crop = & $tile 'crop'
        Machine = & $tile 'machine'
        Chest = & $tile 'chest'
        Player = if ($line -match ' tile=(\d+),(\d+) facing=(\d)') { [pscustomobject]@{ X = [int]$Matches[1]; Y = [int]$Matches[2]; Facing = [int]$Matches[3] } }
    }
}

function Invoke-ProbeCommand([string] $Command) {
    $result = Invoke-SdvKit $Command @('project', 'review', 'command', $Command, '--json')
    Assert-That ($result.Json.commandWritten -eq $true) "the helper command $Command did not run."
    Start-Sleep -Seconds 1
}

function Read-Tile([string] $Label, $Tile) {
    (Invoke-SdvKit "world-$Label" @('project', 'review', 'world', $Tile.X, $Tile.Y, '1', '1', '--json')).Json.data.tiles[0]
}

function Read-Inventory([string] $Label) {
    (Invoke-SdvKit "inventory-$Label" @('project', 'review', 'inventory', '--json')).Json.data
}

function Get-ItemCount($Inventory, [string] $ItemId) {
    [int](@($Inventory.slots | Where-Object { $_.item.qualifiedItemId -ceq $ItemId } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
}

function Select-Slot([string] $Label, [int] $Slot) {
    # Toolbar keys: slots 0-8 are D1-D9, slot 9 is D0, 10 is OemMinus, 11 is OemPlus.
    $key = switch ($Slot) { { $_ -lt 9 } { "D$($Slot + 1)" } 9 { 'D0' } 10 { 'OemMinus' } 11 { 'OemPlus' } }
    Invoke-Tool "select-$Label" 'stardew_input_press' @{ button = $key } -OptIn '--allow-input' | Out-Null
    $inventory = Read-Inventory "selected-$Label"
    Assert-That ($inventory.selectedSlot -eq $Slot) "toolbar slot $Slot is not selected."
    return $inventory
}

function Invoke-WorldAction {
    # One native world action bound to fresh target and inventory revisions. Returns the action result.
    param([string] $Label, [string] $Action, $Tile, [int[]] $AllowExit = @(0))
    $target = Read-Tile "before-$Label" $Tile
    $inventory = Read-Inventory "before-$Label"
    $identity = switch ($Action) {
        'water' { $target.soil }
        'harvest' { $target.soil.crop.data }
        default { $target.machine }
    }
    Assert-That ($null -ne $identity) "no $Action target at $($Tile.X),$($Tile.Y)."
    return (Invoke-SdvKit "interact-$Label" @('project', 'review', 'interact', $Action, $Tile.X, $Tile.Y,
            $identity.instanceId, $identity.revision, $inventory.inventoryRevision, '--json') -AllowExit $AllowExit).Json
}

function Start-CropReview([string] $Label, [string] $Pack) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $Pack, '--game-path', $context.GamePath, '--topology', 'single',
        '--test-save', '--companion', $script:provider, '--companion', $script:probe, '--json') | Out-Null
    Wait-ReviewReady -TestSave | Out-Null
    # The test save loads in the FarmHouse; the helper works on the Farm.
    Invoke-Tool "farm-$Label" 'stardew_fixture_farm' -OptIn '--allow-fixture-actions' | Out-Null
}

function Invoke-HarvestToKeg {
    # Waters, matures and harvests one Strawberry natively, then inserts it into the Keg. Returns the Keg tile.
    param([string] $Label, [string] $ExpectedRuleItem)

    $cp = Invoke-SdvKit "cp-diagnose-$Label" @('project', 'review', 'cp-diagnose', '--pack', 'SDVKit.ObservedHarvestRecipe',
        '--provider', 'Pathoschild.ContentPatcher', '--asset', 'Data/Machines', '--json')
    $patch = @($cp.Json.summary.patches)[0]
    Assert-That ($patch.loadedAndEnabled -and $patch.conditionsMatch -and $patch.applied) 'the Keg patch is not loaded, matching and applied.'
    $record = (Invoke-SdvKit "keg-record-$Label" @('project', 'review', 'data', 'get', 'Data/Machines', '(BC)12', '--json')).Json.record
    Assert-That ($record.OutputRules[0].Triggers[0].RequiredItemId -ceq $ExpectedRuleItem) "the first Keg rule requires $($record.OutputRules[0].Triggers[0].RequiredItemId), expected $ExpectedRuleItem."

    Invoke-ProbeCommand 'recipe_probe_prepare_crop'
    $crop = (Get-ProbeState "$Label-crop").Crop
    Assert-That ($null -ne $crop) 'RecipeProbe did not report a crop tile.'
    Assert-That ((Read-Tile "$Label-dry" $crop).soil.watered -eq $false) 'the prepared soil is already watered.'
    $water = Invoke-WorldAction "$Label-water" 'water' $crop
    Assert-That ($water.state -ceq 'completed') "watering did not complete: $($water.errorCode)"
    Assert-That ((Read-Tile "$Label-watered" $crop).soil.watered -eq $true) 'the soil is not watered after the native watering action.'

    Invoke-ProbeCommand 'recipe_probe_mature'
    $inventory = Read-Inventory "$Label-before-harvest"
    $empty = @($inventory.slots | Where-Object { $_.state -ceq 'empty' })[0].slot
    Select-Slot "$Label-empty" $empty | Out-Null
    $before = Get-ItemCount (Read-Inventory "$Label-harvest-count") $strawberry
    $harvest = Invoke-WorldAction "$Label-harvest" 'harvest' $crop
    Assert-That ($harvest.state -ceq 'completed') "harvest did not complete: $($harvest.errorCode)"
    $inventory = Read-Inventory "$Label-after-harvest"
    Assert-That ((Get-ItemCount $inventory $strawberry) -eq $before + 1) 'the harvest did not add exactly one Strawberry.'
    Assert-That ((Read-Tile "$Label-harvested" $crop).soil.crop.data.readyForHarvest -ne $true) 'the crop is still ready after harvest.'

    Invoke-ProbeCommand 'recipe_probe_prepare_machine'
    $keg = (Get-ProbeState "$Label-machine").Machine
    Assert-That ($null -ne $keg) 'RecipeProbe did not report a Keg tile.'
    $slot = @($inventory.slots | Where-Object { $_.item.qualifiedItemId -ceq $strawberry })[0].slot
    Select-Slot "$Label-strawberry" $slot | Out-Null
    Assert-That ((Read-Tile "$Label-keg-idle" $keg).machine.state -ceq 'idle') 'the Keg is not idle before insertion.'
    $insert = Invoke-WorldAction "$Label-insert" 'machineInsert' $keg
    Assert-That ($insert.state -ceq 'completed') "machine insertion did not complete: $($insert.errorCode)"
    Assert-That ((Get-ItemCount (Read-Inventory "$Label-after-insert") $strawberry) -eq $before) 'the Strawberry was not consumed.'
    return $keg
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $examples = Join-Path $PSScriptRoot '..\..\docs\examples\crop-machine-recipe'
    $script:provider = Join-Path $work 'ContentPatcher'
    $script:probe = Join-Path $work 'RecipeProbe'
    $wrongPack = Join-Path $work 'wrong\ObservedHarvestRecipe'
    $fixedPack = Join-Path $work 'fixed\ObservedHarvestRecipe'
    Copy-Item -Recurse -LiteralPath $ContentPatcher -Destination $script:provider
    Copy-Item -Recurse -LiteralPath (Join-Path $examples 'RecipeProbe') -Destination $script:probe
    Remove-Item -LiteralPath (Join-Path $script:probe 'README.md')
    foreach ($pack in $wrongPack, $fixedPack) {
        Copy-Item -Recurse -LiteralPath (Join-Path $examples 'ObservedHarvestRecipe') -Destination $pack
    }

    # The deliberate mistake: Spring Onion (O)399 instead of Strawberry (O)400.
    $content = Join-Path $wrongPack 'content.json'
    (Get-Content -Raw -LiteralPath $content).Replace('"RequiredItemId": "(O)400"', '"RequiredItemId": "(O)399"') | Set-Content -LiteralPath $content -NoNewline -Encoding utf8NoBOM
    $script:smapiLog = Join-Path $context.Lab '.sdvkit\lab\profiles\single\AppData\Roaming\StardewValley\ErrorLogs\SMAPI-latest.txt'

    Write-Step 'Check and package both pack variants; build the helper.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    foreach ($variant in @{ wrong = $wrongPack; fixed = $fixedPack }.GetEnumerator()) {
        Invoke-SdvKit "check-$($variant.Key)" @('project', 'check', $variant.Value, '--json') | Out-Null
        Invoke-SdvKit "package-$($variant.Key)" @('project', 'package', $variant.Value, '--json') | Out-Null
    }
    Invoke-SdvKit 'probe-build' @('project', 'build', $script:probe, '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Wrong rule: the Strawberry must become Wine.'
    Start-CropReview 'wrong' $wrongPack
    $keg = Invoke-HarvestToKeg 'wrong' '(O)399'
    $processing = Read-Tile 'wrong-processing' $keg
    Assert-That ($processing.machine.state -ceq 'processing' -and $processing.machine.output.item.qualifiedItemId -ceq $wine) "the Keg output is $($processing.machine.output.item.qualifiedItemId), expected Wine $wine."
    Invoke-ProbeCommand 'recipe_probe_advance'
    $ready = Read-Tile 'wrong-ready' $keg
    Assert-That ($ready.machine.readyForHarvest -eq $true -and $ready.machine.output.item.qualifiedItemId -ceq $wine) 'the Wine is not ready after the synthetic time advance.'
    Write-Step 'Wrong rule produced Wine, as expected.'
    Stop-Review -Reset -Label 'wrong'

    Write-Step 'Correct rule: the Strawberry must become two Joja Cola.'
    Start-CropReview 'fixed' $fixedPack
    $keg = Invoke-HarvestToKeg 'fixed' $strawberry
    $processing = Read-Tile 'fixed-processing' $keg
    Assert-That ($processing.machine.state -ceq 'processing' -and $processing.machine.input.item.qualifiedItemId -ceq $strawberry) 'the Keg is not processing the Strawberry.'
    Assert-That ($processing.machine.output.item.qualifiedItemId -ceq $cola -and $processing.machine.output.item.stack -eq 2) 'the pending output is not two Joja Cola.'
    Assert-That ($processing.machine.minutesUntilReady -gt 0 -and $processing.machine.minutesUntilReady -le 20) "the countdown is $($processing.machine.minutesUntilReady) minutes, expected 1-20."

    # 20 in-game minutes take about 14 real seconds.
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Seconds 5
        $ready = Read-Tile 'fixed-waiting' $keg
    } until ($ready.machine.readyForHarvest -eq $true -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($ready.machine.readyForHarvest -eq $true) 'the Cola did not become ready through native time.'

    Write-Step 'Refusals: an action without facing the Keg, and one with a stale revision, must not dispatch.'
    $player = (Get-ProbeState 'fixed-before-refusal').Player
    $faceKeg = if ($keg.X -gt $player.X) { 'D' } elseif ($keg.X -lt $player.X) { 'A' } elseif ($keg.Y -gt $player.Y) { 'S' } else { 'W' }
    $faceAway = @{ D = 'A'; A = 'D'; S = 'W'; W = 'S' }[$faceKeg]
    Invoke-Tool 'face-away' 'stardew_input_press' @{ button = $faceAway } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1
    $refused = Invoke-WorldAction 'refuse-not-faced' 'machineCollect' $keg -AllowExit @(0, 3)
    Assert-That ($refused.dispatchState -ceq 'notDispatched') "an action while not facing the Keg was $($refused.dispatchState)."
    Invoke-Tool 'face-keg' 'stardew_input_press' @{ button = $faceKeg } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1
    $state = (Get-ProbeState 'fixed-faced').Player
    Assert-That ($state.X -eq $player.X -and $state.Y -eq $player.Y) 'turning moved the player off its tile.'

    # The revision read while the Keg was processing is stale now that the output is ready.
    $inventory = Read-Inventory 'stale'
    $refused = (Invoke-SdvKit 'interact-stale' @('project', 'review', 'interact', 'machineCollect', $keg.X, $keg.Y,
            $processing.machine.instanceId, $processing.machine.revision, $inventory.inventoryRevision, '--json') -AllowExit @(0, 3)).Json
    Assert-That ($refused.dispatchState -ceq 'notDispatched') "an action with a stale revision was $($refused.dispatchState)."

    Write-Step 'Collect the two Cola natively.'
    $colaBefore = Get-ItemCount (Read-Inventory 'collect-before') $cola
    $collect = Invoke-WorldAction 'collect' 'machineCollect' $keg
    Assert-That ($collect.state -ceq 'completed') "collection did not complete: $($collect.errorCode)"
    Assert-That ((Get-ItemCount (Read-Inventory 'collect-after') $cola) -eq $colaBefore + 2) 'collection did not add two Cola.'
    Assert-That ((Read-Tile 'collect-keg' $keg).machine.state -ceq 'idle') 'the Keg is not idle after collection.'

    Write-Step 'Move the Cola into a chest with a native click, save, restart and read the chest again.'
    Invoke-ProbeCommand 'recipe_probe_prepare_chest'
    Get-ProbeState 'chest' | Out-Null
    Invoke-Tool 'open-chest' 'stardew_input_press' @{ button = 'X' } -OptIn '--allow-input' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $menu = (Invoke-SdvKit 'chest-menu' @('project', 'review', 'menu', '--json')).Json
    } until ($menu.menuOpen -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($menu.menuOpen -eq $true) 'the chest menu did not open.'
    $container = (Invoke-SdvKit 'chest-before' @('project', 'review', 'container', '--json')).Json.data
    $source = @($container.player.slots | Where-Object { $_.item.qualifiedItemId -ceq $cola -and $_.item.stack -eq 2 })
    Assert-That ($source.Count -eq 1) 'expected exactly one stack of two Cola in the backpack.'
    $component = @($menu.menus.components | Where-Object { $_.controllerId -eq $source[0].slot -and $_.visibleFlag -and $_.intersectsViewport })
    Assert-That ($component.Count -eq 1) "backpack slot $($source[0].slot) has no single visible menu component."
    Invoke-ComponentClick 'click-cola' -Menu $menu -Component $component[0] | Out-Null
    $container = (Invoke-SdvKit 'chest-after' @('project', 'review', 'container', '--json')).Json.data
    $inChest = [int](@($container.container.slots | Where-Object { $_.item.qualifiedItemId -ceq $cola } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
    Assert-That ($inChest -eq 2 -and $container.heldItem.state -ceq 'empty' -and $container.complete) "the chest holds $inChest Cola, expected 2 with nothing held."
    Invoke-Tool 'close-chest' 'stardew_input_press' @{ button = 'Escape' } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1

    $save = Invoke-Tool 'fixture-save' 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
    Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) 'the fixture save did not complete.'
    Stop-Review -Label 'before-restart'

    Start-CropReview 'restart' $fixedPack
    Invoke-ProbeCommand 'recipe_probe_bind_chest'
    Get-ProbeState 'rebound' | Out-Null
    Invoke-Tool 'reopen-chest' 'stardew_input_press' @{ button = 'X' } -OptIn '--allow-input' | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $menu = (Invoke-SdvKit 'reopen-menu' @('project', 'review', 'menu', '--json')).Json
    } until ($menu.menuOpen -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($menu.menuOpen -eq $true) 'the chest menu did not open after the restart.'
    $container = (Invoke-SdvKit 'chest-restart' @('project', 'review', 'container', '--json')).Json.data
    $inChest = [int](@($container.container.slots | Where-Object { $_.item.qualifiedItemId -ceq $cola } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
    Assert-That ($inChest -eq 2) "after the restart the chest holds $inChest Cola, expected 2."
    Invoke-Tool 'close-restart' 'stardew_input_press' @{ button = 'Escape' } -OptIn '--allow-input' | Out-Null
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', 'SDVKit.ObservedHarvestRecipe', '--json') | Out-Null
    Write-Step 'Two Cola survived the save and restart.'

    Stop-Review -Reset -Label 'final'
}
catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Complete-Acceptance -Failed:$failed
}

if ($failed) { exit 1 }

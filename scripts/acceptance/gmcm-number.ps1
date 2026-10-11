#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a GMCM integer slider (rows 15/17/19) changes the Farm row a mod warps to; prove that slider moves
stay unsaved, that Save writes row 19, and that a restart with the saved config warps to row 19.

.DESCRIPTION
Builds docs/examples/gmcm-number-recipe/GmcmArrivalRow, packages it once and reviews the extracted package with a
copy of Generic Mod Config Menu 1.16.0. The script:

1. checks the default (ArrivalRow=15, Farm 64,15) and opens the mod's GMCM page;
2. clicks the middle slider step, drags off-step and then to the maximum, and requires the mod's value to stay 15
   at every unsaved position;
3. clicks Save, expects SDVKit to report the staged config change (reviewStagingOwnershipDrifted), reconciles it
   with `project review config-reconcile`, and requires ArrivalRow=19 in the same process;
4. stops, extracts the unchanged ZIP again with the reconciled config, restarts and requires Farm 64,19.

GMCM's page exposes no menu components to SDVKit, so the slider and Save positions are screen coordinates. The
defaults were measured for GMCM 1.16.0 in SDVKit's default 1280x720 lab window; the script refuses to click when
the viewport differs. The checks prove that no slider move changes the value before Save and that the final drag
reaches the maximum; whether the middle click and off-step drag landed on Row 17 is visible only on the screenshots
kept in the evidence folder.

Guide: docs/test-a-gmcm-option.md.

.EXAMPLE
pwsh scripts/acceptance/gmcm-number.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Gmcm D:\Mods\GenericModConfigMenu
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    # A local Generic Mod Config Menu 1.16.0 folder. It is copied into the lab; the original is not changed.
    [Parameter(Mandatory)] [string] $Gmcm,
    [int] $SliderMinX = 660,
    [int] $SliderMidX = 820,
    [int] $SliderMaxX = 980,
    [int] $SliderY = 141,
    # Right of the maximum tick: GMCM truncates the slider position before clamping it.
    [int] $SliderMaxDragX = 1004,
    [int] $SaveX = 692,
    [int] $SaveY = 657,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'gmcm-number' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$modId = 'ExampleAuthor.GmcmArrivalRow'
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-ArrivalRow([string] $Path) {
    # Accepts exactly {"ArrivalRow": 15|17|19}.
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $fields = @($document.RootElement.EnumerateObject())
        [int]$value = 0
        if ($fields.Count -ne 1 -or $fields[0].Name -cne 'ArrivalRow' -or -not $fields[0].Value.TryGetInt32([ref]$value) -or
            $value -notin 15, 17, 19) {
            throw "Acceptance failed: $Path is not a valid ArrivalRow config."
        }
        return $value
    }
    finally { $document.Dispose() }
}

function Get-ModState([string] $Label) {
    # Runs the mod's state command and returns the ArrivalRow it logs.
    $offset = (Get-Item -LiteralPath $script:smapiLog).Length
    $command = Invoke-SdvKit "state-$Label" @('project', 'review', 'command', 'gmcm_arrival_row_state', '--json')
    Assert-That ($command.Json.commandWritten -eq $true) 'the state command did not run.'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 300
        $stream = [IO.File]::Open($script:smapiLog, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$stream.Seek($offset, 'Begin')
            $tail = [IO.StreamReader]::new($stream).ReadToEnd()
        }
        finally { $stream.Dispose() }
    } until ($tail -match 'ArrivalRow=(\d+)' -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($tail -match 'ArrivalRow=(\d+)') 'the mod did not report its ArrivalRow.'
    return [int]$Matches[1]
}

function Get-Position([string] $Label) {
    $runtime = (Invoke-SdvKit "status-$Label" @('project', 'review', 'status', '--json')).Json.lab.gameSide.runtime
    "$($runtime.locationId) $($runtime.tileX),$($runtime.tileY)"
}

function Get-MenuRevision([string] $Label) {
    $menu = (Invoke-SdvKit "menu-$Label" @('project', 'review', 'menu', '--json')).Json
    Assert-That ($menu.menuOpen -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$menu.uiRevision)) 'the GMCM page is not open.'
    Assert-That ($menu.viewport.width -eq 1280 -and $menu.viewport.height -eq 720) "the viewport is $($menu.viewport.width)x$($menu.viewport.height); the slider coordinates are for 1280x720. Measure them on the screenshots and pass -SliderMinX and the other coordinates."
    return [string]$menu.uiRevision
}

function Start-GmcmReview([string] $Label, [string] $Target) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $Target, '--game-path', $context.GamePath, '--companion', $script:provider,
        '--topology', 'single', '--test-save', '--json') | Out-Null
    $status = Wait-ReviewReady -TestSave
    $script:smapiLog = Join-Path (Split-Path ([IO.Path]::GetFullPath((Join-Path $status.labRoot $status.persistentSavesPath))) -Parent) 'ErrorLogs\SMAPI-latest.txt'
    return $status
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'GmcmArrivalRow'
    $script:provider = Join-Path $work 'GenericModConfigMenu'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\gmcm-number-recipe\GmcmArrivalRow') -Destination $mod
    Copy-Item -Recurse -LiteralPath $Gmcm -Destination $script:provider
    Add-ProviderIdentity 'GenericModConfigMenu' $script:provider

    $manifest = Get-Content -Raw -LiteralPath (Join-Path $script:provider 'manifest.json') | ConvertFrom-Json
    Assert-That ($manifest.UniqueID -ceq 'spacechase0.GenericModConfigMenu' -and $manifest.Version -ceq '1.16.0') "the selected GMCM is $($manifest.UniqueID) $($manifest.Version), expected 1.16.0."
    # GMCM writes its own config.json on first launch. Writing its default bytes first keeps the staged copy unchanged.
    [IO.File]::WriteAllText((Join-Path $script:provider 'config.json'), "{`r`n  `"OpenMenuKey`": `"None`",`r`n  `"ScrollSpeed`": 120`r`n}", $utf8)
    # The default config in the bytes SMAPI keeps after ReadConfig: no BOM, CRLF, no final newline.
    [IO.File]::WriteAllText((Join-Path $mod 'config.json'), "{`r`n  `"ArrivalRow`": 15`r`n}", $utf8)

    Write-Step 'Check, build and package the mod once.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'check' @('project', 'check', $mod, '--json') | Out-Null
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package-default')
    $ready = Join-Path $work 'package-default\GmcmArrivalRow'
    Assert-That ((Read-ArrivalRow (Join-Path $ready 'config.json')) -eq 15) 'the package does not contain the default config.'
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Default: ArrivalRow 15 at Farm 64,15.'
    $status = Start-GmcmReview 'default' $ready
    $target = @($status.artifacts | Where-Object { $_.role -ceq 'target' -and $_.uniqueId -ceq $modId })
    Assert-That ($target.Count -eq 1) 'the reviewed target is not in the status.'
    $stagedConfig = Join-Path ([IO.Path]::GetFullPath((Join-Path $status.labRoot $target[0].stagingPath))) 'config.json'
    Assert-That ((Get-ModState 'default') -eq 15) 'the default ArrivalRow is not 15.'
    Assert-That ((Get-Position 'default') -ceq 'Farm 64,15') "the player is at $(Get-Position 'default-retry'), expected Farm 64,15."

    $open = Invoke-SdvKit 'open-gmcm' @('project', 'review', 'command', 'gmcm_arrival_row_open', '--json')
    Assert-That ($open.Json.commandWritten -eq $true) 'the open command did not run.'
    Start-Sleep -Seconds 1
    Get-MenuRevision 'opened' | Out-Null
    Save-ViewportScreenshot 'default'

    Write-Step 'Slider moves stay unsaved: the mod must keep ArrivalRow 15.'
    $click = Invoke-Tool 'click-middle' 'stardew_input_click' @{ x = $SliderMidX; y = $SliderY; button = 'MouseLeft'; count = 1; uiRevision = (Get-MenuRevision 'before-middle') } -OptIn '--allow-input'
    Assert-That ($click.Json.succeeded -eq $true -and $click.Json.released -eq $true) 'the middle click did not complete.'
    Save-ViewportScreenshot 'middle'
    Assert-That ((Get-ModState 'middle') -eq 15) 'the value changed before Save.'

    $offStepX = [int][Math]::Round(($SliderMidX + $SliderMaxX) / 2, [MidpointRounding]::AwayFromZero)
    $drag = Invoke-Tool 'drag-off-step' 'stardew_input_drag' @{ x = $SliderMidX; y = $SliderY; endX = $offStepX; endY = $SliderY; button = 'MouseLeft'; durationTicks = 6; uiRevision = (Get-MenuRevision 'before-off-step') } -OptIn '--allow-input'
    Assert-That ($drag.Json.succeeded -eq $true -and $drag.Json.released -eq $true) 'the off-step drag did not complete.'
    Save-ViewportScreenshot 'off-step'
    Assert-That ((Get-ModState 'off-step') -eq 15) 'the value changed before Save.'

    # The handle snapped to a valid step after the off-step drag; drag from there to beyond the maximum tick.
    $drag = Invoke-Tool 'drag-maximum' 'stardew_input_drag' @{ x = $offStepX; y = $SliderY; endX = $SliderMaxDragX; endY = $SliderY; button = 'MouseLeft'; durationTicks = 6; uiRevision = (Get-MenuRevision 'before-maximum') } -OptIn '--allow-input'
    Assert-That ($drag.Json.succeeded -eq $true -and $drag.Json.released -eq $true) 'the maximum drag did not complete.'
    Save-ViewportScreenshot 'unsaved-maximum'
    Assert-That ((Get-ModState 'unsaved-maximum') -eq 15) 'the value changed before Save.'
    Assert-That ((Read-ArrivalRow $stagedConfig) -eq 15) 'the staged config changed before Save.'

    Write-Step 'Save: GMCM writes row 19; SDVKit reports the change until it is reconciled.'
    # GMCM rewrites config.json during the click, so the click may end with exit 3 and reviewStagingOwnershipDrifted.
    $save = Invoke-Tool 'click-save' 'stardew_input_click' @{ x = $SaveX; y = $SaveY; button = 'MouseLeft'; count = 1; uiRevision = (Get-MenuRevision 'before-save') } -OptIn '--allow-input' -AllowExit @(0, 3)
    if ($save.Exit -eq 3) {
        Assert-That ((Get-Content -Raw -LiteralPath $save.File) + (Get-Content -Raw -LiteralPath ($save.File -replace '\.json$', '.stderr.txt') -ErrorAction SilentlyContinue) -match 'reviewStagingOwnershipDrifted') 'the Save click failed for an unexpected reason.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $saved = try { Read-ArrivalRow $stagedConfig } catch { $null }
    } until ($saved -eq 19 -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($saved -eq 19) "the saved config holds ArrivalRow $saved, expected 19."

    $drift = Invoke-SdvKit 'expected-drift' @('project', 'review', 'menu', '--json') -AllowExit @(3)
    Assert-That ($drift.Json.errorCode -ceq 'reviewStagingOwnershipDrifted') "expected reviewStagingOwnershipDrifted, got $($drift.Json.errorCode)."
    $reconcile = Invoke-SdvKit 'config-reconcile' @('project', 'review', 'config-reconcile', '--mod', $modId, '--json')
    Assert-That ($reconcile.Json.state -ceq 'reconciled') 'the saved config was not reconciled.'
    $export = [IO.Path]::GetFullPath((Join-Path $context.Lab $reconcile.Json.reconciliation.exportPath))
    Assert-That ((Read-ArrivalRow $export) -eq 19) 'the reconciled export is not row 19.'
    Invoke-Tool 'cursor-clear' 'stardew_input_cursor_clear' -OptIn '--allow-input' | Out-Null

    Assert-That ((Get-ModState 'saved') -eq 19) 'the mod does not use row 19 after Save.'
    Assert-That ((Get-Position 'saved') -ceq 'Farm 64,15') 'Save moved the player; the warp should only happen at day start.'
    Save-ViewportScreenshot 'saved'
    Stop-Review -Label 'default'

    Write-Step 'Restart with the reconciled config: the player must arrive at Farm 64,19.'
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package-configured')
    $configured = Join-Path $work 'package-configured\GmcmArrivalRow'
    [IO.File]::Copy($export, (Join-Path $configured 'config.json'), $true)
    foreach ($file in 'manifest.json', 'GmcmArrivalRow.dll') {
        Assert-That ((Get-FileHash (Join-Path $ready $file)).Hash -eq (Get-FileHash (Join-Path $configured $file)).Hash) "$file differs from the first package extraction."
    }

    Start-GmcmReview 'configured' $configured | Out-Null
    Assert-That ((Get-ModState 'restart') -eq 19) 'the restarted mod does not use row 19.'
    Assert-That ((Get-Position 'restart') -ceq 'Farm 64,19') "the player is at $(Get-Position 'restart-retry'), expected Farm 64,19."
    Save-ViewportScreenshot 'restart'
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', $modId, '--limit', '20', '--json') | Out-Null
    Write-Step 'Saved row 19 took effect after the restart.'

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

#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a GMCM checkbox (Enabled, default off) switches a start-of-day warp to Farm 64,15; prove that the
toggle stays unsaved, that Save writes it, and that a restart with the saved config warps.

.DESCRIPTION
Builds docs/examples/gmcm-checkbox-recipe/GmcmArrival, packages it once and reviews the extracted package with a copy
of Generic Mod Config Menu 1.16.0. The script:

1. checks the default (Enabled=false, player not at Farm 64,15) and opens the mod's GMCM page;
2. clicks the checkbox and requires the mod's value and the staged config to stay false;
3. clicks Save, expects SDVKit to report the staged config change (reviewStagingOwnershipDrifted), reconciles it
   with `project review config-reconcile`, and requires Enabled=true in the same process;
4. stops, extracts the unchanged ZIP again with the reconciled config, restarts and requires Farm 64,15.

GMCM's page exposes no menu components to SDVKit, so the checkbox and Save positions are screen coordinates. The
defaults were measured for GMCM 1.16.0 in SDVKit's default 1280x720 lab window; the script refuses to click when
the viewport differs. Whether the click landed on the checkbox is visible on the screenshots kept in the evidence
folder, and proven by the saved value.

Guide: docs/test-a-gmcm-option.md.

.EXAMPLE
pwsh scripts/acceptance/gmcm-checkbox.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Gmcm D:\Mods\GenericModConfigMenu
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    # A local Generic Mod Config Menu 1.16.0 folder. It is copied into the lab; the original is not changed.
    [Parameter(Mandatory)] [string] $Gmcm,
    [int] $CheckboxX = 660,
    [int] $CheckboxY = 141,
    [int] $SaveX = 692,
    [int] $SaveY = 657,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'gmcm-checkbox' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$modId = 'ExampleAuthor.GmcmArrival'
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-Enabled([string] $Path) {
    # Accepts exactly {"Enabled": true|false} with a JSON Boolean.
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $fields = @($document.RootElement.EnumerateObject())
        if ($fields.Count -ne 1 -or $fields[0].Name -cne 'Enabled' -or
            $fields[0].Value.ValueKind -notin [Text.Json.JsonValueKind]::True, [Text.Json.JsonValueKind]::False) {
            throw "Acceptance failed: $Path is not a valid Enabled config."
        }
        return $fields[0].Value.GetBoolean()
    }
    finally { $document.Dispose() }
}

function Get-ModState([string] $Label) {
    (Invoke-ModLogCommand "state-$Label" -Command 'gmcm_arrival_state' -Pattern 'Enabled=(true|false)' -Log $script:smapiLog)[1] -ceq 'true'
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'GmcmArrival'
    $script:provider = Join-Path $work 'GenericModConfigMenu'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\gmcm-checkbox-recipe\GmcmArrival') -Destination $mod
    Copy-GmcmProvider -Source $Gmcm -Destination $script:provider
    # The default config in the bytes SMAPI keeps after ReadConfig: no BOM, CRLF, no final newline.
    [IO.File]::WriteAllText((Join-Path $mod 'config.json'), "{`r`n  `"Enabled`": false`r`n}", $utf8)

    Write-Step 'Check, build and package the mod once.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'check' @('project', 'check', $mod, '--json') | Out-Null
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package-default')
    $ready = Join-Path $work 'package-default\GmcmArrival'
    Assert-That ((Read-Enabled (Join-Path $ready 'config.json')) -eq $false) 'the package does not contain the default config.'
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Default: Enabled=false, so no arrival warp.'
    $status = Start-CompanionReview 'default' $ready -Companion $script:provider
    $script:smapiLog = Get-SmapiLogPath $status
    $target = @($status.artifacts | Where-Object { $_.role -ceq 'target' -and $_.uniqueId -ceq $modId })
    Assert-That ($target.Count -eq 1) 'the reviewed target is not in the status.'
    $stagedConfig = Join-Path ([IO.Path]::GetFullPath((Join-Path $status.labRoot $target[0].stagingPath))) 'config.json'
    Assert-That ((Get-ModState 'default') -eq $false) 'the default Enabled is not false.'
    $position = Get-PlayerPosition 'default'
    Assert-That ($position -cne 'Farm 64,15') "the player is at $position without the option enabled."

    $open = Invoke-SdvKit 'open-gmcm' @('project', 'review', 'command', 'gmcm_arrival_open', '--json')
    Assert-That ($open.Json.commandWritten -eq $true) 'the open command did not run.'
    Start-Sleep -Seconds 1
    Get-GmcmMenuRevision 'opened' | Out-Null
    Save-ViewportScreenshot 'default'

    Write-Step 'Checking the box stays unsaved: the mod must keep Enabled=false.'
    $click = Invoke-Tool 'click-checkbox' 'stardew_input_click' @{ x = $CheckboxX; y = $CheckboxY; button = 'MouseLeft'; count = 1; uiRevision = (Get-GmcmMenuRevision 'before-checkbox') } -OptIn '--allow-input'
    Assert-That ($click.Json.succeeded -eq $true -and $click.Json.released -eq $true) 'the checkbox click did not complete.'
    Save-ViewportScreenshot 'checked'
    Assert-That ((Get-ModState 'checked') -eq $false) 'the value changed before Save.'
    Assert-That ((Read-Enabled $stagedConfig) -eq $false) 'the staged config changed before Save.'

    Write-Step 'Save: GMCM writes Enabled=true; SDVKit reports the change until it is reconciled.'
    $export = Save-GmcmPage -X $SaveX -Y $SaveY -ModId $modId -StagedConfig $stagedConfig -ReadValue ${function:Read-Enabled} -Expected $true
    Assert-That ((Get-ModState 'saved') -eq $true) 'the mod is not enabled after Save.'
    Assert-That ((Get-PlayerPosition 'saved') -ceq $position) 'Save moved the player; the warp should only happen at day start.'
    Save-ViewportScreenshot 'saved'
    Stop-Review -Label 'default'

    Write-Step 'Restart with the reconciled config: the player must arrive at Farm 64,15.'
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package-configured')
    $configured = Join-Path $work 'package-configured\GmcmArrival'
    [IO.File]::Copy($export, (Join-Path $configured 'config.json'), $true)
    foreach ($file in 'manifest.json', 'GmcmArrival.dll') {
        Assert-That ((Get-FileHash (Join-Path $ready $file)).Hash -eq (Get-FileHash (Join-Path $configured $file)).Hash) "$file differs from the first package extraction."
    }

    Start-CompanionReview 'configured' $configured -Companion $script:provider | Out-Null
    Assert-That ((Get-ModState 'restart') -eq $true) 'the restarted mod is not enabled.'
    Assert-That ((Get-PlayerPosition 'restart') -ceq 'Farm 64,15') "the player is at $(Get-PlayerPosition 'restart-retry'), expected Farm 64,15."
    Save-ViewportScreenshot 'restart'
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', $modId, '--limit', '20', '--json') | Out-Null
    Write-Step 'The saved option took effect after the restart.'

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

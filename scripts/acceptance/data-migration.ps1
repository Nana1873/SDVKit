#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a mod's save data survives stops and restarts, a broken upgrade keeps the old data, and the
corrected upgrade migrates it exactly once.

.DESCRIPTION
Uses docs/recipes/data-migration through scripts/prepare-data-migration-recipe.ps1, which builds and packages three
artifacts of the same mod (v1 1.0.0, broken v2 2.0.0-beta.1, corrected v2 2.0.0). The script reviews them one after
another against the same retained test save:

1. v1 creates the ledger (credits 37, crop (O)634, claimed) and grants five Stone once; the fixture is saved;
2. broken v2 refuses to migrate and leaves schema 1, the values and the Stone unchanged;
3. corrected v2 migrates to schema 2 (balance 37, crop (O)634, claimed, one migration) without another reward;
4. after a save and a restart, v2 loads the existing ledger without migrating again.

Each restart is a stop without reset followed by a new start of the next artifact. The test save is reset only at
the end. Guide: docs/test-a-data-migration.md.

.EXAMPLE
pwsh scripts/acceptance/data-migration.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'data-migration' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

function Read-Log([string] $Path) {
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try { [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

function Start-Artifact([string] $Label, [string] $Target) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $Target, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    $status = Wait-ReviewReady -TestSave
    $script:smapiLog = Get-SmapiLogPath $status
}

function Get-Ledger([string] $Label) {
    # The migration recipe's selected status line, such as "artifact=2.0.0; schema=2; balance=37; ...; stone=5".
    (Invoke-ModLogCommand "status-$Label" -Command 'migration-recipe status' -Pattern '(?m)Migration recipe (artifact=[^\r\n]+?)\.\s*$' -Log $script:smapiLog)[1]
}

function Save-Fixture([string] $Label) {
    $save = Invoke-Tool "fixture-save-$Label" 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
    Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) "the fixture save '$Label' did not complete."
}

$failed = $false
try {
    Write-Step 'Build and package the three artifacts.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    $recipe = Join-Path $context.Evidence 'recipe'
    & (Join-Path $PSScriptRoot '..\prepare-data-migration-recipe.ps1') -Sdvkit $context.SdvKit -GamePath $context.GamePath -RecipeRoot $recipe | Out-Null
    $artifacts = Get-Content -Raw -LiteralPath (Join-Path $recipe 'artifacts.json') | ConvertFrom-Json
    $v1 = ($artifacts | Where-Object variant -eq 'v1').target
    $broken = ($artifacts | Where-Object variant -eq 'v2-broken').target
    $v2 = ($artifacts | Where-Object variant -eq 'v2').target
    Assert-That (@($artifacts.dllSha256 | Select-Object -Unique).Count -eq 3) 'the three artifacts are not distinct.'
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'v1 creates the ledger and the one-time reward.'
    Start-Artifact 'v1' $v1
    $ledger = Get-Ledger 'v1'
    Assert-That ($ledger -ceq 'artifact=1.0.0; schema=1; credits=37; crop=(O)634; claimed=True; stone=5') "the v1 ledger is '$ledger'."
    Save-Fixture 'v1'
    Stop-Review -Label 'v1'

    Write-Step 'Broken v2 refuses to migrate and keeps the old data.'
    Start-Artifact 'broken' $broken
    Assert-That ((Read-Log $script:smapiLog) -match 'Broken v2 cannot migrate schema 1; original data was retained\.') 'the broken upgrade did not report its refusal.'
    $ledger = Get-Ledger 'broken'
    Assert-That ($ledger -ceq 'artifact=2.0.0-beta.1; schema=1; credits=37; crop=(O)634; claimed=True; stone=5') "the broken v2 changed the data: '$ledger'."
    Stop-Review -Label 'broken'

    Write-Step 'Corrected v2 migrates once.'
    Start-Artifact 'v2' $v2
    Assert-That ((Read-Log $script:smapiLog) -match 'Migrated v1 ledger to v2') 'the corrected upgrade did not migrate.'
    $ledger = Get-Ledger 'v2'
    $migrated = 'artifact=2.0.0; schema=2; balance=37; crop=(O)634; claimed=True; migrations=1; stone=5'
    Assert-That ($ledger -ceq $migrated) "the migrated ledger is '$ledger'."
    Invoke-SdvKit 'diagnostics-v2' @('project', 'review', 'diagnostics', '--mod', 'SDVKit.DataMigrationRecipe', '--limit', '30', '--json') | Out-Null
    Save-Fixture 'v2'
    Stop-Review -Label 'v2'

    Write-Step 'Restart v2: the saved ledger loads without another migration or reward.'
    Start-Artifact 'v2-again' $v2
    $log = Read-Log $script:smapiLog
    Assert-That ($log -match 'Loaded existing v2 ledger' -and $log -notmatch 'Migrated v1 ledger') 'the restart did not load the existing v2 ledger.'
    $ledger = Get-Ledger 'v2-again'
    Assert-That ($ledger -ceq $migrated) "after the restart the ledger is '$ledger'."
    Write-Step 'The migrated data survived the restart.'

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

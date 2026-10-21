#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: move exact quantities of Stone between the backpack and a chest with `stardew_container_transfer`,
read both sides with `stardew_container_get`, refuse a stale request, and prove that the chest contents survive a
save, stop and restart.

.DESCRIPTION
Builds docs/examples/container-recipe/InventoryReward, packages it once and reviews the extracted package. The mod
grants ten Stone once per save and places and opens one marked wooden chest on command. The script:

1. checks that the backpack holds exactly ten Stone and the chest is empty (36 slots) with nothing held;
2. deposits three Stone and requires player/chest/held 10/0/0 -> 7/3/0, then withdraws one and requires 8/2/0;
3. repeats the first deposit request with its old identities and requires containerTransferSelectionStale and an
   unchanged 8/2/0;
4. closes the chest, saves the fixture, stops without reset, restarts the same package, reopens the chest and
   requires 8/2/0 again. A reward granted twice would show as 18 Stone.

Guide: docs/test-a-container-transfer.md.

.EXAMPLE
pwsh scripts/acceptance/container.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'container' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$stone = '(O)390'

function Read-Container([string] $Label) {
    $read = Invoke-Tool "container-$Label" 'stardew_container_get' @{}
    Assert-That ($read.Json.state -ceq 'ready' -and $read.Json.data.complete -eq $true) "the container read '$Label' is not ready and complete."
    return $read.Json.data
}

function Get-Stone($Side) {
    [int](@($Side.slots | Where-Object { $_.item.qualifiedItemId -ceq $stone } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
}

function Get-Totals($Container) {
    # Player, chest and held Stone as "10/0/0".
    $held = if ($Container.heldItem.state -ceq 'occupied' -and $Container.heldItem.item.qualifiedItemId -ceq $stone) { [int]$Container.heldItem.item.stack } else { 0 }
    "$(Get-Stone $Container.player)/$(Get-Stone $Container.container)/$held"
}

function Get-StoneSlot($Side) {
    $slots = @($Side.slots | Where-Object { $_.item.qualifiedItemId -ceq $stone })
    Assert-That ($slots.Count -eq 1) "expected one Stone stack on the $($Side.side) side, found $($slots.Count)."
    return $slots[0]
}

function New-TransferRequest($Container, [string] $Direction, [int] $Quantity) {
    $side = if ($Direction -ceq 'deposit') { $Container.player } else { $Container.container }
    $slot = Get-StoneSlot $side
    @{
        direction = $Direction; sourceSlot = [int]$slot.slot; quantity = $Quantity; qualifiedItemId = $stone
        selectionIdentity = $Container.selectionIdentity; containerRevision = $Container.containerRevision
        instanceIdentity = $slot.identity.instanceIdentity; itemRevision = $slot.identity.itemRevision
    }
}

function Invoke-Transfer([string] $Label, [hashtable] $Request, [int] $Quantity) {
    $result = Invoke-Tool $Label 'stardew_container_transfer' $Request -OptIn '--allow-container-transfer'
    Assert-That ($result.Json.state -ceq 'completed' -and $result.Json.data.outcome -ceq 'completed') "the $Label transfer is '$($result.Json.data.outcome)'; never replay a partial or uncertain transfer."
    Assert-That ($result.Json.data.observedQuantity -eq $Quantity) "the $Label transfer moved $($result.Json.data.observedQuantity), expected $Quantity."
}

function Open-Chest([string] $Label) {
    Invoke-ModLogCommand $Label -Command 'inventory_reward_open' -Pattern 'Opened the chest' -Log $script:smapiLog | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $menu = (Invoke-SdvKit "$Label-menu" @('project', 'review', 'menu', '--json')).Json
    } until (($menu.menuOpen -and @($menu.menus | ForEach-Object type) -contains 'StardewValley.Menus.ItemGrabMenu') -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($menu.menuOpen -eq $true) 'the chest menu did not open.'
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'InventoryReward'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\container-recipe\InventoryReward') -Destination $mod

    Write-Step 'Check, build and package the mod once.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'check' @('project', 'check', $mod, '--json') | Out-Null
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package')
    $ready = Join-Path $work 'package\InventoryReward'
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Start: ten Stone from the one-time reward, then an empty chest.'
    Invoke-SdvKit 'start' @('project', 'review', 'start', $ready, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    $status = Wait-ReviewReady -TestSave
    $script:smapiLog = Get-SmapiLogPath $status
    $inventory = (Invoke-SdvKit 'inventory-start' @('project', 'review', 'inventory', '--json')).Json.data
    Assert-That ((Get-Stone $inventory) -eq 10) "the backpack holds $(Get-Stone $inventory) Stone, expected 10."
    Invoke-ModLogCommand 'prepare-chest' -Command 'inventory_reward_chest' -Pattern 'Placed an empty chest' -Log $script:smapiLog | Out-Null
    Open-Chest 'open'
    Save-ViewportScreenshot 'opened'
    $before = Read-Container 'before'
    Assert-That ($before.container.capacity -eq 36 -and (Get-Totals $before) -ceq '10/0/0' -and $before.heldItem.state -ceq 'empty') "the opened chest is not empty with nothing held (totals $(Get-Totals $before))."

    Write-Step 'Deposit three Stone, withdraw one.'
    $deposit = New-TransferRequest $before 'deposit' 3
    Invoke-Transfer 'deposit' $deposit 3
    $afterDeposit = Read-Container 'after-deposit'
    Assert-That ((Get-Totals $afterDeposit) -ceq '7/3/0' -and $afterDeposit.heldItem.state -ceq 'empty') "after the deposit the totals are $(Get-Totals $afterDeposit), expected 7/3/0."
    Invoke-Transfer 'withdraw' (New-TransferRequest $afterDeposit 'withdraw' 1) 1
    $afterWithdraw = Read-Container 'after-withdraw'
    Assert-That ((Get-Totals $afterWithdraw) -ceq '8/2/0' -and $afterWithdraw.heldItem.state -ceq 'empty') "after the withdrawal the totals are $(Get-Totals $afterWithdraw), expected 8/2/0."
    Save-ViewportScreenshot 'transferred'

    Write-Step 'A stale request is refused and changes nothing.'
    $stale = Invoke-Tool 'stale-deposit' 'stardew_container_transfer' $deposit -OptIn '--allow-container-transfer' -AllowExit @(0, 3)
    Assert-That ($stale.Exit -eq 3 -and $stale.Json.state -ceq 'refused' -and $stale.Json.errorCode -ceq 'containerTransferSelectionStale' -and $null -eq $stale.Json.data) "the stale request was not refused without dispatch as containerTransferSelectionStale (exit $($stale.Exit), state $($stale.Json.state), code $($stale.Json.errorCode))."
    Assert-That ((Get-Totals (Read-Container 'after-stale')) -ceq '8/2/0') 'the refused request changed the totals.'

    Write-Step 'Close the chest, save, stop without reset and restart.'
    Invoke-Tool 'close-chest' 'stardew_input_press' @{ button = 'Escape' } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1
    Assert-That ((Invoke-SdvKit 'menu-closed' @('project', 'review', 'menu', '--json')).Json.menuOpen -eq $false) 'the chest menu is still open.'
    $save = Invoke-Tool 'fixture-save' 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
    Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) 'the fixture save did not complete.'
    Stop-Review -Label 'before-restart'

    Invoke-SdvKit 'start-restart' @('project', 'review', 'start', $ready, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    $status = Wait-ReviewReady -TestSave
    $script:smapiLog = Get-SmapiLogPath $status
    Open-Chest 'reopen'
    $restart = Read-Container 'restart'
    Assert-That ((Get-Totals $restart) -ceq '8/2/0') "after the restart the totals are $(Get-Totals $restart), expected 8/2/0 (18 in total would mean the reward repeated)."
    Save-ViewportScreenshot 'restart'
    Invoke-Tool 'close-restart' 'stardew_input_press' @{ button = 'Escape' } -OptIn '--allow-input' | Out-Null
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', 'ExampleAuthor.InventoryReward', '--limit', '20', '--json') | Out-Null
    Write-Step 'The chest contents survived the save and restart.'

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

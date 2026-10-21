#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: a per-player multiplayer mod in a network-2 host and farmhand review and, as far as the game allows, on a split-screen screen.

.DESCRIPTION
Builds the three sample mods in docs/examples/multiplayer-recipe, packages each once and reviews the extracted
packages. The mod gives one Stone to the player who presses F7 and shows that player's inventory, persisted modData and
last inventory event on F8. The script runs these sections in order:

1. broken: the deliberately broken target collapses both players into the host. The host's F7 gives the host one Stone,
   the farmhand's F7 gives the host another one and the farmhand none, and the farmhand's F8 shows the host's farmer.
2. network: the corrected target in a network-2 review.
   - Host F7, farmhand F7 twice: host Stone 1, farmhand Stone 2; each F8 dialog shows its own farmer, actual count,
     persisted count and last event count.
   - The host prepares one marked chest with two Stone (probe companion). The farmhand walks one tile with
     stardew_world_move, opens the chest with stardew_world_act, and withdraws one Stone with
     stardew_container_transfer; the host then opens the chest itself and reads one Stone in the chest and one in its backpack.
   - The farmhand leaves and rejoins: the host keeps its session, the farmhand gets a new one, and the host's
     inventory read is refused while the farmhand is away.
   - The host saves the fixture, the review stops without reset and starts again from the kept save: new processes,
     the same farmers, host Stone 1, farmhand Stone 3, and both F8 dialogs report persisted=True.
3. split: the corrected target on a single test-save review with the second screen joined. F7 on screen 0 once and on
   screen 1 twice gives host 1 and farmhand 2 with each screen's own F8 dialog. Screen 1 walks to its cabin door with
   stardew_world_move and out with a key chord, the host warps next to it on the Farm and prepares the probe chest, screen 1
   opens it with the probe command, reads it and withdraws one Stone with stardew_container_transfer, and screen 0 reads
   the chest. stardew_world_act on screen 1 is refused with worldActionCursorAimUnavailable (Stardew forces gamepad
   controls on that screen). Screen leave and replacement and persistence after a restart stay manual; see
   docs/test-a-multiplayer-mod.md.

Every tool call and every read names its role or screen, so one player's state is never inferred from the other's.

While developing SDVKit, -Only runs only the named sections (broken, network, split). Such a partial run writes its
evidence under multiplayer-partial and never counts as acceptance; PRs and releases need a full run.

Guide: docs/test-a-multiplayer-mod.md.

.EXAMPLE
pwsh scripts/acceptance/multiplayer.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck,
    [string[]] $Only
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$sections = @('broken', 'network', 'split')
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
if ($PSBoundParameters.ContainsKey('Only') -and $Only.Count -eq 0) { throw "-Only needs at least one section. Use: $($sections -join ', ')." }
$unknown = @($Only | Where-Object { $_ -cnotin $sections })
if ($unknown.Count -gt 0) { throw "Unknown -Only section(s): $($unknown -join ', '). Use: $($sections -join ', ')." }
$partial = $Only.Count -gt 0
$name = if ($partial) { 'multiplayer-partial' } else { 'multiplayer' }
$context = Initialize-Acceptance -Name $name -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

function Test-Section([string] $Name) { -not $partial -or $Name -in $Only }

$stone = '(O)390'
$dialogPattern = 'farmer=(-?\d+); persisted=(True|False); stone=(\w+); actual=(\d+); eventFarmer=(-?\d+); eventStone=(\d+)'

function Get-Selection([string] $Player) {
    # A player is a network role ('host', 'farmhand') or a split-screen screen ('screen0', 'screen1').
    if ($Player -like 'screen*') { return @('--screen', $Player.Substring(6)) }
    return @('--topology', 'network-2', '--role', $Player)
}

function Invoke-PlayerTool([string] $Label, [string] $Player, [string] $Tool, [hashtable] $Arguments = @{}, [string[]] $OptIn = @(), [int[]] $AllowExit = @(0)) {
    $json = $Arguments | ConvertTo-Json -Compress -Depth 20
    Invoke-SdvKit "$Label-$Player" (@('project', 'review', 'call', $Tool, $json) + (Get-Selection $Player) + $OptIn) -AllowExit $AllowExit
}

function Get-PlayerRuntime([string] $Label, [string] $Player) {
    $result = (Invoke-PlayerTool "runtime-$Label" $Player 'stardew_runtime_get').Json
    if ($Player -like 'screen*') { Assert-That ($result.screen.screenId -eq [int]$Player.Substring(6)) "the runtime read '$Label' is not bound to $Player." }
    else { Assert-That ($result.role -ceq $Player) "the runtime read '$Label' came from role '$($result.role)', not $Player." }
    $result.runtime
}

function Get-PlayerInventory([string] $Label, [string] $Player) {
    (Invoke-SdvKit "inventory-$Label-$Player" (@('project', 'review', 'inventory', '--json') + (Get-Selection $Player))).Json.data
}

function Get-StoneCount($Slots) {
    [int](@($Slots | Where-Object { $_.item.qualifiedItemId -ceq $stone } | ForEach-Object { $_.item.stack }) | Measure-Object -Sum).Sum
}

function Get-PlayerStone([string] $Label, [string] $Player) {
    Get-StoneCount (Get-PlayerInventory $Label $Player).slots
}

function Wait-PlayerStone([string] $Label, [string] $Player, [int] $Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Seconds 1
        $count = Get-PlayerStone $Label $Player
    } until ($count -eq $Expected -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($count -eq $Expected) "$Player holds $count Stone, expected $Expected."
}

function Get-PlayerMenu([string] $Label, [string] $Player) {
    (Invoke-SdvKit "menu-$Label-$Player" (@('project', 'review', 'menu', '--json') + (Get-Selection $Player))).Json
}

function Invoke-PlayerPress([string] $Label, [string] $Player, [string] $Button) {
    $press = Invoke-PlayerTool "press-$Label" $Player 'stardew_input_press' @{ button = $Button } -OptIn '--allow-input'
    Assert-That ($press.Json.succeeded -eq $true) "the $Button press for $Player did not complete."
}

function Read-ResultDialog([string] $Label, [string] $Player) {
    # F8 shows the mod's result for the player who pressed it. The dialog text is part of the menu read.
    Invoke-PlayerPress "$Label-f8" $Player 'F8'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 700
        $menu = Get-PlayerMenu "$Label-dialog" $Player
        $matched = ($menu | ConvertTo-Json -Depth 30 -Compress) -match $dialogPattern
    } until ($matched -or [DateTime]::UtcNow -gt $deadline)
    Assert-That $matched "the F8 dialog of $Player did not show the result text."
    $dialog = [pscustomobject]@{ Farmer = $Matches[1]; Persisted = $Matches[2]; Stone = $Matches[3]; Actual = [int]$Matches[4]; EventFarmer = $Matches[5]; EventStone = [int]$Matches[6] }
    Close-Dialogue "$Label-close" $Player
    return $dialog
}

function Close-Dialogue([string] $Label, [string] $Player) {
    # Escape does not close a DialogueBox; each click on it finishes the text or turns the page. Decide every click from a fresh read.
    foreach ($attempt in 1..8) {
        $menu = Get-PlayerMenu "$Label-$attempt" $Player
        if ($menu.menuOpen -eq $false) { return }
        $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
        Assert-That ($box.Count -eq 1) "expected a dialogue for $Player, found $(@($menu.menus | ForEach-Object type) -join ', ')."
        $point = Get-ComponentCenter $box[0]
        $click = Invoke-PlayerTool "click-$Label-$attempt" $Player 'stardew_input_click' @{ x = $point.X; y = $point.Y; button = 'MouseLeft'; count = 1; uiRevision = [string]$menu.uiRevision } -OptIn '--allow-input'
        Assert-That ($click.Json.succeeded -eq $true) "the click on the $Player dialogue did not complete."
        Start-Sleep -Milliseconds 1500
    }
    Assert-That ((Get-PlayerMenu "$Label-closed" $Player).menuOpen -eq $false) "the $Player dialogue did not close."
}

function Assert-Dialog($Dialog, [string] $Player, [string] $FarmerId, [int] $Count) {
    Assert-That ($Dialog.Farmer -ceq $FarmerId -and $Dialog.EventFarmer -ceq $FarmerId) "the $Player dialog shows farmer $($Dialog.Farmer)/event farmer $($Dialog.EventFarmer), not $FarmerId."
    Assert-That ($Dialog.Actual -eq $Count -and $Dialog.EventStone -eq $Count -and $Dialog.Persisted -ceq 'True' -and $Dialog.Stone -ceq "$Count") "the $Player dialog shows persisted=$($Dialog.Persisted) stone=$($Dialog.Stone) actual=$($Dialog.Actual) eventStone=$($Dialog.EventStone), expected $Count everywhere."
}

function Invoke-PlayerLogCommand([string] $Label, [string] $Player, [string] $Command, [string] $Pattern) {
    # A console command of the reviewed mods in one player's process: a network role through its own profile log, screen 0
    # unbound, another screen through SMAPI's screen=<id> argument in the command itself.
    $isScreen = $Player -like 'screen*'
    $log = if ($isScreen) { $script:singleLog } else { Get-SmapiLogPath -Role $Player }
    $selection = if ($isScreen) { @() } else { Get-Selection $Player }
    Invoke-ModLogCommand "$Label-$Player" -Command $Command -Pattern $Pattern -Log $log -Selection $selection -TimeoutSeconds 15
}

function Get-Area([string] $Label, [string] $Player, [int] $X, [int] $Y, [int] $Width, [int] $Height) {
    (Invoke-SdvKit "world-$Label-$Player" (@('project', 'review', 'world', $X, $Y, $Width, $Height, '--json') + (Get-Selection $Player))).Json.data.tiles
}

function Test-Free($Tile) { $null -ne $Tile -and $Tile.objectState -ceq 'missing' -and $null -eq $Tile.soil -and $null -eq $Tile.feature }

function Start-Network([string] $Label, [string] $Target, [string] $Companion) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $Target, '--game-path', $context.GamePath, '--topology', 'network-2', '--companion', $Companion, '--json') | Out-Null
    Wait-ReviewReady -Topology 'network-2'
}

function Get-FarmerId([string] $Label, [string] $Player) {
    [string](Get-PlayerRuntime $Label $Player).localPlayer.data.playerId
}

function Read-Chest([string] $Label, [string] $Player) {
    $read = Invoke-PlayerTool "container-$Label" $Player 'stardew_container_get'
    Assert-That ($read.Json.state -ceq 'ready' -and $read.Json.data.complete -eq $true) "the container read '$Label' for $Player is not ready and complete."
    return $read.Json.data
}

function Wait-ChestMenu([string] $Label, [string] $Player) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $menu = Get-PlayerMenu "$Label-chest-menu" $Player
    } until (($menu.menuOpen -and @($menu.menus | ForEach-Object type) -contains 'StardewValley.Menus.ItemGrabMenu') -or [DateTime]::UtcNow -gt $deadline)
    Assert-That ($menu.menuOpen -eq $true) "the chest menu did not open for $Player."
}

function Close-ChestMenu([string] $Label, [string] $Player) {
    Invoke-PlayerPress "$Label-close-chest" $Player 'Escape'
    Start-Sleep -Seconds 1
    Assert-That ((Get-PlayerMenu "$Label-chest-closed" $Player).menuOpen -eq $false) "the chest menu is still open for $Player."
}

function Get-Slot($Side) {
    $slots = @($Side.slots | Where-Object { $_.item.qualifiedItemId -ceq $stone })
    Assert-That ($slots.Count -eq 1) "expected one Stone stack on the $($Side.side) side, found $($slots.Count)."
    return $slots[0]
}

function Invoke-Withdraw([string] $Label, [string] $Player, [int] $Quantity) {
    $chest = Read-Chest "$Label-before" $Player
    $slot = Get-Slot $chest.container
    $request = @{ direction = 'withdraw'; sourceSlot = [int]$slot.slot; quantity = $Quantity; qualifiedItemId = $stone
        selectionIdentity = $chest.selectionIdentity; containerRevision = $chest.containerRevision
        instanceIdentity = $slot.identity.instanceIdentity; itemRevision = $slot.identity.itemRevision }
    $result = Invoke-PlayerTool "transfer-$Label" $Player 'stardew_container_transfer' $request -OptIn '--allow-container-transfer'
    Assert-That ($result.Json.state -ceq 'completed' -and $result.Json.data.outcome -ceq 'completed' -and $result.Json.data.observedQuantity -eq $Quantity) "the withdrawal for $Player returned $($result.Json.state)/$($result.Json.data.outcome), observed $($result.Json.data.observedQuantity); never replay a partial or uncertain transfer."
    return $chest
}

function Build-Package([string] $Work, [string] $Variant, [string] $Name) {
    $source = Join-Path $Work "$Variant\$Name"
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot "..\..\docs\examples\multiplayer-recipe\$Variant\$Name") -Destination $source
    Invoke-SdvKit "check-$Variant" @('project', 'check', $source, '--json') | Out-Null
    Invoke-SdvKit "build-$Variant" @('project', 'build', $source, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit "package-$Variant" @('project', 'package', $source, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $source $package.Json.archive)).Path
    $extract = Join-Path $Work "$Variant-ready"
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    return (Join-Path $extract $Name)
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    Write-Step 'Check, build and package the broken target, the corrected target and the chest probe once each.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    $brokenReady = Build-Package $work 'broken' 'PerPlayerInventoryProof'
    $fixedReady = Build-Package $work 'fixed' 'PerPlayerInventoryProof'
    $probeReady = Build-Package $work 'probe' 'PerPlayerProofChestProbe'
    Add-ProviderIdentity 'broken' $brokenReady
    Add-ProviderIdentity 'fixed' $fixedReady
    Add-ProviderIdentity 'probe' $probeReady
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null
    if ($partial) { Write-Step "Partial run ($($Only -join ', ')): development only, not acceptance evidence." }

    if (Test-Section 'broken') {
        Write-Step 'Broken target: the farmhand request goes to the host.'
        Start-Network 'broken' $brokenReady $probeReady | Out-Null
        $hostId = Get-FarmerId 'broken' 'host'
        $farmhandId = Get-FarmerId 'broken' 'farmhand'
        Assert-That ($hostId -cne $farmhandId) 'host and farmhand report the same farmer.'
        Assert-That ((Get-PlayerStone 'broken-base' 'host') -eq 0 -and (Get-PlayerStone 'broken-base' 'farmhand') -eq 0) 'the baseline already holds Stone.'
        Invoke-PlayerPress 'broken-host' 'host' 'F7'
        Wait-PlayerStone 'broken-host' 'host' 1
        Invoke-PlayerPress 'broken-farmhand' 'farmhand' 'F7'
        Wait-PlayerStone 'broken-farmhand' 'host' 2
        Start-Sleep -Seconds 3
        Assert-That ((Get-PlayerStone 'broken-after' 'farmhand') -eq 0) 'the farmhand received Stone from the broken target.'
        $dialog = Read-ResultDialog 'broken-farmhand' 'farmhand'
        Assert-That ($dialog.Farmer -ceq $hostId -and $dialog.Farmer -cne $farmhandId -and $dialog.Actual -eq 2) "the farmhand's F8 shows farmer $($dialog.Farmer) with $($dialog.Actual) Stone, expected the host $hostId with 2."
        Write-Step 'Broken target confirmed: host Stone 2, farmhand 0, and the farmhand dialog shows the host.'
        Stop-Review -Topology 'network-2' -Reset -Label 'broken'
    }

    if (Test-Section 'network') {
        Write-Step 'Corrected target: one Stone for the host, two for the farmhand.'
        $ready = Start-Network 'fixed' $fixedReady $probeReady
        $hostId = Get-FarmerId 'fixed' 'host'
        $farmhandId = Get-FarmerId 'fixed' 'farmhand'
        $launch = @{ host = $ready.network.host.launchId; farmhand = $ready.network.farmhand.launchId }
        Assert-That ($hostId -cne $farmhandId) 'host and farmhand report the same farmer.'
        Assert-That ((Get-PlayerStone 'fixed-base' 'host') -eq 0 -and (Get-PlayerStone 'fixed-base' 'farmhand') -eq 0) 'the baseline already holds Stone.'
        Invoke-PlayerPress 'host-1' 'host' 'F7'
        Wait-PlayerStone 'host-1' 'host' 1
        Invoke-PlayerPress 'farmhand-1' 'farmhand' 'F7'
        Wait-PlayerStone 'farmhand-1' 'farmhand' 1
        Invoke-PlayerPress 'farmhand-2' 'farmhand' 'F7'
        Wait-PlayerStone 'farmhand-2' 'farmhand' 2
        Assert-That ((Get-PlayerStone 'fixed-host' 'host') -eq 1) 'the farmhand requests changed the host inventory.'
        Assert-Dialog (Read-ResultDialog 'fixed-host' 'host') 'host' $hostId 1
        Assert-Dialog (Read-ResultDialog 'fixed-farmhand' 'farmhand') 'farmhand' $farmhandId 2
        Write-Step 'Host Stone 1 and farmhand Stone 2, each dialog with its own farmer.'

        Write-Step 'Chest: the host prepares it, the farmhand moves, opens it and withdraws one Stone, the host reads the effect.'
        $warp = Invoke-PlayerTool 'warp-host' 'host' 'stardew_lab_warp' @{ location = 'Farm'; x = 64; y = 16 } -OptIn '--allow-fixture-actions'
        Assert-That ($warp.Json.state -ceq 'ready' -and $warp.Json.lab.tileX -eq 64 -and $warp.Json.lab.tileY -eq 16) 'the host warp did not land on Farm 64,16.'
        Start-Sleep -Milliseconds 1500
        $prepared = Invoke-PlayerLogCommand 'prepare-chest' 'host' 'per-player-proof-chest prepare' 'Prepared owned proof chest at Farm \((\d+), (\d+)\) with Stone 2'
        $chestX = [int]$prepared[1]
        $chestY = [int]$prepared[2]
        # World reads cover the player's own location, so the farmhand first joins the host on the Farm.
        $join = Invoke-PlayerTool 'warp-farmhand-farm' 'farmhand' 'stardew_lab_warp' @{ location = 'Farm'; x = 64; y = 16 } -OptIn '--allow-fixture-actions'
        Assert-That ($join.Json.state -ceq 'ready' -and $join.Json.lab.locationId -ceq 'Farm') 'the farmhand did not warp to the Farm.'
        Start-Sleep -Milliseconds 1500
        # The farmhand's world read must show the chest (replication) and the neighbouring tiles it needs must be free.
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Seconds 1
            $area = @(Get-Area 'farmhand-chest' 'farmhand' ($chestX - 2) ($chestY - 2) 5 5)
        } until (@($area | Where-Object { $_.x -eq $chestX -and $_.y -eq $chestY -and $_.objectQualifiedItemId -ceq '(BC)130' }).Count -eq 1 -or [DateTime]::UtcNow -gt $deadline)
        Assert-That (@($area | Where-Object { $_.x -eq $chestX -and $_.y -eq $chestY -and $_.objectQualifiedItemId -ceq '(BC)130' }).Count -eq 1) 'the farmhand cannot see the chest the host prepared.'
        $byTile = @{}
        foreach ($tile in $area) { $byTile["$($tile.x),$($tile.y)"] = $tile }
        $approach = $null
        foreach ($direction in @(@{ name = 'down'; dx = 0; dy = 1 }, @{ name = 'right'; dx = 1; dy = 0 }, @{ name = 'left'; dx = -1; dy = 0 }, @{ name = 'up'; dx = 0; dy = -1 })) {
            # The farmhand starts two tiles from the chest, opposite to its walking direction, and walks one tile toward it.
            $near = $byTile["$($chestX - $direction.dx),$($chestY - $direction.dy)"]
            $far = $byTile["$($chestX - 2 * $direction.dx),$($chestY - 2 * $direction.dy)"]
            if ((Test-Free $near) -and (Test-Free $far) -and "$($near.x),$($near.y)" -cne '64,16' -and "$($far.x),$($far.y)" -cne '64,16') { $approach = @{ direction = $direction; near = $near; far = $far }; break }
        }
        Assert-That ($null -ne $approach) "no free approach to the chest at $chestX,$chestY."
        $warp = Invoke-PlayerTool 'warp-farmhand' 'farmhand' 'stardew_lab_warp' @{ location = 'Farm'; x = $approach.far.x; y = $approach.far.y } -OptIn '--allow-fixture-actions'
        Assert-That ($warp.Json.state -ceq 'ready' -and $warp.Json.lab.locationId -ceq 'Farm' -and $warp.Json.lab.tileX -eq $approach.far.x -and $warp.Json.lab.tileY -eq $approach.far.y) "the farmhand warp did not land on Farm $($approach.far.x),$($approach.far.y)."
        Start-Sleep -Milliseconds 1500
        $move = Invoke-PlayerTool 'move' 'farmhand' 'stardew_world_move' @{ direction = $approach.direction.name } -OptIn '--allow-world-actions'
        Assert-That ($move.Json.state -ceq 'completed' -and $move.Json.reached -eq $true -and $move.Json.tileX -eq $approach.near.x -and $move.Json.tileY -eq $approach.near.y -and $move.Json.position.facing -ceq $approach.direction.name) "the farmhand move $($approach.direction.name) returned $($move.Json.state)/$($move.Json.errorCode) at $($move.Json.tileX),$($move.Json.tileY)."
        Start-Sleep -Milliseconds 1500
        $here = Get-PlayerRuntime 'farmhand-moved' 'farmhand'
        Assert-That ($here.tileX -eq $approach.near.x -and $here.tileY -eq $approach.near.y) "a runtime read puts the farmhand at $($here.tileX),$($here.tileY), not $($approach.near.x),$($approach.near.y)."
        $inventory = Get-PlayerInventory 'farmhand-open' 'farmhand'
        $open = Invoke-PlayerTool 'open-chest' 'farmhand' 'stardew_world_act' @{ action = 'check'; x = $chestX; y = $chestY; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($open.Json.state -ceq 'completed') "check on the chest did not complete for the farmhand: $($open.Json.errorCode)"
        Wait-ChestMenu 'farmhand' 'farmhand'
        $before = Read-Chest 'farmhand-open' 'farmhand'
        Assert-That ([string]$before.playerId -ceq $farmhandId -and (Get-StoneCount $before.player.slots) -eq 2 -and (Get-StoneCount $before.container.slots) -eq 2) "the farmhand's chest read shows player $($before.playerId), backpack Stone $(Get-StoneCount $before.player.slots), chest Stone $(Get-StoneCount $before.container.slots); expected the farmhand with 2/2."
        Invoke-Withdraw 'farmhand' 'farmhand' 1 | Out-Null
        Start-Sleep -Milliseconds 1500
        $after = Read-Chest 'farmhand-after' 'farmhand'
        Assert-That ((Get-StoneCount $after.player.slots) -eq 3 -and (Get-StoneCount $after.container.slots) -eq 1) "after the withdrawal the farmhand sees backpack $(Get-StoneCount $after.player.slots) and chest $(Get-StoneCount $after.container.slots), expected 3/1."
        Close-ChestMenu 'farmhand' 'farmhand'
        Wait-PlayerStone 'farmhand-withdrawn' 'farmhand' 3

        Invoke-PlayerLogCommand 'open-chest' 'host' 'per-player-proof-chest open' 'Opened owned proof chest for farmer' | Out-Null
        Wait-ChestMenu 'host' 'host'
        $hostChest = Read-Chest 'host' 'host'
        Assert-That ([string]$hostChest.playerId -ceq $hostId -and (Get-StoneCount $hostChest.player.slots) -eq 1 -and (Get-StoneCount $hostChest.container.slots) -eq 1) "the host reads backpack Stone $(Get-StoneCount $hostChest.player.slots) and chest Stone $(Get-StoneCount $hostChest.container.slots); expected its own 1 and the chest's 1 after the farmhand's withdrawal."
        Close-ChestMenu 'host' 'host'
        Assert-That ((Get-PlayerStone 'host-after-chest' 'host') -eq 1) 'the farmhand transfer changed the host backpack.'
        Invoke-PlayerLogCommand 'cleanup-chest' 'host' 'per-player-proof-chest cleanup' 'Removed exactly one owned proof chest' | Out-Null
        Assert-Dialog (Read-ResultDialog 'fixed-farmhand-3' 'farmhand') 'farmhand' $farmhandId 3
        Write-Step 'The farmhand walked, opened the chest and withdrew one Stone; the host read the chest at 1 and its own backpack unchanged at 1.'

        Write-Step 'Leave and rejoin: the host keeps its session, the farmhand gets a new one.'
        $hostSession = $ready.network.host.gameSide.networkTwo.sessionId
        $farmhandSession = $ready.network.farmhand.gameSide.networkTwo.sessionId
        Assert-That (-not [string]::IsNullOrEmpty($hostSession) -and -not [string]::IsNullOrEmpty($farmhandSession)) 'the ready status reports no session IDs.'
        Invoke-SdvKit 'leave' @('project', 'review', 'network', 'leave', '--role', 'farmhand', '--json') | Out-Null
        $away = (Invoke-SdvKit 'status-away' @('project', 'review', 'status', '--topology', 'network-2', '--json') -AllowExit @(0, 3)).Json
        Assert-That ($away.network.host.gameSide.state -ceq 'active' -and $away.network.host.gameSide.projectMod.phase -ceq 'loaded') 'the host is not loaded and active while the farmhand is away.'
        $refused = Invoke-SdvKit 'inventory-while-away' (@('project', 'review', 'inventory', '--json') + (Get-Selection 'host')) -AllowExit @(3)
        Assert-That ($refused.Json.state -ceq 'unavailable' -and $refused.Json.errorCode -ceq 'reviewPairNotReady') "the host inventory read while the farmhand was away returned $($refused.Json.state)/$($refused.Json.errorCode), expected unavailable/reviewPairNotReady."
        Invoke-SdvKit 'join' @('project', 'review', 'network', 'join', '--role', 'farmhand', '--json') | Out-Null
        $rejoined = Wait-ReviewReady -Topology 'network-2'
        Assert-That ($rejoined.network.host.gameSide.networkTwo.sessionId -ceq $hostSession) 'the host session changed after the farmhand rejoined.'
        Assert-That ($rejoined.network.farmhand.gameSide.networkTwo.sessionId -cne $farmhandSession) 'the farmhand session did not change after the rejoin.'
        Assert-That ((Get-FarmerId 'rejoined' 'farmhand') -ceq $farmhandId -and (Get-PlayerStone 'rejoined' 'farmhand') -eq 3) 'the rejoined farmhand is not the same farmer with Stone 3.'
        Write-Step 'The farmhand rejoined with a new session; the host session did not change.'

        Write-Step 'Save, stop without reset and restart: the same farmers, host Stone 1, farmhand Stone 3, persisted.'
        $save = Invoke-PlayerTool 'save' 'host' 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
        Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) 'the fixture save did not complete.'
        Stop-Review -Topology 'network-2' -Label 'before-restart'
        $restarted = Start-Network 'restart' $fixedReady $probeReady
        Assert-That ($restarted.network.host.launchId -cne $launch.host -and $restarted.network.farmhand.launchId -cne $launch.farmhand) 'the restart reused a launch.'
        Assert-That ((Get-FarmerId 'restart' 'host') -ceq $hostId -and (Get-FarmerId 'restart' 'farmhand') -ceq $farmhandId) 'the restart changed a farmer.'
        Assert-That ((Get-PlayerStone 'restart' 'host') -eq 1 -and (Get-PlayerStone 'restart' 'farmhand') -eq 3) 'the restart did not keep host Stone 1 and farmhand Stone 3.'
        Assert-Dialog (Read-ResultDialog 'restart-host' 'host') 'host' $hostId 1
        Assert-Dialog (Read-ResultDialog 'restart-farmhand' 'farmhand') 'farmhand' $farmhandId 3
        Write-Step 'The saved state survived the restart.'
        Stop-Review -Topology 'network-2' -Reset -Label 'network'
    }

    if (Test-Section 'split') {
        Write-Step 'Split-screen: the second screen in one test-save review.'
        Invoke-SdvKit 'start-split' @('project', 'review', 'start', $fixedReady, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--companion', $probeReady, '--json') | Out-Null
        $script:singleLog = Get-SmapiLogPath (Wait-ReviewReady -TestSave)
        Invoke-SdvKit 'split-screen-join' @('project', 'review', 'split-screen', 'join', '--json') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        do {
            Start-Sleep -Seconds 3
            $status = (Invoke-SdvKit 'split-status' @('project', 'review', 'status', '--topology', 'single', '--json')).Json
            $runtime = $null
            if ($status.lab.gameSide.testSave.localSplitScreen -eq $true) {
                # Until the second screen answers, the read is refused with state unavailable (exit 3); anything else is a failure.
                $join = Invoke-PlayerTool 'runtime-join' 'screen1' 'stardew_runtime_get' -AllowExit @(0, 3)
                Assert-That ($join.Exit -eq 0 -or $join.Json.state -ceq 'unavailable') "the screen 1 read failed with exit $($join.Exit)."
                $runtime = $join.Json
            }
        } until (($runtime -and $runtime.PSObject.Properties['screen'] -and $runtime.screen.screenId -eq 1) -or [DateTime]::UtcNow -gt $deadline)
        Assert-That ($runtime -and $runtime.PSObject.Properties['screen'] -and $runtime.screen.screenId -eq 1) 'screen 1 did not become readable after the split-screen join.'
        $hostId = [string](Get-PlayerRuntime 'split-host' 'screen0').localPlayer.data.playerId
        $farmhandId = [string](Get-PlayerRuntime 'split-guest' 'screen1').localPlayer.data.playerId
        Assert-That ($hostId -cne $farmhandId) 'both screens report the same farmer.'
        Assert-That ((Get-PlayerStone 'split-base' 'screen0') -eq 0 -and (Get-PlayerStone 'split-base' 'screen1') -eq 0) 'the split-screen baseline already holds Stone.'
        Invoke-PlayerPress 'split-host' 'screen0' 'F7'
        Wait-PlayerStone 'split-host' 'screen0' 1
        Invoke-PlayerPress 'split-guest-1' 'screen1' 'F7'
        Wait-PlayerStone 'split-guest-1' 'screen1' 1
        Invoke-PlayerPress 'split-guest-2' 'screen1' 'F7'
        Wait-PlayerStone 'split-guest-2' 'screen1' 2
        Assert-Dialog (Read-ResultDialog 'split-host' 'screen0') 'screen0' $hostId 1
        Assert-Dialog (Read-ResultDialog 'split-guest' 'screen1') 'screen1' $farmhandId 2
        Write-Step 'Screen 0 holds Stone 1 and screen 1 holds Stone 2, each dialog with its own farmer.'

        Write-Step 'Screen 1 walks out of its cabin so that both screens share the Farm.'
        # The cabin interior is a generated location (FarmHouse<guid>): screens have no fixture tools, and the unbound
        # stardew_lab_warp refuses it as not a permanent location. The cabin door is the map warp at 3,12 (Maps/FarmHouse),
        # which stardew_world_move reports as blocked, so screen 1 walks to 3,11 and leaves with a key chord instead.
        $hostBefore = Get-PlayerRuntime 'split-host-before' 'screen0'
        $cabin = Get-PlayerRuntime 'split-guest-cabin' 'screen1'
        Assert-That ($cabin.tileX -eq 9 -and $cabin.tileY -eq 9) "screen 1 starts at $($cabin.tileX),$($cabin.tileY), not on the cabin bed 9,9."
        $x = $cabin.tileX
        $y = $cabin.tileY
        foreach ($direction in @('left') * 6 + @('down') * 2) {
            $step = Invoke-PlayerTool "walk-$direction" 'screen1' 'stardew_world_move' @{ direction = $direction } -OptIn '--allow-world-actions'
            if ($direction -ceq 'left') { $x-- } else { $y++ }
            Assert-That ($step.Json.state -ceq 'completed' -and $step.Json.reached -eq $true -and $step.Json.tileX -eq $x -and $step.Json.tileY -eq $y -and $step.Json.position.facing -ceq $direction) "screen 1 walking $direction returned $($step.Json.state)/$($step.Json.errorCode) at $($step.Json.tileX),$($step.Json.tileY), expected $x,$y."
        }
        Start-Sleep -Milliseconds 1500
        $beforeDoor = Get-PlayerRuntime 'split-guest-door' 'screen1'
        Assert-That ($beforeDoor.tileX -eq 3 -and $beforeDoor.tileY -eq 11 -and $beforeDoor.locationId -ceq $cabin.locationId) "screen 1 is at $($beforeDoor.tileX),$($beforeDoor.tileY), not in front of the cabin door."
        Assert-That ((Get-PlayerRuntime 'split-host-still' 'screen0').tileX -eq $hostBefore.tileX -and (Get-PlayerRuntime 'split-host-still2' 'screen0').tileY -eq $hostBefore.tileY) 'walking screen 1 moved screen 0.'
        $revision = (Get-PlayerMenu 'split-door' 'screen1').uiRevision
        $exit = Invoke-PlayerTool 'leave-cabin' 'screen1' 'stardew_input_chord' @{ buttons = @('S'); durationTicks = 40; uiRevision = [string]$revision } -OptIn '--allow-input'
        Assert-That ($exit.Json.succeeded -eq $true -and $exit.Json.released -eq $true) 'the key chord through the cabin door did not complete.'
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Seconds 1
            $outside = Get-PlayerRuntime 'split-guest-outside' 'screen1'
        } until ($outside.locationId -ceq 'Farm' -or [DateTime]::UtcNow -gt $deadline)
        Assert-That ($outside.locationId -ceq 'Farm') "screen 1 is in $($outside.locationId), not on the Farm."
        Write-Step "Screen 1 left the cabin and stands on the Farm at $($outside.tileX),$($outside.tileY)."

        Write-Step 'The host joins it on the Farm, prepares the probe chest, and screen 1 reads and uses it.'
        # Fixture tools are unbound only (the host is screen 0): no --screen here.
        $warp = Invoke-SdvKit 'warp-host' @('project', 'review', 'call', 'stardew_lab_warp', (@{ location = 'Farm'; x = $outside.tileX + 2; y = $outside.tileY } | ConvertTo-Json -Compress), '--allow-fixture-actions')
        Assert-That ($warp.Json.state -ceq 'ready' -and $warp.Json.lab.locationId -ceq 'Farm') 'the host did not warp to the Farm next to screen 1.'
        Start-Sleep -Milliseconds 1500
        $prepared = Invoke-PlayerLogCommand 'prepare-chest' 'screen0' 'per-player-proof-chest prepare' 'Prepared owned proof chest at Farm \((\d+), (\d+)\) with Stone 2'
        Invoke-PlayerLogCommand 'open-chest' 'screen1' 'per-player-proof-chest open screen=1' 'Opened owned proof chest for farmer' | Out-Null
        Wait-ChestMenu 'split-guest' 'screen1'
        $chest = Read-Chest 'split-guest' 'screen1'
        Assert-That ([string]$chest.playerId -ceq $farmhandId -and (Get-StoneCount $chest.player.slots) -eq 2 -and (Get-StoneCount $chest.container.slots) -eq 2) "screen 1 reads player $($chest.playerId), backpack Stone $(Get-StoneCount $chest.player.slots), chest Stone $(Get-StoneCount $chest.container.slots); expected its own farmer with 2/2."
        Invoke-Withdraw 'split-guest' 'screen1' 1 | Out-Null
        Start-Sleep -Milliseconds 1500
        $after = Read-Chest 'split-guest-after' 'screen1'
        Assert-That ((Get-StoneCount $after.player.slots) -eq 3 -and (Get-StoneCount $after.container.slots) -eq 1) "after the withdrawal screen 1 sees backpack $(Get-StoneCount $after.player.slots) and chest $(Get-StoneCount $after.container.slots), expected 3/1."
        Close-ChestMenu 'split-guest' 'screen1'
        Invoke-PlayerLogCommand 'open-chest-host' 'screen0' 'per-player-proof-chest open' 'Opened owned proof chest for farmer' | Out-Null
        Wait-ChestMenu 'split-host' 'screen0'
        $hostChest = Read-Chest 'split-host' 'screen0'
        Assert-That ([string]$hostChest.playerId -ceq $hostId -and (Get-StoneCount $hostChest.player.slots) -eq 1 -and (Get-StoneCount $hostChest.container.slots) -eq 1) "screen 0 reads backpack Stone $(Get-StoneCount $hostChest.player.slots) and chest Stone $(Get-StoneCount $hostChest.container.slots); expected its own 1 and the chest's 1."
        Close-ChestMenu 'split-host' 'screen0'
        Invoke-PlayerLogCommand 'cleanup-chest' 'screen0' 'per-player-proof-chest cleanup' 'Removed exactly one owned proof chest' | Out-Null
        Write-Step 'Screen 1 withdrew one Stone from the chest (3/1); screen 0 read the chest at 1 and its own backpack at 1.'

        Write-Step 'The act refusal: Stardew forces gamepad controls on screen 1.'
        $guest = Get-PlayerRuntime 'split-guest-act' 'screen1'
        Assert-That ($guest.localPlayer.data.input.gamepadControls -eq $true) 'screen 1 does not report gamepad controls, so the act refusal below would not be the gamepad-mode refusal.'
        $inventory = Get-PlayerInventory 'split-act' 'screen1'
        $act = Invoke-PlayerTool 'act' 'screen1' 'stardew_world_act' @{ action = 'check'; x = $guest.tileX - 1; y = $guest.tileY; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions' -AllowExit @(3)
        Assert-That ($act.Json.errorCode -ceq 'worldActionCursorAimUnavailable' -and $act.Json.dispatchState -ceq 'notDispatched') "stardew_world_act on screen 1 returned $($act.Json.state)/$($act.Json.errorCode)/$($act.Json.dispatchState), expected the gamepad-mode refusal worldActionCursorAimUnavailable before dispatch."
        Write-Step 'Screen 1 left its cabin, used the chest and its act was refused (gamepad mode); screen leave and replacement and restart persistence stay manual.'
        Stop-Review -Topology 'single' -Reset -Label 'split'
    }

    if ($partial) { Write-Step 'The selected sections passed. Run without -Only for acceptance evidence.' }
}
catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Complete-Acceptance -Failed:$failed
}

if ($failed) { exit 1 }

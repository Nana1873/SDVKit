#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: everyday keyboard-and-mouse player actions in a single review on the test save (#270).

.DESCRIPTION
Proves each action through a state read, not through the input acknowledgement:

- walking in four directions (tile changes and the reported facing) and walking versus running (distance per tick count);
- stardew_lab_warp (runtime location and tile) and a location change by walking through a map warp;
- hotbar selection with number keys, and the documented refusal of the mouse wheel without a menu;
- the game menu: open with E, switch tab by clicking its component, close with Escape;
- hoe, pickaxe and scythe on the faced tile, after checking the reported facing and tool tile (soil appears, the stone or weeds disappear, stamina drops);
- stardew_world_act: the hoe by useTool on a tile next to the player, check on the Farm sign opens its message, and a
  tile that is not next to the player is refused;
- stardew_fixture_item_add puts a chest into the backpack (slot and inventory revision match a fresh read), and check
  with the chest selected places it on the target tile;
- stardew_fixture_npc_place brings Lewis next to the player, the runtime NPC list shows him there, check with a tool
  selected talks to him (dialogue opens, friendship rises), and check with an added parsnip selected gives it as a gift
  (friendship rises again);
- fishing: preparation and a cast that puts the bobber in the water.

The tool tests pick their tiles from a world-area read, so they adapt to the test save. The virtual cursor is
parked in the screen corner before keyboard tool use, because Stardew aims at the tile under the cursor when it is
within one tile of the player. Matrix and gaps: docs/player-actions.md.

While developing SDVKit, -Only runs the shared setup (lab, test save, review start, Farm) and then only the named
sections: walk, mapwarp, hotbar, menu, tools, act, item, npc, fishing. Such a partial run writes its evidence under
player-actions-partial and never counts as acceptance; PRs and releases need a full run.

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Only npc,item
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
# Accept both -Only npc,item from PowerShell and a single comma-separated string from pwsh -File.
$sections = @('walk', 'mapwarp', 'hotbar', 'menu', 'tools', 'act', 'item', 'npc', 'fishing')
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = @($Only | Where-Object { $_ -cnotin $sections })
if ($unknown.Count -gt 0) { throw "Unknown -Only section(s): $($unknown -join ', '). Use: $($sections -join ', ')." }
$partial = $Only.Count -gt 0
$context = Initialize-Acceptance -Name ($partial ? 'player-actions-partial' : 'player-actions') -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

function Test-Section([string[]] $Names) {
    # Every section runs in a full run; -Only selects some for a partial development run.
    -not $partial -or @($Names | Where-Object { $_ -in $Only }).Count -gt 0
}

$stones = @('(O)343', '(O)450', '(O)668', '(O)670', '(O)760', '(O)762')
$weeds = @('(O)313', '(O)314', '(O)315', '(O)316', '(O)317', '(O)318', '(O)319', '(O)320', '(O)321', '(O)674', '(O)675', '(O)676', '(O)677', '(O)678', '(O)679', '(O)784', '(O)785', '(O)786', '(O)792', '(O)793', '(O)794')

function Get-Runtime([string] $Label) {
    (Invoke-Tool "runtime-$Label" 'stardew_runtime_get').Json.runtime
}

function Wait-Settled {
    # Runtime is published about once per second; wait for a fresh sample.
    Start-Sleep -Milliseconds 1500
}

function Invoke-Press([string] $Label, [string] $Button) {
    $press = Invoke-Tool "press-$Label" 'stardew_input_press' @{ button = $Button } -OptIn '--allow-input'
    Assert-That ($press.Json.succeeded -eq $true) "the $Button press did not complete."
}

function Invoke-Hold([string] $Label, [string[]] $Buttons, [int] $Ticks) {
    $revision = (Invoke-SdvKit "menu-$Label" @('project', 'review', 'menu', '--json')).Json.uiRevision
    $chord = Invoke-Tool "chord-$Label" 'stardew_input_chord' @{ buttons = $Buttons; durationTicks = $Ticks; uiRevision = $revision } -OptIn '--allow-input'
    Assert-That ($chord.Json.succeeded -eq $true -and $chord.Json.released -eq $true) "holding $($Buttons -join '+') did not complete."
}

function Invoke-Warp([string] $Label, [string] $Location, [int] $X, [int] $Y) {
    $warp = Invoke-Tool "warp-$Label" 'stardew_lab_warp' @{ location = $Location; x = $X; y = $Y } -OptIn '--allow-fixture-actions'
    Assert-That ($warp.Json.state -ceq 'ready' -and $warp.Json.lab.locationId -ceq $Location -and $warp.Json.lab.tileX -eq $X -and $warp.Json.lab.tileY -eq $Y) "the warp to $Location $X,$Y did not land there."
    Wait-Settled
}

function Get-Area([string] $Label, [int] $X, [int] $Y, [int] $Width, [int] $Height) {
    (Invoke-SdvKit "world-$Label" @('project', 'review', 'world', $X, $Y, $Width, $Height, '--json')).Json.data.tiles
}

function Get-SlotKey([int] $Slot) {
    # Toolbar keys: slots 0-8 are D1-D9, slot 9 is D0, 10 is OemMinus, 11 is OemPlus.
    Assert-That ($Slot -ge 0 -and $Slot -lt 12) "slot $Slot is not on the toolbar."
    switch ($Slot) { { $_ -lt 9 } { "D$($Slot + 1)" } 9 { 'D0' } 10 { 'OemMinus' } 11 { 'OemPlus' } }
}

function Get-Friendship([string] $Label, [string] $Npc) {
    $read = (Invoke-SdvKit "progression-$Label" @('project', 'review', 'progression', '--npc', $Npc, '--mail', 'SDVKit.None', '--quest', 'SDVKit.None', '--json')).Json.data.npc
    if ($read.state -ceq 'available') { return [int] $read.friendshipPoints }
    Assert-That ($read.state -ceq 'noFriendship') "the friendship read for $Npc returned $($read.state)."
    return 0
}

function Close-Dialogue([string] $Label) {
    # Escape does not close a DialogueBox. Each click on it finishes the text or turns the page; decide every click
    # from a fresh read.
    foreach ($attempt in 1..8) {
        $menu = (Invoke-SdvKit "menu-$Label-$attempt" @('project', 'review', 'menu', '--json')).Json
        if ($menu.menuOpen -eq $false) { return }
        $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
        Assert-That ($box.Count -eq 1) "expected a dialogue, found $(@($menu.menus | ForEach-Object type) -join ', ')."
        Invoke-ComponentClick "$Label-$attempt" -Menu $menu -Component $box[0] | Out-Null
        Wait-Settled
    }
    Assert-That ((Invoke-SdvKit "menu-$Label-closed" @('project', 'review', 'menu', '--json')).Json.menuOpen -eq $false) 'the dialogue did not close.'
}

function Find-ToolSlot([string] $Label, [string] $ItemId) {
    $slot = @((Invoke-SdvKit "inventory-$Label" @('project', 'review', 'inventory', '--json')).Json.data.slots |
        Where-Object { $_.item.qualifiedItemId -ceq $ItemId })[0].slot
    Assert-That ($null -ne $slot) "the backpack has no $ItemId."
    return $slot
}

function Invoke-ToolDown([string] $Label, [string] $ItemId, $Tile, [switch] $NoStamina) {
    # Stand on the free tile north of $Tile, face down, select the tool, park the cursor and use the tool once.
    $slot = Find-ToolSlot $Label $ItemId
    Invoke-Warp $Label 'Farm' $Tile.x ($Tile.y - 1)
    Invoke-Press "$Label-face" 'S'
    Invoke-Press "$Label-select" (Get-SlotKey $slot)
    Invoke-Tool "park-$Label" 'stardew_input_cursor_set' @{ x = 8; y = 8 } -OptIn '--allow-input' | Out-Null
    Wait-Settled
    $before = Get-Runtime "$Label-before"
    Assert-That ($before.localPlayer.data.selectedSlot -eq $slot) "$ItemId is not selected."
    $facing = $before.localPlayer.data.position
    Assert-That ($facing.facing -ceq 'down' -and $facing.toolTileX -eq $Tile.x -and $facing.toolTileY -eq $Tile.y) "the player faces $($facing.facing) with the tool tile $($facing.toolTileX),$($facing.toolTileY), not down toward $($Tile.x),$($Tile.y)."
    Invoke-Press "$Label-use" 'C'
    Start-Sleep -Seconds 2
    $after = Get-Runtime "$Label-after"
    # The scythe costs no stamina in Stardew; the other tools do.
    Assert-That ($NoStamina -or $after.localPlayer.data.stamina -lt $before.localPlayer.data.stamina) "using $ItemId did not cost stamina."
    return (Get-Area "$Label-result" $Tile.x $Tile.y 1 1)[0]
}

$failed = $false
try {
    $mod = Join-Path $context.Evidence 'work\PlayerActionsTarget'
    Invoke-SdvKit 'create' @('project', 'create', 'smapi-mod', $mod, '--name', 'Player Actions Target', '--author', 'SDVKit',
        '--unique-id', 'SDVKit.PlayerActionsTarget', '--description', 'Empty target for the player-action acceptance.', '--json') | Out-Null
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'start' @('project', 'review', 'start', $mod, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    Wait-ReviewReady -TestSave | Out-Null
    Invoke-Tool 'farm' 'stardew_fixture_farm' -OptIn '--allow-fixture-actions' | Out-Null
    Wait-Settled
    $spot = $null
    if ($partial) { Write-Step "Partial run ($($Only -join ', ')): development only, not acceptance evidence." }

    if (Test-Section 'walk') {
        Write-Step 'Walking: four directions, then walking versus running.'
        Invoke-Warp 'walk-start' 'Farm' 64 16
        $start = Get-Runtime 'walk-start'
        $position = $start
        foreach ($step in @(@{ key = 'S'; dx = 0; dy = 1; facing = 'down' }, @{ key = 'D'; dx = 1; dy = 0; facing = 'right' }, @{ key = 'A'; dx = -1; dy = 0; facing = 'left' }, @{ key = 'W'; dx = 0; dy = -1; facing = 'up' })) {
            Invoke-Hold "walk-$($step.key)" @($step.key) 30
            Wait-Settled
            $next = Get-Runtime "walk-$($step.key)"
            $moveX = $next.tileX - $position.tileX
            $moveY = $next.tileY - $position.tileY
            Assert-That ([Math]::Sign($moveX) -eq $step.dx -and [Math]::Sign($moveY) -eq $step.dy) "walking $($step.key) moved by $moveX,$moveY."
            Assert-That ($next.localPlayer.data.position.facing -ceq $step.facing) "after walking $($step.key) the player faces $($next.localPlayer.data.position.facing), expected $($step.facing)."
            $position = $next
        }

        # Equal tick counts need not give equal distances (sub-tile position, acceleration), so no exact return is expected.
        $released = (Invoke-Tool 'runtime-walk-released' 'stardew_runtime_get').Json
        Wait-Settled
        $later = (Invoke-Tool 'runtime-walk-stopped' 'stardew_runtime_get').Json
        $still = $later.runtime
        Assert-That ([DateTimeOffset]$later.observedAtUtc -gt [DateTimeOffset]$released.observedAtUtc) 'no newer runtime sample arrived to check that the player stopped.'
        $position = $released.runtime
        Assert-That ($still.tileX -eq $position.tileX -and $still.tileY -eq $position.tileY) 'the player kept moving after the input was released.'

        Invoke-Warp 'run-start' 'Farm' 64 16
        Invoke-Hold 'run' @('S') 30
        Wait-Settled
        $run = (Get-Runtime 'run').tileY - $start.tileY
        Invoke-Warp 'walk-start-2' 'Farm' 64 16
        Invoke-Hold 'walk-slow' @('LeftShift', 'S') 30
        Wait-Settled
        $walk = (Get-Runtime 'walk-slow').tileY - $start.tileY
        Assert-That ($run -gt $walk -and $walk -ge 1) "30 ticks moved $run tiles normally and $walk with Shift; expected Shift to be slower but still moving."
        Write-Step "Walked 4 directions; 30 ticks moved $run tiles normally, $walk with Shift."
    }

    if (Test-Section 'mapwarp') {
        Write-Step 'Location change by walking through the Farm-to-BusStop map warp.'
        $warps = (Invoke-Tool 'warps' 'stardew_map_warps_list' @{ asset = 'Maps/Farm' }).Json.warps
        $exit = @($warps | Where-Object { $_.targetName -ceq 'BusStop' -and $_.kind -ceq 'playerAndNpc' } | Sort-Object fromY)[0]
        Assert-That ($null -ne $exit) 'the Farm map has no warp to BusStop.'
        Invoke-Warp 'edge' 'Farm' ($exit.fromX - 3) $exit.fromY
        Invoke-Hold 'edge-walk' @('D') 90
        Start-Sleep -Seconds 2
        $arrived = Get-Runtime 'edge-arrived'
        Assert-That ($arrived.locationId -ceq 'BusStop') "walking east ended in $($arrived.locationId) $($arrived.tileX),$($arrived.tileY), not BusStop."
    }

    if (Test-Section 'hotbar') {
        Write-Step 'Hotbar: number keys select slots; the mouse wheel needs a menu.'
        Invoke-Warp 'hotbar' 'Farm' 64 16
        foreach ($slot in 2, 5) {
            Invoke-Press "hotbar-$slot" (Get-SlotKey $slot)
            Wait-Settled
            Assert-That ((Get-Runtime "hotbar-$slot").localPlayer.data.selectedSlot -eq $slot) "$(Get-SlotKey $slot) did not select slot $slot."
        }

        Invoke-Tool 'park-wheel' 'stardew_input_cursor_set' @{ x = 8; y = 8 } -OptIn '--allow-input' | Out-Null
        $wheel = Invoke-Tool 'wheel-without-menu' 'stardew_input_wheel' @{ direction = 'down' } -OptIn '--allow-input' -AllowExit @(3)
        Assert-That ($wheel.Json.problem.code -ceq 'inputMenuMissing') "the wheel without a menu returned $($wheel.Json.problem.code), expected inputMenuMissing."
    }

    if (Test-Section 'menu') {
        Write-Step 'Game menu: open with E, click a tab, close with Escape.'
        Invoke-Press 'menu-open' 'E'
        Wait-Settled
        $menu = (Invoke-SdvKit 'menu-opened' @('project', 'review', 'menu', '--json')).Json
        $gameMenu = @($menu.menus | Where-Object adapter -ceq 'gameMenu')[0]
        Assert-That ($null -ne $gameMenu -and $gameMenu.currentTab -eq 0) 'E did not open the game menu on its first tab.'
        $tabs = @($gameMenu.components | Where-Object kind -ceq 'tab' | Sort-Object { $_.bounds.x })
        Assert-That ($tabs.Count -ge 4) "the game menu shows $($tabs.Count) tabs."
        Write-Step 'Inventory: move the first item to an empty slot (click to pick up, click to place).'
        $inventory = (Invoke-SdvKit 'move-inventory-before' @('project', 'review', 'inventory', '--json')).Json.data
        $from = @($inventory.slots | Where-Object { $null -ne $_.item })[0]
        $to = @($inventory.slots | Where-Object { $_.state -ceq 'empty' })[0]
        Assert-That ($null -ne $from -and $null -ne $to) 'the backpack needs one item and one empty slot.'
        foreach ($step in @(@{ label = 'pick-up'; slot = $from.slot }, @{ label = 'place'; slot = $to.slot })) {
            # The slots belong to the inventory page's InventoryMenu, a child node of the game menu.
            $current = (Invoke-SdvKit "menu-$($step.label)" @('project', 'review', 'menu', '--json')).Json
            $component = @($current.menus.components | Where-Object { $_.kind -ceq 'inventorySlot' -and $_.controllerId -eq $step.slot -and $_.visibleFlag -and $_.intersectsViewport })
            Assert-That ($component.Count -eq 1) "inventory slot $($step.slot) has no single visible component."
            Invoke-ComponentClick "inventory-$($step.label)" -Menu $current -Component $component[0] | Out-Null
        }

        Wait-Settled
        $moved = (Invoke-SdvKit 'move-inventory-after' @('project', 'review', 'inventory', '--json')).Json.data
        $fromAfter = @($moved.slots | Where-Object slot -eq $from.slot)[0]
        $toAfter = @($moved.slots | Where-Object slot -eq $to.slot)[0]
        Assert-That ($fromAfter.state -ceq 'empty' -and $toAfter.item.qualifiedItemId -ceq $from.item.qualifiedItemId) "clicking $($from.item.qualifiedItemId) from slot $($from.slot) to $($to.slot) did not move it."

        $menu = (Invoke-SdvKit 'menu-after-move' @('project', 'review', 'menu', '--json')).Json
        $tabs = @(@($menu.menus | Where-Object adapter -ceq 'gameMenu')[0].components | Where-Object kind -ceq 'tab' | Sort-Object { $_.bounds.x })
        Invoke-ComponentClick 'menu-tab' -Menu $menu -Component $tabs[3] | Out-Null
        Wait-Settled
        $switched = @((Invoke-SdvKit 'menu-switched' @('project', 'review', 'menu', '--json')).Json.menus | Where-Object adapter -ceq 'gameMenu')[0]
        Assert-That ($switched.currentTab -eq 3) "clicking the fourth tab left currentTab at $($switched.currentTab)."
        Invoke-Press 'menu-close' 'Escape'
        Wait-Settled
        Assert-That ((Invoke-SdvKit 'menu-closed' @('project', 'review', 'menu', '--json')).Json.menuOpen -eq $false) 'Escape did not close the game menu.'
    }

    if (Test-Section 'tools', 'act', 'item', 'npc') {
        # One world read for the sections that pick their tiles from it.
        $area = @(Get-Area 'scan' 58 18 16 16)
        $byTile = @{}
        foreach ($tile in $area) { $byTile["$($tile.x),$($tile.y)"] = $tile }
        function Test-Free($tile) { $null -ne $tile -and $tile.objectState -ceq 'missing' -and $null -eq $tile.soil }
        function Find-Target([scriptblock] $Match) {
            @($area | Where-Object { (& $Match $_) -and (Test-Free $byTile["$($_.x),$($_.y - 1)"]) })[0]
        }
    }

    if (Test-Section 'tools') {
        Write-Step 'Tools on the faced tile: hoe, pickaxe, scythe.'
        $plain = Find-Target { param($t) Test-Free $t }
        Assert-That ($null -ne $plain) 'no free tile with a free tile north of it in the scanned area.'
        $hoed = Invoke-ToolDown 'hoe' '(T)Hoe' $plain
        Assert-That ($null -ne $hoed.soil) "the hoe did not till $($plain.x),$($plain.y)."

        $stone = Find-Target { param($t) $t.objectQualifiedItemId -cin $stones }
        Assert-That ($null -ne $stone) 'no reachable stone in the scanned area of the test save.'
        $mined = Invoke-ToolDown 'pickaxe' '(T)Pickaxe' $stone
        Assert-That ($mined.objectState -ceq 'missing') "the pickaxe left $($stone.objectQualifiedItemId) at $($stone.x),$($stone.y)."

        $weed = Find-Target { param($t) $t.objectQualifiedItemId -cin $weeds }
        Assert-That ($null -ne $weed) 'no reachable weeds in the scanned area of the test save.'
        $cut = Invoke-ToolDown 'scythe' '(W)47' $weed -NoStamina
        Assert-That ($cut.objectState -ceq 'missing') "the scythe left $($weed.objectQualifiedItemId) at $($weed.x),$($weed.y)."
    }

    if (Test-Section 'act') {
        Write-Step 'Faced-tile actions with stardew_world_act: hoe by useTool, read the Farm sign by check, refuse a far tile.'
        $east = @($area | Where-Object { (Test-Free $_) -and (Test-Free $byTile["$($_.x - 1),$($_.y)"]) -and $null -eq $byTile["$($_.x),$($_.y)"].soil })[1]
        Assert-That ($null -ne $east) 'no free tile with a free tile west of it in the scanned area.'
        Invoke-Warp 'act-hoe' 'Farm' ($east.x - 1) $east.y
        $hoeSlot = Find-ToolSlot 'act-hoe' '(T)Hoe'
        Invoke-Press 'act-hoe-select' (Get-SlotKey $hoeSlot)
        Wait-Settled
        # The scan above is older than the hoe, pickaxe and scythe steps: confirm the target is still untilled.
        Assert-That ($null -eq (Get-Area 'act-hoe-target' $east.x $east.y 1 1)[0].soil) "$($east.x),$($east.y) is already tilled."
        $before = Get-Runtime 'act-hoe-before'
        # Prove cursor aim: the player must not already face the target to the east.
        Assert-That ($before.localPlayer.data.position.facing -cne 'right') 'the player already faces the hoe target; the act would not prove cursor aim.'
        $inventory = (Invoke-SdvKit 'act-hoe-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        $act = Invoke-Tool 'act-hoe' 'stardew_world_act' @{ action = 'useTool'; x = $east.x; y = $east.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($act.Json.state -ceq 'completed' -and $act.Json.button -ceq 'MouseLeft') "useTool did not complete: $($act.Json.errorCode)"
        Start-Sleep -Seconds 2
        Assert-That ((Get-Runtime 'act-hoe-after').localPlayer.data.stamina -lt $before.localPlayer.data.stamina) 'useTool with the hoe did not cost stamina.'
        Assert-That ($null -ne (Get-Area 'act-hoe-result' $east.x $east.y 1 1)[0].soil) "useTool with the hoe did not till $($east.x),$($east.y)."

        $sign = (Invoke-Tool 'sign-property' 'stardew_map_property_get' @{ asset = 'Maps/Farm'; property = 'Action'; scope = 'tile'; source = 'direct'; layer = 'Buildings'; x = 8; y = 7 }).Json
        Assert-That ("$($sign.property.value)" -clike 'Message *') "Maps/Farm has no sign at 8,7 (Action is '$($sign.property.value)')."
        Invoke-Warp 'act-sign' 'Farm' 8 8
        Assert-That ((Get-Runtime 'act-sign-before').localPlayer.data.position.facing -cne 'up') 'the player already faces the sign; the check would not prove cursor aim.'
        $inventory = (Invoke-SdvKit 'act-sign-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        $far = Invoke-Tool 'act-far' 'stardew_world_act' @{ action = 'check'; x = 8; y = 5; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions' -AllowExit @(3)
        Assert-That ($far.Json.errorCode -ceq 'worldActionTargetNotAdjacent' -and $far.Json.dispatchState -ceq 'notDispatched') "a far target returned $($far.Json.errorCode)/$($far.Json.dispatchState)."
        $check = Invoke-Tool 'act-sign' 'stardew_world_act' @{ action = 'check'; x = 8; y = 7; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($check.Json.state -ceq 'completed' -and $check.Json.button -ceq 'MouseRight') "check did not complete: $($check.Json.errorCode)"
        Wait-Settled
        $dialogue = (Invoke-SdvKit 'act-sign-menu' @('project', 'review', 'menu', '--json')).Json
        # The Farm sign shows its text in a LetterViewerMenu in Stardew 1.6.
        Assert-That ($dialogue.menuOpen -eq $true -and @($dialogue.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.LetterViewerMenu' }).Count -eq 1) "check on the sign opened $(@($dialogue.menus | ForEach-Object type) -join ', ')."
        Invoke-Press 'act-sign-close' 'Escape'
        Wait-Settled
    }

    if (Test-Section 'item') {
        Write-Step 'Item fixture: add a chest to the backpack, then place it with check on a free tile next to the player.'
        $spot = $null
        foreach ($candidate in @($area | Where-Object { (Test-Free $_) -and (Test-Free $byTile["$($_.x - 1),$($_.y)"]) } | Select-Object -Skip 2 -First 6)) {
            # The scan is older than the tool steps: re-read the target and the tile west of it.
            $fresh = @(Get-Area "place-candidate-$($candidate.x)-$($candidate.y)" ($candidate.x - 1) $candidate.y 2 1)
            if (@($fresh | Where-Object { Test-Free $_ }).Count -eq 2) { $spot = $candidate; break }
        }
        Assert-That ($null -ne $spot) 'no free tile with a free tile west of it for the placement.'
        $beforeUnknown = (Invoke-SdvKit 'item-add-unknown-before' @('project', 'review', 'inventory', '--json')).Json.data.inventoryRevision
        $unknown = (Invoke-Tool 'item-add-unknown' 'stardew_fixture_item_add' @{ qualifiedItemId = '(O)SDVKit_NoSuchItem'; stack = 1 } -OptIn '--allow-fixture-actions' -AllowExit @(3)).Json
        Assert-That ($unknown.state -ceq 'blocked' -and $null -eq $unknown.item) "an unknown item was not refused: $($unknown.message)"
        Assert-That ((Invoke-SdvKit 'item-add-unknown-after' @('project', 'review', 'inventory', '--json')).Json.data.inventoryRevision -ceq $beforeUnknown) 'refusing an unknown item changed the backpack.'
        $toolQuality = (Invoke-Tool 'item-add-tool-quality' 'stardew_fixture_item_add' @{ qualifiedItemId = '(T)Hoe'; stack = 1; quality = 2 } -OptIn '--allow-fixture-actions' -AllowExit @(3)).Json
        Assert-That ($toolQuality.state -ceq 'blocked') 'a tool with gold quality was not refused.'
        $add = (Invoke-Tool 'item-add' 'stardew_fixture_item_add' @{ qualifiedItemId = '(BC)130'; stack = 1 } -OptIn '--allow-fixture-actions').Json
        Assert-That ($add.state -ceq 'ready' -and $add.item.qualifiedItemId -ceq '(BC)130' -and $add.item.stack -eq 1) "item add failed: $($add.message)"
        $added = (Invoke-SdvKit 'item-add-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        $addedSlot = @($added.slots | Where-Object slot -eq $add.item.slot)[0]
        Assert-That ($addedSlot.item.qualifiedItemId -ceq '(BC)130' -and $added.inventoryRevision -ceq $add.item.inventoryRevision) "the backpack read does not show the chest in slot $($add.item.slot) with the reported revision."
        Invoke-Warp 'place' 'Farm' ($spot.x - 1) $spot.y
        Invoke-Press 'place-select' (Get-SlotKey $add.item.slot)
        Wait-Settled
        $inventory = (Invoke-SdvKit 'place-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        Assert-That ($inventory.selectedSlot -eq $add.item.slot) "the chest in slot $($add.item.slot) is not selected."
        $place = Invoke-Tool 'place' 'stardew_world_act' @{ action = 'check'; x = $spot.x; y = $spot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($place.Json.state -ceq 'completed') "check with the chest did not complete: $($place.Json.errorCode)"
        Wait-Settled
        Assert-That ((Get-Area 'place-result' $spot.x $spot.y 1 1)[0].objectQualifiedItemId -ceq '(BC)130') "the chest was not placed on $($spot.x),$($spot.y)."
        $placed = (Invoke-SdvKit 'place-inventory-after' @('project', 'review', 'inventory', '--json')).Json.data
        Assert-That (@($placed.slots | Where-Object slot -eq $add.item.slot)[0].state -ceq 'empty') "slot $($add.item.slot) still holds the chest."
    }

    if (Test-Section 'npc') {
        Write-Step 'NPCs: place Lewis next to the player, find him in the runtime NPC list, talk with check, then give a gift.'
        $npcSpot = $null
        foreach ($candidate in @($area | Where-Object { (Test-Free $_) -and (Test-Free $byTile["$($_.x - 1),$($_.y)"]) -and ($null -eq $spot -or (-not ($_.x -eq $spot.x -and $_.y -eq $spot.y) -and -not ($_.x - 1 -eq $spot.x -and $_.y -eq $spot.y))) } | Select-Object -Skip 3 -First 6)) {
            $fresh = @(Get-Area "npc-candidate-$($candidate.x)-$($candidate.y)" ($candidate.x - 1) $candidate.y 2 1)
            if (@($fresh | Where-Object { Test-Free $_ }).Count -eq 2) { $npcSpot = $candidate; break }
        }
        Assert-That ($null -ne $npcSpot) 'no free tile with a free tile west of it for the NPC.'
        Invoke-Warp 'npc' 'Farm' ($npcSpot.x - 1) $npcSpot.y
        $placed = (Invoke-Tool 'npc-place' 'stardew_fixture_npc_place' @{ name = 'Lewis'; x = $npcSpot.x; y = $npcSpot.y } -OptIn '--allow-fixture-actions').Json
        Assert-That ($placed.state -ceq 'ready' -and $placed.navigation.locationId -ceq 'Farm' -and $placed.navigation.tileX -eq $npcSpot.x -and $placed.navigation.tileY -eq $npcSpot.y) "placing Lewis failed: $($placed.message)"
        Wait-Settled
        $lewis = @((Get-Runtime 'npc-list').localPlayer.data.npcs | Where-Object name -ceq 'Lewis')
        Assert-That ($lewis.Count -eq 1 -and $lewis[0].tileX -eq $npcSpot.x -and $lewis[0].tileY -eq $npcSpot.y) "the runtime NPC list does not show Lewis at $($npcSpot.x),$($npcSpot.y)."

        $friendship = Get-Friendship 'npc-before' 'Lewis'
        Invoke-Press 'npc-talk-select' (Get-SlotKey (Find-ToolSlot 'npc-talk' '(T)Hoe'))
        Wait-Settled
        $inventory = (Invoke-SdvKit 'npc-talk-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        $talk = Invoke-Tool 'npc-talk' 'stardew_world_act' @{ action = 'check'; x = $npcSpot.x; y = $npcSpot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($talk.Json.state -ceq 'completed') "check on Lewis did not complete: $($talk.Json.errorCode)"
        Wait-Settled
        $talkMenu = (Invoke-SdvKit 'npc-talk-menu' @('project', 'review', 'menu', '--json')).Json
        Assert-That (@($talkMenu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' }).Count -eq 1) "talking to Lewis opened $(@($talkMenu.menus | ForEach-Object type) -join ', ')."
        Close-Dialogue 'npc-talk-close'
        $talked = Get-Friendship 'npc-talked' 'Lewis'
        Assert-That ($talked -gt $friendship) "talking to Lewis left friendship at $talked (before $friendship)."

        $gift = (Invoke-Tool 'npc-gift-add' 'stardew_fixture_item_add' @{ qualifiedItemId = '(O)24'; stack = 1 } -OptIn '--allow-fixture-actions').Json
        Assert-That ($gift.state -ceq 'ready') "adding the parsnip failed: $($gift.message)"
        Invoke-Press 'npc-gift-select' (Get-SlotKey $gift.item.slot)
        Wait-Settled
        $inventory = (Invoke-SdvKit 'npc-gift-inventory' @('project', 'review', 'inventory', '--json')).Json.data
        Assert-That ($inventory.selectedSlot -eq $gift.item.slot) 'the parsnip is not selected.'
        $give = Invoke-Tool 'npc-gift' 'stardew_world_act' @{ action = 'check'; x = $npcSpot.x; y = $npcSpot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($give.Json.state -ceq 'completed') "check with the parsnip did not complete: $($give.Json.errorCode)"
        Wait-Settled
        Close-Dialogue 'npc-gift-close'
        $gifted = Get-Friendship 'npc-gifted' 'Lewis'
        $giftSlot = @((Invoke-SdvKit 'npc-gift-after' @('project', 'review', 'inventory', '--json')).Json.data.slots | Where-Object slot -eq $gift.item.slot)[0]
        Assert-That ($gifted -gt $talked -and $giftSlot.state -ceq 'empty') "the gift did not register: friendship $gifted (after talking $talked), slot $($giftSlot.state)."
        Write-Step "Lewis: friendship $friendship before, $talked after talking, $gifted after the gift."
    }

    if (Test-Section 'fishing') {
        Write-Step 'Fishing: prepare and cast.'
        # A fixed search start keeps the same bank whatever the previous steps did; the default starts at the player.
        # The bank at 69,30 casts to the right. Short casts facing down sometimes cost stamina without landing (#305).
        $prepare = Invoke-Tool 'fishing-prepare' 'stardew_fixture_fishing_prepare' @{ skipTutorial = $true; x = 69; y = 30 } -OptIn '--allow-fixture-actions'
        Assert-That ($prepare.Json.state -ceq 'ready') 'fishing preparation failed.'
        Invoke-Tool 'park-fishing' 'stardew_input_cursor_set' @{ x = 8; y = 8 } -OptIn '--allow-input' | Out-Null
        Wait-Settled
        # The game can drop a cast right after other input. Cast once more only when a fresh read shows the rod idle
        # (not charging, not casting, bobber not in the air, not fishing) and no stamina spent, so the first cast never ran.
        $staminaBeforeCast = (Get-Runtime 'cast-before').localPlayer.data.stamina
        foreach ($attempt in 1, 2) {
            Invoke-Hold "cast-$attempt" @('C') 40
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            do {
                Start-Sleep -Seconds 1
                $fishing = (Get-Runtime "cast-$attempt").localPlayer.data.fishing
            } until (($fishing.fishing -eq $true -and $fishing.bobberTileY -gt 0) -or [DateTime]::UtcNow -gt $deadline)
            if ($fishing.fishing -eq $true) { break }
            $idle = -not ($fishing.timingCast -or $fishing.casting -or $fishing.bobberInAir)
            $unspent = (Get-Runtime "cast-$attempt-stamina").localPlayer.data.stamina -eq $staminaBeforeCast
            Assert-That ($attempt -eq 1 -and $idle -and $unspent) 'the cast did not put the bobber in the water.'
            Write-Step 'The rod is idle after the first cast; casting once more.'
        }
        Assert-That ($fishing.fishing -eq $true -and $fishing.bobberTileY -gt 0) 'the cast did not put the bobber in the water.'
        Write-Step "Cast: bobber at $($fishing.bobberTileX),$($fishing.bobberTileY). Hooking and the minigame are not covered."
    }

    if ($partial) { Write-Step 'The selected sections passed. Run without -Only for acceptance evidence.' }
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

#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: everyday keyboard-and-mouse player actions in a single review on the test save (#270).

.DESCRIPTION
Proves each action through a state read, not through the input acknowledgement:

- walking in four directions (tile changes and the reported facing) and walking versus running (distance per tick count);
- stardew_world_move: exactly one tile in each of the four directions back to the start tile (the reported tile, facing and
  reached flag, confirmed by a runtime read), a turn that keeps the tile, and a blocked target refused with
  worldMoveTargetBlocked before any input;
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
sections: walk, move, mapwarp, hotbar, menu, tools, act, item, npc, fishing. Sections always run in this order, and each one
starts from the Farm. Such a partial run writes its evidence under player-actions-partial and never counts as
acceptance; PRs and releases need a full run.

-Topology network-2 -Role <host|farmhand> runs the input-only sections (walk, hotbar, menu) through the selected
network role, and -Screen <id> (1 or higher; screen 0 is the host) runs them on one screen of a split-screen test-save
review (#299). The other sections (move, mapwarp, tools, act, item, npc, fishing) need fixture or world actions that
are single-only (#297), so those modes skip them with a console note; a role run still uses stardew_fixture_farm and
stardew_lab_warp as setup on the host or farmhand, a screen has no warp and starts from the local farmhand's cabin bed.
Every tool call and every menu, inventory and world read carries the role or screen selection; every runtime read
asserts the selected role or screen, and inventory reads assert that player's ID. Such a run proves only walking,
hotbar and menu input on that player and is not full acceptance; it complements the full single run. Evidence goes to
player-actions-<role> or player-actions-screen<id>. The topology the run started (network-2 for a role, single for a
screen) is stopped and reset at the end, also after a failure. A network-2 start that fails with gameSideStale (#325)
stops and resets its host and can simply be rerun.

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Only npc,item

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Topology network-2 -Role farmhand

.EXAMPLE
pwsh scripts/acceptance/player-actions.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance -Screen 1
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [ValidateSet('single', 'network-2')] [string] $Topology = 'single',
    [ValidateSet('host', 'farmhand')] [string] $Role,
    [ValidateRange(1, 99)] [int] $Screen,
    [switch] $SkipProtectedCheck,
    [string[]] $Only
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
# Accept both -Only npc,item from PowerShell and a single comma-separated string from pwsh -File.
$sections = @('walk', 'move', 'mapwarp', 'hotbar', 'menu', 'tools', 'act', 'item', 'npc', 'fishing')
$inputOnly = @('walk', 'hotbar', 'menu')
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
if ($PSBoundParameters.ContainsKey('Only') -and $Only.Count -eq 0) { throw "-Only needs at least one section. Use: $($sections -join ', ')." }
$unknown = @($Only | Where-Object { $_ -cnotin $sections })
if ($unknown.Count -gt 0) { throw "Unknown -Only section(s): $($unknown -join ', '). Use: $($sections -join ', ')." }
$partial = $Only.Count -gt 0

# The selected player: an unbound single review (the default), one network-2 role, or one split-screen screen.
$screenMode = $PSBoundParameters.ContainsKey('Screen')
if ($Topology -eq 'network-2' -and -not $Role) { throw '-Topology network-2 needs -Role host or -Role farmhand.' }
if ($Role -and $Topology -ne 'network-2') { throw '-Role needs -Topology network-2.' }
if ($screenMode -and $Topology -ne 'single') { throw '-Screen selects a screen of a split-screen single review; it cannot be combined with -Topology network-2.' }
# Wrapped in @(), because an empty array assigned from an if statement collapses to $null.
$selection = @(if ($Role) { @('--topology', 'network-2', '--role', $Role) } elseif ($screenMode) { @('--screen', "$Screen") })
$mode = if ($Role) { $Role } elseif ($screenMode) { "screen$Screen" } else { 'single' }
if ($mode -ne 'single') {
    $singleOnly = @($Only | Where-Object { $_ -cnotin $inputOnly })
    if ($singleOnly.Count -gt 0) { throw "The $mode mode runs only the input-only sections ($($inputOnly -join ', ')); $($singleOnly -join ', ') need fixture or world actions that are single-only (#297)." }
}
$name = if ($partial) { ($mode -eq 'single') ? 'player-actions-partial' : "player-actions-partial-$mode" } elseif ($mode -eq 'single') { 'player-actions' } else { "player-actions-$mode" }
$context = Initialize-Acceptance -Name $name -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck -Selection $selection

function Test-Section([string[]] $Names) {
    # Every section runs in a full single run; -Only selects some for a partial development run; a role or screen
    # run covers only the input-only sections.
    if ($mode -ne 'single' -and @($Names | Where-Object { $_ -cin $inputOnly }).Count -eq 0) { return $false }
    -not $partial -or @($Names | Where-Object { $_ -in $Only }).Count -gt 0
}

function Get-Menu([string] $Label) {
    (Invoke-SdvKit $Label (@('project', 'review', 'menu', '--json') + $selection)).Json
}

function Get-Inventory([string] $Label) {
    $data = (Invoke-SdvKit $Label (@('project', 'review', 'inventory', '--json') + $selection)).Json.data
    if ($mode -ne 'single' -and $null -ne $script:boundPlayerId) {
        Assert-That ([string]$data.playerId -ceq $script:boundPlayerId) "the inventory read '$Label' reports player $($data.playerId), not the bound player $script:boundPlayerId."
    }
    $data
}

function Join-SplitScreen {
    # Opts the ready single test-save review into local split-screen, then requires the selected screen to answer a
    # runtime read with its own screen ID. The game's runner joins the fixture's marked local farmhand on a new screen.
    Invoke-SdvKit 'split-screen-join' @('project', 'review', 'split-screen', 'join', '--json') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $attempt = 0
    while ($true) {
        Start-Sleep -Seconds 3
        $attempt++
        $status = (Invoke-SdvKit "split-screen-status-$attempt" @('project', 'review', 'status', '--topology', 'single', '--json')).Json
        if ($status.lab.gameSide.testSave.localSplitScreen -eq $true) {
            $runtime = (Invoke-Tool "runtime-screen-$attempt" 'stardew_runtime_get' -AllowExit @(0, 3)).Json
            if ($runtime -and $runtime.PSObject.Properties['screen'] -and $runtime.screen.screenId -eq $Screen) {
                Write-Step "Screen $Screen joined: farmer $($runtime.screen.farmerId), context $($runtime.screen.contextId), in $($runtime.runtime.locationId) at $($runtime.runtime.tileX),$($runtime.runtime.tileY)."
                return
            }
        }
        Assert-That ([DateTime]::UtcNow -le $deadline) "screen $Screen did not become readable after the split-screen join; read the reported screen IDs from 'project review split-screen status'."
    }
}

$stones = @('(O)343', '(O)450', '(O)668', '(O)670', '(O)760', '(O)762')
$weeds = @('(O)313', '(O)314', '(O)315', '(O)316', '(O)317', '(O)318', '(O)319', '(O)320', '(O)321', '(O)674', '(O)675', '(O)676', '(O)677', '(O)678', '(O)679', '(O)784', '(O)785', '(O)786', '(O)792', '(O)793', '(O)794')

$boundPlayerId = $null
function Get-Runtime([string] $Label) {
    # In a role or screen run the read must come from the selected player; later inventory reads must show the same player.
    $result = (Invoke-Tool "runtime-$Label" 'stardew_runtime_get').Json
    if ($Role) { Assert-That ($result.role -ceq $Role) "the runtime read '$Label' came from role '$($result.role)', not $Role." }
    elseif ($screenMode) { Assert-That ($result.PSObject.Properties['screen'] -and $result.screen.screenId -eq $Screen) "the runtime read '$Label' is not bound to screen $Screen." }
    if ($mode -ne 'single') {
        $playerId = [string]$result.runtime.localPlayer.data.playerId
        if ($null -eq $script:boundPlayerId) { $script:boundPlayerId = $playerId }
        Assert-That ($playerId -ceq $script:boundPlayerId) "the runtime read '$Label' reports player $playerId, not the bound player $script:boundPlayerId."
    }
    $result.runtime
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
    $revision = (Get-Menu "menu-$Label").uiRevision
    $chord = Invoke-Tool "chord-$Label" 'stardew_input_chord' @{ buttons = $Buttons; durationTicks = $Ticks; uiRevision = $revision } -OptIn '--allow-input'
    Assert-That ($chord.Json.succeeded -eq $true -and $chord.Json.released -eq $true) "holding $($Buttons -join '+') did not complete."
}

function Invoke-Warp([string] $Label, [string] $Location, [int] $X, [int] $Y) {
    $warp = Invoke-Tool "warp-$Label" 'stardew_lab_warp' @{ location = $Location; x = $X; y = $Y } -OptIn '--allow-fixture-actions'
    Assert-That ($warp.Json.state -ceq 'ready' -and $warp.Json.lab.locationId -ceq $Location -and $warp.Json.lab.tileX -eq $X -and $warp.Json.lab.tileY -eq $Y) "the warp to $Location $X,$Y did not land there."
    Wait-Settled
}

function Get-Area([string] $Label, [int] $X, [int] $Y, [int] $Width, [int] $Height) {
    (Invoke-SdvKit "world-$Label" (@('project', 'review', 'world', $X, $Y, $Width, $Height, '--json') + $selection)).Json.data.tiles
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
        $menu = (Get-Menu "menu-$Label-$attempt")
        if ($menu.menuOpen -eq $false) { return }
        $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
        Assert-That ($box.Count -eq 1) "expected a dialogue, found $(@($menu.menus | ForEach-Object type) -join ', ')."
        Invoke-ComponentClick "$Label-$attempt" -Menu $menu -Component $box[0] | Out-Null
        Wait-Settled
    }
    Assert-That ((Get-Menu "menu-$Label-closed").menuOpen -eq $false) 'the dialogue did not close.'
}

function Find-ToolSlot([string] $Label, [string] $ItemId) {
    $slot = @((Get-Inventory "inventory-$Label").slots |
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
    # The single test save is the baseline for every topology; a network-2 review loads it for both roles.
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null
    if ($Topology -eq 'network-2') {
        Invoke-SdvKit 'start' @('project', 'review', 'start', $mod, '--game-path', $context.GamePath, '--topology', 'network-2', '--json') | Out-Null
        Wait-ReviewReady -Topology 'network-2' | Out-Null
    }
    else {
        Invoke-SdvKit 'start' @('project', 'review', 'start', $mod, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
        Wait-ReviewReady -TestSave | Out-Null
        if ($screenMode) { Join-SplitScreen }
    }

    if ($screenMode) {
        Write-Step "Screen ${Screen}: fixture tools are single-only (#297), so the local farmhand is not warped and starts in its cabin bed."
    }
    else {
        Invoke-Tool 'farm' 'stardew_fixture_farm' -OptIn '--allow-fixture-actions' | Out-Null
        Wait-Settled
    }
    if ($mode -ne 'single') { Write-Step "$mode mode: only the input-only sections run ($($inputOnly -join ', ')); move, mapwarp, tools, act, item, npc and fishing need fixture or world actions that are single-only (#297) and are skipped. This proves input on the selected player only and is not full acceptance." }
    $spot = $null
    if ($partial) { Write-Step "Partial run ($($Only -join ', ')): development only, not acceptance evidence." }

    if (Test-Section 'walk') {
        Write-Step 'Walking: four directions, then walking versus running.'
        if ($screenMode) {
            # No warp on a screen: the local farmhand starts in its bed (9,9 of the fixture's cabin). Left, up, down and
            # right in this order stay on free floor and rug tiles and end on the bed again; the bed blocks walking
            # south from the tile above it, so the single order S, D, A, W cannot be used here.
            $steps = @(@{ key = 'A'; dx = -1; dy = 0; facing = 'left' }, @{ key = 'W'; dx = 0; dy = -1; facing = 'up' }, @{ key = 'S'; dx = 0; dy = 1; facing = 'down' }, @{ key = 'D'; dx = 1; dy = 0; facing = 'right' })
        }
        else {
            Invoke-Warp 'walk-start' 'Farm' 64 16
            $steps = @(@{ key = 'S'; dx = 0; dy = 1; facing = 'down' }, @{ key = 'D'; dx = 1; dy = 0; facing = 'right' }, @{ key = 'A'; dx = -1; dy = 0; facing = 'left' }, @{ key = 'W'; dx = 0; dy = -1; facing = 'up' })
        }
        $start = Get-Runtime 'walk-start'
        $position = $start
        foreach ($step in $steps) {
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

        if ($screenMode) {
            # From the bed, run west over the rug, then walk back east over the same tiles. Without a warp to a tile
            # center the run ends at a tile edge, so the distances are measured in tiles from the sub-tile position.
            $runStart = Get-Runtime 'run-start'
            Invoke-Hold 'run' @('A') 30
            Wait-Settled
            $ran = Get-Runtime 'run'
            $run = [Math]::Round($runStart.localPlayer.data.position.standingX - $ran.localPlayer.data.position.standingX, 2)
            Invoke-Hold 'walk-slow' @('LeftShift', 'D') 30
            Wait-Settled
            $walk = [Math]::Round((Get-Runtime 'walk-slow').localPlayer.data.position.standingX - $ran.localPlayer.data.position.standingX, 2)
        }
        else {
            Invoke-Warp 'run-start' 'Farm' 64 16
            Invoke-Hold 'run' @('S') 30
            Wait-Settled
            $run = (Get-Runtime 'run').tileY - $start.tileY
            Invoke-Warp 'walk-start-2' 'Farm' 64 16
            Invoke-Hold 'walk-slow' @('LeftShift', 'S') 30
            Wait-Settled
            $walk = (Get-Runtime 'walk-slow').tileY - $start.tileY
        }
        # Whole tiles from a tile center in single and role runs; on a screen, sub-tile distances with half a tile as the minimum.
        $walkMinimum = if ($screenMode) { 0.5 } else { 1 }
        Assert-That ($run -gt $walk -and $walk -ge $walkMinimum) "30 ticks moved $run tiles normally and $walk with Shift; expected Shift to be slower but still moving."
        Write-Step "Walked 4 directions; 30 ticks moved $run tiles normally, $walk with Shift."
    }

    if (Test-Section 'move', 'tools', 'act', 'item', 'npc') {
        # One world read for the sections that pick their tiles from it.
        $area = @(Get-Area 'scan' 58 18 16 16)
        $byTile = @{}
        foreach ($tile in $area) { $byTile["$($tile.x),$($tile.y)"] = $tile }
        function Test-Free($tile) { $null -ne $tile -and $tile.objectState -ceq 'missing' -and $null -eq $tile.soil }
        function Find-Target([scriptblock] $Match) {
            @($area | Where-Object { (& $Match $_) -and (Test-Free $byTile["$($_.x),$($_.y - 1)"]) })[0]
        }
    }

    if (Test-Section 'move') {
        Write-Step 'Bounded move: exactly one tile in four directions back to the start, a turn, and a blocked target refused.'
        Invoke-Warp 'move-start' 'Farm' 64 16
        $start = Get-Runtime 'move-start'
        $position = $start
        foreach ($step in @(@{ dir = 'down'; dx = 0; dy = 1 }, @{ dir = 'right'; dx = 1; dy = 0 }, @{ dir = 'up'; dx = 0; dy = -1 }, @{ dir = 'left'; dx = -1; dy = 0 })) {
            $expectedX = $position.tileX + $step.dx
            $expectedY = $position.tileY + $step.dy
            $move = Invoke-Tool "move-$($step.dir)" 'stardew_world_move' @{ direction = $step.dir } -OptIn '--allow-world-actions'
            Assert-That ($move.Json.state -ceq 'completed' -and $move.Json.reached -eq $true) "moving $($step.dir) from $($position.tileX),$($position.tileY) returned $($move.Json.state)/$($move.Json.errorCode) with reached=$($move.Json.reached) at $($move.Json.tileX),$($move.Json.tileY)."
            Assert-That ($move.Json.tileX -eq $expectedX -and $move.Json.tileY -eq $expectedY -and $move.Json.targetX -eq $expectedX -and $move.Json.targetY -eq $expectedY) "moving $($step.dir) reported $($move.Json.tileX),$($move.Json.tileY) (target $($move.Json.targetX),$($move.Json.targetY)), expected $expectedX,$expectedY."
            Assert-That ($move.Json.position.facing -ceq $step.dir -and $move.Json.locationId -ceq 'Farm') "after moving $($step.dir) the player faces $($move.Json.position.facing) in $($move.Json.locationId)."
            Assert-That ([Math]::Floor($move.Json.position.standingX) -eq $expectedX -and [Math]::Floor($move.Json.position.standingY) -eq $expectedY) "the standing position $($move.Json.position.standingX),$($move.Json.position.standingY) is not inside $expectedX,$expectedY."
            Wait-Settled
            $next = Get-Runtime "move-$($step.dir)"
            Assert-That ($next.tileX -eq $expectedX -and $next.tileY -eq $expectedY) "a runtime read after moving $($step.dir) shows $($next.tileX),$($next.tileY), not $expectedX,$expectedY."
            $position = $next
        }
        Assert-That ($position.tileX -eq $start.tileX -and $position.tileY -eq $start.tileY) "four moves ended at $($position.tileX),$($position.tileY), not back at $($start.tileX),$($start.tileY)."

        $turn = Invoke-Tool 'move-turn' 'stardew_world_move' @{ direction = 'down'; turnOnly = $true } -OptIn '--allow-world-actions'
        Assert-That ($turn.Json.state -ceq 'completed' -and $turn.Json.reached -eq $true -and $turn.Json.turnOnly -eq $true) "the turn returned $($turn.Json.state)/$($turn.Json.errorCode) with reached=$($turn.Json.reached)."
        Assert-That ($turn.Json.tileX -eq $position.tileX -and $turn.Json.tileY -eq $position.tileY -and $turn.Json.position.facing -ceq 'down') "the turn left the player at $($turn.Json.tileX),$($turn.Json.tileY) facing $($turn.Json.position.facing)."
        Wait-Settled
        $turned = Get-Runtime 'move-turned'
        Assert-That ($turned.tileX -eq $position.tileX -and $turned.tileY -eq $position.tileY -and $turned.localPlayer.data.position.facing -ceq 'down') 'a runtime read after the turn disagrees with its result.'

        $blocker = Find-Target { param($t) $t.objectQualifiedItemId -cin ($stones + $weeds) }
        Assert-That ($null -ne $blocker) 'no stone or weeds with a free tile north of it in the scanned area.'
        Invoke-Warp 'move-blocked' 'Farm' $blocker.x ($blocker.y - 1)
        $blocked = Invoke-Tool 'move-blocked' 'stardew_world_move' @{ direction = 'down' } -OptIn '--allow-world-actions' -AllowExit @(3)
        Assert-That ($blocked.Json.state -ceq 'unavailable' -and $blocked.Json.errorCode -ceq 'worldMoveTargetBlocked' -and $blocked.Json.dispatchState -ceq 'notDispatched' -and $blocked.Json.reached -eq $false) "moving onto $($blocker.objectQualifiedItemId) at $($blocker.x),$($blocker.y) returned $($blocked.Json.state)/$($blocked.Json.errorCode)/$($blocked.Json.dispatchState)."
        Assert-That ($blocked.Json.tileX -eq $blocker.x -and $blocked.Json.tileY -eq $blocker.y - 1) "the refusal reports $($blocked.Json.tileX),$($blocked.Json.tileY), not the start tile."
        Wait-Settled
        $still = Get-Runtime 'move-blocked-after'
        Assert-That ($still.tileX -eq $blocker.x -and $still.tileY -eq $blocker.y - 1) 'the refused move changed the tile.'
        Write-Step "Moved one tile down, right, up and left back to $($start.tileX),$($start.tileY); turned in place; $($blocker.objectQualifiedItemId) at $($blocker.x),$($blocker.y) was refused."
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
        # Later sections read the player's location; go back to the Farm, where the next section starts anyway.
        Invoke-Warp 'mapwarp-return' 'Farm' 64 16
    }

    if (Test-Section 'hotbar') {
        Write-Step 'Hotbar: number keys select slots; the mouse wheel needs a menu.'
        if (-not $screenMode) { Invoke-Warp 'hotbar' 'Farm' 64 16 }
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
        $menu = (Get-Menu 'menu-opened')
        $gameMenu = @($menu.menus | Where-Object adapter -ceq 'gameMenu')[0]
        Assert-That ($null -ne $gameMenu -and $gameMenu.currentTab -eq 0) 'E did not open the game menu on its first tab.'
        if ($screenMode) {
            # Input is screen-local: the menu opened on the selected screen only, and the host screen shows none.
            $hostMenu = (Invoke-SdvKit 'menu-screen-0' @('project', 'review', 'menu', '--screen', '0', '--json')).Json
            Assert-That ($hostMenu.menuOpen -eq $false) "E on screen $Screen also opened a menu on screen 0."
        }
        $tabs = @($gameMenu.components | Where-Object kind -ceq 'tab' | Sort-Object { $_.bounds.x })
        Assert-That ($tabs.Count -ge 4) "the game menu shows $($tabs.Count) tabs."
        Write-Step 'Inventory: move the first item to an empty slot (click to pick up, click to place).'
        $inventory = (Get-Inventory 'move-inventory-before')
        $from = @($inventory.slots | Where-Object { $null -ne $_.item })[0]
        $to = @($inventory.slots | Where-Object { $_.state -ceq 'empty' })[0]
        Assert-That ($null -ne $from -and $null -ne $to) 'the backpack needs one item and one empty slot.'
        foreach ($step in @(@{ label = 'pick-up'; slot = $from.slot }, @{ label = 'place'; slot = $to.slot })) {
            # The slots belong to the inventory page's InventoryMenu, a child node of the game menu.
            $current = (Get-Menu "menu-$($step.label)")
            $component = @($current.menus.components | Where-Object { $_.kind -ceq 'inventorySlot' -and $_.controllerId -eq $step.slot -and $_.visibleFlag -and $_.intersectsViewport })
            Assert-That ($component.Count -eq 1) "inventory slot $($step.slot) has no single visible component."
            Invoke-ComponentClick "inventory-$($step.label)" -Menu $current -Component $component[0] | Out-Null
        }

        Wait-Settled
        $moved = (Get-Inventory 'move-inventory-after')
        $fromAfter = @($moved.slots | Where-Object slot -eq $from.slot)[0]
        $toAfter = @($moved.slots | Where-Object slot -eq $to.slot)[0]
        Assert-That ($fromAfter.state -ceq 'empty' -and $toAfter.item.qualifiedItemId -ceq $from.item.qualifiedItemId) "clicking $($from.item.qualifiedItemId) from slot $($from.slot) to $($to.slot) did not move it."

        $menu = (Get-Menu 'menu-after-move')
        $tabs = @(@($menu.menus | Where-Object adapter -ceq 'gameMenu')[0].components | Where-Object kind -ceq 'tab' | Sort-Object { $_.bounds.x })
        Invoke-ComponentClick 'menu-tab' -Menu $menu -Component $tabs[3] | Out-Null
        Wait-Settled
        $switched = @((Get-Menu 'menu-switched').menus | Where-Object adapter -ceq 'gameMenu')[0]
        Assert-That ($switched.currentTab -eq 3) "clicking the fourth tab left currentTab at $($switched.currentTab)."
        Invoke-Press 'menu-close' 'Escape'
        Wait-Settled
        Assert-That ((Get-Menu 'menu-closed').menuOpen -eq $false) 'Escape did not close the game menu.'
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
        # Face away from the target, so the act proves cursor aim whatever the earlier sections left.
        Invoke-Press 'act-hoe-face' 'S'
        $hoeSlot = Find-ToolSlot 'act-hoe' '(T)Hoe'
        Invoke-Press 'act-hoe-select' (Get-SlotKey $hoeSlot)
        Wait-Settled
        # The scan above is older than the hoe, pickaxe and scythe steps: confirm the target is still untilled.
        Assert-That ($null -eq (Get-Area 'act-hoe-target' $east.x $east.y 1 1)[0].soil) "$($east.x),$($east.y) is already tilled."
        $before = Get-Runtime 'act-hoe-before'
        # Prove cursor aim: the player must not already face the target to the east.
        Assert-That ($before.localPlayer.data.position.facing -cne 'right') 'the player already faces the hoe target; the act would not prove cursor aim.'
        $inventory = (Get-Inventory 'act-hoe-inventory')
        $act = Invoke-Tool 'act-hoe' 'stardew_world_act' @{ action = 'useTool'; x = $east.x; y = $east.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($act.Json.state -ceq 'completed' -and $act.Json.button -ceq 'MouseLeft') "useTool did not complete: $($act.Json.errorCode)"
        Start-Sleep -Seconds 2
        Assert-That ((Get-Runtime 'act-hoe-after').localPlayer.data.stamina -lt $before.localPlayer.data.stamina) 'useTool with the hoe did not cost stamina.'
        Assert-That ($null -ne (Get-Area 'act-hoe-result' $east.x $east.y 1 1)[0].soil) "useTool with the hoe did not till $($east.x),$($east.y)."

        $sign = (Invoke-Tool 'sign-property' 'stardew_map_property_get' @{ asset = 'Maps/Farm'; property = 'Action'; scope = 'tile'; source = 'direct'; layer = 'Buildings'; x = 8; y = 7 }).Json
        Assert-That ("$($sign.property.value)" -clike 'Message *') "Maps/Farm has no sign at 8,7 (Action is '$($sign.property.value)')."
        Invoke-Warp 'act-sign' 'Farm' 8 8
        Assert-That ((Get-Runtime 'act-sign-before').localPlayer.data.position.facing -cne 'up') 'the player already faces the sign; the check would not prove cursor aim.'
        $inventory = (Get-Inventory 'act-sign-inventory')
        $far = Invoke-Tool 'act-far' 'stardew_world_act' @{ action = 'check'; x = 8; y = 5; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions' -AllowExit @(3)
        Assert-That ($far.Json.errorCode -ceq 'worldActionTargetNotAdjacent' -and $far.Json.dispatchState -ceq 'notDispatched') "a far target returned $($far.Json.errorCode)/$($far.Json.dispatchState)."
        $check = Invoke-Tool 'act-sign' 'stardew_world_act' @{ action = 'check'; x = 8; y = 7; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($check.Json.state -ceq 'completed' -and $check.Json.button -ceq 'MouseRight') "check did not complete: $($check.Json.errorCode)"
        Wait-Settled
        $dialogue = (Get-Menu 'act-sign-menu')
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
        $beforeUnknown = (Get-Inventory 'item-add-unknown-before').inventoryRevision
        $unknown = (Invoke-Tool 'item-add-unknown' 'stardew_fixture_item_add' @{ qualifiedItemId = '(O)SDVKit_NoSuchItem'; stack = 1 } -OptIn '--allow-fixture-actions' -AllowExit @(3)).Json
        Assert-That ($unknown.state -ceq 'blocked' -and $null -eq $unknown.item) "an unknown item was not refused: $($unknown.message)"
        Assert-That ((Get-Inventory 'item-add-unknown-after').inventoryRevision -ceq $beforeUnknown) 'refusing an unknown item changed the backpack.'
        $toolQuality = (Invoke-Tool 'item-add-tool-quality' 'stardew_fixture_item_add' @{ qualifiedItemId = '(T)Hoe'; stack = 1; quality = 2 } -OptIn '--allow-fixture-actions' -AllowExit @(3)).Json
        Assert-That ($toolQuality.state -ceq 'blocked') 'a tool with gold quality was not refused.'
        $add = (Invoke-Tool 'item-add' 'stardew_fixture_item_add' @{ qualifiedItemId = '(BC)130'; stack = 1 } -OptIn '--allow-fixture-actions').Json
        Assert-That ($add.state -ceq 'ready' -and $add.item.qualifiedItemId -ceq '(BC)130' -and $add.item.stack -eq 1) "item add failed: $($add.message)"
        $added = (Get-Inventory 'item-add-inventory')
        $addedSlot = @($added.slots | Where-Object slot -eq $add.item.slot)[0]
        Assert-That ($addedSlot.item.qualifiedItemId -ceq '(BC)130' -and $added.inventoryRevision -ceq $add.item.inventoryRevision) "the backpack read does not show the chest in slot $($add.item.slot) with the reported revision."
        Invoke-Warp 'place' 'Farm' ($spot.x - 1) $spot.y
        Invoke-Press 'place-select' (Get-SlotKey $add.item.slot)
        Wait-Settled
        $inventory = (Get-Inventory 'place-inventory')
        Assert-That ($inventory.selectedSlot -eq $add.item.slot) "the chest in slot $($add.item.slot) is not selected."
        $place = Invoke-Tool 'place' 'stardew_world_act' @{ action = 'check'; x = $spot.x; y = $spot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($place.Json.state -ceq 'completed') "check with the chest did not complete: $($place.Json.errorCode)"
        Wait-Settled
        Assert-That ((Get-Area 'place-result' $spot.x $spot.y 1 1)[0].objectQualifiedItemId -ceq '(BC)130') "the chest was not placed on $($spot.x),$($spot.y)."
        $placed = (Get-Inventory 'place-inventory-after')
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
        $inventory = (Get-Inventory 'npc-talk-inventory')
        $talk = Invoke-Tool 'npc-talk' 'stardew_world_act' @{ action = 'check'; x = $npcSpot.x; y = $npcSpot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($talk.Json.state -ceq 'completed') "check on Lewis did not complete: $($talk.Json.errorCode)"
        Wait-Settled
        $talkMenu = (Get-Menu 'npc-talk-menu')
        Assert-That (@($talkMenu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' }).Count -eq 1) "talking to Lewis opened $(@($talkMenu.menus | ForEach-Object type) -join ', ')."
        Close-Dialogue 'npc-talk-close'
        $talked = Get-Friendship 'npc-talked' 'Lewis'
        Assert-That ($talked -gt $friendship) "talking to Lewis left friendship at $talked (before $friendship)."

        $gift = (Invoke-Tool 'npc-gift-add' 'stardew_fixture_item_add' @{ qualifiedItemId = '(O)24'; stack = 1 } -OptIn '--allow-fixture-actions').Json
        Assert-That ($gift.state -ceq 'ready') "adding the parsnip failed: $($gift.message)"
        Invoke-Press 'npc-gift-select' (Get-SlotKey $gift.item.slot)
        Wait-Settled
        $inventory = (Get-Inventory 'npc-gift-inventory')
        Assert-That ($inventory.selectedSlot -eq $gift.item.slot) 'the parsnip is not selected.'
        $give = Invoke-Tool 'npc-gift' 'stardew_world_act' @{ action = 'check'; x = $npcSpot.x; y = $npcSpot.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
        Assert-That ($give.Json.state -ceq 'completed') "check with the parsnip did not complete: $($give.Json.errorCode)"
        Wait-Settled
        Close-Dialogue 'npc-gift-close'
        $gifted = Get-Friendship 'npc-gifted' 'Lewis'
        $giftSlot = @((Get-Inventory 'npc-gift-after').slots | Where-Object slot -eq $gift.item.slot)[0]
        Assert-That ($gifted -gt $talked -and $giftSlot.state -ceq 'empty') "the gift did not register: friendship $gifted (after talking $talked), slot $($giftSlot.state)."
        Write-Step "Lewis: friendship $friendship before, $talked after talking, $gifted after the gift."
    }

    if (Test-Section 'fishing') {
        Write-Step 'Fishing: prepare and cast.'
        # A fixed search start keeps the same bank whatever the previous steps did; the default starts at the player.
        # From 64,16 the search picks a side-facing bank (69,30 on the test save's Farm).
        $prepare = Invoke-Tool 'fishing-prepare' 'stardew_fixture_fishing_prepare' @{ skipTutorial = $true; x = 64; y = 16 } -OptIn '--allow-fixture-actions'
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
    Stop-Review -Topology $Topology -Reset -Label 'final'
}
catch {
    $failed = $true
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Complete-Acceptance -Failed:$failed
}

if ($failed) { exit 1 }

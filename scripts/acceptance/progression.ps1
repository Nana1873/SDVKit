#Requires -Version 7.3
<#
.SYNOPSIS
Live acceptance: NPC friendship, mail and quest reads (`project review progression`) through a wrong condition, its correction, a native mailbox receipt, a placed villager and two process reloads.

.DESCRIPTION
Builds docs/examples/progression-recipe/ProgressionProbe with its default config (a wrong 750-point condition) and
reviews the extracted package. The script:

1. seeds exactly 500 Leah friendship points with the sample's prepare command and requires the read to show
   available/500 points, no mail and no quest, and the mod log to say PROGRESSION_PROBE_BLOCKED (required=750);
2. corrects the threshold to 500, accepts the saved config with config-reconcile, evaluates, and requires exactly one
   mailbox entry and one incomplete quest, and the same after a second evaluation;
3. opens the Farm mailbox through the native mailbox action on the Farm, and requires mail received with an empty mailbox
   and the letter on screen, then closes the letter;
4. finishes the objective and requires the quest accepted and completed once;
5. places Leah next to the player with stardew_fixture_npc_place instead of waiting for her schedule, requires her in the
   runtime NPC list on that tile, talks to her with stardew_world_act and requires her friendship to rise;
6. saves, stops without reset and restarts twice with the accepted config: every progression read must be unchanged
   (friendship exactly as saved, mail received, mailbox empty, quest accepted and completed once, no new effects), and
   an evaluation must log PROGRESSION_PROBE_RETAINED without adding any.

The sample's preparation and objective callback are synthetic, as is the direct native mailbox action, so the script
proves reads and persistence, not a player walking to the mailbox or finishing the objective.

Guide: docs/test-a-progression-read.md.

.EXAMPLE
pwsh scripts/acceptance/progression.ps1 -SdvKit C:\Tools\SDVKit\sdvkit.exe -GamePath 'D:\Games\Stardew Valley' -LabRoot D:\Labs\acceptance
#>
param(
    [Parameter(Mandatory)] [string] $SdvKit,
    [Parameter(Mandatory)] [string] $GamePath,
    [Parameter(Mandatory)] [string] $LabRoot,
    [switch] $SkipProtectedCheck
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SdvKitAcceptance.psm1') -Force
$context = Initialize-Acceptance -Name 'progression' -SdvKit $SdvKit -GamePath $GamePath -LabRoot $LabRoot -SkipProtectedCheck:$SkipProtectedCheck

$modId = 'SDVKit.ProgressionProbe'
$mailId = 'SDVKit.ProgressionProbe_RainBarrel'
$questId = 'SDVKit.ProgressionProbe_CheckBarrel'
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-Progression([string] $Label) {
    $read = (Invoke-SdvKit "progression-$Label" @('project', 'review', 'progression', '--npc', 'Leah', '--mail', $mailId, '--quest', $questId, '--json')).Json
    Assert-That ($read.state -ceq 'ready') "the progression read '$Label' is $($read.state)/$($read.errorCode)."
    return $read.data
}

function Get-Effects($Data) {
    # Mail and quest facts as one string: received/tomorrow/mailbox, then quest match/accepted/completed.
    "mail=$($Data.mail.received)/$($Data.mail.tomorrow)/$($Data.mail.mailboxCount) quest=$($Data.quest.matchCount)/$($Data.quest.acceptedCount)/$($Data.quest.completedCount)"
}

function Assert-Effects($Data, [string] $Expected, [string] $Label) {
    Assert-That ((Get-Effects $Data) -ceq $Expected) "after $Label the progression read shows $(Get-Effects $Data), expected $Expected."
}

function Get-Points($Data) {
    Assert-That ($Data.npc.state -ceq 'available') "Leah's friendship read is $($Data.npc.state)."
    return [int]$Data.npc.friendshipPoints
}

function Invoke-Probe([string] $Label, [string] $Command, [string] $Pattern) {
    Invoke-ModLogCommand $Label -Command $Command -Pattern $Pattern -Log $script:smapiLog
}

function Start-Probe([string] $Label, [string] $Target) {
    Invoke-SdvKit "start-$Label" @('project', 'review', 'start', $Target, '--game-path', $context.GamePath, '--topology', 'single', '--test-save', '--json') | Out-Null
    $status = Wait-ReviewReady -TestSave
    $script:smapiLog = Get-SmapiLogPath $status
}

function Save-Fixture([string] $Label) {
    $save = Invoke-Tool "fixture-save-$Label" 'stardew_fixture_save' -OptIn '--allow-fixture-actions'
    Assert-That ($save.Json.state -ceq 'ready' -and $save.Json.mayHaveRun -eq $false) "the fixture save '$Label' did not complete."
}

function Get-Menu([string] $Label) {
    (Invoke-SdvKit "menu-$Label" @('project', 'review', 'menu', '--json')).Json
}

function Get-Area([string] $Label, [int] $X, [int] $Y, [int] $Width, [int] $Height) {
    (Invoke-SdvKit "world-$Label" @('project', 'review', 'world', $X, $Y, $Width, $Height, '--json')).Json.data.tiles
}

function Close-Dialogue([string] $Label) {
    # Escape does not close a DialogueBox; each click on it finishes the text or turns the page. Decide every click from a fresh read.
    foreach ($attempt in 1..8) {
        $menu = Get-Menu "$Label-$attempt"
        if ($menu.menuOpen -eq $false) { return }
        $box = @($menu.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' })
        Assert-That ($box.Count -eq 1) "expected a dialogue, found $(@($menu.menus | ForEach-Object type) -join ', ')."
        Invoke-ComponentClick "$Label-$attempt" -Menu $menu -Component $box[0] | Out-Null
        Start-Sleep -Milliseconds 1500
    }
    Assert-That ((Get-Menu "$Label-closed").menuOpen -eq $false) 'the dialogue did not close.'
}

$failed = $false
try {
    $work = Join-Path $context.Evidence 'work'
    $mod = Join-Path $work 'ProgressionProbe'
    Copy-Item -Recurse -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\examples\progression-recipe\ProgressionProbe') -Destination $mod
    Remove-Item -LiteralPath (Join-Path $mod 'README.md')
    # The default config in the bytes SMAPI keeps after ReadConfig: no BOM, CRLF, no final newline.
    [IO.File]::WriteAllText((Join-Path $mod 'config.json'), "{`r`n  `"RequiredFriendshipPoints`": 750`r`n}", $utf8)

    Write-Step 'Check, build and package the mod once.'
    Invoke-SdvKit 'doctor' @('doctor', '--game-path', $context.GamePath, '--json') | Out-Null
    Invoke-SdvKit 'check' @('project', 'check', $mod, '--json') | Out-Null
    Invoke-SdvKit 'build' @('project', 'build', $mod, '--game-path', $context.GamePath, '--json') | Out-Null
    $package = Invoke-SdvKit 'package' @('project', 'package', $mod, '--game-path', $context.GamePath, '--json')
    $zip = (Resolve-Path -LiteralPath (Join-Path $mod $package.Json.archive)).Path
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'package-default')
    $ready = Join-Path $work 'package-default\ProgressionProbe'
    Invoke-SdvKit 'test-save' @('lab', 'test-save', '--topology', 'single', '--game-path', $context.GamePath, '--json') | Out-Null

    Write-Step 'Start with the wrong 750-point condition and seed 500 Leah points.'
    Start-Probe 'default' $ready
    Invoke-Probe 'prepare' 'progression_probe_prepare' 'PROGRESSION_PROBE_PREPARED setup=synthetic npc=Leah points=500' | Out-Null
    $data = Get-Progression 'prepared'
    Assert-That ((Get-Points $data) -eq 500) "Leah has $(Get-Points $data) points, expected 500."
    Assert-Effects $data 'mail=False/False/0 quest=0/0/0' 'preparation'
    Invoke-Probe 'blocked' 'progression_probe_evaluate' 'PROGRESSION_PROBE_BLOCKED stage=requested npc=Leah points=500 required=750' | Out-Null
    Assert-Effects (Get-Progression 'blocked') 'mail=False/False/0 quest=0/0/0' 'the blocked evaluation'

    Write-Step 'Correct the threshold to 500, reconcile the saved config and deliver the mail and quest once.'
    Invoke-Probe 'correct' 'progression_probe_correct' 'PROGRESSION_PROBE_CORRECTED requiredPoints=500' | Out-Null
    $reconcile = Invoke-SdvKit 'config-reconcile' @('project', 'review', 'config-reconcile', '--mod', $modId, '--json')
    Assert-That ($reconcile.Json.state -ceq 'reconciled') 'the corrected config was not reconciled.'
    $export = [IO.Path]::GetFullPath((Join-Path $context.Lab $reconcile.Json.reconciliation.exportPath))
    Assert-That ((Get-Content -Raw -LiteralPath $export) -match '"RequiredFriendshipPoints":\s*500\b') 'the reconciled export does not hold 500.'
    Invoke-Probe 'evaluate' 'progression_probe_evaluate' 'PROGRESSION_PROBE_DELIVERED stage=requested mailboxAdded=1 questAdded=1' | Out-Null
    Assert-Effects (Get-Progression 'delivered') 'mail=False/False/1 quest=1/0/0' 'the evaluation'
    Invoke-Probe 'evaluate-again' 'progression_probe_evaluate' 'PROGRESSION_PROBE_RETAINED stage=requested duplicateEffects=0' | Out-Null
    Assert-Effects (Get-Progression 'delivered-again') 'mail=False/False/1 quest=1/0/0' 'the repeated evaluation'

    Write-Step 'Native mailbox receipt on the Farm, then the objective.'
    Invoke-Tool 'farm' 'stardew_fixture_farm' -OptIn '--allow-fixture-actions' | Out-Null
    Start-Sleep -Milliseconds 1500
    Invoke-Probe 'open-mail' 'progression_probe_open_mail' 'PROGRESSION_PROBE_MAIL_ACTION nativeHandled=True' | Out-Null
    Start-Sleep -Seconds 1
    $letter = Get-Menu 'letter'
    Assert-That ($letter.menuOpen -eq $true -and @($letter.menus | Where-Object { $_.type -ceq 'StardewValley.Menus.LetterViewerMenu' }).Count -eq 1) "the mailbox opened $(@($letter.menus | ForEach-Object type) -join ', '), expected the letter."
    Assert-Effects (Get-Progression 'received') 'mail=True/False/0 quest=1/0/0' 'the mailbox action'
    Invoke-Tool 'close-letter' 'stardew_input_press' @{ button = 'Escape' } -OptIn '--allow-input' | Out-Null
    Start-Sleep -Seconds 1
    Assert-That ((Get-Menu 'letter-closed').menuOpen -eq $false) 'Escape did not close the letter.'
    Invoke-Probe 'finish' 'progression_probe_finish' 'PROGRESSION_PROBE_FINISHED accepted=True completed=True' | Out-Null
    Assert-Effects (Get-Progression 'finished') 'mail=True/False/0 quest=1/1/1' 'the objective'

    Write-Step 'Place Leah next to the player and talk to her.'
    # The area read has no passability or building-layer data, so a tile it shows empty can still be refused for a character.
    # Candidates come from a Farm scan; only the refusal "not free for a character" moves on to the next one, at most six tiles.
    # -Skip 3 is kept from player-actions.ps1, which uses the first scanned tiles for its earlier sections; it only varies the first try here.
    $area = @(Get-Area 'scan' 58 18 16 16)
    $byTile = @{}
    foreach ($tile in $area) { $byTile["$($tile.x),$($tile.y)"] = $tile }
    function Test-Free($tile) { $null -ne $tile -and $tile.objectState -ceq 'missing' -and $null -eq $tile.soil -and $null -eq $tile.feature }
    $target = $null
    $placed = $null
    foreach ($candidate in @($area | Where-Object { (Test-Free $_) -and (Test-Free $byTile["$($_.x - 1),$($_.y)"]) } | Select-Object -Skip 3 -First 6)) {
        $standX = $candidate.x - 1
        $warp = (Invoke-Tool "warp-stand-$($candidate.x)-$($candidate.y)" 'stardew_lab_warp' @{ location = 'Farm'; x = $standX; y = $candidate.y } -OptIn '--allow-fixture-actions' -AllowExit @(0, 3)).Json
        if ($null -eq $warp -or $warp.state -cne 'ready' -or $warp.lab.locationId -cne 'Farm' -or $warp.lab.tileX -ne $standX -or $warp.lab.tileY -ne $candidate.y) {
            Write-Step "Tile $($candidate.x),$($candidate.y) skipped: the player did not land on $standX,$($candidate.y) ($($warp.message))."
            continue
        }
        Start-Sleep -Milliseconds 1500
        $attempt = (Invoke-Tool "npc-place-$($candidate.x)-$($candidate.y)" 'stardew_fixture_npc_place' @{ name = 'Leah'; x = $candidate.x; y = $candidate.y } -OptIn '--allow-fixture-actions' -AllowExit @(0, 3)).Json
        if ($null -eq $attempt) { throw "Acceptance failed: placing Leah at $($candidate.x),$($candidate.y) returned no result." }
        if ($attempt.state -ceq 'ready') { $target = $candidate; $placed = $attempt; break }
        $reason = "$($attempt.message)"
        if ($reason -notmatch 'not free for a character') { throw "Acceptance failed: placing Leah at $($candidate.x),$($candidate.y) was refused for another reason: $($attempt.state)/$reason" }
        Write-Step "Tile $($candidate.x),$($candidate.y) refused: $reason"
    }
    Assert-That ($null -ne $target) 'the game accepted no scanned Farm tile for Leah.'
    Assert-That ($placed.navigation.locationId -ceq 'Farm' -and $placed.navigation.tileX -eq $target.x -and $placed.navigation.tileY -eq $target.y) "placing Leah reported $($placed.navigation.locationId) $($placed.navigation.tileX),$($placed.navigation.tileY), not Farm $($target.x),$($target.y)."
    Start-Sleep -Milliseconds 1500
    $leah = @((Invoke-Tool 'runtime' 'stardew_runtime_get').Json.runtime.localPlayer.data.npcs | Where-Object name -ceq 'Leah')
    Assert-That ($leah.Count -eq 1 -and $leah[0].tileX -eq $target.x -and $leah[0].tileY -eq $target.y) "the runtime NPC list does not show Leah at $($target.x),$($target.y)."
    $before = Get-Points (Get-Progression 'before-talk')
    # A check with an item selected can read as a gift, so select the hoe first, as player-actions.ps1 does.
    $hoe = @((Invoke-SdvKit 'inventory-hoe' @('project', 'review', 'inventory', '--json')).Json.data.slots | Where-Object { $_.item.qualifiedItemId -ceq '(T)Hoe' })[0]
    Assert-That ($null -ne $hoe) 'the backpack has no hoe to select for the talk.'
    $hoeKey = switch ($hoe.slot) { { $_ -lt 9 } { "D$($hoe.slot + 1)" } 9 { 'D0' } 10 { 'OemMinus' } 11 { 'OemPlus' } }
    $press = Invoke-Tool 'select-hoe' 'stardew_input_press' @{ button = $hoeKey } -OptIn '--allow-input'
    Assert-That ($press.Json.succeeded -eq $true) 'selecting the hoe did not complete.'
    Start-Sleep -Milliseconds 1500
    $inventory = (Invoke-SdvKit 'inventory' @('project', 'review', 'inventory', '--json')).Json.data
    Assert-That ($inventory.selectedSlot -eq $hoe.slot) 'the hoe is not selected for the talk.'
    # Placed villagers can walk again at any time: read her tile right before the check.
    $leah = @((Invoke-Tool 'runtime-before-talk' 'stardew_runtime_get').Json.runtime.localPlayer.data.npcs | Where-Object name -ceq 'Leah')
    Assert-That ($leah.Count -eq 1 -and $leah[0].tileX -eq $target.x -and $leah[0].tileY -eq $target.y) "Leah moved from $($target.x),$($target.y) before the talk."
    $talk = Invoke-Tool 'talk' 'stardew_world_act' @{ action = 'check'; x = $target.x; y = $target.y; inventoryRevision = $inventory.inventoryRevision } -OptIn '--allow-world-actions'
    Assert-That ($talk.Json.state -ceq 'completed') "check on Leah did not complete: $($talk.Json.errorCode)"
    Start-Sleep -Seconds 1
    Assert-That (@((Get-Menu 'talk-menu').menus | Where-Object { $_.type -ceq 'StardewValley.Menus.DialogueBox' }).Count -eq 1) 'talking to Leah did not open a dialogue.'
    Close-Dialogue 'talk-close'
    $talked = Get-Progression 'talked'
    Assert-That ((Get-Points $talked) -gt $before) "talking to Leah left friendship at $(Get-Points $talked) (before $before)."
    Assert-Effects $talked 'mail=True/False/0 quest=1/1/1' 'talking to Leah'
    $saved = Get-Points $talked
    Write-Step "Leah: friendship $before before, $saved after talking."

    Write-Step 'Save and restart twice with the accepted config: every read must stay as saved.'
    Save-Fixture 'before-restart'
    Stop-Review -Label 'before-restart-1'
    foreach ($round in 1, 2) {
        $accepted = Join-Path $work "package-accepted-$round"
        Expand-Archive -LiteralPath $zip -DestinationPath $accepted
        $acceptedReady = Join-Path $accepted 'ProgressionProbe'
        [IO.File]::Copy($export, (Join-Path $acceptedReady 'config.json'), $true)
        foreach ($file in 'manifest.json', 'ProgressionProbe.dll') {
            Assert-That ((Get-FileHash (Join-Path $ready $file)).Hash -eq (Get-FileHash (Join-Path $acceptedReady $file)).Hash) "$file differs from the first package extraction."
        }
        Start-Probe "reload-$round" $acceptedReady
        $reloaded = Get-Progression "reload-$round"
        Assert-That ((Get-Points $reloaded) -eq $saved) "after reload $round Leah has $(Get-Points $reloaded) points, saved $saved."
        Assert-Effects $reloaded 'mail=True/False/0 quest=1/1/1' "reload $round"
        Invoke-Probe "retained-$round" 'progression_probe_evaluate' 'PROGRESSION_PROBE_RETAINED stage=requested duplicateEffects=0' | Out-Null
        Assert-Effects (Get-Progression "retained-$round") 'mail=True/False/0 quest=1/1/1' "the evaluation after reload $round"
        Write-Step "Reload ${round}: Leah $saved points, mail received, quest accepted and completed once, nothing added."
        if ($round -eq 1) { Save-Fixture 'reload-1'; Stop-Review -Label 'between-reloads' }
    }
    # Evidence only: the selected mod's diagnostics are kept, nothing is asserted on them.
    Invoke-SdvKit 'diagnostics' @('project', 'review', 'diagnostics', '--mod', $modId, '--limit', '30', '--json') | Out-Null
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

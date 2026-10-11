#Requires -Version 7.3
# Shared helpers for SDVKit's live acceptance scripts. See scripts/acceptance/README.md.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Context = $null

function Initialize-Acceptance {
    <#
    .SYNOPSIS
    Validates the inputs, prepares the lab root and evidence folder, and checks that the lab is free.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $SdvKit,
        [Parameter(Mandatory)] [string] $GamePath,
        [Parameter(Mandatory)] [string] $LabRoot,
        [switch] $SkipProtectedCheck
    )

    $sdvkitPath = (Resolve-Path -LiteralPath $SdvKit).Path
    $game = (Resolve-Path -LiteralPath $GamePath).Path
    New-Item -ItemType Directory -Force -Path $LabRoot | Out-Null
    $lab = (Resolve-Path -LiteralPath $LabRoot).Path
    if ($lab.StartsWith($game.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The lab root must not be inside the game folder: $lab"
    }

    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $evidence = Join-Path $lab ".sdvkit\acceptance\$Name\$stamp"
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null

    $script:Context = [pscustomobject]@{
        Name = $Name
        SdvKit = $sdvkitPath
        GamePath = $game
        Lab = $lab
        Evidence = $evidence
        Step = 0
        ProtectedRoots = @(
            (Join-Path $game 'Mods'),
            (Join-Path $env:APPDATA 'StardewValley')
        )
        ProtectedBefore = $null
        SkipProtectedCheck = [bool]$SkipProtectedCheck
        Reviews = [Collections.Generic.HashSet[string]]::new()
    }

    Set-Location -LiteralPath $lab
    Write-Step "Lab root $lab, evidence $evidence"

    foreach ($topology in 'single', 'network-2') {
        $status = Invoke-SdvKit "preflight-$topology" @('project', 'review', 'status', '--topology', $topology, '--json')
        Assert-That ($status.Json.state -ceq 'stopped') "The $topology lab is not free (state '$($status.Json.state)'). Wait for its owner to stop it."
    }

    if (-not $script:Context.SkipProtectedCheck) {
        $script:Context.ProtectedBefore = Get-ProtectedFingerprint
        $script:Context.ProtectedBefore | Set-Content -LiteralPath (Join-Path $evidence 'protected-before.txt') -Encoding utf8
    }

    return $script:Context
}

function Get-AcceptanceContext { $script:Context }

function Write-Step([string] $Message) {
    Write-Host "[$($script:Context.Name)] $Message"
}

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw "Acceptance failed: $Message"
    }
}

function Invoke-SdvKit {
    <#
    .SYNOPSIS
    Runs sdvkit once, saves its stdout to the evidence folder, and returns the exit code and parsed JSON.
    Throws unless the exit code is one of -AllowExit (default 0).
    #>
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Label,
        [Parameter(Mandatory, Position = 1)] [string[]] $Arguments,
        [int[]] $AllowExit = @(0)
    )

    $script:Context.Step++
    $file = Join-Path $script:Context.Evidence ('{0:d3}-{1}.json' -f $script:Context.Step, $Label)
    $output = & $script:Context.SdvKit @Arguments 2>&1
    $exit = $LASTEXITCODE
    $stdout = @($output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] }) -join [Environment]::NewLine
    $stderr = @($output | Where-Object { $_ -is [Management.Automation.ErrorRecord] }) -join [Environment]::NewLine
    $stdout | Set-Content -LiteralPath $file -Encoding utf8
    if ($stderr) {
        $stderr | Set-Content -LiteralPath ($file -replace '\.json$', '.stderr.txt') -Encoding utf8
    }

    $json = $null
    if ($stdout.TrimStart().StartsWith('{')) {
        $json = $stdout | ConvertFrom-Json -Depth 100
    }

    if ($Arguments.Count -ge 3 -and $Arguments[0] -eq 'project' -and $Arguments[1] -eq 'review' -and $Arguments[2] -eq 'start') {
        $topology = if ($Arguments -contains 'network-2') { 'network-2' } else { 'single' }
        [void]$script:Context.Reviews.Add($topology)
    }

    if ($exit -notin $AllowExit) {
        throw "sdvkit $($Arguments -join ' ') exited with $exit; see $file"
    }

    return [pscustomobject]@{ Exit = $exit; Json = $json; File = $file }
}

function Invoke-Tool {
    <#
    .SYNOPSIS
    Runs one SDVKit tool through `project review call` with an argument object and optional opt-ins.
    #>
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Label,
        [Parameter(Mandatory, Position = 1)] [string] $Tool,
        [Parameter(Position = 2)] [hashtable] $Arguments = @{},
        [string[]] $OptIn = @(),
        [int[]] $AllowExit = @(0)
    )

    $json = $Arguments | ConvertTo-Json -Compress -Depth 20
    return Invoke-SdvKit $Label (@('project', 'review', 'call', $Tool, $json) + $OptIn) -AllowExit $AllowExit
}

function Wait-ReviewReady {
    <#
    .SYNOPSIS
    Polls review status until GameSide is active, the project mod is loaded and, with -TestSave, the test save passed.
    #>
    param(
        [string] $Topology = 'single',
        [switch] $TestSave,
        [int] $TimeoutSeconds = 240
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        Start-Sleep -Seconds 3
        $status = & $script:Context.SdvKit project review status --topology $Topology --json | ConvertFrom-Json -Depth 100
        $gameSide = if ($status.lab) { $status.lab.gameSide } else { $null }
        $ready = $null -ne $gameSide -and $gameSide.state -ceq 'active' -and $null -ne $gameSide.projectMod -and
            $gameSide.projectMod.phase -ceq 'loaded' -and $gameSide.projectMod.loadConfirmed -eq $true -and
            (-not $TestSave -or ($null -ne $gameSide.testSave -and $gameSide.testSave.phase -ceq 'passed'))
        if ($ready) {
            $status | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $script:Context.Evidence ('{0:d3}-ready.json' -f ++$script:Context.Step)) -Encoding utf8
            Write-Step "Review ready (launch $($status.lab.launchId))."
            return $status
        }

        if (@($status.problems).Count -gt 0 -or [DateTime]::UtcNow -gt $deadline) {
            $status | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $script:Context.Evidence ('{0:d3}-not-ready.json' -f ++$script:Context.Step)) -Encoding utf8
            throw "Acceptance failed: the review did not become ready (state '$($status.state)', problems: $(@($status.problems | ForEach-Object code) -join ', '))."
        }
    }
}

function Stop-Review {
    <#
    .SYNOPSIS
    Stops a review and, for test-save and network reviews, resets it. Verifies the result.
    #>
    param([string] $Topology = 'single', [switch] $Reset, [string] $Label = 'stop')

    $stop = Invoke-SdvKit "$Label-$Topology" @('project', 'review', 'stop', '--topology', $Topology, '--json') -AllowExit @(0, 3)
    $stopped = $stop.Json.state -ceq 'stopped'
    if ($Reset) {
        $resetResult = Invoke-SdvKit "$Label-reset-$Topology" @('project', 'review', 'reset', '--topology', $Topology, '--json') -AllowExit @(0, 3)
        $stopped = $stopped -and $resetResult.Exit -eq 0
    }

    [void]$script:Context.Reviews.Remove($Topology)
    Assert-That $stopped "stop or reset of the $Topology review did not complete; see the evidence folder."
}

function Complete-Acceptance {
    <#
    .SYNOPSIS
    Always runs at the end: stops and resets any review this script started, then compares the protected folders.
    #>
    param([switch] $Failed)

    $problems = [Collections.Generic.List[string]]::new()
    foreach ($topology in @($script:Context.Reviews)) {
        try { Stop-Review -Topology $topology -Reset -Label 'cleanup' }
        catch { $problems.Add($_.Exception.Message) }
    }

    if (-not $script:Context.SkipProtectedCheck -and $null -ne $script:Context.ProtectedBefore) {
        $after = Get-ProtectedFingerprint
        $after | Set-Content -LiteralPath (Join-Path $script:Context.Evidence 'protected-after.txt') -Encoding utf8
        $changes = @(Compare-Object $script:Context.ProtectedBefore $after)
        if ($changes.Count -gt 0) {
            $changes | Out-String | Set-Content -LiteralPath (Join-Path $script:Context.Evidence 'protected-changes.txt') -Encoding utf8
            $problems.Add("$($changes.Count) protected entries changed; see protected-changes.txt. Changes made by you or other programs during the run also count.")
        }
        else {
            Write-Step "Protected folders unchanged ($(@($after).Count) entries)."
        }
    }

    if ($problems.Count -gt 0) {
        throw ("Acceptance cleanup failed: " + ($problems -join ' '))
    }

    $verdict = if ($Failed) { 'FAILED' } else { 'PASSED' }
    Write-Step "$verdict. Evidence: $($script:Context.Evidence)"
}

function Get-ProtectedFingerprint {
    # Read-only listing of the game's Mods folder and the normal Stardew data folder, without following reparse points.
    $entries = foreach ($root in $script:Context.ProtectedRoots) {
        if (-not (Test-Path -LiteralPath $root)) {
            "MISSING $root"
            continue
        }

        $stack = [Collections.Generic.Stack[string]]::new()
        $stack.Push($root)
        while ($stack.Count -gt 0) {
            foreach ($entry in [IO.DirectoryInfo]::new($stack.Pop()).EnumerateFileSystemInfos()) {
                $relative = $root + '|' + $entry.FullName.Substring($root.Length)
                $reparse = [bool]($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)
                if ($entry -is [IO.DirectoryInfo]) {
                    "D $relative"
                    if (-not $reparse) { $stack.Push($entry.FullName) }
                }
                elseif ($reparse) {
                    "F $relative reparse"
                }
                else {
                    "F $relative $($entry.Length) $($entry.LastWriteTimeUtc.ToString('o'))"
                }
            }
        }
    }

    $entries | Sort-Object
}

function Save-ViewportScreenshot {
    <#
    .SYNOPSIS
    Captures the single review's viewport and copies the PNG into the evidence folder.
    #>
    param([Parameter(Mandatory, Position = 0)] [string] $Label)

    $name = "$($script:Context.Name)-$Label-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $shot = Invoke-Tool "screenshot-$Label" 'stardew_screenshot_capture' @{ mode = 'viewport'; label = $name }
    $file = Join-Path $script:Context.Lab ".sdvkit\lab\profiles\single\AppData\Roaming\StardewValley\Screenshots\$($shot.Json.fileName)"
    Assert-That (Test-Path -LiteralPath $file -PathType Leaf) "screenshot $Label was not written."
    $hash = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-That ($hash -ceq $shot.Json.sha256) "screenshot $Label does not match its capture result."
    Copy-Item -LiteralPath $file -Destination (Join-Path $script:Context.Evidence "$Label.png")
}

function Get-ComponentCenter {
    # The center of a menu component's screen bounds.
    param([Parameter(Mandatory)] $Component)
    [pscustomobject]@{
        X = [int]$Component.bounds.x + [int][Math]::Floor($Component.bounds.width / 2)
        Y = [int]$Component.bounds.y + [int][Math]::Floor($Component.bounds.height / 2)
    }
}

function Invoke-ComponentClick {
    <#
    .SYNOPSIS
    Clicks the center of a menu component with process-local input, bound to the menu capture's uiRevision.
    Fails without retrying when the click does not complete.
    #>
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Label,
        [Parameter(Mandatory)] $Menu,
        [Parameter(Mandatory)] $Component,
        [string] $Button = 'MouseLeft'
    )

    $point = Get-ComponentCenter $Component
    $click = Invoke-Tool $Label 'stardew_input_click' @{
        x = $point.X; y = $point.Y; button = $Button; count = 1; uiRevision = [string]$Menu.uiRevision
    } -OptIn '--allow-input'
    Assert-That ($click.Json.succeeded -eq $true -and $click.Json.released -eq $true) "the click '$Label' did not complete. Inspect the game state before any repeat."
    return $click
}

Export-ModuleMember -Function Initialize-Acceptance, Get-AcceptanceContext, Write-Step, Assert-That, Invoke-SdvKit, Invoke-Tool,
    Wait-ReviewReady, Stop-Review, Complete-Acceptance, Get-ProtectedFingerprint, Save-ViewportScreenshot, Get-ComponentCenter, Invoke-ComponentClick

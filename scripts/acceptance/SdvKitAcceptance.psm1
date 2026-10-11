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
    $game = (Resolve-Path -LiteralPath $GamePath).Path.TrimEnd('\')
    $stardewData = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'StardewValley')).TrimEnd('\')
    # Validate before creating anything: the lab must not be, or be inside, the game folder or the normal Stardew data.
    $lab = [IO.Path]::GetFullPath($LabRoot).TrimEnd('\')
    foreach ($protected in $game, $stardewData) {
        if ($lab.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or
            $lab.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "The lab root must not be inside ${protected}: $lab"
        }
    }
    New-Item -ItemType Directory -Force -Path $lab | Out-Null

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
        ProtectedRoots = @((Join-Path $game 'Mods'), $stardewData)
        ProtectedBefore = $null
        SkipProtectedCheck = [bool]$SkipProtectedCheck
        # Topologies this run started and may still need to stop, and those whose test save still needs a reset.
        Running = [Collections.Generic.HashSet[string]]::new()
        ResetPending = [Collections.Generic.HashSet[string]]::new()
    }

    Set-Location -LiteralPath $lab
    Write-Step "Lab root $lab, evidence $evidence"

    foreach ($topology in 'single', 'network-2') {
        $status = (Invoke-SdvKit "preflight-$topology" @('project', 'review', 'status', '--topology', $topology, '--json')).Json
        # Free means stopped with nothing retained: no review root and no staging from a paused persistence test.
        Assert-That ($status.state -ceq 'stopped' -and $null -eq $status.root -and $status.stagingRemoved -eq $true) "The $topology lab is not free (state '$($status.state)', root '$($status.root)'). Wait for its owner to stop and reset it."
    }

    Write-Identity

    if (-not $script:Context.SkipProtectedCheck) {
        $script:Context.ProtectedBefore = Get-ProtectedFingerprint
        $script:Context.ProtectedBefore | Set-Content -LiteralPath (Join-Path $evidence 'protected-before.txt') -Encoding utf8
    }

    return $script:Context
}

function Get-AcceptanceContext { $script:Context }

function Write-Identity {
    # Records what was tested: the sdvkit build and the game and SMAPI versions.
    $version = & $script:Context.SdvKit --version
    $identity = [ordered]@{
        sdvkitVersion = "$version".Trim()
        sdvkitSha256 = (Get-FileHash -LiteralPath $script:Context.SdvKit -Algorithm SHA256).Hash
        game = (Get-Item -LiteralPath (Join-Path $script:Context.GamePath 'Stardew Valley.dll')).VersionInfo.FileVersion
        smapi = (Get-Item -LiteralPath (Join-Path $script:Context.GamePath 'StardewModdingAPI.dll')).VersionInfo.FileVersion
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:Context.Evidence 'identity.json') -Encoding utf8
}

function Add-ProviderIdentity([string] $Name, [string] $Folder) {
    # Records a copied provider's manifest and DLL hashes next to identity.json.
    $hashes = Get-ChildItem -LiteralPath $Folder -File | Where-Object { $_.Extension -in '.json', '.dll' } |
        ForEach-Object { [ordered]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }
    @($hashes) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:Context.Evidence "identity-$Name.json") -Encoding utf8
}

function Test-OwnRoot($Root) {
    # A review belongs to this run when its root lies below this run's evidence folder.
    if ([string]::IsNullOrEmpty([string]$Root)) { return $false }
    $full = [IO.Path]::GetFullPath([string]$Root)
    return $full.StartsWith($script:Context.Evidence.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

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

    # A review counts as this run's only when its start succeeded and status names this run's target.
    if ($exit -eq 0 -and $Arguments.Count -ge 3 -and $Arguments[0] -eq 'project' -and $Arguments[1] -eq 'review' -and $Arguments[2] -eq 'start') {
        $topology = if ($Arguments -contains 'network-2') { 'network-2' } else { 'single' }
        $status = & $script:Context.SdvKit project review status --topology $topology --json | ConvertFrom-Json -Depth 100
        if (-not (Test-OwnRoot $status.root)) {
            throw "Acceptance failed: after start, the $topology review belongs to '$($status.root)', not to this run."
        }

        [void]$script:Context.Running.Add($topology)
        [void]$script:Context.ResetPending.Add($topology)
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
        # A status read that fails or prints nothing counts as "not ready yet", not as a failure.
        $status = $null
        $ready = $false
        try {
            $status = & $script:Context.SdvKit project review status --topology $Topology --json | ConvertFrom-Json -Depth 100
            $gameSide = $status.lab.gameSide
            $ready = $gameSide.state -ceq 'active' -and $gameSide.projectMod.phase -ceq 'loaded' -and
                $gameSide.projectMod.loadConfirmed -eq $true -and (-not $TestSave -or $gameSide.testSave.phase -ceq 'passed')
        }
        catch { $ready = $false }
        if ($ready) {
            $status | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $script:Context.Evidence ('{0:d3}-ready.json' -f ++$script:Context.Step)) -Encoding utf8
            Write-Step "Review ready (launch $($status.lab.launchId))."
            return $status
        }

        $problems = @(if ($null -ne $status -and $status.PSObject.Properties['problems']) { $status.problems })
        if ($problems.Count -gt 0 -or [DateTime]::UtcNow -gt $deadline) {
            $status | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $script:Context.Evidence ('{0:d3}-not-ready.json' -f ++$script:Context.Step)) -Encoding utf8
            throw "Acceptance failed: the review did not become ready (problems: $(@($problems | ForEach-Object code) -join ', '))."
        }
    }
}

function Stop-Review {
    <#
    .SYNOPSIS
    Stops this run's review and, with -Reset, resets its test save. Refuses a review that belongs to someone else.
    #>
    param([string] $Topology = 'single', [switch] $Reset, [string] $Label = 'stop')

    # Never act on a review this run did not start.
    $status = (Invoke-SdvKit "$Label-status-$Topology" @('project', 'review', 'status', '--topology', $Topology, '--json') -AllowExit @(0, 3)).Json
    if ($null -ne $status -and -not [string]::IsNullOrEmpty([string]$status.root) -and -not (Test-OwnRoot $status.root)) {
        throw "Acceptance failed: the $Topology review now belongs to '$($status.root)'; not stopping or resetting it."
    }

    $stop = Invoke-SdvKit "$Label-$Topology" @('project', 'review', 'stop', '--topology', $Topology, '--json') -AllowExit @(0, 3)
    Assert-That ($stop.Json.state -ceq 'stopped') "stop of the $Topology review did not complete; see the evidence folder."
    [void]$script:Context.Running.Remove($Topology)
    if ($Reset) {
        $resetResult = Invoke-SdvKit "$Label-reset-$Topology" @('project', 'review', 'reset', '--topology', $Topology, '--json') -AllowExit @(0, 3)
        Assert-That ($resetResult.Exit -eq 0) "reset of the $Topology review did not complete; see the evidence folder."
        [void]$script:Context.ResetPending.Remove($Topology)
    }
}

function Complete-Acceptance {
    <#
    .SYNOPSIS
    Always runs at the end: stops and resets any review this script started, then compares the protected folders.
    #>
    param([switch] $Failed)

    $problems = [Collections.Generic.List[string]]::new()
    # Stop and reset what this run started, including a test save left between a stop and a planned restart.
    foreach ($topology in @(@($script:Context.Running) + @($script:Context.ResetPending) | Select-Object -Unique)) {
        try { Stop-Review -Topology $topology -Reset -Label 'cleanup' }
        catch { $problems.Add($_.Exception.Message) }
    }

    if (-not $script:Context.SkipProtectedCheck -and $null -ne $script:Context.ProtectedBefore) {
        $after = Get-ProtectedFingerprint
        $after | Set-Content -LiteralPath (Join-Path $script:Context.Evidence 'protected-after.txt') -Encoding utf8
        $changes = @(Compare-Object $script:Context.ProtectedBefore $after -CaseSensitive)
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
    # Read-only listing, without following reparse points: the game folder's top level, its Mods folder recursively,
    # and the normal Stardew data folder (saves, settings, logs) recursively. Size and write time identify each file.
    $game = $script:Context.GamePath
    $top = foreach ($entry in [IO.DirectoryInfo]::new($game).EnumerateFileSystemInfos()) {
        if ($entry -is [IO.DirectoryInfo]) { "D $game|\$($entry.Name)" }
        else { "F $game|\$($entry.Name) $($entry.Length) $($entry.LastWriteTimeUtc.ToString('o'))" }
    }

    $entries = @($top) + @(foreach ($root in $script:Context.ProtectedRoots) {
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
    })

    $entries | Sort-Object -CaseSensitive
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
    Wait-ReviewReady, Stop-Review, Complete-Acceptance, Add-ProviderIdentity, Get-ProtectedFingerprint, Save-ViewportScreenshot, Get-ComponentCenter, Invoke-ComponentClick

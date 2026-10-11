# SDVKit lab guard: a Claude Code PreToolUse hook written by `sdvkit lab init`.
# It blocks file writes and write-like shell commands that target the game folder (including its
# Mods folder), the normal Stardew Saves folder, *.xnb content, or game binaries. It covers Claude
# Code's own Write, Edit, NotebookEdit, Bash and PowerShell tools. It is a mechanical backstop for
# the lab's safety rules, not a sandbox: an unusual command can still get past it.
# Exit code 2 blocks the tool call and shows the message to the agent.
$ErrorActionPreference = 'Stop'

try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
    $request = $reader.ReadToEnd().TrimStart([char]0xFEFF) | ConvertFrom-Json
    $config = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'sdvkit-guard.json') | ConvertFrom-Json
}
catch {
    # A guard that cannot read its input or configuration must not break the session.
    exit 0
}

function ConvertTo-GuardText([string] $Text) {
    # Lower-case, forward slashes, Bash's '\ ' as a space, Git Bash's /e/x as e:/x, and
    # 'a/b/../' collapsed to 'a/'.
    $value = $Text.ToLowerInvariant().Replace('\ ', ' ').Replace('\', '/')
    $value = [regex]::Replace($value, '(^|[\s"''=(])/([a-z])/', '$1$2:/')
    do {
        $before = $value
        $value = [regex]::Replace($value, '[^/\s"'']+/\.\./', '')
    } while ($value -ne $before)
    return $value
}

$roots = @($config.protectedRoots | Where-Object { $_ } | ForEach-Object { (ConvertTo-GuardText $_).TrimEnd('/') + '/' })
$binaries = @('stardew valley.dll', 'stardew valley.exe', 'stardewmoddingapi.dll', 'stardewmoddingapi.exe')

function Test-SavesMention([string] $Text) {
    # A normal Saves folder, except SDVKit's isolated profiles below a .sdvkit folder.
    foreach ($match in [regex]::Matches($Text, 'stardewvalley/saves')) {
        $start = $Text.LastIndexOfAny([char[]]@(' ', '"', "'", '='), [Math]::Max(0, $match.Index - 1)) + 1
        $prefix = $Text.Substring($start, $match.Index - $start)
        if (-not ($prefix.StartsWith('.sdvkit/') -or $prefix.Contains('/.sdvkit/'))) { return $true }
    }
    return $false
}

function Get-TextViolation([string] $Text, [bool] $AsPath) {
    foreach ($root in $roots) {
        if ($Text.Contains($root.TrimEnd('/') + '/') -or $Text.EndsWith($root.TrimEnd('/')) -or ($AsPath -and ($Text + '/') -eq $root)) {
            return "the protected folder '$root'"
        }
    }
    if (Test-SavesMention $Text) { return 'a normal Stardew Saves folder' }
    if ($Text -match '\.xnb\b') { return 'game content (*.xnb)' }
    foreach ($binary in $binaries) {
        if ($Text.Contains($binary)) { return "a game binary ($binary)" }
    }
    return $null
}

function Get-CommandViolation([string] $Command) {
    $text = ConvertTo-GuardText $Command
    # Output operators and cmdlets: check only the rest of their statement, so reading from a
    # protected folder and redirecting elsewhere stays allowed.
    $outputs = [regex]::Matches($text, '[0-9&]?>{1,2}(?!&)|(?:^|[\s;|&(])(?:tee-object|tee|out-file|set-content|add-content|sc|ac|sed\s+-i)(?=\s)')
    foreach ($match in $outputs) {
        $rest = $text.Substring($match.Index + $match.Length)
        $segment = ($rest -split '[;|&]', 2)[0]
        if ($segment.Trim() -in @('$null', '/dev/null', 'nul')) { continue }
        $violation = Get-TextViolation $segment $false
        if ($violation) { return "This command writes to $violation." }
    }

    # File-changing commands: block when the command names a protected location at all.
    $changes = $text -match '(^|[\s;|&(])(rm|del|erase|rmdir|rd|mv|move|cp|copy|xcopy|robocopy|mkdir|md|ni|new-item|remove-item|move-item|copy-item|rename-item|ri|mi|cpi|expand-archive|tar|unzip|7z|ln|mklink)(\s|$)|\[(system\.)?io\.(file|directory)\]::(write|append|copy|move|delete|create)'
    if ($changes) {
        $violation = Get-TextViolation $text $false
        if ($violation) { return "This command changes files and names $violation." }
    }
    return $null
}

$tool = [string]$request.tool_name
$toolInput = $request.tool_input
$reason = $null

if ($tool -in @('Write', 'Edit', 'MultiEdit', 'NotebookEdit')) {
    $path = if ($toolInput.file_path) { [string]$toolInput.file_path } else { [string]$toolInput.notebook_path }
    if ($path) {
        $violation = Get-TextViolation (ConvertTo-GuardText $path) $true
        if ($violation) { $reason = "Writing '$path' is blocked because it touches $violation." }
    }
}
elseif ($tool -in @('Bash', 'PowerShell')) {
    $reason = Get-CommandViolation ([string]$toolInput.command)
}

if ($reason) {
    [Console]::Error.WriteLine("SDVKit lab guard: $reason SDVKit stages mods into its own isolated profile below .sdvkit/, so the game folder, its Mods folder and the normal Saves folder never need changes. Use the sdvkit commands instead (see SDVKIT.md).")
    exit 2
}

exit 0

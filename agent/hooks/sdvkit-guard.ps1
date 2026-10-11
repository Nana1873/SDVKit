# SDVKit lab guard: a Claude Code PreToolUse hook written by `sdvkit lab init`.
# It blocks file writes and write-like shell commands that target the game folder (including its
# Mods folder), the normal Stardew Saves folder, *.xnb content, or game binaries. It covers Claude
# Code's own Write, Edit, NotebookEdit, Bash and PowerShell tools. It is a mechanical backstop for
# the lab's safety rules, not a sandbox: an unusual command can still get past it.
# Exit code 2 blocks the tool call and shows the message to the agent.
$ErrorActionPreference = 'Stop'

try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8
    $request = [Console]::In.ReadToEnd().TrimStart([char]0xFEFF) | ConvertFrom-Json
    $config = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'sdvkit-guard.json') | ConvertFrom-Json
}
catch {
    # A guard that cannot read its input or configuration must not break the session.
    exit 0
}

function ConvertTo-GuardText([string] $Text) {
    # Bash escapes spaces as '\ ' and spells E:\x as /e/x; compare lower-case forward-slash text.
    $value = $Text.ToLowerInvariant().Replace('\ ', ' ').Replace('\', '/')
    return [regex]::Replace($value, '(^|[\s"''=(])/([a-z])/', '$1$2:/')
}

function ConvertTo-GuardPath([string] $Path) {
    try { $Path = [IO.Path]::GetFullPath($Path) } catch { }
    return ConvertTo-GuardText $Path
}

$roots = @($config.protectedRoots | Where-Object { $_ } | ForEach-Object { (ConvertTo-GuardPath $_).TrimEnd('/') + '/' })
$binaries = @('stardew valley.dll', 'stardew valley.exe', 'stardewmoddingapi.dll', 'stardewmoddingapi.exe')

function Get-PathViolation([string] $Normal) {
    foreach ($root in $roots) {
        if ($Normal.StartsWith($root) -or ($Normal + '/') -eq $root) {
            return "it is inside the protected folder '$root'"
        }
    }
    if ($Normal.Contains('stardewvalley/saves/') -and -not $Normal.Contains('/.sdvkit/')) {
        return 'it is inside a normal Stardew Saves folder'
    }
    if ($Normal.EndsWith('.xnb')) { return 'it is game content (*.xnb)' }
    $name = $Normal.Substring($Normal.LastIndexOf('/') + 1)
    if ($binaries -contains $name) { return "it is a game binary ($name)" }
    return $null
}

function Get-CommandViolation([string] $Command) {
    $text = ConvertTo-GuardText $Command
    # Output targets: redirection, tee, Out-File, Set-Content, Add-Content. Only the target counts.
    $targets = [regex]::Matches($text, '(?<![0-9&])>{1,2}\s*("[^"]*"|''[^'']*''|[^\s;|&)]+)|(?:^|[\s;|&(])(?:tee-object|tee|out-file|set-content|add-content|sc|ac)\s+(?:-(?:filepath|path|literalpath|encoding\s+\S+|append|force|nonewline)\s+)*("[^"]*"|''[^'']*''|[^\s;|&)]+)')
    foreach ($match in $targets) {
        $target = ($match.Groups[1].Value + $match.Groups[2].Value).Trim('"', "'")
        if ($target -and $target -notin @('$null', '/dev/null', 'nul')) {
            $violation = Get-PathViolation $target
            if ($violation) { return "This command writes to '$target', and $violation." }
        }
    }

    # File-changing commands: block when the command mentions a protected location at all.
    $changes = $text -match '(^|[\s;|&(])(rm|del|erase|rmdir|rd|mv|move|cp|copy|xcopy|robocopy|mkdir|md|ni|new-item|remove-item|move-item|copy-item|rename-item|ri|mi|cpi|expand-archive|tar|unzip|7z)(\s|$)|\[(system\.)?io\.(file|directory)\]::(write|append|copy|move|delete|create)'
    if (-not $changes) { return $null }
    foreach ($root in $roots) {
        if ($text.Contains($root.TrimEnd('/'))) { return "This command changes files and names the protected folder '$root'." }
    }
    if ($text.Contains('stardewvalley/saves') -and -not $text.Contains('/.sdvkit/')) {
        return 'This command changes files and names a normal Stardew Saves folder.'
    }
    if ($text -match '\.xnb\b') { return 'This command changes or copies game content (*.xnb).' }
    foreach ($binary in $binaries) {
        if ($text.Contains($binary)) { return "This command changes or copies a game binary ($binary)." }
    }
    return $null
}

$tool = [string]$request.tool_name
$toolInput = $request.tool_input
$reason = $null

if ($tool -in @('Write', 'Edit', 'MultiEdit', 'NotebookEdit')) {
    $path = if ($toolInput.file_path) { [string]$toolInput.file_path } else { [string]$toolInput.notebook_path }
    if ($path) {
        $violation = Get-PathViolation (ConvertTo-GuardPath $path)
        if ($violation) { $reason = "Writing '$path' is blocked because $violation." }
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

# SDVKit lab guard: a Claude Code PreToolUse hook written by `sdvkit lab init`.
# It blocks file writes and write-like shell commands that touch the game folder (including its
# Mods folder), the normal Stardew Saves folder, *.xnb content, or game binaries. It is a
# mechanical backstop for the lab's safety rules, not a sandbox: an unusual command can still
# get past it. Exit code 2 blocks the tool call and shows the message to the agent.
$ErrorActionPreference = 'Stop'

try {
    $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'sdvkit-guard.json') | ConvertFrom-Json
}
catch {
    # A guard that cannot read its input or configuration must not break the session.
    exit 0
}

function ConvertTo-GuardPath([string] $Text) {
    # Bash escapes spaces as '\ '; compare the plain path.
    $value = $Text.ToLowerInvariant().Replace('\ ', ' ').Replace('\', '/')
    # Git Bash spells E:\x as /e/x.
    return [regex]::Replace($value, '(^|[\s"''=(])/([a-z])/', '$1$2:/')
}

$roots = @($config.protectedRoots | Where-Object { $_ } | ForEach-Object { (ConvertTo-GuardPath $_).TrimEnd('/') + '/' })
$binaries = @('stardew valley.dll', 'stardew valley.exe', 'stardewmoddingapi.dll', 'stardewmoddingapi.exe')

function Get-Violation([string] $Path) {
    $normal = ConvertTo-GuardPath $Path
    foreach ($root in $roots) {
        if ($normal.StartsWith($root) -or ($normal + '/') -eq $root) {
            return "it is inside the protected folder '$root'"
        }
    }
    if ($normal.EndsWith('.xnb')) { return 'it is game content (*.xnb)' }
    $name = $normal.Substring($normal.LastIndexOf('/') + 1)
    if ($binaries -contains $name) { return "it is a game binary ($name)" }
    return $null
}

$tool = [string]$request.tool_name
$toolInput = $request.tool_input
$reason = $null

if ($tool -in @('Write', 'Edit', 'MultiEdit', 'NotebookEdit')) {
    $path = if ($toolInput.file_path) { [string]$toolInput.file_path } else { [string]$toolInput.notebook_path }
    if ($path) {
        $violation = Get-Violation $path
        if ($violation) { $reason = "Writing '$path' is blocked because $violation." }
    }
}
elseif ($tool -in @('Bash', 'PowerShell')) {
    $command = ConvertTo-GuardPath ([string]$toolInput.command)
    $writes = $command -match '(^|[\s;|&(])(rm|del|erase|rmdir|rd|mv|move|cp|copy|xcopy|robocopy|mkdir|md|ni|new-item|remove-item|move-item|copy-item|rename-item|set-content|add-content|out-file|tee|tee-object|sc|ac|ri|mi|cpi)(\s|$)|(?<![0-9])>(?!\s*(\$null|/dev/null|&))|expand-archive|tar\s'
    if ($writes) {
        foreach ($root in $roots) {
            if ($command.Contains($root.TrimEnd('/'))) {
                $reason = "This command would write inside the protected folder '$root'."
                break
            }
        }
        if (-not $reason -and $command -match '\.xnb\b') {
            $reason = 'This command would write or copy game content (*.xnb).'
        }
        if (-not $reason) {
            foreach ($binary in $binaries) {
                if ($command.Contains($binary)) { $reason = "This command would write or copy a game binary ($binary)."; break }
            }
        }
    }
}

if ($reason) {
    [Console]::Error.WriteLine("SDVKit lab guard: $reason SDVKit stages mods into its own isolated profile below .sdvkit/, so the game folder, its Mods folder and the normal Saves folder never need changes. Use the sdvkit commands instead (see SDVKIT.md).")
    exit 2
}

exit 0

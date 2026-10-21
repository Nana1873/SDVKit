[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$changelogPath = Join-Path $PSScriptRoot '..\CHANGELOG.md'
$lines = @(Get-Content -LiteralPath $changelogPath -Encoding utf8)
$heading = '^## \[' + [regex]::Escape($Version) + '\] - \d{4}-\d{2}-\d{2}$'
$sectionMatches = @($lines | Select-String -Pattern $heading)
if ($sectionMatches.Count -ne 1) {
    throw "Expected exactly one dated changelog section for $Version."
}

$start = $sectionMatches[0].LineNumber
$end = $start
while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## \[') {
    $end++
}

if ($end -eq $start) {
    throw "The changelog section for $Version is empty."
}

# '### Development' records changes that matter only to SDVKit's own development (tests, CI, acceptance-script
# internals); it stays in CHANGELOG.md but is left out of the release notes.
$section = [System.Collections.Generic.List[string]]::new()
$skipping = $false
foreach ($line in $lines[$start..($end - 1)]) {
    if ($line -match '^### ') { $skipping = $line -match '^### Development\s*$' }
    elseif ($line -match '^\[') { $skipping = $false }
    if (-not $skipping) { $section.Add($line) }
}

$notes = ($section -join "`n").Trim()
# A release body does not resolve repository-relative links, so point them at the tagged files.
$notes = [regex]::Replace($notes, '\]\((?!https?:|#|mailto:)([^)\s]+)\)', { param($match) "](https://github.com/Nana1873/SDVKit/blob/v$Version/$($match.Groups[1].Value))" })
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "The changelog section for $Version is empty."
}

Write-Output $notes

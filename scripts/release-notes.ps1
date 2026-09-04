[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$changelogPath = Join-Path $PSScriptRoot '..\CHANGELOG.md'
$lines = @(Get-Content -LiteralPath $changelogPath)
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

$notes = ($lines[$start..($end - 1)] -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "The changelog section for $Version is empty."
}

Write-Output $notes

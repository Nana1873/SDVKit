#Requires -Version 5.1
<#
.SYNOPSIS
Compiles the game-bound half of SdvKit.GameSide against a local Stardew Valley + SMAPI installation.

.DESCRIPTION
Hosted CI cannot reference the proprietary game assemblies, so it never compiles the
game-bound code, including the SdvKit.Shared project that SdvKit.GameSide references.
Run this script for every change to those files and before every release. All build output goes
below .sdvkit/gameside-build; nothing is copied into the game folder, a Mods directory, or the
shared src/*/bin outputs that a running lab may use.

Without -GamePath the script uses SDV_GAME_PATH, or else the single ready installation that
`sdvkit doctor` discovers. It prints the game and SMAPI versions so a pull request or release
can record what the half was compiled against.

.EXAMPLE
.\scripts\build-gameside.ps1
.\scripts\build-gameside.ps1 -GamePath 'D:\Games\Stardew Valley'
#>
[CmdletBinding()]
param(
    [string] $GamePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\SdvKit.GameSide\SdvKit.GameSide.csproj'
$artifacts = Join-Path $repoRoot '.sdvkit\gameside-build'

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = $env:SDV_GAME_PATH
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    # Run doctor from a private build so the shared CLI output, which a lab may be using, stays untouched.
    $cli = Join-Path $repoRoot 'src\SdvKit.Cli\SdvKit.Cli.csproj'
    $doctorJson = & dotnet run --project $cli -c Release --verbosity quiet `
        --artifacts-path (Join-Path $artifacts 'doctor') -- doctor --json
    $doctorExit = $LASTEXITCODE
    $doctor = $null
    try { $doctor = ($doctorJson -join [Environment]::NewLine) | ConvertFrom-Json } catch { }
    $installations = @(if ($doctor) { $doctor.installations })
    if ($doctorExit -ne 0 -or $installations.Count -ne 1) {
        $status = if ($doctor) { $doctor.status } else { 'unreadable' }
        throw "sdvkit doctor reported '$status' with $($installations.Count) installation(s); pass -GamePath or set SDV_GAME_PATH."
    }
    $GamePath = [string]$installations[0].gamePath
}

# A trailing backslash would escape the closing quote of the -p: argument in Windows PowerShell 5.1.
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path.TrimEnd('\', '/')
$gameDll = Join-Path $GamePath 'Stardew Valley.dll'
$smapiDll = Join-Path $GamePath 'StardewModdingAPI.dll'
foreach ($required in @($gameDll, $smapiDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Not a Stardew Valley + SMAPI installation: '$required' is missing."
    }
}

function Get-FileVersion([string] $Path) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    return "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
}

$gameVersion = Get-FileVersion $gameDll
$smapiVersion = Get-FileVersion $smapiDll

& dotnet build $project -c Release "-p:SdvGamePath=$GamePath" --artifacts-path (Join-Path $artifacts 'gameside')
if ($LASTEXITCODE -ne 0) {
    throw "The game-bound GameSide build failed against Stardew Valley $gameVersion / SMAPI $smapiVersion."
}

Write-Output "GameSide compiled against Stardew Valley $gameVersion / SMAPI $smapiVersion."

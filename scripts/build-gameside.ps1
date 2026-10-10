#Requires -Version 5.1
<#
.SYNOPSIS
Compiles the game-bound half of SdvKit.GameSide against a local Stardew Valley + SMAPI installation.

.DESCRIPTION
Hosted CI cannot reference the proprietary game assemblies, so it never compiles the
SDVKIT_GAME_AVAILABLE code. Run this script for every change to src/SdvKit.GameSide and
before every release. Build output goes below .sdvkit/gameside-build; nothing is copied into
the game folder or a Mods directory.

Without -GamePath the script uses SDV_GAME_PATH, or else the single installation that
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

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = $env:SDV_GAME_PATH
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $cli = Join-Path $repoRoot 'src\SdvKit.Cli\SdvKit.Cli.csproj'
    $doctorJson = & dotnet run --project $cli -c Release --verbosity quiet -- doctor --json
    if ($LASTEXITCODE -ne 0) {
        throw 'sdvkit doctor failed; pass -GamePath or set SDV_GAME_PATH.'
    }
    $doctor = ($doctorJson -join [Environment]::NewLine) | ConvertFrom-Json
    $installations = @($doctor.installations)
    if ($installations.Count -ne 1) {
        throw "sdvkit doctor found $($installations.Count) installations; pass -GamePath or set SDV_GAME_PATH."
    }
    $GamePath = [string]$installations[0].gamePath
}

$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
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
$artifacts = Join-Path $repoRoot '.sdvkit\gameside-build'

& dotnet build $project -c Release "-p:SdvGamePath=$GamePath" --artifacts-path $artifacts
if ($LASTEXITCODE -ne 0) {
    throw "The game-bound GameSide build failed against Stardew Valley $gameVersion / SMAPI $smapiVersion."
}

Write-Output "GameSide compiled against Stardew Valley $gameVersion / SMAPI $smapiVersion."

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Sdvkit,
    [Parameter(Mandatory = $true)][string] $GamePath,
    [string] $RecipeRoot = (Join-Path $PWD '.sdvkit\data-migration-recipe')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$recipe = [IO.Path]::GetFullPath($RecipeRoot)
$labOutput = [IO.Path]::GetFullPath((Join-Path $PWD '.sdvkit')) + [IO.Path]::DirectorySeparatorChar
if (-not $recipe.StartsWith($labOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "RecipeRoot must be below the current lab root's ignored .sdvkit directory."
}
if (Test-Path -LiteralPath $recipe) { throw 'Choose a fresh RecipeRoot; existing recipe artifacts are never overwritten.' }
$ancestor = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($recipe))
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Recipe output must not traverse a symbolic link or junction.'
    }
    $ancestor = $ancestor.Parent
}
New-Item -ItemType Directory -Path $recipe | Out-Null
$sourceRoot = Join-Path $PSScriptRoot '..\docs\recipes\data-migration'
$artifacts = @()
foreach ($variant in @(
    @{ Name = 'v1'; Version = '1.0.0'; Constants = 'RECIPE_V1' },
    @{ Name = 'v2-broken'; Version = '2.0.0-beta.1'; Constants = 'RECIPE_BROKEN' },
    @{ Name = 'v2'; Version = '2.0.0'; Constants = '' }
)) {
    $source = Join-Path $recipe ($variant.Name + '-source')
    New-Item -ItemType Directory -Path $source | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Ledger.cs'), (Join-Path $sourceRoot 'ModEntry.cs') -Destination $source
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <Version>$($variant.Version)</Version>
    <AssemblyName>DataMigrationRecipe</AssemblyName>
    <Nullable>enable</Nullable>
    <EnableModDeploy>false</EnableModDeploy>
    <EnableModZip>false</EnableModZip>
    <DefineConstants>$($variant.Constants)</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $source 'DataMigrationRecipe.csproj') -Encoding utf8
    [ordered]@{
        Name = 'Data Migration Recipe'; Author = 'SDVKit'; Version = $variant.Version
        Description = 'One original owned-save ledger migration from v1 to v2.'
        UniqueID = 'SDVKit.DataMigrationRecipe'; EntryDll = 'DataMigrationRecipe.dll'; MinimumApiVersion = '4.5.2'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $source 'manifest.json') -Encoding utf8
    foreach ($operation in @('check', 'package')) {
        $arguments = @('project', $operation, $source)
        if ($operation -eq 'package') { $arguments += @('--project', 'DataMigrationRecipe.csproj', '--game-path', $GamePath) }
        $arguments += '--json'
        $output = & $Sdvkit @arguments
        $exitCode = $LASTEXITCODE
        $output | Set-Content -LiteralPath (Join-Path $recipe ($variant.Name + '-' + $operation + '.json')) -Encoding utf8
        if ($exitCode -ne 0) { throw "$operation failed for $($variant.Name): $output" }
    }
    $report = $output | ConvertFrom-Json
    $archive = Join-Path $source $report.archive
    $frozen = Join-Path $recipe ("DataMigrationRecipe-$($variant.Version).zip")
    Copy-Item -LiteralPath $archive -Destination $frozen
    $extract = Join-Path $recipe ($variant.Name + '-extracted')
    Expand-Archive -LiteralPath $frozen -DestinationPath $extract
    $target = Join-Path $extract 'DataMigrationRecipe'
    if (-not (Test-Path -LiteralPath (Join-Path $target 'DataMigrationRecipe.dll'))) { throw 'The extracted ready mod is missing its DLL.' }
    $artifacts += [ordered]@{
        variant = $variant.Name; version = $variant.Version; target = $target; archive = $frozen
        archiveSha256 = (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant()
        dllSha256 = (Get-FileHash -LiteralPath (Join-Path $target 'DataMigrationRecipe.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
if (@($artifacts.dllSha256 | Select-Object -Unique).Count -ne 3) { throw 'Expected three distinct compiled artifact versions.' }
$artifacts | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $recipe 'artifacts.json') -Encoding utf8
$artifacts | ConvertTo-Json -Depth 5

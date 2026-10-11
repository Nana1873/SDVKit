$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$cliProjectPath = Join-Path $repositoryRoot "src\SdvKit.Cli\SdvKit.Cli.csproj"
$artifactRoot = Join-Path $repositoryRoot ".sdvkit\distribution"
$workRoot = Join-Path $artifactRoot "work"
$publishRoot = Join-Path $workRoot "publish"
$buildArtifactRoot = Join-Path $workRoot "artifacts"

[xml]$cliProject = Get-Content -LiteralPath $cliProjectPath -Raw
$version = [string](@($cliProject.Project.PropertyGroup.Version) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "The CLI project does not declare a package-safe semantic version."
}

$gameSideProjectPath = Join-Path $repositoryRoot "src\SdvKit.GameSide\SdvKit.GameSide.csproj"
[xml]$gameSideProject = Get-Content -LiteralPath $gameSideProjectPath -Raw
$gameSideVersion = [string](@($gameSideProject.Project.PropertyGroup.Version) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1)
$gameSideManifestPath = Join-Path $repositoryRoot "src\SdvKit.GameSide\manifest.json"
$gameSideManifestVersion = [string](
    (Get-Content -LiteralPath $gameSideManifestPath -Raw | ConvertFrom-Json).Version)
if ($gameSideVersion -ne $version -or $gameSideManifestVersion -ne $version) {
    throw "The CLI, GameSide project, and GameSide manifest versions must match."
}

$packageName = "SDVKit-$version-win-x64"
$packageRoot = Join-Path $workRoot $packageName
$archivePath = Join-Path $artifactRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
& dotnet publish $cliProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $publishRoot `
    --artifacts-path $buildArtifactRoot `
    --property:DebugSymbols=false `
    --property:DebugType=None
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$runtimeFiles = @(
    "sdvkit.exe",
    "sdvkit.dll",
    "SdvKit.Shared.dll",
    "sdvkit.deps.json",
    "sdvkit.runtimeconfig.json",
    "Humanizer.dll",
    "Json.More.dll",
    "JsonPointer.Net.dll",
    "JsonSchema.Net.dll",
    "Microsoft.Extensions.AI.Abstractions.dll",
    "Microsoft.Extensions.Caching.Abstractions.dll",
    "Microsoft.Extensions.Configuration.Abstractions.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
    "Microsoft.Extensions.Diagnostics.Abstractions.dll",
    "Microsoft.Extensions.FileProviders.Abstractions.dll",
    "Microsoft.Extensions.Hosting.Abstractions.dll",
    "Microsoft.Extensions.Logging.Abstractions.dll",
    "Microsoft.Extensions.Options.dll",
    "Microsoft.Extensions.Primitives.dll",
    "ModelContextProtocol.AspNetCore.dll",
    "ModelContextProtocol.Core.dll",
    "ModelContextProtocol.dll",
    "System.Diagnostics.DiagnosticSource.dll",
    "System.IO.Pipelines.dll",
    "System.Net.ServerSentEvents.dll",
    "System.Text.Encodings.Web.dll",
    "System.Text.Json.dll"
)
foreach ($fileName in $runtimeFiles) {
    $sourcePath = Join-Path $publishRoot $fileName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "The framework-dependent publish did not produce $fileName."
    }

    Copy-Item -LiteralPath $sourcePath -Destination $packageRoot
}

Copy-Item -LiteralPath (Join-Path $publishRoot "schemas") -Destination $packageRoot -Recurse

foreach ($fileName in @(
    "Directory.Build.props",
    "global.json",
    "NuGet.config",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md")) {
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot $fileName) `
        -Destination (Join-Path $packageRoot $fileName)
}

$gameSideSource = Join-Path $repositoryRoot "src\SdvKit.GameSide"
Copy-Item `
    -LiteralPath (Join-Path $repositoryRoot "docs\portable-quickstart.txt") `
    -Destination (Join-Path $packageRoot "README.txt")

$gameSideDestination = Join-Path $packageRoot "src\SdvKit.GameSide"
New-Item -ItemType Directory -Path $gameSideDestination -Force | Out-Null
$gameSideFiles = @(
    Get-ChildItem -LiteralPath $gameSideSource -File -Filter "*.cs"
) + @(
    Get-Item -LiteralPath $gameSideProjectPath, $gameSideManifestPath
)
foreach ($file in $gameSideFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $gameSideDestination
}

$sharedSource = Join-Path $repositoryRoot "src\SdvKit.Shared"
$sharedDestination = Join-Path $packageRoot "src\SdvKit.Shared"
foreach ($file in Get-ChildItem -LiteralPath $sharedSource -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($sharedSource, $file.FullName)
    if ($relative -match '^(bin|obj)[\\/]') { continue }
    if ($file.Extension -notin @('.cs', '.csproj')) { continue }
    $destination = Join-Path $sharedDestination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}

$forbiddenExtensions = @(".pdb", ".xnb")
$localPaths = @($repositoryRoot, $repositoryRoot.Replace('\', '/'))
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
    if ($forbiddenExtensions -contains $file.Extension.ToLowerInvariant()) {
        throw "The portable package contains a forbidden file: $($file.FullName)"
    }

    $contents = [System.IO.File]::ReadAllBytes($file.FullName)
    $utf8 = [System.Text.Encoding]::UTF8.GetString($contents)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($contents)
    foreach ($localPath in $localPaths) {
        if ($utf8.Contains($localPath, [System.StringComparison]::OrdinalIgnoreCase) `
            -or $utf16.Contains($localPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The portable package contains the local checkout path in $($file.FullName)."
        }
    }
}

Compress-Archive `
    -LiteralPath $packageRoot `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content `
    -LiteralPath $checksumPath `
    -Value "$hash  $([System.IO.Path]::GetFileName($archivePath))" `
    -Encoding ascii

Write-Output $archivePath
Write-Output $checksumPath

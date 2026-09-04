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

$alwaysOnProjectPath = Join-Path $repositoryRoot "src\SdvKit.AlwaysOn\SdvKit.AlwaysOn.csproj"
[xml]$alwaysOnProject = Get-Content -LiteralPath $alwaysOnProjectPath -Raw
$alwaysOnVersion = [string](@($alwaysOnProject.Project.PropertyGroup.Version) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1)
$alwaysOnManifestPath = Join-Path $repositoryRoot "src\SdvKit.AlwaysOn\manifest.json"
$alwaysOnManifestVersion = [string](
    (Get-Content -LiteralPath $alwaysOnManifestPath -Raw | ConvertFrom-Json).Version)
if ($alwaysOnVersion -ne $version -or $alwaysOnManifestVersion -ne $version) {
    throw "The CLI, AlwaysOn project, and AlwaysOn manifest versions must match."
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
    "sdvkit.deps.json",
    "sdvkit.runtimeconfig.json",
    "Microsoft.Extensions.AI.Abstractions.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
    "Microsoft.Extensions.Logging.Abstractions.dll",
    "ModelContextProtocol.Core.dll",
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

$alwaysOnSource = Join-Path $repositoryRoot "src\SdvKit.AlwaysOn"
$alwaysOnDestination = Join-Path $packageRoot "src\SdvKit.AlwaysOn"
New-Item -ItemType Directory -Path $alwaysOnDestination -Force | Out-Null
$alwaysOnFiles = @(
    Get-ChildItem -LiteralPath $alwaysOnSource -File -Filter "*.cs"
) + @(
    Get-Item -LiteralPath $alwaysOnProjectPath, $alwaysOnManifestPath
)
foreach ($file in $alwaysOnFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $alwaysOnDestination
}

$linkedSource = Join-Path $repositoryRoot "src\SdvKit.Cli\LiveLab"
$linkedDestination = Join-Path $packageRoot "src\SdvKit.Cli\LiveLab"
New-Item -ItemType Directory -Path $linkedDestination -Force | Out-Null
$linkedFiles = @(
    "LoadedModsModels.cs",
    "ModBuildIdentity.cs",
    "NetworkTwoModels.cs",
    "ProjectModModels.cs",
    "ReviewAudioModels.cs",
    "ReviewInputModels.cs",
    "ReviewDataModels.cs",
    "ReviewMapModels.cs",
    "ReviewModAssetModels.cs",
    "ReviewScreenshotModels.cs",
    "ReviewTextureModels.cs",
    "ReviewTexturePngValidator.cs",
    "ReviewTransportModels.cs",
    "RuntimeVersionCompatibility.cs",
    "RuntimeSnapshotModels.cs",
    "TestSaveModels.cs"
)
foreach ($fileName in $linkedFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $linkedSource $fileName) `
        -Destination (Join-Path $linkedDestination $fileName)
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

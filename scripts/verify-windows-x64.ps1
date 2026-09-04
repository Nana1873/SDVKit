[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,

    [string] $ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [ValidateSet("ready", "notFound")]
    [string] $ExpectedDoctorStatus,

    [string] $ExtractRoot = (Join-Path ([System.IO.Path]::GetTempPath()) "sdvkit-portable-$([Guid]::NewGuid().ToString('N'))")
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$verificationTimer = [System.Diagnostics.Stopwatch]::StartNew()

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$archive = Get-Item -LiteralPath $ArchivePath
if ($archive.PSIsContainer -or $archive.Name -notmatch '^SDVKit-(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)-win-x64\.zip$') {
    throw "Expected a versioned SDVKit Windows-x64 ZIP."
}

$archiveVersion = $Matches[1]
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = $archiveVersion
}
if ($archiveVersion -cne $ExpectedVersion) {
    throw "The archive version does not match the expected version: $ExpectedVersion"
}

$checksumPath = "$($archive.FullName).sha256"
$checksumParts = (Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+', 2
$actualHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
if ($checksumParts.Count -ne 2 `
    -or $checksumParts[0] -ne $actualHash `
    -or $checksumParts[1] -cne $archive.Name) {
    throw "The portable archive checksum is invalid."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
try {
    $allowedDlls = @(
        'Humanizer.dll',
        'Json.More.dll',
        'JsonPointer.Net.dll',
        'JsonSchema.Net.dll',
        'sdvkit.dll',
        'Microsoft.Extensions.AI.Abstractions.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll',
        'ModelContextProtocol.Core.dll',
        'System.Diagnostics.DiagnosticSource.dll',
        'System.IO.Pipelines.dll',
        'System.Net.ServerSentEvents.dll',
        'System.Text.Encodings.Web.dll',
        'System.Text.Json.dll'
    )
    foreach ($entry in $zip.Entries) {
        $entryPath = $entry.FullName.Replace('\', '/')
        $segments = @($entryPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries))
        if ($entryPath.StartsWith('/') `
            -or $entryPath -match '^[A-Za-z]:' `
            -or $segments -contains '..' `
            -or $segments -contains '.sdvkit' `
            -or $segments -contains '.git' `
            -or $segments -contains 'bin' `
            -or $segments -contains 'obj' `
            -or $segments -contains 'Saves' `
            -or -not $entryPath.StartsWith("$($archive.BaseName)/")) {
            throw "The portable archive contains an invalid entry: $entryPath"
        }

        $extension = [System.IO.Path]::GetExtension($entryPath).ToLowerInvariant()
        $fileName = [System.IO.Path]::GetFileName($entryPath)
        if ($extension -in @('.pdb', '.xnb') `
            -or ($extension -eq '.dll' -and $fileName -notin $allowedDlls) `
            -or ($extension -eq '.exe' -and $fileName -ne 'sdvkit.exe')) {
            throw "The portable archive contains an unexpected binary: $entryPath"
        }
    }
}
finally {
    $zip.Dispose()
}

$ExtractRoot = [System.IO.Path]::GetFullPath($ExtractRoot, (Get-Location).ProviderPath)
if (Test-Path -LiteralPath $ExtractRoot) {
    throw "The portable smoke destination is not fresh: $ExtractRoot"
}

$relativeToWorkspace = [System.IO.Path]::GetRelativePath($repositoryRoot, $ExtractRoot)
if (-not [System.IO.Path]::IsPathRooted($relativeToWorkspace) `
    -and $relativeToWorkspace -ne '..' `
    -and -not $relativeToWorkspace.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)")) {
    throw "The portable archive must be tested outside the repository tree."
}

Expand-Archive -LiteralPath $archive.FullName -DestinationPath $ExtractRoot
$packageRoot = Join-Path $ExtractRoot $archive.BaseName
$cli = Join-Path $packageRoot "sdvkit.exe"
Push-Location -LiteralPath $packageRoot
try {
    $versionOutput = (& $cli --version | Out-String).Trim()
    $versionExitCode = $LASTEXITCODE
    if ($versionExitCode -ne 0 -or $versionOutput -cne "SDVKit $ExpectedVersion") {
        throw "The extracted CLI version check failed: $versionOutput"
    }

    $doctorOutput = (& $cli doctor --json | Out-String).Trim()
    $doctorExitCode = $LASTEXITCODE
    $doctor = $doctorOutput | ConvertFrom-Json
    $expectedDoctorExitCode = if ($ExpectedDoctorStatus -eq "ready") { 0 } else { 3 }
    $expectedInstallationCount = if ($ExpectedDoctorStatus -eq "ready") { 1 } else { 0 }
    if ($doctorExitCode -ne $expectedDoctorExitCode `
        -or $doctor.schemaVersion -ne 1 `
        -or $doctor.status -cne $ExpectedDoctorStatus `
        -or @($doctor.installations).Count -ne $expectedInstallationCount) {
        throw "The extracted CLI returned unexpected doctor diagnostics: $doctorOutput"
    }

    $mcpErrorPath = Join-Path $ExtractRoot "mcp.stderr.log"
    $mcpOutput = (& $cli project review mcp serve 2> $mcpErrorPath | Out-String).Trim()
    $mcpExitCode = $LASTEXITCODE
    $mcpError = (Get-Content -LiteralPath $mcpErrorPath -Raw).Trim()
    if ($mcpExitCode -ne 3 `
        -or $mcpOutput.Length -ne 0 `
        -or $mcpError -notmatch '^SDVKit MCP startup failed \[[A-Za-z0-9]+\]: ') {
        throw "The extracted MCP entry point did not fail cleanly without an active review."
    }

    $fixtureRoot = Join-Path $packageRoot ".sdvkit\inspect-fixture"
    $createOutput = (& $cli project create smapi-mod $fixtureRoot `
        --name "Portable Inspect Fixture" `
        --author "SDVKit" `
        --unique-id "SDVKit.PortableInspectFixture" `
        --description "CI fixture for the extracted portable archive." `
        --json | Out-String).Trim()
    $createExitCode = $LASTEXITCODE
    if ($createExitCode -ne 0) {
        throw "The extracted CLI could not create the inspect fixture: $createOutput"
    }

    $inspectOutput = (& $cli project inspect $fixtureRoot --json | Out-String).Trim()
    $inspectExitCode = $LASTEXITCODE
    $inspection = $inspectOutput | ConvertFrom-Json
    if ($inspectExitCode -ne 0 `
        -or $inspection.schemaVersion -ne 1 `
        -or $inspection.kind -ne "smapiMod" `
        -or @($inspection.projectFiles).Count -ne 1 `
        -or @($inspection.manifests).Count -ne 1 `
        -or @($inspection.problems).Count -ne 0) {
        throw "The extracted CLI returned an unexpected project inspection: $inspectOutput"
    }

    $checkOutput = (& $cli project check $fixtureRoot --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($checkOutput | ConvertFrom-Json).status -ne 'passed') {
        throw "The extracted C# manifest check failed: $checkOutput"
    }

    $packRoot = Join-Path $packageRoot '.sdvkit\check-pack'
    $createPackOutput = (& $cli project create content-pack $packRoot `
        --name 'Portable Check Pack' --author SDVKit --unique-id SDVKit.PortableCheckPack `
        --description 'Offline schema resource verification.' --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "The extracted CP creation failed: $createPackOutput" }
    $i18nRoot = Join-Path $packRoot 'i18n'
    New-Item -ItemType Directory -Path $i18nRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $i18nRoot 'default.json') -Value '{ /* comment */ "hello": "Hello {{name}}!", }' -Encoding utf8
    $checkOutput = (& $cli project check $packRoot --json | Out-String).Trim()
    $checkExit = $LASTEXITCODE
    $check = $checkOutput | ConvertFrom-Json
    if ($checkExit -ne 0 -or $check.status -ne 'passed' -or @($check.files).Count -ne 3 `
        -or $check.schemaSource -ne '79f9bbbe3edbb7ca3369e7ad0d3dd45131b34fc0') {
        throw "The extracted CP/i18n schema check failed: $checkOutput"
    }

    Set-Content -LiteralPath (Join-Path $i18nRoot 'default.json') -Value '{"hello": 42}' -Encoding utf8
    $invalidOutput = (& $cli project check $packRoot --json | Out-String).Trim()
    $invalidExit = $LASTEXITCODE
    $invalid = $invalidOutput | ConvertFrom-Json
    if ($invalidExit -ne 3 -or -not @($invalid.problems | Where-Object {
        $_.file -eq 'i18n/default.json' -and $_.field -eq '/hello' -and $_.code -eq 'schemaViolation'
    }).Count) {
        throw "The extracted checker missed the invalid translation: $invalidOutput"
    }
    Set-Content -LiteralPath (Join-Path $i18nRoot 'default.json') -Value '{"hello": "Hello!"}' -Encoding utf8

    $packageOutput = (& $cli project package $packRoot --json | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "The extracted content-pack package failed: $packageOutput" }
}
finally {
    Pop-Location
}

Write-Output "Portable archive verified: $($archive.Name) (SHA-256 $($actualHash.ToLowerInvariant()))"
Write-Output "Extracted package: $packageRoot"
$verificationTimer.Stop()
Write-Output ("Offline verification elapsed: {0:n1}s" -f $verificationTimer.Elapsed.TotalSeconds)

# Author one GMCM number slider and verify its saved effect

Build **GMCM Arrival Row**, an original C# mod whose integer slider selects the
Farm row used by a start-of-day warp. The option has a default of **15**, a
minimum of **15**, a maximum of **19**, an interval of **2**, and the stable
GMCM `fieldId` **ArrivalRow**. Edit the native slider through the owned
process-local cursor, distinguish its unsaved value from the mod's current
value, save and explicitly reconcile the changed config, then restart with the
retained export and prove the same packaged DLL uses row 19.

This is the acceptance procedure for
[#218](https://github.com/Nana1873/SDVKit/issues/218). It extends the
[Boolean GMCM recipe](gmcm-authoring.md) with one bounded integer control; it
does not add a general GMCM editor or change SDVKit's partial custom-menu
inspection coverage.

## Contract and references

| Concern | Bounded contract |
| --- | --- |
| SDVKit | Use a package built from merged [PR #226](https://github.com/Nana1873/SDVKit/pull/226), commit [`3d255b6`](https://github.com/Nana1873/SDVKit/commit/3d255b6d3e2ca1eea32d96019977918de3aa1604), or a verified descendant. Record the exact package hash and source commit. |
| Provider | Explicitly selected `spacechase0.GenericModConfigMenu` **1.16.0**, copied below this lab and initialized with that version's canonical default config bytes before review, with recorded manifest/DLL/config hashes. Other versions do not register this recipe's menu integration. |
| Environment | Single review, Stardew Valley **1.6.15**, SMAPI **4.5.2**, owned disposable standard-farm fixture. No multiplayer acceptance. |
| Option | Original mod `ExampleAuthor.GmcmArrivalRow`; integer `ArrivalRow`; domain default/minimum **15**, maximum **19**, interval **2**, values **15/17/19**, `fieldId` **ArrivalRow**. The native GMCM control uses step indexes **0/1/2** with interval **1** and formats/maps them to those rows. |
| Registration | `Register` during `GameLaunched`, `titleScreenOnly: false`; the verified integer `AddNumberOption` overload supplies explicit step-index `min`, `max`, `interval`, formatter, conversion adapter and field identity. |
| Effect | `DayStarted` warps the local farmer to Farm tile **64,ArrivalRow** in a ready single-player world. Default and saved acceptance positions are 64,15 and 64,19. Saving does not warp again in the current day. |
| Save | The real GMCM **Save** action commits the cached integer through the setter and invokes `Helper.WriteConfig`. Slider movement alone changes neither the in-memory config nor `config.json`. |
| Reconcile | The packaged default `config.json` participates in the original staging identity. Its canonical SMAPI serialization is established before packaging, not adopted through an early reconciliation. After the user's real Save, the next owned inspection/input request rejects drift; explicit `config-reconcile` accepts only this selected root config and retains its export without restarting. |
| Restart | Stop removes staging. A fresh extraction of the unchanged ZIP receives only the reconciled config export, then a new process loads row 19 from the same DLL. |
| Unsupported | Float sliders, text/custom controls, other provider versions, foreign configs, automatic dependency installation, implicit config retention and multiplayer are outside this recipe. |

SDVKit v0.10.2 includes the native raw-mouse adapter. The older v0.10.1 archive predates it.
Its version string alone is therefore insufficient for this workflow. The exact
CLI package must contain #226's process-local XNA mouse publication, neutral
click/drag preparation and held drag endpoint behavior; verify that through its
recorded commit/artifact identity instead of assuming support from `0.10.1`.

Use the [official GMCM download](https://www.nexusmods.com/stardewvalley/mods/5098?tab=files)
to obtain the selected provider. Keep an explicitly selected independent copy
below the lab's `.sdvkit/`; never discover or stage it by scanning normal Mods.
The provider is a required acceptance companion but remains an optional mod
dependency so the example can report an unavailable integration cleanly.

The integer API must be checked against the selected local 1.16.0 provider's
public `GenericModConfigMenu.IGenericModConfigMenuApi`. The bounded reflection
step appears below, after `$gamePath` and `$provider` have both been established
and their identities recorded.

Use the [SMAPI config guide](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Config),
the [SMAPI authoring recipe](smapi-authoring.md), [menu inspection](menu-inspection.md),
[owned input](mcp.md#opt-in-input), and the
[configuration reconciliation contract](live-review.md#accept-an-intentional-configuration-change).

## Prepare the original project and provider

Use PowerShell 7 and set `$sdvkit` to the exact installed or extracted CLI under
test. Keep the current directory at one selected lab root for every live command.
Resolve `$gamePath` from `doctor`, select it explicitly, and retain SDVKit's
commit/archive identity. Set `$selectedGmcm` only to a project-owned provider
copy; do not scan normal Mods.

```powershell
$lab = $PWD.Path
$mod = Join-Path $lab '.sdvkit/GmcmArrivalRow'
$providerDestination = Join-Path $lab '.sdvkit/GenericModConfigMenu'
$evidence = Join-Path $lab '.sdvkit/gmcm-number-evidence'
foreach ($fresh in @($mod, $providerDestination, $evidence)) {
    if (Test-Path -LiteralPath $fresh) { throw "Choose a fresh path: $fresh" }
}
New-Item -ItemType Directory -Path $evidence | Out-Null
$selectedProviderRoot = (Resolve-Path -LiteralPath $selectedGmcm).Path
$selectedProviderConfig = Join-Path $selectedProviderRoot 'config.json'
[ordered]@{
    sourceRoot = $selectedProviderRoot
    manifestSha256 = (Get-FileHash -LiteralPath (
        Join-Path $selectedProviderRoot 'manifest.json') -Algorithm SHA256).Hash
    dllSha256 = (Get-FileHash -LiteralPath (
        Join-Path $selectedProviderRoot 'GenericModConfigMenu.dll') -Algorithm SHA256).Hash
    configPresent = Test-Path -LiteralPath $selectedProviderConfig -PathType Leaf
    configSha256 = if (Test-Path -LiteralPath $selectedProviderConfig -PathType Leaf) {
        (Get-FileHash -LiteralPath $selectedProviderConfig -Algorithm SHA256).Hash
    } else { $null }
} | ConvertTo-Json | Set-Content (Join-Path $evidence 'selected-provider-source.json')
New-Item -ItemType Directory -Force -Path (Split-Path $providerDestination) | Out-Null
Copy-Item -LiteralPath $selectedProviderRoot -Destination $providerDestination -Recurse
$provider = (Resolve-Path -LiteralPath $providerDestination).Path

$providerManifest = Get-Content -LiteralPath (Join-Path $provider 'manifest.json') -Raw |
    ConvertFrom-Json
if ($providerManifest.UniqueID -cne 'spacechase0.GenericModConfigMenu' -or
    $providerManifest.Version -cne '1.16.0') {
    throw 'Select GMCM 1.16.0.'
}
Get-FileHash -LiteralPath @(
    (Join-Path $provider 'manifest.json'),
    (Join-Path $provider 'GenericModConfigMenu.dll')) -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'provider-hashes.json')

& $sdvkit doctor --game-path $gamePath --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
& $sdvkit project create smapi-mod $mod `
    --name 'GMCM Arrival Row' `
    --author ExampleAuthor `
    --unique-id ExampleAuthor.GmcmArrivalRow `
    --description 'Choose the start-of-day Farm arrival row through GMCM.' `
    --json
& $sdvkit project inspect $mod --json
$project = 'GmcmArrivalRow.csproj'
```

Now inspect only the selected binaries. This does not discover or download a
provider:

```powershell
foreach ($assembly in @(
    'smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll',
    'MonoGame.Framework.dll',
    'Stardew Valley.dll',
    'StardewModdingAPI.dll'
)) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $gamePath $assembly))
}
$providerAssembly = [Reflection.Assembly]::LoadFrom(
    (Join-Path $provider 'GenericModConfigMenu.dll'))
$numberMethods = $providerAssembly.GetType(
    'GenericModConfigMenu.IGenericModConfigMenuApi',
    $true).GetMethods() | Where-Object Name -ceq 'AddNumberOption'
$integerMethod = @($numberMethods | Where-Object {
    $_.GetParameters()[1].ParameterType.ToString() -ceq 'System.Func`1[System.Int32]'
})
if ($integerMethod.Count -ne 1) { throw 'The expected integer GMCM API is unavailable.' }
$integerMethod[0].GetParameters() | Select-Object Position, Name, ParameterType, IsOptional
```

Require one overload with `IManifest`, `Func<int>`, `Action<int>`, name and
tooltip delegates, nullable integer `min`, `max`, and `interval`, optional
`Func<int,string>` formatting, and optional string `fieldId`. Reflection is
feasibility evidence for this selected binary, not permission to call private
provider implementation or treat its widgets as a stable inspection API.

Require both review topologies stopped before preparing a fixture or claiming
the live slot. Identify its current owner and wait for a verified handoff. Record
protected Saves, normal/mod-manager-owned Mods, and preferences through the
[existing fingerprint procedure](cp-authoring.md#prerequisites-and-one-lab-directory).
Copy an independent provider directory into the lab; never alter its original.
GMCM 1.16.0 creates its own default `config.json` during the first launch when
that file is absent. That write is legitimate provider behavior, but it changes
the selected companion after the ownership marker is established. Prepare the
owned copy with those exact defaults before staging instead. This is not an
ownership bypass, and it is not the mod user's Save action:

```powershell
$providerConfig = Join-Path $provider 'config.json'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    $providerConfig,
    "{`r`n  `"OpenMenuKey`": `"None`",`r`n  `"ScrollSpeed`": 120`r`n}",
    $utf8NoBom)
$providerDefaults = Get-Content -LiteralPath $providerConfig -Raw | ConvertFrom-Json
if ($providerDefaults.OpenMenuKey -cne 'None' -or
    [int]$providerDefaults.ScrollSpeed -ne 120 -or
    (Get-Item -LiteralPath $providerConfig).Length -ne 52) {
    throw 'The owned GMCM 1.16.0 config is not the exact canonical default.'
}
Get-FileHash -LiteralPath @(
    (Join-Path $provider 'manifest.json'),
    (Join-Path $provider 'GenericModConfigMenu.dll'),
    $providerConfig) -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'prepared-provider-hashes.json')
```

The selected provider source remains untouched. Record whether it originally
contained a config and its hash if present, then record the owned copy's new
config hash separately. A different provider version or different intended
provider settings require a new bounded acceptance; do not silently normalize
them into this 1.16.0 baseline.

Add this optional dependency to the generated manifest:

```json
"Dependencies": [
  {
    "UniqueID": "spacechase0.GenericModConfigMenu",
    "MinimumVersion": "1.16.0",
    "IsRequired": false
  }
]
```

Add a checked-in default config to the example:

```json
{
  "ArrivalRow": 15
}
```

Create that file with the serialization SMAPI 4.5.2 preserves after
`ReadConfig`: UTF-8 without BOM, CRLF inside the object, and no final newline.
Validate the numeric target before accepting these bytes as the packaging
baseline:

```powershell
$sourceConfig = Join-Path $mod 'config.json'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    $sourceConfig,
    "{`r`n  `"ArrivalRow`": 15`r`n}",
    $utf8NoBom)
$sourceDocument = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($sourceConfig))
try {
    $sourceFields = @($sourceDocument.RootElement.EnumerateObject())
    [int]$sourceValue = 0
    if ($sourceFields.Count -ne 1 -or
        $sourceFields[0].Name -cne 'ArrivalRow' -or
        -not $sourceFields[0].Value.TryGetInt32([ref]$sourceValue) -or
        $sourceValue -ne 15 -or
        (Get-Item -LiteralPath $sourceConfig).Length -ne 24) {
        throw 'The source config is not the exact canonical numeric default.'
    }
}
finally { $sourceDocument.Dispose() }
Get-FileHash -LiteralPath $sourceConfig -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'source-config-baseline-hash.json')
```

Do not obtain this baseline by launching the review and reconciling startup
drift. If a startup normalization was used to discover the bytes, preserve that
launch as a failed candidate, stop it cleanly, update the source and package,
and declare the rebuilt ZIP as the new candidate before functional input.

The reconciliation operation requires the selected root config to exist when
staging begins. Make that explicit in the project file so the package contains
the default rather than relying on SMAPI to create a new file after launch:

```xml
<ItemGroup>
  <None Update="config.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Replace `ModEntry.cs` with this complete source:

```csharp
using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace GmcmArrivalRow;

public sealed class ModConfig
{
    public int ArrivalRow { get; set; } = ModEntry.DefaultArrivalRow;
}

public sealed class ModEntry : Mod
{
    public const int MinimumArrivalRow = 15;
    public const int MaximumArrivalRow = 19;
    public const int ArrivalRowInterval = 2;
    public const int DefaultArrivalRow = 15;

    private const int MinimumSliderStep = 0;
    private const int MaximumSliderStep = 2;
    private const int SliderStepInterval = 1;

    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string SupportedGmcmVersion = "1.16.0";

    private ModConfig Config = new();
    private IGenericModConfigMenuApi? GmcmApi;
    private string GmcmUnavailableReason = "GMCM initialization has not run yet.";

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        if (!IsSupportedArrivalRow(Config.ArrivalRow))
        {
            Monitor.Log(
                $"Invalid ArrivalRow={Config.ArrivalRow}; using default {DefaultArrivalRow}. " +
                $"Expected {MinimumArrivalRow}..{MaximumArrivalRow} in steps of {ArrivalRowInterval}.",
                LogLevel.Error);
            Config.ArrivalRow = DefaultArrivalRow;
        }

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.ConsoleCommands.Add(
            "gmcm_arrival_row_open",
            "Open this mod's GMCM page. A loaded world and GMCM 1.16.0 are required.",
            OnOpenMenuCommand);
        helper.ConsoleCommands.Add(
            "gmcm_arrival_row_state",
            "Report the current in-memory arrival row.",
            OnStateCommand);
    }

    private static bool IsSupportedArrivalRow(int value)
    {
        return value >= MinimumArrivalRow &&
            value <= MaximumArrivalRow &&
            (value - MinimumArrivalRow) % ArrivalRowInterval == 0;
    }

    private static int ArrivalRowToSliderStep(int arrivalRow)
    {
        return (arrivalRow - MinimumArrivalRow) / ArrivalRowInterval;
    }

    private static int SliderStepToArrivalRow(int sliderStep)
    {
        int boundedStep = Math.Clamp(sliderStep, MinimumSliderStep, MaximumSliderStep);
        return MinimumArrivalRow + boundedStep * ArrivalRowInterval;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IModInfo? provider = Helper.ModRegistry.Get(GmcmId);
        if (provider is null)
        {
            GmcmUnavailableReason = $"Required provider {GmcmId} {SupportedGmcmVersion} is not installed.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        string installedVersion = provider.Manifest.Version.ToString();
        if (!string.Equals(installedVersion, SupportedGmcmVersion, StringComparison.Ordinal))
        {
            GmcmUnavailableReason =
                $"Unsupported {GmcmId} version {installedVersion}; this example requires exactly {SupportedGmcmVersion}.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        IGenericModConfigMenuApi? api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (api is null)
        {
            GmcmUnavailableReason =
                $"{GmcmId} {SupportedGmcmVersion} did not expose the expected GMCM API.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        GmcmApi = api;
        GmcmUnavailableReason = string.Empty;

        api.Register(
            manifest: ModManifest,
            reset: () => Config = new ModConfig(),
            save: () => Helper.WriteConfig(Config),
            titleScreenOnly: false);
        api.AddNumberOption(
            manifest: ModManifest,
            getValue: () => ArrivalRowToSliderStep(Config.ArrivalRow),
            setValue: step => Config.ArrivalRow = SliderStepToArrivalRow(step),
            name: () => "Arrival row",
            tooltip: () => "Warp the local player to Farm tile 64 on this row when a day starts.",
            min: MinimumSliderStep,
            max: MaximumSliderStep,
            interval: SliderStepInterval,
            formatValue: step => $"Row {SliderStepToArrivalRow(step)}",
            fieldId: "ArrivalRow");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
            return;

        Game1.warpFarmer("Farm", 64, Config.ArrivalRow, false);
    }

    private void OnOpenMenuCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("gmcm_arrival_row_open requires a loaded world.", LogLevel.Warn);
            return;
        }

        if (GmcmApi is null)
        {
            Monitor.Log($"GMCM menu unavailable: {GmcmUnavailableReason}", LogLevel.Warn);
            return;
        }

        GmcmApi.OpenModMenu(ModManifest);
    }

    private void OnStateCommand(string command, string[] args)
    {
        Monitor.Log($"ArrivalRow={Config.ArrivalRow}", LogLevel.Info);
    }
}

public interface IGenericModConfigMenuApi
{
    void Register(IManifest manifest, Action reset, Action save, bool titleScreenOnly = true);

    void AddNumberOption(
        IManifest manifest,
        Func<int> getValue,
        Action<int> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        int? min = null,
        int? max = null,
        int? interval = null,
        Func<int, string>? formatValue = null,
        string? fieldId = null);

    void OpenModMenu(IManifest manifest);
}
```

The runtime check deliberately fails closed to the default for a direct invalid
integer config. It does not rewrite the invalid file. The native integer slider
uses 0/1/2 because GMCM 1.16.0 adjusts integers to absolute multiples of its
`interval`; passing the domain's odd minimum 15 with interval 2 would expose
invalid even values 16 and 18. The thin conversion keeps the native widget and
its boundary behavior while mapping its three valid positions to rows 15/17/19.
The recipe additionally validates all explicit config transfers before staging
them.

## Validate, build, and retain the exact package

Use this recipe-specific validator. It accepts exactly one integer property and
only the three values generated by the declared bounds and interval:

```powershell
function Read-ArrivalRow([string] $Path) {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'Arrival-row config must be an object.'
        }
        $fields = @($document.RootElement.EnumerateObject())
        if ($fields.Count -ne 1 -or $fields[0].Name -cne 'ArrivalRow') {
            throw 'Arrival-row config must contain exactly one ArrivalRow property.'
        }
        [int]$value = 0
        if (-not $fields[0].Value.TryGetInt32([ref]$value) -or
            $value -lt 15 -or $value -gt 19 -or (($value - 15) % 2) -ne 0) {
            throw 'ArrivalRow must be integer 15, 17, or 19.'
        }
        return $value
    }
    finally { $document.Dispose() }
}

$invalidCases = @(
    '{"ArrivalRow":16}',
    '{"ArrivalRow":21}',
    '{"ArrivalRow":"19"}',
    '{"ArrivalRow":15,"ArrivalRow":19}'
)
foreach ($index in 0..($invalidCases.Count - 1)) {
    $path = Join-Path $evidence "invalid-$index.json"
    $invalidCases[$index] | Set-Content -LiteralPath $path
    $rejected = $false
    try { Read-ArrivalRow $path | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid case $index was accepted." }
}
if ((Read-ArrivalRow (Join-Path $mod 'config.json')) -ne 15) {
    throw 'The source default is not ArrivalRow=15.'
}
```

These negative cases prove only this transfer validator. They are never staged
or packaged as an accepted config.

```powershell
& $sdvkit project check $mod --json | Tee-Object (Join-Path $evidence 'check.json')
if ($LASTEXITCODE -ne 0) { throw 'Authoring check failed.' }
& $sdvkit project build $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'build.json')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$packageJson = & $sdvkit project package $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'package.json')
if ($LASTEXITCODE -ne 0) { throw 'Package failed.' }
$package = ($packageJson -join [Environment]::NewLine) | ConvertFrom-Json
$packageRoot = [IO.Path]::GetFullPath([string]$package.root)
$zip = [IO.Path]::GetFullPath((Join-Path $packageRoot ([string]$package.archive)))
$packagePrefix = [IO.Path]::TrimEndingDirectorySeparator($packageRoot) +
    [IO.Path]::DirectorySeparatorChar
if (-not $zip.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $zip -PathType Leaf)) {
    throw 'The reported package archive is not a regular file below its root.'
}
Get-FileHash -LiteralPath $zip -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'package-hash.json')

$defaultExtraction = Join-Path $evidence 'package-default'
if (Test-Path -LiteralPath $defaultExtraction) { throw 'Choose a fresh extraction.' }
Expand-Archive -LiteralPath $zip -DestinationPath $defaultExtraction
$ready = Join-Path $defaultExtraction 'GmcmArrivalRow'
if ((Read-ArrivalRow (Join-Path $ready 'config.json')) -ne 15) {
    throw 'The exact package does not contain the expected default config.'
}
Get-FileHash -LiteralPath @(
    (Join-Path $ready 'manifest.json'),
    (Join-Path $ready 'GmcmArrivalRow.dll'),
    (Join-Path $ready 'config.json')) -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'default-artifact-hashes.json')
```

Require the ZIP to contain only the original mod's DLL, manifest and default
config. It must not contain GMCM, game binaries, saves or evidence. Review this
extracted ready artifact directly, without `--project`, so the exact retained
DLL is staged. Record the ZIP hash and the extracted DLL, manifest, and canonical
24-byte config hashes as the final candidate identities. Build/package success
does not prove its runtime effect.

## Prove default and unsaved slider steps

Prepare the disposable fixture only after exclusive ownership is granted and all
previous roles are confirmed stopped/reset:

```powershell
& $sdvkit lab test-save --topology single --game-path $gamePath --json
if ($LASTEXITCODE -ne 0) { throw 'Disposable baseline preparation failed.' }
& $sdvkit project review start $ready --game-path $gamePath `
    --companion $provider --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-default.json')
if ($LASTEXITCODE -ne 0) { throw 'Default review start failed.' }
$status = & $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'status-default.json') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $status.state -cne 'running') {
    throw 'Default review status failed.'
}
$targets = @($status.artifacts | Where-Object {
    $_.role -ceq 'target' -and $_.uniqueId -ceq 'ExampleAuthor.GmcmArrivalRow'
})
if ($targets.Count -ne 1) { throw 'The exact original target is unavailable.' }
$readyRoot = [IO.Path]::GetFullPath($ready)
$sourceRoot = [IO.Path]::GetFullPath([string]$targets[0].sourceRoot)
if (-not $sourceRoot.Equals($readyRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The target is not the retained default package extraction.'
}
$staged = [IO.Path]::GetFullPath((Join-Path $status.labRoot $targets[0].stagingPath))
$labOutput = [IO.Path]::GetFullPath((Join-Path $lab '.sdvkit')) +
    [IO.Path]::DirectorySeparatorChar
if (-not $staged.StartsWith($labOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected staging directory is outside this lab .sdvkit.'
}
$stagedConfig = Join-Path $staged 'config.json'
foreach ($file in @('manifest.json', 'GmcmArrivalRow.dll', 'config.json')) {
    if ((Get-FileHash (Join-Path $ready $file) -Algorithm SHA256).Hash -ne
        (Get-FileHash (Join-Path $staged $file) -Algorithm SHA256).Hash) {
        throw "Default staged bytes differ: $file"
    }
}
$companions = @($status.artifacts | Where-Object {
    $_.role -ceq 'companion' -and $_.uniqueId -ceq 'spacechase0.GenericModConfigMenu'
})
if ($companions.Count -ne 1) { throw 'The exact selected GMCM companion is unavailable.' }
$stagedProvider = [IO.Path]::GetFullPath(
    (Join-Path $status.labRoot $companions[0].stagingPath))
foreach ($file in @('manifest.json', 'GenericModConfigMenu.dll', 'config.json')) {
    if ((Get-FileHash (Join-Path $provider $file) -Algorithm SHA256).Hash -ne
        (Get-FileHash (Join-Path $stagedProvider $file) -Algorithm SHA256).Hash) {
        throw "Prepared provider staged bytes differ: $file"
    }
}
$stagingIdentities = [ordered]@{
    targetBuildIdentity = $targets[0].buildIdentity
    targetConfigSha256 = (Get-FileHash $stagedConfig -Algorithm SHA256).Hash
    providerBuildIdentity = $companions[0].buildIdentity
    providerConfigSha256 = (
        Get-FileHash (Join-Path $stagedProvider 'config.json') -Algorithm SHA256).Hash
}
$stagingIdentities | ConvertTo-Json |
    Set-Content (Join-Path $evidence 'default-staging-identities.json')
$persistentSaves = [IO.Path]::GetFullPath(
    (Join-Path $status.labRoot $status.persistentSavesPath))
$stardewData = Split-Path $persistentSaves -Parent
$smapiLog = Join-Path $stardewData 'ErrorLogs/SMAPI-latest.txt'
if (-not (Test-Path -LiteralPath $smapiLog -PathType Leaf)) {
    throw 'The exact owned SMAPI log is unavailable.'
}

function Wait-OwnedLogText(
    [string] $Path,
    [long] $StartOffset,
    [string] $Expected,
    [int] $TimeoutSeconds = 10
) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastObserverFailure = $null
    do {
        try {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite)
            try {
                if ($stream.Length -lt $StartOffset) { throw 'The owned log rotated.' }
                [void]$stream.Seek($StartOffset, [IO.SeekOrigin]::Begin)
                $reader = [IO.StreamReader]::new(
                    $stream,
                    [Text.Encoding]::UTF8,
                    $true,
                    1024,
                    $true)
                try { $tail = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
            if ($tail.Contains($Expected, [StringComparison]::Ordinal)) { return $tail }
        }
        catch { $lastObserverFailure = $_.Exception.Message }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $detail = if ($lastObserverFailure) { " Last observer failure: $lastObserverFailure" } else { '' }
    throw "Timed out waiting for owned log text '$Expected'.$detail"
}

function Invoke-OwnedReviewCommand(
    [string] $Command,
    [string] $ExpectedLogText,
    [string] $EvidenceName
) {
    $offset = (Get-Item -LiteralPath $smapiLog).Length
    $commandJson = & $sdvkit project review command $Command --topology single --json
    $commandExit = $LASTEXITCODE
    $commandJson | Set-Content -LiteralPath (Join-Path $evidence "$EvidenceName-command.json")
    $delivery = ($commandJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($commandExit -ne 0 -or $delivery.commandWritten -ne $true) {
        throw "Review command delivery failed: $Command"
    }
    try {
        Wait-OwnedLogText $smapiLog $offset $ExpectedLogText 10 |
            Set-Content -LiteralPath (Join-Path $evidence "$EvidenceName-log-tail.txt")
    }
    catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence "$EvidenceName-observer-failure.txt")
        throw
    }
}

function Invoke-OwnedViewportScreenshot(
    [string] $Label,
    [string] $EvidenceName
) {
    $screenshot = Join-Path $stardewData "Screenshots/SDVKit-$Label.png"
    if (Test-Path -LiteralPath $screenshot) {
        throw "Choose a fresh screenshot label: $Label"
    }
    $offset = (Get-Item -LiteralPath $smapiLog).Length
    $requestedAt = [DateTime]::UtcNow
    $commandJson = & $sdvkit project review command `
        "sdvkit screenshot viewport $Label" --topology single --json
    $commandExit = $LASTEXITCODE
    $commandJson | Set-Content -LiteralPath (
        Join-Path $evidence "$EvidenceName-command.json")
    $delivery = ($commandJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($commandExit -ne 0 -or $delivery.commandWritten -ne $true) {
        throw "Screenshot command delivery failed: $Label"
    }
    try {
        $expectedSuccess = "Created isolated viewport screenshot '$screenshot'."
        Wait-OwnedLogText $smapiLog $offset $expectedSuccess 10 |
            Set-Content -LiteralPath (Join-Path $evidence "$EvidenceName-log-tail.txt")
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        $lastObserverFailure = $null
        do {
            try {
                $file = Get-Item -LiteralPath $screenshot -Force
                if ($file.PSIsContainer -or
                    ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                    $file.Length -lt 1 -or
                    $file.LastWriteTimeUtc -lt $requestedAt.AddSeconds(-2)) {
                    throw 'The screenshot file is empty, linked, stale, or not regular.'
                }
                [pscustomobject]@{
                    path = $file.FullName
                    bytes = $file.Length
                    sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                    lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('O')
                } | ConvertTo-Json | Set-Content -LiteralPath (
                    Join-Path $evidence "$EvidenceName-file.json")
                return $file.FullName
            }
            catch { $lastObserverFailure = $_.Exception.Message }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Screenshot file confirmation timed out: $lastObserverFailure"
    }
    catch {
        $_ | Out-String | Set-Content -LiteralPath (
            Join-Path $evidence "$EvidenceName-observer-failure.txt")
        throw
    }
}

function Get-OwnedMenuRevision([string] $EvidenceName) {
    $menuJson = & $sdvkit project review menu --topology single --json
    $menuExit = $LASTEXITCODE
    $menuJson | Set-Content -LiteralPath (Join-Path $evidence "$EvidenceName.json")
    $current = ($menuJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($menuExit -ne 0 -or
        $current.state -cne 'ready' -or
        -not $current.menuOpen -or
        [string]::IsNullOrWhiteSpace([string]$current.uiRevision)) {
        throw 'A fresh owned GMCM revision is unavailable.'
    }
    return [string]$current.uiRevision
}
```

Require exact target/provider versions and hashes, a loaded target and GMCM
1.16.0, verified fixture identity, and Farm tile **64,15**. Resolve the target's
status-owned staging path now and verify its manifest, DLL and default config
against `$ready`. Retain the staged config path before editing; do not rediscover
it from normal Mods.

```powershell
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=15' 'default-state'
$openJson = & $sdvkit project review command 'gmcm_arrival_row_open' `
    --topology single --json
$openExit = $LASTEXITCODE
$openJson | Set-Content -LiteralPath (Join-Path $evidence 'open-command.json')
$open = ($openJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($openExit -ne 0 -or $open.commandWritten -ne $true) {
    throw 'GMCM open-command delivery failed.'
}
$menuJson = & $sdvkit project review menu --topology single --json
$menuExit = $LASTEXITCODE
$menuJson | Set-Content -LiteralPath (Join-Path $evidence 'menu-default.json')
$menu = ($menuJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($menuExit -ne 0 -or $menu.state -cne 'ready' -or -not $menu.menuOpen) {
    throw 'The expected GMCM page did not open.'
}
[void](Invoke-OwnedViewportScreenshot 'gmcm-number-default' 'screenshot-default')
```

Inspect the fresh owned screenshot. Require this mod's GMCM page, one **Arrival
row** slider at **Row 15**, and the real Save control. The menu type should be
`GenericModConfigMenu.Framework.SpecificModConfigMenu`; partial custom-menu
inspection may return no numeric components, so the screenshot owns slider
location and value. Translate the observed screenshot point to screen-local UI
coordinates when scaling differs.

Set `$sliderMinX`, `$sliderMidX`, `$sliderMaxX`, and `$sliderY` from this exact
screenshot. Here `$sliderMaxX` is the visible maximum tick or knob center, not
the maximum drag endpoint. The displayed default at `$sliderMinX`
proves the lower endpoint. Calculate one point strictly between the observed
middle and maximum tick centers; this is an intentionally off-step pointer
position, not a fourth valid value. Use a fresh menu revision for every action.
The atomic click and drag commands prepare the process-local cursor through one
neutral game update before pressing; the drag additionally holds its endpoint
for one update before release. Move to the middle step with one click, then drag
from that known middle handle to the off-step point, capturing each result:

```powershell
$middleRevision = Get-OwnedMenuRevision 'menu-before-middle'
Invoke-OwnedReviewCommand `
    "sdvkit input click $sliderMidX $sliderY MouseLeft 1 $middleRevision" `
    'The complete chord was observed and released.' `
    'middle-click'
[void](Invoke-OwnedViewportScreenshot 'gmcm-number-middle' 'screenshot-middle')
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=15' 'middle-unsaved-state'

$sliderOffStepX = [int][Math]::Round(
    ($sliderMidX + $sliderMaxX) / 2,
    [MidpointRounding]::AwayFromZero)
if ($sliderOffStepX -le $sliderMidX -or $sliderOffStepX -ge $sliderMaxX) {
    throw 'The observed slider is too narrow for a distinct off-step click.'
}
$offStepRevision = Get-OwnedMenuRevision 'menu-before-off-step'
Invoke-OwnedReviewCommand `
    "sdvkit input drag $sliderMidX $sliderY $sliderOffStepX $sliderY MouseLeft 6 $offStepRevision" `
    'The complete chord was observed and released.' `
    'off-step-drag'
[void](Invoke-OwnedViewportScreenshot 'gmcm-number-off-step' 'screenshot-off-step')
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=15' 'off-step-unsaved-state'
```

Inspect the off-step screenshot before continuing. Set `$sliderCurrentX` to the
center of the handle in that exact screenshot. Set `$sliderMaxDragX` to the
point at or just beyond the visible right edge of the track, strictly to the
right of the maximum tick or knob center and still inside the owned viewport.
Do not use the maximum knob center itself as the drag endpoint. In the selected
GMCM 1.16.0 provider, `Slider<int>` calculates
`(mouseX - Position.X) / Width`, truncates the scaled integer range, and only
then clamps it. A center click can therefore remain below the maximum. The
accepted native-input proof for this same provider and 1280 x 720 viewport used
**830,140 -> 1010,140** and visibly changed Row 17 to Row 19; remeasure after
any viewport or scaling change.

Drag from the currently observed handle to that measured endpoint and capture
the unsaved maximum:

```powershell
if ($sliderCurrentX -lt $sliderMinX -or $sliderCurrentX -gt $sliderMaxX) {
    throw 'The observed current handle is outside the slider tick range.'
}
if ($sliderMaxDragX -le $sliderMaxX) {
    throw 'The maximum drag endpoint must be right of the visible maximum tick center.'
}

$maximumRevision = Get-OwnedMenuRevision 'menu-before-maximum'
Invoke-OwnedReviewCommand `
    "sdvkit input drag $sliderCurrentX $sliderY $sliderMaxDragX $sliderY MouseLeft 6 $maximumRevision" `
    'The complete chord was observed and released.' `
    'maximum-drag'
[void](Invoke-OwnedViewportScreenshot `
    'gmcm-number-unsaved-max' `
    'screenshot-unsaved-maximum')
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=15' 'maximum-unsaved-state'
```

Require completed process-local click/drag release acknowledgements. The
default, middle, and endpoint screenshots must show **Row 15**, **Row 17**, and
**Row 19**. Inspect the off-step screenshot and record whether GMCM snapped that
between-ticks drag to Row 17 or Row 19; any Row 16, Row 18, out-of-range value,
or unreadable result fails this gate. At every unsaved position, the owned log
must still report `ArrivalRow=15`, the staged config must still validate as 15,
and runtime state must remain Farm 64,15. This separates pointer geometry and
native UI snapping from the mod's current value and effect.

Keep Stardew unfocused while observing the mouse actions. Record unchanged
physical pointer and foreground state, allowing the acknowledgement's documented
external-foreground diagnostic. Never use global input, physical pointer movement,
focus automation, or coordinates from another run.

## Save, reconcile, and retain the export

Read the current screenshot again and set `$saveX`/`$saveY` to the exact Save
control center. The legacy command reports delivery before game-side completion,
so wait separately for the exact input acknowledgement and then for the saved
file within a bounded interval. Preserve the last observer error if the file
cannot be confirmed:

```powershell
function Wait-ArrivalRowFile(
    [string] $Path,
    [int] $Expected,
    [int] $TimeoutSeconds = 10
) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastObserverFailure = $null
    do {
        try {
            $actual = Read-ArrivalRow $Path
            if ($actual -eq $Expected) { return $actual }
            $lastObserverFailure = "Observed ArrivalRow=$actual."
        }
        catch { $lastObserverFailure = $_.Exception.Message }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for ArrivalRow=$Expected. Last observer result: $lastObserverFailure"
}

$saveRevision = Get-OwnedMenuRevision 'menu-before-save'
Invoke-OwnedReviewCommand `
    "sdvkit input click $saveX $saveY MouseLeft 1 $saveRevision" `
    'The complete chord was observed and released.' `
    'save-click'
try {
    [void](Wait-ArrivalRowFile $stagedConfig 19 10)
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (
        Join-Path $evidence 'save-file-observer-failure.txt')
    throw
}
```

Now require one ordinary owned menu request to fail with staged-file drift. That
failure is expected evidence that Save changed bytes; do not repeat input or edit
an ownership marker. Accept only the selected mod's valid root config:

```powershell
$driftJson = & $sdvkit project review menu --topology single --json
$driftExit = $LASTEXITCODE
$driftJson | Set-Content -LiteralPath (Join-Path $evidence 'expected-saved-drift.json')
$drift = ($driftJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($driftExit -ne 3 -or
    $drift.state -cne 'unavailable' -or
    $drift.errorCode -cne 'reviewStagingOwnershipDrifted') {
    throw 'The request did not fail with the expected saved-config staging drift.'
}

$reconcileJson = & $sdvkit project review config-reconcile `
    --mod ExampleAuthor.GmcmArrivalRow --topology single --json |
    Tee-Object (Join-Path $evidence 'config-reconcile.json')
if ($LASTEXITCODE -ne 0) { throw 'Config reconciliation failed.' }
$reconcile = ($reconcileJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($reconcile.state -cne 'reconciled' -or $null -eq $reconcile.reconciliation) {
    throw 'The saved config was not reconciled.'
}
$export = [IO.Path]::GetFullPath((Join-Path $lab $reconcile.reconciliation.exportPath))
if ((Read-ArrivalRow $export) -ne 19) { throw 'The retained export is not row 19.' }
if ((Get-FileHash $export -Algorithm SHA256).Hash -ne
    (Get-FileHash $stagedConfig -Algorithm SHA256).Hash) {
    throw 'The retained export differs from the reconciled staged config.'
}
Invoke-OwnedReviewCommand `
    'sdvkit input cursor clear' `
    'Cleared the virtual review cursor.' `
    'saved-cursor-clear'
```

Require old/new config hashes, unchanged non-config content identity, exact
launch/process/profile ownership, and an audit/export below this lab's
`.sdvkit/`. The source and ZIP must remain unchanged. If the documented audit
warning occurs, follow the bounded same-selection recovery in the reconciliation
contract before continuing.

After reconciliation, the same process should accept menu and input inspection
again. Require `gmcm_arrival_row_state` to report **19**, a screenshot to show
Row 19 with the menu still open, and runtime position to remain **64,15** because
Save does not replay `DayStarted`:

```powershell
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=19' 'saved-state'
$savedMenuJson = & $sdvkit project review menu --topology single --json
$savedMenuExit = $LASTEXITCODE
$savedMenuJson | Set-Content -LiteralPath (Join-Path $evidence 'menu-saved.json')
$savedMenu = ($savedMenuJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($savedMenuExit -ne 0 -or $savedMenu.state -cne 'ready' -or -not $savedMenu.menuOpen) {
    throw 'Menu inspection did not resume after reconciliation.'
}
[void](Invoke-OwnedViewportScreenshot 'gmcm-number-saved' 'screenshot-saved')
```

## Stop, restage, and prove row 19 in a new process

Retain exact status, full owned log, screenshots, reconciliation receipt and
hashes before stopping. Stop without resetting the fixture:

```powershell
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-default.json')
if ($LASTEXITCODE -ne 0) { throw 'Exact stop did not complete.' }
if (Test-Path -LiteralPath $staged) { throw 'Owned staging was not removed.' }
```

Require a confirmed exact process exit and `stagingRemoved=true`. Create a fresh
configured extraction from the unchanged ZIP and replace only its own validated
default config with the reconciled export:

```powershell
$configuredExtraction = Join-Path $evidence 'package-configured'
if (Test-Path -LiteralPath $configuredExtraction) { throw 'Choose a fresh extraction.' }
Expand-Archive -LiteralPath $zip -DestinationPath $configuredExtraction
$configured = Join-Path $configuredExtraction 'GmcmArrivalRow'
$configuredConfig = Join-Path $configured 'config.json'
if ((Read-ArrivalRow $configuredConfig) -ne 15) { throw 'Fresh package default changed.' }
[IO.File]::Copy($export, $configuredConfig, $true)
if ((Read-ArrivalRow $configuredConfig) -ne 19) { throw 'Configured input is not row 19.' }
foreach ($file in @('manifest.json', 'GmcmArrivalRow.dll')) {
    if ((Get-FileHash (Join-Path $ready $file) -Algorithm SHA256).Hash -ne
        (Get-FileHash (Join-Path $configured $file) -Algorithm SHA256).Hash) {
        throw "Packaged code identity changed: $file"
    }
}
if ((Get-FileHash $export -Algorithm SHA256).Hash -ne
    (Get-FileHash $configuredConfig -Algorithm SHA256).Hash) {
    throw 'Configured input differs from the reconciled export.'
}

& $sdvkit project review start $configured --game-path $gamePath `
    --companion $provider --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Configured restart failed.' }
$configuredStatusJson = & $sdvkit project review status --topology single --json
$configuredStatusExit = $LASTEXITCODE
$configuredStatusJson | Set-Content -LiteralPath (Join-Path $evidence 'status-configured.json')
$configuredStatus = ($configuredStatusJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($configuredStatusExit -ne 0 -or $configuredStatus.state -cne 'running') {
    throw 'Configured review status failed.'
}
$persistentSaves = [IO.Path]::GetFullPath(
    (Join-Path $configuredStatus.labRoot $configuredStatus.persistentSavesPath))
$stardewData = Split-Path $persistentSaves -Parent
$smapiLog = Join-Path $stardewData 'ErrorLogs/SMAPI-latest.txt'
if (-not (Test-Path -LiteralPath $smapiLog -PathType Leaf)) {
    throw 'The restarted owned SMAPI log is unavailable.'
}
Invoke-OwnedReviewCommand `
    'gmcm_arrival_row_state' 'ArrivalRow=19' 'restart-state'
[void](Invoke-OwnedViewportScreenshot 'gmcm-number-restart' 'screenshot-restart')
```

Require a new launch and process identity, the same exact DLL/manifest and
provider hashes, a changed staged build identity attributable only to config,
the fresh log reply `ArrivalRow=19`, and runtime state **Farm, tile 64,19** after
the new process's `DayStarted`. Inspect the fresh viewport image before claiming
the visible destination. Do not edit or save the configured value in this second
process; it already participates in the accepted staging identity.

This proves explicit config transfer and a restart-loaded numeric effect. It does
not prove automatic config retention, a changed compiled default, overnight
behavior, arbitrary coordinates, float controls, or multiplayer.

## Finish and record the result

Retain final status, full isolated log, diagnostics, screenshots and identities,
then complete the owned cleanup:

```powershell
& $sdvkit project review diagnostics --mod ExampleAuthor.GmcmArrivalRow `
    --limit 20 --topology single --json |
    Tee-Object (Join-Path $evidence 'diagnostics-configured.json')
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Final exact stop failed.' }
& $sdvkit project review reset --topology single --json |
    Tee-Object (Join-Path $evidence 'reset.json')
if ($LASTEXITCODE -ne 0) { throw 'Final fixture reset failed.' }
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'final-status.json')
```

Require exact process exit, removed owned staging/mailbox/mount, reset fixture,
and no unknown ownership. Recheck source/ZIP/provider hashes and the protected
fingerprints; attribute unrelated external user or mod-manager changes separately.
Never delete ownership state manually to make cleanup pass.

Record default 15, visible unsaved 17 and 19, unchanged unsaved state/config,
Save to 19, expected drift rejection, reconciliation/export hashes, same-process
value 19 with unchanged position, exact stop/removal, new-process value/effect,
same DLL/provider identity, and final cleanup as separate evidence rows. Preserve
failed or inconclusive attempts. Report authoring check, compilation, packaging,
automated tests and actual in-game behavior as distinct gates.

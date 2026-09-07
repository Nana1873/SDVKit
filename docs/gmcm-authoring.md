# Author one GMCM option and verify its saved effect

Build **GMCM Arrival**, an original C# mod whose `Enabled` checkbox controls a
start-of-day warp to Farm tile 64,15. Diagnose its deliberately disabled default,
edit and save the option through GMCM, then explicitly export its own config and
restage the exact packaged DLL to prove the changed behavior after restart.

This is the acceptance procedure for [#161](https://github.com/Nana1873/SDVKit/issues/161)
and [#162](https://github.com/Nana1873/SDVKit/issues/162). The single-player workflow
was observed on 2026-09-07 with the versions below: unchecked default, checked
but unsaved option, saved Boolean, explicit transfer across stop/restaging, and
Farm 64,15 after restart. The linked issues retain delivery gates and limitations;
rerun the affected checks when changing this example or its environment.

## Contract and references

| Concern | Bounded contract |
| --- | --- |
| Provider | Explicitly selected `spacechase0.GenericModConfigMenu` **1.16.0**, with recorded manifest/DLL hashes; the example refuses menu integration with other versions. |
| Environment | Single review, Stardew Valley **1.6.15**, SMAPI **4.5.2**, owned disposable standard-farm fixture. No multiplayer acceptance. |
| Option | Original mod `ExampleAuthor.GmcmArrival`, explicit GMCM `fieldId` **Enabled**, Boolean checkbox, default **false**. Only JSON Boolean values are accepted for the exported variant. |
| Registration | `Register` during `GameLaunched`, `titleScreenOnly: false`; `AddBoolOption` reads/writes the mod's typed config. |
| Effect | `DayStarted` warps the local farmer to Farm 64,15 only when enabled and in a ready single-player world. Saving config does not immediately warp the farmer. |
| Save | The real GMCM **Save** action commits its cached Boolean through the setter and calls the registered `Helper.WriteConfig` callback; the menu stays open. A checked box alone is not saved-state proof. |
| Reload | `Helper.ReadConfig` reads once at Entry. Single-review stop removes staged config; explicit export and renewed staging below `.sdvkit/` carry it into the next process. |
| Unsupported | Other controls, custom/text fields, other provider versions and foreign mod options are outside this contract. No generic GMCM text or option-editing API is added to SDVKit. |

Use the [official GMCM download](https://www.nexusmods.com/stardewvalley/mods/5098?tab=files)
to obtain the selected provider. The small API interface below was checked against
the local 1.16.0 provider's `GenericModConfigMenu.Framework.Api`; its
`SpecificModConfigMenu` uses a `SpaceShared.UI.Checkbox` for Boolean options.
Those implementation details explain feasibility, not a stable introspection API.
Do not copy provider implementation code or binaries into this repository.

For mod authoring, use the [SMAPI config guide](https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Config),
[4.5.2 game-loop events](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Events/IGameLoopEvents.cs)
and the [existing SMAPI recipe](smapi-authoring.md#choose-the-tool-and-references).
Use [menu inspection](menu-inspection.md) for fresh revisions and
[owned input](mcp.md#opt-in-input) for the exact process-local mouse path.
GMCM `fieldId` is an author-selected identity; it is not an SDVKit numeric component
ID. GMCM remains partial public-menu coverage, so use screenshots to locate this
particular checkbox and Save control.

## Prepare the original project and selected provider

Use PowerShell 7 and the [installed CLI](../README.md#install), with `$sdvkit`
set to its absolute executable path. For a checkout build, follow the
[authoring setup](cp-authoring.md#prerequisites-and-one-lab-directory), omitting CP
selection. Retain the exact SDVKit ZIP/hash/commit and use one lab directory as
the current directory for every live command. Resolve `$gamePath` from `doctor`
and select it explicitly. Place an explicitly chosen ready GMCM 1.16.0 directory
at `.sdvkit/GenericModConfigMenu`; do not install it into normal Mods for this test.
Use an independent ordinary copy, not a link to a mod-manager installation.
Set `$selectedGmcm` to the explicitly selected local directory, require the lab
destination to be absent, and copy it there before comparing manifest/DLL hashes.
A Vortex hardlinked source may
fail preparation with `Refresh files must have exactly one filesystem link.`;
copying to the own lab and selecting that independent copy resolves this boundary.
Never unlink, redeploy or alter the original normal Mods files to make staging pass.

```powershell
$lab = $PWD.Path
$mod = Join-Path $lab '.sdvkit/GmcmArrival'
$providerDestination = Join-Path $lab '.sdvkit/GenericModConfigMenu'
if (Test-Path -LiteralPath $providerDestination) { throw 'Choose a fresh GMCM lab destination.' }
New-Item -ItemType Directory -Force -Path (Split-Path $providerDestination) | Out-Null
Copy-Item -LiteralPath $selectedGmcm -Destination $providerDestination -Recurse
$provider = (Resolve-Path -LiteralPath $providerDestination).Path
$evidence = Join-Path $lab '.sdvkit/gmcm-evidence'
if (Test-Path -LiteralPath $evidence) { throw 'Choose a fresh GMCM evidence directory.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$providerManifest = Get-Content -LiteralPath (Join-Path $provider 'manifest.json') -Raw | ConvertFrom-Json
if ($providerManifest.UniqueID -cne 'spacechase0.GenericModConfigMenu' -or
    $providerManifest.Version -cne '1.16.0') { throw 'Select GMCM 1.16.0.' }
Get-FileHash -LiteralPath @(
    (Join-Path $provider 'manifest.json'),
    (Join-Path $provider 'GenericModConfigMenu.dll')) -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'provider-hashes.json')
& $sdvkit doctor --json
# Set $gamePath from the intended ready installation, then verify it:
& $sdvkit doctor --game-path $gamePath --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
& $sdvkit project create smapi-mod $mod --name 'GMCM Arrival' --author ExampleAuthor --unique-id ExampleAuthor.GmcmArrival --description 'Configure an optional start-of-day Farm warp through GMCM.' --json
& $sdvkit project inspect $mod --json
$project = 'GmcmArrival.csproj'
```

Confirm the generated project filename. Do not create over an existing mod.
Identify the intended lab's owner and wait for any previous owner's verified
stop/reset before taking over. Record protected normal Saves/Mods/preferences
read-only using the [existing fingerprint procedure](cp-authoring.md#prerequisites-and-one-lab-directory).
Attribute external user/mod-manager changes separately; never mutate those paths.

Replace this new project's `ModEntry.cs` with the complete source below. Keep the
generated project and manifest, adding this optional manifest dependency:

```json
"Dependencies": [
  { "UniqueID": "spacechase0.GenericModConfigMenu", "MinimumVersion": "1.16.0", "IsRequired": false }
]
```

The optional dependency allows the mod to explain unavailable integration; the
recipe always selects GMCM explicitly. The runtime check enforces this recipe's
exact version, since a manifest minimum is not an exact-version constraint.
The core warp still uses a previously loaded valid config when the optional
provider is unavailable; this gate disables menu integration, not the mod's
independent feature. Such a run cannot pass this recipe's provider-bound acceptance.

```csharp
using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace GmcmArrival;

public sealed class ModConfig
{
    public bool Enabled { get; set; }
}

public sealed class ModEntry : Mod
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string SupportedGmcmVersion = "1.16.0";

    private ModConfig Config = new();
    private IGenericModConfigMenuApi? GmcmApi;
    private string GmcmUnavailableReason = "GMCM initialization has not run yet.";

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.ConsoleCommands.Add(
            "gmcm_arrival_open",
            "Open this mod's GMCM page. A loaded world and GMCM 1.16.0 are required.",
            OnOpenMenuCommand);
        helper.ConsoleCommands.Add(
            "gmcm_arrival_state",
            "Report the current in-memory config value.",
            OnStateCommand);
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
        api.AddBoolOption(
            manifest: ModManifest,
            getValue: () => Config.Enabled,
            setValue: value => Config.Enabled = value,
            name: () => "Enabled",
            tooltip: () => "Warp the local player to Farm tile 64,15 when a day starts.",
            fieldId: "Enabled");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || !Config.Enabled)
            return;

        Game1.warpFarmer("Farm", 64, 15, false);
    }

    private void OnOpenMenuCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("gmcm_arrival_open requires a loaded world.", LogLevel.Warn);
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
        Monitor.Log($"Enabled={Config.Enabled.ToString().ToLowerInvariant()}", LogLevel.Info);
    }
}

public interface IGenericModConfigMenuApi
{
    void Register(IManifest manifest, Action reset, Action save, bool titleScreenOnly = true);

    void AddBoolOption(
        IManifest manifest,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        string? fieldId = null);

    void OpenModMenu(IManifest manifest);
}
```

## Build and retain the exact package

```powershell
& $sdvkit project check $mod --json | Tee-Object (Join-Path $evidence 'check.json')
if ($LASTEXITCODE -ne 0) { throw 'Authoring check failed.' }
& $sdvkit project build $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'build.json')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$packageJson = & $sdvkit project package $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'package.json')
$packageExitCode = $LASTEXITCODE
if ($packageExitCode -ne 0) { throw 'Package failed.' }
$package = ($packageJson -join [Environment]::NewLine) | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$package.root) -or
    [string]::IsNullOrWhiteSpace([string]$package.archive) -or
    [IO.Path]::IsPathFullyQualified([string]$package.archive)) {
    throw 'Package did not report a relative archive below its root.'
}
$packageRoot = [IO.Path]::GetFullPath([string]$package.root)
$zip = [IO.Path]::GetFullPath((Join-Path $packageRoot ([string]$package.archive)))
$packagePrefix = [IO.Path]::TrimEndingDirectorySeparator($packageRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $zip.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Reported package archive escapes its root.'
}
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw 'Reported package archive is unavailable.' }
$unpacked = Join-Path $evidence 'package-default'
if (Test-Path -LiteralPath $unpacked) { throw 'Choose a fresh package extraction directory.' }
Get-FileHash -LiteralPath $zip -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'package-hash.json')
Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
# Select the one extracted root that contains manifest.json and GmcmArrival.dll:
$ready = Join-Path $unpacked 'GmcmArrival'
if (Test-Path (Join-Path $ready 'config.json')) { throw 'Default package must omit config.json.' }
Get-FileHash -LiteralPath @(
    (Join-Path $ready 'manifest.json'),
    (Join-Path $ready 'GmcmArrival.dll')) -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'default-artifact-hashes.json')
```

Inspect ZIP entries and the actual extracted folder name before setting `$ready`.
The original package contains the mod and its defaults, not GMCM, game binaries,
personal config, saves or evidence. Build/package and `project check` do not prove
runtime behavior; `project check` does not validate arbitrary `config.json`.
Review this ready artifact directly, without `--project`, so the game receives
the retained package's DLL rather than another compilation.

## Diagnose the deliberately disabled default

Prepare a disposable baseline only when absent and all roles are stopped:

```powershell
& $sdvkit lab test-save --topology single --game-path $gamePath --json
if ($LASTEXITCODE -ne 0) { throw 'Disposable baseline preparation failed.' }
& $sdvkit project review start $ready --game-path $gamePath --companion $provider --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-default.json')
if ($LASTEXITCODE -ne 0) { throw 'Default review start failed.' }
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'status-default.json')
if ($LASTEXITCODE -ne 0) { throw 'Default review status failed.' }
& $sdvkit project review command 'gmcm_arrival_state' --topology single --json
if ($LASTEXITCODE -ne 0) { throw 'Default config-state command failed.' }
& $sdvkit project review diagnostics --mod ExampleAuthor.GmcmArrival --limit 20 --json |
    Tee-Object (Join-Path $evidence 'diagnostics-default.json')
```

Require exact process/launch, target ID/version/build, loaded target and loaded
GMCM 1.16.0, plus `testSave.state=ready`, `phase=passed`, `identityVerified=true`.
Resolve the target's `artifacts[].stagingPath` against `labRoot` from status and
compare staged DLL/manifest hashes with `$ready`. No unrelated companion is needed.

Read the [runtime values](runtime-state.md) in `project review status --json`.
The intentional diagnosis is: the expected arrival did not happen because **Enabled
defaults to false**. Require the observed disposable-fixture position **FarmHouse,
tile 9,9** instead of Farm 64,15,
and `gmcm_arrival_state`'s actual `Enabled=false` reply in the owned SMAPI log.
Read the isolated log using the [documented status-owned path](live-review.md#diagnose-selected-mod-warnings-and-exceptions);
the diagnostics tool only includes warnings/errors and will not return this Info
reply. Retain the full available log before cleanup. A loaded-mod message or zero
diagnostic warnings alone cannot establish the diagnosis.

## Edit and save through the owned UI

```powershell
& $sdvkit project review command 'gmcm_arrival_open' --topology single --json
& $sdvkit project review menu --topology single --json
& $sdvkit project review command 'sdvkit screenshot viewport gmcm-default' --topology single --json
```

Require the screenshot's completion reply and fresh PNG at its exact owned
profile path, then inspect the real image: it must be the original mod's GMCM page and its sole
**Enabled** checkbox must be unchecked. Require the expected GMCM menu type
`GenericModConfigMenu.Framework.SpecificModConfigMenu` and current exact review
binding. Its menu report has partial coverage and may have `components=[]`;
that does not identify the checkbox or prove it is absent. Pick the checkbox
center from the image, translating screenshot pixels to screen-local UI
coordinates when UI scale differs. Set `$checkboxX` and `$checkboxY` to that
observed center, then run sequentially:

```powershell
& $sdvkit project review command "sdvkit input cursor $checkboxX $checkboxY" --topology single --json
& $sdvkit project review command 'sdvkit input press MouseLeft' --topology single --json
& $sdvkit project review command 'sdvkit screenshot viewport gmcm-checked' --topology single --json
& $sdvkit project review command 'gmcm_arrival_state' --topology single --json
```

After each command, inspect its matching owned log acknowledgement: `commandWritten`
proves delivery only. Require cursor acceptance, completed press/release and the
fresh screenshot proving the checkbox is checked. Require the state command's
owned log reply to remain `Enabled=false`: GMCM has only cached the visible edit,
and the staged config must still contain Boolean `false`. Recheck exact menu and
review state; identify the actual **Save** control in the image and set `$saveX`/`$saveY`
to its center. Coordinates depend on the current window, language and scale;
never reuse another run's numbers without inspecting the current image.

```powershell
& $sdvkit project review menu --topology single --json
& $sdvkit project review command "sdvkit input cursor $saveX $saveY" --topology single --json
& $sdvkit project review command 'sdvkit input press MouseLeft' --topology single --json
& $sdvkit project review command 'gmcm_arrival_state' --topology single --json
& $sdvkit project review command 'sdvkit screenshot viewport gmcm-saved' --topology single --json
& $sdvkit project review command 'sdvkit input cursor clear' --topology single --json
```

Observe the menu remaining open, actual `Enabled=true` from `gmcm_arrival_state`,
and the written staged config. Input acknowledgement does not establish these
effects. Do not call the setter or write a true config file to manufacture the UI
proof. If the menu changes between observation and a cursor/press command, stop
and inspect again: these legacy single-input commands do not take a UI revision.

Keep the game unfocused while verifying mouse effects; record that the physical
pointer and foreground window remain unchanged. Use no global mouse input or
window-focus automation. Unknown controls/provider versions, another mod's menu,
unexpected UI, uncertain completion or an unavailable adapter
require a fresh observation or an explicit failed gate, never a blind retry.

MCP is optional. An existing client bound in this lab with
`project review mcp serve --topology single --allow-input` can use the documented
[screenshot, menu and input tools](mcp.md#opt-in-input), including
`stardew_input_cursor_set`, `stardew_input_press` and `stardew_input_cursor_clear`.
The bounded `stardew_input_click` alternative additionally requires a fresh
`stardew_menu_get` revision. This recipe's CLI acceptance does not separately
establish an MCP input run. Read-only runtime/mod tools provide optional
equivalent observations; no dedicated GMCM MCP bridge is required.

## Validate, export, stop and restage the saved config

Run this small recipe-specific validator in PowerShell 7. It reads only the path
explicitly passed to it and rejects malformed JSON, extra/duplicate properties
and values such as the string `"true"`. It does not repair any file:

```powershell
function Read-ArrivalEnabled([string] $Path) {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'Arrival config must be an object.'
        }
        $fields = @($document.RootElement.EnumerateObject())
        if ($fields.Count -ne 1 -or $fields[0].Name -cne 'Enabled' -or
            $fields[0].Value.ValueKind -notin @(
                [System.Text.Json.JsonValueKind]::True,
                [System.Text.Json.JsonValueKind]::False)) {
            throw 'Arrival config must contain exactly one Boolean Enabled property.'
        }
        return $fields[0].Value.GetBoolean()
    }
    finally { $document.Dispose() }
}
function Assert-PlainPath([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Reparse points are not allowed in this explicit config transfer.'
            }
        }
        $parent = Split-Path -LiteralPath $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
$invalid = Join-Path $evidence 'invalid-enabled-config.json'
'{"Enabled":"not-a-boolean"}' | Set-Content $invalid
$rejected = $false
try { Read-ArrivalEnabled $invalid | Out-Null }
catch { $rejected = $true; $_.Exception.Message | Set-Content (Join-Path $evidence 'invalid-config-diagnosis.txt') }
if (-not $rejected) { throw 'Invalid config was unexpectedly accepted.' }
```

This additional invalid-value exercise proves the local validator only. It does
not claim a game-side validation error, and the invalid file is never staged or
packaged. Correct the real default through the UI as above.

Before stop, get fresh exact status and select only the own target's staged config:

```powershell
$status = & $sdvkit project review status --topology single --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Review status unavailable.' }
if ($status.state -cne 'running') { throw 'The exact review is not running.' }
$targets = @($status.artifacts | Where-Object {
    $_.role -ceq 'target' -and $_.uniqueId -ceq 'ExampleAuthor.GmcmArrival'
})
if ($targets.Count -ne 1) { throw 'The exact original target is unavailable.' }
$readyRoot = [IO.Path]::GetFullPath($ready)
$sourceRoot = [IO.Path]::GetFullPath([string]$targets[0].sourceRoot)
if (-not $sourceRoot.Equals($readyRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The owned target is not the exact ready package source.'
}
$staged = [IO.Path]::GetFullPath((Join-Path $status.labRoot $targets[0].stagingPath))
$labOutput = [IO.Path]::GetFullPath((Join-Path $lab '.sdvkit')) + [IO.Path]::DirectorySeparatorChar
if (-not $staged.StartsWith($labOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected staging directory must stay below this lab .sdvkit.'
}
$stagedConfig = Join-Path $staged 'config.json'
Assert-PlainPath $stagedConfig
Assert-PlainPath $evidence
if (-not (Read-ArrivalEnabled $stagedConfig)) { throw 'The UI has not saved Enabled=true.' }
$export = Join-Path $evidence 'ui-saved-config.json'
if (Test-Path -LiteralPath $export) { throw 'Choose a fresh export file.' }
$sourceHashBefore = (Get-FileHash -LiteralPath $stagedConfig -Algorithm SHA256).Hash
[IO.File]::Copy($stagedConfig, $export, $false)
$exportHash = (Get-FileHash -LiteralPath $export -Algorithm SHA256).Hash
$sourceHashAfter = (Get-FileHash -LiteralPath $stagedConfig -Algorithm SHA256).Hash
if ($sourceHashBefore -ne $exportHash -or $sourceHashAfter -ne $exportHash) {
    throw 'The saved config changed while it was exported.'
}
[pscustomobject]@{
    sourceBefore = $sourceHashBefore
    export = $exportHash
    sourceAfter = $sourceHashAfter
} | ConvertTo-Json | Set-Content (Join-Path $evidence 'export-hash.json')
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-default.json')
if ($LASTEXITCODE -ne 0) { throw 'Exact stop did not complete.' }
```

Resolve and reject reparse points in the selected staging/export ancestors
before copying. The export is authorized only for this original mod, the verified
owned staging source, and these own `.sdvkit/` destinations. Never export a
companion's config, overwrite normal Mods, or copy back to foreign source. Retain
exact status and full owned logs first. Require verified process exit and
`stagingRemoved=true`; the old staged config must now be absent. Do not reset the
fixture between the two halves of this persistence test.

```powershell
if (Test-Path -LiteralPath $staged) { throw 'Owned staging was not removed.' }
$variant = Join-Path $evidence 'configured/GmcmArrival'
if (Test-Path -LiteralPath $variant) { throw 'Choose a fresh configured variant.' }
Assert-PlainPath $variant
Assert-PlainPath $ready
New-Item -ItemType Directory -Force (Split-Path $variant) | Out-Null
Copy-Item -LiteralPath $ready -Destination $variant -Recurse
$variantConfig = Join-Path $variant 'config.json'
if (Test-Path -LiteralPath $variantConfig) { throw 'The default package unexpectedly supplied config.json.' }
[IO.File]::Copy($export, $variantConfig, $false)
if (-not (Read-ArrivalEnabled $variantConfig)) { throw 'Variant is disabled.' }
foreach ($file in @('manifest.json', 'GmcmArrival.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $ready $file) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $variant $file) -Algorithm SHA256).Hash) {
        throw "Package bytes changed: $file"
    }
}
if ((Get-FileHash -LiteralPath $export -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $variantConfig -Algorithm SHA256).Hash) {
    throw 'Variant config differs from the UI export.'
}
& $sdvkit project review start $variant --game-path $gamePath --companion $provider --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Configured review start failed.' }
$configuredStatusJson = & $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'status-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Configured review status failed.' }
$configuredStatus = ($configuredStatusJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($configuredStatus.state -cne 'running') { throw 'Configured review is not running.' }
& $sdvkit project review command 'gmcm_arrival_state' --topology single --json
if ($LASTEXITCODE -ne 0) { throw 'Configured config-state command failed.' }
```

Require a new exact launch and the configured artifact's new staged build identity;
the added config intentionally changes that identity. Compare staged manifest,
DLL and config with the variant. Require unchanged provider bytes/version, loaded
target, ready verified fixture, actual `Enabled=true`, and fresh `project review status --json`
showing **Farm, tile 64,15** after the new process's DayStarted event. Capture and
inspect a fresh viewport to assess that destination in the disposable fixture.
This is not a general safe-spawn resolver for arbitrary farms or tiles.
During this second phase, observe only: do not save/change the already supplied
config again. It now participates in the staged build identity, so changing its
bytes would introduce artifact drift and invalidate this exact-variant check.

These observations prove explicit config transfer across stop/restage and the
new runtime effect. They do not imply automatic mod-config retention, a changed
compiled default, overnight behavior, world-save persistence or multiplayer.
Retain the original ZIP unchanged: it ships `Enabled=false`; the own configured
variant is a separately selected test input carrying the same packaged DLL.

## Finish and record the result

Retain final runtime/menu observations, available full isolated logs, diagnostics,
all identities and file hashes before cleanup:

```powershell
& $sdvkit project review diagnostics --mod ExampleAuthor.GmcmArrival --limit 20 --json |
    Tee-Object (Join-Path $evidence 'diagnostics-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Final diagnostics failed.' }
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-configured.json')
if ($LASTEXITCODE -ne 0) { throw 'Final exact stop failed.' }
& $sdvkit project review reset --topology single --json |
    Tee-Object (Join-Path $evidence 'reset.json')
if ($LASTEXITCODE -ne 0) { throw 'Final fixture reset failed.' }
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'final-status.json')
if ($LASTEXITCODE -ne 0) { throw 'Final stopped status failed.' }
```

Require exact exit, removed owned staging/mailbox/mount, reset fixture and no
unknown active ownership. Recheck source, original ZIP and provider hashes;
compare protected-path fingerprints and attribute external changes. Do not
manually delete ownership state to make cleanup pass. An isolated-option
restoration warning remains separate from a blocked exit or cleanup.

Record a small evidence table covering default diagnosis, actual UI edit/save,
invalid JSON rejection, export/hash equality, exact stop/removal, new staging and
post-restart effect, package identity and final cleanup. Include measured elapsed
time, failed/inconclusive attempts and interventions. Report compilation,
automated tests, packaging and verified game behavior separately. For changes to
SDVKit itself, complete the full offline and selected live
[release checks](releasing.md); this recipe does not waive a failing gate.

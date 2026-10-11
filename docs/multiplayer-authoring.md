# Author and prove a per-player multiplayer mod

> Verified for issue #196. The steps below reproduce the accepted artifacts and gates.

This original SMAPI mod gives Stone to the current farmer on F7. On F8 it shows that farmer's inventory, persisted `modData` and local inventory-event state. The deliberate bug collapses both players into `Game1.MasterPlayer`.

The fix splits authority this way:

1. The host validates and authorizes a farmhand's request.
2. The farmhand's own client adds Stone to its own backpack and writes its own `Farmer.modData`.
3. The client reports a correlated result, which the host validates.

The host does **not** measure or write a remote player's inventory.

| Gate | State |
| --- | --- |
| Broken network shared-player failure | Verified |
| Corrected network host 1 / farmhand 2 | Verified |
| Farmhand leave, stale MCP, rejoin | Verified |
| Natural overnight disk save | Observed, not restart proof |
| Explicit save and new-process reload | Verified |
| Corrected-probe network chest acceptance | Verified |
| Local split-screen state, replacement, persistence, chest, and cleanup | Verified |

This covers one local host with one farmhand in `network-2`, and two screens in one `single --test-save` process. It does not prove other or internet multiplayer setups.

## Reproduce the source and packages

Use project-local paths. Keep generated files below the lab's ignored `.sdvkit` folder. Never use a normal save or your normal or mod-manager `Mods` folder.

```powershell
$sdvkit = 'sdvkit'
$gamePath = '<Stardew Valley directory>'
$lab = '<SDVKit lab root>'
$work = Join-Path $lab '.sdvkit/recipes/per-player-inventory'
$broken = Join-Path $work 'broken'
$fixed = Join-Path $work 'fixed'
$probe = Join-Path $work 'probe'
$evidence = Join-Path $work 'evidence'
New-Item -ItemType Directory -Force $broken,$fixed,$probe,$evidence | Out-Null
Set-Location -LiteralPath $lab
```

Each `project review mcp serve` line below is its own long-running STDIO server. Connect a client as in [Native MCP client setup](mcp.md#client-configuration). Run the review lifecycle commands in a separate PowerShell window whose current folder is `$lab`. Do not paste an MCP server and later lifecycle commands into one blocking terminal.

Copy Appendix A into `broken`, Appendix B into `fixed` and Appendix C into `probe`. The broken and fixed mods share one unique ID, so never stage them together.

```powershell
& $sdvkit project check $broken --json | Tee-Object (Join-Path $evidence 'broken-check.json')
& $sdvkit project build $broken --project PerPlayerInventoryProof.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'broken-build.json')
& $sdvkit project package $broken --project PerPlayerInventoryProof.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'broken-package.json')
if ($LASTEXITCODE -ne 0) { throw 'Broken package failed.' }

& $sdvkit project check $fixed --json | Tee-Object (Join-Path $evidence 'fixed-check.json')
& $sdvkit project build $fixed --project PerPlayerInventoryProof.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'fixed-build.json')
& $sdvkit project package $fixed --project PerPlayerInventoryProof.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'fixed-package.json')
if ($LASTEXITCODE -ne 0) { throw 'Fixed package failed.' }

& $sdvkit project check $probe --json | Tee-Object (Join-Path $evidence 'probe-check.json')
& $sdvkit project build $probe --project PerPlayerProofChestProbe.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'probe-build.json')
& $sdvkit project package $probe --project PerPlayerProofChestProbe.csproj --game-path $gamePath --json | Tee-Object (Join-Path $evidence 'probe-package.json')
if ($LASTEXITCODE -ne 0) { throw 'Probe package failed.' }

$brokenReport = Get-Content (Join-Path $evidence 'broken-package.json') -Raw | ConvertFrom-Json
$fixedReport = Get-Content (Join-Path $evidence 'fixed-package.json') -Raw | ConvertFrom-Json
$probeReport = Get-Content (Join-Path $evidence 'probe-package.json') -Raw | ConvertFrom-Json
$brokenArchive = Join-Path ([IO.Path]::GetFullPath([string]$brokenReport.root)) ([string]$brokenReport.archive)
$fixedArchive = Join-Path ([IO.Path]::GetFullPath([string]$fixedReport.root)) ([string]$fixedReport.archive)
$probeArchive = Join-Path ([IO.Path]::GetFullPath([string]$probeReport.root)) ([string]$probeReport.archive)
$brokenFrozen = Join-Path $evidence 'PerPlayerInventoryProof-broken.zip'
$fixedFrozen = Join-Path $evidence 'PerPlayerInventoryProof-fixed.zip'
$probeFrozen = Join-Path $evidence 'PerPlayerProofChestProbe-v3.zip'
Copy-Item -LiteralPath $brokenArchive -Destination $brokenFrozen
Copy-Item -LiteralPath $fixedArchive -Destination $fixedFrozen
Copy-Item -LiteralPath $probeArchive -Destination $probeFrozen
$brokenExtract = Join-Path $work 'broken-ready'
$fixedExtract = Join-Path $work 'fixed-ready'
$probeExtract = Join-Path $work 'probe-ready'
if ((Test-Path -LiteralPath $brokenExtract) -or
    (Test-Path -LiteralPath $fixedExtract) -or
    (Test-Path -LiteralPath $probeExtract)) {
    throw 'Choose fresh package extraction directories.'
}
Expand-Archive -LiteralPath $brokenFrozen -DestinationPath $brokenExtract
Expand-Archive -LiteralPath $fixedFrozen -DestinationPath $fixedExtract
Expand-Archive -LiteralPath $probeFrozen -DestinationPath $probeExtract
$brokenReady = Join-Path $brokenExtract 'PerPlayerInventoryProof'
$fixedReady = Join-Path $fixedExtract 'PerPlayerInventoryProof'
$probeReady = Join-Path $probeExtract 'PerPlayerProofChestProbe'
Get-FileHash -Algorithm SHA256 -LiteralPath @(
    $brokenFrozen,
    $fixedFrozen,
    $probeFrozen,
    (Join-Path $brokenReady 'manifest.json'),
    (Join-Path $brokenReady 'PerPlayerInventoryProof.dll'),
    (Join-Path $fixedReady 'manifest.json'),
    (Join-Path $fixedReady 'PerPlayerInventoryProof.dll'),
    (Join-Path $probeReady 'manifest.json'),
    (Join-Path $probeReady 'PerPlayerProofChestProbe.dll')) |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'ready-artifact-hashes.json')
```

Check the results:

- Every check, build and package exits `0` and selects one project.
- Each ZIP holds one safe top-level mod folder with the expected manifest and DLL.
- No ZIP holds source, secrets, proprietary assets or machine-local paths.
- Record all archive entries and compute SHA-256 yourself.
- Resolve each archive from its current JSON report. Extract it into its own fresh ignored folder. Review that extracted copy, not the mutable build output.

Hashes of the accepted artifacts are in [Reference details](#reference-details). A copy you rebuild from the appendices can differ through newline or encoding changes. Hash what you built; do not require it to match those hashes.

## Preserve the deliberate failure

Run Appendix A first, with its frozen ready copy and the frozen probe companion:

```powershell
& $sdvkit project review start $brokenReady --game-path $gamePath --topology network-2 --companion $probeReady --json
```

Accepted observation:

- Baseline: host 0, farmhand 0.
- Host F7: host 1, farmhand 0.
- Farmhand F7: host 2, farmhand 0. The host logged a warning that the farmhand's request went to the master.
- Both F8 dialogs showed the host farmer and Stone 2.

Stop and reset before you select Appendix B.

## Corrected network-2 recipe

```powershell
& $sdvkit project review start $fixedReady --game-path $gamePath --topology network-2 --companion $probeReady --json
& $sdvkit project review status --topology network-2 --json
```

Require loaded host and farmhand roles, the target build identity, a reciprocal join and `identityVerified=true`. Then start one STDIO server per role:

```powershell
& $sdvkit project review mcp serve --topology network-2 --role host --allow-input --allow-fixture-actions
& $sdvkit project review mcp serve --topology network-2 --role farmhand --allow-input
```

On each, keep the `initialize` and `tools/list` responses. Call `stardew_runtime_get {}`, `stardew_review_get {}`, `stardew_data_record_get` for `Data/Objects` key `390`, and `stardew_inventory_get {}`. Record the two farmer IDs and baseline counts.

1. Host: call `stardew_input_press {"button":"F7"}` once.
2. Farmhand: make the same call twice, one after the other.
3. Read both inventories. Require host 1 and farmhand 2.
4. Press F8 on each and call `stardew_menu_get {}`. Each dialog's `farmer`, `eventFarmer`, `stone`, `actual` and `eventStone` must match its own role and count.
5. Screenshots are supplementary. The structured inventory and menu data are the primary evidence.

This proves host-authorized, client-local changes, not host-side changes to a remote inventory.

### Leave and rejoin

```powershell
& $sdvkit project review network leave --role farmhand --json
& $sdvkit project review status --topology network-2 --json
```

Require:

- The farmhand is at the title screen and waits to rejoin.
- The host is still loaded and identity-verified, with an unchanged session.
- The old farmhand MCP client is rejected as stale.
- On the host client started earlier, only `stardew_runtime_get` still works. Inventory, world, Data and action calls need the joined pair, so they fail.
- A new host MCP server is refused while the farmhand is away.

```powershell
& $sdvkit project review network join --role farmhand --json
& $sdvkit project review status --topology network-2 --json
```

Require an unchanged host session and a new farmhand session. The old farmhand client stays stale. A fresh `--role farmhand` client reads Stone 2 and the correct persisted dialog.

### Save boundaries

The natural overnight save reached day 2 and wrote host 1 / farmhand 2 inventory and `modData` to disk. No process restart had happened yet, so this counts only as an observation.

For restart proof:

1. Call `stardew_fixture_save {}` from the host MCP client that has fixture actions.
2. Keep the save identity and hash.
3. Stop without reset, then start again against the kept work save.
4. Require new host and farmhand process identities, the same artifact and farmer IDs, and fresh MCP clients.
5. Require inventory 1/2 and F8 `persisted=True` with matching actual and event values.

The issue196-v4 JSON evidence for first roles, grants, distinct players, departure, explicit save, restart roles and restart persistence covers this network scope only.

## Corrected chest probe

Appendix C is the accepted probe v3. It allows a fallback save-ID check only for a real network farmhand, or for a local split-screen client whose farmer carries the matching fixture marker. Its save, fixture, master-player, launch, role/build and unique-game-ID guards stay in place. Earlier probe versions are listed under [Failed candidates retained](#failed-candidates-retained).

The accepted gate:

1. The host prepared one marked chest with Stone 2.
2. The test waited until the farmhand's world read showed that chest.
3. The chest was opened for the selected network farmhand and for the selected local client.
4. Each capture showed the selected farmer, an empty held item, all 12 backpack slots and all 36 chest slots: backpack Stone 0, chest Stone 2. Nothing was transferred.
5. The network farmhand still could not call `stardew_fixture_save`, even with fixture opt-in on its MCP server.
6. Host cleanup removed only the marked chest. Inventories were unchanged.

If you must click, call `stardew_menu_get {}` first. Then call `stardew_input_click` with every required field:

```json
{"x":320,"y":240,"uiRevision":"<fresh revision>","button":"MouseLeft","count":1}
```

Accept this gate only with both topologies, the fixture and role or screen guards, a menu read from the selected player, unchanged inventories and host cleanup.

## Local split-screen

This sequence produced the accepted split-screen result:

```powershell
& $sdvkit project review start $fixedReady --game-path $gamePath --topology single --test-save --companion $probeReady --json
& $sdvkit project review mcp serve --topology single --allow-fixture-actions
& $sdvkit project review split-screen join --json
& $sdvkit project review split-screen status --screen 0 --json
& $sdvkit project review split-screen status --screen 1 --json
```

The [unbound](glossary.md#topology-roles-and-screens) server above holds fixture authority. Check that fixture authority cannot attach to a screen. This startup must be refused, because `--allow-fixture-actions` is not supported with `--screen`:

```powershell
& $sdvkit project review mcp serve --topology single --screen <host-screen-id> --allow-fixture-actions
```

Read the actual screen IDs from status; a replacement is not always screen 1. Start one input client per reported screen:

```powershell
& $sdvkit project review mcp serve --topology single --screen <host-screen-id> --allow-input
& $sdvkit project review mcp serve --topology single --screen <farmhand-screen-id> --allow-input
```

Record `screenId`, `farmerId` and `contextId`. Press F7 once on the host screen and twice on the second screen. Then press F8 on each. Require inventories 1 and 2, and each dialog showing its own farmer and event values. The accepted run produced host Stone 1 and local farmhand Stone 2.

The host screen removes the second screen. Send leave from screen 0, not from the departing screen:

```powershell
& $sdvkit project review split-screen leave --screen 0 --json
```

Require the departed screen's MCP client to become stale, and check the host state. Join again through the host screen. Read status and select the replacement screen that the runtime reports:

```powershell
& $sdvkit project review split-screen join --screen 0 --json
& $sdvkit project review split-screen status --screen 0 --json
& $sdvkit project review split-screen status --screen <reported-replacement-id> --json
```

Read the replacement's farmer and context again. Start a fresh screen-bound client for it, even if a screen number is reused.

Then prove persistence:

1. Call `stardew_fixture_save {}` on the authority MCP client.
2. Stop without reset and restart the kept `single --test-save` review.
3. Rejoin and read the screens again.
4. Require 1/2 and `persisted=True` with matching actual and event values.
5. Run the chest gate for both screens without transfer. Then clean up, stop and reset.

In the accepted run, the old farmhand client failed and the replacement appeared as screen 2 with a new context. Save and restart kept host 1 / farmhand 2, and probe v3 produced the chest capture above. Final cleanup stopped and reset both topologies. All 41,612 protected normal-save, `Mods` and staging entries matched the baseline, with nothing changed, missing or added.

## Failed candidates retained

- v1: host 1, farmhand 0, with "No inventory room" from a host-side remote change.
- v2: the warning disappeared but the farmhand stayed at 0. Silence was not proof.
- v3: farmhand inventory and event reached 1, but the farmhand's persisted state was missing.
- v4: the client changes its own inventory and `modData`, and the host validates the correlated result. Accepted network implementation.
- Probe v1: the network farmhand failed the save-ID guard. It compared the local folder name directly with the review save ID.
- Probe v2: allowed the unique-game-ID fallback for a network farmhand, but still rejected the local split-screen client.
- Probe v3: kept all guards, added the local-farmer fallback and passed both chest gates.
- The first network chest open, before replication, found zero chests. It was kept as a failed attempt.
- Overnight: day 2 disk state was 1/2. Reload was proved only by the later explicit-save, new-process gate.
- A local join sent during loading was refused before dispatch. The later join ran once. A second chest prepare was refused because the marked chest already existed.
- `inputBusy` was rejected before execution. `inputBindingChanged` meant completion was uncertain, so the menu was read before any further action. The open dialogue proved completion, and no blind retry was sent.
- Moving around during the overnight attempt gained Fiber 8 but left Stone 2 unchanged. It was not used as restart proof.
- Later processes overwrite the normal latest-SMAPI-log file. Only native logs copied before each transition count as log evidence. The kept JSONL captures cover the structured calls.

## Cleanup

Close the MCP clients first. Stop and reset only after the matching persistence evidence is complete. Require exited processes, released mounts, a restored fixture baseline, removed network staging, a removed probe chest and unchanged normal saves and `Mods`.

```powershell
& $sdvkit project review stop --topology network-2 --json
& $sdvkit project review reset --topology network-2 --json
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

## Reference details

`SaveLoaded` sets the sample's displayed event fields. Matching event values after a reload prove restored display state, not a fresh `InventoryChanged` event after restart.

SHA-256 of the accepted artifacts, kept as provenance:

| Artifact | Source | DLL | Two-entry ZIP |
| --- | --- | --- | --- |
| Fixed v4 | `62239739d868e1ad60d77f05f00e613f3abd3a5d358bd7133c24a1cebd2e8226` | `d09d2e8072b5f27cb73d67b488d28be2977984c94fbde13caa72da501ad47b79` | `9e3ca8e0e5912c4a28f5483e31ff7af15b26a8ee5a8197f02dde9fdf351ce568` |
| Probe v3 | `a01826ef4e52e41c1f43a27137addea82d1c265e01fd850dba07513e661ec71e` | `0070994589090b6a861a9f0b58b2c2de6f1234bbfa289ac523d6f967a0342243` | `ce592229c6d07659d24398d530377d7b065fbbce0247cc3c2f2cf76687b16e17` |

The acceptance used separate runs. Do not merge their identities or assume one save produced every result:

| Evidence slice | Companion | Accepted result |
| --- | --- | --- |
| Network fixed-v4 state and persistence | Original guarded probe candidate | Host 1/farmhand 2; farmhand leave/rejoin with session rotation and stale-client rejection; explicit save and new-process reload |
| Local fixed-v4 state and persistence | Probe v2 | Host Stone 1 / local farmhand Stone 2 on host screen 0; original screen 1 removed, replacement screen 2 freshly bound; explicit save and new-process reload |
| Fresh network and local chest observation | Probe v3 | The selected clients each saw backpack Stone 0 and chest Stone 2 without transfer; the fresh local farmhand ID was `-3969011337315626278`, distinct from the persistence run's farmer `2954633391945246945` |

## Appendix A: complete broken target

PerPlayerInventoryProof.csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <Version>1.0.0-broken</Version>
    <EnableModDeploy>false</EnableModDeploy>
    <EnableModZip>false</EnableModZip>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>

```

manifest.json:

```json
{
  "Name": "Per-Player Inventory Proof (Broken)",
  "Author": "SDVKitAcceptance",
  "Version": "1.0.0-broken",
  "Description": "Intentional shared-player bug used by the SDVKit multiplayer recipe.",
  "UniqueID": "SDVKitAcceptance.PerPlayerInventoryProof",
  "EntryDll": "PerPlayerInventoryProof.dll",
  "MinimumApiVersion": "4.0.0",
  "UpdateKeys": []
}

```

ModEntry.cs:

```csharp
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

internal sealed class ModEntry : Mod
{
    private const string GrantMessage = "GrantStone";
    private const string StoneId = "(O)390";
    private const string StateKey = "SDVKitAcceptance.PerPlayerInventoryProof/StoneCount";

    private readonly PerScreen<LocalState> _local = new(() => new());

    public override void Entry(IModHelper helper)
    {
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Farmer master = Game1.MasterPlayer;
        _local.Value.LastEventFarmerId = master.UniqueMultiplayerID;
        _local.Value.LastStoneCount = CountStone(master);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.activeClickableMenu is not null)
            return;

        if (e.Button == SButton.F7)
            RequestStone();
        else if (e.Button == SButton.F8)
            ShowResult();
    }

    private void RequestStone()
    {
        long requestedFarmerId = Game1.player.UniqueMultiplayerID;
        if (Context.IsOnHostComputer)
        {
            // Intentional bug: the current screen/player is discarded.
            GrantStone(Game1.MasterPlayer);
            return;
        }

        Helper.Multiplayer.SendMessage(
            new GrantRequest { FarmerId = requestedFarmerId },
            GrantMessage,
            modIDs: new[] { ModManifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer || e.FromModID != ModManifest.UniqueID || e.Type != GrantMessage)
            return;

        GrantRequest request = e.ReadAs<GrantRequest>();
        Monitor.Log(
            $"BROKEN: request from {e.FromPlayerID} for {request.FarmerId} is applied to master {Game1.MasterPlayer.UniqueMultiplayerID}.",
            LogLevel.Warn);
        GrantStone(Game1.MasterPlayer);
    }

    private void GrantStone(Farmer farmer)
    {
        if (!farmer.addItemToInventoryBool(ItemRegistry.Create(StoneId), makeActiveObject: false))
        {
            Monitor.Log($"No inventory room for farmer {farmer.UniqueMultiplayerID}.", LogLevel.Warn);
            return;
        }

        Persist(Game1.MasterPlayer);
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        // Intentional bug: every event is collapsed into one shared master-player value.
        Farmer master = Game1.MasterPlayer;
        int count = CountStone(master);
        if (Context.IsOnHostComputer)
            master.modData[StateKey] = count.ToString(CultureInfo.InvariantCulture);

        if (e.IsLocalPlayer)
        {
            _local.Value.LastEventFarmerId = master.UniqueMultiplayerID;
            _local.Value.LastStoneCount = count;
        }
    }

    private void ShowResult()
    {
        Farmer master = Game1.MasterPlayer;
        bool persisted = TryReadPersisted(master, out int persistedCount);
        string persistedStone = persisted ? persistedCount.ToString(CultureInfo.InvariantCulture) : "missing";
        string text = $"Per-player inventory result: farmer={master.UniqueMultiplayerID}; persisted={persisted}; stone={persistedStone}; actual={CountStone(master)}; eventFarmer={_local.Value.LastEventFarmerId}; eventStone={_local.Value.LastStoneCount}";
        Game1.activeClickableMenu = new DialogueBox(text);
    }

    private static void Persist(Farmer farmer)
    {
        farmer.modData[StateKey] = CountStone(farmer).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryReadPersisted(Farmer farmer, out int count)
    {
        if (farmer.modData.TryGetValue(StateKey, out string? value))
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);

        count = 0;
        return false;
    }

    private static int CountStone(Farmer farmer)
    {
        return farmer.Items.Where(item => item?.QualifiedItemId == StoneId).Sum(item => item!.Stack);
    }

    private sealed class LocalState
    {
        public long LastEventFarmerId { get; set; }

        public int LastStoneCount { get; set; }
    }

    private sealed class GrantRequest
    {
        public long FarmerId { get; set; }
    }
}

```

## Appendix B: complete corrected target

PerPlayerInventoryProof.csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <Version>1.0.0</Version>
    <EnableModDeploy>false</EnableModDeploy>
    <EnableModZip>false</EnableModZip>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>

```

manifest.json:

```json
{
  "Name": "Per-Player Inventory Proof",
  "Author": "SDVKitAcceptance",
  "Version": "1.0.0",
  "Description": "Track and display an exact player's host-authorized Stone inventory result.",
  "UniqueID": "SDVKitAcceptance.PerPlayerInventoryProof",
  "EntryDll": "PerPlayerInventoryProof.dll",
  "MinimumApiVersion": "4.0.0",
  "UpdateKeys": []
}

```

ModEntry.cs:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

internal sealed class ModEntry : Mod
{
    private const string GrantMessage = "GrantStone";
    private const string GrantApprovedMessage = "GrantStoneApproved";
    private const string GrantAppliedMessage = "GrantStoneApplied";
    private const string StoneId = "(O)390";
    private const string StateKey = "SDVKitAcceptance.PerPlayerInventoryProof/StoneCount";

    private readonly PerScreen<LocalState> _local = new(() => new());
    private readonly Dictionary<long, PendingGrant> _pendingRemoteGrants = new();

    public override void Entry(IModHelper helper)
    {
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.Player.InventoryChanged += OnInventoryChanged;
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Farmer farmer = Game1.player;
        _local.Value.LastEventFarmerId = farmer.UniqueMultiplayerID;
        _local.Value.LastStoneCount = CountStone(farmer);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.activeClickableMenu is not null)
            return;

        if (e.Button == SButton.F7)
            RequestStone();
        else if (e.Button == SButton.F8)
            ShowResult(Game1.player);
    }

    private void RequestStone()
    {
        long requestedFarmerId = Game1.player.UniqueMultiplayerID;
        if (Context.IsOnHostComputer)
        {
            GrantStoneLocally(Game1.player);
            return;
        }

        if (_local.Value.PendingRequestId is not null)
            return;

        string requestId = Guid.NewGuid().ToString("N");
        _local.Value.PendingRequestId = requestId;

        Helper.Multiplayer.SendMessage(
            new GrantRequest
            {
                FarmerId = requestedFarmerId,
                RequestId = requestId,
                StoneCount = CountStone(Game1.player),
            },
            GrantMessage,
            modIDs: new[] { ModManifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModManifest.UniqueID)
            return;

        if (Context.IsMainPlayer && e.Type == GrantMessage)
            ApproveRemoteGrant(e);
        else if (!Context.IsMainPlayer && e.Type == GrantApprovedMessage)
            ApplyApprovedGrant(e);
        else if (Context.IsMainPlayer && e.Type == GrantAppliedMessage)
            CompleteRemoteGrant(e);
    }

    private void ApproveRemoteGrant(ModMessageReceivedEventArgs e)
    {
        GrantRequest request = e.ReadAs<GrantRequest>();
        if (request.FarmerId != e.FromPlayerID
            || !Guid.TryParseExact(request.RequestId, "N", out _)
            || request.StoneCount < 0
            || request.StoneCount == int.MaxValue)
        {
            Monitor.Log(
                $"Rejected mismatched grant request: sender={e.FromPlayerID}, requestedFarmer={request.FarmerId}.",
                LogLevel.Warn);
            return;
        }

        Farmer? target = Game1.GetPlayer(request.FarmerId, onlyOnline: true);
        if (target is null || Helper.Multiplayer.GetConnectedPlayer(request.FarmerId) is null)
        {
            Monitor.Log($"Rejected grant request for disconnected farmer {request.FarmerId}.", LogLevel.Warn);
            return;
        }

        if (_pendingRemoteGrants.ContainsKey(request.FarmerId))
        {
            Monitor.Log($"Rejected overlapping grant request for farmer {request.FarmerId}.", LogLevel.Warn);
            return;
        }

        int expectedCount = request.StoneCount + 1;
        _pendingRemoteGrants[request.FarmerId] = new PendingGrant(request.RequestId, expectedCount);
        Helper.Multiplayer.SendMessage(
            new GrantApproval { FarmerId = request.FarmerId, RequestId = request.RequestId },
            GrantApprovedMessage,
            modIDs: new[] { ModManifest.UniqueID },
            playerIDs: new[] { request.FarmerId });
    }

    private void ApplyApprovedGrant(ModMessageReceivedEventArgs e)
    {
        GrantApproval approval = e.ReadAs<GrantApproval>();
        if (e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID
            || approval.FarmerId != Game1.player.UniqueMultiplayerID
            || approval.RequestId != _local.Value.PendingRequestId)
        {
            Monitor.Log("Rejected mismatched host grant approval.", LogLevel.Warn);
            return;
        }

        _local.Value.PendingRequestId = null;
        bool succeeded = GrantStoneLocally(Game1.player);

        Helper.Multiplayer.SendMessage(
            new GrantApplied
            {
                FarmerId = approval.FarmerId,
                RequestId = approval.RequestId,
                Succeeded = succeeded,
                StoneCount = CountStone(Game1.player),
            },
            GrantAppliedMessage,
            modIDs: new[] { ModManifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
    }

    private void CompleteRemoteGrant(ModMessageReceivedEventArgs e)
    {
        GrantApplied applied = e.ReadAs<GrantApplied>();
        if (applied.FarmerId != e.FromPlayerID
            || !_pendingRemoteGrants.TryGetValue(applied.FarmerId, out PendingGrant pending)
            || pending.RequestId != applied.RequestId
            || applied.StoneCount != (applied.Succeeded ? pending.ExpectedCount : pending.ExpectedCount - 1))
        {
            Monitor.Log($"Rejected mismatched applied grant from farmer {e.FromPlayerID}.", LogLevel.Warn);
            return;
        }

        Farmer? target = Game1.GetPlayer(applied.FarmerId, onlyOnline: true);
        if (target is null || Helper.Multiplayer.GetConnectedPlayer(applied.FarmerId) is null)
        {
            Monitor.Log($"Rejected applied grant for disconnected farmer {applied.FarmerId}.", LogLevel.Warn);
            return;
        }

        _pendingRemoteGrants.Remove(applied.FarmerId);
    }

    private bool GrantStoneLocally(Farmer farmer)
    {
        if (!farmer.addItemToInventoryBool(ItemRegistry.Create(StoneId), makeActiveObject: false))
        {
            Monitor.Log($"No inventory room for farmer {farmer.UniqueMultiplayerID}.", LogLevel.Warn);
            return false;
        }

        Persist(farmer);
        return true;
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        int count = CountStone(e.Player);
        if (e.IsLocalPlayer)
            e.Player.modData[StateKey] = count.ToString(CultureInfo.InvariantCulture);

        if (e.IsLocalPlayer && Game1.player.UniqueMultiplayerID == e.Player.UniqueMultiplayerID)
        {
            _local.Value.LastEventFarmerId = e.Player.UniqueMultiplayerID;
            _local.Value.LastStoneCount = count;
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        _pendingRemoteGrants.Remove(e.Peer.PlayerID);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _pendingRemoteGrants.Clear();
        _local.Value.PendingRequestId = null;
    }

    private void ShowResult(Farmer farmer)
    {
        bool persisted = TryReadPersisted(farmer, out int persistedCount);
        string persistedStone = persisted ? persistedCount.ToString(CultureInfo.InvariantCulture) : "missing";
        string text = $"Per-player inventory result: farmer={farmer.UniqueMultiplayerID}; persisted={persisted}; stone={persistedStone}; actual={CountStone(farmer)}; eventFarmer={_local.Value.LastEventFarmerId}; eventStone={_local.Value.LastStoneCount}";
        Game1.activeClickableMenu = new DialogueBox(text);
    }

    private static void Persist(Farmer farmer)
    {
        farmer.modData[StateKey] = CountStone(farmer).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryReadPersisted(Farmer farmer, out int count)
    {
        if (farmer.modData.TryGetValue(StateKey, out string? value))
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);

        count = 0;
        return false;
    }

    private static int CountStone(Farmer farmer)
    {
        return farmer.Items.Where(item => item?.QualifiedItemId == StoneId).Sum(item => item!.Stack);
    }

    private sealed class LocalState
    {
        public long LastEventFarmerId { get; set; }

        public int LastStoneCount { get; set; }

        public string? PendingRequestId { get; set; }
    }

    private sealed class GrantRequest
    {
        public long FarmerId { get; set; }

        public string RequestId { get; set; } = string.Empty;

        public int StoneCount { get; set; }
    }

    private sealed class GrantApproval
    {
        public long FarmerId { get; set; }

        public string RequestId { get; set; } = string.Empty;
    }

    private sealed class GrantApplied
    {
        public long FarmerId { get; set; }

        public string RequestId { get; set; } = string.Empty;

        public bool Succeeded { get; set; }

        public int StoneCount { get; set; }
    }

    private readonly record struct PendingGrant(string RequestId, int ExpectedCount);
}

```

## Appendix C: complete corrected probe

PerPlayerProofChestProbe.csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <Version>1.0.0</Version>
    <EnableModDeploy>false</EnableModDeploy>
    <EnableModZip>false</EnableModZip>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>

```

manifest.json:

```json
{
  "Name": "Per-Player Proof Chest Probe",
  "Author": "SDVKitAcceptance",
  "Version": "1.0.0",
  "Description": "Bounded disposable chest setup for selected-client observation proof.",
  "UniqueID": "SDVKitAcceptance.PerPlayerProofChestProbe",
  "EntryDll": "PerPlayerProofChestProbe.dll",
  "MinimumApiVersion": "4.0.0",
  "UpdateKeys": []
}

```

ModEntry.cs:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

internal sealed class ModEntry : Mod
{
    private const string CommandName = "per-player-proof-chest";
    private const string MarkerKey = "SDVKitAcceptance.PerPlayerProofChestProbe/Owned";
    private const string MarkerValue = "issue-196";
    private const string StoneId = "(O)390";
    private const string FixtureMarkerKey = "SDVKit/FixtureId";
    private const string NetworkBuildMarkerKey = "SDVKit/NetworkTwoBuild";
    private const string NetworkRoleMarkerKey = "SDVKit/NetworkTwoRole";
    private const string LocalReviewFarmerMarkerKey = "SDVKit/LocalReviewFarmer";

    private static readonly IReadOnlyList<Point> CandidateOffsets = new[]
    {
        new Point(1, 0),
        new Point(-1, 0),
        new Point(0, 1),
        new Point(0, -1),
        new Point(2, 0),
        new Point(-2, 0),
        new Point(0, 2),
        new Point(0, -2),
    };

    public override void Entry(IModHelper helper)
    {
        helper.ConsoleCommands.Add(
            CommandName,
            "Disposable proof chest: per-player-proof-chest prepare|open|cleanup",
            OnCommand);
    }

    private void OnCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
            throw new InvalidOperationException("The proof chest requires a world-ready local player.");
        RequireExactOwnedFixture();
        if (Game1.activeClickableMenu is not null)
            throw new InvalidOperationException("Close the current menu before using the proof chest command.");
        if (args.Length != 1 || args[0] is not ("prepare" or "open" or "cleanup"))
            throw new InvalidOperationException("Usage: per-player-proof-chest prepare|open|cleanup");

        switch (args[0])
        {
            case "prepare":
                Prepare();
                break;
            case "open":
                Open();
                break;
            case "cleanup":
                Cleanup();
                break;
        }
    }

    private void Prepare()
    {
        string role = ReadEnvironment("SDVKIT_NETWORK_TWO_ROLE");
        if (!Context.IsMainPlayer || (role.Length != 0 && role != "host"))
            throw new InvalidOperationException("Only the main host may prepare the proof chest.");

        GameLocation location = Game1.currentLocation;
        if (FindOwned(location).Count != 0)
            throw new InvalidOperationException("The current location already contains an owned proof chest.");

        Point origin = Game1.player.TilePoint;
        foreach (Point offset in CandidateOffsets)
        {
            Vector2 tile = new(origin.X + offset.X, origin.Y + offset.Y);
            if (!location.CanItemBePlacedHere(tile))
                continue;

            Chest chest = new(playerChest: true, tile);
            chest.Location = location;
            chest.TileLocation = tile;
            chest.modData[MarkerKey] = MarkerValue;
            chest.Items.Add(ItemRegistry.Create(StoneId, amount: 2));
            location.Objects.Add(tile, chest);
            Monitor.Log($"Prepared owned proof chest at {location.NameOrUniqueName} ({tile.X}, {tile.Y}) with Stone 2.", LogLevel.Info);
            return;
        }

        throw new InvalidOperationException("No clear adjacent tile was available for the proof chest.");
    }

    private void Open()
    {
        List<(Vector2 Tile, Chest Chest)> matches = FindOwned(Game1.currentLocation);
        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected exactly one owned proof chest in the current location; found {matches.Count}.");

        matches[0].Chest.ShowMenu();
        Monitor.Log($"Opened owned proof chest for farmer {Game1.player.UniqueMultiplayerID}.", LogLevel.Info);
    }

    private void Cleanup()
    {
        string role = ReadEnvironment("SDVKIT_NETWORK_TWO_ROLE");
        if (!Context.IsMainPlayer || (role.Length != 0 && role != "host"))
            throw new InvalidOperationException("Only the main host may clean up the proof chest.");

        List<(Vector2 Tile, Chest Chest)> matches = FindOwned(Game1.currentLocation);
        if (matches.Count > 1)
            throw new InvalidOperationException($"Refusing ambiguous cleanup of {matches.Count} owned proof chests.");

        if (matches.Count == 0)
        {
            Monitor.Log("No owned proof chest is present in the current location.", LogLevel.Info);
            return;
        }

        if (!Game1.currentLocation.Objects.Remove(matches[0].Tile))
            throw new InvalidOperationException("The exact owned proof chest could not be removed.");

        Monitor.Log("Removed exactly one owned proof chest.", LogLevel.Info);
    }

    private static List<(Vector2 Tile, Chest Chest)> FindOwned(GameLocation location)
    {
        return location.Objects.Pairs
            .Where(pair => pair.Value is Chest chest
                && chest.modData.TryGetValue(MarkerKey, out string? marker)
                && marker == MarkerValue)
            .Select(pair => (pair.Key, (Chest)pair.Value))
            .Take(2)
            .ToList();
    }

    private static void RequireExactOwnedFixture()
    {
        if (ReadEnvironment("SDVKIT_PROJECT_REVIEW") != "1"
            || ReadEnvironment("SDVKIT_LAB_LAUNCH_ID").Length == 0)
        {
            throw new InvalidOperationException("The proof chest requires an exact owned project review launch.");
        }

        string role = ReadEnvironment("SDVKIT_NETWORK_TWO_ROLE");
        bool network = role.Length != 0;
        if (network && role is not ("host" or "farmhand"))
            throw new InvalidOperationException("The proof chest received an invalid network role.");

        string fixtureId = ReadEnvironment(network
            ? "SDVKIT_NETWORK_TWO_FIXTURE_ID"
            : "SDVKIT_TEST_SAVE_FIXTURE_ID");
        string saveId = ReadEnvironment(network
            ? "SDVKIT_NETWORK_TWO_SAVE_ID"
            : "SDVKIT_TEST_SAVE_ID");
        bool allowDerivedSaveIdentity = role == "farmhand"
            || !network
                && Context.IsSplitScreen
                && Game1.IsClient
                && HasMarker(Game1.player, LocalReviewFarmerMarkerKey, fixtureId);
        if (fixtureId.Length == 0 || saveId.Length == 0
            || !MatchesReviewSaveIdentity(saveId, allowDerivedSaveIdentity)
            || !HasMarker(Game1.player, FixtureMarkerKey, fixtureId)
            || !HasMarker(Game1.MasterPlayer, FixtureMarkerKey, fixtureId))
        {
            throw new InvalidOperationException("The current player/save does not match the exact disposable fixture.");
        }

        if (network)
        {
            string buildId = ReadEnvironment("SDVKIT_NETWORK_TWO_BUILD_ID");
            if (buildId.Length == 0
                || !HasMarker(Game1.player, NetworkBuildMarkerKey, buildId)
                || !HasMarker(Game1.player, NetworkRoleMarkerKey, role))
            {
                throw new InvalidOperationException("The current player does not match the exact network role/build binding.");
            }
        }
        else
        {
            if (ReadEnvironment("SDVKIT_TEST_SAVE_MODE").Length == 0
                || !ulong.TryParse(
                    ReadEnvironment("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out ulong expectedGameId)
                || Game1.uniqueIDForThisGame != expectedGameId)
            {
                throw new InvalidOperationException("The current single review does not match the exact test-save identity.");
            }
        }
    }

    private static bool MatchesReviewSaveIdentity(string expectedSaveId, bool allowDerivedIdentity)
    {
        if (string.IsNullOrWhiteSpace(Constants.SaveFolderName))
            return false;

        if (string.Equals(Constants.SaveFolderName, expectedSaveId, StringComparison.Ordinal))
            return true;

        return allowDerivedIdentity
            && Game1.uniqueIDForThisGame is > 0 and <= long.MaxValue
            && string.Equals(
                $"SDVKit_{Game1.uniqueIDForThisGame}",
                expectedSaveId,
                StringComparison.Ordinal);
    }

    private static bool HasMarker(Farmer farmer, string key, string expected)
    {
        return farmer.modData.TryGetValue(key, out string? actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static string ReadEnvironment(string name)
    {
        return Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;
    }
}
```

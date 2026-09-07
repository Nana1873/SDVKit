# Author and verify one shop purchase

Build **Shop Purchase Proof**, a small SMAPI mod which inserts one ordinary
Stone offer at the start of vanilla `Data/Shops` `SeedShop`: one `(O)390` per
purchase, 10 Gold, finite daily stock 3. Package the corrected source once,
extract that exact ZIP into two isolated ready targets, diagnose an invalid shop
configuration, correct it, buy once through bounded process-local input, and
compare actual offer, money, inventory, held-item and stock observations.

> **Acceptance status:** the functional recipe was observed on 2026-09-07 for
> [#160](https://github.com/Nana1873/SDVKit/issues/160): the same packaged DLL
> diagnosed the broken config, then the corrected config produced 500 to 490
> Gold, stock 3 to 2, one held Stone and finally one Stone in empty inventory
> slot 5. Exact stop/reset and the comparison of all 20,292 protected entries
> passed. The issue retains the full identities, failures and integration evidence.
> The shop observation capability was implemented in
> [#159](https://github.com/Nana1873/SDVKit/issues/159).

This recipe targets **Stardew Valley 1.6.15 build 24356** and **SMAPI 4.5.2**.
The supported evidence contract is the exact vanilla `ShopMenu` for
`ShopId=SeedShop`, Gold currency and an ordinary stack-one object offer. Read
[shop inspection](shop-inspection.md) before extending that boundary. Barter,
non-Gold currency, callbacks, custom price logic, custom shop menus, recipes,
special objects and inferred purchase outcomes stay unavailable.

The commands below use the CLI. Recorded acceptance used CLI lifecycle and
captures together with native MCP `stardew_shop_get`, `stardew_menu_get`,
diagnostics and opt-in `stardew_input_click` for the two completed clicks.
The CLI console equivalents below deliver to the same input adapter; require
the observed effect after delivery before continuing. Screenshots support the typed proof; they do
not replace it.

## Select the installation and lab

Use the [installation requirements](../README.md#requirements), one explicit
ready game/SMAPI installation, and the same lab directory for every live
command. Keep generated source, builds, ready artifacts, logs, screenshots and
evidence below that lab's ignored `.sdvkit`; never deploy to a normal or
mod-manager-owned `Mods` directory.

```powershell
$lab = $PWD.Path
$mod = Join-Path $lab '.sdvkit\shop-authoring\ShopPurchaseProof'
$evidence = Join-Path $lab '.sdvkit\shop-authoring\evidence'
$project = 'ShopPurchaseProof.csproj'
New-Item -ItemType Directory -Force $evidence | Out-Null

& $sdvkit doctor --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'doctor.json')
& $sdvkit project create smapi-mod $mod --name 'Shop Purchase Proof' `
    --author ExampleAuthor --unique-id ExampleAuthor.ShopPurchaseProof `
    --description 'Add one bounded Stone offer for purchase verification.' --json |
    Tee-Object (Join-Path $evidence 'create.json')
```

Set `$gamePath` to the intended installation before running the command. Require
one ready installation and retain the exact SDVKit commit or ZIP identity,
Stardew and SMAPI versions, lab path and owner. Published-package users should
follow the current [installation](../README.md#install); contributors should
produce and verify the candidate through [Contributing](../CONTRIBUTING.md).

## Write the original mod

Replace the generated files with these three exact files. They are the complete
original mod source.
`AssetRequested` adds the controlled offer, while the console command opens only
the configured existing shop. The explicit Data lookup makes the deliberate
typo an actionable selected-mod error without depending on unknown-shop behavior.

`ShopPurchaseProof.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <Version>1.0.0</Version>
    <EnableModDeploy>false</EnableModDeploy>
    <EnableModZip>false</EnableModZip>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
  </ItemGroup>
</Project>
```

`manifest.json`:

```json
{
  "Name": "Shop Purchase Proof",
  "Author": "ExampleAuthor",
  "Version": "1.0.0",
  "Description": "Add one bounded Stone offer to Pierre's shop for purchase verification.",
  "UniqueID": "ExampleAuthor.ShopPurchaseProof",
  "EntryDll": "ShopPurchaseProof.dll",
  "MinimumApiVersion": "4.0.0",
  "UpdateKeys": []
}
```

`ModEntry.cs`:

```csharp
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Shops;

namespace ShopPurchaseProof;

public sealed class ModConfig
{
    public string ShopId { get; set; } = "SeedShop";
}

public sealed class ModEntry : Mod
{
    private const string OfferId = "ExampleAuthor.ShopPurchaseProof.Stone";
    private ModConfig Config = new();

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.ConsoleCommands.Add(
            "shop_purchase_proof_open",
            "Open the configured vanilla shop for the isolated purchase proof.",
            OnOpenShop);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo("Data/Shops"))
            return;

        e.Edit(asset =>
        {
            IDictionary<string, ShopData> shops = asset.AsDictionary<string, ShopData>().Data;
            if (!shops.TryGetValue("SeedShop", out ShopData? shop))
            {
                Monitor.Log("Shop Purchase Proof could not find Data/Shops entry 'SeedShop'.", LogLevel.Error);
                return;
            }

            shop.Items.Insert(0, new ShopItemData
            {
                Id = OfferId,
                ItemId = "(O)390",
                Price = 10,
                AvailableStock = 3,
                IgnoreShopPriceModifiers = true,
            });
        });
    }

    private void OnOpenShop(string command, string[] args)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
        {
            Monitor.Log("Shop Purchase Proof requires a ready single-player world.", LogLevel.Error);
            return;
        }

        if (Game1.activeClickableMenu is not null)
        {
            Monitor.Log("Shop Purchase Proof requires no active menu before opening the shop.", LogLevel.Error);
            return;
        }

        Dictionary<string, ShopData> shops = Game1.content.Load<Dictionary<string, ShopData>>("Data/Shops");
        if (!shops.ContainsKey(Config.ShopId))
        {
            Monitor.Log($"Shop Purchase Proof could not open configured shop '{Config.ShopId}': Data/Shops has no matching entry.", LogLevel.Error);
            return;
        }

        if (!Utility.TryOpenShopMenu(Config.ShopId, ownerName: null, playOpenSound: true))
        {
            Monitor.Log($"Shop Purchase Proof could not open configured shop '{Config.ShopId}'.", LogLevel.Error);
            return;
        }

        Monitor.Log($"Shop Purchase Proof opened configured shop '{Config.ShopId}'.", LogLevel.Info);
    }
}
```

The first retained live preparation exposed why the item-level
`IgnoreShopPriceModifiers` is necessary: the authored `Data/Shops` entry had
raw `Price=10`, while the opened SeedShop materialized 20 Gold through its
existing `DefaultMarkup` amount 2. Setting the flag on this one offer preserves
every other SeedShop price and produces the intended materialized 10 Gold. That
failed-price run was stopped and reset exactly; it is diagnosis evidence, not a
successful purchase acceptance.

## Check, build and package once

```powershell
& $sdvkit project inspect $mod --json |
    Tee-Object (Join-Path $evidence 'inspect.json')
& $sdvkit project check $mod --json |
    Tee-Object (Join-Path $evidence 'check.json')
& $sdvkit project build $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'build.json')
& $sdvkit project package $mod --project $project --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'package.json')
```

Require a recognized standalone SMAPI mod, check exit 0, and a successful
isolated build and package. `project check` validates supported authoring files;
it does not compile C# or prove an in-game purchase. Parse the package result
instead of assuming its filename:

```powershell
$package = Get-Content (Join-Path $evidence 'package.json') -Raw | ConvertFrom-Json
$zip = (Resolve-Path (Join-Path $mod $package.archive)).Path
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
```

Require the returned entries to contain exactly the mod's `manifest.json` and
DLL. The package intentionally omits `config.json`; the corrected `SeedShop`
value is the compiled default.

## Prepare exact broken and corrected artifacts

Extract the unchanged ZIP twice into fresh ignored directories. Add only the
configuration variant to each copy:

```powershell
$brokenRoot = Join-Path $lab '.sdvkit\shop-authoring\ready-broken'
$fixedRoot = Join-Path $lab '.sdvkit\shop-authoring\ready-fixed'
Expand-Archive -LiteralPath $zip -DestinationPath $brokenRoot
Expand-Archive -LiteralPath $zip -DestinationPath $fixedRoot
$broken = Join-Path $brokenRoot 'ShopPurchaseProof'
$fixed = Join-Path $fixedRoot 'ShopPurchaseProof'

$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText(
    (Join-Path $broken 'config.json'),
    "{`r`n  `"ShopId`": `"SeedShpo`"`r`n}",
    $utf8NoBom)
[IO.File]::WriteAllText(
    (Join-Path $fixed 'config.json'),
    "{`r`n  `"ShopId`": `"SeedShop`"`r`n}",
    $utf8NoBom)

& $sdvkit project inspect $broken --json |
    Tee-Object (Join-Path $evidence 'inspect-ready-broken.json')
& $sdvkit project inspect $fixed --json |
    Tee-Object (Join-Path $evidence 'inspect-ready-fixed.json')
```

Compare both manifest and DLL hashes with each other and with the two exact ZIP
entries. Record the expected config difference separately. Each ready artifact
has no C# project, so its review start below deliberately omits `--project` and
must preserve the supplied bytes without rebuilding.

SMAPI rewrites this config as UTF-8 without BOM, Windows CRLF and no trailing
newline. The explicit writer above creates those exact bytes. A one-line file,
LF endings or a trailing newline can be semantically valid yet be normalized on
startup, correctly causing `reviewStagingOwnershipDrifted`. If that happens,
retain the failed preparation evidence, stop and reset, normalize only the
ignored ready copy while no review is active, then start a new review. Do not
weaken staging identity checks or edit an active staged artifact.

## Protect normal paths and prepare the fixture

Identify the exclusive lab owner. Require both topologies stopped. Fingerprint
the selected normal game `Mods`, any distinct mod-manager-owned `Mods`, and
`$env:APPDATA/StardewValley` read-only before and after the workflow, without
following reparse points. Retain sorted relative path, entry type and attributes,
file length, `LastWriteTimeUtc` and SHA-256 for regular files. Follow the
[live-review preparation](live-review.md#prepare-the-lab); never write to those
roots.

```powershell
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'preflight-single.json')
& $sdvkit project review status --topology network-2 --json |
    Tee-Object (Join-Path $evidence 'preflight-network.json')
& $sdvkit lab test-save --topology single --game-path $gamePath --json |
    Tee-Object (Join-Path $evidence 'baseline.json')
```

Create the disposable baseline only if it is absent and all roles are stopped.

## Diagnose the deliberate configuration failure

```powershell
& $sdvkit project review start $broken --game-path $gamePath --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-broken.json')
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'status-broken.json')
& $sdvkit project review command 'shop_purchase_proof_open' --topology single --json |
    Tee-Object (Join-Path $evidence 'command-broken.json')
& $sdvkit project review diagnostics --mod ExampleAuthor.ShopPurchaseProof --limit 20 --topology single --json |
    Tee-Object (Join-Path $evidence 'diagnostics-broken.json')
& $sdvkit project review menu --topology single --json |
    Tee-Object (Join-Path $evidence 'menu-broken.json')
```

Before sending the command, require the exact target ID/version/build, loaded
state, process/launch identity and verified disposable fixture. The command
receipt proves delivery only. Diagnosis must contain this selected-mod error:

```text
Shop Purchase Proof could not open configured shop 'SeedShpo': Data/Shops has no matching entry.
```

Require the menu observation to report no open menu. Preserve the complete owned
SMAPI log before cleanup. An offline check or build cannot replace this runtime
diagnosis.

```powershell
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-broken.json')
& $sdvkit project review reset --topology single --json |
    Tee-Object (Join-Path $evidence 'reset-broken.json')
```

Require exact exit, staging removal and fixture reset before starting the fixed
phase. Config is read in `Entry`, so it requires this fresh process; do not hot
reload or alter staged files.

## Open the corrected shop and capture the baseline

```powershell
& $sdvkit project review start $fixed --game-path $gamePath --topology single --test-save --json |
    Tee-Object (Join-Path $evidence 'start-fixed.json')
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'status-fixed.json')
& $sdvkit project review command 'shop_purchase_proof_open' --topology single --json |
    Tee-Object (Join-Path $evidence 'command-fixed.json')
& $sdvkit project review shop --topology single --json |
    Tee-Object (Join-Path $evidence 'shop-before.json')
& $sdvkit project review menu --topology single --json |
    Tee-Object (Join-Path $evidence 'menu-before.json')
```

Require the same packaged manifest/DLL bytes as the broken phase, the intentional
config-only difference, a new exact launch, loaded target, ready fixture and no
matching selected-mod error. `shop-before.json` must report:

- `state=ready`, `shopId=SeedShop`, `currency=Gold`, `scrollIndex=0`;
- offer index 0 available as `(O)390`, stack 1, unchanged quality, price 10,
  finite stock 3 and `unlimitedStock=false`;
- one identity scope and player ID, current money, all inventory slots and
  `heldItem=null`;
- at least one empty inventory slot, so the purchased Stone can be collected.

Require an exact vanilla `shopMenu` node in `menu-before.json`, matching scroll
index 0, plus a fresh `uiRevision`. Select the top visible `saleRow` component
by its smallest screen `bounds.y`; offer index 0 was deliberately inserted first.
Use the center of that observed rectangle. Component IDs themselves are not
offer IDs.

## Buy once and prove the deltas

Send one bounded process-local click through the existing review console path:

```powershell
$menuBefore = Get-Content (Join-Path $evidence 'menu-before.json') -Raw | ConvertFrom-Json
$shopMenu = @($menuBefore.menus | Where-Object adapter -eq 'shopMenu')
if ($shopMenu.Count -ne 1 -or $shopMenu[0].scrollIndex -ne 0) {
    throw 'Expected one unscrolled exact shop menu.'
}
$saleRows = @($shopMenu[0].components |
    Where-Object { $_.kind -eq 'saleRow' -and $_.visibleFlag -and $_.intersectsViewport } |
    Sort-Object { $_.bounds.y })
if ($saleRows.Count -lt 1) {
    throw 'No visible sale row was observed.'
}
$saleRow = $saleRows[0]
$x = $saleRow.bounds.x + [int]($saleRow.bounds.width / 2)
$y = $saleRow.bounds.y + [int]($saleRow.bounds.height / 2)
$click = "sdvkit input click $x $y MouseLeft 1 $($menuBefore.uiRevision)"
& $sdvkit project review command $click --topology single --json |
    Tee-Object (Join-Path $evidence 'purchase-click.json')
& $sdvkit project review shop --topology single --json |
    Tee-Object (Join-Path $evidence 'shop-after-purchase.json')
```

Do not send another action until the fresh shop capture establishes what happened.
An acknowledgement alone is not purchase proof. For a timeout or
`mayHaveRun=true`, inspect current shop state before deciding whether any repeat
is safe.

Compare only captures with the same launch ID, shop identity scope and player
ID. Require money to decrease by exactly 10, offer 0 stock to change from 3 to 2,
and total `(O)390` quantity across inventory plus `heldItem` to increase by
exactly 1. For the normal first click, require the new Stone as the held item;
do not claim an inventory increase yet.

Place the held Stone into an observed empty inventory slot. Match the typed
zero-based slot to the inventory component's public controller ID, and fail
closed unless exactly one visible component matches:

```powershell
& $sdvkit project review menu --topology single --json |
    Tee-Object (Join-Path $evidence 'menu-held.json')
$shopAfter = Get-Content (Join-Path $evidence 'shop-after-purchase.json') -Raw |
    ConvertFrom-Json
$menuHeld = Get-Content (Join-Path $evidence 'menu-held.json') -Raw | ConvertFrom-Json
$emptySlots = @($shopAfter.data.inventory | Where-Object { $null -eq $_.item })
if ($emptySlots.Count -lt 1) {
    throw 'No empty inventory slot is available.'
}
$emptySlot = $emptySlots[0].slot
$slotComponents = @($menuHeld.menus.components |
    Where-Object {
        $_.kind -eq 'inventorySlot' -and
        $_.controllerId -eq $emptySlot -and
        $_.visibleFlag -and
        $_.intersectsViewport
    })
if ($slotComponents.Count -ne 1) {
    throw 'The empty inventory slot does not have one visible matching component.'
}
$slot = $slotComponents[0]
$slotX = $slot.bounds.x + [int]($slot.bounds.width / 2)
$slotY = $slot.bounds.y + [int]($slot.bounds.height / 2)
$place = "sdvkit input click $slotX $slotY MouseLeft 1 $($menuHeld.uiRevision)"
& $sdvkit project review command $place --topology single --json |
    Tee-Object (Join-Path $evidence 'place-held.json')
& $sdvkit project review shop --topology single --json |
    Tee-Object (Join-Path $evidence 'shop-final.json')
```

Do not send another action before reading the final state. Require the same
launch/player, money still exactly 10 below baseline, stock still 2,
`heldItem=null`, and matching inventory quantity exactly 1 above the pre-purchase
inventory. Retain labelled viewport screenshots before the purchase and after
final placement as supporting evidence. Separate physical-pointer samples were
not retained for the recorded purchase, so it does not add a new proof of
physical-cursor invariance. Input implementation changes require the broader
observations in the release matrix. Use only SDVKit's
process-local input; never move the physical pointer or activate the game window.

## Finish and retain evidence

Preserve final status, shop/menu captures, diagnostics, screenshots and the full
owned log before cleanup:

```powershell
& $sdvkit project review stop --topology single --json |
    Tee-Object (Join-Path $evidence 'stop-fixed.json')
& $sdvkit project review reset --topology single --json |
    Tee-Object (Join-Path $evidence 'reset-fixed.json')
& $sdvkit project review status --topology single --json |
    Tee-Object (Join-Path $evidence 'final-status.json')
```

Require exact process exit, restored fixture baseline, removed staging, mount,
mailbox and ownership state, and an explained protected-path comparison. Recheck
the original ZIP SHA-256 and both ready artifacts' manifest/DLL equality after
cleanup. Do not rebuild or repackage after accepting the exact packaged bytes.

Record elapsed phases, manual interventions, exact artifact/runtime/launch
identities, expected and actual deltas, failed or inconclusive attempts and
evidence paths in a simple table. Keep these claims separate:

1. project check, build and package passed;
2. the selected artifact loaded;
3. the deliberate runtime failure was diagnosed;
4. the corrected shop offer was observed;
5. one purchase and its money, stock and inventory deltas were observed;
6. exact cleanup and protected-path checks passed.

The #159 candidate also needs its complete release checks: restore, formatting,
Release build, all tests, product packaging, fresh portable verification,
extracted game-backed AlwaysOn build and the affected single shop/input/cleanup
gate. Follow [releasing](releasing.md). Map and texture MCP work from #155 is not
required by this workflow; add it only if a later scope explicitly requires map
or texture asset evidence.

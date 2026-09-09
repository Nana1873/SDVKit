# Author an inventory reward and prove chest persistence

Build **Inventory Reward Recipe**, a small SMAPI mod that grants ten Stone once
per save. Diagnose a deliberately packaged quantity of twelve from the selected
mod warning and observed backpack, correct the package, move exact quantities
through a native chest menu, then save and restart the same owned world. The
final proof is unchanged player/chest state after reload, not a successful
build, command acknowledgement, or log line.

This recipe targets Stardew Valley 1.6.15 and SMAPI 4.5.2. It reuses the
[toolkit](toolkit.md), [owned review lifecycle](live-review.md),
[inventory](inventory-inspection.md), [container](container-inspection.md), and
[transfer](container-transfer.md) contracts. It supports only an owned
single-player disposable test save and never touches normal saves or Mods.

## Select the exact toolkit and workspace

Use the Windows-x64 ZIP from one green exact commit. Verify and extract it with
the [portable procedure](releasing.md#verify-the-extracted-package), build its
included AlwaysOn source against the selected game, and keep that ZIP unchanged.
Keep one lab directory as owner for the complete exercise:

```powershell
$packageRoot = '<fresh external SDVKit package directory reported by the verifier>'
$sdvkit = Join-Path $packageRoot 'sdvkit.exe'
$lab = $PWD.Path
$recipe = Join-Path $lab '.sdvkit\inventory-container-recipe'
$mod = Join-Path $recipe 'InventoryRewardRecipe'
$harness = Join-Path $recipe 'InventoryRewardHarness'
$evidence = Join-Path $recipe 'evidence'
New-Item -ItemType Directory -Force $evidence | Out-Null

$doctor = & $sdvkit doctor --json | Tee-Object (Join-Path $evidence 'doctor.json') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $doctor.status -ne 'ready' -or @($doctor.installations).Count -ne 1) {
    throw 'Select exactly one complete Stardew Valley and SMAPI installation.'
}
$gamePath = $doctor.installations[0].gamePath
& $sdvkit doctor --game-path $gamePath --json
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Record the toolkit commit and ZIP SHA-256, extracted path, game/SMAPI versions,
start time, and current lab owner. Wait if another task owns either topology.
Capture the documented protected-path baseline before the first live start.

## Create the one-time reward

Create one standalone C# mod, then replace the generated source. Do not run
`project create` over existing files.

```powershell
& $sdvkit project create smapi-mod $mod `
  --name 'Inventory Reward Recipe' --author SDVKit `
  --unique-id SDVKit.InventoryRewardRecipe `
  --description 'Award one configured ordinary item stack once in an owned disposable save.' --json
```

Use this `ModEntry.cs`:

```csharp
using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace InventoryRewardRecipe;

internal sealed class RewardDefinition
{
    public int RewardQuantity { get; set; }
}

internal sealed class ModEntry : Mod
{
    private const string RewardItemId = "(O)390";
    private const int ExpectedRecipeQuantity = 10;
    private const string GrantedKey = "SDVKit.InventoryRewardRecipe/RewardGranted";
    private RewardDefinition Reward = new();

    public override void Entry(IModHelper helper)
    {
        Reward = helper.Data.ReadJsonFile<RewardDefinition>("assets/reward.json")
            ?? throw new InvalidOperationException("Missing assets/reward.json.");
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
            return;
        if (Reward.RewardQuantity is < 1 or > 99)
        {
            Monitor.Log($"RewardQuantity must be between 1 and 99; got {Reward.RewardQuantity}.", LogLevel.Error);
            return;
        }
        if (Game1.player.modData.ContainsKey(GrantedKey))
        {
            Monitor.Log("The one-time inventory reward was already granted in this save.", LogLevel.Trace);
            return;
        }
        if (Reward.RewardQuantity != ExpectedRecipeQuantity)
            Monitor.Log($"Configured reward quantity {Reward.RewardQuantity} differs from the recipe expectation {ExpectedRecipeQuantity}.", LogLevel.Warn);

        Item reward = ItemRegistry.Create(RewardItemId, Reward.RewardQuantity);
        if (!Game1.player.couldInventoryAcceptThisItem(reward))
        {
            Monitor.Log("The complete one-time inventory reward does not fit in the backpack; nothing was added.", LogLevel.Warn);
            return;
        }
        if (!Game1.player.addItemToInventoryBool(reward))
        {
            Monitor.Log("The preflighted one-time inventory reward was not accepted; it was not marked as granted.", LogLevel.Error);
            return;
        }
        Game1.player.modData[GrantedKey] = "true";
        Monitor.Log($"Granted {Reward.RewardQuantity} Stone once for this save.", LogLevel.Info);
    }
}
```

Add the following to the project so the build contains the data file while
automatic deployment stays disabled:

```xml
<PropertyGroup>
  <EnableModDeploy>false</EnableModDeploy>
  <EnableModZip>false</EnableModZip>
</PropertyGroup>
<ItemGroup>
  <None Include="assets\reward.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

The persisted marker is written only after the game's complete-stack capacity
preflight and successful insertion. That ordering prevents a partial or failed
award from being marked complete and a completed award from repeating.

## Package and diagnose the deliberate error

First set `assets/reward.json` to the deliberately wrong value:

```json
{ "RewardQuantity": 12 }
```

Check, build, and package it. Copy the resulting ZIP to a frozen evidence name;
do not edit or rebuild that copy.

```powershell
& $sdvkit project inspect $mod --json | Tee-Object (Join-Path $evidence 'inspect-wrong.json')
& $sdvkit project check $mod --json | Tee-Object (Join-Path $evidence 'check-wrong.json')
& $sdvkit project build $mod --project InventoryRewardRecipe.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'build-wrong.json')
& $sdvkit project package $mod --project InventoryRewardRecipe.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'package-wrong.json')
$wrongReport = Get-Content (Join-Path $evidence 'package-wrong.json') -Raw | ConvertFrom-Json
$wrongArchive = Join-Path $mod $wrongReport.archive
$wrongFrozen = Join-Path $evidence 'InventoryRewardRecipe-reviewed-wrong-1.0.0.zip'
Copy-Item -LiteralPath $wrongArchive -Destination $wrongFrozen
$wrongExtract = Join-Path $recipe 'reviewed-wrong-extracted'
Expand-Archive -LiteralPath $wrongFrozen -DestinationPath $wrongExtract
$wrongTarget = Join-Path $wrongExtract 'InventoryRewardRecipe'
```

Extract the frozen ZIP to a fresh directory below `$recipe`; use that directory,
not mutable source output, as the direct review target. Start it with
`--test-save`, require the exact staged identity and loaded target, then capture:

```powershell
& $sdvkit lab test-save --topology single --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'baseline.json')
& $sdvkit project review start $wrongTarget --game-path $gamePath `
  --topology single --test-save --json | Tee-Object (Join-Path $evidence 'start-wrong.json')
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-wrong.json')
& $sdvkit project review diagnostics --mod SDVKit.InventoryRewardRecipe --limit 20 --json |
  Tee-Object (Join-Path $evidence 'diagnostics-wrong.json')
```

Require twelve `(O)390` Stone and the attributed warning `Configured reward
quantity 12 differs from the recipe expectation 10.` The compiled feature ran,
but packaged JSON is wrong. Stop and reset; do not retain its grant marker.

```powershell
& $sdvkit project review stop --topology single --json
& $sdvkit project review reset --topology single --json
```

Change only the JSON quantity to `10`; repeat check, build, package, fresh
extraction, and hashes. Require identical DLL bytes between wrong and correct
packages and a different JSON/archive hash. Freeze and select it explicitly:

```powershell
& $sdvkit project check $mod --json | Tee-Object (Join-Path $evidence 'check-correct.json')
& $sdvkit project build $mod --project InventoryRewardRecipe.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'build-correct.json')
& $sdvkit project package $mod --project InventoryRewardRecipe.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'package-correct.json')
$correctReport = Get-Content (Join-Path $evidence 'package-correct.json') -Raw | ConvertFrom-Json
$correctArchive = Join-Path $mod $correctReport.archive
$correctFrozen = Join-Path $evidence 'InventoryRewardRecipe-reviewed-correct-1.0.0.zip'
Copy-Item -LiteralPath $correctArchive -Destination $correctFrozen
$correctExtract = Join-Path $recipe 'reviewed-correct-extracted'
Expand-Archive -LiteralPath $correctFrozen -DestinationPath $correctExtract
$correctTarget = Join-Path $correctExtract 'InventoryRewardRecipe'
```

## Add the bounded chest companion

Create a second standalone mod named `Inventory Reward Harness`, unique ID
`SDVKit.InventoryRewardHarness`. It is a sample-specific companion, not a new
fixture framework. Its console command supports only `prepare`, `open`, `status`,
and `cleanup`.

```powershell
& $sdvkit project create smapi-mod $harness `
  --name 'Inventory Reward Harness' --author SDVKit `
  --unique-id SDVKit.InventoryRewardHarness `
  --description 'Prepare and inspect one empty vanilla chest in the owned disposable recipe fixture.' --json
```

Use this complete `ModEntry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace InventoryRewardHarness;

internal sealed class ModEntry : Mod
{
private const string Command = "inventory-reward-harness";
private const string MarkerKey = "SDVKit.InventoryRewardHarness/Chest";

public override void Entry(IModHelper helper) => helper.ConsoleCommands.Add(
    Command, "Owned recipe harness: prepare | open | status | cleanup", (_, args) => Run(args));

private void Run(string[] args)
{
    if (!OwnedFixtureReady())
    {
        Monitor.Log("Inventory reward harness rejected: exact owned disposable single fixture is not ready.", LogLevel.Error);
        return;
    }
    if (args.Length == 1 && args[0] == "prepare") Prepare();
    else if (args.Length == 1 && args[0] == "open") Open();
    else if (args.Length == 1 && args[0] == "status") Status("status");
    else if (args.Length == 1 && args[0] == "cleanup") Cleanup();
    else Monitor.Log($"Usage: {Command} prepare | open | status | cleanup", LogLevel.Error);
}

private void Prepare()
{
    if (FindChests().Length != 0)
    {
        Monitor.Log("Inventory reward harness prepare refused: a marked chest already exists.", LogLevel.Error);
        return;
    }
    if (!TryCloseMenu("prepare")) return;
    Vector2? tile = FindClearTiles(Game1.currentLocation, Game1.player.Tile).FirstOrDefault();
    if (tile is null || tile == Vector2.Zero)
    {
        Monitor.Log("Inventory reward harness prepare failed: a clear nearby tile was not found.", LogLevel.Error);
        return;
    }
    var chest = new Chest(playerChest: true, tile.Value, "130");
    chest.modData[MarkerKey] = "true";
    Game1.currentLocation.Objects.Add(tile.Value, chest);
    Status("prepared");
}

private void Open()
{
    var matches = FindChests();
    if (matches.Length != 1 || !ReferenceEquals(matches[0].Location, Game1.currentLocation))
    {
        Monitor.Log($"Inventory reward harness open refused: expected one marked chest in the current location, found {matches.Length}.", LogLevel.Error);
        return;
    }
    if (!TryCloseMenu("open")) return;
    matches[0].Chest.ShowMenu();
    Status("opened");
}

private void Cleanup()
{
    if (!TryCloseMenu("cleanup")) return;
    var matches = FindChests();
    foreach ((GameLocation location, Vector2 tile, _) in matches)
        location.Objects.Remove(tile);
    Monitor.Log($"Inventory reward harness cleanup: removedChest={matches.Length == 1}; removedCount={matches.Length}.", LogLevel.Info);
}

private void Status(string phase)
{
    var matches = FindChests();
    if (matches.Length != 1)
    {
        Monitor.Log($"Inventory reward harness {phase}: chestCount={matches.Length}.", LogLevel.Info);
        return;
    }
    var match = matches[0];
    Item? held = Game1.activeClickableMenu is ItemGrabMenu menu ? menu.heldItem : null;
    int playerStone = Game1.player.Items.Where(item => item?.QualifiedItemId == "(O)390").Sum(item => item!.Stack);
    int chestStone = match.Chest.Items.Where(item => item?.QualifiedItemId == "(O)390").Sum(item => item!.Stack);
    int heldStone = held?.QualifiedItemId == "(O)390" ? held.Stack : 0;
    Monitor.Log($"Inventory reward harness {phase}: location={match.Location.NameOrUniqueName}; "
        + $"tile={(int)match.Tile.X},{(int)match.Tile.Y}; chestItemId={match.Chest.QualifiedItemId}; "
        + $"capacity={match.Chest.GetActualCapacity()}; chestOccupied={match.Chest.Items.Count(item => item is not null)}; "
        + $"heldEmpty={held is null}; stoneTotal={playerStone + chestStone + heldStone} "
        + $"(player={playerStone}, chest={chestStone}, held={heldStone}).", LogLevel.Info);
}

private static (GameLocation Location, Vector2 Tile, Chest Chest)[] FindChests() =>
    Game1.locations.SelectMany(location => location.Objects.Pairs
        .Where(pair => pair.Value is Chest chest && chest.modData.ContainsKey(MarkerKey))
        .Select(pair => (location, pair.Key, (Chest)pair.Value))).ToArray();

private static IEnumerable<Vector2> FindClearTiles(GameLocation location, Vector2 origin)
{
    for (int radius = 2; radius <= 8; radius++)
    for (int y = -radius; y <= radius; y++)
    for (int x = -radius; x <= radius; x++)
    {
        if (Math.Abs(x) != radius && Math.Abs(y) != radius) continue;
        var tile = new Vector2((int)origin.X + x, (int)origin.Y + y);
        if (location.isTileOnMap(tile) && location.isTilePlaceable(tile)
            && !location.Objects.ContainsKey(tile) && !location.terrainFeatures.ContainsKey(tile)
            && location.isTileOccupiedByFarmer(tile) is null) yield return tile;
    }
}

private bool TryCloseMenu(string operation)
{
    if (Game1.activeClickableMenu is ItemGrabMenu { heldItem: not null })
    {
        Monitor.Log($"Inventory reward harness {operation} refused: the chest menu is holding an item.", LogLevel.Error);
        return false;
    }
    if (Game1.activeClickableMenu is not null) Game1.exitActiveMenu();
    return true;
}

private static bool OwnedFixtureReady()
{
    string? owner = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID");
    string? fixture = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FIXTURE_ID");
    string? gameId = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID");
    return Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"))
        && Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_MODE") == "review"
        && Guid.TryParseExact(owner, "N", out _) && Guid.TryParseExact(fixture, "N", out _)
        && long.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out long expectedGameId)
        && expectedGameId > 0 && Game1.uniqueIDForThisGame == (ulong)expectedGameId
        && Constants.SaveFolderName == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_ID")
        && Context.ScreenId == 0 && Context.IsMainPlayer && Context.IsWorldReady
        && !Context.IsMultiplayer && !Game1.exitToTitle
        && Game1.player.Name == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_PLAYER_NAME")
        && Game1.player.farmName.Value == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FARM_NAME")
        && Game1.player.favoriteThing.Value == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FAVORITE_THING")
        && Game1.player.modData.TryGetValue("SDVKit/WorkspaceOwnerId", out string? observedOwner)
        && observedOwner == owner
        && Game1.player.modData.TryGetValue("SDVKit/FixtureId", out string? observedFixture)
        && observedFixture == fixture;
}
}
```

Check, build, package, freeze, hash, and freshly extract this companion like the
target, then bind its exact directory:

```powershell
& $sdvkit project check $harness --json | Tee-Object (Join-Path $evidence 'check-harness.json')
& $sdvkit project build $harness --project InventoryRewardHarness.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'build-harness.json')
& $sdvkit project package $harness --project InventoryRewardHarness.csproj --game-path $gamePath --json |
  Tee-Object (Join-Path $evidence 'package-harness.json')
$harnessReport = Get-Content (Join-Path $evidence 'package-harness.json') -Raw | ConvertFrom-Json
$harnessArchive = Join-Path $harness $harnessReport.archive
$harnessFrozen = Join-Path $evidence 'InventoryRewardHarness-reviewed-1.0.0.zip'
Copy-Item -LiteralPath $harnessArchive -Destination $harnessFrozen
$harnessExtract = Join-Path $recipe 'reviewed-harness-extracted'
Expand-Archive -LiteralPath $harnessFrozen -DestinationPath $harnessExtract
$harnessTarget = Join-Path $harnessExtract 'InventoryRewardHarness'
```

## Prove the corrected transfer

Start the extracted corrected reward as direct target and the extracted harness
as its only companion:

```powershell
& $sdvkit project review start $correctTarget --game-path $gamePath `
  --companion $harnessTarget --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-correct.json')
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-correct.json')
```

Require exactly ten Stone and no deliberate mismatch warning. Send the quoted
review-console command `inventory-reward-harness prepare`, then `open`. Require
harness status and fresh container observation proving one empty supported
36-slot chest and an empty held item; delivery alone is insufficient.

```powershell
& $sdvkit project review command 'inventory-reward-harness prepare' --topology single --json |
  Tee-Object (Join-Path $evidence 'harness-prepare.json')
& $sdvkit project review command 'inventory-reward-harness open' --topology single --json |
  Tee-Object (Join-Path $evidence 'harness-open.json')
& $sdvkit project review command 'inventory-reward-harness status' --topology single --json |
  Tee-Object (Join-Path $evidence 'harness-status.json')
& $sdvkit project review container --topology single --json |
  Tee-Object (Join-Path $evidence 'container-before.json')
```

Connect one real MCP client to this exact unbound review:

```powershell
& $sdvkit project review mcp serve --topology single `
  --allow-container-transfer --allow-fixture-actions --allow-input
```

Retain its catalogue and orderly JSONL/EOF transcript. Require
`stardew_container_get`, `stardew_container_transfer`, `stardew_fixture_save`,
and intended input tools. These are three independent grants.

Call `stardew_container_get {}`, then transfer from the actual Stone slot using
every returned identity unchanged:

```json
{
  "direction": "deposit",
  "sourceSlot": 0,
  "quantity": 3,
  "qualifiedItemId": "(O)390",
  "selectionIdentity": "<fresh selectionIdentity>",
  "containerRevision": "<fresh containerRevision>",
  "instanceIdentity": "<fresh player-slot instanceIdentity>",
  "itemRevision": "<fresh player-slot itemRevision>"
}
```

Do not assume slot 0 if the read differs. Require `completed`, observed quantity
3, held empty, and player/chest/held `10/0/0 -> 7/3/0`. Read again and withdraw
one from the actual chest Stone slot with fresh identities. Require
`7/3/0 -> 8/2/0`; total Stone remains ten.

Submit the retained pre-deposit request once. Require
`containerTransferSelectionStale` plus either `data=null` or explicit
non-dispatch evidence, and a fresh unchanged `8/2/0` read. Never replay
`partial` or `uncertain`; inspect current state.

## Save, restart, and finish

Close the chest with `stardew_input_press { "button": "Escape" }` while held is
empty, then require a fresh menu read showing it closed. Call
`stardew_fixture_save {}` and require completion. Stop without reset, then start
the same extracted target, companion, topology, game, and retained fixture:

```powershell
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-before-reload.json')
& $sdvkit project review start $correctTarget --game-path $gamePath `
  --companion $harnessTarget --topology single --test-save --json |
  Tee-Object (Join-Path $evidence 'start-reload.json')
```

Require the same target/package identities and new valid launch. Send only
`inventory-reward-harness open`, then make fresh CLI/MCP reads. Acceptance is
`8/2/0`, total ten: the chest persisted and reward did not repeat to eighteen.
The selected-mod trace about an existing grant supports but cannot replace this.

```powershell
& $sdvkit project review command 'inventory-reward-harness open' --topology single --json |
  Tee-Object (Join-Path $evidence 'harness-open-reload.json')
& $sdvkit project review inventory --topology single --json |
  Tee-Object (Join-Path $evidence 'inventory-reload.json')
& $sdvkit project review container --topology single --json |
  Tee-Object (Join-Path $evidence 'container-reload.json')
```

Send harness `cleanup` once with held empty, require exactly one chest removed,
close MCP by orderly EOF, retain the final log, and finish:

```powershell
& $sdvkit project review command 'inventory-reward-harness cleanup' --topology single --json |
  Tee-Object (Join-Path $evidence 'harness-cleanup.json')
& $sdvkit project review stop --topology single --json |
  Tee-Object (Join-Path $evidence 'stop-final.json')
& $sdvkit project review reset --topology single --json |
  Tee-Object (Join-Path $evidence 'reset-final.json')
& $sdvkit project review status --topology single --json
& $sdvkit project review status --topology network-2 --json
```

Require both topologies stopped, fixture reset, staging removed, zero game
processes and reparse points, clean locks, and zero unexplained protected-path
deltas. Run the full [applicable check matrix](releasing.md#select-the-checks),
exact PR CI, and exact resulting-main CI.

Record artifact hashes, result, elapsed wall time, evidence, and manual
intervention for every phase. Report build, package, load, diagnosis, transfer,
persistence, cleanup, and CI separately. This proves one ordinary Stone/chest
workflow in one owned disposable single world—not general item, chest,
multiplayer, or normal-save compatibility.

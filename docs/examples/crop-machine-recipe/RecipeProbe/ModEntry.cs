using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using StardewObject = StardewValley.Object;

internal sealed class ModEntry : Mod
{
    private const string Marker = "SDVKit.RecipeProbe/role";
    private const string CropRole = "crop";
    private const string MachineRole = "machine";
    private const string ChestRole = "chest";
    private const string SeedId = "745";
    private const string HarvestId = "(O)400";
    private const string WrongInputId = "(O)399";
    private const string OutputId = "(O)167";

    private Farm? _farm;
    private Vector2? _cropTile;
    private Vector2? _machineTile;
    private Vector2? _chestTile;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += (_, _) => Rediscover("save-loaded");
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => ClearReferences("return-to-title");
        helper.ConsoleCommands.Add("recipe_probe_prepare_crop",
            "Prepare one dry Spring strawberry crop in the disposable SDVKit Farm.",
            (_, _) => PrepareCrop());
        helper.ConsoleCommands.Add("recipe_probe_mature",
            "Synthetically mature the exact watered probe crop after validating native crop data.",
            (_, _) => MatureCrop());
        helper.ConsoleCommands.Add("recipe_probe_prepare_machine",
            "Place one empty Keg. Pass 'synthetic' only for the deliberate wrong-condition diagnosis.",
            (_, args) => PrepareMachine(args.SingleOrDefault()));
        helper.ConsoleCommands.Add("recipe_probe_advance",
            "Advance only the exact processing probe Keg through native minutesElapsed.",
            (_, _) => AdvanceMachine());
        helper.ConsoleCommands.Add("recipe_probe_prepare_chest",
            "Place one empty standard vanilla wooden Chest without moving any item into it.",
            (_, _) => PrepareChest());
        helper.ConsoleCommands.Add("recipe_probe_bind_chest",
            "Rebind the player to the exact existing marked Chest without replacing or opening it.",
            (_, _) => BindExistingChest());
        helper.ConsoleCommands.Add("recipe_probe_report",
            "Log exact probe crop, machine, chest, player geometry, and complete relevant backpack totals.",
            (_, _) => Report("requested"));
    }

    private void PrepareCrop()
    {
        if (!TryGetOwnedFarm(out Farm farm)) return;
        if (!TryFindEmptyInventorySlot(out int canSlot)) return;
        RemoveMarkedWorldEntries(farm);
        if (!TryFindPlacement(farm, excluded: [], out Vector2 playerTile, out Vector2 cropTile))
        {
            Refuse("no open adjacent Farm tiles were found for the crop");
            return;
        }

        Game1.cropData.TryGetValue(SeedId, out CropData? data);
        if (!CropDataMatches(data, farm, out string dataReason))
        {
            Refuse(dataReason);
            return;
        }

        var crop = new Crop(SeedId, (int)cropTile.X, (int)cropTile.Y, farm);
        var dirt = new HoeDirt(HoeDirt.dry, crop);
        dirt.modData[Marker] = CropRole;
        crop.modData[Marker] = CropRole;
        farm.terrainFeatures.Add(cropTile, dirt);

        var can = new WateringCan { WaterLeft = 20 };
        Game1.player.Items[canSlot] = can;
        Game1.player.CurrentToolIndex = canSlot;
        if (!BindPlayer(farm, playerTile, cropTile))
        {
            farm.terrainFeatures.Remove(cropTile);
            Refuse("no bounded player position produced the exact native east-facing grab tile");
            return;
        }

        _farm = farm;
        _cropTile = cropTile;
        _machineTile = null;
        _chestTile = null;
        Monitor.Log($"RECIPE_PROBE_READY stage=crop-prepared setup=synthetic seed=(O){SeedId} harvest={HarvestId} season={farm.GetSeason()} regrowDays={data!.RegrowDays} harvestMethod={data.HarvestMethod} isRaised={data.IsRaised}", LogLevel.Info);
        Report("crop-prepared");
    }

    private void MatureCrop()
    {
        if (!TryGetOwnedCrop(out Farm farm, out _, out HoeDirt dirt, out Crop crop)) return;
        CropData? data = crop.GetData();
        if (!CropDataMatches(data, farm, out string dataReason) || !dirt.isWatered())
        {
            Refuse(!dirt.isWatered() ? "the exact probe crop has not been watered through native behavior" : dataReason);
            return;
        }

        crop.growCompletely();
        if (!dirt.readyForHarvest() || crop.GetHarvestMethod() != HarvestMethod.Grab)
        {
            Refuse("the synthetically matured probe crop is not ready for native Grab harvest");
            return;
        }

        Monitor.Log("RECIPE_PROBE_READY stage=crop-matured setup=synthetic-growth nativeReady=true", LogLevel.Info);
        Report("crop-matured");
    }

    private void PrepareMachine(string? mode)
    {
        if (mode is not (null or "synthetic"))
        {
            Refuse("machine mode must be omitted or exactly 'synthetic'");
            return;
        }
        if (!TryGetOwnedCrop(out Farm farm, out Vector2 cropTile, out HoeDirt dirt, out Crop crop)) return;
        CropData? data = crop.GetData();
        if (!CropDataMatches(data, farm, out string dataReason)
            || dirt.readyForHarvest() || !crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value != data!.RegrowDays)
        {
            Refuse(dataReason.Length > 0 ? dataReason : "the same probe crop is not in its exact native regrowing/not-ready post-harvest state");
            return;
        }

        bool supplied = false;
        if (BackpackTotal(HarvestId) < 1)
        {
            if (mode != "synthetic")
            {
                Refuse("no actually harvested strawberry exists in the backpack");
                return;
            }
            Item synthetic = ItemRegistry.Create(HarvestId);
            if (!Game1.player.addItemToInventoryBool(synthetic))
            {
                Refuse("the synthetic diagnostic strawberry could not be added to the backpack");
                return;
            }
            supplied = true;
        }

        if (!TryFindPlacement(farm, [cropTile], out Vector2 playerTile, out Vector2 machineTile))
        {
            Refuse("no separate open adjacent Farm tiles were found for the Keg");
            return;
        }
        StardewObject keg = ItemRegistry.Create<StardewObject>("(BC)12");
        keg.modData[Marker] = MachineRole;
        keg.Location = farm;
        keg.TileLocation = machineTile;
        farm.Objects.Add(machineTile, keg);
        if (!BindPlayer(farm, playerTile, machineTile))
        {
            farm.Objects.Remove(machineTile);
            Refuse("the Keg could not be bound to an exact native grab tile");
            return;
        }
        if (!TrySelectFirst(HarvestId))
        {
            farm.Objects.Remove(machineTile);
            Refuse("the preserved Strawberry could not be selected without changing inventory");
            return;
        }
        _farm = farm;
        _machineTile = machineTile;
        Monitor.Log($"RECIPE_PROBE_READY stage=machine-prepared setup=synthetic-placement inputSource={(supplied ? "synthetic-wrong-diagnostic" : "native-harvest")}", LogLevel.Info);
        Report("machine-prepared");
    }

    private void AdvanceMachine()
    {
        if (!TryGetOwnedMachine(out _, out StardewObject keg)) return;
        if (keg.MinutesUntilReady <= 0 || keg.readyForHarvest.Value || keg.heldObject.Value is null)
        {
            Refuse("the exact probe Keg is not processing an input");
            return;
        }
        int elapsed = keg.MinutesUntilReady;
        bool removed = keg.minutesElapsed(elapsed);
        if (removed || !keg.readyForHarvest.Value || keg.heldObject.Value is null)
        {
            Refuse("native Keg timing did not reach a retained ready output");
            return;
        }
        Monitor.Log($"RECIPE_PROBE_READY stage=machine-advanced setup=synthetic-time elapsed={elapsed} output={keg.heldObject.Value.QualifiedItemId} stack={keg.heldObject.Value.Stack}", LogLevel.Info);
        Report("machine-advanced");
    }

    private void PrepareChest()
    {
        if (!TryGetOwnedFarm(out Farm farm)) return;
        if (!TryFindEmptyInventorySlot(out int emptySlot)) return;
        var excluded = new List<Vector2>();
        if (_cropTile is not null) excluded.Add(_cropTile.Value);
        if (_machineTile is not null) excluded.Add(_machineTile.Value);
        if (!TryFindPlacement(farm, excluded, out Vector2 playerTile, out Vector2 chestTile))
        {
            Refuse("no separate open adjacent Farm tiles were found for the Chest");
            return;
        }
        var chest = new Chest(playerChest: true, chestTile, itemId: "130");
        if (chest.QualifiedItemId != "(BC)130" || chest.Items.Count != 0)
        {
            Refuse("the standard vanilla wooden Chest could not be created empty");
            return;
        }
        chest.modData[Marker] = ChestRole;
        chest.Location = farm;
        chest.TileLocation = chestTile;
        farm.Objects.Add(chestTile, chest);
        if (!BindPlayer(farm, playerTile, chestTile))
        {
            farm.Objects.Remove(chestTile);
            Refuse("the Chest could not be bound to an exact native grab tile");
            return;
        }
        Game1.player.CurrentToolIndex = emptySlot;
        _farm = farm;
        _chestTile = chestTile;
        Monitor.Log("RECIPE_PROBE_READY stage=chest-prepared setup=synthetic-placement injectedItems=0", LogLevel.Info);
        Report("chest-prepared");
    }

    private void BindExistingChest()
    {
        if (!TryGetOwnedFarm(out Farm farm) || !TryFindEmptyInventorySlot(out int emptySlot)) return;
        Vector2? found = _chestTile ?? FindMarkedObject(farm, ChestRole);
        if (found is null || !farm.Objects.TryGetValue(found.Value, out StardewObject? obj)
            || obj is not Chest chest || chest.QualifiedItemId != "(BC)130"
            || !chest.modData.TryGetValue(Marker, out string? role) || role != ChestRole)
        {
            Refuse("the exact existing marked wooden Chest is unavailable");
            return;
        }
        if (!TryBindToExistingTarget(farm, found.Value))
        {
            Refuse("no bounded open adjacent tile can reach the exact existing Chest");
            return;
        }
        Game1.player.CurrentToolIndex = emptySlot;
        _farm = farm;
        _chestTile = found;
        Monitor.Log($"RECIPE_PROBE_READY stage=chest-rebound setup=position-only tile={TileText(found)} items={chest.Items.Count}", LogLevel.Info);
        Report("chest-rebound");
    }

    private void Rediscover(string stage)
    {
        if (!ExactFixtureReady() || Game1.getFarm() is not Farm farm
            || farm.Map?.GetLayer("Back") is null)
        {
            Refuse("rediscovery requires the exact owned disposable SDVKit main-player save");
            return;
        }
        ClearReferences(stage);
        _farm = farm;
        foreach ((Vector2 tile, TerrainFeature feature) in farm.terrainFeatures.Pairs)
        {
            if (feature is HoeDirt dirt && dirt.modData.TryGetValue(Marker, out string? role) && role == CropRole)
                _cropTile = tile;
        }
        foreach ((Vector2 tile, StardewObject obj) in farm.Objects.Pairs)
        {
            if (!obj.modData.TryGetValue(Marker, out string? role)) continue;
            if (role == MachineRole) _machineTile = tile;
            if (role == ChestRole) _chestTile = tile;
        }
        Monitor.Log($"RECIPE_PROBE_REDISCOVERED stage={stage} crop={TileText(_cropTile)} machine={TileText(_machineTile)} chest={TileText(_chestTile)}", LogLevel.Info);
        Report(stage);
    }

    private bool TryGetOwnedFarm(out Farm farm)
    {
        farm = null!;
        if (!ExactFixtureReady() || Game1.currentLocation is not Farm candidate
            || !ReferenceEquals(Game1.getFarm(), candidate) || candidate.Map?.GetLayer("Back") is null)
        {
            Refuse("requires the exact owned disposable SDVKit main-player Farm");
            return false;
        }
        farm = candidate;
        return true;
    }

    private bool TryGetOwnedCrop(out Farm farm, out Vector2 tile, out HoeDirt dirt, out Crop crop)
    {
        tile = default;
        dirt = null!;
        crop = null!;
        if (!TryGetOwnedFarm(out farm)) return false;
        Vector2? found = _cropTile ?? FindMarkedTerrain(farm, CropRole);
        if (found is null || !farm.terrainFeatures.TryGetValue(found.Value, out TerrainFeature? feature)
            || feature is not HoeDirt candidate || candidate.crop is not Crop candidateCrop
            || !candidate.modData.TryGetValue(Marker, out string? role) || role != CropRole
            || !candidateCrop.modData.TryGetValue(Marker, out string? cropRole) || cropRole != CropRole)
        {
            Refuse("the exact marked probe crop is unavailable");
            return false;
        }
        _farm = farm;
        _cropTile = found;
        tile = found.Value;
        dirt = candidate;
        crop = candidateCrop;
        return true;
    }

    private bool TryGetOwnedMachine(out Farm farm, out StardewObject keg)
    {
        keg = null!;
        if (!TryGetOwnedFarm(out farm)) return false;
        Vector2? found = _machineTile ?? FindMarkedObject(farm, MachineRole);
        if (found is null || !farm.Objects.TryGetValue(found.Value, out StardewObject? candidate)
            || candidate.QualifiedItemId != "(BC)12" || !candidate.modData.TryGetValue(Marker, out string? role) || role != MachineRole)
        {
            Refuse("the exact marked probe Keg is unavailable");
            return false;
        }
        _farm = farm;
        _machineTile = found;
        keg = candidate;
        return true;
    }

    private static bool CropDataMatches(CropData? data, Farm farm, out string reason)
    {
        reason = "";
        if (data is null) reason = "installed Data/Crops has no Strawberry seed 745";
        else if (!data.Seasons.Contains(Season.Spring) || farm.GetSeason() != Season.Spring) reason = "the fixture and Strawberry crop are not both Spring";
        else if (data.HarvestItemId != "400") reason = "installed Strawberry harvest item is not object 400";
        else if (data.RegrowDays != 4) reason = "installed Strawberry regrow duration is not four days";
        else if (data.HarvestMethod != HarvestMethod.Grab) reason = "installed Strawberry harvest method is not Grab";
        else if (data.IsRaised) reason = "installed Strawberry unexpectedly requires raised/trellis geometry";
        return reason.Length == 0;
    }

    private static bool TryFindPlacement(Farm farm, IEnumerable<Vector2> excluded, out Vector2 playerTile, out Vector2 targetTile)
    {
        HashSet<Vector2> skip = [.. excluded];
        var back = farm.Map.GetLayer("Back");
        for (int y = 2; y < back.LayerHeight - 1; y++)
        {
            for (int x = 2; x < back.LayerWidth - 2; x++)
            {
                playerTile = new Vector2(x, y);
                targetTile = new Vector2(x + 1, y);
                if (!skip.Contains(playerTile) && !skip.Contains(targetTile)
                    && Open(farm, playerTile) && Open(farm, targetTile)) return true;
            }
        }
        playerTile = targetTile = default;
        return false;
    }

    private static bool Open(Farm farm, Vector2 tile) =>
        !farm.Objects.ContainsKey(tile) && !farm.terrainFeatures.ContainsKey(tile)
        && farm.isTileLocationOpen(tile) && farm.isTilePlaceable(tile, itemIsPassable: false);

    private static bool BindPlayer(Farm farm, Vector2 playerTile, Vector2 targetTile)
    {
        Game1.player.Halt();
        for (int offset = 0; offset < Game1.tileSize; offset++)
        {
            Game1.player.Position = playerTile * Game1.tileSize + new Vector2(offset, 0);
            Game1.player.FacingDirection = 1;
            if (ReferenceEquals(Game1.currentLocation, farm)
                && Game1.player.TilePoint == playerTile.ToPoint() && Game1.player.GetGrabTile() == targetTile) return true;
        }
        return false;
    }

    private static bool TryBindToExistingTarget(Farm farm, Vector2 targetTile)
    {
        (Vector2 tile, int facing)[] candidates =
        [
            (targetTile + new Vector2(-1, 0), 1),
            (targetTile + new Vector2(1, 0), 3),
            (targetTile + new Vector2(0, -1), 2),
            (targetTile + new Vector2(0, 1), 0)
        ];
        foreach ((Vector2 playerTile, int facing) in candidates)
        {
            if (!Open(farm, playerTile)) continue;
            Game1.player.Halt();
            for (int offset = 0; offset < Game1.tileSize; offset++)
            {
                Game1.player.Position = playerTile * Game1.tileSize + new Vector2(offset, 0);
                Game1.player.FacingDirection = facing;
                if (Game1.player.TilePoint == playerTile.ToPoint() && Game1.player.GetGrabTile() == targetTile)
                    return true;
            }
        }
        return false;
    }

    private bool TryFindEmptyInventorySlot(out int slot)
    {
        slot = -1;
        if (Game1.player.CursorSlotItem is not null)
        {
            Refuse("the cursor slot must be empty; the probe never clears it");
            return false;
        }
        for (var index = 0; index < Game1.player.Items.Count; index++)
        {
            if (Game1.player.Items[index] is not null) continue;
            slot = index;
            return true;
        }
        Refuse("an actually empty backpack slot is required; the probe never overwrites items");
        return false;
    }

    private bool TrySelectFirst(string qualifiedItemId)
    {
        if (Game1.player.CursorSlotItem is not null)
        {
            Refuse("the cursor slot must be empty; the probe never clears it");
            return false;
        }
        for (var slot = 0; slot < Game1.player.Items.Count; slot++)
        {
            if (Game1.player.Items[slot]?.QualifiedItemId != qualifiedItemId) continue;
            Game1.player.CurrentToolIndex = slot;
            return true;
        }
        return false;
    }

    private static bool ExactFixtureReady()
    {
        if (!Context.IsMainPlayer || Context.IsMultiplayer || Context.ScreenId != 0 || !Context.IsWorldReady
            || Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1"
            || Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_MODE") != "review"
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))) return false;
        string owner = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID")?.Trim() ?? "";
        string fixture = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FIXTURE_ID")?.Trim() ?? "";
        string unique = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID")?.Trim() ?? "";
        string save = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_ID")?.Trim() ?? "";
        return Guid.TryParseExact(owner, "N", out _) && Guid.TryParseExact(fixture, "N", out _)
            && ulong.TryParse(unique, out ulong uniqueId) && uniqueId == Game1.uniqueIDForThisGame
            && Constants.SaveFolderName == save && Game1.player.Name == "SDVKit"
            && Game1.player.farmName.Value == "SDVKit" && Game1.player.favoriteThing.Value == "Tests"
            && Game1.player.modData.TryGetValue("SDVKit/WorkspaceOwnerId", out string? actualOwner) && actualOwner == owner
            && Game1.player.modData.TryGetValue("SDVKit/FixtureId", out string? actualFixture) && actualFixture == fixture;
    }

    private void Report(string stage)
    {
        if (!Context.IsWorldReady || Game1.player is null) return;
        Farm? farm = _farm ?? Game1.getFarm();
        string crop = CropState(farm, _cropTile);
        string machine = ObjectState(farm, _machineTile, MachineRole);
        string chest = ObjectState(farm, _chestTile, ChestRole);
        Rectangle bounds = Game1.player.GetBoundingBox();
        Vector2 grab = Game1.player.GetGrabTile();
        Item? selected = Game1.player.CurrentItem;
        Item? cursor = Game1.player.CursorSlotItem;
        Monitor.Log($"RECIPE_PROBE_STATE stage={stage} position={Game1.player.Position.X:0},{Game1.player.Position.Y:0} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} tile={Game1.player.TilePoint.X},{Game1.player.TilePoint.Y} facing={Game1.player.FacingDirection} grab={grab.X:0},{grab.Y:0} crop=[{crop}] machine=[{machine}] chest=[{chest}] selected={selected?.QualifiedItemId ?? "empty"} selectedStack={selected?.Stack ?? 0} water={(selected as WateringCan)?.WaterLeft ?? -1} cursor={cursor?.QualifiedItemId ?? "empty"} cursorStack={cursor?.Stack ?? 0} strawberry={BackpackTotal(HarvestId)} springOnion={BackpackTotal(WrongInputId)} wine={BackpackTotal("(O)348")} cola={BackpackTotal(OutputId)}", LogLevel.Info);
    }

    private static string CropState(Farm farm, Vector2? tile)
    {
        if (tile is null || !farm.terrainFeatures.TryGetValue(tile.Value, out TerrainFeature? feature) || feature is not HoeDirt dirt) return "missing";
        Crop? crop = dirt.crop;
        if (crop is null) return $"tile={TileText(tile)} watered={dirt.isWatered()} crop=missing";
        CropData? data = crop.GetData();
        return $"tile={TileText(tile)} watered={dirt.isWatered()} seed=(O){crop.netSeedIndex.Value} harvest=(O){crop.indexOfHarvest.Value} phase={crop.currentPhase.Value}/{crop.phaseDays.Count} day={crop.dayOfCurrentPhase.Value} fullyGrown={crop.fullyGrown.Value} dead={crop.dead.Value} ready={dirt.readyForHarvest()} regrows={crop.RegrowsAfterHarvest()} regrowDays={data?.RegrowDays ?? -1} method={crop.GetHarvestMethod()} raised={crop.raisedSeeds.Value}";
    }

    private static string ObjectState(Farm farm, Vector2? tile, string role)
    {
        if (tile is null || !farm.Objects.TryGetValue(tile.Value, out StardewObject? obj)) return "missing";
        if (role == ChestRole && obj is Chest chest)
            return $"tile={TileText(tile)} id={obj.QualifiedItemId} slots=36 items={chest.Items.Count} cola={chest.Items.Where(item => item?.QualifiedItemId == OutputId).Sum(item => item!.Stack)}";
        return $"tile={TileText(tile)} id={obj.QualifiedItemId} ready={obj.readyForHarvest.Value} minutes={obj.MinutesUntilReady} input={obj.lastInputItem.Value?.QualifiedItemId ?? "null"} output={obj.heldObject.Value?.QualifiedItemId ?? "null"} outputStack={obj.heldObject.Value?.Stack ?? 0}";
    }

    private static Vector2? FindMarkedTerrain(Farm farm, string role)
    {
        foreach ((Vector2 tile, TerrainFeature feature) in farm.terrainFeatures.Pairs)
        {
            if (feature.modData.TryGetValue(Marker, out string? value) && value == role) return tile;
        }
        return null;
    }

    private static Vector2? FindMarkedObject(Farm farm, string role)
    {
        foreach ((Vector2 tile, StardewObject obj) in farm.Objects.Pairs)
        {
            if (obj.modData.TryGetValue(Marker, out string? value) && value == role) return tile;
        }
        return null;
    }

    private static void RemoveMarkedWorldEntries(Farm farm)
    {
        foreach (Vector2 tile in farm.terrainFeatures.Pairs.Where(pair => pair.Value.modData.ContainsKey(Marker)).Select(pair => pair.Key).ToArray())
            farm.terrainFeatures.Remove(tile);
        foreach (Vector2 tile in farm.Objects.Pairs.Where(pair => pair.Value.modData.ContainsKey(Marker)).Select(pair => pair.Key).ToArray())
            farm.Objects.Remove(tile);
    }

    private void ClearReferences(string stage)
    {
        _farm = null;
        _cropTile = null;
        _machineTile = null;
        _chestTile = null;
        Monitor.Log($"RECIPE_PROBE_REFERENCES_CLEARED stage={stage} worldMutation=false", LogLevel.Trace);
    }

    private void Refuse(string reason) => Monitor.Log($"RECIPE_PROBE_REFUSED: {reason}.", LogLevel.Error);

    private static string TileText(Vector2? tile) => tile is null ? "none" : $"{tile.Value.X:0},{tile.Value.Y:0}";

    private static int BackpackTotal(string qualifiedItemId) => Game1.player.Items
        .Where(item => item?.QualifiedItemId == qualifiedItemId).Sum(item => item!.Stack);
}

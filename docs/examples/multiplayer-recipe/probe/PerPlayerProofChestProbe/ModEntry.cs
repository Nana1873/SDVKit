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

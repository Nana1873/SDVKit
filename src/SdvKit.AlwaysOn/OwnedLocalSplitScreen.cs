#if SDVKIT_GAME_AVAILABLE
using System.Text.Json;
using Microsoft.Xna.Framework;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SdvKit.AlwaysOn;

/// <summary>The explicitly selected second local screen of an existing owned fixture review.</summary>
internal sealed class OwnedLocalSplitScreen(TestSaveAutomation fixture, IMonitor monitor, Action<Game1> prepareScreenExit)
{
    internal const string FarmerMarker = "SDVKit/LocalReviewFarmer";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private Game1? _host;
    private Game1? _farmhandScreen;
    private long _farmerId;
    private long _hostFarmerId;
    private ulong _gameId;
    private string? _workspaceOwnerId;
    private DateTimeOffset? _joiningSince;
    private string? _failure;

    internal bool Selected => _host is not null;

    internal void Join()
    {
        RequireHost();
        if (!fixture.TryVerifyReviewFixture(out string fixtureId, out string reason))
            throw new InvalidOperationException(reason);
        if (_failure is not null) throw new InvalidOperationException(_failure);
        if (fixture.IsSaveBusy)
            throw new InvalidOperationException("Wait for the current fixture save before joining a screen.");
        if (_farmhandScreen is not null || GameRunner.instance.gameInstances.Count != 1)
            throw new InvalidOperationException("A second native screen already exists; inspect its status before joining.");
        if (!Selected && Context.IsMultiplayer)
            throw new InvalidOperationException("Select local split-screen before starting any multiplayer session.");

        Farmer[] farmers = Game1.getAllFarmhands().ToArray();
        Farmer farmer = farmers.Length == 0
            ? ReviewFarmhandPreparation.CreateSingleUnclaimedFarmhand()
            : farmers.Length == 1 && farmers[0].modData.TryGetValue(FarmerMarker, out string marker)
                && marker == fixtureId ? farmers[0]
                : throw new InvalidOperationException("The fixture contains a farmhand not owned by this local review.");
        if (farmer.UniqueMultiplayerID == 0 || farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            throw new InvalidOperationException("The native farmhand identity is invalid.");

        farmer.modData[FarmerMarker] = fixtureId;
        farmer.modData[TestSaveContract.FixtureMarkerKey] = fixtureId;
        _workspaceOwnerId = Game1.player.modData[TestSaveContract.WorkspaceOwnerMarkerKey];
        farmer.modData[TestSaveContract.WorkspaceOwnerMarkerKey] = _workspaceOwnerId;
        farmer.Name = "SDVKitLocal";
        farmer.displayName = farmer.Name;
        farmer.farmName.Value = TestSaveContract.FarmName;
        farmer.favoriteThing.Value = TestSaveContract.FavoriteThing;
        farmer.isCustomized.Value = true;
        _farmerId = farmer.UniqueMultiplayerID;
        _hostFarmerId = Game1.player.UniqueMultiplayerID;
        _gameId = Game1.uniqueIDForThisGame;
        _host = Game1.game1;
        _joiningSince = DateTimeOffset.UtcNow;
        ReviewVirtualCursor.Clear();
        // GameRunner creates SMAPI's normal SGame and its per-screen helpers.
        // The game starts its native local server and presents FarmhandMenu.
        GameRunner.instance.AddGameInstance(PlayerIndex.Two);
        _farmhandScreen = GameRunner.instance.gameInstances.Single(game => !ReferenceEquals(game, _host));
        monitor.Log($"SDVKit local split-screen joining fixture={fixtureId} farmer={_farmerId}.", LogLevel.Info);
    }

    internal void Leave()
    {
        RequireHost();
        VerifyHost();
        Game1 screen = _farmhandScreen
            ?? throw new InvalidOperationException("There is no owned second screen to close.");
        if (fixture.IsSaveBusy)
            throw new InvalidOperationException("Wait for the current fixture save before closing a screen.");
        prepareScreenExit(screen);
        GameRunner.instance.RemoveGameInstance(screen);
        monitor.Log("SDVKit local split-screen requested normal removal of the owned second screen.", LogLevel.Info);
    }

    internal void OnUpdateTicked()
    {
        if (!Selected || _failure is not null) return;
        try
        {
            if (Context.ScreenId == 0)
            {
                if (_farmhandScreen is not null && !GameRunner.instance.gameInstances.Contains(_farmhandScreen))
                {
                    _farmhandScreen = null;
                    _joiningSince = null;
                    monitor.Log("SDVKit local split-screen confirmed second-screen removal; host retained.", LogLevel.Info);
                }
                VerifyHost();
                if (_joiningSince is { } started && DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(2))
                    throw new InvalidOperationException("The owned second screen did not join within two minutes.");
                return;
            }
            if (!ReferenceEquals(Game1.game1, _farmhandScreen))
                throw new InvalidOperationException("An unexpected local screen entered the review.");
            if (_joiningSince is not null && Game1.activeClickableMenu is FarmhandMenu menu
                && menu.client?.availableFarmhands is { } available)
            {
                Farmer farmer = available.Single();
                if (farmer.UniqueMultiplayerID != _farmerId
                    || !farmer.modData.TryGetValue(FarmerMarker, out string marker)
                    || marker != fixture.Snapshot.FixtureId)
                    throw new InvalidOperationException("The native menu offered a different farmhand.");
                new FarmhandMenu.FarmhandSlot(menu, farmer).Activate();
            }
            if (Context.IsWorldReady)
            {
                VerifyCurrentPlayer();
                if (_joiningSince is not null)
                {
                    _joiningSince = null;
                    monitor.Log($"SDVKit local split-screen joined screen={Context.ScreenId} farmer={_farmerId} fixture={fixture.Snapshot.FixtureId}.", LogLevel.Info);
                }
            }
        }
        catch (Exception exception)
        {
            _failure = exception.GetBaseException().Message;
            monitor.Log($"SDVKit local split-screen failed: {_failure}", LogLevel.Error);
        }
    }

    internal void VerifyHost()
    {
        if (_failure is not null) throw new InvalidOperationException(_failure);
        if (!Selected || !ReferenceEquals(Game1.game1, _host) || !Context.IsMainPlayer)
            throw new InvalidOperationException("The exact local-review host is not current.");
        if (GameRunner.instance.gameInstances.Any(game => !ReferenceEquals(game, _host)
                && !ReferenceEquals(game, _farmhandScreen))
            || Game1.getOnlineFarmers().Any(farmer => farmer.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                && farmer.UniqueMultiplayerID != _farmerId))
            throw new InvalidOperationException("The local review contains an unexpected screen or remote farmer.");
    }

    internal void VerifyCurrentPlayer()
    {
        if (Context.ScreenId == 0) { VerifyHost(); return; }
        if (_failure is not null) throw new InvalidOperationException(_failure);
        if (!Context.IsWorldReady || !Context.IsSplitScreen || !Game1.IsClient
            || !ReferenceEquals(Game1.game1, _farmhandScreen)
            || Game1.player.UniqueMultiplayerID != _farmerId
            || Game1.MasterPlayer.UniqueMultiplayerID != _hostFarmerId
            || Game1.uniqueIDForThisGame != _gameId
            || !Game1.player.modData.TryGetValue(TestSaveContract.WorkspaceOwnerMarkerKey, out string owner)
            || owner != _workspaceOwnerId
            || !Game1.player.modData.TryGetValue(FarmerMarker, out string marker)
            || marker != fixture.Snapshot.FixtureId
            || !Game1.MasterPlayer.modData.TryGetValue(TestSaveContract.FixtureMarkerKey, out string hostMarker)
            || hostMarker != fixture.Snapshot.FixtureId)
            throw new InvalidOperationException("The current screen does not match the exact local-review farmer and host.");
    }

    internal void Status(ReviewMenuCommand menu)
    {
        if (!fixture.TryVerifyReviewFixture(out string fixtureId, out string reason))
            throw new InvalidOperationException(reason);
        monitor.Log("SDVKit local screen " + JsonSerializer.Serialize(new
        {
            launchId = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"),
            fixtureId,
            selected = Selected,
            screenId = Context.ScreenId,
            activeScreenIds = GameRunner.instance.gameInstances.Select(game => game.instanceId).ToArray(),
            joining = _joiningSince is not null,
            farmerId = Game1.player.UniqueMultiplayerID,
            runtime = ModEntry.CaptureRuntimeSnapshot(),
            menu = menu.CaptureCurrent(),
        }, JsonOptions), LogLevel.Info);
    }

    private static void RequireHost()
    {
        if (Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
            || Context.ScreenId != 0 || !Context.IsMainPlayer || !Context.IsWorldReady)
            throw new InvalidOperationException("Local split-screen requires screen 0 of an owned single fixture review.");
    }
}
#endif

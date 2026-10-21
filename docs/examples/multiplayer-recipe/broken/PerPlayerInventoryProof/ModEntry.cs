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

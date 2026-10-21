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

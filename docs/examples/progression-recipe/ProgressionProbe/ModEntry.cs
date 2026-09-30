using StardewModdingAPI;
using StardewValley;

namespace ProgressionProbe;

public sealed class ModConfig
{
    public int RequiredFriendshipPoints { get; set; } = 750;
}

public sealed class ModEntry : Mod
{
    private const string NpcId = "Leah";
    private const string MailId = "SDVKit.ProgressionProbe_RainBarrel";
    private const string QuestId = "SDVKit.ProgressionProbe_CheckBarrel";
    private const string DeliveredKey = "SDVKit.ProgressionProbe/Delivered";
    private ModConfig _config = new();

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        helper.Events.Content.AssetRequested += (_, e) =>
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/mail"))
                e.Edit(asset => asset.AsDictionary<string, string>().Data.Add(MailId,
                    "Could you check the rain barrel by my workshop? A careful look will keep the next storm from washing it away. -Leah[#]Leah's rain barrel"));
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Quests"))
                e.Edit(asset => asset.AsDictionary<string, string>().Data.Add(QuestId,
                    "Basic/Rain barrel check/Leah asked for a careful inspection of her workshop rain barrel./Inspect the rain barrel./null/-1/25/-1/false"));
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => Evaluate("save-loaded");
        helper.ConsoleCommands.Add("progression_probe_prepare", "Synthetically seed exactly 500 Leah points in a fresh owned disposable fixture.", (_, _) => Prepare());
        helper.ConsoleCommands.Add("progression_probe_evaluate", "Evaluate the original mail and quest condition without resetting progression.", (_, _) => Evaluate("requested"));
        helper.ConsoleCommands.Add("progression_probe_correct", "Correct this recipe's config threshold from 750 to 500; reconcile the staged config afterwards.", (_, _) => Correct());
        helper.ConsoleCommands.Add("progression_probe_finish", "Run the recipe's objective callback through native quest acceptance/completion, retaining its unclaimed reward.", (_, _) => Finish());
        helper.ConsoleCommands.Add("progression_probe_open_mail", "Invoke the native Farm mailbox action for the exact first recipe letter; observe receipt separately.", (_, _) => OpenMail());
    }

    private void Prepare()
    {
        if (!OwnedFixture()) { Refuse(); return; }
        Farmer player = Game1.player;
        if (Game1.getCharacterFromName(NpcId) is null || player.modData.ContainsKey(DeliveredKey)
            || player.hasOrWillReceiveMail(MailId) || player.hasQuest(QuestId))
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: prepare requires a fresh recipe state and installed Leah.", LogLevel.Error); return; }
        player.friendshipData[NpcId] = new Friendship(500);
        Monitor.Log("PROGRESSION_PROBE_PREPARED setup=synthetic npc=Leah points=500", LogLevel.Info);
        Evaluate("prepared");
    }

    private void Correct()
    {
        if (!OwnedFixture()) { Refuse(); return; }
        if (_config.RequiredFriendshipPoints != 750)
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: correction expects the original 750-point condition.", LogLevel.Error); return; }
        _config.RequiredFriendshipPoints = 500;
        Helper.WriteConfig(_config);
        Monitor.Log("PROGRESSION_PROBE_CORRECTED requiredPoints=500 configWritten=true reconcileRequired=true", LogLevel.Info);
    }

    private void Evaluate(string stage)
    {
        if (!OwnedFixture()) { Refuse(); return; }
        Farmer player = Game1.player;
        if (_config.RequiredFriendshipPoints is not (500 or 750))
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: only the recipe's 500/750-point conditions are supported.", LogLevel.Error); return; }
        if (player.modData.ContainsKey(DeliveredKey))
        { Monitor.Log($"PROGRESSION_PROBE_RETAINED stage={stage} duplicateEffects=0", LogLevel.Info); return; }
        int? points = player.friendshipData.TryGetValue(NpcId, out Friendship? friendship) ? friendship.Points : null;
        if (points is null || points < _config.RequiredFriendshipPoints)
        { Monitor.Log($"PROGRESSION_PROBE_BLOCKED stage={stage} npc=Leah points={points?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"} required={_config.RequiredFriendshipPoints}", LogLevel.Info); return; }
        if (player.hasOrWillReceiveMail(MailId) || player.hasQuest(QuestId))
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: unmarked existing recipe effects must be diagnosed, never duplicated.", LogLevel.Error); return; }
        player.addQuest(QuestId);
        if (!player.hasQuest(QuestId))
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: installed quest definition did not create the selected quest.", LogLevel.Error); return; }
        player.mailbox.Insert(0, MailId);
        player.modData[DeliveredKey] = "true";
        Monitor.Log($"PROGRESSION_PROBE_DELIVERED stage={stage} mailboxAdded=1 questAdded=1", LogLevel.Info);
    }

    private void Finish()
    {
        if (!OwnedFixture()) { Refuse(); return; }
        var matches = Game1.player.questLog.Where(quest => quest.id.Value == QuestId).ToArray();
        if (!Game1.player.modData.ContainsKey(DeliveredKey) || matches.Length != 1 || matches[0].completed.Value)
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: finish requires the single delivered incomplete recipe quest.", LogLevel.Error); return; }
        matches[0].accept();
        matches[0].questComplete();
        Monitor.Log($"PROGRESSION_PROBE_FINISHED accepted={matches[0].accepted.Value} completed={matches[0].completed.Value} rewardUnclaimed=true objectiveCallback=synthetic", LogLevel.Info);
    }

    private void OpenMail()
    {
        if (!OwnedFixture()) { Refuse(); return; }
        if (Game1.currentLocation is not Farm farm || Game1.activeClickableMenu is not null
            || Game1.player.mailbox.Count == 0 || Game1.player.mailbox[0] != MailId)
        { Monitor.Log("PROGRESSION_PROBE_REFUSED: native mail action requires Farm, no menu and the selected recipe letter first.", LogLevel.Error); return; }
        var tile = Game1.player.getMailboxPosition();
        bool handled = farm.checkAction(new xTile.Dimensions.Location(tile.X, tile.Y), Game1.viewport, Game1.player);
        Monitor.Log($"PROGRESSION_PROBE_MAIL_ACTION nativeHandled={handled} receiptMustBeObserved=true", LogLevel.Info);
    }

    private static bool OwnedFixture()
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

    private void Refuse() => Monitor.Log("PROGRESSION_PROBE_REFUSED: exact owned disposable single-player fixture required.", LogLevel.Error);
}

#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static class ReviewProgressionCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor,
        Func<TestSaveAutomation?> testSave)
    {
        if (args.Length != 6 || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]))
        {
            monitor.Log("SDVKit progression rejected invalid transport arguments.", LogLevel.Error);
            return;
        }
        var selection = new ReviewProgressionSelection(args[3], args[4], args[5]);
        string launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? "";
        ReviewProgressionReport Failure(string code) => new(1, "unavailable", code, launch,
            "single", null, DateTimeOffset.UtcNow, null);
        ReviewProgressionReport report;
        try
        {
            string? problem = ReviewProgressionContract.QueryProblem(selection);
            if (problem is not null) report = Failure(problem);
            else if (Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1" || args[2] != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                || Context.ScreenId != 0 || Context.IsMultiplayer || !Context.IsMainPlayer)
                report = Failure("progressionReviewBindingInvalid");
            else if (!Context.IsWorldReady || Game1.exitToTitle) report = Failure("progressionWorldNotReady");
            else if (testSave() is not { } fixture || !fixture.TryVerifyReviewFixture(out string fixtureId, out _))
                report = Failure("progressionTestSaveRequired");
            else
            {
                Farmer player = Game1.player;
                var npc = Game1.getCharacterFromName(selection.NpcId);
                ReviewProgressionNpc friendship = npc is null ? new("missing", null)
                    : player.friendshipData.TryGetValue(selection.NpcId, out Friendship? value)
                        ? new("available", value.Points) : new("noFriendship", null);
                var quests = player.questLog.Where(quest => quest.id.Value == selection.QuestId).ToArray();
                var data = new ReviewProgressionValues(player.UniqueMultiplayerID.ToString(CultureInfo.InvariantCulture),
                    fixtureId, Game1.ticks,
                    selection, friendship,
                    new(player.mailReceived.Contains(selection.MailId), player.mailForTomorrow.Contains(selection.MailId),
                        player.mailbox.Count(id => id == selection.MailId)),
                    new(quests.Length, quests.Count(quest => quest.accepted.Value), quests.Count(quest => quest.completed.Value)));
                report = ReviewProgressionContract.DataValid(data)
                    ? new(1, "ready", null, launch, "single", null, DateTimeOffset.UtcNow, data)
                    : Failure("progressionValuesInvalid");
            }
        }
        catch (Exception) { report = Failure("progressionCaptureFailed"); }
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewProgressionResponseEnvelope(1, args[1], report), JsonOptions);
            if (bytes.Length > ReviewProgressionContract.MaximumResponseBytes)
                bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewProgressionResponseEnvelope(1, args[1], Failure("progressionResponseLimit")), JsonOptions);
            ReviewResponseFile.Write(ReviewProgressionContract.ResponsePath(runtimePath, args[1]), bytes);
        }
        catch (Exception) { monitor.Log("SDVKit progression could not publish its bounded response.", LogLevel.Error); }
    }
}
#endif

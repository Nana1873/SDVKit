namespace SdvKit.Cli.LiveLab;

internal static class ReviewProgressionContract
{
    public const int MaximumResponseBytes = 32 * 1024;
    public const int MaximumIdLength = 128;
    public static string ResponsePath(string runtimePath, string requestId) =>
        Path.Combine(runtimePath, $"review-progression-{requestId}.json");

    public static bool IdValid(string? id) => id is { Length: > 0 and <= MaximumIdLength }
        && id.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-');

    public static string? QueryProblem(ReviewProgressionSelection? selection) => selection is null
        || !IdValid(selection.NpcId) || !IdValid(selection.MailId) || !IdValid(selection.QuestId)
        ? "progressionSelectionInvalid" : null;

    public static bool DataValid(ReviewProgressionValues? data) => data is not null
        && QueryProblem(data.Selection) is null && data.CaptureTick >= 0
        && long.TryParse(data.PlayerId, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out long player) && player != 0
        && data.PlayerId == player.ToString(System.Globalization.CultureInfo.InvariantCulture)
        && ReviewTransportToken.IsRequestId(data.FixtureId)
        && data.Npc is not null && (data.Npc.State == "available" && data.Npc.FriendshipPoints is not null
            || data.Npc.State is "missing" or "noFriendship" && data.Npc.FriendshipPoints is null)
        && data.Mail is not null && data.Mail.MailboxCount is >= 0 and <= 100
        && data.Quest is not null && data.Quest.MatchCount is >= 0 and <= 100
        && data.Quest.CompletedCount >= 0 && data.Quest.CompletedCount <= data.Quest.MatchCount
        && data.Quest.AcceptedCount >= 0 && data.Quest.AcceptedCount <= data.Quest.MatchCount;
}

internal sealed record ReviewProgressionSelection(string NpcId, string MailId, string QuestId);
internal sealed record ReviewProgressionNpc(string State, int? FriendshipPoints);
internal sealed record ReviewProgressionMail(bool Received, bool Tomorrow, int MailboxCount);
internal sealed record ReviewProgressionQuest(int MatchCount, int AcceptedCount, int CompletedCount);
internal sealed record ReviewProgressionValues(string PlayerId, string FixtureId, int CaptureTick,
    ReviewProgressionSelection Selection, ReviewProgressionNpc Npc, ReviewProgressionMail Mail,
    ReviewProgressionQuest Quest);
internal sealed record ReviewProgressionReport(int SchemaVersion, string State, string? ErrorCode,
    string? LaunchId, string Topology, string? Role, DateTimeOffset CapturedAtUtc,
    ReviewProgressionValues? Data);
internal sealed record ReviewProgressionResponseEnvelope(int SchemaVersion, string RequestId,
    ReviewProgressionReport Report);

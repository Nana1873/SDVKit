using System.Text.Json.Serialization;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewInputContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 4096;
    public const int MaximumProblemLength = 256;
    public const string PressAction = "press";
    public const string ChordAction = "chord";

    public static bool IsUiRevision(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public const string CursorSetAction = "cursorSet";
    public const string CursorClearAction = "cursorClear";
    public const string WheelAction = "wheel";

    public static string ResponsePath(string runtimePath, string requestId)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-input runtime path is required.",
                nameof(runtimePath));
        }
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            throw new ArgumentException(
                "The review-input request ID is invalid.",
                nameof(requestId));
        }

        return Path.Combine(runtimePath, $"review-input-{requestId}.json");
    }
}

internal sealed record ReviewInputQuery(
    string Action,
    string? Button,
    string? Direction,
    int? X,
    int? Y,
    IReadOnlyList<string>? Buttons = null,
    int? DurationTicks = null,
    string? UiRevision = null);

internal sealed record ReviewInputProblem(
    string Code,
    string Message);

internal sealed record ReviewInputResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    DateTimeOffset ObservedAtUtc,
    int GameTick,
    string Action,
    bool Succeeded,
    string? Button,
    string? Direction,
    int? X,
    int? Y,
    bool CursorSet,
    bool MenuOpen,
    ReviewInputProblem? Problem,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Buttons = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartTick = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndTick = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Released = null);

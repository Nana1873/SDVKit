using System.Globalization;
using System.Text.Json.Serialization;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewInputContract
{
    public const int SchemaVersion = 1;
    public const int MaximumResponseBytes = 4096;
    public const int MaximumProblemLength = 256;
    public const string PressAction = "press";
    public const string ChordAction = "chord";
    public const string ClickAction = "click";
    public const string ScrollAction = "scroll";
    public const string DragAction = "drag";
    public static bool IsGesture(string action) => action is ClickAction or ScrollAction or DragAction;
    private static readonly string[] MouseNames = ["MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2"];
    private static readonly string[] ModifierNames = ["LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt"];
    public static string? MouseButton(string? value) => MouseNames
        .FirstOrDefault(b => string.Equals(b, value, StringComparison.OrdinalIgnoreCase));
    public static string? Modifier(string? value) => ModifierNames
        .FirstOrDefault(b => string.Equals(b, value, StringComparison.OrdinalIgnoreCase));

    public static ReviewInputQuery? ParseGesture(string action, IReadOnlyList<string> values)
    {
        bool Number(int index, out int value)
        {
            value = 0;
            return index < values.Count && int.TryParse(values[index], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value);
        }
        ReviewInputQuery? gesture = null;
        if (Number(0, out int gestureX) && Number(1, out int gestureY))
        {
            if (action == ReviewInputContract.ClickAction && values.Count >= 5 && Number(3, out int count))
                gesture = new(action, values[2], null, gestureX, gestureY, UiRevision: values[4], Modifiers: values.Skip(5).ToArray(), Count: count);
            else if (action == ReviewInputContract.ScrollAction && values.Count == 4 && Number(2, out int notches))
                gesture = new(action, null, null, gestureX, gestureY, UiRevision: values[3], Notches: notches);
            else if (action == ReviewInputContract.DragAction && values.Count >= 7
                && Number(2, out int endX) && Number(3, out int endY) && Number(5, out int dragDuration))
                gesture = new(action, values[4], null, gestureX, gestureY, DurationTicks: dragDuration,
                    UiRevision: values[6], Modifiers: values.Skip(7).ToArray(), EndX: endX, EndY: endY);
        }
        return gesture is not null && ValidGesture(gesture) ? gesture : null;
    }

    public static bool ValidGesture(ReviewInputQuery query) => IsGesture(query.Action)
        && query.Buttons is null && query.Direction is null
        && query.X is >= 0 && query.Y is >= 0 && IsUiRevision(query.UiRevision)
        && (query.Action == ScrollAction
            ? query.Button is null && query.Modifiers is null && query.Count is null
                && query.Notches is >= -20 and <= 20 and not 0 && query.DurationTicks is null
                && query.EndX is null && query.EndY is null
            : MouseButton(query.Button) is not null && query.Notches is null
                && (query.Modifiers is null || query.Modifiers.Count <= 6)
                && (query.Modifiers ?? []).All(m => Modifier(m) is not null)
                && (query.Modifiers ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count() == (query.Modifiers?.Count ?? 0)
                && (query.Action == ClickAction
                    ? query.Count is 1 or 2 && query.DurationTicks is null && query.EndX is null && query.EndY is null
                    : query.Count is null && query.DurationTicks is >= 1 and <= 120 && query.EndX is >= 0 && query.EndY is >= 0));

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
    string? UiRevision = null,
    IReadOnlyList<string>? Modifiers = null,
    int? Count = null,
    int? Notches = null,
    int? EndX = null,
    int? EndY = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Released = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Modifiers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Notches = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CompletedSteps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FinalX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FinalY = null);

using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal enum ReviewInputKind
{
    Press,
    Scroll,
    Cursor,
    ClearCursor,
    Chord,
    Gesture,
    Text,
}

internal sealed record ReviewInputRequest(
    ReviewInputKind Kind,
    string? Button,
    int X,
    int Y,
    string? RequestId = null,
    IReadOnlyList<string>? Buttons = null,
    int DurationTicks = 1,
    string? UiRevision = null,
    ReviewInputQuery? Gesture = null,
    ReviewInputQuery? TextQuery = null);

internal static class ReviewInputArguments
{
    internal const string Usage =
        "Usage: sdvkit input press <SButton|MouseWheelUp|MouseWheelDown> | sdvkit input chord <ticks> <ui-revision> <SButton...> | sdvkit input cursor <ui-x> <ui-y> | sdvkit input cursor clear | sdvkit input text <ui-revision> <field-id> <UTF-8-base64>";

    public static bool TryParse(
        IReadOnlyList<string>? arguments,
        out ReviewInputRequest? request,
        out string error)
    {
        request = null;
        error = Usage;
        if (arguments is null
            || arguments.Count < 3
            || !string.Equals(arguments[0], "input", StringComparison.Ordinal))
        {
            return false;
        }

        var actionIndex = 1;
        string? requestId = null;
        if (arguments.Count >= 5
            && string.Equals(arguments[1], "request", StringComparison.Ordinal)
            && ReviewTransportToken.IsRequestId(arguments[2]))
        {
            requestId = arguments[2];
            actionIndex = 3;
        }

        if (arguments[actionIndex] == "text" && arguments.Count == actionIndex + 4
            && ReviewInputContract.IsUiRevision(arguments[actionIndex + 1])
            && long.TryParse(arguments[actionIndex + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long fieldId)
            && fieldId > 0 && arguments[actionIndex + 3].Length <= 1024)
        {
            try
            {
                string text = new System.Text.UTF8Encoding(false, true).GetString(Convert.FromBase64String(arguments[actionIndex + 3]));
                if (ReviewInputContract.ValidateText(text) is null)
                    request = new(ReviewInputKind.Text, null, 0, 0, requestId,
                        TextQuery: new(ReviewInputContract.TextAction, null, null, null, null,
                            UiRevision: arguments[actionIndex + 1], Text: text, FieldId: fieldId));
            }
            catch (Exception exception) when (exception is FormatException or System.Text.DecoderFallbackException) { }
        }
        else if (ReviewInputContract.IsGesture(arguments[actionIndex]))
        {
            string action = arguments[actionIndex];
            string[] values = arguments.Skip(actionIndex + 1).ToArray();
            ReviewInputQuery? gesture = ReviewInputContract.ParseGesture(action, values);
            if (gesture is not null && ReviewInputContract.ValidGesture(gesture))
                request = new(ReviewInputKind.Gesture, gesture.Button, gesture.X!.Value, gesture.Y!.Value,
                    requestId, UiRevision: gesture.UiRevision, Gesture: gesture);
        }
        else if (arguments.Count >= actionIndex + 4
            && arguments[actionIndex] == "chord"
            && int.TryParse(arguments[actionIndex + 1], NumberStyles.None,
                CultureInfo.InvariantCulture, out int duration)
            && duration is >= 1 and <= 120
            && ReviewInputContract.IsUiRevision(arguments[actionIndex + 2]))
        {
            string[] buttons = arguments.Skip(actionIndex + 3).ToArray();
            if (buttons.Length is >= 1 and <= 8
                && buttons.All(b => IsValidButtonToken(b) && !IsMouseWheelToken(b)
                    && !string.Equals(b, "None", StringComparison.OrdinalIgnoreCase))
                && buttons.Distinct(StringComparer.OrdinalIgnoreCase).Count() == buttons.Length)
            {
                request = new(ReviewInputKind.Chord, null, 0, 0, requestId, buttons, duration,
                    arguments[actionIndex + 2]);
            }
        }
        else if (arguments.Count == actionIndex + 2
            && string.Equals(arguments[actionIndex], "press", StringComparison.Ordinal)
            && IsValidButtonToken(arguments[actionIndex + 1])
            && (requestId is null || !IsMouseWheelToken(arguments[actionIndex + 1])))
        {
            request = new ReviewInputRequest(
                IsMouseWheelToken(arguments[actionIndex + 1])
                    ? ReviewInputKind.Scroll
                    : ReviewInputKind.Press,
                arguments[actionIndex + 1],
                0,
                0,
                requestId);
        }
        else if (requestId is not null
            && arguments.Count == actionIndex + 2
            && string.Equals(arguments[actionIndex], "wheel", StringComparison.Ordinal)
            && arguments[actionIndex + 1] is "up" or "down")
        {
            request = new ReviewInputRequest(
                ReviewInputKind.Scroll,
                string.Equals(arguments[actionIndex + 1], "up", StringComparison.Ordinal)
                    ? "MouseWheelUp"
                    : "MouseWheelDown",
                0,
                0,
                requestId);
        }
        else if (arguments.Count == actionIndex + 3
            && string.Equals(arguments[actionIndex], "cursor", StringComparison.Ordinal)
            && TryParseCoordinate(arguments[actionIndex + 1], out int x)
            && TryParseCoordinate(arguments[actionIndex + 2], out int y))
        {
            request = new ReviewInputRequest(
                ReviewInputKind.Cursor,
                null,
                x,
                y,
                requestId);
        }
        else if (arguments.Count == actionIndex + 2
            && string.Equals(arguments[actionIndex], "cursor", StringComparison.Ordinal)
            && string.Equals(arguments[actionIndex + 1], "clear", StringComparison.Ordinal))
        {
            request = new ReviewInputRequest(
                ReviewInputKind.ClearCursor,
                null,
                0,
                0,
                requestId);
        }

        return request is not null;
    }

    public static bool IsValidButtonToken(string? value) =>
        value is not null
        && value.Length is >= 1 and <= 64
        && value.All(character =>
            (character >= 'a' && character <= 'z')
            || (character >= 'A' && character <= 'Z')
            || (character >= '0' && character <= '9'));

    public static bool IsMouseWheelToken(string? value) =>
        string.Equals(value, "MouseWheelUp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseWheelDown", StringComparison.OrdinalIgnoreCase);

    public static bool IsMouseButtonToken(string? value) =>
        string.Equals(value, "MouseLeft", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseRight", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseMiddle", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseX1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "MouseX2", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseCoordinate(string value, out int coordinate) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out coordinate);
}

internal interface IReviewInputRuntime
{
    int UiWidth { get; }

    int UiHeight { get; }

    int GameTick => 0;

    bool InputAdapterReady => false;

    bool CursorSet => false;

    bool MenuOpen => false;
    int? CursorX => null;
    int? CursorY => null;

    bool TryPress(string button, out string canonicalButton, out string error);

    bool TryChord(ReviewInputRequest request, Action<ReviewInputResult> completed,
        out string error)
    { error = "Chord input is unavailable."; return false; }

    bool TryWorldPress(ReviewInputRequest request, Func<string?> validateDispatch,
        Action<ReviewInputResult> completed, out string error)
    { error = "Validated world input is unavailable."; return false; }

    bool TryText(ReviewInputRequest request, Action<ReviewInputResult> completed, out string error)
    { error = "Text input is unavailable."; return false; }

    bool TryScroll(int direction, out string error);

    bool TrySetCursor(int x, int y, out string error);

    bool TryClearCursor(out string error);
}

internal sealed record ReviewInputResult(
    bool Succeeded,
    string Message,
    string? CanonicalButton = null,
    string? ProblemCode = null,
    IReadOnlyList<string>? Buttons = null,
    int? StartTick = null,
    int? EndTick = null,
    bool? Released = null,
    int? CompletedSteps = null,
    int? FinalX = null,
    int? FinalY = null,
    int? DeliveredScalars = null);

// Advances only after the normal dispatcher poll, with no prequeued remainder.
internal sealed class ReviewTextProgress
{
    private readonly string _text;
    private bool _inFlight;
    public ReviewTextProgress(string text)
    {
        if (ReviewInputContract.ValidateText(text) is not null) throw new ArgumentException("Unsupported text.", nameof(text));
        _text = text;
    }
    public int Delivered { get; private set; }
    public bool Stopped { get; private set; }
    public bool Complete => Delivered == _text.Length;
    public bool TryNext(bool current, bool concurrent, out char character)
    {
        character = default;
        if (!current || concurrent) Stop();
        if (Stopped || Complete || _inFlight) return false;
        character = _text[Delivered];
        _inFlight = true;
        return true;
    }
    public void ObservePoll(bool completed)
    {
        if (!_inFlight) throw new InvalidOperationException("No character event is pending.");
        _inFlight = false;
        if (completed) Delivered++;
        else Stop();
    }
    public void Stop() => Stopped = true;
}

// The existing adapter's mouse values, before SMAPI derives its helpers and events.
internal sealed class ReviewVirtualMouseState
{
    private int? _uiX;
    private int? _uiY;
    private int _wheelOffset;
    private int _pendingWheel;
    private int _physicalWheel;
    private bool _hasPhysicalWheelSample;
    private int _lastWheel;

    public bool IsSet => _uiX is not null && _uiY is not null;
    public int? X => _uiX;
    public int? Y => _uiY;
    public int LastWheel => _lastWheel;
    public long WheelSample { get; private set; }
    public bool HasPendingWheel => _pendingWheel != 0;
    public bool HasWheelOffset => _wheelOffset != 0;
    public void CancelWheel() => _pendingWheel = 0;

    public int ApplyWheelOrigin(int physicalWheel)
    {
        long neutral = (long)physicalWheel + _wheelOffset;
        return neutral is >= int.MinValue and <= int.MaxValue ? (int)neutral : _lastWheel;
    }

    public void Set(int x, int y)
    {
        _uiX = x;
        _uiY = y;
    }

    public bool TryQueueWheel(int direction)
    {
        long nextOffset = (long)_wheelOffset + direction;
        long nextValue = _physicalWheel + nextOffset;
        if (!IsSet || !_hasPhysicalWheelSample || direction is not (120 or -120) || _pendingWheel != 0
            || nextOffset is < int.MinValue or > int.MaxValue
            || nextValue is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        _pendingWheel = direction;
        return true;
    }

    public (int X, int Y, int Wheel) Apply(
        int physicalX, int physicalY, int physicalWheel,
        int uiWidth, int uiHeight, float uiScale, out bool wheelRejected, bool consumeWheel = true)
    {
        _physicalWheel = physicalWheel;
        _hasPhysicalWheelSample = true;
        long neutral = (long)physicalWheel + _wheelOffset;
        int pending = consumeWheel ? _pendingWheel : 0;
        long requested = neutral + pending;
        wheelRejected = requested is < int.MinValue or > int.MaxValue;
        // Consume at the common input sample, never at a game/helper read afterwards.
        if (!wheelRejected)
        {
            _wheelOffset += pending;
        }
        if (consumeWheel)
        {
            if (pending != 0 && !wheelRejected) WheelSample++;
            _pendingWheel = 0;
        }
        // An external counter jump may also make the consumed origin unrepresentable.
        // Retain the last output then; resetting the origin would replay an opposite delta.
        int wheel = wheelRejected ? ApplyWheelOrigin(physicalWheel) : (int)requested;
        _lastWheel = wheel;
        if (_uiX is not int x || _uiY is not int y)
        {
            return (physicalX, physicalY, wheel);
        }

        x = Math.Clamp(x, 0, Math.Max(0, uiWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, uiHeight - 1));
        return (
            (int)Math.Round(x * uiScale, MidpointRounding.AwayFromZero),
            (int)Math.Round(y * uiScale, MidpointRounding.AwayFromZero),
            wheel);
    }

    public void Clear()
    {
        _uiX = null;
        _uiY = null;
        _pendingWheel = 0;
        // Keep the neutral cumulative origin: resetting it would emit a reverse notch.
    }
}

// A synchronous player update owns publication, not the shorter input-builder call.
// Reference identity matters: a replacement player/input on the same screen is not its owner.
internal sealed class ReviewMouseSampleScope(
    object game, object input, object player, int screen, string launch, string? role) : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _closed;

    public bool IsCurrent(object? currentGame, object? currentInput, object? currentPlayer,
        int currentScreen, string? currentLaunch, string? currentRole, bool reviewActive) =>
        !_closed && reviewActive && _thread == Environment.CurrentManagedThreadId
        && ReferenceEquals(game, currentGame) && ReferenceEquals(input, currentInput)
        && ReferenceEquals(player, currentPlayer) && screen == currentScreen
        && !string.IsNullOrWhiteSpace(launch) && launch == currentLaunch && role == currentRole;

    public void Dispose() => _closed = true;
}

[Flags]
internal enum ReviewMouseButtons { None = 0, Left = 1, Middle = 2, Right = 4, X1 = 8, X2 = 16 }

internal readonly record struct ReviewMouseValues(int X, int Y, int Wheel, ReviewMouseButtons Buttons);

// A cache of the completed SMAPI sample, not another chord/gesture progression.
internal sealed class ReviewNativeMousePublication(ReviewVirtualMouseState mouse)
{
    [ThreadStatic] private static int _hardwareReads;
    private sealed class HardwareRead : IDisposable
    {
        private bool _disposed;
        public HardwareRead() => _hardwareReads++;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hardwareReads--;
        }
    }
    private ReviewMouseValues? _sample;
    private ReviewMouseButtons _buttons;
    private bool _invalidated;
    public bool Published { get; private set; }
    public static bool ReadingHardware => _hardwareReads != 0;
    public static IDisposable BeginHardwareRead() => new HardwareRead();

    public void BeginInputSample()
    {
        _sample = null;
        _buttons = ReviewMouseButtons.None;
        _invalidated = false;
        Published = false;
    }

    public void Publish(ReviewMouseValues sample, ReviewMouseButtons ownedButtons)
    {
        if (_invalidated) return;
        _sample = sample;
        _buttons = ownedButtons;
        Published = true;
    }

    public void Invalidate()
    {
        _sample = null;
        _buttons = ReviewMouseButtons.None;
        _invalidated = true;
    }

    public static ReviewMouseValues ReadHardware(Func<ReviewMouseValues> read)
    {
        using (BeginHardwareRead()) return read();
    }

    public ReviewMouseValues Read(ReviewMouseValues hardware, ReviewMouseButtons suppressed,
        bool current, out bool overlap)
    {
        overlap = false;
        if (ReadingHardware || !current) return hardware;
        if (_sample is { } sample)
        {
            overlap = ((hardware.Buttons | suppressed) & _buttons) != 0;
            if (!overlap)
                return new(mouse.IsSet ? sample.X : hardware.X, mouse.IsSet ? sample.Y : hardware.Y,
                    sample.Wheel, hardware.Buttons | (sample.Buttons & _buttons));
            Invalidate();
        }
        // Unpublished and invalidated samples expose no owned coordinates/buttons.
        // Keep only the consumed wheel origin, including cancellation before Publish.
        // Reading cannot consume or cancel another notch.
        return hardware with { Wheel = mouse.ApplyWheelOrigin(hardware.Wheel) };
    }
}

// Counts completed input samples, never wall-clock or public game-loop events.
internal sealed class ReviewChordProgress
{
    public ReviewChordProgress(int duration, ReviewInputQuery? gesture = null)
    {
        if (gesture is null && duration is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(duration));
        Gesture = gesture;
        Remaining = gesture?.Action switch
        {
            ReviewInputContract.ClickAction => 1,
            ReviewInputContract.ScrollAction => Math.Abs(gesture.Notches!.Value),
            ReviewInputContract.DragAction => gesture.DurationTicks!.Value + 2,
            _ => duration,
        };
    }
    public ReviewInputQuery? Gesture { get; }
    public int CompletedSteps { get; private set; }
    public int EdgeSamples { get; private set; }
    public bool MoreActions => Remaining > 0 || (!Canceled && Gesture?.Action == ReviewInputContract.ClickAction && CompletedSteps + 1 < Gesture.Count);
    public bool AwaitingGameUpdate { get; private set; }
    public bool PreparingCursor => !Canceled && !_cursorPrepared
        && Gesture?.Action is ReviewInputContract.ClickAction or ReviewInputContract.DragAction;
    public bool AwaitingCursorUpdate => AwaitingGameUpdate && _samplePreparingCursor;
    private bool _cursorPrepared;
    private bool _samplePreparingCursor;
    private bool _sampleReleased;
    private bool _sampleSucceeded;
    private int _sampleTick;
    private int _failedInputSamples;
    public void ObserveGestureSample(int tick, bool released, bool succeeded, bool preparingCursor = false)
    {
        if (Gesture is null || AwaitingGameUpdate) throw new InvalidOperationException("No gesture sample can be observed.");
        if (succeeded && !released && PreparingCursor && !preparingCursor)
            throw new InvalidOperationException("The cursor must complete a game update before button-down.");
        _sampleTick = tick;
        _sampleReleased = released;
        _sampleSucceeded = succeeded;
        _samplePreparingCursor = preparingCursor;
        if (!released && succeeded && !preparingCursor) StartTick ??= tick;
        AwaitingGameUpdate = true;
    }
    public void ObserveFailedInputSample(int tick)
    {
        Cancel();
        ObserveGestureSample(tick, ++_failedInputSamples >= 2, false);
    }
    public bool MissingGameUpdate()
    {
        if (!AwaitingGameUpdate) return false;
        Cancel();
        AwaitingGameUpdate = false;
        return _sampleReleased && !_samplePreparingCursor;
    }
    public bool CompleteGameUpdate(out bool released)
    {
        released = _sampleSucceeded && _sampleReleased && !_samplePreparingCursor;
        if (!AwaitingGameUpdate) return false;
        AwaitingGameUpdate = false;
        if (_samplePreparingCursor)
        {
            // Historical-coordinate consumers see this position on the next update.
            // Preparation owns no button edge and cannot survive cancellation.
            _cursorPrepared = _sampleSucceeded && !Canceled;
            return false;
        }
        if (!_sampleReleased)
        {
            if (_sampleSucceeded) Consumed(_sampleTick, Canceled);
            return false;
        }
        Released(_sampleTick, _sampleSucceeded);
        return Finished;
    }
    public (int X, int Y)? Position => Gesture is null ? null
        : Gesture.Action != ReviewInputContract.DragAction ? (Gesture.X!.Value, Gesture.Y!.Value)
        : ((int)Math.Round(Gesture.X!.Value + ((double)Gesture.EndX!.Value - Gesture.X.Value) * Math.Min(EdgeSamples, Gesture.DurationTicks!.Value) / Gesture.DurationTicks.Value, MidpointRounding.AwayFromZero),
           (int)Math.Round(Gesture.Y!.Value + ((double)Gesture.EndY!.Value - Gesture.Y.Value) * Math.Min(EdgeSamples, Gesture.DurationTicks.Value) / Gesture.DurationTicks.Value, MidpointRounding.AwayFromZero));
    public int Remaining { get; private set; }
    public int? StartTick { get; private set; }
    public int? EndTick { get; private set; }
    public bool Canceled { get; private set; }
    public bool Finished => EndTick is not null;
    public void Cancel() { Canceled = true; Remaining = 0; }
    public void Consumed(int tick, bool canceledGestureSample = false)
    {
        if (PreparingCursor || Finished || ((Canceled || Remaining == 0) && !(canceledGestureSample && Gesture is not null))) throw new InvalidOperationException("No input sample was requested.");
        StartTick ??= tick;
        EdgeSamples++;
        if (!Canceled) Remaining--;
        if (Gesture?.Action == ReviewInputContract.ScrollAction) CompletedSteps++;
        if (Gesture?.Action == ReviewInputContract.DragAction)
            CompletedSteps = Math.Min(Gesture.DurationTicks!.Value, Math.Max(0, EdgeSamples - 1));
    }
    public void Released(int tick, bool observed = true)
    {
        if (Remaining != 0 || Finished) throw new InvalidOperationException("The chord still owns input samples.");
        if (observed && EdgeSamples > 0 && Gesture?.Action == ReviewInputContract.ClickAction)
        {
            CompletedSteps++;
            if (!Canceled && CompletedSteps < Gesture.Count)
            {
                Remaining = 1;
                EdgeSamples = 0;
                return;
            }
        }
        EndTick = tick;
    }
}

// Own only additions made within one synchronous SMAPI sample.
internal sealed class ReviewOwnedButtonSample
{
    private readonly HashSet<int> _added = new();
    public bool Injected { get; private set; }
    public bool TryInject(IReadOnlyList<int> buttons, Func<int, bool> queued, Action<int> press)
    {
        if (buttons.Any(queued)) return false;
        foreach (int button in buttons)
        {
            _added.Add(button);
            press(button);
        }
        Injected = true;
        return true;
    }
    public bool IsConsumed(Func<int, bool> queued) => _added.All(b => !queued(b));
    public void Rollback(Action<int> remove)
    {
        foreach (int button in _added) remove(button);
        _added.Clear();
    }
}

internal static class ReviewInputResponseFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(
        string runtimePath,
        ReviewInputResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("The review runtime path is required.", nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string responsePath = ReviewInputContract.ResponsePath(
            Path.GetFullPath(runtimePath),
            envelope.RequestId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > ReviewInputContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-input response exceeds its maximum size.");
        }
        ReviewResponseFile.Write(responsePath, bytes);
    }
}

internal static class ReviewInputOperation
{
    public static ReviewInputResult Execute(
        ReviewInputRequest request,
        IReviewInputRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        if (request.Kind == ReviewInputKind.ClearCursor)
        {
            return runtime.TryClearCursor(out string clearError)
                ? new ReviewInputResult(true, "Cleared the virtual review cursor.")
                : Failure("inputClearFailed", clearError);
        }

        if (request.Kind == ReviewInputKind.Press)
        {
            if (!ReviewInputArguments.IsValidButtonToken(request.Button))
            {
                return Failure("inputArgumentsInvalid", ReviewInputArguments.Usage);
            }

            if (!runtime.InputAdapterReady)
            {
                return Failure(
                    "inputAdapterUnavailable",
                    "The process-local background input adapter is not installed and ready.");
            }

            if (ReviewInputArguments.IsMouseButtonToken(request.Button)
                && !runtime.CursorSet)
            {
                return Failure(
                    "inputCursorMissing",
                    "Set the virtual review cursor before pressing a mouse button.");
            }

            return runtime.TryPress(request.Button!, out string canonicalButton, out string error)
                ? new ReviewInputResult(
                    true,
                    $"Pressed review input '{canonicalButton}' for one input tick.",
                    canonicalButton)
                : Failure("inputButtonUnsupported", error, request.Button);
        }

        if (request.Kind == ReviewInputKind.Scroll)
        {
            if (!runtime.InputAdapterReady)
            {
                return Failure(
                    "inputAdapterUnavailable",
                    "The process-local background input adapter is not installed and ready.");
            }
            if (!runtime.CursorSet)
            {
                return Failure(
                    "inputCursorMissing",
                    "Set the virtual review cursor before sending mouse-wheel input.");
            }
            if (!runtime.MenuOpen)
            {
                return Failure(
                    "inputMenuMissing",
                    "Mouse-wheel review input requires an active game menu.");
            }

            int direction = string.Equals(
                request.Button,
                "MouseWheelUp",
                StringComparison.OrdinalIgnoreCase)
                ? 120
                : -120;
            string canonicalButton = direction > 0 ? "MouseWheelUp" : "MouseWheelDown";
            return runtime.TryScroll(direction, out string error)
                ? new ReviewInputResult(
                    true,
                    $"Pressed review input '{canonicalButton}' for one mouse-wheel notch.",
                    canonicalButton)
                : Failure("inputWheelRejected", error);
        }

        if (request.Kind != ReviewInputKind.Cursor
            || request.X < 0
            || request.Y < 0
            || request.X >= runtime.UiWidth
            || request.Y >= runtime.UiHeight)
        {
            return Failure(
                "inputCursorOutOfBounds",
                $"Review cursor coordinates must be inside the current UI viewport "
                + $"{runtime.UiWidth}x{runtime.UiHeight}.");
        }

        if (!runtime.InputAdapterReady)
        {
            return Failure(
                "inputAdapterUnavailable",
                "The process-local background input adapter is not installed and ready.");
        }

        return runtime.TrySetCursor(request.X, request.Y, out string cursorError)
            ? new ReviewInputResult(
                true,
                $"Set the virtual review cursor to UI coordinate {request.X},{request.Y}; the physical pointer was not moved.")
            : Failure("inputCursorUnavailable", cursorError);
    }

    private static ReviewInputResult Failure(
        string code,
        string message,
        string? canonicalButton = null) =>
        new(false, message, canonicalButton, code);
}

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewInputCommand
{
    public static void Handle(
        string[] arguments,
        IReviewInputRuntime runtime,
        string runtimePath,
        IMonitor monitor)
    {
        if (arguments.Length == 3 && arguments[0] == "input" && arguments[1] == "cancel"
            && ReviewTransportToken.IsRequestId(arguments[2]))
        {
            ReviewVirtualCursor.CancelRequest(arguments[2]);
            return;
        }
        if (!ReviewInputArguments.TryParse(
                arguments,
                out ReviewInputRequest? request,
                out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        try
        {
            if (request!.Kind == ReviewInputKind.Text)
            {
                if (!runtime.TryText(request, Complete, out string textError))
                    Complete(new(false, textError, ProblemCode: "inputTextRejected", DeliveredScalars: 0));
                return;
            }
            if (request!.Kind is ReviewInputKind.Chord or ReviewInputKind.Press or ReviewInputKind.Gesture)
            {
                if (!runtime.TryChord(request, Complete, out string chordError))
                    Complete(new(false, chordError, ProblemCode: "inputChordRejected",
                        Buttons: request.Buttons, Released: false));
                return;
            }
            ReviewInputResult result = ReviewInputOperation.Execute(request!, runtime);
            if (request!.RequestId is not null)
            {
                ReviewInputResponseFile.Write(
                    runtimePath,
                    CreateResponse(request, runtime, result));
            }
            monitor.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }

        catch (Exception exception)
        {
            if (request!.Kind == ReviewInputKind.Gesture)
            {
                if (!ReviewVirtualCursor.CancelGestureRequest(request.RequestId) && request.RequestId is not null)
                    TryWriteFailureResponse(request, runtimePath, runtime);
                monitor.Log($"SDVKit gesture failed without confirming input: {exception.Message}", LogLevel.Error);
                return;
            }
            if (request!.Kind != ReviewInputKind.ClearCursor)
            {
                try
                {
                    if (!runtime.TryClearCursor(out string cleanupError))
                    {
                        monitor.Log(cleanupError, LogLevel.Error);
                    }
                }
                catch (Exception cleanupException)
                {
                    monitor.Log($"Pending review input could not be canceled; the cursor was retained: {cleanupException.Message}", LogLevel.Error);
                }
            }
            if (request.RequestId is not null)
            {
                TryWriteFailureResponse(request, runtimePath, runtime);
            }
            monitor.Log(
                $"SDVKit input command failed without confirming input: {exception.Message}",
                LogLevel.Error);
        }
        void Complete(ReviewInputResult result)
        {
            try
            {
                if (request!.RequestId is not null)
                    ReviewInputResponseFile.Write(runtimePath, CreateResponse(request, runtime, result));
                monitor.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Error);
            }
            catch (Exception exception)
            {
                monitor.Log($"Review input completion could not be published: {exception.Message}", LogLevel.Error);
            }
        }

    }

    private static ReviewInputResponseEnvelope CreateResponse(
        ReviewInputRequest request,
        IReviewInputRuntime runtime,
        ReviewInputResult result)
    {
        string action = request.Kind switch
        {
            ReviewInputKind.Text => ReviewInputContract.TextAction,
            ReviewInputKind.Press => ReviewInputContract.PressAction,
            ReviewInputKind.Chord => ReviewInputContract.ChordAction,
            ReviewInputKind.Gesture => request.Gesture!.Action,
            ReviewInputKind.Scroll => ReviewInputContract.WheelAction,
            ReviewInputKind.Cursor => ReviewInputContract.CursorSetAction,
            ReviewInputKind.ClearCursor => ReviewInputContract.CursorClearAction,
            _ => throw new InvalidOperationException(
                "The review-input action is unsupported."),
        };
        string? direction = request.Kind == ReviewInputKind.Scroll
            ? string.Equals(request.Button, "MouseWheelUp", StringComparison.OrdinalIgnoreCase)
                ? "up"
                : "down"
            : null;
        return new ReviewInputResponseEnvelope(
            ReviewInputContract.SchemaVersion,
            request.RequestId!,
            DateTimeOffset.UtcNow,
            runtime.GameTick,
            action,
            result.Succeeded,
            request.Kind is ReviewInputKind.Press or ReviewInputKind.Gesture
                ? result.CanonicalButton ?? request.Button
                : null,
            direction,
            request.Kind is ReviewInputKind.Cursor or ReviewInputKind.Gesture ? request.X : null,
            request.Kind is ReviewInputKind.Cursor or ReviewInputKind.Gesture ? request.Y : null,
            runtime.CursorSet,
            runtime.MenuOpen,
            result.Succeeded
                ? null
                : new ReviewInputProblem(
                    result.ProblemCode ?? "inputRejected",
                    result.Message.Length <= ReviewInputContract.MaximumProblemLength
                        ? result.Message
                        : result.Message[..ReviewInputContract.MaximumProblemLength]),
            request.Kind == ReviewInputKind.Chord ? result.Buttons ?? request.Buttons : null,
            request.Kind == ReviewInputKind.Chord ? request.DurationTicks : request.Gesture?.DurationTicks,
            request.Kind is ReviewInputKind.Chord or ReviewInputKind.Gesture ? result.StartTick : null,
            request.Kind is ReviewInputKind.Chord or ReviewInputKind.Gesture ? result.EndTick : null,
            request.Kind is ReviewInputKind.Chord or ReviewInputKind.Gesture ? result.Released ?? false : null,
            request.Gesture is { Action: not ReviewInputContract.ScrollAction } g
                ? (g.Modifiers ?? []).Select(m => ReviewInputContract.Modifier(m)!).ToArray() : null,
            request.Gesture?.Count, request.Gesture?.Notches, request.Gesture?.EndX, request.Gesture?.EndY,
            request.Gesture is not null ? result.CompletedSteps ?? 0 : null,
            request.Gesture is not null && runtime.CursorSet ? result.FinalX ?? runtime.CursorX : null,
            request.Gesture is not null && runtime.CursorSet ? result.FinalY ?? runtime.CursorY : null,
            request.Kind == ReviewInputKind.Text ? result.DeliveredScalars ?? 0 : null);
    }

    private static void TryWriteFailureResponse(
        ReviewInputRequest request,
        string runtimePath,
        IReviewInputRuntime runtime)
    {
        try
        {
            ReviewInputResponseFile.Write(
                runtimePath,
                CreateResponse(
                    request,
                    runtime,
                    new ReviewInputResult(
                        false,
                        "The bounded review-input action failed before acknowledgement.",
                        ProblemCode: "inputRejected")));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException)
        {
            // A failed create-new acknowledgement must never be retried or replaced.
        }
    }
}

internal sealed class StardewReviewInputRuntime(IModHelper helper, Func<string?> currentRevision, Func<string?> currentContinuity, Func<string?> currentViewport, Func<long, bool> currentTextField) : IReviewInputRuntime
{
    public int UiWidth => Game1.uiViewport.Width;

    public int UiHeight => Game1.uiViewport.Height;

    public int GameTick => Game1.ticks;

    public bool InputAdapterReady => ReviewVirtualCursor.IsInstalled;

    public bool CursorSet => ReviewVirtualCursor.IsSet;

    public bool MenuOpen => Game1.activeClickableMenu is not null;
    public int? CursorX => ReviewVirtualCursor.Position.X;
    public int? CursorY => ReviewVirtualCursor.Position.Y;

    public bool TryPress(string button, out string canonicalButton, out string error)
    {
        if (!ReviewVirtualCursor.IsInstalled)
        {
            canonicalButton = string.Empty;
            error = "The process-local background input adapter is not installed and ready.";
            return false;
        }

        if (ReviewInputArguments.IsMouseButtonToken(button)
            && !ReviewVirtualCursor.IsSet)
        {
            canonicalButton = string.Empty;
            error = "Set the virtual review cursor before pressing a mouse button.";
            return false;
        }

        if (!Enum.TryParse(button, ignoreCase: true, out SButton parsed)
            || !Enum.IsDefined(parsed)
            || parsed == SButton.None)
        {
            canonicalButton = string.Empty;
            error = $"'{button}' is not one exact SMAPI SButton name.";
            return false;
        }

        canonicalButton = parsed.ToString();
        return ReviewVirtualCursor.TryChord(helper.Input, [parsed], 1, null, currentRevision,
            _ => { }, out error, null);
    }

    public bool TryText(ReviewInputRequest request, Action<ReviewInputResult> completed, out string error) =>
        ReviewVirtualCursor.TryText(request, currentRevision, currentTextField, completed, out error);

    public bool TryChord(ReviewInputRequest request, Action<ReviewInputResult> completed,
        out string error)
    {
        if (request.Gesture is ReviewInputQuery gesture)
        {
            error = "The gesture arguments, coordinates or current UI revision are invalid.";
            if (!MenuOpen || !ReviewInputContract.ValidGesture(gesture) || gesture.X >= UiWidth || gesture.Y >= UiHeight
                || gesture.EndX >= UiWidth || gesture.EndY >= UiHeight) return false;
            SButton[] gestureButtons = gesture.Action == ReviewInputContract.ScrollAction ? []
                : new[] { ReviewInputContract.MouseButton(gesture.Button)! }
                    .Concat((gesture.Modifiers ?? []).Select(m => ReviewInputContract.Modifier(m)!))
                    .Select(Enum.Parse<SButton>).ToArray();
            return ReviewVirtualCursor.TryChord(helper.Input, gestureButtons, 1, gesture.UiRevision,
                currentRevision, result => completed(result with { CanonicalButton = gestureButtons.Length > 0 ? gestureButtons[0].ToString() : null }),
                out error, request.RequestId, gesture, currentContinuity, currentViewport);
        }
        error = "A chord requires 1-8 distinct exact SMAPI button names, 1-120 input ticks and a current UI revision.";
        bool chord = request.Kind == ReviewInputKind.Chord;
        IReadOnlyList<string>? names = chord ? request.Buttons : request.Button is null ? null : [request.Button];
        if (names is not { Count: >= 1 and <= 8 }
            || request.DurationTicks is < 1 or > 120
            || chord && !ReviewInputContract.IsUiRevision(request.UiRevision)) return false;
        var buttons = new List<SButton>();
        foreach (string token in names)
        {
            string? name = Enum.GetNames<SButton>().FirstOrDefault(n =>
                string.Equals(n, token, StringComparison.OrdinalIgnoreCase));
            if (name is null || !Enum.TryParse(name, out SButton button) || button == SButton.None
                || buttons.Contains(button)) return false;
            buttons.Add(button);
        }
        return ReviewVirtualCursor.TryChord(helper.Input, buttons.ToArray(), request.DurationTicks,
            request.UiRevision, currentRevision,
            result => completed(result with { CanonicalButton = result.Buttons?[0] }), out error, request.RequestId);
    }

    public bool TryWorldPress(ReviewInputRequest request, Func<string?> validateDispatch,
        Action<ReviewInputResult> completed, out string error)
    {
        if (request.Kind != ReviewInputKind.Press || request.Button is null
            || !Enum.TryParse(request.Button, out SButton button)
            || button is not (SButton.MouseLeft or SButton.MouseRight))
        {
            error = "A world interaction requires exactly one native mouse action.";
            return false;
        }
        return ReviewVirtualCursor.TryChord(helper.Input, [button], 1, null, currentRevision,
            result => completed(result with { CanonicalButton = result.Buttons?[0] }), out error,
            request.RequestId, validateDispatch: validateDispatch);
    }

    public bool TryScroll(int direction, out string error)
    {
        if (!ReviewVirtualCursor.IsSet)
        {
            error = "Set the virtual review cursor before sending mouse-wheel input.";
            return false;
        }

        if (Game1.activeClickableMenu is null)
        {
            error = "Mouse-wheel review input requires an active game menu.";
            return false;
        }

        return ReviewVirtualCursor.TryScroll(direction, out error);
    }

    public bool TrySetCursor(int x, int y, out string error)
    {
        return ReviewVirtualCursor.TrySet(x, y, out error);
    }

    public bool TryClearCursor(out string error)
    {
        try
        {
            ReviewVirtualCursor.Clear();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Pending review input could not be canceled; the cursor was retained: {exception.Message}";
            if (error.Length > ReviewInputContract.MaximumProblemLength)
            {
                error = error[..ReviewInputContract.MaximumProblemLength];
            }
            return false;
        }
    }
}

internal static partial class ReviewVirtualCursor
{
    private sealed class ScreenInputState
    {
        public ReviewVirtualMouseState Mouse { get; set; } = new();
        public ReviewMouseSampleScope? NativeMouseBinding { get; set; }
        public InputSample? ActiveSample { get; set; }
        public bool WheelFailureReported { get; set; }
        public InputState? InputOwner { get; set; }
        public int BackgroundInputThroughTick { get; set; } = -1;
        public PendingInput? Pending { get; set; }
        public PendingText? Text { get; set; }
    }
    private static readonly PerScreen<ScreenInputState> Screen = new(() => new());

    private const string HarmonyId = "SDVKit.AlwaysOn.VirtualReviewCursor";

    private static readonly object Sync = new();
    private static bool _installed;
    private sealed record PendingInput(IInputHelper Helper, SButton[] Buttons, ReviewChordProgress Progress,
        string? Revision, Func<string?> CurrentRevision, Action<ReviewInputResult> Completed,
        object? Root, object? Player, string? Launch, string? Role, string? RequestId)
    {
        public TextTarget? CommandTarget { get; init; }
        public Keys CommandKey { get; init; }
        public bool CommandDelivered { get; set; }
        public string? Failure { get; set; }
        public Func<string?>? CurrentContinuity { get; init; }
        public string? Continuity { get; init; }
        public Func<string?>? CurrentViewport { get; init; }
        public string? Viewport { get; init; }
        public Func<string?>? ValidateDispatch { get; init; }
    }
    private sealed class InputSample
    {
        public bool PreparingCursor { get; set; }
        public (int X, int Y)? CursorPosition { get; set; }
        public object? PreviousStates { get; init; }
        public ReviewOwnedButtonSample Owned { get; } = new();
        public long WheelSample { get; set; }
        public int? WheelBefore { get; set; }
        public int WheelDelta { get; set; }
    }
    private static PropertyInfo? _buttonStates;
    private static FieldInfo? _pressedKeys;
    private static IMonitor? _monitor;

    public static (int? X, int? Y) Position
    {
        get { lock (Sync) { EnsureInputOwner(); return (Screen.Value.Mouse.X, Screen.Value.Mouse.Y); } }
    }

    public static bool IsSet
    {
        get
        {
            lock (Sync)
            {
                EnsureInputOwner();
                return Screen.Value.Mouse.IsSet;
            }
        }
    }

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static bool TryInstall(IMonitor monitor, out string error)
    {
        lock (Sync)
        {
            if (_installed)
            {
                error = string.Empty;
                return true;
            }

            // TrueUpdate reads this base method before building MouseState and CursorPosition.
            MethodInfo? getMouseState = AccessTools.Method(
                typeof(InputState), nameof(InputState.GetMouseState));
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReviewVirtualCursor),
                nameof(AfterGetMouseState));
            Type? smapiInput = AccessTools.TypeByName("StardewModdingAPI.Framework.Input.SInputState");
            MethodInfo? trueUpdate = smapiInput is null ? null : AccessTools.Method(smapiInput, "TrueUpdate");
            Type? smapiGame = AccessTools.TypeByName("StardewModdingAPI.Framework.SGame");
            MethodInfo? gameUpdate = smapiGame is null ? null : AccessTools.DeclaredMethod(
                smapiGame, "Update", [typeof(Microsoft.Xna.Framework.GameTime)]);
            MethodInfo? rawMouse = AccessTools.DeclaredMethod(typeof(Mouse), nameof(Mouse.GetState), Type.EmptyTypes);
            MethodInfo? hardwareCapture = AccessTools.DeclaredMethod(typeof(InputState), nameof(InputState.UpdateStates), Type.EmptyTypes);
            FieldInfo? pressedKeys = smapiInput is null ? null : AccessTools.Field(smapiInput, "CustomPressedKeys");
            PropertyInfo? buttonStates = smapiInput is null ? null : AccessTools.Property(smapiInput, "ButtonStates");
            MethodInfo? inputPrefix = AccessTools.Method(typeof(ReviewVirtualCursor), nameof(BeforeInputUpdate));
            MethodInfo? inputFinalizer = AccessTools.Method(
                typeof(ReviewVirtualCursor), nameof(AfterInputUpdate));
            MethodInfo? isActiveNoOverlay = AccessTools.PropertyGetter(
                typeof(Game1),
                nameof(Game1.IsActiveNoOverlay));
            MethodInfo? isActive = AccessTools.PropertyGetter(
                typeof(Microsoft.Xna.Framework.Game),
                nameof(Microsoft.Xna.Framework.Game.IsActive));
            MethodInfo? activePostfix = AccessTools.Method(
                typeof(ReviewVirtualCursor),
                nameof(AfterGetReviewActivity));
            if (getMouseState is null
                || postfix is null
                || trueUpdate is null
                || trueUpdate.IsStatic
                || trueUpdate.ReturnType != typeof(void)
                || trueUpdate.GetParameters().Length != 0
                || gameUpdate is null || gameUpdate.IsStatic || gameUpdate.ReturnType != typeof(void)
                || rawMouse is null || !rawMouse.IsStatic || rawMouse.ReturnType != typeof(MouseState)
                || hardwareCapture is null || hardwareCapture.IsStatic || hardwareCapture.ReturnType != typeof(void)
                || pressedKeys?.FieldType != typeof(HashSet<SButton>)
                || pressedKeys.IsStatic
                || buttonStates?.PropertyType != typeof(IDictionary<SButton, SButtonState>)
                || buttonStates.GetMethod is null || buttonStates.GetMethod.IsStatic
                || inputPrefix is null
                || inputFinalizer is null
                || isActiveNoOverlay is null
                || isActive is null
                || activePostfix is null)
            {
                error = "A required Stardew or SMAPI input-state method is unavailable; background review input was not enabled.";
                return false;
            }

            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    getMouseState,
                    postfix: new HarmonyMethod(postfix));
                harmony.Patch(
                    trueUpdate,
                    prefix: new HarmonyMethod(inputPrefix),
                    finalizer: new HarmonyMethod(inputFinalizer));
                harmony.Patch(gameUpdate,
                    prefix: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(BeforePlayerUpdate)),
                    finalizer: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(AfterPlayerUpdate)));
                harmony.Patch(rawMouse,
                    postfix: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(AfterGetNativeMouseState)));
                harmony.Patch(hardwareCapture,
                    prefix: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(BeforeHardwareMouseCapture)),
                    finalizer: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(AfterHardwareMouseCapture)));
                harmony.Patch(
                    isActiveNoOverlay,
                    postfix: new HarmonyMethod(activePostfix));
                harmony.Patch(
                    isActive,
                    postfix: new HarmonyMethod(activePostfix));
                _buttonStates = buttonStates;
                _pressedKeys = pressedKeys;
                _monitor = monitor;
                _installed = true;
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                error = $"The process-local background input patch could not be installed: {exception.Message}";
                return false;
            }
        }
    }

    public static bool TrySet(int uiX, int uiY, out string error)
    {
        lock (Sync)
        {
            if (!_installed || Screen.Value.Text is not null || Screen.Value.Pending is not null)
            {
                error = "Virtual cursor input is unavailable because its process-local input patch was not installed.";
                return false;
            }

            EnsureInputOwner();
            Screen.Value.Mouse.Set(uiX, uiY);
            BindNativeMouse();
            AllowBackgroundInputForNextTicks();
            error = string.Empty;
            return true;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            EnsureInputOwner();
            InvalidateNativeMouseSample();
            FinishText("The review input was cleared before completion.");
            CancelChord("The review input was cleared before completion.");
            if (Screen.Value.Pending?.Progress.Gesture is null) Screen.Value.Mouse.Clear();
            AllowBackgroundInputForNextTicks();
        }
    }

    public static bool TryChord(IInputHelper helper, SButton[] buttons, int duration, string? revision,
        Func<string?> currentRevision, Action<ReviewInputResult> completed, out string error, string? requestId,
        ReviewInputQuery? gesture = null, Func<string?>? currentContinuity = null, Func<string?>? currentViewport = null,
        Func<string?>? validateDispatch = null)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            error = "The input adapter is unavailable or another input action is pending.";
            if (!_installed || Screen.Value.Text is not null || Screen.Value.Pending is not null || Screen.Value.Mouse.HasPendingWheel) return false;
            if (gesture is null && buttons.Any(b => ReviewInputArguments.IsMouseButtonToken(b.ToString())) && !Screen.Value.Mouse.IsSet)
            { error = "Set the virtual review cursor before pressing a mouse button."; return false; }
            if (buttons.Any(b => helper.IsDown(b) || helper.IsSuppressed(b))
                || _pressedKeys?.GetValue(Screen.Value.InputOwner) is not HashSet<SButton> queued
                || buttons.Any(queued.Contains))
            { error = "A chord member is already down, suppressed or externally queued."; return false; }
            if (revision is not null && currentRevision() != revision)
            { error = "The UI revision is stale; read the current menu before sending input."; return false; }
            TextTarget? commandTarget = null;
            Keys commandKey = Keys.None;
            if (gesture is null && Game1.activeClickableMenu?.GetType() == typeof(StardewValley.Menus.NamingMenu)
                && buttons.Any(b => b is SButton.Back or SButton.Enter or SButton.Tab))
            {
                error = "Text commands require one unmodified Back, Enter or Tab button for one tick and an exact supported selected field.";
                if (buttons.Length != 1 || duration != 1 || !EnsureTextBinding()
                    || (commandTarget = CaptureTextTarget()) is null || !buttons[0].TryGetKeyboard(out commandKey)) return false;
            }
            Screen.Value.Pending = new(helper, buttons, new(duration, gesture), revision, currentRevision, completed,
                Game1.activeClickableMenu, Game1.player,
                Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"),
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"), requestId)
            {
                CommandTarget = commandTarget,
                CommandKey = commandKey,
                CurrentContinuity = currentContinuity,
                Continuity = currentContinuity?.Invoke(),
                CurrentViewport = currentViewport,
                Viewport = currentViewport?.Invoke(),
                ValidateDispatch = validateDispatch
            };
            if (gesture is not null || buttons.Any(b => ReviewInputArguments.IsMouseButtonToken(b.ToString())))
                BindNativeMouse();
            AllowBackgroundInputForNextTicks();
            error = string.Empty;
            return true;
        }
    }

    internal static void CancelRequest(string requestId)
    {
        lock (Sync)
        {
            if (Screen.Value.Text?.Request.RequestId == requestId) FinishText("The request was canceled by its owner.");
            if (Screen.Value.Pending?.RequestId == requestId) CancelChord("The request was canceled by its owner.");
        }
    }

    internal static bool CancelGestureRequest(string? requestId)
    {
        lock (Sync)
        {
            if (Screen.Value.Pending?.Progress.Gesture is null || Screen.Value.Pending.RequestId != requestId) return false;
            CancelChord("The gesture command failed before completion.");
            return true;
        }
    }

    private static void BeforeInputUpdate(InputState __instance, HashSet<SButton> ___CustomPressedKeys,
        out InputSample? __state)
    {
        __state = null;
        lock (Sync)
        {
            EnsureInputOwner();
            BeginNativeMouseInputSample(__instance);
            ObserveTextLifetime();
            if (Screen.Value.Text is not null) AllowBackgroundInputForNextTicks();
            if (!ReferenceEquals(__instance, Screen.Value.InputOwner) || Screen.Value.Pending is not PendingInput chord) return;
            if (chord.Progress.AwaitingGameUpdate)
            {
                // The prior common input sample has passed its game-update opportunity,
                // but SMAPI skipped the public completion event (e.g. loading/saving).
                CancelChord("The game-update completion callback was not observed.");
                if (chord.Progress.MissingGameUpdate())
                {
                    FinishChord(false);
                    return;
                }
            }
            __state = new() { PreviousStates = _buttonStates!.GetValue(__instance), WheelSample = Screen.Value.Mouse.WheelSample };
            Screen.Value.ActiveSample = __state;
            try
            {
                AllowBackgroundInputForNextTicks();
                if (Game1.exitToTitle || !ReferenceEquals(chord.Player, Game1.player)
                    || chord.Launch != Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")
                    || chord.Role != Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")
                    || chord.Revision is not null && (!Context.IsWorldReady
                        || Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1"))
                    CancelChord("The owned review context changed during input.");
                if (chord.Progress.Gesture is not null && chord.CurrentViewport?.Invoke() != chord.Viewport)
                    CancelChord("The UI viewport or scale changed before gesture release.");
                if (chord.Revision is not null && (chord.Progress.StartTick is null || chord.Progress.MoreActions))
                {
                    bool changed = chord.Progress.StartTick is null || chord.Progress.Gesture is null
                        ? chord.CurrentRevision() != chord.Revision
                        : chord.CurrentContinuity?.Invoke() != chord.Continuity;
                    if (changed) CancelChord(chord.Progress.StartTick is null
                        ? "The UI revision changed before the first input update."
                        : "The UI context changed while input actions remained.");
                }
                if (chord.Progress.Remaining == 0 && chord.Progress.Gesture is null) return;
                if (chord.Progress.StartTick is null && chord.ValidateDispatch is not null
                    && !ReviewWorldActionDispatchGate.TryAuthorize(chord.ValidateDispatch, out string? dispatchProblem))
                {
                    CancelChord(dispatchProblem!);
                    return;
                }

                // Read-only physical samples: never move the cursor, change focus or suppress a key.
                KeyboardState keyboard = Keyboard.GetState();
                MouseState mouse = ReadHardwareMouse();
                GamePadState controller = Game1.playerOneIndex >= Microsoft.Xna.Framework.PlayerIndex.One
                    ? GamePad.GetState(Game1.playerOneIndex) : default;
                bool Physical(SButton b) => b.TryGetKeyboard(out Keys key) ? keyboard.IsKeyDown(key)
                    : b.TryGetController(out Buttons pad) ? controller.IsButtonDown(pad)
                    : b switch
                    {
                        SButton.MouseLeft => mouse.LeftButton == ButtonState.Pressed,
                        SButton.MouseRight => mouse.RightButton == ButtonState.Pressed,
                        SButton.MouseMiddle => mouse.MiddleButton == ButtonState.Pressed,
                        SButton.MouseX1 => mouse.XButton1 == ButtonState.Pressed,
                        SButton.MouseX2 => mouse.XButton2 == ButtonState.Pressed,
                        _ => false,
                    };
                bool Merged(SButton b) => chord.Buttons.Contains(b) || Physical(b)
                    || ___CustomPressedKeys.Contains(b) || (!chord.Buttons.Contains(b) && chord.Helper.IsDown(b));
                if (chord.Buttons.Any(b => Physical(b) || chord.Helper.IsSuppressed(b)
                    || ___CustomPressedKeys.Contains(b)))
                { CancelChord("A physical or external input overlaps a chord member."); return; }
                // KeyboardDispatcher polls Ctrl+V even without a text subscriber.
                if (Merged(SButton.V) && (Merged(SButton.LeftControl) || Merged(SButton.RightControl))
                    && chord.Buttons.Any(b => b is SButton.V or SButton.LeftControl or SButton.RightControl))
                { CancelChord("Clipboard-triggering Ctrl+V input is unsupported."); return; }
                if (chord.Progress.Remaining == 0) return;
                if (chord.Progress.Position is { } position)
                {
                    Screen.Value.Mouse.Set(position.X, position.Y);
                    if (chord.Progress.PreparingCursor)
                    {
                        __state.PreparingCursor = true;
                        return;
                    }
                    if (chord.Progress.Gesture?.Action == ReviewInputContract.ScrollAction)
                    {
                        __state.WheelBefore = __instance.GetMouseState().ScrollWheelValue;
                        __state.WheelDelta = Math.Sign(chord.Progress.Gesture.Notches!.Value) * 120;
                        if (!Screen.Value.Mouse.TryQueueWheel(__state.WheelDelta))
                            CancelChord("The virtual wheel sample could not be queued.");
                        return;
                    }
                }
                if (!__state.Owned.TryInject(chord.Buttons.Select(b => (int)b).ToArray(),
                    b => ___CustomPressedKeys.Contains((SButton)b), b => chord.Helper.Press((SButton)b)))
                    CancelChord("An external press overlapped the chord before injection.");
            }
            catch (Exception)
            {
                __state.Owned.Rollback(b => ___CustomPressedKeys.Remove((SButton)b));
                Screen.Value.Mouse.CancelWheel();
                CancelChord("The input update failed before the complete chord was applied.");
            }
        }
    }

    private static Exception? AfterInputUpdate(InputState __instance,
        HashSet<SButton> ___CustomPressedKeys, InputSample? __state, Exception? __exception)
    {
        bool completed = false;
        try
        {
            Exception? result = CompleteInputUpdate(__instance, ___CustomPressedKeys, __state, __exception);
            completed = true;
            return result;
        }
        finally
        {
            PublishNativeMouseSample(__instance, __state, completed && __exception is null);
        }
    }

    private static Exception? CompleteInputUpdate(InputState __instance,
        HashSet<SButton> ___CustomPressedKeys, InputSample? __state, Exception? __exception)
    {
        lock (Sync)
        {
            Screen.Value.ActiveSample = null;
            if (__state is null || Screen.Value.Pending is not PendingInput chord) return __exception;
            bool updated = __exception is null
                && !ReferenceEquals(__state.PreviousStates, _buttonStates!.GetValue(__instance));
            bool consumed = __state.Owned.IsConsumed(b => ___CustomPressedKeys.Contains((SButton)b));
            if (!updated || !consumed)
            {
                // Only entries absent before this exact prefix and added by it are ours.
                __state.Owned.Rollback(b => ___CustomPressedKeys.Remove((SButton)b));
                Screen.Value.Mouse.CancelWheel();
                CancelChord("SMAPI did not complete the input sample; owned overrides were removed.");
                if (chord.Progress.Gesture is null) FinishChord(false);
                else chord.Progress.ObserveFailedInputSample(Game1.ticks);
            }
            else if (chord.Progress.Gesture is not null)
            {
                bool downSample = chord.Progress.Remaining > 0 && !__state.PreparingCursor;
                bool valid = downSample
                    ? chord.Progress.Gesture.Action == ReviewInputContract.ScrollAction
                        ? __state.WheelDelta != 0 && Screen.Value.Mouse.WheelSample == __state.WheelSample + 1
                            && (long)__instance.GetMouseState().ScrollWheelValue - __state.WheelBefore == __state.WheelDelta
                        : __state.Owned.Injected && chord.Buttons.All(b => chord.Helper.GetState(b)
                            == (chord.Progress.EdgeSamples == 0 ? SButtonState.Pressed : SButtonState.Held))
                    : chord.Buttons.All(b => !chord.Helper.IsDown(b));
                if (__state.PreparingCursor)
                {
                    MouseState confirmed = __instance.GetMouseState();
                    valid &= !__state.Owned.Injected && __state.CursorPosition is { } position
                        && confirmed.X == position.X && confirmed.Y == position.Y;
                }
                if (!valid) CancelChord("The complete gesture input sample was not observed.");
                chord.Progress.ObserveGestureSample(Game1.ticks, !downSample, valid, __state.PreparingCursor);
            }
            else if (__state.Owned.Injected)
            {
                SButtonState expected = chord.Progress.StartTick is null ? SButtonState.Pressed : SButtonState.Held;
                if (chord.Buttons.All(b => chord.Helper.GetState(b) == expected))
                    chord.Progress.Consumed(Game1.ticks);
                else CancelChord("SMAPI did not observe every chord member in the expected state.");
            }
            else if (chord.Progress.Remaining == 0)
            {
                bool released = chord.Buttons.All(b => chord.Progress.StartTick is null
                    ? !chord.Helper.IsDown(b) : chord.Helper.GetState(b) == SButtonState.Released);
                chord.Progress.Released(Game1.ticks);
                FinishChord(released);
            }
        }
        return __exception;
    }

    internal static void AfterGameUpdate()
    {
        lock (Sync)
        {
            ObserveTextLifetime();
            if (Screen.Value.Pending is not PendingInput pending || !pending.Progress.AwaitingGameUpdate) return;
            if (pending.Progress.AwaitingCursorUpdate
                && (_nativeMouseUpdate is not { } update || !IsCurrent(update)))
                CancelChord("The owned player update changed during cursor preparation.");
            if (pending.Progress.CompleteGameUpdate(out bool released)) FinishChord(released);
        }
    }

    private static void CancelChord(string failure)
    {
        InvalidateNativeMouseSample();
        if (Screen.Value.Pending is not PendingInput chord) return;
        chord.Failure ??= failure;
        chord.Progress.Cancel();
        Screen.Value.Mouse.CancelWheel();
        AllowBackgroundInputForNextTicks();
    }

    private static void FinishChord(bool released)
    {
        if (Screen.Value.Pending is not PendingInput chord) return;
        Screen.Value.Pending = null;
        bool success = !chord.Progress.Canceled && released
            && (chord.CommandTarget is null || chord.CommandDelivered);
        if (!success && chord.Progress.Gesture is not null) Screen.Value.Mouse.Clear();
        chord.Completed(new(success,
            success ? "The complete chord was observed and released."
                : chord.Failure ?? "Release could not be confirmed without suppressing external input.",
            ProblemCode: success ? null : "inputChordInterrupted",
            Buttons: chord.Buttons.Select(b => b.ToString()).ToArray(),
            StartTick: chord.Progress.StartTick, EndTick: chord.Progress.EndTick, Released: released,
            CompletedSteps: chord.Progress.Gesture is not null ? chord.Progress.CompletedSteps : null,
            FinalX: chord.Progress.Gesture is not null ? Screen.Value.Mouse.X : null,
            FinalY: chord.Progress.Gesture is not null ? Screen.Value.Mouse.Y : null));
    }

    public static void AllowBackgroundInputForNextTicks()
    {
        lock (Sync)
        {
            Screen.Value.BackgroundInputThroughTick = Game1.ticks + 4;
        }
    }

    public static bool TryScroll(int direction, out string error)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (!_installed || Screen.Value.Text is not null || Screen.Value.Pending is not null || !Screen.Value.Mouse.TryQueueWheel(direction))
            {
                error = "The virtual wheel requires a cursor, one pending notch at most, and an available cumulative counter range.";
                return false;
            }

            AllowBackgroundInputForNextTicks();
            error = string.Empty;
            return true;
        }
    }

    private static void EnsureInputOwner()
    {
        if (!ReferenceEquals(Screen.Value.InputOwner, Game1.input))
        {
            InvalidateNativeMouseSample();
            FinishText("The input owner changed before text delivery completed.");
            Screen.Value.InputOwner = Game1.input;
            Screen.Value.Mouse = new ReviewVirtualMouseState();
            Screen.Value.NativeMouseBinding?.Dispose();
            Screen.Value.NativeMouseBinding = null;
            CancelChord("The input owner changed before release was confirmed.");
            FinishChord(false);
            Screen.Value.WheelFailureReported = false;
            Screen.Value.BackgroundInputThroughTick = -1;
        }
    }

    private static void AfterGetMouseState(InputState __instance, ref MouseState __result)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (!ReferenceEquals(__instance, Screen.Value.InputOwner))
            {
                return;
            }

            var sample = Screen.Value.Mouse.Apply(
                __result.X, __result.Y, __result.ScrollWheelValue,
                Game1.uiViewport.Width, Game1.uiViewport.Height,
                Game1.options?.uiScale ?? 1f, out bool wheelRejected,
                consumeWheel: Screen.Value.Pending?.Progress.Gesture is null || Screen.Value.ActiveSample is not null);
            if (wheelRejected)
            {
                CancelChord("Virtual wheel input was canceled.");
                if (Screen.Value.Pending?.Progress.Gesture is null) Screen.Value.Mouse.Clear();
                sample.X = __result.X;
                sample.Y = __result.Y;
                if (!Screen.Value.WheelFailureReported)
                {
                    _monitor?.Log("Virtual wheel input was canceled because the sampled cumulative counter exceeded its range; the consumed origin was retained.", LogLevel.Error);
                    Screen.Value.WheelFailureReported = true;
                }
            }
            else
            {
                Screen.Value.WheelFailureReported = false;
                if (Screen.Value.ActiveSample is { PreparingCursor: true } activeSample)
                    activeSample.CursorPosition = (sample.X, sample.Y);
            }
            __result = new MouseState(
                sample.X,
                sample.Y,
                sample.Wheel,
                __result.LeftButton,
                __result.MiddleButton,
                __result.RightButton,
                __result.XButton1,
                __result.XButton2);
        }
    }

    private static void AfterGetReviewActivity(ref bool __result)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (Screen.Value.BackgroundInputThroughTick >= Game1.ticks)
            {
                __result = true;
            }
        }
    }
}
#endif

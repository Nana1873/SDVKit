using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;


namespace SdvKit.GameSide;

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

    /// <summary>Holds one keyboard button for at most the request's ticks, releasing it as soon as
    /// <paramref name="releaseWhen"/> is true at an input update after the first held tick.</summary>
    bool TryWorldHold(ReviewInputRequest request, Func<string?> validateDispatch, Func<bool> releaseWhen,
        Action<ReviewInputResult> completed, out string error)
    { error = "Validated held input is unavailable."; return false; }

    bool TryText(ReviewInputRequest request, Action<ReviewInputResult> completed, out string error)
    { error = "Text input is unavailable."; return false; }

    /// <summary>Null when the game would turn a wheel notch into a hotbar switch with no menu open,
    /// else the problem code: <c>inputWheelPlayerBusy</c>, <c>inputWheelInventoryEmpty</c>, or
    /// <c>inputMenuMissing</c> for a runtime without world wheel support.</summary>
    string? WorldWheelRefusal => "inputMenuMissing";

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
    /// <summary>Ends a plain chord before its requested ticks: the next input sample releases it normally.</summary>
    public void StopEarly()
    {
        if (Gesture is not null || Canceled || Finished || StartTick is null)
            throw new InvalidOperationException("Only a started plain chord can stop early.");
        Remaining = 0;
    }
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

internal static class ReviewInputResponsePublisher
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
        ReviewResponseMailbox.Publish(responsePath, bytes);
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
            // Without a menu, Game1.UpdateControlInput turns the same cumulative wheel delta into a
            // hotbar switch; the runtime names the game state that would ignore it.
            string? worldRefusal = runtime.MenuOpen ? null : runtime.WorldWheelRefusal;
            if (worldRefusal is not null)
            {
                return Failure(worldRefusal, worldRefusal switch
                {
                    "inputWheelPlayerBusy" =>
                        "The local player cannot switch tools now: the world or location is not ready; a minigame, emote menu, text entry, chat, pause, pan mode, fade, frozen controls, tool use, dialogue or event blocks hotbar wheel input; or the player cannot move.",
                    "inputWheelInventoryEmpty" =>
                        "The local player's inventory is empty, so the game ignores hotbar wheel input.",
                    _ => "Mouse-wheel review input requires an active game menu.",
                });
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
                    runtime.MenuOpen
                        ? $"Pressed review input '{canonicalButton}' for one mouse-wheel notch."
                        : $"Pressed review input '{canonicalButton}' for one mouse-wheel notch; with no menu open the game switches the hotbar slot on its next input update.",
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

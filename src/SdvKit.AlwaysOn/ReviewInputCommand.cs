using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal enum ReviewInputKind
{
    Press,
    Scroll,
    Cursor,
    ClearCursor,
}

internal sealed record ReviewInputRequest(
    ReviewInputKind Kind,
    string? Button,
    int X,
    int Y,
    string? RequestId = null);

internal static class ReviewInputArguments
{
    internal const string Usage =
        "Usage: sdvkit input press <SButton|MouseWheelUp|MouseWheelDown> | sdvkit input cursor <ui-x> <ui-y> | sdvkit input cursor clear";

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

        if (arguments.Count == actionIndex + 2
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

    bool TryPress(string button, out string canonicalButton, out string error);

    bool TryScroll(int direction, out string error);

    bool TrySetCursor(int x, int y, out string error);

    bool TryClearCursor(out string error);
}

internal sealed record ReviewInputResult(
    bool Succeeded,
    string Message,
    string? CanonicalButton = null,
    string? ProblemCode = null);

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
        int uiWidth, int uiHeight, float uiScale, out bool wheelRejected)
    {
        _physicalWheel = physicalWheel;
        _hasPhysicalWheelSample = true;
        long neutral = (long)physicalWheel + _wheelOffset;
        long requested = neutral + _pendingWheel;
        wheelRejected = requested is < int.MinValue or > int.MaxValue;
        // Consume at the common input sample, never at a game/helper read afterwards.
        if (!wheelRejected)
        {
            _wheelOffset += _pendingWheel;
        }
        _pendingWheel = 0;
        // An external counter jump may also make the consumed origin unrepresentable.
        // Retain the last output then; resetting the origin would replay an opposite delta.
        int wheel = wheelRejected
            ? neutral is >= int.MinValue and <= int.MaxValue ? (int)neutral : _lastWheel
            : (int)requested;
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

// Ownership bookkeeping only; SMAPI still owns every actual button transition.
internal sealed class ReviewPendingPresses
{
    private readonly HashSet<int> _buttons = new();

    public void Add(int button) => _buttons.Add(button);

    public void RetireConsumed(Func<int, bool> isStillPending) =>
        _buttons.RemoveWhere(button => !isStillPending(button));

    public void Reset() => _buttons.Clear();

    public void CancelAndClear(ReviewVirtualMouseState mouse, Action<int> suppress)
    {
        foreach (int button in _buttons)
        {
            suppress(button);
        }
        _buttons.Clear();
        mouse.Clear();
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
    }

    private static ReviewInputResponseEnvelope CreateResponse(
        ReviewInputRequest request,
        IReviewInputRuntime runtime,
        ReviewInputResult result)
    {
        string action = request.Kind switch
        {
            ReviewInputKind.Press => ReviewInputContract.PressAction,
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
            request.Kind == ReviewInputKind.Press
                ? result.CanonicalButton ?? request.Button
                : null,
            direction,
            request.Kind == ReviewInputKind.Cursor ? request.X : null,
            request.Kind == ReviewInputKind.Cursor ? request.Y : null,
            runtime.CursorSet,
            runtime.MenuOpen,
            result.Succeeded
                ? null
                : new ReviewInputProblem(
                    result.ProblemCode ?? "inputRejected",
                    result.Message.Length <= ReviewInputContract.MaximumProblemLength
                        ? result.Message
                        : result.Message[..ReviewInputContract.MaximumProblemLength]));
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

internal sealed class StardewReviewInputRuntime(IModHelper helper) : IReviewInputRuntime
{
    public int UiWidth => Game1.uiViewport.Width;

    public int UiHeight => Game1.uiViewport.Height;

    public int GameTick => Game1.ticks;

    public bool InputAdapterReady => ReviewVirtualCursor.IsInstalled;

    public bool CursorSet => ReviewVirtualCursor.IsSet;

    public bool MenuOpen => Game1.activeClickableMenu is not null;

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

        helper.Input.Press(parsed);
        ReviewVirtualCursor.RecordPress(helper.Input, parsed);
        canonicalButton = parsed.ToString();
        error = string.Empty;
        return true;
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

internal static class ReviewVirtualCursor
{
    private const string HarmonyId = "SDVKit.AlwaysOn.VirtualReviewCursor";

    private static readonly object Sync = new();
    private static bool _installed;
    private static ReviewVirtualMouseState _mouse = new();
    private static InputState? _inputOwner;
    private static readonly ReviewPendingPresses PendingButtons = new();
    private static IInputHelper? _buttonHelper;
    private static IMonitor? _monitor;
    private static bool _wheelFailureReported;
    private static int _backgroundInputThroughTick = -1;

    public static bool IsSet
    {
        get
        {
            lock (Sync)
            {
                EnsureInputOwner();
                return _mouse.IsSet;
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
            FieldInfo? pressedKeys = smapiInput is null ? null : AccessTools.Field(smapiInput, "CustomPressedKeys");
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
                || pressedKeys?.FieldType != typeof(HashSet<SButton>)
                || pressedKeys.IsStatic
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
                    finalizer: new HarmonyMethod(inputFinalizer));
                harmony.Patch(
                    isActiveNoOverlay,
                    postfix: new HarmonyMethod(activePostfix));
                harmony.Patch(
                    isActive,
                    postfix: new HarmonyMethod(activePostfix));
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
            if (!_installed)
            {
                error = "Virtual cursor input is unavailable because its process-local input patch was not installed.";
                return false;
            }

            EnsureInputOwner();
            _mouse.Set(uiX, uiY);
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
            // Console commands run before TrueUpdate. Cancel queued owned presses
            // before restoring physical coordinates, so a click cannot be redirected.
            PendingButtons.CancelAndClear(_mouse, button => _buttonHelper!.Suppress((SButton)button));
            AllowBackgroundInputForNextTicks();
        }
    }

    public static void RecordPress(IInputHelper helper, SButton button)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            _buttonHelper = helper;
            PendingButtons.Add((int)button);
            AllowBackgroundInputForNextTicks();
        }
    }

    private static Exception? AfterInputUpdate(
        InputState __instance,
        HashSet<SButton> ___CustomPressedKeys,
        Exception? __exception)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (ReferenceEquals(__instance, _inputOwner))
            {
                // Read only: TrueUpdate may catch an exception before or after clearing
                // its queue, and public UpdateTicked is skipped during loading/saving.
                PendingButtons.RetireConsumed(button => ___CustomPressedKeys.Contains((SButton)button));
            }
        }
        return __exception;
    }

    public static void AllowBackgroundInputForNextTicks()
    {
        lock (Sync)
        {
            _backgroundInputThroughTick = Game1.ticks + 4;
        }
    }

    public static bool TryScroll(int direction, out string error)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (!_installed || !_mouse.TryQueueWheel(direction))
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
        if (!ReferenceEquals(_inputOwner, Game1.input))
        {
            _inputOwner = Game1.input;
            _mouse = new ReviewVirtualMouseState();
            PendingButtons.Reset();
            _buttonHelper = null;
            _wheelFailureReported = false;
            _backgroundInputThroughTick = -1;
        }
    }

    private static void AfterGetMouseState(InputState __instance, ref MouseState __result)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            if (!ReferenceEquals(__instance, _inputOwner))
            {
                return;
            }

            var sample = _mouse.Apply(
                __result.X, __result.Y, __result.ScrollWheelValue,
                Game1.uiViewport.Width, Game1.uiViewport.Height,
                Game1.options?.uiScale ?? 1f, out bool wheelRejected);
            if (wheelRejected)
            {
                PendingButtons.CancelAndClear(_mouse, button => _buttonHelper!.Suppress((SButton)button));
                sample.X = __result.X;
                sample.Y = __result.Y;
                if (!_wheelFailureReported)
                {
                    _monitor?.Log("Virtual wheel input was canceled because the sampled cumulative counter exceeded its range; the consumed origin was retained.", LogLevel.Error);
                    _wheelFailureReported = true;
                }
            }
            else
            {
                _wheelFailureReported = false;
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
            if (_backgroundInputThroughTick >= Game1.ticks)
            {
                __result = true;
            }
        }
    }
}
#endif

using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace SdvKit.GameSide;

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
                if (ReviewInputOperation.ControllerRefusal(request, runtime) is { } refusal)
                {
                    Complete(refusal);
                    return;
                }
                if (!runtime.TryChord(request, Complete, out string chordError))
                    Complete(new(false, chordError, ProblemCode: "inputChordRejected",
                        Buttons: request.Buttons, Released: false));
                return;
            }
            ReviewInputResult result = ReviewInputOperation.Execute(request!, runtime);
            if (request!.RequestId is not null)
            {
                ReviewInputResponsePublisher.Write(
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
                    ReviewInputResponsePublisher.Write(runtimePath, CreateResponse(request, runtime, result));
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
            ReviewInputResponsePublisher.Write(
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

    public string? GamepadMode => Game1.options?.gamepadMode switch
    {
        Options.GamepadModes.Auto => "auto",
        Options.GamepadModes.ForceOn => "forceOn",
        Options.GamepadModes.ForceOff => "forceOff",
        _ => null,
    };

    // The raw MonoGame state: SMAPI's override marks an injected controller button as connected.
    public bool GamepadConnected => Game1.playerOneIndex >= Microsoft.Xna.Framework.PlayerIndex.One
        && GamePad.GetState(Game1.playerOneIndex).IsConnected;
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
        // Stardew aims a mouse tool use at the previous update's mouse state. A one-click gesture
        // first completes one cursor-only game update, so that state holds the target cursor.
        var gesture = new ReviewInputQuery(ReviewInputContract.ClickAction, button.ToString(), null,
            request.X, request.Y, Modifiers: [], Count: 1);
        return ReviewVirtualCursor.TryChord(helper.Input, [button], 1, null, currentRevision,
            result => completed(result with { CanonicalButton = result.Buttons?[0] }), out error,
            request.RequestId, gesture, validateDispatch: validateDispatch);
    }

    public bool TryWorldHold(ReviewInputRequest request, Func<string?> validateDispatch, Func<bool> releaseWhen,
        Action<ReviewInputResult> completed, out string error)
    {
        if (request.Kind != ReviewInputKind.Chord || request.Buttons is not { Count: 1 }
            || !Enum.TryParse(request.Buttons[0], out SButton button) || !button.TryGetKeyboard(out _)
            || request.DurationTicks is < 1 or > 120)
        {
            error = "A held move requires exactly one keyboard button for 1-120 input ticks.";
            return false;
        }
        return ReviewVirtualCursor.TryChord(helper.Input, [button], request.DurationTicks, null, currentRevision,
            result => completed(result with { CanonicalButton = result.Buttons?[0] }), out error,
            request.RequestId, validateDispatch: validateDispatch, releaseWhen: releaseWhen);
    }

    public bool TryScroll(int direction, out string error)
    {
        if (!ReviewVirtualCursor.IsSet)
        {
            error = "Set the virtual review cursor before sending mouse-wheel input.";
            return false;
        }

        if (Game1.activeClickableMenu is null && WorldWheelRefusal is not null)
        {
            error = "The game would not switch the hotbar with this mouse-wheel notch now.";
            return false;
        }

        return ReviewVirtualCursor.TryScroll(direction, out error);
    }

    // Mirrors the path to Game1.pressSwitchToolButton: Game1 calls UpdateControlInput only with a
    // location and no minigame, emote menu, text entry, menu, fade or frozen controls; UpdateControlInput
    // returns early in pan mode, while chatting or freeze-paused, and while paused; its final gate needs
    // a free player outside events. The game reads the mouse wheel with gamepad controls too.
    public string? WorldWheelRefusal =>
        !Context.IsWorldReady || Game1.player is not { } player || Game1.currentLocation is null
            || Game1.currentMinigame is not null || Game1.emoteMenu is not null || Game1.textEntry is not null
            || Game1.globalFade || Game1.freezeControls || Game1.panMode || Game1.IsChatting || player.freezePause > 0
            || Game1.paused || Game1.HostPaused
            || player.UsingTool || Game1.dialogueUp || !player.CanMove || Game1.eventUp || Game1.farmEvent is not null
            ? "inputWheelPlayerBusy"
            : !player.Items.HasAny() ? "inputWheelInventoryEmpty" : null;

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

    private const string HarmonyId = "SDVKit.GameSide.VirtualReviewCursor";

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
        public Func<bool>? ReleaseWhen { get; init; }
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
        Func<string?>? validateDispatch = null, Func<bool>? releaseWhen = null)
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
                ValidateDispatch = validateDispatch,
                ReleaseWhen = gesture is null ? releaseWhen : null
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
                // A held move reads the player after the previous game update; releasing here means the
                // key is up in this tick's update, and the game halts the player before it moves again.
                if (chord.ReleaseWhen is not null && chord.Progress.Gesture is null && !chord.Progress.Canceled
                    && chord.Progress.StartTick is not null && chord.Progress.Remaining > 0 && chord.ReleaseWhen())
                    chord.Progress.StopEarly();
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

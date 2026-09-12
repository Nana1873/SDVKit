#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static partial class ReviewVirtualCursor
{
    private sealed class NativeMouseUpdate(ReviewMouseSampleScope scope, ScreenInputState owner)
    {
        public ReviewMouseSampleScope Scope { get; } = scope;
        public ScreenInputState Owner { get; } = owner;
        public object? PreviousStates { get; set; }
        public ReviewNativeMousePublication Publication { get; } = new(owner.Mouse);
    }

    [ThreadStatic] private static NativeMouseUpdate? _nativeMouseUpdate;

    private static bool NativeMouseContextReady => Context.IsWorldReady && !Game1.exitToTitle
        && Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1";

    private static bool IsCurrent(ReviewMouseSampleScope scope) =>
        scope.IsCurrent(Game1.game1, Game1.input, Game1.player, Context.ScreenId,
            Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"),
            Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"), NativeMouseContextReady);

    private static bool IsCurrent(NativeMouseUpdate update) => IsCurrent(update.Scope)
        && ReferenceEquals(update.Owner, Screen.Value)
        && update.Owner.NativeMouseBinding is { } binding && IsCurrent(binding);

    private static void BindNativeMouse()
    {
        Screen.Value.NativeMouseBinding?.Dispose();
        Screen.Value.NativeMouseBinding = new(Game1.game1, Game1.input, Game1.player, Context.ScreenId,
            Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") ?? string.Empty,
            Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
    }

    private static void BeforePlayerUpdate(Game1 __instance, out NativeMouseUpdate? __state)
    {
        lock (Sync)
        {
            // A nested/replaced update cannot inherit an earlier player's publication.
            _nativeMouseUpdate?.Scope.Dispose();
            _nativeMouseUpdate = null;
            __state = null;
            if (!NativeMouseContextReady || !ReferenceEquals(__instance, Game1.game1)
                || Game1.input is null || Game1.player is null
                || Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") is not { Length: > 0 } launch) return;
            EnsureInputOwner();
            __state = new(new(__instance, Game1.input, Game1.player, Context.ScreenId, launch,
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")), Screen.Value);
            _nativeMouseUpdate = __state;
        }
    }

    private static Exception? AfterPlayerUpdate(NativeMouseUpdate? __state, Exception? __exception)
    {
        // Runs after every mod's UpdateTicked handler and also on skipped events/exceptions.
        __state?.Scope.Dispose();
        if (ReferenceEquals(_nativeMouseUpdate, __state)) _nativeMouseUpdate = null;
        return __exception;
    }

    private static void BeginNativeMouseInputSample(InputState input)
    {
        if (_nativeMouseUpdate is not { } update) return;
        update.Publication.BeginInputSample();
        update.PreviousStates = IsCurrent(update) && ReferenceEquals(input, update.Owner.InputOwner)
            ? _buttonStates!.GetValue(input) : null;
    }

    private static void PublishNativeMouseSample(InputState input, InputSample? sample, bool succeeded)
    {
        lock (Sync)
        {
            if (_nativeMouseUpdate is not { } update || !succeeded
                || update.PreviousStates is null || !IsCurrent(update)
                || !ReferenceEquals(input, update.Owner.InputOwner)
                || ReferenceEquals(update.PreviousStates, _buttonStates!.GetValue(input))
                || Screen.Value.Pending?.Progress.Canceled == true
                || (!Screen.Value.Mouse.IsSet && !Screen.Value.Mouse.HasWheelOffset)) return;

            try
            {
                // SInputState's override returns its completed MouseState builder result.
                // Never call Mouse.Apply here: its cumulative wheel was already consumed once.
                MouseState confirmed = input.GetMouseState();
                ReviewMouseButtons buttons = ReviewMouseButtons.None;
                if (sample?.Owned.Injected == true && Screen.Value.Pending is { } chord)
                    foreach (SButton button in chord.Buttons) buttons |= MouseButton(button);
                update.Publication.Publish(Values(confirmed), buttons);
            }
            catch (Exception)
            {
                CancelChord("The confirmed native mouse sample could not be published.");
            }
        }
    }

    private static void InvalidateNativeMouseSample()
    {
        if (_nativeMouseUpdate is not { } update || !ReferenceEquals(update.Owner, Screen.Value)) return;
        update.Publication.Invalidate();
    }

    private static MouseState ReadHardwareMouse() => State(
        ReviewNativeMousePublication.ReadHardware(() => Values(Mouse.GetState())));

    private static ReviewMouseButtons MouseButton(SButton button) => button switch
    {
        SButton.MouseLeft => ReviewMouseButtons.Left,
        SButton.MouseMiddle => ReviewMouseButtons.Middle,
        SButton.MouseRight => ReviewMouseButtons.Right,
        SButton.MouseX1 => ReviewMouseButtons.X1,
        SButton.MouseX2 => ReviewMouseButtons.X2,
        _ => ReviewMouseButtons.None
    };

    private static ReviewMouseValues Values(MouseState state) => new(state.X, state.Y, state.ScrollWheelValue,
        (state.LeftButton == ButtonState.Pressed ? ReviewMouseButtons.Left : ReviewMouseButtons.None)
        | (state.MiddleButton == ButtonState.Pressed ? ReviewMouseButtons.Middle : ReviewMouseButtons.None)
        | (state.RightButton == ButtonState.Pressed ? ReviewMouseButtons.Right : ReviewMouseButtons.None)
        | (state.XButton1 == ButtonState.Pressed ? ReviewMouseButtons.X1 : ReviewMouseButtons.None)
        | (state.XButton2 == ButtonState.Pressed ? ReviewMouseButtons.X2 : ReviewMouseButtons.None));

    private static MouseState State(ReviewMouseValues value, int horizontalWheel = 0)
    {
        ButtonState Button(ReviewMouseButtons button) => (value.Buttons & button) != 0 ? ButtonState.Pressed : ButtonState.Released;
        return new(value.X, value.Y, value.Wheel, Button(ReviewMouseButtons.Left), Button(ReviewMouseButtons.Middle),
            Button(ReviewMouseButtons.Right), Button(ReviewMouseButtons.X1), Button(ReviewMouseButtons.X2), horizontalWheel);
    }

    private static void AfterGetNativeMouseState(ref MouseState __result)
    {
        if (ReviewNativeMousePublication.ReadingHardware
            || _nativeMouseUpdate is not { Publication.Published: true } update) return;
        lock (Sync)
        {
            if (!IsCurrent(update))
            {
                update.Scope.Dispose();
                return;
            }
            ReviewMouseButtons suppressed = ReviewMouseButtons.None;
            if (Screen.Value.Pending is { } chord)
                foreach (SButton button in chord.Buttons)
                    if (chord.Helper.IsSuppressed(button)) suppressed |= MouseButton(button);
            __result = State(update.Publication.Read(Values(__result), suppressed, true, out bool overlap),
                __result.HorizontalScrollWheelValue);
            if (overlap)
                CancelChord("A physical or external input overlaps a native mouse sample.");
        }
    }
}
#endif

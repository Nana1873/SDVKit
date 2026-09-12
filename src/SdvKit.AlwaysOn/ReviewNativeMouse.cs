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
        public MouseState? Sample { get; set; }
        public SButton[] Buttons { get; set; } = [];
        public bool Invalidated { get; set; }
        public bool Published { get; set; }
    }

    [ThreadStatic] private static NativeMouseUpdate? _nativeMouseUpdate;
    [ThreadStatic] private static int _hardwareMouseReads;

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
        update.Sample = null;
        update.Buttons = [];
        update.Invalidated = false;
        update.Published = false;
        update.PreviousStates = IsCurrent(update) && ReferenceEquals(input, update.Owner.InputOwner)
            ? _buttonStates!.GetValue(input) : null;
    }

    private static void PublishNativeMouseSample(InputState input, InputSample? sample, bool succeeded)
    {
        lock (Sync)
        {
            if (_nativeMouseUpdate is not { } update || update.Invalidated || !succeeded
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
                update.Buttons = sample?.Owned.Injected == true && Screen.Value.Pending is { } chord
                    ? chord.Buttons.Where(b => ReviewInputArguments.IsMouseButtonToken(b.ToString())).ToArray() : [];
                update.Sample = confirmed;
                update.Published = true;
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
        update.Sample = null;
        update.Buttons = [];
        update.Invalidated = true;
    }

    private static MouseState ReadHardwareMouse()
    {
        _hardwareMouseReads++;
        try { return Mouse.GetState(); }
        finally { _hardwareMouseReads--; }
    }

    private static void AfterGetNativeMouseState(ref MouseState __result)
    {
        if (_hardwareMouseReads != 0 || _nativeMouseUpdate is not { Published: true } update) return;
        lock (Sync)
        {
            if (!IsCurrent(update))
            {
                update.Scope.Dispose();
                return;
            }
            MouseState hardware = __result;
            MouseState Neutral() => new(hardware.X, hardware.Y,
                update.Owner.Mouse.ApplyWheelOrigin(hardware.ScrollWheelValue),
                hardware.LeftButton, hardware.MiddleButton, hardware.RightButton,
                hardware.XButton1, hardware.XButton2);
            if (update.Sample is not { } sample)
            {
                // Clear/cancel removes every owned field immediately. The already consumed
                // cumulative origin survives, just as it does in InputState.GetMouseState.
                __result = Neutral();
                return;
            }
            ButtonState Value(MouseState state, SButton button) => button switch
            {
                SButton.MouseLeft => state.LeftButton,
                SButton.MouseMiddle => state.MiddleButton,
                SButton.MouseRight => state.RightButton,
                SButton.MouseX1 => state.XButton1,
                SButton.MouseX2 => state.XButton2,
                _ => ButtonState.Released
            };
            if (update.Buttons.Any(b => Value(hardware, b) == ButtonState.Pressed
                || Screen.Value.Pending?.Helper.IsSuppressed(b) == true))
            {
                CancelChord("A physical or external input overlaps a native mouse sample.");
                __result = Neutral();
                return;
            }
            ButtonState Merge(SButton button) => update.Buttons.Contains(button)
                ? Value(sample, button) : Value(hardware, button);
            __result = new MouseState(
                Screen.Value.Mouse.IsSet ? sample.X : hardware.X,
                Screen.Value.Mouse.IsSet ? sample.Y : hardware.Y,
                sample.ScrollWheelValue,
                Merge(SButton.MouseLeft), Merge(SButton.MouseMiddle), Merge(SButton.MouseRight),
                Merge(SButton.MouseX1), Merge(SButton.MouseX2));
        }
    }
}
#endif

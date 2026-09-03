using System.Globalization;

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
    int Y);

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

        if (arguments.Count == 3
            && string.Equals(arguments[1], "press", StringComparison.Ordinal)
            && IsValidButtonToken(arguments[2]))
        {
            request = new ReviewInputRequest(
                IsMouseWheelToken(arguments[2])
                    ? ReviewInputKind.Scroll
                    : ReviewInputKind.Press,
                arguments[2],
                0,
                0);
        }
        else if (arguments.Count == 4
            && string.Equals(arguments[1], "cursor", StringComparison.Ordinal)
            && TryParseCoordinate(arguments[2], out int x)
            && TryParseCoordinate(arguments[3], out int y))
        {
            request = new ReviewInputRequest(
                ReviewInputKind.Cursor,
                null,
                x,
                y);
        }
        else if (arguments.Count == 3
            && string.Equals(arguments[1], "cursor", StringComparison.Ordinal)
            && string.Equals(arguments[2], "clear", StringComparison.Ordinal))
        {
            request = new ReviewInputRequest(
                ReviewInputKind.ClearCursor,
                null,
                0,
                0);
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

    bool TryPress(string button, out string canonicalButton, out string error);

    bool TryScroll(int direction, out string error);

    bool TrySetCursor(int x, int y, out string error);

    bool TryClearCursor(out string error);
}

internal sealed record ReviewInputResult(bool Succeeded, string Message);

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
                : Failure(clearError);
        }

        if (request.Kind == ReviewInputKind.Press)
        {
            if (!ReviewInputArguments.IsValidButtonToken(request.Button))
            {
                return Failure(ReviewInputArguments.Usage);
            }

            return runtime.TryPress(request.Button!, out string canonicalButton, out string error)
                ? new ReviewInputResult(true, $"Pressed review input '{canonicalButton}' for one input tick.")
                : Failure(error);
        }

        if (request.Kind == ReviewInputKind.Scroll)
        {
            int direction = string.Equals(
                request.Button,
                "MouseWheelUp",
                StringComparison.OrdinalIgnoreCase)
                ? 120
                : -120;
            string canonicalButton = direction > 0 ? "MouseWheelUp" : "MouseWheelDown";
            return runtime.TryScroll(direction, out string error)
                ? new ReviewInputResult(true, $"Pressed review input '{canonicalButton}' for one mouse-wheel notch.")
                : Failure(error);
        }

        if (request.Kind != ReviewInputKind.Cursor
            || request.X < 0
            || request.Y < 0
            || request.X >= runtime.UiWidth
            || request.Y >= runtime.UiHeight)
        {
            return Failure(
                $"Review cursor coordinates must be inside the current UI viewport "
                + $"{runtime.UiWidth}x{runtime.UiHeight}.");
        }

        return runtime.TrySetCursor(request.X, request.Y, out string cursorError)
            ? new ReviewInputResult(
                true,
                $"Set the virtual review cursor to UI coordinate {request.X},{request.Y}; the physical pointer was not moved.")
            : Failure(cursorError);
    }

    private static ReviewInputResult Failure(string message) => new(false, message);
}

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewInputCommand
{
    public static void Handle(
        string[] arguments,
        IReviewInputRuntime runtime,
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
            monitor.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit input command failed without confirming input: {exception.Message}",
                LogLevel.Error);
        }
    }
}

internal sealed class StardewReviewInputRuntime(IModHelper helper) : IReviewInputRuntime
{
    public int UiWidth => Game1.uiViewport.Width;

    public int UiHeight => Game1.uiViewport.Height;

    public bool TryPress(string button, out string canonicalButton, out string error)
    {
        if (!Enum.TryParse(button, ignoreCase: true, out SButton parsed)
            || !Enum.IsDefined(parsed)
            || parsed == SButton.None)
        {
            canonicalButton = string.Empty;
            error = $"'{button}' is not one exact SMAPI SButton name.";
            return false;
        }

        helper.Input.Press(parsed);
        ReviewVirtualCursor.AllowBackgroundInputForNextTicks();
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

        Game1.activeClickableMenu.receiveScrollWheelAction(direction);
        error = string.Empty;
        return true;
    }

    public bool TrySetCursor(int x, int y, out string error)
    {
        return ReviewVirtualCursor.TrySet(x, y, out error);
    }

    public bool TryClearCursor(out string error)
    {
        ReviewVirtualCursor.Clear();
        error = string.Empty;
        return true;
    }
}

internal static class ReviewVirtualCursor
{
    private const string HarmonyId = "SDVKit.AlwaysOn.VirtualReviewCursor";

    private static readonly object Sync = new();
    private static bool _installed;
    private static int? _uiX;
    private static int? _uiY;
    private static int _backgroundInputThroughTick = -1;

    public static bool IsSet
    {
        get
        {
            lock (Sync)
            {
                return _uiX is not null && _uiY is not null;
            }
        }
    }

    public static bool TryInstall(out string error)
    {
        lock (Sync)
        {
            if (_installed)
            {
                error = string.Empty;
                return true;
            }

            MethodInfo? getMouseState = AccessTools.Method(
                "StardewModdingAPI.Framework.Input.SInputState:GetMouseState");
            MethodInfo? postfix = AccessTools.Method(
                typeof(ReviewVirtualCursor),
                nameof(AfterGetMouseState));
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
                    isActiveNoOverlay,
                    postfix: new HarmonyMethod(activePostfix));
                harmony.Patch(
                    isActive,
                    postfix: new HarmonyMethod(activePostfix));
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

            _uiX = uiX;
            _uiY = uiY;
            error = string.Empty;
            return true;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _uiX = null;
            _uiY = null;
            _backgroundInputThroughTick = -1;
        }
    }

    public static void AllowBackgroundInputForNextTicks()
    {
        lock (Sync)
        {
            _backgroundInputThroughTick = Game1.ticks + 4;
        }
    }

    private static void AfterGetMouseState(ref MouseState __result)
    {
        int uiX;
        int uiY;
        lock (Sync)
        {
            if (_uiX is not int storedX || _uiY is not int storedY)
            {
                return;
            }

            uiX = Math.Clamp(storedX, 0, Math.Max(0, Game1.uiViewport.Width - 1));
            uiY = Math.Clamp(storedY, 0, Math.Max(0, Game1.uiViewport.Height - 1));
        }

        float uiScale = Game1.options?.uiScale ?? 1f;
        int rawX = (int)Math.Round(uiX * uiScale, MidpointRounding.AwayFromZero);
        int rawY = (int)Math.Round(uiY * uiScale, MidpointRounding.AwayFromZero);
        __result = new MouseState(
            rawX,
            rawY,
            __result.ScrollWheelValue,
            __result.LeftButton,
            __result.MiddleButton,
            __result.RightButton,
            __result.XButton1,
            __result.XButton2);
    }

    private static void AfterGetReviewActivity(ref bool __result)
    {
        lock (Sync)
        {
            if (_backgroundInputThroughTick >= Game1.ticks)
            {
                __result = true;
            }
        }
    }
}
#endif

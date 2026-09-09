#if SDVKIT_GAME_AVAILABLE
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static class BackgroundOptionsReplacement
{
    private const string HarmonyId = "SDVKit.AlwaysOn.BackgroundOptionsReplacement";
    private static Func<BackgroundRunGuard?>? _guard;
    private static IMonitor? _monitor;

    public static void Install(Func<BackgroundRunGuard?> guard, IMonitor monitor)
    {
        // Stardew replaces Options during FarmhandSlot.Activate and again when
        // ClientOptions_Load completes. Hook the setter so either replacement is
        // rebound before an unfocused client can pause.
        var original = AccessTools.PropertySetter(typeof(Game1), nameof(Game1.options))
            ?? throw new MissingMethodException(typeof(Game1).FullName, $"set_{nameof(Game1.options)}");
        var postfix = AccessTools.DeclaredMethod(typeof(BackgroundOptionsReplacement), nameof(AfterLoad));
        new Harmony(HarmonyId).Patch(original, postfix: new HarmonyMethod(postfix));
        _guard = guard;
        _monitor = monitor;
    }

    private static void AfterLoad()
    {
        try
        {
            _guard?.Invoke()?.RecaptureAfterOptionsReplacement();
        }
        catch (Exception exception)
        {
            _monitor?.Log($"Could not preserve background execution after Stardew replaced options: {exception.Message}", LogLevel.Error);
        }
    }
}
#endif

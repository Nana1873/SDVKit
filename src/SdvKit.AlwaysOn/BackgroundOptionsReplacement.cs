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
        // FarmhandSlot.Activate replaces Options synchronously. Waiting for the
        // next validated update can leave an unfocused client paused in loading.
        var original = AccessTools.DeclaredMethod(typeof(Game1), nameof(Game1.loadForNewGame), new[] { typeof(bool) })
            ?? throw new MissingMethodException(typeof(Game1).FullName, nameof(Game1.loadForNewGame));
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

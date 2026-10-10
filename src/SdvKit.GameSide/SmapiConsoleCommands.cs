#if SDVKIT_GAME_AVAILABLE
using System.Reflection;
using StardewModdingAPI;

namespace SdvKit.GameSide;

/// <summary>
/// Runs another SMAPI console command, such as one registered by the reviewed mod, exactly as if
/// it had been typed: SMAPI exposes no public API for this, so the command is looked up in SMAPI's
/// own command manager. A missing command or an unsupported SMAPI version fails closed.
/// </summary>
internal static class SmapiConsoleCommands
{
    private static readonly Lazy<Func<string, Action<string, string[]>?>?> Lookup = new(CreateLookup);

    /// <returns>False when the command does not exist, so it did not run.</returns>
    public static bool TryRun(string name, string[] arguments, IMonitor monitor)
    {
        Func<string, Action<string, string[]>?>? lookup = Lookup.Value;
        if (lookup is null)
        {
            monitor.Log("SDVKit cannot run other console commands with this SMAPI version.", LogLevel.Error);
            return false;
        }

        Action<string, string[]>? callback = lookup(name);
        if (callback is null)
        {
            monitor.Log($"SDVKit review command rejected: SMAPI has no console command '{name}'.", LogLevel.Error);
            return false;
        }

        // SMAPI records typed input; keep the same evidence in the isolated log.
        monitor.Log($"SDVKit review runs console command: {string.Join(' ', [name, .. arguments])}", LogLevel.Info);
        try
        {
            callback(name, arguments);
            return true;
        }
        catch (Exception exception)
        {
            monitor.Log($"The console command '{name}' failed: {exception}", LogLevel.Error);
            throw;
        }
    }

    private static Func<string, Action<string, string[]>?>? CreateLookup()
    {
        try
        {
            return CreateLookupCore();
        }
        catch (Exception exception) when (exception is AmbiguousMatchException
            or TargetException
            or TargetInvocationException
            or MemberAccessException)
        {
            // A changed SMAPI layout fails closed: other console commands are rejected, never run.
            return null;
        }
    }

    private static Func<string, Action<string, string[]>?>? CreateLookupCore()
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
        Type? core = typeof(IModHelper).Assembly.GetType("StardewModdingAPI.Framework.SCore");
        object? instance = core?.GetProperty("Instance", any | BindingFlags.Static)?.GetValue(null);
        object? manager = core?.GetField("CommandManager", any | BindingFlags.Instance)?.GetValue(instance);
        MethodInfo? get = manager?.GetType().GetMethod("Get", any | BindingFlags.Instance, [typeof(string)]);
        if (manager is null || get is null)
        {
            return null;
        }

        return name =>
        {
            object? command = get.Invoke(manager, [name]);
            return command?.GetType().GetProperty("Callback", any | BindingFlags.Instance)?.GetValue(command)
                as Action<string, string[]>;
        };
    }
}
#endif

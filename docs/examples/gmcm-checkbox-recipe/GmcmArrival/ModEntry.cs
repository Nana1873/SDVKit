using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace GmcmArrival;

public sealed class ModConfig
{
    public bool Enabled { get; set; }
}

public sealed class ModEntry : Mod
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string SupportedGmcmVersion = "1.16.0";

    private ModConfig Config = new();
    private IGenericModConfigMenuApi? GmcmApi;
    private string GmcmUnavailableReason = "GMCM initialization has not run yet.";

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.ConsoleCommands.Add(
            "gmcm_arrival_open",
            "Open this mod's GMCM page. A loaded world and GMCM 1.16.0 are required.",
            OnOpenMenuCommand);
        helper.ConsoleCommands.Add(
            "gmcm_arrival_state",
            "Report the current in-memory config value.",
            OnStateCommand);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        IModInfo? provider = Helper.ModRegistry.Get(GmcmId);
        if (provider is null)
        {
            GmcmUnavailableReason = $"Required provider {GmcmId} {SupportedGmcmVersion} is not installed.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        string installedVersion = provider.Manifest.Version.ToString();
        if (!string.Equals(installedVersion, SupportedGmcmVersion, StringComparison.Ordinal))
        {
            GmcmUnavailableReason =
                $"Unsupported {GmcmId} version {installedVersion}; this example requires exactly {SupportedGmcmVersion}.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        IGenericModConfigMenuApi? api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (api is null)
        {
            GmcmUnavailableReason =
                $"{GmcmId} {SupportedGmcmVersion} did not expose the expected GMCM API.";
            Monitor.Log(GmcmUnavailableReason, LogLevel.Error);
            return;
        }

        GmcmApi = api;
        GmcmUnavailableReason = string.Empty;

        api.Register(
            manifest: ModManifest,
            reset: () => Config = new ModConfig(),
            save: () => Helper.WriteConfig(Config),
            titleScreenOnly: false);
        api.AddBoolOption(
            manifest: ModManifest,
            getValue: () => Config.Enabled,
            setValue: value => Config.Enabled = value,
            name: () => "Enabled",
            tooltip: () => "Warp the local player to Farm tile 64,15 when a day starts.",
            fieldId: "Enabled");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer || !Config.Enabled)
            return;

        Game1.warpFarmer("Farm", 64, 15, false);
    }

    private void OnOpenMenuCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("gmcm_arrival_open requires a loaded world.", LogLevel.Warn);
            return;
        }

        if (GmcmApi is null)
        {
            Monitor.Log($"GMCM menu unavailable: {GmcmUnavailableReason}", LogLevel.Warn);
            return;
        }

        GmcmApi.OpenModMenu(ModManifest);
    }

    private void OnStateCommand(string command, string[] args)
    {
        Monitor.Log($"Enabled={Config.Enabled.ToString().ToLowerInvariant()}", LogLevel.Info);
    }
}

public interface IGenericModConfigMenuApi
{
    void Register(IManifest manifest, Action reset, Action save, bool titleScreenOnly = true);

    void AddBoolOption(
        IManifest manifest,
        Func<bool> getValue,
        Action<bool> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        string? fieldId = null);

    void OpenModMenu(IManifest manifest);
}

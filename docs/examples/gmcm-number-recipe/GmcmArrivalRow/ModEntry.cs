using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace GmcmArrivalRow;

public sealed class ModConfig
{
    public int ArrivalRow { get; set; } = ModEntry.DefaultArrivalRow;
}

public sealed class ModEntry : Mod
{
    public const int MinimumArrivalRow = 15;
    public const int MaximumArrivalRow = 19;
    public const int ArrivalRowInterval = 2;
    public const int DefaultArrivalRow = 15;

    private const int MinimumSliderStep = 0;
    private const int MaximumSliderStep = 2;
    private const int SliderStepInterval = 1;

    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string SupportedGmcmVersion = "1.16.0";

    private ModConfig Config = new();
    private IGenericModConfigMenuApi? GmcmApi;
    private string GmcmUnavailableReason = "GMCM initialization has not run yet.";

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        if (!IsSupportedArrivalRow(Config.ArrivalRow))
        {
            Monitor.Log(
                $"Invalid ArrivalRow={Config.ArrivalRow}; using default {DefaultArrivalRow}. " +
                $"Expected {MinimumArrivalRow}..{MaximumArrivalRow} in steps of {ArrivalRowInterval}.",
                LogLevel.Error);
            Config.ArrivalRow = DefaultArrivalRow;
        }

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.ConsoleCommands.Add(
            "gmcm_arrival_row_open",
            "Open this mod's GMCM page. A loaded world and GMCM 1.16.0 are required.",
            OnOpenMenuCommand);
        helper.ConsoleCommands.Add(
            "gmcm_arrival_row_state",
            "Report the current in-memory arrival row.",
            OnStateCommand);
    }

    private static bool IsSupportedArrivalRow(int value)
    {
        return value >= MinimumArrivalRow &&
            value <= MaximumArrivalRow &&
            (value - MinimumArrivalRow) % ArrivalRowInterval == 0;
    }

    private static int ArrivalRowToSliderStep(int arrivalRow)
    {
        return (arrivalRow - MinimumArrivalRow) / ArrivalRowInterval;
    }

    private static int SliderStepToArrivalRow(int sliderStep)
    {
        int boundedStep = Math.Clamp(sliderStep, MinimumSliderStep, MaximumSliderStep);
        return MinimumArrivalRow + boundedStep * ArrivalRowInterval;
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
        api.AddNumberOption(
            manifest: ModManifest,
            getValue: () => ArrivalRowToSliderStep(Config.ArrivalRow),
            setValue: step => Config.ArrivalRow = SliderStepToArrivalRow(step),
            name: () => "Arrival row",
            tooltip: () => "Warp the local player to Farm tile 64 on this row when a day starts.",
            min: MinimumSliderStep,
            max: MaximumSliderStep,
            interval: SliderStepInterval,
            formatValue: step => $"Row {SliderStepToArrivalRow(step)}",
            fieldId: "ArrivalRow");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Context.IsMultiplayer)
            return;

        Game1.warpFarmer("Farm", 64, Config.ArrivalRow, false);
    }

    private void OnOpenMenuCommand(string command, string[] args)
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("gmcm_arrival_row_open requires a loaded world.", LogLevel.Warn);
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
        Monitor.Log($"ArrivalRow={Config.ArrivalRow}", LogLevel.Info);
    }
}

public interface IGenericModConfigMenuApi
{
    void Register(IManifest manifest, Action reset, Action save, bool titleScreenOnly = true);

    void AddNumberOption(
        IManifest manifest,
        Func<int> getValue,
        Action<int> setValue,
        Func<string> name,
        Func<string>? tooltip = null,
        int? min = null,
        int? max = null,
        int? interval = null,
        Func<int, string>? formatValue = null,
        string? fieldId = null);

    void OpenModMenu(IManifest manifest);
}

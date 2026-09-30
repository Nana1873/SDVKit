using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
#endif

namespace SdvKit.AlwaysOn;

#if SDVKIT_GAME_AVAILABLE
internal static class ReviewFixtureCommand
{
    public static void Handle(
        string[] arguments,
        IReviewFixtureRuntime runtime,
        IMonitor monitor,
        string? runtimePath = null,
        Func<TestSaveAutomation?>? testSave = null)
    {
        if (arguments.Length > 1
            && ReviewTransportToken.IsRequestId(arguments[1]))
        {
            if (string.IsNullOrWhiteSpace(runtimePath) || testSave is null)
            {
                monitor.Log(
                    "SDVKit review-fixture transport is unavailable.",
                    LogLevel.Error);
                return;
            }

            ReviewFixtureTransportCommand.Handle(
                arguments,
                runtime,
                testSave,
                runtimePath,
                monitor);
            return;
        }

        if (!ReviewFixtureArguments.TryParse(arguments, out ReviewFixtureRequest? request, out string error))
        {
            monitor.Log(error, LogLevel.Error);
            return;
        }

        try
        {
            ReviewFixtureResult result = ReviewFixtureOperation.Execute(request!, runtime);
            monitor.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit fixture command failed closed: {exception.GetBaseException().Message}",
                LogLevel.Error);
        }
    }
}
#endif

using SdvKit.Cli.LiveLab;

#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using StardewValley;
#endif

namespace SdvKit.GameSide;

#if SDVKIT_GAME_AVAILABLE
/// <summary>
/// Lab world, player and window changes for the owned disposable fixture. Time and season use the
/// game's own debug commands, as a player typing them would; weather sets the current location's
/// weather state, and zoom and window use the same options the in-game menu changes.
/// </summary>
internal sealed partial class StardewReviewFixtureRuntime
{
    public ReviewFixtureResult ApplyLab(ReviewFixtureAccess access, ReviewFixtureRequest request)
    {
        string? error = request switch
        {
            ReviewFixtureSetTimeRequest time => Debug("time", time.Time.ToString(CultureInfo.InvariantCulture)),
            ReviewFixtureSetSeasonRequest season => Debug("season", season.Season),
            ReviewFixtureSetWeatherRequest weather => SetWeather(weather.Weather),
            ReviewFixtureWarpRequest warp => Warp(warp),
            ReviewFixtureSetZoomRequest zoom => SetZoom(zoom.Zoom),
            ReviewFixtureSetWindowRequest window => SetWindow(window.Width, window.Height),
            _ => "The lab action is unknown.",
        };
        if (error is not null)
        {
            return new ReviewFixtureResult(false, error);
        }

        string message = request switch
        {
            ReviewFixtureWarpRequest warp =>
                $"Warp to '{warp.Location}' requested; the location changes within a few frames.",
            ReviewFixtureSetTimeRequest =>
                "Time set; ambient light catches up at the next ten-minute tick or location change.",
            ReviewFixtureSetSeasonRequest =>
                "Season set; map tiles refresh at the next location change.",
            _ => "Lab state applied.",
        };
        return new ReviewFixtureResult(true, message, Lab: CaptureLab());
    }

    private static string? Debug(params string[] command) =>
        DebugCommands.TryHandle(command, null)
            ? null
            : $"The game's '{command[0]}' debug command is unavailable.";

    private static string? SetWeather(string weather)
    {
        if (Game1.currentLocation is not { } location)
        {
            return "The current location is unavailable.";
        }

        var state = location.GetWeather();
        state.Weather = weather;
        state.IsRaining = weather is "Rain" or "Storm" or "GreenRain";
        state.IsLightning = weather == "Storm";
        state.IsSnowing = weather == "Snow";
        state.IsDebrisWeather = weather == "Wind";
        state.IsGreenRain = weather == "GreenRain";
        if (location.GetLocationContextId() == "Default")
        {
            Game1.isRaining = state.IsRaining;
            Game1.isLightning = state.IsLightning;
            Game1.isSnowing = state.IsSnowing;
            Game1.isDebrisWeather = state.IsDebrisWeather;
            Game1.isGreenRain = state.IsGreenRain;
        }

        if (state.IsDebrisWeather)
        {
            Game1.populateDebrisWeatherArray();
        }
        else
        {
            Game1.debrisWeather.Clear();
        }

        Game1.updateWeatherIcon();
        return null;
    }

    private static string? Warp(ReviewFixtureWarpRequest warp)
    {
        if (Game1.getLocationFromName(warp.Location) is null)
        {
            return $"The location '{warp.Location}' does not exist.";
        }

        return warp.X is int x && warp.Y is int y
            ? Debug("warp", warp.Location, x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture))
            : Debug("warp", warp.Location);
    }

    private static string? SetZoom(double zoom)
    {
        Game1.options.desiredBaseZoomLevel = (float)zoom;
        Game1.game1.refreshWindowSettings();
        return null;
    }

    private static string? SetWindow(int width, int height)
    {
        if (!Game1.options.isCurrentlyWindowed())
        {
            Game1.options.setWindowedOption("Windowed");
        }

        Game1.game1.SetWindowSize(width, height);
        return null;
    }

    private static ReviewFixtureLabReport CaptureLab()
    {
        GameLocation? location = Game1.currentLocation;
        Microsoft.Xna.Framework.Rectangle window = Game1.game1.Window.ClientBounds;
        return new ReviewFixtureLabReport(
            Game1.timeOfDay,
            Game1.currentSeason ?? string.Empty,
            location?.GetWeather().Weather ?? string.Empty,
            location?.NameOrUniqueName ?? string.Empty,
            Game1.player.TilePoint.X,
            Game1.player.TilePoint.Y,
            Game1.options.desiredBaseZoomLevel,
            window.Width,
            window.Height);
    }
}
#endif

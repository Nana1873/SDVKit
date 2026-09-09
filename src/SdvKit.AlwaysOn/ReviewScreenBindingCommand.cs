#if SDVKIT_GAME_AVAILABLE
using System.Runtime.CompilerServices;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;

namespace SdvKit.AlwaysOn;

internal static class ReviewScreenBindingCommand
{
    private sealed class ContextIdentity
    {
        internal string Value { get; } = Guid.NewGuid().ToString("N");
    }

    private static readonly ConditionalWeakTable<Game1, ContextIdentity> Contexts = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool TryValidateDispatch(
        string[] arguments,
        TestSaveAutomation? fixture,
        out string[] commandArguments,
        out string error)
    {
        commandArguments = arguments;
        error = string.Empty;
        bool hasBinding = arguments.Any(value => value.StartsWith("binding=", StringComparison.Ordinal)
            || value.StartsWith("farmer=", StringComparison.Ordinal));
        if (!hasBinding) return true;
        try
        {
            TestSaveAutomation owned = fixture
                ?? throw new InvalidOperationException("An owned disposable fixture review is required.");
            owned.VerifyCurrentLocalScreen();
            if (!ReviewScreenBindingContract.TryValidateDispatch(
                    arguments,
                    Game1.player.UniqueMultiplayerID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Contexts.GetValue(Game1.game1, _ => new ContextIdentity()).Value,
                    out commandArguments))
            {
                throw new InvalidOperationException("The selected local screen or farmer context changed.");
            }
            return true;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static void Handle(
        string[] arguments,
        string runtimePath,
        TestSaveAutomation? fixture,
        IMonitor monitor)
    {
        if (arguments.Length != 4
            || !string.Equals(arguments[0], "screen-binding", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "request", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[2])
            || !ReviewTransportToken.IsRequestId(arguments[3]))
        {
            monitor.Log("Usage: sdvkit screen-binding request <request-id> <launch-id> screen=<id>", LogLevel.Error);
            return;
        }

        try
        {
            TestSaveAutomation owned = fixture
                ?? throw new InvalidOperationException("An owned disposable fixture review is required.");
            owned.VerifyCurrentLocalScreen();
            string launchId = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")?.Trim()
                ?? string.Empty;
            if (!ReviewTransportToken.IsRequestId(launchId))
                throw new InvalidOperationException("The owned launch identity is unavailable.");
            if (!string.Equals(arguments[3], launchId, StringComparison.Ordinal))
                throw new InvalidOperationException("The screen-binding request targets another launch.");

            string requestId = arguments[2];
            var response = new ReviewScreenBindingResponse(
                ReviewScreenBindingContract.SchemaVersion,
                requestId,
                new ReviewScreenBindingReport(
                    ReviewScreenBindingContract.SchemaVersion,
                    launchId,
                    owned.Snapshot.FixtureId,
                    Context.ScreenId,
                    Game1.player.UniqueMultiplayerID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Contexts.GetValue(Game1.game1, _ => new ContextIdentity()).Value,
                    DateTimeOffset.UtcNow,
                    ModEntry.CaptureRuntimeSnapshot()));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            if (bytes.Length > ReviewScreenBindingContract.MaximumResponseBytes)
                throw new InvalidDataException("The screen-binding response exceeds its bounded maximum.");
            ReviewResponseFile.Write(
                ReviewScreenBindingContract.ResponsePath(runtimePath, requestId),
                bytes);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            monitor.Log($"SDVKit screen binding rejected: {exception.GetBaseException().Message}", LogLevel.Error);
        }
    }
}
#endif

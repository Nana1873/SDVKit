using System.Globalization;
using System.Text;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using System.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SdvKit.GameSide;

internal sealed class StardewReviewModAssetSource : IReviewModAssetSource
{
    private readonly IModHelper _helper;
    private readonly ReviewModAssetCatalog _catalogue;
    private readonly ReviewModAssetQueryObservationGuard _queryObservationGuard = new();

    public StardewReviewModAssetSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        _catalogue = new ReviewModAssetCatalog(
            ReviewModAssetRegistryReader.Read(() =>
                helper.ModRegistry.GetAll()
                    .Select(mod => mod.Manifest.UniqueID)
                    .Where(id => !string.Equals(
                        id,
                        "SDVKit.GameSide",
                        StringComparison.OrdinalIgnoreCase))));
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public ReviewModAssetInventorySnapshot GetInventory() => _catalogue.Snapshot();

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        string assetName = eventArgs.NameWithoutLocale.Name;
        if (!_queryObservationGuard.SuppressesRequested(assetName, eventArgs.DataType))
        {
            _catalogue.ObserveRequested(assetName, eventArgs.DataType);
        }
    }

    public void OnAssetReady(object? sender, AssetReadyEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        string assetName = eventArgs.NameWithoutLocale.Name;
        if (!_queryObservationGuard.SuppressesReady(assetName))
        {
            _catalogue.ObserveReady(assetName);
        }
    }

    public void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        _catalogue.ObserveInvalidated(
            eventArgs.NamesWithoutLocale.Select(name => name.Name));
    }

    public ReviewModAssetLoadResult Load(ReviewModAssetObservation asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!ReviewModAssetAdapterRegistry.TryGet(
                asset.DataType,
                out ReviewModAssetAdapterKind adapter,
                out _,
                out _))
        {
            return new ReviewModAssetLoadResult(
                false,
                null,
                "modAssetAdapterUnavailable");
        }

        try
        {
            object value;
            using (_queryObservationGuard.Enter(asset.AssetName, asset.DataType))
            {
                value = adapter switch
                {
                    ReviewModAssetAdapterKind.StringDictionary =>
                        _helper.GameContent.Load<Dictionary<string, string>>(asset.AssetName),
                    ReviewModAssetAdapterKind.IntegerDictionary =>
                        _helper.GameContent.Load<Dictionary<string, int>>(asset.AssetName),
                    ReviewModAssetAdapterKind.IntegerKeyStringDictionary =>
                        _helper.GameContent.Load<Dictionary<int, string>>(asset.AssetName),
                    ReviewModAssetAdapterKind.IntegerKeyIntegerDictionary =>
                        _helper.GameContent.Load<Dictionary<int, int>>(asset.AssetName),
                    ReviewModAssetAdapterKind.StringList =>
                        _helper.GameContent.Load<List<string>>(asset.AssetName),
                    ReviewModAssetAdapterKind.StringSingleton =>
                        _helper.GameContent.Load<string>(asset.AssetName),
                    _ => throw new InvalidOperationException(
                        "The observed asset has no reviewed adapter."),
                };
            }
            if (value.GetType() != asset.DataType)
            {
                _catalogue.MarkUnavailable(asset.AssetName, asset.DataType);
                return new ReviewModAssetLoadResult(
                    true,
                    value,
                    "modAssetTypeChanged");
            }

            _catalogue.MarkVerifiedReady(asset.AssetName, asset.DataType);
            return new ReviewModAssetLoadResult(true, value, null);
        }
        catch (Exception exception) when (exception is ArgumentException
            or Microsoft.Xna.Framework.Content.ContentLoadException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or NotSupportedException)
        {
            _catalogue.MarkUnavailable(asset.AssetName, asset.DataType);
            return new ReviewModAssetLoadResult(
                false,
                null,
                "modAssetUnavailable");
        }
    }
}

internal static class ReviewModAssetCommand
{
    private const string MissingToken = "-";

    public static void Handle(
        string[] arguments,
        IReviewModAssetSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-mod-assets runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewTransportToken.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-mod-assets rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewModAssetReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewModAssetOperation.Failure(
                operation,
                source,
                new ReviewModAssetProblem(
                    "modAssetReviewTopologyUnsupported",
                    "Review-mod-assets queries require an active owned single project review."));
        }
        else if (!TryParse(
                arguments,
                out ReviewModAssetQuery? query,
                out ReviewModAssetProblem? problem))
        {
            string operation = arguments.Length > 2 ? arguments[2] : "unknown";
            report = ReviewModAssetOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewModAssetOperation.Execute(query!, source);
            }
            catch (Exception exception) when (!ReviewException.IsFatal(exception))
            {
                report = ReviewModAssetOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewModAssetProblem(
                        "modAssetQueryFailed",
                        $"The review-mod-assets query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewModAssetResponseEnvelope(
            ReviewModAssetContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            ReviewModAssetResponseWriter.Write(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-mod-assets completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            monitor.Log(
                $"SDVKit review-mod-assets could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewModAssetQuery? query,
        out ReviewModAssetProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count != 7
            || !string.Equals(arguments[0], "mod-assets", StringComparison.Ordinal)
            || !ReviewTransportToken.IsRequestId(arguments[1])
            || !int.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int offset)
            || !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit)
            || !TryDecodeOptional(
                arguments[5],
                ReviewModAssetContract.MaximumAssetLength,
                out string? asset)
            || !TryDecodeOptional(
                arguments[6],
                ReviewModAssetContract.MaximumKeyLength,
                out string? key))
        {
            problem = new ReviewModAssetProblem(
                "modAssetTransportInvalid",
                "The bounded review-mod-assets transport request is invalid.");
            return false;
        }

        query = new ReviewModAssetQuery(
            arguments[2],
            asset,
            key,
            offset,
            limit);
        return true;
    }

    private static bool TryDecodeOptional(
        string token,
        int maximumLength,
        out string? value)
    {
        if (string.Equals(token, MissingToken, StringComparison.Ordinal))
        {
            value = null;
            return true;
        }

        if (ReviewTransportToken.TryDecode(token, maximumLength, out string decoded))
        {
            value = decoded;
            return true;
        }

        value = null;
        return false;
    }

}

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using StardewModdingAPI;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal interface IReviewDataSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    IReadOnlyList<string> DiscoverCanonicalAssetNames();

    object LoadAsset(string assetName);
}

internal sealed record ReviewDataRecord(string Key, object? Value);

internal sealed record ReviewDataAssetSnapshot(
    ReviewDataAssetReport Report,
    IReadOnlyList<ReviewDataRecord> Records);

internal static class ReviewDataOperation
{
    private const int MaximumDiscoveredAssets = 2048;
    private const int MaximumRecordsPerAsset = 1_000_000;

    public static ReviewDataReport Execute(
        ReviewDataQuery query,
        IReviewDataSource source)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(source);

        ReviewDataProblem? requestProblem = Validate(query);
        if (requestProblem is not null)
        {
            return Failure(query.Operation, source, requestProblem);
        }

        IReadOnlyList<string> discovered;
        try
        {
            discovered = source
                .DiscoverCanonicalAssetNames()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "dataInventoryFailed",
                    $"The installed canonical Data asset inventory could not be read ({exception.GetType().Name})."));
        }

        if (discovered.Count > MaximumDiscoveredAssets)
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "dataInventoryTooLarge",
                    $"The installed canonical Data asset inventory exceeds the bounded maximum of {MaximumDiscoveredAssets} assets."));
        }

        return query.Operation switch
        {
            ReviewDataContract.AssetsOperation =>
                ListAssets(query, source, discovered),
            ReviewDataContract.KeysOperation =>
                ListKeys(query, source, discovered),
            ReviewDataContract.GetOperation =>
                GetRecord(query, source, discovered),
            _ => Failure(
                query.Operation,
                source,
                Problem("dataOperationUnknown", "The review-data operation is unknown.")),
        };
    }

    public static ReviewDataReport Failure(
        string operation,
        IReviewDataSource source,
        ReviewDataProblem problem) =>
        new(
            ReviewDataContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [problem]);

    private static ReviewDataReport ListAssets(
        ReviewDataQuery query,
        IReviewDataSource source,
        IReadOnlyList<string> discovered)
    {
        IReadOnlyDictionary<string, int> collisionCounts = discovered
            .GroupBy(ReviewFixtureKindResolver.Normalize, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var reports = new List<ReviewDataAssetReport>(discovered.Count);
        var unknown = 0;
        var unclassified = 0;
        var unsupported = 0;
        foreach (string assetName in discovered)
        {
            if (!IsCanonicalAssetName(assetName))
            {
                unknown++;
                reports.Add(new ReviewDataAssetReport(
                    assetName,
                    null,
                    null,
                    null,
                    null,
                    false,
                    "dataAssetNameInvalid"));
                continue;
            }

            string normalized = ReviewFixtureKindResolver.Normalize(assetName);
            if (normalized.Length == 0 || collisionCounts[normalized] != 1)
            {
                unsupported++;
                reports.Add(new ReviewDataAssetReport(
                    assetName,
                    null,
                    null,
                    null,
                    null,
                    false,
                    "dataAssetNormalizationCollision"));
                continue;
            }

            if (!TryLoadSnapshot(
                    source,
                    assetName,
                    out ReviewDataAssetSnapshot? snapshot,
                    out bool loadFailed))
            {
                if (loadFailed)
                {
                    unclassified++;
                }
                else
                {
                    unsupported++;
                }

                reports.Add(snapshot!.Report);
                continue;
            }

            reports.Add(snapshot!.Report);
        }

        int supported = reports.Count(report => report.Supported);
        int classified = discovered.Count - unknown - unclassified;
        var coverage = new ReviewDataCoverageReport(
            discovered.Count,
            classified,
            supported,
            unknown,
            unclassified,
            unsupported);
        IReadOnlyList<ReviewDataAssetReport> page = reports
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return new ReviewDataReport(
            ReviewDataContract.SchemaVersion,
            coverage.Complete ? "ready" : "blocked",
            query.Operation,
            source.GameVersion,
            source.GameFileVersion,
            null,
            null,
            null,
            null,
            null,
            page,
            null,
            Page(query, page.Count, reports.Count),
            coverage,
            null,
            coverage.Complete
                ? []
                : [Problem(
                    "dataCoverageIncomplete",
                    "The installed canonical Data asset inventory contains unknown, unclassified, or unsupported assets.")]);
    }

    private static ReviewDataReport ListKeys(
        ReviewDataQuery query,
        IReviewDataSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveAsset(
                query.Asset!,
                discovered,
                out string? assetName,
                out ReviewDataProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        if (!TryLoadSnapshot(
                source,
                assetName!,
                out ReviewDataAssetSnapshot? snapshot,
                out _))
        {
            return AssetFailure(query.Operation, source, snapshot!);
        }

        IReadOnlyList<string> keys = snapshot!.Records
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(record => record.Key)
            .ToArray();
        return Success(
            query,
            source,
            snapshot,
            key: null,
            keys,
            Page(query, keys.Count, snapshot.Records.Count),
            record: null);
    }

    private static ReviewDataReport GetRecord(
        ReviewDataQuery query,
        IReviewDataSource source,
        IReadOnlyList<string> discovered)
    {
        if (!TryResolveAsset(
                query.Asset!,
                discovered,
                out string? assetName,
                out ReviewDataProblem? problem))
        {
            return Failure(query.Operation, source, problem!);
        }

        if (!TryLoadSnapshot(
                source,
                assetName!,
                out ReviewDataAssetSnapshot? snapshot,
                out _))
        {
            return AssetFailure(query.Operation, source, snapshot!);
        }

        if (!TryResolveKey(
                query.Key!,
                snapshot!,
                out ReviewDataRecord? selected,
                out problem))
        {
            return AssetFailure(query.Operation, source, snapshot!, problem!);
        }

        if (!ReviewDataJson.TrySerialize(
                selected!.Value,
                out JsonElement record,
                out string? serializationError))
        {
            return AssetFailure(
                query.Operation,
                source,
                snapshot!,
                Problem(
                    "dataRecordNotSafelySerializable",
                    $"The selected record cannot be serialized safely ({serializationError})."));
        }

        return Success(
            query,
            source,
            snapshot!,
            selected.Key,
            keys: null,
            page: null,
            record);
    }

    private static ReviewDataReport Success(
        ReviewDataQuery query,
        IReviewDataSource source,
        ReviewDataAssetSnapshot snapshot,
        string? key,
        IReadOnlyList<string>? keys,
        ReviewDataPage? page,
        JsonElement? record) =>
        new(
            ReviewDataContract.SchemaVersion,
            "ready",
            query.Operation,
            source.GameVersion,
            source.GameFileVersion,
            snapshot.Report.AssetName,
            snapshot.Report.DataType,
            snapshot.Report.Shape,
            snapshot.Report.KeyKind,
            key,
            null,
            keys,
            page,
            null,
            record,
            []);

    private static ReviewDataReport AssetFailure(
        string operation,
        IReviewDataSource source,
        ReviewDataAssetSnapshot snapshot,
        ReviewDataProblem? problem = null) =>
        new(
            ReviewDataContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            snapshot.Report.AssetName,
            snapshot.Report.DataType,
            snapshot.Report.Shape,
            snapshot.Report.KeyKind,
            null,
            null,
            null,
            null,
            null,
            null,
            [problem ?? Problem(
                snapshot.Report.ProblemCode ?? "dataAssetUnsupported",
                "The canonical Data asset is not safely queryable.")]);

    private static ReviewDataProblem? Validate(ReviewDataQuery query)
    {
        if (query.Operation is not (
                ReviewDataContract.AssetsOperation
                or ReviewDataContract.KeysOperation
                or ReviewDataContract.GetOperation))
        {
            return Problem("dataOperationUnknown", "The review-data operation is unknown.");
        }

        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewDataContract.MaximumPageLimit)
        {
            return Problem(
                "dataPaginationInvalid",
                $"Offset must be non-negative and limit must be between 1 and {ReviewDataContract.MaximumPageLimit}.");
        }

        bool needsAsset = query.Operation is ReviewDataContract.KeysOperation
            or ReviewDataContract.GetOperation;
        bool needsKey = query.Operation is ReviewDataContract.GetOperation;
        if (needsAsset
            && (string.IsNullOrWhiteSpace(query.Asset)
                || query.Asset.Length > ReviewDataContract.MaximumAssetLength
                || query.Asset.Any(char.IsControl)))
        {
            return Problem("dataAssetInvalid", "A bounded non-empty Data asset name is required.");
        }

        if (needsKey
            && (string.IsNullOrWhiteSpace(query.Key)
                || query.Key.Length > ReviewDataContract.MaximumKeyLength
                || query.Key.Any(char.IsControl)))
        {
            return Problem("dataKeyInvalid", "A bounded non-empty stable record key is required.");
        }

        if ((!needsAsset && query.Asset is not null)
            || (!needsKey && query.Key is not null))
        {
            return Problem("dataRequestInvalid", "The review-data request has unexpected operands.");
        }

        return null;
    }

    private static bool TryResolveAsset(
        string input,
        IReadOnlyList<string> discovered,
        out string? assetName,
        out ReviewDataProblem? problem)
    {
        string normalizedInput = ReviewFixtureKindResolver.Normalize(input);
        string[] normalizedMatches = discovered
            .Where(candidate => string.Equals(
                ReviewFixtureKindResolver.Normalize(candidate),
                normalizedInput,
                StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        if (normalizedMatches.Length > 1)
        {
            assetName = null;
            problem = Problem(
                "dataAssetAmbiguous",
                "The asset token collides after case/separator normalization; the query cannot proceed safely.");
            return false;
        }

        string[] exactMatches = discovered
            .Where(candidate => string.Equals(
                candidate,
                input,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (exactMatches.Length == 1)
        {
            assetName = exactMatches[0];
            problem = null;
            return true;
        }

        if (normalizedMatches.Length == 1)
        {
            assetName = normalizedMatches[0];
            problem = null;
            return true;
        }

        assetName = null;
        problem = IsDataAssetRequest(input)
            ? Problem(
                "dataAssetUnavailableInGameVersion",
                "The requested canonical Data asset is not shipped by this installed game version.")
            : Problem(
                "dataAssetUnknown",
                "The requested name is not a canonical Data asset.");
        return false;
    }

    private static bool TryResolveKey(
        string input,
        ReviewDataAssetSnapshot snapshot,
        out ReviewDataRecord? selected,
        out ReviewDataProblem? problem)
    {
        ReviewDataRecord[] exact = snapshot.Records
            .Where(record => string.Equals(record.Key, input, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exact.Length == 1)
        {
            selected = exact[0];
            problem = null;
            return true;
        }

        bool normalizable = snapshot.Report.KeyKind is "string" or "singleton";
        if (normalizable)
        {
            string normalizedInput = ReviewFixtureKindResolver.Normalize(input);
            ReviewDataRecord[] normalized = snapshot.Records
                .Where(record => string.Equals(
                    ReviewFixtureKindResolver.Normalize(record.Key),
                    normalizedInput,
                    StringComparison.Ordinal))
                .Take(3)
                .ToArray();
            if (normalized.Length == 1)
            {
                selected = normalized[0];
                problem = null;
                return true;
            }

            if (normalized.Length > 1)
            {
                selected = null;
                problem = Problem(
                    "dataKeyAmbiguous",
                    "The record key collides after case/separator normalization; use an exact canonical key.");
                return false;
            }
        }

        selected = null;
        problem = Problem(
            "dataKeyUnknown",
            "The canonical Data asset has no record with that stable internal key.");
        return false;
    }

    private static bool TryLoadSnapshot(
        IReviewDataSource source,
        string assetName,
        out ReviewDataAssetSnapshot? snapshot,
        out bool loadFailed)
    {
        object value;
        try
        {
            value = source.LoadAsset(assetName);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            loadFailed = true;
            snapshot = Unsupported(
                assetName,
                dataType: null,
                shape: null,
                keyKind: null,
                recordCount: null,
                "dataAssetLoadFailed");
            return false;
        }

        loadFailed = false;
        snapshot = Classify(assetName, value);
        return snapshot.Report.Supported;
    }

    private static ReviewDataAssetSnapshot Classify(string assetName, object value)
    {
        string dataType = FriendlyName(value.GetType());
        if (value is IDictionary dictionary)
        {
            if (dictionary.Count > MaximumRecordsPerAsset)
            {
                return Unsupported(
                    assetName,
                    dataType,
                    "dictionary",
                    null,
                    dictionary.Count,
                    "dataAssetRecordCountTooLarge");
            }

            Type? declaredKeyType = DictionaryKeyType(value.GetType());
            string? keyKind = declaredKeyType == typeof(string)
                ? "string"
                : declaredKeyType == typeof(int)
                    ? "integer"
                    : null;
            var records = new List<ReviewDataRecord>(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                string? key = entry.Key switch
                {
                    string text => text,
                    int number => number.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                string observedKind = entry.Key switch
                {
                    string => "string",
                    int => "integer",
                    _ => string.Empty,
                };
                keyKind ??= observedKind.Length == 0 ? null : observedKind;
                if (key is null
                    || keyKind is null
                    || !string.Equals(keyKind, observedKind, StringComparison.Ordinal)
                    || !IsStableKey(key))
                {
                    return Unsupported(
                        assetName,
                        dataType,
                        "dictionary",
                        keyKind,
                        dictionary.Count,
                        "dataDictionaryKeyUnsupported");
                }

                records.Add(new ReviewDataRecord(key, entry.Value));
            }

            if (keyKind is null)
            {
                return Unsupported(
                    assetName,
                    dataType,
                    "dictionary",
                    null,
                    dictionary.Count,
                    "dataDictionaryKeyUnsupported");
            }

            records.Sort(keyKind == "integer"
                ? CompareIntegerKeys
                : CompareOrdinalKeys);
            return ValidateSerialization(
                assetName,
                dataType,
                "dictionary",
                keyKind,
                records);
        }

        if (value is IList list)
        {
            if (list.Count > MaximumRecordsPerAsset)
            {
                return Unsupported(
                    assetName,
                    dataType,
                    "list",
                    "index",
                    list.Count,
                    "dataAssetRecordCountTooLarge");
            }

            ReviewDataRecord[] records = Enumerable
                .Range(0, list.Count)
                .Select(index => new ReviewDataRecord(
                    index.ToString(CultureInfo.InvariantCulture),
                    list[index]))
                .ToArray();
            return ValidateSerialization(
                assetName,
                dataType,
                "list",
                "index",
                records);
        }

        return ValidateSerialization(
            assetName,
            dataType,
            "singleton",
            "singleton",
            [new ReviewDataRecord(ReviewDataContract.SingletonKey, value)]);
    }

    private static ReviewDataAssetSnapshot ValidateSerialization(
        string assetName,
        string dataType,
        string shape,
        string keyKind,
        IReadOnlyList<ReviewDataRecord> records)
    {
        foreach (ReviewDataRecord record in records)
        {
            if (!ReviewDataJson.TrySerialize(
                    record.Value,
                    out _,
                    out _))
            {
                return Unsupported(
                    assetName,
                    dataType,
                    shape,
                    keyKind,
                    records.Count,
                    "dataRecordNotSafelySerializable");
            }
        }

        return new ReviewDataAssetSnapshot(
            new ReviewDataAssetReport(
                assetName,
                dataType,
                shape,
                keyKind,
                records.Count,
                true,
                null),
            records);
    }

    private static ReviewDataAssetSnapshot Unsupported(
        string assetName,
        string? dataType,
        string? shape,
        string? keyKind,
        int? recordCount,
        string problemCode) =>
        new(
            new ReviewDataAssetReport(
                assetName,
                dataType,
                shape,
                keyKind,
                recordCount,
                false,
                problemCode),
            []);

    private static Type? DictionaryKeyType(Type type) =>
        type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            ?.GetGenericArguments()[0];

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int marker = name.IndexOf((char)96);
        if (marker >= 0)
        {
            name = name[..marker];
        }

        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static bool IsStableKey(string key) =>
        key.Length is > 0 and <= ReviewDataContract.MaximumKeyLength
        && !key.Any(char.IsControl);

    private static bool IsCanonicalAssetName(string assetName)
    {
        if (!IsDataAssetRequest(assetName)
            || assetName.Length > ReviewDataContract.MaximumAssetLength
            || assetName.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(
                assetName,
                @"\.[a-z]{2}-[A-Z]{2}$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return true;
    }

    private static bool IsDataAssetRequest(string input)
    {
        string normalized = input.Replace('\\', '/').Trim();
        return normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith('/')
            && normalized.Split('/').All(segment =>
                segment.Length > 0
                && segment is not "." and not ".."
                && !segment.Any(char.IsControl));
    }

    private static ReviewDataPage Page(
        ReviewDataQuery query,
        int returned,
        int total)
    {
        int consumed = Math.Min(total, checked(query.Offset + returned));
        return new ReviewDataPage(
            query.Offset,
            query.Limit,
            returned,
            total,
            consumed < total ? consumed : null);
    }

    private static int CompareOrdinalKeys(ReviewDataRecord left, ReviewDataRecord right) =>
        StringComparer.Ordinal.Compare(left.Key, right.Key);

    private static int CompareIntegerKeys(ReviewDataRecord left, ReviewDataRecord right) =>
        int.Parse(left.Key, CultureInfo.InvariantCulture).CompareTo(
            int.Parse(right.Key, CultureInfo.InvariantCulture));

    private static ReviewDataProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException;
}

internal static class ReviewDataJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        IncludeFields = true,
        MaxDepth = 64,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static bool TrySerialize(
        object? value,
        out JsonElement element,
        out string? error)
    {
        try
        {
            JsonElement initial = JsonSerializer.SerializeToElement(
                value,
                value?.GetType() ?? typeof(object),
                SerializerOptions);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(writer, initial);
            }

            if (stream.Length > ReviewDataContract.MaximumRecordBytes)
            {
                element = default;
                error = $"record exceeds {ReviewDataContract.MaximumRecordBytes} UTF-8 bytes";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
            element = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or JsonException
            or NotSupportedException)
        {
            element = default;
            error = exception.GetType().Name;
            return false;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(
                    $"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
internal sealed class StardewReviewDataSource : IReviewDataSource
{
    private static readonly Regex LocaleSuffix = new(
        @"\.[a-z]{2}-[A-Z]{2}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IModHelper _helper;
    private readonly string _contentRoot;
    private readonly string _dataRoot;

    public StardewReviewDataSource(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        string gameRoot = Path.GetDirectoryName(typeof(Game1).Assembly.Location)
            ?? throw new InvalidOperationException("The game assembly has no directory.");
        _contentRoot = Path.Combine(gameRoot, "Content");
        _dataRoot = Path.Combine(_contentRoot, "Data");
    }

    public string GameVersion => Game1.version.ToString();

    public string GameFileVersion =>
        FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
        ?? string.Empty;

    public IReadOnlyList<string> DiscoverCanonicalAssetNames()
    {
        var names = new List<string>();
        var pending = new Stack<string>();
        RefuseReparsePoint(_dataRoot);
        pending.Push(_dataRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "The installed Data asset tree contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (!string.Equals(
                        Path.GetExtension(entry),
                        ".xnb",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = Path
                    .GetRelativePath(_contentRoot, entry)
                    .Replace('\\', '/');
                string assetName = relative[..^Path.GetExtension(relative).Length];
                if (!LocaleSuffix.IsMatch(assetName))
                {
                    names.Add(assetName);
                }
            }
        }

        return names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public object LoadAsset(string assetName) =>
        _helper.GameContent.Load<object>(assetName);

    private static void RefuseReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The installed Data asset root is not a regular directory.");
        }
    }
}

internal static class ReviewDataCommand
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Handle(
        string[] arguments,
        IReviewDataSource source,
        string runtimePath,
        IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-data runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(monitor);

        string? requestId = arguments.Length > 1 ? arguments[1] : null;
        if (!ReviewDataContract.IsRequestId(requestId))
        {
            monitor.Log(
                "SDVKit review-data rejected an invalid request ID.",
                LogLevel.Error);
            return;
        }

        ReviewDataReport report;
        bool singleReview = string.Equals(
                Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW"),
                "1",
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        if (!singleReview)
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewDataOperation.Failure(
                operation,
                source,
                new ReviewDataProblem(
                    "dataReviewTopologyUnsupported",
                    "Review-data queries require an active owned single project review."));
        }
        else if (!TryParse(
                arguments,
                out ReviewDataQuery? query,
                out ReviewDataProblem? problem))
        {
            string operation = arguments.Length > 2
                ? arguments[2]
                : "unknown";
            report = ReviewDataOperation.Failure(operation, source, problem!);
        }
        else
        {
            try
            {
                report = ReviewDataOperation.Execute(query!, source);
            }
            catch (Exception exception)
            {
                report = ReviewDataOperation.Failure(
                    query!.Operation,
                    source,
                    new ReviewDataProblem(
                        "dataQueryFailed",
                        $"The review-data query failed closed ({exception.GetType().Name})."));
            }
        }

        var envelope = new ReviewDataResponseEnvelope(
            ReviewDataContract.SchemaVersion,
            requestId!,
            report);
        try
        {
            WriteResponse(runtimePath, envelope);
            monitor.Log(
                $"SDVKit review-data completed '{report.Operation}' with state '{report.State}'.",
                report.Problems.Count == 0 ? LogLevel.Info : LogLevel.Error);
        }
        catch (Exception exception)
        {
            monitor.Log(
                $"SDVKit review-data could not publish its bounded response ({exception.GetType().Name}).",
                LogLevel.Error);
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ReviewDataQuery? query,
        out ReviewDataProblem? problem)
    {
        query = null;
        problem = null;
        if (arguments.Count < 3
            || !string.Equals(arguments[0], "data", StringComparison.Ordinal)
            || !ReviewDataContract.IsRequestId(arguments[1]))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The bounded review-data transport request is invalid.");
            return false;
        }

        string operation = arguments[2];
        int expectedCount = operation switch
        {
            ReviewDataContract.AssetsOperation => 5,
            ReviewDataContract.KeysOperation => 6,
            ReviewDataContract.GetOperation => 7,
            _ => 0,
        };
        if (expectedCount == 0
            || arguments.Count != expectedCount
            || !int.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int offset)
            || !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The bounded review-data transport request is invalid.");
            return false;
        }

        string? asset = null;
        string? key = null;
        bool needsAsset = operation is ReviewDataContract.KeysOperation
            or ReviewDataContract.GetOperation;
        if (needsAsset
            && !ReviewDataContract.TryDecode(
                arguments[5],
                ReviewDataContract.MaximumAssetLength,
                out asset))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The encoded review-data asset name is invalid.");
            return false;
        }

        if (operation == ReviewDataContract.GetOperation
            && !ReviewDataContract.TryDecode(
                arguments[6],
                ReviewDataContract.MaximumKeyLength,
                out key))
        {
            problem = new ReviewDataProblem(
                "dataTransportInvalid",
                "The encoded review-data record key is invalid.");
            return false;
        }

        query = new ReviewDataQuery(operation, asset, key, offset, limit);
        return true;
    }

    private static void WriteResponse(
        string runtimePath,
        ReviewDataResponseEnvelope envelope)
    {
        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }

        string responsePath = ReviewDataContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (File.Exists(responsePath) || File.Exists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-data response target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewDataContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-data response exceeds its maximum size.");
        }

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, responsePath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
#endif

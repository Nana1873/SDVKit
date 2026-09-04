using System.Globalization;
using System.Text;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
#if SDVKIT_GAME_AVAILABLE
using System.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
#endif

namespace SdvKit.AlwaysOn;

internal enum ReviewModAssetAdapterKind
{
    StringDictionary,
    IntegerDictionary,
    IntegerKeyStringDictionary,
    IntegerKeyIntegerDictionary,
    StringList,
    StringSingleton,
}

internal sealed record ReviewModAssetObservation(
    string AssetName,
    string? NamespaceOwnerId,
    string NamespaceOwnerStatus,
    Type DataType,
    string DataTypeName,
    string Lifecycle,
    int Generation,
    int RequestCount,
    int ReadyCount,
    bool Available,
    bool NameCollision,
    bool TypeCollision);

internal sealed record ReviewModAssetInventorySnapshot(
    DateTimeOffset ObservationStartedAtUtc,
    int Observed,
    int Dropped,
    IReadOnlyList<ReviewModAssetObservation> Assets);

internal sealed record ReviewModAssetLoadResult(
    bool Succeeded,
    object? Value,
    string? ProblemCode);

internal static class ReviewModAssetRegistryReader
{
    public static IReadOnlyList<string> Read(Func<IEnumerable<string>> readModIds)
    {
        ArgumentNullException.ThrowIfNull(readModIds);
        try
        {
            return readModIds().ToArray();
        }
        catch (Exception exception) when (!ReviewTextureException.IsFatal(exception))
        {
            return [];
        }
    }
}

internal interface IReviewModAssetSource
{
    string GameVersion { get; }

    string GameFileVersion { get; }

    ReviewModAssetInventorySnapshot GetInventory();

    ReviewModAssetLoadResult Load(ReviewModAssetObservation asset);
}

internal sealed class ReviewModAssetQueryObservationGuard
{
    [ThreadStatic]
    private static Identity? Active;

    public IDisposable Enter(string assetName, Type dataType)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new ArgumentException(
                "The review-mod-assets query asset name is required.",
                nameof(assetName));
        }
        ArgumentNullException.ThrowIfNull(dataType);

        Identity? previous = Active;
        var current = new Identity(this, assetName, dataType);
        Active = current;
        return new Scope(current, previous);
    }

    public bool SuppressesRequested(string assetName, Type dataType)
    {
        Identity? active = Active;
        return active is not null
            && ReferenceEquals(active.Owner, this)
            && active.DataType == dataType
            && ReviewModAssetContract.AssetIdentityEquals(
                active.AssetName,
                assetName);
    }

    public bool SuppressesReady(string assetName)
    {
        Identity? active = Active;
        return active is not null
            && ReferenceEquals(active.Owner, this)
            && ReviewModAssetContract.AssetIdentityEquals(
                active.AssetName,
                assetName);
    }

    private sealed record Identity(
        ReviewModAssetQueryObservationGuard Owner,
        string AssetName,
        Type DataType);

    private sealed class Scope(
        Identity current,
        Identity? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            if (!ReferenceEquals(Active, current))
            {
                throw new InvalidOperationException(
                    "Review-mod-assets query observation scopes must be disposed in order.");
            }

            Active = previous;
            _disposed = true;
        }
    }
}

internal sealed class ReviewModAssetCatalog
{
    private readonly object _sync = new();
    private readonly string[] _loadedModIds;
    private readonly List<Entry> _entries = [];
    private int _dropped;

    public ReviewModAssetCatalog(
        IEnumerable<string> loadedModIds,
        DateTimeOffset? observationStartedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(loadedModIds);
        _loadedModIds = loadedModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        ObservationStartedAtUtc = observationStartedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public DateTimeOffset ObservationStartedAtUtc { get; }

    public void ObserveRequested(string assetName, Type dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        if (!TryParseNamespace(assetName, out string canonicalName, out string ownerSegment))
        {
            if (LooksLikeConventionalNamespace(assetName))
            {
                lock (_sync)
                {
                    IncrementDropped();
                }
            }

            return;
        }

        lock (_sync)
        {
            Entry[] matchingName = MatchingName(canonicalName).ToArray();
            Entry? existing = matchingName.FirstOrDefault(entry =>
                ReviewModAssetContract.AssetIdentityEquals(
                    entry.AssetName,
                    canonicalName)
                && entry.DataType == dataType);
            if (existing is not null)
            {
                IncrementRequestCount(existing);
                existing.LastRequestedGeneration = existing.Generation;
                // SMAPI may raise AssetRequested for an existence check which never loads.
                if (!existing.Available
                    || !string.Equals(existing.Lifecycle, "ready", StringComparison.Ordinal))
                {
                    existing.Lifecycle = "requested";
                    existing.Available = false;
                }

                return;
            }

            if (_entries.Count >= ReviewModAssetContract.MaximumObservedAssets)
            {
                IncrementDropped();
                return;
            }

            string[] ownerMatches = _loadedModIds
                .Where(id => string.Equals(id, ownerSegment, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            int generation = matchingName.Length == 0
                ? 0
                : matchingName.Max(entry => entry.Generation);
            var added = new Entry(
                canonicalName,
                ownerMatches.Length == 1 ? ownerMatches[0] : null,
                ownerMatches.Length switch
                {
                    1 => "resolved",
                    > 1 => "ambiguous",
                    _ => "unknown",
                },
                dataType,
                FriendlyTypeName(dataType),
                generation);
            _entries.Add(added);

            if (matchingName.Any(entry => entry.DataType != dataType))
            {
                added.Available = false;
                foreach (Entry entry in matchingName)
                {
                    entry.Available = false;
                }
            }
        }
    }

    public void ObserveReady(string assetName)
    {
        if (!TryCanonicalizeObservedName(assetName, out string canonicalName))
        {
            return;
        }

        lock (_sync)
        {
            Entry[] matchingName = MatchingName(canonicalName).ToArray();
            Entry[] candidates = matchingName
                .Where(entry =>
                    entry.LastRequestedGeneration == entry.Generation
                    && string.Equals(
                        entry.Lifecycle,
                        "requested",
                        StringComparison.Ordinal))
                .ToArray();
            // SMAPI's public AssetReady event has no requested-type field.
            if (matchingName.Select(entry => entry.DataType).Distinct().Take(2).Count() != 1
                || candidates.Length != 1)
            {
                return;
            }

            MarkReady(candidates[0]);
        }
    }

    public void ObserveInvalidated(IEnumerable<string> assetNames)
    {
        ArgumentNullException.ThrowIfNull(assetNames);
        lock (_sync)
        {
            foreach (string assetName in assetNames)
            {
                if (!TryCanonicalizeObservedName(assetName, out string canonicalName))
                {
                    continue;
                }

                Entry[] matchingName = MatchingName(canonicalName).ToArray();
                if (matchingName.Length == 0)
                {
                    continue;
                }

                int generation = matchingName.Max(entry => entry.Generation);
                if (generation < int.MaxValue)
                {
                    generation++;
                }
                foreach (Entry entry in matchingName)
                {
                    entry.Generation = generation;
                    entry.Lifecycle = "invalidated";
                    entry.Available = false;
                }
            }
        }
    }

    public void MarkVerifiedReady(string assetName, Type dataType)
    {
        lock (_sync)
        {
            Entry[] matchingName = MatchingName(assetName).ToArray();
            Entry? entry = matchingName.FirstOrDefault(candidate =>
                ReviewModAssetContract.AssetIdentityEquals(
                    candidate.AssetName,
                    assetName)
                && candidate.DataType == dataType);
            if (entry is null
                || matchingName.Length != 1)
            {
                return;
            }

            MarkReady(entry);
        }
    }

    public void MarkUnavailable(string assetName, Type dataType)
    {
        lock (_sync)
        {
            Entry? entry = FindExact(assetName, dataType);
            if (entry is null)
            {
                return;
            }

            entry.Lifecycle = "unavailable";
            entry.Available = false;
        }
    }

    public ReviewModAssetInventorySnapshot Snapshot()
    {
        lock (_sync)
        {
            IReadOnlyDictionary<string, int> nameCollisions = _entries
                .GroupBy(
                    entry => ReviewModAssetContract.StableAssetIdentityKey(entry.AssetName),
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => entry.AssetName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    StringComparer.Ordinal);
            IReadOnlyDictionary<string, int> typeCollisions = _entries
                .GroupBy(entry => entry.AssetName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => entry.DataType).Distinct().Count(),
                    StringComparer.OrdinalIgnoreCase);
            ReviewModAssetObservation[] assets = _entries
                .OrderBy(entry => entry.AssetName, StringComparer.Ordinal)
                .ThenBy(entry => entry.DataTypeName, StringComparer.Ordinal)
                .Select(entry => new ReviewModAssetObservation(
                    entry.AssetName,
                    entry.NamespaceOwnerId,
                    entry.NamespaceOwnerStatus,
                    entry.DataType,
                    entry.DataTypeName,
                    entry.Lifecycle,
                    entry.Generation,
                    entry.RequestCount,
                    entry.ReadyCount,
                    entry.Available,
                    nameCollisions[ReviewModAssetContract.StableAssetIdentityKey(entry.AssetName)] > 1,
                    typeCollisions[entry.AssetName] > 1))
                .ToArray();
            int observed = _dropped > int.MaxValue - assets.Length
                ? int.MaxValue
                : assets.Length + _dropped;
            return new ReviewModAssetInventorySnapshot(
                ObservationStartedAtUtc,
                observed,
                _dropped,
                assets);
        }
    }

    private IEnumerable<Entry> MatchingName(string canonicalName) =>
        _entries.Where(entry => ReviewModAssetContract.AssetIdentityEquals(
            entry.AssetName,
            canonicalName));

    private Entry? FindExact(string assetName, Type dataType) =>
        _entries.FirstOrDefault(entry =>
            ReviewModAssetContract.AssetIdentityEquals(
                entry.AssetName,
                assetName)
            && entry.DataType == dataType);

    private static bool TryParseNamespace(
        string? assetName,
        out string canonicalName,
        out string ownerSegment)
    {
        ownerSegment = string.Empty;
        if (!TryCanonicalizeObservedName(assetName, out canonicalName))
        {
            return false;
        }

        string[] segments = canonicalName.Split('/');
        if (segments.Length < 3
            || !string.Equals(segments[0], "Mods", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ownerSegment = segments[1];
        return true;
    }

    private static bool TryCanonicalizeObservedName(
        string? assetName,
        out string canonicalName)
    {
        canonicalName = string.Empty;
        if (assetName is null)
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = assetName.AsSpan().Trim();
        if (trimmed.Length is <= 0 or > ReviewModAssetContract.MaximumAssetLength)
        {
            return false;
        }

        if (!ReviewTransportText.IsWellFormedUtf16(assetName)
            || !trimmed.SequenceEqual(assetName.AsSpan()))
        {
            return false;
        }

        string[] segments = assetName.Replace('\\', '/').Split('/');
        if (segments.Length < 3
            || !string.Equals(segments[0], "Mods", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment =>
                segment.Length > 0
                    ? segment is "." or ".."
                        || segment.Any(char.IsControl)
                        || StableIdentityNormalizer.Normalize(segment).Length == 0
                    : true))
        {
            return false;
        }

        segments[0] = "Mods";
        canonicalName = string.Join('/', segments);
        return true;
    }

    private static bool LooksLikeConventionalNamespace(string? assetName)
    {
        if (assetName is null)
        {
            return false;
        }

        ReadOnlySpan<char> value = assetName.AsSpan().Trim();
        int firstSeparator = value.IndexOfAny('/', '\\');
        if (firstSeparator != 4
            || !value[..firstSeparator].Equals("Mods", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> remainder = value[(firstSeparator + 1)..];
        int secondSeparator = remainder.IndexOfAny('/', '\\');
        return secondSeparator > 0 && secondSeparator < remainder.Length - 1;
    }

    private static void IncrementRequestCount(Entry entry)
    {
        if (entry.RequestCount < int.MaxValue)
        {
            entry.RequestCount++;
        }
    }

    private void IncrementDropped()
    {
        if (_dropped < int.MaxValue)
        {
            _dropped++;
        }
    }

    private static void MarkReady(Entry entry)
    {
        if (entry.LastReadyGeneration != entry.Generation)
        {
            if (entry.ReadyCount < int.MaxValue)
            {
                entry.ReadyCount++;
            }

            entry.LastReadyGeneration = entry.Generation;
        }

        entry.Lifecycle = "ready";
        entry.Available = true;
    }

    private static string FriendlyTypeName(Type type)
    {
        if (ReviewModAssetAdapterRegistry.TryGet(type, out _, out string? knownName, out _))
        {
            return knownName!;
        }

        return type.FullName ?? type.Name;
    }

    private sealed class Entry(
        string assetName,
        string? namespaceOwnerId,
        string namespaceOwnerStatus,
        Type dataType,
        string dataTypeName,
        int generation)
    {
        public string AssetName { get; } = assetName;

        public string? NamespaceOwnerId { get; } = namespaceOwnerId;

        public string NamespaceOwnerStatus { get; } = namespaceOwnerStatus;

        public Type DataType { get; } = dataType;

        public string DataTypeName { get; } = dataTypeName;

        public string Lifecycle { get; set; } = "requested";

        public int Generation { get; set; } = generation;

        public int LastRequestedGeneration { get; set; } = generation;

        public int LastReadyGeneration { get; set; } = -1;

        public int RequestCount { get; set; } = 1;

        public int ReadyCount { get; set; }

        public bool Available { get; set; }
    }
}

internal sealed record ReviewModAssetRecord(string Key, JsonElement Value);

internal static class ReviewModAssetAdapterRegistry
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryGet(
        Type type,
        out ReviewModAssetAdapterKind kind,
        out string? dataTypeName,
        out string? shape)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(Dictionary<string, string>))
        {
            kind = ReviewModAssetAdapterKind.StringDictionary;
            dataTypeName = "System.Collections.Generic.Dictionary<System.String,System.String>";
            shape = "stringDictionary";
            return true;
        }
        if (type == typeof(Dictionary<string, int>))
        {
            kind = ReviewModAssetAdapterKind.IntegerDictionary;
            dataTypeName = "System.Collections.Generic.Dictionary<System.String,System.Int32>";
            shape = "integerDictionary";
            return true;
        }
        if (type == typeof(Dictionary<int, string>))
        {
            kind = ReviewModAssetAdapterKind.IntegerKeyStringDictionary;
            dataTypeName = "System.Collections.Generic.Dictionary<System.Int32,System.String>";
            shape = "integerKeyStringDictionary";
            return true;
        }
        if (type == typeof(Dictionary<int, int>))
        {
            kind = ReviewModAssetAdapterKind.IntegerKeyIntegerDictionary;
            dataTypeName = "System.Collections.Generic.Dictionary<System.Int32,System.Int32>";
            shape = "integerKeyIntegerDictionary";
            return true;
        }
        if (type == typeof(List<string>))
        {
            kind = ReviewModAssetAdapterKind.StringList;
            dataTypeName = "System.Collections.Generic.List<System.String>";
            shape = "stringList";
            return true;
        }
        if (type == typeof(string))
        {
            kind = ReviewModAssetAdapterKind.StringSingleton;
            dataTypeName = "System.String";
            shape = "stringSingleton";
            return true;
        }

        kind = default;
        dataTypeName = null;
        shape = null;
        return false;
    }

    public static bool TryAdapt(
        ReviewModAssetAdapterKind kind,
        object value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return kind switch
            {
                ReviewModAssetAdapterKind.StringDictionary =>
                    AdaptStringDictionary((Dictionary<string, string>)value, out records, out problemCode),
                ReviewModAssetAdapterKind.IntegerDictionary =>
                    AdaptIntegerDictionary((Dictionary<string, int>)value, out records, out problemCode),
                ReviewModAssetAdapterKind.IntegerKeyStringDictionary =>
                    AdaptIntegerKeyStringDictionary((Dictionary<int, string>)value, out records, out problemCode),
                ReviewModAssetAdapterKind.IntegerKeyIntegerDictionary =>
                    AdaptIntegerKeyIntegerDictionary((Dictionary<int, int>)value, out records, out problemCode),
                ReviewModAssetAdapterKind.StringList =>
                    AdaptStringList((List<string>)value, out records, out problemCode),
                ReviewModAssetAdapterKind.StringSingleton =>
                    AdaptStringSingleton((string)value, out records, out problemCode),
                _ => Failed(out records, out problemCode, "modAssetAdapterUnavailable"),
            };
        }
        catch (Exception exception) when (exception is ArgumentException
            or EncoderFallbackException
            or InvalidOperationException
            or JsonException
            or NotSupportedException)
        {
            records = [];
            problemCode = "modAssetRecordNotSafelySerializable";
            return false;
        }
    }

    private static bool AdaptStringDictionary(
        Dictionary<string, string> value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!ValidateCount(value.Count, out records, out problemCode))
        {
            return false;
        }

        var adapted = new List<ReviewModAssetRecord>(value.Count);
        var payloadBytes = 0;
        foreach ((string key, string item) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsStableKey(key) || !IsBoundedString(item))
            {
                return Failed(out records, out problemCode, "modAssetRecordNotSafelySerializable");
            }
            if (!TryAddRecord(
                    adapted,
                    key,
                    JsonSerializer.SerializeToElement(item),
                    ref payloadBytes,
                    out records,
                    out problemCode))
            {
                return false;
            }
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool AdaptIntegerDictionary(
        Dictionary<string, int> value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!ValidateCount(value.Count, out records, out problemCode))
        {
            return false;
        }

        var adapted = new List<ReviewModAssetRecord>(value.Count);
        var payloadBytes = 0;
        foreach ((string key, int item) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsStableKey(key))
            {
                return Failed(out records, out problemCode, "modAssetRecordNotSafelySerializable");
            }
            if (!TryAddRecord(
                    adapted,
                    key,
                    JsonSerializer.SerializeToElement(item),
                    ref payloadBytes,
                    out records,
                    out problemCode))
            {
                return false;
            }
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool AdaptIntegerKeyStringDictionary(
        Dictionary<int, string> value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!ValidateCount(value.Count, out records, out problemCode))
        {
            return false;
        }

        var adapted = new List<ReviewModAssetRecord>(value.Count);
        var payloadBytes = 0;
        foreach ((string key, string item) in value
            .Select(pair => (
                Key: pair.Key.ToString(CultureInfo.InvariantCulture),
                Item: pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsBoundedString(item))
            {
                return Failed(out records, out problemCode, "modAssetRecordNotSafelySerializable");
            }
            if (!TryAddRecord(
                    adapted,
                    key,
                    JsonSerializer.SerializeToElement(item),
                    ref payloadBytes,
                    out records,
                    out problemCode))
            {
                return false;
            }
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool AdaptIntegerKeyIntegerDictionary(
        Dictionary<int, int> value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!ValidateCount(value.Count, out records, out problemCode))
        {
            return false;
        }

        var adapted = new List<ReviewModAssetRecord>(value.Count);
        var payloadBytes = 0;
        foreach ((string key, int item) in value
            .Select(pair => (
                Key: pair.Key.ToString(CultureInfo.InvariantCulture),
                Item: pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!TryAddRecord(
                    adapted,
                    key,
                    JsonSerializer.SerializeToElement(item),
                    ref payloadBytes,
                    out records,
                    out problemCode))
            {
                return false;
            }
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool AdaptStringList(
        List<string> value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!ValidateCount(value.Count, out records, out problemCode))
        {
            return false;
        }

        var adapted = new List<ReviewModAssetRecord>(value.Count);
        var payloadBytes = 0;
        for (var index = 0; index < value.Count; index++)
        {
            if (!IsBoundedString(value[index]))
            {
                return Failed(out records, out problemCode, "modAssetRecordNotSafelySerializable");
            }
            if (!TryAddRecord(
                    adapted,
                    index.ToString(CultureInfo.InvariantCulture),
                    JsonSerializer.SerializeToElement(value[index]),
                    ref payloadBytes,
                    out records,
                    out problemCode))
            {
                return false;
            }
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool AdaptStringSingleton(
        string value,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (!IsBoundedString(value))
        {
            return Failed(out records, out problemCode, "modAssetRecordNotSafelySerializable");
        }

        var adapted = new List<ReviewModAssetRecord>(1);
        var payloadBytes = 0;
        if (!TryAddRecord(
                adapted,
                ReviewModAssetContract.SingletonKey,
                JsonSerializer.SerializeToElement(value),
                ref payloadBytes,
                out records,
                out problemCode))
        {
            return false;
        }

        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool TryAddRecord(
        List<ReviewModAssetRecord> adapted,
        string key,
        JsonElement value,
        ref int payloadBytes,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        int recordBytes = checked(
            JsonSerializer.SerializeToUtf8Bytes(key).Length
            + StrictUtf8.GetByteCount(value.GetRawText())
            + 32);
        if (recordBytes > ReviewModAssetContract.MaximumAdaptedPayloadBytes - payloadBytes)
        {
            return Failed(out records, out problemCode, "modAssetAdaptedPayloadTooLarge");
        }

        payloadBytes += recordBytes;
        adapted.Add(new ReviewModAssetRecord(key, value));
        records = adapted;
        problemCode = null;
        return true;
    }

    private static bool ValidateCount(
        int count,
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode)
    {
        if (count <= ReviewModAssetContract.MaximumRecordsPerAsset)
        {
            records = [];
            problemCode = null;
            return true;
        }

        return Failed(out records, out problemCode, "modAssetRecordCountTooLarge");
    }

    private static bool IsStableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key!.Length <= ReviewModAssetContract.MaximumKeyLength
        && !key.Any(char.IsControl)
        && ReviewTransportText.IsWellFormedUtf16(key);

    private static bool IsBoundedString(string? value)
    {
        if (value is null
            || value.Length > ReviewModAssetContract.MaximumStringValueLength
            || !ReviewTransportText.IsWellFormedUtf16(value))
        {
            return false;
        }

        _ = StrictUtf8.GetByteCount(value);
        return true;
    }

    private static bool Failed(
        out IReadOnlyList<ReviewModAssetRecord> records,
        out string? problemCode,
        string code)
    {
        records = [];
        problemCode = code;
        return false;
    }
}

internal static class ReviewModAssetOperation
{
    public static ReviewModAssetReport Execute(
        ReviewModAssetQuery query,
        IReviewModAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(source);

        ReviewModAssetProblem? validation = Validate(query);
        if (validation is not null)
        {
            return Failure(query.Operation, source, validation);
        }

        ReviewModAssetInventorySnapshot inventory;
        try
        {
            inventory = source.GetInventory();
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            return Failure(
                query.Operation,
                source,
                Problem(
                    "modAssetInventoryFailed",
                    $"The observed mod-owned asset catalogue could not be read ({exception.GetType().Name})."));
        }

        return query.Operation switch
        {
            ReviewModAssetContract.AssetsOperation => ListAssets(query, source, inventory),
            ReviewModAssetContract.KeysOperation => ListKeys(query, source, inventory),
            ReviewModAssetContract.GetOperation => GetRecord(query, source, inventory),
            _ => Failure(
                query.Operation,
                source,
                Problem("modAssetOperationUnknown", "The review-mod-assets operation is unknown.")),
        };
    }

    public static ReviewModAssetReport Failure(
        string operation,
        IReviewModAssetSource source,
        ReviewModAssetProblem problem) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            ReviewModAssetContract.CoverageScope,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [problem]);

    private static ReviewModAssetReport ListAssets(
        ReviewModAssetQuery query,
        IReviewModAssetSource source,
        ReviewModAssetInventorySnapshot inventory)
    {
        ReviewModAssetAssetReport[] all = inventory.Assets
            .Select(asset => ToReport(asset))
            .ToArray();
        ReviewModAssetAssetReport[] page = all
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        var coverage = new ReviewModAssetCoverageReport(
            ReviewModAssetContract.CoverageScope,
            inventory.ObservationStartedAtUtc,
            inventory.Observed,
            all.Length,
            all.Count(asset => asset.AdapterSupported),
            all.Count(asset => !asset.AdapterSupported),
            all.Count(asset => string.Equals(asset.Lifecycle, "ready", StringComparison.Ordinal)),
            all.Count(asset => string.Equals(asset.Lifecycle, "invalidated", StringComparison.Ordinal)),
            all.Count(asset => string.Equals(asset.Lifecycle, "unavailable", StringComparison.Ordinal)),
            all.Count(asset => asset.NameCollision),
            all.Count(asset => asset.TypeCollision),
            inventory.Dropped);
        return new ReviewModAssetReport(
            ReviewModAssetContract.SchemaVersion,
            coverage.Complete ? "ready" : "blocked",
            query.Operation,
            source.GameVersion,
            source.GameFileVersion,
            ReviewModAssetContract.CoverageScope,
            null,
            null,
            page,
            null,
            Page(query, page.Length, all.Length),
            coverage,
            null,
            coverage.Complete
                ? []
                : [Problem(
                    "modAssetCoverageIncomplete",
                    "The bounded observed-request catalogue dropped one or more mod-owned assets.")]);
    }

    private static ReviewModAssetReport ListKeys(
        ReviewModAssetQuery query,
        IReviewModAssetSource source,
        ReviewModAssetInventorySnapshot inventory)
    {
        if (!TryLoadAdapted(
                query,
                source,
                inventory,
                out ReviewModAssetObservation? asset,
                out IReadOnlyList<ReviewModAssetRecord>? records,
                out ReviewModAssetProblem? problem))
        {
            return AssetFailure(query.Operation, source, asset, problem!);
        }

        string[] keys = records!
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(record => record.Key)
            .ToArray();
        return Success(
            query,
            source,
            asset!,
            key: null,
            keys,
            Page(query, keys.Length, records!.Count),
            record: null);
    }

    private static ReviewModAssetReport GetRecord(
        ReviewModAssetQuery query,
        IReviewModAssetSource source,
        ReviewModAssetInventorySnapshot inventory)
    {
        if (!TryLoadAdapted(
                query,
                source,
                inventory,
                out ReviewModAssetObservation? asset,
                out IReadOnlyList<ReviewModAssetRecord>? records,
                out ReviewModAssetProblem? problem))
        {
            return AssetFailure(query.Operation, source, asset, problem!);
        }

        if (!TryResolveKey(
                query.Key!,
                asset!.DataType,
                records!,
                out ReviewModAssetRecord? selected,
                out problem))
        {
            return AssetFailure(query.Operation, source, asset, problem!);
        }

        return Success(
            query,
            source,
            asset!,
            selected!.Key,
            keys: null,
            page: null,
            selected.Value);
    }

    private static bool TryLoadAdapted(
        ReviewModAssetQuery query,
        IReviewModAssetSource source,
        ReviewModAssetInventorySnapshot inventory,
        out ReviewModAssetObservation? asset,
        out IReadOnlyList<ReviewModAssetRecord>? records,
        out ReviewModAssetProblem? problem)
    {
        records = null;
        if (!TryResolveAsset(query.Asset!, inventory.Assets, out asset, out problem))
        {
            return false;
        }

        if (!ReviewModAssetAdapterRegistry.TryGet(
                asset!.DataType,
                out ReviewModAssetAdapterKind adapter,
                out _,
                out _))
        {
            problem = Problem(
                "modAssetAdapterUnavailable",
                "The observed runtime type is catalogued but has no reviewed safe adapter.");
            return false;
        }

        ReviewModAssetLoadResult loaded;
        try
        {
            loaded = source.Load(asset);
        }
        catch (Exception exception) when (IsControlledFailure(exception))
        {
            problem = Problem(
                "modAssetLoadFailed",
                $"The exact observed asset could not be loaded ({exception.GetType().Name}).");
            return false;
        }
        if (!loaded.Succeeded || loaded.Value is null)
        {
            asset = RefreshObservation(source, asset);
            problem = Problem(
                loaded.ProblemCode ?? "modAssetUnavailable",
                "The exact observed asset is unavailable in its current lifecycle generation.");
            return false;
        }
        if (loaded.Value.GetType() != asset.DataType)
        {
            problem = Problem(
                "modAssetTypeChanged",
                "The loaded runtime type no longer matches the exact observed request type.");
            return false;
        }

        asset = RefreshObservation(source, asset);

        if (!ReviewModAssetAdapterRegistry.TryAdapt(
                adapter,
                loaded.Value,
                out records,
                out string? adaptationProblem))
        {
            problem = Problem(
                adaptationProblem ?? "modAssetRecordNotSafelySerializable",
                "The exact observed asset could not be copied through its reviewed bounded adapter.");
            return false;
        }

        problem = null;
        return true;
    }

    private static ReviewModAssetObservation RefreshObservation(
        IReviewModAssetSource source,
        ReviewModAssetObservation prior) =>
        source.GetInventory().Assets.FirstOrDefault(candidate =>
            ReviewModAssetContract.AssetIdentityEquals(
                candidate.AssetName,
                prior.AssetName)
            && candidate.DataType == prior.DataType)
        ?? prior;

    private static bool TryResolveAsset(
        string input,
        IReadOnlyList<ReviewModAssetObservation> assets,
        out ReviewModAssetObservation? asset,
        out ReviewModAssetProblem? problem)
    {
        ReviewModAssetObservation[] exact = assets
            .Where(candidate => ReviewModAssetContract.AssetIdentityEquals(
                candidate.AssetName,
                input))
            .Take(3)
            .ToArray();
        if (exact.Length == 1 && !exact[0].NameCollision && !exact[0].TypeCollision)
        {
            asset = exact[0];
            problem = null;
            return true;
        }
        if (exact.Length > 1 || exact.Any(candidate => candidate.TypeCollision))
        {
            asset = exact.FirstOrDefault();
            problem = Problem(
                "modAssetTypeAmbiguous",
                "The observed asset name was requested with multiple runtime types.");
            return false;
        }
        if (exact.Length == 1 && exact[0].NameCollision)
        {
            asset = exact[0];
            problem = Problem(
                "modAssetNameAmbiguous",
                "The observed asset name collides after stable identity normalization.");
            return false;
        }

        ReviewModAssetObservation[] normalizedMatches = assets
            .Where(candidate => ReviewModAssetContract.StableAssetIdentityEquals(
                candidate.AssetName,
                input))
            .Take(3)
            .ToArray();
        if (normalizedMatches.Length == 1
            && !normalizedMatches[0].NameCollision
            && !normalizedMatches[0].TypeCollision)
        {
            asset = normalizedMatches[0];
            problem = null;
            return true;
        }

        asset = normalizedMatches.FirstOrDefault();
        problem = normalizedMatches.Length > 1
            ? Problem(
                "modAssetNameAmbiguous",
                "The observed asset token collides after stable identity normalization.")
            : Problem(
                "modAssetUnknown",
                "The requested mod-owned asset has not been observed since AlwaysOn subscribed.");
        return false;
    }

    private static bool TryResolveKey(
        string input,
        Type dataType,
        IReadOnlyList<ReviewModAssetRecord> records,
        out ReviewModAssetRecord? selected,
        out ReviewModAssetProblem? problem)
    {
        ReviewModAssetRecord[] exact = records
            .Where(record => string.Equals(record.Key, input, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exact.Length == 1)
        {
            selected = exact[0];
            problem = null;
            return true;
        }

        bool allowsStableAlias = dataType == typeof(Dictionary<string, string>)
            || dataType == typeof(Dictionary<string, int>);
        if (!allowsStableAlias)
        {
            selected = null;
            problem = Problem(
                "modAssetKeyUnknown",
                "The adapted asset has no record with that exact key.");
            return false;
        }

        string normalized = StableIdentityNormalizer.Normalize(input);
        ReviewModAssetRecord[] normalizedMatches = records
            .Where(record => string.Equals(
                StableIdentityNormalizer.Normalize(record.Key),
                normalized,
                StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        if (normalizedMatches.Length == 1)
        {
            selected = normalizedMatches[0];
            problem = null;
            return true;
        }

        selected = null;
        problem = normalizedMatches.Length > 1
            ? Problem(
                "modAssetKeyAmbiguous",
                "The adapted record key collides after stable identity normalization; use an exact key.")
            : Problem(
                "modAssetKeyUnknown",
                "The adapted asset has no record with that stable key.");
        return false;
    }

    private static ReviewModAssetReport Success(
        ReviewModAssetQuery query,
        IReviewModAssetSource source,
        ReviewModAssetObservation asset,
        string? key,
        IReadOnlyList<string>? keys,
        ReviewModAssetPage? page,
        JsonElement? record) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            "ready",
            query.Operation,
            source.GameVersion,
            source.GameFileVersion,
            ReviewModAssetContract.CoverageScope,
            ToReport(asset, availableOverride: true, lifecycleOverride: "ready"),
            key,
            null,
            keys,
            page,
            null,
            record,
            []);

    private static ReviewModAssetReport AssetFailure(
        string operation,
        IReviewModAssetSource source,
        ReviewModAssetObservation? asset,
        ReviewModAssetProblem problem) =>
        new(
            ReviewModAssetContract.SchemaVersion,
            "blocked",
            operation,
            source.GameVersion,
            source.GameFileVersion,
            ReviewModAssetContract.CoverageScope,
            asset is null ? null : ToReport(asset),
            null,
            null,
            null,
            null,
            null,
            null,
            [problem]);

    private static ReviewModAssetAssetReport ToReport(
        ReviewModAssetObservation asset,
        bool? availableOverride = null,
        string? lifecycleOverride = null)
    {
        bool supported = ReviewModAssetAdapterRegistry.TryGet(
            asset.DataType,
            out _,
            out _,
            out string? shape);
        string? problemCode = asset.TypeCollision
            ? "modAssetTypeAmbiguous"
            : asset.NameCollision
                ? "modAssetNameAmbiguous"
                : supported
                    ? null
                    : "modAssetAdapterUnavailable";
        return new ReviewModAssetAssetReport(
            asset.AssetName,
            asset.NamespaceOwnerId,
            asset.NamespaceOwnerStatus,
            null,
            "unavailableThroughPublicSmapiApi",
            asset.DataTypeName,
            shape,
            lifecycleOverride ?? asset.Lifecycle,
            asset.Generation,
            asset.RequestCount,
            asset.ReadyCount,
            availableOverride ?? asset.Available,
            supported,
            asset.NameCollision,
            asset.TypeCollision,
            problemCode);
    }

    private static ReviewModAssetProblem? Validate(ReviewModAssetQuery query)
    {
        if (query.Operation is not (
                ReviewModAssetContract.AssetsOperation
                or ReviewModAssetContract.KeysOperation
                or ReviewModAssetContract.GetOperation))
        {
            return Problem("modAssetOperationUnknown", "The review-mod-assets operation is unknown.");
        }
        bool listOperation = query.Operation is ReviewModAssetContract.AssetsOperation
            or ReviewModAssetContract.KeysOperation;
        if (query.Offset < 0
            || query.Limit < 1
            || query.Limit > ReviewModAssetContract.MaximumPageLimit
            || (!listOperation && (query.Offset != 0 || query.Limit != 1)))
        {
            return Problem(
                "modAssetPaginationInvalid",
                $"List offsets must be non-negative with limits from 1 through {ReviewModAssetContract.MaximumPageLimit}; exact reads do not accept pagination.");
        }

        bool needsAsset = query.Operation is ReviewModAssetContract.KeysOperation
            or ReviewModAssetContract.GetOperation;
        bool needsKey = query.Operation is ReviewModAssetContract.GetOperation;
        if (needsAsset
            && !ReviewModAssetContract.IsCanonicalAssetName(query.Asset))
        {
            return Problem(
                "modAssetNameInvalid",
                "A canonical bounded Mods/<owner>/... asset name is required.");
        }
        if (needsKey
            && (!ReviewModAssetContract.IsBoundedText(
                    query.Key,
                    ReviewModAssetContract.MaximumKeyLength)
                || string.IsNullOrWhiteSpace(query.Key)))
        {
            return Problem(
                "modAssetKeyInvalid",
                "A bounded non-empty adapted record key is required.");
        }
        if ((!needsAsset && query.Asset is not null)
            || (!needsKey && query.Key is not null))
        {
            return Problem(
                "modAssetRequestInvalid",
                "The review-mod-assets request has unexpected operands.");
        }

        return null;
    }

    private static ReviewModAssetPage Page(
        ReviewModAssetQuery query,
        int returned,
        int total)
    {
        int consumed = Math.Min(total, checked(query.Offset + returned));
        return new ReviewModAssetPage(
            query.Offset,
            query.Limit,
            returned,
            total,
            consumed < total ? consumed : null);
    }

    private static ReviewModAssetProblem Problem(string code, string message) =>
        new(code, message);

    private static bool IsControlledFailure(Exception exception) =>
        exception is ArgumentException
            or DirectoryNotFoundException
            or EncoderFallbackException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or JsonException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException;
}

internal static class ReviewModAssetResponseWriter
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(
        string runtimePath,
        ReviewModAssetResponseEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException(
                "The review-mod-assets runtime path is required.",
                nameof(runtimePath));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        string absoluteRuntimePath = Path.GetFullPath(runtimePath);
        FileAttributes runtimeAttributes = File.GetAttributes(absoluteRuntimePath);
        if ((runtimeAttributes & FileAttributes.ReparsePoint) != 0
            || (runtimeAttributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                "The review runtime response root is not a regular directory.");
        }

        string responsePath = ReviewModAssetContract.ResponsePath(
            absoluteRuntimePath,
            envelope.RequestId);
        string temporaryPath = responsePath + ".tmp";
        if (File.Exists(responsePath)
            || Directory.Exists(responsePath)
            || File.Exists(temporaryPath)
            || Directory.Exists(temporaryPath))
        {
            throw new InvalidDataException(
                "The review-mod-assets response target already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ResponseJsonOptions);
        if (bytes.Length > ReviewModAssetContract.MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "The bounded review-mod-assets response exceeds its maximum size.");
        }

        var ownsTemporary = false;
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
                ownsTemporary = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            FileAttributes temporaryAttributes = File.GetAttributes(temporaryPath);
            if ((temporaryAttributes & FileAttributes.ReparsePoint) != 0
                || (temporaryAttributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException(
                    "The owned review-mod-assets temporary response is not a regular file.");
            }

            File.Move(temporaryPath, responsePath);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporaryPath))
            {
                FileAttributes attributes = File.GetAttributes(temporaryPath);
                if ((attributes & FileAttributes.ReparsePoint) == 0
                    && (attributes & FileAttributes.Directory) == 0)
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}

#if SDVKIT_GAME_AVAILABLE
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
                        "SDVKit.AlwaysOn",
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
        if (!ReviewModAssetContract.IsRequestId(requestId))
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
            catch (Exception exception) when (!ReviewTextureException.IsFatal(exception))
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
        catch (Exception exception) when (!ReviewTextureException.IsFatal(exception))
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
            || !ReviewModAssetContract.IsRequestId(arguments[1])
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

        if (ReviewModAssetContract.TryDecode(token, maximumLength, out string decoded))
        {
            value = decoded;
            return true;
        }

        value = null;
        return false;
    }

}
#endif

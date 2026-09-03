namespace SdvKit.Cli.LiveLab;

internal static class LoadedModsContract
{
    public const int SchemaVersion = 1;
    public const int MaximumEntries = 257;
    public const int MaximumUniqueIdLength = 256;
    public const int MaximumVersionLength = 128;
    public const string AlwaysOnUniqueId = "SDVKit.AlwaysOn";
    public const string CaptureFailedProblemCode = "loadedModsCaptureFailed";

    public static LoadedModsStatusMarker CreateReady(
        IEnumerable<LoadedModEntry> mods,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ValidateTimestamp(capturedAtUtc);

        var entries = new List<LoadedModEntry>();
        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LoadedModEntry? mod in mods)
        {
            if (entries.Count == MaximumEntries)
            {
                throw new InvalidDataException(
                    $"The loaded-mod inventory exceeds {MaximumEntries} entries.");
            }

            if (mod is null)
            {
                throw new InvalidDataException(
                    "The loaded-mod inventory contains an invalid or duplicate identity.");
            }

            LoadedModEntry normalized = NormalizeEntry(mod);
            if (!uniqueIds.Add(normalized.UniqueId))
            {
                throw new InvalidDataException(
                    "The loaded-mod inventory contains an invalid or duplicate identity.");
            }

            entries.Add(normalized);
        }

        LoadedModEntry? alwaysOn = entries.SingleOrDefault(mod => string.Equals(
            mod.UniqueId,
            AlwaysOnUniqueId,
            StringComparison.Ordinal));
        if (alwaysOn is null || alwaysOn.IsContentPack)
        {
            throw new InvalidDataException(
                "The loaded-mod inventory does not contain the SDVKit AlwaysOn code mod.");
        }

        return new LoadedModsStatusMarker(
            SchemaVersion,
            capturedAtUtc,
            entries
                .OrderBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ProblemCode: null);
    }

    public static LoadedModsStatusMarker CreateCaptureFailure(
        DateTimeOffset capturedAtUtc)
    {
        ValidateTimestamp(capturedAtUtc);
        return new LoadedModsStatusMarker(
            SchemaVersion,
            capturedAtUtc,
            [],
            CaptureFailedProblemCode);
    }

    public static bool IsValidEntry(LoadedModEntry mod) =>
        IsSafeUniqueId(mod.UniqueId)
        && IsBoundedVersion(mod.Version)
        && IsNormalizedVersion(mod.Version);

    private static LoadedModEntry NormalizeEntry(LoadedModEntry mod)
    {
        if (!IsSafeUniqueId(mod.UniqueId) || !IsBoundedVersion(mod.Version))
        {
            throw new InvalidDataException(
                "The loaded-mod inventory contains an invalid identity.");
        }

        string version = ProjectModLaunchState.NormalizeVersion(mod.Version);
        if (version.Length > MaximumVersionLength)
        {
            throw new InvalidDataException(
                "The loaded-mod inventory contains an invalid version.");
        }

        return mod with { Version = version };
    }

    private static bool IsSafeUniqueId(string? uniqueId) =>
        uniqueId is not null
        && uniqueId.Length is > 0 and <= MaximumUniqueIdLength
        && uniqueId.All(IsSafeUniqueIdCharacter);

    private static bool IsBoundedVersion(string? version) =>
        version is not null
        && version.Length is > 0 and <= MaximumVersionLength
        && !version.Any(char.IsControl);

    private static bool IsNormalizedVersion(string version)
    {
        try
        {
            return string.Equals(
                ProjectModLaunchState.NormalizeVersion(version),
                version,
                StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsSafeUniqueIdCharacter(char character) =>
        character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '.'
            or '-';

    private static void ValidateTimestamp(DateTimeOffset capturedAtUtc)
    {
        if (capturedAtUtc == default || capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The loaded-mod capture time must be a non-default UTC timestamp.",
                nameof(capturedAtUtc));
        }
    }
}

internal sealed record LoadedModEntry(
    string UniqueId,
    string Version,
    bool IsContentPack);

internal sealed record LoadedModsStatusMarker(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<LoadedModEntry> Mods,
    string? ProblemCode);

internal sealed record LoadedModsStatusReport(
    string State,
    int? SchemaVersion,
    DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<LoadedModEntry> Mods,
    string? ProblemCode);

using System.Security;
using System.Text.Json.Serialization;

namespace SdvKit.Cli;

internal sealed record DetectedInstallation(string GamePath);

internal sealed record IncompleteInstallation(string GamePath, IReadOnlyList<string> MissingRequirements, IReadOnlyList<string> Actions);

internal sealed record DoctorReport(
    int SchemaVersion,
    string Status,
    IReadOnlyList<DetectedInstallation> Installations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<IncompleteInstallation>? IncompleteCandidates { get; init; }

    public const string Ready = "ready";
    public const string Ambiguous = "ambiguous";
    public const string NotFound = "notFound";
}

internal static class GameInstallationDiscovery
{
    private const string GameAssembly = "Stardew Valley.dll";
    private const string GameExecutable = "Stardew Valley.exe";
    private const string SmapiExecutable = "StardewModdingAPI.exe";
    private const string SmapiAssembly = "StardewModdingAPI.dll";

    public static DoctorReport Discover()
    {
        return Inspect(GameInstallLocator.FindCandidatePaths());
    }

    internal static DoctorReport Inspect(IEnumerable<string> candidatePaths, bool includeMissingPaths = false)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var incomplete = new List<IncompleteInstallation>();
        var installations = new List<DetectedInstallation>();
        var seen = new HashSet<string>(pathComparer);
        foreach (string candidatePath in candidatePaths)
        {
            string? normalized = Normalize(candidatePath);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            string[] missing = new[] { GameExecutable, GameAssembly, SmapiExecutable, SmapiAssembly }
                .Where(file => !File.Exists(Path.Combine(normalized, file))).ToArray();
            if (missing.Length == 0)
            {
                installations.Add(new DetectedInstallation(normalized));
            }
            else if (includeMissingPaths || Directory.Exists(normalized))
            {
                var actions = new List<string>();
                if (missing.Contains(GameExecutable) || missing.Contains(GameAssembly))
                    actions.Add("Select the Stardew Valley installation directory, or repair/install the Windows game through its store client.");
                if (missing.Contains(SmapiExecutable) || missing.Contains(SmapiAssembly))
                    actions.Add("Install or repair SMAPI in this game directory, then rerun doctor --game-path <directory> --json.");
                incomplete.Add(new IncompleteInstallation(normalized, missing, actions));
            }
        }

        installations.Sort((left, right) => pathComparer.Compare(left.GamePath, right.GamePath));
        string status = installations.Count switch
        {
            1 => DoctorReport.Ready,
            > 1 => DoctorReport.Ambiguous,
            _ => DoctorReport.NotFound,
        };

        incomplete.Sort((left, right) => pathComparer.Compare(left.GamePath, right.GamePath));
        return new DoctorReport(1, status, installations)
        {
            IncompleteCandidates = incomplete.Count == 0 ? null : incomplete,
        };
    }

    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or SecurityException)
        {
            return null;
        }
    }
}

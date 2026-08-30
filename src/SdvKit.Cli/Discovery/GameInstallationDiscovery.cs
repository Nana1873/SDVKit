using System.Security;

namespace SdvKit.Cli;

internal sealed record DetectedInstallation(string GamePath);

internal sealed record DoctorReport(
    int SchemaVersion,
    string Status,
    IReadOnlyList<DetectedInstallation> Installations)
{
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

    internal static DoctorReport Inspect(IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var installations = new List<DetectedInstallation>();
        var seen = new HashSet<string>(pathComparer);
        foreach (string candidatePath in candidatePaths)
        {
            string? normalized = Normalize(candidatePath);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            bool ready = File.Exists(Path.Combine(normalized, GameExecutable))
                && File.Exists(Path.Combine(normalized, GameAssembly))
                && File.Exists(Path.Combine(normalized, SmapiExecutable))
                && File.Exists(Path.Combine(normalized, SmapiAssembly));
            if (ready)
            {
                installations.Add(new DetectedInstallation(normalized));
            }
        }

        installations.Sort((left, right) => pathComparer.Compare(left.GamePath, right.GamePath));
        string status = installations.Count switch
        {
            1 => DoctorReport.Ready,
            > 1 => DoctorReport.Ambiguous,
            _ => DoctorReport.NotFound,
        };

        return new DoctorReport(1, status, installations);
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

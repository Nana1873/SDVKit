using System.Globalization;

namespace SdvKit.Cli.LiveLab;

internal static class ProjectModContract
{
    public const int SchemaVersion = 1;
    public const string WaitingForGameLaunchPhase = "waitingForGameLaunch";
    public const string LoadedPhase = "loaded";
    public const string FailedPhase = "failed";
}

internal sealed record ProjectModLaunchState(
    string UniqueId,
    string Version,
    string BuildIdentity)
{
    public void Validate()
    {
        if (!IsUniqueId(UniqueId)
            || TryNormalizeVersion(Version) is null
            || !ModBuildIdentity.IsValid(BuildIdentity))
        {
            throw new InvalidDataException(
                "The project-mod launch identity is invalid.");
        }
    }

    public static string NormalizeVersion(string version)
    {
        string? normalized = TryNormalizeVersion(version);
        return normalized
            ?? throw new InvalidDataException(
                "The project-mod manifest version is invalid.");
    }

    private static bool IsUniqueId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '.'
            or '-');

    private static string? TryNormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] versionAndMetadata = value.Split('+', count: 2);
        if (versionAndMetadata.Length == 2
            && !IsVersionTag(versionAndMetadata[1]))
        {
            return null;
        }

        string[] versionAndPrerelease = versionAndMetadata[0].Split('-', count: 2);
        string[] numbers = versionAndPrerelease[0].Split('.');
        if (numbers.Length is < 2 or > 3
            || numbers.Any(number => number.Length == 0
                || (number.Length > 1 && number[0] == '0')
                || !number.All(character => character is >= '0' and <= '9')
                || !int.TryParse(
                    number,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _)))
        {
            return null;
        }

        if (versionAndPrerelease.Length == 2
            && !IsVersionTag(versionAndPrerelease[1]))
        {
            return null;
        }

        string core = numbers.Length == 2
            ? $"{numbers[0]}.{numbers[1]}.0"
            : versionAndPrerelease[0];
        string prerelease = versionAndPrerelease.Length == 2
            ? $"-{versionAndPrerelease[1]}"
            : string.Empty;
        string metadata = versionAndMetadata.Length == 2
            ? $"+{versionAndMetadata[1]}"
            : string.Empty;
        return core + prerelease + metadata;
    }

    private static bool IsVersionTag(string value)
    {
        var needsAlphaNumeric = true;
        foreach (char character in value)
        {
            bool isAlphaNumeric = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9';
            if (isAlphaNumeric)
            {
                needsAlphaNumeric = false;
            }
            else if ((character is '.' or '-') && !needsAlphaNumeric)
            {
                needsAlphaNumeric = true;
            }
            else
            {
                return false;
            }
        }

        return !needsAlphaNumeric;
    }
}

internal sealed record ProjectModStatusMarker(
    int SchemaVersion,
    string Phase,
    string ExpectedUniqueId,
    string ExpectedVersion,
    string? LoadedUniqueId,
    string? LoadedVersion,
    string BuildIdentity,
    bool LoadConfirmed,
    string? Message);

internal sealed record ProjectModStatusReport(
    string State,
    int? SchemaVersion,
    string? Phase,
    string? ExpectedUniqueId,
    string? ExpectedVersion,
    string? LoadedUniqueId,
    string? LoadedVersion,
    string? BuildIdentity,
    bool? LoadConfirmed,
    string? Message);

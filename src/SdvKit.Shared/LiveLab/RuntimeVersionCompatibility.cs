namespace SdvKit.Cli.LiveLab;

internal static class RuntimeVersionCompatibility
{
    private static readonly Version MinimumGameVersion = new(1, 6, 15);
    private static readonly Version MaximumGameVersionExclusive = new(1, 7);
    private static readonly Version MinimumGameFileVersion = new(1, 6, 15, 24356);
    private static readonly Version MaximumGameFileVersionExclusive = new(1, 7);
    private static readonly Version MinimumSmapiVersion = new(4, 5);
    private static readonly Version MaximumSmapiVersionExclusive = new(5, 0);

    private const string SupportedRuntimeDescription =
        "Stardew game >= 1.6.15 and < 1.7, "
        + "Stardew file version >= 1.6.15.24356 and < 1.7, "
        + "and SMAPI >= 4.5.0 and < 5.0";

    public static bool TryValidate(
        string gameVersion,
        string gameFileVersion,
        string smapiVersion,
        out string error)
    {
        if (!Version.TryParse(gameVersion, out Version? parsedGameVersion)
            || !Version.TryParse(gameFileVersion, out Version? parsedGameFileVersion)
            || !Version.TryParse(smapiVersion, out Version? parsedSmapiVersion))
        {
            error = FailureMessage(
                gameVersion,
                gameFileVersion,
                smapiVersion,
                "one or more reported versions are not parseable");
            return false;
        }

        var incompatible = new List<string>();
        AddIfOutsideRange(
            parsedGameVersion,
            MinimumGameVersion,
            MaximumGameVersionExclusive,
            "Stardew game",
            incompatible);
        AddIfOutsideRange(
            parsedGameFileVersion,
            MinimumGameFileVersion,
            MaximumGameFileVersionExclusive,
            "Stardew file version",
            incompatible);
        AddIfOutsideRange(
            parsedSmapiVersion,
            MinimumSmapiVersion,
            MaximumSmapiVersionExclusive,
            "SMAPI",
            incompatible);
        if (incompatible.Count > 0)
        {
            error = FailureMessage(
                gameVersion,
                gameFileVersion,
                smapiVersion,
                $"unsupported component(s): {string.Join(", ", incompatible)}");
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void AddIfOutsideRange(
        Version actual,
        Version minimum,
        Version maximumExclusive,
        string component,
        List<string> incompatible)
    {
        if (actual.CompareTo(minimum) < 0
            || actual.CompareTo(maximumExclusive) >= 0)
        {
            incompatible.Add(component);
        }
    }

    private static string FailureMessage(
        string gameVersion,
        string gameFileVersion,
        string smapiVersion,
        string reason) =>
        $"Test-save automation supports {SupportedRuntimeDescription} when its required runtime capabilities are present; "
        + $"the runtime reported Stardew game '{gameVersion}', Stardew file version '{gameFileVersion}', "
        + $"and SMAPI '{smapiVersion}' ({reason}).";
}

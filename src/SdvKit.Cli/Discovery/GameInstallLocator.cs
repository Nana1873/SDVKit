using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SdvKit.Cli;

internal static class GameInstallLocator
{
    private static readonly string SteamGameRelativePath = Path.Combine(
        "steamapps",
        "common",
        "Stardew Valley");

    public static IReadOnlyList<string> FindCandidatePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var steamRoots = new List<string>();
        AddIfPresent(steamRoots, ReadRegistry(
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            "SteamPath"));
        AddIfPresent(steamRoots, ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath"));
        AddIfPresent(steamRoots, CombineIfPresent(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam"));
        AddIfPresent(steamRoots, CombineIfPresent(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Steam"));

        var candidates = new List<string>();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddIfPresent(candidates, ReadCustomGamePath(
                Path.Combine(userProfile, "stardewvalley.targets")));
        }

        AddIfPresent(candidates, ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 413150",
            "InstallLocation"));
        AddIfPresent(candidates, ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 413150",
            "InstallLocation"));

        StringComparer pathComparer = StringComparer.OrdinalIgnoreCase;
        foreach (string steamRoot in steamRoots.Distinct(pathComparer))
        {
            string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            string? content = ReadText(libraryFile);
            candidates.AddRange(GetSteamGamePaths(steamRoot, content));
        }

        AddGogCandidates(candidates);
        return candidates
            .Select(Normalize)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(pathComparer)
            .OrderBy(path => path, pathComparer)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetSteamGamePaths(
        string steamRoot,
        string? libraryFoldersContent)
    {
        var paths = new List<string>
        {
            Path.Combine(steamRoot, SteamGameRelativePath),
        };
        if (libraryFoldersContent is not null)
        {
            paths.AddRange(SteamVdfParser
                .ExtractLibraryPaths(libraryFoldersContent)
                .Select(libraryPath => Path.Combine(libraryPath, SteamGameRelativePath)));
        }

        return paths;
    }

    private static void AddGogCandidates(List<string> candidates)
    {
        AddIfPresent(candidates, ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1453375253",
            "PATH"));
        AddIfPresent(candidates, ReadRegistry(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1453375253",
            "PATH"));

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AddIfPresent(candidates, CombineIfPresent(
            programFiles,
            "GOG Galaxy",
            "Games",
            "Stardew Valley"));
        AddIfPresent(candidates, CombineIfPresent(
            programFilesX86,
            "GOG Galaxy",
            "Games",
            "Stardew Valley"));
        AddIfPresent(candidates, CombineIfPresent(
            programFiles,
            "GalaxyClient",
            "Games",
            "Stardew Valley"));
        AddIfPresent(candidates, CombineIfPresent(
            programFilesX86,
            "GalaxyClient",
            "Games",
            "Stardew Valley"));
        AddIfPresent(candidates, CombineIfPresent(
            programFiles,
            "GOG Games",
            "Stardew Valley"));
        AddIfPresent(candidates, CombineIfPresent(
            programFilesX86,
            "GOG Games",
            "Stardew Valley"));

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        AddIfPresent(candidates, CombineIfPresent(systemRoot, "GOG Games", "Stardew Valley"));

        for (char driveLetter = 'C'; driveLetter <= 'H'; driveLetter++)
        {
            candidates.Add($@"{driveLetter}:\Program Files\ModifiableWindowsApps\Stardew Valley");
        }
    }

    internal static string? ReadCustomGamePath(string targetsFile)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using XmlReader reader = XmlReader.Create(targetsFile, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            string? gamePath = document
                .Descendants()
                .FirstOrDefault(element => string.Equals(
                    element.Name.LocalName,
                    "GamePath",
                    StringComparison.Ordinal))
                ?.Value;
            return string.IsNullOrWhiteSpace(gamePath) ? null : gamePath.Trim();
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException
            or XmlException)
        {
            return null;
        }
    }

    private static string? ReadRegistry(string keyName, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Registry.GetValue(keyName, valueName, null) as string;
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Normalize(string path)
    {
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

    private static void AddIfPresent(List<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    private static string? CombineIfPresent(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return parts.Aggregate(root, Path.Combine);
    }
}

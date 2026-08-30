using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class GameInstallationDiscoveryTests
{
    [Fact]
    public void EmptyCandidatesAreNotFound()
    {
        DoctorReport report = GameInstallationDiscovery.Inspect([]);

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(DoctorReport.NotFound, report.Status);
        Assert.Empty(report.Installations);
    }

    [Theory]
    [InlineData("Stardew Valley.exe")]
    [InlineData("Stardew Valley.dll")]
    [InlineData("StardewModdingAPI.exe")]
    [InlineData("StardewModdingAPI.dll")]
    public void EveryReadinessMarkerIsRequired(string omittedMarker)
    {
        using TemporaryDirectory temporary = new();
        foreach (string marker in new[]
        {
            "Stardew Valley.exe",
            "Stardew Valley.dll",
            "StardewModdingAPI.exe",
            "StardewModdingAPI.dll",
        })
        {
            if (!string.Equals(marker, omittedMarker, StringComparison.Ordinal))
            {
                temporary.WriteFile(marker);
            }
        }

        DoctorReport report = GameInstallationDiscovery.Inspect([temporary.Path]);

        Assert.Equal(DoctorReport.NotFound, report.Status);
        Assert.Empty(report.Installations);
        Assert.False(Directory.Exists(System.IO.Path.Combine(temporary.Path, ".sdvkit")));
    }

    [Fact]
    public void MultipleReadyInstallationsAreSortedAndAmbiguous()
    {
        using TemporaryDirectory firstRoot = new();
        using TemporaryDirectory secondRoot = new();
        string first = firstRoot.WriteFile("A/.keep");
        string second = secondRoot.WriteFile("B/.keep");
        string firstPath = System.IO.Path.GetDirectoryName(first)!;
        string secondPath = System.IO.Path.GetDirectoryName(second)!;
        CreateReadyInstallation(firstPath);
        CreateReadyInstallation(secondPath);

        DoctorReport report = GameInstallationDiscovery.Inspect([
            secondPath,
            firstPath + System.IO.Path.DirectorySeparatorChar,
            firstPath,
        ]);

        Assert.Equal(DoctorReport.Ambiguous, report.Status);
        Assert.Equal(2, report.Installations.Count);
        Assert.Equal(
            report.Installations.OrderBy(
                installation => installation.GamePath,
                StringComparer.OrdinalIgnoreCase),
            report.Installations);
    }

    [Fact]
    public void SteamVdfParserReadsEscapedLibraryPathsAndDeduplicatesThem()
    {
        const string content = """
            "libraryfolders"
            {
              "1"
              {
                "path" "E:\\SteamLibrary"
              }
              "2"
              {
                "path" "D:\\Games"
              }
              "3"
              {
                "path" "e:\\steamlibrary"
              }
            }
            """;

        IReadOnlyList<string> paths = SteamVdfParser.ExtractLibraryPaths(content);

        Assert.Equal([@"E:\SteamLibrary", @"D:\Games"], paths);
    }

    [Fact]
    public void SteamGamePathsCombineThePrimaryAndAdditionalLibraries()
    {
        const string content = """
            "libraryfolders"
            {
              "1" { "path" "E:\\SteamLibrary" }
            }
            """;

        IReadOnlyList<string> paths = GameInstallLocator.GetSteamGamePaths(
            @"C:\Program Files (x86)\Steam",
            content);

        Assert.Equal(
            [
                @"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
                @"E:\SteamLibrary\steamapps\common\Stardew Valley",
            ],
            paths);
    }

    [Fact]
    public void CustomTargetsFileReadsNamespacedGamePath()
    {
        using TemporaryDirectory temporary = new();
        string targetsFile = temporary.WriteFile("stardewvalley.targets", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <GamePath> E:\Games\Stardew Valley </GamePath>
              </PropertyGroup>
            </Project>
            """);

        string? path = GameInstallLocator.ReadCustomGamePath(targetsFile);

        Assert.Equal(@"E:\Games\Stardew Valley", path);
    }

    [Fact]
    public void InvalidOrOverlongCandidateIsIgnored()
    {
        string overlong = new('x', 40_000);

        DoctorReport report = GameInstallationDiscovery.Inspect([overlong]);

        Assert.Equal(DoctorReport.NotFound, report.Status);
        Assert.Empty(report.Installations);
    }

    private static void CreateReadyInstallation(string path)
    {
        foreach (string marker in new[]
        {
            "Stardew Valley.exe",
            "Stardew Valley.dll",
            "StardewModdingAPI.exe",
            "StardewModdingAPI.dll",
        })
        {
            File.WriteAllText(System.IO.Path.Combine(path, marker), string.Empty);
        }
    }
}

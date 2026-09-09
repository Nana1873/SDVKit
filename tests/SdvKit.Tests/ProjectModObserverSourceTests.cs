using System.Xml.Linq;

namespace SdvKit.Tests;

public sealed class ProjectModObserverSourceTests
{
    [Fact]
    public void LoadedModsCaptureRunsFromGameLaunchedBeforeTheStatusWrite()
    {
        string source = ReadSource("ModEntry.cs");
        int gameLaunched = source.IndexOf(
            "helper.Events.GameLoop.GameLaunched +=",
            StringComparison.Ordinal);
        int capture = source.IndexOf(
            "_projectMod?.ObserveLoadedMod();",
            gameLaunched,
            StringComparison.Ordinal);
        int statusWrite = source.IndexOf(
            "WriteActiveStatus();",
            capture,
            StringComparison.Ordinal);

        Assert.True(gameLaunched >= 0);
        Assert.True(capture > gameLaunched);
        Assert.True(statusWrite > capture);
        Assert.Equal(
            capture,
            source.LastIndexOf(
                "_projectMod?.ObserveLoadedMod();",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LoadedModsCaptureUsesOnlyThePublicBoundedRegistrySurface()
    {
        string source = ReadObserverSource();

        Assert.Contains("_modRegistry.GetAll()", source, StringComparison.Ordinal);
        Assert.Equal(
            source.IndexOf("_modRegistry.GetAll()", StringComparison.Ordinal),
            source.LastIndexOf("_modRegistry.GetAll()", StringComparison.Ordinal));
        Assert.Contains("IModInfo mod", source, StringComparison.Ordinal);
        Assert.Contains("IManifest manifest", source, StringComparison.Ordinal);
        Assert.Contains("manifest.UniqueID", source, StringComparison.Ordinal);
        Assert.Contains("manifest.Version.ToString()", source, StringComparison.Ordinal);
        Assert.Contains("mod.IsContentPack", source, StringComparison.Ordinal);
        Assert.Contains(
            "loaded.Count == LoadedModsContract.MaximumEntries",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("StardewModdingAPI.Framework", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DirectoryPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectoryInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtraFields", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateKeys", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedModsCaptureRunsOnceAndStoresOnlyAControlledFailureCode()
    {
        string source = ReadObserverSource();
        int capture = source.IndexOf("CaptureLoadedModsOnce();", StringComparison.Ordinal);
        int targetPhaseGuard = source.IndexOf(
            "if (!string.Equals(",
            capture,
            StringComparison.Ordinal);
        int onceGuard = source.IndexOf("if (_loadedModsCaptured)", StringComparison.Ordinal);
        int controlledFailure = source.IndexOf(
            "LoadedModsContract.CreateCaptureFailure(capturedAtUtc)",
            onceGuard,
            StringComparison.Ordinal);
        int privateLog = source.IndexOf(
            "exception.GetBaseException().Message",
            controlledFailure,
            StringComparison.Ordinal);

        Assert.True(capture >= 0);
        Assert.True(targetPhaseGuard > capture);
        Assert.True(onceGuard > targetPhaseGuard);
        Assert.True(controlledFailure > onceGuard);
        Assert.True(privateLog > controlledFailure);
    }

    [Theory]
    [MemberData(nameof(AlwaysOnLinkedSourceFiles))]
    public void PortablePackageIncludesAlwaysOnLinkedCliSource(string fileName)
    {
        string packageScript = ReadRepositoryFile(
            "scripts",
            "package-windows-x64.ps1");

        Assert.Contains($"\"{fileName}\"", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerTransferUsesOwnedNativeCapacityAndProcessLocalInputPaths()
    {
        string source = ReadSource("ReviewContainerTransferCommand.cs");
        Assert.Contains("Context.ScreenId != 0", source, StringComparison.Ordinal);
        Assert.Contains("TryVerifyReviewFixture", source, StringComparison.Ordinal);
        Assert.Contains("Game1.player.couldInventoryAcceptThisItem(probe)", source, StringComparison.Ordinal);
        Assert.Contains("Utility.addItemToThisInventoryList", source, StringComparison.Ordinal);
        Assert.Contains("original.canStackWith(item)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewVirtualCursor.TryChord", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", source, StringComparison.Ordinal);
    }

    public static TheoryData<string> AlwaysOnLinkedSourceFiles
    {
        get
        {
            XDocument project = XDocument.Parse(ReadRepositoryFile("src", "SdvKit.AlwaysOn", "SdvKit.AlwaysOn.csproj"));
            var files = new TheoryData<string>();
            foreach (XElement compile in project.Descendants("Compile").Where(element => element.Attribute("Link") is not null))
                files.Add(Path.GetFileName(compile.Attribute("Include")!.Value));
            return files;
        }
    }

    private static string ReadObserverSource()
        => ReadSource("ProjectModObserver.cs");

    private static string ReadSource(string fileName)
        => ReadRepositoryFile("src", "SdvKit.AlwaysOn", fileName);

    private static string ReadRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }
}

using System.Xml.Linq;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LabWindowPreferencesTests
{
    [Fact]
    public void ExistingIsolatedPreferencesAreForcedToWindowed1280By720()
    {
        using TemporaryDirectory temporary = new();
        string stardewDataPath = Path.Combine(temporary.Path, "StardewValley");
        Directory.CreateDirectory(stardewDataPath);
        string path = Path.Combine(stardewDataPath, "startup_preferences");
        File.WriteAllText(
            path,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <StartupPreferences>
              <windowMode>0</windowMode>
              <clientOptions>
                <fullscreen>true</fullscreen>
                <windowedBorderlessFullscreen>true</windowedBorderlessFullscreen>
                <preferredResolutionX>2560</preferredResolutionX>
                <preferredResolutionY>1440</preferredResolutionY>
              </clientOptions>
            </StartupPreferences>
            """);

        LabWindowPreferences.Prepare(stardewDataPath);

        XDocument document = XDocument.Load(path);
        XElement root = Assert.IsType<XElement>(document.Root);
        Assert.Equal("1", root.Element("windowMode")?.Value);
        XElement options = Assert.IsType<XElement>(root.Element("clientOptions"));
        Assert.Equal("false", options.Element("fullscreen")?.Value);
        Assert.Equal("false", options.Element("windowedBorderlessFullscreen")?.Value);
        Assert.Equal("1280", options.Element("preferredResolutionX")?.Value);
        Assert.Equal("720", options.Element("preferredResolutionY")?.Value);
    }

    [Fact]
    public void MissingPreferencesRemainOwnedByStardew()
    {
        using TemporaryDirectory temporary = new();

        LabWindowPreferences.Prepare(temporary.Path);

        Assert.False(File.Exists(Path.Combine(temporary.Path, "startup_preferences")));
    }

    [Fact]
    public void UnexpectedPreferencesShapeFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.WriteFile(
            "startup_preferences",
            "<StartupPreferences><windowMode>0</windowMode></StartupPreferences>");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => LabWindowPreferences.Prepare(temporary.Path));

        Assert.Contains("clientOptions", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MalformedPreferencesFailAsControlledInvalidData()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.WriteFile(
            "startup_preferences",
            "<StartupPreferences>");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => LabWindowPreferences.Prepare(temporary.Path));

        Assert.Contains("invalid XML", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }
}

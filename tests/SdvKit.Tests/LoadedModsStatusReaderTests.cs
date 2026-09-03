using System.Text.Json;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LoadedModsStatusReaderTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset CapturedAt = StartedAt.AddSeconds(5);

    private static readonly DateTimeOffset ObservedAt = StartedAt.AddSeconds(10);

    [Fact]
    public void CaptureSortsAndReaderReturnsTheExactLoadedInventory()
    {
        using TemporaryDirectory temporary = new();
        LoadedModsStatusMarker loadedMods = LoadedModsContract.CreateReady(
            [
                new LoadedModEntry("Zulu.Pack", "2.0", IsContentPack: true),
                new LoadedModEntry(
                    LoadedModsContract.AlwaysOnUniqueId,
                    "0.6.1",
                    IsContentPack: false),
                new LoadedModEntry("Alpha.Mod", "1.2.3-beta+local", IsContentPack: false),
            ],
            CapturedAt);
        string statusPath = WriteMarker(temporary, loadedMods);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("active", report.State);
        LoadedModsStatusReport inventory = Assert.IsType<LoadedModsStatusReport>(
            report.LoadedMods);
        Assert.Equal("ready", inventory.State);
        Assert.Equal(LoadedModsContract.SchemaVersion, inventory.SchemaVersion);
        Assert.Equal(CapturedAt, inventory.CapturedAtUtc);
        Assert.Null(inventory.ProblemCode);
        Assert.Equal(
            ["Alpha.Mod", LoadedModsContract.AlwaysOnUniqueId, "Zulu.Pack"],
            inventory.Mods.Select(mod => mod.UniqueId).ToArray());
        Assert.True(inventory.Mods[^1].IsContentPack);
        Assert.Equal("2.0.0", inventory.Mods[^1].Version);
    }

    [Fact]
    public void MissingLoadedModsSnapshotRemainsOptional()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = WriteMarker(temporary, loadedMods: null);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("active", report.State);
        Assert.Null(report.LoadedMods);
    }

    [Fact]
    public void CaptureFailureReturnsOnlyItsControlledProblemCode()
    {
        using TemporaryDirectory temporary = new();
        LoadedModsStatusMarker loadedMods =
            LoadedModsContract.CreateCaptureFailure(CapturedAt);
        string statusPath = WriteMarker(temporary, loadedMods);

        LoadedModsStatusReport report = Assert.IsType<LoadedModsStatusReport>(
            AlwaysOnStatusReader.Read(
                statusPath,
                "launch-1",
                Process(),
                ObservedAt).LoadedMods);

        Assert.Equal("failed", report.State);
        Assert.Equal(
            LoadedModsContract.CaptureFailedProblemCode,
            report.ProblemCode);
        Assert.Empty(report.Mods);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("beforeProcess")]
    [InlineData("afterStatus")]
    [InlineData("nullMods")]
    [InlineData("empty")]
    [InlineData("tooMany")]
    [InlineData("unsafeId")]
    [InlineData("longId")]
    [InlineData("emptyVersion")]
    [InlineData("longVersion")]
    [InlineData("controlVersion")]
    [InlineData("unsafeVersion")]
    [InlineData("nonCanonicalVersion")]
    [InlineData("duplicate")]
    [InlineData("unsorted")]
    [InlineData("missingAlwaysOn")]
    [InlineData("alwaysOnPack")]
    [InlineData("unknownProblem")]
    [InlineData("problemWithMods")]
    public void ReaderRejectsMalformedOrUnboundedInventories(string caseName)
    {
        using TemporaryDirectory temporary = new();
        LoadedModsStatusMarker marker = ReadyMarker();
        marker = caseName switch
        {
            "schema" => marker with { SchemaVersion = 2 },
            "beforeProcess" => marker with
            {
                CapturedAtUtc = StartedAt.AddTicks(-1),
            },
            "afterStatus" => marker with
            {
                CapturedAtUtc = ObservedAt.AddSeconds(2),
            },
            "nullMods" => marker with { Mods = null! },
            "empty" => marker with { Mods = [] },
            "tooMany" => marker with { Mods = TooManyMods() },
            "unsafeId" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example/Mod", "1.0.0", false),
                    AlwaysOn(),
                ],
            },
            "longId" => marker with
            {
                Mods =
                [
                    new LoadedModEntry(
                        new string('A', LoadedModsContract.MaximumUniqueIdLength + 1),
                        "1.0.0",
                        false),
                    AlwaysOn(),
                ],
            },
            "emptyVersion" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", string.Empty, false),
                    AlwaysOn(),
                ],
            },
            "longVersion" => marker with
            {
                Mods =
                [
                    new LoadedModEntry(
                        "Example.Mod",
                        new string('1', LoadedModsContract.MaximumVersionLength + 1),
                        false),
                    AlwaysOn(),
                ],
            },
            "controlVersion" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", "1.0.0\nsecret", false),
                    AlwaysOn(),
                ],
            },
            "unsafeVersion" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", "../../secret", false),
                    AlwaysOn(),
                ],
            },
            "nonCanonicalVersion" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", "1.2", false),
                    AlwaysOn(),
                ],
            },
            "duplicate" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", "1.0.0", false),
                    new LoadedModEntry("example.mod", "1.0.0", false),
                    AlwaysOn(),
                ],
            },
            "unsorted" => marker with
            {
                Mods =
                [
                    AlwaysOn(),
                    new LoadedModEntry("Example.Mod", "1.0.0", false),
                ],
            },
            "missingAlwaysOn" => marker with
            {
                Mods = [new LoadedModEntry("Example.Mod", "1.0.0", false)],
            },
            "alwaysOnPack" => marker with
            {
                Mods =
                [
                    new LoadedModEntry("Example.Mod", "1.0.0", false),
                    AlwaysOn() with { IsContentPack = true },
                ],
            },
            "unknownProblem" => marker with
            {
                Mods = [],
                ProblemCode = "private exception detail",
            },
            "problemWithMods" => marker with
            {
                ProblemCode = LoadedModsContract.CaptureFailedProblemCode,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };
        string statusPath = WriteMarker(temporary, marker);

        LoadedModsStatusReport report = Assert.IsType<LoadedModsStatusReport>(
            AlwaysOnStatusReader.Read(
                statusPath,
                "launch-1",
                Process(),
                ObservedAt).LoadedMods);

        Assert.Equal("invalid", report.State);
        Assert.Null(report.SchemaVersion);
        Assert.Null(report.CapturedAtUtc);
        Assert.Empty(report.Mods);
        Assert.Null(report.ProblemCode);
    }

    [Fact]
    public void CaptureRejectsAnEntryPastTheHardMaximum()
    {
        LoadedModEntry[] entries = TooManyMods();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            LoadedModsContract.CreateReady(entries, CapturedAt));

        Assert.Contains(
            LoadedModsContract.MaximumEntries.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatusWriterTransportsTheNestedSnapshot()
    {
        using TemporaryDirectory temporary = new();
        const string launchId = "11111111111111111111111111111111";
        string statusPath = Path.Combine(temporary.Path, "always-on.json");
        var writer = new StatusWriter(launchId, statusPath);
        LoadedModsStatusMarker loadedMods = LoadedModsContract.CreateReady(
            [AlwaysOn()],
            DateTimeOffset.UtcNow);

        writer.Write(
            "active",
            tick: 1,
            isActive: false,
            pauseWhenOutOfFocus: false,
            loadedMods: loadedMods);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(statusPath));
        JsonElement nested = json.RootElement.GetProperty("loadedMods");
        Assert.Equal(LoadedModsContract.SchemaVersion, nested.GetProperty("schemaVersion").GetInt32());
        Assert.False(nested.GetProperty("mods")[0].GetProperty("isContentPack").GetBoolean());
        Assert.False(nested.TryGetProperty("problemCode", out _));

        using System.Diagnostics.Process current =
            System.Diagnostics.Process.GetCurrentProcess();
        var process = new OwnedProcessIdentity(
            Environment.ProcessId,
            current.StartTime.ToUniversalTime(),
            Environment.ProcessPath
                ?? throw new InvalidOperationException("The test process path is unavailable."));
        DateTimeOffset observedAtUtc = json.RootElement
            .GetProperty("observedAtUtc")
            .GetDateTimeOffset();
        LoadedModsStatusReport report = Assert.IsType<LoadedModsStatusReport>(
            AlwaysOnStatusReader.Read(
                statusPath,
                launchId,
                process,
                observedAtUtc).LoadedMods);
        Assert.Equal("ready", report.State);
        Assert.Single(report.Mods);
    }

    [Fact]
    public void ReaderRejectsAnOversizedStatusFileBeforeDeserialization()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = Path.Combine(temporary.Path, "always-on.json");
        File.WriteAllText(
            statusPath,
            "{\"padding\":\"" + new string(
                'x',
                AlwaysOnStatusReader.MaximumStatusBytes) + "\"}");

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("invalid", report.State);
        Assert.Null(report.LoadedMods);
    }

    private static LoadedModsStatusMarker ReadyMarker() =>
        new(
            LoadedModsContract.SchemaVersion,
            CapturedAt,
            [
                new LoadedModEntry("Example.Mod", "1.0.0", false),
                AlwaysOn(),
            ],
            ProblemCode: null);

    private static LoadedModEntry AlwaysOn() =>
        new(LoadedModsContract.AlwaysOnUniqueId, "0.6.1", IsContentPack: false);

    private static LoadedModEntry[] TooManyMods() =>
        Enumerable.Range(0, LoadedModsContract.MaximumEntries)
            .Select(index => new LoadedModEntry($"Example.Mod{index:D3}", "1.0.0", false))
            .Append(AlwaysOn())
            .OrderBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static OwnedProcessIdentity Process() =>
        new(4242, StartedAt, @"E:\Games\StardewModdingAPI.exe");

    private static string WriteMarker(
        TemporaryDirectory temporary,
        LoadedModsStatusMarker? loadedMods)
    {
        string path = Path.Combine(temporary.Path, "always-on.json");
        var marker = new AlwaysOnStatusMarker(
            1,
            "launch-1",
            Process().ProcessId,
            Process().StartTimeUtc,
            "active",
            600,
            IsActive: false,
            PauseWhenOutOfFocus: false,
            ObservedAt,
            LoadedMods: loadedMods);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
        return path;
    }
}

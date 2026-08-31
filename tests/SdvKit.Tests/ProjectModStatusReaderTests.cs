using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectModStatusReaderTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 30, 20, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ObservedAt = StartedAt.AddSeconds(10);

    private const string BuildIdentity =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void MissingProjectModMarkerIsPendingForExpectedTarget()
    {
        using TemporaryDirectory temporary = new();
        ProjectModLaunchState expected = Expected();
        string statusPath = WriteMarker(temporary, projectMod: null);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt,
            expectedProjectMod: expected);

        Assert.Equal("active", report.State);
        ProjectModStatusReport projectMod = Assert.IsType<ProjectModStatusReport>(
            report.ProjectMod);
        Assert.Equal("pending", projectMod.State);
        Assert.Equal(ProjectModContract.WaitingForGameLaunchPhase, projectMod.Phase);
        Assert.Equal(expected.UniqueId, projectMod.ExpectedUniqueId);
        Assert.Equal(expected.Version, projectMod.ExpectedVersion);
        Assert.Equal(expected.BuildIdentity, projectMod.BuildIdentity);
        Assert.Null(projectMod.LoadConfirmed);
    }

    [Fact]
    public void ExactLoadedProjectModMarkerIsReady()
    {
        using TemporaryDirectory temporary = new();
        ProjectModLaunchState expected = Expected();
        ProjectModStatusMarker marker = Loaded(expected);
        string statusPath = WriteMarker(temporary, marker);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt,
            expectedProjectMod: expected);

        ProjectModStatusReport projectMod = Assert.IsType<ProjectModStatusReport>(
            report.ProjectMod);
        Assert.Equal("ready", projectMod.State);
        Assert.Equal(ProjectModContract.LoadedPhase, projectMod.Phase);
        Assert.Equal(expected.UniqueId, projectMod.LoadedUniqueId);
        Assert.Equal(expected.Version, projectMod.LoadedVersion);
        Assert.Equal(expected.BuildIdentity, projectMod.BuildIdentity);
        Assert.True(projectMod.LoadConfirmed);
    }

    [Fact]
    public void FailedProjectModMarkerIsReportedWithItsMessage()
    {
        using TemporaryDirectory temporary = new();
        ProjectModLaunchState expected = Expected();
        var marker = new ProjectModStatusMarker(
            ProjectModContract.SchemaVersion,
            ProjectModContract.FailedPhase,
            expected.UniqueId,
            expected.Version,
            LoadedUniqueId: null,
            LoadedVersion: null,
            expected.BuildIdentity,
            LoadConfirmed: false,
            "SMAPI did not expose the expected manifest.");
        string statusPath = WriteMarker(temporary, marker);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt,
            expectedProjectMod: expected);

        ProjectModStatusReport projectMod = Assert.IsType<ProjectModStatusReport>(
            report.ProjectMod);
        Assert.Equal("failed", projectMod.State);
        Assert.False(projectMod.LoadConfirmed);
        Assert.Equal("SMAPI did not expose the expected manifest.", projectMod.Message);
    }

    [Theory]
    [InlineData("uniqueId")]
    [InlineData("version")]
    [InlineData("buildIdentity")]
    public void ExpectedProjectModIdentityDriftIsMismatch(string field)
    {
        using TemporaryDirectory temporary = new();
        ProjectModLaunchState expected = Expected();
        ProjectModStatusMarker marker = field switch
        {
            "uniqueId" => Loaded(expected) with
            {
                ExpectedUniqueId = "Example.DifferentMod",
            },
            "version" => Loaded(expected) with
            {
                ExpectedVersion = "2.0.0",
            },
            "buildIdentity" => Loaded(expected) with
            {
                BuildIdentity =
                    "sha256:2222222222222222222222222222222222222222222222222222222222222222",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        string statusPath = WriteMarker(temporary, marker);

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt,
            expectedProjectMod: expected);

        Assert.Equal("mismatch", report.ProjectMod?.State);
    }

    [Fact]
    public void ProjectModMarkerWithoutExpectedTargetIsUnexpected()
    {
        using TemporaryDirectory temporary = new();
        string statusPath = WriteMarker(temporary, Loaded(Expected()));

        AlwaysOnStatusReport report = AlwaysOnStatusReader.Read(
            statusPath,
            "launch-1",
            Process(),
            ObservedAt);

        Assert.Equal("unexpected", report.ProjectMod?.State);
    }

    private static ProjectModLaunchState Expected() =>
        new("Example.ProjectMod", "1.2.3", BuildIdentity);

    private static ProjectModStatusMarker Loaded(ProjectModLaunchState expected) =>
        new(
            ProjectModContract.SchemaVersion,
            ProjectModContract.LoadedPhase,
            expected.UniqueId,
            expected.Version,
            expected.UniqueId,
            expected.Version,
            expected.BuildIdentity,
            LoadConfirmed: true,
            "Exact manifest identity and version are loaded.");

    private static OwnedProcessIdentity Process() =>
        new(4242, StartedAt, @"E:\Games\StardewModdingAPI.exe");

    private static string WriteMarker(
        TemporaryDirectory temporary,
        ProjectModStatusMarker? projectMod)
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
            ProjectMod: projectMod);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase));
        return path;
    }
}

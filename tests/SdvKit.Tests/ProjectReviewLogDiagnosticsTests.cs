using System.Text.Json;
using ModelContextProtocol.Protocol;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    [Fact]
    public async Task OwnedWarningsAndExceptionsHaveCliServiceAndMcpParity()
    {
        using TemporaryDirectory temporary = new();
        PreparedReview review = LogReview(temporary);
        WriteLog(review.Reader, """
            [08:00:01 INFO  SMAPI] Loading mods...
            [08:00:02 WARN  Target] Known warning: relative/config.json is invalid.
            [08:00:03 INFO  SMAPI] Mods loaded and ready!
            [08:00:04 ERROR Target] System.InvalidOperationException: known probe failure
               at Probe.ModEntry.Run() in src/ModEntry.cs:line 42
               at Probe.ModEntry.Private() in C:\Users\private-user\repo\ModEntry.cs:line 99
            ENV_TOKEN=secret-do-not-return
            an unrelated private conversation
            [08:00:05 WARN  SMAPI] Zulu.Target and Alpha.Missing share this failure
            [08:00:06 ERROR Other Mod] unrelated crash
            [08:00:07 INFO  Target] ordinary info must not be returned
            """);
        ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target");
        Assert.Equal("ready", result.State);
        Assert.Equal(3, result.Counts!.Matching);
        Assert.Equal("loading", result.Diagnostics[0].Phase);
        Assert.Equal("runtime", result.Diagnostics[1].Phase);
        Assert.Contains("src/ModEntry.cs:line 42", string.Join('\n', result.Diagnostics[1].Lines));
        Assert.Equal(3, result.Diagnostics[1].WithheldLines);
        Assert.Equal("sharedMention", result.Diagnostics[2].Attribution);
        Assert.Equal("[message context withheld]", Assert.Single(result.Diagnostics[2].Lines));
        await using ClientHarness harness = await ClientHarness.StartAsync(review.Reader, false);
        JsonElement actual = AssertSuccessfulJson(await harness.Client.CallToolAsync(
            ProjectReviewMcpLogTools.ToolName, Args(("modId", "Zulu.Target")), cancellationToken: harness.Token));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(result, LiveLabJsonOptions.CamelCase), actual));
        string json = actual.GetRawText();
        foreach (string excluded in new[] { "private-user", "secret-do-not-return", "conversation", "unrelated crash", "ordinary info", temporary.Path })
        {
            Assert.DoesNotContain(excluded, json, StringComparison.Ordinal);
        }
        Assert.Equal("loaded", FindMod(await CallMods(harness, Args()), "Zulu.Target").GetProperty("loadStatus").GetString());
    }

    [Theory]
    [InlineData("missing", "reviewLogUnavailable")]
    [InlineData("rotated", "reviewLogUnavailable")]
    [InlineData("replaced", "reviewLogIdentityMismatch")]
    [InlineData("stale", "reviewLogStale")]
    [InlineData("hardlink", "reviewLogPathInvalid")]
    public void OwnedLogRejectsUnavailableOrUnboundSource(string mutation, string error)
    {
        using TemporaryDirectory temporary = new();
        PreparedReview review = LogReview(temporary);
        string path = WriteLog(review.Reader, "[08:00:04 WARN  Target] current");
        if (mutation == "missing") File.Delete(path);
        if (mutation == "rotated") File.Move(path, path + ".old");
        if (mutation == "replaced") File.WriteAllText(path, "[08:00:04 WARN  Target] foreign\n");
        if (mutation == "stale") File.SetLastWriteTimeUtc(path, StartedAt.AddDays(-1).UtcDateTime);
        if (mutation == "hardlink")
        {
            Assert.True(CreateHardLink(path + ".link", path, IntPtr.Zero));
        }
        ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target");
        Assert.Equal(error, result.ErrorCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LogQueriesRejectUnselectedModStaleStatusAndMissingOwnership()
    {
        using TemporaryDirectory temporary = new();
        PreparedReview review = LogReview(temporary);
        WriteLog(review.Reader, "[08:00:04 WARN  Target] current");
        Assert.Equal("reviewModNotSelected", ProjectReviewLogDiagnostics.Execute(review.Reader, "Other.Mod").ErrorCode);
        File.Delete(LiveLabPaths.Resolve(temporary.Path).StatusPath);
        Assert.NotEqual("ready", ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target").State);
        File.Delete(review.Staging.OwnershipPath);
        Assert.Empty(ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target").Diagnostics);
    }

    [Fact]
    public void ParserDistinguishesAmbiguityNoMatchesBoundsAndContinuation()
    {
        using TemporaryDirectory temporary = new();
        var manifests = CompleteArtifacts(temporary.Path).Select(a => a.Manifest).ToArray();
        ProjectReviewManifest target = manifests[0];
        var duplicate = target with { UniqueId = "Other.Mod" };
        var ambiguous = ProjectReviewLogDiagnostics.Parse("[08:00:01 WARN  Target] warning\n", target, [target, duplicate], 20);
        Assert.Equal("ambiguousLogger", Assert.Single(ambiguous.Diagnostics).Attribution);
        var empty = ProjectReviewLogDiagnostics.Parse("[08:00:01 WARN  Other] unrelated\n", target, manifests, 20);
        Assert.Empty(empty.Diagnostics);
        Assert.Equal(1, empty.Total);
        string log = string.Join('\n', Enumerable.Range(0, 110).Select(i => $"[08:00:01 WARN  Target] warning {i}"));
        var bounded = ProjectReviewLogDiagnostics.Parse(log, target, manifests, 2);
        Assert.Equal(110, bounded.Matching);
        Assert.Equal("warning 108", bounded.Diagnostics[0].Lines[0]);
        string exception = "[08:00:02 ERROR Target] System.Exception: " + new string('x', 2000) + "\n"
            + string.Join('\n', Enumerable.Repeat(" at Probe.Method()", 40));
        var longEntry = Assert.Single(ProjectReviewLogDiagnostics.Parse(exception, target, manifests, 20).Diagnostics);
        Assert.True(longEntry.Truncated);
        Assert.Equal(32, longEntry.Lines.Count);
        Assert.Equal(1024, longEntry.Lines[0].Length);
    }

    [Fact]
    public void ReaderReportsTailBoundsAndDoesNotReturnPartialLine()
    {
        using TemporaryDirectory temporary = new();
        PreparedReview review = LogReview(temporary);
        string path = WriteLog(review.Reader, new string('x', OwnedReviewLogReader.MaximumBytes)
            + "\n[08:00:03 WARN  Target] tail warning");
        File.AppendAllText(path, "[08:00:04 ERROR Target] incomplete private");
        ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target");
        Assert.Equal("ready", result.State);
        Assert.True(result.Source!.ScanTruncated);
        Assert.True(result.Source.IncompleteLineWithheld);
        Assert.False(result.Counts!.TotalIsExact);
        Assert.Equal("tail warning", Assert.Single(result.Diagnostics).Lines[0]);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("farmhand")]
    public async Task OwnedLogReturnsOnlyFixedNetworkRole(string role)
    {
        using TemporaryDirectory temporary = new();
        PreparedNetwork network = PrepareNetwork(temporary, NetworkArtifacts(temporary.Path),
            ReadyLoadedMods(new LoadedModEntry("SDVKit.AlwaysOn", "0.7.0", false)),
            ReadyLoadedMods(new LoadedModEntry("SDVKit.AlwaysOn", "0.7.0", false)));
        WriteLog(network.HostReader, "[08:00:04 WARN  Target] host-only warning");
        WriteLog(network.FarmhandReader, "[08:00:04 WARN  Target] farmhand-only warning");
        ProjectReviewMcpRuntimeReader reader = role == "host" ? network.HostReader : network.FarmhandReader;
        await using ClientHarness harness = await ClientHarness.StartAsync(reader, false);
        JsonElement result = AssertSuccessfulJson(await harness.Client.CallToolAsync(ProjectReviewMcpLogTools.ToolName,
            Args(("modId", "Nana.Target")), cancellationToken: harness.Token));
        Assert.Equal(role, result.GetProperty("role").GetString());
        Assert.Contains(role + "-only warning", result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain((role == "host" ? "farmhand" : "host") + "-only", result.GetRawText(), StringComparison.Ordinal);
        CallToolResult invalid = await harness.Client.CallToolAsync(ProjectReviewMcpLogTools.ToolName,
            Args(("modId", "Nana.Target"), ("role", role)), cancellationToken: harness.Token);
        Assert.True(invalid.IsError);
    }

    private static PreparedReview LogReview(TemporaryDirectory temporary) => PrepareSingle(temporary,
        CompleteArtifacts(temporary.Path), ReadyLoadedMods(new LoadedModEntry("Zulu.Target", "1.0.0", false),
            new LoadedModEntry("SDVKit.AlwaysOn", "0.7.0", false)));

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public void LinkedLogCannotReadOutsideTheOwnedProfile(string kind)
    {
        using TemporaryDirectory temporary = new();
        using TemporaryDirectory outside = new();
        PreparedReview review = LogReview(temporary);
        string path = WriteLog(review.Reader, "[08:00:04 WARN  Target] current");
        string foreign = outside.WriteFile("SMAPI-latest.txt", "private outside sentinel");
        File.Delete(path);
        string directory = Path.GetDirectoryName(path)!;
        try
        {
            if (kind == "file") File.CreateSymbolicLink(path, foreign);
            else
            {
                Directory.Delete(directory);
                Directory.CreateSymbolicLink(directory, outside.Path);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        ReviewLogDiagnosticsResult result = ProjectReviewLogDiagnostics.Execute(review.Reader, "Zulu.Target");
        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("private outside sentinel", File.ReadAllText(foreign));
    }

    [Theory]
    [InlineData("System.Exception: {\"token\": \"private-value\"}")]
    [InlineData("System.Exception: api_key=private-value")]
    [InlineData("System.Exception: Bearer private-value")]
    [InlineData("System.Exception: PID=1234")]
    [InlineData("System.Exception: https://user:private-value@host/")]
    public void KnownPrivateContextIsExplicitlyWithheld(string message)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewManifest target = CompleteArtifacts(temporary.Path)[0].Manifest;
        ReviewLogDiagnostic entry = Assert.Single(ProjectReviewLogDiagnostics.Parse(
            "[08:00:01 ERROR Target] " + message + "\n at Probe.Run() in src/Entry.cs:line 5\n",
            target, [target], 20).Diagnostics);
        Assert.Equal(1, entry.WithheldLines);
        Assert.Equal(" at Probe.Run() in src/Entry.cs:line 5", Assert.Single(entry.Lines));
    }

    [Theory]
    [InlineData("src/Entry.cs")]
    [InlineData("./src/Entry.cs")]
    [InlineData("../src/Entry.cs")]
    [InlineData("../../src/Entry.cs")]
    public void RelativeExceptionLocationsSurvive(string path)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewManifest target = CompleteArtifacts(temporary.Path)[0].Manifest;
        string line = $" at Probe.Run() in {path}:line 5";
        ReviewLogDiagnostic entry = Assert.Single(ProjectReviewLogDiagnostics.Parse(
            "[08:00:01 ERROR Target] System.Exception: failure\n" + line + "\n", target, [target], 20).Diagnostics);
        Assert.Contains(line, entry.Lines);
        Assert.Equal(0, entry.WithheldLines);
    }

    [Theory]
    [InlineData("/home/private/src/Entry.cs")]
    [InlineData("C:\\Users\\private\\Entry.cs")]
    [InlineData("\\\\private\\share\\Entry.cs")]
    public void AbsoluteExceptionLocationsAreWithheld(string path)
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewManifest target = CompleteArtifacts(temporary.Path)[0].Manifest;
        ReviewLogDiagnostic entry = Assert.Single(ProjectReviewLogDiagnostics.Parse(
            $"[08:00:01 ERROR Target] System.Exception: failure\n at Probe.Run() in {path}:line 5\n", target, [target], 20).Diagnostics);
        Assert.Equal(1, entry.WithheldLines);
        Assert.DoesNotContain("private", string.Join('\n', entry.Lines).Replace("[private path withheld]", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderIdentityUsesTheSameCaseInsensitiveBindingAsStaging()
    {
        using TemporaryDirectory temporary = new();
        ProjectReviewManifest target = CompleteArtifacts(temporary.Path)[0].Manifest with
        {
            Kind = "contentPack",
            ContentPackFor = "pathoschild.contentpatcher",
        };
        ProjectReviewManifest provider = target with
        {
            Name = "Content Patcher",
            UniqueId = "Pathoschild.ContentPatcher",
            ContentPackFor = null,
        };
        ReviewLogDiagnostic entry = Assert.Single(ProjectReviewLogDiagnostics.Parse(
            "[08:00:01 ERROR Content Patcher] Cannot load Target: invalid field\n", target, [target, provider], 20).Diagnostics);
        Assert.Equal("sharedMention", entry.Attribution);
        Assert.Equal("Cannot load Target: invalid field", Assert.Single(entry.Lines));
    }

    [Theory]
    [InlineData("--mod Zulu.Target --json", true)]
    [InlineData("--mod Zulu.Target --topology network-2 --role farmhand --json", true)]
    [InlineData("--mod Zulu.Target --topology network-2 --json", false)]
    [InlineData("--mod Zulu.Target --role host --json", false)]
    [InlineData("--mod Zulu.Target --limit 0 --json", false)]
    [InlineData("--mod Zulu.Target --limit 101 --json", false)]
    [InlineData("--mod Zulu.Target --json --json", false)]
    [InlineData("--mod Zulu.Target --path private --json", false)]
    [InlineData("--mod ../private --json", false)]
    [InlineData("--mod Zulu.Target", false)]
    public void DiagnosticsCliHasClosedRoleBoundArguments(string options, bool expected)
    {
        Assert.Equal(expected, CliApplication.TryParseReviewDiagnostics(
            ("project review diagnostics " + options).Split(' '), out _, out _, out _, out _));
    }

    private static string WriteLog(ProjectReviewMcpRuntimeReader reader, string text)
    {
        ProjectReviewMcpVerifiedContext context = reader.ReadContext().Context!;
        LiveLabPaths paths = LiveLabPaths.Resolve(reader.ProjectRoot);
        if (reader.Role is not null) paths = LiveLabPaths.ResolveNetworkRole(paths, reader.Role);
        string path = Path.Combine(paths.StardewDataPath, "ErrorLogs", "SMAPI-latest.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"[08:00:00 INFO  SDVKit AlwaysOn] SDVKit AlwaysOn activated for isolated lab launch '{context.State.LaunchId}'.\n" + text + "\n");
        return path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr attributes);
}

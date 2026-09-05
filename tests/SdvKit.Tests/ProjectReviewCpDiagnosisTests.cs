using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    private const string CpSummary = """
        [08:00:02 DEBUG Content Patcher]
        == Global tokens ==
        FarmName | private unrelated global context
        == Content patches ==
        (Filtered to content pack ID: Test.Pack.)
        (Filtered to asset name: Data/Objects.)
        Test Pack:
           Patches:
              loaded  | conditions | applied | priority | name + details
              [X]     | [X]        | [X]     | Default  | Success (Data/Objects)
              [X]     | [ ]        | [ ]     | Default  | Unmet // conditions don't match: Season
              [ ]     | [ ]        | [ ]     | Default  | Invalid // invalid token: Missing
              [X]     | [X]        | [ ]     | Default  | NotLoaded
           Current changes:
              asset name | changes
              Data/Objects | changed entries
        """;
    private static string CpMarker(string marker) => $"[08:00:01 DEBUG Content Patcher] \n   The token string is valid and ready. Parsed value: \"{marker}\"\n";
    private static CpResponse CpInterpret(string middle, string prefix = "", string suffix = "", bool parse = false) =>
        ProjectReviewCpDiagnosis.InterpretWindow(prefix + CpMarker("begin") + middle.Replace("Patcher]\n", "Patcher] \n", StringComparison.Ordinal) + "\n" + CpMarker("end") + suffix,
            "Content Patcher", "Test.Pack", "Data/Objects", "{{Missing}}", "begin", "end", parse, [], DateTimeOffset.UtcNow);

    [Fact]
    public void CpInformationalReplyPreservesSeparatePatchStatesAndOmitsGlobalContext()
    {
        var result = CpInterpret(CpSummary, "[08:00:00 INFO  Other] unrelated\n", "[08:00:03 INFO  Other] unrelated\n");
        Assert.Equal("ready", result.State);
        Assert.Equal(4, result.Patches.Count);
        Assert.True(result.Patches[0].Applied);
        Assert.False(result.Patches[1].ConditionsMatch);
        Assert.False(result.Patches[2].LoadedAndEnabled);
        Assert.True(result.Patches[3].LoadedAndEnabled && result.Patches[3].ConditionsMatch);
        Assert.False(result.Patches[3].Applied);
        Assert.Equal("08:00:02", result.LogTime);
        Assert.DoesNotContain("global context", string.Join('\n', result.Messages));
        Assert.DoesNotContain("unrelated", string.Join('\n', result.Messages));
    }

    [Theory]
    [InlineData("[08:00:02 INFO  Content Patcher] other CP command")]
    [InlineData("[08:00:02 DEBUG Content Patcher]\nThe token string is valid and ready. Parsed value: \"other-begin\"")]
    public void CpOverlappingProviderOutputIsRejected(string extra)
    {
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", CpInterpret(CpSummary + "\n" + extra).ErrorCode);
    }

    [Fact]
    public void CpUnrelatedEntriesBetweenRepliesAreOmittedAndInterruptedReplyIsIncomplete()
    {
        var result = CpInterpret("[08:00:02 INFO  Other] private unrelated\n" + CpSummary);
        Assert.Equal("ready", result.State);
        Assert.DoesNotContain("private unrelated", string.Join('\n', result.Messages));
        string interrupted = CpSummary.Replace("   Current changes:", "[08:00:02 INFO  Other] interruption\n   Current changes:", StringComparison.Ordinal);
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", CpInterpret(interrupted).ErrorCode);
    }

    [Fact]
    public void CpEarlySummaryInterruptionCannotMasqueradeAsAnEmptySummary()
    {
        string interrupted = CpSummary.Replace("Test Pack:", "[08:00:02 INFO  Other] interruption\nTest Pack:", StringComparison.Ordinal);
        var result = CpInterpret(interrupted);
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", result.ErrorCode);
        Assert.Empty(result.Patches);
        string empty = CpSummary[..CpSummary.IndexOf("Test Pack:", StringComparison.Ordinal)];
        Assert.Equal("ready", CpInterpret(empty).State);
        Assert.Empty(CpInterpret(empty).Patches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CpParseRequiresItsActualResultAfterTheResultHeading(bool interrupted)
    {
        const string parse = "[08:00:02 DEBUG Content Patcher] \nMetadata\n   raw value:   {{Missing}}\nDiagnostic state\nResult\n";
        string middle = parse + (interrupted ? "[08:00:02 INFO  Other] interruption\nThe token string is invalid or unready.\n" : "");
        Assert.Equal(interrupted ? "cpResponseUncorrelatedOrOverlapping" : "cpOutputUnsupported", CpInterpret(middle, parse: true).ErrorCode);
    }

    [Fact]
    public void CpWrongSelectionUnknownOutputAndMissingMarkersAreExplicit()
    {
        Assert.Equal("cpOutputUnsupported", CpInterpret(CpSummary.Replace("Test.Pack", "Other.Pack", StringComparison.Ordinal)).ErrorCode);
        Assert.Equal("cpOutputUnsupported", CpInterpret("[08:00:02 DEBUG Content Patcher] new format").ErrorCode);
        var missing = ProjectReviewCpDiagnosis.InterpretWindow(CpSummary, "Content Patcher", "Test.Pack", null, null,
            "begin", "end", false, [], DateTimeOffset.UtcNow);
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", missing.ErrorCode);
    }

    [Fact]
    public void CpParsePreservesInvalidTokenAndRelativeLocationsWithDisclosureBounds()
    {
        var result = CpInterpret("""
            [08:00:02 DEBUG Content Patcher]
            Metadata
               raw value:   {{Missing}}
            Diagnostic state
               valid: False
               invalid tokens: Missing
               context: ../assets/file.json
               source: C:\Users\private\file.json
               api_key=do-not-return
            Result
            The token string is invalid or unready.
            """, parse: true);
        Assert.Equal("ready", result.State);
        Assert.Equal(2, result.WithheldLines);
        Assert.Contains("../assets/file.json", string.Join('\n', result.Messages));
        Assert.DoesNotContain("private\\", string.Join('\n', result.Messages));
        Assert.DoesNotContain("do-not-return", string.Join('\n', result.Messages));
        Assert.Empty(result.Patches);
        Assert.Equal("ready", CpInterpret("[08:00:02 ERROR Content Patcher] Can't parse that token value: unclosed token", parse: true).State);
    }

    [Theory]
    [InlineData("asset", null, null)]
    [InlineData("Test.Pack", "Data/Objects\" full", null)]
    [InlineData("Test.Pack", "Data/Objects\nexit", null)]
    [InlineData("Test.Pack", "../private", null)]
    [InlineData("Test.Pack", "Data/furniture.fr-FR", null)]
    [InlineData("Test.Pack", "Data//Objects", null)]
    [InlineData("Test.Pack", "Data/ Objects", null)]
    [InlineData("Test.Pack", null, "value\" Other.Pack")]
    [InlineData("Test.Pack", null, "value\\\"")]
    [InlineData("Test.Pack", null, "value;exit")]
    public void CpUnsafeFiltersNeverBecomeCommands(string pack, string? asset, string? parse) =>
        Assert.False(ProjectReviewCpDiagnosis.ValidArguments(pack, ProjectReviewCpDiagnosis.ProviderId, asset, parse));

    [Fact]
    public void CpCliRequiresExplicitProviderAndRejectsDuplicateOrRoleArguments()
    {
        string[] valid = ["project", "review", "cp-diagnose", "--pack", "Test.Pack", "--provider", ProjectReviewCpDiagnosis.ProviderId, "--json"];
        Assert.True(CliApplication.TryParseCpDiagnosis(valid, out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpDiagnosis(valid.Concat(["--json"]).ToArray(), out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpDiagnosis(valid.Concat(["--role", "host"]).ToArray(), out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpDiagnosis(["project", "review", "cp-diagnose", "--pack", "Test.Pack", "--json"], out _, out _, out _, out _));
    }

    [Fact]
    public void CpWindowRejectsRotationReplacementAndOverflow()
    {
        var before = new OwnedReviewLog("prefix\n", 7, 7, false, false, DateTimeOffset.UtcNow, "file1");
        Assert.Equal("new\n", ProjectReviewCpDiagnosis.WindowDelta(before, before with { Text = "prefix\nnew\n", TotalBytes = 11 }));
        foreach (var changed in new[] { before with { FileIdentity = "file2" }, before with { Text = "changed" }, before with { ScanTruncated = true }, before with { TotalBytes = 2 } })
            Assert.Throws<InvalidDataException>(() => ProjectReviewCpDiagnosis.WindowDelta(before, changed));
    }

    [Theory]
    [InlineData("2.9.1", "ready", "normal")]
    [InlineData("9.0.0", "unsupported", "normal")]
    [InlineData("2.9.1", "incomplete", "timeout")]
    [InlineData("2.9.1", "incomplete", "delivery")]
    [InlineData("2.9.1", "unavailable", "busy")]
    public void CpServiceChecksVersionAndCorrelatesDeliveredCommands(string version, string expected, string mode)
    {
        using TemporaryDirectory temporary = new();
        var pack = ProjectReviewStagerTests.Artifact(temporary.Path, "Test Pack", ProjectReviewArtifactRole.Target, "Test.Pack", contentPackFor: ProjectReviewCpDiagnosis.ProviderId, kind: ProjectInspectionReport.ContentPack);
        var provider = ProjectReviewStagerTests.Artifact(temporary.Path, "ContentPatcher", ProjectReviewArtifactRole.Companion, ProjectReviewCpDiagnosis.ProviderId, version: version);
        var review = PrepareSingle(temporary, [pack, provider], ReadyLoadedMods(
            new LoadedModEntry("Test.Pack", "1.0.0", true), new LoadedModEntry(ProjectReviewCpDiagnosis.ProviderId, version, false), new LoadedModEntry("SDVKit.AlwaysOn", "0.7.0", false)));
        var verified = review.Reader.ReadContext();
        Assert.True(verified.Succeeded, verified.ErrorCode + ": " + verified.ErrorMessage);
        string log = WriteLog(review.Reader, "");
        var commands = new List<string>();
        LiveLabCommandResult Send(string command)
        {
            commands.Add(command);
            if (command.StartsWith("patch summary", StringComparison.Ordinal) && mode is "timeout" or "delivery")
                return new(mode == "delivery" ? 3 : 0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, mode == "delivery" ? null : true, [], []));
            string message = command.StartsWith("patch summary", StringComparison.Ordinal) ? CpSummary.Replace("Patcher]\n", "Patcher] \n", StringComparison.Ordinal) + "\n" : CpMarker(command.Split('"')[1]);
            File.AppendAllText(log, message.Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal));
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }
        using var heldLock = mode == "busy" ? ProjectReviewActionLock.TryAcquire(LiveLabPaths.Resolve(temporary.Path).RuntimePath) : null;
        var result = ProjectReviewCpDiagnosis.Execute(review.Reader, "Test.Pack", ProjectReviewCpDiagnosis.ProviderId, "Data/Objects", null, Send, TimeSpan.FromMilliseconds(150));
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == "ready" ? 3 : expected == "incomplete" ? 2 : 0, commands.Count);
        if (expected == "ready") Assert.Equal(4, result.Summary!.Patches.Count);
        if (mode == "timeout")
        {
            Assert.True(result.Summary!.CommandWritten);
            Assert.Equal("cpResponseTimedOut", result.ErrorCode);
        }
        if (mode == "delivery") Assert.True(result.Summary!.CommandMayHaveBeenWritten);
    }

    [Fact]
    public void CpSensitiveTableValuesAreWithheldWhileOrdinaryTokensSurvive()
    {
        var result = CpInterpret(CpSummary + "\n   Local tokens:\n      ApiKey   | [X] do-not-return\n      Password | [X] hidden\n      Variant  | [X] summer");
        Assert.Equal("ready", result.State);
        Assert.Equal(2, result.WithheldLines);
        Assert.DoesNotContain("do-not-return", string.Join('\n', result.Messages));
        Assert.Contains("summer", string.Join('\n', result.Messages));
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Password")]
    [InlineData("Token")]
    public void CpSensitiveParseWithholdsTheSeparateResult(string token)
    {
        string middle = "[08:00:02 DEBUG Content Patcher] \nMetadata\n   raw value:   {{" + token
            + "}}\n   tokens used: " + token + "\nDiagnostic state\nResult\nThe token string is valid and ready. Parsed value: \"do-not-return\"\n";
        var result = ProjectReviewCpDiagnosis.InterpretWindow(CpMarker("begin") + middle + CpMarker("end"), "Content Patcher",
            "Test.Pack", null, "{{" + token + "}}", "begin", "end", true, [], DateTimeOffset.UtcNow);
        Assert.Equal("cpParsePrivateContextWithheld", result.ErrorCode);
        Assert.DoesNotContain("do-not-return", string.Join('\n', result.Messages));
    }
}

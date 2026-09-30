using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed partial class ProjectReviewMcpDiagnosticsTests
{
    // Exact headings/row grammar from the pinned CP 2.9.1 DumpCommand, not summary.
    private const string CpAppliedHeading = "Here are the active patches grouped by their current target value. Within each group, patches are listed in the expected apply order and the checkbox indicates whether each patch is currently applied. See `patch summary` for more info about each patch, including reasons it may not be applied.";
    private const string CpOrderHeading = "Here's the global patch definition order across all loaded content packs, which affects the order that patches are applied. The 'order' column is the patch's global position in the order; the 'index path' column is Content Patcher's internal hierarchical definition order.";
    private static readonly string CpAppliedDump = CpAppliedHeading + "\n\nData/Objects\n------------\n   [X] Load Other Pack > Base\n   [X] EditData Test Pack > Preferred\n   [ ] EditData Other Pack > Unexpected\n\nData/Crops\n----------\n   [X] EditData Private Pack > Unselected";
    private static readonly string CpOrderDump = CpOrderHeading + "\n\n   order   index path   patch\n   -----   ----------   -----\n   1       0 > 0        Test Pack > Preferred\n   2       1 > 0        Other Pack > Base\n   3       1 > 1        Other Pack > Unexpected\n   4       2 > 0        Private Pack > Unselected";
    private static CpResponse CpDumpInterpret(string message, string dump = "applied", string asset = "Data/Objects",
        IReadOnlyList<string>? paths = null, string level = "INFO  ") => ProjectReviewCpDiagnosis.InterpretWindow(
            CpMarker("begin") + $"[08:00:02 {level}Content Patcher] \n" + message + "\n" + CpMarker("end"),
            "Content Patcher", "Test.Pack", asset, null, "begin", "end", false, [], DateTimeOffset.UtcNow,
            dump: dump, appliedPaths: paths);
    private static readonly string[] CpSelectedPaths = ["Other Pack > Base", "Test Pack > Preferred", "Other Pack > Unexpected"];

    [Fact]
    public void CpAssetOrderPreservesProviderApplyAndDefinitionOrderWithoutUnselectedContext()
    {
        var applied = CpDumpInterpret(CpAppliedDump);
        Assert.Equal("ready", applied.State);
        Assert.Equal("   [X] Load Other Pack > Base", applied.Messages[3]);
        Assert.Equal("   [ ] EditData Other Pack > Unexpected", applied.Messages[^1]);
        Assert.DoesNotContain("Unselected", string.Join('\n', applied.Messages));
        var order = CpDumpInterpret(CpOrderDump, "order", paths: CpSelectedPaths);
        Assert.Equal("ready", order.State);
        Assert.Equal("   1       0 > 0        Test Pack > Preferred", order.Messages[3]);
        Assert.Equal("   3       1 > 1        Other Pack > Unexpected", order.Messages[^1]);
        Assert.DoesNotContain("Private Pack", string.Join('\n', order.Messages));
        Assert.Empty(order.Patches); // Summary booleans are not invented for dump rows.
    }

    [Fact]
    public void CpAssetOrderSupportsExplicitEmptyAndFilteredResults()
    {
        Assert.Equal("ready", CpDumpInterpret(CpAppliedHeading).State);
        var absent = CpDumpInterpret(CpAppliedDump, asset: "Data/Furniture");
        Assert.Equal("ready", absent.State);
        Assert.Equal(3, absent.Messages.Count);
        Assert.Equal("ready", CpDumpInterpret(CpOrderDump, "order", paths: []).State);
        Assert.Equal("cpOrderSelectionChanged", CpDumpInterpret(CpOrderDump, "order", paths: ["Missing Patch"]).ErrorCode);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("divider")]
    [InlineData("row")]
    [InlineData("emptyGroup")]
    [InlineData("duplicate")]
    [InlineData("position")]
    [InlineData("shortOrder")]
    [InlineData("level")]
    public void CpAssetOrderUnknownOrIncompleteGrammarIsRejected(string mode)
    {
        var result = mode switch
        {
            "header" => CpDumpInterpret(CpAppliedDump.Replace("Here are", "These are", StringComparison.Ordinal)),
            "divider" => CpDumpInterpret(CpAppliedDump.Replace("------------", "---", StringComparison.Ordinal)),
            "row" => CpDumpInterpret(CpAppliedDump.Replace("[X] Load", "[?] Load", StringComparison.Ordinal)),
            "emptyGroup" => CpDumpInterpret(CpAppliedHeading + "\n\nData/Objects\n------------"),
            "duplicate" => CpDumpInterpret(CpAppliedDump + "\n\nData/Objects\n------------\n   [X] EditData Duplicate"),
            "position" => CpDumpInterpret(CpOrderDump.Replace("   3       ", "   5       ", StringComparison.Ordinal), "order", paths: CpSelectedPaths),
            "shortOrder" => CpDumpInterpret(CpOrderHeading + "\n\n   order   index path   patch", "order", paths: []),
            _ => CpDumpInterpret(CpAppliedDump, level: "DEBUG "),
        };
        Assert.Equal("cpOutputUnsupported", result.ErrorCode);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void CpAssetOrderWithholdsPartialOrderForBoundsPrivacyAndInterruptedLogs()
    {
        var longLine = CpDumpInterpret(CpAppliedDump.Replace("Unselected", new string('a', 1025), StringComparison.Ordinal));
        Assert.Equal("cpOrderOutputTruncated", longLine.ErrorCode);
        Assert.True(longLine.Truncated);
        var manyLines = CpDumpInterpret(CpAppliedDump + string.Concat(Enumerable.Repeat("\n   [X] EditData Other > Another", 256)));
        Assert.Equal("cpOrderOutputTruncated", manyLines.ErrorCode);
        var secret = CpDumpInterpret(CpAppliedDump.Replace("Preferred", "api_key=private", StringComparison.Ordinal));
        Assert.Equal("cpOrderPrivateContextWithheld", secret.ErrorCode);
        Assert.Empty(secret.Messages);
        var interrupted = CpDumpInterpret(CpAppliedDump.Replace("   [X] Load", "[08:00:02 INFO  Other] interrupt\n   [X] Load", StringComparison.Ordinal));
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", interrupted.ErrorCode);
        var overlap = CpDumpInterpret(CpAppliedDump + "\n[08:00:02 INFO  Content Patcher] Another response");
        Assert.Equal("cpResponseUncorrelatedOrOverlapping", overlap.ErrorCode);
    }

    [Fact]
    public void CpCliOrderRequiresExplicitCanonicalAssetAndRejectsRepeatedFlags()
    {
        string[] args = ["project", "review", "cp-diagnose", "--pack", "Test.Pack", "--provider", ProjectReviewCpDiagnosis.ProviderId, "--order", "--json"];
        Assert.False(CliApplication.TryParseCpDiagnosis(args, out _, out _, out _, out _, out _));
        Assert.True(CliApplication.TryParseCpDiagnosis([.. args, "--asset", "Data/Objects"], out _, out _, out _, out _, out bool order));
        Assert.True(order);
        Assert.False(CliApplication.TryParseCpDiagnosis([.. args, "--asset", "Data/Objects", "--order"], out _, out _, out _, out _, out _));
        Assert.False(CliApplication.TryParseCpDiagnosis([.. args, "--asset", "Data/Objects.fr-FR"], out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("stale")]
    [InlineData("replacement")]
    [InlineData("summaryStale")]
    [InlineData("summaryReplacement")]
    [InlineData("version")]
    [InlineData("timeout")]
    public void CpAssetOrderServiceKeepsOwnedWindowVersionAndNoRetryChecks(string mode)
    {
        using TemporaryDirectory temporary = new();
        string version = mode == "version" ? "2.9.2" : "2.9.1";
        var pack = ProjectReviewStagerTests.Artifact(temporary.Path, "Test Pack", ProjectReviewArtifactRole.Target, "Test.Pack", contentPackFor: ProjectReviewCpDiagnosis.ProviderId, kind: ProjectInspectionReport.ContentPack);
        var provider = ProjectReviewStagerTests.Artifact(temporary.Path, "ContentPatcher", ProjectReviewArtifactRole.Companion, ProjectReviewCpDiagnosis.ProviderId, version: version);
        var review = PrepareSingle(temporary, [pack, provider], ReadyLoadedMods(
            new LoadedModEntry("Test.Pack", "1.0.0", true), new LoadedModEntry(ProjectReviewCpDiagnosis.ProviderId, version, false), new LoadedModEntry("SDVKit.AlwaysOn", "0.8.0", false)));
        string log = WriteLog(review.Reader, "");
        var commands = new List<string>();
        LiveLabCommandResult Send(string command)
        {
            commands.Add(command);
            if (command == "patch dump applied" && mode is "stale" or "replacement" or "timeout"
                || command.StartsWith("patch summary", StringComparison.Ordinal) && mode is "summaryStale" or "summaryReplacement")
            {
                if (mode is "stale" or "summaryStale") File.Delete(review.Reader.ReadContext().Context!.State.StatusPath);
                if (mode is "replacement" or "summaryReplacement") { File.Move(log, log + ".old"); File.WriteAllText(log, ""); }
                return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
            }
            string text = command switch
            {
                "patch dump applied" => "[08:00:02 INFO  ContentPatcher] \n" + CpAppliedDump + "\n",
                "patch dump order" => "[08:00:02 INFO  ContentPatcher] \n" + CpOrderDump + "\n",
                _ when command.StartsWith("patch summary", StringComparison.Ordinal) => CpSummary.Replace("Patcher]\n", "Patcher] \n", StringComparison.Ordinal).Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal) + "\n",
                _ => CpMarker(command.Split('"')[1]).Replace("Content Patcher", "ContentPatcher", StringComparison.Ordinal),
            };
            File.AppendAllText(log, text);
            return new(0, new ProjectReviewCommandReport(1, null, temporary.Path, "ready", null, true, [], []));
        }
        var result = ProjectReviewCpDiagnosis.Execute(review.Reader, "Test.Pack", ProjectReviewCpDiagnosis.ProviderId,
            "Data/Objects", null, Send, TimeSpan.FromMilliseconds(200), order: true);
        Assert.Equal(mode == "success" ? "ready" : mode == "version" ? "unsupported" : "incomplete", result.State);
        if (mode == "success")
        {
            Assert.Equal("Data/Objects", result.OrderAsset);
            Assert.Equal(9, commands.Count);
            Assert.Equal("ready", result.Order!.State);
            Assert.Equal("ready", result.Applied!.State);
            Assert.DoesNotContain(commands, c => c.StartsWith("sdvkit data", StringComparison.Ordinal));
        }
        else if (mode == "version") Assert.Empty(commands);
        else
        {
            Assert.Equal(mode.StartsWith("summary", StringComparison.Ordinal) ? 2 : 5, commands.Count);
            Assert.Equal(mode.StartsWith("summary", StringComparison.Ordinal) ? 0 : 1, commands.Count(c => c == "patch dump applied"));
            Assert.Null(result.Order);
        }
    }
}

using System.Text.Json;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewBundleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitReadyBundleFreezesSeparateExactMembersAndCleansUp(bool container)
    {
        using TemporaryDirectory temporary = new();
        var (root, pack, paths) = Bundle(temporary);
        if (container)
        {
            string code = Directory.CreateDirectory(Path.Combine(root, "Code")).FullName;
            File.Move(Path.Combine(root, "manifest.json"), Path.Combine(code, "manifest.json"));
            File.Move(Path.Combine(root, "Bundle.dll"), Path.Combine(code, "Bundle.dll"));
        }
        string before = ModBuildIdentity.ComputeFileSet(root);
        var prepared = ProjectModStager.PrepareReview(root, [], [pack], paths, NoDoctor);
        Assert.Null(prepared.Problem);
        Assert.Equal(2, prepared.Artifacts.Count);
        var target = prepared.Artifacts.Single(a => a.Role == ProjectReviewArtifactRole.Target);
        var member = prepared.Artifacts.Single(a => a.Role == ProjectReviewArtifactRole.ContentPack);
        Assert.Equal("Test.Bundle", target.Manifest.UniqueId);
        Assert.Equal("Test.BundlePack", member.Manifest.UniqueId);
        Assert.Equal(pack, member.SourceRoot);
        Assert.False(Directory.Exists(Path.Combine(target.PreparedPath, "Pack")));
        Assert.Equal(ModBuildIdentity.ComputeFileSet(pack), member.BuildIdentity);
        Assert.Equal(2, Directory.GetFiles(target.PreparedPath, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("reviewTargetTopologyUnsupported", ProjectModStager.StageReview(prepared.Artifacts, "network-2", paths).Problem?.Code);
        var staged = ProjectModStager.StageReview(prepared.Artifacts, paths);
        Assert.Null(staged.Problem);
        var retained = ProjectModStager.ReadReview(paths);
        Assert.Null(retained.Problem);
        Assert.Equal(2, retained.Staging!.Artifacts.Count);
        Assert.All(retained.Staging.Artifacts, a => Assert.Equal(a.BuildIdentity, ModBuildIdentity.ComputeFileSet(a.StagingPath)));
        Assert.Null(ProjectReviewService.ReviewSetRequestProblem(root, [], [pack], retained.Staging, null));
        var changed = ProjectReviewService.Execute("start", root, [], [], "single", paths.ProjectRoot, NoDoctor);
        Assert.Contains(((ProjectReviewReport)changed.Report).Problems, p => p.Code == "reviewSetMismatch");
        Assert.NotNull(ProjectModStager.ReadReview(paths).Staging);
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        Assert.True(ProjectModStager.RemoveReview(paths).Removed);
        Assert.Null(ProjectModStager.ReadReview(paths).Staging);
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Theory]
    [InlineData("unselected", "reviewReadyDirectoryInvalid")]
    [InlineData("extraManifest", "reviewBundleInvalid")]
    [InlineData("missingPack", "reviewBundleInvalid")]
    [InlineData("nestedPack", "reviewBundleInvalid")]
    [InlineData("duplicateSelection", "reviewBundleInvalid")]
    [InlineData("missingDll", "reviewReadyManifestInvalid")]
    [InlineData("escapedDll", "reviewBundleInvalid")]
    [InlineData("duplicateId", "reviewModIdentityCollision")]
    [InlineData("missingProvider", "reviewDependencyUnavailable")]
    [InlineData("malformedPack", "reviewBundleInvalid")]
    [InlineData("duplicateProperty", "reviewReadyManifestInvalid")]
    [InlineData("escapedMember", "reviewReadyDirectoryInvalid")]
    public void InvalidBundleFailsBeforeLaunchAndRollsBackPreparation(string mutation, string code)
    {
        using TemporaryDirectory temporary = new();
        var (root, pack, paths) = Bundle(temporary);
        string[] selected = [pack];
        switch (mutation)
        {
            case "unselected": selected = []; break;
            case "escapedMember":
                string outside = Path.Combine(temporary.Path, "Outside");
                WritePack(outside);
                selected = [Path.Combine(root, "..", "Outside")];
                break;
            case "extraManifest": WritePack(Path.Combine(root, "Other")); break;
            case "missingPack": Directory.Delete(pack, true); break;
            case "nestedPack":
                string nested = Path.Combine(root, "Container", "Pack");
                Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
                Directory.Move(pack, nested);
                selected = [nested];
                break;
            case "duplicateSelection": selected = [pack, pack]; break;
            case "missingDll": File.Delete(Path.Combine(root, "Bundle.dll")); break;
            case "escapedDll": WriteManifest(root, "Test.Bundle", entryDll: "../Bundle.dll"); break;
            case "duplicateId": WriteManifest(pack, "Test.Bundle", provider: "Test.Bundle"); break;
            case "missingProvider": WriteManifest(pack, "Test.BundlePack", provider: "Missing.Provider"); break;
            case "malformedPack": File.WriteAllText(Path.Combine(pack, "manifest.json"), "{"); break;
            case "duplicateProperty":
                string manifest = File.ReadAllText(Path.Combine(pack, "manifest.json"));
                File.WriteAllText(Path.Combine(pack, "manifest.json"), manifest.Insert(1, "\"UniqueID\":\"Other.Id\","));
                break;
        }
        string before = ModBuildIdentity.ComputeFileSet(root);
        var prepared = ProjectModStager.PrepareReview(root, [], selected, paths, NoDoctor);
        Assert.Equal(code, prepared.Problem?.Code);
        Assert.Empty(prepared.Artifacts);
        Assert.Null(prepared.PreparationRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Fact]
    public void BundlePartialCopyRollsBackEveryMemberAndSourceRemainsUnchanged()
    {
        using TemporaryDirectory temporary = new();
        var (root, pack, paths) = Bundle(temporary);
        string before = ModBuildIdentity.ComputeFileSet(root);
        var prepared = ProjectModStager.PrepareReview(root, [], [pack], paths, NoDoctor);
        Assert.Null(prepared.Problem);
        int copies = 0;
        var staged = ProjectModStager.StageReview(prepared.Artifacts, paths, (source, destination) =>
        {
            ProjectModStager.CopyReadyTree(source, destination);
            if (++copies == 2) throw new IOException("Injected second member copy failure.");
        });
        Assert.Equal("reviewStagingFailed", staged.Problem?.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ModsPath));
        Assert.Null(ProjectModStager.ReadReview(paths).Staging);
        Assert.True(ProjectModStager.RemoveReviewPreparation(prepared.PreparationRoot, paths));
        Assert.Equal(before, ModBuildIdentity.ComputeFileSet(root));
    }

    [Fact]
    public void LinkedMemberIsRejectedWithoutFollowingIt()
    {
        using TemporaryDirectory temporary = new();
        var (root, pack, paths) = Bundle(temporary);
        string outside = Path.Combine(temporary.Path, "Outside");
        Directory.Move(pack, outside);
        var junction = new Win32DirectChildJunctionPlatform();
        junction.CreateDirectoryJunction(pack, outside);
        try
        {
            string before = ModBuildIdentity.ComputeFileSet(outside);
            var prepared = ProjectModStager.PrepareReview(root, [], [pack], paths, NoDoctor);
            Assert.Equal("reviewBundleInvalid", prepared.Problem?.Code);
            Assert.Null(prepared.PreparationRoot);
            Assert.Equal(before, ModBuildIdentity.ComputeFileSet(outside));
        }
        finally { junction.DeleteExactDirectoryJunction(pack, outside); }
    }

    [Fact]
    public void BundleIsRejectedForNetworkBeforeDiscoveryOrLaunch()
    {
        using TemporaryDirectory temporary = new();
        var (root, pack, paths) = Bundle(temporary);
        var result = ProjectReviewService.Execute("start", root, [], [pack], "network-2", paths.ProjectRoot, NoDoctor);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(((ProjectNetworkReviewReport)result.Report).Problems, p => p.Code == "reviewTargetTopologyUnsupported");
        Assert.False(Directory.Exists(paths.SingleRoot));
    }

    private static (string Root, string Pack, LiveLabPaths Paths) Bundle(TemporaryDirectory temporary)
    {
        string root = Directory.CreateDirectory(Path.Combine(temporary.Path, "Bundle")).FullName;
        WriteManifest(root, "Test.Bundle", entryDll: "Bundle.dll");
        File.WriteAllText(Path.Combine(root, "Bundle.dll"), "Original bundle assembly fixture.");
        string pack = Path.Combine(root, "Pack");
        WritePack(pack);
        return (root, pack, LiveLabPaths.Resolve(Directory.CreateDirectory(Path.Combine(temporary.Path, "Lab")).FullName));
    }

    private static void WritePack(string pack)
    {
        Directory.CreateDirectory(pack);
        WriteManifest(pack, "Test.BundlePack", provider: "Test.Bundle");
        File.WriteAllText(Path.Combine(pack, "message.json"), "{\"Message\":\"Hello from the pack\"}");
    }

    private static void WriteManifest(string root, string id, string? entryDll = null, string? provider = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["Name"] = id,
            ["Author"] = "SDVKit",
            ["Description"] = "Original ready bundle fixture.",
            ["Version"] = "1.0.0",
            ["UniqueID"] = id,
        };
        if (entryDll is not null) manifest["EntryDll"] = entryDll;
        if (provider is not null) manifest["ContentPackFor"] = new { UniqueID = provider };
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static DoctorReport NoDoctor() => throw new InvalidOperationException("No game discovery or launch is allowed for this check.");
}

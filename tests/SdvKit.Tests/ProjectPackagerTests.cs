using System.IO.Compression;
using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class ProjectPackagerTests
{
    [Fact]
    public void GeneratedContentPackProducesOnlyRuntimeFiles()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.ContentPack, "ExamplePack");
        Write(target, "assets/data.json", "{}");
        Write(target, "Authoring.cs", "source");
        Write(target, "Stardew Valley.dll", "game binary");
        Write(target, "bin/generated.txt", "build output");
        Write(target, ".sdvkit/private.txt", "state");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            () => throw new InvalidOperationException("Content packs don't discover the game."));

        Assert.Empty(report.Problems);
        Assert.Equal(ProjectInspectionReport.ContentPack, report.Kind);
        Assert.Equal(".sdvkit/packages/ExamplePack 1.0.0.zip", report.Archive);
        Assert.Null(report.Log);
        Assert.Equal(
            [
                "ExamplePack/assets/data.json",
                "ExamplePack/content.json",
                "ExamplePack/manifest.json",
            ],
            report.Entries);
        string archivePath = System.IO.Path.Combine(
            target,
            report.Archive!.Replace('/', System.IO.Path.DirectorySeparatorChar));
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(report.Entries, archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [Fact]
    public void SmapiPackageDelegatesFileSelectionToModBuildConfig()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.SmapiMod, "ExampleMod");
        DotNetBuildCommand? observed = null;

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            ReadyDoctor,
            command =>
            {
                observed = command;
                string zipPath = command.Arguments
                    .Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))
                    ["-p:ModZipPath=".Length..];
                Directory.CreateDirectory(zipPath);
                using ZipArchive archive = ZipFile.Open(
                    System.IO.Path.Combine(zipPath, "ExampleMod 1.0.0.zip"),
                    ZipArchiveMode.Create);
                WriteEntry(archive, "ExampleMod/manifest.json", MainModManifest());
                WriteEntry(archive, "ExampleMod/ExampleMod.dll", "mod assembly");
                WriteEntry(archive, "ExampleMod/ExampleDependency.dll", "declared dependency");
                return new DotNetBuildResult(0, "package succeeded", null);
            });

        Assert.Empty(report.Problems);
        Assert.Equal(".sdvkit/packages/ExampleMod 1.0.0.zip", report.Archive);
        Assert.Equal(
            [
                "ExampleMod/ExampleDependency.dll",
                "ExampleMod/ExampleMod.dll",
                "ExampleMod/manifest.json",
            ],
            report.Entries);
        Assert.Equal(".sdvkit/logs/package.log", report.Log);
        Assert.NotNull(observed);
        Assert.Contains("-p:EnableModDeploy=false", observed.Arguments);
        Assert.Contains("-p:EnableModZip=true", observed.Arguments);
        Assert.Contains(
            observed.Arguments,
            argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal));
        Assert.DoesNotContain(
            observed.Arguments,
            argument => argument.Contains("\\Mods", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OfficialHybridArchiveShapeIsAccepted()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.SmapiMod, "ExampleMod");
        Write(target, "Pack/manifest.json", """
            {
              "Name": "Pack",
              "Author": "Nana",
              "UniqueID": "Nana.ExampleMod.Pack",
              "Version": "1.0.0",
              "Description": "Bundled pack.",
              "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher" }
            }
            """);
        Write(target, "Pack/content.json", "{ \"Format\": \"2.9.0\", \"Changes\": [] }");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            ReadyDoctor,
            command =>
            {
                string zipPath = command.Arguments
                    .Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))
                    ["-p:ModZipPath=".Length..];
                Directory.CreateDirectory(zipPath);
                using ZipArchive archive = ZipFile.Open(
                    System.IO.Path.Combine(zipPath, "Bundle 1.0.0.zip"),
                    ZipArchiveMode.Create);
                WriteEntry(archive, "Bundle/Main/manifest.json", MainModManifest());
                WriteEntry(archive, "Bundle/Main/ExampleMod.dll", "mod assembly");
                WriteEntry(archive, "Bundle/[CP] Pack/manifest.json", "{}");
                WriteEntry(archive, "Bundle/[CP] Pack/content.json", "{}");
                return new DotNetBuildResult(0, string.Empty, null);
            });

        Assert.Empty(report.Problems);
        Assert.Equal(ProjectInspectionReport.Hybrid, report.Kind);
        Assert.Equal(".sdvkit/packages/Bundle 1.0.0.zip", report.Archive);
        Assert.Equal(
            [
                "Bundle/Main/ExampleMod.dll",
                "Bundle/Main/manifest.json",
                "Bundle/[CP] Pack/content.json",
                "Bundle/[CP] Pack/manifest.json",
            ],
            report.Entries);
    }

    [Fact]
    public void UnsafeArchiveFromBuildToolIsRejected()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.SmapiMod, "ExampleMod");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            ReadyDoctor,
            command =>
            {
                string zipPath = command.Arguments
                    .Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))
                    ["-p:ModZipPath=".Length..];
                Directory.CreateDirectory(zipPath);
                using ZipArchive archive = ZipFile.Open(
                    System.IO.Path.Combine(zipPath, "unsafe.zip"),
                    ZipArchiveMode.Create);
                WriteEntry(archive, "ExampleMod/manifest.json", MainModManifest());
                WriteEntry(archive, "ExampleMod/ExampleMod.dll", "mod assembly");
                archive.CreateEntry("../outside/");
                return new DotNetBuildResult(0, string.Empty, null);
            });

        Assert.Equal("unsafePackageArchive", Assert.Single(report.Problems).Code);
        Assert.Null(report.Archive);
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit", "packages")));
    }

    [Theory]
    [InlineData("Stardew Valley.dll")]
    [InlineData("Newtonsoft.Json.dll")]
    [InlineData("Mono.Cecil.xml")]
    public void GameAssemblyFromBuildToolIsRejected(string fileName)
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.SmapiMod, "ExampleMod");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            ReadyDoctor,
            command =>
            {
                string zipPath = command.Arguments
                    .Single(argument => argument.StartsWith("-p:ModZipPath=", StringComparison.Ordinal))
                    ["-p:ModZipPath=".Length..];
                Directory.CreateDirectory(zipPath);
                using ZipArchive archive = ZipFile.Open(
                    System.IO.Path.Combine(zipPath, "unsafe.zip"),
                    ZipArchiveMode.Create);
                WriteEntry(archive, "ExampleMod/manifest.json", "{}");
                WriteEntry(archive, "ExampleMod/ExampleMod.dll", "mod assembly");
                WriteEntry(archive, $"ExampleMod/{fileName}", "game assembly");
                return new DotNetBuildResult(0, string.Empty, null);
            });

        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("unsafePackageArchive", problem.Code);
        Assert.Equal($"ExampleMod/{fileName}", problem.Path);
        Assert.Null(report.Archive);
    }

    [Fact]
    public void CopiedSaveTreeIsRejectedInsteadOfPartiallyPackaged()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.ContentPack, "ExamplePack");
        Write(target, "copied-save/Farm_123456789", "save data");
        Write(target, "copied-save/Farm_123456789_old", "old save data");
        Write(target, "copied-save/SaveGameInfo_old", "save marker");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            () => throw new InvalidOperationException("Content packs don't discover the game."));

        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("saveDataNotAllowed", problem.Code);
        Assert.Equal("copied-save/SaveGameInfo_old", problem.Path);
        Assert.Null(report.Archive);
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));
    }

    [Fact]
    public void SmapiPackageRejectsSaveMarkersBeforeBuildStarts()
    {
        using TemporaryDirectory temporary = new();
        string target = CreateProject(temporary, ProjectCreator.SmapiMod, "ExampleMod");
        Write(target, "Pack/Farm_123456789", "save data");
        Write(target, "Pack/SaveGameInfo", "save marker");

        ProjectPackageReport report = ProjectPackager.Package(
            target,
            ReadyDoctor,
            _ => throw new InvalidOperationException("dotnet should not run."));

        ProjectProblem problem = Assert.Single(report.Problems);
        Assert.Equal("saveDataNotAllowed", problem.Code);
        Assert.Equal("Pack/SaveGameInfo", problem.Path);
        Assert.Null(report.Archive);
        Assert.False(Directory.Exists(System.IO.Path.Combine(target, ".sdvkit")));
    }

    private static string CreateProject(
        TemporaryDirectory temporary,
        string kind,
        string directoryName)
    {
        string target = System.IO.Path.Combine(temporary.Path, directoryName);
        ProjectCreationReport report = ProjectCreator.Create(new ProjectCreationRequest(
            kind,
            target,
            directoryName,
            "Nana",
            $"Nana.{directoryName}",
            "Example."));
        Assert.Empty(report.Problems);
        return target;
    }

    private static DoctorReport ReadyDoctor()
    {
        return new DoctorReport(1, DoctorReport.Ready, [new DetectedInstallation("C:\\Game")]);
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = System.IO.Path.Combine(
            root,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private static string MainModManifest()
    {
        return "{ \"UniqueID\": \"Nana.ExampleMod\", \"EntryDll\": \"ExampleMod.dll\" }";
    }
}

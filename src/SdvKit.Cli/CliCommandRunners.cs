using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

internal sealed record LiveLabCommandResult(int ExitCode, object Report);

internal delegate LiveLabCommandResult LiveLabCommandRunner(
    string action,
    string topology,
    string projectRoot);

internal delegate LiveLabCommandResult ProjectSmokeCommandRunner(
    string sourcePath,
    string topology,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewCommandRunner(
    string action,
    string sourcePath,
    IReadOnlyList<string> companionPaths,
    IReadOnlyList<string> contentPackPaths,
    bool useTestSave,
    string topology,
    string labRoot,
    string? projectFile);

internal delegate LiveLabCommandResult ProjectReviewConsoleCommandRunner(
    string command,
    string topology,
    string? role,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewDataCommandRunner(
    ReviewDataQuery query,
    string labRoot,
    string topology,
    string? role,
    int? screenId);

internal delegate LiveLabCommandResult ProjectReviewMapCommandRunner(
    ReviewMapQuery query,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewTextureCommandRunner(
    ReviewTextureQuery query,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewAudioCommandRunner(
    ReviewAudioQuery query,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewModAssetCommandRunner(
    ReviewModAssetQuery query,
    string labRoot);

internal delegate int ProjectReviewMcpCommandRunner(
    string topology,
    string? role,
    int? screenId,
    string labRoot,
    bool allowInput,
    bool allowFixtureActions,
    bool allowWorldActions,
    bool allowContainerTransfer,
    bool allowCpRefresh,
    TextWriter error,
    int? httpPort = null);

internal sealed class CliCommandRunners
{
    public LiveLabCommandRunner LiveLab { get; set; }

    public ProjectSmokeCommandRunner ProjectSmoke { get; set; }

    public ProjectReviewCommandRunner ProjectReview { get; set; }

    public ProjectReviewConsoleCommandRunner ProjectReviewConsole { get; set; }

    public ProjectReviewDataCommandRunner ProjectReviewData { get; set; }

    public ProjectReviewTextureCommandRunner ProjectReviewTexture { get; set; }

    public ProjectReviewAudioCommandRunner ProjectReviewAudio { get; set; }

    public ProjectReviewMcpCommandRunner ProjectReviewMcp { get; set; }

    public ProjectReviewMapCommandRunner ProjectReviewMap { get; set; }

    public ProjectReviewModAssetCommandRunner ProjectReviewModAsset { get; set; }

    public CliCommandRunners(Func<DoctorReport> discoverInstallations, string? selectedGamePath)
    {
        LiveLab = (action, topology, projectRoot) =>
            string.Equals(action, "smoke", StringComparison.Ordinal)
            && string.Equals(topology, "network-2", StringComparison.Ordinal)
                ? NetworkTwoSmokeService.Execute(projectRoot, discoverInstallations)
                : LiveLabService.Execute(action, projectRoot, discoverInstallations);

        ProjectSmoke = (sourcePath, topology, labRoot) =>
            ProjectSmokeService.Execute(
                sourcePath,
                topology,
                labRoot,
                discoverInstallations);

        ProjectReview = (
            action,
            sourcePath,
            companionPaths,
            contentPackPaths,
            useTestSave,
            topology,
            labRoot,
            projectFile) => ProjectReviewService.Execute(
                action,
                sourcePath,
                companionPaths,
                contentPackPaths,
                topology,
                labRoot,
                discoverInstallations,
                useTestSave,
                projectFile,
                selectedGamePath);

        ProjectReviewConsole = (command, topology, role, labRoot) =>
            ProjectReviewService.ExecuteCommand(
                command,
                topology,
                role,
                labRoot,
                requireFixtureNotReady: ProjectReviewConsoleLine.IsTitleInputOrViewport(command));
        ProjectReviewData = (query, labRoot, topology, role, screenId) =>
            ProjectReviewDataService.Execute(
                query,
                new ProjectReviewMcpRuntimeReader(labRoot, topology, role, screenId: screenId));
        ProjectReviewMap = (query, labRoot) =>
            ProjectReviewMapService.Execute(query, labRoot);
        ProjectReviewTexture = (query, labRoot) =>
            ProjectReviewTextureService.Execute(query, labRoot);
        ProjectReviewAudio = (query, labRoot) =>
            ProjectReviewAudioService.Execute(query, labRoot);
        ProjectReviewModAsset = (query, labRoot) =>
            ProjectReviewModAssetService.Execute(query, labRoot);
        ProjectReviewMcp = (
            topology,
            role,
            screenId,
            labRoot,
            allowInput,
            allowFixtureActions,
            allowWorldActions,
            allowContainerTransfer,
            allowCpRefresh,
            mcpError,
            httpPort) =>
            ProjectReviewMcpServer.RunAsync(
                labRoot,
                topology,
                role,
                screenId,
                allowInput,
                allowFixtureActions,
                allowWorldActions,
                allowContainerTransfer,
                allowCpRefresh,
                mcpError,
                httpPort)
                .GetAwaiter().GetResult();

    }
}

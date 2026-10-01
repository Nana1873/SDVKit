using System.Globalization;
using System.Text.Json;
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
    TextWriter error);

public static partial class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int InspectionFailed = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        return Run(arguments, output, error, GameInstallationDiscovery.Discover);
    }

    internal static int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations,
        LiveLabCommandRunner? runLiveLab = null,
        ProjectSmokeCommandRunner? runProjectSmoke = null,
        ProjectReviewCommandRunner? runProjectReview = null,
        ProjectReviewConsoleCommandRunner? runProjectReviewConsole = null,
        ProjectReviewDataCommandRunner? runProjectReviewData = null,
        ProjectReviewTextureCommandRunner? runProjectReviewTexture = null,
        ProjectReviewAudioCommandRunner? runProjectReviewAudio = null,
        ProjectReviewMcpCommandRunner? runProjectReviewMcp = null,
        ProjectReviewMapCommandRunner? runProjectReviewMap = null,
        ProjectReviewModAssetCommandRunner? runProjectReviewModAsset = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(discoverInstallations);

        string? selectedGamePath = null;
        bool acceptsGamePath = arguments.Count > 0 && (arguments[0] == "doctor"
            || arguments.Count > 1 && arguments[0] == "lab" && arguments[1] is "start" or "test-save" or "smoke"
            || arguments.Count > 1 && arguments[0] == "project" && arguments[1] is "build" or "package" or "smoke"
            || arguments.Count > 2 && arguments[0] == "project" && arguments[1] == "review" && arguments[2] == "start");
        if (acceptsGamePath)
        {
            if (!TryExtractOption(arguments, "--game-path", out arguments, out string? gamePath))
            {
                error.WriteLine("Supply --game-path <directory> at most once.");
                return UsageError;
            }
            selectedGamePath = gamePath;
            Func<DoctorReport> originalDiscovery = discoverInstallations;
            var frozen = new Lazy<DoctorReport>(() => gamePath is null
                ? originalDiscovery()
                : GameInstallationDiscovery.Inspect([gamePath], includeMissingPaths: true));
            discoverInstallations = () => frozen.Value;
        }

        if (runLiveLab is null)
        {
            runLiveLab = (action, topology, projectRoot) =>
                string.Equals(action, "smoke", StringComparison.Ordinal)
                && string.Equals(topology, "network-2", StringComparison.Ordinal)
                    ? NetworkTwoSmokeService.Execute(projectRoot, discoverInstallations)
                    : LiveLabService.Execute(action, projectRoot, discoverInstallations);
        }

        if (runProjectSmoke is null)
        {
            runProjectSmoke = (sourcePath, topology, labRoot) =>
                ProjectSmokeService.Execute(
                    sourcePath,
                    topology,
                    labRoot,
                    discoverInstallations);
        }

        if (runProjectReview is null)
        {
            runProjectReview = (
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
        }

        runProjectReviewConsole ??= (command, topology, role, labRoot) =>
            ProjectReviewService.ExecuteCommand(command, topology, role, labRoot);
        runProjectReviewData ??= (query, labRoot, topology, role, screenId) =>
            ProjectReviewDataService.Execute(
                query,
                new ProjectReviewMcpRuntimeReader(labRoot, topology, role, screenId: screenId));
        runProjectReviewMap ??= (query, labRoot) =>
            ProjectReviewMapService.Execute(query, labRoot);
        runProjectReviewTexture ??= (query, labRoot) =>
            ProjectReviewTextureService.Execute(query, labRoot);
        runProjectReviewAudio ??= (query, labRoot) =>
            ProjectReviewAudioService.Execute(query, labRoot);
        runProjectReviewModAsset ??= (query, labRoot) =>
            ProjectReviewModAssetService.Execute(query, labRoot);
        runProjectReviewMcp ??= (
            topology,
            role,
            screenId,
            labRoot,
            allowInput,
            allowFixtureActions,
            allowWorldActions,
            allowContainerTransfer,
            allowCpRefresh,
            mcpError) =>
            ProjectReviewMcpServer.RunStdioAsync(
                labRoot,
                topology,
                role,
                screenId,
                allowInput,
                allowFixtureActions,
                allowWorldActions,
                allowContainerTransfer,
                allowCpRefresh,
                mcpError)
                .GetAwaiter().GetResult();

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            WriteHelp(output);
            return Success;
        }

        if (IsVersion(arguments[0]))
        {
            return RunVersion(arguments, output, error);
        }

        if (string.Equals(arguments[0], "doctor", StringComparison.Ordinal))
        {
            return RunDoctor(arguments, output, error, discoverInstallations);
        }

        if (string.Equals(arguments[0], "save", StringComparison.Ordinal))
        {
            return RunSave(arguments, output, error);
        }

        if (string.Equals(arguments[0], "project", StringComparison.Ordinal))
        {
            return RunProject(
                arguments,
                output,
                error,
                discoverInstallations,
                runProjectSmoke,
                runProjectReview,
                runProjectReviewConsole,
                runProjectReviewData,
                runProjectReviewTexture,
                runProjectReviewAudio,
                runProjectReviewMcp,
                runProjectReviewMap,
                runProjectReviewModAsset);
        }

        if (string.Equals(arguments[0], "lab", StringComparison.Ordinal))
        {
            return RunLab(arguments, output, error, runLiveLab);
        }

        error.WriteLine($"Unknown command '{arguments[0]}'. Run 'sdvkit help'.");
        return UsageError;
    }

    private static string CurrentVersion =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static int RunVersion(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if (arguments.Count == 1)
        {
            output.WriteLine($"SDVKit {CurrentVersion}");
            return Success;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            WriteJson(output, new
            {
                name = "sdvkit",
                version = CurrentVersion,
            });
            return Success;
        }

        error.WriteLine("Usage: sdvkit version [--json]");
        return UsageError;
    }

    private static int RunDoctor(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            output.WriteLine("Usage: sdvkit doctor [--game-path <directory>] --json");
            return Success;
        }

        if (arguments.Count != 2
            || !string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: sdvkit doctor [--game-path <directory>] --json");
            return UsageError;
        }

        DoctorReport report = discoverInstallations();
        WriteJson(output, report);
        return string.Equals(report.Status, DoctorReport.Ready, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
    }

    private static int RunLab(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        LiveLabCommandRunner runLiveLab)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            WriteLabUsage(output);
            return Success;
        }

        if (arguments.Count != 5)
        {
            WriteLabUsage(error);
            return UsageError;
        }

        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        string? topology = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (string.Equals(arguments[index], "--topology", StringComparison.Ordinal)
                && index + 1 < arguments.Count)
            {
                topologyOptionCount++;
                topology = arguments[++index];
                continue;
            }

            WriteLabUsage(error);
            return UsageError;
        }

        string action = arguments[1];
        bool isSingleCommand = string.Equals(topology, "single", StringComparison.Ordinal)
            && action is "start" or "status" or "stop" or "test-save";
        bool isNetworkTwoSmoke = string.Equals(topology, "network-2", StringComparison.Ordinal)
            && string.Equals(action, "smoke", StringComparison.Ordinal);
        if (jsonOptionCount != 1
            || topologyOptionCount != 1
            || (!isSingleCommand && !isNetworkTwoSmoke))
        {
            WriteLabUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runLiveLab(
            action,
            topology!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "help", StringComparison.Ordinal)
        || string.Equals(value, "--help", StringComparison.Ordinal)
        || string.Equals(value, "-h", StringComparison.Ordinal);

    private static bool IsVersion(string value) =>
        string.Equals(value, "version", StringComparison.Ordinal)
        || string.Equals(value, "--version", StringComparison.Ordinal);

    private static void WriteJson<T>(TextWriter output, T value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }
}

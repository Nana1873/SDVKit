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
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewConsoleCommandRunner(
    string command,
    string topology,
    string? role,
    string labRoot);

internal delegate LiveLabCommandResult ProjectReviewDataCommandRunner(
    ReviewDataQuery query,
    string labRoot);

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
    string labRoot,
    bool allowInput,
    TextWriter error);

public static class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int InspectionFailed = 3;
    private const string InspectUsage = "Usage: sdvkit project inspect [path] --json";
    private const string CreateUsage = "Usage: sdvkit project create <smapi-mod|content-pack> <path> --name <name> --author <author> --unique-id <id> --description <text> --json";
    private const string BuildUsage = "Usage: sdvkit project build [path] --json";
    private const string PackageUsage = "Usage: sdvkit project package [path] --json";
    private const string SmokeUsage =
        "Usage: sdvkit project smoke [path] --topology <single|network-2> --json";
    private const string ReviewStartUsage =
        "Usage: sdvkit project review start [code-project-or-content-pack] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json";
    private const string ReviewStatusUsage =
        "       sdvkit project review status [--topology <single|network-2>] --json";
    private const string ReviewCommandUsage =
        "       sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json";
    private const string ReviewStopUsage =
        "       sdvkit project review stop [--topology <single|network-2>] --json";
    private const string ReviewResetUsage =
        "       sdvkit project review reset --topology <single|network-2> --json";
    private const string ReviewDataAssetsUsage =
        "       sdvkit project review data assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewDataKeysUsage =
        "       sdvkit project review data keys <asset> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewDataGetUsage =
        "       sdvkit project review data get <asset> <key> [--topology single] --json";
    private const string ReviewMapAssetsUsage =
        "       sdvkit project review map assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapSummaryUsage =
        "       sdvkit project review map <assets|get|layers|layer|tilesheets|warps|tile|property> ... --json";
    private const string ReviewMapGetUsage =
        "       sdvkit project review map get <map> [--topology single] --json";
    private const string ReviewMapLayersUsage =
        "       sdvkit project review map layers <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapLayerUsage =
        "       sdvkit project review map layer <map> <layer> [--topology single] --json";
    private const string ReviewMapTileSheetsUsage =
        "       sdvkit project review map tilesheets <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapWarpsUsage =
        "       sdvkit project review map warps <map> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewMapTileUsage =
        "       sdvkit project review map tile <map> <layer> <x> <y> [--topology single] --json";
    private const string ReviewMapPropertyMapUsage =
        "       sdvkit project review map property <map> map <property> [--topology single] --json";
    private const string ReviewMapPropertyLayerUsage =
        "       sdvkit project review map property <map> layer <layer> <property> [--topology single] --json";
    private const string ReviewMapPropertyTileUsage =
        "       sdvkit project review map property <map> tile <layer> <x> <y> direct <property> [--topology single] --json";
    private const string ReviewMapPropertyIndexUsage =
        "       sdvkit project review map property <map> tile <layer> <x> <y> tile-index <property> [--frame <n>] [--topology single] --json";
    private const string ReviewTextureAssetsUsage =
        "       sdvkit project review texture assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewTextureGetUsage =
        "       sdvkit project review texture get <asset> [--topology single] --json";
    private const string ReviewTexturePreviewUsage =
        "       sdvkit project review texture preview <asset> [--topology single] --json";
    private const string ReviewAudioCuesUsage =
        "       sdvkit project review audio cues [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewAudioCueUsage =
        "       sdvkit project review audio cue <id> [--topology single] --json";
    private const string ReviewModAssetAssetsUsage =
        "       sdvkit project review mod-assets assets [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewModAssetKeysUsage =
        "       sdvkit project review mod-assets keys <Mods/owner/asset> [--offset <n>] [--limit <1-100>] [--topology single] --json";
    private const string ReviewModAssetGetUsage =
        "       sdvkit project review mod-assets get <Mods/owner/asset> <key> [--topology single] --json";
    private const string ReviewMcpSingleUsage =
        "       sdvkit project review mcp serve [--topology single] [--allow-input]";
    private const string ReviewMcpNetworkUsage =
        "       sdvkit project review mcp serve --topology network-2 --role <host|farmhand> [--allow-input]";
    private const string ReviewMcpToolsDescription =
        "       all MCP topologies: stardew_runtime_get, stardew_review_get, stardew_mods_list, stardew_screenshot_capture; single additionally: stardew_data_assets_list, stardew_data_keys_list, stardew_data_record_get";
    private const string ReviewMcpInputDescription =
        "       --allow-input additionally exposes only: stardew_input_press, stardew_input_cursor_set, stardew_input_cursor_clear, stardew_input_wheel";
    private const string LabSingleUsage =
        "Usage: sdvkit lab <start|status|stop|test-save> --topology single --json";
    private const string LabNetworkTwoUsage =
        "       sdvkit lab smoke --topology network-2 --json";

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
                labRoot) => ProjectReviewService.Execute(
                    action,
                    sourcePath,
                    companionPaths,
                    contentPackPaths,
                    topology,
                    labRoot,
                    discoverInstallations,
                    useTestSave);
        }

        runProjectReviewConsole ??= (command, topology, role, labRoot) =>
            ProjectReviewService.ExecuteCommand(command, topology, role, labRoot);
        runProjectReviewData ??= (query, labRoot) =>
            ProjectReviewDataService.Execute(query, labRoot);
        runProjectReviewMap ??= (query, labRoot) =>
            ProjectReviewMapService.Execute(query, labRoot);
        runProjectReviewTexture ??= (query, labRoot) =>
            ProjectReviewTextureService.Execute(query, labRoot);
        runProjectReviewAudio ??= (query, labRoot) =>
            ProjectReviewAudioService.Execute(query, labRoot);
        runProjectReviewModAsset ??= (query, labRoot) =>
            ProjectReviewModAssetService.Execute(query, labRoot);
        runProjectReviewMcp ??= (topology, role, labRoot, allowInput, mcpError) =>
            ProjectReviewMcpServer.RunStdioAsync(
                labRoot,
                topology,
                role,
                allowInput,
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
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.6.1";

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
            output.WriteLine("Usage: sdvkit doctor --json");
            return Success;
        }

        if (arguments.Count != 2
            || !string.Equals(arguments[1], "--json", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: sdvkit doctor --json");
            return UsageError;
        }

        DoctorReport report = discoverInstallations();
        WriteJson(output, report);
        return string.Equals(report.Status, DoctorReport.Ready, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
    }

    private static int RunProject(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations,
        ProjectSmokeCommandRunner runProjectSmoke,
        ProjectReviewCommandRunner runProjectReview,
        ProjectReviewConsoleCommandRunner runProjectReviewConsole,
        ProjectReviewDataCommandRunner runProjectReviewData,
        ProjectReviewTextureCommandRunner runProjectReviewTexture,
        ProjectReviewAudioCommandRunner runProjectReviewAudio,
        ProjectReviewMcpCommandRunner runProjectReviewMcp,
        ProjectReviewMapCommandRunner runProjectReviewMap,
        ProjectReviewModAssetCommandRunner runProjectReviewModAsset)
    {
        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            WriteProjectHelp(output);
            return Success;
        }

        if (arguments.Count < 2)
        {
            error.WriteLine(
                "Usage: sdvkit project <inspect|create|build|package|smoke|review> ...");
            return UsageError;
        }

        return arguments[1] switch
        {
            "inspect" => RunProjectInspect(arguments, output, error),
            "create" => RunProjectCreate(arguments, output, error),
            "build" => RunProjectBuild(arguments, output, error, discoverInstallations),
            "package" => RunProjectPackage(arguments, output, error, discoverInstallations),
            "smoke" => RunProjectSmoke(arguments, output, error, runProjectSmoke),
            "review" => RunProjectReview(
                arguments,
                output,
                error,
                runProjectReview,
                runProjectReviewConsole,
                runProjectReviewData,
                runProjectReviewTexture,
                runProjectReviewAudio,
                runProjectReviewMcp,
                runProjectReviewMap,
                runProjectReviewModAsset),
            _ => ProjectUsageError(error),
        };
    }

    private static int RunProjectInspect(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if (arguments.Count == 3
            && IsHelp(arguments[2]))
        {
            output.WriteLine(InspectUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(InspectUsage);
            return UsageError;
        }

        ProjectInspectionReport report = ProjectInspector.Inspect(path!);
        WriteJson(output, report);
        return report.Problems.Count == 0
            && !string.Equals(report.Kind, ProjectInspectionReport.Unknown, StringComparison.Ordinal)
            ? Success
            : InspectionFailed;
    }

    private static int RunProjectCreate(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error)
    {
        if ((arguments.Count == 3 && IsHelp(arguments[2]))
            || (arguments.Count == 4 && IsHelp(arguments[3])))
        {
            output.WriteLine(CreateUsage);
            return Success;
        }

        if (!TryParseCreationRequest(arguments, out ProjectCreationRequest? request))
        {
            error.WriteLine(CreateUsage);
            return UsageError;
        }

        ProjectCreationReport report = ProjectCreator.Create(request!);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectBuild(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(BuildUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(BuildUsage);
            return UsageError;
        }

        ProjectBuildReport report = ProjectBuilder.Build(path!, discoverInstallations);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectPackage(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(PackageUsage);
            return Success;
        }

        if (!TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(PackageUsage);
            return UsageError;
        }

        ProjectPackageReport report = ProjectPackager.Package(path!, discoverInstallations);
        WriteJson(output, report);
        return report.Problems.Count == 0 ? Success : InspectionFailed;
    }

    private static int RunProjectSmoke(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectSmokeCommandRunner runProjectSmoke)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(SmokeUsage);
            return Success;
        }

        if (!TryParseProjectSmoke(arguments, out string? path, out string? topology))
        {
            error.WriteLine(SmokeUsage);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectSmoke(
            path!,
            topology!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static int RunProjectReview(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewCommandRunner runProjectReview,
        ProjectReviewConsoleCommandRunner runProjectReviewConsole,
        ProjectReviewDataCommandRunner runProjectReviewData,
        ProjectReviewTextureCommandRunner runProjectReviewTexture,
        ProjectReviewAudioCommandRunner runProjectReviewAudio,
        ProjectReviewMcpCommandRunner runProjectReviewMcp,
        ProjectReviewMapCommandRunner runProjectReviewMap,
        ProjectReviewModAssetCommandRunner runProjectReviewModAsset)
    {
        if (arguments.Count > 2
            && string.Equals(arguments[2], "data", StringComparison.Ordinal))
        {
            return RunProjectReviewData(
                arguments,
                output,
                error,
                runProjectReviewData);
        }

        if (arguments.Count > 2
            && string.Equals(arguments[2], "map", StringComparison.Ordinal))
        {
            return RunProjectReviewMap(
                arguments,
                output,
                error,
                runProjectReviewMap);
        }

        if (arguments.Count > 2
            && string.Equals(arguments[2], "texture", StringComparison.Ordinal))
        {
            return RunProjectReviewTexture(
                arguments,
                output,
                error,
                runProjectReviewTexture);
        }

        if (arguments.Count > 2
            && string.Equals(arguments[2], "audio", StringComparison.Ordinal))
        {
            return RunProjectReviewAudio(
                arguments,
                output,
                error,
                runProjectReviewAudio);
        }

        if (arguments.Count > 2
            && string.Equals(arguments[2], "mod-assets", StringComparison.Ordinal))
        {
            return RunProjectReviewModAssets(
                arguments,
                output,
                error,
                runProjectReviewModAsset);
        }

        if (TryParseProjectReviewMcp(
                arguments,
                out string? mcpTopology,
                out string? mcpRole,
                out bool allowInput))
        {
            return runProjectReviewMcp(
                mcpTopology!,
                mcpRole,
                Environment.CurrentDirectory,
                allowInput,
                error);
        }

        if ((arguments.Count == 3 && IsHelp(arguments[2]))
            || (arguments.Count == 4 && IsHelp(arguments[3])))
        {
            WriteProjectReviewUsage(output);
            return Success;
        }

        if (!TryParseProjectReview(
                arguments,
                out string? action,
                out string? path,
                out IReadOnlyList<string>? companionPaths,
                out IReadOnlyList<string>? contentPackPaths,
                out string? command,
                out string? topology,
                out string? role,
                out bool useTestSave))
        {
            WriteProjectReviewUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = string.Equals(
            action,
            "command",
            StringComparison.Ordinal)
                ? runProjectReviewConsole(
                    command!,
                    topology!,
                    role,
                    Environment.CurrentDirectory)
                : runProjectReview(
                    action!,
                    path!,
                    companionPaths!,
                    contentPackPaths!,
                    useTestSave,
                    topology!,
                    Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static int RunProjectReviewData(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewDataCommandRunner runProjectReviewData)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewDataUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewData(arguments, out ReviewDataQuery? query))
        {
            WriteProjectReviewDataUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewData(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewData(
        IReadOnlyList<string> arguments,
        out ReviewDataQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "data", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewDataContract.AssetsOperation
                or ReviewDataContract.KeysOperation
                or ReviewDataContract.GetOperation))
        {
            return false;
        }

        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var offsetOptionCount = 0;
        var limitOptionCount = 0;
        string topology = LiveLabState.SingleTopology;
        var offset = 0;
        int limit = operation == ReviewDataContract.GetOperation
            ? 1
            : ReviewDataContract.DefaultPageLimit;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (argument is "--topology" or "--offset" or "--limit")
            {
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                if (string.Equals(argument, "--topology", StringComparison.Ordinal))
                {
                    topologyOptionCount++;
                    topology = value;
                }
                else if (string.Equals(argument, "--offset", StringComparison.Ordinal))
                {
                    offsetOptionCount++;
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out offset))
                    {
                        return false;
                    }
                }
                else
                {
                    limitOptionCount++;
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out limit))
                    {
                        return false;
                    }
                }

                continue;
            }

            operands.Add(argument);
        }

        int expectedOperands = operation switch
        {
            ReviewDataContract.AssetsOperation => 0,
            ReviewDataContract.KeysOperation => 1,
            ReviewDataContract.GetOperation => 2,
            _ => throw new InvalidOperationException(),
        };
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || !string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || offsetOptionCount > 1
            || limitOptionCount > 1
            || offset < 0
            || limit < 1
            || limit > ReviewDataContract.MaximumPageLimit
            || operands.Count != expectedOperands
            || operands.Any(string.IsNullOrWhiteSpace)
            || (operation == ReviewDataContract.GetOperation
                && (offsetOptionCount > 0 || limitOptionCount > 0)))
        {
            return false;
        }

        query = new ReviewDataQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            operands.Count > 1 ? operands[1] : null,
            offset,
            limit);
        return true;
    }

    private static int RunProjectReviewAudio(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewAudioCommandRunner runProjectReviewAudio)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewAudioUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewAudio(arguments, out ReviewAudioQuery? query))
        {
            WriteProjectReviewAudioUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewAudio(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewAudio(
        IReadOnlyList<string> arguments,
        out ReviewAudioQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "audio", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewAudioContract.CuesOperation
                or ReviewAudioContract.CueOperation))
        {
            return false;
        }

        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var offsetOptionCount = 0;
        var limitOptionCount = 0;
        string topology = LiveLabState.SingleTopology;
        var offset = 0;
        int limit = operation == ReviewAudioContract.CueOperation
            ? 1
            : ReviewAudioContract.DefaultPageLimit;
        var optionsEnded = false;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (!optionsEnded && argument is "--topology" or "--offset" or "--limit")
            {
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                if (string.Equals(argument, "--topology", StringComparison.Ordinal))
                {
                    topologyOptionCount++;
                    topology = value;
                }
                else if (string.Equals(argument, "--offset", StringComparison.Ordinal))
                {
                    offsetOptionCount++;
                    if (!TryParseNonNegative(value, out offset))
                    {
                        return false;
                    }
                }
                else
                {
                    limitOptionCount++;
                    if (!TryParseNonNegative(value, out limit))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                return false;
            }

            operands.Add(argument);
        }

        int expectedOperands = operation == ReviewAudioContract.CueOperation ? 1 : 0;
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || !string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || offsetOptionCount > 1
            || limitOptionCount > 1
            || offset < 0
            || limit < 1
            || limit > ReviewAudioContract.MaximumPageLimit
            || operands.Count != expectedOperands
            || operands.Any(string.IsNullOrWhiteSpace)
            || (operation == ReviewAudioContract.CueOperation
                && (offsetOptionCount > 0 || limitOptionCount > 0))
            || (operation == ReviewAudioContract.CueOperation
                && !ReviewAudioValidation.IsSafeCueId(operands[0])))
        {
            return false;
        }

        query = new ReviewAudioQuery(
            operation,
            operands.Count == 1 ? operands[0] : null,
            offset,
            limit);
        return ProjectReviewAudioService.Validate(query) is null;
    }

    private static int RunProjectReviewMap(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewMapCommandRunner runProjectReviewMap)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewMapUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewMap(arguments, out ReviewMapQuery? query))
        {
            WriteProjectReviewMapUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewMap(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewMap(
        IReadOnlyList<string> arguments,
        out ReviewMapQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "map", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewMapContract.AssetsOperation
                or ReviewMapContract.GetOperation
                or ReviewMapContract.LayersOperation
                or ReviewMapContract.LayerOperation
                or ReviewMapContract.TileSheetsOperation
                or ReviewMapContract.WarpsOperation
                or ReviewMapContract.TileOperation
                or ReviewMapContract.PropertyOperation))
        {
            return false;
        }

        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var offsetOptionCount = 0;
        var limitOptionCount = 0;
        var frameOptionCount = 0;
        string topology = LiveLabState.SingleTopology;
        var offset = 0;
        int limit = operation is ReviewMapContract.AssetsOperation
            or ReviewMapContract.LayersOperation
            or ReviewMapContract.TileSheetsOperation
            or ReviewMapContract.WarpsOperation
                ? ReviewMapContract.DefaultPageLimit
                : 1;
        int? frameIndex = null;
        var optionsEnded = false;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (!optionsEnded && argument is "--topology" or "--offset" or "--limit" or "--frame")
            {
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                if (argument == "--topology")
                {
                    topologyOptionCount++;
                    topology = value;
                }
                else if (argument == "--offset")
                {
                    offsetOptionCount++;
                    if (!TryParseNonNegative(value, out offset))
                    {
                        return false;
                    }
                }
                else if (argument == "--limit")
                {
                    limitOptionCount++;
                    if (!TryParseNonNegative(value, out limit))
                    {
                        return false;
                    }
                }
                else
                {
                    frameOptionCount++;
                    if (!TryParseNonNegative(value, out int parsedFrame))
                    {
                        return false;
                    }

                    frameIndex = parsedFrame;
                }

                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                return false;
            }

            operands.Add(argument);
        }

        bool listOperation = operation is ReviewMapContract.AssetsOperation
            or ReviewMapContract.LayersOperation
            or ReviewMapContract.TileSheetsOperation
            or ReviewMapContract.WarpsOperation;
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || !string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || offsetOptionCount > 1
            || limitOptionCount > 1
            || frameOptionCount > 1
            || offset < 0
            || limit < 1
            || limit > ReviewMapContract.MaximumPageLimit
            || (!listOperation && (offsetOptionCount > 0 || limitOptionCount > 0))
            || (operation != ReviewMapContract.PropertyOperation && frameOptionCount > 0)
            || operands.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        string? asset = null;
        string? layer = null;
        int? x = null;
        int? y = null;
        string? propertyScope = null;
        string? propertySource = null;
        string? property = null;
        switch (operation)
        {
            case ReviewMapContract.AssetsOperation when operands.Count == 0:
                break;
            case ReviewMapContract.GetOperation:
            case ReviewMapContract.LayersOperation:
            case ReviewMapContract.TileSheetsOperation:
            case ReviewMapContract.WarpsOperation:
                if (operands.Count != 1)
                {
                    return false;
                }
                asset = operands[0];
                break;
            case ReviewMapContract.LayerOperation:
                if (operands.Count != 2)
                {
                    return false;
                }
                asset = operands[0];
                layer = operands[1];
                break;
            case ReviewMapContract.TileOperation:
                if (operands.Count != 4
                    || !TryParseNonNegative(operands[2], out int tileX)
                    || !TryParseNonNegative(operands[3], out int tileY))
                {
                    return false;
                }
                asset = operands[0];
                layer = operands[1];
                x = tileX;
                y = tileY;
                break;
            case ReviewMapContract.PropertyOperation:
                if (!TryParseMapPropertyOperands(
                        operands,
                        frameIndex,
                        out asset,
                        out layer,
                        out x,
                        out y,
                        out propertyScope,
                        out propertySource,
                        out property))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        query = new ReviewMapQuery(
            operation,
            asset,
            layer,
            x,
            y,
            propertyScope,
            propertySource,
            frameIndex,
            property,
            offset,
            limit);
        return ProjectReviewMapService.Validate(query) is null;
    }

    private static bool TryParseMapPropertyOperands(
        IReadOnlyList<string> operands,
        int? frameIndex,
        out string? asset,
        out string? layer,
        out int? x,
        out int? y,
        out string? scope,
        out string? source,
        out string? property)
    {
        asset = null;
        layer = null;
        x = null;
        y = null;
        scope = null;
        source = null;
        property = null;
        if (operands.Count == 3 && operands[1] == ReviewMapContract.MapScope)
        {
            asset = operands[0];
            scope = ReviewMapContract.MapScope;
            source = ReviewMapContract.DirectSource;
            property = operands[2];
            return frameIndex is null;
        }
        if (operands.Count == 4 && operands[1] == ReviewMapContract.LayerScope)
        {
            asset = operands[0];
            scope = ReviewMapContract.LayerScope;
            layer = operands[2];
            source = ReviewMapContract.DirectSource;
            property = operands[3];
            return frameIndex is null;
        }
        if (operands.Count == 7
            && operands[1] == ReviewMapContract.TileScope
            && operands[5] is ReviewMapContract.DirectSource or ReviewMapContract.TileIndexSource
            && TryParseNonNegative(operands[3], out int tileX)
            && TryParseNonNegative(operands[4], out int tileY))
        {
            asset = operands[0];
            scope = ReviewMapContract.TileScope;
            layer = operands[2];
            x = tileX;
            y = tileY;
            source = operands[5];
            property = operands[6];
            return source == ReviewMapContract.TileIndexSource || frameIndex is null;
        }

        return false;
    }

    private static bool TryParseNonNegative(string value, out int parsed) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed);

    private static int RunProjectReviewTexture(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewTextureCommandRunner runProjectReviewTexture)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewTextureUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewTexture(arguments, out ReviewTextureQuery? query))
        {
            WriteProjectReviewTextureUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewTexture(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewTexture(
        IReadOnlyList<string> arguments,
        out ReviewTextureQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "texture", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewTextureContract.AssetsOperation
                or ReviewTextureContract.GetOperation
                or ReviewTextureContract.PreviewOperation))
        {
            return false;
        }

        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var offsetOptionCount = 0;
        var limitOptionCount = 0;
        string topology = LiveLabState.SingleTopology;
        var offset = 0;
        int limit = operation == ReviewTextureContract.AssetsOperation
            ? ReviewTextureContract.DefaultPageLimit
            : 1;
        var optionsEnded = false;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (!optionsEnded && argument is "--topology" or "--offset" or "--limit")
            {
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                if (string.Equals(argument, "--topology", StringComparison.Ordinal))
                {
                    topologyOptionCount++;
                    topology = value;
                }
                else if (string.Equals(argument, "--offset", StringComparison.Ordinal))
                {
                    offsetOptionCount++;
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out offset))
                    {
                        return false;
                    }
                }
                else
                {
                    limitOptionCount++;
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out limit))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                return false;
            }

            operands.Add(argument);
        }

        int expectedOperands = operation == ReviewTextureContract.AssetsOperation
            ? 0
            : 1;
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || !string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || offsetOptionCount > 1
            || limitOptionCount > 1
            || offset < 0
            || limit < 1
            || limit > ReviewTextureContract.MaximumPageLimit
            || operands.Count != expectedOperands
            || operands.Any(string.IsNullOrWhiteSpace)
            || (operands.Count == 1
                && !ReviewTextureContract.IsCanonicalAssetName(operands[0]))
            || (operation != ReviewTextureContract.AssetsOperation
                && (offsetOptionCount > 0 || limitOptionCount > 0)))
        {
            return false;
        }

        query = new ReviewTextureQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            offset,
            limit);
        return true;
    }

    private static int RunProjectReviewModAssets(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        ProjectReviewModAssetCommandRunner runProjectReviewModAsset)
    {
        if ((arguments.Count == 4 && IsHelp(arguments[3]))
            || (arguments.Count == 5 && IsHelp(arguments[4])))
        {
            WriteProjectReviewModAssetUsage(output);
            return Success;
        }

        if (!TryParseProjectReviewModAssets(
                arguments,
                out ReviewModAssetQuery? query))
        {
            WriteProjectReviewModAssetUsage(error);
            return UsageError;
        }

        LiveLabCommandResult result = runProjectReviewModAsset(
            query!,
            Environment.CurrentDirectory);
        WriteJson(output, result.Report);
        return result.ExitCode;
    }

    private static bool TryParseProjectReviewModAssets(
        IReadOnlyList<string> arguments,
        out ReviewModAssetQuery? query)
    {
        query = null;
        if (arguments.Count < 5
            || !string.Equals(arguments[0], "project", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "review", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "mod-assets", StringComparison.Ordinal))
        {
            return false;
        }

        string operation = arguments[3];
        if (operation is not (
                ReviewModAssetContract.AssetsOperation
                or ReviewModAssetContract.KeysOperation
                or ReviewModAssetContract.GetOperation))
        {
            return false;
        }

        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var offsetOptionCount = 0;
        var limitOptionCount = 0;
        string topology = LiveLabState.SingleTopology;
        var offset = 0;
        int limit = operation == ReviewModAssetContract.GetOperation
            ? 1
            : ReviewModAssetContract.DefaultPageLimit;
        var optionsEnded = false;
        var operandsBeforeEndMarker = -1;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                operandsBeforeEndMarker = operands.Count;
                continue;
            }

            if (!optionsEnded && string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (!optionsEnded && argument is "--topology" or "--offset" or "--limit")
            {
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                if (argument == "--topology")
                {
                    topologyOptionCount++;
                    topology = value;
                }
                else if (argument == "--offset")
                {
                    offsetOptionCount++;
                    if (!TryParseNonNegative(value, out offset))
                    {
                        return false;
                    }
                }
                else
                {
                    limitOptionCount++;
                    if (!TryParseNonNegative(value, out limit))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!optionsEnded && argument.StartsWith('-'))
            {
                return false;
            }

            operands.Add(argument);
        }

        int expectedOperands = operation switch
        {
            ReviewModAssetContract.AssetsOperation => 0,
            ReviewModAssetContract.KeysOperation => 1,
            ReviewModAssetContract.GetOperation => 2,
            _ => throw new InvalidOperationException(),
        };
        bool exactOperation = operation == ReviewModAssetContract.GetOperation;
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || !string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            || offsetOptionCount > 1
            || limitOptionCount > 1
            || offset < 0
            || limit < 1
            || limit > ReviewModAssetContract.MaximumPageLimit
            || (exactOperation && (offsetOptionCount > 0 || limitOptionCount > 0))
            || operands.Count != expectedOperands
            || (operands.Count > 0
                && !ReviewModAssetContract.IsCanonicalAssetName(operands[0]))
            || (operands.Count > 1
                && (!ReviewModAssetContract.IsBoundedText(
                        operands[1],
                        ReviewModAssetContract.MaximumKeyLength)
                    || string.IsNullOrWhiteSpace(operands[1])))
            || (optionsEnded && operands.Count == operandsBeforeEndMarker))
        {
            return false;
        }

        query = new ReviewModAssetQuery(
            operation,
            operands.Count > 0 ? operands[0] : null,
            operands.Count > 1 ? operands[1] : null,
            offset,
            limit);
        return ProjectReviewModAssetService.Validate(query) is null;
    }

    private static bool TryParseProjectReviewMcp(
        IReadOnlyList<string> arguments,
        out string? topology,
        out string? role,
        out bool allowInput)
    {
        topology = LiveLabState.SingleTopology;
        role = null;
        allowInput = false;
        if (arguments.Count < 4
            || !string.Equals(arguments[2], "mcp", StringComparison.Ordinal)
            || !string.Equals(arguments[3], "serve", StringComparison.Ordinal))
        {
            return false;
        }

        var topologyCount = 0;
        var roleCount = 0;
        var allowInputCount = 0;
        for (var index = 4; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (string.Equals(option, "--allow-input", StringComparison.Ordinal))
            {
                allowInputCount++;
                allowInput = true;
                continue;
            }

            if (index + 1 >= arguments.Count
                || arguments[index + 1].StartsWith('-'))
            {
                return false;
            }

            string value = arguments[++index];
            if (string.Equals(option, "--topology", StringComparison.Ordinal))
            {
                topologyCount++;
                topology = value;
            }
            else if (string.Equals(option, "--role", StringComparison.Ordinal))
            {
                roleCount++;
                role = value;
            }
            else
            {
                return false;
            }
        }

        if (topologyCount > 1 || roleCount > 1 || allowInputCount > 1)
        {
            return false;
        }

        return string.Equals(topology, LiveLabState.SingleTopology, StringComparison.Ordinal)
            ? roleCount == 0
            : string.Equals(topology, NetworkTwoContract.Topology, StringComparison.Ordinal)
                && topologyCount == 1
                && roleCount == 1
                && NetworkTwoContract.IsRole(role!);
    }

    private static bool TryParseOptionalPath(
        IReadOnlyList<string> arguments,
        out string? path)
    {
        List<string> operands = [];
        var jsonOptionCount = 0;
        foreach (string argument in arguments.Skip(2))
        {
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
            }
            else
            {
                operands.Add(argument);
            }
        }

        if (jsonOptionCount != 1
            || operands.Count > 1
            || operands.Any(argument => argument.StartsWith('-')))
        {
            path = null;
            return false;
        }

        path = operands.Count == 0 ? Environment.CurrentDirectory : operands[0];
        return true;
    }

    private static bool TryParseProjectSmoke(
        IReadOnlyList<string> arguments,
        out string? path,
        out string? topology)
    {
        var operands = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        topology = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (string.Equals(argument, "--topology", StringComparison.Ordinal))
            {
                topologyOptionCount++;
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    path = null;
                    topology = null;
                    return false;
                }

                topology = arguments[++index];
                continue;
            }

            operands.Add(argument);
        }

        if (jsonOptionCount != 1
            || topologyOptionCount != 1
            || topology is not ("single" or "network-2")
            || operands.Count > 1
            || operands.Any(argument => argument.StartsWith('-')))
        {
            path = null;
            topology = null;
            return false;
        }

        path = operands.Count == 0 ? Environment.CurrentDirectory : operands[0];
        return true;
    }

    private static bool TryParseProjectReview(
        IReadOnlyList<string> arguments,
        out string? action,
        out string? path,
        out IReadOnlyList<string>? companionPaths,
        out IReadOnlyList<string>? contentPackPaths,
        out string? command,
        out string? topology,
        out string? role,
        out bool useTestSave)
    {
        action = arguments.Count > 2 ? arguments[2] : null;
        path = null;
        companionPaths = null;
        contentPackPaths = null;
        command = null;
        topology = "single";
        role = null;
        useTestSave = false;
        if (action is not ("start" or "status" or "command" or "stop" or "reset"))
        {
            return false;
        }

        var operands = new List<string>();
        var companions = new List<string>();
        var packs = new List<string>();
        var jsonOptionCount = 0;
        var topologyOptionCount = 0;
        var roleOptionCount = 0;
        var testSaveOptionCount = 0;
        for (var index = 3; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (string.Equals(argument, "--topology", StringComparison.Ordinal))
            {
                topologyOptionCount++;
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                topology = arguments[++index];
                continue;
            }

            if (string.Equals(argument, "--role", StringComparison.Ordinal))
            {
                roleOptionCount++;
                if (index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                role = arguments[++index];
                continue;
            }

            if (string.Equals(argument, "--test-save", StringComparison.Ordinal))
            {
                testSaveOptionCount++;
                useTestSave = true;
                continue;
            }

            if (argument is "--companion" or "--content-pack")
            {
                if (!string.Equals(action, "start", StringComparison.Ordinal)
                    || index + 1 >= arguments.Count
                    || arguments[index + 1].StartsWith('-'))
                {
                    return false;
                }

                string value = arguments[++index];
                (string.Equals(argument, "--companion", StringComparison.Ordinal)
                    ? companions
                    : packs).Add(value);
                continue;
            }

            operands.Add(argument);
        }

        bool isStart = string.Equals(action, "start", StringComparison.Ordinal);
        bool isCommand = string.Equals(action, "command", StringComparison.Ordinal);
        bool isReset = string.Equals(action, "reset", StringComparison.Ordinal);
        bool networkTwo = string.Equals(topology, "network-2", StringComparison.Ordinal);
        if (jsonOptionCount != 1
            || topologyOptionCount > 1
            || topology is not ("single" or "network-2")
            || roleOptionCount > 1
            || testSaveOptionCount > 1
            || (role is not null && role is not ("host" or "farmhand"))
            || (isStart && operands.Count > 1)
            || (isCommand && (operands.Count != 1
                || ProjectReviewConsoleLine.ValidationError(operands[0]) is not null))
            || (!isStart && !isCommand && operands.Count > 0)
            || (!isStart && (companions.Count > 0 || packs.Count > 0))
            || (!isCommand && roleOptionCount > 0)
            || (isCommand && networkTwo && roleOptionCount != 1)
            || (isCommand && !networkTwo && roleOptionCount != 0)
            || (testSaveOptionCount > 0 && (!isStart || networkTwo))
            || (isReset && topologyOptionCount != 1)
            || (isStart && operands.Any(argument => argument.StartsWith('-'))))
        {
            return false;
        }

        path = isStart && operands.Count == 1
            ? operands[0]
            : Environment.CurrentDirectory;
        companionPaths = companions;
        contentPackPaths = packs;
        command = isCommand ? operands[0] : null;
        return true;
    }

    private static bool TryParseCreationRequest(
        IReadOnlyList<string> arguments,
        out ProjectCreationRequest? request)
    {
        request = null;
        if (arguments.Count < 5
            || (!string.Equals(arguments[2], ProjectCreator.SmapiMod, StringComparison.Ordinal)
                && !string.Equals(arguments[2], ProjectCreator.ContentPack, StringComparison.Ordinal))
            || arguments[3].StartsWith('-'))
        {
            return false;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var jsonOptionCount = 0;
        for (var index = 4; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.Ordinal))
            {
                jsonOptionCount++;
                continue;
            }

            if (argument is not "--name" and not "--author" and not "--unique-id" and not "--description"
                || index + 1 >= arguments.Count
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(argument, arguments[++index]))
            {
                return false;
            }
        }

        if (jsonOptionCount != 1
            || !options.TryGetValue("--name", out string? name)
            || !options.TryGetValue("--author", out string? author)
            || !options.TryGetValue("--unique-id", out string? uniqueId)
            || !options.TryGetValue("--description", out string? description)
            || options.Count != 4)
        {
            return false;
        }

        request = new ProjectCreationRequest(
            arguments[2],
            arguments[3],
            name,
            author,
            uniqueId,
            description);
        return ProjectCreator.IsValidRequest(request);
    }

    private static int ProjectUsageError(TextWriter error)
    {
        error.WriteLine(
            "Usage: sdvkit project <inspect|create|build|package|smoke|review> ...");
        return UsageError;
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

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("SDVKit — lean Stardew Valley modding toolkit and live test lab");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  sdvkit help");
        output.WriteLine("  sdvkit version [--json]");
        output.WriteLine("  sdvkit doctor --json");
        output.WriteLine("  sdvkit project inspect [path] --json");
        output.WriteLine("  sdvkit project create <smapi-mod|content-pack> <path> [options] --json");
        output.WriteLine("  sdvkit project build [path] --json");
        output.WriteLine("  sdvkit project package [path] --json");
        output.WriteLine(
            "  sdvkit project smoke [path] --topology <single|network-2> --json");
        output.WriteLine(
            "  sdvkit project review start [code-project-or-content-pack] [--topology <single|network-2>] [--test-save] [--companion <path>]... [--content-pack <path>]... --json");
        output.WriteLine(
            "  sdvkit project review status [--topology <single|network-2>] --json");
        output.WriteLine(
            "  sdvkit project review command <text> [--topology <single|network-2>] [--role <host|farmhand>] --json");
        output.WriteLine(
            "  sdvkit project review data <assets|keys|get> ... [--topology single] --json");
        output.WriteLine(
            "  sdvkit project review map <assets|get|layers|layer|tilesheets|warps|tile|property> ... [--topology single] --json");
        output.WriteLine(
            "  sdvkit project review texture <assets|get|preview> ... [--topology single] --json");
        output.WriteLine(
            "  sdvkit project review audio <cues|cue> ... [--topology single] --json");
        output.WriteLine(
            "  sdvkit project review mod-assets <assets|keys|get> ... [--topology single] --json");
        output.WriteLine(
            "    Owned review-fixture console lines are transported as <text>; see project review --help.");
        output.WriteLine(
            "  sdvkit project review stop [--topology <single|network-2>] --json");
        output.WriteLine("  sdvkit project review reset --topology <single|network-2> --json");
        output.WriteLine("  sdvkit project review mcp serve [--topology single]");
        output.WriteLine(
            "  sdvkit project review mcp serve --topology network-2 --role <host|farmhand>");
        output.WriteLine(ReviewMcpToolsDescription.TrimStart());
        output.WriteLine(ReviewMcpInputDescription.TrimStart());
        output.WriteLine("  sdvkit lab <start|status|stop|test-save> --topology single --json");
        output.WriteLine("  sdvkit lab smoke --topology network-2 --json");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  doctor          Detect ready Stardew Valley + SMAPI installations (read-only).");
        output.WriteLine("  project inspect Classify a SMAPI mod, content pack, or hybrid (read-only).");
        output.WriteLine("  project create  Create a minimal SMAPI mod or Content Patcher pack.");
        output.WriteLine("  project build   Build one SMAPI project with deployment disabled.");
        output.WriteLine("  project package Create an isolated release archive below .sdvkit/packages.");
        output.WriteLine("  project smoke   Build and smoke-test one mod in the isolated live lab.");
        output.WriteLine("  project review  Review one C# mod target, or one root content-pack target in singleplayer.");
        output.WriteLine("  lab             Control one isolated process or run an isolated live-lab smoke.");
    }

    private static void WriteLabUsage(TextWriter output)
    {
        output.WriteLine(LabSingleUsage);
        output.WriteLine(LabNetworkTwoUsage);
    }

    private static void WriteProjectHelp(TextWriter output)
    {
        output.WriteLine("SDVKit project toolkit");
        output.WriteLine();
        output.WriteLine(InspectUsage);
        output.WriteLine(CreateUsage);
        output.WriteLine(BuildUsage);
        output.WriteLine(PackageUsage);
        output.WriteLine(SmokeUsage);
        output.WriteLine(ReviewStartUsage);
        output.WriteLine(ReviewStatusUsage);
        output.WriteLine(ReviewCommandUsage);
        output.WriteLine(ReviewDataAssetsUsage);
        output.WriteLine(ReviewDataKeysUsage);
        output.WriteLine(ReviewDataGetUsage);
        output.WriteLine(ReviewMapSummaryUsage);
        output.WriteLine(ReviewTextureAssetsUsage);
        output.WriteLine(ReviewTextureGetUsage);
        output.WriteLine(ReviewTexturePreviewUsage);
        output.WriteLine(ReviewAudioCuesUsage);
        output.WriteLine(ReviewAudioCueUsage);
        output.WriteLine(ReviewModAssetAssetsUsage);
        output.WriteLine(ReviewModAssetKeysUsage);
        output.WriteLine(ReviewModAssetGetUsage);
        output.WriteLine(ReviewStopUsage);
        output.WriteLine(ReviewResetUsage);
        output.WriteLine(ReviewMcpSingleUsage);
        output.WriteLine(ReviewMcpNetworkUsage);
        output.WriteLine(ReviewMcpInputDescription);
        WriteReviewFixtureConsoleUsage(output);
    }

    private static void WriteProjectReviewUsage(TextWriter output)
    {
        output.WriteLine(ReviewStartUsage);
        output.WriteLine(ReviewStatusUsage);
        output.WriteLine(ReviewCommandUsage);
        output.WriteLine(ReviewDataAssetsUsage);
        output.WriteLine(ReviewDataKeysUsage);
        output.WriteLine(ReviewDataGetUsage);
        output.WriteLine(ReviewMapSummaryUsage);
        output.WriteLine(ReviewTextureAssetsUsage);
        output.WriteLine(ReviewTextureGetUsage);
        output.WriteLine(ReviewTexturePreviewUsage);
        output.WriteLine(ReviewAudioCuesUsage);
        output.WriteLine(ReviewAudioCueUsage);
        output.WriteLine(ReviewModAssetAssetsUsage);
        output.WriteLine(ReviewModAssetKeysUsage);
        output.WriteLine(ReviewModAssetGetUsage);
        output.WriteLine(ReviewStopUsage);
        output.WriteLine(ReviewResetUsage);
        output.WriteLine(ReviewMcpSingleUsage);
        output.WriteLine(ReviewMcpNetworkUsage);
        output.WriteLine(ReviewMcpToolsDescription);
        output.WriteLine(ReviewMcpInputDescription);
        output.WriteLine(
            "Content-pack targets require --topology single and an explicit provider --companion.");
        WriteReviewFixtureConsoleUsage(output);
    }

    private static void WriteProjectReviewDataUsage(TextWriter output)
    {
        output.WriteLine(ReviewDataAssetsUsage.TrimStart());
        output.WriteLine(ReviewDataKeysUsage.TrimStart());
        output.WriteLine(ReviewDataGetUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review and return only canonical installed Data assets after the active SMAPI content pipeline.");
    }

    private static void WriteProjectReviewMapUsage(TextWriter output)
    {
        output.WriteLine(ReviewMapAssetsUsage.TrimStart());
        output.WriteLine(ReviewMapGetUsage.TrimStart());
        output.WriteLine(ReviewMapLayersUsage.TrimStart());
        output.WriteLine(ReviewMapLayerUsage.TrimStart());
        output.WriteLine(ReviewMapTileSheetsUsage.TrimStart());
        output.WriteLine(ReviewMapWarpsUsage.TrimStart());
        output.WriteLine(ReviewMapTileUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyMapUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyLayerUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyTileUsage.TrimStart());
        output.WriteLine(ReviewMapPropertyIndexUsage.TrimStart());
        output.WriteLine(
            "Property scopes: map <name>; layer <layer> <name>; tile <layer> <x> <y> <direct|tile-index> <name> (animated tile-index requires --frame <n>). Queries require an active owned single review.");
        output.WriteLine(
            "For a map, layer, or property operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewTextureUsage(TextWriter output)
    {
        output.WriteLine(ReviewTextureAssetsUsage.TrimStart());
        output.WriteLine(ReviewTextureGetUsage.TrimStart());
        output.WriteLine(ReviewTexturePreviewUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review. Inventory is canonical and measured; exact metadata and one bounded diagnostic PNG reflect the final SMAPI content pipeline without claiming per-mod provenance.");
        output.WriteLine(
            "For an asset operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewAudioUsage(TextWriter output)
    {
        output.WriteLine(ReviewAudioCuesUsage.TrimStart());
        output.WriteLine(ReviewAudioCueUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review. Discovery covers the final Data/AudioChanges and Data/JukeboxTracks populations; the public API cannot enumerate the built-in XACT cue bank.");
        output.WriteLine(
            "For a cue operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteProjectReviewModAssetUsage(TextWriter output)
    {
        output.WriteLine(ReviewModAssetAssetsUsage.TrimStart());
        output.WriteLine(ReviewModAssetKeysUsage.TrimStart());
        output.WriteLine(ReviewModAssetGetUsage.TrimStart());
        output.WriteLine(
            "Queries require an active owned single review and cover only observed requests in canonical Mods/<owner>/... namespaces. Six explicit primitive adapters are supported; detailed provider attribution is unavailable through the public SMAPI API.");
        output.WriteLine(
            "For an asset or key operand that starts with '-' or matches an option name, put every CLI option before '--'; every following token is treated as an operand.");
    }

    private static void WriteReviewFixtureConsoleUsage(TextWriter output)
    {
        output.WriteLine(
            "AlwaysOn review console lines (quote one as <text> for project review command; not top-level CLI):");
        output.WriteLine("  sdvkit screenshot <label>");
        output.WriteLine("  sdvkit screenshot viewport <label>");
        output.WriteLine("  sdvkit input press <SButton>");
        output.WriteLine("  sdvkit input cursor <ui-x> <ui-y>");
        output.WriteLine("  sdvkit input cursor clear");
        output.WriteLine("  sdvkit fixture status");
        output.WriteLine("  sdvkit fixture building ensure <alias> <building-kind> <x> <y>");
        output.WriteLine("  sdvkit fixture object ensure <alias-or-id> <qualified-item-id>");
        output.WriteLine("  sdvkit fixture object clear-owned <alias-or-id>");
        output.WriteLine("  sdvkit fixture animal ensure <alias-or-id> <animal-kind>");
        output.WriteLine(
            "  Kinds resolve from loaded canonical Stardew data IDs; legacy deluxe-barn and white-cow remain valid.");
        output.WriteLine(
            "  Unknown, ambiguous, unplaceable, or animal-house-incompatible kinds fail before mutation.");
        output.WriteLine("  sdvkit fixture enter <alias-or-id>");
        output.WriteLine("  sdvkit fixture enter greenhouse");
        output.WriteLine("  sdvkit fixture farm");
    }
}

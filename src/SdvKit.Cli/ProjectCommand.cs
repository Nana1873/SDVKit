using System.Globalization;
using System.Text.Json;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

public static partial class CliApplication
{
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
                "Usage: sdvkit project <inspect|check|create|build|package|smoke|review> ...");
            return UsageError;
        }

        return arguments[1] switch
        {
            "inspect" => RunProjectInspect(arguments, output, error),
            "check" => RunProjectCheck(arguments, output, error),
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

    private static int RunProjectCheck(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(CheckUsage);
            output.WriteLine("Offline schema check and locale comparison of one mod root: manifest.json, CP 2.9.x content.json, and direct i18n/*.json.");
            output.WriteLine("Checks reachable literal CP Includes and FromFile existence; conditional/tokenized references are skipped with warnings.");
            output.WriteLine("No recursive mod discovery, build, or runtime validation; Include validation is bounded.");
            return Success;
        }

        bool json = arguments.Contains("--json", StringComparer.Ordinal);
        IReadOnlyList<string> parseArguments = json ? arguments : [.. arguments, "--json"];
        if (!TryParseOptionalPath(parseArguments, out string? path))
        {
            error.WriteLine(CheckUsage);
            return UsageError;
        }

        ProjectCheckReport report = ProjectChecker.Check(path!);
        if (json)
        {
            WriteJson(output, report);
        }
        else
        {
            output.WriteLine($"Project check {report.Status}: {report.Files.Count} file(s) evaluated.");
            foreach (ProjectCheckProblem problem in report.Problems)
            {
                output.WriteLine($"{problem.File} {problem.Field} [{problem.Code}]: {problem.Message}");
            }
            foreach (ProjectCheckProblem warning in report.Warnings)
            {
                output.WriteLine($"{warning.File} {warning.Field} [warning:{warning.Code}]: {warning.Message}");
            }
            output.WriteLine("Schema and locale checks do not prove that patches apply in game or translations render correctly.");
        }
        return report.Problems.Count == 0 ? Success : InspectionFailed;
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

        if (!TryExtractOption(arguments, "--project", out arguments, out string? projectFile)
            || !TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(BuildUsage);
            return UsageError;
        }

        ProjectBuildReport report = ProjectBuilder.Build(path!, discoverInstallations, projectFile: projectFile);
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

        if (!TryExtractOption(arguments, "--project", out arguments, out string? projectFile)
            || !TryParseOptionalPath(arguments, out string? path))
        {
            error.WriteLine(PackageUsage);
            return UsageError;
        }

        ProjectPackageReport report = ProjectPackager.Package(path!, discoverInstallations, projectFile: projectFile);
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

    private static bool TryExtractOption(
        IReadOnlyList<string> arguments,
        string option,
        out IReadOnlyList<string> remaining,
        out string? value)
    {
        var result = new List<string>();
        value = null;
        remaining = arguments;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != option) { result.Add(arguments[index]); continue; }
            if (value is not null || index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]) || arguments[index + 1].StartsWith('-')) return false;
            value = arguments[++index];
        }
        remaining = result;
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
            "Usage: sdvkit project <inspect|check|create|build|package|smoke|review> ...");
        return UsageError;
    }
}

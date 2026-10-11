using SdvKit.Cli.AgentKit;

namespace SdvKit.Cli;

public static partial class CliApplication
{
    private const string LabInitUsage = "Usage: sdvkit lab init [<lab-root>] [--force] --json";

    /// <summary>Sets up or updates SDVKit's agent layer in a lab root.</summary>
    private static int RunLabInit(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<DoctorReport> discoverInstallations)
    {
        if (arguments.Count == 3 && IsHelp(arguments[2]))
        {
            output.WriteLine(LabInitUsage);
            output.WriteLine("Writes SDVKit's agent layer into a lab root (default: the current directory): SDVKIT.md, task skills for Claude Code and Codex, a Claude Code hook that blocks writes to the game folder and the normal Saves folder, a sdvkit.cmd launcher, and an MCP template.");
            output.WriteLine("CLAUDE.md, AGENTS.md, .claude/settings.json, .gitignore and global.json are created only when missing. Run it again after installing a new SDVKit version: unchanged SDVKit files are updated, locally edited ones are kept unless --force.");
            return Success;
        }

        string? labRoot = null;
        bool force = false;
        bool json = false;
        for (int index = 2; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--force" when !force:
                    force = true;
                    break;
                case string value when labRoot is null && !value.StartsWith("--", StringComparison.Ordinal):
                    labRoot = value;
                    break;
                default:
                    error.WriteLine(LabInitUsage);
                    return UsageError;
            }
        }

        if (!json)
        {
            error.WriteLine(LabInitUsage);
            return UsageError;
        }

        DoctorReport doctor = discoverInstallations();
        var protectedRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley", "Saves"),
        };
        if (doctor.Status == "ready")
        {
            protectedRoots.AddRange(doctor.Installations.Select(installation => installation.GamePath));
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "sdvkit.exe");
        string executable = File.Exists(bundled) ? bundled : Environment.ProcessPath ?? bundled;
        LabAgentKitReport report = LabAgentKit.Initialize(
            labRoot ?? Environment.CurrentDirectory,
            force,
            CurrentVersion,
            executable,
            protectedRoots);
        WriteJson(output, report);
        return report.State == "initialized" ? Success : InspectionFailed;
    }
}

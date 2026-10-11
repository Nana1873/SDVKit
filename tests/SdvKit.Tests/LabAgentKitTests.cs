using System.Text.Json;
using SdvKit.Cli.AgentKit;

namespace SdvKit.Tests;

public sealed class LabAgentKitTests
{
    private const string Version = "9.8.7";
    private static readonly string[] SkillNames =
    [
        "sdv-build-package", "sdv-content-patcher", "sdv-diagnose", "sdv-gmcm", "sdv-live-review",
        "sdv-multiplayer", "sdv-new-mod", "sdv-project-smoke", "sdv-smapi-csharp", "sdv-test-world",
    ];

    private static LabAgentKitReport Run(string root, bool force = false, string? exe = null, params string[] protectedRoots) =>
        LabAgentKit.Initialize(
            root,
            force,
            Version,
            exe ?? @"C:\Tools\SDVKit\sdvkit.exe",
            protectedRoots.Length > 0 ? protectedRoots : [@"C:\Users\x\AppData\Roaming\StardewValley\Saves", @"D:\Games\Stardew Valley"]);

    private static string Read(TemporaryDirectory lab, string relative) =>
        File.ReadAllText(Path.Combine(lab.Path, relative));

    [Fact]
    public void FirstRunWritesTheWholeKitWithRenderedPlaceholders()
    {
        using var lab = new TemporaryDirectory();
        LabAgentKitReport report = Run(lab.Path);

        Assert.Equal("initialized", report.State);
        Assert.All(report.Files, file => Assert.Equal("created", file.Action));
        foreach (string skill in SkillNames)
        {
            Assert.Contains($"name: {skill}", Read(lab, $".claude/skills/{skill}/SKILL.md"), StringComparison.Ordinal);
            Assert.Equal(Read(lab, $".claude/skills/{skill}/SKILL.md"), Read(lab, $".agents/skills/{skill}/SKILL.md"));
        }

        foreach (string path in Directory.EnumerateFiles(lab.Path, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("{{", File.ReadAllText(path), StringComparison.Ordinal);
        }

        Assert.Contains($"https://github.com/Nana1873/SDVKit/blob/v{Version}/docs", Read(lab, "SDVKIT.md"), StringComparison.Ordinal);
        Assert.StartsWith("@SDVKIT.md", Read(lab, "CLAUDE.md"), StringComparison.Ordinal);
        Assert.Contains(@"""C:\Tools\SDVKit\sdvkit.exe"" %*", Read(lab, "sdvkit.cmd"), StringComparison.Ordinal);
        Assert.Contains(".sdvkit/", Read(lab, ".gitignore"), StringComparison.Ordinal);
        Assert.Contains("sdvkit-guard.ps1", Read(lab, ".claude/settings.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonFilesStayValidAndCarryTheProtectedRoots()
    {
        using var lab = new TemporaryDirectory();
        Run(lab.Path);

        using JsonDocument mcp = JsonDocument.Parse(Read(lab, ".sdvkit/agent/claude-mcp.json"));
        Assert.Equal(@"C:\Tools\SDVKit\sdvkit.exe", mcp.RootElement.GetProperty("mcpServers").GetProperty("sdvkit-review").GetProperty("command").GetString());
        using JsonDocument guard = JsonDocument.Parse(Read(lab, ".claude/hooks/sdvkit-guard.json"));
        Assert.Equal(
            [@"C:\Users\x\AppData\Roaming\StardewValley\Saves", @"D:\Games\Stardew Valley"],
            guard.RootElement.GetProperty("protectedRoots").EnumerateArray().Select(value => value.GetString()));
        using JsonDocument settings = JsonDocument.Parse(Read(lab, ".claude/settings.json"));
        Assert.Equal(JsonValueKind.Object, settings.RootElement.GetProperty("hooks").ValueKind);
    }

    [Fact]
    public void LauncherUsesARelativePathForAnInstallationInsideTheLab()
    {
        using var lab = new TemporaryDirectory();
        string exe = Path.Combine(lab.Path, ".sdvkit", "tools", "SDVKit", "sdvkit.exe");
        Run(lab.Path, exe: exe);

        Assert.Contains(@"""%~dp0.sdvkit\tools\SDVKit\sdvkit.exe"" %*", Read(lab, "sdvkit.cmd"), StringComparison.Ordinal);
    }

    [Fact]
    public void RerunUpdatesNothingAndKeepsLocalEditsUnlessForced()
    {
        using var lab = new TemporaryDirectory();
        Run(lab.Path);
        File.AppendAllText(Path.Combine(lab.Path, "SDVKIT.md"), "local note\n");
        File.AppendAllText(Path.Combine(lab.Path, "CLAUDE.md"), "my rule\n");

        LabAgentKitReport rerun = Run(lab.Path);
        Assert.Equal("kept", rerun.Files.Single(file => file.Path == "SDVKIT.md").Action);
        Assert.Equal("exists", rerun.Files.Single(file => file.Path == "CLAUDE.md").Action);
        Assert.All(rerun.Files.Where(file => file.Path.Contains("skills/", StringComparison.Ordinal)), file => Assert.Equal("unchanged", file.Action));

        LabAgentKitReport forced = Run(lab.Path, force: true);
        Assert.Equal("updated", forced.Files.Single(file => file.Path == "SDVKIT.md").Action);
        Assert.DoesNotContain("local note", Read(lab, "SDVKIT.md"), StringComparison.Ordinal);
        Assert.Contains("my rule", Read(lab, "CLAUDE.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingUserFilesAreNeverReplacedWithoutForce()
    {
        using var lab = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(lab.Path, "AGENTS.md"), "user agents\n");
        File.WriteAllText(Path.Combine(lab.Path, "SDVKIT.md"), "user guide\n");

        LabAgentKitReport report = Run(lab.Path);

        Assert.Equal("exists", report.Files.Single(file => file.Path == "AGENTS.md").Action);
        Assert.Equal("kept", report.Files.Single(file => file.Path == "SDVKIT.md").Action);
        Assert.Equal("user agents\n", Read(lab, "AGENTS.md"));
        Assert.Equal("user guide\n", Read(lab, "SDVKIT.md"));
    }

    [Fact]
    public void ALabRootInsideAProtectedFolderIsRefused()
    {
        using var game = new TemporaryDirectory();
        string inside = Directory.CreateDirectory(Path.Combine(game.Path, "Mods", "Lab")).FullName;

        LabAgentKitReport report = Run(inside, protectedRoots: [game.Path]);

        Assert.Equal("failed", report.State);
        Assert.Contains("protected", report.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public void MissingGameInstallationIsReportedAsAWarning()
    {
        using var lab = new TemporaryDirectory();
        LabAgentKitReport report = Run(lab.Path, protectedRoots: [@"C:\Users\x\AppData\Roaming\StardewValley\Saves"]);

        Assert.Equal("initialized", report.State);
        Assert.Contains(report.Warnings, warning => warning.Contains("No ready game installation", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewVersionUpdatesUneditedFilesAndKeepsEditedOnes()
    {
        using var lab = new TemporaryDirectory();
        Run(lab.Path);
        File.AppendAllText(Path.Combine(lab.Path, ".claude", "skills", "sdv-diagnose", "SKILL.md"), "mine\n");

        LabAgentKitReport report = LabAgentKit.Initialize(
            lab.Path, false, "9.9.9", @"C:\Tools\SDVKit\sdvkit.exe", [@"C:\Saves", @"D:\Game"]);

        Assert.Equal("updated", report.Files.Single(file => file.Path == "SDVKIT.md").Action);
        Assert.Contains("blob/v9.9.9/docs", Read(lab, "SDVKIT.md"), StringComparison.Ordinal);
        Assert.Equal("kept", report.Files.Single(file => file.Path == ".claude/skills/sdv-diagnose/SKILL.md").Action);
        Assert.Contains("blob/v9.8.7/docs", Read(lab, ".claude/skills/sdv-diagnose/SKILL.md"), StringComparison.Ordinal);
        Assert.Contains("blob/v9.9.9/docs", Read(lab, ".agents/skills/sdv-diagnose/SKILL.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void StaleFilesAreRemovedOnlyInsideTheLabAndOnlyWhenUnchanged()
    {
        using var lab = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        Run(lab.Path);
        string staleSkill = Path.Combine(lab.Path, ".claude", "skills", "sdv-old", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(staleSkill)!);
        File.WriteAllText(staleSkill, "old skill");
        string victim = Path.Combine(outside.Path, "keep.txt");
        File.WriteAllText(victim, "user data");
        string manifestPath = Path.Combine(lab.Path, ".sdvkit", "agent-kit.json");
        string manifest = File.ReadAllText(manifestPath);
        string extra = $"\"files\": {{\n    \".claude/skills/sdv-old/SKILL.md\": \"{HashOf("old skill")}\",\n    \"{victim.Replace(@"\", @"\\", StringComparison.Ordinal)}\": \"{HashOf("user data")}\",\n    \"../escape.txt\": \"{HashOf("user data")}\",";
        File.WriteAllText(manifestPath, manifest.Replace("\"files\": {", extra, StringComparison.Ordinal));

        LabAgentKitReport report = Run(lab.Path);

        Assert.Equal("initialized", report.State);
        Assert.Equal("removed", report.Files.Single(file => file.Path == ".claude/skills/sdv-old/SKILL.md").Action);
        Assert.False(File.Exists(staleSkill));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void ForeignSeedFilesProduceWarnings()
    {
        using var lab = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(lab.Path, "CLAUDE.md"), "# My lab\n");
        File.WriteAllText(Path.Combine(lab.Path, "AGENTS.md"), "# My agents\n");
        File.WriteAllText(Path.Combine(lab.Path, ".gitignore"), "bin/\n");

        LabAgentKitReport report = Run(lab.Path);

        Assert.Contains(report.Warnings, warning => warning.StartsWith("CLAUDE.md does not mention SDVKIT.md", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.StartsWith("AGENTS.md does not mention SDVKIT.md", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.StartsWith(".gitignore does not exclude .sdvkit/", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameRootFromAnEarlierRunIsKeptWhenDiscoveryIsNotReady()
    {
        using var lab = new TemporaryDirectory();
        Run(lab.Path);

        LabAgentKitReport report = Run(lab.Path, protectedRoots: [@"C:\Users\x\AppData\Roaming\StardewValley\Saves"]);

        Assert.DoesNotContain(report.Warnings, warning => warning.StartsWith("No ready game installation was found, so", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("keeps the protected folders from the previous run", StringComparison.Ordinal));
        Assert.Contains(@"D:\\Games\\Stardew Valley", Read(lab, ".claude/hooks/sdvkit-guard.json"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Write", "file_path", @"D:\Games\Stardew Valley\Mods\X\manifest.json", 2)]
    [InlineData("Write", "file_path", @"C:\Users\Jörg\AppData\Roaming\StardewValley\Saves\Farm_1\Farm_1", 2)]
    [InlineData("Edit", "file_path", @"E:\lab\workspaces\M\assets\x.xnb", 2)]
    [InlineData("Write", "file_path", @"E:\lab\workspaces\M\ModEntry.cs", 0)]
    [InlineData("Bash", "command", "cp -r bin/M /d/Games/Stardew\\ Valley/Mods/", 2)]
    [InlineData("PowerShell", "command", @"Copy-Item out ""$env:APPDATA\StardewValley\Saves\x""", 2)]
    [InlineData("PowerShell", "command", @"Get-ChildItem ""D:\Games\Stardew Valley\Mods"" 2>$null", 0)]
    [InlineData("Bash", "command", @"dotnet build -p:GamePath=""D:\Games\Stardew Valley"" 2>&1 | tee build.log", 0)]
    [InlineData("Read", "file_path", @"D:\Games\Stardew Valley\Mods\X\manifest.json", 0)]
    [InlineData("Write", "file_path", "/d/Games/Stardew Valley/Mods/X/manifest.json", 2)]
    [InlineData("Bash", "command", @"echo x | tee -a ""D:\Games\Stardew Valley\Mods\X\config.json""", 2)]
    [InlineData("PowerShell", "command", @"Set-Content -Value '{}' -Path ""D:\Games\Stardew Valley\Mods\a.json""", 2)]
    [InlineData("Bash", "command", @"cp a ""$APPDATA/StardewValley/Saves/Farm_1/Farm_1""; ls ./.sdvkit/", 2)]
    [InlineData("PowerShell", "command", @"'x' > .sdvkit\profiles\single\StardewValley\Saves\x", 0)]
    public void GuardHookBlocksOnlyProtectedWrites(string tool, string field, string value, int expectedExit)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var lab = new TemporaryDirectory();
        Run(lab.Path, protectedRoots: [@"C:\Users\Jörg\AppData\Roaming\StardewValley\Saves", @"D:\Games\Stardew Valley"]);
        string hook = Path.Combine(lab.Path, ".claude", "hooks", "sdvkit-guard.ps1");
        string input = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tool_name"] = tool,
            ["tool_input"] = new Dictionary<string, string> { [field] = value },
        });
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", hook })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(input);
        process.StandardInput.BaseStream.Write(bytes);
        process.StandardInput.Close();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));

        Assert.True(expectedExit == process.ExitCode, $"exit {process.ExitCode}, stderr: {error}");
        if (expectedExit == 2)
        {
            Assert.Contains("SDVKit lab guard", error, StringComparison.Ordinal);
        }
    }

    private static string HashOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    [Fact]
    public void ReadmeSetupPromptMatchesSetupPromptFile()
    {
        string root = FindRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string prompt = File.ReadAllText(Path.Combine(root, "SETUP_PROMPT.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("```text\n" + prompt + "```", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDVKit.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the SDVKit repository.");
    }

    [Fact]
    public void EveryTemplateIsEmbeddedAndEverySkillHasFrontMatter()
    {
        IReadOnlyDictionary<string, string> templates = LabAgentKit.ReadTemplates();

        Assert.Contains("SDVKIT.md", templates.Keys);
        Assert.Contains("hooks/sdvkit-guard.ps1", templates.Keys);
        foreach (string skill in SkillNames)
        {
            string text = templates[$"skills/{skill}/SKILL.md"];
            Assert.StartsWith($"---\nname: {skill}\ndescription: ", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains($"`{skill}`", templates["SDVKIT.md"], StringComparison.Ordinal);
        }
    }
}

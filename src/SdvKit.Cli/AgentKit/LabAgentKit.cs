using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SdvKit.Cli.AgentKit;

internal sealed record LabAgentKitFile(string Path, string Action, string? Reason = null);

internal sealed record LabAgentKitReport(
    int SchemaVersion,
    string State,
    string LabRoot,
    string Version,
    IReadOnlyList<LabAgentKitFile> Files,
    IReadOnlyList<string> Warnings,
    string? Error = null);

/// <summary>
/// Writes SDVKit's agent layer (guide, skills, Claude Code hook, launcher, templates) into a lab root.
/// Managed files are updated when unchanged since the last run and kept when edited locally;
/// seed files (CLAUDE.md, AGENTS.md, settings, .gitignore, global.json) are only ever created.
/// </summary>
internal static class LabAgentKit
{
    public const int SchemaVersion = 1;
    private const string ResourcePrefix = "agent/";
    private const string ManifestPath = ".sdvkit/agent-kit.json";
    private const string GuardConfigPath = ".claude/hooks/sdvkit-guard.json";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly Dictionary<string, string> Seeds = new(StringComparer.Ordinal)
    {
        ["CLAUDE.template.md"] = "CLAUDE.md",
        ["AGENTS.template.md"] = "AGENTS.md",
        ["claude-settings.json"] = ".claude/settings.json",
        ["gitignore"] = ".gitignore",
        ["global.json"] = "global.json",
    };

    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["claude-mcp.json"] = ".sdvkit/agent/claude-mcp.json",
        ["hooks/sdvkit-guard.ps1"] = ".claude/hooks/sdvkit-guard.ps1",
    };

    public static LabAgentKitReport Initialize(
        string labRoot,
        bool force,
        string version,
        string executablePath,
        IReadOnlyList<string> protectedRoots)
    {
        string root = Path.GetFullPath(labRoot);
        var files = new List<LabAgentKitFile>();
        var warnings = new List<string>();
        try
        {
            if (!Directory.Exists(root))
            {
                return Failure(root, version, $"The lab root '{root}' does not exist.");
            }

            foreach (string protectedRoot in protectedRoots)
            {
                if (IsSameOrInside(root, protectedRoot))
                {
                    return Failure(root, version, $"A lab root must not be inside the protected folder '{protectedRoot}'.");
                }
            }

            Dictionary<string, string> rendered = Render(version, executablePath, root, protectedRoots);
            Dictionary<string, string> previous = ReadManifest(root);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach ((string path, string content) in rendered.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                bool seed = Seeds.ContainsValue(path);
                LabAgentKitFile result = seed
                    ? WriteSeed(root, path, content)
                    : WriteManaged(root, path, content, previous, force);
                files.Add(result);
                if (!seed)
                {
                    string target = Resolve(root, path);
                    current[path] = File.Exists(target) && result.Action != "kept"
                        ? Hash(File.ReadAllText(target))
                        : previous.TryGetValue(path, out string? kept) ? kept : Hash(content);
                }
            }

            foreach ((string stalePath, string staleHash) in previous)
            {
                if (rendered.ContainsKey(stalePath))
                {
                    continue;
                }

                string target = Resolve(root, stalePath);
                if (File.Exists(target) && Hash(File.ReadAllText(target)) == staleHash)
                {
                    File.Delete(target);
                    files.Add(new(stalePath, "removed", "No longer part of this SDVKit version."));
                }
                else if (File.Exists(target))
                {
                    files.Add(new(stalePath, "kept", "No longer part of this SDVKit version, but edited locally."));
                }
            }

            WriteManifest(root, version, current);

            string settings = Resolve(root, ".claude/settings.json");
            if (File.Exists(settings) && !File.ReadAllText(settings).Contains("sdvkit-guard.ps1", StringComparison.Ordinal))
            {
                warnings.Add("The existing .claude/settings.json does not register the SDVKit lab guard. Add the PreToolUse hook from SDVKit's agent/claude-settings.json to enable it.");
            }

            if (protectedRoots.Count < 2)
            {
                warnings.Add("No ready game installation was found, so the lab guard protects only the normal Saves folder, *.xnb files and game binaries. Run 'sdvkit lab init' again once 'sdvkit doctor' reports ready.");
            }

            return new(SchemaVersion, "initialized", root, version, files, warnings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure(root, version, exception.Message) with { Files = files };
        }
    }

    internal static IReadOnlyDictionary<string, string> ReadTemplates()
    {
        Assembly assembly = typeof(LabAgentKit).Assembly;
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames())
        {
            string normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            templates[normalized[ResourcePrefix.Length..]] = reader.ReadToEnd();
        }

        return templates;
    }

    private static Dictionary<string, string> Render(
        string version,
        string executablePath,
        string root,
        IReadOnlyList<string> protectedRoots)
    {
        string docs = $"https://github.com/Nana1873/SDVKit/blob/v{version}/docs";
        string launcherTarget = IsSameOrInside(executablePath, root)
            ? "%~dp0" + Path.GetRelativePath(root, executablePath)
            : executablePath;
        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string template, string text) in ReadTemplates())
        {
            foreach (string destination in Destinations(template))
            {
                bool json = destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                string exe = destination == "sdvkit.cmd" ? launcherTarget : executablePath;
                rendered[destination] = text
                    .Replace("{{version}}", version, StringComparison.Ordinal)
                    .Replace("{{docs}}", docs, StringComparison.Ordinal)
                    .Replace("{{exe}}", json ? JsonEncodedText.Encode(exe).ToString() : exe, StringComparison.Ordinal);
            }
        }

        rendered[GuardConfigPath] = JsonSerializer.Serialize(
            new { schemaVersion = SchemaVersion, protectedRoots },
            Indented) + "\n";
        return rendered;
    }

    private static IEnumerable<string> Destinations(string template)
    {
        if (Seeds.TryGetValue(template, out string? seed))
        {
            yield return seed;
        }
        else if (Renamed.TryGetValue(template, out string? renamed))
        {
            yield return renamed;
        }
        else if (template.StartsWith("skills/", StringComparison.Ordinal))
        {
            // Claude Code reads .claude/skills; Codex reads .agents/skills.
            yield return ".claude/" + template;
            yield return ".agents/" + template;
        }
        else
        {
            yield return template;
        }
    }

    private static LabAgentKitFile WriteSeed(string root, string path, string content)
    {
        string target = Resolve(root, path);
        if (File.Exists(target))
        {
            return new(path, "exists");
        }

        Write(target, content);
        return new(path, "created");
    }

    private static LabAgentKitFile WriteManaged(
        string root,
        string path,
        string content,
        Dictionary<string, string> previous,
        bool force)
    {
        string target = Resolve(root, path);
        if (!File.Exists(target))
        {
            Write(target, content);
            return new(path, "created");
        }

        string existing = File.ReadAllText(target);
        if (existing == content)
        {
            return new(path, "unchanged");
        }

        bool untouched = previous.TryGetValue(path, out string? recorded) && recorded == Hash(existing);
        if (!untouched && !force)
        {
            return new(path, "kept", "Edited locally or not written by SDVKit; rerun with --force to replace it.");
        }

        Write(target, content);
        return new(path, "updated");
    }

    private static void Write(string target, string content)
    {
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Refusing to write through the link '{target}'.");
        }

        File.WriteAllText(target, content, new UTF8Encoding(false));
    }

    private static void RejectReparsePoints(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Refusing to write below the link '{current.FullName}'.");
            }
        }
    }

    private static string Resolve(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static Dictionary<string, string> ReadManifest(string root)
    {
        string path = Resolve(root, ManifestPath);
        if (!File.Exists(path))
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("files").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // An unreadable manifest only means every differing managed file is treated as edited.
            return new(StringComparer.Ordinal);
        }
    }

    private static void WriteManifest(string root, string version, IReadOnlyDictionary<string, string> files) =>
        Write(
            Resolve(root, ManifestPath),
            JsonSerializer.Serialize(
                new { schemaVersion = SchemaVersion, version, files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary() },
                Indented) + "\n");

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool IsSameOrInside(string path, string folder)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static LabAgentKitReport Failure(string root, string version, string message) =>
        new(SchemaVersion, "failed", root, version, [], [], message);
}

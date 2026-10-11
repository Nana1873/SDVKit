using System.Text;
using System.Text.RegularExpressions;

namespace SdvKit.Tests;

public sealed partial class DocumentationLinkTests
{
    [Fact]
    public void RelativeLinksAndAnchorsResolve()
    {
        string root = FindRepositoryRoot();
        var broken = new List<string>();
        foreach (string file in MarkdownFiles(root))
        {
            string text = StripCode(File.ReadAllText(file));
            foreach (Match match in LinkPattern().Matches(text))
            {
                string target = match.Groups[1].Value;
                if (SchemePattern().IsMatch(target) || target.StartsWith("{{", StringComparison.Ordinal))
                {
                    continue;
                }

                int hash = target.IndexOf('#', StringComparison.Ordinal);
                string path = hash < 0 ? target : target[..hash];
                string anchor = hash < 0 ? "" : target[(hash + 1)..];
                string destination = path.Length == 0
                    ? file
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(path)));
                string source = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!File.Exists(destination) && !Directory.Exists(destination))
                {
                    broken.Add($"{source}: {target}");
                }
                else if (anchor.Length > 0 && destination.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !Anchors(destination).Contains(anchor))
                {
                    broken.Add($"{source}: {target} (missing anchor)");
                }
            }
        }

        Assert.True(broken.Count == 0, "Broken documentation links:\n" + string.Join("\n", broken));
    }

    [Theory]
    [InlineData("Start a review", "start-a-review")]
    [InlineData("CP format and `canonical` asset names", "cp-format-and-canonical-asset-names")]
    [InlineData("Leave and rejoin the network farmhand", "leave-and-rejoin-the-network-farmhand")]
    [InlineData("Observed behavior (2026-09-12)", "observed-behavior-2026-09-12")]
    public void AnchorsFollowGitHubSlugs(string heading, string expected)
    {
        Assert.Equal(expected, Slug(heading));
    }

    private static IEnumerable<string> MarkdownFiles(string root)
    {
        foreach (string name in new[] { "README.md", "CONTRIBUTING.md", "CHANGELOG.md", "AGENTS.md", "CLAUDE.md" })
        {
            yield return Path.Combine(root, name);
        }

        foreach (string folder in new[] { "docs", "agent" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.md", SearchOption.AllDirectories))
            {
                if (!file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".sdvkit"))
                {
                    yield return file;
                }
            }
        }
    }

    private static HashSet<string> Anchors(string file)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in HeadingPattern().Matches(FencePattern().Replace(File.ReadAllText(file), "")))
        {
            string slug = Slug(match.Groups[1].Value);
            int count = seen.GetValueOrDefault(slug);
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
            seen[slug] = count + 1;
        }

        return anchors;
    }

    // GitHub's heading anchors: lower case, inline markup and punctuation dropped, spaces as hyphens.
    private static string Slug(string heading)
    {
        string text = InlineLinkPattern().Replace(heading.Trim(), "$1").ToLowerInvariant();
        var slug = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    private static string StripCode(string text) => InlineCodePattern().Replace(FencePattern().Replace(text, ""), "");

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDVKit.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex InlineLinkPattern();

    [GeneratedRegex(@"^\s*```.*?^\s*```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodePattern();
}

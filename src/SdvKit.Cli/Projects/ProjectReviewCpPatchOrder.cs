using System.Globalization;
using System.Text.RegularExpressions;

namespace SdvKit.Cli;

internal static partial class ProjectReviewCpDiagnosis
{
    // Pinned CP 2.9.1 DumpCommand at b2d750f944dff2c0a540a02dd2af653b09e378c4.
    private const string AppliedHeading = "Here are the active patches grouped by their current target value. Within each group, patches are listed in the expected apply order and the checkbox indicates whether each patch is currently applied. See `patch summary` for more info about each patch, including reasons it may not be applied.";
    private const string OrderHeading = "Here's the global patch definition order across all loaded content packs, which affects the order that patches are applied. The 'order' column is the patch's global position in the order; the 'index path' column is Content Patcher's internal hierarchical definition order.";
    private static readonly Regex AppliedRow = new(@"^   \[(X| )\] (Load|EditData|EditImage|EditMap|Include) (?<path>\S.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OrderRow = new(@"^   (?<position>[1-9][0-9]*) +(?<index>[0-9]+(?: > [0-9]+)*) +(?<path>\S.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OrderColumns = new(@"^   order +index path +patch$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OrderDivider = new(@"^   -{5,} +-{10,} +-----$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static CpResponse InterpretDump(string level, string time, string message, string asset,
        string dump, IReadOnlyList<string>? appliedPaths, DateTimeOffset started)
    {
        CpResponse Result(string state, string? code, IReadOnlyList<string> messages, int withheld = 0, bool truncated = false) =>
            new(state, code, true, true, started, DateTimeOffset.UtcNow, time, messages, withheld, truncated, []);
        CpResponse Unsupported() => Result("incomplete", "cpOutputUnsupported", []);
        // Preserve leading whitespace (provider padding); discard only trailing blank lines.
        string[] lines = message.Trim('\r', '\n').Split('\n');
        if (level != "INFO" || lines.Length == 0) return Unsupported();
        if (lines.Length > 256 || lines.Any(l => l.Length > 1024))
            return Result("incomplete", "cpOrderOutputTruncated", [], truncated: true);
        var selected = new List<string>();
        if (dump == "applied")
        {
            if (lines[0] != AppliedHeading) return Unsupported();
            selected.Add(lines[0]);
            selected.Add(asset);
            selected.Add(new string('-', asset.Length));
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            int i = 1;
            while (i < lines.Length)
            {
                if (lines[i++] != "" || i + 2 >= lines.Length) return Unsupported();
                string target = lines[i++];
                // Target names may be localized/custom. They are only used for equality,
                // never dispatched, inspected or returned outside the selected group.
                if (target.Length == 0 || !targets.Add(target) || lines[i++] != new string('-', target.Length)) return Unsupported();
                int count = 0;
                while (i < lines.Length && lines[i] != "")
                {
                    var row = AppliedRow.Match(lines[i]);
                    if (!row.Success || !paths.Add(target + "\n" + row.Groups["path"].Value)) return Unsupported();
                    if (target.Equals(asset, StringComparison.OrdinalIgnoreCase)) selected.Add(lines[i]);
                    count++;
                    i++;
                }
                if (count == 0) return Unsupported();
            }
        }
        else if (dump == "order")
        {
            // CP's Max() calls cannot emit a successful order report with no rows.
            if (lines.Length < 5 || lines[0] != OrderHeading || lines[1] != ""
                || !OrderColumns.IsMatch(lines[2]) || !OrderDivider.IsMatch(lines[3]) || appliedPaths is null) return Unsupported();
            selected.Add(lines[0]);
            selected.Add(lines[2]);
            selected.Add(lines[3]);
            var remaining = appliedPaths.ToHashSet(StringComparer.Ordinal);
            if (remaining.Count != appliedPaths.Count) return Unsupported();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 4; i < lines.Length; i++)
            {
                var row = OrderRow.Match(lines[i]);
                if (!row.Success || !int.TryParse(row.Groups["position"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int position)
                    || position != i - 3 || !paths.Add(row.Groups["path"].Value)) return Unsupported();
                if (remaining.Remove(row.Groups["path"].Value)) selected.Add(lines[i]);
            }
            if (remaining.Count != 0) return Result("incomplete", "cpOrderSelectionChanged", []);
        }
        else return Unsupported();

        // A partial order can misidentify the last patch. Never publish it as ready.
        var visible = new List<string>();
        int withheld = 0;
        bool truncated = false;
        foreach (string line in selected)
        {
            string? safe = ProjectReviewLogDiagnostics.DiscloseLine(line, out bool privateContext, cpTokenMetadata: true);
            if (privateContext || safe is null) { withheld++; continue; }
            if (safe.Length > 1024 || visible.Count >= 256) { truncated = true; continue; }
            visible.Add(safe);
        }
        if (withheld > 0 || truncated)
            return Result("incomplete", truncated ? "cpOrderOutputTruncated" : "cpOrderPrivateContextWithheld", [], withheld, truncated);
        return Result("ready", null, visible);
    }
}

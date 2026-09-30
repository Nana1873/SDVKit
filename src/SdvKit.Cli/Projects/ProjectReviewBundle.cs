using System.Text.Json;

namespace SdvKit.Cli;

internal static partial class ProjectModStager
{
    private static bool HasDuplicateManifestProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || element.EnumerateObject().Any(property => HasDuplicateManifestProperties(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateManifestProperties),
        _ => false,
    };

    private static ProjectReviewProblem? ValidateReadyBundle(
        ProjectInspectionReport inspection, IReadOnlyList<string> packs)
    {
        ProjectReviewProblem Invalid() => ReviewProblem("reviewBundleInvalid", inspection.Root,
            "A ready bundle requires one code mod at its root or in a direct child directory, and every other manifest explicitly selected with --content-pack as a direct child pack. Source projects, missing members, nested mods, and linked paths are unsupported.");
        if (inspection.Problems.Count > 0 || inspection.ProjectFiles.Count != 0
            || inspection.Kind != ProjectInspectionReport.Hybrid
            || packs.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != packs.Count
            || packs.Any(pack => !PathEquals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(pack))!, inspection.Root)
                || !Directory.Exists(pack) || ProjectChecker.HasLinkedAncestor(pack)))
            return Invalid();

        ProjectManifestSummary[] code = inspection.Manifests.Where(manifest => manifest.Kind == ProjectInspectionReport.SmapiMod).ToArray();
        if (code.Length != 1 || code[0].Path.Split('/').Length > 2 || inspection.Manifests.Count != packs.Count + 1)
            return Invalid();
        foreach (string pack in packs)
        {
            string manifestPath = Path.GetRelativePath(inspection.Root, Path.Combine(pack, "manifest.json")).Replace('\\', '/');
            if (!inspection.Manifests.Any(manifest => manifest.Path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)
                && manifest.Kind == ProjectInspectionReport.ContentPack))
                return Invalid();
        }
        return null;
    }
}

using System.Security;

namespace SdvKit.Cli.LiveLab;

internal sealed record LiveLabPaths(
    string ProjectRoot,
    string SingleRoot,
    string ModsPath,
    string RuntimePath,
    string BuildPath,
    string StatePath,
    string StatusPath,
    string StopRequestPath,
    string StandardOutputPath,
    string StandardErrorPath,
    string AlwaysOnModPath)
{
    public static LiveLabPaths Resolve(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string absoluteProjectRoot =
            ProjectPathCanonicalizer.CanonicalizeExistingDirectory(
                Path.GetFullPath(projectRoot));

        string managedRoot = Path.Combine(absoluteProjectRoot, ".sdvkit");
        string labRoot = Path.Combine(managedRoot, "lab");
        string singleRoot = Path.Combine(labRoot, "single");
        string modsPath = Path.Combine(singleRoot, "mods");
        string runtimePath = Path.Combine(singleRoot, "runtime");
        string buildPath = Path.Combine(singleRoot, "build");

        var paths = new LiveLabPaths(
            absoluteProjectRoot,
            singleRoot,
            modsPath,
            runtimePath,
            buildPath,
            Path.Combine(runtimePath, "state.json"),
            Path.Combine(runtimePath, "always-on-status.json"),
            Path.Combine(runtimePath, "stop.request"),
            Path.Combine(runtimePath, "smapi.stdout.log"),
            Path.Combine(runtimePath, "smapi.stderr.log"),
            Path.Combine(modsPath, "SDVKit.AlwaysOn"));
        paths.RejectExistingManagedReparsePoints();
        return paths;
    }

    public void EnsureDirectories()
    {
        RejectExistingManagedReparsePoints();
        EnsureDirectory(Path.Combine(ProjectRoot, ".sdvkit"));
        EnsureDirectory(Path.Combine(ProjectRoot, ".sdvkit", "lab"));
        EnsureDirectory(SingleRoot);
        EnsureDirectory(ModsPath);
        EnsureDirectory(RuntimePath);
        EnsureDirectory(BuildPath);
        RejectExistingManagedReparsePoints();
    }

    internal static void RejectReparsePointsBelow(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            RejectReparsePoint(directory);

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"The managed live-lab path contains a reparse point: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private void RejectExistingManagedReparsePoints()
    {
        foreach (string path in new[]
        {
            Path.Combine(ProjectRoot, ".sdvkit"),
            Path.Combine(ProjectRoot, ".sdvkit", "lab"),
            SingleRoot,
        })
        {
            if (!TryGetAttributes(path, out FileAttributes attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"The managed live-lab path is a reparse point: {path}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(
                    $"The managed live-lab path is not a directory: {path}");
            }
        }

        RejectReparsePointsBelow(SingleRoot);
    }

    private static void EnsureDirectory(string path)
    {
        if (TryGetAttributes(path, out FileAttributes attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"The managed live-lab path is a reparse point: {path}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(
                    $"The managed live-lab path is not a directory: {path}");
            }

            return;
        }

        Directory.CreateDirectory(path);
        RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The managed live-lab path is a reparse point: {path}");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException)
        {
            throw new InvalidOperationException(
                $"The managed live-lab path could not be inspected: {path}",
                exception);
        }
    }
}

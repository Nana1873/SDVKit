using System.Security;

namespace SdvKit.Cli.LiveLab;

internal sealed record LiveLabPaths(
    string ProjectRoot,
    string SingleRoot,
    string ModsPath,
    string RuntimePath,
    string BuildPath,
    string UserProfilePath,
    string StatePath,
    string StatusPath,
    string StopRequestPath,
    string StandardOutputPath,
    string StandardErrorPath,
    string AlwaysOnModPath,
    string TestSaveRoot,
    string TestSaveManifestPath,
    string TestSaveWorkPath,
    string TestSaveBaselinePath,
    string TestSaveScenarioLogPath)
{
    public string RoamingAppDataPath =>
        Path.Combine(UserProfilePath, "AppData", "Roaming");

    public string LocalAppDataPath =>
        Path.Combine(UserProfilePath, "AppData", "Local");

    public string StardewDataPath =>
        Path.Combine(RoamingAppDataPath, "StardewValley");

    public string SavesPath => Path.Combine(StardewDataPath, "Saves");

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
        string userProfilePath = Path.Combine(labRoot, "profiles", "single");
        string testSaveRoot = Path.Combine(singleRoot, "test-save");

        var paths = new LiveLabPaths(
            absoluteProjectRoot,
            singleRoot,
            modsPath,
            runtimePath,
            buildPath,
            userProfilePath,
            Path.Combine(runtimePath, "state.json"),
            Path.Combine(runtimePath, "always-on-status.json"),
            Path.Combine(runtimePath, "stop.request"),
            Path.Combine(runtimePath, "smapi.stdout.log"),
            Path.Combine(runtimePath, "smapi.stderr.log"),
            Path.Combine(modsPath, "SDVKit.AlwaysOn"),
            testSaveRoot,
            Path.Combine(testSaveRoot, "fixture.json"),
            Path.Combine(testSaveRoot, "work"),
            Path.Combine(testSaveRoot, "baseline"),
            Path.Combine(runtimePath, "test-save-scenario.log"));
        paths.RejectExistingManagedReparsePoints();
        return paths;
    }

    public static LiveLabPaths ResolveNetworkRole(
        LiveLabPaths singlePaths,
        string role)
    {
        ArgumentNullException.ThrowIfNull(singlePaths);
        if (!NetworkTwoContract.IsRole(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        string networkRoot = Path.Combine(
            singlePaths.ProjectRoot,
            ".sdvkit",
            "lab",
            NetworkTwoContract.Topology);
        string roleRoot = Path.Combine(networkRoot, role);
        string modsPath = Path.Combine(roleRoot, "mods");
        string runtimePath = Path.Combine(roleRoot, "runtime");
        string buildPath = Path.Combine(roleRoot, "build");
        string userProfilePath = Path.Combine(
            singlePaths.ProjectRoot,
            ".sdvkit",
            "lab",
            "profiles",
            NetworkTwoContract.Topology,
            role);
        var paths = new LiveLabPaths(
            singlePaths.ProjectRoot,
            roleRoot,
            modsPath,
            runtimePath,
            buildPath,
            userProfilePath,
            Path.Combine(runtimePath, "state.json"),
            Path.Combine(runtimePath, "always-on-status.json"),
            Path.Combine(runtimePath, "stop.request"),
            Path.Combine(runtimePath, "smapi.stdout.log"),
            Path.Combine(runtimePath, "smapi.stderr.log"),
            Path.Combine(modsPath, "SDVKit.AlwaysOn"),
            singlePaths.TestSaveRoot,
            singlePaths.TestSaveManifestPath,
            singlePaths.TestSaveWorkPath,
            singlePaths.TestSaveBaselinePath,
            Path.Combine(runtimePath, "test-save-scenario.log"));
        paths.RejectExistingManagedReparsePoints();
        return paths;
    }

    public void EnsureDirectories()
    {
        RejectExistingManagedReparsePoints();
        EnsureDirectory(Path.Combine(ProjectRoot, ".sdvkit"));
        EnsureDirectory(Path.Combine(ProjectRoot, ".sdvkit", "lab"));
        EnsureDirectory(Path.GetDirectoryName(SingleRoot)
            ?? throw new InvalidOperationException("The live-lab instance root has no parent."));
        EnsureDirectory(SingleRoot);
        EnsureDirectory(ModsPath);
        EnsureDirectory(RuntimePath);
        EnsureDirectory(BuildPath);
        EnsureDirectory(Path.Combine(ProjectRoot, ".sdvkit", "lab", "profiles"));
        EnsureDirectory(Path.GetDirectoryName(UserProfilePath)
            ?? throw new InvalidOperationException("The lab user-profile root has no parent."));
        EnsureDirectory(UserProfilePath);
        EnsureDirectory(Path.Combine(UserProfilePath, "AppData"));
        EnsureDirectory(RoamingAppDataPath);
        EnsureDirectory(LocalAppDataPath);
        EnsureDirectory(StardewDataPath);
        EnsureDirectory(SavesPath);
        RejectExistingManagedReparsePoints();
    }

    internal void RejectUserProfileReparsePoints()
    {
        if (Directory.Exists(UserProfilePath))
        {
            RejectReparsePointsBelow(UserProfilePath);
        }
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
            Path.GetDirectoryName(SingleRoot)
                ?? throw new InvalidOperationException("The live-lab instance root has no parent."),
            SingleRoot,
            Path.Combine(ProjectRoot, ".sdvkit", "lab", "profiles"),
            Path.GetDirectoryName(UserProfilePath)
                ?? throw new InvalidOperationException("The lab user-profile root has no parent."),
            UserProfilePath,
            Path.Combine(UserProfilePath, "AppData"),
            RoamingAppDataPath,
            LocalAppDataPath,
            StardewDataPath,
            SavesPath,
        })
        {
            if (!TryGetAttributes(path, out FileAttributes attributes))
            {
                continue;
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

        if (Directory.Exists(SingleRoot))
        {
            RejectReparsePointsBelow(SingleRoot);
        }
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

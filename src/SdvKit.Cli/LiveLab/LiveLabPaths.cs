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
    string GameSideModPath,
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
            Path.Combine(runtimePath, "game-side-status.json"),
            Path.Combine(runtimePath, "stop.request"),
            Path.Combine(runtimePath, "smapi.stdout.log"),
            Path.Combine(runtimePath, "smapi.stderr.log"),
            Path.Combine(modsPath, "SDVKit.GameSide"),
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
            Path.Combine(runtimePath, "game-side-status.json"),
            Path.Combine(runtimePath, "stop.request"),
            Path.Combine(runtimePath, "smapi.stdout.log"),
            Path.Combine(runtimePath, "smapi.stderr.log"),
            Path.Combine(modsPath, "SDVKit.GameSide"),
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

    public string LegacyGameSideModPath =>
        Path.Combine(ModsPath, LoadedModsContract.LegacyGameSideUniqueId);

    private const string LegacyGameSideAssembly = "SdvKit.AlwaysOn.dll";
    private const string LegacyGameSideManifest = "manifest.json";

    /// <summary>
    /// Whether <see cref="LegacyGameSideModPath"/> holds the game-side mod that SDVKit 1.x
    /// installed under its former identity, or what is left of it after an interrupted removal:
    /// a plain directory with at most its manifest and assembly. A present manifest must carry
    /// the legacy ID and entry DLL. The directory name itself is reserved for user mods.
    /// </summary>
    internal bool IsLegacyGameSideInstall()
    {
        string path = LegacyGameSideModPath;
        if (!TryGetAttributes(path, out FileAttributes attributes)
            || (attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            string name = Path.GetFileName(entry);
            if ((!string.Equals(name, LegacyGameSideManifest, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, LegacyGameSideAssembly, StringComparison.OrdinalIgnoreCase))
                || !TryGetAttributes(entry, out FileAttributes entryAttributes)
                || (entryAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }
        }

        string manifestPath = Path.Combine(path, LegacyGameSideManifest);
        if (!File.Exists(manifestPath))
        {
            return true;
        }

        try
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            System.Text.Json.JsonElement root = manifest.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("UniqueID", out System.Text.Json.JsonElement id)
                && string.Equals(id.GetString(), LoadedModsContract.LegacyGameSideUniqueId, StringComparison.Ordinal)
                && root.TryGetProperty("EntryDll", out System.Text.Json.JsonElement entryDll)
                && string.Equals(entryDll.GetString(), LegacyGameSideAssembly, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a legacy 1.x game-side install (see <see cref="IsLegacyGameSideInstall"/>).
    /// The assembly goes first and the manifest last, so an interrupted removal still leaves a
    /// recognizable remainder that the next launch preparation finishes. Anything else is left
    /// untouched for the existing ownership checks. Call this only while preparing a new launch,
    /// never for status or stop.
    /// </summary>
    internal bool RemoveLegacyGameSideDirectory()
    {
        if (!IsLegacyGameSideInstall())
        {
            return false;
        }

        string path = LegacyGameSideModPath;
        File.Delete(Path.Combine(path, LegacyGameSideAssembly));
        File.Delete(Path.Combine(path, LegacyGameSideManifest));
        Directory.Delete(path, recursive: false);
        return true;
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
                // Atomic writers can remove their transient replacement file after
                // enumeration but before inspection. A vanished entry is no longer
                // a path we can follow; every observed reparse point and every
                // non-missing inspection failure still fails closed.
                if (!TryGetAttributes(entry, out FileAttributes attributes))
                {
                    continue;
                }

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

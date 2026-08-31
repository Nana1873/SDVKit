namespace SdvKit.Cli.LiveLab;

internal sealed record NetworkTwoModBuildResult(
    bool Succeeded,
    AlwaysOnBuildResult HostBuild,
    string? BuildIdentity,
    string? Error);

internal sealed class NetworkTwoModBuildPreparer
{
    private const string AssemblyFileName = "SdvKit.AlwaysOn.dll";
    private const string ManifestFileName = "manifest.json";

    private readonly IAlwaysOnBuilder _builder;

    public NetworkTwoModBuildPreparer()
        : this(new AlwaysOnBuilder())
    {
    }

    internal NetworkTwoModBuildPreparer(IAlwaysOnBuilder builder)
    {
        _builder = builder;
    }

    public NetworkTwoModBuildResult Prepare(
        string gamePath,
        LiveLabPaths hostPaths,
        LiveLabPaths farmhandPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(farmhandPaths);

        if (!PathEquals(hostPaths.ProjectRoot, farmhandPaths.ProjectRoot))
        {
            throw new ArgumentException(
                "Host and farmhand must belong to the same live-lab project.",
                nameof(farmhandPaths));
        }

        if (PathEquals(hostPaths.SingleRoot, farmhandPaths.SingleRoot)
            || PathEquals(hostPaths.AlwaysOnModPath, farmhandPaths.AlwaysOnModPath))
        {
            throw new ArgumentException(
                "Host and farmhand must use separate managed live-lab paths.",
                nameof(farmhandPaths));
        }

        AlwaysOnBuildResult hostBuild = _builder.BuildAndInstall(gamePath, hostPaths);
        if (!hostBuild.Succeeded)
        {
            return Failure(hostBuild, hostBuild.Error ?? "The host AlwaysOn build failed.");
        }

        string hostIdentity;
        try
        {
            VerifyDeclaredModContents(hostPaths.AlwaysOnModPath);
            hostIdentity = ModBuildIdentity.Compute(hostPaths.AlwaysOnModPath);

            farmhandPaths.EnsureDirectories();
            LiveLabPaths.RejectReparsePointsBelow(farmhandPaths.SingleRoot);
            RecreateFarmhandModDirectory(farmhandPaths.AlwaysOnModPath);
            CopyDeclaredFile(
                hostPaths.AlwaysOnModPath,
                farmhandPaths.AlwaysOnModPath,
                AssemblyFileName);
            CopyDeclaredFile(
                hostPaths.AlwaysOnModPath,
                farmhandPaths.AlwaysOnModPath,
                ManifestFileName);
            LiveLabPaths.RejectReparsePointsBelow(farmhandPaths.SingleRoot);

            VerifyDeclaredModContents(farmhandPaths.AlwaysOnModPath);
            string farmhandIdentity = ModBuildIdentity.Compute(farmhandPaths.AlwaysOnModPath);
            if (!string.Equals(hostIdentity, farmhandIdentity, StringComparison.Ordinal))
            {
                return Failure(
                    hostBuild,
                    "Host and farmhand AlwaysOn build identities do not match.");
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            return Failure(
                hostBuild,
                $"The declared AlwaysOn build could not be prepared for network-2: {exception.Message}");
        }

        return new NetworkTwoModBuildResult(
            true,
            hostBuild,
            hostIdentity,
            null);
    }

    private static NetworkTwoModBuildResult Failure(
        AlwaysOnBuildResult hostBuild,
        string error)
    {
        return new NetworkTwoModBuildResult(false, hostBuild, null, error);
    }

    private static void RecreateFarmhandModDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void CopyDeclaredFile(
        string sourceDirectory,
        string destinationDirectory,
        string fileName)
    {
        File.Copy(
            Path.Combine(sourceDirectory, fileName),
            Path.Combine(destinationDirectory, fileName),
            overwrite: false);
    }

    private static void VerifyDeclaredModContents(string modPath)
    {
        string[] entries = Directory.GetFileSystemEntries(modPath)
            .Select(path => Path.GetFileName(path)
                ?? throw new InvalidOperationException(
                    $"The declared AlwaysOn mod contains an invalid path: {path}"))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        string[] expected =
        [
            AssemblyFileName,
            ManifestFileName,
        ];

        if (!entries.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The declared AlwaysOn mod must contain only SdvKit.AlwaysOn.dll and manifest.json.");
        }
    }

    private static bool PathEquals(string left, string right)
    {
        string absoluteLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string absoluteRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(
            absoluteLeft,
            absoluteRight,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}

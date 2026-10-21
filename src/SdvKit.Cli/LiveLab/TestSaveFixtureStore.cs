using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SdvKit.Cli.LiveLab;

internal sealed record TestSavePreparation(TestSaveLaunchState LaunchState);

internal sealed record TestSaveCleanupResult(
    IReadOnlyList<string> ArchivedLogPaths,
    bool ScenarioLogArchived);

internal interface ITestSaveFixtureStore
{
    TestSavePreparation PrepareForStart();

    TestSavePreparation PrepareReviewForStart(bool resetFromBaseline);

    TestSaveCleanupResult CompleteStopped(
        TestSaveLaunchState launch,
        string launchId);

    TestSaveCleanupResult AbortStopped(
        TestSaveLaunchState launch,
        string launchId);
}

internal sealed class TestSaveFixtureStore : ITestSaveFixtureStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly LiveLabPaths _paths;
    private readonly string _savesRoot;
    private readonly IDirectChildJunction _junction;
    private readonly Func<string> _createId;
    private readonly Func<long> _createUniqueGameId;
    private readonly Action? _afterWorkInstalledForTest;

    public TestSaveFixtureStore(LiveLabPaths paths)
        : this(
            paths,
            paths.SavesPath,
            new WindowsDirectChildJunction(paths.TestSaveRoot),
            () => Guid.NewGuid().ToString("N"),
            CreateUniqueGameId)
    {
    }

    internal TestSaveFixtureStore(
        LiveLabPaths paths,
        string savesRoot,
        IDirectChildJunction junction,
        Func<string> createId,
        Func<long> createUniqueGameId,
        Action? afterWorkInstalledForTest = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _savesRoot = Path.GetFullPath(savesRoot);
        _junction = junction ?? throw new ArgumentNullException(nameof(junction));
        _createId = createId ?? throw new ArgumentNullException(nameof(createId));
        _createUniqueGameId = createUniqueGameId
            ?? throw new ArgumentNullException(nameof(createUniqueGameId));
        _afterWorkInstalledForTest = afterWorkInstalledForTest;
    }

    public TestSavePreparation PrepareForStart()
    {
        TestSaveIdentity identity = PrepareFixtureAccess(allowCreateIdentity: true);
        RemoveStaleCreationCopy(FindStaleCreationCopy(identity));

        string mode;
        if (Directory.Exists(_paths.TestSaveBaselinePath))
        {
            VerifyOwnedPayload(_paths.TestSaveBaselinePath, identity);
            ResetWorkFromBaseline(identity);
            mode = TestSaveContract.ScenarioMode;
        }
        else
        {
            PrepareEmptyWork(identity);
            mode = TestSaveContract.CreateMode;
        }

        return Activate(identity, mode);
    }

    public TestSavePreparation PrepareReviewForStart(bool resetFromBaseline)
    {
        if (!File.Exists(_paths.TestSaveManifestPath)
            || !Directory.Exists(_paths.TestSaveBaselinePath))
        {
            throw new InvalidOperationException(
                "Interactive review requires the existing disposable test-save baseline. Prepare it with 'sdvkit lab test-save --topology single --json'.");
        }

        TestSaveIdentity identity = PrepareFixtureAccess(allowCreateIdentity: false);
        VerifyOwnedPayload(_paths.TestSaveBaselinePath, identity);
        VerifyRequiredStardewFiles(_paths.TestSaveBaselinePath, identity);
        if (resetFromBaseline)
        {
            ResetWorkFromBaseline(identity);
        }
        else
        {
            VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
            VerifyRequiredStardewFiles(_paths.TestSaveWorkPath, identity);
        }

        return Activate(identity, TestSaveContract.ReviewMode);
    }

    // Restores the work save and the language of this profile and of the other given profiles
    // (the network-2 farmhand). Everything is checked before the first change.
    public void ResetReview(params string[] otherStardewDataPaths)
    {
        if (!File.Exists(_paths.TestSaveManifestPath)
            || !Directory.Exists(_paths.TestSaveBaselinePath))
        {
            throw new InvalidOperationException(
                "Interactive review requires the existing disposable test-save baseline. Prepare it with 'sdvkit lab test-save --topology single --json'.");
        }

        TestSaveIdentity identity = PrepareFixtureAccess(allowCreateIdentity: false);
        string? staleCopy = FindStaleCreationCopy(identity);
        string[] profiles = [_paths.StardewDataPath, .. otherStardewDataPaths];
        foreach (string profile in profiles)
        {
            LabWindowPreferences.VerifyLanguageRestorable(profile);
        }

        ResetWorkFromBaseline(identity);
        _junction.VerifyInactive(
            _savesRoot,
            identity.SaveId,
            _paths.TestSaveWorkPath);
        RemoveStaleCreationCopy(staleCopy);
        foreach (string profile in profiles)
        {
            LabWindowPreferences.RestoreDefaultLanguage(profile);
        }
    }

    // SDVKit 2.1.0 and earlier let Stardew's first serialization of a new fixture avoid the
    // fixture's slot binding, which left a '<farm>2_<id>' copy that the Load menu lists in
    // arbitrary order beside the fixture (#341). Only that exact, marked copy is removed.
    private string? FindStaleCreationCopy(TestSaveIdentity identity)
    {
        string name = $"{identity.FarmName}2_{identity.UniqueGameId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        string path = Path.Combine(_savesRoot, name);
        if (!Directory.Exists(path))
        {
            return File.Exists(path)
                ? throw new TestSaveStaleCopyException($"The stale fixture copy '{name}' is not a directory; SDVKit leaves it alone.")
                : null;
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("It is a link.");
            }

            LiveLabPaths.RejectReparsePointsBelow(path);
            string[] allowed = [name, name + "_old", "SaveGameInfo", "SaveGameInfo_old"];
            if (Directory.EnumerateDirectories(path).Any()
                || Directory.EnumerateFiles(path).Any(file => !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                || !File.Exists(Path.Combine(path, name)))
            {
                throw new InvalidDataException("It holds other entries than one Stardew save.");
            }

            XElement farmer = ReadSaveGameInfo(Path.Combine(path, "SaveGameInfo"));
            if (ReadMarker(farmer, TestSaveContract.FixtureMarkerKey) != identity.FixtureId
                || ReadMarker(farmer, TestSaveContract.WorkspaceOwnerMarkerKey) != identity.WorkspaceOwnerId)
            {
                throw new InvalidDataException("Its fixture markers differ from the registered fixture.");
            }
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException
            or XmlException)
        {
            throw new TestSaveStaleCopyException(
                $"The isolated profile holds '{name}', which is not SDVKit's own stale copy of this fixture; SDVKit leaves it alone. {exception.Message}");
        }

        return path;
    }

    private static void RemoveStaleCreationCopy(string? path)
    {
        if (path is not null)
        {
            LiveLabPaths.RejectReparsePointsBelow(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static XElement ReadSaveGameInfo(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using XmlReader reader = XmlReader.Create(path, settings);
        XElement root = XDocument.Load(reader).Root
            ?? throw new InvalidDataException("Its SaveGameInfo is empty.");
        return string.Equals(root.Name.LocalName, "Farmer", StringComparison.Ordinal)
            ? root
            : throw new InvalidDataException("Its SaveGameInfo is not a Stardew farmer.");
    }

    private static string? ReadMarker(XElement farmer, string key)
    {
        string?[] values = (farmer.Element("modData")?.Elements("item") ?? [])
            .Where(item => string.Equals(item.Element("key")?.Element("string")?.Value, key, StringComparison.Ordinal))
            .Select(item => item.Element("value")?.Element("string")?.Value)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    internal string SelectInspectionSource(string selection, out TestSaveIdentity identity)
    {
        string payload = selection switch
        {
            "baseline" => _paths.TestSaveBaselinePath,
            "work" => _paths.TestSaveWorkPath,
            _ => throw new InvalidDataException("Select baseline or work."),
        };
        SaveInspector.RequirePlainAncestors(_paths.TestSaveManifestPath);
        SaveInspector.RequirePlainAncestors(payload);
        identity = ReadIdentity(_paths.TestSaveManifestPath);
        VerifyOwnedPayload(payload, identity);
        VerifyRequiredStardewFiles(payload, identity);
        return Path.Combine(payload, identity.SaveId);
    }

    private TestSaveIdentity PrepareFixtureAccess(bool allowCreateIdentity)
    {
        _paths.EnsureDirectories();
        _paths.RejectUserProfileReparsePoints();
        EnsurePlainDirectory(_paths.TestSaveRoot);
        RejectManagedReparsePoints();
        TestSaveIdentity identity = allowCreateIdentity
            ? LoadOrCreateIdentity()
            : ReadIdentity(_paths.TestSaveManifestPath);
        ValidateSavesRoot();
        _junction.VerifyInactive(
            _savesRoot,
            identity.SaveId,
            _paths.TestSaveWorkPath);
        return identity;
    }

    private TestSavePreparation Activate(TestSaveIdentity identity, string mode)
    {
        File.Delete(_paths.TestSaveScenarioLogPath);
        string slotPath = Path.Combine(_savesRoot, identity.SaveId);
        var launch = new TestSaveLaunchState(
            mode,
            identity,
            slotPath,
            _paths.TestSaveWorkPath,
            _paths.TestSaveScenarioLogPath);
        launch.Validate();
        string activatedSlotPath = _junction.Activate(
            _savesRoot,
            identity.SaveId,
            _paths.TestSaveWorkPath);
        if (!PathEquals(activatedSlotPath, slotPath))
        {
            _junction.EnsureInactive(
                _savesRoot,
                identity.SaveId,
                _paths.TestSaveWorkPath);
            throw new InvalidOperationException(
                "The activated Stardew test-save slot did not match its exact retained binding.");
        }

        return new TestSavePreparation(launch);
    }

    public TestSaveCleanupResult CompleteStopped(
        TestSaveLaunchState launch,
        string launchId)
    {
        TestSaveIdentity identity = UnmountBeforeFullValidation(launch);
        VerifyRegisteredIdentity(identity);
        VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
        if (string.Equals(launch.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal))
        {
            VerifyRequiredStardewFiles(_paths.TestSaveWorkPath, identity);
        }

        IReadOnlyList<string> logs = ArchiveLogs(launchId, launch.Mode);

        if (string.Equals(launch.Mode, TestSaveContract.CreateMode, StringComparison.Ordinal))
        {
            CaptureBaseline(identity);
        }

        if (!string.Equals(launch.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal))
        {
            ResetWorkFromBaseline(identity);
        }

        _junction.EnsureInactive(_savesRoot, identity.SaveId, _paths.TestSaveWorkPath);
        return new TestSaveCleanupResult(logs, HasScenarioLog(logs));
    }

    public TestSaveCleanupResult AbortStopped(
        TestSaveLaunchState launch,
        string launchId)
    {
        TestSaveIdentity identity = UnmountBeforeFullValidation(launch);
        VerifyRegisteredIdentity(identity);
        VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
        IReadOnlyList<string> logs = ArchiveLogs(launchId, $"{launch.Mode}-failed");
        if (!string.Equals(launch.Mode, TestSaveContract.ReviewMode, StringComparison.Ordinal)
            && Directory.Exists(_paths.TestSaveBaselinePath))
        {
            ResetWorkFromBaseline(identity);
        }

        _junction.EnsureInactive(_savesRoot, identity.SaveId, _paths.TestSaveWorkPath);
        return new TestSaveCleanupResult(logs, HasScenarioLog(logs));
    }

    private TestSaveIdentity LoadOrCreateIdentity()
    {
        if (File.Exists(_paths.TestSaveManifestPath))
        {
            return ReadIdentity(_paths.TestSaveManifestPath);
        }

        string workspaceOwnerId = _createId();
        string fixtureId = _createId();
        long uniqueGameId = _createUniqueGameId();
        var identity = new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            workspaceOwnerId,
            fixtureId,
            uniqueGameId,
            TestSaveContract.GetSaveId(uniqueGameId),
            TestSaveContract.PlayerName,
            TestSaveContract.FarmName,
            TestSaveContract.FavoriteThing);
        identity.Validate();
        WriteIdentityAtomically(_paths.TestSaveManifestPath, identity);
        return ReadIdentity(_paths.TestSaveManifestPath);
    }

    private TestSaveIdentity ValidateLaunchBinding(TestSaveLaunchState launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        launch.Validate();
        RejectManagedReparsePoints();
        TestSaveIdentity identity = launch.Identity;
        if (!PathEquals(launch.WorkPath, _paths.TestSaveWorkPath)
            || !PathEquals(launch.ScenarioLogPath, _paths.TestSaveScenarioLogPath)
            || !PathEquals(
                launch.SlotPath,
                Path.Combine(_savesRoot, identity.SaveId)))
        {
            throw new InvalidDataException(
                "The retained test-save binding does not match this exact SDVKit fixture.");
        }

        return identity;
    }

    private void RejectManagedReparsePoints()
    {
        LiveLabPaths.RejectReparsePointsBelow(_paths.SingleRoot);
        if (!PathEquals(_paths.TestSaveRoot, _paths.SingleRoot))
        {
            LiveLabPaths.RejectReparsePointsBelow(_paths.TestSaveRoot);
        }
    }

    private TestSaveIdentity UnmountBeforeFullValidation(TestSaveLaunchState launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (string.IsNullOrWhiteSpace(launch.SlotPath)
            || string.IsNullOrWhiteSpace(launch.WorkPath)
            || !PathEquals(launch.WorkPath, _paths.TestSaveWorkPath))
        {
            throw new InvalidDataException(
                "The retained test-save binding does not identify SDVKit's exact work path.");
        }

        string slotPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(launch.SlotPath));
        string? slotParent = Path.GetDirectoryName(slotPath);
        string slotName = Path.GetFileName(slotPath);
        if (slotParent is null
            || string.IsNullOrWhiteSpace(slotName)
            || !PathEquals(slotParent, _savesRoot))
        {
            throw new InvalidDataException(
                "The retained test-save slot is not one exact child of Stardew's Saves root.");
        }

        _junction.EnsureInactive(
            _savesRoot,
            slotName,
            _paths.TestSaveWorkPath);
        return ValidateLaunchBinding(launch);
    }

    private void VerifyRegisteredIdentity(TestSaveIdentity expected)
    {
        TestSaveIdentity registered = ReadIdentity(_paths.TestSaveManifestPath);
        if (registered != expected)
        {
            throw new InvalidDataException(
                "The registered test-save identity changed after this exact fixture was launched.");
        }
    }

    private void PrepareEmptyWork(TestSaveIdentity identity)
    {
        if (Directory.Exists(_paths.TestSaveWorkPath))
        {
            VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
            DeleteOwnedPayload(_paths.TestSaveWorkPath, identity);
        }
        else if (File.Exists(_paths.TestSaveWorkPath))
        {
            throw new InvalidDataException("The test-save work path is not a directory.");
        }

        Directory.CreateDirectory(_paths.TestSaveWorkPath);
        WriteIdentityAtomically(
            Path.Combine(
                _paths.TestSaveWorkPath,
                TestSaveContract.FixtureMarkerFileName),
            identity);
        VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
    }

    private void CaptureBaseline(TestSaveIdentity identity)
    {
        VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
        VerifyRequiredStardewFiles(_paths.TestSaveWorkPath, identity);
        if (Directory.Exists(_paths.TestSaveBaselinePath))
        {
            VerifyOwnedPayload(_paths.TestSaveBaselinePath, identity);
            return;
        }

        string staging = Path.Combine(
            _paths.TestSaveRoot,
            $".baseline-{Guid.NewGuid():N}.tmp");
        try
        {
            CopyPlainTree(_paths.TestSaveWorkPath, staging);
            VerifyOwnedPayload(staging, identity);
            if (!TreesEqual(_paths.TestSaveWorkPath, staging))
            {
                throw new IOException("The captured test-save baseline failed byte-for-byte readback.");
            }

            Directory.Move(staging, _paths.TestSaveBaselinePath);
            VerifyOwnedPayload(_paths.TestSaveBaselinePath, identity);
        }
        finally
        {
            TryDeleteOwnedStaging(staging);
        }
    }

    private void ResetWorkFromBaseline(TestSaveIdentity identity)
    {
        VerifyOwnedPayload(_paths.TestSaveBaselinePath, identity);
        VerifyRequiredStardewFiles(_paths.TestSaveBaselinePath, identity);
        string staging = Path.Combine(
            _paths.TestSaveRoot,
            $".reset-{Guid.NewGuid():N}.tmp");
        string displaced = Path.Combine(
            _paths.TestSaveRoot,
            $".previous-{Guid.NewGuid():N}.tmp");
        var displacedWork = false;
        try
        {
            CopyPlainTree(_paths.TestSaveBaselinePath, staging);
            VerifyOwnedPayload(staging, identity);
            if (!TreesEqual(_paths.TestSaveBaselinePath, staging))
            {
                throw new IOException("The staged test-save reset failed byte-for-byte readback.");
            }

            if (Directory.Exists(_paths.TestSaveWorkPath))
            {
                VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
                Directory.Move(_paths.TestSaveWorkPath, displaced);
                displacedWork = true;
            }
            else if (File.Exists(_paths.TestSaveWorkPath))
            {
                throw new InvalidDataException("The test-save work path is not a directory.");
            }

            try
            {
                Directory.Move(staging, _paths.TestSaveWorkPath);
            }
            catch
            {
                if (displacedWork && !Directory.Exists(_paths.TestSaveWorkPath))
                {
                    Directory.Move(displaced, _paths.TestSaveWorkPath);
                    displacedWork = false;
                }

                throw;
            }

            try
            {
                _afterWorkInstalledForTest?.Invoke();
                VerifyOwnedPayload(_paths.TestSaveWorkPath, identity);
                if (!TreesEqual(_paths.TestSaveBaselinePath, _paths.TestSaveWorkPath))
                {
                    throw new IOException("The restored test-save work tree differs from its baseline.");
                }
            }
            catch (Exception verificationException) when (displacedWork)
            {
                string failed = Path.Combine(
                    _paths.TestSaveRoot,
                    $".failed-{Guid.NewGuid():N}.tmp");
                try
                {
                    if (Directory.Exists(_paths.TestSaveWorkPath))
                    {
                        LiveLabPaths.RejectReparsePointsBelow(_paths.TestSaveWorkPath);
                        Directory.Move(_paths.TestSaveWorkPath, failed);
                    }
                    else if (File.Exists(_paths.TestSaveWorkPath))
                    {
                        throw new InvalidDataException(
                            "The failed test-save work installation became a file.");
                    }

                    Directory.Move(displaced, _paths.TestSaveWorkPath);
                    displacedWork = false;
                    TryDeleteOwnedStaging(failed);
                }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        "The staged test-save reset failed verification and its previous work tree could not be fully restored.",
                        new AggregateException(verificationException, rollbackException));
                }

                throw;
            }

            if (displacedWork)
            {
                DeleteOwnedPayload(displaced, identity);
                displacedWork = false;
            }
        }
        finally
        {
            TryDeleteOwnedStaging(staging);
            // A still-displaced tree is the last known good work copy. Preserve it on failure.
        }
    }

    private List<string> ArchiveLogs(string launchId, string label)
    {
        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            throw new InvalidDataException("The test-save log launch ID is invalid.");
        }

        string logsRoot = Path.Combine(_paths.TestSaveRoot, "logs");
        EnsurePlainDirectory(logsRoot);
        var archived = new List<string>();
        ArchiveIfPresent(_paths.StandardOutputPath, logsRoot, launchId, label, "smapi.stdout.log", archived);
        ArchiveIfPresent(_paths.StandardErrorPath, logsRoot, launchId, label, "smapi.stderr.log", archived);
        ArchiveIfPresent(_paths.StatusPath, logsRoot, launchId, label, "status.json", archived);
        ArchiveIfPresent(_paths.TestSaveScenarioLogPath, logsRoot, launchId, label, "scenario.log", archived);
        return archived;
    }

    private static void ArchiveIfPresent(
        string source,
        string logsRoot,
        string launchId,
        string label,
        string suffix,
        List<string> archived)
    {
        if (!File.Exists(source))
        {
            return;
        }

        string destination = Path.Combine(logsRoot, $"{launchId}.{label}.{suffix}");
        if (File.Exists(destination))
        {
            if (!FilesEqual(source, destination))
            {
                throw new IOException(
                    $"An archived test-save log differs from its retry source: {destination}");
            }

            archived.Add(destination);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        archived.Add(destination);
    }

    private static bool HasScenarioLog(IEnumerable<string> archived) =>
        archived.Any(path => path.EndsWith(".scenario.log", StringComparison.Ordinal));

    private void ValidateSavesRoot()
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(_savesRoot);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or SecurityException
            or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Stardew's exact Saves root is unavailable; SDVKit will not discover an alternative path.",
                exception);
        }

        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Stardew's exact Saves root must be a plain directory.");
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The managed test-save path is not a plain directory: {path}");
        }
    }

    private static TestSaveIdentity ReadIdentity(string path)
    {
        TestSaveIdentity identity;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            identity = JsonSerializer.Deserialize<TestSaveIdentity>(stream, JsonOptions)
                ?? throw new InvalidDataException("The SDVKit test-save identity is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The SDVKit test-save identity is invalid JSON.", exception);
        }

        identity.Validate();
        return identity;
    }

    private static void WriteIdentityAtomically(string path, TestSaveIdentity identity)
    {
        identity.Validate();
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The test-save identity path has no directory.");
        EnsurePlainDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            string json = JsonSerializer.Serialize(identity, JsonOptions) + Environment.NewLine;
            File.WriteAllText(temporary, json, Utf8WithoutBom);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void VerifyOwnedPayload(string path, TestSaveIdentity identity)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The SDVKit fixture payload is missing: {path}");
        }

        LiveLabPaths.RejectReparsePointsBelow(path);
        TestSaveIdentity marker = ReadIdentity(
            Path.Combine(path, TestSaveContract.FixtureMarkerFileName));
        if (marker != identity)
        {
            throw new InvalidDataException(
                "The fixture payload marker does not match the exact registered test-save identity.");
        }
    }

    private static void VerifyRequiredStardewFiles(
        string path,
        TestSaveIdentity identity)
    {
        VerifyRequiredStardewFile(path, identity.SaveId);
        VerifyRequiredStardewFile(path, "SaveGameInfo");
    }

    private static void VerifyRequiredStardewFile(string path, string fileName)
    {
        string filePath = Path.Combine(path, fileName);
        FileInfo file = new(filePath);
        if (!file.Exists
            || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || file.Length == 0)
        {
            throw new InvalidDataException(
                $"The disposable fixture is missing its non-empty Stardew file '{fileName}'.");
        }
    }

    private static void CopyPlainTree(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"The test-save staging path already exists: {destination}");
        }

        LiveLabPaths.RejectReparsePointsBelow(source);
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }

        LiveLabPaths.RejectReparsePointsBelow(destination);
    }

    private static bool TreesEqual(string left, string right)
    {
        string[] leftFiles = Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(left, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] rightFiles = Directory.EnumerateFiles(right, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(right, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!leftFiles.SequenceEqual(rightFiles, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (string relative in leftFiles)
        {
            if (!FilesEqual(Path.Combine(left, relative), Path.Combine(right, relative)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FilesEqual(string left, string right)
    {
        using FileStream leftStream = File.OpenRead(left);
        using FileStream rightStream = File.OpenRead(right);
        if (leftStream.Length != rightStream.Length)
        {
            return false;
        }

        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];
        while (true)
        {
            int leftRead = leftStream.Read(leftBuffer);
            int rightRead = rightStream.Read(rightBuffer);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static void DeleteOwnedPayload(string path, TestSaveIdentity identity)
    {
        VerifyOwnedPayload(path, identity);
        Directory.Delete(path, recursive: true);
    }

    private void TryDeleteOwnedStaging(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        string[] prefixes = [".baseline-", ".reset-", ".previous-", ".failed-"];
        string? prefix = prefixes.FirstOrDefault(
            candidate => name.StartsWith(candidate, StringComparison.Ordinal));
        string token = prefix is null || !name.EndsWith(".tmp", StringComparison.Ordinal)
            ? string.Empty
            : name[prefix.Length..^4];
        if (!PathEquals(parent ?? string.Empty, _paths.TestSaveRoot)
            || !Guid.TryParseExact(token, "N", out _))
        {
            throw new InvalidOperationException(
                "SDVKit refused to remove a path that is not one of its exact test-save staging directories.");
        }

        LiveLabPaths.RejectReparsePointsBelow(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static long CreateUniqueGameId()
    {
        long value = BitConverter.ToInt64(Guid.NewGuid().ToByteArray()) & long.MaxValue;
        return value == 0 ? 1 : value;
    }
}

internal sealed class TestSaveStaleCopyException(string message) : InvalidOperationException(message);

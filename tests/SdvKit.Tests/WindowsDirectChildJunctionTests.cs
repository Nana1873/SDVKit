using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class WindowsDirectChildJunctionTests
{
    private const string SlotName = "SDVKitFixture_123456789";

    [Fact]
    public void ActivateVerifiesAndDeletesOnlyTheExactDirectChild()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        SeedPlainTarget(platform, trustedRoot, targetPath);
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        string activatedPath = junction.Activate(
            savesRoot,
            SlotName,
            targetPath);
        junction.VerifyActive(savesRoot, SlotName, targetPath);
        junction.EnsureInactive(savesRoot, SlotName, targetPath);

        Assert.Equal(slotPath, activatedPath);
        Assert.Equal((slotPath, targetPath), platform.CreatedJunction);
        Assert.Equal(slotPath, platform.DeletedJunction);
        Assert.Equal(
            WindowsDirectChildEntryKind.Missing,
            platform.GetEntry(slotPath).Kind);
        Assert.DoesNotContain(
            platform.InspectedPaths,
            path => StringComparer.OrdinalIgnoreCase.Equals(path, savesRoot));
        Assert.All(
            platform.InspectedPaths,
            path => Assert.True(
                StringComparer.OrdinalIgnoreCase.Equals(path, slotPath)
                || path.StartsWith(
                    Path.TrimEndingDirectorySeparator(trustedRoot),
                    StringComparison.OrdinalIgnoreCase),
                $"Unexpected inspected path: {path}"));
    }

    [Theory]
    [InlineData(nameof(WindowsDirectChildEntryKind.PlainFile))]
    [InlineData(nameof(WindowsDirectChildEntryKind.PlainDirectory))]
    [InlineData(nameof(WindowsDirectChildEntryKind.DirectoryJunction))]
    [InlineData(nameof(WindowsDirectChildEntryKind.OtherReparsePoint))]
    public void ActivateRejectsEveryExistingSlotEntry(string existingKindName)
    {
        WindowsDirectChildEntryKind existingKind = Enum.Parse<WindowsDirectChildEntryKind>(
            existingKindName);
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        SeedPlainTarget(platform, trustedRoot, targetPath);
        platform.SetEntry(
            slotPath,
            new WindowsDirectChildEntry(
                existingKind,
                existingKind == WindowsDirectChildEntryKind.DirectoryJunction
                    ? targetPath
                    : null));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.Activate(savesRoot, SlotName, targetPath));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Null(platform.CreatedJunction);
    }

    [Fact]
    public void VerifyInactiveRejectsTheExactExistingSlotWithoutDeletingIt()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        platform.SetEntry(
            slotPath,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.DirectoryJunction,
                targetPath));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.VerifyInactive(savesRoot, SlotName, targetPath));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Null(platform.DeletedJunction);
        Assert.Equal(
            WindowsDirectChildEntryKind.DirectoryJunction,
            platform.GetEntry(slotPath).Kind);
        Assert.Equal([slotPath], platform.InspectedPaths);
    }

    [Fact]
    public void ActivateFailsWhenTheCreatedJunctionReportsAnotherTarget()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string wrongTarget = Path.Combine(trustedRoot, "other", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        FakeWindowsDirectChildJunctionPlatform platform = new()
        {
            CreatedTargetOverride = wrongTarget,
        };
        SeedPlainTarget(platform, trustedRoot, targetPath);
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.Activate(savesRoot, SlotName, targetPath));

        Assert.Contains("failed verification", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(platform.CreatedJunction);
        Assert.Null(platform.DeletedJunction);
    }

    [Fact]
    public void EnsureInactiveRejectsAnotherJunctionTargetWithoutDeletingIt()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string wrongTarget = Path.Combine(trustedRoot, "other", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        platform.SetEntry(
            slotPath,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.DirectoryJunction,
                wrongTarget));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.EnsureInactive(savesRoot, SlotName, targetPath));

        Assert.Contains("does not own", exception.Message, StringComparison.Ordinal);
        Assert.Null(platform.DeletedJunction);
        Assert.Equal(
            WindowsDirectChildEntryKind.DirectoryJunction,
            platform.GetEntry(slotPath).Kind);
    }

    [Fact]
    public void EnsureInactiveDoesNotDeleteAnEntrySwappedAfterPreflightInspection()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new()
        {
            ReplacementBeforeExactDelete = new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.PlainDirectory),
        };
        platform.SetEntry(
            slotPath,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.DirectoryJunction,
                targetPath));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.EnsureInactive(savesRoot, SlotName, targetPath));

        Assert.Contains("changed before handle-bound cleanup", exception.Message, StringComparison.Ordinal);
        Assert.Equal(targetPath, platform.ExpectedDeleteTarget);
        Assert.Null(platform.DeletedJunction);
        Assert.Equal(
            WindowsDirectChildEntryKind.PlainDirectory,
            platform.GetEntry(slotPath).Kind);
    }

    [Theory]
    [InlineData(nameof(WindowsDirectChildEntryKind.PlainFile))]
    [InlineData(nameof(WindowsDirectChildEntryKind.PlainDirectory))]
    [InlineData(nameof(WindowsDirectChildEntryKind.OtherReparsePoint))]
    public void EnsureInactiveRejectsEveryNonJunctionEntryWithoutDeletingIt(
        string existingKindName)
    {
        WindowsDirectChildEntryKind existingKind = Enum.Parse<WindowsDirectChildEntryKind>(
            existingKindName);
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        platform.SetEntry(slotPath, new WindowsDirectChildEntry(existingKind));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        Assert.Throws<InvalidOperationException>(
            () => junction.EnsureInactive(savesRoot, SlotName, targetPath));

        Assert.Null(platform.DeletedJunction);
        Assert.Equal(existingKind, platform.GetEntry(slotPath).Kind);
    }

    [Fact]
    public void TraversalAndNestedSlotNamesAreRejectedBeforeInspection()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetWithTraversal = Path.Combine(
            trustedRoot,
            "baseline",
            "..",
            "outside");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        FakeWindowsDirectChildJunctionPlatform platform = new();
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        Assert.Throws<ArgumentException>(
            () => junction.Activate(savesRoot, SlotName, targetWithTraversal));
        Assert.Throws<ArgumentException>(
            () => junction.Activate(
                savesRoot,
                Path.Combine("nested", SlotName),
                Path.Combine(trustedRoot, "baseline", "save")));
        Assert.Empty(platform.InspectedPaths);
        Assert.Null(platform.CreatedJunction);
    }

    [Fact]
    public void TargetOutsideTheTrustedRootIsRejectedBeforeInspection()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string outsideTarget = Path.Combine(temporary.Path, "outside", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        FakeWindowsDirectChildJunctionPlatform platform = new();
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        Assert.Throws<ArgumentException>(
            () => junction.Activate(savesRoot, SlotName, outsideTarget));

        Assert.Empty(platform.InspectedPaths);
        Assert.Null(platform.CreatedJunction);
    }

    [Fact]
    public void ReparsePointInTheTrustedTargetPathIsRejectedBeforeSlotInspection()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string reparseComponent = Path.Combine(trustedRoot, "baseline");
        string targetPath = Path.Combine(reparseComponent, "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        platform.SetEntry(
            trustedRoot,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.PlainDirectory));
        platform.SetEntry(
            reparseComponent,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.OtherReparsePoint));
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => junction.Activate(savesRoot, SlotName, targetPath));

        Assert.Contains("plain directories", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            platform.InspectedPaths,
            path => StringComparer.OrdinalIgnoreCase.Equals(path, slotPath));
        Assert.Null(platform.CreatedJunction);
    }

    [Fact]
    public void EnsureInactiveIsIdempotentWhenTheExactChildIsMissing()
    {
        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "personal-saves");
        string slotPath = Path.Combine(savesRoot, SlotName);
        FakeWindowsDirectChildJunctionPlatform platform = new();
        WindowsDirectChildJunction junction = new(trustedRoot, platform);

        junction.EnsureInactive(savesRoot, SlotName, targetPath);
        junction.EnsureInactive(savesRoot, SlotName, targetPath);

        Assert.Equal([slotPath, slotPath], platform.InspectedPaths);
        Assert.Null(platform.DeletedJunction);
    }

    [Fact]
    public void NativeJunctionRoundTripDeletesTheVerifiedHandleNotTheTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string trustedRoot = Path.Combine(temporary.Path, ".sdvkit", "fixtures");
        string targetPath = Path.Combine(trustedRoot, "baseline", "save");
        string savesRoot = Path.Combine(temporary.Path, "saves");
        string targetMarker = Path.Combine(targetPath, "target-marker.txt");
        Directory.CreateDirectory(targetPath);
        Directory.CreateDirectory(savesRoot);
        File.WriteAllText(targetMarker, "owned fixture");
        WindowsDirectChildJunction junction = new(trustedRoot);

        string slotPath = junction.Activate(savesRoot, SlotName, targetPath);
        try
        {
            Assert.True(
                (File.GetAttributes(slotPath) & FileAttributes.ReparsePoint) != 0);

            junction.EnsureInactive(savesRoot, SlotName, targetPath);

            Assert.False(Directory.Exists(slotPath));
            Assert.True(File.Exists(targetMarker));
        }
        finally
        {
            junction.EnsureInactive(savesRoot, SlotName, targetPath);
        }
    }

    private static void SeedPlainTarget(
        FakeWindowsDirectChildJunctionPlatform platform,
        string trustedRoot,
        string targetPath)
    {
        platform.SetEntry(
            trustedRoot,
            new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.PlainDirectory));

        string current = trustedRoot;
        foreach (string component in Path.GetRelativePath(trustedRoot, targetPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            platform.SetEntry(
                current,
                new WindowsDirectChildEntry(
                    WindowsDirectChildEntryKind.PlainDirectory));
        }
    }

    private sealed class FakeWindowsDirectChildJunctionPlatform
        : IWindowsDirectChildJunctionPlatform
    {
        private readonly Dictionary<string, WindowsDirectChildEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> InspectedPaths { get; } = [];

        public (string JunctionPath, string TargetPath)? CreatedJunction { get; private set; }

        public string? CreatedTargetOverride { get; init; }

        public WindowsDirectChildEntry? ReplacementBeforeExactDelete { get; init; }

        public string? DeletedJunction { get; private set; }

        public string? ExpectedDeleteTarget { get; private set; }

        public WindowsDirectChildEntry Inspect(string path)
        {
            string normalized = Normalize(path);
            InspectedPaths.Add(normalized);
            return GetEntry(normalized);
        }

        public void CreateDirectoryJunction(string junctionPath, string targetPath)
        {
            string normalizedJunction = Normalize(junctionPath);
            string normalizedTarget = Normalize(targetPath);
            CreatedJunction = (normalizedJunction, normalizedTarget);
            _entries[normalizedJunction] = new WindowsDirectChildEntry(
                WindowsDirectChildEntryKind.DirectoryJunction,
                CreatedTargetOverride ?? normalizedTarget);
        }

        public void DeleteExactDirectoryJunction(
            string junctionPath,
            string expectedTargetPath)
        {
            string normalized = Normalize(junctionPath);
            ExpectedDeleteTarget = Normalize(expectedTargetPath);
            if (ReplacementBeforeExactDelete is { } replacement)
            {
                _entries[normalized] = replacement;
            }

            WindowsDirectChildEntry entry = GetEntry(normalized);
            if (entry.Kind != WindowsDirectChildEntryKind.DirectoryJunction
                || !WindowsDirectChildJunction.IsSameNormalizedPath(
                    entry.JunctionTarget,
                    ExpectedDeleteTarget))
            {
                throw new InvalidOperationException(
                    $"The exact Stardew test-save slot changed before handle-bound cleanup: {normalized}");
            }

            DeletedJunction = normalized;
            _entries.Remove(normalized);
        }

        public void SetEntry(string path, WindowsDirectChildEntry entry) =>
            _entries[Normalize(path)] = entry;

        public WindowsDirectChildEntry GetEntry(string path) =>
            _entries.TryGetValue(
                Normalize(path),
                out WindowsDirectChildEntry entry)
                ? entry
                : new WindowsDirectChildEntry(
                    WindowsDirectChildEntryKind.Missing);

        private static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

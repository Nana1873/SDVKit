using System.Security.Cryptography;
using SdvKit.AlwaysOn;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewTextureCommandTests
{
    [Fact]
    public void FileInventoryIncludesOnlyCanonicalNonLocalizedXnbIdentities()
    {
        using TemporaryDirectory temporary = new();
        string maps = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "Maps")).FullName;
        string data = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "Data")).FullName;
        File.WriteAllText(Path.Combine(maps, "Farm.xnb"), "candidate");
        File.WriteAllText(Path.Combine(maps, "Farm.de-DE.xnb"), "localized");
        File.WriteAllText(Path.Combine(maps, "Farm.FR-fr.XNB"), "localized");
        File.WriteAllText(Path.Combine(data, "Buildings.xnb"), "candidate");
        File.WriteAllText(Path.Combine(data, "notes.txt"), "ignored");

        IReadOnlyList<string> assets = ReviewTextureFileInventory.Discover(
            temporary.Path);

        Assert.Equal(["Data/Buildings", "Maps/Farm"], assets);
    }

    [Fact]
    public void FileInventoryStopsAsSoonAsTheCandidateLimitIsExceeded()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllText(Path.Combine(temporary.Path, "One.xnb"), "candidate");
        File.WriteAllText(Path.Combine(temporary.Path, "Two.xnb"), "candidate");
        File.WriteAllText(Path.Combine(temporary.Path, "Three.xnb"), "candidate");

        Assert.Throws<ReviewTextureInventoryTooLargeException>(() =>
            ReviewTextureFileInventory.Discover(temporary.Path, maximumCandidates: 2));
    }

    [Fact]
    public void AssetsMeasureTheWholeCanonicalPopulationAndPageOnlyTextures()
    {
        using TemporaryDirectory temporary = new();
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Cursors"] = new(320, 640, "Color", 1),
                ["Data/Buildings"] = null,
                ["Characters/Farmer"] = new(96, 672, "Color", 1),
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.AssetsOperation,
                null,
                1,
                1));

        Assert.Equal("ready", report.State);
        Assert.Empty(report.Problems);
        ReviewTextureCoverageReport coverage = Assert.IsType<
            ReviewTextureCoverageReport>(report.Coverage);
        Assert.Equal(3, coverage.Candidates);
        Assert.Equal(3, coverage.Classified);
        Assert.Equal(2, coverage.Textures);
        Assert.Equal(1, coverage.NonTextures);
        Assert.Equal(0, coverage.Gaps);
        Assert.True(coverage.Complete);
        ReviewTextureAssetReport asset = Assert.Single(report.Assets!);
        Assert.Equal("LooseSprites/Cursors", asset.AssetName);
        Assert.Equal(
            ReviewTextureContract.CanonicalGameContentSource,
            asset.SourceCategory);
        Assert.True(asset.Available);
        Assert.Equal(
            new ReviewTexturePage(1, 1, 1, 2, null),
            report.Page);
        Assert.False(report.Provenance!.DetailedProviderAvailable);
    }

    [Fact]
    public void GetReturnsBoundedFinalPipelineMetadataWithoutInventedProvider()
    {
        using TemporaryDirectory temporary = new();
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["Characters/Farmer"] = new(96, 672, "Dxt5", 4),
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.GetOperation,
                "characters_farmer",
                0,
                1));

        Assert.Equal("ready", report.State);
        Assert.Equal("Characters/Farmer", report.AssetName);
        Assert.True(report.Available);
        Assert.Equal(
            new ReviewTextureMetadataReport(96, 672, "Dxt5", 4, true),
            report.Metadata);
        Assert.Equal(
            ReviewTextureContract.FinalPipelineStage,
            report.Provenance!.PipelineStage);
        Assert.False(report.Provenance.DetailedProviderAvailable);
        Assert.Contains(
            "not its per-mod loader or editor chain",
            report.Provenance.Detail,
            StringComparison.Ordinal);
        Assert.Null(report.Preview);
    }

    [Fact]
    public void PreviewCreatesOneGuidNamedDownscaledPngWithHash()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(2048, 1024, "Color", 1);
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["Maps/springobjects"] = texture,
            });
        string requestId = Guid.NewGuid().ToString("N");

        ReviewTextureReport report = ReviewTextureOperation.Execute(
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "Maps/springobjects",
                0,
                1),
            source,
            temporary.Path,
            requestId);

        Assert.Equal("ready", report.State);
        ReviewTexturePreviewReport preview = Assert.IsType<
            ReviewTexturePreviewReport>(report.Preview);
        Assert.Equal(
            ReviewTextureContract.PreviewFileName(requestId),
            preview.RelativePath);
        Assert.Equal(512, preview.Width);
        Assert.Equal(256, preview.Height);
        Assert.Equal((512, 256), texture.LastPreviewDimensions);
        Assert.Equal(1, texture.WriteCount);
        string path = Path.Combine(temporary.Path, preview.RelativePath);
        Assert.True(File.Exists(path));
        Assert.Equal(new FileInfo(path).Length, preview.EncodedBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant(),
            preview.Sha256);
        Assert.Single(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(4097, 4097)]
    public void PreviewRejectsOversizedSourcesBeforePixelReadback(
        int width,
        int height)
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(width, height, "Color", 1);
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["Maps/TooLarge"] = texture,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "Maps/TooLarge",
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal(
            "texturePreviewSourceTooLarge",
            Assert.Single(report.Problems).Code);
        Assert.Equal(0, texture.WriteCount);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Fact]
    public void PreviewRejectsAndRemovesMismatchedPng()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(64, 32, "Color", 1)
        {
            WriteMismatchedDimensions = true,
        };
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Test"] = texture,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Test",
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal("texturePreviewFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(1, texture.WriteCount);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Fact]
    public void PreviewRejectsAndRemovesAHeaderOnlyPng()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(64, 32, "Color", 1)
        {
            OmitEndChunk = true,
        };
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Test"] = texture,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Test",
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal("texturePreviewFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(1, texture.WriteCount);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Fact]
    public void PreviewRejectsAndRemovesAnOversizedEncoding()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(64, 32, "Color", 1)
        {
            EncodedLength = ReviewTextureContract.MaximumPreviewBytes + 1,
        };
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Test"] = texture,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Test",
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal("texturePreviewFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(1, texture.WriteCount);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.png"));
    }

    [Fact]
    public void PreviewNeverOverwritesItsExactGuidTarget()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(64, 32, "Color", 1);
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Test"] = texture,
            });
        string requestId = Guid.NewGuid().ToString("N");
        string path = ReviewTextureContract.PreviewPath(
            temporary.Path,
            requestId);
        File.WriteAllText(path, "preserve");

        ReviewTextureReport report = ReviewTextureOperation.Execute(
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Test",
                0,
                1),
            source,
            temporary.Path,
            requestId);

        Assert.Equal("blocked", report.State);
        Assert.Equal("texturePreviewFailed", Assert.Single(report.Problems).Code);
        Assert.Equal(0, texture.WriteCount);
        Assert.Equal("preserve", File.ReadAllText(path));
    }

    [Fact]
    public void InventoryGapsAndNormalizationCollisionsAreMeasuredAndBlock()
    {
        using TemporaryDirectory temporary = new();
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/A-B"] = new(16, 16, "Color", 1),
                ["LooseSprites/A_B"] = new(16, 16, "Color", 1),
                ["Data/Buildings"] = null,
                ["LooseSprites/Unclassified"] = new(16, 16, "Color", 1),
            },
            unclassified: new HashSet<string>(StringComparer.Ordinal)
            {
                "LooseSprites/Unclassified",
            });

        ReviewTextureReport inventory = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.AssetsOperation,
                null,
                0,
                50));

        Assert.Equal("blocked", inventory.State);
        Assert.Equal(
            "textureCoverageIncomplete",
            Assert.Single(inventory.Problems).Code);
        Assert.Equal(
            new ReviewTextureCoverageReport(4, 1, 0, 1, 3),
            inventory.Coverage);
        Assert.Empty(inventory.Assets!);

        ReviewTextureReport ambiguous = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.GetOperation,
                "LooseSprites/A B",
                0,
                1));
        Assert.Equal(
            "textureAssetAmbiguous",
            Assert.Single(ambiguous.Problems).Code);

        ReviewTextureReport unclassified = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.GetOperation,
                "LooseSprites/Unclassified",
                0,
                1));
        Assert.Equal(
            "textureAssetUnclassified",
            Assert.Single(unclassified.Problems).Code);
    }

    [Fact]
    public void OversizedSourceInventoryFailsBeforeSortingOrClassification()
    {
        using TemporaryDirectory temporary = new();
        Dictionary<string, FakeTexture?> assets = Enumerable
            .Range(0, ReviewTextureContract.MaximumDiscoveredAssets + 1)
            .ToDictionary(
                index => $"LooseSprites/Test{index:D5}",
                _ => (FakeTexture?)null,
                StringComparer.Ordinal);
        var source = new FakeReviewTextureSource(assets);

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.AssetsOperation,
                null,
                0,
                50));

        Assert.Equal("blocked", report.State);
        Assert.Equal("textureInventoryTooLarge", Assert.Single(report.Problems).Code);
        Assert.Equal(0, source.ClassificationCount);
    }

    [Fact]
    public void NearestNeighborSamplingUsesTheExpectedSourcePixels()
    {
        int[] source = [0, 1, 2, 3, 4, 5, 6, 7];
        var destination = new int[2];

        ReviewTextureSampling.CopyNearestNeighbor(
            source,
            sourceWidth: 4,
            sourceHeight: 2,
            destination,
            destinationWidth: 2,
            destinationHeight: 1);

        Assert.Equal([0, 2], destination);
    }

    [Theory]
    [InlineData("Data/Buildings", "textureAssetNotTexture")]
    [InlineData("LooseSprites/Missing", "textureAssetUnknown")]
    public void ExactQueriesFailClosedForNonTextureOrUnknownAssets(
        string asset,
        string expectedProblem)
    {
        using TemporaryDirectory temporary = new();
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["Data/Buildings"] = null,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.GetOperation,
                asset,
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal(expectedProblem, Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void InvalidMetadataFailsWithoutPreviewing()
    {
        using TemporaryDirectory temporary = new();
        var texture = new FakeTexture(0, 16, "Color", 1);
        var source = new FakeReviewTextureSource(
            new Dictionary<string, FakeTexture?>
            {
                ["LooseSprites/Invalid"] = texture,
            });

        ReviewTextureReport report = Execute(
            source,
            temporary.Path,
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Invalid",
                0,
                1));

        Assert.Equal("blocked", report.State);
        Assert.Equal("textureMetadataInvalid", Assert.Single(report.Problems).Code);
        Assert.Equal(0, texture.WriteCount);
    }

    [Fact]
    public void TransportTokenRoundTripsUnicodeButRejectsNonCanonicalEncoding()
    {
        const string asset = "Portraits/Märchen Figur";
        string token = ReviewTransportToken.Encode(asset);

        Assert.True(ReviewTransportToken.TryDecode(
            token,
            ReviewTextureContract.MaximumAssetLength,
            out string decoded));
        Assert.Equal(asset, decoded);
        Assert.False(ReviewTransportToken.TryDecode(
            token + "=",
            ReviewTextureContract.MaximumAssetLength,
            out _));
    }

    [Fact]
    public void PreviewCleanupRefusesAReparsePoint()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string target = Path.Combine(temporary.Path, "target.png");
        File.WriteAllText(target, "keep");
        string previewPath = ReviewTextureContract.PreviewPath(
            temporary.Path,
            requestId);
        try
        {
            File.CreateSymbolicLink(previewPath, target);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(ReviewTextureOperation.TryDeletePreview(
            temporary.Path,
            requestId));
        Assert.True(File.Exists(previewPath));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    private static ReviewTextureReport Execute(
        IReviewTextureSource source,
        string runtimePath,
        ReviewTextureQuery query) =>
        ReviewTextureOperation.Execute(
            query,
            source,
            runtimePath,
            Guid.NewGuid().ToString("N"));

    private sealed class FakeReviewTextureSource(
        IReadOnlyDictionary<string, FakeTexture?> assets,
        IReadOnlySet<string>? unclassified = null)
        : IReviewTextureSource
    {
        public string GameVersion => "1.6.15";

        public string GameFileVersion => "1.6.15.24356";

        public IReadOnlyList<string> DiscoverCanonicalAssetNames() =>
            assets.Keys.ToArray();

        public int ClassificationCount { get; private set; }

        public bool TryClassifyTexture(string assetName, out bool isTexture)
        {
            ClassificationCount++;
            isTexture = assets[assetName] is not null;
            return unclassified?.Contains(assetName) != true;
        }

        public IReviewTextureAsset LoadTexture(string assetName) =>
            assets[assetName]
            ?? throw new InvalidOperationException("Not a texture.");
    }

    private sealed class FakeTexture(
        int width,
        int height,
        string runtimeFormat,
        int levelCount)
        : IReviewTextureAsset
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public string RuntimeFormat { get; } = runtimeFormat;

        public int LevelCount { get; } = levelCount;

        public bool WriteMismatchedDimensions { get; init; }

        public bool OmitEndChunk { get; init; }

        public int? EncodedLength { get; init; }

        public int WriteCount { get; private set; }

        public (int Width, int Height)? LastPreviewDimensions { get; private set; }

        public void WriteNearestNeighborPng(Stream output, int width, int height)
        {
            WriteCount++;
            LastPreviewDimensions = (width, height);
            byte[] png = PngTestData.CreateRgba8(
                WriteMismatchedDimensions ? width + 1 : width,
                height);
            int writeLength = OmitEndChunk ? png.Length - 12 : png.Length;
            output.Write(png, 0, writeLength);
            if (EncodedLength is int encodedLength)
            {
                output.SetLength(encodedLength);
            }
        }
    }
}

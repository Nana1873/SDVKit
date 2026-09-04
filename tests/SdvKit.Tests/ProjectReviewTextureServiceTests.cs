using System.Security.Cryptography;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ProjectReviewTextureServiceTests
{
    [Fact]
    public void BuildCommandBindsAndEncodesTheExactAsset()
    {
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewTextureQuery(
            ReviewTextureContract.PreviewOperation,
            "Portraits/Märchen Figur",
            0,
            1);

        string command = ProjectReviewTextureService.BuildCommand(requestId, query);

        Assert.StartsWith(
            $"sdvkit texture {requestId} preview 0 1 ",
            command,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Portraits/", command, StringComparison.Ordinal);
        string encoded = command.Split(' ')[6];
        Assert.True(ReviewTransportToken.TryDecode(
            encoded,
            ReviewTextureContract.MaximumAssetLength,
            out string decoded));
        Assert.Equal(query.Asset, decoded);
    }

    [Fact]
    public void InvalidQueryFailsBeforeReviewTransport()
    {
        LiveLabCommandResult result = ProjectReviewTextureService.Execute(
            new ReviewTextureQuery(
                ReviewTextureContract.PreviewOperation,
                "LooseSprites/Cursors",
                1,
                1),
            "not-used");

        Assert.Equal(3, result.ExitCode);
        ReviewTextureReport report = Assert.IsType<ReviewTextureReport>(result.Report);
        Assert.Equal("blocked", report.State);
        Assert.Equal("textureRequestInvalid", Assert.Single(report.Problems).Code);
    }

    [Fact]
    public void MatchingPreviewResponseRequiresExactOwnedPngDimensionsAndHash()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string path = ReviewTextureContract.PreviewPath(temporary.Path, requestId);
        File.WriteAllBytes(path, PngTestData.CreateRgba8(64, 32));
        long length = new FileInfo(path).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
        var query = new ReviewTextureQuery(
            ReviewTextureContract.PreviewOperation,
            "LooseSprites/Cursors",
            0,
            1);
        ReviewTextureResponseEnvelope envelope = Envelope(
            requestId,
            query,
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId),
                64,
                32,
                length,
                hash));

        Assert.True(ProjectReviewTextureService.MatchesRequest(
            envelope,
            query,
            requestId,
            temporary.Path));
        Assert.True(File.Exists(path));

        ReviewTextureResponseEnvelope wrongHash = Envelope(
            requestId,
            query,
            envelope.Report.Preview! with { Sha256 = new string('0', 64) });
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            wrongHash,
            query,
            requestId,
            temporary.Path));

        ReviewTextureResponseEnvelope wrongDimensions = Envelope(
            requestId,
            query,
            envelope.Report.Preview! with { Width = 63 });
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            wrongDimensions,
            query,
            requestId,
            temporary.Path));

        ReviewTextureResponseEnvelope wrongAsset = envelope with
        {
            Report = envelope.Report with { AssetName = "Portraits/Abigail" },
        };
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            wrongAsset,
            query,
            requestId,
            temporary.Path));

        ReviewTextureResponseEnvelope contradictoryState = envelope with
        {
            Report = envelope.Report with
            {
                Problems = [new ReviewTextureProblem("unexpected", "Unexpected.")],
            },
        };
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            contradictoryState,
            query,
            requestId,
            temporary.Path));
    }

    [Fact]
    public void PreviewResponseRejectsNullableGraphMembersWithoutThrowing()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        string path = ReviewTextureContract.PreviewPath(temporary.Path, requestId);
        File.WriteAllBytes(path, PngTestData.CreateRgba8(64, 32));
        long length = new FileInfo(path).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
        var query = new ReviewTextureQuery(
            ReviewTextureContract.PreviewOperation,
            "LooseSprites/Cursors",
            0,
            1);
        ReviewTextureResponseEnvelope envelope = Envelope(
            requestId,
            query,
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId),
                64,
                32,
                length,
                hash));

        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with { Report = null! },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with { Problems = null! },
            },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with
                {
                    Metadata = envelope.Report.Metadata! with
                    {
                        RuntimeFormat = null!,
                    },
                },
            },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with
                {
                    Preview = envelope.Report.Preview! with { Sha256 = null! },
                },
            },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = envelope.Report with
                {
                    Problems = [null!],
                    State = "blocked",
                },
            },
            query,
            requestId,
            temporary.Path));
    }

    [Fact]
    public void AssetsResponseRequiresTheExactPageCoverageAndSafeIdentities()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewTextureQuery(
            ReviewTextureContract.AssetsOperation,
            null,
            1,
            1);
        ReviewTextureResponseEnvelope envelope = AssetsEnvelope(requestId, query);

        Assert.True(ProjectReviewTextureService.MatchesRequest(
            envelope,
            query,
            requestId,
            temporary.Path));

        ReviewTextureReport report = envelope.Report;
        ReviewTexturePage page = report.Page!;
        ReviewTextureCoverageReport coverage = report.Coverage!;
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with { Report = report with { Page = page with { Offset = 0 } } },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with { Report = report with { Page = page with { Limit = 100 } } },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with { Report = report with { Page = page with { NextOffset = 2 } } },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Coverage = coverage with { NonTextures = 2 },
                },
            },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with { Report = report with { Assets = [null!] } },
            query,
            requestId,
            temporary.Path));
        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope with
            {
                Report = report with
                {
                    Assets =
                    [
                        new ReviewTextureAssetReport(
                            "../Outside",
                            ReviewTextureContract.CanonicalGameContentSource,
                            true),
                    ],
                },
            },
            query,
            requestId,
            temporary.Path));
    }

    [Fact]
    public void StructurallyIncompletePngIsRejectedEvenWhenDimensionsAndHashMatch()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        byte[] complete = PngTestData.CreateRgba8(64, 32);
        byte[] incomplete = complete[..^12];
        string path = ReviewTextureContract.PreviewPath(temporary.Path, requestId);
        File.WriteAllBytes(path, incomplete);
        var query = new ReviewTextureQuery(
            ReviewTextureContract.PreviewOperation,
            "LooseSprites/Cursors",
            0,
            1);
        ReviewTextureResponseEnvelope envelope = Envelope(
            requestId,
            query,
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId),
                64,
                32,
                incomplete.Length,
                Convert.ToHexString(SHA256.HashData(incomplete)).ToLowerInvariant()));

        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope,
            query,
            requestId,
            temporary.Path));
    }

    [Fact]
    public void PngValidatorDecodesExactRgbaPixels()
    {
        byte[] pixels =
        [
            255, 0, 0, 255,
            0, 255, 0, 128,
        ];
        using var stream = new MemoryStream(PngTestData.CreateRgba8(2, 1, pixels));

        Assert.True(ReviewTexturePngValidator.TryValidateRgba8(
            stream,
            ReviewTextureContract.MaximumPreviewBytes,
            ReviewTextureContract.MaximumPreviewDimension,
            ReviewTextureContract.MaximumPreviewPixels,
            out ReviewTexturePngInfo? info));
        Assert.NotNull(info);
        Assert.Equal(2, info.Width);
        Assert.Equal(1, info.Height);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant(),
            info.PixelSha256);
    }

    [Fact]
    public void NonPreviewResponsesCannotClaimPreviewEvidence()
    {
        using TemporaryDirectory temporary = new();
        string requestId = Guid.NewGuid().ToString("N");
        var query = new ReviewTextureQuery(
            ReviewTextureContract.GetOperation,
            "LooseSprites/Cursors",
            0,
            1);
        ReviewTextureResponseEnvelope envelope = Envelope(
            requestId,
            query,
            new ReviewTexturePreviewReport(
                ReviewTextureContract.PreviewFileName(requestId),
                1,
                1,
                24,
                new string('0', 64)));

        Assert.False(ProjectReviewTextureService.MatchesRequest(
            envelope,
            query,
            requestId,
            temporary.Path));
    }

    private static ReviewTextureResponseEnvelope Envelope(
        string requestId,
        ReviewTextureQuery query,
        ReviewTexturePreviewReport? preview) =>
        new(
            ReviewTextureContract.SchemaVersion,
            requestId,
            new ReviewTextureReport(
                ReviewTextureContract.SchemaVersion,
                "ready",
                query.Operation,
                "1.6.15",
                "1.6.15.24356",
                query.Asset,
                ReviewTextureContract.CanonicalGameContentSource,
                true,
                new ReviewTextureMetadataReport(64, 32, "Color", 1, false),
                new ReviewTextureProvenanceReport(
                    ReviewTextureContract.FinalPipelineStage,
                    false,
                    ReviewTextureContract.ProvenanceUnavailableDetail),
                preview,
                null,
                null,
                null,
                []));

    private static ReviewTextureResponseEnvelope AssetsEnvelope(
        string requestId,
        ReviewTextureQuery query) =>
        new(
            ReviewTextureContract.SchemaVersion,
            requestId,
            new ReviewTextureReport(
                ReviewTextureContract.SchemaVersion,
                "ready",
                query.Operation,
                "1.6.15",
                "1.6.15.24356",
                null,
                ReviewTextureContract.CanonicalGameContentSource,
                null,
                null,
                new ReviewTextureProvenanceReport(
                    ReviewTextureContract.FinalPipelineStage,
                    false,
                    ReviewTextureContract.ProvenanceUnavailableDetail),
                null,
                [
                    new ReviewTextureAssetReport(
                        "LooseSprites/Cursors",
                        ReviewTextureContract.CanonicalGameContentSource,
                        true),
                ],
                new ReviewTexturePage(1, 1, 1, 2, null),
                new ReviewTextureCoverageReport(3, 3, 2, 1, 0),
                []));
}

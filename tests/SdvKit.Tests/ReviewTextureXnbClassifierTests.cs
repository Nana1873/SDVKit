using System.Reflection;
using System.Text;
using SdvKit.AlwaysOn;

namespace SdvKit.Tests;

public sealed class ReviewTextureXnbClassifierTests
{
    private const string TextureReader =
        "Microsoft.Xna.Framework.Content.Texture2DReader, MonoGame.Framework, Version=3.8.0.1641, Culture=neutral, PublicKeyToken=null";
    private const string XnaTextureReader =
        "Microsoft.Xna.Framework.Content.Texture2DReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";
    private const string DictionaryReader =
        "Microsoft.Xna.Framework.Content.DictionaryReader`2[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],[StardewValley.GameData.Objects.ObjectData, StardewValley.GameData, Version=1.6.15.24356, Culture=neutral, PublicKeyToken=null]]";
    private const string GameDataReader =
        "Microsoft.Xna.Framework.Content.ReflectiveReader`1[[StardewValley.GameData.Buildings.BuildingData, StardewValley.GameData, Version=1.6.0.0, Culture=neutral, PublicKeyToken=null]]";
    private const string ForeignReflectiveReader =
        "Microsoft.Xna.Framework.Content.ReflectiveReader`1[[Example.CustomData, Example.Mod, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], MonoGame.Framework";

    [Theory]
    [InlineData(TextureReader, true, true)]
    [InlineData(XnaTextureReader, true, true)]
    [InlineData(DictionaryReader, true, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System.String, System.Private.CoreLib]]", true, false)]
    [InlineData("Microsoft.Xna.Framework.Content.SpriteFontReader, MonoGame.Framework", true, false)]
    [InlineData("Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework", true, false)]
    [InlineData("xTile.Pipeline.TideReader, xTile", true, false)]
    [InlineData("BmFont.XmlSourceReader, BmFont", true, false)]
    [InlineData(GameDataReader, true, false)]
    [InlineData(ForeignReflectiveReader, false, false)]
    [InlineData("Example.CustomReader, Example.Mod", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader, Evil.Framework", false, false)]
    [InlineData("xTile.Pipeline.TideReader, Evil.Framework", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System.String, mscorlib]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.DictionaryReader`2[[System.String, mscorlib]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[.System.String, mscorlib]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System..String, mscorlib]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System.Bad`0, mscorlib]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System.Bad`01, mscorlib]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ListReader`1[[System.String, mscorlib, Unknown=value]]", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader, MonoGame.Framework, Culture=-", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader, MonoGame.Framework, Version=65536.0.0.0", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.ReflectiveReader`1[[StardewValley.GameData.Objects.ObjectData, StardewValley.GameData]], Evil.Framework", false, false)]
    [InlineData(" Microsoft.Xna.Framework.Content.Texture2DReader, MonoGame.Framework", false, false)]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader , MonoGame.Framework", false, false)]
    public void RootReaderAllowlistClassifiesWithoutInstantiatingAssets(
        string rootReader,
        bool expectedClassified,
        bool expectedTexture)
    {
        byte[] xnb = CreateUncompressedXnb(CreateManifest([rootReader]));

        bool classified = TryClassify(
            xnb,
            new FakeLzxDecoder([]),
            long.MaxValue,
            out bool isTexture,
            out long inputBytes);

        Assert.Equal(expectedClassified, classified);
        Assert.Equal(expectedTexture, isTexture);
        Assert.Equal(xnb.Length - 10, inputBytes);
    }

    [Fact]
    public void RootReaderIndexSelectsTheDeclaredReader()
    {
        byte[] xnb = CreateUncompressedXnb(CreateManifest(
            ["Example.UnusedReader, Example.Mod", TextureReader],
            rootIndex: 2));

        Assert.True(TryClassify(
            xnb,
            new FakeLzxDecoder([]),
            long.MaxValue,
            out bool isTexture,
            out _));
        Assert.True(isTexture);
    }

    [Theory]
    [InlineData("w")]
    [InlineData("x")]
    [InlineData("m")]
    [InlineData("i")]
    [InlineData("a")]
    [InlineData("d")]
    [InlineData("X")]
    [InlineData("W")]
    [InlineData("n")]
    [InlineData("M")]
    [InlineData("r")]
    [InlineData("P")]
    [InlineData("v")]
    [InlineData("O")]
    [InlineData("S")]
    [InlineData("G")]
    [InlineData("b")]
    [InlineData("p")]
    [InlineData("g")]
    [InlineData("l")]
    public void KnownMonoGamePlatformMarkersAreAccepted(string platform)
    {
        byte[] xnb = CreateUncompressedXnb(
            CreateManifest([TextureReader]),
            platform: (byte)platform[0]);

        Assert.True(TryClassify(
            xnb,
            new FakeLzxDecoder([]),
            long.MaxValue,
            out bool isTexture,
            out _));
        Assert.True(isTexture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstLzxFrameIsValidatedAndDecodedWithinItsDeclaredBounds(
        bool extendedHeader)
    {
        byte[] manifest = CreateManifest([TextureReader]);
        byte[] decoded = extendedHeader
            ? manifest
            : PrefixWithZeros(manifest, ReviewTextureXnbClassifier.MaximumPrefixBytes);
        byte[] compressed = [1, 2, 3, 4, 5, 6];
        byte[] xnb = CreateCompressedXnb(decoded.Length, compressed, extendedHeader);
        var decoder = new FakeLzxDecoder(decoded);

        Assert.True(TryClassify(
            xnb,
            decoder,
            xnb.Length,
            out bool isTexture,
            out long inputBytes));

        Assert.True(isTexture);
        Assert.Equal(1, decoder.CallCount);
        Assert.Equal(decoded.Length, decoder.DecompressedSize);
        Assert.Equal(compressed.Length + (extendedHeader ? 5 : 2), inputBytes);
        Assert.Equal(inputBytes, decoder.CompressedSize);
        Assert.Equal(
            xnb[^checked((int)inputBytes)..],
            decoder.FramedInput);
    }

    [Fact]
    public void NormalLzxFrameRejectsADeclaredOutputShorterThanTheFixedFrame()
    {
        byte[] manifest = CreateManifest([TextureReader]);
        byte[] xnb = CreateCompressedXnb(
            manifest.Length,
            [1, 2, 3],
            extendedHeader: false);
        var decoder = new FakeLzxDecoder(manifest);

        AssertGap(xnb, decoder);
        Assert.Equal(0, decoder.CallCount);
    }

    [Fact]
    public void DecoderFailureConsumesTheValidatedFrameBudget()
    {
        byte[] manifest = CreateManifest([TextureReader]);
        byte[] xnb = CreateCompressedXnb(
            manifest.Length,
            [1, 2, 3],
            extendedHeader: true);

        Assert.False(TryClassify(
            xnb,
            new ThrowingLzxDecoder(),
            long.MaxValue,
            out bool isTexture,
            out long inputBytes));

        Assert.False(isTexture);
        Assert.Equal(xnb.Length - 14, inputBytes);
    }

    [Fact]
    public void FatalInvocationFailureIsNotConvertedToAClassificationGap()
    {
        byte[] manifest = CreateManifest([TextureReader]);
        byte[] xnb = CreateCompressedXnb(
            manifest.Length,
            [1, 2, 3],
            extendedHeader: true);
        var fatal = new TargetInvocationException(
            Assert.IsType<OutOfMemoryException>(Activator.CreateInstance(
                typeof(OutOfMemoryException),
                "Synthetic fatal failure.")));

        TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() =>
            TryClassify(
                xnb,
                new ThrowingLzxDecoder(fatal),
                long.MaxValue,
                out _,
                out _));

        Assert.Same(fatal, thrown);
        Assert.True(ReviewException.IsFatal(thrown));
        Assert.False(ReviewException.IsFatal(
            new TargetInvocationException(new InvalidDataException())));
    }

    [Fact]
    public void LzxFrameBudgetIsCheckedBeforeDecompression()
    {
        byte[] manifest = CreateManifest([TextureReader]);
        byte[] xnb = CreateCompressedXnb(
            manifest.Length,
            [1, 2, 3, 4, 5, 6],
            extendedHeader: false);
        var decoder = new FakeLzxDecoder(manifest);
        long required = xnb.Length - 14;

        Assert.False(TryClassify(
            xnb,
            decoder,
            required - 1,
            out bool isTexture,
            out long inputBytes));

        Assert.False(isTexture);
        Assert.Equal(required, inputBytes);
        Assert.Equal(0, decoder.CallCount);
    }

    [Fact]
    public void MalformedHeadersAndManifestBoundsRemainUnclassified()
    {
        byte[] valid = CreateUncompressedXnb(CreateManifest([TextureReader]));

        AssertGap(Mutate(valid, 0, (byte)'Q'));
        AssertGap(Mutate(valid, 3, (byte)'z'));
        AssertGap(Mutate(valid, 4, 3));
        AssertGap(Mutate(valid, 5, 0x40));
        AssertGap(Mutate(valid, 6, 0));
        AssertGap(CreateUncompressedXnb([0x81, 0x00]));
        AssertGap(CreateUncompressedXnb(CreateManifest(
            Enumerable.Repeat(TextureReader, 129).ToArray())));
        AssertGap(CreateUncompressedXnb(CreateManifestWithRawReaderName(
            Enumerable.Repeat((byte)'A', 4097).ToArray())));
        AssertGap(CreateUncompressedXnb(CreateManifestWithRawReaderName([0xff])));
        AssertGap(CreateUncompressedXnb(CreateManifest([TextureReader], rootIndex: 0)));
    }

    [Fact]
    public void ExtendedLzxFrameRejectsOversizedOutputAndFileEscape()
    {
        byte[] oversizedOutput = CreateCompressedXnb(
            declaredOutput: ReviewTextureXnbClassifier.MaximumPrefixBytes + 1,
            compressed: [1],
            extendedHeader: true);
        AssertGap(oversizedOutput, new FakeLzxDecoder([]));

        byte[] truncated = CreateCompressedXnb(
            declaredOutput: 64,
            compressed: [1, 2],
            extendedHeader: true,
            declaredCompressedLength: 10);
        AssertGap(truncated, new FakeLzxDecoder([]));
    }

    [Fact]
    public void PhysicalPathResolutionRejectsTraversalAndReparseDirectories()
    {
        using TemporaryDirectory temporary = new();
        string maps = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "Maps")).FullName;
        File.WriteAllBytes(
            Path.Combine(maps, "Farm.xnb"),
            CreateUncompressedXnb(CreateManifest([TextureReader])));
        var classifier = new ReviewTextureXnbClassifier(
            temporary.Path,
            new FakeLzxDecoder([]));

        Assert.True(classifier.TryClassify(
            "Maps/Farm",
            long.MaxValue,
            out bool isTexture,
            out _));
        Assert.True(isTexture);
        Assert.False(classifier.TryClassify(
            "../Maps/Farm",
            long.MaxValue,
            out _,
            out _));

        string outside = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "outside")).FullName;
        File.WriteAllBytes(
            Path.Combine(outside, "Linked.xnb"),
            CreateUncompressedXnb(CreateManifest([TextureReader])));
        string link = Path.Combine(temporary.Path, "Linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(classifier.TryClassify(
            "Linked/Linked",
            long.MaxValue,
            out _,
            out _));
    }

    [Fact]
    public void ConstructionDoesNotTouchAMissingContentRoot()
    {
        using TemporaryDirectory temporary = new();
        string missing = Path.Combine(temporary.Path, "missing");

        var classifier = new ReviewTextureXnbClassifier(
            missing,
            new FakeLzxDecoder([]));

        Assert.NotNull(classifier);
    }

    [Fact]
    public void MissingMonoGameDecoderTypeFailsClosed()
    {
        var decoder = new ReviewTextureLzxReflectionDecoder(
            typeof(ReviewTextureXnbClassifierTests).Assembly);

        Assert.False(decoder.TryDecode(
            new MemoryStream([0, 1], writable: false),
            1,
            2,
            new byte[1]));
    }

    private static bool TryClassify(
        byte[] xnb,
        IReviewTextureLzxDecoder decoder,
        long maximumInputBytes,
        out bool isTexture,
        out long inputBytes) =>
        ReviewTextureXnbClassifier.TryClassify(
            new MemoryStream(xnb, writable: false),
            decoder,
            maximumInputBytes,
            out isTexture,
            out inputBytes);

    private static void AssertGap(
        byte[] xnb,
        IReviewTextureLzxDecoder? decoder = null)
    {
        Assert.False(TryClassify(
            xnb,
            decoder ?? new FakeLzxDecoder([]),
            long.MaxValue,
            out bool isTexture,
            out _));
        Assert.False(isTexture);
    }

    private static byte[] CreateManifest(
        IReadOnlyList<string> readers,
        int rootIndex = 1)
    {
        using var output = new MemoryStream();
        Write7BitEncodedInt(output, readers.Count);
        foreach (string reader in readers)
        {
            byte[] name = Encoding.UTF8.GetBytes(reader);
            Write7BitEncodedInt(output, name.Length);
            output.Write(name);
            output.Write(new byte[4]);
        }

        Write7BitEncodedInt(output, 0);
        Write7BitEncodedInt(output, rootIndex);
        return output.ToArray();
    }

    private static byte[] CreateManifestWithRawReaderName(byte[] name)
    {
        using var output = new MemoryStream();
        Write7BitEncodedInt(output, 1);
        Write7BitEncodedInt(output, name.Length);
        output.Write(name);
        output.Write(new byte[4]);
        Write7BitEncodedInt(output, 0);
        Write7BitEncodedInt(output, 1);
        return output.ToArray();
    }

    private static byte[] CreateUncompressedXnb(
        byte[] payload,
        byte platform = (byte)'w')
    {
        using var output = new MemoryStream();
        output.Write([(byte)'X', (byte)'N', (byte)'B', platform, 5, 0]);
        output.Write(BitConverter.GetBytes(checked(payload.Length + 10)));
        output.Write(payload);
        return output.ToArray();
    }

    private static byte[] CreateCompressedXnb(
        int declaredOutput,
        byte[] compressed,
        bool extendedHeader,
        int? declaredCompressedLength = null)
    {
        using var frame = new MemoryStream();
        int compressedLength = declaredCompressedLength ?? compressed.Length;
        if (extendedHeader)
        {
            frame.WriteByte(byte.MaxValue);
            frame.WriteByte((byte)(declaredOutput >> 8));
            frame.WriteByte((byte)declaredOutput);
        }

        frame.WriteByte((byte)(compressedLength >> 8));
        frame.WriteByte((byte)compressedLength);
        frame.Write(compressed);
        byte[] framed = frame.ToArray();

        using var output = new MemoryStream();
        output.Write([(byte)'X', (byte)'N', (byte)'B', (byte)'d', 5, 0x81]);
        output.Write(BitConverter.GetBytes(checked(framed.Length + 14)));
        output.Write(BitConverter.GetBytes(declaredOutput));
        output.Write(framed);
        return output.ToArray();
    }

    private static byte[] Mutate(byte[] source, int index, byte value)
    {
        byte[] copy = source.ToArray();
        copy[index] = value;
        return copy;
    }

    private static byte[] PrefixWithZeros(byte[] prefix, int length)
    {
        var result = new byte[length];
        prefix.CopyTo(result, 0);
        return result;
    }

    private static void Write7BitEncodedInt(Stream output, int value)
    {
        uint remaining = checked((uint)value);
        while (remaining >= 0x80)
        {
            output.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        output.WriteByte((byte)remaining);
    }

    private sealed class FakeLzxDecoder(byte[] output) : IReviewTextureLzxDecoder
    {
        public int CallCount { get; private set; }

        public int DecompressedSize { get; private set; }

        public int CompressedSize { get; private set; }

        public byte[]? FramedInput { get; private set; }

        public bool TryDecode(
            Stream input,
            int decompressedSize,
            int compressedSize,
            byte[] destination)
        {
            CallCount++;
            DecompressedSize = decompressedSize;
            CompressedSize = compressedSize;
            using var captured = new MemoryStream();
            input.CopyTo(captured);
            FramedInput = captured.ToArray();
            if (output.Length != destination.Length)
            {
                return false;
            }

            output.CopyTo(destination, 0);
            return true;
        }
    }

    private sealed class ThrowingLzxDecoder(Exception? exception = null) : IReviewTextureLzxDecoder
    {
        private readonly Exception _exception = exception
            ?? new InvalidDataException("Synthetic decoder failure.");

        public bool TryDecode(
            Stream input,
            int decompressedSize,
            int compressedSize,
            byte[] destination) =>
            throw _exception;
    }
}

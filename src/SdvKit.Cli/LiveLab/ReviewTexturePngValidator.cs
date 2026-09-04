using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SdvKit.Cli.LiveLab;

internal sealed record ReviewTexturePngInfo(
    int Width,
    int Height,
    string PixelSha256);

internal static class ReviewTexturePngValidator
{
    private const int BytesPerPixel = 4;
    private const int MaximumChunkCount = 1024;
    private const uint CrcPolynomial = 0xedb88320u;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] IhdrType = "IHDR"u8.ToArray();
    private static readonly byte[] PlteType = "PLTE"u8.ToArray();
    private static readonly byte[] IdatType = "IDAT"u8.ToArray();
    private static readonly byte[] IendType = "IEND"u8.ToArray();

    public static bool TryValidateRgba8(
        Stream stream,
        int maximumEncodedBytes,
        int maximumDimension,
        int maximumPixels,
        out ReviewTexturePngInfo? info)
    {
        info = null;
        if (stream is null
            || maximumEncodedBytes < 1
            || maximumDimension < 1
            || maximumPixels < 1
            || !stream.CanRead
            || !stream.CanSeek)
        {
            return false;
        }

        try
        {
            long encodedLength = stream.Length;
            if (encodedLength is < 57 || encodedLength > maximumEncodedBytes)
            {
                return false;
            }

            stream.Position = 0;
            Span<byte> signature = stackalloc byte[Signature.Length];
            if (!TryReadExactly(stream, signature)
                || !signature.SequenceEqual(Signature))
            {
                return false;
            }

            var width = 0;
            var height = 0;
            var sawHeader = false;
            var sawPalette = false;
            var sawImageData = false;
            var imageDataEnded = false;
            var sawEnd = false;
            var chunks = 0;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> storedCrcBytes = stackalloc byte[4];
            using var compressed = new MemoryStream();
            while (stream.Position < encodedLength)
            {
                chunks++;
                if (chunks > MaximumChunkCount)
                {
                    return false;
                }

                if (!TryReadExactly(stream, chunkHeader))
                {
                    return false;
                }

                uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(
                    chunkHeader[..4]);
                if (unsignedLength > maximumEncodedBytes)
                {
                    return false;
                }

                int chunkLength = checked((int)unsignedLength);
                ReadOnlySpan<byte> chunkType = chunkHeader[4..8];
                if (!IsValidChunkType(chunkType)
                    || encodedLength - stream.Position < (long)chunkLength + 4)
                {
                    return false;
                }

                var chunkData = new byte[chunkLength];
                if (!TryReadExactly(stream, chunkData))
                {
                    return false;
                }

                if (!TryReadExactly(stream, storedCrcBytes))
                {
                    return false;
                }

                uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(storedCrcBytes);
                if (storedCrc != ComputeCrc(chunkType, chunkData))
                {
                    return false;
                }

                if (chunkType.SequenceEqual(IhdrType))
                {
                    if (chunks != 1 || sawHeader || chunkLength != 13)
                    {
                        return false;
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(chunkData.AsSpan(0, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(chunkData.AsSpan(4, 4));
                    if (width <= 0
                        || height <= 0
                        || width > maximumDimension
                        || height > maximumDimension
                        || (long)width * height > maximumPixels
                        || chunkData[8] != 8
                        || chunkData[9] != 6
                        || chunkData[10] != 0
                        || chunkData[11] != 0
                        || chunkData[12] != 0)
                    {
                        return false;
                    }

                    sawHeader = true;
                    continue;
                }

                if (!sawHeader || sawEnd)
                {
                    return false;
                }

                if (chunkType.SequenceEqual(PlteType))
                {
                    if (sawPalette
                        || sawImageData
                        || chunkLength is < 3 or > 768
                        || chunkLength % 3 != 0)
                    {
                        return false;
                    }

                    sawPalette = true;
                    continue;
                }

                if (chunkType.SequenceEqual(IdatType))
                {
                    if (imageDataEnded
                        || compressed.Length + chunkLength > maximumEncodedBytes)
                    {
                        return false;
                    }

                    compressed.Write(chunkData);
                    sawImageData = true;
                    continue;
                }

                if (chunkType.SequenceEqual(IendType))
                {
                    if (!sawImageData
                        || chunkLength != 0
                        || stream.Position != encodedLength)
                    {
                        return false;
                    }

                    sawEnd = true;
                    break;
                }

                if (IsCriticalChunk(chunkType))
                {
                    return false;
                }

                if (sawImageData)
                {
                    imageDataEnded = true;
                }
            }

            if (!sawHeader || !sawImageData || !sawEnd || compressed.Length == 0)
            {
                return false;
            }

            compressed.Position = 0;
            if (!TryDecodePixels(compressed, width, height, out byte[]? pixels)
                || pixels is null)
            {
                return false;
            }

            info = new ReviewTexturePngInfo(
                width,
                height,
                Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant());
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException)
        {
            info = null;
            return false;
        }
    }

    private static bool TryDecodePixels(
        Stream compressed,
        int width,
        int height,
        out byte[]? pixels)
    {
        pixels = null;
        int stride = checked(width * BytesPerPixel);
        int filteredLength = checked((stride + 1) * height);
        var filtered = new byte[filteredLength];
        using (var exactInput = new NoReadAheadStream(compressed))
        using (var inflater = new ZLibStream(
            exactInput,
            CompressionMode.Decompress,
            leaveOpen: true))
        {
            if (!TryReadExactly(inflater, filtered) || inflater.ReadByte() != -1)
            {
                return false;
            }
        }
        if (compressed.Position != compressed.Length)
        {
            return false;
        }

        var decoded = new byte[checked(stride * height)];
        var inputOffset = 0;
        for (var y = 0; y < height; y++)
        {
            byte filter = filtered[inputOffset++];
            if (filter > 4)
            {
                return false;
            }

            int rowOffset = y * stride;
            int previousRowOffset = rowOffset - stride;
            for (var x = 0; x < stride; x++)
            {
                byte encoded = filtered[inputOffset++];
                byte left = x >= BytesPerPixel
                    ? decoded[rowOffset + x - BytesPerPixel]
                    : (byte)0;
                byte above = y > 0
                    ? decoded[previousRowOffset + x]
                    : (byte)0;
                byte upperLeft = y > 0 && x >= BytesPerPixel
                    ? decoded[previousRowOffset + x - BytesPerPixel]
                    : (byte)0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException(
                        "The PNG row filter is unsupported."),
                };
                decoded[rowOffset + x] = unchecked((byte)(encoded + predictor));
            }
        }

        pixels = decoded;
        return true;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
        {
            return left;
        }

        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static bool IsValidChunkType(ReadOnlySpan<byte> type)
    {
        if (type.Length != 4
            || type[2] is not (>= (byte)'A' and <= (byte)'Z'))
        {
            return false;
        }

        foreach (byte value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z')
                and not (>= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCriticalChunk(ReadOnlySpan<byte> type) =>
        type[0] is >= (byte)'A' and <= (byte)'Z';

    private static uint ComputeCrc(
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> chunkData)
    {
        uint crc = UpdateCrc(uint.MaxValue, chunkType);
        crc = UpdateCrc(crc, chunkData);
        return crc ^ uint.MaxValue;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> values)
    {
        foreach (byte value in values)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? CrcPolynomial ^ (crc >> 1)
                    : crc >> 1;
            }
        }

        return crc;
    }

    private sealed class NoReadAheadStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) =>
            _inner.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

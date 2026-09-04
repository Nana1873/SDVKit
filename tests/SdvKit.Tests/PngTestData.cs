using System.Buffers.Binary;
using System.IO.Compression;

namespace SdvKit.Tests;

internal static class PngTestData
{
    private const uint CrcPolynomial = 0xedb88320u;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] CreateRgba8(
        int width,
        int height,
        byte[]? pixels = null,
        byte[]? trailingCompressedBytes = null) =>
        CreateRgb8(
            width,
            height,
            bytesPerPixel: 4,
            colorType: 6,
            format: "RGBA8",
            pixels,
            trailingCompressedBytes);

    public static byte[] CreateRgb8(
        int width,
        int height,
        byte[]? pixels = null,
        byte[]? trailingCompressedBytes = null) =>
        CreateRgb8(
            width,
            height,
            bytesPerPixel: 3,
            colorType: 2,
            format: "RGB8",
            pixels,
            trailingCompressedBytes);

    private static byte[] CreateRgb8(
        int width,
        int height,
        int bytesPerPixel,
        byte colorType,
        string format,
        byte[]? pixels,
        byte[]? trailingCompressedBytes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        pixels ??= new byte[checked(width * height * bytesPerPixel)];
        if (pixels.Length != checked(width * height * bytesPerPixel))
        {
            throw new ArgumentException(
                $"The test pixel buffer must contain exact {format} pixels.",
                nameof(pixels));
        }

        using var filtered = new MemoryStream();
        int stride = checked(width * bytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            filtered.WriteByte(0);
            filtered.Write(pixels, y * stride, stride);
        }

        using var compressed = new MemoryStream();
        using (var compressor = new ZLibStream(
            compressed,
            CompressionLevel.SmallestSize,
            leaveOpen: true))
        {
            filtered.Position = 0;
            filtered.CopyTo(compressor);
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = colorType;
        WriteChunk(png, "IHDR"u8, header);
        byte[] imageData = trailingCompressedBytes is null
            ? compressed.ToArray()
            : [.. compressed.ToArray(), .. trailingCompressedBytes];
        WriteChunk(png, "IDAT"u8, imageData);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            crcBytes,
            ComputeCrc(type, data));
        output.Write(crcBytes);
    }

    private static uint ComputeCrc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        uint crc = UpdateCrc(uint.MaxValue, type);
        crc = UpdateCrc(crc, data);
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
}

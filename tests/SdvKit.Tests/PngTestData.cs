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
        byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        pixels ??= new byte[checked(width * height * 4)];
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException(
                "The test pixel buffer must contain exact RGBA8 pixels.",
                nameof(pixels));
        }

        using var filtered = new MemoryStream();
        int stride = checked(width * 4);
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
        header[9] = 6;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ModBuildIdentity
{
    private static readonly string[] RequiredFiles =
    [
        "SdvKit.AlwaysOn.dll",
        "manifest.json",
    ];

    public static string Compute(string modPath)
    {
        if (string.IsNullOrWhiteSpace(modPath))
        {
            throw new ArgumentException("The mod path is required.", nameof(modPath));
        }

        string absoluteModPath = Path.GetFullPath(modPath);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        byte[] buffer = new byte[81920];
        foreach (string fileName in RequiredFiles)
        {
            string path = Path.Combine(absoluteModPath, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The declared AlwaysOn build is missing {fileName}.",
                    path);
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            byte[] name = Encoding.UTF8.GetBytes(fileName);
            BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
            hash.AppendData(name);
            hash.AppendData([0]);
            hash.AppendData(length);

            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    public static bool IsValid(string? value)
    {
        const int Sha256HexLength = 64;
        if (value is null
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Length != "sha256:".Length + Sha256HexLength)
        {
            return false;
        }

        return value["sha256:".Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

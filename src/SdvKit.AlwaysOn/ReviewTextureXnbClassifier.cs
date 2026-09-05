using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal interface IReviewTextureLzxDecoder
{
    bool TryDecode(
        Stream input,
        int decompressedSize,
        int compressedSize,
        byte[] destination);
}

internal sealed class ReviewTextureLzxReflectionDecoder : IReviewTextureLzxDecoder
{
    private const string DecoderTypeName =
        "MonoGame.Framework.Utilities.LzxDecoderStream";
    private readonly ConstructorInfo? _constructor;

    public ReviewTextureLzxReflectionDecoder(Assembly monoGameAssembly)
    {
        ArgumentNullException.ThrowIfNull(monoGameAssembly);
        Type? decoderType = monoGameAssembly.GetType(
            DecoderTypeName,
            throwOnError: false,
            ignoreCase: false);
        _constructor = decoderType?.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(Stream), typeof(int), typeof(int)],
            modifiers: null);
    }

    public bool TryDecode(
        Stream input,
        int decompressedSize,
        int compressedSize,
        byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destination);
        if (_constructor is null
            || decompressedSize <= 0
            || decompressedSize != destination.Length
            || compressedSize <= 0
            || !input.CanRead
            || !input.CanSeek
            || input.Position != 0
            || input.Length != compressedSize)
        {
            return false;
        }

        try
        {
            object? created = _constructor.Invoke(
                [input, decompressedSize, compressedSize]);
            if (created is not Stream decoder)
            {
                (created as IDisposable)?.Dispose();
                return false;
            }

            using (decoder)
            {
                var offset = 0;
                while (offset < destination.Length)
                {
                    int read = decoder.Read(
                        destination,
                        offset,
                        destination.Length - offset);
                    if (read <= 0)
                    {
                        return false;
                    }

                    offset += read;
                }

                return decoder.ReadByte() == -1;
            }
        }
        catch (Exception exception) when (!ReviewException.IsFatal(exception))
        {
            return false;
        }
    }
}

internal sealed class ReviewTextureXnbClassifier
{
    internal const int MaximumPrefixBytes = 32 * 1024;
    internal const long MaximumTotalInputBytes = 64L * 1024 * 1024;
    internal const int MaximumReaderCount = 128;
    internal const int MaximumReaderNameBytes = 4096;
    internal const int MaximumSharedResources = 4096;

    private const byte HiDefFlag = 0x01;
    private const byte LzxFlag = 0x80;
    private const string TextureReader =
        "Microsoft.Xna.Framework.Content.Texture2DReader";
    private const string ReflectiveReader =
        "Microsoft.Xna.Framework.Content.ReflectiveReader`1";
    private const string GameDataAssembly = "StardewValley.GameData";
    private const string KnownPlatformIdentifiers = "wxmiadXWnMrPvOSGbpgl";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> CoreFrameworkAssemblies = new(
        StringComparer.Ordinal)
    {
        "Microsoft.Xna.Framework",
        "MonoGame.Framework",
    };
    private static readonly HashSet<string> GraphicsFrameworkAssemblies = new(
        StringComparer.Ordinal)
    {
        "Microsoft.Xna.Framework.Graphics",
        "MonoGame.Framework",
    };
    private static readonly HashSet<string> XTileAssemblies = new(
        ["xTile"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> BmFontAssemblies = new(
        ["BmFont"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> GameDataAssemblies = new(
        [GameDataAssembly],
        StringComparer.Ordinal);

    private readonly string _contentRoot;
    private readonly IReviewTextureLzxDecoder _decoder;

    public ReviewTextureXnbClassifier(
        string contentRoot,
        IReviewTextureLzxDecoder decoder)
    {
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            throw new ArgumentException(
                "The canonical content root is required.",
                nameof(contentRoot));
        }
        ArgumentNullException.ThrowIfNull(decoder);
        _contentRoot = Path.GetFullPath(contentRoot);
        _decoder = decoder;
    }

    public bool TryClassify(
        string assetName,
        long maximumInputBytes,
        out bool isTexture,
        out long inputBytes)
    {
        isTexture = false;
        inputBytes = 0;
        if (!ReviewTextureContract.IsCanonicalAssetName(assetName)
            || maximumInputBytes < 0
            || !TryResolvePhysicalAsset(assetName, out string? path))
        {
            return false;
        }

        using var stream = new FileStream(
            path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return TryClassify(
            stream,
            _decoder,
            maximumInputBytes,
            out isTexture,
            out inputBytes);
    }

    internal static bool TryClassify(
        Stream stream,
        IReviewTextureLzxDecoder decoder,
        long maximumInputBytes,
        out bool isTexture,
        out long inputBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(decoder);
        isTexture = false;
        inputBytes = 0;
        if (maximumInputBytes < 0
            || !stream.CanRead
            || !stream.CanSeek
            || stream.Length is < 11 or > int.MaxValue)
        {
            return false;
        }

        stream.Position = 0;
        var header = new byte[10];
        if (!TryReadExact(stream, header)
            || header[0] != (byte)'X'
            || header[1] != (byte)'N'
            || header[2] != (byte)'B'
            || !KnownPlatformIdentifiers.Contains((char)header[3])
            || header[4] is not 4 and not 5
            || (header[5] & ~(HiDefFlag | LzxFlag)) != 0)
        {
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(6, sizeof(int)));
        if (declaredLength != stream.Length)
        {
            return false;
        }

        byte[] prefix;
        if ((header[5] & LzxFlag) == 0)
        {
            int prefixLength = (int)Math.Min(
                MaximumPrefixBytes,
                stream.Length - header.Length);
            inputBytes = prefixLength;
            if (prefixLength <= 0 || inputBytes > maximumInputBytes)
            {
                return false;
            }

            prefix = new byte[prefixLength];
            if (!TryReadExact(stream, prefix))
            {
                return false;
            }
        }
        else
        {
            var sizeBytes = new byte[sizeof(int)];
            if (!TryReadExact(stream, sizeBytes))
            {
                return false;
            }

            int declaredOutput = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
            if (declaredOutput <= 0
                || !TryReadFirstFrame(
                    stream,
                    declaredOutput,
                    maximumInputBytes,
                    out byte[]? frame,
                    out int frameOutput,
                    out inputBytes)
                || frame is null)
            {
                return false;
            }

            prefix = new byte[frameOutput];
            using var frameStream = new MemoryStream(frame, writable: false);
            bool decoded;
            try
            {
                decoded = decoder.TryDecode(
                    frameStream,
                    frameOutput,
                    frame.Length,
                    prefix);
            }
            catch (Exception exception) when (!ReviewException.IsFatal(exception))
            {
                decoded = false;
            }

            if (!decoded)
            {
                return false;
            }
        }

        if (!TryReadRootReader(prefix, out string? rootReader)
            || rootReader is null)
        {
            return false;
        }

        if (!TryParseReaderName(
                rootReader,
                out string? outerReader,
                out string? genericArguments,
                out string? assemblySuffix)
            || outerReader is null
            || assemblySuffix is null)
        {
            return false;
        }

        if (string.Equals(outerReader, TextureReader, StringComparison.Ordinal)
            && genericArguments is null
            && HasAllowedAssembly(
                assemblySuffix,
                GraphicsFrameworkAssemblies,
                allowUnqualified: false))
        {
            isTexture = true;
            return true;
        }

        return IsKnownNonTextureReader(
            outerReader,
            genericArguments,
            assemblySuffix);
    }

    private bool TryResolvePhysicalAsset(string assetName, out string? path)
    {
        path = null;
        string[] segments = assetName.Split('/');
        string current = _contentRoot;
        if (!IsRegularDirectory(current))
        {
            return false;
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!IsRegularDirectory(current))
            {
                return false;
            }
        }

        string candidate = Path.GetFullPath(Path.Combine(
            current,
            segments[^1] + ".xnb"));
        string relative = Path.GetRelativePath(_contentRoot, candidate);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return false;
        }

        FileAttributes attributes = File.GetAttributes(candidate);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool TryReadFirstFrame(
        Stream stream,
        int declaredOutput,
        long maximumInputBytes,
        out byte[]? frame,
        out int frameOutput,
        out long inputBytes)
    {
        frame = null;
        frameOutput = 0;
        inputBytes = 0;
        var frameHeader = new byte[5];
        if (!TryReadExact(stream, frameHeader.AsSpan(0, 2)))
        {
            return false;
        }

        int headerLength;
        int compressedLength;
        if (frameHeader[0] == byte.MaxValue)
        {
            if (!TryReadExact(stream, frameHeader.AsSpan(2, 3)))
            {
                return false;
            }

            headerLength = 5;
            frameOutput = (frameHeader[1] << 8) | frameHeader[2];
            compressedLength = (frameHeader[3] << 8) | frameHeader[4];
        }
        else
        {
            headerLength = 2;
            frameOutput = MaximumPrefixBytes;
            compressedLength = (frameHeader[0] << 8) | frameHeader[1];
        }

        inputBytes = (long)headerLength + compressedLength;
        if (frameOutput is < 1 or > MaximumPrefixBytes
            || frameOutput > declaredOutput
            || compressedLength <= 0
            || inputBytes > maximumInputBytes
            || inputBytes > stream.Length - (stream.Position - headerLength))
        {
            return false;
        }

        frame = new byte[checked((int)inputBytes)];
        frameHeader.AsSpan(0, headerLength).CopyTo(frame);
        return TryReadExact(stream, frame.AsSpan(headerLength, compressedLength));
    }

    private static bool TryReadRootReader(byte[] prefix, out string? rootReader)
    {
        rootReader = null;
        var offset = 0;
        if (!TryRead7BitEncodedInt(prefix, ref offset, out int readerCount)
            || readerCount is < 1 or > MaximumReaderCount)
        {
            return false;
        }

        var readers = new string[readerCount];
        for (var index = 0; index < readers.Length; index++)
        {
            if (!TryRead7BitEncodedInt(prefix, ref offset, out int byteCount)
                || byteCount is < 1 or > MaximumReaderNameBytes
                || byteCount > prefix.Length - offset)
            {
                return false;
            }

            try
            {
                readers[index] = StrictUtf8.GetString(prefix, offset, byteCount);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            if (!ReviewTransportText.IsWellFormedUtf16(readers[index])
                || readers[index].Any(char.IsControl))
            {
                return false;
            }

            offset += byteCount;
            if (prefix.Length - offset < sizeof(int))
            {
                return false;
            }

            offset += sizeof(int);
        }

        if (!TryRead7BitEncodedInt(prefix, ref offset, out int sharedResources)
            || sharedResources is < 0 or > MaximumSharedResources
            || !TryRead7BitEncodedInt(prefix, ref offset, out int rootIndex)
            || rootIndex < 1
            || rootIndex > readers.Length)
        {
            return false;
        }

        rootReader = readers[rootIndex - 1];
        return true;
    }

    private static bool TryRead7BitEncodedInt(
        byte[] bytes,
        ref int offset,
        out int value)
    {
        value = 0;
        uint result = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= bytes.Length)
            {
                return false;
            }

            byte current = bytes[offset++];
            if (index == 4 && (current & 0xf8) != 0)
            {
                return false;
            }

            result |= (uint)(current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
            {
                if (index > 0 && result < (1U << (index * 7)))
                {
                    return false;
                }

                value = (int)result;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseReaderName(
        string readerName,
        out string? outerReader,
        out string? genericArguments,
        out string? assemblySuffix)
    {
        outerReader = null;
        genericArguments = null;
        assemblySuffix = null;
        int bracket = readerName.IndexOf('[');
        int comma = readerName.IndexOf(',');
        if (bracket < 0)
        {
            int typeEnd = comma >= 0 ? comma : readerName.Length;
            string typeName = readerName[..typeEnd];
            if (!IsExactTypeName(typeName))
            {
                return false;
            }

            outerReader = typeName;
            assemblySuffix = readerName[typeEnd..];
            return true;
        }

        if (bracket == 0
            || bracket + 1 >= readerName.Length
            || readerName[bracket + 1] != '['
            || (comma >= 0 && comma < bracket)
            || !IsExactTypeName(readerName[..bracket]))
        {
            return false;
        }

        var depth = 0;
        var closingBracket = -1;
        for (var index = bracket; index < readerName.Length; index++)
        {
            if (readerName[index] == '[')
            {
                depth++;
            }
            else if (readerName[index] == ']')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
                if (depth == 0)
                {
                    closingBracket = index;
                    break;
                }
            }
        }

        if (closingBracket < bracket + 3
            || readerName[closingBracket - 1] != ']')
        {
            return false;
        }

        outerReader = readerName[..bracket];
        genericArguments = readerName[bracket..(closingBracket + 1)];
        assemblySuffix = readerName[(closingBracket + 1)..];
        return true;
    }

    private static bool IsKnownNonTextureReader(
        string outerReader,
        string? genericArguments,
        string assemblySuffix)
    {
        if (string.Equals(
                outerReader,
                "Microsoft.Xna.Framework.Content.DictionaryReader`2",
                StringComparison.Ordinal))
        {
            return genericArguments is not null
                && TryValidateGenericArguments(genericArguments, 2, out _)
                && HasAllowedAssembly(
                    assemblySuffix,
                    CoreFrameworkAssemblies,
                    allowUnqualified: true);
        }

        if (string.Equals(
                outerReader,
                "Microsoft.Xna.Framework.Content.ListReader`1",
                StringComparison.Ordinal))
        {
            return genericArguments is not null
                && TryValidateGenericArguments(genericArguments, 1, out _)
                && HasAllowedAssembly(
                    assemblySuffix,
                    CoreFrameworkAssemblies,
                    allowUnqualified: true);
        }

        if (string.Equals(
                outerReader,
                "Microsoft.Xna.Framework.Content.SpriteFontReader",
                StringComparison.Ordinal)
            || string.Equals(
                outerReader,
                "Microsoft.Xna.Framework.Content.EffectReader",
                StringComparison.Ordinal))
        {
            return genericArguments is null
                && HasAllowedAssembly(
                    assemblySuffix,
                    GraphicsFrameworkAssemblies,
                    allowUnqualified: false);
        }

        if (string.Equals(outerReader, "xTile.Pipeline.TideReader", StringComparison.Ordinal))
        {
            return genericArguments is null
                && HasAllowedAssembly(
                    assemblySuffix,
                    XTileAssemblies,
                    allowUnqualified: false);
        }

        if (string.Equals(outerReader, "BmFont.XmlSourceReader", StringComparison.Ordinal))
        {
            return genericArguments is null
                && HasAllowedAssembly(
                    assemblySuffix,
                    BmFontAssemblies,
                    allowUnqualified: false);
        }

        return string.Equals(outerReader, ReflectiveReader, StringComparison.Ordinal)
            && genericArguments is not null
            && HasAllowedAssembly(
                assemblySuffix,
                CoreFrameworkAssemblies,
                allowUnqualified: true)
            && IsGameDataReflectiveArgument(genericArguments);
    }

    private static bool IsGameDataReflectiveArgument(string genericArgument)
    {
        if (!TryValidateGenericArguments(
                genericArgument,
                1,
                out string[]? arguments)
            || arguments is null
            || !TryParseAssemblyQualifiedType(
                arguments[0],
                out string? targetType,
                out string? targetAssembly)
            || targetType is null
            || targetAssembly is null)
        {
            return false;
        }

        return targetType.StartsWith(GameDataAssembly + ".", StringComparison.Ordinal)
            && GameDataAssemblies.Contains(targetAssembly);
    }

    private static bool HasAllowedAssembly(
        string suffix,
        HashSet<string> allowedAssemblies,
        bool allowUnqualified)
    {
        if (suffix.Length == 0)
        {
            return allowUnqualified;
        }

        return suffix.StartsWith(", ", StringComparison.Ordinal)
            && IsAllowedAssemblySpecification(suffix[2..], allowedAssemblies);
    }

    private static bool IsAllowedAssemblySpecification(
        string specification,
        HashSet<string> allowedAssemblies)
    {
        return TryParseAssemblySpecification(specification, out string? assemblyName)
            && assemblyName is not null
            && allowedAssemblies.Contains(assemblyName);
    }

    private static bool TryValidateGenericArguments(
        string value,
        int expectedCount,
        out string[]? arguments)
    {
        arguments = null;
        if (expectedCount <= 0
            || value.Length < 4
            || value[0] != '['
            || value[1] != '['
            || value[^2] != ']'
            || value[^1] != ']')
        {
            return false;
        }

        var parsed = new string[expectedCount];
        var offset = 1;
        for (var index = 0; index < parsed.Length; index++)
        {
            if (offset >= value.Length - 1 || value[offset] != '[')
            {
                return false;
            }

            int start = ++offset;
            while (offset < value.Length - 1 && value[offset] != ']')
            {
                if (value[offset] == '[')
                {
                    return false;
                }

                offset++;
            }

            if (offset == start
                || offset >= value.Length - 1
                || !TryParseAssemblyQualifiedType(
                    value[start..offset],
                    out _,
                    out _))
            {
                return false;
            }

            parsed[index] = value[start..offset];
            offset++;
            if (index < parsed.Length - 1)
            {
                if (offset >= value.Length - 1 || value[offset] != ',')
                {
                    return false;
                }

                offset++;
            }
        }

        if (offset != value.Length - 1)
        {
            return false;
        }

        arguments = parsed;
        return true;
    }

    private static bool TryParseAssemblyQualifiedType(
        string value,
        out string? typeName,
        out string? assemblyName)
    {
        typeName = null;
        assemblyName = null;
        int separator = value.IndexOf(", ", StringComparison.Ordinal);
        if (separator <= 0
            || !IsExactTypeName(value[..separator])
            || !TryParseAssemblySpecification(
                value[(separator + 2)..],
                out assemblyName))
        {
            return false;
        }

        typeName = value[..separator];
        return true;
    }

    private static bool TryParseAssemblySpecification(
        string specification,
        out string? assemblyName)
    {
        assemblyName = null;
        string[] parts = specification.Split(", ", StringSplitOptions.None);
        if (parts.Length == 0 || !IsExactAssemblyName(parts[0]))
        {
            return false;
        }

        var attributes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < parts.Length; index++)
        {
            int equals = parts[index].IndexOf('=');
            if (equals <= 0
                || equals == parts[index].Length - 1
                || parts[index].IndexOf('=', equals + 1) >= 0)
            {
                return false;
            }

            string name = parts[index][..equals];
            string value = parts[index][(equals + 1)..];
            if (!attributes.Add(name)
                || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                return false;
            }

            bool valid = name switch
            {
                "Version" => Version.TryParse(value, out _),
                "Culture" => IsCanonicalCulture(value),
                "PublicKeyToken" => string.Equals(value, "null", StringComparison.Ordinal)
                    || (value.Length == 16 && value.All(Uri.IsHexDigit)),
                _ => false,
            };
            if (!valid)
            {
                return false;
            }
        }

        try
        {
            var parsed = new AssemblyName(specification);
            if (!string.Equals(parsed.Name, parts[0], StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FileLoadException)
        {
            return false;
        }

        assemblyName = parts[0];
        return true;
    }

    private static bool IsExactTypeName(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var componentStart = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] is not ('.' or '+'))
            {
                continue;
            }

            if (!IsExactTypeComponent(value[componentStart..index]))
            {
                return false;
            }

            componentStart = index + 1;
        }

        return true;
    }

    private static bool IsExactTypeComponent(string value)
    {
        int arity = value.IndexOf('`');
        ReadOnlySpan<char> name = arity >= 0
            ? value.AsSpan(0, arity)
            : value.AsSpan();
        if (name.Length == 0
            || !(IsAsciiLetter(name[0]) || name[0] == '_')
            || !name[1..].ToArray().All(character =>
                IsAsciiLetterOrDigit(character) || character == '_'))
        {
            return false;
        }

        if (arity < 0)
        {
            return true;
        }

        ReadOnlySpan<char> digits = value.AsSpan(arity + 1);
        return digits.Length > 0
            && digits[0] is >= '1' and <= '9'
            && int.TryParse(digits, out int genericArity)
            && genericArity > 0;
    }

    private static bool IsExactAssemblyName(string value) =>
        value.Length > 0
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && value[0] != '.'
        && value[^1] != '.'
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("  ", StringComparison.Ordinal)
        && value.All(character =>
            IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or ' ');

    private static bool IsCanonicalCulture(string value) =>
        string.Equals(value, "neutral", StringComparison.Ordinal)
        || value.Split('-').All(segment =>
            segment.Length > 0 && segment.All(IsAsciiLetterOrDigit));

    private static bool IsAsciiLetter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsAsciiLetterOrDigit(char character) =>
        IsAsciiLetter(character) || character is >= '0' and <= '9';

    private static bool TryReadExact(Stream stream, byte[] destination) =>
        TryReadExact(stream, destination.AsSpan());

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static bool IsRegularDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0
            && (attributes & FileAttributes.ReparsePoint) == 0;
    }

}

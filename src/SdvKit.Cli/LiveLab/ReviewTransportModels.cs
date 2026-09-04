using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewTransportToken
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool IsRequestId(string? value) =>
        value is not null && Guid.TryParseExact(value, "N", out _);

    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToBase64String(StrictUtf8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string token, int maximumLength, out string value)
    {
        ArgumentNullException.ThrowIfNull(token);
        value = string.Empty;
        if (token.Length == 0
            || token.Any(character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            return false;
        }

        string padded = token.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => "\0",
        };
        if (padded[^1] == '\0')
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(Convert.FromBase64String(padded));
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }

        if (value.Length == 0
            || value.Length > maximumLength
            || value.Any(char.IsControl)
            || !string.Equals(Encode(value), token, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        return true;
    }
}

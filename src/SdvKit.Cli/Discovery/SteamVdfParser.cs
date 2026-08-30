using System.Text;

namespace SdvKit.Cli;

internal static class SteamVdfParser
{
    public static IReadOnlyList<string> ExtractLibraryPaths(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        List<string> tokens = Tokenize(content);
        var paths = new List<string>();
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (string.Equals(tokens[index], "path", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(tokens[index + 1]))
            {
                paths.Add(tokens[index + 1]);
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '"')
            {
                continue;
            }

            var value = new StringBuilder();
            for (index++; index < content.Length; index++)
            {
                char character = content[index];
                if (character == '"')
                {
                    break;
                }

                if (character == '\\' && index + 1 < content.Length)
                {
                    char escaped = content[++index];
                    value.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escaped,
                    });
                    continue;
                }

                value.Append(character);
            }

            tokens.Add(value.ToString());
        }

        return tokens;
    }
}

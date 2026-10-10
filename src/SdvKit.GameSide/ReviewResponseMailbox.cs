namespace SdvKit.GameSide;

/// <summary>
/// Holds review responses in memory until the CLI takes them through the loopback command
/// endpoint. A response key is its logical response path; each key is published at most once
/// and taken at most once. Entries are bounded by count, total size and age.
/// </summary>
internal static class ReviewResponseMailbox
{
    public const int MaximumEntries = 64;
    public const long MaximumTotalBytes = 32L * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static long _totalBytes;

    public static void Publish(string responsePath, ReadOnlySpan<byte> bytes) =>
        Publish(responsePath, bytes, DateTimeOffset.UtcNow);

    internal static void Publish(string responsePath, ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        string key = Key(responsePath);
        if (bytes.Length == 0 || bytes.Length > MaximumTotalBytes)
        {
            throw new InvalidDataException("The review response is empty or exceeds the mailbox limit.");
        }

        lock (Gate)
        {
            Prune(now);
            if (Entries.ContainsKey(key))
            {
                throw new InvalidDataException("The review response target already exists.");
            }

            while (Entries.Count >= MaximumEntries || _totalBytes + bytes.Length > MaximumTotalBytes)
            {
                Remove(Entries.MinBy(pair => pair.Value.PublishedAtUtc).Key);
            }

            Entries.Add(key, new Entry(bytes.ToArray(), now));
            _totalBytes += bytes.Length;
        }
    }

    /// <summary>Takes the exact response, so it can never be read twice.</summary>
    public static bool TryTake(string responsePath, out byte[] bytes)
    {
        string key = Key(responsePath);
        lock (Gate)
        {
            Prune(DateTimeOffset.UtcNow);
            return TryRemove(key, out bytes);
        }
    }

    /// <summary>Takes the response with this file name below the process's runtime root.</summary>
    public static bool TryTakeByName(string runtimeRoot, string name, out byte[] bytes)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot)) + Path.DirectorySeparatorChar;
        lock (Gate)
        {
            Prune(DateTimeOffset.UtcNow);
            string? key = Entries.Keys.FirstOrDefault(candidate =>
                candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(candidate), name, StringComparison.Ordinal));
            if (key is null)
            {
                bytes = [];
                return false;
            }

            return TryRemove(key, out bytes);
        }
    }

    public static bool Contains(string responsePath)
    {
        string key = Key(responsePath);
        lock (Gate)
        {
            return Entries.ContainsKey(key);
        }
    }

    private static string Key(string responsePath)
    {
        if (string.IsNullOrWhiteSpace(responsePath))
        {
            throw new ArgumentException("A non-empty path is required.", nameof(responsePath));
        }
        return Path.GetFullPath(responsePath);
    }

    private static void Prune(DateTimeOffset now)
    {
        foreach (string key in Entries.Where(pair => now - pair.Value.PublishedAtUtc > Lifetime).Select(pair => pair.Key).ToArray())
        {
            Remove(key);
        }
    }

    private static bool TryRemove(string key, out byte[] bytes)
    {
        if (!Entries.TryGetValue(key, out Entry? entry))
        {
            bytes = [];
            return false;
        }

        Remove(key);
        bytes = entry.Bytes;
        return true;
    }

    private static void Remove(string key)
    {
        if (Entries.Remove(key, out Entry? entry))
        {
            _totalBytes -= entry.Bytes.Length;
        }
    }

    private sealed record Entry(byte[] Bytes, DateTimeOffset PublishedAtUtc);
}

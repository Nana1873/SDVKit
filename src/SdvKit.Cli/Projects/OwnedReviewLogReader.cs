using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using SdvKit.Cli.LiveLab;
using SdvKit.Cli.Mcp;

namespace SdvKit.Cli;

// Internal source only: callers supply a verified review, never a log path.
internal static class OwnedReviewLogReader
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const int HeaderBytes = 256 * 1024;
    internal static readonly Regex Header = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)? (?:(?<level>TRACE|DEBUG|ERROR|ALERT) |(?<level>INFO|WARN)  )(?<logger>[^\]\r\n]{1,256})\] (?<message>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static OwnedReviewLog Read(ProjectReviewMcpRuntimeReader reader,
        ProjectReviewMcpVerifiedContext context)
    {
        LiveLabPaths paths = LiveLabPaths.Resolve(reader.ProjectRoot);
        if (context.Role is not null)
        {
            paths = LiveLabPaths.ResolveNetworkRole(paths, context.Role);
        }

        string directory = Path.Combine(paths.StardewDataPath, "ErrorLogs");
        string path = Path.Combine(directory, "SMAPI-latest.txt");
        if (Directory.Exists(directory))
        {
            LiveLabPaths.RejectReparsePointsBelow(directory);
        }

        // Open the link itself, reject links (including hard links), then read from
        // that same handle. No delete sharing: rotation during the read fails closed.
        using SafeFileHandle handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero,
            3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("reviewLogUnavailable");
        }
        if (!GetFileInformationByHandle(handle, out FileInformation info)
            || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
            || info.Links != 1)
        {
            throw new InvalidDataException("reviewLogPathInvalid");
        }

        var finalPath = new char[32768];
        uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Length, 0);
        if (length == 0 || length >= finalPath.Length
            || !string.Equals(new string(finalPath, 0, (int)length), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("reviewLogPathInvalid");
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        long totalBytes = stream.Length;
        DateTimeOffset written = DateTimeOffset.FromFileTime(((long)info.WriteHigh << 32) | info.WriteLow);
        if (written < context.State.OwnedProcessIdentity.StartTimeUtc)
        {
            throw new InvalidDataException("reviewLogStale");
        }
        byte[] prefix = new byte[(int)Math.Min(totalBytes, HeaderBytes)];
        stream.ReadExactly(prefix);
        string activation = $"SDVKit AlwaysOn activated for isolated lab launch '{context.State.LaunchId}'.";
        bool bound = Encoding.UTF8.GetString(prefix).Split('\n').Any(line =>
        {
            Match match = Header.Match(line.TrimEnd('\r'));
            return match.Success && match.Groups["logger"].Value == "SDVKit AlwaysOn"
                && match.Groups["level"].Value == "INFO" && match.Groups["message"].Value == activation;
        });
        if (!bound)
        {
            throw new InvalidDataException("reviewLogIdentityMismatch");
        }

        long start = Math.Max(0, totalBytes - MaximumBytes);
        stream.Position = start;
        byte[] bytes = new byte[(int)(totalBytes - start)];
        stream.ReadExactly(bytes);
        string text = Encoding.UTF8.GetString(bytes);
        if (start > 0)
        {
            int newline = text.IndexOf('\n');
            text = newline < 0 ? "" : text[(newline + 1)..];
        }
        // Do not publish an entry whose final physical line was still being written.
        int complete = text.LastIndexOf('\n');
        bool incomplete = complete != text.Length - 1;
        text = complete < 0 ? "" : text[..(complete + 1)];
        if (stream.Length < totalBytes)
        {
            throw new InvalidDataException("reviewLogChanged");
        }
        ProjectReviewMcpContextResult after = reader.ReadContext();
        if (!after.Succeeded || after.Context!.State != context.State
            || !SameStagedContent(context.Staging, after.Context.Staging))
        {
            throw new InvalidDataException("reviewLogBindingChanged");
        }
        return new OwnedReviewLog(text, totalBytes, bytes.Length, start > 0, incomplete, written,
            $"{info.Volume:x8}:{info.IndexHigh:x8}:{info.IndexLow:x8}");
    }

    internal static bool SameStagedContent(ProjectReviewStaging before, ProjectReviewStaging after) =>
        before.Artifacts.Select(a => (a.Manifest.UniqueId, a.StagedBuildIdentity, a.CpRefresh?.RefreshId))
            .SequenceEqual(after.Artifacts.Select(a => (a.Manifest.UniqueId, a.StagedBuildIdentity, a.CpRefresh?.RefreshId)));

    internal static void RequireSingleLink(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out FileInformation info) || info.Links != 1)
            throw new InvalidDataException("Refresh files must have exactly one filesystem link.");
    }

    internal static FileStream OpenSingleLinkSnapshot(string path)
    {
        SafeFileHandle handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero,
            3, 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
                throw new IOException("The selected file is unavailable for a read-only snapshot.");
            if (!GetFileInformationByHandle(handle, out FileInformation info)
                || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || info.Links != 1)
                throw new InvalidDataException("linkedPath: Select a plain single-link file.");
            var finalPath = new char[32768];
            uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Length, 0);
            if (length == 0 || length >= finalPath.Length
                || !string.Equals(new string(finalPath, 0, (int)length), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("linkedPath: The selected handle resolved outside its exact path.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh,
            WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);
}

internal sealed record OwnedReviewLog(string Text, long TotalBytes, int ScannedBytes,
    bool ScanTruncated, bool IncompleteLineWithheld, DateTimeOffset LastWrittenAtUtc, string FileIdentity);

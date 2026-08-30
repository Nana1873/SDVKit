using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class StopRequestFile
{
    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string launchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The stop-request path must be fully qualified.",
                nameof(path));
        }

        if (!Guid.TryParseExact(launchId, "N", out _))
        {
            throw new ArgumentException(
                "The stop-request launch ID is invalid.",
                nameof(launchId));
        }

        string absolutePath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(absolutePath)
            ?? throw new IOException("The stop-request path has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The live-lab runtime directory was not found: {directory}");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                byte[] content = Utf8WithoutBom.GetBytes(launchId + Environment.NewLine);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, absolutePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

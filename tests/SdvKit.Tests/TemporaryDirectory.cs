namespace SdvKit.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"SDVKit tests {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string WriteFile(string relativePath, string content = "")
    {
        string path = System.IO.Path.Combine(Path, relativePath);
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
        return path;
    }

    public void CreateReadyInstallation()
    {
        WriteFile("Stardew Valley.exe");
        WriteFile("Stardew Valley.dll");
        WriteFile("StardewModdingAPI.exe");
        WriteFile("StardewModdingAPI.dll");
    }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}

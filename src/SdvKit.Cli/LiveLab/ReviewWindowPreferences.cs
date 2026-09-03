using System.Xml;
using System.Xml.Linq;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewWindowPreferences
{
    internal const int Width = 1280;
    internal const int Height = 720;

    public static void Prepare(string stardewDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stardewDataPath);

        string preferencesPath = Path.Combine(stardewDataPath, "startup_preferences");
        if (!File.Exists(preferencesPath))
        {
            if (Directory.Exists(preferencesPath))
            {
                throw new InvalidDataException(
                    $"The isolated review startup preferences path is not a regular file: {preferencesPath}");
            }

            return;
        }

        FileAttributes attributes = File.GetAttributes(preferencesPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                $"The isolated review startup preferences path is not a regular file: {preferencesPath}");
        }

        XDocument document;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        try
        {
            using XmlReader reader = XmlReader.Create(preferencesPath, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "The isolated review startup preferences file is invalid XML.",
                exception);
        }

        XElement root = document.Root
            ?? throw new InvalidDataException(
                "The isolated review startup preferences file has no root element.");
        if (!string.Equals(root.Name.LocalName, "StartupPreferences", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The isolated review startup preferences file has an unexpected root element.");
        }

        XElement clientOptions = RequiredChild(root, "clientOptions");
        RequiredChild(root, "windowMode").Value = "1";
        RequiredChild(clientOptions, "fullscreen").Value = "false";
        RequiredChild(clientOptions, "windowedBorderlessFullscreen").Value = "false";
        RequiredChild(clientOptions, "preferredResolutionX").Value =
            Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RequiredChild(clientOptions, "preferredResolutionY").Value =
            Height.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string temporaryPath = preferencesPath + ".sdvkit-" + Guid.NewGuid().ToString("N");
        try
        {
            var writerSettings = new XmlWriterSettings
            {
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
            };
            using (XmlWriter writer = XmlWriter.Create(temporaryPath, writerSettings))
            {
                document.Save(writer);
            }

            File.Move(temporaryPath, preferencesPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static XElement RequiredChild(XElement parent, string localName)
    {
        XElement[] matches = parent
            .Elements()
            .Where(element => string.Equals(
                element.Name.LocalName,
                localName,
                StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"The isolated review startup preferences file must contain exactly one '{localName}' element below '{parent.Name.LocalName}'.");
    }
}

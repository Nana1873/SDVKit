using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Cli;

internal sealed record SaveInspection(string Sha256, long Bytes, string Copy, string GameVersion,
    string Schema, SavePlayer Player, IReadOnlyDictionary<string, int?> World,
    string? Season, bool FarmAvailable, bool BuildingsAvailable, bool ObjectsAvailable, int BuildingCount, IReadOnlyList<SaveBuilding> Buildings,
    int ObjectCount, IReadOnlyList<SaveObject> Objects, IReadOnlyList<string> Limitations)
{
    public bool BuildingsTruncated => BuildingCount > Buildings.Count;
    public bool ObjectsTruncated => ObjectCount > Objects.Count;
}
internal sealed record SavePlayer(int? Money, int? Health, int? MaxHealth, float? Stamina,
    int? MaxStamina, int? FarmingLevel, int? MiningLevel, int? CombatLevel, int? ForagingLevel, int? FishingLevel);
internal sealed record SaveBuilding(int X, int Y, string Type);
internal sealed record SaveObject(int X, int Y, string ItemId, int Stack);

internal static class SaveInspector
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    internal const int MaximumRecords = 10000;
    internal static readonly string[] PlayerFields = ["money", "health", "maxHealth", "stamina", "maxStamina",
        "farmingLevel", "miningLevel", "combatLevel", "foragingLevel", "fishingLevel"];
    internal static readonly string[] WorldFields = ["year", "dayOfMonth", "whichFarm"];

    internal static SaveInspection Inspect(string projectRoot, string source, Action<string>? afterCopy = null, TestSaveIdentity? fixture = null)
    {
        RequirePlainAncestors(projectRoot);
        RequirePlainAncestors(source);
        source = Path.GetFullPath(source);
        RequirePlainAncestors(source);
        string root = Path.Combine(Path.GetFullPath(projectRoot), ".sdvkit", "save-inspection");
        RequirePlainAncestors(root);
        Directory.CreateDirectory(root);
        string copyId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, copyId);
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "save.xml");
        string hash;
        long size;
        // Deny source writes and replacement throughout the copy and readback.
        using (var input = OwnedReviewLogReader.OpenSingleLinkSnapshot(source))
        {
            RequirePlainAncestors(source);
            size = input.Length;
            if (size is <= 0 or > MaximumBytes)
                throw new InvalidDataException("sizeLimit: Select a nonempty save of at most 32 MiB.");
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            input.Position = 0;
            hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            using var readback = File.OpenRead(copy);
            if (readback.Length != size || !string.Equals(hash,
                Convert.ToHexString(SHA256.HashData(readback)).ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException("copyMismatch: The isolated copy failed byte verification.");
        }
        afterCopy?.Invoke(copy);
        RequirePlainAncestors(copy);
        using var copied = OwnedReviewLogReader.OpenSingleLinkSnapshot(copy);
        if (copied.Length != size || !string.Equals(Convert.ToHexString(SHA256.HashData(copied)), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("copyChanged: The isolated copy changed before inspection.");
        copied.Position = 0;
        XDocument document = ReadXml(copied);
        XElement save = document.Root ?? throw new InvalidDataException("schemaUnavailable: SaveGame root is missing.");
        if (save.Name != "SaveGame")
            throw new InvalidDataException("schemaUnavailable: Expected a Stardew SaveGame XML payload, not SaveGameInfo.");
        string version = Text(save, "gameVersion") ?? throw new InvalidDataException("versionUnavailable: gameVersion is missing.");
        if (!Version.TryParse(version, out Version? parsed) || parsed.Major != 1 || parsed.Minor != 6)
            throw new InvalidDataException("unsupportedVersion: Only Stardew 1.6 save XML is supported.");
        XElement? player = One(save, "player");
        if (fixture is not null)
        {
            string? Marker(string key) => (One(player, "modData")?.Elements("item") ?? [])
                .Where(item => Text(One(item, "key"), "string") == key)
                .Select(item => Text(One(item, "value"), "string")).SingleOrDefault();
            if (Text(save, "uniqueIDForThisGame") != fixture.UniqueGameId.ToString(CultureInfo.InvariantCulture)
                || Marker(TestSaveContract.WorkspaceOwnerMarkerKey) != fixture.WorkspaceOwnerId
                || Marker(TestSaveContract.FixtureMarkerKey) != fixture.FixtureId)
                throw new InvalidDataException("fixtureMismatch: Copied save does not match the registered fixture.");
        }
        var players = new SavePlayer(Integer(player, "money"), Integer(player, "health"),
            Integer(player, "maxHealth"), Single(player, "stamina"), Integer(player, "maxStamina"),
            Integer(player, "farmingLevel"), Integer(player, "miningLevel"), Integer(player, "combatLevel"),
            Integer(player, "foragingLevel"), Integer(player, "fishingLevel"));
        var world = new SortedDictionary<string, int?>(StringComparer.Ordinal);
        foreach (string field in WorldFields)
            world[field] = Integer(save, field);
        string? season = Text(save, "currentSeason");
        if (season is not (null or "spring" or "summer" or "fall" or "winter"))
            throw new InvalidDataException("schemaUnavailable: currentSeason is not a supported season.");
        XElement[] farms = (One(save, "locations")?.Elements("GameLocation") ?? [])
            .Where(location => Text(location, "name") == "Farm").Take(2).ToArray();
        if (farms.Length > 1)
            throw new InvalidDataException("ambiguousRecord: More than one Farm location exists.");
        XElement? farm = farms.SingleOrDefault();
        XElement[] buildingElements = Bounded(One(farm, "buildings")?.Elements("Building"));
        SaveBuilding[] buildings = buildingElements.Select(building => new SaveBuilding(
            RequiredInt(building, "tileX"), RequiredInt(building, "tileY"),
            RequiredText(building, "buildingType"))).OrderBy(b => b.X).ThenBy(b => b.Y).ToArray();
        XElement[] objectElements = Bounded(One(farm, "objects")?.Elements("item"));
        SaveObject[] objects = objectElements.Select(item =>
        {
            XElement? tile = One(One(item, "key"), "Vector2");
            XElement? value = One(One(item, "value"), "Object");
            return new SaveObject(RequiredTile(tile, "X"), RequiredTile(tile, "Y"),
                RequiredText(value, "itemId"), RequiredInt(value, "stack"));
        }).OrderBy(o => o.X).ThenBy(o => o.Y).ToArray();
        if (buildings.Select(b => (b.X, b.Y)).Distinct().Count() != buildings.Length
            || objects.Select(o => (o.X, o.Y)).Distinct().Count() != objects.Length)
            throw new InvalidDataException("ambiguousRecord: Duplicate Farm tile identities.");
        return new SaveInspection(hash, size, $".sdvkit/save-inspection/{copyId}/save.xml", version,
            "stardew-1.6-known-fields", players, world, season, farm is not null, One(farm, "buildings") is not null,
            One(farm, "objects") is not null, buildings.Length, buildings.Take(100).ToArray(),
            objects.Length, objects.Take(100).ToArray(),
            ["Offline saved values only; no current runtime or gameplay assertion.",
             "Missing scalar fields are null; only the main player and exact Farm location are inspected.",
             "Farm collections return the first 100 tiles in numeric X/Y order; counts report the totals.",
             "Modded types and unknown sections are not traversed; no player names or source paths are returned."]);
    }

    private static XDocument ReadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using (XmlReader validation = XmlReader.Create(stream, settings))
        {
            int nodes = 0;
            while (validation.Read())
                if (validation.Depth > 64 || ++nodes > 1000000)
                    throw new InvalidDataException("xmlLimit: XML exceeds 64 levels or one million nodes.");
        }
        stream.Position = 0;
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    private static XElement[] Bounded(IEnumerable<XElement>? elements)
    {
        XElement[] values = (elements ?? []).Take(MaximumRecords + 1).ToArray();
        if (values.Length > MaximumRecords)
            throw new InvalidDataException("recordLimit: A Farm collection exceeds 10000 records.");
        return values;
    }

    private static XElement? One(XElement? parent, string name)
    {
        XElement[] values = (parent?.Elements(name) ?? []).Take(2).ToArray();
        if (values.Length > 1)
            throw new InvalidDataException("ambiguousField: A known save field occurs more than once.");
        return values.SingleOrDefault();
    }

    private static string? Text(XElement? parent, string name)
    {
        XElement? element = One(parent, name);
        if (element is null) return null;
        if (element.HasElements || element.Value.Length > 128 || element.Value.Any(char.IsControl))
            throw new InvalidDataException("fieldLimit: A known scalar must be plain text of at most 128 characters.");
        return element.Value;
    }

    private static string RequiredText(XElement? parent, string name) =>
        Text(parent, name) is { Length: > 0 } value ? value
            : throw new InvalidDataException("schemaUnavailable: A known Farm record is incomplete.");

    private static float? Single(XElement? parent, string name)
    {
        string? value = Text(parent, name);
        if (value is null) return null;
        try
        {
            float number = XmlConvert.ToSingle(value);
            if (float.IsFinite(number)) return number;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException("invalidNumber: A known Single field must contain a finite XML number.", exception);
        }
        throw new InvalidDataException("invalidNumber: A known Single field must contain a finite XML number.");
    }

    private static int? Integer(XElement? parent, string name)
    {
        string? value = Text(parent, name);
        if (value is null) return null;
        try
        {
            return XmlConvert.ToInt32(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidDataException("invalidInteger: A known Int32 field is malformed or out of range.", exception);
        }
    }

    private static int RequiredTile(XElement? parent, string name)
    {
        float? value = Single(parent, name);
        if (value is null || (double)value < int.MinValue || (double)value > int.MaxValue
            || MathF.Truncate(value.Value) != value.Value)
            throw new InvalidDataException("invalidInteger: A Farm tile coordinate must be an integral Int32 value.");
        return (int)value.Value;
    }

    private static int RequiredInt(XElement? parent, string name) => Integer(parent, name)
        ?? throw new InvalidDataException("schemaUnavailable: A known Farm record is incomplete.");

    internal static void RequirePlainAncestors(string path)
    {
        string full = Path.GetFullPath(path);
        // Never silently normalize a traversal selector or accept device/UNC paths.
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "..")
            || full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("unsafePath: Use a local plain path without traversal or streams.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("linkedPath: Save inspection rejects linked files and ancestors.");
        }
    }
}

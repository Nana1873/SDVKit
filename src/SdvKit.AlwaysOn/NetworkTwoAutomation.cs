#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using System.Reflection;
using System.Text;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;

namespace SdvKit.AlwaysOn;

internal sealed class NetworkTwoAutomation
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);

    private readonly NetworkTwoLaunchState _launch;
    private readonly string _loadedBuildIdentity;
    private readonly IMonitor _monitor;
    private readonly Action _publishStatus;
    private readonly Func<TestSaveStatusMarker?> _testSaveStatus;
    private readonly FieldInfo? _multiplayer;
    private readonly MethodInfo? _startServer;
    private readonly MethodInfo? _initClient;
    private readonly FieldInfo? _availableFarmhands;
    private readonly ConstructorInfo? _farmhandSlotConstructor;
    private readonly MethodInfo? _activateFarmhandSlot;
    private readonly FieldInfo? _startingCabinLocations;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    private string _phase;
    private bool _identityVerified;
    private int _joinedTicks;
    private long? _localPlayerId;
    private string? _localPlayerName;
    private long? _remotePlayerId;
    private string? _remotePlayerName;
    private string? _message;
    private Client? _client;
    private WindowsForegroundWindowObservation _foregroundWindow;

    private NetworkTwoAutomation(
        NetworkTwoLaunchState launch,
        string loadedBuildIdentity,
        IMonitor monitor,
        Action publishStatus,
        Func<TestSaveStatusMarker?> testSaveStatus,
        FieldInfo? multiplayer,
        MethodInfo? startServer,
        MethodInfo? initClient,
        FieldInfo? availableFarmhands,
        ConstructorInfo? farmhandSlotConstructor,
        MethodInfo? activateFarmhandSlot,
        FieldInfo? startingCabinLocations)
    {
        _launch = launch;
        _loadedBuildIdentity = loadedBuildIdentity;
        _monitor = monitor;
        _publishStatus = publishStatus;
        _testSaveStatus = testSaveStatus;
        _multiplayer = multiplayer;
        _startServer = startServer;
        _initClient = initClient;
        _availableFarmhands = availableFarmhands;
        _farmhandSlotConstructor = farmhandSlotConstructor;
        _activateFarmhandSlot = activateFarmhandSlot;
        _startingCabinLocations = startingCabinLocations;
        _phase = string.Equals(
            launch.Role,
            NetworkTwoContract.HostRole,
            StringComparison.Ordinal)
            ? "waitingForFixture"
            : "waitingForTitle";
    }

    public bool CanStop => _phase.Length > 0;

    public bool IsHost => string.Equals(
        _launch.Role,
        NetworkTwoContract.HostRole,
        StringComparison.Ordinal);

    public WindowsForegroundWindowObservation ForegroundWindow => _foregroundWindow;

    public NetworkTwoStatusMarker Snapshot => new(
        NetworkTwoContract.SchemaVersion,
        _launch.Role,
        _phase,
        _loadedBuildIdentity,
        _launch.FixtureId,
        _launch.SaveId,
        _identityVerified,
        _joinedTicks,
        _localPlayerId,
        _localPlayerName,
        _remotePlayerId,
        _remotePlayerName,
        _message,
        _launch.NetworkLogPath);

    private bool IsTerminal => _phase is "passed" or "failed";

    public static bool TryCreate(
        string modDirectory,
        IMonitor monitor,
        Action publishStatus,
        Func<TestSaveStatusMarker?> testSaveStatus,
        out NetworkTwoAutomation? automation,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            throw new ArgumentException(
                "The mod directory is required.",
                nameof(modDirectory));
        }

        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(publishStatus);
        ArgumentNullException.ThrowIfNull(testSaveStatus);

        automation = null;
        string role = ReadEnvironment("SDVKIT_NETWORK_TWO_ROLE");
        if (role.Length == 0)
        {
            reason = string.Empty;
            return true;
        }

        NetworkTwoLaunchState? launch = null;
        string loadedBuildIdentity = string.Empty;
        try
        {
            launch = new NetworkTwoLaunchState(
                role,
                ReadEnvironment("SDVKIT_NETWORK_TWO_BUILD_ID"),
                ReadEnvironment("SDVKIT_NETWORK_TWO_FIXTURE_ID"),
                ReadEnvironment("SDVKIT_NETWORK_TWO_SAVE_ID"),
                ReadEnvironment("SDVKIT_NETWORK_TWO_LOG_PATH"),
                ReadExpectedFarmhandId());
            launch.Validate();
            TestSaveAutomation.VerifyRuntimeVersion();

            loadedBuildIdentity = ModBuildIdentity.Compute(modDirectory);
            if (!string.Equals(
                    loadedBuildIdentity,
                    launch.BuildIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The loaded AlwaysOn files do not match the declared network-2 build identity.");
            }

            FieldInfo multiplayer = typeof(Game1).GetField(
                "multiplayer",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException(
                    "Stardew's exact multiplayer field is unavailable.");
            MethodInfo startServer = typeof(Multiplayer).GetMethod(
                "StartServer",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "Stardew's exact host-start method is unavailable.");
            MethodInfo initClient = typeof(Multiplayer).GetMethod(
                "InitClient",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                [typeof(Client)],
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "Stardew's exact client-start method is unavailable.");
            if (multiplayer.FieldType != typeof(Multiplayer)
                || startServer.ReturnType != typeof(void)
                || initClient.ReturnType != typeof(Client))
            {
                throw new InvalidOperationException(
                    "Stardew's exact multiplayer signatures are unavailable.");
            }

            FieldInfo startingCabinLocations = typeof(GameLocation).GetField(
                "_startingCabinLocations",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "Stardew's exact starting-cabin location field is unavailable.");
            if (startingCabinLocations.FieldType != typeof(List<Microsoft.Xna.Framework.Vector2>))
            {
                throw new InvalidOperationException(
                    "Stardew's exact starting-cabin location signature is unavailable.");
            }

            FieldInfo? availableFarmhands = null;
            ConstructorInfo? farmhandSlotConstructor = null;
            MethodInfo? activateFarmhandSlot = null;
            if (string.Equals(role, NetworkTwoContract.FarmhandRole, StringComparison.Ordinal))
            {
                availableFarmhands = typeof(Client).GetField(
                    "availableFarmhands",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Type? farmhandSlot = typeof(FarmhandMenu).GetNestedType(
                    "FarmhandSlot",
                    BindingFlags.NonPublic | BindingFlags.Public);
                farmhandSlotConstructor = farmhandSlot?.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    binder: null,
                    [typeof(FarmhandMenu), typeof(Farmer)],
                    modifiers: null);
                activateFarmhandSlot = farmhandSlot?.GetMethod(
                    "Activate",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    binder: null,
                    Type.EmptyTypes,
                    modifiers: null);
                if (availableFarmhands?.FieldType != typeof(List<Farmer>)
                    || farmhandSlotConstructor is null
                    || activateFarmhandSlot?.ReturnType != typeof(void))
                {
                    throw new InvalidOperationException(
                        "The exact Stardew farmhand selection signatures are unavailable.");
                }
            }

            automation = new NetworkTwoAutomation(
                launch,
                loadedBuildIdentity,
                monitor,
                publishStatus,
                testSaveStatus,
                multiplayer,
                startServer,
                initClient,
                availableFarmhands,
                farmhandSlotConstructor,
                activateFarmhandSlot,
                startingCabinLocations);
            reason = string.Empty;
            automation.Log(
                "configured",
                $"role={role}; build={loadedBuildIdentity}; fixture={launch.FixtureId}");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            reason = exception.Message;
            if (launch is not null
                && NetworkTwoContract.IsRole(launch.Role)
                && Path.IsPathFullyQualified(launch.NetworkLogPath))
            {
                automation = new NetworkTwoAutomation(
                    launch,
                    loadedBuildIdentity,
                    monitor,
                    publishStatus,
                    testSaveStatus,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)
                {
                    _phase = "failed",
                    _message = reason,
                };
            }

            return false;
        }
    }

    public void LogInitializationFailure()
    {
        if (string.Equals(_phase, "failed", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_message))
        {
            Log("failed", _message, LogLevel.Error);
            _publishStatus();
        }
    }

    public void OnUpdateTicked()
    {
        if (IsTerminal)
        {
            return;
        }

        try
        {
            _foregroundWindow = WindowsForegroundWindowProbe.Observe();
            if (DateTimeOffset.UtcNow - _startedAtUtc > OperationTimeout)
            {
                Fail("The bounded network-2 game-side operation exceeded two minutes.");
                return;
            }

            if (IsHost)
            {
                UpdateHost();
            }
            else
            {
                UpdateFarmhand();
            }
        }
        catch (Exception exception)
        {
            Fail(exception.GetBaseException().Message);
        }
    }

    public void OnReturnedToTitle()
    {
        if (!IsTerminal)
        {
            Fail("Stardew returned to title before the exact network-2 workflow completed.");
        }
    }

    private void UpdateHost()
    {
        if (string.Equals(_phase, "waitingForFixture", StringComparison.Ordinal))
        {
            TestSaveStatusMarker? testSave = _testSaveStatus();
            if (testSave is null)
            {
                throw new InvalidOperationException(
                    "The network-2 host is missing its disposable test-save automation.");
            }

            if (string.Equals(testSave.Phase, "failed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    testSave.Message ?? "The network-2 host fixture failed to load.");
            }

            if (!string.Equals(testSave.Phase, "passed", StringComparison.Ordinal)
                || !testSave.IdentityVerified)
            {
                return;
            }

            VerifyHostFixtureBeforeHosting(testSave.Mode);
            SetPhase("startingHost", "Starting Stardew's normal LAN host.");
            Game1.multiplayerMode = 2;
            object multiplayer = _multiplayer!.GetValue(null)
                ?? throw new InvalidOperationException(
                    "Stardew's multiplayer service is unavailable.");
            _startServer!.Invoke(multiplayer, null);
            _identityVerified = true;
            SetPhase(
                "hosting",
                $"Stardew LAN host is waiting for farmhand {_remotePlayerId}.");
            return;
        }

        if (string.Equals(_phase, "hosting", StringComparison.Ordinal))
        {
            if (!TryVerifyJoinedPair())
            {
                return;
            }

            _joinedTicks = 0;
            SetPhase("joined", "Host observed the exact farmhand in the loaded fixture.");
            return;
        }

        ObserveJoinedTicks();
    }

    private void UpdateFarmhand()
    {
        if (string.Equals(_phase, "waitingForTitle", StringComparison.Ordinal))
        {
            if (!IsUnobstructedTitle())
            {
                return;
            }

            object multiplayer = _multiplayer!.GetValue(null)
                ?? throw new InvalidOperationException(
                    "Stardew's multiplayer service is unavailable.");
            _client = _initClient!.Invoke(
                multiplayer,
                [new LidgrenClient("127.0.0.1")]) as Client
                ?? throw new InvalidOperationException(
                    "Stardew didn't initialize the exact loopback client.");
            TitleMenu.subMenu = new FarmhandMenu(_client);
            SetPhase("connecting", "Connecting to Stardew's loopback LAN host.");
            return;
        }

        if (string.Equals(_phase, "connecting", StringComparison.Ordinal))
        {
            List<Farmer>? available = _availableFarmhands!.GetValue(_client) as List<Farmer>;
            if (available is null)
            {
                return;
            }

            if (available.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The exact host offered {available.Count} farmhands instead of one.");
            }

            Farmer farmhand = available[0];
            if (farmhand.UniqueMultiplayerID != _launch.ExpectedFarmhandId)
            {
                throw new InvalidOperationException(
                    "The host offered a different farmhand identity than the one declared by the hosting marker.");
            }

            bool isFreshFarmhand = farmhand.isUnclaimedFarmhand;
            bool isPersistedReviewFarmhand = !isFreshFarmhand
                && MatchesPlayer(
                    farmhand,
                    NetworkTwoContract.FarmhandRole,
                    NetworkTwoContract.FarmhandName);
            if (!isFreshFarmhand && !isPersistedReviewFarmhand)
            {
                throw new InvalidOperationException(
                    "The exact host farmhand is neither disposable nor the saved review farmhand.");
            }

            ConfigureFarmhand(farmhand);
            SetPhase(
                "selectingFarmhand",
                isFreshFarmhand
                    ? "Selecting Stardew's one exact unclaimed farmhand."
                    : "Selecting Stardew's one exact saved review farmhand.");
            FarmhandMenu farmhandMenu = TitleMenu.subMenu as FarmhandMenu
                ?? throw new InvalidOperationException(
                    "Stardew closed the exact farmhand selection menu before activation.");
            object slot = _farmhandSlotConstructor!.Invoke([farmhandMenu, farmhand]);
            _activateFarmhandSlot!.Invoke(slot, null);
            SetPhase("joining", "Stardew accepted the farmhand selection; awaiting world join.");
            return;
        }

        if (string.Equals(_phase, "joining", StringComparison.Ordinal))
        {
            if (!Context.IsWorldReady || !Game1.IsClient || !Context.IsMultiplayer)
            {
                return;
            }

            if (!TryVerifyJoinedPair())
            {
                return;
            }

            _joinedTicks = 0;
            SetPhase("joined", "Farmhand observed the exact host and joined fixture.");
            return;
        }

        ObserveJoinedTicks();
    }

    private void ObserveJoinedTicks()
    {
        if (!string.Equals(_phase, "joined", StringComparison.Ordinal))
        {
            return;
        }

        bool exactPairVerified = TryVerifyJoinedPair();
        _joinedTicks = NetworkTwoContract.NextVerifiedUnfocusedTickCount(
            _joinedTicks,
            exactPairVerified,
            _foregroundWindow.IsVerifiedUnfocused);
        if (!exactPairVerified)
        {
            throw new InvalidOperationException(
                "The exact host/farmhand pair disconnected during the network-2 observation.");
        }

        if (_joinedTicks >= NetworkTwoContract.RequiredJoinedTicks)
        {
            SetPhase(
                "passed",
                $"Exact pair {_localPlayerName}/{_localPlayerId} and "
                + $"{_remotePlayerName}/{_remotePlayerId} remained joined for "
                + $"{_joinedTicks} verified unfocused game ticks while foreground "
                + $"HWND {_foregroundWindow.WindowHandle} belonged to PID "
                + $"{_foregroundWindow.ProcessId}.");
        }
    }

    private void VerifyHostFixtureBeforeHosting(string testSaveMode)
    {
        if (!Context.IsWorldReady
            || !Context.IsMainPlayer
            || Context.IsMultiplayer
            || !string.Equals(Constants.SaveFolderName, _launch.SaveId, StringComparison.Ordinal)
            || !Game1.player.modData.TryGetValue(
                TestSaveContract.FixtureMarkerKey,
                out string fixtureId)
            || !string.Equals(fixtureId, _launch.FixtureId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The network-2 host did not retain the exact disposable fixture identity.");
        }

        Farmer farmhand = PrepareHostFarmhand(testSaveMode);

        if (farmhand.UniqueMultiplayerID == 0)
        {
            throw new InvalidOperationException(
                "The loaded disposable fixture created an invalid farmhand identity.");
        }

        Game1.player.modData[NetworkTwoContract.BuildMarkerKey] = _loadedBuildIdentity;
        Game1.player.modData[NetworkTwoContract.RoleMarkerKey] = NetworkTwoContract.HostRole;
        _localPlayerId = Game1.player.UniqueMultiplayerID;
        _localPlayerName = Game1.player.Name;
        _remotePlayerId = farmhand.UniqueMultiplayerID;
        _identityVerified = true;
    }

    private Farmer PrepareHostFarmhand(string testSaveMode)
    {
        List<Farmer> farmhands = Game1.getAllFarmhands().ToList();
        if (string.Equals(
                testSaveMode,
                TestSaveContract.ReviewMode,
                StringComparison.Ordinal)
            && farmhands.Count == 1)
        {
            Farmer savedFarmhand = farmhands[0];
            if (savedFarmhand.isUnclaimedFarmhand
                || savedFarmhand.UniqueMultiplayerID == 0
                || !MatchesPlayer(
                    savedFarmhand,
                    NetworkTwoContract.FarmhandRole,
                    NetworkTwoContract.FarmhandName))
            {
                throw new InvalidOperationException(
                    "The loaded review save did not retain the exact saved farmhand identity.");
            }

            return savedFarmhand;
        }

        if (farmhands.Count != 0)
        {
            throw new InvalidOperationException(
                "The disposable baseline already contains farmhands instead of the exact empty #5 state.");
        }

        List<Microsoft.Xna.Framework.Vector2> startingCabinLocations =
            _startingCabinLocations!.GetValue(Game1.getFarm())
                as List<Microsoft.Xna.Framework.Vector2>
            ?? throw new InvalidOperationException(
                "Stardew's starting-cabin location state is unavailable.");
        if (startingCabinLocations.Count != 0)
        {
            throw new InvalidOperationException(
                "Stardew retained unexpected starting-cabin locations after loading the exact baseline.");
        }

        xTile.Layers.Layer pathsLayer = Game1.getFarm().Map.GetLayer("Paths")
            ?? throw new InvalidOperationException(
                "The exact disposable farm map has no Paths layer.");
        var candidates = new List<Microsoft.Xna.Framework.Vector2>();
        for (var x = 0; x < pathsLayer.LayerWidth; x++)
        {
            for (var y = 0; y < pathsLayer.LayerHeight; y++)
            {
                xTile.Tiles.Tile? tile = pathsLayer.Tiles[x, y];
                if (tile?.TileIndex == 29
                    && tile.Properties.TryGetValue("Order", out xTile.ObjectModel.PropertyValue? order)
                    && string.Equals(order?.ToString(), "1", StringComparison.Ordinal))
                {
                    candidates.Add(new Microsoft.Xna.Framework.Vector2(x, y));
                }
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"The exact disposable farm map exposed {candidates.Count} first-cabin locations instead of one.");
        }

        startingCabinLocations.Add(candidates[0]);
        Game1.getFarm().BuildStartingCabins();

        farmhands = Game1.getAllFarmhands().ToList();
        if (farmhands.Count != 1 || !farmhands[0].isUnclaimedFarmhand)
        {
            throw new InvalidOperationException(
                "The loaded disposable fixture did not create exactly one unclaimed farmhand.");
        }

        return farmhands[0];
    }

    private void ConfigureFarmhand(Farmer farmhand)
    {
        farmhand.Name = NetworkTwoContract.FarmhandName;
        farmhand.displayName = NetworkTwoContract.FarmhandName;
        farmhand.farmName.Value = TestSaveContract.FarmName;
        farmhand.favoriteThing.Value = TestSaveContract.FavoriteThing;
        farmhand.modData[TestSaveContract.FixtureMarkerKey] = _launch.FixtureId;
        farmhand.modData[NetworkTwoContract.BuildMarkerKey] = _loadedBuildIdentity;
        farmhand.modData[NetworkTwoContract.RoleMarkerKey] = NetworkTwoContract.FarmhandRole;
        farmhand.isCustomized.Value = true;
        _localPlayerId = farmhand.UniqueMultiplayerID;
        _localPlayerName = farmhand.Name;
    }

    private bool TryVerifyJoinedPair()
    {
        if (!Context.IsWorldReady || !Context.IsMultiplayer)
        {
            return false;
        }

        if ((IsHost && (!Context.IsMainPlayer || !Game1.IsServer))
            || (!IsHost && (Context.IsMainPlayer || !Game1.IsClient)))
        {
            return false;
        }

        List<Farmer> online = Game1.getOnlineFarmers().ToList();
        if (online.Count != 2)
        {
            return false;
        }

        Farmer local = Game1.player;
        Farmer? remote = online.SingleOrDefault(
            candidate => candidate.UniqueMultiplayerID != local.UniqueMultiplayerID);
        long? expectedFarmhandId = IsHost
            ? _remotePlayerId
            : _launch.ExpectedFarmhandId;
        Farmer? observedFarmhand = IsHost ? remote : local;
        string expectedLocalRole = IsHost
            ? NetworkTwoContract.HostRole
            : NetworkTwoContract.FarmhandRole;
        string expectedRemoteRole = IsHost
            ? NetworkTwoContract.FarmhandRole
            : NetworkTwoContract.HostRole;
        string expectedLocalName = IsHost
            ? TestSaveContract.PlayerName
            : NetworkTwoContract.FarmhandName;
        string expectedRemoteName = IsHost
            ? NetworkTwoContract.FarmhandName
            : TestSaveContract.PlayerName;
        if (remote is null
            || expectedFarmhandId is null
            || observedFarmhand is null
            || observedFarmhand.UniqueMultiplayerID != expectedFarmhandId
            || !MatchesPlayer(local, expectedLocalRole, expectedLocalName)
            || !MatchesPlayer(remote, expectedRemoteRole, expectedRemoteName))
        {
            return false;
        }

        _localPlayerId = local.UniqueMultiplayerID;
        _localPlayerName = local.Name;
        _remotePlayerId = remote.UniqueMultiplayerID;
        _remotePlayerName = remote.Name;
        _identityVerified = true;
        return true;
    }

    private bool MatchesPlayer(Farmer player, string role, string name) =>
        string.Equals(player.Name, name, StringComparison.Ordinal)
        && player.modData.TryGetValue(
            TestSaveContract.FixtureMarkerKey,
            out string fixtureId)
        && string.Equals(fixtureId, _launch.FixtureId, StringComparison.Ordinal)
        && player.modData.TryGetValue(
            NetworkTwoContract.BuildMarkerKey,
            out string buildIdentity)
        && string.Equals(buildIdentity, _loadedBuildIdentity, StringComparison.Ordinal)
        && player.modData.TryGetValue(
            NetworkTwoContract.RoleMarkerKey,
            out string observedRole)
        && string.Equals(observedRole, role, StringComparison.Ordinal);

    private void SetPhase(string phase, string message)
    {
        _phase = phase;
        _message = message;
        Log(phase, message);
        _publishStatus();
    }

    private void Fail(string message)
    {
        if (string.Equals(_phase, "failed", StringComparison.Ordinal))
        {
            return;
        }

        _phase = "failed";
        _identityVerified = false;
        _message = message;
        Log("failed", message, LogLevel.Error);
        _publishStatus();
    }

    private void Log(string phase, string message, LogLevel level = LogLevel.Info)
    {
        string text = $"SDVKit network-2 [{_launch.Role}/{phase}] {message}";
        _monitor.Log(text, level);
        try
        {
            string? directory = Path.GetDirectoryName(_launch.NetworkLogPath);
            if (directory is null || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The project-local network-2 log directory is unavailable.");
            }

            var line = new StringBuilder();
            line.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            line.Append('\t');
            line.Append(phase);
            line.Append('\t');
            line.AppendLine(message.ReplaceLineEndings(" "));
            File.AppendAllText(
                _launch.NetworkLogPath,
                line.ToString(),
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            _monitor.Log(
                $"SDVKit network-2 couldn't write its project-local log: {exception.Message}",
                LogLevel.Error);
        }
    }

    private static string ReadEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    private static long? ReadExpectedFarmhandId()
    {
        string value = ReadEnvironment("SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID");
        if (value.Length == 0)
        {
            return null;
        }

        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long farmhandId)
            || farmhandId == 0)
        {
            throw new InvalidDataException(
                "SDVKIT_NETWORK_TWO_EXPECTED_FARMHAND_ID is invalid.");
        }

        return farmhandId;
    }

    private static bool IsUnobstructedTitle() =>
        !Context.IsWorldReady
        && Game1.activeClickableMenu is TitleMenu
        && TitleMenu.subMenu is null;
}
#endif

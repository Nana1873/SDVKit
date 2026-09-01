#if SDVKIT_GAME_AVAILABLE
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SdvKit.AlwaysOn;

internal sealed class TestSaveAutomation
{
    private const string SupportedGameVersion = "1.6.15";
    private const string SupportedGameFileVersion = "1.6.15.24356";
    private const string SupportedSmapiVersion = "4.5.2";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);

    private readonly TestSaveIdentity _identity;
    private readonly string _mode;
    private readonly string _scenarioLogPath;
    private readonly bool _allowMultiplayer;
    private readonly IMonitor _monitor;
    private readonly Action _publishStatus;
    private readonly FieldInfo? _nameBox;
    private readonly FieldInfo? _farmNameBox;
    private readonly FieldInfo? _favoriteThingBox;
    private readonly FieldInfo? _skipIntro;
    private readonly MethodInfo? _optionButtonClick;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    private string _phase = "waitingForTitle";
    private bool _identityVerified;
    private int _waitedTicks;
    private int _scenarioStartTick;
    private bool _createMarkersApplied;
    private bool _saveCreated;
    private IEnumerator<int>? _saveIterator;
    private bool _saveReachedCompletion;
    private string? _message;

    private TestSaveAutomation(
        TestSaveIdentity identity,
        string mode,
        string scenarioLogPath,
        bool allowMultiplayer,
        IMonitor monitor,
        Action publishStatus,
        FieldInfo? nameBox,
        FieldInfo? farmNameBox,
        FieldInfo? favoriteThingBox,
        FieldInfo? skipIntro,
        MethodInfo? optionButtonClick)
    {
        _identity = identity;
        _mode = mode;
        _scenarioLogPath = scenarioLogPath;
        _allowMultiplayer = allowMultiplayer;
        _monitor = monitor;
        _publishStatus = publishStatus;
        _nameBox = nameBox;
        _farmNameBox = farmNameBox;
        _favoriteThingBox = favoriteThingBox;
        _skipIntro = skipIntro;
        _optionButtonClick = optionButtonClick;
    }

    public bool CanStop =>
        IsTerminal
        && _saveIterator is null
        && !SaveGame.IsProcessing
        && !Game1.game1.IsSaving;

    public TestSaveStatusMarker Snapshot => new(
        TestSaveContract.SchemaVersion,
        _mode,
        _phase,
        _identity.FixtureId,
        _identity.SaveId,
        _identityVerified,
        _waitedTicks,
        _message,
        _scenarioLogPath);

    private bool IsTerminal => _phase is "created" or "passed" or "failed";

    private bool IsPassedReview =>
        string.Equals(_mode, TestSaveContract.ReviewMode, StringComparison.Ordinal)
        && string.Equals(_phase, "passed", StringComparison.Ordinal);

    public static bool TryCreate(
        IMonitor monitor,
        Action publishStatus,
        bool networkHost,
        out TestSaveAutomation? automation,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(publishStatus);

        automation = null;
        string mode = ReadEnvironment("SDVKIT_TEST_SAVE_MODE");
        if (mode.Length == 0)
        {
            reason = string.Empty;
            return true;
        }

        TestSaveIdentity? identity = null;
        string scenarioLogPath = string.Empty;
        var identityValidated = false;
        try
        {
            identity = ReadIdentity();
            identity.Validate();
            identityValidated = true;
            if (mode is not (TestSaveContract.CreateMode
                or TestSaveContract.ScenarioMode
                or TestSaveContract.ReviewMode))
            {
                throw new InvalidDataException("SDVKIT_TEST_SAVE_MODE is invalid.");
            }

            if (networkHost
                && mode is not (TestSaveContract.ScenarioMode or TestSaveContract.ReviewMode))
            {
                throw new InvalidDataException(
                    "The network-2 host requires the existing disposable scenario or review fixture.");
            }

            scenarioLogPath = ReadEnvironment("SDVKIT_TEST_SAVE_LOG_PATH");
            if (!Path.IsPathFullyQualified(scenarioLogPath))
            {
                throw new InvalidDataException("SDVKIT_TEST_SAVE_LOG_PATH must be absolute.");
            }

            VerifyRuntimeVersion();
            Type customization = typeof(CharacterCustomization);
            ConstructorInfo? constructor = customization.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                [typeof(CharacterCustomization.Source), typeof(bool)],
                modifiers: null);
            FieldInfo? nameBox = ExactField(customization, "nameBox", typeof(TextBox));
            FieldInfo? farmNameBox = ExactField(customization, "farmnameBox", typeof(TextBox));
            FieldInfo? favoriteThingBox = ExactField(customization, "favThingBox", typeof(TextBox));
            FieldInfo? skipIntro = ExactField(customization, "skipIntro", typeof(bool));
            MethodInfo? optionButtonClick = customization.GetMethod(
                "optionButtonClick",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string)],
                modifiers: null);
            MethodInfo? directLoad = typeof(SaveGame).GetMethod(
                nameof(SaveGame.Load),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                [typeof(string)],
                modifiers: null);
            MethodInfo? exitActiveMenu = typeof(Game1).GetMethod(
                nameof(Game1.exitActiveMenu),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            MethodInfo? directSave = typeof(SaveGame).GetMethod(
                nameof(SaveGame.Save),
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            FieldInfo? startingGameSeed = typeof(Game1).GetField(
                nameof(Game1.startingGameSeed),
                BindingFlags.Static | BindingFlags.Public);
            if (constructor is null
                || nameBox is null
                || farmNameBox is null
                || favoriteThingBox is null
                || skipIntro is null
                || optionButtonClick?.ReturnType != typeof(void)
                || directLoad?.ReturnType != typeof(void)
                || exitActiveMenu?.ReturnType != typeof(void)
                || directSave?.ReturnType != typeof(IEnumerator<int>)
                || startingGameSeed?.FieldType != typeof(ulong?))
            {
                throw new InvalidOperationException(
                    "The exact Stardew test-save creation, load, and save signatures are unavailable.");
            }

            automation = new TestSaveAutomation(
                identity,
                mode,
                Path.GetFullPath(scenarioLogPath),
                networkHost,
                monitor,
                publishStatus,
                nameBox,
                farmNameBox,
                favoriteThingBox,
                skipIntro,
                optionButtonClick);
            reason = string.Empty;
            automation.Log("configured", $"mode={mode}; saveId={identity.SaveId}");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException)
        {
            reason = exception.Message;
            if (identityValidated
                && identity is not null
                && mode is (TestSaveContract.CreateMode
                    or TestSaveContract.ScenarioMode
                    or TestSaveContract.ReviewMode)
                && Path.IsPathFullyQualified(scenarioLogPath))
            {
                automation = new TestSaveAutomation(
                    identity,
                    mode,
                    Path.GetFullPath(scenarioLogPath),
                    networkHost,
                    monitor,
                    publishStatus,
                    null,
                    null,
                    null,
                    null,
                    null)
                {
                    _phase = "failed",
                    _identityVerified = false,
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
        }
    }

    public bool TryVerifyReviewFixture(
        out string fixtureId,
        out string reason)
    {
        fixtureId = _identity.FixtureId;
        if (!IsPassedReview)
        {
            reason = "Fixture commands require the exact test save in passed review mode.";
            return false;
        }

        try
        {
            VerifyExactWorld(allowMultiplayer: _allowMultiplayer);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.GetBaseException().Message;
            return false;
        }
    }

    public void OnUpdateTicked()
    {
        try
        {
            if (_saveIterator is not null)
            {
                if (!string.Equals(_phase, "failed", StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - _startedAtUtc > OperationTimeout)
                {
                    Fail("The bounded test-save operation exceeded two minutes.");
                }

                DriveDurableSave();
                return;
            }

            if (IsPassedReview)
            {
                VerifyExactWorld(allowMultiplayer: _allowMultiplayer);
                return;
            }

            if (IsTerminal)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - _startedAtUtc > OperationTimeout)
            {
                Fail("The bounded test-save operation exceeded two minutes.");
                return;
            }

            if (string.Equals(_phase, "waitingForTitle", StringComparison.Ordinal))
            {
                if (!IsUnobstructedTitle())
                {
                    return;
                }

                if (string.Equals(_mode, TestSaveContract.CreateMode, StringComparison.Ordinal))
                {
                    StartCreate();
                }
                else
                {
                    StartLoad();
                }

                return;
            }

            if (string.Equals(_phase, "waiting", StringComparison.Ordinal))
            {
                VerifyExactWorld();
                int observed = Game1.ticks - _scenarioStartTick;
                _waitedTicks = Math.Max(0, observed);
                if (_waitedTicks >= TestSaveContract.RequiredScenarioTicks)
                {
                    CompleteScenario();
                }
            }

            if (string.Equals(_mode, TestSaveContract.CreateMode, StringComparison.Ordinal)
                && string.Equals(_phase, "creating", StringComparison.Ordinal)
                && _saveCreated
                && Context.IsWorldReady)
            {
                if (SaveGame.IsProcessing || Game1.game1.IsSaving)
                {
                    return;
                }

                StartDurableSave();
            }
        }
        catch (Exception exception)
        {
            HandleUpdateFailure(exception);
        }
    }

    public void OnSaveCreating()
    {
        if (!string.Equals(_mode, TestSaveContract.CreateMode, StringComparison.Ordinal)
            || !string.Equals(_phase, "creating", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (_createMarkersApplied || _saveCreated)
            {
                throw new InvalidOperationException(
                    "Stardew reported SaveCreating more than once for the disposable fixture.");
            }

            VerifyCreatePrerequisites();
            Farmer player = Game1.player;
            player.Name = _identity.PlayerName;
            player.displayName = _identity.PlayerName;
            player.farmName.Value = _identity.FarmName;
            player.favoriteThing.Value = _identity.FavoriteThing;
            player.modData[TestSaveContract.WorkspaceOwnerMarkerKey] =
                _identity.WorkspaceOwnerId;
            player.modData[TestSaveContract.FixtureMarkerKey] = _identity.FixtureId;
            VerifyCreatePrerequisites(requireMarkers: true);
            _createMarkersApplied = true;
            _identityVerified = true;
            Log("saveCreating", "Exact fixture identity verified before Stardew's initial serialization.");
            _publishStatus();
        }
        catch (Exception exception)
        {
            Fail(exception.GetBaseException().Message);
            throw;
        }
    }

    public void OnSaveCreated()
    {
        if (!string.Equals(_mode, TestSaveContract.CreateMode, StringComparison.Ordinal)
            || !string.Equals(_phase, "creating", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (_saveCreated)
            {
                throw new InvalidOperationException(
                    "Stardew reported SaveCreated more than once for the disposable fixture.");
            }

            if (!_createMarkersApplied)
            {
                throw new InvalidOperationException(
                    "Stardew completed creation without the verified pre-serialization fixture markers.");
            }

            Game1.SetSaveName(_identity.PlayerName);
            if (!string.Equals(
                    Game1.GetSaveGameName(false),
                    _identity.PlayerName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Stardew didn't retain the exact fixture save name.");
            }

            VerifyExactWorld(requireWorldReady: false);
            _saveCreated = true;
            _identityVerified = true;
            Log(
                "saveCreated",
                "Stardew serialized the exact fixture; waiting for its world-ready transition.");
            _publishStatus();
        }
        catch (Exception exception)
        {
            Fail(exception.GetBaseException().Message);
            throw;
        }
        finally
        {
            Game1.startingGameSeed = null;
        }
    }

    public void OnSaveLoaded()
    {
        if (_mode is not (TestSaveContract.ScenarioMode or TestSaveContract.ReviewMode)
            || !string.Equals(_phase, "loading", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            VerifyExactWorld();
            if (string.Equals(_mode, TestSaveContract.ReviewMode, StringComparison.Ordinal))
            {
                SetPhase(
                    "passed",
                    "Exact fixture loaded for interactive review without scenario mutation.");
                return;
            }

            if (Game1.player.modData.ContainsKey(TestSaveContract.ScenarioMarkerKey))
            {
                throw new InvalidOperationException(
                    "The restored fixture contains the forbidden scenario marker instead of the known baseline.");
            }

            Game1.addHUDMessage(new HUDMessage("SDVKit test-save smoke"));
            _scenarioStartTick = Game1.ticks;
            _waitedTicks = 0;
            SetPhase(
                "waiting",
                $"Exact fixture loaded; observing {TestSaveContract.RequiredScenarioTicks} game ticks.");
        }
        catch (Exception exception)
        {
            Fail(exception.GetBaseException().Message);
            throw;
        }
    }

    public void OnSaving()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        try
        {
            VerifyExactWorld(
                allowMultiplayer: string.Equals(
                    _mode,
                    TestSaveContract.ReviewMode,
                    StringComparison.Ordinal));
            Log("saving", "Exact fixture identity verified before Stardew save serialization.");
        }
        catch (Exception exception)
        {
            Fail(exception.GetBaseException().Message);
            throw;
        }
    }

    public void OnReturnedToTitle()
    {
        Game1.startingGameSeed = null;
        if (IsPassedReview)
        {
            Fail("Stardew returned to title after the exact fixture was loaded for review.");
            return;
        }

        if (!IsTerminal)
        {
            Fail("Stardew returned to title before the test-save workflow completed.");
        }
    }

    private void StartCreate()
    {
        Game1.startingGameSeed = checked((ulong)_identity.UniqueGameId);
        var menu = new CharacterCustomization(CharacterCustomization.Source.NewGame, false);
        SetText(_nameBox!, menu, _identity.PlayerName);
        SetText(_farmNameBox!, menu, _identity.FarmName);
        SetText(_favoriteThingBox!, menu, _identity.FavoriteThing);
        _skipIntro!.SetValue(menu, true);

        Game1.player.Name = _identity.PlayerName;
        Game1.player.displayName = _identity.PlayerName;
        Game1.player.farmName.Value = _identity.FarmName;
        Game1.player.favoriteThing.Value = _identity.FavoriteThing;
        TitleMenu.subMenu = menu;
        if (!menu.canLeaveMenu())
        {
            throw new InvalidOperationException(
                "Stardew rejected the exact fixture character identity.");
        }

        SetPhase("creating", "Starting Stardew's normal new-game flow.");
        try
        {
            _optionButtonClick!.Invoke(menu, ["OK"]);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Stardew rejected fixture creation: {exception.GetBaseException().Message}",
                exception.GetBaseException());
        }

        if (Game1.uniqueIDForThisGame != (ulong)_identity.UniqueGameId)
        {
            throw new InvalidOperationException(
                "Stardew didn't retain the exact pre-authorized fixture game ID.");
        }
    }

    private void StartLoad()
    {
        Game1.exitActiveMenu();
        SetPhase("loading", $"Loading only exact fixture '{_identity.SaveId}'.");
        SaveGame.Load(_identity.SaveId);
    }

    private void StartDurableSave()
    {
        VerifyExactWorld();
        if (SaveGame.IsProcessing || Game1.game1.IsSaving)
        {
            throw new InvalidOperationException(
                "Stardew is already processing save data for the exact fixture.");
        }

        Game1.game1.IsSaving = true;
        try
        {
            _saveIterator = SaveGame.Save()
                ?? throw new InvalidOperationException(
                    "The exact Stardew save iterator is unavailable.");
            if (!MoveSaveIteratorWithIdentityVerification()
                || _saveIterator.Current != 1)
            {
                throw new InvalidOperationException(
                    "Stardew's save iterator ended before starting fixture serialization.");
            }

            bool running = MoveSaveIteratorWithIdentityVerification();
            if (!running)
            {
                throw new InvalidOperationException(
                    "Stardew canceled fixture serialization before reporting completion.");
            }

            RecordSaveIteratorProgress();
        }
        catch
        {
            // The pinned SMAPI runtime executes Stardew's save task synchronously.
            // A thrown MoveNext therefore leaves no serializer running, even if
            // Stardew didn't unwind its public processing flag itself.
            SaveGame.IsProcessing = false;
            DisposeSaveIterator();
            throw;
        }

        Log(
            "savingBaseline",
            "Exact fixture identity verified; driving Stardew's supported save iterator.");
        _publishStatus();
    }

    private void DriveDurableSave()
    {
        if (!string.Equals(_phase, "failed", StringComparison.Ordinal))
        {
            VerifyExactWorld();
        }

        if (_saveIterator!.MoveNext())
        {
            RecordSaveIteratorProgress();
            return;
        }

        if (!_saveReachedCompletion)
        {
            throw new InvalidOperationException(
                "Stardew's save iterator ended without its completion signal.");
        }

        if (SaveGame.IsProcessing)
        {
            throw new InvalidOperationException(
                "Stardew's save iterator ended while still reporting active processing.");
        }

        CompleteDurableSave();
    }

    private bool MoveSaveIteratorWithIdentityVerification()
    {
        VerifyExactWorld();
        return _saveIterator!.MoveNext();
    }

    private void RecordSaveIteratorProgress()
    {
        int progress = _saveIterator!.Current;
        if (progress >= 100)
        {
            _saveReachedCompletion = true;
            return;
        }

        if (progress != 1)
        {
            throw new InvalidOperationException(
                $"Stardew's save iterator reported unexpected progress '{progress}'.");
        }
    }

    private void CompleteDurableSave()
    {
        DisposeSaveIterator();
        if (string.Equals(_phase, "failed", StringComparison.Ordinal))
        {
            return;
        }

        VerifyExactWorld();
        SetPhase(
            "created",
            "Stardew durably saved and retained the exact disposable fixture.");
    }

    private void DisposeSaveIterator()
    {
        IEnumerator<int>? saveIterator = _saveIterator;
        _saveIterator = null;
        _saveReachedCompletion = false;
        try
        {
            saveIterator?.Dispose();
        }
        finally
        {
            if (saveIterator is not null)
            {
                Game1.game1.IsSaving = false;
            }
        }
    }

    private void HandleUpdateFailure(Exception exception)
    {
        Fail(exception.GetBaseException().Message);
        if (_saveIterator is null)
        {
            return;
        }

        try
        {
            // With the version-locked SMAPI StartTask implementation, MoveNext
            // returns only after the serializer task ended or yielded safely.
            SaveGame.IsProcessing = false;
            DisposeSaveIterator();
        }
        catch (Exception cleanupException)
        {
            string message =
                $"{exception.GetBaseException().Message} "
                + $"The Stardew save iterator also failed to close: {cleanupException.GetBaseException().Message}";
            _message = message;
            Log("failed", message, LogLevel.Error);
            _publishStatus();
        }
    }

    private void CompleteScenario()
    {
        VerifyExactWorld();
        if (Game1.player.modData.ContainsKey(TestSaveContract.ScenarioMarkerKey))
        {
            throw new InvalidOperationException(
                "The fixture drifted from its known baseline during the scenario wait.");
        }

        _identityVerified = true;
        SetPhase(
            "passed",
            $"Exact fixture remained world-ready for {_waitedTicks} observed game ticks.");
    }

    private void VerifyCreatePrerequisites(bool requireMarkers = false)
    {
        var mismatches = new List<string>();
        Farmer? player = Game1.player;
        if (!Context.IsMainPlayer)
        {
            mismatches.Add("mainPlayer");
        }

        if (Context.IsMultiplayer)
        {
            mismatches.Add("singlePlayer");
        }

        if (player is null)
        {
            mismatches.Add("player");
        }

        if (Game1.uniqueIDForThisGame != (ulong)_identity.UniqueGameId)
        {
            mismatches.Add("uniqueGameId");
        }

        if (player is not null)
        {
            AddMismatch(player.Name, _identity.PlayerName, "playerName", mismatches);
            AddMismatch(player.farmName.Value, _identity.FarmName, "farmName", mismatches);
            AddMismatch(
                player.favoriteThing.Value,
                _identity.FavoriteThing,
                "favoriteThing",
                mismatches);
            if (requireMarkers)
            {
                AddMarkerMismatch(
                    player,
                    TestSaveContract.WorkspaceOwnerMarkerKey,
                    _identity.WorkspaceOwnerId,
                    "workspaceOwnerId",
                    mismatches);
                AddMarkerMismatch(
                    player,
                    TestSaveContract.FixtureMarkerKey,
                    _identity.FixtureId,
                    "fixtureId",
                    mismatches);
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Stardew's creating world differs in these fixture fields: {string.Join(", ", mismatches)}.");
        }
    }

    private void VerifyExactWorld(
        bool requireWorldReady = true,
        bool allowMultiplayer = false)
    {
        var mismatches = new List<string>();
        Farmer? player = Game1.player;
        if (requireWorldReady && !Context.IsWorldReady)
        {
            mismatches.Add("worldReady");
        }

        if (!Context.IsMainPlayer)
        {
            mismatches.Add("mainPlayer");
        }

        if (!allowMultiplayer && Context.IsMultiplayer)
        {
            mismatches.Add("singlePlayer");
        }

        if (player is null)
        {
            mismatches.Add("player");
        }

        if (!string.Equals(Constants.SaveFolderName, _identity.SaveId, StringComparison.Ordinal))
        {
            mismatches.Add("saveId");
        }

        if (Game1.uniqueIDForThisGame != (ulong)_identity.UniqueGameId)
        {
            mismatches.Add("uniqueGameId");
        }

        if (player is not null)
        {
            AddMismatch(player.Name, _identity.PlayerName, "playerName", mismatches);
            AddMismatch(player.farmName.Value, _identity.FarmName, "farmName", mismatches);
            AddMismatch(
                player.favoriteThing.Value,
                _identity.FavoriteThing,
                "favoriteThing",
                mismatches);
            AddMarkerMismatch(
                player,
                TestSaveContract.WorkspaceOwnerMarkerKey,
                _identity.WorkspaceOwnerId,
                "workspaceOwnerId",
                mismatches);
            AddMarkerMismatch(
                player,
                TestSaveContract.FixtureMarkerKey,
                _identity.FixtureId,
                "fixtureId",
                mismatches);
        }

        if (mismatches.Count > 0)
        {
            _identityVerified = false;
            throw new InvalidOperationException(
                $"The live world differs in these exact fixture fields: {string.Join(", ", mismatches)}.");
        }

        _identityVerified = true;
    }

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
        string text = $"SDVKit test-save [{phase}] {message}";
        _monitor.Log(text, level);
        try
        {
            string? directory = Path.GetDirectoryName(_scenarioLogPath);
            if (directory is null || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The project-local test-save log directory is unavailable.");
            }

            var line = new StringBuilder();
            line.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            line.Append('\t');
            line.Append(phase);
            line.Append('\t');
            line.AppendLine(message.ReplaceLineEndings(" "));
            File.AppendAllText(_scenarioLogPath, line.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            _monitor.Log(
                $"SDVKit test-save couldn't write its project-local scenario log: {exception.Message}",
                LogLevel.Error);
        }
    }

    private static TestSaveIdentity ReadIdentity()
    {
        if (!long.TryParse(
                ReadEnvironment("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long uniqueGameId))
        {
            throw new InvalidDataException("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID is invalid.");
        }

        return new TestSaveIdentity(
            TestSaveContract.SchemaVersion,
            ReadEnvironment("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID"),
            ReadEnvironment("SDVKIT_TEST_SAVE_FIXTURE_ID"),
            uniqueGameId,
            ReadEnvironment("SDVKIT_TEST_SAVE_ID"),
            ReadEnvironment("SDVKIT_TEST_SAVE_PLAYER_NAME"),
            ReadEnvironment("SDVKIT_TEST_SAVE_FARM_NAME"),
            ReadEnvironment("SDVKIT_TEST_SAVE_FAVORITE_THING"));
    }

    internal static void VerifyRuntimeVersion()
    {
        string gameVersion = Game1.version.ToString();
        string gameFileVersion =
            FileVersionInfo.GetVersionInfo(typeof(Game1).Assembly.Location).FileVersion
            ?? string.Empty;
        string smapiVersion = Constants.ApiVersion.ToString();
        if (!string.Equals(gameVersion, SupportedGameVersion, StringComparison.Ordinal)
            || !string.Equals(gameFileVersion, SupportedGameFileVersion, StringComparison.Ordinal)
            || !string.Equals(smapiVersion, SupportedSmapiVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Test-save automation requires Stardew {SupportedGameFileVersion} and SMAPI {SupportedSmapiVersion}; "
                + $"the runtime reported Stardew {gameFileVersion} and SMAPI {smapiVersion}.");
        }
    }

    private static string ReadEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    private static bool IsUnobstructedTitle() =>
        !Context.IsWorldReady
        && Game1.activeClickableMenu is TitleMenu
        && TitleMenu.subMenu is null;

    private static FieldInfo? ExactField(Type type, string name, Type fieldType)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.FieldType == fieldType ? field : null;
    }

    private static void SetText(
        FieldInfo field,
        CharacterCustomization menu,
        string value)
    {
        TextBox box = field.GetValue(menu) as TextBox
            ?? throw new InvalidOperationException(
                "The exact Stardew character customization text field is unavailable.");
        box.Text = value;
    }

    private static void AddMismatch(
        string? observed,
        string expected,
        string name,
        List<string> mismatches)
    {
        if (!string.Equals(observed, expected, StringComparison.Ordinal))
        {
            mismatches.Add(name);
        }
    }

    private static void AddMarkerMismatch(
        Farmer player,
        string key,
        string expected,
        string name,
        List<string> mismatches)
    {
        if (!player.modData.TryGetValue(key, out string observed)
            || !string.Equals(observed, expected, StringComparison.Ordinal))
        {
            mismatches.Add(name);
        }
    }
}
#endif

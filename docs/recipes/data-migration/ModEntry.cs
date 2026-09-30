using System;
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace DataMigrationRecipe;

internal sealed class ModEntry : Mod
{
    private const string Key = "ledger";

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.ConsoleCommands.Add("migration-recipe", "Owned recipe: status | fixture missing | fixture future", (_, args) => Run(args));
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!OwnedFixtureReady())
        {
            Monitor.Log("Migration recipe rejected: exact owned disposable single fixture is not ready.", LogLevel.Error);
            return;
        }
        try
        {
            SchemaHeader? header = Helper.Data.ReadSaveData<SchemaHeader>(Key);
#if RECIPE_V1
            if (header is null)
            {
                Item reward = ItemRegistry.Create("(O)390", 5);
                if (!Game1.player.couldInventoryAcceptThisItem(reward)
                    || !Game1.player.addItemToInventoryBool(reward))
                    throw new InvalidOperationException("The complete welcome reward does not fit; no ledger was written.");
                Helper.Data.WriteSaveData(Key, new LegacyLedger
                {
                    SchemaVersion = 1,
                    Credits = 37,
                    FavoriteCrop = "(O)634",
                    WelcomeRewardClaimed = true
                });
                Monitor.Log("Created v1 ledger and granted five Stone once.", LogLevel.Info);
            }
            else if (header.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported schema {header.SchemaVersion}; original data was retained.");
#else
            if (header is null)
            {
                Helper.Data.WriteSaveData(Key, LedgerMigration.CreateNew());
                Monitor.Log("Created new v2 ledger; no welcome reward was granted.", LogLevel.Info);
            }
            else if (header.SchemaVersion == 1)
            {
#if RECIPE_BROKEN
                // Deliberate missing migration: reject instead of overwriting user data.
                throw new InvalidOperationException("Broken v2 cannot migrate schema 1; original data was retained.");
#else
                LegacyLedger previous = Helper.Data.ReadSaveData<LegacyLedger>(Key)
                    ?? throw new InvalidOperationException("The selected v1 ledger disappeared.");
                Ledger upgraded = LedgerMigration.Upgrade(previous);
                Helper.Data.WriteSaveData(Key, upgraded);
                Monitor.Log("Migrated v1 ledger to v2; selected values and welcome reward marker were preserved.", LogLevel.Info);
#endif
            }
            else if (header.SchemaVersion == 2)
                Monitor.Log("Loaded existing v2 ledger; no migration or welcome reward was repeated.", LogLevel.Info);
            else
                throw new InvalidOperationException($"Unsupported schema {header.SchemaVersion}; original data was retained.");
#endif
        }
        catch (Exception exception)
        {
            Monitor.Log($"Migration refused: {exception.Message}", LogLevel.Error);
        }
        Status();
    }

    private void Run(string[] args)
    {
        if (!OwnedFixtureReady())
        {
            Monitor.Log("Migration recipe command rejected: exact owned disposable single fixture is not ready.", LogLevel.Error);
            return;
        }
        if (args.Length == 1 && args[0] == "status") Status();
        else if (args.Length == 2 && args[0] == "fixture" && args[1] == "missing")
        {
            Helper.Data.WriteSaveData<SchemaHeader>(Key, null);
            Monitor.Log("Recipe fixture removed only this mod's ledger; save and restart to test new data.", LogLevel.Info);
            Status();
        }
        else if (args.Length == 2 && args[0] == "fixture" && args[1] == "future")
        {
            Helper.Data.WriteSaveData(Key, new FutureLedger());
            Monitor.Log("Recipe fixture wrote schema 99; save and restart to test future-schema refusal.", LogLevel.Info);
            Status();
        }
        else Monitor.Log("Usage: migration-recipe status | fixture missing | fixture future", LogLevel.Error);
    }

    private void Status()
    {
        SchemaHeader? header = Helper.Data.ReadSaveData<SchemaHeader>(Key);
        string values;
        if (header?.SchemaVersion == 1)
        {
            LegacyLedger data = Helper.Data.ReadSaveData<LegacyLedger>(Key)!;
            values = $"credits={data.Credits}; crop={data.FavoriteCrop}; claimed={data.WelcomeRewardClaimed}";
        }
        else if (header?.SchemaVersion == 2)
        {
            Ledger data = Helper.Data.ReadSaveData<Ledger>(Key)!;
            values = $"balance={data.Balance}; crop={data.Preference.CropId}; claimed={data.WelcomeRewardClaimed}; migrations={data.MigrationCount}";
        }
        else if (header?.SchemaVersion == 99)
        {
            FutureLedger data = Helper.Data.ReadSaveData<FutureLedger>(Key)!;
            values = $"balance={data.Balance}; futureOnly={data.FutureOnly}";
        }
        else values = "no supported ledger";
        int stone = Game1.player.Items.Where(item => item?.QualifiedItemId == "(O)390").Sum(item => item!.Stack);
        Monitor.Log($"Migration recipe artifact={ModManifest.Version}; schema={header?.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "missing"}; {values}; stone={stone}.", LogLevel.Info);
    }

    private static bool OwnedFixtureReady()
    {
        string? owner = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID");
        string? fixture = Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FIXTURE_ID");
        return Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"))
            && Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_MODE") == "review"
            && Guid.TryParseExact(owner, "N", out _) && Guid.TryParseExact(fixture, "N", out _)
            && long.TryParse(Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID"),
                NumberStyles.None, CultureInfo.InvariantCulture, out long expectedGameId)
            && expectedGameId > 0 && Game1.uniqueIDForThisGame == (ulong)expectedGameId
            && Constants.SaveFolderName == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_ID")
            && Context.ScreenId == 0 && Context.IsMainPlayer && Context.IsWorldReady
            && !Context.IsMultiplayer && !Game1.exitToTitle
            && Game1.player.Name == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_PLAYER_NAME")
            && Game1.player.farmName.Value == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FARM_NAME")
            && Game1.player.favoriteThing.Value == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FAVORITE_THING")
            && Game1.player.modData.TryGetValue("SDVKit/WorkspaceOwnerId", out string? observedOwner) && observedOwner == owner
            && Game1.player.modData.TryGetValue("SDVKit/FixtureId", out string? observedFixture) && observedFixture == fixture;
    }
}

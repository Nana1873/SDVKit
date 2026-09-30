using System;

namespace DataMigrationRecipe;

public sealed class SchemaHeader
{
    public int? SchemaVersion { get; set; }
}

public sealed class LegacyLedger
{
    public int SchemaVersion { get; set; }
    public int Credits { get; set; }
    public string? FavoriteCrop { get; set; }
    public bool WelcomeRewardClaimed { get; set; }
}

public sealed class CropPreference
{
    public string CropId { get; set; } = "(O)24";
}

public sealed class Ledger
{
    public int SchemaVersion { get; set; }
    public int Balance { get; set; }
    public CropPreference Preference { get; set; } = new();
    public bool WelcomeRewardClaimed { get; set; }
    public int MigrationCount { get; set; }
}

public sealed class FutureLedger
{
    public int SchemaVersion { get; set; } = 99;
    public int Balance { get; set; } = 73;
    public string FutureOnly { get; set; } = "preserve-this-value";
}

// One recipe's mapping, not a general migration framework.
public static class LedgerMigration
{
    public static Ledger CreateNew() => new() { SchemaVersion = 2 };

    public static Ledger Upgrade(LegacyLedger previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.SchemaVersion != 1 || previous.Credits < 0
            || string.IsNullOrWhiteSpace(previous.FavoriteCrop))
            throw new InvalidOperationException("Invalid v1 ledger; original data was retained.");

        return new Ledger
        {
            SchemaVersion = 2,
            Balance = previous.Credits,
            Preference = new CropPreference { CropId = previous.FavoriteCrop },
            WelcomeRewardClaimed = previous.WelcomeRewardClaimed,
            MigrationCount = 1
        };
    }
}

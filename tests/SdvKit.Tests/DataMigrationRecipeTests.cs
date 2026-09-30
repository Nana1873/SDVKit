using DataMigrationRecipe;
using Xunit;

namespace SdvKit.Tests;

public sealed class DataMigrationRecipeTests
{
    [Theory]
    [InlineData(37, "(O)634", true)]
    [InlineData(0, "(O)24", false)]
    [InlineData(int.MaxValue, "(O)190", true)]
    public void UpgradePreservesSelectedUserValuesWithoutChangingLegacyData(int credits, string crop, bool claimed)
    {
        var previous = new LegacyLedger { SchemaVersion = 1, Credits = credits, FavoriteCrop = crop, WelcomeRewardClaimed = claimed };
        Ledger migrated = LedgerMigration.Upgrade(previous);
        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(credits, migrated.Balance);
        Assert.Equal(crop, migrated.Preference.CropId);
        Assert.Equal(claimed, migrated.WelcomeRewardClaimed);
        Assert.Equal(1, migrated.MigrationCount);
        Assert.Equal(1, previous.SchemaVersion);
        Assert.Equal(credits, previous.Credits);
        Assert.Equal(crop, previous.FavoriteCrop);
        Assert.Equal(claimed, previous.WelcomeRewardClaimed);
    }

    [Theory]
    [InlineData(99, 37, "(O)634")]
    [InlineData(2, 37, "(O)634")]
    [InlineData(1, -1, "(O)634")]
    [InlineData(1, 37, null)]
    [InlineData(1, 37, " ")]
    public void InvalidLegacyDataIsRejectedBeforeReplacement(int schema, int credits, string? crop)
    {
        var previous = new LegacyLedger { SchemaVersion = schema, Credits = credits, FavoriteCrop = crop };
        Assert.Throws<InvalidOperationException>(() => LedgerMigration.Upgrade(previous));
        Assert.Equal(schema, previous.SchemaVersion);
        Assert.Equal(credits, previous.Credits);
        Assert.Equal(crop, previous.FavoriteCrop);
    }

    [Fact]
    public void MissingDataHasExplicitNewDefaultsAndIndependentPreferences()
    {
        Ledger first = LedgerMigration.CreateNew();
        Ledger second = LedgerMigration.CreateNew();
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(0, first.Balance);
        Assert.Equal("(O)24", first.Preference.CropId);
        Assert.False(first.WelcomeRewardClaimed);
        Assert.Equal(0, first.MigrationCount);
        first.Preference.CropId = "(O)634";
        Assert.Equal("(O)24", second.Preference.CropId);
    }
}

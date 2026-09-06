using System.Text.Json;
using System.Text.Json.Nodes;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class LocalPlayerSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly OwnedProcessIdentity Process = new(1234, Now.AddMinutes(-1), @"C:\Game\StardewModdingAPI.exe");

    internal static LocalPlayerSnapshot Player(string id = "101", int money = 12345) =>
        new(1, "available", null, new LocalPlayerValues(id, money, 75, 150, 220.5f, 300, 0,
            new SelectedItemValues("(O)388", 17, 2)));

    private static RuntimeSnapshotMarker Runtime(LocalPlayerSnapshot? player) =>
        new(1, true, "spring", 1, 1, 600, "Farm", 10, 20, false, Now, player);

    [Fact]
    public void ModifiedValuesAreNotClampedToVanillaLimits()
    {
        LocalPlayerSnapshot marker = Player(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture)) with
        {
            Data = Player().Data! with
            {
                PlayerId = "-9223372036854775808",
                Money = int.MaxValue,
                Health = -100,
                MaxHealth = int.MaxValue,
                Stamina = -16.5f,
                MaxStamina = 5000,
                SelectedSlot = 500,
                SelectedItem = new SelectedItemValues("(O)Example.Mod_Custom", 10000, 10),
            },
        };
        Assert.True(LocalPlayerSnapshotContract.TryRead(marker, true, out LocalPlayerSnapshot report));
        Assert.Equal(marker, report);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("")]
    public void InvalidPlayerIdentityIsRejected(string id) =>
        Assert.False(LocalPlayerSnapshotContract.TryRead(Player(id), true, out _));

    [Fact]
    public void InvalidBoundsAndNonFiniteValuesAreRejected()
    {
        LocalPlayerValues good = Player().Data!;
        LocalPlayerValues[] invalid =
        [
            good with { Stamina = float.NaN },
            good with { Stamina = float.PositiveInfinity },
            good with { MaxStamina = float.NegativeInfinity },
            good with { SelectedSlot = -1 },
            good with { SelectedSlot = null },
            good with { SelectedItem = new SelectedItemValues("(O)" + new string('a', 254), 1, 0) },
            good with { SelectedItem = new SelectedItemValues("(O)bad\nvalue", 1, 0) },
            good with { SelectedItem = new SelectedItemValues("unqualified", 1, 0) },
            good with { SelectedItem = new SelectedItemValues("(O)", 1, 0) },
        ];
        foreach (LocalPlayerValues values in invalid)
        {
            Assert.False(LocalPlayerSnapshotContract.TryRead(Player() with { Data = values }, true, out _));
        }
        Assert.True(LocalPlayerSnapshotContract.ValuesValid(good with
        {
            SelectedItem = new SelectedItemValues("(O)" + new string('a', 253), int.MinValue, null),
        }));
    }

    [Fact]
    public void EmptySelectionAndInapplicableQualityRemainNull()
    {
        LocalPlayerValues good = Player().Data!;
        Assert.True(LocalPlayerSnapshotContract.ValuesValid(good with { SelectedSlot = null, SelectedItem = null }));
        Assert.True(LocalPlayerSnapshotContract.ValuesValid(good with { SelectedItem = null }));
        Assert.True(LocalPlayerSnapshotContract.ValuesValid(good with
        {
            SelectedItem = new SelectedItemValues("(T)Axe", 1, null),
        }));
    }

    [Fact]
    public void OlderAndFutureProducersDoNotInventValues()
    {
        Assert.True(LocalPlayerSnapshotContract.TryRead(null, true, out LocalPlayerSnapshot old));
        Assert.Equal("unavailable", old.Availability);
        Assert.Equal("notPublished", old.Reason);
        Assert.Null(old.Data);
        Assert.True(LocalPlayerSnapshotContract.TryRead(Player() with { SchemaVersion = 2 }, true, out LocalPlayerSnapshot future));
        Assert.Equal("unsupportedVersion", future.Availability);
        Assert.Null(future.Data);
    }

    [Theory]
    [InlineData("worldNotReady", null, false)]
    [InlineData("unavailable", "selectionUnavailable", true)]
    [InlineData("error", "captureFailed", true)]
    [InlineData("error", "invalidValues", true)]
    public void UnavailableStatesWithholdDataAndRejectLeakedValues(string availability, string? reason, bool ready)
    {
        LocalPlayerSnapshot marker = LocalPlayerSnapshotContract.WithoutData(availability, reason);
        Assert.True(LocalPlayerSnapshotContract.TryRead(marker, ready, out LocalPlayerSnapshot report));
        Assert.Equal(marker, report);
        Assert.False(LocalPlayerSnapshotContract.TryRead(marker with { Data = Player().Data }, ready, out _));
        Assert.False(LocalPlayerSnapshotContract.TryRead(marker, !ready, out _));
    }

    [Fact]
    public void PublicStatusTransitionsFromWorldToTitleWithoutOldPlayerValues()
    {
        using TemporaryDirectory temporary = new();
        RuntimeSnapshotMarker marker = Runtime(Player());
        Assert.Equal(12345, Read(temporary, marker).Runtime?.LocalPlayer?.Data?.Money);
        RuntimeSnapshotMarker title = new(1, false, null, null, null, null, null, null, null, true, Now,
            LocalPlayerSnapshotContract.WithoutData("worldNotReady"));
        AlwaysOnStatusReport result = Read(temporary, title);
        Assert.Equal("ready", result.Runtime?.State);
        Assert.Equal("worldNotReady", result.Runtime?.LocalPlayer?.Availability);
        Assert.Null(result.Runtime?.LocalPlayer?.Data);
        Assert.Null(result.Runtime?.LocationId);
        Assert.Equal("invalid", Read(temporary, title with { LocalPlayer = Player() }).Runtime?.State);
        Assert.Equal(54321, Read(temporary, Runtime(Player(money: 54321))).Runtime?.LocalPlayer?.Data?.Money);
    }

    [Fact]
    public void OldRuntimeSchemaRemainsReadableWithExplicitUnavailablePlayer()
    {
        using TemporaryDirectory temporary = new();
        AlwaysOnStatusReport result = Read(temporary, Runtime(null));
        Assert.Equal("ready", result.Runtime?.State);
        Assert.Equal("Farm", result.Runtime?.LocationId);
        Assert.Equal("notPublished", result.Runtime?.LocalPlayer?.Reason);
    }

    [Theory]
    [InlineData("active", 6, "stale")]
    [InlineData("exiting", 0, "exiting")]
    [InlineData("restoreFailed", 0, "restoreFailed")]
    public void NonActiveOuterMarkersWithholdBothOldAndNewRuntimeValues(string phase, int age, string state)
    {
        using TemporaryDirectory temporary = new();
        foreach (LocalPlayerSnapshot? player in new[] { null, Player() })
        {
            AlwaysOnStatusReport report = Read(temporary, Runtime(player), phase, Now.AddSeconds(age));
            Assert.Equal(state, report.Runtime?.State);
            Assert.Null(report.Runtime?.LocationId);
            Assert.Null(report.Runtime?.LocalPlayer);
        }
    }

    [Fact]
    public void FreshOuterMarkerCannotRefreshOldInnerValues()
    {
        using TemporaryDirectory temporary = new();
        Assert.Equal("invalid", Read(temporary, Runtime(Player()) with { ObservedAtUtc = Now.AddSeconds(-6) }).Runtime?.State);
        Assert.Equal("invalid", Read(temporary, Runtime(Player()) with { ObservedAtUtc = Now.AddSeconds(2) }).Runtime?.State);
    }

    [Theory]
    [InlineData("money")]
    [InlineData("selectedSlot")]
    [InlineData("playerId")]
    public void MissingRequiredPlayerFieldsDoNotBecomeDefaultValues(string field)
    {
        using TemporaryDirectory temporary = new();
        Read(temporary, Runtime(Player()));
        string path = Path.Combine(temporary.Path, "status.json");
        JsonNode payload = JsonNode.Parse(File.ReadAllText(path))!;
        payload["runtime"]!["localPlayer"]!["data"]!.AsObject().Remove(field);
        File.WriteAllText(path, payload.ToJsonString());
        AlwaysOnStatusReport result = AlwaysOnStatusReader.Read(path, "launch", Process, Now);
        Assert.Equal("invalid", result.State);
        Assert.Null(result.Runtime);
    }

    private static AlwaysOnStatusReport Read(TemporaryDirectory temporary, RuntimeSnapshotMarker runtime,
        string phase = "active", DateTimeOffset? now = null)
    {
        string path = Path.Combine(temporary.Path, "status.json");
        var marker = new AlwaysOnStatusMarker(1, "launch", Process.ProcessId, Process.StartTimeUtc,
            phase, 600, false, false, Now, Runtime: runtime);
        string json = JsonSerializer.Serialize(marker, LiveLabJsonOptions.CamelCase);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) < 2048);
        File.WriteAllText(path, json);
        return AlwaysOnStatusReader.Read(path, "launch", Process, now ?? Now);
    }
}

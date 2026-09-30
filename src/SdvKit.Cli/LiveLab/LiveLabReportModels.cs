using System.Globalization;
using System.Security;
using System.Text.Json;

namespace SdvKit.Cli.LiveLab;

internal sealed record LiveLabProblem(string Code, string Message);

internal sealed record LiveLabReport(
    int SchemaVersion,
    string Topology,
    string State,
    string? LaunchId,
    int? ProcessId,
    DateTimeOffset? ProcessStartTimeUtc,
    string? ExecutablePath,
    string? ModsPath,
    string? BuildLogPath,
    AlwaysOnStatusReport? AlwaysOn,
    IReadOnlyList<LiveLabProblem> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? TestSaveLogPaths = null);

internal sealed record TestSaveWorkflowReport(
    int SchemaVersion,
    string Topology,
    string State,
    string? FixtureId,
    string? SaveId,
    string Scenario,
    int RequiredTicks,
    int? ObservedTicks,
    string BaselinePath,
    IReadOnlyList<string> LogPaths,
    IReadOnlyList<LiveLabProblem> Problems,
    IReadOnlyList<string> Warnings);

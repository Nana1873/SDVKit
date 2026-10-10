using SdvKit.Cli;

namespace SdvKit.Tests;

public sealed class CliReviewCallTests
{
    [Fact]
    public void CallParsesToolArgumentsAndServeOptions()
    {
        Assert.True(CliApplication.TryParseProjectReviewCall(
            ["project", "review", "call", "stardew_input_press", "{\"button\":\"F8\"}", "--allow-input", "--screen", "1"],
            out ProjectReviewCallRequest? request,
            out _));

        Assert.Equal("stardew_input_press", request!.Tool);
        Assert.Equal("F8", request.Arguments!.Value.GetProperty("button").GetString());
        Assert.Equal("single", request.Topology);
        Assert.Equal(1, request.ScreenId);
        Assert.True(request.AllowInput);
        Assert.False(request.AllowFixtureActions);
    }

    [Fact]
    public void CallWithoutArgumentsSelectsANetworkRole()
    {
        Assert.True(CliApplication.TryParseProjectReviewCall(
            ["project", "review", "call", "stardew_runtime_get", "--topology", "network-2", "--role", "farmhand"],
            out ProjectReviewCallRequest? request,
            out _));

        Assert.Null(request!.Arguments);
        Assert.Equal("network-2", request.Topology);
        Assert.Equal("farmhand", request.Role);
    }

    [Theory]
    [InlineData("stardew_runtime_get", "[1,2]")]
    [InlineData("stardew_runtime_get", "{not json")]
    [InlineData("Stardew-Runtime", "{}")]
    [InlineData("stardew_runtime_get", "--http")]
    [InlineData("stardew_runtime_get", "--role")]
    public void CallRejectsInvalidToolsArgumentsAndTransportOptions(string tool, string next) =>
        Assert.False(CliApplication.TryParseProjectReviewCall(
            ["project", "review", "call", tool, next],
            out _,
            out _));
}

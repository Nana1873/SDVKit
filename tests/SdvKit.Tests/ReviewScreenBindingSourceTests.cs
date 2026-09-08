using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

public sealed class ReviewScreenBindingSourceTests
{
    [Fact]
    public void GameSideDispatchValidatesFrozenContextBeforeAnyCommandFamily()
    {
        string dispatcher = ReadSource("ReviewScreenshotCommand.cs");
        int callback = dispatcher.IndexOf("(_, arguments) =>", StringComparison.Ordinal);
        int validation = dispatcher.IndexOf("ReviewScreenBindingCommand.TryValidateDispatch(", callback,
            StringComparison.Ordinal);
        int firstFamily = dispatcher.IndexOf("if (Context.IsSplitScreen", callback,
            StringComparison.Ordinal);
        Assert.True(callback >= 0);
        Assert.True(validation > callback);
        Assert.True(firstFamily > validation);

        string binding = ReadSource("ReviewScreenBindingCommand.cs");
        Assert.Contains("Contexts.GetValue(Game1.game1", binding, StringComparison.Ordinal);
        Assert.Contains("Game1.player.UniqueMultiplayerID.ToString", binding, StringComparison.Ordinal);
        Assert.Contains("owned.VerifyCurrentLocalScreen();", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementAtDispatchCannotInheritTheFrozenBinding()
    {
        string[] command =
        [
            "input", "request", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "press", "K",
            "binding=11111111111111111111111111111111", "farmer=202",
        ];
        Assert.True(ReviewScreenBindingContract.TryValidateDispatch(command, "202",
            "11111111111111111111111111111111", out string[] stripped));
        Assert.Equal(["input", "request", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "press", "K"], stripped);

        Assert.False(ReviewScreenBindingContract.TryValidateDispatch(command, "303",
            "11111111111111111111111111111111", out string[] wrongFarmer));
        Assert.Same(command, wrongFarmer);
        Assert.False(ReviewScreenBindingContract.TryValidateDispatch(command, "202",
            "22222222222222222222222222222222", out string[] reusedScreen));
        Assert.Same(command, reusedScreen);
    }

    private static string ReadSource(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "src", "SdvKit.AlwaysOn", fileName);
            if (File.Exists(path)) return File.ReadAllText(path).ReplaceLineEndings("\n");
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find the SDVKit repository above '{AppContext.BaseDirectory}'.");
    }
}

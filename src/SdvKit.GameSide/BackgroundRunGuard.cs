namespace SdvKit.GameSide;

internal sealed class SmapiBackgroundRunState : IBackgroundRunState
{
    public bool IsAvailable => StardewValley.Game1.options is not null;

    public object? OptionsIdentity => StardewValley.Game1.options;

    public bool PauseWhenOutOfFocus
    {
        get => StardewValley.Game1.options.pauseWhenOutOfFocus;
        set => StardewValley.Game1.options.pauseWhenOutOfFocus = value;
    }

    public bool EnableServer
    {
        get => StardewValley.Game1.options.enableServer;
        set => StardewValley.Game1.options.enableServer = value;
    }

    public bool IpConnectionsEnabled
    {
        get => StardewValley.Game1.options.ipConnectionsEnabled;
        set => StardewValley.Game1.options.ipConnectionsEnabled = value;
    }
}

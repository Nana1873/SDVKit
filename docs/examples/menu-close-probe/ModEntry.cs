using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace MenuCloseProbe;

// An observer and an ordinary IClickableMenu, never an input injector or runtime patch.
public sealed class ModEntry : Mod
{
    private static readonly SButton[] ButtonsToObserve = [SButton.Escape, SButton.MouseLeft, SButton.ControllerB];
    private string? launch;
    private string label = "unarmed";
    private int remaining;
    private int following;
    private int sequence;
    private string lastState = "";

    public override void Entry(IModHelper helper)
    {
        launch = Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID");
        helper.ConsoleCommands.Add("mc217_open", "mc217_open <label> <root|child|controller-root>: open only from a closed-menu baseline.", Open);
        helper.ConsoleCommands.Add("mc217_observe", "mc217_observe <label>: observe the current state without changing it.", Observe);
        helper.Events.GameLoop.UpdateTicking += (_, _) => Sample("UpdateTicking");
        helper.Events.GameLoop.UpdateTicked += (_, _) =>
        {
            Sample("UpdateTicked");
            if (following > 0) following--;
            if (remaining > 0 && --remaining == 0) Log("END observation window; no input or menu cleanup performed");
        };
        helper.Events.Input.ButtonPressed += (_, e) => Edge("ButtonPressed", e.Button);
        helper.Events.Input.ButtonReleased += (_, e) => Edge("ButtonReleased", e.Button);
        helper.Events.Display.MenuChanged += (_, e) =>
        {
            if (Active()) Log($"MenuChanged old={Describe(e.OldMenu)} new={Describe(e.NewMenu)}");
        };
    }

    private bool Owned() =>
        !string.IsNullOrWhiteSpace(launch) && launch == Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")
        && Context.IsMainPlayer && !Context.IsMultiplayer && Context.ScreenId == 0 && Context.IsWorldReady
        && Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
        && Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_MODE") == "review"
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
        && Guid.TryParseExact(Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID"), "N", out _)
        && Guid.TryParseExact(Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FIXTURE_ID"), "N", out _)
        && ulong.TryParse(Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_UNIQUE_GAME_ID"), out ulong id)
        && id == Game1.uniqueIDForThisGame
        && Constants.SaveFolderName == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_ID")
        && Game1.player.modData.TryGetValue("SDVKit/WorkspaceOwnerId", out string? owner)
        && owner == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_WORKSPACE_OWNER_ID")
        && Game1.player.modData.TryGetValue("SDVKit/FixtureId", out string? fixture)
        && fixture == Environment.GetEnvironmentVariable("SDVKIT_TEST_SAVE_FIXTURE_ID");

    private bool Active()
    {
        if (remaining <= 0) return false;
        if (Owned()) return true;
        remaining = 0;
        Log("STOP owned single fixture binding lost");
        return false;
    }

    private bool Arm(string[] args, int count)
    {
        if (!Owned() || args.Length != count || args[0].Length is < 1 or > 48
            || args[0].Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            || ButtonsToObserve.Any(b => Helper.Input.GetState(b) != SButtonState.None || Helper.Input.IsSuppressed(b)))
        {
            Monitor.Log("Refused: require exact owned single fixture, label, and neutral observed buttons.", LogLevel.Warn);
            return false;
        }
        label = args[0];
        remaining = 1800;
        following = 0;
        lastState = "";
        Log($"ARM pid={Environment.ProcessId} launch={launch} screen={Context.ScreenId} save={Constants.SaveFolderName}");
        Sample("baseline");
        return true;
    }

    private void Open(string command, string[] args)
    {
        if (args.Length != 2 || args[1] is not ("root" or "child" or "controller-root") || Game1.activeClickableMenu is not null)
        {
            Monitor.Log("Refused: mc217_open requires <label> <root|child|controller-root> and no active menu.", LogLevel.Warn);
            return;
        }
        if (!Arm(args, 2)) return;
        bool controller = args[1] == "controller-root";
        var root = new ProbeMenu(controller ? "controller-root" : "root", Callback, controller);
        if (args[1] == "child") root.SetChildMenu(new ProbeMenu("child", Callback));
        Game1.activeClickableMenu = root;
        Sample("opened");
    }

    private void Observe(string command, string[] args) => Arm(args, 1);

    private void Edge(string kind, SButton button)
    {
        if (!Active() || !ButtonsToObserve.Contains(button)) return;
        following = 16;
        Log($"{kind} button={button} state={Helper.Input.GetState(button)}");
    }

    private void Callback(string text)
    {
        if (!Active()) return;
        Log(text);
        Sample("callback");
    }

    private void Sample(string phase)
    {
        if (!Active()) return;
        GamePadState raw = Game1.playerOneIndex >= PlayerIndex.One ? GamePad.GetState(Game1.playerOneIndex) : default;
        IClickableMenu? root = Game1.activeClickableMenu;
        string state = $"root={Describe(root)} child={Describe(root?.GetChildMenu())} "
            + $"connected={Game1.input.GetGamePadState().IsConnected} rawConnected={raw.IsConnected} rawB={raw.Buttons.B} "
            + $"rawEscape={Keyboard.GetState().IsKeyDown(Keys.Escape)} rawLeft={Mouse.GetState().LeftButton} "
            + $"gamepadMode={Game1.options.gamepadMode} gamepadControls={Game1.options.gamepadControls} "
            + $"playerIndex={Game1.playerOneIndex} mappedB={Utility.mapGamePadButtonToKey(Buttons.B)} "
            + string.Join(" ", ButtonsToObserve.Select(b => $"{b}={Helper.Input.GetState(b)}"));
        if (following > 0 || state != lastState) Log($"{phase} {state}");
        lastState = state;
    }

    private static string Describe(IClickableMenu? menu) => menu is ProbeMenu probe ? $"Probe:{probe.Label}"
        : menu?.GetType().FullName ?? "none";

    private void Log(string text) => Monitor.Log($"MC217 seq={++sequence} case={label} tick={Game1.ticks} {text}", LogLevel.Info);
}

internal sealed class ProbeMenu : IClickableMenu
{
    private readonly Action<string> observe;
    private readonly bool explicitController;
    public string Label { get; }

    public ProbeMenu(string label, Action<string> observe, bool explicitController = false)
        : base((Game1.uiViewport.Width - 640) / 2, (Game1.uiViewport.Height - 320) / 2, 640, 320, true)
    {
        Label = label;
        this.observe = observe;
        this.explicitController = explicitController;
        Rectangle close = upperRightCloseButton.bounds;
        observe($"construct menu={Label} closeX={close.Center.X} closeY={close.Center.Y}");
    }

    public override void receiveKeyPress(Keys key)
    {
        observe($"receiveKeyPress menu={Label} key={key}");
        base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        observe($"receiveGamePadButton menu={Label} button={button}");
        if (explicitController && button == Buttons.B && readyToClose())
        {
            observe($"explicitControllerClose menu={Label}");
            exitThisMenu();
        }
        else base.receiveGamePadButton(button);
    }

    public override bool areGamePadControlsImplemented() => explicitController || base.areGamePadControlsImplemented();

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        observe($"receiveLeftClick menu={Label} x={x} y={y} closeHit={upperRightCloseButton.containsPoint(x, y)}");
        base.receiveLeftClick(x, y, playSound);
    }

    protected override void cleanupBeforeExit()
    {
        observe($"cleanupBeforeExit menu={Label}");
        base.cleanupBeforeExit();
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Color.DarkSlateGray);
        string behavior = explicitController ? "Explicit native B close; inherited keyboard/mouse." : "Inherited keyboard, mouse and controller behavior.";
        b.DrawString(Game1.smallFont, $"Neutral close probe: {Label}\n{behavior}",
            new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 64), Color.White);
        base.draw(b);
        if (GetChildMenu() is { } child) child.draw(b);
        else drawMouse(b);
    }
}

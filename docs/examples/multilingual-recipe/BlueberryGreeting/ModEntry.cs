using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace BlueberryGreeting;

internal sealed class ModEntry : Mod
{
    private const string AcceptKey = "blueberry_accept";
    private const string CancelKey = "blueberry_cancel";
    private const string StateKey = "SDVKit.Example.BlueberryGreeting/Accepted";
    private const string Marker = "BLUEBERRY-42";
    private bool _pendingSave;

    public override void Entry(IModHelper helper)
    {
        helper.ConsoleCommands.Add("blueberry-greeting",
            "Open or inspect the original multilingual greeting recipe.", Run);
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.Saved += OnSaved;
    }

    private void Run(string command, string[] arguments)
    {
        if (!Context.IsWorldReady || Game1.player is null || Game1.currentLocation is null)
        {
            Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
            {
                state = "worldNotReady",
                locale = Helper.Translation.Locale,
                accepted = (bool?)null,
                pendingSave = (bool?)null,
                marker = Marker,
                fallback = (string?)null,
            }), LogLevel.Warn);
            return;
        }

        switch (arguments.FirstOrDefault()?.ToLowerInvariant())
        {
            case "open":
                OpenDialogue();
                break;
            case "status":
                LogStatus("ready");
                break;
            case "title" when _pendingSave:
                LogStatus("saveRequired");
                Monitor.Log("Save the accepted greeting before requesting the title transition.", LogLevel.Warn);
                break;
            case "title" when Game1.activeClickableMenu is null:
                LogStatus("titleRequested");
                Game1.ExitToTitle();
                break;
            case "title":
                Monitor.Log("Close the active menu before requesting the title transition.", LogLevel.Warn);
                break;
            default:
                Monitor.Log("Usage: blueberry-greeting open|status|title", LogLevel.Info);
                break;
        }
    }

    private void OpenDialogue()
    {
        bool accepted = IsAccepted();
        string fallback = Translate("fallback.default-only");
        string prompt = Translate(accepted ? "dialogue.completed" : "dialogue.prompt", fallback);
        Response[] responses =
        [
            new Response(CancelKey, Translate("choice.cancel")),
            new Response(AcceptKey, Translate("choice.accept")),
        ];
        Game1.currentLocation.createQuestionDialogue(prompt, responses, (_, answer) =>
        {
            bool before = IsAccepted();
            bool changed = answer == AcceptKey && !before;
            if (changed)
            {
                Game1.player.modData[StateKey] = "1";
                _pendingSave = true;
            }
            bool after = IsAccepted();
            Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
            {
                state = "answered",
                locale = Helper.Translation.Locale,
                answer,
                before,
                after,
                changed,
            }), LogLevel.Info);
            Game1.drawObjectDialogue(Translate(after ? "result.accepted" : "result.cancelled"));
        });
        LogStatus("dialogueOpened");
    }

    private string Translate(string key, string? fallback = null) => Helper.Translation.Get(key, new
    {
        farmer = Game1.player.Name,
        marker = Marker,
        fallback = fallback ?? "",
    }).ToString();

    private static bool IsAccepted() => Game1.player.modData.TryGetValue(StateKey, out string? value)
        && value == "1";

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _pendingSave = false;
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        _pendingSave = false;
        LogStatus("saveCompleted");
    }

    private void LogStatus(string state) => Monitor.Log("BLUEBERRY_GREETING " + JsonSerializer.Serialize(new
    {
        state,
        locale = Helper.Translation.Locale,
        accepted = IsAccepted(),
        pendingSave = _pendingSave,
        marker = Marker,
        fallback = Translate("fallback.default-only"),
    }), LogLevel.Info);
}

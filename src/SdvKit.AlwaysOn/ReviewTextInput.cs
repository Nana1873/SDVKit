#if SDVKIT_GAME_AVAILABLE
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SdvKit.Cli.LiveLab;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace SdvKit.AlwaysOn;

internal static partial class ReviewVirtualCursor
{
    private sealed record TextTarget(NamingMenu Menu, TextBox Field, KeyboardDispatcher Dispatcher,
        GameWindow Window, object Player, string? Launch, string? Role)
    {
        public bool IsCurrent() => Context.IsWorldReady && !Game1.exitToTitle
            && Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") == "1"
            && Launch == Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID")
            && Role == Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE")
            && ReferenceEquals(Game1.activeClickableMenu, Menu) && Menu.GetChildMenu() is null
            && ReferenceEquals(Menu.textBox, Field) && !Field.PasswordBox && Field.Selected
            && ReferenceEquals(Game1.keyboardDispatcher, Dispatcher) && ReferenceEquals(Dispatcher.Subscriber, Field)
            && ReferenceEquals(Game1.player, Player) && ReferenceEquals(WindowField?.GetValue(Dispatcher), Window);
    }

    private sealed record PendingText(ReviewInputRequest Request, TextTarget Target,
        Func<string?> Revision, Action<ReviewInputResult> Completed)
    {
        public ReviewTextProgress Progress { get; } = new(Request.TextQuery!.Text!);
        public long StartedAt { get; } = Environment.TickCount64;
    }
    private sealed record PollSample(TextPoll? Text, CommandPoll? Command);
    private sealed record CommandPoll(PendingInput Pending, List<char> Queue, char Character);
    private sealed record TextPoll(PendingText Pending, List<char> Queue, char Character);
    private static MethodInfo? _raiseText;
    private static FieldInfo? WindowField;
    private static FieldInfo? _characters;
    private static FieldInfo? _commands;
    private static FieldInfo? _keys;
    private static FieldInfo? _enteredText;
    private static bool _textInstalled;
    private const string TextHarmonyId = "SDVKit.AlwaysOn.ReviewText";

    private static bool EnsureTextBinding()
    {
        // Native text events use a shared window. Until delivery can be bound
        // to one dispatcher, refuse text rather than forwarding it to peers.
        if (Context.IsSplitScreen) return false;
        if (_textInstalled) return true;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo? Field(string name, Type type)
        {
            FieldInfo? field = typeof(KeyboardDispatcher).GetField(name, fields);
            return field?.FieldType == type ? field : null;
        }
        _raiseText = typeof(GameWindow).GetMethod("OnTextInput", fields, null, [typeof(TextInputEventArgs)], null);
        WindowField = Field("_window", typeof(GameWindow));
        _characters = Field("_charsEntered", typeof(List<char>));
        _commands = Field("_commandInputs", typeof(List<char>));
        _keys = Field("_keysDown", typeof(List<Keys>));
        _enteredText = Field("_enteredText", typeof(string));
        if (!OperatingSystem.IsWindows() || _raiseText is not { IsAssembly: true, IsStatic: false }
            || _raiseText.ReturnType != typeof(void) || WindowField is null || _characters is null
            || _commands is null || _keys is null || _enteredText is null) return false;
        try
        {
            new Harmony(TextHarmonyId).Patch(AccessTools.DeclaredMethod(typeof(KeyboardDispatcher), nameof(KeyboardDispatcher.Poll)),
                prefix: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(BeforeTextPoll)),
                finalizer: new HarmonyMethod(typeof(ReviewVirtualCursor), nameof(AfterTextPoll)));
            _textInstalled = true;
            return true;
        }
        catch (Exception)
        {
            new Harmony(TextHarmonyId).UnpatchAll(TextHarmonyId);
            return false;
        }
    }

    private static TextTarget? CaptureTextTarget()
    {
        if (Game1.activeClickableMenu is not NamingMenu menu || menu.GetType() != typeof(NamingMenu)
            || menu.textBox is not { } field || field.GetType() != typeof(TextBox)
            || Game1.keyboardDispatcher is not { } dispatcher || dispatcher.GetType() != typeof(KeyboardDispatcher)
            || WindowField?.GetValue(dispatcher) is not GameWindow window
            || window.GetType().FullName != "Microsoft.Xna.Framework.SdlGameWindow") return null;
        var target = new TextTarget(menu, field, dispatcher, window, Game1.player,
            Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID"),
            Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"));
        return target.IsCurrent() ? target : null;
    }

    internal static bool TryText(ReviewInputRequest request, Func<string?> revision, Func<long, bool> field,
        Action<ReviewInputResult> completed, out string error)
    {
        lock (Sync)
        {
            EnsureInputOwner();
            error = "The text adapter is unavailable or another input action is pending.";
            if (!_installed || Screen.Value.Pending is not null || Screen.Value.Text is not null || Screen.Value.Mouse.HasPendingWheel || !EnsureTextBinding()) return false;
            error = "Text requires a fresh supported selected field and exact dispatcher subscriber.";
            if (request.TextQuery is not { } query || !ReviewInputContract.ValidTextTarget(query)
                || ReviewInputContract.ValidateText(query.Text) is not null || revision() != query.UiRevision
                || !field(query.FieldId!.Value) || CaptureTextTarget() is not { } target) return false;
            Screen.Value.Text = new(request, target, revision, completed);
            AllowBackgroundInputForNextTicks();
            error = string.Empty;
            return true;
        }
    }

    private static bool EmptyTextQueues(KeyboardDispatcher dispatcher) =>
        _characters?.GetValue(dispatcher) is List<char> { Count: 0 }
        && _commands?.GetValue(dispatcher) is List<char> { Count: 0 }
        && _keys?.GetValue(dispatcher) is List<Keys> { Count: 0 }
        && _enteredText?.GetValue(dispatcher) is null;

    private static void ObserveTextLifetime()
    {
        if (Screen.Value.Text is not { } pending) return;
        if (!pending.Target.IsCurrent() || Environment.TickCount64 - pending.StartedAt >= 10000)
            FinishText("The text target became unavailable or delivery exceeded its bounded lifetime.");
    }

    private static void BeforeTextPoll(KeyboardDispatcher __instance, out PollSample __state)
    {
        BeforeTextPollCore(__instance, out TextPoll? text);
        BeforeCommandPoll(__instance, out CommandPoll? command);
        __state = new(text, command);
    }

    private static Exception? AfterTextPoll(PollSample? __state, Exception? __exception)
    {
        AfterTextPollCore(__state?.Text, __exception);
        AfterCommandPoll(__state?.Command, __exception);
        return __exception;
    }

    private static void BeforeTextPollCore(KeyboardDispatcher __instance, out TextPoll? __state)
    {
        __state = null;
        lock (Sync)
        {
            if (Screen.Value.Text is not { } pending || !ReferenceEquals(__instance, pending.Target.Dispatcher)) return;
            try
            {
                if (!pending.Target.IsCurrent() || pending.Revision() != pending.Request.TextQuery!.UiRevision)
                { FinishText("The text field, subscriber or UI identity changed before delivery."); return; }
                // Existing native input belongs to the user. Leave it untouched and stop this request.
                if (!EmptyTextQueues(__instance) || Keyboard.GetState().GetPressedKeyCount() != 0)
                { FinishText("Concurrent native text or keyboard input interrupted delivery."); return; }
                var queue = (List<char>)_characters!.GetValue(__instance)!;
                if (!pending.Progress.TryNext(pending.Target.IsCurrent(), !EmptyTextQueues(__instance), out char character))
                { FinishText("Text delivery stopped before the next character."); return; }
                __state = new(pending, queue, character);
                _raiseText!.Invoke(pending.Target.Window, [new TextInputEventArgs(character, Keys.None)]);
                if (!pending.Target.IsCurrent() || queue.Count != 1 || queue[0] != character)
                {
                    RemoveOwnedCharacter(__state);
                    __state = null;
                    FinishText("The text event did not retain the exact field and single queued character.");
                }
            }
            catch (Exception)
            {
                if (__state is not null) RemoveOwnedCharacter(__state);
                __state = null;
                FinishText("The text event failed before acknowledged delivery.");
            }
        }
    }

    private static Exception? AfterTextPollCore(TextPoll? __state, Exception? __exception)
    {
        lock (Sync)
        {
            if (__state is null) return __exception;
            if (__exception is not null || __state.Queue.Count != 0)
            {
                RemoveOwnedCharacter(__state);
                FinishText("The dispatcher did not complete the queued character poll.");
            }
            else if (ReferenceEquals(Screen.Value.Text, __state.Pending))
            {
                Screen.Value.Text.Progress.ObservePoll(true);
                if (Screen.Value.Text.Progress.Complete) FinishText(null);
            }
        }
        return __exception;
    }

    private static void BeforeCommandPoll(KeyboardDispatcher __instance, out CommandPoll? __state)
    {
        __state = null;
        lock (Sync)
        {
            if (Screen.Value.Pending is not { CommandTarget: { } target, CommandDelivered: false } pending
                || pending.Progress.Canceled || pending.Progress.StartTick is null
                || !ReferenceEquals(__instance, target.Dispatcher)) return;
            try
            {
                if (!target.IsCurrent() || !EmptyTextQueues(__instance)
                    || Keyboard.GetState().GetPressedKeyCount() != 0)
                { CancelChord("Concurrent native input or a changed text target prevented the command event."); return; }
                char character = pending.CommandKey switch { Keys.Back => '\b', Keys.Enter => '\r', Keys.Tab => '\t', _ => '\0' };
                var queue = (List<char>)_commands!.GetValue(__instance)!;
                __state = new(pending, queue, character);
                _raiseText!.Invoke(target.Window, [new TextInputEventArgs(character, pending.CommandKey)]);
                if (!target.IsCurrent() || queue.Count != 1 || queue[0] != character)
                {
                    if (queue.Count > 0 && queue[0] == character) queue.RemoveAt(0);
                    __state = null;
                    CancelChord("The command event did not retain the exact field and single queue entry.");
                }
            }
            catch (Exception)
            {
                if (__state is { } sample && sample.Queue.Count > 0 && sample.Queue[0] == sample.Character) sample.Queue.RemoveAt(0);
                __state = null;
                CancelChord("The text command event failed before acknowledgement.");
            }
        }
    }

    private static Exception? AfterCommandPoll(CommandPoll? __state, Exception? __exception)
    {
        lock (Sync)
        {
            if (__state is not { } sample) return __exception;
            if (__exception is not null || sample.Queue.Count != 0)
            {
                if (sample.Queue.Count > 0 && sample.Queue[0] == sample.Character) sample.Queue.RemoveAt(0);
                CancelChord("The dispatcher did not complete the text command poll.");
            }
            else sample.Pending.CommandDelivered = true;
        }
        return __exception;
    }

    private static void RemoveOwnedCharacter(TextPoll sample)
    {
        if (sample.Queue.Count > 0 && sample.Queue[0] == sample.Character) sample.Queue.RemoveAt(0);
    }

    private static void FinishText(string? failure)
    {
        if (Screen.Value.Text is not { } pending) return;
        Screen.Value.Text = null;
        pending.Progress.Stop();
        pending.Completed(new(failure is null,
            failure ?? "Character events were delivered through the dispatcher; field acceptance and persistence require separate observation.",
            ProblemCode: failure is null ? null : "inputTextInterrupted", DeliveredScalars: pending.Progress.Delivered));
    }
}
#endif

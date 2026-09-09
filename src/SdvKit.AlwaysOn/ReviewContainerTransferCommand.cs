#if SDVKIT_GAME_AVAILABLE
using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using SdvKit.Cli.LiveLab;

namespace SdvKit.AlwaysOn;

internal static class ReviewContainerTransferCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Handle(string[] args, string runtimePath, IMonitor monitor,
        IModHelper helper, ReviewMenuCommand menuCommand, Func<TestSaveAutomation?> testSave)
    {
        if (args.Length == 3 && args[1] == "cancel" && ReviewTransportToken.IsRequestId(args[2]))
        {
            ReviewVirtualCursor.CancelRequest(args[2]);
            return;
        }
        if (!TryParse(args, out string requestId, out string launch, out ReviewContainerTransferQuery? query))
        {
            monitor.Log("SDVKit container transfer rejected an invalid transport request.", LogLevel.Error);
            return;
        }
        ReviewContainerTransferReport Refused(string code, ReviewContainerValues? before = null) =>
            new(1, "refused", code, launch, "single", null, DateTimeOffset.UtcNow,
                before is null ? null : new(query!.Direction, ReviewContainerTransferContract.SourceSide(query.Direction),
                    query.SourceSlot, query.Quantity, 0, query.QualifiedItemId, false, false, "refused", before, null, []));
        bool dispatchAccepted = false;
        ReviewContainerValues? retainedBefore = null;
        try
        {
            if (Environment.GetEnvironmentVariable("SDVKIT_PROJECT_REVIEW") != "1"
                || Environment.GetEnvironmentVariable("SDVKIT_LAB_LAUNCH_ID") != launch
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SDVKIT_NETWORK_TWO_ROLE"))
                || Context.ScreenId != 0 || testSave() is not { } fixture
                || !fixture.TryVerifyReviewFixture(out _, out _))
            { Write(Refused("containerTransferReviewBindingInvalid")); return; }
            if (!Context.IsWorldReady || Game1.exitToTitle)
            { Write(Refused("containerTransferWorldNotReady")); return; }
            ReviewContainerReport captured = ReviewContainerCommand.Capture(Guid.NewGuid().ToString("N"), launch, menuCommand);
            if (captured.State != "ready" || captured.Data is not { } before)
            { Write(Refused(captured.ErrorCode ?? "containerTransferCaptureFailed")); return; }
            retainedBefore = before;
            string sourceSide = ReviewContainerTransferContract.SourceSide(query!.Direction);
            if (ReviewContainerTransferContract.ObservationProblem(query, before) is string observationProblem)
            { Write(Refused(observationProblem, before)); return; }

            ItemGrabMenu menu = (ItemGrabMenu)Game1.activeClickableMenu;
            Chest chest = (Chest)menu.context;
            IList<Item> source = sourceSide == "player" ? Game1.player.Items : chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID);
            IList<Item> destination = sourceSide == "player" ? chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID) : Game1.player.Items;
            Item item = source[query.SourceSlot];
            if (item.GetType() != typeof(StardewValley.Object) || item.maximumStackSize() <= 1
                || item.IsRecipe || item.QualifiedItemId is "(O)102" or "(O)326" or "(O)434")
            { Write(Refused("containerTransferItemUnsupported", before)); return; }
            Item probe = item.getOne();
            probe.Stack = query.Quantity;
            bool capacity = query.Direction == ReviewContainerTransferContract.Withdraw
                ? Game1.player.couldInventoryAcceptThisItem(probe)
                : ChestCanAccept(chest, probe);
            if (!capacity)
            { Write(Refused("containerTransferDestinationFull", before)); return; }

            InventoryMenu sourceMenu = sourceSide == "player" ? menu.inventory : menu.ItemsToGrabMenu;
            if (query.SourceSlot >= sourceMenu.inventory.Count)
            { Write(Refused("containerTransferSourceSlotInvalid", before)); return; }
            Rectangle bounds = sourceMenu.inventory[query.SourceSlot].bounds;
            int x = bounds.Center.X;
            int y = bounds.Center.Y;
            object expectedChest = chest;
            object expectedItem = item;
            int initialSourceStack = item.Stack;
            Item?[] initialSource = source.ToArray();
            int[] initialSourceStacks = initialSource.Select(value => value?.Stack ?? 0).ToArray();
            string?[] initialSourceFacts = initialSource.Select(Fingerprint).ToArray();
            Item?[] initialDestination = destination.ToArray();
            int[] initialDestinationStacks = initialDestination.Select(value => value?.Stack ?? 0).ToArray();
            string?[] initialDestinationFacts = initialDestination.Select(Fingerprint).ToArray();
            string Continuity()
            {
                if (Game1.activeClickableMenu is not ItemGrabMenu current || current.GetType() != typeof(ItemGrabMenu)
                    || !ReferenceEquals(current.context, expectedChest) || current.heldItem is not null
                    || current.source != ItemGrabMenu.source_chest) return string.Empty;
                IList<Item> currentSource = sourceSide == "player" ? Game1.player.Items : chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID);
                IList<Item> currentDestination = sourceSide == "player" ? chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID) : Game1.player.Items;
                if (query.SourceSlot >= currentSource.Count || !ReferenceEquals(currentSource[query.SourceSlot], expectedItem)
                    || Fingerprint(currentSource[query.SourceSlot]) != initialSourceFacts[query.SourceSlot]
                    || currentSource[query.SourceSlot].Stack > initialSourceStack
                    || currentSource[query.SourceSlot].Stack < initialSourceStack - query.Quantity) return string.Empty;
                for (int index=0; index<initialSource.Length; index++)
                {
                    if (index == query.SourceSlot) continue;
                    Item? currentItem = index < currentSource.Count ? currentSource[index] : null;
                    if (!ReferenceEquals(currentItem, initialSource[index]) || (currentItem?.Stack ?? 0) != initialSourceStacks[index]
                        || Fingerprint(currentItem) != initialSourceFacts[index]) return string.Empty;
                }
                for (int index=0; index<initialDestination.Length; index++)
                {
                    Item? original = initialDestination[index];
                    Item? currentItem = index < currentDestination.Count ? currentDestination[index] : null;
                    if (original is null)
                    {
                        if (currentItem is not null && (Fingerprint(currentItem) != initialSourceFacts[query.SourceSlot]
                            || !currentItem.canStackWith(item))) return string.Empty;
                    }
                    else if (!ReferenceEquals(currentItem, original)
                        || Fingerprint(currentItem) != initialDestinationFacts[index]
                        || (!original.canStackWith(item) && currentItem.Stack != initialDestinationStacks[index])
                        || original.canStackWith(item) && (currentItem.Stack < initialDestinationStacks[index]
                            || currentItem.Stack > currentItem.maximumStackSize())) return string.Empty;
                }
                for (int index=initialDestination.Length; index<currentDestination.Count; index++)
                    if (currentDestination[index] is Item added && (Fingerprint(added) != initialSourceFacts[query.SourceSlot]
                        || !added.canStackWith(item))) return string.Empty;
                ReviewContainerReport progressReport = ReviewContainerCommand.Capture(Guid.NewGuid().ToString("N"), launch, menuCommand);
                if (progressReport.Data is not { } progress
                    || !ReviewContainerTransferContract.ProgressValid(before, progress, query, query.Quantity))
                    return string.Empty;
                int moved = initialSourceStack-currentSource[query.SourceSlot].Stack;
                return moved == ReviewContainerTransferContract.ObservedQuantity(before, progress, query)
                    ? "exact-source" : string.Empty;
            }
            var gesture = new ReviewInputQuery(ReviewInputContract.ClickAction, "MouseRight", null, x, y,
                UiRevision: before.ContainerRevision, Modifiers: [], Count: query.Quantity);
            bool scheduled = ReviewVirtualCursor.TryChord(helper.Input, [SButton.MouseRight], 1,
                before.ContainerRevision, () => CaptureRevision(), Complete, out string scheduleError,
                requestId, gesture, Continuity, () => $"{Game1.uiViewport.Width}x{Game1.uiViewport.Height}:{Game1.options.uiScale}");
            dispatchAccepted = scheduled;
            if (!scheduled)
            { Write(Refused("containerTransferDispatchRejected", before)); monitor.Log(scheduleError, LogLevel.Error); }

            string? CaptureRevision()
            {
                ReviewContainerReport current = ReviewContainerCommand.Capture(Guid.NewGuid().ToString("N"), launch, menuCommand);
                return current.Data?.ContainerRevision;
            }
            void Complete(ReviewInputResult result)
            {
                if (Game1.activeClickableMenu is not ItemGrabMenu finalMenu
                    || finalMenu.GetType() != typeof(ItemGrabMenu)
                    || !ReferenceEquals(finalMenu.context, expectedChest) || finalMenu.heldItem is not null
                    || !ReferenceEquals(finalMenu.sourceItem, expectedChest)
                    || finalMenu.source != ItemGrabMenu.source_chest)
                {
                    Write(new(1, "uncertain", "containerTransferBindingChanged", launch, "single", null,
                        DateTimeOffset.UtcNow, new(query.Direction, sourceSide, query.SourceSlot, query.Quantity,
                            null, query.QualifiedItemId, true, result.ProblemCode is "inputChordInterrupted",
                            "uncertain", before, null, ["afterObservationUnavailable"])));
                    return;
                }
                ReviewContainerReport afterReport = ReviewContainerCommand.Capture(Guid.NewGuid().ToString("N"), launch, menuCommand);
                ReviewContainerValues? after = afterReport.Data;
                int? observed = after is null ? null : ReviewContainerTransferContract.ObservedQuantity(before, after, query);
                bool complete = result.Succeeded && observed == query.Quantity
                    && ReviewContainerTransferContract.Conserved(before, after!, query);
                string outcome = complete ? "completed" : observed is > 0 ? "partial" : after is null ? "uncertain" : "refused";
                string? code = complete ? null : result.ProblemCode ?? (after is null
                    ? "containerTransferAfterUnavailable" : observed is > 0
                        ? "containerTransferPartial" : "containerTransferNotObserved");
                Write(new(1, complete ? "completed" : outcome, code, launch, "single", null, DateTimeOffset.UtcNow,
                    new(query.Direction, sourceSide, query.SourceSlot, query.Quantity, observed, query.QualifiedItemId,
                        true, result.ProblemCode is "inputChordInterrupted", outcome, before, after,
                        after is null ? ["afterObservationUnavailable"] : [])));
            }
        }
        catch (Exception exception)
        {
            Write(dispatchAccepted && retainedBefore is not null
                ? new(1, "uncertain", "containerTransferFailed", launch, "single", null, DateTimeOffset.UtcNow,
                    new(query!.Direction, ReviewContainerTransferContract.SourceSide(query.Direction), query.SourceSlot,
                        query.Quantity, null, query.QualifiedItemId, true, false, "uncertain", retainedBefore, null,
                        ["afterObservationUnavailable"]))
                : Refused("containerTransferFailed", retainedBefore));
            monitor.Log($"SDVKit container transfer failed closed: {exception.GetBaseException().Message}", LogLevel.Error);
        }

        void Write(ReviewContainerTransferReport report)
        {
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewContainerTransferResponseEnvelope(1, requestId, report), JsonOptions);
                if (bytes.Length > ReviewContainerTransferContract.MaximumResponseBytes) throw new InvalidDataException("Transfer response limit exceeded.");
                ReviewResponseFile.Write(ReviewContainerTransferContract.ResponsePath(runtimePath, requestId), bytes);
            }
            catch (Exception exception) { monitor.Log($"SDVKit container transfer response failed: {exception.Message}", LogLevel.Error); }
        }
    }

    private static bool TryParse(string[] args, out string requestId, out string launch, out ReviewContainerTransferQuery? query)
    {
        requestId = launch = string.Empty; query = null;
        if (args.Length != 11 || args[0] != "container-transfer" || !ReviewTransportToken.IsRequestId(args[1])
            || !ReviewTransportToken.IsRequestId(args[2]) || !ReviewContainerTransferContract.IsDirection(args[3])
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
            || !int.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out int quantity)
            || quantity is < 1 or > ReviewContainerTransferContract.MaximumQuantity) return false;
        requestId=args[1]; launch=args[2];
        query = new(args[3], slot, quantity, args[6], args[7], args[8], args[9], args[10]);
        return true;
    }

    private static bool ChestCanAccept(Chest chest, Item item)
    {
        var copies = chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Select(value => value?.getOne()).ToList();
        for (int i=0;i<copies.Count;i++) if (copies[i] is Item source) source.Stack = chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID)[i].Stack;
        Item probe = item.getOne(); probe.Stack = item.Stack;
        return StardewValley.Utility.addItemToThisInventoryList(probe, copies, chest.GetActualCapacity()) is null;
    }

    private static string? Fingerprint(Item? item)
    {
        if (item is null) return null;
        return ReviewContainerContract.TryTransferItemFingerprint(item.QualifiedItemId,
            item is StardewValley.Object value ? value.Quality : null,
            item.GetType().FullName ?? item.GetType().Name, item.maximumStackSize(), item.Name,
            item is ColoredObject colored ? colored.color.Value.PackedValue : null,
            item is StardewValley.Object ordered ? ordered.orderData.Value : null,
            out string fingerprint) ? fingerprint : null;
    }

}
#endif

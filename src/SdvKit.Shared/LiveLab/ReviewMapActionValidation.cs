using System.Text;

namespace SdvKit.Cli.LiveLab;

internal static class ReviewMapActionValidation
{
    internal static string? FirstToken(string value)
    {
        int start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        int end = start;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return end == start ? null : value[start..end];
    }

    internal static int Compare(ReviewMapActionReport left, ReviewMapActionReport right)
    {
        int order = string.CompareOrdinal(left.LayerId, right.LayerId);
        if (order == 0) order = left.Y.CompareTo(right.Y);
        if (order == 0) order = left.X.CompareTo(right.X);
        if (order == 0) order = string.CompareOrdinal(left.Name, right.Name);
        if (order == 0) order = string.CompareOrdinal(left.Source, right.Source);
        if (order == 0) order = Nullable.Compare(left.FrameIndex, right.FrameIndex);
        return order;
    }

    internal static bool IsValidList(IReadOnlyList<ReviewMapActionReport> actions)
    {
        if (actions.Count > ReviewMapContract.MaximumActionsPerMap) return false;
        long payloadBytes = 0;
        ReviewMapActionReport? previous = null;
        foreach (ReviewMapActionReport? action in actions)
        {
            if (action is null
                || string.IsNullOrWhiteSpace(action.LayerId)
                || action.LayerId.Length > ReviewMapContract.MaximumIdentityLength
                || action.LayerId.Any(char.IsControl)
                || !ReviewTransportText.IsWellFormedUtf16(action.LayerId)
                || StableIdentityNormalizer.Normalize(action.LayerId).Length == 0
                || action.X is < 0 or >= ReviewMapContract.MaximumLayerDimension
                || action.Y is < 0 or >= ReviewMapContract.MaximumLayerDimension
                || action.Name is not ("Action" or "TouchAction")
                || action.Source is not (ReviewMapContract.DirectSource or ReviewMapContract.TileIndexSource)
                || (action.Source == ReviewMapContract.DirectSource && action.FrameIndex is not null)
                || action.FrameIndex is < 0 or >= ReviewMapContract.MaximumFramesPerTile
                || action.Value is null
                || action.Value.Any(char.IsControl)
                || !ReviewTransportText.IsWellFormedUtf16(action.Value)
                || Encoding.UTF8.GetByteCount(action.Value) > ReviewMapContract.MaximumPropertyValueBytes
                || !string.Equals(action.FirstToken, FirstToken(action.Value), StringComparison.Ordinal)
                || (previous is not null && Compare(previous, action) >= 0))
            {
                return false;
            }
            payloadBytes += Encoding.UTF8.GetByteCount(action.Name)
                + (long)Encoding.UTF8.GetByteCount(action.Value);
            if (payloadBytes > ReviewMapContract.MaximumPropertyPayloadBytes) return false;
            previous = action;
        }
        return true;
    }
}

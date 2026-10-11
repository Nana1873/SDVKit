namespace SdvKit.GameSide;

internal sealed record FishingBank(int X, int Y, int Facing, int WaterX, int WaterY);

internal static class FishingBankSearch
{
    // Bound a synchronous main-thread scan and keep every game query inside the map.
    public const int MaximumDimension = 512;

    /// <summary>
    /// Finds the standing tile closest to the origin with natural water two and three tiles to its left or right.
    /// The search grows in square rings around the origin and stops once no closer bank can exist.
    /// </summary>
    public static FishingBank? Find(
        int width, int height,
        Func<int, int, bool> isNaturalWater,
        Func<int, int, bool> canStand,
        int originX = 0,
        int originY = 0)
    {
        if (width is < 3 or > MaximumDimension || height is < 3 or > MaximumDimension
            || originX < 0 || originY < 0 || originX >= width || originY >= height)
        {
            return null;
        }

        // Down casts sometimes spent stamina without starting fishing; the cause is undetermined (#305).
        // Up casts share the untested vertical path, so do not fall back to either vertical direction.
        (int X, int Y, int Facing)[] offsets = [(2, 0, 1), (-2, 0, 3)];
        FishingBank? best = null;
        long bestDistance = long.MaxValue;
        int maximumRadius = Math.Max(width, height);
        for (int radius = 0; radius <= maximumRadius; radius++)
        {
            if (best is not null && (long)radius * radius > bestDistance)
            {
                break;
            }

            // Walk only the ring's perimeter.
            for (int x = originX - radius; x <= originX + radius; x++)
            {
                Visit(x, originY - radius);
                if (radius > 0)
                {
                    Visit(x, originY + radius);
                }
            }

            for (int y = originY - radius + 1; y <= originY + radius - 1; y++)
            {
                Visit(originX - radius, y);
                Visit(originX + radius, y);
            }
        }

        return best;

        void Visit(int x, int y)
        {
            if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1)
            {
                return;
            }

            long distance = (long)(x - originX) * (x - originX) + (long)(y - originY) * (y - originY);
            if (distance >= bestDistance)
            {
                return;
            }

            foreach (var offset in offsets)
            {
                int waterX = x + offset.X, waterY = y + offset.Y;
                int beyondX = waterX + offset.X / 2, beyondY = waterY + offset.Y / 2;
                if (waterX < 0 || waterY < 0 || waterX >= width || waterY >= height
                    || beyondX < 0 || beyondY < 0 || beyondX >= width || beyondY >= height
                    || !isNaturalWater(waterX, waterY) || !isNaturalWater(beyondX, beyondY) || !canStand(x, y))
                {
                    continue;
                }

                best = new FishingBank(x, y, offset.Facing, waterX, waterY);
                bestDistance = distance;
                return;
            }
        }
    }
}

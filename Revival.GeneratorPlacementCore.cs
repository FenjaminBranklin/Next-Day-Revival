// Pure detail-grid bounds for static generator placement, tested off game.
using System;

namespace NextDayRevival
{
    internal static class GeneratorPlacementCore
    {
        internal static bool Cells(float low, float high, float origin, float size,
            int resolution, out int start, out int count)
        {
            start = count = 0;
            if (size <= 0f || resolution <= 0 || high <= low
                || high <= origin || low >= origin + size) return false;
            start = Math.Max(0, Math.Min(resolution,
                (int)Math.Floor((low - origin) / size * resolution)));
            int end = Math.Max(0, Math.Min(resolution,
                (int)Math.Ceiling((high - origin) / size * resolution)));
            count = end - start;
            return count > 0;
        }
    }
}

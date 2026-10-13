// Manual ground fire policy. Automatic ZU self-defence keeps its 300 m envelope.
using System;

namespace NextDayRevival
{
    internal static class RadarGroundCore
    {
        internal const int Kind = 1, Marker = 6, RayBudget = 2, ThinkBudget = 4;
        internal const float K = 2.8f, Margin = 20f * K, Near = 300f * K;

        internal static bool Area(float x, float z)
        {
            return x >= 3990f - Margin && x <= 4820f + Margin
                && z >= -1690f - Margin && z <= 1690f + Margin;
        }

        internal static float Interval(float distanceSquared, bool selected)
        { return selected || distanceSquared <= Near * Near ? 0.25f : 1f; }

        internal static bool Reach(float x, float y, float z, float range, float minPitch, float dead)
        {
            float flat = x * x + z * z;
            if (flat < dead * dead || flat + y * y > range * range) return false;
            double pitch = Math.Atan2(y, Math.Sqrt(flat)) * 180.0 / Math.PI;
            return pitch >= minPitch && pitch <= 15.0;
        }
    }
}

// Y S4: bounded, allocation-free seat allocation. One row per actual seat.
using System;

namespace NextDayRevival
{
    internal struct MercStationChoice
    {
        internal int Group;
        internal float Distance;
        internal bool Free, Used;
    }

    internal static class MercStationPlan
    {
        internal const float Reach = 168f; // 60 m, 2.8 world units per metre

        // Prefer every seat of the explicitly picked gun before spilling to
        // other nearby guns. Default commands use distance from the owner.
        internal static int Take(MercStationChoice[] seats, int count, int preferred)
        {
            int best = -1;
            float score = Single.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!seats[i].Free || seats[i].Used || seats[i].Distance > Reach * Reach) continue;
                float s = seats[i].Distance;
                if (preferred != 0 && seats[i].Group != preferred) s += Reach * Reach + 1f;
                if (s < score) { best = i; score = s; }
            }
            if (best >= 0) seats[best].Used = true;
            return best;
        }

        internal static bool Retreat(float health, bool retreating, bool danger)
        {
            return danger || health < (retreating ? 0.5f : 0.35f);
        }

        // The station identity fits exactly in Photon float packets. Collision
        // detection lives in the tube registry; never seat on an ambiguous key.
        internal static int TubeKey(int x, int z)
        {
            unchecked
            {
                uint h = ((uint)x * 16777619u) ^ ((uint)z * 2166136261u);
                h ^= h >> 16;
                return 100 + (int)(h & 0x3fffff) * 2;
            }
        }
    }
}

// Power 3 (docs/ai/POWER.md): the engine-free rules of the fragile POL depot
// tanks. A tank has a few hit points; heavy hits (bombs, artillery, Katyusha,
// big native blasts such as C4 or a tank shell) cost two, medium hits (tank
// shells, FPV drones, mid-size native blasts, a heavy MG burst) cost one.
// At zero the tank explodes (smoke column, no fire) and its share of the pool
// is lost; it stays a wreck until it is repaired, and a repaired tank is empty.
// research/fuel_depot_check.py compiles this file with csc 3.5.
using System;

namespace NextDayRevival
{
    internal static class FuelDepotCore
    {
        // Same values as PowerNetCore.HitMedium / HitHeavy.
        internal const int HitLight = 0, HitMedium = 1, HitHeavy = 2;
        internal const int DefaultTankHits = 4;          // two heavy or four medium hits
        internal const float HeavyMarginM = 4f, MediumMarginM = 1.5f;
        internal const float SameImpactSeconds = 0.2f;   // one bang reported by two paths
        internal const float DefaultHeavyBlast = 1200f, DefaultMediumBlast = 600f;
        internal const int DefaultBurstHits = 15;
        internal const float DefaultBurstSeconds = 3f;

        internal static int Cost(int cls)
        {
            return cls == HitHeavy ? 2 : cls == HitMedium ? 1 : 0;
        }

        /// <summary>Distance from the tank shell within which a hit of this
        /// class counts, in metres.</summary>
        internal static float MarginM(int cls)
        {
            return cls == HitHeavy ? HeavyMarginM : cls == HitMedium ? MediumMarginM : 0f;
        }

        /// <summary>The class of a native explosion by its ExplosionDamage
        /// (grenades and small charges stay light).</summary>
        internal static int BlastClass(float damage, float heavyAt, float mediumAt)
        {
            if (damage >= heavyAt && heavyAt > 0f) return HitHeavy;
            if (damage >= mediumAt && mediumAt > 0f) return HitMedium;
            return HitLight;
        }

        /// <summary>A hit at distance `distM` (metres) from the shell of a
        /// blast of radius `radiusM` counts if it is within the class margin
        /// or within a quarter of the blast radius.</summary>
        internal static bool Counts(int cls, float distM, float radiusM)
        {
            if (Cost(cls) == 0 || distM < 0f) return false;
            return distM <= MarginM(cls) || distM <= radiusM * 0.25f;
        }

        /// <summary>Firearm hits on one tank: `need` hits within `window`
        /// seconds make one medium hit (a heavy MG burst). Returns true when
        /// the burst is complete; the counter starts again.</summary>
        internal static bool Burst(ref int count, ref float since, float now, int need, float window)
        {
            if (need <= 0) return false;
            if (count == 0 || now - since > window) { count = 0; since = now; }
            count++;
            if (count < need) return false;
            count = 0;
            return true;
        }

        /// <summary>The same impact reported twice (shooter and native echo,
        /// or a bomb through two hooks) is billed once.</summary>
        internal static bool SameImpact(float lastAt, float now)
        {
            return lastAt > 0f && now >= lastAt && now - lastAt < SameImpactSeconds;
        }

        /// <summary>Power 5: the damage report that means "C4 on the tank"
        /// (FuelDepot.Demolish); it is above every hit class.</summary>
        internal const float DemolishCost = 100f;

        /// <summary>Power 5: a C4 charge on the tank - every hit point goes.
        /// Returns true when it was standing.</summary>
        internal static bool Demolish(ref float points)
        {
            if (points <= 0f) return false;
            points = 0f;
            return true;
        }

        /// <summary>Applies a hit to `points`; returns true when this hit
        /// destroyed the tank.</summary>
        internal static bool Hit(ref float points, int cls)
        {
            int cost = Cost(cls);
            if (cost == 0 || points <= 0f) return false;
            points = Math.Max(0f, points - cost);
            return points <= 0f;
        }

        /// <summary>The pool after a tank of `units` out of `capBefore` is
        /// lost: its share of what is in the pool goes with it, and the rest
        /// never exceeds what the standing tanks hold.</summary>
        internal static float PoolAfterLoss(float pool, float units, float capBefore)
        {
            float p = pool;
            if (capBefore > 0f) p = Math.Max(0f, p - p * units / capBefore);
            float capAfter = Math.Max(0f, capBefore - units);
            return Math.Min(p, capAfter);
        }

        /// <summary>A repaired tank stands again with full hit points; the
        /// pool is not touched (the tank is empty and fills with the refill).</summary>
        internal static float PoolAfterRepair(float pool)
        {
            return pool;
        }
    }
}

// Player An-2 ground contact policy. Real metres, m/s and degrees; no engine calls.
using System;

namespace NextDayRevival
{
    internal enum An2GroundTouch { None, Scratch, Destroy }

    internal static class An2GroundTouchCore
    {
        internal const float SinkDefault = 6f;
        internal const float SideDefault = 70f; // km/h, sideways only
        internal const float BankDefault = 60f;
        internal const float SlopeDefault = 0.6f;
        internal const float ImpactDefault = 45f; // km/h into a surface
        internal const float SoftSeconds = 3f;
        internal const int RayBudget = 8;

        internal static bool Migrate(int version, ref float sink, ref float side,
            ref float bank, ref float slope, ref float impact)
        {
            if (version >= 1) return false;
            if (Math.Abs(sink - 4f) < 0.001f) sink = SinkDefault;
            if (Math.Abs(side - 140f) < 0.001f) side = SideDefault;
            if (Math.Abs(bank - 25f) < 0.001f) bank = BankDefault;
            if (Math.Abs(slope - 0.45f) < 0.001f) slope = SlopeDefault;
            if (Math.Abs(impact - 20f) < 0.001f) impact = ImpactDefault;
            return true;
        }

        internal static An2GroundTouch Landing(float sink, float run, float side,
            float nose, float bank, float airAge, float sinkLimit, float sideKmh,
            float bankLimit)
        {
            float limit = Math.Max(0.5f, sinkLimit);
            bool soft = airAge >= 0f && airAge < SoftSeconds;
            float factor = soft ? 1.5f : 1f;
            if (sink > factor * limit || Math.Abs(side) * 3.6f > factor * Math.Max(10f, sideKmh))
                return An2GroundTouch.Destroy;
            // Bank alone and forward runway speed alone cannot destroy the plane.
            if ((nose < -12f || nose > 35f) && (sink > 1.5f || run > 8f))
                return An2GroundTouch.Destroy;
            if (!soft && Math.Abs(bank) > Math.Max(30f, bankLimit) && sink > 0.5f * limit)
                return An2GroundTouch.Destroy;
            if (sink > 0.6f * limit || Math.Abs(side) > 3f || Math.Abs(bank) > 25f)
                return An2GroundTouch.Scratch;
            return An2GroundTouch.None;
        }

        internal static An2GroundTouch Probe(int part, float penetration, float sink,
            float run, float side, float airAge, float sinkLimit, float sideKmh)
        {
            if (penetration <= 0.05f) return An2GroundTouch.None;
            float factor = airAge >= 0f && airAge < SoftSeconds ? 1.5f : 1f;
            if (sink > factor * Math.Max(0.5f, sinkLimit)
                || Math.Abs(side) * 3.6f > factor * Math.Max(10f, sideKmh))
                return An2GroundTouch.Destroy;
            // Four wing probes precede the propeller in the measured rig.
            // A shallow body contact is still a scrape; a driven-in body is an impact.
            if (part >= 4 && penetration > 0.35f && (sink > 1.5f || run > 8f))
                return An2GroundTouch.Destroy;
            return An2GroundTouch.Scratch;
        }

        internal static An2GroundTouch Surface(float closing, float limitKmh)
        {
            if (closing > Math.Max(5f, limitKmh) / 3.6f) return An2GroundTouch.Destroy;
            return closing > 0.2f ? An2GroundTouch.Scratch : An2GroundTouch.None;
        }

        internal static float ScratchDamage(float damage, float now, ref float next)
        {
            if (now < next) return damage;
            next = now + 3f;
            // Scratches impair thrust by 1 percent, capped at 10 percent. They never kill.
            return Math.Min(0.1f, damage + 0.01f);
        }
    }
}

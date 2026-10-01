// W AA5: arithmetic shared unchanged with the offline flight simulation.
using System;

namespace NextDayRevival
{
    internal struct ShortBurst
    {
        internal float End, Next, PauseUntil;
        internal bool Active;
    }

    internal static class ShortRangeCore
    {
        internal const float RangeM = 1400f, CeilingM = 600f, SpeedM = 970f;
        internal const float BurstSeconds = 0.6f, PauseSeconds = 1.0f;
        internal const float ShotSeconds = 0.05f, ReloadSeconds = 10f;
        internal const int Magazine = 100, HeliHits = 16;
        internal const float InitialMil = 18f, FloorMil = 1.8f;
        internal const float SplashM = 1.2f, Lag = 4f, Evade = 0.9f;

        // No catch-up volley after a slow frame. Correction is once per burst.
        internal static bool Shot(ref ShortBurst b, float now, bool ready, out bool corrected)
        {
            return Shot(ref b, now, ready, 1f, out corrected);
        }

        internal static bool Shot(ref ShortBurst b, float now, bool ready, float pauseScale, out bool corrected)
        {
            corrected = false;
            if (b.Active && (!ready || now >= b.End))
            {
                b.Active = false;
                b.PauseUntil = now + PauseSeconds * pauseScale;
                corrected = true;
            }
            if (!ready || now < b.PauseUntil) return false;
            if (!b.Active)
            {
                b.Active = true;
                b.End = now + BurstSeconds;
                b.Next = now;
            }
            if (now < b.Next) return false;
            b.Next = now + ShotSeconds;
            return true;
        }

        // All calibres contribute fractions of the same airframe, not a
        // shared raw hit counter (one 23 mm hit cannot become half an 85 mm kill).
        internal static float AddHit(float damage, int hitsToKill)
        {
            return Math.Min(1f, damage + 1f / Math.Max(1, hitsToKill));
        }

        internal static float Reaction(bool radar, bool drone)
        {
            return drone ? 0.5f : radar ? 0.8f : 1.2f;
        }
        internal static float Initial(float distance, bool drone)
        {
            return distance * InitialMil * (drone ? 0.35f : 1f) * 0.001f;
        }
        internal static float Correct(float error, float distance, bool radar, bool drone)
        {
            float floor = distance * (drone ? 0.7f : FloorMil) * 0.001f;
            return Math.Max(floor, error * (radar ? 0.4f : 0.5f));
        }
        internal static float Evasion(float deltaSpeed, float flightTime)
        {
            return deltaSpeed > 0.5f ? deltaSpeed * flightTime * Evade : 0f;
        }
    }
}

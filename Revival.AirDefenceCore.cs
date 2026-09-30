// W AA6: allocation-free arithmetic shared with the offline simulation.
using System;

namespace NextDayRevival
{
    internal struct FlareSupply
    {
        internal int Remaining, Sequence;
        internal float Next, Until;
    }

    internal static class AirDefenceCore
    {
        internal const int FlareBursts = 6;
        internal const float FlareCooldown = 4f, FlareLife = 2.5f;
        internal const float ShadowHeightU = 30f * 2.8f;

        internal static bool Dispense(ref FlareSupply s, float now)
        {
            if (s.Remaining <= 0 || now < s.Next) return false;
            s.Remaining--; s.Sequence++;
            s.Next = now + FlareCooldown; s.Until = now + FlareLife;
            return true;
        }

        internal static bool Decoy(float now, float until, float distanceSquared, float forwardDot)
        {
            // A bright flare must be in the forward seeker cone, within 300 m.
            return now < until && distanceSquared <= 840f * 840f
                && distanceSquared > 4f && forwardDot >= 0.85f;
        }

        internal static float Lock(float held, float dt, bool same, bool visible, float seconds)
        {
            if (!visible) return 0f;
            return Math.Min(seconds, (same ? held : 0f) + Math.Min(dt, 0.1f));
        }

        internal static bool RadarVisible(float heightU, bool terrainBlocked)
        {
            // No blanket 30 m cutoff: a low aircraft over open ground is visible.
            // All heights are masked by an intervening ridge; low flight is
            // particularly easy to hide. Existing 3 m clutter remains separate.
            return heightU >= 3f * 2.8f && !terrainBlocked;
        }
    }
}

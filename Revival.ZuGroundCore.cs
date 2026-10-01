// Z K5a2: arithmetic ground envelope, shared with the offline simulation.
using System;

namespace NextDayRevival
{
    internal static class ZuGroundCore
    {
        internal const float K = 2.8f, RangeM = 600f, DeadM = 25f;
        internal const float MinPitch = -4f, MaxPitch = 15f, Damage = 90f;
        internal const int ModePacket = 13;

        internal static bool Envelope(float dx, float dy, float dz)
        {
            float flat = dx * dx + dz * dz;
            if (flat < DeadM * DeadM * K * K || flat > RangeM * RangeM * K * K) return false;
            double pitch = Math.Atan2(dy, Math.Sqrt(flat)) * 180 / Math.PI;
            return pitch >= MinPitch && pitch <= MaxPitch;
        }

        internal static bool GroundAllowed(int duty, bool airTarget)
        {
            return duty != MercCrewPhase.Air && (duty == MercCrewPhase.Ground || !airTarget);
        }

        internal static bool Rifle(bool groundPhase, bool reachable, bool closeThreat)
        {
            return closeThreat || (groundPhase && !reachable);
        }
    }
}

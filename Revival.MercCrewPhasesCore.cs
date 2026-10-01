// Z K5a1: allocation-free AA duty policy. Units are 2.8 world units/metre.
using System;

namespace NextDayRevival
{
    internal sealed class MercCrewPhase
    {
        internal const int Auto = 0, Air = 1, Ground = 2;
        internal const float GroundRange = 300f * 2.8f;
        internal const float PostRadius = 6f * 2.8f;
        internal int Override;
        internal bool OnGround;
        internal float NextSample, GroundUntil;
        internal int Cursor;
        float _clearSince = -1f;

        internal void Reset()
        {
            Override = Auto; OnGround = false; NextSample = GroundUntil = 0f;
            Cursor = 0; _clearSince = -1f;
        }

        // New air has no hysteresis. Only the all-clear is delayed, to avoid
        // seat churn at a radar/scan boundary. Manual AIR/GROUND are immediate.
        internal bool Step(float now, bool radarFresh, bool inbound, bool ground)
        {
            if (Override == Air) { OnGround = false; _clearSince = -1f; }
            else if (Override == Ground) OnGround = true;
            else if (!radarFresh || inbound) { OnGround = false; _clearSince = -1f; }
            else
            {
                if (_clearSince < 0f) _clearSince = now;
                OnGround = ground && now - _clearSince >= 1f;
            }
            return OnGround;
        }

        internal static bool NearGround(float dx, float dz)
        {
            return dx * dx + dz * dz <= GroundRange * GroundRange;
        }
        internal static bool InPost(float dx, float dz)
        {
            return dx * dx + dz * dz <= PostRadius * PostRadius;
        }
    }
}

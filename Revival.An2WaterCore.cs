// Amphibious flight policy, engine-free C# 3.0. Speeds are metres/second.
using System;

namespace NextDayRevival
{
    internal enum An2WaterPhase { Air, Displacement, Planing }

    internal static class An2WaterCore
    {
        internal const float TopKmh = 185f;
        internal const float Draft = .06f;
        internal static An2WaterPhase Step(An2WaterPhase previous, bool supported, float speed)
        {
            if (!supported) return An2WaterPhase.Air;
            // Hysteresis prevents a drag/attitude oscillation at the step.
            return speed >= (previous == An2WaterPhase.Planing ? 9f : 11f)
                ? An2WaterPhase.Planing : An2WaterPhase.Displacement;
        }
        internal static float Drag(An2WaterPhase phase, float speed)
        {
            speed = Math.Abs(speed);
            if (phase == An2WaterPhase.Air) return 0;
            // Displacement hump, then less wetted hull on the step. Both stay
            // below engine thrust, including at rotation with aerodynamic drag.
            return phase == An2WaterPhase.Planing ? .22f + .002f * speed * speed
                : .18f + .11f * Math.Min(speed, 11f);
        }
        internal static float TurnRate(float speed, float wash)
        {
            // Water rudders need flow; no pirouetting a stopped, dead aircraft.
            return (float)(22f * Math.Min(1f, Math.Abs(speed) / 3f + wash * .3f)
                / (1f + Math.Abs(speed) / 7f));
        }
        internal static float Pitch(An2WaterPhase phase, float speed, float stick,
            float throttle, float stall, bool assisted)
        {
            if (phase != An2WaterPhase.Planing) return 0;
            bool rotate = speed > 1.12f * stall && throttle > .7f && stick > -.1f && assisted;
            return rotate ? 10f : Math.Max(0, Math.Min(10f, 3f + stick * 7f));
        }
        internal static An2GroundTouch Touch(float sink, float run, float side, float nose,
            float bank, float airborne, float sinkLimit, float sideLimit, float bankLimit)
        {
            An2GroundTouch land = An2GroundTouchCore.Landing(sink, run, side, 0,
                bank, airborne, sinkLimit, sideLimit, bankLimit);
            if (land == An2GroundTouch.Destroy) return land;
            // Floats tolerate a shallow flare, but a fast one-float contact or
            // digging the bows into the surface can overturn the aircraft.
            if (run > 3f && (Math.Abs(bank) > 18f || nose < -8f || nose > 22f))
                return An2GroundTouch.Destroy;
            if (run * 3.6f > 145f) return An2GroundTouch.Destroy;
            return land;
        }
        internal static float RadarFactor(bool water, float heightMetres)
        {
            return water && heightMetres >= 0 && heightMetres < 40f ? .2f : 1f;
        }
        internal static bool NearJetty(float x, float z, float dockX, float dockZ)
        {
            return (x - dockX) * (x - dockX) + (z - dockZ) * (z - dockZ) <= 45f * 45f;
        }
    }
}

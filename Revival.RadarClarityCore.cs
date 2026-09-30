// X radar clarity: pure arithmetic, compiled unchanged by the offline check.
using System;

namespace NextDayRevival
{
    internal static class RadarClarityCore
    {
        internal const float K = 2.8f, LowM = 120f, LandingLead = 30f;
        internal const int Friend = 0, Identify = 1, Heavy = 2, Short = 3,
            Low = 4, Outside = 5, NoGuns = 6, Observe = 7;

        // ETA is closest approach to the airfield, only for a closing track
        // passing within 1 km. A wide pass, hover or departure has no ETA.
        internal static int Eta(float rx, float rz, float vx, float vz)
        {
            float v2 = vx * vx + vz * vz;
            if (v2 < 1f) return -1;
            float t = -(rx * vx + rz * vz) / v2;
            if (t <= 0f || t > 600f) return -1;
            float x = rx + vx * t, z = rz + vz * t;
            float radius = 1000f * K;
            return x * x + z * z <= radius * radius ? (int)Math.Round(t) : -1;
        }

        internal static int Threat(int iff, int type, int eta, int rangeTenths)
        {
            if (iff > 0) return 0;
            if (iff == 0) return 1000 - Math.Min(999, rangeTenths);
            int payload = type == 2 ? 400 : type == 3 ? 350 : type == 1 ? 300 : type == 0 ? 250 : 100;
            return 2000 + payload + (eta >= 0 ? 2400 - 4 * Math.Min(600, eta) : 0)
                + Math.Max(0, 200 - rangeTenths);
        }

        internal static bool Reach(float dx, float dy, float dz, float rangeU,
                                   float ceilingU, float minPitch, float maxPitch)
        {
            float flat2 = dx * dx + dz * dz;
            if (flat2 + dy * dy > rangeU * rangeU || dy > ceilingU) return false;
            float pitch = (float)(Math.Atan2(dy, Math.Sqrt(flat2)) * 180.0 / Math.PI);
            return pitch >= minPitch && pitch <= maxPitch;
        }

        internal static int Advice(int iff, int type, float heightM, bool heavy, bool shortGun, bool ownGuns)
        {
            if (iff > 0) return Friend;
            if (iff == 0) return Identify;
            bool low = heightM < LowM || type == 3 || type == 4;
            if (low && shortGun) return Short;
            if (low) return Low;
            if (heavy) return Heavy;
            if (shortGun) return Short;
            return ownGuns ? Outside : NoGuns;
        }

        internal static bool Due(bool master, bool world, bool scene, float now, float at)
        { return master && world && scene && now >= at; }

        // Matches Mi-8 Cruise's clamp(distance * .2, 2.5, speed), plus
        // the 14 m hover descent and unload start. Terrain can add a delay.
        internal static float LandingEta(float distanceU, float speedU)
        {
            float brake = speedU * 5f, slow = 12.5f;
            double cruise = Math.Max(0f, distanceU - brake) / speedU;
            float top = Math.Min(distanceU, brake);
            if (top > slow) cruise += 5.0 * Math.Log(top / slow);
            cruise += Math.Max(0f, Math.Min(top, slow) - 0.3f) / 2.5f;
            return LandingLead + (float)cruise + 10f;
        }
    }
}

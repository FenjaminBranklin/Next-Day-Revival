// AA crew calibration in real metres. Shared by runtime and offline checks.
// No random kills: these numbers control the actual shells and timed fuze.
using System;

namespace NextDayRevival
{
    internal struct AACalibration
    {
        internal float Range, InitialMil, FloorMil, Walk, FuzeError, Reaction;
    }

    internal static class MercAACore
    {
        internal const float RadarMetres = 7000f;
        internal static float ApproachUnits(float lineLength, float worldPerMetre)
        {
            return (RadarMetres + 1000f) * worldPerMetre + Math.Max(0f, lineLength) * 0.5f;
        }
        internal static float FlightTime(float x, float y, float z, float vx, float vy, float vz, float speed, float gravity)
        {
            float t = (float)Math.Sqrt(x * x + y * y + z * z) / speed;
            for (int i = 0; i < 8; i++)
            {
                float ax = x + vx * t, ay = y + vy * t + 0.5f * gravity * t * t, az = z + vz * t;
                t = (float)Math.Sqrt(ax * ax + ay * ay + az * az) / speed;
            }
            return t;
        }
        internal static AACalibration Calibrate(bool merc, bool radar, int trait)
        {
            float skill = Math.Max(0, Math.Min(50, trait)) / 50f;
            AACalibration c = new AACalibration();
            c.Range = radar ? RadarMetres : merc ? 5000f : 1800f;
            c.InitialMil = merc ? (radar ? 24f : 30f) : (radar ? 33f : 60f);
            c.FloorMil = merc ? (radar ? 1.8f : 2.5f) : (radar ? 3f : 5f);
            c.Walk = merc ? (radar ? 0.35f : 0.4f) : (radar ? 0.45f : 0.75f);
            c.FuzeError = merc ? (radar ? 0.0115f : 0.014f) : (radar ? 0.022f : 0.03f);
            c.Reaction = radar ? 1.35f : 3f;
            if (merc)
            {
                c.InitialMil *= 1f - skill * 0.25f;
                c.FloorMil *= 1f - skill * 0.35f;
                c.Walk *= 1f - skill * 0.15f;
                c.FuzeError *= 1f - skill * 0.25f;
            }
            return c;
        }

        internal static bool Safe(float health, bool retreating, bool threat, bool danger, bool fighting)
        {
            return health >= (retreating ? 0.5f : 0.35f) && !threat && !danger && !fighting;
        }
    }
}

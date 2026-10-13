// Drone-specific acquisition and small-target accuracy. World scale is 2.8 u/m.
// Pure production policy, also compiled by the offline targeting harness.
namespace NextDayRevival
{
    internal static class DroneNpcAimCore
    {
        internal const float Metre = 2.8f;
        // FPV presents less area than the larger recon drone. Calibrated with
        // the same low-hover fixture; this is a balance factor, not armour.
        internal const float FpvArea = 0.25f;
        internal struct Contact
        {
            internal bool Heard, Located;
            internal float SearchUntil, VisibleSince, LastVisible, NextShot;
        }

        internal static float ThinkSeconds(float metres, bool heard)
        {
            if (!heard && metres > 90f) return 5f;
            return metres <= 30f ? 0.25f : 1f;
        }

        // Rotor sound is the usual cue; it still takes time to locate the sound
        // in the sky. Outside hearing range, visual discovery is rare.
        internal static bool Notice(ref Contact c, float now, float metres, float roll)
        {
            if (c.Heard) return now >= c.SearchUntil;
            float interval = ThinkSeconds(metres, false);
            float rate = metres < 90f ? 0.9f * (1f - metres / 100f) : 0.004f;
            float chance = rate * interval;
            if (chance > 0.8f) chance = 0.8f;
            if (roll >= chance) return false;
            c.Heard = true;
            c.SearchUntil = now + 2f + metres * 0.10f;
            c.VisibleSince = -1f;
            c.LastVisible = now;
            return false;
        }

        internal static bool Track(ref Contact c, float now, float metres, bool visible)
        {
            if (!visible)
            {
                c.VisibleSince = -1f;
                // Brief occlusion retains the cue, never permits a shot.
                if (now - c.LastVisible > 3f) c.Located = false;
                if (now - c.LastVisible > 12f && now > c.SearchUntil)
                { c.Heard = false; c.Located = false; }
                return false;
            }
            c.LastVisible = now;
            if (c.VisibleSince < 0f) c.VisibleSince = now;
            if (!c.Located && now - c.VisibleSince < (metres <= 30f ? 0.75f : 2f)) return false;
            c.Located = true;
            return now >= c.NextShot;
        }

        // Unknown weapons remain rifles. Shotgun pellets and MG volume help
        // close in; they do not give long-range hits against a tiny quadcopter.
        internal static float Chance(byte kind, float metres, float speedMps, float accuracy)
        {
            float ratio, chance;
            if (kind == MercWeaponReach.Shotgun)
            {
                ratio = metres / 14f;
                chance = accuracy * 0.9f / (1f + ratio * ratio * ratio * ratio);
            }
            else if (kind == MercWeaponReach.MachineGun)
            {
                ratio = metres / 20f;
                chance = accuracy * 0.65f / (1f + ratio * ratio * ratio * ratio);
            }
            else
            {
                ratio = metres / 18f;
                chance = accuracy * 0.5f / (1f + ratio * ratio);
                if (kind == MercWeaponReach.Pistol || kind == MercWeaponReach.Smg)
                    chance *= 0.75f;
            }
            chance /= 1f + speedMps * 0.65f;
            return chance < 0f ? 0f : (chance > 0.95f ? 0.95f : chance);
        }
    }
}

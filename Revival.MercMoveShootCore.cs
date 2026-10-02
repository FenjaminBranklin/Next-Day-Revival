// K3a: bounded walking fire on the K0 shared legacy rig. C# 3.0.
using System;

namespace NextDayRevival
{
    internal static class MercMoveShootPolicy
    {
        internal const float Speed = 4.2f; // 1.5 m/s, 2.8 units/m
        internal const float Fade = 0.15f, Heartbeat = 0.5f, Lease = 1.5f;

        internal static bool Eligible(bool enabled, bool move, bool safeMove, bool armed,
            bool target, bool reload, bool medicine, bool danger, bool survive, float health)
        {
            return enabled && move && safeMove && armed && target && !reload && !medicine
                && !danger && !survive && health >= 0.35f;
        }

        // Fast cover dashes/escape stay fast under pressure. Shooting bounds
        // use a nearby confirmed M1 destination, never an invented open point.
        internal static bool Bound(bool confirmed, float distance, float health, float pressure,
            int threats, bool retreat)
        {
            return confirmed && distance > 1f && distance <= 24f && health >= 0.55f
                && pressure < 0.3f && threats <= 2 && !retreat;
        }

        // The existing brain already chose this combat move. An attack bound
        // releases its old cover; a strafe has none. Neither pressure nor a
        // crowd is a reason to leave a healthy moving rifle silent.
        internal static bool Travel(bool step, bool combatRun, float distance, bool retreat)
        {
            return !retreat && (step || (combatRun && distance > 1f && distance <= 24f));
        }

        // Player directional clips: forward, back, left, right relative to aim.
        internal static int Direction(float right, float forward)
        {
            if (Math.Abs(right) > Math.Abs(forward)) return right < 0f ? 2 : 3;
            return forward < 0f ? 1 : 0;
        }

        internal static string Prefix(int weapon)
        {
            if (weapon == 1162) return null; // LAW is not walking infantry fire
            if (weapon >= 1001 && weapon <= 1099) return "asr";
            if (weapon >= 1100 && weapon <= 1199) return "rifle";
            if (weapon >= 1200 && weapon <= 1299) return "hg";
            return null;
        }

        internal static bool Packet(float[] d)
        {
            if (d == null || d.Length != 10 || d[0] != 103f || d[9] != 1f) return false;
            for (int i = 0; i < d.Length; i++)
                if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return false;
            if (!Integer(d[1], 1, 16000000) || !Integer(d[2], 1, 16000000)
                || !Integer(d[3], 0, 1299) || !Integer(d[8], 0, 16000000)
                || (d[4] != 0f && d[4] != 1f)) return false;
            if (d[4] == 1f && Prefix((int)d[3]) == null) return false;
            return Math.Abs(d[5]) <= 100000f && Math.Abs(d[6]) <= 100000f && Math.Abs(d[7]) <= 100000f;
        }
        static bool Integer(float value, int min, int max)
        { return value >= min && value <= max && value == (float)(int)value; }

        internal static bool Accept(int sender, int owner, int sequence, int previous, bool local)
        { return sender > 0 && sender == owner && !local && sequence > previous; }
    }
}

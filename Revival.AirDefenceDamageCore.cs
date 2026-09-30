// W AA7: authoritative fixed-AA health and repair rules, independent of Unity.
using System;

namespace NextDayRevival
{
    internal struct AaDamageState
    {
        internal float Hp, Work, LastPulse;
        internal int Revision, RepairActor;
    }

    internal static class AaDamageCore
    {
        internal const float Lease = 1.5f;
        internal const float GunSeconds = 45f, RadarSeconds = 60f;

        internal static AaDamageState Fresh()
        {
            AaDamageState s = new AaDamageState();
            s.Hp = 1f; s.RepairActor = -1;
            return s;
        }

        internal static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        internal static void Cancel(ref AaDamageState s)
        {
            s.RepairActor = -1; s.Work = 0f; s.LastPulse = 0f;
        }

        internal static bool Hit(ref AaDamageState s, float amount)
        {
            if (!Finite(amount) || amount <= 0f) return false;
            // A fresh blast on the wreck interrupts ongoing work as well.
            bool changed = s.Hp > 0f || s.RepairActor >= 0;
            s.Hp = Math.Max(0f, s.Hp - Math.Min(1f, amount));
            if (changed) { s.Revision++; Cancel(ref s); }
            return changed;
        }

        internal static bool Expire(ref AaDamageState s, float now, bool eligible)
        {
            if (s.RepairActor < 0 || (eligible && now - s.LastPulse <= Lease)) return false;
            Cancel(ref s);
            return true;
        }

        // The host calls this only after validating player life and proximity.
        // Client clocks/progress never contribute; host elapsed time does.
        internal static bool Pulse(ref AaDamageState s, int actor, int revision,
                                   float now, float seconds, bool eligible)
        {
            if (!Finite(now) || !Finite(seconds) || seconds <= 0f || actor < 0) return false;
            Expire(ref s, now, true);
            if (!eligible || revision != s.Revision || s.Hp >= 1f) return false;
            if (s.RepairActor >= 0 && s.RepairActor != actor) return false;
            if (s.RepairActor < 0)
            {
                s.RepairActor = actor; s.Work = 0f; s.LastPulse = now;
                return false;
            }
            s.Work += Math.Max(0f, Math.Min(Lease, now - s.LastPulse));
            s.LastPulse = now;
            if (s.Work + 0.001f < seconds) return false;
            s.Hp = 1f; s.Revision++; Cancel(ref s);
            return true;
        }

        internal static bool Snapshot(ref AaDamageState s, float hp, int revision,
                                      int actor, float work, float seconds)
        {
            if (!Finite(hp) || !Finite(work) || hp < 0f || hp > 1f
                || work < 0f || work > seconds || revision < s.Revision || actor < -1) return false;
            s.Hp = hp; s.Revision = revision; s.RepairActor = actor; s.Work = work;
            return true;
        }

        // A direct explosive hit removes the gun. Near misses fall off from
        // the gun (2 m envelope, including its height); rifle fire only kills its crew.
        internal static float Blast(float distance, float radius, float halfSize, float peak)
        {
            if (!Finite(distance) || !Finite(radius) || !Finite(peak) || peak <= 0f) return 0f;
            float d = Math.Max(0f, distance - halfSize);
            if (d >= radius || radius <= 0f) return 0f;
            return Math.Min(1f, peak * (1f - d / radius));
        }
    }
}

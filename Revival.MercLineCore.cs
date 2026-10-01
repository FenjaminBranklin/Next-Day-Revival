// Z K1a: muzzle clearance is independent of eye visibility. C# 3.0.
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercLineCache
    {
        internal const byte Unknown = 0, Clear = 1, Blocked = 2;
        internal const float Every = 0.1f;
        // Invalidate after 0.2 u of barrel or endpoint motion.
        internal const float DriftSquared = 0.04f; // 0.2 u = 7 cm
        internal float NextQuery, Expires;
        internal int Target, Weapon;
        internal Vector3 From, To;
        internal byte Result;

        internal byte Read(int target, int weapon, Vector3 from, Vector3 to, float now)
        {
            if (now >= Expires || Target != target || Weapon != weapon
                || (from - From).sqrMagnitude > DriftSquared
                || (to - To).sqrMagnitude > DriftSquared) return Unknown;
            return Result;
        }

        internal void Store(int target, int weapon, Vector3 from, Vector3 to, float now, bool clear)
        {
            Target = target; Weapon = weapon; From = from; To = to;
            NextQuery = Expires = now + Every;
            Result = clear ? Clear : Blocked;
        }
    }

    internal sealed class MercLineBudget
    {
        int _frame = -1;
        // One ray + origin overlap per frame across all mercs, also in bursts.
        internal bool Take(int frame)
        {
            if (_frame == frame) return false;
            _frame = frame;
            return true;
        }
    }
}

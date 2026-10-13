// Next Day: Survival - Revival Toolkit
//
// C4 demolition charge - the pure rules (no Unity, no game types), shared by
// the runtime (Revival.C4.cs) and the headless test (tests/c4_core_test.cs via
// research/c4_core_check.py, csc 3.5). Distances are world units (2.8 per
// metre). Nothing here allocates: the chain walk works in caller-owned arrays.
//
// C# 3.0, ASCII.

using System;

namespace NextDayRevival
{
    internal static class C4Core
    {
        internal const float K = 2.8f;

        /// <summary>Timer steps the mouse wheel walks through while a charge is
        /// in the hand. Step 0 is "remote only" (no timer); the rest are 10 s to
        /// 5 min.</summary>
        internal static readonly int[] TimerSteps = new int[] { 0, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300 };
        internal const float MinTimer = 10f, MaxTimer = 300f;

        internal static int StepTimer(int index, int delta)
        {
            int i = index + delta;
            if (i < 0) i = 0;
            if (i >= TimerSteps.Length) i = TimerSteps.Length - 1;
            return i;
        }

        /// <summary>0 (or less) = remote only; anything else is held inside
        /// 10 s .. 5 min, whatever a packet claims.</summary>
        internal static float ClampTimer(float seconds)
        {
            if (!Finite(seconds) || seconds <= 0f) return 0f;
            return Math.Max(MinTimer, Math.Min(MaxTimer, seconds));
        }

        // ------------------------------------------------------------ limit
        internal static int CountOwn(int[] owners, int count, int owner)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if (owners[i] == owner) n++;
            return n;
        }

        /// <summary>One more charge allowed for a player who already has
        /// <paramref name="own"/> placed. The limit is never below one.</summary>
        internal static bool MayPlace(int own, int max)
        {
            return own < Math.Max(1, max);
        }

        // ------------------------------------------------------- beep / LED
        /// <summary>Seconds between two beeps (and LED flashes) of a timed
        /// charge: 2 s while a minute or more is left, then faster in step
        /// with the time left, down to 0.12 s in the last seconds.</summary>
        internal static float BeepInterval(float remaining)
        {
            if (!Finite(remaining)) return 2f;
            return Math.Max(0.12f, Math.Min(2f, remaining / 30f));
        }

        /// <summary>A remote-only charge blinks slowly and never beeps.</summary>
        internal const float RemoteBlink = 2.5f;
        internal const float FlashSeconds = 0.08f;

        // ------------------------------------------------------------ chain
        /// <summary>
        /// Every charge the blast of <paramref name="start"/> sets off, in the
        /// order the shock wave reaches them (breadth first: a charge goes off
        /// when ANY charge that went off before it lies within the radius).
        /// xyz holds count positions (x, y, z), live says which are still
        /// placed, seen and order are caller-owned scratch of at least count
        /// entries. Returns the number of entries written to order; order[0]
        /// is start itself. A charge that is not live is never reached and
        /// never relays.
        /// </summary>
        internal static int Chain(float[] xyz, bool[] live, int count, int start, float radius,
                                  bool[] seen, int[] order)
        {
            if (xyz == null || live == null || seen == null || order == null) return 0;
            if (start < 0 || start >= count || !live[start]) return 0;
            float r2 = Finite(radius) && radius > 0f ? radius * radius : 0f;
            for (int i = 0; i < count; i++) seen[i] = false;
            int head = 0, tail = 0;
            order[tail++] = start;
            seen[start] = true;
            while (head < tail)
            {
                int a = order[head++];
                float ax = xyz[a * 3], ay = xyz[a * 3 + 1], az = xyz[a * 3 + 2];
                for (int b = 0; b < count; b++)
                {
                    if (seen[b] || !live[b]) continue;
                    float dx = xyz[b * 3] - ax, dy = xyz[b * 3 + 1] - ay, dz = xyz[b * 3 + 2] - az;
                    if (dx * dx + dy * dy + dz * dz > r2) continue;
                    seen[b] = true;
                    order[tail++] = b;
                }
            }
            return tail;
        }

        /// <summary>Radio reach of the detonator, measured in 3D.</summary>
        internal static bool InRange(float dx, float dy, float dz, float range)
        {
            if (!Finite(range) || range <= 0f) return false;
            return dx * dx + dy * dy + dz * dz <= range * range;
        }

        // ------------------------------------------------- object damage
        /// <summary>Share of one charge an object takes: 1 when the charge sits
        /// on it or within <paramref name="full"/>, linear to 0 at
        /// <paramref name="zero"/>. Charges add up on the object.</summary>
        internal static float ChargeShare(float distance, float full, float zero)
        {
            if (!Finite(distance)) return 0f;
            if (distance <= full) return 1f;
            if (distance >= zero || zero <= full) return 0f;
            return 1f - (distance - full) / (zero - full);
        }

        /// <summary>The object is destroyed once the shares add up to the
        /// charges it takes (a small tolerance absorbs float sums).</summary>
        internal static bool Destroyed(float shares, float needed)
        {
            return Finite(shares) && shares >= Math.Max(0.01f, needed) - 0.001f;
        }

        // ---------------------------------------------------------- defuse
        /// <summary>Seconds a defuse takes; the Mechanic buff (when the merc
        /// buff system reports it) scales it by <paramref name="mechanicFactor"/>.
        /// Never under one second.</summary>
        internal static float DefuseSeconds(float baseSeconds, bool mechanic, float mechanicFactor)
        {
            float s = Finite(baseSeconds) ? baseSeconds : 6f;
            if (mechanic && Finite(mechanicFactor) && mechanicFactor > 0f) s *= Math.Min(1f, mechanicFactor);
            return Math.Max(1f, s);
        }

        internal static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    }
}

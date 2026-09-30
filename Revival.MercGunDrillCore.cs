// X merc technical gunner: the fire discipline of a merc on a mounted gun
// (Revival.MercsRide.cs, MercRide.Gun / Shoot). Pure logic, no Unity: the
// offline check research/merc_gunner_check.py compiles this file unchanged.
//
//   BURSTS    full-auto at the gun's own cadence (the player's RateOfFire),
//             BurstMin..BurstMax rounds, then a pause of PauseMin..PauseMax.
//             The schedule keeps the cadence across frames (the next round
//             is due one interval after the last one was DUE, not after the
//             frame that fired it), so 600 rpm stays 600 rpm at 45 fps.
//   LAID      a burst STARTS only with the bore within StartTol of the
//             target and CONTINUES while it stays within HoldTol - a gunner
//             walks a burst onto a moving man instead of stopping after
//             every round the target drifts a degree.
//   BELT      Belt rounds, then Reload seconds with no fire. A merc with no
//             target for TopUpQuiet seconds and less than half a belt left
//             loads a fresh one before the next contact (survival first:
//             never run dry in the next fight).
//   TARGET    a new target or a lost lay ends the burst with a short
//             switch pause; nothing here decides WHO is shot.
//
// Allocation free: one instance per MercSeat, reused across boardings.
using System;

namespace NextDayRevival
{
    internal sealed class MercGunDrill
    {
        internal const float TopUpQuiet = 2.5f;

        // profile (Configure)
        internal float Interval = 0.1f, PauseMin = 0.8f, PauseMax = 1.4f, Reload = 4.5f;
        internal float StartTol = 3.5f, HoldTol = 7f;
        internal int BurstMin = 4, BurstMax = 8, Belt = 50;

        // state
        internal int Loaded = -1;             // -1: a full belt at the first round
        internal int Burst, BurstLen;
        internal float NextShot, ReloadUntil;
        internal bool Reloading;
        internal int Rounds, Bursts, Reloads;

        /// <summary>The gun's numbers. A changed belt size refills.</summary>
        internal void Configure(float interval, int burstMin, int burstMax, float pause,
            int belt, float reload, float laid)
        {
            Interval = Math.Max(0.02f, interval);
            BurstMin = Math.Max(1, burstMin);
            BurstMax = Math.Max(BurstMin, burstMax);
            PauseMin = Math.Max(Interval, pause * 0.75f);
            PauseMax = Math.Max(PauseMin, pause * 1.3f);
            belt = Math.Max(1, belt);
            if (belt != Belt) { Belt = belt; Loaded = -1; }
            Reload = Math.Max(0f, reload);
            StartTol = Math.Max(0.1f, laid);
            HoldTol = BurstMax > 1 ? StartTol * 2f : StartTol;
        }

        /// <summary>A new seat: a full belt, no burst, no reload.</summary>
        internal void Reset()
        {
            Loaded = -1; Burst = 0; BurstLen = 0;
            NextShot = 0f; ReloadUntil = 0f; Reloading = false;
        }

        internal bool InBurst { get { return Burst > 0 && !Reloading; } }

        internal int Left { get { return Loaded < 0 ? Belt : Loaded; } }

        /// <summary>Is the bore close enough: to open a burst, or to go on with one.</summary>
        internal bool Laid(float angle)
        {
            return angle <= (Burst > 0 ? HoldTol : StartTol);
        }

        /// <summary>May a round go now? Finishes a reload that has run out.</summary>
        internal bool Ready(float now)
        {
            if (Reloading)
            {
                if (now < ReloadUntil) return false;
                Reloading = false;
                Loaded = Belt;
            }
            return now >= NextShot;
        }

        /// <summary>One round fired. r01 in [0,1): the burst's length at its
        /// first round, the pause at its last.</summary>
        internal void Fire(float now, float r01)
        {
            if (Loaded < 0) Loaded = Belt;
            if (r01 < 0f) r01 = 0f;
            if (r01 >= 1f) r01 = 0.9999f;
            if (Burst == 0)
            {
                BurstLen = BurstMin + (int)(r01 * (BurstMax - BurstMin + 1));
                if (BurstLen > BurstMax) BurstLen = BurstMax;
                Bursts++;
            }
            Burst++;
            Loaded--;
            Rounds++;
            if (Loaded <= 0)
            {
                Burst = 0;
                StartReload(now);
                return;
            }
            if (Burst >= BurstLen)
            {
                Burst = 0;
                NextShot = now + PauseMin + (PauseMax - PauseMin) * r01;
                return;
            }
            // Keep the cadence: due one interval after this round was due.
            float due = NextShot > now - Interval ? NextShot : now;
            NextShot = due + Interval;
        }

        /// <summary>The target changed or the lay was lost mid-burst.</summary>
        internal void BreakOff(float now)
        {
            if (Burst == 0) return;
            Burst = 0;
            float soon = now + PauseMin * 0.5f;
            if (NextShot < soon) NextShot = soon;
        }

        /// <summary>No target for <paramref name="quiet"/> seconds: load a
        /// fresh belt if less than half is left. True when a reload started.</summary>
        internal bool Idle(float now, float quiet)
        {
            if (Reloading || Belt <= 1 || quiet < TopUpQuiet) return false;
            if (Left * 2 >= Belt) return false;
            Burst = 0;
            StartReload(now);
            return true;
        }

        void StartReload(float now)
        {
            Loaded = 0;
            Reloading = true;
            ReloadUntil = now + Reload;
            if (NextShot < ReloadUntil) NextShot = ReloadUntil;
            Reloads++;
        }
    }
}

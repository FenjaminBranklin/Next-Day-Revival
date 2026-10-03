// W AA4: a kill must matter. The arithmetic of aircraft damage, the bomber's
// release decision, the gun pit (revetment) rule and the bounty - no Unity
// types, so research/air_kills_check.py compiles this file unchanged.
using System;

namespace NextDayRevival
{
    internal enum ReleaseKind { Normal = 0, Wide = 1, Abort = 2 }

    /// <summary>What a bomber does at its release point.</summary>
    internal struct ReleasePlan
    {
        internal ReleaseKind Kind;
        /// <summary>Multiplier on the stick's own few metres of dispersion.</summary>
        internal float Scatter;
        /// <summary>The whole stick's miss, real metres along / across the track.</summary>
        internal float AlongM, AcrossM;
    }

    /// <summary>
    /// One aircraft's hit points (1 = whole, 0 = down) and who took them.
    /// Every weapon removes a fraction: a rifle round 1/(30 x toughness), a
    /// gun hit 1/(the hits that gun needs), a blast on the airframe all of it.
    /// The master alone keeps it, so damage from different guns and clients
    /// adds up and persists for the whole flight.
    /// </summary>
    internal sealed class DamageLedger
    {
        internal const int Slots = 4;
        internal readonly int[] Actor = new int[Slots];
        internal readonly float[] Share = new float[Slots];
        internal float Damage;
        /// <summary>The last hit's Photon actor; 0 = an NPC crew or nobody.</summary>
        internal int Last;

        internal bool Down { get { return Damage >= AirKillCore.DownAt; } }

        /// <summary>Adds a hit; true when this hit brought it down.</summary>
        internal bool Add(float amount, int actor)
        {
            if (Down || amount <= 0f) return false;
            float before = Damage;
            Damage = Math.Min(1f, Damage + amount);
            Last = actor > 0 ? actor : 0;
            if (actor > 0)
            {
                int free = -1, weakest = 0;
                for (int i = 0; i < Slots; i++)
                {
                    if (Actor[i] == actor) { Share[i] += Damage - before; return Down; }
                    if (Actor[i] <= 0 && free < 0) free = i;
                    if (Share[i] < Share[weakest]) weakest = i;
                }
                int slot = free >= 0 ? free : weakest;
                Actor[slot] = actor;
                Share[slot] = Damage - before;
            }
            return Down;
        }

        /// <summary>Only the owner of the finishing weapon is paid.
        /// A garrison's final hit pays nobody, regardless of earlier assists.</summary>
        internal int Winner()
        {
            return Last;
        }
    }

    internal static class AirKillCore
    {
        internal const float DownAt = 0.999f;
        /// <summary>Rifle and MG rounds that bring an An-2 down.</summary>
        internal const int SmallArmsHits = 30;
        internal const float AssistShare = 0.25f;

        /// <summary>Snapshot the shooting gun's owner at launch. Explicit zero
        /// means a garrison; never substitute the local player for it.</summary>
        internal static int ShotCredit(int assigned, bool npc, int localActor)
        {
            return assigned >= 0 ? assigned : npc ? 0 : Math.Max(0, localActor);
        }

        /// <summary>From this much damage a bomber's stick goes wide.</summary>
        internal const float WideFrom = 0.15f;
        /// <summary>From this much accumulated airframe damage a bomber may
        /// abort a non-coordinated run.</summary>
        internal const float AbortFrom = 0.45f;

        // The AA ring (east_af_shelters "AA position"): sandbags 6.3 m inside,
        // 10.3 m outside. A man inside the ring is hurt by a blast only when
        // the bomb lands in the pit (the inner radius plus the inner half of
        // the bag wall); a carpet outside it, however close, spares him.
        internal const float PitHitRadiusM = 4.0f;
        internal const float PitWallRadiusM = 5.3f;
        internal const float PitDepthM = 3.0f;

        internal static float RoundDamage(int toughness)
        {
            return 1f / (SmallArmsHits * Math.Max(1, toughness));
        }

        internal static float GunHitDamage(int hitsNeeded)
        {
            return 1f / Math.Max(1, hitsNeeded);
        }

        /// <summary>0 whole, 1 damaged (smoking, drops wide), 2 badly damaged.</summary>
        internal static int Level(float damage)
        {
            return damage >= AbortFrom ? 2 : damage >= WideFrom ? 1 : 0;
        }

        /// <summary>
        /// The release decision, on the master, once, at the release point.
        /// <paramref name="roll"/> and <paramref name="angle"/> are uniform
        /// 0..1. Whole: the chosen line. Damaged: the crew fights the
        /// aircraft, the stick scatters and misses by up to ~250 m in a
        /// random direction (never toward anything in particular). Badly
        /// damaged: mostly aborts - no bomb falls on the target.
        /// </summary>
        internal static ReleasePlan Plan(float damage, float roll, float angle)
        {
            ReleasePlan p = new ReleasePlan();
            p.Scatter = 1f;
            if (damage >= DownAt) { p.Kind = ReleaseKind.Abort; return p; }
            if (damage < WideFrom) { p.Kind = ReleaseKind.Normal; return p; }
            if (damage >= AbortFrom)
            {
                float abort = Math.Min(1f, 0.55f + (damage - AbortFrom) * 1.5f);
                if (roll < abort) { p.Kind = ReleaseKind.Abort; return p; }
            }
            p.Kind = ReleaseKind.Wide;
            p.Scatter = 1f + 8f * damage;
            float miss = Math.Min(250f, 40f + 260f * damage);
            double a = angle * Math.PI * 2.0;
            // A damaged bomber mostly misses along its track (late or early
            // release), less across it.
            p.AlongM = (float)(Math.Cos(a) * miss * 1.3);
            p.AcrossM = (float)(Math.Sin(a) * miss * 0.7);
            return p;
        }

        /// <summary>
        /// A man at (<paramref name="mx"/>, <paramref name="my"/>, <paramref name="mz"/>)
        /// and a blast at (<paramref name="bx"/>, <paramref name="bz"/>), both
        /// relative to the pit centre, in world units: true when the ring
        /// shelters him (he is inside it, and the bomb is not in the pit).
        /// </summary>
        internal static bool Sheltered(float mx, float my, float mz, float bx, float bz, float worldPerMetre)
        {
            float wall = PitWallRadiusM * worldPerMetre, depth = PitDepthM * worldPerMetre;
            if (mx * mx + mz * mz > wall * wall || Math.Abs(my) > depth) return false;
            float pit = PitHitRadiusM * worldPerMetre;
            return bx * bx + bz * bz > pit * pit;
        }

        /// <summary>Bounty per kind (0 Tu-95, 1 An-2 transport, 2 troop
        /// Mi-8; anything else - the admin's test flyover - pays nothing).</summary>
        internal static int Bounty(int kind, int tu95, int an2, int mi8)
        {
            int v = kind == 0 ? tu95 : kind == 1 ? an2 : kind == 2 ? mi8 : 0;
            return Math.Max(0, Math.Min(10000000, v));
        }
    }
}

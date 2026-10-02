// merc-combat-response - the pure part (docs/ai/tasks/merc-combat-response.md).
// It knows Vector3 and Mathf and nothing of the game, so
// research/merc_combat_response_check.py compiles this file UNCHANGED next to
// the M1/M2/M3 cores and runs the adapter's decisions through it.
//
//   GATE      MercFireGate: the one decision the adapter takes every frame a
//             merc is up (FightAct.Fire) - a real round, covering fire, or
//             hold, and why. The friendly check (the owner and every other
//             merc near the line, MercFriendInLine) is asked for right
//             before, every frame; NpcWar.Shoot asks again for every round.
//   REACH     MercWeaponReach: one effective engagement table, by weapon.
//             Acquisition may look further; every round obeys Allows unless
//             he is returning incoming fire. Unknown ids count as a rifle.
//   THREAT    MercThreat.SightUnits: a threat further than this does not
//             count as seeing him (M1 exposure) unless it hits him - a
//             distant enemy with a geometric line to him is no reason to
//             leave a firing position.
//   AIM       MercAimHold: the owner's held aim. A look that stayed within
//             HoldTurn for HoldSeconds is his line of fire, AimLength long.
//   TRACE     MercReaction: per merc, timestamped contact -> decision ->
//             first real round, and what held the round when it was late.
//
// No allocation after construction. Units: game units (~2.8 per metre),
// seconds. C# 3.0, ASCII only.
using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>The adapter's per-frame fire decision for a merc who is up.</summary>
    internal static class MercFireGate
    {
        internal const byte Shoot = 1, Suppress = 2, HoldNoTarget = 3, HoldUnseen = 4, HoldBrain = 5,
            HoldSurvive = 6, HoldFriend = 7, HoldRange = 8;
        static readonly string[] Names = { "-", "SHOOT", "SUPPRESS", "NO TARGET", "UNSEEN", "BRAIN", "SURVIVE", "FRIEND", "RANGE" };

        internal static string Name(byte g) { return g < Names.Length ? Names[g] : "?"; }

        /// <summary>Is the live friendly check needed for this decision? (It
        /// is arithmetic over the owner and the mercs; skipped when the round
        /// is held anyway.)</summary>
        internal static bool NeedFriendCheck(bool target, bool noShot, bool survive, bool survivalFire)
        {
            return target && !noShot && !(survive && !survivalFire);
        }

        /// <summary>target: he has a live target; sees: a line of fire to it
        /// (NpcWar.AimPoint); noShot: the brain holds (a friend in its view,
        /// an aimed pause); survive / survivalFire: the owner-death shelter
        /// and its contact permission; friendInLine: the live check;
        /// suppress: the brain asks for covering fire at the last spot.</summary>
        internal static byte Decide(bool target, bool sees, bool noShot, bool survive, bool survivalFire,
            bool friendInLine, bool suppress)
        {
            bool hold = noShot || (survive && !survivalFire) || (target && friendInLine);
            if (target && sees && !hold) return Shoot;
            if (!survive && suppress && !hold) return Suppress;
            if (noShot) return HoldBrain;
            if (survive && !survivalFire) return HoldSurvive;
            if (target && friendInLine) return HoldFriend;
            return target ? HoldUnseen : HoldNoTarget;
        }
    }

    /// <summary>Effective engagement range; sight alone never extends it.</summary>
    internal static class MercWeaponReach
    {
        internal const float Metre = 2.8f;
        internal const byte Pistol = 1, Shotgun = 2, Rifle = 3, MachineGun = 4, Marksman = 5, Smg = 6;
        static readonly string[] Names = { "-", "pistol", "shotgun", "rifle", "machine gun", "marksman", "SMG" };
        // Metres, indexed by kind. Index zero and unknown kinds use Rifle.
        static readonly float[] EngagementMetres = { 180f, 45f, 35f, 180f, 220f, 350f, 75f };

        internal static string Name(byte kind) { return kind < Names.Length ? Names[kind] : "?"; }

        /// <summary>The weapon kind of an item id (research/items.tsv, the
        /// default merc profiles: 1201-1205 pistols, 1151-1154 shotguns,
        /// 1010 SVD / 1161 TAC-50 and the other 5..10 round rifles
        /// marksman, RPK / MG42 and the 75..100 round guns machine guns).</summary>
        internal static byte Kind(int item)
        {
            if (item >= 1201 && item <= 1205) return Pistol;
            if (item >= 1151 && item <= 1154) return Shotgun;
            switch (item)
            {
                case 1003: return Smg; // PP 1901 (catalogue)
                case 1008: case 1013: case 1021: return Shotgun; // Vepr/Saiga/USAS-12
                case 1009: case 1010: case 1015: case 1025: case 1027: case 1161: return Marksman;
                case 1016: case 1018: case 1023: case 1160: return MachineGun;
            }
            return Rifle;
        }

        /// <summary>Metres in the single effective-range table.</summary>
        internal static float Metres(byte kind)
        {
            return EngagementMetres[kind > 0 && kind < EngagementMetres.Length ? kind : Rifle];
        }

        internal static float EffectiveUnits(int item) { return Metres(Kind(item)) * Metre; }

        /// <summary>Three-dimensional target-body distance, before aim scatter.
        /// Return fire keeps all other sight/muzzle/friend/ammo gates.</summary>
        internal static bool Allows(int item, Vector3 me, Vector3 target, bool defending)
        {
            float range = EffectiveUnits(item);
            return defending || (target - me).sqrMagnitude <= range * range;
        }

        /// <summary>Acquisition only: look at least as far as the squad. This
        /// floor is deliberately never applied to the effective fire range.</summary>
        internal static float Units(int item, float floor)
        {
            return Mathf.Max(floor, EffectiveUnits(item));
        }
    }

    /// <summary>How far a threat is taken to see him (M1 exposure).</summary>
    internal static class MercThreat
    {
        internal const float SightUnits = 200f;   // 71 m: past the NpcWar AssaultRange (180) and stock sight (110)

        internal static bool InSight(Vector3 me, Vector3 threat)
        {
            float dx = threat.x - me.x, dz = threat.z - me.z;
            return dx * dx + dz * dz <= SightUnits * SightUnits;
        }
    }

    /// <summary>The owner's held aim: his look stayed within HoldTurn for
    /// HoldSeconds (a look round is no line of fire).</summary>
    internal sealed class MercAimHold
    {
        internal const float HoldSeconds = 0.35f;
        internal const float HoldTurn = 0.99f;    // cos 8 degrees
        internal const float AimLength = 200f;    // 71 m of his line of fire
        internal const float MaxPitch = 0.8f;     // looking at the ground or the sky: no line
        Vector3 _dir;
        float _since = -1000f;

        /// <summary>One look this frame (eye, forward). True: he holds his
        /// aim; from/to is the flat line of fire.</summary>
        internal bool Update(Vector3 eye, Vector3 forward, float now, out Vector3 from, out Vector3 to)
        {
            from = eye; to = eye;
            float fx = forward.x, fz = forward.z;
            float flat = Mathf.Sqrt(fx * fx + fz * fz);
            if (flat < 0.01f || Mathf.Abs(forward.y) > MaxPitch) { _since = now; _dir = Vector3.zero; return false; }
            fx /= flat; fz /= flat;
            if (_dir.x * fx + _dir.z * fz < HoldTurn) { _since = now; _dir = new Vector3(fx, 0f, fz); }
            if (now - _since < HoldSeconds) return false;
            to = new Vector3(eye.x + fx * AimLength, eye.y, eye.z + fz * AimLength);
            return true;
        }

        internal void Reset() { _since = -1000f; _dir = Vector3.zero; }
    }

    /// <summary>One merc's reaction trace: contact (a target in sight after
    /// none) -> the brain's first fire decision -> the first real round, and
    /// what held a late round (Why flags).</summary>
    internal sealed class MercReaction
    {
        internal const float Prompt = 1f;          // the acceptance limit, seconds
        internal const int Moving = 1, Reload = 2, Friend = 4, Muzzle = 8, Planting = 16, Brain = 32, Unseen = 64;
        internal float ContactAt = -1f, DecideAt = -1f, ShotAt = -1f, ContactUnits;
        internal int Why;
        // Totals (F8, the offline check).
        internal int Contacts, Answered, Late, LateMoving, LateReload, LateFriend, LateMuzzle, LateBrain, Dropped;
        internal float Sum, Max, DecideSum;
        internal float Last = -1f;                 // the last contact -> round, seconds

        internal bool Open { get { return ContactAt >= 0f && ShotAt < 0f; } }

        /// <summary>A target in sight now (units away); starts a contact
        /// when none is running (Lost / Quiet end one).</summary>
        internal void Sight(float now, float units)
        {
            if (ContactAt >= 0f) return;
            ContactAt = now; DecideAt = -1f; ShotAt = -1f; Why = 0; ContactUnits = units;
            Contacts++;
        }

        /// <summary>The brain chose to fire (FightAct.Fire, no hold).</summary>
        internal void Decided(float now)
        {
            if (ContactAt >= 0f && DecideAt < 0f && ShotAt < 0f) DecideAt = now;
        }

        /// <summary>What held the round this frame (Why flags).</summary>
        internal void Held(int why) { if (ContactAt >= 0f && ShotAt < 0f) Why |= why; }

        /// <summary>A real round left (NpcWar.Shoot, or the game's own shot
        /// at a player target).</summary>
        internal void Fired(float now)
        {
            if (ContactAt < 0f || ShotAt >= 0f) return;
            ShotAt = now;
            float took = now - ContactAt;
            Last = took;
            Answered++;
            Sum += took;
            if (took > Max) Max = took;
            if (DecideAt >= 0f) DecideSum += DecideAt - ContactAt;
            if (took <= Prompt) return;
            Late++;
            if ((Why & Moving) != 0) LateMoving++;
            if ((Why & Reload) != 0) LateReload++;
            if ((Why & Friend) != 0) LateFriend++;
            if ((Why & Muzzle) != 0) LateMuzzle++;
            if ((Why & Brain) != 0) LateBrain++;
        }

        /// <summary>The target is gone before a round: the contact is closed
        /// unanswered (it is no reaction to measure).</summary>
        internal void Lost()
        {
            if (ContactAt >= 0f && ShotAt < 0f) Dropped++;
            ContactAt = -1f; DecideAt = -1f; ShotAt = -1f; Why = 0;
        }

        /// <summary>The fight is over: the next sight is a new contact.</summary>
        internal void Quiet() { ContactAt = -1f; DecideAt = -1f; ShotAt = -1f; Why = 0; }
    }
}

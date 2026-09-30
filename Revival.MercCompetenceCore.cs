// x-merc-competence - the pure part (docs/ai/tasks/x-merc-competence.md).
// It knows Mathf and nothing of the game, so research/merc_competence_check.py
// compiles this file UNCHANGED next to the M1/M2/M3 cores and plays the fight
// with the numbers the game adapter takes from here.
//
//   STATS     MercCompetence: what a merc's pay buys, by tier / grade. His
//             paid armour takes a share off every hit (health x2.5 at tier 0 up
//             to x3 at tier 3: NpcWar sizes a defender round from the victim's
//             own HealthMax, so more health alone would change nothing - the
//             share comes off the damage in Mercs.DamagePrefix), a steadier
//             shot, less of the range falloff, a quicker aim at players, a
//             steadier nerve, and seen in the open the NEAR cover first (Rush).
//   HEAL      MercSelfHeal: out of a fight and unhurt for HealAfter seconds
//             he patches himself up, HealStep every HealEvery seconds - the
//             server's own cap (+0.1 health a 15 s roster report), so the
//             server keeps up and a relog is no heal button.
//   REPORT    MercDeathNote: the death report line (BepInEx log) and the
//             owner's toast - killer, weapon, distance, cover, last order,
//             brain state - from the snapshot of the killing hit.
//
// No allocation outside the death report (one string per death). Units:
// game units (~2.8 per metre), seconds. C# 3.0, ASCII only.
using System;
using System.Globalization;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>A merc's paid-for edge over a stock NPC, by tier / grade.</summary>
    internal static class MercCompetence
    {
        /// <summary>Health multiple his armour buys: x2.5 at tier 0, x2.67,
        /// x2.83, x3 at tier 3 (the win-rate simulation: x2 left the
        /// cheapest tier below 70 % against 8).</summary>
        internal static float HealthScale(float grade) { return 2.5f + MercGrade.Tier(grade) / 6f; }

        /// <summary>The share of every hit that reaches him.</summary>
        internal static float DamageScale(float grade) { return 1f / HealthScale(grade); }

        /// <summary>His NPC-versus-NPC shot (NpcWar Fighter.Skill), on top of
        /// the precise trait: x1.3 at grade 0 .. x1.6 at grade 1.</summary>
        internal static float SkillScale(float grade) { return MercGrade.Lerp(grade, 1.3f, 1.6f); }

        /// <summary>The share of the range falloff he suffers (Fighter.RangeFalloff):
        /// 70 % at grade 0 .. 30 % at grade 1 (M3 had 100 % .. 40 %).</summary>
        internal static float RangeFalloff(float grade) { return MercGrade.Lerp(grade, 0.7f, 0.3f); }

        /// <summary>The vanilla aiming delay at players (NPCSpecifications.AimingDelay):
        /// x0.8 at grade 0 .. x0.55 at grade 1, on top of the precise trait.</summary>
        internal static float AimDelayScale(float grade) { return MercGrade.Lerp(grade, 0.8f, 0.55f); }

        /// <summary>His nerve under fire (NpcWar Fighter.Nerve, stock 0.65..1.45):
        /// at least 1.3 at grade 0 .. 1.8 at grade 1 - rounds past him shake
        /// him less (suppression 0.34 / Nerve a round).</summary>
        internal static float Nerve(float grade) { return MercGrade.Lerp(grade, 1.3f, 1.8f); }

        /// <summary>Seen in the open (no cover held, no planned anchor search):
        /// the M1 search reaches only RushRadius, so the NEAR cover wins - the
        /// travel penalty is scaled by the radius. Nothing there: the next
        /// query (RushRetry s later) takes the full radius.</summary>
        internal const float RushRadius = 24f, RushRetry = 0.25f;

        internal static bool Rush(bool exposed, bool holding, bool anchored, bool missed)
        {
            return exposed && !holding && !anchored && !missed;
        }
    }

    /// <summary>Halt cover: under FOLLOW, once the owner has stood still for
    /// After seconds, a merc takes the M1 cover point nearest his wedge slot
    /// that hides him from the owner's front (Approach units out) and holds
    /// it crouched, instead of standing in the open at the slot. The owner
    /// moves Moved units or turns away: back to the slot. One M1 query per
    /// halt (Retry s apart while none is found).</summary>
    internal sealed class MercHalt
    {
        internal const float After = 2f;        // s the owner stands still first
        internal const float Moved = 6f;        // units the owner moves: follow again
        internal const float Turn = 0.7f;       // cos of the owner's turn (about 45 degrees): look again
        internal const float Leash = 28f;       // the cover lies within this of his slot (10 m)
        internal const float Approach = 280f;   // the watched approach: this far along the owner's front (100 m)
        internal const float Arrive = 1.2f;     // at the point
        internal const float Retry = 4f;        // s between two queries that found nothing

        internal Vector3 OwnerAt, Front;
        internal float StillSince = -1f, NextQuery, NextClaim;
        internal bool Has;
        internal CoverPick Pick;

        /// <summary>The FOLLOW wedge: two per rank behind the owner, the fifth at the tail.</summary>
        internal static Vector3 Slot(Vector3 owner, Vector3 fwd, int slot)
        {
            Vector3 side = new Vector3(fwd.z, 0f, -fwd.x);
            int rank = slot / 2;
            float lateral = slot >= 4 ? 0f : (slot % 2 == 0 ? -1f : 1f) * (7f + 5f * rank);
            return owner - fwd * (12f + 8f * rank) + side * lateral;
        }

        internal static Vector3 Watch(Vector3 owner, Vector3 fwd) { return owner + fwd * Approach; }

        /// <summary>Has the owner stood still (and faced the same way) for
        /// After s? A move or a turn restarts the clock and drops the cover.</summary>
        internal bool Still(Vector3 owner, Vector3 fwd, float now)
        {
            float dx = owner.x - OwnerAt.x, dz = owner.z - OwnerAt.z;
            bool moved = StillSince < 0f || dx * dx + dz * dz > Moved * Moved
                || fwd.x * Front.x + fwd.z * Front.z < Turn;
            if (moved)
            {
                OwnerAt = owner; Front = fwd; StillSince = now;
                Drop();
                return false;
            }
            return now - StillSince >= After;
        }

        internal void Take(CoverPick pick) { Pick = pick; Has = true; NextClaim = 0f; }

        internal void Drop() { Has = false; Pick = new CoverPick(); NextQuery = 0f; }
    }

    /// <summary>Out-of-combat self-heal pacing.</summary>
    internal static class MercSelfHeal
    {
        internal const float HealAfter = 10f;   // s without a hit and out of the fight
        internal const float HealEvery = 3f;    // one step this often
        internal const float HealStep = 0.02f;  // share of full health a step (0.1 in 15 s: the server's cap)

        /// <summary>The health share after this step, or the same value when
        /// no step is due (in a fight, hit lately, full, dead).</summary>
        internal static float Step(float health, bool fighting, float sinceHit)
        {
            if (fighting || health <= 0f || health >= 0.999f || sinceHit < HealAfter) return health;
            return Mathf.Min(1f, health + HealStep);
        }
    }

    /// <summary>What the killing hit looked like, kept per merc on every hit
    /// (a struct: no allocation in the damage prefix).</summary>
    internal struct MercHitNote
    {
        public float At;             // game time of the hit
        public float Damage;         // damage after his armour share
        public float Health;         // health share left after it (estimate)
        public int Type;             // NPC_AI2 damageType
        public int Attacker;         // damageOwnerId: a player's actor, 0 for plugin / NPC rounds
        public float Distance;       // units to the killer candidate (-1 unknown)
        public bool InCover;         // down at his cover point
        public bool Exposed;         // a threat saw him where he stood (M1 sense)
        public bool Up;              // up at a peek / firing / on the move up
        public byte State, Mode;     // MercBrain state and mode
        public int Threats;          // threats sensed
        public int HitsInFight;      // hits taken in this fight so far
    }

    /// <summary>The death report: the log line and the owner's toast.</summary>
    internal static class MercDeathNote
    {
        internal const float Metre = 2.8f;

        /// <summary>NPC_AI2 damage types (RE 35). Type 0 with attacker 0 is a
        /// plugin round (NpcWar squads and defenders, turrets): Turret.TryDamage
        /// and RemoteHit send damageType 0.</summary>
        internal static string DamageName(int type, int attacker)
        {
            if (type == 0 && attacker <= 0) return "NPC round";
            switch (type)
            {
                case 0: case 1: case 2: case 3: case 8: return "melee";
                case 4: return "firearm";
                case 5: return "fall";
                case 9: return "fire";
                case 10: return "animal";
                case 11: return "vehicle";
                case 12: return "gas";
                case 13: return "crushed";
                case 14: return "explosion";
                case 17: return "NPC melee";
                case 18: return "NPC firearm";
                case 19: return "bleeding";
            }
            return "damage type " + type;
        }

        /// <summary>A weapon item id as a name (the default profiles and
        /// research/items.tsv); unknown ids by kind.</summary>
        internal static string WeaponName(int item)
        {
            switch (item)
            {
                case 1002: return "AKM";
                case 1006: return "AKS-74U";
                case 1007: return "AK";
                case 1010: return "SVD";
                case 1011: return "AS Val";
                case 1016: return "RPK";
                case 1154: return "MP-133";
                case 1160: return "MG42";
                case 1161: return "TAC-50";
                case 1201: return "Makarov";
                case 1202: return "APS";
                case 1203: return "TT";
            }
            if (item <= 0) return null;
            return MercWeaponReach.Name(MercWeaponReach.Kind(item)) + " " + item;
        }

        internal static string Metres(float units)
        {
            return units < 0f ? "? m" : Mathf.FloorToInt(units / Metre + 0.5f) + " m";
        }

        /// <summary>Where he was when it hit: in cover / up at a peek / in the open.</summary>
        internal static string Place(ref MercHitNote h)
        {
            if (h.InCover) return h.Exposed ? "in cover (flanked)" : "in cover";
            if (h.Up) return "up firing";
            return "in the open";
        }

        /// <summary>The BepInEx log line. killer / weapon may be null.</summary>
        internal static string Line(string name, int id, string killer, string weapon, ref MercHitNote h,
            string order, string state, string mode, int grade100)
        {
            return "Mercs: DEATH " + name + " (id " + id + ", grade " + grade100 + " %): killed by "
                + (killer ?? "unknown") + " with " + (weapon ?? DamageName(h.Type, h.Attacker))
                + " at " + Metres(h.Distance) + ", " + Place(ref h)
                + ", last order " + order + ", state " + state + "/" + mode
                + ", " + h.Threats + " threats, " + h.HitsInFight + " hits this fight, last hit "
                + h.Damage.ToString("0.0", CultureInfo.InvariantCulture) + " damage.";
        }
    }
}

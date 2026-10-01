// K4b: native paradrop contacts, owner-local perception; no synthetic damage.
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercParas
    {
        internal struct Contact
        {
            internal Component Ai;
            internal Vector3 Landing, GroupLanding;
            internal float JumpAt, LandAt, RegisteredAt;
        }

        // Matches CombatLoadPolicy.ParaCap (64); retaliation caps its wave at
        // 36. Entries survive landing until native destruction/dead-slot reuse.
        internal const int Capacity = 64;
        internal static readonly Contact[] Contacts = new Contact[Capacity];
        internal static int Count;

        internal static void Register(Component ai, Vector3 landing, Vector3 groupLanding,
            float jumpAt, float landAt)
        {
            if (ai == null) return;
            int free = -1;
            for (int i = 0; i < Count; i++)
            {
                if (Contacts[i].Ai == ai) return;
                // Native Start/remote appearance can lag registration. An
                // uninitialized body must not be mistaken for a reusable corpse.
                if (Contacts[i].Ai == null || (Time.time - Contacts[i].RegisteredAt > 1f
                    && !NpcWar.GroundAlive(Contacts[i].Ai))) free = i;
            }
            if (free < 0)
            {
                if (Count == Capacity) return;
                free = Count++;
            }
            Contacts[free] = new Contact { Ai = ai, Landing = landing,
                GroupLanding = groupLanding, JumpAt = jumpAt, LandAt = landAt, RegisteredAt = Time.time };
        }

        internal static void Forget(Component ai)
        {
            for (int i = 0; i < Count; i++)
                if (Contacts[i].Ai == ai) Contacts[i] = new Contact();
        }

        internal static bool Known(Component ai, out Contact contact)
        {
            for (int i = 0; i < Count; i++)
                if (Contacts[i].Ai != null && Contacts[i].Ai == ai)
                { contact = Contacts[i]; return true; }
            contact = new Contact();
            return false;
        }

        internal static float Score(Contact c, Vector3 me, Vector3 defend, float now)
        {
            Vector3 a = c.GroupLanding - me, b = c.GroupLanding - defend;
            a.y = b.y = 0f;
            // Close landings threaten the merc or his owner's position; an
            // imminent landing wins over a similarly close, higher stick.
            float distance = Mathf.Sqrt(Mathf.Min(a.sqrMagnitude, b.sqrMagnitude));
            Vector3 individual = c.Landing - me; individual.y = 0f;
            return distance + Mathf.Clamp(c.LandAt - now, 0f, 30f) * 2.8f
                + Mathf.Sqrt(individual.sqrMagnitude) * 0.1f;
        }
    }

    public static partial class NpcWar
    {
        static readonly Component[] _paraCandidates = new Component[3];
        static readonly float[] _paraScores = new float[3];

        // Only authenticated native drop bodies bypass Targetable's local
        // ownership gate. ApplyDamage still runs/RPCs on that NPC's owner.
        static bool MercNpcTargetable(Fighter f, Component ai)
        {
            if (f.Squad == null || f.Squad.Merc == null) return Targetable(ai);
            MercParas.Contact contact;
            if (!MercParas.Known(ai, out contact)) return Targetable(ai);
            return Time.time >= contact.JumpAt && !Bool(ai, "GodModeEnabled")
                && !Bool(ai, "_isSafeSettlement") && !Bool(ai, "IsTalkActive")
                && Hurtable(ai) && FactionOf(ai) != null;
        }

        static bool MercParaInReach(Fighter f)
        {
            if (f.Squad == null || f.Squad.Merc == null || f.TargetIsPlayer || f.Target == null) return true;
            Component ai = f.Target.GetComponent(_npcType);
            MercParas.Contact c;
            if (ai == null || !MercParas.Known(ai, out c)) return true;
            float reach = MercReachUnits(f);
            return Alive(ai) && MercNpcTargetable(f, ai)
                && (AimWorld(f) - (f.Tr.position + Vector3.up * EyeHeight)).sqrMagnitude <= reach * reach;
        }

        static bool MercParaPick(Fighter f, float now)
        {
            MercUnit u = f.Squad.Merc;
            if (MercParas.Count == 0 || !MercMayEngage(u, now)) return false;
            if (now < f.NextParaScan) return false;
            f.NextParaScan = now + 0.2f;
            FrameProf.S(FrameProf.S_MercParas);
            try { return MercParaPickNow(f, u, now); }
            finally { FrameProf.E(FrameProf.S_MercParas); }
        }

        static bool MercParaPickNow(Fighter f, MercUnit u, float now)
        {
            // An immediate visible ground attacker or a recent hit takes
            // priority over looking up. Explicit quick focus is checked first.
            if (f.Target != null && f.Sees && now - f.LastSeen < 0.3f)
            {
                MercParas.Contact current;
                Component ai = f.TargetIsPlayer ? null : f.Target.GetComponent(_npcType);
                bool flying = ai != null && MercParas.Known(ai, out current) && now < current.LandAt;
                if (!flying && (f.Target.position - f.Tr.position).sqrMagnitude < 84f * 84f) return false;
                if (now - u.LastHit.At < 1.5f) return false;
            }
            int n = 0;
            Component fallback = null;
            Vector3 me = f.Tr.position;
            Vector3 eye = me + Vector3.up * (f.Crouched ? CrouchEye : EyeHeight);
            Vector3 defend = u.Owner == null ? me : u.Owner.position;
            float reach = MercReachUnits(f);
            for (int i = 0; i < MercParas.Count; i++)
            {
                MercParas.Contact c = MercParas.Contacts[(f.ParaCursor + i) % MercParas.Count];
                Component ai = c.Ai;
                if (ai == null || now < c.JumpAt || now > c.LandAt + 8f) continue;
                if ((ai.transform.position + Vector3.up * ChestHeight - eye).sqrMagnitude > reach * reach) continue;
                if (!Alive(ai) || !Hostile(f.Hated, FactionOf(ai)) || !MercNpcTargetable(f, ai)) continue;
                if (u.Peaceful && ai.transform != u.LastHitBy) continue;
                if (fallback == null) fallback = ai;
                float score = MercParas.Score(c, me, defend, now);
                // Avoid needless target changes/reaction resets within a group.
                if (ai.transform == f.Target) score *= 0.85f;
                int at = n < _paraCandidates.Length ? n : n - 1;
                if (n == _paraCandidates.Length && score >= _paraScores[at]) continue;
                while (at > 0 && _paraScores[at - 1] > score)
                {
                    _paraScores[at] = _paraScores[at - 1];
                    _paraCandidates[at] = _paraCandidates[at - 1];
                    at--;
                }
                _paraScores[at] = score; _paraCandidates[at] = ai;
                if (n < _paraCandidates.Length) n++;
            }
            f.ParaCursor = (f.ParaCursor + 1) % MercParas.Count;
            // A hidden nearest group must not starve a clear group farther
            // away. Rotate one of the same three LOS slots after a blind scan.
            if (f.ParaBlind && n == 3 && fallback != _paraCandidates[0] && fallback != _paraCandidates[1])
                _paraCandidates[2] = fallback;
            bool selected = false;
            for (int i = 0; i < n; i++)
            {
                Component ai = _paraCandidates[i];
                if (!selected && Clear(eye, ai.transform.position + Vector3.up * ChestHeight, ai.transform))
                {
                    f.Target = ai.transform; f.TargetIsPlayer = false;
                    f.AimHeight = ChestHeight; f.Sees = true; f.LastSeen = now;
                    f.NextLos = now + 0.2f;
                    u.Sense.Note(f.Target, false, now);
                    u.Sense.NextSense = 0f;
                    selected = true;
                }
                _paraCandidates[i] = null;
            }
            f.ParaBlind = !selected;
            return selected;
        }
    }
}

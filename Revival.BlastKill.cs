// A-L1: An-2 bombs kill NPCs - the live-game side of OrdnanceBlast's seams.
//
// Cause (6.66 field run, 2026-10-02, eight FAB-100 over Locator, host): the
// shared queue did reach the NPCs - vehicles burned in the same job, the same
// owner path killed mercs that session, the installed IL matches the source.
// Three things kept the bombs from killing anyone a pilot could notice:
//   1. A pure linear falloff from the burst point: a FAB-100 (630 at 0 m,
//      zero at 31.5 m) killed Locator's 100-250 HP men only inside 19-27 m
//      and could never kill its 7500/8500 HP bosses.
//   2. NPC_AI2.ApplyDamage returns while _isWoundedAction is set (IL_0040).
//      A near miss that leaves a man under 25 HP may put him into the native
//      wounded state, and from then on every later bomb did nothing to him.
//   3. Nobody saw a result: from the aircraft a dead NPC leaves the air
//      silhouette tier and vanilla LOD culls him past 125 m.
// The fix: An-2 jobs use a full-damage core with a certain-kill centre
// (OrdnanceBlast.Enqueue lethalCore), a lethal anonymous explosion finishes a
// natively wounded NPC on his Photon owner (prefix below), every An-2
// explosion writes one throttled report line, and the master sends the
// dropper the result for an on-screen tally (An2Bombs.BlastResult).
//
// Cost: no tick. Reports run inside OrdnanceBlast.Tick (its F6 slot) only for
// labelled An-2 jobs; nothing runs while no bomb falls. The prefix returns on
// its first integer compare for every non-explosion hit.
//
// C# 3.0 (csc from .NET 3.5). ASCII.
using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class OrdnanceBlast
    {
        static partial void ReadHealth(Component ai, ref float health)
        {
            health = BlastKill.Health(ai);
        }

        // Inside the certain-kill centre: enough to kill through boss scaling
        // and any squad/merc armour scale down to 1/LethalMargin.
        static partial void LethalFloor(Component ai, ref float damage)
        {
            float hp = BlastKill.Health(ai);
            if (hp > 0f) damage = Mathf.Max(damage, (hp + 1f) * BlastKill.LethalMargin);
        }

        static partial void NoteNpc(Blast b, Component ai, float distance, float damage, int outcome)
        {
            BlastKill.Note(b.Serial, ai, distance, damage, outcome);
        }

        static partial void NoteMiss(Blast b, Component ai, float distance)
        {
            BlastKill.Miss(b.Serial, ai, distance);
        }

        static partial void BlastDone(Blast b)
        {
            BlastKill.Done(b.Serial, b.Label, b.ReportId, b.Point, b.Radius, b.NpcCore, b.NpcLethal, b.NpcPeak,
                b.Seen, b.Killed, b.Hurt, b.Remote, b.Refused, b.Corpses);
        }

        /// <summary>Photon actor owning this NPC, or -1.</summary>
        internal static int OwnerActor(Component ai)
        {
            try
            {
                if (ai == null || _getView == null) return -1;
                object view = _getView.Invoke(null, new object[] { ai.gameObject });
                object owner = view == null ? null : _owner.GetValue(view, null);
                if (owner == null) return -1;
                PropertyInfo id = owner.GetType().GetProperty("ID");
                return id == null ? -1 : Convert.ToInt32(id.GetValue(owner, null));
            }
            catch { return -1; }
        }
    }

    internal static class BlastKill
    {
        internal const float LethalMargin = 4f;
        const int Top = 4;
        const float Window = 10f;
        const int LinesPerWindow = 12;

        static FieldInfo _fSpecs, _fHealth, _fWounded;
        static bool _healthLooked;
        static int _finished;

        // One report at a time: OrdnanceBlast visits its jobs one after another.
        static int _serial = -1;
        static readonly Component[] _ai = new Component[Top];
        static readonly float[] _dist = new float[Top], _dmg = new float[Top], _hp = new float[Top];
        static readonly int[] _out = new int[Top];
        static int _count;
        static Component _missAi;
        static float _missDist;
        static float _windowStart = -999f;
        static int _lines, _suppressed;
        static readonly StringBuilder _sb = new StringBuilder(512);

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Type info = RevivalPlugin.TypeByName("PhotonMessageInfo");
                _fSpecs = npc == null ? null : AccessTools.Field(npc, "Specifications");
                _fWounded = npc == null ? null : AccessTools.Field(npc, "_isWoundedAction");
                if (_fWounded != null && _fWounded.FieldType != typeof(bool)) _fWounded = null;
                MethodInfo apply = npc == null || info == null ? null : AccessTools.Method(npc, "ApplyDamage", new Type[] {
                    typeof(float), typeof(int), typeof(int), typeof(int), typeof(Vector3), info }, null);
                if (apply == null || _fWounded == null || _fSpecs == null)
                    throw new Exception("NPC_AI2.ApplyDamage/_isWoundedAction/Specifications missing");
                HarmonyMethod prefix = new HarmonyMethod(typeof(BlastKill).GetMethod("FinishWoundedPrefix"));
                prefix.priority = Priority.Last; // see the damage after armour prefixes
                harmony.Patch(apply, prefix, null, null, null, null);
                RevivalPlugin.L.LogInfo("BlastKill: a lethal explosion finishes wounded NPCs; An-2 bursts are reported.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("BlastKill: " + ex.Message + " - wounded NPCs stay immune to blasts.");
            }
        }

        /// <summary>Prefix on NPC_AI2.ApplyDamage, runs on the NPC's Photon
        /// owner (a local call or the ApplyDamage RPC). The native method
        /// returns while _isWoundedAction is set; an anonymous explosion that
        /// would kill him clears it so the same call kills. Mercs keep their own
        /// downed/rescue rules.</summary>
        public static void FinishWoundedPrefix(object __instance, float __0, int __2, int __3)
        {
            if (__2 != OrdnanceBlast.Explosion || __3 != 0 || __0 < 1f || _fWounded == null) return;
            Component ai = __instance as Component;
            if (ai == null) return;
            try
            {
                if (!FastField.GetBool(_fWounded, ai) || Mercs.UnitOf(ai) != null) return;
                float hp = Health(ai);
                if (hp < 0f || __0 < hp) return;
                FastField.SetBool(_fWounded, ai, false);
                if (_finished++ < 5)
                    RevivalPlugin.L.LogInfo("BlastKill: wounded " + ai.name + " (" + hp.ToString("0") + " HP) finished by a "
                        + __0.ToString("0") + " explosion.");
            }
            catch { }
        }

        /// <summary>Specifications.Health, -1 when unreadable.</summary>
        internal static float Health(Component ai)
        {
            try
            {
                if (ai == null || _fSpecs == null) return -1f;
                object specs = _fSpecs.GetValue(ai);
                if (specs == null) return -1f;
                if (!_healthLooked)
                {
                    _healthLooked = true;
                    _fHealth = AccessTools.Field(specs.GetType(), "Health");
                    if (_fHealth != null && _fHealth.FieldType != typeof(float)) _fHealth = null;
                }
                return _fHealth == null ? -1f : FastField.GetFloat(_fHealth, specs);
            }
            catch { return -1f; }
        }

        static void Begin(int serial)
        {
            if (serial == _serial) return;
            _serial = serial;
            _count = 0;
            _missAi = null; _missDist = float.MaxValue;
            for (int i = 0; i < Top; i++) _ai[i] = null;
        }

        internal static void Note(int serial, Component ai, float distance, float damage, int outcome)
        {
            Begin(serial);
            if (outcome == OrdnanceBlast.OutDead) return; // counted as a corpse only
            int at = _count;
            while (at > 0 && _dist[at - 1] > distance) at--;
            if (at >= Top) return;
            int last = Math.Min(_count, Top - 1);
            for (int i = last; i > at; i--)
            {
                _ai[i] = _ai[i - 1]; _dist[i] = _dist[i - 1]; _dmg[i] = _dmg[i - 1];
                _hp[i] = _hp[i - 1]; _out[i] = _out[i - 1];
            }
            _ai[at] = ai; _dist[at] = distance; _dmg[at] = damage; _out[at] = outcome;
            _hp[at] = outcome == OrdnanceBlast.OutRemote ? -1f : Health(ai);
            if (_count < Top) _count++;
        }

        internal static void Miss(int serial, Component ai, float distance)
        {
            Begin(serial);
            if (distance < _missDist) { _missDist = distance; _missAi = ai; }
        }

        internal static void Done(int serial, string label, int reportId, Vector3 point, float radius, float core,
            float lethal, float peak, int seen, int killed, int hurt, int remote, int refused, int corpses)
        {
            try
            {
                Begin(serial);
                if (reportId != 0) An2Bombs.BlastResult(reportId, killed, hurt + remote, seen);
                float now = Time.realtimeSinceStartup;
                if (now - _windowStart > Window) { _windowStart = now; _lines = 0; }
                if (_lines >= LinesPerWindow) { _suppressed++; return; }
                _lines++;
                const float K = OrdnanceBlast.UnitsPerMetre;
                StringBuilder s = _sb;
                s.Length = 0;
                s.Append("BlastKill: ").Append(label).Append(" #").Append(reportId).Append(" at ").Append(point.ToString("0"))
                    .Append(", radius ").Append((radius / K).ToString("0.0")).Append(" m");
                if (core > 0f)
                    s.Append(" (full ").Append((core / K).ToString("0.0")).Append(" m, certain kill ")
                        .Append((lethal / K).ToString("0.0")).Append(" m)");
                s.Append(", NPC peak ").Append(peak.ToString("0")).Append(": ").Append(seen)
                    .Append(" NPC(s) in reach - killed ").Append(killed).Append(", hurt ").Append(hurt)
                    .Append(", other owner ").Append(remote).Append(", refused ").Append(refused)
                    .Append(", corpses ").Append(corpses).Append('.');
                for (int i = 0; i < _count; i++)
                {
                    Component ai = _ai[i];
                    s.Append(i == 0 ? " Nearest: " : "; ").Append(ai == null ? "(gone)" : ai.name).Append(' ')
                        .Append((_dist[i] / K).ToString("0.0")).Append(" m, ").Append(_dmg[i].ToString("0"))
                        .Append(" dmg -> ").Append(OutcomeName(_out[i]));
                    if (_hp[i] >= 0f) s.Append(", ").Append(_hp[i].ToString("0")).Append(" HP left");
                    s.Append(", owner actor ").Append(OrdnanceBlast.OwnerActor(ai));
                }
                if (_missAi != null)
                    s.Append(". Nearest outside reach: ").Append(_missAi.name).Append(' ')
                        .Append((_missDist / K).ToString("0.0")).Append(" m");
                if (_suppressed > 0) s.Append(" (").Append(_suppressed).Append(" report(s) suppressed)");
                _suppressed = 0;
                RevivalPlugin.L.LogInfo(s.ToString());
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("BlastKill report: " + ex.Message);
            }
            finally
            {
                for (int i = 0; i < Top; i++) _ai[i] = null;
                _missAi = null; _count = 0; _serial = -1;
            }
        }

        internal static string OutcomeName(int outcome)
        {
            switch (outcome)
            {
                case OrdnanceBlast.OutDead: return "already dead";
                case OrdnanceBlast.OutShielded: return "shielded (god/safe/trader/talk/uninitialised)";
                case OrdnanceBlast.OutNoView: return "no PhotonView";
                case OrdnanceBlast.OutNoOwner: return "no owner";
                case OrdnanceBlast.OutRemote: return "sent to owner";
                case OrdnanceBlast.OutHurt: return "hurt";
                case OrdnanceBlast.OutKilled: return "killed";
                case OrdnanceBlast.OutUnchanged: return "health unchanged";
                case OrdnanceBlast.OutError: return "error";
            }
            return "?";
        }
    }
}

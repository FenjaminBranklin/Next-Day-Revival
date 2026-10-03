// C-W1: bomb kills leave lootable corpses where they fell.
//
// Cause (6.67.0 field run, 2026-10-03: six FAB-100 bursts, 8 men killed at
// 2.4-23 m, no bodies found afterwards). Nothing removed them: BlastKill only
// reports, CombatLoad and PhysicsDiet handle event crews, NpcWar graves its
// own squads, and a vanilla corpse stays until its settlement's
// RespawnTimeInSec (1800 s at Locator). The bodies were thrown away (IL):
//   OrdnanceBlast.NpcDamage hands NPC_AI2.ApplyDamage the full displacement
//   (victim - burst, world units) as its direction. ApplyDamage ->
//   DecreaseHealth -> SetHealthValue (RPC to every other client) ->
//   DeathAction(type, part, dir) -> RagdollController.ApplyPhysicsForce(part,
//   dir, GetPhysicsForceByDamageType(14 Explosion) = 60) ->
//   DamagedPart.ApplyForce: body.velocity = dir * 60. Native
//   ExplosionObject.ExplosionPhysicsEffect sends dir.y = 0, normalized, i.e.
//   60 u/s. A man 12 m from a FAB-100 got 33.6 u x 60 = 2016 u/s (720 m/s)
//   on his torso - out of the settlement or through the ground in one
//   physics step. The ragdoll is live whenever the settlement shows its men,
//   and a pilot overhead counts (horizontal presence test, RE A-L1), so
//   every bomb kill from the An-2 was thrown.
// The fix, on every client (DeathAction runs on the NPC's owner and through
// the SetHealthValue RPC everywhere else):
//   1. A DeathAction prefix gives explosion deaths the native horizontal unit
//      direction. A burst exactly under the man (a zero vector, which
//      ApplyPhysicsForce turns into Vector3.down x 60) throws him backwards
//      instead. FlakPositions still receives the full displacement in
//      ApplyDamage, where it rebuilds the burst point.
//   2. A postfix watches each explosion corpse for WatchSeconds: a torso
//      sunk under the terrain (only when he died above it - bunker and
//      cellar dead are never lifted) or more than 15 m from where he fell
//      lays the whole ragdoll back on the spot he died on, velocities
//      cleared, its torso LiftU over the higher of his feet and the terrain
//      and every bone at least ClearU over that floor and over the terrain
//      under it (one lift for all bones, so the pose is kept). That spot is
//      where the living man stood, so it is free of colliders; the native
//      ragdoll settles from there. The NPC object, his inventory and the
//      native respawn timer are untouched.
//
// Cost (F6 "BlastCorpse.Tick"): one int compare while no explosion corpse is
// watched. A watched corpse is checked at 4 Hz, at most PerFrame per frame:
// one terrain sample and one position read, no physics query, no
// allocation (fixed table of Slots entries). A recall (rare: a lost corpse)
// adds one terrain sample per bone.
//
// C# 3.0 (csc from .NET 3.5). ASCII.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class BlastCorpse
    {
        const float K = 2.8f;
        internal const int Explosion = 14;                  // OrdnanceBlast.Explosion, native damage type
        internal const float WatchSeconds = 8f, CheckEvery = 0.25f;
        internal const float SinkU = 0.5f * K;              // torso this far under the terrain: sunk
        internal const float CellarU = 0.5f * K;            // feet this far under the terrain at death: never lifted
        internal const float StrayU = 15f * K;              // torso this far from where he fell: thrown
        internal const float LiftU = 0.6f * K;              // torso over the death spot after a recall
        internal const float ClearU = 0.15f * K;            // every bone at least this far over the terrain after a recall
        internal const int MaxRecalls = 3;
        internal const int Kept = 0, Sunk = 1, Thrown = 2;
        internal const int Slots = 64, PerFrame = 8;

        sealed class Watch
        {
            internal Component Ai;
            internal Rigidbody[] Bodies;
            internal Rigidbody Torso;
            internal Vector3 Fell;
            internal float Floor;                           // feet or terrain, the higher; feet under a cellar roof
            internal bool Above;
            internal int Recalls;
            internal float Until, Next;
            internal bool Used;
        }

        static readonly Watch[] _watch = NewTable();
        static int _count, _cursor, _errors, _forceLines, _recallLines, _recalled, _straightened;
        static FieldInfo _fRagdoll, _fBodies, _fHips;
        static MethodInfo _alive;
        static bool _ready;

        static Watch[] NewTable()
        {
            Watch[] t = new Watch[Slots];
            for (int i = 0; i < t.Length; i++) t[i] = new Watch();
            return t;
        }

        internal static int Watched { get { return _count; } }
        internal static int Recalled { get { return _recalled; } }
        internal static int Straightened { get { return _straightened; } }

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Type ragdoll = RevivalPlugin.TypeByName("PlayerRagdollController");
                MethodInfo death = npc == null ? null : DeathAction(npc);
                _alive = npc == null ? null : AccessTools.Method(npc, "IsAlive", Type.EmptyTypes, null);
                if (death == null || _alive == null) throw new Exception("NPC_AI2.DeathAction(DamageTypes, DamagePart, Vector3)/IsAlive missing");
                _fRagdoll = AccessTools.Field(npc, "RagdollController");
                _fBodies = ragdoll == null ? null : AccessTools.Field(ragdoll, "RigidBodies");
                _fHips = ragdoll == null ? null : AccessTools.Field(ragdoll, "MainChar_Hips");
                if (_fBodies != null && _fBodies.FieldType != typeof(Rigidbody[])) _fBodies = null;
                _ready = _fRagdoll != null && _fBodies != null;
                // Last: Crew.RemoteMessagePrefix takes __args, which Harmony
                // fills at entry and writes back after that prefix - an
                // earlier ref change would be overwritten with the raw vector.
                HarmonyMethod prefix = new HarmonyMethod(typeof(BlastCorpse).GetMethod("NativeForcePrefix"));
                prefix.priority = Priority.Last;
                harmony.Patch(death, prefix, new HarmonyMethod(typeof(BlastCorpse).GetMethod("WatchPostfix")), null, null, null);
                RevivalPlugin.L.LogInfo("BlastCorpse: explosion deaths get the native unit force; "
                    + (_ready ? "corpses watched " + WatchSeconds.ToString("0") + " s and laid back where they fell."
                        : "corpse watch OFF (ragdoll fields missing)."));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("BlastCorpse: " + ex.Message + " - explosion corpses keep the raw blast throw.");
            }
        }

        /// <summary>The native DeathAction(DamageTypes, DamagePart, Vector3).
        /// Its first two parameters are the game's int-backed enums (unlike
        /// ApplyDamage/SetHealthValue, which take ints), so it is found by
        /// shape; the patches read them as int, which is the same IL value.</summary>
        static MethodInfo DeathAction(Type npc)
        {
            foreach (MethodInfo m in npc.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "DeathAction") continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 3 && p[2].ParameterType == typeof(Vector3)
                    && IntBacked(p[0].ParameterType) && IntBacked(p[1].ParameterType)) return m;
            }
            return null;
        }

        static bool IntBacked(Type t)
        {
            return t == typeof(int) || (t.IsEnum && Enum.GetUnderlyingType(t) == typeof(int));
        }

        /// <summary>Prefix on NPC_AI2.DeathAction(type, part, direction), on
        /// every client. Explosion deaths get the native unit direction.</summary>
        public static void NativeForcePrefix(object __instance, int __0, ref Vector3 __2)
        {
            if (__0 != Explosion) return;
            Component ai = __instance as Component;
            Vector3 raw = __2;
            __2 = NativeDirection(raw, ai == null ? Vector3.zero : -ai.transform.forward);
            float length = Mathf.Sqrt(raw.x * raw.x + raw.y * raw.y + raw.z * raw.z);
            if (length <= 1.01f) return;
            _straightened++;
            if (_forceLines < 6)
            {
                _forceLines++;
                RevivalPlugin.L.LogInfo("BlastCorpse: " + (ai == null ? "NPC" : ai.name) + " killed by a blast "
                    + (length / K).ToString("0.0") + " m away - corpse thrown at the native 60 u/s, not "
                    + (length * 60f).ToString("0") + " u/s.");
            }
        }

        /// <summary>The native explosion direction: horizontal, unit length.
        /// A (near) zero vector falls back to 'fallback', then to +x, so
        /// ApplyPhysicsForce never drives the torso straight down.</summary>
        internal static Vector3 NativeDirection(Vector3 d, Vector3 fallback)
        {
            float sq = d.x * d.x + d.z * d.z;
            if (sq < 1e-6f)
            {
                d = fallback;
                sq = d.x * d.x + d.z * d.z;
                if (sq < 1e-6f) return new Vector3(1f, 0f, 0f);
            }
            float inv = 1f / Mathf.Sqrt(sq);
            return new Vector3(d.x * inv, 0f, d.z * inv);
        }

        /// <summary>Postfix on DeathAction: an explosion corpse is watched. A
        /// deferred call (Crew's remote replay skipped the original) leaves
        /// the man alive and is registered when it is replayed.</summary>
        public static void WatchPostfix(object __instance, int __0)
        {
            if (__0 != Explosion || !_ready) return;
            Component ai = __instance as Component;
            if (ai == null) return;
            try
            {
                if (FastCall.Bool(_alive, ai)) return;
                Add(ai);
            }
            catch (Exception ex)
            {
                if (_errors++ < 3) RevivalPlugin.L.LogWarning("BlastCorpse watch: " + ex.Message);
            }
        }

        static void Add(Component ai)
        {
            object ragdoll = _fRagdoll.GetValue(ai);
            Rigidbody[] bodies = ragdoll == null ? null : _fBodies.GetValue(ragdoll) as Rigidbody[];
            Rigidbody torso = bodies == null ? null : Torso(ragdoll, bodies);
            if (torso == null) return;
            Watch w = null, free = null, oldest = null;
            for (int i = 0; i < _watch.Length; i++)
            {
                Watch s = _watch[i];
                if (!s.Used) { if (free == null) free = s; continue; }
                if (ReferenceEquals(s.Ai, ai)) { w = s; break; }              // the same man again
                if (oldest == null || s.Until < oldest.Until) oldest = s;
            }
            if (w == null) w = free != null ? free : oldest;                   // full: replace the oldest watch
            if (!w.Used) { w.Used = true; _count++; }
            float now = Time.time, ground;
            w.Ai = ai; w.Bodies = bodies; w.Torso = torso; w.Recalls = 0;
            w.Fell = ai.transform.position;
            w.Above = EastWorld.TerrainHeight(w.Fell, out ground) && w.Fell.y >= ground - CellarU;
            w.Floor = w.Above ? Mathf.Max(w.Fell.y, ground) : w.Fell.y;
            w.Until = now + WatchSeconds; w.Next = now + CheckEvery;
        }

        static Rigidbody Torso(object ragdoll, Rigidbody[] bodies)
        {
            Component hips = _fHips == null ? null : _fHips.GetValue(ragdoll) as Component;
            if (hips != null)
            {
                Rigidbody r = hips as Rigidbody;
                if (r == null) r = hips.GetComponent<Rigidbody>();
                if (r != null) return r;
            }
            // RigidBodies is GetComponentsInChildren order: the hips first.
            for (int i = 0; i < bodies.Length; i++) if (bodies[i] != null) return bodies[i];
            return null;
        }

        internal static void Tick()
        {
            if (_count == 0) return;
            FrameProf.S(FrameProf.S_BlastCorpseT);
            try
            {
                float now = Time.time;
                int checks = 0;
                for (int n = 0; n < _watch.Length && checks < PerFrame; n++)
                {
                    Watch w = _watch[_cursor];
                    _cursor = (_cursor + 1) % _watch.Length;
                    if (!w.Used) continue;
                    if (w.Ai == null || w.Torso == null || now >= w.Until) { Drop(w); continue; }
                    if (now < w.Next) continue;
                    w.Next = now + CheckEvery;
                    checks++;
                    Check(w);
                }
            }
            catch (Exception ex)
            {
                if (_errors++ < 3) RevivalPlugin.L.LogWarning("BlastCorpse tick: " + ex.Message);
            }
            finally { FrameProf.E(FrameProf.S_BlastCorpseT); }
        }

        static void Check(Watch w)
        {
            Vector3 torso = w.Torso.position;
            float ground;
            bool terrain = EastWorld.TerrainHeight(torso, out ground);
            int why = Lost(w.Fell, w.Above, torso, terrain, ground);
            if (why == Kept) return;
            Recall(w, torso);
            _recalled++;
            if (_recallLines < 12)
            {
                _recallLines++;
                float dx = torso.x - w.Fell.x, dz = torso.z - w.Fell.z;
                RevivalPlugin.L.LogInfo("BlastCorpse: " + w.Ai.name + "'s corpse "
                    + (why == Sunk ? "sank " + ((ground - torso.y) / K).ToString("0.0") + " m under the terrain"
                        : "was thrown " + (Mathf.Sqrt(dx * dx + dz * dz) / K).ToString("0") + " m")
                    + " - laid back where he fell at " + w.Fell.ToString("0") + ".");
            }
            if (++w.Recalls >= MaxRecalls) Drop(w); // the spot itself keeps rejecting him: leave it to the game
        }

        /// <summary>Kept, Sunk or Thrown for a torso position. 'above': he died
        /// on or over the terrain; 'terrain'/'ground': the terrain height under
        /// the torso, when there is terrain there.</summary>
        internal static int Lost(Vector3 fell, bool above, Vector3 torso, bool terrain, float ground)
        {
            if (above && terrain && torso.y < ground - SinkU) return Sunk;
            float dx = torso.x - fell.x, dz = torso.z - fell.z;
            if (dx * dx + dz * dz > StrayU * StrayU || torso.y < fell.y - StrayU) return Thrown;
            return Kept;
        }

        /// <summary>Moves every ragdoll body by the same offset, so the pose
        /// is kept: the torso LiftU over the floor of the death spot, every
        /// bone at least ClearU over that floor (roof, storey or cellar he
        /// stood on) and, when he died above the terrain, over the terrain
        /// under it.</summary>
        static void Recall(Watch w, Vector3 torso)
        {
            Vector3 delta = RecallOffset(w.Fell, w.Floor, torso);
            float lift = 0f, ground;
            for (int i = 0; i < w.Bodies.Length; i++)
            {
                Rigidbody b = w.Bodies[i];
                if (b == null) continue;
                Vector3 p = b.position + delta;
                lift = Mathf.Max(lift, ClearLift(p.y, w.Floor));
                if (w.Above && EastWorld.TerrainHeight(p, out ground)) lift = Mathf.Max(lift, ClearLift(p.y, ground));
            }
            delta.y += lift;
            for (int i = 0; i < w.Bodies.Length; i++)
            {
                Rigidbody b = w.Bodies[i];
                if (b == null) continue;
                b.position = b.position + delta;
                if (b.isKinematic) continue;
                b.velocity = Vector3.zero;
                b.angularVelocity = Vector3.zero;
            }
        }

        internal static Vector3 RecallOffset(Vector3 fell, float floor, Vector3 torso)
        {
            return new Vector3(fell.x - torso.x, floor + LiftU - torso.y, fell.z - torso.z);
        }

        /// <summary>How far a bone at height y must rise to clear the
        /// terrain at 'ground' by ClearU; zero when it already does.</summary>
        internal static float ClearLift(float y, float ground)
        {
            float need = ground + ClearU - y;
            return need > 0f ? need : 0f;
        }

        static void Drop(Watch w)
        {
            if (w.Used) { w.Used = false; _count--; }
            w.Ai = null; w.Bodies = null; w.Torso = null;
        }
    }
}

// Editor ground groups, the alive layer - the game adapter. The decisions are
// Revival.GroundAliveCore.cs (GroundBoard / GroundBrain, checked offline by
// research/ground_alive_check.py); this file feeds them what NpcWar's men see
// and turns their orders into NpcWar moves (docs/ai/tasks/editor-npcs-alive.md).
//
//   - only groups from the editor channel (RevivalGroundEnemies, not the
//     airfield / military town pockets) get a brain: GroundAliveOn;
//   - a sleeping group (no player within GroundBrain.WakeRange) is skipped by
//     RunGround before any per-man work; every 2 s its men are counted and
//     told to keep still (GroundAliveGate);
//   - an awake, calm group steps each man every other frame; in a fight
//     every frame. Runs to cover and flanks are the only runs a ground group
//     makes (GroundAliveBusy); everything calm is a walk;
//   - F6 slot GroundAlive.Tick covers the board and every awake group's
//     RunGround. Photon master only, like the rest of NpcWar: the moves go
//     out through the game's own synced states, nothing new on the wire.
//
// C# 3.0, ASCII only.
using System;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>IGroundWorld in the game: the M1 cover world's rays (non-alloc,
    /// people are no hit), the ground groups' walkable-ground test, one reused
    /// NavMeshPath for the walk check, NpcWar's player list.</summary>
    internal sealed class PhysicsGroundWorld : IGroundWorld
    {
        readonly PhysicsCoverWorld _cover;
        readonly NavMeshPath _path = new NavMeshPath();
        readonly Vector3[] _corners = new Vector3[64];

        internal PhysicsGroundWorld(Type npcType) { _cover = new PhysicsCoverWorld(npcType); }

        internal void Forget() { _cover.Forget(); }

        public bool Cast(Vector3 from, Vector3 dir, float range, out float distance)
        {
            Vector3 point, normal;
            bool dynamic;
            distance = range;
            // Something that can drive away is no cover to map.
            if (!_cover.Cast(from, dir, range, out point, out normal, out dynamic) || dynamic) return false;
            Vector3 d = point - from;
            d.y = 0f;
            distance = d.magnitude;
            return true;
        }

        public bool Stand(Vector3 near, float reach, out Vector3 at)
        {
            return RevivalGroundEnemies.TryGround(near, reach, out at);
        }

        public bool Walk(Vector3 from, Vector3 to, Vector3 home, float leash)
        {
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _path)
                || _path.status != NavMeshPathStatus.PathComplete) return false;
            int n = _path.GetCornersNonAlloc(_corners);
            float sqr = leash * leash;
            for (int c = 0; c < n; c++)
            {
                Vector3 v = _corners[c] - home;
                v.y = 0f;
                if (v.sqrMagnitude > sqr) return false;
            }
            return true;
        }

        public bool PlayerNear(Vector3 p, float range) { return NpcWar.AnyPlayerNear(p, range); }
    }

    public static partial class NpcWar
    {
        static readonly GroundBoard _aliveBoard = new GroundBoard();
        static PhysicsGroundWorld _aliveWorld;
        // Shared, never written: Invoke copies the arguments (n01 perf). A
        // sleeping man's vanilla idle logic stays off until the next upkeep.
        static readonly object[] PauseFar = { 2.6f };

        internal static bool AnyPlayerNear(Vector3 p, float range) { return PlayerNear(p, range); }

        /// <summary>RevivalGroundEnemies: an editor group has started (or a new
        /// master adopted it) - give it a brain. Traitors shoot traitors, so a
        /// traitor group has no friends to call; the other factions share one
        /// side each.</summary>
        internal static void GroundAliveOn(string tag, string faction)
        {
            Squad s = null;
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i].GroundGroup && _squads[i].Tag == tag) { s = _squads[i]; break; }
            if (s == null || s.Alive != null || s.Men.Count == 0) return;
            byte duty = s.GroundDuty == GroundMode.Patrol ? GroundDuty.Patrol
                : s.GroundDuty == GroundMode.Guard ? GroundDuty.Hold
                : s.GroundDuty == GroundMode.Wander ? GroundDuty.Wander : GroundDuty.Roam;
            GroundBrain b = new GroundBrain(s.Men.Count, s.Lz, s.GroundRadius, duty, tag.GetHashCode());
            // Traitor sides are negative and unique, faction sides never are.
            b.Side = faction == "traitor" ? -1 - (tag.GetHashCode() & 0x7FFFFFF)
                : (faction ?? "").GetHashCode() & 0x7FFFFFFF;
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                GroundMan m = b.Men[i];
                m.Alive = f.Ai != null && f.Tr != null;
                if (m.Alive) m.Pos = f.Tr.position;
                // A building post (BuildingNav) is held, not left for cover.
                m.Fixed = s.GroundDuty == GroundMode.Guard && f.GuardLook != Vector3.zero;
            }
            s.Alive = b;
            _aliveBoard.Add(b);
            RevivalPlugin.L.LogInfo("Ground enemies: " + tag + " is alive (" + GroundDutyName(duty)
                + ", wakes within " + (GroundBrain.WakeRange / 2.8f).ToString("0") + " m of a player).");
        }

        static string GroundDutyName(byte duty)
        {
            return duty == GroundDuty.Roam ? "guard with roam" : duty == GroundDuty.Hold ? "hold position"
                : duty == GroundDuty.Wander ? "wander" : "patrol";
        }

        /// <summary>A removed squad leaves the board.</summary>
        static void GroundAliveDrop(Squad s)
        {
            if (s.Alive == null) return;
            _aliveBoard.Remove(s.Alive);
            s.Alive = null;
        }

        /// <summary>Once per NpcWar tick (master): proximity, thinks, cover
        /// mapping - each capped per frame by GroundBoard.</summary>
        static void GroundAliveFrame(float now)
        {
            if (_aliveBoard.Brains.Count == 0) return;
            _aliveBoard.Frame(AliveWorld, now);
        }

        /// <summary>The world, made on first use.</summary>
        static PhysicsGroundWorld AliveWorld
        {
            get
            {
                if (_aliveWorld == null) _aliveWorld = new PhysicsGroundWorld(_npcType);
                return _aliveWorld;
            }
        }

        /// <summary>RunGround's door: true = the group is awake, step its men.
        /// A sleeping group is counted and kept still every 2 s and costs
        /// nothing in between.</summary>
        static bool GroundAliveGate(Squad s, float now)
        {
            GroundBrain b = s.Alive;
            if (b.ResumePending) { b.ResumePending = false; GroundAliveResume(s, now); }
            // A patrol stands while its group fights or searches.
            if (b.Phase != GroundPhase.Calm && s.GroundDuty == GroundMode.Patrol) s.GroundContact = now + 5f;
            if (b.Active) return true;
            if (now < s.NextFar) return false;
            s.NextFar = now + 2f;
            int alive = 0;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < s.Men.Count && i < b.Count; i++)
            {
                Fighter f = s.Men[i];
                GroundMan m = b.Men[i];
                if (f.Ai == null || f.Tr == null || !Alive(f.Ai)) { m.Alive = false; continue; }
                alive++;
                m.Alive = true;
                m.Pos = f.Tr.position;
                sum += m.Pos;
                if (!IsMine(f.Ai)) continue;
                if (f.IkDriven) ReleaseAim(f);
                try
                {
                    if (_mClearIntentions != null) _mClearIntentions.Invoke(f.Ai, null);
                    if (_mPauseTime != null) _mPauseTime.Invoke(f.Ai, PauseFar);
                }
                catch { }
            }
            if (alive == 0) { Remove(s, "ground group defeated"); return false; }
            b.Centre = sum / alive;
            return false;
        }

        /// <summary>After a fight the duty code takes the men back: the patrol
        /// gets its waypoint slots again, the guard and the wanderers new
        /// orders.</summary>
        static void GroundAliveResume(Squad s, float now)
        {
            if (s.GroundDuty == GroundMode.Patrol) { GroundOrders(s, now); return; }
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                f.HasOrder = false;
                f.GroundPause = now + UnityEngine.Random.Range(0.5f, 3f);
            }
        }

        /// <summary>What the brain reads of a man, written in the per-man loop
        /// (field copies, no probe).</summary>
        static void GroundAliveInputs(Fighter f, GroundMan m, float now)
        {
            m.Alive = true;
            m.Pos = f.Tr.position;
            bool target = f.Target != null && f.Target;
            m.HasThreat = target && now - f.LastSeen < 3f;
            m.Sees = target && f.Sees;
            m.Hurt = target && f.TargetIsPlayer;
            if (target) m.Threat = f.Target.position;
            if (m.Hurt) m.HasThreat = true;
        }

        static void GroundAliveOrder(Fighter f, GroundMan m, float now)
        {
            m.Issued = m.Order;
            f.GroundPause = now + 3f;
            Go(f, m.Dest, m.Run ? MainRun : MainWalk, PoseStand, now, m.Run ? Stance.Reposition : Stance.Advance);
        }

        /// <summary>Before the fire code: a man on his run to cover or to a
        /// flank does not stop to shoot until he is there.</summary>
        static bool GroundAliveBusy(Fighter f, Squad s, int i, float now)
        {
            GroundBrain b = s.Alive;
            if (i >= b.Count || b.Phase != GroundPhase.Contact) return false;
            GroundMan m = b.Men[i];
            if (!m.Moving || !m.Run || now >= m.Deadline) return false;
            if (m.Order != m.Issued) { GroundAliveOrder(f, m, now); return true; }
            if (Flat(f.Tr.position - m.Dest) <= 2.5f) return false;
            if (f.HasOrder && now < f.MoveDeadline) { Drive(f, MainRun, AddNone, PoseStand, now, false); return true; }
            if (now >= f.GroundPause) GroundAliveOrder(f, m, now);
            return true;
        }

        /// <summary>After the fire code: the brain's order for a man who is
        /// not shooting. false = his calm duty is NpcWar's (hold / wander /
        /// patrol).</summary>
        static bool GroundAliveStep(Fighter f, Squad s, int i, float now)
        {
            GroundBrain b = s.Alive;
            if (i >= b.Count) return false;
            GroundMan m = b.Men[i];
            if (m.Act == GroundAct.Duty) return false;
            if (m.Moving)
            {
                if (m.Order != m.Issued) { GroundAliveOrder(f, m, now); return true; }
                if (Flat(f.Tr.position - m.Dest) > 2.5f)
                {
                    // Keep the native alarm from switching a walk to a run, and
                    // re-send an order the alarm replaced, at most every 3 s.
                    if (f.HasOrder && now < f.MoveDeadline)
                        Drive(f, m.Run ? MainRun : MainWalk, AddNone, PoseStand, now, false);
                    else if (now >= f.GroundPause) GroundAliveOrder(f, m, now);
                    return true;
                }
            }
            f.HasOrder = false;
            f.Stance = Stance.Hold;
            if (f.IkDriven) ReleaseAim(f);
            // Down behind his cover - but not in the 2 s after a shot, so a man
            // who just lost sight does not bob up and down with every glimpse.
            Drive(f, MainIdle, AddNone, m.Crouch && now >= f.GroundPause ? PoseCrouch : PoseStand, now, true);
            FaceDir(f, m.Look);
            return true;
        }
    }
}

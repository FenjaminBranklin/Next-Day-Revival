// Master-only paratroop orders, driven by RunGround under F6 GroundAlive.Tick.
using System;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        // At most one new NavMesh/native Photon move order per 0.1 s globally.
        static float _paraOrderAt;

        internal static bool StartParatroopers(string tag, GameObject settlement, Array men,
            Vector3 rally, Vector3 objective, string faction, float holdSeconds)
        {
            if (!IsMaster() || !StartGround(tag, settlement, men, rally, "waiting",
                ParaObjective.MovingRadius, null, null, false, 0f)) return false;
            Squad s = null;
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i].Tag == tag) { s = _squads[i]; break; }
            if (s == null) return false;
            s.GroundDuty = GroundMode.Guard;
            s.WalkRoot = WalkRootOf(settlement);
            s.Para = new ParaObjective(s.Men.Count, rally, objective, Mathf.Clamp(holdSeconds, 60f, 7200f));
            s.HardEnd = Time.time + Flat(objective - rally) / 0.8f + 1800f + holdSeconds;
            GroundAliveOn(tag, faction);
            for (int i = 0; i < s.Men.Count; i++)
            {
                s.Men[i].GuardLook = Vector3.zero;
                s.Men[i].GroundPause = 0f;
            }
            return s.Alive != null;
        }

        static void ParaObjectiveTick(Squad s, float now)
        {
            ParaObjective p = s.Para;
            if (!p.Due(now)) return;
            // Refresh every member before counting arrival (calm fire/animation
            // steps are sliced). Wounded men do not block the regroup quorum.
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                GroundMan m = s.Alive.Men[i];
                m.Alive = f.Ai != null && f.Tr != null && Alive(f.Ai) && !GroundDowned(f.Ai);
                if (m.Alive) m.Pos = f.Tr.position;
            }
            p.Tick(s.Alive, now);
            s.Centre = s.Alive.Centre;
            if (s.WalkRoot != null) s.WalkRoot.position = s.Centre;
            // Move the tactics leash and rebuild cached cover in small board
            // slices, only out of contact. Never drag the settlement/NPC parents.
            GroundBrain b = s.Alive;
            float radius = p.Phase == ParaObjective.Hold ? ParaObjective.HoldRadius : ParaObjective.MovingRadius;
            if (b.Phase == GroundPhase.Calm && (Flat(p.Anchor - b.Home) >= 56f || b.Radius != radius))
            {
                b.Home = p.Anchor; s.Lz = p.Anchor;
                b.Radius = radius; b.Leash = radius + GroundBrain.LeashExtra;
                b.Cover.Reset(b.Home, Mathf.Clamp(radius * 0.5f, 40f, 80f));
                b.Ready = false;
                for (int i = 0; i < b.Count; i++) b.Men[i].Cover = -1;
            }
        }

        static void ParaObjectiveStep(Fighter f, Squad s, float now)
        {
            ParaObjective p = s.Para;
            // Mission slots are projected only when a move is issued.
            Vector3 dest = p.Slots[f.GroundSlot];
            if (Flat(f.Tr.position - dest) <= ParaObjective.Arrive)
            {
                f.HasOrder = false;
                Hold(f, null, now);
                FaceDir(f, dest - p.Anchor);
                return;
            }
            bool current = Flat(f.GroundDest - dest) < 1f;
            if (current && f.HasOrder && now < f.MoveDeadline)
            {
                Drive(f, p.Phase == ParaObjective.Hold ? MainWalk : MainRun, AddNone, PoseStand, now, false);
                return;
            }
            if (now < _paraOrderAt || now < f.GroundPause) return;
            _paraOrderAt = now + 0.1f;
            f.GroundPause = now + 3.5f;
            Vector3 at;
            if (!RevivalGroundEnemies.TryGround(dest, 12f, out at)) return;
            f.GroundDest = dest;
            Go(f, at, p.Phase == ParaObjective.Hold ? MainWalk : MainRun, PoseStand, now, Stance.Advance);
        }
    }
}

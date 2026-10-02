// B S3c: catch-up inputs for the existing FOLLOW and M2/M3 movement paths.
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static int _mercFollowRouteFrame = -1;

        static bool MercFollowRouteTurn()
        {
            if (_mercFollowRouteFrame == Time.frameCount) return false;
            _mercFollowRouteFrame = Time.frameCount;
            return true;
        }

        static void MercFollowState(Fighter f, MercUnit u, float now)
        {
            FrameProf.S(FrameProf.S_MercCatchUp);
            try
            {
                Transform owner = u.Owner;
                bool enabled = MercRoleFollow(u) && !u.Supply.Active
                    && (u.Medicine == null || u.Medicine.Active == 0)
                    && owner != null && !TowerRoof.GoalUp(owner.position);
                u.Fight.Follow.Observe(u.Order, enabled, u.Fight.Overwatch.Role, u.Slot, Mercs.IsMedic(u),
                    f.Tr.position, owner == null ? Vector3.zero : owner.position,
                    owner == null ? Vector3.forward : owner.forward, now);
            }
            finally { FrameProf.E(FrameProf.S_MercCatchUp); }
        }

        static void MercFollowFight(Fighter f, MercUnit u, MercFight ft, float now)
        {
            MercFollowState(f, u, now);
            MercCatchUp follow = ft.Follow;
            ft.In.Follow = follow.Active;
            ft.In.FollowBound = follow.Active && now >= follow.NextBound;
            ft.In.FollowOrder = u.Order;
            ft.In.FollowDest = follow.Bound(f.Tr.position, u.Sense.Pick, now - u.Sense.PickAt < 2f);
            // A distant contact uses M2 bounds instead of cancelling combat,
            // or allowing continuous exposure to hold a marksman indefinitely.
            if (follow.Active && MercMayEngage(u, now)) ft.In.MayFight = true;
        }

        static bool MercFollowLeash(Fighter f, MercUnit u, out Vector3 centre, out float radius)
        {
            centre = f.Tr.position; radius = MercCoverService.Radius;
            if (!u.Fight.Follow.Active) return false;
            // The owner's far-away leash contains no shelter near this man.
            // Preserve M3 retreat/fall-back anchors and the same M1 budget.
            if (u.Fight.Out.AnchorOn) centre = u.Fight.Out.Anchor;
            return true;
        }

        static void MercFollowSpeed(Fighter f, MercUnit u, bool run)
        {
            MercCatchUp follow = u.Fight.Follow;
            bool sprint = run && follow.Active;
            if (!sprint && !follow.PaceApplied) return;
            FrameProf.S(FrameProf.S_MercCatchUp);
            try
            {
                NavMeshAgent agent = Agent(f);
                if (agent == null || !agent.isActiveAndEnabled) return;
                float basis = u.BaseSpeed(agent.speed) * (1f + Mathf.Max(0, u.Fast) / 100f);
                float want = sprint ? Mathf.Max(basis, follow.Speed) : basis;
                if (Mathf.Abs(agent.speed - want) > 0.05f) agent.speed = want;
                u.LastSpeedSet = want;
                follow.PaceApplied = sprint;
            }
            finally { FrameProf.E(FrameProf.S_MercCatchUp); }
        }

        static void MercFollowOrder(Fighter f, Vector3 dest, int state, float now, Stance stance)
        {
            FrameProf.S(FrameProf.S_MercCatchUp);
            try
            {
                NavMeshAgent agent = Agent(f);
                if (f.HasOrder && f.WantMain == state && f.Point != null
                    && agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    // Pending native jobs finish before the next 2 Hz retry.
                    // Replacing them with a full order restarts the same job.
                    if (agent.pathPending) return;
                    if (Retarget(f, dest, now)) { Drive(f, state, AddNone, PoseStand, now, false); return; }
                }
                Go(f, dest, state, PoseStand, now, stance);
            }
            finally { FrameProf.E(FrameProf.S_MercCatchUp); }
        }
    }
}

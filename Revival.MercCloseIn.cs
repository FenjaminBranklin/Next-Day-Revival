// c-m2-close-the-distance - the game side of Revival.MercCloseInCore.cs
// (docs/ai/tasks/c-m2-close-the-distance.md).
//
//   CLOSE     MercCloseInStep: out of contact (M2 released him because the
//             target is past his fire range), a merc on FOLLOW / STAY /
//             PATROL walks up to firing distance inside his order's circle,
//             to M1 cover near that point when the service has one. Once the
//             target is in range the M2 fight loop takes him again; when the
//             order leaves no such point he keeps his post and holds.
//   COST      arithmetic per merc step; one MercCoverService.Best per merc at
//             most every 1.5 s, behind the one-query-a-frame throttle. Inside
//             the existing NpcWar tick (F6 NpcWar slot), no allocation.
//
// Mercenaries only. Units: game units (~2.8 per metre). C# 3.0, ASCII only.
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        /// <summary>The circle his order lets him close inside. False: this
        /// order does not close (ATTACK and PERIMETER close on their own; a
        /// crew post, a seat, a medic or the marksman band hold).</summary>
        static bool MercCloseAnchor(Fighter f, MercUnit u, out Vector3 anchor, out float leash)
        {
            anchor = f.Tr.position; leash = 0f;
            MercOrder o = u.Order;
            if (o.MoveNear) return false;
            switch (o.Mode)
            {
                case MercOrder.Follow:
                    if (u.Owner == null || Mercs.IsMedic(u) || u.Fight.Follow.Active
                        || u.Fight.Overwatch.Role == MercRole.Marksman || TowerRoof.GoalUp(u.Owner.position)) return false;
                    anchor = u.Owner.position; leash = MercCloseIn.FollowLeash;
                    return true;
                case MercOrder.Stay:
                    if (TowerRoof.GoalUp(o.Centre)) return false;
                    anchor = o.Centre; leash = MercCloseIn.StayLeash;
                    return true;
                case MercOrder.Patrol:
                    if (o.Points == null || o.Points.Length < 2) { anchor = o.Centre; leash = MercCloseIn.StayLeash; return true; }
                    anchor = MercCloseIn.RouteAnchor(o.Points, f.Tr.position); leash = MercCloseIn.PatrolLeash;
                    return true;
            }
            return false;
        }

        /// <summary>A live target seen a moment ago, past his fire range, and
        /// nothing else asks for him (incoming fire, wounds, reload, medicine,
        /// a seat, a supply run, the owner's death shelter).</summary>
        static bool MercCloseWanted(Fighter f, MercUnit u, float now)
        {
            if (f.Target == null || !f.Target || !f.Armed || now - f.LastSeen > MercCloseIn.SeenSeconds) return false;
            if (u.Deserting || u.Rally || u.Order.Survive || u.Supply.Active || u.Ride.Boarding != null) return false;
            if (!MercMayEngage(u, now) || Reloading(f) || u.Fight.Health < MercBrain.RetreatUntil) return false;
            if (u.Medicine != null && u.Medicine.Active != 0) return false;
            return !MercMayFireAt(f, f.Target.position, now);
        }

        /// <summary>Walk up to firing distance. True: this step moved or
        /// placed him; false: his order step runs.</summary>
        static bool MercCloseInStep(Fighter f, MercUnit u, float now)
        {
            MercCloseRun c = u.Close;
            Vector3 anchor, goal;
            float leash;
            if (!MercCloseWanted(f, u, now) || !MercCloseAnchor(f, u, out anchor, out leash)) { c.End(); return false; }
            Vector3 me = f.Tr.position, at = f.Target.position;
            float fire = MercWeaponReach.EffectiveUnits(f.WeaponId);
            if (!MercCloseIn.Goal(me, at, fire, anchor, leash, out goal)) { c.End(); return false; }
            bool first = !c.Active;
            c.Begin(now);
            if (first)
                RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " closes in: target " + (Flat(at - me) / MercWeaponReach.Metre).ToString("0")
                    + " m, fires from " + (fire / MercWeaponReach.Metre).ToString("0") + " m.");
            // M1: cover near that point that still has the target in range.
            if (now >= c.NextQuery && u.Sense.Count > 0 && MercCoverService.MayQuery())
            {
                c.NextQuery = now + MercCloseRun.CoverEvery;
                CoverPick pick;
                c.Covered = MercCoverService.Best(goal, u.Sense.At, u.Sense.Weight, u.Sense.Count,
                        MercCoverService.Radius, anchor, leash, u.Id, out pick)
                    && MercCloseIn.InReach(pick.Point.Pos, at, fire);
                if (c.Covered)
                {
                    c.Cover = pick.Point.Pos;
                    MercCoverService.Field.Claim(u.Id, c.Cover, now + 5f, now);
                }
            }
            Vector3 dest = c.Covered && MercCloseIn.InReach(c.Cover, at, fire) ? c.Cover : goal;
            if (Flat(dest - me) < MercCloseIn.Arrive) { MercCrouch(f, now); FaceDir(f, at - me); return true; }
            MercMove(f, u, dest, true, now);
            return true;
        }
    }
}

// Owner-side move-to-cover adapter; no new tick or scene/physics search.
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        internal static bool OrderMove(Vector3 point, Vector3 facing)
        {
            Vector3 ground;
            if (!RevivalGroundEnemies.TryGround(point, 12f, out ground))
            {
                MercUi.Toast(Loc.T("Нет проходимой земли у отметки.", "No walkable ground near the mark."), true);
                return false;
            }
            System.Collections.Generic.List<Record> selected = SquadSelection();
            if (selected.Count == 0) { Announce(Loc.T("В УКРЫТИЕ У ОТМЕТКИ", "MOVE TO COVER"), selected); return false; }
            MercOrder order = new MercOrder();
            order.Mode = MercOrder.Stay; order.MoveNear = true;
            order.Points = new Vector3[] { ground }; order.Facing = facing;
            order.RadiusM = MercMovePlan.Radius / 2.8f;
            Give(selected, order);
            Announce(Loc.T("В УКРЫТИЕ У ОТМЕТКИ", "MOVE TO COVER"), selected);
            return true;
        }
    }

    public static partial class NpcWar
    {
        static readonly Vector3[] _moveWatch = new Vector3[1];
        static readonly float[] _moveWeight = new float[] { 1f };

        // Invoked from the existing sense path, also while the fight is active.
        // Reservations survive M2 changing/releasing its combat cover claim.
        static void MercMovePrepare(Fighter f, MercUnit u, float now)
        {
            if (!u.Order.MoveNear || u.Deserting) return;
            FrameProf.S(FrameProf.S_MercMoveOrder);
            try
            {
                CoverField field = MercCoverService.Field;
                MercMovePlan plan = u.Move;
                Vector3 me = f.Tr.position;
                plan.Begin(u.Order, field, u.Id, u.Fight.Hits);
                plan.Hit(u.Fight.Hits, me, field, u.Id, now);
                if (!plan.Travelling(me)) plan.Arrived = true;
                if (plan.Pick.Found)
                {
                    if (now >= plan.NextHold)
                    { plan.NextHold = now + 0.5f; field.ReserveOrder(u.Id, plan.Goal, now + 2f, now); }
                    return;
                }
                // Distant orders do not evict the local fighting field. Warm
                // the destination once per second when the squad nears it.
                if (Flat(me - u.Order.Centre) > MercMovePlan.Radius + CoverField.CellSize) return;
                if (now >= plan.NextWant)
                { plan.NextWant = now + 1f; field.Want(u.Order.Centre, now); }
                if (!plan.Grounded && now >= plan.NextQuery)
                {
                    Vector3 ground;
                    if (RevivalGroundEnemies.TryGround(plan.Goal, 4f, out ground)) plan.Goal = ground;
                    plan.Grounded = true;
                }
                if (now < plan.NextQuery || !MercCoverService.MayQuery()) return;
                Vector3 watch = u.Order.Facing;
                if (watch.sqrMagnitude < 0.01f) watch = f.Tr.forward;
                _moveWatch[0] = u.Order.Centre + watch.normalized * 280f;
                MercSense sense = u.Sense;
                plan.Select(field, u.Id, sense.Count > 0 ? sense.At : _moveWatch,
                    sense.Count > 0 ? sense.Weight : _moveWeight, sense.Count > 0 ? sense.Count : 1, now);
            }
            finally { FrameProf.E(FrameProf.S_MercMoveOrder); }
        }

        static void MercMoveStep(Fighter f, MercUnit u, float now)
        {
            MercMovePlan plan = u.Move;
            if (plan.For != u.Order) MercMovePrepare(f, u, now);
            if (plan.Travelling(f.Tr.position)) MercMove(f, u, plan.Waypoint(f.Tr.position), true, now);
            else { MercCrouch(f, now); MercLook(f, u, u.Order.Facing, 60f, 2f, now); }
            u.GoalFor = u.Order; u.Goal = plan.Goal;
        }

        static void MercMoveFight(Fighter f, MercUnit u, MercFight ft)
        {
            ft.In.Move = u.Order.MoveNear && !u.Deserting && !ft.In.Survive;
            ft.In.MoveTravel = ft.In.Move && !u.Move.Arrived && u.Move.Travelling(ft.In.Me);
            ft.In.MoveDest = u.Move.Waypoint(ft.In.Me);
            // Transfer the already confirmed destination on arrival. Later
            // peeks, hits and relocations use the normal M1/M2 perception.
            if (ft.In.Move && !ft.In.MoveTravel && u.Move.Pick.Found
                && ft.Brain != null && ft.Brain.State == MercBrain.Dash && !ft.Brain.Cover.Found)
            {
                ft.In.Pick = u.Move.Pick; ft.In.PickFresh = true;
                ft.In.PickFrom = u.Move.Goal;
            }
        }
    }
}

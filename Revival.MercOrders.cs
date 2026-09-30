// Y S1: immediate owner replies; one 3 s progress receipt per given order.
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        static void OrderReceived(Record r, bool focus, Vector3 objective)
        {
            MercUnit u = r.Unit;
            Vector3 at = u == null || u.Ai == null ? OwnerPosition : u.Ai.transform.position;
            r.ReceiptFor = r.Order; r.ReceiptFocus = focus;
            r.Receipt.Begin(Time.time, at, NpcWar.MercOrderShots(u));
            string action = focus ? Loc.T("атакую", "attacking") : OrderAction(r.Order);
            float metres = Flat(objective - at) / 2.8f;
            MercUi.OrderReply(r.Name + ": " + action + (metres >= 1f ? ", " + metres.ToString("0") + " m" : ""), false);
            if (u != null) NpcWar.MercOrderWake(u);
        }

        internal static void OrderFocused(Record r, Vector3 objective)
        {
            OrderReceived(r, true, objective);
        }

        static string OrderAction(MercOrder o)
        {
            switch (o.Mode)
            {
                case MercOrder.Follow: return Loc.T("следую за вами", "following you");
                case MercOrder.Vehicle: return Loc.T("следую за техникой", "following your vehicle");
                case MercOrder.Stay: return Loc.T("занимаю позицию", "taking position");
                case MercOrder.Patrol: return Loc.T("патрулирую", "patrolling");
                case MercOrder.Perimeter: return Loc.T("охраняю периметр", "securing perimeter");
                case MercOrder.ManGun: return Loc.T("иду к пушке", "manning gun");
                case MercOrder.ManRadar: return Loc.T("иду к радару", "manning radar");
                default: return Loc.T("атакую", "attacking");
            }
        }

        // Called at the roster's existing 4 Hz, only pending local commands.
        static void OrderReceipts(float now)
        {
            FrameProf.S(FrameProf.S_MercReceiptT);
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (!r.Receipt.Pending) continue;
                if (r.Dead || r.Deserted || r.Order != r.ReceiptFor)
                { r.Receipt.Pending = false; continue; }
                MercUnit u = r.Unit;
                Vector3 at = u == null || u.Ai == null ? r.Receipt.Origin : u.Ai.transform.position;
                bool settled = !r.ReceiptFocus && NpcWar.MercOrderSettled(u);
                // A stolen gun is a refusal even if the fallback FOLLOW moved.
                bool taken = u != null && u.Ai != null && MercAA.IsOrder(r.Order)
                    && !MercAA.IsVehicle(r.Order) && !MercAA.CanApproach(MercAA.PostOf(u), u.Ai);
                if (taken) r.Receipt.Pending = false;
                if (taken || r.Receipt.Check(now, at, NpcWar.MercOrderShots(u), settled))
                    MercUi.OrderReply(r.Name + ": " + (taken ? Loc.T("пушка или радар занят", "gun or radar taken")
                        : NpcWar.MercOrderReason(u, r.ReceiptFocus)), true);
            }
            FrameProf.E(FrameProf.S_MercReceiptT);
        }
    }

    public static partial class NpcWar
    {
        static Fighter MercOrderFighter(MercUnit u)
        {
            if (u == null || u.Ai == null) return null;
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i].Merc == u && _squads[i].Men.Count != 0) return _squads[i].Men[0];
            return null;
        }

        internal static int MercOrderShots(MercUnit u)
        {
            Fighter f = MercOrderFighter(u);
            return f == null || f.Squad == null ? 0 : f.Squad.Shots;
        }

        internal static void MercOrderWake(MercUnit u)
        {
            Fighter f = MercOrderFighter(u);
            if (f == null) return;
            Mercs.MedicineCancel(u, Time.time);
            f.HasOrder = false; f.MoveDeadline = 0f; f.NextScan = 0f; f.NextLos = 0f;
            u.GoalFor = null; u.GoalLeg = -1; u.NextOrder = 0f;
            u.Sense.NextSense = 0f;
        }

        internal static bool MercOrderSettled(MercUnit u)
        {
            if (u == null || u.Ai == null || u.Deserting) return false;
            Vector3 me = u.Ai.transform.position;
            MercOrder o = u.Order;
            if (o.Mode == MercOrder.Follow || o.Mode == MercOrder.Vehicle)
                return (u.Ride.Carrier != null && o.Mode == MercOrder.Vehicle)
                    || (u.Owner != null && Flat(me - u.Owner.position) <= 22f);
            if (MercAA.IsOrder(o))
            {
                if (MercAA.IsVehicle(o)) return u.Ride.Carrier != null && u.Ride.Carrier.View == -Mathf.RoundToInt(o.Facing.x)
                    && u.Ride.Seat == Mathf.RoundToInt(o.Facing.z) - 1;
                MercAAPost post = MercAA.Held(MercAA.PostOf(u));
                return post != null && post.Ai == u.Ai;
            }
            if (o.Mode == MercOrder.Attack) return u.Attack.Phase == MercAttackRun.Holding;
            if (u.GoalFor != o) return false;
            return o.Mode == MercOrder.Perimeter ? Flat(me - u.Post) <= 5.6f
                : o.Mode == MercOrder.Patrol ? u.PauseUntil > Time.time : Flat(me - u.Goal) <= 5.6f;
        }

        internal static string MercOrderReason(MercUnit u, bool focus)
        {
            if (u == null || u.Ai == null) return Loc.T("ещё не развёрнут", "not deployed yet");
            if (u.Deserting) return Loc.T("покидаю отряд: нет оплаты", "deserting: unpaid");
            MercBrain b = u.Fight.Brain;
            if (b != null && (b.Mode == MercBrain.Retreating || b.Mode == MercBrain.Falling))
                return Loc.T("отхожу в укрытие: мало здоровья или отряд теряет бой", "falling back to cover: low health or losing fight");
            if (u.Ride.Carrier != null) return Loc.T("жду остановки техники для выхода", "waiting for vehicle to stop before dismounting");
            if (u.Fight.Out.Act == FightAct.Reload) return Loc.T("перезаряжаюсь в укрытии", "reloading in cover");
            if (u.Fight.Out.NoShot) return Loc.T("линия огня перекрыта своим", "friendly blocks firing lane");
            Fighter f = MercOrderFighter(u);
            NavMeshAgent agent = f == null ? null : Agent(f);
            // Read the existing path once at timeout; never calculate a path here.
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh
                || (!agent.pathPending && agent.pathStatus == NavMeshPathStatus.PathInvalid))
                return Loc.T("нет пути", "no path");
            if (!agent.pathPending && agent.pathStatus == NavMeshPathStatus.PathPartial)
                return Loc.T("цель недостижима", "target unreachable");
            if (focus && (f == null || !f.Sees)) return Loc.T("цель вне видимости или дальности огня", "target out of sight or weapon range");
            if (b != null && b.Fighting) return Loc.T("занимаю укрытие для выполнения приказа", "taking cover to carry out the order");
            return agent.pathPending ? Loc.T("путь ещё рассчитывается", "path still pending")
                : Loc.T("путь заблокирован, ищу обход", "path blocked, seeking another route");
        }
    }
}

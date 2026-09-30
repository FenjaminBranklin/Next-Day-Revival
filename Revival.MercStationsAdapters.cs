// Y S4: reuse the existing vehicle ride/gun path and mortar registry.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercRide
    {
        internal static void GunStations(List<MercStation> rows)
        {
            if (!VgsLook()) return;
            Component[] vehicles = VehicleScan.All();
            for (int i = 0; i < vehicles.Length; i++)
            {
                Component v = vehicles[i];
                if (v == null || (v.transform.position - Mercs.OwnerPosition).sqrMagnitude > MercStationPlan.Reach * MercStationPlan.Reach) continue;
                MercCarrier c = CarrierOf(MercCarrier.Ground, v.gameObject, v);
                if (c == null || c.View <= 0 || c.GunKind == MercCarrier.GunNone || !Alive(c)) continue;
                // Gun first, then every free passenger/crew place. Never driver.
                for (int k = 0; k <= c.Seats; k++)
                {
                    int seat = k == 0 ? c.GunSeat : k - 1;
                    if (seat <= 0 || seat >= c.Seats || (k > 0 && seat == c.GunSeat)) continue;
                    Vector3 at;
                    if (!StationPose(c, seat, out at)) continue;
                    MercStations.Add(-1, c, seat, -c.View, at, Loc.T(c.Ru, c.En) + " #" + c.View
                        + (seat == c.GunSeat ? Loc.T(" / наводчик", " / gunner") : Loc.T(" / место ", " / seat ") + seat));
                }
            }
        }

        internal static bool StationPose(MercCarrier c, int seat, out Vector3 at)
        {
            at = Vector3.zero;
            if (c == null || c.Go == null || c.SeatPoints == null || seat < 1 || seat >= c.SeatPoints.childCount) return false;
            at = c.SeatPoints.GetChild(seat).position;
            return true;
        }

        internal static bool StationFree(MercCarrier c, int seat) { return c != null && Alive(c) && SeatFree(c, seat, null); }

        internal static bool StationBlocked(MercUnit u)
        {
            MercCarrier c = u.Ride.AssignedCarrier;
            return c != null && (c.Go == null || !Alive(c)
                || !SeatFree(c, Mathf.RoundToInt(u.Order.Facing.z) - 1, u.Ride));
        }

        internal static bool StationOwned(MercCarrier c, int seat, List<Mercs.Record> sel)
        {
            for (int i = 0; i < sel.Count; i++)
            {
                MercUnit u = sel[i].Unit;
                if (u != null && MercAA.IsVehicle(u.Order) && -Mathf.RoundToInt(u.Order.Facing.x) == c.View
                    && Mathf.RoundToInt(u.Order.Facing.z) == seat + 1 && SeatFree(c, seat, u.Ride)) return true;
            }
            return false;
        }

        static MercCarrier Assigned(MercUnit u, float now)
        {
            MercSeat st = u.Ride;
            if (st.AssignedOrder != u.Order)
            { st.AssignedOrder = u.Order; st.AssignedCarrier = null; st.NextAssignedSearch = 0f; }
            if (st.AssignedCarrier != null && st.AssignedCarrier.Go != null) return st.AssignedCarrier;
            // Relog resolution uses the existing Photon view lookup, at 1 Hz
            // only until it resolves. An issued command already holds the cache.
            if (now < st.NextAssignedSearch) return null;
            st.NextAssignedSearch = now + 1f;
            st.AssignedCarrier = CarrierByView(MercCarrier.Ground, -Mathf.RoundToInt(u.Order.Facing.x));
            return st.AssignedCarrier;
        }
    }

    public static partial class ArtyBattery
    {
        internal static bool MercFree(int settlement, int seat)
        {
            Post p;
            return !_byId.TryGetValue(settlement, out p) || !Alive(seat == 0 ? p.Gunner : p.Operator);
        }
    }

    public static partial class Mortar
    {
        static int MercKey(Tube t)
        {
            if (t.MercKey != 0) return t.MercKey;
            if (Time.time < t.MercNextKeyAt) return 0;
            t.MercNextKeyAt = Time.time + 0.5f;
            int key;
            if (t.Mobile)
            {
                int view = PlayerHeli.ViewOf(t.Go.transform.root.gameObject);
                if (view <= 0) return 0;
                key = MercStationPlan.TubeKey(view, Int32.MinValue);
            }
            else key = MercStationPlan.TubeKey(Mathf.RoundToInt(t.Centre.x * 10f), Mathf.RoundToInt(t.Centre.z * 10f));
            t.MercKey = key;
            return key;
        }

        static Tube MercTube(int post)
        {
            int key = post - ((post - 100) & 1);
            Tube found = null;
            for (int i = 0; i < _tubes.Count; i++)
            {
                Tube t = _tubes[i];
                if (t.Go == null || MercKey(t) != key) continue;
                if (found != null) return null; // refuse hash collisions
                found = t;
            }
            return found;
        }

        internal static void MercStations(List<MercStation> rows)
        {
            for (int i = 0; i < _tubes.Count; i++)
            {
                Tube t = _tubes[i];
                if (t.Go == null || (t.Go.transform.position - NextDayRevival.Mercs.OwnerPosition).sqrMagnitude > MercStationPlan.Reach * MercStationPlan.Reach) continue;
                int key = MercKey(t);
                if (key == 0) continue;
                for (int seat = 0; seat < 2; seat++)
                {
                    Vector3 at; Quaternion rot;
                    if (!MercPose(key + seat, out at, out rot)) continue;
                    NextDayRevival.MercStations.Add(key + seat, null, -1, key, at,
                        Name() + " #" + key + (seat == 0 ? Loc.T(" / наводчик", " / gunner") : Loc.T(" / расчёт", " / crew")));
                }
            }
        }

        internal static bool MercPose(int post, out Vector3 at, out Quaternion rot)
        {
            at = Vector3.zero; rot = Quaternion.identity;
            Tube t = MercTube(post);
            if (t == null) return false;
            int seat = (post - 100) & 1;
            // Same stations as the native battery, beside its hull. No terrain
            // queries or transform hierarchy walks in the pose tick.
            at = t.Go.transform.TransformPoint(new Vector3(8f, 0f, -8f - seat * 4f));
            rot = t.Go.transform.rotation;
            return true;
        }

        internal static bool MercAvailable(int post)
        {
            Tube t = MercTube(post);
            return t != null && t != _aiming && !t.Holding
                && (!t.Mobile || !MercDriver(t)) && ArtyBattery.MercFree(t.SettlementId, (post - 100) & 1);
        }

        static bool MercDriver(Tube t)
        {
            if (Time.time < t.MercNextDriverAt) return t.MercDriverUp;
            t.MercNextDriverAt = Time.time + 0.25f;
            t.MercDriverUp = DriverAboard(t.Go);
            return t.MercDriverUp;
        }

        internal static void MercPrepare(int post)
        {
            Tube t = MercTube(post);
            if (t == null || !t.Mobile || MercDriver(t)) return;
            // A parked howitzer starts stowed. Crew orders open the weapon
            // through the existing slew, without selecting any fire target.
            t.WantPitch = Mathf.Max(ElevLow, t.WantPitch);
            t.Stowed = false;
        }
    }

    public static partial class NpcWar
    {
        internal static void MercStationWake(MercUnit u)
        {
            Fighter f = MercOrderFighter(u);
            if (f == null) return;
            u.Fight.Health = HealthFraction(f);
            if (u.Fight.Health < 0.35f) u.AARetreat = true;
            if (u.Fight.Health >= 0.5f) u.AARetreat = false;
            if (MercAA.IsVehicle(u.Order))
            {
                // Rebuild the existing boarding intents immediately, rather
                // than waiting for the roster's next 4 Hz step.
                MercRide.StepOwner(Time.time, Mercs.OwnerObject);
                if (u.Ride.Carrier == null) MercBoard(f, u, Time.time);
            }
            else MercPostStep(f, u, Time.time);
        }

        // Immediate orders still use M1/M2/M3 when badly hurt or when a blast
        // is imminent. Ordinary contact shapes a crouched sprint to the gun;
        // it never vetoes the order by keeping him in a rifle fight forever.
        static bool MercStationDuty(Fighter f, MercUnit u, float now)
        {
            if (!MercAA.IsOrder(u.Order) || u.Deserting) return false;
            Vector3 dangerAt;
            bool danger = MercDanger.Near(f.Tr.position, MercBrain.DangerRadius, now, out dangerAt);
            float health = HealthFraction(f);
            u.Fight.Health = health;
            if (health < 0.35f) u.AARetreat = true;
            if (health >= 0.5f) u.AARetreat = false;
            if (MercStationPlan.Retreat(health, u.AARetreat, danger))
            { MercAA.Release(u); return false; }
            if (MercAA.IsVehicle(u.Order))
            {
                if (u.Ride.Boarding == null)
                { if (MercRide.StationBlocked(u) && MercStations.Replace(u)) return true; return false; }
                MercBoard(f, u, now);
            }
            else MercPostStep(f, u, now);
            return true;
        }

        static void MercStationApproach(Fighter f, MercUnit u, Vector3 goal, float now)
        {
            // Reuse M1's already sampled cover: move through it only when it
            // gains ground toward the assigned seat. Never stop at the cover
            // for a rifle duel or require another physics query to obey.
            if (u.Sense.Count > 0 && u.Sense.Pick.Found)
            {
                Vector3 cover = u.Sense.Pick.Point.Pos;
                if ((cover - f.Tr.position).sqrMagnitude > 16f
                    && (cover - goal).sqrMagnitude + 16f < (f.Tr.position - goal).sqrMagnitude)
                    goal = cover;
            }
            MercMove(f, u, goal, true, now);
        }
    }
}

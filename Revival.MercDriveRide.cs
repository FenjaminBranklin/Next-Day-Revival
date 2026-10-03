// Z M5a: narrow seams into existing seat, weapon and merc order paths.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercRide
    {
        internal static int DriveView(GameObject go) { return ViewOf(go); }
        internal static MercCarrier DriveByView(int view) { return CarrierByView(MercCarrier.Ground, view); }
        internal static bool DriveAlive(MercCarrier c) { return Alive(c); }
        internal static bool DrivePlayer(MercCarrier c) { return c == null || c.Go == null || PlayerIn(c, 0); }
        internal static bool DriveUnclaimed(MercCarrier c)
        { return c != null && c.Kind == MercCarrier.Ground && !PlayerIn(c, 0) && Patrol.CrewedCount(c.Vgs) == 0; }
        internal static int DrivePassengerSeat(MercCarrier c, MercSeat self) { return PickSeat(c, self); }

        internal static void DrivePlayers(MercCarrier c, GameObject[] cache)
        {
            Array pass = c == null ? null : Passengers(c.Vgs);
            int n = 0;
            if (pass != null)
                for (int i = 0; i < pass.Length && n < cache.Length; i++)
                {
                    GameObject p = pass.GetValue(i) as GameObject;
                    if (p != null) cache[n++] = p;
                }
            for (int i = n; i < cache.Length; i++) cache[i] = null;
        }

        internal static bool DriveVehicle(MercUnit driver, out MercCarrier car, out MercDriveNative native)
        {
            car = null; native = null;
            if (!VgsLook()) return false;
            float best = 168f * 168f;
            Component[] list = VehicleScan.All();
            for (int i = 0; i < list.Length; i++)
            {
                Component v = list[i]; if (v == null) continue;
                float d = (v.transform.position - driver.Ai.transform.position).sqrMagnitude;
                if (d >= best || (v.transform.position - Mercs.OwnerPosition).sqrMagnitude > 168f * 168f) continue;
                MercCarrier c = CarrierOf(MercCarrier.Ground, v.gameObject, v);
                if (c == null || c.View <= 0 || c.Seats < 1 || MercDrive.Reserved(c)
                    || (MercDrive.NeedsEscortGun && (c.GunSeat <= 0 || c.Seats < 2 || Claimant(c, c.GunSeat, driver.Ride) != null)) || !Alive(c) || !DriveUnclaimed(c) || Claimant(c, 0, driver.Ride) != null) continue;
                Motion(c, Time.time); if (!Standing(c, LeaveSpeed)) continue;
                MercDriveNative n = MercDriveNative.Bind(c);
                if (n == null || !n.Ready || (n.Owner > 0 && n.Owner != Mercs.LocalActor && n.Owner != MercAA.MasterActor())) continue;
                car = c; native = n; best = d;
            }
            return car != null;
        }

        internal static bool DriveRifle(MercSeat st)
        {
            // Only open passenger places. Driver and enclosed cabin crew keep
            // their heads down; the assigned gunner uses the established gun AI.
            return st != null && st.Carrier != null && st.Carrier.Kind == MercCarrier.Ground
                && !st.Hidden && !st.Gunner && st.Seat > 0 && !st.Carrier.Closed
                && st.DriveEscort;
        }
    }

    internal static partial class Mercs
    {
        internal static void OrderDrive(Vector3 target)
        { MercDrive.Start(Willing(SquadSelection(), "DRIVE"), target); }

        internal static void GiveDrive(Record r, MercCarrier c, int seat, Vector3 target)
        {
            MercOrder o = new MercOrder(); o.Mode = MercOrder.Drive;
            o.Points = new Vector3[] { target }; o.Facing = new Vector3(-c.View, 0f, seat + 1);
            o.Scene = MapScene.Current; o.IssuedAt = Time.time;
            r.Order = o; r.Receipt.Pending = false;
            MercUnit u = r.Unit;
            MercAA.Release(u); u.Order = o; u.Rally = false; u.Chasing = false; u.Attack.Reset();
            u.Ride.Boarding = null; u.Ride.NextBoard = 0f; u.NextOrder = 0f; u.GoalFor = null;
            NpcWar.MercOrderWake(u);
        }

        internal static void EndDrive(List<Record> riders, MercCarrier c)
        {
            for (int i = 0; i < riders.Count; i++)
            {
                Record r = riders[i];
                if (r.Order.Mode != MercOrder.Drive) continue;
                Vector3 at = r.Unit == null || r.Unit.Ai == null ? OwnerPosition : r.Unit.Ai.transform.position;
                MercOrder o = new MercOrder(); o.Mode = MercOrder.Stay;
                // The normal ride loop dismounts when stopped. Hold nearby
                // afterwards; do not FOLLOW-warp a returning squad to its owner.
                o.Points = new Vector3[] { c == null || c.Root == null ? at : c.Root.position + c.Root.right * (c.Radius + 8f) };
                o.Scene = MapScene.Current; o.K = i; o.N = riders.Count; r.Order = o;
                if (r.Unit != null) { r.Unit.Order = o; r.Unit.NextOrder = 0f; NpcWar.MercOrderWake(r.Unit); }
            }
            SaveStationOrders(riders);
        }
    }

    public static partial class NpcWar
    {
        static void MercDriveRifle(Fighter f, MercUnit u, float now)
        {
            f.HasOrder = false;
            EnsureArmed(f, now);
            if (!MercMayEngage(u, now)) { f.Target = null; f.Sees = false; Quiet(f, true); return; }
            Acquire(f, now); // Existing profile reach, LOS budget and ally guards.
            if (Reloading(f)) { Quiet(f, true); return; }
            if (f.Target != null && f.Sees && f.Armed && Flat(f.Target.position - f.Tr.position) <= MercReachUnits(f))
                Fire(f, now); // Real profile weapon, muzzle clearance and native shots.
            else Quiet(f, false);
        }
    }
}

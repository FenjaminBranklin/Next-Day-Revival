// Air bridge AB2: durable departure and destination materialisation.
using System;
using System.Globalization;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class AirRegistry
    {
        const int OpDepart = 6, OpArrive = 7, OpConfirm = 8, OpReady = 9;
        static Craft _depart;
        static AirRoute _route;
        static float _departureSpeed;
        static int _departureActor;
        static bool _departureSnap;
        static readonly CultureInfo TravelCI = CultureInfo.InvariantCulture;
        internal static bool CanTravel { get { return _mode == Mode.Live && _haveBook && AirBridgeCargo.Installed; } }

        internal static bool IsArriving(GameObject go)
        {
            if (AirBridge.Seating(go)) return true;
            if (go == null || !_haveBook) return false;
            for (int i = 0; i < _crafts.Length; i++)
                if (_crafts[i].Go == go)
                {
                    AirCraft a = _book.Crafts[i];
                    return a.State == "in_transit" && a.Phase == "arriving" && a.Room == _room
                        && a.Tok == _crafts[i].Tok && a.Lease > ServerNow() + AirPlan.LeaseGuard;
                }
            return false;
        }

        internal static bool HoldingArrival(GameObject go)
        {
            if (IsArriving(go)) return true;
            if (go == null || !_haveBook) return false;
            for (int i = 0; i < _crafts.Length; i++)
            {
                AirCraft a = _book.Crafts[i];
                if (_crafts[i].Go != go || a.Phase != "confirmed" || a.Lease <= ServerNow()) continue;
                for (int j = 0; j < a.PeopleCount; j++) if (a.Seats[j] == -1 && !a.Seated[j])
                    return ServerNow() < a.Since + AirTravel.RecoverSeconds;
            }
            return false;
        }

        static bool HasTransit()
        {
            for (int i = 0; i < _book.Crafts.Length; i++) if (_book.Crafts[i].State == "in_transit") return true;
            return false;
        }

        internal static void RequestDeparture(GameObject go, int actor, float speed)
        {
            if (_mode != Mode.Live || !RevivalTroopInsertion.MasterClient() || _depart != null || !AirBridgeCargo.Installed) return;
            for (int i = 0; i < _crafts.Length; i++)
            {
                Craft c = _crafts[i];
                if (c.Go != go || c.Restoring || c.Tok < 0 || !AirBridge.Pilot(go, actor)) continue;
                Read(c, Time.realtimeSinceStartup);
                float ground;
                if (!AirBridge.Floor(c.Pos, out ground)) return;
                AirRoute r = AirTravel.Gate(c.Id, _region, c.Pos.x, c.Pos.z, c.Yaw, (c.Pos.y - ground) / 2.8f);
                if (r == null || !c.InAir || c.Fuel01 + .0001f < r.Required) return;
                _depart = c; _route = r; _departureSpeed = Mathf.Clamp(speed, 0, 1000);
                _departureActor = actor; _departureSnap = false;
                return;
            }
        }

        static string TravelText(string op, AirCraft c)
        {
            return AirBook.Magic + "\nseq=" + (++_seq).ToString(TravelCI) + "\nop=" + op + "\nid=" + c.Id
                + "\nroom=" + _room + "\nregion=" + _region + "\ntok=" + c.Tok.ToString(TravelCI)
                + "\ntransfer=" + c.Transfer + "\n";
        }

        static void TravelStep(float now, bool master)
        {
            // Every passenger uses their own authenticated backend connection.
            string sid = AirBridge.LocalSid;
            if (_link.Busy) return;
            for (int i = 0; i < _crafts.Length; i++)
            {
                Craft c = _crafts[i]; AirCraft a = _book.Crafts[i];
                if (a.Region != _region || a.Transfer.Length == 0) continue;
                for (int j = 0; j < a.PeopleCount; j++) if (a.People[j] == sid)
                {
                    if (a.State == "in_transit" && !a.Ready[j])
                    {
                        Begin(OpReady, c, TravelText("ready", a), now); return;
                    }
                    if ((a.Phase == "confirmed" || a.Phase == "arriving") && a.Room == _room && !a.Seated[j]
                        && a.Lease > ServerNow() + AirPlan.LeaseGuard)
                    {
                        GameObject go = RoomObject(c);
                        if (go != null && AirBridge.Seat(go, a, j))
                        { Begin(OpReady, c, TravelText("ready", a) + "seated=1\n", now); return; }
                    }
                }
                if (!master || a.State != "in_transit") continue;
                if (a.Phase == "arriving" && a.Room == _room && a.Lease > ServerNow() + AirPlan.LeaseGuard)
                {
                    if (c.Go == null)
                    {
                        // A retry/master switch adopts the reserved view instead
                        // of creating another scene object.
                        GameObject go = RoomObject(c);
                        bool fresh = go == null;
                        if (fresh) go = Build(c, a, false);
                        if (go == null) return;
                        go.transform.position = new Vector3(a.X, a.Y, a.Z);
                        go.transform.rotation = Quaternion.Euler(0, a.Yaw, 0);
                        if (c.An2)
                        {
                            PlayerAn2.SetFuel(go, a.Fuel * PlayerAn2.Capacity); PlayerAn2.RestoreWear(go, a.Wear);
                            An2Repair.TravelRestore(go, a.Parts);
                        }
                        else HeliCondition.Restore(go, a.Parts, a.Fuel * HeliCondition.Capacity);
                        Track(c, go, a.Tok); SetRoomView(c, c.View);
                        RestoreCargo(c, a);
                    }
                    bool seated = a.PeopleCount > 0;
                    for (int j = 0; j < a.PeopleCount; j++) if (!a.Seated[j]) seated = false;
                    if (!c.Restoring && seated)
                    { Begin(OpConfirm, c, TravelText("confirm", a), now); return; }
                    continue;
                }
                bool recovery = ServerNow() >= a.Since + AirTravel.RecoverSeconds;
                bool pilot = false;
                for (int j = 0; j < a.PeopleCount; j++) if (a.Seats[j] == -1 && a.Ready[j]) pilot = true;
                if (!pilot && !recovery) continue;
                if (a.Phase == "arriving" && a.Lease > ServerNow()) continue;
                AirRoute r = AirTravel.Find(a.Route);
                if (r == null) continue;
                if (r.To == "SWAMP" && !AirLanding.PlatformReady) continue;
                Vector3 at = new Vector3(recovery ? r.ParkX : r.X, 0, recovery ? r.ParkZ : r.Z);
                float floor;
                if (r.To == "PRIM" && !recovery) floor = 42f;
                else if (recovery && r.ParkY >= 0) floor = r.ParkY;
                else if (!AirBridge.Floor(at, out floor)) continue;
                Begin(OpArrive, c, TravelText("arrive", a) + "floor=" + floor.ToString("0.##", TravelCI) + "\n", now);
                return;
            }
            if (!master || _depart == null) return;
            Craft d = _depart;
            if (d.Go == null || _region != _route.From || !AirBridge.Pilot(d.Go, _departureActor))
            { _depart = null; return; }
            AirCraft rec = _book.Get(d.Id);
            if (!rec.Present || rec.Tok != d.Tok || rec.Room != _room) { _depart = null; return; }
            Read(d, now); float groundY;
            if (!AirBridge.Floor(d.Pos, out groundY) || !_route.Contains(d.Pos.x, d.Pos.z, d.Yaw, (d.Pos.y - groundY) / 2.8f)
                || !d.InAir || d.Restoring) { _depart = null; return; }
            // Snapshot fuel/cargo first. The depart snapshot is captured again
            // after its acknowledgement, with cargo interactions locked.
            d.CargoN = d.An2 ? ReadBombs(d) : ReadHold(d);
            if (d.CargoN < 0) { _depart = null; AirBridge.Note("The cargo hold is not ready."); return; }
            string manifest = AirBridge.Manifest(d.Go, _departureActor);
            if (manifest == null) { _depart = null; AirBridge.Note("Waiting for the passenger list."); return; }
            string snap = AirText.Snap(++_seq, d.Id, d.Tok, _room, true, d.Pos.x, d.Pos.y, d.Pos.z, d.Yaw, d.Fuel01,
                d.Parts, AirPlan.DamageOf(d.Parts, d.An2 ? An2Repair.AllParts : HeliCondition.AllParts),
                d.Slot, d.Item, d.Count, d.Cond, d.Clip, d.Water, d.Energy, d.CargoN)
                + "wear=" + d.Wear.ToString("0.####", TravelCI) + "\n";
            if (!_departureSnap)
            { _departureSnap = true; Begin(OpSnap, d, snap, now); return; }
            string text = snap.Replace("op=snap\n", "op=depart\n") + "route=" + _route.Id
                + "\nagl=" + ((d.Pos.y - groundY) / 2.8f).ToString("0.##", TravelCI)
                + "\nspeed=" + _departureSpeed.ToString("0.###", TravelCI) + "\npeople=" + manifest + "\n";
            float power, throttle; bool running;
            if (d.An2) PlayerAn2.TravelEngine(d.Go, out power, out running, out throttle);
            else { PlayerHeli.TravelEngine(d.Go, out power, out running); throttle = 1f; }
            text += "power=" + power.ToString("0.###", TravelCI) + "\nrunning=" + (running ? "1" : "0")
                + "\nthrottle=" + throttle.ToString("0.###", TravelCI) + "\n";
            Begin(OpDepart, d, text, now); _depart = null;
        }

        static void RestoreCargo(Craft c, AirCraft rec)
        {
            if (c.An2)
            {
                int bombs = 0, kg = An2Bombs.LoadKg;
                for (int i = 0; i < rec.CargoCount; i++)
                    if (rec.Item[i] == An2Bombs.ItemId) { bombs = rec.Count[i]; kg = Mathf.RoundToInt(rec.Cond[i]); }
                An2Bombs.Restore(c.Go, bombs, kg);
                return;
            }
            int n = rec.CargoCount;
            Array.Copy(rec.Item, c.RItem, n); Array.Copy(rec.Count, c.RCount, n); Array.Copy(rec.Cond, c.RCond, n);
            Array.Copy(rec.Clip, c.RClip, n); Array.Copy(rec.Water, c.RWater, n); Array.Copy(rec.Energy, c.REnergy, n);
            c.RN = n; c.RAt = 0; c.RFree = -1; c.Restoring = true; c.RestoreAfter = Time.realtimeSinceStartup + 2f;
            c.RClear = true; c.RClearAt = 0;
        }

        static void TravelFinish(int kind, Craft c, string result)
        {
            if (!result.StartsWith("ok", StringComparison.Ordinal))
                RevivalPlugin.L.LogWarning("AirBridge: operation " + kind + " -> " + result);
            _nextPoll = 0f;
            if (kind == OpConfirm && c != null && result.StartsWith("ok", StringComparison.Ordinal)) c.Gate.Reset();
        }

        internal static bool CargoLocked(GameObject go)
        {
            if (go == null) return false;
            if (AirBridge.Departing(go) || AirBridge.Seating(go)) return true;
            if (_depart != null && _depart.Go == go) return true;
            if (_opKind == OpDepart && _opCraft != null && _opCraft.Go == go) return true;
            if (IsArriving(go)) return true;
            for (int i = 0; i < _crafts.Length; i++)
                if (_crafts[i].Go == go && (!_book.Crafts[i].Present || _crafts[i].Restoring)) return true;
            return false;
        }
    }
}

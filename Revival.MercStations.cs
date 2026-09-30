// Y S4: command-time discovery, explicit seats and nearby station picker.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercStation
    {
        internal int Post = -1, Seat = -1, Group;
        internal MercCarrier Carrier;
        internal Vector3 At;
        internal string Name, Label;
        internal bool Free;
        internal int LabelKey = Int32.MinValue;
    }

    internal static class MercStations
    {
        internal static readonly List<MercStation> Rows = new List<MercStation>(64);
        static readonly MercStationChoice[] Choices = new MercStationChoice[128];
        static readonly List<Mercs.Record> Replacement = new List<Mercs.Record>(1);

        // No polling discovery. Called only by an order, opening the picker or
        // its refresh button. Vehicle components are cached by MercRide.
        internal static void Discover(bool radar)
        {
            Rows.Clear();
            for (int i = 0; i < 14; i++)
            {
                if ((i == MercAA.Radar) != radar || i == 11) continue;
                Vector3 at; Quaternion rot;
                if (!MercAA.Pose(i, out at, out rot)) continue;
                Flak.Gun gun = i == MercAA.Radar ? null : Flak.ByIndex(i >= 7 ? i - 7 : i);
                string name = radar ? Loc.T("Консоль радара", "Radar console")
                    : (gun.ShortRange ? "ZU-23" : "52-K") + " #" + (gun.Index + 1)
                      + (i >= 7 ? Loc.T(" / заряжающий", " / loader") : Loc.T(" / наводчик", " / gunner"));
                Add(i, null, -1, radar ? 5 : (i >= 7 ? i - 7 : i) + 1, at, name);
            }
            if (!radar) { Mortar.MercStations(Rows); MercRide.GunStations(Rows); }
            Refresh();
        }

        internal static void Add(int post, MercCarrier carrier, int seat, int group, Vector3 at, string name)
        {
            if ((at - Mercs.OwnerPosition).sqrMagnitude > MercStationPlan.Reach * MercStationPlan.Reach || Rows.Count >= Choices.Length) return;
            MercStation row = new MercStation();
            row.Post = post; row.Carrier = carrier; row.Seat = seat; row.Group = group; row.At = at; row.Name = name;
            Rows.Add(row);
        }

        internal static bool Matches(MercOrder o, MercStation row)
        {
            if (!MercAA.IsOrder(o)) return false;
            return row.Carrier == null ? !MercAA.IsVehicle(o) && Mathf.RoundToInt(o.Facing.x) == row.Post + 1
                : MercAA.IsVehicle(o) && -Mathf.RoundToInt(o.Facing.x) == row.Carrier.View && Mathf.RoundToInt(o.Facing.z) == row.Seat + 1;
        }

        internal static void Refresh()
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                MercStation row = Rows[i];
                Vector3 at; Quaternion rot;
                bool live = row.Carrier == null ? MercAA.Pose(row.Post, out at, out rot)
                    : MercRide.StationPose(row.Carrier, row.Seat, out at);
                if (live) row.At = at;
                row.Free = live && (row.Carrier == null ? MercAA.CanApproach(row.Post, null)
                    : MercRide.StationFree(row.Carrier, row.Seat));
                List<Mercs.Record> roster = Mercs.Roster;
                for (int j = 0; j < roster.Count && row.Free; j++)
                    if (!roster[j].Dead && !roster[j].Deserted && Matches(roster[j].Order, row)) row.Free = false;
                int metres = Mathf.RoundToInt((row.At - Mercs.OwnerPosition).magnitude / 2.8f);
                int key = metres * 8 + Loc.Lang() * 2 + (row.Free ? 1 : 0);
                if (row.LabelKey != key)
                {
                    row.LabelKey = key;
                    row.Label = row.Name + "  " + metres + " m  " + (row.Free ? Loc.T("свободно", "free") : Loc.T("занято", "occupied"));
                }
            }
        }

        internal static void Order(bool radar, int preferred)
        {
            List<Mercs.Record> sel = Mercs.StationSelection(radar);
            if (sel.Count == 0) return;
            for (int i = 0; i < Rows.Count; i++)
            {
                MercStation row = Rows[i];
                // A selected merc can keep his own seat; somebody else's
                // pending order also reserves a seat before arrival.
                bool own = false;
                for (int j = 0; j < sel.Count; j++) if (Matches(sel[j].Order, row)) { own = true; break; }
                Choices[i].Group = row.Group;
                Choices[i].Distance = (row.At - Mercs.OwnerPosition).sqrMagnitude;
                Choices[i].Free = row.Free || (own && (row.Carrier == null
                    ? PostOwned(row.Post, sel) : MercRide.StationOwned(row.Carrier, row.Seat, sel)));
                Choices[i].Used = false;
            }
            for (int i = 0; i < sel.Count; i++)
            {
                int seat = MercStationPlan.Take(Choices, Rows.Count, preferred);
                if (seat < 0)
                {
                    MercUi.OrderReply(sel[i].Name + Loc.T(": нет свободного места в радиусе 60 м", ": no free seat within 60 m"), true);
                    continue;
                }
                Mercs.GiveStation(sel[i], Rows[seat], radar);
            }
            Mercs.SaveStationOrders(sel);
        }

        static bool PostOwned(int post, List<Mercs.Record> sel)
        {
            if (!MercAA.AvailableForOrder(post)) return false;
            MercAAPost held = MercAA.Held(post);
            if (held == null) return true;
            for (int i = 0; i < sel.Count; i++) if (sel[i].Unit != null && sel[i].Unit.Ai == held.Ai) return true;
            return false;
        }

        // Seat loss is exceptional. Search the command's cached stations at
        // most twice a second; only a successful replacement creates an order.
        internal static bool Replace(MercUnit u)
        {
            if (Time.time < u.StationRetryAt) return false;
            u.StationRetryAt = Time.time + 0.5f;
            bool radar = u.Order.Mode == MercOrder.ManRadar;
            int best = -1; float distance = MercStationPlan.Reach * MercStationPlan.Reach;
            List<Mercs.Record> roster = Mercs.Roster;
            Mercs.Record record = null;
            for (int j = 0; j < roster.Count; j++) if (roster[j].Unit == u) { record = roster[j]; break; }
            if (record == null) return false;
            for (int i = 0; i < Rows.Count; i++)
            {
                MercStation row = Rows[i];
                if ((row.Post == MercAA.Radar) != radar || Matches(u.Order, row)) continue;
                Vector3 at; Quaternion rot;
                bool live = row.Carrier == null ? MercAA.Pose(row.Post, out at, out rot)
                    : MercRide.StationPose(row.Carrier, row.Seat, out at);
                if (!live || !(row.Carrier == null ? MercAA.CanApproach(row.Post, u.Ai) : MercRide.StationFree(row.Carrier, row.Seat))) continue;
                bool reserved = false;
                for (int j = 0; j < roster.Count; j++)
                    if (roster[j] != record && !roster[j].Dead && !roster[j].Deserted && Matches(roster[j].Order, row)) { reserved = true; break; }
                if (reserved) continue;
                float d = (at - Mercs.OwnerPosition).sqrMagnitude;
                if (d < distance) { distance = d; best = i; row.At = at; }
            }
            if (best < 0) return false;
            Mercs.GiveStation(record, Rows[best], radar);
            Replacement.Clear(); Replacement.Add(record); Mercs.SaveStationOrders(Replacement);
            return true;
        }
    }

    internal static partial class Mercs
    {
        internal static List<Record> StationSelection(bool radar)
        {
            List<Record> sel = Willing(Selection(), radar ? "MAN RADAR" : "MAN GUN");
            // Preserve already staffed guns when ordering a group to the one
            // radar console. Best free specialist first; injured men last.
            for (int i = 0; i < sel.Count; i++)
                for (int j = i + 1; j < sel.Count; j++)
                    if (StationRank(sel[j], radar) > StationRank(sel[i], radar))
                    { Record swap = sel[i]; sel[i] = sel[j]; sel[j] = swap; }
            return sel;
        }
        static float StationRank(Record r, bool radar)
        {
            MercUnit u = r.Unit;
            return (u == null ? 0f : radar ? u.Grade : u.AAGunner + u.Grade)
                - (radar && MercAA.IsOrder(r.Order) ? 100f : 0f) - (r.Hp < 0.35f ? 1000f : 0f);
        }
        internal static void SaveStationOrders(List<Record> sel) { SendOrders(sel); }
        internal static void GiveStation(Record r, MercStation seat, bool radar)
        {
            MercOrder o = new MercOrder();
            o.Mode = radar ? MercOrder.ManRadar : MercOrder.ManGun;
            o.Points = new Vector3[] { seat.At };
            o.Facing = seat.Carrier == null ? new Vector3(seat.Post + 1, 0f, 0f)
                : new Vector3(-seat.Carrier.View, 0f, seat.Seat + 1);
            o.Scene = MapScene.Current; o.IssuedAt = Time.time;
            r.Order = o;
            if (seat.Post >= 100) Mortar.MercPrepare(seat.Post);
            if (r.Unit != null)
            {
                MercUnit u = r.Unit;
                MercAA.Release(u); u.Order = o; u.Rally = false; u.Chasing = false;
                u.NextOrder = 0f; u.AANextSend = 0f; u.GoalFor = null;
                u.Ride.Boarding = null; u.Ride.NextBoard = 0f;
                u.Ride.AssignedOrder = o; u.Ride.AssignedCarrier = seat.Carrier;
                u.Attack.Reset(); u.Sense.NextPick = 0f;
            }
            OrderReceived(r, false, seat.At);
            NpcWar.MercStationWake(r.Unit);
        }
    }

    internal static partial class MercUi
    {
        static bool _stationRadar;
        static float _stationRefreshAt;
        static Vector2 _stationScroll;

        static void OpenStations(bool radar)
        {
            _stationRadar = radar; MercStations.Discover(radar);
            _listOpen = true; _listTab = 2; _stationRefreshAt = Time.time + 0.5f;
        }

        static void StationRows(Rect r)
        {
            if (Time.time >= _stationRefreshAt)
            { _stationRefreshAt = Time.time + 0.5f; MercStations.Refresh(); }
            GUI.Label(new Rect(14f, 44f, r.width - 28f, 24f), Loc.T("Выбранный расчёт: все места этой пушки, затем соседние. Радиус 60 м.",
                "Selected mercs: fill this gun, then nearby guns. Range 60 m."), _label);
            if (ButtonColored(new Rect(14f, 74f, 210f, 28f), Loc.T("Занять ближайшую", "Man nearest"), Green, true))
            { MercStations.Discover(_stationRadar); MercStations.Order(_stationRadar, 0); }
            if (ButtonColored(new Rect(230f, 74f, 160f, 28f), Loc.T("Обновить список", "Refresh list"), Grey, true)) MercStations.Discover(_stationRadar);
            if (ButtonColored(new Rect(396f, 74f, 140f, 28f), _stationRadar ? Loc.T("Пушки", "Guns") : Loc.T("Радар", "Radar"), Grey, true))
            { _stationRadar = !_stationRadar; MercStations.Discover(_stationRadar); }
            Rect view = new Rect(10f, 112f, r.width - 20f, r.height - 124f);
            Rect content = new Rect(0f, 0f, view.width - 24f, Mathf.Max(view.height - 2f, MercStations.Rows.Count * 34f));
            _stationScroll = GUI.BeginScrollView(view, _stationScroll, content);
            for (int i = 0; i < MercStations.Rows.Count; i++)
            {
                MercStation row = MercStations.Rows[i];
                if (ButtonColored(new Rect(4f, i * 34f, content.width - 8f, 30f), row.Label, row.Free ? Green : Grey, row.Free))
                { MercStations.Refresh(); MercStations.Order(_stationRadar, row.Group); }
            }
            if (MercStations.Rows.Count == 0) GUI.Label(new Rect(4f, 4f, content.width, 30f), Loc.T("Нет пушек или радара поблизости.", "No guns or radar nearby."), _label);
            GUI.EndScrollView();
        }
    }
}

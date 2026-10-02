// Offline doubles for the production A S6 owner adapter and GiveStation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using NextDayRevival;

namespace UnityEngine
{
    class GameObject { }
    class Component { }
    struct Quaternion { }
    struct Vector3
    {
        internal float x, y, z;
        internal Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        internal static Vector3 zero { get { return new Vector3(); } }
        internal float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
    }
    static class Mathf { internal static int RoundToInt(float v) { return (int)Math.Round(v); } }
    static class Time { internal static float time; }
}
namespace NextDayRevival
{
    class MercOrder
    {
        internal const int Follow = 0, ManGun = 5, ManRadar = 6, Stay = 1, Vehicle = 4, Attack = 7;
        internal int Mode;
        internal Vector3[] Points;
        internal Vector3 Facing;
        internal string Scene;
        internal float IssuedAt;
        internal bool Survive, MoveNear;
        internal Vector3 Centre { get { return Points == null ? Vector3.zero : Points[0]; } }
        internal MercOrder For(int slot, int count) { return this; }
        internal static MercOrder FollowMe() { return new MercOrder(); }
    }
    class DownState { internal bool Down, Final; }
    class RaidState { internal MercOrder Cover, Previous; }
    class Attack { internal void Reset() { } }
    class Sense { internal float NextPick, PickAt; }
    // A S4 move order (composed in 6.67): Give resets the cover reservation.
    class CoverField { }
    static class MercCoverService { internal static CoverField Field; }
    class MoveRun { internal void Reset(CoverField field, int id) { } }
    class Ride
    {
        internal object Boarding; internal float NextBoard;
        internal MercOrder AssignedOrder; internal MercCarrier AssignedCarrier;
    }
    class MercUnit
    {
        internal Component Ai = new Component(); internal MercOrder Order;
        internal bool Deserting, Rally, Chasing;
        internal int AAGunner; internal float NextOrder, AANextSend;
        internal object GoalFor;
        internal Ride Ride = new Ride(); internal Attack Attack = new Attack(); internal Sense Sense = new Sense();
        internal MoveRun Move = new MoveRun(); internal int Id;
    }
    class MercCarrier { internal int View; }
    class MercStation { internal int Post = -1, Seat; internal MercCarrier Carrier; internal Vector3 At; }
    class MercAAPost { internal Component Ai; }
    static class MercStations { // PRODUCTION_MATCHES
    }
    static class MercAA
    {
        internal const int Radar = 4;
        internal static readonly bool[] Blocked = new bool[7];
        internal static readonly Component[] Occupant = new Component[7];
        internal static int Releases;
        internal static bool IsOrder(MercOrder o) { return o.Mode == MercOrder.ManGun || o.Mode == MercOrder.ManRadar; }
        internal static bool IsVehicle(MercOrder o) { return o.Mode == MercOrder.ManGun && o.Facing.x < 0f; }
        internal static bool Pose(int p, out Vector3 at, out Quaternion rot)
        {
            at = new Vector3(p * 400f, p == Radar ? 43f : 0f, 0f); rot = new Quaternion();
            return p == Radar ? TowerRadar.Built : Flak.ByIndex(p) != null;
        }
        internal static bool CanApproach(int post, Component ai)
        { return !Blocked[post] && (Occupant[post] == null || Occupant[post] == ai); }
        internal static void Release(MercUnit u)
        {
            Releases++;
            for (int p = 0; p < 7; p++) if (Occupant[p] == u.Ai) Occupant[p] = null;
        }
    }
    static class Flak
    {
        internal class Gun { internal bool Town, ShortRange; }
        internal static Gun[] Guns = new Gun[7];
        internal static Gun ByIndex(int i) { return Guns[i]; }
    }
    static class TowerRadar
    {
        internal static bool Built = true;
        internal static Vector3 TowerPoint(Vector3 p) { return p; }
    }
    static class MapScene { internal static string Current = "East"; }
    static class Loc { internal static string T(string ru, string en) { return en; } }
    static class FrameProf
    {
        internal const int S_MercAirfieldT = 227;
        internal static int Starts, Ends;
        internal static void S(int slot) { Starts++; }
        internal static void E(int slot) { Ends++; }
    }
    static class MercUi { internal static void OrderReply(string s, bool error) { } }
    static class NpcWar { internal static int Wakes; internal static void MercStationWake(MercUnit u) { Wakes++; } }
    static class Mortar { internal static void MercPrepare(int post) { } }
    internal static partial class Mercs
    {
        internal class Profile { internal int AAGunner; }
        internal class Record
        {
            internal MercUnit Unit = new MercUnit(); internal MercOrder Order = MercOrder.FollowMe();
            internal bool Dead, Deserted, Unpaid, Selected;
            internal string ProfileId = ""; internal DownState Down = new DownState(); internal RaidState Raid = new RaidState();
        }
        static readonly List<Record> _roster = new List<Record>(10);
        internal static GameObject OwnerObject = new GameObject(); internal static Vector3 OwnerPosition;
        internal static int Saves;
        internal static Profile ProfileById(string id) { return null; }
        // PRODUCTION_GIVE
        static void SendOrders(List<Record> list) { Saves++; }
        static void OrderReceived(Record r, bool focus, Vector3 p) { }
        internal static void SaveStationOrders(List<Record> list) { Saves++; }
        // PRODUCTION_GIVE_STATION
        internal static List<Record> Roster { get { return _roster; } }
        internal static void TestTick(float t) { Time.time = t; AirDefenceTick(t); }
        internal static void Reset()
        {
            _defenceActive = false; Defence.Clear(); _roster.Clear(); Saves = 0;
            OwnerObject = new GameObject(); OwnerPosition = Vector3.zero;
            TowerRadar.Built = true; MapScene.Current = "East"; Time.time = 0f;
            for (int p = 0; p < 7; p++)
            {
                MercAA.Blocked[p] = false; MercAA.Occupant[p] = null;
                Flak.Guns[p] = p == 4 ? null : new Flak.Gun();
                if (p != 4) { Flak.Guns[p].Town = p == 2 || p == 3; Flak.Guns[p].ShortRange = p == 5; }
            }
        }
        internal static Record Add(int trait)
        { Record r = new Record(); r.Unit.AAGunner = trait; r.Unit.Order = r.Order; _roster.Add(r); return r; }
    }
}
class Check
{
    static int checks, failures;
    static void Ok(bool condition, string name)
    { checks++; if (!condition) { failures++; Console.WriteLine("FAIL: " + name); } }
    static int Post(Mercs.Record r) { return MercAA.IsOrder(r.Order) ? (int)r.Order.Facing.x - 1 : -1; }
    static Mercs.Record At(int post)
    { foreach (Mercs.Record r in Mercs.Roster) if (!r.Dead && Post(r) == post) return r; return null; }
    static void Unique()
    {
        int[] seen = new int[7];
        foreach (Mercs.Record r in Mercs.Roster)
            if (!r.Dead && Post(r) >= 0) { seen[Post(r)]++; Ok(Post(r) != 2 && Post(r) != 3, "no town/loader seats"); }
        for (int p = 0; p < 7; p++) Ok(seen[p] <= 1, "one merc per post " + p);
    }
    static int Main()
    {
        Mercs.Reset();
        int[] traits = { 0, 50, 10, 40, 25, 0 };
        for (int i = 0; i < 6; i++) Mercs.Add(traits[i]);
        Mercs.Roster[0].Selected = true; // Whole squad despite single selection.
        Mercs.ToggleAirDefence();
        Ok(Mercs.AirDefenceActive, "toggle active immediately"); Unique();
        Ok(At(0) != null && At(1) != null && At(6) != null && At(4) != null && At(5) != null, "three 52-K, radar and ZU staffed");
        Ok(At(0).Unit.AAGunner == 50 && At(1).Unit.AAGunner == 40 && At(6).Unit.AAGunner == 25, "best specialists to heavy guns");
        Ok(At(4).Unit.AAGunner == 0 && At(5).Unit.AAGunner == 10, "radar leaves remaining specialist for ZU");
        Ok(Mercs.Roster[5].Order.Mode == MercOrder.Follow, "overflow follows");
        Ok(At(6).Order.Points[0].x == 2400f, "whole airfield even beyond old seat proximity limits");
        Ok(NpcWar.Wakes == 5, "every post immediately wakes real station path");
        MercOrder stable = At(0).Order; int saves = Mercs.Saves;
        Mercs.TestTick(0.5f); Ok(At(0).Order == stable && Mercs.Saves == saves, "stable posts keep identity and do not save each tick");
        Mercs.Record dead = At(0); dead.Dead = true;
        Mercs.TestTick(0.6f); Ok(At(0) == null, "casualty discovery throttled to 2 Hz");
        Mercs.TestTick(1f); Ok(At(0) == Mercs.Roster[5], "follower replaces dead gunner"); Unique();
        Mercs.ToggleAirDefence(); Ok(!Mercs.AirDefenceActive, "second click releases automation");
        foreach (Mercs.Record r in Mercs.Roster) if (!r.Dead) Ok(Post(r) < 0, "release every surviving station");
        Mercs.TestTick(2f); Ok(At(0) == null, "released duty cannot reman");

        // Every squad size and scarcity uses 52-K > radar > one ZU priority.
        for (int size = 1; size <= 10; size++)
        {
            Mercs.Reset(); for (int i = 0; i < size; i++) Mercs.Add(i * 5);
            Mercs.ToggleAirDefence(); Unique();
            Ok((At(4) != null) == (size >= 4), "radar priority size " + size);
            Ok((At(5) != null) == (size >= 5), "ZU priority size " + size);
            if (size >= 4)
            {
                At(0).Dead = true; Mercs.TestTick(1f);
                Ok(At(0) != null, "borrow lower priority crew when no follower, size " + size); Unique();
            }
        }

        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(i * 5);
        MercAA.Blocked[0] = true; MercAA.Blocked[4] = true; Mercs.ToggleAirDefence();
        Ok(At(0) == null && At(4) == null && At(1) != null && At(5) != null, "native/player/destroyed posts respected");
        MercAA.Blocked[0] = false; MercAA.Blocked[4] = false; Mercs.TestTick(1f);
        Ok(At(0) != null && At(4) != null, "newly available/repaired posts filled"); Unique();
        Mercs.Record wound = At(1); wound.Down.Down = true; Mercs.TestTick(2f);
        Ok(At(1) != wound && At(1) != null && Post(wound) < 0, "downed post freed and replaced");
        wound.Down.Down = false; Mercs.TestTick(3f); Ok(Post(wound) < 0, "revived merc follows rather than stealing replacement");
        Mercs.Record manual = At(0); manual.Order = new MercOrder(); manual.Order.Mode = MercOrder.Stay;
        Mercs.TestTick(4f); Ok(manual.Order.Mode == MercOrder.Stay, "manual override remains");
        Mercs.ToggleAirDefence(); Ok(manual.Order.Mode == MercOrder.Stay, "release respects manual override");

        Mercs.Reset(); Mercs.Record unpaid = Mercs.Add(50); unpaid.Unpaid = true; Mercs.Add(10);
        Mercs.ToggleAirDefence(); Ok(Post(unpaid) < 0 && At(0) != null, "unpaid merc excluded");
        At(0).Unpaid = true; Mercs.TestTick(1f); Ok(At(0) == null, "newly unpaid crew releases");
        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(i);
        Mercs.ToggleAirDefence(); Mercs.Record reserve = Mercs.Roster[0];
        foreach (Mercs.Record r in Mercs.Roster) if (Post(r) < 0) reserve = r;
        reserve.Raid.Previous = reserve.Order; reserve.Raid.Cover = new MercOrder(); reserve.Order = reserve.Raid.Cover;
        At(0).Dead = true; Mercs.TestTick(1f); Ok(At(0) == reserve, "automatic siren cover does not lose reserve");
        MapScene.Current = "Other"; Mercs.TestTick(2f); Ok(!Mercs.AirDefenceActive, "region change clears local automation");
        Mercs.Reset(); Mercs.Add(10); Mercs.ToggleAirDefence(); Mercs.OwnerObject = new GameObject();
        Mercs.TestTick(1f); Ok(!Mercs.AirDefenceActive, "owner respawn clears local automation");
        Mercs.Reset(); Mercs.Add(10); Mercs.OwnerPosition = new Vector3(1681, 0, 0); Mercs.ToggleAirDefence();
        Ok(!Mercs.AirDefenceActive, "command limited to airfield presence");
        Mercs.Reset(); Mercs.Add(10); TowerRadar.Built = false; Mercs.ToggleAirDefence();
        Ok(!Mercs.AirDefenceActive, "missing field refuses with reply");
        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(i);
        // Missing gun may be built later; registry is sampled without scene scans.
        Flak.Guns[6] = null; Mercs.ToggleAirDefence(); Ok(At(6) == null, "missing gun skipped");
        Flak.Guns[6] = new Flak.Gun(); Mercs.TestTick(1f); Ok(At(6) != null, "late registered heavy gun staffed");
        foreach (Mercs.Record r in Mercs.Roster) if (Post(r) >= 0) MercAA.Occupant[Post(r)] = r.Unit.Ai;
        Mercs.TestTick(2f); Unique();
        for (int i = 0; i < 1000; i++) Mercs.TestTick(3f + i);
        Stopwatch timer = new Stopwatch();
        GC.Collect(); long before = GC.GetTotalMemory(false); int gen = GC.CollectionCount(0);
        timer.Start(); long peak = 0;
        for (int i = 0; i < 100000; i++)
        {
            long start = Stopwatch.GetTimestamp(); Mercs.TestTick(2000f + i);
            long elapsed = Stopwatch.GetTimestamp() - start; if (elapsed > peak) peak = elapsed;
        }
        timer.Stop(); long delta = GC.GetTotalMemory(false) - before;
        double avg = timer.Elapsed.TotalMilliseconds / 100000;
        Ok(delta == 0 && GC.CollectionCount(0) == gen, "steady adapter zero heap growth/collections");
        Ok(avg < 0.1, "average scheduled tick below 0.1 ms");
        Ok(FrameProf.Starts == FrameProf.Ends, "balanced F6 scope");
        Console.WriteLine("INFO: six-merc scheduled tick avg {0:F6} ms, wall peak {1:F6} ms; heap delta {2}, gen0 delta {3}", avg, peak * 1000.0 / Stopwatch.Frequency, delta, GC.CollectionCount(0) - gen);
        Console.WriteLine("PASS: {0} production adapter/station assertions; failures {1}", checks, failures);
        return failures == 0 ? 0 : 1;
    }
}

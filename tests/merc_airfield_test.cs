// Offline doubles for the production A S6 owner adapter and GiveStation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using NextDayRevival;

namespace UnityEngine
{
    class GameObject { }
    class Transform { internal Vector3 position; }
    class Component { internal Transform transform = new Transform(); }
    struct Quaternion { }
    struct Vector3
    {
        internal float x, y, z;
        internal Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        internal static Vector3 zero { get { return new Vector3(); } }
        internal float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator *(Vector3 a, float k) { return new Vector3(a.x * k, a.y * k, a.z * k); }
    }
    static class Mathf { internal static int RoundToInt(float v) { return (int)Math.Round(v); } }
    static class Time { internal static float time; }
}
namespace NextDayRevival
{
    class MercOrder
    {
        internal const int Follow = 0, ManGun = 5, ManRadar = 6, Stay = 1, Vehicle = 4, Attack = 7,
            Drive = 8, Patrol = 2, Perimeter = 3;
        internal int Mode;
        internal Vector3[] Points;
        internal Vector3 Facing;
        internal string Scene;
        internal float IssuedAt;
        internal float RadiusM;
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
        static readonly MercAAPost[] Holds = MakeHolds();
        static MercAAPost[] MakeHolds()
        {
            MercAAPost[] posts = new MercAAPost[7];
            for (int i = 0; i < posts.Length; i++) posts[i] = new MercAAPost();
            return posts;
        }
        internal static MercAAPost Held(int post)
        { Holds[post].Ai = Occupant[post]; return Occupant[post] == null ? null : Holds[post]; }
        internal static int LocalActor() { return 1; }
        internal static bool AvailableForOrder(int post) { return Available(post, LocalActor()); }
        internal static int Releases;
        internal static bool IsOrder(MercOrder o) { return o.Mode == MercOrder.ManGun || o.Mode == MercOrder.ManRadar; }
        internal static bool IsVehicle(MercOrder o) { return o.Mode == MercOrder.ManGun && o.Facing.x < 0f; }
        internal static bool Pose(int p, out Vector3 at, out Quaternion rot)
        {
            at = new Vector3(p * 400f, p == Radar ? 43f : 0f, 0f); rot = new Quaternion();
            return p == Radar ? TowerRadar.Built : Flak.ByIndex(p) != null;
        }
        // PRODUCTION_AVAILABLE
        // PRODUCTION_CAN_APPROACH
        // PRODUCTION_HOSTILE_CREW
        internal static void Release(MercUnit u)
        {
            Releases++;
            for (int p = 0; p < 7; p++) if (Occupant[p] == u.Ai) Occupant[p] = null;
        }
    }
    static class Flak
    {
        internal class Gun
        {
            internal int Index;
            internal bool Town, ShortRange;
            internal float ClaimedUntil;
            internal Component Gunner, Loader;
        }
        internal static Gun _manned;
        internal static bool Up(Component ai) { return ai != null; }
        internal static Gun[] Guns = new Gun[7];
        internal static Gun ByIndex(int i) { return Guns[i]; }
    }
    static class TowerRadar
    {
        internal static bool Built = true;
        internal static bool Working { get { return !MercAA.Blocked[MercAA.Radar]; } }
        internal static int OperatorActor = -1;
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
    static class MercUi
    {
        internal static void OrderReply(string s, bool error) { }
        internal static int Toasts; internal static string Last;
        internal static void Toast(string s, bool warn) { Toasts++; Last = s; }
        static string AttackText(Mercs.Record m) { return "ATTACK"; }
        // PRODUCTION_ORDER_TEXT
    }
    static class NpcWar
    {
        internal static int Wakes; internal static void MercStationWake(MercUnit u) { Wakes++; }
        // H M2: the factions the squad hates, as bodies.
        internal static readonly List<Component> Enemies = new List<Component>();
        internal static bool Hates(Component me, Component other) { return me != null && Enemies.Contains(other); }
    }
    static class RadarOperator { internal static bool Alive; internal static Component Man; }
    static class AirDefenceDamage { internal static bool Alive(int post) { return !MercAA.Blocked[post]; } }
    static class Mortar
    {
        internal static void MercPrepare(int post) { }
        internal static bool MercAvailable(int post) { return false; }
    }
    internal static partial class Mercs
    {
        internal class Profile { internal int AAGunner; }
        internal class Record
        {
            internal MercUnit Unit = new MercUnit(); internal MercOrder Order = MercOrder.FollowMe();
            internal bool Dead, Deserted, Unpaid, Selected = true, Peaceful;
            internal int Id; internal string Name = "M";
            internal string ProfileId = ""; internal DownState Down = new DownState(); internal RaidState Raid = new RaidState();
        }
        static readonly List<Record> _roster = new List<Record>(10);
        internal static GameObject OwnerObject = new GameObject(); internal static Vector3 OwnerPosition;
        internal static int Saves;
        internal static Profile ProfileById(string id) { return null; }
        // PRODUCTION_GIVE
        static void SendOrders(List<Record> list) { Saves++; }
        // H M2: the production OrderReceived returns before its reply while quiet.
        internal static int Loud; static bool _orderQuiet;
        static void OrderReceived(Record r, bool focus, Vector3 p) { if (!_orderQuiet) Loud++; }
        internal static bool Picked; internal static int Announced;
        static readonly List<Record> _onDuty = new List<Record>();
        internal static bool PickActive()
        { int a = 0, n = 0; foreach (Record r in _roster) { if (r.Dead) continue; a++; if (r.Selected) n++; } return MercTargetPlan.Explicit(Picked, n, a); }
        internal static string AnnouncedText;
        static void Announce(string what, List<Record> got) { Announced = got.Count; AnnouncedText = what; }
        internal static void SaveStationOrders(List<Record> list) { Saves++; }
        // PRODUCTION_GIVE_STATION
        internal static List<Record> Roster { get { return _roster; } }
        internal static void TestTick(float t) { Time.time = t; AirDefenceTick(t); }
        internal static void Reset()
        {
            _defenceActive = false; Defence.Clear(); _roster.Clear(); Saves = 0; Picked = false; Announced = 0;
            OwnerObject = new GameObject(); OwnerPosition = Vector3.zero;
            TowerRadar.Built = true; MapScene.Current = "East"; Time.time = 0f;
            TowerRadar.OperatorActor = -1; RadarOperator.Alive = false; RadarOperator.Man = null; Flak._manned = null;
            NpcWar.Enemies.Clear(); Loud = 0; MercUi.Toasts = 0;
            for (int p = 0; p < 7; p++)
            {
                MercAA.Blocked[p] = false; MercAA.Occupant[p] = null;
                Flak.Guns[p] = p == 4 ? null : new Flak.Gun();
                if (p != 4) { Flak.Guns[p].Index = p; Flak.Guns[p].Town = p == 2 || p == 3; Flak.Guns[p].ShortRange = p == 5; }
            }
        }
        internal static Record Add(int trait)
        { Record r = new Record(); r.Id = _roster.Count + 1; r.Unit.AAGunner = trait; r.Unit.Order = r.Order; _roster.Add(r); return r; }
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
        // H M2: nearest first, not by trait. Each merc stands by one post.
        float[] near = { 2400f, 0f, 1600f, 400f, 2000f, 5000f };
        for (int i = 0; i < 6; i++) Mercs.Add(i * 10).Unit.Ai.transform.position = new Vector3(near[i], 0f, 0f);
        Mercs.Picked = false; // All checked, no pick: the whole squad.
        Mercs.ToggleAirDefence();
        Ok(Mercs.AirDefenceActive, "toggle active immediately"); Unique();
        Ok(At(0) != null && At(1) != null && At(6) != null && At(4) != null && At(5) != null, "three 52-K, radar and ZU staffed");
        Ok(At(6) == Mercs.Roster[0] && At(0) == Mercs.Roster[1] && At(4) == Mercs.Roster[2]
            && At(1) == Mercs.Roster[3] && At(5) == Mercs.Roster[4], "nearest merc takes each post");
        Ok(Mercs.Loud == 0 && Mercs.AnnouncedText.StartsWith("MAN AIR DEFENCE"), "one summary instead of a reply per merc");
        Ok(MercUi.OrderText(At(4)) == "MAN RADAR", "production squad list displays MAN RADAR on assignment");
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

        // Equal traits have a stable contract-ID tie, independent of roster order.
        for (int reverse = 0; reverse < 2; reverse++)
        {
            Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(0);
            if (reverse != 0) Mercs.Roster.Reverse();
            Mercs.ToggleAirDefence();
            Ok(At(0).Id == 1 && At(1).Id == 2 && At(4).Id == 3 && At(5).Id == 4 && At(6).Id == 5,
                "stable equal-distance gun/radar assignments despite roster permutation");
        }
        Mercs.Reset(); for (int i = 0; i < 5; i++) Mercs.Add(0);
        Mercs.ToggleAirDefence(); Mercs.Record radar = At(4), zu = At(5);
        At(0).Dead = true; Mercs.TestTick(1f);
        Ok(At(4) == radar && At(0) == zu, "equal-trait casualty borrows ZU before radar");

        // H M2: posts are kept; a casualty moves only the lowest-priority
        // crew up, releasing old physical claims in the SAME reconciliation.
        Mercs.Reset(); for (int i = 0; i < 5; i++) Mercs.Add(0);
        Mercs.ToggleAirDefence(); radar = At(4); zu = At(5);
        radar.Unit.AAGunner = 50;
        foreach (Mercs.Record r in Mercs.Roster) if (Post(r) >= 0) MercAA.Occupant[Post(r)] = r.Unit.Ai;
        int toastsBefore = MercUi.Toasts;
        At(0).Down.Down = true; Mercs.TestTick(1f);
        Ok(At(0) == zu && At(4) == radar && At(5) == null && MercAA.Occupant[5] == null,
            "gun transfer releases old physical claims before replacement; radar keeps its operator"); Unique();
        Ok(MercUi.Toasts == toastsBefore + 1 && Mercs.Loud == 0, "re-crewing shows one short summary line");
        MercOrder gunStable = zu.Order; Mercs.TestTick(2f);
        Ok(At(0) == zu && zu.Order == gunStable, "replacement gunner remains stable on next tick");
        Ok(MercUi.OrderText(radar) == "MAN RADAR", "production squad list keeps the radar label");

        // Exercise the actual MercAA availability checks, not a free-seat bool.
        Mercs.Reset(); for (int i = 0; i < 4; i++) Mercs.Add(0);
        RadarOperator.Alive = true; Mercs.ToggleAirDefence();
        Ok(At(4) == null && At(5) != null, "living native operator is never evicted");
        RadarOperator.Alive = false; Mercs.TestTick(1f);
        Ok(At(4) != null && At(5) == null, "free native console gains radar before ZU");
        Mercs.Record op = At(4); MercOrder opOrder = op.Order;
        TowerRadar.OperatorActor = 2; Mercs.TestTick(2f);
        Ok(At(4) == op && op.Order == opOrder && !MercAA.CanApproach(4, op.Unit.Ai),
            "player at the console: the merc keeps his post and waits there, no FOLLOW");
        TowerRadar.OperatorActor = -1; Mercs.TestTick(3f);
        Ok(At(4) != null, "radar returns when player leaves console");
        MercAA.Blocked[4] = true; Mercs.TestTick(4f);
        Ok(At(4) == null, "damaged radar cannot accept a merc");
        MercAA.Blocked[4] = false; Mercs.TestTick(5f);
        Ok(At(4) != null, "repaired radar resumes priority");
        op = At(4); MercAA.Occupant[4] = new Component(); Mercs.TestTick(6f);
        Ok(At(4) == op && !MercAA.CanApproach(4, op.Unit.Ai), "foreign merc radar lease is waited out, never bypassed");
        MercAA.Occupant[4] = null; Mercs.TestTick(7f);
        Ok(At(4) != null, "expired foreign radar lease permits assignment");

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
        // G O1: a pick in L mans air defence alone; the rest keep their orders.
        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(i);
        Mercs.Record staying = Mercs.Roster[1]; staying.Order = new MercOrder(); staying.Order.Mode = MercOrder.Stay;
        foreach (Mercs.Record r in Mercs.Roster) r.Selected = r.Id == 3 || r.Id == 5;
        Mercs.ToggleAirDefence(); int manned = 0;
        foreach (Mercs.Record r in Mercs.Roster) if (Post(r) >= 0) { manned++; Ok(r.Id == 3 || r.Id == 5, "only picked mercs take posts"); }
        Ok(manned == 2 && staying.Order.Mode == MercOrder.Stay && Mercs.Announced == 2, "pick mans air defence alone; feedback names the two");
        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(i);
        // Missing gun may be built later; registry is sampled without scene scans.
        Flak.Guns[6] = null; Mercs.ToggleAirDefence(); Ok(At(6) == null, "missing gun skipped");
        Flak.Guns[6] = new Flak.Gun(); Flak.Guns[6].Index = 6;
        Mercs.TestTick(1f); Ok(At(6) != null, "late registered heavy gun staffed");
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
        // H M2 harness: four 52-K + radar, two guns held by enemy crews. One
        // plan on the click, nearest first; the enemy crews are cleared by the
        // production step decision, every merc reaches his seat, nobody follows.
        Mercs.Reset(); Flak.Guns[2].Town = false;
        Component enemyA = new Component(), enemyB = new Component();
        Flak.Guns[1].Gunner = enemyA; Flak.Guns[6].Gunner = enemyB;
        NpcWar.Enemies.Add(enemyA); NpcWar.Enemies.Add(enemyB);
        float[] spot = { 2350f, 60f, 1500f, 450f, 820f }; // by posts 6, 0, radar, 1, 2
        for (int i = 0; i < 5; i++) Mercs.Add(0).Unit.Ai.transform.position = new Vector3(spot[i], 0f, 600f);
        NpcWar.Wakes = 0;
        Mercs.ToggleAirDefence();
        Mercs.Record[] crew = Mercs.Roster.ToArray();
        int[] expect = { 6, 0, 4, 1, 2 };
        for (int i = 0; i < 5; i++) Ok(Post(crew[i]) == expect[i], "H M2: nearest-first split on the click, merc " + i);
        Ok(At(5) == null, "H M2: four 52-K and radar before the ZU-23");
        Ok(NpcWar.Wakes == 5 && Mercs.Loud == 0, "H M2: every merc starts at once, no reply per merc");
        Ok(Mercs.AnnouncedText.Contains("clearing enemy crews: 2"), "H M2: the summary names the two enemy-held guns");
        Ok(MercAA.HostileCrew(1, crew[3].Unit.Ai) == enemyA && MercAA.HostileCrew(6, crew[0].Unit.Ai) == enemyB
            && MercAA.HostileCrew(0, crew[1].Unit.Ai) == null, "H M2: production HostileCrew finds the enemy gunners only");
        MercOrder[] given = new MercOrder[5];
        for (int i = 0; i < 5; i++) given[i] = crew[i].Order;
        int savesAt = Mercs.Saves, toastsAt = MercUi.Toasts;
        float[] engagedAt = new float[7], seatedAt = { -1f, -1f, -1f, -1f, -1f };
        bool followed = false, drifted = false, reissued = false;
        float walk = 4f * 2.8f * 0.5f, reach = 60f * 2.8f; // 4 m/s per 0.5 s tick; 60 m rifle line
        for (int step = 1; step <= 240; step++)
        {
            float t = step * 0.5f;
            Mercs.TestTick(t);
            for (int i = 0; i < 5; i++)
            {
                Mercs.Record r = crew[i]; int post = Post(r);
                if (r.Order != given[i]) reissued = true;
                if (r.Order.Mode == MercOrder.Follow || post < 0) { followed = true; continue; }
                Component occupant = MercAA.HostileCrew(post, r.Unit.Ai);
                bool open = occupant == null && MercAA.CanApproach(post, r.Unit.Ai);
                int act = MercStationPlan.PostAction(occupant != null, open, !open && Mercs.AirDefenceManaged(r.Unit));
                if (act == MercStationPlan.PostReplace) drifted = true;
                Vector3 me = r.Unit.Ai.transform.position, d = r.Order.Points[0] - me; d.y = 0f;
                float dist = (float)Math.Sqrt(d.sqrMagnitude);
                float stop = act == MercStationPlan.PostClear ? reach : act == MercStationPlan.PostWait ? MercStationPlan.WaitReach : 0f;
                if (dist > stop) r.Unit.Ai.transform.position = me + d * (Math.Min(walk, dist - stop) / dist);
                if (act == MercStationPlan.PostClear && dist <= reach + walk)
                {
                    if (engagedAt[post] == 0f) engagedAt[post] = t;
                    if (t - engagedAt[post] >= 6f) Flak.Guns[post].Gunner = null; // six seconds of fire kill him
                }
                if (act == MercStationPlan.PostGo && dist <= walk && seatedAt[i] < 0f)
                { MercAA.Occupant[post] = r.Unit.Ai; seatedAt[i] = t; }
            }
        }
        float last = 0f; bool all = true;
        for (int i = 0; i < 5; i++) { if (seatedAt[i] < 0f) all = false; if (seatedAt[i] > last) last = seatedAt[i]; }
        Ok(!followed, "H M2: no assigned merc falls back to FOLLOW while the order stands");
        Ok(!reissued && Mercs.Saves == savesAt, "H M2: the assignment is computed once; no re-orders while posts are busy");
        Ok(!drifted, "H M2: a busy post is waited out or cleared, never swapped");
        Ok(engagedAt[1] > 0f && engagedAt[1] <= 45f && engagedAt[6] > 0f && engagedAt[6] <= 45f,
            "H M2: both enemy-held guns are engaged within 45 s (direct 154 m walk: 39 s)");
        Ok(all && last <= 90f, "H M2: every merc sits at his post within 90 s (last " + last + " s)");
        Ok(MercUi.Toasts == toastsAt && Mercs.Loud == 0, "H M2: no notifications for the duty's own status changes");
        Console.WriteLine("INFO: H M2 enemy guns engaged at {0} s / {1} s, last seat {2} s", engagedAt[1], engagedAt[6], last);
        Mercs.Reset(); for (int i = 0; i < 6; i++) Mercs.Add(0);
        Component friend = new Component(); Flak.Guns[0].Gunner = friend; MercAA.Occupant[4] = new Component();
        Mercs.ToggleAirDefence();
        Ok(At(0) == null && At(4) == null && At(1) != null && At(5) != null,
            "H M2: a friendly native crew and a foreign radar lease are not planned over");
        Console.WriteLine("INFO: six-merc scheduled tick avg {0:F6} ms, wall peak {1:F6} ms; heap delta {2}, gen0 delta {3}", avg, peak * 1000.0 / Stopwatch.Frequency, delta, GC.CollectionCount(0) - gen);
        Console.WriteLine("PASS: {0} production adapter/station assertions; failures {1}", checks, failures);
        return failures == 0 ? 0 : 1;
    }
}

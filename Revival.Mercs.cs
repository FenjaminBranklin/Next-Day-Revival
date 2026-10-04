// B3 mercenaries, phase 1 (docs/ai/tasks/mercenaries.md; B3 report in
// docs/ai/tasks/b3-mercs-phase1.md): hire at every settlement trader's
// "Mercenaries" tab, FOLLOW / STAY / PEACEFUL orders, whitelist, upkeep per
// 24 in-game hours of deployment, permanent death, roster on the master server.
// Phase 2 (B3b, docs/ai/tasks/b3b-mercs-patrol-perimeter.md): PATROL and
// SECURE PERIMETER (MercOrder), stored with the merc in the roster row.
// Phase 4 (B3c, docs/ai/tasks/b3c-mercs-vehicle.md): FOLLOW MY VEHICLE -
// boarding, riding, the vehicle gun, getting out (Revival.MercsRide.cs).
// B3d (docs/ai/tasks/b3d-mercs-e2e.md): one money op at a time, ended
// contracts resent until the server drops them, regeneration (D13); checked
// end to end against the server module by research/merc_e2e_check.py.
//
//   PROFILES   built-in defaults (DefaultProfiles = mercdef.to_tsv of
//              assets/editor/mercs.json, verify.py compares) until the web
//              editor's /runtime/mercs answers (LiveRoutes.TickMercs).
//   ROSTER     never local. It lives on the master server per Steam id and
//              travels over the game's own storage message on storageId 7700:
//              a command is a playerStorage bag whose description is the text
//              below, the answer is a storageListResponse holding one 7700 bag
//              (prefix on BackendManager.MsgStorageListRecieve). The server
//              checks every op (money already gone from the stored profile,
//              cap, profile exists, dead ids never return). A server without
//              the roster (phase 1S not deployed) answers a 7700 request with
//              the normal bags 0/1: hiring is then disabled with the reason,
//              and the admin panel's "give merc" makes a session-only test
//              merc that is never saved.
//   RUNTIME    the owner's client spawns his mercs as his own Photon objects
//              (Crew.DropOwnedSquad), so PUN removes them everywhere when he
//              logs out or drops; NpcWar drives them (Revival.MercsWar.cs).
//   UI         Revival.MercsUi.cs.
//
// Wire format (ASCII, one key=value per line):
//   command  NDR-MERC-1 / seq=<n> / op=<get|hire|pay|died|state|desert|dismiss|
//            wl|order|admin-give|admin-clear|admin-hours> / op arguments
//   answer   NDR-MERC-1 / seq=<last processed> / result=ok|err:<code> / cap= /
//            grace= / serial= / m=id|profile|hp01|paidUntil|deployed|order|
//            peaceful|combat|name / dead=1,2 / w=steam|name
//   order    follow | vehicle | stay | mode~scene~k~n~radius~x,y,z;...~fx,fz (MercOrder)
//
// SEAMS: RevivalPlugin (BindConfig, Install, Update -> Tick, OnGUI -> Draw),
// LiveRoutes (LoadProfiles), NpcWar (Revival.MercsWar.cs), Crew
// (DropOwnedSquad), Admin (panel section, LocalStats, LocalSteamId).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>One spawned merc on his owner's client: what NpcWar's merc
    /// squad and the roster share.</summary>
    internal sealed class MercUnit
    {
        internal int Id;
        internal string Name = "";
        internal GameObject Settlement;
        internal Component Ai;
        internal int Slot;
        internal bool Peaceful;
        internal float DefendUntil;
        internal int LastAttacker = -1;
        internal bool Deserting;
        internal Vector3 DesertTo;
        internal float DesertUntil;
        internal Transform Owner;
        internal GameObject OwnerGo;
        internal int Precise, Fast, Tanky, AAGunner;
        internal int AAView;
        internal float AANextSend;
        internal float StationRetryAt;
        internal bool AARetreat;
        internal bool RaidCover;
        internal int RaidProbe;
        internal CoverPick RaidRoof;
        internal float RaidRoofAt;
        internal bool RaidProbeDeferred;
        internal float TierDamageScale = 1f; // Cached at spawn; owner applies incoming armor.
        internal float Grade = 0.5f;         // M3: MercGrade.Of his traits and level (0..1, tiers 0..3)
        internal float MaxHealth;            // B3d: his full health at spawn (regeneration)
        internal float AttackerUntil;        // B3d: LastAttacker is answered until then
        internal float NextOrder, NextSpeed, NextWarp, NextPlayerScan;
        internal float ClearAimAt;           // H M2: next ray at the enemy crewman on his post
        internal readonly MercPostTrack DefTrack = new MercPostTrack(); // H M3: way to his AA post (Revival.MercDefenceCore.cs)
        internal Transform PlayerTarget;
        internal GameObject KillTargetSet;
        internal Component QuickNpc;
        internal GameObject QuickPlayer;
        internal MercOrder QuickFor;
        internal float QuickUntil;
        internal string QuickSteam;
        internal float LastSpeedSet = -1f;
        // B3b: PATROL / PERIMETER (Revival.MercsWar.cs). Order is shared with
        // the roster record; the rest is this merc's own progress through it.
        internal bool Rally;
        internal Vector3 Approach;       // remembered approach when threats go quiet
        internal MercOrder Order = MercOrder.FollowMe();
        internal MercOrder GoalFor;          // the order Goal/Post were computed for
        internal int GoalLeg = -1;
        internal Vector3 Goal, Post, PostLook;
        internal int Leg;                    // patrol: the point he walks to
        internal float LegUntil, PauseUntil, NextLook, ChaseUntil, ChaseCooldown, NextCover, LastStep;
        internal int LookStep;
        internal bool Chasing;
        internal Vector3 Probe;              // perimeter: the corner he checks
        internal float NextProbe, ProbeUntil;
        internal int ProbePhase;             // 0 at the post, 1 walking out, 2 looking
        // B3c: FOLLOW MY VEHICLE - his seat, boarding and gun (Revival.MercsRide.cs).
        internal readonly MercSeat Ride = new MercSeat();
        // M1: his threats, their view of him and his best cover (Revival.MercCover.cs).
        internal readonly MercSense Sense = new MercSense();
        // W: what his notifications last saw (Revival.MercNotify.cs).
        internal readonly MercWatch Watch = new MercWatch();
        // M2: his fight loop - cover, peek, burst, relocate (Revival.MercFight.cs).
        internal readonly MercFight Fight = new MercFight();
        // merc-attack-orders: his progress through an ATTACK (Revival.MercAttackCore.cs).
        internal readonly MercAttackRun Attack = new MercAttackRun();
        internal readonly MercMovePlan Move = new MercMovePlan();
        // i-m3: the one owner of his move target per tick (Revival.MercMoveOwnerCore.cs).
        internal readonly MercMoveOwner Own = new MercMoveOwner();
        // x-merc-competence: his halt cover under FOLLOW (Revival.MercsWar.cs MercHaltCover).
        internal readonly MercHalt Halt = new MercHalt();
        // c-m2: his walk up to firing distance (Revival.MercCloseIn.cs).
        internal readonly MercCloseRun Close = new MercCloseRun();
        // x-merc-competence: the last hit as it landed (the death report), the
        // threat nearest him then (NPC rounds carry no attacker), self-heal pace.
        internal MercHitNote LastHit;
        internal Transform LastHitBy;
        internal float NextSelfHeal; // short recovery cooldown after a new order
        internal MercMedicine Medicine;
        internal Mercs.Record RescueTarget;
        internal float NextRescue;
        internal readonly MercMedicRoute MedicRoute = new MercMedicRoute();
        internal byte WeaponRole;
        internal readonly MercSupplyTrip Supply = new MercSupplyTrip();
        float _base = -1f;

        internal bool Follow { get { return Order.Mode == MercOrder.Follow; } }
        internal bool Alert { get { return Order.Mode == MercOrder.Perimeter && !Deserting; } }

        /// <summary>The vanilla speed of his current state: a value that is
        /// not the one we wrote is the state machine's new base.</summary>
        internal float BaseSpeed(float current)
        {
            if (_base < 0f || Mathf.Abs(current - LastSpeedSet) > 0.05f) _base = current;
            return _base;
        }
    }

    /// <summary>B3b: one order as the owner gave it. A new order is a new
    /// instance, shared by every merc it was given to (K of N tells each his
    /// place in the group: a start leg on a route, a sector of a perimeter).
    /// It travels to the server in the roster row's order field, so it has no
    /// '|' and no line break: mode~scene~k~n~radius~x,y,z;x,y,z~fx,fz.
    /// B3c: VEHICLE (FOLLOW MY VEHICLE) is a bare "vehicle" like "follow".
    /// merc-attack-orders: ATTACK is a FOLLOW extension the unchanged server
    /// keeps verbatim (SafeOrder passes any "follow~" text of its own
    /// characters) and an older client reads as plain FOLLOW:
    /// follow~scene~k~n~radius~objective;origin~dx,dz~attack~p|d|m.</summary>
    internal sealed class MercOrder
    {
        internal const int Follow = 0, Stay = 1, Patrol = 2, Perimeter = 3, Vehicle = 4, ManGun = 5, ManRadar = 6, Attack = 7, Drive = 8;
        internal const int MaxPoints = 6;
        // ATTACK: what the owner gave - a point he aimed at, a direction (no
        // ground under the crosshair: a bounded endpoint) or a map click.
        internal const byte AtPoint = 0, AtDirection = 1, AtMap = 2;
        internal const float AttackMaxUnits = 1680f;     // 600 m from where it was given
        internal const float AttackMinUnits = 28f;       // 10 m: nearer is no attack
        internal static readonly float[] Radii = { 15f, 25f, 40f, 60f };   // metres

        internal int Mode;
        internal bool Survive;           // STAY extension, accepted by existing server
        internal bool MoveNear;          // STAY ~move: independent cover near the marker
        internal string MoveInkScene, MoveInkKey; // Order-local map caches, not serialized.
        internal Vector3[] Points = new Vector3[0];
        internal Vector3 Facing;
        internal float RadiusM = 25f;
        internal string Scene = "";
        internal int K, N = 1;
        internal float IssuedAt;
        internal byte Kind;              // ATTACK: AtPoint / AtDirection / AtMap
        internal object Team;            // ATTACK: the group's shared run state (MercAttackTeam), not saved

        internal static MercOrder FollowMe() { return new MercOrder(); }
        internal static MercOrder FollowVehicle() { MercOrder o = new MercOrder(); o.Mode = Vehicle; return o; }

        internal Vector3 Centre { get { return Points.Length > 0 ? Points[0] : Vector3.zero; } }
        /// <summary>ATTACK: where the owner gave it (the corridor's start).</summary>
        internal Vector3 Origin { get { return Points.Length > 1 ? Points[1] : Centre; } }
        internal float RadiusUnits { get { return RadiusM * 2.8f; } }
        internal bool Placed { get { return Mode == Stay || Mode == Patrol || Mode == Perimeter || Mode == ManGun || Mode == ManRadar; } }

        /// <summary>The same order for merc k of n.</summary>
        internal MercOrder For(int k, int n)
        {
            MercOrder o = (MercOrder)MemberwiseClone();
            o.K = k; o.N = Mathf.Max(1, n);
            return o;
        }

        internal static string SceneKey(string scene)
        {
            if (scene == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < scene.Length && sb.Length < 40; i++)
            {
                char c = scene[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        static string F(float v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        internal string Encode()
        {
            // Driving is a session lease: never resume an unattended trip after relog.
            if (Mode == Drive) return "follow";
            if (Mode == Follow) return "follow";
            if (Mode == Vehicle) return "vehicle";
            StringBuilder sb = new StringBuilder();
            if (Mode == Attack)
            {
                sb.Append("follow~").Append(SceneKey(Scene)).Append('~').Append(K.ToString(CultureInfo.InvariantCulture)).Append('~')
                  .Append(N.ToString(CultureInfo.InvariantCulture)).Append('~').Append(F(RadiusM)).Append('~');
                Vector3 a = Centre, b = Origin;
                sb.Append(F(a.x)).Append(',').Append(F(a.y)).Append(',').Append(F(a.z)).Append(';')
                  .Append(F(b.x)).Append(',').Append(F(b.y)).Append(',').Append(F(b.z));
                sb.Append('~').Append(Facing.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Facing.z.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append("~attack~").Append(Kind == AtDirection ? 'd' : Kind == AtMap ? 'm' : 'p');
                return sb.ToString();
            }
            sb.Append(Mode == Stay ? "stay" : Mode == Patrol ? "patrol" : Mode == ManGun ? "gun" : Mode == ManRadar ? "radar" : "perim").Append('~')
              .Append(SceneKey(Scene)).Append('~').Append(K.ToString(CultureInfo.InvariantCulture)).Append('~')
              .Append(N.ToString(CultureInfo.InvariantCulture)).Append('~').Append(F(RadiusM)).Append('~');
            for (int i = 0; i < Points.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(F(Points[i].x)).Append(',').Append(F(Points[i].y)).Append(',').Append(F(Points[i].z));
            }
            sb.Append('~').Append(F(Facing.x)).Append(',').Append(F(Facing.z));
            if (Survive) sb.Append("~survive");
            else if (MoveNear) sb.Append("~move");
            return sb.ToString();
        }

        /// <summary>Never null. Phase 1 rows ("follow", "stay") and anything
        /// unreadable decode to FOLLOW, except a bare "stay", which holds where
        /// he spawns (phase 1 did not store the point).</summary>
        internal static MercOrder Decode(string text)
        {
            MercOrder o = new MercOrder();
            if (string.IsNullOrEmpty(text) || text == "follow") return o;
            if (text == "stay") { o.Mode = Stay; return o; }
            if (text == "vehicle") { o.Mode = Vehicle; return o; }
            try
            {
                string[] c = text.Split('~');
                if (c.Length < 7) return o;
                if (c[0] == "follow") return DecodeAttack(c);
                int mode = c[0] == "stay" ? Stay : c[0] == "patrol" ? Patrol : c[0] == "perim" ? Perimeter : c[0] == "gun" ? ManGun : c[0] == "radar" ? ManRadar : Follow;
                if (mode == Follow) return o;
                List<Vector3> pts = new List<Vector3>();
                foreach (string p in c[5].Split(';'))
                {
                    string[] xyz = p.Split(',');
                    if (xyz.Length != 3) continue;
                    float x, y, z;
                    if (!float.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        || !float.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        || !float.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) continue;
                    if (pts.Count < MaxPoints) pts.Add(new Vector3(x, y, z));
                }
                if (pts.Count == 0 || (mode == Patrol && pts.Count < 2)) return o;
                int k, n;
                float r;
                int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out k);
                int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                float.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                string[] fc = c[6].Split(',');
                float fx = 0f, fz = 0f;
                if (fc.Length == 2)
                {
                    float.TryParse(fc[0], NumberStyles.Float, CultureInfo.InvariantCulture, out fx);
                    float.TryParse(fc[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fz);
                }
                o.Survive = mode == Stay && c.Length > 7 && c[7] == "survive";
                o.MoveNear = mode == Stay && c.Length == 8 && c[7] == "move";
                if (o.MoveNear)
                {
                    if (pts.Count != 1 || !Finite(pts[0].x) || !Finite(pts[0].y) || !Finite(pts[0].z)
                        || !Finite(fx) || !Finite(fz)) return new MercOrder();
                }
                if (mode == ManGun || mode == ManRadar)
                {
                    if (pts.Count != 1 || float.IsNaN(fx) || float.IsInfinity(fx)
                        || (mode == ManGun && (fx == 0f || fx < -16777215f || fx > 8388708f || fx == 5f || fx == 12f
                            || (fx > 14f && fx < 101f) || fx != (float)Math.Floor(fx)
                            || (fx < 0f && (fz < 1f || fz > 32f || fz != (float)Math.Floor(fz)))
                            || (fx > 0f && fz != 0f)))
                        || (mode == ManRadar && fx != 5f)) return o;
                    Vector3 p = pts[0];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                        || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return o;
                }
                o.Mode = mode; o.Scene = c[1]; o.Points = pts.ToArray();
                o.K = Mathf.Clamp(k, 0, 9); o.N = Mathf.Clamp(n, 1, 10);
                o.RadiusM = Mathf.Clamp(r <= 0f ? 25f : r, 10f, 80f);
                o.Facing = new Vector3(fx, 0f, fz);
                return o;
            }
            catch { return new MercOrder(); }
        }

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v) && v > -1e6f && v < 1e6f; }

        static bool Point(string text, out Vector3 p)
        {
            p = Vector3.zero;
            string[] xyz = text.Split(',');
            float x, y, z;
            if (xyz.Length != 3
                || !float.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)
                || !Finite(x) || !Finite(y) || !Finite(z)) return false;
            p = new Vector3(x, y, z);
            return true;
        }

        /// <summary>merc-attack-orders: a "follow~..." row. Only a complete,
        /// finite ATTACK with a sane length decodes as one; everything else is
        /// FOLLOW (the old reading of the same text).</summary>
        static MercOrder DecodeAttack(string[] c)
        {
            MercOrder o = new MercOrder();
            if (c.Length != 9 || c[7] != "attack" || c[8].Length != 1) return o;
            byte kind = c[8] == "p" ? AtPoint : c[8] == "d" ? AtDirection : c[8] == "m" ? AtMap : (byte)255;
            if (kind == 255) return o;
            string[] pts = c[5].Split(';');
            Vector3 at, from;
            if (pts.Length != 2 || !Point(pts[0], out at) || !Point(pts[1], out from)) return o;
            float dx = at.x - from.x, dz = at.z - from.z;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < AttackMinUnits * 0.5f || len > AttackMaxUnits + 60f) return o;
            int k, n;
            float r;
            if (!int.TryParse(c[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
                || !int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                || !float.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || !Finite(r)) return o;
            if (n < 1 || n > 10 || k < 0 || k >= n) return o;
            o.Mode = Attack; o.Kind = kind; o.Scene = c[1];
            o.Points = new Vector3[] { at, from };
            o.K = k; o.N = n;
            o.RadiusM = Mathf.Clamp(r <= 0f ? 25f : r, 10f, 80f);
            // The direction is the corridor's: origin to objective.
            o.Facing = new Vector3(dx / len, 0f, dz / len);
            return o;
        }
    }

    internal static partial class Mercs
    {
        internal const int StorageId = 7700;
        internal const string KeyPrefix = "merc/";
        const string Magic = "NDR-MERC-1";

        // ============================================================ config
        internal static ConfigEntry<string> CfgWheelKey, CfgListKey;
        internal static ConfigEntry<bool> CfgHudStrip;
        internal static ConfigEntry<float> CfgToastSeconds;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgWheelKey = cfg.Bind("Mercs", "OrderKey", "K",
                "Hold for the mercenary order wheel (1 follow, 2 stay here, 3 patrol, 4 secure perimeter, "
                + "5 follow my vehicle, 6 peaceful, 7 man gun, 8 man radar), double-tap for FOLLOW ME (in a vehicle: FOLLOW MY "
                + "VEHICLE). While a patrol route is set: tap = point at the crosshair, Shift+tap = point here, hold = go. Ctrl+1..5 selects one merc, Ctrl+0 all.");
            CfgListKey = cfg.Bind("Mercs", "ListKey", "L",
                "Opens the mercenary list and whitelist. Ctrl + this key whitelists the "
                + "player under the crosshair.");
            CfgHudStrip = cfg.Bind("Mercs", "HudStrip", true,
                "Hint: the small always-on strip bottom-left with each merc's health and order.");
            CfgToastSeconds = cfg.Bind("Mercs", "ToastSeconds", 4f,
                "Hint: how long merc messages stay top right (1..15 s).");
            MercQuick.BindConfig(cfg);
            MercRide.BindConfig(cfg);
            MercNotify.BindConfig(cfg);
            MercPage.BindConfig(cfg);            // W-UI3: [Mercs] Notifications (the page's filter)
            MercCoverService.BindConfig(cfg);
            MercMoveShoot.BindConfig(cfg);
        }

        // ========================================================== profiles
        internal sealed class Profile
        {
            internal string Settlement, Id, Name, WeaponLabel, ArmourLabel;
            internal int Price, Upkeep, Weapon, Headwear, Mask, Body, Hands, Legs, Backpack;
            internal int Precise, Fast, Tanky, Level, Health, AAGunner;
            internal int HealthPercent = 0, ArmorPercent = -1;
            float _protection = -1f;

            internal RevivalComposition.CrewMan Loadout(string role)
            {
                RevivalComposition.CrewMan man = new RevivalComposition.CrewMan();
                man.Role = role == null ? Name : role;
                man.Weapons = new int[] { Weapon };
                man.Headwear = Headwear; man.Mask = Mask; man.Body = Body;
                man.Hands = Hands; man.Legs = Legs; man.Backpack = Backpack;
                man.Fpv = false;
                man.Class = "regular";
                return man;
            }

            /// <summary>0..1, NpcWar's armour rule, for the card's bar.</summary>
            internal float Protection
            {
                get
                {
                    if (_protection < 0f)
                        _protection = Mathf.Clamp01(1f - (1f - NpcWar.MercProtection(Loadout(null)))
                            * (1f - Tanky / 200f) * MercVital.DamageScale(
                                MercGrade.Of(Precise, Fast, Tanky, Level), ArmorPercent));
                    return _protection;
                }
            }
        }

        // mercdef.to_tsv(assets/editor/mercs.json) - verify.py keeps them equal.
        static readonly string[] DefaultProfiles = new string[]
        {
            "cap\t6",
            "grace\t24",
            "p\tcivilian\tciv-watch\tWatchman\t700000\t60000\t1201\t4106\t0\t4312\t0\t0\t0\t0\t0\t0\t3\t150\tMakarov Pistol\tCap (Black), Black Jacket\t0",
            "p\tcivilian\tciv-rifle\tRifleman\t1200000\t100000\t1006\t4005\t0\t4323\t0\t0\t0\t15\t0\t0\t3\t150\tAKS74U\tMilitary Helmet (Green), Militia shirt with a bulletproof vest\t0",
            "p\tcivilian\tciv-vet\tVeteran\t2200000\t180000\t1002\t4011\t0\t4313\t0\t0\t0\t10\t0\t25\t4\t150\tAKM\tArmor helmet 'Altyn' (Black), Blue Shirt with Bullet-Proof Vest\t0",
            "p\tcivilian\tciv-mark\tMarksman\t2800000\t220000\t1010\t4009\t0\t4308\t0\t0\t0\t35\t10\t0\t4\t150\tSVD\tCamo Hood (Green), Military Jacket with Harness\t0",
            "p\tlooter\tloot-thug\tThug\t700000\t60000\t1203\t0\t0\t4311\t0\t0\t0\t0\t10\t0\t3\t150\tTT\tBrown Jacket\t0",
            "p\tlooter\tloot-raider\tRaider\t1300000\t110000\t1154\t0\t0\t4310\t0\t0\t0\t0\t25\t0\t3\t150\tMP-133\tCivilian Jacket with Harness\t0",
            "p\tlooter\tloot-gunner\tGunner\t2400000\t200000\t1016\t4007\t0\t4314\t0\t0\t0\t0\t0\t30\t4\t150\tRPK\tMilitary Helmet (Black), Plaid Shirt with Bullet-Proof Vest\t25",
            "p\ttraitor\ttrt-deserter\tDeserter\t750000\t65000\t1202\t0\t0\t4321\t0\t0\t0\t0\t0\t0\t3\t150\tAPS\tMilitary uniform (upper part)\t0",
            "p\ttraitor\ttrt-assault\tAssault\t1600000\t130000\t1007\t4008\t0\t4313\t0\t0\t0\t10\t15\t0\t3\t150\tUnknown AK\tMilitary Helmet (Camo), Blue Shirt with Bullet-Proof Vest\t0",
            "p\tmtown\tmt-spetsnaz\tSpetsnaz\t3500000\t280000\t1011\t4017\t0\t4316\t0\t0\t0\t25\t10\t25\t5\t150\tAS Val\tHelmet 'UKB-1', Upper part of UKB-1\t0",
            "p\tmtown\tmt-sniper\tSniper\t3200000\t250000\t1010\t4010\t0\t4308\t0\t0\t0\t40\t0\t0\t5\t150\tSVD\tCamo Hood (Brown), Military Jacket with Harness\t0",
        };

        static List<Profile> _profiles = new List<Profile>();
        static int _profileCap = 6, _profileGrace = 24;
        static string _profileSource = "built-in";

        /// <summary>Validation only (LiveRoutes, worker thread): throws on a bad table.</summary>
        internal static List<Profile> ParseProfiles(string[] lines)
        {
            int cap, grace;
            return ParseProfiles(lines, out cap, out grace);
        }

        static List<Profile> ParseProfiles(string[] lines, out int cap, out int grace)
        {
            List<Profile> result = new List<Profile>();
            HashSet<string> ids = new HashSet<string>();
            cap = -1; grace = -1;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c[0] == "cap" && c.Length == 2) { cap = Whole(c[1], 1, 10); continue; }
                if (c[0] == "grace" && c.Length == 2) { grace = Whole(c[1], 1, 240); continue; }
                if (c[0] != "p" || (c.Length != 20 && c.Length != 21 && c.Length != 23) || result.Count >= 48)
                    throw new IOException("Invalid mercenary profile row");
                Profile p = new Profile();
                p.Settlement = c[1];
                if (p.Settlement != "civilian" && p.Settlement != "looter" && p.Settlement != "traitor"
                    && p.Settlement != "mtown") throw new IOException("Unknown merc settlement");
                p.Id = c[2];
                if (!System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9-]{1,32}$") || !ids.Add(p.Id))
                    throw new IOException("Invalid merc profile id");
                p.Name = c[3].Length == 0 || c[3].Length > 24 ? "Guard" : c[3];
                p.Price = Whole(c[4], 1, 100000000); p.Upkeep = Whole(c[5], 0, p.Price);
                p.Weapon = Whole(c[6], 1, 99999);
                p.Headwear = Whole(c[7], 0, 99999); p.Mask = Whole(c[8], 0, 99999);
                p.Body = Whole(c[9], 0, 99999); p.Hands = Whole(c[10], 0, 99999);
                p.Legs = Whole(c[11], 0, 99999); p.Backpack = Whole(c[12], 0, 99999);
                p.Precise = Whole(c[13], 0, 50); p.Fast = Whole(c[14], 0, 50); p.Tanky = Whole(c[15], 0, 50);
                p.Level = Whole(c[16], 1, 10); p.Health = Whole(c[17], 50, 1000);
                p.WeaponLabel = c[18]; p.ArmourLabel = c[19];
                p.AAGunner = c.Length >= 21 ? Whole(c[20], 0, 50) : 0;
                if (c.Length == 23)
                {
                    p.HealthPercent = Whole(c[21], 0, 500);
                    if (p.HealthPercent != 0 && p.HealthPercent < 100) throw new IOException("Merc HP percent must be 0 or 100..500");
                    p.ArmorPercent = Whole(c[22], -1, 90);
                }
                result.Add(p);
            }
            if (cap < 0 || grace < 0) throw new IOException("Mercenary table without cap/grace");
            return result;
        }

        static int Whole(string s, int lo, int hi)
        {
            int n;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < lo || n > hi)
                throw new IOException("Invalid mercenary number");
            return n;
        }

        internal static void LoadProfiles(string[] lines, string source)
        {
            try
            {
                int cap, grace;
                List<Profile> list = ParseProfiles(lines, out cap, out grace);
                _profiles = list; _profileCap = cap; _profileGrace = grace;
                _profileSource = source;
                RevivalPlugin.L.LogInfo("Mercs: " + list.Count + " hire profile(s) from " + source
                    + ", cap " + _profileCap + ", grace " + _profileGrace + " h.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mercs: hire profiles from " + source + " rejected - " + ex.Message);
            }
        }

        internal static string ProfileSource { get { return _profileSource; } }
        internal static List<Profile> AllProfiles { get { return _profiles; } }

        internal static Profile ProfileById(string id)
        {
            for (int i = 0; i < _profiles.Count; i++) if (_profiles[i].Id == id) return _profiles[i];
            return null;
        }

        /// <summary>The cards a settlement's trader shows. The military town
        /// has no trader of its own (MilitaryTown.cs, N5), so its profiles
        /// are offered at the traitor trader, its side.</summary>
        internal static List<Profile> ProfilesFor(string settlement)
        {
            List<Profile> list = new List<Profile>();
            for (int i = 0; i < _profiles.Count; i++)
            {
                Profile p = _profiles[i];
                if (p.Settlement == settlement || (settlement == "traitor" && p.Settlement == "mtown"))
                    list.Add(p);
            }
            return list;
        }

        // ============================================================ roster
        internal sealed class Record
        {
            internal int Id;
            internal string ProfileId = "", Name = "";
            internal float Hp = 1f;
            internal readonly MercMedicine Medicine = new MercMedicine();
            internal readonly MercDownState Down = new MercDownState();
            internal readonly MercMedicAid Aid = new MercMedicAid();
            internal bool Medic, MedicPending;
            internal float HelpAt;
            internal bool DownConfirmed;
            internal double PaidUntil, Deployed;
            internal bool Peaceful, Combat;
            internal MercOrder Order = MercOrder.FollowMe();
            internal readonly MercRaidState Raid = new MercRaidState();
            internal MercReceiptClock Receipt;
            internal MercOrder ReceiptFor;
            internal bool ReceiptFocus;
            internal bool Follow { get { return Order.Mode == MercOrder.Follow; } }
            internal bool Session;        // admin test merc, never saved
            internal MercUnit Unit;
            internal float NextSpawn;
            internal bool Dead, Deserted, PayPending, PayWanted;
            internal float DeadAt, NextPayTry;
            // W-UI3: the last payment that failed (money refunded), for the
            // trader page; null after a payment went through.
            internal string PayError;
            internal float PayErrorAt;
            internal int WarnStage;
            internal bool Selected = true;
            internal float HpSent = -1f, RegenHp;
            internal double DeployedSent = -1;
            // W: hired at a settlement trader - he appears there once, then
            // walks to his order (FOLLOW: the owner).
            internal Vector3 SpawnAt;
            internal Vector3 SpawnBuyer;
            internal MercTraderSpawnSearch SpawnSearch;
            internal bool SpawnSearchPending;
            internal bool SpawnAtSet;
            internal string SpawnScene;
            internal bool Unpaid { get { return !Dead && Deployed > PaidUntil + 0.0001; } }
        }

        internal sealed class WlEntry { internal string Steam, Name; }

        static readonly List<Record> _roster = new List<Record>();
        static readonly List<WlEntry> _wl = new List<WlEntry>();
        static readonly HashSet<string> _wlSet = new HashSet<string>();
        static readonly List<int> _dead = new List<int>();
        static readonly Dictionary<int, MercUnit> _units = new Dictionary<int, MercUnit>();
        static int _serverCap = -1, _serverGrace = -1, _serial, _sessionSerial;
        // W: the roster link (asking, retries, last known roster, UI phase):
        // Revival.MercLinkCore.cs. Support -1 unknown (asking), 0 the master
        // server has no roster, 1 it has.
        static readonly MercLink _link = new MercLink();
        static int _support { get { return _link.Support; } }
        static string _supportNote = "";

        internal static List<Record> Roster { get { return _roster; } }
        internal static List<WlEntry> Whitelist { get { return _wl; } }
        internal static int Support { get { return _support; } }
        internal static int Cap { get { return _serverCap > 0 ? _serverCap : _profileCap; } }
        internal static int GraceHours { get { return _serverGrace > 0 ? _serverGrace : _profileGrace; } }
        internal static bool OwnKillTarget;
        internal static string SupportNote { get { return _supportNote; } }

        internal static int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _roster.Count; i++) if (!_roster[i].Dead && !_roster[i].Deserted) n++;
                return n;
            }
        }

        internal static bool Whitelisted(string steam)
        {
            return steam != null && _wlSet.Contains(steam);
        }

        // B3d: contracts ended on this client (died, dismissed, deserted) until
        // the server's roster no longer lists them. The op is resent until it
        // lands, and a row the server still carries meanwhile never respawns
        // him (a lost "died" must not bring a dead man back).
        sealed class Gone
        {
            internal string Op;
            internal float Hp;
            internal double Deployed;
            internal int Tries;
            internal float NextTry;
            internal bool InFlight, Confirmed;
        }

        static readonly Dictionary<int, Gone> _gone = new Dictionary<int, Gone>();

        /// <summary>Any merc of this client alive (M2: the danger feed idles without).</summary>
        internal static bool Any { get { return _units.Count > 0; } }

        internal static MercUnit UnitOf(object ai)
        {
            Component c = ai as Component;
            MercUnit u;
            return c != null && _units.TryGetValue(c.GetInstanceID(), out u) ? u : null;
        }

        internal static Record RecordOf(MercUnit u)
        {
            for (int i = 0; i < _roster.Count; i++) if (_roster[i].Unit == u) return _roster[i];
            return null;
        }

        // ========================================================== protocol
        sealed class Op
        {
            internal string Name;
            internal string Body;          // null = a plain roster request
            internal int Seq;
            internal int Tries, Resends;
            internal float Due, NotBefore;
            internal Action<string> Done;
        }

        static readonly List<Op> _ops = new List<Op>();
        static Op _flight;
        static int _seq;

        static Type _bmType, _bagType, _containerType, _requestType;
        static MethodInfo _mMessageStorage, _mSend;
        static FieldInfo _fConnection, _fCurrentStorage;
        static object _backend;
        static bool _netLooked;

        static bool NetLook()
        {
            if (_netLooked) return _mMessageStorage != null && _mSend != null && _requestType != null;
            _netLooked = true;
            try
            {
                _bmType = RevivalPlugin.TypeByName("BackendManager");
                if (_bmType == null) return false;
                _mMessageStorage = AccessTools.Method(_bmType, "MessagePlayerStorage", null, null);
                _bagType = _mMessageStorage == null ? null : _mMessageStorage.GetParameters()[0].ParameterType;
                _fConnection = AccessTools.Field(_bmType, "_connection");
                _fCurrentStorage = AccessTools.Field(_bmType, "currentStorage");
                if (_fConnection != null)
                    for (Type t = _fConnection.FieldType; t != null && _mSend == null; t = t.BaseType)
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (m.Name == "sendMessage" && m.GetParameters().Length == 1) { _mSend = m; break; }
                _containerType = _mSend == null ? null : _mSend.GetParameters()[0].ParameterType;
                if (_containerType != null)
                {
                    PropertyInfo p = _containerType.GetProperty("storageListRequest");
                    FieldInfo f = p == null ? AccessTools.Field(_containerType, "storageListRequest") : null;
                    _requestType = p != null ? p.PropertyType : (f != null ? f.FieldType : null);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: storage message lookup - " + ex.Message); }
            bool ok = _mMessageStorage != null && _mSend != null && _requestType != null;
            if (!ok) RevivalPlugin.L.LogWarning("Mercs: the storage message road is incomplete (MessagePlayerStorage "
                + (_mMessageStorage != null) + ", sendMessage " + (_mSend != null) + ", request "
                + (_requestType != null) + ") - no roster, no hiring.");
            return ok;
        }

        static PropertyInfo _pInstance;
        static float _offlineLogAt = -1000f;

        /// <summary>The live BackendManager. NetLook runs first: _bmType is
        /// only set there, and before 6.62 Backend() ran ahead of it, found
        /// no type, returned null and every op ended in err:offline without
        /// a single 7700 message leaving the client.</summary>
        static object Backend()
        {
            UnityEngine.Object live = _backend as UnityEngine.Object;
            if (_backend != null && live != null) return _backend;
            _backend = null;
            if (!NetLook()) return null;
            if (_pInstance == null)
                _pInstance = _bmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object o = _pInstance == null ? null : _pInstance.GetValue(null, null);
            if (o == null)
            {
                Component stats = LocalStatsCached();
                FieldInfo f = stats == null ? null : AccessTools.Field(stats.GetType(), "_backendManager");
                o = f == null ? null : f.GetValue(stats);
            }
            _backend = o;
            if (o == null && Time.time - _offlineLogAt > 60f)
            {
                _offlineLogAt = Time.time;
                RevivalPlugin.L.LogWarning("Mercs: no BackendManager instance yet - roster ops wait.");
            }
            return o;
        }

        static void SetMember(object o, string name, object value)
        {
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite) { p.SetValue(o, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture), null); return; }
            FieldInfo f = AccessTools.Field(t, name);
            if (f != null) f.SetValue(o, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
        }

        static object GetMember(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanRead) return p.GetValue(o, null);
            FieldInfo f = AccessTools.Field(t, name);
            return f == null ? null : f.GetValue(o);
        }

        static bool SendCommand(string text)
        {
            object bm = Backend();
            if (bm == null || !NetLook()) return false;
            object bag = Activator.CreateInstance(_bagType);
            SetMember(bag, "storageId", StorageId);
            SetMember(bag, "storageDescription", text);
            _mMessageStorage.Invoke(bm, new object[] { bag });
            return true;
        }

        /// <summary>storageListRequest{storageId 7700} straight on the
        /// connection: BackendManager.MsgStorageListRequest would overwrite the
        /// open stash's currentStorage. playerId 0 = the server's own id for
        /// the authenticated connection.</summary>
        static bool SendRequest()
        {
            object bm = Backend();
            if (bm == null || !NetLook() || _fConnection == null) return false;
            object conn = _fConnection.GetValue(bm);
            if (conn == null) return false;
            object request = Activator.CreateInstance(_requestType);
            SetMember(request, "storageId", StorageId);
            object container = Activator.CreateInstance(_containerType);
            SetMember(container, "storageListRequest", request);
            _mSend.Invoke(conn, new object[] { container });
            return true;
        }

        static void Enqueue(string name, string args, Action<string> done, float delay)
        {
            Op op = new Op();
            op.Name = name;
            op.Body = name == "get" ? null : args;
            op.Done = done;
            op.NotBefore = Time.time + delay;
            _ops.Add(op);
        }

        static void PumpOps(float now)
        {
            if (_flight != null)
            {
                if (now < _flight.Due) return;
                Op f = _flight;
                f.Tries++;
                if (f.Tries > 3 && (f.Body == null || f.Resends >= 1)) { Finish(f, "timeout"); return; }
                try
                {
                    if (f.Tries > 3) { f.Resends++; f.Tries = 0; SendCommand(Build(f)); }
                    SendRequest();
                }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: roster request - " + ex.Message); }
                f.Due = now + 4f;
                return;
            }
            for (int i = 0; i < _ops.Count; i++)
            {
                Op op = _ops[i];
                if (now < op.NotBefore) continue;
                // Until the server has said it keeps rosters, only asking is allowed.
                if (_support != 1 && op.Body != null)
                {
                    _ops.RemoveAt(i);
                    if (op.Done != null) op.Done(_support == 0 ? "err:no-server-support" : "err:not-ready");
                    return;
                }
                _ops.RemoveAt(i);
                op.Seq = ++_seq;
                _flight = op;
                op.Due = now + 4f;
                try
                {
                    if (op.Body != null && !SendCommand(Build(op))) { Finish(op, "err:offline"); return; }
                    if (!SendRequest()) { Finish(op, "err:offline"); return; }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Mercs: roster op " + op.Name + " - " + ex.Message);
                    Finish(op, "err:send");
                }
                return;
            }
        }

        static string Build(Op op)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Magic).Append('\n');
            sb.Append("seq=").Append(op.Seq.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("op=").Append(op.Name).Append('\n');
            if (!string.IsNullOrEmpty(op.Body)) sb.Append(op.Body);
            return sb.ToString();
        }

        static void Finish(Op op, string result)
        {
            if (_flight == op) _flight = null;
            if (op.Done != null)
            {
                try { op.Done(result); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: op " + op.Name + " callback - " + ex); }
            }
            if (result != "ok") RevivalPlugin.L.LogInfo("Mercs: op " + op.Name + " -> " + result);
        }

        /// <summary>Prefix on BackendManager.MsgStorageListRecieve(list): the
        /// 7700 bag is ours and never reaches the stash code.</summary>
        public static bool StorageListPrefix(object __instance, object __0)
        {
            IList list = __0 as IList;
            if (list == null) return true;
            try
            {
                string mine = null;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object bag = list[i];
                    object id = GetMember(bag, "storageId");
                    if (id == null || Convert.ToInt32(id) != StorageId) continue;
                    object text = GetMember(bag, "storageDescription");
                    mine = text == null ? "" : text.ToString();
                    list.RemoveAt(i);
                }
                bool stashOpen = _fCurrentStorage != null && _fCurrentStorage.GetValue(__instance) != null;
                if (mine != null)
                {
                    OnAnswer(mine);
                    return list.Count > 0 && stashOpen;
                }
                // A plain answer while we asked and no stash is open: this
                // server hands out bags 0/1 for any id - no roster support.
                // W: a server that answered with the roster in this game run
                // has the module - a plain list then is the game's own and
                // passes (our request is simply asked again).
                if (_flight != null && !stashOpen && !_link.Answered)
                {
                    if (_support != 0)
                        RevivalPlugin.L.LogWarning("Mercs: the master server answered the roster request "
                            + "with plain storage - it has no mercenary roster (phase 1S not deployed). "
                            + "Hiring is off; admin test mercs are session-only.");
                    _link.OnNoModule(Time.time);
                    _supportNote = "the master server has no mercenary roster yet";
                    Op f = _flight;
                    Finish(f, "err:no-server-support");
                    return false;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: storage answer - " + ex.Message); }
            return true;
        }

        static void OnAnswer(string text)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0] != Magic)
            {
                RevivalPlugin.L.LogWarning("Mercs: unknown roster answer");
                return;
            }
            bool fallback = _link.OnFallback(Time.time);
            if (_support != 1)
                RevivalPlugin.L.LogInfo("Mercs: the master server keeps the roster"
                    + (fallback ? " - its answer replaces the last known roster." : "."));
            _link.OnAnswer(Time.time);
            _supportNote = "";
            int seq = -1;
            string result = "ok";
            List<string> mercs = new List<string>();
            List<WlEntry> wl = new List<WlEntry>();
            List<int> dead = new List<int>();
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string k = l.Substring(0, eq), v = l.Substring(eq + 1);
                switch (k)
                {
                    case "seq": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq); break;
                    case "result": result = v; break;
                    case "cap": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _serverCap); break;
                    case "grace": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _serverGrace); break;
                    case "serial": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _serial); break;
                    default: RosterLine(k, v, mercs, dead, wl); break;
                }
            }
            Merge(mercs, dead, wl);
            MedicineAnswer(lines);
            MedicAnswer(lines);
            DownAnswer(lines);
            CacheSave(lines);
            if (fallback)
            {
                // Orders given while the last known roster stood in never
                // reached the server (SendOrders waits for support): now.
                List<Record> held = new List<Record>();
                for (int i = 0; i < _roster.Count; i++)
                    if (!_roster[i].Session && !_roster[i].Dead && _roster[i].Unit != null) held.Add(_roster[i]);
                if (held.Count > 0) SendOrders(held);
            }
            if (seq > _seq) _seq = seq;
            Op f = _flight;
            if (f == null) return;
            if (f.Body == null) { Finish(f, "ok"); return; }
            if (seq >= f.Seq) Finish(f, seq == f.Seq ? result : "ok");
            else f.Due = Time.time + 1f;     // not processed yet: ask again soon
        }

        /// <summary>One roster line of an answer (or of the local copy).</summary>
        static void RosterLine(string k, string v, List<string> mercs, List<int> dead, List<WlEntry> wl)
        {
            switch (k)
            {
                case "m": mercs.Add(v); break;
                case "dead":
                    foreach (string d in v.Split(','))
                    {
                        int n;
                        if (int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) dead.Add(n);
                    }
                    break;
                case "w":
                    {
                        string[] c = v.Split('|');
                        if (c.Length >= 2 && c[0].Length > 0)
                        { WlEntry e = new WlEntry(); e.Steam = c[0]; e.Name = c[1]; wl.Add(e); }
                    }
                    break;
            }
        }

        // ------------------------------------------------ last known roster
        // W: the roster rows of the server's last answer, kept per Steam id
        // beside the config. Read only while the server has not answered in
        // this game run (MercLink.Fallback); the first live answer replaces
        // it, so the server stays the authority over who is hired. Written
        // only when an answer changes the rows.
        const string CacheMagic = "NDR-MERC-CACHE-1";
        const double CacheDays = 14.0;
        static string _cacheBody;
        static bool _cacheTried;
        static float _cacheNextTry;
        // A merc hired at a trader steps out there while the owner is within
        // the FOLLOW warp distance (MercsWar MercWarpUnits) of it.
        const float TraderSpawnReach = 1000f;

        static string CachePath()
        {
            string steam = Admin.LocalSteamId();
            if (string.IsNullOrEmpty(steam)) return null;
            for (int i = 0; i < steam.Length; i++) if (steam[i] < '0' || steam[i] > '9') return null;
            return Path.Combine(BepInEx.Paths.ConfigPath, "NextDayRevival.mercs." + steam + ".txt");
        }

        static void CacheSave(string[] lines)
        {
            StringBuilder body = new StringBuilder();
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("m=", StringComparison.Ordinal) || l.StartsWith("dead=", StringComparison.Ordinal)
                    || l.StartsWith("w=", StringComparison.Ordinal) || l.StartsWith("kit=", StringComparison.Ordinal))
                    body.Append(l).Append('\n');
            }
            string text = body.ToString();
            if (text == _cacheBody) return;
            _cacheBody = text;
            try
            {
                string path = CachePath();
                if (path == null) return;
                File.WriteAllText(path, CacheMagic + "\nsaved=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)
                    + "\n" + text);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: last known roster not stored - " + ex.Message); }
        }

        /// <summary>Once per game run, before the first live answer: the
        /// local copy of the server's last roster.</summary>
        static void CacheLoad()
        {
            if (_cacheTried || _link.Answered || Time.time < _cacheNextTry) return;
            _cacheNextTry = Time.time + 2f;
            try
            {
                string path = CachePath();
                if (path == null) return;          // no Steam id yet: again in 2 s
                _cacheTried = true;
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllText(path).Replace("\r", "").Split('\n');
                if (lines.Length < 2 || lines[0] != CacheMagic) return;
                long ticks = 0;
                if (!lines[1].StartsWith("saved=", StringComparison.Ordinal)
                    || !long.TryParse(lines[1].Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return;
                double days = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalDays;
                if (days > CacheDays || days < -1.0) return;
                List<string> mercs = new List<string>();
                List<WlEntry> wl = new List<WlEntry>();
                List<int> dead = new List<int>();
                for (int i = 2; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0) RosterLine(lines[i].Substring(0, eq), lines[i].Substring(eq + 1), mercs, dead, wl);
                }
                if (mercs.Count > Cap) mercs.RemoveRange(Cap, mercs.Count - Cap);
                if (_link.Answered) return;
                Merge(mercs, dead, wl);
                // Cached supplies are informative only. Live server confirmation
                // is required before consuming a persisted medkit.
                _link.OnCacheLoaded();
                RevivalPlugin.L.LogInfo("Mercs: last known roster read (" + mercs.Count + " merc(s), "
                    + days.ToString("0.0", CultureInfo.InvariantCulture) + " days old) - it spawns if the master server "
                    + "has not answered within " + MercLink.Fallback + " s.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: last known roster not read - " + ex.Message); }
        }

        static void Merge(List<string> rows, List<int> dead, List<WlEntry> wl)
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (string row in rows)
            {
                string[] c = row.Split('|');
                if (c.Length < 9) continue;
                int id;
                if (!int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                seen.Add(id);
                if (_gone.ContainsKey(id)) continue;     // ended here, the server has not heard yet
                Record r = Find(id);
                bool fresh = r == null;
                if (fresh) { r = new Record(); r.Id = id; _roster.Add(r); }
                r.ProfileId = c[1];
                float hp; double paid, dep;
                float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out hp);
                double.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out paid);
                double.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dep);
                if (r.Unit == null) r.Hp = Mathf.Clamp01(hp);
                r.PaidUntil = paid;
                if (dep > r.Deployed) r.Deployed = dep;
                if (fresh)
                {
                    r.Order = MercOrder.Decode(c[5]);
                    // Zero HP cached/server rows must never redeploy as healthy.
                    if (hp <= 0f) r.Down.Enter(Time.time);
                    r.Peaceful = c[6] == "1";
                    r.DeployedSent = r.Deployed; r.HpSent = r.Hp;
                    // Last saved in a fight (design 3.4 vector 2: the owner
                    // pulled the cable as he died): he comes back hurt, at
                    // most a quarter; the next state push stores it.
                    if (c[7] == "1" && r.Hp > 0.25f) r.Hp = 0.25f;
                }
                r.Name = string.Join("|", c, 8, c.Length - 8);
                if (r.Unit != null) r.Unit.Name = r.Name;
            }
            _dead.Clear(); _dead.AddRange(dead);
            for (int i = _roster.Count - 1; i >= 0; i--)
            {
                Record r = _roster[i];
                if (r.Session || r.Dead) continue;
                if (seen.Contains(r.Id) && !_dead.Contains(r.Id)) continue;
                // Gone on the server (dismissed, deserted, died elsewhere).
                if (r.Down.Down && _dead.Contains(r.Id)) FinalDown(r, Time.time);
                Despawn(r, !_dead.Contains(r.Id));
                _roster.RemoveAt(i);
            }
            // The server no longer lists an ended contract: settled.
            List<int> settled = null;
            foreach (KeyValuePair<int, Gone> pair in _gone)
                if (!seen.Contains(pair.Key) && !pair.Value.InFlight)
                {
                    if (settled == null) settled = new List<int>();
                    settled.Add(pair.Key);
                }
            if (settled != null) for (int i = 0; i < settled.Count; i++) _gone.Remove(settled[i]);
            _wl.Clear(); _wl.AddRange(wl);
            RebuildWhitelist();
        }

        static void RebuildWhitelist()
        {
            _wlSet.Clear();
            for (int i = 0; i < _wl.Count; i++) _wlSet.Add(_wl[i].Steam);
        }

        static Record Find(int id)
        {
            for (int i = 0; i < _roster.Count; i++) if (_roster[i].Id == id) return _roster[i];
            return null;
        }

        static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        static string N(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        // ===================================================== player helpers
        static Type _psmType, _viewType, _statesType;
        static FieldInfo _fPlayerInfo, _fInfoSteam, _fInfoName, _fCharState;
        static readonly Dictionary<int, Component> _psmByGo = new Dictionary<int, Component>();
        static readonly Dictionary<int, int> _actorByGo = new Dictionary<int, int>();
        static readonly Dictionary<int, Component> _statesByGo = new Dictionary<int, Component>();
        static int _localActor = -1;
        static float _localActorAt = -10f;
        static PropertyInfo _pPlayer, _pId;

        internal static int LocalActor
        {
            get
            {
                if (Time.time - _localActorAt < 1f) return _localActor;
                _localActorAt = Time.time;
                try
                {
                    if (_pPlayer == null)
                    {
                        Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                        _pPlayer = photon == null ? null : photon.GetProperty("player", BindingFlags.Public | BindingFlags.Static);
                    }
                    object player = _pPlayer == null ? null : _pPlayer.GetValue(null, null);
                    if (player != null && _pId == null) _pId = player.GetType().GetProperty("ID");
                    _localActor = player == null || _pId == null ? -1 : Convert.ToInt32(_pId.GetValue(player, null));
                }
                catch { _localActor = -1; }
                return _localActor;
            }
        }

        internal static int ActorOf(GameObject go)
        {
            if (go == null) return -1;
            int key = go.GetInstanceID(), actor;
            if (_actorByGo.TryGetValue(key, out actor)) return actor;
            actor = -1;
            try
            {
                if (_viewType == null) _viewType = RevivalPlugin.TypeByName("PhotonView");
                Component view = _viewType == null ? null : go.GetComponent(_viewType);
                if (view == null && _viewType != null) view = go.GetComponentInChildren(_viewType);
                object o = GetMember(view, "ownerId");
                if (o != null) actor = Convert.ToInt32(o);
            }
            catch { }
            if (_actorByGo.Count > 512) _actorByGo.Clear();
            _actorByGo[key] = actor;
            return actor;
        }

        static Component Psm(GameObject go)
        {
            if (go == null) return null;
            int key = go.GetInstanceID();
            Component c;
            if (_psmByGo.TryGetValue(key, out c) && c != null) return c;
            if (_psmType == null) _psmType = RevivalPlugin.TypeByName("PlayerStatisticsManager");
            if (_psmType == null) return null;
            c = go.GetComponent(_psmType);
            if (c == null) c = go.GetComponentInChildren(_psmType);
            if (_psmByGo.Count > 512) _psmByGo.Clear();
            _psmByGo[key] = c;
            return c;
        }

        static object Info(GameObject go)
        {
            Component psm = Psm(go);
            if (psm == null) return null;
            if (_fPlayerInfo == null) _fPlayerInfo = AccessTools.Field(psm.GetType(), "_playerInfo");
            object info = _fPlayerInfo == null ? null : _fPlayerInfo.GetValue(psm);
            if (info != null && _fInfoSteam == null)
            {
                _fInfoSteam = AccessTools.Field(info.GetType(), "SteamID");
                _fInfoName = AccessTools.Field(info.GetType(), "Name");
            }
            return info;
        }

        internal static string SteamOf(GameObject go)
        {
            try
            {
                object info = Info(go);
                object v = info == null || _fInfoSteam == null ? null : _fInfoSteam.GetValue(info);
                string s = v == null ? null : v.ToString();
                return string.IsNullOrEmpty(s) || s == "0" ? null : s;
            }
            catch { return null; }
        }

        internal static string NameOf(GameObject go)
        {
            try
            {
                object info = Info(go);
                object v = info == null || _fInfoName == null ? null : _fInfoName.GetValue(info);
                string s = v == null ? "" : Clean(v.ToString());
                return s.Length == 0 ? "player" : s;
            }
            catch { return "player"; }
        }

        internal static int FactionOf(GameObject go)
        {
            return go == null ? -1 : Mortar.FactionShield.FactionOf(go);
        }

        /// <summary>The vanilla sensor skips a player in character state 8
        /// (NPC_AI2.SensorNewEnemy IL_007A); so do the mercs.</summary>
        internal static bool PlayerDead(GameObject go)
        {
            try
            {
                if (_statesType == null)
                {
                    _statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                    if (_statesType != null) _fCharState = AccessTools.Field(_statesType, "_characterState");
                }
                if (_statesType == null || _fCharState == null) return false;
                int key = go.GetInstanceID();
                Component c;
                if (!_statesByGo.TryGetValue(key, out c) || c == null)
                {
                    c = go.GetComponent(_statesType);
                    if (c == null) c = go.GetComponentInChildren(_statesType);
                    if (_statesByGo.Count > 512) _statesByGo.Clear();
                    _statesByGo[key] = c;
                }
                return c != null && FastField.GetInt(_fCharState, c) == 8;
            }
            catch { return false; }
        }

        internal static string SideOf(int faction)
        {
            switch (faction)
            {
                case 2: return "civilian";
                case 1: return "looter";
                case 6: return "traitor";
                default: return "neutral";
            }
        }

        internal static string FactionLabel(int faction)
        {
            switch (faction)
            {
                case 0: return "Neutral";
                case 1: return "Looter";
                case 2: return "Peaceful";
                case 3: return "Hermit";
                case 4: return "Wildman";
                case 5: return "Military";
                case 6: return "Traitor";
                default: return "?";
            }
        }

        internal static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
                if (ch >= 32 && ch < 127 && ch != '|' && ch != '=') sb.Append(ch);
            string r = sb.ToString().Trim();
            return r.Length > 24 ? r.Substring(0, 24) : r;
        }

        // ============================================================= money
        static Component _stats;
        static float _statsAt = -10f;
        static MethodInfo _mGetMoney, _mAddMoney;

        static Component LocalStatsCached()
        {
            UnityEngine.Object live = _stats;
            if (live != null) return _stats;
            if (Time.time - _statsAt < 2f) return null;
            _statsAt = Time.time;
            _stats = Admin.LocalStats();
            if (_stats != null)
            {
                _mGetMoney = AccessTools.Method(_stats.GetType(), "GetPlayerMoney", Type.EmptyTypes, null);
                _mAddMoney = AccessTools.Method(_stats.GetType(), "AddPlayerMoney",
                    new Type[] { typeof(int), typeof(bool), typeof(bool) }, null);
            }
            return _stats;
        }

        static int _moneyFrame = -1, _moneyCache = -1;

        /// <summary>The balance, read once per frame (the hire cards ask per card).</summary>
        internal static int Money
        {
            get
            {
                if (_moneyFrame == Time.frameCount) return _moneyCache;
                _moneyFrame = Time.frameCount;
                Component s = LocalStatsCached();
                _moneyCache = -1;
                if (s == null || _mGetMoney == null) return -1;
                try { _moneyCache = Convert.ToInt32(_mGetMoney.Invoke(s, null)); }
                catch { _moneyCache = -1; }
                return _moneyCache;
            }
        }

        /// <summary>Money through the game's own setter, pushed to the backend
        /// (the same call as a trader purchase). Returns the balance after,
        /// or -1 when it failed.</summary>
        internal static int AddMoney(int delta)
        {
            Component s = LocalStatsCached();
            if (s == null || _mAddMoney == null) return -1;
            try
            {
                _mAddMoney.Invoke(s, new object[] { delta, true, false });
                _moneyFrame = -1;
                return Money;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mercs: money - " + ex.Message);
                return -1;
            }
        }

        // ============================================================ install
        internal static void Install(Harmony harmony)
        {
            MercQuick.Install(harmony);
            DownInstall(harmony);
            LoadProfiles(DefaultProfiles, "built-in defaults");
            try
            {
                Type bm = RevivalPlugin.TypeByName("BackendManager");
                MethodInfo recv = bm == null ? null : AccessTools.Method(bm, "MsgStorageListRecieve", null, null);
                if (recv != null)
                    harmony.Patch(recv, new HarmonyMethod(typeof(Mercs).GetMethod("StorageListPrefix")), null, null, null, null);
                else RevivalPlugin.L.LogWarning("Mercs: BackendManager.MsgStorageListRecieve not found - no roster.");
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo apply = null;
                if (npc != null)
                    foreach (MethodInfo m in npc.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (m.Name == "ApplyDamage" && IsDamageRpc(m.GetParameters())) { apply = m; break; }
                if (apply != null)
                    harmony.Patch(apply, new HarmonyMethod(typeof(Mercs).GetMethod("DamagePrefix")), null, null, null, null);
                else RevivalPlugin.L.LogWarning("Mercs: NPC_AI2.ApplyDamage(float,int,int,int,...) not found - "
                    + "owners can hurt their own mercs.");
                // x-merc-no-traitor: the kill credit itself. A merc carries his
                // owner's faction, so a death credited to the owner is an
                // own-faction NPC kill: AddKillData(1, faction) ->
                // CalculateReputationValue(faction, -1) -> reputation down and
                // TraitorTimeLeft = 5400 (CONFIRMED IL).
                MethodInfo credit = npc == null ? null : AccessTools.Method(npc, "SendAddKillData", null, null);
                if (credit != null)
                    harmony.Patch(credit, new HarmonyMethod(typeof(Mercs).GetMethod("KillCreditPrefix")), null, null, null, null);
                else RevivalPlugin.L.LogWarning("Mercs: NPC_AI2.SendAddKillData not found - "
                    + "a merc killed by his owner may still cost reputation.");
                MercRide.Install(harmony);
                MercSupplyDepot.Bind(); // Cold startup binding; no delegate generation in a duty tick.
                MercDanger.Install(harmony);     // M2: blasts and grenades near mercs
                MethodInfo kill = npc == null ? null : AccessTools.Method(npc, "SetKillTarget", null, null);
                if (kill != null)
                    harmony.Patch(kill, new HarmonyMethod(typeof(Mercs).GetMethod("KillTargetPrefix")), null, null, null, null);
                RevivalPlugin.L.LogInfo("Mercs: hooks installed (roster " + (recv != null) + ", damage "
                    + (apply != null) + ", kill credit " + (credit != null) + ", target veto " + (kill != null) + ").");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Mercs: install failed - " + ex);
            }
        }

        /// <summary>Prefix on NPC_AI2.ApplyDamage(damage, part, type, attacker,
        /// point, info). The owner cannot hurt his own merc (D11) - bullets,
        /// grenade and blast splash, the bumper all carry his actor id; the attacker is
        /// remembered for PEACEFUL self-defence; a whitelisted player's round
        /// is taken and reported, never answered.</summary>
        public static bool DamagePrefix(object __instance, ref float __0, int __2, int __3)
        {
            if (_units.Count == 0)
            {
                if (__3 == 0) NpcWar.MercHitOnMaster(__instance as Component);
                return true;
            }
            MercUnit u = UnitOf(__instance);
            if (u == null)
            {
                if (__3 == 0) NpcWar.MercHitOnMaster(__instance as Component);
                return true;
            }
            if (__3 > 0 && __3 == LocalActor) return false;
            // B3c: inside a closed hull or an aircraft, and for a moment after
            // getting out, nothing reaches him (D14; Revival.MercsRide.cs).
            if (DownHit(u, ref __0)) return false;
            if (MercRide.Shielded(u)) return false;
            if (__0 <= 0f) return true;
            // Z K6a: tier armor replaces the old competence reduction. Native
            // gear/tanky protection stays separate; real HP is set at spawn.
            __0 *= u.TierDamageScale;
            Snapshot(u, __0, __2, __3);
            u.DefendUntil = Time.time + 20f;
            u.Fight.Hits++;                  // M2: a hit in cover means the cover failed
            if (__3 > 0)
            {
                GameObject shooter = PlayerByActor(__3);
                string steam = shooter == null ? null : SteamOf(shooter);
                if (steam != null && Whitelisted(steam))
                {
                    MercUi.WhitelistHit(NameOf(shooter), steam, u.Name);
                    return true;
                }
                u.LastAttacker = __3;
                u.AttackerUntil = u.DefendUntil;
                u.NextPlayerScan = 0f;
            }
            return true;
        }

        /// <summary>x-merc-competence: the hit as it landed, for the death
        /// report - where he was, what his brain did, the threat nearest him
        /// (an NPC round carries attacker 0). Arithmetic over at most four
        /// sensed threats; no allocation.</summary>
        static void Snapshot(MercUnit u, float damage, int type, int attacker)
        {
            MercHitNote h = new MercHitNote();
            float now = Time.time;
            h.At = now; h.Damage = damage; h.Type = type; h.Attacker = attacker;
            MercBrain b = u.Fight.Brain;
            if (b != null)
            {
                h.State = b.State; h.Mode = b.Mode; h.InCover = b.Down; h.Up = b.Up;
            }
            MercSense s = u.Sense;
            h.Exposed = s.Exposed && now - s.ExposedAt < 0.8f;
            h.Threats = s.Count;
            h.HitsInFight = u.Fight.Hits + 1;
            h.Health = Mathf.Max(0f, u.Fight.In.Health - (u.MaxHealth > 0f ? damage / u.MaxHealth : 0f));
            h.Distance = -1f;
            Transform by = null;
            if (u.Ai != null && attacker <= 0)
            {
                Vector3 me = u.Ai.transform.position;
                float best = float.MaxValue;
                for (int i = 0; i < s.Count; i++)
                {
                    Transform t = s.Who[i];
                    if (t == null) continue;
                    float d = (s.At[i] - me).sqrMagnitude;
                    if (d < best) { best = d; by = t; }
                }
                if (by != null) h.Distance = Mathf.Sqrt(best);
            }
            u.LastHitBy = by;
            u.LastHit = h;
        }

        /// <summary>NPC_AI2.ApplyDamage is the RPC (damage, damagePart,
        /// damageType, damageOwnerId, direction, PhotonMessageInfo info) -
        /// six parameters (CONFIRMED metadata). Until x-merc-no-traitor the
        /// lookup asked for exactly five, found nothing, and DamagePrefix was
        /// never installed ("damage False" in the log): the owner's rounds,
        /// grenades and bumper killed his merc and he took the kill.</summary>
        internal static bool IsDamageRpc(ParameterInfo[] ps)
        {
            return ps != null && ps.Length >= 5
                && ps[0].ParameterType == typeof(float)
                && ps[3].ParameterType == typeof(int);
        }

        static int _creditVetoes;

        /// <summary>Prefix on NPC_AI2.SendAddKillData(killer), called by
        /// DecreaseHealth on the NPC's owner when the health reaches 0. A merc
        /// is never a kill for his own owner: no AddKillData RPC, so no
        /// kill statistic, no reputation change and no traitor timer. Only
        /// mercs of this client (_units) and only the local player as the
        /// killer; every other death and killer keeps the game's rule.
        /// Runs once per NPC death; nothing per frame.</summary>
        public static bool KillCreditPrefix(object __instance, object __0)
        {
            if (_units.Count == 0 || __0 == null) return true;
            MercUnit u = UnitOf(__instance);
            if (u == null) return true;
            if (DownEligible(u) && !RecordOf(u).Down.Final) return false;
            int local = LocalActor;
            int killer = -2;
            try
            {
                PropertyInfo id = _pId ?? __0.GetType().GetProperty("ID");
                if (id != null) killer = Convert.ToInt32(id.GetValue(__0, null));
            }
            catch { }
            if (!OwnerCredit(local, killer)) return true;
            if (_creditVetoes++ < 20)
                RevivalPlugin.L.LogInfo("Mercs: " + u.Name + "'s death is not credited to his owner "
                    + "(no kill, no reputation, no traitor timer).");
            return false;
        }

        /// <summary>The killer id is the local owner's actor. Pure; shared with
        /// research/merc_traitor_check.py's model.</summary>
        internal static bool OwnerCredit(int localActor, int killerActor)
        {
            return localActor > 0 && killerActor == localActor;
        }

        /// <summary>Prefix on NPC_AI2.SetKillTarget: a merc never takes his
        /// owner, a whitelisted player, or - while PEACEFUL - anyone but the
        /// player who shot him.</summary>
        public static bool KillTargetPrefix(object __instance, object __0)
        {
            if (_units.Count == 0 || OwnKillTarget || __0 == null) return true;
            MercUnit u = UnitOf(__instance);
            if (u == null) return true;
            GameObject go = __0 as GameObject;
            if (go == null) { Component c = __0 as Component; go = c == null ? null : c.gameObject; }
            if (go == null) return true;
            if (go == u.OwnerGo) return false;
            // B3c: a seated merc has no rifle target; the vehicle gun is his.
            if (u.Ride.Carrier != null) return false;
            string steam = SteamOf(go);
            if (steam != null && Whitelisted(steam)) return false;
            if (u.Peaceful && (Time.time >= u.DefendUntil || ActorOf(go) != u.LastAttacker)) return false;
            return true;
        }

        internal static GameObject PlayerByActor(int actor)
        {
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && ActorOf(players[i]) == actor) return players[i];
            return null;
        }

        // ========================================================== lifecycle
        static GameObject _owner;
        static float _nextTick, _nextSpawn, _nextFaction, _nextUpkeep, _nextState;
        static int _ownerFaction = -99;
        static float _lastHour = -1f, _lastHourAt;
        static bool _dayLogged;
        static readonly List<GameObject> _bodies = new List<GameObject>();
        static readonly List<float> _bodyUntil = new List<float>();

        internal static void Tick()
        {
            MercUi.PollCommands();
            float now = Time.time;
            MercMates.Tick(now);            // W: only while the map is open, 1 Hz
            if (now < _nextTick) return;
            _nextTick = now + 0.25f;
            try { Step(now); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Mercs: " + ex); _nextTick = now + 5f; }
        }

        static void Step(float now)
        {
            GameObject owner = MapTools.LocalPlayer();
            if (owner != _owner)
            {
                _owner = owner;
                if (owner != null)
                {
                    // A (re)spawn: ask the server for the roster (and whether it
                    // keeps one) a moment after the character has settled.
                    _nextSpawn = now + 3f;
                    _link.OwnerArrived(now);
                }
                else _link.OwnerLeft();
            }
            // W: never give up on the roster - retries with back-off, a
            // re-read every minute, the last known roster meanwhile.
            if (owner != null)
            {
                if (!_cacheTried && !_link.Answered) CacheLoad();
                if (_link.Due(now)) RequestRoster(0f);
            }
            PumpOps(now);
            Gones(now);
            MedicTick(now);
            DownTick(now, owner);
            Units(now);
            AirDefenceTick(now);
            RaidTick(now);
            OrderReceipts(now);
            SurvivalOwner(owner, now);
            MercRide.StepOwner(now, owner);
            if (owner == null) return;
            if (!_dayLogged) LogDayLength();
            if (now >= _nextFaction) { _nextFaction = now + 2f; FactionCheck(owner); }
            Clock(now);
            if (_pendingGive.Count > 0) PendingGives(now);
            if (now >= _nextSpawn) { _nextSpawn = now + 0.5f; SpawnNext(owner, now); }
            if (now >= _nextUpkeep) { _nextUpkeep = now + 1f; Upkeep(now); }
            if (now >= _nextState) { _nextState = now + 15f; PushState(); }
            Bodies(now);
        }

        internal static void RequestRoster(float delay)
        {
            for (int i = 0; i < _ops.Count; i++) if (_ops[i].Name == "get") return;
            if (_flight != null && _flight.Name == "get") return;
            Enqueue("get", "session=" + _downSession + "\n", _linkDone, delay);
        }

        // One delegate for every roster request (no closure per ask).
        static readonly Action<string> _linkDone = LinkDone;
        static bool _fallbackLogged;

        /// <summary>A roster request ended. An answer already told the link
        /// (OnAnswer, or the prefix for a server without the module); what
        /// is left is a request that got nothing back.</summary>
        static void LinkDone(string result)
        {
            if (result == "ok" || result == "err:no-server-support") return;
            _link.OnFailed(Time.time);
            if (_link.Fails <= 2 || _link.Fails % 10 == 0)
                RevivalPlugin.L.LogWarning("Mercs: roster request -> " + result + " (" + _link.Fails
                    + " in a row), asking again in " + MercLink.Backoff(_link.Fails) + " s"
                    + (_link.Known && !_link.Answered ? "; the last known roster spawns meanwhile." : "."));
        }

        static string _linkText, _reasonText;
        static int _linkKey = int.MinValue, _reasonKey = int.MinValue;

        /// <summary>The link state as one number: the UI texts are built only
        /// when it changes (the trader tab and the L list ask every frame).</summary>
        static int LinkKey(float now)
        {
            return ((_link.Phase * 1000 + _link.RetryIn(now)) * 2 + (_link.OnFallback(now) ? 1 : 0)) * 2 + (Loc.Ru ? 1 : 0);
        }

        /// <summary>For the UI: what the link to the master server is doing.</summary>
        internal static string LinkText()
        {
            float now = Time.time;
            int key = LinkKey(now);
            if (key != _linkKey || _linkText == null) { _linkKey = key; _linkText = BuildLinkText(now); }
            return _linkText;
        }

        static string BuildLinkText(float now)
        {
            switch (_link.Phase)
            {
                case MercLink.PhaseLive: return Loc.T("Контракты хранятся на мастер-сервере.", "Contracts are kept on the master server.");
                case MercLink.PhaseNoModule: return Loc.T("Мастер-сервер пока не поддерживает наёмников - найм выключен.",
                    "The master server has no merc support yet - hiring is off.");
                case MercLink.PhaseSilent:
                    return Loc.T("Мастер-сервер не отвечает - повтор через ", "The master server is not answering - retrying in ")
                        + _link.RetryIn(now) + Loc.T(" с", " s") + FallbackNote(now);
                default: return Loc.T("Спрашиваем мастер-сервер...", "Asking the master server...") + FallbackNote(now);
            }
        }

        static string FallbackNote(float now)
        {
            return _link.OnFallback(now) ? Loc.T(" Ваши наёмники по последнему списку с вами.",
                " Your mercs from the last known roster are with you.") : "";
        }

        static void Units(float now)
        {
            Transform ownerTr = _owner == null ? null : _owner.transform;
            int slot = 0;
            bool vanished = false;
            for (int i = _roster.Count - 1; i >= 0; i--)
            {
                Record r = _roster[i];
                if (r.Dead && now - r.DeadAt > 60f) { _roster.RemoveAt(i); continue; }
            }
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                MercUnit u = r.Unit;
                if (u == null) continue;
                UnityEngine.Object live = u.Ai;
                if (live == null)
                {
                    if (r.Down.Down) { FinalDown(r, now); continue; }
                    // PUN took him with a room change: back beside the owner.
                    Despawn(r, false);
                    r.NextSpawn = now + 3f;
                    vanished = true;
                    continue;
                }
                if (!NpcWar.MercAlive(u.Ai)) { OnDeath(r, now); continue; }
                if (r.Down.Down) { r.Hp = 0f; r.Combat = true; continue; }
                u.Owner = ownerTr; u.OwnerGo = _owner;
                u.Slot = slot++;
                r.Hp = NpcWar.MercHealth(u.Ai);
                r.Combat = NpcWar.MercInCombat(u, 10f);
                // Y S5: active recovery runs through the cover/fight loop.
                // No free competence self-heal in the lifecycle.
                // B3d: paid while walking off (L, P): the debt is under the
                // grace again, so he turns round instead of leaving.
                if (u.Deserting && r.Deployed - r.PaidUntil < GraceHours)
                {
                    u.Deserting = false; r.WarnStage = 0; u.NextOrder = 0f;
                    MercUi.Toast(r.Name + Loc.T(" получил плату и возвращается.", " was paid and comes back."), false);
                }
                if (u.Deserting && (now >= u.DesertUntil
                    || (u.Ai.transform.position - u.DesertTo).sqrMagnitude < 36f))
                {
                    FinishDesert(r);      // takes him out of _roster
                    i--;
                }
            }
            if (vanished) PushState();
        }

        static void SpawnNext(GameObject owner, float now)
        {
            if (PlayerDead(owner)) return;
            // W: an admin test merc always; roster rows once the server has
            // answered in this game run, or from the last known roster when
            // it stays silent (MercLink.SpawnAllowed).
            bool roster = _link.SpawnAllowed(now);
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Unit != null || r.Dead || r.Deserted || r.Down.Down || now < r.NextSpawn) continue;
                if (!r.Session && !roster) continue;
                if (_dead.Contains(r.Id)) continue;
                if (!r.Session && !_fallbackLogged && _link.OnFallback(now))
                {
                    _fallbackLogged = true;
                    RevivalPlugin.L.LogWarning("Mercs: no roster answer from the master server yet - spawning the last "
                        + "known roster; the server's answer corrects it when it comes.");
                }
                if (!Spawn(r, owner)) r.NextSpawn = now + (r.SpawnSearchPending || r.Order.Survive ? 0.5f : 10f);
                return;
            }
        }

        static bool Spawn(Record r, GameObject owner)
        {
            r.SpawnSearchPending = false;
            Profile p = ProfileById(r.ProfileId);
            if (p == null && _profiles.Count > 0)
            {
                // A profile removed from the editor: the contract stands, the
                // man keeps the look of the first card (phase 3 stores terms).
                p = _profiles[0];
            }
            if (p == null) return false;
            Vector3 fwd = owner.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 side = new Vector3(fwd.z, 0f, -fwd.x);
            int n = UnitCount();
            Vector3 want = owner.transform.position - fwd * (10f + 4f * (n / 2)) + side * ((n % 2 == 0 ? -1f : 1f) * 6f);
            if (r.Order.Survive && MercOrder.SceneKey(r.Order.Scene) == MercOrder.SceneKey(MapScene.Current))
            {
                want = r.Order.Centre;
                if (!ShelterSpawn(r, ref want)) return false;
            }
            // Too far (the answer came after the owner left, another map): as ever.
            bool atTrader = r.SpawnAtSet
                && MercOrder.SceneKey(r.SpawnScene) == MercOrder.SceneKey(MapScene.Current)
                && Flat(r.SpawnAt - owner.transform.position) < TraderSpawnReach;
            Vector3 pos;
            if (atTrader)
            {
                // H S1: preserve the purchase side across a delayed answer.
                // No valid escape means retry later, never fall behind a counter.
                if (!MercTraderSpawn.TryPick(ref r.SpawnSearch, r.SpawnAt, r.SpawnBuyer,
                    owner.transform, n, out pos, out r.SpawnSearchPending)) return false;
            }
            else
            {
                if (r.Order.Survive && !RevivalGroundEnemies.TryGround(want, 12f, out pos)) return false;
                if (!RevivalGroundEnemies.TryGround(want, 12f, out pos)
                    && !RevivalGroundEnemies.TryGround(owner.transform.position, 12f, out pos)) return false;
            }
            int faction = FactionOf(owner);
            string key = KeyPrefix + LocalActor + "/" + r.Id;
            RevivalComposition.CrewMan man = p.Loadout(r.Name);
            float grade = MercGrade.Of(p.Precise, p.Fast, p.Tanky, p.Level);
            float health = MercVital.MaxHealth(p.Health, p.Tanky, grade, p.HealthPercent);
            int level = p.Level;
            GameObject settlement = Crew.DropOwnedSquad(pos, new Vector3[] { pos }, SideOf(faction),
                new List<RevivalComposition.CrewMan>(new RevivalComposition.CrewMan[] { man }), key,
                delegate(Component point, int index)
                {
                    SetNumber(point, "Health", health);
                    SetNumber(point, "Level", level);
                });
            if (settlement == null) return false;
            Crew.Forget(settlement);
            Array men = Crew.Men(settlement);
            Component ai = men == null || men.Length == 0 ? null : men.GetValue(0) as Component;
            if (ai == null)
            {
                UnityEngine.Object.Destroy(settlement);
                RevivalPlugin.L.LogWarning("Mercs: " + r.Name + " did not spawn (no NPC built).");
                return false;
            }
            MercUnit u = new MercUnit();
            u.Id = r.Id; u.Name = r.Name; u.Settlement = settlement; u.Ai = ai;
            u.Owner = owner.transform; u.OwnerGo = owner;
            u.Peaceful = r.Peaceful;
            // B3b: a stored point order holds on the map it was given on. A
            // phase 1 "stay" without a point holds where he spawned; an order
            // from another region is dropped for FOLLOW, never walked to.
            MercOrder order = r.Order;
            if (order.Placed && order.Points.Length == 0)
            {
                order = order.For(0, 1);
                order.Points = new Vector3[] { pos };
                order.Scene = MapScene.Current;
                r.Order = order;
            }
            else if (order.Placed && MercOrder.SceneKey(order.Scene) != MercOrder.SceneKey(MapScene.Current))
            {
                RevivalPlugin.L.LogInfo("Mercs: " + r.Name + "'s " + order.Encode().Split('~')[0]
                    + " order belongs to " + order.Scene + ", now FOLLOW.");
                MercUi.Toast(r.Name + Loc.T(": приказ был в другом районе - за мной.",
                    ": his order was for another region - FOLLOW."), false);
                order = MercOrder.FollowMe();
                r.Order = order;
                SendOrders(new List<Record>(new Record[] { r }));
            }
            else if (order.Mode == MercOrder.Attack)
            {
                // merc-attack-orders: a relog, a restart or a new region never
                // resumes an assault - he comes back beside the owner on FOLLOW.
                RevivalPlugin.L.LogInfo("Mercs: " + r.Name + "'s attack order ended with the respawn, now FOLLOW.");
                MercUi.Toast(r.Name + Loc.T(": атака прервана при возвращении - за мной.",
                    ": the attack ended when he came back - FOLLOW."), false);
                order = MercOrder.FollowMe();
                r.Order = order;
                SendOrders(new List<Record>(new Record[] { r }));
            }
            u.Order = order;
            u.RaidCover = r.Raid.Cover == order;
            if (order.Survive) _survivalNotice = true;
            u.Approach = pos + (order.Facing.sqrMagnitude > 0.01f ? order.Facing : fwd) * 280f;
            u.Precise = p.Precise; u.Fast = p.Fast; u.Tanky = p.Tanky; u.AAGunner = p.AAGunner;
            u.Grade = grade;
            u.TierDamageScale = MercVital.DamageScale(grade, p.ArmorPercent);
            u.MaxHealth = health;
            u.Medicine = r.Medicine; u.Medicine.MaxHealth = u.MaxHealth;
            if (r.Session && !r.Medicine.Known) r.Medicine.SessionLoadout();
            if (!NpcWar.StartMerc("merc-" + r.Id, settlement, ai, u, man))
            {
                RevivalPlugin.L.LogWarning("Mercs: NpcWar did not take " + r.Name + ".");
            }
            _units[ai.GetInstanceID()] = u;
            r.Unit = u;
            MedicRole(u, r);
            r.SpawnAtSet = false;
            Specs(ai, p);
            if (r.Hp > 0.01f && r.Hp < 0.995f) SetHealth(ai, health * r.Hp);
            RevivalPlugin.L.LogInfo("Mercs: " + r.Name + " (" + p.Id + ", id " + r.Id + ") spawned at "
                + pos + (atTrader ? " (the trader he was hired at)" : "") + " for " + SideOf(faction) + ", "
                + Mathf.RoundToInt(r.Hp * 100f) + " % health"
                + (r.Session ? ", session-only test merc." : "."));
            return true;
        }

        static int UnitCount()
        {
            int n = 0;
            for (int i = 0; i < _roster.Count; i++) if (_roster[i].Unit != null) n++;
            return n;
        }

        /// <summary>precise: a shorter aiming delay for the vanilla shot at
        /// players (NPCSpecifications.AimingDelay).</summary>
        static void Specs(Component ai, Profile p)
        {
            try
            {
                FieldInfo fs = AccessTools.Field(ai.GetType(), "Specifications");
                object specs = fs == null ? null : fs.GetValue(ai);
                if (specs == null) return;
                FieldInfo aim = AccessTools.Field(specs.GetType(), "AimingDelay");
                if (aim == null || aim.FieldType != typeof(float)) return;
                // x-merc-competence: a quicker aim at players by grade, on top of precise.
                float grade = MercGrade.Of(p.Precise, p.Fast, p.Tanky, p.Level);
                aim.SetValue(specs, (float)aim.GetValue(specs) * (1f - p.Precise / 100f) * MercCompetence.AimDelayScale(grade));
                if (specs.GetType().IsValueType) fs.SetValue(ai, specs);
            }
            catch { }
        }

        static MethodInfo _mSetHealth;
        static ParameterInfo[] _setHealthPs;
        static object[] _setHealthArgs;

        static void SetHealth(Component ai, float value)
        {
            try
            {
                if (_mSetHealth == null) _mSetHealth = AccessTools.Method(ai.GetType(), "SetHealthValue", null, null);
                if (_mSetHealth == null) return;
                if (_setHealthPs == null)
                {
                    // x-merc-competence: the parameter list and the argument array
                    // once (the self-heal calls this every few seconds).
                    _setHealthPs = _mSetHealth.GetParameters();
                    _setHealthArgs = new object[_setHealthPs.Length];
                }
                ParameterInfo[] ps = _setHealthPs;
                object[] args = _setHealthArgs;
                for (int i = 0; i < ps.Length; i++)
                {
                    Type t = ps[i].ParameterType;
                    args[i] = i == 0 ? (object)Mathf.Max(0f, value)
                        : t == typeof(Vector3) ? (object)ai.transform.position
                        : t.IsValueType ? Activator.CreateInstance(t) : null;
                }
                _mSetHealth.Invoke(ai, args);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: stored health not applied - " + ex.Message); }
        }

        static void SetNumber(object o, string name, float value)
        {
            FieldInfo fi = o == null ? null : FastField.Find(o.GetType(), name);
            if (fi == null) return;
            try
            {
                object v = fi.FieldType == typeof(int) ? (object)Mathf.RoundToInt(value)
                    : fi.FieldType == typeof(float) ? (object)value
                    : Convert.ChangeType(value, fi.FieldType, CultureInfo.InvariantCulture);
                fi.SetValue(o, v);
            }
            catch { }
        }

        static MethodInfo _mNetDestroy;

        static void NetDestroy(GameObject go)
        {
            if (go == null) return;
            try
            {
                if (_mNetDestroy == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    _mNetDestroy = photon == null ? null
                        : AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
                }
                if (_mNetDestroy != null) { _mNetDestroy.Invoke(null, new object[] { go }); return; }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: network destroy - " + ex.Message); }
            UnityEngine.Object.Destroy(go);
        }

        static void Despawn(Record r, bool destroy)
        {
            MedicineCancel(r.Unit, Time.time);
            MercUnit u = r.Unit;
            if (u == null) return;
            DownForget(r);
            MercRide.Forget(u);
            NpcWar.StopMerc(u);
            UnityEngine.Object live = u.Ai;
            if (live != null) _units.Remove(u.Ai.GetInstanceID());
            else
            {
                List<int> stale = new List<int>();
                foreach (KeyValuePair<int, MercUnit> pair in _units) if (pair.Value == u) stale.Add(pair.Key);
                for (int i = 0; i < stale.Count; i++) _units.Remove(stale[i]);
            }
            if (destroy && live != null) NetDestroy(u.Ai.gameObject);
            if (u.Settlement != null && (destroy || live == null)) UnityEngine.Object.Destroy(u.Settlement);
            r.Unit = null;
        }

        static void OnDeath(Record r, float now)
        {
            MercUnit u = r.Unit;
            DeathReport(r, u, now);
            r.Dead = true; r.DeadAt = now; r.Hp = 0f;
            DownForget(r);
            MercRide.Forget(u);
            NpcWar.StopMerc(u);
            UnityEngine.Object live = u.Ai;
            if (live != null)
            {
                _units.Remove(u.Ai.GetInstanceID());
                // The body is his loot; it goes after ten minutes (or with the owner).
                _bodies.Add(u.Ai.gameObject); _bodyUntil.Add(now + 600f);
            }
            r.Unit = null;
            // W: the death toast, the radio click (Revival.MercNotify.cs); never filtered
            // (it goes out through MercUi.Death, the page's own death kind).
            MercNotify.Fallen(r.Id, r.Name, live != null ? u.Ai.transform.position : Vector3.zero);
            RevivalPlugin.L.LogInfo("Mercs: " + r.Name + " (id " + r.Id + ") died.");
            EndContract(r, "died");
        }

        /// <summary>x-merc-competence: every merc death, with its cause, to the
        /// BepInEx log and to the owner as a death toast - killer, weapon,
        /// distance, cover, last order, brain state - from the snapshot of the
        /// killing hit, so the next session shows real causes. Once per death.</summary>
        static void DeathReport(Record r, MercUnit u, float now)
        {
            try
            {
                MercHitNote h = u.LastHit;
                string killer = null, weapon = null;
                Vector3 me = u.Ai != null ? u.Ai.transform.position : Vector3.zero;
                if (h.At <= 0f) { killer = "no hit seen"; weapon = "unknown cause"; }
                else if (h.Attacker > 0)
                {
                    GameObject p = PlayerByActor(h.Attacker);
                    killer = (p != null ? NameOf(p) : "actor " + h.Attacker) + " (player)";
                    if (p != null && u.Ai != null) h.Distance = Vector3.Distance(p.transform.position, me);
                }
                else if (u.LastHitBy != null)
                {
                    int item;
                    killer = NpcWar.MercKillerLabel(u.LastHitBy, out item);
                    weapon = MercDeathNote.WeaponName(item);
                    if (u.Ai != null) h.Distance = Vector3.Distance(u.LastHitBy.position, me);
                }
                string order = MercUi.OrderText(r);
                string state = MercBrain.Name(h.State), mode = MercBrain.ModeName(h.Mode);
                string line = MercDeathNote.Line(r.Name, r.Id, killer, weapon, ref h, order, state, mode,
                    Mathf.RoundToInt(u.Grade * 100f));
                if (h.At > 0f && now - h.At > 3f) line += " Last hit " + (now - h.At).ToString("0.0") + " s before death.";
                RevivalPlugin.L.LogInfo(line);
                MercUi.Death(r.Name + Loc.T(": убит - ", ": killed by ") + (killer ?? Loc.T("неизвестно", "unknown"))
                    + " (" + (weapon ?? MercDeathNote.DamageName(h.Type, h.Attacker)) + ", " + MercDeathNote.Metres(h.Distance)
                    + ", " + MercDeathNote.Place(ref h) + "), " + order + ", " + state + ".");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: death report for " + r.Name + " - " + ex.Message); }
        }

        static void Bodies(float now)
        {
            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object live = _bodies[i];
                if (live == null) { _bodies.RemoveAt(i); _bodyUntil.RemoveAt(i); continue; }
                if (now < _bodyUntil[i]) continue;
                GameObject go = _bodies[i];
                Transform settlement = go.transform.root;
                NetDestroy(go);
                if (settlement != null && settlement.gameObject != go) UnityEngine.Object.Destroy(settlement.gameObject);
                _bodies.RemoveAt(i); _bodyUntil.RemoveAt(i);
            }
        }

        static void FactionCheck(GameObject owner)
        {
            int f = FactionOf(owner);
            if (f == _ownerFaction) return;
            bool first = _ownerFaction == -99;
            _ownerFaction = f;
            if (first) return;
            string side = SideOf(f);
            for (int i = 0; i < _roster.Count; i++)
                if (_roster[i].Unit != null)
                {
                    MercUnit u = _roster[i].Unit;
                    MercAA.Release(u);
                    u.AANextSend = Time.time + 1f;
                    NpcWar.MercFaction(u, side);
                }
            if (UnitCount() > 0)
                MercUi.Toast(Loc.T("Наёмники теперь на стороне: ", "Your mercs now fight for: ") + FactionLabel(f), false);
        }

        // ============================================================== clock
        static FieldInfo _fCycle, _fHour;
        static PropertyInfo _pSky;

        static float Hour()
        {
            try
            {
                if (_pSky == null)
                {
                    Type sky = RevivalPlugin.TypeByName("TOD_Sky");
                    _pSky = sky == null ? null : sky.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    _fCycle = sky == null ? null : AccessTools.Field(sky, "Cycle");
                }
                object s = _pSky == null ? null : _pSky.GetValue(null, null);
                object cycle = s == null || _fCycle == null ? null : _fCycle.GetValue(s);
                if (cycle == null) return -1f;
                if (_fHour == null) _fHour = AccessTools.Field(cycle.GetType(), "Hour");
                return _fHour == null ? -1f : Convert.ToSingle(_fHour.GetValue(cycle));
            }
            catch { return -1f; }
        }

        static void LogDayLength()
        {
            _dayLogged = true;
            try
            {
                Type sky = RevivalPlugin.TypeByName("TOD_Sky");
                object s = _pSky == null ? null : _pSky.GetValue(null, null);
                Component c = s as Component;
                Type time = RevivalPlugin.TypeByName("TOD_Time");
                Component t = c == null || time == null ? null : c.GetComponent(time);
                object len = t == null ? null : GetMember(t, "DayLengthInMinutes");
                RevivalPlugin.L.LogInfo("Mercs: in-game day = " + (len == null ? "?" : len.ToString())
                    + " real minutes (TOD_Time.DayLengthInMinutes); upkeep is billed per 24 in-game hours"
                    + " of deployment" + (sky == null ? ", TOD_Sky missing" : "") + ".");
            }
            catch { }
        }

        /// <summary>In-game hours since the last step, wrap-aware and at most
        /// one hour per real second, so a server time resync never bills a day.</summary>
        static void Clock(float now)
        {
            float h = Hour();
            if (h < 0f) return;
            if (_lastHour < 0f) { _lastHour = h; _lastHourAt = now; return; }
            float d = h - _lastHour;
            if (d < -12f) d += 24f;
            float realDt = now - _lastHourAt;
            _lastHour = h; _lastHourAt = now;
            if (d <= 0f) return;
            d = Mathf.Min(d, Mathf.Max(0.05f, realDt));
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Unit == null || r.Dead || r.Deserted) continue;
                r.Deployed += d;
                Regen(r, d);
            }
        }

        const float RegenPerHour = 6f;          // D13: 1 HP per in-game 10 minutes

        /// <summary>B3d (D13): a wounded merc heals slowly while he is deployed
        /// and out of a fight; relog is no heal button (the server lets health
        /// rise by at most 0.1 per state report).</summary>
        static void Regen(Record r, float hours)
        {
            MercUnit u = r.Unit;
            if (r.Combat || u.Deserting || u.MaxHealth <= 0f || r.Hp >= 0.999f
                || (u.Medicine != null && u.Medicine.Active != 0) || u.Sense.Exposed
                || Time.time - u.LastHit.At < MercMedicine.AfterHit) { r.RegenHp = 0f; return; }
            r.RegenHp += hours * RegenPerHour;
            if (r.RegenHp < 1f) return;
            float add = Mathf.Floor(r.RegenHp);
            r.RegenHp -= add;
            float to = Mathf.Min(u.MaxHealth, r.Hp * u.MaxHealth + add);
            SetHealth(u.Ai, to);
            r.Hp = to / u.MaxHealth;
        }

        // ============================================================= upkeep
        static void Upkeep(float now)
        {
            double grace = GraceHours;
            bool busy = MoneyBusy;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead || r.Deserted || r.Unit == null) continue;
                // B3d: a payment asked for in the list (also for a man walking
                // off) goes as soon as the money line is free.
                if (r.PayWanted && !r.PayPending)
                {
                    if (!r.Unpaid) r.PayWanted = false;
                    else if (!busy) { r.PayWanted = false; Pay(r, true); busy = MoneyBusy; continue; }
                }
                if (r.Unit.Deserting) continue;
                if (r.Deployed < r.PaidUntil) { r.WarnStage = 0; continue; }
                if (r.PayPending) continue;
                Profile p = ProfileById(r.ProfileId);
                int upkeep = p == null ? 0 : p.Upkeep;
                if (now >= r.NextPayTry)
                {
                    int money = Money;
                    if (upkeep == 0 || (money >= 0 && money >= upkeep))
                    {
                        if (!busy) { Pay(r, false); busy = MoneyBusy; }
                        continue;                   // paid now, or next in line
                    }
                    r.NextPayTry = now + 30f;
                }
                // W: no desertion clock for a row the server has not confirmed
                // (the last known roster standing in): the server's word first.
                if (!r.Session && _support != 1) continue;
                double over = r.Deployed - r.PaidUntil;
                if (over >= grace) { StartDesert(r, now); continue; }
                int stage = over >= grace - 1.0 ? 3 : over >= grace / 2.0 ? 2 : 1;
                if (stage > r.WarnStage)
                {
                    r.WarnStage = stage;
                    double left = grace - over;
                    MercUi.Toast(r.Name + Loc.T(" без оплаты: уйдёт через ", " is UNPAID: deserts in ")
                        + left.ToString("0.0", CultureInfo.InvariantCulture) + Loc.T(" игровых ч.", " in-game h.")
                        + Loc.T(" Оплата: L, затем P.", " Pay: L, then P."), true);
                }
            }
        }

        /// <summary>Pay one day of upkeep: the money leaves through the game's
        /// own setter first, the server then sees it gone (moneyAfter).</summary>
        internal static void Pay(Record r, bool manual)
        {
            if (r == null || r.Dead || r.PayPending) return;
            Profile p = ProfileById(r.ProfileId);
            int cost = p == null ? 0 : p.Upkeep;
            int money = Money;
            if (cost > 0 && (money < 0 || money < cost))
            {
                if (manual)
                {
                    r.PayError = "no-money"; r.PayErrorAt = Time.time;
                    MercUi.Toast(Loc.T("Не хватает денег на оплату ", "Not enough money to pay ") + r.Name, true);
                }
                return;
            }
            // B3d: a payment that cannot start waits like one refused, so the
            // automatic bill neither spins every second nor stops the clock.
            if (r.Session)
            {
                if (cost > 0 && AddMoney(-cost) < 0) { r.NextPayTry = Time.time + 30f; return; }
                r.PaidUntil += 24.0; r.WarnStage = 0; r.Medicine.Refill();
                MercUi.Toast(r.Name + Loc.T(" оплачен: ", " paid: ") + Money0(cost), false);
                return;
            }
            if (_support != 1) { r.NextPayTry = Time.time + 30f; if (manual) MercUi.Toast(ReasonNoServer(), true); return; }
            // B3d: another hire or payment is on the wire - this one waits its
            // turn (Upkeep starts it when the line is free).
            if (MoneyBusy) { if (manual) r.PayWanted = true; return; }
            int after = cost > 0 ? AddMoney(-cost) : money;
            if (after < 0) { r.NextPayTry = Time.time + 30f; return; }
            r.PayPending = true;
            PayAttempt(r, cost, after, 0, r.PaidUntil);
        }

        /// <summary>B3d: one money op at a time. The server checks each hire
        /// and payment against the STORED balance, which is the balance after
        /// the latest push: two on the wire at once would each find the
        /// other's money gone too (money-mismatch, refund).</summary>
        internal static bool MoneyBusy
        {
            get
            {
                if (_hirePending) return true;
                for (int i = 0; i < _roster.Count; i++) if (_roster[i].PayPending) return true;
                return false;
            }
        }

        /// <summary>One pay op; payment-pending (the server has not seen the
        /// money leave yet) is retried after 1, 2, 4 s, then refunded. A lost
        /// answer is settled by the roster: nothing paid there = refund.</summary>
        static void PayAttempt(Record r, int cost, int after, int attempt, double paidBefore)
        {
            float delay = attempt == 0 ? 0f : (attempt == 1 ? 1f : (attempt == 2 ? 2f : 4f));
            Enqueue("pay", "id=" + N(r.Id) + "\ndays=1\nmoney=" + N(after) + "\n", delegate(string result)
            {
                if (result == "ok")
                {
                    r.PayPending = false; r.WarnStage = 0; r.PayError = null;
                    _nextUpkeep = 0f;                // the next in line goes now
                    MercUi.Toast(r.Name + Loc.T(" оплачен: ", " paid: ") + Money0(cost), false);
                    return;
                }
                if (result == "err:payment-pending" && attempt < 3)
                { PayAttempt(r, cost, after, attempt + 1, paidBefore); return; }
                r.NextPayTry = Time.time + 30f;
                _nextUpkeep = 0f;
                if (result == "timeout")
                {
                    Settle(0, delegate(bool heard)
                    {
                        r.PayPending = false;
                        if (heard && r.PaidUntil > paidBefore + 0.001) { r.PayError = null; return; }
                        if (cost > 0) AddMoney(cost);
                        r.PayError = "timeout"; r.PayErrorAt = Time.time;
                        MercUi.Toast(Loc.T("Оплата не подтверждена, деньги возвращены.", "Payment not confirmed, money refunded."), true);
                    });
                    return;
                }
                r.PayPending = false;
                if (cost > 0) AddMoney(cost);
                r.PayError = result; r.PayErrorAt = Time.time;
                MercUi.Toast(Loc.T("Оплата не прошла (", "Payment failed (") + result + Loc.T("), деньги возвращены.", "), money refunded."), true);
            }, delay);
        }

        /// <summary>B3d: after a lost answer, ask for the roster until it
        /// comes (four tries, 4 s apart) before deciding: a refund on a roster
        /// that never answered could pay back what the server did take.</summary>
        static void Settle(int tries, Action<bool> decide)
        {
            Enqueue("get", null, delegate(string again)
            {
                if (again != "ok" && tries < 3) { Settle(tries + 1, decide); return; }
                decide(again == "ok");
            }, tries == 0 ? 1f : 4f);
        }

        internal static void PayAllDue()
        {
            int n = 0;
            for (int i = 0; i < _roster.Count; i++)
                if (_roster[i].Unpaid && !_roster[i].PayPending) { Pay(_roster[i], true); n++; }
            if (n == 0) MercUi.Toast(Loc.T("Долгов нет.", "Nothing is due."), false);
        }

        static void StartDesert(Record r, float now)
        {
            MercUnit u = r.Unit;
            if (u == null || u.Deserting) return;
            u.Deserting = true;
            Vector3 away = u.Ai.transform.position - (u.Owner == null ? u.Ai.transform.position : u.Owner.position);
            away.y = 0f;
            if (away.sqrMagnitude < 1f) away = u.Ai.transform.forward;
            Vector3 to;
            if (!RevivalGroundEnemies.TryGround(u.Ai.transform.position + away.normalized * 170f, 20f, out to))
                to = u.Ai.transform.position + away.normalized * 170f;
            u.DesertTo = to; u.DesertUntil = now + 30f;
            MercUi.Toast(r.Name + Loc.T(" не получил денег и уходит.", " was not paid and walks away."), true);
        }

        static void FinishDesert(Record r)
        {
            Despawn(r, true);
            r.Deserted = true;
            _roster.Remove(r);
            EndContract(r, "desert");
            RevivalPlugin.L.LogInfo("Mercs: " + r.Name + " (id " + r.Id + ") deserted.");
        }

        /// <summary>B3d: the end of a saved contract goes to the server until
        /// it lands (Gone); a session test merc has nothing to tell.</summary>
        static void EndContract(Record r, string op)
        {
            if (r.Session) return;
            Gone g = new Gone();
            g.Op = op; g.Hp = r.Hp; g.Deployed = r.Deployed;
            _gone[r.Id] = g;
            Gones(Time.time);
        }

        /// <summary>Resend what the server has not taken yet: a transport
        /// failure every 10 s for as long as the game runs (the server always
        /// accepts died and dismiss). A desertion goes with a state row first
        /// (the server checks its own deployed hours, which trail ours by up
        /// to one report); one it still finds not due goes again every 15 s,
        /// eight times, then the server's word stands and he comes back.</summary>
        static void Gones(float now)
        {
            if (_gone.Count == 0 || _support != 1) return;
            foreach (KeyValuePair<int, Gone> pair in _gone)
            {
                Gone g = pair.Value;
                if (g.InFlight || g.Confirmed || now < g.NextTry) continue;
                int id = pair.Key;
                g.InFlight = true; g.Tries++;
                if (g.Op == "desert")
                    Enqueue("state", "s=" + N(id) + "|" + N(g.Hp) + "|" + N(g.Deployed) + "|0\n", null, 0f);
                Enqueue(g.Op, "id=" + N(id) + "\n", delegate(string result)
                {
                    g.InFlight = false;
                    if (result == "ok") { g.Confirmed = true; return; }
                    if (result == "err:not-due" && g.Tries >= 8)
                    {
                        _gone.Remove(id);
                        RevivalPlugin.L.LogWarning("Mercs: the master server keeps merc " + id
                            + " (desertion not due by its count) - he stays.");
                        RequestRoster(0f);
                        return;
                    }
                    g.NextTry = Time.time + (result == "err:not-due" ? 15f : 10f);
                }, 0f);
            }
        }

        static void PushState()
        {
            if (_support != 1) return;
            StringBuilder sb = null;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                // B3d: a man PUN just took with the room still has his last
                // health to store (D13: "and on despawn").
                if (r.Session || r.Dead || r.Down.Down) continue;
                if (Mathf.Abs(r.Hp - r.HpSent) < 0.01f && r.Deployed - r.DeployedSent < 0.05
                    && r.Medicine.Revision == r.Medicine.SentRevision) continue;
                if (sb == null) sb = new StringBuilder();
                sb.Append("s=").Append(N(r.Id)).Append('|').Append(N(r.Hp)).Append('|')
                  .Append(N(r.Deployed)).Append('|').Append(r.Combat ? "1" : "0");
                if (r.Medicine.Known) sb.Append('|').Append(MercMedicine.Encode(r.Medicine.Used))
                    .Append('|').Append(MercMedicine.Encode(r.Medicine.Completed));
                sb.Append('\n');
                r.Medicine.SentRevision = r.Medicine.Revision;
                r.HpSent = r.Hp; r.DeployedSent = r.Deployed;
            }
            if (sb != null) Enqueue("state", sb.ToString(), null, 0f);
        }

        // ============================================================ actions
        internal static string ReasonNoServer()
        {
            int key = LinkKey(Time.time);
            if (key != _reasonKey || _reasonText == null) { _reasonKey = key; _reasonText = BuildReason(); }
            return _reasonText;
        }

        static string BuildReason()
        {
            return _support == 0 ? Loc.T("мастер-сервер пока не поддерживает наёмников",
                                         "master server has no merc support yet")
                : _link.Phase == MercLink.PhaseSilent
                    ? Loc.T("сервер не отвечает, повтор через ", "server not answering, retry in ") + _link.RetryIn(Time.time) + Loc.T(" с", " s")
                    : Loc.T("связь с сервером...", "asking the master server...");
        }

        /// <summary>Why a card cannot be hired now, or null.</summary>
        internal static string HireBlock(Profile p)
        {
            if (_support != 1) return ReasonNoServer();
            if (AliveCount >= Cap) return AliveCount + " / " + Cap + Loc.T(" наёмников", " mercs");
            if (_hirePending) return Loc.T("найм...", "hiring...");
            if (MoneyBusy) return Loc.T("идёт оплата...", "paying upkeep...");
            int money = Money;
            if (money < 0) return Loc.T("персонаж не загружен", "character not loaded");
            if (money < p.Price) return Loc.T("не хватает денег", "not enough money");
            return null;
        }

        static bool _hirePending;
        // W: the trader the pending hire was made at (one hire at a time).
        static Vector3 _hireAt;
        static Vector3 _hireBuyer;
        static bool _hireAtSet;
        // W-UI3: the card on the wire and the last refused hire (trader page).
        static string _hireProfile, _hireError, _hireErrorProfile;
        static float _hireErrorAt;
        static string _hireScene;

        internal static bool HirePending { get { return _hirePending; } }
        internal static string HireProfile { get { return _hirePending ? _hireProfile : null; } }
        internal static string HireError { get { return _hireError; } }
        internal static string HireErrorProfile { get { return _hireErrorProfile; } }
        internal static float HireErrorAt { get { return _hireErrorAt; } }
        internal static int LinkPhase { get { return _link.Phase; } }

        internal static void Hire(Profile p)
        {
            string why = HireBlock(p);
            if (why != null) { MercUi.Toast(why, true); if (_support != 1) { _link.AskNow(Time.time); RequestRoster(0f); } return; }
            int serialBefore = _serial;
            int after = AddMoney(-p.Price);
            if (after < 0) { MercUi.Toast(Loc.T("Ошибка денег.", "Money error."), true); return; }
            _hirePending = true;
            _hireProfile = p.Id; _hireError = null;
            _hireAtSet = MercUi.TraderPoint(out _hireAt);
            _hireBuyer = _owner == null ? _hireAt : _owner.transform.position;
            _hireScene = MapScene.Current;
            HireAttempt(p, after, 0, serialBefore);
        }

        /// <summary>The new contract steps out at the trader he was hired at.</summary>
        static void AtTrader(Record r)
        {
            if (r == null || !_hireAtSet) return;
            r.SpawnAt = _hireAt; r.SpawnAtSet = true; r.SpawnScene = _hireScene;
            r.SpawnBuyer = _hireBuyer;
        }

        static void HireAttempt(Profile p, int after, int attempt, int serialBefore)
        {
            float delay = attempt == 0 ? 0f : (attempt == 1 ? 1f : (attempt == 2 ? 2f : 4f));
            Enqueue("hire", "profile=" + p.Id + "\nmoney=" + N(after) + "\n", delegate(string result)
            {
                if (result == "ok") { _hirePending = false; AtTrader(Joined(serialBefore)); return; }
                if (result == "err:payment-pending" && attempt < 3)
                { HireAttempt(p, after, attempt + 1, serialBefore); return; }
                if (result == "timeout")
                {
                    // Unknown whether it went through: the roster decides.
                    Settle(0, delegate(bool heard)
                    {
                        _hirePending = false;
                        if (_serial > serialBefore) AtTrader(Joined(serialBefore));
                        else
                        {
                            AddMoney(p.Price);
                            _hireError = "timeout"; _hireErrorProfile = p.Id; _hireErrorAt = Time.time;
                            MercUi.Toast(Loc.T("Найм не подтверждён, деньги возвращены.", "Hire not confirmed, money refunded."), true);
                        }
                    });
                    return;
                }
                _hirePending = false;
                AddMoney(p.Price);
                _hireError = result; _hireErrorProfile = p.Id; _hireErrorAt = Time.time;
                MercUi.Toast(Loc.T("Найм отклонён (", "Hire refused (") + result + Loc.T("), деньги возвращены.", "), money refunded."), true);
            }, delay);
        }

        static Record Joined(int serialBefore)
        {
            Record newest = null;
            for (int i = 0; i < _roster.Count; i++)
                if (_roster[i].Id > serialBefore && (newest == null || _roster[i].Id > newest.Id)) newest = _roster[i];
            if (newest == null) return null;
            newest.NextSpawn = 0f;
            _nextSpawn = 0f;
            MercUi.Toast(newest.Name + Loc.T(" нанят - СЛЕДУЕТ за вами", " joined - FOLLOW"), false);
            MercUi.FlashHired(newest.ProfileId);
            return newest;
        }

        internal static void Dismiss(Record r)
        {
            if (r == null) return;
            Despawn(r, true);
            _roster.Remove(r);
            EndContract(r, "dismiss");
            MercUi.Toast(r.Name + Loc.T(" уволен.", " dismissed."), false);
        }

        internal static List<Record> Selection()
        {
            List<Record> list = new List<Record>();
            for (int i = 0; i < _roster.Count; i++)
                if (!_roster[i].Dead && _roster[i].Selected) list.Add(_roster[i]);
            if (list.Count == 0)
                for (int i = 0; i < _roster.Count; i++) if (!_roster[i].Dead) list.Add(_roster[i]);
            return list;
        }

        internal static void Select(int number)
        {
            int alive = 0;
            for (int i = 0; i < _roster.Count; i++) if (!_roster[i].Dead) alive++;
            // B3d: Ctrl+4 with three mercs keeps the selection instead of
            // quietly falling back to all.
            if (number > alive)
            {
                MercUi.Toast(Loc.T("Нет наёмника #", "No merc #") + number, false);
                return;
            }
            int k = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                if (_roster[i].Dead) continue;
                k++;
                _roster[i].Selected = number == 0 || k == number;
            }
            Picked = number != 0;
            List<Record> sel = Selection();
            MercUi.Toast(number == 0 ? Loc.T("Выбраны все наёмники", "All mercs selected")
                : (sel.Count == 1 ? sel[0].Name : "-") + Loc.T(" выбран", " selected"), false);
        }

        /// <summary>G O1: Ctrl+1..5, an L checkbox or a merc's own row picks
        /// mercs; Ctrl+0 clears the pick.</summary>
        internal static bool Picked;
        static readonly List<Record> _onDuty = new List<Record>(8);

        /// <summary>G O1: the player addressed particular mercs (MercTargetPlan.Explicit).</summary>
        internal static bool PickActive()
        {
            int alive = 0, selected = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                if (_roster[i].Dead) continue;
                alive++;
                if (_roster[i].Selected) selected++;
            }
            return MercTargetPlan.Explicit(Picked, selected, alive);
        }

        internal static bool OnDuty(Record r)
        {
            return r.Order != null && (r.Order.Mode == MercOrder.ManGun || r.Order.Mode == MercOrder.ManRadar);
        }

        /// <summary>G O1: who a squad order (FOLLOW, STAY, ATTACK, TAKE COVER
        /// ...) reaches: the picked mercs, or with no pick everyone except the
        /// gun and radar crews, who keep their posts (Announce names them).</summary>
        internal static List<Record> SquadSelection()
        {
            bool pick = PickActive();
            List<Record> all = Selection();
            List<Record> sel = new List<Record>(all.Count);
            _onDuty.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                bool duty = OnDuty(all[i]);
                if (MercTargetPlan.Takes(pick, all[i].Selected, duty)) sel.Add(all[i]);
                else if (duty) _onDuty.Add(all[i]);
            }
            return sel;
        }

        /// <summary>G O1 order feedback: who got the order and which crews
        /// stayed at their guns or the radar.</summary>
        internal static void Announce(string what, List<Record> got)
        {
            if (got.Count == 0 && _onDuty.Count == 0) return;
            StringBuilder sb = new StringBuilder(what).Append(": ");
            if (got.Count == 0) sb.Append(Loc.T("никто", "nobody"));
            for (int i = 0; i < got.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(got[i].Name);
            if (got.Count > 1) sb.Append(" (").Append(Addressed(got)).Append(')');
            if (_onDuty.Count > 0)
            {
                sb.Append(Loc.T(". На посту (пушка/радар) остаются: ", ". Staying on gun/radar: "));
                for (int i = 0; i < _onDuty.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(_onDuty[i].Name);
                sb.Append(Loc.T(" - отметьте в L, чтобы снять", " - check them in L to move them"));
            }
            MercUi.OrderReply(sb.ToString(), got.Count == 0);
            _onDuty.Clear();
        }

        internal static void OrderFollow()
        {
            List<Record> sel = SquadSelection();
            if (sel.Count > 0) Give(sel, MercOrder.FollowMe());
            Announce(Loc.T("ЗА МНОЙ", "FOLLOW"), sel);
        }

        /// <summary>B3c FOLLOW MY VEHICLE: board the vehicle the owner sits in
        /// (now or the next one he takes), else follow on foot.</summary>
        internal static void OrderVehicle()
        {
            List<Record> sel = SquadSelection();
            if (sel.Count > 0) Give(sel, MercOrder.FollowVehicle());
            Announce(Loc.T("ЗА ТЕХНИКОЙ", "VEHICLE"), sel);
        }

        internal static void OrderStay(Vector3 point, Vector3 facing)
        {
            List<Record> sel = SquadSelection();
            if (sel.Count == 0) { Announce(Loc.T("СТОЯТЬ", "STAY"), sel); return; }
            MercOrder o = new MercOrder();
            o.Mode = MercOrder.Stay; o.Points = new Vector3[] { point }; o.Facing = facing;
            Give(sel, o);
            Announce(Loc.T("СТОЯТЬ", "STAY"), sel);
        }

        /// <summary>B3b PATROL: a loop through the owner's points. One point
        /// (or none: the owner's own spot) becomes a 30 m loop around it.</summary>
        internal static void OrderPatrol(List<Vector3> points)
        {
            List<Record> sel = Willing(SquadSelection(), Loc.T("патруль", "patrol"));
            if (sel.Count == 0) { Announce(Loc.T("ПАТРУЛЬ", "PATROL"), sel); return; }
            List<Vector3> route = new List<Vector3>(points);
            if (route.Count > MercOrder.MaxPoints) route.RemoveRange(MercOrder.MaxPoints, route.Count - MercOrder.MaxPoints);
            if (route.Count < 2)
            {
                Vector3 c = route.Count == 1 ? route[0] : OwnerPosition;
                route.Clear();
                for (int i = 0; i < 4; i++)
                {
                    float a = (45f + 90f * i) * Mathf.Deg2Rad;
                    Vector3 p = c + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * 84f;
                    Vector3 g;
                    if (RevivalGroundEnemies.TryGround(p, 20f, out g)) route.Add(g);
                }
                if (route.Count < 2)
                {
                    MercUi.Toast(Loc.T("Здесь нет места для патруля - поставьте точки.",
                        "No walkable loop here - place the points yourself."), true);
                    return;
                }
            }
            MercOrder o = new MercOrder();
            o.Mode = MercOrder.Patrol; o.Points = route.ToArray();
            Give(sel, o);
            Announce(Loc.T("ПАТРУЛЬ", "PATROL"), sel);
        }

        static MercOrder _lastPerim;
        static float _lastPerimAt = -100f;

        /// <summary>B3b SECURE PERIMETER around the point. Picking it again
        /// within a few seconds at about the same spot steps the radius
        /// 15 / 25 / 40 / 60 m instead of moving the centre.</summary>
        internal static void OrderPerimeter(Vector3 point, Vector3 facing)
        {
            List<Record> sel = Willing(SquadSelection(), Loc.T("периметр", "perimeter"));
            if (sel.Count == 0) { Announce(Loc.T("ПЕРИМЕТР", "PERIMETER"), sel); return; }
            MercOrder o = new MercOrder();
            o.Mode = MercOrder.Perimeter; o.Facing = facing; o.RadiusM = MercOrder.Radii[1];
            o.Points = new Vector3[] { point };
            if (_lastPerim != null && Time.time - _lastPerimAt < 8f
                && Flat(_lastPerim.Centre - point) < 60f && SameOrder(sel, _lastPerim))
            {
                o.Points = _lastPerim.Points; o.Facing = _lastPerim.Facing;
                o.RadiusM = NextRadius(_lastPerim.RadiusM);
            }
            _lastPerim = o; _lastPerimAt = Time.time;
            Give(sel, o);
            Announce(Loc.T("ПЕРИМЕТР", "PERIMETER"), sel);
        }

        /// <summary>The list's radius button: the next step for every selected
        /// merc holding a perimeter, centre and sectors unchanged.</summary>
        internal static void CyclePerimeterRadius()
        {
            List<Record> sel = Selection();
            List<Record> held = new List<Record>();
            MercOrder first = null;
            for (int i = 0; i < sel.Count; i++)
                if (sel[i].Order.Mode == MercOrder.Perimeter) { held.Add(sel[i]); if (first == null) first = sel[i].Order; }
            if (first == null) { MercUi.Toast(Loc.T("Никто не держит периметр.", "Nobody holds a perimeter."), false); return; }
            MercOrder o = first.For(0, 1);
            o.RadiusM = NextRadius(first.RadiusM);
            _lastPerim = o; _lastPerimAt = Time.time;
            Give(held, o);

        }

        /// <summary>merc-attack-orders ATTACK: the selected mercs advance from
        /// where they stand to the objective (a point under the crosshair, the
        /// bounded end of a direction, a map click), fight on the way and hold
        /// it. Unpaid and PEACEFUL mercs refuse with the reason; a point too
        /// near, too far or without ground is no order at all.</summary>
        internal static void OrderAttack(Vector3 objective, byte kind)
        {
            List<Record> sel = Willing(SquadSelection(), Loc.T("атака", "attack"));
            if (sel.Count == 0) { Announce(Loc.T("АТАКА", "ATTACK"), sel); return; }
            List<Record> ok = new List<Record>();
            for (int i = 0; i < sel.Count; i++)
            {
                if (!sel[i].Peaceful) { ok.Add(sel[i]); continue; }
                MercUi.Toast(sel[i].Name + Loc.T(" в МИРНОМ режиме и не атакует (K 6 - выключить).",
                    " is PEACEFUL and will not attack (K 6 turns it off)."), true);
            }
            if (ok.Count == 0) return;
            // The corridor starts where the attackers are (the owner's spot for
            // those not in the world yet).
            Vector3 from = Vector3.zero;
            int placed = 0;
            for (int i = 0; i < ok.Count; i++)
                if (ok[i].Unit != null && ok[i].Unit.Ai != null) { from += ok[i].Unit.Ai.transform.position; placed++; }
            from = placed > 0 ? from / placed : OwnerPosition;
            float dist = Flat(objective - from);
            if (dist < MercOrder.AttackMinUnits)
            {
                MercUi.Toast(Loc.T("Цель атаки слишком близко (", "Attack objective too close (") + (dist / 2.8f).ToString("0")
                    + Loc.T(" м) - используйте СТОЯТЬ.", " m) - use STAY."), true);
                return;
            }
            if (dist > MercOrder.AttackMaxUnits)
            {
                MercUi.Toast(Loc.T("Цель атаки слишком далеко: ", "Attack objective too far: ") + (dist / 2.8f).ToString("0")
                    + Loc.T(" м (предел 600 м).", " m (the limit is 600 m)."), true);
                return;
            }
            Vector3 ground;
            if (!RevivalGroundEnemies.TryGround(objective, 12f, out ground))
            {
                MercUi.Toast(Loc.T("У цели атаки нет проходимой земли - приказ не отдан.",
                    "No walkable ground at the attack objective - no order given."), true);
                return;
            }
            MercOrder o = new MercOrder();
            o.Mode = MercOrder.Attack; o.Kind = kind; o.RadiusM = MercOrder.Radii[1];
            o.Points = new Vector3[] { ground, from };
            Vector3 d = ground - from; d.y = 0f;
            o.Facing = d / Mathf.Max(0.01f, d.magnitude);
            o.Team = new MercAttackTeam(ok.Count);
            Give(ok, o);
            Announce(Loc.T("АТАКА", "ATTACK"), ok);
        }

        static float NextRadius(float r)
        {
            for (int i = 0; i < MercOrder.Radii.Length; i++)
                if (MercOrder.Radii[i] > r + 0.5f) return MercOrder.Radii[i];
            return MercOrder.Radii[0];
        }

        static bool SameOrder(List<Record> sel, MercOrder o)
        {
            for (int i = 0; i < sel.Count; i++)
                if (sel[i].Order.Mode != MercOrder.Perimeter || Flat(sel[i].Order.Centre - o.Centre) > 1f) return false;
            return true;
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        /// <summary>D6: an unpaid merc keeps FOLLOW and STAY but refuses the
        /// work orders until he is paid.</summary>
        static List<Record> Willing(List<Record> sel, string what)
        {
            List<Record> ok = new List<Record>();
            for (int i = 0; i < sel.Count; i++)
            {
                if (!sel[i].Unpaid) { ok.Add(sel[i]); continue; }
                MercUi.Toast(sel[i].Name + Loc.T(" не получил плату и отказывается: ", " is unpaid and refuses: ")
                    + what + Loc.T(" (L - оплатить)", " (L - pay him)"), true);
            }
            return ok;
        }

        /// <summary>Hand one order to the selection: each merc his own copy
        /// with his place in the group, the map it belongs to, saved.</summary>
        internal static void OrderAAPost(bool radar, Vector3 selectedPoint)
        {
            // Legacy callers now use owner proximity; no crosshair is required.
            MercStations.Discover(radar);
            MercStations.Order(radar, 0);
        }

        static void Give(List<Record> sel, MercOrder order)
        {
            order.Scene = MapScene.Current;
            order.IssuedAt = Time.time;
            for (int i = 0; i < sel.Count; i++)
            {
                Record r = sel[i];
                bool sheltered = r.Order.Survive || (r.Unit != null && r.Unit.Rally);
                r.Order = order.Mode == MercOrder.Follow || order.Mode == MercOrder.Vehicle ? order : order.For(i, sel.Count);
                if (r.Unit != null)
                {
                    MercAA.Release(r.Unit);
                    if (r.Unit.Order.MoveNear) r.Unit.Move.Reset(MercCoverService.Field, r.Unit.Id);
                    r.Unit.Rally = sheltered && (order.Mode == MercOrder.Follow || order.Mode == MercOrder.Vehicle);
                    // Keep the current M1 cover/M2-M3 retreat while the new
                    // objective and leash are picked up on the next AI Think.
                    r.Unit.Sense.NextPick = 0f; r.Unit.Sense.PickAt = -1000f;
                    r.Unit.Order = r.Order;
                    r.Unit.NextOrder = 0f;
                    r.Unit.Chasing = false;
                    // merc-attack-orders: a new order restarts his attack run; an
                    // attack is on foot - a pending boarding ends now (a seated
                    // rider gets out when the vehicle stands, MercRide).
                    r.Unit.Attack.Reset();
                    if (order.Mode == MercOrder.Attack || order.MoveNear) r.Unit.Ride.Boarding = null;
                }
                Vector3 objective = order.Mode == MercOrder.Follow || order.Mode == MercOrder.Vehicle
                    ? OwnerPosition : r.Order.Centre;
                OrderReceived(r, false, objective);
            }
            SendOrders(sel);
        }

        internal static void TogglePeaceful()
        {
            List<Record> sel = Selection();
            if (sel.Count == 0) return;
            bool on = !sel[0].Peaceful;
            for (int i = 0; i < sel.Count; i++)
            {
                sel[i].Peaceful = on;
                if (sel[i].Unit != null) sel[i].Unit.Peaceful = on;
                MercUi.OrderReply(sel[i].Name + (on ? Loc.T(": мирный режим включён", ": peaceful on")
                    : Loc.T(": мирный режим выключен", ": peaceful off")), false);
            }
            SendOrders(sel);
        }

        static void SendOrders(List<Record> sel)
        {
            if (_support != 1) return;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sel.Count; i++)
            {
                if (sel[i].Session) continue;
                sb.Append("o=").Append(N(sel[i].Id)).Append('|').Append(sel[i].Order.Encode())
                  .Append('|').Append(sel[i].Peaceful ? "1" : "0").Append('\n');
            }
            if (sb.Length > 0) Enqueue("order", sb.ToString(), null, 0f);
        }

        internal static string Addressed(List<Record> sel)
        {
            int alive = 0;
            for (int i = 0; i < _roster.Count; i++) if (!_roster[i].Dead) alive++;
            if (sel.Count == 1) return sel[0].Name;
            return sel.Count == alive ? Loc.T("ВСЕ ", "ALL ") + alive : sel.Count + Loc.T(" из ", " of ") + alive;
        }

        internal static void WhitelistAdd(string steam, string name)
        {
            if (steam == null || Whitelisted(steam)) return;
            if (_wl.Count >= 50) { MercUi.Toast(Loc.T("Белый список полон (50).", "Whitelist is full (50)."), true); return; }
            WlEntry e = new WlEntry(); e.Steam = steam; e.Name = Clean(name);
            _wl.Add(e); RebuildWhitelist();
            if (_support == 1) Enqueue("wl", "act=add\nsteam=" + steam + "\nname=" + e.Name + "\n", null, 0f);
            MercUi.Toast(e.Name + Loc.T(" в белом списке: наёмники не стреляют.", " whitelisted: your mercs hold fire."), false);
        }

        internal static void WhitelistRemove(string steam)
        {
            for (int i = _wl.Count - 1; i >= 0; i--)
                if (_wl[i].Steam == steam)
                {
                    string name = _wl[i].Name;
                    _wl.RemoveAt(i); RebuildWhitelist();
                    if (_support == 1) Enqueue("wl", "act=remove\nsteam=" + steam + "\n", null, 0f);
                    MercUi.Toast(name + Loc.T(" убран из белого списка.", " removed from the whitelist."), false);
                }
        }

        internal static string Money0(int v)
        {
            return v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        }

        internal static Vector3 OwnerPosition
        {
            get { return _owner == null ? Vector3.zero : _owner.transform.position; }
        }

        internal static GameObject OwnerObject { get { return _owner; } }

        // ============================================================== admin
        /// <summary>F8: a merc of any profile for testing. With the server
        /// roster it is a real, saved contract the server grants to listed
        /// admins free of charge; without it a session-only test merc.</summary>
        internal static string AdminGive(string profileId)
        {
            Profile p = ProfileById(profileId);
            if (p == null) return "Mercs: unknown profile " + profileId;
            if (_owner == null) return "Mercs: enter the world first.";
            if (_support == 1) { AdminGrant(p); return "Mercs: asked the master server for a " + p.Name + " (admin grant)."; }
            if (_support == -1)
            {
                // W: the server has not answered yet - the grant waits for its
                // answer (a real, saved merc), at most PendingGiveWait s, then
                // it is a session-only test merc so the admin is never left
                // without one.
                _pendingGive.Add(p.Id);
                _pendingGiveUntil = Time.time + PendingGiveWait;
                _link.AskNow(Time.time);
                RequestRoster(0f);
                return "Mercs: " + p.Name + " granted as soon as the master server answers (session-only test merc after "
                    + PendingGiveWait + " s without an answer).";
            }
            string made = SessionMerc(p);
            return made == null ? "Mercs: 10 test mercs are the limit."
                : "Mercs: session-only test merc " + made + " (the master server has no roster yet - not saved).";
        }

        const float PendingGiveWait = 10f;
        static readonly List<string> _pendingGive = new List<string>();
        static float _pendingGiveUntil;

        /// <summary>F8 grants made before the server answered.</summary>
        static void PendingGives(float now)
        {
            if (_support == -1 && now < _pendingGiveUntil) return;
            for (int i = 0; i < _pendingGive.Count; i++)
            {
                Profile p = ProfileById(_pendingGive[i]);
                if (p == null) continue;
                if (_support == 1) { AdminGrant(p); continue; }
                string made = SessionMerc(p);
                if (made != null)
                    MercUi.Toast(made + Loc.T(" - тестовый наёмник на сессию (сервер не ответил).",
                        " - session-only test merc (no server answer)."), true);
            }
            _pendingGive.Clear();
        }

        /// <summary>The server grants a free contract to a listed admin; he
        /// spawns the moment the answer lists him.</summary>
        static void AdminGrant(Profile p)
        {
            int before = _serial;
            Enqueue("admin-give", "profile=" + p.Id + "\n", delegate(string result)
            {
                if (result == "ok") { Joined(before); return; }
                if (result == "timeout")
                {
                    Settle(0, delegate(bool heard)
                    {
                        if (_serial > before) Joined(before);
                        else MercUi.Toast("admin-give: " + Loc.T("нет ответа сервера", "no answer from the master server"), true);
                    });
                    return;
                }
                MercUi.Toast("admin-give: " + result, true);
            }, 0f);
        }

        static string SessionMerc(Profile p)
        {
            if (AliveCount >= 10) return null;
            Record r = new Record();
            r.Id = --_sessionSerial; r.Session = true;
            r.ProfileId = p.Id; r.Name = p.Name + " #T" + (-r.Id);
            r.PaidUntil = 24.0;
            _roster.Add(r);
            _nextSpawn = 0f;
            return r.Name;
        }

        /// <summary>F8 "Bring my mercs to me": every merc of this owner put
        /// down beside him and set to FOLLOW; the ones not spawned (no roster
        /// answer yet, a failed spawn) spawn now - from the last known roster
        /// if the server is silent - and the roster is asked again.</summary>
        internal static string AdminBring()
        {
            if (_owner == null) return "Mercs: enter the world first.";
            Transform tr = _owner.transform;
            Vector3 fwd = tr.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 side = new Vector3(fwd.z, 0f, -fwd.x);
            List<Record> all = new List<Record>();
            int moved = 0, waiting = 0, stuck = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead || r.Deserted) continue;
                all.Add(r);
                MercUnit u = r.Unit;
                UnityEngine.Object live = u == null ? null : u.Ai;
                if (live == null) { r.NextSpawn = 0f; r.SpawnAtSet = false; waiting++; continue; }
                if (u.Deserting) { stuck++; continue; }
                MercRide.Forget(u);
                int k = moved + stuck;
                Vector3 want = tr.position - fwd * (8f + 4f * (k / 2)) + side * ((k % 2 == 0 ? -1f : 1f) * 5f);
                Vector3 spot;
                if (RevivalGroundEnemies.TryGround(want, 12f, out spot) && NpcWar.MercTeleport(u, spot)) moved++;
                else stuck++;
            }
            _link.AskNow(Time.time);
            RequestRoster(0f);
            if (all.Count == 0)
                return "Mercs: no merc on the roster (" + (_support == 1 ? "server roster read" : ReasonNoServer()) + ").";
            _link.Forced = true;
            _nextSpawn = 0f;
            Give(all, MercOrder.FollowMe());
            return "Mercs: " + moved + " brought to you" + (waiting > 0 ? ", " + waiting + " spawning now" : "")
                + (stuck > 0 ? ", " + stuck + " could not be moved" : "") + " - all on FOLLOW.";
        }

        internal static string AdminBillNow()
        {
            int n = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead) continue;
                if (r.Deployed < r.PaidUntil) r.Deployed = r.PaidUntil + 0.001;
                r.NextPayTry = 0f; n++;
            }
            _nextUpkeep = 0f;
            if (_support == 1) Enqueue("admin-hours", "hours=0\nbill=1\n", null, 0f);
            return "Mercs: upkeep due now for " + n + " merc(s).";
        }

        internal static string AdminAddHours(double hours)
        {
            int n = 0;
            for (int i = 0; i < _roster.Count; i++)
                if (!_roster[i].Dead) { _roster[i].Deployed += hours; n++; }
            if (_support == 1) Enqueue("admin-hours", "hours=" + N(hours) + "\n", null, 0f);
            _nextUpkeep = 0f;
            return "Mercs: +" + hours + " in-game h of deployment for " + n + " merc(s).";
        }

        internal static string AdminKillSelected()
        {
            List<Record> sel = Selection();
            for (int i = 0; i < sel.Count; i++)
            {
                MercUnit u = sel[i].Unit;
                if (u == null) continue;
                UnityEngine.Object live = u.Ai;
                if (live == null) continue;
                bool hit = false;
                u.Ride.Dying = true;
                try
                {
                    for (int k = 0; k < 3 && NpcWar.MercAlive(u.Ai); k++)
                        hit |= Turret.TryDamage(u.Ai.gameObject, "NPC_AI2", "ApplyDamage", 100000f);
                }
                finally { u.Ride.Dying = false; }
                return "Mercs: " + sel[i].Name + (hit ? " shot by the admin." : " could not be damaged.");
            }
            return "Mercs: no spawned merc selected.";
        }

        internal static string AdminClear()
        {
            for (int i = 0; i < _roster.Count; i++) Despawn(_roster[i], true);
            _roster.Clear();
            _gone.Clear();
            _pendingGive.Clear();
            if (_support == 1) Enqueue("admin-clear", "", null, 0f);
            return "Mercs: roster cleared" + (_support == 1 ? " (server asked)." : " (session).");
        }

        internal static string AdminDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Mercs roster: support ").Append(_support).Append(", cap ").Append(Cap)
              .Append(", grace ").Append(GraceHours).Append(" h, serial ").Append(_serial)
              .Append(", profiles ").Append(_profiles.Count).Append(" (").Append(_profileSource).Append(")");
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                sb.Append("\n  ").Append(r.Id).Append(' ').Append(r.Name).Append(" [").Append(r.ProfileId)
                  .Append("] hp ").Append(N(r.Hp)).Append(" paid ").Append(N(r.PaidUntil)).Append(" deployed ")
                  .Append(N(r.Deployed)).Append(" ").Append(r.Order.Encode()).Append(r.Peaceful ? " peaceful" : "")
                  .Append(r.Unit != null ? " spawned" : "").Append(r.Dead ? " DEAD" : "").Append(r.Session ? " session" : "");
            }
            sb.Append("\n  dead ids: ").Append(_dead.Count).Append(", whitelist ").Append(_wl.Count);
            RevivalPlugin.L.LogInfo(sb.ToString());
            return "Mercs: roster written to the log (" + _roster.Count + " merc(s)).";
        }

        internal static string Status()
        {
            float now = Time.time;
            return "Mercs " + AliveCount + "/" + Cap + ", server roster: "
                + (_support == 1 ? "yes" : _support == 0 ? "NO (session-only tests)"
                    : _link.Fails > 0 ? "not answering (" + _link.Fails + " tries, next in " + _link.RetryIn(now) + " s"
                        + (_link.OnFallback(now) ? ", last known roster" : "") + ")" : "asking")
                + ", profiles: " + _profileSource;
        }
    }
}

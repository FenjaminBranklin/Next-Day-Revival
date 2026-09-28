// Next Day: Survival - Revival Toolkit
//
// MILITARY TOWN GAMEPLAY (MT5): the voenny gorodok on the east tile as a hard,
// rewarding destination (docs/ai/tasks/military-town-gameplay.md; the town
// plan and its combat layout: docs/ai/tasks/military-town.md). Acts only with
// [World] EastTile (EastWorld.On), every part behind its own [MilitaryTown]
// key. No new framework - every part hangs on a system that already runs:
//
//   ARTILLERY     four guns in the pits MT-AB1a/b and MT-AB2a/b, raised by
//                 Mortar.RaiseFixed and crewed by ArtyBattery as TOWN posts:
//                 the settlement battery's crew, laying, delay, aim error and
//                 dispersion unchanged, but no drone - a town gun fires only on
//                 what a LIVING spotter of SP1-SP3 sees (Sees: range, sector,
//                 a clear ray), and never into the town itself. Killing every
//                 spotter silences both batteries; a dead gunner ends his gun;
//                 GunHits explosions on a gun wreck it (Explosion).
//   AA SITE       a built-in Patrol route with a Gepard and "hold" on the AA1
//                 platform: RevivalGepardCrew's air-first gunner does the rest.
//   PATROL        a built-in Patrol route: an armed technical over the S3 road
//                 and the town streets.
//   DEFENDERS     built-in RevivalGroundEnemies groups, as the airfield's
//                 pockets: gate, street patrols, the HQ core, battery and AA
//                 guards - and POSTED men (spotters on the chimney, A1 and the
//                 HQ roof; snipers on the fourth floors), held on their post
//                 by LateFrame on every client like the Gepard's crew.
//   LOOT          the airfield's loot engine (Airfield.Slot) with the town's
//                 points keyed by MT building id: tier 4 in the armoury.
//   REINFORCEMENT after ReinforceMinutes of fighting the master sends the
//                 built-in convoy route MT-Reinforce up the S3 road, through
//                 the shared east event budget (Airfield.Full).
//   SCALING       players within ScaleRange of the town: extra groups, a
//                 bigger reinforcement column, a shorter fight clock.
//   RING, NO-FLY  (P6b) the outer defence ring - road checkpoints, breach
//                 posts, a patrol round the fence, the watchtowers - and the
//                 town's no-fly zone: Revival.MilitaryTownRing.cs.
//
//   SIDE (N5)     the town is a TRAITOR settlement: every part above is
//                 spawned on the plugin's "traitor" side (Fraktion: MyFraction
//                 Traitor, hates every faction but Traitor). This is the
//                 game's own hook, the same one the Litvinovka camp uses:
//                 hostility is the per-NPC HatedFractions array checked
//                 against the player's fraction (RE 23), so a player who took
//                 the Traitor side is left alone by the garrison, the guns
//                 (ArtyBattery.HostileToBattery), the Gepard and the no-fly
//                 zone (an owning-faction player aboard is no violator), and
//                 everyone else is shot. The game has no reputation value and
//                 level7 no standing traitor base or traitor trader (RE, the
//                 settlement inventory); the toolkit's traitor trader stays
//                 in Litvinovka. The side is not a setting any more: the old
//                 [MilitaryTown] Faction key (default looter) is not bound,
//                 an existing line in a config file is inert.
//
// Everything keys on the MT building ids of the markers EastZones reads, so it
// works on the greybox now and on the real models later.
//
// SEAMS: RevivalPlugin (BindConfig, Install, Tick, LateFrame), Revival.
// GroundEnemies (AddGroups, GroupsVersion, HoldSpawn), Revival.Patrol
// (RouteLines, RouteReady, Route.Builtin), RevivalConvoy (ConvoyKinds),
// RevivalMortar (RaiseFixed/RemoveFixed), RevivalArtyBattery (town posts,
// SpotterAlive/Sees), Revival.Airfield (LootSlots, the shared budget).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static partial class MilitaryTown
    {
        // ============================================================= config
        internal static ConfigEntry<bool>   CfgEnabled;
        internal static ConfigEntry<bool>   CfgLoot;
        internal static ConfigEntry<float>  CfgArmouryReset;
        internal static ConfigEntry<bool>   CfgDefenders;
        internal static ConfigEntry<float>  CfgRespawn;
        internal static ConfigEntry<bool>   CfgBatteries;
        internal static ConfigEntry<float>  CfgBatteryReset;
        internal static ConfigEntry<int>    CfgGunHits;
        internal static ConfigEntry<float>  CfgSpotterRange;
        internal static ConfigEntry<bool>   CfgAaSite;
        internal static ConfigEntry<bool>   CfgPatrol;
        internal static ConfigEntry<string> CfgPatrolVehicle;
        internal static ConfigEntry<bool>   CfgReinforce;
        internal static ConfigEntry<float>  CfgReinforceMinutes;
        internal static ConfigEntry<float>  CfgReinforceCooldown;
        internal static ConfigEntry<float>  CfgScaleRange;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("MilitaryTown", "Enabled", true,
                "East military town gameplay: artillery batteries with spotters, the "
                + "Gepard AA site, defenders, loot, reinforcements. Acts only with "
                + "[World] EastTile = true; on the home map alone it does nothing.");
            CfgLoot = cfg.Bind("MilitaryTown", "Loot", true,
                "Loot points in the town's buildings by MT id; the HQ armoury is tier 4.");
            CfgArmouryReset = cfg.Bind("MilitaryTown", "ArmouryResetMinutes", 240f,
                "Minutes before a looted armoury (tier 4) point can roll again (5..1440).");
            CfgDefenders = cfg.Bind("MilitaryTown", "Defenders", true,
                "Gate guard, street patrols, the HQ core, battery and AA guards, the "
                + "posted spotters and snipers as ground groups.");
            CfgRespawn = cfg.Bind("MilitaryTown", "DefenderRespawnMinutes", 40f,
                "Minutes after a group is wiped before it comes back (5..240). Never "
                + "while a player is inside the town.");
            CfgBatteries = cfg.Bind("MilitaryTown", "Batteries", true,
                "The two crewed artillery batteries (4 guns). They fire only on what a "
                + "living spotter sees. Needs [Artillery] Enabled and [Mortar] Enabled.");
            CfgBatteryReset = cfg.Bind("MilitaryTown", "BatteryResetMinutes", 90f,
                "Minutes after a gun is ended (crew dead or wrecked) before a fresh gun "
                + "and crew stand in the pit, only with no player near the town (10..600).");
            CfgGunHits = cfg.Bind("MilitaryTown", "GunHits", 2,
                "Explosions (grenade, LAW, tank shell, drone) on a gun that wreck it (1..10).");
            CfgSpotterRange = cfg.Bind("MilitaryTown", "SpotterRange", 1200f,
                "Units a posted spotter sees a man at, with a clear line (200..1600).");
            CfgAaSite = cfg.Bind("MilitaryTown", "AaSite", true,
                "The stationary NPC Gepard on the AA1 platform.");
            CfgPatrol = cfg.Bind("MilitaryTown", "VehiclePatrol", true,
                "One armed vehicle patrol on the S3 road and the town streets.");
            CfgPatrolVehicle = cfg.Bind("MilitaryTown", "PatrolVehicle", "technical",
                "Vehicle of that patrol: technical, btr or tank.");
            CfgReinforce = cfg.Bind("MilitaryTown", "Reinforcements", true,
                "After ReinforceMinutes of fighting a convoy comes up the S3 road into the "
                + "town. Shares the airfield's [Airfield] EventBudget.");
            CfgReinforceMinutes = cfg.Bind("MilitaryTown", "ReinforceMinutes", 8f,
                "Minutes of fighting at the town before the reinforcement convoy is sent "
                + "(solo; shorter with more players) (2..60).");
            CfgReinforceCooldown = cfg.Bind("MilitaryTown", "ReinforceCooldownMinutes", 30f,
                "Minutes after a reinforcement convoy before the next one can come (5..240).");
            CfgScaleRange = cfg.Bind("MilitaryTown", "ScaleRange", 700f,
                "Units around the town in which players count for the difficulty (200..2000).");
            BindRingConfig(cfg);
        }

        internal static bool On
        {
            get { return EastWorld.On && (CfgEnabled == null || CfgEnabled.Value); }
        }

        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        static float F(ConfigEntry<float> c, float fallback, float min, float max)
        { return Mathf.Clamp(c == null ? fallback : c.Value, min, max); }

        internal static bool LootOn { get { return On && B(CfgLoot); } }

        /// <summary>N5: the whole garrison (defenders, gun crews, Gepard,
        /// patrol, convoy, no-fly owner) is Traitor - see the file header.</summary>
        internal const string Side = "traitor";

        internal static string Faction() { return Side; }

        internal static float ArmourySeconds() { return F(CfgArmouryReset, 240f, 5f, 1440f) * 60f; }

        // ================================================================ area

        // The perimeter fence MT-F1 (military-town.md 2.1): x 5452 / 5848,
        // z 602 / 1198. The town centre is the parade's west edge.
        internal const float MinX = 5452f, MaxX = 5848f, MinZ = 602f, MaxZ = 1198f;
        internal static readonly Vector3 Centre = new Vector3(5650f, 0f, 900f);

        /// <summary>N4/N5: radius of the settlement ring round Centre on the
        /// map (NewSettlement.DrawMilitaryTownRing; editor/pois.js draws the
        /// same 380): it covers every corner of the fence MT-F1.</summary>
        internal const float MapRingRadius = 380f;

        internal static bool Inside(Vector3 p, float margin)
        {
            return p.x >= MinX - margin && p.x <= MaxX + margin
                && p.z >= MinZ - margin && p.z <= MaxZ + margin;
        }

        static bool PlayerInside(float margin)
        {
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && Inside(players[i].transform.position, margin)) return true;
            return false;
        }

        static bool PlayerNear(Vector3 p, float range)
        {
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == null) continue;
                if (Flat(players[i].transform.position - p) < range) return true;
            }
            return false;
        }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        static int _level = 1;
        static float _levelAt = -10f;

        /// <summary>THE DIFFICULTY: players within ScaleRange of the town,
        /// 1..4. Asked by the extra groups, the convoy size and the fight
        /// clock.</summary>
        internal static int Level()
        {
            if (Time.time - _levelAt < 2f) return _level;
            _levelAt = Time.time;
            float range = F(CfgScaleRange, 700f, 200f, 2000f);
            int n = 0;
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && Flat(players[i].transform.position - Centre) < range) n++;
            _level = Mathf.Clamp(n, 1, 4);
            return _level;
        }

        // ======================================================= event budget

        /// <summary>A troop landing whose zone or arrow reaches the town is an
        /// east event: it shares the airfield's budget (Airfield.cs).</summary>
        internal static bool TroopTargets(Vector3 zone, List<Vector3> arrow)
        {
            if (!On) return false;
            if (Inside(zone, 150f)) return true;
            if (arrow != null)
                for (int i = 0; i < arrow.Count; i++)
                    if (Inside(arrow[i], 50f)) return true;
            return false;
        }

        internal static bool ConvoyTargets(List<Vector3> route)
        {
            if (!On || route == null) return false;
            for (int i = 0; i < route.Count; i++)
                if (Inside(route[i], 100f)) return true;
            return false;
        }

        // ============================================================== loot

        static Airfield.Slot S(string building, int tier, string pool, float fx, float fz, float chance)
        {
            Airfield.Slot s = new Airfield.Slot(building, tier, pool, fx, fz, chance);
            s.World = true;
            return s;
        }

        /// <summary>Kept inside the HQ compound (fence MT-F2, x 5757..5846,
        /// z 870..1065): the armoury and the staff building are only reached
        /// through the HQ fight, also where a solid greybox shell moves a
        /// point beside it.</summary>
        static Airfield.Slot Hq(Airfield.Slot s, float ax, float az)
        {
            s.Keep = true;
            s.KeepMinX = 5759f; s.KeepMaxX = 5844f; s.KeepMinZ = 872f; s.KeepMaxZ = 1063f;
            s.AnchorX = ax; s.AnchorZ = az;
            return s;
        }

        // Fractions are along the WORLD axes of the building's marker box
        // (x east, z north). Tier 1 field, 2 specialist, 3 signature, 4 the
        // armoury. Chance 0 = the tier's default, 1 = always back after the
        // reset.
        internal static List<Airfield.Slot> LootSlots()
        {
            List<Airfield.Slot> l = new List<Airfield.Slot>();
            // H2 armoury: the best tier of the east, behind the HQ core.
            l.Add(Hq(S("MT-H2", 4, "armoury", -0.42f, -0.30f, 1f), 5780f, 1010f));
            l.Add(Hq(S("MT-H2", 4, "armoury", -0.42f,  0.00f, 1f), 5780f, 1020f));
            l.Add(Hq(S("MT-H2", 4, "armoury", -0.42f,  0.30f, 1f), 5780f, 1030f));
            l.Add(Hq(S("MT-H2", 4, "armoury", -0.15f, -0.38f, 0f), 5790f, 985f));
            l.Add(Hq(S("MT-H2", 4, "armoury", -0.15f,  0.38f, 0f), 5790f, 1055f));
            l.Add(Hq(S("MT-H2", 4, "armoury",  0.10f,  0.00f, 0f), 5815f, 1057f));
            // H1 regiment staff: maps, radio, the duty officers' kit.
            l.Add(Hq(S("MT-H1", 3, "hq", -0.30f,  0.30f, 1f), 5790f, 975f));
            l.Add(Hq(S("MT-H1", 3, "hq",  0.30f,  0.30f, 0f), 5830f, 975f));
            l.Add(Hq(S("MT-H1", 3, "hq", -0.30f, -0.30f, 0f), 5790f, 880f));
            l.Add(Hq(S("MT-H1", 2, "comms", 0.30f, -0.30f, 0f), 5830f, 880f));
            // A3, the officers' block on the parade: the special flats.
            l.Add(S("MT-A3", 3, "officer", -0.30f,  0.20f, 1f));
            l.Add(S("MT-A3", 3, "officer", -0.10f, -0.20f, 0f));
            l.Add(S("MT-A3", 3, "officer",  0.10f,  0.20f, 0f));
            l.Add(S("MT-A3", 3, "officer",  0.30f, -0.20f, 0f));
            // A1, A2, A4, A5: the families' flats.
            l.Add(S("MT-A1", 1, "flat", 0.00f, -0.30f, 0f));
            l.Add(S("MT-A1", 1, "flat", 0.00f,  0.30f, 0f));
            l.Add(S("MT-A2", 1, "flat", -0.30f, 0.00f, 0f));
            l.Add(S("MT-A2", 1, "flat",  0.30f, 0.00f, 0f));
            l.Add(S("MT-A4", 1, "flat", 0.00f, -0.30f, 0f));
            l.Add(S("MT-A4", 1, "flat", 0.00f,  0.30f, 0f));
            l.Add(S("MT-A5", 1, "flat", -0.30f, 0.00f, 0f));
            l.Add(S("MT-A5", 1, "flat",  0.30f, 0.00f, 0f));
            // D1 officers' house DOF: documents, radio, medicine.
            l.Add(S("MT-D1", 2, "comms",  -0.30f, 0.00f, 0f));
            l.Add(S("MT-D1", 2, "comms",   0.00f, 0.20f, 0f));
            l.Add(S("MT-D1", 2, "medical", 0.30f, 0.00f, 0f));
            // V1 voentorg: uniforms and food. C1 canteen, S2, S1.
            l.Add(S("MT-V1", 1, "guard", -0.30f, 0.00f, 0f));
            l.Add(S("MT-V1", 1, "guard",  0.00f, 0.00f, 0f));
            l.Add(S("MT-V1", 1, "field",  0.30f, 0.00f, 0f));
            l.Add(S("MT-C1", 1, "field", -0.20f, 0.00f, 0f));
            l.Add(S("MT-C1", 1, "field",  0.20f, 0.00f, 0f));
            l.Add(S("MT-S2", 2, "medical", 0.00f, 0.00f, 0f));
            l.Add(S("MT-S1", 0, "dressing", -0.25f, 0.00f, 0f));
            l.Add(S("MT-S1", 0, "dressing",  0.25f, 0.00f, 0f));
            // N9a: the school's staff room - civilian kit, the town's
            // nearest thing to a village house.
            l.Add(S("MT-S1", 1, "flat", 0.00f, 0.30f, 0f));
            // B1 boiler house, the garages: tools, fuel, vehicle parts.
            l.Add(S("MT-B1", 2, "tools", -0.25f, 0.00f, 0f));
            l.Add(S("MT-B1", 3, "fuel",   0.25f, 0.00f, 0.4f));
            l.Add(S("MT-G1", 2, "salvage", -0.30f, 0.00f, 0f));
            l.Add(S("MT-G1", 3, "parts",    0.30f, 0.00f, 0.25f));
            l.Add(S("MT-G2", 2, "salvage", -0.30f, 0.00f, 0f));
            l.Add(S("MT-G2", 3, "parts",    0.30f, 0.00f, 0.25f));
            l.Add(S("MT-G3", 2, "salvage", -0.30f, 0.00f, 0f));
            l.Add(S("MT-G3", 3, "parts",    0.00f, 0.00f, 0.35f));
            l.Add(S("MT-G3", 3, "fuel",     0.30f, 0.00f, 0.4f));
            // Gate and towers: modest guard supplies.
            l.Add(S("MT-K1", 1, "guard", 0.00f, 0.00f, 0f));
            l.Add(S("MT-T1", 1, "guard", 0.00f, 0.00f, 0f));
            l.Add(S("MT-T2", 1, "guard", 0.00f, 0.00f, 0f));
            // The batteries: 122 mm shells in the ammunition niches, the
            // crews' kit in each pit.
            l.Add(S("MT-AB1m", 3, "shells", 0.00f, 0.00f, 1f));
            l.Add(S("MT-AB1m", 3, "shells", 0.20f, 0.00f, 0.6f));
            l.Add(S("MT-AB2m", 3, "shells", 0.00f, 0.00f, 1f));
            l.Add(S("MT-AB2m", 3, "shells", 0.20f, 0.00f, 0.6f));
            l.Add(S("MT-AB1a", 2, "gunners", 0.25f, 0.00f, 0f));
            l.Add(S("MT-AB1b", 2, "gunners", 0.25f, 0.00f, 0f));
            l.Add(S("MT-AB2a", 2, "gunners", 0.25f, 0.00f, 0f));
            l.Add(S("MT-AB2b", 2, "gunners", 0.25f, 0.00f, 0f));
            return l;
        }

        // ========================================================= defenders

        sealed class Spec
        {
            internal string Name, Behavior, Class;
            internal int Count, MinPlayers;
            internal float Radius;
            internal Vector3[] Route;
            internal Spec(string name, string behavior, int count, float radius, string cls,
                          int minPlayers, params Vector3[] route)
            {
                Name = name; Behavior = behavior; Count = count; Radius = radius; Class = cls;
                MinPlayers = minPlayers; Route = route;
            }
        }

        static Vector3 P(float x, float z) { return new Vector3(x, 0f, z); }

        // Every point stands on a street, the parade, a courtyard or a yard,
        // outside every greybox footprint (verify.py [34] checks it against
        // the recipe). The posted groups (spotters, snipers) spawn at the foot
        // of their building and are then held on their post (Posts).
        // MinPlayers > 1: only with that many players near the town (Level).
        static readonly Spec[] Specs = new Spec[] {
            new Spec("mt-N1-gate", "guard", 3, 25f, "regular", 1, P(5792f, 1175f)),
            new Spec("mt-R2-street", "patrol", 3, 250f, "regular", 1,
                P(5480f, 1098f), P(5560f, 1098f), P(5646f, 1098f), P(5745f, 1098f), P(5745f, 1060f)),
            new Spec("mt-R4-street", "patrol", 3, 250f, "regular", 1,
                P(5480f, 715f), P(5560f, 715f), P(5646f, 715f), P(5646f, 800f), P(5646f, 870f)),
            new Spec("mt-P1-parade", "patrol", 3, 120f, "regular", 2,
                P(5660f, 880f), P(5660f, 985f), P(5740f, 985f), P(5740f, 880f)),
            new Spec("mt-Y1-yard", "patrol", 2, 80f, "regular", 3,
                P(5550f, 985f), P(5610f, 985f)),
            new Spec("mt-HQ-gate", "guard", 3, 10f, "defender", 1, P(5750f, 930f)),
            new Spec("mt-HQ-core", "guard", 6, 30f, "defender", 1, P(5778f, 1000f)),
            new Spec("mt-HQ-armoury", "guard", 3, 15f, "defender", 1, P(5815f, 1057f)),
            new Spec("mt-HQ-reserve", "guard", 4, 25f, "defender", 3, P(5778f, 1040f)),
            new Spec("mt-AB1-guard", "guard", 2, 20f, "regular", 1, P(5805f, 710f)),
            new Spec("mt-AB2-guard", "guard", 2, 20f, "regular", 1, P(5540f, 1157f)),
            new Spec("mt-AA1-guard", "guard", 2, 15f, "regular", 1, P(5510f, 892f)),
            // the defence ring's watchtowers T1/T2 (P6b, Revival.MilitaryTownRing.cs)
            new Spec("mt-TW1", "waiting", 1, 5f, "regular", 1, P(5470f, 1188f)),
            new Spec("mt-TW2", "waiting", 1, 5f, "regular", 1, P(5470f, 612f)),
            // posted
            new Spec("mt-SP1", "waiting", 1, 5f, "regular", 1, P(5705f, 1180f)),
            new Spec("mt-SP2", "waiting", 1, 5f, "regular", 1, P(5522f, 1000f)),
            new Spec("mt-SP3", "waiting", 1, 5f, "regular", 1, P(5750f, 950f)),
            new Spec("mt-SN1", "waiting", 2, 5f, "sniper", 1, P(5522f, 985f)),
            new Spec("mt-SN2", "waiting", 2, 5f, "sniper", 1, P(5581f, 722f)),
            new Spec("mt-SN3", "waiting", 1, 5f, "sniper", 1, P(5581f, 1090f)),
            new Spec("mt-SN3b", "waiting", 1, 5f, "sniper", 2, P(5610f, 1090f)),
            new Spec("mt-SN4", "waiting", 2, 5f, "sniper", 1, P(5695f, 998f))
        };

        static float RespawnSeconds() { return F(CfgRespawn, 40f, 5f, 240f) * 60f; }

        internal static int GroupsVersion()
        {
            if (!On || !B(CfgDefenders)) return 0;
            return (Faction() + "|mt|" + RespawnSeconds().ToString("0") + "|ring" + RingOn).GetHashCode() | 1;
        }

        static readonly Dictionary<string, string> _keys = new Dictionary<string, string>();
        static readonly Dictionary<string, Spec> _specs = new Dictionary<string, Spec>();

        internal static void AddGroups(List<RevivalGroundEnemies.Group> into)
        {
            if (GroupsVersion() == 0) return;
            string faction = Faction();
            float respawn = RespawnSeconds();
            for (int i = 0; i < Specs.Length + RingSpecs.Length; i++)
            {
                Spec p = i < Specs.Length ? Specs[i] : RingSpecs[i - Specs.Length];
                if (IsRing(p.Name) && !RingOn) continue;
                RevivalGroundEnemies.Group g = new RevivalGroundEnemies.Group();
                g.Name = p.Name; g.Enabled = true; g.Builtin = true;
                g.X = p.Route[0].x; g.Z = p.Route[0].z;
                g.Faction = faction; g.Count = p.Count; g.Behavior = p.Behavior;
                g.Radius = p.Radius; g.Respawn = respawn;
                // Only a route whose ends meet may loop (the parade ring); the
                // closing leg of a street patrol would cut through a block.
                g.Loop = p.Behavior == "patrol" && p.Route.Length > 3
                    && Flat(p.Route[p.Route.Length - 1] - p.Route[0]) < 100f;
                g.Hold = p.Behavior == "patrol" ? 15f : 0f;
                if (p.Behavior == "patrol")
                    for (int k = 0; k < p.Route.Length; k++) g.Route.Add(p.Route[k]);
                RevivalComposition.CrewMan man = new RevivalComposition.CrewMan();
                man.Role = ""; man.Class = p.Class; man.Fpv = false;
                List<RevivalComposition.CrewMan> kit = new List<RevivalComposition.CrewMan>();
                kit.Add(man);
                // The class kit (the sniper's rifle, the defender's MG and
                // armour) the way a troop landing gets it; a regular keeps
                // the map's default kit, like the airfield's pockets.
                if (p.Class != "regular") kit = NpcWar.WithClassDefaults(kit);
                g.Loadout.AddRange(kit);
                string row = p.Name + "\t" + faction + "\t" + p.Count + "\t" + p.Behavior
                    + "\t" + p.Radius.ToString("0") + "\t" + respawn.ToString("0") + "\t" + p.Class;
                for (int k = 0; k < p.Route.Length; k++)
                    row += "\t" + p.Route[k].x.ToString("0") + "," + p.Route[k].z.ToString("0");
                g.Rows.Append(row).Append('\n');
                g.Meta = row;
                g.Key = p.Name + ":" + Fnv(row).ToString("x8");
                _keys[g.Key] = p.Name;
                _specs[p.Name] = p;
                into.Add(g);
            }
        }

        static uint Fnv(string text)
        {
            uint h = 2166136261u;
            for (int i = 0; i < text.Length; i++) { h ^= text[i]; h *= 16777619u; }
            return h;
        }

        /// <summary>A town group waits: for enough players (its MinPlayers),
        /// with its respawn while a player is inside the town, and with its
        /// first spawn while one is close to its post.</summary>
        internal static bool HoldSpawn(RevivalGroundEnemies.Group g)
        {
            if (g == null || !g.Builtin || !g.Name.StartsWith("mt-", StringComparison.Ordinal)) return false;
            Spec spec;
            if (_specs.TryGetValue(g.Name, out spec) && Level() < spec.MinPlayers) return true;
            if (!TownLoaded()) return true;
            if (g.Seen) return PlayerInside(IsRing(g.Name) ? RingHold : 60f);
            if (PlayerNear(new Vector3(g.X, 0f, g.Z), 150f)) return true;
            for (int i = 0; i < g.Route.Count; i++)
                if (PlayerNear(g.Route[i], 150f)) return true;
            return false;
        }

        // ============================================================== posts

        const int Roof = 0, Floor = 1, Height = 2;

        sealed class Post
        {
            internal string Group, Building;
            internal float Fx, Fz, H;
            internal int Mode;
            internal bool Spotter;
            internal float SectorFrom, SectorTo, Range;   // spotters: bearing arc (deg, clockwise), max range
            // runtime, every client
            internal bool Placed;
            internal Vector3 At;
            internal Component Man;
            internal bool Held;
            internal Post(string group, string building, float fx, float fz, int mode, float h)
            { Group = group; Building = building; Fx = fx; Fz = fz; Mode = mode; H = h; }
        }

        static Post Spot(string group, string building, float fx, float fz, int mode, float h,
                         float from, float to, float range)
        {
            Post p = new Post(group, building, fx, fz, mode, h);
            p.Spotter = true; p.SectorFrom = from; p.SectorTo = to; p.Range = range;
            return p;
        }

        // The spotter posts SP1-SP3 and the sniper floors SN1-SN4
        // (military-town.md 3). SP1: the chimney platform at 32 m = 90 u above
        // the base, on the chimney's west side; it sees the north and the
        // west. SP2 the west edge of A1's roof: the glacis below it, short.
        // SP3 the south-east corner of the HQ roof: the east and the south.
        // A spotter stands at the EDGE he looks over - from the middle of a
        // 27 m roof the ray to a man below clips the roof's own edge. The
        // courtyards and the parade need no spotter: the guns never fire
        // inside the fence. The snipers stand on the fourth floor (a
        // fraction of the block's height) and on the roof above it where the
        // block is solid (the greybox).
        static readonly Post[] Posts = new Post[] {
            Spot("mt-SP1", "MT-B1c", -0.62f, 0.00f, Height, 90f, 180f, 60f, 0f),
            Spot("mt-SP2", "MT-A1", -0.45f, 0.30f, Roof, 0f, 190f, 350f, 600f),
            Spot("mt-SP3", "MT-H1", 0.45f, -0.45f, Roof, 0f, 60f, 240f, 0f),
            new Post("mt-SN1", "MT-A1", -0.42f, -0.20f, Floor, 0.5f),
            new Post("mt-SN1", "MT-A1", -0.42f,  0.20f, Floor, 0.5f),
            new Post("mt-SN2", "MT-A5", -0.20f, -0.42f, Floor, 0.5f),
            new Post("mt-SN2", "MT-A5",  0.20f, -0.42f, Floor, 0.5f),
            new Post("mt-SN3", "MT-A2",  0.00f,  0.42f, Floor, 0.5f),
            new Post("mt-SN3b", "MT-A2", 0.30f,  0.42f, Floor, 0.5f),
            new Post("mt-SN4", "MT-D1", -0.20f, -0.42f, Floor, 0.55f),
            new Post("mt-SN4", "MT-D1",  0.20f, -0.42f, Floor, 0.55f),
            // the defence ring's watchtowers, on top of the tower, facing out
            new Post("mt-TW1", "MT-T1", -0.25f, 0.00f, Roof, 0f),
            new Post("mt-TW2", "MT-T2", -0.25f, 0.00f, Roof, 0f)
        };

        static float _markersAt = -1f;
        static bool _placedSaid;

        /// <summary>The town's content scene is loaded (its markers are there)
        /// and has had 20 s to settle: carving obstacles, colliders.</summary>
        internal static bool TownLoaded()
        {
            Vector3 c, h;
            Quaternion r;
            if (!EastZones.Find("MT-H1", out c, out h, out r)) { _markersAt = -1f; return false; }
            if (_markersAt < 0f) _markersAt = Time.time;
            return Time.time - _markersAt >= 20f;
        }

        static bool Resolve(Post p)
        {
            Vector3 centre, half;
            Quaternion rot;
            if (!EastZones.Find(p.Building, out centre, out half, out rot)) return false;
            Vector3 ext = Airfield.Extent(half, rot);
            float x = centre.x + p.Fx * ext.x * 2f, z = centre.z + p.Fz * ext.z * 2f;
            float bottom = centre.y - ext.y, top = centre.y + ext.y;
            float y;
            if (p.Mode == Height) y = bottom + p.H;
            else if (p.Mode == Floor)
            {
                float probe = bottom + p.H * (top - bottom);
                // Inside a solid (greybox) block there is no floor: the roof
                // above the facade instead. A real model has the room.
                if (Physics.CheckSphere(new Vector3(x, probe + 2f, z), 0.6f, ~0, QueryTriggerInteraction.Ignore)
                    || !Highest(new Vector3(x, probe + 3f, z), 7f, out y))
                    y = RoofAt(x, z, top);
            }
            else y = RoofAt(x, z, top);
            p.At = new Vector3(x, y + 0.05f, z);
            p.Placed = true;
            return true;
        }

        static float RoofAt(float x, float z, float top)
        {
            float y;
            return Highest(new Vector3(x, top + 30f, z), 60f, out y) ? y : top;
        }

        /// <summary>The highest solid surface under a point, men and vehicles
        /// left out.</summary>
        static bool Highest(Vector3 from, float depth, out float y)
        {
            y = from.y;
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, depth, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            float best = float.MinValue;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider == null || IsActor(hits[i].collider.transform)) continue;
                if (hits[i].point.y > best) best = hits[i].point.y;
            }
            if (best == float.MinValue) return false;
            y = best;
            return true;
        }

        static Type _npcType, _vgsType;

        static bool IsActor(Transform t)
        {
            if (_npcType == null) _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_vgsType == null) _vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            return (_npcType != null && t.GetComponentInParent(_npcType) != null)
                || (_vgsType != null && t.GetComponentInParent(_vgsType) != null);
        }

        static float _nextMen;
        static readonly List<Component> _found = new List<Component>();

        /// <summary>Every client, every 2 s: the men of the posted groups, by
        /// their Photon spawn key, each group's men in Photon view id order so
        /// every client puts the same man on the same post.</summary>
        static void FindMen(float now)
        {
            if (now < _nextMen) return;
            _nextMen = now + 2f;
            for (int i = 0; i < Posts.Length; i++)
            {
                Post p = Posts[i];
                if (!p.Placed && TownLoaded()) Resolve(p);
                if (p.Man != null && !NpcWar.GroundAlive(p.Man)) { p.Man = null; p.Held = false; }
            }
            if (!_placedSaid && Posts[0].Placed)
            {
                _placedSaid = true;
                string s = "";
                for (int i = 0; i < Posts.Length; i++)
                    s += (i == 0 ? "" : ", ") + Posts[i].Group + " " + Posts[i].At.ToString("0");
                RevivalPlugin.L.LogInfo("MilitaryTown: posts " + s + ".");
            }
            List<Component> npcs = NpcWar.PatrolTargets();
            Dictionary<string, List<Component>> byGroup = new Dictionary<string, List<Component>>();
            for (int i = 0; i < npcs.Count; i++)
            {
                Component ai = npcs[i];
                if (ai == null) continue;
                string key = Crew.GroundKey(ai);
                string name;
                if (key == null || !_keys.TryGetValue(key, out name)) continue;
                List<Component> l;
                if (!byGroup.TryGetValue(name, out l)) { l = new List<Component>(); byGroup[name] = l; }
                l.Add(ai);
            }
            foreach (KeyValuePair<string, List<Component>> kv in byGroup)
            {
                kv.Value.Sort(delegate(Component a, Component b) { return ViewId(a).CompareTo(ViewId(b)); });
                int k = 0;
                for (int i = 0; i < Posts.Length; i++)
                {
                    Post p = Posts[i];
                    if (p.Group != kv.Key) continue;
                    Component man = k < kv.Value.Count ? kv.Value[k] : null;
                    k++;
                    if (man == null || !NpcWar.GroundAlive(man)) continue;
                    if (!ReferenceEquals(man, p.Man)) { p.Man = man; p.Held = false; }
                }
            }
        }

        static Type _viewType;
        static PropertyInfo _viewId;

        static int ViewId(Component ai)
        {
            try
            {
                if (_viewType == null) _viewType = RevivalPlugin.TypeByName("PhotonView");
                if (_viewType == null) return ai.GetInstanceID();
                if (_viewId == null) _viewId = _viewType.GetProperty("viewID");
                Component v = ai.GetComponent(_viewType);
                if (v == null || _viewId == null) return ai.GetInstanceID();
                return (int)_viewId.GetValue(v, null);
            }
            catch { return ai.GetInstanceID(); }
        }

        /// <summary>Every client, late in the frame: each posted man on his
        /// post, his own navigation held off (the Gepard crew's way). Only his
        /// position is set; he turns and shoots as the AI decides.</summary>
        internal static void LateFrame()
        {
            if (!On) return;
            try
            {
                for (int i = 0; i < Posts.Length; i++)
                {
                    Post p = Posts[i];
                    if (!p.Placed || p.Man == null) continue;
                    Transform tr = p.Man.transform;
                    if (tr == null) { p.Man = null; continue; }
                    Park(p.Man);
                    if (!p.Held)
                    {
                        p.Held = true;
                        float yaw = p.Spotter ? (p.SectorFrom + Arc(p) * 0.5f) : Outward(p);
                        tr.rotation = Quaternion.Euler(0f, yaw, 0f);
                    }
                    if ((tr.position - p.At).sqrMagnitude > 0.0025f) tr.position = p.At;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MilitaryTown posts: " + ex.Message);
            }
        }

        static float Outward(Post p)
        {
            // a facade post faces out of its facade
            if (Mathf.Abs(p.Fx) >= Mathf.Abs(p.Fz)) return p.Fx < 0f ? 270f : 90f;
            return p.Fz < 0f ? 180f : 0f;
        }

        static void Park(Component ai)
        {
            NavMeshAgent agent = ai.GetComponent<NavMeshAgent>();
            if (agent == null) agent = ai.GetComponentInChildren<NavMeshAgent>();
            if (agent == null) return;
            try
            {
                if (agent.updatePosition) agent.updatePosition = false;
                if (agent.updateRotation) agent.updateRotation = false;
            }
            catch { }
        }

        // ========================================================== spotters

        static float Arc(Post p)
        {
            float a = p.SectorTo - p.SectorFrom;
            while (a <= 0f) a += 360f;
            return Mathf.Min(a, 360f);
        }

        static bool InSector(Post p, float bearing)
        {
            float a = Arc(p);
            if (a >= 359.9f) return true;
            float d = bearing - p.SectorFrom;
            while (d < 0f) d += 360f;
            while (d >= 360f) d -= 360f;
            return d <= a;
        }

        static bool Alive(Post p) { return p.Placed && p.Man != null && NpcWar.GroundAlive(p.Man); }

        /// <summary>Is any spotter of SP1-SP3 alive on his post? With all
        /// three dead both batteries stay silent.</summary>
        internal static bool SpotterAlive()
        {
            if (!On) return false;
            for (int i = 0; i < Posts.Length; i++)
                if (Posts[i].Spotter && Alive(Posts[i])) return true;
            return false;
        }

        static int SpottersAlive()
        {
            int n = 0;
            for (int i = 0; i < Posts.Length; i++)
                if (Posts[i].Spotter && Alive(Posts[i])) n++;
            return n;
        }

        sealed class Seen { internal float At; internal bool Yes; }
        static readonly Dictionary<int, Seen> _seen = new Dictionary<int, Seen>();
        static float _lastPlayerSpotted = -1000f;

        /// <summary>
        /// THE SPOTTER RULE (military-town.md 3). A living spotter sees the
        /// point: inside his range and sector, with a clear ray from his eye to
        /// the man's chest (the man himself, or something within 4 u of him
        /// such as the car he sits in, may stop it). Never inside the fence:
        /// the batteries cover the approaches, not their own town. Answers are
        /// kept for 0.4 s per target - four guns ask about the same men.
        /// </summary>
        internal static bool Sees(Vector3 at, GameObject target)
        {
            if (!On || target == null) return false;
            if (Inside(at, 10f)) return false;
            int id = target.GetInstanceID();
            Seen s;
            float now = Time.time;
            if (_seen.TryGetValue(id, out s) && now - s.At < 0.4f) return s.Yes;
            if (s == null) { s = new Seen(); _seen[id] = s; }
            if (_seen.Count > 256) { _seen.Clear(); _seen[id] = s; }
            s.At = now;
            s.Yes = false;
            float range = F(CfgSpotterRange, 1200f, 200f, 1600f);
            Vector3 chest = at + Vector3.up * 2.5f;
            for (int i = 0; i < Posts.Length && !s.Yes; i++)
            {
                Post p = Posts[i];
                if (!p.Spotter || !Alive(p)) continue;
                Vector3 eye = p.At + Vector3.up * 4.5f;
                Vector3 d = chest - eye;
                float flat = Flat(d);
                if (flat > (p.Range > 0f ? Mathf.Min(p.Range, range) : range)) continue;
                float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                if (bearing < 0f) bearing += 360f;
                if (!InSector(p, bearing)) continue;
                float dist = d.magnitude;
                if (dist < 1f) { s.Yes = true; break; }
                Vector3 dir = d / dist;
                RaycastHit hit;
                if (!Physics.Raycast(eye + dir * 1.2f, dir, out hit, dist - 1.2f, ~0, QueryTriggerInteraction.Ignore))
                { s.Yes = true; break; }
                Transform root = target.transform.root;
                if (hit.transform != null && hit.transform.root == root) { s.Yes = true; break; }
                if ((hit.point - chest).sqrMagnitude < 16f) { s.Yes = true; break; }
            }
            if (s.Yes && Crocodile.Players().Contains(target)) _lastPlayerSpotted = now;
            return s.Yes;
        }

        // ========================================================= batteries

        sealed class Gun
        {
            internal string Pit, Battery;
            internal Vector3 Face;
            internal int Index, Gen, Id, Hits;
            internal bool Raised, Wrecked, CrewSeen;
            internal Vector3 Spot;
            internal float EndedAt = -1f;
        }

        // Pits of military-town.md 3: AB1 fires W and SW, AB2 W and N. The
        // hull lies on the pit's diagonal (the 11 m hull in the 10 m pit).
        static readonly Gun[] Guns = MakeGuns();

        static Gun[] MakeGuns()
        {
            string[] pits = { "MT-AB1a", "MT-AB1b", "MT-AB2a", "MT-AB2b" };
            Gun[] g = new Gun[pits.Length];
            for (int i = 0; i < pits.Length; i++)
            {
                g[i] = new Gun();
                g[i].Pit = pits[i];
                g[i].Battery = pits[i].Substring(0, 6);
                g[i].Face = i < 2 ? new Vector3(-1f, 0f, -1f) : new Vector3(-1f, 0f, 1f);
                g[i].Index = i;
                g[i].Id = GunId(i, 0);
            }
            return g;
        }

        // Far below any instance id the settlement scan can produce.
        const int IdBase = -2090000000;
        static int GunId(int index, int gen) { return IdBase + index * 10000 + (gen % 10000); }

        static float _nextGuns;
        static readonly Dictionary<string, bool> _batteryOut = new Dictionary<string, bool>();

        static void TickGuns(float now)
        {
            if (!B(CfgBatteries) || now < _nextGuns) return;
            _nextGuns = now + 1f;
            if (!TownLoaded()) return;
            for (int i = 0; i < Guns.Length; i++)
            {
                Gun g = Guns[i];
                if (!g.Raised)
                {
                    Vector3 spot, normal;
                    if (!PitSpot(g.Pit, out spot, out normal)) continue;
                    if (Mortar.RaiseFixed(g.Id, spot, normal, g.Face, g.Pit, Faction()))
                    {
                        g.Raised = true; g.Spot = spot; g.Hits = 0; g.Wrecked = false;
                        g.CrewSeen = false; g.EndedAt = -1f;
                    }
                    continue;
                }
                if (!g.Wrecked && !Mortar.HasFixed(g.Id))
                {
                    // The scene took the gun: raise it again when the town is
                    // back, under a new id so the old post cannot be mistaken
                    // for the new gun's.
                    ArtyBattery.GunLost(g.Id);
                    g.Gen++;
                    g.Id = GunId(g.Index, g.Gen);
                    g.Raised = false;
                    continue;
                }
                bool serves;
                int crew = ArtyBattery.TownCrew(g.Id, out serves);
                if (crew > 0) g.CrewSeen = true;
                bool ended = g.Wrecked || (g.CrewSeen && !serves);
                if (ended && g.EndedAt < 0f)
                {
                    g.EndedAt = now;
                    RevivalPlugin.L.LogInfo("MilitaryTown: gun " + g.Pit + " is out ("
                        + (g.Wrecked ? "wrecked" : "crew dead") + ").");
                }
                if (ended && now - g.EndedAt > F(CfgBatteryReset, 90f, 10f, 600f) * 60f
                    && !PlayerNear(Centre, 450f))
                {
                    GameObject go = ArtyBattery.TownGun(g.Id);
                    Mortar.RemoveFixed(g.Id, true);
                    ArtyBattery.GunLost(g.Id);
                    if (go != null) UnityEngine.Object.Destroy(go);
                    g.Gen++;
                    g.Id = GunId(g.Index, g.Gen);
                    g.Raised = false;
                    RevivalPlugin.L.LogInfo("MilitaryTown: gun " + g.Pit + " reset - a fresh gun and crew.");
                }
            }
            // A battery is out when both its guns are.
            for (int b = 0; b < 2; b++)
            {
                string name = b == 0 ? "MT-AB1" : "MT-AB2";
                bool out2 = Guns[b * 2].EndedAt >= 0f && Guns[b * 2 + 1].EndedAt >= 0f;
                bool was;
                _batteryOut.TryGetValue(name, out was);
                if (out2 == was) continue;
                _batteryOut[name] = out2;
                RevivalPlugin.L.LogInfo("MilitaryTown: battery " + name + (out2 ? " is ended." : " is manned again."));
            }
        }

        /// <summary>The pit's own terrain height and normal - no collider and
        /// no search, so every client stands the gun at the same spot.</summary>
        static bool PitSpot(string pit, out Vector3 spot, out Vector3 normal)
        {
            spot = Vector3.zero;
            normal = Vector3.up;
            Vector3 centre, half;
            Quaternion rot;
            if (!EastZones.Find(pit, out centre, out half, out rot)) return false;
            Terrain t = EastWorld.TerrainAt(centre.x, centre.z);
            if (t == null || t.terrainData == null) return false;
            Vector3 tp = t.transform.position;
            Vector3 size = t.terrainData.size;
            float y = t.SampleHeight(new Vector3(centre.x, 0f, centre.z)) + tp.y;
            normal = t.terrainData.GetInterpolatedNormal((centre.x - tp.x) / size.x, (centre.z - tp.z) / size.z);
            if (normal.y < 0.5f) normal = Vector3.up;
            spot = new Vector3(centre.x, y, centre.z);
            return true;
        }

        /// <summary>Any explosion on this client (the game's own RPC runs on
        /// every client in range, ExplodePostfix). One close to a town gun is
        /// a hit on it; GunHits of them wreck it.</summary>
        internal static void Explosion(Vector3 at, float radius)
        {
            if (!On || !B(CfgBatteries)) return;
            for (int i = 0; i < Guns.Length; i++)
            {
                Gun g = Guns[i];
                if (!g.Raised || g.Wrecked) continue;
                if (Mathf.Abs(at.y - g.Spot.y) > 15f) continue;
                if (Flat(at - g.Spot) > Mathf.Clamp(radius, 2f, 12f) + 12f) continue;
                g.Hits++;
                int need = Mathf.Clamp(CfgGunHits == null ? 2 : CfgGunHits.Value, 1, 10);
                RevivalPlugin.L.LogInfo("MilitaryTown: gun " + g.Pit + " hit by an explosion ("
                    + g.Hits + "/" + need + ").");
                if (g.Hits >= need) Wreck(g);
            }
        }

        static void Wreck(Gun g)
        {
            g.Wrecked = true;
            ArtyBattery.TownWreck(g.Id);
            GameObject go = ArtyBattery.TownGun(g.Id);
            Mortar.RemoveFixed(g.Id, false);
            if (go != null)
            {
                Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null) continue;
                    Material[] ms = rs[i].materials;
                    for (int k = 0; k < ms.Length; k++)
                        if (ms[k] != null && ms[k].HasProperty("_Color"))
                            ms[k].color = ms[k].color * 0.22f;
                }
            }
            try { FireEffect.Spawn(g.Spot + Vector3.up * 2f, 8f); } catch { }
            RevivalPlugin.L.LogInfo("MilitaryTown: gun " + g.Pit + " wrecked.");
        }

        internal static void Install(Harmony harmony)
        {
            if (!On || !B(CfgBatteries)) return;
            try
            {
                Type t = RevivalPlugin.TypeByName("ExplosionObject");
                MethodInfo m = null;
                if (t != null)
                    foreach (MethodInfo cand in t.GetMethods(BindingFlags.Instance
                                 | BindingFlags.Public | BindingFlags.NonPublic))
                        if (cand.Name == "NetworkVisualizeExplode") { m = cand; break; }
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("MilitaryTown: ExplosionObject.NetworkVisualizeExplode "
                        + "not found - the town guns cannot be wrecked, only their crews killed.");
                    return;
                }
                harmony.Patch(m, null, new HarmonyMethod(typeof(MilitaryTown).GetMethod("ExplodePostfix",
                    BindingFlags.Static | BindingFlags.Public)), null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("MilitaryTown: explosion patch: " + ex); }
        }

        public static void ExplodePostfix(object __instance)
        {
            try
            {
                Component c = __instance as Component;
                if (c == null) return;
                Explosion(c.transform.position, Radius(c));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MilitaryTown explosion: " + ex.Message); }
        }

        static float Radius(Component c)
        {
            try
            {
                FieldInfo f = AccessTools.Field(c.GetType(), "ExplodeDamageRadius");
                object v = f == null ? null : f.GetValue(c);
                if (v == null) return 6f;
                if (v is float) return (float)v;
                MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float))
                        return (float)ms[i].Invoke(null, new object[] { v });
            }
            catch { }
            return 6f;
        }

        // ============================================== built-in vehicle routes

        internal const string AaRoute = "MT-AA1", PatrolRoute = "MT-Patrol", ReinforceRoute = "MT-Reinforce";

        /// <summary>Route file lines for Patrol.Load: the AA site (a held
        /// Gepard), the vehicle patrol and the reinforcement convoy's road
        /// (Revival.MilitaryTownData.cs, research/mt_gameplay.py).</summary>
        internal static string[] RouteLines()
        {
            List<string> l = new List<string>();
            if (!On) return l.ToArray();
            string f = Faction();
            if (B(CfgAaSite))
                Lines(l, AaRoute, MilitaryTownData.AaSite,
                      "spawn,fraction=" + f + ",vehicle=gepard,count=1,hold");
            if (B(CfgPatrol))
                Lines(l, PatrolRoute, MilitaryTownData.Patrol,
                      "spawn,fraction=" + f + ",vehicle=" + PatrolVehicle() + ",count=1");
            if (B(CfgReinforce))
                Lines(l, ReinforceRoute, MilitaryTownData.Reinforce,
                      "spawn,fraction=" + f + ",count=1,kind=convoy");
            return l.ToArray();
        }

        static string PatrolVehicle()
        {
            string v = CfgPatrolVehicle == null ? "technical" : CfgPatrolVehicle.Value.Trim().ToLowerInvariant();
            return v == "btr" || v == "tank" ? v : "technical";
        }

        static void Lines(List<string> into, string name, float[] pts, string flags)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            for (int i = 0; i + 2 < pts.Length; i += 3)
                into.Add(name + "\t" + (i / 3).ToString(inv) + "\t" + pts[i].ToString("0.00", inv)
                    + "\t" + pts[i + 1].ToString("0.00", inv) + "\t" + pts[i + 2].ToString("0.00", inv)
                    + "\t0\t" + (i == 0 ? flags : ""));
        }

        /// <summary>A built-in route is driven only once the town is loaded:
        /// a vehicle put down before the tile's colliders exist falls.</summary>
        internal static bool RouteReady(string name)
        {
            return On && TownLoaded();
        }

        /// <summary>The reinforcement column, front to tail, by the number of
        /// players at the town.</summary>
        internal static string[] ConvoyKinds(string route)
        {
            if (!On || route != ReinforceRoute) return null;
            switch (Level())
            {
                case 1: return new string[] { "btr", "btr" };
                case 2: return new string[] { "tank", "btr", "btr" };
                case 3: return new string[] { "tank", "btr", "btr", "tank" };
                default: return new string[] { "tank", "btr", "btr", "btr", "tank" };
            }
        }

        // ===================================================== reinforcements

        static float _fight, _lastFight = -1000f, _nextSend, _nextFightTick;
        static bool _dueSaid;

        /// <summary>Master only. THE FIGHT CLOCK runs while a player is inside
        /// the town (fence + 120 u) or a spotter has had one in sight in the
        /// last 30 s; five quiet minutes end the fight. When it reaches
        /// ReinforceMinutes (shorter with more players) the convoy is sent -
        /// if the shared east event budget has room, else it waits.</summary>
        static void TickReinforce(float now)
        {
            if (!B(CfgReinforce) || now < _nextFightTick) return;
            float dt = _nextFightTick <= 0f ? 1f : Mathf.Min(5f, now - _nextFightTick + 1f);
            _nextFightTick = now + 1f;
            if (!RevivalTroopInsertion.MasterClient() || !TownLoaded()) return;
            bool fighting = PlayerInside(120f) || now - _lastPlayerSpotted < 30f;
            if (fighting) { _fight += dt; _lastFight = now; }
            else if (now - _lastFight > 300f) { _fight = 0f; _dueSaid = false; }
            int level = Level();
            float factor = level <= 1 ? 1f : level == 2 ? 0.8f : level == 3 ? 0.65f : 0.55f;
            float due = F(CfgReinforceMinutes, 8f, 2f, 60f) * 60f * factor;
            if (_fight < due || now < _nextSend) return;
            if (Airfield.Full())
            {
                if (!_dueSaid)
                {
                    _dueSaid = true;
                    RevivalPlugin.L.LogInfo("MilitaryTown: reinforcements due, but the east event "
                        + "budget is full (a landing or convoy at the airfield or the town) - waiting.");
                }
                _nextSend = now + 30f;
                return;
            }
            if (RevivalConvoy.SpawnOn(ReinforceRoute))
            {
                RevivalPlugin.L.LogInfo("MilitaryTown: reinforcement convoy sent after "
                    + (_fight / 60f).ToString("0.0") + " min of fighting, " + level + " player(s) at the town.");
                _fight = 0f;
                _dueSaid = false;
                _nextSend = now + F(CfgReinforceCooldown, 30f, 5f, 240f) * 60f;
            }
            else _nextSend = now + 60f;
        }

        // ============================================================== tick

        static float _nextWarn, _nextStatus;
        static int _spottersSaid = -1;

        internal static void Tick()
        {
            if (!On) return;
            float now = Time.time;
            try
            {
                FindMen(now);
                TickGuns(now);
                TickReinforce(now);
                if (now >= _nextStatus)
                {
                    _nextStatus = now + 5f;
                    int alive = SpottersAlive();
                    if (alive != _spottersSaid && TownLoaded())
                    {
                        _spottersSaid = alive;
                        RevivalPlugin.L.LogInfo("MilitaryTown: " + alive + " of 3 spotters alive"
                            + (alive == 0 ? " - the batteries are blind." : "."));
                    }
                }
                Warn(now);
                Arrive(now);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MilitaryTown: " + ex.Message);
            }
        }

        /// <summary>Every client: the local player hears it when a spotter has
        /// him and a gun still serves - the settlement battery's warning.</summary>
        static void Warn(float now)
        {
            if (now < _nextWarn) return;
            _nextWarn = now + 1f;
            GameObject me = MapTools.LocalPlayer();
            if (me == null || !B(CfgBatteries)) return;
            bool serves = false;
            for (int i = 0; i < Guns.Length && !serves; i++)
            {
                bool s;
                if (Guns[i].Raised && !Guns[i].Wrecked && ArtyBattery.TownCrew(Guns[i].Id, out s) >= 0 && s) serves = true;
            }
            if (!serves || OwnSide(me) || !Sees(me.transform.position, me)) return;
            _nextWarn = now + 25f;
            string line = Mortar.TextSpotted();
            if (!NativeMessage.Warn(line)) Turret.Hinweis(line, 3.5f);
            RevivalPlugin.L.LogInfo("MilitaryTown: a spotter has us at " + me.transform.position.ToString("0") + ".");
        }

        /// <summary>N5: the local player fights for the Traitors - the
        /// garrison and its guns leave him alone (their HatedFractions).</summary>
        static bool OwnSide(GameObject me)
        {
            return Fraktion.Spielerseite(me) == Fraktion.Eigene(Side);
        }

        static bool _inRing;
        static float _nextArrive;

        /// <summary>N5, every client: crossing into the map ring says whose
        /// place this is and whether the garrison will shoot - once per entry
        /// (out again past the ring plus 60 u re-arms it).</summary>
        static void Arrive(float now)
        {
            if (now < _nextArrive) return;
            _nextArrive = now + 1f;
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return;
            float d = Flat(me.transform.position - Centre);
            if (_inRing) { if (d > MapRingRadius + 60f) _inRing = false; return; }
            if (d > MapRingRadius) return;
            _inRing = true;
            bool own = OwnSide(me);
            string line = own
                ? Loc.T("\u0412\u043e\u0435\u043d\u043d\u044b\u0439 \u0433\u043e\u0440\u043e\u0434\u043e\u043a - \u043f\u043e\u0441\u0435\u043b\u0435\u043d\u0438\u0435 \u043f\u0440\u0435\u0434\u0430\u0442\u0435\u043b\u0435\u0439. \u0413\u0430\u0440\u043d\u0438\u0437\u043e\u043d \u043d\u0430 \u0432\u0430\u0448\u0435\u0439 \u0441\u0442\u043e\u0440\u043e\u043d\u0435.", "Military town - Traitor settlement. The garrison is on your side.")
                : Loc.T("\u0412\u043e\u0435\u043d\u043d\u044b\u0439 \u0433\u043e\u0440\u043e\u0434\u043e\u043a - \u043f\u043e\u0441\u0435\u043b\u0435\u043d\u0438\u0435 \u043f\u0440\u0435\u0434\u0430\u0442\u0435\u043b\u0435\u0439. \u0413\u0430\u0440\u043d\u0438\u0437\u043e\u043d \u0441\u0442\u0440\u0435\u043b\u044f\u0435\u0442 \u0431\u0435\u0437 \u043f\u0440\u0435\u0434\u0443\u043f\u0440\u0435\u0436\u0434\u0435\u043d\u0438\u044f.", "Military town - Traitor settlement. The garrison shoots on sight.");
            if (!NativeMessage.Warn(line)) Turret.Hinweis(line, 5f);
            RevivalPlugin.L.LogInfo("MilitaryTown: entered the Traitor settlement ring"
                + (own ? " as a Traitor - the garrison holds fire." : " - the garrison is hostile."));
        }
    }
}

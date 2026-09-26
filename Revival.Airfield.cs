// Next Day: Survival - Revival Toolkit
//
// AIRFIELD, PHASE 1: loot, defenders and the event budget of the abandoned
// airfield on the east tile (docs/ai/tasks/airfield-gameplay.md, section 6
// phase 1; acceptance docs/ai/tasks/airfield-gameplay-p1.md). Acts only with
// [World] EastTile (EastWorld.On), every part behind its own [Airfield] key.
// No new framework: each part hangs on a system that already runs.
//
// LOOT. The fixed spawn points below are keyed by the greybox building ids
// (H1, H2, C1, F1, B1, D1, D1c, D2a, D3, G1a, S1, S2, S4, W1, V1-V3) and a
// FRACTION of that building's marker box, never by a world position. The
// markers are the invisible "<id>|<name>" objects every east content scene
// carries (AirfieldGreybox.cs writes them, EastZones reads them), so a final
// model that keeps its id keeps its loot without a change here. The floor is
// the lowest collider under the point. What spawns is the game's own loot:
// ItemSpawnCategoriesDB.GetRandomItemByCategory for a category of the slot's
// pool (or a fixed item id), instantiated with PhotonNetwork.
// InstantiateSceneObject at the point + 0.8 exactly as ItemSpawnPoint.
// InstantiateSpawnedItem does - a networked pickup every client sees. Only the
// Photon master spawns. A slot whose pickup is gone waits its tier's reset;
// a new master adopts the pickups that are already there and starts every
// empty slot on a full reset, so a master change never refills the site.
//
// DEFENDERS. Four pockets N1-N4 (six editor-style groups, 18 men) are handed
// to RevivalGroundEnemies as built-in groups: its spawn, NavMesh checks,
// patrol/guard behaviour, adoption and respawn delay run them unchanged. The
// editor's own ground channel cannot carry them - its parser keeps groups on
// the home map (-2500..2500). A pocket does not respawn while a player is
// inside the airfield.
//
// EVENT BUDGET. A troop landing whose zone or arrow reaches the airfield and
// a convoy whose route reaches it are "airfield events". While EventBudget of
// them are active (a landing: helicopter inbound or its squad still in the
// field; a convoy: any vehicle still driving) no further one starts there:
// a scheduled landing waits ten minutes, a scheduled convoy takes a route
// elsewhere, the admin buttons say why.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class Airfield
    {
        // ============================================================= config
        internal static ConfigEntry<bool>   CfgEnabled;
        internal static ConfigEntry<bool>   CfgLoot;
        internal static ConfigEntry<float>  CfgLootReset;
        internal static ConfigEntry<float>  CfgSignatureReset;
        internal static ConfigEntry<bool>   CfgDefenders;
        internal static ConfigEntry<string> CfgFaction;
        internal static ConfigEntry<float>  CfgRespawn;
        internal static ConfigEntry<int>    CfgEventBudget;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Airfield", "Enabled", true,
                "East airfield gameplay, phase 1: building loot, the four defender "
                + "pockets and the event budget. Acts only with [World] EastTile = "
                + "true; on the home map alone it does nothing.");
            CfgLoot = cfg.Bind("Airfield", "Loot", true,
                "Fixed loot points in the airfield buildings (H1, H2, C1, F1, B1, "
                + "D1-D3, G1, the shelters, W1, V1-V3), by tier. Master client only.");
            CfgLootReset = cfg.Bind("Airfield", "LootResetMinutes", 45f,
                "Minutes before a looted tier 0-2 point (field, specialist) can "
                + "roll again (5..600).");
            CfgSignatureReset = cfg.Bind("Airfield", "SignatureResetMinutes", 180f,
                "Minutes before a looted tier 3 point (parts, fuel, military "
                + "stores) can roll again (5..1440).");
            CfgDefenders = cfg.Bind("Airfield", "Defenders", true,
                "The baseline defender pockets N1 gate, N2 repair compound, N3 "
                + "shelters and N4 ammunition enclosure (18 men) as ground groups.");
            CfgFaction = cfg.Bind("Airfield", "DefenderFaction", "looter",
                "Faction of the defenders: civilian, looter, traitor or neutral.");
            CfgRespawn = cfg.Bind("Airfield", "DefenderRespawnMinutes", 35f,
                "Minutes after a pocket is wiped before it comes back (5..240). It "
                + "never comes back while a player is inside the airfield.");
            CfgEventBudget = cfg.Bind("Airfield", "EventBudget", 1,
                "How many troop landings and convoys may be active at the airfield "
                + "at once (1..3). 1 = a landing or a convoy, never both.");
        }

        /// <summary>The whole site is on only in the east world.</summary>
        internal static bool On
        {
            get { return EastWorld.On && (CfgEnabled == null || CfgEnabled.Value); }
        }

        // ================================================================ area

        // The perimeter fence (airfield-greybox.md, P1): x 3990 / 4820,
        // z -1690 / 1690, in world units.
        const float MinX = 3990f, MaxX = 4820f, MinZ = -1690f, MaxZ = 1690f;

        /// <summary>Is the point inside the fence, widened by margin?</summary>
        internal static bool Inside(Vector3 p, float margin)
        {
            return p.x >= MinX - margin && p.x <= MaxX + margin
                && p.z >= MinZ - margin && p.z <= MaxZ + margin;
        }

        /// <summary>Any player inside the fence (plus margin)?</summary>
        internal static bool PlayerInside(float margin)
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
                Vector3 d = players[i].transform.position - p;
                d.y = 0f;
                if (d.sqrMagnitude < range * range) return true;
            }
            return false;
        }

        // ========================================================= event budget

        static int Budget()
        {
            return CfgEventBudget == null ? 1 : Mathf.Clamp(CfgEventBudget.Value, 1, 3);
        }

        /// <summary>Does a troop landing (zone or any arrow point) reach the
        /// airfield?</summary>
        internal static bool TroopTargets(Vector3 zone, List<Vector3> arrow)
        {
            if (Inside(zone, 150f)) return true;
            if (arrow != null)
                for (int i = 0; i < arrow.Count; i++)
                    if (Inside(arrow[i], 50f)) return true;
            return false;
        }

        /// <summary>Does a convoy route reach the airfield?</summary>
        internal static bool ConvoyTargets(List<Vector3> route)
        {
            if (route == null) return false;
            for (int i = 0; i < route.Count; i++)
                if (Inside(route[i], 100f)) return true;
            return false;
        }

        static int Active()
        {
            return RevivalTroopInsertion.AirfieldActive() + RevivalConvoy.AirfieldActive();
        }

        /// <summary>True when a NEW airfield event must not start now. The
        /// caller has already found that its event targets the airfield.</summary>
        internal static bool Full()
        {
            if (!On) return false;
            int active = Active();
            return active >= Budget();
        }

        internal static bool BlocksTroop(Vector3 zone, List<Vector3> arrow)
        {
            return On && TroopTargets(zone, arrow) && Full();
        }

        internal static bool BlocksConvoy(List<Vector3> route)
        {
            return On && ConvoyTargets(route) && Full();
        }

        // ========================================================== defenders

        sealed class Pocket
        {
            internal string Name, Behavior;
            internal int Count;
            internal float Radius;
            internal Vector3[] Route;
            internal Pocket(string name, string behavior, int count, float radius, params Vector3[] route)
            { Name = name; Behavior = behavior; Count = count; Radius = radius; Route = route; }
        }

        static Vector3 P(float x, float z) { return new Vector3(x, 0f, z); }

        // Every point stands outside the building shells, on the service road,
        // the apron or a hardstand, so it lies on the tile's NavMesh. N1 is
        // the front door, N2 the repair compound with its C1 observer, N3 the
        // shelter line (a part of it, not the 2 km zone), N4 holds D2 and does
        // not wander north. 5 + 5 + 4 + 4 = 18 men.
        static readonly Pocket[] Pockets = new Pocket[] {
            new Pocket("airfield-N1-gate", "guard", 2, 30f, P(4100f, 1655f)),
            new Pocket("airfield-N1-duty", "patrol", 3, 250f,
                P(4135f, 1615f), P(4140f, 1490f), P(4200f, 1400f), P(4280f, 1395f)),
            new Pocket("airfield-N2-repair", "patrol", 4, 320f,
                P(4320f, 1360f), P(4325f, 1240f), P(4385f, 1165f), P(4390f, 1030f), P(4290f, 1015f)),
            new Pocket("airfield-N2-observer", "waiting", 1, 25f, P(4300f, 1335f)),
            new Pocket("airfield-N3-shelters", "patrol", 4, 500f,
                P(4410f, 600f), P(4410f, 200f), P(4410f, -50f), P(4410f, -380f)),
            new Pocket("airfield-N4-ammo", "guard", 4, 60f, P(4130f, -1110f))
        };

        static string Faction()
        {
            string f = CfgFaction == null ? "looter" : CfgFaction.Value.Trim().ToLowerInvariant();
            return f == "civilian" || f == "traitor" || f == "neutral" ? f : "looter";
        }

        static float RespawnSeconds()
        {
            return Mathf.Clamp(CfgRespawn == null ? 35f : CfgRespawn.Value, 5f, 240f) * 60f;
        }

        /// <summary>Changes whenever the built-in groups would change, so
        /// RevivalGroundEnemies.Load knows to rebuild its list. 0 = none.</summary>
        internal static int GroupsVersion()
        {
            if (!On || CfgDefenders == null || !CfgDefenders.Value) return 0;
            return (Faction() + "|" + RespawnSeconds().ToString("0")).GetHashCode() | 1;
        }

        /// <summary>Appends the defender pockets to a freshly parsed ground
        /// list, in the form Parse itself produces.</summary>
        internal static void AddGroups(List<RevivalGroundEnemies.Group> into)
        {
            if (GroupsVersion() == 0) return;
            string faction = Faction();
            float respawn = RespawnSeconds();
            for (int i = 0; i < Pockets.Length; i++)
            {
                Pocket p = Pockets[i];
                RevivalGroundEnemies.Group g = new RevivalGroundEnemies.Group();
                g.Name = p.Name; g.Enabled = true; g.Builtin = true;
                g.X = p.Route[0].x; g.Z = p.Route[0].z;
                g.Faction = faction; g.Count = p.Count; g.Behavior = p.Behavior;
                g.Radius = p.Radius; g.Respawn = respawn;
                g.Loop = false; g.Hold = p.Behavior == "patrol" ? 20f : 0f;
                if (p.Behavior == "patrol")
                    for (int k = 0; k < p.Route.Length; k++) g.Route.Add(p.Route[k]);
                // The default kit, like the one row an empty editor roster
                // exports; loadouts are a later decision (concept section 7).
                RevivalComposition.CrewMan man = new RevivalComposition.CrewMan();
                man.Role = ""; man.Class = "regular"; man.Fpv = false;
                g.Loadout.Add(man);
                string row = p.Name + "\t" + faction + "\t" + p.Count + "\t" + p.Behavior
                    + "\t" + p.Radius.ToString("0") + "\t" + respawn.ToString("0");
                for (int k = 0; k < p.Route.Length; k++)
                    row += "\t" + p.Route[k].x.ToString("0") + "," + p.Route[k].z.ToString("0");
                g.Rows.Append(row).Append('\n');
                g.Meta = row;
                // The key travels in the spawn data and a new master adopts
                // the men by it, so it must be the same on every client.
                g.Key = p.Name + ":" + Fnv(row).ToString("x8");
                into.Add(g);
            }
        }

        static uint Fnv(string text)
        {
            uint h = 2166136261u;
            for (int i = 0; i < text.Length; i++) { h ^= text[i]; h *= 16777619u; }
            return h;
        }

        /// <summary>A built-in pocket waits with its respawn while a player is
        /// inside the airfield, and with its first spawn while one is close to
        /// its post, so nobody watches men appear.</summary>
        internal static bool HoldSpawn(RevivalGroundEnemies.Group g)
        {
            if (g == null || !g.Builtin) return false;
            if (g.Seen) return PlayerInside(60f);
            if (PlayerNear(new Vector3(g.X, 0f, g.Z), 150f)) return true;
            for (int i = 0; i < g.Route.Count; i++)
                if (PlayerNear(g.Route[i], 150f)) return true;
            return false;
        }

        // =============================================================== loot

        sealed class Slot
        {
            internal string Building, Pool;
            internal int Tier;
            internal float Fx, Fz, Chance;
            // Runtime, master only.
            internal GameObject Item;
            internal float NextAt;
            internal Vector3 At;
            internal bool Placed;
            internal Slot(string building, int tier, string pool, float fx, float fz, float chance)
            { Building = building; Tier = tier; Pool = pool; Fx = fx; Fz = fz; Chance = chance; }
        }

        // Tier 0 dressing, 1 field, 2 specialist, 3 signature (concept 2).
        // Chance 0 = the tier's default; 1 = the reset always brings it back
        // (the guaranteed category at H1, F1, D1 and D2).
        static readonly Slot[] Slots = new Slot[] {
            // H1 repair hangar: the parts cage and the lockers along the west
            // wall; the floor stays empty. No fuel here - that is D1.
            new Slot("H1", 3, "parts",   -0.38f,  0.36f, 1f),
            new Slot("H1", 2, "tools",   -0.38f, -0.36f, 0f),
            new Slot("H1", 2, "tools",   -0.10f,  0.40f, 0f),
            new Slot("H1", 2, "salvage", -0.10f, -0.40f, 0f),
            // H2 workshop ruin: salvage, a low chance of a signature part.
            new Slot("H2", 2, "salvage", -0.30f,  0.20f, 0f),
            new Slot("H2", 2, "salvage",  0.00f, -0.25f, 0f),
            new Slot("H2", 2, "salvage",  0.25f,  0.20f, 0f),
            new Slot("H2", 3, "parts",   -0.35f, -0.20f, 0.12f),
            // C1 tower: communications and observation in the lower block.
            new Slot("C1", 2, "comms",   -0.30f,  0.25f, 0f),
            new Slot("C1", 2, "comms",   -0.30f, -0.25f, 0f),
            new Slot("C1", 1, "field",    0.00f,  0.30f, 0f),
            // F1 fire station: dependable medicine, no military tier 3.
            new Slot("F1", 2, "medical", -0.35f,  0.30f, 1f),
            new Slot("F1", 2, "medical", -0.35f, -0.30f, 0f),
            new Slot("F1", 2, "medical",  0.10f,  0.35f, 0f),
            new Slot("F1", 1, "field",    0.10f, -0.35f, 0f),
            // B1 duty barracks: basic resupply after the gate.
            new Slot("B1", 1, "guard",    0.00f,  0.35f, 0f),
            new Slot("B1", 1, "guard",    0.00f,  0.10f, 0f),
            new Slot("B1", 1, "field",    0.00f, -0.15f, 0f),
            new Slot("B1", 2, "military", 0.00f, -0.38f, 0.2f),
            // G1 gatehouse: modest guard supplies.
            new Slot("G1a", 1, "guard",   0.00f,  0.00f, 0f),
            // D3 technical stores: components, one low-volume signature slot.
            new Slot("D3", 2, "tools",   -0.35f,  0.20f, 0f),
            new Slot("D3", 2, "tools",    0.00f,  0.25f, 0f),
            new Slot("D3", 2, "tools",    0.30f,  0.20f, 0f),
            new Slot("D3", 3, "parts",   -0.10f, -0.20f, 0.3f),
            // D1 fuel compound: the pump house always has fuel, the bund may.
            new Slot("D1c", 3, "fuel",    0.00f,  0.00f, 1f),
            new Slot("D1", 3, "fuel",    -0.23f, -0.25f, 0.4f),
            new Slot("D1", 2, "fuel",     0.25f, -0.30f, 0f),
            // D2a open ammunition store. D2b stays sealed (key/event, later).
            new Slot("D2a", 3, "military", -0.10f,  0.15f, 1f),
            new Slot("D2a", 3, "military", -0.10f, -0.15f, 0f),
            new Slot("D2a", 2, "military",  0.10f,  0.00f, 0f),
            // Shelters: scattered military salvage; S2's rear breach hides
            // the smuggler cache. S3 (door shut) has no loot on purpose.
            new Slot("S1", 1, "guard",    0.05f,  0.10f, 0f),
            new Slot("S1", 2, "salvage",  0.05f, -0.10f, 0f),
            new Slot("S2", 2, "salvage",  0.05f,  0.10f, 0f),
            new Slot("S2", 2, "military",-0.18f,  0.00f, 0.5f),
            new Slot("S4", 1, "guard",    0.05f,  0.00f, 0f),
            new Slot("S4", 2, "salvage",  0.05f, -0.12f, 0f),
            // W1 scrap yard: aircraft and vehicle salvage by the Mi-8 hulk.
            new Slot("W1", 2, "salvage", -0.20f,  0.15f, 0f),
            new Slot("W1", 2, "salvage",  0.20f, -0.10f, 0f),
            new Slot("W1", 3, "parts",   -0.15f, -0.10f, 0.15f),
            // V1-V3 revetments: sparse field caches.
            new Slot("V1", 0, "dressing", -0.20f, 0.00f, 0f),
            new Slot("V2", 0, "dressing", -0.20f, 0.00f, 0f),
            new Slot("V3", 0, "dressing", -0.20f, 0.00f, 0f)
        };

        // What each pool rolls: ItemSpawnCategory names of the game's own
        // loot tables, or a fixed item id. The An-2 parts and aviation fuel
        // of phase 3 are added to "parts" and "fuel" as ids - the slots stay.
        static readonly Dictionary<string, string[]> Pools = MakePools();

        static Dictionary<string, string[]> MakePools()
        {
            Dictionary<string, string[]> p = new Dictionary<string, string[]>();
            p["dressing"] = new string[] { "HomeItems", "EatFoodCans", "EatWater", "CraftComponents" };
            p["field"] = new string[] { "EatFoodCans", "EatWater", "EatFoodOpened", "CivilianMeds",
                "CivilianClotheJackets", "CivilianAmmunation" };
            p["guard"] = new string[] { "MilitaryClotheHats", "MilitaryClotheJackets",
                "MilitaryClothePants", "MilitaryClotheGloves", "CivilianAmmunation", "MilitaryMeds",
                "EatFoodCans" };
            p["salvage"] = new string[] { "CraftComponents", "VehicleItems", "HomeItems" };
            p["tools"] = new string[] { "VehicleItems", "CraftComponents", "MilitaryWeaponUsableItem" };
            p["comms"] = new string[] { "MilitaryWeaponUsableItem", "CraftComponents", "CivilianMeds",
                "MilitaryBackpacks" };
            p["medical"] = new string[] { "MilitaryMeds", "SpecialMeds", "CivilianMeds",
                "MilitaryClotheMasks" };
            p["fuel"] = new string[] { "VehicleItems" };
            p["military"] = new string[] { "MilitaryAmmunation", "SpecialAmmunation",
                "MilitaryWeaponFirearm", "SpecialWeaponFirearm", "MilitaryWeaponUsableItem",
                "SpecialClotheJackets" };
            p["parts"] = new string[] { "VehicleItems", "CraftComponents", "RareItem" };
            return p;
        }

        static float TierChance(int tier)
        {
            switch (tier)
            {
                case 0: return 0.3f;
                case 1: return 0.7f;
                case 2: return 0.55f;
                default: return 0.35f;
            }
        }

        static float ResetSeconds(int tier)
        {
            if (tier >= 3)
                return Mathf.Clamp(CfgSignatureReset == null ? 180f : CfgSignatureReset.Value, 5f, 1440f) * 60f;
            return Mathf.Clamp(CfgLootReset == null ? 45f : CfgLootReset.Value, 5f, 600f) * 60f;
        }

        static float _next, _masterSince = -1f;
        static object _room;
        static bool _started, _fresh;
        static MethodInfo _roomGetter, _masterGetter;

        internal static void Tick()
        {
            if (!On || CfgLoot == null || !CfgLoot.Value) return;
            float now = Time.time;
            if (now < _next) return;
            _next = now + 2f;
            try
            {
                if (_roomGetter == null || _masterGetter == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) return;
                    _roomGetter = AccessTools.PropertyGetter(photon, "room");
                    _masterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                    if (_roomGetter == null || _masterGetter == null) return;
                }
                object room = _roomGetter.Invoke(null, null);
                if (!System.Object.ReferenceEquals(room, _room))
                {
                    // A new room: nothing we spawned is ours any more. Whoever
                    // is master within its first seconds opened the round.
                    _room = room; _started = false; _masterSince = -1f;
                    _fresh = room != null && (bool)_masterGetter.Invoke(null, null);
                    for (int i = 0; i < Slots.Length; i++)
                    { Slots[i].Item = null; Slots[i].Placed = false; Slots[i].NextAt = 0f; }
                }
                bool master = room != null && (bool)_masterGetter.Invoke(null, null);
                if (!master) { _started = false; _masterSince = -1f; _fresh = false; return; }
                if (_masterSince < 0f) _masterSince = now;
                // Ownership and the cached scene objects arrive first.
                if (now - _masterSince < 8f || MapTools.LocalPlayer() == null) return;
                if (!Place()) return;
                if (!_started) { Start(now); _started = true; }
                Run(now);
            }
            catch (Exception ex)
            {
                _next = now + 30f;
                RevivalPlugin.L.LogWarning("Airfield loot: " + ex.Message);
            }
        }

        /// <summary>Resolve every slot on its marker. False while the airfield
        /// content scene is not loaded, and for its first seconds, while the
        /// carving obstacles are still cutting the NavMesh.</summary>
        static bool Place()
        {
            Vector3 c0, h0;
            Quaternion r0;
            if (!EastZones.Find("H1", out c0, out h0, out r0)) { _markersAt = -1f; return false; }
            if (_markersAt < 0f) _markersAt = Time.time;
            if (Time.time - _markersAt < 15f) return false;
            int placed = 0, missing = 0, beside = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                Slot s = Slots[i];
                if (s.Placed) { placed++; continue; }
                Vector3 centre, half;
                Quaternion rot;
                if (!EastZones.Find(s.Building, out centre, out half, out rot)) { missing++; continue; }
                Vector3 local = new Vector3(s.Fx * half.x * 2f, 0f, s.Fz * half.z * 2f);
                Vector3 top = centre + rot * local + Vector3.up * (half.y + 2f);
                Vector3 floor;
                bool moved;
                if (!Floor(top, half.y * 2f + 12f, out floor) || !Walkable(ref floor, out moved))
                { missing++; continue; }
                if (moved) beside++;
                s.At = floor;
                s.Placed = true;
                placed++;
            }
            if (placed == 0) return false;
            if (!_placedSaid && missing == 0)
            {
                _placedSaid = true;
                RevivalPlugin.L.LogInfo("Airfield loot: " + placed + " points placed, " + beside
                    + " of them beside a solid (greybox) shell.");
            }
            if (missing > 0 && !_missingSaid)
            {
                _missingSaid = true;
                RevivalPlugin.L.LogWarning("Airfield loot: " + missing + " of " + Slots.Length
                    + " points have no marker, floor or walkable ground near them.");
            }
            return true;
        }

        /// <summary>A pickup has to lie where a player can walk. Inside a hall,
        /// a bunker or a shelter the floor is on the NavMesh. A greybox block
        /// such as F1, B1, D3 or C1 is solid and carved out of it; there the
        /// point moves to the nearest walkable spot beside the shell. The data
        /// stays the same, so a final model with an interior gets it inside.</summary>
        static bool Walkable(ref Vector3 floor, out bool moved)
        {
            moved = false;
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(floor, out hit, 3f, UnityEngine.AI.NavMesh.AllAreas))
            {
                Vector3 d = hit.position - floor;
                d.y = 0f;
                if (d.sqrMagnitude < 2.25f) return true;
            }
            if (!UnityEngine.AI.NavMesh.SamplePosition(floor, out hit, 40f, UnityEngine.AI.NavMesh.AllAreas))
                return false;
            Vector3 ground;
            floor = Floor(hit.position + Vector3.up * 3f, 8f, out ground) ? ground : hit.position;
            moved = true;
            return true;
        }

        static float _markersAt = -1f;
        static bool _placedSaid;
        static bool _missingSaid;

        /// <summary>The LOWEST solid surface under the point: the floor of a
        /// hall, bunker or shelter rather than its roof or earth cover.</summary>
        static bool Floor(Vector3 top, float depth, out Vector3 floor)
        {
            floor = top;
            RaycastHit[] hits = Physics.RaycastAll(top, Vector3.down, depth, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            float best = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].point.y < best) { best = hits[i].point.y; floor = hits[i].point; }
            return best < float.MaxValue;
        }

        /// <summary>First run as master: adopt what lies there already. A
        /// fresh round rolls every slot soon; a master change waits a reset.</summary>
        static void Start(float now)
        {
            int adopted = 0;
            Type spawned = RevivalPlugin.TypeByName("ItemSpawned");
            UnityEngine.Object[] items = spawned == null
                ? new UnityEngine.Object[0] : UnityEngine.Object.FindObjectsOfType(spawned);
            for (int i = 0; i < Slots.Length; i++)
            {
                Slot s = Slots[i];
                s.Item = null;
                for (int k = 0; s.Placed && k < items.Length; k++)
                {
                    Component c = items[k] as Component;
                    if (c == null) continue;
                    Vector3 d = c.transform.position - s.At;
                    if (Mathf.Abs(d.y) > 8f) continue;
                    d.y = 0f;
                    if (d.sqrMagnitude > 9f) continue;
                    s.Item = c.gameObject;
                    adopted++;
                    break;
                }
                s.NextAt = s.Item != null ? -1f
                    : _fresh ? now + UnityEngine.Random.Range(5f, 60f)
                    : now + ResetSeconds(s.Tier);
            }
            RevivalPlugin.L.LogInfo("Airfield loot: " + Slots.Length + " points, " + adopted
                + " pickups adopted, " + (_fresh ? "fresh round - rolling now." : "new master - empty points wait a reset."));
        }

        static void Run(float now)
        {
            int spawnedThisTick = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                Slot s = Slots[i];
                if (!s.Placed) continue;
                if (s.NextAt < 0f)
                {
                    if (s.Item != null) continue;
                    // Taken (or cleaned away): the tier's reset starts now.
                    s.NextAt = now + ResetSeconds(s.Tier);
                    continue;
                }
                if (now < s.NextAt || spawnedThisTick >= 3) continue;
                // Never refill a point in front of someone.
                if (PlayerNear(s.At, 45f)) { s.NextAt = now + 60f; continue; }
                float chance = s.Chance > 0f ? s.Chance : TierChance(s.Tier);
                if (UnityEngine.Random.value > chance) { s.NextAt = now + ResetSeconds(s.Tier); continue; }
                GameObject go = Spawn(s);
                if (go == null) { s.NextAt = now + 300f; continue; }
                s.Item = go;
                s.NextAt = -1f;
                spawnedThisTick++;
            }
        }

        // ------------------------------------------------ the game's own loot

        static MethodInfo _random, _byId, _instantiate;
        static FieldInfo _path, _euler;
        static Type _catType, _catEnum;
        static object _itemData;
        static bool _lootLooked;

        static bool LookUpLoot()
        {
            if (_lootLooked) return _random != null && _instantiate != null;
            _lootLooked = true;
            Type db = RevivalPlugin.TypeByName("ItemSpawnCategoriesDB");
            _random = db == null ? null : AccessTools.Method(db, "GetRandomItemByCategory", null, null);
            if (_random != null) _catType = _random.GetParameters()[0].ParameterType;
            _catEnum = RevivalPlugin.TypeByName("ItemSpawnCategory");
            if (_catEnum == null && _catType != null && _catType.IsEnum) _catEnum = _catType;
            Type idm = RevivalPlugin.TypeByName("ItemDataManager");
            if (idm != null)
            {
                _byId = AccessTools.Method(idm, "GetItemByID", new Type[] { typeof(int) }, null);
                FieldInfo inst = AccessTools.Field(idm, "Instance");
                if (inst != null) _itemData = inst.GetValue(null);
            }
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon != null)
                foreach (MethodInfo m in photon.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "InstantiateSceneObject") continue;
                    ParameterInfo[] a = m.GetParameters();
                    if (a.Length == 5 && a[0].ParameterType == typeof(string)
                        && a[1].ParameterType == typeof(Vector3) && a[2].ParameterType == typeof(Quaternion)
                        && a[3].ParameterType == typeof(byte)) { _instantiate = m; break; }
                }
            if (_random == null || _catType == null || _catEnum == null || !_catEnum.IsEnum
                || _instantiate == null)
            {
                RevivalPlugin.L.LogWarning("Airfield loot: the game's loot table or "
                    + "InstantiateSceneObject was not found - no airfield loot.");
                _random = null;
            }
            return _random != null && _instantiate != null;
        }

        /// <summary>One roll from the slot's pool: a category of the game's
        /// loot tables, or a fixed id. Null when nothing could be drawn.</summary>
        static object Draw(string pool)
        {
            string[] entries;
            if (!Pools.TryGetValue(pool, out entries) || entries.Length == 0) return null;
            entries = An2Repair.Pool(pool, entries);   // the An-2 repair parts, when on
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string e = entries[UnityEngine.Random.Range(0, entries.Length)];
                object item = null;
                int id;
                if (Int32.TryParse(e, out id))
                {
                    if (_byId != null && _itemData != null) item = _byId.Invoke(_itemData, new object[] { id });
                }
                else
                {
                    object db = Registry.GetDb();
                    object cat = Category(e);
                    if (db != null && cat != null) item = _random.Invoke(db, new object[] { cat });
                }
                if (item != null && PathOf(item).Length > 0) return item;
            }
            return null;
        }

        /// <summary>The category argument by its enum NAME, so the table never
        /// depends on the enum's numbering; converted when the method takes
        /// the plain number.</summary>
        static object Category(string name)
        {
            object value;
            try { value = Enum.Parse(_catEnum, name); }
            catch (ArgumentException) { return null; }
            if (_catType == _catEnum) return value;
            try { return Convert.ChangeType(Convert.ToInt32(value), _catType); }
            catch (Exception) { return null; }
        }

        static string PathOf(object item)
        {
            if (_path == null || _path.DeclaringType != item.GetType())
            {
                _path = AccessTools.Field(item.GetType(), "Path");
                _euler = AccessTools.Field(item.GetType(), "EulerAngles");
            }
            string p = _path == null ? null : _path.GetValue(item) as string;
            return p ?? "";
        }

        static GameObject Spawn(Slot s)
        {
            if (!LookUpLoot()) return null;
            object item = Draw(s.Pool);
            if (item == null)
            {
                RevivalPlugin.L.LogWarning("Airfield loot: pool " + s.Pool + " drew nothing for " + s.Building + ".");
                return null;
            }
            string path = PathOf(item);
            Quaternion rot = _euler == null ? Quaternion.identity
                : Quaternion.Euler((Vector3)_euler.GetValue(item));
            // ItemSpawnPoint.InstantiateSpawnedItem: the point + 0.8 up. No
            // point id travels with it - there is no ItemSpawnPoint to report
            // back to; the slot watches the object itself.
            GameObject go = _instantiate.Invoke(null, new object[] {
                path, s.At + new Vector3(0f, 0.8f, 0f), rot, (byte)0, null }) as GameObject;
            if (go != null)
                RevivalPlugin.L.LogInfo("Airfield loot: " + s.Building + " tier " + s.Tier + " "
                    + s.Pool + " -> " + path + " at (" + s.At.x.ToString("0") + ", " + s.At.z.ToString("0") + ").");
            return go;
        }

        /// <summary>The building ids the loot table uses (verify.py compares
        /// them with the greybox recipe).</summary>
        internal static List<string> LootBuildings()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < Slots.Length; i++)
                if (!ids.Contains(Slots[i].Building)) ids.Add(Slots[i].Building);
            return ids;
        }
    }
}

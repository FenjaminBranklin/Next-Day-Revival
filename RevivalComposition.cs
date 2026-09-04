// Next Day: Survival - Revival Toolkit
//
// Patrol/convoy COMPOSITION - the runtime side of the map/road/composition
// editor (docs/ai/tasks/map-road-composition-editor.md, workstream A).
//
// WHAT IT IS. The editor (routeeditor.py) is the authoring surface. It saves an
// authoritative, versioned definition to assets/ndr_compositions.json AND writes
// two flat runtime views next to it:
//   - assets/ndr_routes.tsv        the EXISTING route file, unchanged format, so
//                                  Patrol/RevivalConvoy drive the routes with no
//                                  code change (one-way convoy included).
//   - assets/ndr_composition.tsv   this file's input: one line per crew member,
//                                  giving each route its vehicles (order + type)
//                                  and each vehicle its crew (role, weapons,
//                                  uniform).
//
// WHY A TSV AND NOT THE JSON. The plugin references no JSON library and compiles
// as C# 3.0 (.NET 3.5). The Route loader already reads a tab file with the same
// simple String.Split, so the runtime reads a tab file here too - the JSON stays
// the human-authoritative source the editor and compcheck.py own. This mirrors
// the deliberate design of Patrol.Route (its class comment): a flat file the
// runtime already knows how to read beats teaching it a new parser.
//
// WHAT IT FEEDS. RevivalConvoy.Composition() asks VehicleKinds(route) for the
// tank/APC order of a convoy route; when the editor defined one, that order wins
// over the Convoy/Tanks + Convoy/Apcs config. Patrol uses the same order for a
// mini-convoy. CrewOf supplies the dismount path with each role's main weapon
// and uniform. FPV is an independent locked capability, not a fabricated item.
//
// C# 3.0: no optional arguments, no expression-tree lambdas. ASCII-only comments
// and logs; this file has no player-facing strings, so it needs no /codepage.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;

namespace NextDayRevival
{
    internal static class RevivalComposition
    {
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgFile;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Composition", "Enabled", true,
                "Use convoy and patrol compositions from the map editor. When "
                + "CompositionFile exists, its vehicle order (tank/BTR) replaces "
                + "Convoy/Tanks and Convoy/Apcs for matching routes. A missing "
                + "file leaves existing behavior unchanged.");
            CfgFile = cfg.Bind("Composition", "CompositionFile", "ndr_composition.tsv",
                "Composition file written by the editor beside the DLL in "
                + "BepInEx\\plugins\\assets. A missing file leaves existing "
                + "behavior unchanged.");
        }

        // ---------------------------------------------------------- data model

        /// <summary>One crew member of one vehicle: a role, the item ids of its
        /// main weapon and mandatory FPV capability, plus the uniform item ids
        /// per equipment slot (0 = slot left as the game default).</summary>
        internal sealed class CrewMan
        {
            public string Role = "";
            public int[] Weapons = new int[0];
            public int Headwear, Mask, Body, Legs, Hands;
            public bool Fpv = true;

            public int MainWeapon
            {
                get
                {
                    for (int i = 0; i < Weapons.Length; i++)
                        if (Weapons[i] > 0 && Weapons[i] != 1150)
                            return Weapons[i];
                    return 0;
                }
            }
        }

        /// <summary>One vehicle in a route's column, front to tail.</summary>
        internal sealed class Vehicle
        {
            public string Type = "btr";          // "tank" or "btr"
            public List<CrewMan> Crew = new List<CrewMan>();
            public bool IsTank { get { return Type == "tank"; } }
        }

        /// <summary>A route's whole composition, vehicles in column order.</summary>
        internal sealed class Composition
        {
            public string Route = "";
            public List<Vehicle> Vehicles = new List<Vehicle>();
        }

        static readonly Dictionary<string, Composition> _byRoute =
            new Dictionary<string, Composition>();
        static bool _loaded;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        // ---------------------------------------------------------------- load

        internal static void Load(bool force)
        {
            if (_loaded && !force) return;
            _loaded = true;
            _byRoute.Clear();
            if (!Enabled) return;

            string file = CfgFile == null ? "ndr_composition.tsv" : CfgFile.Value;
            string path = Path.Combine(RevivalPlugin.AssetDir, file);
            if (!File.Exists(path))
            {
                RevivalPlugin.L.LogInfo("Composition: " + path
                    + " not present - convoy uses the config counts.");
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                // Vehicles are keyed by (route, index) so crew lines in any order
                // still assemble the right column; the final list is sorted by
                // index so vehicle 0 is the front.
                Dictionary<string, SortedDictionary<int, Vehicle>> stage =
                    new Dictionary<string, SortedDictionary<int, Vehicle>>();
                int bad = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    if (raw == null) continue;
                    string t = raw.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;

                    // v3: route index type role weapon headwear mask body legs hands fpv
                    string[] c = raw.Split('\t');
                    if (c.Length < 3) { bad++; continue; }
                    string route = c[0].Trim();
                    int vindex;
                    if (route.Length == 0
                        || !int.TryParse(c[1].Trim(), NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out vindex))
                    { bad++; continue; }
                    string vtype = c[2].Trim().ToLowerInvariant();
                    if (vtype != "tank" && vtype != "btr") vtype = "btr";

                    SortedDictionary<int, Vehicle> col;
                    if (!stage.TryGetValue(route, out col))
                    {
                        col = new SortedDictionary<int, Vehicle>();
                        stage[route] = col;
                    }
                    Vehicle v;
                    if (!col.TryGetValue(vindex, out v))
                    {
                        v = new Vehicle();
                        v.Type = vtype;
                        col[vindex] = v;
                    }
                    else if (vtype == "tank")
                    {
                        v.Type = "tank";   // any tank line makes the vehicle a tank
                    }

                    // A crew line? (a role in column 4). A vehicle with no crew
                    // line still defines the column slot and its type.
                    if (c.Length >= 4 && c[3].Trim().Length > 0)
                    {
                        CrewMan man = new CrewMan();
                        man.Role = c[3].Trim();
                        man.Weapons = ParseIds(c.Length > 4 ? c[4] : "");
                        if (c.Length >= 11)
                        {
                            man.Headwear = ParseId(c[5]);
                            man.Mask = ParseId(c[6]);
                            man.Body = ParseId(c[7]);
                            man.Legs = ParseId(c[8]);
                            man.Hands = ParseId(c[9]);
                        }
                        else
                        {
                            // Version 2: one coarse headwear column, no mask.
                            man.Headwear = ParseId(c.Length > 5 ? c[5] : "");
                            man.Body = ParseId(c.Length > 6 ? c[6] : "");
                            man.Legs = ParseId(c.Length > 7 ? c[7] : "");
                            man.Hands = ParseId(c.Length > 8 ? c[8] : "");
                        }
                        // Version 1 encoded the capability as fabricated item
                        // 1150 in the weapons column. Version 2+ carries an
                        // explicit locked flag after the uniform slots.
                        man.Fpv = c.Length < 10 || c[c.Length >= 11 ? 10 : 9].Trim() == "1";
                        v.Crew.Add(man);
                    }
                }

                foreach (KeyValuePair<string, SortedDictionary<int, Vehicle>> kv in stage)
                {
                    Composition comp = new Composition();
                    comp.Route = kv.Key;
                    foreach (KeyValuePair<int, Vehicle> vk in kv.Value)
                        comp.Vehicles.Add(vk.Value);
                    _byRoute[kv.Key] = comp;
                    RevivalPlugin.L.LogInfo("Composition: route " + kv.Key + ", "
                        + comp.Vehicles.Count + " vehicle(s).");
                }
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Composition: " + bad
                        + " unreadable line(s) in " + file + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Composition: reading " + file + ": " + ex);
            }
        }

        static int ParseId(string s)
        {
            int n;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out n) && n > 0)
                return n;
            return 0;
        }

        static int[] ParseIds(string s)
        {
            if (s == null) return new int[0];
            string[] parts = s.Split('|');
            List<int> ids = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                int n = ParseId(parts[i]);
                if (n > 0) ids.Add(n);
            }
            return ids.ToArray();
        }

        // -------------------------------------------------------------- public

        /// <summary>The tank/APC order of a convoy route as the editor defined it
        /// (true = tank, front to tail), or null when the route has no editor
        /// composition. RevivalConvoy.Composition() uses this in place of the
        /// Convoy/Tanks + Convoy/Apcs config when it is present.</summary>
        internal static bool[] VehicleKinds(string route)
        {
            if (!Enabled || route == null) return null;
            Load(false);
            Composition comp;
            if (!_byRoute.TryGetValue(route, out comp) || comp.Vehicles.Count == 0)
                return null;
            bool[] kinds = new bool[comp.Vehicles.Count];
            for (int i = 0; i < comp.Vehicles.Count; i++)
                kinds[i] = comp.Vehicles[i].IsTank;
            return kinds;
        }

        /// <summary>The full composition of a route, or null. Exposed for the
        /// crew-equip hook (role/weapon/uniform).</summary>
        internal static Composition Of(string route)
        {
            if (!Enabled || route == null) return null;
            Load(false);
            Composition comp;
            return _byRoute.TryGetValue(route, out comp) ? comp : null;
        }

        /// <summary>The crew of one vehicle in a route's column, or null.</summary>
        internal static List<CrewMan> CrewOf(string route, int vehicleIndex)
        {
            Composition comp = Of(route);
            if (comp == null || vehicleIndex < 0
                || vehicleIndex >= comp.Vehicles.Count) return null;
            return comp.Vehicles[vehicleIndex].Crew;
        }
    }
}

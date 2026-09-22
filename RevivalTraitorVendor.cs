// Next Day: Survival - Revival Toolkit
//
// THE TRADER OF THE TRAITOR SETTLEMENT.
//
// In the middle of Litvinovka - the village the toolkit's own traitor camp
// stands in (`RevivalNewSettlement.cs`) - there is a closed blue block that
// looks like a kiosk. Field request: cut an opening into the side that faces
// the street, stand a vendor in it, and let him trade what the vendor in the
// civilian settlement trades. This file is that whole feature: the block is
// found, opened, and a real storekeeper is built into it.
//
// THREE THINGS ARE DONE HERE, AND EACH ONE IS DONE THE WAY THE GAME ALREADY
// DOES IT.
//
//   1 THE MAN. Not a hand-built NPC. `Crew.DropCustomSquad` is
//     `Crew.DropGroundSquad` plus one callback, so the vendor is a squad of ONE
//     built by the game's own `NPC_Settlement.StartMainInit` -> `InitSpawnNpc`
//     chain, and the callback is the only place where his spawn point differs
//     from a crewman's: `NPCType` is the storekeeper prefab
//     (`NpcType.Kladovshik` -> `NPCSpawn\Kladovshik_NPC`, the path
//     `research/resource_paths.tsv` lists as `npcspawn/kladovshik_npc`) and
//     `BehaviorPattern` is `StoreKeeper`. Everything the trade itself needs
//     sits on that same spawn point: `StorageId`, `MarketItemRanksSelling` and
//     `BuyPlayerItemsPercent` (`docs/ai/tasks/testregion-arena.md`, the field
//     list of `NPC_SpawnPoint`; RE 10 for the init chain that reads them).
//
//   2 WHAT HE SELLS IS COPIED, NEVER INVENTED. The three trade values above are
//     read off the CIVILIAN settlement's own storekeeper spawn point at runtime
//     and written onto ours. That is what "the same as the vendor in the civ
//     settlement" means, and it is the same trick the artillery crew uses to
//     take a settlement's real faction off a living man (RE 39). The rank list
//     is CLONED, not shared: `RecalculateMarketItemsCategory` walks the list
//     the storekeeper was given, and two traders holding the same List object
//     is a bug waiting for the first time the game sorts it in place. Without a
//     trader on the map the fall-back is ranks A-D and 50 percent, which is a
//     working trader, and the log says it was a fall-back.
//     The toolkit's own items ride along by themselves: `Registry`
//     (`Revival.Items.cs`) writes them into every A-D list and into
//     `MarketItemsPriceDictionary`, so anything the civilian trader offers this
//     one offers too (RE 30, RE 30.1).
//
//   3 THE OPENING. The block is a piece of the map, and a piece of the map is
//     not ours to damage: everything here is reversible, and the mesh asset in
//     `resources.assets` is never touched - a cut ALWAYS produces a new Mesh
//     object and leaves the original in place for `Restore`.
//
// WHY THE OPENING HAS TWO WAYS OF BEING MADE. `Revival.Helipads.cs` records the
// measured reason (`WorldBounds`, and `Revival.MapUnblock.cs` before it): the
// props of these maps are STATICALLY BATCHED, so a prop's `MeshFilter` can hold
// the combined mesh of a whole map chunk, and its physics geometry is the only
// thing that still describes the single object. A mesh like that cannot be cut:
// reading it is usually refused outright, and writing a cut copy back would put
// a hole through a chunk instead of a wall. So:
//
//   cut     The wall mesh is genuinely cut - every triangle of the road-facing
//           wall is clipped against the window rectangle (a Sutherland-Hodgman
//           difference, four half-planes), the hole gets a lined recess behind
//           it so one does not look straight through the kiosk, and the
//           colliders that cross the opening are split into the pieces around
//           it. Used when the mesh can be read AND belongs to this prop alone.
//   hatch   The wall is left exactly as it is and the opening is built ONTO it:
//           a serving hatch that projects from the wall, counter, cheeks, roof
//           and a back panel flush against the block, with the trader standing
//           in it. No mesh is read, no collider is touched, and the trader is
//           in FRONT of the wall plane, so nothing can block the line to him.
//
// `Mode` picks one or leaves the choice to the code (`auto`, the default:
// cut when the mesh allows it, otherwise hatch). `rebuild` is the third way out
// - hide the block and draw it again as a box with a window - and it is only
// ever used when the config says so.
//
// WHICH SIDE FACES THE STREET. Not guessed at a runtime raycast: the dirt road
// through Litvinovka is a straight run of about 110 m and its bearing is known
// from committed data (`assets/editor/roadnet_sample.json` edge 41, nodes 23
// (-1434.33, -1458.74) and 18 (-1614.99, -1636.96) -> 225.4 degrees, and the
// camp centre itself sits ON that road). The window looks at the closest point
// of that line, which is the one direction a village kiosk can face. `Facing`
// overrides it with a single number when the block turns out to stand the other
// way round.
//
// WHERE THE BLOCK IS is a question no file in this repository can answer: the
// level data lives in the game folder, not here. So it is SEARCHED for, once a
// player is near enough for the village to be loaded, and every candidate is
// written to the log with its path, its size and its distance - so a wrong pick
// is fixed by putting one name into `Block` rather than by another release.
//
// MASTER AND CLIENT. The opening is local geometry and is built on EVERY
// machine from the block's own transform, so everyone sees the same hole. The
// man is a networked scene object and therefore master-only, exactly like the
// camp around him. A master who has never been to Litvinovka cannot find the
// block and therefore has no place to put the trader; `StandX`/`StandZ`/
// `StandYaw` pin the spot once it is known, and the log prints the three
// numbers ready to be copied.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas, no LINQ. ASCII outside the Cyrillic of player-facing `Loc.T` text.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs           BindConfig / Tick / LateFrame.
//   Revival.Crew.cs            DropCustomSquad - DropGroundSquad plus the
//                              per-spawn-point callback this file needs.
//   RevivalNewSettlement.cs    Wanted / Centre / Here / Unhate, now internal:
//                              the camp owns the switch, the place, the faction
//                              and the map gate. The shop opens and closes with
//                              the camp - `Tick` reads `Wanted` first.

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
    /// <summary>
    /// The vendor in the blue block at Litvinovka: finding the block, opening
    /// it, and the storekeeper standing behind the counter.
    /// </summary>
    public static class TraitorVendor
    {
        // =============================================================== config

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgMode;
        static ConfigEntry<string> _cfgBlock;
        static ConfigEntry<float> _cfgSearch;
        static ConfigEntry<float> _cfgFacing;
        static ConfigEntry<float> _cfgRoadBearing;
        static ConfigEntry<float> _cfgWidth;
        static ConfigEntry<float> _cfgHeight;
        static ConfigEntry<float> _cfgSill;
        static ConfigEntry<float> _cfgShift;
        static ConfigEntry<float> _cfgRecess;
        static ConfigEntry<float> _cfgStandBack;
        static ConfigEntry<string> _cfgTraderFrom;
        static ConfigEntry<bool> _cfgOutside;
        static ConfigEntry<float> _cfgStandX;
        static ConfigEntry<float> _cfgStandZ;
        static ConfigEntry<float> _cfgStandYaw;

        /// <summary>The bearing of the dirt road through Litvinovka, in degrees
        /// clockwise from north (+Z). Measured from the two end nodes of edge 41
        /// in `assets/editor/roadnet_sample.json` - see the file header.</summary>
        const float DefaultRoadBearing = 225.4f;

        /// <summary>How far a player may be from the camp centre and still have
        /// the village loaded around him. Below this the block is searched for;
        /// above it nothing is scanned, because nothing is there to find.</summary>
        const float PlayerReach = 260f;

        /// <summary>Seconds between two searches for the block. A search is a
        /// physics query, and only in the worst case a scene-wide scan.</summary>
        const float ScanSeconds = 15f;

        /// <summary>Seconds before a failed spawn is tried again.</summary>
        const float RetrySeconds = 30f;

        /// <summary>Seconds between two head counts of the one man.</summary>
        const float WatchSeconds = 20f;

        /// <summary>Seconds before the civilian trader is looked for again after
        /// a miss. `FindObjectsOfType` walks every NPC_SpawnPoint in the scene,
        /// so a map that really has no storekeeper is not re-searched on every
        /// pass - but it IS searched again, because a miss usually only means
        /// the civilian settlement's chunk was not up yet.</summary>
        const float LookSeconds = 30f;

        /// <summary>How long the world has to have been up before the trader is
        /// built. The camp itself waits 20 s; the trader follows it.</summary>
        const float SettleSeconds = 25f;

        /// <summary>What a kiosk may measure, in metres. A thing smaller than
        /// this is a crate and a thing bigger is a house.</summary>
        const float MinSide = 0.9f;
        const float MaxSide = 6.5f;
        const float MinHeight = 1.6f;
        const float MaxHeight = 4.6f;

        /// <summary>The Photon spawn key of the trader. It HAS to carry a slash:
        /// `Revival.GroundEnemies.cs` reconciles every keyed NPC that looks like
        /// one of the editor's ground groups and deletes the rest, and the slash
        /// is what keeps our man out of that pass (the same guard the technical's
        /// crew relies on).</summary>
        const string ShopKey = "shop/litvinovka/trader";

        /// <summary>NPCBehaviorPattern.StoreKeeper. The value is used for the
        /// readback check only; the field itself is written by NAME.</summary>
        const int BehaviorStoreKeeper = 1;

        // ================================================================ state

        sealed class Block
        {
            internal Transform Root;
            internal string Path = "";
            internal Bounds Local;          // in Root's local space
            internal Vector3 Normal;        // world, outward, horizontal
            internal Vector3 Right;         // world, along the face
            internal Vector3 Up;            // world
            internal Vector3 Origin;        // world centre of the opening
            internal float Width;
            internal float Height;
            internal float Depth;           // half depth of the block along Normal
            internal float HalfWide;        // half width of the face, along Right
            internal float FloorY;
        }

        sealed class Cand
        {
            internal Transform Root;
            internal string Path = "";
            internal Bounds Local;
            internal Vector3 Size;
            internal float Distance;
            internal float Score;
            internal string Why = "";
        }

        static Block _block;
        static GameObject _window;
        static GameObject _shop;
        static Component _vendor;
        static Vector3 _stand;
        static Quaternion _look = Quaternion.identity;
        static bool _standKnown;
        static string _opening = "";

        static float _nextTick;
        static float _nextScan;
        static float _nextSpawn;
        static float _nextWatch;
        static float _worldSince;
        static float _goneSince;
        static bool _announced;
        static bool _standLogged;
        static bool _searchedInVain;
        static bool _playerFar;
        static string _lastFail = "";

        // What was changed on the map, so all of it can be put back.
        static readonly List<MeshFilter> _cutFilters = new List<MeshFilter>();
        static readonly List<Mesh> _cutBefore = new List<Mesh>();
        static readonly List<MeshCollider> _cutHulls = new List<MeshCollider>();
        static readonly List<Mesh> _hullBefore = new List<Mesh>();
        static readonly List<Collider> _switchedOff = new List<Collider>();
        static readonly List<Collider> _added = new List<Collider>();
        static readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        // The trade values, read off the civilian settlement's own trader.
        // `_tradeLooked` latches on the HIT only: a miss means the civilian
        // settlement's spawn points were not in the scene yet, and remembering
        // that would pin this shop to the fall-back for the whole session.
        static bool _tradeLooked;
        static bool _tradeWarned;
        static float _nextLook;
        static bool _tradeCopied;
        /// <summary>The man who stands there was built before the shop was
        /// found, so his four values are the fall-back. `Regrade` builds him
        /// again; `Spawn` clears it, because what it builds is current.</summary>
        static bool _tradeStale;
        static object _tradeStorage;
        static object _tradeRanks;
        static float _tradePercent = 0.5f;
        static int _tradeNpcType = -1;
        static int _tradeGrantWeapon = -1;
        static int _tradeWeaponId = -1;
        static string _tradeFrom = "";

        // ============================================================== binding

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("TraitorVendor", "Enabled", true,
                "A trader in the traitor settlement at Litvinovka, in the blue "
                + "block in the middle of the village: an opening is made in the "
                + "side of it that faces the street and a storekeeper is put "
                + "inside. He offers what the trader in the civilian settlement "
                + "offers - the shop's own values are copied off that trader at "
                + "runtime, not invented here. Off leaves the village exactly as "
                + "the map has it - and so does a switched-off TraitorSettlement, "
                + "because a village with no traitors in it has no traitors' "
                + "trader.");
            _cfgMode = cfg.Bind("TraitorVendor", "Mode", "auto",
                "How the opening is made. auto = cut the wall when its mesh can "
                + "be read and belongs to this block alone, otherwise build the "
                + "hatch. cut = only ever cut (nothing is built if the mesh "
                + "refuses). hatch = never touch the block; build a serving "
                + "hatch that projects from its wall instead. rebuild = hide the "
                + "block and draw it again as a box with a window in it - the "
                + "last resort, and it loses the original artwork. none = no "
                + "geometry at all, just the man.");
            _cfgBlock = cfg.Bind("TraitorVendor", "Block", "",
                "Pin the block by name. Empty = search for it. Any part of the "
                + "object's name or of its path in the scene will do; the log "
                + "prints the candidates it found with exactly that path, so a "
                + "wrong pick is corrected by copying one of those lines in "
                + "here.");
            _cfgSearch = cfg.Bind("TraitorVendor", "SearchRadius", 45f,
                "How far from the centre of the camp the block is looked for, in "
                + "metres (5..200). The request says it stands centrally in the "
                + "village.");
            _cfgFacing = cfg.Bind("TraitorVendor", "Facing", -1f,
                "Which way the opening looks, in degrees (0 = north, 90 = east). "
                + "Negative = work it out from the road: the window then faces "
                + "the closest point of the street through the village. Use this "
                + "when the hole ends up on the wrong side.");
            _cfgRoadBearing = cfg.Bind("TraitorVendor", "RoadBearing",
                DefaultRoadBearing,
                "The bearing of the street through Litvinovka in degrees, used "
                + "only while Facing is negative. The default is measured from "
                + "the road data in the repository (edge 41 of "
                + "assets/editor/roadnet_sample.json).");
            _cfgWidth = cfg.Bind("TraitorVendor", "WindowWidth", 1.3f,
                "Width of the opening in metres (0.6..4). It is never allowed to "
                + "eat the whole wall: the block keeps a pillar on each side.");
            _cfgHeight = cfg.Bind("TraitorVendor", "WindowHeight", 1.05f,
                "Height of the opening in metres (0.5..3).");
            _cfgSill = cfg.Bind("TraitorVendor", "WindowSill", 0.95f,
                "Height of the counter above the foot of the block, in metres "
                + "(0.2..2.5). The trader is seen from the chest up, which is "
                + "what a serving window looks like.");
            _cfgShift = cfg.Bind("TraitorVendor", "WindowShift", 0f,
                "Move the opening sideways along the wall, in metres. 0 centres "
                + "it.");
            _cfgRecess = cfg.Bind("TraitorVendor", "Recess", 0.55f,
                "How deep the opening is lined behind the wall, in metres "
                + "(0.2..1.5). A cut hole without this would look straight "
                + "through the block - the far wall is only drawn from outside. "
                + "In hatch mode this is how far the counter projects instead.");
            _cfgStandBack = cfg.Bind("TraitorVendor", "StandBack", 0.45f,
                "How far behind the wall the trader stands, in metres "
                + "(0.1..1.5). In hatch mode he stands that far IN FRONT of it "
                + "instead, inside the hatch.");
            _cfgTraderFrom = cfg.Bind("TraitorVendor", "CopyShopFrom", "",
                "Which trader on the map his shop is copied from. Empty = the "
                + "one in the civilian settlement (a StoreKeeper spawn point "
                + "under a settlement whose name mentions StoreKeeper or Peace). "
                + "Any part of a settlement's name pins another one.");
            _cfgOutside = cfg.Bind("TraitorVendor", "VendorWithoutBlock", true,
                "Put the trader in the village even when the block could not be "
                + "found - in the open, at the centre of the camp. The "
                + "settlement then has a trader like every other settlement, "
                + "which is the point of the whole feature, and the log names "
                + "the candidates so the block can be pinned afterwards.");
            _cfgStandX = cfg.Bind("TraitorVendor", "StandX", 0f,
                "Pin the exact spot the trader stands on, world X. 0 = work it "
                + "out from the block. The log prints the three numbers of the "
                + "spot it found, ready to be copied in here - which is also how "
                + "a host who never walks to Litvinovka gets a trader there.");
            _cfgStandZ = cfg.Bind("TraitorVendor", "StandZ", 0f,
                "Pin the exact spot, world Z. 0 = work it out from the block.");
            _cfgStandYaw = cfg.Bind("TraitorVendor", "StandYaw", -1f,
                "Which way the pinned trader looks, in degrees. Negative = work "
                + "it out.");
        }

        static bool Enabled
        {
            get { return _cfgEnabled != null && _cfgEnabled.Value; }
        }

        static string Mode
        {
            get
            {
                string m = _cfgMode == null ? "auto" : _cfgMode.Value;
                if (m == null) return "auto";
                m = m.Trim().ToLowerInvariant();
                return m.Length == 0 ? "auto" : m;
            }
        }

        static float Radius()
        {
            return Mathf.Clamp(_cfgSearch == null ? 45f : _cfgSearch.Value, 5f, 200f);
        }

        static float Width()
        {
            return Mathf.Clamp(_cfgWidth == null ? 1.3f : _cfgWidth.Value, 0.6f, 4f);
        }

        static float Height()
        {
            return Mathf.Clamp(_cfgHeight == null ? 1.05f : _cfgHeight.Value, 0.5f, 3f);
        }

        static float Sill()
        {
            return Mathf.Clamp(_cfgSill == null ? 0.95f : _cfgSill.Value, 0.2f, 2.5f);
        }

        static float Recess()
        {
            return Mathf.Clamp(_cfgRecess == null ? 0.55f : _cfgRecess.Value, 0.2f, 1.5f);
        }

        static float StandBack()
        {
            return Mathf.Clamp(_cfgStandBack == null ? 0.45f : _cfgStandBack.Value,
                               0.1f, 1.5f);
        }

        // ================================================================ frame

        public static void Tick()
        {
            if (_cfgEnabled == null) return;
            try
            {
                // The camp owns the village. A Litvinovka with no traitors in it
                // is no traitor settlement, so the shop closes with the camp
                // (`NewSettlement.Wanted`) exactly as it closes with its own
                // switch: the block is put back and the man is taken away.
                if (!Enabled || !NewSettlement.Wanted())
                {
                    if (_window != null || _shop != null || _cutFilters.Count > 0)
                        Teardown();
                    return;
                }
                if (Time.time < _nextTick) return;
                _nextTick = Time.time + 1f;

                // The block, the camp and the road all belong to ONE map. On any
                // other region this coordinate is a piece of landscape.
                if (!NewSettlement.Here()) { Forget(); return; }

                GameObject player = MapTools.LocalPlayer();
                if (player == null) { _worldSince = 0f; Forget(); return; }
                if (_worldSince <= 0f) _worldSince = Time.time;

                Vector3 centre = NewSettlement.Centre();
                Vector3 me = player.transform.position;
                float dx = me.x - centre.x, dz = me.z - centre.z;
                bool near = dx * dx + dz * dz <= PlayerReach * PlayerReach;
                _playerFar = !near;

                // A block that was unloaded with its chunk takes its opening
                // with it; both come back when the village is loaded again.
                if (_block != null && (_block.Root == null
                    || !_block.Root.gameObject.activeInHierarchy))
                {
                    Restore();
                    _block = null;
                }

                if (_block == null && near && Time.time >= _nextScan) Scan(centre);
                if (_block != null && _window == null) Open();

                if (!RevivalTroopInsertion.MasterClient()) return;
                if (Time.time < _worldSince + SettleSeconds) return;

                Stand();
                if (!_standKnown) return;

                if (_shop == null || _vendor == null)
                {
                    if (Time.time >= _nextSpawn) Spawn();
                    return;
                }
                if (Time.time < _nextWatch) return;
                _nextWatch = Time.time + WatchSeconds;
                Watch();
                // Watch may have dropped him already; two drops in one pass
                // would only log the same rebuild twice.
                if (_shop != null) Regrade();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TraitorVendor tick: " + ex);
            }
        }

        /// <summary>
        /// Hold the man on his spot, AFTER the animator has written his root.
        /// The same pass `TechnicalCrew.Setzen` makes for a man riding a truck,
        /// for the same measured reason: a character placed in Update is back
        /// where the animation put him before anything is drawn, and a
        /// NavMeshAgent with a path of its own walks him out of the shop.
        /// Master only - his position is replicated from there, and a client
        /// that writes a remote puppet's transform only fights the sync.
        /// </summary>
        internal static void LateFrame()
        {
            if (!Enabled || _vendor == null || !_standKnown) return;
            try
            {
                if (!RevivalTroopInsertion.MasterClient()) return;

                // HANDS OFF WHILE SOMEBODY IS TALKING TO HIM. The trade runs
                // through the game's own talk state, and a man whose intentions
                // are cleared and whose body is turned sixty times a second
                // while that state is up is a shop that closes in the customer's
                // face. He cannot walk away during a conversation either, so
                // there is nothing to hold.
                if (Flag(_vendor, "IsTalkActive")) return;

                Transform tr = _vendor.transform;
                if (tr == null) return;
                float away = (tr.position - _stand).sqrMagnitude;
                if (away > 0.0004f)
                {
                    // Only a REAL jump takes the agent with it - it is a NavMesh
                    // query, and a man who has drifted a centimetre is not
                    // walking anywhere (TechnicalCrew.Setzen, same reason).
                    if (away > 4f)
                    {
                        NavMeshAgent agent = Agent(_vendor);
                        try
                        {
                            if (agent != null && agent.isActiveAndEnabled
                                && agent.isOnNavMesh)
                            {
                                agent.ResetPath();
                                agent.Warp(_stand);
                            }
                        }
                        catch { }
                    }
                    tr.position = _stand;
                    Quiet(_vendor);
                }
                tr.rotation = _look;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor, frame: " + ex.Message);
            }
        }

        static void Forget()
        {
            if (_window != null || _cutFilters.Count > 0 || _switchedOff.Count > 0)
                Restore();
            _block = null;
        }

        // ============================================================ the block

        /// <summary>
        /// Look for the blue block. Physics first, because a prop's colliders
        /// are the one thing static batching leaves alone; a scene scan only if
        /// that finds nothing at all, and at most once every ScanSeconds.
        /// </summary>
        static void Scan(Vector3 centre)
        {
            _nextScan = Time.time + ScanSeconds;
            float reach = Radius();
            float groundY;
            if (!RevivalTroopInsertion.GroundY(centre, out groundY)) groundY = centre.y;

            List<Cand> found = new List<Cand>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            string pin = _cfgBlock == null ? "" : _cfgBlock.Value.Trim();

            try
            {
                Collider[] hits = Physics.OverlapBox(
                    new Vector3(centre.x, groundY + 6f, centre.z),
                    new Vector3(reach, 14f, reach), Quaternion.identity,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i] == null || hits[i] is TerrainCollider) continue;
                    Consider(PropRoot(hits[i].transform), centre, reach, seen, found);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: physics search - "
                    + ex.Message);
            }

            if (found.Count == 0)
            {
                // Nothing has a live collider here - the village may be drawn
                // without being solid yet. The renderers are still in the scene.
                MeshFilter[] filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
                for (int i = 0; i < filters.Length; i++)
                {
                    if (filters[i] == null) continue;
                    Vector3 p = filters[i].transform.position;
                    float fx = p.x - centre.x, fz = p.z - centre.z;
                    if (fx * fx + fz * fz > reach * reach) continue;
                    Consider(PropRoot(filters[i].transform), centre, reach, seen, found);
                }
                if (found.Count > 0)
                    RevivalPlugin.L.LogInfo("TraitorVendor: no prop collider was "
                        + "live at Litvinovka, so the block was found by its "
                        + "renderer instead.");
            }

            if (found.Count == 0)
            {
                _searchedInVain = true;
                Fail("nothing that could be a kiosk stands within "
                    + Num(reach) + " m of " + Num(centre.x) + ", " + Num(centre.z)
                    + " - raise TraitorVendor.SearchRadius or move the camp");
                return;
            }

            found.Sort(new Comparison<Cand>(ByScore));
            int show = Mathf.Min(6, found.Count);
            string list = "";
            for (int i = 0; i < show; i++)
                list += "\n    " + Num(found[i].Score) + "  " + found[i].Path
                    + "  " + Num(found[i].Size.x) + " x " + Num(found[i].Size.z)
                    + " x " + Num(found[i].Size.y) + " m, "
                    + Num(found[i].Distance) + " m away" + found[i].Why;

            Cand best = null;
            if (pin.Length > 0)
            {
                for (int i = 0; i < found.Count && best == null; i++)
                    if (found[i].Path.ToLowerInvariant().IndexOf(pin.ToLowerInvariant(),
                        StringComparison.Ordinal) >= 0) best = found[i];
                if (best == null)
                {
                    _searchedInVain = true;
                    Fail("TraitorVendor.Block is \"" + pin + "\" and nothing near "
                        + "the camp carries that name. Candidates:" + list);
                    return;
                }
            }
            else best = found[0];

            _block = Frame(best);
            if (_block == null)
            {
                _searchedInVain = true;
                Fail("the block \"" + best.Path + "\" could not be measured");
                return;
            }
            _searchedInVain = false;
            _standLogged = false;
            _lastFail = "";
            RevivalPlugin.L.LogInfo("TraitorVendor: the block is \"" + best.Path
                + "\", " + Num(best.Size.x) + " x " + Num(best.Size.z) + " x "
                + Num(best.Size.y) + " m, " + Num(best.Distance)
                + " m from the centre of the camp; the opening goes into the "
                + "side facing " + Num(Bearing(_block.Normal))
                + " degrees. Candidates were:" + list);
        }

        static int ByScore(Cand a, Cand b)
        {
            if (a == null || b == null) return 0;
            return b.Score.CompareTo(a.Score);
        }

        /// <summary>One candidate, measured and scored. Everything that cannot
        /// be a kiosk is refused here, so the log stays readable.</summary>
        static void Consider(Transform root, Vector3 centre, float reach,
                             Dictionary<int, bool> seen, List<Cand> found)
        {
            if (root == null) return;
            int id = root.GetInstanceID();
            if (seen.ContainsKey(id)) return;
            seen[id] = true;
            try
            {
                if (root.name.StartsWith("NDR_", StringComparison.Ordinal)) return;
                if (root.GetComponentInParent<Rigidbody>() != null) return;
                if (root.GetComponentInParent<CharacterController>() != null) return;
                if (root.GetComponentInChildren<Terrain>(true) != null) return;
                if (Networked(root)) return;

                Bounds local;
                if (!LocalBounds(root, out local)) return;
                Vector3 scale = Abs(root.lossyScale);
                Vector3 size = new Vector3(local.size.x * scale.x,
                                           local.size.y * scale.y,
                                           local.size.z * scale.z);
                if (size.y < MinHeight || size.y > MaxHeight) return;
                if (size.x < MinSide || size.z < MinSide) return;
                if (size.x > MaxSide || size.z > MaxSide) return;

                Vector3 mid = root.TransformPoint(local.center);
                float dx = mid.x - centre.x, dz = mid.z - centre.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > reach) return;

                Cand c = new Cand();
                c.Root = root;
                c.Path = PathOf(root);
                c.Local = local;
                c.Size = size;
                c.Distance = dist;

                string lower = c.Path.ToLowerInvariant();
                float score = 4f - dist / Mathf.Max(1f, reach) * 4f;
                if (Has(lower, "kiosk") || Has(lower, "larek") || Has(lower, "larok"))
                { score += 6f; c.Why += ", named like a kiosk"; }
                if (Has(lower, "shop") || Has(lower, "magaz") || Has(lower, "market")
                    || Has(lower, "torg") || Has(lower, "trade"))
                { score += 4f; c.Why += ", named like a shop"; }
                if (Has(lower, "stall") || Has(lower, "stand") || Has(lower, "palat")
                    || Has(lower, "booth") || Has(lower, "post"))
                { score += 2f; c.Why += ", named like a stall"; }
                if (Has(lower, "trash") || Has(lower, "musor") || Has(lower, "garbage")
                    || Has(lower, "container") || Has(lower, "toilet")
                    || Has(lower, "fence") || Has(lower, "fance")
                    || Has(lower, "tree") || Has(lower, "rock"))
                { score -= 5f; c.Why += ", named like something else"; }

                Color paint;
                if (Tint(root, out paint))
                {
                    if (paint.b > paint.r * 1.12f && paint.b > paint.g * 1.04f)
                    { score += 4f; c.Why += ", blue"; }
                    else c.Why += ", not blue";
                }

                // A kiosk is a box: no side more than three times another.
                float wide = Mathf.Max(size.x, size.z), thin = Mathf.Min(size.x, size.z);
                if (wide > thin * 3f) { score -= 3f; c.Why += ", long and thin"; }

                c.Score = score;
                found.Add(c);
            }
            catch { }
        }

        static bool Has(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>The prop this collider or renderer belongs to. A prop in
        /// these scenes is an LOD GROUP - every level, the colliders and the
        /// parts hang under the node that carries the LODGroup, and that node is
        /// the object (`Revival.Helipads.cs`, Group/Prop).</summary>
        static Transform PropRoot(Transform t)
        {
            if (t == null) return null;
            LODGroup group = t.GetComponentInParent<LODGroup>();
            if (group != null) return group.transform;
            // Nothing above it is an LOD group, so climb while the parent is
            // still a single object rather than a container of props.
            Transform p = t;
            int climbed = 0;
            while (p.parent != null && climbed < 3 && p.parent.childCount <= 8
                   && !Container(p.parent.name))
            {
                Bounds probe;
                if (!LocalBounds(p.parent, out probe)) break;
                Vector3 scale = Abs(p.parent.lossyScale);
                if (probe.size.x * scale.x > MaxSide
                    || probe.size.z * scale.z > MaxSide
                    || probe.size.y * scale.y > MaxHeight) break;
                p = p.parent;
                climbed++;
            }
            return p;
        }

        /// <summary>A node that holds props rather than being one. Measuring
        /// those means walking a whole map chunk, which is both wrong and
        /// expensive; the climb above stops at them by name.</summary>
        static bool Container(string name)
        {
            string lower = name.ToLowerInvariant();
            return Has(lower, "chunk") || Has(lower, "content")
                || Has(lower, "props") || Has(lower, "objects")
                || Has(lower, "gameworld") || Has(lower, "scene");
        }

        /// <summary>Bounds in the object's OWN space, from the colliders first:
        /// static batching can leave a renderer describing a whole map chunk,
        /// and the physics geometry is what still describes this one prop.
        /// </summary>
        static bool LocalBounds(Transform root, out Bounds box)
        {
            box = new Bounds();
            bool any = false;
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || colliders[i].isTrigger) continue;
                if (colliders[i] is TerrainCollider) continue;
                Bounds b;
                if (!LocalOf(root, colliders[i].transform,
                             LocalShape(colliders[i]), out b)) continue;
                if (!any) { box = b; any = true; } else box.Encapsulate(b);
            }
            if (any) return true;

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i] == null || filters[i].sharedMesh == null) continue;
                Bounds b;
                if (!LocalOf(root, filters[i].transform,
                             filters[i].sharedMesh.bounds, out b)) continue;
                if (!any) { box = b; any = true; } else box.Encapsulate(b);
            }
            return any;
        }

        /// <summary>The shape of one collider in ITS own local space.</summary>
        static Bounds LocalShape(Collider c)
        {
            BoxCollider box = c as BoxCollider;
            if (box != null) return new Bounds(box.center, box.size);
            SphereCollider ball = c as SphereCollider;
            if (ball != null)
                return new Bounds(ball.center, Vector3.one * (ball.radius * 2f));
            CapsuleCollider cap = c as CapsuleCollider;
            if (cap != null)
            {
                Vector3 size = Vector3.one * (cap.radius * 2f);
                if (cap.direction == 0) size.x = Mathf.Max(size.x, cap.height);
                else if (cap.direction == 1) size.y = Mathf.Max(size.y, cap.height);
                else size.z = Mathf.Max(size.z, cap.height);
                return new Bounds(cap.center, size);
            }
            MeshCollider mesh = c as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null) return mesh.sharedMesh.bounds;
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>A child's local box expressed in the root's local space.
        /// Eight corners, because the child may be turned.</summary>
        static bool LocalOf(Transform root, Transform child, Bounds shape,
                            out Bounds box)
        {
            box = new Bounds();
            if (shape.size.sqrMagnitude <= 0f) return false;
            Vector3 c = shape.center, e = shape.extents;
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 local = root.InverseTransformPoint(
                    child.TransformPoint(corner));
                if (!any) { box = new Bounds(local, Vector3.zero); any = true; }
                else box.Encapsulate(local);
            }
            return any;
        }

        static bool Networked(Transform root)
        {
            try
            {
                Type view = RevivalPlugin.TypeByName("PhotonView");
                if (view == null) return false;
                return root.GetComponentInParent(view) != null;
            }
            catch { return false; }
        }

        /// <summary>The colour of the first material that has one. Most world
        /// materials are white with a texture, so this only ever ADDS to a
        /// candidate's score - it never refuses one.</summary>
        static bool Tint(Transform root, out Color colour)
        {
            colour = Color.white;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                Material m = rs[i].sharedMaterial;
                if (m == null || !m.HasProperty("_Color")) continue;
                Color c = m.GetColor("_Color");
                if (c.r > 0.92f && c.g > 0.92f && c.b > 0.92f) continue;
                colour = c;
                return true;
            }
            return false;
        }

        static string PathOf(Transform t)
        {
            string path = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard < 12) { path = p.name + "/" + path; p = p.parent; guard++; }
            return path;
        }

        // ======================================================== the geometry

        /// <summary>Measure the opening on a candidate: which side it goes into,
        /// where its centre is and how big it may be on this block.</summary>
        static Block Frame(Cand c)
        {
            if (c == null || c.Root == null) return null;
            Block b = new Block();
            b.Root = c.Root;
            b.Path = c.Path;
            b.Local = c.Local;

            Vector3 scale = Abs(c.Root.lossyScale);
            Vector3 mid = c.Root.TransformPoint(c.Local.center);
            Vector3 up = c.Root.TransformDirection(Vector3.up);
            if (up.sqrMagnitude < 0.0001f) up = Vector3.up;
            up.Normalize();

            // Which of the four upright sides looks at the street.
            Vector3 want = ToRoad(mid);
            Vector3[] axes = new Vector3[] { Vector3.right, Vector3.left,
                                             Vector3.forward, Vector3.back };
            float bestDot = -2f;
            Vector3 normal = Vector3.forward;
            float halfDepth = 1f, halfWidth = 1f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 world = c.Root.TransformDirection(axes[i]);
                world.y = 0f;
                if (world.sqrMagnitude < 0.0001f) continue;
                world.Normalize();
                float dot = Vector3.Dot(world, want);
                if (dot <= bestDot) continue;
                bestDot = dot;
                normal = world;
                bool alongX = i < 2;
                halfDepth = alongX ? c.Local.extents.x * scale.x
                                   : c.Local.extents.z * scale.z;
                halfWidth = alongX ? c.Local.extents.z * scale.z
                                   : c.Local.extents.x * scale.x;
            }

            b.Normal = normal;
            b.Up = up;
            b.Right = Vector3.Cross(up, normal).normalized;
            b.Depth = halfDepth;
            b.HalfWide = halfWidth;

            float boxHeight = c.Local.size.y * scale.y;
            float width = Mathf.Min(Width(), Mathf.Max(0.5f, halfWidth * 2f - 0.5f));
            float sill = Mathf.Clamp(Sill(), 0.2f, Mathf.Max(0.25f, boxHeight - 0.6f));
            float height = Mathf.Min(Height(), Mathf.Max(0.35f, boxHeight - sill - 0.2f));
            b.Width = width;
            b.Height = height;

            float shift = _cfgShift == null ? 0f : _cfgShift.Value;
            shift = Mathf.Clamp(shift, -(halfWidth - width * 0.5f),
                                halfWidth - width * 0.5f);

            Vector3 faceMid = mid + normal * halfDepth;
            float bottom = c.Local.extents.y * scale.y;      // from mid to the foot
            b.Origin = faceMid + b.Right * shift
                     + up * (sill + height * 0.5f - bottom);

            float ground;
            float footY = mid.y - bottom;
            if (!RevivalTroopInsertion.GroundY(b.Origin, out ground)) ground = footY;
            b.FloorY = footY > ground + 0.05f && footY < ground + 1.2f ? footY : ground;
            return b;
        }

        /// <summary>Which way the street is from here. `Facing` answers it with
        /// one number; otherwise the road through the village is a straight line
        /// of known bearing through the camp centre, and the window looks at the
        /// closest point of it.</summary>
        static Vector3 ToRoad(Vector3 from)
        {
            float facing = _cfgFacing == null ? -1f : _cfgFacing.Value;
            if (facing >= 0f)
                return Quaternion.Euler(0f, facing, 0f) * Vector3.forward;

            float bearing = _cfgRoadBearing == null ? DefaultRoadBearing
                                                    : _cfgRoadBearing.Value;
            float rad = bearing * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 centre = NewSettlement.Centre();
            Vector3 rel = new Vector3(from.x - centre.x, 0f, from.z - centre.z);
            Vector3 onLine = dir * Vector3.Dot(rel, dir);
            Vector3 away = onLine - rel;                 // from the block to the road
            if (away.sqrMagnitude < 0.25f)
            {
                // The block stands ON the line - then the street is beside it,
                // and either side of the road is as good an answer as the other.
                away = new Vector3(dir.z, 0f, -dir.x);
            }
            away.y = 0f;
            return away.normalized;
        }

        static float Bearing(Vector3 dir)
        {
            float a = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return a < 0f ? a + 360f : a;
        }

        /// <summary>Make the opening. One of three ways, see the file header.
        /// Everything built here hangs under one object and everything changed
        /// is remembered, so `Restore` puts the map back as it was.</summary>
        static void Open()
        {
            string mode = Mode;
            if (mode == "none") { _opening = "none"; MarkOpen(); return; }
            try
            {
                bool cut = false;
                if (mode == "cut" || mode == "auto")
                {
                    int triangles;
                    cut = CutWall(out triangles);
                    if (cut)
                        RevivalPlugin.L.LogInfo("TraitorVendor: the wall was cut - "
                            + triangles + " triangle(s) of \"" + _block.Path
                            + "\" clipped against the window, colliders split "
                            + "around it.");
                    else if (mode == "cut")
                    {
                        Fail("Mode is \"cut\" and the mesh of \"" + _block.Path
                            + "\" cannot be cut (see the line above). Nothing was "
                            + "built; \"auto\" would have built the hatch instead");
                        _opening = "";
                        MarkOpen();
                        return;
                    }
                }

                if (mode == "rebuild")
                {
                    HideBlock();
                    BuildShell();
                    BuildLining(true);
                    Carve();
                    _opening = "rebuild";
                }
                else if (cut)
                {
                    BuildLining(true);
                    Carve();
                    _opening = "cut";
                }
                else
                {
                    BuildLining(false);
                    _opening = "hatch";
                }
                MarkOpen();
                RevivalPlugin.L.LogInfo("TraitorVendor: the opening is ready ("
                    + _opening + ") in \"" + _block.Path + "\", "
                    + Num(_block.Width) + " x " + Num(_block.Height)
                    + " m, counter " + Num(Sill()) + " m up, facing "
                    + Num(Bearing(_block.Normal)) + " degrees.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: the opening could not be "
                    + "made - " + ex.Message);
                Restore();
                _opening = "";
                MarkOpen();
            }
        }

        /// <summary>The opening was dealt with, whatever came of it: without
        /// this the whole pass would run again every second.</summary>
        static void MarkOpen()
        {
            if (_window != null) return;
            _window = new GameObject("NDR_TraitorShopWindow");
            _window.transform.position = _block.Origin;
            _window.transform.rotation = Quaternion.LookRotation(_block.Normal, _block.Up);
        }

        // ---------------------------------------------------------- the cut

        /// <summary>
        /// Cut the window out of the block's own mesh.
        ///
        /// Every MeshFilter under the block is taken in turn. A mesh is only cut
        /// when it can be READ and when it is this prop's own - a mesh whose
        /// bounds are far bigger than the prop is a static batch of a whole map
        /// chunk, and putting a hole through that would cut a hole in the
        /// village. The original Mesh object is never written to: the cut is a
        /// new Mesh and the old one is kept for `Restore`.
        /// </summary>
        static bool CutWall(out int triangles)
        {
            triangles = 0;
            if (_block == null || _block.Root == null) return false;
            MeshFilter[] filters = _block.Root.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length == 0)
            {
                Note("\"" + _block.Path + "\" has no mesh of its own to cut");
                return false;
            }

            Matrix4x4 toFrame = Matrix4x4.TRS(_block.Origin,
                Quaternion.LookRotation(_block.Normal, _block.Up), Vector3.one).inverse;
            float halfW = _block.Width * 0.5f;
            float halfH = _block.Height * 0.5f;
            // How deep the cut reaches. Far enough to take the whole thickness
            // of a wall, never far enough to reach the far side of the block -
            // the back of a kiosk is what one is meant to see through the hole.
            float back = -Mathf.Min(Recess(),
                                    Mathf.Max(0.15f, _block.Depth * 2f - 0.12f));
            float front = 0.08f;
            Vector3 blockSize = Vector3.Scale(_block.Local.size,
                                              Abs(_block.Root.lossyScale));

            int done = 0;
            string why = "";
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                Mesh src = mf.sharedMesh;

                if (src.vertexCount > 20000)
                { why = "the mesh has " + src.vertexCount + " vertices"; continue; }

                // A batched chunk mesh describes far more than this prop. Both
                // sizes are taken in metres, or a scaled child would look like a
                // chunk and a chunk under a small scale would not.
                Vector3 mSize = Vector3.Scale(src.bounds.size,
                                              Abs(mf.transform.lossyScale));
                if (mSize.x > blockSize.x * 2f + 2f || mSize.y > blockSize.y * 2f + 2f
                    || mSize.z > blockSize.z * 2f + 2f)
                { why = "its mesh covers " + Num(mSize.x) + " x " + Num(mSize.z)
                        + " x " + Num(mSize.y) + " m and is a static batch of more "
                        + "than this block"; continue; }

                Vector3[] verts = null;
                try { verts = src.vertices; }
                catch { verts = null; }
                if (verts == null || verts.Length == 0)
                { why = "its mesh cannot be read (Read/Write is off)"; continue; }

                int removed;
                Mesh cut = CutMesh(src, verts, toFrame * mf.transform.localToWorldMatrix,
                                   halfW, halfH, back, front, out removed);
                if (cut == null || removed == 0) continue;

                _cutFilters.Add(mf);
                _cutBefore.Add(src);
                mf.sharedMesh = cut;

                // A hull built from the same mesh gets the same hole, and then
                // nothing has to be split around the opening for that object.
                MeshCollider hull = mf.GetComponent<MeshCollider>();
                if (hull != null && hull.sharedMesh == src && !hull.convex)
                {
                    _cutHulls.Add(hull);
                    _hullBefore.Add(src);
                    hull.sharedMesh = cut;
                }
                triangles += removed;
                done++;
            }

            if (done == 0 && why.Length > 0)
                Note("the wall of \"" + _block.Path + "\" was not cut: " + why);
            return done > 0;
        }

        struct Pt
        {
            internal Vector3 F;      // the vertex in the window frame
            internal int I;          // its index in the growing vertex arrays
        }

        static readonly List<Pt> _poly = new List<Pt>();
        static readonly List<Pt> _rest = new List<Pt>();
        static readonly List<Pt> _keep = new List<Pt>();
        static readonly List<Pt> _spare = new List<Pt>();

        /// <summary>
        /// One mesh, minus the window. Every triangle that lies INSIDE the wall
        /// slab is clipped against the four sides of the opening: the parts
        /// outside the rectangle are kept and re-triangulated, the part inside is
        /// dropped. A triangle that is not wholly inside the slab is left alone,
        /// so the roof and the far wall of the block are never touched.
        ///
        /// Split vertices interpolate every attribute the mesh has, lightmap UVs
        /// included - a wall that loses its second UV set renders unlit.
        /// </summary>
        static Mesh CutMesh(Mesh src, Vector3[] verts, Matrix4x4 toFrame,
                            float halfW, float halfH, float back, float front,
                            out int removed)
        {
            removed = 0;
            Vector3[] normals = src.normals;
            Vector4[] tangents = src.tangents;
            Vector2[] uv = src.uv;
            Vector2[] uv2 = src.uv2;
            Color32[] colors = src.colors32;
            if (normals != null && normals.Length != verts.Length) normals = null;
            if (tangents != null && tangents.Length != verts.Length) tangents = null;
            if (uv != null && uv.Length != verts.Length) uv = null;
            if (uv2 != null && uv2.Length != verts.Length) uv2 = null;
            if (colors != null && colors.Length != verts.Length) colors = null;

            List<Vector3> nv = new List<Vector3>(verts);
            List<Vector3> nn = normals == null ? null : new List<Vector3>(normals);
            List<Vector4> nt = tangents == null ? null : new List<Vector4>(tangents);
            List<Vector2> n1 = uv == null ? null : new List<Vector2>(uv);
            List<Vector2> n2 = uv2 == null ? null : new List<Vector2>(uv2);
            List<Color32> nc = colors == null ? null : new List<Color32>(colors);

            Vector3[] frame = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                frame[i] = toFrame.MultiplyPoint3x4(verts[i]);

            int subs = src.subMeshCount;
            List<int[]> rebuilt = new List<int[]>();
            bool touched = false;
            for (int s = 0; s < subs; s++)
            {
                int[] tris = src.GetTriangles(s);
                List<int> keep = new List<int>(tris.Length);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    Vector3 fa = frame[a], fb = frame[b], fc = frame[c];

                    bool inSlab = fa.z >= back && fa.z <= front
                               && fb.z >= back && fb.z <= front
                               && fc.z >= back && fc.z <= front;
                    bool overlaps = inSlab
                        && Mathf.Min(fa.x, Mathf.Min(fb.x, fc.x)) < halfW
                        && Mathf.Max(fa.x, Mathf.Max(fb.x, fc.x)) > -halfW
                        && Mathf.Min(fa.y, Mathf.Min(fb.y, fc.y)) < halfH
                        && Mathf.Max(fa.y, Mathf.Max(fb.y, fc.y)) > -halfH;
                    if (!overlaps)
                    {
                        keep.Add(a); keep.Add(b); keep.Add(c);
                        continue;
                    }

                    _poly.Clear();
                    _poly.Add(Make(fa, a));
                    _poly.Add(Make(fb, b));
                    _poly.Add(Make(fc, c));
                    Difference(_poly, halfW, halfH, keep, nv, nn, nt, n1, n2, nc);
                    touched = true;
                    removed++;
                }
                rebuilt.Add(keep.ToArray());
            }
            if (!touched) return null;

            Mesh cut = new Mesh();
            cut.name = src.name + "_NDR_window";
            cut.vertices = nv.ToArray();
            if (nn != null) cut.normals = nn.ToArray();
            if (nt != null) cut.tangents = nt.ToArray();
            if (n1 != null) cut.uv = n1.ToArray();
            if (n2 != null) cut.uv2 = n2.ToArray();
            if (nc != null) cut.colors32 = nc.ToArray();
            cut.subMeshCount = subs;
            for (int s = 0; s < subs; s++) cut.SetTriangles(rebuilt[s], s);
            cut.RecalculateBounds();
            return cut;
        }

        static Pt Make(Vector3 f, int i)
        {
            Pt p = new Pt();
            p.F = f;
            p.I = i;
            return p;
        }

        /// <summary>
        /// Triangle minus rectangle. The polygon is cut by the four sides of the
        /// opening in turn; whatever falls OUTSIDE a side is a finished piece of
        /// wall, whatever falls inside all four is the hole and is dropped. Every
        /// piece is emitted as a fan, which is correct because each piece is
        /// convex (a convex polygon cut by a straight line stays convex).
        /// </summary>
        static void Difference(List<Pt> poly, float halfW, float halfH,
            List<int> tris, List<Vector3> nv, List<Vector3> nn, List<Vector4> nt,
            List<Vector2> n1, List<Vector2> n2, List<Color32> nc)
        {
            // x <= -halfW is wall, the rest carries on.
            Split(poly, 0, -halfW, _keep, _rest, nv, nn, nt, n1, n2, nc);
            Fan(_keep, tris);
            Copy(_rest, _poly);
            if (_poly.Count < 3) return;

            // x >= +halfW is wall.
            Split(_poly, 0, halfW, _rest, _keep, nv, nn, nt, n1, n2, nc);
            Fan(_keep, tris);
            Copy(_rest, _poly);
            if (_poly.Count < 3) return;

            // y <= -halfH is wall (below the opening).
            Split(_poly, 1, -halfH, _keep, _rest, nv, nn, nt, n1, n2, nc);
            Fan(_keep, tris);
            Copy(_rest, _poly);
            if (_poly.Count < 3) return;

            // y >= +halfH is wall (above the opening); what is left is the hole.
            Split(_poly, 1, halfH, _rest, _keep, nv, nn, nt, n1, n2, nc);
            Fan(_keep, tris);
        }

        static void Copy(List<Pt> from, List<Pt> to)
        {
            to.Clear();
            for (int i = 0; i < from.Count; i++) to.Add(from[i]);
        }

        /// <summary>Cut a convex polygon by the line (axis == value). `low` takes
        /// the part on the smaller side, `high` the part on the larger; the two
        /// share the vertices created on the line.</summary>
        static void Split(List<Pt> poly, int axis, float value,
            List<Pt> low, List<Pt> high, List<Vector3> nv, List<Vector3> nn,
            List<Vector4> nt, List<Vector2> n1, List<Vector2> n2, List<Color32> nc)
        {
            _spare.Clear();
            for (int i = 0; i < poly.Count; i++) _spare.Add(poly[i]);
            low.Clear();
            high.Clear();
            for (int i = 0; i < _spare.Count; i++)
            {
                Pt a = _spare[i];
                Pt b = _spare[(i + 1) % _spare.Count];
                float da = (axis == 0 ? a.F.x : a.F.y) - value;
                float db = (axis == 0 ? b.F.x : b.F.y) - value;
                if (da <= 0f) low.Add(a);
                if (da >= 0f) high.Add(a);
                if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
                {
                    float t = da / (da - db);
                    Pt mid = Lerp(a, b, t, nv, nn, nt, n1, n2, nc);
                    low.Add(mid);
                    high.Add(mid);
                }
            }
            if (low.Count < 3) low.Clear();
            if (high.Count < 3) high.Clear();
        }

        /// <summary>A new vertex on the line between two old ones. Every
        /// attribute the mesh carries is interpolated with it.</summary>
        static Pt Lerp(Pt a, Pt b, float t, List<Vector3> nv, List<Vector3> nn,
            List<Vector4> nt, List<Vector2> n1, List<Vector2> n2, List<Color32> nc)
        {
            int index = nv.Count;
            nv.Add(Vector3.Lerp(nv[a.I], nv[b.I], t));
            if (nn != null) nn.Add(Vector3.Lerp(nn[a.I], nn[b.I], t).normalized);
            if (nt != null) nt.Add(Vector4.Lerp(nt[a.I], nt[b.I], t));
            if (n1 != null) n1.Add(Vector2.Lerp(n1[a.I], n1[b.I], t));
            if (n2 != null) n2.Add(Vector2.Lerp(n2[a.I], n2[b.I], t));
            if (nc != null)
            {
                Color32 ca = nc[a.I], cb = nc[b.I];
                nc.Add(new Color32(Mix(ca.r, cb.r, t), Mix(ca.g, cb.g, t),
                                   Mix(ca.b, cb.b, t), Mix(ca.a, cb.a, t)));
            }
            Pt p = new Pt();
            p.F = Vector3.Lerp(a.F, b.F, t);
            p.I = index;
            return p;
        }

        /// <summary>One channel of a vertex colour, interpolated by hand: not
        /// every Unity of this era carries Color32.Lerp.</summary>
        static byte Mix(byte a, byte b, float t)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(a + (b - a) * t), 0, 255);
        }

        static void Fan(List<Pt> poly, List<int> tris)
        {
            if (poly.Count < 3) return;
            for (int i = 1; i + 1 < poly.Count; i++)
            {
                tris.Add(poly[0].I);
                tris.Add(poly[i].I);
                tris.Add(poly[i + 1].I);
            }
        }

        // ------------------------------------------------------ the colliders

        /// <summary>
        /// Take the colliders out of the opening. A BoxCollider that crosses it
        /// is replaced by the pieces around the hole - left, right, under, over -
        /// so the block stays as solid as it was everywhere else. Anything that
        /// is not a box (a mesh hull that was not cut with its mesh, a capsule)
        /// is switched off and its own bounding box is put back in the same four
        /// pieces. Every change is remembered for `Restore`.
        /// </summary>
        static void Carve()
        {
            if (_block == null || _block.Root == null) return;
            Bounds hole = HoleBounds();
            Collider[] all = _block.Root.GetComponentsInChildren<Collider>(true);
            int split = 0, off = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (c is TerrainCollider) continue;
                if (_cutHulls.Contains(c as MeshCollider)) continue;   // already has the hole
                if (!c.bounds.Intersects(hole)) continue;

                BoxCollider box = c as BoxCollider;
                if (box != null && SplitBox(box)) { split++; continue; }

                Bounds world = c.bounds;
                c.enabled = false;
                _switchedOff.Add(c);
                off++;
                Rebuild(world);
            }
            if (split + off > 0)
                RevivalPlugin.L.LogInfo("TraitorVendor: " + split
                    + " collider(s) split around the opening, " + off
                    + " replaced by their own box.");
        }

        /// <summary>The volume the opening takes out of the wall, in world
        /// space.</summary>
        static Bounds HoleBounds()
        {
            float depth = Recess() + 0.4f;
            Vector3 centre = _block.Origin + _block.Normal * (0.1f - depth * 0.5f);
            Bounds b = new Bounds(centre, Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = _block.Origin
                    + _block.Right * ((i & 1) == 0 ? -_block.Width * 0.5f : _block.Width * 0.5f)
                    + _block.Up * ((i & 2) == 0 ? -_block.Height * 0.5f : _block.Height * 0.5f)
                    + _block.Normal * ((i & 4) == 0 ? 0.1f : 0.1f - depth);
                b.Encapsulate(corner);
            }
            return b;
        }

        /// <summary>Replace one box with the pieces of it that are not in the
        /// opening. False when the box is turned against the window, which is the
        /// one case this cannot do exactly - the caller then switches it off and
        /// rebuilds its bounds instead.</summary>
        static bool SplitBox(BoxCollider box)
        {
            Transform t = box.transform;
            // The window frame has to line up with the box's own axes, or the
            // four pieces would not be boxes at all.
            if (!Aligned(t, _block.Normal) || !Aligned(t, _block.Right)
                || !Aligned(t, _block.Up)) return false;

            Bounds hole;
            if (!HoleInLocal(t, out hole)) return false;

            Vector3 min = box.center - box.size * 0.5f;
            Vector3 max = box.center + box.size * 0.5f;
            Vector3 hmin = hole.min, hmax = hole.max;

            // Which local axis the opening goes INTO: the one the block's normal
            // lies along. The other two carry the four pieces.
            Vector3 local = t.InverseTransformDirection(_block.Normal);
            int d = 0;
            float bestAxis = Mathf.Abs(local.x);
            if (Mathf.Abs(local.y) > bestAxis) { d = 1; bestAxis = Mathf.Abs(local.y); }
            if (Mathf.Abs(local.z) > bestAxis) { d = 2; }
            int p = d == 0 ? 1 : 0;
            int q = d == 2 ? 1 : 2;

            List<Bounds> pieces = new List<Bounds>();
            Piece(pieces, min, max, q, Get(min, q), Mathf.Min(Get(max, q), Get(hmin, q)));
            Piece(pieces, min, max, q, Mathf.Max(Get(min, q), Get(hmax, q)), Get(max, q));
            Vector3 bandMin = min, bandMax = max;
            Set(ref bandMin, q, Mathf.Max(Get(min, q), Get(hmin, q)));
            Set(ref bandMax, q, Mathf.Min(Get(max, q), Get(hmax, q)));
            if (Get(bandMax, q) > Get(bandMin, q))
            {
                Piece(pieces, bandMin, bandMax, p, Get(min, p),
                      Mathf.Min(Get(max, p), Get(hmin, p)));
                Piece(pieces, bandMin, bandMax, p, Mathf.Max(Get(min, p), Get(hmax, p)),
                      Get(max, p));
            }

            box.enabled = false;
            _switchedOff.Add(box);
            for (int i = 0; i < pieces.Count; i++)
            {
                BoxCollider piece = box.gameObject.AddComponent<BoxCollider>();
                piece.center = pieces[i].center;
                piece.size = pieces[i].size;
                piece.sharedMaterial = box.sharedMaterial;
                _added.Add(piece);
            }
            return true;
        }

        static void Piece(List<Bounds> into, Vector3 min, Vector3 max, int axis,
                          float lo, float hi)
        {
            if (hi - lo <= 0.005f) return;
            Vector3 a = min, b = max;
            Set(ref a, axis, lo);
            Set(ref b, axis, hi);
            Vector3 size = b - a;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f) return;
            into.Add(new Bounds((a + b) * 0.5f, size));
        }

        static float Get(Vector3 v, int axis)
        {
            return axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        }

        static void Set(ref Vector3 v, int axis, float value)
        {
            if (axis == 0) v.x = value;
            else if (axis == 1) v.y = value;
            else v.z = value;
        }

        /// <summary>The opening's own volume in a collider's local space.</summary>
        static bool HoleInLocal(Transform t, out Bounds box)
        {
            box = new Bounds();
            float depth = Recess() + 0.4f;
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = _block.Origin
                    + _block.Right * ((i & 1) == 0 ? -_block.Width * 0.5f : _block.Width * 0.5f)
                    + _block.Up * ((i & 2) == 0 ? -_block.Height * 0.5f : _block.Height * 0.5f)
                    + _block.Normal * ((i & 4) == 0 ? 0.2f : -depth);
                Vector3 local = t.InverseTransformPoint(corner);
                if (!any) { box = new Bounds(local, Vector3.zero); any = true; }
                else box.Encapsulate(local);
            }
            return any;
        }

        static bool Aligned(Transform t, Vector3 world)
        {
            Vector3 local = t.InverseTransformDirection(world).normalized;
            float x = Mathf.Abs(local.x), y = Mathf.Abs(local.y), z = Mathf.Abs(local.z);
            return Mathf.Max(x, Mathf.Max(y, z)) > 0.985f;
        }

        /// <summary>A switched-off collider's bounding box, back in four pieces
        /// around the opening. Box colliders on a fresh object of our own, so the
        /// original object is left exactly as it was found.</summary>
        static void Rebuild(Bounds world)
        {
            GameObject holder = new GameObject("NDR_TraitorShopCollision");
            holder.transform.position = world.center;
            holder.transform.rotation = Quaternion.LookRotation(_block.Normal, _block.Up);
            holder.layer = _block.Root.gameObject.layer;

            // In the holder's own frame the block is an upright box and the
            // opening is centred on the wall, so the same four pieces apply.
            Vector3 size = Size(world, holder.transform);
            Vector3 hole = new Vector3(_block.Width, _block.Height, size.z + 1f);
            Vector3 mid = holder.transform.InverseTransformPoint(_block.Origin);
            Vector3 min = -size * 0.5f, max = size * 0.5f;
            Vector3 hmin = new Vector3(mid.x - hole.x * 0.5f, mid.y - hole.y * 0.5f, -1000f);
            Vector3 hmax = new Vector3(mid.x + hole.x * 0.5f, mid.y + hole.y * 0.5f, 1000f);

            List<Bounds> pieces = new List<Bounds>();
            Piece(pieces, min, max, 1, min.y, Mathf.Min(max.y, hmin.y));
            Piece(pieces, min, max, 1, Mathf.Max(min.y, hmax.y), max.y);
            Vector3 bandMin = min, bandMax = max;
            bandMin.y = Mathf.Max(min.y, hmin.y);
            bandMax.y = Mathf.Min(max.y, hmax.y);
            if (bandMax.y > bandMin.y)
            {
                Piece(pieces, bandMin, bandMax, 0, min.x, Mathf.Min(max.x, hmin.x));
                Piece(pieces, bandMin, bandMax, 0, Mathf.Max(min.x, hmax.x), max.x);
            }
            for (int i = 0; i < pieces.Count; i++)
            {
                BoxCollider piece = holder.AddComponent<BoxCollider>();
                piece.center = pieces[i].center;
                piece.size = pieces[i].size;
                _added.Add(piece);
            }
            if (_window != null) holder.transform.SetParent(_window.transform, true);
            else _holders.Add(holder);
        }

        static readonly List<GameObject> _holders = new List<GameObject>();

        /// <summary>A world box measured along another frame's axes.</summary>
        static Vector3 Size(Bounds world, Transform frame)
        {
            Vector3 e = world.extents;
            Vector3 x = frame.InverseTransformDirection(Vector3.right) * e.x;
            Vector3 y = frame.InverseTransformDirection(Vector3.up) * e.y;
            Vector3 z = frame.InverseTransformDirection(Vector3.forward) * e.z;
            return new Vector3(
                (Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x)) * 2f,
                (Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y)) * 2f,
                (Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f);
        }

        // ------------------------------------------------- the built geometry

        /// <summary>
        /// The inside of the opening. With a cut wall it is a RECESS: a lined
        /// box behind the hole, so one looks into a shop and not through the
        /// block (the far wall of a closed prop is drawn from outside only, and
        /// a bare hole would show the landscape behind it). Without a cut wall
        /// it is the same box turned inside out - a serving HATCH standing
        /// proud of the wall, with its back panel against the untouched wall.
        /// </summary>
        static void BuildLining(bool recessed)
        {
            MarkOpen();
            float w = _block.Width, h = _block.Height;
            // A recess may not reach out of the back of the block; a hatch may
            // stand as proud of it as the config says.
            float d = recessed
                ? Mathf.Min(Recess(), Mathf.Max(0.15f, _block.Depth * 2f - 0.12f))
                : Recess();
            float t = 0.04f;
            // Outward is +z in the window's own frame; a recess goes in, a hatch
            // comes out.
            float near = recessed ? 0.004f : d;
            float far = recessed ? -d : -0.004f;
            float mid = (near + far) * 0.5f;
            float span = Mathf.Abs(near - far);

            Material shell = Paint(Colour(0.55f));
            Material inside = Paint(Colour(0.30f));

            Panel("Back", new Vector3(0f, 0f, far - t * 0.5f),
                  new Vector3(w + t * 2f, h + t * 2f, t), inside);
            Panel("Counter", new Vector3(0f, -(h * 0.5f + t * 0.5f), mid),
                  new Vector3(w + t * 2f, t, span), shell);
            Panel("Head", new Vector3(0f, h * 0.5f + t * 0.5f, mid),
                  new Vector3(w + t * 2f, t, span), shell);
            Panel("CheekLeft", new Vector3(-(w * 0.5f + t * 0.5f), 0f, mid),
                  new Vector3(t, h, span), shell);
            Panel("CheekRight", new Vector3(w * 0.5f + t * 0.5f, 0f, mid),
                  new Vector3(t, h, span), shell);

            if (!recessed)
            {
                // A hatch is a thing bolted to a wall, so it gets the lip that
                // says so: a small roof over the counter.
                Panel("Roof", new Vector3(0f, h * 0.5f + t * 2.5f, d * 0.55f),
                      new Vector3(w + 0.3f, t, d * 1.3f), shell);

                // AND IT NEEDS ITS FOOT CLOSED. The man stands on the ground in
                // front of an uncut wall, so without this his legs are out in
                // the street under the counter. The apron is the base of the
                // stall: solid from the ground to the counter, the whole depth
                // of the hatch, which is what hides them.
                float foot = _block.FloorY - _block.Origin.y;      // below the sill
                float apron = -h * 0.5f;
                if (apron > foot + 0.05f)
                    Panel("Apron", new Vector3(0f, (foot + apron) * 0.5f, d * 0.5f),
                          new Vector3(w + t * 2f, apron - foot, d), shell);
            }
        }

        /// <summary>`rebuild` only: the block drawn again as a box, with the
        /// wall around the opening in four pieces. The original artwork is gone
        /// and the log says so - this is the way out for a block whose mesh
        /// cannot be cut and whose wall must nevertheless have a real hole in
        /// it.</summary>
        static void BuildShell()
        {
            MarkOpen();
            Vector3 mid = _block.Root.TransformPoint(_block.Local.center);
            Vector3 local = _window.transform.InverseTransformPoint(mid);
            float halfDepth = _block.Depth;
            float halfWide = _block.HalfWide;
            float halfHigh = _block.Local.size.y * Abs(_block.Root.lossyScale).y * 0.5f;
            float t = 0.08f;
            Material shell = Paint(Colour(1f));

            float top = local.y + halfHigh, bottom = local.y - halfHigh;
            float left = local.x - halfWide, right = local.x + halfWide;
            float front = 0f, back = -halfDepth * 2f;
            float deep = halfDepth * 2f;

            Panel("ShellBack", new Vector3(local.x, local.y, back),
                  new Vector3(halfWide * 2f, halfHigh * 2f, t), shell);
            Panel("ShellLeft", new Vector3(left, local.y, (front + back) * 0.5f),
                  new Vector3(t, halfHigh * 2f, deep), shell);
            Panel("ShellRight", new Vector3(right, local.y, (front + back) * 0.5f),
                  new Vector3(t, halfHigh * 2f, deep), shell);
            Panel("ShellRoof", new Vector3(local.x, top, (front + back) * 0.5f),
                  new Vector3(halfWide * 2f + 0.2f, t, deep + 0.2f), shell);
            Panel("ShellFloor", new Vector3(local.x, bottom, (front + back) * 0.5f),
                  new Vector3(halfWide * 2f, t, deep), shell);

            // The street side, in the four pieces around the window.
            float wl = -_block.Width * 0.5f, wr = _block.Width * 0.5f;
            float wb = -_block.Height * 0.5f, wt = _block.Height * 0.5f;
            Wall("ShellFaceUnder", left, right, bottom, wb, t, shell);
            Wall("ShellFaceOver", left, right, wt, top, t, shell);
            Wall("ShellFaceLeft", left, wl, wb, wt, t, shell);
            Wall("ShellFaceRight", wr, right, wb, wt, t, shell);
        }

        static void Wall(string name, float x0, float x1, float y0, float y1,
                         float t, Material paint)
        {
            if (x1 - x0 <= 0.01f || y1 - y0 <= 0.01f) return;
            Panel(name, new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, -t * 0.5f),
                  new Vector3(x1 - x0, y1 - y0, t), paint);
        }

        /// <summary>One box of the built geometry, in the window's own frame.
        /// No collider: the wall around it is what stops a player, and a
        /// collider in the opening is exactly what must not be there - the line
        /// from a player's eye to the trader has to be clear.</summary>
        static void Panel(string name, Vector3 centre, Vector3 size, Material paint)
        {
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f) return;
            GameObject go = new GameObject("NDR_" + name);
            go.transform.SetParent(_window.transform, false);
            go.transform.localPosition = centre;
            go.transform.localRotation = Quaternion.identity;
            go.layer = _block != null && _block.Root != null
                ? _block.Root.gameObject.layer : 0;
            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = BoxMesh(size);
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            if (paint != null) mr.sharedMaterial = paint;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        /// <summary>A box, six quads, UVs in metres so one material tiles the
        /// same way on every panel whatever its size. Winding: Unity draws the
        /// face whose vertices run clockwise on screen, which for an outward
        /// normal n and an in-plane right r means the up axis is Cross(r, n) and
        /// the corners run (-r,-u), (-r,+u), (+r,+u), (+r,-u).</summary>
        static Mesh BoxMesh(Vector3 size)
        {
            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> t = new List<int>();
            Vector3 h = size * 0.5f;
            Quad(v, n, uv, t, new Vector3(h.x, 0f, 0f), Vector3.right, Vector3.forward, size.z, size.y);
            Quad(v, n, uv, t, new Vector3(-h.x, 0f, 0f), Vector3.left, Vector3.back, size.z, size.y);
            Quad(v, n, uv, t, new Vector3(0f, h.y, 0f), Vector3.up, Vector3.right, size.x, size.z);
            Quad(v, n, uv, t, new Vector3(0f, -h.y, 0f), Vector3.down, Vector3.right, size.x, size.z);
            Quad(v, n, uv, t, new Vector3(0f, 0f, h.z), Vector3.forward, Vector3.left, size.x, size.y);
            Quad(v, n, uv, t, new Vector3(0f, 0f, -h.z), Vector3.back, Vector3.right, size.x, size.y);
            Mesh mesh = new Mesh();
            mesh.name = "NDR_ShopPanel";
            mesh.vertices = v.ToArray();
            mesh.normals = n.ToArray();
            mesh.uv = uv.ToArray();
            mesh.triangles = t.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void Quad(List<Vector3> v, List<Vector3> n, List<Vector2> uv,
                         List<int> t, Vector3 centre, Vector3 normal, Vector3 right,
                         float width, float height)
        {
            Vector3 up = Vector3.Cross(right, normal);
            Vector3 r = right * (width * 0.5f);
            Vector3 u = up * (height * 0.5f);
            int b = v.Count;
            v.Add(centre - r - u); v.Add(centre - r + u);
            v.Add(centre + r + u); v.Add(centre + r - u);
            for (int i = 0; i < 4; i++) n.Add(normal);
            uv.Add(new Vector2(0f, 0f));
            uv.Add(new Vector2(0f, height));
            uv.Add(new Vector2(width, height));
            uv.Add(new Vector2(width, 0f));
            t.Add(b); t.Add(b + 1); t.Add(b + 2);
            t.Add(b); t.Add(b + 2); t.Add(b + 3);
        }

        static Color Colour(float shade)
        {
            Color paint;
            if (_block == null || _block.Root == null || !Tint(_block.Root, out paint))
                paint = new Color(0.17f, 0.32f, 0.52f);   // the blue of the block
            return new Color(paint.r * shade, paint.g * shade, paint.b * shade, 1f);
        }

        /// <summary>A material on a shader the game itself uses. The same chain
        /// `ItemFactory.MakeMaterial` and the arena floor walk.</summary>
        static Material Paint(Color colour)
        {
            Shader shader = null;
            if (_block != null && _block.Root != null)
            {
                Renderer r = _block.Root.GetComponentInChildren<Renderer>(true);
                if (r != null && r.sharedMaterial != null) shader = r.sharedMaterial.shader;
            }
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: no usable shader - the "
                    + "opening is built without a material.");
                return null;
            }
            Material m = new Material(shader);
            m.name = "NDR_TraitorShop";
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", Grain());
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.18f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.05f);
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
            return m;
        }

        static Texture2D _grain;

        /// <summary>A small painted-metal texture, so a flat panel does not read
        /// as plastic. One metre per tile, which is what the UVs above hand it.
        /// </summary>
        static Texture2D Grain()
        {
            if (_grain != null) return _grain;
            int n = 32;
            Texture2D tex = new Texture2D(n, n);
            tex.name = "NDR_TraitorShopGrain";
            tex.wrapMode = TextureWrapMode.Repeat;
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float v = 0.86f + 0.14f * Mathf.PerlinNoise(x * 0.28f, y * 0.28f);
                    if (x == 0 || y == 0) v *= 0.82f;       // a seam every metre
                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
                    px[y * n + x] = new Color32(b, b, b, 255);
                }
            tex.SetPixels32(px);
            tex.Apply();
            _grain = tex;
            return tex;
        }

        static void HideBlock()
        {
            Renderer[] rs = _block.Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || !rs[i].enabled) continue;
                rs[i].enabled = false;
                _hiddenRenderers.Add(rs[i]);
            }
            LODGroup group = _block.Root.GetComponent<LODGroup>();
            if (group != null) group.enabled = false;
            RevivalPlugin.L.LogInfo("TraitorVendor: Mode is \"rebuild\", so \""
                + _block.Path + "\" is hidden (" + _hiddenRenderers.Count
                + " renderer(s)) and drawn again as a box with a window. The "
                + "block's own artwork is not on screen while this mode is on.");
        }

        /// <summary>Put the map back exactly as it was found.</summary>
        static void Restore()
        {
            try
            {
                for (int i = 0; i < _cutFilters.Count; i++)
                    if (_cutFilters[i] != null) _cutFilters[i].sharedMesh = _cutBefore[i];
                for (int i = 0; i < _cutHulls.Count; i++)
                    if (_cutHulls[i] != null) _cutHulls[i].sharedMesh = _hullBefore[i];
                for (int i = 0; i < _added.Count; i++)
                    if (_added[i] != null) UnityEngine.Object.Destroy(_added[i]);
                for (int i = 0; i < _switchedOff.Count; i++)
                    if (_switchedOff[i] != null) _switchedOff[i].enabled = true;
                for (int i = 0; i < _hiddenRenderers.Count; i++)
                    if (_hiddenRenderers[i] != null) _hiddenRenderers[i].enabled = true;
                if (_block != null && _block.Root != null)
                {
                    LODGroup group = _block.Root.GetComponent<LODGroup>();
                    if (group != null) group.enabled = true;
                }
                for (int i = 0; i < _holders.Count; i++)
                    if (_holders[i] != null) UnityEngine.Object.Destroy(_holders[i]);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: putting the block back - "
                    + ex.Message);
            }
            _cutFilters.Clear(); _cutBefore.Clear();
            _cutHulls.Clear(); _hullBefore.Clear();
            _added.Clear(); _switchedOff.Clear();
            _hiddenRenderers.Clear(); _holders.Clear();
            if (_window != null) UnityEngine.Object.Destroy(_window);
            _window = null;
            _opening = "";
        }

        // ============================================================== the man

        /// <summary>Where the trader stands and which way he looks. Pinned by
        /// config when the numbers are in it, otherwise measured from the block,
        /// otherwise - if the block is not there and the config allows it - the
        /// middle of the camp, so the settlement has a trader at all.</summary>
        static void Stand()
        {
            float px = _cfgStandX == null ? 0f : _cfgStandX.Value;
            float pz = _cfgStandZ == null ? 0f : _cfgStandZ.Value;
            if (px != 0f || pz != 0f)
            {
                float y;
                if (!RevivalTroopInsertion.GroundY(new Vector3(px, 0f, pz), out y))
                { Fail("no ground at the pinned spot " + Num(px) + ", " + Num(pz)); return; }
                float yaw = _cfgStandYaw == null ? -1f : _cfgStandYaw.Value;
                _stand = new Vector3(px, y, pz);
                _look = yaw >= 0f ? Quaternion.Euler(0f, yaw, 0f)
                                  : Quaternion.LookRotation(ToRoad(_stand), Vector3.up);
                _standKnown = true;
                return;
            }

            if (_block != null)
            {
                // BEHIND THE WALL ONLY WHEN THERE IS A HOLE IN IT. A cut wall
                // and a rebuilt one have one; a hatch is built around him on the
                // outside, and "none" or a refused cut leave a closed block, in
                // which a man would simply be invisible.
                float back = StandBack();
                bool inside = _opening == "cut" || _opening == "rebuild";
                Vector3 at = _block.Origin + _block.Normal * (inside ? -back : back);
                _stand = new Vector3(at.x, _block.FloorY, at.z);
                _look = Quaternion.LookRotation(_block.Normal, Vector3.up);
                _standKnown = true;
                if (!_standLogged)
                {
                    _standLogged = true;
                    RevivalPlugin.L.LogInfo("TraitorVendor: the trader's place is "
                        + Num(_stand.x) + ", " + Num(_stand.y) + ", " + Num(_stand.z)
                        + ", looking " + Num(Bearing(_block.Normal)) + " degrees. "
                        + "To have him there without walking to Litvinovka first, "
                        + "put StandX = " + Num(_stand.x) + ", StandZ = "
                        + Num(_stand.z) + ", StandYaw = " + Num(Bearing(_block.Normal))
                        + " into the config.");
                }
                return;
            }

            if (_cfgOutside == null || !_cfgOutside.Value) return;
            // No block: at least the settlement gets its trader, the way every
            // other settlement has one. Not before the search has actually run
            // and come back empty though - a village that is still loading must
            // not put him in the road and into the shop a minute later. A master
            // who is nowhere near Litvinovka cannot search at all, and then this
            // is the only place he can be put; if that master later walks there,
            // the block is found and the frame pass carries him into the shop.
            if (!_searchedInVain && !_playerFar) return;
            Vector3 centre = NewSettlement.Centre();
            float gy;
            if (!RevivalTroopInsertion.GroundY(centre, out gy)) return;
            _stand = new Vector3(centre.x, gy, centre.z);
            _look = Quaternion.LookRotation(ToRoad(_stand), Vector3.up);
            _standKnown = true;
        }

        /// <summary>
        /// MASTER ONLY. Build the trader: one man, one settlement, the game's own
        /// spawn chain. `Konfigurieren` is the only thing that makes him a
        /// storekeeper rather than another armed traitor, and it runs on his
        /// spawn point BEFORE `StartMainInit` reads it.
        /// </summary>
        static void Spawn()
        {
            _nextSpawn = Time.time + RetrySeconds;
            if (_shop != null)
            {
                Crew.Forget(_shop);
                UnityEngine.Object.Destroy(_shop);
                _shop = null;
                _vendor = null;
            }
            LookUpTrade();
            _tradeStale = false;       // he is built from what is known NOW
            try
            {
                Vector3[] where = new Vector3[] { _stand };
                _shop = Crew.DropCustomSquad(_stand, where, "traitor", null, ShopKey,
                    new Action<Component, int>(Konfigurieren));
            }
            catch (Exception ex)
            {
                Fail("the trader could not be built - " + ex.Message);
                return;
            }
            if (_shop == null)
            {
                Fail("Crew built nobody for the shop - the Crew lines above say why");
                return;
            }

            // The camp does not shoot its own trader, and its own trader raises
            // no alarm about the camp: the same in-place fix the camp uses.
            NewSettlement.Unhate(_shop);

            Array men = Crew.Men(_shop);
            _vendor = men == null || men.Length == 0 ? null
                    : men.GetValue(0) as Component;
            if (_vendor == null)
            {
                Fail("the shop settlement stands but holds no man");
                return;
            }

            int pattern = (int)Number(_vendor, "BehaviorPattern");
            _goneSince = 0f;
            _nextWatch = Time.time + WatchSeconds;
            RevivalPlugin.L.LogInfo("TraitorVendor: the trader stands at "
                + _stand.ToString("0.0") + " ("
                + (_opening.Length == 0 ? "no opening" : _opening)
                + "), behaviour " + pattern
                + (pattern == BehaviorStoreKeeper ? " = StoreKeeper"
                   : " - NOT a StoreKeeper, so he will not trade; the spawn point "
                     + "did not take the value")
                + ", shop " + (_tradeCopied ? "copied from " + _tradeFrom
                               : "on the fall-back ranks A-D at "
                                 + Num(_tradePercent * 100f) + " percent") + ".");
            if (!_announced)
            {
                _announced = true;
                Turret.Hinweis(Loc.T(
                    "В Литвиновке открылся торговец.",
                    "A trader has opened up in Litvinovka."), 5f);
            }
        }

        /// <summary>
        /// The one difference between this man and a crewman, written on his
        /// spawn point before the game builds him. Everything the trade needs is
        /// here: `InitSpawnNpc` hands `BehaviorPattern`, `StorageId`,
        /// `MarketItemRanksSelling` and `BuyPlayerItemsPercent` straight to
        /// `NPC_AI2.SetBehaviorPattern`, which is how every shipped storekeeper
        /// on the map is made.
        /// </summary>
        static void Konfigurieren(Component sp, int index)
        {
            try
            {
                SetEnum(sp, "BehaviorPattern", "StoreKeeper");
                // NPCType 0 is "Customizable" and would build a random body out
                // of this point's own appearance fields - a crewman's, which is
                // a soldier. A trader is a PREFAB: Kladovshik_NPC, the one the
                // runtime log names in the civilian settlement.
                if (_tradeNpcType > 0) SetNumber(sp, "NPCType", _tradeNpcType);
                else SetEnum(sp, "NPCType", "Kladovshik");

                if (_tradeStorage != null) Write(sp, "StorageId", _tradeStorage);
                object ranks = Ranks(sp);
                if (ranks != null) Write(sp, "MarketItemRanksSelling", ranks);
                SetNumber(sp, "BuyPlayerItemsPercent", _tradePercent);

                // A trader stands behind his counter. No walking, no guard post
                // of a template's, no loot to roll on a man who cannot be killed.
                Write(sp, "RandomWalkPoint", false);
                Write(sp, "CircleWalkPoint", false);
                Write(sp, "UseIndividualWalkPoints", false);
                // MainTask stays the 0 Crew.Punkt writes, and is deliberately
                // NOT copied off the civilian trader: a task that means "stand
                // at your GuardPoint" with no GuardPoint is a null reference
                // inside the game's own AI. He is held on his spot by the frame
                // pass instead, which needs no task at all.
                Write(sp, "GuardPoint", null);
                SetNumber(sp, "MainTask", 0f);
                SetNumber(sp, "RandomItemsCount", 0);
                Write(sp, "UseIndividualGodMode", true);
                Write(sp, "GodModeEnabled", true);
                // What he carries, but only when the man he is copied from
                // carries something a fixed id can name. A WeaponId of 0 would
                // send NetworkShowWeapon at a prefab that does not exist, and
                // the crew weapon Crew.UsableWeapon has already checked is a
                // better answer than that.
                if (_tradeWeaponId > 0)
                {
                    SetNumber(sp, "GrantWeaponType", _tradeGrantWeapon >= 0
                                                     ? _tradeGrantWeapon : 1f);
                    SetNumber(sp, "WeaponId", _tradeWeaponId);
                }

                // PlayerInteractingManager localizes this key before it draws the
                // name plate, and a key longer than five characters that is not
                // in the table is returned verbatim (Crew.Punkt, same reason).
                FieldInfo qf = AccessTools.Field(sp.GetType(), "Quests");
                object quests = qf == null ? null : qf.GetValue(sp);
                if (quests != null) Write(quests, "NameKey",
                    Loc.T("Торговец", "Trader"));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: the spawn point could "
                    + "not be made a trader's - " + ex.Message);
            }
        }

        /// <summary>
        /// Read the shop off the civilian settlement's own storekeeper. The
        /// spawn point is what carries it (`InitSpawnNpc` reads the four values
        /// from there), so that is what is searched for: a spawn point whose
        /// BehaviorPattern is StoreKeeper, preferring the settlement whose name
        /// says StoreKeeper or Peace - `[StoreKeeper]` and `[Peaces]` are the two
        /// the map has (RE 39).
        ///
        /// ASKED AGAIN UNTIL IT ANSWERS. The first spawn runs 25 s after a
        /// player exists, which can be before the civilian settlement's own
        /// spawn points are in the scene - the master is still loading, or he
        /// stands at Litvinovka with that chunk down. Only a hit is latched;
        /// a miss leaves `_tradeLooked` false, so `Spawn` and `Regrade` look
        /// once more. (A hit is kept across a scene change on purpose: this
        /// file only ever runs on the one map `NewSettlement.Here` allows, so
        /// what was copied there is what the same map holds again.)
        /// </summary>
        static void LookUpTrade()
        {
            if (_tradeLooked) return;
            if (Time.time < _nextLook) return;
            _nextLook = Time.time + LookSeconds;
            try
            {
                Type pType = RevivalPlugin.TypeByName("NPC_SpawnPoint");
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                if (pType == null) return;
                string pin = _cfgTraderFrom == null ? "" : _cfgTraderFrom.Value.Trim().ToLowerInvariant();

                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(pType);
                Component best = null;
                string bestName = "";
                int bestScore = -1;
                int traders = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    Component sp = all[i] as Component;
                    if (sp == null) continue;
                    if (sp.transform.parent != null
                        && sp.transform.parent.name == Crew.Name) continue;   // ours
                    if ((int)Number(sp, "BehaviorPattern") != BehaviorStoreKeeper) continue;
                    traders++;

                    string owner = "";
                    if (sType != null)
                    {
                        Component settlement = sp.GetComponentInParent(sType);
                        if (settlement != null) owner = settlement.gameObject.name;
                    }
                    if (owner.Length == 0) owner = sp.gameObject.name;
                    string lower = owner.ToLowerInvariant();
                    if (pin.Length > 0 && !Has(lower, pin)) continue;

                    int score = 0;
                    if (Has(lower, "storekeeper")) score += 3;
                    if (Has(lower, "peace")) score += 2;
                    if (pin.Length > 0) score += 4;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = sp;
                    bestName = owner;
                }

                if (best != null)
                {
                    _tradeLooked = true;          // the hit, and only the hit
                    _tradeStorage = Field(best, "StorageId");
                    _tradeRanks = Field(best, "MarketItemRanksSelling");
                    float percent = Number(best, "BuyPlayerItemsPercent");
                    if (percent > 0f) _tradePercent = percent;
                    _tradeNpcType = (int)Number(best, "NPCType");
                    _tradeGrantWeapon = (int)Number(best, "GrantWeaponType");
                    _tradeWeaponId = (int)Number(best, "WeaponId");
                    _tradeFrom = bestName;
                    _tradeCopied = true;
                    RevivalPlugin.L.LogInfo("TraitorVendor: the shop is copied from "
                        + "the trader of \"" + bestName + "\" - storage "
                        + (_tradeStorage == null ? "?" : _tradeStorage.ToString())
                        + ", " + Count(_tradeRanks) + " rank(s), "
                        + Num(_tradePercent * 100f) + " percent for what a player "
                        + "sells. " + traders + " storekeeper(s) on the map.");
                }
                else if (!_tradeWarned)
                {
                    // Once. The search itself is repeated every LookSeconds
                    // until it answers; the log is not.
                    _tradeWarned = true;
                    RevivalPlugin.L.LogWarning("TraitorVendor: no storekeeper spawn "
                        + "point is in the scene yet"
                        + (pin.Length > 0 ? " for CopyShopFrom = \""
                           + _cfgTraderFrom.Value + "\"" : "")
                        + " - until one turns up the traitors' trader falls back "
                        + "to ranks A-D and " + Num(_tradePercent * 100f)
                        + " percent. He still sells everything the marketplace "
                        + "table holds, including this plugin's own items. The "
                        + "search is repeated every " + Num(LookSeconds)
                        + " s and he is rebuilt with the real shop the moment it "
                        + "answers.");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: reading the civilian "
                    + "trader - " + ex.Message);
            }
        }

        /// <summary>A rank list of his own. Sharing the civilian trader's List
        /// object would let one storekeeper's sort reach the other; a fresh list
        /// with the same contents is what the game gives each of them.</summary>
        static object Ranks(Component sp)
        {
            try
            {
                FieldInfo fi = AccessTools.Field(sp.GetType(), "MarketItemRanksSelling");
                if (fi == null) return null;
                Type listType = fi.FieldType;
                if (!listType.IsGenericType) return null;
                object made = Activator.CreateInstance(listType);
                System.Collections.IList target = made as System.Collections.IList;
                if (target == null) return null;

                System.Collections.IList source = _tradeRanks as System.Collections.IList;
                if (source != null && source.Count > 0)
                {
                    for (int i = 0; i < source.Count; i++) target.Add(source[i]);
                    return made;
                }
                // No trader to copy: every rank, which is the whole shelf.
                Type[] args = listType.GetGenericArguments();
                if (args.Length == 1 && args[0].IsEnum)
                    for (int i = 0; i < 4; i++) target.Add(Enum.ToObject(args[0], i));
                return made;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: the rank list - " + ex.Message);
                return null;
            }
        }

        static int Count(object list)
        {
            System.Collections.IList l = list as System.Collections.IList;
            return l == null ? 0 : l.Count;
        }

        /// <summary>Is he still there? A trader cannot be killed
        /// (`NPC_AI2.ApplyDamage` branches past `DecreaseHealth` for a
        /// StoreKeeper, RE), so this only ever catches a scene change or a
        /// `Crew.StopAll`.</summary>
        static void Watch()
        {
            if (_shop != null && _vendor != null
                && _vendor.gameObject != null
                && _vendor.gameObject.activeInHierarchy)
            { _goneSince = 0f; return; }

            if (_goneSince <= 0f)
            {
                _goneSince = Time.time;
                RevivalPlugin.L.LogInfo("TraitorVendor: the trader is gone - "
                    + "building him again.");
            }
            if (_shop != null)
            {
                Crew.Forget(_shop);
                UnityEngine.Object.Destroy(_shop);
            }
            _shop = null;
            _vendor = null;
            _nextSpawn = 0f;
        }

        /// <summary>
        /// A trader who was built while the civilian settlement was not in the
        /// scene carries the fall-back shelf. The four values only ever reach a
        /// man through his spawn point in `Konfigurieren`, so there is no way to
        /// upgrade the one who already stands: the search is made again, and the
        /// moment it answers he is dropped and built once more - the same drop
        /// `Watch` makes when he is gone. Silent and free once the shop is
        /// copied and the man carries it, because `LookUpTrade` has latched by
        /// then and `_tradeStale` is false.
        /// </summary>
        static void Regrade()
        {
            if (!_tradeCopied)
            {
                LookUpTrade();
                if (!_tradeCopied) return;
                _tradeStale = true;         // the man standing there is older
                RevivalPlugin.L.LogInfo("TraitorVendor: the civilian trader of \""
                    + _tradeFrom + "\" is in the scene now - the traitors' trader "
                    + "is built again with that shop instead of the fall-back.");
            }
            if (!_tradeStale) return;

            // NOT IN THE MIDDLE OF A SALE. Destroying him while the game's talk
            // state is up closes the trade window in the customer's face; the
            // rebuild waits for the next pass. The same read `LateFrame` makes.
            if (Flag(_vendor, "IsTalkActive")) return;

            if (_shop != null)
            {
                Crew.Forget(_shop);
                UnityEngine.Object.Destroy(_shop);
            }
            _shop = null;
            _vendor = null;
            _goneSince = 0f;
            _nextSpawn = 0f;
        }

        static void Teardown()
        {
            Restore();
            if (_shop != null)
            {
                Crew.Forget(_shop);
                UnityEngine.Object.Destroy(_shop);
            }
            _shop = null;
            _vendor = null;
            _block = null;
            _standKnown = false;
            _standLogged = false;
        }

        // =========================================================== reflection

        static NavMeshAgent Agent(Component ai)
        {
            if (ai == null) return null;
            try
            {
                FieldInfo fi = AccessTools.Field(ai.GetType(), "_navMeshAgent");
                NavMeshAgent a = fi == null ? null : fi.GetValue(ai) as NavMeshAgent;
                return a != null ? a : ai.GetComponent<NavMeshAgent>();
            }
            catch { return ai.GetComponent<NavMeshAgent>(); }
        }

        static MethodInfo _mClearIntentions, _mPauseTime;
        static Type _mQuietOwner;

        /// <summary>Keep the vanilla idle logic off him: `IdleStateAction` queues
        /// its own intentions on every pass and returns early while the
        /// calculated pause is still running, so a short pause refreshed every
        /// frame is the whole hold - the same one the technical's crew and a
        /// posted artillery man use.</summary>
        static void Quiet(Component ai)
        {
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _mQuietOwner))
            {
                _mQuietOwner = t;
                _mClearIntentions = AccessTools.Method(t, "ClearIntentions", null, null);
                _mPauseTime = AccessTools.Method(t, "SetCalculatedPauseTime",
                                                 new Type[] { typeof(float) }, null);
                if (_mPauseTime == null)
                    _mPauseTime = AccessTools.Method(t, "SetPauseTime",
                                                     new Type[] { typeof(float) }, null);
            }
            try
            {
                if (_mClearIntentions != null) _mClearIntentions.Invoke(ai, null);
                if (_mPauseTime != null) _mPauseTime.Invoke(ai, new object[] { 1.4f });
            }
            catch { }
        }

        static object Field(object owner, string name)
        {
            if (owner == null) return null;
            FieldInfo fi = AccessTools.Field(owner.GetType(), name);
            try { return fi == null ? null : fi.GetValue(owner); }
            catch { return null; }
        }

        /// <summary>A bool field, false when it is not there. The same read
        /// NpcWar and Mortar use for IsTalkActive.</summary>
        static bool Flag(object owner, string name)
        {
            object v = Field(owner, name);
            return v is bool && (bool)v;
        }

        static float Number(object owner, string name)
        {
            object v = Field(owner, name);
            if (v == null) return -1f;
            try { return Convert.ToSingle(v); }
            catch { return -1f; }
        }

        static void Write(object owner, string name, object value)
        {
            if (owner == null) return;
            FieldInfo fi = AccessTools.Field(owner.GetType(), name);
            if (fi == null) return;
            try { fi.SetValue(owner, value); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: " + name + " - " + ex.Message);
            }
        }

        static void SetNumber(object owner, string name, float value)
        {
            if (owner == null) return;
            FieldInfo fi = AccessTools.Field(owner.GetType(), name);
            if (fi == null) return;
            try
            {
                Type t = fi.FieldType;
                if (t.IsEnum) fi.SetValue(owner, Enum.ToObject(t, (int)value));
                else if (t == typeof(float)) fi.SetValue(owner, value);
                else if (t == typeof(int)) fi.SetValue(owner, (int)value);
                else if (t == typeof(double)) fi.SetValue(owner, (double)value);
                else fi.SetValue(owner, Convert.ChangeType(value, t));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: " + name + " - " + ex.Message);
            }
        }

        static void SetEnum(object owner, string name, string value)
        {
            if (owner == null) return;
            FieldInfo fi = AccessTools.Field(owner.GetType(), name);
            if (fi == null || !fi.FieldType.IsEnum)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: enum field " + name
                    + " is not on " + (owner == null ? "?" : owner.GetType().Name)
                    + " - the trader may not be a trader.");
                return;
            }
            try { fi.SetValue(owner, Enum.Parse(fi.FieldType, value, true)); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorVendor: " + name + " = " + value
                    + " - " + ex.Message);
            }
        }

        static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Say it once, not once a second.</summary>
        static void Fail(string why)
        {
            if (why == _lastFail) return;
            _lastFail = why;
            RevivalPlugin.L.LogWarning("TraitorVendor: " + why + ".");
        }

        static void Note(string what)
        {
            if (what == _lastFail) return;
            _lastFail = what;
            RevivalPlugin.L.LogInfo("TraitorVendor: " + what + ".");
        }
    }
}

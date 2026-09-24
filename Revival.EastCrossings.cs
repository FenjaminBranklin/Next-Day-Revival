// Next Day: Survival - Revival Toolkit
//
// EAST CROSSINGS - the three saddle cuts into GW_Scene_1's east berm (S1
// Berezki rail gap, S2 Gorshovo pass, S3 Point N12 pass), so three roads run
// from the vanilla map over the seam onto the east tile. [World] EastCrossings
// is the ONE switch: on by default, but it only ever acts inside the east world
// ([World] EastTile, Revival.EastWorld.cs, off by default) - a cut with no
// ground east of it would be a ramp off the end of the world. The tile was
// built for the cut seam column ("ship both or neither", east-tile-design.md).
// Read once at start. Off = not one byte of the vanilla map is changed, and the
// tile's temporary seam walls close S1/S3 as before.
//
// What it does, per GW_Scene_1 load, all in memory (no asset file is written;
// a restart without the switch is the vanilla map):
//   1. heights  every saddle's sample box on ALL SEVEN TerrainData (763 draws,
//               764..767 draw trees, 768/769 collide - identical heights),
//               only where all of them hold the vanilla values (CrossingCore).
//               First from EastWorld.SpawnPrefix, i.e. BEFORE SpawnPlayer puts
//               anybody on the ground (it runs inside GW_Scene_1's Awake), so a
//               player saved in a cut is never spawned inside the old berm.
//   2. trees    the listed entries removed / re-seated on all seven.
//   3. props    the listed scene objects hidden or moved - both tunnels (S1
//               railway, S3 road) and their plug/gate go - and the Conductor
//               (the location-change NPC at the S3 tunnel mouth) is placed
//               beside the road; his arrival spawn points stay where they are.
//   4. paint    GWTerrain2's alphamap on the exposed cut faces and
//               embankments: scree and rock (the tile's own layers), and the
//               grass taken off the rock. Deterministic bytes, so every client
//               paints the same.
//   5. navmesh  the vanilla NavMesh is NOT rebuilt (an in-place update drops
//               every tile outside its bounds - CrossingCore): per saddle a
//               patch NavMesh on the cut ground, the shallow band of the cut
//               carved through (so the vanilla surface left hanging over the
//               cut is cut off), the high hanging parts carved away, and links
//               over the band. A patch is built again when a chunk with
//               colliders there streams in or out.
//   6. seam     the tile's temporary seam walls off (they stood where the cuts
//               now meet the tile), the tile's road and railway pieces on the
//               cut floors on (EastTileCutRoads, docs/ai/tasks/east-roads.md),
//               and per saddle one link from the patch to the tile's own NavMesh.
// Every step logs what it found against what the generator expected, so the
// in-world session reads the result from the log (prefix "EastCrossings:").
//
// Data: Revival.EastCrossingsData.cs (python research/east_crossings.py -emit).
// Report: docs/ai/tasks/east-crossings.md.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastCrossings
    {
        static ConfigEntry<bool> _cfg;
        /// <summary>The switch as read at start, AND the east world on.</summary>
        internal static bool On;

        // The seven TerrainData of GW_Scene_1 (REVERSE_ENGINEERING.md 37).
        static readonly string[] Names = {
            "GWTerrain2", "GWTerrain2_more__billboards_0", "GWTerrain2_more__billboards_1",
            "GWTerrain2_more__billboards_2", "GWTerrain2_more__bush_billboards_3",
            "GWTerrain2_two__billboards_0", "GWTerrain2_two__billboards_1" };

        /// <summary>One saddle at runtime: its data and its NavMesh state.</summary>
        sealed class Cut
        {
            internal CrossingSaddle D;
            internal short[] Old, New;
            internal byte[] Paint;              // records of 7 bytes (EastCrossingsData)
            internal byte[] Strength;           // per paint texel, row * AlphaW + col
            internal Bounds Box;                // the changed ground, 2 m wider
            internal NavMeshData Patch;
            internal NavMeshDataInstance PatchI;
            internal AsyncOperation Op;
            internal bool Again, RimDue, Linked, SeamChecked;
            internal float Started, CarvedAt;
            internal int Runs;
            internal Mesh Ground;
            internal readonly List<NavMeshLinkInstance> Rim = new List<NavMeshLinkInstance>();
            internal NavMeshLinkInstance Seam;
            internal readonly HashSet<string> Touching = new HashSet<string>();   // chunks with colliders at the cut
        }

        static Cut[] _cuts;
        static Dictionary<string, CrossingCore.TreePlan> _plans;   // all three saddles, per TerrainData name

        // per GW_Scene_1 load
        static int _load;                   // counts sceneLoaded(GW_Scene_1)
        static int _doneLoad = -1;          // the load the heights were handled for
        static int _waitLogged = -1;
        static bool _refused;
        static float _nextTry, _nextTrees, _groundAt, _errAt = -100f;
        static readonly Dictionary<int, int> _treeCounts = new Dictionary<int, int>();
        static readonly Dictionary<int, long> _painted = new Dictionary<int, long>();  // GWTerrain2 id * 4 + saddle -> byte sum
        static GameObject _carveRoot;
        static bool _wallOff;               // per tile load
        static bool _hooked;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfg = cfg.Bind("World", "EastCrossings", true,
                "East extension: the three saddle crossings (S1 Berezki rail gap, S2 Gorshovo pass, S3 Point N12 "
                + "pass) cut into GW_Scene_1's east berm in memory - heights of all seven terrains, the trees, "
                + "props and both tunnels on them, the ground paint, the NavMesh over them, the Conductor moved "
                + "beside the S3 road - and the tile's seam walls taken down. Only acts together with [World] "
                + "EastTile; every player in a session needs the same value. Read at start. Off = the vanilla "
                + "berm and the tile's seam walls.");
            On = _cfg.Value && EastWorld.On;
            if (!_cfg.Value && EastWorld.On)
                Log("[World] EastCrossings is off - the vanilla berm stays; the tile's seam walls close S1 and S3.");
        }

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastCrossings: " + s); }

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            CrossingCore.Log = Log;
            CrossingSaddle[] all = EastCrossingsData.Saddles;
            _cuts = new Cut[all.Length];
            List<string> rm = new List<string>(), rs = new List<string>();
            string s = "";
            for (int i = 0; i < all.Length; i++)
            {
                Cut c = new Cut();
                c.D = all[i];
                c.Old = CrossingCore.DecodeI16(c.D.OldHeights);
                c.New = CrossingCore.DecodeI16(c.D.NewHeights);
                c.Paint = Convert.FromBase64String(c.D.Paint);
                c.Strength = CrossingCore.PaintStrength(c.Paint, c.D.AlphaW, c.D.AlphaH);
                c.Box = PatchBox(c.D);
                _cuts[i] = c;
                rm.AddRange(c.D.TreeRemove);
                rs.AddRange(c.D.TreeReseat);
                s += (i > 0 ? "; " : "") + c.D.Name + " " + c.D.ChangedSamples + " samples, " + c.D.Props.Length
                     + " props, " + c.D.PaintTexels + " painted texels";
            }
            _plans = CrossingCore.ParseTrees(rm.ToArray(), rs.ToArray());
            Log("[World] EastCrossings is ON - " + s + "; trees on " + _plans.Count + " terrains; "
                + EastCrossingsData.Place.Length + " object(s) placed.");
            SceneManager.sceneLoaded += delegate(Scene sc, LoadSceneMode mode)
            {
                if (sc.name == MapScene.Home && mode == LoadSceneMode.Single)
                {
                    _load++;
                    DropNav("GW_Scene_1 loaded again");
                    _treeCounts.Clear();
                    for (int i = 0; i < _cuts.Length; i++) _cuts[i].Touching.Clear();
                }
                else if (sc.name.StartsWith(MapScene.Home + "_Chunk_"))
                {
                    Props(sc);
                    for (int i = 0; i < _cuts.Length; i++)
                    {
                        if (!Touches(sc, _cuts[i].Box)) continue;
                        _cuts[i].Touching.Add(sc.name);
                        if (_doneLoad == _load && !_refused) _cuts[i].Again = true;
                    }
                }
            };
            SceneManager.sceneUnloaded += delegate(Scene sc)
            {
                if (sc.name == EastWorld.SceneName) DropSeam("tile scene unloaded");
                else if (sc.name == MapScene.Home) DropNav("GW_Scene_1 unloaded");
                else
                    for (int i = 0; i < _cuts.Length; i++)
                        if (_cuts[i].Touching.Remove(sc.name) && _doneLoad == _load && !_refused) _cuts[i].Again = true;
            };
            // already loaded when the switch came on mid-session: count it as a load
            if (SceneManager.GetSceneByName(MapScene.Home).isLoaded) _load++;
        }

        /// <summary>From EastWorld.SpawnPrefix: the heights, before SpawnPlayer
        /// places anybody. SpawnPlayer runs inside GW_Scene_1's Awake - before
        /// sceneLoaded, before any Tick - and a player saved on a cut would
        /// otherwise be put down inside the old berm.</summary>
        internal static void BeforeSpawn()
        {
            if (!On) return;
            try
            {
                Hook();
                Dictionary<string, Td> all = Terrains();
                for (int i = 0; i < Names.Length; i++)
                    if (!all.ContainsKey(Names[i]))
                    {
                        Log("before the spawn: " + Names[i] + " not found yet - the heights follow in Tick.");
                        return;
                    }
                bool refused;
                Log("heights before the spawn: " + CutHeights(all, out refused));
            }
            catch (Exception ex)
            {
                Log("heights before the spawn failed, Tick tries again: " + ex.Message);
            }
        }

        internal static void Tick()
        {
            if (!On) return;
            try
            {
                Hook();
                Scene home = SceneManager.GetSceneByName(MapScene.Home);
                if (!home.isLoaded) return;
                if (_doneLoad != _load && Time.realtimeSinceStartup >= _nextTry) Apply();
                if (_doneLoad != _load || _refused) return;
                if (_groundAt > 0f && Time.realtimeSinceStartup >= _groundAt) { _groundAt = 0f; Ground("after the cut"); }
                if (Time.realtimeSinceStartup >= _nextTrees) { _nextTrees = Time.realtimeSinceStartup + 5f; Trees(false); }
                TickNav();
                TickSeam();
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup - _errAt > 10f) { _errAt = Time.realtimeSinceStartup; Log("tick error: " + ex); }
                _nextTry = Time.realtimeSinceStartup + 10f;
            }
        }

        // ------------------------------------------------------------- terrains

        sealed class Td
        {
            internal TerrainData Data;
            internal Terrain Terrain;           // null for the collider-only ones
            internal TerrainCollider Hull;
            internal Vector3 Org;
        }

        /// <summary>The seven TerrainData, found through the Terrain components
        /// AND the TerrainColliders (768/769 have no Terrain), by name.</summary>
        static Dictionary<string, Td> Terrains()
        {
            Dictionary<string, Td> found = new Dictionary<string, Td>();
            UnityEngine.Object[] ts = UnityEngine.Object.FindObjectsOfType(typeof(Terrain));
            for (int i = 0; i < ts.Length; i++)
            {
                Terrain t = ts[i] as Terrain;
                if (t == null || t.terrainData == null || t.gameObject.scene.name != MapScene.Home) continue;
                Td e;
                if (!found.TryGetValue(t.terrainData.name, out e))
                {
                    e = new Td(); e.Data = t.terrainData; e.Org = t.GetPosition();
                    found.Add(t.terrainData.name, e);
                }
                e.Terrain = t;
            }
            UnityEngine.Object[] cs = UnityEngine.Object.FindObjectsOfType(typeof(TerrainCollider));
            for (int i = 0; i < cs.Length; i++)
            {
                TerrainCollider c = cs[i] as TerrainCollider;
                if (c == null || c.terrainData == null || c.gameObject.scene.name != MapScene.Home) continue;
                Td e;
                if (!found.TryGetValue(c.terrainData.name, out e))
                {
                    e = new Td(); e.Data = c.terrainData; e.Org = c.transform.position;
                    found.Add(c.terrainData.name, e);
                }
                if (e.Hull == null || c.enabled) e.Hull = c;
            }
            return found;
        }

        // ------------------------------------------------------------- 1. heights

        /// <summary>All saddles on all seven: classified first (vanilla /
        /// already cut / foreign), and nothing is written unless every box on
        /// every terrain is vanilla or already cut. Returns the report line.</summary>
        static string CutHeights(Dictionary<string, Td> all, out bool refused)
        {
            refused = false;
            string report = "";
            int[,] state = new int[_cuts.Length, Names.Length];
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                int nOld = 0, nNew = 0;
                string foreign = "";
                for (int i = 0; i < Names.Length; i++)
                {
                    int changed, mo, mn;
                    state[k, i] = CrossingCore.ClassifyHeights(all[Names[i]].Data, c.D.Col0, c.D.Row0, c.D.Cols,
                                                               c.D.Rows, c.Old, c.New, out changed, out mo, out mn);
                    if (state[k, i] == CrossingCore.HeightsOld) nOld++;
                    else if (state[k, i] == CrossingCore.HeightsNew) nNew++;
                    else
                    {
                        refused = true;
                        foreign += " " + Names[i] + " (" + mo + " vanilla / " + mn + " cut of " + changed + ")";
                    }
                }
                report += (k > 0 ? "; " : "") + c.D.Key + ": " + nOld + " vanilla, " + nNew + " already cut"
                          + (foreign.Length > 0 ? ", FOREIGN" + foreign : "");
            }
            if (refused)
                return "REFUSED: a height box is neither the vanilla heights nor the cut - the game's terrain "
                       + "changed; nothing written. " + report;
            int written = 0, bad = 0;
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                for (int i = 0; i < Names.Length; i++)
                {
                    if (state[k, i] != CrossingCore.HeightsOld) continue;
                    bad += CrossingCore.WriteHeights(all[Names[i]].Data, c.D.Col0, c.D.Row0, c.D.Cols, c.D.Rows, c.New);
                    written++;
                }
            }
            if (written > 0)
                for (int i = 0; i < Names.Length; i++)
                {
                    Td e = all[Names[i]];
                    if (e.Terrain != null) e.Terrain.Flush();
                }
            return report + "; written " + written + " of " + (_cuts.Length * Names.Length) + " boxes, read back "
                   + (bad == 0 ? "exact" : bad + " sample(s) OFF") + ".";
        }

        static void Apply()
        {
            _nextTry = Time.realtimeSinceStartup + 3f;
            Dictionary<string, Td> all = Terrains();
            List<string> missing = new List<string>();
            for (int i = 0; i < Names.Length; i++) if (!all.ContainsKey(Names[i])) missing.Add(Names[i]);
            if (missing.Count > 0)
            {
                // the scene may still be activating; after 60 s say so, once per load
                if (Time.timeSinceLevelLoad > 60f && _waitLogged != _load)
                {
                    _waitLogged = _load;
                    Log("waiting for the GW_Scene_1 terrains, missing: " + string.Join(", ", missing.ToArray()));
                }
                return;
            }
            _doneLoad = _load;
            bool refused;
            Log("heights: " + CutHeights(all, out refused));
            _refused = refused;
            if (_refused) return;
            _groundAt = Time.realtimeSinceStartup + 2f;         // after physics has taken the new heights
            Trees(true);
            Paint(all);
            Props(SceneManager.GetSceneByName(MapScene.Home));
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || !s.name.StartsWith(MapScene.Home + "_Chunk_")) continue;
                Props(s);
                for (int k = 0; k < _cuts.Length; k++) if (Touches(s, _cuts[k].Box)) _cuts[k].Touching.Add(s.name);
            }
            for (int k = 0; k < _cuts.Length; k++) _cuts[k].Again = true;
        }

        /// <summary>Ray down onto the collision terrain on each road centreline:
        /// proves the colliders took the new heights, not only the data.</summary>
        static void Ground(string when)
        {
            for (int k = 0; k < _cuts.Length; k++)
            {
                float[] rc = _cuts[k].D.RoadChecks;
                string s = "";
                int good = 0, n = 0;
                for (int i = 0; i + 3 < rc.Length; i += 4)
                {
                    RaycastHit hit;
                    Vector3 from = new Vector3(rc[i], 1500f, rc[i + 1]);
                    bool ok = Physics.Raycast(from, Vector3.down, out hit, 2000f, Physics.DefaultRaycastLayers,
                                              QueryTriggerInteraction.Ignore);
                    n++;
                    if (ok && Mathf.Abs(hit.point.y - rc[i + 3]) < 0.3f) good++;
                    s += "; x " + rc[i].ToString("F0") + " ray " + (ok ? hit.point.y.ToString("F2") + " on "
                         + hit.collider.name : "NOTHING") + " (cut " + rc[i + 3].ToString("F2") + ", vanilla "
                         + rc[i + 2].ToString("F2") + ")";
                }
                Log("ground " + when + ", " + _cuts[k].D.Key + ": " + good + "/" + n + " on the cut" + s + ".");
            }
        }

        // ------------------------------------------------------------- 2. trees

        /// <summary>All seven lists. Re-run when a list's count changes (RoadClear
        /// and Helipads rewrite the same arrays; the plan is idempotent).</summary>
        static void Trees(bool first)
        {
            Dictionary<string, Td> all = Terrains();
            string s = "";
            foreach (KeyValuePair<string, CrossingCore.TreePlan> p in _plans)
            {
                Td e;
                if (!all.TryGetValue(p.Key, out e)) { s += p.Key + " MISSING; "; continue; }
                int id = e.Data.GetInstanceID(), known;
                if (!first && _treeCounts.TryGetValue(id, out known) && known == e.Data.treeInstanceCount) continue;
                int removed, reseated;
                float err;
                CrossingCore.ApplyTrees(e.Data, e.Org, p.Value, out removed, out reseated, out err);
                _treeCounts[id] = e.Data.treeInstanceCount;
                if (removed > 0 || reseated > 0 || first)
                {
                    if (e.Terrain != null) e.Terrain.Flush();
                    if (e.Hull != null && e.Hull.enabled) { e.Hull.enabled = false; e.Hull.enabled = true; }
                    s += p.Key + " removed " + removed + "/" + p.Value.Remove.Count + " re-seated " + reseated + "/"
                         + p.Value.Reseat.Count + " (onto the terrain's own height; plan estimate off by up to "
                         + err.ToString("F2") + " m); ";
                }
            }
            if (s.Length > 0) Log("trees" + (first ? "" : " (a list changed, filtered again)") + ": " + s);
        }

        // ------------------------------------------------------------- 3. props

        /// <summary>The listed objects of one scene: found by path (first
        /// segment = root) at the listed position, else by name and position
        /// anywhere under that root. Hidden, moved with the ground, or placed.</summary>
        static void Props(Scene scene)
        {
            if (!scene.isLoaded || _doneLoad != _load || _refused) return;
            int done = 0, want = 0;
            GameObject[] roots = null;
            for (int k = 0; k < _cuts.Length; k++)
            {
                string[] props = _cuts[k].D.Props;
                for (int j = 0; j < props.Length; j++)
                {
                    string[] f = props[j].Split('\t');
                    if (f[0] != scene.name) continue;
                    want++;
                    if (roots == null) roots = scene.GetRootGameObjects();
                    Vector3 pos = new Vector3(CrossingCore.Parse(f[2]), CrossingCore.Parse(f[3]), CrossingCore.Parse(f[4]));
                    Transform t = FindAt(roots, f[1], pos, 0.25f);
                    if (t == null) continue;
                    if (f[5] == "hide") { if (t.gameObject.activeSelf) t.gameObject.SetActive(false); }
                    else t.position = pos + Vector3.up * CrossingCore.Parse(f[6]);
                    done++;
                }
            }
            string placed = "";
            for (int j = 0; j < EastCrossingsData.Place.Length; j++)
            {
                string[] f = EastCrossingsData.Place[j].Split('\t');
                if (f[0] != scene.name) continue;
                want++;
                if (roots == null) roots = scene.GetRootGameObjects();
                Vector3 from = V3(f[2]), to = V3(f[3]);
                Transform t = FindAt(roots, f[1], to, 0.5f);
                if (t != null) { done++; continue; }                   // already there (this load)
                t = FindAt(roots, f[1], from, 0.5f);
                if (t == null) { placed += " " + Leaf(f[1]) + " NOT FOUND at " + from + ";"; continue; }
                Place(t, to, CrossingCore.Parse(f[4]), f[5]);
                done++;
                placed += " " + Leaf(f[1]) + " " + from + " -> " + to + (f[5].Length > 0 ? " (" + f[5] + " stay)" : "") + ";";
            }
            if (want > 0) Log("props in " + scene.name + ": " + done + "/" + want + " handled." + placed);
        }

        /// <summary>LocationChangeTrigger.Start registers its map marker from
        /// the trigger transform before the ordinary sceneLoaded/Tick pass can
        /// move the Conductor. Put that one authored object at its destination
        /// first, while keeping its arrival SpawnPoints at their old positions.</summary>
        internal static void BeforeLocationMarker(object instance)
        {
            if (!On) return;
            Component trigger = instance as Component;
            if (trigger == null) return;
            for (int j = 0; j < EastCrossingsData.Place.Length; j++)
            {
                string[] f = EastCrossingsData.Place[j].Split('\t');
                if (f.Length < 6 || f[0] != MapScene.Home
                    || trigger.transform.name != Leaf(f[1])) continue;
                Vector3 from = V3(f[2]), to = V3(f[3]);
                if ((trigger.transform.position - to).sqrMagnitude <= 0.25f) return;
                if ((trigger.transform.position - from).sqrMagnitude > 0.25f) continue;
                Place(trigger.transform, to, CrossingCore.Parse(f[4]), f[5]);
                Log("location marker moved before Start: " + from + " -> " + to + ".");
                return;
            }
        }

        static Vector3 V3(string s)
        {
            string[] v = s.Split(' ');
            return new Vector3(CrossingCore.Parse(v[0]), CrossingCore.Parse(v[1]), CrossingCore.Parse(v[2]));
        }

        static string Leaf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        static Transform FindAt(GameObject[] roots, string path, Vector3 pos, float tol)
        {
            int slash = path.IndexOf('/');
            string root = slash < 0 ? path : path.Substring(0, slash);
            string leaf = Leaf(path);
            for (int r = 0; r < roots.Length; r++)
            {
                if (roots[r].name != root) continue;
                Transform t = slash < 0 ? roots[r].transform : roots[r].transform.Find(path.Substring(slash + 1));
                if (t != null && (t.position - pos).sqrMagnitude <= tol * tol) return t;
                Transform[] all = roots[r].GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i].name == leaf && (all[i].position - pos).sqrMagnitude <= tol * tol) return all[i];
            }
            return null;
        }

        /// <summary>Puts t at `to` (yaw in degrees, below 0 = keep its
        /// rotation); the named children keep their world position and
        /// rotation - the Conductor's arrival spawn points.</summary>
        static void Place(Transform t, Vector3 to, float yaw, string pin)
        {
            List<Transform> kids = new List<Transform>();
            List<Vector3> kp = new List<Vector3>();
            List<Quaternion> kr = new List<Quaternion>();
            if (pin.Length > 0)
                foreach (string n in pin.Split(','))
                {
                    Transform k = t.Find(n);
                    if (k == null) continue;
                    kids.Add(k); kp.Add(k.position); kr.Add(k.rotation);
                }
            t.position = to;
            if (yaw >= 0f) t.rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int i = 0; i < kids.Count; i++) { kids[i].position = kp[i]; kids[i].rotation = kr[i]; }
        }

        // ------------------------------------------------------------- 4. paint

        /// <summary>GWTerrain2's alphamap on the cut faces, and the grass taken
        /// off where they turned to rock. A box is painted when it does not hold
        /// what this session painted into it (a reloaded TerrainData is vanilla
        /// again); the heights' classification has already refused a changed
        /// game file.</summary>
        static void Paint(Dictionary<string, Td> all)
        {
            TerrainData g = all[Names[0]].Data;
            if (g.alphamapLayers != EastCrossingsData.PaintLayerCount || g.alphamapWidth != EastCrossingsData.AlphaRes)
            {
                Log("paint: GWTerrain2 has " + g.alphamapLayers + " layers at " + g.alphamapWidth + ", expected "
                    + EastCrossingsData.PaintLayerCount + " at " + EastCrossingsData.AlphaRes + " - not painted.");
                return;
            }
            string s = "";
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                int key = g.GetInstanceID() * 4 + k;
                long had;
                long now = CrossingCore.AlphaSum(g, c.D.AlphaX0, c.D.AlphaZ0, c.D.AlphaW, c.D.AlphaH);
                if (_painted.TryGetValue(key, out had) && had == now) { s += c.D.Key + " already painted; "; continue; }
                int off;
                long sum = CrossingCore.PaintAlpha(g, c.D.AlphaX0, c.D.AlphaZ0, c.D.AlphaW, c.D.AlphaH, c.Paint,
                                                   EastCrossingsData.PaintLayers, out off);
                _painted[key] = sum;
                s += c.D.Key + " " + c.D.PaintTexels + " texels, read back " + (off == 0 ? "exact" : off + " OFF") + "; ";
            }
            int cleared = 0;
            for (int i = 0; i < Names.Length; i++)
            {
                Td e = all[Names[i]];
                for (int k = 0; k < _cuts.Length; k++)
                    cleared += CrossingCore.ClearGrass(e.Data, e.Org, _cuts[k].D.AlphaX0, _cuts[k].D.AlphaZ0,
                                                       _cuts[k].D.AlphaW, _cuts[k].D.AlphaH, _cuts[k].Strength,
                                                       EastCrossingsData.AlphaRes, 128);
            }
            Log("paint: " + s + "grass cells cleared on the rock: " + cleared + ".");
        }

        // ------------------------------------------------------------- 5. navmesh

        /// <summary>The changed ground: the height box, 2 m wider on each side.</summary>
        static Bounds PatchBox(CrossingSaddle d)
        {
            float sp = CrossingCore.Spacing;
            float x0 = -2500f + d.Col0 * sp, z0 = -2500f + d.Row0 * sp;
            float w = (d.Cols - 1) * sp, dd = (d.Rows - 1) * sp;
            return new Bounds(new Vector3(x0 + w / 2f, 500f, z0 + dd / 2f), new Vector3(w + 4f, 300f, dd + 4f));
        }

        static bool Touches(Scene s, Bounds patch)
        {
            Bounds box = patch;
            box.Expand(new Vector3(4f, 0f, 4f));
            GameObject[] roots = s.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                Collider[] cs = roots[r].GetComponentsInChildren<Collider>(false);
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] != null && cs[i].enabled && !cs[i].isTrigger && cs[i].bounds.Intersects(box)) return true;
            }
            return false;
        }

        /// <summary>What is baked: scenery of GW_Scene_1 and its chunks. Not
        /// players, NPCs, vehicles or loose physics props, not triggers, not
        /// the tile or anything outside the map scenes.</summary>
        static bool Bakeable(Component c)
        {
            if (c == null) return false;
            string scene = c.gameObject.scene.name;
            if (scene != MapScene.Home && !scene.StartsWith(MapScene.Home + "_Chunk_")) return false;
            Collider col = c as Collider;
            if (col != null && col.isTrigger) return false;
            if (c.GetComponentInParent<Rigidbody>() != null) return false;
            if (c.GetComponentInParent<CharacterController>() != null) return false;
            if (c.GetComponentInParent<NavMeshAgent>() != null) return false;
            return true;
        }

        /// <summary>Per saddle: patch built -> (once per load, all saddles) carve
        /// boxes -> 1.5 s later, when the carving has settled, the rim links and
        /// the checks; the seam link follows in TickSeam.</summary>
        static void TickNav()
        {
            bool allBuilt = true;
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                if (c.Op != null)
                {
                    if (!c.Op.isDone) { allBuilt = false; continue; }
                    c.Op = null;
                    Log("navmesh " + c.D.Key + ": patch built on area " + CrossingCore.PatchArea + " (run " + c.Runs + ", "
                        + (Time.realtimeSinceStartup - c.Started).ToString("F1") + " s).");
                    c.CarvedAt = Time.realtimeSinceStartup;
                    c.RimDue = true;
                }
                if (c.Patch == null) allBuilt = false;
            }
            if (allBuilt && _carveRoot == null)
            {
                _carveRoot = new GameObject("EastCrossingCarve");
                SceneManager.MoveGameObjectToScene(_carveRoot, SceneManager.GetSceneByName(MapScene.Home));
                string s = "";
                for (int k = 0; k < _cuts.Length; k++)
                {
                    Cut c = _cuts[k];
                    int nb = CrossingCore.Carve(CrossingCore.BandBoxes(c.Old, c.New, c.D.Col0, c.D.Row0, c.D.Cols, c.D.Rows),
                                                _carveRoot.transform, c.D.Key + " band");
                    int nd = CrossingCore.Carve(CrossingCore.DeepBoxes(c.Old, c.New, c.D.Col0, c.D.Row0, c.D.Cols, c.D.Rows),
                                                _carveRoot.transform, c.D.Key + " deep");
                    s += (k > 0 ? "; " : "") + c.D.Key + " " + nb + " band / " + nd + " deep";
                    c.CarvedAt = Time.realtimeSinceStartup;
                    c.RimDue = true;
                }
                Log("navmesh: carve boxes " + s + " (band = cut under " + CrossingCore.BandMax + " m, carved through; "
                    + "deep = vanilla surface more than " + CrossingCore.DeepClear + " m up).");
            }
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                if (c.RimDue && _carveRoot != null && Time.realtimeSinceStartup - c.CarvedAt > 1.5f)
                {
                    c.RimDue = false;
                    for (int i = 0; i < c.Rim.Count; i++) if (c.Rim[i].valid) c.Rim[i].Remove();
                    c.Rim.Clear();
                    int cand;
                    c.Rim.AddRange(CrossingCore.BandLinks(c.Old, c.New, c.D.Col0, c.D.Row0, c.D.Cols, c.D.Rows, 8, 10f, 3f,
                                                          out cand));
                    Log("navmesh " + c.D.Key + ": " + c.Rim.Count + " band links (of " + cand + " candidates) from the "
                        + "untouched vanilla surface over the band to the patch.");
                    NavChecks(c);
                }
                if (!c.Again || c.Op != null) continue;
                c.Again = false;
                Bounds collect = c.Box;
                collect.Expand(new Vector3(4f, 0f, 4f));
                int dropped;
                if (c.Ground == null) c.Ground = CrossingCore.GroundMesh(c.Old, c.New, c.D.Col0, c.D.Row0, c.D.Cols, c.D.Rows);
                List<NavMeshBuildSource> src = CrossingCore.PatchSources(c.Ground,
                                                                         CrossingCore.Sources(collect, Bakeable, out dropped));
                c.Runs++;
                c.Started = Time.realtimeSinceStartup;
                if (c.Patch == null) c.Patch = CrossingCore.Patch(src, c.Box, out c.PatchI, out c.Op);
                else c.Op = CrossingCore.Repatch(c.Patch, src, c.Box);
                Log("navmesh " + c.D.Key + ": building the patch over " + c.Box.min.x.ToString("F0") + ".."
                    + c.Box.max.x.ToString("F0") + " x " + c.Box.min.z.ToString("F0") + ".." + c.Box.max.z.ToString("F0")
                    + " from the cut's ground mesh (" + c.Ground.vertexCount / 4 + " quads) and " + (src.Count - 1)
                    + " colliders (" + dropped + " dropped as dynamic/foreign); the vanilla NavMesh is not rebuilt.");
            }
        }

        /// <summary>On the road centreline: the patch on the new ground, no
        /// vanilla surface left at the old height where the cut is deep, and a
        /// path in from untouched vanilla ground.</summary>
        static void NavChecks(Cut c)
        {
            float[] rc = c.D.RoadChecks;
            string s = "";
            int good = 0, n = 0;
            for (int i = 0; i + 3 < rc.Length; i += 4)
            {
                if (rc[i + 2] - rc[i + 3] < CrossingCore.BandMax)
                {
                    // the band: carved through on purpose, crossed by the band links
                    s += "; x " + rc[i].ToString("F0") + " shallow (" + (rc[i + 2] - rc[i + 3]).ToString("F1") + " m), band";
                    continue;
                }
                Vector3 hit;
                bool found = CrossingCore.NavAt(new Vector3(rc[i], rc[i + 3] + 0.5f, rc[i + 1]), 2f,
                                                CrossingCore.PatchMask, out hit);
                bool ok = found && Mathf.Abs(hit.y - rc[i + 3]) < 1f;
                string old = "";
                if (rc[i + 2] - rc[i + 3] > CrossingCore.DeepClear + 1f)
                {
                    Vector3 h2;
                    bool hang = CrossingCore.NavAt(new Vector3(rc[i], rc[i + 2] + 0.3f, rc[i + 1]), 0.8f,
                                                   CrossingCore.VanillaMask, out h2) && h2.y > rc[i + 3] + 1f;
                    old = hang ? ", vanilla STILL at the old height " + h2.y.ToString("F1") : ", vanilla carved";
                    ok &= !hang;
                }
                n++;
                if (ok) good++;
                s += "; x " + rc[i].ToString("F0") + (found ? " patch at " + hit.y.ToString("F2") : " NO patch")
                     + " (cut " + rc[i + 3].ToString("F2") + ")" + old;
            }
            Log("navmesh checks " + c.D.Key + " " + good + "/" + n + " good" + s + ".");
            float[] w = c.D.PathWest;
            float zc = c.D.SeamZ, yo, yi;
            Vector3 outer;
            if (EastWorld.TerrainHeight(new Vector3(w[0], 0f, w[1]), out yo)
                && CrossingCore.NavAt(new Vector3(w[0], yo, w[1]), 30f, CrossingCore.VanillaMask, out outer)
                && EastWorld.TerrainHeight(new Vector3(2490f, 0f, zc), out yi))
                Log("navmesh path " + c.D.Key + " untouched vanilla " + outer + " -> cut road x 2490: "
                    + CrossingCore.PathCheck(outer, new Vector3(2490f, yi, zc)) + "; back: "
                    + CrossingCore.PathCheck(new Vector3(2490f, yi, zc), outer) + ".");
            c.Linked = false;            // re-link and re-check the path after every patch
            c.SeamChecked = false;
        }

        /// <summary>Everything this class added to the NavMesh, gone - the added
        /// data is not bound to a scene, so it would outlive GW_Scene_1.</summary>
        static void DropNav(string why)
        {
            if (_cuts == null) return;
            bool any = false;
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                any |= c.PatchI.valid || c.Rim.Count > 0 || c.Seam.valid;
                if (c.PatchI.valid) c.PatchI.Remove();
                for (int i = 0; i < c.Rim.Count; i++) if (c.Rim[i].valid) c.Rim[i].Remove();
                c.Rim.Clear();
                if (c.Seam.valid) c.Seam.Remove();
                c.Patch = null;
                c.Ground = null;
                c.Op = null;
                c.Runs = 0;
                c.RimDue = false;
                c.Again = false;
                c.Linked = false;
                c.SeamChecked = false;
            }
            _carveRoot = null;              // a child of GW_Scene_1: gone with it
            if (any) Log("navmesh: patches and links removed (" + why + ").");
        }

        // ------------------------------------------------------------- 6. seam

        static void TickSeam()
        {
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) return;
            if (!_wallOff)
            {
                _wallOff = true;
                GameObject[] roots = tile.GetRootGameObjects();
                string s = "no EastTileSeamWall in the tile scene";
                for (int r = 0; r < roots.Length; r++)
                {
                    Transform w = roots[r].transform.Find("EastTileSeamWall");
                    if (w == null) continue;
                    w.gameObject.SetActive(false);
                    s = "the tile's seam wall (" + w.childCount + " strip(s)) switched off - the cuts meet the tile";
                }
                Log("seam: " + s + ".");
                // The road and railway pieces the tile carries for GW_Scene_1's
                // side of the seam lie on the CUT floor (x < 2500): shown only
                // now, with the cuts in (docs/ai/tasks/east-roads.md).
                string cr = "no EastTileCutRoads in the tile scene (a tile built before the roads)";
                for (int r = 0; r < roots.Length; r++)
                {
                    Transform g = roots[r].transform.Find("EastTileCutRoads");
                    if (g == null) continue;
                    g.gameObject.SetActive(true);
                    cr = g.childCount + " road/rail piece(s) in the cuts switched on";
                }
                Log("seam: " + cr + ".");
            }
            for (int k = 0; k < _cuts.Length; k++)
            {
                Cut c = _cuts[k];
                if (c.Op != null || c.Again || c.RimDue || c.Patch == null || _carveRoot == null) continue;
                float zc = c.D.SeamZ;
                if (!c.Linked)
                {
                    c.Linked = true;
                    if (c.Seam.valid) c.Seam.Remove();
                    Vector3 a, b = new Vector3(2503f, 0f, zc);
                    float yb;
                    bool west = CrossingCore.SeamWest(2497f, zc, 40f, delegate(float x)
                    {
                        float y;
                        return EastWorld.TerrainHeight(new Vector3(x, 0f, zc), out y) ? y : 0f;
                    }, out a);
                    if (!west || !EastWorld.TerrainHeight(b, out yb))
                    {
                        Log("seam " + c.D.Key + ": " + (west ? "no tile terrain east of the seam" : "no patch within 40 m "
                            + "west of the seam") + " at z " + zc.ToString("F1") + " - not linked.");
                        continue;
                    }
                    b.y = yb;
                    c.Seam = CrossingCore.Link(a, b, c.D.SeamWidth);
                    Log("seam " + c.D.Key + ": link " + a + " -> " + b + ", width " + c.D.SeamWidth.ToString("F0") + " m, "
                        + (c.Seam.valid ? "added" : "REFUSED by the NavMesh") + ".");
                }
                if (!c.SeamChecked)
                {
                    c.SeamChecked = true;
                    SeamChecks(c);
                }
            }
        }

        static void SeamChecks(Cut c)
        {
            // heights across the seam on the road: west side the cut, east side the tile
            float zc = c.D.SeamZ, worst = 0f;
            for (float dz = -20f; dz <= 20.01f; dz += 5f)
            {
                float yw, ye;
                if (!EastWorld.TerrainHeight(new Vector3(2499.95f, 0f, zc + dz), out yw)) continue;
                if (!EastWorld.TerrainHeight(new Vector3(2500.05f, 0f, zc + dz), out ye)) continue;
                worst = Mathf.Max(worst, Mathf.Abs(ye - yw));
            }
            float[] w = c.D.PathWest, e = c.D.PathEast;
            float y1, y2;
            EastWorld.TerrainHeight(new Vector3(w[0], 0f, w[1]), out y1);
            EastWorld.TerrainHeight(new Vector3(e[0], 0f, e[1]), out y2);
            Vector3 a = new Vector3(w[0], y1, w[1]), b = new Vector3(e[0], y2, e[1]), snapped;
            if (CrossingCore.NavAt(a, 30f, CrossingCore.VanillaMask, out snapped)) a = snapped;
            Log("seam " + c.D.Key + ": largest step across x 2500 on the road " + worst.ToString("F2") + " m; path west -> "
                + "east " + CrossingCore.PathCheck(a, b) + "; east -> west " + CrossingCore.PathCheck(b, a) + ".");
        }

        static void DropSeam(string why)
        {
            _wallOff = false;
            if (_cuts == null) return;
            for (int k = 0; k < _cuts.Length; k++)
            {
                if (_cuts[k].Seam.valid) _cuts[k].Seam.Remove();
                _cuts[k].Linked = false;
                _cuts[k].SeamChecked = false;
            }
            Log("seam: links removed (" + why + ").");
        }
    }
}

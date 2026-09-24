// Next Day: Survival - Revival Toolkit
//
// EAST CROSSINGS, the engine half. Pure UnityEngine calls - no BepInEx, no
// Harmony, no game types - so the very same file also compiles in the Unity
// 2018.1.0f2 test project (unity/EastCrossingsTest) that proves it offline.
// The plugin half, which decides WHEN and on WHAT, is Revival.EastCrossings.cs;
// the data is generated (Revival.EastCrossingsData.cs); the inventory and the
// reasons are docs/ai/tasks/east-crossings.md.
//
// Heights. GW_Scene_1 has seven TerrainData (763 GWTerrain2 draws the ground,
// 764..767 draw only trees, 768/769 only collide) and all seven hold the same
// heights. ApplyHeights writes the NEW native values of one sample box only
// where it reads the OLD ones: a second call is a no-op, and a changed game
// file is refused instead of cut twice. Unity stores native n as n / 32766;
// (n + 0.25) / 32766 gives n back whether SetHeights rounds or truncates.
//
// Trees. A tree is an entry of TerrainData.treeInstances with a stored y
// (fraction of the 2000 m scale). Unity 2018.1 itself re-seats the entries
// standing on samples SetHeights changed (measured in unity/EastCrossingsTest:
// exactly those, on every one of the seven TerrainData), so the removals are
// what must be done here; the re-seat is written anyway, to the terrain's own
// GetInterpolatedHeight - the rule the vanilla trees were seated by - so it
// holds even where the engine did not do it. Entries are found by world x/z
// quantised to 0.1 m (one step of tolerance, as RoadClear does).
//
// NavMesh. The vanilla bake (agent 0: radius 1, height 3.64, slope 35, climb
// 0.4, voxel 1/3 m, tiles of 256 voxels = 85.33 m - NOT the project settings,
// which say radius 0.5, slope 45, voxel 1/6) cannot be rebuilt in place:
// UpdateNavMeshData drops every tile outside the bounds it is given
// (unity/EastCrossingsTest: 562 -> 193 triangles, all ten probes outside the
// box gone), and the vanilla sources are not available to rebuild all 3600.
// So the vanilla NavMesh is left as it is and three things are added:
//   - a PATCH: our own NavMeshData on the cut ground only (a mesh of the
//     changed quads at their new heights, plus the colliders beside it), on
//     area 3 (unnamed, cost 1 in the game's area table; every NavMeshAgent
//     walks all areas), so queries can tell it from the vanilla surface;
//   - CARVING: a carving NavMeshObstacle also carves NavMesh up to the agent
//     height BELOW it, so the hanging vanilla surface cannot be carved just
//     above the patch. Instead the BAND - changed ground where the cut is less
//     than BandMax deep - is carved through, both surfaces: it rings the deep
//     part and cuts the hanging vanilla surface off from the untouched one.
//     Where the vanilla surface hangs more than DeepClear up, it is carved
//     too; lower, it stays, reachable from nowhere;
//   - BAND LINKS from the untouched vanilla surface over the band to the
//     patch, and at the seam the link to the tile.
//
// Paint. GWTerrain2's alphamap (17 layers, 2048 x 2048 bytes) is blended
// toward scree and rock on the cut faces with integer arithmetic on the
// shipped bytes, so every client paints the same; the grass goes where a face
// turned to rock.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static class CrossingCore
    {
        internal static Action<string> Log = delegate(string s) { Debug.Log("EastCrossings: " + s); };

        internal const float Q = 32766f;             // TerrainData native maximum
        internal const float Spacing = 4.8828125f;   // GW_Scene_1 sample spacing, x and z

        // ------------------------------------------------------------ decode

        internal static short[] DecodeI16(string b64)
        {
            byte[] b = Convert.FromBase64String(b64);
            short[] v = new short[b.Length / 2];
            Buffer.BlockCopy(b, 0, v, 0, v.Length * 2);
            return v;
        }

        internal static ushort[] DecodeU16(string b64)
        {
            byte[] b = Convert.FromBase64String(b64);
            ushort[] v = new ushort[b.Length / 2];
            Buffer.BlockCopy(b, 0, v, 0, v.Length * 2);
            return v;
        }

        // ------------------------------------------------------------ heights

        internal const int HeightsOld = 0, HeightsNew = 1, HeightsForeign = 2;

        /// <summary>What the box holds now: the vanilla heights, the cut ones,
        /// or neither (a changed game file - never write over that).</summary>
        internal static int ClassifyHeights(TerrainData d, int col0, int row0, int cols, int rows,
                                            short[] oldN, short[] newN, out int changed, out int matchOld,
                                            out int matchNew)
        {
            float[,] h = d.GetHeights(col0, row0, cols, rows);
            changed = 0; matchOld = 0; matchNew = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int k = r * cols + c;
                    if (oldN[k] == newN[k]) continue;
                    changed++;
                    int n = Mathf.RoundToInt(h[r, c] * Q);
                    if (n == oldN[k]) matchOld++;
                    else if (n == newN[k]) matchNew++;
                }
            if (matchNew == changed) return HeightsNew;
            if (matchOld == changed) return HeightsOld;
            return HeightsForeign;
        }

        /// <summary>Writes the box and reads it back; returns the samples that
        /// did not come back as the intended native value.</summary>
        internal static int WriteHeights(TerrainData d, int col0, int row0, int cols, int rows, short[] newN)
        {
            float[,] w = new float[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    w[r, c] = (newN[r * cols + c] + 0.25f) / Q;
            d.SetHeights(col0, row0, w);
            float[,] b = d.GetHeights(col0, row0, cols, rows);
            int bad = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (Mathf.RoundToInt(b[r, c] * Q) != newN[r * cols + c]) bad++;
            return bad;
        }

        // ------------------------------------------------------------ trees

        internal sealed class TreePlan
        {
            internal readonly HashSet<long> Remove = new HashSet<long>();
            internal readonly Dictionary<long, float> Reseat = new Dictionary<long, float>();
        }

        internal static long Key(int qx, int qz) { return ((long)qx << 32) ^ (uint)qz; }

        static long Q10(float v) { return Mathf.RoundToInt(v * 10f); }

        /// <summary>"name TAB x z x z ..." / "name TAB x z y x z y ..." -> plan per TerrainData name.</summary>
        internal static Dictionary<string, TreePlan> ParseTrees(string[] remove, string[] reseat)
        {
            Dictionary<string, TreePlan> plans = new Dictionary<string, TreePlan>();
            for (int i = 0; i < remove.Length; i++)
            {
                string[] f = remove[i].Split('\t');
                string[] v = f[1].Split(' ');
                TreePlan p = Plan(plans, f[0]);
                for (int k = 0; k + 1 < v.Length; k += 2)
                    p.Remove.Add(Key((int)Q10(Parse(v[k])), (int)Q10(Parse(v[k + 1]))));
            }
            for (int i = 0; i < reseat.Length; i++)
            {
                string[] f = reseat[i].Split('\t');
                string[] v = f[1].Split(' ');
                TreePlan p = Plan(plans, f[0]);
                for (int k = 0; k + 2 < v.Length; k += 3)
                    p.Reseat[Key((int)Q10(Parse(v[k])), (int)Q10(Parse(v[k + 1])))] = Parse(v[k + 2]);
            }
            return plans;
        }

        static TreePlan Plan(Dictionary<string, TreePlan> plans, string name)
        {
            TreePlan p;
            if (!plans.TryGetValue(name, out p)) { p = new TreePlan(); plans.Add(name, p); }
            return p;
        }

        internal static float Parse(string s)
        {
            return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }

        static bool Find(HashSet<long> set, int qx, int qz)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (set.Contains(Key(qx + dx, qz + dz))) return true;
            return false;
        }

        static bool Find(Dictionary<long, float> map, int qx, int qz, out float y)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (map.TryGetValue(Key(qx + dx, qz + dz), out y)) return true;
            y = 0f;
            return false;
        }

        /// <summary>Removes and re-seats one TerrainData's trees. A re-seated
        /// tree goes onto the terrain's own interpolated height (call after the
        /// heights). seatErr: the largest difference, in metres, between that
        /// height and the generator's bilinear estimate - information only.</summary>
        internal static void ApplyTrees(TerrainData d, Vector3 org, TreePlan plan, out int removed,
                                        out int reseated, out float seatErr)
        {
            removed = 0; reseated = 0; seatErr = 0f;
            TreeInstance[] trees = d.treeInstances;
            Vector3 size = d.size;
            List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
            bool dirty = false;
            for (int i = 0; i < trees.Length; i++)
            {
                TreeInstance t = trees[i];
                int qx = (int)Q10(org.x + t.position.x * size.x);
                int qz = (int)Q10(org.z + t.position.z * size.z);
                if (plan.Remove.Count > 0 && Find(plan.Remove, qx, qz)) { removed++; dirty = true; continue; }
                float y;
                if (plan.Reseat.Count > 0 && Find(plan.Reseat, qx, qz, out y))
                {
                    reseated++;
                    float g = d.GetInterpolatedHeight(t.position.x, t.position.z) / size.y;
                    seatErr = Mathf.Max(seatErr, Mathf.Abs(g - y) * size.y);
                    if (Mathf.Abs(t.position.y - g) > 1e-7f)
                    {
                        t.position = new Vector3(t.position.x, g, t.position.z);
                        dirty = true;
                    }
                }
                keep.Add(t);
            }
            if (dirty) d.treeInstances = keep.ToArray();
        }

        // ------------------------------------------------------------ navmesh

        /// <summary>The vanilla bake's own settings (sharedassets7 NavMeshData
        /// 633, m_NavMeshBuildSettings), so rebuilt tiles match their neighbours.</summary>
        internal static NavMeshBuildSettings Settings()
        {
            NavMeshBuildSettings s = NavMesh.GetSettingsByID(0);
            s.agentTypeID = 0;
            s.agentRadius = 1f;
            s.agentHeight = 3.64f;
            s.agentSlope = 35f;
            s.agentClimb = 0.4f;
            s.minRegionArea = 2f;
            s.overrideVoxelSize = true;
            s.voxelSize = 1f / 3f;
            s.overrideTileSize = true;
            s.tileSize = 256;
            return s;
        }

        /// <summary>Colliders (and TerrainColliders, as terrain sources) inside
        /// the box; keep() drops what must not be baked (players, NPCs,
        /// vehicles, triggers, foreign scenes).</summary>
        internal static List<NavMeshBuildSource> Sources(Bounds b, Predicate<Component> keep, out int dropped)
        {
            List<NavMeshBuildSource> all = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(b, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
                                          new List<NavMeshBuildMarkup>(), all);
            List<NavMeshBuildSource> kept = new List<NavMeshBuildSource>(all.Count);
            dropped = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (keep != null && !keep(all[i].component)) { dropped++; continue; }
                kept.Add(all[i]);
            }
            return kept;
        }

        internal static NavMeshBuildSource TerrainSource(Terrain t)
        {
            NavMeshBuildSource s = new NavMeshBuildSource();
            s.shape = NavMeshBuildSourceShape.Terrain;
            s.sourceObject = t.terrainData;
            s.transform = Matrix4x4.TRS(t.GetPosition(), Quaternion.identity, Vector3.one);
            s.area = 0;
            return s;
        }

        /// <summary>Is there NavMesh within `radius` of p, and where.</summary>
        internal static bool NavAt(Vector3 p, float radius, out Vector3 hit)
        {
            NavMeshHit h;
            if (NavMesh.SamplePosition(p, out h, radius, NavMesh.AllAreas)) { hit = h.position; return true; }
            hit = Vector3.zero;
            return false;
        }

        internal static string PathCheck(Vector3 a, Vector3 b)
        {
            Vector3 na, nb;
            if (!NavAt(a, 6f, out na)) return "no NavMesh within 6 m of " + a;
            if (!NavAt(b, 6f, out nb)) return "no NavMesh within 6 m of " + b;
            NavMeshPath p = new NavMeshPath();
            bool ok = NavMesh.CalculatePath(na, nb, NavMesh.AllAreas, p);
            float len = 0f;
            for (int i = 1; i < p.corners.Length; i++) len += Vector3.Distance(p.corners[i - 1], p.corners[i]);
            Vector3 end = p.corners.Length > 0 ? p.corners[p.corners.Length - 1] : na;
            return "computed " + ok + ", status " + p.status + ", " + p.corners.Length + " corners, "
                   + len.ToString("F0") + " m, ends " + Vector3.Distance(end, nb).ToString("F1") + " m from the goal";
        }

        internal static NavMeshLinkInstance Link(Vector3 a, Vector3 b, float width)
        {
            NavMeshLinkData l = new NavMeshLinkData();
            l.startPosition = a;
            l.endPosition = b;
            l.width = width;
            l.costModifier = -1f;
            l.bidirectional = true;
            l.area = 0;
            l.agentTypeID = 0;
            return NavMesh.AddLink(l);
        }

        // ------------------------------------------------------------ patch, carve, links

        internal const int PatchArea = 3;           // unnamed, cost 1 in the game's area table
        internal const int VanillaMask = 1 << 0, PatchMask = 1 << PatchArea;
        /// <summary>Changed quads where the vanilla surface would hang less than
        /// this above the new ground form the BAND: both surfaces are carved
        /// there, which cuts the hanging vanilla surface off from the untouched
        /// one. Deeper quads keep the patch; links cross the band.</summary>
        internal const float BandMax = 2.0f;
        /// <summary>A carving obstacle also carves NavMesh up to the agent's
        /// height (3.64 m) BELOW it (unity/EastCrossingsTest). A deep box starts
        /// this far above the new ground, so the patch under it is spared.</summary>
        internal const float DeepClear = 4.3f;

        internal const int Untouched = 0, Band = 1, Deep = 2;

        internal static bool NavAt(Vector3 p, float radius, int mask, out Vector3 hit)
        {
            NavMeshHit h;
            if (NavMesh.SamplePosition(p, out h, radius, mask)) { hit = h.position; return true; }
            hit = Vector3.zero;
            return false;
        }

        static float M(short n) { return n * 2000f / Q; }

        /// <summary>Per sample quad of the height box (row r, col c = its south-west
        /// sample): lowest / highest old corner, lowest / highest new corner,
        /// and whether any corner changed.</summary>
        static bool Quad(short[] oldN, short[] newN, int cols, int r, int c, out float minOld, out float maxOld,
                         out float minNew, out float maxNew)
        {
            minOld = float.MaxValue; maxOld = float.MinValue; minNew = float.MaxValue; maxNew = float.MinValue;
            bool changed = false;
            for (int dr = 0; dr <= 1; dr++)
                for (int dc = 0; dc <= 1; dc++)
                {
                    int k = (r + dr) * cols + c + dc;
                    float o = M(oldN[k]), n = M(newN[k]);
                    if (oldN[k] != newN[k]) changed = true;
                    minOld = Mathf.Min(minOld, o); maxOld = Mathf.Max(maxOld, o);
                    minNew = Mathf.Min(minNew, n); maxNew = Mathf.Max(maxNew, n);
                }
            return changed;
        }

        /// <summary>Untouched, Band or Deep (see BandMax).</summary>
        internal static int Class(short[] oldN, short[] newN, int cols, int r, int c)
        {
            float minOld, maxOld, minNew, maxNew;
            if (!Quad(oldN, newN, cols, r, c, out minOld, out maxOld, out minNew, out maxNew)) return Untouched;
            return minOld - maxNew >= BandMax ? Deep : Band;
        }

        static Vector3 QuadCentre(int col, int row)
        {
            return new Vector3(-2500f + (col + 0.5f) * Spacing, 0f, -2500f + (row + 0.5f) * Spacing);
        }

        /// <summary>
        /// The patch's ground: one mesh of every changed quad at its NEW heights
        /// (world coordinates), so the patch lies on the cut and nowhere on
        /// untouched ground - no two surfaces at one height, no ambiguous link
        /// end. Obstacles come from the colliders beside it (Sources without the
        /// TerrainColliders).
        /// </summary>
        internal static Mesh GroundMesh(short[] oldN, short[] newN, int col0, int row0, int cols, int rows)
        {
            List<Vector3> v = new List<Vector3>();
            List<int> t = new List<int>();
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c + 1 < cols; c++)
                {
                    if (Class(oldN, newN, cols, r, c) == Untouched) continue;
                    int b = v.Count;
                    for (int dr = 0; dr <= 1; dr++)
                        for (int dc = 0; dc <= 1; dc++)
                            v.Add(new Vector3(-2500f + (col0 + c + dc) * Spacing, M(newN[(r + dr) * cols + c + dc]),
                                              -2500f + (row0 + r + dr) * Spacing));
                    // (r,c) (r,c+1) (r+1,c) (r+1,c+1): the same diagonal as Unity's terrain
                    t.Add(b); t.Add(b + 2); t.Add(b + 3);
                    t.Add(b); t.Add(b + 3); t.Add(b + 1);
                }
            Mesh m = new Mesh();
            m.name = "EastCrossingGround";
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Patch sources: the ground mesh plus the given colliders
        /// (TerrainColliders dropped - the mesh is the ground), all on PatchArea.</summary>
        internal static List<NavMeshBuildSource> PatchSources(Mesh ground, List<NavMeshBuildSource> colliders)
        {
            List<NavMeshBuildSource> src = new List<NavMeshBuildSource>();
            NavMeshBuildSource g = new NavMeshBuildSource();
            g.shape = NavMeshBuildSourceShape.Mesh;
            g.sourceObject = ground;
            g.transform = Matrix4x4.identity;
            g.area = PatchArea;
            src.Add(g);
            for (int i = 0; i < colliders.Count; i++)
            {
                if (colliders[i].shape == NavMeshBuildSourceShape.Terrain) continue;
                NavMeshBuildSource s = colliders[i];
                s.area = PatchArea;
                src.Add(s);
            }
            return src;
        }

        /// <summary>Our own NavMeshData over the box, added at once; the tiles
        /// arrive when the returned op is done. Every tile of it is ours, so a
        /// later in-place update (Repatch) loses nothing.</summary>
        internal static NavMeshData Patch(List<NavMeshBuildSource> src, Bounds box, out NavMeshDataInstance inst,
                                          out AsyncOperation op)
        {
            NavMeshData d = new NavMeshData(0);
            inst = NavMesh.AddNavMeshData(d);
            op = NavMeshBuilder.UpdateNavMeshDataAsync(d, Settings(), src, box);
            return d;
        }

        internal static AsyncOperation Repatch(NavMeshData d, List<NavMeshBuildSource> src, Bounds box)
        {
            return NavMeshBuilder.UpdateNavMeshDataAsync(d, Settings(), src, box);
        }

        /// <summary>Row runs of quads of one class merged into boxes. through:
        /// the box reaches from the lowest bottom to the highest top of its
        /// quads (the band: take everything). Otherwise the box stays above the
        /// highest bottom and the run ends when that would reach `floor`.</summary>
        delegate bool Span(int r, int c, out float lo, out float hi, out float floor);

        static List<Bounds> Runs(int col0, int row0, int cols, int rows, bool through, Span span)
        {
            List<Bounds> boxes = new List<Bounds>();
            for (int r = 0; r + 1 < rows; r++)
            {
                int start = -1;
                float lo = 0f, hi = 0f, floor = 0f;
                for (int c = 0; c < cols; c++)
                {
                    float l = 0f, h = 0f, f = 0f;
                    bool ok = c + 1 < cols && span(r, c, out l, out h, out f);
                    if (ok && start >= 0 && (through || Mathf.Max(lo, l) < Mathf.Min(floor, f)))
                    {
                        lo = through ? Mathf.Min(lo, l) : Mathf.Max(lo, l);
                        hi = Mathf.Max(hi, h);
                        floor = Mathf.Min(floor, f);
                        continue;
                    }
                    if (start >= 0) boxes.Add(Box(col0 + start, col0 + c, row0 + r, lo, hi));
                    start = -1;
                    if (ok) { start = c; lo = l; hi = h; floor = f; }
                }
            }
            return boxes;
        }

        static Bounds Box(int c0, int c1, int r, float lo, float hi)
        {
            float x0 = -2500f + c0 * Spacing, x1 = -2500f + c1 * Spacing;
            float z0 = -2500f + r * Spacing, z1 = z0 + Spacing;
            return new Bounds(new Vector3((x0 + x1) / 2f, (lo + hi) / 2f, (z0 + z1) / 2f),
                              new Vector3(x1 - x0, hi - lo, z1 - z0));
        }

        /// <summary>The band: every Band quad, from under both surfaces to over
        /// both. Carves the hanging vanilla surface and the patch alike.</summary>
        internal static List<Bounds> BandBoxes(short[] oldN, short[] newN, int col0, int row0, int cols, int rows)
        {
            return Runs(col0, row0, cols, rows, true, delegate(int r, int c, out float lo, out float hi, out float floor)
            {
                float minOld, maxOld, minNew, maxNew;
                lo = hi = floor = 0f;
                if (Class(oldN, newN, cols, r, c) != Band) return false;
                Quad(oldN, newN, cols, r, c, out minOld, out maxOld, out minNew, out maxNew);
                lo = Mathf.Min(minOld, minNew) - 1.5f;
                hi = Mathf.Max(maxOld, maxNew) + 2f;
                floor = float.MaxValue;
                return true;
            });
        }

        /// <summary>Over Deep quads where the vanilla surface hangs high enough:
        /// from DeepClear above the new ground to over the old one - the vanilla
        /// surface out, the patch spared. Where it hangs lower it stays, cut off
        /// by the band and reachable from nowhere.</summary>
        internal static List<Bounds> DeepBoxes(short[] oldN, short[] newN, int col0, int row0, int cols, int rows)
        {
            return Runs(col0, row0, cols, rows, false, delegate(int r, int c, out float lo, out float hi, out float floor)
            {
                float minOld, maxOld, minNew, maxNew;
                lo = hi = floor = 0f;
                if (!DeepCarvable(oldN, newN, cols, r, c)) return false;
                Quad(oldN, newN, cols, r, c, out minOld, out maxOld, out minNew, out maxNew);
                lo = maxNew + DeepClear;
                hi = maxOld + 1.5f;
                floor = minOld - 0.3f;
                return true;
            });
        }

        /// <summary>A Deep quad whose vanilla surface hangs high enough for a
        /// box that spares the patch (DeepClear over the new ground).</summary>
        internal static bool DeepCarvable(short[] oldN, short[] newN, int cols, int r, int c)
        {
            float minOld, maxOld, minNew, maxNew;
            if (Class(oldN, newN, cols, r, c) != Deep) return false;
            Quad(oldN, newN, cols, r, c, out minOld, out maxOld, out minNew, out maxNew);
            return maxNew + DeepClear < minOld - 0.3f;
        }

        /// <summary>
        /// The vanilla-side end of a seam link: where the cut meets the seam it
        /// may be shallow (the berm's outer face falls toward the edge), so the
        /// band there is carved and the patch starts further west. Walks west
        /// from x0 along z to the first point on the patch; false if none
        /// within `reach` metres.
        /// </summary>
        internal static bool SeamWest(float x0, float z, float reach, Func<float, float> ground, out Vector3 at)
        {
            for (float x = x0; x >= x0 - reach; x -= 1f)
                if (NavAt(new Vector3(x, ground(x), z), 1f, PatchMask, out at)) return true;
            at = Vector3.zero;
            return false;
        }

        /// <summary>One carving NavMeshObstacle per box under parent.</summary>
        internal static int Carve(List<Bounds> boxes, Transform parent, string name)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                GameObject go = new GameObject(name + " " + i);
                go.transform.SetParent(parent, false);
                go.transform.position = boxes[i].center;
                NavMeshObstacle o = go.AddComponent<NavMeshObstacle>();
                o.shape = NavMeshObstacleShape.Box;
                o.center = Vector3.zero;
                o.size = boxes[i].size;
                o.carving = true;
                o.carveOnlyStationary = true;
                o.carvingTimeToStationary = 0.1f;
            }
            return boxes.Count;
        }

        /// <summary>
        /// Links over the band: from every Deep quad that borders the band, the
        /// nearest Untouched quad reached through band quads only (at most
        /// `reach` quads); start 2 m beyond that quad's centre on the vanilla
        /// surface, end at the Deep quad's centre on the patch. Kept when the
        /// grade is walkable (35 deg, the bake's slope), found on both surfaces
        /// with their area masks, no vanilla remnant hangs over the patch end,
        /// and at least `spacing` from another link.
        /// </summary>
        internal static List<NavMeshLinkInstance> BandLinks(short[] oldN, short[] newN, int col0, int row0, int cols,
                                                            int rows, int reach, float spacing, float width,
                                                            out int candidates)
        {
            List<NavMeshLinkInstance> links = new List<NavMeshLinkInstance>();
            List<Vector3> placed = new List<Vector3>();
            candidates = 0;
            int nr = rows - 1, nc = cols - 1;
            int[] cls = new int[nr * nc];
            for (int r = 0; r < nr; r++)
                for (int c = 0; c < nc; c++) cls[r * nc + c] = Class(oldN, newN, cols, r, c);
            int[] dr = { 0, 0, 1, -1 }, dc = { 1, -1, 0, 0 };
            for (int r = 0; r < nr; r++)
                for (int c = 0; c < nc; c++)
                {
                    if (cls[r * nc + c] != Deep) continue;
                    bool rim = false;
                    for (int k = 0; k < 4 && !rim; k++)
                    {
                        int r2 = r + dr[k], c2 = c + dc[k];
                        rim = r2 >= 0 && c2 >= 0 && r2 < nr && c2 < nc && cls[r2 * nc + c2] == Band;
                    }
                    if (!rim) continue;
                    // breadth first through band quads to the nearest untouched quad
                    Dictionary<int, int> seen = new Dictionary<int, int>();
                    Queue<int> q = new Queue<int>();
                    seen[r * nc + c] = 0;
                    q.Enqueue(r * nc + c);
                    int found = -1, from = -1;
                    while (q.Count > 0 && found < 0)
                    {
                        int cur = q.Dequeue(), d = seen[cur];
                        if (d >= reach) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            int r2 = cur / nc + dr[k], c2 = cur % nc + dc[k];
                            if (r2 < 0 || c2 < 0 || r2 >= nr || c2 >= nc) continue;
                            int id = r2 * nc + c2;
                            if (seen.ContainsKey(id)) continue;
                            seen[id] = d + 1;
                            if (cls[id] == Untouched) { found = id; from = cur; break; }
                            if (cls[id] == Band) q.Enqueue(id);
                        }
                    }
                    if (found < 0) continue;
                    candidates++;
                    Vector3 u = QuadCentre(col0 + found % nc, row0 + found / nc);
                    Vector3 via = QuadCentre(col0 + from % nc, row0 + from / nc);
                    Vector3 dv = QuadCentre(col0 + c, row0 + r);
                    Vector3 a = u + (u - via).normalized * 2f;
                    bool near = false;
                    for (int i = 0; i < placed.Count && !near; i++)
                        near = (placed[i] - dv).sqrMagnitude < spacing * spacing;
                    if (near) continue;
                    a.y = Height(oldN, cols, col0, row0, a);
                    dv.y = Height(newN, cols, col0, row0, dv);
                    Vector3 va, pb, hang;
                    if (!NavAt(a, 1.5f, VanillaMask, out va) || !NavAt(dv, 1.5f, PatchMask, out pb)) continue;
                    // a remnant of the vanilla surface hanging low over the patch end
                    // (under DeepClear, so not carved): the link would attach to it
                    // and lead agents up onto a floating floor (unity/EastCrossingsTest)
                    if (NavAt(pb + Vector3.up * (DeepClear / 2f + 0.3f), DeepClear / 2f + 0.6f, VanillaMask, out hang)
                        && hang.y > pb.y + 0.3f) continue;
                    Vector3 flat = pb - va;
                    flat.y = 0f;
                    if (Mathf.Abs(pb.y - va.y) > Mathf.Tan(35f * Mathf.Deg2Rad) * flat.magnitude) continue;
                    NavMeshLinkInstance li = Link(va, pb, width);
                    if (!li.valid) continue;
                    links.Add(li);
                    placed.Add(dv);
                }
            return links;
        }

        /// <summary>Ground of the given native box at a world point, bilinear.</summary>
        static float Height(short[] a, int cols, int col0, int row0, Vector3 p)
        {
            int rows = a.Length / cols;
            float fx = (p.x + 2500f) / Spacing - col0, fz = (p.z + 2500f) / Spacing - row0;
            int c = Mathf.Clamp((int)fx, 0, cols - 2), r = Mathf.Clamp((int)fz, 0, rows - 2);
            float tx = Mathf.Clamp01(fx - c), tz = Mathf.Clamp01(fz - r);
            float h00 = M(a[r * cols + c]), h01 = M(a[r * cols + c + 1]);
            float h10 = M(a[(r + 1) * cols + c]), h11 = M(a[(r + 1) * cols + c + 1]);
            return (h00 * (1 - tx) + h01 * tx) * (1 - tz) + (h10 * (1 - tx) + h11 * tx) * tz;
        }

        // ------------------------------------------------------------ paint

        /// <summary>Per texel of a paint box, the strength (0 = untouched) from
        /// the 7-byte records: u16 index, strength, four target bytes.</summary>
        internal static byte[] PaintStrength(byte[] rec, int w, int h)
        {
            byte[] s = new byte[w * h];
            for (int i = 0; i + 6 < rec.Length; i += 7) s[rec[i] | (rec[i + 1] << 8)] = rec[i + 2];
            return s;
        }

        /// <summary>A weighted sum of the box's alphamap bytes: tells the box a
        /// session painted from one it did not (a swap of two layers counts).</summary>
        internal static long AlphaSum(TerrainData d, int x0, int z0, int w, int h)
        {
            float[,,] a = d.GetAlphamaps(x0, z0, w, h);
            int layers = a.GetLength(2);
            long sum = 0;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    for (int l = 0; l < layers; l++)
                        sum += Mathf.RoundToInt(a[z, x, l] * 255f) * (long)(((z * w + x) * layers + l) % 251 + 1);
            return sum;
        }

        /// <summary>
        /// Blends each listed texel toward its target mix, in whole alphamap
        /// bytes (the textures hold bytes): new = (old * (255 - s) + target * s)
        /// / 255, rounded; the target is 0 on every layer but the four of
        /// `layers`. Integer arithmetic on the bytes the game shipped, so every
        /// client writes the same. Written as (n + 0.25) / 255 - n back whether
        /// the engine rounds or truncates - and read back. Returns AlphaSum after.
        /// </summary>
        internal static long PaintAlpha(TerrainData d, int x0, int z0, int w, int h, byte[] rec, int[] layers,
                                        out int off)
        {
            float[,,] a = d.GetAlphamaps(x0, z0, w, h);
            int nl = a.GetLength(2);
            int[] want = new int[rec.Length / 7 * nl];
            for (int i = 0; i + 6 < rec.Length; i += 7)
            {
                int idx = rec[i] | (rec[i + 1] << 8), s = rec[i + 2];
                int z = idx / w, x = idx % w;
                for (int l = 0; l < nl; l++)
                {
                    int o = Mathf.RoundToInt(a[z, x, l] * 255f), t = 0;
                    for (int m = 0; m < layers.Length; m++) if (layers[m] == l) t = rec[i + 3 + m];
                    int n = (o * (255 - s) + t * s + 127) / 255;
                    want[i / 7 * nl + l] = n;
                    a[z, x, l] = (n + 0.25f) / 255f;
                }
            }
            d.SetAlphamaps(x0, z0, a);
            float[,,] b = d.GetAlphamaps(x0, z0, w, h);
            off = 0;
            for (int i = 0; i + 6 < rec.Length; i += 7)
            {
                int idx = rec[i] | (rec[i + 1] << 8);
                for (int l = 0; l < nl; l++)
                    if (Mathf.RoundToInt(b[idx / w, idx % w, l] * 255f) != want[i / 7 * nl + l]) off++;
            }
            return AlphaSum(d, x0, z0, w, h);
        }

        /// <summary>Grass off every detail cell whose centre lies on a paint
        /// texel of at least `min` strength (a cut face turned to rock). Returns
        /// the cells cleared; a second call clears none.</summary>
        internal static int ClearGrass(TerrainData d, Vector3 org, int ax0, int az0, int aw, int ah, byte[] strength,
                                       int alphaRes, int min)
        {
            int res = d.detailResolution;
            DetailPrototype[] kinds = d.detailPrototypes;
            if (res <= 0 || kinds == null || kinds.Length == 0) return 0;
            Vector3 size = d.size;
            float at = size.x / alphaRes;
            float wx0 = org.x + ax0 * at, wz0 = org.z + az0 * at, wx1 = wx0 + aw * at, wz1 = wz0 + ah * at;
            int dx0 = Mathf.Clamp(Mathf.FloorToInt((wx0 - org.x) / size.x * res), 0, res - 1);
            int dz0 = Mathf.Clamp(Mathf.FloorToInt((wz0 - org.z) / size.z * res), 0, res - 1);
            int dx1 = Mathf.Clamp(Mathf.CeilToInt((wx1 - org.x) / size.x * res), 0, res - 1);
            int dz1 = Mathf.Clamp(Mathf.CeilToInt((wz1 - org.z) / size.z * res), 0, res - 1);
            int dw = dx1 - dx0 + 1, dh = dz1 - dz0 + 1, cleared = 0;
            for (int layer = 0; layer < kinds.Length; layer++)
            {
                int[,] patch = d.GetDetailLayer(dx0, dz0, dw, dh, layer);     // [row = z, column = x]
                bool changed = false;
                for (int row = 0; row < dh; row++)
                    for (int col = 0; col < dw; col++)
                    {
                        if (patch[row, col] == 0) continue;
                        float wx = org.x + (dx0 + col + 0.5f) / res * size.x;
                        float wz = org.z + (dz0 + row + 0.5f) / res * size.z;
                        int tx = Mathf.FloorToInt((wx - org.x) / at) - ax0, tz = Mathf.FloorToInt((wz - org.z) / at) - az0;
                        if (tx < 0 || tz < 0 || tx >= aw || tz >= ah || strength[tz * aw + tx] < min) continue;
                        patch[row, col] = 0;
                        changed = true;
                        cleared++;
                    }
                if (changed) d.SetDetailLayer(dx0, dz0, layer, patch);
            }
            return cleared;
        }
    }
}

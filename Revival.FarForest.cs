// Next Day: Survival - Revival Toolkit
//
// FAR FOREST (task N2b). Forest is visible at every distance.
//
// Why: the terrain draws its trees only out to Terrain.treeDistance (1000 u =
// 357 m on foot at ViewDistance Medium, x2.5 in FlightView), the far clip is
// further out (2000 u, x1.6 in flight). Everything between is bare splat
// terrain: from the An-2 the land past ~900 m looks like desert.
//
// WHAT IT DRAWS: a canopy sheet built from the SAME forest mask as N2's
// "hidden in forest" rule (NpcDistance.Masks: tree instances per 4 m cell,
// forest where 2+ trees in 12 m and 10+ in 28 m). Per terrain the mask is
// merged into canopy cells (Normal 12 m, Low 24 m; a canopy cell is forest when
// at least half its mask cells are). Every forest cell is a quad at about 80 %
// of the terrain's average tree top over the ground (a little lower at the
// forest edge, +-12 % hash jitter), every edge towards open land gets a
// vertical skirt down into the ground, so a treeline reads as a wall of trees
// at grazing angles. One opaque Standard material (forest green times a
// blotchy 6 m noise texture), lit and fogged like the terrain, no shadows,
// no colliders, no probes. Meshes are static per 16x16-cell chunk (192 m).
//
// NO RING: a chunk is drawn whole when all of it lies beyond the tree
// distance minus a margin; a chunk that straddles the tree distance gets a
// per-cell mesh of only the cells whose centre is beyond that line
// (rebuilt when the camera moved 20 m or the tree distance changed, e.g.
// the FlightView blend). The margin (2 x 20 m + half a cell) makes the canopy
// start a little INSIDE the drawn trees, never behind them: the overlap sits
// inside the tree crowns, a gap would show bare ground. Distances are 3D from
// the camera, like the terrain's own tree cull.
//
// Rejected options (see docs/ai/tasks/n02b-far-forest.md): pushing the tree or
// billboard distance (legacy terrain rebuilds billboards on the main thread,
// 67,738 trees on the east tile - Q1 perf), tinting the basemap / splat maps
// (EastWorld runs the basemap at 20000 u; a flat tint has no height, and a
// splat edit changes the ground under the near trees too), impostor cards
// (thousands of quads that need sorting and a custom shader to look right).
//
// Cost: the mesh build is time-sliced (0.6 ms a frame) after the mask is
// ready; per frame one distance test per chunk only after 20 m of movement,
// and edge-chunk rebuilds (a few hundred quads each) in the same slice. The
// GPU draws one opaque sheet. Admin "Far forest bench" measures on vs off.
//
// Setting: [FarForest] Quality Off / Low / Normal (default Normal), also in
// the F2 settings window. Colour is a config key.
//
// Seams: RevivalPlugin.Awake (BindConfig), RevivalPlugin.Update (Tick, slot
// S_FarForestT), NpcDistance (Masks, MaskVersion, PumpMask), Admin panel
// (Bench, Status), Settings window (Level, SetLevel).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class FarForest
    {
        /// <summary>World units per real metre (PlayerAn2.K).</summary>
        const float K = 2.8f;

        const int ChunkCells = 16;
        const float MoveM = 20f;                // re-evaluate after this much camera movement
        const float SliceMs = 0.6f;             // build + edge rebuild budget per frame
        const float CanopyOfTop = 0.8f;         // sheet height, fraction of the average tree top
        const float EdgeFactor = 0.7f;          // lower at the forest edge
        const float Jitter = 0.12f;
        const float DefaultTopM = 14f;          // tree top when the prototypes tell nothing
        const float SkirtSinkU = 2f;
        const float NoiseTexelM = 6f;
        const int NoiseSize = 128;

        internal static readonly string[] Names = { "Off", "Low", "Normal" };
        static readonly float[] CellM = { 0f, 24f, 12f };

        // ============================================================ config

        static ConfigEntry<string> _cfgQuality, _cfgColour;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgQuality = cfg.Bind("FarForest", "Quality", "Normal",
                "Forest past the tree draw distance: Normal (12 m canopy cells), Low "
                + "(24 m cells, a quarter of the triangles) or Off (bare terrain past the "
                + "trees, the game's own). A low-cost canopy from the same forest mask "
                + "that hides men in the forest, so distant land reads as forest from "
                + "the air and on the ground.");
            _cfgColour = cfg.Bind("FarForest", "Colour", "0.20,0.27,0.13",
                "Far canopy colour R,G,B (0..1), lit by the sun like the terrain.");
        }

        internal static int Level
        {
            get
            {
                string s = _cfgQuality == null ? "Normal" : _cfgQuality.Value;
                for (int i = 0; i < Names.Length; i++)
                    if (string.Equals(s, Names[i], StringComparison.OrdinalIgnoreCase)) return i;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)) return 0;
                return Names.Length - 1;
            }
        }

        internal static void SetLevel(int level)
        {
            if (_cfgQuality == null) return;
            level = Mathf.Clamp(level, 0, Names.Length - 1);
            _cfgQuality.Value = Names[level];
        }

        /// <summary>The far canopy wants the forest mask built.</summary>
        internal static bool Wanted { get { return Level > 0; } }

        // ============================================================ state

        sealed class Field
        {
            public Terrain T;
            public float X0, Z0, Cell, Top;
            public int W, H, Cells;
            public bool[] Forest;
            public GameObject Root;
            public readonly List<Chunk> Chunks = new List<Chunk>();

            public bool At(int x, int z)
            {
                if (x >= 0 && z >= 0 && x < W && z < H) return Forest[z * W + x];
                // Beyond this terrain: ask the neighbour (east tile seams).
                return ForestAtWorld(X0 + (x + 0.5f) * Cell, Z0 + (z + 0.5f) * Cell, this);
            }
        }

        sealed class Chunk
        {
            public Field F;
            public int CX, CZ, NX, NZ, Cells;
            public float[] Ground;              // (NX+1)*(NZ+1), world y
            public Vector3[] Normal;
            public float X0, Z0, X1, Z1, MinY, MaxY;
            public MeshFilter Mf;
            public MeshRenderer R;
            public Mesh Full, Part;
            public int FullTris, PartTris;
            public byte State;                  // Off / Whole / Edge
            public bool Queued;
            public Vector3 QCam;
            public float QCut;
        }

        const byte Off = 0, Whole = 1, Edge = 2;

        static List<Field> _fields = new List<Field>();
        static List<Field> _building;           // fields of the build in flight (neighbour lookups)
        static IEnumerator _build;
        static int _builtVersion = -1, _builtLevel = -1;
        static readonly List<Chunk> _queue = new List<Chunk>();
        static Material _mat;
        static string _matColour;
        static bool _warned, _dirty = true;
        static Vector3 _evalCam;
        static float _evalFar;
        static readonly Stopwatch _sw = new Stopwatch();
        static double _tickMs;
        static int _sWhole, _sEdge, _sTris, _totalChunks;

        // ============================================================ tick

        internal static void Tick()
        {
            long t0 = Stopwatch.GetTimestamp();
            _sw.Reset();
            _sw.Start();
            try { Step(); }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("FarForest: " + ex); }
                _build = null;
            }
            _sw.Stop();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _tickMs += (ms - _tickMs) * 0.05;
            if (_bench != 0 && _benchSampling) _benchOwn += ms;
        }

        static bool OverBudget() { return _sw.Elapsed.TotalMilliseconds > SliceMs; }

        static void Step()
        {
            int level = Level;
            if (level == 0)
            {
                if (_fields.Count > 0 || _build != null) { Clear(); _builtLevel = 0; }
                return;
            }
            // The mask is built by NpcDistance; pumped from here as well, in
            // case its Harmony install failed and its Tick never runs.
            NpcDistance.PumpMask();

            if (_build == null && (NpcDistance.MaskVersion != _builtVersion || level != _builtLevel))
            {
                _builtVersion = NpcDistance.MaskVersion;
                _builtLevel = level;
                if (NpcDistance.Masks.Count > 0) _build = Build(NpcDistance.Masks, CellM[level]);
                else Clear();
            }
            if (_build != null)
            {
                bool more;
                try { more = _build.MoveNext(); }
                catch (Exception ex)
                {
                    more = false;
                    RevivalPlugin.L.LogWarning("FarForest: canopy build failed - " + ex.Message);
                }
                if (!more) { _build = null; _building = null; }
            }

            BenchStep();
            if (_mat != null && _cfgColour != null && _cfgColour.Value != _matColour) Mat();
            Camera cam = Camera.main;
            if (cam == null || _fields.Count == 0) return;
            Vector3 p = cam.transform.position;
            float far = cam.farClipPlane;
            if (!_dirty)
            {
                float move = MoveM * K;
                if ((p - _evalCam).sqrMagnitude > move * move || Mathf.Abs(far - _evalFar) > move) _dirty = true;
                for (int i = 0; !_dirty && i < _fields.Count; i++)
                {
                    Field f = _fields[i];
                    if (f.T != null && Mathf.Abs(f.T.treeDistance - _fieldTd[i]) > move * 0.5f) _dirty = true;
                }
            }
            if (_dirty) Evaluate(p, far);
            Rebuild();
        }

        // ============================================================ visibility

        static float[] _fieldTd = new float[0];

        static void Evaluate(Vector3 cam, float far)
        {
            _dirty = false;
            _evalCam = cam;
            _evalFar = far;
            if (_fieldTd.Length != _fields.Count) _fieldTd = new float[_fields.Count];
            float move = MoveM * K;
            for (int i = 0; i < _fields.Count; i++)
            {
                Field f = _fields[i];
                if (f.T == null || f.Root == null) continue;
                float td = f.T.treeDistance;
                _fieldTd[i] = td;
                float cut = td - (2f * move + f.Cell * 0.71f);
                for (int c = 0; c < f.Chunks.Count; c++)
                {
                    Chunk ch = f.Chunks[c];
                    if (ch.R == null) continue;
                    float dx = Mathf.Max(0f, Mathf.Max(ch.X0 - cam.x, cam.x - ch.X1));
                    float dz = Mathf.Max(0f, Mathf.Max(ch.Z0 - cam.z, cam.z - ch.Z1));
                    float dy = cam.y < ch.MinY ? ch.MinY - cam.y : (cam.y > ch.MaxY ? cam.y - ch.MaxY : 0f);
                    float minD = Mathf.Sqrt(dx * dx + dz * dz + dy * dy);
                    float fx = Mathf.Max(Mathf.Abs(cam.x - ch.X0), Mathf.Abs(cam.x - ch.X1));
                    float fz = Mathf.Max(Mathf.Abs(cam.z - ch.Z0), Mathf.Abs(cam.z - ch.Z1));
                    float fy = Mathf.Max(Mathf.Abs(cam.y - ch.MinY), Mathf.Abs(cam.y - ch.MaxY));
                    float maxD = Mathf.Sqrt(fx * fx + fz * fz + fy * fy);
                    if (minD > far || maxD < cut) SetState(ch, Off);
                    else if (minD > cut) SetState(ch, Whole);
                    else
                    {
                        // Straddles the tree distance: per-cell mesh.
                        ch.QCam = cam;
                        ch.QCut = cut;
                        if (!ch.Queued) { ch.Queued = true; _queue.Add(ch); }
                    }
                }
            }
            Stats();
        }

        /// <summary>Drawn chunks and triangles, for the admin line and the bench.</summary>
        static void Stats()
        {
            _sWhole = _sEdge = _sTris = 0;
            for (int i = 0; i < _fields.Count; i++)
                for (int c = 0; c < _fields[i].Chunks.Count; c++)
                {
                    Chunk ch = _fields[i].Chunks[c];
                    if (ch.R == null || !ch.R.enabled) continue;
                    if (ch.State == Whole) { _sWhole++; _sTris += ch.FullTris; }
                    else if (ch.State == Edge) { _sEdge++; _sTris += ch.PartTris; }
                }
        }

        static void SetState(Chunk ch, byte s)
        {
            ch.Queued = false;                  // a queued edge rebuild is dropped (skipped in Rebuild)
            if (ch.State == s) return;
            ch.State = s;
            if (s == Whole) { ch.Mf.sharedMesh = ch.Full; ch.R.enabled = true; }
            else ch.R.enabled = false;
        }

        static void Rebuild()
        {
            int done = 0;
            while (_queue.Count > 0 && (done == 0 || !OverBudget()))
            {
                Chunk ch = _queue[_queue.Count - 1];
                _queue.RemoveAt(_queue.Count - 1);
                if (!ch.Queued || ch.R == null) continue;
                ch.Queued = false;
                done++;
                if (ch.Part == null)
                {
                    ch.Part = new Mesh();
                    ch.Part.name = "NDR_FarForestEdge";
                    ch.Part.MarkDynamic();
                }
                ch.PartTris = Fill(ch, ch.Part, ch.QCam, ch.QCut);
                ch.State = Edge;
                ch.Mf.sharedMesh = ch.Part;
                ch.R.enabled = ch.PartTris > 0;
            }
            if (done > 0 && _queue.Count == 0) Stats();
        }

        // ============================================================ build

        static IEnumerator Build(List<NpcDistance.Mask> masks, float cellM)
        {
            float started = Time.realtimeSinceStartup;
            int frames = 0;
            List<Field> made = new List<Field>();
            _building = made;

            // 1. Canopy cells of every terrain first: the skirts look across
            //    terrain seams.
            for (int t = 0; t < masks.Count; t++)
            {
                NpcDistance.Mask m = masks[t];
                if (m == null || m.Terrain == null || m.Bits == null) continue;
                Field f = new Field();
                f.T = m.Terrain;
                int k = Mathf.Max(1, Mathf.RoundToInt(cellM * K / m.Cell));
                f.Cell = m.Cell * k;
                f.X0 = m.X0;
                f.Z0 = m.Z0;
                f.W = (m.W + k - 1) / k;
                f.H = (m.H + k - 1) / k;
                float top = m.TreeTop > 0f ? m.TreeTop : DefaultTopM * K;
                f.Top = Mathf.Clamp(top, 6f * K, 35f * K) * CanopyOfTop;
                f.Forest = new bool[f.W * f.H];
                for (int z = 0; z < f.H; z++)
                {
                    if (OverBudget()) { frames++; yield return null; }
                    for (int x = 0; x < f.W; x++)
                    {
                        int n = 0, of = 0;
                        for (int zz = z * k; zz < z * k + k && zz < m.H; zz++)
                            for (int xx = x * k; xx < x * k + k && xx < m.W; xx++)
                            {
                                n++;
                                if (m.At(xx, zz)) of++;
                            }
                        if (n > 0 && of * 2 >= n) { f.Forest[z * f.W + x] = true; f.Cells++; }
                    }
                }
                made.Add(f);
            }

            // 2. Chunks: ground heights and normals once, then the whole mesh.
            Material mat = Mat();
            int chunks = 0, tris = 0;
            for (int t = 0; t < made.Count; t++)
            {
                Field f = made[t];
                if (f.T == null) continue;
                f.Root = new GameObject("NDR_FarForest_" + f.T.name);
                f.Root.SetActive(false);
                ViewDistance.KeepVisible(f.Root);   // big sheets, never prop-culled
                TerrainData data = f.T.terrainData;
                Vector3 org = f.T.GetPosition(), size = data.size;
                for (int cz = 0; cz < f.H; cz += ChunkCells)
                    for (int cx = 0; cx < f.W; cx += ChunkCells)
                    {
                        if (OverBudget()) { frames++; yield return null; }
                        if (f.T == null || f.Root == null) break;
                        Chunk ch = new Chunk();
                        ch.F = f;
                        ch.CX = cx; ch.CZ = cz;
                        ch.NX = Math.Min(ChunkCells, f.W - cx);
                        ch.NZ = Math.Min(ChunkCells, f.H - cz);
                        for (int z = 0; z < ch.NZ; z++)
                            for (int x = 0; x < ch.NX; x++)
                                if (f.Forest[(cz + z) * f.W + cx + x]) ch.Cells++;
                        if (ch.Cells == 0) continue;
                        int vw = ch.NX + 1, vh = ch.NZ + 1;
                        ch.Ground = new float[vw * vh];
                        ch.Normal = new Vector3[vw * vh];
                        ch.MinY = float.MaxValue; ch.MaxY = float.MinValue;
                        for (int z = 0; z < vh; z++)
                            for (int x = 0; x < vw; x++)
                            {
                                float wx = f.X0 + (cx + x) * f.Cell, wz = f.Z0 + (cz + z) * f.Cell;
                                float y = org.y + f.T.SampleHeight(new Vector3(wx, 0f, wz));
                                ch.Ground[z * vw + x] = y;
                                Vector3 n = data.GetInterpolatedNormal(
                                    Mathf.Clamp01((wx - org.x) / size.x), Mathf.Clamp01((wz - org.z) / size.z));
                                ch.Normal[z * vw + x] = (n + Vector3.up).normalized;
                                ch.MinY = Mathf.Min(ch.MinY, y - SkirtSinkU);
                                ch.MaxY = Mathf.Max(ch.MaxY, y + f.Top * (1f + Jitter));
                            }
                        ch.X0 = f.X0 + cx * f.Cell;
                        ch.Z0 = f.Z0 + cz * f.Cell;
                        ch.X1 = ch.X0 + ch.NX * f.Cell;
                        ch.Z1 = ch.Z0 + ch.NZ * f.Cell;
                        GameObject go = new GameObject("c" + cx + "_" + cz);
                        go.transform.SetParent(f.Root.transform, false);
                        go.transform.position = new Vector3(ch.X0, 0f, ch.Z0);
                        ch.Mf = go.AddComponent<MeshFilter>();
                        ch.R = go.AddComponent<MeshRenderer>();
                        ch.R.sharedMaterial = mat;
                        ch.R.shadowCastingMode = ShadowCastingMode.Off;
                        ch.R.receiveShadows = false;
                        ch.R.lightProbeUsage = LightProbeUsage.Off;
                        ch.R.reflectionProbeUsage = ReflectionProbeUsage.Off;
                        ch.R.enabled = false;
                        ch.Full = new Mesh();
                        ch.Full.name = "NDR_FarForest";
                        ch.FullTris = Fill(ch, ch.Full, Vector3.zero, float.MinValue);
                        ch.Full.UploadMeshData(true);
                        ch.Mf.sharedMesh = ch.Full;
                        f.Chunks.Add(ch);
                        chunks++;
                        tris += ch.FullTris;
                    }
            }

            // 3. Swap in one step.
            Clear();
            for (int t = 0; t < made.Count; t++)
                if (made[t].Root != null) made[t].Root.SetActive(true);
            _fields = made;
            _totalChunks = chunks;
            _dirty = true;
            StringBuilder sb = new StringBuilder();
            for (int t = 0; t < made.Count; t++)
            {
                Field f = made[t];
                if (f.T == null) continue;
                sb.Append(t == 0 ? "" : ", ").Append(f.T.name).Append(' ').Append(f.W).Append('x').Append(f.H)
                  .Append(" cells, ").Append((100.0 * f.Cells / Math.Max(1, f.W * f.H)).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" % forest, top ").Append((f.Top / K).ToString("0", CultureInfo.InvariantCulture)).Append(" m");
            }
            RevivalPlugin.L.LogInfo("FarForest: " + Names[_builtLevel] + " canopy of "
                + (made.Count > 0 ? (made[0].Cell / K).ToString("0", CultureInfo.InvariantCulture) : "?")
                + " m cells - " + sb + "; " + chunks + " chunks, " + tris + " triangles, built in "
                + (Time.realtimeSinceStartup - started).ToString("0.0", CultureInfo.InvariantCulture)
                + " s over " + (frames + 1) + " frame(s).");
        }

        static void Clear()
        {
            for (int i = 0; i < _fields.Count; i++)
            {
                Field f = _fields[i];
                for (int c = 0; c < f.Chunks.Count; c++)
                {
                    if (f.Chunks[c].Full != null) UnityEngine.Object.Destroy(f.Chunks[c].Full);
                    if (f.Chunks[c].Part != null) UnityEngine.Object.Destroy(f.Chunks[c].Part);
                }
                if (f.Root != null) UnityEngine.Object.Destroy(f.Root);
            }
            _fields = new List<Field>();
            _queue.Clear();
            _totalChunks = _sWhole = _sEdge = _sTris = 0;
        }

        static bool ForestAtWorld(float x, float z, Field except)
        {
            List<Field> list = _building ?? _fields;
            for (int i = 0; i < list.Count; i++)
            {
                Field f = list[i];
                if (f == except || f.Forest == null) continue;
                int cx = Mathf.FloorToInt((x - f.X0) / f.Cell), cz = Mathf.FloorToInt((z - f.Z0) / f.Cell);
                if (cx >= 0 && cz >= 0 && cx < f.W && cz < f.H) return f.Forest[cz * f.W + cx];
            }
            return false;
        }

        // ============================================================ mesh

        static readonly List<Vector3> _v = new List<Vector3>();
        static readonly List<Vector3> _n = new List<Vector3>();
        static readonly List<Vector2> _uv = new List<Vector2>();
        static readonly List<int> _t = new List<int>();
        static int[] _map = new int[0];

        /// <summary>Fills `mesh` with the chunk's forest cells whose centre is
        /// farther than `cut` from `cam` (cut = MinValue: every cell). Returns
        /// the triangle count.</summary>
        static int Fill(Chunk ch, Mesh mesh, Vector3 cam, float cut)
        {
            Field f = ch.F;
            int vw = ch.NX + 1;
            int nv = vw * (ch.NZ + 1);
            if (_map.Length < nv) _map = new int[nv];
            for (int i = 0; i < nv; i++) _map[i] = -1;
            _v.Clear(); _n.Clear(); _uv.Clear(); _t.Clear();
            float uvs = 1f / (NoiseTexelM * K * NoiseSize);
            bool all = cut == float.MinValue;
            float cut2 = cut > 0f ? cut * cut : 0f;

            for (int z = 0; z < ch.NZ; z++)
                for (int x = 0; x < ch.NX; x++)
                {
                    int gx = ch.CX + x, gz = ch.CZ + z;
                    if (!f.Forest[gz * f.W + gx]) continue;
                    if (!all)
                    {
                        int a = z * vw + x;
                        float gy = (ch.Ground[a] + ch.Ground[a + 1] + ch.Ground[a + vw] + ch.Ground[a + vw + 1]) * 0.25f + f.Top;
                        float ddx = ch.X0 + (x + 0.5f) * f.Cell - cam.x;
                        float ddz = ch.Z0 + (z + 0.5f) * f.Cell - cam.z;
                        float ddy = gy - cam.y;
                        if (ddx * ddx + ddy * ddy + ddz * ddz <= cut2) continue;
                    }
                    int v00 = Top(ch, x, z, uvs), v10 = Top(ch, x + 1, z, uvs);
                    int v01 = Top(ch, x, z + 1, uvs), v11 = Top(ch, x + 1, z + 1, uvs);
                    _t.Add(v00); _t.Add(v01); _t.Add(v11);
                    _t.Add(v00); _t.Add(v11); _t.Add(v10);
                    // Skirts towards open land (outward faces, clockwise seen from outside).
                    if (!f.At(gx, gz - 1)) Skirt(ch, x, z, x + 1, z, new Vector3(0f, 0f, -1f), uvs);
                    if (!f.At(gx, gz + 1)) Skirt(ch, x + 1, z + 1, x, z + 1, new Vector3(0f, 0f, 1f), uvs);
                    if (!f.At(gx - 1, gz)) Skirt(ch, x, z + 1, x, z, new Vector3(-1f, 0f, 0f), uvs);
                    if (!f.At(gx + 1, gz)) Skirt(ch, x + 1, z, x + 1, z + 1, new Vector3(1f, 0f, 0f), uvs);
                }

            mesh.Clear();
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            return _t.Count / 3;
        }

        static float CanopyY(Chunk ch, int x, int z)
        {
            Field f = ch.F;
            int gx = ch.CX + x, gz = ch.CZ + z;
            float h = f.Top;
            if (!f.At(gx - 1, gz - 1) || !f.At(gx, gz - 1) || !f.At(gx - 1, gz) || !f.At(gx, gz)) h *= EdgeFactor;
            uint s = (uint)(gx * 73856093) ^ (uint)(gz * 19349663);
            s ^= s >> 13; s *= 0x5bd1e995; s ^= s >> 15;
            h *= 1f + Jitter * ((s & 1023) / 511.5f - 1f);
            return ch.Ground[z * (ch.NX + 1) + x] + h;
        }

        static int Top(Chunk ch, int x, int z, float uvs)
        {
            int i = z * (ch.NX + 1) + x;
            if (_map[i] >= 0) return _map[i];
            float lx = x * ch.F.Cell, lz = z * ch.F.Cell;
            _map[i] = _v.Count;
            _v.Add(new Vector3(lx, CanopyY(ch, x, z), lz));
            _n.Add(ch.Normal[i]);
            _uv.Add(new Vector2((ch.X0 + lx) * uvs, (ch.Z0 + lz) * uvs));
            return _map[i];
        }

        static void Skirt(Chunk ch, int ax, int az, int bx, int bz, Vector3 outward, float uvs)
        {
            int vw = ch.NX + 1;
            float cell = ch.F.Cell;
            Vector3 n = (outward + Vector3.up * 0.3f).normalized;
            float tA = CanopyY(ch, ax, az), tB = CanopyY(ch, bx, bz);
            float gA = ch.Ground[az * vw + ax] - SkirtSinkU, gB = ch.Ground[bz * vw + bx] - SkirtSinkU;
            float u0 = (ch.X0 + ax * cell + ch.Z0 + az * cell) * uvs;
            float u1 = (ch.X0 + bx * cell + ch.Z0 + bz * cell) * uvs;
            int b = _v.Count;
            _v.Add(new Vector3(ax * cell, gA, az * cell));  // bottom A
            _v.Add(new Vector3(ax * cell, tA, az * cell));  // top A
            _v.Add(new Vector3(bx * cell, tB, bz * cell));  // top B
            _v.Add(new Vector3(bx * cell, gB, bz * cell));  // bottom B
            for (int i = 0; i < 4; i++) _n.Add(n);
            _uv.Add(new Vector2(u0, gA * uvs)); _uv.Add(new Vector2(u0, tA * uvs));
            _uv.Add(new Vector2(u1, tB * uvs)); _uv.Add(new Vector2(u1, gB * uvs));
            _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
            _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
        }

        // ============================================================ material

        static Material Mat()
        {
            string colour = _cfgColour == null ? "" : _cfgColour.Value;
            if (_mat != null && colour == _matColour) return _mat;
            _matColour = colour;
            if (_mat == null)
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                _mat = new Material(shader);
                _mat.name = "NDR_FarForest";
                _mat.mainTexture = Noise();
                if (_mat.HasProperty("_Glossiness")) _mat.SetFloat("_Glossiness", 0f);
                if (_mat.HasProperty("_Metallic")) _mat.SetFloat("_Metallic", 0f);
            }
            _mat.color = ParseColour(colour, new Color(0.20f, 0.27f, 0.13f));
            return _mat;
        }

        static Color ParseColour(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            string[] p = s.Split(',');
            if (p.Length < 3) return fallback;
            float[] c = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]))
                    return fallback;
            return new Color(Mathf.Clamp01(c[0]), Mathf.Clamp01(c[1]), Mathf.Clamp01(c[2]), 1f);
        }

        /// <summary>Blotchy crown noise, tileable: two octaves of value noise,
        /// brightness 0.7..1, a slight yellow/blue shift.</summary>
        static Texture2D Noise()
        {
            int n = NoiseSize;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, true);
            tex.name = "NDR_FarForestNoise";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 2;
            System.Random rnd = new System.Random(4711);
            float[] g1 = Lattice(rnd, 16), g2 = Lattice(rnd, 64), g3 = Lattice(rnd, 8);
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float v = 0.65f * Sample(g1, 16, x, y, n) + 0.35f * Sample(g2, 64, x, y, n);
                    float hue = Sample(g3, 8, x, y, n) - 0.5f;
                    float b = 0.7f + 0.3f * v;
                    px[y * n + x] = new Color32((byte)(255f * Mathf.Clamp01(b * (1f + 0.10f * hue))),
                        (byte)(255f * b), (byte)(255f * Mathf.Clamp01(b * (1f - 0.12f * hue))), 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        static float[] Lattice(System.Random rnd, int g)
        {
            float[] a = new float[g * g];
            for (int i = 0; i < a.Length; i++) a[i] = (float)rnd.NextDouble();
            return a;
        }

        static float Sample(float[] a, int g, int x, int y, int n)
        {
            float fx = (float)x * g / n, fy = (float)y * g / n;
            int x0 = (int)fx, y0 = (int)fy;
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            int x1 = (x0 + 1) % g, y1 = (y0 + 1) % g;
            x0 %= g; y0 %= g;
            float top = Mathf.Lerp(a[y0 * g + x0], a[y0 * g + x1], tx);
            float bot = Mathf.Lerp(a[y1 * g + x0], a[y1 * g + x1], tx);
            return Mathf.Lerp(top, bot, ty);
        }

        // ============================================================ bench

        // 0 none, 1 on, 2 off. Settle 2 s, sample 5 s each.
        const float BenchSettle = 2f, BenchSample = 5f;
        static int _bench;
        static bool _benchSampling;
        static float _benchPhaseStart;
        static int _benchFrames;
        static double _benchMs, _benchOwn, _benchOn, _benchOnOwn;
        static int _benchTris;
        static string _benchResult;

        /// <summary>Admin panel: frame time with the far canopy on and off at
        /// this spot (~14 s, hold still).</summary>
        internal static string Bench()
        {
            if (Level == 0) return "Far forest is Off ([FarForest] Quality or the F2 window).";
            if (_fields.Count == 0) return "Far forest: no canopy built yet (no terrain trees loaded?).";
            if (_bench != 0) return "Far forest bench already running.";
            _benchResult = null;
            SetBenchPhase(1);
            return "Far forest bench: hold still ~14 s (canopy on, then off).";
        }

        static void SetBenchPhase(int phase)
        {
            _bench = phase;
            _benchSampling = false;
            _benchPhaseStart = Time.realtimeSinceStartup;
            _benchFrames = 0;
            _benchMs = _benchOwn = 0.0;
            bool show = phase != 2;
            for (int i = 0; i < _fields.Count; i++)
                if (_fields[i].Root != null) _fields[i].Root.SetActive(show);
        }

        static void BenchStep()
        {
            if (_bench == 0) return;
            float age = Time.realtimeSinceStartup - _benchPhaseStart;
            if (!_benchSampling)
            {
                if (age >= BenchSettle) { _benchSampling = true; _benchPhaseStart = Time.realtimeSinceStartup; }
                return;
            }
            _benchFrames++;
            _benchMs += Time.unscaledDeltaTime * 1000.0;
            if (age < BenchSample) return;
            double avg = _benchMs / Math.Max(1, _benchFrames);
            if (_bench == 1)
            {
                _benchOn = avg;
                _benchOnOwn = _benchOwn / Math.Max(1, _benchFrames);
                _benchTris = _sTris;
                SetBenchPhase(2);
                return;
            }
            SetBenchPhase(0);
            _benchResult = "Far forest bench (" + Names[Level] + ", " + (_sWhole + _sEdge) + " chunks, "
                + _benchTris + " tris in range): on " + F2(_benchOn) + " ms (" + F1(1000.0 / Math.Max(0.01, _benchOn))
                + " FPS), off " + F2(avg) + " ms (" + F1(1000.0 / Math.Max(0.01, avg)) + " FPS), cost "
                + F2(_benchOn - avg) + " ms/frame, own tick " + F3(_benchOnOwn) + " ms.";
            RevivalPlugin.L.LogInfo("[FarForest] " + _benchResult);
        }

        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        /// <summary>Admin panel line: the bench result, else the live state.</summary>
        internal static string Status()
        {
            if (_bench != 0) return "far forest bench: " + (_bench == 1 ? "canopy on" : "canopy off") + "...";
            if (_benchResult != null) return _benchResult;
            int level = Level;
            if (level == 0) return "far forest Off";
            if (_build != null || (_fields.Count == 0 && NpcDistance.Masks.Count == 0))
                return "far forest " + Names[level] + ": building...";
            float td = 0f;
            for (int i = 0; i < _fields.Count; i++) if (_fields[i].T != null) { td = _fields[i].T.treeDistance; break; }
            return "far forest " + Names[level] + ": " + _fields.Count + " terrain(s), " + _totalChunks
                + " chunks, drawn " + _sWhole + " + " + _sEdge + " edge, " + (_sTris / 1000) + "k tris, trees to "
                + (td / K).ToString("0", CultureInfo.InvariantCulture) + " m, " + F3(_tickMs) + " ms";
        }
    }
}

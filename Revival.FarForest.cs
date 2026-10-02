// Next Day: Survival - Revival Toolkit
//
// FAR FOREST (task P3, replaces the N2b canopy boxes). Forest past the tree
// draw distance looks like the game's own forest, just far away.
//
// Why: the terrain draws its trees only out to Terrain.treeDistance (1000 u =
// 357 m on foot at ViewDistance Medium, x2.5 in FlightView); the far clip is
// further out. Everything between was bare splat terrain ("desert"), and the
// N2b answer - a green sheet with vertical skirts - read as green boxes.
//
// WHAT IT DOES (no per-frame work, the engine does the hand-over):
//
// 1. CANOPY GROUND LAYER. Every terrain that draws the ground (GWTerrain2,
//    EastTileGround) gets ONE extra splat layer, painted from the N2 forest
//    mask (NpcDistance.Masks, the cells of the "hidden in forest" rule; the
//    union of every tree terrain over that ground). Its texture is built at
//    load: mip 0-2 are the forest floor layer that dominates under the forest
//    (vanilla: Ter3), tiled 2 x 2 at twice its tile size - near, the ground
//    under the trees looks as before. From mip 3 on it blends into a dark
//    canopy seen from above: crowns stamped from the real tree prototypes
//    rendered from above (light and shadow), tinted with the real leaf
//    textures. The GPU picks the mip by distance, so the far ground turns to
//    canopy smoothly from ~50 m to ~220 m - under the drawn trees, long before
//    they end: no ring, no colour jump. Lit, shaded and fogged by the terrain
//    shader itself (vanilla haze). Both terrains have one free slot in their
//    last splat pass (17 and 13 layers), so the layer costs no extra pass.
//
// 2. SILHOUETTE CARDS. Where forest meets the sky - forest edges, ridges and
//    hilltops (ground 4 m over the ring 60 m around), plus every 2nd interior
//    tree on Normal - the REAL tree instances get a crossed, double-sided
//    impostor card of their own prototype (rendered from the side at load,
//    procedural shape when the render fails), same place, same size.
//    Legacy Cutout/Diffuse (in the game build), up normals, vanilla fog.
//    128 m chunks, one LODGroup each: LOD0 is an empty mesh (inside the tree
//    distance), LOD1 the cards. The switch sits a half chunk INSIDE the tree
//    distance, so cards overlap the terrain's own billboards of the same trees
//    for a moment and a gap can never open. The thresholds are re-set (time-
//    sliced) only when the tree distance, the FOV or the LOD bias moved 4 %.
//
// Cost: the build is sliced (0.4 ms a frame) and runs once per forest mask;
// a few single hitches at the end (alphamap upload, one per control texture;
// one prototype render per frame). Per frame: three float compares.
//
// Setting: [FarForest] Quality Off / Low / Normal (default Normal, also F2):
// Low = edge and ridge cards only. Off swaps the canopy texture for the plain
// forest floor at once and hides the cards. [FarForest] Tint multiplies the
// colours taken from the game's tree textures.
//
// Admin: "Far forest bench" (frame time on vs off), "Far forest shots"
// (two screenshots from the same spot, after and before, in <game>/NDR_Shots).
//
// Seams: RevivalPlugin.Awake (BindConfig, Install), RevivalPlugin.Update
// (Tick, slot S_FarForestT), NpcDistance (Masks, MaskVersion, PumpMask),
// TerrainSurface.GetTextureMix postfix (our layer counts as its source layer
// for footsteps/impacts), EastCrossings.Paint (AddedLayers), EastWorld
// (ForgetLayers), Admin panel (Bench, Shots, Status), Settings (Level).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static partial class FarForest
    {
        /// <summary>World units per real metre (PlayerAn2.K).</summary>
        const float K = 2.8f;

        const float SliceMs = 0.4f;             // build budget per frame (once per mask)
        const float LodSliceMs = 0.05f;         // LOD re-threshold budget per frame
        const float LodChange = 0.04f;          // re-threshold after 4 % tree distance / FOV / bias
        const float ChunkM = 128f;              // card chunk (one LODGroup, one draw)
        const float MinTreeM = 2.5f;            // shorter prototypes (bushes, cannabis) get no card
        const float EdgeM = 12f;                // open land this close = forest edge
        const float RidgeRingM = 60f, RidgeRiseM = 4f;
        const int SlotW = 128, SlotH = 256, AtlasCols = 8, AtlasRows = 2;
        const int MaxSlots = AtlasCols * AtlasRows;
        const int TopSize = 64;
        const int RenderLayer = 31;             // like the gas launcher icon rig
        const int TileScale = 2;                // canopy layer tile = 2 x the source layer's
        const float BlendNearM = 50f, BlendFarM = 220f;   // mip hand-over, near floor -> far canopy
        const int MaxTexSize = 2048;
        const int Block = 64;                   // alphamap paint block, direct on the control textures
        const int ApiBlock = 256;               // the same through SetAlphamaps (an upload per block)
        const float Cutoff = 0.5f;
        static readonly Color LeafFallback = new Color(0.24f, 0.31f, 0.14f, 1f);

        internal static readonly string[] Names = { "Off", "Low", "Normal" };
        static readonly int[] InteriorEvery = { 0, 0, 2 };   // interior cards: every n-th tree (0 = none)

        // ============================================================ config

        static ConfigEntry<string> _cfgQuality, _cfgTint;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgQuality = cfg.Bind("FarForest", "Quality", "Normal",
                "Forest past the tree draw distance: Normal (canopy ground + tree cards on forest "
                + "edges, ridges and every 2nd tree inside), Low (canopy ground + cards on edges "
                + "and ridges only) or Off (bare terrain past the trees, the game's own). Built "
                + "from the same forest mask that hides men in the forest.");
            _cfgTint = cfg.Bind("FarForest", "Tint", "1,1,1",
                "Far forest colour multiplier R,G,B (0..2; 1 = the colours of the game's own tree "
                + "textures). Takes effect with the next build (map load or quality change).");
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

        /// <summary>The far forest wants the forest mask built.</summary>
        internal static bool Wanted { get { return Level > 0; } }

        // ============================================================ state

        /// <summary>A terrain that draws the ground and carries our layer.</summary>
        sealed class Ground
        {
            public Terrain T;
            public TerrainData D;
            public int N0, Src, Layer;          // layers before ours, source layer, our index
            public SplatPrototype[] Canopy, Plain;
            public Texture2D Tex;
            public int Version = -1;            // mask version painted
            public int Forest;                  // painted texels (w > 0)
            public string SrcName;
        }

        /// <summary>A tree prototype and its impostor.</summary>
        sealed class Slot
        {
            public GameObject Prefab;
            public string Name;
            public int Uses, Index = -1;        // Index = atlas slot, -1 = no card of its own
            public bool Conifer, Rendered, Measured;
            public float H, W, Bottom;          // prefab bounds over the pivot, u
            public float X1, Y0, Y1;            // card rectangle over the pivot, u
            public Color Leaf = LeafFallback;
            public Color32[] Side, Top;         // SlotW x SlotH, TopSize^2 (Top null = procedural crown)
            public Slot Card;                   // the slot whose image this prototype uses
        }

        sealed class Chunk
        {
            public Terrain T;
            public GameObject Go;
            public LODGroup G;
            public Renderer[] Near, Far;
            public Vector3 Ref;
            public float Size, Half;
            public int Cards, Tris;
        }

        struct Card
        {
            public Vector3 P;
            public float Ws, Hs, Yaw;
            public Slot S;
        }

        static readonly Dictionary<TerrainData, Ground> _grounds = new Dictionary<TerrainData, Ground>();
        static List<Chunk> _chunks = new List<Chunk>();
        static GameObject _root;
        static readonly Dictionary<GameObject, Slot> _slots = new Dictionary<GameObject, Slot>();
        static Texture2D _atlas;
        static Material _cardMat;
        static string _slotSig = "";

        static IEnumerator _build;
        static int _builtVersion = -1, _builtLevel = -1, _buildFrames;
        static bool _look;                      // canopy textures + cards shown
        static bool _warned;
        static string _lastBuild = "";
        static readonly Stopwatch _sw = new Stopwatch();
        static double _tickMs;
        static int _sCards, _sTris, _sRendered, _sSlots;

        // LOD thresholds as applied
        static float _lodFov = -1f, _lodBias = -1f;
        static readonly List<Terrain> _lodTerrains = new List<Terrain>();
        static readonly List<float> _lodTd = new List<float>();
        static int _lodNext;

        // ============================================================ seams

        /// <summary>Footsteps, impacts and grip read the splat index under a
        /// point (TerrainSurface.GetTextureMix). Our layer is folded onto its
        /// source layer and the array keeps the vanilla length, so
        /// GetMaterialTypeBySplatMapIndex never sees an index it does not
        /// know.</summary>
        internal static void Install(Harmony h)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("TerrainSurface");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "GetTextureMix", new Type[] { typeof(Vector3) }, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("FarForest: TerrainSurface.GetTextureMix not found - canopy layer not folded for footsteps.");
                    return;
                }
                h.Patch(m, null, new HarmonyMethod(typeof(FarForest).GetMethod("MixPostfix", BindingFlags.Static | BindingFlags.NonPublic)),
                    null, null, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("FarForest: GetTextureMix patch failed - " + ex.Message);
            }
        }

        static void MixPostfix(ref float[] __result)
        {
            if (__result == null || _grounds.Count == 0) return;
            try
            {
                Terrain act = Terrain.activeTerrain;
                if (act == null) return;
                Ground g;
                if (!_grounds.TryGetValue(act.terrainData, out g) || __result.Length <= g.N0) return;
                float[] r = new float[g.N0];
                Array.Copy(__result, r, g.N0);
                for (int i = g.N0; i < __result.Length; i++) r[g.Src] += __result[i];
                __result = r;
            }
            catch { }
        }

        // Static edge scenery must bind after P3 has appended/painted its layer.
        internal static bool GroundReady
        {
            get { return !Wanted || (_build == null && _builtVersion == NpcDistance.MaskVersion
                && NpcDistance.Masks.Count > 0); }
        }
        internal static int GroundRevision { get { return _groundRevision; } }
        static int _groundRevision;

        /// <summary>Layers this file added to `d` (EastCrossings' layer-count
        /// guard).</summary>
        internal static int AddedLayers(TerrainData d)
        {
            Ground g;
            return d != null && _grounds.TryGetValue(d, out g) && d.alphamapLayers > g.N0 ? d.alphamapLayers - g.N0 : 0;
        }

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
            ShotStep();
            BenchStep();
            int level = Level;
            if (level == 0)
            {
                if (_build != null) { _build = null; _builtLevel = -1; }
                if (_look && _bench == 0 && _shot == 0) SetLook(false);
                return;
            }
            // The mask is built by NpcDistance; pumped from here as well, in
            // case its Harmony install failed and its Tick never runs.
            NpcDistance.PumpMask();

            if (_build == null && (NpcDistance.MaskVersion != _builtVersion || level != _builtLevel))
            {
                if (level == _builtLevel && KeepAfterEdit()) _builtVersion = NpcDistance.MaskVersion;
                else
                {
                    _builtVersion = NpcDistance.MaskVersion;
                    _builtLevel = level;
                    _builtMasks = NpcDistance.Masks;
                    if (NpcDistance.Masks.Count > 0) { _buildFrames = 0; _build = Build(level); }
                    else Clear();
                }
            }
            if (_build != null)
            {
                bool more;
                try { more = _build.MoveNext(); }
                catch (Exception ex)
                {
                    more = false;
                    RevivalPlugin.L.LogWarning("FarForest: build failed - " + ex);
                }
                if (more) { _buildFrames++; return; }
                _build = null;
            }
            if (!_look && _bench == 0 && _shot == 0 && (_grounds.Count > 0 || _chunks.Count > 0)) SetLook(true);
            CheckLods();
            EdgeStep();
        }

        // X perf-fix: an admin/editor helipad site that clears ~1500 trees moved
        // NpcDistance's tree-count signature, and the far forest was built again
        // from scratch - ground paint and 74,000 cards in 913 chunks, 139 s over
        // 2780 frames on 6.63.0 (FarForest.Tick 134 KB/frame, 39 ms peaks, the
        // control-texture uploads and basemap errors on top) while the game ran
        // at 8-24 FPS. A change of at most ForestEdit.Keep of the forest cells
        // the build was made from keeps the build; NPC hiding takes the new
        // masks at once. Cards stay over a cleared site - they are only drawn
        // past the tree distance. Level change, bench and relog still rebuild.
        static List<NpcDistance.Mask> _builtMasks;      // swapped whole by NpcDistance, never edited
        static string _kept = "";

        static bool KeepAfterEdit()
        {
            List<NpcDistance.Mask> was = _builtMasks, now = NpcDistance.Masks;
            if (was == null || now == null || was == now || now.Count == 0 || was.Count != now.Count) return false;
            if (_chunks.Count == 0 && _grounds.Count == 0) return false;
            int changed = 0, forest = 0;
            for (int i = 0; i < now.Count; i++)
            {
                NpcDistance.Mask a = was[i], b = now[i];
                if (a == null || b == null || a.Terrain == null || a.Terrain != b.Terrain
                    || a.W != b.W || a.H != b.H || a.X0 != b.X0 || a.Z0 != b.Z0 || a.Cell != b.Cell) return false;
                int d = ForestEdit.BitDiff(a.Bits, b.Bits);
                if (d < 0) return false;
                changed += d;
                forest += a.Cells;
            }
            if (!ForestEdit.Keep(changed, forest)) return false;
            _kept = changed + " of " + forest + " forest cells changed since the build, kept";
            RevivalPlugin.L.LogInfo("FarForest: forest masks changed by " + changed + " of " + forest
                + " cells (" + (100.0 * changed / Math.Max(1, forest)).ToString("0.00", CultureInfo.InvariantCulture)
                + " %) - the far forest is kept as built, no rebuild (level change or relog rebuilds).");
            return true;
        }

        /// <summary>Canopy textures + cards on, or the plain forest floor and
        /// no cards (Off, bench, "before" shot). Swapping a prototype array of
        /// the same length keeps the paint.</summary>
        static void SetLook(bool on)
        {
            _look = on;
            _groundRevision++;
            foreach (Ground g in _grounds.Values)
            {
                if (g.T == null || g.D == null || g.T.terrainData != g.D || g.Canopy == null) continue;
                if (g.D.alphamapLayers != g.N0 + 1) continue;
                g.D.splatPrototypes = on ? g.Canopy : g.Plain;
            }
            if (_root != null) _root.SetActive(on);
        }

        static bool _bisectHid;

        /// <summary>X perf-bisect (admin Perf tab): the look off like the
        /// bench does it, and back on only if it was on. Tick is skipped by
        /// RevivalPlugin meanwhile, so nothing switches it in between.</summary>
        internal static void BisectLook(bool off)
        {
            if (off)
            {
                _bisectHid = _look;
                if (_look) SetLook(false);
            }
            else if (_bisectHid)
            {
                _bisectHid = false;
                if (!_look) SetLook(true);
            }
        }

        // ============================================================ LOD hand-over

        static void CheckLods()
        {
            if (_chunks.Count == 0) return;
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return;
            float fov = cam.fieldOfView, bias = QualitySettings.lodBias;
            bool changed = Moved(fov, _lodFov) || Moved(bias, _lodBias);
            for (int i = 0; !changed && i < _lodTerrains.Count; i++)
                if (_lodTerrains[i] != null && Moved(_lodTerrains[i].treeDistance, _lodTd[i])) changed = true;
            if (changed)
            {
                _lodFov = fov;
                _lodBias = bias;
                for (int i = 0; i < _lodTerrains.Count; i++)
                    if (_lodTerrains[i] != null) _lodTd[i] = _lodTerrains[i].treeDistance;
                _lodNext = 0;
            }
            if (_lodNext >= _chunks.Count) return;
            float tanHalf = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(_lodFov, 1f, 170f) * 0.5f);
            long t0 = Stopwatch.GetTimestamp();
            long limit = (long)(LodSliceMs * Stopwatch.Frequency / 1000.0);
            while (_lodNext < _chunks.Count)
            {
                ApplyLod(_chunks[_lodNext++], tanHalf, _lodBias);
                if (Stopwatch.GetTimestamp() - t0 > limit) break;
            }
        }

        static bool Moved(float now, float was)
        {
            return was <= 0f || Mathf.Abs(now - was) > LodChange * Mathf.Max(Mathf.Abs(was), 1e-3f);
        }

        /// <summary>LOD0 (empty) while the chunk's reference point is closer
        /// than its tree distance minus half a chunk, LOD1 (cards) beyond.
        /// Unity: relative height = size/2 * lodBias / (distance * tan(fov/2)).</summary>
        static readonly LOD[] _lods2 = new LOD[2];

        static void ApplyLod(Chunk ch, float tanHalf, float bias)
        {
            if (ch.G == null) return;
            float td = ch.T != null ? ch.T.treeDistance : 1000f;
            float d = Mathf.Max(20f * K, td - ch.Half);
            float h = Mathf.Clamp(ch.Size * 0.5f * Mathf.Max(0.01f, bias) / (d * tanHalf), 0.0005f, 0.99f);
            // W Perf1: one array for every chunk - SetLODs copies it.
            LOD[] lods = _lods2;
            lods[0] = new LOD(h, ch.Near);
            lods[1] = new LOD(0.00001f, ch.Far);
            ch.G.SetLODs(lods);
            ch.G.localReferencePoint = ch.Ref;
            ch.G.size = ch.Size;
        }

        // ============================================================ build

        static IEnumerator Build(int level)
        {
            float started = Time.realtimeSinceStartup;
            List<NpcDistance.Mask> masks = new List<NpcDistance.Mask>();
            for (int i = 0; i < NpcDistance.Masks.Count; i++)
            {
                NpcDistance.Mask m = NpcDistance.Masks[i];
                if (m != null && m.Terrain != null && m.Bits != null && m.Cells > 0) masks.Add(m);
            }
            if (masks.Count == 0) { Clear(); yield break; }

            // 1. Prototypes: usage, measure, leaf colour, impostors (one render a frame).
            IEnumerator e = BuildSlots(masks);
            while (e.MoveNext()) yield return null;

            // 2. The canopy layer on every ground terrain under a mask.
            StringBuilder gl = new StringBuilder();
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
            {
                Terrain t = all[i];
                if (!IsGround(t) || !Overlaps(t, masks)) continue;
                e = PaintGround(t, masks, gl);
                while (e.MoveNext()) yield return null;
            }
            List<TerrainData> gone = new List<TerrainData>();
            foreach (KeyValuePair<TerrainData, Ground> kv in _grounds)
                if (kv.Value.T == null || kv.Key == null) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++) _grounds.Remove(gone[i]);

            // 3. Cards at the real tree instances.
            List<Chunk> made = new List<Chunk>();
            GameObject root = new GameObject("NDR_FarForest");
            root.SetActive(false);
            e = BuildCards(masks, level, root, made);
            while (e.MoveNext()) yield return null;
            e = EdgeMeasureArea(masks, made);
            while (e.MoveNext()) yield return null;

            // 4. Swap in one step.
            ClearCards();
            _root = root;
            _chunks = made;
            ViewDistance.KeepVisible(_root);    // big chunks, never prop-culled
            _lodTerrains.Clear();
            _lodTd.Clear();
            for (int i = 0; i < made.Count; i++)
                if (made[i].T != null && !_lodTerrains.Contains(made[i].T)) { _lodTerrains.Add(made[i].T); _lodTd.Add(-1f); }
            _lodFov = -1f;                      // Step's CheckLods re-thresholds every chunk
            _look = false;                      // Step turns the look on (and the root active)

            _groundRevision++;
            _sCards = _sTris = 0;
            for (int i = 0; i < made.Count; i++) { _sCards += made[i].Cards; _sTris += made[i].Tris; }
            _lastBuild = Names[level] + ": " + (gl.Length > 0 ? gl.ToString() : "no ground terrain painted") + "; "
                + _sCards + " cards (" + _sTris + " tris) in " + made.Count + " chunks, " + _sSlots + " impostors ("
                + _sRendered + " rendered, the rest procedural)";
            RevivalPlugin.L.LogInfo("FarForest: " + _lastBuild + "; built in "
                + (Time.realtimeSinceStartup - started).ToString("0.0", CultureInfo.InvariantCulture)
                + " s over " + (_buildFrames + 1) + " frame(s).");
        }

        static bool IsGround(Terrain t)
        {
            if (t == null || !t.drawHeightmap || !t.isActiveAndEnabled || t.terrainData == null) return false;
            TerrainData d = t.terrainData;
            return d.alphamapLayers > 0 && d.alphamapWidth > 0 && d.splatPrototypes.Length > 0;
        }

        static bool Overlaps(Terrain t, List<NpcDistance.Mask> masks)
        {
            Vector3 o = t.GetPosition(), s = t.terrainData.size;
            for (int i = 0; i < masks.Count; i++)
            {
                NpcDistance.Mask m = masks[i];
                if (m.X0 < o.x + s.x && m.X0 + m.W * m.Cell > o.x && m.Z0 < o.z + s.z && m.Z0 + m.H * m.Cell > o.z) return true;
            }
            return false;
        }

        static bool ForestAt(List<NpcDistance.Mask> masks, float x, float z)
        {
            Vector3 p = new Vector3(x, 0f, z);
            for (int i = 0; i < masks.Count; i++)
                if (masks[i].Contains(p) && masks[i].Forest(p)) return true;
            return false;
        }

        static void Clear()
        {
            ClearCards();
            _lastBuild = "";
        }

        static void ClearCards()
        {
            EdgeDrop(); // also cancels a pending strip before its pool/root are replaced
            for (int i = 0; i < _chunks.Count; i++)
            {
                Chunk ch = _chunks[i];
                if (ch.Go == null) continue;
                MeshFilter[] mfs = ch.Go.GetComponentsInChildren<MeshFilter>(true);
                for (int j = 0; j < mfs.Length; j++) if (mfs[j].sharedMesh != null) UnityEngine.Object.Destroy(mfs[j].sharedMesh);
            }
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _chunks = new List<Chunk>();
            _lodNext = 0;
            _sCards = _sTris = 0;
        }

        // ============================================================ 1. prototypes

        static IEnumerator BuildSlots(List<NpcDistance.Mask> masks)
        {
            // Usage per prefab over all tree terrains.
            Dictionary<GameObject, int> uses = new Dictionary<GameObject, int>();
            List<TerrainData> seen = new List<TerrainData>();
            for (int t = 0; t < masks.Count; t++)
            {
                TerrainData d = masks[t].Terrain.terrainData;
                if (d == null || seen.Contains(d)) continue;
                seen.Add(d);
                TreePrototype[] protos = d.treePrototypes;
                int[] n = new int[protos.Length];
                TreeInstance[] inst = d.treeInstances;
                for (int i = 0; i < inst.Length; i++)
                {
                    int p = inst[i].prototypeIndex;
                    if (p >= 0 && p < n.Length) n[p]++;
                    if ((i & 4095) == 4095 && OverBudget()) yield return null;
                }
                for (int p = 0; p < protos.Length; p++)
                {
                    GameObject pf = protos[p].prefab;
                    if (pf == null) continue;
                    int had;
                    uses.TryGetValue(pf, out had);
                    uses[pf] = had + n[p];
                }
            }
            List<GameObject> order = new List<GameObject>(uses.Keys);
            order.Sort(delegate(GameObject a, GameObject b) { return uses[b].CompareTo(uses[a]); });
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < order.Count; i++) sig.Append(order[i].GetInstanceID()).Append(',');
            if (sig.ToString() == _slotSig && _atlas != null && _cardMat != null)
            {
                foreach (KeyValuePair<GameObject, int> kv in uses) { Slot s; if (_slots.TryGetValue(kv.Key, out s)) s.Uses = kv.Value; }
                yield break;
            }

            // Measure + render, most used first, one prototype a frame.
            _slots.Clear();
            List<Slot> cards = new List<Slot>();
            _sRendered = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Slot s = new Slot();
                s.Prefab = order[i];
                s.Name = order[i].name;
                s.Uses = uses[order[i]];
                _slots[order[i]] = s;
                if (s.Uses == 0) continue;
                bool wantImage = cards.Count < MaxSlots;
                try { Bake(s, wantImage); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("FarForest: prototype " + s.Name + " not baked - " + ex.Message);
                }
                if (s.Measured && s.H >= MinTreeM * K && wantImage)
                {
                    if (s.Side == null) Procedural(s);
                    else _sRendered++;
                    s.Index = cards.Count;
                    s.Card = s;
                    cards.Add(s);
                }
                yield return null;              // a prototype render is a hitch of its own
            }
            // Prototypes past the atlas use the closest slot of the same kind.
            foreach (Slot s in _slots.Values)
            {
                if (s.Card != null || !s.Measured || s.H < MinTreeM * K || cards.Count == 0) continue;
                Slot best = null;
                float bd = float.MaxValue;
                for (int i = 0; i < cards.Count; i++)
                {
                    float dd = Mathf.Abs(cards[i].H - s.H) + (cards[i].Conifer == s.Conifer ? 0f : 1000f);
                    if (dd < bd) { bd = dd; best = cards[i]; }
                }
                s.Card = best;
            }
            _sSlots = cards.Count;
            BuildAtlas(cards);
            _slotSig = sig.ToString();
        }

        /// <summary>Instantiates the prefab on an isolated layer under an
        /// inactive rig (no Awake), strips scripts and colliders, measures it,
        /// reads the leaf colour and renders the side and top impostors with
        /// its own light, no fog. Always restores the scene lights.</summary>
        static void Bake(Slot s, bool render)
        {
            GameObject rig = null;
            RenderTexture prevActive = RenderTexture.active;
            Light[] lights = null;
            int[] lightMasks = null;
            bool fog = RenderSettings.fog;
            AmbientMode ambMode = RenderSettings.ambientMode;
            Color amb = RenderSettings.ambientLight;
            try
            {
                rig = new GameObject("NDR_FarForest_Bake");
                rig.SetActive(false);
                rig.transform.position = new Vector3(0f, -6000f, 0f);
                GameObject inst = (GameObject)UnityEngine.Object.Instantiate(s.Prefab, rig.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;
                MonoBehaviour[] mbs = inst.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < mbs.Length; i++) if (mbs[i] != null) UnityEngine.Object.DestroyImmediate(mbs[i]);
                Collider[] cols = inst.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++) UnityEngine.Object.DestroyImmediate(cols[i]);
                Rigidbody[] rbs = inst.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < rbs.Length; i++) UnityEngine.Object.DestroyImmediate(rbs[i]);
                Transform[] all = inst.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++) all[i].gameObject.layer = RenderLayer;
                rig.SetActive(true);
                LODGroup lg = inst.GetComponentInChildren<LODGroup>();
                if (lg != null) lg.ForceLOD(0);

                Renderer[] rs = inst.GetComponentsInChildren<Renderer>(false);
                bool any = false;
                Bounds b = new Bounds();
                for (int i = 0; i < rs.Length; i++)
                {
                    if (!rs[i].enabled || rs[i] is ParticleSystemRenderer) continue;
                    if (!any) { b = rs[i].bounds; any = true; }
                    else b.Encapsulate(rs[i].bounds);
                }
                if (!any || b.size.y <= 0.01f) return;
                Vector3 piv = rig.transform.position;
                s.H = b.max.y - piv.y;
                s.Bottom = Mathf.Max(0f, b.min.y - piv.y);
                s.W = Mathf.Max(b.size.x, b.size.z);
                s.Measured = true;
                string ln = s.Name.ToLowerInvariant();
                s.Conifer = ln.Contains("fir") || ln.Contains("pine") || ln.Contains("spruce") || ln.Contains("el_")
                    || ln.Contains("sosn") || ln.Contains("conif") || s.H > 2.6f * s.W;
                s.Leaf = LeafColour(rs);
                if (!render || s.H < MinTreeM * K) return;

                // Isolated light: scene lights leave our layer, one key light
                // for it; flat grey ambient; no fog.
                lights = UnityEngine.Object.FindObjectsOfType<Light>();
                lightMasks = new int[lights.Length];
                for (int i = 0; i < lights.Length; i++)
                {
                    lightMasks[i] = lights[i].cullingMask;
                    lights[i].cullingMask = lightMasks[i] & ~(1 << RenderLayer);
                }
                GameObject lightGo = new GameObject("key");
                lightGo.transform.SetParent(rig.transform, false);
                lightGo.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
                Light key = lightGo.AddComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1f;
                key.shadows = LightShadows.None;
                key.cullingMask = 1 << RenderLayer;
                RenderSettings.fog = false;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f, 1f);

                GameObject camGo = new GameObject("cam");
                camGo.transform.SetParent(rig.transform, false);
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.cullingMask = 1 << RenderLayer;
                cam.renderingPath = RenderingPath.Forward;   // keeps the background (gas launcher icon)
                cam.useOcclusionCulling = false;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                float reach = b.extents.magnitude + 5f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = reach * 2f + 5f;

                // Side: image 1:2, centred on the bounds.
                float halfW = Mathf.Max(b.extents.x, b.extents.z);
                float ortho = Mathf.Max(b.extents.y, 2f * halfW) * 1.04f;
                cam.orthographicSize = ortho;
                camGo.transform.position = b.center - Vector3.forward * reach;
                camGo.transform.rotation = Quaternion.identity;
                float cover;
                Color32[] side = Grab(cam, SlotW, SlotH, out cover);
                if (!Plausible(side, cover)) return;
                s.X1 = ortho * 0.5f;
                s.Y0 = b.center.y - piv.y - ortho;
                s.Y1 = b.center.y - piv.y + ortho;
                s.Side = Normalise(side, s.Leaf);

                // Top: the crown seen from above (canopy ground).
                cam.orthographicSize = halfW * 1.04f;
                camGo.transform.position = b.center + Vector3.up * reach;
                camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                Color32[] top = Grab(cam, TopSize, TopSize, out cover);
                if (Plausible(top, cover)) s.Top = Normalise(top, s.Leaf);
                s.Rendered = true;
            }
            finally
            {
                if (lights != null)
                    for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].cullingMask = lightMasks[i];
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambMode;
                RenderSettings.ambientLight = amb;
                RenderTexture.active = prevActive;
                if (rig != null) UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        /// <summary>Renders twice (black, then white background): equal
        /// pixels are covered, the difference is the alpha, the black render
        /// divided by the alpha is the colour. Independent of what the tree
        /// shaders write into alpha.</summary>
        static Color32[] Grab(Camera cam, int w, int h, out float cover)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D img = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                cam.targetTexture = rt;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.Render();
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                Color32[] a = img.GetPixels32();
                cam.backgroundColor = new Color(1f, 1f, 1f, 1f);
                cam.Render();
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                Color32[] bw = img.GetPixels32();
                cam.targetTexture = null;
                int solid = 0;
                Color32[] o = new Color32[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    int diff = ((bw[i].r - a[i].r) + (bw[i].g - a[i].g) + (bw[i].b - a[i].b)) / 3;
                    int al = Mathf.Clamp(255 - diff, 0, 255);
                    if (al < 13) { o[i] = new Color32(0, 0, 0, 0); continue; }
                    float k = 255f / al;
                    o[i] = new Color32((byte)Mathf.Min(255f, a[i].r * k), (byte)Mathf.Min(255f, a[i].g * k),
                                       (byte)Mathf.Min(255f, a[i].b * k), (byte)al);
                    if (al >= 128) solid++;
                }
                cover = (float)solid / a.Length;
                return o;
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(img);
            }
        }

        /// <summary>A render that is nearly empty or full, black, or the
        /// magenta of a missing shader is not a tree.</summary>
        static bool Plausible(Color32[] px, float cover)
        {
            if (px == null || cover < 0.02f || cover > 0.95f) return false;
            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int i = 0; i < px.Length; i++)
                if (px[i].a >= 128) { r += px[i].r; g += px[i].g; b += px[i].b; n++; }
            if (n == 0) return false;
            r /= n * 255.0; g /= n * 255.0; b /= n * 255.0;
            if (r + g + b < 0.03) return false;
            if (r > 0.55 && b > 0.55 && g < 0.35) return false;
            return true;
        }

        /// <summary>Keeps the render's light and shadow and its hue (bark,
        /// yellow birch), but sets its mean brightness to the leaf texture's -
        /// the bake light (night, dusk) must not end up in the impostor.</summary>
        static Color32[] Normalise(Color32[] px, Color leaf)
        {
            double lum = 0, r = 0, g = 0, b = 0;
            int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 128) continue;
                lum += Lum(px[i]); r += px[i].r; g += px[i].g; b += px[i].b; n++;
            }
            if (n == 0) return px;
            float mean = (float)(lum / n) / 255f;
            float target = 0.299f * leaf.r + 0.587f * leaf.g + 0.114f * leaf.b;
            Color hue = new Color((float)(r / n) / 255f, (float)(g / n) / 255f, (float)(b / n) / 255f, 1f);
            // 30 % towards the leaf hue: a render in a tinted light (sunset) is pulled back.
            float hl = Mathf.Max(1e-3f, 0.299f * hue.r + 0.587f * hue.g + 0.114f * hue.b);
            Color shift = new Color(Mathf.Lerp(1f, leaf.r / target * hl / Mathf.Max(1e-3f, hue.r), 0.3f),
                                    Mathf.Lerp(1f, leaf.g / target * hl / Mathf.Max(1e-3f, hue.g), 0.3f),
                                    Mathf.Lerp(1f, leaf.b / target * hl / Mathf.Max(1e-3f, hue.b), 0.3f), 1f);
            float k = target / Mathf.Max(0.01f, mean);
            Color32[] o = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (c.a == 0) { o[i] = c; continue; }
                float l = Lum(c) / 255f;
                float lk = Mathf.Clamp(l / Mathf.Max(0.01f, mean), 0.3f, 1.8f) * target / Mathf.Max(1e-3f, l);
                o[i] = new Color32(B(c.r * lk * shift.r), B(c.g * lk * shift.g), B(c.b * lk * shift.b), c.a);
            }
            return o;
        }

        static float Lum(Color32 c) { return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; }
        static byte B(float v) { return (byte)Mathf.Clamp(v + 0.5f, 0f, 255f); }

        /// <summary>Average leaf albedo: the material that looks most like
        /// leaves (Soft Occlusion Leaves shader, leaf-ish name), its main
        /// texture averaged where it is opaque, times its colour.</summary>
        static Color LeafColour(Renderer[] rs)
        {
            Material best = null;
            int bestScore = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                Material[] ms = rs[i].sharedMaterials;
                for (int j = 0; j < ms.Length; j++)
                {
                    Material m = ms[j];
                    if (m == null || !m.HasProperty("_MainTex") || m.mainTexture == null) continue;
                    string sn = m.shader == null ? "" : m.shader.name.ToLowerInvariant();
                    string mn = m.name.ToLowerInvariant();
                    int score = 1;
                    if (sn.Contains("leaves") || sn.Contains("leaf")) score += 4;
                    if (mn.Contains("leaf") || mn.Contains("leav") || mn.Contains("list") || mn.Contains("needle")
                        || mn.Contains("crown") || mn.Contains("branch") || mn.Contains("hvo") || mn.Contains("fol")) score += 2;
                    if (sn.Contains("bark") || mn.Contains("bark") || mn.Contains("kora") || mn.Contains("trunk")) score -= 3;
                    if (score > bestScore) { bestScore = score; best = m; }
                }
            }
            if (best == null) return LeafFallback;
            Color avg = Average(best.mainTexture);
            if (avg.a <= 0f) return LeafFallback;
            Color tint = best.HasProperty("_Color") ? best.color : Color.white;
            return new Color(avg.r * tint.r, avg.g * tint.g, avg.b * tint.b, 1f);
        }

        /// <summary>Blit to 32 x 32 (the GPU picks the matching mip), average
        /// where alpha > 0.3. Works on the game's non-readable textures.</summary>
        static Color Average(Texture tex)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D img = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0f, 0f, 32f, 32f), 0, 0);
                Color32[] px = img.GetPixels32();
                double r = 0, g = 0, b = 0, w = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a < 77) continue;
                    double a = px[i].a / 255.0;
                    r += px[i].r * a; g += px[i].g * a; b += px[i].b * a; w += a;
                }
                if (w < 1.0) return new Color(0f, 0f, 0f, 0f);
                return new Color((float)(r / w / 255.0), (float)(g / w / 255.0), (float)(b / w / 255.0), 1f);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(img);
            }
        }

        /// <summary>No usable render: a drawn silhouette of the right kind and
        /// size - a jagged cone for conifers, a lumpy crown on a trunk for
        /// broadleaf trees - lit from the upper left.</summary>
        static void Procedural(Slot s)
        {
            Color32[] px = new Color32[SlotW * SlotH];
            Color leaf = s.Leaf;
            uint seed = (uint)s.Name.GetHashCode();
            for (int y = 0; y < SlotH; y++)
            {
                float v = (y + 0.5f) / SlotH;           // 0 bottom .. 1 top
                for (int x = 0; x < SlotW; x++)
                {
                    float u = (x + 0.5f) / SlotW * 2f - 1f;   // -1 .. 1
                    float nz = Hash01(seed, x / 3, y / 3);
                    bool crown, trunk;
                    if (s.Conifer)
                    {
                        float hw = Mathf.Pow(Mathf.Clamp01((1f - v) / 0.88f), 0.95f) * (0.72f + 0.28f * Frac(v * 9f));
                        if (v < 0.2f) hw *= 0.75f;      // no flared bottom row
                        crown = v > 0.12f && Mathf.Abs(u) < hw * (0.9f + 0.2f * nz);
                        trunk = Mathf.Abs(u) < 0.05f && v < 0.14f;
                    }
                    else
                    {
                        float dx = u / 0.95f, dy = (v - 0.62f) / 0.38f;
                        crown = dx * dx + dy * dy < 0.8f + 0.35f * nz;
                        trunk = Mathf.Abs(u) < 0.05f && v < 0.4f;
                    }
                    if (crown)
                    {
                        float light = 0.75f + 0.35f * (v - 0.5f) - 0.2f * u + 0.25f * (nz - 0.5f);
                        px[y * SlotW + x] = new Color32(B(leaf.r * light * 255f), B(leaf.g * light * 255f), B(leaf.b * light * 255f), 255);
                    }
                    else if (trunk) px[y * SlotW + x] = new Color32(62, 52, 40, 255);
                }
            }
            s.Side = px;
            s.X1 = s.W * 0.5f;
            s.Y0 = 0f;
            s.Y1 = s.H;
        }

        static float Frac(float v) { return v - Mathf.Floor(v); }

        static float Hash01(uint seed, int x, int y)
        {
            uint h = seed ^ (uint)(x * 73856093) ^ (uint)(y * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xffff) / 65535f;
        }

        /// <summary>Impostor atlas 1024 x 512 (8 x 2 slots). Transparent
        /// texels carry the slot's mean colour (no dark fringe); every mip
        /// keeps the level-0 coverage at the cutoff (cards do not thin out
        /// with distance).</summary>
        static void BuildAtlas(List<Slot> cards)
        {
            int aw = SlotW * AtlasCols, ah = SlotH * AtlasRows;
            if (_atlas != null) UnityEngine.Object.Destroy(_atlas);
            _atlas = new Texture2D(aw, ah, TextureFormat.RGBA32, true);
            _atlas.name = "NDR_FarForestCards";
            _atlas.wrapMode = TextureWrapMode.Clamp;
            _atlas.filterMode = FilterMode.Trilinear;
            _atlas.anisoLevel = 1;
            Color32[] px = new Color32[aw * ah];
            float[] cover0 = new float[cards.Count];
            for (int i = 0; i < cards.Count; i++)
            {
                Slot s = cards[i];
                int ox = (i % AtlasCols) * SlotW, oy = (i / AtlasCols) * SlotH;
                long r = 0, g = 0, b = 0; int n = 0, solid = 0;
                for (int k = 0; k < s.Side.Length; k++)
                    if (s.Side[k].a >= 128) { r += s.Side[k].r; g += s.Side[k].g; b += s.Side[k].b; n++; solid++; }
                Color32 fill = n == 0 ? new Color32(60, 75, 35, 0)
                    : new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
                cover0[i] = (float)solid / s.Side.Length;
                for (int y = 0; y < SlotH; y++)
                    for (int x = 0; x < SlotW; x++)
                    {
                        Color32 c = s.Side[y * SlotW + x];
                        // one texel of clear border: no bleeding into the next slot
                        if (x == 0 || y == 0 || x == SlotW - 1 || y == SlotH - 1 || c.a < 13) c = fill;
                        px[(oy + y) * aw + ox + x] = c;
                    }
            }
            _atlas.SetPixels32(px, 0);
            int w = aw, h = ah, level = 0;
            while (w > 1 || h > 1)
            {
                int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
                Color32[] dn = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0, cw = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Mathf.Min(w - 1, x * 2 + dx), sy = Mathf.Min(h - 1, y * 2 + dy);
                                Color32 c = px[sy * w + sx];
                                int wt = c.a + 1;
                                r += c.r * wt; g += c.g * wt; b += c.b * wt; cw += wt; a += c.a;
                            }
                        dn[y * nw + x] = new Color32((byte)(r / cw), (byte)(g / cw), (byte)(b / cw), (byte)(a / 4));
                    }
                level++;
                int sw = SlotW >> level, sh = SlotH >> level;
                if (sw >= 2 && sh >= 2)
                    for (int i = 0; i < cards.Count; i++)
                        KeepCoverage(dn, nw, (i % AtlasCols) * sw, (i / AtlasCols) * sh, sw, sh, cover0[i]);
                _atlas.SetPixels32(dn, level);
                px = dn; w = nw; h = nh;
            }
            _atlas.Apply(false, true);

            if (_cardMat == null)
            {
                Shader sh = Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse");
                if (sh == null) sh = Shader.Find("Transparent/Cutout/Diffuse");
                bool standard = false;
                if (sh == null) { sh = Shader.Find("Standard"); standard = true; }
                _cardMat = new Material(sh);
                _cardMat.name = "NDR_FarForestCards";
                if (standard)
                {
                    _cardMat.SetFloat("_Mode", 1f);
                    _cardMat.EnableKeyword("_ALPHATEST_ON");
                    _cardMat.renderQueue = 2450;
                    if (_cardMat.HasProperty("_Glossiness")) _cardMat.SetFloat("_Glossiness", 0f);
                }
                if (_cardMat.HasProperty("_Cutoff")) _cardMat.SetFloat("_Cutoff", Cutoff);
            }
            _cardMat.mainTexture = _atlas;
            _cardMat.color = Tint();
        }

        /// <summary>Scales one slot's alpha so the share of texels over the
        /// cutoff matches level 0 (binary search on the factor).</summary>
        static void KeepCoverage(Color32[] px, int stride, int x0, int y0, int w, int h, float target)
        {
            if (target <= 0f) return;
            float lo = 1f, hi = 4f;
            for (int it = 0; it < 8; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Coverage(px, stride, x0, y0, w, h, mid) < target) lo = mid; else hi = mid;
            }
            float k = (lo + hi) * 0.5f;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    Color32 c = px[y * stride + x];
                    c.a = (byte)Mathf.Min(255f, c.a * k);
                    px[y * stride + x] = c;
                }
        }

        static float Coverage(Color32[] px, int stride, int x0, int y0, int w, int h, float k)
        {
            int n = 0;
            float cut = Cutoff * 255f;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (px[y * stride + x].a * k >= cut) n++;
            return (float)n / (w * h);
        }

        static Color Tint()
        {
            string s = _cfgTint == null ? "" : _cfgTint.Value;
            Color c = Color.white;
            if (string.IsNullOrEmpty(s)) return c;
            string[] p = s.Split(',');
            if (p.Length < 3) return c;
            float[] v = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return c;
            return new Color(Mathf.Clamp(v[0], 0f, 2f), Mathf.Clamp(v[1], 0f, 2f), Mathf.Clamp(v[2], 0f, 2f), 1f);
        }

        // ============================================================ 2. canopy ground layer

        static IEnumerator PaintGround(Terrain t, List<NpcDistance.Mask> masks, StringBuilder log)
        {
            TerrainData d = t.terrainData;
            Ground g;
            if (_grounds.TryGetValue(d, out g) && g.Version == NpcDistance.MaskVersion && d.alphamapLayers == g.N0 + 1)
                yield break;                    // same mask, only the level changed

            // a. Union forest grid of every mask over this terrain.
            Vector3 org = t.GetPosition(), size = d.size;
            float cell = float.MaxValue;
            for (int i = 0; i < masks.Count; i++) cell = Mathf.Min(cell, masks[i].Cell);
            int gw = Mathf.Max(1, Mathf.CeilToInt(size.x / cell)), gh = Mathf.Max(1, Mathf.CeilToInt(size.z / cell));
            byte[] grid = new byte[gw * gh];
            for (int i = 0; i < masks.Count; i++)
            {
                NpcDistance.Mask m = masks[i];
                for (int mz = 0; mz < m.H; mz++)
                {
                    if ((mz & 15) == 0 && OverBudget()) yield return null;
                    int gz = (int)((m.Z0 + (mz + 0.5f) * m.Cell - org.z) / cell);
                    if (gz < 0 || gz >= gh) continue;
                    for (int mx = 0; mx < m.W; mx++)
                    {
                        if (!m.At(mx, mz)) continue;
                        int gx = (int)((m.X0 + (mx + 0.5f) * m.Cell - org.x) / cell);
                        if (gx >= 0 && gx < gw) grid[gz * gw + gx] = 1;
                    }
                }
            }

            // b. Weights at alphamap resolution: bilinear over the grid (soft
            //    edge one cell wide) times a noise of 0.53..1 (below).
            int aw = d.alphamapWidth, ah = d.alphamapHeight;
            byte[] W = new byte[aw * ah];
            int forest = 0;
            float[] macro = Lattice(new System.Random(911), 17);
            float[] crowns = Lattice(new System.Random(5), 61);
            for (int az = 0; az < ah; az++)
            {
                if (OverBudget()) yield return null;
                float wz = (az + 0.5f) * size.z / ah;
                float fz = wz / cell - 0.5f;
                int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, gh - 1), z1 = Mathf.Min(gh - 1, z0 + 1);
                float tz = Mathf.Clamp01(fz - z0);
                for (int ax = 0; ax < aw; ax++)
                {
                    float wx = (ax + 0.5f) * size.x / aw;
                    float fx = wx / cell - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, gw - 1), x1 = Mathf.Min(gw - 1, x0 + 1);
                    float tx = Mathf.Clamp01(fx - x0);
                    int a = grid[z0 * gw + x0], b = grid[z0 * gw + x1], c = grid[z1 * gw + x0], e = grid[z1 * gw + x1];
                    if ((a | b | c | e) == 0) continue;
                    float cov = (a * (1f - tx) + b * tx) * (1f - tz) + (c * (1f - tx) + e * tx) * tz;
                    // Crown-sized (5 m) and slow (220 m) variation, not periodic
                    // with the 8.6 m texture tile: the far canopy is mottled
                    // instead of a repeating grid, near it only shifts the mix
                    // of forest floor textures.
                    float gx5 = (org.x + wx) / (5f * K), gz5 = (org.z + wz) / (5f * K);
                    float v = (0.85f + 0.15f * Wrapped(macro, 17, gx5 / 44f, gz5 / 44f))
                            * (0.62f + 0.38f * Wrapped(crowns, 61, gx5, gz5));
                    byte wb = (byte)Mathf.Clamp(cov * v * 255f + 0.5f, 0f, 255f);
                    W[az * aw + ax] = wb;
                    if (wb > 0) forest++;
                }
            }
            grid = null;

            // c. First time on this TerrainData: the source layer, the
            //    texture, and our layer appended.
            if (g == null || g.D != d || g.T != t || d.alphamapLayers != g.N0 + 1)
            {
                if (forest == 0) yield break;
                SplatPrototype[] sp = d.splatPrototypes;
                int n0 = sp.Length;
                double[] sum = new double[n0];
                for (int az = 0; az < ah; az += 8)
                {
                    if (OverBudget()) yield return null;
                    float[,,] row = d.GetAlphamaps(0, az, aw, 1);
                    int nl = Mathf.Min(n0, row.GetLength(2));
                    for (int ax = 0; ax < aw; ax++)
                    {
                        int wb = W[az * aw + ax];
                        if (wb < 128) continue;
                        for (int l = 0; l < nl; l++) sum[l] += row[0, ax, l] * wb;
                    }
                }
                int src = 0;
                for (int l = 1; l < n0; l++) if (sum[l] > sum[src]) src = l;
                if (sp[src].texture == null)
                    for (int l = 0; l < n0; l++) if (sp[l].texture != null) { src = l; break; }
                if (sp[src].texture == null) yield break;

                g = new Ground();
                g.T = t; g.D = d; g.N0 = n0; g.Src = src; g.Layer = n0;
                g.SrcName = sp[src].texture.name;
                IEnumerator e = CanopyTexture(g, sp[src]);
                while (e.MoveNext()) yield return null;
                if (g.Tex == null) yield break;
                if (t == null || t.terrainData != d) yield break;

                g.Canopy = new SplatPrototype[n0 + 1];
                g.Plain = new SplatPrototype[n0 + 1];
                Array.Copy(sp, g.Canopy, n0);
                Array.Copy(sp, g.Plain, n0);
                g.Plain[n0] = CopyProto(sp[src], sp[src].texture, sp[src].tileSize);
                g.Canopy[n0] = CopyProto(sp[src], g.Tex, sp[src].tileSize * TileScale);

                // Appending a layer keeps the paint (Unity's own "add texture");
                // three samples prove it in the log.
                int[] sx = { aw / 4, aw / 2, aw * 3 / 4 };
                float[][,,] before = new float[3][,,];
                for (int i = 0; i < 3; i++) before[i] = d.GetAlphamaps(Mathf.Min(sx[i], aw - 8), Mathf.Min(sx[i], ah - 8), 8, 8);
                d.splatPrototypes = g.Canopy;
                _look = true;
                int off = 0;
                if (d.alphamapLayers != n0 + 1) off = -1;
                else
                    for (int i = 0; i < 3; i++)
                    {
                        float[,,] after = d.GetAlphamaps(Mathf.Min(sx[i], aw - 8), Mathf.Min(sx[i], ah - 8), 8, 8);
                        for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++) for (int l = 0; l < n0; l++)
                                    if (Mathf.Abs(after[z, x, l] - before[i][z, x, l]) > 0.01f) off++;
                    }
                if (off != 0)
                    RevivalPlugin.L.LogWarning("FarForest: " + t.name + " - adding the canopy layer changed the paint ("
                        + (off < 0 ? "layer count " + d.alphamapLayers : off + " sample weights") + ").");
                _grounds[d] = g;
                EastWorld.ForgetLayers(d);
            }

            // d. Paint: our weight W, every other layer scaled so the sum stays 1.
            IEnumerator p = Paint(g, W);
            while (p.MoveNext()) yield return null;
            g.Version = NpcDistance.MaskVersion;
            g.Forest = forest;
            if (log.Length > 0) log.Append(", ");
            log.Append(t.name).Append(" layer ").Append(g.Layer).Append(" over ").Append(g.SrcName).Append(" (")
               .Append((100.0 * forest / Math.Max(1, aw * ah)).ToString("0.0", CultureInfo.InvariantCulture)).Append(" % forest)");
        }

        static SplatPrototype CopyProto(SplatPrototype src, Texture2D tex, Vector2 tile)
        {
            SplatPrototype p = new SplatPrototype();
            p.texture = tex;
            p.normalMap = src.normalMap;
            p.tileSize = tile;
            p.tileOffset = src.tileOffset;
            p.specular = src.specular;
            p.metallic = src.metallic;
            p.smoothness = src.smoothness;
            return p;
        }

        /// <summary>Writes W into our layer. Direct on the control textures
        /// (one upload each at the end); the API (GetAlphamaps/SetAlphamaps
        /// per block) if they are not readable.</summary>
        static IEnumerator Paint(Ground g, byte[] W)
        {
            TerrainData d = g.D;
            int aw = d.alphamapWidth, ah = d.alphamapHeight;
            Texture2D[] tx = d.alphamapTextures;
            int ot = g.Layer / 4, oc = g.Layer % 4, st = g.Src / 4, sc = g.Src % 4;
            bool direct = tx != null && tx.Length > ot && tx[ot] != null && tx[ot].width == aw && tx[ot].height == ah;
            if (direct)
            {
                try { tx[0].GetPixel(0, 0); } catch { direct = false; }
            }
            bool[] dirty = new bool[tx == null ? 0 : tx.Length];
            int n = tx == null ? 0 : tx.Length;
            int blk = direct ? Block : ApiBlock;
            for (int bz = 0; bz < ah; bz += blk)
                for (int bx = 0; bx < aw; bx += blk)
                {
                    if (OverBudget()) yield return null;
                    if (g.T == null || g.T.terrainData != d) yield break;
                    int bw = Math.Min(blk, aw - bx), bh = Math.Min(blk, ah - bz);
                    if (direct)
                    {
                        Color[] ours = tx[ot].GetPixels(bx, bz, bw, bh);
                        bool need = false;
                        for (int z = 0; z < bh && !need; z++)
                            for (int x = 0; x < bw; x++)
                                if (Mathf.Abs(Ch(ours[z * bw + x], oc) * 255f - W[(bz + z) * aw + bx + x]) > 1.5f) { need = true; break; }
                        if (!need) continue;
                        if (OverBudget()) yield return null;
                        Color[][] px = new Color[n][];
                        for (int i = 0; i < n; i++) px[i] = i == ot ? ours : tx[i].GetPixels(bx, bz, bw, bh);
                        for (int z = 0; z < bh; z++)
                            for (int x = 0; x < bw; x++)
                            {
                                int k = z * bw + x;
                                float wn = W[(bz + z) * aw + bx + x] / 255f, wo = Ch(px[ot][k], oc);
                                if (Mathf.Abs(wn - wo) * 255f <= 1.5f) continue;
                                if (wo > 0.996f)
                                {
                                    for (int i = 0; i < n; i++) px[i][k] = new Color(0f, 0f, 0f, 0f);
                                    px[st][k] = SetCh(px[st][k], sc, 1f - wn);
                                }
                                else
                                {
                                    float f = (1f - wn) / (1f - wo);
                                    for (int i = 0; i < n; i++) px[i][k] = px[i][k] * f;
                                }
                                px[ot][k] = SetCh(px[ot][k], oc, wn);
                            }
                        for (int i = 0; i < n; i++) { tx[i].SetPixels(bx, bz, bw, bh, px[i]); dirty[i] = true; }
                    }
                    else
                    {
                        float[,,] a = d.GetAlphamaps(bx, bz, bw, bh);
                        int nl = a.GetLength(2);
                        if (g.Layer >= nl) yield break;
                        bool changed = false;
                        for (int z = 0; z < bh; z++)
                            for (int x = 0; x < bw; x++)
                            {
                                float wn = W[(bz + z) * aw + bx + x] / 255f, wo = a[z, x, g.Layer];
                                if (Mathf.Abs(wn - wo) * 255f <= 1.5f) continue;
                                changed = true;
                                if (wo > 0.996f)
                                {
                                    for (int l = 0; l < nl; l++) a[z, x, l] = 0f;
                                    a[z, x, g.Src] = 1f - wn;
                                }
                                else
                                {
                                    float f = (1f - wn) / (1f - wo);
                                    for (int l = 0; l < nl; l++) a[z, x, l] *= f;
                                }
                                a[z, x, g.Layer] = wn;
                            }
                        if (changed) d.SetAlphamaps(bx, bz, a);
                    }
                }
            if (!direct) yield break;
            for (int i = 0; i < n; i++)
            {
                if (!dirty[i]) continue;
                yield return null;              // one upload a frame
                if (g.T == null || g.T.terrainData != d) yield break;
                tx[i].Apply(false);
            }
            // Tell the terrain (basemap, if one is drawn): a 1 x 1 write of what is there.
            d.SetAlphamaps(0, 0, d.GetAlphamaps(0, 0, 1, 1));
        }

        static float Ch(Color c, int i) { return i == 0 ? c.r : i == 1 ? c.g : i == 2 ? c.b : c.a; }

        static Color SetCh(Color c, int i, float v)
        {
            if (i == 0) c.r = v; else if (i == 1) c.g = v; else if (i == 2) c.b = v; else c.a = v;
            return c;
        }

        /// <summary>The canopy layer texture: the source layer tiled 2 x 2
        /// (mip 0 texel = the source texel), mips blended level by level into
        /// a canopy image of the same tile. Level L is used where a screen
        /// pixel covers its texel: distance ~ texel * screen height /
        /// (2 tan(fov/2)); blend 0 at 50 m, 1 at 220 m.</summary>
        static IEnumerator CanopyTexture(Ground g, SplatPrototype src)
        {
            Texture st = src.texture;
            int S = Mathf.Min(MaxTexSize, Mathf.NextPowerOfTwo(Mathf.Max(st.width, st.height)) * TileScale);
            int ss = S / TileScale;
            float tileM = Mathf.Max(0.5f, src.tileSize.x) * TileScale / K;

            // Source pixels (non-readable, DXT): Blit + ReadPixels.
            Color32[] s0;
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(ss, ss, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D img = new Texture2D(ss, ss, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(st, rt);
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0f, 0f, ss, ss), 0, 0);
                s0 = img.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(img);
            }
            yield return null;

            // Mip schedule.
            int levels = 1;
            while ((S >> levels) > 0) levels++;
            Camera cam = CameraOwner.MainCamera();
            float fov = cam != null ? cam.fieldOfView : 60f;
            float screenH = Mathf.Max(480, Screen.height);
            float perPx = 2f * Mathf.Tan(Mathf.Deg2Rad * fov * 0.5f) / screenH;
            float[] tl = new float[levels];
            int lc = -1;
            for (int l = 0; l < levels; l++)
            {
                float texelM = tileM / (S >> l);
                float dist = texelM / perPx;
                tl[l] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BlendNearM, BlendFarM, dist));
                if (l < 2) tl[l] = 0f;          // mip 0-1 are always the floor
                if (lc < 0 && tl[l] > 0.01f) lc = l;
            }
            if (lc < 0) lc = levels - 1;

            // Canopy image at level lc (tileable).
            int cs = Mathf.Max(8, S >> lc);
            Color32[] can = CanopyImage(cs, tileM);
            yield return null;

            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.name = st.name;                 // EastWorld maps tile layers onto vanilla by texture name
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = Mathf.Max(4, st.anisoLevel);
            Color32[] cur = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                if ((y & 15) == 0 && OverBudget()) yield return null;
                int sy = (y % ss) * ss;
                for (int x = 0; x < S; x++) cur[y * S + x] = s0[sy + x % ss];
            }
            s0 = null;
            tex.SetPixels32(cur, 0);
            int size = S;
            Color32[] canL = null;
            for (int l = 1; l < levels; l++)
            {
                if (OverBudget()) yield return null;
                int half = Mathf.Max(1, size / 2);
                Color32[] nxt = new Color32[half * half];
                for (int y0 = 0; y0 < half; y0 += 64)
                {
                    DownRows(cur, size, nxt, y0, Math.Min(half, y0 + 64));
                    if (OverBudget()) yield return null;
                }
                cur = nxt;
                size = half;
                if (l < lc) { tex.SetPixels32(cur, l); continue; }
                canL = l == lc ? Resize(can, cs, size) : Down(canL, size * 2);
                float k = tl[l];
                Color32[] o = new Color32[cur.Length];
                for (int i = 0; i < o.Length; i++) o[i] = Color32.Lerp(cur[i], canL[i], k);
                tex.SetPixels32(o, l);
            }
            tex.Apply(false, true);
            g.Tex = tex;
        }

        static Color32[] Down(Color32[] px, int size)
        {
            int n = Mathf.Max(1, size / 2);
            Color32[] o = new Color32[n * n];
            DownRows(px, size, o, 0, n);
            return o;
        }

        /// <summary>2 x 2 box filter of rows y0..y1 of the half-size image.</summary>
        static void DownRows(Color32[] px, int size, Color32[] o, int y0r, int y1r)
        {
            int n = Mathf.Max(1, size / 2);
            for (int y = y0r; y < y1r; y++)
                for (int x = 0; x < n; x++)
                {
                    int x0 = Mathf.Min(size - 1, x * 2), y0 = Mathf.Min(size - 1, y * 2);
                    int x1 = Mathf.Min(size - 1, x0 + 1), y1 = Mathf.Min(size - 1, y0 + 1);
                    Color32 a = px[y0 * size + x0], b = px[y0 * size + x1], c = px[y1 * size + x0], e = px[y1 * size + x1];
                    o[y * n + x] = new Color32((byte)((a.r + b.r + c.r + e.r + 2) / 4), (byte)((a.g + b.g + c.g + e.g + 2) / 4),
                                               (byte)((a.b + b.b + c.b + e.b + 2) / 4), (byte)((a.a + b.a + c.a + e.a + 2) / 4));
                }
        }

        static Color32[] Resize(Color32[] px, int from, int to)
        {
            Color32[] cur = px;
            int s = from;
            while (s > to) { cur = Down(cur, s); s /= 2; }
            if (s == to) return cur;
            Color32[] o = new Color32[to * to];   // smaller source: nearest
            for (int y = 0; y < to; y++) for (int x = 0; x < to; x++) o[y * to + x] = cur[(y * s / to) * s + x * s / to];
            return o;
        }

        /// <summary>Forest canopy from above, tileable, `size` pixels over
        /// `tileM` metres: deep shadow between the crowns, each crown's cast
        /// shadow towards the lower right, crowns stamped from the rendered
        /// top views (procedural domes where there is none), prototypes drawn
        /// in proportion to their use.</summary>
        static Color32[] CanopyImage(int size, float tileM)
        {
            List<Slot> pick = new List<Slot>();
            List<int> weight = new List<int>();
            int total = 0;
            Color mean = new Color(0f, 0f, 0f, 0f);
            foreach (Slot s in _slots.Values)
            {
                if (!s.Measured || s.Uses == 0 || s.H < MinTreeM * K) continue;
                pick.Add(s); weight.Add(s.Uses); total += s.Uses;
                mean += s.Leaf * s.Uses;
            }
            Color tint = Tint();
            Color leafMean = total > 0 ? mean / total : LeafFallback;
            float ppm = size / tileM;
            Color32[] o = new Color32[size * size];
            Color gap = leafMean * 0.32f;
            for (int i = 0; i < o.Length; i++)
            {
                float n = 0.85f + 0.3f * Hash01(77u, i % size / 2, i / size / 2);
                o[i] = new Color32(B(gap.r * n * tint.r * 255f), B(gap.g * n * tint.g * 255f), B(gap.b * n * tint.b * 255f), 255);
            }
            System.Random rnd = new System.Random(4711);
            int grid = Mathf.Max(2, Mathf.RoundToInt(tileM / 3.2f));
            float step = size / (float)grid;
            List<Vector3> crowns = new List<Vector3>();     // x, y, radius px
            List<Slot> kinds = new List<Slot>();
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                    for (int rep = 0; rep < 2; rep++)
                    {
                        if (rep == 1 && rnd.NextDouble() > 0.35) continue;
                        Slot s = PickSlot(pick, weight, total, rnd);
                        float rm = s == null ? 1.8f : Mathf.Clamp(s.W / K * 0.4f, 1.2f, 2.6f);
                        rm *= 0.8f + 0.4f * (float)rnd.NextDouble();
                        if (rep == 1) rm *= 0.7f;
                        crowns.Add(new Vector3((gx + (float)rnd.NextDouble()) * step, (gy + (float)rnd.NextDouble()) * step, rm * ppm));
                        kinds.Add(s);
                    }
            // cast shadows first, then the crowns
            for (int c = 0; c < crowns.Count; c++)
            {
                Vector3 cr = crowns[c];
                Disc(o, size, cr.x + cr.z * 0.4f, cr.y - cr.z * 0.4f, cr.z * 1.05f, null, 0f, 0.6f, tint);
            }
            for (int c = 0; c < crowns.Count; c++)
            {
                Vector3 cr = crowns[c];
                Disc(o, size, cr.x, cr.y, cr.z, kinds[c], (float)rnd.NextDouble() * 6.283f, 0f, tint);
            }
            return o;
        }

        static Slot PickSlot(List<Slot> pick, List<int> weight, int total, System.Random rnd)
        {
            if (total <= 0) return null;
            int r = rnd.Next(total);
            for (int i = 0; i < pick.Count; i++) { r -= weight[i]; if (r < 0) return pick[i]; }
            return pick[pick.Count - 1];
        }

        /// <summary>One crown (or its shadow when `shade` > 0) wrapped around
        /// the tile edges.</summary>
        static void Disc(Color32[] o, int size, float cx, float cy, float r, Slot s, float rot, float shade, Color tint)
        {
            int ri = Mathf.CeilToInt(r);
            float cr = Mathf.Cos(rot), sr = Mathf.Sin(rot);
            Color leaf = s == null ? LeafFallback : s.Leaf;
            for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    float nx = dx / r, ny = dy / r;
                    float rr = nx * nx + ny * ny;
                    if (rr > 1f) continue;
                    int x = ((int)cx + dx) % size, y = ((int)cy + dy) % size;
                    if (x < 0) x += size;
                    if (y < 0) y += size;
                    int i = y * size + x;
                    if (shade > 0f)
                    {
                        float k = Mathf.Lerp(shade, 1f, rr * rr);
                        Color32 c0 = o[i];
                        o[i] = new Color32(B(c0.r * k), B(c0.g * k), B(c0.b * k), 255);
                        continue;
                    }
                    Color col;
                    float alpha = 1f;
                    if (s != null && s.Top != null)
                    {
                        // rotated lookup into the top view
                        float u = (nx * cr - ny * sr) * 0.5f + 0.5f, v = (nx * sr + ny * cr) * 0.5f + 0.5f;
                        Color32 t = s.Top[Mathf.Clamp((int)(v * TopSize), 0, TopSize - 1) * TopSize + Mathf.Clamp((int)(u * TopSize), 0, TopSize - 1)];
                        alpha = t.a / 255f;
                        if (alpha <= 0.05f) continue;
                        col = new Color(t.r / 255f, t.g / 255f, t.b / 255f, 1f);
                    }
                    else
                    {
                        // dome lit from the upper left, leafy noise, ragged rim
                        float edge = 0.78f + 0.22f * Hash01(13u, x / 2, y / 2);
                        if (rr > edge) continue;
                        float up = Mathf.Sqrt(Mathf.Max(0f, 1f - rr));
                        float lambert = Mathf.Clamp01(-0.45f * nx + 0.45f * ny + 0.77f * up);
                        float b = (0.45f + 0.75f * lambert) * (0.8f + 0.4f * Hash01(29u, x, y));
                        col = leaf * b;
                    }
                    Color32 was = o[i];
                    Color bg = new Color(was.r / 255f, was.g / 255f, was.b / 255f, 1f);
                    Color mix = Color.Lerp(bg, new Color(col.r * tint.r, col.g * tint.g, col.b * tint.b, 1f), alpha);
                    o[i] = new Color32(B(mix.r * 255f), B(mix.g * 255f), B(mix.b * 255f), 255);
                }
        }

        static float[] Lattice(System.Random rnd, int g)
        {
            float[] a = new float[g * g];
            for (int i = 0; i < a.Length; i++) a[i] = (float)rnd.NextDouble();
            return a;
        }

        /// <summary>Smooth value noise over a g x g lattice, wrapped.</summary>
        static float Wrapped(float[] a, int g, float fx, float fy)
        {
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            x0 = ((x0 % g) + g) % g; y0 = ((y0 % g) + g) % g;
            int x1 = (x0 + 1) % g, y1 = (y0 + 1) % g;
            float top = Mathf.Lerp(a[y0 * g + x0], a[y0 * g + x1], tx);
            float bot = Mathf.Lerp(a[y1 * g + x0], a[y1 * g + x1], tx);
            return Mathf.Lerp(top, bot, ty);
        }

        // ============================================================ 3. cards

        static IEnumerator BuildCards(List<NpcDistance.Mask> masks, int level, GameObject root, List<Chunk> made)
        {
            if (_cardMat == null || _atlas == null) yield break;
            EdgeMeasureStart();
            int every = InteriorEvery[Mathf.Clamp(level, 0, InteriorEvery.Length - 1)];
            float edge = EdgeM * K, ring = RidgeRingM * K, rise = RidgeRiseM * K;
            List<TerrainData> seen = new List<TerrainData>();
            for (int t = 0; t < masks.Count; t++)
            {
                Terrain tr = masks[t].Terrain;
                if (tr == null || tr.terrainData == null || seen.Contains(tr.terrainData)) continue;
                seen.Add(tr.terrainData);
                TerrainData d = tr.terrainData;
                Vector3 org = tr.GetPosition(), size = d.size;
                TreePrototype[] protos = d.treePrototypes;
                Slot[] map = new Slot[protos.Length];
                for (int p = 0; p < protos.Length; p++)
                {
                    Slot s;
                    if (protos[p].prefab != null && _slots.TryGetValue(protos[p].prefab, out s) && s.Card != null) map[p] = s;
                }
                TreeInstance[] inst = d.treeInstances;
                Dictionary<long, List<Card>> buckets = new Dictionary<long, List<Card>>();
                float chunk = ChunkM * K;
                for (int i = 0; i < inst.Length; i++)
                {
                    if ((i & 63) == 0 && OverBudget()) yield return null;
                    if (tr == null) yield break;
                    TreeInstance ti = inst[i];
                    if (ti.prototypeIndex < 0 || ti.prototypeIndex >= map.Length || map[ti.prototypeIndex] == null) continue;
                    float x = org.x + ti.position.x * size.x, z = org.z + ti.position.z * size.z;
                    if (!ForestAt(masks, x, z)) continue;
                    EdgeSample(map[ti.prototypeIndex].Card, ti.widthScale, ti.heightScale);
                    uint h = (uint)(i * 2654435761u);
                    bool take = !ForestAt(masks, x + edge, z) || !ForestAt(masks, x - edge, z)
                        || !ForestAt(masks, x, z + edge) || !ForestAt(masks, x, z - edge);
                    if (!take && every > 0 && (h >> 8) % (uint)every == 0) take = true;
                    if (!take)
                    {
                        // ridge / hilltop: the ground here is well over its ring
                        float y0 = tr.SampleHeight(new Vector3(x, 0f, z)), sum = 0f;
                        for (int k = 0; k < 6; k++)
                        {
                            float a = k * 1.0472f;
                            sum += tr.SampleHeight(new Vector3(x + Mathf.Cos(a) * ring, 0f, z + Mathf.Sin(a) * ring));
                        }
                        take = y0 - sum / 6f > rise;
                    }
                    if (!take) continue;
                    Card c = new Card();
                    c.P = new Vector3(x, org.y + ti.position.y * size.y, z);
                    c.Ws = ti.widthScale;
                    c.Hs = ti.heightScale;
                    c.Yaw = (h & 1023) / 1023f * Mathf.PI;
                    c.S = map[ti.prototypeIndex].Card;
                    long key = ((long)Mathf.FloorToInt(x / chunk) << 32) ^ (uint)Mathf.FloorToInt(z / chunk);
                    List<Card> list;
                    if (!buckets.TryGetValue(key, out list)) { list = new List<Card>(); buckets[key] = list; }
                    list.Add(c);
                }
                foreach (KeyValuePair<long, List<Card>> kv in buckets)
                {
                    if (OverBudget()) yield return null;
                    if (tr == null) yield break;
                    Chunk ch = MakeChunk(tr, kv.Value, root, chunk);
                    if (ch != null) made.Add(ch);
                }
            }
        }

        static readonly List<Vector3> _v = new List<Vector3>();
        static readonly List<Vector3> _n = new List<Vector3>();
        static readonly List<Vector2> _uv = new List<Vector2>();
        static readonly List<int> _t = new List<int>();

        /// <summary>One crossed, double-sided card into _v/_n/_uv/_t or the given
        /// lists (8 vertices,
        /// 8 triangles), relative to origin. Shared by the forest chunks and
        /// the edge strip (Revival.EdgeForest.cs).</summary>
        static void AddCard(Card c, Vector3 origin)
        {
            AddCard(c, origin, _v, _n, _uv, _t);
        }

        static void AddCard(Card c, Vector3 origin, List<Vector3> vs, List<Vector3> ns, List<Vector2> uvs, List<int> ts)
        {
            const float aw = SlotW * AtlasCols, ah = SlotH * AtlasRows;
            Slot s = c.S;
            int col = s.Index % AtlasCols, row = s.Index / AtlasCols;
            float u0 = (col * SlotW + 1.5f) / aw, u1 = ((col + 1) * SlotW - 1.5f) / aw;
            float v0 = (row * SlotH + 1.5f) / ah, v1 = ((row + 1) * SlotH - 1.5f) / ah;
            float hw = s.X1 * c.Ws;
            Vector3 p = c.P - origin;
            float y0 = p.y + s.Y0 * c.Hs, y1 = p.y + s.Y1 * c.Hs;
            for (int pl = 0; pl < 2; pl++)
            {
                float a = c.Yaw + pl * 1.5708f;
                Vector3 dir = new Vector3(Mathf.Cos(a) * hw, 0f, Mathf.Sin(a) * hw);
                int b = vs.Count;
                vs.Add(new Vector3(p.x - dir.x, y0, p.z - dir.z));
                vs.Add(new Vector3(p.x - dir.x, y1, p.z - dir.z));
                vs.Add(new Vector3(p.x + dir.x, y1, p.z + dir.z));
                vs.Add(new Vector3(p.x + dir.x, y0, p.z + dir.z));
                for (int k = 0; k < 4; k++) ns.Add(Vector3.up);   // lit like the ground and the canopy
                uvs.Add(new Vector2(u0, v0)); uvs.Add(new Vector2(u0, v1));
                uvs.Add(new Vector2(u1, v1)); uvs.Add(new Vector2(u1, v0));
                ts.Add(b); ts.Add(b + 1); ts.Add(b + 2); ts.Add(b); ts.Add(b + 2); ts.Add(b + 3);
                ts.Add(b); ts.Add(b + 2); ts.Add(b + 1); ts.Add(b); ts.Add(b + 3); ts.Add(b + 2);
            }
        }

        static Chunk MakeChunk(Terrain tr, List<Card> cards, GameObject root, float chunk)
        {
            if (cards.Count == 0) return null;
            int max = Math.Min(cards.Count, 65000 / 8);
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            for (int i = 0; i < max; i++)
            {
                Card c = cards[i];
                lo = Vector3.Min(lo, c.P);
                hi = Vector3.Max(hi, c.P + Vector3.up * c.S.Y1 * c.Hs);
            }
            Vector3 origin = new Vector3(Mathf.Floor(lo.x / chunk) * chunk, lo.y, Mathf.Floor(lo.z / chunk) * chunk);
            _v.Clear(); _n.Clear(); _uv.Clear(); _t.Clear();
            for (int i = 0; i < max; i++) AddCard(cards[i], origin);
            Chunk ch = new Chunk();
            ch.T = tr;
            ch.Cards = max;
            ch.Tris = _t.Count / 3;
            ch.Go = new GameObject("c" + Mathf.RoundToInt(origin.x) + "_" + Mathf.RoundToInt(origin.z));
            ch.Go.transform.SetParent(root.transform, false);
            ch.Go.transform.position = origin;

            GameObject far = new GameObject("cards");
            far.transform.SetParent(ch.Go.transform, false);
            Mesh mesh = new Mesh();
            mesh.name = "NDR_FarForestCards";
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            far.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer fr = far.AddComponent<MeshRenderer>();
            fr.sharedMaterial = _cardMat;
            Quiet(fr);

            GameObject near = new GameObject("near");
            near.transform.SetParent(ch.Go.transform, false);
            Mesh empty = new Mesh();
            empty.name = "NDR_FarForestEmpty";
            near.AddComponent<MeshFilter>().sharedMesh = empty;
            MeshRenderer nr = near.AddComponent<MeshRenderer>();
            nr.sharedMaterial = _cardMat;
            Quiet(nr);

            ch.Near = new Renderer[] { nr };
            ch.Far = new Renderer[] { fr };
            Vector3 ext = hi - lo;
            ch.Ref = (lo + hi) * 0.5f - origin;
            ch.Half = 0.5f * Mathf.Sqrt(ext.x * ext.x + ext.z * ext.z);
            ch.Size = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));
            if (ch.Size < 1f) ch.Size = 1f;
            ch.G = ch.Go.AddComponent<LODGroup>();
            ch.G.fadeMode = LODFadeMode.None;
            ch.G.animateCrossFading = false;
            ApplyLod(ch, Mathf.Tan(Mathf.Deg2Rad * 30f), QualitySettings.lodBias);
            return ch;
        }

        static void Quiet(MeshRenderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // ============================================================ bench

        // 0 none, 1 on, 2 off. Settle 2 s, sample 5 s each.
        const float BenchSettle = 2f, BenchSample = 5f;
        static int _bench;
        static bool _benchSampling;
        static float _benchPhaseStart;
        static int _benchFrames;
        static double _benchMs, _benchOwn, _benchOn, _benchOnOwn;
        static string _benchResult;

        /// <summary>Admin panel: frame time with the far forest on and off at
        /// this spot (~14 s, hold still).</summary>
        internal static string Bench()
        {
            if (Level == 0) return "Far forest is Off ([FarForest] Quality or the F2 window).";
            if (_build != null) return "Far forest still building - try again in a moment.";
            if (_grounds.Count == 0 && _chunks.Count == 0) return "Far forest: nothing built yet (no terrain trees loaded?).";
            if (_bench != 0 || _shot != 0) return "Far forest bench or shots already running.";
            _benchResult = null;
            SetBenchPhase(1);
            return "Far forest bench: hold still ~14 s (on, then off).";
        }

        static void SetBenchPhase(int phase)
        {
            _bench = phase;
            _benchSampling = false;
            _benchPhaseStart = Time.realtimeSinceStartup;
            _benchFrames = 0;
            _benchMs = _benchOwn = 0.0;
            SetLook(phase != 2 && Level > 0);
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
                SetBenchPhase(2);
                return;
            }
            SetBenchPhase(0);
            _benchResult = "Far forest bench (" + Names[Level] + ", " + _sCards + " cards / " + (_sTris / 1000)
                + "k tris built): on " + F2(_benchOn) + " ms (" + F1(1000.0 / Math.Max(0.01, _benchOn))
                + " FPS), off " + F2(avg) + " ms (" + F1(1000.0 / Math.Max(0.01, avg)) + " FPS), cost "
                + F2(_benchOn - avg) + " ms/frame, own tick " + F3(_benchOnOwn) + " ms.";
            RevivalPlugin.L.LogInfo("[FarForest] " + _benchResult);
        }

        // ============================================================ comparison shots

        static int _shot, _shotFrame;
        static string _shotBase, _shotResult;

        /// <summary>Admin panel: two screenshots from this spot, far forest on
        /// ("after") and off ("before", the game's bare terrain past the trees),
        /// into &lt;game&gt;/NDR_Shots. Name: map terrain, height over ground,
        /// tree distance.</summary>
        internal static string Shots()
        {
            if (Level == 0) return "Far forest is Off - switch it on first.";
            if (_build != null) return "Far forest still building - try again in a moment.";
            if (_bench != 0 || _shot != 0) return "Far forest bench or shots already running.";
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return "No camera.";
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "NDR_Shots");
                Directory.CreateDirectory(dir);
                Vector3 p = cam.transform.position;
                Terrain under = null;
                Terrain[] all = Terrain.activeTerrains;
                for (int i = 0; i < all.Length && under == null; i++)
                {
                    Terrain t = all[i];
                    if (!IsGround(t)) continue;
                    Vector3 o = t.GetPosition(), s = t.terrainData.size;
                    if (p.x >= o.x && p.z >= o.z && p.x < o.x + s.x && p.z < o.z + s.z) under = t;
                }
                float agl = under == null ? 0f : (p.y - under.SampleHeight(p) - under.GetPosition().y) / K;
                float td = 0f;
                for (int i = 0; i < _lodTerrains.Count; i++) if (_lodTerrains[i] != null) td = Mathf.Max(td, _lodTerrains[i].treeDistance);
                _shotBase = Path.Combine(dir, "farforest_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                    + "_" + (under == null ? "none" : under.name) + "_agl" + Mathf.RoundToInt(agl) + "m_trees"
                    + Mathf.RoundToInt(td / K) + "m");
            }
            catch (Exception ex) { return "Far forest shots: " + ex.Message; }
            _shotResult = null;
            _shot = 1;
            _shotFrame = Time.frameCount;
            SetLook(true);
            return "Far forest shots: hold still ~1 s.";
        }

        static void ShotStep()
        {
            if (_shot == 0) return;
            int age = Time.frameCount - _shotFrame;
            if (_shot == 1 && age >= 3)
            {
                ScreenCapture.CaptureScreenshot(_shotBase + "_after.png");
                _shot = 2; _shotFrame = Time.frameCount;
            }
            else if (_shot == 2 && age >= 2)
            {
                SetLook(false);
                _shot = 3; _shotFrame = Time.frameCount;
            }
            else if (_shot == 3 && age >= 4)
            {
                ScreenCapture.CaptureScreenshot(_shotBase + "_before.png");
                _shot = 4; _shotFrame = Time.frameCount;
            }
            else if (_shot == 4 && age >= 2)
            {
                SetLook(Level > 0);
                _shot = 0;
                _shotResult = "Far forest shots: " + Path.GetFileName(_shotBase) + "_after/_before.png in NDR_Shots.";
                RevivalPlugin.L.LogInfo("[FarForest] " + _shotResult);
            }
        }

        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        /// <summary>Admin panel line: bench / shots result, else the live state.</summary>
        internal static string Status()
        {
            if (_bench != 0) return "far forest bench: " + (_bench == 1 ? "on" : "off") + "...";
            if (_shot != 0) return "far forest shots...";
            if (_benchResult != null) return _benchResult;
            if (_shotResult != null) return _shotResult;
            int level = Level;
            if (level == 0) return "far forest Off";
            if (_build != null || (_grounds.Count == 0 && _chunks.Count == 0 && NpcDistance.Masks.Count == 0))
                return "far forest " + Names[level] + ": building...";
            float td = 0f;
            for (int i = 0; i < _lodTerrains.Count; i++) if (_lodTerrains[i] != null) td = Mathf.Max(td, _lodTerrains[i].treeDistance);
            return "far forest " + Names[level] + ": " + _grounds.Count + " ground layer(s), " + _sCards + " cards in "
                + _chunks.Count + " chunks (" + (_sTris / 1000) + "k tris), " + _sRendered + "/" + _sSlots
                + " impostors rendered, trees to " + (td / K).ToString("0", CultureInfo.InvariantCulture) + " m, "
                + F3(_tickMs) + " ms" + (_kept.Length > 0 ? "; " + _kept : "") + "; " + EdgeStatus();
        }
    }

    /// <summary>X perf-fix: is a forest mask change small enough to keep the
    /// far forest as built (FarForest.KeepAfterEdit)? No UnityEngine here -
    /// tests/x_perf_fix_check.py compiles this class with the .NET 3.5 csc.</summary>
    internal static class ForestEdit
    {
        /// <summary>Changed cells tolerated: 3 % of the forest cells the build
        /// was made from, at least KeepMin. The 6.63.0 helipad site changed
        /// ~2,800 of ~245,000 (1.1 %).</summary>
        internal const double KeepShare = 0.03;
        internal const int KeepMin = 256;

        static readonly byte[] _ones = Ones();

        static byte[] Ones()
        {
            byte[] t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int n = 0;
                for (int v = i; v != 0; v >>= 1) n += v & 1;
                t[i] = (byte)n;
            }
            return t;
        }

        /// <summary>Cells that differ between two bit masks of one grid; -1 when
        /// they are not the same grid.</summary>
        internal static int BitDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            int n = 0;
            for (int i = 0; i < a.Length; i++) n += _ones[a[i] ^ b[i]];
            return n;
        }

        internal static bool Keep(int changed, int forest)
        {
            if (changed < 0 || forest < 0) return false;
            return changed <= Math.Max(KeepMin, (int)(forest * KeepShare));
        }
    }
}

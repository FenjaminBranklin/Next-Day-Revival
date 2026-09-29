// Next Day: Survival - Revival Toolkit
//
// CONTENT PERF: the render and physics settings of the east content scenes,
// applied in the game once per load (docs/ai/tasks/airfield-perf.md). Acts
// only with [World] EastTile (EastWorld.On) and [World] ContentPerf (on).
//
// Why at runtime: a content bundle points at the game's own meshes by GAME
// reference (unity/EastTile GameRefs.cs); in the editor those are one-triangle
// placeholders, so sizes, shadows and static batching can only be judged here,
// where the real meshes are loaded. The same pass serves every content scene
// whose name starts with a profile prefix: the airfield (EastAf*, one scene per
// building bundle) and the military town (EastMt*, EastTown) share the
// numbers below; a new area adds its prefix to Profiles.
//
// Per scene, in this order (sizes are world-bounds extents in game units,
// 2.8 u = 1 m; a human is 5 u):
//   1. shadows   a renderer under ShadowMaxU casts no shadow (it still
//                receives them).
//   2. cull      every renderer outside a LODGroup belongs to a piece: the
//                highest ancestor under LargeU that holds no LODGroup (a prop's
//                holder, a vehicle, one weed). A piece under LargeU gets a
//                one-level LODGroup culled at CullPerU x its size, clamped to
//                CullMinU..CullMaxU. A renderer under DetailMaxU inside a
//                building's LODGroup (weeds, benches, debris) leaves it and
//                gets its own cull, never farther than the building showed it.
//                An authored cull farther than CullMaxU is pulled in to it.
//   3. colliders BoxColliders of one tag, layer, physic material and rotation
//                whose union is a box are joined into one; a box inside
//                another goes. The solid stays exactly the same (fence panels
//                in a row on flat ground, a kit building's wall boxes).
//   4. interior  a building (a node 20..260 u across with a collider covering
//                most of its footprint: shell mesh or pad) hides the renderers
//                that lie wholly inside its shell while no line from the camera
//                reaches inside: at most 6 linecasts a frame (P1b), 18 to points
//                inside the shell; visible when one ray gets in (nothing hit,
//                or the hit is inside the shell), when the camera is within
//                NearU of the shell, and for HoldSeconds after the last hit.
//   5. batching  StaticBatchingUtility.Combine over the scene's renderers with
//                a readable mesh (the bundles' own meshes; a game mesh that is
//                not readable stays as it is).
// One log line per scene: "ContentPerf: EastAfH1 ...".
//
// n01 perf: the pass above used to run inside ONE frame per scene (42 ms peak
// in Kevin's 6.59.0 F6 at the airfield, the MergeBoxes pair loop and the
// static batching the heaviest parts). It is now an iterator stepped from
// Tick with a SliceMs budget a frame: the same steps in the same order with
// the same numbers, spread over as many frames as it takes. A scene unloaded
// mid-pass ends its job; Occlusion keeps running meanwhile.
//
// Measure before/after with [Research] FrameBench, FrameBenchSpots = airfield,
// once with ContentPerf off and once on (read at load: re-enter the world).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class ContentPerf
    {
        /// <summary>The settings of one kind of area; every number in game units.</summary>
        internal sealed class Profile
        {
            internal string Name;
            internal string[] Prefixes;
            internal float ShadowMaxU = 4f;      // smaller renderers cast no shadow (~1.4 m)
            internal float DetailMaxU = 8f;      // smaller renderers leave a building's LODGroup
            internal float LargeU = 20f;         // pieces this big get the large cull, not the piece cull
            // P2: real camera distances (lodBias folded in, see Height). 6.60
            // wrote 80 / 150 / 1500 "before lodBias", which the game's lodBias
            // 2 doubled; the doubled values keep Medium's piece culls as they were.
            internal float CullPerU = 160f;      // cull distance per unit of size
            internal float CullMinU = 300f;      // ~107 m
            internal float CullMaxU = 3000f;     // ~1070 m
            internal float SmallCullU = 420f;    // P2: a piece under DetailMaxU is never drawn farther (150 m)
            internal float LargeMaxU = 150f;     // P2: free pieces LargeU..this get a cull by size ...
            internal float LargeCullMaxU = 2800f; // ... at most this far (1 km; past the Medium far clip)
            internal bool Combine = true;        // P2: merge meshes per building LOD level / prop cluster / interior
            internal float ClusterU = 64f;       // P2: one-level prop groups merge per square cell of this edge
            internal int CombineMaxVerts = 16000; // P2: one merged mesh; also bounds its one-frame build cost
            internal float ShellMinU = 20f, ShellMaxU = 260f, ShellMinHeightU = 8f;
            internal float ShellCover = 0.7f;    // collider share of the footprint that makes a shell
            internal float InsetU = 1f;          // inside = at least this far inside the shell
            internal float NearU = 60f;          // interior always on this close (~21 m)
            internal float FarU = 1500f;         // interior always off farther away
            internal float HoldSeconds = 1f;
            internal float MergeEpsU = 0.02f;
            internal bool Batch = true;

            internal Profile(string name, string[] prefixes) { Name = name; Prefixes = prefixes; }
        }

        internal static readonly Profile[] Profiles = new Profile[] {
            new Profile("airfield", new string[] { "EastAf" }),
            new Profile("military town", new string[] { "EastMt", "EastTown" }),
        };

        sealed class Interior
        {
            internal string Name;
            internal Bounds Shell;
            internal Renderer[] Renderers;
            internal Vector3[] Points;
            internal int Ray;                   // P1b: next point of a test spread over frames
            internal float TestedAt = -100f;    // P1b: last complete test that found no line in
            internal Vector3 TestedEye;
            internal bool Shown = true;
            internal float LastSeen;
            internal Profile P;
            internal int Scene;
        }

        static ConfigEntry<bool> _cfg;
        static readonly HashSet<int> _done = new HashSet<int>();
        static readonly List<Interior> _interiors = new List<Interior>();
        static int _next;
        static float _scanAt;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfg = cfg.Bind("World", "ContentPerf", true,
                "East content scenes (airfield, military town): small renderers cast no "
                + "shadow, small pieces are culled by distance, touching box colliders "
                + "merge, building interiors hide while no line of sight reaches in, "
                + "readable meshes are statically batched. Read when a scene loads. "
                + "Off = the bundles exactly as built (the FrameBench 'before').");
            _cfgCombine = cfg.Bind("World", "ContentPerfCombine", true,
                "P2 performance: with ContentPerf on, the readable meshes of one building LOD "
                + "level, one interior or one cluster of small props are merged into one mesh "
                + "per material (far fewer renderers for the CPU). Read when a scene loads. "
                + "Off = 6.60's static batching only (for a FrameBench before/after).");
        }

        internal static bool On { get { return EastWorld.On && _cfg != null && _cfg.Value; } }

        /// <summary>For FrameBench: interiors shown / registered.</summary>
        internal static string Summary()
        {
            if (!On) return "ContentPerf off";
            int shown = 0, rend = 0;
            for (int i = 0; i < _interiors.Count; i++)
            {
                if (_interiors[i].Shown) shown++;
                rend += _interiors[i].Renderers.Length;
            }
            return "ContentPerf on, interiors shown " + shown + "/" + _interiors.Count + " (" + rend + " renderers)";
        }

        internal static void Tick()
        {
            if (!On) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_run != null) Step();
                else if (now >= _scanAt)
                {
                    _scanAt = now + 1f;
                    Scan();
                }
                Occlusion(now);
            }
            catch (Exception ex)
            {
                Log("tick error: " + ex);
                _scanAt = Time.realtimeSinceStartup + 10f;
            }
        }

        // ------------------------------------------------------------ the job

        const double SliceMs = 0.15;
        static IEnumerator<bool> _run;
        static Scene _runScene;
        static string _runName;
        static int _runFrames;
        static double _runMs;
        static long _sliceStart, _sliceBudget;

        sealed class SceneGone : Exception { }

        /// <summary>True when this frame's slice is spent: the caller yields.</summary>
        static bool Over()
        {
            return Stopwatch.GetTimestamp() - _sliceStart > _sliceBudget;
        }

        /// <summary>After a yield: the scene may have been unloaded meanwhile.</summary>
        static void Still()
        {
            if (!_runScene.isLoaded) throw new SceneGone();
        }

        static void Start(Scene s, Profile p)
        {
            _runScene = s;
            _runName = s.name;
            _runFrames = 0;
            _runMs = 0;
            _run = Apply(s, p).GetEnumerator();
            Step();
        }

        static void Step()
        {
            _sliceStart = Stopwatch.GetTimestamp();
            _sliceBudget = (long)(SliceMs * Stopwatch.Frequency / 1000.0);
            _runFrames++;
            bool more;
            try { more = _run.MoveNext(); }
            catch (SceneGone) { Log(_runName + ": unloaded mid-pass - stopped."); more = false; }
            catch (Exception ex) { Log(_runName + ": failed: " + ex); more = false; }
            _runMs += (Stopwatch.GetTimestamp() - _sliceStart) * 1000.0 / Stopwatch.Frequency;
            if (!more) _run = null;
        }

        // ------------------------------------------------------------ per scene

        static void Scan()
        {
            HashSet<int> live = _live;                          // no garbage once a second
            live.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                live.Add(s.GetHashCode());
                if (_done.Contains(s.GetHashCode())) continue;
                Profile p = ProfileOf(s.name);
                if (p == null) continue;
                _done.Add(s.GetHashCode());
                Start(s, p);
                return;                                         // one scene per scan
            }
            _done.RemoveWhere(_notLive);
            _interiors.RemoveAll(_interiorGone);
            if (_next >= _interiors.Count) _next = 0;
        }

        static readonly HashSet<int> _live = new HashSet<int>();
        static readonly Predicate<int> _notLive = delegate(int h) { return !_live.Contains(h); };
        static readonly Predicate<Interior> _interiorGone = delegate(Interior it) { return !_live.Contains(it.Scene); };

        static Profile ProfileOf(string scene)
        {
            foreach (Profile p in Profiles)
                foreach (string pre in p.Prefixes)
                    if (scene.StartsWith(pre, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>The counters of one pass (iterators take no out/ref).</summary>
        sealed class Counts
        {
            internal int Pulled, Groups, Tightened, Before, After, Interiors, InteriorRenderers, Batched;
            internal int Merged, MergedInto, Clusters, Kept;   // P2 combine
        }

        static IEnumerable<bool> Apply(Scene s, Profile p)
        {
            _tan = TanHalfFovNow();
            _bias = Mathf.Clamp(QualitySettings.lodBias, 0.25f, 8f);
            Counts n = new Counts();
            GameObject[] roots = s.GetRootGameObjects();
            List<Renderer> all = new List<Renderer>();
            foreach (GameObject r in roots)
            {
                all.AddRange(r.GetComponentsInChildren<Renderer>());
                if (Over()) { yield return true; Still(); }
            }
            Dictionary<Renderer, Bounds> rb = new Dictionary<Renderer, Bounds>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null) rb[all[i]] = all[i].bounds;
                if (Over()) { yield return true; Still(); }
            }
            // Destroyed between the frames: out before anything reads them.
            for (int i = all.Count - 1; i >= 0; i--) if (!rb.ContainsKey(all[i])) all.RemoveAt(i);

            int shadows = 0;
            foreach (Renderer r in all)
            {
                if (r != null && Size(rb[r]) < p.ShadowMaxU && r.shadowCastingMode != ShadowCastingMode.Off)
                {
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    shadows++;
                }
                if (Over()) { yield return true; Still(); }
            }

            foreach (bool y in Cull(roots, all, rb, p, n)) yield return y;
            foreach (bool y in MergeBoxes(s, roots, p, n)) yield return y;
            foreach (GameObject r in roots)
                if (r != null)
                    foreach (bool y in FindShells(r.transform, rb, p, s.GetHashCode(), n)) yield return y;
            _retired.Clear();
            if (p.Combine && CombineOn)
                foreach (bool y in Combine(s, roots, all, p, n)) yield return y;
            if (p.Batch) foreach (bool y in Batch(roots, n)) yield return y;
            _retired.Clear();
            // merged meshes the batch did not take stay readable; forget them
            foreach (KeyValuePair<MeshFilter, Mesh> kv in _madeMeshes)
                if (kv.Value != null && kv.Key != null && kv.Key.sharedMesh == kv.Value) kv.Value.UploadMeshData(true);
            _madeMeshes.Clear();

            Log(s.name + " (" + p.Name + "): " + all.Count + " renderers, " + shadows + " stop casting shadows, "
                + n.Groups + " cull groups added (" + n.Pulled + " renderers out of building LODGroups, " + n.Tightened
                + " authored culls pulled in), box colliders " + n.Before + " -> " + n.After + ", " + n.Interiors
                + " interior(s) with " + n.InteriorRenderers + " renderers, " + n.Merged + " renderers merged into "
                + n.MergedInto + " (" + n.Clusters + " prop clusters, " + n.Kept + " left as they are), "
                + n.Batched + " renderers statically batched; "
                + (_runMs + (Stopwatch.GetTimestamp() - _sliceStart) * 1000.0 / Stopwatch.Frequency).ToString("F0")
                + " ms over " + _runFrames + " frame(s).");
        }

        static float Size(Bounds b) { return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)); }

        static float _tan = 0.57735f;
        static float _bias = 2f;

        static float TanHalfFov() { return _tan; }

        static float TanHalfFovNow()
        {
            Camera c = CameraOwner.MainCamera();
            float fov = c != null ? c.fieldOfView : 60f;
            return Mathf.Tan(Mathf.Clamp(fov, 20f, 120f) * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>Screen relative height for a cull at camera distance d of a
        /// group this big. Unity compares size / (2 d tan(fov/2)) x lodBias
        /// with the height, so the bias of the pass is folded in (P2: 6.60 left
        /// it out and every cull landed lodBias times farther than written).</summary>
        static float Height(float size, float d)
        {
            return Mathf.Clamp(size * _bias / (2f * Mathf.Max(d, 1f) * TanHalfFov()), 0.0005f, 0.99f);
        }

        static float Distance(float size, float h)
        {
            return size * _bias / (2f * Mathf.Max(h, 0.0001f) * TanHalfFov());
        }

        /// <summary>P2: the cull distance of a piece this big: by size between
        /// CullMinU and CullMaxU, a small one (under DetailMaxU) never past
        /// SmallCullU.</summary>
        static float PieceCull(float size, Profile p)
        {
            float d = Mathf.Min(p.CullMaxU, Mathf.Max(p.CullMinU, p.CullPerU * size));
            return size < p.DetailMaxU ? Mathf.Min(d, p.SmallCullU) : d;
        }

        static float WorldSize(LODGroup lg)
        {
            Vector3 s = lg.transform.lossyScale;
            return lg.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        }

        static IEnumerable<bool> Cull(GameObject[] roots, List<Renderer> all, Dictionary<Renderer, Bounds> rb, Profile p,
                                      Counts n)
        {
            int pulled = 0, groups = 0, tightened = 0;
            // renderer -> the distance its LODGroup last shows it; -1 = stays in its group
            Dictionary<Renderer, float> inGroup = new Dictionary<Renderer, float>();
            List<LODGroup> lgs = new List<LODGroup>();
            foreach (GameObject r in roots) if (r != null) lgs.AddRange(r.GetComponentsInChildren<LODGroup>());
            foreach (LODGroup lg in lgs)
            {
                if (Over()) { yield return true; Still(); }
                if (lg == null) continue;
                LOD[] lods = lg.GetLODs();
                if (lods.Length == 0) continue;
                float ws = WorldSize(lg);
                bool changed = false;
                // an authored cull beyond CullMaxU comes in to it (large groups keep
                // theirs); P2: a small group's beyond SmallCullU comes in to that
                float cap = ws < p.DetailMaxU ? p.SmallCullU : p.CullMaxU;
                if (ws < p.LargeU && Distance(ws, lods[lods.Length - 1].screenRelativeTransitionHeight) > cap)
                {
                    float h = Height(ws, cap);
                    if (lods.Length == 1 || h < lods[lods.Length - 2].screenRelativeTransitionHeight)
                    {
                        lods[lods.Length - 1].screenRelativeTransitionHeight = h;
                        tightened++;
                        changed = true;
                    }
                }
                // the levels each renderer is in (bit i = LOD i)
                Dictionary<Renderer, int> mask = new Dictionary<Renderer, int>();
                for (int i = 0; i < lods.Length && i < 30; i++)
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r == null) continue;
                        int m;
                        mask.TryGetValue(r, out m);
                        mask[r] = m | (1 << i);
                    }
                HashSet<Renderer> pull = new HashSet<Renderer>();
                foreach (KeyValuePair<Renderer, int> kv in mask)
                {
                    inGroup[kv.Key] = -1f;
                    Bounds b;
                    if (ws < p.LargeU || !rb.TryGetValue(kv.Key, out b) || Size(b) >= p.DetailMaxU) continue;
                    // only a renderer shown from LOD0 up to its last level (never a far-only stand-in)
                    int m = kv.Value, last = 0;
                    while ((m >> (last + 1)) != 0) last++;
                    if (m != (1 << (last + 1)) - 1) continue;
                    float far = Distance(ws, lods[last].screenRelativeTransitionHeight);
                    if (PieceCull(Size(b), p) >= far) continue;
                    pull.Add(kv.Key);
                    inGroup[kv.Key] = far;
                }
                if (pull.Count > 0)
                {
                    for (int i = 0; i < lods.Length; i++)
                    {
                        List<Renderer> keep = new List<Renderer>();
                        foreach (Renderer r in lods[i].renderers)
                            if (r != null && !pull.Contains(r)) keep.Add(r);
                        lods[i].renderers = keep.ToArray();
                    }
                    changed = true;
                }
                if (changed) lg.SetLODs(lods);
            }
            // free renderers: never in a group, or pulled out of a building's
            List<Renderer> free = new List<Renderer>();
            foreach (Renderer r in all)
            {
                if (r == null) continue;
                float far;
                if (!inGroup.TryGetValue(r, out far)) free.Add(r);
                else if (far > 0f) { free.Add(r); pulled++; }
            }

            Dictionary<Transform, Bounds> tb = new Dictionary<Transform, Bounds>();
            HashSet<Transform> hasLod = new HashSet<Transform>();
            foreach (LODGroup lg in lgs)
                if (lg != null)
                    for (Transform t = lg.transform; t != null; t = t.parent) hasLod.Add(t);
            if (Over()) { yield return true; Still(); }
            foreach (Renderer r in all)
            {
                if (Over()) { yield return true; Still(); }
                if (r == null) continue;
                for (Transform t = r.transform; t != null; t = t.parent)
                {
                    Bounds b;
                    if (tb.TryGetValue(t, out b)) { b.Encapsulate(rb[r]); tb[t] = b; }
                    else tb[t] = rb[r];
                }
            }

            Dictionary<Transform, List<Renderer>> pieces = new Dictionary<Transform, List<Renderer>>();
            foreach (Renderer r in free)
            {
                if (Over()) { yield return true; Still(); }
                if (r == null) continue;
                if (hasLod.Contains(r.transform)) continue;       // a LODGroup on it or below: leave it
                Transform piece = r.transform;
                while (piece.parent != null && piece.parent.parent != null && !hasLod.Contains(piece.parent)
                       && Size(tb[piece.parent]) < p.LargeU)
                    piece = piece.parent;
                List<Renderer> l;
                if (!pieces.TryGetValue(piece, out l)) pieces[piece] = l = new List<Renderer>();
                l.Add(r);
            }
            foreach (KeyValuePair<Transform, List<Renderer>> kv in pieces)
            {
                if (Over()) { yield return true; Still(); }
                if (kv.Key == null) continue;
                Bounds b = rb[kv.Value[0]];
                float limit = float.MaxValue;
                foreach (Renderer r in kv.Value)
                {
                    b.Encapsulate(rb[r]);
                    float f;
                    if (inGroup.TryGetValue(r, out f) && f > 0f) limit = Mathf.Min(limit, f);
                }
                float size = Size(b);
                float d;
                if (size < p.LargeU || limit != float.MaxValue) d = Mathf.Min(limit, PieceCull(size, p));
                else if (size < p.LargeMaxU) d = Mathf.Min(p.LargeCullMaxU, p.CullPerU * size);   // P2: by size
                else continue;                                  // slabs, pads, whole ruins: drawn to the far clip
                if (kv.Key.GetComponent<LODGroup>() != null) continue;
                LODGroup lg = kv.Key.gameObject.AddComponent<LODGroup>();
                lg.SetLODs(new LOD[] { new LOD(0.5f, kv.Value.ToArray()) });
                lg.RecalculateBounds();
                LOD[] one = lg.GetLODs();
                one[0].screenRelativeTransitionHeight = Height(WorldSize(lg), d);
                lg.SetLODs(one);
                groups++;
            }
            n.Pulled = pulled;
            n.Groups = groups;
            n.Tightened = tightened;
        }

        // ------------------------------------------------------------ colliders

        struct Span
        {
            public BoxCollider Box;
            public Vector3 Min, Max;      // in the rotation's frame
            public int Members;
            public bool Gone;
        }

        static IEnumerable<bool> MergeBoxes(Scene s, GameObject[] roots, Profile p, Counts n)
        {
            int before = 0, after = 0;
            Dictionary<string, List<Span>> groups = new Dictionary<string, List<Span>>();
            Dictionary<string, Quaternion> rots = new Dictionary<string, Quaternion>();
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                foreach (BoxCollider bc in root.GetComponentsInChildren<BoxCollider>())
                {
                    if (Over()) { yield return true; Still(); }
                    if (bc == null || !bc.enabled || bc.isTrigger || bc.attachedRigidbody != null) continue;
                    before++;
                    Transform t = bc.transform;
                    Quaternion q = t.rotation;
                    if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    string key = t.gameObject.layer + "|" + t.gameObject.tag + "|"
                        + (bc.sharedMaterial == null ? 0 : bc.sharedMaterial.GetInstanceID()) + "|"
                        + Mathf.Round(q.x * 1000f) + "," + Mathf.Round(q.y * 1000f) + ","
                        + Mathf.Round(q.z * 1000f) + "," + Mathf.Round(q.w * 1000f);
                    if (!rots.ContainsKey(key)) rots[key] = q;
                    Quaternion inv = Quaternion.Inverse(rots[key]);
                    Vector3 ls = t.lossyScale;
                    Vector3 size = new Vector3(Mathf.Abs(bc.size.x * ls.x), Mathf.Abs(bc.size.y * ls.y), Mathf.Abs(bc.size.z * ls.z));
                    Vector3 c = inv * t.TransformPoint(bc.center);
                    Span sp = new Span();
                    sp.Box = bc;
                    sp.Min = c - size * 0.5f;
                    sp.Max = c + size * 0.5f;
                    sp.Members = 1;
                    List<Span> l;
                    if (!groups.TryGetValue(key, out l)) groups[key] = l = new List<Span>();
                    l.Add(sp);
                }
            }
            after = before;
            foreach (KeyValuePair<string, List<Span>> kv in groups)
            {
                Span[] a = kv.Value.ToArray();
                if (a.Length < 2) continue;
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (Over()) { yield return true; Still(); }
                        if (a[i].Gone) continue;
                        for (int j = 0; j < a.Length; j++)
                        {
                            if (j == i || a[j].Gone) continue;
                            Vector3 mn, mx;
                            if (!Union(a[i], a[j], p.MergeEpsU, out mn, out mx)) continue;
                            a[i].Min = mn;
                            a[i].Max = mx;
                            a[i].Members += a[j].Members;
                            a[j].Gone = true;
                            changed = true;
                        }
                    }
                }
                Quaternion q = rots[kv.Key];
                for (int i = 0; i < a.Length; i++)
                {
                    if (Over()) { yield return true; Still(); }
                    if (a[i].Gone) { UnityEngine.Object.Destroy(a[i].Box); after--; continue; }
                    if (a[i].Members < 2) continue;
                    BoxCollider src = a[i].Box;
                    if (src == null) continue;
                    GameObject g = new GameObject("MergedBoxes_" + src.gameObject.name);
                    SceneManager.MoveGameObjectToScene(g, s);
                    g.layer = src.gameObject.layer;
                    g.tag = src.gameObject.tag;
                    g.transform.position = q * ((a[i].Min + a[i].Max) * 0.5f);
                    g.transform.rotation = q;
                    BoxCollider bc = g.AddComponent<BoxCollider>();
                    bc.size = a[i].Max - a[i].Min;
                    bc.sharedMaterial = src.sharedMaterial;
                    UnityEngine.Object.Destroy(src);
                }
            }
            n.Before = before;
            n.After = after;
        }

        /// <summary>The union of two boxes of one frame when it is a box (or one
        /// holds the other): equal on two axes, touching on the third.</summary>
        static bool Union(Span a, Span b, float eps, out Vector3 mn, out Vector3 mx)
        {
            mn = Vector3.Min(a.Min, b.Min);
            mx = Vector3.Max(a.Max, b.Max);
            bool aHoldsB = true;
            for (int k = 0; k < 3; k++)
                if (b.Min[k] < a.Min[k] - eps || b.Max[k] > a.Max[k] + eps) aHoldsB = false;
            if (aHoldsB) { mn = a.Min; mx = a.Max; return true; }
            int free = -1;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(a.Min[k] - b.Min[k]) <= eps && Mathf.Abs(a.Max[k] - b.Max[k]) <= eps) continue;
                if (free >= 0) return false;
                free = k;
            }
            if (free < 0) return true;
            return b.Min[free] <= a.Max[free] + eps && a.Min[free] <= b.Max[free] + eps;
        }

        // ------------------------------------------------------------ interiors

        static IEnumerable<bool> FindShells(Transform n, Dictionary<Renderer, Bounds> rb, Profile p, int scene, Counts cn)
        {
            if (Over()) { yield return true; Still(); }
            if (n == null) yield break;
            Renderer[] rs = n.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) yield break;
            Bounds all = rb.ContainsKey(rs[0]) ? rb[rs[0]] : rs[0].bounds;
            foreach (Renderer r in rs) all.Encapsulate(rb.ContainsKey(r) ? rb[r] : r.bounds);
            float w = Mathf.Min(all.size.x, all.size.z), wmax = Mathf.Max(all.size.x, all.size.z);
            if (wmax > p.ShellMaxU)
            {
                // The children as they are now (the list stays put across yields).
                Transform[] kids = new Transform[n.childCount];
                for (int i = 0; i < kids.Length; i++) kids[i] = n.GetChild(i);
                foreach (Transform c in kids)
                    foreach (bool y in FindShells(c, rb, p, scene, cn)) yield return y;
                yield break;
            }
            if (w < p.ShellMinU || all.size.y < p.ShellMinHeightU) yield break;
            // the shell: the widest solid collider that covers most of the footprint
            Bounds shell = new Bounds();
            bool found = false;
            foreach (Collider c in n.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger || !c.enabled) continue;
                Bounds b = c.bounds;
                if (b.size.x < p.ShellCover * all.size.x || b.size.z < p.ShellCover * all.size.z) continue;
                if (!found) { shell = b; found = true; }
                else shell.Encapsulate(b);
            }
            if (!found) yield break;
            // the top of the shell: the renderers' top when the shell collider is a floor or pad
            if (shell.max.y < all.max.y - p.InsetU)
                shell.SetMinMax(shell.min, new Vector3(shell.max.x, all.max.y, shell.max.z));
            if (shell.size.y < p.ShellMinHeightU) yield break;
            Vector3 lo = shell.min + new Vector3(p.InsetU, 0f, p.InsetU), hi = shell.max - Vector3.one * p.InsetU;
            float big = 0.6f * Mathf.Min(shell.size.x, shell.size.z);
            List<Renderer> inner = new List<Renderer>();
            foreach (Renderer r in rs)
            {
                Bounds b = rb.ContainsKey(r) ? rb[r] : r.bounds;
                if (Size(b) >= big) continue;
                if (b.min.x < lo.x || b.min.z < lo.z || b.max.x > hi.x || b.max.z > hi.z || b.max.y > hi.y) continue;
                inner.Add(r);
            }
            if (inner.Count < 3) yield break;
            Interior it = new Interior();
            it.Name = n.name;
            it.Shell = shell;
            it.Renderers = inner.ToArray();
            it.P = p;
            it.Scene = scene;
            it.LastSeen = Time.realtimeSinceStartup;
            List<Vector3> pts = new List<Vector3>();
            float[] fy = { 0.2f, 0.6f };
            float[] fx = { 0.2f, 0.5f, 0.8f };
            foreach (float y in fy)
                foreach (float x in fx)
                    foreach (float z in fx)
                        pts.Add(new Vector3(Mathf.Lerp(lo.x, hi.x, x), Mathf.Lerp(shell.min.y, hi.y, y), Mathf.Lerp(lo.z, hi.z, z)));
            it.Points = pts.ToArray();
            _interiors.Add(it);
            cn.Interiors++;
            cn.InteriorRenderers += inner.Count;
        }

        // P1b: the 18 linecasts of one building used to run in one frame, one
        // building per frame. Now at most RaysPerFrame linecasts a frame: a
        // building's test resumes at its next point in the following frame,
        // buildings that need no ray (near, beyond FarU) cost nothing and do
        // not stop the round. A hidden building the camera has not moved
        // RetestMoveU away from since its last full test keeps its answer for
        // RetestSeconds (vehicles and doors still get a fresh test then).
        const int RaysPerFrame = 6;
        const float RetestMoveU = 1f;
        const float RetestSeconds = 2f;

        static void Occlusion(float now)
        {
            int count = _interiors.Count;
            if (count == 0) return;
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            int rays = RaysPerFrame;
            for (int visits = 0; visits < count && rays > 0; visits++)
            {
                if (_next >= count) _next = 0;
                Interior it = _interiors[_next];
                int r = See(it, eye, now, ref rays);
                if (r < 0) return;                          // budget spent mid-building: resume here
                _next++;
                bool seen = r > 0;
                if (seen) it.LastSeen = now;
                bool show = seen || now - it.LastSeen < it.P.HoldSeconds;
                if (show == it.Shown) continue;
                it.Shown = show;
                Renderer[] rs = it.Renderers;
                for (int i = 0; i < rs.Length; i++)
                    if (rs[i] != null) rs[i].enabled = show;
            }
        }

        /// <summary>1 = a ray gets in, 0 = none does, -1 = the frame's rays
        /// ran out first (the next call resumes at the same point).</summary>
        static int See(Interior it, Vector3 eye, float now, ref int rays)
        {
            Bounds near = it.Shell;
            near.Expand(it.P.NearU * 2f);
            if (near.Contains(eye)) { it.Ray = 0; return 1; }
            if (it.Shell.SqrDistance(eye) > it.P.FarU * it.P.FarU) { it.Ray = 0; return 0; }
            if (it.Ray == 0 && !it.Shown && now < it.TestedAt + RetestSeconds
                && (eye - it.TestedEye).sqrMagnitude < RetestMoveU * RetestMoveU)
                return 0;                                   // nothing moved: still no line in
            Bounds inside = it.Shell;
            inside.Expand(-it.P.InsetU * 4f);               // 2 u in from every side
            while (it.Ray < it.Points.Length)
            {
                if (rays <= 0) return -1;
                rays--;
                RaycastHit hit;
                Vector3 pt = it.Points[it.Ray++];
                if (!Physics.Linecast(eye, pt, out hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    || inside.Contains(hit.point))
                {
                    it.Ray = 0;
                    return 1;
                }
            }
            it.Ray = 0;
            it.TestedAt = now;
            it.TestedEye = eye;
            return 0;
        }

        // ------------------------------------------------------------ combine (P2)

        // P2 (docs/ai/tasks/p2-render-batching.md): the frame is CPU bound, and
        // static batching (step 5) saves draw calls but not the per-renderer
        // cost - every batched renderer is still culled, sorted and submitted
        // on its own, and a town block brings ~410 of them. The combine bakes
        // real meshes instead: per OWNER (one level mask of a building's
        // LODGroup, one interior, or one cluster of small one-level prop
        // groups in a ClusterU cell and cull band) and per material, the
        // sub-meshes are copied into merged meshes of at most CombineMaxVerts
        // vertices (only the vertices each sub-mesh uses; mirrored parts get
        // their winding turned). The merged renderer takes the originals'
        // place in the LODGroup / interior; a cluster gets one LODGroup that
        // culls where its nearest-culling member did. The originals are
        // switched off (not destroyed: game code may still read them).
        //
        // Never merged: objects other modules bind by name and then move,
        // swap, hide or re-skin - keep HandsOffNames in step with them:
        // Flak Names/EmptyName (AA holders, derelict ZU swapped), FuelDepot
        // ModelNames (tanks blown), PlayerAn2 StandInName (SetActive),
        // TowerRadar RadarName with its Head (turns) and Elements (switched),
        // EastWorld's Fallback group, EastZones Markers, the content ladders.
        // Also anything under a MonoBehaviour or Rigidbody, transparent or
        // DisableBatching materials, lightmapped or non-triangle meshes, and
        // renderers switched off when the pass comes (another module's).
        static readonly string[] HandsOffNames = {
            "AA position north", "AA position S2-S3", "Town battery west", "Town battery east",
            "AA position V3 (empty)",
            "pol_tank_vertical 1", "pol_tank_vertical_split_roof 1",
            "pol_tank_horizontal 1", "pol_tank_horizontal 2", "pol_tank_horizontal 3",
            "AN An-2 (nose east)", TowerRadar.RadarName, "Head", "Elements", "Fallback", "Markers", "Ladder",
        };
        static readonly string[] HandsOffPrefixes = { "NDR", "Edge light", "Ladder_", "P2 " };
        static readonly float[] Bands = { 300f, 420f, 700f, 1000f, 1500f, 2200f, 3000f };

        static ConfigEntry<bool> _cfgCombine;
        static bool CombineOn { get { return _cfgCombine == null || _cfgCombine.Value; } }
        static readonly HashSet<Renderer> _retired = new HashSet<Renderer>();
        static readonly Dictionary<MeshFilter, Mesh> _madeMeshes = new Dictionary<MeshFilter, Mesh>();

        sealed class Src
        {
            internal MeshRenderer R;
            internal Mesh M;
            internal int Sub;
            internal Vector3 C;
            internal int Verts;
        }

        sealed class Group
        {
            internal Material Mat;
            internal MeshRenderer First;
            internal readonly List<Src> Items = new List<Src>();
        }

        sealed class Owner
        {
            internal LODGroup Lod;          // the existing group the sources are in (not for a cluster)
            internal int Mask;              // its levels the sources are in
            internal Interior In;
            internal bool Cluster;
            internal float Cull = float.MaxValue;
            internal Transform Parent;
            internal Scene Scene;
            internal readonly List<Group> Groups = new List<Group>();
            internal readonly Dictionary<string, Group> ByKey = new Dictionary<string, Group>();
            internal readonly HashSet<MeshRenderer> Renderers = new HashSet<MeshRenderer>();
            internal readonly HashSet<LODGroup> Pieces = new HashSet<LODGroup>();   // a cluster's members' groups
            internal readonly List<Renderer> Made = new List<Renderer>();
        }

        sealed class MeshData
        {
            internal Vector3[] V, N;
            internal Vector4[] T;
            internal Vector2[] U, U2;
            internal Color32[] C;
            internal int[][] Tris;
        }

        static readonly Dictionary<Mesh, MeshData> _meshData = new Dictionary<Mesh, MeshData>();
        static int[] _mapIdx = new int[0], _mapStamp = new int[0];
        static int _stamp;

        static bool HandsOff(Transform t, Dictionary<Transform, bool> cache)
        {
            if (t == null) return false;
            bool v;
            if (cache.TryGetValue(t, out v)) return v;
            string name = t.name;
            v = Array.IndexOf(HandsOffNames, name) >= 0;
            for (int i = 0; i < HandsOffPrefixes.Length && !v; i++)
                if (name.StartsWith(HandsOffPrefixes[i], StringComparison.Ordinal)) v = true;
            if (!v) v = t.GetComponent<MonoBehaviour>() != null || t.GetComponent<Rigidbody>() != null;
            if (!v) v = HandsOff(t.parent, cache);
            cache[t] = v;
            return v;
        }

        /// <summary>The renderer's mesh when every sub-mesh it draws can be
        /// merged, else null.</summary>
        static Mesh Mergeable(MeshRenderer mr, Profile p, Dictionary<Transform, bool> cache)
        {
            if (mr == null || !mr.gameObject.activeInHierarchy || mr.isPartOfStaticBatch) return null;
            if (mr.lightmapIndex >= 0 && mr.lightmapIndex < 65534) return null;
            if (mr.additionalVertexStreams != null) return null;
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            Mesh m = mf != null ? mf.sharedMesh : null;
            if (m == null || !m.isReadable || m.vertexCount == 0 || m.vertexCount > p.CombineMaxVerts) return null;
            Material[] mats = mr.sharedMaterials;
            if (mats.Length == 0 || mats.Length > m.subMeshCount) return null;
            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (mat == null || mat.renderQueue >= 2500) return null;
                string tag = mat.GetTag("DisableBatching", false);
                if (!string.IsNullOrEmpty(tag) && !string.Equals(tag, "False", StringComparison.OrdinalIgnoreCase)) return null;
                if (m.GetTopology(i) != MeshTopology.Triangles) return null;
            }
            if (HandsOff(mr.transform, cache)) return null;
            return m;
        }

        static int Band(float d)
        {
            for (int i = 0; i < Bands.Length; i++) if (d <= Bands[i] + 0.5f) return i;
            return Bands.Length;
        }

        static IEnumerable<bool> Combine(Scene s, GameObject[] roots, List<Renderer> all, Profile p, Counts n)
        {
            int scene = s.GetHashCode();
            _meshData.Clear();
            // Every renderer's LODGroup and levels (bit i = LOD i); one in two groups stays as it is.
            Dictionary<Renderer, LODGroup> lodOf = new Dictionary<Renderer, LODGroup>();
            Dictionary<Renderer, int> maskOf = new Dictionary<Renderer, int>();
            HashSet<Renderer> twice = new HashSet<Renderer>();
            Dictionary<LODGroup, LOD[]> lodsOf = new Dictionary<LODGroup, LOD[]>();
            List<LODGroup> lgs = new List<LODGroup>();
            foreach (GameObject r in roots) if (r != null) lgs.AddRange(r.GetComponentsInChildren<LODGroup>());
            foreach (LODGroup lg in lgs)
            {
                if (Over()) { yield return true; Still(); }
                if (lg == null) continue;
                LOD[] lods = lg.GetLODs();
                lodsOf[lg] = lods;
                for (int i = 0; i < lods.Length && i < 30; i++)
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r == null) continue;
                        LODGroup had;
                        if (lodOf.TryGetValue(r, out had) && had != lg) { twice.Add(r); continue; }
                        if (!lg.enabled) twice.Add(r);
                        lodOf[r] = lg;
                        int m;
                        maskOf.TryGetValue(r, out m);
                        maskOf[r] = m | (1 << i);
                    }
            }
            Dictionary<Renderer, Interior> inOf = new Dictionary<Renderer, Interior>();
            Dictionary<Interior, int> inIdx = new Dictionary<Interior, int>();
            foreach (Interior it in _interiors)
            {
                if (it.Scene != scene) continue;
                inIdx[it] = inIdx.Count;
                foreach (Renderer r in it.Renderers) if (r != null) inOf[r] = it;
            }

            // The renderers that can go, and the one-level prop groups whose every renderer can.
            Dictionary<Transform, bool> cache = new Dictionary<Transform, bool>();
            Dictionary<MeshRenderer, Mesh> ok = new Dictionary<MeshRenderer, Mesh>();
            foreach (Renderer r in all)
            {
                if (Over()) { yield return true; Still(); }
                MeshRenderer mr = r as MeshRenderer;
                if (mr == null || twice.Contains(mr)) continue;
                Interior it;
                bool inside = inOf.TryGetValue(mr, out it);
                if (!mr.enabled && !inside) continue;            // switched off by somebody else
                Mesh m = Mergeable(mr, p, cache);
                if (m != null) ok[mr] = m;
            }
            Dictionary<LODGroup, bool> piece = new Dictionary<LODGroup, bool>();
            foreach (KeyValuePair<LODGroup, LOD[]> kv in lodsOf)
            {
                if (Over()) { yield return true; Still(); }
                LODGroup lg = kv.Key;
                LOD[] lods = kv.Value;
                bool can = lg != null && lg.enabled && lods.Length == 1 && WorldSize(lg) < p.LargeU
                           && lods[0].renderers.Length > 0;
                Interior first = null;
                for (int i = 0; can && i < lods[0].renderers.Length; i++)
                {
                    MeshRenderer mr = lods[0].renderers[i] as MeshRenderer;
                    Interior it;
                    if (mr == null || !ok.ContainsKey(mr)) { can = false; break; }
                    inOf.TryGetValue(mr, out it);
                    if (i == 0) first = it;
                    else if (it != first) can = false;
                }
                piece[lg] = can;
            }

            // Owners and their per-material groups.
            Dictionary<string, Owner> owners = new Dictionary<string, Owner>();
            List<Owner> order = new List<Owner>();
            foreach (KeyValuePair<MeshRenderer, Mesh> kv in ok)
            {
                if (Over()) { yield return true; Still(); }
                MeshRenderer mr = kv.Key;
                if (mr == null) continue;
                LODGroup lg;
                lodOf.TryGetValue(mr, out lg);
                Interior it;
                inOf.TryGetValue(mr, out it);
                int ii = it != null ? inIdx[it] : -1;
                Bounds b = mr.bounds;
                string key;
                bool cluster = false;
                float cull = 0f;
                bool isPiece;
                if (lg != null && piece.TryGetValue(lg, out isPiece) && isPiece)
                {
                    cull = Distance(WorldSize(lg), lodsOf[lg][0].screenRelativeTransitionHeight);
                    Vector3 at = lg.transform.TransformPoint(lg.localReferencePoint);
                    key = "C" + Mathf.FloorToInt(at.x / p.ClusterU) + "," + Mathf.FloorToInt(at.z / p.ClusterU)
                          + "," + Band(cull) + "," + ii;
                    cluster = true;
                }
                else if (lg != null) key = "L" + lg.GetInstanceID() + "," + maskOf[mr] + "," + ii;
                else if (it != null) key = "I" + ii;
                else continue;                                  // a free large piece: static batching's
                Owner o;
                if (!owners.TryGetValue(key, out o))
                {
                    o = new Owner();
                    o.Cluster = cluster;
                    o.Lod = cluster ? null : lg;
                    o.Mask = cluster || lg == null ? 0 : maskOf[mr];
                    o.In = it;
                    o.Scene = s;
                    o.Parent = cluster ? null : lg != null ? lg.transform : mr.transform.root;
                    owners[key] = o;
                    order.Add(o);
                }
                if (cluster) { o.Cull = Mathf.Min(o.Cull, cull); o.Pieces.Add(lg); }
                o.Renderers.Add(mr);
                Mesh mesh = kv.Value;
                Material[] mats = mr.sharedMaterials;
                for (int k = 0; k < mats.Length; k++)
                {
                    string gk = mats[k].GetInstanceID() + "," + (int)mr.shadowCastingMode + "," + (mr.receiveShadows ? 1 : 0)
                                + "," + mr.gameObject.layer;
                    Group g;
                    if (!o.ByKey.TryGetValue(gk, out g))
                    {
                        g = new Group();
                        g.Mat = mats[k];
                        g.First = mr;
                        o.ByKey[gk] = g;
                        o.Groups.Add(g);
                    }
                    Src src = new Src();
                    src.R = mr;
                    src.M = mesh;
                    src.Sub = k;
                    src.C = b.center;
                    src.Verts = Mathf.Min(mesh.vertexCount, (int)mesh.GetIndexCount(k));
                    g.Items.Add(src);
                }
            }

            foreach (Owner o in order)
            {
                if (o.Renderers.Count < 2) { n.Kept += o.Renderers.Count; continue; }
                if (o.Cluster)
                {
                    GameObject root = new GameObject("P2 cluster");
                    SceneManager.MoveGameObjectToScene(root, o.Scene);
                    o.Parent = root.transform;
                }
                if (o.Parent == null || Mathf.Abs(o.Parent.localToWorldMatrix.determinant) < 1e-9f)
                {
                    n.Kept += o.Renderers.Count;
                    continue;
                }
                foreach (Group g in o.Groups)
                {
                    g.Items.Sort(_near);
                    int at = 0;
                    while (at < g.Items.Count)
                    {
                        int end = at, verts = 0;
                        while (end < g.Items.Count && (end == at || verts + g.Items[end].Verts <= p.CombineMaxVerts))
                            verts += g.Items[end++].Verts;
                        foreach (bool y in Build(o, g, at, end)) yield return y;
                        at = end;
                    }
                }
                Swap(o, n);
                if (Over()) { yield return true; Still(); }
            }
            _meshData.Clear();
        }

        /// <summary>Chunk order: 32 u cells, x-major, so a chunk stays compact.</summary>
        static readonly Comparison<Src> _near = delegate(Src a, Src b)
        {
            int c = Mathf.FloorToInt(a.C.x / 32f).CompareTo(Mathf.FloorToInt(b.C.x / 32f));
            if (c != 0) return c;
            c = Mathf.FloorToInt(a.C.z / 32f).CompareTo(Mathf.FloorToInt(b.C.z / 32f));
            return c != 0 ? c : a.C.y.CompareTo(b.C.y);
        };

        static MeshData Data(Mesh m)
        {
            MeshData d;
            if (_meshData.TryGetValue(m, out d)) return d;
            d = new MeshData();
            d.V = m.vertices;
            int n = d.V.Length;
            Vector3[] nn = m.normals;
            d.N = nn != null && nn.Length == n ? nn : null;
            Vector4[] tt = m.tangents;
            d.T = tt != null && tt.Length == n ? tt : null;
            Vector2[] uu = m.uv;
            d.U = uu != null && uu.Length == n ? uu : null;
            Vector2[] u2 = m.uv2;
            d.U2 = u2 != null && u2.Length == n ? u2 : null;
            Color32[] cc = m.colors32;
            d.C = cc != null && cc.Length == n ? cc : null;
            d.Tris = new int[m.subMeshCount][];
            _meshData[m] = d;
            return d;
        }

        /// <summary>One merged mesh from g.Items[from..to): the vertices are
        /// copied one source per slice, the mesh is made in the last step.</summary>
        static IEnumerable<bool> Build(Owner o, Group g, int from, int to)
        {
            bool hasN = false, hasT = false, hasU = false, hasU2 = false, hasC = false;
            for (int i = from; i < to; i++)
            {
                if (Over()) { yield return true; Still(); }
                if (g.Items[i].M == null) continue;
                MeshData d = Data(g.Items[i].M);
                hasN |= d.N != null; hasT |= d.T != null; hasU |= d.U != null; hasU2 |= d.U2 != null; hasC |= d.C != null;
            }
            List<Vector3> vs = new List<Vector3>(), ns = new List<Vector3>();
            List<Vector4> ts = new List<Vector4>();
            List<Vector2> us = new List<Vector2>(), u2s = new List<Vector2>();
            List<Color32> cs = new List<Color32>();
            List<int> tris = new List<int>();
            Matrix4x4 w2l = o.Parent.worldToLocalMatrix;
            for (int i = from; i < to; i++)
            {
                if (Over()) { yield return true; Still(); }
                Src src = g.Items[i];
                if (src.R == null || src.M == null) continue;
                MeshData d = Data(src.M);
                int[] st = d.Tris[src.Sub];
                if (st == null) st = d.Tris[src.Sub] = src.M.GetTriangles(src.Sub);
                Matrix4x4 m = w2l * src.R.localToWorldMatrix;
                Matrix4x4 nm = m.inverse.transpose;
                bool flip = m.determinant < 0f;
                int nv = d.V.Length;
                if (_mapIdx.Length < nv) { _mapIdx = new int[nv]; _mapStamp = new int[nv]; }
                _stamp++;
                for (int t = 0; t + 2 < st.Length; t += 3)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int v = st[t + c];
                        if (_mapStamp[v] == _stamp) continue;
                        _mapStamp[v] = _stamp;
                        _mapIdx[v] = vs.Count;
                        vs.Add(m.MultiplyPoint3x4(d.V[v]));
                        if (hasN) ns.Add(d.N != null ? nm.MultiplyVector(d.N[v]).normalized : Vector3.up);
                        if (hasT)
                        {
                            if (d.T != null)
                            {
                                Vector4 tg = d.T[v];
                                Vector3 x = m.MultiplyVector(new Vector3(tg.x, tg.y, tg.z)).normalized;
                                ts.Add(new Vector4(x.x, x.y, x.z, flip ? -tg.w : tg.w));
                            }
                            else ts.Add(new Vector4(1f, 0f, 0f, 1f));
                        }
                        if (hasU) us.Add(d.U != null ? d.U[v] : Vector2.zero);
                        if (hasU2) u2s.Add(d.U2 != null ? d.U2[v] : Vector2.zero);
                        if (hasC) cs.Add(d.C != null ? d.C[v] : new Color32(255, 255, 255, 255));
                    }
                    int a = _mapIdx[st[t]], b = _mapIdx[st[t + 1]], e = _mapIdx[st[t + 2]];
                    tris.Add(a);
                    if (flip) { tris.Add(e); tris.Add(b); }
                    else { tris.Add(b); tris.Add(e); }
                }
            }
            if (Over()) { yield return true; Still(); }
            if (vs.Count == 0 || o.Parent == null) yield break;
            Mesh cm = new Mesh();
            cm.name = "P2 merged " + g.Mat.name;
            if (vs.Count > 65535) cm.indexFormat = IndexFormat.UInt32;
            cm.SetVertices(vs);
            if (hasN) cm.SetNormals(ns);
            if (hasT) cm.SetTangents(ts);
            if (hasU) cm.SetUVs(0, us);
            if (hasU2) cm.SetUVs(1, u2s);
            if (hasC) cm.SetColors(cs);
            cm.SetTriangles(tris, 0);
            cm.RecalculateBounds();
            // Readable on purpose: step 5 statically batches the merged meshes
            // too (same material across buildings = one draw) and then frees
            // every merged mesh it copied (Batch, _madeMeshes).
            GameObject go = new GameObject("P2 merged " + o.Made.Count);
            go.layer = g.First.gameObject.layer;
            go.transform.SetParent(o.Parent, false);
            MeshFilter cmf = go.AddComponent<MeshFilter>();
            cmf.sharedMesh = cm;
            _madeMeshes[cmf] = cm;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = g.Mat;
            mr.shadowCastingMode = g.First.shadowCastingMode;
            mr.receiveShadows = g.First.receiveShadows;
            mr.lightProbeUsage = g.First.lightProbeUsage;
            mr.reflectionProbeUsage = g.First.reflectionProbeUsage;
            mr.enabled = false;                                 // on in Swap, with the originals off
            o.Made.Add(mr);
        }

        /// <summary>The merged renderers take the originals' place, in one frame.</summary>
        static void Swap(Owner o, Counts n)
        {
            if (o.Made.Count == 0) { n.Kept += o.Renderers.Count; return; }
            Renderer[] made = o.Made.ToArray();
            if (o.Lod != null)
            {
                LOD[] lods = o.Lod.GetLODs();
                for (int i = 0; i < lods.Length && i < 30; i++)
                    if ((o.Mask & (1 << i)) != 0) lods[i].renderers = Replace(lods[i].renderers, o.Renderers, made);
                o.Lod.SetLODs(lods);
            }
            if (o.Cluster)
            {
                LODGroup lg = o.Parent.gameObject.AddComponent<LODGroup>();
                lg.SetLODs(new LOD[] { new LOD(0.5f, made) });
                lg.RecalculateBounds();
                LOD[] one = lg.GetLODs();
                one[0].screenRelativeTransitionHeight = Height(WorldSize(lg), o.Cull);
                lg.SetLODs(one);
                // the members' own one-level groups go when nothing is left in them
                foreach (LODGroup g in o.Pieces)
                {
                    if (g == null) continue;
                    LOD[] ls = g.GetLODs();
                    bool empty = true;
                    for (int i = 0; i < ls.Length; i++)
                    {
                        ls[i].renderers = Replace(ls[i].renderers, o.Renderers, new Renderer[0]);
                        if (ls[i].renderers.Length > 0) empty = false;
                    }
                    if (empty) UnityEngine.Object.Destroy(g);
                    else g.SetLODs(ls);
                }
                n.Clusters++;
            }
            bool show = o.In == null || o.In.Shown;
            if (o.In != null) o.In.Renderers = Replace(o.In.Renderers, o.Renderers, made);
            for (int i = 0; i < made.Length; i++) made[i].enabled = show;
            foreach (MeshRenderer r in o.Renderers)
            {
                if (r == null) continue;
                ViewDistance.Retire(r);
                r.enabled = false;
                _retired.Add(r);
            }
            n.Merged += o.Renderers.Count;
            n.MergedInto += made.Length;
        }

        static Renderer[] Replace(Renderer[] had, HashSet<MeshRenderer> gone, Renderer[] add)
        {
            List<Renderer> l = new List<Renderer>(had.Length + add.Length);
            for (int i = 0; i < had.Length; i++)
            {
                MeshRenderer mr = had[i] as MeshRenderer;
                if (had[i] != null && (mr == null || !gone.Contains(mr))) l.Add(had[i]);
            }
            l.AddRange(add);
            return l.ToArray();
        }

        // ------------------------------------------------------------ batching

        // One Combine call per root was the longest single step (every mesh of
        // a building bundle in one frame); a root with more than this many
        // pieces is combined in runs of it, each its own frame at most.
        const int BatchRun = 64;

        static IEnumerable<bool> Batch(GameObject[] roots, Counts cn)
        {
            int n = 0;
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                List<GameObject> gos = new List<GameObject>();
                foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if (Over()) { yield return true; Still(); }
                    if (mr == null || mr.isPartOfStaticBatch || _retired.Contains(mr)) continue;
                    MeshFilter mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    if (mr.sharedMaterials.Length > mf.sharedMesh.subMeshCount) continue;
                    gos.Add(mr.gameObject);
                }
                if (gos.Count < 2) continue;
                for (int at = 0; at < gos.Count; )
                {
                    if (at > 0 && Over()) { yield return true; Still(); }
                    int len = gos.Count - at;
                    if (len > BatchRun) len = len - BatchRun == 1 ? BatchRun + 1 : BatchRun;   // never a lone last piece
                    List<GameObject> run = gos.GetRange(at, len);
                    at += len;
                    run.RemoveAll(_gone);
                    if (run.Count < 2) continue;
                    StaticBatchingUtility.Combine(run.ToArray(), root);
                    // P2: a merged mesh the batch copied is not needed any more
                    foreach (GameObject g in run)
                    {
                        MeshFilter mf = g.GetComponent<MeshFilter>();
                        Mesh made;
                        if (mf == null || !_madeMeshes.TryGetValue(mf, out made)) continue;
                        _madeMeshes.Remove(mf);
                        if (made != null && mf.sharedMesh != made) UnityEngine.Object.Destroy(made);
                    }
                    n += run.Count;
                }
            }
            cn.Batched = n;
        }

        static readonly Predicate<GameObject> _gone = delegate(GameObject g) { return g == null; };

        static void Log(string s) { RevivalPlugin.L.LogInfo("ContentPerf: " + s); }
    }
}

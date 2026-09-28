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
//                reaches inside: one building per frame, 18 linecasts to points
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
            internal float LargeU = 20f;         // pieces this big get no new cull
            internal float CullPerU = 80f;       // cull distance per unit of size
            internal float CullMinU = 150f;      // ~54 m
            internal float CullMaxU = 1500f;     // ~540 m
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

        const double SliceMs = 1.5;
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
        }

        static IEnumerable<bool> Apply(Scene s, Profile p)
        {
            _tan = TanHalfFovNow();
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
            if (p.Batch) foreach (bool y in Batch(roots, n)) yield return y;

            Log(s.name + " (" + p.Name + "): " + all.Count + " renderers, " + shadows + " stop casting shadows, "
                + n.Groups + " cull groups added (" + n.Pulled + " renderers out of building LODGroups, " + n.Tightened
                + " authored culls pulled in), box colliders " + n.Before + " -> " + n.After + ", " + n.Interiors
                + " interior(s) with " + n.InteriorRenderers + " renderers, " + n.Batched + " renderers statically batched; "
                + (_runMs + (Stopwatch.GetTimestamp() - _sliceStart) * 1000.0 / Stopwatch.Frequency).ToString("F0")
                + " ms over " + _runFrames + " frame(s).");
        }

        static float Size(Bounds b) { return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)); }

        static float _tan = 0.57735f;

        static float TanHalfFov() { return _tan; }

        static float TanHalfFovNow()
        {
            Camera c = Camera.main;
            float fov = c != null ? c.fieldOfView : 60f;
            return Mathf.Tan(Mathf.Clamp(fov, 20f, 120f) * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>Screen relative height for a cull at distance d of a group
        /// this big (Unity: size / (2 d tan(fov/2)), before lodBias).</summary>
        static float Height(float size, float d)
        {
            return Mathf.Clamp(size / (2f * Mathf.Max(d, 1f) * TanHalfFov()), 0.0005f, 0.99f);
        }

        static float Distance(float size, float h)
        {
            return size / (2f * Mathf.Max(h, 0.0001f) * TanHalfFov());
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
                // an authored cull beyond CullMaxU comes in to it (large groups keep theirs)
                if (ws < p.LargeU && Distance(ws, lods[lods.Length - 1].screenRelativeTransitionHeight) > p.CullMaxU)
                {
                    float h = Height(ws, p.CullMaxU);
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
                    if (Mathf.Min(p.CullMaxU, Mathf.Max(p.CullMinU, p.CullPerU * Size(b))) >= far) continue;
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
                if (size >= p.LargeU && limit == float.MaxValue) continue;
                if (kv.Key.GetComponent<LODGroup>() != null) continue;
                float d = Mathf.Min(limit, Mathf.Min(p.CullMaxU, Mathf.Max(p.CullMinU, p.CullPerU * size)));
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

        static void Occlusion(float now)
        {
            if (_interiors.Count == 0) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_next >= _interiors.Count) _next = 0;
            Interior it = _interiors[_next++];
            Vector3 eye = cam.transform.position;
            bool seen = See(it, eye);
            if (seen) it.LastSeen = now;
            bool show = seen || now - it.LastSeen < it.P.HoldSeconds;
            if (show == it.Shown) return;
            it.Shown = show;
            foreach (Renderer r in it.Renderers)
                if (r != null) r.enabled = show;
        }

        static bool See(Interior it, Vector3 eye)
        {
            Bounds near = it.Shell;
            near.Expand(it.P.NearU * 2f);
            if (near.Contains(eye)) return true;
            if (it.Shell.SqrDistance(eye) > it.P.FarU * it.P.FarU) return false;
            Bounds inside = it.Shell;
            inside.Expand(-it.P.InsetU * 4f);           // 2 u in from every side
            for (int i = 0; i < it.Points.Length; i++)
            {
                RaycastHit hit;
                if (!Physics.Linecast(eye, it.Points[i], out hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return true;
                if (inside.Contains(hit.point)) return true;
            }
            return false;
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
                    if (mr == null || mr.isPartOfStaticBatch) continue;
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
                    n += run.Count;
                }
            }
            cn.Batched = n;
        }

        static readonly Predicate<GameObject> _gone = delegate(GameObject g) { return g == null; };

        static void Log(string s) { RevivalPlugin.L.LogInfo("ContentPerf: " + s); }
    }
}

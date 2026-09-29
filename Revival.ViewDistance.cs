// Next Day: Survival - Revival Toolkit
//
// VIEW DISTANCE. A Low / Medium / High / Ultra setting for how far the world
// is drawn, made for air combat, AA and bombing: aircraft, flak bursts,
// tracers, the airfield and the towns have to be visible from far away,
// without the frame time the vanilla renderer would need for that.
//
// WHAT THE GAME DOES (measured offline, docs/ai/tasks/view-distance.md):
//   - camera far clip 1000 u on GW_Scene_1 (PostProcessingSceneLoader, the
//     default branch; 3000 u only while parachuting, SetCameraFarClipMax);
//   - linear fog 1..1600 u, so the far clip cuts the world at 62 % fog;
//   - terrain pixel error 10, tree distance 2000 (clipped at 1000 by the far
//     plane), billboards 50..150 u by the game's TreeDistance setting;
//   - lodBias 2.0 on all four quality levels; no layer cull distances;
//   - 6086 of 6113 scene renderers on layer 0, 996 LODGroups - most props are
//     plain MeshRenderers drawn out to the far clip, whatever their size.
//
// WHAT A LEVEL DOES (the table in Profiles; Off = the game untouched):
//   1. far clip = max(game's, level's). A level never shortens the game's own
//      far clip, so the parachute's 3000 u still wins over Low and Medium.
//   2. SMALL AND MEDIUM PROPS CULLED EARLY: every static layer-0 MeshRenderer
//      without an LODGroup is sorted by its bounds diagonal - small (< 8 u),
//      medium (< 40 u), large. Two CullingGroups (the engine does the
//      distance test natively, no per-frame C# loop) switch small props off
//      past Small u and medium props past Medium u. Large ones (buildings,
//      hangars, the runway greybox) are drawn to the far clip. This is what
//      pays for the longer far clip: today every crate out to 1000 u is drawn.
//   3. LARGE LODGROUPS REACH FURTHER: an LODGroup of 40 u and more keeps its
//      last (coarsest) LOD out to the far clip, instead of culling at its
//      screen size (a 158 u hangar culls at 1830 u) - the existing far LOD is
//      the impostor. Smaller LODGroups are left to the game.
//   4. LAYER CULL DISTANCES for the layers that only carry small things
//      (loot items, campfire items, bushes, ragdoll bones); spherical, so
//      turning the head does not pop them.
//   5. terrain: pixel error and tree distance per level (billboards stay the
//      game's setting; basemap is EastWorld's business).
//   6. lodBias per level.
//   7. linear fog: the end is pushed to 1.07 x the far clip when that is
//      further than the game's, the start scaled with it - the far edge fades
//      into the haze instead of being cut, and the air in between is clearer.
//      Exponential fog (never set by GW_Scene_1) is left alone.
//      P2 haze: the end never goes past HazeEndU (1.6 km), so every level is
//      at least half haze by 800 m, and step 3 keeps a far LOD only to where
//      the haze is HazeDetailFog thick - far detail drops inside the haze.
//
// AIRCRAFT, FLAK, TRACERS: nothing that moves is ever culled here - roots with
// a Rigidbody (every vehicle and aircraft), SkinnedMeshRenderers, particle
// systems and line renderers are not collected, and KeepVisible marks
// anything else. They are drawn out to the level's far clip, which is the
// level's combat range: Low 1000, Medium 2000, High 3500, Ultra 6000 u.
//
// OTHER WRITERS: the game writes the far clip (scene load, parachute), fog
// (parachute, underwater) and terrain values; FlightView blends its own
// profile on top in the air. Every field remembers what this file wrote; a
// different value is somebody else's and becomes the new base before the
// level is applied again. While FlightView holds its profile, nothing here is
// written, so its baseline is the level's value and its restore lands on it.
//
// MEASURING (BenchKey, PageDown): stand still, press it; the bench runs Off,
// Low, Medium, High, Ultra for SettleSeconds + SampleSeconds each and logs the
// average, 99th percentile and FPS per level, then the configured level is
// back. Off is "the game today", so Medium vs Off is the target check.
// CycleKey (PageUp) steps through the levels and saves the choice.
//
// Seams: RevivalPlugin.Awake (BindConfig), RevivalPlugin.LateUpdate
// (LateTick, before PlayerHeli.LateFrame), PlayerHeli/PlayerAn2.Prepare
// (KeepVisible). FlightView.Active. Tracers (LineRenderer) and flak bursts
// (ParticleSystem) need no call: only MeshRenderers are ever collected.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class ViewDistance
    {
        // ========================================================= profiles

        sealed class Profile
        {
            public string Name;
            public float Far, Trees, Pixel, LodBias, Small, Medium,
                Items, Camp, Bush, Ragdoll;
        }

        // Game units. Medium: twice today's far clip, paid for by the props
        // between 250 and 1000 u that are not drawn any more and a slightly
        // coarser far terrain. Trees stop at 1000 u - exactly where the game's
        // 1000 u far clip stopped them before P12 (its own tree distance of
        // 2000 was clipped there). Q1 perf: 6.57.0 had 1200 u, i.e. 44 % more
        // billboard area than 6.55 over the east tile's 67,738 terrain trees,
        // and legacy terrain rebuilds billboards on the main thread.
        static readonly Profile[] Profiles = new Profile[]
        {
            null,
            new Profile { Name = "Low",    Far = 1000f, Trees =  800f, Pixel = 15f, LodBias = 1.5f,
                          Small = 150f, Medium =  450f, Items = 120f, Camp = 200f, Bush = 300f, Ragdoll = 300f },
            new Profile { Name = "Medium", Far = 2000f, Trees = 1000f, Pixel = 12f, LodBias = 2.0f,
                          Small = 250f, Medium =  700f, Items = 150f, Camp = 250f, Bush = 400f, Ragdoll = 500f },
            new Profile { Name = "High",   Far = 3500f, Trees = 2000f, Pixel = 10f, LodBias = 2.0f,
                          Small = 350f, Medium = 1000f, Items = 200f, Camp = 300f, Bush = 500f, Ragdoll = 600f },
            new Profile { Name = "Ultra",  Far = 6000f, Trees = 3000f, Pixel =  8f, LodBias = 2.5f,
                          Small = 500f, Medium = 1500f, Items = 250f, Camp = 400f, Bush = 600f, Ragdoll = 800f },
        };

        static readonly string[] LevelNames = { "Off", "Low", "Medium", "High", "Ultra" };

        const float SmallSize = 8f;          // bounds diagonal, u
        const float LargeSize = 40f;         // bounds diagonal / LODGroup size, u
        const float FogOverFar = 1.07f;
        // P2 haze (docs/ai/tasks/p2-render-batching.md): the linear fog ends no
        // farther than HazeEndU on the ground, so on every level the air is at
        // least half haze by 800 m (2240 u) - Low and Medium already were, High
        // stays as it was, Ultra's 6420 u comes in. The far clip is untouched:
        // aircraft stay drawn to it (FlightView thins the fog again in the air).
        // The large LODGroups' far LOD is kept only to where the haze reaches
        // HazeDetailFog; past that the groups cull at their own distance.
        const float HazeEndU = 4480f;        // 1.6 km
        const float HazeDetailFog = 0.85f;

        // Layers (TagManager): 11/12 ragdoll bones, 16 campfire items,
        // 18 Bush_Tree, 19 ItemSpawned, 21-24 berry bushes, 30 TerrainBushes.
        static readonly int[] ItemLayers = { 19 };
        static readonly int[] CampLayers = { 16 };
        static readonly int[] BushLayers = { 18, 21, 22, 23, 24, 30 };
        static readonly int[] RagdollLayers = { 11, 12 };

        // =========================================================== config

        static ConfigEntry<string> _cfgLevel, _cfgCycleKey, _cfgBenchKey;
        static ConfigEntry<float> _cfgSettle, _cfgSample;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgLevel = cfg.Bind("ViewDistance", "Level", "Medium",
                "How far the world is drawn: Off (the game untouched), Low, Medium, "
                + "High, Ultra. Far clip / combat range 1000 / 2000 / 3500 / 6000 u; "
                + "small props are culled early to pay for it. Aircraft, flak and "
                + "tracers are always drawn out to the far clip.");
            _cfgCycleKey = cfg.Bind("ViewDistance", "CycleKey", "PageUp",
                "Steps Off -> Low -> Medium -> High -> Ultra in the game and saves "
                + "the choice. None = no key.");
            _cfgBenchKey = cfg.Bind("ViewDistance", "BenchKey", "PageDown",
                "Stand still and press: runs every level at this spot and logs the "
                + "frame time per level ([ViewDistance] bench lines). None = no key.");
            _cfgSettle = cfg.Bind("ViewDistance", "BenchSettleSeconds", 3f,
                "Bench: seconds after a level switch before sampling starts.");
            _cfgSample = cfg.Bind("ViewDistance", "BenchSampleSeconds", 6f,
                "Bench: seconds sampled per level.");
            SceneManager.sceneLoaded += delegate(Scene s, LoadSceneMode mode)
            {
                // The scene's props exist a few frames after the event; the
                // tile scene and late spawns are caught by the second pass.
                _scanAt = Time.unscaledTime + 8f;
                _scanAgainAt = Time.unscaledTime + 30f;
            };
        }

        static int ConfiguredLevel()
        {
            string v = _cfgLevel == null ? "Medium" : (_cfgLevel.Value ?? "").Trim();
            for (int i = 0; i < LevelNames.Length; i++)
                if (string.Equals(v, LevelNames[i], StringComparison.OrdinalIgnoreCase)) return i;
            return 2;
        }

        /// <summary>The level in force: the bench's while it runs, else the
        /// configured one.</summary>
        static int Level { get { return _benchStage >= 0 ? BenchOrder[_benchStage] : ConfiguredLevel(); } }

        // ======================================================= keep visible

        static readonly HashSet<int> _keep = new HashSet<int>();

        /// <summary>This object and everything under it are never culled
        /// here (aircraft, flak and tracer effects). Cheap; call it where the
        /// object is made.</summary>
        internal static void KeepVisible(GameObject go)
        {
            if (go == null) return;
            _keep.Add(go.GetInstanceID());
            if (_propCount > 0 || _job != JobNone) Release(go.transform);
        }

        /// <summary>P2: another module switched this renderer off for good
        /// (ContentPerf merged it into a combined mesh). It is forgotten here,
        /// so no band ever switches it back on.</summary>
        internal static void Retire(Renderer r)
        {
            if (r != null) _hidden.Remove(r);
        }

        // ============================================================ slots

        // One value with the base (the last value somebody else wrote) and
        // what this file wrote last.
        sealed class Slot
        {
            public float Base, Written;
            public bool Has;
        }

        static float Eps(float v) { return Mathf.Max(0.001f, Mathf.Abs(v) * 0.0005f); }

        /// <summary>The current value is not ours: it becomes the base.</summary>
        static void Settle(Slot s, float current)
        {
            if (s.Has && Mathf.Abs(current - s.Written) <= Eps(s.Written)) return;
            s.Base = current;
            s.Written = current;
            s.Has = true;
        }

        /// <summary>True when target has to be written.</summary>
        static bool Put(Slot s, float target)
        {
            if (Mathf.Abs(target - s.Written) <= Eps(target)) return false;
            s.Written = target;
            return true;
        }

        static bool Mine(Slot s, float current)
        {
            return s.Has && Mathf.Abs(current - s.Written) <= Eps(s.Written);
        }

        sealed class TerrainSlots { public Slot Trees = new Slot(), Pixel = new Slot(); }

        static readonly Slot _far = new Slot(), _lod = new Slot(),
            _fogStart = new Slot(), _fogEnd = new Slot();
        static readonly Dictionary<Terrain, TerrainSlots> _terrains = new Dictionary<Terrain, TerrainSlots>();
        static readonly List<Terrain> _terrainList = new List<Terrain>();
        static float _nextTerrainScan;
        static int _terrainCount = -1;
        static Camera _cam;
        static bool _cullSet;
        static float[] _cullBase;
        static float _nextCullCheck;

        // ============================================================ frame

        static int _applied = -1;
        static bool _baselineLogged, _failed;

        /// <summary>LateUpdate, after the game's writers and before
        /// FlightView. Seam: RevivalPlugin.LateUpdate.</summary>
        internal static void LateTick()
        {
            if (_failed) return;
            try
            {
                Keys();
                Bench();
                int level = Level;
                if (level != _applied)
                {
                    Withdraw();
                    _applied = level;
                    if (level > 0) _scanAt = Time.unscaledTime;
                    RevivalPlugin.L.LogInfo("ViewDistance: level " + LevelNames[level]
                        + (_benchStage >= 0 ? " (bench)" : "") + ".");
                }
                if (level <= 0) return;
                Profile p = Profiles[level];

                Camera cam = CameraOwner.ViewCamera();
                if (!ReferenceEquals(cam, _cam)) SwitchCamera(cam);
                if (!_baselineLogged && cam != null) LogBaseline(cam);

                if (!FlightView.Active)
                {
                    ApplyCamera(p);
                    ApplyFog();
                    ApplyTerrains(p);
                    Settle(_lod, QualitySettings.lodBias);
                    if (Put(_lod, p.LodBias)) QualitySettings.lodBias = p.LodBias;
                }
                Props(p);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ViewDistance: " + ex.Message + " - the level is withdrawn.");
                try { Withdraw(); } catch { }
                _benchStage = -1;
                _failed = true;
            }
        }

        static void SwitchCamera(Camera cam)
        {
            // The old camera gets its own values back, the new one is met fresh.
            RestoreCamera();
            _cam = cam;
            if (_small != null) _small.Target(cam);
            if (_medium != null) _medium.Target(cam);
        }

        static void ApplyCamera(Profile p)
        {
            if (_cam == null) return;
            Settle(_far, _cam.farClipPlane);
            float far = Mathf.Max(_far.Base, p.Far);
            if (Put(_far, far)) _cam.farClipPlane = far;

            if (Time.unscaledTime < _nextCullCheck) return;
            _nextCullCheck = Time.unscaledTime + 1f;
            float[] now = _cam.layerCullDistances;      // a fresh copy per get
            if (!_cullSet)
            {
                _cullBase = now;
                _cullSet = true;
            }
            float[] want = _want;                       // P1b: reused, the setter copies it
            for (int i = 0; i < 32; i++) want[i] = 0f;
            Fill(want, ItemLayers, p.Items);
            Fill(want, CampLayers, p.Camp);
            Fill(want, BushLayers, p.Bush);
            Fill(want, RagdollLayers, p.Ragdoll);
            bool same = now != null && now.Length == 32;
            for (int i = 0; i < 32; i++)
            {
                // A layer the game culls closer keeps the game's distance.
                if (_cullBase != null && _cullBase.Length == 32 && _cullBase[i] > 0f
                    && (want[i] <= 0f || _cullBase[i] < want[i])) want[i] = _cullBase[i];
                if (same && Mathf.Abs(now[i] - want[i]) > 0.01f) same = false;
            }
            if (!same)
            {
                _cam.layerCullDistances = want;
                _cam.layerCullSpherical = true;
            }
        }

        static readonly float[] _want = new float[32];

        static void Fill(float[] into, int[] layers, float d)
        {
            for (int i = 0; i < layers.Length; i++) into[layers[i]] = d;
        }

        static void ApplyFog()
        {
            if (!RenderSettings.fog || RenderSettings.fogMode != FogMode.Linear || _cam == null) return;
            Settle(_fogEnd, RenderSettings.fogEndDistance);
            Settle(_fogStart, RenderSettings.fogStartDistance);
            float end = Mathf.Max(_fogEnd.Base, Mathf.Min(_cam.farClipPlane * FogOverFar, HazeEndU));
            float k = _fogEnd.Base > 1f ? end / _fogEnd.Base : 1f;
            float start = _fogStart.Base * k;
            if (Put(_fogEnd, end)) RenderSettings.fogEndDistance = end;
            if (Put(_fogStart, start)) RenderSettings.fogStartDistance = start;
        }

        static void ApplyTerrains(Profile p)
        {
            if (Time.unscaledTime >= _nextTerrainScan)
            {
                _nextTerrainScan = Time.unscaledTime + 1f;
                _terrainList.Clear();
                Terrain[] all = Terrain.activeTerrains;
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null) _terrainList.Add(all[i]);
                if (_terrainList.Count != _terrainCount)
                {
                    // The east tile came or went: its props want sorting too.
                    if (_terrainCount >= 0) _scanAt = Time.unscaledTime + 5f;
                    _terrainCount = _terrainList.Count;
                }
            }
            for (int i = 0; i < _terrainList.Count; i++)
            {
                Terrain t = _terrainList[i];
                if (t == null) continue;
                TerrainSlots s;
                if (!_terrains.TryGetValue(t, out s)) { s = new TerrainSlots(); _terrains[t] = s; }
                Settle(s.Trees, t.treeDistance);
                if (Put(s.Trees, p.Trees)) t.treeDistance = p.Trees;
                Settle(s.Pixel, t.heightmapPixelError);
                if (Put(s.Pixel, p.Pixel)) t.heightmapPixelError = p.Pixel;
            }
        }

        // ========================================================== restore

        /// <summary>Every value back to what the game had, if it still holds
        /// ours; props on again, LODGroups as they were.</summary>
        static void Withdraw()
        {
            RestoreCamera();
            if (Mine(_lod, QualitySettings.lodBias)) QualitySettings.lodBias = _lod.Base;
            if (Mine(_fogEnd, RenderSettings.fogEndDistance)) RenderSettings.fogEndDistance = _fogEnd.Base;
            if (Mine(_fogStart, RenderSettings.fogStartDistance)) RenderSettings.fogStartDistance = _fogStart.Base;
            _lod.Has = _fogEnd.Has = _fogStart.Has = false;
            foreach (KeyValuePair<Terrain, TerrainSlots> e in _terrains)
            {
                Terrain t = e.Key;
                if (t == null) continue;
                if (Mine(e.Value.Trees, t.treeDistance)) t.treeDistance = e.Value.Trees.Base;
                if (Mine(e.Value.Pixel, t.heightmapPixelError)) t.heightmapPixelError = e.Value.Pixel.Base;
            }
            _terrains.Clear();
            CancelCollect();
            ClearProps();
            RestoreLods();
        }

        static void RestoreCamera()
        {
            if (_cam != null)
            {
                if (Mine(_far, _cam.farClipPlane)) _cam.farClipPlane = _far.Base;
                if (_cullSet)
                {
                    _cam.layerCullDistances = _cullBase != null && _cullBase.Length == 32
                        ? _cullBase : new float[32];
                }
            }
            _far.Has = false;
            _cullSet = false;
            _cullBase = null;
            _nextCullCheck = 0f;
            _cam = null;
            _baselineLogged = false;
        }

        // ============================================================ props

        // n01 perf (6.59.0 F6 at the airfield: LateTick 2.8 ms EVERY frame).
        // The east content scenes load one by one during play and every load
        // restarted the whole-scene sort (FindObjectsOfType over ~40,000
        // MeshRenderers, three GetComponentInParent walks each) at 2 ms a
        // frame; the finished sort then built both bands, primed ~30,000
        // renderers and restored/re-extended every large LODGroup inside ONE
        // frame each. Now:
        //   - the sort walks the loaded scenes' hierarchies top-down: a
        //     LODGroup, Rigidbody, Animator, kept object or the camera's rig
        //     skips its whole subtree (the same filter as the three parent
        //     walks, evaluated once per node instead of once per renderer);
        //   - every phase (walk, prime, orphans, LODs) runs in SliceMs slices;
        //   - a scan asked for while one runs waits for it instead of
        //     restarting it (loads every few seconds never let it finish);
        //   - the renderers this file switched off are one set (_hidden), so
        //     new bands take over from old ones without switching thousands
        //     of props on and off again. That also fixes the second pass (30 s
        //     after a load) dropping every prop the first pass had hidden -
        //     it skipped disabled renderers, which were exactly those.

        /// <summary>One CullingGroup over one size class: the engine tests the
        /// distance of each sphere to the camera, and a renderer is switched
        /// off past the band and on again inside it. Only renderers this file
        /// switched off are ever switched on.</summary>
        sealed class Band
        {
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<BoundingSphere> Pending = new List<BoundingSphere>();
            public CullingGroup Group;
            public BoundingSphere[] Spheres;
            /// <summary>-1: not primed yet; the renderers below it follow
            /// the group's events.</summary>
            public int PrimeIdx = -1;
            public float Distance;

            public void Add(Renderer r, Bounds b)
            {
                Renderers.Add(r);
                Pending.Add(new BoundingSphere(b.center, b.extents.magnitude));
            }

            public void Build(Camera cam, float distance)
            {
                Distance = distance;
                int n = Renderers.Count;
                Spheres = new BoundingSphere[Mathf.Max(1, n)];
                Pending.CopyTo(Spheres);
                Pending.Clear();
                Group = new CullingGroup();
                Group.SetBoundingSpheres(Spheres);
                Group.SetBoundingSphereCount(n);
                Group.SetBoundingDistances(new float[] { distance });
                Group.onStateChanged = Changed;
                Target(cam);
                PrimeIdx = -1;
            }

            public void Target(Camera cam)
            {
                if (Group == null) return;
                Group.targetCamera = cam;
                if (cam != null) Group.SetDistanceReferencePoint(cam.transform);
            }

            void Changed(CullingGroupEvent ev)
            {
                if (ev.index >= PrimeIdx || ev.index >= Renderers.Count) return;
                SetHidden(Renderers[ev.index], ev.currentDistance >= 1);
            }

            /// <summary>After the group has run once every sphere's band is
            /// known: apply them, a slice at a time. True when done.</summary>
            public bool PrimeStep(long t0, long budget)
            {
                if (Group == null) return true;
                if (PrimeIdx < 0) PrimeIdx = 0;
                int n = Renderers.Count;
                while (PrimeIdx < n)
                {
                    if ((PrimeIdx & 63) == 63 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) return false;
                    int i = PrimeIdx++;
                    SetHidden(Renderers[i], Group.GetDistance(i) >= 1);
                }
                return true;
            }

            /// <summary>The group goes; the renderers stay as they are (the
            /// caller shows them or hands them to new bands).</summary>
            public void Dispose()
            {
                if (Group != null) { Group.Dispose(); Group = null; }
                Renderers.Clear();
                Pending.Clear();
            }
        }

        static Band _small, _medium;
        static readonly HashSet<Renderer> _hidden = new HashSet<Renderer>();     // switched off by this file
        static readonly HashSet<Renderer> _released = new HashSet<Renderer>();   // KeepVisible since the sort
        static HashSet<Renderer> _members = new HashSet<Renderer>();              // in _small or _medium
        static int _propCount;
        static float _scanAt = -1f, _scanAgainAt = -1f;

        static void SetHidden(Renderer r, bool hide)
        {
            if (r == null) return;
            if (hide)
            {
                if (!_hidden.Contains(r) && r.enabled && !_released.Contains(r)) { r.enabled = false; _hidden.Add(r); }
            }
            else if (_hidden.Remove(r))
            {
                if (!r.enabled) r.enabled = true;
            }
        }

        static void ShowAll()
        {
            foreach (Renderer r in _hidden)
                if (r != null && !r.enabled) r.enabled = true;
            _hidden.Clear();
        }

        // The job after a scan: walk, then (next frame) prime, orphans, LODs.
        const int JobNone = 0, JobWalk = 1, JobPrime = 2, JobOrphans = 3, JobLods = 4;
        static int _job;
        static int _primeFrame = -1;

        static void Props(Profile p)
        {
            float now = Time.unscaledTime;
            bool scan = (_scanAt >= 0f && now >= _scanAt) || (_scanAgainAt >= 0f && now >= _scanAgainAt);
            // A scan asked for while one runs waits for it (see above).
            if (scan && _cam != null && _job == JobNone)
            {
                if (now >= _scanAt) _scanAt = -1f;
                if (now >= _scanAgainAt) _scanAgainAt = -1f;
                BeginCollect(p);
            }
            if (_job == JobNone) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = Budget();
            switch (_job)
            {
                case JobWalk: StepCollect(t0, budget); break;
                case JobPrime:
                    if (Time.frameCount <= _primeFrame) break;      // the groups have not run yet
                    if ((_small == null || _small.PrimeStep(t0, budget))
                        && (_medium == null || _medium.PrimeStep(t0, budget)))
                        BeginOrphans();
                    break;
                case JobOrphans: StepOrphans(t0, budget); break;
                case JobLods: StepLods(t0, budget); break;
            }
        }

        // P1b: 0.2 -> 0.15 ms, and the clock is read before every step (a
        // step is one node or one child push), not every 8th node: a node
        // with thousands of children (a map root) is expanded one child a
        // step through the (node, next child) cursor below, so no single
        // node overruns the slice. Same nodes, same depth-first order.
        const double SliceMs = 0.15;
        static readonly List<Transform> _wStack = new List<Transform>();
        static readonly List<int> _wNext = new List<int>();     // -1: visit the node; k: expand its children from k
        static Transform _cCamRoot;
        static int _cLarge, _cSkipped, _cFrames, _cNodes;
        static double _cMs;
        static Profile _cP;
        static Type _cAnimator;
        static Band _cSmall, _cMedium;
        static HashSet<Renderer> _cMembers = new HashSet<Renderer>();
        static readonly List<LODGroup> _cLods = new List<LODGroup>();

        static void BeginCollect(Profile p)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _cP = p;
            _cSmall = new Band();
            _cMedium = new Band();
            _cMembers.Clear();
            _cLods.Clear();
            _cLarge = _cSkipped = _cFrames = _cNodes = 0;
            _wStack.Clear();
            _wNext.Clear();
            // Pushed in reverse so they pop in scene order, roots in order.
            for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded) continue;
                GameObject[] roots = sc.GetRootGameObjects();
                for (int r = roots.Length - 1; r >= 0; r--) { _wStack.Add(roots[r].transform); _wNext.Add(-1); }
            }
            _cCamRoot = _cam != null ? _cam.transform.root : null;
            // Animator lives in UnityEngine.AnimationModule, which build.ps1
            // does not reference; by name, and skipped if it cannot be found.
            _cAnimator = Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
            _job = JobWalk;
            _cMs = Ms(t0);
        }

        static double Ms(long since)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }

        static long Budget()
        {
            return (long)(SliceMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        }

        /// <summary>This node and everything under it are left alone: the
        /// old GetComponentInParent tests (LODGroup, Rigidbody, Animator),
        /// KeepVisible and the camera's own rig.</summary>
        static bool SkipsSubtree(Transform t, GameObject go)
        {
            if (ReferenceEquals(t, _cCamRoot)) return true;
            LODGroup lg = t.GetComponent<LODGroup>();
            bool body = t.GetComponent<Rigidbody>() != null;
            // The LOD pass's list comes from the same walk (no second
            // FindObjectsOfType): the first LODGroup down a branch that no
            // Rigidbody carries.
            if (lg != null && !body) _cLods.Add(lg);
            return lg != null || body
                || (_cAnimator != null && t.GetComponent(_cAnimator) != null)
                || _keep.Contains(go.GetInstanceID());
        }

        static void StepCollect(long t0, long budget)
        {
            _cFrames++;
            while (_wStack.Count > 0)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) break;
                int last = _wStack.Count - 1;
                Transform t = _wStack[last];
                int next = _wNext[last];
                _wStack.RemoveAt(last);
                _wNext.RemoveAt(last);
                if (t == null) continue;                        // destroyed since it was queued
                if (next >= 0)
                {
                    // Child 'next' first, then the rest of t's children.
                    if (next >= t.childCount) continue;
                    if (next + 1 < t.childCount) { _wStack.Add(t); _wNext.Add(next + 1); }
                    _wStack.Add(t.GetChild(next));
                    _wNext.Add(-1);
                    continue;
                }
                GameObject go = t.gameObject;
                if (!go.activeSelf) continue;                   // FindObjectsOfType saw active objects only
                _cNodes++;
                if (SkipsSubtree(t, go)) { _cSkipped++; continue; }
                MeshRenderer r = t.GetComponent<MeshRenderer>();
                // One this file switched off is still a prop (enabled = false is ours).
                if (r != null && go.layer == 0 && (r.enabled || _hidden.Contains(r)))
                {
                    Bounds b = r.bounds;
                    float size = b.size.magnitude;
                    if (size < SmallSize) { _cSmall.Add(r, b); _cMembers.Add(r); }
                    else if (size < LargeSize) { _cMedium.Add(r, b); _cMembers.Add(r); }
                    else _cLarge++;
                }
                if (t.childCount > 0) { _wStack.Add(t); _wNext.Add(0); }
            }
            _cMs += Ms(t0);
            if (_wStack.Count > 0) return;

            // Done: the new bands replace the old ones in one step. Renderers
            // stay as they are; the prime and orphan passes correct them.
            if (_small != null) _small.Dispose();
            if (_medium != null) _medium.Dispose();
            _small = _cSmall;
            _medium = _cMedium;
            HashSet<Renderer> old = _members;
            _members = _cMembers;
            _cMembers = old;
            _cMembers.Clear();
            _propCount = _small.Renderers.Count + _medium.Renderers.Count + _cLarge;
            _small.Build(_cam, _cP.Small);
            _medium.Build(_cam, _cP.Medium);
            _primeFrame = Time.frameCount;
            _job = JobPrime;
            _cSmall = _cMedium = null;
            _cCamRoot = null;
            RevivalPlugin.L.LogInfo("ViewDistance: props sorted in "
                + _cMs.ToString("0", CultureInfo.InvariantCulture) + " ms over " + _cFrames + " frame(s), "
                + _cNodes + " nodes - " + _small.Renderers.Count + " small (off past " + _cP.Small + " u), "
                + _medium.Renderers.Count + " medium (off past " + _cP.Medium + " u), "
                + _cLarge + " large (to the far clip), " + _cSkipped
                + " subtree(s) left alone (LODGroup, moving, animated, kept).");
        }

        // A renderer this file switched off that is in no band any more
        // (destroyed, moved into a LODGroup, kept) is switched on again.
        static readonly List<Renderer> _orphans = new List<Renderer>();
        static int _oIdx;

        static void BeginOrphans()
        {
            _orphans.Clear();
            foreach (Renderer r in _hidden) _orphans.Add(r);
            _oIdx = 0;
            _job = JobOrphans;
        }

        static void StepOrphans(long t0, long budget)
        {
            while (_oIdx < _orphans.Count)
            {
                if ((_oIdx & 63) == 63 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) return;
                Renderer r = _orphans[_oIdx++];
                if (_members.Contains(r)) continue;
                _hidden.Remove(r);
                if (r != null && !r.enabled) r.enabled = true;
            }
            _orphans.Clear();
            BeginLods(_cP);
        }

        static void CancelCollect()
        {
            _job = JobNone;
            _wStack.Clear();
            _wNext.Clear();
            _orphans.Clear();
            _lAll = null;
            _cLods.Clear();
            _cSmall = _cMedium = null;
            _cMembers.Clear();
            _cCamRoot = null;
        }

        /// <summary>A kept object's renderers: on again if this file hid
        /// them, and never hidden again by the current bands.</summary>
        static void Release(Transform root)
        {
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                _released.Add(r);
                if (_hidden.Remove(r) && r != null && !r.enabled) r.enabled = true;
            }
        }

        static void ClearProps()
        {
            if (_small != null) { _small.Dispose(); _small = null; }
            if (_medium != null) { _medium.Dispose(); _medium = null; }
            ShowAll();
            _members.Clear();
            _released.Clear();
            _propCount = 0;
            _primeFrame = -1;
        }

        // ============================================================= lods

        static readonly Dictionary<LODGroup, float[]> _lodOriginal = new Dictionary<LODGroup, float[]>();

        static LODGroup[] _lAll;
        static int _lIdx, _lExt, _lFrames;
        static float _lFar, _lTan, _lBias;
        static double _lMs;

        /// <summary>Large LODGroups keep their coarsest LOD out to the far
        /// clip. A LOD level of screen height h culls at
        /// size * lodBias / (2 tan(fov/2) h); the last level's h is lowered
        /// until that is the far clip, never raised. Time-sliced like the
        /// prop sort (StepLods); a group extended before is measured against
        /// its original heights and only written when its value changes (no
        /// restore-everything frame before the pass any more).</summary>
        static void BeginLods(Profile p)
        {
            _lAll = null;
            _job = JobNone;
            if (_cam == null || p == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _lFar = Mathf.Min(Mathf.Max(p.Far, _cam.farClipPlane), HazeDetailDistance());
            _lTan = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _lBias = p.LodBias;
            _lIdx = _lExt = _lFrames = 0;
            _lAll = _cLods.ToArray();
            _cLods.Clear();
            _job = JobLods;
            _lMs = Ms(t0);
        }

        /// <summary>P2: where the linear haze reaches HazeDetailFog; no limit
        /// without linear fog.</summary>
        static float HazeDetailDistance()
        {
            if (!RenderSettings.fog || RenderSettings.fogMode != FogMode.Linear) return float.MaxValue;
            float a = RenderSettings.fogStartDistance, b = RenderSettings.fogEndDistance;
            return b > a ? a + HazeDetailFog * (b - a) : float.MaxValue;
        }

        static void StepLods(long t0, long budget)
        {
            _lFrames++;
            LODGroup[] all = _lAll;
            while (_lIdx < all.Length)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) break;   // P1b: every group, not every 32nd
                LODGroup g = all[_lIdx++];
                if (g == null || !g.enabled) continue;
                float[] orig;
                bool had = _lodOriginal.TryGetValue(g, out orig);
                Vector3 ls = g.transform.lossyScale;
                float size = g.size * Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                if (size < LargeSize || g.GetComponentInParent<Rigidbody>() != null)
                {
                    if (had) { Restore(g, orig); _lodOriginal.Remove(g); }
                    continue;
                }
                LOD[] lods = g.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                if (had && orig.Length != lods.Length) { _lodOriginal.Remove(g); had = false; }
                int last = lods.Length - 1;
                float baseH = had ? orig[last] : lods[last].screenRelativeTransitionHeight;
                float h = size * _lBias / (2f * _lTan * _lFar);
                h = Mathf.Max(h, 0.0001f);
                bool extend = lods[last].renderers != null && lods[last].renderers.Length > 0 && h < baseH;
                if (!extend)
                {
                    if (had) { Restore(g, orig); _lodOriginal.Remove(g); }
                    continue;
                }
                _lExt++;
                if (had && Mathf.Abs(lods[last].screenRelativeTransitionHeight - h) < 1e-6f) continue;   // already so
                if (!had)
                {
                    orig = new float[lods.Length];
                    for (int k = 0; k < lods.Length; k++) orig[k] = lods[k].screenRelativeTransitionHeight;
                    _lodOriginal[g] = orig;
                }
                else for (int k = 0; k < last; k++) lods[k].screenRelativeTransitionHeight = orig[k];
                lods[last].screenRelativeTransitionHeight = h;
                g.SetLODs(lods);
            }
            _lMs += Ms(t0);
            if (_lIdx < all.Length) return;
            RevivalPlugin.L.LogInfo("ViewDistance: " + _lExt + " of " + all.Length
                + " LODGroups (>= " + LargeSize + " u) keep their far LOD out to the haze, "
                + _lFar.ToString("0", CultureInfo.InvariantCulture) + " u ("
                + _lMs.ToString("0", CultureInfo.InvariantCulture) + " ms over " + _lFrames + " frame(s)).");
            _lAll = null;
            _job = JobNone;
        }

        static void Restore(LODGroup g, float[] orig)
        {
            LOD[] lods = g.GetLODs();
            if (lods == null || lods.Length != orig.Length) return;
            for (int k = 0; k < lods.Length; k++) lods[k].screenRelativeTransitionHeight = orig[k];
            g.SetLODs(lods);
        }

        static void RestoreLods()
        {
            foreach (KeyValuePair<LODGroup, float[]> e in _lodOriginal)
                if (e.Key != null) Restore(e.Key, e.Value);
            _lodOriginal.Clear();
        }

        // ======================================================== measuring

        static void LogBaseline(Camera cam)
        {
            _baselineLogged = true;
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder("ViewDistance baseline (the game's values): scene ");
            sb.Append(SceneManager.GetActiveScene().name)
              .Append(", far ").Append(cam.farClipPlane.ToString("0", ci))
              .Append(", lodBias ").Append(QualitySettings.lodBias.ToString("0.00", ci))
              .Append(", quality ").Append(QualitySettings.GetQualityLevel())
              .Append(", fog ").Append(RenderSettings.fog ? RenderSettings.fogMode.ToString() : "off")
              .Append(" ").Append(RenderSettings.fogStartDistance.ToString("0", ci))
              .Append("..").Append(RenderSettings.fogEndDistance.ToString("0", ci))
              .Append(" d ").Append(RenderSettings.fogDensity.ToString("0.00000", ci));
            float[] cull = cam.layerCullDistances;
            int set = 0;
            if (cull != null) for (int i = 0; i < cull.Length; i++) if (cull[i] > 0f) set++;
            sb.Append(", layer culls set ").Append(set);
            Terrain t = Terrain.activeTerrain;
            if (t != null)
                sb.Append(" | ").Append(t.name).Append(" pixelError ").Append(t.heightmapPixelError.ToString("0.0", ci))
                  .Append(", basemap ").Append(t.basemapDistance.ToString("0", ci))
                  .Append(", trees ").Append(t.treeDistance.ToString("0", ci))
                  .Append(", billboard ").Append(t.treeBillboardDistance.ToString("0", ci))
                  .Append(", detail ").Append(t.detailObjectDistance.ToString("0", ci));
            RevivalPlugin.L.LogInfo(sb.ToString());
        }

        static KeyCode _cycleKey = KeyCode.None, _benchKey = KeyCode.None;
        static bool _keysParsed;

        static KeyCode ParseKey(ConfigEntry<string> e)
        {
            if (e == null || string.IsNullOrEmpty(e.Value)) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), e.Value.Trim(), true); }
            catch { return KeyCode.None; }
        }

        static void Keys()
        {
            if (!_keysParsed)
            {
                _keysParsed = true;
                _cycleKey = ParseKey(_cfgCycleKey);
                _benchKey = ParseKey(_cfgBenchKey);
            }
            if (Admin.IsOpen) return;
            if (_cycleKey != KeyCode.None && Input.GetKeyDown(_cycleKey) && _benchStage < 0)
            {
                int next = (ConfiguredLevel() + 1) % LevelNames.Length;
                if (_cfgLevel != null) _cfgLevel.Value = LevelNames[next];
            }
            if (_benchKey != KeyCode.None && Input.GetKeyDown(_benchKey))
            {
                if (_benchStage >= 0)
                {
                    _benchStage = -1;
                    RevivalPlugin.L.LogInfo("ViewDistance bench: stopped.");
                }
                else StartBench();
            }
        }

        static readonly int[] BenchOrder = { 0, 1, 2, 3, 4 };
        static int _benchStage = -1;
        static float _stageStart;
        static readonly List<float> _frames = new List<float>();
        static readonly StringBuilder _benchSummary = new StringBuilder();

        static void StartBench()
        {
            _benchStage = 0;
            _stageStart = Time.unscaledTime;
            _frames.Clear();
            _benchSummary.Length = 0;
            RevivalPlugin.L.LogInfo("ViewDistance bench: started at "
                + (_cam != null ? _cam.transform.position.ToString("F0") : "?")
                + " - keep the camera still.");
        }

        static void Bench()
        {
            if (_benchStage < 0) return;
            float settle = Mathf.Max(0.5f, _cfgSettle == null ? 3f : _cfgSettle.Value);
            float sample = Mathf.Max(1f, _cfgSample == null ? 6f : _cfgSample.Value);
            float t = Time.unscaledTime - _stageStart;
            if (t >= settle) _frames.Add(Time.unscaledDeltaTime);
            if (t < settle + sample) return;

            CultureInfo ci = CultureInfo.InvariantCulture;
            string name = LevelNames[BenchOrder[_benchStage]];
            if (_frames.Count > 1)
            {
                float sum = 0f;
                for (int i = 0; i < _frames.Count; i++) sum += _frames[i];
                _frames.Sort();
                float p99 = _frames[Mathf.Min(_frames.Count - 1, (int)(_frames.Count * 0.99f))];
                float avg = sum / _frames.Count;
                string line = name + " avg " + (avg * 1000f).ToString("0.0", ci) + " ms ("
                    + (1f / Mathf.Max(0.0001f, avg)).ToString("0", ci) + " fps), p99 "
                    + (p99 * 1000f).ToString("0.0", ci) + " ms";
                RevivalPlugin.L.LogInfo("ViewDistance bench: " + line + ", far "
                    + (_cam != null ? _cam.farClipPlane.ToString("0", ci) : "?") + ", frames " + _frames.Count
                    + ", props " + (_small != null ? _small.Renderers.Count : 0) + "/"
                    + (_medium != null ? _medium.Renderers.Count : 0) + ".");
                if (_benchSummary.Length > 0) _benchSummary.Append(" | ");
                _benchSummary.Append(line);
            }
            _frames.Clear();
            _benchStage++;
            _stageStart = Time.unscaledTime;
            if (_benchStage >= BenchOrder.Length)
            {
                _benchStage = -1;
                RevivalPlugin.L.LogInfo("ViewDistance bench summary: " + _benchSummary.ToString());
            }
        }
    }
}

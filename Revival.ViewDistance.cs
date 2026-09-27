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
            if (_props.Count > 0) Release(go.transform);
        }

        static bool Kept(Transform t)
        {
            for (; t != null; t = t.parent)
                if (_keep.Contains(t.gameObject.GetInstanceID())) return true;
            return false;
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
            float[] want = new float[32];
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

        static void Fill(float[] into, int[] layers, float d)
        {
            for (int i = 0; i < layers.Length; i++) into[layers[i]] = d;
        }

        static void ApplyFog()
        {
            if (!RenderSettings.fog || RenderSettings.fogMode != FogMode.Linear || _cam == null) return;
            Settle(_fogEnd, RenderSettings.fogEndDistance);
            Settle(_fogStart, RenderSettings.fogStartDistance);
            float end = Mathf.Max(_fogEnd.Base, _cam.farClipPlane * FogOverFar);
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

        /// <summary>One CullingGroup over one size class: the engine tests the
        /// distance of each sphere to the camera, and a renderer is switched
        /// off past the band and on again inside it. Only renderers this file
        /// switched off are ever switched on.</summary>
        sealed class Band
        {
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public bool[] Hidden;
            public CullingGroup Group;
            public BoundingSphere[] Spheres;
            public bool Primed;
            public float Distance;

            public void Build(Camera cam, float distance)
            {
                Distance = distance;
                int n = Renderers.Count;
                Hidden = new bool[n];
                Spheres = new BoundingSphere[Mathf.Max(1, n)];
                for (int i = 0; i < n; i++)
                {
                    Bounds b = Renderers[i].bounds;
                    Spheres[i] = new BoundingSphere(b.center, b.extents.magnitude);
                }
                Group = new CullingGroup();
                Group.SetBoundingSpheres(Spheres);
                Group.SetBoundingSphereCount(n);
                Group.SetBoundingDistances(new float[] { distance });
                Group.onStateChanged = Changed;
                Target(cam);
                Primed = false;
            }

            public void Target(Camera cam)
            {
                if (Group == null) return;
                Group.targetCamera = cam;
                if (cam != null) Group.SetDistanceReferencePoint(cam.transform);
            }

            void Changed(CullingGroupEvent ev)
            {
                if (!Primed) return;
                Set(ev.index, ev.currentDistance >= 1);
            }

            /// <summary>The first frame after the group has run once: every
            /// sphere's band is known, apply them all.</summary>
            public void Prime()
            {
                if (Primed || Group == null) return;
                Primed = true;
                for (int i = 0; i < Renderers.Count; i++) Set(i, Group.GetDistance(i) >= 1);
            }

            void Set(int i, bool hide)
            {
                if (i < 0 || i >= Renderers.Count) return;
                Renderer r = Renderers[i];
                if (r == null) { Hidden[i] = false; return; }
                if (hide)
                {
                    if (!Hidden[i] && r.enabled) { r.enabled = false; Hidden[i] = true; }
                }
                else if (Hidden[i])
                {
                    if (!r.enabled) r.enabled = true;
                    Hidden[i] = false;
                }
            }

            public void Release(Transform root)
            {
                for (int i = 0; i < Renderers.Count; i++)
                {
                    Renderer r = Renderers[i];
                    if (r == null || !r.transform.IsChildOf(root)) continue;
                    if (Hidden[i]) r.enabled = true;
                    Hidden[i] = false;
                    Renderers[i] = null;
                }
            }

            public void Dispose()
            {
                if (Group != null) { Group.Dispose(); Group = null; }
                if (Hidden != null)
                    for (int i = 0; i < Renderers.Count; i++)
                        if (Hidden[i] && Renderers[i] != null) Renderers[i].enabled = true;
                Renderers.Clear();
                Hidden = null;
            }
        }

        static Band _small, _medium;
        static readonly List<Renderer> _props = new List<Renderer>();
        static float _scanAt = -1f, _scanAgainAt = -1f;
        static int _primeFrame = -1;

        static void Props(Profile p)
        {
            float now = Time.unscaledTime;
            bool scan = (_scanAt >= 0f && now >= _scanAt) || (_scanAgainAt >= 0f && now >= _scanAgainAt);
            if (scan && _cam != null)
            {
                if (now >= _scanAt) _scanAt = -1f;
                if (now >= _scanAgainAt) _scanAgainAt = -1f;
                BeginCollect(p);
            }
            if (_cAll != null) StepCollect();
            else if (_lAll != null) StepLods();
            if (_primeFrame >= 0 && Time.frameCount > _primeFrame)
            {
                if (_small != null) _small.Prime();
                if (_medium != null) _medium.Prime();
                _primeFrame = -1;
            }
        }

        // Q1 perf: the sort is TIME-SLICED. It walks every MeshRenderer of
        // the scene (40,777 in Kevin's 6.57.0 session, three
        // GetComponentInParent each) and then every LODGroup (9,464), and ran
        // 8 s and 30 s after every scene load - the east content scenes load
        // during play, so these were 35 ms + LOD frames mid-game. Now each
        // frame does at most SliceMs of it; the finished bands replace the old
        // ones in one step. Same filters, same bands, same log lines.
        const double SliceMs = 2.0;
        static MeshRenderer[] _cAll;
        static int _cIdx, _cLarge, _cSkipped, _cFrames;
        static double _cMs;
        static Profile _cP;
        static Type _cAnimator;
        static Band _cSmall, _cMedium;
        static readonly List<Renderer> _cProps = new List<Renderer>();

        static void BeginCollect(Profile p)
        {
            _lAll = null;
            _cP = p;
            _cSmall = new Band();
            _cMedium = new Band();
            _cProps.Clear();
            _cLarge = _cSkipped = _cFrames = _cIdx = 0;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _cAll = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            // Animator lives in UnityEngine.AnimationModule, which build.ps1
            // does not reference; by name, and skipped if it cannot be found.
            _cAnimator = Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
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

        static void StepCollect()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = Budget();
            _cFrames++;
            MeshRenderer[] all = _cAll;
            Type animator = _cAnimator;
            while (_cIdx < all.Length)
            {
                if ((_cIdx & 31) == 31 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) break;
                MeshRenderer r = all[_cIdx++];
                if (r == null || !r.enabled || r.gameObject.layer != 0) continue;
                Transform t = r.transform;
                if (r.GetComponentInParent<LODGroup>() != null
                    || r.GetComponentInParent<Rigidbody>() != null
                    || (animator != null && r.GetComponentInParent(animator) != null)
                    || Kept(t)
                    || (_cam != null && t.IsChildOf(_cam.transform.root)))
                { _cSkipped++; continue; }
                float size = r.bounds.size.magnitude;
                if (size < SmallSize) _cSmall.Renderers.Add(r);
                else if (size < LargeSize) _cMedium.Renderers.Add(r);
                else _cLarge++;
                _cProps.Add(r);
            }
            _cMs += Ms(t0);
            if (_cIdx < all.Length) return;

            // Done: anything destroyed or kept visible since it was looked at
            // leaves the bands, then the new bands replace the old ones.
            Prune(_cSmall.Renderers);
            Prune(_cMedium.Renderers);
            Prune(_cProps);
            ClearProps();
            _small = _cSmall;
            _medium = _cMedium;
            _props.AddRange(_cProps);
            _small.Build(_cam, _cP.Small);
            _medium.Build(_cam, _cP.Medium);
            _primeFrame = Time.frameCount;
            _cAll = null;
            _cSmall = _cMedium = null;
            _cProps.Clear();
            RevivalPlugin.L.LogInfo("ViewDistance: props sorted in "
                + _cMs.ToString("0", CultureInfo.InvariantCulture) + " ms over " + _cFrames + " frame(s) - "
                + _small.Renderers.Count + " small (off past " + _cP.Small + " u), "
                + _medium.Renderers.Count + " medium (off past " + _cP.Medium + " u), "
                + _cLarge + " large (to the far clip), " + _cSkipped
                + " left alone (LODGroup, moving, animated, kept).");
            BeginLods(_cP);
        }

        static void Prune(List<Renderer> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null || Kept(list[i].transform)) list.RemoveAt(i);
        }

        static void CancelCollect()
        {
            _cAll = null;
            _lAll = null;
            _cSmall = _cMedium = null;
            _cProps.Clear();
        }

        static void Release(Transform root)
        {
            if (_small != null) _small.Release(root);
            if (_medium != null) _medium.Release(root);
        }

        static void ClearProps()
        {
            if (_small != null) { _small.Dispose(); _small = null; }
            if (_medium != null) { _medium.Dispose(); _medium = null; }
            _props.Clear();
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
        /// prop sort (StepLods).</summary>
        static void BeginLods(Profile p)
        {
            RestoreLods();
            _lAll = null;
            if (_cam == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _lFar = Mathf.Max(p.Far, _cam.farClipPlane);
            _lTan = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _lBias = p.LodBias;
            _lIdx = _lExt = _lFrames = 0;
            _lAll = UnityEngine.Object.FindObjectsOfType<LODGroup>();
            _lMs = Ms(t0);
        }

        static void StepLods()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = Budget();
            _lFrames++;
            LODGroup[] all = _lAll;
            while (_lIdx < all.Length)
            {
                if ((_lIdx & 31) == 31 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budget) break;
                LODGroup g = all[_lIdx++];
                if (g == null || !g.enabled || g.GetComponentInParent<Rigidbody>() != null) continue;
                Vector3 ls = g.transform.lossyScale;
                float size = g.size * Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                if (size < LargeSize) continue;
                LOD[] lods = g.GetLODs();
                if (lods == null || lods.Length == 0) continue;
                int last = lods.Length - 1;
                if (lods[last].renderers == null || lods[last].renderers.Length == 0) continue;
                float h = size * _lBias / (2f * _lTan * _lFar);
                h = Mathf.Max(h, 0.0001f);
                if (h >= lods[last].screenRelativeTransitionHeight) continue;
                float[] orig = new float[lods.Length];
                for (int k = 0; k < lods.Length; k++) orig[k] = lods[k].screenRelativeTransitionHeight;
                _lodOriginal[g] = orig;
                lods[last].screenRelativeTransitionHeight = h;
                g.SetLODs(lods);
                _lExt++;
            }
            _lMs += Ms(t0);
            if (_lIdx < all.Length) return;
            RevivalPlugin.L.LogInfo("ViewDistance: " + _lExt + " of " + all.Length
                + " LODGroups (>= " + LargeSize + " u) keep their far LOD out to "
                + _lFar.ToString("0", CultureInfo.InvariantCulture) + " u ("
                + _lMs.ToString("0", CultureInfo.InvariantCulture) + " ms over " + _lFrames + " frame(s)).");
            _lAll = null;
        }

        static void RestoreLods()
        {
            foreach (KeyValuePair<LODGroup, float[]> e in _lodOriginal)
            {
                LODGroup g = e.Key;
                if (g == null) continue;
                LOD[] lods = g.GetLODs();
                if (lods == null || lods.Length != e.Value.Length) continue;
                for (int k = 0; k < lods.Length; k++) lods[k].screenRelativeTransitionHeight = e.Value[k];
                g.SetLODs(lods);
            }
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

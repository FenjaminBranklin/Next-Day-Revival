// Next Day: Survival - Revival Toolkit
// Shared gunner/recon sensor overlay. Unity draws actual warm renderers into
// a transparent mask; a narrow contour follows their real silhouette. Skinning,
// submeshes and unreadable GPU meshes stay in Unity's coordinate system.
// No CPU snapshots, projected triangles, bounds boxes or oval substitutes.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class GunnerOptics
    {
        static Texture2D _px;
        static Texture2D _disc;    // soft round glow, used for the outer bloom
        static Texture2D _hot;     // flat-topped hot disc: solid to the rim, soft edge
        static Texture2D _vig;    // radial vignette: clear centre, dark rim
        static Texture2D _grain;  // static sensor noise, tiled

        // Warm-target cache. FindObjectsOfType per frame is exactly the cost
        // that never shows in a log and always shows in the frame time, so the
        // list is rebuilt a few times a second and only projected each frame.
        static readonly List<Transform> _warm = new List<Transform>();   // people: crew + players
        static readonly List<Transform> _veh = new List<Transform>();     // vehicles
        static float _warmUntil;
        static Camera _collectionCamera;
        static Vector3 _collectionEye;
        static Type _npcType, _playerType, _vehType, _explType;
        static MethodInfo _npcAlive;   // NPC_AI2.IsAlive() - dead crew do not radiate
        static bool _typesResolved;

        // Explosions radiate a short, very hot flare. ExplosionObject instances are
        // sampled at the refresh tick (they live a second or two); each new one
        // seeds a Flash that is projected and drawn every frame until it decays, so
        // the flare animates smoothly and reads as the hottest thing in the field.
        struct Flash { public Vector3 pos; public float born; public float dur; }
        static readonly List<Flash> _flash = new List<Flash>();
        static readonly Dictionary<int, float> _boomSeen = new Dictionary<int, float>();
        static readonly List<int> _boomPurge = new List<int>();
        static float _nextBoom;   // next explosion sample (see ScanExplosions)

        const float VehRange = 900f;
        const float PplRange = 600f;
        const float PplNear = 2.5f;
        const int MaskMaxSide = 1280;
        const int MaskDrawLimit = 512;
        const int MaskBuildLimit = 2;
        static readonly string[] MaskFogKeywords = { "FOG_LINEAR", "FOG_EXP", "FOG_EXP2" };
        static RenderTexture _mask;
        static Material _maskMaterial;
        static CommandBuffer _maskCommands;
        static bool _maskUnavailable;
        static int _maskFrame = -1;
        static Camera _maskCamera;
        static int _maskBuilds;
        static readonly Plane[] _maskPlanes = new Plane[6];
        static readonly Dictionary<int, MaskEntry> _maskEntries = new Dictionary<int, MaskEntry>();
        static readonly List<int> _maskPurge = new List<int>();
        static float _maskPurgeAt;
        static readonly List<MaskPart> _maskVisible = new List<MaskPart>();
        sealed class MaskPart
        {
            public Renderer renderer;
            public Mesh mesh;
            public MeshFilter filter;
            public SkinnedMeshRenderer skin;
            public bool managed;
        }
        sealed class MaskEntry
        {
            public Transform root;
            public float built, used;
            public int level;
            public readonly List<MaskPart> parts = new List<MaskPart>();
        }

        static Texture2D Px()
        {
            if (_px == null)
            {
                _px = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
                _px.hideFlags = HideFlags.HideAndDontSave;
            }
            return _px;
        }

        static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Px());
            GUI.color = old;
        }

        /// <summary>
        /// A soft round glow texture, built once: opaque at the centre, fading to
        /// zero alpha at the rim. Warm targets are drawn with this, so they read
        /// as round heat blobs instead of the old concentric squares.
        /// </summary>
        static Texture2D Disc()
        {
            if (_disc != null) return _disc;
            const int N = 64;
            _disc = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _disc.hideFlags = HideFlags.HideAndDontSave;
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);      // 0 centre .. 1 rim
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;                                    // softer falloff
                    _disc.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _disc.Apply();
            return _disc;
        }

        static void Glow(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Disc());
            GUI.color = old;
        }

        /// <summary>
        /// A FLAT-TOPPED round heat disc: full alpha out to ~0.8 of the radius,
        /// then a short soft ramp to zero at the rim. Drawing a warm target with
        /// this fills the whole target shape with one solid, uniform colour - the
        /// target reads as completely lit, not as a bright point with a dim halo
        /// (the old soft disc concentrated brightness in the centre).
        /// </summary>
        static Texture2D HotDisc()
        {
            if (_hot != null) return _hot;
            const int N = 64;
            _hot = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _hot.hideFlags = HideFlags.HideAndDontSave;
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);      // 0 centre .. 1 rim
                    float a = d <= 0.80f ? 1f : Mathf.Clamp01((1f - d) / 0.20f);
                    _hot.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _hot.Apply();
            return _hot;
        }

        static void HotGlow(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, HotDisc());
            GUI.color = old;
        }

        /// <summary>Radial vignette texture, built once: fully transparent at the
        /// centre, opaque toward the corners. Stretched over the whole screen it
        /// darkens the optic edges the way a real periscope/thermal tube does.</summary>
        static Texture2D Vig()
        {
            if (_vig != null) return _vig;
            const int N = 64;
            _vig = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _vig.hideFlags = HideFlags.HideAndDontSave;
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);      // 0 centre .. ~1.41 corner
                    float a = Mathf.Clamp01((d - 0.55f) / 0.85f); // clear until 0.55, then ramp
                    a = a * a;                                    // ease in
                    _vig.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _vig.Apply();
            return _vig;
        }

        /// <summary>Fine static noise, built once, tiled across the picture at low
        /// alpha so the cold field reads as a live detector rather than flat paint.</summary>
        static Texture2D Grain()
        {
            if (_grain != null) return _grain;
            const int N = 128;
            _grain = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _grain.hideFlags = HideFlags.HideAndDontSave;
            _grain.wrapMode = TextureWrapMode.Repeat;
            System.Random rnd = new System.Random(1337);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float v = (float)rnd.NextDouble();
                    _grain.SetPixel(x, y, new Color(v, v, v, 1f));
                }
            _grain.Apply();
            return _grain;
        }

        static void Vignette(Rect full, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(full, Vig());
            GUI.color = old;
        }

        static void ThermalGrain(Rect full, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            // Tile the 128px noise; the tile count is screen/128 so pixels stay 1:1.
            Rect uv = new Rect(0f, 0f, full.width / 128f, full.height / 128f);
            GUI.DrawTextureWithTexCoords(full, Grain(), uv);
            GUI.color = old;
        }

        /// <summary>
        /// Draws the whole gunner optic. Returns true when it has taken over the
        /// view, so Turret skips its legacy scope. Called from Turret.DrawScope
        /// only while the player is manning a seat.
        /// </summary>
        internal static bool Draw(bool tank, Transform veh)
        {
            if (!VehicleModules.Enabled) return false;
            if (VehicleModules.CfgPeriscope == null || !VehicleModules.CfgPeriscope.Value) return false;

            // The optic is purely visual: fixed-Rect GUI draws, no layout and no
            // hit-testing. OnGUI runs once per input event as well as on Repaint,
            // so while the gunner AIMS - a flood of mouse-move/drag events - this
            // whole method, including the per-target projection loops in DrawVision
            // (a WorldToScreenPoint over every NPC, vehicle and player), would run
            // many times per frame for zero visible gain. That is the thermal
            // "lag while aiming": the paint is cheap, the repeated CPU sweep is
            // not. Do the work only on the Repaint pass - the one event that
            // actually paints - exactly as Patrol.DrawMap already does. Still
            // return true on every event so Turret keeps skipping its legacy scope.
            if (Event.current == null || Event.current.type != EventType.Repaint) return true;

            VisionMode mode = VehicleModules.CurrentMode(veh);
            try
            {
                DrawVision(mode, Camera.main);
                DrawFrame(mode);
                DrawReticle(mode);
                DrawStatus(veh, mode);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("GunnerOptics: " + ex); }
            return true;
        }

        /// <summary>
        /// The same cold field and warm silhouettes for a viewer that is NOT the
        /// gunner periscope - the recon drone looking through its own camera. Draws
        /// only the thermal/night picture; the caller keeps its own HUD on top and
        /// supplies the camera it actually renders with. Repaint-only for the same
        /// reason Draw() is: OnGUI runs once per input event, and the projection
        /// sweep must not run on a mouse move.
        /// </summary>
        internal static void DrawExternal(VisionMode mode, Camera cam)
        {
            if (mode == VisionMode.Normal || cam == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try { DrawVision(mode, cam); }
            catch (Exception ex) { RevivalPlugin.L.LogError("GunnerOptics external: " + ex); }
        }

        // ------------------------------------------------------ scene tint

        static void DrawVision(VisionMode mode, Camera cam)
        {
            if (mode == VisionMode.Normal) return;

            Rect full = new Rect(0f, 0f, Screen.width, Screen.height);
            bool thermal = mode == VisionMode.Thermal;

            if (thermal)
            {
                // Crush the picture to a very dark, deep-BLUE, low-contrast field.
                // Even darker and bluer than before, and almost fully opaque, so
                // the cold scene keeps almost no detail and the yellow targets
                // slam against it - maximum thermal contrast.
                Fill(full, new Color(0.005f, 0.02f, 0.16f, 0.95f));   // very dark deep blue
                Vignette(full, new Color(0.00f, 0.00f, 0.02f, 1f));   // edges go to black
                ThermalGrain(full, new Color(0.30f, 0.45f, 0.90f, 0.05f)); // faint blue detector noise
            }
            else
            {
                // Night: a softer green light-gain wash (unchanged intent).
                Fill(full, new Color(0.00f, 0.22f, 0.03f, 0.42f));
                Vignette(full, new Color(0.00f, 0.02f, 0.00f, 0.95f));
                ThermalGrain(full, new Color(0.60f, 0.90f, 0.60f, 0.04f));
            }

            if (cam == null) return;
            RefreshTargets(cam);

            // The actual model mask supplies both body and contour.
            DrawSilhouettes(cam, thermal);

            // Explosions radiate: draw the short-lived heat flares on top, in the
            // mode's palette. Living AI (crew AND hostiles) are already in the
            // warm set above via NPC_AI2, so enemies glow like everything else.
            // Sample explosions on their OWN fast tick (ScanExplosions, ten times a
            // second) rather than at the 0.35 s target-refresh tick: a tank-shot
            // blast is caught while it still burns instead of being missed between
            // ticks. The flares themselves are projected and drawn every frame, so
            // the flare animates smoothly whatever the sampling rate is.
            ScanExplosions();
            DrawFlashes(cam, thermal);
        }

        // ------------------------------------------------ thermal silhouettes

        static bool EnsureMask()
        {
            if (_maskUnavailable) return false;
            if (_maskMaterial == null)
            {
                // Confirmed in resources.assets (Shader 7276). White input
                // yields an opaque mask without model textures/vertex colors.
                Shader shader = Shader.Find("Unlit/Transparent Cutout");
                if (shader == null || !shader.isSupported)
                {
                    _maskUnavailable = true;
                    RevivalPlugin.L.LogWarning("Thermal mask: unlit shader unavailable; heat overlay disabled.");
                    return false;
                }
                _maskMaterial = new Material(shader);
                _maskMaterial.hideFlags = HideFlags.HideAndDontSave;
                _maskMaterial.SetTexture("_MainTex", Px());
                _maskMaterial.SetFloat("_Cutoff", 0f);
                _maskCommands = new CommandBuffer();
                _maskCommands.name = "NDR thermal silhouettes";
            }
            int width, height;
            MaskSize(Screen.width, Screen.height, out width, out height);
            if (_mask != null && _mask.width == width && _mask.height == height && _mask.IsCreated()) return true;
            if (_mask != null) { _mask.Release(); UnityEngine.Object.Destroy(_mask); }
            _mask = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            _mask.hideFlags = HideFlags.HideAndDontSave;
            _mask.filterMode = FilterMode.Bilinear;
            _mask.wrapMode = TextureWrapMode.Clamp;
            _mask.useMipMap = false;
            _mask.antiAliasing = 1;
            _maskFrame = -1;
            return _mask.Create();
        }

        static void MaskSize(int width, int height, out int w, out int h)
        {
            float scale = Mathf.Min(1f, (float)MaskMaxSide / Mathf.Max(1, Mathf.Max(width, height)));
            w = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            h = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        }

        static void DrawSilhouettes(Camera cam, bool thermal)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || !EnsureMask()) return;
            if (_maskFrame != Time.frameCount || _maskCamera != cam)
            {
                PurgeMaskEntries();
                _maskBuilds = 0;
                GeometryUtility.CalculateFrustumPlanes(cam, _maskPlanes);
                _maskCommands.Clear();
                _maskCommands.SetRenderTarget(_mask);
                _maskCommands.SetViewport(new Rect(0, 0, _mask.width, _mask.height));
                _maskCommands.ClearRenderTarget(true, true, Color.clear);
                _maskCommands.SetViewProjectionMatrices(cam.worldToCameraMatrix,
                    GL.GetGPUProjectionMatrix(cam.projectionMatrix, true));
                // Fog variants use GLOBAL keywords. Disabling the material's
                // keywords alone does not keep the white mask free of scene fog.
                for (int i = 0; i < MaskFogKeywords.Length; i++)
                    _maskCommands.DisableShaderKeyword(MaskFogKeywords[i]);
                int draws = QueueMaskTargets(cam, _warm, true, 0);
                QueueMaskTargets(cam, _veh, false, draws);
                for (int i = 0; i < MaskFogKeywords.Length; i++)
                    if (Shader.IsKeywordEnabled(MaskFogKeywords[i]))
                        _maskCommands.EnableShaderKeyword(MaskFogKeywords[i]);
                RenderTexture previous = RenderTexture.active;
                GL.PushMatrix();
                try { Graphics.ExecuteCommandBuffer(_maskCommands); }
                finally
                {
                    RenderTexture.active = previous;
                    GL.PopMatrix();
                    GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
                }
                _maskFrame = Time.frameCount;
                _maskCamera = cam;
            }
            // Shift the alpha silhouette by one sensor pixel, then cover its
            // interior with the unshifted body. Only the real contour remains.
            Color old = GUI.color;
            try
            {
                GUI.color = thermal ? new Color(1f, 0.90f, 0.18f, 1f) : new Color(0.75f, 1f, 0.55f, 1f);
                float edge = Mathf.Clamp((float)Screen.width / _mask.width, 1f, 2f);
                Rect full = new Rect(0, 0, Screen.width, Screen.height);
                GUI.DrawTexture(new Rect(-edge, 0, full.width, full.height), _mask);
                GUI.DrawTexture(new Rect(edge, 0, full.width, full.height), _mask);
                GUI.DrawTexture(new Rect(0, -edge, full.width, full.height), _mask);
                GUI.DrawTexture(new Rect(0, edge, full.width, full.height), _mask);
                GUI.color = thermal ? new Color(1f, 0.22f, 0.04f, 1f) : new Color(0.25f, 0.85f, 0.20f, 1f);
                GUI.DrawTexture(full, _mask);
            }
            finally { GUI.color = old; }
        }

        static int QueueMaskTargets(Camera cam, List<Transform> targets, bool person, int draws)
        {
            Vector3 eye = cam.transform.position;
            float range = person ? PplRange : VehRange;
            float near = person ? PplNear : 0f;
            float pixels = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            for (int i = 0; i < targets.Count && draws < MaskDrawLimit; i++)
            {
                Transform root = targets[i];
                if (root == null || !root.gameObject.activeInHierarchy) continue;
                float distance2 = (root.position - eye).sqrMagnitude;
                if (!(distance2 >= near * near) || !(distance2 <= range * range)) continue;
                float radius = person ? 12f : 80f;
                if (!GeometryUtility.TestPlanesAABB(_maskPlanes, new Bounds(root.position, Vector3.one * (radius * 2f)))) continue;
                float size = (person ? 5f : 16f) * pixels / Mathf.Max(1f, Mathf.Sqrt(distance2));
                int level = size >= 220f ? 0 : (size >= 110f ? 1 : (size >= 45f ? 2 : 3));
                MaskEntry entry = MaskParts(root, level);
                if (entry == null) continue;
                _maskVisible.Clear();
                int needed = 0;
                for (int j = 0; j < entry.parts.Count; j++)
                {
                    MaskPart part = entry.parts[j];
                    Renderer r = part.renderer;
                    if (r == null || part.mesh == null || !r.gameObject.activeInHierarchy) continue;
                    if (!part.managed && !r.enabled) continue;
                    // Equipment may change before the timed hierarchy refresh.
                    Mesh live = part.skin != null ? part.skin.sharedMesh : (part.filter != null ? part.filter.sharedMesh : null);
                    if (live != part.mesh) { entry.built = -100f; continue; }
                    Bounds bounds = r.bounds;
                    if (!MaskBounds(bounds.center, bounds.extents, root.position, radius)) continue;
                    if (!GeometryUtility.TestPlanesAABB(_maskPlanes, bounds)) continue;
                    needed += part.mesh.subMeshCount;
                    _maskVisible.Add(part);
                }
                // All submeshes fit or none are drawn: never truncate a body.
                if (needed > MaskDrawLimit - draws) continue;
                for (int j = 0; j < _maskVisible.Count; j++)
                {
                    MaskPart part = _maskVisible[j];
                    for (int sub = 0; sub < part.mesh.subMeshCount; sub++)
                    {
                        _maskCommands.DrawRenderer(part.renderer, _maskMaterial, sub, 0);
                        draws++;
                    }
                }
            }
            return draws;
        }

        static bool MaskBounds(Vector3 centre, Vector3 extents, Vector3 root, float limit)
        {
            // Whole renderers must fit the target's world-space envelope. These
            // comparisons reject non-finite geometry as well as displaced parts.
            Vector3 delta = centre - root;
            return Mathf.Abs(delta.x) + extents.x <= limit
                && Mathf.Abs(delta.y) + extents.y <= limit
                && Mathf.Abs(delta.z) + extents.z <= limit
                && extents.x >= 0f && extents.y >= 0f && extents.z >= 0f;
        }

        static MaskEntry MaskParts(Transform root, int level)
        {
            int id = root.GetInstanceID();
            MaskEntry entry;
            float now = Time.time;
            if (_maskEntries.TryGetValue(id, out entry) && entry.root == root)
            {
                entry.used = now;
                if ((now - entry.built < 1.5f && entry.level == level) || _maskBuilds >= MaskBuildLimit) return entry;
            }
            else
            {
                if (_maskBuilds >= MaskBuildLimit) return null;
                entry = new MaskEntry();
                entry.root = root;
                _maskEntries[id] = entry;
            }
            _maskBuilds++;
            entry.parts.Clear();
            HashSet<Renderer> managed = new HashSet<Renderer>();
            HashSet<Renderer> skip = new HashSet<Renderer>();
            CollectPersonLods(root, level, managed, skip);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || !r.gameObject.activeInHierarchy || skip.Contains(r)) continue;
                if (!managed.Contains(r) && !r.enabled) continue;
                SkinnedMeshRenderer skin = r as SkinnedMeshRenderer;
                MeshFilter filter = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
                Mesh mesh = skin != null ? skin.sharedMesh : (filter != null ? filter.sharedMesh : null);
                if (mesh == null) continue; // No particles, trails or lines.
                MaskPart part = new MaskPart();
                part.renderer = r; part.mesh = mesh; part.skin = skin; part.filter = filter;
                part.managed = managed.Contains(r);
                entry.parts.Add(part);
            }
            entry.built = entry.used = now;
            entry.level = level;
            return entry;
        }

        static void PurgeMaskEntries()
        {
            if (Time.time < _maskPurgeAt) return;
            _maskPurgeAt = Time.time + 2f;
            _maskPurge.Clear();
            foreach (KeyValuePair<int, MaskEntry> pair in _maskEntries)
                if (pair.Value.root == null || Time.time - pair.Value.used > 5f) _maskPurge.Add(pair.Key);
            for (int i = 0; i < _maskPurge.Count; i++) _maskEntries.Remove(_maskPurge[i]);
        }


        static bool Project(Camera cam, Vector3 world, out Vector2 gui, out float dist)
        {
            gui = Vector2.zero;
            Vector3 sp = cam.WorldToScreenPoint(world);
            dist = sp.z;
            if (sp.z <= 0.5f) return false;            // behind the camera
            gui = new Vector2(sp.x, Screen.height - sp.y); // GUI y is top-down
            return true;
        }

        /// <summary>
        /// Rebuild the list of warm targets a few times a second. This is now a
        /// Transform collection only. Renderer hierarchies are cached separately
        /// on demand for targets in the view; no mesh data is copied.
        /// </summary>
        static void RefreshTargets(Camera cam)
        {
            if (Time.time < _warmUntil && _collectionCamera == cam
                && (cam.transform.position - _collectionEye).sqrMagnitude < 1600f) return;
            _warmUntil = Time.time + 0.35f;
            _collectionCamera = cam;
            _collectionEye = cam.transform.position;
            VehicleModules.Sweep();

            _warm.Clear();
            _veh.Clear();
            ResolveTypes();
            Vector3 eye = cam.transform.position;
            AddAll(_npcType, true, eye);    // AI (crew AND hostiles): skip the dead
            AddAll(_playerType, false, eye);
            AddVehicles(_vehType, eye);
            // Explosions are sampled from DrawVision, not here: they are brief and
            // the 0.35 s target tick was too coarse to catch them.
        }

        // Sample live ExplosionObject instances; seed a Flash for each newly seen
        // one and forget ids that are gone.
        //
        // FindObjectsOfType is a whole-scene sweep and the most expensive call in
        // this file. Running it on every repaint - which is what "sample explosions
        // EVERY frame" amounted to - is a per-scene walk that happens only while
        // the optic is up: exactly the shape of "the thermal view is laggy". A
        // blast lives a second or two and its flare 1.1 s, so ten samples a second
        // still catch it far sooner than the 0.35 s target tick ever did, at a
        // sixth of the cost on a 60 fps frame.
        static void ScanExplosions()
        {
            if (_explType == null) return;
            float now = Time.time;
            if (now < _nextBoom) return;
            _nextBoom = now + 0.10f;
            UnityEngine.Object[] objs;
            try { objs = UnityEngine.Object.FindObjectsOfType(_explType); }
            catch { return; }
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i] as Component;
                if (c == null || c.transform == null) continue;
                int id = c.GetInstanceID();
                if (!_boomSeen.ContainsKey(id))
                {
                    Flash f = new Flash();
                    f.pos = c.transform.position + new Vector3(0f, 1.0f, 0f);
                    f.born = now;
                    f.dur = 1.1f;
                    _flash.Add(f);
                }
                _boomSeen[id] = now;
            }
            _boomPurge.Clear();
            foreach (KeyValuePair<int, float> kv in _boomSeen)
                if (kv.Value < now) _boomPurge.Add(kv.Key);
            for (int i = 0; i < _boomPurge.Count; i++) _boomSeen.Remove(_boomPurge[i]);
        }

        // Draw the active explosion flares over the cold field, hottest of all:
        // a wide bloom, a hot mid ring and a white-hot core, fading with age.
        static void DrawFlashes(Camera cam, bool thermal)
        {
            if (_flash.Count == 0) return;
            float now = Time.time;
            for (int i = _flash.Count - 1; i >= 0; i--)
            {
                Flash f = _flash[i];
                float age = now - f.born;
                if (age >= f.dur) { _flash.RemoveAt(i); continue; }
                float life = 1f - age / f.dur;             // 1 .. 0
                float dist; Vector2 g;
                if (!Project(cam, f.pos, out g, out dist)) continue;
                if (dist > 2000f) continue;   // a tank-gun blast can land far off

                Vector2 ge; float ed; float px = 60f;      // from a ~6 m fireball
                if (Project(cam, f.pos + cam.transform.right * 6f, out ge, out ed))
                    px = Mathf.Abs(ge.x - g.x);
                px = Mathf.Clamp(px, 24f, 480f);
                Flare(g, px, life, thermal);
            }
        }

        static void Flare(Vector2 c, float px, float life, bool thermal)
        {
            float a = life < 0f ? 0f : (life > 1f ? 1f : life);
            if (thermal)
            {
                Glow(new Rect(c.x - px * 1.6f, c.y - px * 1.6f, px * 3.2f, px * 3.2f),
                     new Color(1.00f, 0.55f, 0.10f, 0.45f * a));   // orange bloom
                Glow(new Rect(c.x - px * 0.9f, c.y - px * 0.9f, px * 1.8f, px * 1.8f),
                     new Color(1.00f, 0.80f, 0.30f, 0.75f * a));   // hot shoulder
                HotGlow(new Rect(c.x - px * 0.5f, c.y - px * 0.5f, px, px),
                     new Color(1.00f, 0.97f, 0.85f, 0.95f * a));   // white-hot core
            }
            else
            {
                Glow(new Rect(c.x - px * 1.6f, c.y - px * 1.6f, px * 3.2f, px * 3.2f),
                     new Color(0.40f, 1.00f, 0.35f, 0.42f * a));
                HotGlow(new Rect(c.x - px * 0.5f, c.y - px * 0.5f, px, px),
                     new Color(0.85f, 1.00f, 0.70f, 0.95f * a));
            }
        }

        // Collect the living people near the viewer. The RANGE is decided here,
        // on the squared distance, before anything expensive happens: a map-wide
        // FindObjectsOfType costs what it costs, but everything after it - the
        // IsAlive reflection call, the per-frame projection, the silhouette -
        // then scales with what is actually near the optic instead of with how
        // many NPCs the whole map has.
        static void AddAll(Type t, bool crew, Vector3 eye)
        {
            if (t == null) return;
            // A little past the fill range, so a man walking in is already in the
            // list when he crosses it rather than popping in at the next tick.
            float far = PplRange + 60f;
            float far2 = far * far;
            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i] as Component;
                if (c == null || c.transform == null) continue;
                if ((c.transform.position - eye).sqrMagnitude > far2) continue;
                // A corpse cools: a dead crewman must not radiate. If IsAlive()
                // is missing or throws we keep the target (fail-safe, as before).
                if (crew && _npcAlive != null)
                {
                    try
                    {
                        object alive = _npcAlive.Invoke(c, null);
                        if (alive is bool && !(bool)alive) continue;
                    }
                    catch { }
                }
                _warm.Add(c.transform);
            }
        }

        static void AddVehicles(Type t, Vector3 eye)
        {
            if (t == null) return;
            Transform mine = Turret.MannedVehicle;   // do not glow our own vehicle
            // The vehicle type here IS VehicleGameSystem, so this takes the
            // shared scan instead of walking the whole scene again. The cache is
            // at most 0,25 s old and this list is rebuilt every 0,35 s, so the
            // targets are no staler than before - but while the optic is up, the
            // turret and this view now pay for one whole-scene scan, not two.
            float far = VehRange + 80f;
            float far2 = far * far;
            Component[] objs = VehicleScan.All();
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i];
                if (c == null || c.transform == null) continue;
                if (c.transform == mine) continue;
                if ((c.transform.position - eye).sqrMagnitude > far2) continue;
                _veh.Add(c.transform);
            }
        }

        // Pick one LOD per group for people and vehicles; only renderer
        // references are cached. All geometry and skinning stay on the GPU.
        static void CollectPersonLods(Transform root, int level,
                                      HashSet<Renderer> managed, HashSet<Renderer> skip)
        {
            try
            {
                LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
                for (int gi = 0; gi < groups.Length; gi++)
                {
                    if (groups[gi] == null) continue;
                    LOD[] lods = groups[gi].GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    // The COARSEST non-empty level at or below the wanted one; if
                    // none of those has renderers, the first level that does.
                    int keep = -1;
                    for (int li = 0; li < lods.Length; li++)
                    {
                        Renderer[] rs = lods[li].renderers;
                        if (rs == null || rs.Length == 0) continue;
                        if (keep < 0 || li <= level) keep = li;
                    }
                    for (int li = 0; li < lods.Length; li++)
                    {
                        Renderer[] rs = lods[li].renderers;
                        if (rs == null) continue;
                        for (int ri = 0; ri < rs.Length; ri++)
                        {
                            Renderer r = rs[ri];
                            if (r == null) continue;
                            managed.Add(r);
                            if (li != keep) skip.Add(r);
                        }
                    }
                    // Some models share an accessory across levels. Membership
                    // in a discarded level must not hide it in the chosen one.
                    if (keep >= 0)
                        for (int ri = 0; ri < lods[keep].renderers.Length; ri++)
                            skip.Remove(lods[keep].renderers[ri]);
                }
            }
            catch { }
        }

        static void ResolveTypes()
        {
            if (_typesResolved) return;
            _typesResolved = true;
            _npcType = AccessTools.TypeByName("NPC_AI2");
            _playerType = AccessTools.TypeByName("PlayerNetworkController");
            _vehType = AccessTools.TypeByName("VehicleGameSystem");
            _explType = AccessTools.TypeByName("ExplosionObject");
            if (_npcType != null)
                _npcAlive = AccessTools.Method(_npcType, "IsAlive", Type.EmptyTypes, null);
        }

        // ---------------------------------------------------------- framing

        static void DrawFrame(VisionMode mode)
        {
            float w = Screen.width, h = Screen.height;
            // Thin margins = a wide field of view, the point of the periscope.
            float mx = w * 0.055f, my = h * 0.095f;

            Color bar = new Color(0f, 0f, 0f, 0.66f);
            Fill(new Rect(0f, 0f, w, my), bar);
            Fill(new Rect(0f, h - my, w, my), bar);
            Fill(new Rect(0f, my, mx, h - 2f * my), bar);
            Fill(new Rect(w - mx, my, mx, h - 2f * my), bar);

            Color edge = EdgeColor(mode);
            float t = 2f;
            Fill(new Rect(mx, my, w - 2f * mx, t), edge);
            Fill(new Rect(mx, h - my - t, w - 2f * mx, t), edge);
            Fill(new Rect(mx, my, t, h - 2f * my), edge);
            Fill(new Rect(w - mx - t, my, t, h - 2f * my), edge);

            // Corner ticks - a technical, modern read.
            float ct = 22f, th = 2f;
            Color e = edge;
            // top-left
            Fill(new Rect(mx, my, ct, th), e); Fill(new Rect(mx, my, th, ct), e);
            // top-right
            Fill(new Rect(w - mx - ct, my, ct, th), e); Fill(new Rect(w - mx - th, my, th, ct), e);
            // bottom-left
            Fill(new Rect(mx, h - my - th, ct, th), e); Fill(new Rect(mx, h - my - ct, th, ct), e);
            // bottom-right
            Fill(new Rect(w - mx - ct, h - my - th, ct, th), e); Fill(new Rect(w - mx - th, h - my - ct, th, ct), e);
        }

        static Color EdgeColor(VisionMode mode)
        {
            if (mode == VisionMode.Thermal) return new Color(1f, 0.6f, 0.2f, 0.5f);
            if (mode == VisionMode.Night) return new Color(0.4f, 1f, 0.4f, 0.5f);
            return new Color(0.72f, 0.85f, 0.72f, 0.42f);
        }

        // ---------------------------------------------------------- reticle

        static void DrawReticle(VisionMode mode)
        {
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            Color col = mode == VisionMode.Thermal ? new Color(1f, 0.65f, 0.2f, 0.95f)
                      : mode == VisionMode.Night ? new Color(0.5f, 1f, 0.5f, 0.9f)
                      : new Color(0.85f, 1f, 0.85f, 0.9f);
            Color sh = new Color(0f, 0f, 0f, 0.55f);
            float gap = 8f, arm = 22f, th = 2f;

            Cross(cx + 1f, cy + 1f, gap, arm, th, sh);
            Cross(cx, cy, gap, arm, th, col);
            Fill(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), new Color(1f, 0.35f, 0.2f, 0.95f));

            Color lad = new Color(col.r, col.g, col.b, 0.55f);
            float step = Mathf.Max(12f, Screen.height * 0.025f);
            for (int i = 1; i <= 4; i++)
            {
                float y = cy + arm + gap + i * step;
                float len = 18f - i * 2f;
                Fill(new Rect(cx - len, y - 0.75f, len * 2f, 1.5f), lad);
            }
        }

        static void Cross(float cx, float cy, float gap, float arm, float th, Color c)
        {
            Fill(new Rect(cx - gap - arm, cy - th * 0.5f, arm, th), c);
            Fill(new Rect(cx + gap, cy - th * 0.5f, arm, th), c);
            Fill(new Rect(cx - th * 0.5f, cy - gap - arm, th, arm), c);
            Fill(new Rect(cx - th * 0.5f, cy + gap, th, arm), c);
        }

        // ----------------------------------------------------------- status

        static void DrawStatus(Transform veh, VisionMode mode)
        {
            float w = Screen.width, h = Screen.height;
            float mx = w * 0.055f, my = h * 0.095f;

            string modeTxt = mode == VisionMode.Thermal ? Loc.T("ТЕПЛО", "THERMAL")
                           : mode == VisionMode.Night ? Loc.T("НОЧЬ", "NIGHT")
                           : Loc.T("ОБЫЧНЫЙ", "NORMAL");

            VehicleModules.Slot s = VehicleModules.Get(veh);
            string mods = "";
            if (s != null)
            {
                if (s.Thermal) mods += Loc.T(" ТЕПЛО", " THERMAL");
                if (s.Night) mods += Loc.T(" НОЧЬ", " NIGHT");
                if (s.Jammer) mods += Loc.T(" РЭБ", " ECM");
            }
            if (mods.Length == 0) mods = Loc.T(" нет", " none");

            Color oldc = GUI.contentColor;
            GUI.contentColor = EdgeColor(mode);
            GUI.contentColor = new Color(GUI.contentColor.r, GUI.contentColor.g, GUI.contentColor.b, 0.95f);
            GUI.Label(new Rect(mx + 8f, my - 22f, w * 0.6f, 20f),
                      Loc.T("ОПТИКА: ", "OPTICS: ") + modeTxt
                      + Loc.T("   МОДУЛИ:", "   MODULES:") + mods);

            GUI.contentColor = new Color(0.82f, 0.86f, 0.82f, 0.72f);
            GUI.Label(new Rect(mx + 8f, h - my + 3f, w - 2f * mx - 16f, 20f),
                      Loc.T("N: режим   I: установить модуль   Shift+I: снять модуль",
                            "N: mode   I: install module   Shift+I: remove module"));
            GUI.contentColor = oldc;
        }
    }
}

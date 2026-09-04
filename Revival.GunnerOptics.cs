// Next Day: Survival - Revival Toolkit
// The gunner periscope: draws the modern, wide-field optic that replaces the
// old round scope, plus the thermal and night-vision overlays unlocked by the
// installed modules (Revival.Modules.cs).
//
// Rendering is pure IMGUI (this is called from Turret.DrawScope, inside OnGUI):
//   - THERMAL crushes the whole picture to a very dark, deep-BLUE, low-contrast
//     field: an almost fully opaque dark-blue quad kills the "normal view +
//     shadows" read, a radial vignette drops the optic edges to black, and a
//     faint sensor grain sells the detector. Nothing in the cold scene carries
//     usable detail - so the warm targets slam against it. NIGHT crushes to a
//     softer green light-gain wash instead of blue;
//   - warm things then BLAZE on top of that cold field. Because we cannot give
//     every world object a per-pixel heat value (no camera post-process on this
//     old Unity + BepInEx build), the things we DO know are warm - patrol/convoy
//     vehicles, all LIVING AI (settlement crew AND hostiles - both are NPC_AI2),
//     and players - are FILLED in their real mesh shape (GL triangles), coloured
//     by a radial ramp: THERMAL uses IRONBOW (a red-hot core, the way a real
//     sight shows a body/engine, fading to a yellow contour edge), NIGHT uses the
//     green light-gain equivalent (bright core, mid-green edge). So a target
//     reads as a lit SILHOUETTE with an outline in either mode, never an oval.
//     A target too small on screen or without a usable mesh falls back to a cheap
//     ramp blob in the same palette. DEAD crew no longer radiate - a corpse
//     cools, so an NPC that fails IsAlive() is dropped from the warm set;
//   - EXPLOSIONS radiate the hottest of all: each live ExplosionObject seeds a
//     short white-hot flare (DrawFlashes) that fades over about a second;
//   - the frame, reticle and status text are drawn on top.
//
// PERFORMANCE: the GL fill runs once per vertex, tens of thousands of times a
// frame. Two things keep it from stuttering - the ironbow/green ramp stops are
// static (no per-vertex array allocation, which was the old lag), and small or
// distant targets are drawn as blobs rather than filled, so only close targets
// pay for their triangles. A true per-pixel camera post-effect would be nicer
// still but needs a runtime shader this build cannot load reliably.
//
// ROBUSTNESS: warm bodies (crew, players, hostiles) are filled BEFORE vehicles so
// a big vehicle or wreck mesh can never eat the whole triangle budget and leave
// NPCs undrawn; any target the budget can no longer afford still shows as a blob.
// And EmitMesh skips any triangle whose on-screen box dwarfs the target - a wreck
// or debris that ends up close to the camera produces near-plane-straddling
// triangles that would otherwise stretch across the field and flood it yellow.
//
// ASCII-only code and comments; on-screen text is bilingual through Loc.T.

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
        static Texture2D _ramp;    // radial ironbow ramp, colours baked in: white/red core -> yellow rim
        static Texture2D _vig;    // radial vignette: clear centre, dark rim
        static Texture2D _grain;  // static sensor noise, tiled

        // Warm-target cache. FindObjectsOfType per frame is exactly the cost
        // that never shows in a log and always shows in the frame time, so the
        // list is rebuilt a few times a second and only projected each frame.
        static readonly List<Transform> _warm = new List<Transform>();   // people: crew + players
        static readonly List<Transform> _veh = new List<Transform>();     // vehicles
        static readonly List<float> _vehR = new List<float>();            // vehicle world radius (m)
        // Per-target mesh silhouettes, parallel to _veh / _warm. A target with a
        // usable mesh is FILLED in its real shape (GL triangles); one without falls
        // back to the old ramp ellipse. Vehicles are rigid (mesh + live transform);
        // people are skinned, baked to world verts at the refresh tick.
        static readonly List<Silh> _vehSilh = new List<Silh>();
        static readonly List<Silh> _warmSilh = new List<Silh>();
        static float _warmUntil;
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

        // GL immediate-mode fill (Unity 2018.1). Silhouettes are drawn as coloured
        // triangles over the cold field, so warm targets read in their real shape.
        // _silThermal picks the per-vertex ramp: ironbow for THERMAL, green for
        // NIGHT. It is set once before the GL pass and read inside Emit, so the
        // hot inner loop stays allocation- and branch-cheap.
        static bool _silThermal = true;
        static Material _glMat;
        static Mesh _bakeScratch;   // reused snapshot target for skinned bakes
        // mesh id -> (verts, tris). mesh.vertices/.triangles each allocate, so the
        // rigid meshes are copied once and only re-projected per frame; skinned tris
        // (pose-independent) are cached here too, keyed by the shared mesh.
        static readonly Dictionary<int, MeshData> _meshCache = new Dictionary<int, MeshData>();

        sealed class MeshData { public Vector3[] v; public int[] t; }
        sealed class RigidPart { public Vector3[] v; public int[] t; public Transform tr; }
        sealed class BakedPart { public Vector3[] wv; public int[] t; }   // world verts, baked at refresh
        sealed class Silh
        {
            public readonly List<RigidPart> rigid = new List<RigidPart>();
            public readonly List<BakedPart> skinned = new List<BakedPart>();
            public bool Any { get { return rigid.Count > 0 || skinned.Count > 0; } }
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

        /// <summary>
        /// One position along the ironbow heat ramp, keyed by radius t (0 = the
        /// hottest core, 1 = the coolest rim). A tiny white-hot pinpoint gives way
        /// to a deep RED core - the way a real thermal sight shows the engine block
        /// / body core as the hottest spot - then bleeds out through orange to a
        /// yellow edge. So a target is NOT one flat colour: it reads as a heat
        /// gradient with a red centre, exactly what the user asked for.
        /// </summary>
        // Ironbow ramp stops, hoisted to static readonly. This is the lag fix:
        // Ironbow() runs once PER VERTEX inside the GL fill (three times per
        // triangle, tens of thousands of triangles a frame). Allocating these
        // two arrays inside the method meant hundreds of thousands of throwaway
        // arrays every frame - a GC storm that showed up as exactly the thermal
        // stutter. Built once, they cost nothing per call.
        // t:   0.00      0.08      0.20      0.42      0.68      1.00
        static readonly float[] _ibT = { 0.00f, 0.08f, 0.20f, 0.42f, 0.68f, 1.00f };
        static readonly Color[] _ibC = {
            new Color(1.00f, 0.94f, 0.80f),  // white-hot pinpoint
            new Color(1.00f, 0.52f, 0.20f),  // hot amber shoulder
            new Color(1.00f, 0.16f, 0.05f),  // deep RED core
            new Color(1.00f, 0.42f, 0.05f),  // orange body
            new Color(1.00f, 0.72f, 0.10f),  // amber
            new Color(1.00f, 0.86f, 0.24f),  // yellow rim
        };

        static Color Ironbow(float t)
        {
            if (t <= _ibT[0]) return _ibC[0];
            for (int i = 1; i < _ibT.Length; i++)
            {
                if (t <= _ibT[i])
                {
                    float f = (t - _ibT[i - 1]) / (_ibT[i] - _ibT[i - 1]);
                    return Color.Lerp(_ibC[i - 1], _ibC[i], f);
                }
            }
            return _ibC[_ibC.Length - 1];
        }

        // Night light-gain equivalent of the ironbow ramp: a warm body glows a
        // bright yellow-green in the middle and fades to a mid-green silhouette
        // edge. Used so NIGHT fills the target's REAL mesh shape (an outline),
        // exactly like THERMAL, instead of the old flat green oval.
        static readonly Color _ngCore = new Color(0.82f, 1.00f, 0.55f);
        static readonly Color _ngRim = new Color(0.18f, 0.85f, 0.22f);

        static Color NightRamp(float t)
        {
            return Color.Lerp(_ngCore, _ngRim, t < 0f ? 0f : (t > 1f ? 1f : t));
        }

        /// <summary>
        /// The colored heat body: an ironbow ramp baked into the texture (red core,
        /// yellow rim) with a DEFINED edge so the target reads as a hot shape with a
        /// visible outline, not a formless yellow ball. Alpha is solid out to ~0.82
        /// of the radius, then a short ramp to zero; a thin brighter ring just
        /// inside the edge draws the contour so the silhouette stays legible against
        /// the cold field. A true per-pixel scene silhouette still needs the
        /// deferred camera shader; this is the closest asset-free read.
        /// </summary>
        static Texture2D HeatRamp()
        {
            if (_ramp != null) return _ramp;
            const int N = 128;
            _ramp = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _ramp.hideFlags = HideFlags.HideAndDontSave;
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);   // 0 centre .. 1 rim, >1 corners
                    if (d >= 1f) { _ramp.SetPixel(x, y, new Color(0f, 0f, 0f, 0f)); continue; }
                    Color col = Ironbow(d);
                    // A brighter contour ring just inside the rim so the edge reads.
                    if (d >= 0.80f && d <= 0.93f)
                        col = Color.Lerp(col, new Color(1.00f, 0.95f, 0.62f), 0.55f);
                    // Solid body, short soft edge -> a defined outline, not a fuzz.
                    float a = d <= 0.82f ? 1f : Mathf.Clamp01((1f - d) / 0.18f);
                    _ramp.SetPixel(x, y, new Color(col.r, col.g, col.b, a));
                }
            _ramp.Apply();
            return _ramp;
        }

        /// <summary>Draw the colored heat ramp into a rect. GUI.color stays white so
        /// the ramp's baked ironbow colours show; alpha scales the whole body.</summary>
        static void RampGlow(Rect r, float alpha)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(r, HeatRamp());
            GUI.color = old;
        }

        static void RampRect(Vector2 c, float halfW, float halfH, float alpha)
        {
            RampGlow(new Rect(c.x - halfW, c.y - halfH, halfW * 2f, halfH * 2f), alpha);
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
                DrawVision(mode, veh);
                DrawFrame(mode);
                DrawReticle(mode);
                DrawStatus(veh, mode);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("GunnerOptics: " + ex); }
            return true;
        }

        // ------------------------------------------------------ scene tint

        static void DrawVision(VisionMode mode, Transform veh)
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

            RefreshTargets();
            Camera cam = Camera.main;
            if (cam == null) return;

            // Both THERMAL and NIGHT now fill each target in its REAL shape (its
            // own mesh triangles): ironbow for thermal, a green light-gain ramp
            // for night. No ovals - a target reads as a lit silhouette with an
            // outline in either mode. Targets too small on screen or without a
            // usable mesh fall back to a cheap ramp blob inside this call.
            DrawSilhouettes(cam, thermal);

            // Explosions radiate: draw the short-lived heat flares on top, in the
            // mode's palette. Living AI (crew AND hostiles) are already in the
            // warm set above via NPC_AI2, so enemies glow like everything else.
            // Sample explosions EVERY frame (this runs once per Repaint), not only
            // at the 0.35 s target-refresh tick: a tank-shot blast is caught the
            // frame it appears instead of being missed between ticks.
            ScanExplosions();
            DrawFlashes(cam, thermal);
        }

        // ------------------------------------------------ thermal silhouettes

        static Material GLMat()
        {
            if (_glMat != null) return _glMat;
            Shader s = Shader.Find("Hidden/Internal-Colored");
            if (s == null) return null;
            _glMat = new Material(s);
            _glMat.hideFlags = HideFlags.HideAndDontSave;
            _glMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _glMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _glMat.SetInt("_Cull", (int)CullMode.Off);
            _glMat.SetInt("_ZWrite", 0);
            _glMat.SetInt("_ZTest", (int)CompareFunction.Always);   // no occlusion, as before
            return _glMat;
        }

        // Draw every warm target as filled mesh triangles in its true shape. The
        // GL fill only runs on the Repaint pass; targets with no mesh are collected
        // and drawn afterwards as the ramp ellipse, so nothing ever goes unlit.
        static readonly List<int> _fbVeh = new List<int>();
        static readonly List<int> _fbWarm = new List<int>();

        // Range caps and the on-screen size below which a target is cheaper (and
        // visually identical) as a ramp blob than as thousands of filled mesh
        // triangles. Routing small/distant targets to the blob path is the second
        // half of the lag fix, and it lets far enemies still show as a heat mark
        // without paying for their full mesh every frame.
        const float VehRange = 900f;
        const float PplRange = 600f;
        const float VehMinPx = 30f;
        const float PplMinPx = 22f;

        // Draws every warm target for the given mode. THERMAL colours the fill
        // with the ironbow ramp, NIGHT with the green light-gain ramp; both fill
        // the target's real mesh shape (an outline), never an oval.
        static void DrawSilhouettes(Camera cam, bool thermal)
        {
            _silThermal = thermal;
            _fbVeh.Clear();
            _fbWarm.Clear();

            bool repaint = Event.current == null || Event.current.type == EventType.Repaint;
            Material m = repaint ? GLMat() : null;
            if (m != null)
            {
                Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
                // Triangle ceiling per frame (only while aiming). Well clear of a
                // couple of close targets now that distant ones are blobs.
                int budget = 45000;
                m.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();          // screen pixels, origin bottom-left, y up
                GL.Begin(GL.TRIANGLES);

                // People (skinned) FIRST. A warm body - crew, player or hostile - is
                // the priority target and must never be starved of the shared
                // triangle budget by a big vehicle or wreck mesh. Drawing them before
                // vehicles, and routing any target the budget can no longer afford to
                // the blob fallback, is the fix for "thermal shows no NPCs": a single
                // detailed vehicle used to exhaust the 45000 ceiling before the people
                // loop ran at all, and those NPCs were then neither filled nor blobbed.
                for (int i = 0; i < _warm.Count; i++)
                {
                    Transform t = _warm[i];
                    Silh s = i < _warmSilh.Count ? _warmSilh[i] : null;
                    if (t == null) continue;
                    Vector3 chest = t.position + new Vector3(0f, 1.0f, 0f);
                    float dist; Vector2 g;
                    if (!Project(cam, chest, out g, out dist)) continue;
                    if (dist > PplRange) continue;

                    float px = Mathf.Clamp(1500f / dist, 8f, 46f);
                    // No mesh, too small to be worth its triangles, or the budget is
                    // spent: draw a cheap blob instead so the target never vanishes.
                    if (s == null || !s.Any || px < PplMinPx || budget <= 0) { _fbWarm.Add(i); continue; }
                    Vector2 centre = ScreenGL(g);
                    for (int p = 0; p < s.skinned.Count && budget > 0; p++)
                    {
                        BakedPart bp = s.skinned[p];
                        budget -= EmitMesh(bp.wv, bp.t, VP, centre, px, budget);
                    }
                    for (int p = 0; p < s.rigid.Count && budget > 0; p++)
                    {
                        RigidPart rp = s.rigid[p];
                        if (rp.tr == null) continue;
                        Matrix4x4 mvp = VP * rp.tr.localToWorldMatrix;
                        budget -= EmitMesh(rp.v, rp.t, mvp, centre, px, budget);
                    }
                }

                // Vehicles (rigid): fill the hull/turret/tracks in their real shape.
                for (int i = 0; i < _veh.Count; i++)
                {
                    Transform t = _veh[i];
                    Silh s = i < _vehSilh.Count ? _vehSilh[i] : null;
                    if (t == null) continue;
                    Vector3 mid = t.position + new Vector3(0f, 1.0f, 0f);
                    float dist; Vector2 g;
                    if (!Project(cam, mid, out g, out dist)) continue;
                    if (dist > VehRange) continue;

                    float r = i < _vehR.Count ? _vehR[i] : 3f;
                    Vector2 ge; float ed;
                    float px = 40f;
                    if (Project(cam, mid + cam.transform.right * r, out ge, out ed))
                        px = Mathf.Abs(ge.x - g.x);
                    px = Mathf.Clamp(px, 16f, 320f);
                    // No mesh, too small on screen, or budget spent: cheap ramp blob.
                    if (s == null || !s.Any || px < VehMinPx || budget <= 0) { _fbVeh.Add(i); continue; }
                    Vector2 centre = ScreenGL(g);        // GL y-up centre

                    for (int p = 0; p < s.rigid.Count && budget > 0; p++)
                    {
                        RigidPart rp = s.rigid[p];
                        if (rp.tr == null) continue;
                        Matrix4x4 mvp = VP * rp.tr.localToWorldMatrix;
                        budget -= EmitMesh(rp.v, rp.t, mvp, centre, px, budget);
                    }
                }

                GL.End();
                GL.PopMatrix();
            }
            else
            {
                // Not a repaint (or no GL material): everything falls back.
                for (int i = 0; i < _veh.Count; i++) _fbVeh.Add(i);
                for (int i = 0; i < _warm.Count; i++) _fbWarm.Add(i);
            }

            // Ramp blobs for any target that is meshless or too small to fill,
            // in the active mode's palette (ironbow for thermal, green for night).
            for (int k = 0; k < _fbVeh.Count; k++)
            {
                int i = _fbVeh[k];
                Transform t = _veh[i];
                if (t == null) continue;
                Vector3 mid = t.position + new Vector3(0f, 1.0f, 0f);
                float dist; Vector2 g;
                if (!Project(cam, mid, out g, out dist)) continue;
                if (dist > VehRange) continue;
                float r = i < _vehR.Count ? _vehR[i] : 3f;
                Vector2 ge; float ed; float px = 40f;
                if (Project(cam, mid + cam.transform.right * r, out ge, out ed))
                    px = Mathf.Abs(ge.x - g.x);
                px = Mathf.Clamp(px, 16f, 320f);
                VehicleGlow(g, px, thermal);
            }
            for (int k = 0; k < _fbWarm.Count; k++)
            {
                int i = _fbWarm[k];
                Transform t = _warm[i];
                if (t == null) continue;
                Vector3 chest = t.position + new Vector3(0f, 1.0f, 0f);
                float dist; Vector2 g;
                if (!Project(cam, chest, out g, out dist)) continue;
                if (dist > PplRange) continue;
                float size = Mathf.Clamp(1500f / dist, 8f, 46f);
                Blob(g, size, thermal);
            }
        }

        // GUI space is y-down (top-left); the GL pixel matrix is y-up (bottom-left).
        // Project() returns a GUI point, so flip y once for the GL centre.
        static Vector2 ScreenGL(Vector2 gui) { return new Vector2(gui.x, Screen.height - gui.y); }

        // Fill one mesh's triangles, each vertex coloured by the active mode's ramp
        // (ironbow for thermal, green for night) keyed by its distance from the
        // target's screen centre over the target radius, so the middle runs hot and
        // the silhouette edge runs cooler - a lit outline, not a disc. Vertices are
        // taken to clip space by mvp and divided by w by hand (fast, and correct on
        // 2018.1 without GL.GetGPUProjectionMatrix because we feed pixels, not a
        // matrix, to GL). Returns the number of triangles emitted (for the budget).
        static int EmitMesh(Vector3[] verts, int[] tris, Matrix4x4 mvp, Vector2 centre, float pxR, int budget)
        {
            if (verts == null || tris == null || verts.Length == 0) return 0;
            float invR = pxR > 1f ? 1f / pxR : 1f;
            float w = Screen.width, h = Screen.height;
            // A single triangle must never be much larger on screen than the target
            // itself. ProjV rejects only vertices fully behind the camera, not a
            // triangle that STRADDLES the near plane: such a triangle has one vertex
            // with a near-zero w and projects to enormous screen coordinates,
            // stretching across the whole field. Coloured at its far-from-centre rim
            // (which is the ramp's yellow), one such triangle floods the picture solid
            // yellow - exactly the "vehicle destroyed -> everything yellow" report,
            // where the wreck/debris ends up close to the camera. Skip any triangle
            // whose screen bounding box is far bigger than the target (NaN-safe: a NaN
            // fails the <= test and is skipped too).
            float triCap = pxR * 6f; if (triCap < 96f) triCap = 96f;
            int drawn = 0;
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                if (drawn >= budget) break;
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                float ax, ay, bx, by, cx, cy;
                if (!ProjV(mvp, verts[a], w, h, out ax, out ay)) continue;
                if (!ProjV(mvp, verts[b], w, h, out bx, out by)) continue;
                if (!ProjV(mvp, verts[c], w, h, out cx, out cy)) continue;

                float minx = ax < bx ? (ax < cx ? ax : cx) : (bx < cx ? bx : cx);
                float maxx = ax > bx ? (ax > cx ? ax : cx) : (bx > cx ? bx : cx);
                float miny = ay < by ? (ay < cy ? ay : cy) : (by < cy ? by : cy);
                float maxy = ay > by ? (ay > cy ? ay : cy) : (by > cy ? by : cy);
                if (!((maxx - minx) <= triCap) || !((maxy - miny) <= triCap)) continue;

                Emit(ax, ay, centre, invR);
                Emit(bx, by, centre, invR);
                Emit(cx, cy, centre, invR);
                drawn++;
            }
            return drawn;
        }

        static bool ProjV(Matrix4x4 mvp, Vector3 v, float w, float h, out float sx, out float sy)
        {
            // clip = mvp * (v,1); reject anything at or behind the near plane.
            float cx = mvp.m00 * v.x + mvp.m01 * v.y + mvp.m02 * v.z + mvp.m03;
            float cy = mvp.m10 * v.x + mvp.m11 * v.y + mvp.m12 * v.z + mvp.m13;
            float cw = mvp.m30 * v.x + mvp.m31 * v.y + mvp.m32 * v.z + mvp.m33;
            sx = sy = 0f;
            if (cw <= 0.0001f) return false;
            float inv = 1f / cw;
            sx = (cx * inv * 0.5f + 0.5f) * w;
            sy = (cy * inv * 0.5f + 0.5f) * h;   // GL pixel matrix is y-up
            return true;
        }

        static void Emit(float sx, float sy, Vector2 centre, float invR)
        {
            float dx = sx - centre.x, dy = sy - centre.y;
            float t = Mathf.Sqrt(dx * dx + dy * dy) * invR;
            if (t > 1f) t = 1f;
            Color col = _silThermal ? Ironbow(t) : NightRamp(t);
            GL.Color(new Color(col.r, col.g, col.b, 0.92f));
            GL.Vertex3(sx, sy, 0f);
        }

        // THERMAL warm targets are drawn with the ironbow HeatRamp (RampRect):
        // a red-hot core, an amber body and a bright yellow contour edge, so a
        // target reads as a heat gradient with a visible outline, not a flat
        // uniform yellow disc. NIGHT keeps the old solid-green body/bloom look.
        static readonly Color NightBody = new Color(0.45f, 1.00f, 0.30f, 1f);
        static readonly Color NightBloom = new Color(0.30f, 1.00f, 0.20f, 1f);

        static void SoftRect(Vector2 c, float halfW, float halfH, Color col)
        {
            Glow(new Rect(c.x - halfW, c.y - halfH, halfW * 2f, halfH * 2f), col);
        }

        static void HotRect(Vector2 c, float halfW, float halfH, Color col)
        {
            HotGlow(new Rect(c.x - halfW, c.y - halfH, halfW * 2f, halfH * 2f), col);
        }

        static void VehicleGlow(Vector2 c, float px, bool thermal)
        {
            // Wider than tall - a vehicle silhouette reads horizontal. A modest
            // bloom bleeds heat into the cold field (kept small so the target does
            // not swell into one big ball), then the hull is filled with the ironbow
            // ramp: a RED-hot core, an amber body and a bright yellow contour edge -
            // a heat gradient with a visible outline, not a flat yellow disc.
            float w = px * 1.5f, h = px * 0.95f;
            if (thermal)
            {
                Glow(new Rect(c.x - w * 1.20f, c.y - h * 1.28f, w * 2.40f, h * 2.56f),
                     new Color(1.00f, 0.55f, 0.06f, 0.28f));  // small orange bloom
                RampRect(c, w, h, 0.96f);                     // ironbow hull, red core
            }
            else
            {
                SoftRect(c, w * 1.30f, h * 1.40f, new Color(NightBloom.r, NightBloom.g, NightBloom.b, 0.42f));
                HotRect (c, w,         h,          new Color(NightBody.r,  NightBody.g,  NightBody.b,  0.96f));
            }
        }

        static void Blob(Vector2 c, float s, bool thermal)
        {
            // A person: crew and players run hotter than the hull, so the ramp is
            // pushed toward its core - a person reads as a small blazing red/white
            // heat source with a yellow edge, still a shape with an outline, never a
            // flat coin. Round only, never a square, never a bright single point.
            if (thermal)
            {
                Glow(new Rect(c.x - s * 1.7f, c.y - s * 1.7f, s * 3.4f, s * 3.4f),
                     new Color(1.00f, 0.50f, 0.08f, 0.30f));  // small hot bloom
                RampRect(c, s * 1.15f, s * 1.15f, 0.98f);     // ironbow body, red core
            }
            else
            {
                SoftRect(c, s * 2.0f, s * 2.0f, new Color(NightBloom.r, NightBloom.g, NightBloom.b, 0.42f));
                HotRect (c, s * 1.15f, s * 1.15f, new Color(NightBody.r, NightBody.g, NightBody.b, 0.98f));
            }
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

        static void RefreshTargets()
        {
            if (Time.time < _warmUntil) return;
            _warmUntil = Time.time + 0.35f;
            VehicleModules.Sweep();

            _warm.Clear();
            _veh.Clear();
            _vehR.Clear();
            _vehSilh.Clear();
            _warmSilh.Clear();
            ResolveTypes();
            AddAll(_npcType, true);    // AI (crew AND hostiles): skip the dead
            AddAll(_playerType, false);
            AddVehicles(_vehType);
            // Explosions are sampled every frame from DrawVision, not here: they
            // are brief and the 0.35 s target tick was too coarse to catch them.
        }

        // Sample live ExplosionObject instances; seed a Flash for each newly seen
        // one and forget ids that are gone. Cheap: explosions are rare and one
        // extra FindObjectsOfType at the refresh tick (a few times a second).
        static void ScanExplosions()
        {
            if (_explType == null) return;
            float now = Time.time;
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

        static void AddAll(Type t, bool crew)
        {
            if (t == null) return;
            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i] as Component;
                if (c == null || c.transform == null) continue;
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
                _warmSilh.Add(BuildPerson(c.transform));
            }
        }

        static void AddVehicles(Type t)
        {
            if (t == null) return;
            Transform mine = Turret.MannedVehicle;   // do not glow our own vehicle
            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i] as Component;
                if (c == null || c.transform == null) continue;
                if (c.transform == mine) continue;
                _veh.Add(c.transform);
                _vehR.Add(VehicleRadius(c.transform));
                _vehSilh.Add(BuildRigid(c.transform));
            }
        }

        // Cached (verts, tris) for a mesh - mesh.vertices/.triangles each allocate
        // a fresh copy, so pull them once and reuse the arrays every frame.
        static MeshData Cache(Mesh mesh)
        {
            if (mesh == null) return null;
            int id = mesh.GetInstanceID();
            MeshData d;
            if (_meshCache.TryGetValue(id, out d)) return d;
            d = new MeshData();
            try { d.v = mesh.vertices; d.t = mesh.triangles; }
            catch { d = null; }
            _meshCache[id] = d;
            return d;
        }

        // A rigid target (vehicle): each child MeshFilter becomes a RigidPart with
        // the shared mesh (cached) and its live transform, so the hull/turret/tracks
        // are filled in their real shape and follow the vehicle every frame.
        static Silh BuildRigid(Transform root)
        {
            Silh s = new Silh();
            try
            {
                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                for (int i = 0; i < mfs.Length; i++)
                {
                    MeshFilter mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    Renderer r = mf.GetComponent<Renderer>();
                    if (r != null && !r.enabled) continue;
                    MeshData d = Cache(mf.sharedMesh);
                    if (d == null || d.v == null || d.t == null || d.v.Length == 0) continue;
                    RigidPart rp = new RigidPart();
                    rp.v = d.v; rp.t = d.t; rp.tr = mf.transform;
                    s.rigid.Add(rp);
                }
            }
            catch { }
            return s;
        }

        // A person: bake each SkinnedMeshRenderer to a world-space snapshot at the
        // refresh tick (baking every frame is too costly), plus any rigid child
        // meshes (helmet, weapon). The baked pose lags at most one refresh - fine
        // for a heat silhouette. Triangles are pose-independent, so they are cached.
        static Silh BuildPerson(Transform root)
        {
            Silh s = new Silh();
            try
            {
                SkinnedMeshRenderer[] sk = root.GetComponentsInChildren<SkinnedMeshRenderer>();
                for (int i = 0; i < sk.Length; i++)
                {
                    SkinnedMeshRenderer smr = sk[i];
                    if (smr == null || smr.sharedMesh == null || !smr.enabled) continue;
                    if (_bakeScratch == null) _bakeScratch = new Mesh();
                    _bakeScratch.hideFlags = HideFlags.HideAndDontSave;
                    smr.BakeMesh(_bakeScratch);
                    Vector3[] lv = _bakeScratch.vertices;
                    if (lv == null || lv.Length == 0) continue;
                    // BakeMesh yields verts in the renderer transform's local space;
                    // take them to world with its full localToWorld (scale 1 for
                    // these characters, so no double-scale).
                    Matrix4x4 mtx = smr.transform.localToWorldMatrix;
                    Vector3[] wv = new Vector3[lv.Length];
                    for (int k = 0; k < lv.Length; k++) wv[k] = mtx.MultiplyPoint3x4(lv[k]);
                    MeshData td = Cache(smr.sharedMesh);   // triangles only (pose-independent)
                    if (td == null || td.t == null) continue;
                    BakedPart bp = new BakedPart();
                    bp.wv = wv; bp.t = td.t;
                    s.skinned.Add(bp);
                }
                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                for (int i = 0; i < mfs.Length; i++)
                {
                    MeshFilter mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    Renderer r = mf.GetComponent<Renderer>();
                    if (r != null && !r.enabled) continue;
                    MeshData d = Cache(mf.sharedMesh);
                    if (d == null || d.v == null || d.t == null || d.v.Length == 0) continue;
                    RigidPart rp = new RigidPart();
                    rp.v = d.v; rp.t = d.t; rp.tr = mf.transform;
                    s.rigid.Add(rp);
                }
            }
            catch { }
            return s;
        }

        /// <summary>Horizontal world radius of a vehicle from its child renderers,
        /// used to size its heat glow on screen. Clamped and never throwing.</summary>
        static float VehicleRadius(Transform t)
        {
            try
            {
                Renderer[] rs = t.GetComponentsInChildren<Renderer>();
                if (rs == null || rs.Length == 0) return 3f;
                Bounds b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                float rad = new Vector2(b.extents.x, b.extents.z).magnitude;
                return Mathf.Clamp(rad, 1.5f, 14f);
            }
            catch { return 3f; }
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

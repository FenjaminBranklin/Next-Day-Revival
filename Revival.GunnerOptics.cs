// Next Day: Survival - Revival Toolkit
// The gunner periscope: draws the modern, wide-field optic that replaces the
// old round scope, plus the thermal and night-vision overlays unlocked by the
// installed modules (Revival.Modules.cs).
//
// TWO VIEWERS use this file. Turret.DrawScope calls Draw() for the gunner
// periscope - frame, reticle, status and all. SurvDrone.Draw calls DrawExternal()
// for the recon drone, which keeps its own video-feed HUD and only wants the
// sensor picture underneath it: same cold field, same warm silhouettes, its own
// camera. Everything below therefore talks about "the viewer", not "the gunner".
//
// Rendering is pure IMGUI (both entry points run inside OnGUI):
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
//     A vehicle whose GAME mesh cannot be read on the CPU (isReadable == false -
//     a shipped BTR) is filled as an ORIENTED BOX per part, built from each
//     part's bounds: hull, turret and every wheel read as a heat silhouette that
//     turns with the vehicle, not a round oval. The custom T-72 (runtime-built,
//     readable) fills in its true mesh. Only a very small/distant person, or a
//     target with no geometry at all, still falls back to a cheap ramp blob in
//     the same palette. DEAD crew no longer radiate - a corpse
//     cools, so an NPC that fails IsAlive() is dropped from the warm set;
//   - EXPLOSIONS radiate the hottest of all: each live ExplosionObject seeds a
//     short white-hot flare (DrawFlashes) that fades over about a second;
//   - the frame, reticle and status text are drawn on top.
//
// PERFORMANCE: the GL fill runs once per vertex, tens of thousands of times a
// frame. Four things keep it from stuttering - the ironbow/green ramp stops are
// static (no per-vertex array allocation, which was the old lag); silhouettes
// are built LAZILY, only for a target that is actually about to be filled, and
// kept across refresh ticks (the old code re-BAKED every NPC on the map, in or
// out of view, several times a second - the real stutter); only the few largest
// targets on screen are filled at all, the rest blob; and a person far away is
// built from a COARSE LOD instead of the full character mesh. A true per-pixel
// camera post-effect would be nicer still but needs a runtime shader this build
// cannot load reliably.
//
// SIZE IS MEASURED, NEVER GUESSED: a human here is about 5 WORLD UNITS tall (the
// NPC capsule is 5.0 and the chest sits 3.3 above the feet - world units are not
// metres in this game) and the gunner optic runs at 32 or 20 degrees, a 2-3x
// zoom over the game's 60. The old "1500/dist" size guess ignored both, called
// every person beyond ~100 units tiny and drew them as a ramp dot: that is why
// vehicles - which measure their size by projecting their own radius - read as
// real shapes while people did not. People are now measured the same way, from
// the camera's field of view, so the optic's zoom counts.
//
// ROBUSTNESS: each target gets its OWN triangle cap (people are still filled
// before vehicles, but neither can drain a shared pool now), so one big vehicle
// can no longer starve every other into an oval - the bug this file last shipped.
// A generous per-frame ceiling only bites in a pathological scene.
//
// NOTHING IS DRAWN AWAY FROM ITS TARGET. A triangle is emitted only if it lands
// inside a box around the target's own projected centre, sized from the target's
// measured on-screen size (Target.span). One test, three causes of the "random
// geometric shapes glitching around the field" report: a triangle straddling the
// near plane, which projects to enormous coordinates and floods the picture; an
// index list cached for a mesh that has since been swapped; and a part whose
// bounds box came out larger than the whole vehicle. Cheap, and it never reaches
// a triangle that really belongs to the target. The cause behind most of that
// report was upstream of the guard, in the silhouette itself: BuildPerson bakes
// a man into WORLD space but never captured the bakeInv that the draw uses to
// undo his world placement, so every baked vertex went through his
// localToWorld twice and his heat shape was painted at a point that swung around
// the map as he turned. GL work is also flushed in blocks (GLBlockTris) rather
// than handed to immediate mode as one huge Begin/End batch.
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
        static readonly List<float> _vehR = new List<float>();            // vehicle world radius (units)
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
        static float _nextBoom;   // next explosion sample (see ScanExplosions)

        // GL immediate-mode fill (Unity 2018.1). Silhouettes are drawn as coloured
        // triangles over the cold field, so warm targets read in their real shape.
        // _silThermal picks the per-vertex ramp: ironbow for THERMAL, green for
        // NIGHT. It is set once before the GL pass and read inside Emit, so the
        // hot inner loop stays allocation- and branch-cheap.
        static bool _silThermal = true;
        static Material _glMat;
        // Unity's immediate mode collects everything between GL.Begin and GL.End
        // into ONE dynamic mesh. A settlement fight can hand it six people and six
        // vehicles in a single block - well past a hundred thousand triangles, and
        // so past the 65535 vertices a 16-bit index buffer addresses that it is not
        // worth finding out what this build does with the overflow: the failure
        // mode of a mesh whose indices wrap is triangles stitched between unrelated
        // vertices, which is the very report this file is being fixed for.
        // HYPOTHESIS, not measured: that the block ever actually overran here.
        // Closing and reopening it every GLBlockTris triangles costs nothing if it
        // did not - the material and the matrix stack survive a Begin/End pair, so
        // this is a draw call boundary and nothing else, a dozen a frame at most.
        const int GLBlockTris = 8192;
        static int _glBlock;
        static Mesh _bakeScratch;   // reused snapshot target for skinned bakes
        // mesh id -> (verts, tris). mesh.vertices/.triangles each allocate, so the
        // rigid meshes are copied once and only re-projected per frame; skinned tris
        // (pose-independent) are cached here too, keyed by the shared mesh.
        static readonly Dictionary<int, MeshData> _meshCache = new Dictionary<int, MeshData>();
        // source mesh id -> the index list of its BAKED snapshot. BakeMesh keeps the
        // source topology, so this never changes while a man walks; reading
        // Mesh.triangles off the snapshot at every bake copied it out again.
        static readonly Dictionary<int, int[]> _bakedTris = new Dictionary<int, int[]>();

        sealed class MeshData { public Vector3[] v; public int[] t; }
        // A rigid mesh part reprojected live every frame: its verts are in the
        // space of `tr` (a shared MeshFilter mesh under its own transform, a box
        // built from a part's local bounds, or a vehicle's skinned part resolved
        // into the vehicle root's space), so the draw places it with tr's full
        // local-to-world matrix and nothing else.
        sealed class RigidPart { public Vector3[] v; public int[] t; public Transform tr; }
        sealed class BakedPart { public Vector3[] wv; public int[] t; }   // world verts, baked at refresh
        sealed class Silh
        {
            public readonly List<RigidPart> rigid = new List<RigidPart>();
            public readonly List<BakedPart> skinned = new List<BakedPart>();
            // The baked world verts are a SNAPSHOT of one pose. bakeInv is the
            // root's worldToLocal at that moment, so the draw can rigidly carry the
            // snapshot along with the live transform
            // (root.localToWorld * bakeInv): a walking man's silhouette sits on him
            // every frame and only his LIMB pose is as old as the last bake,
            // instead of the whole body trailing a third of a second behind.
            //
            // BuildPerson MUST set this. Left at identity the draw multiplies
            // world-space verts by the man's localToWorld a SECOND time, which
            // throws his silhouette to (R * p + p) - a point that swings around the
            // map as he turns and lands anywhere on screen. That is what painted
            // the field with random drifting geometry; the identity default is a
            // safe no-op only for a silhouette that carries no baked part at all.
            public Matrix4x4 bakeInv = Matrix4x4.identity;
            public bool Any { get { return rigid.Count > 0 || skinned.Count > 0; } }
        }

        // Silhouettes are expensive to build (BakeMesh plus a world-space copy of
        // every vertex) and cheap to keep, so they are built on demand for the
        // targets that are actually being filled and cached by instance id across
        // refresh ticks. The old code rebuilt EVERY npc and vehicle on the map at
        // every 0.35 s tick, in view or not, filled or not - hundreds of bakes and
        // tens of megabytes of throwaway arrays a second. That was the stutter.
        sealed class SilhEntry
        {
            public Silh s;
            public Transform tr;
            public float built;   // Time.time of the last build
            public float used;    // Time.time it was last drawn (for purging)
            public int level;     // LOD level it was built at (people)
        }
        static readonly Dictionary<int, SilhEntry> _silCache = new Dictionary<int, SilhEntry>();
        static readonly Dictionary<int, float> _vehRadius = new Dictionary<int, float>();
        static readonly List<int> _silPurge = new List<int>();
        static float _nextSilPurge;
        static int _builtThisFrame;
        // A person is re-baked at about the old refresh rate (the pose moves); a
        // vehicle's rigid parts follow their live transforms, so a rebuild is only
        // needed when a part is shot off - seconds, not frames.
        const float PersonRebuild = 0.30f;
        const float VehicleRebuild = 1.50f;
        const int MaxBuildsPerFrame = 2;

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

            // Both THERMAL and NIGHT now fill each target in its REAL shape (its
            // own mesh triangles): ironbow for thermal, a green light-gain ramp
            // for night. No ovals - a target reads as a lit silhouette with an
            // outline in either mode. Targets too small on screen or without a
            // usable mesh fall back to a cheap ramp blob inside this call.
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
        // GL fill only runs on the Repaint pass; targets with no mesh, too small to
        // be worth their triangles, or beyond the per-frame fill count are collected
        // and drawn afterwards as the ramp blob, so nothing ever goes unlit.
        struct FallBack { public Vector2 gui; public float px; }
        static readonly List<FallBack> _fbVeh = new List<FallBack>();
        static readonly List<FallBack> _fbWarm = new List<FallBack>();

        // One projected target, measured ONCE per frame and then either filled or
        // blobbed. The old code projected every target twice - once in the fill loop
        // and again in the fallback loop - and threw the first result away.
        //
        // px is the size the LOOK is built on (clamped, so a blob never swells past
        // the field and the fill threshold compares like with like). span is the
        // same measurement UNCLAMPED and is used for one thing only: deciding how
        // far from the target's screen centre its own geometry can possibly reach.
        // A vehicle filling the screen really does span thousands of pixels, and a
        // guard built on the clamped number would cut its hull away.
        struct Target { public int index; public Vector2 gui; public float px; public float span; }
        static readonly List<Target> _tgtWarm = new List<Target>();
        static readonly List<Target> _tgtVeh = new List<Target>();

        // Range caps and the on-screen size below which a person is cheaper (and
        // visually identical) as a ramp blob than as thousands of filled mesh
        // triangles. Routing small/distant targets to the blob path is the second
        // half of the lag fix, and it lets far enemies still show as a heat mark
        // without paying for their full mesh every frame.
        const float VehRange = 900f;
        const float PplRange = 600f;
        // Half a body length in front of the lens. Closer than this a man is not a
        // target, he is the VIEWER - manning a turret puts the gunner's own body
        // within arm's reach of the camera - or someone standing against it. Either
        // way his chest projects to a body several screens tall whose triangles all
        // straddle the near plane, so the optic would spend its whole person budget
        // on geometry it then has to throw away, and paint a heat mark in the
        // middle of the field for it. The one place the view must stay clean is
        // where the gunner is looking.
        const float PplNear = 2.5f;
        // The person's on-screen BODY HEIGHT in pixels below which the blob is
        // drawn. This is a MEASURED height now (see BodyHeight/PixelsPerUnit), not
        // the old "1500/dist" guess that ignored both the world-unit scale and the
        // optic's zoom and so turned every person past about 100 units into a dot.
        // Kept low on purpose: a man below this reads as a mark, and "people are
        // only dots" is the complaint this whole measurement exists to answer.
        // Going lower costs nothing in the worst case - MaxFillPeople, not this,
        // bounds how many bodies are filled, and PersonLod sends a small one to the
        // coarsest LOD the character already ships.
        const float PplMinPx = 14f;
        // A human is about 5 world units tall here and his chest sits 3.3 above his
        // feet: the NPC CapsuleCollider is 5.0/0.75 and REVERSE_ENGINEERING records
        // the chest at 3.3, eyes at 4.2. Metres are NOT units in this game - the old
        // code took 1.0 (knee height) as the heat centre, so the hot core of the
        // ramp sat at a man's knees and his whole body ran at the ramp's rim colour.
        const float BodyHeight = 5.0f;
        const float ChestUp = 3.3f;
        // How many targets of each kind may be FILLED in one frame - the largest on
        // screen win, everything else blobs. A triangle budget alone does not bound
        // the work: twenty close NPCs each cost their own mesh every frame long
        // before any budget notices. This is the ceiling that keeps the view smooth
        // in a settlement fight, and it degrades to the blob, never to nothing.
        const int MaxFillPeople = 6;
        const int MaxFillVehicles = 6;
        static readonly float[] _topPx = new float[8];   // scratch for FillThreshold
        // Per-target triangle caps (used in DrawSilhouettes). Each target gets its
        // OWN cap so no target can drain a shared pool and leave the next as an
        // oval. Sized to hold a WHOLE target so nothing is truncated into holes:
        // the full custom T-72 (hull+turret+tracks+wheels) is ~31.6k triangles, a
        // baked character a few thousand, and a game vehicle whose mesh cannot be
        // read is a handful of boxes at ~12 triangles each.
        const int PerPerson = 12000;
        const int PerVehicle = 40000;

        // Draws every warm target for the given mode. THERMAL colours the fill
        // with the ironbow ramp, NIGHT with the green light-gain ramp; both fill
        // the target's real mesh shape (an outline), never an oval.
        static void DrawSilhouettes(Camera cam, bool thermal)
        {
            _silThermal = thermal;
            _fbVeh.Clear();
            _fbWarm.Clear();
            _tgtWarm.Clear();
            _tgtVeh.Clear();
            _builtThisFrame = 0;

            // Pass 1 - project and MEASURE every target once, and throw away what
            // is out of range or off the screen before it can cost anything.
            float ppu = PixelsPerUnit(cam);
            float sw = Screen.width, sh = Screen.height;
            // The ceiling on how far a target's own geometry may reach from its
            // centre (Target.span). A target close enough to fill the whole view
            // legitimately spans the screen; nothing legitimately spans it three
            // times over, so this is where a near-plane triangle with its enormous
            // coordinates stops being treated as geometry at all.
            float maxSpan = (sw > sh ? sw : sh) * 3f + 96f;
            for (int i = 0; i < _warm.Count; i++)
            {
                Transform t = _warm[i];
                if (t == null) continue;
                Vector3 chest = t.position + new Vector3(0f, ChestUp, 0f);
                float dist; Vector2 g;
                if (!Project(cam, chest, out g, out dist)) continue;
                if (dist > PplRange || dist < PplNear) continue;
                float px = BodyHeight * ppu / (dist > 1f ? dist : 1f);
                if (!(px >= 0f)) continue;                 // NaN-safe
                if (px > 4000f) px = 4000f;
                // A man's mesh reaches about two thirds of his body height from the
                // chest point that was projected; twice the height plus a floor is
                // room enough for him, his rifle and his pack at any range.
                float span = px * 2f + 96f;
                if (span > maxSpan) span = maxSpan;
                if (g.x < -span || g.x > sw + span || g.y < -span || g.y > sh + span) continue;
                Target tg = new Target();
                tg.index = i; tg.gui = g; tg.px = px; tg.span = span;
                _tgtWarm.Add(tg);
            }
            for (int i = 0; i < _veh.Count; i++)
            {
                Transform t = _veh[i];
                if (t == null) continue;
                Vector3 mid = t.position + new Vector3(0f, 1.0f, 0f);
                float dist; Vector2 g;
                if (!Project(cam, mid, out g, out dist)) continue;
                if (dist > VehRange) continue;
                // A vehicle measures its own half width by projecting a point one
                // radius to the side - it always did, which is why vehicles read as
                // shapes while people did not.
                float r = i < _vehR.Count ? _vehR[i] : 3f;
                Vector2 ge; float ed;
                float raw = 40f;
                if (Project(cam, mid + cam.transform.right * r, out ge, out ed))
                    raw = Mathf.Abs(ge.x - g.x);
                if (!(raw >= 0f) || raw > 20000f) raw = 20000f;   // NaN-safe
                float px = Mathf.Clamp(raw, 16f, 320f);
                // The measured radius already encloses the hull in x and z, so four
                // radii from the centre covers hull, turret, tracks and a gun barrel
                // even on the longest vehicle - measured UNCLAMPED, because a
                // vehicle right in front of the optic is genuinely that big.
                float span = raw * 4f + 96f;
                if (span > maxSpan) span = maxSpan;
                if (g.x < -span || g.x > sw + span || g.y < -span || g.y > sh + span) continue;
                Target tg = new Target();
                tg.index = i; tg.gui = g; tg.px = px; tg.span = span;
                _tgtVeh.Add(tg);
            }

            // Only the biggest few of each kind are filled; the cut is the k-th
            // largest on-screen size, found without sorting or allocating.
            float warmCut = FillThreshold(_tgtWarm, MaxFillPeople);
            float vehCut = FillThreshold(_tgtVeh, MaxFillVehicles);

            bool repaint = Event.current == null || Event.current.type == EventType.Repaint;
            Material m = repaint ? GLMat() : null;
            if (m != null)
            {
                Matrix4x4 VP = cam.projectionMatrix * cam.worldToCameraMatrix;
                // Per-target triangle budgets. The old design shared ONE small
                // ceiling across every target and drew people first, so the first
                // vehicle(s) drained it and every later vehicle fell back to the
                // ramp oval - the "only one tank is a real shape, the rest are big
                // ovals" bug. Now each target gets its OWN cap, so no target can
                // starve the next; a generous FRAME ceiling only ever bites in a
                // pathological scene (dozens of close vehicles at once), degrading
                // to ovals for the overflow rather than for everything but one.
                int frameBudget = 120000;   // global safety valve, rarely reached
                int spent = 0;
                m.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();          // screen pixels, origin bottom-left, y up
                _glBlock = 0;
                GL.Begin(GL.TRIANGLES);

                // People (skinned) FIRST: a warm body - crew, player or hostile - is
                // the priority target and gets its own cap before vehicles, so it
                // can never be starved by a big vehicle mesh. With per-target caps
                // both people and vehicles now fit within one frame.
                for (int k = 0; k < _tgtWarm.Count; k++)
                {
                    Target tg = _tgtWarm[k];
                    Transform t = _warm[tg.index];
                    if (t == null) continue;
                    int cap = PerPerson, left = frameBudget - spent;
                    if (cap > left) cap = left;
                    // Too small to be worth its triangles, not one of the biggest
                    // few on screen, or the frame ceiling is reached: a cheap blob,
                    // so the target never vanishes.
                    if (tg.px < PplMinPx || tg.px < warmCut || cap <= 0) { Fallback(_fbWarm, tg); continue; }
                    // Built on demand, at a detail level that matches how big he is
                    // on screen, and kept until he moves enough to need a new pose.
                    Silh s = TargetSilh(t, true, PersonLod(tg.px));
                    if (s == null || !s.Any) { Fallback(_fbWarm, tg); continue; }
                    Vector2 centre = ScreenGL(tg.gui);
                    // The ramp radius is about half the body: the chest runs
                    // white/red hot and the limbs cool towards the yellow contour,
                    // which is what a thermal sight shows.
                    float pxR = tg.px * 0.55f;
                    int used = 0;
                    // The baked pose is carried along by the live transform, so a
                    // walking man does not trail behind his own silhouette.
                    Matrix4x4 follow = VP * (t.localToWorldMatrix * s.bakeInv);
                    for (int p = 0; p < s.skinned.Count && used < cap; p++)
                    {
                        BakedPart bp = s.skinned[p];
                        used += EmitMesh(bp.wv, bp.t, follow, centre, pxR, tg.span, cap - used);
                    }
                    for (int p = 0; p < s.rigid.Count && used < cap; p++)
                    {
                        RigidPart rp = s.rigid[p];
                        if (rp.tr == null) continue;
                        Matrix4x4 mvp = VP * rp.tr.localToWorldMatrix;
                        used += EmitMesh(rp.v, rp.t, mvp, centre, pxR, tg.span, cap - used);
                    }
                    // Everything was clipped away (all of him behind the near plane,
                    // or a mesh that projected to nothing): show the mark anyway.
                    if (used == 0) { Fallback(_fbWarm, tg); continue; }
                    spent += used;
                }

                // Vehicles (rigid): fill the hull/turret/tracks/wheels in their real
                // shape. A vehicle whose game mesh the CPU cannot read
                // (isReadable == false - the shipped BTR) is built as an oriented
                // box per part in BuildRigid, so it STILL reads as a hull-and-turret
                // silhouette that turns with the vehicle, never a formless oval.
                for (int k = 0; k < _tgtVeh.Count; k++)
                {
                    Target tg = _tgtVeh[k];
                    Transform t = _veh[tg.index];
                    if (t == null) continue;
                    int cap = PerVehicle, left = frameBudget - spent;
                    if (cap > left) cap = left;
                    // Only genuinely missing geometry, the per-frame fill count or
                    // the frame ceiling needs the oval fallback now - every readable
                    // OR boxed vehicle has a Silh.
                    if (tg.px < vehCut || cap <= 0) { Fallback(_fbVeh, tg); continue; }
                    Silh s = TargetSilh(t, false, 0);
                    if (s == null || !s.Any) { Fallback(_fbVeh, tg); continue; }
                    Vector2 centre = ScreenGL(tg.gui);        // GL y-up centre
                    int used = 0;
                    for (int p = 0; p < s.rigid.Count && used < cap; p++)
                    {
                        RigidPart rp = s.rigid[p];
                        if (rp.tr == null) continue;
                        Matrix4x4 mvp = VP * rp.tr.localToWorldMatrix;
                        used += EmitMesh(rp.v, rp.t, mvp, centre, tg.px, tg.span, cap - used);
                    }
                    if (used == 0) { Fallback(_fbVeh, tg); continue; }
                    spent += used;
                }

                GL.End();
                GL.PopMatrix();
            }
            else
            {
                // Not a repaint (or no GL material): everything falls back.
                for (int k = 0; k < _tgtVeh.Count; k++) Fallback(_fbVeh, _tgtVeh[k]);
                for (int k = 0; k < _tgtWarm.Count; k++) Fallback(_fbWarm, _tgtWarm[k]);
            }

            // Ramp blobs for any target that is meshless or too small to fill,
            // in the active mode's palette (ironbow for thermal, green for night).
            // Everything was measured in pass 1; nothing is projected twice.
            for (int k = 0; k < _fbVeh.Count; k++)
                VehicleGlow(_fbVeh[k].gui, _fbVeh[k].px, thermal);
            for (int k = 0; k < _fbWarm.Count; k++)
                Blob(_fbWarm[k].gui, BlobSize(_fbWarm[k].px), thermal);
        }

        static void Fallback(List<FallBack> list, Target tg)
        {
            FallBack fb = new FallBack();
            fb.gui = tg.gui; fb.px = tg.px;
            list.Add(fb);
        }

        // A blob stands in for a body: Blob() draws it at about 2.3 times the size
        // it is given, so half the measured body height keeps a distant man the same
        // size as the silhouette he would have had.
        static float BlobSize(float px)
        {
            float s = px * 0.42f;
            return s < 3f ? 3f : (s > 60f ? 60f : s);
        }

        /// <summary>
        /// Pixels per world unit at one unit of distance: size_px = size_units *
        /// this / distance. Follows the camera's field of view, so the gunner
        /// optic's 32/20 degree ZOOM counts - the missing half of the old size
        /// guess, which assumed the game's 60 degrees and metres for units.
        /// </summary>
        static float PixelsPerUnit(Camera cam)
        {
            float fov = cam.fieldOfView;
            if (fov < 1f || fov > 179f) fov = 60f;
            float tan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            if (tan < 0.0001f) tan = 0.0001f;
            return Screen.height / (2f * tan);
        }

        /// <summary>
        /// The on-screen size of the k-th largest target, i.e. the cut above which a
        /// target is filled. No sort, no allocation: k is at most 8, so a small
        /// ascending scratch array of the biggest ones seen so far is enough.
        /// </summary>
        static float FillThreshold(List<Target> list, int k)
        {
            if (k <= 0) return float.MaxValue;
            if (k > _topPx.Length) k = _topPx.Length;
            if (list.Count <= k) return 0f;
            for (int i = 0; i < k; i++) _topPx[i] = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                float v = list[i].px;
                if (v <= _topPx[0]) continue;
                int j = 0;
                while (j + 1 < k && _topPx[j + 1] < v) { _topPx[j] = _topPx[j + 1]; j++; }
                _topPx[j] = v;
            }
            return _topPx[0];
        }

        /// <summary>
        /// How detailed a person's mesh needs to be for the size he is on screen.
        /// A character LODGroup already carries properly decimated meshes; using the
        /// one that fits costs a fraction of the triangles and looks the same,
        /// which is what keeps a settlement fight smooth. Without a LODGroup this
        /// is simply ignored (CollectLodRenderers then has nothing to choose from).
        /// </summary>
        static int PersonLod(float px)
        {
            if (px >= 220f) return 0;
            if (px >= 110f) return 1;
            if (px >= 45f) return 2;
            return 3;
        }

        /// <summary>
        /// The cached silhouette of one target, built on demand. A person is rebaked
        /// about three times a second (his pose moves) or when his detail level
        /// changes; a vehicle's parts follow their own live transforms, so it is only
        /// rebuilt every couple of seconds to pick up a part that was shot off.
        /// At most MaxBuildsPerFrame targets are built in any one frame - a target
        /// still waiting draws as a blob for a frame or two instead of hitching.
        /// </summary>
        static Silh TargetSilh(Transform t, bool person, int level)
        {
            if (t == null) return null;
            int id = t.GetInstanceID();
            float now = Time.time;
            SilhEntry e;
            if (_silCache.TryGetValue(id, out e) && e.tr == t)
            {
                e.used = now;
                float span = person ? PersonRebuild : VehicleRebuild;
                bool stale = now - e.built >= span || e.level != level;
                if (!stale || _builtThisFrame >= MaxBuildsPerFrame) return e.s;
            }
            else
            {
                if (_builtThisFrame >= MaxBuildsPerFrame) return null;
                e = new SilhEntry();
                e.tr = t;
                _silCache[id] = e;
            }
            // The outgoing silhouette is handed to the person build so its baked
            // vertex arrays can be written over instead of allocated again; it is
            // dropped here and now, so nothing else can still be holding it.
            Silh old = e.s;
            e.s = person ? BuildPerson(t, level, old) : BuildRigid(t);
            e.built = now;
            e.used = now;
            e.level = level;
            _builtThisFrame++;
            return e.s;
        }

        /// <summary>Drop silhouettes of targets nobody has looked at for a while -
        /// a dead NPC, a wreck that despawned, a vehicle left behind.</summary>
        static void PurgeSilhouettes()
        {
            float now = Time.time;
            if (now < _nextSilPurge) return;
            _nextSilPurge = now + 5f;
            _vehRadius.Clear();     // radii are cheap to remeasure and may change
            // The two mesh caches are keyed by instance id and hold on to arrays for
            // meshes that may long be destroyed - a slow leak, and an id that Unity
            // hands out again would serve the wrong geometry. Neither is worth a
            // per-entry liveness walk, so they are simply dropped once they grow past
            // anything a scene needs; the rebuild is spread over MaxBuildsPerFrame.
            if (_meshCache.Count > 1024) _meshCache.Clear();
            if (_bakedTris.Count > 1024) _bakedTris.Clear();
            if (_silCache.Count == 0) return;
            _silPurge.Clear();
            foreach (KeyValuePair<int, SilhEntry> kv in _silCache)
                if (kv.Value == null || kv.Value.tr == null || now - kv.Value.used > 10f)
                    _silPurge.Add(kv.Key);
            for (int i = 0; i < _silPurge.Count; i++) _silCache.Remove(_silPurge[i]);
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
        static int EmitMesh(Vector3[] verts, int[] tris, Matrix4x4 mvp, Vector2 centre, float pxR, float reach, int budget)
        {
            if (verts == null || tris == null || verts.Length == 0) return 0;
            float invR = pxR > 1f ? 1f / pxR : 1f;
            float w = Screen.width, h = Screen.height;
            // THE STRAY-GEOMETRY GUARD, and the reason the field is not littered with
            // drifting shapes: a triangle that belongs to this target must LAND ON
            // this target. `reach` is how far the target's own pixels can possibly
            // get from its projected centre (see Target.span), so every triangle has
            // to fit inside that box - which bounds its size and its position in one
            // test, and is NaN-safe (a NaN fails both comparisons).
            //
            // It catches, without having to know which one it is:
            //   - a triangle STRADDLING the near plane. ProjV drops vertices behind
            //     the camera, but a vertex just in front of it has a near-zero w and
            //     projects to enormous coordinates; coloured at its far-from-centre
            //     rim (the ramp's yellow) one such triangle floods the whole picture
            //     - the old "vehicle destroyed -> everything yellow" report;
            //   - geometry placed by a wrong matrix or a stale cached index list,
            //     which lands at some unrelated point of the map and paints a small
            //     weird shape wherever it happens to project;
            //   - a part whose bounds box turned out far larger than the vehicle.
            // Anything genuinely belonging to the target passes: `reach` is measured
            // from the target's own unclamped on-screen size with a wide margin.
            float lox = centre.x - reach, hix = centre.x + reach;
            float loy = centre.y - reach, hiy = centre.y + reach;
            int drawn = 0;
            int vn = verts.Length;
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                if (drawn >= budget) break;
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                // An index list is cached per mesh and a mesh can be swapped or
                // destroyed under us; an index that no longer fits its vertex array
                // would either throw (killing the whole optic for the frame) or draw
                // a triangle out of unrelated vertices. Skip it instead.
                if (a < 0 || b < 0 || c < 0 || a >= vn || b >= vn || c >= vn) continue;
                float ax, ay, bx, by, cx, cy;
                if (!ProjV(mvp, verts[a], w, h, out ax, out ay)) continue;
                if (!ProjV(mvp, verts[b], w, h, out bx, out by)) continue;
                if (!ProjV(mvp, verts[c], w, h, out cx, out cy)) continue;

                if (!(ax >= lox) || !(ax <= hix) || !(ay >= loy) || !(ay <= hiy)) continue;
                if (!(bx >= lox) || !(bx <= hix) || !(by >= loy) || !(by <= hiy)) continue;
                if (!(cx >= lox) || !(cx <= hix) || !(cy >= loy) || !(cy <= hiy)) continue;

                Emit(ax, ay, centre, invR);
                Emit(bx, by, centre, invR);
                Emit(cx, cy, centre, invR);
                drawn++;
                // Keep the open immediate-mode block small (see GLBlockTris).
                if (++_glBlock >= GLBlockTris) { _glBlock = 0; GL.End(); GL.Begin(GL.TRIANGLES); }
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
            // flat coin. STANDING, not round: a man is roughly twice as tall as he
            // is wide, and at the range where the blob replaces the mesh that
            // proportion is the only thing left that says "man" rather than "mark".
            float hw = s * 0.62f, hh = s * 1.20f;
            if (thermal)
            {
                Glow(new Rect(c.x - hw * 2.2f, c.y - hh * 1.5f, hw * 4.4f, hh * 3.0f),
                     new Color(1.00f, 0.50f, 0.08f, 0.30f));  // small hot bloom
                RampRect(c, hw, hh, 0.98f);                   // ironbow body, red core
            }
            else
            {
                SoftRect(c, hw * 2.0f, hh * 1.4f, new Color(NightBloom.r, NightBloom.g, NightBloom.b, 0.42f));
                HotRect (c, hw, hh, new Color(NightBody.r, NightBody.g, NightBody.b, 0.98f));
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

        /// <summary>
        /// Rebuild the list of warm targets a few times a second. This is now a
        /// CHEAP pass: it finds the transforms in range and nothing else. Building
        /// their silhouettes here - for every NPC on the map, whether or not it was
        /// ever drawn - is what made the optic stutter; that happens on demand in
        /// DrawSilhouettes instead, only for what is actually filled.
        /// </summary>
        static void RefreshTargets(Camera cam)
        {
            if (Time.time < _warmUntil) return;
            _warmUntil = Time.time + 0.35f;
            VehicleModules.Sweep();
            PurgeSilhouettes();

            _warm.Clear();
            _veh.Clear();
            _vehR.Clear();
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
                _vehR.Add(VehicleRadius(c.transform));
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
            // A mesh whose CPU copy was stripped at import (isReadable == false -
            // the shipped game vehicle meshes) throws or returns nothing here.
            // Detect it up front so we neither spam the Unity error log nor cache a
            // half mesh; the caller then draws that part as an oriented box built
            // from mesh.bounds (which stays available even when the mesh is not
            // readable), so it still reads as a real silhouette, not an oval.
            try
            {
                if (!mesh.isReadable) d = null;
                else { d.v = mesh.vertices; d.t = mesh.triangles; }
            }
            catch { d = null; }
            _meshCache[id] = d;
            return d;
        }

        // The 12 triangles (36 indices) of a box over corners 0..7. Cull is Off in
        // GLMat, so winding is irrelevant - every face fills. Static: a box part
        // reuses this one index array, so only the 8 corner verts allocate.
        static readonly int[] _boxTris = {
            0,1,2, 0,2,3,   // -z face
            5,4,7, 5,7,6,   // +z face
            4,0,3, 4,3,7,   // -x face
            1,5,6, 1,6,2,   // +x face
            3,2,6, 3,6,7,   // +y face
            4,5,1, 4,1,0,   // -y face
        };

        // Fill lodManaged with every renderer a LODGroup under root controls, and
        // lodSkip with all of them EXCEPT the finest non-empty LOD. The caller then
        // draws each LOD group exactly once, at its most detailed level, whatever
        // the LODGroup currently has enabled for the camera. Never throws.
        static void CollectLodRenderers(Transform root, HashSet<Renderer> lodManaged, HashSet<Renderer> lodSkip)
        {
            try
            {
                LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
                for (int gi = 0; gi < groups.Length; gi++)
                {
                    if (groups[gi] == null) continue;
                    LOD[] lods = groups[gi].GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    int keep = -1;
                    for (int li = 0; li < lods.Length; li++)
                        if (lods[li].renderers != null && lods[li].renderers.Length > 0) { keep = li; break; }
                    for (int li = 0; li < lods.Length; li++)
                    {
                        Renderer[] rs = lods[li].renderers;
                        if (rs == null) continue;
                        for (int ri = 0; ri < rs.Length; ri++)
                        {
                            Renderer r = rs[ri];
                            if (r == null) continue;
                            lodManaged.Add(r);
                            if (li != keep) lodSkip.Add(r);
                        }
                    }
                }
            }
            catch { }
        }

        // Add one vehicle MeshFilter to the silhouette. A readable mesh (the custom
        // T-72 hull/turret and the built tracks/wheels) fills in its true triangles.
        // A mesh the CPU cannot read (a shipped game vehicle, isReadable == false)
        // becomes an ORIENTED box from its local bounds, placed by the part's own
        // transform, so hull, turret and each wheel still read as a heat silhouette
        // that turns with the vehicle - never the formless ramp oval.
        static void AddVehiclePart(Silh s, MeshFilter mf)
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) return;
            MeshData d = mesh.isReadable ? Cache(mesh) : null;
            if (d != null && d.v != null && d.t != null && d.v.Length > 0 && d.t.Length > 0)
            {
                RigidPart rp = new RigidPart();
                rp.v = d.v; rp.t = d.t; rp.tr = mf.transform;
                s.rigid.Add(rp);
                return;
            }
            AddBoxPart(s, mf.transform, mesh.bounds);
        }

        // Build an oriented box (8 corners + the shared _boxTris) from a mesh's local
        // bounds and add it as a RigidPart. Placed by the part transform, so it sits
        // and rotates exactly where the real part is. mesh.bounds stays valid even
        // when mesh.isReadable is false, which is the whole point of this fallback.
        static void AddBoxPart(Silh s, Transform tr, Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            if (e.x <= 0f && e.y <= 0f && e.z <= 0f) return;
            Vector3[] v = new Vector3[8];
            v[0] = new Vector3(c.x - e.x, c.y - e.y, c.z - e.z);
            v[1] = new Vector3(c.x + e.x, c.y - e.y, c.z - e.z);
            v[2] = new Vector3(c.x + e.x, c.y + e.y, c.z - e.z);
            v[3] = new Vector3(c.x - e.x, c.y + e.y, c.z - e.z);
            v[4] = new Vector3(c.x - e.x, c.y - e.y, c.z + e.z);
            v[5] = new Vector3(c.x + e.x, c.y - e.y, c.z + e.z);
            v[6] = new Vector3(c.x + e.x, c.y + e.y, c.z + e.z);
            v[7] = new Vector3(c.x - e.x, c.y + e.y, c.z + e.z);
            RigidPart rp = new RigidPart();
            rp.v = v; rp.t = _boxTris; rp.tr = tr;
            s.rigid.Add(rp);
        }

        // A rigid target (vehicle): each child MeshFilter becomes a RigidPart with
        // the shared mesh (cached) and its live transform, so the hull/turret/tracks
        // are filled in their real shape and follow the vehicle every frame.
        static Silh BuildRigid(Transform root)
        {
            Silh s = new Silh();
            HashSet<Renderer> lodManaged = new HashSet<Renderer>();
            HashSet<Renderer> lodSkip = new HashSet<Renderer>();
            try
            {
                // A vehicle carries the SAME hull/turret four times, LOD0..LOD3
                // under a LODGroup that enables only one at a time. Draw exactly the
                // finest LOD ourselves - independent of which one the LODGroup has
                // enabled for the current camera distance - so the vehicle is drawn
                // once (not four overlapping copies) and is never dropped because the
                // active LOD happened to be a coarse one. Renderers a LODGroup does
                // NOT manage (wheels, glass) are always considered.
                CollectLodRenderers(root, lodManaged, lodSkip);

                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                for (int i = 0; i < mfs.Length; i++)
                {
                    MeshFilter mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    Renderer r = mf.GetComponent<Renderer>();
                    if (r == null) continue;
                    if (lodManaged.Contains(r))
                    {
                        // A LOD renderer: keep only the finest LOD, and IGNORE the
                        // LODGroup's per-camera .enabled toggling (we chose the LOD).
                        if (lodSkip.Contains(r)) continue;
                        if (!r.gameObject.activeInHierarchy) continue;
                    }
                    else
                    {
                        // A normal renderer (wheel, glass): a shot-off part is
                        // disabled, so honour .enabled here.
                        if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                    }
                    AddVehiclePart(s, mf);
                }
            }
            catch { }
            // Skinned vehicle parts are not exposed through MeshFilters.
            // Skin into the vehicle root's local space at the refresh tick. Using
            // bone matrices explicitly avoids BakeMesh scale differences between
            // Unity versions; the live root transform still follows vehicle motion.
            SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skins.Length; i++)
            {
                SkinnedMeshRenderer skin = skins[i];
                if (skin == null || skin.sharedMesh == null || !skin.gameObject.activeInHierarchy) continue;
                if (lodManaged.Contains(skin) ? lodSkip.Contains(skin) : !skin.enabled) continue;
                try
                {
                    Mesh mesh = skin.sharedMesh;
                    MeshData d = Cache(mesh);
                    if (d == null || d.v == null || d.t == null || d.v.Length == 0)
                    {
                        // localBounds is expressed in the space Unity animates this
                        // skin in - the ROOT BONE's, where there is one; the renderer
                        // transform is only the right frame when there is not. Taking
                        // the wrong one puts a vehicle-sized box wherever that
                        // transform happens to sit, which is a stray shape, not a
                        // silhouette.
                        Transform bs = skin.rootBone != null ? skin.rootBone : skin.transform;
                        AddBoxPart(s, bs, skin.localBounds);
                        continue;
                    }
                    Transform[] bones = skin.bones;
                    Matrix4x4[] bind = mesh.bindposes;
                    BoneWeight[] weights = mesh.boneWeights;
                    if (weights.Length != d.v.Length || bones.Length != bind.Length) continue;
                    Matrix4x4[] pose = new Matrix4x4[bones.Length];
                    Matrix4x4 worldToRoot = root.worldToLocalMatrix;
                    bool valid = bones.Length > 0;
                    for (int b = 0; b < bones.Length; b++)
                    {
                        if (bones[b] == null) { valid = false; break; }
                        pose[b] = worldToRoot * bones[b].localToWorldMatrix * bind[b];
                    }
                    if (!valid) continue;
                    Vector3[] verts = new Vector3[d.v.Length];
                    for (int v = 0; v < verts.Length; v++)
                        verts[v] = SkinVertex(d.v[v], weights[v], pose);
                    RigidPart part = new RigidPart();
                    part.v = verts; part.t = d.t; part.tr = root;
                    s.rigid.Add(part);
                }
                catch { } // One unreadable renderer must not hide the other parts.
            }
            return s;
        }

        static Vector3 SkinVertex(Vector3 vertex, BoneWeight weight, Matrix4x4[] pose)
        {
            Vector3 result = Vector3.zero;
            if (weight.weight0 > 0f) result += pose[weight.boneIndex0].MultiplyPoint3x4(vertex) * weight.weight0;
            if (weight.weight1 > 0f) result += pose[weight.boneIndex1].MultiplyPoint3x4(vertex) * weight.weight1;
            if (weight.weight2 > 0f) result += pose[weight.boneIndex2].MultiplyPoint3x4(vertex) * weight.weight2;
            if (weight.weight3 > 0f) result += pose[weight.boneIndex3].MultiplyPoint3x4(vertex) * weight.weight3;
            return result;
        }

        // Like CollectLodRenderers, but for a PERSON: keep the LOD that MATCHES how
        // big he is on screen (PersonLod) instead of always the finest one. A
        // character LODGroup already ships properly decimated meshes, so a man who
        // is 50 px tall costs a fraction of the triangles of the same man filling
        // the screen and reads identically once he is a heat silhouette - that is
        // what keeps a settlement fight smooth. `level` is a wish, not a demand: a
        // group without renderers at that level uses the finest one that has any.
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
                }
            }
            catch { }
        }

        // The world-vertex array of the SAME part of the previous silhouette, when
        // it has the same length. Every element is overwritten by the bake below,
        // and the old silhouette is dropped by the caller the moment the new one
        // replaces it, so the array simply changes hands.
        static Vector3[] Recycle(Silh reuse, int part, int length)
        {
            if (reuse != null && part < reuse.skinned.Count)
            {
                BakedPart old = reuse.skinned[part];
                if (old != null && old.wv != null && old.wv.Length == length) return old.wv;
            }
            return new Vector3[length];
        }

        // Triangles of a baked snapshot, cached by the SOURCE mesh. BakeMesh keeps
        // the source topology, so the index list is the same at every bake, while
        // Mesh.triangles hands out a fresh copy on every single read.
        static int[] BakedTris(Mesh source, Mesh baked)
        {
            if (baked == null) return null;
            if (source == null) return baked.triangles;
            int id = source.GetInstanceID();
            int[] t;
            if (_bakedTris.TryGetValue(id, out t)) return t;
            t = baked.triangles;
            _bakedTris[id] = t;
            return t;
        }

        // A person: bake each SkinnedMeshRenderer to a world-space snapshot at the
        // refresh tick (baking every frame is too costly), plus any rigid child
        // meshes (helmet, weapon). The baked pose lags at most one refresh - fine
        // for a heat silhouette.
        //
        // Three things keep this off the frame time, because it is the one build
        // that repeats while a man walks: only the LOD that matches his on-screen
        // size is baked, the index list is cached per source mesh instead of being
        // copied out of the snapshot every time, and the world-vertex arrays of the
        // previous silhouette are written over. A character mesh is a few hundred
        // kilobytes; re-baking the filled men three times a second used to hand the
        // collector megabytes a second, and that garbage is the last of the stutter.
        static Silh BuildPerson(Transform root, int level, Silh reuse)
        {
            Silh s = new Silh();
            // The pose below is baked into WORLD space, so the draw has to undo the
            // root's world placement before it re-applies the live one. Captured
            // here, at the moment of the bake, and never anywhere else: without it
            // every baked vertex is run through the man's localToWorld twice and
            // his silhouette is drawn at a point that has nothing to do with him.
            s.bakeInv = root.worldToLocalMatrix;
            HashSet<Renderer> managed = new HashSet<Renderer>();
            HashSet<Renderer> skip = new HashSet<Renderer>();
            CollectPersonLods(root, level, managed, skip);
            try
            {
                SkinnedMeshRenderer[] sk = root.GetComponentsInChildren<SkinnedMeshRenderer>();
                for (int i = 0; i < sk.Length; i++)
                {
                    SkinnedMeshRenderer smr = sk[i];
                    if (smr == null || smr.sharedMesh == null) continue;
                    // A switched-off GameObject is never drawn by the game and must
                    // not be drawn here either: a character carries spare heads,
                    // hats and weapon variants that are deactivated rather than
                    // removed, and a deactivated one keeps whatever transform it was
                    // left with - stray geometry sitting beside the man.
                    if (!smr.gameObject.activeInHierarchy) continue;
                    if (managed.Contains(smr))
                    {
                        // WE pick the level, so the LODGroup's own per-camera
                        // .enabled toggling is ignored here: otherwise the level
                        // asked for is exactly the one the group just switched off.
                        if (skip.Contains(smr)) continue;
                    }
                    else if (!smr.enabled) continue;
                    if (_bakeScratch == null) _bakeScratch = new Mesh();
                    _bakeScratch.hideFlags = HideFlags.HideAndDontSave;
                    smr.BakeMesh(_bakeScratch);
                    Vector3[] lv = _bakeScratch.vertices;
                    if (lv == null || lv.Length == 0) continue;
                    // BakeMesh produces a readable snapshot even if the source mesh
                    // has no CPU copy, so the indices come from the snapshot too -
                    // once per mesh, not once per bake.
                    int[] tris = BakedTris(smr.sharedMesh, _bakeScratch);
                    if (tris == null || tris.Length == 0) continue;
                    // BakeMesh yields verts in the renderer transform's local space;
                    // take them to world with its full localToWorld (scale 1 for
                    // these characters, so no double-scale).
                    Matrix4x4 mtx = smr.transform.localToWorldMatrix;
                    Vector3[] wv = Recycle(reuse, s.skinned.Count, lv.Length);
                    for (int k = 0; k < lv.Length; k++) wv[k] = mtx.MultiplyPoint3x4(lv[k]);
                    BakedPart bp = new BakedPart();
                    bp.wv = wv; bp.t = tris;
                    s.skinned.Add(bp);
                }
                MeshFilter[] mfs = root.GetComponentsInChildren<MeshFilter>();
                for (int i = 0; i < mfs.Length; i++)
                {
                    MeshFilter mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!mf.gameObject.activeInHierarchy) continue;   // see above
                    Renderer r = mf.GetComponent<Renderer>();
                    if (r != null)
                    {
                        if (managed.Contains(r)) { if (skip.Contains(r)) continue; }
                        else if (!r.enabled) continue;
                    }
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
        /// used to size its heat glow on screen. Clamped and never throwing.
        /// MEASURED ONCE per vehicle and cached: walking every child renderer of
        /// every vehicle in range, three times a second, is exactly the kind of
        /// invisible cost this file is otherwise careful about. PurgeSilhouettes
        /// drops the cache every 5 s, so a vehicle that loses a part remeasures.
        /// </summary>
        static float VehicleRadius(Transform t)
        {
            int id = t.GetInstanceID();
            float cached;
            if (_vehRadius.TryGetValue(id, out cached)) return cached;
            float r = MeasureRadius(t);
            _vehRadius[id] = r;
            return r;
        }

        static float MeasureRadius(Transform t)
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

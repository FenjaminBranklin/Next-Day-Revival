// Next Day: Survival - Revival Toolkit
// The gunner periscope: draws the modern, wide-field optic that replaces the
// old round scope, plus the thermal and night-vision overlays unlocked by the
// installed modules (Revival.Modules.cs).
//
// Rendering is pure IMGUI (this is called from Turret.DrawScope, inside OnGUI):
//   - THERMAL crushes the whole picture to a flat, cold, low-contrast field: a
//     NEAR-OPAQUE deep-indigo quad kills the "normal view + shadows" read, a
//     radial vignette darkens the optic edges, and a faint sensor grain sells
//     the detector. Nothing in the cold scene carries usable detail - exactly
//     as a real thermal image where nothing is radiating heat. NIGHT keeps the
//     softer green light-gain look;
//   - warm things then BLAZE on top of that cold field. Because we cannot give
//     every world object a per-pixel heat value (no camera post-process on this
//     old Unity + BepInEx build), the things we DO know are warm - patrol/convoy
//     vehicles and their crew, and players - are drawn through an ironbow ramp:
//     a wide deep-red halo, an orange body, and a white-hot core, so a target
//     reads as genuine radiated heat, not a marker;
//   - the frame, reticle and status text are drawn on top.
// A true per-pixel camera post-effect (scene desaturation + heat ramp) would be
// nicer still and is a possible later enhancement; it needs a runtime shader,
// which this build cannot load reliably, so this version stays robust and
// asset-free while reading far more like real thermal than a plain blue tint.
//
// ASCII-only code and comments; on-screen text is bilingual through Loc.T.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class GunnerOptics
    {
        static Texture2D _px;
        static Texture2D _disc;
        static Texture2D _vig;    // radial vignette: clear centre, dark rim
        static Texture2D _grain;  // static sensor noise, tiled

        // Warm-target cache. FindObjectsOfType per frame is exactly the cost
        // that never shows in a log and always shows in the frame time, so the
        // list is rebuilt a few times a second and only projected each frame.
        static readonly List<Transform> _warm = new List<Transform>();   // people: crew + players
        static readonly List<Transform> _veh = new List<Transform>();     // vehicles
        static readonly List<float> _vehR = new List<float>();            // vehicle world radius (m)
        static float _warmUntil;
        static Type _npcType, _playerType, _vehType;
        static bool _typesResolved;

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
                // Crush the picture to a flat, cold, low-contrast field. This is
                // the whole point of the rework: a NEAR-OPAQUE cold quad removes
                // the "normal view + shadows" read (a plain 0.66 tint left the lit
                // scene showing through), so cold objects carry almost no detail.
                Fill(full, new Color(0.03f, 0.045f, 0.11f, 0.88f));   // deep cold indigo
                Vignette(full, new Color(0.00f, 0.00f, 0.015f, 1f));  // edges go colder/black
                ThermalGrain(full, new Color(0.55f, 0.62f, 0.85f, 0.05f)); // faint detector noise
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

            // Vehicles first: an ironbow heat shape sized to the machine itself -
            // no marker, no rectangle. People draw brighter over it.
            for (int i = 0; i < _veh.Count; i++)
            {
                Transform t = _veh[i];
                if (t == null) continue;
                Vector3 mid = t.position + new Vector3(0f, 1.0f, 0f);
                float dist; Vector2 g;
                if (!Project(cam, mid, out g, out dist)) continue;
                if (dist > 800f) continue;

                // On-screen size from the vehicle's world radius: project a point
                // one radius to the side and measure the pixel gap, so the glow
                // tracks the vehicle's apparent size at any distance.
                float r = i < _vehR.Count ? _vehR[i] : 3f;
                float edgeDist; Vector2 ge;
                float px = 40f;
                if (Project(cam, mid + cam.transform.right * r, out ge, out edgeDist))
                    px = Mathf.Abs(ge.x - g.x);
                px = Mathf.Clamp(px, 16f, 320f);
                VehicleGlow(g, px, thermal);
            }

            // People: crew and players. They radiate hottest, so they blaze the
            // brightest - a white-hot core against the cold field.
            for (int i = 0; i < _warm.Count; i++)
            {
                Transform t = _warm[i];
                if (t == null) continue;
                Vector3 chest = t.position + new Vector3(0f, 1.0f, 0f);
                float dist; Vector2 g;
                if (!Project(cam, chest, out g, out dist)) continue;
                if (dist > 450f) continue;

                float size = Mathf.Clamp(1500f / dist, 8f, 46f);
                Blob(g, size, thermal);
            }
        }

        // Ironbow ramp, coolest to hottest. Warm targets are stacked discs in
        // these colours: a wide deep-red halo, an orange body, then a near-white
        // core - the classic thermal look, unmistakable against the cold field.
        // Night falls back to a green ramp.
        static readonly Color IronHalo = new Color(0.85f, 0.06f, 0.14f, 1f);  // deep red
        static readonly Color IronBody = new Color(1.00f, 0.42f, 0.05f, 1f);  // orange
        static readonly Color IronCore = new Color(1.00f, 0.96f, 0.80f, 1f);  // white-hot
        static readonly Color NightHalo = new Color(0.10f, 0.75f, 0.20f, 1f);
        static readonly Color NightBody = new Color(0.35f, 1.00f, 0.40f, 1f);
        static readonly Color NightCore = new Color(0.85f, 1.00f, 0.85f, 1f);

        static void GlowRect(Vector2 c, float halfW, float halfH, Color col)
        {
            Glow(new Rect(c.x - halfW, c.y - halfH, halfW * 2f, halfH * 2f), col);
        }

        static void VehicleGlow(Vector2 c, float px, bool thermal)
        {
            // Wider than tall - a vehicle silhouette reads horizontal. Three
            // ironbow layers give a hot centre (engine/crew) bleeding out to a
            // cooler red hull edge.
            Color halo = thermal ? IronHalo : NightHalo;
            Color body = thermal ? IronBody : NightBody;
            Color core = thermal ? IronCore : NightCore;
            float w = px * 1.5f, h = px * 0.95f;
            GlowRect(c, w * 1.15f, h * 1.15f, new Color(halo.r, halo.g, halo.b, 0.50f));
            GlowRect(c, w,         h,         new Color(body.r, body.g, body.b, 0.72f));
            GlowRect(c, w * 0.5f,  h * 0.5f,  new Color(core.r, core.g, core.b, 0.90f));
        }

        static void Blob(Vector2 c, float s, bool thermal)
        {
            // A person: the same ironbow ramp, tighter and brighter, so crew and
            // players are the hottest thing on screen. Round discs only, never a
            // square.
            Color halo = thermal ? IronHalo : NightHalo;
            Color body = thermal ? IronBody : NightBody;
            Color core = thermal ? IronCore : NightCore;
            GlowRect(c, s * 1.9f, s * 1.9f, new Color(halo.r, halo.g, halo.b, 0.50f));
            GlowRect(c, s * 1.0f, s * 1.0f, new Color(body.r, body.g, body.b, 0.80f));
            GlowRect(c, s * 0.5f, s * 0.5f, new Color(core.r, core.g, core.b, 0.98f));
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
            ResolveTypes();
            AddAll(_npcType);
            AddAll(_playerType);
            AddVehicles(_vehType);
        }

        static void AddAll(Type t)
        {
            if (t == null) return;
            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < objs.Length; i++)
            {
                Component c = objs[i] as Component;
                if (c != null && c.transform != null) _warm.Add(c.transform);
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
            }
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

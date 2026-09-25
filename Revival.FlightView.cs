// Next Day: Survival - Revival Toolkit
//
// FLIGHT VIEW PROFILE. Only in an aircraft: from about 50 m over the ground
// the render settings blend towards a profile made for looking DOWN at a
// landscape instead of along a street, and back again on the way down.
//
// Why: the game's settings are tuned for a man on foot. From 200 m up, the
// grass is out of its own detail range anyway, the shadows are spent on the
// ground right under the fuselage, the terrain is drawn with the basemap
// (blurry) almost everywhere, and the trees stop at a distance that is a few
// hundred metres from the camera - which from the air is a visible edge. The
// profile moves that budget: grass off, shadows shorter, a coarser terrain
// mesh, trees to billboards earlier but drawn much further, a longer far clip
// and thinner fog.
//
// WHAT IS CHANGED (at full blend; every value is a config key, see BindConfig):
//   terrain detailObjectDistance   -> 0              (grass and detail off)
//   terrain treeBillboardDistance  x BillboardScale (billboards earlier)
//   terrain treeDistance           x TreeDistanceScale
//   terrain heightmapPixelError    x PixelErrorScale
//   terrain basemapDistance        x BasemapScale
//   QualitySettings.shadowDistance x ShadowScale
//   QualitySettings.lodBias        x LodBiasScale (1 = unchanged, the default)
//   camera farClipPlane            x FarClipScale
//   RenderSettings fog             density x FogScale, start/end / FogScale
//
// THE BASELINE IS WHATEVER THE GAME SET. Nothing here knows the player's
// settings; every value is captured the moment the blend leaves zero and
// restored EXACTLY when it gets back to zero. Other code writes some of these
// values too - the game's DynamicShadowSettings writes the shadow distance
// every Update, GameSettingsManager writes the terrain and the far clip when a
// setting is changed, EastWorld copies the terrain values onto the tile. So
// every field is checked each frame against what this file wrote last: if it
// differs, somebody else wrote it, and that value becomes the new baseline
// before the profile is applied on top. Applied in LateUpdate, after all of
// them.
//
// NO POPPING AT LANDING. The blend is a smoothstep of the height between
// StartHeight and FullHeight, and it moves at most 1/BlendSeconds per second.
// Below StartHeight it is zero, so a machine coming in to land has every value
// back at the player's own long before the gear touches.
//
// FOR THE AN-2 LATER: any aircraft calls FlightView.Report(heightMetres) once
// per frame while the local player is in it. No report in a frame means "not
// in an aircraft", and the profile blends back.
//
// MEASUREMENT ([FlightView] Measure, off by default): once a second a line
// with the height, the blend, the average and 99th-percentile frame time of
// that second and the values in force; MeasureKey (End) switches the profile
// off and on in flight, so the same spot can be measured with and without it
// within seconds.
//
// Seams: PlayerHeli.BindConfig (BindConfig), PlayerHeli.Tick (Report),
// PlayerHeli.LateFrame (LateTick). Nothing in RevivalPlugin.cs.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class FlightView
    {
        static ConfigEntry<bool> _cfgEnabled, _cfgMeasure;
        static ConfigEntry<string> _cfgMeasureKey;
        static ConfigEntry<float> _cfgStart, _cfgFull, _cfgBlendSeconds,
            _cfgDetail, _cfgBillboard, _cfgTreeDistance, _cfgPixelError,
            _cfgBasemap, _cfgShadow, _cfgLodBias, _cfgFarClip, _cfgFog;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("FlightView", "Enabled", true,
                "Height-driven view profile, only while you are in an aircraft "
                + "(the player helicopter now). From StartHeight over the ground "
                + "the settings below blend in, full at FullHeight, and back out "
                + "on the way down. On the ground nothing is changed.");
            _cfgStart = cfg.Bind("FlightView", "StartHeight", 50f,
                "Metres over the ground where the profile starts to blend in.");
            _cfgFull = cfg.Bind("FlightView", "FullHeight", 200f,
                "Metres over the ground where the profile is fully in force.");
            _cfgBlendSeconds = cfg.Bind("FlightView", "BlendSeconds", 3f,
                "The blend moves at most from zero to full in this many seconds, "
                + "so a sudden height change (a ridge, a valley) does not snap "
                + "the view.");
            _cfgDetail = cfg.Bind("FlightView", "DetailScale", 0f,
                "Grass and detail distance at full blend, times the game's own. "
                + "0 = grass off.");
            _cfgBillboard = cfg.Bind("FlightView", "BillboardScale", 0.4f,
                "Distance at which trees become billboards, times the game's own. "
                + "Below 1 = billboards earlier, which pays for the longer tree "
                + "distance.");
            _cfgTreeDistance = cfg.Bind("FlightView", "TreeDistanceScale", 2.5f,
                "Distance up to which trees are drawn at all, times the game's own. "
                + "From the air the tree edge is what gives the height away.");
            _cfgPixelError = cfg.Bind("FlightView", "PixelErrorScale", 2.5f,
                "Terrain mesh pixel error, times the game's own (capped at 200). "
                + "Higher = coarser mesh, fewer triangles.");
            _cfgBasemap = cfg.Bind("FlightView", "BasemapScale", 2f,
                "Distance up to which the terrain is drawn with its full splat "
                + "textures instead of the low-resolution basemap, times the "
                + "game's own.");
            _cfgShadow = cfg.Bind("FlightView", "ShadowScale", 0.4f,
                "Shadow distance, times the game's own. From the air the shadow "
                + "cascades are spent on the ground under the fuselage.");
            _cfgLodBias = cfg.Bind("FlightView", "LodBiasScale", 1f,
                "LOD bias, times the game's own. 1 = unchanged (the default): "
                + "raise it only if the frame time at 200 m has room for it.");
            _cfgFarClip = cfg.Bind("FlightView", "FarClipScale", 1.6f,
                "Camera far clip plane, times the game's own (capped at 12000 "
                + "units).");
            _cfgFog = cfg.Bind("FlightView", "FogScale", 0.6f,
                "Fog at full blend: exponential density times this, linear start "
                + "and end divided by it. Below 1 = thinner fog.");
            _cfgMeasure = cfg.Bind("FlightView", "Measure", false,
                "Research: once a second one log line with height, blend, frame "
                + "time (average and 99th percentile) and the values in force.");
            _cfgMeasureKey = cfg.Bind("FlightView", "MeasureKey", "End",
                "Research, only with Measure on: switches the profile off and on "
                + "in flight, for a with/without comparison at the same spot.");
        }

        static bool Enabled { get { return _cfgEnabled == null || _cfgEnabled.Value; } }

        static float V(ConfigEntry<float> e, float fallback)
        {
            return e == null ? fallback : e.Value;
        }

        // ============================================================ report

        static float _reported = -1f;
        static int _reportedFrame = -1;

        /// <summary>The local player is in an aircraft this frame, this many
        /// metres over the ground. Call once per frame; the highest report of a
        /// frame wins. Not calling it is "not in an aircraft".</summary>
        internal static void Report(float heightMetres)
        {
            if (_reportedFrame != Time.frameCount)
            {
                _reportedFrame = Time.frameCount;
                _reported = heightMetres;
            }
            else if (heightMetres > _reported) _reported = heightMetres;
        }

        // ============================================================= state

        static float _blend;                 // 0..1, what is in force
        static bool _active;                 // baselines captured, values written
        static bool _abOff;                  // measure key: profile switched off

        // One value with its baseline and what this file wrote last. The write
        // is skipped when nothing moved, and a current value that differs from
        // the last write is somebody else's, which becomes the new baseline.
        sealed class Slot
        {
            public float Base, Written;
            public bool Has;

            public float Adopt(float current)
            {
                if (!Has) { Base = current; Written = current; Has = true; }
                else if (Mathf.Abs(current - Written) > Eps(Written)) Base = current;
                return Base;
            }

            static float Eps(float v) { return Mathf.Max(0.001f, Mathf.Abs(v) * 0.0005f); }
        }

        sealed class TerrainSlots
        {
            public Slot Detail = new Slot(), Billboard = new Slot(), Trees = new Slot(),
                Pixel = new Slot(), Basemap = new Slot();
        }

        static readonly Dictionary<Terrain, TerrainSlots> _terrains =
            new Dictionary<Terrain, TerrainSlots>();
        static readonly List<Terrain> _terrainList = new List<Terrain>();
        static readonly List<Terrain> _drop = new List<Terrain>();
        static float _nextScan;

        static readonly Slot _shadow = new Slot(), _lod = new Slot(), _far = new Slot(),
            _fogDensity = new Slot(), _fogStart = new Slot(), _fogEnd = new Slot();
        static Camera _cam;

        // ============================================================= frame

        /// <summary>LateUpdate, after the game's own writers. Seam:
        /// PlayerHeli.LateFrame.</summary>
        internal static void LateTick()
        {
            try
            {
                Keys();
                bool inAircraft = _reportedFrame == Time.frameCount
                                  || _reportedFrame == Time.frameCount - 1;
                float target = 0f;
                if (Enabled && inAircraft && !_abOff)
                {
                    float start = V(_cfgStart, 50f);
                    float full = Mathf.Max(start + 1f, V(_cfgFull, 200f));
                    float x = Mathf.Clamp01((_reported - start) / (full - start));
                    target = x * x * (3f - 2f * x);
                }
                float rate = 1f / Mathf.Max(0.1f, V(_cfgBlendSeconds, 3f));
                _blend = Mathf.MoveTowards(_blend, target, rate * Time.unscaledDeltaTime);

                Measure(inAircraft);

                if (_blend <= 0f)
                {
                    if (_active) Restore();
                    return;
                }
                _active = true;
                Apply(_blend);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("FlightView: " + ex.Message
                    + " - the profile is withdrawn.");
                try { Restore(); } catch { }
                _blend = 0f;
            }
        }

        static void Apply(float b)
        {
            if (Time.unscaledTime >= _nextScan || _terrainList.Count == 0) Scan();

            float detail = Mathf.Max(0f, V(_cfgDetail, 0f));
            float billboard = Mathf.Max(0.05f, V(_cfgBillboard, 0.4f));
            float trees = Mathf.Max(0.1f, V(_cfgTreeDistance, 2.5f));
            float pixel = Mathf.Max(0.1f, V(_cfgPixelError, 2.5f));
            float basemap = Mathf.Max(0.1f, V(_cfgBasemap, 2f));

            for (int i = 0; i < _terrainList.Count; i++)
            {
                Terrain t = _terrainList[i];
                if (t == null) continue;
                TerrainSlots s;
                if (!_terrains.TryGetValue(t, out s))
                {
                    s = new TerrainSlots();
                    Inherit(t, s);
                    _terrains[t] = s;
                }
                float v;
                v = Blend(s.Detail.Adopt(t.detailObjectDistance), detail, b, 0f, 100000f);
                if (Changed(s.Detail, v)) t.detailObjectDistance = v;
                v = Blend(s.Billboard.Adopt(t.treeBillboardDistance), billboard, b, 0f, 100000f);
                if (Changed(s.Billboard, v)) t.treeBillboardDistance = v;
                v = Blend(s.Trees.Adopt(t.treeDistance), trees, b, 0f, 20000f);
                if (Changed(s.Trees, v)) t.treeDistance = v;
                v = Blend(s.Pixel.Adopt(t.heightmapPixelError), pixel, b, 1f, 200f);
                if (Changed(s.Pixel, v)) t.heightmapPixelError = v;
                v = Blend(s.Basemap.Adopt(t.basemapDistance), basemap, b, 0f, 20000f);
                if (Changed(s.Basemap, v)) t.basemapDistance = v;
            }

            float val;
            val = Blend(_shadow.Adopt(QualitySettings.shadowDistance),
                        Mathf.Max(0.05f, V(_cfgShadow, 0.4f)), b, 0f, 100000f);
            if (Changed(_shadow, val)) QualitySettings.shadowDistance = val;

            val = Blend(_lod.Adopt(QualitySettings.lodBias),
                        Mathf.Max(0.1f, V(_cfgLodBias, 1f)), b, 0.01f, 10f);
            if (Changed(_lod, val)) QualitySettings.lodBias = val;

            Camera cam = CameraOwner.ViewCamera();
            if (!ReferenceEquals(cam, _cam))
            {
                // A different camera: the old one gets its own value back, the
                // new one is measured fresh.
                if (_cam != null && _far.Has) _cam.farClipPlane = _far.Base;
                _far.Has = false;
                _cam = cam;
            }
            if (_cam != null)
            {
                val = Blend(_far.Adopt(_cam.farClipPlane),
                            Mathf.Max(1f, V(_cfgFarClip, 1.6f)), b, 1f, 12000f);
                if (Changed(_far, val)) _cam.farClipPlane = val;
            }

            float fog = Mathf.Clamp(V(_cfgFog, 0.6f), 0.05f, 1f);
            val = Blend(_fogDensity.Adopt(RenderSettings.fogDensity), fog, b, 0f, 100f);
            if (Changed(_fogDensity, val)) RenderSettings.fogDensity = val;
            val = Blend(_fogStart.Adopt(RenderSettings.fogStartDistance), 1f / fog, b, -100000f, 100000f);
            if (Changed(_fogStart, val)) RenderSettings.fogStartDistance = val;
            val = Blend(_fogEnd.Adopt(RenderSettings.fogEndDistance), 1f / fog, b, -100000f, 100000f);
            if (Changed(_fogEnd, val)) RenderSettings.fogEndDistance = val;
        }

        /// <summary>The baseline times the scale, blended in by b. The cap only
        /// ever limits the scaled end, never the player's own value.</summary>
        static float Blend(float baseline, float scale, float b, float lo, float hi)
        {
            float full = baseline * scale;
            if (scale > 1f) full = Mathf.Min(full, Mathf.Max(hi, baseline));
            if (scale < 1f) full = Mathf.Max(full, Mathf.Min(lo, baseline));
            return Mathf.Lerp(baseline, full, b);
        }

        static bool Changed(Slot s, float v)
        {
            if (Mathf.Abs(v - s.Written) <= Mathf.Max(0.0005f, Mathf.Abs(v) * 0.0002f))
                return false;
            s.Written = v;
            return true;
        }

        /// <summary>A terrain met for the first time while the profile is in
        /// force - the east tile coming into range, say. EastWorld copies the
        /// grass and billboard distances of the main terrain onto the tile, and
        /// if that happened while we had them lowered, what the tile holds now
        /// is OUR value, not a baseline. Recognised by being exactly what we
        /// last wrote on a terrain we know; the baseline is then that
        /// terrain's.</summary>
        static void Inherit(Terrain t, TerrainSlots s)
        {
            foreach (KeyValuePair<Terrain, TerrainSlots> e in _terrains)
            {
                TerrainSlots o = e.Value;
                if (!o.Detail.Has || !o.Billboard.Has) continue;
                if (Mathf.Abs(t.detailObjectDistance - o.Detail.Written) > 0.01f) continue;
                if (Mathf.Abs(t.treeBillboardDistance - o.Billboard.Written) > 0.01f) continue;
                if (Mathf.Abs(o.Detail.Written - o.Detail.Base) < 0.01f
                    && Mathf.Abs(o.Billboard.Written - o.Billboard.Base) < 0.01f) continue;
                s.Detail.Base = o.Detail.Base; s.Detail.Written = o.Detail.Written; s.Detail.Has = true;
                s.Billboard.Base = o.Billboard.Base; s.Billboard.Written = o.Billboard.Written; s.Billboard.Has = true;
                RevivalPlugin.L.LogInfo("FlightView: " + t.name + " carries the profile's "
                    + "grass and billboard values (copied from " + e.Key.name
                    + ") - its baseline is taken from there.");
                return;
            }
        }

        static void Scan()
        {
            _nextScan = Time.unscaledTime + 0.5f;
            _terrainList.Clear();
            Terrain[] all = Terrain.activeTerrains;
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) _terrainList.Add(all[i]);
            _drop.Clear();
            foreach (KeyValuePair<Terrain, TerrainSlots> e in _terrains)
                if (e.Key == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _terrains.Remove(_drop[i]);
        }

        /// <summary>Everything back to the baseline, exactly, and forgotten.
        /// Only fields this file actually changed are written, and only if
        /// nobody else has written them since.</summary>
        static void Restore()
        {
            foreach (KeyValuePair<Terrain, TerrainSlots> e in _terrains)
            {
                Terrain t = e.Key;
                if (t == null) continue;
                TerrainSlots s = e.Value;
                if (Mine(s.Detail, t.detailObjectDistance)) t.detailObjectDistance = s.Detail.Base;
                if (Mine(s.Billboard, t.treeBillboardDistance)) t.treeBillboardDistance = s.Billboard.Base;
                if (Mine(s.Trees, t.treeDistance)) t.treeDistance = s.Trees.Base;
                if (Mine(s.Pixel, t.heightmapPixelError)) t.heightmapPixelError = s.Pixel.Base;
                if (Mine(s.Basemap, t.basemapDistance)) t.basemapDistance = s.Basemap.Base;
            }
            _terrains.Clear();
            _terrainList.Clear();
            if (Mine(_shadow, QualitySettings.shadowDistance)) QualitySettings.shadowDistance = _shadow.Base;
            if (Mine(_lod, QualitySettings.lodBias)) QualitySettings.lodBias = _lod.Base;
            if (_cam != null && Mine(_far, _cam.farClipPlane)) _cam.farClipPlane = _far.Base;
            if (Mine(_fogDensity, RenderSettings.fogDensity)) RenderSettings.fogDensity = _fogDensity.Base;
            if (Mine(_fogStart, RenderSettings.fogStartDistance)) RenderSettings.fogStartDistance = _fogStart.Base;
            if (Mine(_fogEnd, RenderSettings.fogEndDistance)) RenderSettings.fogEndDistance = _fogEnd.Base;
            _shadow.Has = _lod.Has = _far.Has = false;
            _fogDensity.Has = _fogStart.Has = _fogEnd.Has = false;
            _cam = null;
            _active = false;
        }

        static bool Mine(Slot s, float current)
        {
            return s.Has && Mathf.Abs(current - s.Written) <= Mathf.Max(0.001f, Mathf.Abs(s.Written) * 0.0005f);
        }

        // ========================================================= measuring

        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static readonly List<float> _frames = new List<float>();
        static float _nextLine;

        static void Keys()
        {
            if (_cfgMeasure == null || !_cfgMeasure.Value) return;
            if (!_keyParsed)
            {
                _keyParsed = true;
                try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgMeasureKey.Value, true); }
                catch { _key = KeyCode.None; }
            }
            if (_key != KeyCode.None && Input.GetKeyDown(_key))
            {
                _abOff = !_abOff;
                RevivalPlugin.L.LogInfo("FlightView: profile switched "
                    + (_abOff ? "OFF" : "ON") + " by the measure key at "
                    + _reported.ToString("0", CultureInfo.InvariantCulture) + " m.");
            }
        }

        static void Measure(bool inAircraft)
        {
            if (_cfgMeasure == null || !_cfgMeasure.Value || !inAircraft) { _frames.Clear(); return; }
            _frames.Add(Time.unscaledDeltaTime);
            if (Time.unscaledTime < _nextLine) return;
            _nextLine = Time.unscaledTime + 1f;
            if (_frames.Count < 2) { _frames.Clear(); return; }
            float sum = 0f;
            for (int i = 0; i < _frames.Count; i++) sum += _frames[i];
            _frames.Sort();
            float p99 = _frames[Mathf.Min(_frames.Count - 1, (int)(_frames.Count * 0.99f))];
            float avg = sum / _frames.Count;
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder("FlightView measure: height ");
            sb.Append(_reported.ToString("0", ci)).Append(" m, blend ").Append(_blend.ToString("0.00", ci))
              .Append(_abOff ? " (profile OFF)" : "")
              .Append(", frames ").Append(_frames.Count)
              .Append(", avg ").Append((avg * 1000f).ToString("0.0", ci)).Append(" ms")
              .Append(", p99 ").Append((p99 * 1000f).ToString("0.0", ci)).Append(" ms")
              .Append(" | shadow ").Append(QualitySettings.shadowDistance.ToString("0", ci))
              .Append(", lodBias ").Append(QualitySettings.lodBias.ToString("0.00", ci));
            Camera cam = CameraOwner.ViewCamera();
            if (cam != null) sb.Append(", far ").Append(cam.farClipPlane.ToString("0", ci));
            sb.Append(", fog ").Append(RenderSettings.fog ? RenderSettings.fogMode.ToString() : "off")
              .Append(" ").Append(RenderSettings.fogDensity.ToString("0.00000", ci))
              .Append("/").Append(RenderSettings.fogEndDistance.ToString("0", ci));
            Terrain t = Terrain.activeTerrain;
            if (t != null)
                sb.Append(" | ").Append(t.name).Append(" detail ").Append(t.detailObjectDistance.ToString("0", ci))
                  .Append(", billboard ").Append(t.treeBillboardDistance.ToString("0", ci))
                  .Append(", trees ").Append(t.treeDistance.ToString("0", ci))
                  .Append(", pixelError ").Append(t.heightmapPixelError.ToString("0.0", ci))
                  .Append(", basemap ").Append(t.basemapDistance.ToString("0", ci));
            RevivalPlugin.L.LogInfo(sb.ToString());
            _frames.Clear();
        }
    }
}

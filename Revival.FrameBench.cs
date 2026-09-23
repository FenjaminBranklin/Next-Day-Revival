// Next Day: Survival - Revival Toolkit
//
// FRAME-TIME BENCHMARK for the east extension. Research only, off by default:
// [Research] FrameBench = false.
//
// Why: the east extension (docs/ai/tasks/east-extension-feasibility.md) adds
// terrain, trees and objects next to GW_Scene_1. Whether that costs frame
// rate can only be judged against a "before" number taken the same way on the
// same machine. This is that measurement, repeatable to the metre and the
// degree, so the "after" run later compares like with like.
//
// What it does, on [Research] FrameBenchKey (Home) in GW_Scene_1:
//   for each spot in Spots: teleport the local player there (MapTools.
//   TeleportLocal, the admin/map teleport), hold a fixed facing (player yaw,
//   camera pitch level), wait SettleSeconds for streaming to settle, then
//   record MeasureSeconds of Time.unscaledDeltaTime. Per spot it logs frames,
//   average FPS, 1 % low (1 / the 99th percentile frame time, the same
//   definition as the F6 overlay in RevivalFrameProfiler.cs), max frame time,
//   frames over 50 ms, the scene count at start and end of the window (a
//   change means SceneStreamer was still loading) and the collider under the
//   player (a fall shows up there). At the end: one table in the log and a
//   CSV in %TEMP% (ndr_framebench_<time>.csv), then the player goes back to
//   where the run started. The key again aborts the run and returns the player.
//
// Spots (world x, z; yaw 0 = +z north, 90 = +x east). Settlements ranked by
// counting scene renderers in level7..level17 (vegetation and rocks left
// out); densest forest from GWTerrain2's tree instances; stand points are
// open ground, facing into the most geometry. Measurement and numbers in
// docs/ai/tasks/east-extension-session.md.
//
// The header line logs what the numbers depend on: quality level, resolution,
// vSync, target frame rate, lodBias, shadow distance, the terrain's tree and
// detail distances and the camera far plane, so a LOWEST run and a normal run
// can be told apart and compared.
//
// Nothing here runs unless the switch is on; nothing is changed but the local
// player's position and facing while a run is in progress.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class FrameBench
    {
        const float SettleSeconds = 5f;
        const float MeasureSeconds = 10f;
        const float HitchSeconds = 0.05f;

        struct Spot
        {
            public string Name;
            public float X, Z, Yaw;
            public Spot(string name, float x, float z, float yaw)
            { Name = name; X = x; Z = z; Yaw = yaw; }
        }

        // Keep in step with docs/ai/tasks/east-extension-session.md.
        static readonly Spot[] Spots = new Spot[] {
            new Spot("berezki",        1312f, -2008f, 105f),  // biggest settlement
            new Spot("gorshovo",       1231f,   525f, 135f),  // second
            new Spot("kochkino",       -803f,   430f,  45f),  // third
            new Spot("forest_densest", 2250f,  2250f,  90f),  // 894 trees within 250 m
            new Spot("depot",           238f, -1514f, 270f),  // facing the depot hall
            new Spot("rail_east_edge", 2300f, -1600f,  90f),  // railway at the east edge
            new Spot("seam",           2480f,   950f,  90f),  // the probe's crossing
            new Spot("ne_corner",      2450f,  2450f,  45f),  // north-east corner
        };

        struct Result
        {
            public bool Done;
            public Vector3 Placed, Start;
            public int Frames, Hitches, ScenesStart, ScenesEnd;
            public float Seconds, AvgFps, Low1Fps, MaxMs;
            public string Ground;
        }

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgKey;
        static KeyCode _key = KeyCode.Home;
        static bool _keyParsed;

        static bool _running;
        static int _spot;
        static bool _measuring;
        static float _phaseStart;
        static readonly List<float> _dt = new List<float>(4096);
        static Result[] _results;
        static Vector3 _home;
        static Quaternion _homeRot;
        static string _stamp;

        static Type _camType;
        static PropertyInfo _camInst;
        static FieldInfo _camPitch;
        static bool _camLooked, _pitchWarned;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Research", "FrameBench", false,
                "Frame-time baseline for the east extension: on FrameBenchKey in "
                + "GW_Scene_1, teleport the local player through a fixed list of "
                + "spots, wait 5 s at each, record 10 s of frame times, log a table "
                + "and write a CSV to %TEMP%. Research only, therefore off.");
            _cfgKey = cfg.Bind("Research", "FrameBenchKey", "Home",
                "With FrameBench on: start the run; pressed again, abort it. A "
                + "name from UnityEngine.KeyCode.");
        }

        static bool On { get { return _cfgEnabled != null && _cfgEnabled.Value; } }

        internal static void Tick()
        {
            if (!On) return;
            try
            {
                if (Input.GetKeyDown(Key()))
                {
                    if (_running) Finish("ABORTED by key");
                    else Begin();
                    return;
                }
                if (_running) Step();
            }
            catch (Exception ex)
            {
                Log("error: " + ex);
                _running = false;
            }
        }

        static void Begin()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) { Log("no local player; enter GW_Scene_1 first."); return; }
            if (Vanilla() == null)
            {
                Log("GWTerrain2 not among the active terrains (scene "
                    + SceneManager.GetActiveScene().name + "); the spots are GW_Scene_1 spots.");
                return;
            }
            _home = me.transform.position;
            _homeRot = me.transform.rotation;
            _results = new Result[Spots.Length];
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            _running = true;
            Log("START " + Spots.Length + " spots, settle " + F0(SettleSeconds) + " s, measure "
                + F0(MeasureSeconds) + " s each, about "
                + F0(Spots.Length * (SettleSeconds + MeasureSeconds)) + " s. Hands off mouse and keys; "
                + Key() + " aborts.");
            Log("SETTINGS " + Settings());
            Go(0);
        }

        static void Go(int i)
        {
            _spot = i;
            _measuring = false;
            _dt.Clear();
            Spot s = Spots[i];
            Vector3 p = Stand(s);
            string msg;
            if (!MapTools.TeleportLocal(p, out msg))
            {
                Finish("teleport to " + s.Name + " failed: " + msg);
                return;
            }
            _results[i].Placed = p;
            Face(s);
            _phaseStart = Time.realtimeSinceStartup;
            Log("spot " + (i + 1) + "/" + Spots.Length + " " + s.Name + " at " + V(p) + ", yaw "
                + F0(s.Yaw) + "; settling.");
        }

        static void Step()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) { Finish("ABORTED: the local player is gone (death, scene change, disconnect)"); return; }
            Spot s = Spots[_spot];
            Face(s);
            float now = Time.realtimeSinceStartup;
            if (!_measuring)
            {
                if (now - _phaseStart < SettleSeconds) return;
                _measuring = true;
                _phaseStart = now;
                _results[_spot].Start = me.transform.position;
                _results[_spot].ScenesStart = SceneManager.sceneCount;
                return;
            }
            _dt.Add(Time.unscaledDeltaTime);
            if (now - _phaseStart < MeasureSeconds) return;

            Result r = _results[_spot];
            Score(ref r);
            r.ScenesEnd = SceneManager.sceneCount;
            r.Ground = Ground(me.transform.position);
            r.Done = true;
            _results[_spot] = r;
            Log(Row(s, r));
            if (_spot + 1 < Spots.Length) Go(_spot + 1);
            else Finish("DONE");
        }

        static void Score(ref Result r)
        {
            int n = _dt.Count;
            r.Frames = n;
            if (n == 0) return;
            float[] a = _dt.ToArray();
            float sum = 0f;
            int hitches = 0;
            for (int i = 0; i < n; i++)
            {
                sum += a[i];
                if (a[i] > HitchSeconds) hitches++;
            }
            Array.Sort(a);
            float p99 = a[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];
            r.Seconds = sum;
            r.AvgFps = sum > 0f ? n / sum : 0f;
            r.Low1Fps = p99 > 0f ? 1f / p99 : 0f;
            r.MaxMs = a[n - 1] * 1000f;
            r.Hitches = hitches;
        }

        static void Finish(string why)
        {
            _running = false;
            Log("END " + why + ".");
            GameObject me = MapTools.LocalPlayer();
            if (me != null)
            {
                string msg;
                if (MapTools.TeleportLocal(_home, out msg)) me.transform.rotation = _homeRot;
                else Log("return to " + V(_home) + " failed: " + msg);
            }
            if (_results == null) return;
            Log("RESULT " + SceneManager.GetActiveScene().name + ", " + Settings());
            Log(Header());
            for (int i = 0; i < Spots.Length; i++)
                if (_results[i].Done) Log(Row(Spots[i], _results[i]));
            WriteCsv();
        }

        static void WriteCsv()
        {
            string path = Path.Combine(Path.GetTempPath(), "ndr_framebench_" + _stamp + ".csv");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("spot,x,z,yaw,placed_y,start_y,frames,seconds,avg_fps,low1_fps,max_ms,")
                  .Append("over50ms,scenes_start,scenes_end,ground,settings\n");
                string settings = Settings().Replace("\"", "'");
                for (int i = 0; i < Spots.Length; i++)
                {
                    Result r = _results[i];
                    if (!r.Done) continue;
                    Spot s = Spots[i];
                    sb.Append(s.Name).Append(',').Append(F0(s.X)).Append(',').Append(F0(s.Z)).Append(',')
                      .Append(F0(s.Yaw)).Append(',').Append(F2(r.Placed.y)).Append(',').Append(F2(r.Start.y))
                      .Append(',').Append(r.Frames).Append(',').Append(F2(r.Seconds)).Append(',')
                      .Append(F1(r.AvgFps)).Append(',').Append(F1(r.Low1Fps)).Append(',').Append(F1(r.MaxMs))
                      .Append(',').Append(r.Hitches).Append(',').Append(r.ScenesStart).Append(',')
                      .Append(r.ScenesEnd).Append(",\"").Append((r.Ground ?? "").Replace("\"", "'"))
                      .Append("\",\"").Append(settings).Append("\"\n");
                }
                File.WriteAllText(path, sb.ToString());
                Log("CSV " + path);
            }
            catch (Exception ex)
            {
                Log("CSV " + path + " not written: " + ex.Message);
            }
        }

        static string Header()
        {
            return Pad("spot", 14) + " " + LPad("x", 5) + " " + LPad("z", 6) + " " + LPad("yaw", 5)
                + " " + LPad("frames", 7) + " " + LPad("avg fps", 8) + " " + LPad("1% low", 7) + " "
                + LPad("max ms", 7) + " " + LPad(">50ms", 6) + " " + LPad("scenes", 7) + " "
                + LPad("drop m", 7) + "  ground";
        }

        static string Row(Spot s, Result r)
        {
            return Pad(s.Name, 14) + " " + LPad(F0(s.X), 5) + " " + LPad(F0(s.Z), 6) + " " + LPad(F0(s.Yaw), 5)
                + " " + LPad(r.Frames.ToString(Inv), 7) + " " + LPad(F1(r.AvgFps), 8) + " "
                + LPad(F1(r.Low1Fps), 7) + " " + LPad(F1(r.MaxMs), 7) + " " + LPad(r.Hitches.ToString(Inv), 6)
                + " " + LPad(r.ScenesStart + "->" + r.ScenesEnd, 7) + " " + LPad(F1(r.Placed.y - r.Start.y), 7)
                + "  " + r.Ground;
        }

        // Ground at the spot: the vanilla terrain height, then the first
        // collider within 3 m above it (a road, a platform), never a roof.
        static Vector3 Stand(Spot s)
        {
            Terrain t = Vanilla();
            Vector3 p = new Vector3(s.X, 0f, s.Z);
            float h = t == null ? 500f : t.SampleHeight(p) + t.GetPosition().y;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(s.X, h + 3f, s.Z), Vector3.down, out hit, 10f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                h = hit.point.y;
            p.y = h + 1f;
            return p;
        }

        // Player yaw and a level camera, every frame of the run: the game adds
        // mouse deltas to both, so this also cancels a nudged mouse.
        static void Face(Spot s)
        {
            GameObject me = MapTools.LocalPlayer();
            if (me != null) me.transform.rotation = Quaternion.Euler(0f, s.Yaw, 0f);
            if (!_camLooked)
            {
                _camLooked = true;
                _camType = RevivalPlugin.TypeByName("CameraFPSController");
                if (_camType != null)
                {
                    _camInst = AccessTools.Property(_camType, "Inst");
                    _camPitch = AccessTools.Field(_camType, "rotationX");
                }
            }
            try
            {
                object cam = _camInst == null ? null : _camInst.GetValue(null, null);
                if (cam != null && _camPitch != null) { _camPitch.SetValue(cam, 0f); return; }
            }
            catch (Exception) { }
            if (!_pitchWarned) Log("CameraFPSController.rotationX not reachable; camera pitch not held.");
            _pitchWarned = true;
        }

        static string Settings()
        {
            StringBuilder sb = new StringBuilder();
            int q = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            sb.Append("quality ").Append(q).Append(" '").Append(q >= 0 && q < names.Length ? names[q] : "?")
              .Append("', ").Append(Screen.width).Append('x').Append(Screen.height)
              .Append(Screen.fullScreen ? " fullscreen" : " windowed")
              .Append(", vSync ").Append(QualitySettings.vSyncCount)
              .Append(", targetFrameRate ").Append(Application.targetFrameRate)
              .Append(", lodBias ").Append(F2(QualitySettings.lodBias))
              .Append(", shadowDistance ").Append(F0(QualitySettings.shadowDistance))
              .Append(", pixelLights ").Append(QualitySettings.pixelLightCount);
            Terrain t = Vanilla();
            if (t != null)
                sb.Append(", terrain treeDistance ").Append(F0(t.treeDistance))
                  .Append(" billboardStart ").Append(F0(t.treeBillboardDistance))
                  .Append(" detailDistance ").Append(F0(t.detailObjectDistance))
                  .Append(" detailDensity ").Append(F2(t.detailObjectDensity))
                  .Append(" pixelError ").Append(F1(t.heightmapPixelError))
                  .Append(" basemap ").Append(F0(t.basemapDistance));
            Camera c = Camera.main;
            if (c != null) sb.Append(", camera far ").Append(F0(c.farClipPlane)).Append(" fov ").Append(F0(c.fieldOfView));
            sb.Append(", GPU ").Append(SystemInfo.graphicsDeviceName)
              .Append(", CPU ").Append(SystemInfo.processorType);
            return sb.ToString();
        }

        static Terrain Vanilla()
        {
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].terrainData != null && all[i].terrainData.name == "GWTerrain2")
                    return all[i];
            return null;
        }

        static string Ground(Vector3 p)
        {
            RaycastHit hit;
            if (!Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out hit, 50f,
                                 Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return "NOTHING";
            return hit.collider.name + " (" + F1(p.y - hit.point.y) + " m above)";
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgKey.Value, true); }
            catch
            {
                _key = KeyCode.Home;
                Log("FrameBenchKey " + _cfgKey.Value + " unknown, using Home.");
            }
            return _key;
        }

        static string F0(float v) { return v.ToString("F0", Inv); }
        static string F1(float v) { return v.ToString("F1", Inv); }
        static string F2(float v) { return v.ToString("F2", Inv); }
        static string Pad(string s, int n) { return s.Length >= n ? s : s + new string(' ', n - s.Length); }
        static string LPad(string s, int n) { return s.Length >= n ? s : new string(' ', n - s.Length) + s; }
        static string V(Vector3 v) { return "(" + F1(v.x) + ", " + F2(v.y) + ", " + F1(v.z) + ")"; }
        static void Log(string s) { RevivalPlugin.L.LogInfo("FrameBench: " + s); }
    }
}

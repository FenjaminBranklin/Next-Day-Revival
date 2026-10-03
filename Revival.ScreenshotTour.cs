// Next Day: Survival - Revival Toolkit
//
// SCREENSHOT TOUR. Research only: [Research] ScreenshotTour = false.
//
// Why: judging a change to the airfield, the seam or a town means walking
// there by hand. The tour walks a fixed list of named camera stops instead and
// leaves one JPG per stop, so Kevin and the agents compare pictures, not
// memories.
//
// What it does, on [Research] ScreenshotTourKey (End) in GW_Scene_1:
//   for each stop in BepInEx/plugins/assets/ndr_screenshot_tour.tsv: teleport
//   the local player there (MapTools.TeleportLocal, the admin teleport), hold
//   the stop's facing (player yaw, camera pitch) and time of day, wait until
//   streaming has settled (SettleSeconds, then the scene count unchanged for
//   2 s and SceneStreamer's queues empty, at most SettleSeconds + 20 s), take
//   the screenshot, move on. Output: BepInEx/screenshots/<yyyy-MM-dd>/<stop>.jpg
//   and index.html beside them. At the end the player goes back to where the
//   run started and the sky to its hour. The key again aborts the run.
//
// Never changes savegames or multiplayer state:
//   - online it refuses to run while anyone else is in the room (the teleports
//     and the forced hour would be seen by them), and it ends where it began;
//   - offline (singleplayer) the emulated master server's save writer
//     (TcpConnectionEmulator.serializeAndSaveData) is skipped for the whole run,
//     so the offline save file is not written while the player is away.
//
// -ndrTour on the command line (tools/screenshot_tour.ps1 starts the game with
// it): the plugin loads a singleplayer world itself through the game's own
// code - ClientOptions.SetOfflineMode(true) before the splash screen asks for a
// region (SplashScreenUI.WaitRegionSelect then goes straight to the lobby),
// then in the lobby exactly what the Play button's GameModeOptionsUI.
// PlayGameOnClick does for the offline mode: CreateRoomOptionsDefault.Clone(),
// a new NetworkServerOptions with NormalOffline, the default region and its
// start scene, one player, GameLocationChangeManager.
// CheckTransAndJoinRoomWithRoomOptions. Once the player stands in the world
// the tour runs and Application.Quit ends the game. The offline save is not
// written during the whole session. Any step that fails logs "ScreenshotTour:
// BOOT FAILED <why>" and quits. A watchdog quits after BootLimitSeconds; the
// script kills the process at 5 min regardless. The flag runs the tour even
// with the config switch off - the flag is the request.
//
// Stop file (tab or space separated, # comments, one stop per line):
//   name  x  z  height  yaw  pitch  hour  world  note...
//   name    [A-Za-z0-9_-], also the file name of the JPG
//   x, z    world position (game units, x east, z north)
//   height  eye point above the ground: 0 stands on the ground (the first
//           collider within 3 m above the terrain, a road or a slab); more
//           drops the player from that height onto the first collider below
//           (a roof, a tower cab)
//   yaw     0 = +z north, 90 = +x east
//   pitch   degrees, positive looks up (the game's CameraFPSController.
//           rotationX; its own limits clamp it)
//   hour    0..24 on the TOD_Sky, or - to keep the current hour
//   world   west = GW_Scene_1 itself; east = needs [World] EastTile, skipped
//           (and listed as skipped) without it
//   note    free text for the index
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class ScreenshotTour
    {
        internal const string StopFile = "ndr_screenshot_tour.tsv";
        internal const string BootFlag = "-ndrTour";

        const float DefaultSettle = 5f;
        const float StableSeconds = 2f;
        const float ExtraWaitCap = 20f;
        const float WorldSettleSeconds = 12f;
        const float LobbyGraceSeconds = 3f;
        const float LobbyLimitSeconds = 150f;
        const float BootLimitSeconds = 285f;
        const int JpgQuality = 88;

        internal struct Stop
        {
            public string Name, World, Note;
            public float X, Z, Height, Yaw, Pitch, Hour;   // Hour < 0: keep
        }

        struct Shot
        {
            public string Status, File;
            public Vector3 Placed;
            public float Waited, HourSet;
        }

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgKey;
        static ConfigEntry<float> _cfgSettle;
        static KeyCode _key = KeyCode.End;
        static bool _keyParsed;

        static List<Stop> _stops;
        static Shot[] _shots;
        static bool _running, _capturing;
        static int _index;
        static float _arrived, _stableSince;
        static int _lastSceneCount;
        static Vector3 _home;
        static Quaternion _homeRot;
        static float _homeHour = -1f;
        static string _dir, _started;
        static bool _saveGuard;
        static ScreenshotTourHost _host;

        // -ndrTour
        static bool _boot;
        enum Boot { Lobby, Loading, World, Tour, Quit }
        static Boot _bootState;
        static float _bootStart, _phaseStart, _windowChecked;
        static int _bootWidth = 960, _bootHeight = 540;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Research", "ScreenshotTour", false,
                "Screenshot tour: on ScreenshotTourKey in GW_Scene_1, teleport the local "
                + "player through the stops in assets/ndr_screenshot_tour.tsv, wait for "
                + "streaming, write BepInEx/screenshots/<date>/<stop>.jpg and index.html. "
                + "Refuses while other players are in the room. Research only, therefore off. "
                + "The command-line flag -ndrTour runs it regardless (tools/screenshot_tour.ps1).");
            _cfgKey = cfg.Bind("Research", "ScreenshotTourKey", "End",
                "With ScreenshotTour on: start the tour; pressed again, abort it. A name "
                + "from UnityEngine.KeyCode.");
            _cfgSettle = cfg.Bind("Research", "ScreenshotTourSettle", DefaultSettle,
                "Seconds at each stop before the streaming check starts (then scene count "
                + "stable for 2 s and SceneStreamer idle, at most 20 s more).");
        }

        static bool On { get { return _boot || (_cfgEnabled != null && _cfgEnabled.Value); } }

        /// <summary>From Awake: the host component always (it only acts when the
        /// switch or the flag is on), the save guard patch, and with -ndrTour the
        /// offline mode before the splash screen reaches its region question.</summary>
        internal static void Install(GameObject owner, Harmony harmony)
        {
            _host = owner.AddComponent<ScreenshotTourHost>();
            _boot = HasFlag();
            try
            {
                Type emu = RevivalPlugin.TypeByName("TcpConnectionEmulator");
                MethodInfo save = emu == null ? null : AccessTools.Method(emu, "serializeAndSaveData", null, null);
                if (save != null)
                    harmony.Patch(save, new HarmonyMethod(typeof(ScreenshotTour).GetMethod(
                        "SavePrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
                else if (_boot) Log("TcpConnectionEmulator.serializeAndSaveData not found; the offline save is NOT guarded.");
            }
            catch (Exception ex) { Log("save guard not installed: " + ex.Message); }
            if (!_boot) return;

            _saveGuard = true;
            _bootStart = Time.realtimeSinceStartup;
            _phaseStart = _bootStart;
            _bootState = Boot.Lobby;
            Application.runInBackground = true;
            Log("BOOT " + BootFlag + ": singleplayer world, tour, quit. Offline save writes are skipped this session.");
            try
            {
                Type co = RevivalPlugin.TypeByName("ClientOptions");
                object current = co == null ? null : AccessTools.Property(co, "current").GetValue(null, null);
                MethodInfo set = co == null ? null : AccessTools.Method(co, "SetOfflineMode", null, null);
                if (current == null || set == null) { BootFail("ClientOptions.current / SetOfflineMode not found"); return; }
                set.Invoke(current, new object[] { true });
                Log("BOOT offline mode set (master server emulated, Photon offline).");
            }
            catch (Exception ex) { BootFail("SetOfflineMode: " + ex.Message); }
        }

        // Harmony prefix on TcpConnectionEmulator.serializeAndSaveData: the only
        // writer of the offline save. False skips it.
        static bool SavePrefix()
        {
            return !_saveGuard;
        }

        /// True only in the -ndrTour session and only while the backend's
        /// connection is the game's offline master-server emulator: then
        /// ClientIntegrity lets the (emulated) authentication pass without a
        /// launch receipt. A real server connection is never exempt.
        internal static bool OfflineBoot(object backend)
        {
            if (!_boot || backend == null) return false;
            try
            {
                FieldInfo f = AccessTools.Field(backend.GetType(), "_connection");
                object c = f == null ? null : f.GetValue(backend);
                return c != null && c.GetType().Name == "TcpConnectionEmulator";
            }
            catch (Exception) { return false; }
        }

        static bool HasFlag()
        {
            bool flag = false;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], BootFlag, StringComparison.OrdinalIgnoreCase)) flag = true;
                    int v;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, Inv, out v) && v >= 320)
                    {
                        if (args[i] == "-screen-width") _bootWidth = v;
                        if (args[i] == "-screen-height") _bootHeight = v;
                    }
                }
            }
            catch (Exception) { }
            return flag;
        }

        internal static void Tick()
        {
            if (!On) return;
            try
            {
                if (_boot) BootStep();
                else if (Input.GetKeyDown(Key()))
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
                if (_running) Finish("ABORTED by error");
                if (_boot) BootFail("exception");
            }
        }

        // ------------------------------------------------------------ -ndrTour

        static void BootStep()
        {
            float now = Time.realtimeSinceStartup;
            if (_bootState == Boot.Quit) return;
            KeepWindow(now);
            if (now - _bootStart > BootLimitSeconds)
            {
                if (_running) Finish("ABORTED by the boot watchdog");
                BootFail("watchdog: " + F0(BootLimitSeconds) + " s passed in state " + _bootState);
                return;
            }
            if (_bootState == Boot.Lobby)
            {
                string why = LobbyNotReady();
                if (why != null)
                {
                    if (now - _phaseStart > LobbyLimitSeconds) BootFail("lobby not ready after " + F0(LobbyLimitSeconds) + " s: " + why);
                    return;
                }
                if (now - _lobbyReadyAt < LobbyGraceSeconds) return;   // first ready frame + grace
                Play();
                _bootState = Boot.Loading;
                _phaseStart = now;
                return;
            }
            if (_bootState == Boot.Loading)
            {
                // Not the lobby's character preview: the lobby scene must be gone.
                if (SceneManager.GetActiveScene().name == _lobbyScene || MapTools.LocalPlayer() == null) return;
                Log("BOOT local player in " + SceneManager.GetActiveScene().name + "; settling " + F0(WorldSettleSeconds) + " s.");
                _bootState = Boot.World;
                _phaseStart = now;
                return;
            }
            if (_bootState == Boot.World)
            {
                if (MapTools.LocalPlayer() == null) { _bootState = Boot.Loading; return; }
                if (now - _phaseStart < WorldSettleSeconds) return;
                _bootState = Boot.Tour;
                if (!Begin()) BootFail("the tour did not start (see above)");
                return;
            }
            if (_bootState == Boot.Tour && !_running) Quit("tour finished");
        }

        // The game applies its own saved resolution after start; the tour
        // wants the small window the script asked for (-screen-width/-height).
        static void KeepWindow(float now)
        {
            if (now - _windowChecked < 2f || _capturing) return;
            _windowChecked = now;
            if (Screen.fullScreen || Screen.width != _bootWidth || Screen.height != _bootHeight)
                Screen.SetResolution(_bootWidth, _bootHeight, false);
        }

        static float _lobbyReadyAt = -1f;
        static string _lobbyScene;

        /// Null when the lobby can take the Play call, else what is missing.
        static string LobbyNotReady()
        {
            Type gsl = RevivalPlugin.TypeByName("GameScenesLoadingManager");
            if (gsl == null) return "GameScenesLoadingManager type";
            object lobby = AccessTools.Property(gsl, "DEFAULT_SCENE_LOBBY").GetValue(null, null);
            string want = lobby == null ? null : lobby.ToString();
            _lobbyScene = want;
            if (SceneManager.GetActiveScene().name != want) { _lobbyReadyAt = -1f; return "active scene " + SceneManager.GetActiveScene().name + ", lobby is " + want; }
            object backend = Static("BackendManager", "Instance");
            if (backend == null) return "BackendManager.Instance";
            object connected = AccessTools.Method(backend.GetType(), "IsConnected", null, null).Invoke(backend, null);
            if (!(connected is bool) || !(bool)connected) return "BackendManager not connected";
            FieldInfo loaded = AccessTools.Field(backend.GetType(), "allDataSuccessfullLoaded");
            if (loaded != null && !(bool)loaded.GetValue(backend)) return "BackendManager.allDataSuccessfullLoaded false";
            if (Static("GameLocationChangeManager", "Instance") == null) return "GameLocationChangeManager.Instance";
            if (Static("GameRegionsManager", "Instance") == null) return "GameRegionsManager.Instance";
            if (Character(backend) == null) return "no NormalOffline character in the offline profile";
            if (_lobbyReadyAt < 0f) _lobbyReadyAt = Time.realtimeSinceStartup;
            return null;
        }

        static object Character(object backend)
        {
            Type gm = RevivalPlugin.TypeByName("GameMode");
            MethodInfo get = AccessTools.Method(backend.GetType(), "GetCharacterDataByGameMode", null, null);
            if (gm == null || get == null) return null;
            return get.Invoke(backend, new object[] { Enum.Parse(gm, "NormalOffline") });
        }

        /// GameModeOptionsUI.PlayGameOnClick for the offline mode, without its UI.
        static void Play()
        {
            Type co = RevivalPlugin.TypeByName("ClientOptions");
            object current = AccessTools.Property(co, "current").GetValue(null, null);
            object defaults = AccessTools.Field(co, "CreateRoomOptionsDefault").GetValue(current);
            object opts = AccessTools.Method(defaults.GetType(), "Clone", null, null).Invoke(defaults, null);
            Type nsoType = RevivalPlugin.TypeByName("NetworkServerOptions");
            object nso = Activator.CreateInstance(nsoType, true);
            AccessTools.Field(opts.GetType(), "_networkServerOptions").SetValue(opts, nso);

            Type gm = RevivalPlugin.TypeByName("GameMode");
            AccessTools.Field(nsoType, "_gameMode").SetValue(nso, Enum.Parse(gm, "NormalOffline"));
            FieldInfo regionField = AccessTools.Field(nsoType, "_gameRegion");
            object region = regionField.GetValue(nso);          // the popup's default: the first region
            object regions = Static("GameRegionsManager", "Instance");
            object data = AccessTools.Method(regions.GetType(), "GetGameRegionData", null, null).Invoke(regions, new object[] { region });
            if (data == null) { BootFail("no game region data for " + region); return; }
            object scene = AccessTools.Field(data.GetType(), "startScene").GetValue(data);
            AccessTools.Field(nsoType, "_gameScene").SetValue(nso, scene);
            AccessTools.Field(opts.GetType(), "maxPlayers").SetValue(opts, 1);

            object glc = Static("GameLocationChangeManager", "Instance");
            Log("BOOT play: NormalOffline, region " + region + ", start scene " + scene
                + " (the character's saved location decides the scene the game loads).");
            AccessTools.Method(glc.GetType(), "CheckTransAndJoinRoomWithRoomOptions", null, null).Invoke(glc, new object[] { opts });
        }

        static object Static(string type, string member)
        {
            Type t = RevivalPlugin.TypeByName(type);
            if (t == null) return null;
            PropertyInfo p = AccessTools.Property(t, member);
            if (p != null && p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic) return p.GetValue(null, null);
            FieldInfo f = AccessTools.Field(t, member);
            return f != null && f.IsStatic ? f.GetValue(null) : null;
        }

        static void BootFail(string why)
        {
            Log("BOOT FAILED " + why + ".");
            Quit("boot failed");
        }

        static void Quit(string why)
        {
            if (_bootState == Boot.Quit) return;
            _bootState = Boot.Quit;
            Log("BOOT quit (" + why + ").");
            if (_host != null) _host.StartCoroutine(QuitSoon());
            else Application.Quit();
        }

        static IEnumerator QuitSoon()
        {
            yield return new WaitForSeconds(1f);   // let the log flush
            Application.Quit();
        }

        // ---------------------------------------------------------------- tour

        static bool Begin()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) { Log("no local player; enter the world first."); return false; }
            if (Vanilla() == null)
            {
                Log("GWTerrain2 not among the active terrains (scene " + SceneManager.GetActiveScene().name
                    + "); the stops are GW_Scene_1 stops.");
                return false;
            }
            int others = OtherPlayers();
            if (others > 0)
            {
                Log("refused: " + others + " other player(s) in the room would see the teleports and the forced hour.");
                return false;
            }
            string msg;
            _stops = LoadStops(Path.Combine(RevivalPlugin.AssetDir, StopFile), out msg);
            if (_stops == null || _stops.Count == 0) { Log("no stops: " + msg); return false; }
            _home = me.transform.position;
            _homeRot = me.transform.rotation;
            _homeHour = Hour();
            _shots = new Shot[_stops.Count];
            _started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv);
            _dir = Path.Combine(Path.Combine(Paths.BepInExRootPath, "screenshots"), DateTime.Now.ToString("yyyy-MM-dd", Inv));
            Directory.CreateDirectory(_dir);
            if (PhotonOffline()) _saveGuard = true;
            _running = true;
            Log("START " + _stops.Count + " stops from " + StopFile + " -> " + _dir + ". Hands off mouse and keys"
                + (_boot ? "." : "; " + Key() + " aborts."));
            Go(0);
            return true;
        }

        static void Go(int i)
        {
            for (; i < _stops.Count; i++)
            {
                Stop s = _stops[i];
                if (s.World == "east" && !EastWorld.Extends)
                {
                    _shots[i].Status = "skipped: east stop, [World] EastTile off";
                    Log("stop " + (i + 1) + "/" + _stops.Count + " " + s.Name + " " + _shots[i].Status + ".");
                    continue;
                }
                Vector3 p = Stand(s);
                string msg;
                if (!MapTools.TeleportLocal(p, out msg))
                {
                    _shots[i].Status = "teleport failed: " + msg;
                    Log("stop " + s.Name + " " + _shots[i].Status);
                    continue;
                }
                _index = i;
                _shots[i].Placed = p;
                _arrived = Time.realtimeSinceStartup;
                _stableSince = _arrived;
                _lastSceneCount = SceneManager.sceneCount;
                Face(s);
                Log("stop " + (i + 1) + "/" + _stops.Count + " " + s.Name + " at " + V(p) + ", yaw " + F0(s.Yaw)
                    + ", pitch " + F0(s.Pitch) + ", hour " + (s.Hour < 0f ? "kept" : F1(s.Hour)) + "; settling.");
                return;
            }
            Finish("DONE");
        }

        static void Step()
        {
            if (_capturing) return;
            GameObject me = MapTools.LocalPlayer();
            if (me == null) { Finish("ABORTED: the local player is gone (death, scene change, disconnect)"); return; }
            Stop s = _stops[_index];
            Face(s);
            float now = Time.realtimeSinceStartup;
            int scenes = SceneManager.sceneCount;
            if (scenes != _lastSceneCount || StreamerBusy()) { _lastSceneCount = scenes; _stableSince = now; }
            float settle = _cfgSettle == null ? DefaultSettle : Mathf.Max(1f, _cfgSettle.Value);
            float waited = now - _arrived;
            if (waited < settle) return;
            bool stable = now - _stableSince >= StableSeconds;
            if (!stable && waited < settle + ExtraWaitCap) return;
            _shots[_index].Waited = waited;
            _shots[_index].HourSet = Hour();
            if (!stable) Log("stop " + s.Name + ": streaming still busy after " + F0(waited) + " s; shooting anyway.");
            _capturing = true;
            _host.StartCoroutine(Capture(_index));
        }

        static IEnumerator Capture(int i)
        {
            yield return new WaitForEndOfFrame();
            Stop s = _stops[i];
            string file = s.Name + ".jpg";
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToJPG(JpgQuality));
                _shots[i].Status = "ok";
                _shots[i].File = file;
                Log("stop " + s.Name + " -> " + file + " (" + Screen.width + "x" + Screen.height + ", waited "
                    + F1(_shots[i].Waited) + " s).");
            }
            catch (Exception ex)
            {
                _shots[i].Status = "screenshot failed: " + ex.Message;
                Log("stop " + s.Name + " " + _shots[i].Status);
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                _capturing = false;
            }
            if (_running) Go(i + 1);
        }

        static void Finish(string why)
        {
            _running = false;
            _capturing = false;
            Log("END " + why + ".");
            GameObject me = MapTools.LocalPlayer();
            if (me != null)
            {
                string msg;
                if (MapTools.TeleportLocal(_home, out msg)) me.transform.rotation = _homeRot;
                else Log("return to " + V(_home) + " failed: " + msg);
            }
            if (_homeHour >= 0f) SetHour(_homeHour);
            if (!_boot) _saveGuard = false;
            WriteIndex(why);
        }

        // --------------------------------------------------------------- stops

        internal static List<Stop> LoadStops(string path, out string message)
        {
            List<Stop> list = new List<Stop>();
            if (!File.Exists(path)) { message = path + " missing"; return null; }
            string[] lines = File.ReadAllLines(path);
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split(new char[] { '\t', ' ' }, 9, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 8) { Log(StopFile + " line " + (n + 1) + ": 8 fields expected, skipped."); continue; }
                Stop s = new Stop();
                s.Name = f[0];
                s.World = f[7].ToLowerInvariant();
                s.Note = f.Length > 8 ? f[8].Trim() : "";
                bool ok = ValidName(s.Name) && (s.World == "west" || s.World == "east")
                    && Num(f[1], out s.X) && Num(f[2], out s.Z) && Num(f[3], out s.Height)
                    && Num(f[4], out s.Yaw) && Num(f[5], out s.Pitch);
                if (f[6] == "-") s.Hour = -1f;
                else ok = ok && Num(f[6], out s.Hour) && s.Hour >= 0f && s.Hour <= 24f;
                if (!ok) { Log(StopFile + " line " + (n + 1) + ": bad value, skipped: " + line); continue; }
                list.Add(s);
            }
            message = list.Count + " stops";
            return list;
        }

        static bool ValidName(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-') return false;
            }
            return s.Length > 0;
        }

        static bool Num(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float, Inv, out v);
        }

        // --------------------------------------------------------------- world

        // The terrain under the point, then the first collider below
        // terrain + height + 3 (height 0: a road or slab within 3 m, never a
        // roof; more: a roof or a tower cab), one metre up.
        static Vector3 Stand(Stop s)
        {
            float h = 500f;
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
            {
                Terrain t = all[i];
                if (t == null || t.terrainData == null) continue;
                Vector3 o = t.GetPosition(), size = t.terrainData.size;
                if (s.X < o.x || s.Z < o.z || s.X > o.x + size.x || s.Z > o.z + size.z) continue;
                h = t.SampleHeight(new Vector3(s.X, 0f, s.Z)) + o.y;
                break;
            }
            float top = h + Mathf.Max(0f, s.Height) + 3f;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(s.X, top, s.Z), Vector3.down, out hit, top - h + 10f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                h = hit.point.y;
            return new Vector3(s.X, h + 1f, s.Z);
        }

        static Type _camType;
        static PropertyInfo _camInst;
        static FieldInfo _camPitch;
        static bool _camLooked;

        // Every frame: player yaw, camera pitch and hour (the game adds mouse
        // deltas and the sky moves on).
        static void Face(Stop s)
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
                if (cam != null && _camPitch != null) _camPitch.SetValue(cam, s.Pitch);
            }
            catch (Exception) { }
            if (s.Hour >= 0f) SetHour(s.Hour);
        }

        static object Cycle()
        {
            object sky = Static("TOD_Sky", "Instance");
            if (sky == null) return null;
            FieldInfo f = AccessTools.Field(sky.GetType(), "Cycle");
            return f == null ? null : f.GetValue(sky);
        }

        static float Hour()
        {
            try
            {
                object c = Cycle();
                FieldInfo f = c == null ? null : AccessTools.Field(c.GetType(), "Hour");
                return f == null ? -1f : Convert.ToSingle(f.GetValue(c));
            }
            catch (Exception) { return -1f; }
        }

        static void SetHour(float h)
        {
            try
            {
                object c = Cycle();
                FieldInfo f = c == null ? null : AccessTools.Field(c.GetType(), "Hour");
                if (f != null) f.SetValue(c, Convert.ChangeType(h, f.FieldType, Inv));
            }
            catch (Exception) { }
        }

        static Type _streamerType;
        static bool _streamerLooked;

        // SceneStreamer's load/unload queues; false when it cannot be read (the
        // scene-count test still holds the shot).
        static bool StreamerBusy()
        {
            if (!_streamerLooked)
            {
                _streamerLooked = true;
                _streamerType = RevivalPlugin.TypeByName("SceneStreamer.SceneStreamer")
                    ?? RevivalPlugin.TypeByName("SceneStreamer");
            }
            if (_streamerType == null) return false;
            try
            {
                object inst = null;
                PropertyInfo p = AccessTools.Property(_streamerType, "Inst");
                if (p != null) inst = p.GetValue(null, null);
                if (inst == null) return false;
                string[] queues = { "AsyncQueue", "m_loadQueue", "m_unloadQueue", "loadQueue", "unloadQueue" };
                for (int i = 0; i < queues.Length; i++)
                {
                    FieldInfo f = AccessTools.Field(_streamerType, queues[i]);
                    ICollection c = f == null ? null : f.GetValue(inst) as ICollection;
                    if (c != null && c.Count > 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        static Type PhotonType()
        {
            return RevivalPlugin.TypeByName("PhotonNetwork");
        }

        static bool PhotonOffline()
        {
            try
            {
                Type t = PhotonType();
                PropertyInfo p = t == null ? null : AccessTools.Property(t, "offlineMode");
                return p != null && (bool)p.GetValue(null, null);
            }
            catch (Exception) { return false; }
        }

        static int OtherPlayers()
        {
            if (PhotonOffline()) return 0;
            try
            {
                Type t = PhotonType();
                PropertyInfo p = t == null ? null : AccessTools.Property(t, "otherPlayers");
                Array a = p == null ? null : p.GetValue(null, null) as Array;
                if (a != null) return a.Length;
                p = t == null ? null : AccessTools.Property(t, "playerList");
                a = p == null ? null : p.GetValue(null, null) as Array;
                return a == null ? 0 : Mathf.Max(0, a.Length - 1);
            }
            catch (Exception) { return 0; }
        }

        static Terrain Vanilla()
        {
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].terrainData != null && all[i].terrainData.name == "GWTerrain2")
                    return all[i];
            return null;
        }

        // --------------------------------------------------------------- index

        static void WriteIndex(string why)
        {
            if (_dir == null || _stops == null || _shots == null) return;
            string path = Path.Combine(_dir, "index.html");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>NDR screenshot tour ")
                  .Append(Html(_started)).Append("</title>\n<style>body{font-family:sans-serif;background:#222;color:#ddd}")
                  .Append("figure{display:inline-block;margin:8px;vertical-align:top;width:480px}")
                  .Append("img{width:480px;border:1px solid #555}figcaption{font-size:13px}")
                  .Append(".skip{color:#e99}</style></head><body>\n<h1>NDR screenshot tour</h1>\n<p>")
                  .Append(Html(_started)).Append(" - ").Append(Html(why)).Append(" - ")
                  .Append(Html(SceneManager.GetActiveScene().name)).Append(", east world ")
                  .Append(EastWorld.Extends ? "on" : "off").Append(", ").Append(Screen.width).Append('x')
                  .Append(Screen.height).Append(", quality ").Append(QualitySettings.GetQualityLevel())
                  .Append(", plugin ").Append(Html(RevivalPlugin.VERSION)).Append("</p>\n");
                for (int i = 0; i < _stops.Count; i++)
                {
                    Stop s = _stops[i];
                    Shot r = _shots[i];
                    string head = Html(s.Name) + " (" + F0(s.X) + ", " + F0(s.Z) + ") yaw " + F0(s.Yaw)
                        + " pitch " + F0(s.Pitch) + " hour " + (s.Hour < 0f ? "kept" : F1(s.Hour));
                    sb.Append("<figure>");
                    if (r.File != null)
                        sb.Append("<a href=\"").Append(r.File).Append("\"><img src=\"").Append(r.File)
                          .Append("\" alt=\"").Append(Html(s.Name)).Append("\"></a>");
                    sb.Append("<figcaption><b>").Append(head).Append("</b><br>");
                    if (r.File == null)
                        sb.Append("<span class=\"skip\">").Append(Html(r.Status ?? "not reached")).Append("</span><br>");
                    else
                        sb.Append("placed y ").Append(F1(r.Placed.y)).Append(", waited ").Append(F1(r.Waited)).Append(" s<br>");
                    sb.Append(Html(s.Note)).Append("</figcaption></figure>\n");
                }
                sb.Append("</body></html>\n");
                File.WriteAllText(path, sb.ToString());
                Log("INDEX " + path);
            }
            catch (Exception ex)
            {
                Log("index " + path + " not written: " + ex.Message);
            }
        }

        static string Html(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '&') sb.Append("&amp;");
                else if (c == '"') sb.Append("&quot;");
                else if (c < 128) sb.Append(c);
                else sb.Append("&#").Append((int)c).Append(';');
            }
            return sb.ToString();
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgKey.Value, true); }
            catch
            {
                _key = KeyCode.End;
                Log("ScreenshotTourKey " + _cfgKey.Value + " unknown, using End.");
            }
            return _key;
        }

        static string F0(float v) { return v.ToString("F0", Inv); }
        static string F1(float v) { return v.ToString("F1", Inv); }
        static string V(Vector3 v) { return "(" + F1(v.x) + ", " + F1(v.y) + ", " + F1(v.z) + ")"; }
        static void Log(string s) { RevivalPlugin.L.LogInfo("ScreenshotTour: " + s); }
    }

    /// Runs the tour's frame step and its end-of-frame screenshot coroutine.
    internal sealed class ScreenshotTourHost : MonoBehaviour
    {
        void Update() { ScreenshotTour.Tick(); }
    }
}

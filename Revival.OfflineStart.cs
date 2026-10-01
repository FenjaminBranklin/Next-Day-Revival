// Z H0a: explicit command-line test mode; never installed into the Steam game.
// Native NormalOffline uses TcpConnectionEmulator + Photon offline ownership.
// C# 3.0, ASCII. offline_start_check.py checks the native IL contracts.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class OfflineStart
    {
        internal static bool Active;
        static bool _valid, _written;
        static string _output, _profile, _error = "";
        static int _seed;
        // Generated run output, rather than an input asset under assets/.
        static readonly string ScreenshotName = Path.ChangeExtension("frame", "png");
        static float _nextProbe;
        static OfflineRunClock _clock;
        static Type _backendType, _photonType, _locationType;
        static FieldInfo _allLoaded, _playerField;
        static PropertyInfo _backendInstance, _connected, _offline, _master;
        static long _aiActions, _aiAtLoad;
        static int _npcAtLoad;
        static Component _trackedNpc;
        static Vector3 _npcStart;
        // Held until process termination, including asynchronous Unity shutdown.
        static ScenarioGate _runGate;

        internal static void Configure()
        {
            string[] args = Environment.GetCommandLineArgs();
            Active = Array.IndexOf(args, "--ndr-offline-run") >= 0 || Array.IndexOf(args, "-ndrScenario") >= 0;
            if (!Active) return;
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string marker = Path.Combine(root, "ndr-test-copy.marker");
                PlainPath(root); PlainPath(marker);
                if (root.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || !File.Exists(marker)
                    || !string.Equals(File.ReadAllText(marker).Trim(), root, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Offline run requires a marked test copy outside Steam.");
                string output = Path.GetFullPath(ScenarioRun.Argument(args, "-ndrOut", Arg(args, "--ndr-output", "")));
                if (!Inside(output, Path.Combine(root, "runs")))
                    throw new Exception("Output must be inside the test copy's runs directory.");
                PlainPath(output); PlainPath(Path.Combine(output, "profile"));
                _runGate = new ScenarioGate("Global\\NDR_OfflineGame_Runtime");
                _output = output;
                _profile = Path.Combine(_output, "profile");
                Directory.CreateDirectory(_profile);
                _seed = int.Parse(ScenarioRun.Argument(args, "-ndrSeed", Arg(args, "--ndr-seed", "1729")), CultureInfo.InvariantCulture);
                ScenarioRun.Configure(args, root, _output, _seed);
                _clock = new OfflineRunClock(Time.realtimeSinceStartup,
                    ScenarioRun.Active ? 1 : double.Parse(Arg(args, "--ndr-wait", "15"), CultureInfo.InvariantCulture),
                    ScenarioRun.Active ? ScenarioRun.Timeout : double.Parse(Arg(args, "--ndr-timeout", "240"), CultureInfo.InvariantCulture));
                UnityEngine.Random.InitState(_seed);
                Application.runInBackground = true;
                _valid = true;
                RevivalPlugin.L.LogInfo("OfflineStart: marked test copy; seed " + _seed + ", output " + _output);
            }
            catch (Exception ex) { Fail(ex); }
        }

        static string Arg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return fallback;
        }

        static bool Inside(string path, string root)
        {
            return path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void PlainPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Offline paths must not contain reparse points.");
                current = Path.GetDirectoryName(current);
            }
        }

        internal static void Install(Harmony h)
        {
            if (!Active || !_valid) return;
            try
            {
                Patch(h, "ClientOptions", "Init", null, "OptionsReady");
                Patch(h, "SteamManager", "Awake", "Skip", null);
                Patch(h, "SteamManager", "get_Initialized", "SteamInactive", null);
                Patch(h, "SteamInterface", "GetPersonaName", "Persona", null);
                Patch(h, "SteamInterface", "GetCurrentGameLanguage", "Language", null);
                Patch(h, "BackendManager", "Start", "BackendStart", null);
                Patch(h, "BackendManager", "PhotonNetworkCustomConnection", "PhotonConnect", null);
                Patch(h, "NPC_AI2", "StateAction", null, "AiAction");
                // Steam callbacks remain inactive (Initialized stays false).
                // Rewrite managed storage callers, not Unity internal-call getters.
                string[] storageTypes = { "IOWrapper", "TcpConnectionEmulator", "CTPlayerPrefs",
                    "ObscuredPrefs", "Localization", "NGUITools", "UIInput", "UISavedOption",
                    "PhotonHandler", "Config", "ActTesterGui", "NamePickGui",
                    "ObscuredPerformanceTests", "PlayerNameInputField", "RpsDemoConnect", "WorkerMenu" };
                foreach (string name in storageTypes)
                {
                    Type type = NeedType(name);
                    List<MethodBase> methods = new List<MethodBase>();
                    methods.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
                    methods.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance));
                    foreach (MethodBase method in methods)
                    {
                        if (method.GetMethodBody() == null || method.ContainsGenericParameters) continue;
                        h.Patch(method, null, null, Hook("Storage"), null, null);
                    }
                }
                ConstructorInfo io = NeedType("IOWrapper").GetConstructor(new Type[] { typeof(string), typeof(string), typeof(bool) });
                if (io == null) throw new Exception("Missing IOWrapper constructor.");
                h.Patch(io, Hook("IoPath"), null, null, null, null);
                _backendType = NeedType("BackendManager");
                _photonType = NeedType("PhotonNetwork");
                _locationType = NeedType("GameLocationChangeManager");
                _backendInstance = AccessTools.Property(_backendType, "Instance");
                _allLoaded = AccessTools.Field(_backendType, "allDataSuccessfullLoaded");
                _offline = AccessTools.Property(_photonType, "offlineMode");
                _connected = AccessTools.Property(_photonType, "connectedAndReady");
                _master = AccessTools.Property(_photonType, "isMasterClient");
                _playerField = AccessTools.Field(NeedType("NetworkGameServer"), "localPlayer");
                if (_backendInstance == null || _allLoaded == null || _offline == null
                    || _connected == null || _master == null || _playerField == null)
                    throw new Exception("Offline bootstrap contract differs from the inspected game.");
                // ClientOptions may already have been cached before BepInEx Awake.
                OptionsReady(AccessTools.Property(NeedType("ClientOptions"), "current").GetValue(null, null));
                NpcWar.ScenarioInstall(h);
            }
            catch (Exception ex) { Fail(ex); }
        }

        static Type NeedType(string name)
        {
            Type type = RevivalPlugin.TypeByName(name);
            if (type == null) throw new Exception("Missing game type " + name);
            return type;
        }

        static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(OfflineStart).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        static void Patch(Harmony h, string type, string method, string pre, string post)
        {
            MethodInfo target = AccessTools.Method(NeedType(type), method, null, null);
            if (target == null) throw new Exception("Missing game method " + type + "." + method);
            h.Patch(target, pre == null ? null : Hook(pre), post == null ? null : Hook(post), null, null, null);
        }

        static bool Skip() { return false; }
        // The native getter creates a manager when Awake has not set s_instance.
        // Return false directly so repeated callers never create Steam objects.
        static bool SteamInactive(ref bool __result) { __result = false; return false; }
        static bool Persona(ref string __result) { __result = "NDR Offline Test"; return false; }
        static bool Language(ref string __result) { __result = "english"; return false; }

        static void OptionsReady(object __instance)
        {
            AccessTools.Method(__instance.GetType(), "SetOfflineMode", null, null).Invoke(__instance, new object[] { true });
            AccessTools.Field(__instance.GetType(), "_isDisabledEAC").SetValue(__instance, true);
        }

        static bool BackendStart(object __instance)
        {
            try
            {
                // Preserve the emulator's full native auth/static-data/profile flow.
                // The normal Start only adds Steam auth/stats and loading UI to this.
                AccessTools.Field(__instance.GetType(), "steamUserId").SetValue(__instance, "0");
                AccessTools.Field(__instance.GetType(), "steamAuthTicketHex").SetValue(__instance, "");
                object routine = AccessTools.Method(__instance.GetType(), "MasterServerConnection", null, null)
                    .Invoke(__instance, new object[] { true });
                ((MonoBehaviour)__instance).StartCoroutine((System.Collections.IEnumerator)routine);
            }
            catch (Exception ex) { Fail(ex); }
            return false;
        }

        static bool PhotonConnect(bool __0)
        {
            // No transport, DNS or sockets; native callbacks/room ownership remain.
            AccessTools.Property(NeedType("PhotonNetwork"), "offlineMode").SetValue(null, __0, null);
            return false;
        }

        static void IoPath(ref string __0)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (string.IsNullOrEmpty(__0) || !Inside(Path.GetFullPath(__0), root))
                __0 = _profile;
        }

        static string Profile() { return _profile; }

        static IEnumerable<CodeInstruction> Storage(IEnumerable<CodeInstruction> input)
        {
            foreach (CodeInstruction code in input)
            {
                MethodInfo method = code.operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(Application) && method.Name == "get_persistentDataPath")
                    code.operand = typeof(OfflineStart).GetMethod("Profile", BindingFlags.Static | BindingFlags.NonPublic);
                else if (method != null && method.DeclaringType == typeof(PlayerPrefs))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    Type[] types = new Type[parameters.Length];
                    for (int i = 0; i < types.Length; i++) types[i] = parameters[i].ParameterType;
                    MethodInfo replacement = typeof(OfflinePrefs).GetMethod(method.Name, types);
                    if (replacement == null) throw new Exception("Unhandled PlayerPrefs method " + method.Name);
                    code.operand = replacement;
                    code.opcode = OpCodes.Call;
                }
                yield return code;
            }
        }

        static void AiAction()
        {
            FrameProf.S(FrameProf.S_OfflineAi);
            _aiActions++;
            FrameProf.E(FrameProf.S_OfflineAi);
        }

        internal static void Tick()
        {
            if (!Active || !_valid || _written) return;
            try
            {
                double now = Time.realtimeSinceStartup;
                if (ScenarioRun.Active && _clock.Phase == OfflinePhase.Waiting) { ScenarioRun.Tick(); return; }
                _clock.Step(now, Time.unscaledDeltaTime);
                if (_clock.Phase == OfflinePhase.Failed) { Fail(new Exception("Offline load/run deadline exceeded.")); return; }
                if (_clock.Phase == OfflinePhase.Waiting) return;
                if (_clock.Phase == OfflinePhase.Capture)
                {
                    // ScreenCapture needs rendered frames; retain the graphics device.
                    if (_clock.CaptureAt == now) ScreenCapture.CaptureScreenshot(Path.Combine(_output, ScreenshotName));
                    if (now - _clock.CaptureAt < 3) return;
                    if (!File.Exists(Path.Combine(_output, ScreenshotName)))
                        throw new Exception("Screenshot missing after capture; inspect batch-mode graphics support.");
                    _clock.Finish();
                    WriteResult(true);
                    Application.Quit();
                    return;
                }
                if (now < _nextProbe) return;
                _nextProbe = (float)now + 0.5f;
                if (_clock.Phase == OfflinePhase.Bootstrap)
                {
                    object backend = _backendInstance.GetValue(null, null);
                    if (backend == null || !FastField.GetBool(_allLoaded, backend) || !(bool)_connected.GetValue(null, null)) return;
                    if (!(bool)_offline.GetValue(null, null)) throw new Exception("Photon transport was not offline.");
                    object location = AccessTools.Field(_locationType, "Instance").GetValue(null);
                    if (location == null) return;
                    object options = AccessTools.Field(NeedType("ClientOptions"), "CreateRoomOptionsDefault")
                        .GetValue(AccessTools.Property(NeedType("ClientOptions"), "current").GetValue(null, null));
                    options = AccessTools.Method(options.GetType(), "Clone", null, null).Invoke(options, null);
                    AccessTools.Field(options.GetType(), "roomName").SetValue(options, "NDR_OFFLINE_TEST");
                    AccessTools.Field(options.GetType(), "maxPlayers").SetValue(options, 1);
                    object serverOptions = AccessTools.Field(options.GetType(), "_networkServerOptions").GetValue(options);
                    SetEnum(serverOptions, "_gameMode", "NormalOffline");
                    SetEnum(serverOptions, "_gameScene", MapScene.Home);
                    SetEnum(serverOptions, "_gameRegion", "Severoufimsk");
                    _clock.Joined();
                    UnityEngine.Random.InitState(_seed);
                    AccessTools.Method(_locationType, "CreateRoom", null, null).Invoke(location, new object[] { options, false });
                    RevivalPlugin.L.LogInfo("OfflineStart: native NormalOffline room requested.");
                }
                else if (_clock.Phase == OfflinePhase.Loading)
                {
                    if (!SceneManager.GetSceneByName(MapScene.Home).isLoaded
                        || !SceneManager.GetSceneByName(EastWorld.SceneName).isLoaded) return;
                    object server = AccessTools.Property(NeedType("NetworkGameServer"), "Instance").GetValue(null, null);
                    if (server == null || _playerField.GetValue(server) == null || !(bool)_master.GetValue(null, null)) return;
                    // Existing east pipeline queues content additively; require all its scenes.
                    Dictionary<string, string> content = (Dictionary<string, string>)AccessTools.Field(typeof(EastWorld), "_contentScenes").GetValue(null);
                    if (!(bool)AccessTools.Field(typeof(EastWorld), "_contentQueued").GetValue(null) || content.Count == 0) return;
                    foreach (string scene in content.Values) if (!SceneManager.GetSceneByName(scene).isLoaded) return;
                    Component[] npcs = NpcScan.All();
                    _npcAtLoad = npcs.Length;
                    if (npcs.Length > 0) { _trackedNpc = npcs[0]; _npcStart = _trackedNpc.transform.position; }
                    _aiAtLoad = _aiActions;
                    _clock.Loaded(now);
                    ScenarioRun.Begin();
                    RevivalPlugin.L.LogInfo("OfflineStart: map, east content, local player and master ready; timed wait begins.");
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        static void SetEnum(object target, string field, string value)
        {
            FieldInfo info = AccessTools.Field(target.GetType(), field);
            info.SetValue(target, Enum.Parse(info.FieldType, value));
        }

        static void Fail(Exception ex)
        {
            _error = ex.GetBaseException().Message;
            _valid = false;
            RevivalPlugin.L.LogError("OfflineStart: " + _error);
            if (ScenarioRun.Active) ScenarioRun.Fail(_error);
            else if (_output != null) WriteResult(false);
            Application.Quit();
        }

        static void WriteResult(bool success)
        {
            _written = true;
            try
            {
                StringBuilder scenes = new StringBuilder();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    if (i > 0) scenes.Append(',');
                    scenes.Append(Quote(SceneManager.GetSceneAt(i).name));
                }
                double displacement = _trackedNpc == null ? 0 : Vector3.Distance(_npcStart, _trackedNpc.transform.position) / 2.8;
                string json = "{\"schema\":1,\"success\":" + (success ? "true" : "false")
                    + ",\"error\":" + Quote(_error) + ",\"seed\":" + _seed
                    + ",\"phase\":" + Quote(_clock == null ? "configure" : _clock.Phase.ToString())
                    + ",\"scenes\":[" + scenes + "],\"frames\":" + (_clock == null ? 0 : _clock.Frames)
                    + ",\"photon_offline\":" + (_offline != null && (bool)_offline.GetValue(null, null) ? "true" : "false")
                    + ",\"photon_master\":" + (_master != null && (bool)_master.GetValue(null, null) ? "true" : "false")
                    + ",\"wait_seconds\":" + Number(_clock == null ? 0 : _clock.Duration)
                    + ",\"frame_avg_ms\":" + Number(_clock == null || _clock.Frames == 0 ? 0 : _clock.FrameTotal * 1000 / _clock.Frames)
                    + ",\"frame_max_ms\":" + Number(_clock == null ? 0 : _clock.FrameMax * 1000)
                    + ",\"npc_count_at_load\":" + _npcAtLoad + ",\"npc_state_actions\":" + (_aiActions - _aiAtLoad)
                    + ",\"tracked_npc_displacement_m\":" + Number(displacement)
                    + ",\"screenshot\":" + Quote(ScreenshotName) + ",\"screenshot_exists\":" + (File.Exists(Path.Combine(_output, ScreenshotName)) ? "true" : "false") + "}";
                File.WriteAllText(Path.Combine(_output, "metrics.json"), json, new UTF8Encoding(false));
                RevivalPlugin.L.LogInfo("OfflineStart: metrics written; success=" + success);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("OfflineStart: metrics write failed: " + ex.Message); }
        }

        static string Number(double n) { return n.ToString("0.######", CultureInfo.InvariantCulture); }
        static string Quote(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\""; }
    }

    // Test-session preferences stay in memory; the real game's registry is untouched.
    internal static class OfflinePrefs
    {
        static readonly Dictionary<string, object> Values = new Dictionary<string, object>();
        public static void SetInt(string k, int v) { Values[k] = v; }
        public static void SetFloat(string k, float v) { Values[k] = v; }
        public static void SetString(string k, string v) { Values[k] = v; }
        public static int GetInt(string k) { return GetInt(k, 0); }
        public static int GetInt(string k, int d) { object v; return Values.TryGetValue(k, out v) && v is int ? (int)v : d; }
        public static float GetFloat(string k) { return GetFloat(k, 0); }
        public static float GetFloat(string k, float d) { object v; return Values.TryGetValue(k, out v) && v is float ? (float)v : d; }
        public static string GetString(string k) { return GetString(k, ""); }
        public static string GetString(string k, string d) { object v; return Values.TryGetValue(k, out v) && v is string ? (string)v : d; }
        public static bool HasKey(string k) { return Values.ContainsKey(k); }
        public static void DeleteKey(string k) { Values.Remove(k); }
        public static void DeleteAll() { Values.Clear(); }
        public static void Save() { }
    }
}

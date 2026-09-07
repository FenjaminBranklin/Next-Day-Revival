using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // This is accidental-version-drift protection, not an anti-cheat protocol.
    // A process launched by a legacy script has no receipt and cannot claim
    // verified admission. The master server also rejects clients with no mod.
    public static class ClientIntegrity
    {
        static string _error = "";
        static string _token = "ndr1|invalid";
        static bool _checking;
        static bool _resumeAuth;
        static volatile bool _finished;
        static string _checkError;
        static object _backend;
        static MethodInfo _auth;
        const string Download = "https://github.com/FenjaminBranklin/Next-Day-Revival/releases/latest";

        public static void Install(Harmony harmony)
        {
            try
            {
                Type backend = RevivalPlugin.TypeByName("BackendManager");
                Type request = RevivalPlugin.TypeByName("MasterServerMessages.MsgAuthRequest");
                if (request == null) request = Assembly.Load("ClientNet").GetType("MasterServerMessages.MsgAuthRequest");
                MethodInfo auth = AccessTools.Method(backend, "MessageAuthRequest", Type.EmptyTypes, null);
                MethodInfo version = AccessTools.PropertySetter(request, "clientVersion");
                if (auth == null || version == null) throw new Exception("Authentication hooks were not found.");
                _auth = auth;
                BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                harmony.Patch(auth, new HarmonyMethod(typeof(ClientIntegrity).GetMethod("BeforeAuth", flags)), null, null, null, null);
                harmony.Patch(version, new HarmonyMethod(typeof(ClientIntegrity).GetMethod("VersionPrefix", flags)), null, null, null, null);
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        static void Fail(string message)
        {
            _token = "ndr1|invalid";
            _error = "Client verification failed: " + message
                + "\nClose the game and use the current Launcher from GitHub."
                + "\nAn old launcher or a direct Steam start cannot verify this installation.";
            RevivalPlugin.L.LogError("ClientIntegrity: " + _error);
        }

        static bool BeforeAuth(object __instance)
        {
            if (_resumeAuth) { _resumeAuth = false; return true; }
            if (_checking) return false;
            _checking = true;
            _finished = false;
            _backend = __instance;
            _checkError = "";
            _error = "Verifying Steam and mod files before connecting. Please wait...";
            string receipt = Environment.GetEnvironmentVariable("NDR_VERIFIED_RECEIPT");
            string game = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string plugin = typeof(ClientIntegrity).Assembly.Location;
            ThreadPool.QueueUserWorkItem(delegate(object unused) {
                try { VerifyReceipt(receipt, RevivalPlugin.VERSION, game, plugin); }
                catch (Exception ex) { _checkError = ex.Message; }
                finally { _finished = true; }
            });
            return false;
        }

        public static void Tick()
        {
            if (!_checking || !_finished) return;
            _checking = false;
            if (_checkError.Length != 0) { Fail(_checkError); return; }
            try
            {
                _token = "ndr1|" + RevivalPlugin.VERSION;
                _error = "";
                RevivalPlugin.L.LogInfo("ClientIntegrity: all launch files verified before authentication.");
                _resumeAuth = true;
                _auth.Invoke(_backend, null);
            }
            catch (Exception ex) { Fail(ex.Message); }
            finally { _resumeAuth = false; _backend = null; }
        }

        static void VersionPrefix(ref string __0) { __0 = _token; }

        // Kept free of Unity calls so the actual validator is exercised by the
        // offline regression test, including mutations after receipt creation.
        internal static void VerifyReceipt(string receipt, string version, string game, string plugin)
        {
            if (string.IsNullOrEmpty(receipt) || !File.Exists(receipt))
                throw new Exception("No verified launch receipt.");
            string[] lines = File.ReadAllLines(receipt);
            if (lines.Length < 3) throw new Exception("Incomplete launch receipt.");
            string[] header = lines[0].Split('\t');
            string root = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (header.Length != 3 || header[0] != "NDR1" || header[1] != version ||
                !string.Equals(Path.GetFullPath(header[2]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Receipt is for another client.");
            bool foundPlugin = false;
            bool foundRoutes = false;
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            using (SHA256 sha = SHA256.Create())
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] row = lines[i].Split('\t');
                    bool steam = row.Length == 4 && row[1].Length == 40;
                    if ((!steam && (row.Length != 2 || row[1].Length != 64)) || Path.IsPathRooted(row[0]))
                        throw new Exception("Invalid receipt entry.");
                    string path = Path.GetFullPath(Path.Combine(root, row[0].Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || seen.ContainsKey(path))
                        throw new Exception("Unsafe or duplicate receipt entry.");
                    seen[path] = true;
                    if (steam)
                    {
                        FileInfo info = new FileInfo(path);
                        if (!info.Exists || info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) != row[2] ||
                            info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) != row[3])
                            throw new Exception("Steam file changed since launch verification: " + row[0]);
                        continue;
                    }
                    string hash;
                    using (FileStream stream = File.OpenRead(path))
                        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(hash, row[1], StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Changed file: " + row[0]);
                    if (string.Equals(path, Path.GetFullPath(plugin), StringComparison.OrdinalIgnoreCase)) foundPlugin = true;
                    if (row[0] == "BepInEx/plugins/assets/ndr_routes.tsv") foundRoutes = true;
                }
            }
            if (!foundPlugin || !foundRoutes) throw new Exception("Plugin or world data was not verified.");
            string assets = Path.Combine(root, "BepInEx/plugins/assets");
            foreach (string path in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
                if (!seen.ContainsKey(Path.GetFullPath(path))) throw new Exception("Unexpected asset: " + Path.GetFileName(path));
        }

        public static void Draw()
        {
            if (_error.Length == 0) return;
            GUILayout.BeginArea(new Rect(30, 70, Math.Min(720, Screen.width - 60), 190), GUI.skin.box);
            GUILayout.Label(_error);
            if (!_checking && GUILayout.Button("Open current GitHub download")) Application.OpenURL(Download);
            GUILayout.EndArea();
        }
    }
}

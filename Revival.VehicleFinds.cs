using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    // Room properties survive a master change; scene objects survive the player
    // who found them. No local asset is an authoritative runtime data store.
    internal static class VehicleFinds
    {
        internal sealed class Spot
        {
            internal string Id, Kind;
            internal Vector3 At;
            internal float Yaw, Search, Clear;
            internal bool East, Exact;
        }
        static List<Spot> _spots = new List<Spot>();
        static float _next;
        static int _cursor;
        static string _error = "";
        static readonly Dictionary<string, string> _status = new Dictionary<string, string>();
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        internal static List<Spot> Parse(string[] lines)
        {
            List<Spot> result = new List<Spot>();
            HashSet<string> ids = new HashSet<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] c = line.Split('\t');
                if (c.Length != 9 || !System.Text.RegularExpressions.Regex.IsMatch(c[0], "^[a-zA-Z0-9_-]{1,64}$")
                    || !ids.Add(c[0]) || result.Count >= 128) throw new IOException("Invalid vehicle find row");
                if (Array.IndexOf(new string[] { "ural", "tank", "btr", "technical", "howitzer", "gepard", "mi8" }, c[1]) < 0)
                    throw new IOException("Unknown vehicle find kind");
                Spot s = new Spot(); s.Id = c[0]; s.Kind = c[1];
                s.At = new Vector3(Number(c[2], -2500, 7500), 0, Number(c[3], -2500, 2500));
                s.Yaw = Number(c[4], 0, 360); s.Search = Number(c[6], 0, 200); s.Clear = Number(c[7], 16, 45);
                if ((c[5] != "exact" && c[5] != "search") || (c[8] != "0" && c[8] != "1"))
                    throw new IOException("Invalid vehicle find placement");
                s.Exact = c[5] == "exact"; s.East = c[8] == "1"; result.Add(s);
            }
            return result;
        }
        static float Number(string value, float min, float max)
        {
            float n;
            if (!float.TryParse(value, NumberStyles.Float, CI, out n) || float.IsNaN(n) || float.IsInfinity(n) || n < min || n > max)
                throw new IOException("Invalid vehicle find number");
            return n;
        }
        internal static void Load(string[] lines) { _spots = Parse(lines); _cursor = 0; }

        static object Room()
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            return photon == null ? null : AccessTools.PropertyGetter(photon, "room").Invoke(null, null);
        }
        static void Save(object room, string key, double[] state)
        {
            MethodInfo set = AccessTools.Method(room.GetType(), "SetCustomProperties", null, null);
            IDictionary data = (IDictionary)Activator.CreateInstance(set.GetParameters()[0].ParameterType);
            data[key] = state;
            set.Invoke(room, new object[] { data, null, false });
        }
        static GameObject Find(int id)
        {
            if (id <= 0) return null;
            Type view = RevivalPlugin.TypeByName("PhotonView");
            Component c = AccessTools.Method(view, "Find", new Type[] { typeof(int) }, null).Invoke(null, new object[] { id }) as Component;
            return c == null ? null : c.gameObject;
        }
        static bool Dead(GameObject go, string kind)
        {
            if (go == null) return true;
            Component car = go.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem"));
            return car == null ? PlayerHeli.FindWreck(go)
                : Convert.ToSingle(AccessTools.Field(car.GetType(), "Durability").GetValue(car)) <= 0;
        }
        internal static bool Owns(GameObject go)
        {
            if (go == null) return false;
            try
            {
                object room = Room(); if (room == null) return false;
                IDictionary props = (IDictionary)AccessTools.PropertyGetter(room.GetType(), "CustomProperties").Invoke(room, null);
                int id = PlayerHeli.ViewOf(go);
                foreach (DictionaryEntry entry in props)
                {
                    if (!(entry.Key is string) || !((string)entry.Key).StartsWith("ndr.find.")) continue;
                    double[] state = entry.Value as double[];
                    if (state != null && state.Length == 7 && (state[0] == id || state[6] == id)) return true;
                }
            }
            catch { }
            return false;
        }
        internal static string Report()
        {
            string result = "Vehicle finds: " + _spots.Count + " published; "
                + (RevivalTroopInsertion.MasterClient() ? "master" : "spawns run on master") + ".";
            GameObject me = MapTools.LocalPlayer(); Spot nearest = null; float distance = float.MaxValue;
            foreach (Spot s in _spots)
            {
                float d = me == null ? 0 : Distance(me.transform.position, s.At);
                if (d < distance) { nearest = s; distance = d; }
            }
            if (nearest != null)
            {
                string status; if (!_status.TryGetValue(nearest.Id, out status)) status = "not checked on this client yet";
                result += " Nearest " + nearest.Id + ": " + status + ".";
            }
            if (_error.Length > 0) result += " " + _error;
            return result;
        }
        static string Kind(GameObject go)
        {
            Transform t = go.transform;
            if (Tank.IstPanzer(t)) return "tank";
            if (Gepard.IstGepard(t)) return "gepard";
            if (ArtyVehicle.IstArty(t)) return "howitzer";
            if (UralTruck.IstUral(t)) return "ural";
            if (Technical.IstTechnical(t)) return "technical";
            return go.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem")) == null ? "mi8" : "btr";
        }
        static bool AtCap(IDictionary props, string kind)
        {
            int spots = 0, alive = 0;
            foreach (Spot s in _spots) if (s.Kind == kind) spots++;
            HashSet<int> seen = new HashSet<int>();
            foreach (DictionaryEntry entry in props)
            {
                if (!(entry.Key is string) || !((string)entry.Key).StartsWith("ndr.find.")) continue;
                double[] state = entry.Value as double[];
                if (state == null || state.Length != 7) continue;
                foreach (int index in new int[] { 0, 6 })
                {
                    int id = (int)state[index]; if (!seen.Add(id)) continue;
                    GameObject go = Find(id);
                    if (go != null && Kind(go) == kind && !Dead(go, kind)) alive++;
                }
            }
            return alive >= Math.Max(1, spots) * 2;
        }
        static float Distance(Vector3 a, Vector3 b) { a.y = b.y = 0; return (a - b).magnitude; }
        static bool NearPlayer(Vector3 at)
        {
            foreach (GameObject p in Mortar.PlayerList()) if (p != null && Distance(at, p.transform.position) < 300) return true;
            return false;
        }
        static bool Occupied(Vector3 at, float clear, GameObject ignore)
        {
            foreach (Component v in VehicleScan.All())
                if (v != null && v.gameObject != ignore && Distance(at, v.transform.position) < clear) return true;
            foreach (GameObject h in PlayerHeli.FindMachines())
                if (h != null && h != ignore && Distance(at, h.transform.position) < clear) return true;
            return false;
        }
        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            if (_spots.Count == 0 || MapScene.Current != "GW_Scene_1" || MapTools.LocalPlayer() == null
                || !RevivalTroopInsertion.MasterClient()) return;
            Spot s = _spots[_cursor++ % _spots.Count];
            if (s.East && !EastWorld.On) { _status[s.Id] = "east tile unavailable"; return; }
            try
            {
                object room = Room(); if (room == null) return;
                IDictionary props = (IDictionary)AccessTools.PropertyGetter(room.GetType(), "CustomProperties").Invoke(room, null);
                string key = "ndr.find." + s.Id;
                // current view, waiting flag, departure time, actual x/y/z,
                // previous vehicle. Two living vehicles per spot bounds each
                // kind to twice its authored population without deleting loot.
                double[] state = props[key] as double[];
                double now = ArtyRoom.Now();
                if (state == null || state.Length != 7) state = new double[] { 0, 1, now - 3600, s.At.x, 0, s.At.z, 0 };
                GameObject current = Find((int)state[0]), previous = Find((int)state[6]);
                Vector3 home = new Vector3((float)state[3], (float)state[4], (float)state[5]);
                if (!Dead(current, s.Kind) && Distance(home, current.transform.position) < s.Clear)
                {
                    _status[s.Id] = "vehicle present";
                    if (state[1] != 0) { state[1] = 0; Save(room, key, state); }
                    return;
                }
                _status[s.Id] = "waiting for 60 minute cooldown / population cap";
                if (state[1] == 0) { state[1] = 1; state[2] = now; Save(room, key, state); return; }
                if (ArtyRoom.Elapsed(now, state[2]) < 3600 || (!Dead(current, s.Kind) && !Dead(previous, s.Kind)) || AtCap(props, s.Kind)) return;
                _status[s.Id] = "player within 300 units";
                if (NearPlayer(s.At)) return;
                Vector3 at;
                GameObject wreck = Dead(current, s.Kind) ? current : null;
                _status[s.Id] = "waiting for a flat, free, loaded footprint";
                if (!Place(s, wreck, out at) || NearPlayer(at) || Occupied(at, s.Clear, wreck)) return;
                // Only our own destroyed vehicle at its stand is removed. A
                // driven-away vehicle and its inventory remain untouched.
                if (wreck != null && Distance(at, wreck.transform.position) < s.Clear)
                    HeliFlight.NetDestroy(wreck);
                GameObject go;
                if (s.Kind == "mi8") go = PlayerHeli.BuildFound(at, s.Yaw);
                else
                {
                    bool tank;
                    go = VehicleRegistry.Spawn(s.Kind == "howitzer" ? "arty" : s.Kind, at + Vector3.up, Quaternion.Euler(0, s.Yaw, 0), out tank);
                    if (go != null) VehicleCondition.Found(go);
                }
                if (go == null) return;
                int id = PlayerHeli.ViewOf(go);
                if (id <= 0) throw new IOException("Vehicle find has no Photon view");
                Save(room, key, new double[] { id, 0, now, at.x, at.y, at.z,
                    !Dead(current, s.Kind) ? state[0] : (!Dead(previous, s.Kind) ? state[6] : 0) });
                RevivalPlugin.L.LogInfo("VehicleFinds: " + s.Id + " " + s.Kind + " view " + id + " at " + at);
                _status[s.Id] = "spawned view " + id;
                _error = "";
            }
            catch (Exception ex)
            {
                if (_error != ex.Message) { _error = ex.Message; RevivalPlugin.L.LogWarning("VehicleFinds: " + _error); }
            }
        }
        static bool Ground(Vector3 origin, float length, GameObject ignore, out RaycastHit hit)
        {
            hit = new RaycastHit(); float nearest = float.MaxValue; bool found = false;
            foreach (RaycastHit candidate in Physics.RaycastAll(origin, Vector3.down, length))
            {
                Collider c = candidate.collider;
                if (c.isTrigger || (ignore != null && c.transform.IsChildOf(ignore.transform))) continue;
                if (candidate.distance < nearest) { nearest = candidate.distance; hit = candidate; found = true; }
            }
            return found;
        }
        static bool Place(Spot s, GameObject ignore, out Vector3 best)
        {
            best = s.At; float score = float.MaxValue; bool found = false;
            // Golden-angle disc samples: deterministic, bounded, no per-frame scan.
            int count = s.Exact ? 1 : 81;
            for (int i = 0; i < count; i++)
            {
                float r = s.Search * Mathf.Sqrt(i / 80f), angle = i * 2.399963f;
                Vector3 p = s.At + new Vector3(Mathf.Cos(angle) * r, 0, Mathf.Sin(angle) * r);
                if (!s.Exact)
                {
                    NavMeshHit nav;
                    float y;
                    if (!RevivalTroopInsertion.TerrainHeight(p, out y)) continue;
                    p.y = y;
                    if (!NavMesh.SamplePosition(p, out nav, 4, NavMesh.AllAreas)) continue;
                    p = nav.position;
                    if (Distance(s.At, p) > s.Search) continue;
                }
                RaycastHit hit;
                if (!Ground(new Vector3(p.x, 1500, p.z), 3000, ignore, out hit) || hit.normal.y < 0.97f) continue;
                // A search must stay on its walkable surface, not a roof hit
                // by the downward ray above that surface.
                if (!s.Exact && Mathf.Abs(p.y - hit.point.y) > 2) continue;
                p.y = hit.point.y;
                float low = p.y, high = p.y;
                bool ok = true;
                for (int j = 0; j < 8; j++)
                {
                    float a = j * Mathf.PI / 4;
                    Vector3 q = p + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (s.Clear / 2);
                    RaycastHit edge;
                    if (!Ground(q + Vector3.up * 15, 30, ignore, out edge) || edge.normal.y < 0.97f)
                    { ok = false; break; }
                    low = Mathf.Min(low, edge.point.y); high = Mathf.Max(high, edge.point.y);
                }
                if (!ok || high - low > 1.5f || Occupied(p, s.Clear, ignore)) continue;
                foreach (Collider c in Physics.OverlapSphere(p + Vector3.up * (s.Clear / 2 + 1), s.Clear / 2))
                    if (!c.isTrigger && (ignore == null || !c.transform.IsChildOf(ignore.transform))) { ok = false; break; }
                float value = high - low + r * 0.001f;
                if (ok && value < score) { score = value; best = p; best.y = high; found = true; }
            }
            return found;
        }
    }
}

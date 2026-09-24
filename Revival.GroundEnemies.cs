using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    // Online editor groups. All clients retain the definition; only the Photon
    // master spawns or controls NPCs. No writes to installed, verified assets.
    // A group holds its post (waiting), wanders inside its radius (walking),
    // walks the route the editor drew (patrol) or spreads onto a perimeter
    // around its point and holds that (guard).
    internal static class RevivalGroundEnemies
    {
        internal const int MaxGroups = 64, MaxGroupSize = 12, MaxTotal = 128;
        // Tail, up to six bends and head - the same walked polyline the troop
        // arrow uses (troopdef.MAX_ARROW_POINTS).
        internal const int MaxRoutePoints = 8;
        internal sealed class Group
        {
            internal string Name, Faction, Behavior, Key, Meta;
            internal bool Enabled, Seen, Loop;
            internal float X, Z, Radius, Respawn, NextSpawn, Hold;
            internal int Count;
            // The patrol route in world coordinates, empty for the behaviors
            // that never leave their post.
            internal readonly List<Vector3> Route = new List<Vector3>();
            internal readonly List<RevivalComposition.CrewMan> Loadout = new List<RevivalComposition.CrewMan>();
            internal readonly StringBuilder Rows = new StringBuilder();
            internal string Tag { get { return "ground/" + Name; } }
        }

        static List<Group> _groups = new List<Group>();
        static readonly Dictionary<string, string> _running = new Dictionary<string, string>();
        static string[] _source;
        static float _next, _masterReady;
        static bool _wasMaster;
        static object _room;
        static MethodInfo _roomGetter, _masterGetter;
        static Type _npcType;

        internal static List<Group> Parse(string[] lines)
        {
            if (lines == null || lines.Length > 1025) throw new IOException("Too many ground rows");
            List<Group> result = new List<Group>();
            Dictionary<string, Group> names = new Dictionary<string, Group>();
            int total = 0;
            foreach (string line in lines)
            {
                string raw = line.TrimEnd('\r');
                if (raw.Length == 0 || raw[0] == '#') continue;
                string[] c = raw.Split('\t');
                // A snapshot from an editor before the routes carries the nine
                // old metadata columns; it reads as a group without a route.
                if (c.Length == 17) c = WithoutRoute(c);
                if (c.Length != 20 || !Regex.IsMatch(c[0], "^[A-Za-z0-9_.-]{1,64}$"))
                    throw new IOException("Invalid ground row");
                Group g;
                string meta = String.Join("\t", c, 0, 12);
                if (!names.TryGetValue(c[0], out g))
                {
                    if (result.Count >= MaxGroups) throw new IOException("Too many ground groups");
                    g = new Group(); g.Name = c[0]; g.Meta = meta;
                    if (c[1] != "0" && c[1] != "1") throw new IOException("Invalid ground enabled flag");
                    g.Enabled = c[1] == "1";
                    g.X = Number(c[2], -2501f, 2501f); g.Z = Number(c[3], -2501f, 2501f);
                    g.Faction = c[4];
                    if (g.Faction != "traitor" && g.Faction != "looter"
                        && g.Faction != "civilian" && g.Faction != "neutral")
                        throw new IOException("Invalid ground faction");
                    g.Count = Integer(c[5], 1, MaxGroupSize);
                    g.Behavior = c[6];
                    if (g.Behavior != "waiting" && g.Behavior != "walking"
                        && g.Behavior != "patrol" && g.Behavior != "guard")
                        throw new IOException("Invalid ground behavior");
                    g.Radius = Number(c[7], 25f, 500f);
                    g.Respawn = Number(c[8], 5f, 240f) * 60f;
                    Route(c[9], g.Route);
                    if (c[10] != "loop" && c[10] != "pingpong")
                        throw new IOException("Invalid ground route mode");
                    g.Loop = c[10] == "loop";
                    g.Hold = Number(c[11], 0f, 600f);
                    // A patrol without a walkable line is not a patrol. The
                    // editor refuses to save one; a hand-made snapshot is
                    // rejected whole, like every other malformed value here.
                    if (g.Behavior == "patrol" && g.Route.Count < 2)
                        throw new IOException("Ground patrol without a route");
                    if (g.Enabled) total += g.Count;
                    if (total > MaxTotal) throw new IOException("Too many ground soldiers");
                    names.Add(g.Name, g); result.Add(g);
                }
                else if (g.Meta != meta) throw new IOException("Conflicting ground metadata");
                if (g.Loadout.Count >= MaxGroupSize) throw new IOException("Too many ground loadout rows");
                RevivalComposition.CrewMan man = new RevivalComposition.CrewMan();
                man.Role = c[12]; man.Class = "regular"; man.Fpv = false;
                if (man.Role.Length > 0 && !Regex.IsMatch(man.Role, "^[A-Za-z0-9_. -]{1,40}$"))
                    throw new IOException("Invalid ground role");
                int weapon = Integer(c[13], 0, Int32.MaxValue);
                man.Weapons = weapon == 0 ? new int[0] : new int[] { weapon };
                man.Headwear = Integer(c[14], 0, Int32.MaxValue);
                man.Mask = Integer(c[15], 0, Int32.MaxValue);
                man.Body = Integer(c[16], 0, Int32.MaxValue);
                man.Legs = Integer(c[17], 0, Int32.MaxValue);
                man.Hands = Integer(c[18], 0, Int32.MaxValue);
                man.Backpack = Integer(c[19], 0, Int32.MaxValue);
                // The empty editor roster exports one default-kit row.
                g.Loadout.Add(man);
                g.Rows.Append(raw).Append('\n');
            }
            foreach (Group g in result)
                using (SHA256 sha = SHA256.Create())
                    g.Key = g.Name + ":" + BitConverter.ToString(sha.ComputeHash(
                        Encoding.ASCII.GetBytes(g.Rows.ToString()))).Replace("-", "").ToLowerInvariant();
            return result;
        }

        /// <summary>The three route columns a pre-route snapshot has no idea
        /// about, inserted behind the respawn delay: no route, and the values
        /// the editor writes for a group that never walks one.</summary>
        static string[] WithoutRoute(string[] c)
        {
            string[] full = new string[20];
            Array.Copy(c, 0, full, 0, 9);
            full[9] = "-"; full[10] = "pingpong"; full[11] = "0";
            Array.Copy(c, 9, full, 12, 8);
            return full;
        }

        /// <summary>"x,z;x,z;..." - the walk route the editor drew, or "-".
        /// Bounded in length, in point count and in how close two points may
        /// sit, so a published route can never cost more than it says.</summary>
        static void Route(string text, List<Vector3> into)
        {
            if (text.Length == 0 || text == "-") return;
            if (text.Length > 256) throw new IOException("Ground route too long");
            string[] points = text.Split(';');
            if (points.Length > MaxRoutePoints) throw new IOException("Too many ground route points");
            foreach (string point in points)
            {
                string[] xz = point.Split(',');
                if (xz.Length != 2) throw new IOException("Invalid ground route point");
                Vector3 p = new Vector3(Number(xz[0], -2501f, 2501f), 0f,
                    Number(xz[1], -2501f, 2501f));
                if (into.Count > 0 && (into[into.Count - 1] - p).magnitude < 5f)
                    throw new IOException("Ground route leg is too short");
                into.Add(p);
            }
        }

        static float Number(string text, float min, float max)
        {
            float value;
            if (!Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || Single.IsNaN(value) || Single.IsInfinity(value) || value < min || value > max)
                throw new IOException("Invalid ground number");
            return value;
        }

        static int Integer(string text, int min, int max)
        {
            int value;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || value < min || value > max) throw new IOException("Invalid ground integer");
            return value;
        }

        internal static void Load()
        {
            string[] source = LiveRoutes.Ground;
            if (source == null || source == _source) return;
            List<Group> fresh = Parse(source);
            foreach (Group g in fresh)
                foreach (Group old in _groups)
                    if (g.Key == old.Key) { g.Seen = old.Seen; g.NextSpawn = old.NextSpawn; break; }
            _groups = fresh; _source = source;
            RevivalPlugin.L.LogInfo("Ground enemies: loaded " + fresh.Count + " editor group(s).");
        }

        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            try
            {
                Load();
                if (_source == null) return;
                if (_roomGetter == null || _masterGetter == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon == null) return;
                    _roomGetter = AccessTools.PropertyGetter(photon, "room");
                    _masterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                    if (_roomGetter == null || _masterGetter == null) return;
                }
                object room = _roomGetter.Invoke(null, null);
                if (!System.Object.ReferenceEquals(room, _room))
                {
                    _room = room; _wasMaster = false; _running.Clear();
                    foreach (Group g in _groups) { g.Seen = false; g.NextSpawn = 0f; }
                }
                bool master = room != null && (bool)_masterGetter.Invoke(null, null);
                if (!master) { _wasMaster = false; return; }
                if (!_wasMaster)
                {
                    _wasMaster = true;
                    // Allow ownership transfer and cached spawns to arrive before
                    // reconciling a newly joined room or a new master.
                    _masterReady = Time.time + 5f;
                }
                if (Time.time < _masterReady || MapTools.LocalPlayer() == null) return;
                Reconcile();
            }
            catch (Exception ex)
            {
                _next = Time.realtimeSinceStartup + 10f;
                RevivalPlugin.L.LogWarning("Ground enemies: " + ex.Message);
            }
        }

        static void Reconcile()
        {
            Dictionary<string, Group> desired = new Dictionary<string, Group>();
            foreach (Group g in _groups) if (g.Enabled) desired.Add(g.Key, g);
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, string> running in _running)
                if (!desired.ContainsKey(running.Value))
                { NpcWar.StopGround(running.Key); remove.Add(running.Key); }
            foreach (string tag in remove) _running.Remove(tag);

            if (_npcType == null) _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_npcType == null) return;
            Dictionary<string, List<Component>> existing = new Dictionary<string, List<Component>>();
            UnityEngine.Object[] actors = UnityEngine.Object.FindObjectsOfType(_npcType);
            foreach (UnityEngine.Object actor in actors)
            {
                Component ai = actor as Component;
                if (ai == null) continue;
                string key = Crew.GroundKey(ai);
                if (key == null || key.IndexOf('/') >= 0) continue;
                // A key with a slash belongs to a DIFFERENT feature that uses
                // the same spawn-data channel to name its men across clients -
                // the technical's riding crew writes "tech/<view id>". A ground
                // group cannot produce one: grounddef.py restricts a group name
                // to [A-Za-z0-9_.-] and the rest of the key is a hex digest. Not
                // skipping them would have this reconcile destroy every rider on
                // the map as a stale ground group, because it is not in the
                // desired set and never will be.
                if (!desired.ContainsKey(key))
                {
                    // Corpses already belong to NpcWar's loot cleanup queue.
                    if (NpcWar.GroundAlive(ai)) NpcWar.RemoveGroundActor(ai);
                    continue;
                }
                List<Component> men;
                if (!existing.TryGetValue(key, out men))
                { men = new List<Component>(); existing.Add(key, men); }
                men.Add(ai);
            }

            // At most one newly spawned group per second; loaded areas are not
            // stalled by a bulk editor save. Adoption does not create NPCs.
            bool spawned = false;
            foreach (Group g in _groups)
            {
                if (!g.Enabled) continue;
                if (NpcWar.IsActive(g.Tag))
                { g.Seen = true; g.NextSpawn = -1f; _running[g.Tag] = g.Key; continue; }
                List<Component> men;
                if (existing.TryGetValue(g.Key, out men))
                {
                    bool alive = false, owned = true;
                    foreach (Component ai in men)
                    {
                        if (NpcWar.GroundAlive(ai)) alive = true;
                        if (!NpcWar.GroundOwned(ai)) owned = false;
                    }
                    // Even a live actor still transferring ownership reserves
                    // this group. Never create replacements alongside it.
                    if (alive || !g.Seen)
                    {
                        if (!owned) continue;
                        Vector3 home;
                        if (!TryGround(new Vector3(g.X, 0f, g.Z), 20f, out home)) continue;
                        GameObject root = new GameObject("NDR_GroundControl");
                        root.transform.position = home;
                        if (NpcWar.StartGround(g.Tag, root, men.ToArray(), home,
                            g.Behavior, g.Radius, g.Loadout, g.Route, g.Loop, g.Hold))
                        { g.Seen = true; g.NextSpawn = -1f; _running[g.Tag] = g.Key; }
                        else UnityEngine.Object.Destroy(root);
                        continue;
                    }
                }
                if (g.Seen && g.NextSpawn < 0f) g.NextSpawn = Time.time + g.Respawn;
                if (spawned || Time.time < g.NextSpawn) continue;
                spawned = true;
                if (Spawn(g)) { g.Seen = true; g.NextSpawn = -1f; _running[g.Tag] = g.Key; }
                else g.NextSpawn = Time.time + 60f;
            }
        }

        static bool Spawn(Group g)
        {
            Vector3 home;
            if (!TryGround(new Vector3(g.X, 0f, g.Z), 20f, out home))
            {
                RevivalPlugin.L.LogWarning("Ground enemies: " + g.Name + " has no nearby walkable ground; retry in 60 s.");
                return false;
            }
            Vector3[] positions = new Vector3[g.Count];
            for (int i = 0; i < positions.Length; i++)
            {
                float angle = i * Mathf.PI * 2f / g.Count;
                Vector3 candidate = home + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle))
                    * (g.Count == 1 ? 0f : 3f + g.Count * 0.4f);
                if (!TryGround(candidate, 4f, out positions[i])) return false;
                NavMeshPath path = new NavMeshPath();
                if (!NavMesh.CalculatePath(home, positions[i], NavMesh.AllAreas, path)
                    || path.status != NavMeshPathStatus.PathComplete) return false;
            }
            GameObject settlement = Crew.DropGroundSquad(home, positions, g.Faction, g.Loadout, g.Key);
            Array npcs = Crew.Men(settlement);
            if (settlement != null && NpcWar.StartGround(g.Tag, settlement, npcs,
                home, g.Behavior, g.Radius, g.Loadout, g.Route, g.Loop, g.Hold)) return true;
            if (npcs != null)
                foreach (object npc in npcs) NpcWar.RemoveGroundActor(npc as Component);
            if (settlement != null) { Crew.Forget(settlement); UnityEngine.Object.Destroy(settlement); }
            RevivalPlugin.L.LogWarning("Ground enemies: failed to start " + g.Name + "; retry in 60 s.");
            return false;
        }

        internal static bool TryGround(Vector3 point, float search, out Vector3 result)
        {
            result = point;
            // The home map is -2500..2500. In the east world the bound is the
            // terrain itself, so a group can stand on the tile as well.
            if (EastWorld.On ? !EastWorld.OnTerrain(point)
                : (point.x < -2500f || point.x > 2500f || point.z < -2500f || point.z > 2500f)) return false;
            float y;
            if (!RevivalTroopInsertion.TerrainHeight(point, out y)) return false;
            point.y = y;
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(point, out hit, search, NavMesh.AllAreas)
                || Mathf.Abs(hit.position.y - y) > 5f) return false;
            result = hit.position;
            return EastWorld.On ? EastWorld.OnTerrain(result)
                : result.x >= -2500f && result.x <= 2500f && result.z >= -2500f && result.z <= 2500f;
        }
    }
}

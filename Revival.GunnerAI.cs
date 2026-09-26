// Next Day: Survival - Revival Toolkit
//
// GunnerAI - what the NPC gunner of an armed vehicle knows about SEEING and
// MISSING, shared by every AI gun: the patrol and convoy BTRs and tanks
// (Patrol.Gun in Revival.Patrol.cs) and the technical's gunner
// (TechnicalCrew in RevivalTechnicalCrew.cs). The guns keep their own turret,
// ballistics and damage roads; this file answers four questions for them.
//
//   1. WHO. A player who sits in a vehicle is parented under the vehicle's
//      _passengersRootTr (REVERSE_ENGINEERING 18.7), so a sight ray at him ends
//      on his own hull and the old test called that cover. `Carrier` names the
//      hull he sits in, and the guns count a ray that ends on that hull as a
//      ray that reached him. `Weight` is the priority: heavy guns (tank gun,
//      BTR autocannon) prefer vehicles, small arms prefer exposed people.
//
//   2. SIGHT. The game's own infantry sight (NPC_AI2::CanSeeTarget, IL
//      2026-09-26) is one default-layer raycast and knows nothing about
//      vegetation - bushes have no collider, a tree crown has none either. So
//      a gun could aim through a treeline and "snipe" a heli crew at 400 m.
//      `Transmit` walks the terrain trees and bushes (TerrainData.treeInstances,
//      which is how every map here carries its vegetation) along the sight
//      line and returns how much of the target still shows through them. The
//      guns require the raycast AND `Transmit >= SightThreshold`.
//
//   3. SPREAD. `Spread` is a radius in metres around the aim point. Against
//      NPCs it is nearly zero. Against real players it is near zero inside
//      PlayerFreeRange (50 m) and grows with range, with the target's speed and
//      with poor sight. Suppression fire at the last known position of a
//      target that just broke sight uses its own, wide radius.
//
//   4. REACTION. `Reaction` is the time a new target has to be held before the
//      first round, from range and visibility - not a fixed nerf.
//
// Every number is a [GunnerAI] config key; the defaults and the reasoning are
// in docs/ai/tasks/vehicle-gunner-ai.md. `LogShot` writes one line per round.
// Nothing here runs on its own: it is a library the two gunners call.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class GunnerAI
    {
        // ------------------------------------------------------------ tuning

        /// <summary>The live numbers. Field initialisers are the defaults the
        /// config keys are bound with; `Sync` copies the config over them.
        /// Plain fields so the headless check (research/vehicle_gunner_check.py)
        /// compiles the real formulas without BepInEx.</summary>
        internal sealed class Tuning
        {
            // spread against real players, metres of radius
            public float PlayerBase = 0.05f;
            public float PlayerFreeRange = 50f;
            public float PlayerPer100m = 0.30f;
            public float PlayerPer100mSquared = 0.25f;
            public float PlayerMove = 0.06f;
            public float PlayerPoorSight = 1.5f;
            // spread against NPCs
            public float NpcBase = 0.02f;
            public float NpcPer100m = 0.08f;
            public float NpcMove = 0.02f;
            public float NpcPoorSight = 0.5f;
            // suppression and the cap
            public float SuppressSeconds = 3f;
            public float SuppressBase = 2.5f;
            public float SuppressPer100m = 1.5f;
            public float SpreadMax = 8f;
            // reaction
            public float ReactionPlayer = 0.6f;
            public float ReactionNpc = 0.3f;
            public float ReactionPer100m = 0.35f;
            public float ReactionPoorSight = 1.2f;
            // sight
            public float SightThreshold = 0.3f;
            public float TreeDensity = 0.30f;
            public float BushDensity = 0.45f;
            public float FoliageRangeScale = 250f;
            public float ShooterClear = 2.5f;
            // priority (distance multiplier: smaller is preferred)
            public float HeavyVehicle = 0.6f;
            public float HeavySeated = 0.8f;
            public float SmallVehicle = 2.0f;
            public float SmallSeated = 1.3f;
            // damage through a soft-skinned hull to the man sitting in it
            public float SoftPassThrough = 0.5f;
        }

        internal static readonly Tuning T = new Tuning();

        static ConfigEntry<bool> _logShots, _foliage;
        static readonly List<KeyValuePair<ConfigEntry<float>, FieldInfo>> _floats =
            new List<KeyValuePair<ConfigEntry<float>, FieldInfo>>();

        internal static bool LogShots { get { return _logShots == null || _logShots.Value; } }
        static bool FoliageOn { get { return _foliage == null || _foliage.Value; } }

        public static void BindConfig(ConfigFile cfg)
        {
            _logShots = cfg.Bind("GunnerAI", "LogShots", true,
                "One log line per round an NPC vehicle gun fires: gun, target, range, "
                + "sight yes/no, visibility, spread, result. For the acceptance run; "
                + "switch off afterwards to keep the log small.");
            _foliage = cfg.Bind("GunnerAI", "Foliage", true,
                "Terrain trees and bushes block an NPC gunner's sight (the game's own "
                + "sight ray ignores them). Off = raycast only, the old behaviour.");

            F(cfg, "PlayerBase", "Spread radius in metres against a real player inside "
                + "PlayerFreeRange, standing still, in full sight. Near zero: a tank at 30 m hits.");
            F(cfg, "PlayerFreeRange", "Metres up to which the spread against a player does not "
                + "grow with range.");
            F(cfg, "PlayerPer100m", "Linear growth of the player spread, metres per 100 m "
                + "beyond PlayerFreeRange.");
            F(cfg, "PlayerPer100mSquared", "Quadratic growth of the player spread, metres per "
                + "(100 m beyond PlayerFreeRange) squared. 150 m ~ 0.6 m, 400 m ~ 4 m.");
            F(cfg, "PlayerMove", "Extra player spread, metres per (m/s of target speed) per 100 m "
                + "of range. A car at 8 m/s and 100 m adds ~0.5 m.");
            F(cfg, "PlayerPoorSight", "Player spread multiplier at zero visibility is 1 + this "
                + "(scaled by how much foliage is in the way).");
            F(cfg, "NpcBase", "Spread radius in metres against an NPC at any range (base).");
            F(cfg, "NpcPer100m", "NPC spread growth, metres per 100 m of range.");
            F(cfg, "NpcMove", "NPC spread, metres per (m/s of target speed) per 100 m.");
            F(cfg, "NpcPoorSight", "NPC spread multiplier at zero visibility is 1 + this.");
            F(cfg, "SuppressSeconds", "Seconds a gun keeps firing at the last known position "
                + "after the target broke sight. 0 = never fire without sight.");
            F(cfg, "SuppressBase", "Suppression spread radius in metres (base).");
            F(cfg, "SuppressPer100m", "Suppression spread growth, metres per 100 m.");
            F(cfg, "SpreadMax", "Upper limit of any spread radius, metres.");
            F(cfg, "ReactionPlayer", "Seconds a new PLAYER target is held before the first "
                + "round, at 0 m in full sight.");
            F(cfg, "ReactionNpc", "Seconds a new NPC or NPC vehicle target is held before the "
                + "first round, at 0 m in full sight.");
            F(cfg, "ReactionPer100m", "Extra reaction seconds per 100 m of range.");
            F(cfg, "ReactionPoorSight", "Extra reaction seconds at zero visibility (scaled).");
            F(cfg, "SightThreshold", "Visibility (0..1, share of the target showing through "
                + "foliage) a gun needs to see and fire.");
            F(cfg, "TreeDensity", "Optical density of a tree crown per metre of sight line.");
            F(cfg, "BushDensity", "Optical density of a bush per metre of sight line.");
            F(cfg, "FoliageRangeScale", "Foliage counts (1 + range / this) times as much: at "
                + "400 m one bush hides a man, at 30 m it only blurs him.");
            F(cfg, "ShooterClear", "Metres in front of the muzzle in which vegetation is "
                + "ignored (a vehicle standing in a bush still sees out).");
            F(cfg, "HeavyVehicle", "Target priority of a hostile vehicle for heavy guns (tank, "
                + "BTR): its range is multiplied by this, lower = preferred.");
            F(cfg, "HeavySeated", "Priority multiplier of a player sitting in a vehicle, heavy guns.");
            F(cfg, "SmallVehicle", "Priority multiplier of a hostile vehicle for small arms "
                + "(technical MG).");
            F(cfg, "SmallSeated", "Priority multiplier of a player sitting in a vehicle, small arms.");
            F(cfg, "SoftPassThrough", "Share of a round's damage that reaches a player sitting in "
                + "a soft-skinned vehicle (car, truck, UAZ) when the round hits the hull. Armoured "
                + "hulls (tank, BTR) pass nothing.");
            Sync();
        }

        static void F(ConfigFile cfg, string key, string text)
        {
            FieldInfo f = typeof(Tuning).GetField(key);
            if (f == null) return;
            float def = (float)f.GetValue(T);
            _floats.Add(new KeyValuePair<ConfigEntry<float>, FieldInfo>(
                cfg.Bind("GunnerAI", key, def, text), f));
        }

        static float _nextSync;

        /// <summary>Config to Tuning, at most once a second, so a value edited
        /// in the config manager applies without a restart.</summary>
        internal static void Sync()
        {
            if (Time.time < _nextSync && _nextSync > 0f) return;
            _nextSync = Time.time + 1f;
            for (int i = 0; i < _floats.Count; i++)
                _floats[i].Value.SetValue(T, _floats[i].Key.Value);
        }

        // ------------------------------------------------------------ spread

        /// <summary>
        /// Radius in metres of the random displacement around the aim point.
        /// `visibility` is 0..1 (1 = full sight); `speed` the target's speed in
        /// m/s. Suppression ignores the target kind: it is fire at a place.
        /// </summary>
        internal static float Spread(bool player, float dist, float speed, float visibility,
                                     bool suppress)
        {
            dist = Mathf.Max(0f, dist);
            speed = Mathf.Max(0f, speed);
            float hidden = 1f - Mathf.Clamp01(visibility);
            float r;
            if (suppress)
                r = T.SuppressBase + T.SuppressPer100m * dist / 100f;
            else if (player)
            {
                float over = Mathf.Max(0f, dist - T.PlayerFreeRange) / 100f;
                r = T.PlayerBase + T.PlayerPer100m * over + T.PlayerPer100mSquared * over * over
                    + T.PlayerMove * speed * dist / 100f;
                r *= 1f + T.PlayerPoorSight * hidden;
            }
            else
            {
                r = T.NpcBase + T.NpcPer100m * dist / 100f + T.NpcMove * speed * dist / 100f;
                r *= 1f + T.NpcPoorSight * hidden;
            }
            return Mathf.Clamp(r, 0f, Mathf.Max(0.01f, T.SpreadMax));
        }

        /// <summary>Seconds a new target is held before the first round.</summary>
        internal static float Reaction(bool player, float dist, float visibility)
        {
            float hidden = 1f - Mathf.Clamp01(visibility);
            return (player ? T.ReactionPlayer : T.ReactionNpc)
                + T.ReactionPer100m * Mathf.Max(0f, dist) / 100f
                + T.ReactionPoorSight * hidden;
        }

        /// <summary>Priority multiplier on a candidate's range. kind: 0 an
        /// exposed person, 1 a player sitting in a vehicle, 2 a vehicle.</summary>
        internal static float Weight(bool heavy, int kind)
        {
            if (kind == 2) return heavy ? T.HeavyVehicle : T.SmallVehicle;
            if (kind == 1) return heavy ? T.HeavySeated : T.SmallSeated;
            return 1f;
        }

        /// <summary>A point uniformly inside a disc of radius r, perpendicular
        /// to the shot. Uniform in area, so a radius smaller than a man's chest
        /// really is a hit.</summary>
        internal static Vector3 Offset(Vector3 shot, float r)
        {
            if (r <= 0f) return Vector3.zero;
            Vector3 axis = shot.sqrMagnitude < 0.0001f ? Vector3.forward : shot.normalized;
            Vector3 side = Vector3.Cross(Vector3.up, axis);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(axis, side).normalized;
            float a = UnityEngine.Random.value * Mathf.PI * 2f;
            float m = r * Mathf.Sqrt(UnityEngine.Random.value);
            return (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * m;
        }

        // ------------------------------------------------------------ log

        internal static void LogShot(string gun, string target, float range, bool sight,
                                     float visibility, float spread, bool suppress, string result)
        {
            if (!LogShots || RevivalPlugin.L == null) return;
            RevivalPlugin.L.LogInfo("GunnerAI shot: " + gun + " -> " + target
                + " range " + range.ToString("0", CultureInfo.InvariantCulture) + " m"
                + " sight " + (sight ? "yes" : "no")
                + " vis " + visibility.ToString("0.00", CultureInfo.InvariantCulture)
                + " spread " + spread.ToString("0.00", CultureInfo.InvariantCulture) + " m"
                + (suppress ? " SUPPRESS" : "")
                + " -> " + result);
        }

        // ------------------------------------------------------------ vehicles

        static Type _vgs;
        static bool _vgsTried;
        static FieldInfo _passengers;

        static Type Vgs()
        {
            if (!_vgsTried)
            {
                _vgsTried = true;
                _vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (_vgs != null) _passengers = AccessTools.Field(_vgs, "Passengers");
            }
            return _vgs;
        }

        /// <summary>The vehicle this transform sits in, or null. A seated
        /// player hangs under the hull's _passengersRootTr.</summary>
        internal static Component Carrier(Transform t)
        {
            if (t == null || t.parent == null) return null;
            Type v = Vgs();
            if (v == null) return null;
            try { return t.parent.GetComponentInParent(v); }
            catch { return null; }
        }

        /// <summary>Tank or BTR-80A (and everything built on its hull: the
        /// Gepard, the T-72). Everything else is soft-skinned.</summary>
        internal static bool Armoured(Component vehicle)
        {
            if (vehicle == null) return false;
            Transform root = vehicle.transform;
            if (Tank.IstPanzer(root)) return true;
            return root.name.IndexOf("btr", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The player GameObjects in this vehicle's seats.</summary>
        internal static void Occupants(Component vehicle, List<GameObject> into)
        {
            into.Clear();
            if (vehicle == null || Vgs() == null || _passengers == null) return;
            Array seats = null;
            try { seats = _passengers.GetValue(vehicle) as Array; }
            catch { seats = null; }
            if (seats == null) return;
            for (int i = 0; i < seats.Length; i++)
            {
                object seat = seats.GetValue(i);
                GameObject go = seat as GameObject;
                Component c = seat as Component;
                if (go == null && c != null) go = c.gameObject;
                if (go != null) into.Add(go);
            }
        }

        /// <summary>Is this hit on the given hull (or on something under it)?</summary>
        internal static bool OnCarrier(GameObject hit, Component carrier)
        {
            return hit != null && carrier != null && hit.transform.IsChildOf(carrier.transform);
        }

        // ------------------------------------------------------ player damage

        static Type _pnc;
        static PropertyInfo _photonPlayer;
        static MethodInfo _getView, _rpc;
        static bool _damageTried;

        /// <summary>
        /// A bullet into a player, the way NPC_FirearmWeaponController::
        /// FireOneShot sends it: RPC "PlayerApplyDamage" to the victim's own
        /// client (partType 0 body, kind 4 bullet). The same road
        /// Patrol.Gun.SpielerSchaden takes.
        /// </summary>
        internal static bool PlayerDamage(GameObject player, float damage, Vector3 point, Vector3 from)
        {
            if (player == null || damage <= 0f || !DamageLookUp()) return false;
            try
            {
                Component pnc = player.GetComponentInParent(_pnc);
                if (pnc == null) return false;
                object victim = _photonPlayer.GetValue(pnc, null);
                if (victim == null) return false;
                object view = _getView.Invoke(null, new object[] { pnc.gameObject });
                if (view == null) return false;
                _rpc.Invoke(view, new object[] {
                    "PlayerApplyDamage", victim, new object[] { damage, 0, 4, point, from } });
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("GunnerAI: player damage - " + ex.Message);
                return false;
            }
        }

        static bool DamageLookUp()
        {
            if (_damageTried) return _rpc != null;
            _damageTried = true;
            _pnc = RevivalPlugin.TypeByName("PlayerNetworkController");
            Type ext = RevivalPlugin.TypeByName("Extensions");
            Type viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_pnc == null || ext == null || viewType == null) return false;
            _photonPlayer = _pnc.GetProperty("GetPhotonPlayer",
                BindingFlags.Public | BindingFlags.Instance);
            _getView = AccessTools.Method(ext, "GetPhotonView", new Type[] { typeof(GameObject) }, null);
            MethodInfo[] ms = viewType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "RPC") continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length != 3 || ps[0].ParameterType != typeof(string)
                    || ps[2].ParameterType != typeof(object[])
                    || ps[1].ParameterType.Name != "PhotonPlayer") continue;
                _rpc = ms[i];
                break;
            }
            if (_photonPlayer == null || _getView == null) _rpc = null;
            if (_rpc == null)
                RevivalPlugin.L.LogWarning("GunnerAI: the player damage road is incomplete - "
                    + "a round through a car door cannot hurt its driver.");
            return _rpc != null;
        }

        // ------------------------------------------------------------ foliage

        /// <summary>One tree or bush, in world metres. Crown is a vertical
        /// cylinder from Lo to Hi above Y; a trunk (Trunk > 0) is solid below
        /// the crown.</summary>
        internal struct Veg
        {
            public float X, Z, Y, R, Lo, Hi, Trunk, Density;
        }

        const float Cell = 10f;
        static readonly Dictionary<long, List<Veg>> _grid = new Dictionary<long, List<Veg>>();
        static readonly HashSet<long> _seen = new HashSet<long>();
        static float _nextGridCheck;
        static int _gridSignature = int.MinValue;
        static float _maxR = 4f;

        static long Key(int cx, int cz)
        {
            return ((long)cx << 32) ^ (uint)cz;
        }

        /// <summary>
        /// Share (0..1) of a target at `to` that shows through the vegetation
        /// between `from` and it. 1 = nothing in the way. Cost: the cells the
        /// line crosses, a few dozen trees each.
        /// </summary>
        internal static float Transmit(Vector3 from, Vector3 to)
        {
            if (!FoliageOn) return 1f;
            try { Grid(); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GunnerAI: vegetation grid failed - sight is raycast "
                    + "only. " + ex.Message);
                _grid.Clear();
                _gridSignature = int.MaxValue;
                _nextGridCheck = float.MaxValue;
            }
            if (_grid.Count == 0) return 1f;

            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.5f) return 1f;
            float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            float start = Mathf.Clamp01(T.ShooterClear / len);
            float scale = 1f + len / Mathf.Max(1f, T.FoliageRangeScale);

            _seen.Clear();
            float depth = 0f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(flat / (Cell * 0.5f)));
            int reach = Mathf.CeilToInt(_maxR / Cell);
            for (int s = 0; s <= steps; s++)
            {
                float t = (float)s / steps;
                int cx = Mathf.FloorToInt((from.x + d.x * t) / Cell);
                int cz = Mathf.FloorToInt((from.z + d.z * t) / Cell);
                for (int ix = -reach; ix <= reach; ix++)
                    for (int iz = -reach; iz <= reach; iz++)
                    {
                        long k = Key(cx + ix, cz + iz);
                        if (!_seen.Add(k)) continue;
                        List<Veg> list;
                        if (!_grid.TryGetValue(k, out list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float c = Chord(list[i], from, to, start);
                            if (c < 0f) return 0f;               // a trunk
                            depth += c * list[i].Density * scale;
                        }
                        if (depth > 6f) return 0f;
                    }
            }
            return Mathf.Exp(-depth);
        }

        /// <summary>
        /// Metres of the segment from..to (from parameter `start` on) inside
        /// the crown of v, or -1 when it passes through the trunk. Pure
        /// geometry, compiled by the headless check.
        /// </summary>
        internal static float Chord(Veg v, Vector3 from, Vector3 to, float start)
        {
            float dx = to.x - from.x, dz = to.z - from.z;
            float flat2 = dx * dx + dz * dz;
            float len = Mathf.Sqrt(flat2 + (to.y - from.y) * (to.y - from.y));
            if (len < 0.001f) return 0f;
            float px = v.X - from.x, pz = v.Z - from.z;
            float tc, h2;
            if (flat2 < 0.0001f)
            {
                // A vertical line (a heli straight overhead): it is inside the
                // cylinder or not; the part inside is its vertical overlap.
                h2 = px * px + pz * pz;
                if (h2 > v.R * v.R) return 0f;
                float lo = Mathf.Max(Mathf.Min(from.y, to.y), v.Y + v.Lo);
                float hi = Mathf.Min(Mathf.Max(from.y, to.y), v.Y + v.Hi);
                return Mathf.Max(0f, hi - lo);
            }
            tc = (px * dx + pz * dz) / flat2;
            float ex = px - dx * tc, ez = pz - dz * tc;
            h2 = ex * ex + ez * ez;

            float trunk2 = v.Trunk * v.Trunk;
            if (v.Trunk > 0f && h2 < trunk2 && tc > start && tc < 1f)
            {
                float y = from.y + (to.y - from.y) * tc;
                if (y >= v.Y && y < v.Y + v.Lo) return -1f;
            }
            if (h2 >= v.R * v.R) return 0f;

            float half = Mathf.Sqrt(v.R * v.R - h2) / Mathf.Sqrt(flat2);   // in parameter units
            float a = Mathf.Max(start, tc - half);
            float b = Mathf.Min(1f, tc + half);
            if (b <= a) return 0f;
            // Height of the line across that stretch against the crown band.
            float ya = from.y + (to.y - from.y) * a;
            float yb = from.y + (to.y - from.y) * b;
            float bandLo = v.Y + v.Lo, bandHi = v.Y + v.Hi;
            float inside;
            if (Mathf.Abs(yb - ya) < 0.001f)
                inside = ya >= bandLo && ya <= bandHi ? 1f : 0f;
            else
            {
                float lo = Mathf.Max(Mathf.Min(ya, yb), bandLo);
                float hi = Mathf.Min(Mathf.Max(ya, yb), bandHi);
                inside = Mathf.Clamp01((hi - lo) / Mathf.Abs(yb - ya));
            }
            return (b - a) * len * inside;
        }

        /// <summary>
        /// (Re)build the vegetation grid when the set of terrains or the tree
        /// count on any of them changed (RoadClear and Helipads edit trees at
        /// runtime). Checked every five seconds; a rebuild is O(trees) once.
        /// </summary>
        static void Grid()
        {
            if (Time.time < _nextGridCheck) return;
            _nextGridCheck = Time.time + 5f;
            Terrain[] all = Terrain.activeTerrains;
            int sig = 17;
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || all[i].terrainData == null) continue;
                    sig = sig * 31 + all[i].terrainData.GetInstanceID();
                    sig = sig * 31 + all[i].terrainData.treeInstanceCount;
                }
            if (sig == _gridSignature) return;
            _gridSignature = sig;
            _grid.Clear();
            _maxR = 4f;
            if (all == null) return;

            int trees = 0, bushes = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Terrain terrain = all[i];
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData data = terrain.terrainData;
                Vector3 org = terrain.GetPosition();
                Vector3 size = data.size;
                TreePrototype[] protos = data.treePrototypes;
                Veg[] shapes = new Veg[protos == null ? 0 : protos.Length];
                for (int p = 0; p < shapes.Length; p++) shapes[p] = Shape(protos[p]);
                TreeInstance[] inst = data.treeInstances;
                for (int n = 0; n < inst.Length; n++)
                {
                    TreeInstance ti = inst[n];
                    if (ti.prototypeIndex < 0 || ti.prototypeIndex >= shapes.Length) continue;
                    Veg shape = shapes[ti.prototypeIndex];
                    if (shape.Density <= 0f) continue;
                    Veg v = shape;
                    v.X = org.x + ti.position.x * size.x;
                    v.Z = org.z + ti.position.z * size.z;
                    v.Y = org.y + ti.position.y * size.y;
                    v.R = shape.R * ti.widthScale;
                    v.Trunk = shape.Trunk * ti.widthScale;
                    v.Lo = shape.Lo * ti.heightScale;
                    v.Hi = shape.Hi * ti.heightScale;
                    if (v.R > _maxR) _maxR = Mathf.Min(v.R, 12f);
                    long k = Key(Mathf.FloorToInt(v.X / Cell), Mathf.FloorToInt(v.Z / Cell));
                    List<Veg> list;
                    if (!_grid.TryGetValue(k, out list)) { list = new List<Veg>(); _grid.Add(k, list); }
                    list.Add(v);
                    if (shape.Trunk > 0f) trees++; else bushes++;
                }
            }
            RevivalPlugin.L.LogInfo("GunnerAI: vegetation for gunner sight - " + trees
                + " trees, " + bushes + " bushes in " + _grid.Count + " cells.");
        }

        /// <summary>The size of one prototype, out of its meshes. A name with
        /// "bush"/"kust" in it or a plant under 4 m is a bush (crown from the
        /// ground, no trunk); everything else a tree (crown from 35 percent of
        /// its height, a 0.35 m trunk under it). Grass and rocks are skipped.</summary>
        static Veg Shape(TreePrototype proto)
        {
            Veg v = new Veg();
            GameObject prefab = proto == null ? null : proto.prefab;
            string name = prefab == null ? "" : prefab.name.ToLowerInvariant();
            if (name.IndexOf("rock") >= 0 || name.IndexOf("stone") >= 0
                || name.IndexOf("grass") >= 0 || name.IndexOf("kamen") >= 0) return v;

            float height = 0f, radius = 0f;
            if (prefab != null)
            {
                Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
                MeshFilter[] meshes = prefab.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i] == null || meshes[i].sharedMesh == null) continue;
                    Bounds b = meshes[i].sharedMesh.bounds;
                    Matrix4x4 m = toRoot * meshes[i].transform.localToWorldMatrix;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 corner = new Vector3(
                            (c & 1) == 0 ? b.min.x : b.max.x,
                            (c & 2) == 0 ? b.min.y : b.max.y,
                            (c & 4) == 0 ? b.min.z : b.max.z);
                        Vector3 w = m.MultiplyPoint3x4(corner);
                        height = Mathf.Max(height, w.y);
                        radius = Mathf.Max(radius, Mathf.Sqrt(w.x * w.x + w.z * w.z) * 0.75f);
                    }
                }
            }
            bool bush = name.IndexOf("bush") >= 0 || name.IndexOf("kust") >= 0
                || (height > 0f && height < 4f);
            if (bush)
            {
                if (height <= 0f) height = 2f;
                if (radius <= 0f) radius = 1.4f;
                v.R = Mathf.Clamp(radius, 0.5f, 4f);
                v.Lo = 0f;
                v.Hi = Mathf.Clamp(height, 0.8f, 5f);
                v.Trunk = 0f;
                v.Density = T.BushDensity;
            }
            else
            {
                if (height <= 0f) height = 12f;
                if (radius <= 0f) radius = 2.5f;
                v.R = Mathf.Clamp(radius, 1f, 8f);
                v.Lo = Mathf.Clamp(height * 0.35f, 1.2f, 10f);
                v.Hi = Mathf.Clamp(height, 3f, 45f);
                v.Trunk = 0.35f;
                v.Density = T.TreeDensity;
            }
            return v;
        }
    }
}

// Next Day: Survival - Revival Toolkit
//
// FUEL ECONOMY AND THE AIRFIELD POL DEPOT (task 75462e6f82, P3).
// docs/ai/tasks/fuel-economy.md has the research and the numbers.
//
//   FuelBalance   [Fuel]      FuelConsumption and FuelMax per vehicle class
//                             (small car low ... tank high), set the same way on
//                             every client because ExpendFuel runs on all of them.
//   FuelStations  [Fuel]      the map's fuel columns keep a small stock (a few
//                             canisters, then empty) and refill slowly in real
//                             time; the master owns the stock.
//   FuelDepot     [FuelDepot] the five big tanks of the east airfield's POL
//                             depot: health like a vehicle, bullets and every
//                             ExplosionObject lower it, at 0 a blast with fire
//                             and smoke and an empty tank until a long respawn.
//                             The intact tanks hold a large but finite pool that
//                             refills slowly; vehicles parked near the depot and
//                             canisters are filled from it, and the An-2 pump of
//                             Revival.An2Repair.cs draws from it too.
//
// Network: one Photon event ([FuelDepot] NetworkEventCode, 146). The master
// decides everything - damage, pool, grants, station stock - and repeats the
// state in a heartbeat so a late joiner sees the same depot.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // ================================================================ balance

    /// <summary>Fuel use and tank size per vehicle class.</summary>
    public static class FuelBalance
    {
        internal static ConfigEntry<bool> CfgEnabled;
        static readonly Dictionary<string, ConfigEntry<float>> _rate = new Dictionary<string, ConfigEntry<float>>();
        static readonly Dictionary<string, ConfigEntry<float>> _max = new Dictionary<string, ConfigEntry<float>>();

        internal static readonly string[] Classes =
            { "small", "car", "bus", "truck", "btr", "gepard", "tank" };

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "Fuel";
            CfgEnabled = cfg.Bind(S, "Balance", true,
                "Sets fuel use and tank size per vehicle class (the values below). "
                + "Off: every vehicle keeps the game's own numbers.");
            Bind(cfg, "small", 10f, 0f, "zaz/vaz");
            Bind(cfg, "car", 15f, 0f, "UAZ, the technical");
            Bind(cfg, "bus", 25f, 0f, "PAZ bus");
            Bind(cfg, "truck", 35f, 900f, "Ural, the drivable howitzer");
            Bind(cfg, "btr", 45f, 1000f, "BTR-80A");
            Bind(cfg, "gepard", 50f, 1000f, "Gepard");
            Bind(cfg, "tank", 55f, 1200f, "T-72");
        }

        static void Bind(ConfigFile cfg, string cls, float perMinute, float max, string what)
        {
            string name = char.ToUpperInvariant(cls[0]) + cls.Substring(1);
            _rate[cls] = cfg.Bind("Fuel", name + "PerMinute", perMinute,
                "Fuel units per minute at throttle (" + what + "); idle burns half. "
                + "A canister holds 100. 0 = the game's own value.");
            _max[cls] = cfg.Bind("Fuel", name + "TankSize", max,
                "Tank size in fuel units (" + what + "). 0 = the game's own value.");
        }

        /// <summary>The class of a vehicle root by its name; null = not ours to tune.</summary>
        internal static string ClassOf(Transform root)
        {
            if (root == null) return null;
            if (Tank.IstPanzer(root)) return "tank";
            if (Gepard.IstGepard(root)) return "gepard";
            if (ArtyVehicle.IstArty(root) || UralTruck.IstUral(root)) return "truck";
            if (Technical.IstTechnical(root)) return "car";
            string n = root.name.ToLowerInvariant();
            if (n.IndexOf("btr") >= 0) return "btr";
            if (n.IndexOf("ural") >= 0) return "truck";
            if (n.IndexOf("paz") >= 0) return "bus";
            if (n.IndexOf("uaz") >= 0) return "car";
            if (n.IndexOf("zaz") >= 0 || n.IndexOf("vaz") >= 0) return "small";
            return null;
        }

        static Type _vgs;
        static FieldInfo _fuel, _fuelMax, _cons;
        static float _next;
        static readonly HashSet<int> _logged = new HashSet<int>();

        internal static bool LookUp()
        {
            if (_vgs != null) return _fuel != null;
            _vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (_vgs == null) return false;
            _fuel = AccessTools.Field(_vgs, "Fuel");
            _fuelMax = AccessTools.Field(_vgs, "FuelMax");
            _cons = AccessTools.Field(_vgs, "FuelConsumption");
            if (_fuel == null || _fuelMax == null || _cons == null
                || _fuel.FieldType != typeof(float) || _fuelMax.FieldType != typeof(float)
                || _cons.FieldType != typeof(float))
            {
                RevivalPlugin.L.LogWarning("Fuel: VehicleGameSystem.Fuel/FuelMax/FuelConsumption "
                    + "are not plain floats - the fuel balance stays off.");
                _fuel = null;
                return false;
            }
            return true;
        }

        internal static Type VgsType { get { LookUp(); return _vgs; } }
        internal static float Fuel(Component v) { return v == null || !LookUp() ? 0f : (float)_fuel.GetValue(v); }
        internal static float FuelMax(Component v) { return v == null || !LookUp() ? 0f : (float)_fuelMax.GetValue(v); }

        internal static void Tick()
        {
            if (CfgEnabled == null || !CfgEnabled.Value) return;
            if (Time.time < _next) return;
            _next = Time.time + 4f;
            if (!LookUp()) return;
            UnityEngine.Object[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i] as Component;
                if (v == null) continue;
                try { Apply(v); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Fuel: tuning " + v.name + ": " + ex.Message); }
            }
        }

        static void Apply(Component v)
        {
            string cls = ClassOf(v.transform.root);
            if (cls == null) return;
            float perMinute = _rate[cls].Value, max = _max[cls].Value;
            if (perMinute > 0f) _cons.SetValue(v, perMinute / 60f);
            if (max > 0f) _fuelMax.SetValue(v, max);
            // Mod vehicles are spawned with 4000 and the patrol tops up to
            // 4000; above the tank size it is cut down, on every client alike.
            float cap = (float)_fuelMax.GetValue(v);
            if (cap > 0f && (float)_fuel.GetValue(v) > cap + 0.5f) _fuel.SetValue(v, cap);
            int id = v.GetInstanceID();
            if (_logged.Add(id) && _logged.Count < 200)
                RevivalPlugin.L.LogInfo("Fuel: tuned " + v.transform.root.name + " as " + cls
                    + " (" + perMinute + "/min, tank " + Mathf.RoundToInt(cap) + ").");
        }

        /// <summary>Puts fuel into a vehicle the way the game's own canister
        /// does: the SetFuelValue RPC, so the owner and everyone else agree.</summary>
        internal static bool SetFuel(Component v, float value)
        {
            if (v == null || !LookUp()) return false;
            try
            {
                MethodInfo getView = AccessTools.PropertyGetter(v.GetType(), "photonView");
                object view = getView == null ? null : getView.Invoke(v, null);
                MethodInfo rpc = view == null ? null : RpcAll(view.GetType());
                if (rpc != null)
                {
                    object all = Enum.ToObject(rpc.GetParameters()[1].ParameterType, 0);  // PhotonTargets.All
                    rpc.Invoke(view, new object[] { "SetFuelValue", all, new object[] { value } });
                    return true;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Fuel: SetFuelValue RPC: " + ex.Message); }
            _fuel.SetValue(v, value);
            return true;
        }

        static MethodInfo _rpc;
        static MethodInfo RpcAll(Type view)
        {
            if (_rpc != null) return _rpc;
            MethodInfo[] ms = view.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "RPC") continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType.IsEnum && ps[2].ParameterType == typeof(object[]))
                { _rpc = ms[i]; break; }
            }
            return _rpc;
        }
    }

    // =============================================================== stations

    /// <summary>The map's fuel columns: a small stock, a slow refill, one
    /// truth on the master.</summary>
    public static class FuelStations
    {
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<float> CfgCanisters;
        internal static ConfigEntry<float> CfgRefillMinutes;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "Fuel";
            CfgEnabled = cfg.Bind(S, "Stations", true,
                "Fuel columns hold only a few canisters and refill slowly (below). The "
                + "master owns the stock, so every player sees the same empty column.");
            CfgCanisters = cfg.Bind(S, "StationCanisters", 3f,
                "Canisters (100 units each) a full fuel column holds.");
            CfgRefillMinutes = cfg.Bind(S, "StationRefillMinutes", 60f,
                "Real minutes in which an empty column fills up completely again.");
        }

        sealed class Column
        {
            internal Component C;
            internal Vector3 Pos;
            internal float Seen;        // the stock we last wrote or read
        }

        static readonly List<Column> _cols = new List<Column>();
        static Type _type;
        static FieldInfo _stock;
        static float _nextScan, _nextBeat, _lastTick;

        static float Cap { get { return Mathf.Max(0f, CfgCanisters.Value) * 100f; } }

        internal static void Tick()
        {
            if (CfgEnabled == null || !CfgEnabled.Value) return;
            if (_type == null)
            {
                _type = RevivalPlugin.TypeByName("FuelCollumObject");
                if (_type == null) return;
                _stock = AccessTools.Field(_type, "FuelStock");
                if (_stock != null && _stock.FieldType != typeof(float)) _stock = null;
                if (_stock == null) { RevivalPlugin.L.LogWarning("Fuel: FuelCollumObject.FuelStock missing - stations unchanged."); return; }
            }
            if (_stock == null) return;
            FuelDepot.EnsureNet();             // receive the master's stocks
            if (Time.time >= _nextScan) { _nextScan = Time.time + 10f; Scan(); }

            float dt = Time.realtimeSinceStartup - _lastTick;
            _lastTick = Time.realtimeSinceStartup;
            bool master = RevivalTroopInsertion.MasterClient();
            bool changed = false;
            for (int i = _cols.Count - 1; i >= 0; i--)
            {
                Column c = _cols[i];
                if (c.C == null) { _cols.RemoveAt(i); continue; }
                float now = (float)_stock.GetValue(c.C);
                if (now < c.Seen - 1f)
                {
                    // A canister was filled here, on this client.
                    float took = c.Seen - now;
                    c.Seen = now;
                    if (master) changed = true;
                    else FuelDepot.Send(FuelDepot.OpStationTake, new float[] { c.Pos.x, c.Pos.z, took });
                }
                if (master && now < Cap && dt > 0f && dt < 5f)
                {
                    float add = Cap * dt / (Mathf.Max(1f, CfgRefillMinutes.Value) * 60f);
                    now = Mathf.Min(Cap, now + add);
                    _stock.SetValue(c.C, now);
                    c.Seen = now;
                }
            }
            if (master && (changed || Time.time >= _nextBeat)) { _nextBeat = Time.time + 15f; Beat(); }
        }

        static void Scan()
        {
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_type);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null || Find(c) != null) continue;
                Column col = new Column();
                col.C = c;
                col.Pos = c.transform.position;
                float s = Mathf.Min((float)_stock.GetValue(c), Cap);
                _stock.SetValue(c, s);
                col.Seen = s;
                _cols.Add(col);
                RevivalPlugin.L.LogInfo("Fuel: station at " + Mathf.RoundToInt(col.Pos.x) + ", "
                    + Mathf.RoundToInt(col.Pos.z) + " holds " + Mathf.RoundToInt(s) + " of " + Mathf.RoundToInt(Cap) + ".");
            }
        }

        static Column Find(Component c)
        {
            for (int i = 0; i < _cols.Count; i++) if (_cols[i].C == c) return _cols[i];
            return null;
        }

        static Column Near(float x, float z)
        {
            for (int i = 0; i < _cols.Count; i++)
            {
                Column c = _cols[i];
                if (c.C != null && Mathf.Abs(c.Pos.x - x) < 2f && Mathf.Abs(c.Pos.z - z) < 2f) return c;
            }
            return null;
        }

        static void Beat()
        {
            List<float> f = new List<float>();
            for (int i = 0; i < _cols.Count && i < 200; i++)
            {
                if (_cols[i].C == null) continue;
                f.Add(_cols[i].Pos.x); f.Add(_cols[i].Pos.z); f.Add(_cols[i].Seen);
            }
            if (f.Count > 0) FuelDepot.Send(FuelDepot.OpStations, f.ToArray());
        }

        /// <summary>Master: a client filled a canister at a column.</summary>
        internal static void OnTake(float[] f)
        {
            if (_stock == null || f.Length < 3) return;
            Column c = Near(f[0], f[1]);
            if (c == null) return;
            float s = Mathf.Max(0f, (float)_stock.GetValue(c.C) - Mathf.Clamp(f[2], 0f, 100f));
            _stock.SetValue(c.C, s);
            c.Seen = s;
            _nextBeat = 0f;
        }

        /// <summary>Client: the master's stock of every column.</summary>
        internal static void OnStocks(float[] f)
        {
            if (_stock == null) return;
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                Column c = Near(f[i], f[i + 1]);
                if (c == null) continue;
                float s = Mathf.Clamp(f[i + 2], 0f, Mathf.Max(Cap, 100f));
                _stock.SetValue(c.C, s);
                c.Seen = s;
            }
        }
    }

    // ================================================================= depot

    /// <summary>The east airfield's POL depot: five damageable tanks and the
    /// fuel pool they hold.</summary>
    public static class FuelDepot
    {
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgKey;
        internal static ConfigEntry<float> CfgBigHealth, CfgSmallHealth, CfgBigUnits, CfgSmallUnits;
        internal static ConfigEntry<float> CfgRefillMinutes, CfgRespawnMinutes, CfgBurnMinutes;
        internal static ConfigEntry<float> CfgBulletDamage, CfgExplosionFactor, CfgBlastDamage, CfgBlastRadius;
        internal static ConfigEntry<float> CfgRange, CfgVehicleRange, CfgSeconds;
        internal static ConfigEntry<int> CfgEventCode;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "FuelDepot";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The POL depot of the east airfield (D1): five big tanks with health and a "
                + "finite fuel pool. Acts only with [World] EastTile.");
            CfgKey = cfg.Bind(S, "Key", "G", "Key on foot at the depot: refuel the nearest vehicle, else fill a canister.");
            CfgBigHealth = cfg.Bind(S, "BigTankHealth", 2000f, "Health of each of the two vertical tanks (a BTR has 2000).");
            CfgSmallHealth = cfg.Bind(S, "SmallTankHealth", 1200f, "Health of each of the three horizontal tanks.");
            CfgBigUnits = cfg.Bind(S, "BigTankUnits", 6000f, "Fuel units a vertical tank adds to the pool (a canister is 100).");
            CfgSmallUnits = cfg.Bind(S, "SmallTankUnits", 2500f, "Fuel units a horizontal tank adds to the pool.");
            CfgRefillMinutes = cfg.Bind(S, "RefillMinutes", 120f, "Real minutes in which an empty pool fills up again.");
            CfgRespawnMinutes = cfg.Bind(S, "RespawnMinutes", 60f,
                "Real minutes a destroyed tank stays empty and dead before it stands again (empty, then filling).");
            CfgBurnMinutes = cfg.Bind(S, "BurnMinutes", 8f, "How long a destroyed tank burns and smokes ([Effects] Fire).");
            CfgBulletDamage = cfg.Bind(S, "BulletDamage", 5f, "Health one firearm hit takes from a tank.");
            CfgExplosionFactor = cfg.Bind(S, "ExplosionFactor", 1f,
                "Explosion damage x this x (1 - distance / radius) goes onto a tank.");
            CfgBlastDamage = cfg.Bind(S, "BlastDamage", 400f,
                "Damage of the explosion of a destroyed tank (the game's grenade blast; can set a neighbour off). 0 = fire only.");
            CfgBlastRadius = cfg.Bind(S, "BlastRadius", 30f, "Radius of that explosion.");
            CfgRange = cfg.Bind(S, "Range", 90f, "Distance from the depot centre in which the depot can be used.");
            CfgVehicleRange = cfg.Bind(S, "VehicleRange", 30f, "How near the player a vehicle must stand to be refuelled.");
            CfgSeconds = cfg.Bind(S, "FuelSeconds", 6f, "Duration of one refuelling at the depot.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 146, "Photon event code (0..199) of fuel depot and stations.");
        }

        // ------------------------------------------------------------ tanks

        sealed class TankDef
        {
            internal string Id;
            internal Vector3 Pos;       // x, z of the model; y is found on the terrain
            internal bool Big;          // vertical: cylinder; else a box along world X
            internal float Health;
            internal float RespawnAt;   // master clock; 0 = standing
            internal float BurnUntil;   // local clock
            internal GameObject Fire;
            internal bool HaveY;
            internal float Y;
            internal GameObject Model;
            internal Collider[] Shell;
            internal Bounds Bounds;
            internal float NextBind;
        }

        // unity/EastTile/Content/east_af_fuel_water.json; the torn horizontal
        // tank (4102.76, 520.6) is already a wreck and is scenery.
        static readonly TankDef[] _tanks =
        {
            Def("V1", 4053.2f, 526.2f, true), Def("V2", 4053.2f, 573.8f, true),
            Def("H1", 4102.76f, 506.6f, false), Def("H2", 4102.76f, 579.4f, false),
            Def("H3", 4102.76f, 593.4f, false),
        };
        const float BigR = 11.5f, BigH = 26f, SmallHalfX = 14.4f, SmallHalfZ = 4.5f, SmallH = 11.4f;
        internal static readonly Vector3 Centre = new Vector3(4090f, 0f, 550f);

        static TankDef Def(string id, float x, float z, bool big)
        {
            TankDef t = new TankDef();
            t.Id = id; t.Pos = new Vector3(x, 0f, z); t.Big = big; t.Health = -1f;
            return t;
        }

        internal static bool Active
        {
            get { return CfgEnabled != null && CfgEnabled.Value && EastWorld.On; }
        }

        static float MaxHealth(TankDef t) { return t.Big ? CfgBigHealth.Value : CfgSmallHealth.Value; }
        static float Units(TankDef t) { return Mathf.Max(0f, t.Big ? CfgBigUnits.Value : CfgSmallUnits.Value); }
        static bool Intact(TankDef t) { return t.Health > 0f; }

        static float _pool = -1f;
        static float _lastRefill;

        internal static float Capacity
        {
            get
            {
                float c = 0f;
                for (int i = 0; i < _tanks.Length; i++) if (Intact(_tanks[i])) c += Units(_tanks[i]);
                return c;
            }
        }

        static int IntactCount
        {
            get { int n = 0; for (int i = 0; i < _tanks.Length; i++) if (Intact(_tanks[i])) n++; return n; }
        }

        /// <summary>Fuel units in the pool now (master's truth, the last
        /// heartbeat on a client).</summary>
        internal static float Pool
        {
            get { EnsureInit(); return Mathf.Max(0f, _pool); }
        }

        static void EnsureInit()
        {
            if (_pool >= 0f) return;
            for (int i = 0; i < _tanks.Length; i++) if (_tanks[i].Health < 0f) _tanks[i].Health = MaxHealth(_tanks[i]);
            _pool = Capacity;
            _lastRefill = Time.time;
        }

        /// <summary>Master: take up to `units` from the pool; returns what was taken.</summary>
        internal static float Draw(float units)
        {
            EnsureInit();
            float take = Mathf.Clamp(units, 0f, _pool);
            _pool -= take;
            _dirty = true;
            return take;
        }

        static readonly string[] ModelNames = {
            "pol_tank_vertical 1", "pol_tank_vertical_split_roof 1",
            "pol_tank_horizontal 1", "pol_tank_horizontal 2", "pol_tank_horizontal 3"
        };

        static bool TankY(TankDef t)
        {
            // Use the assembled tank's real collision geometry: its seating
            // height includes the bund and model pivot, not just the terrain.
            if (t.Model != null && t.Model.activeInHierarchy) return true;
            t.Shell = null;
            if (Time.time >= t.NextBind)
            {
                t.NextBind = Time.time + 5f;
                int index = Array.IndexOf(_tanks, t);
                GameObject model = GameObject.Find(ModelNames[index]);
                if (model != null)
                {
                    Collider[] all = model.GetComponentsInChildren<Collider>();
                    List<Collider> shell = new List<Collider>();
                    foreach (Collider c in all)
                        if (c != null && c.enabled && !c.isTrigger) shell.Add(c);
                    if (shell.Count > 0)
                    {
                        t.Model = model; t.Shell = shell.ToArray();
                        t.Bounds = t.Shell[0].bounds;
                        for (int i = 1; i < t.Shell.Length; i++) t.Bounds.Encapsulate(t.Shell[i].bounds);
                        t.Y = t.Bounds.min.y; t.HaveY = true;
                        RevivalPlugin.L.LogInfo("FuelDepot: bound " + t.Id + " to " + model.name
                            + " at " + t.Bounds.center + ", " + t.Shell.Length + " colliders.");
                        return true;
                    }
                }
            }
            // Greybox fallback, also while the assembly bundle is loading.
            if (t.HaveY) return true;
            float y;
            if (!EastWorld.TerrainHeight(t.Pos, out y)) return false;
            t.Y = y; t.HaveY = true;
            return true;
        }

        static Vector3 Middle(TankDef t)
        {
            if (t.Shell != null) return t.Bounds.center;
            return new Vector3(t.Pos.x, t.Y + (t.Big ? BigH : SmallH) * 0.5f, t.Pos.z);
        }

        /// <summary>Distance from a point to the tank's shell (0 inside).</summary>
        static float Distance(TankDef t, Vector3 p)
        {
            if (t.Shell != null)
            {
                float nearest = float.MaxValue;
                foreach (Collider c in t.Shell)
                    if (c != null && c.enabled)
                        nearest = Mathf.Min(nearest, Vector3.Distance(p, c.ClosestPoint(p)));
                return nearest;
            }
            float top = t.Y + (t.Big ? BigH : SmallH);
            float dy = Mathf.Max(0f, Mathf.Max(t.Y - p.y, p.y - top));
            float dxz;
            if (t.Big)
            {
                Vector2 d = new Vector2(p.x - t.Pos.x, p.z - t.Pos.z);
                dxz = Mathf.Max(0f, d.magnitude - BigR);
            }
            else
            {
                float dx = Mathf.Max(0f, Mathf.Abs(p.x - t.Pos.x) - SmallHalfX);
                float dz = Mathf.Max(0f, Mathf.Abs(p.z - t.Pos.z) - SmallHalfZ);
                dxz = Mathf.Sqrt(dx * dx + dz * dz);
            }
            return Mathf.Sqrt(dxz * dxz + dy * dy);
        }

        /// <summary>Where a ray enters the tank's shell, or -1.</summary>
        static float RayEnter(TankDef t, Vector3 o, Vector3 d, float range)
        {
            if (t.Shell != null)
            {
                float nearest = range + 1f;
                Ray ray = new Ray(o, d);
                foreach (Collider c in t.Shell)
                {
                    RaycastHit hit;
                    if (c != null && c.enabled && c.Raycast(ray, out hit, range))
                        nearest = Mathf.Min(nearest, hit.distance);
                }
                return nearest <= range ? nearest : -1f;
            }
            float top = t.Y + (t.Big ? BigH : SmallH);
            if (t.Big)
            {
                float ox = o.x - t.Pos.x, oz = o.z - t.Pos.z;
                float a = d.x * d.x + d.z * d.z, b = 2f * (ox * d.x + oz * d.z), c = ox * ox + oz * oz - BigR * BigR;
                if (a < 1e-6f)
                {
                    if (c > 0f || Mathf.Abs(d.y) < 1e-6f) return -1f;
                    float cap = ((d.y < 0f ? top : t.Y) - o.y) / d.y;
                    return cap >= 0f && cap <= range ? cap : -1f;
                }
                float disc = b * b - 4f * a * c;
                if (disc < 0f) return -1f;
                float s = Mathf.Sqrt(disc);
                float t0 = (-b - s) / (2f * a), t1 = (-b + s) / (2f * a);
                float enter = t0 >= 0f ? t0 : (t1 >= 0f ? 0f : -1f);
                if (enter < 0f || enter > range) return -1f;
                // Through the wall between floor and roof, or down onto the roof.
                float y = o.y + d.y * enter;
                if (y >= t.Y && y <= top) return enter;
                if (Mathf.Abs(d.y) < 1e-4f) return -1f;
                float tr = (top - o.y) / d.y;
                if (tr < enter || tr > t1 || tr > range) return -1f;
                return tr;
            }
            Vector3 lo = new Vector3(t.Pos.x - SmallHalfX, t.Y, t.Pos.z - SmallHalfZ);
            Vector3 hi = new Vector3(t.Pos.x + SmallHalfX, top, t.Pos.z + SmallHalfZ);
            float tmin = 0f, tmax = range;
            for (int k = 0; k < 3; k++)
            {
                float ok = o[k], dk = d[k];
                if (Mathf.Abs(dk) < 1e-6f) { if (ok < lo[k] || ok > hi[k]) return -1f; continue; }
                float a1 = (lo[k] - ok) / dk, a2 = (hi[k] - ok) / dk;
                if (a1 > a2) { float x = a1; a1 = a2; a2 = x; }
                tmin = Mathf.Max(tmin, a1);
                tmax = Mathf.Min(tmax, a2);
                if (tmin > tmax) return -1f;
            }
            return tmin;
        }

        // ----------------------------------------------------------- damage

        /// <summary>Any client: an explosion at `point`. The public door for a
        /// weapon that does not go through the game's ExplosionObject (an An-2
        /// bomb, for instance); everything that does is caught by ExplodePostfix.</summary>
        public static void Blast(Vector3 point, float damage, float radius)
        {
            if (!Active || damage <= 0f || radius <= 0f) return;
            EnsureInit();
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                if (!Intact(t) || !TankY(t)) continue;
                float d = Distance(t, point);
                if (d >= radius) continue;
                float dmg = damage * Mathf.Max(0f, CfgExplosionFactor.Value) * (1f - d / radius);
                if (dmg > 0.5f) Report(i, dmg);
            }
        }

        /// <summary>Postfix on ExplosionObject::Explode - that runs on the
        /// explosion's owner only, so one report per bang.</summary>
        public static void ExplodePostfix(object __instance)
        {
            try
            {
                if (!Active) return;
                Component c = __instance as Component;
                if (c == null || !Mine(c)) return;
                Vector3 p = c.transform.position;
                Vector3 flat = p - Centre; flat.y = 0f;
                if (flat.sqrMagnitude > 400f * 400f) return;
                float dmg = ToFloat(AccessTools.Field(c.GetType(), "ExplosionDamage"), c, 0f);
                float rad = ToFloat(AccessTools.Field(c.GetType(), "ExplodeDamageRadius"), c, 6f);
                Blast(p, dmg, rad);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot explosion: " + ex.Message); }
        }

        static FieldInfo _camera;
        static bool _cameraLooked;

        /// <summary>Postfix on PlayerFirearmWeaponController::FireOneShot:
        /// the shooter's camera ray against the tanks, blocked by whatever
        /// stands closer (the same line the drone hit uses).</summary>
        public static void ShotPostfix(object __instance)
        {
            try
            {
                if (!Active || __instance == null) return;
                if (Stinger.IsStinger(__instance) || Drone.Flying) return;
                if (!Mine(__instance)) return;      // a remote copy of somebody's gun
                if (!_cameraLooked)
                {
                    _cameraLooked = true;
                    _camera = AccessTools.Field(__instance.GetType(), "MainCamera");
                }
                Transform cam = _camera == null ? null : _camera.GetValue(__instance) as Transform;
                if (cam == null) return;
                Vector3 o = cam.position, d = cam.forward;
                Vector3 flat = o - Centre; flat.y = 0f;
                if (flat.sqrMagnitude > 900f * 900f) return;
                EnsureInit();
                int best = -1;
                float bestT = 800f;
                for (int i = 0; i < _tanks.Length; i++)
                {
                    TankDef t = _tanks[i];
                    if (!Intact(t) || !TankY(t)) continue;
                    float e = RayEnter(t, o, d, bestT);
                    if (e >= 0f && e < bestT) { bestT = e; best = i; }
                }
                if (best < 0) return;
                RaycastHit hit;
                if (Physics.Raycast(o + d * 0.5f, d, out hit, bestT, ~0, QueryTriggerInteraction.Ignore)
                    && hit.distance < bestT - 3f && Distance(_tanks[best], hit.point) > 3f)
                    return;         // something else stopped the bullet first
                Report(best, Mathf.Max(0f, CfgBulletDamage.Value));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot shot: " + ex.Message); }
        }

        static bool Mine(object mb)
        {
            try
            {
                MethodInfo get = AccessTools.PropertyGetter(mb.GetType(), "photonView");
                object view = get == null ? null : get.Invoke(mb, null);
                if (view == null) return true;
                MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                return isMine == null || (bool)isMine.Invoke(view, null);
            }
            catch { return true; }
        }

        static void Report(int tank, float dmg)
        {
            if (dmg <= 0f) return;
            if (RevivalTroopInsertion.MasterClient()) Damage(tank, dmg);
            else Send(OpDamage, new float[] { tank, dmg });
        }

        /// <summary>Master: health down; at zero the tank blows.</summary>
        static void Damage(int i, float dmg)
        {
            EnsureInit();
            if (i < 0 || i >= _tanks.Length || dmg <= 0f) return;
            TankDef t = _tanks[i];
            if (!Intact(t)) return;
            float capBefore = Capacity;
            t.Health = Mathf.Max(0f, t.Health - Mathf.Min(dmg, 100000f));
            _dirty = true;
            if (t.Health > 0f)
            {
                if (Time.time > _nextDamageLog) { _nextDamageLog = Time.time + 1f; RevivalPlugin.L.LogInfo("FuelDepot: tank " + t.Id + " health " + Mathf.RoundToInt(t.Health) + "."); }
                return;
            }
            // Its share of what is in the pool burns with it.
            if (capBefore > 0f) _pool = Mathf.Max(0f, _pool - _pool * Units(t) / capBefore);
            _pool = Mathf.Min(_pool, Capacity);
            t.RespawnAt = Time.time + Mathf.Max(1f, CfgRespawnMinutes.Value) * 60f;
            RevivalPlugin.L.LogInfo("FuelDepot: tank " + t.Id + " destroyed - " + IntactCount + "/" + _tanks.Length
                + " left, pool " + Mathf.RoundToInt(_pool) + " / " + Mathf.RoundToInt(Capacity) + ".");
            Send(OpExplode, new float[] { i });
            Explode(i, true);
            float bd = CfgBlastDamage.Value;
            if (bd > 0f && TankY(t))
            {
                try { RocketHook.Detonate(Middle(t), bd, Mathf.Max(1f, CfgBlastRadius.Value), 3f); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot blast: " + ex.Message); }
            }
            Broadcast();
        }

        static float _nextDamageLog;

        // -------------------------------------------------------------- FX

        /// <summary>Every client: the fireball (fresh) and the burning wreck.</summary>
        static void Explode(int i, bool fresh)
        {
            TankDef t = _tanks[i];
            float burn = Mathf.Max(0f, CfgBurnMinutes.Value) * 60f;
            if (fresh) t.BurnUntil = Time.time + burn;
            if (RevivalPlugin.CfgFire == null || !RevivalPlugin.CfgFire.Value) return;
            if (!TankY(t)) return;
            try
            {
                if (fresh) FireEffect.SpawnHeliBlast(Middle(t), t.Big ? 40f : 28f);
                if (t.Fire == null && Time.time < t.BurnUntil)
                {
                    float top = t.Shell != null ? t.Bounds.max.y : t.Y + (t.Big ? BigH : SmallH);
                    t.Fire = new GameObject("NDR FuelDepot fire " + t.Id);
                    t.Fire.transform.position = new Vector3(t.Pos.x, top - 1.9f, t.Pos.z);
                    Anchor(t.Fire, Vector3.zero);
                    if (t.Big) { Anchor(t.Fire, new Vector3(6f, 0f, 0f)); Anchor(t.Fire, new Vector3(-6f, 0f, 0f)); }
                    else { Anchor(t.Fire, new Vector3(9f, 0f, 0f)); Anchor(t.Fire, new Vector3(-9f, 0f, 0f)); }
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot fire: " + ex.Message); }
        }

        static void Anchor(GameObject root, Vector3 offset)
        {
            GameObject a = new GameObject("anchor");
            a.transform.position = root.transform.position + offset;
            a.transform.parent = root.transform;
            FireEffect.SpawnWreck(a, true);
        }

        static void TickFire()
        {
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                if (t.Fire == null)
                {
                    if (!Intact(t) && Time.time < t.BurnUntil) Explode(i, false);
                    continue;
                }
                if (Time.time < t.BurnUntil && !Intact(t)) continue;
                FireEffect.StopEmitting(t.Fire);
                UnityEngine.Object.Destroy(t.Fire, 30f);
                t.Fire = null;
            }
        }

        // ------------------------------------------------------------ frame

        static bool _dirty;
        static float _nextBeat;

        internal static void Tick()
        {
            if (!Active) { if (_job) Cancel(null); _prompt = null; _status = null; _tankStatus = null; return; }
            try
            {
                EnsureNet();
                EnsureInit();
                TickFire();
                if (RevivalTroopInsertion.MasterClient()) TickMaster();
                if (Time.time >= _nextAim) { _nextAim = Time.time + 0.1f; LookAtTank(); }
                if (_job) TickJob(); else TickIdle();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("FuelDepot.Tick: " + ex);
                Cancel(null);
            }
        }

        static void TickMaster()
        {
            float dt = Time.time - _lastRefill;
            _lastRefill = Time.time;
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                if (Intact(t) || t.RespawnAt <= 0f || Time.time < t.RespawnAt) continue;
                t.Health = MaxHealth(t);
                t.RespawnAt = 0f;
                _dirty = true;
                RevivalPlugin.L.LogInfo("FuelDepot: tank " + t.Id + " stands again (empty).");
            }
            float cap = Capacity;
            if (_pool < cap && dt > 0f && dt < 5f)
                _pool = Mathf.Min(cap, _pool + cap * dt / (Mathf.Max(1f, CfgRefillMinutes.Value) * 60f));
            if (_pool > cap) _pool = cap;
            if (_dirty || Time.time >= _nextBeat) Broadcast();
        }

        static void Broadcast()
        {
            _dirty = false;
            _nextBeat = Time.time + 10f;
            float[] f = new float[1 + _tanks.Length * 3];
            f[0] = _pool;
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                f[1 + i * 3] = t.Health;
                f[2 + i * 3] = t.RespawnAt > 0f ? Mathf.Max(0f, t.RespawnAt - Time.time) : 0f;
                f[3 + i * 3] = Mathf.Max(0f, t.BurnUntil - Time.time);
            }
            Send(OpState, f);
        }

        static void OnState(float[] f)
        {
            if (f.Length < 1 + _tanks.Length * 3) return;
            EnsureInit();
            _pool = Mathf.Max(0f, f[0]);
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                t.Health = Mathf.Max(0f, f[1 + i * 3]);
                t.RespawnAt = f[2 + i * 3] > 0f ? Time.time + f[2 + i * 3] : 0f;
                float burn = f[3 + i * 3];
                t.BurnUntil = burn > 0f ? Time.time + burn : 0f;
                // A late joiner: the wreck is still burning.
                if (!Intact(t) && burn > 1f && t.Fire == null)
                {
                    Explode(i, false);
                }
            }
        }

        // ---------------------------------------------------- the player

        const string ProgressOwner = "fuel-depot";
        static bool _job;
        static Component _jobVehicle;
        static float _jobStart, _jobLen;
        static string _prompt, _status, _tankStatus;
        static float _nextAim;

        static void LookAtTank()
        {
            _tankStatus = null;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 o = cam.transform.position, d = cam.transform.forward;
            Vector3 flat = o - Centre; flat.y = 0f;
            if (flat.sqrMagnitude > 900f * 900f) return;
            int best = -1; float nearest = 800f;
            for (int i = 0; i < _tanks.Length; i++)
            {
                TankDef t = _tanks[i];
                if (!TankY(t)) continue;
                float enter = RayEnter(t, o, d, nearest);
                if (enter >= 0f && enter < nearest) { nearest = enter; best = i; }
            }
            if (best < 0) return;
            RaycastHit hit;
            if (Physics.Raycast(o, d, out hit, nearest, ~0, QueryTriggerInteraction.Ignore)
                && hit.distance < nearest - 3f && Distance(_tanks[best], hit.point) > 3f) return;
            TankDef aimed = _tanks[best];
            _tankStatus = Loc.T("Цистерна ", "Tank ") + aimed.Id + ": "
                + Mathf.CeilToInt(aimed.Health) + " / " + Mathf.CeilToInt(MaxHealth(aimed))
                + (Intact(aimed) ? " HP" : Loc.T(" - уничтожена", " - destroyed"));
        }

        static float _nextLook;
        static Component _near;
        static int _nextRequest = 1, _waiting;
        static Component _waitVehicle;

        /// <summary>The body freeze of ConvoyFreezeHook holds while refuelling.</summary>
        internal static bool Busy { get { return _job; } }

        static KeyCode Key()
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), CfgKey.Value, true); }
            catch { return KeyCode.G; }
        }

        static void TickIdle()
        {
            if (Time.time >= _nextLook)
            {
                _nextLook = Time.time + 0.5f;
                Look();
            }
            if (_prompt == null || !_prompt.StartsWith("[")) return;
            if (!Input.GetKeyDown(Key())) return;
            Begin(_near);
        }

        static void Look()
        {
            _prompt = null; _status = null; _near = null;
            GameObject me = MapTools.LocalPlayer();
            if (me == null || PlayerAn2.Aboard || PlayerHeli.Aboard || ConvoyRepair.InVehicle()) return;
            Vector3 p = me.transform.position;
            Vector3 d = p - Centre; d.y = 0f;
            if (d.magnitude > Mathf.Max(10f, CfgRange.Value)) return;

            int intact = IntactCount;
            _status = Loc.T("Склад ГСМ: ", "Fuel depot: ") + Mathf.RoundToInt(Pool) + " / " + Mathf.RoundToInt(Capacity)
                + Loc.T("  -  цистерн целы: ", "  -  tanks intact: ") + intact + "/" + _tanks.Length;
            string key = "[" + Key() + "] ";
            if (Pool < 1f)
            {
                _prompt = intact == 0 ? Loc.T("Все цистерны уничтожены - топлива нет", "Every tank is destroyed - no fuel")
                                      : Loc.T("Склад ГСМ пуст - ждите", "The depot is dry - wait for it to refill");
                return;
            }
            if (_waiting != 0) { _prompt = Loc.T("Запрос...", "Asking..."); return; }

            Component v = NearestVehicle(p);
            if (v != null)
            {
                float room = FuelBalance.FuelMax(v) - FuelBalance.Fuel(v);
                _near = v;
                _prompt = key + Loc.T("Заправить технику", "Refuel the vehicle") + " (+"
                    + Mathf.RoundToInt(Mathf.Min(room, Pool)) + ")";
                return;
            }
            int slot; object inv; float energy;
            if (FindCan(out inv, out slot, out energy))
            {
                _prompt = key + Loc.T("Наполнить канистру", "Fill a canister") + " (+"
                    + Mathf.RoundToInt(Mathf.Min(100f - energy, Pool)) + ")";
                return;
            }
            _prompt = Loc.T("Подгоните технику или возьмите канистру", "Park a vehicle here or bring a canister");
        }

        static Component NearestVehicle(Vector3 p)
        {
            Type vt = FuelBalance.VgsType;
            if (vt == null || !FuelBalance.LookUp()) return null;
            UnityEngine.Object[] all = VehicleScan.All();
            Component best = null;
            float bestD = Mathf.Max(3f, CfgVehicleRange.Value);
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i] as Component;
                if (v == null) continue;
                float max = FuelBalance.FuelMax(v);
                if (max <= 0f || FuelBalance.Fuel(v) > max - 1f) continue;
                float dist = Vector3.Distance(v.transform.position, p);
                if (dist < bestD) { bestD = dist; best = v; }
            }
            return best;
        }

        static void Begin(Component vehicle)
        {
            float seconds = Mathf.Max(1f, CfgSeconds.Value);
            string label = vehicle != null ? Loc.T("Заправка техники", "Refuelling the vehicle")
                                           : Loc.T("Наполнение канистры", "Filling a canister");
            if (!NativeActionProgress.Begin(ProgressOwner, label, seconds, true, "repair", "vehicle_repair_01")) return;
            _job = true;
            _jobVehicle = vehicle;
            _jobStart = Time.time;
            _jobLen = seconds;
            _prompt = null;
        }

        static void Cancel(string why)
        {
            if (!string.IsNullOrEmpty(why)) Turret.Hinweis(why, 2f);
            NativeActionProgress.End(ProgressOwner);
            _job = false;
            _jobVehicle = null;
        }

        static void TickJob()
        {
            if (!NativeActionProgress.IsActive(ProgressOwner)) { Cancel(Loc.T("Прервано", "Interrupted")); return; }
            if (Time.time - _jobStart < _jobLen) return;
            Component v = _jobVehicle;
            NativeActionProgress.End(ProgressOwner);
            _job = false;
            _jobVehicle = null;

            float want;
            if (v != null) want = FuelBalance.FuelMax(v) - FuelBalance.Fuel(v);
            else
            {
                object inv; int slot; float energy;
                if (!FindCan(out inv, out slot, out energy)) { Turret.Hinweis(Loc.T("Канистры нет", "No canister to fill"), 2f); return; }
                want = 100f - energy;
            }
            if (want < 1f) { Turret.Hinweis(Loc.T("Уже полон", "Already full"), 2f); return; }
            if (RevivalTroopInsertion.MasterClient()) { Deliver(v, Draw(want)); return; }
            _waiting = _nextRequest++;
            _waitVehicle = v;
            _waitSince = Time.time;
            Send(OpRequest, new float[] { _waiting, want });
        }

        static float _waitSince;

        /// <summary>The granted units go into the vehicle or the canister.</summary>
        static void Deliver(Component v, float units)
        {
            _waiting = 0;
            _waitVehicle = null;
            if (units < 0.5f) { Turret.Hinweis(Loc.T("Склад ГСМ пуст", "The depot is dry"), 2f); return; }
            if (v != null)
            {
                float put = Mathf.Min(FuelBalance.Fuel(v) + units, FuelBalance.FuelMax(v));
                FuelBalance.SetFuel(v, put);
                Turret.Hinweis(Loc.T("Заправлено: +", "Refuelled: +") + Mathf.RoundToInt(units), 3f);
                return;
            }
            object inv; int slot; float energy;
            if (!FindCan(out inv, out slot, out energy)) return;
            MethodInfo fill = AccessTools.Method(inv.GetType(), "FillItemJerryCan", new Type[] { typeof(int), typeof(float) }, null);
            if (fill == null) return;
            fill.Invoke(inv, new object[] { slot, Mathf.Min(100f, energy + units) });
            Turret.Hinweis(Loc.T("Канистра наполнена: +", "Canister filled: +") + Mathf.RoundToInt(units), 3f);
        }

        /// <summary>A canister (10001) in the local pack with room in it:
        /// the game's own FindItemJerryCan(true) and the slot's ItemEnergy.</summary>
        static bool FindCan(out object inv, out int slot, out float energy)
        {
            inv = null; slot = -1; energy = 0f;
            List<object> invs = Turret.PlayerInventories();
            for (int i = 0; i < invs.Count; i++)
            {
                object pim = invs[i];
                MethodInfo find = AccessTools.Method(pim.GetType(), "FindItemJerryCan", new Type[] { typeof(bool) }, null);
                if (find == null) continue;
                int s = (int)find.Invoke(pim, new object[] { true });
                if (s < 0) continue;
                FieldInfo bp = AccessTools.Field(pim.GetType(), "_backpackData");
                object data = bp == null ? null : bp.GetValue(pim);
                FieldInfo en = data == null ? null : AccessTools.Field(data.GetType(), "ItemEnergy");
                Array arr = en == null ? null : en.GetValue(data) as Array;
                if (arr == null || s >= arr.Length) continue;
                inv = pim; slot = s; energy = Mathf.Clamp(ToFloat(arr.GetValue(s), 0f), 0f, 100f);
                return true;
            }
            return false;
        }

        internal static void Draw()
        {
            if (!Active) return;
            try
            {
                if (_waiting != 0 && Time.time - _waitSince > 5f) { _waiting = 0; _waitVehicle = null; }
                if (!string.IsNullOrEmpty(_tankStatus)) Line(_tankStatus, 0.61f);
                if (_job) return;
                if (!string.IsNullOrEmpty(_status)) Line(_status, 0.66f);
                if (!string.IsNullOrEmpty(_prompt)) Line(_prompt, 0.70f);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("FuelDepot.Draw: " + ex); }
        }

        static void Line(string text, float at)
        {
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            float w = size.x + 24f, h = Mathf.Max(24f, size.y + 8f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * at;
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = keep;
            GUI.Label(new Rect(x + 12f, y + (h - size.y) * 0.5f, size.x + 4f, size.y), text);
        }

        // ---------------------------------------------------------- install

        internal static void Install(Harmony h)
        {
            try
            {
                Type ex = RevivalPlugin.TypeByName("ExplosionObject");
                MethodInfo explode = ex == null ? null : AccessTools.Method(ex, "Explode", null, null);
                if (explode != null)
                    h.Patch(explode, null, new HarmonyMethod(typeof(FuelDepot).GetMethod("ExplodePostfix")), null, null, null);
                else RevivalPlugin.L.LogWarning("FuelDepot: ExplosionObject.Explode missing - explosions do not hurt the tanks.");

                Type fw = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                MethodInfo shot = fw == null ? null : AccessTools.Method(fw, "FireOneShot", null, null);
                if (shot != null)
                    h.Patch(shot, null, new HarmonyMethod(typeof(FuelDepot).GetMethod("ShotPostfix")), null, null, null);
                else RevivalPlugin.L.LogWarning("FuelDepot: FireOneShot missing - bullets do not hurt the tanks.");
                ConvoyFreezeHook.Install(h);
                RevivalPlugin.L.LogInfo("FuelDepot: explosion and firearm hooks installed.");
            }
            catch (Exception e) { RevivalPlugin.L.LogError("FuelDepot.Install: " + e); }
        }

        // ---------------------------------------------------------- network

        internal const int OpState = 0, OpStationTake = 1, OpDamage = 2, OpRequest = 3,
                           OpGrant = 4, OpStations = 5, OpExplode = 6;
        const string Tag = "fuel-v1";
        static bool _netReady, _netFailed;
        static MethodInfo _raise;
        static Type _options;

        static byte Code() { return (byte)Mathf.Clamp(CfgEventCode == null ? 146 : CfgEventCode.Value, 0, 199); }

        internal static void EnsureNet()
        {
            if (_netReady || _netFailed) return;
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                FieldInfo ev = photon == null ? null : AccessTools.Field(photon, "OnEventCall");
                if (ev == null) return;
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                _options = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (_raise == null || _options == null)
                {
                    _netFailed = true;
                    RevivalPlugin.L.LogWarning("FuelDepot: RaiseEvent missing - depot and stations stay local.");
                    return;
                }
                Delegate cb = Delegate.CreateDelegate(ev.FieldType, typeof(FuelDepot).GetMethod("OnEvent"));
                ev.SetValue(null, Delegate.Combine(ev.GetValue(null) as Delegate, cb));
                _netReady = true;
                RevivalPlugin.L.LogInfo("FuelDepot: event channel " + Code() + " ready.");
            }
            catch (Exception ex)
            {
                _netFailed = true;
                RevivalPlugin.L.LogError("FuelDepot: network: " + ex);
            }
        }

        internal static void Send(int op, float[] f)
        {
            EnsureNet();
            if (!_netReady) return;
            try
            {
                _raise.Invoke(null, new object[] { Code(), new object[] { Tag, op, f }, true,
                    Activator.CreateInstance(_options) });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot: send: " + ex.Message); }
        }

        public static void OnEvent(byte code, object content, int sender)
        {
            if (!_netReady || code != Code()) return;
            try
            {
                object[] p = content as object[];
                if (p == null || p.Length != 3 || !(p[0] is string) || (string)p[0] != Tag || !(p[1] is int)) return;
                float[] f = p[2] as float[];
                if (f == null || f.Length > 1000) return;
                for (int i = 0; i < f.Length; i++) if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                int op = (int)p[1];
                bool master = RevivalTroopInsertion.MasterClient();
                switch (op)
                {
                    case OpState: if (!master) OnState(f); break;
                    case OpStations: if (!master) FuelStations.OnStocks(f); break;
                    case OpStationTake: if (master) FuelStations.OnTake(f); break;
                    case OpDamage:
                        if (master && Active && f.Length >= 2) Damage((int)f[0], Mathf.Clamp(f[1], 0f, 5000f));
                        break;
                    case OpExplode:
                        if (!master && f.Length >= 1 && (int)f[0] >= 0 && (int)f[0] < _tanks.Length)
                        { _tanks[(int)f[0]].Health = 0f; Explode((int)f[0], true); }
                        break;
                    case OpRequest:
                        if (master && Active && f.Length >= 2)
                        {
                            float got = Draw(Mathf.Clamp(f[1], 0f, 5000f));
                            Send(OpGrant, new float[] { sender, f[0], got });
                            RevivalPlugin.L.LogInfo("FuelDepot: " + Mathf.RoundToInt(got) + " to player " + sender
                                + ", pool " + Mathf.RoundToInt(_pool) + ".");
                        }
                        break;
                    case OpGrant:
                        if (f.Length >= 3 && (int)f[0] == MyActor() && (int)f[1] == _waiting && _waiting != 0)
                            Deliver(_waitVehicle, f[2]);
                        break;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("FuelDepot: event: " + ex.Message); }
        }

        static MethodInfo _playerGet, _idGet;
        static int MyActor()
        {
            try
            {
                if (_playerGet == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null) _playerGet = AccessTools.PropertyGetter(photon, "player");
                }
                object me = _playerGet == null ? null : _playerGet.Invoke(null, null);
                if (me == null) return -1;
                if (_idGet == null) _idGet = AccessTools.PropertyGetter(me.GetType(), "ID");
                return _idGet == null ? -1 : (int)_idGet.Invoke(me, null);
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------- helpers

        static float ToFloat(FieldInfo f, object owner, float fallback)
        {
            if (f == null) return fallback;
            return ToFloat(f.GetValue(owner), fallback);
        }

        /// <summary>A float or an Obscured float, through its implicit operator.</summary>
        internal static float ToFloat(object v, float fallback)
        {
            if (v == null) return fallback;
            if (v is float) return (float)v;
            try
            {
                MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float)
                        && ms[i].GetParameters().Length == 1 && ms[i].GetParameters()[0].ParameterType == v.GetType())
                        return (float)ms[i].Invoke(null, new object[] { v });
            }
            catch { }
            return fallback;
        }
    }
}

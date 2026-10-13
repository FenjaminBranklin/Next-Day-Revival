// Power 2: the power network at run time. Rules: Revival.PowerNetCore.cs;
// sites: Revival.PowerSitesData.cs; design: docs/ai/POWER.md.
//
//   STATE   The master owns one PowerState per declared site: bunker armour,
//           generator hit points, day tank, breaker. It burns the day tank
//           while the generator runs, tops it up from the POL depot
//           (FuelDepot.Draw) and rebuilds a destroyed generator after
//           [Power] RestoreMinutes. Clients mirror the master's snapshot
//           (FlakNet kind 30) sent on every change and every 4 s (late joins,
//           a new master continues from its mirror).
//   HITS    Tagged ordnance reports its impact (Strike): bombs, artillery and
//           Katyusha are heavy, tank shells and FPV drones medium; small arms,
//           grenades, mines and other native blasts never reach the generator.
//           A remote shooter's strike goes to the master (kind 31).
//   LOOK    Every client: the bunker shell (Intact/Cracked/Breached) follows
//           the armour, the diesel hum runs only while the generator does,
//           the door sign's lamp shows on/off/destroyed and its plate reads
//           the status (building %, generator %, day tank %) up close.
//   BREAKER Inside the bunker: [F] held a few seconds pulls or closes it
//           (native progress); the master checks time and place (kind 32).
//   REFUEL  At the day tank's filler: [F] pours one canister from the pack
//           (the game's jerry can calls); the master adds it (kind 33).
//   USERS   PowerNet.Has(consumer) / FeedsGun(gun): the tower radar, the
//           radar-guided AA, the lights, the supply terminal, the screens.
//           A site whose bunker is not loaded on the master is not live and
//           cuts nothing (fail-open: no asset, no outage).
//
// Event-driven: per frame only an edge capture near the breaker; a 4 Hz local
// scan, a 1 Hz master step; strings are built on state change only.
// C# 3.0 (csc from .NET 3.5). ASCII only; Loc.T keeps the Cyrillic.
using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class PowerNet
    {
        internal const int StateMsg = 30, StrikeMsg = 31, BreakerMsg = 32, RefuelMsg = 33;
        const float K = PowerNetCore.K;
        const string Progress = "power-breaker";
        internal const string Scene = "EastAfGenerator";

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<float> CfgTankMinutes, CfgRefill, CfgRestore, CfgOffSeconds, CfgOnSeconds;
        internal static ConfigEntry<KeyCode> CfgKey;

        static readonly PowerSite[] Sites = PowerSitesData.Sites;
        static readonly PowerState[] States = FreshStates();
        static readonly bool[] Live = new bool[Sites.Length];
        static readonly Bound[] Bounds = new Bound[Sites.Length];
        static readonly float[] _state = new float[8], _strike = new float[8], _breaker = new float[8], _refuel = new float[8];
        static readonly Vector4[] Recent = new Vector4[8];
        static int _recent;
        static float _nextMaster, _nextBeat, _nextLocal, _lastStep = -1f;
        static bool _master;

        // local interaction
        static int _near = -1, _nearKind, _sign = -1, _job = -1, _jobKind, _promptKind = -1;
        static bool _jobClose, _pressed, _waitRelease;
        static float _jobStart, _jobEnd;
        static string _prompt, _actionPrompt, _promptStatus;
        static bool _promptClose;

        // master: the one player working each breaker
        static readonly int[] WorkActor = Filled(Sites.Length, -1);
        static readonly bool[] WorkClose = new bool[Sites.Length];
        static readonly float[] WorkStart = new float[Sites.Length];
        static readonly int[] RefuelActor = Filled(Sites.Length, -1);
        static readonly float[] RefuelAt = new float[Sites.Length];

        sealed class Bound
        {
            internal Transform Root, Breaker, Tank;
            internal GameObject[] Shells = new GameObject[3];
            internal AudioSource Hum;
            internal Renderer Lamp;
            internal Material LampOn, LampOff, LampDead;
            internal int Stage = -1, Generator = -1;
            // cached status text: rebuilt only when what it shows changes
            internal string Status;
            internal int StatusKey = int.MinValue;
        }

        static PowerState[] FreshStates()
        {
            PowerState[] a = new PowerState[Sites.Length];
            for (int i = 0; i < a.Length; i++) a[i] = PowerNetCore.Fresh(Sites[i]);
            return a;
        }
        static int[] Filled(int n, int v) { int[] a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        internal static void BindConfig(ConfigFile cfg)
        {
            const string G = "Power";
            CfgEnabled = cfg.Bind(G, "Enabled", true,
                "Power network: the generator bunker feeds the tower radar, radar-guided AA, lights, supply terminal and command-room screens.");
            CfgTankMinutes = cfg.Bind(G, "DayTankMinutes", 45f, "Minutes a full day tank runs the generator.");
            CfgRefill = cfg.Bind(G, "DepotRefillPerMinute", PowerNetCore.RefillPerMinute,
                "Fraction of a full day tank the depot pump adds per minute while the depot has fuel.");
            CfgRestore = cfg.Bind(G, "RestoreMinutes", PowerNetCore.RestoreMinutes,
                "Minutes until a destroyed generator and its bunker are rebuilt.");
            CfgOffSeconds = cfg.Bind(G, "BreakerOffSeconds", PowerNetCore.BreakerOffSeconds, "Seconds to pull the breaker (sabotage).");
            CfgOnSeconds = cfg.Bind(G, "BreakerOnSeconds", PowerNetCore.BreakerOnSeconds, "Seconds to close the breaker again.");
            CfgKey = cfg.Bind(G, "BreakerKey", KeyCode.F, "Key held at the breaker cabinet inside the generator bunker.");
            PowerBunkerDoor.BindConfig(cfg);   // pb-4: the blast door
        }

        internal static bool On { get { return CfgEnabled == null || CfgEnabled.Value; } }

        // ------------------------------------------------------------ users

        /// <summary>False only while a live site feeding this consumer is down.</summary>
        internal static bool Has(int consumer)
        {
            if (!On) return true;
            for (int i = 0; i < Sites.Length; i++)
            {
                if (Live[i] && PowerNetCore.Feeds(Sites[i], consumer) && !PowerNetCore.Powered(States[i])) return false;
                if (PostConsumer(consumer) && PowerNetCore.Feeds(Sites[i], consumer) && Sabotage.PostDown(Sites[i].Id)) return false;
            }
            return true;
        }

        /// <summary>Power 5: is the command post of a live-declared site
        /// wrecked (the terminal names that instead of the generator)?</summary>
        internal static bool CommandPostDown
        {
            get
            {
                if (!On) return false;
                for (int i = 0; i < Sites.Length; i++) if (Sabotage.PostDown(Sites[i].Id)) return true;
                return false;
            }
        }

        /// <summary>Power 5: the command post's screens and supply order
        /// terminal are off while it is wrecked (Sabotage.PostDown).</summary>
        static bool PostConsumer(int consumer)
        {
            return consumer == PowerNetCore.Terminal || consumer == PowerNetCore.Screens;
        }

        /// <summary>Power 4: false only while the named live site feeds this
        /// consumer and is down (an undeclared site falls back to Has).</summary>
        internal static bool SiteHas(string site, int consumer)
        {
            if (!On) return true;
            for (int i = 0; i < Sites.Length; i++)
                if (Sites[i].Id == site)
                    return !(Live[i] && PowerNetCore.Feeds(Sites[i], consumer) && !PowerNetCore.Powered(States[i]))
                        && !(PostConsumer(consumer) && PowerNetCore.Feeds(Sites[i], consumer) && Sabotage.PostDown(site));
            return Has(consumer);
        }

        /// <summary>False only while the live site feeding this AA gun is down.</summary>
        internal static bool FeedsGun(int gun)
        {
            if (!On) return true;
            for (int i = 0; i < Sites.Length; i++)
                if (Live[i] && PowerNetCore.FeedsGun(Sites[i], gun) && !PowerNetCore.Powered(States[i])) return false;
            return true;
        }

        /// <summary>The tower view's power line for the site feeding the radar, or null.</summary>
        internal static string TowerLine(out bool powered)
        {
            powered = true;
            if (!On) return null;
            for (int i = 0; i < Sites.Length; i++)
            {
                if (!Live[i] || !PowerNetCore.Feeds(Sites[i], PowerNetCore.Radar)) continue;
                powered = PowerNetCore.Powered(States[i]);
                return Status(i);
            }
            return null;
        }

        static float TankMinutes(PowerSite site)
        {
            float cfg = CfgTankMinutes == null ? site.TankMinutes : CfgTankMinutes.Value;
            return Mathf.Max(1f, cfg);
        }
        static float Seconds(bool close)
        {
            ConfigEntry<float> c = close ? CfgOnSeconds : CfgOffSeconds;
            return Mathf.Clamp(c == null ? PowerNetCore.BreakerSeconds(close) : c.Value, 0.5f, 30f);
        }

        // ------------------------------------------------------------ binding

        /// <summary>Scene load (AirfieldGround.Loaded): bind each declared
        /// site to its bunker by generator id.</summary>
        internal static void Loaded(Scene scene)
        {
            if (!On || scene.name != Scene) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int s = 0; s < Sites.Length; s++)
                for (int r = 0; r < roots.Length; r++)
                {
                    Transform t = roots[r].transform.Find(Sites[s].Generator);
                    if (t == null) continue;
                    Bind(s, t);
                    break;
                }
        }

        static void Bind(int s, Transform root)
        {
            Bound b = new Bound();
            b.Root = root;
            for (int i = 0; i < 3; i++)
            {
                Transform shell = root.Find(PowerNetCore.StageName[i]);
                b.Shells[i] = shell == null ? null : shell.gameObject;
            }
            b.Breaker = root.Find("Sockets/Breaker");
            b.Tank = root.Find("Sockets/DayTank");
            Transform hum = root.Find("DieselHum");
            b.Hum = hum == null ? null : hum.GetComponent<AudioSource>();
            Sign(b, Sites[s]);
            Bounds[s] = b;
            Apply(s);
            PowerBunkerDoor.Bind(s, root);   // pb-4: the blast door on this bunker
            RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " bound to " + root.name + " (shells "
                + (b.Shells[0] != null) + "/" + (b.Shells[1] != null) + "/" + (b.Shells[2] != null)
                + ", hum " + (b.Hum != null) + ", breaker " + (b.Breaker != null) + ").");
        }

        static bool IsBound(int s) { return Bounds[s] != null && Bounds[s].Root != null; }

        /// <summary>A breaker/tank prompt or job owns the key (the blast door yields).</summary>
        internal static bool Prompting { get { return _near >= 0 || _job >= 0; } }

        /// <summary>The door sign: a painted steel plate with one lamp, built
        /// once from the bunker's own kit material (no new textures).</summary>
        static void Sign(Bound b, PowerSite site)
        {
            Material donor = null;
            Renderer[] rs = b.Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length && donor == null; i++)
            {
                Material[] ms = rs[i].sharedMaterials;   // pb-5 models: several slots per renderer
                for (int j = 0; j < ms.Length && donor == null; j++)
                    if (ms[j] != null && ms[j].name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0) donor = ms[j];
            }
            if (donor == null && rs.Length > 0) donor = rs[0].sharedMaterial;
            GameObject plate = Box("Power sign", b.Root, new Vector3(site.SignX, site.SignY, site.SignZ),
                new Vector3(0.05f, 0.42f, 0.6f) * K, Tint(donor, "NDR power sign", new Color(0.24f, 0.29f, 0.24f), false));
            plate.transform.localRotation = Quaternion.Euler(0f, site.SignYaw, 0f);   // thin side along the wall normal
            Transform lamp = Box("Power sign lamp", plate.transform, new Vector3(0.6f, 0.28f, 0f),
                new Vector3(1.2f, 0.18f, 0.18f), null).transform;
            b.Lamp = lamp.GetComponent<Renderer>();
            b.LampOn = Tint(donor, "NDR power lamp on", new Color(0.35f, 0.9f, 0.3f), true);
            b.LampOff = Tint(donor, "NDR power lamp off", new Color(0.95f, 0.62f, 0.1f), true);
            b.LampDead = Tint(donor, "NDR power lamp dead", new Color(0.18f, 0.05f, 0.05f), false);
        }

        static GameObject Box(string name, Transform parent, Vector3 local, Vector3 scale, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.transform.localScale = scale;
            Renderer r = go.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (m != null) r.sharedMaterial = m;
            return go;
        }

        static Material Tint(Material donor, string name, Color c, bool glow)
        {
            Material m = donor != null ? new Material(donor) : new Material(Shader.Find("Standard"));
            m.name = name; m.color = c;
            if (m.HasProperty("_EmissionColor"))
            {
                if (glow) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 0.8f); }
                else { m.DisableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.black); }
            }
            return m;
        }

        /// <summary>Every client: shells, hum and lamp follow the state, on change only.</summary>
        static void Apply(int s)
        {
            Bound b = Bounds[s];
            if (b == null || b.Root == null) return;
            int stage = PowerNetCore.Stage(States[s], Sites[s]);
            int gen = PowerNetCore.Generator(States[s]);
            if (stage != b.Stage)
            {
                b.Stage = stage;
                // Only touch real siblings: a missing variant keeps the shell shown.
                if (b.Shells[stage] != null)
                    for (int i = 0; i < 3; i++)
                        if (b.Shells[i] != null && b.Shells[i].activeSelf != (i == stage)) b.Shells[i].SetActive(i == stage);
            }
            if (gen != b.Generator)
            {
                b.Generator = gen;
                if (b.Hum != null)
                {
                    if (gen == PowerNetCore.On) { if (!b.Hum.isPlaying) b.Hum.Play(); }
                    else if (b.Hum.isPlaying) b.Hum.Stop();
                }
                if (b.Lamp != null)
                    b.Lamp.sharedMaterial = gen == PowerNetCore.On ? b.LampOn : gen == PowerNetCore.Off ? b.LampOff : b.LampDead;
                AirfieldLights.PowerChanged();   // Power 4: the lights follow at once
            }
        }

        // ------------------------------------------------------------ status

        static string Status(int s)
        {
            Bound b = Bounds[s];
            PowerState st = States[s];
            int gen = PowerNetCore.Generator(st), build = PowerNetCore.BuildingPercent(st, Sites[s]),
                hp = PowerNetCore.Percent(st.GenHp), tank = PowerNetCore.Percent(st.Tank);
            int key = ((((gen * 101 + build) * 101 + hp) * 101 + tank) * 2 + (st.Breaker ? 1 : 0)) * 2 + (Loc.Ru ? 1 : 0);
            if (b == null) { b = new Bound(); Bounds[s] = b; }
            if (key == b.StatusKey && b.Status != null) return b.Status;
            b.StatusKey = key;
            string word = gen == PowerNetCore.On ? Loc.T("РАБОТАЕТ", "ON")
                : gen == PowerNetCore.Destroyed ? Loc.T("УНИЧТОЖЕН", "DESTROYED")
                : !st.Breaker ? Loc.T("ВЫКЛ - РУБИЛЬНИК", "OFF - BREAKER OPEN")
                : Loc.T("ВЫКЛ - НЕТ ТОПЛИВА", "OFF - NO FUEL");
            b.Status = Loc.T("ГЕНЕРАТОР ", "GENERATOR ") + Sites[s].Generator + ": " + word
                + Loc.T("  |  Здание ", "  |  Building ") + build
                + Loc.T("%  |  Генератор ", "%  |  Generator ") + hp
                + Loc.T("%  |  Расходный бак ", "%  |  Day tank ") + tank + "%";
            return b.Status;
        }

        // ------------------------------------------------------------ hits

        /// <summary>Any client, at the impact of tagged ordnance. Only a hit on
        /// a declared bunker costs anything: a few multiplications otherwise.</summary>
        internal static void Strike(Vector3 at, int cls)
        {
            FuelDepot.Strike(at, cls);   // Power 3: the same ordnance on the fragile depot tanks
            if (!On || (cls != PowerNetCore.HitHeavy && cls != PowerNetCore.HitMedium)) return;
            try
            {
                int s = Site(at, cls);
                if (s < 0) return;
                if (Crocodile.IsMaster()) { MasterStrike(s, cls, at); return; }
                float[] p = _strike;
                p[0] = StrikeMsg; p[1] = at.x; p[2] = at.y; p[3] = at.z; p[4] = cls; p[5] = s; p[6] = p[7] = 0f;
                FlakNet.SendPacket(p, true);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PowerNet strike: " + ex.Message); }
        }

        static int Site(Vector3 at, int cls)
        {
            for (int s = 0; s < Sites.Length; s++)
                if (PowerNetCore.OnBuilding(Sites[s], cls, at.x, at.y, at.z, IsBound(s) ? Bounds[s].Root.position.y : float.NaN))
                    return s;
            return -1;
        }

        static void MasterStrike(int s, int cls, Vector3 at)
        {
            if (!Live[s]) return;
            // One bill for one impact reported twice (shooter and a native echo).
            for (int i = 0; i < Recent.Length; i++)
            {
                Vector4 r = Recent[i];
                if (r.w > 0f && Time.time - r.w < 0.15f && (at - new Vector3(r.x, r.y, r.z)).sqrMagnitude < K * K) return;
            }
            Recent[_recent] = new Vector4(at.x, at.y, at.z, Time.time);
            _recent = (_recent + 1) % Recent.Length;
            bool genHit;
            int before = PowerNetCore.Generator(States[s]);
            if (!PowerNetCore.Strike(ref States[s], Sites[s], cls, Time.time, out genHit)) return;
            int after = PowerNetCore.Generator(States[s]);
            RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " " + (cls == PowerNetCore.HitHeavy ? "heavy" : "medium")
                + " hit " + States[s].Hits + " - building " + PowerNetCore.BuildingPercent(States[s], Sites[s])
                + "%, generator " + PowerNetCore.Percent(States[s].GenHp) + "%" + (genHit ? " (generator hit)" : "") + ".");
            if (before != after && after == PowerNetCore.Destroyed)
                RadarScope.Note("GENERATOR " + Sites[s].Generator + " DESTROYED - the airfield has no power.");
            Changed(s);
        }

        // ------------------------------------------------------- sabotage

        /// <summary>Power 5: the bunker frame of a site (its bound root, else
        /// the declared position on the terrain).</summary>
        internal static bool BunkerFrame(string site, out Vector3 origin, out float yaw)
        {
            origin = Vector3.zero; yaw = 0f;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (Sites[s].Id != site) continue;
                if (IsBound(s)) { origin = Bounds[s].Root.position; yaw = Bounds[s].Root.eulerAngles.y; return true; }
                if (!On) return false;
                Vector3 at = new Vector3(Sites[s].X, 0f, Sites[s].Z);
                float y;
                if (!EastWorld.TerrainHeight(at, out y)) return false;
                at.y = y; origin = at; yaw = Sites[s].Yaw;
                return true;
            }
            return false;
        }

        /// <summary>Power 5: is this point inside a declared generator bunker?</summary>
        internal static bool InsideHouse(Vector3 at)
        {
            if (!On) return false;
            for (int s = 0; s < Sites.Length; s++)
                if (PowerNetCore.InsideHouse(Sites[s], at.x, at.y, at.z, IsBound(s) ? Bounds[s].Root.position.y : float.NaN))
                    return true;
            return false;
        }

        /// <summary>Power 5, master: C4 inside the bunker on the generator
        /// (Sabotage.C4Chain) - charges = summed shares; the armour is not touched.</summary>
        internal static void Demolition(string site, float charges, float need)
        {
            if (!On || !Crocodile.IsMaster()) return;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (Sites[s].Id != site || !Live[s]) continue;
                int before = PowerNetCore.Generator(States[s]);
                if (!PowerNetCore.Demolition(ref States[s], charges, need, Time.time)) return;
                RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " C4 on the generator ("
                    + charges.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " of " + need + " charges) - generator "
                    + PowerNetCore.Percent(States[s].GenHp) + "%, building " + PowerNetCore.BuildingPercent(States[s], Sites[s]) + "%.");
                if (before != PowerNetCore.Destroyed && PowerNetCore.Generator(States[s]) == PowerNetCore.Destroyed)
                    RadarScope.Note("GENERATOR " + Sites[s].Generator + " DESTROYED - the airfield has no power.");
                Changed(s);
                return;
            }
        }

        // ------------------------------------------------------------ wire

        static void Changed(int s)
        {
            Apply(s);
            Send(s);
        }

        static void Send(int s)
        {
            if (!Crocodile.IsMaster()) return;
            PowerState st = States[s];
            float[] p = _state;
            p[0] = StateMsg; p[1] = s; p[2] = st.Armour; p[3] = st.GenHp; p[4] = st.Tank;
            p[5] = PowerNetCore.Flags(st) + (Live[s] ? 2f : 0f); p[6] = st.Revision; p[7] = st.Hits;
            FlakNet.SendPacket(p, true);
        }

        internal static void OnPacket(float[] f, int sender)
        {
            if (!On || f.Length < 8) return;
            int kind = Mathf.RoundToInt(f[0]);
            bool master = Crocodile.IsMaster();
            if (kind == StateMsg)
            {
                int s = Mathf.RoundToInt(f[1]);
                if (master || s < 0 || s >= Sites.Length || sender != MercAA.MasterActor()) return;
                int flags = Mathf.RoundToInt(f[5]);
                bool wasPowered = PowerNetCore.Powered(States[s]);
                if (!PowerNetCore.Snapshot(ref States[s], Sites[s], f[2], f[3], f[4], flags & 1, f[6], f[7])) return;
                Live[s] = (flags & 2) != 0;
                if (wasPowered != PowerNetCore.Powered(States[s])) Log(s);
                Apply(s);
            }
            else if (kind == StrikeMsg && master)
            {
                GameObject player = Crocodile.PlayerByActor(sender);
                Vector3 at = new Vector3(f[1], f[2], f[3]);
                int cls = Mathf.RoundToInt(f[4]);
                // A drone or a gun far from its shooter, still in the same world.
                if (player == null || (player.transform.position - at).sqrMagnitude > 20000f * 20000f) return;
                int s = Site(at, cls);
                if (s >= 0) MasterStrike(s, cls, at);
            }
            else if (kind == BreakerMsg && master)
                OnBreaker(Mathf.RoundToInt(f[1]), f[2] > 0.5f, Mathf.RoundToInt(f[3]), sender);
            else if (kind == RefuelMsg && master)
                OnRefuel(Mathf.RoundToInt(f[1]), f[2], sender);
        }

        static void OnBreaker(int s, bool close, int phase, int actor)
        {
            if (s < 0 || s >= Sites.Length || !Live[s]) return;
            GameObject player = Crocodile.PlayerByActor(actor);
            if (phase == 0) { if (WorkActor[s] == actor) WorkActor[s] = -1; return; }
            float reach = PowerNetCore.BreakerReachM * K * 2f;
            if (player == null || !Crocodile.PlayerUp(player) || !IsBound(s)
                || (player.transform.position - BreakerAt(s)).sqrMagnitude > reach * reach) return;
            if (phase == 1)
            {
                if (WorkActor[s] >= 0 && WorkActor[s] != actor && Time.time - WorkStart[s] < 15f) return;
                WorkActor[s] = actor; WorkClose[s] = close; WorkStart[s] = Time.time;
                return;
            }
            if (WorkActor[s] != actor || WorkClose[s] != close) return;
            WorkActor[s] = -1;
            // Master clock: the action took its full time on this side too.
            float need = Seconds(close) - PowerNetCore.BreakerSlack;
            if (Time.time - WorkStart[s] < need || !PowerNetCore.SetBreaker(ref States[s], close)) return;
            RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " breaker " + (close ? "closed" : "pulled") + " by actor " + actor + ".");
            RadarScope.Note(close ? "Generator breaker closed - power back." : "GENERATOR BREAKER PULLED - the airfield has no power.");
            Log(s);
            Changed(s);
        }

        static void Log(int s)
        {
            RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " power " + (PowerNetCore.Powered(States[s]) ? "ON" : "OFF")
                + " (generator " + PowerNetCore.Generator(States[s]) + ").");
        }

        static Vector3 BreakerAt(int s)
        {
            Bound b = Bounds[s];
            if (b.Breaker != null) return b.Breaker.position;
            PowerSite site = Sites[s];
            return b.Root.TransformPoint(new Vector3(site.BreakerX, site.BreakerY, site.BreakerZ));
        }

        // ------------------------------------------------------------ tick

        internal static void Tick()
        {
            PowerBunkerDoor.Tick();   // pb-4: the blast door (its own switch)
            if (!On) return;
            try { TickInner(); }
            catch (Exception ex)
            {
                StopJob(false);
                RevivalPlugin.L.LogWarning("PowerNet: " + ex.Message);
            }
        }

        static void TickInner()
        {
            // Per frame: only the key edge while a breaker is in reach.
            if ((_near >= 0 || _job >= 0) && CfgKey != null && Input.GetKeyDown(CfgKey.Value)) _pressed = true;
            if (_job >= 0) Work();
            if (Time.time < _nextLocal) return;
            _nextLocal = Time.time + 0.25f;
            bool master = Crocodile.IsMaster();
            if (master != _master) { _master = master; _nextBeat = 0f; _lastStep = -1f; }
            // The master's word on which sites are live: its bunker is loaded.
            if (master) for (int s = 0; s < Sites.Length; s++) Live[s] = IsBound(s);
            if (master && Time.time >= _nextMaster) { _nextMaster = Time.time + 1f; MasterStep(); }
            Local();
        }

        static void MasterStep()
        {
            float now = Time.time, dt = _lastStep < 0f ? 0f : Mathf.Min(5f, now - _lastStep);
            _lastStep = now;
            bool beat = now >= _nextBeat;
            if (beat) _nextBeat = now + 4f;
            for (int s = 0; s < Sites.Length; s++)
            {
                if (!Live[s]) { if (beat) Send(s); continue; }
                bool changed = false;
                if (States[s].GenHp <= 0f && States[s].DownSince < 0f) States[s].DownSince = now;
                if (PowerNetCore.Burn(ref States[s], dt, TankMinutes(Sites[s])))
                {
                    changed = true;
                    RadarScope.Note("GENERATOR " + Sites[s].Generator + " OUT OF FUEL - the airfield has no power.");
                    Log(s);
                }
                float want = PowerNetCore.RefillWant(States[s], dt, CfgRefill == null ? PowerNetCore.RefillPerMinute : CfgRefill.Value);
                if (want > 0f)
                {
                    // The depot pump: a dry or burnt-out depot gives nothing.
                    float got = FuelDepot.Active ? FuelDepot.Draw(want * PowerNetCore.TankUnits) / PowerNetCore.TankUnits : want;
                    if (PowerNetCore.Refill(ref States[s], got)) { changed = true; Log(s); }
                }
                if (PowerNetCore.Restore(ref States[s], Sites[s], now, CfgRestore == null ? PowerNetCore.RestoreMinutes : CfgRestore.Value))
                {
                    changed = true;
                    RadarScope.Note("Generator " + Sites[s].Generator + " rebuilt - power back.");
                    Log(s);
                }
                if (changed) Changed(s);
                else if (beat) Send(s);
            }
        }

        static void Local()
        {
            GameObject me = MapTools.LocalPlayer();
            int near = -1, kind = 0, sign = -1;
            if (me != null && Crocodile.PlayerUp(me) && !GameUi.WindowOpen && !RadarScope.InView && Flak._manned == null)
            {
                Vector3 p = me.transform.position;
                float reach = PowerNetCore.BreakerReachM * K, signReach = 4f * K;
                for (int s = 0; s < Sites.Length; s++)
                {
                    if (!Live[s] || !IsBound(s)) continue;
                    Bound b = Bounds[s];
                    if ((p - b.Root.position).sqrMagnitude > 60f * 60f) continue;
                    if (States[s].GenHp > 0f && (p - BreakerAt(s)).sqrMagnitude <= reach * reach) { near = s; kind = 0; }
                    else if (States[s].GenHp > 0f && States[s].Tank < 0.98f && (p - TankAt(s)).sqrMagnitude <= reach * reach) { near = s; kind = 1; }
                    else if (b.Lamp != null && (p - b.Lamp.transform.position).sqrMagnitude <= signReach * signReach) sign = s;
                }
            }
            _near = near; _nearKind = kind; _sign = sign;
            if (_job >= 0) { _prompt = null; return; }
            bool pressed = _pressed; _pressed = false;
            if (_waitRelease && !Input.GetKey(CfgKey.Value)) _waitRelease = false;
            if (near < 0) { _prompt = sign >= 0 ? Status(sign) : null; return; }
            bool close = !States[near].Breaker;
            string status = Status(near);
            if (!ReferenceEquals(status, _promptStatus) || close != _promptClose || kind != _promptKind)
            {
                _promptStatus = status; _promptClose = close; _promptKind = kind;
                _actionPrompt = "[" + CfgKey.Value + "] " + (kind == 1
                    ? Loc.T("Залить топливо в расходный бак (канистра или машина рядом)", "Fill the day tank (canister or a vehicle alongside)")
                    : close ? Loc.T("Включить рубильник генератора", "Close the generator breaker")
                    : Loc.T("Выключить рубильник генератора", "Pull the generator breaker"))
                    + "\n" + status;
            }
            _prompt = _actionPrompt;
            if (!pressed || _waitRelease) return;
            if (kind == 1 && !FuelDepot.HasFuelCan() && !FuelDepot.HasVehicleFuel(TankAt(near)))
            {
                Turret.Hinweis(NeedCan, 2f);
                _waitRelease = true;
                return;
            }
            float seconds = kind == 1 ? PowerNetCore.RefuelSeconds : Seconds(close);
            string label = kind == 1 ? Loc.T("Заправка расходного бака", "Refuelling the day tank")
                : close ? Loc.T("Включение рубильника", "Closing the breaker")
                : Loc.T("Выключение рубильника", "Pulling the breaker");
            if (!NativeActionProgress.Begin(Progress, label, seconds, true, "repair", "vehicle_repair_01")) return;
            _job = near; _jobKind = kind; _jobClose = close; _jobStart = Time.time; _jobEnd = Time.time + seconds;
            _prompt = null;
            if (kind == 0) SendBreaker(near, close, 1);
        }

        static string NeedCan { get { return Loc.T("Нужна канистра с топливом или машина с топливом рядом", "A canister with fuel or a fuelled vehicle alongside is needed"); } }

        static void Work()
        {
            bool gone = _near != _job || _nearKind != _jobKind || !NativeActionProgress.IsActive(Progress)
                || (_jobKind == 0 && States[_job].Breaker == _jobClose);
            if (_pressed && Time.time - _jobStart > 0.3f) { _pressed = false; gone = true; }
            if (gone) { StopJob(true); return; }
            if (Time.time < _jobEnd) return;
            if (_jobKind == 0) SendBreaker(_job, _jobClose, 2);
            else Refuel(_job);
            StopJob(false);
        }

        /// <summary>The local player pours one canister (only what the tank takes).</summary>
        static void Refuel(int s)
        {
            float room = (1f - States[s].Tank) * PowerNetCore.TankUnits;
            float units = FuelDepot.PourFromCan(Mathf.Min(PowerNetCore.CanUnits, room));
            // Power 3: no canister - a fuel truck (any fuelled vehicle) parked at the filler gives from its tank.
            if (units <= 0f) units = FuelDepot.PourFromVehicle(TankAt(s), Mathf.Min(PowerNetCore.CanUnits, room));
            if (units <= 0f) { Turret.Hinweis(NeedCan, 2f); return; }
            Turret.Hinweis(Loc.T("Расходный бак: +", "Day tank: +") + Mathf.RoundToInt(units), 3f);
            if (Crocodile.IsMaster()) { OnRefuel(s, units, Crocodile.LocalActor()); return; }
            float[] p = _refuel;
            p[0] = RefuelMsg; p[1] = s; p[2] = units; p[3] = p[4] = p[5] = p[6] = p[7] = 0f;
            FlakNet.SendPacket(p, true);
        }

        static void OnRefuel(int s, float units, int actor)
        {
            if (s < 0 || s >= Sites.Length || !Live[s] || !IsBound(s)) return;
            GameObject player = Crocodile.PlayerByActor(actor);
            float reach = PowerNetCore.BreakerReachM * K * 2f;
            if (player == null || !Crocodile.PlayerUp(player) || (player.transform.position - TankAt(s)).sqrMagnitude > reach * reach) return;
            float since = RefuelActor[s] == actor ? Time.time - RefuelAt[s] : float.MaxValue;
            bool dry;
            float added = PowerNetCore.HandRefuel(ref States[s], units, since, out dry);
            if (added <= 0f) return;
            RefuelActor[s] = actor; RefuelAt[s] = Time.time;
            RevivalPlugin.L.LogInfo("PowerNet: " + Sites[s].Id + " day tank +" + Mathf.RoundToInt(units) + " by hand (actor " + actor + ").");
            if (dry) Log(s);
            Changed(s);   // the tank percentage is news even without a power change
        }

        static Vector3 TankAt(int s)
        {
            Bound b = Bounds[s];
            if (b.Tank != null) return b.Tank.position;
            PowerSite site = Sites[s];
            return b.Root.TransformPoint(new Vector3(site.TankX, site.TankY, site.TankZ));
        }

        static void StopJob(bool cancel)
        {
            if (_job < 0) return;
            if (cancel && _jobKind == 0) SendBreaker(_job, _jobClose, 0);
            NativeActionProgress.End(Progress);
            _job = -1; _waitRelease = true; _pressed = false;
        }

        static void SendBreaker(int s, bool close, int phase)
        {
            if (Crocodile.IsMaster()) { OnBreaker(s, close, phase, Crocodile.LocalActor()); return; }
            float[] p = _breaker;
            p[0] = BreakerMsg; p[1] = s; p[2] = close ? 1f : 0f; p[3] = phase; p[4] = p[5] = p[6] = p[7] = 0f;
            FlakNet.SendPacket(p, true);
        }

        internal static void Draw()
        {
            PowerBunkerDoor.Draw();
            if (_prompt == null || _job >= 0) return;
            VanillaUi.Prompt(_prompt, Screen.height * 0.68f);
        }
    }
}

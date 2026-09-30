// Next Day: Survival - Revival Toolkit
//
// THE KATYUSHA - a BM-13-style rocket launcher on the game's own Ural truck:
// sixteen M-13 rockets on eight guide rails, fired as one ripple salvo into a
// large area the crew picks on the map. docs/ai/tasks/n07-katyusha.md has the
// numbers, the decisions and the in-game checklist.
//
// WHERE THE MODEL COMES FROM. Nothing existed (no katyusha/bm13/bm21/grad art
// in any branch, asset or the main checkout, 2026-09-28), so the launcher is
// built by katyusha_build.py: a static sub-frame on the bed, a turntable that
// traverses, and the rail rack that elevates, in real metres. The truck is the
// vanilla "ural-375(mod)_spawn" - driving, collision, damage and networking all
// belong to that prefab. Its machine-gun turret, its armour kit and the boxes
// on its bed are switched off (SetActive, so the LODGroup cannot bring them
// back), and the launcher stands where the boxes were.
//
// WHAT A PLAYER DOES
//   1. Loading. On foot at a standing Katyusha with M-13 rockets (item 2075)
//      in the backpack, R starts loading: one rocket every LoadSeconds (the
//      game's own progress bar and repair animation), rocket after rocket
//      until the rails are full or the backpack is empty. The rockets are
//      heavy and rare (the airfield's military loot, the admin panel): they
//      are carried in a truck's storage and loaded at the launcher.
//   2. Fire control. Anyone aboard presses G while the truck stands: the map
//      opens and shows the TARGET AREA - an ellipse EllipseLength x
//      EllipseWidth (400 x 300 by default) along the line of fire - that
//      follows the mouse pointer. The rack turns and elevates at its crank
//      rates to lay on it (range is elevation); the ellipse drawn solid is
//      where the rack actually points, the faint one where the pointer asks.
//      Minimum and maximum range are drawn as rings, safe zones in red.
//   3. The salvo. Left mouse button: the target is pinned and, as soon as the
//      rack is laid on it, EVERY loaded rocket goes as a ripple salvo, one
//      every RippleInterval seconds. Right mouse button unpins / cancels. Every
//      impact point is drawn UNIFORMLY at random over the whole ellipse - the
//      weapon is an area weapon and nothing else. G or Esc closes the view;
//      the rack stows itself afterwards and the truck will not drive before it
//      has (the travel lock).
//   4. Counterplay. Every launch throws a big smoke and dust cloud round the
//      truck that hangs for many seconds, and a loud roar carries for
//      kilometres; players within HearRange get a "salvo heard" line with the
//      direction. There is a minimum range, and no salvo may be fired into -
//      or out of - a safe zone (trader settlements with IsSafeSettlement and
//      the neutral no-fly zone N12).
//
// HOW A ROCKET GOES OFF
//   The shooter draws the impact points and sends them in ONE reliable event
//   (KatyushaNet kind 1). Every client flies the same rockets on the same
//   schedule; the shooter sends the final xyz impact (kind 3), used by both
//   the pooled burst and master damage on every client - no
//   network object per explosion. OrdnanceBlast queues one master damage pass
//   through NPC Photon owners with body/explosion parameters. The shooter
//   alone sends player victim RPCs, preserving sender attribution. The
//   master touches the airfield fuel depots (FuelDepot.Blast; with the old
//   depot the An-2 repair's DepotHit), so one depot burns once.
//
// PERFORMANCE. Four shared, hand-emitted particle systems (Fx.Keep gates each
// Emit, [Effects] ParticleDensity), a ring of pooled AudioSources and pooled
// Lights, and a free list of rocket objects: a salvo creates no GameObject
// after the first one.
//
// NETWORK. One Photon event code ([Katyusha] NetworkEventCode, 188 - 170-185
// and 190-199 are taken), float[] payloads, the first float the kind:
//   0 pose   view, yaw, pitch, raised          operator -> others, 4 Hz
//   1 salvo  view, n, interval, pitch, x0, z0, ... xn-1, zn-1   reliable
//   2 load   view, count                        whoever changed it; master
//                                               repeats every 5 s
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. UTF-8 without BOM;
// outside ASCII only the Cyrillic of the player lines (Loc.T, KatyushaText).
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs      BindConfig / Install / AddItems / Update / OnGUI
//   RevivalUralTruck.cs   VehicleRegistry entry "katyusha"
//   Revival.Admin.cs      "Spawn Katyusha" and "Katyusha rockets x16"
//   Revival.Airfield.cs   the loot pool (Pool)
//   RevivalMortar.cs      ShowMap / MapPoint; OrdnanceBlast shared damage
//   RevivalFrameProfiler.cs two slots

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>The Katyusha: vehicle, fire control, salvo, loading.</summary>
    public static class Katyusha
    {
        /// <summary>M-13 rocket, after the FAB-50 (2072).</summary>
        internal const int ItemId = 2075;
        const int DONOR = 2030;                    // the generic carryable, like 2072

        /// <summary>vehiclespawn/ural-375(mod)_spawn (research/resource_paths.tsv).</summary>
        public const string Prefab = "ural-375(mod)_spawn";

        /// <summary>Name marker of a rebuilt instance. Neither "_URAL15" nor
        /// "_ARTY" nor "btr-80a", so no other vehicle rule claims it.</summary>
        public const string Marke = "_KATJUSHA";

        /// <summary>The Ural cab: driver and commander. The bed is launcher.</summary>
        public const int SeatTotal = 2;

        public static ConfigEntry<bool> CfgEnabled, CfgTrails;
        public static ConfigEntry<string> CfgFireKey, CfgLoadKey;
        public static ConfigEntry<int> CfgCapacity, CfgEventCode;
        public static ConfigEntry<float> CfgDurability, CfgLoadSeconds, CfgEllipseLength,
            CfgEllipseWidth, CfgMinRange, CfgMaxRange, CfgSafeRadius, CfgInterval, CfgTraverse,
            CfgElevate, CfgMinPitch, CfgMaxPitch, CfgStowPitch, CfgRadius, CfgNpcDamage,
            CfgVehicleDamage, CfgPlayerDamage, CfgFlightBase, CfgFlightSpeed, CfgHearRange,
            CfgLoadReach, CfgSmokeSeconds;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "Katyusha";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The Katyusha rocket launcher: a vehicle kind (admin panel), the M-13 "
                + "rocket item (2075), the map fire control and the salvo. Every "
                + "client must have the same setting.");
            CfgTrails = cfg.Bind("Graphics", "KatyushaRocketTrails", true,
                "Smoke trails behind the flying rockets. Off saves particles; the "
                + "launch cloud stays, it is how the launcher is found.");
            CfgFireKey = cfg.Bind(S, "FireControlKey", "G",
                "Aboard a standing Katyusha: open or close the map fire control.");
            CfgLoadKey = cfg.Bind(S, "LoadKey", "R",
                "On foot at a standing Katyusha: load M-13 rockets from the backpack.");
            CfgCapacity = cfg.Bind(S, "Rockets", 16, "Rails x rows: M-13 rockets one launcher carries.");
            CfgDurability = cfg.Bind(S, "Durability", 800f,
                "Hit points of the truck, capped DOWNWARDS on every scan.");
            CfgLoadSeconds = cfg.Bind(S, "LoadSeconds", 5f,
                "Seconds to load ONE rocket (a full load is Rockets x this).");
            CfgEllipseLength = cfg.Bind(S, "EllipseLength", 400f,
                "Length of the target area along the line of fire, map metres (world units, "
                + "as on the howitzer's map).");
            CfgEllipseWidth = cfg.Bind(S, "EllipseWidth", 300f, "Width of the target area, map metres.");
            CfgMinRange = cfg.Bind(S, "MinRange", 600f,
                "Nearest target-area centre, map metres. The near edge of the area is "
                + "half an EllipseLength closer.");
            CfgMaxRange = cfg.Bind(S, "MaxRange", 2400f, "Farthest target-area centre, map metres.");
            CfgSafeRadius = cfg.Bind(S, "SafeZoneRadius", 300f,
                "Radius round every settlement the game marks IsSafeSettlement that no "
                + "rocket may be aimed into and no salvo fired from.");
            CfgInterval = cfg.Bind(S, "RippleInterval", 0.5f, "Seconds between two rockets of a salvo.");
            CfgTraverse = cfg.Bind(S, "TraverseRate", 12f, "Degrees a second the rack turns.");
            CfgElevate = cfg.Bind(S, "ElevationRate", 6f, "Degrees a second the rack elevates.");
            CfgMinPitch = cfg.Bind(S, "MinElevation", 12f, "Rail elevation at MinRange, degrees.");
            CfgMaxPitch = cfg.Bind(S, "MaxElevation", 45f, "Rail elevation at MaxRange, degrees.");
            CfgStowPitch = cfg.Bind(S, "StowElevation", 4f,
                "Rail elevation in the travel position (the rails clear the cab roof).");
            ConfigEntry<float> legacyRadius = cfg.Bind(S, "BlastRadius", 32f,
                "Legacy burst radius in world units; custom values seed BlastRadiusMetres once.");
            float radiusM = Mathf.Abs(legacyRadius.Value - 32f) < 0.01f ? 18f
                : legacyRadius.Value / OrdnanceBlast.UnitsPerMetre;
            CfgRadius = cfg.Bind(S, "BlastRadiusMetres", radiusM,
                "Burst radius of one M-13 rocket in metres, linear falloff to zero.");
            CfgNpcDamage = cfg.Bind(S, "NpcDamage", 700f, "Damage to an NPC at the burst point.");
            CfgVehicleDamage = cfg.Bind(S, "VehicleDamage", 1600f,
                "Damage to a vehicle at the burst point (explosion part, armour rules apply); "
                + "also what a fuel depot tank takes.");
            CfgPlayerDamage = cfg.Bind(S, "PlayerDamage", 350f,
                "Body explosion damage to a player at the burst point, including the shooter's faction.");
            CfgFlightBase = cfg.Bind(S, "FlightSeconds", 2.5f, "Flight time at zero range, seconds.");
            CfgFlightSpeed = cfg.Bind(S, "FlightSpeed", 260f, "Map metres a second added to the flight.");
            CfgHearRange = cfg.Bind(S, "HearRange", 3000f,
                "Players this close to a launch get the \"salvo heard\" line with its direction.");
            CfgLoadReach = cfg.Bind(S, "LoadReach", 16f, "World units from the truck within which R loads.");
            CfgSmokeSeconds = cfg.Bind(S, "LaunchSmokeSeconds", 16f,
                "How long the launch cloud hangs round the truck.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 188,
                "Photon event code (0..199). Must be the same on every client.");
        }

        public static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        static float F(ConfigEntry<float> e, float fallback) { return e == null ? fallback : e.Value; }
        internal static int Capacity { get { return Mathf.Clamp(CfgCapacity == null ? 16 : CfgCapacity.Value, 1, KatyushaModel.Slots); } }
        static float MinRange { get { return Mathf.Max(50f, F(CfgMinRange, 600f)); } }
        static float MaxRange { get { return Mathf.Max(MinRange + 50f, F(CfgMaxRange, 2400f)); } }
        static float SemiLength { get { return Mathf.Max(10f, F(CfgEllipseLength, 400f)) * 0.5f; } }
        static float SemiWidth { get { return Mathf.Max(10f, F(CfgEllipseWidth, 300f)) * 0.5f; } }
        static float MinPitch { get { return Mathf.Clamp(F(CfgMinPitch, 12f), 5f, 60f); } }
        static float MaxPitch { get { return Mathf.Clamp(F(CfgMaxPitch, 45f), MinPitch + 1f, 70f); } }
        static float StowPitch { get { return Mathf.Clamp(F(CfgStowPitch, 4f), 0f, 10f); } }
        internal static float Radius { get { return Mathf.Max(1f, F(CfgRadius, 18f)) * OrdnanceBlast.UnitsPerMetre; } }

        internal static bool IstKatyusha(Transform root)
        {
            return root != null && root.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static KeyCode KeyOf(ConfigEntry<string> e, KeyCode fallback)
        {
            if (e == null || string.IsNullOrEmpty(e.Value)) return fallback;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), e.Value.Trim(), true); }
            catch { return fallback; }
        }

        // ============================================================== items

        internal static void AddItems(List<ItemDef> items)
        {
            // Registered whatever [Katyusha] Enabled says, like the FAB-50: a
            // rocket lying in a save or a truck must stay a known item.
            items.Add(new ItemDef(
                ItemId, DONOR, false,
                "Реактивный снаряд М-13", "M-13 rocket",
                "132-мм реактивный снаряд «Катюши». Тяжёлый: возите их в кузове "
                + "грузовика и заряжайте у пусковой установки (клавиша загрузки).",
                "The Katyusha's 132 mm rocket. Heavy: haul them in a truck and load "
                + "them at the launcher (the load key).",
                "m13.ndmesh", "m13_diffuse.png", "m13_normal.png",
                "m13_icon.png", null,
                1, 0, 15.0f));
        }

        /// <summary>The airfield's "military" pool rolls an M-13 now and then
        /// (Airfield.Draw); one entry among the whole pool keeps them rare.</summary>
        internal static string[] Pool(string pool, string[] entries)
        {
            if (!Enabled || entries == null || pool != "military") return entries;
            string key = pool + "|" + entries.Length;
            string[] cached;
            if (_pools.TryGetValue(key, out cached)) return cached;
            string[] all = new string[entries.Length + 1];
            entries.CopyTo(all, 0);
            all[entries.Length] = ItemId.ToString();
            _pools[key] = all;
            return all;
        }

        static readonly Dictionary<string, string[]> _pools = new Dictionary<string, string[]>();

        // ============================================================ vehicle

        /// <summary>The rebuild (registry and network marker). Idempotent;
        /// a failure leaves a plain, drivable Ural.</summary>
        public static void Umbauen(GameObject car)
        {
            if (car == null) return;
            bool fertig = IstKatyusha(car.transform);
            if (!fertig)
            {
                try { KatyushaModel.Build(car); }
                catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha, rebuild: " + ex); }
                if (car.name.IndexOf(Marke, StringComparison.OrdinalIgnoreCase) < 0)
                    car.name = car.name + Marke;
            }
            try { Deckel(car.GetComponent(RevivalPlugin.TypeByName("VehicleGameSystem"))); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha, hit points: " + ex); }
        }

        static void Deckel(Component vgs)
        {
            if (vgs == null) return;
            float cap = F(CfgDurability, 800f);
            if (cap <= 0f) return;
            FieldInfo f = AccessTools.Field(vgs.GetType(), "Durability");
            if (f == null || f.FieldType != typeof(float)) return;
            float have = (float)f.GetValue(vgs);
            if (have <= cap) return;
            f.SetValue(vgs, cap);
        }

        public static void Install(Harmony harmony)
        {
            if (!Enabled)
            {
                RevivalPlugin.L.LogInfo("Katyusha: off (Katyusha/Enabled).");
                return;
            }
            VehicleRegistry.EnsureBuilt();
            KatyushaNet.InstallSpawnMarker(harmony);
            KatyushaTravel.Install(harmony);
            RevivalPlugin.L.LogInfo("Katyusha: registered (prefab " + Prefab + ", "
                + Capacity + " rockets, target area " + (SemiLength * 2f).ToString("0") + " x "
                + (SemiWidth * 2f).ToString("0") + ", range " + MinRange.ToString("0") + "-"
                + MaxRange.ToString("0") + ", event " + KatyushaNet.Code() + ").");
        }

        /// <summary>The admin spawn: in front of the player, rails full.</summary>
        internal static string SpawnInFront()
        {
            if (!Enabled) return "The Katyusha is disabled in the configuration ([Katyusha] Enabled).";
            Camera cam = CameraOwner.MainCamera();
            if (cam == null) return "No player camera available.";
            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();
            Vector3 above = cam.transform.position + ahead * 14f + Vector3.up * 30f;
            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null) return "No ground found ahead of the player.";
            bool isTank;
            GameObject car = VehicleRegistry.Spawn("katyusha", ground + Vector3.up * 1.6f,
                Quaternion.LookRotation(ahead, Vector3.up), out isTank);
            if (car == null) return "Katyusha spawn failed (master client only); see the game log.";
            KatyushaNet.EnsureHooked();
            SetCount(car, Capacity, true);
            RevivalPlugin.L.LogInfo("Katyusha: spawned at " + ground.ToString("0") + ", rails full.");
            return KatyushaText.Spawned(Capacity);
        }

        // ========================================================== launchers

        internal sealed class Launcher
        {
            public GameObject Car;
            public Component Vgs;
            public Rigidbody Body;
            public int View;
            public Transform Base, Rack;
            public Transform[] Rockets;
            public float Scale;                   // world units per model metre
            public float Yaw, Pitch;              // current, degrees, yaw relative to the hull
            public float WantYaw, WantPitch;
            public bool Raised;                   // laid for fire (aim open or salvo)
            public float PoseHeard;               // remote: last pose event
            public readonly List<float> Schedule = new List<float>();   // launch times
            public readonly List<Vector3> Targets = new List<Vector3>();
            public float SalvoPitch;
            public bool SalvoMine;
            public bool Firing { get { return Schedule.Count > 0; } }
            public bool Stowed
            {
                get { return Mathf.Abs(Yaw) < 0.5f && Mathf.Abs(Pitch - StowPitch) < 0.5f; }
            }
        }

        static readonly List<Launcher> _launchers = new List<Launcher>();
        static readonly Dictionary<int, int> _count = new Dictionary<int, int>();   // view -> rockets aboard
        static float _nextScan, _nextHeartbeat;
        static FieldInfo _seatField;
        internal static bool AnyUnstowed;

        internal static int CountOf(Launcher l)
        {
            int n;
            return l != null && l.View != 0 && _count.TryGetValue(l.View, out n) ? n : 0;
        }

        static void SetCount(GameObject car, int n, bool send)
        {
            int view = PlayerAn2.View(car);
            if (view == 0) return;
            n = Mathf.Clamp(n, 0, Capacity);
            _count[view] = n;
            if (send) KatyushaNet.Send(new float[] { 2f, view, n }, true);
        }

        internal static void ReceiveCount(int view, int n)
        {
            if (view != 0) _count[view] = Mathf.Clamp(n, 0, Capacity);
        }

        internal static Launcher ByView(int view)
        {
            for (int i = 0; i < _launchers.Count; i++)
                if (_launchers[i].View == view) return _launchers[i];
            return null;
        }

        /// <summary>Launchers built on this client: new ones enrolled, gone
        /// ones dropped, hit points capped. A few times a second, on the
        /// shared vehicle scan.</summary>
        static void Scan()
        {
            for (int i = _launchers.Count - 1; i >= 0; i--)
            {
                Launcher l = _launchers[i];
                if (l.Car != null && l.Rack != null) continue;
                if (ReferenceEquals(_aim, l)) CloseAim("launcher gone");
                _launchers.RemoveAt(i);
            }
            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null || !IstKatyusha(vgs.transform)) continue;
                Deckel(vgs);
                bool known = false;
                for (int k = 0; k < _launchers.Count; k++)
                    if (_launchers[k].Car == vgs.gameObject) { known = true; break; }
                if (known) continue;
                Launcher l = KatyushaModel.Find(vgs.gameObject);
                if (l == null) continue;
                l.Vgs = vgs;
                l.Body = vgs.GetComponent<Rigidbody>();
                l.View = PlayerAn2.View(vgs.gameObject);
                l.Pitch = l.WantPitch = StowPitch;
                _launchers.Add(l);
                RevivalPlugin.L.LogInfo("Katyusha: launcher on \"" + vgs.name + "\" (view "
                    + l.View + ", scale " + l.Scale.ToString("0.00") + ").");
            }
            for (int i = 0; i < _launchers.Count; i++)
                if (_launchers[i].View == 0) _launchers[i].View = PlayerAn2.View(_launchers[i].Car);
        }

        static int LocalSeat(Launcher l)
        {
            if (l == null || l.Vgs == null) return -1;
            try
            {
                if (_seatField == null) _seatField = AccessTools.Field(l.Vgs.GetType(), "_localPlayerPassengerId");
                if (_seatField == null) return -1;
                return Convert.ToInt32(_seatField.GetValue(l.Vgs));
            }
            catch { return -1; }
        }

        static float Speed(Launcher l)
        {
            return l.Body == null ? 0f : l.Body.velocity.magnitude;
        }

        // ============================================================== frame

        static float _errors;

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                KatyushaNet.EnsureHooked();
                if (Time.time >= _nextScan) { _nextScan = Time.time + 0.25f; Scan(); }
                Heartbeat();

                Launcher mine = null;
                bool unstowed = false;
                float dt = Mathf.Min(Time.deltaTime, 0.1f);
                for (int i = 0; i < _launchers.Count; i++)
                {
                    Launcher l = _launchers[i];
                    if (l.Car == null) continue;
                    if (mine == null && LocalSeat(l) >= 0) mine = l;
                    Lay(l, dt);
                    Launches(l);
                    KatyushaModel.ShowRockets(l, CountOf(l));
                    if (!l.Stowed || l.Raised) unstowed = true;
                }
                AnyUnstowed = unstowed;
                KatyushaFlight.Tick(dt);
                KatyushaSound.Tick();
                KatyushaFx.Tick();

                _crew = mine;
                if (_aim != null) Aim();
                else if (mine != null)
                {
                    if (GameUi.KeyDown(KeyOf(CfgFireKey, KeyCode.G))) OpenAim(mine);
                }
                if (mine == null) OnFoot();
                else if (_loading != null) CancelLoad(null);
                _errors = 0f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Katyusha: " + ex);
                if (++_errors >= 5f) { _errors = 0f; CloseAim("errors"); CancelLoad(null); }
            }
        }

        /// <summary>One frame of the rack: towards the laid lay when raised,
        /// towards the travel position otherwise, at the crank rates. A remote
        /// raised rack that has not been heard of for 4 s stows itself.</summary>
        static void Lay(Launcher l, float dt)
        {
            bool operatedHere = ReferenceEquals(_aim, l) || (l.Firing && l.SalvoMine);
            if (!operatedHere && l.Raised && !l.Firing && Time.time - l.PoseHeard > 4f) l.Raised = false;
            float ty = Mathf.Max(0.5f, F(CfgTraverse, 12f)) * dt;
            float tp = Mathf.Max(0.5f, F(CfgElevate, 6f)) * dt;
            if (l.Raised || l.Firing)
            {
                // Out of the travel position the rails lift off the cab
                // before they swing.
                l.Pitch = Mathf.MoveTowards(l.Pitch, Mathf.Max(l.WantPitch, MinPitch), tp);
                if (l.Pitch >= MinPitch - 0.5f) l.Yaw = Mathf.MoveTowardsAngle(l.Yaw, l.WantYaw, ty);
            }
            else if (Mathf.Abs(l.Yaw) > 0.01f)
            {
                // Home: swing back at firing elevation, then lower.
                if (l.Pitch < MinPitch - 0.5f) l.Pitch = Mathf.MoveTowards(l.Pitch, MinPitch, tp);
                else l.Yaw = Mathf.MoveTowardsAngle(l.Yaw, 0f, ty);
            }
            else l.Pitch = Mathf.MoveTowards(l.Pitch, StowPitch, tp);
            l.Yaw = Mathf.DeltaAngle(0f, l.Yaw);
            KatyushaModel.Pose(l);
        }

        /// <summary>Travel lock query: this truck's rack is out of its
        /// travel position (or about to be).</summary>
        internal static bool Unstowed(GameObject car)
        {
            for (int i = 0; i < _launchers.Count; i++)
                if (_launchers[i].Car == car) return !_launchers[i].Stowed || _launchers[i].Raised;
            return false;
        }

        internal static Vector3 Pivot(Launcher l) { return l.Base != null ? l.Base.position : l.Car.transform.position; }

        static float Heading(Launcher l)
        {
            Vector3 f = l.Car.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        internal static float RangeFor(float pitch)
        {
            float t = Mathf.InverseLerp(MinPitch, MaxPitch, pitch);
            return Mathf.Lerp(MinRange, MaxRange, t);
        }

        internal static float PitchFor(float range)
        {
            float t = Mathf.InverseLerp(MinRange, MaxRange, range);
            return Mathf.Lerp(MinPitch, MaxPitch, t);
        }

        /// <summary>Where the rack points now: the centre of the area a salvo
        /// fired this instant would fall into.</summary>
        internal static Vector3 LaidPoint(Launcher l, out float bearing)
        {
            bearing = Heading(l) + l.Yaw;
            float r = RangeFor(Mathf.Max(l.Pitch, MinPitch));
            Vector3 p = Pivot(l);
            float rad = bearing * Mathf.Deg2Rad;
            Vector3 c = new Vector3(p.x + Mathf.Sin(rad) * r, 0f, p.z + Mathf.Cos(rad) * r);
            float y;
            if (RevivalTroopInsertion.GroundY(c, out y)) c.y = y;
            return c;
        }

        // ======================================================= fire control

        static Launcher _aim, _crew;
        static bool _weOpenedMap, _pinned, _queued, _wantHave;
        static float _aimSince, _nextPose;
        static Vector3 _want;
        static string _hint = "";
        static float _hintUntil;

        internal static bool Aiming { get { return _aim != null; } }

        static void OpenAim(Launcher l)
        {
            if (Speed(l) > 2.5f) { Hint(KatyushaText.StopFirst(), 3f); return; }
            if (l.Firing) { Hint(KatyushaText.SalvoRunning(), 2f); return; }
            _aim = l;
            _aimSince = Time.time;
            _pinned = false;
            _queued = false;
            _wantHave = false;
            float bearing;
            _want = LaidPoint(l, out bearing);
            _weOpenedMap = Mortar.ShowMap(true);
            l.Raised = true;
            l.WantYaw = l.Yaw;
            l.WantPitch = Mathf.Max(l.Pitch, MinPitch);
            SafeZones.Refresh();
            Publish(l, true);
            Hint(KatyushaText.Controls(CountOf(l), Capacity), 6f);
            RevivalPlugin.L.LogInfo("Katyusha: fire control opened, " + CountOf(l) + " rockets on the rails.");
        }

        static void CloseAim(string why)
        {
            if (_aim == null) return;
            Launcher l = _aim;
            _aim = null;
            _queued = false;
            if (_weOpenedMap) Mortar.ShowMap(false);
            _weOpenedMap = false;
            if (!l.Firing) l.Raised = false;
            Publish(l, true);
            RevivalPlugin.L.LogInfo("Katyusha: fire control closed (" + why + ").");
        }

        static void Publish(Launcher l, bool force)
        {
            if (l == null || l.View == 0) return;
            if (!force && Time.time < _nextPose) return;
            _nextPose = Time.time + 0.25f;
            KatyushaNet.Send(new float[] { 0f, l.View, l.WantYaw, l.WantPitch, l.Raised || l.Firing ? 1f : 0f }, force);
        }

        internal static void ReceivePose(int view, float yaw, float pitch, bool raised)
        {
            Launcher l = ByView(view);
            if (l == null || ReferenceEquals(_aim, l)) return;
            l.WantYaw = yaw;
            l.WantPitch = pitch;
            l.Raised = raised;
            l.PoseHeard = Time.time;
        }

        static void Aim()
        {
            Launcher l = _aim;
            if (l.Car == null) { CloseAim("launcher gone"); return; }
            if (LocalSeat(l) < 0) { CloseAim("left the vehicle"); return; }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyOf(CfgFireKey, KeyCode.G)))
            { CloseAim("key"); return; }

            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out cam, out world, out map))
            {
                float grace = _weOpenedMap ? 2.5f : 15f;
                if (Time.time - _aimSince > grace) CloseAim("map not open");
                Publish(l, false);
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                if (_queued || _pinned) { _queued = false; _pinned = false; Hint(KatyushaText.Unpinned(), 2f); }
            }
            Vector3 pointer;
            bool onMap = Mortar.MapPoint(texture, cam, world, out pointer);
            if (!_pinned && onMap) { _want = pointer; _wantHave = true; }

            // The rack is laid on the wanted centre: bearing is traverse,
            // range is elevation. Out-of-reach centres are laid on the nearest
            // reachable range along the same bearing, so the rack still turns
            // towards what the pointer asks.
            if (_wantHave)
            {
                Vector3 p = Pivot(l);
                Vector3 d = _want - p;
                d.y = 0f;
                float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                l.WantYaw = Mathf.DeltaAngle(0f, bearing - Heading(l));
                l.WantPitch = PitchFor(Mathf.Clamp(d.magnitude, MinRange, MaxRange));
            }
            Publish(l, false);

            if (Input.GetMouseButtonDown(0) && onMap)
            {
                string why = Refusal(l, pointer);
                if (why != null) { Hint(why, 3f); _queued = false; }
                else if (CountOf(l) <= 0) Hint(KatyushaText.Empty(), 3f);
                else
                {
                    _want = pointer;
                    _pinned = true;
                    _queued = true;
                    Hint(KatyushaText.Queued(), 2f);
                }
            }
            if (_queued && Laid(l))
            {
                _queued = false;
                float bearing;
                Vector3 centre = LaidPoint(l, out bearing);
                string why = Refusal(l, centre);
                if (why != null) Hint(why, 3f);
                else Fire(l, centre, bearing);
            }
        }

        static bool Laid(Launcher l)
        {
            return Mathf.Abs(Mathf.DeltaAngle(l.Yaw, l.WantYaw)) < 0.3f
                && Mathf.Abs(l.Pitch - l.WantPitch) < 0.2f;
        }

        /// <summary>Why a salvo at this centre is refused, or null. A refusal
        /// is never clamped onto something else: that would fire at a place
        /// nobody chose.</summary>
        internal static string Refusal(Launcher l, Vector3 centre)
        {
            Vector3 d = centre - Pivot(l);
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < MinRange) return KatyushaText.TooClose(dist, MinRange);
            if (dist > MaxRange) return KatyushaText.TooFar(dist, MaxRange);
            if (SafeZones.Inside(Pivot(l), 0f)) return KatyushaText.FromSafeZone();
            float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (SafeZones.Overlaps(centre, bearing, SemiLength, SemiWidth)) return KatyushaText.IntoSafeZone();
            return null;
        }

        /// <summary>A uniformly random point of the ellipse: sqrt of a uniform
        /// radius fraction and a uniform angle, then stretched onto the two
        /// semi-axes and turned onto the line of fire. Uniform per AREA.</summary>
        internal static Vector3 RandomInEllipse(System.Random rng, Vector3 centre, float bearing,
                                               float semiLength, float semiWidth)
        {
            double r = Math.Sqrt(rng.NextDouble());
            double a = rng.NextDouble() * Math.PI * 2.0;
            float along = (float)(r * Math.Cos(a)) * semiLength;
            float across = (float)(r * Math.Sin(a)) * semiWidth;
            float rad = bearing * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            return new Vector3(centre.x, 0f, centre.z) + fwd * along + right * across;
        }

        static void Fire(Launcher l, Vector3 centre, float bearing)
        {
            int n = CountOf(l);
            if (n <= 0) { Hint(KatyushaText.Empty(), 3f); return; }
            System.Random rng = new System.Random(unchecked(Environment.TickCount * 31 + l.View));
            float interval = Mathf.Clamp(F(CfgInterval, 0.5f), 0.05f, 3f);
            float[] msg = new float[5 + n * 2];
            msg[0] = 1f; msg[1] = l.View; msg[2] = n; msg[3] = interval; msg[4] = l.Pitch;
            List<Vector3> points = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = RandomInEllipse(rng, centre, bearing, SemiLength, SemiWidth);
                points.Add(p);
                msg[5 + i * 2] = p.x;
                msg[6 + i * 2] = p.z;
            }
            KatyushaNet.Send(msg, true);
            StartSalvo(l, points, interval, l.Pitch, true);
            _pinned = false;
            Hint(KatyushaText.Salvo(n, Vector3.Distance(Flat(Pivot(l)), Flat(centre))), 4f);
            RevivalPlugin.L.LogInfo("Katyusha: salvo of " + n + " at " + centre.ToString("0")
                + ", bearing " + bearing.ToString("0") + ", range "
                + Vector3.Distance(Flat(Pivot(l)), Flat(centre)).ToString("0") + ", area "
                + (SemiLength * 2f).ToString("0") + " x " + (SemiWidth * 2f).ToString("0") + ".");
        }

        static Vector3 Flat(Vector3 v) { return new Vector3(v.x, 0f, v.z); }

        /// <summary>The salvo on this client: the shooter's own, or one heard
        /// over the wire. Same schedule and same points on every client.</summary>
        internal static void StartSalvo(Launcher l, List<Vector3> points, float interval, float pitch, bool mine)
        {
            l.Schedule.Clear();
            l.Targets.Clear();
            float t = Time.time + 0.15f;
            for (int i = 0; i < points.Count; i++)
            {
                l.Schedule.Add(t + i * interval);
                l.Targets.Add(points[i]);
            }
            l.SalvoPitch = pitch;
            l.SalvoMine = mine;
            if (!mine)
            {
                l.Raised = true;
                l.PoseHeard = Time.time;
                Heard(l, points.Count);
            }
        }

        internal static void ReceiveSalvo(float[] f)
        {
            Launcher l = ByView(Mathf.RoundToInt(f[1]));
            if (l == null) return;
            int n = Mathf.Clamp(Mathf.RoundToInt(f[2]), 0, KatyushaModel.Slots);
            if (f.Length < 5 + n * 2) return;
            List<Vector3> points = new List<Vector3>();
            for (int i = 0; i < n; i++) points.Add(new Vector3(f[5 + i * 2], 0f, f[6 + i * 2]));
            _count[l.View] = n;
            StartSalvo(l, points, Mathf.Clamp(f[3], 0.05f, 3f), f[4], false);
        }

        /// <summary>Launch whatever is due: the top rocket of the rails goes,
        /// the count drops on every client alike.</summary>
        static void Launches(Launcher l)
        {
            while (l.Schedule.Count > 0 && Time.time >= l.Schedule[0])
            {
                l.Schedule.RemoveAt(0);
                Vector3 target = l.Targets[0];
                l.Targets.RemoveAt(0);
                int left = Mathf.Max(0, CountOf(l) - 1);
                if (l.View != 0) _count[l.View] = left;
                KatyushaFlight.Launch(l, left, target, l.SalvoPitch, l.SalvoMine);
                if (l.Schedule.Count == 0)
                {
                    if (l.SalvoMine) KatyushaNet.Send(new float[] { 2f, l.View, left }, true);
                    if (!ReferenceEquals(_aim, l)) { l.Raised = false; if (l.SalvoMine) Publish(l, true); }
                    else Hint(KatyushaText.Done(), 3f);
                }
            }
        }

        static void Heard(Launcher l, int n)
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null || l.Car == null) return;
            Vector3 d = Pivot(l) - me.transform.position;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist > Mathf.Max(0f, F(CfgHearRange, 3000f))) return;
            if (LocalSeat(l) >= 0) return;
            Turret.Hinweis(KatyushaText.Heard(Compass(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg), dist), 5f);
        }

        static string Compass(float bearing)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            int i = Mathf.RoundToInt(Mathf.Repeat(bearing, 360f) / 45f) % 8;
            return names[i];
        }

        // ============================================================ impacts

        /// <summary>A rocket's burst. The picture on every client (pooled);
        /// master NPC/vehicle damage and shooter player victim RPCs.</summary>
        internal static void Burst(Vector3 point, bool mine)
        {
            if (!mine) return; // Visual flight copies wait for the shooter's actual impact.
            KatyushaNet.Send(new float[] { 3f, 0f, point.x, point.y, point.z }, true);
            ReceiveImpact(point);
            OrdnanceBlast.EnqueuePlayers(point, Radius, Mathf.Max(0f, F(CfgPlayerDamage, 350f)));
        }

        internal static void ReceiveImpact(Vector3 point)
        {
            KatyushaFx.Explosion(point);
            KatyushaSound.Boom(point);
            bool master = RevivalTroopInsertion.MasterClient();
            if (!master) return;
            float radius = Radius;
            OrdnanceBlast.Enqueue(point, radius, Mathf.Max(0f, F(CfgNpcDamage, 700f)),
                Mathf.Max(0f, F(CfgVehicleDamage, 1600f)),
                0f);
            if (master) Depot(point);
        }

        /// <summary>The airfield fuel: the POL depot's tanks take the blast
        /// (FuelDepot.Blast - enough rockets set them off); with the old
        /// single depot the An-2 repair's DepotHit sets its fuel off. Master
        /// only, so a depot burns once.</summary>
        static void Depot(Vector3 point)
        {
            try
            {
                if (FuelDepot.Active)
                {
                    FuelDepot.Blast(point, Mathf.Max(0f, F(CfgVehicleDamage, 1600f)), Radius * 1.25f);
                    return;
                }
                if (!An2Repair.DepotHit(point, Radius * 1.25f)) return;
                Vector3 at = An2Repair.Depot;
                float y;
                if (RevivalTroopInsertion.GroundY(at, out y)) at.y = y;
                RocketHook.Detonate(at + Vector3.up * 4f, 0f, Radius * 1.6f, 4f);
                RocketHook.Detonate(at + new Vector3(8f, 2f, -6f), 0f, Radius * 1.6f, 4f);
                RevivalPlugin.L.LogInfo("Katyusha: the D1 fuel depot went up.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Katyusha: depot blast: " + ex.Message); }
        }

        // ============================================================ loading

        sealed class LoadJob
        {
            public Launcher L;
            public float Start, Length;
            public int Loaded;
        }

        const string ProgressOwner = "katyusha-load";
        static LoadJob _loading;
        static Launcher _near;
        static float _nearCheck;

        static void OnFoot()
        {
            if (Time.time >= _nearCheck)
            {
                _nearCheck = Time.time + 0.4f;
                _near = null;
                GameObject me = MapTools.LocalPlayer();
                if (me != null && !PlayerHeli.Aboard && !PlayerAn2.Aboard)
                {
                    float best = Mathf.Max(4f, F(CfgLoadReach, 16f));
                    for (int i = 0; i < _launchers.Count; i++)
                    {
                        Launcher l = _launchers[i];
                        if (l.Car == null) continue;
                        float d = Vector3.Distance(me.transform.position, l.Car.transform.position);
                        if (d < best) { best = d; _near = l; }
                    }
                }
            }
            if (_loading != null) { TickLoad(); return; }
            if (_near == null) return;
            if (!GameUi.KeyDown(KeyOf(CfgLoadKey, KeyCode.R))) return;
            BeginLoad(_near, 0);
        }

        static void BeginLoad(Launcher l, int loadedSoFar)
        {
            int have = CountOf(l);
            if (have >= Capacity) { Hint(KatyushaText.Full(have, Capacity), 3f); return; }
            if (l.Firing || !l.Stowed) { Hint(KatyushaText.NotStowed(), 3f); return; }
            if (Speed(l) > 1.5f) { Hint(KatyushaText.StopFirst(), 3f); return; }
            if (!Turret.HasItem(ItemId))
            {
                Hint(loadedSoFar > 0 ? KatyushaText.LoadedSome(loadedSoFar, have, Capacity) : KatyushaText.NoRockets(), 3f);
                return;
            }
            float seconds = Mathf.Max(0.5f, F(CfgLoadSeconds, 5f));
            if (!NativeActionProgress.Begin(ProgressOwner, KatyushaText.Loading(have + 1, Capacity), seconds,
                                            true, "repair", "vehicle_repair_01"))
                return;
            LoadJob j = new LoadJob();
            j.L = l;
            j.Start = Time.time;
            j.Length = seconds;
            j.Loaded = loadedSoFar;
            _loading = j;
        }

        static void TickLoad()
        {
            LoadJob j = _loading;
            if (!NativeActionProgress.IsActive(ProgressOwner)) { CancelLoad(KatyushaText.Interrupted()); return; }
            if (j.L.Car == null || !ReferenceEquals(_near, j.L)) { CancelLoad(KatyushaText.OutOfReach()); return; }
            if (Time.time - j.Start < j.Length) return;
            NativeActionProgress.End(ProgressOwner);
            _loading = null;
            if (!Turret.TakeItem(ItemId, "Katyusha")) { Hint(KatyushaText.NoRockets(), 3f); return; }
            int now = Mathf.Min(Capacity, CountOf(j.L) + 1);
            SetCount(j.L.Car, now, true);
            RevivalPlugin.L.LogInfo("Katyusha: rocket loaded onto view " + j.L.View + ", " + now + " aboard.");
            if (now >= Capacity) { Hint(KatyushaText.Full(now, Capacity), 3f); return; }
            BeginLoad(j.L, j.Loaded + 1);     // the next one, until the backpack or the rails run out
        }

        static void CancelLoad(string why)
        {
            if (_loading == null) return;
            _loading = null;
            NativeActionProgress.End(ProgressOwner);
            if (!string.IsNullOrEmpty(why)) Hint(why, 2.5f);
        }

        // ============================================================ network

        static void Heartbeat()
        {
            if (Time.time < _nextHeartbeat) return;
            _nextHeartbeat = Time.time + 5f;
            if (!RevivalTroopInsertion.MasterClient() || _count.Count == 0) return;
            List<int> gone = null;
            foreach (KeyValuePair<int, int> kv in _count)
            {
                if (ByView(kv.Key) == null)
                {
                    if (gone == null) gone = new List<int>();
                    gone.Add(kv.Key);
                    continue;
                }
                KatyushaNet.Send(new float[] { 2f, kv.Key, kv.Value }, false);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) _count.Remove(gone[i]);
        }

        // =============================================================== draw

        static void Hint(string text, float seconds)
        {
            _hint = text;
            _hintUntil = Time.time + seconds;
        }

        public static void Draw()
        {
            if (!Enabled) return;
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint) return;
                float cx = Screen.width * 0.5f;
                float cy = Screen.height * 0.5f;
                bool hint = !string.IsNullOrEmpty(_hint) && Time.time < _hintUntil;
                if (_aim != null)
                {
                    KatyushaMap.Draw(_aim, _want, _wantHave, _pinned, _queued);
                    Label(Status(_aim), cx, Screen.height - 96f, new Color(1f, 0.9f, 0.62f, 1f), 16);
                    if (hint) Label(_hint, cx, Screen.height - 124f, new Color(1f, 0.8f, 0.55f, 1f), 16);
                    return;
                }
                if (GameUi.WindowOpen) return;
                if (_crew != null)
                {
                    if (Hints.Prompts)
                        Label(KatyushaText.Seat(CountOf(_crew), Capacity, KeyOf(CfgFireKey, KeyCode.G).ToString()),
                              cx, cy + 246f, new Color(0.85f, 0.82f, 0.62f, 1f), 13);
                }
                else if (_near != null && _loading == null && Hints.Prompts)
                {
                    Label(KatyushaText.LoadPrompt(KeyOf(CfgLoadKey, KeyCode.R).ToString(), CountOf(_near), Capacity),
                          cx, cy + 84f, new Color(0.95f, 0.90f, 0.70f, 1f), 14);
                }
                if (hint) Label(_hint, cx, cy + 60f, new Color(1f, 0.92f, 0.70f, 1f), 16);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha.Draw: " + ex); }
        }

        static string Status(Launcher l)
        {
            float bearing;
            Vector3 laid = LaidPoint(l, out bearing);
            float dist = Vector3.Distance(Flat(Pivot(l)), Flat(laid));
            string state;
            if (l.Firing) state = KatyushaText.SalvoRunning();
            else if (CountOf(l) <= 0) state = KatyushaText.Empty();
            else if (_queued) state = KatyushaText.Laying();
            else if (!Laid(l)) state = KatyushaText.Laying();
            else
            {
                string why = _wantHave ? Refusal(l, _want) : null;
                state = why ?? KatyushaText.Ready();
            }
            return KatyushaText.Plate(CountOf(l), Capacity, dist, bearing) + "   " + state;
        }

        internal static void Label(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUIContent content = new GUIContent(text);
            Vector2 measured = style.CalcSize(content);
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(cx - measured.x * 0.5f - 6f, y - 2f, measured.x + 12f, measured.y + 4f),
                            Texture2D.whiteTexture);
            GUI.color = keep;
            GUI.Label(new Rect(cx - measured.x * 0.5f, y, measured.x, measured.y), content, style);
        }

        internal static float EllipseSemiLength { get { return SemiLength; } }
        internal static float EllipseSemiWidth { get { return SemiWidth; } }
        internal static float RangeMin { get { return MinRange; } }
        internal static float RangeMax { get { return MaxRange; } }
        internal static bool Trails { get { return CfgTrails == null || CfgTrails.Value; } }
        internal static float SmokeSeconds { get { return Mathf.Clamp(F(CfgSmokeSeconds, 16f), 2f, 60f); } }
        internal static float FlightTime(float dist)
        {
            return Mathf.Max(0.5f, F(CfgFlightBase, 2.5f)) + dist / Mathf.Max(20f, F(CfgFlightSpeed, 260f));
        }
    }

    // =====================================================================
    // Model

    /// <summary>The launcher on the truck: hides the donor's turret, armour
    /// kit and bed boxes, measures the bed, and hangs mount, turntable, rack
    /// and sixteen rockets on it.</summary>
    internal static class KatyushaModel
    {
        internal const string MountName = "NDR_KatyushaMount";
        internal const string BaseName = "NDR_KatyushaBase";
        internal const string RackName = "NDR_KatyushaRack";

        // Same numbers as katyusha_build.py (verify.py compares them).
        internal const float RailPitch = 0.30f;
        internal const float RocketOff = 0.135f;
        internal const float RocketZ = -1.15f;
        internal const float RailFront = 2.9f;
        internal const float TrunnionY = 0.95f;
        internal const int Rails = 8;
        internal const int Slots = 16;
        const float M13Length = 1.41f;

        static Material _mat, _rocketMat;
        static Mesh _rocket;
        static Quaternion _rocketTurn = Quaternion.identity;
        static Vector3 _rocketCentre;
        static float _rocketSize = 1f;          // mesh units per metre along the rocket

        /// <summary>Slot i: rail i % 8, top row for i &lt; 8.</summary>
        internal static Vector3 Slot(int i)
        {
            int rail = i % Rails;
            float y = i < Rails ? RocketOff : -RocketOff;
            return new Vector3((rail - 3.5f) * RailPitch, y, RocketZ);
        }

        static bool Hidden(string n)
        {
            string s = n.ToLowerInvariant();
            if (s == "turret" || s.StartsWith("turret_lod")) return true;
            if (s == "gun" || s.StartsWith("gun_lod")) return true;
            if (s == "armor" || s.StartsWith("armor_lod") || s.Contains("_armor")) return true;
            if (s == "boxes") return true;
            return false;
        }

        internal static void Build(GameObject car)
        {
            Transform root = car.transform;
            Component vgs;
            Transform seats = ArtyVehicle.FindSeatPoints(car, out vgs);
            if (seats != null) Seats(vgs, seats);

            int hidden = 0;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i] == root || !Hidden(all[i].name)) continue;
                if (!all[i].gameObject.activeSelf) continue;
                all[i].gameObject.SetActive(false);
                hidden++;
            }

            Vector3 min, max, bmin, bmax;
            if (!Measure(car, null, out min, out max))
            {
                RevivalPlugin.L.LogWarning("Katyusha: nothing measurable on the donor - a plain Ural.");
                return;
            }
            bool bed = Measure(car, "flatbed", out bmin, out bmax);
            if (!bed)
            {
                bmin = new Vector3(min.x, min.y, min.z);
                bmax = new Vector3(max.x, min.y + (max.y - min.y) * 0.55f, min.z + (max.z - min.z) * 0.55f);
            }
            float scale = (max.z - min.z) / UralLengthMetres;

            if (!LoadArt()) return;
            Transform parent = seats != null && seats.parent != null ? seats.parent : root;
            GameObject mount = Part(MountName, "katyusha_mount.ndmesh", _mat);
            mount.transform.SetParent(parent, false);
            mount.transform.rotation = root.rotation;
            mount.transform.localScale = new Vector3(scale, scale, scale);
            mount.transform.position = root.TransformPoint(new Vector3((bmin.x + bmax.x) * 0.5f, bmax.y,
                                                                       (bmin.z + bmax.z) * 0.5f));
            GameObject bas = Part(BaseName, "katyusha_base.ndmesh", _mat);
            bas.transform.SetParent(mount.transform, false);
            GameObject rack = Part(RackName, "katyusha_rack.ndmesh", _mat);
            rack.transform.SetParent(bas.transform, false);
            rack.transform.localPosition = new Vector3(0f, TrunnionY, 0f);
            for (int i = 0; i < Slots; i++)
            {
                GameObject r = Rocket("M13_" + i);
                if (r == null) break;
                r.transform.SetParent(rack.transform, false);
                r.transform.localPosition = Slot(i);
                r.SetActive(false);
            }
            ModelLod.Apply(mount, ModelLod.Kind.Vehicle);    // P2: rockets and small parts go first
            RevivalPlugin.L.LogInfo("Katyusha: launcher built - donor box z " + min.z.ToString("0.00") + ".."
                + max.z.ToString("0.00") + ", bed " + (bed ? "flatbed" : "estimated") + " top "
                + bmax.y.ToString("0.00") + ", scale " + scale.ToString("0.000") + ", " + hidden
                + " donor parts hidden (turret, gun, armour kit, boxes).");
        }

        const float UralLengthMetres = 7.35f;

        static void Seats(Component vgs, Transform seats)
        {
            List<Transform> places = new List<Transform>();
            for (int i = 0; i < seats.childCount; i++) if (seats.GetChild(i) != null) places.Add(seats.GetChild(i));
            for (int i = places.Count - 1; i >= Katyusha.SeatTotal; i--)
            {
                places[i].SetParent(null, false);
                UnityEngine.Object.Destroy(places[i].gameObject);
            }
            if (vgs != null)
            {
                FieldInfo fPass = AccessTools.Field(vgs.GetType(), "Passengers");
                if (fPass != null) fPass.SetValue(vgs, new GameObject[seats.childCount]);
            }
        }

        /// <summary>Box of the donor's ACTIVE meshes (or of one named part) in
        /// the root's local space.</summary>
        static bool Measure(GameObject car, string only, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            bool any = false;
            Transform root = car.transform;
            MeshFilter[] all = car.GetComponentsInChildren<MeshFilter>(false);
            for (int i = 0; i < all.Length; i++)
            {
                MeshFilter mf = all[i];
                if (mf == null || mf.sharedMesh == null || Ours(mf.transform)) continue;
                if (only != null && !string.Equals(mf.name, only, StringComparison.OrdinalIgnoreCase)) continue;
                Bounds b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x,
                        (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { min = p; max = p; any = true; continue; }
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                if (only != null) break;
            }
            return any;
        }

        static bool Ours(Transform t)
        {
            for (; t != null; t = t.parent) if (t.name == MountName) return true;
            return false;
        }

        static GameObject Part(string name, string mesh, Material mat)
        {
            GameObject go = new GameObject(name);
            Mesh m = Assets.Load(mesh);
            if (m != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = m;
                MeshRenderer r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
            }
            return go;
        }

        static bool LoadArt()
        {
            if (_mat != null && _rocket != null) return true;
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (_mat == null)
                {
                    _mat = new Material(shader);
                    _mat.name = "NDR_Katyusha";
                    Texture2D tex = Assets.Texture("katyusha_diffuse.png", false, true);
                    if (tex != null) _mat.mainTexture = tex;
                    if (_mat.HasProperty("_Glossiness")) _mat.SetFloat("_Glossiness", 0.22f);
                    if (_mat.HasProperty("_Metallic")) _mat.SetFloat("_Metallic", 0.15f);
                }
                if (_rocket == null)
                {
                    _rocket = Assets.Load("m13.ndmesh");
                    if (_rocket == null) return false;
                    _rocketMat = new Material(shader);
                    _rocketMat.name = "NDR_M13";
                    Texture2D tex = Assets.Texture("m13_diffuse.png", false, true);
                    if (tex != null) _rocketMat.mainTexture = tex;
                    if (_rocketMat.HasProperty("_Glossiness")) _rocketMat.SetFloat("_Glossiness", 0.25f);
                    Bounds bb = _rocket.bounds;
                    _rocketCentre = bb.center;
                    _rocketSize = Mathf.Max(0.0001f, bb.size.x) / M13Length;
                    // Long axis X; the fins are the fat end. Nose at -X (fat
                    // end +X): turn -X onto +Z; else +X onto +Z.
                    Vector3[] v = _rocket.vertices;
                    float lo = 0f, hi = 0f;
                    for (int i = 0; i < v.Length; i++)
                    {
                        float rr = new Vector2(v[i].y - bb.center.y, v[i].z - bb.center.z).magnitude;
                        if (v[i].x < bb.min.x + bb.size.x * 0.15f) lo = Mathf.Max(lo, rr);
                        if (v[i].x > bb.max.x - bb.size.x * 0.15f) hi = Mathf.Max(hi, rr);
                    }
                    _rocketTurn = hi > lo ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.Euler(0f, -90f, 0f);
                }
                return _mat != null && _rocket != null;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Katyusha: art: " + ex.Message);
                return false;
            }
        }

        /// <summary>One M-13, 1.41 model metres long, nose along +Z of the
        /// returned object.</summary>
        internal static GameObject Rocket(string name)
        {
            if (!LoadArt()) return null;
            GameObject go = new GameObject(name);
            GameObject part = new GameObject("mesh");
            part.transform.SetParent(go.transform, false);
            float k = 1f / _rocketSize;
            part.transform.localRotation = _rocketTurn;
            part.transform.localScale = new Vector3(k, k, k);
            part.transform.localPosition = -(_rocketTurn * (_rocketCentre * k));
            part.AddComponent<MeshFilter>().sharedMesh = _rocket;
            MeshRenderer r = part.AddComponent<MeshRenderer>();
            r.sharedMaterial = _rocketMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>A launcher record for a rebuilt truck, or null.</summary>
        internal static Katyusha.Launcher Find(GameObject car)
        {
            Transform[] all = car.GetComponentsInChildren<Transform>(true);
            Transform mount = null;
            for (int i = 0; i < all.Length; i++) if (all[i].name == MountName) { mount = all[i]; break; }
            if (mount == null) return null;
            Transform bas = mount.Find(BaseName);
            Transform rack = bas == null ? null : bas.Find(RackName);
            if (rack == null) return null;
            Katyusha.Launcher l = new Katyusha.Launcher();
            l.Car = car;
            l.Base = bas;
            l.Rack = rack;
            l.Scale = mount.lossyScale.x;
            l.Rockets = new Transform[Slots];
            for (int i = 0; i < Slots; i++) l.Rockets[i] = rack.Find("M13_" + i);
            return l;
        }

        internal static void Pose(Katyusha.Launcher l)
        {
            if (l.Base != null) l.Base.localRotation = Quaternion.Euler(0f, l.Yaw, 0f);
            if (l.Rack != null) l.Rack.localRotation = Quaternion.Euler(-l.Pitch, 0f, 0f);
        }

        /// <summary>The first <paramref name="n"/> slots carry a rocket.</summary>
        internal static void ShowRockets(Katyusha.Launcher l, int n)
        {
            if (l.Rockets == null) return;
            for (int i = 0; i < l.Rockets.Length; i++)
            {
                Transform r = l.Rockets[i];
                if (r == null) continue;
                bool on = i < n;
                if (r.gameObject.activeSelf != on) r.gameObject.SetActive(on);
            }
        }

        /// <summary>World position of slot i and of its rail tip.</summary>
        internal static void SlotWorld(Katyusha.Launcher l, int i, out Vector3 slot, out Vector3 tip)
        {
            Vector3 s = Slot(Mathf.Clamp(i, 0, Slots - 1));
            slot = l.Rack.TransformPoint(s);
            tip = l.Rack.TransformPoint(new Vector3(s.x, s.y, RailFront));
        }
    }

    // =====================================================================
    // Rockets in flight

    /// <summary>The rockets of every salvo on this client: a short slide
    /// along the rail, then an arc onto the drawn point. Pooled objects.</summary>
    internal static class KatyushaFlight
    {
        sealed class Flight
        {
            public GameObject Go;
            public Vector3 Slot, Tip, To;
            public float Age, Time, Apex, NextTrail;
            public bool Mine, SupportMission;
            public int SupportGeneration;
        }

        const float Slide = 0.15f;
        const float Burn = 1.6f;
        static readonly List<Flight> _flights = new List<Flight>();
        static readonly List<GameObject> _free = new List<GameObject>();

        internal static void Launch(Katyusha.Launcher l, int slot, Vector3 target, float pitch, bool mine)
        {
            if (l.Rack == null) return;
            Flight f = new Flight();
            KatyushaModel.SlotWorld(l, slot, out f.Slot, out f.Tip);
            float y;
            target.y = RevivalTroopInsertion.GroundY(target, out y) ? y : f.Tip.y - 20f;
            f.To = target;
            Vector3 flat = f.To - f.Tip;
            flat.y = 0f;
            float d = flat.magnitude;
            f.Time = Katyusha.FlightTime(d);
            f.Apex = Mathf.Clamp(d * Mathf.Tan(Mathf.Clamp(pitch, 5f, 70f) * Mathf.Deg2Rad) * 0.25f, 5f, 2000f);
            f.Mine = mine;
            f.Go = Take(l.Scale);
            _flights.Add(f);
            Vector3 dir = (f.Tip - f.Slot).normalized;
            KatyushaFx.Launch(f.Slot, dir, l.Car == null ? f.Slot : l.Car.transform.position, l.Scale);
            KatyushaSound.Launch(f.Slot);
        }

        // W Tower 2: an off-map battery, with the same pooled M-13 flight,
        // motor, sound and impact. Only the master applies support damage.
        internal static void Support(Vector3 origin, Vector3 target, bool master)
        {
            Flight f = new Flight();
            f.Slot = origin;
            f.Tip = origin + Vector3.up * 2f;
            float y;
            if (!RevivalTroopInsertion.TerrainHeight(target, out y)) return;
            target.y = y;
            f.To = target;
            Vector3 flat = target - origin;
            flat.y = 0f;
            f.Time = Katyusha.FlightTime(flat.magnitude);
            f.Apex = Mathf.Clamp(flat.magnitude * 0.25f, 5f, 2000f);
            f.Mine = master;
            f.SupportMission = true;
            f.SupportGeneration = TowerSupport.WorldGeneration;
            f.Go = Take(1f);
            _flights.Add(f);
            KatyushaFx.Launch(f.Slot, (f.To - f.Slot).normalized, f.Slot, 1f);
            KatyushaSound.Launch(f.Slot);
        }

        static GameObject Take(float scale)
        {
            GameObject go = null;
            while (_free.Count > 0 && go == null)
            {
                go = _free[_free.Count - 1];
                _free.RemoveAt(_free.Count - 1);
            }
            if (go == null) go = KatyushaModel.Rocket("NDR_M13_Flight");
            if (go == null) return null;
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.SetActive(true);
            return go;
        }

        static void Give(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            if (_free.Count < 40) _free.Add(go);
            else UnityEngine.Object.Destroy(go);
        }

        static Vector3 At(Flight f, float age)
        {
            if (age < Slide)
            {
                float s = age / Slide;
                return Vector3.Lerp(f.Slot, f.Tip, s * s);
            }
            float t = Mathf.Clamp01((age - Slide) / f.Time);
            Vector3 p = Vector3.Lerp(f.Tip, f.To, t);
            p.y += f.Apex * 4f * t * (1f - t);
            return p;
        }

        internal static void Tick(float dt)
        {
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                Flight f = _flights[i];
                if (f.SupportMission && f.SupportGeneration != TowerSupport.WorldGeneration)
                { Give(f.Go); _flights.RemoveAt(i); continue; }
                f.Age += dt;
                Vector3 p = At(f, f.Age);
                Vector3 ahead = At(f, f.Age + 0.05f) - p;
                if (f.Go != null)
                {
                    f.Go.transform.position = p;
                    if (ahead.sqrMagnitude > 0.0001f) f.Go.transform.rotation = Quaternion.LookRotation(ahead.normalized);
                }
                if (f.Age < Burn + Slide && Time.time >= f.NextTrail)
                {
                    f.NextTrail = Time.time + 0.04f;
                    Vector3 back = ahead.sqrMagnitude > 0.0001f ? -ahead.normalized : Vector3.down;
                    KatyushaFx.Motor(p + back * 2.2f, back, f.Age < Burn * 0.6f);
                }
                if (f.Age < Slide + f.Time) continue;
                _flights.RemoveAt(i);
                Give(f.Go);
                if (!f.SupportMission) Katyusha.Burst(f.To, f.Mine);
                else if (!AirEvents.Safe(f.To))
                {
                    bool previous = Mortar.AnyFaction;
                    Mortar.AnyFaction = true;
                    try { Katyusha.Burst(f.To, Crocodile.IsMaster()); }
                    finally { Mortar.AnyFaction = previous; }
                }
            }
        }
    }

    // =====================================================================
    // Map overlay

    /// <summary>The fire control on the game's map: range rings, safe zones,
    /// the laid target area (solid, hatched) and the wanted one (faint).
    /// Hard-clipped to the visible map like the howitzer's overlay.</summary>
    internal static class KatyushaMap
    {
        internal static void Draw(Katyusha.Launcher l, Vector3 want, bool wantHave, bool pinned, bool queued)
        {
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out cam, out world, out map)) return;
            Rect clip;
            if (!MapTools.MapScreenRect(texture, cam, out clip)) return;
            Rect view;
            if (MapTools.MapViewportRect(texture, cam, out view)) clip = Intersect(clip, view);
            if (clip.width < 2f || clip.height < 2f) return;
            Vector3 pivot = Katyusha.Pivot(l);
            if (Mathf.Abs(pivot.x - EastWorld.MapCentre.x) > world.x * 0.75f
                || Mathf.Abs(pivot.z - EastWorld.MapCentre.y) > world.y * 0.75f) return;

            GUI.BeginClip(clip);
            try
            {
                Ctx c = new Ctx(texture, cam, world, map, clip.position);
                // reach: dashed rings at the minimum and maximum centre range
                Ring(c, pivot, Katyusha.RangeMin, new Color(1f, 0.55f, 0.2f, 0.75f), 72, true);
                Ring(c, pivot, Katyusha.RangeMax, new Color(1f, 0.85f, 0.4f, 0.75f), 120, true);
                // safe zones in red
                for (int i = 0; i < SafeZones.Circles.Count; i++)
                    Ring(c, new Vector3(SafeZones.Circles[i].x, 0f, SafeZones.Circles[i].y), SafeZones.Radii[i],
                         new Color(1f, 0.15f, 0.15f, 0.9f), 48, false);
                // the launcher
                Vector2 g;
                if (c.Gui(pivot, out g)) Box(g, 7f, new Color(1f, 0.9f, 0.5f, 1f));

                float bearing;
                Vector3 laid = Katyusha.LaidPoint(l, out bearing);
                if (wantHave)
                {
                    Vector3 d = want - pivot;
                    float wb = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    bool ok = Katyusha.Refusal(l, want) == null;
                    Ellipse(c, want, wb, ok ? new Color(1f, 1f, 1f, 0.45f) : new Color(1f, 0.25f, 0.2f, 0.6f), false);
                    if (c.Gui(want, out g)) Cross(g, pinned ? 9f : 6f, pinned ? new Color(1f, 0.85f, 0.3f, 1f)
                                                                         : new Color(1f, 1f, 1f, 0.8f));
                }
                Color area = l.Firing ? new Color(1f, 0.3f, 0.15f, 0.95f)
                    : (queued ? new Color(1f, 0.8f, 0.2f, 0.95f) : new Color(1f, 0.55f, 0.1f, 0.9f));
                Ellipse(c, laid, bearing, area, true);
                Vector2 a, b;
                if (c.Gui(pivot, out a) && c.Gui(laid, out b)) Segment(a, b, new Color(1f, 0.6f, 0.2f, 0.6f), 1.5f);
            }
            finally { GUI.EndClip(); }
        }

        sealed class Ctx
        {
            public Component Texture; public Camera Cam; public Vector2 World, Map, Offset;
            public Ctx(Component t, Camera c, Vector2 w, Vector2 m, Vector2 o)
            { Texture = t; Cam = c; World = w; Map = m; Offset = o; }
            public bool Gui(Vector3 p, out Vector2 g)
            {
                if (!MapTools.WorldToGui(p, Texture, Cam, World, Map, out g)) return false;
                g -= Offset;
                return true;
            }
        }

        static void Ellipse(Ctx c, Vector3 centre, float bearing, Color col, bool hatch)
        {
            float a = Katyusha.EllipseSemiLength, b = Katyusha.EllipseSemiWidth;
            float rad = bearing * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            const int n = 48;
            Vector2 prev = Vector2.zero;
            bool havePrev = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i * Mathf.PI * 2f / n;
                Vector3 p = centre + fwd * (Mathf.Cos(t) * a) + right * (Mathf.Sin(t) * b);
                Vector2 g;
                if (!c.Gui(p, out g)) { havePrev = false; continue; }
                if (havePrev) Segment(prev, g, col, hatch ? 2.5f : 1.5f);
                prev = g;
                havePrev = true;
            }
            if (!hatch) return;
            // Chords along the line of fire: the whole area reads as "hit".
            Color h = new Color(col.r, col.g, col.b, col.a * 0.35f);
            for (int k = -4; k <= 4; k++)
            {
                float s = k / 5f;
                float half = a * Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
                Vector3 p0 = centre + right * (s * b) - fwd * half;
                Vector3 p1 = centre + right * (s * b) + fwd * half;
                Vector2 g0, g1;
                if (c.Gui(p0, out g0) && c.Gui(p1, out g1)) Segment(g0, g1, h, 1f);
            }
        }

        static void Ring(Ctx c, Vector3 centre, float r, Color col, int n, bool dashed)
        {
            Vector2 prev = Vector2.zero;
            bool havePrev = false;
            for (int i = 0; i <= n; i++)
            {
                float t = i * Mathf.PI * 2f / n;
                Vector3 p = centre + new Vector3(Mathf.Sin(t) * r, 0f, Mathf.Cos(t) * r);
                Vector2 g;
                if (!c.Gui(p, out g)) { havePrev = false; continue; }
                if (havePrev && (!dashed || (i & 1) == 0)) Segment(prev, g, col, 1.5f);
                prev = g;
                havePrev = true;
            }
        }

        static void Segment(Vector2 a, Vector2 b, Color c, float width)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f || len > 6000f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 keep = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.matrix = keep;
        }

        static void Box(Vector2 g, float r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(g.x - r * 0.5f, g.y - r * 0.5f, r, r), Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Cross(Vector2 g, float r, Color c)
        {
            Segment(new Vector2(g.x - r, g.y), new Vector2(g.x + r, g.y), c, 1.5f);
            Segment(new Vector2(g.x, g.y - r), new Vector2(g.x, g.y + r), c, 1.5f);
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }
    }

    // =====================================================================
    // Safe zones

    /// <summary>Where no rocket may fall and no salvo may start: every
    /// settlement the game flags IsSafeSettlement (a circle of SafeZoneRadius)
    /// and the no-fly zones of the neutral faction (NoFly.Zones, N12).</summary>
    internal static class SafeZones
    {
        internal static readonly List<Vector2> Circles = new List<Vector2>();
        internal static readonly List<float> Radii = new List<float>();
        static float _next;

        internal static void Refresh()
        {
            if (Time.time < _next) return;
            _next = Time.time + 10f;
            Circles.Clear();
            Radii.Clear();
            float r = Mathf.Max(0f, Katyusha.CfgSafeRadius == null ? 300f : Katyusha.CfgSafeRadius.Value);
            try
            {
                Type t = RevivalPlugin.TypeByName("NPC_Settlement");
                if (t != null && r > 0f)
                {
                    FieldInfo safe = AccessTools.Field(t, "IsSafeSettlement");
                    Component[] all = SettlementScan.All();      // P1b: no world walk
                    for (int i = 0; i < all.Length; i++)
                    {
                        Component s = all[i];
                        if (s == null || safe == null || safe.FieldType != typeof(bool)) continue;
                        if (!(bool)safe.GetValue(s)) continue;
                        Circles.Add(new Vector2(s.transform.position.x, s.transform.position.z));
                        Radii.Add(r);
                    }
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Katyusha: safe settlements: " + ex.Message); }
            for (int i = 0; i < NoFly.Zones.Count; i++)
            {
                NoFly.Zone z = NoFly.Zones[i];
                if (z == null || z.Faction != "neutral" || !z.Circle) continue;
                Circles.Add(z.Centre);
                Radii.Add(z.Radius);
            }
        }

        internal static bool Inside(Vector3 p, float margin)
        {
            Refresh();
            Vector2 q = new Vector2(p.x, p.z);
            for (int i = 0; i < Circles.Count; i++)
                if ((q - Circles[i]).magnitude < Radii[i] + margin) return true;
            for (int i = 0; i < NoFly.Zones.Count; i++)
            {
                NoFly.Zone z = NoFly.Zones[i];
                if (z == null || z.Faction != "neutral" || z.Circle) continue;
                if (NoFly.Contains(z, new Vector3(p.x, -100000f, p.z))) return true;
            }
            return false;
        }

        /// <summary>Does the target ellipse touch a safe zone? The rim at 32
        /// points, the half-rim, the centre, and each zone centre inside it.</summary>
        internal static bool Overlaps(Vector3 centre, float bearing, float a, float b)
        {
            float rad = bearing * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            if (Inside(centre, 0f)) return true;
            for (int i = 0; i < 32; i++)
            {
                float t = i * Mathf.PI * 2f / 32f;
                Vector3 rim = centre + fwd * (Mathf.Cos(t) * a) + right * (Mathf.Sin(t) * b);
                if (Inside(rim, 0f)) return true;
                if ((i & 3) == 0 && Inside(centre + (rim - centre) * 0.5f, 0f)) return true;
            }
            for (int i = 0; i < Circles.Count; i++)
            {
                Vector3 d = new Vector3(Circles[i].x - centre.x, 0f, Circles[i].y - centre.z);
                float u = Vector3.Dot(d, fwd) / a, v = Vector3.Dot(d, right) / b;
                if (u * u + v * v <= 1f) return true;
            }
            return false;
        }
    }

    // =====================================================================
    // Travel lock

    /// <summary>A Katyusha whose rack is not in its travel position does not
    /// drive: prefix on VehicleGameSystem::InputAxis (the ArtyVehicleTravel
    /// pattern) zeroes the throttle wish and pulls the handbrake. Its first
    /// line is one static bool that is false while every rack is stowed.</summary>
    public static class KatyushaTravel
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                MethodInfo input = vgs == null ? null : AccessTools.Method(vgs, "InputAxis",
                    new Type[] { typeof(float), typeof(float), typeof(bool) }, null);
                if (input == null)
                {
                    RevivalPlugin.L.LogWarning("Katyusha travel lock: VehicleGameSystem.InputAxis missing.");
                    return;
                }
                harmony.Patch(input, new HarmonyMethod(typeof(KatyushaTravel).GetMethod("Prefix")),
                              null, null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha travel lock: " + ex); }
        }

        public static void Prefix(object __instance, ref float __0, ref bool __2)
        {
            try
            {
                if (!Katyusha.AnyUnstowed) return;
                Component vgs = __instance as Component;
                if (vgs == null || !Katyusha.IstKatyusha(vgs.transform)) return;
                if (!Katyusha.Unstowed(vgs.gameObject)) return;
                __0 = 0f;
                __2 = true;
            }
            catch (Exception) { }
        }
    }

    // =====================================================================
    // Effects

    /// <summary>Four shared, hand-emitted particle systems and a few pooled
    /// lights - the GepardFx pattern. The launch cloud is VITAL (counterplay):
    /// it is emitted whatever [Effects] ParticleDensity says, only thinner.</summary>
    internal static class KatyushaFx
    {
        static ParticleSystem _flash, _fire, _smoke, _dust;
        static Texture2D _soft;
        static readonly List<Light> _lights = new List<Light>();
        static readonly List<float> _lightEnd = new List<float>();
        static GameObject _host;

        static bool Ready()
        {
            if (_flash != null && _fire != null && _smoke != null && _dust != null) return true;
            try
            {
                if (_soft == null) _soft = Soft();
                if (_flash == null) _flash = Make("NDR Katyusha flash", true, 0f, false, 400);
                if (_fire == null) _fire = Make("NDR Katyusha fire", true, 0.6f, true, 1500);
                if (_smoke == null) _smoke = Make("NDR Katyusha smoke", false, -0.02f, false, 4000);
                if (_dust == null) _dust = Make("NDR Katyusha dust", false, 0.05f, false, 2500);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha effects: " + ex.Message); }
            return _flash != null && _fire != null && _smoke != null && _dust != null;
        }

        static Texture2D Soft()
        {
            Texture2D t = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            Color[] px = new Color[64 * 64];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x - 31.5f) / 31.5f, dy = (y - 31.5f) / 31.5f;
                    float a = Mathf.Clamp01(1f - dx * dx - dy * dy);
                    px[y * 64 + x] = new Color(1f, 1f, 1f, a * a);
                }
            t.SetPixels(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static ParticleSystem Make(string name, bool additive, float gravity, bool stretch, int max)
        {
            string[] shaders = additive
                ? new string[] { "Particles/Additive", "Legacy Shaders/Particles/Additive" }
                : new string[] { "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended" };
            Shader shader = null;
            for (int i = 0; i < shaders.Length && shader == null; i++) shader = Shader.Find(shaders[i]);
            if (shader == null) return null;
            Material m = new Material(shader);
            m.mainTexture = _soft;
            m.hideFlags = HideFlags.HideAndDontSave;

            GameObject go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.duration = 1f;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = new ParticleSystem.MinMaxCurve(gravity);
            main.maxParticles = max;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f);
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            if (!additive)
            {
                ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
                col.enabled = true;
                Gradient fade = new Gradient();
                fade.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f) }, new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.75f, 0.45f),
                    new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(fade);
                ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 2.6f));
                ParticleSystem.LimitVelocityOverLifetimeModule drag = ps.limitVelocityOverLifetime;
                drag.enabled = true;
                drag.limit = new ParticleSystem.MinMaxCurve(2f);
                drag.dampen = 0.08f;
            }
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = m;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretch) { r.velocityScale = 0.04f; r.lengthScale = 1f; }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();   // hand-emitted: Fx.Keep() gates every Emit (NDR P9)
            return ps;
        }

        static void Emit(ParticleSystem ps, Vector3 p, Vector3 v, float size, float life, Color c, bool vital)
        {
            if (!vital && !Fx.Keep()) return;   // NDR P9: [Effects] ParticleDensity
            ParticleSystem.EmitParams e = new ParticleSystem.EmitParams();
            e.position = p;
            e.velocity = v;
            e.startSize = size;
            e.startLifetime = life;
            e.startColor = c;
            ps.Emit(e, 1);
        }

        static Vector3 Rnd() { return UnityEngine.Random.onUnitSphere; }
        static float R(float a, float b) { return UnityEngine.Random.Range(a, b); }

        /// <summary>One rocket leaving the rails: flame out of the tail, and
        /// the cloud of smoke and dust that swallows the truck - a salvo is
        /// seen from far away, which is the point.</summary>
        internal static void Launch(Vector3 at, Vector3 dir, Vector3 truck, float k)
        {
            if (!Ready()) return;
            float life = Katyusha.SmokeSeconds;
            Emit(_flash, at, dir * 5f, 4f * k, 0.12f, new Color(1f, 0.75f, 0.45f, 1f), false);
            for (int i = 0; i < 6; i++)
                Emit(_fire, at - dir * 0.5f * k, (-dir + Rnd() * 0.5f).normalized * R(8f, 20f) * k, 0.3f * k,
                     R(0.15f, 0.4f), new Color(1f, 0.65f, 0.3f, 1f), false);
            // backblast into the bed and round the truck: the vital cloud
            int n = Fx.Keep() ? 7 : 3;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = truck + new Vector3(R(-4f, 4f), R(0f, 2.5f), R(-4f, 4f)) * k;
                Emit(_smoke, p, (Rnd() + Vector3.up * 0.6f) * R(0.6f, 1.6f) * k, R(3.5f, 6f) * k,
                     life * R(0.7f, 1.1f), new Color(0.78f, 0.76f, 0.72f, 0.55f), true);
            }
            for (int i = 0; i < 3; i++)
                Emit(_dust, truck + new Vector3(R(-5f, 5f), 0.3f, R(-5f, 5f)) * k,
                     new Vector3(R(-1f, 1f), R(0.2f, 0.6f), R(-1f, 1f)) * 2f * k, R(3f, 5f) * k,
                     life * R(0.4f, 0.7f), new Color(0.55f, 0.49f, 0.4f, 0.5f), true);
            Flash(at, 40f * k, 3f, 0.18f);
        }

        /// <summary>Behind a rocket in flight: flame while the motor burns,
        /// a white trail after it (off with [Graphics] KatyushaRocketTrails).</summary>
        internal static void Motor(Vector3 at, Vector3 back, bool flame)
        {
            if (!Ready()) return;
            if (flame) Emit(_flash, at, back * 2f, 3.5f, 0.06f, new Color(1f, 0.7f, 0.35f, 1f), false);
            if (!Katyusha.Trails) return;
            Emit(_smoke, at, back * 3f + Rnd() * 0.6f, R(2.2f, 3.2f), R(2.5f, 4f),
                 new Color(0.86f, 0.85f, 0.82f, 0.45f), false);
        }

        /// <summary>A rocket's burst: flash, fireball, earth thrown up, a dark
        /// column of smoke, glowing splinters and a flash of light.</summary>
        internal static void Explosion(Vector3 at)
        {
            if (!Ready()) return;
            float s = Katyusha.Radius / 32f;
            Emit(_flash, at + Vector3.up * 2f, Vector3.zero, 26f * s, 0.14f, new Color(1f, 0.78f, 0.45f, 1f), false);
            for (int i = 0; i < 6; i++)
                Emit(_flash, at + Vector3.up * 3f + Rnd() * 4f * s, Rnd() * 6f + Vector3.up * 4f, R(9f, 15f) * s,
                     R(0.3f, 0.6f), new Color(1f, 0.55f, 0.25f, 1f), false);
            for (int i = 0; i < 14; i++)
                Emit(_fire, at + Vector3.up, (Rnd() + Vector3.up * 0.8f).normalized * R(25f, 60f) * s, 0.5f,
                     R(0.4f, 1.1f), new Color(1f, 0.62f, 0.28f, 1f), false);
            int dust = Fx.Keep() ? 8 : 3;
            for (int i = 0; i < dust; i++)
                Emit(_dust, at + new Vector3(R(-6f, 6f), 1f, R(-6f, 6f)) * s,
                     (Vector3.up * R(6f, 14f) + new Vector3(R(-4f, 4f), 0f, R(-4f, 4f))) * s, R(8f, 13f) * s,
                     R(2.5f, 4.5f), new Color(0.42f, 0.36f, 0.28f, 0.8f), true);
            for (int i = 0; i < 5; i++)
                Emit(_smoke, at + Vector3.up * R(3f, 10f) * s + Rnd() * 3f * s, Vector3.up * R(2f, 5f) * s,
                     R(10f, 16f) * s, R(6f, 10f), new Color(0.16f, 0.15f, 0.14f, 0.8f), false);
            Flash(at + Vector3.up * 4f, 90f * s, 6f, 0.28f);
        }

        static void Flash(Vector3 at, float range, float intensity, float seconds)
        {
            if (!Anim.Lights) return;   // NDR P9: [Effects] ExplosionLights
            try
            {
                if (_host == null)
                {
                    _host = new GameObject("NDR Katyusha lights");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _lights.Clear();
                    _lightEnd.Clear();
                }
                int pick = -1;
                for (int i = 0; i < _lights.Count; i++)
                    if (_lights[i] != null && !_lights[i].enabled) { pick = i; break; }
                if (pick < 0 && _lights.Count < 6)
                {
                    GameObject go = new GameObject("flash");
                    go.transform.SetParent(_host.transform, false);
                    Light l = go.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.shadows = LightShadows.None;
                    l.color = new Color(1f, 0.7f, 0.4f, 1f);
                    l.enabled = false;
                    _lights.Add(l);
                    _lightEnd.Add(0f);
                    pick = _lights.Count - 1;
                }
                if (pick < 0) return;
                Light light = _lights[pick];
                light.transform.position = at;
                light.range = range;
                light.intensity = intensity;
                light.enabled = true;
                _lightEnd[pick] = Time.time + seconds;
            }
            catch (Exception) { }
        }

        internal static void Tick()
        {
            for (int i = 0; i < _lights.Count; i++)
            {
                Light l = _lights[i];
                if (l == null || !l.enabled) continue;
                float left = _lightEnd[i] - Time.time;
                if (left <= 0f) { l.enabled = false; continue; }
                l.intensity = Mathf.Min(l.intensity, left * 20f);
            }
        }
    }

    // =====================================================================
    // Sound

    /// <summary>The launch howl and the burst, synthesized once; a ring of
    /// pooled AudioSources (the FlakSound pattern). The burst is heard late by
    /// the distance over the speed of sound.</summary>
    internal static class KatyushaSound
    {
        static AudioClip _launch, _boom;
        static readonly List<AudioSource> _pool = new List<AudioSource>();
        static GameObject _host;
        static int _next;

        struct Pending { public float At; public Vector3 Pos; }
        static readonly List<Pending> _pending = new List<Pending>();

        internal static void Launch(Vector3 at)
        {
            if (_launch == null) _launch = Make(false);
            Play(_launch, at, 45f, 5000f, 1f);
        }

        internal static void Boom(Vector3 at)
        {
            Camera cam = CameraOwner.MainCamera();
            float d = cam == null ? 0f : Vector3.Distance(cam.transform.position, at);
            Pending p;
            p.At = Time.time + d / (343f * PlayerAn2.K);
            p.Pos = at;
            if (_pending.Count < 64) _pending.Add(p);
        }

        internal static void Tick()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pending[i].At) continue;
                if (_boom == null) _boom = Make(true);
                Play(_boom, _pending[i].Pos, 120f, 9000f, 1f);
                _pending.RemoveAt(i);
            }
        }

        static void Play(AudioClip clip, Vector3 at, float min, float max, float volume)
        {
            try
            {
                if (clip == null) return;
                if (_host == null)
                {
                    _host = new GameObject("NDR Katyusha sound");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _pool.Clear();
                }
                AudioSource s = null;
                for (int i = 0; i < _pool.Count; i++)
                {
                    AudioSource c = _pool[(_next + i) % _pool.Count];
                    if (c != null && !c.isPlaying) { s = c; break; }
                }
                if (s == null && _pool.Count < 24)
                {
                    GameObject go = new GameObject("voice");
                    go.transform.SetParent(_host.transform, false);
                    s = go.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.loop = false;
                    s.spatialBlend = 1f;
                    s.dopplerLevel = 0f;
                    s.rolloffMode = AudioRolloffMode.Logarithmic;
                    _pool.Add(s);
                }
                if (s == null) { s = _pool[_next % _pool.Count]; _next++; }
                s.transform.position = at;
                s.minDistance = min;
                s.maxDistance = max;
                s.volume = volume * UnityEngine.Random.Range(0.85f, 1f);
                s.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
                s.clip = clip;
                s.Play();
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Katyusha sound: " + ex.Message); }
        }

        /// <summary>Launch: a hiss that swells into a rising howl and dies
        /// away (the "organ" of a salvo comes from these overlapping).
        /// Burst: a sharp crack and a long rolling rumble.</summary>
        static AudioClip Make(bool boom)
        {
            const int rate = 44100;
            float seconds = boom ? 2.6f : 1.9f;
            float[] data = new float[Mathf.RoundToInt(rate * seconds)];
            System.Random rng = new System.Random(boom ? 1307 : 1941);
            float low = 0f, low2 = 0f, band = 0f, phase = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = i / (float)rate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                low += (noise - low) * (boom ? 0.02f : 0.25f);
                low2 += (low - low2) * (boom ? 0.05f : 0.3f);
                float v;
                if (boom)
                {
                    float crack = t < 0.03f ? noise * (1f - t / 0.03f) * 1.4f : 0f;
                    float body = low2 * 9f * Mathf.Exp(-t * 1.6f);
                    float thump = Mathf.Sin(2f * Mathf.PI * 42f * t) * Mathf.Exp(-t * 5f) * 0.9f;
                    v = crack + body + thump;
                }
                else
                {
                    float env = Mathf.Clamp01(t / 0.08f) * Mathf.Exp(-Mathf.Max(0f, t - 0.25f) * 2.2f);
                    band += (noise - band) * 0.6f;
                    float f = 380f + 520f * Mathf.Clamp01(t / 1.2f);
                    phase += 2f * Mathf.PI * f / rate;
                    float howl = Mathf.Sin(phase) * (0.35f + 0.25f * low2);
                    v = env * (band * 0.55f + low2 * 2.5f + howl * 0.45f);
                }
                data[i] = (float)Math.Tanh(v * 1.4f);
            }
            AudioClip clip = AudioClip.Create(boom ? "NDR Katyusha boom" : "NDR Katyusha launch",
                                              data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    // =====================================================================
    // Network

    /// <summary>The spawn marker in Photon's cached instantiation event
    /// (byte key 5, the howitzer's pattern) and one event channel.</summary>
    public static class KatyushaNet
    {
        const string Marker = "NDR_KATJUSHA_V1";
        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optType;

        public static object[] SpawnData() { return new object[] { null, Marker }; }

        internal static int Code()
        {
            return Mathf.Clamp(Katyusha.CfgEventCode == null ? 188 : Katyusha.CfgEventCode.Value, 0, 199);
        }

        internal static void InstallSpawnMarker(Harmony harmony)
        {
            try
            {
                Type peer = RevivalPlugin.TypeByName("NetworkingPeer");
                if (peer == null) return;
                MethodInfo target = null;
                MethodInfo[] methods = peer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                    if (methods[i].Name == "DoInstantiate" && methods[i].ReturnType == typeof(GameObject)
                        && methods[i].GetParameters().Length == 3) { target = methods[i]; break; }
                if (target == null)
                {
                    RevivalPlugin.L.LogWarning("Katyusha network: DoInstantiate missing.");
                    return;
                }
                harmony.Patch(target, null, new HarmonyMethod(typeof(KatyushaNet).GetMethod("SpawnPostfix")),
                              null, null, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha network marker: " + ex); }
        }

        public static void SpawnPostfix(object __0, GameObject __result)
        {
            try
            {
                if (__result == null || Katyusha.IstKatyusha(__result.transform)) return;
                System.Collections.IDictionary ev = __0 as System.Collections.IDictionary;
                if (ev == null || !ev.Contains((byte)5)) return;
                object[] data = ev[(byte)5] as object[];
                if (data == null || data.Length < 2 || data[0] != null
                    || !string.Equals(data[1] as string, Marker, StringComparison.Ordinal)) return;
                Katyusha.Umbauen(__result);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Katyusha network, spawn marker: " + ex); }
        }

        internal static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                FieldInfo onEvent = photon == null ? null : AccessTools.Field(photon, "OnEventCall");
                _raise = photon == null ? null : AccessTools.Method(photon, "RaiseEvent", null, null);
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (onEvent == null || _raise == null)
                {
                    _failed = true;
                    RevivalPlugin.L.LogWarning("Katyusha net: RaiseEvent or OnEventCall missing.");
                    return;
                }
                MethodInfo mine = typeof(KatyushaNet).GetMethod("OnPhotonEvent",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                Delegate current = onEvent.GetValue(null) as Delegate;
                onEvent.SetValue(null, Delegate.Combine(current, handler));
                _hooked = true;
                RevivalPlugin.L.LogInfo("Katyusha net hooked: event code " + Code() + ".");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("Katyusha net not hooked: " + ex);
            }
        }

        internal static void Send(float[] content, bool reliable)
        {
            if (!_hooked) return;
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] { (byte)Code(), content, reliable, opts });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Katyusha net send: " + ex.Message); }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 2 || !Katyusha.Enabled) return;
                int kind = Mathf.RoundToInt(f[0]);
                int view = Mathf.RoundToInt(f[1]);
                if (kind == 0 && f.Length >= 5) Katyusha.ReceivePose(view, f[2], f[3], f[4] > 0.5f);
                else if (kind == 1 && f.Length >= 5) Katyusha.ReceiveSalvo(f);
                else if (kind == 2 && f.Length >= 3) Katyusha.ReceiveCount(view, Mathf.RoundToInt(f[2]));
                else if (kind == 3 && f.Length >= 5) Katyusha.ReceiveImpact(new Vector3(f[2], f[3], f[4]));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Katyusha net receive: " + ex.Message); }
        }
    }

    // =====================================================================
    // Player lines

    internal static class KatyushaText
    {
        internal static string Label() { return Loc.T("Катюша БМ-13 (16 ракет)", "Katyusha BM-13 (16 rockets)"); }
        internal static string Spawned(int n)
        {
            return Loc.T("Катюша перед вами, заряжено ", "Katyusha in front of you, loaded ") + n
                + Loc.T(" ракет. G в кабине - огонь с карты.", " rockets. G in the cab - fire from the map.");
        }
        internal static string Seat(int n, int cap, string key)
        {
            return Loc.T("Катюша: ", "Katyusha: ") + n + "/" + cap + Loc.T(" ракет   ", " rockets   ")
                + key + Loc.T(" - огонь с карты (на стоянке)", " - map fire control (standing)");
        }
        internal static string LoadPrompt(string key, int n, int cap)
        {
            return "[" + key + "] " + Loc.T("Зарядить ракеты М-13", "Load M-13 rockets") + " (" + n + "/" + cap + ")";
        }
        internal static string Controls(int n, int cap)
        {
            return Loc.T("Мышь - район цели, ЛКМ - залп всеми ", "Mouse - target area, LMB - salvo of all ")
                + n + "/" + cap + Loc.T(", ПКМ - отмена, G - закрыть", ", RMB - cancel, G - close");
        }
        internal static string Plate(int n, int cap, float dist, float bearing)
        {
            return Loc.T("Ракет ", "Rockets ") + n + "/" + cap + "   " + dist.ToString("0") + " m   "
                + Mathf.Repeat(bearing, 360f).ToString("000") + " deg";
        }
        internal static string StopFirst() { return Loc.T("Сначала остановитесь", "Stop the truck first"); }
        internal static string SalvoRunning() { return Loc.T("Идёт залп", "Salvo in progress"); }
        internal static string Unpinned() { return Loc.T("Цель снята", "Target released"); }
        internal static string Empty() { return Loc.T("Направляющие пусты - зарядите ракеты М-13", "The rails are empty - load M-13 rockets"); }
        internal static string Queued() { return Loc.T("Наводка... залп по готовности", "Laying... salvo when laid"); }
        internal static string Laying() { return Loc.T("Наводка...", "Laying..."); }
        internal static string Ready() { return Loc.T("Готово - ЛКМ залп", "Ready - LMB salvo"); }
        internal static string Done() { return Loc.T("Залп окончен", "Salvo complete"); }
        internal static string TooClose(float d, float min)
        {
            return Loc.T("Слишком близко", "Too close") + ": " + d.ToString("0") + " m < " + min.ToString("0") + " m";
        }
        internal static string TooFar(float d, float max)
        {
            return Loc.T("Слишком далеко", "Too far") + ": " + d.ToString("0") + " m > " + max.ToString("0") + " m";
        }
        internal static string IntoSafeZone() { return Loc.T("Район цели задевает безопасную зону", "The target area touches a safe zone"); }
        internal static string FromSafeZone() { return Loc.T("Из безопасной зоны стрелять нельзя", "No firing from inside a safe zone"); }
        internal static string Salvo(int n, float d)
        {
            return Loc.T("Залп! ", "Salvo! ") + n + Loc.T(" ракет на ", " rockets at ") + d.ToString("0") + " m";
        }
        internal static string Heard(string dir, float d)
        {
            return Loc.T("Слышен залп реактивных снарядов: ", "Rocket salvo heard: ") + dir + ", ~"
                + (Mathf.Round(d / 100f) * 100f).ToString("0") + " m";
        }
        internal static string Full(int n, int cap) { return Loc.T("Направляющие заряжены: ", "Rails loaded: ") + n + "/" + cap; }
        internal static string NotStowed() { return Loc.T("Сначала уложите установку (закройте огонь)", "Stow the launcher first (close fire control)"); }
        internal static string NoRockets() { return Loc.T("Нет ракет М-13 в рюкзаке", "No M-13 rockets in the backpack"); }
        internal static string LoadedSome(int k, int n, int cap)
        {
            return Loc.T("Заряжено ", "Loaded ") + k + Loc.T(", ракет больше нет - ", ", no more rockets - ") + n + "/" + cap;
        }
        internal static string Loading(int n, int cap) { return Loc.T("Зарядка ракеты ", "Loading rocket ") + n + "/" + cap; }
        internal static string Interrupted() { return Loc.T("Прервано", "Interrupted"); }
        internal static string OutOfReach() { return Loc.T("Установка вне досягаемости", "The launcher is out of reach"); }
    }
}

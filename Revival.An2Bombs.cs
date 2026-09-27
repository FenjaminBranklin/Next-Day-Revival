// Next Day: Survival - Revival Toolkit
//
// THE AN-2 AS A BOMBER. The player-flown An-2 (Revival.PlayerAn2.cs) carries
// up to six small FAB-50 bombs (item 2072) on its racks. docs/ai/tasks/
// an2-bombs.md has the numbers and the in-game checklist.
//
// WHAT A PLAYER DOES
//   1. Loading. On foot beside a parked An-2 with FAB-50s in the backpack,
//      the load key (R) moves them onto the racks, up to BombCapacity. The
//      bombs come from the airfield loot (the "military" pool, D2a and the
//      other bunkers) or from the admin panel. An An-2 the admin spawns
//      "ready" comes with a full load.
//   2. The sight. The pilot presses the sight key (G, in the air - on the
//      ground G is the engine): the view drops under the belly, WW2 style -
//      a downward camera laid on a crosshair on the ground. The mouse moves
//      the crosshair; with the pilot's hands off A/D the aeroplane banks
//      itself until its track runs through it (Steer, PlayerAn2.Assist) and
//      flies the run-in wings level. The amber pipper is the point where a
//      bomb released NOW bursts; it slides up the track line onto the
//      crosshair, and "DROP!" flashes when they meet. The nadir mark is the
//      point straight below - the gap to the pipper is the lead. Readouts:
//      height over the ground, ground speed, fall time, lead and drift.
//   3. The release key (left mouse button) drops one bomb. It is a REAL falling projectile:
//      it leaves with the aeroplane's velocity, falls under gravity with a
//      little drag, and the sight's prediction runs the same equations, so
//      a steady, level, low run puts it on a vehicle-sized target. The
//      scatter grows with the height of the drop and with the bank at
//      release, so a sloppy run misses.
//
// HOW IT GOES OFF (the mortar's rules, reused, not copied)
//   The dropping client flies the bomb. At impact:
//     the picture  RocketHook.Detonate - the game's own networked grenade
//                  explosion, one blast and one bang on every client, with 0
//                  damage of its own (RevivalMortar.cs says why);
//     the damage   Mortar.Sweep with the bomb's radius and peaks: NPCs with
//                  owner 0 (credited to nobody), vehicles on the master only
//                  (part 14, the explosion part, so VehicleArmor applies),
//                  players from the dropper only and never his own faction;
//                  Mortar.FactionShield is armed at release;
//     the master   a dropper who is not the master sends the burst on this
//                  file's event, and the master sweeps the NPCs and vehicles
//                  it owns - the mortar's SendImpact pattern;
//     the depot    a burst at the D1 fuel depot (An2Repair.Depot) sets its
//                  fuel off: the reserve is gone and a second blast follows.
//   Every other client flies a visual copy of the bomb from the drop event
//   (same start, same velocity, same equations) and removes it at the burst.
//   A bomb that hits the ground before ArmSeconds of fall is a dud: the fuze
//   has not armed. It also keeps a very low drop from blowing up its own
//   aeroplane.
//
// NETWORK. One Photon event code ([An2Bombs] NetworkEventCode, 158: the
// An-2's band 150-157 is full), float[] payloads, the first float the kind:
//   0 drop   id, x, y, z, vx, vy, vz            dropper -> others (visual)
//   1 burst  id, x, y, z, dud                   dropper -> others (master sweeps)
//   2 load   view, count                        whoever changed it -> others;
//                                               the master repeats every 5 s
//
// SETTINGS SWITCHES (task P9 unifies settings later):
//   [Gameplay] An2Bombs        the whole feature
//   [Gameplay] An2BombDepotHit bombs set the D1 depot off
//   [Graphics] An2Bombsight    the downward sight camera (off: the overlay
//                              is drawn over the normal view)
//   [Graphics] An2BombModel    the falling bomb is drawn
// The tuning lives in [An2Bombs].
//
// C# 3.0. ASCII comments and logs; player-facing strings through Loc.T with
// real Cyrillic, so this file is UTF-8 without BOM.
//
// SEAMS OUTSIDE THIS FILE:
//   RevivalPlugin.cs    BindConfig / AddItems / Update / OnGUI
//   Revival.PlayerAn2.cs LateTick hands the camera to SightCamera; Build(ready)
//                       calls FullLoad; Plane / Velocity accessors
//   Revival.Admin.cs    "Spawn An-2 (ready)" and "An-2 bombs x6"
//   Revival.Airfield.cs the loot pool (Pool)
//   RevivalMortar.cs    Sweep overload, Sound, FactionShield
//   Revival.An2Repair.cs DepotHit

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Bombs for the player-flown An-2: racks, sight, release,
    /// ballistic fall, burst through the mortar's damage rules.</summary>
    internal static class An2Bombs
    {
        /// <summary>FAB-50, after the An-2 repair parts (2069-2071).</summary>
        internal const int ItemId = 2072;
        const int DONOR = 2030;              // the generic carryable, like 2069-2071
        const float G = 9.81f;
        const float Step = 0.02f;            // the bomb's integration step, s
        const float RealLength = 1.07f;      // FAB-50, metres nose to fin box

        static float K { get { return PlayerAn2.K; } }

        // ============================================================= config

        internal static ConfigEntry<bool> CfgGameplay, CfgDepotHit, CfgSightView, CfgModel;
        internal static ConfigEntry<string> CfgSightKey, CfgReleaseKey, CfgLoadKey;
        internal static ConfigEntry<int> CfgCapacity, CfgEventCode, CfgControls;
        internal static ConfigEntry<float> CfgRadius, CfgNpcDamage, CfgVehicleDamage,
            CfgPlayerDamage, CfgDrag, CfgScatter, CfgScatterPerHeight, CfgScatterPerBank,
            CfgArmSeconds, CfgInterval, CfgSightFov, CfgDepotReach, CfgAimSensitivity;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgGameplay = cfg.Bind("Gameplay", "An2Bombs", true,
                "The player-flown An-2 carries FAB-50 bombs (item 2072), has a "
                + "bombsight and drops them. Needs [PlayerAn2] Enabled. Every "
                + "client must have the same setting.");
            CfgDepotHit = cfg.Bind("Gameplay", "An2BombDepotHit", true,
                "A bomb bursting at the D1 fuel depot of the airfield sets its "
                + "fuel off (a second blast; the depot is dry until it refills, "
                + "[An2Repair] DepotRefillMinutes). Needs [An2Repair] Enabled.");
            CfgSightView = cfg.Bind("Graphics", "An2Bombsight", true,
                "The sight key switches the pilot's view to the downward "
                + "bombsight camera. Off: the sight marks are drawn over the "
                + "normal view instead.");
            CfgModel = cfg.Bind("Graphics", "An2BombModel", true,
                "The falling bomb is drawn (the FAB-50 model). Off: it falls unseen.");

            const string S = "An2Bombs";
            CfgCapacity = cfg.Bind(S, "BombCapacity", 6,
                "FAB-50s on the racks of one An-2 (the real one carried 4-6 small "
                + "bombs under the lower wing).");
            CfgSightKey = cfg.Bind(S, "SightKey", "G",
                "Pilot, in the air: open or close the bombsight. The same key as "
                + "[PlayerAn2] EngineKey is fine: on the ground it is the engine.");
            CfgReleaseKey = cfg.Bind(S, "ReleaseKey", "Mouse0",
                "Pilot: drop one bomb (Mouse0 = left mouse button).");
            CfgLoadKey = cfg.Bind(S, "LoadKey", "R",
                "On foot beside a parked An-2: move FAB-50s from the backpack onto its racks.");
            CfgRadius = cfg.Bind(S, "BlastRadius", 25f,
                "Metres. Damage falls off linearly to zero here. A vehicle needs "
                + "a burst within a few metres to be hurt badly.");
            CfgNpcDamage = cfg.Bind(S, "NpcDamage", 500f, "Damage to an NPC at the burst point.");
            CfgVehicleDamage = cfg.Bind(S, "VehicleDamage", 1200f,
                "Damage to a vehicle at the burst point (explosion part, so the "
                + "vehicle armour rules apply).");
            CfgPlayerDamage = cfg.Bind(S, "PlayerDamage", 300f,
                "Damage to a player at the burst point. Never to the dropper's own faction.");
            CfgDrag = cfg.Bind(S, "Drag", 0.0006f,
                "Air drag of the bomb, 1/m (deceleration = Drag x speed^2). The "
                + "sight uses the same number, so it only changes how far a bomb trails.");
            CfgScatter = cfg.Bind(S, "Scatter", 1.0f,
                "Metres of random miss (one sigma) of every bomb, however careful the run.");
            CfgScatterPerHeight = cfg.Bind(S, "ScatterPerHeight", 0.015f,
                "Extra metres of miss per metre of drop height: low runs are more accurate.");
            CfgScatterPerBank = cfg.Bind(S, "ScatterPerBank", 0.12f,
                "Extra metres of miss per degree of bank at release: level the wings.");
            CfgArmSeconds = cfg.Bind(S, "ArmSeconds", 1.0f,
                "Seconds of fall before the fuze is armed. Earlier impact: a dud.");
            CfgInterval = cfg.Bind(S, "ReleaseInterval", 0.4f, "Seconds between two releases.");
            CfgSightFov = cfg.Bind(S, "SightFov", 40f, "Field of view of the bombsight camera, degrees.");
            CfgAimSensitivity = cfg.Bind(S, "AimSensitivity", 0.5f,
                "Degrees the bombsight crosshair moves per unit of mouse travel. "
                + "The aeroplane steers itself onto the crosshair.");
            CfgDepotReach = cfg.Bind(S, "DepotReach", 25f,
                "Metres from the D1 pump house within which a burst sets the depot off.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 158,
                "Photon event code (0..199). Must be the same on every client.");
            CfgControls = cfg.Bind(S, "ControlsLayout", 0,
                "Internal: which key/radius defaults this file has been moved to. Do not edit.");
            Migrate();
        }

        /// <summary>
        /// 2026-09-28: sight Z -> G, release R -> left mouse button, blast
        /// radius 12 -> 25 m. BepInEx keeps what a file already holds, so an
        /// old file that still carries the OLD DEFAULT is moved once; a value
        /// somebody chose stays. The stamp makes it once.
        /// </summary>
        static void Migrate()
        {
            if (CfgControls == null || CfgControls.Value >= 1) return;
            CfgControls.Value = 1;
            if (CfgSightKey.Value.Trim().Equals("Z", StringComparison.OrdinalIgnoreCase)) CfgSightKey.Value = "G";
            if (CfgReleaseKey.Value.Trim().Equals("R", StringComparison.OrdinalIgnoreCase)) CfgReleaseKey.Value = "Mouse0";
            if (Mathf.Abs(CfgRadius.Value - 12f) < 0.01f) CfgRadius.Value = 25f;
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogInfo("An2Bombs: config moved to layout 1 - sight " + CfgSightKey.Value
                + ", release " + CfgReleaseKey.Value + ", blast radius " + CfgRadius.Value + " m.");
        }

        /// <summary>The bombsight key; PlayerAn2 keeps the engine off it in the air.</summary>
        internal static KeyCode SightKey { get { return PlayerAn2.KeyOf(CfgSightKey, KeyCode.G); } }

        /// <summary>A key as the HUD names it: the mouse buttons in words.</summary>
        static string KeyName(KeyCode k)
        {
            if (k == KeyCode.Mouse0) return Loc.T("ЛКМ", "LMB");
            if (k == KeyCode.Mouse1) return Loc.T("ПКМ", "RMB");
            return k.ToString();
        }

        internal static bool Enabled
        {
            get { return PlayerAn2.Enabled && (_forced || (CfgGameplay != null && CfgGameplay.Value)); }
        }

        /// <summary>Set by the admin panel's ready An-2 with [Gameplay]
        /// An2Bombs off: the racks work for this session, the file keeps its
        /// value.</summary>
        static bool _forced;

        /// <summary>The admin panel's note on switched-off bombs, or null.</summary>
        internal static string OffNote()
        {
            if (CfgGameplay == null || CfgGameplay.Value || _forced) return null;
            return "[Gameplay] An2Bombs = false in the config - the ready An-2 switches it on for this session";
        }

        /// <summary>Switch the bombs on for this session (admin ready An-2);
        /// returns the note for the admin panel, or "" when already on.</summary>
        internal static string ForceOn()
        {
            if (OffNote() == null) return "";
            _forced = true;
            RevivalPlugin.L.LogWarning("An2Bombs: [Gameplay] An2Bombs = false - switched on for this session by the admin spawn.");
            return "[Gameplay] An2Bombs = false in the config - switched on for this session. ";
        }

        static float F(ConfigEntry<float> e, float fallback) { return e == null ? fallback : e.Value; }
        internal static int Capacity { get { return Mathf.Clamp(CfgCapacity == null ? 6 : CfgCapacity.Value, 1, 12); } }

        // ============================================================== items

        internal static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                ItemId, DONOR, false,
                "Авиабомба ФАБ-50", "FAB-50 bomb",
                "Небольшая фугасная авиабомба. Подвешивается на Ан-2 у самолёта "
                + "(клавиша загрузки), сбрасывается пилотом через бомбовый прицел.",
                "A small high-explosive aircraft bomb. Load it onto the An-2 at the "
                + "aeroplane (the load key); the pilot drops it through the bombsight.",
                "fab50.ndmesh", "fab50_diffuse.png", "fab50_normal.png",
                "fab50_icon.png", null,
                1, 0, 10.0f));
        }

        /// <summary>The airfield's loot with the bombs in it (Airfield.Draw):
        /// the "military" pool of the bunkers rolls a FAB-50 now and then.</summary>
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

        // ============================================================== state

        sealed class Bomb
        {
            public int Id;
            public Vector3 Pos;              // world units
            public Vector3 Vel;              // m/s
            public float Age, Carry;
            public bool Mine, Whistled;
            public GameObject Go;
        }

        static readonly List<Bomb> _bombs = new List<Bomb>();
        static readonly Dictionary<int, int> _count = new Dictionary<int, int>();   // view -> bombs aboard
        static bool _sight;
        static float _savedFov = -1f;
        static float _nextRelease, _nextHeartbeat, _errors;
        static string _hint = "";
        static float _hintUntil;

        // The crosshair: a point on the ground the pilot lays with the mouse.
        // The camera looks at it, the aeroplane steers onto it (Steer).
        static bool _aimSet;
        static Vector3 _aim;

        // Sight solution, refreshed every frame while the pilot flies.
        static bool _solved;
        static Vector3 _impact, _nadir;
        static float _fall, _agl, _lead, _drift, _gs;

        internal static bool SightOn { get { return _sight && CfgSightView != null && CfgSightView.Value; } }

        internal static int CountOf(GameObject go)
        {
            int n;
            int view = PlayerAn2.View(go);
            return view != 0 && _count.TryGetValue(view, out n) ? n : 0;
        }

        static void SetCount(GameObject go, int n, bool send)
        {
            int view = PlayerAn2.View(go);
            if (view == 0) return;
            n = Mathf.Clamp(n, 0, Capacity);
            _count[view] = n;
            if (send) Net.Send(new float[] { 2f, view, n }, true);
        }

        /// <summary>From PlayerAn2.Build for a "ready" An-2 (the admin panel):
        /// the racks full.</summary>
        internal static void FullLoad(GameObject go)
        {
            if (!Enabled || go == null) return;
            Net.EnsureHooked();
            SetCount(go, Capacity, true);
        }

        // ============================================================== frame

        internal static void Tick()
        {
            if (!Enabled) { if (_sight) CloseSight(); return; }
            try
            {
                Net.EnsureHooked();
                Fly(Time.deltaTime);
                Heartbeat();
                GameObject plane = PlayerAn2.Plane;
                if (plane == null)
                {
                    if (_sight) CloseSight();
                    _solved = false;
                    if (!PlayerAn2.Aboard && !PlayerHeli.Aboard) OnFoot();
                    return;
                }
                if (!PlayerAn2.Flying || PlayerAn2.Burning(plane))
                {
                    if (_sight) CloseSight();
                    _solved = false;
                    return;
                }
                // The prediction runs ahead up to 60 s of fall on the terrain
                // height data: only while the sight is open (Release solves
                // for itself).
                if (_sight) { Solve(plane); Aim(plane.transform); }
                else _solved = false;
                // Down on the wheels the sight has nothing to do, and the
                // same key starts the engine there (PlayerAn2.Tick).
                if (_sight && PlayerAn2.OnGround) CloseSight();
                if (!PlayerAn2.OnGround && GameUi.KeyDown(SightKey))
                {
                    if (_sight) CloseSight();
                    else
                    {
                        _sight = true;
                        _aimSet = false;
                        Hint(Loc.T("Бомбовый прицел - мышь наводит, самолёт доворачивает сам",
                                   "Bombsight - the mouse lays the crosshair, the aeroplane steers onto it")
                            + ". " + KeyName(PlayerAn2.KeyOf(CfgReleaseKey, KeyCode.Mouse0)) + " "
                            + Loc.T("сброс", "release"), 4f);
                    }
                }
                if (GameUi.KeyDown(PlayerAn2.KeyOf(CfgReleaseKey, KeyCode.Mouse0))) Release(plane);
                _errors = 0f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("An2Bombs: " + ex);
                if (++_errors >= 5f) { _errors = 0f; CloseSight(); }
            }
        }

        static void CloseSight()
        {
            _sight = false;
            _aimSet = false;
            if (_savedFov > 0f)
            {
                try
                {
                    Camera cam = CameraOwner.ViewCamera();
                    if (cam != null) cam.fieldOfView = _savedFov;
                }
                catch (Exception) { }
                _savedFov = -1f;
            }
        }

        // ------------------------------------------------------------ loading

        static float _nearCheck;
        static GameObject _near;

        static void OnFoot()
        {
            if (Time.time >= _nearCheck)
            {
                _nearCheck = Time.time + 0.5f;
                _near = PlayerAn2.NearestPlane();
                if (_near != null && !PlayerAn2.Parked(_near)) _near = null;
            }
            if (_near == null || An2Repair.Busy) return;
            if (!Input.GetKeyDown(PlayerAn2.KeyOf(CfgLoadKey, KeyCode.R))) return;
            int have = CountOf(_near);
            if (have >= Capacity) { Hint(Loc.T("Бомбодержатели заполнены", "The racks are full"), 3f); return; }
            int taken = 0;
            while (have + taken < Capacity && Turret.TakeItem(ItemId, "An2Bombs")) taken++;
            if (taken == 0)
            {
                Hint(Loc.T("Нет бомб ФАБ-50 в рюкзаке", "No FAB-50 bombs in the backpack"), 3f);
                return;
            }
            SetCount(_near, have + taken, true);
            RevivalPlugin.L.LogInfo("An2Bombs: " + taken + " bomb(s) loaded onto An-2 "
                + PlayerAn2.View(_near) + ", " + (have + taken) + " aboard.");
            Hint(Loc.T("Бомбы подвешены", "Bombs loaded") + ": " + (have + taken) + "/" + Capacity, 3f);
        }

        // --------------------------------------------------------------- sight

        /// <summary>Where the bombs leave the aeroplane: under the lower wing
        /// roots, a little above the wheels' reference point.</summary>
        static Vector3 RackOf(Transform tr)
        {
            return tr.position + tr.rotation * (new Vector3(0f, 0.9f, 0.5f) * K);
        }

        /// <summary>One integration step of a falling bomb: gravity, drag
        /// against the velocity, position in world units (m x K).</summary>
        static void Advance(ref Vector3 pos, ref Vector3 vel, float dt, float drag)
        {
            vel.y -= G * dt;
            vel -= vel * (vel.magnitude * drag * dt);
            pos += vel * (K * dt);
        }

        /// <summary>The same equations as the bomb, run ahead: where a bomb
        /// released now bursts, and after how long. Terrain only (the bomb
        /// itself also stops on objects).</summary>
        internal static bool Predict(Vector3 from, Vector3 vel, out Vector3 impact, out float seconds)
        {
            impact = from;
            seconds = 0f;
            float drag = Mathf.Max(0f, F(CfgDrag, 0.0006f));
            Vector3 pos = from;
            const float dt = 0.04f;
            for (int i = 0; i < 1500; i++)
            {
                Vector3 was = pos;
                Advance(ref pos, ref vel, dt, drag);
                seconds += dt;
                float y;
                if (!RevivalTroopInsertion.TerrainHeight(pos, out y)) return false;
                if (pos.y <= y)
                {
                    float above = was.y - y;
                    float drop = was.y - pos.y;
                    float t = drop > 0.0001f ? Mathf.Clamp01(above / drop) : 1f;
                    impact = Vector3.Lerp(was, pos, t);
                    impact.y = y;
                    seconds -= dt * (1f - t);
                    return true;
                }
            }
            return false;
        }

        static void Solve(GameObject plane)
        {
            Transform tr = plane.transform;
            Vector3 vel = PlayerAn2.Velocity;
            Vector3 rack = RackOf(tr);
            _solved = Predict(rack, vel, out _impact, out _fall);
            float floor;
            _agl = RevivalTroopInsertion.TerrainHeight(tr.position, out floor) ? Mathf.Max(0f, (tr.position.y - floor) / K) : 0f;
            _nadir = new Vector3(tr.position.x, floor, tr.position.z);
            Vector3 flat = new Vector3(vel.x, 0f, vel.z);
            _gs = flat.magnitude;
            Vector3 lead = _impact - _nadir;
            lead.y = 0f;
            _lead = lead.magnitude / K;
            Vector3 nose = tr.forward;
            nose.y = 0f;
            _drift = (_gs > 1f && nose.sqrMagnitude > 0.0001f)
                ? Vector3.Angle(nose, flat) * Mathf.Sign(Vector3.Cross(nose, flat).y) : 0f;
        }

        /// <summary>Where the sight looks from: UNDER the belly, ahead of the
        /// main gear. It used to be half a metre above the wheels' reference
        /// point, which is inside the fuselage - the cabin floor and the
        /// pilot's own body filled the picture (field report 2026-09-28).
        /// Never under the ground on a low pass.</summary>
        static Vector3 SightEye(Transform tr)
        {
            Vector3 eye = tr.position + tr.rotation * (new Vector3(0f, -1.2f, 2.5f) * K);
            float ground;
            if (RevivalTroopInsertion.TerrainHeight(eye, out ground) && eye.y < ground + 0.8f * K)
                eye.y = ground + 0.8f * K;
            return eye;
        }

        static Vector3 Track(Transform tr)
        {
            Vector3 track = PlayerAn2.Velocity;
            track.y = 0f;
            if (track.sqrMagnitude < 1f) { track = tr.forward; track.y = 0f; }
            if (track.sqrMagnitude < 0.0001f) track = Vector3.forward;
            return track.normalized;
        }

        /// <summary>
        /// The crosshair on the ground. Laid on the predicted burst when the
        /// sight opens (a little ahead of it, so there is time to correct);
        /// the mouse turns the line of sight from the eye and the crosshair is
        /// where that line meets the ground again. It stays on the ground
        /// while the aeroplane flies - a target under it stays under it. Once
        /// it has fallen behind the aeroplane it is laid ahead again.
        /// </summary>
        static void Aim(Transform tr)
        {
            if (!SightOn) { _aimSet = false; return; }
            Vector3 eye = SightEye(tr);
            Vector3 track = Track(tr);
            Vector3 nadir = new Vector3(tr.position.x, _nadir.y, tr.position.z);
            if (_aimSet && Vector3.Dot(_aim - nadir, track) < 0f) _aimSet = false;
            if (!_aimSet)
            {
                if (!_solved) return;
                _aim = _impact + track * (Mathf.Max(40f, _lead * 0.5f) * K);
                float gy;
                if (RevivalTroopInsertion.TerrainHeight(_aim, out gy)) _aim.y = gy;
                _aimSet = true;
            }
            float mx = GameUi.Axis("Mouse X"), my = GameUi.Axis("Mouse Y");
            if (Mathf.Abs(mx) < 0.0001f && Mathf.Abs(my) < 0.0001f) return;
            float sens = Mathf.Clamp(F(CfgAimSensitivity, 0.5f), 0.02f, 5f);
            Vector3 dir = _aim - eye;
            if (dir.sqrMagnitude < 0.01f) return;
            dir = Quaternion.AngleAxis(mx * sens, Vector3.up) * dir;
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            if (side.sqrMagnitude > 0.0001f)
                dir = Quaternion.AngleAxis(-my * sens, side.normalized) * dir;
            dir.Normalize();
            // Between 8 degrees under the horizon and straight down.
            float below = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (below < 8f)
            {
                Vector3 flat = new Vector3(dir.x, 0f, dir.z).normalized;
                dir = flat * Mathf.Cos(8f * Mathf.Deg2Rad) + Vector3.down * Mathf.Sin(8f * Mathf.Deg2Rad);
            }
            // Meet the ground: the plane at the crosshair's height, then the
            // terrain there.
            float drop = eye.y - _aim.y;
            if (drop < 1f) drop = 1f;
            Vector3 at = eye + dir * (drop / Mathf.Max(0.05f, -dir.y));
            Vector3 off = at - nadir;
            off.y = 0f;
            float reach = 1500f * K;
            if (off.magnitude > reach) at = nadir + off.normalized * reach;
            if (Vector3.Dot(at - nadir, track) < 5f * K) return;   // not behind the aeroplane
            float y;
            if (RevivalTroopInsertion.TerrainHeight(at, out y)) at.y = y;
            _aim = at;
        }

        /// <summary>
        /// PlayerAn2.Fly asks here while the pilot's hands are off A/D: the
        /// bank that turns the ground track onto the crosshair. The burst
        /// point lies on the track ahead of the nadir, so a track through the
        /// crosshair puts the bombs on it. For the run-in - the crosshair
        /// within the lead plus a margin - the wings are held level: bank at
        /// release widens the scatter (ScatterPerBank). The same law is flown
        /// in research/an2_assist_sim.py (steer_for).
        /// </summary>
        internal static bool Steer(out float bank)
        {
            bank = 0f;
            if (!_sight || !_aimSet || !SightOn) return false;
            GameObject plane = PlayerAn2.Plane;
            if (plane == null || PlayerAn2.OnGround) return false;
            Transform tr = plane.transform;
            Vector3 track = Track(tr);
            Vector3 to = _aim - tr.position;
            to.y = 0f;
            float dist = to.magnitude / K;
            if (dist < _lead * 1.1f + 30f) return true;         // run-in: level
            float err = Vector3.Angle(track, to) * Mathf.Sign(Vector3.Cross(track, to).y);
            bank = Mathf.Clamp(err * 1.5f, -25f, 25f);
            return true;
        }

        /// <summary>PlayerAn2.LateTick while the sight is open: the camera
        /// under the belly, laid on the crosshair (on the predicted burst
        /// until there is one), the ground track up the screen so a target
        /// slides straight down the track line. No smoothing: the crosshair is
        /// on the ground and the eye follows the aeroplane exactly, so the
        /// picture is as steady as the flight.</summary>
        internal static void SightCamera(Camera cam, Transform tr)
        {
            if (cam == null || tr == null) return;
            if (_savedFov < 0f) _savedFov = cam.fieldOfView;
            cam.fieldOfView = Mathf.Clamp(F(CfgSightFov, 40f), 10f, 90f);
            Vector3 eye = SightEye(tr);
            Vector3 track = Track(tr);
            Vector3 look = _aimSet ? (_aim - eye) : _solved ? (_impact - eye) : (track * 2f + Vector3.down);
            if (look.sqrMagnitude < 0.0001f) look = Vector3.down;
            look.Normalize();
            // Keep 'up' off the look direction: at a vertical look the track
            // is perpendicular anyway; ahead it is bent up by LookRotation.
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(look, track);
        }

        // -------------------------------------------------------------- release

        static void Release(GameObject plane)
        {
            if (Time.time < _nextRelease) return;
            _nextRelease = Time.time + Mathf.Max(0.1f, F(CfgInterval, 0.4f));
            int have = CountOf(plane);
            if (have <= 0)
            {
                Hint(Loc.T("Бомб нет - подвесьте ФАБ-50 на земле", "No bombs aboard - load FAB-50s on the ground"), 3f);
                return;
            }
            if (PlayerAn2.OnGround)
            {
                Hint(Loc.T("На земле бомбы не сбрасывают", "Not on the ground"), 2f);
                return;
            }
            Solve(plane);
            Transform tr = plane.transform;
            Vector3 start = RackOf(tr);
            Vector3 vel = PlayerAn2.Velocity;

            // The scatter: a small random velocity that moves the burst by
            // about sigma metres over the fall. Height and bank make it grow.
            Vector3 dummy;
            float fall;
            if (!Predict(start, vel, out dummy, out fall)) fall = 5f;
            float bank = Mathf.Abs(Vector3.Angle(tr.right, Vector3.ProjectOnPlane(tr.right, Vector3.up)));
            float sigma = Mathf.Max(0f, F(CfgScatter, 1f)) + Mathf.Max(0f, F(CfgScatterPerHeight, 0.015f)) * _agl
                + Mathf.Max(0f, F(CfgScatterPerBank, 0.12f)) * bank;
            float r = sigma * Gauss();
            float a = UnityEngine.Random.value * Mathf.PI * 2f;
            vel += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (r / Mathf.Max(0.5f, fall));

            Bomb b = new Bomb();
            b.Id = UnityEngine.Random.Range(1, 8000000);
            b.Pos = start;
            b.Vel = vel;
            b.Mine = true;
            b.Go = Visual(start, vel);
            _bombs.Add(b);
            SetCount(plane, have - 1, true);
            Net.Send(new float[] { 0f, b.Id, start.x, start.y, start.z, vel.x, vel.y, vel.z }, true);
            try { Mortar.FactionShield.Arm(); } catch (Exception) { }
            RevivalPlugin.L.LogInfo("An2Bombs: bomb " + b.Id + " away at " + start.ToString("0")
                + ", " + Mathf.RoundToInt(_agl) + " m over the ground, " + Mathf.RoundToInt(_gs * 3.6f)
                + " km/h, bank " + bank.ToString("0") + ", sigma " + sigma.ToString("0.0") + " m; "
                + (have - 1) + " left.");
            Hint(Loc.T("Бомба сброшена", "Bomb away") + " - " + (have - 1) + "/" + Capacity, 2f);
        }

        static float Gauss()
        {
            float u1 = Mathf.Max(0.0001f, UnityEngine.Random.value);
            float u2 = UnityEngine.Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        // ------------------------------------------------------------- flight

        static void Fly(float frame)
        {
            if (_bombs.Count == 0) return;
            float drag = Mathf.Max(0f, F(CfgDrag, 0.0006f));
            float arm = Mathf.Max(0f, F(CfgArmSeconds, 1f));
            frame = Mathf.Min(frame, 0.1f);
            for (int i = _bombs.Count - 1; i >= 0; i--)
            {
                Bomb b = _bombs[i];
                Vector3 segStart = b.Pos;
                b.Carry += frame;
                bool down = false;
                Vector3 at = b.Pos;
                while (b.Carry >= Step && !down)
                {
                    b.Carry -= Step;
                    Vector3 was = b.Pos;
                    Advance(ref b.Pos, ref b.Vel, Step, drag);
                    b.Age += Step;
                    float y;
                    if (RevivalTroopInsertion.TerrainHeight(b.Pos, out y) && b.Pos.y <= y)
                    {
                        // Where in the step it crossed the ground, as Predict does.
                        float drop = was.y - b.Pos.y;
                        float t = drop > 0.0001f ? Mathf.Clamp01((was.y - y) / drop) : 1f;
                        at = Vector3.Lerp(was, b.Pos, t);
                        at.y = y;
                        down = true;
                    }
                    else if (b.Age > 60f)
                    {
                        at = b.Pos;
                        down = true;
                    }
                    if (!down && b.Mine && b.Age > 0.25f && Obstacle(was, b.Pos, out at)) down = true;
                }
                if (!down && b.Mine && b.Age > 0.25f && Obstacle(segStart, b.Pos, out at)) down = true;

                if (!b.Whistled && b.Vel.y < -5f)
                {
                    float y;
                    if (RevivalTroopInsertion.TerrainHeight(b.Pos, out y)
                        && (b.Pos.y - y) / K < -b.Vel.y * 2.2f)
                    {
                        b.Whistled = true;
                        try { Mortar.Sound.Whistle(b.Pos); } catch (Exception) { }
                    }
                }
                if (b.Go != null)
                {
                    b.Go.transform.position = b.Pos;
                    if (b.Vel.sqrMagnitude > 0.01f) b.Go.transform.rotation = Quaternion.LookRotation(b.Vel.normalized);
                }
                if (!down) continue;
                _bombs.RemoveAt(i);
                if (b.Go != null) UnityEngine.Object.Destroy(b.Go);
                if (b.Mine) Burst(b.Id, at, b.Age < arm);
            }
        }

        /// <summary>The bomb's own path against the world's colliders: a roof,
        /// a vehicle, a tank. Never the An-2 that dropped it or our own man.</summary>
        static bool Obstacle(Vector3 from, Vector3 to, out Vector3 point)
        {
            point = to;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.001f) return false;
            GameObject hit = Turret.RaycastObject(from, d / len, len, out point);
            if (hit == null) return false;
            if (hit.GetComponentInParent<An2Visual>() != null) return false;
            GameObject me = MapTools.LocalPlayer();
            if (me != null && hit.transform.IsChildOf(me.transform.root)) return false;
            return true;
        }

        /// <summary>The dropper's burst: the picture, the sweep, the master.</summary>
        static void Burst(int id, Vector3 point, bool dud)
        {
            Net.Send(new float[] { 1f, id, point.x, point.y, point.z, dud ? 1f : 0f }, true);
            if (dud)
            {
                Hint(Loc.T("Слишком низко - взрыватель не взвёлся", "Too low - the fuze did not arm"), 3f);
                RevivalPlugin.L.LogInfo("An2Bombs: bomb " + id + " was a dud at " + point.ToString("0") + ".");
                return;
            }
            float radius = Mathf.Max(1f, F(CfgRadius, 12f)) * K;
            try { RocketHook.Detonate(point + Vector3.up * 0.2f, 0f, radius, 3f); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("An2Bombs: no visible explosion at " + point.ToString("0") + " - " + ex.Message);
            }
            int npc, veh, plr;
            Mortar.Sweep(point, true, radius, Mathf.Max(0f, F(CfgNpcDamage, 500f)),
                Mathf.Max(0f, F(CfgVehicleDamage, 1200f)), Mathf.Max(0f, F(CfgPlayerDamage, 300f)),
                out npc, out veh, out plr);
            Depot(point);
            RevivalPlugin.L.LogInfo("An2Bombs: bomb " + id + " burst at " + point.ToString("0")
                + " - " + npc + " NPC, " + veh + " vehicle, " + plr + " player hit.");
        }

        /// <summary>The master's side of somebody else's bomb: its own NPCs
        /// and every vehicle, as the mortar's SendImpact does.</summary>
        static void RemoteBurst(Vector3 point)
        {
            float radius = Mathf.Max(1f, F(CfgRadius, 12f)) * K;
            int npc, veh, plr;
            Mortar.Sweep(point, false, radius, Mathf.Max(0f, F(CfgNpcDamage, 500f)),
                Mathf.Max(0f, F(CfgVehicleDamage, 1200f)), 0f, out npc, out veh, out plr);
            Depot(point);
            RevivalPlugin.L.LogInfo("An2Bombs: a player's bomb at " + point.ToString("0")
                + " - master sweep " + npc + " NPC, " + veh + " vehicle.");
        }

        static void Depot(Vector3 point)
        {
            // The visual ExplosionObject has zero damage. Apply the vehicle
            // profile once on the master (local Burst or RemoteBurst).
            if (FuelDepot.Active)
            {
                if (RevivalTroopInsertion.MasterClient())
                    FuelDepot.Blast(point, Mathf.Max(0f, F(CfgVehicleDamage, 1200f)),
                        Mathf.Max(1f, F(CfgRadius, 12f)) * K);
                return;
            }
            if (CfgDepotHit != null && !CfgDepotHit.Value) return;
            if (!An2Repair.DepotHit(point, Mathf.Max(1f, F(CfgDepotReach, 25f)) * K)) return;
            Vector3 at = An2Repair.Depot;
            float y;
            if (RevivalTroopInsertion.GroundY(at, out y)) at.y = y;
            float radius = Mathf.Max(1f, F(CfgRadius, 12f)) * K * 1.6f;
            try
            {
                RocketHook.Detonate(at + Vector3.up * (1.5f * K), 0f, radius, 4f);
                RocketHook.Detonate(at + new Vector3(3f * K, 0.8f * K, -2f * K), 0f, radius, 4f);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("An2Bombs: depot blast: " + ex.Message); }
        }

        // -------------------------------------------------------------- visual

        static Mesh _mesh;
        static Material _mat;
        static bool _modelTried;
        static Quaternion _meshTurn = Quaternion.identity;
        static float _meshScale = 1f;
        static Vector3 _meshCentre;

        /// <summary>The falling FAB-50: the item mesh scaled to its real
        /// 1.07 m, nose along the flight path.</summary>
        static GameObject Visual(Vector3 at, Vector3 vel)
        {
            if (CfgModel != null && !CfgModel.Value) return null;
            if (!LoadModel()) return null;
            GameObject go = new GameObject("NDR_An2Bomb");
            go.transform.position = at;
            if (vel.sqrMagnitude > 0.01f) go.transform.rotation = Quaternion.LookRotation(vel.normalized);
            GameObject part = new GameObject("mesh");
            part.transform.SetParent(go.transform, false);
            part.transform.localRotation = _meshTurn;
            part.transform.localScale = Vector3.one * _meshScale;
            part.transform.localPosition = -(_meshTurn * (_meshCentre * _meshScale));
            part.AddComponent<MeshFilter>().sharedMesh = _mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = _mat;
            return go;
        }

        static bool LoadModel()
        {
            if (_modelTried) return _mesh != null;
            _modelTried = true;
            try
            {
                _mesh = Assets.Load("fab50.ndmesh");
                if (_mesh == null) return false;
                Texture2D tex = Assets.Texture("fab50_diffuse.png", false, true);
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _mat = new Material(shader);
                _mat.name = "NDR_An2Bomb";
                if (tex != null) _mat.mainTexture = tex;
                Bounds bb = _mesh.bounds;
                _meshCentre = bb.center;
                _meshScale = RealLength * K / Mathf.Max(0.01f, bb.size.x);
                // The long axis is X. The fin box is the fat end: compare the
                // radius of the vertices near either end to find the nose.
                Vector3[] v = _mesh.vertices;
                float lo = 0f, hi = 0f;
                for (int i = 0; i < v.Length; i++)
                {
                    float rr = new Vector2(v[i].y - bb.center.y, v[i].z - bb.center.z).magnitude;
                    if (v[i].x < bb.min.x + bb.size.x * 0.1f) lo = Mathf.Max(lo, rr);
                    if (v[i].x > bb.max.x - bb.size.x * 0.1f) hi = Mathf.Max(hi, rr);
                }
                // Nose at -X (fat end at +X): turn -X onto +Z; else +X onto +Z.
                _meshTurn = hi > lo ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.Euler(0f, -90f, 0f);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("An2Bombs: bomb model: " + ex.Message);
                _mesh = null;
                return false;
            }
        }

        // ------------------------------------------------------------ network

        static void Heartbeat()
        {
            if (Time.time < _nextHeartbeat) return;
            _nextHeartbeat = Time.time + 5f;
            if (!RevivalTroopInsertion.MasterClient() || _count.Count == 0) return;
            List<int> gone = null;
            foreach (KeyValuePair<int, int> kv in _count)
            {
                if (PlayerAn2.ByViewId(kv.Key) == null)
                {
                    if (gone == null) gone = new List<int>();
                    gone.Add(kv.Key);
                    continue;
                }
                Net.Send(new float[] { 2f, kv.Key, kv.Value }, false);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) _count.Remove(gone[i]);
        }

        internal static class Net
        {
            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optType;

            static int Code() { return Mathf.Clamp(CfgEventCode == null ? 158 : CfgEventCode.Value, 0, 199); }

            internal static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    int code = Code();
                    int an2 = PlayerAn2.CfgEventCode == null ? 150 : PlayerAn2.CfgEventCode.Value;
                    if (code >= an2 && code <= an2 + 7)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("An2Bombs net: event code " + code
                            + " lies in the An-2's band " + an2 + "-" + (an2 + 7) + " - no bombs over the network.");
                        return;
                    }
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    FieldInfo onEvent = photon == null ? null : AccessTools.Field(photon, "OnEventCall");
                    _raise = photon == null ? null : AccessTools.Method(photon, "RaiseEvent", null, null);
                    _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (onEvent == null || _raise == null)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("An2Bombs net: RaiseEvent or OnEventCall missing.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("An2Bombs net hooked: event code " + code + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("An2Bombs net not hooked: " + ex);
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
                catch (Exception ex) { RevivalPlugin.L.LogWarning("An2Bombs net send: " + ex.Message); }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                if (code != (byte)Code()) return;
                try
                {
                    float[] f = content as float[];
                    if (f == null || f.Length < 1 || !Enabled) return;
                    int kind = Mathf.RoundToInt(f[0]);
                    if (kind == 0 && f.Length >= 8)
                    {
                        Bomb b = new Bomb();
                        b.Id = Mathf.RoundToInt(f[1]);
                        b.Pos = new Vector3(f[2], f[3], f[4]);
                        b.Vel = new Vector3(f[5], f[6], f[7]);
                        b.Mine = false;
                        b.Go = Visual(b.Pos, b.Vel);
                        _bombs.Add(b);
                        return;
                    }
                    if (kind == 1 && f.Length >= 6)
                    {
                        int id = Mathf.RoundToInt(f[1]);
                        for (int i = _bombs.Count - 1; i >= 0; i--)
                        {
                            if (_bombs[i].Mine || _bombs[i].Id != id) continue;
                            if (_bombs[i].Go != null) UnityEngine.Object.Destroy(_bombs[i].Go);
                            _bombs.RemoveAt(i);
                        }
                        if (f[5] < 0.5f && RevivalTroopInsertion.MasterClient())
                            RemoteBurst(new Vector3(f[2], f[3], f[4]));
                        return;
                    }
                    if (kind == 2 && f.Length >= 3)
                    {
                        int view = Mathf.RoundToInt(f[1]);
                        if (view != 0) _count[view] = Mathf.Clamp(Mathf.RoundToInt(f[2]), 0, Capacity);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("An2Bombs net receive: " + ex.Message);
                }
            }
        }

        // ================================================================ draw

        static void Hint(string text, float seconds)
        {
            _hint = text;
            _hintUntil = Time.time + seconds;
        }

        internal static void Draw()
        {
            if (!Enabled) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            bool hint = !string.IsNullOrEmpty(_hint) && Time.time < _hintUntil;
            GameObject plane = PlayerAn2.Plane;

            if (plane != null && PlayerAn2.Flying)
            {
                if (_sight) Sight(cx, cy);
                else
                    Label(Loc.T("Бомбы", "Bombs") + " " + CountOf(plane) + "/" + Capacity + "   "
                          + KeyName(SightKey) + " " + Loc.T("прицел", "sight") + ", "
                          + KeyName(PlayerAn2.KeyOf(CfgReleaseKey, KeyCode.Mouse0)) + " " + Loc.T("сброс", "release"),
                          cx, cy + 246f, new Color(0.85f, 0.82f, 0.62f, 1f), 13);
            }
            else if (plane == null && _near != null && !PlayerAn2.Aboard)
            {
                int n = CountOf(_near);
                Label("[" + PlayerAn2.KeyOf(CfgLoadKey, KeyCode.R) + "] "
                      + Loc.T("Подвесить бомбы ФАБ-50", "Load FAB-50 bombs") + " (" + n + "/" + Capacity + ")",
                      cx, cy + 84f, new Color(0.95f, 0.90f, 0.70f, 1f), 14);
            }
            if (hint) Label(_hint, cx, cy + 60f, new Color(1f, 0.92f, 0.70f, 1f), 16);
        }

        /// <summary>The bombsight: reticle, pipper, track and heading lines,
        /// nadir mark, readouts. Drawn over the sight camera, or over the
        /// normal view when [Graphics] An2Bombsight is off.</summary>
        static void Sight(float cx, float cy)
        {
            Camera cam = CameraOwner.ViewCamera();
            Color green = new Color(0.55f, 1f, 0.55f, 0.9f);
            Color amber = new Color(1f, 0.75f, 0.25f, 0.95f);
            Transform tr = PlayerAn2.Plane.transform;

            // The fixed reticle: the crosshair the pilot lays with the mouse
            // (the camera looks at it). The amber pipper below is the burst.
            if (SightOn)
            {
                Ring(cx, cy, 34f, green);
                Bar(cx - 90f, cy - 0.5f, 56f, 1.5f, green);
                Bar(cx + 34f, cy - 0.5f, 56f, 1.5f, green);
                Bar(cx - 0.5f, cy + 34f, 1.5f, 60f, green);
                for (int i = 1; i <= 3; i++) Bar(cx - 6f, cy - 34f - i * 18f, 12f, 1.2f, green);
            }
            if (cam != null)
            {
                // Track line: from the nadir forward along the ground track.
                Vector3 track = PlayerAn2.Velocity;
                track.y = 0f;
                if (track.sqrMagnitude > 1f)
                {
                    Vector3 far = _nadir + track.normalized * (Mathf.Max(60f, _lead * 2.5f) * K);
                    Segment(cam, _nadir, far, green, 1.5f);
                }
                // Heading tick: the nose over the ground, short.
                Vector3 nose = tr.forward;
                nose.y = 0f;
                if (nose.sqrMagnitude > 0.0001f && _solved)
                    Segment(cam, _impact, _impact + nose.normalized * (15f * K), amber, 1.2f);
                Vector2 s;
                if (ToGui(cam, _nadir, out s))
                {
                    Ring(s.x, s.y, 8f, amber);
                    Label(Loc.T("надир", "nadir"), s.x, s.y + 8f, amber, 11);
                }
                if (_solved && ToGui(cam, _impact, out s))
                {
                    Bar(s.x - 7f, s.y - 1f, 14f, 2f, amber);
                    Bar(s.x - 1f, s.y - 7f, 2f, 14f, amber);
                    Ring(s.x, s.y, 12f, amber);
                }
                // The pipper reaching the crosshair: the moment to release.
                if (SightOn && _solved && _aimSet)
                {
                    Vector3 along = PlayerAn2.Velocity;
                    along.y = 0f;
                    Vector3 miss = _aim - _impact;
                    miss.y = 0f;
                    float ahead = along.sqrMagnitude > 1f ? Vector3.Dot(miss, along.normalized) / K : 0f;
                    float wide = along.sqrMagnitude > 1f
                        ? Mathf.Abs(Vector3.Cross(along.normalized, miss).y) / K : miss.magnitude / K;
                    float r = Mathf.Max(4f, F(CfgRadius, 25f) * 0.35f);
                    bool now = ahead < r && ahead > -r && wide < r;
                    string cue = now ? Loc.T("СБРОС!", "DROP!")
                        : ahead > 0f && _gs > 1f ? Loc.T("до сброса", "to release") + " "
                          + (ahead / _gs).ToString("0.0") + " s" : "";
                    if (cue.Length > 0 && (!now || Mathf.Repeat(Time.time, 0.4f) < 0.28f))
                        Label(cue, cx, cy + 44f, now ? new Color(1f, 0.3f, 0.25f, 1f) : green, now ? 22 : 13);
                }
            }

            // Readouts.
            GameObject plane = PlayerAn2.Plane;
            float left = SightOn ? cx - 290f : cx - 200f;
            float top = SightOn ? cy - 170f : cy - 240f;
            string fall = _solved ? _fall.ToString("0.0") + " s" : "--";
            Text(Loc.T("ВЫС", "ALT") + " " + Mathf.RoundToInt(_agl) + " m", left, top, green);
            Text(Loc.T("ПС", "GS") + " " + Mathf.RoundToInt(_gs * 3.6f) + " km/h", left, top + 18f, green);
            Text(Loc.T("ПАД", "FALL") + " " + fall, left, top + 36f, green);
            Text(Loc.T("УПР", "LEAD") + " " + Mathf.RoundToInt(_lead) + " m", left, top + 54f, green);
            Text(Loc.T("СНОС", "DRIFT") + " " + _drift.ToString("0") + " deg", left, top + 72f, green);
            Text(Loc.T("БОМБ", "BOMBS") + " " + CountOf(plane) + "/" + Capacity, left, top + 90f,
                 CountOf(plane) > 0 ? amber : new Color(1f, 0.35f, 0.3f, 0.95f));
            Label(Loc.T("мышь - прицел", "mouse aims") + "   "
                  + KeyName(PlayerAn2.KeyOf(CfgReleaseKey, KeyCode.Mouse0)) + " " + Loc.T("сброс", "release") + "   "
                  + KeyName(SightKey) + " " + Loc.T("закрыть прицел", "close sight"),
                  cx, cy + 246f, new Color(0.80f, 0.85f, 0.90f, 1f), 13);
        }

        static bool ToGui(Camera cam, Vector3 world, out Vector2 gui)
        {
            Vector3 p = cam.WorldToScreenPoint(world);
            gui = new Vector2(p.x, UnityEngine.Screen.height - p.y);
            return p.z > 0f && gui.x > -50f && gui.x < UnityEngine.Screen.width + 50f
                && gui.y > -50f && gui.y < UnityEngine.Screen.height + 50f;
        }

        static void Segment(Camera cam, Vector3 a, Vector3 b, Color c, float width)
        {
            Vector3 pa = cam.WorldToScreenPoint(a);
            Vector3 pb = cam.WorldToScreenPoint(b);
            if (pa.z <= 0f || pb.z <= 0f) return;
            Vector2 ga = new Vector2(pa.x, UnityEngine.Screen.height - pa.y);
            Vector2 gb = new Vector2(pb.x, UnityEngine.Screen.height - pb.y);
            Vector2 d = gb - ga;
            float len = d.magnitude;
            if (len < 1f || len > 5000f) return;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 keep = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, ga);
            Bar(ga.x, ga.y - width * 0.5f, len, width, c);
            GUI.matrix = keep;
        }

        static void Ring(float x, float y, float r, Color c)
        {
            const int n = 28;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                Bar(x + Mathf.Cos(a) * r - 1f, y + Mathf.Sin(a) * r - 1f, 2f, 2f, c);
            }
        }

        static void Bar(float x, float y, float w, float h, Color c)
        {
            Color keep = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = keep;
        }

        static void Text(string text, float x, float y, Color c)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 13;
            style.normal.textColor = c;
            GUI.Label(new Rect(x, y, 200f, 20f), text, style);
        }

        static void Label(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUIContent content = new GUIContent(text);
            Vector2 measured = style.CalcSize(content);
            GUI.Label(new Rect(cx - measured.x * 0.5f, y, measured.x, measured.y), content, style);
        }
    }
}

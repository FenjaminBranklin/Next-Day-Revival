// Next Day: Survival - Revival Toolkit
//
// THE AN-2 AS A BOMBER. The player-flown An-2 (Revival.PlayerAn2.cs) carries
// aerial bombs (item 2072) on its racks. docs/ai/tasks/an2-bombs.md has the
// original numbers; w-bomb2-bombsight.md documents the CCIP controls, offline
// trajectory proof and current in-game checklist.
//
// THE BOMB LOAD (Y B4). [An2Bombs] BombLoad, also in the F2 settings window:
//   8x100  eight FAB-100 (800 kg) - the default, the realistic load;
//   12x50  twelve FAB-50 (600 kg) - more, smaller bursts.
// One bomb item fills one rack station. An empty rack takes the loader's
// chosen load; a part-full rack is topped up with what already hangs there,
// so one aeroplane never mixes sizes. The rack's bomb mass travels with its
// count (load message) and with every drop and burst, so the master sweeps
// the damage of the bomb that fell, whatever its own setting. BlastRadius and
// the three *Damage keys are a FAB-50's; a bomb of m kg scales them by
// (m / 50)^(1/3), the same law as OrdnanceBlast.RadiusForMass: a FAB-100 is
// x1.26 (radius 31.5 m, NPC 630, vehicle 1512, player 378). A message
// without the mass (an older client) counts as a FAB-50.
//
// WHAT A PLAYER DOES
//   1. Loading. On foot beside a parked An-2 with bombs in the backpack,
//      the load key (R) moves them onto the racks, up to the load's count. The
//      bombs come from the airfield loot (the "military" pool, D2a and the
//      other bunkers) or from the admin panel. An An-2 the admin spawns
//      "ready" comes with a full load.
//   2. CCIP is visible whenever the local pilot is airborne. G opens the
//      belly camera and marks the ground under the current view (unless a
//      map target was selected). G again closes it. Right-click the map or
//      right-click inside the sight to mark a new fixed target; Backspace
//      clears it. The amber ground ring is the predicted impact + dispersion;
//      the cyan line and cue bar guide the run to a marked target. No auto aim.
//   3. LMB drops one bomb; holding it drops a stick at ReleaseInterval. The
//      amber dotted line previews the remaining stick during steady flight.
//      Bombs retain the existing quadratic drag, scatter and network damage.
//
// HOW IT GOES OFF (the mortar's rules, reused, not copied)
//   The dropping client flies the bomb. At impact:
//     the picture  RocketHook.Detonate - the game's own networked grenade
//                  explosion, one blast and one bang on every client, with 0
//                  damage of its own (RevivalMortar.cs says why);
//     the damage   OrdnanceBlast: anonymous NPC owner RPCs and vehicle damage
//                  from master, victim player RPCs from shooter (attribution).
//                  Physical blasts include the dropper and his faction;
//     the master   a dropper who is not the master sends the burst on this
//                  file's event, and the master queues the one damage pass;
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
//   0 drop   id, x, y, z, vx, vy, vz, kg        dropper -> others (visual)
//   1 burst  id, x, y, z, dud, kg               dropper -> others (master sweeps)
//   2 load   view, count, kg                    whoever changed it -> others;
//                                               the master repeats every 5 s
// (kg: the bomb mass, Y B4; missing = 50 from an older client)
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
//   Revival.Admin.cs    "Spawn An-2 (ready)" and "An-2 bombs xN" (the load's count)
//   Revival.Settings.cs the F2 window's bomb load choice (LoadIndex / SetLoad)
//   Revival.Airfield.cs the loot pool (Pool)
//   RevivalMortar.cs    Sound; Revival.OrdnanceBlast.cs shared damage
//   Revival.An2Repair.cs DepotHit

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Y B4: what hangs on the An-2's racks - the two loads, their
    /// capacity, the size scaling and the wire value. Pure (no Unity), no
    /// allocation; research/an2_bomb_load_check.py compiles it offline.</summary>
    internal static class An2BombLoad
    {
        internal const int HeavyKg = 100, HeavyCount = 8;    // 8 x FAB-100, the default
        internal const int LightKg = 50, LightCount = 12;    // 12 x FAB-50
        internal const int MaxCount = 12;
        /// <summary>The config numbers (BlastRadius, *Damage) are a FAB-50's.</summary>
        internal const float BaseKg = 50f;
        internal const string Heavy = "8x100", Light = "12x50";

        /// <summary>The bomb mass a [An2Bombs] BombLoad text asks for: a
        /// number 50 or 12 in it ("12x50", "12 x 50", "FAB-50") is the light
        /// load, anything else (unknown text too) the default 8 x 100 kg.</summary>
        internal static int KgOf(string text)
        {
            if (text == null) return HeavyKg;
            int n = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? text[i] : ' ';
                if (c >= '0' && c <= '9')
                {
                    n = (n < 0 ? 0 : n) * 10 + (c - '0');
                    if (n > 100000) n = 100000;
                    continue;
                }
                if (n == LightKg || n == LightCount) return LightKg;
                n = -1;
            }
            return HeavyKg;
        }

        /// <summary>Only the two known sizes exist; anything else rounds to one.</summary>
        internal static int Known(float kg) { return kg >= 75f ? HeavyKg : LightKg; }

        /// <summary>Rack stations for bombs of this mass.</summary>
        internal static int CapacityFor(int kg) { return Known(kg) == LightKg ? LightCount : HeavyCount; }

        /// <summary>Radius and damage factor of a bomb against the FAB-50
        /// config numbers: (m / 50)^(1/3), OrdnanceBlast.RadiusForMass's law.</summary>
        internal static float Scale(int kg) { return (float)Math.Pow(Known(kg) / BaseKg, 1.0 / 3.0); }

        /// <summary>The mass in an optional message tail; missing = an older
        /// client, which only knew the FAB-50.</summary>
        internal static int KgAt(float[] f, int index)
        {
            return f != null && index >= 0 && index < f.Length ? Known(f[index]) : LightKg;
        }

        internal static string Name(int kg) { return Known(kg) == HeavyKg ? "FAB-100" : "FAB-50"; }
        internal static string NameRu(int kg) { return Known(kg) == HeavyKg ? "ФАБ-100" : "ФАБ-50"; }
    }

    /// <summary>Bombs for the player-flown An-2: racks, sight, release,
    /// ballistic fall, burst through the mortar's damage rules.</summary>
    internal static class An2Bombs
    {
        /// <summary>The aerial bomb, after the An-2 repair parts (2069-2071);
        /// hung as a FAB-100 or a FAB-50 (Y B4, BombLoad).</summary>
        internal const int ItemId = 2072;
        const int DONOR = 2030;              // the generic carryable, like 2069-2071
        const float G = 9.81f;
        const float Step = 0.02f;            // the bomb's integration step, s
        const float RealLength = 1.07f;      // FAB-50, metres nose to fin box (x Scale for a FAB-100)

        static float K { get { return PlayerAn2.K; } }

        // ============================================================= config

        internal static ConfigEntry<bool> CfgGameplay, CfgDepotHit, CfgSightView, CfgModel;
        internal static ConfigEntry<string> CfgSightKey, CfgReleaseKey, CfgLoadKey, CfgLoad;
        internal static ConfigEntry<int> CfgEventCode, CfgControls;
        internal static ConfigEntry<float> CfgRadius, CfgNpcDamage, CfgVehicleDamage, CfgLethalCore,
            CfgPlayerDamage, CfgDrag, CfgScatter, CfgScatterPerHeight, CfgScatterPerBank,
            CfgArmSeconds, CfgInterval, CfgSightFov, CfgDepotReach, CfgAimSensitivity;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgGameplay = cfg.Bind("Gameplay", "An2Bombs", true,
                "The player-flown An-2 carries aerial bombs (item 2072), has a "
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
                "The falling bomb is drawn (the FAB model, sized to the bomb). Off: it falls unseen.");

            const string S = "An2Bombs";
            CfgLoad = cfg.Bind(S, "BombLoad", An2BombLoad.Heavy,
                "What an empty An-2's racks take when you load them (also in the F2 settings window): "
                + "8x100 = eight FAB-100 (default), 12x50 = twelve FAB-50. One bomb item per station. "
                + "Radius and damage below are a FAB-50's; a FAB-100 hits x1.26 (cube root of the mass). "
                + "Replaces the old BombCapacity key, which is ignored.");
            CfgSightKey = cfg.Bind(S, "SightKey", "G",
                "Pilot, in the air: mark the viewed ground and open or close CCIP. The same key as "
                + "[PlayerAn2] EngineKey is fine: on the ground it is the engine.");
            CfgReleaseKey = cfg.Bind(S, "ReleaseKey", "Mouse0",
                "Pilot: tap for one bomb, hold for a stick (Mouse0 = left mouse button).");
            CfgLoadKey = cfg.Bind(S, "LoadKey", "R",
                "On foot beside a parked An-2: move bombs from the backpack onto its racks.");
            CfgRadius = cfg.Bind(S, "BlastRadius", 25f,
                "Metres, for a FAB-50 (a FAB-100 x1.26). Damage falls off linearly to zero here. "
                + "A vehicle needs a burst within a few metres to be hurt badly.");
            CfgNpcDamage = cfg.Bind(S, "NpcDamage", 500f, "Damage to an NPC at the burst point of a FAB-50 (a FAB-100 x1.26).");
            CfgLethalCore = cfg.Bind(S, "LethalCore", 0.5f,
                "Part of BlastRadius (0-0.9) in which an NPC takes the full NpcDamage; beyond it the damage "
                + "falls off linearly to zero at the radius. Inside half of this core a burst kills every NPC "
                + "that can be hurt, bosses included. 0 = the old linear falloff from the burst point.");
            CfgVehicleDamage = cfg.Bind(S, "VehicleDamage", 1200f,
                "Damage to a vehicle at the burst point of a FAB-50, a FAB-100 x1.26 (explosion part, "
                + "so the vehicle armour rules apply).");
            CfgPlayerDamage = cfg.Bind(S, "PlayerDamage", 300f,
                "Body explosion damage to a player at the burst point of a FAB-50 (a FAB-100 x1.26), "
                + "including the dropper's faction.");
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
                "Legacy mouse-aim tuning, retained for config compatibility. "
                + "CCIP uses fixed map/G/RMB targets and manual flight controls.");
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

        static string _loadText;
        static int _loadKg = An2BombLoad.HeavyKg;

        /// <summary>The chosen load's bomb mass, parsed once per change of the text.</summary>
        internal static int LoadKg
        {
            get
            {
                string t = CfgLoad == null ? null : CfgLoad.Value;
                if (!ReferenceEquals(t, _loadText)) { _loadText = t; _loadKg = An2BombLoad.KgOf(t); }
                return _loadKg;
            }
        }

        /// <summary>Stations of the chosen load (8, or 12 for FAB-50s).</summary>
        internal static int Capacity { get { return An2BombLoad.CapacityFor(LoadKg); } }

        /// <summary>F2 settings window: 0 = 8 x FAB-100, 1 = 12 x FAB-50.</summary>
        internal static int LoadIndex { get { return LoadKg == An2BombLoad.LightKg ? 1 : 0; } }

        internal static void SetLoad(int index)
        {
            if (CfgLoad == null) return;
            CfgLoad.Value = index == 1 ? An2BombLoad.Light : An2BombLoad.Heavy;
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogInfo("An2Bombs: bomb load " + CfgLoad.Value + ".");
        }

        /// <summary>Damage / radius of a bomb of this mass from the FAB-50 numbers.</summary>
        static float RadiusU(int kg) { return Mathf.Max(1f, F(CfgRadius, 25f)) * K * An2BombLoad.Scale(kg); }
        static float Peak(ConfigEntry<float> e, float fallback, int kg) { return Mathf.Max(0f, F(e, fallback)) * An2BombLoad.Scale(kg); }
        static float LethalCore { get { return Mathf.Clamp(F(CfgLethalCore, 0.5f), 0f, 0.9f); } }
        static string Label(int kg) { return An2BombLoad.Known(kg) == An2BombLoad.HeavyKg ? "An-2 FAB-100" : "An-2 FAB-50"; }

        // ============================================================== items

        internal static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                ItemId, DONOR, false,
                "Авиабомба ФАБ", "Aerial bomb (FAB)",
                "Фугасная авиабомба. Подвешивается на Ан-2 у самолёта (клавиша "
                + "загрузки) как ФАБ-100 (8 шт.) или ФАБ-50 (12 шт.) - по выбранной "
                + "загрузке; сбрасывается пилотом через бомбовый прицел.",
                "A high-explosive aircraft bomb. Load it onto the An-2 at the aeroplane "
                + "(the load key) as a FAB-100 (8 racks) or a FAB-50 (12 racks), per the "
                + "chosen bomb load; the pilot drops it through the bombsight.",
                "fab50.ndmesh", "fab50_diffuse.png", "fab50_normal.png",
                "fab50_icon.png", null,
                1, 0, 10.0f));
        }

        /// <summary>The airfield's loot with the bombs in it (Airfield.Draw):
        /// the "military" pool of the bunkers rolls a bomb now and then.</summary>
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
            public int Kg;                   // Y B4: 100 or 50
            public bool Mine, Whistled;
            public GameObject Go;
        }

        static readonly List<Bomb> _bombs = new List<Bomb>();
        static readonly Dictionary<int, int> _count = new Dictionary<int, int>();   // view -> bombs aboard
        static readonly Dictionary<int, int> _rackKg = new Dictionary<int, int>();  // view -> their mass (Y B4)
        static bool _sight;
        static float _savedFov = -1f;
        static float _nextRelease, _nextHeartbeat, _errors;
        static string _hint = "";
        static float _hintUntil;

        // Optional user-selected target. Never replaced when passed or on closing.
        // The camera follows the impact; targets do not move the impact marker.
        static bool _aimSet;
        static Vector3 _aim;

        // Local-pilot solution, at most 10 Hz, including releases.
        static bool _solved, _mapAim;
        static Vector3 _impact, _sampleRack, _stickStep;
        static float _fall, _agl, _gs, _sigma, _nextSolve;
        static int _displayCount, _sightView;
        static GameObject _sightPlane;
        static Terrain[] _sightTerrains;
        static int _terrainScenes = -1;

        internal static bool SightOn { get { return _sight && CfgSightView != null && CfgSightView.Value; } }

        internal static int CountOf(GameObject go)
        {
            int n;
            int view = PlayerAn2.View(go);
            return view != 0 && _count.TryGetValue(view, out n) ? n : 0;
        }

        /// <summary>The mass of what hangs on this aeroplane; an empty rack
        /// takes the chosen load.</summary>
        internal static int RackKg(GameObject go) { return RackKgOf(PlayerAn2.View(go)); }

        static int RackKgOf(int view)
        {
            int n, kg;
            if (view != 0 && _count.TryGetValue(view, out n) && n > 0 && _rackKg.TryGetValue(view, out kg)) return kg;
            return LoadKg;
        }

        static void SetCount(GameObject go, int n, int kg, bool send)
        {
            int view = PlayerAn2.View(go);
            if (view == 0) return;
            kg = An2BombLoad.Known(kg);
            n = Mathf.Clamp(n, 0, An2BombLoad.CapacityFor(kg));
            _count[view] = n;
            _rackKg[view] = kg;
            if (send) Net.Send(new float[] { 2f, view, n, kg }, true);
        }

        /// <summary>From PlayerAn2.Build for a "ready" An-2 (the admin panel):
        /// the racks full.</summary>
        internal static void FullLoad(GameObject go)
        {
            if (!Enabled || go == null) return;
            Net.EnsureHooked();
            SetCount(go, Capacity, LoadKg, true);
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
                    _solved = _aimSet = _mapAim = false;
                    _sightPlane = null;
                    if (!PlayerAn2.Aboard && !PlayerHeli.Aboard) OnFoot();
                    return;
                }
                if (!PlayerAn2.Flying || PlayerAn2.Burning(plane))
                {
                    if (_sight) CloseSight();
                    _solved = _aimSet = _mapAim = false;
                    _sightPlane = null;
                    return;
                }
                if (!ReferenceEquals(_sightPlane, plane))
                {
                    CloseSight();
                    _aimSet = _mapAim = false;
                    _sightPlane = plane;
                    _sightView = PlayerAn2.View(plane);
                    _nextSolve = 0f;
                }
                if (PlayerAn2.OnGround)
                {
                    if (_sight) CloseSight();
                    _solved = _aimSet = _mapAim = false;
                    return;
                }
                Solve(plane);
                if (Input.GetMouseButtonDown(1))
                {
                    Vector3 point;
                    if (GameUi.WindowOpen && MapTools.MouseWorld(out point))
                    {
                        _aim = point;
                        _aimSet = _mapAim = true;
                    }
                    else if (_sight && !GameUi.WindowOpen)
                    {
                        Camera cam = CameraOwner.ViewCamera();
                        if (cam != null) Mark(cam.ScreenPointToRay(Input.mousePosition));
                    }
                }
                if (GameUi.KeyDown(KeyCode.Backspace)) _aimSet = _mapAim = false;
                if (GameUi.KeyDown(SightKey))
                {
                    if (_sight) CloseSight();
                    else
                    {
                        if (!_mapAim)
                        {
                            Camera cam = CameraOwner.ViewCamera();
                            if (cam != null) Mark(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)));
                        }
                        _sight = true;
                        Hint(Loc.T("ЛКМ: сброс / удерживать: серия. ПКМ: цель. G: закрыть. Backspace: убрать цель.",
                                   "LMB: drop / hold: stick. RMB: target. G: close. Backspace: clear target."), 5f);
                    }
                }
                KeyCode releaseKey = PlayerAn2.KeyOf(CfgReleaseKey, KeyCode.Mouse0);
                if (!GameUi.WindowOpen && Input.GetKey(releaseKey)) Release(plane);
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
            int kg = RackKg(_near);           // a part-full rack keeps its size
            int cap = An2BombLoad.CapacityFor(kg);
            if (have >= cap) { Hint(Loc.T("Бомбодержатели заполнены", "The racks are full"), 3f); return; }
            int taken = 0;
            while (have + taken < cap && Turret.TakeItem(ItemId, "An2Bombs")) taken++;
            if (taken == 0)
            {
                Hint(Loc.T("Нет авиабомб в рюкзаке", "No aerial bombs in the backpack"), 3f);
                return;
            }
            SetCount(_near, have + taken, kg, true);
            RevivalPlugin.L.LogInfo("An2Bombs: " + taken + " bomb(s) loaded onto An-2 "
                + PlayerAn2.View(_near) + ", " + (have + taken) + " x " + kg + " kg aboard.");
            Hint(Loc.T("Подвешены " + An2BombLoad.NameRu(kg), An2BombLoad.Name(kg) + " loaded")
                + ": " + (have + taken) + "/" + cap, 3f);
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

        // Terrain.activeTerrains allocates an array. Refresh only on scene changes
        // or the first flight, not per solve. Native SampleHeight avoids reflection
        // boxing/argument arrays, and preserves Unity's terrain indexing.
        static bool Ground(Vector3 at, out float y)
        {
            y = 0f;
            int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (_sightTerrains == null || scenes != _terrainScenes)
            {
                _terrainScenes = scenes;
                _sightTerrains = Terrain.activeTerrains;
            }
            Terrain active = Terrain.activeTerrain;
            for (int i = -1; i < _sightTerrains.Length; i++)
            {
                Terrain t = i < 0 ? active : _sightTerrains[i];
                if (t == null || t.terrainData == null || (i >= 0 && !t.drawHeightmap)) continue;
                Vector3 origin = t.GetPosition(), size = t.terrainData.size;
                if (at.x < origin.x || at.z < origin.z || at.x > origin.x + size.x || at.z > origin.z + size.z) continue;
                y = origin.y + t.SampleHeight(at);
                return true;
            }
            return false;
        }

        static bool OnPlane(Vector3 from, Vector3 vel, float floor, out Vector3 impact, out float seconds)
        {
            double x, z, time;
            bool ok = An2BombCurve.Fall((from.y - floor) / K, vel.x, vel.y, vel.z,
                Mathf.Max(0f, F(CfgDrag, 0.0006f)), out x, out z, out time);
            impact = new Vector3(from.x + (float)x * K, floor, from.z + (float)z * K);
            seconds = (float)time;
            return ok;
        }

        /// <summary>Analytic fall panels + bounded terrain-plane refinement.
        /// No raycasts; Solve owns the one ground ray per 10 Hz sample.</summary>
        internal static bool Predict(Vector3 from, Vector3 vel, out Vector3 impact, out float seconds)
        {
            impact = from;
            seconds = 0f;
            float floor;
            if (!Ground(from, out floor)) return false;
            // A secant correction converges on low uphill runs where simple
            // floor replacement oscillates. A cliff without convergence hides CCIP.
            float previousFloor = floor, previousError = 0f;
            for (int i = 0; i < 8; i++)
            {
                if (!OnPlane(from, vel, floor, out impact, out seconds))
                {
                    if (i == 0) return false;
                    // The trial floor can exceed the bomb's climb apex on an
                    // uphill run. Retreat toward the last reachable plane.
                    floor = (floor + previousFloor) * 0.5f;
                    continue;
                }
                float next;
                if (!Ground(impact, out next)) return false;
                if (Mathf.Abs(next - floor) < 0.25f * K) { impact.y = next; return true; }
                float error = next - floor;
                float corrected = next;
                if (i > 0 && Mathf.Abs(error - previousError) > 0.0001f)
                    corrected = floor - error * (floor - previousFloor) / (error - previousError);
                previousFloor = floor;
                previousError = error;
                floor = corrected;
            }
            return false;
        }

        static float Dispersion(Transform tr, float altitude)
        {
            float bank = Mathf.Abs(Vector3.Angle(tr.right, Vector3.ProjectOnPlane(tr.right, Vector3.up)));
            return Mathf.Max(0f, F(CfgScatter, 1f))
                + Mathf.Max(0f, F(CfgScatterPerHeight, 0.015f)) * altitude
                + Mathf.Max(0f, F(CfgScatterPerBank, 0.12f)) * bank;
        }

        static void Solve(GameObject plane)
        {
            if (Time.time < _nextSolve) return;
            _nextSolve = Time.time + 0.1f;
            Transform tr = plane.transform;
            Vector3 vel = PlayerAn2.Velocity;
            _sampleRack = RackOf(tr);
            _solved = Predict(_sampleRack, vel, out _impact, out _fall);
            float floor;
            _agl = Ground(tr.position, out floor) ? Mathf.Max(0f, (tr.position.y - floor) / K) : 0f;
            _gs = new Vector3(vel.x, 0f, vel.z).magnitude;
            _sigma = Dispersion(tr, _agl);
            if (!_count.TryGetValue(_sightView, out _displayCount)) _displayCount = 0;
            _stickStep = new Vector3(vel.x, 0f, vel.z) * (K * Mathf.Max(0.1f, F(CfgInterval, 0.4f)));
            if (!_solved) return;
            // Exactly one non-allocating cast per solution, never on release.
            // Includes roofs; ignore the aircraft/crew if a near-vertical drop
            // catches the carrier, rather than casting repeatedly through it.
            RaycastHit hit;
            Vector3 above = new Vector3(_impact.x, Mathf.Max(_sampleRack.y + K, _impact.y + 50f * K), _impact.z);
            if (Physics.Raycast(above, Vector3.down, out hit, above.y - _impact.y + 10f * K,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && hit.transform != null && !hit.transform.IsChildOf(tr)
                && !(hit.collider is CharacterController))
            {
                // A roof changes the fall time and therefore the horizontal lead.
                // Only accept it when the shorter fall still ends within the same
                // collider footprint; do not advertise a roof the bomb misses.
                Vector3 roof;
                float time;
                if (OnPlane(_sampleRack, vel, hit.point.y, out roof, out time)
                    && hit.collider.bounds.Contains(new Vector3(roof.x, hit.point.y, roof.z)))
                { _impact = roof; _fall = time; }
            }
        }

        // Use a bounded analytic ray/terrain intersection only on a target action.
        // An upward view cannot silently mark the predicted impact as a target.
        static void Mark(Ray ray)
        {
            if (ray.direction.y >= -0.05f) { _aimSet = _mapAim = false; return; }
            float floor;
            if (!Ground(ray.origin, out floor)) return;
            Vector3 at = ray.origin;
            for (int i = 0; i < 5; i++)
            {
                float distance = (floor - ray.origin.y) / ray.direction.y;
                if (distance < 0f || distance > 6000f * K) return;
                at = ray.GetPoint(distance);
                float next;
                if (!Ground(at, out next)) return;
                if (Mathf.Abs(next - floor) < 0.25f * K)
                {
                    at.y = next;
                    _aim = at;
                    _aimSet = true;
                    _mapAim = false;
                    return;
                }
                floor = next;
            }
        }

        /// <summary>Where the sight looks from: UNDER the belly, ahead of the
        /// main gear. It used to be half a metre above the wheels' reference
        /// point, which is inside the fuselage - the cabin floor and the
        /// pilot's own body filled the picture (field report 2026-09-28).
        /// Never under the ground on a low pass.</summary>
        static Vector3 SightEye(Transform tr)
        {
            Vector3 eye = tr.position + tr.rotation * (new Vector3(0f, -1.2f, 2.5f) * K);
            float ground = tr.position.y - _agl * K;
            if (eye.y < ground + 0.8f * K) eye.y = ground + 0.8f * K;
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

        // Keep the existing flight seam but leave steering in the pilot's hands.
        internal static bool Steer(out float bank)
        {
            bank = 0f;
            return false;
        }

        /// <summary>Look at the current predicted impact, independently of
        /// a marked target. Ground track is screen up.</summary>
        internal static void SightCamera(Camera cam, Transform tr)
        {
            if (cam == null || tr == null) return;
            if (_savedFov < 0f) _savedFov = cam.fieldOfView;
            cam.fieldOfView = Mathf.Clamp(F(CfgSightFov, 40f), 10f, 90f);
            Vector3 eye = SightEye(tr);
            Vector3 track = Track(tr);
            Vector3 look = _solved ? (DisplayImpact(tr) - eye) : (track * 2f + Vector3.down);
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
            int have;
            if (!_count.TryGetValue(_sightView, out have)) have = 0;
            if (have <= 0)
            {
                Hint(Loc.T("Бомб нет - подвесьте бомбы на земле", "No bombs aboard - load bombs on the ground"), 3f);
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
            float fall = _solved ? _fall : 5f;
            float bank = Mathf.Abs(Vector3.Angle(tr.right, Vector3.ProjectOnPlane(tr.right, Vector3.up)));
            float sigma = Dispersion(tr, _agl);
            float r = sigma * Gauss();
            float a = UnityEngine.Random.value * Mathf.PI * 2f;
            vel += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (r / Mathf.Max(0.5f, fall));

            int kg = RackKgOf(_sightView);
            Bomb b = new Bomb();
            b.Id = UnityEngine.Random.Range(1, 8000000);
            b.Pos = start;
            b.Vel = vel;
            b.Kg = kg;
            b.Mine = true;
            b.Go = Visual(start, vel, kg);
            _bombs.Add(b);
            SetCount(plane, have - 1, kg, true);
            _displayCount = have - 1;
            Net.Send(new float[] { 0f, b.Id, start.x, start.y, start.z, vel.x, vel.y, vel.z, kg }, true);
            RevivalPlugin.L.LogInfo("An2Bombs: " + kg + " kg bomb " + b.Id + " away at " + start.ToString("0")
                + ", " + Mathf.RoundToInt(_agl) + " m over the ground, " + Mathf.RoundToInt(_gs * 3.6f)
                + " km/h, bank " + bank.ToString("0") + ", sigma " + sigma.ToString("0.0") + " m; "
                + (have - 1) + " left.");
            Hint(Loc.T("Бомба сброшена", "Bomb away") + " - " + (have - 1) + "/" + An2BombLoad.CapacityFor(kg), 2f);
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
                GiveVisual(b.Go);
                if (b.Mine) Burst(b.Id, at, b.Age < arm, b.Kg);
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
        static void Burst(int id, Vector3 point, bool dud, int kg)
        {
            Net.Send(new float[] { 1f, id, point.x, point.y, point.z, dud ? 1f : 0f, kg }, true);
            if (dud)
            {
                Hint(Loc.T("Слишком низко - взрыватель не взвёлся", "Too low - the fuze did not arm"), 3f);
                RevivalPlugin.L.LogInfo("An2Bombs: bomb " + id + " was a dud at " + point.ToString("0") + ".");
                return;
            }
            float radius = RadiusU(kg);
            try { RocketHook.Detonate(point + Vector3.up * 0.2f, 0f, radius, 3f); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("An2Bombs: no visible explosion at " + point.ToString("0") + " - " + ex.Message);
            }
            RememberOwn(id);
            QueueBlast(point, radius, kg, id);
            OrdnanceBlast.EnqueuePlayers(point, radius, Peak(CfgPlayerDamage, 300f, kg));
            Depot(point, kg);
            RevivalPlugin.L.LogInfo("An2Bombs: " + kg + " kg bomb " + id + " burst at " + point.ToString("0")
                + " - damage queued on master.");
        }

        /// <summary>The master's side of somebody else's bomb: one NPC/vehicle
        /// pass at the transmitted collision point. Shooter handles players.</summary>
        static void RemoteBurst(Vector3 point, int kg, int id)
        {
            float radius = RadiusU(kg);
            QueueBlast(point, radius, kg, id);
            Depot(point, kg);
            RevivalPlugin.L.LogInfo("An2Bombs: a player's " + kg + " kg bomb at " + point.ToString("0")
                + " - damage queued on master.");
        }

        // A-L1: full NPC damage in the core and a certain kill at its centre
        // (OrdnanceBlast.Enqueue lethalCore). The master reports the burst and
        // returns the outcome to the dropper by bomb id (BlastResult).
        static void QueueBlast(Vector3 point, float radius, int kg, int id)
        {
            OrdnanceBlast.Enqueue(point, radius, Peak(CfgNpcDamage, 500f, kg),
                Peak(CfgVehicleDamage, 1200f, kg), 0f, LethalCore, Label(kg), id);
        }

        // Ids of this client's own recent bursts: their results become a hint.
        static readonly int[] _ownIds = new int[16];
        static int _ownNext;
        static int _tallyKilled, _tallyHit, _tallyBursts;
        static float _tallyUntil;

        static void RememberOwn(int id) { _ownIds[_ownNext] = id; _ownNext = (_ownNext + 1) % _ownIds.Length; }

        static bool IsOwn(int id)
        {
            if (id == 0) return false;
            for (int i = 0; i < _ownIds.Length; i++) if (_ownIds[i] == id) return true;
            return false;
        }

        /// <summary>Master, after a bomb's NPC pass: the dropper sees it at
        /// once, a remote dropper by event kind 3. hit = hurt plus NPCs whose
        /// damage went to another Photon owner.</summary>
        internal static void BlastResult(int id, int killed, int hit, int reached)
        {
            if (IsOwn(id)) ShowResult(killed, hit);
            else Net.Send(new float[] { 3f, id, killed, hit, reached }, true);
        }

        static void ShowResult(int killed, int hit)
        {
            if (Time.time > _tallyUntil) { _tallyKilled = 0; _tallyHit = 0; _tallyBursts = 0; }
            _tallyUntil = Time.time + 20f;
            _tallyKilled += killed; _tallyHit += hit; _tallyBursts++;
            Hint(Loc.T("Бомбы: убито " + _tallyKilled + ", ранено " + _tallyHit + " (взрывов: " + _tallyBursts + ")",
                "Bombs: " + _tallyKilled + " killed, " + _tallyHit + " hit (" + _tallyBursts + " burst"
                + (_tallyBursts == 1 ? ")" : "s)")), 6f);
        }

        static void Depot(Vector3 point, int kg)
        {
            // The visual ExplosionObject has zero damage. Apply the vehicle
            // profile once on the master (local Burst or RemoteBurst).
            if (FuelDepot.Active)
            {
                if (RevivalTroopInsertion.MasterClient())
                    FuelDepot.Blast(point, Peak(CfgVehicleDamage, 1200f, kg), RadiusU(kg));
                return;
            }
            if (CfgDepotHit != null && !CfgDepotHit.Value) return;
            if (!An2Repair.DepotHit(point, Mathf.Max(1f, F(CfgDepotReach, 25f)) * K)) return;
            Vector3 at = An2Repair.Depot;
            float y;
            if (RevivalTroopInsertion.GroundY(at, out y)) at.y = y;
            float radius = Mathf.Max(1f, F(CfgRadius, 25f)) * K * 1.6f;
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

        /// <summary>The falling bomb: the item mesh scaled to a FAB-50's real
        /// 1.07 m (x Scale for a FAB-100), nose along the flight path.</summary>
        static GameObject Visual(Vector3 at, Vector3 vel, int kg)
        {
            if (CfgModel != null && !CfgModel.Value) return null;
            if (!LoadModel()) return null;
            GameObject go = null;
            for (int i = 0; i < _visualPool.Length; i++)
            {
                if (_visualPool[i] != null && _visualPool[i].activeSelf) continue;
                go = _visualPool[i];
                if (go == null) { go = BuildVisual(); _visualPool[i] = go; }
                go.SetActive(true);
                break;
            }
            if (go == null) return null; // the damage/trajectory is independent
            // Pooled shells are shared by FAB-50 and FAB-100: size per drop.
            float scale = _meshScale * An2BombLoad.Scale(kg);
            Transform part = go.transform.GetChild(0);
            part.localScale = Vector3.one * scale;
            part.localPosition = -(_meshTurn * (_meshCentre * scale));
            go.transform.position = at;
            if (vel.sqrMagnitude > 0.01f) go.transform.rotation = Quaternion.LookRotation(vel.normalized);
            return go;
        }

        static readonly GameObject[] _visualPool = new GameObject[64];
        static void GiveVisual(GameObject go) { if (go != null) go.SetActive(false); }
        static GameObject BuildVisual()
        {
            GameObject go = new GameObject("NDR pooled An2 bomb");
            GameObject part = new GameObject("mesh");
            part.transform.SetParent(go.transform, false);
            part.transform.localRotation = _meshTurn;
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
                Net.Send(new float[] { 2f, kv.Key, kv.Value, RackKgOf(kv.Key) }, false);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) { _count.Remove(gone[i]); _rackKg.Remove(gone[i]); }
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
                        b.Kg = An2BombLoad.KgAt(f, 8);
                        b.Mine = false;
                        b.Go = Visual(b.Pos, b.Vel, b.Kg);
                        _bombs.Add(b);
                        return;
                    }
                    if (kind == 1 && f.Length >= 6)
                    {
                        int id = Mathf.RoundToInt(f[1]);
                        for (int i = _bombs.Count - 1; i >= 0; i--)
                        {
                            if (_bombs[i].Mine || _bombs[i].Id != id) continue;
                            GiveVisual(_bombs[i].Go);
                            _bombs.RemoveAt(i);
                        }
                        if (f[5] < 0.5f && RevivalTroopInsertion.MasterClient())
                            RemoteBurst(new Vector3(f[2], f[3], f[4]), An2BombLoad.KgAt(f, 6), id);
                        return;
                    }
                    if (kind == 3 && f.Length >= 5)
                    {
                        // A-L1: the master's result of a bomb; only its dropper shows it.
                        if (IsOwn(Mathf.RoundToInt(f[1])))
                            ShowResult(Mathf.RoundToInt(f[2]), Mathf.RoundToInt(f[3]));
                        return;
                    }
                    if (kind == 2 && f.Length >= 3)
                    {
                        int view = Mathf.RoundToInt(f[1]);
                        if (view != 0)
                        {
                            // The sender's rack, whatever this client's own choice.
                            int kg = An2BombLoad.KgAt(f, 3);
                            _count[view] = Mathf.Clamp(Mathf.RoundToInt(f[2]), 0, An2BombLoad.CapacityFor(kg));
                            _rackKg[view] = kg;
                        }
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
            // A hint is a few-second line, never a standing notice (vanilla 3-6 s).
            _hintUntil = Time.time + Mathf.Min(seconds, 6f);
        }

        static GUIStyle _labelStyle;
        static readonly GUIContent _labelContent = new GUIContent();
        static readonly GUIContent _bombCaption = new GUIContent();
        static readonly GUIContent _altCaption = new GUIContent();
        static readonly GUIContent[] _digits = {
            new GUIContent("0"), new GUIContent("1"), new GUIContent("2"), new GUIContent("3"),
            new GUIContent("4"), new GUIContent("5"), new GUIContent("6"), new GUIContent("7"),
            new GUIContent("8"), new GUIContent("9") };
        static readonly Vector3[] _circle = MakeCircle();
        static Material _sightLines;
        static bool _sightDrawingTried;
        static string _loadHud;
        static int _loadCount = -1, _loadKgShown = -1;
        static GameObject _loadPlane;

        static Vector3[] MakeCircle()
        {
            Vector3[] points = new Vector3[32];
            for (int i = 0; i < points.Length; i++)
            {
                float a = i * Mathf.PI * 2f / points.Length;
                points[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            return points;
        }

        static void PrepareSightDrawing()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.fontSize = 14;
                _bombCaption.text = Loc.T("БОМБ", "BOMBS");
                _altCaption.text = Loc.T("ВЫС м", "ALT m");
            }
            if (!_sightDrawingTried)
            {
                _sightDrawingTried = true;
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return;
                _sightLines = new Material(shader);
                _sightLines.hideFlags = HideFlags.HideAndDontSave;
                _sightLines.SetInt("_SrcBlend", 5);
                _sightLines.SetInt("_DstBlend", 10);
                _sightLines.SetInt("_Cull", 0);
                _sightLines.SetInt("_ZWrite", 0);
                _sightLines.SetInt("_ZTest", 8);
            }
        }

        // Translate the cached solution with the rack between 10 Hz samples.
        // This removes up to 7 m of sample lag at cruise without another query.
        static Vector3 DisplayImpact(Transform tr)
        {
            Vector3 move = RackOf(tr) - _sampleRack;
            move.y = 0f;
            return _impact + move;
        }

        internal static void Draw()
        {
            if (!Enabled) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            bool hint = !string.IsNullOrEmpty(_hint) && Time.time < _hintUntil;
            bool flying = PlayerAn2.Flying && !PlayerAn2.OnGround;
            bool loading = PlayerAn2.Plane == null && _near != null && !PlayerAn2.Aboard;
            if ((!flying && !loading && !hint) || GameUi.WindowOpen) return;
            PrepareSightDrawing();
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            GameObject plane = PlayerAn2.Plane;
            if (plane != null && flying)
                Sight(cx, cy);
            else if (plane == null && _near != null && !PlayerAn2.Aboard)
            {
                // Build only when loading / changing planes, not every repaint.
                if (_loadHud == null || _loadPlane != _near || Time.time >= _nextLoadHud)
                {
                    _nextLoadHud = Time.time + 0.5f;
                    int n = CountOf(_near), kg = RackKg(_near);
                    if (_loadHud == null || _loadPlane != _near || _loadCount != n || _loadKgShown != kg)
                    {
                        _loadPlane = _near; _loadCount = n; _loadKgShown = kg;
                        _loadHud = "[" + KeyName(PlayerAn2.KeyOf(CfgLoadKey, KeyCode.R)) + "] "
                            + Loc.T("Подвесить " + An2BombLoad.NameRu(kg), "Load " + An2BombLoad.Name(kg))
                            + " (" + n + "/" + An2BombLoad.CapacityFor(kg) + ")";
                    }
                }
                Label(_loadHud, cx, cy + 84f, new Color(0.95f, 0.90f, 0.70f, 1f), 14);
            }
            if (hint)
                Label(_hint, cx, cy + 60f, new Color(1f, 0.92f, 0.70f, 1f), 14);
        }
        static float _nextLoadHud;

        // A single GL batch instead of dozens of IMGUI textures/matrix rotations.
        // Only bomb count and height need text; digits use prebuilt GUIContent.
        static void Sight(float cx, float cy)
        {
            Camera cam = CameraOwner.ViewCamera();
            Color amber = new Color(1f, 0.75f, 0.25f, 1f);
            Color cyan = new Color(0.35f, 0.95f, 1f, 1f);
            if (cam != null && _solved && _sightLines != null && _sightLines.SetPass(0))
            {
                Transform tr = PlayerAn2.Plane.transform;
                Vector3 impact = DisplayImpact(tr);
                Vector3 centre = impact + Vector3.up * (0.15f * K);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);
                GL.Begin(GL.LINES);
                GL.Color(amber);
                // 95% radial dispersion for the release's signed Gaussian scatter.
                float radius = Mathf.Max(0.5f, 1.96f * _sigma) * K;
                for (int i = 0; i < _circle.Length; i++)
                    WorldLine(cam, centre + _circle[i] * radius,
                        centre + _circle[(i + 1) % _circle.Length] * radius);
                Vector2 p;
                if (ToGui(cam, centre, out p) && p.x >= 0f && p.x <= Screen.width
                    && p.y >= 0f && p.y <= Screen.height)
                {
                    Line(p.x - 5f, p.y, p.x + 5f, p.y);
                    Line(p.x, p.y - 5f, p.x, p.y + 5f);
                }
                else TargetMarker(cam, centre, cx, cy);
                // Each dot represents one remaining bomb at the configured
                // interval. Assumes current velocity, height and level ground.
                if (_displayCount > 1)
                {
                    WorldLine(cam, centre, centre + _stickStep * (_displayCount - 1));
                    for (int i = 1; i < _displayCount; i++)
                        if (ToGui(cam, centre + _stickStep * i, out p))
                        {
                            Line(p.x - 3f, p.y, p.x + 3f, p.y);
                            Line(p.x, p.y - 3f, p.x, p.y + 3f);
                        }
                }
                if (_aimSet)
                {
                    GL.Color(cyan);
                    WorldLine(cam, centre, _aim + Vector3.up * K * 0.15f);
                    TargetMarker(cam, _aim, cx, cy);
                    Vector3 track = Track(tr), miss = _aim - impact;
                    miss.y = 0f;
                    float ahead = Vector3.Dot(miss, track) / K;
                    float cross = Mathf.Abs(Vector3.Cross(track, miss).y) / K;
                    float tolerance = Mathf.Max(2f, _sigma);
                    bool now = Mathf.Abs(ahead) <= tolerance && cross <= tolerance && _displayCount > 0;
                    GL.Color(now ? new Color(0.3f, 1f, 0.35f, 1f) : cyan);
                    float y = cy + 120f;
                    Line(cx - 80f, y, cx + 80f, y);
                    Line(cx, y - 9f, cx, y + 9f);
                    // Centre means release now; cue moves through centre even
                    // after a missed target (no automatic target replacement).
                    float cue = Mathf.Clamp(ahead / Mathf.Max(1f, _gs * 5f), -1f, 1f) * 80f;
                    Line(cx + cue, y - 15f, cx + cue, y + 15f);
                    float steer = Mathf.Clamp(Vector3.Cross(track, miss).y / (K * 60f), -1f, 1f) * 80f;
                    Line(cx + steer - 4f, y + 21f, cx + steer, y + 17f);
                    Line(cx + steer, y + 17f, cx + steer + 4f, y + 21f);
                }
                GL.End();
                GL.PopMatrix();
            }
            _labelStyle.fontSize = 14;
            _labelStyle.normal.textColor = amber;
            VanillaUi.Label(new Rect(cx - 88f, cy + 158f, 70f, 24f), _bombCaption, _labelStyle);
            Number(_displayCount, cx - 10f, cy + 158f);
            VanillaUi.Label(new Rect(cx + 24f, cy + 158f, 65f, 24f), _altCaption, _labelStyle);
            Number(Mathf.RoundToInt(_agl), cx + 94f, cy + 158f);
        }

        static void Number(int value, float x, float y)
        {
            value = Mathf.Clamp(value, 0, 99999);
            int divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            do
            {
                VanillaUi.Label(new Rect(x, y, 12f, 24f), _digits[(value / divisor) % 10], _labelStyle);
                x += 10f;
                divisor /= 10;
            } while (divisor > 0);
        }

        static bool ToGui(Camera cam, Vector3 world, out Vector2 gui)
        {
            Vector3 p = cam.WorldToScreenPoint(world);
            gui = new Vector2(p.x, Screen.height - p.y);
            return p.z > 0f && gui.x > -50f && gui.x < Screen.width + 50f
                && gui.y > -50f && gui.y < Screen.height + 50f;
        }

        static void WorldLine(Camera cam, Vector3 a, Vector3 b)
        {
            Vector3 pa = cam.WorldToScreenPoint(a), pb = cam.WorldToScreenPoint(b);
            if (pa.z <= 0f || pb.z <= 0f) return;
            // Clip to the viewport so a distant marker cannot draw an enormous
            // line across the GUI. Near-plane crossings are deliberately hidden.
            float ax = pa.x, ay = Screen.height - pa.y;
            float dx = pb.x - ax, dy = Screen.height - pb.y - ay;
            float lo = 0f, hi = 1f;
            if (!Clip(-dx, ax, ref lo, ref hi) || !Clip(dx, Screen.width - ax, ref lo, ref hi)
                || !Clip(-dy, ay, ref lo, ref hi) || !Clip(dy, Screen.height - ay, ref lo, ref hi)) return;
            Line(ax + dx * lo, ay + dy * lo, ax + dx * hi, ay + dy * hi);
        }

        static bool Clip(float p, float q, ref float lo, ref float hi)
        {
            if (Mathf.Abs(p) < 0.00001f) return q >= 0f;
            float r = q / p;
            if (p < 0f) { if (r > hi) return false; if (r > lo) lo = r; }
            else { if (r < lo) return false; if (r < hi) hi = r; }
            return true;
        }

        static void TargetMarker(Camera cam, Vector3 target, float cx, float cy)
        {
            Vector2 p;
            if (ToGui(cam, target, out p) && p.x >= 12f && p.x <= Screen.width - 12f
                && p.y >= 12f && p.y <= Screen.height - 12f)
            {
                Line(p.x - 8f, p.y, p.x, p.y - 8f); Line(p.x, p.y - 8f, p.x + 8f, p.y);
                Line(p.x + 8f, p.y, p.x, p.y + 8f); Line(p.x, p.y + 8f, p.x - 8f, p.y);
                return;
            }
            Vector3 at = cam.WorldToScreenPoint(target);
            Vector2 d = new Vector2(at.x - cx, Screen.height - at.y - cy);
            if (at.z <= 0f) d = -d;
            if (d.sqrMagnitude < 1f) d = Vector2.down;
            d.Normalize();
            float tx = Mathf.Abs(d.x) > 0.001f ? (cx - 20f) / Mathf.Abs(d.x) : 100000f;
            float ty = Mathf.Abs(d.y) > 0.001f ? (cy - 20f) / Mathf.Abs(d.y) : 100000f;
            Vector2 tip = new Vector2(cx, cy) + d * Mathf.Min(tx, ty);
            Vector2 side = new Vector2(-d.y, d.x) * 5f;
            Vector2 tail = tip - d * 10f;
            Line(tip.x, tip.y, tail.x + side.x, tail.y + side.y);
            Line(tip.x, tip.y, tail.x - side.x, tail.y - side.y);
        }

        static void Line(float ax, float ay, float bx, float by)
        {
            GL.Vertex3(ax, ay, 0f); GL.Vertex3(bx, by, 0f);
        }

        static void Label(string text, float cx, float y, Color colour, int size)
        {
            VanillaUi.Readout(text, cx, y, colour, size);
        }
    }
}

// Next Day: Survival - Revival Toolkit
//
// THE TOWER RADAR (P5): the airfield tower C1 becomes the air defence HQ of
// the east airfield, on top of the P4 flak (Revival.Flak.cs, its API).
//
// WHAT THIS IS, and what it is built from:
//
//   1  THE RADAR. A P-18-style VHF early-warning radar north of the tower:
//      sixteen Yagis in two rows on a turning frame, 8.8 m wide, its centre
//      8 m up, on a mast over a two-axle trailer, with the operators' KUNG
//      shelter and a generator. It is a building-kit model in the H1 look
//      (unity/EastTile/Tools/c1_radar.py -> "C1 radar" in the c1 bundle); the
//      game turns its "Head" transform. Without the rebuilt bundle the same
//      radar is built here from primitives, dressed in the H1 hangar's own
//      kit materials where the H1 bundle is loaded. The antenna turns at
//      AntennaRpm on the shared Photon clock, so every client sees the same
//      bearing without any message, and the scope's sweep IS that bearing.
//   2  THE CONSOLE in the cab (the tower's top room, 12 m up): a cabinet with
//      a round PPI screen and the operator's chair against the south wall.
//      Walk up, press the turret key (G): the radar view - a PPI scope of the
//      whole east tile airspace (ScopeRange, north up), range rings, the tile
//      outline and the runway, the sweep line with its afterglow, and a blip
//      for every helicopter, plane and drone the antenna paints: painted when
//      the sweep passes it, fading until the next pass. Each blip carries a
//      track number, a type guess, heading, speed and height, and its
//      friend/foe from who is aboard (a player of the viewer's own faction =
//      FRIEND, of another = FOE, nobody = UNKNOWN). A virtual cursor (the
//      mouse) selects a blip; buttons: ENGAGE SELECTED (weapons free on it),
//      WEAPONS FREE ALL, HOLD FIRE ALL, CLEAR. The contact log (radio log)
//      lists new and lost tracks, guns opening fire, the siren, damage.
//   3  FIRE CONTROL goes through the flak API on the master: AssignTarget,
//      WeaponsFree, HoldFire and the new AssignedOnly (hold everything but
//      the selected blip). While the HQ's own NPC operator lives at the
//      console the tower is held by the airfield's faction ([Airfield]
//      DefenderFaction) and only that faction is obeyed. Once he is dead the
//      console is free: any player may give orders, but a gun only follows a
//      side whose crew it has - an empty gun or one a player of that side
//      mans follows; a gun still manned by the airfield's crew follows only
//      the airfield's own side. Orders of another side standing on a gun are
//      dropped when the airfield's crew mans it again, and all of them when
//      the HQ operator is back (the scope names who holds the radar and
//      which guns follow; the gun's sight shows the radar's order). With an
//      operator - such a player, or the HQ's own NPC operator (key
//      radar/c1/op) - the guns are radar-directed (Flak.SetFireDirection:
//      shorter reaction, smaller first error, faster tracking; N6
//      Flak.SetRadarDirected: [Flak52K] RadarRange and the fast
//      RadarWalkFactor). Without one they lay by eye: VisualRange, the slow
//      WalkFactor. A dead or WOUNDED operator (the game's wounded state,
//      NpcWar.GroundDowned) counts as none: the console is free. With the radar or the console
//      destroyed they are worse than that (no warning at all).
//   4  DAMAGE. The antenna head, the mast, the KUNG shelter and the console
//      are targets: the local player's rounds (a postfix on the game's
//      FireOneShot, a ray against their boxes, walls in between stop it,
//      window panes do not) and the game's explosions (a postfix on
//      ExplosionObject.NetworkVisualizeExplode, falling off with distance).
//      The master keeps the hit points (RadarHits, ConsoleHits). Destroyed:
//      the antenna stops and tilts, the scope goes dark, the
//      screen dies, the guns lose their direction. W AA7 replaces automatic
//      repair with a toolkit and 60 seconds beside the damaged part.
//   5  EXTRAS. The air raid siren on the tower roof: on the master's word
//      when a hostile aircraft is within SirenRange of a working radar; when
//      it starts, the NPCs round the airfield are alarmed (the game's own
//      SetGeneralAlarm). Runway edge lights for the An-2 at night (the tower
//      has power: the console lives).
//   6  THE WIRE. One Photon event (RadarNet, NetworkEventCode 197): the
//      master's state once a second and on change (hit points, operator,
//      fire mode, assigned blip, siren, direction tier), a client's claim of
//      the console, its hits on the HQ and its console commands.
//
// WHAT IS NOT PROVEN WITHOUT THE GAME: where the console stands in the cab,
// the kit radar in the rebuilt bundle, the feel of the scope, the siren,
// whether the NPC operator can be spawned 12 m up. docs/ai/tasks/
// airfield-radar-p5.md lists the checks.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only, no BOM.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs          BindConfig / Install / Tick / LateUpdate / OnGUI.
//   Revival.Flak.cs           FlakMode.AssignedOnly, Flak.AssignedOnly,
//                             Flak.SetFireDirection (the direction scales).
//   unity/EastTile            Tools/c1_radar.py, airfield_assembly.py b_c1.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    /// <summary>
    /// The airfield's air defence HQ: config, the frame, the master's state
    /// (hit points, operator, fire mode, siren, direction) and the seams.
    /// </summary>
    public static class TowerRadar
    {
        /// <summary>World units per real metre (BuildContent.UnitsPerMetre).</summary>
        internal const float K = 2.8f;

        // ------------------------------------------------------------ config

        internal static ConfigEntry<bool> CfgEnabled, CfgRotation, CfgSweep, CfgFade, CfgGlow, CfgSiren,
            CfgSirenAlarm, CfgLog, CfgRunwayLights, CfgNpcOperator, CfgSeated, CfgDamageLook;
        internal static ConfigEntry<float> CfgRpm, CfgScopeRange, CfgSirenRange, CfgAlarmRadius, CfgRepair,
            CfgOpRespawn, CfgDirReaction, CfgDirError, CfgDirTracking, CfgDarkReaction, CfgDarkError,
            CfgDarkTracking, CfgReach, CfgCursor, CfgMinHeight;
        internal static ConfigEntry<int> CfgRadarHits, CfgConsoleHits, CfgExplosionHits, CfgEventCode;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "TowerRadar";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The air defence HQ in the airfield tower C1: P-18 radar, console with a PPI scope, "
                + "fire control of the 52-K guns (acting only with [World] EastTile, [Airfield] "
                + "Enabled and [Flak] Enabled for the guns).");
            CfgRotation = cfg.Bind(S, "AntennaRotation", true,
                "Animation: the radar antenna turns (the scope's sweep turns either way).");
            CfgSweep = cfg.Bind(S, "SweepLine", true, "Effect: the scope's sweep line and its afterglow.");
            CfgFade = cfg.Bind(S, "BlipFade", true,
                "Effect: blips fade between two sweeps. Off: a blip stays bright until repainted.");
            CfgGlow = cfg.Bind(S, "ScreenGlow", true, "Effect: the console's round screen glows while the radar works.");
            CfgSiren = cfg.Bind(S, "Siren", true,
                "Sound: the air raid siren on the tower roof when a hostile aircraft comes within SirenRange.");
            CfgSirenAlarm = cfg.Bind(S, "SirenNpcAlarm", true,
                "NPCs round the airfield are alarmed when the siren starts (the game's general alarm).");
            CfgLog = cfg.Bind(S, "ContactLog", true, "The radio contact log beside the scope.");
            CfgRunwayLights = cfg.Bind(S, "RunwayLights", true,
                "Effect: the runway edge lights burn at night while the tower has power (console intact).");
            CfgNpcOperator = cfg.Bind(S, "NpcOperator", true,
                "A man of the airfield's faction sits at the console (master spawns him); he directs the guns.");
            CfgSeated = cfg.Bind(S, "SeatedOperator", true,
                "Animation: the NPC operator sits on his chair with the game's seated clip. Off: he stands.");
            CfgDamageLook = cfg.Bind(S, "DamageVisuals", true,
                "Effect: a destroyed antenna loses its Yagi elements and stops; a destroyed console's screen dies.");
            CfgRpm = cfg.Bind(S, "AntennaRpm", 6f, "Antenna turns per minute (the real P-18: 2-6). One sweep of the scope.");
            CfgScopeRange = cfg.Bind(S, "ScopeRange", 2000f,
                "Scope radius in metres (x 2.8 in the world); 2000 m holds the whole east tile.");
            CfgMinHeight = cfg.Bind(S, "MinContactHeight", 3f,
                "An aircraft lower than this over the ground (metres) is ground clutter - not painted.");
            CfgSirenRange = cfg.Bind(S, "SirenRange", 1500f,
                "Metres from the radar within which a hostile aircraft sets the siren off.");
            CfgAlarmRadius = cfg.Bind(S, "SirenAlarmRadius", 450f, "Metres round the tower whose NPCs the siren alarms.");
            CfgRadarHits = cfg.Bind(S, "RadarHits", 30, "Rifle hits the antenna, mast and shelter survive together.");
            CfgConsoleHits = cfg.Bind(S, "ConsoleHits", 12, "Rifle hits the console survives.");
            CfgExplosionHits = cfg.Bind(S, "ExplosionHits", 40,
                "Hits an explosion is worth at its centre (falling off to 0 at its radius + 1.5 u).");
            CfgRepair = cfg.Bind(S, "RepairMinutes", 30f,
                "Legacy setting (unused): repair now requires a toolkit and 60 seconds at the damaged part.");
            CfgOpRespawn = cfg.Bind(S, "OperatorRespawnMinutes", 35f, "A dead NPC operator is replaced after this many minutes.");
            CfgDirReaction = cfg.Bind(S, "DirectedReaction", 0.45f,
                "With an operator: the guns' Reaction x this (radar-cued: they are laid before the target is seen).");
            CfgDirError = cfg.Bind(S, "DirectedError", 0.55f, "With an operator: the first burst's error x this (radar range).");
            CfgDirTracking = cfg.Bind(S, "DirectedTracking", 2.0f, "With an operator: the crews' tracking rate x this (radar rates).");
            CfgDarkReaction = cfg.Bind(S, "DestroyedReaction", 1.4f,
                "Radar or console destroyed: the guns' Reaction x this (no warning at all).");
            CfgDarkError = cfg.Bind(S, "DestroyedError", 1.3f, "Radar or console destroyed: the first burst's error x this.");
            CfgDarkTracking = cfg.Bind(S, "DestroyedTracking", 0.8f, "Radar or console destroyed: tracking rate x this.");
            CfgReach = cfg.Bind(S, "ConsoleReach", 2.5f, "Metres from the console within which the turret key (G) opens the radar view.");
            CfgCursor = cfg.Bind(S, "CursorSensitivity", 14f, "Radar view: screen pixels of cursor per unit of mouse movement.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 197,
                "Photon event code for the HQ's state, hits and commands. Must not overlap any other "
                + "channel (0..199).");
            AirPicture.BindConfig(cfg, S);
        }

        internal static bool On
        {
            get { return Airfield.On && (CfgEnabled == null || CfgEnabled.Value); }
        }

        internal static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        internal static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }
        internal static int I(ConfigEntry<int> c, int fallback) { return c == null ? fallback : c.Value; }
        internal static void Log(string s) { RevivalPlugin.L.LogInfo("TowerRadar: " + s); }

        internal static float ScopeRangeU { get { return Mathf.Max(300f, F(CfgScopeRange, 2000f)) * K; } }

        // ------------------------------------------------------- the places

        /// <summary>C1 (airfield_assembly.py b_c1: the tower at the greybox C1
        /// centre, yaw 0, cab east) and the radar's mast axis (c1_radar.py).</summary>
        internal static readonly Vector2 TowerSpot = new Vector2(4250f, 1335f);
        internal static readonly Vector2 MastSpot = new Vector2(4262f, 1392f);
        internal const string RadarName = "C1 radar";
        /// <summary>The console in the tower's frame, metres (x east, z north):
        /// the cab spans x 4.3..11.5 and z -3.6..3.6 (_c1_body.py), its old
        /// console stands along the east glazing; the radar console stands
        /// against the south wall, its screen facing north.</summary>
        internal static readonly Vector3 ConsoleLocalM = new Vector3(7.4f, 0f, -2.95f);
        internal const float CabFloorM = 12f;
        internal const float RoofM = 15.35f;

        // ------------------------------------------------------------- state

        internal static Transform Tower;          // the C1 model (null: its spot)
        internal static float TowerYaw;
        internal static Vector3 TowerBase;        // ground under the tower's origin
        internal static float CabFloorY;
        internal static Transform RadarRoot, Head, ConsoleRoot;
        internal static Renderer Screen;
        internal static readonly List<Renderer> Elements = new List<Renderer>();
        internal static bool Built, KitRadar;
        static float _nextFind, _tileSince = -1f;
        static bool _fallbackSaid;

        // the master's word (mirrored on every client from the state event)
        internal static float RadarHp = 1f, ConsoleHp = 1f;   // fraction left
        internal static int OperatorActor = -1;               // a player at the console, -1 none
        internal static bool NpcOperatorUp;
        internal static int Mode;                              // 0 weapons free, 1 hold fire, 2 assigned only
        internal static bool SirenOn;
        internal static int Tier = 1;                          // 0 dark, 1 by eye, 2 directed
        internal static int AssignedKind = -1;
        internal static Vector3 AssignedPos;
        internal static int ControlSide = -1;                  // Fraction value whose orders stand on the guns, -1 none

        // master only
        static float _opUntil;
        static float _nextMaster, _nextBroadcast, _sirenSince = -100f, _lastThreat = -100f;
        static bool _dirty, _wasMaster, _directionSet;
        static readonly List<string> _foreignOrders = new List<string>();   // guns holding another side's orders

        internal static bool RadarAlive { get { return RadarHp > 0f; } }
        internal static bool ConsoleAlive { get { return ConsoleHp > 0f; } }
        internal static bool Working { get { return RadarAlive && ConsoleAlive; } }

        internal static Vector3 RadarPos
        {
            get
            {
                if (RadarRoot != null) return RadarRoot.position + Vector3.up * 8f * K;
                return new Vector3(MastSpot.x, TowerBase.y + 8f * K, MastSpot.y);
            }
        }

        // ------------------------------------------------------------ install

        static readonly string[] Locks = {
            "PlayerMovementController::PlayerCantMovement",
            "PlayerMovementController::PlayerCantRotate",
            "PlayerMovementController::PlayerCantRotateAxisX",
            "PlayerMovementController::PlayerCantJump",
            "PlayerMovementController::PlayerCantRun",
            "PlayerFirearmWeaponController::CantShoot",
            "PlayerMeleeWeaponController::MeleeCantAttack",
            "PlayerGrenadeWeaponController::CantThrowGrenade",
            "MouseOrbitController::PlayerCantOrbitRotate",
        };

        public static void Install(Harmony harmony)
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            int n = 0;
            HarmonyMethod post = new HarmonyMethod(typeof(TowerRadar).GetMethod("LockPostfix"));
            for (int i = 0; i < Locks.Length; i++)
            {
                string[] p = Locks[i].Split(new string[] { "::" }, StringSplitOptions.None);
                try
                {
                    Type t = RevivalPlugin.TypeByName(p[0]);
                    MethodInfo m = t == null ? null : AccessTools.Method(t, p[1], null, null);
                    if (m == null || m.ReturnType != typeof(bool)) continue;
                    harmony.Patch(m, null, post, null, null, null);
                    n++;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("TowerRadar: input lock " + Locks[i] + ": " + ex.Message);
                }
            }
            string shot = "no", boom = "no";
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "FireOneShot", null, null);
                if (m != null)
                {
                    harmony.Patch(m, null, new HarmonyMethod(typeof(TowerRadar).GetMethod("ShotPostfix")), null, null, null);
                    shot = "yes";
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar: shot hook: " + ex.Message); }
            boom = "shared AA damage hook";
            Log("input lock on " + n + " of " + Locks.Length + " game predicates, shot hook " + shot
                + ", explosion hook " + boom + ".");
        }

        public static void LockPostfix(ref bool __result)
        {
            if (RadarScope.InView || AirDefenceDamage.Repairing) __result = true;
        }

        public static void ShotPostfix(object __instance)
        {
            try
            {
                if (!Built || !On || __instance == null || RadarScope.InView) return;
                RadarDamage.Shot(__instance);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar shot: " + ex.Message); }
        }

        public static void ExplodePostfix(object __instance)
        {
            try
            {
                if (!Built || !On) return;
                Component c = __instance as Component;
                if (c == null) return;
                AirDefenceDamage.ExplodePostfix(c);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar explosion: " + ex.Message); }
        }

        // ---------------------------------------------------------- the frame

        /// <summary>Every client, every frame.</summary>
        internal static void Tick()
        {
            if (!On)
            {
                if (Built) Clear("off");
                return;
            }
            try
            {
                RadarNet.EnsureHooked();
                RadarClock.Advance();
                Find();
                if (!Built) return;
                if (RadarRoot == null || ConsoleRoot == null) { Clear("the tower's scene went away"); return; }
                bool master = Crocodile.IsMaster();
                AirfieldOwnership.Ensure(master && !_wasMaster);
                if (master && !_wasMaster)
                {
                    // A new master: the flak crews start weapons free.
                    Mode = 0;
                    AssignedKind = -1;
                    if (ControlSide < 0) ControlSide = HomeSide();
                    _foreignOrders.Clear();
                    _dirty = true;
                }
                Flak.SightOrder = SightLine;
                _wasMaster = master;
                RadarOperator.Tick(master);
                if (master) MasterTick();
                Antenna();
                RadarScope.Tick();
                RadarSiren.Tick();
                RunwayLights.Tick();
                ConsoleLook();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TowerRadar: " + ex);
            }
        }

        /// <summary>Every client, after the animator: the NPC operator on his chair.</summary>
        internal static void LateFrame()
        {
            if (!Built) return;
            try { RadarOperator.Hold(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("TowerRadar late frame: " + ex); }
        }

        internal static void Draw()
        {
            if (!Built) return;
            RadarScope.Draw();
        }

        static void Clear(string why)
        {
            TowerSupport.WorldEnded();
            AirfieldHold.WorldEnded();           // W Tower 3: the hold state starts over
            RadarScope.Leave(why);
            RadarSiren.Stop();
            RunwayLights.Clear();
            if (RadarRoot != null && !KitRadar) UnityEngine.Object.Destroy(RadarRoot.gameObject);
            if (ConsoleRoot != null) UnityEngine.Object.Destroy(ConsoleRoot.gameObject);
            RadarRoot = Head = ConsoleRoot = null;
            Screen = null;
            Elements.Clear();
            Built = KitRadar = false;
            AirDefenceDamage.Reset(7, 8);
            RadarHp = ConsoleHp = 1f;
            _wasMaster = false;
            if (_directionSet) { Flak.SetFireDirection(1f, 1f, 1f); Flak.SetRadarDirected(false); Flak.DirectOwned(-1, 1); _directionSet = false; }
            AirfieldOwnership.Reset();
            Log("cleared (" + why + ").");
        }

        // ------------------------------------------------------ finding it all

        // Q1 perf: one time-sliced pass finds the kit radar, the C1 tower and
        // H1 together, instead of three full recursive walks in one frame every
        // 5 s until the HQ is built.
        static readonly SceneSweep _sweep = new SceneSweep();
        static readonly SceneSweep.Visitor _visit = FindVisit;
        static Transform _sKit, _sTower, _sH1;

        static bool FindVisit(Transform t, string name)
        {
            if (_sKit == null && name == RadarName) _sKit = t;
            if (_sH1 == null && name == "H1") _sH1 = t;
            if (_sTower == null && name == "C1" && IsTower(t)) _sTower = t;
            return true;
        }

        static void Find()
        {
            if (Built) { _sweep.Cancel(); return; }
            if (!_sweep.Active)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 5f;
                Scene tile0 = SceneManager.GetSceneByName(EastWorld.SceneName);
                if (!tile0.isLoaded) { _tileSince = -1f; return; }
                if (_tileSince < 0f) _tileSince = Time.realtimeSinceStartup;
                _sKit = _sTower = _sH1 = null;
                _sweep.Begin("East");
            }
            if (!_sweep.Step(1.5, _visit)) return;     // continues next frame
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) { _tileSince = -1f; return; }

            Transform kit = _sKit, tower = _sTower, h1 = _sH1;
            bool fallback = Time.realtimeSinceStartup - _tileSince > 40f;
            if ((kit == null || tower == null) && !fallback) return;
            if (kit == null && !_fallbackSaid)
            {
                _fallbackSaid = true;
                RevivalPlugin.L.LogWarning("TowerRadar: \"" + RadarName + "\" is not in the c1 bundle (not rebuilt "
                    + "with c1_radar.py yet) - the radar is built from primitives.");
            }
            Scene airfield = SceneManager.GetSceneByName("EastAirfield");
            Place(tower);
            RadarModel.Materials(h1, tower);
            if (kit != null)
            {
                RadarRoot = kit;
                Head = kit.Find("Head");
                KitRadar = true;
                Elements.Clear();
                if (Head != null)
                {
                    Renderer[] rs = Head.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < rs.Length; i++) if (rs[i].name == "Elements") Elements.Add(rs[i]);
                }
                Log("the kit radar \"" + RadarName + "\" at " + kit.position + (Head == null ? " has NO Head - it cannot turn." : ", Head found."));
            }
            else
            {
                RadarRoot = RadarModel.Build(airfield.isLoaded ? airfield : tile);
                Head = RadarRoot == null ? null : RadarRoot.Find("Head");
                KitRadar = false;
            }
            ConsoleRoot = RadarModel.Console(airfield.isLoaded ? airfield : tile);
            Built = RadarRoot != null && ConsoleRoot != null;
            Log(Built ? "HQ built: tower " + (tower != null ? "\"" + tower.name + "\"" : "(spot)") + " base y "
                + TowerBase.y.ToString("0.0", CultureInfo.InvariantCulture) + ", cab floor y "
                + CabFloorY.ToString("0.0", CultureInfo.InvariantCulture) + ", console at " + ConsoleRoot.position + "."
                : "HQ could not be built.");
        }

        /// <summary>The C1 model: a transform named "C1" with renderers under
        /// it, near the greybox spot.</summary>
        static bool IsTower(Transform t)
        {
            return Mathf.Abs(t.position.x - TowerSpot.x) < 20f && Mathf.Abs(t.position.z - TowerSpot.y) < 20f
                && t.GetComponentInChildren<Renderer>() != null;
        }

        internal static Transform Deep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform f = Deep(t.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>The tower's frame: its origin at ground level (the C1
        /// handover), the cab floor found by a ray (C1 has a floor collider),
        /// 12 m up where there is none.</summary>
        static void Place(Transform tower)
        {
            Tower = tower;
            Vector3 spot = new Vector3(TowerSpot.x, 0f, TowerSpot.y);
            float ground;
            if (!EastWorld.TerrainHeight(spot, out ground)) ground = tower != null ? tower.position.y : 0f;
            TowerYaw = tower != null ? tower.eulerAngles.y : 0f;
            TowerBase = tower != null ? new Vector3(tower.position.x, Mathf.Min(tower.position.y + 0.5f, ground + 0.5f), tower.position.z)
                                      : new Vector3(spot.x, ground, spot.z);
            if (tower != null && Mathf.Abs(tower.position.y - ground) < 6f) TowerBase.y = tower.position.y;
            CabFloorY = TowerBase.y + CabFloorM * K;
            Vector3 probe = TowerPoint(new Vector3(ConsoleLocalM.x, CabFloorM + 1.5f, 0f));
            RaycastHit hit;
            if (Physics.Raycast(probe, Vector3.down, out hit, 3f * K, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && hit.point.y > TowerBase.y + 10.5f * K)
                CabFloorY = hit.point.y;
        }

        /// <summary>A point of the tower's frame (metres, x east, z north,
        /// y up from its base) in the world.</summary>
        internal static Vector3 TowerPoint(Vector3 metres)
        {
            return TowerBase + Quaternion.Euler(0f, TowerYaw, 0f) * (metres * K);
        }

        // ------------------------------------------------------------ antenna

        internal static float AntennaAngle
        {
            get
            {
                float rpm = Mathf.Clamp(F(CfgRpm, 6f), 0.5f, 30f);
                return Mathf.Repeat(RadarClock.Now * rpm * 6f, 360f);
            }
        }

        internal static float SweepSeconds { get { return 60f / Mathf.Clamp(F(CfgRpm, 6f), 0.5f, 30f); } }

        static float _frozenAt = -1f;

        static void Antenna()
        {
            if (Head == null) return;
            bool turn = B(CfgRotation) && RadarAlive && ConsoleAlive;
            if (turn)
            {
                _frozenAt = -1f;
                Head.localRotation = Quaternion.Euler(0f, AntennaAngle, 0f);
            }
            else
            {
                if (_frozenAt < 0f) _frozenAt = AntennaAngle;
                Head.localRotation = Quaternion.Euler(0f, _frozenAt, 0f);
            }
            if (!RadarAlive && B(CfgDamageLook)) Head.localRotation = Quaternion.Euler(18f, 0f, 20f);
            bool show = true; // leave a tilted, charred wreck rather than disappearing
            for (int i = 0; i < Elements.Count; i++)
                if (Elements[i] != null && Elements[i].enabled != show) Elements[i].enabled = show;
        }

        static bool _screenLit = true, _screenDead;

        static void ConsoleLook()
        {
            if (Screen == null) return;
            bool lit = Working && B(CfgGlow);
            bool dead = !ConsoleAlive && B(CfgDamageLook);
            if (lit == _screenLit && dead == _screenDead) return;
            _screenLit = lit; _screenDead = dead;
            Screen.sharedMaterial = dead ? RadarModel.Black : lit ? RadarModel.Phosphor : RadarModel.DarkGlass;
        }

        // ---------------------------------------------------------- the master

        static void MasterTick()
        {
            if (Time.time < _nextMaster) return;
            _nextMaster = Time.time + 0.5f;

            // the operator: B1 - the claim ends with its owner, not only on a
            // clean leave. A remote claimant must renew it (disconnect, crash,
            // logout: the lease runs out), a dead one loses it at once, and
            // the master's own claim (also after a master change) needs him
            // in the radar view.
            if (OperatorActor >= 0)
            {
                string gone = null;
                if (OperatorActor == Crocodile.LocalActor()) { if (!RadarScope.InView) gone = "is not at the console"; }
                else if (Time.time > _opUntil) gone = "is gone";
                if (gone == null && Crocodile.ActorDown(OperatorActor)) gone = "is down";
                if (gone != null)
                {
                    Log("the console's operator (actor " + OperatorActor + ") " + gone + ": the console is free.");
                    OperatorActor = -1;
                    _dirty = true;
                }
            }
            bool npc = RadarOperator.Alive;
            int home = HomeSide();
            if (ControlSide < 0) ControlSide = home;
            if (npc != NpcOperatorUp)
            {
                NpcOperatorUp = npc;
                _dirty = true;
                RadarScope.Note(npc ? "HQ operator at the console - " + SideLabel(home) + " hold the radar."
                    : "HQ OPERATOR DOWN - the console is free.");
                Log(npc ? "the HQ operator holds the tower." : "the HQ operator is down: the console takes any side's orders.");
                if (npc && ControlSide != home) GiveBack(home);
            }
            if (MercAA.Operator != null) ControlSide = MercAA.Operator.Side;
            DropForeignOrders(home);

            // the guns' direction
            int tier;
            if (!Working) tier = 0;
            else if (MercAA.Operator != null || npc || (OperatorActor >= 0 && Obeyed(OperatorActor))) tier = 2;
            else tier = 1;
            if (tier != Tier || !_directionSet)
            {
                if (tier != Tier) Log("fire direction: " + TierName(tier) + ".");
                Tier = tier;
                _dirty = true;
                _directionSet = true;
                if (tier == 2) Flak.SetFireDirection(F(CfgDirReaction, 0.45f), F(CfgDirError, 0.55f), F(CfgDirTracking, 2f));
                else if (tier == 0) Flak.SetFireDirection(F(CfgDarkReaction, 1.4f), F(CfgDarkError, 1.3f), F(CfgDarkTracking, 0.8f));
                else Flak.SetFireDirection(1f, 1f, 1f);
                // N6: an operator at the console = radar range and fast
                // bracketing; anything else = visual range, slow bracketing.
                Flak.SetRadarDirected(tier == 2);
            }
            Flak.DirectOwned(npc ? home : OperatorActor >= 0 ? PlayerSide(OperatorActor) : home, tier);

            // the assigned blip follows its aircraft
            if (AssignedKind >= 0)
            {
                GepardGun.Contact c = RadarScope.Resolve(AssignedKind, AssignedPos, 400f);
                if (c == null) { AssignedKind = -1; _dirty = true; }
                else AssignedPos = c.Pos;
            }

            // the siren
            bool threat = Working && RadarScope.HostileWithin(Mathf.Max(100f, F(CfgSirenRange, 1500f)) * K);
            if (threat) _lastThreat = Time.time;
            bool want = SirenOn ? (Time.time - _lastThreat < 20f || Time.time - _sirenSince < 25f) : threat;
            if (want != SirenOn)
            {
                SirenOn = want;
                _dirty = true;
                if (want)
                {
                    _sirenSince = Time.time;
                    RadarSiren.AlarmNpcs();
                }
                RadarScope.Note(want ? "AIR RAID SIREN on." : "Siren off - all clear.");
                Log("siren " + (want ? "on" : "off") + ".");
            }

            if (_dirty || Time.time >= _nextBroadcast)
            {
                _dirty = false;
                _nextBroadcast = Time.time + 1f;
                RadarNet.SendState();
            }
        }

        internal static string TierName(int tier)
        {
            return tier == 2 ? "radar-directed" : tier == 0 ? "no radar (degraded)" : "by eye";
        }

        /// <summary>May this player give orders at the console? The airfield's
        /// own faction always; any other side only while the tower is not held
        /// - the HQ's NPC operator is dead (the faction lock).</summary>
        internal static bool Obeyed(int actor)
        {
            int side = PlayerSide(actor);
            if (side < 0) return false;
            MercAAPost merc = MercAA.Operator;
            return merc != null ? side == merc.Side : side == HomeSide() || !NpcOperatorUp;
        }

        /// <summary>B1: a player whose faction cannot be read. He is still a
        /// side of his own ("PLAYERS"), never nobody: an unreadable faction
        /// used to refuse every order at a free console.</summary>
        internal const int UnreadSide = 999;

        /// <summary>The game's Fraction value of a player, -1 no such player,
        /// <see cref="UnreadSide"/> a player whose faction is unreadable.</summary>
        internal static int PlayerSide(int actor)
        {
            GameObject p = Crocodile.PlayerByActor(actor);
            if (p == null) return -1;
            int side = SideId(Fraktion.Spielerseite(p));
            return side < 0 ? UnreadSide : side;
        }

        /// <summary>The airfield's own side (its crews, its HQ operator).</summary>
        internal static int HomeSide() { return AirfieldOwnership.Holder; }

        // W Perf1: a faction name's value, parsed once per name (HomeSide is
        // asked every frame; Enum.Parse allocated each time).
        static readonly Dictionary<string, int> _sideIds = new Dictionary<string, int>();

        internal static int SideId(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            int known;
            if (_sideIds.TryGetValue(name, out known)) return known;
            int id;
            try
            {
                Type t = RevivalPlugin.TypeByName("Fraction");
                if (t == null || !t.IsEnum) return -1;   // not cached: the type may not be loaded yet
                id = Convert.ToInt32(Enum.Parse(t, name), CultureInfo.InvariantCulture);
            }
            catch { id = -1; }
            if (_sideIds.Count > 64) _sideIds.Clear();
            _sideIds[name] = id;
            return id;
        }

        internal static string SideLabel(int id)
        {
            if (id < 0) return "NOBODY";
            if (id == UnreadSide) return "PLAYERS";
            string n = null;
            try
            {
                Type t = RevivalPlugin.TypeByName("Fraction");
                if (t != null && t.IsEnum) n = Enum.GetName(t, Enum.ToObject(t, id));
            }
            catch { }
            return string.IsNullOrEmpty(n) ? "SIDE " + id : n.ToUpperInvariant();
        }

        /// <summary>Does this gun take the orders of <paramref name="side"/>?
        /// Ownership applies even when empty. A player at the sight must also
        /// belong to the commanding side.</summary>
        internal static bool Follows(FlakGunInfo g, int side)
        {
            if (g == null || side < 0) return false;
            // W AA3: a merc crew obeys its own side's radar; otherwise W AA1: the
            // gun's owner, and a player at it must be of that side too.
            if (!g.PlayerManned && g.MercActor >= 0) return side == g.CrewSide;
            return AirDefencePolicy.Follows(g.OwnerSide, side, g.PlayerManned,
                g.PlayerManned && g.ManActor >= 0 ? PlayerSide(g.ManActor) : -1);
        }

        /// <summary>Why a gun does not follow <paramref name="side"/> (scope text).</summary>
        internal static string Ignores(FlakGunInfo g, int side)
        {
            if (g.PlayerManned) return "IGNORES - " + SideLabel(g.ManActor >= 0 ? PlayerSide(g.ManActor) : -1) + " player at the sight";
            return "IGNORES - owned by " + SideLabel(g.OwnerSide);
        }

        static List<string> Following(int side)
        {
            List<string> ids = new List<string>();
            List<FlakGunInfo> guns = Flak.Guns();
            for (int i = 0; i < guns.Count; i++)
                if (Follows(guns[i], side)) ids.Add(guns[i].Id);
            return ids;
        }

        /// <summary>Master: the HQ operator is back - the airfield's side holds
        /// the radar again, every other side's orders are gone.</summary>
        static void GiveBack(int home)
        {
            Flak.ClearAirfieldOrders();
            _foreignOrders.Clear();
            ControlSide = home;
            Mode = 0;
            AssignedKind = -1;
            _dirty = true;
            Log("control back with " + SideLabel(home) + ": all guns weapons free.");
        }

        /// <summary>Master: a gun the airfield's crew mans again drops the
        /// orders another side left on it (they never obey them).</summary>
        static void DropForeignOrders(int home)
        {
            if (_foreignOrders.Count == 0) return;
            List<FlakGunInfo> guns = Flak.Guns();
            for (int i = 0; i < guns.Count; i++)
            {
                FlakGunInfo g = guns[i];
                if (!_foreignOrders.Contains(g.Id) || Follows(g, ControlSide)) continue;
                _foreignOrders.Remove(g.Id);
                Flak.ClearTarget(g.Id);
                Flak.WeaponsFree(g.Id);
                RadarScope.Note(g.Id + " manned by the " + SideLabel(home) + " crew - it ignores the radar.");
                Log(g.Id + ": manned by the airfield's crew again, foreign orders dropped.");
            }
        }

        /// <summary>The line in the sight of a gun that follows the radar.</summary>
        static string SightLine(string id)
        {
            if (!Built || !Working || ControlSide < 0) return null;
            FlakGunInfo g = Flak.Get(id);
            if (g == null || !Follows(g, ControlSide)) return null;
            return "RADAR HQ (" + SideLabel(ControlSide) + "): " + (Mode == 0 ? "WEAPONS FREE" : Mode == 1 ? "HOLD FIRE"
                : "ENGAGE ASSIGNED TRACK ONLY");
        }

        internal static bool PlayerNear(Vector3 p, float r)
        {
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && (players[i].transform.position - p).sqrMagnitude < r * r) return true;
            return false;
        }

        // ------------------------------------------------ inputs to the master

        /// <summary>A hit on the HQ (master; a client's arrives on the wire).
        /// part 0 = the radar (head, mast, shelter), 1 = the console.</summary>
        internal static void ApplyHit(int part, float amount, Vector3 boom, bool explosion)
        {
            if (!Crocodile.IsMaster()) { RadarNet.SendHit(part, amount, boom, explosion); return; }
            AirDefenceDamage.RadarHit(part, Mathf.Clamp(amount, 0f, 200f));
        }

        internal static void SetDamageHealth(float radar, float console)
        {
            if (radar == RadarHp && console == ConsoleHp) return;
            if (radar <= 0f && RadarHp > 0f) RadarScope.Note("RADAR DESTROYED - scope dark.");
            if (console <= 0f && ConsoleHp > 0f) RadarScope.Note("CONSOLE DESTROYED.");
            RadarHp = radar; ConsoleHp = console;
            _dirty = true; _directionSet = false;
        }

        /// <summary>A player's claim of the console (master).</summary>
        internal static void OnClaim(int actor, bool on)
        {
            if (!Crocodile.IsMaster()) return;
            if (on)
            {
                if (Crocodile.ActorDown(actor)) return;
                // B1: a recorded operator who is dead is no operator.
                if (OperatorActor >= 0 && OperatorActor != actor && Time.time < _opUntil
                    && !Crocodile.ActorDown(OperatorActor)) return;
                bool entering = OperatorActor != actor;
                if (entering) { _dirty = true; Log("actor " + actor + " is at the console."); }
                OperatorActor = actor;
                _opUntil = Time.time + 3f;
                if (entering && AirfieldOwnership.Take(actor))
                {
                    ControlSide = HomeSide();
                    Mode = 0;
                    AssignedKind = -1;
                    _dirty = true;
                }
            }
            else if (OperatorActor == actor)
            {
                OperatorActor = -1;
                _dirty = true;
                Log("actor " + actor + " left the console.");
            }
        }

        // W Tower 2: claims alone do not grant a paid command; check its lease
        // and physical holder again at the instant of purchase.
        internal static bool SupportLease(int actor)
        {
            return On && Built && ConsoleAlive && OperatorActor == actor && Time.time < _opUntil;
        }

        internal const int CmdEngage = 1, CmdFree = 2, CmdHold = 3, CmdClear = 4;

        /// <summary>A console command (master): only from the claimed operator
        /// and filtered by each gun's persistent faction.</summary>
        internal static void OnCommand(int actor, int cmd, int kind, Vector3 pos)
        {
            if (!Crocodile.IsMaster()) { RadarNet.SendCommand(cmd, kind, pos); return; }
            if (actor != OperatorActor) return;
            if (!ConsoleAlive) return;
            if (!Obeyed(actor))
            {
                Log("command " + cmd + " from actor " + actor + " refused: the HQ operator holds the tower for "
                    + SideLabel(HomeSide()) + ".");
                return;
            }
            int side = PlayerSide(actor), home = HomeSide();
            if (AirfieldOwnership.Take(actor))
            {
                home = HomeSide();
                Mode = 0;
                AssignedKind = -1;
            }
            List<string> ids = Following(side);
            if (side != ControlSide)
            {
                // Another side takes the radar: its guns start from weapons free.
                for (int i = 0; i < ids.Count; i++) { Flak.ClearTarget(ids[i]); Flak.WeaponsFree(ids[i]); }
                Log("the radar passes from " + SideLabel(ControlSide) + " to " + SideLabel(side) + ".");
                RadarScope.Note("The radar is in the hands of " + SideLabel(side) + ".");
                ControlSide = side;
                Mode = 0;
                AssignedKind = -1;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                if (side != home) { if (!_foreignOrders.Contains(ids[i])) _foreignOrders.Add(ids[i]); }
                else _foreignOrders.Remove(ids[i]);
            }
            switch (cmd)
            {
                case CmdEngage:
                    {
                        GepardGun.Contact c = RadarScope.Resolve(kind, pos, 120f);
                        if (c == null || c.Go == null) return;
                        for (int i = 0; i < ids.Count; i++) Flak.AssignTarget(ids[i], c.Go);
                        if (Mode == 1)
                        {
                            for (int i = 0; i < ids.Count; i++) Flak.AssignedOnly(ids[i]);
                            Mode = 2;
                        }
                        AssignedKind = c.Kind;
                        AssignedPos = c.Pos;
                        break;
                    }
                case CmdFree:
                    for (int i = 0; i < ids.Count; i++) Flak.WeaponsFree(ids[i]);
                    Mode = 0;
                    break;
                case CmdHold:
                    for (int i = 0; i < ids.Count; i++) { Flak.HoldFire(ids[i]); Flak.ClearTarget(ids[i]); }
                    AssignedKind = -1;
                    Mode = 1;
                    break;
                case CmdClear:
                    for (int i = 0; i < ids.Count; i++) Flak.ClearTarget(ids[i]);
                    if (Mode == 2)
                    {
                        for (int i = 0; i < ids.Count; i++) Flak.HoldFire(ids[i]);
                        Mode = 1;
                    }
                    AssignedKind = -1;
                    break;
            }
            Log("command " + cmd + " from " + SideLabel(side) + ": " + ids.Count + " gun(s) follow.");
            _dirty = true;
        }

        /// <summary>The master's state arrived (every other client).</summary>
        internal static void OnState(float radar, float console, int op, bool npc, int mode, bool siren, int tier,
                                     int kind, Vector3 assigned, int control)
        {
            if (Crocodile.IsMaster()) return;
            if (npc != NpcOperatorUp)
                RadarScope.Note(npc ? "HQ operator at the console - " + SideLabel(HomeSide()) + " hold the radar."
                    : "HQ OPERATOR DOWN - the console is free.");
            if (control != ControlSide && control >= 0 && ControlSide >= 0 && !npc)
                RadarScope.Note("The radar is in the hands of " + SideLabel(control) + ".");
            ControlSide = control;
            if (radar <= 0f && RadarHp > 0f) RadarScope.Note("RADAR DESTROYED - scope dark.");
            if (console <= 0f && ConsoleHp > 0f) RadarScope.Note("CONSOLE DESTROYED.");
            if (siren != SirenOn) RadarScope.Note(siren ? "AIR RAID SIREN on." : "Siren off - all clear.");
            RadarHp = Mathf.Clamp01(radar);
            ConsoleHp = Mathf.Clamp01(console);
            OperatorActor = op;
            NpcOperatorUp = npc;
            Mode = mode;
            SirenOn = siren;
            Tier = tier;
            AssignedKind = kind;
            AssignedPos = assigned;
        }
    }

    // =====================================================================
    // The shared clock

    /// <summary>PhotonNetwork.time made smooth (the battery drone's method,
    /// RevivalArtyBattery.AdvanceFlightClock): every client turns the antenna
    /// to the same bearing without a message.</summary>
    internal static class RadarClock
    {
        static MethodInfo _getter;
        static bool _looked, _set;
        static float _clock;

        internal static float Now { get { return _clock; } }

        static float Net()
        {
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null) _getter = AccessTools.PropertyGetter(photon, "time");
                }
                if (_getter != null)
                {
                    // W Perf1: compiled call, no boxed double per frame.
                    double d = FastCall.Double(_getter, null);
                    if (!double.IsNaN(d))
                    {
                        if (d > 0.0) return (float)(d % 100000.0);
                    }
                }
            }
            catch { }
            return Time.realtimeSinceStartup;
        }

        internal static void Advance()
        {
            float net = Net();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            if (!_set) { _set = true; _clock = net; return; }
            _clock += dt;
            float drift = net - _clock;
            if (drift > 1f || drift < -1f) { _clock = net; return; }
            _clock += drift * Mathf.Clamp01(dt * 2f);
        }
    }

    // =====================================================================
    // The models: the fallback radar and the console

    /// <summary>
    /// The radar from primitives when the c1 bundle has no kit radar (the
    /// measures of c1_radar.py, metres x 2.8, the same "Head" convention),
    /// and the console in the cab. Materials: the H1 hangar's kit materials
    /// by name where H1 is loaded (the H1 look), else the tower's, else flat
    /// colours.
    /// </summary>
    internal static class RadarModel
    {
        const float K = TowerRadar.K;
        internal static Material Steel, Sheet, Grey, Wood, Phosphor, DarkGlass, Black, Knob;

        internal static void Materials(Transform h1, Transform tower)
        {
            if (Phosphor == null)
            {
                Phosphor = Unlit("NDR_Radar_Phosphor", new Color(0.25f, 0.95f, 0.45f));
                DarkGlass = Mat("NDR_Radar_Glass", new Color(0.03f, 0.08f, 0.05f), 0f, 0.8f);
                Black = Mat("NDR_Radar_Burnt", new Color(0.02f, 0.02f, 0.02f), 0f, 0.1f);
                Knob = Mat("NDR_Radar_Knob", new Color(0.08f, 0.08f, 0.08f), 0.2f, 0.4f);
            }
            Steel = Pick(h1, "Metal_Ext_1") ?? Pick(tower, "grey") ?? Steel ?? Mat("NDR_Radar_Steel", new Color(0.33f, 0.36f, 0.3f), 0.3f, 0.3f);
            Sheet = Pick(h1, "garage_rusty") ?? Pick(h1, "Metal_Ext") ?? Sheet ?? Mat("NDR_Radar_Olive", new Color(0.29f, 0.32f, 0.21f), 0.15f, 0.25f);
            Grey = Pick(tower, "grey") ?? Pick(tower, "panel") ?? Grey ?? Mat("NDR_Radar_Grey", new Color(0.42f, 0.44f, 0.4f), 0.2f, 0.3f);
            Wood = Pick(tower, "wood") ?? Pick(h1, "Wood") ?? Wood ?? Mat("NDR_Radar_Wood", new Color(0.36f, 0.26f, 0.17f), 0f, 0.2f);
        }

        static Material Pick(Transform root, string part)
        {
            if (root == null) return null;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            string want = part.ToLowerInvariant();
            for (int i = 0; i < rs.Length; i++)
            {
                Material[] ms = rs[i].sharedMaterials;
                for (int j = 0; j < ms.Length; j++)
                    if (ms[j] != null && ms[j].name.ToLowerInvariant().Contains(want)) return ms[j];
            }
            return null;
        }

        static Material Mat(string name, Color c, float metal, float gloss)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            Material m = new Material(shader);
            m.name = name;
            m.color = c;
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        internal static Material Unlit(string name, Color c)
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null) return Mat(name, c, 0f, 0.9f);
            Material m = new Material(shader);
            m.name = name;
            m.color = c;
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        // ------------------------------------------------------ primitives

        /// <summary>Primitives gathered per material under one parent, then
        /// merged into one mesh per material (a handful of draw calls).</summary>
        sealed class Batch
        {
            public readonly Dictionary<Material, List<CombineInstance>> Parts = new Dictionary<Material, List<CombineInstance>>();
            public readonly Transform Parent;
            public Batch(Transform parent) { Parent = parent; }
        }

        static readonly Dictionary<PrimitiveType, Mesh> _meshes = new Dictionary<PrimitiveType, Mesh>();

        static Mesh PrimMesh(PrimitiveType t)
        {
            Mesh m;
            if (_meshes.TryGetValue(t, out m) && m != null) return m;
            GameObject go = GameObject.CreatePrimitive(t);
            m = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            _meshes[t] = m;
            return m;
        }

        static void Add(Batch b, PrimitiveType t, Vector3 centreM, Vector3 sizeM, Quaternion rot, Material m)
        {
            if (m == null) return;
            List<CombineInstance> list;
            if (!b.Parts.TryGetValue(m, out list)) { list = new List<CombineInstance>(); b.Parts[m] = list; }
            CombineInstance ci = new CombineInstance();
            ci.mesh = PrimMesh(t);
            ci.transform = Matrix4x4.TRS(centreM * K, rot, sizeM * K);
            list.Add(ci);
        }

        static void Box(Batch b, Vector3 c, Vector3 s, Material m) { Add(b, PrimitiveType.Cube, c, s, Quaternion.identity, m); }

        static void BoxR(Batch b, Vector3 c, Vector3 s, Quaternion r, Material m) { Add(b, PrimitiveType.Cube, c, s, r, m); }

        /// <summary>A beam from a to b (metres), square section w.</summary>
        static void Beam(Batch b, Vector3 a, Vector3 e, float w, Material m)
        {
            Vector3 d = e - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Add(b, PrimitiveType.Cube, (a + e) * 0.5f, new Vector3(w, w, d.magnitude), Quaternion.LookRotation(d.normalized), m);
        }

        /// <summary>An upright cylinder: base centre, radius, height (metres).</summary>
        static void Post(Batch b, Vector3 baseM, float r, float h, Material m)
        {
            Add(b, PrimitiveType.Cylinder, baseM + Vector3.up * h * 0.5f, new Vector3(r * 2f, h * 0.5f, r * 2f), Quaternion.identity, m);
        }

        static void Wheel(Batch b, Vector3 c, float r, float w, Material m)
        {
            Add(b, PrimitiveType.Cylinder, c, new Vector3(r * 2f, w * 0.5f, r * 2f), Quaternion.Euler(90f, 0f, 0f), m);
        }

        static List<Renderer> Finish(Batch b, string name, bool shadows)
        {
            List<Renderer> made = new List<Renderer>();
            foreach (KeyValuePair<Material, List<CombineInstance>> kv in b.Parts)
            {
                Mesh mesh = new Mesh();
                mesh.name = "NDR " + name;
                mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                mesh.RecalculateBounds();
                GameObject go = new GameObject(name);
                go.transform.SetParent(b.Parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = kv.Key;
                r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                made.Add(r);
            }
            b.Parts.Clear();
            return made;
        }

        // ------------------------------------------------------- the radar

        /// <summary>The radar of c1_radar.py from primitives at the mast spot.</summary>
        internal static Transform Build(Scene scene)
        {
            try
            {
                GameObject root = new GameObject("NDR " + TowerRadar.RadarName);
                SceneManager.MoveGameObjectToScene(root, scene);
                Vector3 at = new Vector3(TowerRadar.MastSpot.x, TowerRadar.TowerBase.y, TowerRadar.MastSpot.y);
                float y;
                if (EastWorld.TerrainHeight(at, out y)) at.y = y;
                root.transform.position = at;
                root.transform.rotation = Quaternion.identity;

                Batch st = new Batch(root.transform);
                // trailer: deck, members, drawbar, jacks, wheels
                Box(st, new Vector3(0f, 1.125f, 0f), new Vector3(5.6f, 0.15f, 2.3f), Steel);
                Box(st, new Vector3(0f, 0.93f, -0.9f), new Vector3(5.4f, 0.24f, 0.14f), Steel);
                Box(st, new Vector3(0f, 0.93f, 0.9f), new Vector3(5.4f, 0.24f, 0.14f), Steel);
                Beam(st, new Vector3(2.7f, 0.95f, -0.7f), new Vector3(4.4f, 0.75f, 0f), 0.1f, Steel);
                Beam(st, new Vector3(2.7f, 0.95f, 0.7f), new Vector3(4.4f, 0.75f, 0f), 0.1f, Steel);
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 a = new Vector3(sx * 2.5f, 1.0f, sz * 1.05f), c = new Vector3(sx * 2.5f, 1.0f, sz * 2.05f);
                        Beam(st, a, c, 0.12f, Steel);
                        Box(st, new Vector3(c.x, 0.5f, c.z), new Vector3(0.1f, 0.95f, 0.1f), Steel);
                        Box(st, new Vector3(c.x, 0.04f, c.z), new Vector3(0.45f, 0.08f, 0.45f), Wood);
                    }
                for (int ax = -1; ax <= 1; ax += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        Wheel(st, new Vector3(ax * 1.5f, 0.48f, sz * 0.95f), 0.48f, 0.3f, Steel);
                // mast
                Box(st, new Vector3(0f, 1.3f, 0f), new Vector3(0.9f, 0.2f, 0.9f), Steel);
                Post(st, new Vector3(0f, 1.4f, 0f), 0.2f, 2.5f, Steel);
                Post(st, new Vector3(0f, 3.88f, 0f), 0.15f, 2.67f, Steel);
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        Beam(st, new Vector3(sx * 1.4f, 1.2f, sz * 0.95f), new Vector3(sx * 0.2f, 3.4f, sz * 0.2f), 0.06f, Steel);
                // the KUNG shelter and the generator
                Box(st, new Vector3(-7.2f, 0.15f, -0.9f), new Vector3(5.3f, 0.3f, 0.16f), Steel);
                Box(st, new Vector3(-7.2f, 0.15f, 0.9f), new Vector3(5.3f, 0.3f, 0.16f), Steel);
                Box(st, new Vector3(-7.2f, 1.45f, 0f), new Vector3(5.0f, 2.3f, 2.4f), Sheet);
                Box(st, new Vector3(-7.2f, 2.64f, 0f), new Vector3(5.1f, 0.08f, 2.5f), Steel);
                Box(st, new Vector3(-5.8f, 1.25f, -1.22f), new Vector3(0.8f, 1.8f, 0.04f), Grey);
                Box(st, new Vector3(-7.2f, 0.55f, -2.4f), new Vector3(1.3f, 0.9f, 0.8f), Sheet);
                Finish(st, "Static", true);
                BoxCollider bc = root.AddComponent<BoxCollider>();
                bc.center = new Vector3(-7.2f, 1.45f, 0f) * K;
                bc.size = new Vector3(5.0f, 2.3f, 2.4f) * K;
                bc = root.AddComponent<BoxCollider>();
                bc.center = new Vector3(0f, 1.125f, 0f) * K;
                bc.size = new Vector3(5.6f, 0.15f, 2.3f) * K;

                // the head: its transform at the frame origin, turning about y
                GameObject head = new GameObject("Head");
                head.transform.SetParent(root.transform, false);
                Batch hd = new Batch(head.transform);
                Add(hd, PrimitiveType.Cylinder, new Vector3(0f, 6.72f, 0f), new Vector3(0.9f, 0.175f, 0.9f), Quaternion.identity, Steel);
                Box(hd, new Vector3(0f, 7.95f, -0.2f), new Vector3(0.3f, 2.5f, 0.3f), Steel);
                Box(hd, new Vector3(0f, 7.2f, 0f), new Vector3(8.8f, 0.14f, 0.14f), Steel);
                Box(hd, new Vector3(0f, 8.8f, 0f), new Vector3(8.8f, 0.14f, 0.14f), Steel);
                for (int i = 0; i < 4; i++)
                    Box(hd, new Vector3(-4.4f + i * 2.9333f, 8f, 0f), new Vector3(0.1f, 1.74f, 0.1f), Steel);
                Beam(hd, new Vector3(0f, 7.1f, -0.2f), new Vector3(-3.5f, 7.2f, 0f), 0.08f, Steel);
                Beam(hd, new Vector3(0f, 7.1f, -0.2f), new Vector3(3.5f, 7.2f, 0f), 0.08f, Steel);
                Box(hd, new Vector3(0f, 8f, -0.55f), new Vector3(0.6f, 0.8f, 0.4f), Sheet);
                for (int row = 0; row < 2; row++)
                {
                    float yy = row == 0 ? 7.2f : 8.8f;
                    for (int i = 0; i < 8; i++)
                    {
                        float x = (-3.5f + i) * 1.1f;
                        Box(hd, new Vector3(x, yy, 1.7f), new Vector3(0.05f, 0.05f, 3.4f), Steel);
                        Box(hd, new Vector3(x, yy, -0.12f), new Vector3(0.92f, 0.035f, 0.035f), Steel);
                    }
                }
                Finish(hd, "Frame", true);
                for (int row = 0; row < 2; row++)
                {
                    float yy = row == 0 ? 7.2f : 8.8f;
                    for (int i = 0; i < 8; i++)
                    {
                        float x = (-3.5f + i) * 1.1f;
                        for (int k = 0; k < 6; k++)
                            Box(hd, new Vector3(x, yy, 0.35f + k * 0.6f), new Vector3(0.8f * (0.95f - 0.03f * k), 0.025f, 0.025f), Steel);
                    }
                }
                TowerRadar.Elements.Clear();
                List<Renderer> el = Finish(hd, "Elements", false);
                for (int i = 0; i < el.Count; i++) TowerRadar.Elements.Add(el[i]);
                TowerRadar.Log("radar built from primitives at " + at + " (no kit radar in the bundle).");
                return root.transform;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TowerRadar: building the radar failed: " + ex);
                return null;
            }
        }

        // ----------------------------------------------------- the console

        /// <summary>The radar console against the cab's south wall: cabinet,
        /// sloped desk, the round PPI screen facing north, knobs, a telephone,
        /// the operator's chair. Frame: +z = the screen's facing (north).</summary>
        internal static Transform Console(Scene scene)
        {
            try
            {
                GameObject root = new GameObject("NDR radar console");
                SceneManager.MoveGameObjectToScene(root, scene);
                Vector3 p = TowerRadar.TowerPoint(TowerRadar.ConsoleLocalM);
                p.y = TowerRadar.CabFloorY;
                root.transform.position = p;
                root.transform.rotation = Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f);

                Batch b = new Batch(root.transform);
                Box(b, new Vector3(0f, 0.45f, 0f), new Vector3(1.3f, 0.9f, 0.7f), Grey);                 // desk body
                BoxR(b, new Vector3(0f, 0.93f, 0.05f), new Vector3(1.3f, 0.06f, 0.62f), Quaternion.Euler(-12f, 0f, 0f), Grey);
                Box(b, new Vector3(0f, 1.25f, -0.22f), new Vector3(1.3f, 0.7f, 0.26f), Grey);            // the scope tower
                Box(b, new Vector3(0f, 1.62f, -0.22f), new Vector3(1.34f, 0.04f, 0.3f), Steel);
                for (int i = 0; i < 5; i++)
                    Add(b, PrimitiveType.Cylinder, new Vector3(-0.5f + i * 0.25f, 0.99f, 0.18f), new Vector3(0.05f, 0.015f, 0.05f),
                        Quaternion.Euler(-12f, 0f, 0f), Knob);
                Box(b, new Vector3(0.52f, 1.0f, -0.05f), new Vector3(0.2f, 0.08f, 0.14f), Knob);          // telephone
                // the chair north of the desk, facing it
                Box(b, new Vector3(0f, 0.45f, 0.85f), new Vector3(0.44f, 0.05f, 0.44f), Wood);
                Box(b, new Vector3(0f, 0.72f, 1.05f), new Vector3(0.44f, 0.5f, 0.04f), Wood);
                Post(b, new Vector3(0f, 0f, 0.85f), 0.03f, 0.43f, Steel);
                Box(b, new Vector3(0f, 0.02f, 0.85f), new Vector3(0.5f, 0.04f, 0.08f), Steel);
                Box(b, new Vector3(0f, 0.02f, 0.85f), new Vector3(0.08f, 0.04f, 0.5f), Steel);
                Finish(b, "Console", true);

                // the round screen, facing north, in its own renderer
                GameObject scr = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                scr.name = "Screen";
                Collider sc = scr.GetComponent<Collider>();
                if (sc != null) UnityEngine.Object.DestroyImmediate(sc);
                scr.transform.SetParent(root.transform, false);
                scr.transform.localPosition = new Vector3(0f, 1.27f, -0.085f) * K;
                scr.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                scr.transform.localScale = new Vector3(0.46f, 0.005f, 0.46f) * K;
                TowerRadar.Screen = scr.GetComponent<Renderer>();
                TowerRadar.Screen.sharedMaterial = Phosphor;
                TowerRadar.Screen.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                BoxCollider bc = root.AddComponent<BoxCollider>();
                bc.center = new Vector3(0f, 0.8f, -0.05f) * K;
                bc.size = new Vector3(1.3f, 1.6f, 0.7f) * K;
                return root.transform;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TowerRadar: building the console failed: " + ex);
                return null;
            }
        }
    }

    // =====================================================================
    // Damage

    /// <summary>
    /// The HQ as a target: the local player's rounds (a ray against the
    /// boxes of the head, mast, shelter and console) and the game's
    /// explosions. The master keeps the hit points (TowerRadar.ApplyHit).
    /// </summary>
    internal static class RadarDamage
    {
        const float K = TowerRadar.K;
        static FieldInfo _camera;
        static bool _looked;

        /// <summary>A box in a frame: centre and half size, world units.</summary>
        struct Part
        {
            public Transform Frame;
            public Vector3 Centre, Half;
            public int Id;                // 0 radar, 1 console
        }

        static readonly List<Part> _parts = new List<Part>();

        static void Parts()
        {
            _parts.Clear();
            Transform head = TowerRadar.Head != null ? TowerRadar.Head : TowerRadar.RadarRoot;
            if (head != null) _parts.Add(P(head, new Vector3(0f, 8f, 1.4f), new Vector3(4.5f, 1.35f, 2.1f), 0));
            if (TowerRadar.RadarRoot != null)
            {
                _parts.Add(P(TowerRadar.RadarRoot, new Vector3(0f, 3.9f, 0f), new Vector3(0.35f, 2.7f, 0.35f), 0));
                _parts.Add(P(TowerRadar.RadarRoot, new Vector3(-7.2f, 1.45f, 0f), new Vector3(2.5f, 1.15f, 1.2f), 0));
            }
            if (TowerRadar.ConsoleRoot != null)
                _parts.Add(P(TowerRadar.ConsoleRoot, new Vector3(0f, 0.8f, -0.05f), new Vector3(0.65f, 0.8f, 0.35f), 1));
        }

        static Part P(Transform f, Vector3 cM, Vector3 hM, int id)
        {
            Part p = new Part();
            p.Frame = f;
            p.Centre = cM * K;
            p.Half = hM * K;
            p.Id = id;
            return p;
        }

        internal static void Shot(object weapon)
        {
            if (!_looked)
            {
                _looked = true;
                _camera = AccessTools.Field(weapon.GetType(), "MainCamera");
            }
            if (_camera == null) return;
            Transform cam = _camera.GetValue(weapon) as Transform;
            if (cam == null) return;
            Vector3 o = cam.position, d = cam.forward;
            if ((o - TowerRadar.RadarPos).sqrMagnitude > 1500f * 1500f
                && (o - TowerRadar.ConsoleRoot.position).sqrMagnitude > 1500f * 1500f) return;
            Parts();
            float best = float.MaxValue;
            int id = -1;
            for (int i = 0; i < _parts.Count; i++)
            {
                float t;
                if (Ray(_parts[i], o, d, out t) && t < best) { best = t; id = _parts[i].Id; }
            }
            if (id < 0 || best > 1400f) return;
            if (Blocked(o, d, best)) return;
            TowerRadar.ApplyHit(id, 1f, Vector3.zero, false);
        }

        /// <summary>Slab test of a ray against a part's box, in its frame.</summary>
        static bool Ray(Part p, Vector3 o, Vector3 d, out float t)
        {
            t = 0f;
            if (p.Frame == null) return false;
            Vector3 lo = p.Frame.InverseTransformPoint(o) - p.Centre;
            Vector3 ld = p.Frame.InverseTransformDirection(d);
            float t0 = 0f, t1 = 5000f;
            for (int a = 0; a < 3; a++)
            {
                float oa = lo[a], da = ld[a], h = p.Half[a];
                if (Mathf.Abs(da) < 1e-6f)
                {
                    if (oa < -h || oa > h) return false;
                    continue;
                }
                float ta = (-h - oa) / da, tb = (h - oa) / da;
                if (ta > tb) { float s = ta; ta = tb; tb = s; }
                t0 = Mathf.Max(t0, ta);
                t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return false;
            }
            t = t0;
            return true;
        }

        /// <summary>Something solid between the muzzle and the part: walls,
        /// terrain, vehicles. The HQ's own colliders, window panes and the
        /// shooter do not count.</summary>
        static bool Blocked(Vector3 o, Vector3 d, float dist)
        {
            Vector3 origin = o + d * 0.6f;
            float rest = dist - 0.8f;
            GameObject me = MapTools.LocalPlayer();
            for (int i = 0; i < 6 && rest > 0.3f; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, d, out hit, rest, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return false;
                Transform t = hit.transform;
                string n = t.name.ToLowerInvariant();
                bool pass = (TowerRadar.RadarRoot != null && t.IsChildOf(TowerRadar.RadarRoot))
                    || (TowerRadar.ConsoleRoot != null && t.IsChildOf(TowerRadar.ConsoleRoot))
                    || (me != null && t.IsChildOf(me.transform))
                    || n.Contains("window") || n.Contains("glass") || n.Contains("pane");
                if (!pass) return true;
                rest -= hit.distance + 0.2f;
                origin = hit.point + d * 0.2f;
            }
            return false;
        }

        internal static void Blast(Vector3 at, float radius, float peak)
        {
            Parts();
            float radar = 0f, console = 0f;
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                if (p.Frame == null) continue;
                Vector3 l = p.Frame.InverseTransformPoint(at) - p.Centre;
                Vector3 q = new Vector3(Mathf.Clamp(l.x, -p.Half.x, p.Half.x), Mathf.Clamp(l.y, -p.Half.y, p.Half.y),
                                        Mathf.Clamp(l.z, -p.Half.z, p.Half.z));
                float dist = (l - q).magnitude;
                float reach = radius + 1.5f;
                if (dist >= reach) continue;
                float amount = TowerRadar.I(TowerRadar.CfgExplosionHits, 40) * peak * (1f - dist / reach);
                if (p.Id == 0) radar = Mathf.Max(radar, amount); else console = Mathf.Max(console, amount);
            }
            if (radar > 0f) TowerRadar.ApplyHit(0, radar, at, true);
            if (console > 0f) TowerRadar.ApplyHit(1, console, at, true);
        }

        /// <summary>The explosion's radius (FireHook.Radius: the field may be
        /// an Obscured type).</summary>
        internal static float Radius(Component c)
        {
            try
            {
                FieldInfo f = AccessTools.Field(c.GetType(), "ExplodeDamageRadius");
                if (f == null) return 6f;
                object v = f.GetValue(c);
                if (v == null) return 6f;
                if (v is float) return (float)v;
                MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float))
                        return (float)ms[i].Invoke(null, new object[] { v });
                return 6f;
            }
            catch { return 6f; }
        }
    }

    // =====================================================================
    // The NPC operator

    /// <summary>
    /// The HQ's own operator: one man of the airfield's faction, key
    /// "radar/c1/op" (THE SLASH IS LOAD BEARING, see FlakCrew), spawned by
    /// the master on the chair once no player is within 60 u, replaced after
    /// OperatorRespawnMinutes. Every client holds him on the chair.
    /// </summary>
    internal static class RadarOperator
    {
        internal const string Key = "radar/c1/op";
        static Component _man;
        static GameObject _squad;
        static bool _asked;
        static int _tries;
        static float _nextResolve, _nextTry, _deadSince = -1f, _builtAt = -1f;

        internal static bool Alive { get { return Flak.Up(_man); } }

        internal static void Tick(bool master)
        {
            if (_builtAt < 0f) _builtAt = Time.time;
            if (Time.time >= _nextResolve)
            {
                _nextResolve = Time.time + 1f;
                Resolve();
            }
            if (master) Spawn();
        }

        static void Resolve()
        {
            Type npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (npcType == null) return;
            UnityEngine.Object[] actors = NpcScan.All();
            Component dead = null;
            for (int i = 0; i < actors.Length; i++)
            {
                Component ai = actors[i] as Component;
                if (ai == null || Crew.GroundKey(ai) != Key) continue;
                if (Flak.Up(ai)) { _man = ai; return; }
                dead = ai;
            }
            if (dead != null || !Flak.Up(_man)) _man = dead != null ? dead : _man;
        }

        static void Spawn()
        {
            if (MercAA.Operator != null) return;
            if (!TowerRadar.B(TowerRadar.CfgNpcOperator) || TowerRadar.ConsoleRoot == null) return;
            if (AirfieldOwnership.CrewSide == null) return;
            if (Alive) { _deadSince = -1f; return; }
            if (Time.time < _nextTry || !TowerRadar.ConsoleAlive) return;
            Vector3 seat = Seat();
            if (_man != null || _asked)
            {
                if (_deadSince < 0f) _deadSince = Time.time;
                float wait = Mathf.Clamp(TowerRadar.F(TowerRadar.CfgOpRespawn, 35f), 1f, 600f) * 60f;
                if (Time.time - _deadSince < wait) return;
                if (TowerRadar.PlayerNear(seat, 150f)) { _nextTry = Time.time + 10f; return; }
            }
            else
            {
                if (Time.time - _builtAt < 10f) return;
                if (TowerRadar.PlayerNear(seat, 60f)) { _nextTry = Time.time + 5f; return; }
            }
            _nextTry = Time.time + 8f;
            _tries++;
            try
            {
                string side = AirfieldOwnership.CrewSide;
                _squad = Crew.DropGroundSquad(seat, new Vector3[] { seat }, side, null, Key);
                _asked = _squad != null || _tries >= 5;
                _deadSince = -1f;
                Array a = Crew.Men(_squad);
                if (a != null && a.Length > 0) _man = a.GetValue(0) as Component;
                TowerRadar.Log("NPC operator (" + side + ") spawned at the console: " + (_man != null) + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TowerRadar: operator spawn failed: " + ex.Message);
            }
        }

        /// <summary>The chair's floor point (console frame: 0.85 m north of
        /// the desk).</summary>
        static Vector3 Seat()
        {
            Transform c = TowerRadar.ConsoleRoot;
            return c.TransformPoint(new Vector3(0f, 0.02f, 0.85f) * TowerRadar.K);
        }

        /// <summary>Every client, late: the man on the chair facing the screen.</summary>
        internal static void Hold()
        {
            if (!Alive || TowerRadar.ConsoleRoot == null) return;
            Component ai = _man;
            bool sit = TowerRadar.B(TowerRadar.CfgSeated);
            Vector3 at = Seat();
            if (sit)
            {
                float drop = Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value;
                at += Vector3.up * (0.45f * TowerRadar.K + 0.05f - drop);
            }
            Vector3 fwd = -TowerRadar.ConsoleRoot.forward;
            fwd.y = 0f;
            Quaternion rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            Transform tr = ai.transform;
            GepardCrew.Parken(ai);
            float away = (tr.position - at).sqrMagnitude;
            if (away > 64f)
            {
                NavMeshAgent agent = GepardCrew.Agent(ai);
                try
                {
                    if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                    {
                        agent.ResetPath();
                        agent.Warp(at);
                    }
                }
                catch { }
            }
            if (away > 0.0025f) tr.position = at;
            tr.rotation = rot;
            GepardCrew.Ruhig(ai);
            if (sit) TechnicalCrew.Sitzen(ai, 0);
            TechnicalCrew.Unbewaffnet(ai);     // he works the console, no rifle
        }
    }

    // =====================================================================
    // The scope

    /// <summary>
    /// The radar picture (every client): contacts from the flak's collector,
    /// blips painted by the sweep, the contact log; the radar view at the
    /// console with its PPI scope, the virtual cursor and the fire-control
    /// buttons.
    /// </summary>
    internal static class RadarScope
    {
        const float K = TowerRadar.K;

        sealed class Blip
        {
            public int Id;
            public GameObject Go;
            public int Kind;
            public float Radius;
            public Vector3 Pos, Vel;          // as painted
            public float PaintedAt = -100f, Height;
            public int Iff;                   // 1 friend, -1 foe, 0 unknown
            public bool Lost, Logged;
        }

        static readonly List<GepardGun.Contact> _air = new List<GepardGun.Contact>();
        /// <summary>The radar's contacts (every client, 4 Hz, followed every
        /// frame): W-Tower1's air picture reads them.</summary>
        internal static List<GepardGun.Contact> Air { get { return _air; } }
        static readonly List<Blip> _blips = new List<Blip>();
        static readonly List<string> _log = new List<string>();
        static readonly Dictionary<string, FlakState> _gunState = new Dictionary<string, FlakState>();
        static int _nextId = 1;
        static float _nextCollect, _lastSweep = -1f;
        static Blip _selected;
        internal static bool InView;
        static bool _near;
        static string _nearWhy;
        static Vector2 _cursor;
        static float _nextClaim, _enteredAt;
        static int _enteredFrame = -1;
        static KeyCode _key = KeyCode.None;
        static bool _keyParsed;
        static string _hint;
        static float _hintUntil;

        static KeyCode Key()
        {
            if (!_keyParsed)
            {
                _keyParsed = true;
                _key = Gepard.ParseKey(RevivalPlugin.CfgTurretKey == null ? "G" : RevivalPlugin.CfgTurretKey.Value, KeyCode.G);
            }
            return _key;
        }

        internal static void Note(string s)
        {
            _log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + s);
            while (_log.Count > 14) _log.RemoveAt(0);
        }

        // ----------------------------------------------------------- tick

        internal static void Tick()
        {
            float now = Time.time;
            Vector3 eye = TowerRadar.RadarPos;
            if (now >= _nextCollect)
            {
                _nextCollect = now + 0.25f;
                FlakFire.Collect(_air, eye, TowerRadar.ScopeRangeU);
                RadarShadow.Scan(_air, eye);
                Guns();
                if (InView) Rows();   // W-UI4: the console's gun and track lines, only at the console
            }
            FlakFire.FollowAll(_air, Time.deltaTime, 5f);
            Sweep(eye);
            Operate();
        }

        /// <summary>The sweep paints what it passes: a working radar only,
        /// airborne contacts only (below MinContactHeight is ground clutter).</summary>
        static void Sweep(Vector3 eye)
        {
            float a1 = TowerRadar.AntennaAngle;
            float a0 = _lastSweep < 0f ? a1 : _lastSweep;
            _lastSweep = a1;
            float span = Mathf.Repeat(a1 - a0, 360f);
            bool working = TowerRadar.Working;
            float range = TowerRadar.ScopeRangeU;
            string mine = MySide();
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null) continue;
                Blip b = Find(c.Go);
                if (!working || !RadarShadow.Visible(c.Go)) continue;
                Vector3 d = c.Pos - eye;
                d.y = 0f;
                if (d.magnitude > range) continue;
                float brg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                if (Mathf.Repeat(brg - a0, 360f) > span && span < 359f) continue;
                float h = RadarShadow.Height(c.Go);
                if (h < Mathf.Max(0f, TowerRadar.F(TowerRadar.CfgMinHeight, 3f)) * K) continue;
                if (b == null)
                {
                    b = new Blip();
                    b.Id = _nextId++;
                    if (_nextId > 99) _nextId = 1;
                    b.Go = c.Go;
                    _blips.Add(b);
                }
                b.Kind = c.Kind;
                b.Radius = c.Radius;
                b.Pos = c.Pos;
                b.Vel = c.Vel;
                b.Height = h;
                b.PaintedAt = Time.time;
                b.Lost = false;
                b.Iff = Iff(c, mine);
                if (!b.Logged)
                {
                    b.Logged = true;
                    Note("TRACK " + b.Id.ToString("00", CultureInfo.InvariantCulture) + " NEW  " + Guess(b) + "  brg "
                        + Mathf.Repeat(brg, 360f).ToString("000", CultureInfo.InvariantCulture) + "  "
                        + (d.magnitude / K / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km  "
                        + IffName(b.Iff));
                }
            }
            // a track not painted for two sweeps is lost
            float lostAfter = TowerRadar.SweepSeconds * 2.2f;
            for (int i = _blips.Count - 1; i >= 0; i--)
            {
                Blip b = _blips[i];
                if (Time.time - b.PaintedAt > lostAfter && working)
                {
                    if (b.Logged) Note("TRACK " + b.Id.ToString("00", CultureInfo.InvariantCulture) + " LOST");
                    if (_selected == b) _selected = null;
                    _blips.RemoveAt(i);
                }
            }
        }

        static Blip Find(GameObject go)
        {
            for (int i = 0; i < _blips.Count; i++) if (_blips[i].Go == go) return _blips[i];
            return null;
        }

        internal static float Height(Vector3 p)
        {
            float g;
            return EastWorld.TerrainHeight(p, out g) ? p.y - g : 999f;
        }

        /// <summary>Guns opening fire and falling silent go to the log.</summary>
        static void Guns()
        {
            List<FlakGunInfo> guns = Flak.Guns();
            _guns.Clear();
            _guns.AddRange(guns);   // W-UI4: the scope and the console panel draw this 4 Hz snapshot
            for (int i = 0; i < guns.Count; i++)
            {
                FlakGunInfo g = guns[i];
                FlakState had;
                bool known = _gunState.TryGetValue(g.Id, out had);
                if (known && had != g.State)
                {
                    if (g.State == FlakState.Firing) Note(g.Id + " ENGAGING");
                    else if (g.State == FlakState.NoCrew) Note(g.Id + " NO CREW");
                    else if (g.State == FlakState.Destroyed) Note(g.Id + " DESTROYED - toolkit repair needed");
                    else if (g.State == FlakState.Reloading) Note(g.Id + " reloading");
                }
                _gunState[g.Id] = g.State;
            }
        }

        // ---------------------------------------------------- friend / foe

        static string MySide()
        {
            GameObject me = MapTools.LocalPlayer();
            return me == null ? null : Fraktion.Spielerseite(me);
        }

        /// <summary>Who is aboard, as the viewer sees it: a player of his own
        /// faction = friend, of another = foe, nobody = unknown. A drone: its
        /// pilot.</summary>
        static int Iff(GepardGun.Contact c, string mine)
        {
            string pilot = AirPilot.Faction(c);
            return pilot == null ? 0 : mine != null && pilot == mine ? 1 : AirDefencePolicy.Hostile(mine, pilot) ? -1 : 0;
        }

        /// <summary>Master: a hostile aircraft (a player of a faction hostile
        /// to the airfield's aboard) airborne within <paramref name="r"/> of
        /// the radar.</summary>
        internal static bool HostileWithin(float r)
        {
            Vector3 eye = TowerRadar.RadarPos;
            string own = AirfieldOwnership.FactionName;
            float min = Mathf.Max(0f, TowerRadar.F(TowerRadar.CfgMinHeight, 3f)) * K;
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null || !RadarShadow.Visible(c.Go) || (c.Pos - eye).sqrMagnitude > r * r || RadarShadow.Height(c.Go) < min) continue;
                if (AirDefencePolicy.Hostile(own, AirPilot.Faction(c))) return true;
            }
            return false;
        }

        /// <summary>Master: the contact of this kind nearest a point.</summary>
        internal static GepardGun.Contact Resolve(int kind, Vector3 pos, float within)
        {
            GepardGun.Contact best = null;
            float bestD = within * within;
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null || !RadarShadow.Visible(c.Go) || (kind >= 0 && c.Kind != kind)) continue;
                float d = (c.Pos - pos).sqrMagnitude;
                if (d < bestD) { best = c; bestD = d; }
            }
            return best;
        }

        static string Guess(Blip b)
        {
            float kmh = new Vector3(b.Vel.x, 0f, b.Vel.z).magnitude / K * 3.6f;
            switch (b.Kind)
            {
                case 0: return kmh < 20f ? "HELO (hover)" : "HELO, prob. Mi-8";
                case 6: return kmh < 60f ? "SLOW FIXED-WING?" : "FIXED-WING, prob. An-2";
                case 2: return "SMALL - DRONE (FPV?)";
                case 3: return "SMALL - DRONE (recon?)";
                default: return "UNKNOWN";
            }
        }

        static string IffName(int iff) { return iff > 0 ? "FRIEND" : iff < 0 ? "FOE" : "UNKNOWN"; }

        // --------------------------------------------------------- the view

        /// <summary>The key at the console, the claim, the cursor, the buttons.</summary>
        static void Operate()
        {
            GameObject me = MapTools.LocalPlayer();
            if (InView)
            {
                if (me == null) { Leave("no local player"); return; }
                if (Vector3.Distance(me.transform.position, TowerRadar.ConsoleRoot.position) > 12f) { Leave("moved away"); return; }
                if (!TowerRadar.ConsoleAlive) { Leave("the console is destroyed"); return; }
                int mine = Crocodile.LocalActor();
                if (TowerRadar.OperatorActor >= 0 && TowerRadar.OperatorActor != mine && Time.time - _enteredAt > 3f
                    && !Crocodile.ActorDown(TowerRadar.OperatorActor))
                {
                    Leave("another player has the console");
                    return;
                }
                if (Time.frameCount != _enteredFrame && (Input.GetKeyDown(Key()) || Input.GetKeyDown(KeyCode.Escape)))
                {
                    Leave("left the console");
                    return;
                }
                if (Time.time >= _nextClaim)
                {
                    _nextClaim = Time.time + 1f;
                    if (!Crocodile.PlayerUp(me)) { Leave("the local player is down"); return; }
                    Claim(true);
                }
                if (TowerSupport.Selecting) return;
                float sens = Mathf.Max(1f, TowerRadar.F(TowerRadar.CfgCursor, 14f));
                _cursor.x = Mathf.Clamp(_cursor.x + Input.GetAxis("Mouse X") * sens, 0f, UnityEngine.Screen.width);
                _cursor.y = Mathf.Clamp(_cursor.y - Input.GetAxis("Mouse Y") * sens, 0f, UnityEngine.Screen.height);
                if (Input.GetMouseButtonDown(0)) Click();
                return;
            }
            _near = false;
            _nearWhy = null;
            if (me == null || TowerRadar.ConsoleRoot == null) return;
            float reach = Mathf.Max(1f, TowerRadar.F(TowerRadar.CfgReach, 2.5f)) * K;
            Vector3 front = TowerRadar.ConsoleRoot.TransformPoint(new Vector3(0f, 0f, 0.8f) * K);
            Vector3 dd = me.transform.position - front;
            if (Mathf.Abs(dd.y) > 2.5f * K) return;
            dd.y = 0f;
            if (dd.magnitude > reach) return;
            _near = true;
            if (!TowerRadar.ConsoleAlive) _nearWhy = "The radar console is destroyed.";
            else if (TowerRadar.OperatorActor >= 0 && TowerRadar.OperatorActor != Crocodile.LocalActor()
                     && !Crocodile.ActorDown(TowerRadar.OperatorActor))
                _nearWhy = "Another player is at the radar console.";
            if (_nearWhy == null && Input.GetKeyDown(Key())) Enter();
        }

        static void Enter()
        {
            if (Flak._manned != null) return;
            InView = true;
            _enteredAt = Time.time;
            _enteredFrame = Time.frameCount;
            _cursor = new Vector2(UnityEngine.Screen.width * 0.35f, UnityEngine.Screen.height * 0.5f);
            _nextClaim = 0f;
            _nextCollect = 0f;   // the console's lines at once
            TowerRadar.Log("the local player is at the console.");
        }

        internal static void Leave(string why)
        {
            if (!InView) return;
            InView = false;
            Claim(false);
            TowerRadar.Log("the local player left the console (" + why + ").");
        }

        static void Claim(bool on)
        {
            int me = Crocodile.LocalActor();
            if (Crocodile.IsMaster()) TowerRadar.OnClaim(me, on);
            else RadarNet.SendClaim(on);
        }

        static void Hint(string s, float seconds) { _hint = s; _hintUntil = Time.time + seconds; }

        static void Command(int cmd)
        {
            Vector3 pos = Vector3.zero;
            int kind = -1;
            if (cmd == TowerRadar.CmdEngage)
            {
                if (_selected == null || _selected.Go == null) { Hint("Select a blip first.", 2f); return; }
                if (_selected.Iff > 0) { Hint("That track is FRIEND - the guns never fire on their own side.", 3f); return; }
                GepardGun.Contact c = null;
                for (int i = 0; i < _air.Count; i++) if (_air[i].Go == _selected.Go) { c = _air[i]; break; }
                pos = c != null ? c.Pos : _selected.Pos;
                kind = _selected.Kind;
            }
            int mine = Crocodile.LocalActor();
            if (!TowerRadar.Obeyed(mine))
            {
                Hint("The " + TowerRadar.SideLabel(TowerRadar.HomeSide())
                    + " HQ operator holds this console - no orders until he is down.", 4f);
                return;
            }
            int side = TowerRadar.PlayerSide(mine), follow = 0;
            List<FlakGunInfo> all = Flak.Guns();
            for (int i = 0; i < all.Count; i++) if (TowerRadar.Follows(all[i], side)) follow++;
            if (follow == 0)
                Hint("No gun follows you - every gun is manned by another side's crew.", 4f);
            string what = cmd == TowerRadar.CmdEngage ? "WEAPONS FREE on track " + _selected.Id.ToString("00", CultureInfo.InvariantCulture)
                : cmd == TowerRadar.CmdFree ? "WEAPONS FREE, all guns" : cmd == TowerRadar.CmdHold ? "HOLD FIRE, all guns"
                : "target assignment cleared";
            Note("ORDER: " + what);
            TowerRadar.OnCommand(Crocodile.LocalActor(), cmd, kind, pos);
        }

        // --------------------------------------------------------- drawing
        //
        // W-UI4: the console in the UI kit's look (docs/UI_KIT.md) - kit
        // panel, cards, chips, buttons and fonts beside the green PPI scope.
        // The console keeps its virtual cursor (the game holds the mouse), so
        // hover and clicks test _cursor instead of IMGUI events. Every text
        // comes from a cached table or a memo rebuilt at the 4 Hz collect
        // (Rows); the draw pass itself builds no string and asks no system.

        static Texture2D _white, _disc;
        static GUIStyle _small;
        static readonly Color Green = new Color(0.35f, 1f, 0.5f, 1f);
        static readonly Color DimGreen = new Color(0.2f, 0.7f, 0.3f, 0.6f);
        static readonly Color Foe = new Color(1f, 0.3f, 0.25f, 1f);
        static readonly Color Unknown = new Color(1f, 0.85f, 0.3f, 1f);
        static float _cx, _cy, _r;
        static readonly Rect[] _buttons = new Rect[4];
        static readonly Vector3[] TileOutline = { new Vector3(2500f, 0f, -2500f), new Vector3(7500f, 0f, -2500f),
                                                  new Vector3(7500f, 0f, 2500f), new Vector3(2500f, 0f, 2500f),
                                                  new Vector3(2500f, 0f, -2500f) };
        static readonly string[] ButtonNames = { "ENGAGE SELECTED", "WEAPONS FREE ALL", "HOLD FIRE ALL", "CLEAR" };
        static readonly string[] StateNames = { "NO CREW", "IDLE", "TRACKING", "FIRING", "RELOADING", "PLAYER", "DESTROYED" };
        static readonly string[] CrewNames = { "crew 0/2", "crew 1/2", "crew 2/2" };
        static readonly string[] ModeNames = { "WEAPONS FREE", "HOLD FIRE", "WEAPONS TIGHT" };

        /// <summary>A gun line of the console, rebuilt at 4 Hz, texts only on change.</summary>
        sealed class GunRow
        {
            public string Id, Owner, Rounds;
            public int State, Crew, OwnerKey = int.MinValue;
            public bool Follows;
        }

        /// <summary>A track line of the air picture: the blip and its numbers.</summary>
        sealed class AirRow
        {
            public Blip B;
            public int RangeTenths, Brg, Hdg, Kmh, AltM;
        }

        static readonly List<FlakGunInfo> _guns = new List<FlakGunInfo>();   // the 4 Hz snapshot
        static readonly List<GunRow> _gunRows = new List<GunRow>();
        static readonly List<AirRow> _airRows = new List<AirRow>();
        static readonly List<AirRow> _airPool = new List<AirRow>();
        static readonly Rect[] _airRects = new Rect[16];
        static readonly Blip[] _airHit = new Blip[16];
        static int _airShown;
        static readonly UiMemo _controlText = new UiMemo(), _operatorText = new UiMemo(), _promptText = new UiMemo();
        static bool _obeyed;
        static int _who = -1;

        // Cached number texts: one string per value for the session.
        static readonly string[] _id2 = new string[100], _deg3 = new string[360], _tenths = new string[1000];
        static readonly string[] _radarChip = new string[102], _consoleChip = new string[102];

        static string Id2(int v) { v = Mathf.Clamp(v, 0, 99); return _id2[v] ?? (_id2[v] = v.ToString("00", CultureInfo.InvariantCulture)); }

        static string Deg3(int v)
        {
            v = ((v % 360) + 360) % 360;
            return _deg3[v] ?? (_deg3[v] = v.ToString("000", CultureInfo.InvariantCulture));
        }

        static string Km(int tenths)
        {
            if (tenths < 0) tenths = 0;
            if (tenths > 999) tenths = 999;
            return _tenths[tenths] ?? (_tenths[tenths] = (tenths / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " km");
        }

        /// <summary>"RADAR 85%" from a per-prefix table; "RADAR DESTROYED" at 0.</summary>
        static string Pct(string[] cache, string prefix, float f)
        {
            int v = f <= 0f ? 101 : Mathf.Clamp(Mathf.RoundToInt(f * 100f), 1, 100);
            return cache[v] ?? (cache[v] = prefix + (v == 101 ? "DESTROYED" : v.ToString(CultureInfo.InvariantCulture) + "%"));
        }

        static int HpTone(float f) { return f >= 0.5f ? UiTone.Success : f > 0f ? UiTone.Warning : UiTone.Error; }

        static void Ensure()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_disc == null)
            {
                const int n = 256;
                _disc = new Texture2D(n, n, TextureFormat.ARGB32, false);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = r > 1f ? 0f : Mathf.Clamp01((1f - r) * 60f);
                        float g = 0.10f + 0.05f * (1f - r);
                        _disc.SetPixel(x, y, new Color(0.01f, g, 0.03f, a));
                    }
                _disc.Apply();
            }
            if (_small == null)
            {
                _small = new GUIStyle(GUI.skin.label);
                _small.fontSize = 12;
            }
        }

        /// <summary>The kit's textures and fonts are built by its first use;
        /// ParagraphHeight is the public call that builds them (true = ready).</summary>
        static bool KitReady() { return UiKit.ParagraphHeight(" ", 64f) > 0f; }

        /// <summary>At the 4 Hz collect, only while the local player is at the
        /// console: the gun and track lines. Texts only when a value changed.</summary>
        static void Rows()
        {
            int me = Crocodile.LocalActor();
            _obeyed = TowerRadar.Obeyed(me);
            _who = _obeyed ? TowerRadar.PlayerSide(me) : TowerRadar.ControlSide;
            int home = TowerRadar.HomeSide();

            int ck = (TowerRadar.NpcOperatorUp ? 1 : 0) + (_obeyed ? 2 : 0) + 4 * (home + 1) + 4096 * (TowerRadar.ControlSide + 1);
            if (_controlText.Stale(ck))
                _controlText.Set(ck, TowerRadar.NpcOperatorUp
                    ? "RADAR HELD BY " + TowerRadar.SideLabel(home) + " - HQ operator at the console"
                        + (_obeyed ? "" : "   (watch only: your orders are refused)")
                    : "HQ OPERATOR DOWN - console free   orders standing: " + TowerRadar.SideLabel(TowerRadar.ControlSide)
                        + (_obeyed ? "   (you may give orders)" : ""));
            int op = TowerRadar.OperatorActor;
            int opSide = op >= 0 ? TowerRadar.PlayerSide(op) : -1;
            int ok = op >= 0 ? 8 * (opSide + 2) : TowerRadar.NpcOperatorUp ? 1 : 2;
            if (_operatorText.Stale(ok))
                _operatorText.Set(ok, "Operator: " + (op >= 0 ? "player at the console (" + TowerRadar.SideLabel(opSide) + ")"
                    : TowerRadar.NpcOperatorUp ? "HQ operator" : "none"));

            // the guns
            while (_gunRows.Count < _guns.Count) _gunRows.Add(new GunRow());
            for (int i = 0; i < _guns.Count; i++)
            {
                FlakGunInfo g = _guns[i];
                GunRow row = _gunRows[i];
                row.Id = g.Id;
                row.State = Mathf.Clamp((int)g.State, 0, StateNames.Length - 1);
                row.Crew = Mathf.Clamp(g.CrewAlive, 0, 2);
                row.Rounds = UiNum.Of(g.Rounds);
                row.Follows = TowerRadar.Follows(g, _who);
                int manSide = g.PlayerManned && g.ManActor >= 0 ? TowerRadar.PlayerSide(g.ManActor) : -1;
                int key = (g.PlayerManned ? 1 : 0) + (g.CrewAlive > 0 ? 2 : 0) + (row.Follows ? 4 : 0)
                    + 8 * (manSide + 2) + 8192 * (_who + 2) + 1048576 * (home + 1);
                if (key != row.OwnerKey || row.Owner == null)
                {
                    row.OwnerKey = key;
                    row.Owner = GunOwner(g, manSide, home) + "  -  "
                        + (row.Follows ? "FOLLOWS " + TowerRadar.SideLabel(_who) : TowerRadar.Ignores(g, _who));
                }
            }

            // the air picture: tracks painted this sweep and the last, foes first, then by range
            for (int i = 0; i < _airRows.Count; i++) _airPool.Add(_airRows[i]);
            _airRows.Clear();
            float sweep = TowerRadar.SweepSeconds;
            Vector3 eye = TowerRadar.RadarPos;
            for (int i = 0; i < _blips.Count; i++)
            {
                Blip b = _blips[i];
                if (b.Go == null || Time.time - b.PaintedAt > sweep * 2.2f) continue;
                AirRow row;
                if (_airPool.Count > 0) { row = _airPool[_airPool.Count - 1]; _airPool.RemoveAt(_airPool.Count - 1); }
                else row = new AirRow();
                Vector3 d = b.Pos - eye;
                d.y = 0f;
                Vector3 v = b.Vel;
                v.y = 0f;
                row.B = b;
                row.RangeTenths = Mathf.RoundToInt(d.magnitude / K / 100f);
                row.Brg = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f));
                row.Kmh = Mathf.RoundToInt(v.magnitude / K * 3.6f);
                row.Hdg = row.Kmh < 5 ? -1 : Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg, 360f));
                row.AltM = Mathf.RoundToInt(b.Height / K);
                // insertion: foe (-1) before unknown (0) before friend (1), then nearer first
                int at = _airRows.Count;
                while (at > 0 && Before(row, _airRows[at - 1])) at--;
                _airRows.Insert(at, row);
            }
        }

        static bool Before(AirRow a, AirRow b)
        {
            if (a.B.Iff != b.B.Iff) return a.B.Iff < b.B.Iff;
            return a.RangeTenths < b.RangeTenths;
        }

        /// <summary>Who holds a gun: a player at the sight, the HQ's crew, nobody.</summary>
        static string GunOwner(FlakGunInfo g, int manSide, int home)
        {
            if (g.PlayerManned) return "PLAYER (" + TowerRadar.SideLabel(manSide) + ")";
            if (g.CrewAlive > 0) return TowerRadar.SideLabel(home) + " CREW";
            return "UNMANNED";
        }

        internal static void Draw()
        {
            if (TowerSupport.Selecting) return;
            try
            {
                if (!InView && !_near && (_hint == null || Time.time > _hintUntil)) return;
                Ensure();
                // Without the kit (its font failed to build) the scope still
                // draws, so the console never goes blind; G / Esc leave it.
                bool kit = KitReady();
                float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                if (!InView)
                {
                    if (!kit) return;
                    if (_near)
                    {
                        int kk = (int)Key();
                        Prompt(h * 0.62f, _nearWhy ?? (_promptText.Stale(kk) ? _promptText.Set(kk, "[" + Key() + "] Radar console") : _promptText.Text),
                               _nearWhy != null ? UiKit.Warn : UiKit.Text);
                    }
                    if (_hint != null && Time.time <= _hintUntil) Prompt(h * 0.66f, _hint, UiKit.Warn);
                    return;
                }
                GUI.color = new Color(0.02f, 0.025f, 0.03f, 0.94f);
                GUI.DrawTexture(new Rect(0f, 0f, w, h), _white);
                // The scope gives way on narrow screens: the side panel keeps 640 design px.
                _r = Mathf.Min(h * 0.44f, Mathf.Min(w * 0.3f, (w - UiKit.S(640f) - h * 0.09f - UiKit.S(UiKit.Pad)) * 0.5f));
                _r = Mathf.Max(_r, h * 0.2f);
                _cx = _r + h * 0.05f;
                _cy = h * 0.5f;
                Scope();
                if (kit) Panel(w, h);
                // the cursor
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(_cursor.x - 9f, _cursor.y - 1f, 18f, 2f), _white);
                GUI.DrawTexture(new Rect(_cursor.x - 1f, _cursor.y - 9f, 2f, 18f), _white);
                if (kit && _hint != null && Time.time <= _hintUntil) Prompt(h - UiKit.S(72f), _hint, UiKit.Warn);
                GUI.color = Color.white;
            }
            catch { GUI.matrix = Matrix4x4.identity; GUI.color = Color.white; }
        }

        /// <summary>A kit pill across the screen's middle: the console prompt and hints.</summary>
        static void Prompt(float y, string text, Color c)
        {
            float pw = UiKit.S(560f), ph = UiKit.S(34f);
            Rect r = new Rect((UnityEngine.Screen.width - pw) * 0.5f, y, pw, ph);
            UiKit.Fill(new Rect(r.x, r.y + UiKit.S(3f), r.width, r.height), UiKit.Shadow, 1);
            UiKit.Fill(r, UiKit.Header, 1);
            UiKit.Fill(new Rect(r.x, r.y + UiKit.S(8f), UiKit.S(3f), ph - UiKit.S(16f)), c, 0);
            UiKit.Label(r, text, UiFont.Body, UiFont.Center, UiKit.Text);
        }

        static Vector2 ToScreen(Vector3 world)
        {
            Vector3 eye = TowerRadar.RadarPos;
            float s = _r / TowerRadar.ScopeRangeU;
            return new Vector2(_cx + (world.x - eye.x) * s, _cy - (world.z - eye.z) * s);
        }

        static void Scope()
        {
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(_cx - _r, _cy - _r, _r * 2f, _r * 2f), _disc);
            if (!TowerRadar.Working)
            {
                GUI.color = new Color(0f, 0f, 0f, 1f);
                GUI.DrawTexture(new Rect(_cx - _r * 0.72f, _cy - _r * 0.72f, _r * 1.44f, _r * 1.44f), _white);
                UiKit.Label(new Rect(_cx - _r, _cy - 13f, _r * 2f, 26f),
                    !TowerRadar.RadarAlive ? "NO VIDEO - ANTENNA DESTROYED" : "NO VIDEO - CONSOLE DAMAGED", UiFont.Heading, UiFont.Center, UiKit.Bad);
                return;
            }
            float range = TowerRadar.ScopeRangeU;
            // range rings every 500 m, bearing ticks every 10 degrees
            GUI.color = DimGreen;
            for (int m = 500; m * K <= range + 1f; m += 500)
                Ring(_cx, _cy, _r * m * K / range, m % 1000 == 0 ? 96 : 64);
            for (int a = 0; a < 360; a += 10)
            {
                float rad = a * Mathf.Deg2Rad;
                float len = a % 30 == 0 ? 10f : 5f;
                Line(_cx + Mathf.Sin(rad) * (_r - len), _cy - Mathf.Cos(rad) * (_r - len), _cx + Mathf.Sin(rad) * _r,
                     _cy - Mathf.Cos(rad) * _r, 1.5f);
            }
            SmallLabel(_cx - 6f, _cy - _r - 18f, "N", Green);
            // the east tile's outline and the runway
            GUI.color = new Color(0.25f, 0.6f, 0.35f, 0.5f);
            Poly(TileOutline);
            GUI.color = new Color(0.4f, 0.9f, 0.5f, 0.7f);
            Vector2 r0 = ToScreen(new Vector3(4632f, 0f, -1600f)), r1 = ToScreen(new Vector3(4632f, 0f, 1600f));
            Line(r0.x, r0.y, r1.x, r1.y, 3f);
            // the guns and their reach (the 4 Hz snapshot)
            for (int i = 0; i < _guns.Count; i++)
            {
                Vector2 g = ToScreen(_guns[i].Position);
                GUI.color = new Color(0.3f, 0.8f, 1f, 0.35f);
                Ring(g.x, g.y, _r * _guns[i].Range / range, 48);
                GUI.color = new Color(0.3f, 0.8f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(g.x - 3f, g.y - 3f, 6f, 6f), _white);
                SmallLabel(g.x + 5f, g.y - 7f, _guns[i].Id, GUI.color);
            }
            // the sweep and its afterglow
            float ang = TowerRadar.AntennaAngle;
            if (TowerRadar.B(TowerRadar.CfgSweep))
            {
                for (int k = 12; k >= 0; k--)
                {
                    float a = (ang - k * 2.2f) * Mathf.Deg2Rad;
                    GUI.color = new Color(0.4f, 1f, 0.55f, k == 0 ? 0.95f : 0.22f * (1f - k / 13f));
                    Line(_cx, _cy, _cx + Mathf.Sin(a) * _r, _cy - Mathf.Cos(a) * _r, k == 0 ? 2.5f : 3.5f);
                }
            }
            // blips
            float sweep = TowerRadar.SweepSeconds;
            bool fade = TowerRadar.B(TowerRadar.CfgFade);
            for (int i = 0; i < _blips.Count; i++)
            {
                Blip b = _blips[i];
                float age = Time.time - b.PaintedAt;
                float alpha = fade ? Mathf.Clamp01(1f - age / (sweep * 1.05f)) * 0.9f + 0.1f : 1f;
                if (age > sweep * 2.2f) continue;
                Vector2 p = ToScreen(b.Pos);
                Color c = b.Iff > 0 ? Green : b.Iff < 0 ? Foe : Unknown;
                c.a = alpha;
                GUI.color = c;
                float s = b.Kind == 2 || b.Kind == 3 ? 4f : 6f;
                GUI.DrawTexture(new Rect(p.x - s * 0.5f, p.y - s * 0.5f, s, s), _white);
                // the heading vector: where it will be in 20 s
                Vector3 v = b.Vel;
                v.y = 0f;
                if (v.sqrMagnitude > 1f)
                {
                    Vector2 q = ToScreen(b.Pos + v * 20f);
                    Line(p.x, p.y, q.x, q.y, 1.2f);
                }
                SmallLabel(p.x + 6f, p.y + 2f, Id2(b.Id), c);
                if (b == _selected) Bracket(p.x, p.y, 11f, new Color(1f, 1f, 1f, 0.95f));
                if (TowerRadar.AssignedKind >= 0 && b.Kind == TowerRadar.AssignedKind
                    && (b.Pos - TowerRadar.AssignedPos).sqrMagnitude < 150f * 150f)
                {
                    Bracket(p.x, p.y, 15f, Foe);
                    SmallLabel(p.x + 6f, p.y - 16f, "ENGAGE", Foe);
                }
            }
            GUI.color = Color.white;
        }

        static int IffTone(int iff) { return iff > 0 ? UiTone.Success : iff < 0 ? UiTone.Error : UiTone.Warning; }

        static Color IffColor(int iff) { return iff > 0 ? UiKit.Good : iff < 0 ? UiKit.Bad : UiKit.Warn; }

        /// <summary>The side panel: status card, orders, the selected track,
        /// the guns (owner, crew, state), the air picture, the contact log.</summary>
        static void Panel(float w, float h)
        {
            float gap = UiKit.S(UiKit.Gap), pad = UiKit.S(UiKit.Pad);
            float rowH = UiKit.S(UiKit.RowH), line = UiKit.S(22f);
            float x = _cx + _r + h * 0.04f;
            float pw = w - x - pad;
            float y = h * 0.04f;
            float bottom = h - pad - line;
            UiKit.Fill(new Rect(x - pad, 0f, w - x + pad, h), UiKit.Panel, 0);
            UiKit.Fill(new Rect(x - pad, 0f, Mathf.Max(1f, UiKit.S(1f)), h), UiKit.Line, 0);

            // the title and the status chips
            UiKit.Fill(new Rect(x, y + UiKit.S(6f), UiKit.S(3f), UiKit.S(22f)), UiKit.Accent, 0);
            UiKit.Label(new Rect(x + UiKit.S(12f), y, pw, UiKit.S(34f)), "P-18 AIR DEFENCE HQ - C1 TOWER", UiFont.Title, UiFont.Left, UiKit.Text);
            y += UiKit.S(38f);
            float chipH = UiKit.S(22f);
            Rect cr = new Rect(x, y, pw, chipH);
            UiKit.Chip(UiKit.Col(cr, 0, 5), Pct(_radarChip, "RADAR ", TowerRadar.RadarHp), HpTone(TowerRadar.RadarHp));
            UiKit.Chip(UiKit.Col(cr, 1, 5), Pct(_consoleChip, "CONSOLE ", TowerRadar.ConsoleHp), HpTone(TowerRadar.ConsoleHp));
            UiKit.Chip(UiKit.Col(cr, 2, 5), Km(Mathf.RoundToInt(TowerRadar.ScopeRangeU / K / 100f)), UiTone.Info);
            int mode = Mathf.Clamp(TowerRadar.Mode, 0, 2);
            UiKit.Chip(UiKit.Col(cr, 3, 5), ModeNames[mode], mode == 0 ? UiTone.Success : mode == 1 ? UiTone.Warning : UiTone.Info);
            if (TowerRadar.SirenOn) UiKit.Chip(UiKit.Col(cr, 4, 5), "SIREN", UiTone.Error);
            y += chipH + gap;
            float lw = UiKit.S(96f);
            UiKit.Label(new Rect(x, y, lw, line), "fire direction", UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Label(new Rect(x + lw, y, UiKit.S(170f), line), TowerRadar.TierName(TowerRadar.Tier), UiFont.Small, UiFont.Left, UiKit.Text);
            UiKit.Label(new Rect(x + lw + UiKit.S(176f), y, UiKit.S(56f), line), "antenna", UiFont.Small, UiFont.Left, UiKit.TextDim);
            UiKit.Label(new Rect(x + lw + UiKit.S(232f), y, UiKit.S(40f), line), Deg3(Mathf.RoundToInt(TowerRadar.AntennaAngle)),
                UiFont.Small, UiFont.Left, UiKit.Text);
            y += line;
            UiKit.Status(new Rect(x, y, pw, rowH), _obeyed ? UiTone.Success : UiTone.Error, _controlText.Text);
            y += rowH + gap * 1.5f;

            // the orders
            bool canEngage = _selected != null && _selected.Go != null && _selected.Iff <= 0;
            Rect br = new Rect(x, y, pw, rowH + UiKit.S(4f));
            for (int i = 0; i < 4; i++)
            {
                Rect r = UiKit.Col(br, i, 4);
                _buttons[i] = r;
                bool active = (i == 1 && mode == 0) || (i == 2 && mode == 1) || (i == 0 && mode == 2);
                ConsoleButton(r, ButtonNames[i], i == 0 ? UiButton.Primary : i == 3 ? UiButton.Ghost : UiButton.Secondary,
                              i != 0 || canEngage, active);
            }
            y += br.height + gap * 1.5f;

            // the selected track
            float cardH = UiKit.S(78f);
            Rect card = new Rect(x, y, pw, cardH);
            UiKit.Fill(card, UiKit.CardFill, 1);
            if (_selected != null && _selected.Go != null)
            {
                Blip b = _selected;
                AirRow a = RowOf(b);
                float ix = x + UiKit.S(12f), iw = pw - UiKit.S(24f);
                UiKit.Label(new Rect(ix, y + UiKit.S(6f), iw * 0.3f, UiKit.S(24f)), "TRACK", UiFont.Heading, UiFont.Left, UiKit.TextDim);
                UiKit.Label(new Rect(ix + UiKit.S(64f), y + UiKit.S(6f), UiKit.S(40f), UiKit.S(24f)), Id2(b.Id), UiFont.Heading, UiFont.Left, UiKit.Text);
                UiKit.Chip(new Rect(ix + UiKit.S(104f), y + UiKit.S(8f), UiKit.S(86f), UiKit.S(20f)), IffName(b.Iff), IffTone(b.Iff));
                UiKit.Label(new Rect(ix + UiKit.S(200f), y + UiKit.S(6f), iw - UiKit.S(200f), UiKit.S(24f)), Guess(b), UiFont.Body, UiFont.Left, IffColor(b.Iff));
                if (a != null) Numbers(new Rect(ix, y + UiKit.S(40f), iw, UiKit.S(26f)), a, UiFont.Body);
            }
            else
                UiKit.Label(new Rect(x + UiKit.S(12f), y, pw - UiKit.S(24f), cardH), "No track selected - click a blip on the scope or a line of the air picture.",
                    UiFont.Body, UiFont.Left, UiKit.TextDim);
            y += cardH + gap * 1.5f;

            // the guns
            float small = UiKit.S(26f);
            UiKit.Section(new Rect(x, y, pw, UiKit.S(22f)), "GUNS - OWNER / CREW / STATE");
            y += UiKit.S(22f) + gap * 0.5f;
            if (_gunRows.Count == 0 || _guns.Count == 0)
            {
                UiKit.Label(new Rect(x, y, pw, small), "No AA guns on the air defence net.", UiFont.Body, UiFont.Left, UiKit.Warn);
                y += small;
            }
            for (int i = 0; i < _guns.Count && i < _gunRows.Count; i++)
            {
                GunRow g = _gunRows[i];
                Rect r = new Rect(x, y, pw, small);
                if (i % 2 == 0) UiKit.Fill(r, UiKit.Fade(Color.white, 0.03f), 2);
                UiKit.Label(new Rect(r.x + UiKit.S(8f), r.y, UiKit.S(60f), small), g.Id, UiFont.Body, UiFont.Left, UiKit.Text);
                UiKit.Label(new Rect(r.x + UiKit.S(70f), r.y, UiKit.S(44f), small), GunType(i), UiFont.Small, UiFont.Left, UiKit.TextDim);
                float cx = r.x + UiKit.S(118f);
                UiKit.Chip(new Rect(cx, r.y + UiKit.S(3f), UiKit.S(86f), small - UiKit.S(6f)), StateNames[g.State], StateTone(g.State));
                UiKit.Chip(new Rect(cx + UiKit.S(92f), r.y + UiKit.S(3f), UiKit.S(66f), small - UiKit.S(6f)), CrewNames[g.Crew],
                    g.Crew == 2 ? UiTone.Success : g.Crew == 1 ? UiTone.Warning : UiTone.Error);
                UiKit.Label(new Rect(cx + UiKit.S(164f), r.y, UiKit.S(36f), small), g.Rounds, UiFont.Small, UiFont.Right, UiKit.TextDim);
                UiKit.Label(new Rect(cx + UiKit.S(204f), r.y, UiKit.S(26f), small), "rds", UiFont.Small, UiFont.Left, UiKit.TextDim);
                UiKit.Label(new Rect(cx + UiKit.S(234f), r.y, r.xMax - cx - UiKit.S(240f), small), g.Owner, UiFont.Small, UiFont.Left,
                    g.Follows ? UiKit.Good : UiKit.Bad);
                y += small;
            }
            UiKit.Label(new Rect(x, y, pw, UiKit.S(20f)), _operatorText.Text, UiFont.Small, UiFont.Left, UiKit.TextDim);
            y += UiKit.S(20f) + gap;

            // the air picture
            UiKit.Section(new Rect(x, y, pw, UiKit.S(22f)), "AIR PICTURE");
            y += UiKit.S(22f) + gap * 0.5f;
            bool log = TowerRadar.B(TowerRadar.CfgLog);
            float logMin = log ? UiKit.S(22f) + 4f * UiKit.S(17f) : 0f;
            _airShown = 0;
            if (_airRows.Count == 0)
            {
                UiKit.Label(new Rect(x, y, pw, small), TowerRadar.Working ? "No air contact." : "No picture - the radar is dark.",
                    UiFont.Body, UiFont.Left, UiKit.TextDim);
                y += small;
            }
            for (int i = 0; i < _airRows.Count && _airShown < _airRects.Length; i++)
            {
                if (y + small > bottom - logMin)
                {
                    UiKit.Label(new Rect(x, y, pw, UiKit.S(18f)), "+ more tracks on the scope", UiFont.Small, UiFont.Left, UiKit.TextDim);
                    y += UiKit.S(18f);
                    break;
                }
                AirRow a = _airRows[i];
                Rect r = new Rect(x, y, pw, small);
                _airRects[_airShown] = r;
                _airHit[_airShown] = a.B;
                _airShown++;
                bool sel = a.B == _selected;
                bool hot = r.Contains(_cursor);
                if (sel || hot || i % 2 == 0)
                    UiKit.Fill(r, sel ? UiKit.Fade(UiKit.Accent, 0.25f) : hot ? UiKit.CardHover : UiKit.Fade(Color.white, 0.03f), 2);
                UiKit.Label(new Rect(r.x + UiKit.S(8f), r.y, UiKit.S(30f), small), Id2(a.B.Id), UiFont.Body, UiFont.Left, UiKit.Text);
                UiKit.Chip(new Rect(r.x + UiKit.S(40f), r.y + UiKit.S(3f), UiKit.S(74f), small - UiKit.S(6f)), IffName(a.B.Iff), IffTone(a.B.Iff));
                UiKit.Label(new Rect(r.x + UiKit.S(122f), r.y, UiKit.S(170f), small), Guess(a.B), UiFont.Small, UiFont.Left, IffColor(a.B.Iff));
                Numbers(new Rect(r.x + UiKit.S(296f), r.y, r.width - UiKit.S(300f), small), a, UiFont.Small);
                if (TowerRadar.AssignedKind >= 0 && a.B.Kind == TowerRadar.AssignedKind
                    && (a.B.Pos - TowerRadar.AssignedPos).sqrMagnitude < 150f * 150f)
                    UiKit.Fill(new Rect(r.x, r.y, UiKit.S(3f), r.height), UiKit.Bad, 0);
                y += small;
            }
            y += gap;

            // the log
            if (log && y + UiKit.S(22f) < bottom)
            {
                UiKit.Section(new Rect(x, y, pw, UiKit.S(22f)), "RADIO / CONTACT LOG");
                y += UiKit.S(22f) + gap * 0.5f;
                float lh = UiKit.S(17f);
                for (int i = _log.Count - 1; i >= 0 && y + lh <= bottom; i--)
                {
                    UiKit.Label(new Rect(x, y, pw, lh), _log[i], UiFont.Small, UiFont.Left, UiKit.Fade(UiKit.Text, 0.8f));
                    y += lh;
                }
            }
            UiKit.Label(new Rect(x, h - pad - line, pw, line), FooterText(), UiFont.Small, UiFont.Left, UiKit.TextDim);
        }

        static string _footer;
        static KeyCode _footerKey = KeyCode.None;

        static string FooterText()
        {
            KeyCode k = Key();
            if (_footer == null || k != _footerKey)
            {
                _footerKey = k;
                _footer = "H: support map   Mouse: cursor   LMB: select / order   " + k + " / Esc: leave";
            }
            return _footer;
        }

        /// <summary>The gun's type label (the 52-K is the net's only gun type here).</summary>
        static string GunType(int index) { return index < _guns.Count && _guns[index].ShortRange ? "ZU-23" : "52-K"; }   // W-AA5

        static int StateTone(int state)
        {
            switch (state)
            {
                case 0: return UiTone.Error;      // no crew
                case 3: return UiTone.Warning;    // firing
                case 4: return UiTone.Info;       // reloading
                case 5: return UiTone.Info;       // player
                case 6: return UiTone.Error;      // W-AA7: destroyed
                default: return UiTone.Success;   // idle, tracking
            }
        }

        /// <summary>Range, bearing, heading, speed, height of a track in columns.</summary>
        static void Numbers(Rect r, AirRow a, int font)
        {
            float c = r.width / 5f;
            Color dim = UiKit.TextDim, txt = UiKit.Text;
            Col(new Rect(r.x, r.y, c, r.height), "RNG", Km(a.RangeTenths), font, dim, txt);
            Col(new Rect(r.x + c, r.y, c, r.height), "BRG", Deg3(a.Brg), font, dim, txt);
            Col(new Rect(r.x + 2f * c, r.y, c, r.height), "HDG", a.Hdg < 0 ? "---" : Deg3(a.Hdg), font, dim, txt);
            Col(new Rect(r.x + 3f * c, r.y, c, r.height), "KM/H", UiNum.Of(a.Kmh), font, dim, txt);
            Col(new Rect(r.x + 4f * c, r.y, c, r.height), "ALT M", a.AltM < 1000 ? UiNum.Of(Mathf.Max(0, a.AltM)) : ">999", font, dim, txt);
        }

        static void Col(Rect r, string label, string value, int font, Color dim, Color txt)
        {
            float lw = UiKit.S(font == UiFont.Small ? 36f : 44f);
            UiKit.Label(new Rect(r.x, r.y, lw, r.height), label, UiFont.Small, UiFont.Left, dim);
            UiKit.Label(new Rect(r.x + lw, r.y, r.width - lw, r.height), value, font, UiFont.Left, txt);
        }

        static AirRow RowOf(Blip b)
        {
            for (int i = 0; i < _airRows.Count; i++) if (_airRows[i].B == b) return _airRows[i];
            return null;
        }

        /// <summary>A kit button under the virtual cursor (the console has no IMGUI mouse).</summary>
        static void ConsoleButton(Rect r, string text, int look, bool enabled, bool active)
        {
            bool hot = enabled && r.Contains(_cursor);
            bool down = hot && Input.GetMouseButton(0);
            Color bg, fg = UiKit.Text;
            if (look == UiButton.Primary)
            {
                bg = down ? UiKit.AccentPress : hot ? UiKit.AccentHover : UiKit.Accent;
                fg = UiKit.TextOnAccent;
            }
            else if (look == UiButton.Ghost)
                bg = UiKit.Fade(Color.white, down ? 0.10f : hot ? 0.06f : 0f);
            else
                bg = active ? UiKit.Fade(UiKit.Accent, hot ? 0.45f : 0.32f) : down ? UiKit.Header : hot ? UiKit.CardHover : UiKit.CardFill;
            if (!enabled) { bg = UiKit.Fade(bg, 0.35f); fg = UiKit.Fade(fg, 0.45f); }
            UiKit.Fill(r, bg, 1);
            if (look == UiButton.Secondary || look == UiButton.Ghost) UiKit.Outline(r, active ? UiKit.Accent : UiKit.Line);
            UiKit.Label(r, text, UiFont.Body, UiFont.Center, fg);
        }

        static void Click()
        {
            for (int i = 0; i < 4; i++)
                if (_buttons[i].Contains(_cursor))
                {
                    Command(i == 0 ? TowerRadar.CmdEngage : i == 1 ? TowerRadar.CmdFree : i == 2 ? TowerRadar.CmdHold : TowerRadar.CmdClear);
                    return;
                }
            for (int i = 0; i < _airShown; i++)
                if (_airRects[i].Contains(_cursor))
                {
                    if (_airHit[i] != null && _blips.Contains(_airHit[i])) _selected = _airHit[i];
                    return;
                }
            if (_r <= 0f) return;
            Blip best = null;
            float bestD = 20f * 20f;
            for (int i = 0; i < _blips.Count; i++)
            {
                Vector2 p = ToScreen(_blips[i].Pos);
                float d = (p - _cursor).sqrMagnitude;
                if (d < bestD) { best = _blips[i]; bestD = d; }
            }
            if (best != null || (_cursor - new Vector2(_cx, _cy)).magnitude < _r) _selected = best;
        }

        // ------------------------------------------------------- primitives

        static void SmallLabel(float x, float y, string s, Color c)
        {
            GUI.color = c;
            GUI.Label(new Rect(x, y, 900f, 18f), s, _small);
        }

        static void Line(float x0, float y0, float x1, float y1, float width)
        {
            Vector2 a = new Vector2(x0, y0), b = new Vector2(x1, y1);
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 keep = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), _white);
            GUI.matrix = keep;
        }

        static void Ring(float cx, float cy, float r, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                GUI.DrawTexture(new Rect(cx + Mathf.Cos(a) * r - 1f, cy + Mathf.Sin(a) * r - 1f, 2f, 2f), _white);
            }
        }

        static void Poly(Vector3[] pts)
        {
            for (int i = 1; i < pts.Length; i++)
            {
                Vector2 a = ToScreen(pts[i - 1]), b = ToScreen(pts[i]);
                Line(a.x, a.y, b.x, b.y, 1f);
            }
        }

        static void Bracket(float x, float y, float s, Color c)
        {
            GUI.color = c;
            float l = s * 0.5f;
            GUI.DrawTexture(new Rect(x - s, y - s, l, 1.5f), _white);
            GUI.DrawTexture(new Rect(x - s, y - s, 1.5f, l), _white);
            GUI.DrawTexture(new Rect(x + s - l, y - s, l, 1.5f), _white);
            GUI.DrawTexture(new Rect(x + s, y - s, 1.5f, l), _white);
            GUI.DrawTexture(new Rect(x - s, y + s, l, 1.5f), _white);
            GUI.DrawTexture(new Rect(x - s, y + s - l, 1.5f, l), _white);
            GUI.DrawTexture(new Rect(x + s - l, y + s, l, 1.5f), _white);
            GUI.DrawTexture(new Rect(x + s, y + s - l, 1.5f, l), _white);
        }
    }

    // =====================================================================
    // The siren

    /// <summary>The air raid siren on the tower roof and the NPCs it alarms.
    /// Its sound is a <see cref="SirenVoice"/> (assets/ndr_siren.wav).</summary>
    internal static class RadarSiren
    {
        static SirenVoice _voice;

        internal static void Tick()
        {
            bool on = TowerRadar.SirenOn && TowerRadar.B(TowerRadar.CfgSiren);
            if (!on && _voice == null) return;
            if (_voice == null) _voice = SirenVoice.Make("NDR tower siren", 90f, 7000f);
            if (_voice == null) return;
            _voice.Position = TowerRadar.TowerPoint(new Vector3(7.9f, TowerRadar.RoofM + 0.8f, 0f));
            _voice.Tick(on);
        }

        internal static void Stop()
        {
            if (_voice != null) _voice.Destroy();
            _voice = null;
        }

        /// <summary>The fallback when assets/ndr_siren.wav is missing or
        /// unreadable. Eight seconds: up from 280 to 620 Hz and down again, a
        /// rotor's chopped harmonics over it.</summary>
        internal static AudioClip Clip()
        {
            const int rate = 22050;
            const float seconds = 8f;
            float[] data = new float[Mathf.RoundToInt(rate * seconds)];
            double phase = 0.0;
            for (int i = 0; i < data.Length; i++)
            {
                float t = (float)i / rate;
                float u = t / seconds;
                float env = u < 0.5f ? Mathf.SmoothStep(0f, 1f, u * 2f) : Mathf.SmoothStep(1f, 0f, (u - 0.5f) * 2f);
                float f = 280f + 340f * env;
                phase += 2.0 * Math.PI * f / rate;
                float s = (float)(Math.Sin(phase) * 0.6 + Math.Sin(phase * 2.0) * 0.25 + Math.Sin(phase * 3.0) * 0.12);
                float chop = 0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * f * 0.25f * t);
                data[i] = Mathf.Clamp(s * chop * 0.7f, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create("NDR air raid siren", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Master: the living NPCs round the tower get the game's
        /// general alarm (the gun crews and the operator keep their posts).</summary>
        internal static void AlarmNpcs()
        {
            if (!TowerRadar.B(TowerRadar.CfgSirenAlarm)) return;
            try
            {
                Type npcType = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo alarm = npcType == null ? null : AccessTools.Method(npcType, "SetGeneralAlarm", null, null);
                if (alarm == null) return;
                float r = Mathf.Max(50f, TowerRadar.F(TowerRadar.CfgAlarmRadius, 450f)) * TowerRadar.K;
                Vector3 c = TowerRadar.TowerPoint(Vector3.zero);
                UnityEngine.Object[] all = NpcScan.All();
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    Component ai = all[i] as Component;
                    if (ai == null || !Flak.Up(ai)) continue;
                    Vector3 d = ai.transform.position - c;
                    d.y = 0f;
                    if (d.sqrMagnitude > r * r) continue;
                    string key = Crew.GroundKey(ai);
                    if (key != null && (key.StartsWith(FlakCrew.KeyPrefix, StringComparison.Ordinal) || key == RadarOperator.Key)) continue;
                    try { alarm.Invoke(ai, new object[] { true }); n++; }
                    catch { }
                }
                TowerRadar.Log("siren: " + n + " NPC(s) alarmed within " + r.ToString("0", CultureInfo.InvariantCulture) + " u.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar alarm: " + ex.Message); }
        }
    }

    /// <summary>
    /// A motor siren's voice (task B8b): assets/ndr_siren.wav, synthesised by
    /// research/siren_synth.py or a recording put in its place. The file's
    /// WAV 'smpl' loop splits it in three - the spin-up played once, the wail
    /// looped while the alarm lasts, the spin-down after the all clear - and
    /// the parts are scheduled on the audio clock, so they join sample-exact:
    /// the spin-down begins where the loop ends (the top of a wail), up to one
    /// wail cycle after the all clear. A file without loop points loops whole
    /// with a pitch/volume run-up and run-down; without the file the
    /// synthesised RadarSiren.Clip() does the same.
    /// </summary>
    internal sealed class SirenVoice
    {
        internal const string FileName = "ndr_siren.wav";

        static bool _loaded, _parts;
        static AudioClip _up, _loop, _down;

        GameObject _go;
        AudioSource _upSrc, _loopSrc, _downSrc;
        int _state;                 // 0 silent, 1 sounding, 2 running down (parts)
        double _loopAt, _downAt, _endAt;
        bool _loopCut;              // the all clear came during the spin-up
        float _volume;              // whole-clip mode

        internal Vector3 Position
        {
            set { if (_go != null) _go.transform.position = value; }
        }

        internal static SirenVoice Make(string name, float min, float max)
        {
            try
            {
                Load();
                if (_loop == null) return null;
                SirenVoice v = new SirenVoice();
                v._go = new GameObject(name);
                UnityEngine.Object.DontDestroyOnLoad(v._go);
                v._loopSrc = v.Source(_loop, true, min, max);
                if (_up != null) v._upSrc = v.Source(_up, false, min, max);
                if (_down != null) v._downSrc = v.Source(_down, false, min, max);
                return v;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Siren: " + ex.Message); return null; }
        }

        AudioSource Source(AudioClip clip, bool loop, float min, float max)
        {
            AudioSource s = _go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = loop;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = min;
            s.maxDistance = max;
            s.volume = _parts ? 1f : 0f;
            return s;
        }

        /// <summary>Every frame; <paramref name="on"/> is the alarm as the
        /// caller's trigger logic has it.</summary>
        internal void Tick(bool on)
        {
            if (_go == null) return;
            if (!_parts) { TickWhole(on); return; }
            double now = AudioSettings.dspTime;
            if (on)
            {
                if (_state == 1)
                {
                    // a loop end that could not be taken back: wail on
                    if (!_loopCut && now > _loopAt + 0.2 && !_loopSrc.isPlaying) _loopSrc.Play();
                    return;
                }
                if (_state == 2 && now < _downAt - 0.05)
                {
                    // the alarm again before the run-down began: keep wailing
                    if (_loopCut) _loopSrc.PlayScheduled(_loopAt);
                    else _loopSrc.SetScheduledEndTime(now + 1e7);
                    if (_downSrc != null) _downSrc.Stop();
                    _state = 1;
                    return;
                }
                StopAll();
                double at = now + 0.1;
                if (_upSrc != null) _upSrc.PlayScheduled(at);
                _loopAt = at + Seconds(_up);
                _loopSrc.PlayScheduled(_loopAt);
                _loopCut = false;
                _state = 1;
                return;
            }
            if (_state == 1)
            {
                double down = now + 0.1;
                if (down <= _loopAt)
                {
                    // still spinning up: the loop is skipped, straight into the spin-down
                    _loopSrc.Stop();
                    _loopCut = true;
                    down = _loopAt;
                }
                else
                {
                    double len = Seconds(_loop);
                    down = _loopAt + Math.Ceiling((down - _loopAt) / len) * len;
                    _loopSrc.SetScheduledEndTime(down);
                    _loopCut = false;
                }
                if (_downSrc != null) _downSrc.PlayScheduled(down);
                _downAt = down;
                _endAt = down + Seconds(_down) + 0.2;
                _state = 2;
                return;
            }
            if (_state == 2 && now > _endAt)
            {
                StopAll();
                _state = 0;
            }
        }

        void TickWhole(bool on)
        {
            _volume = Mathf.MoveTowards(_volume, on ? 1f : 0f, Time.deltaTime / (on ? 3f : 5f));
            _loopSrc.volume = _volume;
            _loopSrc.pitch = 0.7f + 0.3f * _volume;     // the motor spins up and runs down
            if (_volume > 0f && !_loopSrc.isPlaying) _loopSrc.Play();
            if (_volume <= 0f && _loopSrc.isPlaying) _loopSrc.Stop();
        }

        void StopAll()
        {
            if (_upSrc != null) _upSrc.Stop();
            if (_loopSrc != null) _loopSrc.Stop();
            if (_downSrc != null) _downSrc.Stop();
        }

        internal void Destroy()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = null;
            _state = 0;
        }

        static double Seconds(AudioClip c)
        {
            return c == null ? 0.0 : (double)c.samples / c.frequency;
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string path = RevivalPlugin.AssetDir == null ? null : System.IO.Path.Combine(RevivalPlugin.AssetDir, FileName);
            try
            {
                float[] data;
                int rate, a, b;
                if (path != null && System.IO.File.Exists(path)
                    && ReadWav(System.IO.File.ReadAllBytes(path), out data, out rate, out a, out b))
                {
                    if (a >= 0 && b <= data.Length && b - a >= rate / 2)
                    {
                        _up = Part("NDR siren spin-up", data, 0, a, rate);
                        _loop = Part("NDR siren wail", data, a, b, rate);
                        _down = Part("NDR siren spin-down", data, b, data.Length, rate);
                        _parts = true;
                        RevivalPlugin.L.LogInfo("Siren: " + FileName + " " + Sec(data.Length, rate) + " s, lead-in (spin-up) "
                            + Sec(a, rate) + " s, wail loop " + Sec(b - a, rate) + " s, spin-down "
                            + Sec(data.Length - b, rate) + " s.");
                        return;
                    }
                    _loop = Part("NDR siren", data, 0, data.Length, rate);
                    RevivalPlugin.L.LogInfo("Siren: " + FileName + " " + Sec(data.Length, rate)
                        + " s without loop points - looped whole.");
                    return;
                }
                RevivalPlugin.L.LogWarning("Siren: " + FileName + " missing or unreadable - synthesised fallback.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Siren: " + FileName + ": " + ex.Message + " - synthesised fallback.");
            }
            _up = _down = null;
            _parts = false;
            _loop = RadarSiren.Clip();
        }

        static string Sec(int samples, int rate)
        {
            return ((float)samples / rate).ToString("0.0", CultureInfo.InvariantCulture);
        }

        static AudioClip Part(string name, float[] data, int from, int to, int rate)
        {
            if (to - from < 16) return null;
            float[] part = new float[to - from];
            Array.Copy(data, from, part, 0, part.Length);
            AudioClip clip = AudioClip.Create(name, part.Length, 1, rate, false);
            clip.SetData(part, 0);
            return clip;
        }

        /// <summary>RIFF/WAVE, PCM 8/16/24/32 bit or 32-bit float, any rate,
        /// channels mixed to mono; the first 'smpl' loop as [a, b), -1 without
        /// one.</summary>
        internal static bool ReadWav(byte[] raw, out float[] data, out int rate, out int loopA, out int loopB)
        {
            data = null;
            rate = 0;
            loopA = loopB = -1;
            if (raw.Length < 12 || Tag(raw, 0) != "RIFF" || Tag(raw, 8) != "WAVE") return false;
            int format = 0, channels = 0, bits = 0, dataAt = -1, dataLen = 0;
            int pos = 12;
            while (pos + 8 <= raw.Length)
            {
                string id = Tag(raw, pos);
                int body = pos + 8;
                int size = BitConverter.ToInt32(raw, pos + 4);
                if (size < 0 || size > raw.Length - body) size = raw.Length - body;
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToUInt16(raw, body);
                    channels = BitConverter.ToUInt16(raw, body + 2);
                    rate = BitConverter.ToInt32(raw, body + 4);
                    bits = BitConverter.ToUInt16(raw, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(raw, body + 24);
                }
                else if (id == "data") { dataAt = body; dataLen = size; }
                else if (id == "smpl" && size >= 60 && loopB < 0 && BitConverter.ToInt32(raw, body + 28) > 0)
                {
                    loopA = BitConverter.ToInt32(raw, body + 44);
                    loopB = BitConverter.ToInt32(raw, body + 48) + 1;     // the file's loop end is inclusive
                }
                pos = body + size + (size & 1);
            }
            int bytes = bits / 8;
            bool pcm = format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
            bool single = format == 3 && bits == 32;
            if (dataAt < 0 || channels < 1 || rate < 8000 || (!pcm && !single)) return false;
            int frames = dataLen / (bytes * channels);
            if (frames <= 0) return false;
            data = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int o = dataAt + (i * channels + c) * bytes;
                    if (single) sum += BitConverter.ToSingle(raw, o);
                    else if (bits == 8) sum += (raw[o] - 128) / 128f;
                    else if (bits == 16) sum += BitConverter.ToInt16(raw, o) / 32768f;
                    else if (bits == 24) sum += (((raw[o] | (raw[o + 1] << 8) | (raw[o + 2] << 16)) << 8) >> 8) / 8388608f;
                    else sum += BitConverter.ToInt32(raw, o) / 2147483648f;
                }
                data[i] = sum / channels;
            }
            return true;
        }

        static string Tag(byte[] b, int at)
        {
            return System.Text.Encoding.ASCII.GetString(b, at, 4);
        }
    }

    // =====================================================================
    // Runway lights

    /// <summary>
    /// The runway edge lights at night for the An-2: a warm glow on every
    /// intact "Edge light" of the props bundle (or at the assembly's 38
    /// positions without it), a real point light on every third. On while
    /// the sun is down and the tower has power (the console lives).
    /// </summary>
    internal static class RunwayLights
    {
        static GameObject _root;
        static readonly List<Light> _lights = new List<Light>();
        static float _nextCheck, _since = -1f;
        static bool _lit, _night;
        static Light _sun;
        static Material _glow;

        internal static void Tick()
        {
            if (!TowerRadar.B(TowerRadar.CfgRunwayLights)) { if (_root != null) Clear(); return; }
            // Q1 perf: the edge-light search is a time-sliced sweep (see
            // SceneSweep); it used to walk every east scene in one frame, every
            // 5 s at night until the lights stood.
            if (_sweep.Active)
            {
                if (!_sweep.Step(1.5, _visit)) return;
                if (_root == null && _night) Build();
            }
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 5f;
            _night = Night();
            if (_root == null && _night)
            {
                if (_since < 0f) _since = Time.time;
                _at.Clear();
                _sweep.Begin("East");
            }
            bool on = _night && TowerRadar.ConsoleAlive;
            if (_root != null && on != _lit)
            {
                _lit = on;
                _root.SetActive(on);
                TowerRadar.Log("runway lights " + (on ? "on" : "off") + ".");
            }
        }

        static readonly SceneSweep _sweep = new SceneSweep();
        static readonly SceneSweep.Visitor _visit = CollectVisit;
        static readonly List<Vector3> _at = new List<Vector3>();

        static bool CollectVisit(Transform t, string name)
        {
            if (!name.StartsWith("Edge light", StringComparison.Ordinal)) return true;
            if (!Broken(t)) _at.Add(t.position);
            return false;
        }

        internal static void Clear()
        {
            _sweep.Cancel();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _lights.Clear();
            _lit = false;
        }

        /// <summary>The sun below the horizon (or the brightest directional
        /// light dim).</summary>
        static bool Night()
        {
            if (_sun == null || !_sun.isActiveAndEnabled)
            {
                _sun = RenderSettings.sun;
                if (_sun == null)
                {
                    Light[] all = UnityEngine.Object.FindObjectsOfType<Light>();
                    float best = -1f;
                    for (int i = 0; i < all.Length; i++)
                        if (all[i].type == LightType.Directional && all[i].intensity > best) { best = all[i].intensity; _sun = all[i]; }
                }
            }
            if (_sun == null) return false;
            return _sun.transform.forward.y > -0.06f || _sun.intensity < 0.2f;
        }

        static void Build()
        {
            if (_since < 0f) _since = Time.time;
            List<Vector3> at = new List<Vector3>(_at);    // filled by the sweep
            if (at.Count == 0)
            {
                if (Time.time - _since < 40f) return;
                // the assembly's rows (airfield_assembly.py 5.1): 1 m outside the pavement
                for (int side = 0; side < 2; side++)
                    for (int i = 0; i < 19; i++)
                    {
                        Vector3 p = new Vector3(side == 0 ? 4575.7f : 4688.3f, 0f, -1600f + 168f * i);
                        float y;
                        if (EastWorld.TerrainHeight(p, out y)) p.y = y;
                        at.Add(p);
                    }
            }
            if (_glow == null) _glow = RadarModel.Unlit("NDR_Runway_Glow", new Color(1f, 0.85f, 0.55f));
            _root = new GameObject("NDR runway lights");
            _root.SetActive(false);
            for (int i = 0; i < at.Count; i++)
            {
                GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Collider c = g.GetComponent<Collider>();
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
                g.name = "glow";
                g.transform.SetParent(_root.transform, false);
                g.transform.position = at[i] + Vector3.up * 0.45f * TowerRadar.K;
                g.transform.localScale = Vector3.one * 0.35f * TowerRadar.K;
                Renderer rr = g.GetComponent<Renderer>();
                rr.sharedMaterial = _glow;
                rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (i % 3 == 0)
                {
                    Light l = g.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = new Color(1f, 0.82f, 0.5f);
                    l.range = 30f;
                    l.intensity = 1.6f;
                    l.shadows = LightShadows.None;
                    _lights.Add(l);
                }
            }
            _lit = false;
            TowerRadar.Log("runway lights: " + at.Count + " edge light(s), " + _lights.Count + " point light(s).");
        }

        static bool Broken(Transform t)
        {
            MeshFilter[] mf = t.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < mf.Length; i++)
            {
                if (mf[i].name.ToLowerInvariant().Contains("broken")) return true;
                if (mf[i].sharedMesh != null && mf[i].sharedMesh.name.ToLowerInvariant().Contains("broken")) return true;
            }
            return false;
        }
    }

    // =====================================================================
    // Network

    /// <summary>
    /// One Photon event (TowerRadar NetworkEventCode, 197), float arrays:
    ///   { 1 state, radar hp, console hp, operator actor, npc operator, mode,
    ///     siren, tier, assigned kind, ax, ay, az }      master to all, 1/s
    ///   { 2 claim, on }                                   client to master
    ///   { 3 hit, part, amount, x, y, z, explosion }       client to master
    ///   { 4 command, cmd, kind, x, y, z }                 client to master
    /// The same reflection path as FlakNet.
    /// </summary>
    internal static class RadarNet
    {
        const int State = 1, ClaimMsg = 2, Hit = 3, Cmd = 4;
        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optType;

        internal static bool Ready { get { return _hooked; } }

        static int Code() { return TowerRadar.CfgEventCode == null ? 197 : TowerRadar.CfgEventCode.Value; }

        static bool Overlaps(int code)
        {
            int drone = RevivalPlugin.CfgDroneEventCode.Value;
            if (code >= drone && code <= drone + 4) return true;
            if (code == RevivalPlugin.CfgTurretEventCode.Value) return true;
            if (RevivalPlugin.CfgAdminEventCode != null && code == RevivalPlugin.CfgAdminEventCode.Value) return true;
            if (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value) return true;
            if (DroneGear.CfgSurvEventCode != null && code >= DroneGear.CfgSurvEventCode.Value
                && code <= DroneGear.CfgSurvEventCode.Value + 3) return true;
            if (PlayerHeli.CfgEventCode != null && code >= PlayerHeli.CfgEventCode.Value
                && code <= PlayerHeli.CfgEventCode.Value + 5) return true;
            if (RevivalTroopInsertion.CfgEventCode != null && code >= RevivalTroopInsertion.CfgEventCode.Value
                && code <= RevivalTroopInsertion.CfgEventCode.Value + 2) return true;
            if (Gepard.CfgEventCode != null && code == Gepard.CfgEventCode.Value) return true;
            if (PlayerAn2.CfgEventCode != null && code >= PlayerAn2.CfgEventCode.Value
                && code <= PlayerAn2.CfgEventCode.Value + 7) return true;
            if (Flak.CfgEventCode != null && code == Flak.CfgEventCode.Value) return true;
            // crocodile, mortar (190..195), Stinger, AP mine
            return code == 164 || (code >= 190 && code <= 195) || code == 196;
        }

        internal static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                int code = Code();
                if (code < 0 || code > 199 || Overlaps(code))
                    throw new Exception("event code " + code + " is outside 0..199 or overlaps another channel");
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) throw new Exception("PhotonNetwork missing");
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                FieldInfo onEvent = AccessTools.Field(photon, "OnEventCall");
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (_raise == null || onEvent == null) throw new Exception("reflection path incomplete");
                MethodInfo mine = typeof(RadarNet).GetMethod("OnPhotonEvent", BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                onEvent.SetValue(null, Delegate.Combine(onEvent.GetValue(null) as Delegate, handler));
                _hooked = true;
                TowerRadar.Log("event code " + code + " hooked.");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("TowerRadar network not hooked - the HQ is local only: " + ex.Message);
            }
        }

        internal static void Send(float[] data)
        {
            if (!_hooked) return;
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] { (byte)Code(), data, true, opts });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar network send: " + ex.Message); }
        }

        internal static void SendState()
        {
            Send(new float[] { State, TowerRadar.RadarHp, TowerRadar.ConsoleHp, TowerRadar.OperatorActor,
                TowerRadar.NpcOperatorUp ? 1f : 0f, TowerRadar.Mode, TowerRadar.SirenOn ? 1f : 0f, TowerRadar.Tier,
                TowerRadar.AssignedKind, TowerRadar.AssignedPos.x, TowerRadar.AssignedPos.y, TowerRadar.AssignedPos.z,
                TowerRadar.ControlSide, AirfieldOwnership.Holder, AirfieldOwnership.Captured ? 1f : 0f });
        }

        internal static void SendClaim(bool on) { Send(new float[] { ClaimMsg, on ? 1f : 0f }); }

        internal static void SendHit(int part, float amount, Vector3 at, bool explosion)
        {
            Send(new float[] { Hit, part, amount, at.x, at.y, at.z, explosion ? 1f : 0f });
        }

        internal static void SendCommand(int cmd, int kind, Vector3 pos)
        {
            Send(new float[] { Cmd, cmd, kind, pos.x, pos.y, pos.z });
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 2) return;
                for (int i = 0; i < f.Length; i++)
                    if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                int kind = Mathf.RoundToInt(f[0]);
                if (kind >= 5 && kind <= 8) { TowerSupport.Receive(f, sender); return; }
                if (kind == AirfieldHold.StateMsg || kind == AirfieldHold.GrantMsg) { AirfieldHold.Receive(f, sender); return; }
                if (kind == State && f.Length >= 12 && AirfieldOwnership.MasterSender(sender))
                {
                    if (f.Length >= 15 && !Crocodile.IsMaster())
                        AirfieldOwnership.Apply(Mathf.RoundToInt(f[13]), f[14] > 0.5f);
                    TowerRadar.OnState(f[1], f[2], Mathf.RoundToInt(f[3]), f[4] > 0.5f, Mathf.Clamp(Mathf.RoundToInt(f[5]), 0, 2),
                        f[6] > 0.5f, Mathf.Clamp(Mathf.RoundToInt(f[7]), 0, 2), Mathf.RoundToInt(f[8]), new Vector3(f[9], f[10], f[11]),
                        f.Length >= 13 ? Mathf.RoundToInt(f[12]) : -1);
                    Flak.DirectOwned(TowerRadar.ControlSide, TowerRadar.Tier);
                }
                else if (kind == ClaimMsg)
                    TowerRadar.OnClaim(sender, f[1] > 0.5f);
                else if (kind == Hit && f.Length >= 7 && Crocodile.IsMaster() && f[6] < 0.5f
                    && Crocodile.PlayerByActor(sender) != null)
                    TowerRadar.ApplyHit(Mathf.Clamp(Mathf.RoundToInt(f[1]), 0, 1), Mathf.Clamp(f[2], 0f, 1f),
                        new Vector3(f[3], f[4], f[5]), f[6] > 0.5f);
                else if (kind == Cmd && f.Length >= 6 && Crocodile.IsMaster())
                    TowerRadar.OnCommand(sender, Mathf.RoundToInt(f[1]), Mathf.RoundToInt(f[2]), new Vector3(f[3], f[4], f[5]));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TowerRadar network receive: " + ex.Message);
            }
        }
    }
}

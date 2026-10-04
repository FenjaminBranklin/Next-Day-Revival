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
//      Walk up, press the turret key (G): the radar view - the whole world
//      map (north up), gun range rings, the sweep line, and a blip
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
            CfgLog = cfg.Bind(S, "ContactLog", true, "Unused since G R1 (the console has no radio log); kept for old config files.");
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
                "Minimum scope radius in metres (x 2.8); radar always covers every corner of the world map.");
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

        internal static float ScopeRangeU
        {
            get
            {
                float configured = Mathf.Max(300f, F(CfgScopeRange, 2000f)) * K;
                if (!EastWorld.Extends) return configured;
                Rect world = EastWorld.Extended;
                Vector3 eye = RadarPos;
                return RadarScopeCore.Coverage(configured, eye.x, eye.z,
                    world.x, world.y, world.width, world.height);
            }
        }

        // ------------------------------------------------------- the places

        /// <summary>C1 (airfield_assembly.py b_c1: the tower at the greybox C1
        /// centre, yaw 0, cab east) and the radar's mast axis (c1_radar.py).</summary>
        internal static readonly Vector2 TowerSpot = new Vector2(4250f, 1335f);
        internal static readonly Vector2 MastSpot = new Vector2(4272.12f, 1335f);
        internal const string RadarName = "C1 radar";
        /// <summary>The console in the tower's frame, metres (x east, z north):
        /// E W1 - INSIDE the cab (the glazed command room, x 4.3..11.5,
        /// z -3.6..3.6) on its measured floor, against the south window
        /// under the antenna mast, its screen facing north into the room
        /// (TowerRoofCore). The cab's own roof (RoofM) carries the antenna.</summary>
        internal static readonly Vector3 ConsoleLocalM = new Vector3(TowerRoofCore.ConsoleX, 0f, TowerRoofCore.ConsoleZ);
        internal const float CabFloorM = 12f;
        internal const float RoofM = TowerRoofCore.CabRoofY;

        // ------------------------------------------------------------- state

        internal static Transform Tower;          // the C1 model (null: its spot)
        internal static float TowerYaw;
        internal static Vector3 TowerBase;        // ground under the tower's origin
        internal static float CabFloorY;
        internal static Transform RadarRoot, Head, ConsoleRoot;
        internal static Renderer Screen;
        internal static Transform KitRoom;

        // Shared by MAN RADAR and the garrison operator. SeatDrop is the
        // measured model-root to seated-hip offset used by the flak crews.
        internal static Vector3 OperatorSeat(bool seated)
        {
            Vector3 at = ConsoleRoot.TransformPoint(new Vector3(0f, .02f, TowerCommandRoomCore.SeatDZ) * K);
            if (seated) {
                float drop = Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value;
                at.y = RadarSeatCore.RootY(at.y, true, at.y + TowerCommandRoomCore.SeatH * K, drop);
            }
            return at;
        }
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
        static GameObject _focusTarget;
        static string _focusFaction;
        static int _focusSide = -1;
        static bool _focusLegacy;
        static float _opUntil;
        static float _nextMaster, _nextBroadcast, _sirenSince = -100f, _lastThreat = -100f;
        static bool _dirty, _wasMaster, _directionSet;
        static readonly List<string> _foreignOrders = new List<string>();   // guns holding another side's orders
        static MercAAPost _mercAtConsole;

        internal static bool RadarAlive { get { return RadarHp > 0f; } }
        internal static bool ConsoleAlive { get { return ConsoleHp > 0f; } }
        internal static bool Working { get { return RadarAlive && ConsoleAlive; } }

        internal static Vector3 RadarPos
        {
            get
            {
                if (Head != null) return Head.TransformPoint(new Vector3(0f, 8f, 0f) * K);
                return TowerPoint(new Vector3(7.9f, RoofM + AirfieldObjectsCore.RadarLiftM, 0f));
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
            bool scoped = false;
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
                    ClearFocus();
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
                scoped = true;
                RadarSiren.Tick();
                RunwayLights.Tick();
                ConsoleLook();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TowerRadar: " + ex);
                // h-u1: the view holds nine native input predicates, the L list
                // and other prompts. Without its own frame (Operate's leave
                // checks) it must not outlive the console.
                if (!scoped)
                {
                    try { RadarScope.Leave("radar tick failed"); }
                    catch (Exception leave) { RadarScope.InView = false; RevivalPlugin.L.LogError("TowerRadar leave: " + leave.Message); }
                }
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
            ClearFocus();
            TowerSupport.WorldEnded();
            AirfieldHold.WorldEnded();           // W Tower 3: the hold state starts over
            RadarScope.Leave(why);
            RadarSiren.Stop();
            RunwayLights.Clear();
            if (RadarRoot != null && !KitRadar) UnityEngine.Object.Destroy(RadarRoot.gameObject);
            if (ConsoleRoot != null) UnityEngine.Object.Destroy(ConsoleRoot.gameObject);
            RadarRoot = Head = ConsoleRoot = null;
            Screen = null;
            KitRoom = null;
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
        static Transform _sKit, _sTower, _sH1, _sRoom;

        static bool FindVisit(Transform t, string name)
        {
            if (_sKit == null && name == RadarName) _sKit = t;
            if (_sRoom == null && name == "C1 room") _sRoom = t;
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
                _sKit = _sTower = _sH1 = _sRoom = null;
                _sweep.Begin("East");
            }
            if (!_sweep.Step(1.5, _visit)) return;     // continues next frame
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) { _tileSince = -1f; return; }

            Transform kit = _sKit, tower = _sTower, h1 = _sH1;
            KitRoom = _sRoom;
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
            AirfieldObjects.CompactRadar(RadarRoot, Head);
            ConsoleRoot = RadarModel.Console(airfield.isLoaded ? airfield : tile);
            Built = RadarRoot != null && ConsoleRoot != null;
            Log(Built ? "HQ built: tower " + (tower != null ? "\"" + tower.name + "\"" : "(spot)") + " base y "
                + TowerBase.y.ToString("0.0", CultureInfo.InvariantCulture) + ", cab floor y "
                + CabFloorY.ToString("0.0", CultureInfo.InvariantCulture) + ", console in the cab at " + ConsoleRoot.position + "."
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
            Vector3 probe = TowerPoint(new Vector3(7.4f, CabFloorM + 1.5f, 0f));
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
            if (Head == null || !CombatLoad.LocalNear(RadarPos, 1500f)) return;
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
            TowerCommandRoom.ScreenState(lit, dead);
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
                else if (npc) Auto(home, "the HQ operator");
            }
            MercAAPost merc = MercAA.Operator;
            if (merc != null) ControlSide = merc.Side;
            if (merc != null && merc != _mercAtConsole) Auto(merc.Side, "a merc");
            _mercAtConsole = merc;
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
            Flak.DirectOwned(ControlSide, tier);

            // Exact identity: never move a lost order to a nearby same-kind aircraft.
            UpdateFocus();

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
            ClearFocus();
            Flak.ClearAirfieldOrders();
            _foreignOrders.Clear();
            ControlSide = home;
            Mode = 0;
            AssignedKind = -1;
            _dirty = true;
            Log("control back with " + SideLabel(home) + ": all guns weapons free.");
        }

        /// <summary>G R1, master: FULL AUTO - the one state every crew that
        /// sits down at the console starts in (HQ operator, merc, player): no
        /// focus, every gun that follows the side engages hostiles by itself.
        /// The console shows exactly one of FULL AUTO / CEASE FIRE / FIRE.</summary>
        static void Auto(int side, string who)
        {
            ClearFocus();
            List<string> ids = Following(side);
            for (int i = 0; i < ids.Count; i++) Flak.WeaponsFree(ids[i]);
            Mode = 0;
            AssignedKind = -1;
            _dirty = true;
            Log("FULL AUTO: " + who + " at the console, " + ids.Count + " gun(s) of " + SideLabel(side) + " engage by themselves.");
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
            return "RADAR HQ (" + SideLabel(ControlSide) + "): " + (AssignedKind >= 0 || Mode == 2 ? "FIRE ON THE RADAR TRACK"
                : Mode == 1 ? "CEASE FIRE" : "FULL AUTO");
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
                    ClearFocus();
                    ControlSide = HomeSide();
                    Mode = 0;
                    AssignedKind = -1;
                    _dirty = true;
                }
                // G R1: a player who may give orders sits down = FULL AUTO.
                if (entering && Obeyed(actor)) OnCommand(actor, CmdFree, -1, Vector3.zero);
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

        internal const int CmdEngage = 1, CmdFree = 2, CmdHold = 3, CmdClear = 4, CmdFocus = 5;

        static void ClearFocus()
        {
            Flak.ClearRadarFocus(_focusTarget);
            _focusTarget = null;
            _focusFaction = null;
            _focusSide = -1;
            _focusLegacy = false;
            AssignedKind = -1;
            if (Mode == 2) Mode = 0;
        }

        // Existing master 2 Hz tick; no collections, physics or text construction.
        static void UpdateFocus()
        {
            if (AssignedKind < 0) return;
            GepardGun.Contact c = null;
            List<GepardGun.Contact> air = RadarScope.Air;
            for (int i = 0; i < air.Count; i++)
                if (_focusTarget != null && air[i].Go == _focusTarget) { c = air[i]; break; }
            if (ControlSide != _focusSide || !CanFocus(c, _focusFaction))
            {
                ClearFocus();
                _dirty = true;
                return;
            }
            AssignedPos = c.Pos;
            if (!_focusLegacy) Flak.FocusRadar(ControlSide, c);
        }

        static bool CanFocus(GepardGun.Contact c, string side)
        {
            float range = ScopeRangeU;
            return Working && c != null && c.Go != null && RadarShadow.Visible(c.Go)
                && RadarShadow.Height(c.Go) >= Mathf.Max(0f, F(CfgMinHeight, 3f)) * K
                && (c.Pos - RadarPos).sqrMagnitude <= range * range
                && AirDefencePolicy.Hostile(side, AirPilot.Faction(c));
        }

        /// <summary>A console command (master): only from the claimed operator
        /// and filtered by each gun's persistent faction.</summary>
        internal static void OnCommand(int actor, int cmd, int kind, Vector3 pos)
        {
            if (!Crocodile.IsMaster()) { RadarNet.SendCommand(cmd, kind, pos); return; }
            if (!SupportLease(actor) || Crocodile.ActorDown(actor)) return;
            if (cmd < CmdEngage || cmd > CmdFocus) return;
            if (!Obeyed(actor))
            {
                Log("command " + cmd + " from actor " + actor + " refused: the HQ operator holds the tower for "
                    + SideLabel(HomeSide()) + ".");
                return;
            }
            int side = PlayerSide(actor), home = HomeSide();
            GepardGun.Contact focus = null;
            if (cmd == CmdEngage || cmd == CmdFocus)
            {
                if (!Working) return;
                focus = RadarScope.Resolve(kind, pos, 120f);
                if (!CanFocus(focus, Fraktion.Spielerseite(Crocodile.PlayerByActor(actor)))) return;
            }
            if (AirfieldOwnership.Take(actor))
            {
                home = HomeSide();
                ClearFocus();
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
                ClearFocus();
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
                        // Preserve the legacy ENGAGE wire/API for existing callers.
                        ClearFocus();
                        GepardGun.Contact c = focus;
                        for (int i = 0; i < ids.Count; i++)
                        {
                            Flak.AssignTarget(ids[i], c.Go);
                            Flak.TrackRadarTarget(ids[i], c.Go);
                        }
                        if (Mode == 1)
                        {
                            for (int i = 0; i < ids.Count; i++) Flak.AssignedOnly(ids[i]);
                            Mode = 2;
                        }
                        _focusTarget = c.Go;
                        _focusLegacy = true;
                        _focusSide = side;
                        _focusFaction = Fraktion.Spielerseite(Crocodile.PlayerByActor(actor));
                        AssignedKind = c.Kind;
                        AssignedPos = c.Pos;
                        break;
                    }
                case CmdFocus:
                    {
                        ClearFocus();
                        for (int i = 0; i < ids.Count; i++) Flak.WeaponsFree(ids[i]);
                        Mode = 0;
                        _focusTarget = focus.Go;
                        _focusSide = side;
                        _focusFaction = Fraktion.Spielerseite(Crocodile.PlayerByActor(actor));
                        AssignedKind = focus.Kind;
                        AssignedPos = focus.Pos;
                        Flak.FocusRadar(side, focus);
                        break;
                    }
                case CmdFree:
                    ClearFocus();
                    for (int i = 0; i < ids.Count; i++) Flak.WeaponsFree(ids[i]);
                    Mode = 0;
                    break;
                case CmdHold:
                    ClearFocus();
                    for (int i = 0; i < ids.Count; i++) { Flak.HoldFire(ids[i]); Flak.ClearTarget(ids[i]); }
                    AssignedKind = -1;
                    Mode = 1;
                    break;
                case CmdClear:
                    ClearFocus();
                    for (int i = 0; i < ids.Count; i++) Flak.ClearTarget(ids[i]);
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

        internal static Material Pick(Transform root, string part)
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

        /// <summary>The radar console in the tower's cab: cabinet, sloped
        /// desk, the round PPI screen facing north, knobs, a telephone, the
        /// operator's chair (he faces the screen and the south window).
        /// Frame: +z = the screen's facing (north).</summary>
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

                // G R2: the shared room recipe owns the visible console and
                // chair. Keep only this interaction/damage/navigation volume.
                BoxCollider bc = root.AddComponent<BoxCollider>();
                bc.center = new Vector3(0f, 0.8f, -0.05f) * K;
                bc.size = new Vector3(1.3f, 1.6f, 0.7f) * K;
                // E W1: the cab's baked NavMesh walks the operator to the
                // chair; the desk carves it like the room's furniture.
                NavMeshObstacle carve = root.AddComponent<NavMeshObstacle>();
                carve.shape = NavMeshObstacleShape.Box;
                carve.center = bc.center;
                carve.size = bc.size;
                carve.carving = true;
                carve.carveOnlyStationary = true;
                TowerCommandRoom.Attach(root.transform);
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
                float s = AirfieldObjectsCore.RadarScale;
                _parts.Add(P(TowerRadar.RadarRoot, new Vector3(0f, (1.5f-(3f-8f*s))/s, 0f),
                    new Vector3(0.12f/s, 1.5f/s, 0.12f/s), 0));
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
        internal static Component Man { get { return _man; } }

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

        /// <summary>Every client, late: the man on the chair facing the screen.
        /// H T2 (RadarSeatCore): the seat owns him every frame he is up, as
        /// the flak seats do (FlakCrew.Seat) - pose sampled after the
        /// animator, aim IK down, root and rotation on the anchor; the agent
        /// park, AI pause and rifle renew at 5 Hz. Down or dead, his root
        /// goes back onto the cab floor at once (killable, never under it).</summary>
        static float _nextHold;
        static bool _held;
        static Component _heldMan;
        internal static void Hold()
        {
            Component ai = _man;
            if (ai == null || TowerRadar.ConsoleRoot == null) { _held = false; _heldMan = null; return; }
            if (!ReferenceEquals(ai, _heldMan)) { _heldMan = ai; _held = false; }
            float now = Time.time;
            bool full = now >= _nextHold;
            if (full) _nextHold = now + RadarSeatCore.FullEvery;
            Transform tr = ai.transform;
            bool up = Flak.Up(ai);
            Vector3 floor = TowerRadar.OperatorSeat(false);
            int act = RadarSeatCore.Decide(up, up || (full && GepardCrew.Steht(ai)), _held, full, tr.position.y, floor.y);
            if (act == RadarSeatCore.Lift) { Lift(ai, tr, floor); return; }
            if (act != RadarSeatCore.Hold) return;
            bool sit = TowerRadar.B(TowerRadar.CfgSeated);
            Vector3 at = sit ? TowerRadar.OperatorSeat(true) : floor;
            if (!full && !FlakCrew.NearCamera(at)) return;
            _held = true;
            // G C1 on the console chair: his aim IK stays down so it cannot
            // bend the sampled seat pose.
            if (sit) { int foreign = GunSeatPose.Hold(ai); if (foreign != 0) GunSeatPose.Note(foreign); }
            else if (full) GunSeatPose.Release(ai);
            Vector3 fwd = -TowerRadar.ConsoleRoot.forward;
            fwd.y = 0f;
            Quaternion rot = fwd.sqrMagnitude < 1e-6f ? tr.rotation : Quaternion.LookRotation(fwd.normalized, Vector3.up);
            if (full) GepardCrew.Parken(ai);
            float away = (tr.position - at).sqrMagnitude;
            if (full && away > 64f) Warp(ai, at);
            if (RadarSeatCore.Move(away)) tr.position = at;
            tr.rotation = rot;
            if (full) GepardCrew.Ruhig(ai);
            if (sit) TechnicalCrew.Sitzen(ai, 0);
            if (full) TechnicalCrew.Unbewaffnet(ai);     // he works the console, no rifle
        }

        /// <summary>Off the seat (wounded, dead) or found under the floor:
        /// the root onto the chair's floor point. A living man's agent is
        /// put there and handed back to the game (the wounded state is the
        /// game's own); a body is only moved.</summary>
        static void Lift(Component ai, Transform tr, Vector3 floor)
        {
            bool held = _held;
            _held = false;
            GunSeatPose.Release(ai);
            tr.position = floor;
            if (!GepardCrew.Steht(ai)) return;
            NavMeshAgent agent = GepardCrew.Agent(ai);
            if (agent == null) return;
            try
            {
                if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    if (agent.Warp(floor) && held) { agent.updatePosition = true; agent.updateRotation = true; }
                }
            }
            catch { }
            if (Time.time < _liftSaid) return;
            _liftSaid = Time.time + 30f;
            TowerRadar.Log(held ? "the NPC operator is down: off the chair onto the cab floor."
                : "the downed NPC operator was under the cab floor: put back on it.");
        }
        static float _liftSaid;

        static void Warp(Component ai, Vector3 at)
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
            public int Kind, Type;
            public float Radius;
            public Vector3 Pos, Vel;          // as painted
            public float PaintedAt = -100f, Height;
            public int Iff;                   // 1 friend, -1 foe, 0 unknown
            public bool Lost;
        }

        static readonly List<GepardGun.Contact> _air = new List<GepardGun.Contact>();
        /// <summary>The radar's contacts (every client, 4 Hz, followed every
        /// frame): W-Tower1's air picture reads them.</summary>
        internal static List<GepardGun.Contact> Air { get { return _air; } }
        internal static float SampleAt = -1000f;
        static readonly List<Blip> _blips = new List<Blip>();
        static int _nextId = 1;
        static float _nextCollect, _lastFollow, _lastSweep = -1f;
        static string _mine;
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

        /// <summary>G R1: the console has no radio log any more; the rare
        /// events (operator, siren, damage) go to the plugin log only.</summary>
        internal static void Note(string s)
        {
            TowerRadar.Log(s);
        }

        // ----------------------------------------------------------- tick

        internal static void Tick()
        {
            float now = Time.time;
            Vector3 eye = TowerRadar.RadarPos;
            bool collected = now >= _nextCollect;
            // The manned network and map also need live coordinates when nobody
            // opens the console. Follow BEFORE the terrain/height snapshot.
            if (InView || now >= _nextCollect)
            {
                FlakFire.FollowAll(_air, now - _lastFollow, 5f);
                _lastFollow = now;
            }
            if (now >= _nextCollect)
            {
                _nextCollect = now + 0.25f;
                _mine = MySide();
                FlakFire.Collect(_air, eye, TowerRadar.ScopeRangeU);
                RadarShadow.Scan(_air, eye);
                SampleAt = now;
                Guns();
                if (InView) ScopeArt(); // Bake only on entry, resize or a new range.
            }
            if (InView)
            {
                Sweep(eye);
                if (collected) Rows(); // Include newly painted tracks in this snapshot.
            }
            Operate();
        }

        /// <summary>The sweep paints what it passes: a working radar only,
        /// airborne contacts only (below MinContactHeight is ground clutter).</summary>
        static void Sweep(Vector3 eye)
        {
            float a1 = TowerRadar.AntennaAngle;
            float a0 = _lastSweep < 0f ? a1 : _lastSweep;
            // Opening the console shows the existing picture immediately;
            // subsequent paints still follow the antenna, including wraparound.
            float span = _lastSweep < 0f ? 360f : Mathf.Repeat(a1 - a0, 360f);
            _lastSweep = a1;
            bool working = TowerRadar.Working;
            float range = TowerRadar.ScopeRangeU;
            string mine = _mine;
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
                b.Type = AirPicturePolicy.Type(c.Kind, c.Kind == 6 && NpcAircraft.IsTu95(c.Go));
                b.Radius = c.Radius;
                b.Pos = c.Pos;
                b.Vel = c.Vel;
                b.Height = h;
                b.PaintedAt = Time.time;
                b.Lost = false;
                b.Iff = Iff(c, mine);
            }
            // a track not painted for two sweeps is lost
            float lostAfter = TowerRadar.SweepSeconds * 2.2f;
            for (int i = _blips.Count - 1; i >= 0; i--)
            {
                Blip b = _blips[i];
                if (Time.time - b.PaintedAt > lostAfter && working)
                {
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
            Flak.FillGuns(_gunSnapshot);
            _guns.Clear();
            _guns.AddRange(_gunSnapshot);
        }

        /// <summary>B3: 0 fine or garrison/unknown, 1 low, 2 empty.</summary>
        static int Ammo(FlakGunInfo g) { return FlakAmmoStock.Level(g.Rounds, g.ShortRange); }

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
            _lastSweep = -1f;    // show the existing picture on entry
            WarmNumbers();
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
            if (cmd == TowerRadar.CmdEngage || cmd == TowerRadar.CmdFocus)
            {
                if (!TowerRadar.Working || _selected == null || _selected.Go == null || !RadarShadow.Visible(_selected.Go)
                    || Time.time - _selected.PaintedAt > TowerRadar.SweepSeconds * 2.2f)
                { Hint(RadarFireText.SelectFirst, 2f); return; }
                if (_selected.Iff > 0) { Hint(RadarFireText.Friendly, 3f); return; }
                GepardGun.Contact c = null;
                for (int i = 0; i < _air.Count; i++) if (_air[i].Go == _selected.Go) { c = _air[i]; break; }
                pos = c != null ? c.Pos : _selected.Pos;
                kind = _selected.Kind;
            }
            int mine = Crocodile.LocalActor();
            if (!TowerRadar.Obeyed(mine)) { Hint(RadarFireText.Refused, 4f); return; }
            if (_gunRows.Count == 0) Hint(RadarFireText.NoFollow, 4f);
            VanillaUi.Sound("Click");
            TowerRadar.OnCommand(mine, cmd, kind, pos);
        }

        // --------------------------------------------------------- drawing
        //
        // G R1: the green round PPI scope and, beside it, only FULL AUTO,
        // CEASE FIRE, FIRE (on the clicked blip) and a compact gun list - in
        // the game's own art: the warning_02_empty window form and MarkerInfo
        // plate, the btn / btn_hover brush buttons with the UIButton tints,
        // Bebas captions and Roboto rows (VanillaUi). One state shows: the lit
        // button. The console keeps its virtual cursor (the game holds the
        // mouse), so hover and clicks test _cursor. The scope art (disc,
        // range rings, bearing ticks, afterglow) is baked once per size at
        // the 4 Hz collect; a frame draws cached textures and table strings.

        const int AutoButton = 0, CeaseButton = 1, FireButton = 2;
        static Texture2D _white, _disc, _glow;
        static Color32[] _discPixels;
        static int _discSize, _discKey = -1;
        static readonly Color Phosphor = new Color(0.45f, 1f, 0.55f, 1f);
        static readonly Color FriendBlip = new Color(0.55f, 1f, 0.7f, 1f);
        static float _cx, _cy, _r, _u = 1f, _range = 1f;
        static Vector3 _eye;
        static Rect _column, _plate;
        static readonly Rect[] _buttons = new Rect[3];
        static readonly string[] CrewNames = { "0/2", "1/2", "2/2" };
        static GUIStyle _head, _body;

        /// <summary>A gun line of the console, rebuilt at 4 Hz: only the guns
        /// that take this console's orders.</summary>
        sealed class GunRow
        {
            public string Id, Rounds;
            public int Crew, Ammo;
            public bool Dead;
            public Vector3 Pos;
        }

        static readonly List<FlakGunInfo> _gunSnapshot = new List<FlakGunInfo>();
        static readonly List<FlakGunInfo> _guns = new List<FlakGunInfo>();   // the 4 Hz snapshot
        static readonly List<GunRow> _gunRows = new List<GunRow>();
        static int _gunCount;
        static readonly UiMemo _promptText = new UiMemo();
        static bool _obeyed;
        static int _who = -1;

        // Cached number texts: one string per value for the session.
        static readonly string[] _id2 = new string[100];

        static string Id2(int v) { v = Mathf.Clamp(v, 0, 99); return _id2[v] ?? (_id2[v] = v.ToString("00", CultureInfo.InvariantCulture)); }

        // Cold console entry only: numeric labels never allocate during motion.
        static bool _numbersWarm;
        static void WarmNumbers()
        {
            if (_numbersWarm) return;
            _numbersWarm = true;
            for (int i = 0; i < 1000; i++) { UiNum.Of(i); if (i < 100) Id2(i); }
            for (int i = 0; i < 6; i++) AirPicture.Icon(i);
        }

        static void Ensure()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_head == null)
            {
                _head = new GUIStyle();
                _head.clipping = TextClipping.Clip;
                _head.normal.textColor = Color.white;
                _body = new GUIStyle(_head);
            }
        }

        /// <summary>At the 4 Hz collect, only while the local player is at the
        /// console: the guns that take this console's orders.</summary>
        static void Rows()
        {
            int me = Crocodile.LocalActor();
            _obeyed = TowerRadar.Obeyed(me);
            _who = _obeyed ? TowerRadar.PlayerSide(me) : TowerRadar.ControlSide;
            _gunCount = 0;
            for (int i = 0; i < _guns.Count; i++)
            {
                FlakGunInfo g = _guns[i];
                if (!TowerRadar.Follows(g, _who)) continue;
                if (_gunRows.Count <= _gunCount) _gunRows.Add(new GunRow());
                GunRow row = _gunRows[_gunCount++];
                row.Id = g.Id;
                row.Pos = g.Position;
                row.Crew = Mathf.Clamp(g.CrewAlive, 0, 2);
                row.Rounds = g.Rounds == -1 ? "--" : g.Rounds == -2 ? "?" : UiNum.Of(Mathf.Max(0, g.Rounds));
                row.Ammo = Ammo(g);
                row.Dead = g.Health <= 0f;
            }
            if (_selected != null && (!Shown(_selected) || !_blips.Contains(_selected))) _selected = null;
        }

        internal static void Draw()
        {
            if (TowerSupport.Selecting) return;
            try
            {
                if (!InView && !_near && (_hint == null || Time.time > _hintUntil)) return;
                if (Event.current.type != EventType.Repaint) return;
                Ensure();
                float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                if (!InView)
                {
                    if (_near)
                    {
                        int kk = (int)Key();
                        kk += 1024 * Loc.Lang();
                        Prompt(h * 0.62f, _nearWhy ?? (_promptText.Stale(kk) ? _promptText.Set(kk, "[" + Key() + "] " + RadarFireText.Prompt) : _promptText.Text));
                    }
                    if (_hint != null && Time.time <= _hintUntil) Prompt(h * 0.66f, _hint);
                    return;
                }
                // The game's popup dimmer, darker: the scope reads at night.
                GUI.color = new Color(0f, 0f, 0f, 0.86f);
                GUI.DrawTexture(new Rect(0f, 0f, w, h), _white);
                Layout(w, h);
                _eye = TowerRadar.RadarPos;
                Scope();
                Column();
                // the cursor
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(_cursor.x - 9f, _cursor.y - 1f, 18f, 2f), _white);
                GUI.DrawTexture(new Rect(_cursor.x - 1f, _cursor.y - 9f, 2f, 18f), _white);
                if (_hint != null && Time.time <= _hintUntil) Prompt(h - 72f * _u, _hint);
                GUI.color = Color.white;
            }
            catch { GUI.matrix = Matrix4x4.identity; GUI.color = Color.white; }
        }

        /// <summary>The vanilla hint plate across the screen's middle.</summary>
        static void Prompt(float y, string text)
        {
            VanillaUi.Prompt(text, y);
        }

        /// <summary>The scope beside one vanilla-sized column (u = H / 720,
        /// the native HUD unit). The column is a 230 u button wide - the
        /// game's own btn - the scope takes all the remaining height.</summary>
        static void Layout(float w, float h)
        {
            float u = h / 720f;
            float col = 254f * u, gap = 18f * u;
            float r = Mathf.Min(h * 0.45f, (w - col - gap * 3f) * 0.5f);
            r = Mathf.Max(r, 40f);
            float x0 = Mathf.Max(gap, (w - (r * 2f + gap * 2f + col)) * 0.5f);
            _u = u;
            _r = r;
            _cx = x0 + r;
            _cy = h * 0.5f;
            _plate = new Rect(_cx - r - 10f * u, _cy - r - 10f * u, r * 2f + 20f * u, r * 2f + 20f * u);
            _column = new Rect(_plate.xMax + gap, _plate.y, col, _plate.height);
            OrderLayout(_column.x + 12f * u, _column.y + 18f * u, col - 24f * u, u);
        }

        /// <summary>FULL AUTO, CEASE FIRE, then the big FIRE: the native
        /// button's 230 x 60 u at most.</summary>
        static void OrderLayout(float x, float y, float width, float u)
        {
            _buttons[AutoButton] = new Rect(x, y, width, 44f * u);
            _buttons[CeaseButton] = new Rect(x, _buttons[AutoButton].yMax + 8f * u, width, 44f * u);
            _buttons[FireButton] = new Rect(x, _buttons[CeaseButton].yMax + 18f * u, width, 60f * u);
        }

        static Vector2 ToScreen(Vector3 world)
        {
            float s = _r / _range;
            return new Vector2(_cx + (world.x - _eye.x) * s, _cy - (world.z - _eye.z) * s);
        }

        static bool Shown(Blip b)
        {
            return TowerRadar.Working && b.Go != null && RadarShadow.Visible(b.Go)
                && Time.time - b.PaintedAt <= TowerRadar.SweepSeconds * 2.2f;
        }

        // ----------------------------------------------------- the scope art

        /// <summary>4 Hz, at the console only: bake the disc for this size and
        /// range (rings every 500 m, bearing ticks every 10 degrees) and the
        /// afterglow wedge once. A rebuild only on entry, resize or a new range.</summary>
        static void ScopeArt()
        {
            Layout(UnityEngine.Screen.width, UnityEngine.Screen.height);
            _range = Mathf.Max(1f, TowerRadar.ScopeRangeU);
            int n = Mathf.Clamp(Mathf.RoundToInt(_r * 2f), 64, 2048);
            int key = n * 100003 + Mathf.RoundToInt(_range / K);
            if (_disc == null || key != _discKey)
            {
                _discKey = key;
                BakeDisc(n, 500f * K / _range);
                _discSize = n;
            }
            if (_glow == null) _glow = BakeGlow(256);
        }

        /// <summary>The PPI face: dark phosphor, ring every <paramref name="ring"/>
        /// of the radius (brighter each 1000 m), ticks every 10 / 30 degrees,
        /// faint cross. One quadrant is computed and mirrored (all marks are
        /// symmetric about both axes).</summary>
        static void BakeDisc(int n, float ring)
        {
            if (_discPixels == null || _discPixels.Length != n * n) _discPixels = new Color32[n * n];
            Color32[] px = _discPixels;
            float half = n * 0.5f, pxR = half;
            int h = (n + 1) / 2;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < h; x++)
                {
                    float dx = half - (x + 0.5f), dy = half - (y + 0.5f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy), rho = d / pxR;
                    Color32 c;
                    if (rho > 1f) c = new Color32(0, 0, 0, 0);
                    else
                    {
                        float a = Mathf.Clamp01((1f - rho) * pxR);           // 1 px soft edge
                        float g = 0.07f + 0.06f * (1f - rho);                 // glass, brighter centre
                        float r = 0.012f, b = 0.03f, mark = 0f;
                        // the range rings
                        float k = rho / ring, nearest = Mathf.Round(k);
                        if (nearest >= 1f && Mathf.Abs(k - nearest) * ring * pxR < 0.8f)
                            mark = ((int)nearest % 2 == 0) ? 0.42f : 0.26f;
                        // the bezel
                        if (d > pxR - 2.5f) mark = Mathf.Max(mark, 0.5f);
                        // bearing ticks
                        float bearing = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                        float deg = Mathf.Repeat(bearing + 0.5f, 10f) - 0.5f;   // offset from the nearest 10 degrees
                        bool major = Mathf.Abs(Mathf.Repeat(bearing + 1f, 30f) - 1f) < 1f;
                        float len = major ? 14f : 7f;
                        if (d > pxR - 2.5f - len && Mathf.Abs(deg) * Mathf.Deg2Rad * d < 0.8f) mark = Mathf.Max(mark, 0.55f);
                        // the cross through the antenna
                        if ((Mathf.Abs(dx) < 0.6f || Mathf.Abs(dy) < 0.6f) && rho > 0.02f) mark = Mathf.Max(mark, 0.16f);
                        g += mark * 0.75f;
                        r += mark * 0.3f;
                        b += mark * 0.32f;
                        c = new Color32((byte)(Mathf.Clamp01(r) * 255f), (byte)(Mathf.Clamp01(g) * 255f),
                            (byte)(Mathf.Clamp01(b) * 255f), (byte)(a * 255f));
                    }
                    int mx = n - 1 - x, my = n - 1 - y;
                    px[y * n + x] = c; px[y * n + mx] = c; px[my * n + x] = c; px[my * n + mx] = c;
                }
            if (_disc != null && (_disc.width != n || _disc.height != n)) { UnityEngine.Object.Destroy(_disc); _disc = null; }
            if (_disc == null)
            {
                _disc = new Texture2D(n, n, TextureFormat.ARGB32, false);
                _disc.name = "NDR radar PPI";
                _disc.wrapMode = TextureWrapMode.Clamp;
            }
            _disc.SetPixels32(px);
            _disc.Apply(false);
        }

        /// <summary>The afterglow behind a sweep pointing north: 40 degrees
        /// counter-clockwise, fading out; drawn rotated with the antenna.</summary>
        static Texture2D BakeGlow(int n)
        {
            Color32[] px = new Color32[n * n];
            float half = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;   // texture y up = screen up
                    float rho = Mathf.Sqrt(dx * dx + dy * dy) / half;
                    float behind = Mathf.Repeat(-Mathf.Atan2(dx, dy) * Mathf.Rad2Deg, 360f);
                    float a = rho > 1f || behind > 40f ? 0f : (1f - behind / 40f) * (1f - behind / 40f) * 0.45f
                        * Mathf.Clamp01((1f - rho) * half);
                    px[y * n + x] = new Color32(90, 255, 120, (byte)(a * 255f));
                }
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            t.name = "NDR radar afterglow";
            t.wrapMode = TextureWrapMode.Clamp;
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        // -------------------------------------------------------- the scope

        static void Scope()
        {
            float u = _u;
            VanillaUi.Panel(_plate, "MarkerInfo");
            Rect face = new Rect(_cx - _r, _cy - _r, _r * 2f, _r * 2f);
            GUI.color = Color.white;
            if (_disc != null) GUI.DrawTexture(face, _disc);
            Text(new Rect(_cx - 20f * u, face.y + 6f * u, 40f * u, 18f * u), RadarFireText.North, true, 16f, TextAnchor.MiddleCenter, Phosphor);
            if (!TowerRadar.Working)
            {
                Text(new Rect(face.x, _cy - 16f * u, face.width, 32f * u),
                    !TowerRadar.RadarAlive ? RadarScopeText.NoAntenna : RadarScopeText.NoConsole,
                    true, 24f, TextAnchor.MiddleCenter, VanillaUi.Red);
                return;
            }
            // the sweep: one baked afterglow and the bright line
            if (TowerRadar.B(TowerRadar.CfgSweep))
            {
                float ang = TowerRadar.AntennaAngle;
                Matrix4x4 keep = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, new Vector2(_cx, _cy));
                GUI.color = Color.white;
                if (_glow != null) GUI.DrawTexture(face, _glow);
                GUI.matrix = keep;
                float a = ang * Mathf.Deg2Rad;
                GUI.color = Phosphor;
                Line(_cx, _cy, _cx + Mathf.Sin(a) * _r, _cy - Mathf.Cos(a) * _r, Mathf.Max(1.5f, 2f * u));
            }
            // the guns of this console: a small mark, gold when low, red when empty
            float r2 = _r * _r;
            for (int i = 0; i < _gunCount; i++)
            {
                GunRow g = _gunRows[i];
                Vector2 p = ToScreen(g.Pos);
                if ((p - new Vector2(_cx, _cy)).sqrMagnitude > r2) continue;
                Color c = g.Dead || g.Ammo == 2 ? VanillaUi.Red : g.Ammo == 1 ? VanillaUi.Gold : VanillaUi.Grey;
                GUI.color = c;
                float s = Mathf.Max(3f, 4f * u);
                GUI.DrawTexture(new Rect(p.x - s * 0.5f, p.y - s * 0.5f, s, s), _white);
                Text(new Rect(p.x + s, p.y - 2f * u, 60f * u, 14f * u), g.Id, false, 10f, TextAnchor.UpperLeft, c);
            }
            // the blips
            float sweep = TowerRadar.SweepSeconds;
            bool fade = TowerRadar.B(TowerRadar.CfgFade);
            float size = Mathf.Round(20f * u);
            for (int i = 0; i < _blips.Count; i++)
            {
                Blip b = _blips[i];
                if (!Shown(b)) continue;
                Vector2 p = ToScreen(b.Pos);
                if ((p - new Vector2(_cx, _cy)).sqrMagnitude > r2) continue;
                float age = Time.time - b.PaintedAt;
                Color c = IffColor(b.Iff);
                c.a = fade ? Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(1f - age / (sweep * 1.05f))) : 1f;
                GUI.color = c;
                // Rotate the nose with the painted velocity; north is zero.
                Matrix4x4 keep = GUI.matrix;
                if (b.Vel.x * b.Vel.x + b.Vel.z * b.Vel.z > 1f)
                    GUIUtility.RotateAroundPivot(RadarScopeCore.Heading(b.Vel.x, b.Vel.z), p);
                GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), AirPicture.Icon(b.Type));
                GUI.matrix = keep;
                Text(new Rect(p.x + size * 0.5f + 3f * u, p.y - 8f * u, 30f * u, 16f * u), Id2(b.Id), false, 12f, TextAnchor.MiddleLeft, c);
                if (b == _selected)
                {
                    float extent = size * 0.5f + 5f * u;
                    Bracket(p.x, p.y, extent, Color.black);
                    Bracket(p.x, p.y, extent - 1f, Color.white);
                }
                if (TowerRadar.AssignedKind >= 0 && b.Kind == TowerRadar.AssignedKind
                    && (b.Pos - TowerRadar.AssignedPos).sqrMagnitude < 150f * 150f)
                    Bracket(p.x, p.y, size * 0.5f + 9f * u, VanillaUi.Red);
            }
            GUI.color = Color.white;
        }

        static Color IffColor(int iff) { return iff > 0 ? FriendBlip : iff < 0 ? VanillaUi.Red : VanillaUi.Gold; }

        // ------------------------------------------------------- the column

        /// <summary>The window form beside the scope: the three orders (the lit
        /// one is the state the guns are in) and the gun list.</summary>
        static void Column()
        {
            float u = _u;
            // The window form wraps its content, like the game's own dialogs.
            float row = 24f * u, list = _buttons[FireButton].yMax + 22f * u + row * (1 + Mathf.Max(1, _gunCount)) + 14f * u;
            VanillaUi.Panel(new Rect(_column.x, _column.y, _column.width, Mathf.Min(_column.height, list - _column.y)), "warning_02_empty");
            bool focus = TowerRadar.AssignedKind >= 0 || TowerRadar.Mode == 2;
            bool cease = !focus && TowerRadar.Mode == 1;
            bool auto = !focus && !cease;
            bool live = TowerRadar.Working;
            ConsoleButton(_buttons[AutoButton], RadarFireText.Auto, 20f, _obeyed && live, auto && live);
            ConsoleButton(_buttons[CeaseButton], RadarFireText.Cease, 20f, _obeyed && live, cease && live);
            Blip sel = _selected;
            bool canFire = _obeyed && live && sel != null && sel.Iff <= 0 && Shown(sel);
            Rect fire = _buttons[FireButton];
            ConsoleButton(fire, RadarFireText.Fire, 30f, canFire, focus && live);
            if (sel != null && Shown(sel))
                Text(new Rect(fire.xMax - 52f * u, fire.y, 40f * u, fire.height), Id2(sel.Id), true, 20f,
                    TextAnchor.MiddleRight, IffColor(sel.Iff));

            // the guns: name, crew x/2, rounds (EMPTY red, LOW gold)
            float x = fire.x, width = fire.width, y = fire.yMax + 22f * u;
            float crewX = x + width * 0.45f, roundsX = x + width * 0.62f, roundsW = width * 0.38f - 6f * u;
            Text(new Rect(x + 6f * u, y, width * 0.4f, row), RadarFireText.Gun, true, 15f, TextAnchor.MiddleLeft, VanillaUi.Grey);
            Text(new Rect(crewX, y, width * 0.17f, row), RadarFireText.Crew, true, 15f, TextAnchor.MiddleCenter, VanillaUi.Grey);
            Text(new Rect(roundsX, y, roundsW, row), RadarFireText.Rounds, true, 15f, TextAnchor.MiddleRight, VanillaUi.Grey);
            y += row;
            if (_gunCount == 0)
                Text(new Rect(x + 6f * u, y, width - 12f * u, row), RadarFireText.NoGuns, false, 13f, TextAnchor.MiddleLeft, VanillaUi.Grey);
            float bottom = _column.yMax - 14f * u;
            for (int i = 0; i < _gunCount && y + row <= bottom; i++)
            {
                GunRow g = _gunRows[i];
                Rect r = new Rect(x, y, width, row);
                if (i % 2 == 0) VanillaUi.Panel(r, "groupPlayerWhite", new Color(1f, 1f, 1f, 0.07f));
                Text(new Rect(x + 6f * u, y, width * 0.4f, row), g.Id, false, 14f, TextAnchor.MiddleLeft, VanillaUi.White);
                Text(new Rect(crewX, y, width * 0.17f, row), CrewNames[g.Crew], false, 14f, TextAnchor.MiddleCenter,
                    g.Crew == 0 ? VanillaUi.Red : VanillaUi.White);
                Rect rr = new Rect(roundsX, y, roundsW, row);
                if (g.Dead) Text(rr, RadarFireText.Destroyed, true, 15f, TextAnchor.MiddleRight, VanillaUi.Red);
                else if (g.Ammo == 2) Text(rr, RadarFireText.Empty, true, 16f, TextAnchor.MiddleRight, VanillaUi.Red);
                else
                {
                    Text(rr, g.Rounds, false, 14f, TextAnchor.MiddleRight, g.Ammo == 1 ? VanillaUi.Gold : VanillaUi.White);
                    if (g.Ammo == 1)
                        Text(new Rect(roundsX, y, roundsW - 40f * u, row), RadarFireText.Low, true, 15f, TextAnchor.MiddleRight, VanillaUi.Gold);
                }
                y += row;
            }
        }

        /// <summary>The game's btn brush: btn_hover (red) lit for the active
        /// order, the UIButton tints for hover / pressed / disabled, native
        /// caps kept at their aspect. Without the art a plain dark plate.</summary>
        static void ConsoleButton(Rect r, string text, float size, bool enabled, bool active)
        {
            bool hot = enabled && r.Contains(_cursor);
            bool down = hot && Input.GetMouseButton(0);
            Texture2D t = VanillaUi.Asset(active ? "btn_hover" : "btn");
            Color tint = !enabled ? new Color(0.55f, 0.55f, 0.55f, active ? 0.9f : 0.6f)
                : down ? VanillaUi.Pressed : hot ? VanillaUi.Hover : Color.white;
            GUI.color = tint;
            if (t != null)
            {
                float cap = Mathf.Min(r.width * 0.2f, r.height * 230f * 0.08f / 60f);
                GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, cap, r.height), t, new Rect(0f, 0f, 0.08f, 1f));
                GUI.DrawTextureWithTexCoords(new Rect(r.x + cap, r.y, r.width - cap * 2f, r.height), t, new Rect(0.08f, 0f, 0.84f, 1f));
                GUI.DrawTextureWithTexCoords(new Rect(r.xMax - cap, r.y, cap, r.height), t, new Rect(0.92f, 0f, 0.08f, 1f));
            }
            else
            {
                GUI.color = active ? new Color(0.5f, 0f, 0f, tint.a) : new Color(0.2f, 0.2f, 0.2f, tint.a);
                GUI.DrawTexture(r, _white);
            }
            GUI.color = Color.white;
            Text(r, text, true, size, TextAnchor.MiddleCenter, enabled || active ? VanillaUi.White : VanillaUi.Grey);
        }

        /// <summary>One line in the game's fonts (Bebas / Roboto), size in
        /// native HUD units; colours are the caller's (no remap).</summary>
        static void Text(Rect r, string s, bool heading, float size, TextAnchor align, Color c)
        {
            if (s == null) return;
            GUIStyle st = heading ? _head : _body;
            Font f = VanillaUi.Font(heading);
            if (f != null && st.font != f) st.font = f;
            st.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * _u));
            st.alignment = align;
            st.normal.textColor = c;
            GUI.color = Color.white;
            GUI.Label(r, s, st);
        }

        static void Click()
        {
            for (int i = 0; i < _buttons.Length; i++)
                if (_buttons[i].Contains(_cursor))
                {
                    Command(i == AutoButton ? TowerRadar.CmdFree : i == CeaseButton ? TowerRadar.CmdHold : TowerRadar.CmdFocus);
                    return;
                }
            Vector2 centre = new Vector2(_cx, _cy);
            if ((_cursor - centre).sqrMagnitude > _r * _r) return;
            Blip best = null;
            float bestD = 24f * _u;
            bestD *= bestD;
            for (int i = 0; i < _blips.Count; i++)
            {
                if (!Shown(_blips[i])) continue;
                Vector2 p = ToScreen(_blips[i].Pos);
                if ((p - centre).sqrMagnitude > _r * _r) continue;
                float d = (p - _cursor).sqrMagnitude;
                if (d < bestD) { best = _blips[i]; bestD = d; }
            }
            _selected = best;
        }

        // ------------------------------------------------------- primitives

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
    /// One complete motor-siren cycle from assets/ndr_siren.wav per raid.
    /// All towers and event warnings share admission, including the clip's
    /// duration after a brief all clear. WAV sampler sections join on the DSP
    /// clock but each plays once. Without sampler sections, the whole file
    /// (or the synthesised fallback) plays once with a volume run-up/run-down.
    /// </summary>
    internal sealed class SirenVoice
    {
        internal const string FileName = "ndr_siren.wav";

        static readonly SirenGate _gate = new SirenGate();
        static bool _loaded, _parts;
        static AudioClip _up, _loop, _down;

        GameObject _go;
        AudioSource _upSrc, _loopSrc, _downSrc;
        bool _requested, _playing;
        double _startAt, _endAt, _duration;
        Vector3 _position;

        internal Vector3 Position
        {
            // A duplicate warning must not move the already sounding source.
            set { _position = value; }
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
                v._loopSrc = v.Source(_loop, false, min, max);
                if (_up != null) v._upSrc = v.Source(_up, false, min, max);
                if (_down != null) v._downSrc = v.Source(_down, false, min, max);
                v._duration = Seconds(_up) + Seconds(_loop) + Seconds(_down);
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

        /// <summary>Every frame; trigger state also latches a silent raid
        /// after its one complete siren cycle. No allocations or scans.</summary>
        internal void Tick(bool on)
        {
            if (_go == null) return;
            double now = AudioSettings.dspTime;
            if (_playing && now >= _endAt)
            {
                StopAll();
                _playing = false;
            }
            if (_gate.Request(ref _requested, on, now, _duration + 0.3))
            {
                StopAll();
                _go.transform.position = _position;
                _startAt = now + 0.1;
                double loopAt = _startAt + Seconds(_up);
                double downAt = loopAt + Seconds(_loop);
                if (_upSrc != null) _upSrc.PlayScheduled(_startAt);
                _loopSrc.PlayScheduled(loopAt);
                // Keep the sampler section's exact DSP boundary for the join.
                _loopSrc.SetScheduledEndTime(downAt);
                if (_downSrc != null) _downSrc.PlayScheduled(downAt);
                _endAt = _startAt + _duration + 0.2;
                _playing = true;
            }
            if (!_parts && _playing)
            {
                float up = (float)((now - _startAt) / 3.0);
                float down = (float)((_endAt - 0.2 - now) / 5.0);
                _loopSrc.volume = Mathf.Clamp01(Mathf.Min(up, down));
            }
        }

        void StopAll()
        {
            if (_upSrc != null) _upSrc.Stop();
            if (_loopSrc != null) _loopSrc.Stop();
            if (_downSrc != null) _downSrc.Stop();
        }

        internal void Destroy()
        {
            _gate.Release(ref _requested);
            StopAll();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = null;
            _playing = false;
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
                        + " s without loop points - played once.");
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
            if (EastWorld.On || !TowerRadar.B(TowerRadar.CfgRunwayLights)) { if (_root != null) Clear(); return; }
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
            bool lightEngines = on && CombatLoad.LocalNear(TowerRadar.RadarPos, 1500f);
            for (int i = 0; i < _lights.Count; i++)
                if (_lights[i] != null && _lights[i].enabled != lightEngines) _lights[i].enabled = lightEngines;
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
                if (kind >= 20 && kind <= 23) { TowerDelivery.Receive(f, sender); return; }
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

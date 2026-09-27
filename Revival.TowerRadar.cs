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
//      the selected blip). Only an operator whose faction is the airfield's
//      own ([Airfield] DefenderFaction) is obeyed by the crews. With an
//      operator - such a player, or the HQ's own NPC operator (key
//      radar/c1/op) - the guns are radar-directed (Flak.SetFireDirection:
//      shorter reaction, smaller first error, faster tracking). Without one
//      they lay by eye (the P4 numbers). With the radar or the console
//      destroyed they are worse than that (no warning at all).
//   4  DAMAGE. The antenna head, the mast, the KUNG shelter and the console
//      are targets: the local player's rounds (a postfix on the game's
//      FireOneShot, a ray against their boxes, walls in between stop it,
//      window panes do not) and the game's explosions (a postfix on
//      ExplosionObject.NetworkVisualizeExplode, falling off with distance).
//      The master keeps the hit points (RadarHits, ConsoleHits). Destroyed:
//      the antenna stops and loses its elements, the scope goes dark, the
//      screen dies, the guns lose their direction. After RepairMinutes with
//      no player within 150 u the HQ is back.
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
                + "fire control of the ZU-23 guns (acting only with [World] EastTile, [Airfield] "
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
                "A destroyed radar or console is working again after this many minutes, never with a "
                + "player within 150 u.");
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

        // master only
        static float _radarHits, _consoleHits, _radarDeadAt = -1f, _consoleDeadAt = -1f;
        static float _opUntil;
        static float _nextMaster, _nextBroadcast, _sirenSince = -100f, _lastThreat = -100f;
        static bool _dirty, _wasMaster, _directionSet;
        static readonly List<Vector4> _booms = new List<Vector4>();   // xyz + time of reported explosions

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
            try
            {
                Type t = RevivalPlugin.TypeByName("ExplosionObject");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "NetworkVisualizeExplode", null, null);
                if (m != null)
                {
                    harmony.Patch(m, null, new HarmonyMethod(typeof(TowerRadar).GetMethod("ExplodePostfix")), null, null, null);
                    boom = "yes";
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar: explosion hook: " + ex.Message); }
            Log("input lock on " + n + " of " + Locks.Length + " game predicates, shot hook " + shot
                + ", explosion hook " + boom + ".");
        }

        public static void LockPostfix(ref bool __result)
        {
            if (RadarScope.InView) __result = true;
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
                RadarDamage.Explosion(c);
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
                if (master && !_wasMaster)
                {
                    // A new master: the flak crews start weapons free.
                    Mode = 0;
                    AssignedKind = -1;
                    _dirty = true;
                }
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
            RadarScope.Leave(why);
            RadarSiren.Stop();
            RunwayLights.Clear();
            if (RadarRoot != null && !KitRadar) UnityEngine.Object.Destroy(RadarRoot.gameObject);
            if (ConsoleRoot != null) UnityEngine.Object.Destroy(ConsoleRoot.gameObject);
            RadarRoot = Head = ConsoleRoot = null;
            Screen = null;
            Elements.Clear();
            Built = KitRadar = false;
            if (_directionSet) { Flak.SetFireDirection(1f, 1f, 1f); _directionSet = false; }
            Log("cleared (" + why + ").");
        }

        // ------------------------------------------------------ finding it all

        static void Find()
        {
            if (Built || Time.realtimeSinceStartup < _nextFind) return;
            _nextFind = Time.realtimeSinceStartup + 5f;
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) { _tileSince = -1f; return; }
            if (_tileSince < 0f) _tileSince = Time.realtimeSinceStartup;

            Transform kit = null, tower = null, h1 = null;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.name.StartsWith("East", StringComparison.Ordinal)) continue;
                GameObject[] roots = sc.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    if (kit == null) kit = Deep(roots[r].transform, RadarName);
                    if (tower == null) tower = TowerIn(roots[r].transform);
                    if (h1 == null) h1 = Deep(roots[r].transform, "H1");
                }
            }
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
        static Transform TowerIn(Transform t)
        {
            if (t.name == "C1" && Mathf.Abs(t.position.x - TowerSpot.x) < 20f && Mathf.Abs(t.position.z - TowerSpot.y) < 20f
                && t.GetComponentInChildren<Renderer>() != null)
                return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform f = TowerIn(t.GetChild(i));
                if (f != null) return f;
            }
            return null;
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
            else if (_frozenAt < 0f) _frozenAt = AntennaAngle;
            bool show = RadarAlive || !B(CfgDamageLook);
            for (int i = 0; i < Elements.Count; i++)
                if (Elements[i] != null && Elements[i].enabled != show) Elements[i].enabled = show;
        }

        static bool _screenLit = true;

        static void ConsoleLook()
        {
            if (Screen == null) return;
            bool lit = Working && B(CfgGlow);
            bool dead = !ConsoleAlive && B(CfgDamageLook);
            if (lit == _screenLit && !dead) return;
            _screenLit = lit;
            Screen.sharedMaterial = dead ? RadarModel.Black : lit ? RadarModel.Phosphor : RadarModel.DarkGlass;
        }

        // ---------------------------------------------------------- the master

        static void MasterTick()
        {
            if (Time.time < _nextMaster) return;
            _nextMaster = Time.time + 0.5f;

            // the operator
            if (OperatorActor >= 0 && Time.time > _opUntil && OperatorActor != Crocodile.LocalActor())
            {
                Log("the console's operator (actor " + OperatorActor + ") is gone.");
                OperatorActor = -1;
                _dirty = true;
            }
            bool npc = RadarOperator.Alive;
            if (npc != NpcOperatorUp) { NpcOperatorUp = npc; _dirty = true; }

            // repair
            float repair = Mathf.Clamp(F(CfgRepair, 30f), 1f, 600f) * 60f;
            if (!RadarAlive && _radarDeadAt >= 0f && Time.time - _radarDeadAt > repair && !PlayerNear(RadarPos, 150f))
            {
                _radarHits = 0f;
                RadarHp = 1f;
                _radarDeadAt = -1f;
                _dirty = true;
                RadarScope.Note("RADAR back in service.");
                Log("the radar is repaired.");
            }
            if (!ConsoleAlive && _consoleDeadAt >= 0f && Time.time - _consoleDeadAt > repair && !PlayerNear(ConsoleRoot.position, 150f))
            {
                _consoleHits = 0f;
                ConsoleHp = 1f;
                _consoleDeadAt = -1f;
                _dirty = true;
                RadarScope.Note("CONSOLE back in service.");
                Log("the console is repaired.");
            }

            // the guns' direction
            int tier;
            if (!Working) tier = 0;
            else if (npc || (OperatorActor >= 0 && Obeyed(OperatorActor))) tier = 2;
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
            }

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

        /// <summary>The crews take orders only from their own faction.</summary>
        internal static bool Obeyed(int actor)
        {
            GameObject p = Crocodile.PlayerByActor(actor);
            if (p == null) return false;
            string f = Fraktion.Spielerseite(p);
            return f != null && f == Fraktion.Eigene(Airfield.Faction());
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
            if (explosion)
            {
                for (int i = _booms.Count - 1; i >= 0; i--)
                    if (Time.time - _booms[i].w > 3f) _booms.RemoveAt(i);
                for (int i = 0; i < _booms.Count; i++)
                {
                    Vector4 b = _booms[i];
                    if (Mathf.RoundToInt(b.y) == part && (new Vector3(b.x, boom.y, b.z) - boom).sqrMagnitude < 9f) return;
                }
                _booms.Add(new Vector4(boom.x, part, boom.z, Time.time));
            }
            amount = Mathf.Clamp(amount, 0f, 200f);
            if (part == 0)
            {
                if (!RadarAlive) return;
                _radarHits += amount;
                float max = Mathf.Max(1, I(CfgRadarHits, 30));
                RadarHp = Mathf.Clamp01(1f - _radarHits / max);
                if (RadarHp <= 0f)
                {
                    _radarDeadAt = Time.time;
                    RadarScope.Note("RADAR DESTROYED - scope dark.");
                    Log("the radar is destroyed.");
                }
            }
            else
            {
                if (!ConsoleAlive) return;
                _consoleHits += amount;
                float max = Mathf.Max(1, I(CfgConsoleHits, 12));
                ConsoleHp = Mathf.Clamp01(1f - _consoleHits / max);
                if (ConsoleHp <= 0f)
                {
                    _consoleDeadAt = Time.time;
                    RadarScope.Note("CONSOLE DESTROYED.");
                    Log("the console is destroyed.");
                }
            }
            _dirty = true;
        }

        /// <summary>A player's claim of the console (master).</summary>
        internal static void OnClaim(int actor, bool on)
        {
            if (!Crocodile.IsMaster()) return;
            if (on)
            {
                if (OperatorActor >= 0 && OperatorActor != actor && Time.time < _opUntil) return;
                if (OperatorActor != actor) { _dirty = true; Log("actor " + actor + " is at the console."); }
                OperatorActor = actor;
                _opUntil = Time.time + 3f;
            }
            else if (OperatorActor == actor)
            {
                OperatorActor = -1;
                _dirty = true;
                Log("actor " + actor + " left the console.");
            }
        }

        internal const int CmdEngage = 1, CmdFree = 2, CmdHold = 3, CmdClear = 4;

        /// <summary>A console command (master): only from the claimed operator
        /// and only obeyed from the airfield's own faction.</summary>
        internal static void OnCommand(int actor, int cmd, int kind, Vector3 pos)
        {
            if (!Crocodile.IsMaster()) { RadarNet.SendCommand(cmd, kind, pos); return; }
            if (actor != OperatorActor) return;
            if (!ConsoleAlive) return;
            if (!Obeyed(actor))
            {
                Log("command " + cmd + " from actor " + actor + " refused: not the airfield's faction.");
                return;
            }
            switch (cmd)
            {
                case CmdEngage:
                    {
                        GepardGun.Contact c = RadarScope.Resolve(kind, pos, 120f);
                        if (c == null || c.Go == null) return;
                        Flak.AssignTarget(null, c.Go);
                        if (Mode == 1) { Flak.AssignedOnly(null); Mode = 2; }
                        AssignedKind = c.Kind;
                        AssignedPos = c.Pos;
                        break;
                    }
                case CmdFree:
                    Flak.WeaponsFree(null);
                    Mode = 0;
                    break;
                case CmdHold:
                    Flak.HoldFire(null);
                    Flak.ClearTarget(null);
                    AssignedKind = -1;
                    Mode = 1;
                    break;
                case CmdClear:
                    Flak.ClearTarget(null);
                    if (Mode == 2) { Flak.HoldFire(null); Mode = 1; }
                    AssignedKind = -1;
                    break;
            }
            _dirty = true;
        }

        /// <summary>The master's state arrived (every other client).</summary>
        internal static void OnState(float radar, float console, int op, bool npc, int mode, bool siren, int tier,
                                     int kind, Vector3 assigned)
        {
            if (Crocodile.IsMaster()) return;
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
                    object v = _getter.Invoke(null, null);
                    if (v is double)
                    {
                        double d = (double)v;
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

        internal static void Explosion(Component c)
        {
            Vector3 at = c.transform.position;
            float radius = Radius(c);
            Parts();
            float[] worst = new float[2];
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
                float amount = Mathf.Max(1f, TowerRadar.I(TowerRadar.CfgExplosionHits, 40) * (1f - dist / reach));
                if (amount > worst[p.Id]) worst[p.Id] = amount;
            }
            for (int id = 0; id < 2; id++)
                if (worst[id] > 0f) TowerRadar.ApplyHit(id, worst[id], at, true);
        }

        /// <summary>The explosion's radius (FireHook.Radius: the field may be
        /// an Obscured type).</summary>
        static float Radius(Component c)
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
            UnityEngine.Object[] actors = UnityEngine.Object.FindObjectsOfType(npcType);
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
            if (!TowerRadar.B(TowerRadar.CfgNpcOperator) || TowerRadar.ConsoleRoot == null) return;
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
                string side = Airfield.Faction();
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
                Guns();
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
                if (!working) continue;
                Vector3 d = c.Pos - eye;
                d.y = 0f;
                if (d.magnitude > range) continue;
                float brg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                if (Mathf.Repeat(brg - a0, 360f) > span && span < 359f) continue;
                float h = Height(c.Pos);
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

        static float Height(Vector3 p)
        {
            float g;
            return EastWorld.TerrainHeight(p, out g) ? p.y - g : 999f;
        }

        /// <summary>Guns opening fire and falling silent go to the log.</summary>
        static void Guns()
        {
            List<FlakGunInfo> guns = Flak.Guns();
            for (int i = 0; i < guns.Count; i++)
            {
                FlakGunInfo g = guns[i];
                FlakState had;
                bool known = _gunState.TryGetValue(g.Id, out had);
                if (known && had != g.State)
                {
                    if (g.State == FlakState.Firing) Note(g.Id + " ENGAGING");
                    else if (g.State == FlakState.NoCrew) Note(g.Id + " NO CREW");
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
            if (c.Kind == 2 || c.Kind == 3)
            {
                GameObject pilot = Crocodile.PlayerByActor(c.Actor);
                string f = pilot == null ? null : Fraktion.Spielerseite(pilot);
                if (f == null) return 0;
                return mine != null && f == mine ? 1 : -1;
            }
            bool friend = false, foe = false;
            float reach = Mathf.Max(4f, c.Radius * 1.5f) + 2f;
            List<GameObject> players = GepardCrew.Spieler();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null || (go.transform.position - c.Pos).sqrMagnitude > reach * reach) continue;
                string f = Fraktion.Spielerseite(go);
                if (f == null) continue;
                if (mine != null && f == mine) friend = true;
                else foe = true;
            }
            return friend ? 1 : foe ? -1 : 0;
        }

        /// <summary>Master: a hostile aircraft (a player of a faction hostile
        /// to the airfield's aboard) airborne within <paramref name="r"/> of
        /// the radar.</summary>
        internal static bool HostileWithin(float r)
        {
            Vector3 eye = TowerRadar.RadarPos;
            string side = Airfield.Faction();
            string own = Fraktion.Eigene(side);
            float min = Mathf.Max(0f, TowerRadar.F(TowerRadar.CfgMinHeight, 3f)) * K;
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null || (c.Pos - eye).sqrMagnitude > r * r || Height(c.Pos) < min) continue;
                if (c.Kind == 2 || c.Kind == 3)
                {
                    GameObject pilot = Crocodile.PlayerByActor(c.Actor);
                    string f = pilot == null ? null : Fraktion.Spielerseite(pilot);
                    if (f != null && f != own && Fraktion.Feind(side, f)) return true;
                    continue;
                }
                float reach = Mathf.Max(4f, c.Radius * 1.5f) + 2f;
                List<GameObject> players = GepardCrew.Spieler();
                bool friendly = false, hostile = false;
                for (int k = 0; k < players.Count; k++)
                {
                    GameObject go = players[k];
                    if (go == null || (go.transform.position - c.Pos).sqrMagnitude > reach * reach) continue;
                    string f = Fraktion.Spielerseite(go);
                    if (f == null) continue;
                    if (f == own) friendly = true;
                    else if (Fraktion.Feind(side, f)) hostile = true;
                }
                if (hostile && !friendly) return true;
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
                if (c.Go == null || (kind >= 0 && c.Kind != kind)) continue;
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
                if (TowerRadar.OperatorActor >= 0 && TowerRadar.OperatorActor != mine && Time.time - _enteredAt > 3f)
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
                    Claim(true);
                }
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
            else if (TowerRadar.OperatorActor >= 0 && TowerRadar.OperatorActor != Crocodile.LocalActor())
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
            if (!TowerRadar.Obeyed(Crocodile.LocalActor()))
                Hint("The crews do not take orders from your faction.", 3f);
            string what = cmd == TowerRadar.CmdEngage ? "WEAPONS FREE on track " + _selected.Id.ToString("00", CultureInfo.InvariantCulture)
                : cmd == TowerRadar.CmdFree ? "WEAPONS FREE, all guns" : cmd == TowerRadar.CmdHold ? "HOLD FIRE, all guns"
                : "target assignment cleared";
            Note("ORDER: " + what);
            TowerRadar.OnCommand(Crocodile.LocalActor(), cmd, kind, pos);
        }

        // --------------------------------------------------------- drawing

        static Texture2D _white, _disc;
        static GUIStyle _text, _small;
        static readonly Color Green = new Color(0.35f, 1f, 0.5f, 1f);
        static readonly Color DimGreen = new Color(0.2f, 0.7f, 0.3f, 0.6f);
        static readonly Color Foe = new Color(1f, 0.3f, 0.25f, 1f);
        static readonly Color Unknown = new Color(1f, 0.85f, 0.3f, 1f);
        static readonly Color Amber = new Color(1f, 0.78f, 0.3f, 0.95f);
        static float _cx, _cy, _r;
        static Rect[] _buttons = new Rect[4];

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
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label);
                _text.fontSize = 15;
                _small = new GUIStyle(GUI.skin.label);
                _small.fontSize = 12;
            }
        }

        internal static void Draw()
        {
            try
            {
                if (!InView && !_near && (_hint == null || Time.time > _hintUntil)) return;
                Ensure();
                float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                if (!InView)
                {
                    if (_near) Label(new Rect(0f, h * 0.62f, w, 26f), _nearWhy ?? ("[" + Key() + "] Radar console"), Color.white, true);
                    if (_hint != null && Time.time <= _hintUntil) Label(new Rect(0f, h * 0.66f, w, 26f), _hint, Amber, true);
                    return;
                }
                GUI.color = new Color(0f, 0f, 0f, 0.92f);
                GUI.DrawTexture(new Rect(0f, 0f, w, h), _white);
                _r = Mathf.Min(h * 0.44f, w * 0.3f);
                _cx = _r + h * 0.05f;
                _cy = h * 0.5f;
                Scope();
                Panel(w, h);
                // the cursor
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(_cursor.x - 9f, _cursor.y - 1f, 18f, 2f), _white);
                GUI.DrawTexture(new Rect(_cursor.x - 1f, _cursor.y - 9f, 2f, 18f), _white);
                if (_hint != null && Time.time <= _hintUntil) Label(new Rect(0f, h - 60f, w, 26f), _hint, Amber, true);
                GUI.color = Color.white;
            }
            catch { GUI.matrix = Matrix4x4.identity; GUI.color = Color.white; }
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
                Label(new Rect(_cx - _r, _cy - 12f, _r * 2f, 26f),
                    !TowerRadar.RadarAlive ? "NO VIDEO - ANTENNA DESTROYED" : "NO VIDEO - CONSOLE DAMAGED", Foe, true);
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
            Rect tile = new Rect(2500f, -2500f, 5000f, 5000f);
            Poly(new Vector3[] { new Vector3(tile.xMin, 0f, tile.yMin), new Vector3(tile.xMax, 0f, tile.yMin),
                                 new Vector3(tile.xMax, 0f, tile.yMax), new Vector3(tile.xMin, 0f, tile.yMax),
                                 new Vector3(tile.xMin, 0f, tile.yMin) });
            GUI.color = new Color(0.4f, 0.9f, 0.5f, 0.7f);
            Vector2 r0 = ToScreen(new Vector3(4632f, 0f, -1600f)), r1 = ToScreen(new Vector3(4632f, 0f, 1600f));
            Line(r0.x, r0.y, r1.x, r1.y, 3f);
            // the guns and their reach
            List<FlakGunInfo> guns = Flak.Guns();
            for (int i = 0; i < guns.Count; i++)
            {
                Vector2 g = ToScreen(guns[i].Position);
                GUI.color = new Color(0.3f, 0.8f, 1f, 0.35f);
                Ring(g.x, g.y, _r * guns[i].Range / range, 48);
                GUI.color = new Color(0.3f, 0.8f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(g.x - 3f, g.y - 3f, 6f, 6f), _white);
                SmallLabel(g.x + 5f, g.y - 7f, guns[i].Id, GUI.color);
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
                SmallLabel(p.x + 6f, p.y + 2f, b.Id.ToString("00", CultureInfo.InvariantCulture), c);
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

        static void Panel(float w, float h)
        {
            float x = _cx + _r + h * 0.05f;
            float pw = w - x - 20f;
            float y = h * 0.06f;
            Label(new Rect(x, y, pw, 24f), "P-18 AIR DEFENCE HQ - C1 TOWER", Green, false);
            y += 24f;
            string status = "RADAR " + Pct(TowerRadar.RadarHp) + "   CONSOLE " + Pct(TowerRadar.ConsoleHp) + "   ANTENNA "
                + TowerRadar.AntennaAngle.ToString("000", CultureInfo.InvariantCulture) + "   RANGE "
                + (TowerRadar.ScopeRangeU / K / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
            SmallLabel(x, y, status, Green);
            y += 18f;
            SmallLabel(x, y, "FIRE CONTROL: " + (TowerRadar.Mode == 0 ? "WEAPONS FREE" : TowerRadar.Mode == 1 ? "HOLD FIRE"
                : "WEAPONS TIGHT - assigned track only") + "   direction: " + TowerRadar.TierName(TowerRadar.Tier)
                + (TowerRadar.SirenOn ? "   SIREN" : ""), TowerRadar.Mode == 1 ? Amber : Green);
            y += 24f;

            // the selected track
            if (_selected != null && _selected.Go != null)
            {
                Blip b = _selected;
                Vector3 d = b.Pos - TowerRadar.RadarPos;
                float brg = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
                Vector3 v = b.Vel;
                v.y = 0f;
                float hdg = Mathf.Repeat(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg, 360f);
                float kmh = v.magnitude / K * 3.6f;
                Color c = b.Iff > 0 ? Green : b.Iff < 0 ? Foe : Unknown;
                Label(new Rect(x, y, pw, 22f), "TRACK " + b.Id.ToString("00", CultureInfo.InvariantCulture) + "  " + IffName(b.Iff), c, false);
                y += 22f;
                SmallLabel(x, y, "type: " + Guess(b), c); y += 16f;
                SmallLabel(x, y, "bearing " + brg.ToString("000", CultureInfo.InvariantCulture) + "   range "
                    + (new Vector3(d.x, 0f, d.z).magnitude / K / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " km", c); y += 16f;
                SmallLabel(x, y, "heading " + (kmh < 5f ? "---" : hdg.ToString("000", CultureInfo.InvariantCulture)) + "   speed "
                    + kmh.ToString("0", CultureInfo.InvariantCulture) + " km/h   height "
                    + (b.Height / K).ToString("0", CultureInfo.InvariantCulture) + " m", c); y += 22f;
            }
            else
            {
                SmallLabel(x, y, "No track selected - click a blip.", DimGreen);
                y += 22f;
            }

            // the buttons
            string[] names = { "ENGAGE SELECTED", "WEAPONS FREE ALL", "HOLD FIRE ALL", "CLEAR" };
            float bw = Mathf.Min(170f, (pw - 30f) / 4f);
            for (int i = 0; i < 4; i++)
            {
                Rect r = new Rect(x + i * (bw + 8f), y, bw, 30f);
                _buttons[i] = r;
                bool hot = r.Contains(_cursor);
                GUI.color = hot ? new Color(0.3f, 0.8f, 0.4f, 0.9f) : new Color(0.1f, 0.35f, 0.15f, 0.9f);
                GUI.DrawTexture(r, _white);
                SmallLabel(r.x + 6f, r.y + 7f, names[i], Color.white);
            }
            y += 42f;

            // the guns
            List<FlakGunInfo> guns = Flak.Guns();
            if (guns.Count == 0) { SmallLabel(x, y, "No AA guns on the air defence net.", Amber); y += 18f; }
            for (int i = 0; i < guns.Count; i++)
            {
                FlakGunInfo g = guns[i];
                SmallLabel(x, y, g.Id + "  ZU-23-2   " + g.State + "   crew " + g.CrewAlive + "/2   "
                    + g.Rounds + " rds", g.State == FlakState.Firing ? Foe : g.State == FlakState.NoCrew ? Amber : Green);
                y += 16f;
            }
            y += 8f;
            SmallLabel(x, y, "Operator: " + (TowerRadar.OperatorActor >= 0 ? "player at the console" : TowerRadar.NpcOperatorUp ? "HQ operator" : "none"), DimGreen);
            y += 22f;

            // the log
            if (TowerRadar.B(TowerRadar.CfgLog))
            {
                Label(new Rect(x, y, pw, 22f), "RADIO / CONTACT LOG", Green, false);
                y += 20f;
                for (int i = _log.Count - 1; i >= 0 && y < h - 70f; i--)
                {
                    SmallLabel(x, y, _log[i], new Color(0.6f, 1f, 0.7f, 0.9f));
                    y += 15f;
                }
            }
            SmallLabel(x, h - 40f, "Mouse: cursor   LMB: select / button   " + Key() + " / Esc: leave", DimGreen);
        }

        static string Pct(float f) { return f <= 0f ? "DESTROYED" : (f * 100f).ToString("0", CultureInfo.InvariantCulture) + "%"; }

        static void Click()
        {
            for (int i = 0; i < 4; i++)
                if (_buttons[i].Contains(_cursor))
                {
                    Command(i == 0 ? TowerRadar.CmdEngage : i == 1 ? TowerRadar.CmdFree : i == 2 ? TowerRadar.CmdHold : TowerRadar.CmdClear);
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

        static void Label(Rect r, string s, Color c, bool centre)
        {
            GUIStyle st = _text;
            if (centre)
            {
                float tw = st.CalcSize(new GUIContent(s)).x;
                r = new Rect(r.x + (r.width - tw) * 0.5f, r.y, tw + 4f, r.height);
            }
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), s, st);
            GUI.color = c;
            GUI.Label(r, s, st);
            GUI.color = Color.white;
        }

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

    /// <summary>The air raid siren on the tower roof (synthesised: a
    /// motor siren's rising and falling wail) and the NPCs it alarms.</summary>
    internal static class RadarSiren
    {
        static AudioSource _source;
        static AudioClip _clip;
        static float _volume;

        internal static void Tick()
        {
            bool on = TowerRadar.SirenOn && TowerRadar.B(TowerRadar.CfgSiren);
            if (!on && _source == null) return;
            if (_source == null) Make();
            if (_source == null) return;
            _source.transform.position = TowerRadar.TowerPoint(new Vector3(7.9f, TowerRadar.RoofM + 0.8f, 0f));
            _volume = Mathf.MoveTowards(_volume, on ? 1f : 0f, Time.deltaTime / (on ? 3f : 5f));
            _source.volume = _volume;
            _source.pitch = 0.7f + 0.3f * _volume;     // the motor spins up and runs down
            if (_volume > 0f && !_source.isPlaying) _source.Play();
            if (_volume <= 0f && _source.isPlaying) _source.Stop();
        }

        internal static void Stop()
        {
            if (_source != null) UnityEngine.Object.Destroy(_source.gameObject);
            _source = null;
            _volume = 0f;
        }

        static void Make()
        {
            try
            {
                if (_clip == null) _clip = Clip();
                GameObject go = new GameObject("NDR tower siren");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                _source.clip = _clip;
                _source.loop = true;
                _source.playOnAwake = false;
                _source.spatialBlend = 1f;
                _source.dopplerLevel = 0f;
                _source.rolloffMode = AudioRolloffMode.Logarithmic;
                _source.minDistance = 90f;
                _source.maxDistance = 7000f;
                _source.volume = 0f;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRadar siren: " + ex.Message); }
        }

        /// <summary>Eight seconds: up from 280 to 620 Hz and down again, a
        /// rotor's chopped harmonics over it.</summary>
        static AudioClip Clip()
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
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(npcType);
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
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + 5f;
            _night = Night();
            if (_root == null && _night) Build();
            bool on = _night && TowerRadar.ConsoleAlive;
            if (_root != null && on != _lit)
            {
                _lit = on;
                _root.SetActive(on);
                TowerRadar.Log("runway lights " + (on ? "on" : "off") + ".");
            }
        }

        internal static void Clear()
        {
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
            List<Vector3> at = new List<Vector3>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene sc = SceneManager.GetSceneAt(s);
                if (!sc.isLoaded || !sc.name.StartsWith("East", StringComparison.Ordinal)) continue;
                GameObject[] roots = sc.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++) Collect(roots[r].transform, at);
            }
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

        static void Collect(Transform t, List<Vector3> at)
        {
            if (t.name.StartsWith("Edge light", StringComparison.Ordinal))
            {
                if (!Broken(t)) at.Add(t.position);
                return;
            }
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), at);
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

        static void Send(float[] data)
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
                TowerRadar.AssignedKind, TowerRadar.AssignedPos.x, TowerRadar.AssignedPos.y, TowerRadar.AssignedPos.z });
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
                if (kind == State && f.Length >= 12)
                    TowerRadar.OnState(f[1], f[2], Mathf.RoundToInt(f[3]), f[4] > 0.5f, Mathf.Clamp(Mathf.RoundToInt(f[5]), 0, 2),
                        f[6] > 0.5f, Mathf.Clamp(Mathf.RoundToInt(f[7]), 0, 2), Mathf.RoundToInt(f[8]), new Vector3(f[9], f[10], f[11]));
                else if (kind == ClaimMsg)
                    TowerRadar.OnClaim(sender, f[1] > 0.5f);
                else if (kind == Hit && f.Length >= 7 && Crocodile.IsMaster())
                    TowerRadar.ApplyHit(Mathf.Clamp(Mathf.RoundToInt(f[1]), 0, 1), Mathf.Clamp(f[2], 0f, 100f),
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

// Next Day: Survival - Revival Toolkit
//
// THE HELICOPTER A PLAYER FLIES. The game already owns a complete, networked
// Mi-8 - the humanitarian-aid machine "GamePlayObjects/Helicopters/mi-8_mchs"
// that the troop insertion (RevivalTroopInsertion.cs) has been flying by script
// since 6.16.0. This file does NOT build a helicopter. It puts a man in that
// one: a key puts the machine down in front of him or on the helipad he is
// standing on, he boards it, and from there the flight is his.
//
// WHY THE SAME PREFAB AND NOT A NEW MODEL. It is already a scene object with a
// PhotonView and a PhotonInterpolatedTransform, every client can load it from
// its resource path, HelicopterDummy.Update spins its rotors everywhere for
// free, and the size, the hull collider and the gear geometry were all measured
// for the troop landings. A second helicopter would have to earn all of that
// again.
//
// WHAT WAS CONFIRMED BEFORE THIS FILE (RE in RevivalTroopInsertion.cs, ilq.py):
//   NetworkGameplayEvents.HelicopterDummySpawn instantiates the Mi-8 with
//   PhotonNetwork.InstantiateSceneObject and then RPCs NetworkSetMovementData.
//   HelicopterDummy.Update spins the rotors on every client and, on the master
//   only, runs MoveTowards(targetPosition) - which returns at once while
//   startPosition is zero. So a machine whose startPosition we zero is a machine
//   the game will never move, and the transform is ours.
//   HelicopterDummy.Start on a NON-owner stores itself as the aid event's
//   HelicopterDummyObject; the Start hook below saves and restores that
//   reference, so a helicopter of ours never becomes the aid event's.
//
// WHO MOVES IT. Photon owns scene objects on the MASTER, and the prefab's
// PhotonInterpolatedTransform streams the owner's transform to everybody else.
//   pilot is master      he writes the transform, Photon replicates it
//   pilot is not master  he writes his own copy (his interpolator is switched
//                        off while he flies) and sends the pose to the master
//                        at NetHz, which writes it into the object it owns -
//                        from there it is the ordinary replication again
// The same split decides spawning and removal: only the master may call
// InstantiateSceneObject and PhotonNetwork.Destroy, so everyone else asks it.
//
// SIZE AND UNITS. The world is modelled about 2.8 times real size (the note in
// RevivalTroopInsertion.cs measures it: 5.0 unit human capsules, a 75 unit rotor
// disc). K is that factor. Every offset and speed below is written in METRES and
// multiplied by K, so the machine keeps its proportions at any Size setting and
// the numbers in the config stay readable.
//
// FLIGHT MODEL. No Rigidbody and no Unity physics, for the reason the FPV drone
// gives: a Rigidbody on a networked scene object does things with Photon and the
// world's colliders that we do not control. Position and velocity are integrated
// here, the collective holds altitude by itself when nothing is pressed, and the
// ground is a floor the machine cannot sink through. The nose and the bank are
// drawn from the velocity - they are attitude, not input.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// Physics, Collider and AudioSource are not compilable types here (build.ps1
// references four Unity modules only), so they go through reflection. ASCII-only
// comments and logs; player-facing strings go through Loc.T (real Cyrillic), so
// this file is UTF-8 without BOM.
//
// SEAMS OUTSIDE THIS FILE (four one-line calls and one camera owner):
//   RevivalPlugin.cs        BindConfig / Install / Update / LateUpdate / OnGUI
//   Revival.CameraTurret.cs CameraOwner.Heli and its LateTick dispatch

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The player-flown Mi-8: spawn, board, fly, land, leave. Everything that
    /// belongs to one machine and one pilot lives here.
    /// </summary>
    internal static class PlayerHeli
    {
        /// <summary>The game's own aid helicopter, by its resource path
        /// (research/resource_paths.tsv: gameplayobjects/helicopters/mi-8_mchs).</summary>
        internal const string Prefab = "GamePlayObjects/Helicopters/mi-8_mchs";

        /// <summary>Instantiation data marker. Deliberately NOT the troop
        /// insertion's marker: that one enrols a machine in its orphan sweep,
        /// which removes every helicopter no scripted flight is driving after
        /// ten seconds - and a parked machine waiting for a pilot is exactly
        /// that.</summary>
        internal const string Marker = "ndr-flyheli-1";

        // ============================================================= config

        internal static ConfigEntry<bool> CfgEnabled, CfgPassengers, CfgBailOut,
            CfgCrash, CfgWreckModel, CfgHold;
        internal static ConfigEntry<string> CfgSpawnKey, CfgBoardKey, CfgViewKey,
            CfgEngineKey, CfgJumpKey;
        internal static ConfigEntry<float> CfgSize, CfgThrust, CfgSideThrust,
            CfgLift, CfgDrag, CfgMaxSpeed, CfgSensitivity, CfgCamDistance,
            CfgCamHeight, CfgFov, CfgNetHz, CfgBoardRange, CfgSeatSide,
            CfgSeatUp, CfgSeatForward, CfgClimbRate, CfgSinkRate, CfgYawRate,
            CfgSpoolSeconds, CfgCrashSink, CfgCrashSpeed, CfgCrashDamage,
            CfgWreckSeconds, CfgHoldWeight;
        internal static ConfigEntry<int> CfgEventCode, CfgMaxHelis, CfgHoldSlots;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("PlayerHeli", "Enabled", true,
                "Let a player fly the game's own Mi-8. The spawn key puts one "
                + "down, the board key gets in and out.");
            CfgSpawnKey = cfg.Bind("PlayerHeli", "SpawnKey", "F3",
                "Puts a helicopter down in front of you - on the helipad you are "
                + "standing on, if there is one. Pressed next to an empty "
                + "helicopter of your own it takes that one away again. Only the "
                + "Photon master spawns; everyone else asks it.");
            CfgBoardKey = cfg.Bind("PlayerHeli", "BoardKey", "F",
                "Get in when you stand next to a helicopter, get out when it is "
                + "on the ground. The same key while flying.");
            CfgViewKey = cfg.Bind("PlayerHeli", "ViewKey", "V",
                "Switch between the chase view and the cockpit.");
            CfgEngineKey = cfg.Bind("PlayerHeli", "EngineKey", "G",
                "Start and shut down the engine from the pilot's seat. A machine "
                + "that has just been put down is cold and silent; it lifts "
                + "nothing until the disc is up to speed. Leaving the machine "
                + "shuts it down.");
            CfgJumpKey = cfg.Bind("PlayerHeli", "JumpKey", "X",
                "Jump out. Anyone aboard may press it, pilot and passengers "
                + "alike. High enough and with a parachute in the pack the "
                + "canopy opens by itself; without one it is a fall.");
            CfgSize = cfg.Bind("PlayerHeli", "Size", 1f,
                "Size relative to the game's aid Mi-8 (0.5..2). 1 is the "
                + "original, which matches players and vehicles.");
            CfgMaxSpeed = cfg.Bind("PlayerHeli", "MaxSpeed", 62f,
                "Top HORIZONTAL speed in metres per second, real scale "
                + "(62 m/s = 223 km/h, the Mi-8's cruise). Climbing and sinking "
                + "have their own limits below. Everything here is metric and "
                + "multiplied by the world's 2.8.");
            CfgThrust = cfg.Bind("PlayerHeli", "Thrust", 5.5f,
                "Forward and backward acceleration (W/S) in m/s^2. A helicopter "
                + "accelerates by tilting its disc, and 30 degrees of tilt is "
                + "about 5.5 m/s^2 - it takes seconds, not an instant. Together "
                + "with Drag this sets the cruising speed: Thrust/Drag.");
            CfgSideThrust = cfg.Bind("PlayerHeli", "SideThrust", 2.6f,
                "Sideways acceleration (A/D) in m/s^2. Half the forward figure: "
                + "sideways flight is the slow, ugly way to move a helicopter.");
            CfgLift = cfg.Bind("PlayerHeli", "Lift", 3.2f,
                "How fast the vertical speed CHANGES, in m/s^2 (space and left "
                + "control). It is not the climb rate - ClimbRate and SinkRate "
                + "are - it is how long the machine takes to get there. With no "
                + "collective input the machine holds its height by itself, "
                + "which is what a helicopter does.");
            CfgClimbRate = cfg.Bind("PlayerHeli", "ClimbRate", 7f,
                "Largest climb rate in metres per second. The Mi-8 manages about "
                + "8 at sea level; anything much above that is a lift, not a "
                + "helicopter.");
            CfgSinkRate = cfg.Bind("PlayerHeli", "SinkRate", 7f,
                "Largest sink rate under power, in metres per second. With the "
                + "engine off the machine falls faster than this - that is a "
                + "crash, and it is meant to be.");
            CfgDrag = cfg.Bind("PlayerHeli", "Drag", 0.12f,
                "Air resistance per second, proportional to speed. It is also "
                + "the machine's inertia: 1/Drag is roughly the number of "
                + "seconds it takes to reach cruise, and the same to wash the "
                + "speed off again. Larger is more sluggish and more stable.");
            CfgYawRate = cfg.Bind("PlayerHeli", "YawRate", 55f,
                "Largest heading change in degrees per second. The mouse asks "
                + "for a rate here, it does not snap the nose around: the tail "
                + "rotor needs a moment to swing eleven tonnes.");
            CfgSpoolSeconds = cfg.Bind("PlayerHeli", "SpoolSeconds", 12f,
                "Seconds from a cold rotor to full power and back. Nothing lifts "
                + "before the disc is up to speed, and switching the engine off "
                + "in the air does not stop it dead.");
            CfgSensitivity = cfg.Bind("PlayerHeli", "Sensitivity", 2.2f,
                "Mouse sensitivity. Sideways is the tail rotor (heading), "
                + "up and down is where the pilot looks.");
            CfgCamDistance = cfg.Bind("PlayerHeli", "CameraDistance", 26f,
                "Chase camera distance in metres.");
            CfgCamHeight = cfg.Bind("PlayerHeli", "CameraHeight", 8f,
                "Chase camera height over the machine in metres.");
            CfgFov = cfg.Bind("PlayerHeli", "FOV", 0f,
                "Field of view while flying. 0 keeps the game's own.");
            CfgNetHz = cfg.Bind("PlayerHeli", "NetHz", 15f,
                "How often per second a pilot who is not the master sends his "
                + "pose to the master. In between, Photon interpolates.");
            CfgEventCode = cfg.Bind("PlayerHeli", "NetworkEventCode", 170,
                "Photon event code (0..194); this and the five following codes "
                + "are used. Must be the same on every client.");
            CfgMaxHelis = cfg.Bind("PlayerHeli", "MaxHelicopters", 4,
                "How many player helicopters may stand in the world at once. "
                + "Beyond that the oldest empty one is removed.");
            CfgBoardRange = cfg.Bind("PlayerHeli", "BoardRange", 12f,
                "How far from the machine you may get in, in metres.");
            CfgPassengers = cfg.Bind("PlayerHeli", "Passengers", true,
                "Let other players ride along in the cabin.");
            CfgBailOut = cfg.Bind("PlayerHeli", "BailOut", true,
                "Jumping out in flight is allowed - the jump key, and shift with "
                + "the board key. Without it the machine has to be landed first, "
                + "and then a parachute never has anything to open over.");
            CfgSeatSide = cfg.Bind("PlayerHeli", "SeatSide", 0.9f,
                "Pilot's seat across the fuselage, in metres from the model "
                + "origin. The gear footprint's centre line is 0.9.");
            CfgSeatUp = cfg.Bind("PlayerHeli", "SeatUp", 1.2f,
                "Cabin floor over the gear, in metres.");
            CfgSeatForward = cfg.Bind("PlayerHeli", "SeatForward", 5f,
                "Pilot's seat forward of the model origin, in metres.");
            CfgCrash = cfg.Bind("PlayerHeli", "Crash", true,
                "A machine flown into a tree, a building or a wall explodes and "
                + "burns where it stands, exactly like a patrol wreck. Off: the "
                + "old behaviour, where the hull rides through everything.");
            CfgCrashSink = cfg.Bind("PlayerHeli", "CrashSinkRate", 9f,
                "Touching down faster than this, in metres per second, breaks "
                + "the machine. Landing softly is a skill; dropping onto the pad "
                + "is not.");
            CfgCrashSpeed = cfg.Bind("PlayerHeli", "CrashGroundSpeed", 22f,
                "Ground contact with more horizontal speed than this, in metres "
                + "per second, breaks the machine as well - that is a run-on "
                + "landing that ends in a ball.");
            CfgCrashDamage = cfg.Bind("PlayerHeli", "CrashDamage", 1000f,
                "Damage everyone still aboard takes when the machine hits. It "
                + "goes through the game's own damage gate, so admin god mode "
                + "and armour still count. 0 means the crash costs no health.");
            CfgWreckSeconds = cfg.Bind("PlayerHeli", "WreckSeconds", 120f,
                "How long the burning wreck stands before the host takes it "
                + "away. It is the same scene object, so it is one machine off "
                + "the MaxHelicopters count the whole time.");
            CfgWreckModel = cfg.Bind("PlayerHeli", "WreckModel", true,
                "A machine that has hit the ground swaps to the game's own "
                + "broken Mi-8 (the rusted hulk, mesh mi-8_rusty_int) and loses "
                + "its glass and rotor. Off: the intact model stays and is only "
                + "scorched. It also stays if that model is not loaded on this "
                + "map - the log says which of the two happened.");
            CfgHold = cfg.Bind("PlayerHeli", "Hold", true,
                "A cargo hold. The machine carries the game's own container on "
                + "the port side of the cabin, opened with the interact key like "
                + "any vehicle trunk. Off: the machine carries what its "
                + "passengers carry and nothing else.");
            CfgHoldSlots = cfg.Bind("PlayerHeli", "HoldSlots", 200,
                "Slots in the hold, 12 to 600. For comparison: a car trunk has "
                + "24 to 32 and the biggest container the game ships has 42 - "
                + "which is also how many slot forms the container window owns, "
                + "so everything above 42 is built by cloning them and the "
                + "window scrolls. If that fails the hold is cut back to what "
                + "the window can show and the log says so.");
            CfgHoldWeight = cfg.Bind("PlayerHeli", "HoldWeight", 4000f,
                "What the hold carries, in kilograms. A car trunk takes 80 to "
                + "100; an Mi-8 lifts four tonnes.");
            CfgPowerLoss = cfg.Bind("PlayerHeli", "PowerLossThreshold", 0.5f,
                "Engine output (0..1) under which a machine in the air is lost: "
                + "it falls, noses down and crashes like a machine nobody flies. "
                + "A shut-down engine delivers 0 at once, however fast the rotor "
                + "is still turning. 0 switches the crash off and brings back the "
                + "old sink under a windmilling rotor.");
            CfgPowerLossGrace = cfg.Bind("PlayerHeli", "PowerLossGrace", 1f,
                "Seconds the output may stay under the threshold before the "
                + "machine is lost. A restart inside this window is a dip, not "
                + "a crash.");
            CfgPowerLossHeight = cfg.Bind("PlayerHeli", "PowerLossHeight", 3f,
                "Under this height in metres an engine stop is a landing, not a "
                + "crash.");
            CfgSafetyCanopy = cfg.Bind("PlayerHeli", "SafetyCanopy", true,
                "Safety net: if the flight ever ends WITHOUT your key and without "
                + "your death (an error, a machine taken away under you), and you "
                + "are more than SafetyCanopyHeight over the ground, the canopy "
                + "opens by itself - no parachute item needed.");
            CfgSafetyCanopyHeight = cfg.Bind("PlayerHeli", "SafetyCanopyHeight", 10f,
                "Metres over the ground above which the safety net opens a "
                + "canopy.");
            CfgResearchExit = cfg.Bind("PlayerHeli", "ResearchExitTrace", false,
                "Research: keeps the last two seconds of flight (speed, height, "
                + "frame time, what else moved the machine, the game's own fall "
                + "test, collisions of the body) and dumps them into the log at "
                + "every way out of the machine; logs every damage the player "
                + "takes aboard with a stack trace; one jitter line per second. "
                + "The short exit line with its cause is always logged.");
            FlightView.BindConfig(cfg);
        }

        internal static ConfigEntry<bool> CfgSafetyCanopy, CfgResearchExit;
        internal static ConfigEntry<float> CfgPowerLoss, CfgPowerLossGrace,
            CfgPowerLossHeight, CfgSafetyCanopyHeight;

        internal static bool Enabled
        {
            get { return CfgEnabled == null || CfgEnabled.Value; }
        }

        /// <summary>Size relative to the aid Mi-8, exactly as the troop
        /// helicopter reads it.</summary>
        internal static float Scale()
        {
            return CfgSize == null ? 1f : Mathf.Clamp(CfgSize.Value, 0.5f, 2f);
        }

        /// <summary>Metres to world units. The aid Mi-8 at Size 1 is modelled
        /// 1/0.36 = 2.78 times real size, so every metric number below becomes
        /// world units by multiplying with this.</summary>
        internal static float K { get { return Scale() / 0.36f; } }

        // ============================================================== state

        // The local player's seat. _heli is the one machine he is in; null means
        // he is on his own feet and nothing in this file touches him.
        static GameObject _heli;
        static bool _pilot;                  // he has the controls
        static int _seat;                    // passenger seat, -1 while flying it
        static bool _cockpit;                // camera view
        static Vector3 _vel;                 // world units per second
        static float _yaw;                   // degrees, 0 = world +Z
        static float _look;                  // camera pitch, degrees
        static float _nose, _bank;           // drawn attitude, degrees
        static bool _onGround;
        static bool _engine;                 // the pilot has asked for power
        static float _yawRate;               // degrees per second, smoothed
        static float _airborneSince;         // Time.time the skids last left the floor
        static float _boardedAt, _nextPose, _nextHeartbeat;
        static Transform _body;              // the local player's own transform
        static bool _hadBody;                // his body was found at least once
        static string _hint = "";
        static float _hintUntil;
        static bool _poseWarned, _bodyWarned, _interpWarned, _floorWarned;

        // Every machine of ours this client knows about, in the order they were
        // built. Filled by the Start hook, which runs on every client - the
        // creator, the others and late joiners alike.
        static readonly List<GameObject> _all = new List<GameObject>();
        static readonly Dictionary<int, GameObject> _byView = new Dictionary<int, GameObject>();
        // viewID -> Time.time until which somebody else's pilot counts as aboard.
        // Every pose and every heartbeat pushes it out; a pilot who crashes out
        // of the game simply stops refreshing and the machine is free again.
        static readonly Dictionary<int, float> _busyUntil = new Dictionary<int, float>();
        static readonly List<int> _drop = new List<int>();
        // Machine -> Time.time at which the burning wreck is taken away. Keyed
        // by the object rather than the view id: a wreck has to burn in single
        // player too, where there is no PhotonView at all.
        static readonly Dictionary<GameObject, float> _burning =
            new Dictionary<GameObject, float>();
        static readonly List<GameObject> _gone = new List<GameObject>();

        static KeyCode _spawnKey, _boardKey, _viewKey, _engineKey, _jumpKey;
        static bool _keysParsed;

        internal static bool Aboard { get { return _heli != null; } }
        internal static bool Flying { get { return _heli != null && _pilot; } }

        /// <summary>Is this the machine the LOCAL pilot has the controls of? Its
        /// engine is advanced by the flight, in the same frame the power is
        /// used; every other machine advances itself.</summary>
        internal static bool Flown(GameObject go)
        {
            return go != null && _pilot && ReferenceEquals(go, _heli);
        }

        internal static float SpoolTime()
        {
            return CfgSpoolSeconds == null ? 12f : Mathf.Max(0.5f, CfgSpoolSeconds.Value);
        }

        // =============================================================== frame

        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Net.EnsureHooked();
                Sweep();
                Wrecks();

                if (_heli == null)
                {
                    _diedAboard = false;
                    if (Input.GetKeyDown(SpawnKey())) SpawnOrRemove();
                    else if (Input.GetKeyDown(BoardKey())) Board();
                    return;
                }

                // The view profile, pilot and passenger alike.
                float agl;
                if (Height(_heli, out agl)) FlightView.Report(agl);
                if (Research) Record();

                // HE DIED IN HIS SEAT. The game's own death (PlayerDeath, hooked
                // below) ends the flight at once. Before this, a dead man stayed
                // "aboard" until his body was replaced - and the first key he
                // pressed on the death screen threw his corpse out of the door,
                // which is the "jumped at 52 m, canopy False" of 2026-09-25.
                if (_diedAboard)
                {
                    _diedAboard = false;
                    RevivalPlugin.L.LogInfo("PlayerHeli: the local player died aboard "
                        + "- the flight ends.");
                    GameObject left = _heli;
                    bool wasPilot = _pilot;
                    bool inAir = _pilot ? !_onGround : Airborne(left);
                    Vector3 drift = _vel;
                    Why("death");
                    Leave(false);
                    if (wasPilot && inAir && left != null
                        && (CfgCrash == null || CfgCrash.Value))
                        Abandon(left, drift, true);
                    return;
                }

                // THE MAN CAN GO AWAY UNDER HIMSELF. He dies, he is revived, the
                // room or the region changes - and the game builds him a new
                // player object. Nothing of ours would notice: the body lock
                // would hold the new one still and the seat would hold it in a
                // machine he is no longer in. A body that was there and is gone,
                // or one that has been replaced, ends the flight.
                Transform root = LocalPlayerRoot();
                if (_hadBody && (_body == null
                                 || (root != null && !ReferenceEquals(root, _body))))
                {
                    RevivalPlugin.L.LogInfo("PlayerHeli: the pilot's body is gone "
                        + "(death, respawn or a scene change) - the flight ends.");
                    // What is left of the flight has to be decided BEFORE Leave,
                    // which clears all of it.
                    GameObject left = _heli;
                    bool wasPilot = _pilot;
                    bool inAir = _pilot ? !_onGround : Airborne(left);
                    Vector3 drift = _vel;
                    Why("body gone");
                    Leave(false);
                    // The same rule the jump has, for the same reason: a machine
                    // the PILOT is no longer in, in the air, is a machine nobody
                    // is flying. The flight loop that moved it died with him, so
                    // without this the hull hangs at the height and the heading
                    // of the moment he died, silent, for the rest of the session
                    // - which is what the field found after every death in the
                    // air. A helicopter whose pilot is dead goes down.
                    if (wasPilot && inAir && left != null
                        && (CfgCrash == null || CfgCrash.Value))
                        Abandon(left, drift, true);
                    return;
                }

                // A machine that broke while he was in it does not wait for a
                // key. Crash() has already thrown him out on this client; this
                // catches the one somebody else's crash message broke.
                if (Burning(_heli)) { Involuntary("machine burning"); return; }

                // A machine that has lost its power in the air is falling, and
                // HeliCrashFall moves it - not the flight. The pilot rides it
                // down: he can look around, and he can jump.
                bool falling = Falling(_heli);

                if (Input.GetKeyDown(ViewKey())) _cockpit = !_cockpit;
                if (Input.GetKeyDown(JumpKey())) { Why("key " + JumpKey()); Jump(); return; }
                if (Input.GetKeyDown(BoardKey())) { Why("key " + BoardKey()); Leave(true); return; }
                if (_pilot && !falling && Input.GetKeyDown(EngineKey()))
                {
                    if (_engine) Why("key " + EngineKey());
                    SetEngine(!_engine);
                }

                if (_pilot)
                {
                    Steer();
                    if (!falling) Fly();
                }
                Heartbeat();
                _errors = 0;
            }
            catch (Exception ex)
            {
                // One bad frame is not a reason to throw a man out of a machine
                // at two hundred metres. Several in a row are - and then it is
                // an involuntary exit, which opens a canopy.
                RevivalPlugin.L.LogError("PlayerHeli: " + ex);
                if (++_errors >= 5 || !(_pilot ? !_onGround : Airborne(_heli)))
                {
                    _errors = 0;
                    Involuntary("exception in the flight loop");
                }
            }
        }

        static int _errors, _camErrors;
        static bool _diedAboard;

        /// <summary>
        /// The body of everyone aboard, put where it belongs. Has to be
        /// LateUpdate: the game's own animator and movement controller write the
        /// character between Update and here, and a man seated earlier in the
        /// frame is back on the ground before anything is drawn. Same reason the
        /// technical's gunner is placed here.
        /// Seam: RevivalPlugin.LateUpdate.
        /// </summary>
        internal static void LateFrame()
        {
            if (!Enabled) return;
            // Every frame, aboard or not: out of the machine the profile still
            // has to blend back to the player's own settings.
            FlightView.LateTick();
            if (_heli == null) return;
            try
            {
                if (_body == null) _body = LocalPlayerRoot();
                if (_body != null) _hadBody = true;
                if (_body == null)
                {
                    if (!_bodyWarned)
                    {
                        _bodyWarned = true;
                        RevivalPlugin.L.LogWarning("PlayerHeli: the local player "
                            + "object was not found - the machine flies, the man "
                            + "stays where he stood.");
                    }
                    return;
                }
                Transform tr = _heli.transform;
                _body.position = tr.position + tr.rotation * (Seat(_seat) * K);
                Vector3 dir = tr.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.000001f)
                    _body.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli seat: " + ex.Message);
            }
        }

        /// <summary>
        /// The pilot's view. Called by CameraOwner while this feature holds the
        /// camera, in LateUpdate, after the game's own camera scripts have been
        /// paused. Two views: from behind and above, which is how a helicopter
        /// is flown, and from the cockpit.
        /// </summary>
        internal static void LateTick()
        {
            if (_heli == null || !_pilot) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                Transform tr = _heli.transform;
                float k = K;

                if (_cockpit)
                {
                    Vector3 eye = tr.position + tr.rotation
                        * (new Vector3(Seat(-1).x - 0.5f, Seat(-1).y + 1.6f,
                                       Seat(-1).z + 0.6f) * k);
                    cam.transform.position = eye;
                    cam.transform.rotation = Quaternion.Euler(-_look, _yaw, _bank * 0.5f);
                }
                else
                {
                    Vector3 centre = tr.position + tr.rotation * (new Vector3(
                        CfgSeatSide.Value, 2.5f, 0f) * k);
                    float dist = Mathf.Max(6f, CfgCamDistance.Value) * k;
                    float high = CfgCamHeight.Value * k;
                    Quaternion orbit = Quaternion.Euler(-_look, _yaw, 0f);
                    Vector3 back = orbit * new Vector3(0f, 0f, -dist);
                    Vector3 eye = centre + back + Vector3.up * high;
                    // Never put the eye under the ground: on the pad, looking
                    // from behind, the camera would otherwise sit in the hill.
                    float ground;
                    if (RevivalTroopInsertion.TerrainHeight(eye, out ground)
                        && eye.y < ground + 1.5f * k)
                        eye.y = ground + 1.5f * k;
                    cam.transform.position = eye;
                    cam.transform.rotation = Quaternion.LookRotation(
                        (centre - eye).normalized, Vector3.up);
                }

                // Every frame, not once on boarding: the game's own scope and
                // sprint effects write the field of view back otherwise. Same
                // reason as the gun camera and the drone.
                if (CfgFov != null && CfgFov.Value > 1f) cam.fieldOfView = CfgFov.Value;
                _camErrors = 0;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerHeli camera: " + ex);
                if (++_camErrors >= 5)
                {
                    _camErrors = 0;
                    Involuntary("exception in the camera");
                }
            }
        }

        // ======================================================== spawn, board

        /// <summary>
        /// One key for both directions: no machine of ours within reach puts one
        /// down, an empty one within reach takes it away again. Nothing is ever
        /// removed with somebody in it.
        /// </summary>
        static void SpawnOrRemove()
        {
            GameObject near = Nearest(20f * K);
            if (near != null)
            {
                int view = ViewId(near);
                if (Busy(view))
                {
                    Hint(Text.Occupied(), 3f);
                    return;
                }
                // A machine taken away takes its hold with it, and a hold is
                // where a player puts what he does not want to carry. Removing
                // one full of loot without a word is the bug report this
                // feature would otherwise generate.
                if (!HeliHold.Empty(near))
                {
                    Hint(Text.HoldFull(), 4f);
                    return;
                }
                Remove(near, true);
                return;
            }
            Spawn();
        }

        /// <summary>
        /// The admin panel's version of the spawn key. It is the same both-ways
        /// press - an empty machine of ours within reach is taken away, anything
        /// else puts one down - because a second spawn path would be a second
        /// thing to keep in step with the cap, the helipad snap and the master
        /// request. The panel reports lines, not HUD hints, so the hint the
        /// press produced is handed back instead of being drawn.
        /// </summary>
        internal static string SpawnInFront()
        {
            if (!Enabled)
                return "PlayerHeli is switched off ([PlayerHeli] Enabled = false).";
            _hint = "";
            SpawnOrRemove();
            return _hint.Length > 0 ? _hint : Text.SpawnFailed();
        }

        static void Spawn()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null)
            {
                Hint(Text.NoPlayer(), 3f);
                return;
            }
            Transform t = me.transform;
            float heading = t.eulerAngles.y;
            // Far enough in front that the machine cannot be put down on the man
            // himself: the rotor disc alone is 13.3 m across.
            Vector3 at = t.position + Quaternion.Euler(0f, heading, 0f)
                * new Vector3(0f, 0f, 16f * K);

            Vector3 pad;
            float padHeading;
            if (Helipads.Snap(at, out pad, out padHeading)
                || Helipads.Snap(t.position, out pad, out padHeading))
            {
                at = pad;
                heading = padHeading;
                RevivalPlugin.L.LogInfo("PlayerHeli: put down on helipad "
                    + Helipads.NameAt(pad) + ".");
            }
            else
            {
                float y;
                if (RevivalTroopInsertion.GroundY(at, out y)) at.y = y;
                else if (RevivalTroopInsertion.TerrainHeight(at, out y)) at.y = y;
                else at.y = t.position.y;
            }

            if (!RevivalTroopInsertion.MasterClient())
            {
                Net.Send(Net.SpawnRequest,
                    new float[] { at.x, at.y, at.z, heading }, true);
                Hint(Text.Asked(), 3f);
                RevivalPlugin.L.LogInfo("PlayerHeli: not the master - asked it for "
                    + "a helicopter at " + at.ToString("0") + ".");
                return;
            }

            GameObject go = Build(at, heading);
            if (go == null) { Hint(Text.SpawnFailed(), 4f); return; }
            Hint(Text.Spawned(BoardKey().ToString()), 6f);
        }

        /// <summary>The master's half of a spawn: cap first, then the machine.
        /// The cap is what keeps an afternoon of pressing the key from filling
        /// the region with parked helicopters.</summary>
        internal static GameObject Build(Vector3 at, float heading)
        {
            Cap();
            GameObject go = Instantiate(at, heading);
            if (go == null) return null;
            RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + ViewId(go)
                + " at " + at.ToString("0") + ", heading " + heading.ToString("0")
                + " deg, size " + Scale().ToString("0.00") + ".");
            return go;
        }

        /// <summary>Remove the oldest EMPTY machine while there are more than
        /// the configured maximum. An occupied one is never taken away, and
        /// neither is one with cargo aboard: the cap is there to keep the sky
        /// tidy, and no tidying is worth a hold full of loot deleted without a
        /// word. If every machine is loaded the cap simply yields, and the count
        /// stands one over until one of them is emptied.</summary>
        static void Cap()
        {
            int max = CfgMaxHelis == null ? 4 : Mathf.Clamp(CfgMaxHelis.Value, 1, 16);
            for (int guard = 0; guard < 16 && Alive() >= max; guard++)
            {
                GameObject oldest = null;
                for (int i = 0; i < _all.Count; i++)
                {
                    GameObject go = _all[i];
                    if (go == null || ReferenceEquals(go, _heli)) continue;
                    if (Burning(go) || Falling(go)) continue;
                    if (Busy(ViewId(go))) continue;
                    if (!HeliHold.Empty(go)) continue;
                    oldest = go;
                    break;
                }
                if (oldest == null)
                {
                    RevivalPlugin.L.LogInfo("PlayerHeli: the maximum of " + max
                        + " is reached, but every machine standing here is flown "
                        + "or has cargo aboard - none is taken away.");
                    return;
                }
                RevivalPlugin.L.LogInfo("PlayerHeli: " + Alive() + " machines stand "
                    + "in the world, the maximum is " + max + " - the oldest empty "
                    + "one is removed.");
                Remove(oldest, false);
            }
        }

        static int Alive()
        {
            int n = 0;
            for (int i = 0; i < _all.Count; i++) if (_all[i] != null) n++;
            return n;
        }

        /// <summary>Take a machine out of the world. Photon only lets the owner
        /// destroy a scene object, so anybody else asks the master.</summary>
        static void Remove(GameObject go, bool say)
        {
            if (go == null) return;
            int view = ViewId(go);
            if (!RevivalTroopInsertion.MasterClient())
            {
                if (view == 0) { if (say) Hint(Text.NotYours(), 3f); return; }
                Net.Send(Net.RemoveRequest, new float[] { view }, true);
                if (say) Hint(Text.Asked(), 3f);
                return;
            }
            Forget(go);
            HeliFlight.NetDestroy(go);
            if (say) Hint(Text.Removed(), 3f);
            RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + view + " removed.");
        }

        /// <summary>
        /// Get in. The controls belong to whoever is first; anyone after that
        /// rides in the cabin, which is what the Mi-8 is for. Every reason to
        /// refuse says so on the screen - a key that silently does nothing is
        /// the bug report this feature would otherwise generate.
        /// </summary>
        static void Board()
        {
            GameObject go = Nearest(Mathf.Max(3f, CfgBoardRange.Value) * K);
            if (go == null) return;

            int view = ViewId(go);
            bool taken = Busy(view);
            if (taken && (CfgPassengers == null || !CfgPassengers.Value))
            {
                Hint(Text.Occupied(), 3f);
                return;
            }

            _heli = go;
            _body = LocalPlayerRoot();
            _hadBody = _body != null;
            _lowSince = -1f;
            _hasWritten = false;
            _foreign = 0f;
            _ring.Clear();
            _boardedAt = Time.time;
            _cockpit = false;
            _seat = taken ? FreeSeat() : -1;
            _pilot = !taken;

            if (_pilot)
            {
                if (!CameraOwner.Request(CameraOwner.Heli, true, "PlayerHeli"))
                {
                    _heli = null;
                    _pilot = false;
                    Hint(Text.CamBusy(), 4f);
                    return;
                }
                Transform tr = go.transform;
                _yaw = tr.eulerAngles.y;
                _look = 8f;
                _vel = Vector3.zero;
                _nose = 0f;
                _bank = 0f;
                _yawRate = 0f;
                _onGround = true;
                _airborneSince = Time.time;
                // Whatever this machine is doing, the new pilot inherits it. A
                // cold one stays cold until he starts it.
                HeliEngine running = EngineOf(go);
                _engine = running != null && running.Running;
                // Our own interpolator must not fight our own writes. On the
                // master it only sends, so it stays on.
                if (!RevivalTroopInsertion.MasterClient()) Interpolator(go, false);
                Idle(go);
                Hint(Text.AtControls(), 8f);
                RevivalPlugin.L.LogInfo("PlayerHeli: controls taken on helicopter "
                    + view + " (master: " + RevivalTroopInsertion.MasterClient() + ").");
            }
            else
            {
                Hint(Text.Seated(), 5f);
                RevivalPlugin.L.LogInfo("PlayerHeli: seated in the cabin of "
                    + view + ", seat " + _seat + ".");
            }
            Net.Send(Net.Aboard, new float[] { view, 1f, _pilot ? 1f : 0f }, true);
        }

        /// <summary>
        /// Get out. ALWAYS runs to the end - the camera, the frozen body and the
        /// interpolator are all held by this feature, and a way out that skips
        /// one of them leaves a player looking through a camera nobody moves.
        /// </summary>
        static void Leave(bool byKey)
        {
            GameObject go = _heli;
            if (go == null && !_pilot) { Reset(); return; }

            if (byKey && go != null && !Burning(go)
                && (_pilot ? !_onGround : Airborne(go)))
            {
                bool bail = CfgBailOut != null && CfgBailOut.Value
                            && (Input.GetKey(KeyCode.LeftShift)
                                || Input.GetKey(KeyCode.RightShift));
                if (!bail)
                {
                    Hint(Text.LandFirst(), 4f);
                    return;
                }
                // Shift and the board key is the old way out, and it is still a
                // jump - so it gets the same canopy the jump key gets.
                Why("key " + BoardKey() + " + shift");
                Jump();
                return;
            }

            if (!_traced) ExitTrace(byKey ? "leave by key" : "leave");
            _traced = false;
            _leaving = true;
            _leftAt = Time.time;
            int view = ViewId(go);
            try
            {
                if (go != null)
                {
                    // Not on a wreck. Burn switches the interpolator off on
                    // every peer so the pose it settles into is left alone, and
                    // a crash leaves through here a moment later - switching it
                    // back on would hand the wreck to the host's last piloted
                    // picture of the machine and stand it up again.
                    if (_pilot && !RevivalTroopInsertion.MasterClient()
                        && !Burning(go))
                        Interpolator(go, true);
                    if (view != 0) Net.Send(Net.Aboard, new float[] { view, 0f, 0f }, true);
                    // The engine does not keep running behind the last man out.
                    // Whoever was flying it shuts it down on his way through the
                    // door; a passenger leaves the pilot's machine alone.
                    if (_pilot && !Burning(go)) SetEngine(false);
                    Ground(go);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerHeli leave: " + ex.Message); }
            finally
            {
                if (_pilot) CameraOwner.Release(CameraOwner.Heli);
                Reset();
                _leaving = false;
            }
            RevivalPlugin.L.LogInfo("PlayerHeli: left helicopter " + view + ".");
        }

        // ======================================================== exit trace

        // Why the next way out is taken. Set by the caller right before it, read
        // and cleared by ExitTrace; an exit without one says so, and the stack
        // in the same line names the caller anyway.
        static string _why;
        static bool _traced, _leaving;
        static float _leftAt = -100f;

        static void Why(string cause) { _why = cause; }

        /// <summary>
        /// One line at EVERY way out of the machine, always on: what happened,
        /// why (the key, the death, the crash, the error), speed and height,
        /// which of the exit keys were HELD at that moment - a key nobody
        /// pressed shows up here - and the call chain. With ResearchExitTrace
        /// the last two seconds of flight follow.
        /// </summary>
        static void ExitTrace(string what)
        {
            string cause = string.IsNullOrEmpty(_why) ? "no cause given" : _why;
            _why = null;
            _traced = true;
            try
            {
                float k = K;
                float speed = new Vector3(_vel.x, 0f, _vel.z).magnitude / k * 3.6f;
                float agl = -1f;
                bool known = _heli != null && Height(_heli, out agl);
                if (!known) agl = -1f;
                StringBuilder sb = new StringBuilder("PlayerHeli exit: ");
                sb.Append(what).Append(" - cause: ").Append(cause)
                  .Append("; helicopter ").Append(ViewId(_heli))
                  .Append(_pilot ? " (pilot)" : " (passenger)")
                  .Append(", ").Append(Mathf.RoundToInt(speed)).Append(" km/h, ")
                  .Append(known ? Mathf.RoundToInt(agl) + " m" : "height unknown")
                  .Append(Falling(_heli) ? ", falling" : "")
                  .Append("; keys held: ").Append(Held())
                  .Append("; caller: ").Append(Caller(Research ? 14 : 6));
                RevivalPlugin.L.LogInfo(sb.ToString());
                if (Research) Dump();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli exit trace: " + ex.Message);
            }
        }

        /// <summary>The keys that can end a flight, as held right now.</summary>
        static string Held()
        {
            StringBuilder sb = new StringBuilder();
            KeyCode[] keys = { JumpKey(), BoardKey(), EngineKey(),
                               KeyCode.LeftShift, KeyCode.RightShift, KeyCode.Escape };
            for (int i = 0; i < keys.Length; i++)
                if (Input.GetKey(keys[i]))
                    sb.Append(sb.Length > 0 ? "+" : "").Append(keys[i]);
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        /// <summary>The call chain, compact: "Leave <- Jump <- Tick <- Update".
        /// Environment.StackTrace, with this method and the tracer cut off.</summary>
        internal static string Caller(int frames)
        {
            string[] lines = Environment.StackTrace.Split('\n');
            StringBuilder sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < lines.Length && n < frames; i++)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("at ")) l = l.Substring(3);
                int paren = l.IndexOf(" (");
                if (paren < 0) paren = l.IndexOf('(');
                if (paren > 0) l = l.Substring(0, paren);
                if (l.Length == 0 || l.StartsWith("System.Environment")
                    || l.EndsWith(".Caller") || l.EndsWith(".ExitTrace")) continue;
                l = l.Replace("NextDayRevival.", "");
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(l);
                n++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The safety net. Every way out that is NOT the player's key and NOT
        /// his death ends up here - an error in the flight loop, a machine
        /// taken away under him - and if he is more than SafetyCanopyHeight up
        /// he gets a canopy, parachute in the pack or not. It should never run;
        /// if it does, the exit line above names the cause.
        /// </summary>
        static void Involuntary(string cause)
        {
            GameObject go = _heli;
            bool wasPilot = _pilot;
            bool inAir = go != null && (_pilot ? !_onGround : Airborne(go));
            Vector3 drift = _vel;
            float height = -1f;
            Vector3 door = Vector3.zero;
            float floor;
            if (go != null)
            {
                Transform tr = go.transform;
                door = tr.position + tr.rotation * (new Vector3(-8f, 0f, 2f) * K);
                if (Floor(tr.position, out floor)) height = (tr.position.y - floor) / K;
            }
            else if (_body != null)
            {
                door = _body.position;
                if (Floor(door, out floor)) height = (door.y - floor) / K;
            }

            Why("involuntary: " + cause);
            Leave(false);

            float safe = CfgSafetyCanopyHeight == null ? 10f : CfgSafetyCanopyHeight.Value;
            if ((CfgSafetyCanopy == null || CfgSafetyCanopy.Value) && height > safe)
            {
                bool open = SafetyCanopy(door);
                RevivalPlugin.L.LogWarning("PlayerHeli: involuntary exit at "
                    + Mathf.RoundToInt(height) + " m (" + cause + ") - safety canopy "
                    + (open ? "open." : "FAILED to open."));
                Hint(open ? Parachute.Text.Open() : Parachute.Text.Failed(), 5f);
            }
            if (wasPilot && inAir && go != null && !Burning(go)
                && (CfgCrash == null || CfgCrash.Value))
                Abandon(go, drift, true);
        }

        static MethodInfo _mCanopy;

        /// <summary>The parachute module's own canopy - the game's parachute
        /// state entered with the canopy open - without its item and height
        /// checks, which are for a jump the player chose. Through reflection,
        /// so Revival.Parachute.cs is not touched for a safety net.</summary>
        static bool SafetyCanopy(Vector3 at)
        {
            try
            {
                if (_mCanopy == null)
                    _mCanopy = AccessTools.Method(typeof(Parachute), "Open",
                        new Type[] { typeof(Vector3) }, null);
                if (_mCanopy == null) return false;
                bool open = (bool)_mCanopy.Invoke(null, new object[] { at });
                if (open) Parachute.Falling = true;
                return open;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli safety canopy: " + ex.Message);
                return false;
            }
        }

        /// <summary>Metres over the floor under the machine.</summary>
        static bool Height(GameObject go, out float metres)
        {
            metres = 0f;
            if (go == null) return false;
            float floor;
            if (!Floor(go.transform.position, out floor)) return false;
            metres = (go.transform.position.y - floor) / K;
            return true;
        }

        /// <summary>The local player's body, for the guard.</summary>
        internal static Transform Body
        {
            get { return _body != null ? _body : LocalPlayerRoot(); }
        }

        /// <summary>Aboard, or out for less than three seconds - the window in
        /// which a hit still belongs to the flight.</summary>
        internal static bool RecentlyAboard
        {
            get { return _heli != null || Time.time - _leftAt < 3f; }
        }

        /// <summary>Speed and height, for a log line.</summary>
        internal static string Where()
        {
            float agl = -1f;
            bool known = _heli != null && Height(_heli, out agl);
            if (!known) agl = -1f;
            return Mathf.RoundToInt(new Vector3(_vel.x, 0f, _vel.z).magnitude / K * 3.6f)
                + " km/h, " + (known ? Mathf.RoundToInt(agl) + " m" : "height unknown");
        }

        /// <summary>The game has just killed the local player while he sat in a
        /// machine. The flight ends on the next Tick, not inside the game's own
        /// death call.</summary>
        internal static void DiedAboard(string args)
        {
            _diedAboard = true;
            RevivalPlugin.L.LogInfo("PlayerHeli: local player died aboard (PlayerDeath "
                + args + "); " + Where() + "; caller: " + Caller(Research ? 14 : 6));
        }

        // ============================================================ research

        /// <summary>ResearchExitTrace: the flight recorder is on.</summary>
        internal static bool Research
        {
            get { return CfgResearchExit != null && CfgResearchExit.Value; }
        }

        /// <summary>One frame of the flight recorder.</summary>
        struct Frame
        {
            public float T, Dt, Kmh, Agl, Power, Foreign, Ground;
            public Vector3 Pos;
            public bool Grounded, RayHit;
            public int Guarded;
        }

        const int RingSize = 120;
        static readonly List<Frame> _ring = new List<Frame>(RingSize);
        static int _ringNext;
        static float _jitterAt, _dtMin = 1f, _dtMax, _foreignMax, _yawStepMax, _lastYawRate;
        static int _hitches, _guardedFrame;
        internal static int GuardedCalls;          // counted by HeliBodyGuard
        internal static string LastBodyHit = "none", LastRayHit = "none";

        /// <summary>
        /// One frame into the ring: time, frame length, where, how fast, how
        /// high, the rotor's power, whether the game thinks the seated body is
        /// on the ground, and the SAME downward line the game's fall check casts
        /// (200 units, its own mask) - a miss there is what used to kill the
        /// pilot. Once a second a jitter line: the shortest and longest frame,
        /// the hitches over 50 ms, the largest foreign jolt and the largest
        /// frame-to-frame step in the yaw rate.
        /// </summary>
        static void Record()
        {
            try
            {
                Frame f = new Frame();
                f.T = Time.time;
                f.Dt = Time.deltaTime;
                f.Pos = _heli.transform.position;
                f.Kmh = new Vector3(_vel.x, 0f, _vel.z).magnitude / K * 3.6f;
                float agl;
                f.Agl = Height(_heli, out agl) ? agl : -1f;
                HeliEngine e = _heli.GetComponent<HeliEngine>();
                f.Power = e == null ? -1f : e.Power;
                f.Foreign = _foreign;
                f.Guarded = GuardedCalls - _guardedFrame;
                _guardedFrame = GuardedCalls;
                f.Ground = -1f;
                if (_body != null)
                {
                    CharacterController cc = _body.GetComponent<CharacterController>();
                    if (cc != null)
                    {
                        f.Grounded = cc.isGrounded;
                        if (cc.GetComponent<HeliBodyHits>() == null)
                            cc.gameObject.AddComponent<HeliBodyHits>();
                    }
                    RaycastHit hit;
                    Vector3 from = _body.position;
                    f.RayHit = Physics.Linecast(from, from - Vector3.up * 200f,
                                                out hit, ~67111940);
                    if (f.RayHit) f.Ground = hit.distance;
                }
                if (_ring.Count < RingSize) _ring.Add(f);
                else _ring[_ringNext] = f;
                _ringNext = (_ringNext + 1) % RingSize;

                _dtMin = Mathf.Min(_dtMin, f.Dt);
                _dtMax = Mathf.Max(_dtMax, f.Dt);
                if (f.Dt > 0.05f) _hitches++;
                _foreignMax = Mathf.Max(_foreignMax, f.Foreign);
                _yawStepMax = Mathf.Max(_yawStepMax, Mathf.Abs(_yawRate - _lastYawRate));
                _lastYawRate = _yawRate;
                if (Time.time >= _jitterAt)
                {
                    _jitterAt = Time.time + 1f;
                    RevivalPlugin.L.LogInfo("PlayerHeli research: "
                        + Mathf.RoundToInt(f.Kmh) + " km/h, " + Mathf.RoundToInt(f.Agl)
                        + " m, pos " + f.Pos.ToString("0")
                        + "; dt " + (_dtMin * 1000f).ToString("0.0") + "-"
                        + (_dtMax * 1000f).ToString("0.0") + " ms, hitches>50ms " + _hitches
                        + ", foreign jolt max " + _foreignMax.ToString("0.00")
                        + " m, yaw-rate step max " + _yawStepMax.ToString("0.0")
                        + " deg/s; body grounded " + f.Grounded
                        + ", fall-check line " + (f.RayHit ? f.Ground.ToString("0") + " u" : "MISS")
                        + ", fall calls held " + GuardedCalls
                        + "; last body contact " + LastBodyHit
                        + "; last hull ray " + LastRayHit + ".");
                    _dtMin = 1f; _dtMax = 0f; _hitches = 0; _foreignMax = 0f; _yawStepMax = 0f;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli research: " + ex.Message);
            }
        }

        /// <summary>The ring, oldest first, one compact line per frame - only the
        /// last 30 frames in detail, the rest summarised, so one exit does not
        /// bury the log.</summary>
        static void Dump()
        {
            int n = _ring.Count;
            if (n == 0) return;
            int start = n < RingSize ? 0 : _ringNext;
            StringBuilder sb = new StringBuilder("PlayerHeli research: last ");
            sb.Append(n).Append(" frames before the exit (t dt kmh agl power grounded line foreign held):");
            for (int i = Mathf.Max(0, n - 30); i < n; i++)
            {
                Frame f = _ring[(start + i) % n];
                sb.Append("\n  ").Append(f.T.ToString("0.000"))
                  .Append(' ').Append((f.Dt * 1000f).ToString("0.0"))
                  .Append(' ').Append(Mathf.RoundToInt(f.Kmh))
                  .Append(' ').Append(f.Agl.ToString("0.0"))
                  .Append(' ').Append(f.Power.ToString("0.00"))
                  .Append(' ').Append(f.Grounded ? "G" : "-")
                  .Append(' ').Append(f.RayHit ? f.Ground.ToString("0") : "MISS")
                  .Append(' ').Append(f.Foreign.ToString("0.00"))
                  .Append(' ').Append(f.Guarded);
            }
            RevivalPlugin.L.LogInfo(sb.ToString());
        }

        static void Reset()
        {
            _heli = null;
            _pilot = false;
            _seat = 0;
            _body = null;
            _hadBody = false;
            _vel = Vector3.zero;
            _nose = 0f;
            _bank = 0f;
            _yawRate = 0f;
            _engine = false;
        }

        /// <summary>Put the man who just got out beside the machine, on the
        /// ground, clear of the rotor disc. Without this he is left standing in
        /// the fuselage he was riding in.</summary>
        static void Ground(GameObject go)
        {
            if (_body == null || go == null) return;
            Transform tr = go.transform;
            // The door is on the left, the same side the troop squad uses.
            Vector3 outside = tr.position + tr.rotation * (new Vector3(-8f, 0f, 2f) * K);
            // Only a man getting out on the ground is SET on the ground. One who
            // jumps in flight keeps his height and falls, which is what jumping
            // out of a helicopter is; putting him down softly under it would be
            // a parachute this feature never built.
            if (Airborne(go))
            {
                outside.y = tr.position.y + 0.2f * K;
                _body.position = outside;
                return;
            }
            float y;
            if (RevivalTroopInsertion.GroundY(outside, out y)
                || RevivalTroopInsertion.TerrainHeight(outside, out y))
                outside.y = y;
            else outside.y = tr.position.y;
            _body.position = outside;
        }

        /// <summary>Is this machine off the ground? The pilot knows it from his
        /// own flight; a passenger has to measure, and both of them may not get
        /// out into thin air by accident.</summary>
        static bool Airborne(GameObject go)
        {
            if (go == null) return false;
            float floor;
            if (!Floor(go.transform.position, out floor)) return false;
            return go.transform.position.y - floor > 2.5f * K;
        }

        // ================================================================ fly

        /// <summary>
        /// The mouse asks for a TURN RATE, it does not set the heading. Writing
        /// the heading straight from the mouse delta is what made the machine
        /// read as a camera on a stick: eleven tonnes snapped round the moment
        /// the hand moved, at whatever rate the hand moved. Here the sideways
        /// axis is a pedal command, the tail rotor takes about a second to reach
        /// the rate asked for, and it winds down the same way when the hand
        /// stops. YawRate is the ceiling.
        /// </summary>
        static void Steer()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float sens = CfgSensitivity == null ? 2.2f : CfgSensitivity.Value;
            float ceiling = CfgYawRate == null ? 55f : Mathf.Max(8f, CfgYawRate.Value);

            // Mouse X is the movement SINCE THE LAST FRAME, so it has to be
            // divided by that frame's length to be a rate. It used to be scaled
            // by a fixed 60: right at 60 fps, half the turn at 120 and double at
            // 30 - and at speed, where the frame time swings with the streaming,
            // the heading twitched with it. At 60 fps both are the same number.
            float want = Mathf.Clamp(Input.GetAxis("Mouse X") * sens
                                     / Mathf.Max(Time.deltaTime, 0.001f),
                                     -ceiling, ceiling);
            _yawRate = Mathf.Lerp(_yawRate, want, Mathf.Min(1f, 3.5f * dt));
            _yaw += _yawRate * dt;
            if (_yaw > 180f) _yaw -= 360f;
            if (_yaw < -180f) _yaw += 360f;
            _look = Mathf.Clamp(_look - Input.GetAxis("Mouse Y") * sens, -60f, 70f);
        }

        /// <summary>
        /// One frame of flight. Three rules make it a helicopter rather than a
        /// drone.
        ///
        /// THE COLLECTIVE HOLDS THE HEIGHT when nothing is pressed - press
        /// nothing and the machine hangs where it is, which is the one thing a
        /// helicopter does that nothing else does.
        ///
        /// EVERY AXIS HAS A RATE LIMIT AND A TIME CONSTANT. The old model gave
        /// the collective a free hand: while space was held nothing damped the
        /// vertical speed at all, so it ran up to the one shared MaxSpeed - 50
        /// m/s straight up, 180 km/h of lift. Climb and sink now have their own
        /// ceilings (ClimbRate, SinkRate), the acceleration towards them is
        /// Lift, and the horizontal axis reaches cruise over 1/Drag seconds
        /// instead of instantly. Nothing here snaps.
        ///
        /// THE DISC HAS TO BE TURNING. Power is the engine's spool state, not a
        /// switch: lift, thrust and the sideways push are all multiplied by it,
        /// so a cold machine sits there, one that has just been started
        /// staggers, and one switched off in the air sinks with the rotor
        /// windmilling down.
        ///
        /// The ground is still a floor rather than a wall, but it is no longer
        /// free: touching it too fast is a crash (Touchdown), and so is flying
        /// into anything that is not ground (Impact).
        /// </summary>
        static void Fly()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (dt <= 0f) return;
            float k = K;
            Transform tr = _heli.transform;
            Vector3 pos = tr.position;
            Vector3 was = pos;
            // Anything that moved the machine since this loop last put it
            // somewhere - the network interpolator, a collider push - is a
            // jolt the pilot sees. Research mode records it.
            _foreign = _hasWritten ? (pos - _lastWritten).magnitude / K : 0f;

            Quaternion flat = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 fwd = flat * Vector3.forward;
            Vector3 right = flat * Vector3.right;

            float power = Spool(dt);
            if (PowerLoss(power, dt)) return;
            float thrust = (CfgThrust == null ? 5.5f : CfgThrust.Value) * k * power;
            float side = (CfgSideThrust == null ? 2.6f : CfgSideThrust.Value) * k * power;
            float lift = (CfgLift == null ? 3.2f : CfgLift.Value) * k;
            float drag = CfgDrag == null ? 0.12f : Mathf.Max(0.02f, CfgDrag.Value);
            float max = (CfgMaxSpeed == null ? 62f : CfgMaxSpeed.Value) * k;
            float climb = (CfgClimbRate == null ? 7f : Mathf.Max(0.5f, CfgClimbRate.Value)) * k;
            float sink = (CfgSinkRate == null ? 7f : Mathf.Max(0.5f, CfgSinkRate.Value)) * k;

            Vector3 accel = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) accel += fwd * thrust;
            if (Input.GetKey(KeyCode.S)) accel -= fwd * thrust;
            if (Input.GetKey(KeyCode.D)) accel += right * side;
            if (Input.GetKey(KeyCode.A)) accel -= right * side;

            bool up = Input.GetKey(KeyCode.Space);
            bool down = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
            if (up) accel += Vector3.up * lift * power;
            else if (down) accel -= Vector3.up * lift;

            // What the disc cannot carry, gravity takes. A turning rotor carries
            // the machine from about three quarters of full power upwards, which
            // is why the collective can hold the height at all; below that the
            // weight comes back in proportion, and a cold disc carries nothing.
            // So a machine has to be spooled up before it will leave the ground,
            // and one switched off in the air sinks faster the further the rotor
            // has wound down.
            float carry = Mathf.InverseLerp(0.15f, 0.75f, power);
            accel -= Vector3.up * ((1f - carry) * 9.81f * k);

            _vel += accel * dt;

            // Horizontal drag is the pilot's brake AND the machine's inertia;
            // vertical damping is the collective holding the disc. Both are
            // computed over dt, never as a per-frame factor - a fast machine
            // would otherwise fly differently on a fast computer.
            Vector3 horizontal = new Vector3(_vel.x, 0f, _vel.z);
            horizontal -= horizontal * Mathf.Min(1f, drag * dt);
            float vertical = _vel.y;
            if (!up && !down) vertical -= vertical * Mathf.Min(1f, 1.6f * dt);
            if (horizontal.magnitude > max) horizontal = horizontal.normalized * max;
            // Climb and sink have their own ceilings. Under power the sink rate
            // is held to SinkRate; with the disc winding down the machine is
            // allowed past it, and that is the fall it should be.
            float floorSink = -sink / Mathf.Max(0.25f, power);
            _vel = new Vector3(horizontal.x,
                               Mathf.Clamp(vertical, floorSink, climb),
                               horizontal.z);

            pos += _vel * dt;
            // The speed the machine ARRIVES with. It has to be read here,
            // before the floor clamp and the skid friction below take it away:
            // by the end of this method the vertical speed of a machine that
            // has just hit the ground is zero, and a hard landing measured
            // there is always a soft one.
            Vector3 arrival = _vel;

            // The floor: terrain height, and the deck of a helipad when the
            // machine stands over one. A downward ray is deliberately NOT used -
            // it would find the helicopter's own hull collider, which is exactly
            // why the scripted troop flight has none either.
            //
            // Past the edge of the terrain there is no floor to read, and the
            // machine used to take ITS OWN HEIGHT for one - which made it stand
            // on the ground at four hundred metres, the instant it crossed the
            // boundary, and a machine that arrives on the ground at cruising
            // speed is a machine that has just crashed. No floor means no
            // ground: it keeps flying, and there is nothing out there to land
            // on.
            float floor;
            bool solid = Floor(pos, out floor);
            if (!solid)
            {
                floor = pos.y;
                if (!_floorWarned)
                {
                    _floorWarned = true;
                    RevivalPlugin.L.LogWarning("PlayerHeli: no floor at "
                        + pos.ToString("0") + " - past the edge of the terrain. "
                        + "The machine flies on, it cannot land out here.");
                }
            }
            bool wasFlying = !_onGround;
            _onGround = solid && pos.y <= floor + 0.4f * k;
            if (!_onGround && !wasFlying) _airborneSince = Time.time;
            if (solid && pos.y < floor)
            {
                pos.y = floor;
                if (_vel.y < 0f) _vel.y = 0f;
            }
            if (_onGround)
            {
                // Skids on the ground: it stops quickly and it stands level.
                _vel.x -= _vel.x * Mathf.Min(1f, 3f * dt);
                _vel.z -= _vel.z * Mathf.Min(1f, 3f * dt);
                if (_vel.y < 0f) _vel.y = 0f;
            }

            // Attitude is drawn from the speed and the turn, not from a key: a
            // helicopter that moves forward has its nose down, one that drifts
            // sideways hangs into the drift, and one that turns banks into the
            // turn. It settles over about a second, not a fifth of one.
            Vector3 local = Quaternion.Inverse(flat) * _vel;
            float wantNose = Mathf.Clamp(local.z / k * 0.22f, -12f, 12f);
            float wantBank = Mathf.Clamp(local.x / k * 0.30f
                                         + _yawRate * 0.22f, -20f, 20f);
            if (_onGround) { wantNose = 0f; wantBank = 0f; }
            _nose = Mathf.Lerp(_nose, wantNose, Mathf.Min(1f, 1.2f * dt));
            _bank = Mathf.Lerp(_bank, wantBank, Mathf.Min(1f, 1.2f * dt));

            tr.position = pos;
            tr.rotation = Quaternion.Euler(_nose, _yaw, -_bank);
            _lastWritten = pos;
            _hasWritten = true;

            // Two ways to break it, both after the move so the wreck stands
            // where the machine actually got to.
            if (Impact(was, pos)) return;
            if (wasFlying && _onGround && Touchdown(arrival)) return;

            Pose(pos);
        }

        static float _lowSince = -1f;
        static Vector3 _lastWritten;
        static bool _hasWritten;
        static float _foreign;

        /// <summary>
        /// ENGINE FAILURE IN FLIGHT IS A CRASH. The machine used to sink under a
        /// windmilling rotor at most SinkRate / 0.25 - a feather. Now an engine
        /// whose output stays under PowerLossThreshold for PowerLossGrace
        /// seconds, with the machine more than PowerLossHeight up, starts the
        /// same stone-fall a pilotless machine gets (HeliCrashFall: nose down,
        /// wreck, fire, bang; Net.Crashed with seven floats for everybody
        /// else). The pilot stays in his seat and rides it: the ground kills
        /// him (FinishAbandonedCrash) unless he jumps first. A dip shorter than
        /// the grace - a restart, a spool-up - does nothing, and close to the
        /// ground an engine stop is simply a landing.
        ///
        /// The engine has no fuel and no damage model yet, so its output is its
        /// spool state while switched on and nothing when off. Anything that
        /// later takes power away only has to lower that number.
        /// </summary>
        static bool PowerLoss(float power, float dt)
        {
            float threshold = CfgPowerLoss == null ? 0.5f : CfgPowerLoss.Value;
            if (threshold <= 0f || (CfgCrash != null && !CfgCrash.Value) || _onGround)
            {
                _lowSince = -1f;
                return false;
            }
            float output = _engine ? power : 0f;
            float agl;
            float low = CfgPowerLossHeight == null ? 3f : CfgPowerLossHeight.Value;
            if (output >= threshold || !Height(_heli, out agl) || agl <= low)
            {
                _lowSince = -1f;
                return false;
            }
            if (_lowSince < 0f) _lowSince = Time.time;
            float grace = CfgPowerLossGrace == null ? 1f : Mathf.Max(0f, CfgPowerLossGrace.Value);
            if (Time.time - _lowSince < grace) return false;

            _lowSince = -1f;
            float kmh = new Vector3(_vel.x, 0f, _vel.z).magnitude / K * 3.6f;
            RevivalPlugin.L.LogInfo("PlayerHeli: power lost at " + Mathf.RoundToInt(agl)
                + " m, " + Mathf.RoundToInt(kmh) + " km/h - crash sequence.");
            Hint(Loc.T("Отказ двигателя!", "Engine failure!"), 4f);
            Abandon(_heli, _vel, true);
            return true;
        }

        /// <summary>
        /// The engine's power, 0 to 1, moved a little closer to what the pilot
        /// asked for each frame. It is the rotor's own state as well: the
        /// component on the machine reads it to turn the disc and to pitch the
        /// sound, so the number that decides whether the machine can lift is the
        /// same number the player hears and sees.
        /// </summary>
        static float Spool(float dt)
        {
            HeliEngine e = EngineOf(_heli);
            if (e == null) return _engine ? 1f : 0f;
            e.Running = _engine;
            e.Advance(dt, SpoolTime());
            return e.Power;
        }

        /// <summary>The height the gear rests at: the helipad deck under the
        /// machine if there is one, the terrain otherwise.</summary>
        static bool Floor(Vector3 at, out float y)
        {
            Vector3 pad;
            float heading;
            if (Helipads.Snap(new Vector3(at.x, 0f, at.z), out pad, out heading))
            {
                float ground;
                y = RevivalTroopInsertion.TerrainHeight(at, out ground)
                    ? Mathf.Max(pad.y, ground) : pad.y;
                return true;
            }
            return RevivalTroopInsertion.TerrainHeight(at, out y);
        }

        /// <summary>A pilot who is not the master hands his pose to the master,
        /// which owns the object and replicates it to everybody else.</summary>
        static void Pose(Vector3 pos)
        {
            if (RevivalTroopInsertion.MasterClient()) return;
            if (Time.time < _nextPose) return;
            float hz = CfgNetHz == null ? 15f : Mathf.Clamp(CfgNetHz.Value, 2f, 30f);
            _nextPose = Time.time + 1f / hz;
            int view = ViewId(_heli);
            if (view == 0)
            {
                if (!_poseWarned)
                {
                    _poseWarned = true;
                    RevivalPlugin.L.LogWarning("PlayerHeli: no PhotonView on this "
                        + "helicopter - only this client sees it move.");
                }
                return;
            }
            Net.Send(Net.PoseUpdate, new float[] {
                view, pos.x, pos.y, pos.z, _yaw, _nose, _bank }, false);
        }

        /// <summary>Two per second, so a machine with somebody in it is never
        /// counted as empty - by the cap here, or by anyone else's boarding
        /// attempt. The pilot's pose stream does the same job at NetHz, so this
        /// is really the passengers' message.</summary>
        static void Heartbeat()
        {
            if (Time.time < _nextHeartbeat) return;
            _nextHeartbeat = Time.time + 2f;
            int view = ViewId(_heli);
            if (view == 0) return;
            Net.Send(Net.Aboard, new float[] { view, 1f, _pilot ? 1f : 0f }, false);
        }


        // ============================================================= engine

        /// <summary>
        /// Start or shut the engine down. A machine put down by the spawn key is
        /// COLD: the aid helicopter's own prefab plays its rotor loop from
        /// HelicopterDummy.Start and never stops, which is the "it stays on for
        /// ever, even after you get out" the first flight found. The state is one
        /// bit per machine and it is networked, because the rotor and the sound
        /// are drawn on every client independently.
        /// </summary>
        static void SetEngine(bool on)
        {
            if (_heli == null) return;
            _engine = on;
            EngineApply(_heli, on);
            int view = ViewId(_heli);
            if (view != 0) Net.Send(Net.EngineState,
                new float[] { view, on ? 1f : 0f }, true);
            Hint(on ? Text.EngineOn() : Text.EngineOff(), 4f);
            RevivalPlugin.L.LogInfo("PlayerHeli: engine " + (on ? "started" : "shut down")
                + " on helicopter " + view + "."
                + (on || _leaving ? "" : " Cause: " + (_why ?? "none given")
                   + "; caller: " + Caller(5)));
            if (!on && !_leaving) _why = null;
        }

        /// <summary>The rotor and sound state of one machine, created on demand.
        /// Everything that can be missing - the mover, its speed fields, the
        /// audio source - is missing inside the component, not here.</summary>
        static HeliEngine EngineOf(GameObject go)
        {
            if (go == null) return null;
            HeliEngine e = go.GetComponent<HeliEngine>();
            if (e == null) e = go.AddComponent<HeliEngine>();
            return e;
        }

        /// <summary>Put one machine's engine into a state, wherever the order
        /// came from - the pilot's key here, or another client's message.</summary>
        static void EngineApply(GameObject go, bool on)
        {
            HeliEngine e = EngineOf(go);
            if (e != null) e.Running = on;
        }

        // ============================================================== crash

        /// <summary>
        /// A pilotless machine does not become a wreck in the air. It keeps the
        /// speed it had at the door, loses lift, rolls away and falls under a
        /// growing smoke/fire trail. Every client starts the same component from
        /// the same pose; the ordinary Photon interpolator is switched off on
        /// non-master peers so it cannot pull the locally animated fall back to
        /// the last piloted pose.
        /// </summary>
        static void Abandon(GameObject go, Vector3 drift, bool broadcast)
        {
            if (go == null || Burning(go) || Falling(go)) return;
            try
            {
                int view = ViewId(go);
                _busyUntil.Remove(view);
                EngineApply(go, false);
                HeliEngine engine = EngineOf(go);
                if (engine != null) engine.Kill();
                if (!RevivalTroopInsertion.MasterClient()) Interpolator(go, false);

                HeliCrashFall fall = go.AddComponent<HeliCrashFall>();
                fall.Begin(drift);

                if (broadcast && view != 0)
                {
                    Vector3 at = go.transform.position;
                    // The ordinary crash uses the same event with four floats.
                    // Seven means "begin the fall" and costs no seventh event
                    // code - 176 already belongs to the FPV drone.
                    Net.Send(Net.Crashed, new float[] {
                        view, at.x, at.y, at.z, drift.x, drift.y, drift.z }, true);
                }
                RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + view
                    + " abandoned in flight - visible crash descent started. Caller: "
                    + Caller(4));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli fall: " + ex.Message);
                Crash(go, go.transform.position);
            }
        }

        static bool Falling(GameObject go)
        {
            return go != null && go.GetComponent<HeliCrashFall>() != null;
        }

        /// <summary>Height used by the fall component, through the same terrain,
        /// road and helipad answer as ordinary flight.</summary>
        internal static bool CrashFloor(Vector3 at, out float floor)
        {
            return Floor(at, out floor);
        }

        /// <summary>The falling component reached the floor. This is local on
        /// every peer: the reliable fall-start event already gave everybody the
        /// same animation, and rebroadcasting the landing from every peer would
        /// make several explosions for one impact.</summary>
        internal static void FinishAbandonedCrash(GameObject go, Vector3 where)
        {
            if (go == null || Burning(go)) return;
            // Burn plays the bang itself now. It used to be played here, which
            // meant only the pilotless fall ever made a sound.
            Burn(go, where);
            RevivalPlugin.L.LogInfo("PlayerHeli: abandoned helicopter "
                + ViewId(go) + " struck the ground at " + where.ToString("0") + ".");
            // A power-loss fall carries its crew down with it; so does a fall
            // the pilot jumped out of while a passenger stayed in the cabin.
            DestroyedAboard(go, "fall ended in the ground");
        }

        /// <summary>
        /// Did the machine fly into something? The floor covers the ground; this
        /// covers everything the ground is not - trees, houses, masts, vehicles,
        /// and a cliff face steep enough that calling it ground would be a lie.
        ///
        /// A forward ray, not a downward one. The downward ray is the thing this
        /// file has always refused, because it finds the machine's own hull; a
        /// ray along the travel of the frame cannot, as long as what it does find
        /// is checked against our own transform. The cast is the distance really
        /// covered plus the nose ahead of the origin, so it is one ray per frame
        /// of flight and it cannot be stepped over at speed.
        /// </summary>
        static bool Impact(Vector3 from, Vector3 to)
        {
            if (CfgCrash != null && !CfgCrash.Value) return false;
            if (_heli == null || _onGround) return false;

            Vector3 travel = to - from;
            float k = K;
            float speed = travel.magnitude;
            if (speed < 0.001f) return false;
            Vector3 dir = travel / speed;

            // From the middle of the cabin, out to the HULL'S OWN SURFACE in the
            // direction of travel, plus the ground actually covered this frame -
            // so the test cannot be stepped over at speed. The offsets are the
            // hull box Prepare builds: its centre sits 2.5 model units across
            // and 5.5 up (0.9 m and 2.0 m), three units ahead of the cast, and
            // it measures 7 by 11 by 38.
            //
            // The reach is measured per direction rather than taken as the
            // nose's 22 units, because the machine is not 22 units wide. A cast
            // that used the nose's reach sideways broke the machine on a tree it
            // was flying PAST, seven metres clear, and downwards it found the
            // roof of a house two storeys before the skids were near it. A box
            // gives its own surface distance exactly: the offset of its centre
            // along the ray, plus each half-size weighted by that axis.
            //
            // The DISC is deliberately not included: rotors clip scenery at
            // every landing, and a machine that explodes when a blade tip
            // brushes a branch is not a helicopter, it is a mine.
            Vector3 origin = _heli.transform.position
                             + _heli.transform.rotation * (new Vector3(0.9f, 2.0f, 0f) * k);
            origin -= travel;                       // where the cabin came from
            Vector3 axis = Quaternion.Inverse(_heli.transform.rotation) * dir;
            float reach = axis.z * 3f + Mathf.Abs(axis.x) * 3.5f
                          + Mathf.Abs(axis.y) * 5.5f + Mathf.Abs(axis.z) * 19f;
            float rest = speed + Mathf.Max(1f, reach) * 0.36f * k;

            // The ray leaves the middle of the cabin, and the people in the
            // cabin are in front of it: the pilot sits five metres forward of
            // the origin, the passengers behind it. A single cast would find one
            // of them every frame and report the machine clear. So the cast
            // steps PAST its own hits, the way the patrol driver steps past a
            // vehicle's own colliders. The budget is ten and not three, because
            // a man is not one collider: a body hands the ray a bone at a time,
            // and a step that ran out of tries inside the cabin used to report
            // the pilot's own arm as the thing the machine flew into.
            for (int step = 0; step < 10 && rest > 0.1f; step++)
            {
                Vector3 point, normal;
                GameObject hit = Turret.RaycastObject(origin, dir, rest, out point, out normal);
                if (hit == null) return false;
                if (Research)
                    LastRayHit = hit.name + " (layer " + hit.layer + ", "
                        + (Vector3.Distance(origin, point) / k).ToString("0.0") + " m)";
                if (Through(hit))
                {
                    float used = Mathf.Max(0.3f, Vector3.Distance(origin, point) + 0.3f);
                    rest -= used;
                    origin = point + dir * 0.3f;
                    continue;
                }

                // Ground is the floor's business. Only a face steep enough to be
                // a wall counts here, and only when there is speed behind it.
                if (IsGround(hit))
                {
                    if (normal.y > 0.55f) return false;
                    if (speed / Mathf.Max(0.0001f, Time.deltaTime) < 6f * k) return false;
                }

                // The distance is in the line because it is the one number that
                // tells a false crash from a real one: anything the machine is
                // supposed to break on is met at the hull, and a hit reported
                // five metres INSIDE the cabin is a passenger, not a mast.
                RevivalPlugin.L.LogInfo("PlayerHeli: hit " + hit.name + " at "
                    + point + ", "
                    + (Vector3.Distance(_heli.transform.position, point) / k).ToString("0.0")
                    + " m from the machine - the machine is down.");
                Crash(_heli, point);
                return true;
            }
            return false;
        }

        /// <summary>The other way to break it: arriving at the ground faster
        /// than the gear can take, or sliding onto it with the speed of a
        /// vehicle. Read once, at the moment of contact.</summary>
        static bool Touchdown(Vector3 arrival)
        {
            if (CfgCrash != null && !CfgCrash.Value) return false;
            // A moment in the air first. Spawn places the machine with a ray
            // (GroundY), the flight reads the floor from height data, and where
            // the two disagree by a metre a machine that has never flown sinks
            // onto its own floor the moment a pilot sits down. That is not a
            // crash, and it must not be read as one.
            if (Time.time - _airborneSince < 1.2f) return false;
            float k = K;
            float sinkLimit = (CfgCrashSink == null ? 9f : CfgCrashSink.Value) * k;
            float runLimit = (CfgCrashSpeed == null ? 22f : CfgCrashSpeed.Value) * k;
            float sink = -arrival.y;
            float run = new Vector3(arrival.x, 0f, arrival.z).magnitude;
            if (sink < sinkLimit && run < runLimit) return false;
            RevivalPlugin.L.LogInfo("PlayerHeli: hard arrival - sink "
                + (sink / k).ToString("0.0") + " m/s, ground speed "
                + (run / k).ToString("0.0") + " m/s.");
            Crash(_heli, _heli.transform.position);
            return true;
        }

        // Stinger calls this only after the master validates a missile impact.
        // Every peer runs it once, so remote passengers use their own damage
        // gate too. Do not rebroadcast the ordinary crash event from here.
        internal static void MissileImpact(int view, Vector3 where)
        {
            GameObject go = MissileTarget(view);
            if (go == null) return;
            Burn(go, where);
            _busyUntil.Remove(view);
            DestroyedAboard(go, "missile");
        }

        internal static GameObject MissileTarget(int view)
        {
            if (view <= 0) return null;
            GameObject go = ByView(view);
            return go != null && _all.Contains(go) && !Burning(go) && !Falling(go)
                ? go : null;
        }

        internal static int MissileView(GameObject go) { return ViewId(go); }

        internal static void MissileTargets(List<GameObject> targets)
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i] != null && !Burning(_all[i]) && !Falling(_all[i]))
                    targets.Add(_all[i]);
        }

        /// <summary>
        /// The machine is destroyed: the bang, then the fire that stands there
        /// afterwards, which is the patrol wreck's own fire - FireEffect.Spawn
        /// and FireEffect.SpawnWreck, the same two calls a burnt-out convoy
        /// vehicle gets, so a downed helicopter looks like every other wreck in
        /// the world instead of like a second, private effect.
        ///
        /// Everyone still aboard pays for it, through the game's own damage gate
        /// so admin god mode and armour keep working. The hull is left standing
        /// and burning until the host takes it away; it is a scene object and
        /// only the master may destroy it.
        /// </summary>
        static void Crash(GameObject go, Vector3 where)
        {
            if (go == null) return;
            int view = ViewId(go);
            if (Burning(go)) return;

            Burn(go, where);
            if (view != 0) Net.Send(Net.Crashed,
                new float[] { view, where.x, where.y, where.z }, true);
            DestroyedAboard(go, "crash");
        }

        /// <summary>The machine the local player sits in has just been
        /// destroyed - flown into something, dropped too hard, brought down by
        /// a missile, or come down at the end of a power-loss fall, on this
        /// client or on the one that sent the crash. Everyone aboard pays for
        /// it through the game's own damage gate, on his own client. This is
        /// the "death" half of the rule that a man leaves the machine only by
        /// his own key or by dying.</summary>
        static void DestroyedAboard(GameObject go, string how)
        {
            if (go == null || !ReferenceEquals(go, _heli)) return;
            float damage = CfgCrashDamage == null ? 1000f : CfgCrashDamage.Value;
            bool wasPilot = _pilot;
            int view = ViewId(go);
            _engine = false;
            Why(how);
            Leave(false);
            if (damage > 0f) Hurt(damage);
            Hint(Text.Wrecked(), 6f);
            RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + view
                + " destroyed with the local player aboard (" + how + ", pilot: "
                + wasPilot + ").");
        }

        /// <summary>
        /// The visible half of a crash, run on EVERY client: the bang, the
        /// fire, a silent rotor, the broken airframe, and the hull LYING ON THE
        /// GROUND where it came down.
        ///
        /// WHERE IT COMES DOWN IS WHERE IT STAYS (order of 2026-09-21). Until
        /// then this method threw the arrival away: it read the heading, rebuilt
        /// the rotation from it with a fixed six degrees of nose and eleven of
        /// bank, and wrote that in one frame. So a machine that came down
        /// inverted, on its side or nose first stood itself up the instant it
        /// touched. The rotation is now left to HeliWreckSettle, which keeps the
        /// heading, keeps the ground track and keeps the side the machine went
        /// over on, and lays the fuselage down along the ground it is lying on.
        ///
        /// Two consequences follow. First, the origin is no longer the lowest
        /// point of the hull: a machine on its flank hangs half its width below
        /// its own transform, and a machine on its roof hangs its whole height,
        /// so the height is read from the hull box's CORNERS against the ground
        /// under each of them (Settle.Rest) instead of from the floor alone.
        /// Second, the pose must be left alone from here on, so the Photon
        /// interpolator is switched off on every peer - a wreck is not streamed,
        /// and without this the host's last piloted pose would pull a remote
        /// client's wreck upright again a moment after it lands, which would
        /// look exactly like the bug this change removes.
        ///
        /// The bang is louder than a vehicle's and the fire is bigger, and both
        /// start in THIS frame - the crash sound moved here from the abandoned
        /// fall, which was the only path that ever played it, so a machine flown
        /// into a mast is no longer silent.
        /// </summary>
        static void Burn(GameObject go, Vector3 where)
        {
            if (go == null || Burning(go)) return;
            try
            {
                _burning[go] = Time.time + WreckLife();
                EngineApply(go, false);
                HeliEngine e = EngineOf(go);
                if (e != null) e.Kill();
                Interpolator(go, false);

                // The broken airframe FIRST and the settling after it:
                // the settle measures what is DRAWN before it accepts
                // that the wreck is on the ground, and what is drawn is
                // the swapped mesh, not the intact prefab it replaced.
                HeliWreckModel.Apply(go);

                Transform tr = go.transform;
                float floor;
                if (!Floor(tr.position, out floor)) floor = tr.position.y;
                HeliWreckSettle settle = go.AddComponent<HeliWreckSettle>();
                settle.Begin(floor);

                // The ball is thrown from the middle of the cabin rather than
                // from the contact point: a 22-unit fireball centred on the
                // skids buries half of itself in the ground.
                FireEffect.SpawnHeliBlast(where + Vector3.up * (1.5f * K), 22f);
                if (!FireEffect.SpawnHeliFire(go)) FireEffect.SpawnWreck(go, false);
                HeliCrashSound.Play(where);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli burn: " + ex.Message);
            }
        }

        static bool Burning(GameObject go)
        {
            return go != null && _burning.ContainsKey(go);
        }

        static float WreckLife()
        {
            return CfgWreckSeconds == null ? 120f : Mathf.Max(5f, CfgWreckSeconds.Value);
        }

        /// <summary>Wrecks time out. Only the master destroys the scene object;
        /// everyone else just forgets it, and the object goes away under them
        /// when the master's Destroy arrives.</summary>
        static void Wrecks()
        {
            if (_burning.Count == 0) return;
            bool master = RevivalTroopInsertion.MasterClient();
            _gone.Clear();
            foreach (KeyValuePair<GameObject, float> e in _burning)
            {
                // A destroyed object always leaves the list. A wreck whose time
                // is up leaves it only on the master, which is the client that
                // takes the object away - everybody else keeps it marked until
                // the master's Destroy actually arrives, so nobody climbs into
                // a burning hull in the seconds in between.
                if (e.Key == null || (master && Time.time > e.Value))
                    _gone.Add(e.Key);
            }
            for (int i = 0; i < _gone.Count; i++)
            {
                GameObject go = _gone[i];
                _burning.Remove(go);
                if (go == null || !master) continue;
                Forget(go);
                HeliFlight.NetDestroy(go);
                RevivalPlugin.L.LogInfo("PlayerHeli: a burnt-out wreck was cleared away.");
            }
            _gone.Clear();
        }

        /// <summary>
        /// Things the cast goes through rather than breaks on.
        ///
        /// The machine itself and the man at the controls, obviously - but also
        /// everything ALIVE, and that is not politeness. The ray leaves the
        /// middle of the cabin and the crew are sitting in it: the pilot five
        /// metres forward, the passengers behind. Without this a helicopter with
        /// anybody aboard would explode on its own crew in the first frame of
        /// flight. A dropped item on the ground is in the list for the same
        /// reason a man is - eleven tonnes are not stopped by a rucksack.
        ///
        /// A VEHICLE is deliberately NOT in the list. Flying into a truck is a
        /// crash, and it should be.
        /// </summary>
        static bool Through(GameObject hit)
        {
            if (hit == null) return true;
            Transform t = hit.transform;

            // FIELD 2026-09-20: "hit MainChar_Upper_arm.R at (1209, 930, 1560) -
            // the machine is down", forty-five seconds into a flight at four
            // hundred metres with nothing in the sky but the pilot. The ray had
            // found the pilot's own right arm, and the walk below did not
            // recognise it: what the cast gets handed is a BONE, and a bone sits
            // ten or more parents under the object the movement controller is
            // on, while the walk gave up after eight. So the two certain
            // answers are asked FIRST, and they are asked of the whole chain -
            // IsChildOf climbs to the root, not to a fixed depth.
            if (_heli != null && t.IsChildOf(_heli.transform)) return true;
            if (_body != null && t.IsChildOf(_body)) return true;

            // Every man in this game wears the same rig, the player and every
            // NPC alike (NPC_AI2 aims through _playerObjectsManager.MainChar_*).
            // A collider called MainChar_something is therefore a limb, whoever
            // it belongs to and wherever it hangs in the scene - and a limb is
            // never the thing an eleven tonne machine breaks on.
            if (hit.name.StartsWith("MainChar")) return true;

            for (int up = 0; up < 24 && t != null; up++)
            {
                Type[] alive = Living();
                for (int i = 0; i < alive.Length; i++)
                    if (alive[i] != null && t.GetComponent(alive[i]) != null) return true;
                t = t.parent;
            }
            return false;
        }

        static Type[] _alive;

        /// <summary>Types whose objects a helicopter never breaks on. Names, not
        /// types: the plugin references no Assembly-CSharp. Same list the patrol
        /// driver refuses to crush, minus the vehicle.</summary>
        static readonly string[] AliveNames = new string[] {
            "PlayerMovementController", "PlayerNetworkController",
            "NPC_AI2", "Animal_AI", "ItemSpawned",
        };

        static Type[] Living()
        {
            if (_alive != null) return _alive;
            _alive = new Type[AliveNames.Length];
            for (int i = 0; i < AliveNames.Length; i++)
                _alive[i] = RevivalPlugin.TypeByName(AliveNames[i]);
            return _alive;
        }

        static Type _tTerrain;
        static bool _terrainLooked;

        /// <summary>Terrain and mesh roads. Both are the floor's business, not
        /// the crash test's.</summary>
        static bool IsGround(GameObject go)
        {
            if (go == null) return false;
            if (!_terrainLooked)
            {
                _terrainLooked = true;
                _tTerrain = RevivalPlugin.TypeByName("UnityEngine.Terrain");
            }
            if (_tTerrain != null && go.GetComponent(_tTerrain) != null) return true;
            string name = go.name;
            return name.IndexOf("errain") >= 0 || name.IndexOf("Road") >= 0
                   || name.IndexOf("road") >= 0;
        }

        static Component _life;
        static float _lifeUntil;
        static bool _hurtWarned;

        /// <summary>Health off the local player through the game's OWN gate:
        /// PlayerLifeDataManager.PlayerApplyDamage consults CanApplyDamage, so
        /// admin god mode and armour keep working and nothing here has to know
        /// how health is stored. Same seam the gas cloud uses.</summary>
        static void Hurt(float damage)
        {
            if (damage <= 0f || _hurtWarned) return;
            try
            {
                if (_life == null || Time.time > _lifeUntil)
                {
                    _lifeUntil = Time.time + 2f;
                    Type t = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                    Transform root = LocalPlayerRoot();
                    _life = (t == null || root == null)
                        ? null : root.GetComponentInChildren(t);
                }
                if (_life == null) return;
                MethodInfo m = AccessTools.Method(_life.GetType(), "PlayerApplyDamage", null, null);
                if (m == null)
                {
                    _hurtWarned = true;
                    RevivalPlugin.L.LogWarning("PlayerHeli: PlayerApplyDamage not found "
                        + "- the machine burns, the crash costs no health.");
                    return;
                }
                ParameterInfo[] ps = m.GetParameters();
                object[] args = new object[ps.Length];
                bool placed = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (!placed && pt == typeof(float)) { args[i] = damage; placed = true; }
                    else if (pt == typeof(string)) args[i] = string.Empty;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                m.Invoke(_life, args);
            }
            catch (Exception ex)
            {
                _hurtWarned = true;
                RevivalPlugin.L.LogWarning("PlayerHeli crash damage: " + ex.Message);
            }
        }

        // =============================================================== jump

        /// <summary>
        /// Out of the door. Anyone aboard may press it, pilot and passengers
        /// alike; on the ground it is the ordinary way out, in the air it is a
        /// jump. What happens after the jump is not this file's business:
        /// Parachute.Jump either opens a canopy on the man's back or reports
        /// why it did not, and the fall is the game's own.
        /// </summary>
        static void Jump()
        {
            GameObject go = _heli;
            if (go == null) return;
            bool flying = _pilot ? !_onGround : Airborne(go);
            if (!flying) { Leave(true); return; }
            // BailOut governs BOTH ways out in flight - this key and the old
            // shift-and-board-key. Switched off, the machine has to be landed,
            // and then the parachute has nothing to open over.
            if (CfgBailOut != null && !CfgBailOut.Value)
            {
                Hint(Text.LandFirst(), 4f);
                return;
            }

            float k = K;
            bool wasPilot = _pilot;
            Vector3 drift = _vel;
            Transform tr = go.transform;
            Vector3 door = tr.position + tr.rotation * (new Vector3(-8f, 0f, 2f) * k);
            float floor;
            float height = Floor(tr.position, out floor)
                ? (tr.position.y - floor) / k : 0f;

            // Out first, canopy second. Leave puts the man at the door and gives
            // the camera and his own legs back; the parachute state then writes
            // his position itself, and nothing of this file's is left to write
            // it again afterwards.
            ExitTrace("jump at " + Mathf.RoundToInt(height) + " m");
            Leave(false);

            string why;
            bool canopy = Parachute.Jump(door, height, out why);
            if (!string.IsNullOrEmpty(why)) Hint(why, 5f);
            RevivalPlugin.L.LogInfo("PlayerHeli: jumped at "
                + Mathf.RoundToInt(height) + " m, canopy " + canopy + ".");

            // A machine the PILOT has just left in the air is a machine nobody
            // is flying: the flight loop that integrated its speed went out of
            // the door with him, and without this it would hang there for ever.
            // So it goes down - which is what an abandoned helicopter does, and
            // it is the reason to be wearing the canopy that just opened.
            if (wasPilot && (CfgCrash == null || CfgCrash.Value))
                Abandon(go, drift, true);
        }

        // ========================================================== the object

        static MethodInfo _instantiate;
        static bool _looked;
        static Type _tDummy, _tCollider, _tBox, _tView, _tInterp, _tEvents;
        static FieldInfo _fStart, _fOptions, _fDummyObject;
        static MethodInfo _mEventsInstance, _mViewId, _mFind;
        static PropertyInfo _pColliderEnabled, _pBoxCentre, _pBoxSize, _pInterpEnabled;

        static bool LookUp()
        {
            if (_looked) return _instantiate != null;
            _looked = true;
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli: PhotonNetwork not found.");
                return false;
            }
            MethodInfo[] all = photon.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name != "InstantiateSceneObject") continue;
                ParameterInfo[] ps = all[i].GetParameters();
                if (ps.Length == 5 && ps[0].ParameterType == typeof(string)
                    && ps[4].ParameterType == typeof(object[]))
                    _instantiate = all[i];
            }
            if (_instantiate == null)
                RevivalPlugin.L.LogWarning("PlayerHeli: "
                    + "PhotonNetwork.InstantiateSceneObject not found - no machine "
                    + "can be put down.");
            return _instantiate != null;
        }

        static GameObject Instantiate(Vector3 at, float heading)
        {
            if (!LookUp()) return null;
            try
            {
                object[] data = new object[] { Marker, Scale() };
                ParameterInfo[] ps = _instantiate.GetParameters();
                object group = Convert.ChangeType(0, ps[3].ParameterType);
                GameObject go = _instantiate.Invoke(null, new object[] {
                    Prefab, at, Quaternion.Euler(0f, heading, 0f), group, data })
                    as GameObject;
                if (go == null)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli: Photon returned null for \""
                        + Prefab + "\" - not in a room?");
                    return null;
                }
                Prepare(go);
                Idle(go);
                go.transform.position = at;
                return go;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli: spawn failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return null;
            }
        }

        /// <summary>The vanilla mover must never take over. HelicopterDummy's
        /// own movement returns at once while startPosition is zero, so a
        /// machine of ours is one the game will not touch.</summary>
        static void Idle(GameObject go)
        {
            try
            {
                if (_tDummy == null) _tDummy = RevivalPlugin.TypeByName("HelicopterDummy");
                if (_tDummy == null) return;
                Component mover = go.GetComponent(_tDummy);
                if (_fStart == null) _fStart = AccessTools.Field(_tDummy, "startPosition");
                if (mover != null && _fStart != null) _fStart.SetValue(mover, Vector3.zero);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerHeli idle: " + ex.Message); }
        }

        /// <summary>
        /// The same on every client: our size, the rotor colliders off (they
        /// spin, and a man standing on the disc is what happens otherwise) and
        /// one box for the cabin so nobody walks through the fuselage. The
        /// measurements are the troop helicopter's, taken from the same prefab.
        /// </summary>
        internal static void Prepare(GameObject go)
        {
            if (go == null || _all.Contains(go)) return;
            _all.Add(go);
            try
            {
                float s = Scale();
                if (_tDummy == null) _tDummy = RevivalPlugin.TypeByName("HelicopterDummy");
                Component heli = _tDummy == null ? null : go.GetComponent(_tDummy);
                object[] data = heli == null ? null : InstantiationData(heli);
                if (data != null && data.Length >= 2 && data[1] is float)
                    s = Mathf.Clamp((float)data[1], 0.5f, 2f);
                go.transform.localScale = Vector3.one * s;

                if (_tCollider == null) _tCollider = RevivalPlugin.TypeByName("Collider");
                if (_tCollider != null)
                {
                    if (_pColliderEnabled == null)
                        _pColliderEnabled = AccessTools.Property(_tCollider, "enabled");
                    Component[] cols = go.GetComponentsInChildren(_tCollider, true);
                    for (int i = 0; i < cols.Length && _pColliderEnabled != null; i++)
                        _pColliderEnabled.SetValue(cols[i], false, null);
                }
                if (_tBox == null) _tBox = RevivalPlugin.TypeByName("BoxCollider");
                if (_tBox != null)
                {
                    GameObject hull = new GameObject("NDR_FlyHeliHull");
                    hull.transform.SetParent(go.transform, false);
                    Component box = hull.AddComponent(_tBox);
                    if (_pBoxCentre == null) _pBoxCentre = AccessTools.Property(_tBox, "center");
                    if (_pBoxSize == null) _pBoxSize = AccessTools.Property(_tBox, "size");
                    // Model units (the parent scale applies): cabin from the nose
                    // to the rear of the clamshell doors, gear to roof.
                    if (_pBoxCentre != null) _pBoxCentre.SetValue(box, new Vector3(2.5f, 5.5f, 3f), null);
                    if (_pBoxSize != null) _pBoxSize.SetValue(box, new Vector3(7f, 11f, 38f), null);
                }

                // The cargo hold, after the sweep above and after the hull box:
                // its plate has to stay switched on, and it has to be the thing
                // the interact ray meets first.
                HeliHold.Attach(go);

                // Cold on arrival, on every client. The prefab's own Start plays
                // the rotor loop and never stops it, so without this a machine
                // parked for a pilot roars to itself for ever - and so does one
                // its pilot has long since walked away from.
                EngineApply(go, false);

                int view = ViewId(go);
                if (view != 0) _byView[view] = go;
                RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + view
                    + " prepared, scale " + s.ToString("0.00") + ", engine cold.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli prepare: " + ex.Message);
            }
        }

        static void Forget(GameObject go)
        {
            _all.Remove(go);
            int view = ViewId(go);
            if (view != 0)
            {
                _byView.Remove(view);
                _busyUntil.Remove(view);
            }
        }

        /// <summary>Destroyed machines leave the lists, and stale claims expire.
        /// Cheap enough to run every frame: the lists hold a handful of
        /// entries.</summary>
        static void Sweep()
        {
            for (int i = _all.Count - 1; i >= 0; i--)
                if (_all[i] == null) _all.RemoveAt(i);
            if (_heli == null && (_pilot || _hadBody)) Involuntary("machine gone");

            _drop.Clear();
            foreach (KeyValuePair<int, float> e in _busyUntil)
                if (Time.time > e.Value) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _busyUntil.Remove(_drop[i]);

            _drop.Clear();
            foreach (KeyValuePair<int, GameObject> e in _byView)
                if (e.Value == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _byView.Remove(_drop[i]);
        }

        static GameObject Nearest(float range)
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return null;
            Vector3 p = me.transform.position;
            GameObject best = null;
            float nearest = range;
            for (int i = 0; i < _all.Count; i++)
            {
                GameObject go = _all[i];
                // A wreck and an unmanned machine already on its way down are
                // both unavailable. In particular, nobody may board the latter
                // during the seconds between the pilot leaving and impact.
                if (go == null || Burning(go) || Falling(go)) continue;
                float d = Vector3.Distance(p, go.transform.position);
                if (d > nearest) continue;
                nearest = d;
                best = go;
            }
            return best;
        }

        static bool Busy(int view)
        {
            if (view == 0) return false;
            float until;
            return _busyUntil.TryGetValue(view, out until) && Time.time < until;
        }

        static int ViewId(GameObject go)
        {
            if (go == null) return 0;
            try
            {
                if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
                if (_tView == null) return 0;
                Component view = go.GetComponent(_tView);
                if (view == null) return 0;
                if (_mViewId == null) _mViewId = AccessTools.PropertyGetter(_tView, "viewID");
                if (_mViewId == null) return 0;
                object v = _mViewId.Invoke(view, null);
                return v == null ? 0 : (int)v;
            }
            catch { return 0; }
        }

        /// <summary>The machine behind a view id, from our own list first - a
        /// PhotonView.Find on every pose message would be a lookup in the frame
        /// loop for no gain.</summary>
        static GameObject ByView(int view)
        {
            GameObject go;
            if (_byView.TryGetValue(view, out go) && go != null) return go;
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i] == null) continue;
                if (ViewId(_all[i]) != view) continue;
                _byView[view] = _all[i];
                return _all[i];
            }
            try
            {
                if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
                if (_tView == null) return null;
                if (_mFind == null)
                    _mFind = AccessTools.Method(_tView, "Find", new Type[] { typeof(int) }, null);
                if (_mFind == null) return null;
                Component found = _mFind.Invoke(null, new object[] { view }) as Component;
                return found == null ? null : found.gameObject;
            }
            catch { return null; }
        }

        /// <summary>The prefab's own transform sync. A pilot who is not the
        /// owner has to switch it off on his copy, or every frame he writes is
        /// dragged back to whatever the master last sent.</summary>
        static void Interpolator(GameObject go, bool on)
        {
            try
            {
                if (_tInterp == null)
                    _tInterp = RevivalPlugin.TypeByName("PhotonInterpolatedTransform");
                Component c = _tInterp == null ? null : go.GetComponent(_tInterp);
                if (c == null)
                {
                    if (!_interpWarned)
                    {
                        _interpWarned = true;
                        RevivalPlugin.L.LogWarning("PlayerHeli: no "
                            + "PhotonInterpolatedTransform on this helicopter - a "
                            + "pilot who is not the host will see his own machine "
                            + "pulled back by the host's picture of it.");
                    }
                    return;
                }
                if (_pInterpEnabled == null)
                    _pInterpEnabled = AccessTools.Property(_tInterp, "enabled");
                if (_pInterpEnabled != null) _pInterpEnabled.SetValue(c, on, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli interpolator: " + ex.Message);
            }
        }

        static object[] InstantiationData(Component c)
        {
            MethodInfo pv = AccessTools.Method(c.GetType(), "get_photonView", null, null);
            object view = pv == null ? null : pv.Invoke(c, null);
            if (view == null) return null;
            MethodInfo inst = AccessTools.PropertyGetter(view.GetType(), "instantiationData");
            return inst == null ? null : inst.Invoke(view, null) as object[];
        }

        internal static bool IsOurs(Component heli)
        {
            object[] data = InstantiationData(heli);
            return data != null && data.Length >= 1 && Marker.Equals(data[0] as string);
        }

        // ============================================================== places

        /// <summary>Where a man sits, in METRES from the model origin. Seat -1
        /// is the pilot's; the rest is the cabin, two files down both sides. The
        /// origin is at gear level, x 0.9 is the fuselage centre line and +z is
        /// the nose - all three measured for the troop landings.</summary>
        static Vector3 Seat(int index)
        {
            float x = CfgSeatSide == null ? 0.9f : CfgSeatSide.Value;
            float y = CfgSeatUp == null ? 1.2f : CfgSeatUp.Value;
            float z = CfgSeatForward == null ? 5f : CfgSeatForward.Value;
            if (index < 0) return new Vector3(x, y, z);
            int row = index / 2;
            float sideways = (index % 2) == 0 ? -0.8f : 0.8f;
            return new Vector3(x + sideways, y, 2f - row * 1.4f);
        }

        /// <summary>A cabin seat nobody can be seen to occupy - the seats are
        /// not networked, so this is only a place to stand, and two riders in
        /// one seat is a cosmetic collision, not a fault.</summary>
        static int FreeSeat()
        {
            return UnityEngine.Random.Range(0, 8);
        }

        static Transform _rootCache;
        static float _rootRetry;

        /// <summary>The local player's own object, found the way the drone finds
        /// its pilot: the PlayerMovementController whose PhotonView is mine.
        /// Cached, because a FindObjectsOfType per frame is the kind of cost
        /// that never shows in a log and always shows in the frame time.</summary>
        static Transform LocalPlayerRoot()
        {
            if (_rootCache != null) return _rootCache;
            if (Time.time < _rootRetry) return null;
            _rootRetry = Time.time + 0.2f;
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerMovementController");
                if (t == null) return null;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i] as MonoBehaviour;
                    if (mb == null || !IsMine(mb)) continue;
                    _rootCache = mb.transform;
                    return _rootCache;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli: local player not found: " + ex.Message);
            }
            return null;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = null;
            try { if (get != null) view = get.Invoke(mb, null); }
            catch { view = null; }
            if (view == null) return true;            // no PhotonView: single player
            MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            try { return isMine == null || (bool)isMine.Invoke(view, null); }
            catch { return true; }
        }

        // ================================================================ keys

        static void ParseKeys()
        {
            if (_keysParsed) return;
            _keysParsed = true;
            _spawnKey = Parse(CfgSpawnKey, KeyCode.F3, "SpawnKey");
            _boardKey = Parse(CfgBoardKey, KeyCode.F, "BoardKey");
            _viewKey = Parse(CfgViewKey, KeyCode.V, "ViewKey");
            _engineKey = Parse(CfgEngineKey, KeyCode.G, "EngineKey");
            _jumpKey = Parse(CfgJumpKey, KeyCode.X, "JumpKey");
        }

        static KeyCode Parse(ConfigEntry<string> entry, KeyCode fallback, string what)
        {
            if (entry == null) return fallback;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), entry.Value, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("PlayerHeli: key \"" + entry.Value
                    + "\" for " + what + " is unknown, using " + fallback + ".");
                return fallback;
            }
        }

        static KeyCode SpawnKey() { ParseKeys(); return _spawnKey; }
        static KeyCode BoardKey() { ParseKeys(); return _boardKey; }
        static KeyCode ViewKey() { ParseKeys(); return _viewKey; }
        static KeyCode EngineKey() { ParseKeys(); return _engineKey; }
        static KeyCode JumpKey() { ParseKeys(); return _jumpKey; }

        // ================================================================= HUD

        static void Hint(string text, float seconds)
        {
            _hint = text;
            _hintUntil = Time.time + seconds;
        }

        internal static void Draw()
        {
            if (!Enabled) return;
            bool hint = !string.IsNullOrEmpty(_hint) && Time.time < _hintUntil;
            if (_heli == null && !hint) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            Color warm = new Color(1f, 0.92f, 0.70f, 1f);

            if (_heli != null && _pilot)
            {
                float k = K;
                float speed = new Vector3(_vel.x, 0f, _vel.z).magnitude / k * 3.6f;
                float floor;
                float alt = Floor(_heli.transform.position, out floor)
                    ? (_heli.transform.position.y - floor) / k : 0f;
                Line(Text.Readout(Mathf.RoundToInt(speed), Mathf.RoundToInt(alt),
                                  _onGround), cx, cy + 150f,
                     new Color(0.92f, 0.92f, 0.86f, 1f), 15);

                HeliEngine e = EngineOf(_heli);
                float power = e == null ? (_engine ? 1f : 0f) : e.Power;
                Line(Text.Power(Mathf.RoundToInt(power * 100f),
                                Mathf.RoundToInt(_vel.y / k)),
                     cx, cy + 172f,
                     power < 0.5f ? new Color(1f, 0.55f, 0.35f, 1f)
                                  : new Color(0.72f, 0.80f, 0.72f, 1f), 13);

                if (Time.time - _boardedAt < 14f)
                    Line(Text.Controls(EngineKey().ToString(), ViewKey().ToString(),
                                       JumpKey().ToString(), BoardKey().ToString()),
                         cx, cy + 194f,
                         new Color(0.80f, 0.85f, 0.90f, 1f), 13);
            }

            if (hint) Line(_hint, cx, cy + 120f, warm, 16);
        }

        /// <summary>One centred line. Centred by measurement, because
        /// GUIStyle.alignment would pull in TextAnchor from
        /// UnityEngine.TextRenderingModule, which this build does not
        /// reference - the same reason the technical measures its own.</summary>
        static void Line(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUIContent content = new GUIContent(text);
            Vector2 measured = style.CalcSize(content);
            GUI.Label(new Rect(cx - measured.x * 0.5f,
                               y + (26f - measured.y) * 0.5f,
                               measured.x, measured.y), content, style);
        }

        // ============================================================ install

        internal static void Install(Harmony harmony)
        {
            if (!Enabled) return;
            try
            {
                if (_tDummy == null) _tDummy = RevivalPlugin.TypeByName("HelicopterDummy");
                MethodInfo start = _tDummy == null ? null
                    : AccessTools.Method(_tDummy, "Start", null, null);
                if (start == null)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli: HelicopterDummy.Start not "
                        + "found - a helicopter put down here keeps the aid model's "
                        + "size and its rotor colliders.");
                }
                else
                {
                    harmony.Patch(start,
                        new HarmonyMethod(typeof(PlayerHeli).GetMethod("StartPrefix",
                            BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(PlayerHeli).GetMethod("StartPostfix",
                            BindingFlags.Public | BindingFlags.Static)),
                        null, null, null);
                }
                HeliInputHook.Install(harmony);
                HeliHold.Install(harmony);
                HeliBodyGuard.Install(harmony);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerHeli: hook failed - " + ex);
            }
        }

        /// <summary>
        /// Every client sizes and prepares our machine when it arrives - the one
        /// that spawned it, the others, and late joiners, because instantiation
        /// data travels with the object.
        ///
        /// The saved reference is the aid event's own helicopter: HelicopterDummy
        /// .Start stores itself there on a non-owner, and a machine of ours must
        /// not become the machine the game's humanitarian aid event thinks it
        /// owns. Same guard as the troop insertion's.
        /// </summary>
        public static void StartPrefix(object __instance, out object __state)
        {
            __state = null;
            try
            {
                Component heli = __instance as Component;
                if (heli == null || !IsOurs(heli)) return;
                Prepare(heli.gameObject);
                object options = EventOptions();
                if (options != null) __state = new object[] { options, _fDummyObject.GetValue(options) };
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerHeli start: " + ex.Message); }
        }

        public static void StartPostfix(object __instance, object __state)
        {
            object[] saved = __state as object[];
            if (saved == null) return;
            try { _fDummyObject.SetValue(saved[0], saved[1]); }
            catch { }
        }

        static object EventOptions()
        {
            if (_tEvents == null) _tEvents = RevivalPlugin.TypeByName("NetworkGameplayEvents");
            if (_tEvents == null) return null;
            if (_mEventsInstance == null) _mEventsInstance = AccessTools.PropertyGetter(_tEvents, "Instance");
            if (_fOptions == null) _fOptions = AccessTools.Field(_tEvents, "_helicopterDummyOptions");
            object instance = _mEventsInstance == null ? null : _mEventsInstance.Invoke(null, null);
            if (instance == null || _fOptions == null) return null;
            object options = _fOptions.GetValue(instance);
            if (options == null) return null;
            if (_fDummyObject == null) _fDummyObject = AccessTools.Field(options.GetType(), "HelicopterDummyObject");
            return _fDummyObject == null ? null : options;
        }

        // ============================================================ network

        /// <summary>
        /// Six messages, all of them small. base+0 asks the master for a
        /// machine, base+1 is the pose of a pilot who is not the master, base+2
        /// says who is aboard which machine, base+3 asks the master to take one
        /// away, base+4 is the engine of one machine going on or off, base+5 is
        /// one machine that has just been destroyed. A four-float crash payload
        /// is an immediate impact; seven floats start the visible fall of a
        /// helicopter whose pilot left it. Everything else - the
        /// machine itself, its position for everyone who is not flying it - is
        /// the prefab's own Photon replication.
        ///
        /// The last two are HERE and not derived locally for the same reason the
        /// pose is: the rotor spin, the engine sound and the fire are drawn by
        /// every client for itself, so every client has to be told.
        /// </summary>
        internal static class Net
        {
            internal const int SpawnRequest = 0;
            internal const int PoseUpdate = 1;
            internal const int Aboard = 2;
            internal const int RemoveRequest = 3;
            internal const int EngineState = 4;
            internal const int Crashed = 5;

            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optType;

            static int Base()
            {
                int b = CfgEventCode == null ? 170 : CfgEventCode.Value;
                return Mathf.Clamp(b, 0, 194);
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
                        RevivalPlugin.L.LogWarning("PlayerHeli net: RaiseEvent or "
                            + "OnEventCall missing - only the master can put a "
                            + "helicopter down and fly it.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("PlayerHeli net hooked: event codes "
                        + Base() + "-" + (Base() + 5) + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("PlayerHeli net not hooked: " + ex);
                }
            }

            internal static void Send(int kind, float[] content, bool reliable)
            {
                if (!_hooked) return;
                try
                {
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] {
                        (byte)(Base() + kind), content, reliable, opts });
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli net send: " + ex.Message);
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                try
                {
                    int kind = code - Base();
                    if (kind < 0 || kind > 5) return;
                    float[] f = content as float[];
                    if (f == null) return;

                    if (kind == SpawnRequest)
                    {
                        if (!Enabled || f.Length < 4) return;
                        if (!RevivalTroopInsertion.MasterClient()) return;
                        RevivalPlugin.L.LogInfo("PlayerHeli: player " + sender
                            + " asked for a helicopter.");
                        Build(new Vector3(f[0], f[1], f[2]), f[3]);
                        return;
                    }

                    if (kind == PoseUpdate)
                    {
                        if (f.Length < 7) return;
                        int view = (int)f[0];
                        _busyUntil[view] = Time.time + 5f;
                        // Only the owner may move the object. Everyone else has
                        // it from the owner's own transform stream already.
                        if (!RevivalTroopInsertion.MasterClient()) return;
                        GameObject go = ByView(view);
                        if (go == null) return;
                        go.transform.position = new Vector3(f[1], f[2], f[3]);
                        go.transform.rotation = Quaternion.Euler(f[5], f[4], -f[6]);
                        return;
                    }

                    if (kind == Aboard)
                    {
                        if (f.Length < 2) return;
                        int view = (int)f[0];
                        if (f[1] > 0.5f) _busyUntil[view] = Time.time + 5f;
                        else _busyUntil.Remove(view);
                        return;
                    }

                    if (kind == EngineState)
                    {
                        if (f.Length < 2) return;
                        GameObject go = ByView((int)f[0]);
                        if (go != null) EngineApply(go, f[1] > 0.5f);
                        return;
                    }

                    if (kind == Crashed)
                    {
                        if (f.Length < 4) return;
                        GameObject wreck = ByView((int)f[0]);
                        if (wreck == null) return;
                        _busyUntil.Remove((int)f[0]);
                        if (f.Length >= 7)
                        {
                            wreck.transform.position = new Vector3(f[1], f[2], f[3]);
                            Abandon(wreck, new Vector3(f[4], f[5], f[6]), false);
                            return;
                        }
                        Burn(wreck, new Vector3(f[1], f[2], f[3]));
                        DestroyedAboard(wreck, "crash on the pilot's client");
                        return;
                    }

                    if (f.Length < 1) return;
                    if (!RevivalTroopInsertion.MasterClient()) return;
                    int id = (int)f[0];
                    if (Busy(id)) return;
                    GameObject gone = ByView(id);
                    if (gone == null) return;
                    Forget(gone);
                    HeliFlight.NetDestroy(gone);
                    RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + id
                        + " removed for player " + sender + ".");
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli net receive: " + ex.Message);
                }
            }
        }

        // ============================================================== lines

        /// <summary>Everything the player reads. Russian for a Russian client,
        /// English otherwise - the file is UTF-8 without BOM and build.ps1
        /// compiles it with /codepage:65001.</summary>
        internal static class Text
        {
            internal static string Spawned(string key)
            {
                return Loc.T("Вертолёт подан - " + key + ", чтобы сесть",
                             "Helicopter ready - press " + key + " to get in");
            }

            internal static string SpawnFailed()
            {
                return Loc.T("Вертолёт не появился - смотри лог",
                             "The helicopter did not appear - see the log");
            }

            internal static string Asked()
            {
                return Loc.T("Запрос отправлен хосту", "Asked the host");
            }

            internal static string Removed()
            {
                return Loc.T("Вертолёт убран", "Helicopter removed");
            }

            internal static string Occupied()
            {
                return Loc.T("В этом вертолёте уже есть пилот",
                             "Somebody is already flying this one");
            }

            internal static string NotYours()
            {
                return Loc.T("Этот вертолёт не убрать отсюда",
                             "This one cannot be removed from here");
            }

            internal static string NoPlayer()
            {
                return Loc.T("Игрок не найден", "Player not found");
            }

            internal static string CamBusy()
            {
                return Loc.T("Обзор занят - сначала выйди из другого вида",
                             "The view is taken - leave the other view first");
            }

            internal static string AtControls()
            {
                return Loc.T("Ты за штурвалом", "You have the controls");
            }

            internal static string EngineOn()
            {
                return Loc.T("Запуск двигателя - винт раскручивается",
                             "Starting up - the rotor is winding up");
            }

            internal static string EngineOff()
            {
                return Loc.T("Двигатель остановлен", "Engine shut down");
            }

            internal static string Wrecked()
            {
                return Loc.T("Машина разбита", "The machine is wrecked");
            }

            internal static string Seated()
            {
                return Loc.T("Ты в грузовой кабине", "You are in the cabin");
            }

            internal static string LandFirst()
            {
                return Loc.T("Сначала посади машину (Shift - прыжок)",
                             "Land first (shift to jump out)");
            }

            internal static string HoldFull()
            {
                return Loc.T("В грузовом отсеке ещё лежит груз - сначала разгрузи",
                             "The hold is not empty - unload it first");
            }

            internal static string Readout(int kmh, int metres, bool onGround)
            {
                string state = onGround
                    ? Loc.T("на земле", "on the ground")
                    : (metres + Loc.T(" м", " m"));
                return kmh + Loc.T(" км/ч", " km/h") + "   " + state;
            }

            /// <summary>The second line of the readout: what the engine is
            /// doing, and how fast the machine is going up or down. Both are
            /// there because both were invisible before - a pilot could not tell
            /// a cold machine from a stalled one, nor a descent from a fall.</summary>
            internal static string Power(int percent, int climb)
            {
                string engine = percent <= 0
                    ? Loc.T("двигатель выключен", "engine off")
                    : (Loc.T("тяга ", "power ") + percent + "%");
                string vertical = climb == 0 ? "" : ("   "
                    + (climb > 0 ? "+" : "") + climb + Loc.T(" м/с", " m/s"));
                return engine + vertical;
            }

            /// <summary>The keys are passed in rather than written out: five of
            /// them are configurable now, and a help line that names the
            /// defaults while the config says something else is worse than no
            /// help line at all.</summary>
            internal static string Controls(string engine, string view,
                                            string jump, string board)
            {
                return Loc.T(
                    "W/S/A/D - движение, мышь - курс, пробел/Ctrl - высота, "
                    + engine + " - двигатель, " + view + " - вид, "
                    + jump + " - прыжок, " + board + " - выйти",
                    "W/S/A/D move, mouse steers, space/ctrl climb and sink, "
                    + engine + " engine, " + view + " view, "
                    + jump + " jump, " + board + " out");
            }
        }
    }

    /// <summary>
    /// The visible descent of an unmanned player helicopter. It is arithmetic,
    /// not a Rigidbody: the networked scene object already owns its transform,
    /// and adding Unity physics to it would make its colliders and Photon fight
    /// over the same machine. The reliable fall event starts this component on
    /// every peer from one position and velocity instead.
    /// </summary>
    public sealed class HeliCrashFall : MonoBehaviour
    {
        const float Gravity = 9.81f;
        const float Terminal = 48f;
        const float MaxFall = 35f;

        Vector3 _drift;
        float _down;
        float _floor;
        float _nextFloor;
        float _life;
        float _rollRate;
        float _yawRate;
        bool _begun;
        bool _landed;
        GameObject _trail;

        internal void Begin(Vector3 velocity)
        {
            if (_begun) return;
            _begun = true;
            float k = PlayerHeli.K;
            _drift = new Vector3(velocity.x, 0f, velocity.z);
            _down = Mathf.Max(1.5f * k, -velocity.y);
            _rollRate = 22f + Mathf.Min(24f, _drift.magnitude / Mathf.Max(1f, k));
            _yawRate = (_drift.x + _drift.z) >= 0f ? 13f : -13f;

            if (!PlayerHeli.CrashFloor(transform.position, out _floor))
                _floor = transform.position.y - 120f * k;

            // An unscaled, world-space emitter is moved with the engine deck.
            // Its old particles stay behind, making a real trail rather than a
            // ball of smoke glued to the fuselage.
            _trail = new GameObject("NDR_PlayerHeliCrashTrail");
            TrailAt();
            FireEffect.SpawnDroneFire(_trail, true);
        }

        void Update()
        {
            if (!_begun || _landed) return;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float k = PlayerHeli.K;
            _life += dt;

            _down = Mathf.Min(Terminal * k, _down + Gravity * k * dt);
            _drift = Vector3.Lerp(_drift, Vector3.zero, Mathf.Clamp01(dt * 0.12f));

            Vector3 at = transform.position + _drift * dt
                       + Vector3.down * (_down * dt);
            transform.position = at;

            // A heavy airframe does not tumble like the quadcopter. It develops
            // one broad roll, yaws away and lowers the nose while it descends.
            transform.Rotate(new Vector3(7f, _yawRate, _rollRate) * dt, Space.Self);
            TrailAt();

            if (Time.time >= _nextFloor)
            {
                _nextFloor = Time.time + 0.18f;
                float floor;
                if (PlayerHeli.CrashFloor(at, out floor)) _floor = floor;
            }

            // The origin is near the gear plane. A small clearance prevents the
            // last integration step from burying the hull before Burn lays it
            // on the exact floor.
            if (at.y <= _floor + 0.7f * k || _life >= MaxFall) Land();
        }

        void TrailAt()
        {
            if (_trail == null) return;
            _trail.transform.position = transform.position
                + transform.rotation * (new Vector3(0f, 3.2f, -2.5f) * PlayerHeli.K);
        }

        void Land()
        {
            if (_landed) return;
            _landed = true;
            Vector3 impact = transform.position;
            impact.y = _floor + 0.7f * PlayerHeli.K;
            transform.position = impact;
            if (_trail != null)
            {
                FireEffect.StopEmitting(_trail);
                Light[] lights = _trail.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                    if (lights[i] != null) UnityEngine.Object.Destroy(lights[i].gameObject);
                UnityEngine.Object.Destroy(_trail, 7f);
                _trail = null;
            }
            PlayerHeli.FinishAbandonedCrash(gameObject, impact);
            UnityEngine.Object.Destroy(this);
        }

        void OnDestroy()
        {
            if (_trail == null) return;
            FireEffect.StopEmitting(_trail);
            UnityEngine.Object.Destroy(_trail, 7f);
            _trail = null;
        }
    }

    /// <summary>
    /// How a wreck comes to rest, and the answer to two complaints in a row.
    ///
    /// The FIRST one (2026-09-21) was that the landing threw the fall away: it
    /// rebuilt a level pose out of the heading, so a machine that came down
    /// inverted stood itself up the instant it touched. That was answered by
    /// keeping the arrival rotation - and keeping ALL of it is what produced the
    /// SECOND one (2026-09-22): "das wrack schwebt mehrere meter ueber dem
    /// boden". The field log says why in a single line. The machine arrived at
    /// 38 degrees of nose and 163 of bank; the old code drove the nose eleven
    /// degrees FURTHER down and then put the deepest corner of a thirty-eight
    /// unit hull box on the floor. A box that long, stood on one corner, holds
    /// its own origin some sixteen units - six metres - into the air. The wreck
    /// was exactly where the arithmetic asked for: balanced on the tip of its
    /// nose.
    ///
    /// Eleven tonnes do not balance, they LIE. The fuselage goes flat along the
    /// ground and the machine ends up on its skids, on one of its flanks or on
    /// its roof - whichever of the four the bank it arrived with is nearest to.
    /// That is what this component does now, and it is also what keeps the first
    /// complaint answered: the heading is untouched, the side it went over on is
    /// the side it arrived on, the place is the place it came down in, and a few
    /// degrees of the arrival lean are left in so the result is not machine-flat.
    ///
    ///   yaw    exactly as it arrived, and so is the ground track
    ///   nose   the arrival nose relaxed to at most five degrees
    ///   bank   the nearest of 0, +-90 and 180, plus half the lean it arrived
    ///          with, up to six degrees
    ///
    /// GROUND, NOT SEA LEVEL. The pose is tilted onto the slope the machine is
    /// lying on, read from four floor samples around it, because a hull lying
    /// flat on a hillside has one end of itself in the air otherwise - which is
    /// the same float by another route. The height is not one sample either:
    /// each of the eight hull-box corners is measured against the floor AT ITS
    /// OWN PLACE and the HIGHEST of those answers wins, so the machine rests on
    /// its highest contact the way a rigid body does and never hangs over a
    /// rise. The last few centimetres are given away on purpose - a hull resting
    /// exactly tangent to the height data shows daylight under itself on every
    /// bump, one bedded slightly into the ground does not.
    ///
    /// The last word belongs to what is DRAWN, once, when the movement is over:
    /// see Drop. The hull box is a measurement of the INTACT prefab, and the
    /// wreck is a different mesh swapped into that prefab's renderers. If the
    /// swapped mesh sits higher in its own space the box says "down" while the
    /// player sees a machine hanging in the air, so Drop measures the mesh
    /// renderers themselves and closes whatever gap is left. It can only ever
    /// move the wreck DOWN.
    /// </summary>
    public sealed class HeliWreckSettle : MonoBehaviour
    {
        const float Seconds = 1.1f;

        // How far the hull is bedded into the ground at rest, in metres.
        const float Bed = 0.15f;

        // The hull box of Prepare, in the machine's own units: centre and half
        // size of the 7 x 11 x 38 cabin whose centre sits 2.5 across, 5.5 up
        // and 3 ahead of the origin.
        static readonly Vector3 Centre = new Vector3(2.5f, 5.5f, 3f);
        static readonly Vector3 Half = new Vector3(3.5f, 5.5f, 19f);

        readonly float[] _under = new float[8];

        Quaternion _from, _to;
        Vector3 _slope = Vector3.up;
        float _floor, _t, _arrivedAt, _over;
        bool _begun, _dropped;

        internal void Begin(float floor)
        {
            if (_begun) return;
            _begun = true;
            _floor = floor;
            _from = transform.rotation;
            _arrivedAt = transform.position.y;

            Quaternion heading;
            float pitch, roll, spin;
            Lean(_from, out heading, out pitch, out roll, out spin);

            float nose = Flat(pitch);
            float bank = Over(roll);
            _to = Slope(transform.position)
                  * heading * Quaternion.Euler(nose, spin, bank);
            Under();
            Rest();

            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("PlayerHeli: wreck arrived at "
                    + pitch.ToString("0") + " deg nose and " + roll.ToString("0")
                    + " deg bank, lies down at " + nose.ToString("0") + " / "
                    + bank.ToString("0") + " deg on ground tilted "
                    + Vector3.Angle(Vector3.up, _slope).ToString("0") + " deg.");
        }

        void Update()
        {
            if (!_begun) return;
            _t += Mathf.Min(Time.deltaTime, 0.1f);
            float u = Mathf.Clamp01(_t / Seconds);
            _over = 1f - (1f - u) * (1f - u);
            transform.rotation = Quaternion.Slerp(_from, _to, _over);
            Rest();
            if (u < 1f) return;
            Drop();
            UnityEngine.Object.Destroy(this);
        }

        /// <summary>The floor under each of the eight hull corners, in the pose
        /// the wreck is settling INTO. Eight samples once and not eight a frame:
        /// the machine does not move sideways while it settles, so the answer
        /// that counts is the one for the attitude it ends in.</summary>
        void Under()
        {
            Vector3 at = transform.position;
            Vector3 scale = transform.lossyScale;
            for (int i = 0; i < 8; i++)
            {
                Vector3 off = _to * Vector3.Scale(Corner(i), scale);
                float floor;
                _under[i] = PlayerHeli.CrashFloor(
                    new Vector3(at.x + off.x, at.y, at.z + off.z), out floor)
                    ? floor : _floor;
            }
        }

        /// <summary>
        /// Put the hull on the ground that is really under it. Each corner says
        /// how high the machine would have to sit for that corner to stand on
        /// its own floor, and the highest of the eight answers is the one the
        /// machine obeys. The ground track is never touched: a wreck settles, it
        /// does not slide.
        ///
        /// The height is on the SAME eased number as the rotation, and it has to
        /// be. The answer for the pose a machine ARRIVES in is not the answer
        /// for the pose it ends in - a hull that touches down nose first stands
        /// six metres tall for as long as it is standing on its nose - so a
        /// height written straight out would jump in the frame of the bang and
        /// then sink. Going over is one movement: the machine rolls off the
        /// attitude it hit the ground with and onto the one it keeps, and its
        /// origin rises or falls with the roll, which is what toppling is.
        /// </summary>
        void Rest()
        {
            Vector3 at = transform.position;
            Vector3 scale = transform.lossyScale;
            Quaternion rot = transform.rotation;
            float best = 0f;
            for (int i = 0; i < 8; i++)
            {
                float y = _under[i] - (rot * Vector3.Scale(Corner(i), scale)).y;
                if (i == 0 || y > best) best = y;
            }
            at.y = Mathf.Lerp(_arrivedAt, best - Bed * PlayerHeli.K, _over);
            transform.position = at;
        }

        /// <summary>
        /// The one check that cannot be argued with, run once when the machine
        /// has stopped moving: whatever is DRAWN must touch the ground. The box
        /// above belongs to the intact prefab; HeliWreckModel has since swapped
        /// a different mesh into those renderers, and a mesh whose own origin
        /// sits lower than the one it replaced leaves the whole wreck in the
        /// air. So the mesh renderers are measured - particle renderers are not
        /// MeshRenderers and the fire cannot get into this - and anything still
        /// standing clear of the highest ground under the hull is lowered by
        /// exactly that gap. Never raised: a wreck bedded into a hillside is
        /// what a crash looks like, a floating one is not.
        /// </summary>
        void Drop()
        {
            if (_dropped) return;
            _dropped = true;
            try
            {
                MeshRenderer[] drawn = GetComponentsInChildren<MeshRenderer>(false);
                bool any = false;
                float bottom = 0f;
                for (int i = 0; i < drawn.Length; i++)
                {
                    if (drawn[i] == null || !drawn[i].enabled) continue;
                    float b = drawn[i].bounds.min.y;
                    if (!any || b < bottom) bottom = b;
                    any = true;
                }
                if (!any) return;

                float ground = _under[0];
                for (int i = 1; i < 8; i++) if (_under[i] > ground) ground = _under[i];
                float gap = bottom - ground;
                if (gap <= 0.1f * PlayerHeli.K) return;

                Vector3 at = transform.position;
                at.y -= gap;
                transform.position = at;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo("PlayerHeli: the drawn wreck stood "
                        + (gap / PlayerHeli.K).ToString("0.0") + " m clear of its "
                        + "ground - the hull box does not fit the swapped mesh, "
                        + "and the wreck was put down the rest of the way.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("PlayerHeli wreck drop: " + ex.Message);
            }
        }

        /// <summary>The ground under the wreck as a tilt, from four floor
        /// samples five metres out. Steep ground is capped at twenty-eight
        /// degrees, because past that the hull would stand on the slope like a
        /// signpost instead of lying on it.</summary>
        Quaternion Slope(Vector3 at)
        {
            _slope = Vector3.up;
            float e = 5f * PlayerHeli.K;
            float west, east, south, north;
            if (!PlayerHeli.CrashFloor(at + new Vector3(-e, 0f, 0f), out west)
                || !PlayerHeli.CrashFloor(at + new Vector3(e, 0f, 0f), out east)
                || !PlayerHeli.CrashFloor(at + new Vector3(0f, 0f, -e), out south)
                || !PlayerHeli.CrashFloor(at + new Vector3(0f, 0f, e), out north))
                return Quaternion.identity;

            Vector3 n = Vector3.Cross(
                new Vector3(0f, north - south, 2f * e),
                new Vector3(2f * e, east - west, 0f)).normalized;
            if (n.y < 0.05f) return Quaternion.identity;
            float tilt = Vector3.Angle(Vector3.up, n);
            if (tilt < 0.5f) return Quaternion.identity;
            if (tilt > 28f) n = Vector3.Slerp(Vector3.up, n, 28f / tilt).normalized;
            _slope = n;
            return Quaternion.FromToRotation(Vector3.up, n);
        }

        static Vector3 Corner(int i)
        {
            return new Vector3(
                Centre.x + ((i & 1) == 0 ? -Half.x : Half.x),
                Centre.y + ((i & 2) == 0 ? -Half.y : Half.y),
                Centre.z + ((i & 4) == 0 ? -Half.z : Half.z));
        }

        /// <summary>Nose and bank in a frame that has the heading taken out of
        /// it, so "how far over is it" is one number. The spin is the leftover
        /// yaw the euler decomposition produces near the poles; it is handed
        /// back and put in again unchanged, because dropping it would turn a
        /// machine standing on its nose.</summary>
        static void Lean(Quaternion rot, out Quaternion heading,
                         out float pitch, out float roll, out float spin)
        {
            Vector3 f = rot * Vector3.forward;
            Vector3 flat = new Vector3(f.x, 0f, f.z);
            if (flat.sqrMagnitude < 0.000001f)
            {
                // Nose straight up or straight down - the heading is then
                // carried by the roof, not by the nose.
                Vector3 up = rot * Vector3.up;
                flat = new Vector3(up.x, 0f, up.z);
                if (flat.sqrMagnitude < 0.000001f) flat = Vector3.forward;
            }
            heading = Quaternion.LookRotation(flat.normalized, Vector3.up);
            Vector3 e = (Quaternion.Inverse(heading) * rot).eulerAngles;
            pitch = Wrap(e.x);
            roll = Wrap(e.z);
            spin = Wrap(e.y);
        }

        /// <summary>The nose comes down. Whatever angle the machine arrived at,
        /// what stops it is its own thirteen metres of fuselage lying along the
        /// ground, so at most five degrees of the arrival nose survive - and
        /// they keep their sign, so a machine that went in nose first still has
        /// its nose in the dirt.</summary>
        static float Flat(float deg)
        {
            float keep = Mathf.Min(5f, Mathf.Abs(deg) * 0.12f);
            return deg < 0f ? -keep : keep;
        }

        /// <summary>Which side it stops on. A helicopter comes to rest in one of
        /// four attitudes - on its skids, on either flank, or on its roof - and
        /// the machine keeps the one its arrival bank is nearest to. Half of the
        /// lean it arrived with is left in, up to six degrees, so that no two
        /// wrecks lie at exactly the same angle.</summary>
        static float Over(float deg)
        {
            float target = Mathf.Round(deg / 90f) * 90f;
            return target + Mathf.Clamp((deg - target) * 0.5f, -6f, 6f);
        }

        static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }
    }

    /// <summary>
    /// The broken airframe, out of the game's own asset list rather than out of
    /// a modelling tool (order of 2026-09-21: "Es gibt im game bereits models
    /// von kaputten helis, einfach so eins benutzen").
    ///
    /// WHICH MODEL. The asset index knows three Mi-8s: `mi-8_mchs` is the intact
    /// aid machine this feature flies, `mi-8_military` is the intact green one,
    /// and `mi-8_rusty_int` is the rusted, gutted hulk that stands on the map as
    /// scenery - mesh, material, texture and three LODs of its own
    /// (research/index_resources_assets.tsv, index_sharedassets1/5/9). That
    /// third one is the wreck, and it is the one taken here.
    ///
    /// WHY IT IS NOT LOADED BY PATH. It has no entry in
    /// research/resource_paths.tsv: only `gameplayobjects/helicopters/mi-8_mchs`
    /// and the airdrop container are addressable that way. So it is picked up
    /// the way Revival.WindSound.cs picks up the game's own wind clip - from the
    /// objects already in memory - and that is also the one thing that can fail:
    /// on a map with no rusted Mi-8 standing on it the asset was never loaded.
    /// That is not an error, it is the fallback, and the log says which of the
    /// two happened.
    ///
    /// WHAT IT DOES TO THE MACHINE. Every hull mesh under the object becomes the
    /// rusty one, the glass goes (a wreck has no windows), and every other
    /// renderer - rotor, interior fittings - is switched off, so exactly one
    /// broken hull is drawn at any distance and no stopped rotor hangs over it.
    /// Meshes and materials are only ASSIGNED, never edited: writing to a shared
    /// material would rust every Mi-8 in the world.
    ///
    /// The fallback scorches instead, and it goes through `material` and not
    /// `sharedMaterial` for exactly that reason - the copy belongs to the
    /// renderer and dies with the wreck.
    /// </summary>
    internal static class HeliWreckModel
    {
        static int _tries;
        static Mesh _hull;
        static Mesh[] _lod = new Mesh[4];
        static Material _skin;

        internal static void Apply(GameObject go)
        {
            if (go == null) return;
            try
            {
                bool want = PlayerHeli.CfgWreckModel == null
                            || PlayerHeli.CfgWreckModel.Value;
                if (want) Look();
                MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
                bool[] hulls = FindHulls(filters);
                int candidates = 0;
                for (int i = 0; i < hulls.Length; i++)
                    if (hulls[i]) candidates++;

                // Finding the rusty asset is not enough to make a safe swap.
                // The shipped prefab's resource name and its embedded mesh name
                // need not be the same. The old code hid every renderer as soon
                // as the rusty asset existed, even when it then recognised zero
                // intact hulls. That turned the whole helicopter invisible.
                // Only commit the destructive half after a real hull was found.
                bool swap = want && _hull != null && candidates > 0;
                int changed = 0, hidden = 0, scorched = 0;
                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter mf = filters[i];
                    if (mf == null) continue;
                    Renderer r = mf.GetComponent<Renderer>();
                    Mesh mesh = mf.sharedMesh;
                    string name = mesh == null ? "" : mesh.name.ToLowerInvariant();

                    if (!swap)
                    {
                        if (Scorch(r)) scorched++;
                        continue;
                    }

                    // Everything except the hull candidates is switched off -
                    // glass, rotor and interior fittings. FindHulls has already
                    // proved that at least one visible airframe will replace it.
                    if (!hulls[i])
                    {
                        if (r != null && r.enabled) { r.enabled = false; hidden++; }
                        continue;
                    }

                    Mesh rusty = Pick(name);
                    mf.sharedMesh = rusty;
                    if (r != null && _skin != null)
                    {
                        Material[] mats = new Material[Mathf.Max(1, rusty.subMeshCount)];
                        for (int m = 0; m < mats.Length; m++) mats[m] = _skin;
                        r.sharedMaterials = mats;
                    }
                    changed++;
                }

                if (swap)
                    RevivalPlugin.L.LogInfo("PlayerHeli: wreck model - "
                        + changed + " hull mesh(es) swapped to mi-8_rusty_int, "
                        + hidden + " renderer(s) switched off (glass, rotor).");
                else
                    RevivalPlugin.L.LogInfo("PlayerHeli: wreck model - the broken "
                        + "Mi-8 is " + (!want ? "switched off in the config"
                        : _hull == null ? "not loaded on this map"
                        : "loaded but no intact hull renderer was recognised")
                        + ", " + scorched + " renderer(s) "
                        + "scorched on the intact hull instead.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli wreck model: " + ex.Message);
            }
        }

        /// <summary>
        /// Mark every LOD of the intact airframe before changing a renderer. The
        /// asset index names the prefab mi-8_mchs, but Unity is allowed to give
        /// an embedded mesh a different name. Prefer the two known names. If
        /// neither survived import, use the largest non-auxiliary meshes: the
        /// hull LODs share the airframe's bounds, while glass, rotors and cabin
        /// fittings either say what they are or are substantially smaller.
        /// </summary>
        static bool[] FindHulls(MeshFilter[] filters)
        {
            bool[] answer = new bool[filters.Length];
            bool named = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                Mesh mesh = mf == null ? null : mf.sharedMesh;
                string name = mesh == null || string.IsNullOrEmpty(mesh.name)
                    ? "" : mesh.name.ToLowerInvariant();
                if (name.IndexOf("mchs") >= 0 || name.IndexOf("military") >= 0)
                {
                    answer[i] = true;
                    named = true;
                }
            }
            if (named) return answer;

            float largest = 0f;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                Mesh mesh = mf == null ? null : mf.sharedMesh;
                if (mesh == null || Auxiliary(mf, mesh)) continue;
                float size = mesh.bounds.size.sqrMagnitude;
                if (size > largest) largest = size;
            }
            if (largest <= 0f) return answer;

            // Half the linear size is one quarter of squared magnitude. It
            // catches every hull LOD without promoting small cabin furniture.
            float threshold = largest * 0.25f;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                Mesh mesh = mf == null ? null : mf.sharedMesh;
                if (mesh != null && !Auxiliary(mf, mesh)
                    && mesh.bounds.size.sqrMagnitude >= threshold)
                    answer[i] = true;
            }
            return answer;
        }

        static bool Auxiliary(MeshFilter mf, Mesh mesh)
        {
            string name = ((mf == null ? "" : mf.name) + " "
                + (mesh == null ? "" : mesh.name)).ToLowerInvariant();
            return name.IndexOf("interior") >= 0 || name.IndexOf("glass") >= 0
                || name.IndexOf("rotor") >= 0 || name.IndexOf("vint") >= 0
                || name.IndexOf("propeller") >= 0 || name.IndexOf("lopast") >= 0
                || name.IndexOf("screw") >= 0 || name.IndexOf("rusty") >= 0;
        }

        /// <summary>The rusty mesh at the same level of detail as the one it
        /// replaces, so an LOD group that swaps at distance keeps swapping
        /// between wrecks and not back to an intact machine.</summary>
        static Mesh Pick(string name)
        {
            int at = name.IndexOf("_lod");
            if (at >= 0 && at + 4 < name.Length)
            {
                int level = name[at + 4] - '0';
                if (level >= 1 && level <= 3 && _lod[level] != null) return _lod[level];
            }
            return _hull;
        }

        /// <summary>Looked up once and then remembered, the way the wind clip
        /// is. Three tries and not one, because the asset is loaded when a
        /// rusted Mi-8 stands on the map and a player who crashes on one map
        /// and flies on another would otherwise be told for ever that the
        /// model does not exist.</summary>
        static void Look()
        {
            if (_hull != null || _tries >= 3) return;
            _tries++;
            try
            {
                UnityEngine.Object[] meshes =
                    Resources.FindObjectsOfTypeAll(typeof(Mesh));
                for (int i = 0; i < meshes.Length; i++)
                {
                    Mesh m = meshes[i] as Mesh;
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    string n = m.name.ToLowerInvariant();
                    if (n.IndexOf("mi-8_rusty") < 0) continue;
                    int at = n.IndexOf("_lod");
                    if (at < 0) { if (_hull == null) _hull = m; continue; }
                    if (at + 4 >= n.Length) continue;
                    int level = n[at + 4] - '0';
                    if (level >= 1 && level <= 3 && _lod[level] == null) _lod[level] = m;
                }
                if (_hull == null) _hull = _lod[1] != null ? _lod[1] : _lod[2];

                UnityEngine.Object[] mats =
                    Resources.FindObjectsOfTypeAll(typeof(Material));
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i] as Material;
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    string n = m.name.ToLowerInvariant();
                    if (n.IndexOf("mi-8_rusty") < 0 || n.IndexOf("_lod") >= 0) continue;
                    _skin = m;
                    break;
                }
                RevivalPlugin.L.LogInfo("PlayerHeli: broken Mi-8 lookup - hull "
                    + (_hull == null ? "NOT found" : _hull.name) + ", material "
                    + (_skin == null ? "NOT found" : _skin.name) + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli wreck lookup: " + ex.Message);
            }
        }

        /// <summary>Burnt paint on the intact hull: the fallback when the broken
        /// model is not in memory. `material` and not `sharedMaterial` - the
        /// instance belongs to this renderer and is destroyed with it.</summary>
        static bool Scorch(Renderer r)
        {
            if (r == null || !r.enabled) return false;
            Material m = r.material;
            if (m == null) return false;
            if (m.HasProperty("_Color"))
                m.SetColor("_Color", new Color(0.13f, 0.12f, 0.11f, 1f));
            if (m.HasProperty("_EmissionColor"))
                m.SetColor("_EmissionColor", Color.black);
            return true;
        }
    }

    /// <summary>
    /// The bang, and it is meant to be heard (order of 2026-09-21: "gerne einen
    /// ganz lauten aufknall sound mit explosion").
    ///
    /// TWO SOURCES, NOT ONE, because one is as loud as an AudioSource goes:
    /// volume is capped at 1 and the cap was already reached. So the game's OWN
    /// explosion is played over a synthesized sub-boom at the same point in the
    /// same frame, and the two sum. The game clip gives the crack anyone playing
    /// this game recognises as an explosion; the boom underneath is the weight
    /// eleven tonnes of airframe should have and that a gas cylinder does not.
    ///
    /// The game clip is `Sounds/Gameplay/Explosions/Gas_Balon_Explode_01` from
    /// research/resource_paths.tsv - the only explosion in the game that IS at a
    /// resource path. `frag_explode-1` is in the asset list but not addressable,
    /// so it is the second try, out of what is already in memory, the way
    /// Revival.WindSound.cs finds the wind. Neither of the two is required: the
    /// synthesized boom alone still plays.
    ///
    /// The rolloff carries much further than the old one. Nineteen metres of
    /// full volume (minDistance) against the old six, and 900 units of reach
    /// against 520: a helicopter going in is a thing the next valley hears.
    /// </summary>
    internal static class HeliCrashSound
    {
        static AudioClip _boom;
        static AudioClip _bang;
        static bool _lookedUpBang;

        internal static void Play(Vector3 at)
        {
            try
            {
                float k = PlayerHeli.K;
                bool any = Source(at, Bang(), 1f, 52f * k, 1f);
                any |= Source(at, Boom(), 1f, 60f * k, 0.92f);
                if (!any && RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("PlayerHeli: no crash sound could "
                        + "be built - the impact is silent.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("PlayerHeli crash sound: " + ex.Message);
            }
        }

        static bool Source(Vector3 at, AudioClip clip, float volume,
                           float full, float pitch)
        {
            if (clip == null) return false;
            GameObject go = new GameObject("NDR PlayerHeli Crash Sound");
            go.transform.position = at;
            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = false;
            source.spatialBlend = 1f;
            source.minDistance = full;
            source.maxDistance = 900f * PlayerHeli.K;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.dopplerLevel = 0f;
            source.pitch = pitch;
            source.volume = volume;
            source.Play();
            UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 1f);
            return true;
        }

        /// <summary>The game's own explosion if it can be had.</summary>
        static AudioClip Bang()
        {
            if (_bang != null || _lookedUpBang) return _bang;
            _lookedUpBang = true;
            try
            {
                _bang = Resources.Load(
                    "Sounds/Gameplay/Explosions/Gas_Balon_Explode_01",
                    typeof(AudioClip)) as AudioClip;
                if (_bang == null)
                {
                    UnityEngine.Object[] all =
                        Resources.FindObjectsOfTypeAll(typeof(AudioClip));
                    for (int i = 0; i < all.Length; i++)
                    {
                        AudioClip c = all[i] as AudioClip;
                        if (c == null || string.IsNullOrEmpty(c.name)) continue;
                        string n = c.name.ToLowerInvariant();
                        if (n.IndexOf("explode") < 0 && n.IndexOf("explosion") < 0)
                            continue;
                        if (n.IndexOf("underwater") >= 0 || n.IndexOf("blood") >= 0)
                            continue;
                        _bang = c;
                        break;
                    }
                }
                RevivalPlugin.L.LogInfo("PlayerHeli: crash bang - "
                    + (_bang == null ? "no game explosion clip found, the "
                       + "synthesized boom carries it alone" : "using " + _bang.name)
                    + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli bang lookup: " + ex.Message);
            }
            return _bang;
        }

        /// <summary>
        /// The weight under the crack: four seconds, and everything in it is
        /// lower and longer than the clip it replaces. The old one decayed at
        /// 1.55 and started at 42 Hz; this starts at 33, sweeps down, decays at
        /// 0.85, and carries a rumble tail that is still audible at two seconds
        /// - which is what makes the difference between a bang and an aircraft
        /// going in.
        /// </summary>
        static AudioClip Boom()
        {
            if (_boom != null) return _boom;
            const int rate = 22050;
            int count = rate * 4;
            float[] data = new float[count];
            System.Random random = new System.Random(81731);
            float rumble = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / rate;
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);

                // The transient: the first fortieth of a second, and the only
                // part that is allowed to be white.
                float crack = noise * Mathf.Exp(-t * 26f);

                // The body: a falling sine from 33 Hz, and the slow decay is the
                // whole point.
                float body = Mathf.Sin(2f * Mathf.PI * (33f - 5.5f * t) * t)
                           * Mathf.Exp(-t * 0.85f);

                // Tearing metal over the top of it, gone in half a second.
                float metal = Mathf.Sin(2f * Mathf.PI * 146f * t)
                            * Mathf.Exp(-t * 4.4f) * (0.6f + 0.4f * noise);

                // The tail: noise dragged through a one-pole low pass, so what
                // is left after a second is rumble and not hiss.
                rumble += (noise - rumble) * 0.020f;
                float tail = rumble * Mathf.Exp(-t * 0.55f) * 3.4f;

                data[i] = Soft(crack * 0.62f + body * 1.00f
                               + metal * 0.20f + tail * 0.75f);
            }
            _boom = AudioClip.Create("NDR_PlayerHeliCrash", count, 1, rate, false);
            _boom.SetData(data, 0);
            return _boom;
        }

        /// <summary>Clipping a sum this hot at plus and minus one buzzes. A
        /// tanh-shaped knee keeps the loudness and loses the buzz.</summary>
        static float Soft(float x)
        {
            if (x > 3f) return 1f;
            if (x < -3f) return -1f;
            return x * (27f + x * x) / (27f + 9f * x * x);
        }
    }

    /// <summary>
    /// The engine of ONE machine: the rotor speed, the sound and the spool
    /// state, as a component sitting on the helicopter itself.
    ///
    /// WHY A COMPONENT AND NOT A TABLE. Rotor spin and engine sound are drawn by
    /// every client for itself - HelicopterDummy.Update turns the disc
    /// everywhere, and its Start plays the loop everywhere - so the state has to
    /// live next to the object on every client, including those that never had a
    /// pilot in it. The object is also the only key that works in single player,
    /// where there is no PhotonView and therefore no view id.
    ///
    /// WHAT IT TOUCHES. Two public fields of the game's own mover
    /// (rotor_flight_speed, rotor_rear_speed) and its AudioSource. Both originals
    /// are read once and kept, and full power restores them exactly - so a
    /// machine that this file never switches off is a machine the game still
    /// flies the way it always did.
    ///
    /// Sound and rotor follow POWER, not the switch: that is the whole point of
    /// the spool. A cold start winds the disc up over SpoolSeconds and the pitch
    /// comes up with it; a shutdown in the air winds both down while the machine
    /// sinks.
    /// </summary>
    public sealed class HeliEngine : MonoBehaviour
    {
        Component _mover;
        FieldInfo _fMain, _fTail;
        float _mainFull, _tailFull;
        AudioSource _audio;
        float _volumeFull = 1f, _pitchFull = 1f;
        bool _read, _dead;
        int _tries;
        float _power;
        bool _running;
        float _lastApplied = -1f;

        /// <summary>What the pilot (or another client's message) asked for.
        /// Setting it does not move anything by itself - Advance does.</summary>
        public bool Running
        {
            get { return _running; }
            set { _running = value; }
        }

        public float Power { get { return _power; } }

        /// <summary>Move the spool one frame closer to what was asked for, and
        /// put the result on the rotor and the sound. Called by the pilot's own
        /// flight; Update does the same for every machine nobody is flying, so a
        /// helicopter left running across the map still winds down when its
        /// pilot shuts it off.</summary>
        public void Advance(float dt, float seconds)
        {
            if (_dead) { Write(0f); return; }
            float step = dt / Mathf.Max(0.5f, seconds);
            _power = Mathf.Clamp01(_power + (_running ? step : -step));
            Write(_power);
        }

        /// <summary>A wreck: the engine is not shut down, it is gone. Nothing
        /// starts it again.</summary>
        public void Kill()
        {
            _dead = true;
            _running = false;
            _power = 0f;
            Write(0f);
            if (_audio != null)
            {
                try { _audio.Stop(); } catch { }
            }
        }

        void Update()
        {
            // The pilot's own machine is advanced by the flight, at the same
            // moment the power is used. This is for all the others.
            if (PlayerHeli.Flown(gameObject)) return;
            Advance(Time.deltaTime, PlayerHeli.SpoolTime());
        }

        void Write(float power)
        {
            if (!Read()) return;

            // The rotor speeds go through reflection, so they are written only
            // when the number actually moved. The SOUND is checked every time,
            // and that is not laziness: HelicopterDummy.Start calls
            // _audioSrc.Play() itself, and on a machine we silenced BEFORE its
            // Start ran - which is every machine, because Prepare is a Start
            // prefix - the loop would come back a frame later and stay. Two
            // property reads a frame is the price of a silent parked machine.
            if (Mathf.Abs(power - _lastApplied) >= 0.002f)
            {
                _lastApplied = power;
                try
                {
                    if (_fMain != null) _fMain.SetValue(_mover, _mainFull * power);
                    if (_fTail != null) _fTail.SetValue(_mover, _tailFull * power);
                }
                catch { }
            }

            if (_audio == null) return;
            try
            {
                if (power <= 0.001f)
                {
                    if (_audio.isPlaying) _audio.Stop();
                    return;
                }
                _audio.volume = _volumeFull * power;
                // The loop was recorded at full speed, so a winding disc has to
                // be played slower or it sounds like a machine at full power
                // that is merely quiet.
                _audio.pitch = _pitchFull * (0.35f + 0.65f * power);
                if (!_audio.isPlaying) _audio.Play();
            }
            catch { }
        }

        /// <summary>Find the mover, its two speed fields and the audio source
        /// once. HelicopterDummy.Start may not have run yet when the first order
        /// arrives, so a failed read is retried - but not for ever: a prefab
        /// that never fills those fields would otherwise be a reflection lookup
        /// every frame of the session.</summary>
        bool Read()
        {
            if (_read) return _mover != null;
            if (_tries > 240) { _read = true; return false; }
            _tries++;

            Type t = RevivalPlugin.TypeByName("HelicopterDummy");
            if (t == null) { _read = true; return false; }
            _mover = GetComponent(t);
            if (_mover == null) return false;
            _fMain = AccessTools.Field(t, "rotor_flight_speed");
            _fTail = AccessTools.Field(t, "rotor_rear_speed");
            try
            {
                if (_fMain != null) _mainFull = Convert.ToSingle(_fMain.GetValue(_mover));
                if (_fTail != null) _tailFull = Convert.ToSingle(_fTail.GetValue(_mover));
            }
            catch { }
            // A zero here would be a machine that can never spin again. It means
            // the field was read before Start filled it, so wait for the retry.
            if (_mainFull == 0f && _tailFull == 0f) return false;

            _audio = GetComponentInChildren<AudioSource>();
            if (_audio != null)
            {
                _volumeFull = _audio.volume;
                _pitchFull = _audio.pitch == 0f ? 1f : _audio.pitch;
            }
            _read = true;
            _lastApplied = -1f;                 // the first real write is due
            RevivalPlugin.L.LogInfo("PlayerHeli engine: rotor " + _mainFull
                + "/" + _tailFull + ", sound "
                + (_audio == null ? "none" : "found") + ".");
            return true;
        }
    }

    /// <summary>
    /// The body stays out of the way while its owner is in a helicopter. The
    /// game's own family of "cannot do that right now" predicates is the door -
    /// the same one the FPV drone uses - so nothing has to be switched off and
    /// nothing has to be switched back on: the moment the flight ends, the man
    /// is under his own control again with no cleanup at all.
    /// </summary>
    internal static class HeliInputHook
    {
        /// <summary>Type::method. Every one returns bool and means "not now".</summary>
        static readonly string[] Locks = {
            "PlayerMovementController::PlayerCantMovement",
            "PlayerMovementController::PlayerCantRotate",
            "PlayerMovementController::PlayerCantRotateAxisX",
            "PlayerMovementController::PlayerCantJump",
            "PlayerMovementController::PlayerCantRun",
            "PlayerFirearmWeaponController::CantShoot",
            "PlayerMeleeWeaponController::MeleeCantAttack",
            "PlayerGrenadeWeaponController::CantThrowGrenade",
            "PlayerInteractingManager::CantInteractWithItem",
        };

        /// <summary>The orbit camera is the ONE lock a passenger does not get.
        /// The pilot's view belongs to this feature and the game's camera
        /// scripts are paused for him anyway; a man in the cabin should be able
        /// to look out of the window while he is carried.</summary>
        static readonly string[] PilotLocks = {
            "MouseOrbitController::PlayerCantOrbitRotate",
        };

        public static void Postfix(ref bool __result)
        {
            if (PlayerHeli.Aboard) __result = true;
        }

        public static void PilotPostfix(ref bool __result)
        {
            if (PlayerHeli.Flying) __result = true;
        }

        internal static void Install(Harmony harmony)
        {
            System.Text.StringBuilder missing = new System.Text.StringBuilder();
            int patched = Patch(harmony, Locks, "Postfix", missing)
                        + Patch(harmony, PilotLocks, "PilotPostfix", missing);
            int total = Locks.Length + PilotLocks.Length;

            RevivalPlugin.L.LogInfo("PlayerHeli locks: " + patched + " of "
                + total + " patched"
                + (missing.Length == 0 ? "." : (", not found: " + missing + ".")));
            if (patched == 0)
                RevivalPlugin.L.LogWarning("PlayerHeli: NO lock patched - the body "
                    + "walks along while the machine is flown.");
        }

        static int Patch(Harmony harmony, string[] list, string postfix,
                         System.Text.StringBuilder missing)
        {
            int patched = 0;
            HarmonyMethod post = new HarmonyMethod(
                typeof(HeliInputHook).GetMethod(postfix));
            for (int i = 0; i < list.Length; i++)
            {
                string[] parts = list[i].Split(new string[] { "::" }, StringSplitOptions.None);
                try
                {
                    Type t = RevivalPlugin.TypeByName(parts[0]);
                    MethodInfo m = t == null ? null : AccessTools.Method(t, parts[1], null, null);
                    if (m == null || m.ReturnType != typeof(bool))
                    {
                        if (missing.Length > 0) missing.Append(", ");
                        missing.Append(list[i]);
                        continue;
                    }
                    harmony.Patch(m, null, post, null, null, null);
                    patched++;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli lock " + list[i]
                        + " not patched: " + ex.Message);
                }
            }
            return patched;
        }
    }

    /// <summary>
    /// THE CARGO HOLD. The machine gets the game's own vehicle-trunk component,
    /// `ItemsContainer`, and a plate to aim at. Nothing about the container is
    /// rebuilt here: the window, the drag and drop, the weight, the one-player
    /// lock and the network copy are all the game's, exactly as they are on a
    /// car. What is ours is where the container hangs, how big it is, and the
    /// window's room to show it.
    ///
    /// WHERE IT HANGS, AND WHY THERE IS NO CHOICE (IL, 2026-09-22).
    ///   PlayerInteractingManager::SearchGameplayItems finds a container with
    ///     hit.collider.gameObject.GetComponent&lt;ItemsContainer&gt;()
    ///   so the collider and the container must be on the SAME GameObject.
    ///   ItemsContainer::Awake reads photonView.viewID, and PhotonView::Get is a
    ///   plain GetComponent - it does not climb to a parent. So the container
    ///   must be on a GameObject that carries a PhotonView.
    /// On this machine exactly one object satisfies both: the root. It already
    /// has the scene object's PhotonView, whose viewID is the same number on
    /// every client, so ContainerID matches everywhere and the container's own
    /// RPCs land - PUN dispatches an RPC to the components on the view's own
    /// GameObject, which is where the container now is.
    ///
    /// WHY IT IS NOT CONTAINER TYPE "Baggage" (5). That is the vehicle trunk,
    /// and the interact coroutine reads
    ///   itemGO.GetComponentInParent&lt;VehicleGameSystem&gt;().DoorsIsLocked
    /// before opening one. A helicopter is not a vehicle in that sense and has
    /// no VehicleGameSystem, so type 5 would open nothing at all. The hold is
    /// the plain type (Universal, 0), which the same coroutine hands straight to
    /// ShowContainerUI, and its ContainerData.Name is set to the game's own
    /// "Baggage_Container" so the window is still titled Kofferraum / Trunk.
    ///
    /// WHY THE WINDOW HAS TO BE GROWN. PlayerInventoryUISystem
    /// ::UpdateContainerSlotsUI walks i from 0 to ContainerData.MaxSlots and
    /// takes ContainerSlotFormsUI.transform.GetChild(i) and ContainerSlotsUI[i]
    /// for each one. Both hold exactly 42 - the size of the biggest container
    /// the game ships - so a bigger MaxSlots is an ArgumentOutOfRangeException,
    /// not a scrollbar. Both parents are NGUI UITables inside a UIScrollView
    /// that the game repositions itself, so more children is all it takes; the
    /// prefix below clones them before the walk and rebuilds the array from
    /// them with the game's own FillArrayItemSlotUI. If that cannot be done the
    /// prefix cuts MaxSlots back to what the window really has, so the walk can
    /// never step off the end.
    ///
    /// C# 3.0 and the four Unity modules: BoxCollider and every game type go
    /// through reflection here, the same way the rest of this file works.
    /// </summary>
    internal static class HeliHold
    {
        /// <summary>Slot forms the container window is built with. Anything
        /// beyond this is cloned at runtime.</summary>
        const int Forms = 42;

        /// <summary>
        /// The plate the interact ray has to meet, in model units on the
        /// machine's own transform - so it moves and scales with the Size
        /// setting exactly as the hull box does.
        ///
        /// It lies flat against the PORT skin of the cabin, which is the side an
        /// Mi-8 is loaded from. Two independent measurements of the same prefab
        /// put it there: the cabin shell mi-8_rusty_int spans x -2.19 to 7.21
        /// and z -9.6 to 28.8 with the nose at +Z, and the troop insertion - the
        /// other feature flying this machine - unloads its squad at -X, two
        /// units forward of the root (HeliFlight.DropNow, "the door is on the
        /// left, forward of the middle").
        ///
        /// The one number that is not about the door is the thickness. The plate
        /// has to stand proud of NDR_FlyHeliHull, the box that stops a man
        /// walking through the fuselage, because that box reaches only x -1
        /// while the skin is at -2.19: a plate inside it would never be the
        /// first thing the interact ray meets, and the hold would not open.
        /// </summary>
        static readonly Vector3 Centre = new Vector3(-1.4f, 5.5f, 6f);
        static readonly Vector3 Size = new Vector3(1.6f, 5f, 20f);

        static bool On
        {
            get { return PlayerHeli.CfgHold == null || PlayerHeli.CfgHold.Value; }
        }

        /// <summary>How many slots one hold gets. Capped at the window's own 42
        /// until the prefix that can grow the window is in place - a hold the
        /// window cannot show is a hold that throws when it is opened.</summary>
        internal static int Slots()
        {
            int want = PlayerHeli.CfgHoldSlots == null ? 200 : PlayerHeli.CfgHoldSlots.Value;
            return Mathf.Clamp(want, 12, _window ? 600 : Forms);
        }

        internal static float Weight()
        {
            float want = PlayerHeli.CfgHoldWeight == null ? 4000f : PlayerHeli.CfgHoldWeight.Value;
            return Mathf.Clamp(want, 50f, 100000f);
        }

        // ======================================================== the container

        static Type _tContainer, _tData, _tBox, _tView;
        static FieldInfo _fData, _fSpawned, _fMaxSlots, _fMaxWeight, _fName;
        static MethodInfo _mLength, _mEmpty, _mRefresh;
        static PropertyInfo _pCentre, _pSize;
        static bool _looked;

        static bool Look()
        {
            if (_looked) return _tContainer != null;
            _looked = true;
            _tContainer = RevivalPlugin.TypeByName("ItemsContainer");
            _tData = RevivalPlugin.TypeByName("ContainerData");
            if (_tContainer == null || _tData == null)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: ItemsContainer or "
                    + "ContainerData not found - the machine flies without a hold.");
                _tContainer = null;
                return false;
            }
            _fData = AccessTools.Field(_tContainer, "_containerData");
            _fSpawned = AccessTools.Field(_tContainer, "IsSpawnedData");
            _mLength = AccessTools.Method(_tContainer, "SetContainerDataArraysLenght", null, null);
            _mEmpty = AccessTools.Method(_tContainer, "IsEmptyContainer", null, null);
            _fMaxSlots = AccessTools.Field(_tData, "MaxSlots");
            _fMaxWeight = AccessTools.Field(_tData, "MaxWeight");
            _fName = AccessTools.Field(_tData, "Name");
            if (_fData == null || _fMaxSlots == null || _mLength == null)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: the container's own "
                    + "fields have moved - the machine flies without a hold.");
                _tContainer = null;
            }
            return _tContainer != null;
        }

        /// <summary>
        /// Fit the hold. Called from Prepare on EVERY client, because every
        /// client has to have the component the container's RPCs are delivered
        /// to, and because the plate is what each of them aims at.
        /// </summary>
        internal static void Attach(GameObject go)
        {
            if (!On || go == null || !Look()) return;
            try
            {
                if (go.GetComponent(_tContainer) != null) return;   // already fitted

                Plate(go);

                Component hold = go.AddComponent(_tContainer);
                if (hold == null) return;

                // Before the container's own Start runs. On the master that
                // method rolls random loot into a fresh container, and this one
                // has no spawn table to roll from - a hold arrives empty.
                if (_fSpawned != null) _fSpawned.SetValue(hold, true);

                // Awake has already run SetContainerData, which reads MaxSlots
                // (zero on a component nobody serialized) and sized the slot
                // arrays to match. The size is ours, so both are set again here.
                object data = _fData.GetValue(hold);
                if (data == null)
                {
                    data = Activator.CreateInstance(_tData);
                    _fData.SetValue(hold, data);
                }

                int slots = Slots();
                _fMaxSlots.SetValue(data, slots);
                if (_fMaxWeight != null) _fMaxWeight.SetValue(data, Weight());
                if (_fName != null) _fName.SetValue(data, "Baggage_Container");
                _mLength.Invoke(hold, new object[] { slots });

                Reheard(go);

                RevivalPlugin.L.LogInfo("PlayerHeli: hold fitted - " + slots
                    + " slots, " + Weight().ToString("0") + " kg.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: " + ex.Message);
            }
        }

        /// <summary>True when there is nothing in the hold, and true as well
        /// when this machine has none - a machine without a hold must not be
        /// harder to take away than it was before.</summary>
        internal static bool Empty(GameObject go)
        {
            if (!On || go == null || !Look() || _mEmpty == null) return true;
            try
            {
                Component hold = go.GetComponent(_tContainer);
                if (hold == null) return true;
                return (bool)_mEmpty.Invoke(hold, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// Ask the PhotonView for its RPC targets again.
        ///
        /// PUN delivers an RPC to the MonoBehaviours it finds with
        /// PhotonView.GetComponents on the view's own GameObject, and it is
        /// allowed to remember that list (NetworkingPeer.UseRpcMonoBehaviourCache).
        /// The container is added long after the view was built, so a machine
        /// that had already carried one RPC could hold a list the container is
        /// not in - and then no client would ever be told what went into the
        /// hold. One call settles it.
        /// </summary>
        static void Reheard(GameObject go)
        {
            if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
            if (_tView == null) return;
            if (_mRefresh == null)
                _mRefresh = AccessTools.Method(_tView, "RefreshRpcMonoBehaviourCache", null, null);
            Component view = go.GetComponent(_tView);
            if (view == null || _mRefresh == null) return;
            try { _mRefresh.Invoke(view, null); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: " + ex.Message);
            }
        }

        static void Plate(GameObject go)
        {
            if (_tBox == null) _tBox = RevivalPlugin.TypeByName("BoxCollider");
            if (_tBox == null) return;
            if (_pCentre == null) _pCentre = AccessTools.Property(_tBox, "center");
            if (_pSize == null) _pSize = AccessTools.Property(_tBox, "size");
            Component box = go.AddComponent(_tBox);
            if (box == null) return;
            if (_pCentre != null) _pCentre.SetValue(box, Centre, null);
            if (_pSize != null) _pSize.SetValue(box, Size, null);
        }

        // =========================================================== the window

        static Type _tUi, _tTable;
        static FieldInfo _fUiController, _fInteract, _fForms, _fSlotParent, _fSlotArray;
        static MethodInfo _mFill, _mReposition;
        static object _measured;      // the window we last counted
        static int _pool;             // and how many slots it had then
        static bool _uiLooked, _window, _cutWarned, _windowWarned;

        internal static void Install(Harmony harmony)
        {
            if (!On) return;
            try
            {
                _tUi = RevivalPlugin.TypeByName("PlayerInventoryUISystem");
                MethodInfo walk = _tUi == null ? null
                    : AccessTools.Method(_tUi, "UpdateContainerSlotsUI", null, null);
                if (walk == null)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli hold: "
                        + "PlayerInventoryUISystem.UpdateContainerSlotsUI not found - "
                        + "the hold stays at the " + Forms + " slots the window owns.");
                    return;
                }
                harmony.Patch(walk,
                    new HarmonyMethod(typeof(HeliHold).GetMethod("WindowPrefix",
                        BindingFlags.Public | BindingFlags.Static)),
                    null, null, null, null);
                _window = true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold window: " + ex.Message);
            }
        }

        /// <summary>
        /// Before the game walks the open container's slots, make sure there are
        /// that many to walk. It runs for every container, not only ours: a
        /// window with room for 42 is the reason the hold has to say how big it
        /// is here, and the clamp at the end is what keeps the walk in bounds
        /// whatever happens above it.
        /// </summary>
        public static void WindowPrefix(object __instance)
        {
            try { Fit(__instance); }
            catch (Exception ex) { Once(ref _windowWarned, "PlayerHeli hold window: " + ex.Message); }
        }

        static void Fit(object ui)
        {
            if (ui == null || !Look() || !UiLook()) return;

            object controller = _fUiController.GetValue(ui);
            if (controller == null) return;
            object container = _fInteract.GetValue(controller);
            if (container == null) return;
            object data = _fData.GetValue(container);
            if (data == null) return;

            int want = (int)_fMaxSlots.GetValue(data);
            // The cheap answer first: this runs while the window is open, and
            // every container the game ships is inside the window's own room.
            if (want <= Forms) return;
            if (want <= _pool && ReferenceEquals(ui, _measured)) return;

            // Whatever happens from here, `have` is a number the walk can
            // safely count to. The window's own 42 always are: the two tables
            // are built with that many children and the array with that many
            // entries, and growing only ever adds to all three.
            int have = Forms;
            try
            {
                have = Build(ui, want);
                _measured = ui;
                _pool = have;
            }
            catch (Exception ex)
            {
                Once(ref _windowWarned, "PlayerHeli hold window: " + ex.Message);
                _measured = null;
                _pool = 0;
            }

            if (want <= have) return;
            _fMaxSlots.SetValue(data, have);
            Once(ref _cutWarned, "PlayerHeli hold: the container window took "
                + have + " slots, not " + want + ", so this container is shown with "
                + have + ". Anything that was put in a higher slot is still in the "
                + "container and comes back when the window can show it again.");
        }

        /// <summary>Clone the window up to `want` cells and report how many of
        /// them the walk may really use - the smallest of the two tables and
        /// the array, because the walk reads an index out of all three.</summary>
        static int Build(object ui, int want)
        {
            Transform forms = TransformOf(_fForms.GetValue(ui));
            Transform slots = TransformOf(_fSlotParent.GetValue(ui));
            if (forms == null || slots == null || _fSlotArray == null || _mFill == null)
                return Forms;

            Grow(forms, want);
            Grow(slots, want);

            // The array the walk reads is built from the same children, by the
            // game's own method, so a cloned slot is wired exactly like a
            // serialized one.
            object[] args = new object[] { slots, null, slots.childCount };
            _mFill.Invoke(ui, args);
            _fSlotArray.SetValue(ui, args[1]);

            LayOut(forms, _fForms.GetValue(ui));
            LayOut(slots, _fSlotParent.GetValue(ui));

            Array filled = _fSlotArray.GetValue(ui) as Array;
            int room = filled == null ? 0 : filled.Length;
            return Mathf.Min(Mathf.Min(forms.childCount, slots.childCount), room);
        }

        /// <summary>Say a thing once. A prefix runs while a window is open, and
        /// a line per frame is a log nobody can read.</summary>
        static void Once(ref bool said, string text)
        {
            if (said) return;
            said = true;
            RevivalPlugin.L.LogWarning(text);
        }

        static bool UiLook()
        {
            if (_uiLooked) return _fForms != null;
            _uiLooked = true;
            if (_tUi == null) _tUi = RevivalPlugin.TypeByName("PlayerInventoryUISystem");
            Type controller = RevivalPlugin.TypeByName("UIController");
            if (_tUi == null || controller == null) return false;
            _fUiController = AccessTools.Field(_tUi, "_uiController");
            _fForms = AccessTools.Field(_tUi, "ContainerSlotFormsUI");
            _fSlotParent = AccessTools.Field(_tUi, "SlotsUIContainer");
            _fSlotArray = AccessTools.Field(_tUi, "ContainerSlotsUI");
            _mFill = AccessTools.Method(_tUi, "FillArrayItemSlotUI", null, null);
            _fInteract = AccessTools.Field(controller, "_interactContainer");
            _tTable = RevivalPlugin.TypeByName("UITable");
            _mReposition = _tTable == null ? null
                : AccessTools.Method(_tTable, "Reposition", null, null);
            if (_fUiController == null || _fForms == null || _fSlotParent == null
                || _fInteract == null)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli hold: the container window's "
                    + "own fields have moved - the hold stays at " + Forms + " slots.");
                _fForms = null;
            }
            return _fForms != null;
        }

        /// <summary>Clone the last slot until there are `want` of them. The
        /// clones arrive switched off; the walk switches on the ones it
        /// fills, exactly as it does with the serialized ones.</summary>
        static void Grow(Transform parent, int want)
        {
            int have = parent.childCount;
            if (have <= 0 || have >= want) return;
            GameObject model = parent.GetChild(have - 1).gameObject;
            Vector3 scale = model.transform.localScale;
            for (int i = have; i < want; i++)
            {
                GameObject clone = UnityEngine.Object.Instantiate(model) as GameObject;
                if (clone == null) return;
                clone.name = "NDR_Slot" + i;
                clone.transform.SetParent(parent, false);
                clone.transform.localScale = scale;
                clone.SetActive(false);
            }
        }

        /// <summary>
        /// Give every cell its place, once.
        ///
        /// A UITable here is `hideInactive`, so Reposition moves the children
        /// that are switched ON and leaves the rest where they are. That is why
        /// the game can switch slots on and off all day without repositioning
        /// anything: the prefab's own children were all on when the table first
        /// laid them out, and a cell keeps its place afterwards. A clone has
        /// never been in such a pass - it sits exactly on top of the slot it was
        /// copied from - so the pass is run here with everything switched on and
        /// the state put back straight after.
        /// </summary>
        static void LayOut(Transform parent, object table)
        {
            int n = parent.childCount;
            bool[] was = new bool[n];
            for (int i = 0; i < n; i++)
            {
                GameObject cell = parent.GetChild(i).gameObject;
                was[i] = cell.activeSelf;
                if (!was[i]) cell.SetActive(true);
            }
            Reposition(table);
            for (int i = 0; i < n && i < parent.childCount; i++)
                if (!was[i]) parent.GetChild(i).gameObject.SetActive(false);
        }

        static Transform TransformOf(object component)
        {
            Component c = component as Component;
            return c == null ? null : c.transform;
        }

        static void Reposition(object table)
        {
            if (table == null || _mReposition == null) return;
            try { _mReposition.Invoke(table, null); }
            catch (Exception ex) { Once(ref _windowWarned, "PlayerHeli hold window: " + ex.Message); }
        }
    }

    /// <summary>
    /// Research: what the local player's CharacterController runs into while
    /// he sits in the machine. There is no Rigidbody on the player, so there is
    /// no impulse to report - the controller's velocity and the game's own
    /// moveDirection are what there is.
    /// </summary>
    internal class HeliBodyHits : MonoBehaviour
    {
        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!PlayerHeli.Research || !PlayerHeli.Aboard || hit == null
                || hit.collider == null) return;
            CharacterController cc = hit.controller;
            PlayerHeli.LastBodyHit = hit.collider.name + " (layer "
                + hit.collider.gameObject.layer + ", controller velocity "
                + (cc == null ? "?" : cc.velocity.ToString("0.0"))
                + ", move " + hit.moveDirection.ToString("0.00")
                + ", impulse n/a - no rigidbody, t " + Time.time.ToString("0.00") + ")";
        }
    }

    /// <summary>
    /// THE ROOT OF "THROWN OUT OF THE HELICOPTER". The game does not know a man
    /// can sit in a machine. Every frame the seated body is not grounded,
    /// PlayerMovementController runs its fall state: moveDirection.y grows by
    /// 25 per second without end and the controller is moved by it, and a line
    /// is cast 200 units down - no hit (high over the ground, or over a chunk
    /// whose colliders have not streamed in yet) is 1000 damage on the spot,
    /// a hit further than 6 units away is a fall whose landing deals damage
    /// later. At speed the chunks stream behind the machine, and the pilot
    /// died in his seat; the first key on the death screen then threw the body
    /// out. While the local player is aboard both fall methods are held here
    /// and the fall data cleared.
    ///
    /// Two more hooks only watch: PlayerDeath ends the flight the moment the
    /// local player dies aboard, and PlayerApplyDamage logs every hit the local
    /// player takes aboard or just after, with its caller.
    /// </summary>
    internal static class HeliBodyGuard
    {
        static MethodInfo _mClear;
        static FieldInfo _fMove, _fLifeData, _fHealth;
        static MethodInfo _mHealth;
        static bool _warned;

        internal static void Install(Harmony harmony)
        {
            Type move = RevivalPlugin.TypeByName("PlayerMovementController");
            Type life = RevivalPlugin.TypeByName("PlayerLifeDataManager");
            if (move != null)
            {
                _mClear = AccessTools.Method(move, "ClearPlayerFallingData", null, null);
                _fMove = AccessTools.Field(move, "moveDirection");
                Patch(harmony, move, "PlayerFallingState", "FallPrefix", null);
                Patch(harmony, move, "PlayerFallingLanding", "FallPrefix", null);
            }
            else RevivalPlugin.L.LogWarning("PlayerHeli: PlayerMovementController not "
                + "found - the fall guard is off and a seated pilot can die of a fall.");
            if (life != null)
            {
                _fLifeData = AccessTools.Field(life, "_playerLifeData");
                Patch(harmony, life, "PlayerDeath", null, "DeathPostfix");
                Patch(harmony, life, "PlayerApplyDamage", "DamagePrefix", null);
            }
        }

        static void Patch(Harmony harmony, Type t, string method, string prefix, string postfix)
        {
            try
            {
                MethodInfo m = AccessTools.Method(t, method, null, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("PlayerHeli guard: " + t.Name + "."
                        + method + " not found.");
                    return;
                }
                harmony.Patch(m,
                    prefix == null ? null : new HarmonyMethod(typeof(HeliBodyGuard).GetMethod(
                        prefix, BindingFlags.Public | BindingFlags.Static)),
                    postfix == null ? null : new HarmonyMethod(typeof(HeliBodyGuard).GetMethod(
                        postfix, BindingFlags.Public | BindingFlags.Static)),
                    null, null, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerHeli guard: " + method + " - " + ex.Message);
            }
        }

        /// <summary>Is this component on the local player's own object?</summary>
        static bool Mine(object instance)
        {
            Component c = instance as Component;
            Transform body = PlayerHeli.Body;
            if (c == null || body == null) return false;
            return c.transform.IsChildOf(body);
        }

        /// <summary>Held while the local player sits in a machine. Everything
        /// else - every other player, and this one on foot - runs untouched.</summary>
        public static bool FallPrefix(object __instance)
        {
            if (!PlayerHeli.Aboard || !Mine(__instance)) return true;
            try
            {
                if (_mClear != null) _mClear.Invoke(__instance, null);
                if (_fMove != null) _fMove.SetValue(__instance, Vector3.zero);
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("PlayerHeli guard: " + ex.Message);
                }
            }
            PlayerHeli.GuardedCalls++;
            return false;
        }

        public static void DeathPostfix(object __instance, object[] __args)
        {
            try
            {
                if (!PlayerHeli.Aboard || !Mine(__instance) || !Dead(__instance)) return;
                PlayerHeli.DiedAboard(Args(__args));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli death hook: " + ex.Message);
            }
        }

        public static void DamagePrefix(object __instance, object[] __args)
        {
            try
            {
                if (!PlayerHeli.RecentlyAboard || !Mine(__instance)) return;
                RevivalPlugin.L.LogInfo("PlayerHeli: local player takes damage "
                    + (PlayerHeli.Aboard ? "aboard" : "just after leaving")
                    + " (" + Args(__args) + "); " + PlayerHeli.Where()
                    + "; caller: " + PlayerHeli.Caller(PlayerHeli.Research ? 14 : 6));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerHeli damage hook: " + ex.Message);
            }
        }

        static string Args(object[] args)
        {
            if (args == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(args[i] == null ? "null" : args[i].ToString());
            }
            return sb.ToString();
        }

        /// <summary>PlayerDeath returns at once unless health is at or under
        /// zero; the postfix runs either way, so it reads the health too. Health
        /// is an ObscuredFloat, read through its own implicit conversion. If it
        /// cannot be read, the call is taken at its word.</summary>
        static bool Dead(object instance)
        {
            try
            {
                if (_fLifeData == null) return true;
                object data = _fLifeData.GetValue(instance);
                if (data == null) return true;
                if (_fHealth == null) _fHealth = AccessTools.Field(data.GetType(), "Health");
                if (_fHealth == null) return true;
                object h = _fHealth.GetValue(data);
                if (h == null) return true;
                if (h is float) return (float)h <= 0f;
                if (_mHealth == null)
                {
                    MethodInfo[] ms = h.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < ms.Length; i++)
                        if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float))
                            _mHealth = ms[i];
                }
                if (_mHealth == null) return true;
                return (float)_mHealth.Invoke(null, new object[] { h }) <= 0f;
            }
            catch
            {
                return true;
            }
        }
    }
}

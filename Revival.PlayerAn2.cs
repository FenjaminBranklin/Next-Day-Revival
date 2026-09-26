// Next Day: Survival - Revival Toolkit
//
// THE AN-2 A PLAYER FLIES. A fixed-wing aircraft, not a reskinned helicopter:
// lift comes from forward speed, it stalls below about 60 km/h, it takes off
// and lands on its wheels - on the runway or on a field, it is a short-field
// aeroplane - it burns fuel, and it breaks when it is put down too hard.
// docs/ai/tasks/an2-flight.md has the tuning numbers and the in-game checklist;
// the gameplay design is docs/ai/tasks/airfield-gameplay.md section 4.
//
// WHAT IS REUSED FROM THE MI-8 (Revival.PlayerHeli.cs), AND HOW.
//   The NETWORK OBJECT. The An-2 rides on the game's own Mi-8 prefab as a
//     hidden carrier: a scene object with a PhotonView and a
//     PhotonInterpolatedTransform that every client can load from its resource
//     path. Its renderers are switched off, its colliders too, its rotor and
//     sound are killed through HeliEngine.Kill, and the An-2 model is built as
//     a child of it on every client. Everything the Mi-8 learnt about owning a
//     scene object - the master spawns and removes, a pilot who is not the
//     master relays his pose to the master, the interpolator is switched off
//     under his own writes - applies unchanged. The marker in the
//     instantiation data is our own ("ndr-an2-1"), so neither the Mi-8 code
//     nor the troop insertion's orphan sweep ever touches one of these.
//   The SEAT, the FALL GUARD and the INPUT LOCKS. HeliInputHook and
//     HeliBodyGuard ask PlayerAn2.Aboard / Flying next to PlayerHeli's, so a
//     seated An-2 pilot is held out of the game's fall state exactly as a
//     helicopter pilot is - the fall that killed men in their seats at speed
//     (HeliBodyGuard's comment) cannot happen here either.
//   The CAMERA: its own CameraOwner slot (An2 = 7), same Request/Release
//     rules. FlightView.Report once per frame while aboard.
//   The CRASH: the patrol wreck's own fire (FireEffect.SpawnHeliBlast and
//     SpawnHeliFire on an anchor shifted onto the An-2's fuselage), the Mi-8's
//     crash sound, the game's own damage gate for everyone aboard, and the
//     jump through Revival.Parachute.cs.
//
// SCALE. The world is 2.8 units per metre: the player and every NPC carry a
// 5.0 u CapsuleCollider, a 1.79 m man (airfield-greybox.md section 0). The
// Mi-8 prefab is NOT at that scale (3.84 u/m) and nothing here is fitted to
// it. The An-2 meshes are in real metres (an2_import.py checks span 18.18 m
// and length 12.74 m against the published data) and K below turns them into
// world units: the span is 50.9 u, ten capsules; the cabin is 2.0 m, 1.1
// capsules - a man stands up in it, as he does in the real one.
//
// FLIGHT MODEL. No Rigidbody, for the Mi-8's reason (Photon and the world's
// colliders). Velocity in metres per second, integrated here:
//   lift  = g (v / Vs)^2 c(aoa), c = 1 at the critical angle -> at Vs = the
//           stall speed the wing can just carry the aeroplane
//   drag  = Cd0 v^2 + induced (grows with lift x c: slow flight needs power)
//   thrust along the nose, falling off with speed (a propeller)
//   stability: the nose weathervanes into the flight path, so a stalled
//           aeroplane drops its nose, gathers speed and flies again
// Controls are rates (W/S pitch, A/D roll, Q/E rudder, space/ctrl throttle),
// scaled by airspeed and propeller wash. On the ground the tail wheel steers
// and the tail stays down until speed and forward stick lift it.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. Physics and Collider go through reflection (build.ps1 references
// four Unity modules). ASCII-only comments and logs; player-facing strings go
// through Loc.T (real Cyrillic), so this file is UTF-8 without BOM.
//
// SEAMS OUTSIDE THIS FILE:
//   RevivalPlugin.cs        BindConfig / Install / Update / LateUpdate / OnGUI
//   Revival.CameraTurret.cs CameraOwner.An2 and its LateTick dispatch
//   Revival.PlayerHeli.cs   HeliInputHook, HeliBodyGuard (Aboard/Body/death),
//                           and PlayerHeli.Tick keeps its keys while aboard
//   Revival.WindSound.cs    wind while aboard

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The player-flown An-2: spawn at the H1 apron, board, fly, land, crash.
    /// </summary>
    internal static class PlayerAn2
    {
        /// <summary>The network carrier: the game's aid Mi-8, drawn as nothing.</summary>
        internal const string Prefab = PlayerHeli.Prefab;

        /// <summary>Instantiation data marker. Neither the Mi-8's nor the troop
        /// insertion's, so neither of their sweeps enrols this object.</summary>
        internal const string Marker = "ndr-an2-1";

        /// <summary>World units per real metre, proven on the capsule: 5.0 u
        /// for a 1.79 m man. The meshes are in metres; this is the only place
        /// the size in the world is decided.</summary>
        internal const float K = 2.8f;

        const float G = 9.81f;

        /// <summary>The stand beside H1 on apron A1, after the 2.8x re-layout
        /// (airfield-greybox.md section 0): the greybox stand-in's own spot,
        /// nose east toward taxiway T1.</summary>
        internal static readonly Vector3 Apron = new Vector3(4395f, 0f, 1100f);
        internal const float ApronHeading = 90f;
        const string StandInName = "AN An-2 (nose east)";

        // ============================================================= config

        internal static ConfigEntry<bool> CfgEnabled, CfgApronSpawn, CfgPassengers,
            CfgBailOut, CfgCrash;
        internal static ConfigEntry<string> CfgSpawnKey, CfgBoardKey, CfgViewKey,
            CfgEngineKey, CfgJumpKey, CfgBrakeKey;
        internal static ConfigEntry<float> CfgStallSpeed, CfgCritAoa, CfgThrust,
            CfgPower, CfgTopSpeed, CfgPitchRate, CfgRollRate, CfgYawRate,
            CfgGroundSteer, CfgEngineStart, CfgFuelCapacity, CfgStartFuel,
            CfgFuelBurn, CfgCrashSink, CfgCrashSpeed, CfgCrashBank, CfgCrashSlope,
            CfgImpactSpeed, CfgRollFriction, CfgGrassFriction, CfgBrake,
            CfgCrashDamage, CfgWreckSeconds, CfgRespawnMinutes, CfgBoardRange,
            CfgCamDistance, CfgCamHeight, CfgSensitivity, CfgNetHz;
        internal static ConfigEntry<int> CfgEventCode;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "PlayerAn2";
            CfgEnabled = cfg.Bind(S, "Enabled", false,
                "A flyable An-2. Off by default. On: the host parks one at the H1 "
                + "apron of the airfield (east world only) and the board key gets "
                + "in. Every client must have the same setting.");
            CfgApronSpawn = cfg.Bind(S, "ApronSpawn", true,
                "The host puts one An-2 on the H1 apron stand when none stands in "
                + "the world. Needs [World] EastTile, which carries the airfield.");
            CfgRespawnMinutes = cfg.Bind(S, "RespawnMinutes", 15f,
                "Minutes after the last An-2 is gone (a wreck cleared away) before "
                + "the host parks a new one on the apron.");
            CfgSpawnKey = cfg.Bind(S, "SpawnKey", "None",
                "Test key: puts an An-2 down in front of you anywhere (the host "
                + "does it; everyone else asks). None = only the apron spawn.");
            CfgBoardKey = cfg.Bind(S, "BoardKey", "F",
                "Get in next to the aeroplane, get out when it stands still.");
            CfgViewKey = cfg.Bind(S, "ViewKey", "V", "Chase view or cockpit.");
            CfgEngineKey = cfg.Bind(S, "EngineKey", "G",
                "Start and stop the engine. A cold radial needs EngineStartSeconds "
                + "before it gives full power.");
            CfgJumpKey = cfg.Bind(S, "JumpKey", "X",
                "Jump out; the parachute opens by itself when there is one in "
                + "the pack and enough height.");
            CfgBrakeKey = cfg.Bind(S, "BrakeKey", "B", "Wheel brakes on the ground.");
            CfgStallSpeed = cfg.Bind(S, "StallSpeed", 60f,
                "km/h. At this airspeed the wing at its critical angle just "
                + "carries the aeroplane; slower, it sinks whatever the pilot "
                + "does. The real An-2 stalls at about 50-60.");
            CfgCritAoa = cfg.Bind(S, "CriticalAngle", 15f,
                "Degrees of angle of attack where the wing stalls. Past it lift "
                + "falls away, the nose drops and a wing may go.");
            CfgThrust = cfg.Bind(S, "Thrust", 2.6f,
                "Static thrust at full throttle, m/s^2 of acceleration. With the "
                + "friction below it gives a takeoff roll of about 180 m on concrete, 200 m on grass.");
            CfgPower = cfg.Bind(S, "Power", 90f,
                "Engine power reaching the air, watts per kilogram of aeroplane "
                + "(the ASh-62's 745 kW at 80 % propeller efficiency over 5.25 t "
                + "is 113). Above Power/Thrust m/s the thrust is Power/speed.");
            CfgTopSpeed = cfg.Bind(S, "TopSpeed", 250f,
                "km/h in level flight at full throttle. Sets the drag.");
            CfgPitchRate = cfg.Bind(S, "PitchRate", 40f, "Deg/s at full elevator and full authority.");
            CfgRollRate = cfg.Bind(S, "RollRate", 75f, "Deg/s at full aileron.");
            CfgYawRate = cfg.Bind(S, "YawRate", 20f, "Deg/s at full rudder in the air.");
            CfgGroundSteer = cfg.Bind(S, "GroundSteer", 35f,
                "Deg/s the tail wheel turns the aeroplane at walking speed; "
                + "fades out towards 100 km/h, where the rudder takes over.");
            CfgEngineStart = cfg.Bind(S, "EngineStartSeconds", 6f,
                "Seconds from a cold start to full power, and back down.");
            CfgFuelCapacity = cfg.Bind(S, "FuelCapacity", 1200f, "Litres in the tanks.");
            CfgStartFuel = cfg.Bind(S, "StartFuel", 0.6f,
                "Fraction of the tanks filled when an An-2 is parked (0..1). Fuel "
                + "never comes back by itself; refuelling is phase 2.");
            CfgFuelBurn = cfg.Bind(S, "FuelBurn", 30f,
                "Litres per minute at full throttle (game time). Idle burns 15 % "
                + "of that. 1200 l last 40 minutes at full power.");
            CfgCrash = cfg.Bind(S, "Crash", true,
                "Hard landings, obstacles and wingtips on the ground destroy the "
                + "aeroplane. Off: nothing breaks (testing).");
            CfgCrashSink = cfg.Bind(S, "CrashSinkRate", 4f,
                "m/s of sink at touchdown that breaks the gear. Above 60 % of it "
                + "the landing is announced as hard.");
            CfgCrashSpeed = cfg.Bind(S, "CrashGroundSpeed", 140f,
                "km/h at touchdown above which the landing is a crash.");
            CfgCrashBank = cfg.Bind(S, "CrashBank", 25f,
                "Degrees of bank at touchdown that put a wingtip in first.");
            CfgCrashSlope = cfg.Bind(S, "CrashSlope", 0.45f,
                "Rise per metre of the ground ahead that breaks the gear when "
                + "rolling faster than 30 km/h (0.45 = 24 degrees).");
            CfgImpactSpeed = cfg.Bind(S, "ImpactSpeed", 20f,
                "km/h. Running into something slower than this just stops the "
                + "aeroplane; faster is a crash.");
            CfgRollFriction = cfg.Bind(S, "RollFriction", 0.04f, "Rolling friction on concrete.");
            CfgGrassFriction = cfg.Bind(S, "GrassFriction", 0.08f, "Rolling friction on grass and earth.");
            CfgBrake = cfg.Bind(S, "BrakeFriction", 0.35f, "Extra friction with the brakes on.");
            CfgCrashDamage = cfg.Bind(S, "CrashDamage", 1000f,
                "Damage everyone aboard takes in a crash, through the game's own "
                + "damage gate. 0 = none.");
            CfgWreckSeconds = cfg.Bind(S, "WreckSeconds", 180f,
                "How long the burning wreck stands before the host clears it.");
            CfgBoardRange = cfg.Bind(S, "BoardRange", 9f, "Metres from the aeroplane to get in.");
            CfgPassengers = cfg.Bind(S, "Passengers", true, "Others may ride in the cabin.");
            CfgBailOut = cfg.Bind(S, "BailOut", true, "Jumping out in flight is allowed.");
            CfgCamDistance = cfg.Bind(S, "CameraDistance", 22f, "Chase camera distance, metres.");
            CfgCamHeight = cfg.Bind(S, "CameraHeight", 5f, "Chase camera height, metres.");
            CfgSensitivity = cfg.Bind(S, "Sensitivity", 2.2f, "Mouse look sensitivity.");
            CfgNetHz = cfg.Bind(S, "NetHz", 15f, "Pose messages per second from the pilot.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 150,
                "Photon event code (0..192); this and the seven following codes "
                + "are used (+6/+7: the repair state, [An2Repair]). Must be the same on every client.");
        }

        internal static bool Enabled
        {
            get { return CfgEnabled != null && CfgEnabled.Value; }
        }

        static float F(ConfigEntry<float> e, float fallback)
        {
            return e == null ? fallback : e.Value;
        }

        internal static float EngineSeconds() { return Mathf.Max(0.5f, F(CfgEngineStart, 6f)); }

        // ============================================================== state

        static GameObject _plane;             // the one the local player is in
        static bool _pilot;
        static int _seat;                     // cabin seat, -1 at the controls
        static bool _cockpit;
        static Vector3 _vel;                  // m/s, world axes
        static Quaternion _rot = Quaternion.identity;
        static float _throttle;               // 0..1, the lever
        static float _pIn, _rIn, _yIn;        // smoothed control deflection -1..1
        static bool _onGround, _paved, _stalled, _brake;
        static float _airborneSince, _boardedAt, _nextPose, _nextHeartbeat, _nextSurface;
        static float _look, _orbit, _lastMouse;
        static float _wingDrop;
        static float _aoa;
        static Transform _body;
        static bool _hadBody, _diedAboard;
        static string _hint = "";
        static float _hintUntil;
        static int _errors;

        static readonly List<GameObject> _all = new List<GameObject>();
        static readonly Dictionary<int, GameObject> _byView = new Dictionary<int, GameObject>();
        static readonly Dictionary<int, float> _busyUntil = new Dictionary<int, float>();
        static readonly Dictionary<GameObject, float> _burning = new Dictionary<GameObject, float>();
        static readonly List<GameObject> _gone = new List<GameObject>();
        static readonly List<int> _drop = new List<int>();

        internal static bool Aboard { get { return _plane != null; } }
        internal static bool Flying { get { return _plane != null && _pilot; } }
        internal static Transform Body { get { return _plane != null ? _body : null; } }

        internal static bool Flown(GameObject go)
        {
            return go != null && _pilot && ReferenceEquals(go, _plane);
        }

        // ---- for the repair loop (Revival.An2Repair.cs)
        internal static float Capacity { get { return F(CfgFuelCapacity, 1200f); } }
        internal static float FuelOf(GameObject go) { return go == null ? 0f : VisualOf(go).Fuel; }
        internal static void SetFuel(GameObject go, float litres)
        {
            if (go != null) VisualOf(go).Fuel = Mathf.Clamp(litres, 0f, Capacity);
        }
        internal static int View(GameObject go) { return ViewId(go); }
        internal static GameObject ByViewId(int view) { return ByView(view); }
        internal static bool Occupied(GameObject go) { return Busy(ViewId(go)) || ReferenceEquals(go, _plane); }
        internal static GameObject NearestPlane() { return Nearest(Mathf.Max(3f, F(CfgBoardRange, 9f)) * K); }
        internal static KeyCode KeyOf(ConfigEntry<string> entry, KeyCode fallback) { return Key(entry, fallback); }
        /// <summary>Standing: nobody aboard, not gliding down, not burning.</summary>
        internal static bool Parked(GameObject go)
        {
            return go != null && !Occupied(go) && !Gliding(go) && !Burning(go);
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
                ApronSpawn();
                StandIn();

                if (_plane == null)
                {
                    _diedAboard = false;
                    if (PlayerHeli.Aboard) return;
                    if (Input.GetKeyDown(Key(CfgSpawnKey, KeyCode.None))) SpawnOrRemove();
                    else if (Input.GetKeyDown(Key(CfgBoardKey, KeyCode.F))) Board();
                    return;
                }

                float agl;
                if (Height(_plane, out agl)) FlightView.Report(agl);

                if (_diedAboard)
                {
                    _diedAboard = false;
                    RevivalPlugin.L.LogInfo("PlayerAn2: the local player died aboard - the flight ends.");
                    EndWithoutPilot("death");
                    return;
                }
                Transform root = LocalPlayerRoot();
                if (_hadBody && (_body == null || (root != null && !ReferenceEquals(root, _body))))
                {
                    RevivalPlugin.L.LogInfo("PlayerAn2: the pilot's body is gone - the flight ends.");
                    EndWithoutPilot("body gone");
                    return;
                }
                if (Burning(_plane)) { Leave(false); return; }
                bool gliding = Gliding(_plane);

                if (Input.GetKeyDown(Key(CfgViewKey, KeyCode.V))) _cockpit = !_cockpit;
                if (Input.GetKeyDown(Key(CfgJumpKey, KeyCode.X))) { Jump(); return; }
                if (Input.GetKeyDown(Key(CfgBoardKey, KeyCode.F))) { Leave(true); return; }
                if (_pilot && !gliding && Input.GetKeyDown(Key(CfgEngineKey, KeyCode.G)))
                    SetEngine(!VisualOf(_plane).Running);

                if (_pilot)
                {
                    Look();
                    if (!gliding) Fly();
                }
                else Look();
                Heartbeat();
                _errors = 0;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerAn2: " + ex);
                if (++_errors >= 5)
                {
                    _errors = 0;
                    EndWithoutPilot("exception in the flight loop");
                }
            }
        }

        /// <summary>A flight that ends without the pilot's key: death, a lost
        /// body, repeated errors. In the air the aeroplane goes on without him
        /// and comes down; a living man high enough jumps with his parachute
        /// if he carries one.</summary>
        static void EndWithoutPilot(string cause)
        {
            GameObject left = _plane;
            bool wasPilot = _pilot;
            bool inAir = left != null && (_pilot ? !_onGround : Airborne(left));
            Vector3 drift = _vel;
            Quaternion rot = _rot;
            float height = -1f;
            if (left != null) Height(left, out height);
            Vector3 door = left != null ? DoorOf(left) : Vector3.zero;
            Leave(false);
            if (cause != "death" && height > 10f && left != null)
            {
                string why;
                Parachute.Jump(door, height, out why);
            }
            if (wasPilot && inAir && left != null) Abandon(left, drift, rot, true);
            RevivalPlugin.L.LogInfo("PlayerAn2: flight ended (" + cause + ").");
        }

        /// <summary>LateUpdate: the man in his seat, after the game's animator
        /// and movement controller have written him.</summary>
        internal static void LateFrame()
        {
            if (!Enabled) return;
            // FlightView blends on its own LateTick; the Mi-8 calls it when it
            // is enabled, and a second call in a frame would blend twice.
            if (!PlayerHeli.Enabled) FlightView.LateTick();
            if (_plane == null) return;
            try
            {
                if (_body == null) _body = LocalPlayerRoot();
                if (_body != null) _hadBody = true;
                if (_body == null) return;
                Transform tr = _plane.transform;
                _body.position = tr.position + tr.rotation * (An2Model.Seat(_seat) * K);
                Vector3 dir = tr.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.000001f)
                    _body.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerAn2 seat: " + ex.Message);
            }
        }

        /// <summary>The pilot's camera (CameraOwner.An2).</summary>
        internal static void LateTick()
        {
            if (_plane == null || !_pilot) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                Transform tr = _plane.transform;
                if (_cockpit)
                {
                    Vector3 seat = An2Model.Seat(-1);
                    Vector3 eye = tr.position + tr.rotation
                        * (new Vector3(seat.x, seat.y + 1.62f, seat.z + 0.15f) * K);
                    cam.transform.position = eye;
                    cam.transform.rotation = tr.rotation * Quaternion.Euler(-_look, _orbit, 0f);
                }
                else
                {
                    Vector3 fwd = tr.forward;
                    float heading = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    Vector3 centre = tr.position + tr.rotation * (new Vector3(0f, 2.4f, -2f) * K);
                    float dist = Mathf.Max(8f, F(CfgCamDistance, 22f)) * K;
                    float high = F(CfgCamHeight, 5f) * K;
                    Quaternion orbit = Quaternion.Euler(-_look, heading + _orbit, 0f);
                    Vector3 eye = centre + orbit * new Vector3(0f, 0f, -dist) + Vector3.up * high;
                    float ground;
                    if (RevivalTroopInsertion.TerrainHeight(eye, out ground) && eye.y < ground + 1.5f * K)
                        eye.y = ground + 1.5f * K;
                    cam.transform.position = eye;
                    cam.transform.rotation = Quaternion.LookRotation((centre - eye).normalized, Vector3.up);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerAn2 camera: " + ex);
            }
        }

        /// <summary>The mouse looks around; the keys fly. Sideways drift of the
        /// view comes back behind the tail by itself once the hand rests.</summary>
        static void Look()
        {
            float sens = F(CfgSensitivity, 2.2f);
            float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
            if (Mathf.Abs(mx) > 0.001f || Mathf.Abs(my) > 0.001f) _lastMouse = Time.time;
            _orbit += mx * sens;
            if (_orbit > 180f) _orbit -= 360f;
            if (_orbit < -180f) _orbit += 360f;
            _look = Mathf.Clamp(_look - my * sens, -60f, 70f);
            if (Time.time - _lastMouse > 1.5f)
                _orbit = Mathf.MoveTowards(_orbit, 0f, 60f * Time.deltaTime);
        }

        // ================================================================ fly

        /// <summary>Stall speed in m/s.</summary>
        static float Vs() { return Mathf.Max(8f, F(CfgStallSpeed, 60f) / 3.6f); }

        /// <summary>Lift coefficient relative to its maximum: 1 at the critical
        /// angle, zero lift at -2 degrees (a cambered wing), past the critical
        /// angle it falls away to 0.45.</summary>
        internal static float LiftShape(float aoa, float crit)
        {
            const float zero = -2f;
            if (aoa <= crit) return Mathf.Clamp((aoa - zero) / (crit - zero), -0.6f, 1f);
            return Mathf.Max(0.45f, 1f - (aoa - crit) * 0.07f);
        }

        internal const float Induced = 0.3f;

        /// <summary>Thrust per unit throttle at this forward speed: the static
        /// pull up to where the engine's power runs out, Power/speed above.</summary>
        static float ThrustAt(float v)
        {
            float tmax = Mathf.Max(0.2f, F(CfgThrust, 2.6f));
            return Mathf.Min(tmax, Mathf.Max(1f, F(CfgPower, 90f)) / Mathf.Max(1f, v));
        }

        /// <summary>Drag coefficient from the configured top speed: thrust and
        /// drag (parasitic plus the induced drag of level flight there)
        /// cancel at full throttle.</summary>
        static float Cd0()
        {
            float vt = Mathf.Max(20f, F(CfgTopSpeed, 250f) / 3.6f);
            float c = (Vs() / vt) * (Vs() / vt);
            float t = ThrustAt(vt) - Induced * G * c;
            return Mathf.Max(0.00001f, t / (vt * vt));
        }

        static float Axis(KeyCode plus, KeyCode minus)
        {
            return (Input.GetKey(plus) ? 1f : 0f) - (Input.GetKey(minus) ? 1f : 0f);
        }

        /// <summary>One frame of flight or ground roll.</summary>
        static void Fly()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;
            Transform tr = _plane.transform;
            An2Visual vis = VisualOf(_plane);
            Vector3 was = tr.position;

            // ---- engine and fuel
            float power = vis.Power;
            float thrIn = (Input.GetKey(KeyCode.Space) ? 1f : 0f)
                - ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C)) ? 1f : 0f);
            _throttle = Mathf.Clamp01(_throttle + thrIn * 0.5f * dt);
            if (vis.Running && vis.Power > 0.01f)
            {
                float burn = F(CfgFuelBurn, 30f) / 60f * (0.15f + 0.85f * _throttle) * power;
                vis.Fuel = Mathf.Max(0f, vis.Fuel - burn * dt);
                if (vis.Fuel <= 0f)
                {
                    SetEngine(false);
                    Hint(Loc.T("Топливо кончилось - планируй!", "Out of fuel - glide it down!"), 6f);
                }
            }

            // ---- controls, smoothed so a key is a deflection, not a switch
            _pIn = Mathf.MoveTowards(_pIn, Axis(KeyCode.S, KeyCode.W), 4f * dt);
            _rIn = Mathf.MoveTowards(_rIn, Axis(KeyCode.D, KeyCode.A), 4f * dt);
            _yIn = Mathf.MoveTowards(_yIn, Axis(KeyCode.E, KeyCode.Q), 3f * dt);
            _brake = Input.GetKey(Key(CfgBrakeKey, KeyCode.B));

            Vector3 fwd = _rot * Vector3.forward;
            Vector3 right = _rot * Vector3.right;
            float speed = _vel.magnitude;
            Vector3 vb = Quaternion.Inverse(_rot) * _vel;
            float aoa = 0f, beta = 0f;
            if (vb.z > 1f)
            {
                aoa = Mathf.Atan2(-vb.y, vb.z) * Mathf.Rad2Deg;
                beta = Mathf.Atan2(vb.x, vb.z) * Mathf.Rad2Deg;
            }
            _aoa = aoa;
            float crit = Mathf.Clamp(F(CfgCritAoa, 15f), 6f, 30f);
            float tmax = Mathf.Max(0.2f, F(CfgThrust, 2.6f));
            float thrust = ThrustAt(Mathf.Max(0f, vb.z)) * _throttle * power;
            float wash = thrust / tmax;
            // Control authority: the elevator and rudder have it all from
            // 65 km/h (18 m/s) and get the propeller's wash at any speed, the
            // ailerons from 80 km/h. Full elevator must be able to reach the
            // critical angle at the stall speed, or the wing never stalls
            // where the pilot expects it (research/an2_flight_sim.py).
            float q = Mathf.Clamp01(speed / 18f);
            _stalled = !_onGround && aoa > crit && speed > 3f;
            if (_stalled && _wingDrop == 0f)
                _wingDrop = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            if (!_stalled) _wingDrop = 0f;

            float auth = Mathf.Clamp01(q + wash * 0.35f);
            float ailAuth = Mathf.Clamp01(speed / 22f);
            if (_stalled) { auth *= 0.6f; ailAuth *= 0.5f; }

            // ---- attitude
            if (_onGround)
                GroundAttitude(dt, speed, auth);
            else
            {
                float pRate = _pIn * F(CfgPitchRate, 40f) * auth;
                float rRate = _rIn * F(CfgRollRate, 75f) * ailAuth;
                float yRate = _yIn * F(CfgYawRate, 20f) * auth;
                float aero = Mathf.Clamp01(speed / 20f);
                // The nose weathervanes into the airflow: that is what makes a
                // stalled aeroplane drop its nose and fly again, and what makes
                // full back stick hold the wing near - not past - its critical
                // angle at speed (40 deg/s against 3 per degree: 13 degrees).
                pRate -= 3f * (aoa - 2f) * aero;
                yRate += 2.5f * beta * aero;
                float bank = Bank(right);
                if (Mathf.Abs(_rIn) < 0.05f && Mathf.Abs(bank) < 60f) rRate -= bank * 0.3f;
                if (_stalled)
                {
                    pRate -= 12f;
                    rRate += _wingDrop * 25f;
                }
                _rot = _rot * Quaternion.Euler(-pRate * dt, yRate * dt, -rRate * dt);
                TailWheel();
            }
            fwd = _rot * Vector3.forward;
            right = _rot * Vector3.right;

            // ---- forces, m/s^2
            Vector3 acc = new Vector3(0f, -G, 0f);
            acc += fwd * thrust;
            float lift = 0f;
            if (speed > 0.5f)
            {
                Vector3 vhat = _vel / speed;
                float vs = Vs();
                float c = LiftShape(aoa, crit);
                lift = Mathf.Clamp(G * (speed / vs) * (speed / vs) * c, -2f * G, 3.5f * G);
                Vector3 liftDir = Vector3.Cross(vhat, right);
                if (liftDir.sqrMagnitude > 0.0001f) acc += liftDir.normalized * lift;
                float drag = Cd0() * speed * speed + Induced * Mathf.Abs(lift) * Mathf.Abs(c);
                acc -= vhat * drag;
                acc -= right * (Vector3.Dot(_vel, right) * 0.8f);
            }
            _vel += acc * dt;

            // ---- ground
            Vector3 pos = tr.position + _vel * (K * dt);
            Vector3 arrival = _vel;
            float floor;
            bool solid = Floor(pos, out floor);
            bool wasFlying = !_onGround;
            bool contact = solid && pos.y <= floor + 0.02f * K;
            if (contact)
            {
                pos.y = floor;
                if (wasFlying)
                {
                    if (Touchdown(arrival)) return;
                    // Landed: the attitude continues from the flight's.
                    Vector3 f = _rot * Vector3.forward;
                    _gHeading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                    _gTheta = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg,
                                          0f, An2Model.Parked);
                }
                if (_vel.y < 0f) _vel.y = 0f;
                if (!_onGround) RevivalPlugin.L.LogInfo("PlayerAn2: touchdown at "
                    + Mathf.RoundToInt(new Vector3(arrival.x, 0f, arrival.z).magnitude * 3.6f)
                    + " km/h, sink " + (-arrival.y).ToString("0.0") + " m/s.");
                _onGround = true;
                Roll(dt, lift, speed);
                if (Rough(pos, speed)) return;
            }
            else
            {
                if (_onGround)
                {
                    _airborneSince = Time.time;
                    RevivalPlugin.L.LogInfo("PlayerAn2: airborne at "
                        + Mathf.RoundToInt(speed * 3.6f) + " km/h.");
                }
                _onGround = false;
            }

            tr.position = pos;
            tr.rotation = _rot;

            // Wingtips and propeller in the ground; obstacles along the path.
            if (Scrape(tr)) return;
            if (Impact(was, pos)) return;

            // The model: surfaces follow the stick, the prop the engine.
            vis.Throttle = _throttle;
            vis.Elevator = _pIn;
            vis.Aileron = _rIn;
            vis.Rudder = _yIn;
            Pose(pos);
        }

        // Ground attitude: heading and the nose-up angle of the three-point
        // stance. The tail rises with speed and forward stick; the tail wheel
        // steers at taxi speed and the rudder takes over as the air gets hold.
        static float _gHeading, _gTheta;

        static void GroundAttitude(float dt, float speed, float auth)
        {
            float steerFade = Mathf.Clamp01(1f - speed / 28f);
            float yRate = _yIn * (F(CfgGroundSteer, 35f) * steerFade + F(CfgYawRate, 20f) * auth);
            _gHeading += yRate * dt;
            float parked = An2Model.Parked;
            float able = Mathf.Clamp01((speed - 8f) / 14f);
            float lift = able * Mathf.Clamp01(0.45f - 0.55f * _pIn);
            float want = parked * (1f - lift);
            // Back stick at flying speed rotates the aeroplane: the tail is
            // already on the ground, so the nose comes up past the stance only
            // as the wheels leave - the lift does that, not this clamp.
            _gTheta = Mathf.MoveTowards(_gTheta, want, 10f * dt);
            _gTheta = Mathf.Clamp(_gTheta, 0f, parked);

            // The ground's own slope under the wheels: pitch from main to
            // tail, roll across the main track.
            float slopePitch = 0f, slopeRoll = 0f;
            Vector3 p = _plane.transform.position;
            Quaternion flat = Quaternion.Euler(0f, _gHeading, 0f);
            float yl, yr, yt, y0;
            Vector3 tail = An2Model.Contact(2);
            if (Floor(p, out y0)
                && Floor(p + flat * (An2Model.Contact(0) * K), out yl)
                && Floor(p + flat * (An2Model.Contact(1) * K), out yr)
                && Floor(p + flat * (new Vector3(0f, 0f, tail.z) * K), out yt))
            {
                float track = Mathf.Max(0.5f, An2Model.Contact(1).x - An2Model.Contact(0).x) * K;
                slopeRoll = Mathf.Clamp(Mathf.Atan2(yl - yr, track) * Mathf.Rad2Deg, -15f, 15f);
                slopePitch = Mathf.Clamp(Mathf.Atan2(yt - y0, -tail.z * K) * Mathf.Rad2Deg, -15f, 15f);
            }
            _rot = Quaternion.Euler(-(_gTheta + slopePitch), _gHeading, slopeRoll);
        }

        /// <summary>Rolling on the wheels: the tyres do not slide sideways,
        /// friction and brakes slow the roll, and the wing takes the weight
        /// off the wheels as it starts to lift.</summary>
        static void Roll(float dt, float lift, float speed)
        {
            Quaternion flat = Quaternion.Euler(0f, _gHeading, 0f);
            Vector3 f = flat * Vector3.forward, r = flat * Vector3.right;
            float vf = Vector3.Dot(_vel, f), vr = Vector3.Dot(_vel, r);
            vr -= vr * Mathf.Min(1f, 6f * dt);
            if (Time.time > _nextSurface)
            {
                _nextSurface = Time.time + 0.25f;
                _paved = Paved(_plane.transform.position);
            }
            float load = Mathf.Clamp01(1f - lift / G);
            float mu = (_paved ? F(CfgRollFriction, 0.04f) : F(CfgGrassFriction, 0.08f))
                       + (_brake ? F(CfgBrake, 0.35f) : 0f);
            vf = Mathf.MoveTowards(vf, 0f, mu * G * load * dt);
            // A tail-dragger does not roll backwards on its own power.
            if (vf < 0f) vf = Mathf.MoveTowards(vf, 0f, 2f * dt);
            _vel = f * vf + r * vr + Vector3.up * Mathf.Max(0f, _vel.y);
        }

        /// <summary>Concrete or grass: a ray onto what the wheels stand on. The
        /// airfield's slabs and runway are objects; a field is terrain.</summary>
        static bool Paved(Vector3 at)
        {
            Vector3 point, normal;
            GameObject hit = Turret.RaycastObject(at + Vector3.up * (1.2f * K), Vector3.down,
                                                  3f * K, out point, out normal);
            int guard = 0;
            while (hit != null && Mine(hit) && guard++ < 4)
                hit = Turret.RaycastObject(point + Vector3.down * 0.05f, Vector3.down,
                                           2f * K, out point, out normal);
            if (hit == null) return false;
            return !IsTerrain(hit);
        }

        /// <summary>Arriving at the ground. Everything the gear cannot take is
        /// a crash: sink, speed, bank, a nose-first arrival.</summary>
        static bool Touchdown(Vector3 arrival)
        {
            if (CfgCrash != null && !CfgCrash.Value) return false;
            if (Time.time - _airborneSince < 1.0f) return false;
            float sink = -arrival.y;
            float run = new Vector3(arrival.x, 0f, arrival.z).magnitude;
            Vector3 f = _rot * Vector3.forward;
            float nose = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            float bank = Bank(_rot * Vector3.right);
            float sinkLimit = Mathf.Max(0.5f, F(CfgCrashSink, 4f));
            string why = null;
            if (sink > sinkLimit) why = "sink " + sink.ToString("0.0") + " m/s";
            else if (run * 3.6f > F(CfgCrashSpeed, 140f)) why = "ground speed " + Mathf.RoundToInt(run * 3.6f) + " km/h";
            else if (Mathf.Abs(bank) > F(CfgCrashBank, 25f)) why = "bank " + Mathf.RoundToInt(bank) + " deg";
            else if (nose < -6f) why = "nose first (" + Mathf.RoundToInt(nose) + " deg)";
            if (why == null)
            {
                if (sink > 0.6f * sinkLimit)
                    Hint(Loc.T("Жёсткая посадка!", "Hard landing!"), 3f);
                return false;
            }
            RevivalPlugin.L.LogInfo("PlayerAn2: crash landing - " + why + ".");
            Crash(_plane, _plane.transform.position);
            return true;
        }

        /// <summary>Rolling fast into ground that rises steeply: the gear
        /// folds. The runway never trips this; a ditch or a bank does.</summary>
        static bool Rough(Vector3 pos, float speed)
        {
            if (CfgCrash != null && !CfgCrash.Value) return false;
            if (speed < 30f / 3.6f) return false;
            Vector3 flatVel = new Vector3(_vel.x, 0f, _vel.z);
            if (flatVel.sqrMagnitude < 1f) return false;
            Vector3 ahead = pos + flatVel.normalized * (3f * K);
            float y0, y1;
            if (!Floor(pos, out y0) || !Floor(ahead, out y1)) return false;
            float rise = (y1 - y0) / (3f * K);
            if (rise < F(CfgCrashSlope, 0.45f)) return false;
            RevivalPlugin.L.LogInfo("PlayerAn2: gear torn off - ground rises " + rise.ToString("0.00")
                + " per metre at " + Mathf.RoundToInt(speed * 3.6f) + " km/h.");
            Crash(_plane, pos);
            return true;
        }

        /// <summary>Lower wingtips, upper wingtips, propeller tip and tail
        /// cone below the ground: a wing or the propeller has hit it.</summary>
        static bool Scrape(Transform tr)
        {
            if (CfgCrash != null && !CfgCrash.Value) return false;
            if (_vel.magnitude < 2f) return false;
            Vector3[] probes = An2Model.Probes;
            // Rolling on its wheels only a wingtip can meet the ground (a
            // slope across the track); the attitude keeps the propeller
            // clear, and uneven ground under it is not a crash.
            int count = _onGround ? Mathf.Min(4, probes.Length) : probes.Length;
            for (int i = 0; i < count; i++)
            {
                Vector3 w = tr.position + tr.rotation * (probes[i] * K);
                float floor;
                if (!Floor(w, out floor)) continue;
                if (w.y >= floor - 0.05f * K) continue;
                RevivalPlugin.L.LogInfo("PlayerAn2: " + An2Model.ProbeNames[i]
                    + " struck the ground at " + Mathf.RoundToInt(_vel.magnitude * 3.6f) + " km/h.");
                Crash(_plane, w);
                return true;
            }
            return false;
        }

        /// <summary>In the air, the tail wheel is a wheel too: a flare past the
        /// three-point attitude puts it on the ground first, and the aeroplane
        /// pivots nose down about it instead of driving the tail into the
        /// ground. Only the main wheels (the origin) decide the touchdown.</summary>
        static void TailWheel()
        {
            Transform tr = _plane.transform;
            Vector3 tail = An2Model.Contact(2);
            Vector3 w = tr.position + _rot * (tail * K);
            float floor;
            if (!Floor(w, out floor) || w.y >= floor) return;
            float arm = Mathf.Max(1f, -tail.z) * K;
            float down = Mathf.Asin(Mathf.Clamp((floor - w.y) / arm, 0f, 0.5f)) * Mathf.Rad2Deg;
            _rot = _rot * Quaternion.Euler(down, 0f, 0f);
        }

        /// <summary>Signed bank, degrees: positive is right wing down.</summary>
        static float Bank(Vector3 right)
        {
            return -Mathf.Asin(Mathf.Clamp(right.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        // ============================================================ impact

        /// <summary>Obstacles along the frame's travel: one ray from the middle
        /// of the fuselage out to its own surface in that direction, and one
        /// from each wingtip - trees, masts, hangar walls. Slower than
        /// ImpactSpeed the aeroplane only stops; faster it is destroyed.</summary>
        static bool Impact(Vector3 from, Vector3 to)
        {
            if (_plane == null) return false;
            Vector3 travel = to - from;
            float dist = travel.magnitude;
            if (dist < 0.001f) return false;
            Vector3 dir = travel / dist;
            Transform tr = _plane.transform;
            Vector3 axis = Quaternion.Inverse(tr.rotation) * dir;
            // Fuselage: centre 2 m aft of the axle, nose 4.9 m ahead of it,
            // tail 6.7 m behind, 0.9 m half-width, 1.2 m half-height.
            float reach = (axis.z > 0f ? 4.9f : 6.7f) * Mathf.Abs(axis.z)
                          + 0.9f * Mathf.Abs(axis.x) + 1.2f * Mathf.Abs(axis.y);
            Vector3 centre = tr.position + tr.rotation * (new Vector3(0f, 2.2f, -2f) * K);
            if (Cast(centre - travel, dir, dist + reach * K)) return true;
            Vector3 tipU = An2Model.WingTip;
            Vector3 tipL = An2Model.LowerTip;
            float side = 0.4f * K + dist;
            if (Cast(tr.position + tr.rotation * (new Vector3(tipU.x, tipU.y, tipU.z) * K) - travel, dir, side)) return true;
            if (Cast(tr.position + tr.rotation * (new Vector3(-tipU.x, tipU.y, tipU.z) * K) - travel, dir, side)) return true;
            if (Cast(tr.position + tr.rotation * (new Vector3(tipL.x, tipL.y, tipL.z) * K) - travel, dir, side)) return true;
            if (Cast(tr.position + tr.rotation * (new Vector3(-tipL.x, tipL.y, tipL.z) * K) - travel, dir, side)) return true;
            return false;
        }

        static bool Cast(Vector3 origin, Vector3 dir, float range)
        {
            float rest = range;
            for (int step = 0; step < 10 && rest > 0.1f; step++)
            {
                Vector3 point, normal;
                GameObject hit = Turret.RaycastObject(origin, dir, rest, out point, out normal);
                if (hit == null) return false;
                if (Through(hit))
                {
                    float used = Mathf.Max(0.3f, Vector3.Distance(origin, point) + 0.3f);
                    rest -= used;
                    origin = point + dir * 0.3f;
                    continue;
                }
                // The ground is the floor's business; only a wall counts.
                if (IsGround(hit) && normal.y > 0.55f) return false;
                float speed = _vel.magnitude;
                if (speed * 3.6f < F(CfgImpactSpeed, 20f) || (CfgCrash != null && !CfgCrash.Value))
                {
                    // A bump: the aeroplane stops where it was.
                    _plane.transform.position -= dir * Mathf.Min(0.5f * K, Vector3.Distance(origin, point));
                    _vel = Vector3.zero;
                    _throttle = 0f;
                    Hint(Loc.T("Препятствие!", "Obstacle!"), 2f);
                    return true;
                }
                RevivalPlugin.L.LogInfo("PlayerAn2: hit " + hit.name + " at "
                    + Mathf.RoundToInt(speed * 3.6f) + " km/h - the aeroplane is down.");
                Crash(_plane, point);
                return true;
            }
            return false;
        }

        static bool Mine(GameObject hit)
        {
            return hit != null && _plane != null && hit.transform.IsChildOf(_plane.transform);
        }

        static readonly string[] AliveNames = new string[] {
            "PlayerMovementController", "PlayerNetworkController",
            "NPC_AI2", "Animal_AI", "ItemSpawned",
        };
        static Type[] _alive;

        /// <summary>What the rays go through: the aeroplane itself, its crew,
        /// anything alive and loose items. A vehicle is NOT in the list - the
        /// Mi-8's rule (PlayerHeli.Through).</summary>
        static bool Through(GameObject hit)
        {
            if (hit == null) return true;
            Transform t = hit.transform;
            if (_plane != null && t.IsChildOf(_plane.transform)) return true;
            if (_body != null && t.IsChildOf(_body)) return true;
            if (hit.name.StartsWith("MainChar")) return true;
            if (_alive == null)
            {
                _alive = new Type[AliveNames.Length];
                for (int i = 0; i < AliveNames.Length; i++)
                    _alive[i] = RevivalPlugin.TypeByName(AliveNames[i]);
            }
            for (int up = 0; up < 24 && t != null; up++)
            {
                for (int i = 0; i < _alive.Length; i++)
                    if (_alive[i] != null && t.GetComponent(_alive[i]) != null) return true;
                t = t.parent;
            }
            return false;
        }

        static Type _tTerrain;
        static bool _terrainLooked;

        static bool IsTerrain(GameObject go)
        {
            if (go == null) return false;
            if (!_terrainLooked)
            {
                _terrainLooked = true;
                _tTerrain = RevivalPlugin.TypeByName("UnityEngine.Terrain");
            }
            if (_tTerrain != null && go.GetComponent(_tTerrain) != null) return true;
            return go.name.IndexOf("errain") >= 0;
        }

        static bool IsGround(GameObject go)
        {
            if (go == null) return false;
            if (IsTerrain(go)) return true;
            string n = go.name;
            // The airfield's slabs, the runway and the roads are flat objects
            // the wheels roll on; their faces up are ground, their sides are not.
            return n.IndexOf("Road") >= 0 || n.IndexOf("road") >= 0
                || n.IndexOf("Runway") >= 0 || n.IndexOf("taxi") >= 0
                || n.IndexOf("Taxi") >= 0 || n.IndexOf("pron") >= 0;
        }

        // ================================================================ floor

        static bool Floor(Vector3 at, out float y)
        {
            return RevivalTroopInsertion.TerrainHeight(at, out y);
        }

        static bool Height(GameObject go, out float metres)
        {
            metres = 0f;
            if (go == null) return false;
            float floor;
            if (!Floor(go.transform.position, out floor)) return false;
            metres = Mathf.Max(0f, (go.transform.position.y - floor) / K);
            return true;
        }

        static bool Airborne(GameObject go)
        {
            float m;
            return Height(go, out m) && m > 1.5f;
        }

        // ========================================================== spawn

        static float _nextApron, _respawnAt = -1f, _firstSeen = -1f;
        static bool _apronWarned;

        /// <summary>The host parks one An-2 on the H1 apron when none stands in
        /// the world: once the airfield is there, 20 s after the local player
        /// first exists (so objects a previous host made have arrived), and
        /// RespawnMinutes after the last one was cleared away.</summary>
        static void ApronSpawn()
        {
            if (CfgApronSpawn != null && !CfgApronSpawn.Value) return;
            if (Time.time < _nextApron) return;
            _nextApron = Time.time + 3f;
            if (LocalPlayerRoot() == null) return;
            if (_firstSeen < 0f) _firstSeen = Time.time;
            if (Time.time - _firstSeen < 20f) return;
            if (!RevivalTroopInsertion.MasterClient()) return;
            if (Alive() > 0) { _respawnAt = -1f; return; }
            if (_respawnAt < 0f)
            {
                // The first An-2 of a session comes at once; a replacement
                // waits for the balance timer.
                _respawnAt = _spawnedOnce
                    ? Time.time + Mathf.Max(0f, F(CfgRespawnMinutes, 15f)) * 60f
                    : Time.time;
            }
            if (Time.time < _respawnAt) return;
            float y;
            if (!EastWorld.On || !MapScene.AtHome || !RevivalTroopInsertion.TerrainHeight(Apron, out y))
            {
                if (!_apronWarned)
                {
                    _apronWarned = true;
                    RevivalPlugin.L.LogInfo("PlayerAn2: no airfield here ([World] EastTile off, "
                        + "another map, or the tile not loaded) - no An-2 on the apron.");
                }
                return;
            }
            Vector3 at = new Vector3(Apron.x, y, Apron.z);
            float g;
            if (RevivalTroopInsertion.GroundY(at, out g) && Mathf.Abs(g - y) < 3f * K) at.y = Mathf.Max(y, g);
            float heading = ApronHeading;
            Vector3 saved;
            float savedHeading;
            if (An2Repair.SavedPlace(out saved, out savedHeading)) { at = saved; heading = savedHeading; }
            GameObject go = Build(at, heading);
            if (go != null)
            {
                _spawnedOnce = true;
                _respawnAt = -1f;
                RevivalPlugin.L.LogInfo("PlayerAn2: parked on the H1 apron stand.");
            }
            else _nextApron = Time.time + 60f;   // not in a room yet: no log every 3 s
        }

        static bool _spawnedOnce;

        static void SpawnOrRemove()
        {
            GameObject near = Nearest(20f * K);
            if (near != null)
            {
                if (Busy(ViewId(near))) { Hint(Text.Occupied(), 3f); return; }
                Remove(near, true);
                return;
            }
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return;
            Transform t = me.transform;
            float heading = t.eulerAngles.y;
            Vector3 at = t.position + Quaternion.Euler(0f, heading, 0f) * new Vector3(0f, 0f, 14f * K);
            float y;
            if (RevivalTroopInsertion.GroundY(at, out y) || RevivalTroopInsertion.TerrainHeight(at, out y))
                at.y = y;
            if (!RevivalTroopInsertion.MasterClient())
            {
                Net.Send(Net.SpawnRequest, new float[] { at.x, at.y, at.z, heading }, true);
                Hint(Text.Asked(), 3f);
                return;
            }
            if (Build(at, heading) == null) Hint(Text.SpawnFailed(), 4f);
            else Hint(Text.Spawned(Key(CfgBoardKey, KeyCode.F).ToString()), 5f);
        }

        internal static GameObject Build(Vector3 at, float heading)
        {
            if (!An2Model.Load()) return null;
            if (!LookUp()) return null;
            try
            {
                float fuel = Mathf.Clamp01(F(CfgStartFuel, 0.6f)) * F(CfgFuelCapacity, 1200f);
                int parts;
                An2Repair.NewPlane(ref fuel, out parts);
                object[] data = parts < 0 ? new object[] { Marker, fuel } : new object[] { Marker, fuel, parts };
                ParameterInfo[] ps = _instantiate.GetParameters();
                object group = Convert.ChangeType(0, ps[3].ParameterType);
                Quaternion rot = Quaternion.Euler(-An2Model.Parked, heading, 0f);
                GameObject go = _instantiate.Invoke(null, new object[] { Prefab, at, rot, group, data })
                    as GameObject;
                if (go == null)
                {
                    RevivalPlugin.L.LogWarning("PlayerAn2: Photon returned null - not in a room?");
                    return null;
                }
                Prepare(go);
                Idle(go);
                go.transform.position = at;
                go.transform.rotation = rot;
                RevivalPlugin.L.LogInfo("PlayerAn2: An-2 " + ViewId(go) + " at " + at.ToString("0")
                    + ", heading " + heading.ToString("0") + ".");
                return go;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerAn2: spawn failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return null;
            }
        }

        static void Remove(GameObject go, bool say)
        {
            if (go == null) return;
            int view = ViewId(go);
            if (!RevivalTroopInsertion.MasterClient())
            {
                if (view == 0) return;
                Net.Send(Net.RemoveRequest, new float[] { view }, true);
                if (say) Hint(Text.Asked(), 3f);
                return;
            }
            Forget(go);
            HeliFlight.NetDestroy(go);
            if (say) Hint(Text.Removed(), 3f);
            RevivalPlugin.L.LogInfo("PlayerAn2: An-2 " + view + " removed.");
        }

        static int Alive()
        {
            int n = 0;
            for (int i = 0; i < _all.Count; i++) if (_all[i] != null) n++;
            return n;
        }

        // ------------------------------------------------ the greybox stand-in

        static GameObject _standIn;
        static float _nextStandIn;

        /// <summary>The airfield greybox carries a static An-2 stand-in on the
        /// same stand. While a flyable An-2 exists it is hidden (it would be a
        /// second aeroplane inside the first); when none is left it comes
        /// back.</summary>
        static void StandIn()
        {
            if (Time.time < _nextStandIn) return;
            _nextStandIn = Time.time + 2f;
            try
            {
                bool any = Alive() > 0;
                if (_standIn == null && any) _standIn = GameObject.Find(StandInName);
                if (_standIn == null) return;
                if (_standIn.activeSelf == any)
                {
                    _standIn.SetActive(!any);
                    RevivalPlugin.L.LogInfo("PlayerAn2: greybox stand-in " + (any ? "hidden" : "shown") + ".");
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 stand-in: " + ex.Message); }
        }

        // ============================================================ board

        static void Board()
        {
            GameObject go = Nearest(Mathf.Max(3f, F(CfgBoardRange, 9f)) * K);
            if (go == null) return;
            int view = ViewId(go);
            bool taken = Busy(view);
            if (taken && (CfgPassengers == null || !CfgPassengers.Value)) { Hint(Text.Occupied(), 3f); return; }

            _plane = go;
            _body = LocalPlayerRoot();
            _hadBody = _body != null;
            _boardedAt = Time.time;
            _cockpit = false;
            _pilot = !taken;
            _seat = taken ? UnityEngine.Random.Range(0, An2Model.CabinSeats) : -1;
            _look = 6f;
            _orbit = 0f;

            if (_pilot)
            {
                if (!CameraOwner.Request(CameraOwner.An2, true, "PlayerAn2"))
                {
                    _plane = null;
                    _pilot = false;
                    Hint(Text.CamBusy(), 4f);
                    return;
                }
                Transform tr = go.transform;
                _rot = tr.rotation;
                Vector3 f = _rot * Vector3.forward;
                _gHeading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                _gTheta = An2Model.Parked;
                _vel = Vector3.zero;
                _throttle = 0f;
                _pIn = _rIn = _yIn = 0f;
                _onGround = true;
                _stalled = false;
                _airborneSince = Time.time;
                if (!RevivalTroopInsertion.MasterClient()) Interpolator(go, false);
                Idle(go);
                Hint(Text.AtControls(), 8f);
                RevivalPlugin.L.LogInfo("PlayerAn2: controls taken on " + view
                    + " (master: " + RevivalTroopInsertion.MasterClient() + ", fuel "
                    + Mathf.RoundToInt(VisualOf(go).Fuel) + " l).");
            }
            else
            {
                Hint(Text.Seated(), 5f);
                RevivalPlugin.L.LogInfo("PlayerAn2: seated in the cabin of " + view + ".");
            }
            Net.Send(Net.Aboard, new float[] { view, 1f, _pilot ? 1f : 0f }, true);
        }

        /// <summary>Get out. Always runs to the end: the camera and the body
        /// are borrowed and both go back.</summary>
        static void Leave(bool byKey)
        {
            GameObject go = _plane;
            if (byKey && go != null && !Burning(go))
            {
                bool moving = _pilot ? (!_onGround || _vel.magnitude > 3f) : Airborne(go);
                if (moving)
                {
                    bool bail = (CfgBailOut == null || CfgBailOut.Value)
                        && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
                    if (!bail) { Hint(Text.LandFirst(), 4f); return; }
                    Jump();
                    return;
                }
            }
            int view = ViewId(go);
            try
            {
                if (go != null)
                {
                    if (_pilot && !RevivalTroopInsertion.MasterClient() && !Burning(go))
                        Interpolator(go, true);
                    if (view != 0) Net.Send(Net.Aboard, new float[] { view, 0f, 0f }, true);
                    if (_pilot && !Burning(go) && !Gliding(go)) SetEngine(false);
                    Ground(go);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 leave: " + ex.Message); }
            finally
            {
                if (_pilot) CameraOwner.Release(CameraOwner.An2);
                _plane = null;
                _pilot = false;
                _seat = -1;
                _body = null;
                _hadBody = false;
                _vel = Vector3.zero;
                _stalled = false;
            }
            RevivalPlugin.L.LogInfo("PlayerAn2: left " + view + ".");
        }

        static Vector3 DoorOf(GameObject go)
        {
            Transform tr = go.transform;
            Vector3 door = An2Model.Door;
            return tr.position + tr.rotation * (new Vector3(door.x - 1.3f, 0f, door.z) * K);
        }

        /// <summary>Out through the cargo door on the port side, behind the
        /// lower wing, onto the ground.</summary>
        static void Ground(GameObject go)
        {
            if (_body == null || go == null) return;
            Vector3 outside = DoorOf(go);
            if (Airborne(go)) { _body.position = outside; return; }
            float y;
            if (RevivalTroopInsertion.GroundY(outside, out y) || RevivalTroopInsertion.TerrainHeight(outside, out y))
                outside.y = y;
            _body.position = outside;
        }

        static void Jump()
        {
            GameObject go = _plane;
            if (go == null) return;
            bool flying = _pilot ? !_onGround : Airborne(go);
            if (!flying) { Leave(true); return; }
            if (CfgBailOut != null && !CfgBailOut.Value) { Hint(Text.LandFirst(), 4f); return; }
            bool wasPilot = _pilot;
            Vector3 drift = _vel;
            Quaternion rot = _rot;
            Vector3 door = DoorOf(go);
            float height;
            Height(go, out height);
            Leave(false);
            string why;
            bool canopy = Parachute.Jump(door, height, out why);
            if (!string.IsNullOrEmpty(why)) Hint(why, 5f);
            RevivalPlugin.L.LogInfo("PlayerAn2: jumped at " + Mathf.RoundToInt(height) + " m, canopy " + canopy + ".");
            if (wasPilot) Abandon(go, drift, rot, true);
        }

        internal static void DiedAboard()
        {
            if (_plane != null) _diedAboard = true;
        }

        // ========================================================== engine

        static void SetEngine(bool on)
        {
            if (_plane == null) return;
            An2Visual vis = VisualOf(_plane);
            if (on && vis.Fuel <= 0f)
            {
                Hint(Loc.T("Баки пусты", "The tanks are empty"), 4f);
                return;
            }
            string why;
            if (on && !An2Repair.CanStart(_plane, vis.Fuel, out why))
            {
                Hint(why, 6f);
                return;
            }
            vis.Running = on;
            int view = ViewId(_plane);
            if (view != 0) Net.Send(Net.EngineState, new float[] { view, on ? 1f : 0f, vis.Fuel }, true);
            Hint(on ? Text.EngineOn() : Text.EngineOff(), 3f);
            RevivalPlugin.L.LogInfo("PlayerAn2: engine " + (on ? "started" : "stopped") + " on " + view + ".");
        }

        static An2Visual VisualOf(GameObject go)
        {
            An2Visual v = go.GetComponent<An2Visual>();
            if (v == null) v = go.AddComponent<An2Visual>();
            return v;
        }

        // =========================================================== crash

        static void Crash(GameObject go, Vector3 where)
        {
            if (go == null || Burning(go)) return;
            int view = ViewId(go);
            Burn(go, where);
            if (view != 0) Net.Send(Net.Crashed, new float[] { view, where.x, where.y, where.z }, true);
            DestroyedAboard(go, "crash");
        }

        static void DestroyedAboard(GameObject go, string how)
        {
            if (go == null || !ReferenceEquals(go, _plane)) return;
            float damage = F(CfgCrashDamage, 1000f);
            Leave(false);
            if (damage > 0f) Hurt(damage);
            Hint(Text.Wrecked(), 6f);
            RevivalPlugin.L.LogInfo("PlayerAn2: destroyed with the local player aboard (" + how + ").");
        }

        /// <summary>Every client: the bang, the fire of a burning aircraft, a
        /// dead engine, a scorched airframe lying where it came down.</summary>
        static void Burn(GameObject go, Vector3 where)
        {
            if (go == null || Burning(go)) return;
            try
            {
                _burning[go] = Time.time + Mathf.Max(5f, F(CfgWreckSeconds, 180f));
                An2Repair.Wrecked(go);
                An2Glide glide = go.GetComponent<An2Glide>();
                if (glide != null) UnityEngine.Object.Destroy(glide);
                Interpolator(go, false);
                An2Visual vis = VisualOf(go);
                vis.Kill();

                // Lie down: on the ground, the nose dug in, one wing low.
                Transform tr = go.transform;
                float floor;
                Vector3 p = tr.position;
                if (Floor(p, out floor)) p.y = floor;
                Vector3 f = tr.forward;
                float heading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                float side = (ViewId(go) % 2 == 0) ? 1f : -1f;
                tr.position = p + Vector3.down * (0.35f * K);
                tr.rotation = Quaternion.Euler(4f, heading + side * 12f, side * 11f);

                FireEffect.SpawnHeliBlast(where + Vector3.up * (1.5f * K), 16f);
                // SpawnHeliFire places its flame beds in the Mi-8's hull frame
                // (x 1..4, z -13..16 model units); the anchor shifts that onto
                // the An-2's fuselage (centre x 0, z -25..+8 u).
                GameObject anchor = new GameObject("NDR_An2FireAnchor");
                anchor.transform.SetParent(tr, false);
                anchor.transform.localPosition = new Vector3(-2.5f, 0.6f, -8.5f);
                if (!FireEffect.SpawnHeliFire(anchor)) FireEffect.SpawnWreck(go, false);
                HeliCrashSound.Play(where);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerAn2 burn: " + ex.Message);
            }
        }

        internal static bool Burning(GameObject go)
        {
            return go != null && _burning.ContainsKey(go);
        }

        static bool Gliding(GameObject go)
        {
            return go != null && go.GetComponent<An2Glide>() != null;
        }

        /// <summary>An aeroplane nobody flies any more goes on with the speed it
        /// had, loses its trim and spirals in. Every client runs the same
        /// descent from the same start (the Crashed event with ten floats).</summary>
        static void Abandon(GameObject go, Vector3 vel, Quaternion rot, bool broadcast)
        {
            if (go == null || Burning(go) || Gliding(go)) return;
            if (CfgCrash != null && !CfgCrash.Value) return;
            try
            {
                int view = ViewId(go);
                _busyUntil.Remove(view);
                if (!RevivalTroopInsertion.MasterClient()) Interpolator(go, false);
                An2Glide g = go.AddComponent<An2Glide>();
                g.Begin(vel, rot);
                if (broadcast && view != 0)
                {
                    Vector3 at = go.transform.position;
                    Vector3 e = rot.eulerAngles;
                    Net.Send(Net.Crashed, new float[] { view, at.x, at.y, at.z,
                        vel.x, vel.y, vel.z, e.x, e.y, e.z }, true);
                }
                RevivalPlugin.L.LogInfo("PlayerAn2: " + view + " abandoned in the air - going down.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerAn2 abandon: " + ex.Message);
                Crash(go, go.transform.position);
            }
        }

        internal static void GlideFloor(Vector3 at, out float y, out bool solid)
        {
            solid = Floor(at, out y);
        }

        /// <summary>The abandoned aeroplane reached the ground (every client
        /// for itself). Anybody still in the cabin goes with it.</summary>
        internal static void FinishGlide(GameObject go, Vector3 where)
        {
            if (go == null || Burning(go)) return;
            Burn(go, where);
            DestroyedAboard(go, "abandoned aeroplane came down");
        }

        static void Wrecks()
        {
            if (_burning.Count == 0) return;
            bool master = RevivalTroopInsertion.MasterClient();
            _gone.Clear();
            foreach (KeyValuePair<GameObject, float> e in _burning)
                if (e.Key == null || (master && Time.time > e.Value)) _gone.Add(e.Key);
            for (int i = 0; i < _gone.Count; i++)
            {
                GameObject go = _gone[i];
                _burning.Remove(go);
                if (go == null || !master) continue;
                Forget(go);
                HeliFlight.NetDestroy(go);
                RevivalPlugin.L.LogInfo("PlayerAn2: a burnt-out wreck was cleared away.");
            }
            _gone.Clear();
        }

        static Component _life;
        static bool _hurtWarned;

        /// <summary>The game's own damage gate (PlayerLifeDataManager
        /// .PlayerApplyDamage), as the Mi-8 uses it.</summary>
        static void Hurt(float damage)
        {
            if (damage <= 0f || _hurtWarned) return;
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                Transform root = LocalPlayerRoot();
                _life = (t == null || root == null) ? null : root.GetComponentInChildren(t);
                if (_life == null) return;
                MethodInfo m = AccessTools.Method(_life.GetType(), "PlayerApplyDamage", null, null);
                if (m == null) { _hurtWarned = true; return; }
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
                RevivalPlugin.L.LogWarning("PlayerAn2 crash damage: " + ex.Message);
            }
        }

        // ========================================================= network pose

        static void Pose(Vector3 pos)
        {
            if (Time.time < _nextPose) return;
            float hz = Mathf.Clamp(F(CfgNetHz, 15f), 2f, 30f);
            _nextPose = Time.time + 1f / hz;
            int view = ViewId(_plane);
            if (view == 0) return;
            An2Visual vis = VisualOf(_plane);
            Vector3 e = _rot.eulerAngles;
            // Every pilot sends, the master too: the pose is only WRITTEN by
            // the master (for a non-master pilot), but every client reads the
            // stick, the throttle and the fuel from it for its own model.
            Net.Send(Net.PoseUpdate, new float[] { view, pos.x, pos.y, pos.z, e.x, e.y, e.z,
                vis.Running ? 1f : 0f, _throttle, _pIn, _rIn, _yIn, vis.Fuel }, false);
        }

        static float _nextState;

        static void Heartbeat()
        {
            if (Time.time < _nextHeartbeat) return;
            _nextHeartbeat = Time.time + 2f;
            int view = ViewId(_plane);
            if (view == 0) return;
            Net.Send(Net.Aboard, new float[] { view, 1f, _pilot ? 1f : 0f }, false);
        }

        /// <summary>The host tells everybody, every five seconds, which engines
        /// run and how much fuel each An-2 has: that is how a late joiner or a
        /// new host learns it.</summary>
        static void State()
        {
            if (Time.time < _nextState) return;
            _nextState = Time.time + 5f;
            if (!RevivalTroopInsertion.MasterClient()) return;
            for (int i = 0; i < _all.Count; i++)
            {
                GameObject go = _all[i];
                if (go == null || Burning(go)) continue;
                int view = ViewId(go);
                if (view == 0) continue;
                An2Visual vis = VisualOf(go);
                Net.Send(Net.EngineState, new float[] { view, vis.Running ? 1f : 0f, vis.Fuel }, false);
                An2Repair.Broadcast(go, view);
            }
        }

        // ============================================================ object

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
            if (photon == null) return false;
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
                RevivalPlugin.L.LogWarning("PlayerAn2: PhotonNetwork.InstantiateSceneObject not found.");
            return _instantiate != null;
        }

        /// <summary>The Mi-8's own mover must never move the carrier:
        /// HelicopterDummy's movement returns at once while startPosition is
        /// zero.</summary>
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
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 idle: " + ex.Message); }
        }

        /// <summary>
        /// The same on every client, the creator, the others and late joiners:
        /// the Mi-8 drawn as nothing and silent, its colliders off, the An-2
        /// model built on it with its own hull boxes, the fuel from the
        /// instantiation data.
        /// </summary>
        internal static void Prepare(GameObject go)
        {
            if (go == null || _all.Contains(go)) return;
            _all.Add(go);
            try
            {
                go.transform.localScale = Vector3.one;
                Renderer[] drawn = go.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < drawn.Length; i++) drawn[i].enabled = false;
                Light[] lights = go.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++) lights[i].enabled = false;

                if (_tCollider == null) _tCollider = RevivalPlugin.TypeByName("Collider");
                if (_tCollider != null)
                {
                    if (_pColliderEnabled == null) _pColliderEnabled = AccessTools.Property(_tCollider, "enabled");
                    Component[] cols = go.GetComponentsInChildren(_tCollider, true);
                    for (int i = 0; i < cols.Length && _pColliderEnabled != null; i++)
                        _pColliderEnabled.SetValue(cols[i], false, null);
                }

                // The rotor stopped and the turbine silent for good.
                HeliEngine rotor = go.GetComponent<HeliEngine>();
                if (rotor == null) rotor = go.AddComponent<HeliEngine>();
                rotor.Kill();

                An2Visual vis = VisualOf(go);
                if (_tDummy == null) _tDummy = RevivalPlugin.TypeByName("HelicopterDummy");
                Component heli = _tDummy == null ? null : go.GetComponent(_tDummy);
                object[] data = heli == null ? null : InstantiationData(heli);
                vis.Fuel = (data != null && data.Length >= 2 && data[1] is float)
                    ? (float)data[1]
                    : Mathf.Clamp01(F(CfgStartFuel, 0.6f)) * F(CfgFuelCapacity, 1200f);
                bool built = An2Model.Build(go.transform, vis);
                An2Repair.Prepared(go, data);

                if (_tBox == null) _tBox = RevivalPlugin.TypeByName("BoxCollider");
                if (_tBox != null)
                {
                    if (_pBoxCentre == null) _pBoxCentre = AccessTools.Property(_tBox, "center");
                    if (_pBoxSize == null) _pBoxSize = AccessTools.Property(_tBox, "size");
                    for (int i = 0; i < An2Model.Boxes.Count; i++)
                    {
                        Bounds b = An2Model.Boxes[i];
                        GameObject hull = new GameObject("NDR_An2Hull" + i);
                        hull.transform.SetParent(go.transform, false);
                        Component box = hull.AddComponent(_tBox);
                        if (_pBoxCentre != null) _pBoxCentre.SetValue(box, b.center * K, null);
                        if (_pBoxSize != null) _pBoxSize.SetValue(box, b.size * K, null);
                    }
                }
                int view = ViewId(go);
                if (view != 0) _byView[view] = go;
                RevivalPlugin.L.LogInfo("PlayerAn2: An-2 " + view + " prepared (model "
                    + (built ? "built" : "MISSING - see the asset lines above")
                    + ", fuel " + Mathf.RoundToInt(vis.Fuel) + " l).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("PlayerAn2 prepare: " + ex);
            }
        }

        static void Forget(GameObject go)
        {
            _all.Remove(go);
            int view = ViewId(go);
            if (view != 0) { _byView.Remove(view); _busyUntil.Remove(view); }
        }

        static void Sweep()
        {
            for (int i = _all.Count - 1; i >= 0; i--) if (_all[i] == null) _all.RemoveAt(i);
            if (_plane == null && (_pilot || _hadBody)) Leave(false);
            _drop.Clear();
            foreach (KeyValuePair<int, float> e in _busyUntil) if (Time.time > e.Value) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _busyUntil.Remove(_drop[i]);
            _drop.Clear();
            foreach (KeyValuePair<int, GameObject> e in _byView) if (e.Value == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _byView.Remove(_drop[i]);
            State();
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
                if (go == null || Burning(go) || Gliding(go)) continue;
                // Measured from the middle of the fuselage, not the axle.
                Vector3 c = go.transform.position + go.transform.rotation * (new Vector3(0f, 1.5f, -2f) * K);
                float d = Vector3.Distance(p, c);
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

        static GameObject ByView(int view)
        {
            GameObject go;
            if (_byView.TryGetValue(view, out go) && go != null) return go;
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i] == null || ViewId(_all[i]) != view) continue;
                _byView[view] = _all[i];
                return _all[i];
            }
            try
            {
                if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
                if (_tView == null) return null;
                if (_mFind == null) _mFind = AccessTools.Method(_tView, "Find", new Type[] { typeof(int) }, null);
                if (_mFind == null) return null;
                Component found = _mFind.Invoke(null, new object[] { view }) as Component;
                return found == null || !_all.Contains(found.gameObject) ? null : found.gameObject;
            }
            catch { return null; }
        }

        static void Interpolator(GameObject go, bool on)
        {
            try
            {
                if (_tInterp == null) _tInterp = RevivalPlugin.TypeByName("PhotonInterpolatedTransform");
                Component c = _tInterp == null ? null : go.GetComponent(_tInterp);
                if (c == null) return;
                if (_pInterpEnabled == null) _pInterpEnabled = AccessTools.Property(_tInterp, "enabled");
                if (_pInterpEnabled != null) _pInterpEnabled.SetValue(c, on, null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 interpolator: " + ex.Message); }
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

        static Transform _rootCache;
        static float _rootRetry;

        static Transform LocalPlayerRoot()
        {
            if (_rootCache != null) return _rootCache;
            if (Time.time < _rootRetry) return null;
            _rootRetry = Time.time + 0.5f;
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
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2: local player: " + ex.Message); }
            return null;
        }

        static bool IsMine(MonoBehaviour mb)
        {
            MethodInfo get = AccessTools.Method(mb.GetType(), "get_photonView", null, null);
            object view = null;
            try { if (get != null) view = get.Invoke(mb, null); }
            catch { view = null; }
            if (view == null) return true;
            MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
            try { return isMine == null || (bool)isMine.Invoke(view, null); }
            catch { return true; }
        }

        // ================================================================ keys

        static readonly Dictionary<ConfigEntry<string>, KeyCode> _keys = new Dictionary<ConfigEntry<string>, KeyCode>();

        static KeyCode Key(ConfigEntry<string> entry, KeyCode fallback)
        {
            if (entry == null) return fallback;
            KeyCode k;
            if (_keys.TryGetValue(entry, out k)) return k;
            try { k = (KeyCode)Enum.Parse(typeof(KeyCode), entry.Value, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("PlayerAn2: key \"" + entry.Value + "\" is unknown, using " + fallback + ".");
                k = fallback;
            }
            _keys[entry] = k;
            return k;
        }

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
            if (_plane == null && !hint) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            if (_plane != null && _pilot)
            {
                An2Visual vis = VisualOf(_plane);
                float agl;
                Height(_plane, out agl);
                float kmh = _vel.magnitude * 3.6f;
                Line(Text.Readout(Mathf.RoundToInt(kmh), Mathf.RoundToInt(agl), _vel.y, _onGround, _paved),
                     cx, cy + 150f, new Color(0.92f, 0.92f, 0.86f, 1f), 15);
                Line(Text.Engine(Mathf.RoundToInt(_throttle * 100f), Mathf.RoundToInt(vis.Power * 100f),
                                 vis.Running, _brake && _onGround),
                     cx, cy + 172f, vis.Power < 0.3f ? new Color(1f, 0.6f, 0.4f, 1f)
                                                     : new Color(0.72f, 0.80f, 0.72f, 1f), 13);
                Gauge(cx, cy + 198f, vis.Fuel, Mathf.Max(1f, F(CfgFuelCapacity, 1200f)));
                bool slow = !_onGround && kmh < F(CfgStallSpeed, 60f) * 1.15f;
                if ((_stalled || slow) && Mathf.Repeat(Time.time, 0.6f) < 0.4f)
                    Line(_stalled ? Loc.T("СВАЛИВАНИЕ", "STALL") : Loc.T("МАЛАЯ СКОРОСТЬ", "LOW SPEED"),
                         cx, cy + 100f, new Color(1f, 0.25f, 0.2f, 1f), 22);
                if (Time.time - _boardedAt < 14f)
                    Line(Text.Controls(Key(CfgEngineKey, KeyCode.G).ToString(), Key(CfgViewKey, KeyCode.V).ToString(),
                                       Key(CfgJumpKey, KeyCode.X).ToString(), Key(CfgBoardKey, KeyCode.F).ToString(),
                                       Key(CfgBrakeKey, KeyCode.B).ToString()),
                         cx, cy + 222f, new Color(0.80f, 0.85f, 0.90f, 1f), 13);
            }
            if (hint) Line(_hint, cx, cy + 120f, new Color(1f, 0.92f, 0.70f, 1f), 16);
        }

        /// <summary>The fuel gauge: a bar with litres and percent, amber under
        /// a quarter, red under a tenth.</summary>
        static void Gauge(float cx, float y, float fuel, float capacity)
        {
            float frac = Mathf.Clamp01(fuel / capacity);
            const float w = 220f, h = 10f;
            Rect back = new Rect(cx - w * 0.5f, y, w, h);
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(back.x - 2f, back.y - 2f, back.width + 4f, back.height + 4f), Texture2D.whiteTexture);
            GUI.color = frac < 0.1f ? new Color(0.95f, 0.25f, 0.2f, 0.95f)
                      : frac < 0.25f ? new Color(0.95f, 0.7f, 0.2f, 0.95f)
                      : new Color(0.55f, 0.8f, 0.45f, 0.95f);
            GUI.DrawTexture(new Rect(back.x, back.y, back.width * frac, back.height), Texture2D.whiteTexture);
            GUI.color = keep;
            Line(Text.Fuel(Mathf.RoundToInt(fuel), Mathf.RoundToInt(frac * 100f)), cx, y + 8f,
                 new Color(0.85f, 0.85f, 0.80f, 1f), 12);
        }

        static void Line(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUIContent content = new GUIContent(text);
            Vector2 measured = style.CalcSize(content);
            GUI.Label(new Rect(cx - measured.x * 0.5f, y + (26f - measured.y) * 0.5f,
                               measured.x, measured.y), content, style);
        }

        // ============================================================ install

        static bool _installed;

        internal static void Install(Harmony harmony)
        {
            if (!Enabled || _installed) return;
            _installed = true;
            try
            {
                if (_tDummy == null) _tDummy = RevivalPlugin.TypeByName("HelicopterDummy");
                MethodInfo start = _tDummy == null ? null : AccessTools.Method(_tDummy, "Start", null, null);
                if (start == null)
                    RevivalPlugin.L.LogWarning("PlayerAn2: HelicopterDummy.Start not found - no An-2 can be prepared.");
                else
                    harmony.Patch(start,
                        new HarmonyMethod(typeof(PlayerAn2).GetMethod("StartPrefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(PlayerAn2).GetMethod("StartPostfix", BindingFlags.Public | BindingFlags.Static)),
                        null, null, null);
                // The Mi-8's body lock and fall guard serve both aircraft; they
                // install once, whichever of the two asks first.
                HeliInputHook.Install(harmony);
                HeliBodyGuard.Install(harmony);
                An2Model.Load();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerAn2: hook failed - " + ex);
            }
        }

        /// <summary>Prepare our carrier on every client, and keep the aid
        /// event's helicopter reference away from it (the Mi-8's guard).</summary>
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
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 start: " + ex.Message); }
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
        /// Eight codes from NetworkEventCode (150): base+0 asks the master for an
        /// An-2, +1 the pilot's pose and controls (13 floats), +2 who is aboard,
        /// +3 asks the master to remove one, +4 engine and fuel of one An-2,
        /// +5 destroyed (4 floats) or abandoned in the air (10 floats), +6 the
        /// repair state from the master, +7 a finished fitting or refuelling to
        /// the master (both Revival.An2Repair.cs).
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
                return Mathf.Clamp(CfgEventCode == null ? 150 : CfgEventCode.Value, 0, 192);
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
                        RevivalPlugin.L.LogWarning("PlayerAn2 net: RaiseEvent or OnEventCall missing.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("PlayerAn2 net hooked: event codes " + Base() + "-" + (Base() + 7) + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("PlayerAn2 net not hooked: " + ex);
                }
            }

            internal static void Send(int kind, float[] content, bool reliable)
            {
                if (!_hooked) return;
                try
                {
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] { (byte)(Base() + kind), content, reliable, opts });
                }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerAn2 net send: " + ex.Message); }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                try
                {
                    int kind = code - Base();
                    if (kind < 0 || kind > 7) return;
                    float[] f = content as float[];
                    if (f == null || !Enabled) return;
                    if (kind == An2Repair.NetState || kind == An2Repair.NetRequest)
                    {
                        An2Repair.OnNet(kind, f, sender);
                        return;
                    }

                    if (kind == SpawnRequest)
                    {
                        if (f.Length < 4 || !RevivalTroopInsertion.MasterClient()) return;
                        Build(new Vector3(f[0], f[1], f[2]), f[3]);
                        return;
                    }
                    if (kind == PoseUpdate)
                    {
                        if (f.Length < 13) return;
                        int view = (int)f[0];
                        _busyUntil[view] = Time.time + 5f;
                        GameObject go = ByView(view);
                        if (go == null || Flown(go)) return;
                        An2Visual vis = VisualOf(go);
                        vis.Running = f[7] > 0.5f;
                        vis.Throttle = f[8];
                        vis.Elevator = f[9];
                        vis.Aileron = f[10];
                        vis.Rudder = f[11];
                        vis.Fuel = f[12];
                        if (!RevivalTroopInsertion.MasterClient() || Burning(go) || Gliding(go)) return;
                        go.transform.position = new Vector3(f[1], f[2], f[3]);
                        go.transform.rotation = Quaternion.Euler(f[4], f[5], f[6]);
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
                        if (go == null || Flown(go) || Burning(go)) return;
                        An2Visual vis = VisualOf(go);
                        vis.Running = f[1] > 0.5f;
                        if (f.Length >= 3) vis.Fuel = f[2];
                        return;
                    }
                    if (kind == Crashed)
                    {
                        if (f.Length < 4) return;
                        GameObject wreck = ByView((int)f[0]);
                        if (wreck == null) return;
                        _busyUntil.Remove((int)f[0]);
                        if (f.Length >= 10)
                        {
                            wreck.transform.position = new Vector3(f[1], f[2], f[3]);
                            Abandon(wreck, new Vector3(f[4], f[5], f[6]),
                                    Quaternion.Euler(f[7], f[8], f[9]), false);
                            return;
                        }
                        Burn(wreck, new Vector3(f[1], f[2], f[3]));
                        DestroyedAboard(wreck, "crash on the pilot's client");
                        return;
                    }
                    // RemoveRequest
                    if (f.Length < 1 || !RevivalTroopInsertion.MasterClient()) return;
                    int id = (int)f[0];
                    if (Busy(id)) return;
                    GameObject gone = ByView(id);
                    if (gone == null) return;
                    Forget(gone);
                    HeliFlight.NetDestroy(gone);
                    RevivalPlugin.L.LogInfo("PlayerAn2: An-2 " + id + " removed for player " + sender + ".");
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("PlayerAn2 net receive: " + ex.Message);
                }
            }
        }

        // ============================================================== lines

        internal static class Text
        {
            internal static string Spawned(string key)
            {
                return Loc.T("Ан-2 подан - " + key + ", чтобы сесть", "An-2 ready - press " + key + " to get in");
            }
            internal static string SpawnFailed() { return Loc.T("Ан-2 не появился - смотри лог", "The An-2 did not appear - see the log"); }
            internal static string Asked() { return Loc.T("Запрос отправлен хосту", "Asked the host"); }
            internal static string Removed() { return Loc.T("Ан-2 убран", "An-2 removed"); }
            internal static string Occupied() { return Loc.T("В этом Ан-2 уже есть пилот", "Somebody is already flying this one"); }
            internal static string CamBusy() { return Loc.T("Обзор занят - сначала выйди из другого вида", "The view is taken - leave the other view first"); }
            internal static string AtControls() { return Loc.T("Ты за штурвалом Ан-2", "You have the controls of the An-2"); }
            internal static string EngineOn() { return Loc.T("Запуск двигателя - прогрев", "Starting up - the engine warms up"); }
            internal static string EngineOff() { return Loc.T("Двигатель остановлен", "Engine stopped"); }
            internal static string Wrecked() { return Loc.T("Самолёт разбит", "The aeroplane is wrecked"); }
            internal static string Seated() { return Loc.T("Ты в грузовой кабине", "You are in the cabin"); }
            internal static string LandFirst() { return Loc.T("Сначала остановись (Shift - прыжок)", "Stop first (shift to jump out)"); }

            internal static string Readout(int kmh, int metres, float climb, bool onGround, bool paved)
            {
                string state = onGround
                    ? (paved ? Loc.T("на бетоне", "on concrete") : Loc.T("на грунте", "on grass"))
                    : (metres + Loc.T(" м", " m") + "   " + (climb >= 0f ? "+" : "")
                       + climb.ToString("0.0") + Loc.T(" м/с", " m/s"));
                return kmh + Loc.T(" км/ч", " km/h") + "   " + state;
            }

            internal static string Engine(int throttle, int rpm, bool running, bool brake)
            {
                string e = running || rpm > 0
                    ? Loc.T("газ ", "throttle ") + throttle + "%   " + Loc.T("обороты ", "rpm ") + rpm + "%"
                    : Loc.T("двигатель выключен", "engine off") + "   " + Loc.T("газ ", "throttle ") + throttle + "%";
                return brake ? e + "   " + Loc.T("ТОРМОЗ", "BRAKE") : e;
            }

            internal static string Fuel(int litres, int percent)
            {
                return Loc.T("топливо ", "fuel ") + litres + Loc.T(" л", " l") + " (" + percent + "%)";
            }

            internal static string Controls(string engine, string view, string jump, string board, string brake)
            {
                return Loc.T(
                    "Пробел/Ctrl - газ, W/S - тангаж, A/D - крен, Q/E - руль, " + brake + " - тормоз, "
                    + engine + " - двигатель, " + view + " - вид, " + jump + " - прыжок, " + board + " - выйти",
                    "space/ctrl throttle, W/S pitch, A/D roll, Q/E rudder, " + brake + " brake, "
                    + engine + " engine, " + view + " view, " + jump + " jump, " + board + " out");
            }
        }
    }

    /// <summary>
    /// The An-2 model from an2_import.py: meshes, materials and the rig file.
    /// Loaded once, built on every carrier.
    /// </summary>
    internal static class An2Model
    {
        static bool _loaded, _ok;
        static Mesh _body, _glass, _prop, _ailL, _ailR, _elev, _rud;
        static Material _skin, _clear;
        static readonly Dictionary<string, Vector3> _pivot = new Dictionary<string, Vector3>();
        static readonly Dictionary<string, Vector3> _axis = new Dictionary<string, Vector3>();
        static readonly Dictionary<int, Vector3> _seats = new Dictionary<int, Vector3>();
        static readonly Vector3[] _contact = new Vector3[] {
            new Vector3(-1.725f, 0f, 0f), new Vector3(1.725f, 0f, 0f), new Vector3(0f, 1.19f, -8.25f) };
        internal static readonly List<Bounds> Boxes = new List<Bounds>();
        internal static float Parked = 8.21f;
        internal static int CabinSeats = 6;
        internal static Vector3 Door = new Vector3(-0.88f, 1.28f, -2.1f);
        internal static Vector3 WingTip = new Vector3(9.09f, 4.8f, -0.96f);
        internal static Vector3 LowerTip = new Vector3(7.0f, 1.55f, -0.65f);
        internal static float Scale = PlayerAn2.K;

        /// <summary>Points that must never touch the ground (metres, flight
        /// line frame): the four wingtips first, then the propeller's lowest
        /// tip.</summary>
        internal static Vector3[] Probes = new Vector3[0];
        internal static string[] ProbeNames = new string[0];

        internal static Vector3 Seat(int index)
        {
            Vector3 s;
            if (_seats.TryGetValue(index < 0 ? -1 : index % Mathf.Max(1, CabinSeats), out s)) return s;
            return index < 0 ? new Vector3(-0.42f, 1.38f, 0.13f) : new Vector3(0f, 1.45f, -2f);
        }

        internal static Vector3 Contact(int i) { return _contact[i]; }

        internal static bool Load()
        {
            if (_loaded) return _ok;
            _loaded = true;
            try
            {
                ReadRig(Path.Combine(RevivalPlugin.AssetDir, "an2_rig.txt"));
                _body = Assets.Load("an2_body.ndmesh");
                _glass = Assets.Load("an2_glass.ndmesh");
                _prop = Assets.Load("an2_prop.ndmesh");
                _ailL = Assets.Load("an2_aileron_l.ndmesh");
                _ailR = Assets.Load("an2_aileron_r.ndmesh");
                _elev = Assets.Load("an2_elevator.ndmesh");
                _rud = Assets.Load("an2_rudder.ndmesh");
                Texture2D tex = Assets.Texture("an2_diffuse.png", false, true);
                Texture2D nrm = Assets.Texture("an2_normal.png", true, true);
                if (_body == null || _prop == null || _ailL == null || _ailR == null
                    || _elev == null || _rud == null || tex == null)
                    throw new InvalidOperationException("An-2 assets missing; repair the client package (python an2_import.py)");

                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _skin = new Material(shader);
                _skin.name = "NDR_An2_Skin";
                tex.anisoLevel = 8;
                tex.filterMode = FilterMode.Trilinear;
                tex.Compress(true); tex.Apply(true, true);
                _skin.mainTexture = tex;
                _skin.color = Color.white;
                if (nrm != null && _skin.HasProperty("_BumpMap"))
                {
                    _skin.SetTexture("_BumpMap", nrm);
                    _skin.EnableKeyword("_NORMALMAP");
                }
                if (_skin.HasProperty("_Glossiness")) _skin.SetFloat("_Glossiness", 0.3f);
                if (_skin.HasProperty("_Metallic")) _skin.SetFloat("_Metallic", 0.1f);

                Shader st = Shader.Find("Standard");
                _clear = new Material(st != null ? st : Shader.Find("Legacy Shaders/Transparent/Diffuse"));
                _clear.name = "NDR_An2_Glass";
                _clear.color = new Color(0.22f, 0.28f, 0.30f, 0.38f);
                if (st != null)
                {
                    // Standard in Fade mode, set the way its own inspector does.
                    _clear.SetFloat("_Mode", 2f);
                    _clear.SetOverrideTag("RenderType", "Transparent");
                    _clear.SetInt("_SrcBlend", 5);          // SrcAlpha
                    _clear.SetInt("_DstBlend", 10);         // OneMinusSrcAlpha
                    _clear.SetInt("_ZWrite", 0);
                    _clear.DisableKeyword("_ALPHATEST_ON");
                    _clear.EnableKeyword("_ALPHABLEND_ON");
                    _clear.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    _clear.renderQueue = 3000;
                    if (_clear.HasProperty("_Glossiness")) _clear.SetFloat("_Glossiness", 0.85f);
                }
                _ok = true;
                RevivalPlugin.L.LogInfo("PlayerAn2: model loaded - " + (_body.vertexCount) + " body vertices, scale "
                    + Scale + " u/m, parked " + Parked.ToString("0.00") + " deg, " + Boxes.Count + " hull boxes.");
            }
            catch (Exception ex)
            {
                _ok = false;
                RevivalPlugin.L.LogError("PlayerAn2 model: " + ex.Message);
            }
            return _ok;
        }

        static Vector3 V(string[] p, int i)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(float.Parse(p[i], c), float.Parse(p[i + 1], c), float.Parse(p[i + 2], c));
        }

        static void ReadRig(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("missing: " + path);
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            List<Vector3> probes = new List<Vector3>();
            List<string> names = new List<string>();
            int seats = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "scale":
                        float s = float.Parse(p[1], c);
                        if (Mathf.Abs(s - PlayerAn2.K) > 0.001f)
                            RevivalPlugin.L.LogWarning("PlayerAn2: an2_rig.txt was built for " + s
                                + " u/m, the plugin uses " + PlayerAn2.K + ".");
                        break;
                    case "pivot":
                        _pivot[p[1]] = V(p, 2);
                        _axis[p[1]] = V(p, 5).normalized;
                        break;
                    case "contact":
                        int w = p[1] == "l" ? 0 : (p[1] == "r" ? 1 : 2);
                        _contact[w] = V(p, 2);
                        break;
                    case "parked": Parked = float.Parse(p[1], c); break;
                    case "seat":
                        int i = int.Parse(p[1], c);
                        _seats[i] = V(p, 2);
                        if (i >= 0) seats++;
                        break;
                    case "door": Door = V(p, 1); break;
                    case "box":
                        Boxes.Add(new Bounds(V(p, 2), V(p, 5)));
                        if (p[1] == "lowerwing")
                        {
                            Vector3 cc = V(p, 2), ss = V(p, 5);
                            LowerTip = new Vector3(ss.x * 0.5f, cc.y, cc.z);
                        }
                        break;
                    case "wingtip": WingTip = V(p, 1); break;
                }
            }
            if (seats > 0) CabinSeats = seats;
            probes.Add(new Vector3(WingTip.x, WingTip.y - 0.3f, WingTip.z)); names.Add("the right upper wingtip");
            probes.Add(new Vector3(-WingTip.x, WingTip.y - 0.3f, WingTip.z)); names.Add("the left upper wingtip");
            probes.Add(new Vector3(LowerTip.x, LowerTip.y - 0.2f, LowerTip.z)); names.Add("the right lower wingtip");
            probes.Add(new Vector3(-LowerTip.x, LowerTip.y - 0.2f, LowerTip.z)); names.Add("the left lower wingtip");
            Vector3 hub;
            if (_pivot.TryGetValue("prop", out hub))
            {
                probes.Add(new Vector3(hub.x, hub.y - 1.8f, hub.z)); names.Add("the propeller");
            }
            Probes = probes.ToArray();
            ProbeNames = names.ToArray();
        }

        static GameObject Part(Transform parent, string name, Mesh mesh, Material mat)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            if (mesh == null) return go;
            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        static Transform Hinge(Transform parent, string part, Mesh mesh)
        {
            Vector3 at;
            _pivot.TryGetValue(part, out at);
            GameObject pivot = new GameObject("Pivot_" + part);
            pivot.transform.SetParent(parent, false);
            pivot.transform.localPosition = at;
            Part(pivot.transform, part, mesh, _skin);
            return pivot.transform;
        }

        internal static Vector3 AxisOf(string part)
        {
            Vector3 a;
            return _axis.TryGetValue(part, out a) ? a : Vector3.right;
        }

        /// <summary>The model on one carrier: a root at the carrier's origin
        /// (the ground under the main axle), scaled from metres to world
        /// units, with a hinge per moving part.</summary>
        internal static bool Build(Transform carrier, An2Visual vis)
        {
            if (!Load()) return false;
            if (carrier.Find("NDR_An2") != null) return true;
            GameObject root = new GameObject("NDR_An2");
            root.transform.SetParent(carrier, false);
            root.transform.localScale = Vector3.one * Scale;
            Part(root.transform, "Body", _body, _skin);
            if (_glass != null) Part(root.transform, "Glass", _glass, _clear);
            vis.Attach(root.transform,
                       Hinge(root.transform, "prop", _prop),
                       Hinge(root.transform, "aileron_l", _ailL),
                       Hinge(root.transform, "aileron_r", _ailR),
                       Hinge(root.transform, "elevator", _elev),
                       Hinge(root.transform, "rudder", _rud));
            return true;
        }
    }

    /// <summary>
    /// One An-2 on one client: engine spool, propeller, control surfaces,
    /// engine sound and the fuel in its tanks. Every client runs it for every
    /// An-2; the pilot's client writes the inputs, the others take them from
    /// the pose messages.
    /// </summary>
    public sealed class An2Visual : MonoBehaviour
    {
        public bool Running;
        public float Fuel;
        public float Throttle, Elevator, Aileron, Rudder;   // -1..1 (throttle 0..1)
        float _power, _spin, _e, _a, _r;
        bool _dead;
        Transform _root, _prop, _ailL, _ailR, _elev, _rud;
        Vector3 _axProp, _axAilL, _axAilR, _axElev, _axRud;
        AudioSource _audio;
        static AudioClip _clip;
        float _hideUntil = -1f;

        /// <summary>The carrier's own Mi-8 renderers stay off: Prepare runs
        /// before HelicopterDummy.Start, so for the first seconds anything the
        /// prefab switches on by itself is switched off again. The An-2 model
        /// and the wreck fire (both named NDR_...) are left alone.</summary>
        void HideCarrier()
        {
            if (_hideUntil < 0f) _hideUntil = Time.time + 3f;
            if (Time.time > _hideUntil) return;
            Renderer[] all = GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i].transform;
                if (_root != null && t.IsChildOf(_root)) continue;
                bool ours = false;
                for (Transform u = t; u != null && u != transform; u = u.parent)
                    if (u.name.StartsWith("NDR")) { ours = true; break; }
                if (!ours) all[i].enabled = false;
            }
        }

        public float Power { get { return _power; } }

        /// <summary>A broken An-2 (Revival.An2Repair.cs) has no propeller.</summary>
        internal void ShowProp(bool on)
        {
            if (_prop != null && _prop.gameObject.activeSelf != on) _prop.gameObject.SetActive(on);
        }

        internal void Attach(Transform root, Transform prop, Transform ailL, Transform ailR,
                             Transform elev, Transform rud)
        {
            _root = root; _prop = prop; _ailL = ailL; _ailR = ailR; _elev = elev; _rud = rud;
            _axProp = An2Model.AxisOf("prop");
            _axAilL = An2Model.AxisOf("aileron_l");
            _axAilR = An2Model.AxisOf("aileron_r");
            _axElev = An2Model.AxisOf("elevator");
            _axRud = An2Model.AxisOf("rudder");
        }

        public void Kill()
        {
            _dead = true;
            Running = false;
            _power = 0f;
            if (_audio != null) { try { _audio.Stop(); } catch { } }
            if (_root == null) return;
            // Scorched: the skin burnt dark, the glass gone.
            Renderer[] rs = _root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i].gameObject.name == "Glass") { rs[i].enabled = false; continue; }
                Material m = rs[i].material;
                m.color = new Color(0.20f, 0.18f, 0.16f, 1f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (!_dead) HideCarrier();
            if (_dead) { _power = 0f; }
            else
            {
                float step = dt / PlayerAn2.EngineSeconds();
                bool on = Running && Fuel > 0f;
                _power = Mathf.Clamp01(_power + (on ? step : -step * 1.5f));
            }
            if (_root == null) return;
            try
            {
                // Idle at 25 % of full rpm, full rpm at full throttle.
                float rpm = _power * (0.25f + 0.75f * Throttle);
                _spin += rpm * 1650f * dt;                         // deg/s at full rpm (visible blur)
                if (_spin > 360f) _spin -= 360f * Mathf.Floor(_spin / 360f);
                _prop.localRotation = Quaternion.AngleAxis(_spin, _axProp);
                float k = Mathf.Min(1f, 8f * dt);
                _e = Mathf.Lerp(_e, Elevator, k);
                _a = Mathf.Lerp(_a, Aileron, k);
                _r = Mathf.Lerp(_r, Rudder, k);
                // Senses from the manifest: + is trailing edge up (ailerons,
                // elevator), + is trailing edge to port (rudder).
                _elev.localRotation = Quaternion.AngleAxis(_e * 18f, _axElev);
                _ailR.localRotation = Quaternion.AngleAxis(_a * 18f, _axAilR);
                _ailL.localRotation = Quaternion.AngleAxis(-_a * 18f, _axAilL);
                _rud.localRotation = Quaternion.AngleAxis(-_r * 22f, _axRud);
                Sound(rpm);
            }
            catch { }
        }

        void Sound(float rpm)
        {
            if (_dead) return;
            if (_audio == null)
            {
                if (rpm <= 0.001f) return;
                _audio = _root.gameObject.AddComponent<AudioSource>();
                _audio.clip = Clip();
                _audio.loop = true;
                _audio.spatialBlend = 1f;
                _audio.minDistance = 30f * PlayerAn2.K;
                _audio.maxDistance = 1400f * PlayerAn2.K;
                _audio.rolloffMode = AudioRolloffMode.Logarithmic;
                _audio.dopplerLevel = 0.3f;
            }
            if (rpm <= 0.001f)
            {
                if (_audio.isPlaying) _audio.Stop();
                return;
            }
            _audio.volume = Mathf.Clamp01(0.25f + 0.75f * rpm) * Mathf.Clamp01(_power * 3f);
            _audio.pitch = 0.55f + 1.0f * rpm;
            if (!_audio.isPlaying) _audio.Play();
        }

        /// <summary>A nine-cylinder radial, synthesized: one second holding a
        /// whole number of firing pulses (75 per second at the recorded
        /// speed), each a short damped knock with its own strength, over a
        /// low rumble - so the loop has no seam and pitch carries the rpm.</summary>
        static AudioClip Clip()
        {
            if (_clip != null) return _clip;
            const int rate = 22050;
            const int pulses = 75;
            float[] data = new float[rate];
            System.Random rnd = new System.Random(2027);
            float[] strength = new float[pulses];
            for (int i = 0; i < pulses; i++) strength[i] = 0.65f + 0.35f * (float)rnd.NextDouble();
            float period = 1f / pulses;
            float low = 0f;
            for (int i = 0; i < rate; i++)
            {
                float t = (float)i / rate;
                int n = (int)(t / period);
                float u = t - n * period;
                float knock = strength[n % pulses] * Mathf.Exp(-u / 0.0032f)
                            * (Mathf.Sin(2f * Mathf.PI * 155f * u) + 0.5f * Mathf.Sin(2f * Mathf.PI * 410f * u));
                float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
                low += (noise - low) * 0.05f;
                float hum = Mathf.Sin(2f * Mathf.PI * 75f * t) * 0.35f + Mathf.Sin(2f * Mathf.PI * 37.5f * t) * 0.2f;
                data[i] = Mathf.Clamp(knock * 0.55f + hum * 0.45f + low * 0.9f, -1f, 1f) * 0.8f;
            }
            _clip = AudioClip.Create("NDR_An2Engine", rate, 1, rate, false);
            _clip.SetData(data, 0);
            return _clip;
        }
    }

    /// <summary>
    /// The descent of an An-2 nobody flies: it keeps its speed, the wing lifts
    /// less and less as the nose drops and one wing goes, and it spirals into
    /// the ground. Arithmetic, like HeliCrashFall; started on every client from
    /// the same pose by the reliable Crashed event.
    /// </summary>
    public sealed class An2Glide : MonoBehaviour
    {
        Vector3 _vel;
        Quaternion _rot;
        float _t;
        float _side;

        public void Begin(Vector3 vel, Quaternion rot)
        {
            _vel = vel;
            _rot = rot;
            _t = 0f;
            _side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;
            _t += dt;
            float speed = _vel.magnitude;
            float carry = Mathf.Clamp01(1f - _t / 5f) * Mathf.Clamp01(speed / 25f);
            _vel += new Vector3(0f, -9.81f * (1f - 0.85f * carry), 0f) * dt;
            _vel -= _vel * Mathf.Min(1f, 0.05f * dt);
            if (speed > 60f) _vel = _vel.normalized * 60f;
            // Nose down and a wing dropping, faster as it goes.
            _rot = _rot * Quaternion.Euler(Mathf.Min(25f, 6f + _t * 4f) * dt, 0f, -_side * Mathf.Min(40f, 10f + _t * 8f) * dt);
            Vector3 pos = transform.position + _vel * (PlayerAn2.K * dt);
            float floor;
            bool solid;
            PlayerAn2.GlideFloor(pos, out floor, out solid);
            transform.rotation = _rot;
            if (solid && pos.y <= floor)
            {
                pos.y = floor;
                transform.position = pos;
                enabled = false;
                PlayerAn2.FinishGlide(gameObject, pos);
                return;
            }
            transform.position = pos;
            if (_t > 120f) { enabled = false; PlayerAn2.FinishGlide(gameObject, pos); }
        }
    }
}

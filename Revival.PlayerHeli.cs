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

        internal static ConfigEntry<bool> CfgEnabled, CfgPassengers, CfgBailOut;
        internal static ConfigEntry<string> CfgSpawnKey, CfgBoardKey, CfgViewKey;
        internal static ConfigEntry<float> CfgSize, CfgThrust, CfgSideThrust,
            CfgLift, CfgDrag, CfgMaxSpeed, CfgSensitivity, CfgCamDistance,
            CfgCamHeight, CfgFov, CfgNetHz, CfgBoardRange, CfgSeatSide,
            CfgSeatUp, CfgSeatForward;
        internal static ConfigEntry<int> CfgEventCode, CfgMaxHelis;

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
            CfgSize = cfg.Bind("PlayerHeli", "Size", 1f,
                "Size relative to the game's aid Mi-8 (0.5..2). 1 is the "
                + "original, which matches players and vehicles.");
            CfgMaxSpeed = cfg.Bind("PlayerHeli", "MaxSpeed", 50f,
                "Top speed in metres per second, real scale (50 m/s = 180 km/h). "
                + "Everything here is metric and multiplied by the world's 2.8.");
            CfgThrust = cfg.Bind("PlayerHeli", "Thrust", 14f,
                "Forward and backward acceleration (W/S) in m/s^2. Together with "
                + "Drag this sets the cruising speed: Thrust/Drag.");
            CfgSideThrust = cfg.Bind("PlayerHeli", "SideThrust", 8f,
                "Sideways acceleration (A/D) in m/s^2.");
            CfgLift = cfg.Bind("PlayerHeli", "Lift", 7f,
                "Climb and sink rate change in m/s^2 (space and left control). "
                + "With no collective input the machine holds its height by "
                + "itself - that is what a helicopter does.");
            CfgDrag = cfg.Bind("PlayerHeli", "Drag", 0.45f,
                "Air resistance per second, proportional to speed. Larger is "
                + "more sluggish and more stable.");
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
                "Photon event code (0..194); this and the three following codes "
                + "are used. Must be the same on every client.");
            CfgMaxHelis = cfg.Bind("PlayerHeli", "MaxHelicopters", 4,
                "How many player helicopters may stand in the world at once. "
                + "Beyond that the oldest empty one is removed.");
            CfgBoardRange = cfg.Bind("PlayerHeli", "BoardRange", 12f,
                "How far from the machine you may get in, in metres.");
            CfgPassengers = cfg.Bind("PlayerHeli", "Passengers", true,
                "Let other players ride along in the cabin.");
            CfgBailOut = cfg.Bind("PlayerHeli", "BailOut", true,
                "Shift and the board key jump out in flight. Without it you can "
                + "only get out on the ground.");
            CfgSeatSide = cfg.Bind("PlayerHeli", "SeatSide", 0.9f,
                "Pilot's seat across the fuselage, in metres from the model "
                + "origin. The gear footprint's centre line is 0.9.");
            CfgSeatUp = cfg.Bind("PlayerHeli", "SeatUp", 1.2f,
                "Cabin floor over the gear, in metres.");
            CfgSeatForward = cfg.Bind("PlayerHeli", "SeatForward", 5f,
                "Pilot's seat forward of the model origin, in metres.");
        }

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
        static float _boardedAt, _nextPose, _nextHeartbeat;
        static Transform _body;              // the local player's own transform
        static bool _hadBody;                // his body was found at least once
        static string _hint = "";
        static float _hintUntil;
        static bool _poseWarned, _bodyWarned, _interpWarned;

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

        static KeyCode _spawnKey, _boardKey, _viewKey;
        static bool _keysParsed;

        internal static bool Aboard { get { return _heli != null; } }
        internal static bool Flying { get { return _heli != null && _pilot; } }

        // =============================================================== frame

        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                Net.EnsureHooked();
                Sweep();

                if (_heli == null)
                {
                    if (Input.GetKeyDown(SpawnKey())) SpawnOrRemove();
                    else if (Input.GetKeyDown(BoardKey())) Board();
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
                    Leave(false);
                    return;
                }

                if (Input.GetKeyDown(ViewKey())) _cockpit = !_cockpit;
                if (Input.GetKeyDown(BoardKey())) { Leave(true); return; }

                if (_pilot)
                {
                    Steer();
                    Fly();
                }
                Heartbeat();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerHeli: " + ex);
                Leave(false);
            }
        }

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
            if (!Enabled || _heli == null) return;
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
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("PlayerHeli camera: " + ex);
                Leave(false);
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
        /// the configured maximum. An occupied one is never taken away.</summary>
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
                    if (Busy(ViewId(go))) continue;
                    oldest = go;
                    break;
                }
                if (oldest == null) return;
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
                _onGround = true;
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

            if (byKey && go != null && (_pilot ? !_onGround : Airborne(go)))
            {
                bool bail = CfgBailOut != null && CfgBailOut.Value
                            && (Input.GetKey(KeyCode.LeftShift)
                                || Input.GetKey(KeyCode.RightShift));
                if (!bail)
                {
                    Hint(Text.LandFirst(), 4f);
                    return;
                }
            }

            int view = ViewId(go);
            try
            {
                if (go != null)
                {
                    if (_pilot && !RevivalTroopInsertion.MasterClient())
                        Interpolator(go, true);
                    if (view != 0) Net.Send(Net.Aboard, new float[] { view, 0f, 0f }, true);
                    Ground(go);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("PlayerHeli leave: " + ex.Message); }
            finally
            {
                if (_pilot) CameraOwner.Release(CameraOwner.Heli);
                Reset();
            }
            RevivalPlugin.L.LogInfo("PlayerHeli: left helicopter " + view + ".");
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

        static void Steer()
        {
            float sens = CfgSensitivity == null ? 2.2f : CfgSensitivity.Value;
            _yaw += Input.GetAxis("Mouse X") * sens;
            if (_yaw > 180f) _yaw -= 360f;
            if (_yaw < -180f) _yaw += 360f;
            _look = Mathf.Clamp(_look - Input.GetAxis("Mouse Y") * sens, -60f, 70f);
        }

        /// <summary>
        /// One frame of flight. Two rules make it a helicopter rather than a
        /// drone: the collective HOLDS the height when nothing is pressed, and
        /// the ground is a floor, not a wall - a machine flown into a hillside
        /// rides up over it instead of exploding, because a crash model this
        /// feature cannot show fairly on every client is worse than none.
        /// </summary>
        static void Fly()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (dt <= 0f) return;
            float k = K;
            Transform tr = _heli.transform;
            Vector3 pos = tr.position;

            Quaternion flat = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 fwd = flat * Vector3.forward;
            Vector3 right = flat * Vector3.right;

            float thrust = (CfgThrust == null ? 14f : CfgThrust.Value) * k;
            float side = (CfgSideThrust == null ? 8f : CfgSideThrust.Value) * k;
            float lift = (CfgLift == null ? 7f : CfgLift.Value) * k;
            float drag = CfgDrag == null ? 0.45f : Mathf.Max(0.02f, CfgDrag.Value);
            float max = (CfgMaxSpeed == null ? 50f : CfgMaxSpeed.Value) * k;

            Vector3 accel = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) accel += fwd * thrust;
            if (Input.GetKey(KeyCode.S)) accel -= fwd * thrust;
            if (Input.GetKey(KeyCode.D)) accel += right * side;
            if (Input.GetKey(KeyCode.A)) accel -= right * side;

            bool up = Input.GetKey(KeyCode.Space);
            bool down = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
            if (up) accel += Vector3.up * lift;
            else if (down) accel -= Vector3.up * lift;

            _vel += accel * dt;

            // Horizontal drag is the pilot's brake; vertical damping is the
            // collective holding the disc. Both are computed over dt, never as a
            // per-frame factor - a fast machine would otherwise fly differently
            // on a fast computer.
            Vector3 horizontal = new Vector3(_vel.x, 0f, _vel.z);
            horizontal -= horizontal * Mathf.Min(1f, drag * dt);
            float vertical = _vel.y;
            if (!up && !down) vertical -= vertical * Mathf.Min(1f, 1.6f * dt);
            _vel = new Vector3(horizontal.x, vertical, horizontal.z);
            if (_vel.magnitude > max) _vel = _vel.normalized * max;

            pos += _vel * dt;

            // The floor: terrain height, and the deck of a helipad when the
            // machine stands over one. A downward ray is deliberately NOT used -
            // it would find the helicopter's own hull collider, which is exactly
            // why the scripted troop flight has none either.
            float floor;
            if (!Floor(pos, out floor)) floor = pos.y;
            _onGround = pos.y <= floor + 0.4f * k;
            if (pos.y < floor)
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

            // Attitude is drawn from the speed, not from a key: a helicopter
            // that moves forward has its nose down, one that drifts sideways
            // hangs into the drift.
            Vector3 local = Quaternion.Inverse(flat) * _vel;
            float wantNose = Mathf.Clamp(local.z / k * 0.35f, -14f, 14f);
            float wantBank = Mathf.Clamp(local.x / k * 0.45f, -22f, 22f);
            if (_onGround) { wantNose = 0f; wantBank = 0f; }
            _nose = Mathf.Lerp(_nose, wantNose, Mathf.Min(1f, 2.5f * dt));
            _bank = Mathf.Lerp(_bank, wantBank, Mathf.Min(1f, 2.5f * dt));

            tr.position = pos;
            tr.rotation = Quaternion.Euler(_nose, _yaw, -_bank);

            Pose(pos);
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

                int view = ViewId(go);
                if (view != 0) _byView[view] = go;
                RevivalPlugin.L.LogInfo("PlayerHeli: helicopter " + view
                    + " prepared, scale " + s.ToString("0.00") + ".");
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
            if (_heli == null && (_pilot || _hadBody)) Leave(false);

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
                if (go == null) continue;
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
                if (Time.time - _boardedAt < 14f)
                    Line(Text.Controls(), cx, cy + 176f,
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
        /// Four messages, all of them small. base+0 asks the master for a
        /// machine, base+1 is the pose of a pilot who is not the master, base+2
        /// says who is aboard which machine, base+3 asks the master to take one
        /// away. Everything else - the machine itself, its position for everyone
        /// who is not flying it - is the prefab's own Photon replication.
        /// </summary>
        internal static class Net
        {
            internal const int SpawnRequest = 0;
            internal const int PoseUpdate = 1;
            internal const int Aboard = 2;
            internal const int RemoveRequest = 3;

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
                        + Base() + "-" + (Base() + 3) + ".");
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
                    if (kind < 0 || kind > 3) return;
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

            internal static string Seated()
            {
                return Loc.T("Ты в грузовой кабине", "You are in the cabin");
            }

            internal static string LandFirst()
            {
                return Loc.T("Сначала посади машину (Shift - прыжок)",
                             "Land first (shift to jump out)");
            }

            internal static string Readout(int kmh, int metres, bool onGround)
            {
                string state = onGround
                    ? Loc.T("на земле", "on the ground")
                    : (metres + Loc.T(" м", " m"));
                return kmh + Loc.T(" км/ч", " km/h") + "   " + state;
            }

            internal static string Controls()
            {
                return Loc.T(
                    "W/S/A/D - движение, мышь - курс, пробел/Ctrl - высота, V - вид, F - выйти",
                    "W/S/A/D move, mouse steers, space/ctrl climb and sink, V view, F out");
            }
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
}

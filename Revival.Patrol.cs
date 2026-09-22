using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{

    // ------------------------------------------------------ NPC vehicle patrols

    /// <summary>
    /// Vehicles that drive the road on their own.
    ///
    /// This is **phase 2** of `docs/ai/tasks/npc-vehicle-patrols.md`: the
    /// driver, and nothing else. One vehicle, one route, no gun, no convoy,
    /// no loot. Its acceptance needs no eyes - switch it on, let it run ten
    /// minutes, and read the lap counter out of `BepInEx\LogOutput.log`.
    ///
    /// HOW A VEHICLE WITH NOBODY IN IT DRIVES (REVERSE_ENGINEERING.md 20)
    ///
    ///   The game's own way in is closed. `VehicleGameSystem::InputAxis`
    ///   returns at once while `_playersCount &lt;= 0`, and it would not help
    ///   anyway: it writes `VerticalAxis` / `HorizontalAxis`, which are only
    ///   the wish. `RCCCarControllerV2::KeyboardControlling` - which reads no
    ///   keyboard - turns that wish into `gasInput` by lerping towards it at
    ///   10 per second. Write `gasInput` yourself and that lerp erases it.
    ///
    ///   The door is one bool. `RCCCarControllerV2::Update` skips
    ///   `KeyboardControlling` when `AIController` is true, and skips the
    ///   branch that zeroes the inputs when `canControl` is false as well.
    ///   So: set `AIController = true`, and the four input fields are ours
    ///   alone. `canControl` gates nothing we need - `Engine`, `Braking` and
    ///   `ApplySteering` sit after that gate in `FixedUpdate`.
    ///
    ///   What DOES stop the vehicle is sleep mode, and it is two paths, not
    ///   one. The timer puts an empty vehicle to sleep 25 s after it is left
    ///   alone; `ForceSleepModeController` disables its physics after about
    ///   ONE second of standing still with an empty driver's seat - which is
    ///   what a patrol looks like in the moment it spawns. Both end in
    ///   `DisablePhys`, which sets `RCCCarControllerV2.IsMine = false`, and
    ///   RCC's `FixedUpdate` returns on its first line when that is false.
    ///
    ///   Faking `_playersCount` is the obvious idea and it is wrong:
    ///   `RefreshPlayersCount` recomputes it from the seats, and while it
    ///   holds, `VehicleGameSystem::Update` runs the HOST's own
    ///   `KeyboardControlling` on the patrol vehicle - his keys would drive
    ///   it and his engine key would switch it off. So the sleep is stopped
    ///   with two Harmony prefixes instead, and only for vehicles this class
    ///   owns. Every other vehicle in the world keeps its sleep mode.
    ///
    /// REVERSE IS THE BRAKE. `RCCCarControllerV2::GearBox` shifts into
    /// reverse when `brakeInput > 0.1` while the local forward velocity is
    /// under 1 m/s. With `autoReverse` on, `canGoReverseNow` is always true,
    /// so **any** braking below walking pace flips the vehicle into reverse.
    /// That is why <see cref="Drive"/> coasts below <see cref="CoastBelow"/>
    /// km/h. Recovery never reverses: after a short confirmed stop it moves
    /// the vehicle forward along the route.
    ///
    /// UNTESTED: all of it. Nothing in this class has been seen in the game.
    /// </summary>
    public static class Patrol
    {
        /// <summary>Below this speed the driver coasts instead of braking,
        /// because braking here means reverse gear. See the class comment.</summary>
        const float CoastBelow = 8f;

        /// <summary>Steering angle in degrees that means full lock.</summary>
        const float FullLockAt = 25f;

        /// <summary>How far ahead of the hull the obstacle rays start. The
        /// BTR-80A is about 7.6 m long, so 4.2 m clears its own collider.</summary>
        const float NoseOffset = 4.2f;

        // ------------------------------------------------------- the ground guard
        //
        //  A vehicle may only be put where there is something to stand on, and a
        //  vehicle that is falling must never be treated as one that is merely
        //  stuck. See GroundGuard for why the two together produced a hull that
        //  appeared, dropped through the world and appeared above it again for
        //  the rest of the session.

        /// <summary>Metres a hull may sit below the surface the ground lookup
        /// finds above it before it counts as buried rather than driving. A hull
        /// stands about a metre under its own origin, and a wheel in a rut is
        /// not a bug.</summary>
        const float BuriedBy = 3f;

        /// <summary>Metres per second downwards that make "no ground under the
        /// hull" a fall. Without it a vehicle resting on a surface the lookup
        /// does not recognise would be picked up and moved for no reason.</summary>
        const float FallSpeed = 1.5f;

        /// <summary>Seconds a hull may be falling before it is put back on the
        /// road. Long enough for a jump, a ditch and one physics hiccup.</summary>
        const float FallSeconds = 2f;

        /// <summary>Seconds between two ground lookups for one vehicle. The
        /// lookup is up to five casts and runs per vehicle, so it does not
        /// belong in the physics step - the fall it looks for lasts seconds.</summary>
        const float GroundEvery = 0.25f;

        /// <summary>Recoveries inside <see cref="FallForget"/> before the
        /// vehicle is given up instead of being put back again. Giving up starts
        /// the ordinary replacement wait; putting it back again forever is the
        /// bug.</summary>
        const int FallRecoveries = 3;

        /// <summary>Seconds of ordinary driving that clear the recovery
        /// count.</summary>
        const float FallForget = 60f;

        /// <summary>Waypoints a placement walks forward looking for one with
        /// ground under it before it gives up on the route.</summary>
        const int GroundTries = 12;

        /// <summary>
        /// Metres of recorded line that same walk covers before it gives up, and
        /// the hard cap on the waypoints it may probe to get there.
        ///
        /// WHY THIS EXISTS. A COUNT of waypoints is the wrong budget, because
        /// the recordings differ by a factor of four in density. Measured on the
        /// live snapshot of 2026-09-21: the looter route averages 11.5 m per
        /// waypoint, the civilian and traitor routes 3.7 m and 3.3 m. Twelve
        /// waypoints are therefore 138 m of the looter's road and 44 m of the
        /// civilians' - and 44 m is not enough to get past the hull a vehicle is
        /// standing nose to nose with, which is why the looter patrol recovered
        /// from every stop in that session's log and the civilian patrol logged
        /// "no waypoint ... has ground under it" every ten seconds for the whole
        /// session without ever moving again.
        ///
        /// Metres give every route the same second chance. The waypoint cap
        /// keeps the cost bounded on a dense recording: each probe that finds
        /// nothing is a handful of raycasts, and this runs at most once every
        /// ten seconds per stopped vehicle.
        /// </summary>
        const float GroundReach = 150f;
        const int GroundWalk = 48;

        // ------------------------------------------------------------- the file

        class Point
        {
            public Vector3 Pos;
            public float Speed;          // km/h on the leg starting here, 0 = config
            public string Flags;
        }

        /// <summary>
        /// One route, and everything about the patrol that drives it.
        ///
        /// WHERE THE SETTINGS LIVE. In the FLAGS of waypoint 0, as
        /// `spawn,fraction=looter,vehicle=btr,count=2`. Not in a second file
        /// and not in a new column: the recorder writes this file from inside
        /// the game, `routecheck.py` reads it, `build.ps1` refuses to
        /// overwrite it, and every one of those three would have needed
        /// teaching. A flag on the first waypoint needed none of them - the
        /// loader already splits flags on commas and the checker already
        /// ignores what it does not know.
        /// </summary>
        class Route
        {
            public string Name;
            public List<Point> P = new List<Point>();

            /// <summary>civilian, looter, traitor or neutral - see
            /// <see cref="Fraktion"/>. Empty means the one in the config.</summary>
            public string Fraction = "";

            /// <summary>btr, tank or mixed. Empty means the one in the
            /// config.</summary>
            public string Vehicle = "";

            /// <summary>How many patrols this route should carry. The
            /// automatic keeps that many alive on it. Its composition contributes
            /// that many groups of vehicles to the global patrol capacity.</summary>
            public int Count = 1;

            /// <summary>Does the automatic use this route at all? A route
            /// being recorded, or one whose waypoints are wrong, is switched
            /// off without being deleted.</summary>
            public bool Enabled = true;

            /// <summary>
            /// WHICH MAP this route lies on, as the scene name (`scene=` in the
            /// first waypoint's flags). Empty means the home map - every route
            /// recorded before the game's other regions were reachable was
            /// recorded there. The route is drawn and driven only while that map
            /// is loaded; see MapScene and Revival.MapScene.cs for why a route
            /// of one region used to show up on the map of another.
            /// </summary>
            public string Scene = "";

            /// <summary>Is this route on the map that is open right now?</summary>
            public bool Here { get { return MapScene.Owns(Scene); } }

            public string Seite
            {
                get
                {
                    string f = Fraktion.Sauber(Fraction);
                    if (f.Length > 0) return f;
                    f = Fraktion.Sauber(RevivalPlugin.CfgPatrolFraction.Value);
                    return f.Length > 0 ? f : "neutral";
                }
            }

            public string Wagen
            {
                get
                {
                    string v = Vehicle == null ? "" : Vehicle.Trim().ToLowerInvariant();
                    if (v == "mixed" || VehicleRegistry.Contains(v)) return v;
                    v = RevivalPlugin.CfgPatrolVehicle.Value;
                    if (v == null) return "mixed";
                    v = v.Trim().ToLowerInvariant();
                    return VehicleRegistry.Contains(v) ? v : "mixed";
                }
            }

            // Cached map LINE in WORLD space (XZ; y is 0 and ignored by
            // WorldToGui): the smoothed centreline of the road this route
            // drives. Built once from the waypoints and only PROJECTED each
            // frame, so it does not jitter as the map or camera micro-moves.
            // Rebuilt only when the waypoint count changes. MapLineLoop
            // remembers whether the route returns to its start, so the dasher
            // can tile it as a closed ring instead of an open line.
            internal List<Vector3> MapLine;
            internal int MapLineN = -1;
            internal bool MapLineLoop;

            // What the map overlay's last BUILD worked out for this route (see
            // Patrol.DrawMap). MapProj is the projected line kept relative to
            // the map picture's own screen origin, which makes it independent
            // of where the map is scrolled; MapBounds is its extent plus the
            // hover slack, so the cursor test can skip a route in one compare.
            // MapAnchor is the projected first waypoint, the fallback hover
            // spot for the route's name. All of it survives until the overlay's
            // layout signature changes, so an unchanged map costs no
            // projection, no clearance pass and no placement search at all.
            internal List<Vector2> MapProj;
            internal Rect MapBounds;
            internal Vector2 MapAnchor;
            internal bool MapInked;
            internal bool MapNamed;

            // NDR convoy (RevivalConvoy.cs): "convoy" marks a route the convoy
            // event drives as a column of tanks and APCs. Empty/"patrol" is an
            // ordinary auto-patrol route. Stored as kind= in the first
            // waypoint's flags; the auto-patrol picker (Duenn) skips convoy
            // routes so a convoy road never also spawns lone patrols.
            internal string Kind = "";
            internal bool IsConvoy { get { return Kind == "convoy"; } }

            // NDR convoy column (RevivalConvoy.cs). Arc length of the recorded
            // line: Cum[i] is the flat distance from waypoint 0 to waypoint i,
            // Length the whole line. A convoy column is positioned in METRES
            // along this, not in waypoint indices, so the spacing between two
            // vehicles is exact no matter how the waypoints are distributed.
            // Built once per route object by Metrics(), invalidated by setting
            // Cum to null after an edit.
            internal float[] Cum;
            internal float Length;
        }

        static Dictionary<string, Route> _routes = new Dictionary<string, Route>();
        static List<string> _order = new List<string>();
        static bool _loaded;

        // ------------------------------------------------------------ one patrol

        class Unit
        {
            public GameObject Car;
            public Component Vgs;        // VehicleGameSystem
            public Component Rcc;        // RCCCarControllerV2
            public object Body;          // UnityEngine.Rigidbody, by reflection
            public Route Route;
            public int Next;             // waypoint being driven to
            public int Lap;
            public bool Armed;
            public float Wait;           // seconds waited for IsInitialized
            public float Stuck;          // seconds below walking speed
            public int Frees;
            public int Reported;         // last lap written to the log

            // Refusals to warp in a row (FreeHold), cleared by the first warp
            // that happens (Free). A single refusal is a sensible answer; the
            // same refusal over and over is a vehicle that has been parked for
            // the rest of the session, which is what the civilian patrol's pair
            // did. See HoldsBeforeForce.
            public int Refusals;

            // Ground guard (GroundGuard, Supported, Recover). A hull with
            // nothing under it is not driving, it is falling - and falling is
            // vertical, so the stuck timer above reads it as standing still and
            // the FREE warp fires into the same hole every StuckSeconds.
            // Airborne is the last answer of the 4 Hz lookup, FallSince the
            // Time.time it was first true, NextGround when to look again,
            // Recoveries how often this vehicle was put back on the road and
            // RecoverAt when that last happened (the count is forgotten after a
            // minute of ordinary driving). FallLog rate-limits the report.
            public bool Airborne;
            public float FallSince;
            public float NextGround;
            public int Recoveries;
            public float RecoverAt;
            public float FallLog;

            // ---------------------------------------------------- the gun
            public bool Tank;            // which of the two value profiles
            public Transform[] Turrets = new Transform[0];
            public Renderer TurretRend;  // for the muzzle, see Muendung
            public Quaternion GunWorldRotation;
            public bool GunStabilized;
            public float Yaw, Pitch;     // where the barrel is being sent
            public Transform Target;     // hostile player, NPC or occupied vehicle
            public CombatTarget GunTarget;
            public float Held;           // seconds this target has been held
            public float Lost;           // seconds since it was last seen
            public float NextShot;       // Time.time the gun may fire again
            public float NextLook;       // Time.time of the next target scan
            public float NextNet;        // Time.time of the next turret-angle network readout
            public int Burst;            // shots fired in the running burst
            public int Shots, Hits;

            // --------------------------------------------------- the crew
            public string Seite;         // which side climbs out of the wreck
            public int CrewSize;         // men aboard, one per seat
            public bool CrewOut;         // they have climbed out
            public bool Truck;
            public bool Deploy;
            public Vector3 DeployTarget;

            // NDR convoy break-up (RevivalConvoy.Behaviour). DeployPosted marks
            // that a REAL firing position was found - without it DeployTarget is
            // just "where I happen to stand", and a vehicle that never found a
            // shoulder has to be asked again (DeployRetry) instead of standing in
            // the column line for the rest of the event. DeployAnchor is where the
            // formation broke and bounds how far the defender may press forward.
            // DeployTries counts the positions this vehicle failed to reach and
            // turns the search ring by one notch each time: the candidates are
            // fixed geometry, so without it a vehicle stopped by a rock would be
            // handed the same unreachable shoulder every four seconds forever.
            public bool DeployPosted;
            public float DeployRetry;
            public int DeployTries;
            public Vector3 DeployAnchor;

            // Where the attack came from, remembered per vehicle and shared
            // across the convoy (ConvoyThreat). The gun clears Target as soon as
            // it loses sight; the survivors still have to know which way to face.
            public Vector3 Threat;
            public float ThreatAt;

            // Seconds this vehicle has been braking for a BURNING convoy mate,
            // and the latched point it is steering to in order to get past one.
            // A wreck never moves again, so without these the survivor behind it
            // queues into it for the rest of the event.
            public float Blocked;
            public Vector3 BypassPoint;
            public float BypassUntil;

            // Seconds an ORDINARY patrol has been queueing behind a mate of its
            // own composition (QueueBehind). Its own field, not Blocked: the
            // convoy brake above clears Blocked on every step in which it does
            // not fire, which for a patrol is every step there is.
            public float Queued;

            // Where this vehicle had got to, and when (MadeProgress/NoProgress).
            // The stuck timer above counts seconds under 3 km/h, and two hulls
            // that interpenetrate do not stand still - they SHAKE, because the
            // physics engine pushes them apart every step. The speedometer
            // therefore reads "moving" while the vehicle has not left the spot
            // for minutes, which is how a welded pair held a road for a whole
            // session. Ground covered is the honest measure.
            public Vector3 ProgressPos;
            public float ProgressAt;

            // Time.time this vehicle was first found off its own recorded line,
            // or 0 while it is on it (Leashed). Being off the road is not being
            // stuck - a tank driving through a wood is moving perfectly well -
            // so nothing else in this class would ever bring it back.
            public float OffRouteSince;

            public float Died;           // Time.time the vehicle was killed
            public int CompositionVehicle = -1; // editor vehicle index, or legacy
            public List<RevivalComposition.CrewMan> CrewSnapshot;
            public int PatrolGroupId;    // one configured mini-convoy formation

            // NDR convoy (RevivalConvoy.cs). ConvoyId 0 = ordinary patrol; a
            // non-zero id groups the vehicles of one convoy. Hold stops the
            // vehicle where it stands (the gun keeps scanning and firing) - the
            // convoy layer sets it for spacing and the behaviour agent uses it
            // for hold-and-search. Stocked marks a trunk already given its loot.
            public int ConvoyId;
            public bool Hold;
            public bool Stocked;

            // NDR convoy one-way drive (feature/convoy-oneway-drive). A convoy
            // vehicle does NOT loop or drive out-and-back: it starts lined up at
            // the route's beginning, drives the recorded route ONCE, and vanishes
            // at the last waypoint (Arrived). OneWay marks that; Arrived is set by
            // Advance when the end is reached and consumed by FixedTick.
            public bool OneWay;
            public bool Arrived;
            // Colliders of this car, cached for "ghost through": a convoy AND an
            // ordinary patrol pass through world props and their own mates via
            // Physics.IgnoreCollision, so they never crash, snag, or shove one
            // another off the road. Their own body collider stays live, so
            // bullets still hit and kill them. Ghosted holds the ids already made
            // non-colliding, so each obstacle is handled at most once. Ghosts
            // counts them and GhostLog rate-limits the report: a convoy is one
            // event and can name every prop it goes through, a patrol drives all
            // session and would write a line per tree. GhostSweep is the clock
            // of the hull sweep (GhostAround), the answer to every obstacle the
            // three feeler rays cannot see.
            public Component[] Cols;
            public Dictionary<int, bool> Ghosted;
            public int Ghosts;
            public float GhostLog;
            public float GhostSweep;

            // NDR convoy column (Columns(), ColumnLock). While Column is true
            // this vehicle is NOT driven by RCC at all: it is placed on the
            // route line every physics step at its own slot behind the column
            // head, so the editor order and the spacing hold exactly and no
            // amount of terrain, props or physics can break the formation.
            // Index is the slot (0 = front). Lift is the metres from the hull
            // origin down to the lowest point of the model, measured once, so
            // the vehicle stands ON the road instead of hovering or sinking.
            // Placed marks that the first exact placement has happened; after
            // that the heading is eased instead of snapped. The four
            // measurements and Placed belong to Carry, so the patrol rail below
            // uses exactly the same ones.
            public bool Column;
            public int ColumnIndex = -1;
            public float ColumnLift;
            public float ColumnHalfLength, ColumnHalfWidth;
            public float ColumnGroundLog;
            public bool Placed;

            // The rail (RailOn, RailStep, RailOff). Rail marks a vehicle that is
            // no longer driven but carried along its own recorded line, Arc
            // where it has got to in metres along that line and Speed how fast
            // it is being carried. From is the arc the carry began at and Since
            // the Time.time it began, which together decide when the driver is
            // offered the wheel back. Runs counts the carries close together and
            // At is when the last one started; Forever marks the vehicle that is
            // not handed back any more. See the rail block above RailMetres.
            public bool Rail;
            public float RailArc;
            public float RailSpeed;
            public float RailFrom;
            public float RailSince;
            public float RailAt;
            public int RailRuns;
            public bool RailForever;
        }

        static List<Unit> _units = new List<Unit>();
        static int _nextPatrolGroupId = 1;

        // --------------------------------------------------- the automatic

        /// <summary>Seconds between two vehicles while the road is being
        /// FILLED. A replacement waits `RespawnSeconds` instead; this is only
        /// so the initial patrols do not all appear in the same second.</summary>
        const float FillEvery = 12f;

        /// <summary>Seconds after the world comes up before the first patrol
        /// goes out. The scene is still settling in the first few seconds -
        /// terrain, colliders, the player's own body - and a vehicle dropped
        /// into that lands on nothing.</summary>
        const float SettleFirst = 25f;

        /// <summary>Metres an automatic patrol keeps away from the player when
        /// it is put down. A patrol appearing at 150 m is a vehicle coming down
        /// the road; one appearing at 30 m is a bug report.</summary>
        const float AutoAway = 150f;

        /// <summary>Is the automatic allowed to act? Shift plus the patrol key
        /// switches it off, the key alone switches it back on - otherwise a
        /// road cleared by hand would refill by itself within seconds.</summary>
        static bool _auto = true;

        /// <summary>Does the automatic still replace losses? `RespawnSeconds`
        /// at 0 fills the road once and never again.</summary>
        static bool _refill = true;

        /// <summary>Was the world up on the last tick? The change from false
        /// to true is what starts the first patrol.</summary>
        static bool _welt;

        /// <summary>Has the teardown below already run for the current outage?
        /// One empty player list must cost the units at most once.</summary>
        static bool _abgeraeumt;

        /// <summary>`Time.time` the automatic may put the next vehicle down.</summary>
        static float _nextAuto;

        /// <summary>Instance ids of the VehicleGameSystem components this class
        /// owns. The sleep prefixes consult it, so it must be filled BEFORE the
        /// vehicle is woken and cleared when it is given up.</summary>
        static Dictionary<int, bool> _owned = new Dictionary<int, bool>();

        // ------------------------------------------------------------ recording

        static bool _recording;
        static Vector3 _lastRecorded;
        static bool _haveLastRecorded;
        static float _nextRecord;

        /// <summary>Metres the recorder must have moved before it writes
        /// another waypoint. The old 3 m rule was retired on the user's word: at
        /// 5 waypoints a second it thinned corners, exactly where a route wants
        /// its points closest. What remains is a hair against a stationary
        /// recorder writing the same point five times a second - a parked
        /// recorder writes nothing; any real driving clears it every frame.</summary>
        const float MinStep = 0.05f;

        // ------------------------------------------------------------------ keys

        static KeyCode _key, _recKey, _autoKey, _editKey;
        static bool _keysParsed;

        // =====================================================================
        //  Harmony: keep our own vehicles out of sleep mode
        // =====================================================================

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol: VehicleGameSystem not found - "
                        + "patrol vehicles would fall asleep, the class stays off.");
                    return;
                }

                MethodInfo disable = AccessTools.Method(t, "DisablePhys", null, null);
                if (disable != null)
                    harmony.Patch(disable,
                        new HarmonyMethod(typeof(Patrol).GetMethod("DisablePhysPrefix")),
                        null, null, null, null);
                else
                    RevivalPlugin.L.LogWarning("Patrol: DisablePhys not found.");

                MethodInfo sleep = AccessTools.Method(t, "SetSleepModeEnabled",
                    new Type[] { typeof(bool) }, null);
                if (sleep != null)
                    harmony.Patch(sleep,
                        new HarmonyMethod(typeof(Patrol).GetMethod("SleepPrefix")),
                        null, null, null, null);
                else
                    RevivalPlugin.L.LogWarning("Patrol: SetSleepModeEnabled not found.");

                RevivalPlugin.L.LogInfo("Patrol: sleep mode suppressed for own vehicles.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol install: " + ex); }
        }

        /// <summary>False = swallow the call. Only for vehicles we own.</summary>
        public static bool DisablePhysPrefix(object __instance)
        {
            return !Owned(__instance);
        }

        /// <summary>Let a wake-up through, swallow a sleep. Sleeping would also
        /// switch the net sync off, and then a second player sees a vehicle
        /// standing where it no longer is.</summary>
        public static bool SleepPrefix(object __instance, bool __0)
        {
            if (!__0) return true;
            return !Owned(__instance);
        }

        static bool Owned(object instance)
        {
            if (_owned.Count == 0) return false;
            UnityEngine.Object o = instance as UnityEngine.Object;
            if (o == null) return false;
            return _owned.ContainsKey(o.GetInstanceID());
        }

        // =====================================================================
        //  Per frame
        // =====================================================================

        public static void Tick()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            try
            {
                ParseKeys();
                if (Input.GetKeyDown(_key))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                        StopAll();
                    else
                        Toggle();
                }
                if (Input.GetKeyDown(_autoKey)) ToggleRecording();
                if (Input.GetKeyDown(_recKey)) RecordHere(true);
                if (_recording) RecordWhileWalking();
                Editor.Tick();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol tick: " + ex); }

            try { Nachschub(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol auto: " + ex); }

            // The gun runs at frame rate, not in the physics step: the turret
            // is turned by RotateTowards and a turret that steps 50 times a
            // second while the picture is drawn 120 times stutters visibly.
            // The driving stays in FixedTick, where it belongs.
            try { Gun.Tick(_units); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol gun: " + ex); }
        }

        /// <summary>The driving itself. Belongs in FixedUpdate: it writes the
        /// same fields RCC reads in ITS FixedUpdate, and a driver running at
        /// frame rate would fight the physics step.</summary>
        /// <summary>The editor window. Belongs in OnGUI, like every other
        /// piece of IMGUI in this plugin.</summary>
        public static void Draw()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            try { Editor.Draw(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol editor: " + ex); }
        }

        /// <summary>Is the editor window open? RevivalPlugin.Update asks,
        /// because the cursor belongs to the window while it is.</summary>
        public static bool EditorOpen
        {
            get { return RevivalPlugin.CfgPatrol.Value && Editor.IsOpen; }
        }

        public static void FixedTick()
        {
            if (!RevivalPlugin.CfgPatrol.Value) return;
            if (_units.Count == 0) return;

            // NDR convoy column: an intact convoy is carried, not driven. This
            // puts every member of every intact column on its exact slot before
            // the driver below runs, and the driver then leaves those vehicles
            // alone. See the column block above ConvoyInColumn.
            try { Columns(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol column: " + ex); }

            try
            {
                for (int i = _units.Count - 1; i >= 0; i--)
                {
                    Unit u = _units[i];
                    if (u.Car == null)
                    {
                        Forget(u);
                        _units.RemoveAt(i);
                        RevivalPlugin.L.LogInfo("Patrol: vehicle on " + u.Route.Name
                            + " is gone after " + u.Lap + " lap(s).");
                        Verloren();
                        continue;
                    }
                    if (!u.Armed) { Arm(u); continue; }
                    if (Gefallen(u)) continue;
                    // A hull with nothing under it is falling, not stuck. It is
                    // put back on the road here, before the driver can read the
                    // fall as "not moving" and warp it into the same hole again.
                    // A carried hull cannot fall - the rail puts it on the
                    // surface every step - and two things moving one vehicle is
                    // the fault this whole file keeps running into.
                    if (!u.Rail && GroundGuard(u)) continue;
                    if (u.OneWay && !u.Column && u.Next == u.Route.P.Count - 1)
                        Advance(u, u.Car.transform.position);
                    // NDR convoy one-way: a convoy that has driven its whole
                    // recorded route to the last waypoint vanishes there.
                    if (u.Arrived) { ArriveEnd(u, i); continue; }
                    Keep(u);
                    // NDR convoy column: Columns() has already put this vehicle
                    // where it belongs. It is not driven and it is not held.
                    if (u.Column) continue;
                    // NDR convoy break-up: the members of one convoy ignore each
                    // other's colliders, so once the column is gone nothing
                    // physical keeps two hulls apart. This does.
                    ConvoySeparate(u);
                    // The vehicles of one patrol composition ignore each other's
                    // colliders too, and had nothing that did this for them -
                    // which is how a tank came to stand inside its own APC.
                    PatrolSeparate(u);
                    if (u.Deploy) { DeployStep(u); continue; }
                    if (u.Hold) { HoldStill(u); continue; }   // NDR convoy: spacing / hold-and-search
                    // The rail: this one proved it cannot drive the road it is
                    // on, so it is carried along it instead. Not driven, not
                    // held, and not steered - see the rail block above
                    // RailMetres.
                    if (u.Rail) { RailStep(u, Time.fixedDeltaTime); continue; }
                    Drive(u);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol drive: " + ex); }
        }

        // =====================================================================
        //  Start and stop
        // =====================================================================

        static void Toggle()
        {
            // The key is also the way back: whoever presses it wants patrols,
            // so the automatic that Shift switched off is switched on again.
            if (!_auto)
            {
                _auto = true;
                _refill = true;
                _nextAuto = Time.time + FillEvery;
                RevivalPlugin.L.LogInfo("Patrol: the automatic is on again.");
            }

            int max = PatrolVehicleLimit();
            if (PatrolUnitCount() >= max)   // NDR convoy: convoy vehicles do not count
            {
                RevivalPlugin.L.LogInfo("Patrol: " + PatrolUnitCount() + " vehicle(s) are "
                    + "already out, patrol capacity is " + max
                    + ". Shift plus the key takes them off the road.");
                Turret.Hinweis(PatrolUnitCount() + Loc.T(" патрулей в рейсе - Shift+клавиша убирает",
                                                    " patrols out - Shift+key stops them"), 3f);
                return;
            }

            Load(false);
            // The named route first, because the key has always meant "one
            // more on the route I am working on". Since every route is a
            // patrol of its own, a name that is not in the file is no longer a
            // dead end: the route most in need gets the vehicle instead.
            Route r = Active();
            if (r == null || r.IsConvoy) r = Duenn();   // NDR convoy: F11 never puts a lone patrol on a convoy road
            // The named route may belong to another region. Its waypoints are
            // not roads here, so fall back to a route this map does own rather
            // than dropping a patrol into the landscape.
            if (r != null && !r.Here) r = Duenn();
            if (r == null)
            {
                RevivalPlugin.L.LogWarning("Patrol: route \""
                    + RevivalPlugin.CfgPatrolRoute.Value + "\" is not in "
                    + RevivalPlugin.CfgPatrolFile.Value
                    + " and no other route wants a patrol. Record one - the "
                    + "editor key is " + RevivalPlugin.CfgPatrolEditorKey.Value + ".");
                Turret.Hinweis(Loc.T("Нет маршрута \"", "No route \"") + RevivalPlugin.CfgPatrolRoute.Value + "\"", 4f);
                return;
            }
            if (r.P.Count < 3)
            {
                RevivalPlugin.L.LogWarning("Patrol: route " + r.Name + " has "
                    + r.P.Count + " waypoints. A loop needs at least three.");
                return;
            }
            Spawn(r, false);
        }

        /// <summary>
        /// The patrols nobody pressed a key for.
        ///
        /// Until 2026-08-30 a patrol existed only between one F11 and the end
        /// of the session, and the user asked for what the game's own NPCs do:
        /// be there when the world comes up, and come back some time after
        /// they are killed. This is that, and it is deliberately made of the
        /// pieces that were already there - `Spawn` puts one down, `Active`
        /// finds the route, `patrol capacity` bounds the road.
        ///
        /// EVERY ROUTE IS A PATROL. Since 2026-08-30 the automatic does not
        /// serve one route out of the config but every route in the file:
        /// each says how many patrols it wants (`count=` in its first
        /// waypoint's flags) and which side drives it, `Duenn` picks the one
        /// furthest short of its number, and `patrol capacity` is the ceiling over
        /// all of them together, derived from their counts and compositions.
        /// That makes a looter patrol outside the looter base and a civilian
        /// one around the civilian base a
        /// setting rather than a rebuild.
        ///
        /// TWO CLOCKS, and they mean different things. The road is FILLED at
        /// `FillEvery` - a short interval that fills the requested patrol groups
        /// gradually after the world comes up. A LOSS is replaced after
        /// `RespawnSeconds`, and that clock starts when the vehicle is
        /// destroyed (see <see cref="Verloren"/>), not when its wreck is
        /// cleared away - otherwise the two waits would add up and the road
        /// would stay empty for nine minutes after a kill.
        ///
        /// It waits for the world. `Gun.WeltLaeuft` asks the game's own player
        /// list, which is empty in the menu and in the loading screen; the
        /// first patrol goes out `SettleFirst` seconds after it fills, because
        /// a vehicle dropped into a scene that is still building lands on
        /// nothing.
        /// </summary>
        static void Nachschub()
        {
            if (!RevivalPlugin.CfgPatrolAuto.Value || !_auto || !_refill) return;

            bool welt = Gun.WeltLaeuft();
            if (welt != _welt)
            {
                _welt = welt;
                if (welt)
                {
                    _nextAuto = Time.time + SettleFirst;
                    RevivalPlugin.L.LogInfo("Patrol: the world is up - the first "
                        + "automatic patrol goes out in " + SettleFirst.ToString("0")
                        + " s.");
                }
                else
                {
                    // The player list just went empty. That is NOT proof that
                    // the scene is gone - see WeltWeg below, which decides.
                    _abgeraeumt = false;
                    RevivalPlugin.L.LogInfo("Patrol: no player in the game's list "
                        + "- checking whether the scene is gone or the player is "
                        + "only dead.");
                }
                return;
            }
            if (!welt) { WeltWeg(); return; }

            int max = PatrolVehicleLimit();
            if (PatrolUnitCount() >= max) return;   // NDR convoy: convoy vehicles do not count
            if (Time.time < _nextAuto) return;

            Load(false);
            Route r = Duenn();
            if (r == null)
            {
                // Said once a minute, not once a frame. Both reasons for
                // landing here are normal states, not errors: no route
                // recorded yet, or every route already carrying the patrols
                // it asked for.
                _nextAuto = Time.time + 60f;
                if (_order.Count == 0)
                    RevivalPlugin.L.LogWarning("Patrol: AutoStart is on and no "
                        + "route is recorded - press the editor key ("
                        + RevivalPlugin.CfgPatrolEditorKey.Value + ") and drive "
                        + "one.");
                return;
            }

            int before = _units.Count;
            Spawn(r, true);
            if (_units.Count == before)
            {
                // CarSpawn has already said why. Trying again next frame would
                // say it sixty times a second.
                _nextAuto = Time.time + 30f;
                return;
            }
            _nextAuto = Time.time + FillEvery;
        }

        /// <summary>
        /// The player list is empty. Decide whether the scene really went away
        /// or whether the local player is merely dead, and only tear the unit
        /// list down in the first case.
        ///
        /// CONFIRMED from IL (NetworkGameServer::RespawnPlayer, its
        /// &lt;RespawnPlayer&gt;c__Iterator3): dying destroys the local player
        /// object (PlayerNetworkController::NetworkPlayerDestroy, whose
        /// OnDestroy calls NetworkGameServer::RemoveNetworkObject), sets
        /// localPlayer to null, shows the respawn loading screen and only then
        /// calls SpawnPlayer for a NEW object. Alone on a server the game's
        /// NetworkPlayers list is therefore EMPTY for the whole death screen,
        /// and Gun.WeltLaeuft says false.
        ///
        /// The old code read that as "the scene is gone" and cleared _units.
        /// The vehicles were not destroyed by it - they were only forgotten, so
        /// FixedTick stopped driving them and Gun.Tick stopped aiming and
        /// firing them. A convoy that was shooting at the player before he died
        /// stood silent for the rest of the session. That is the bug this
        /// method fixes.
        ///
        /// The honest test for "the scene is gone" is the one the old comment
        /// already named: are the tracked GameObjects destroyed? A scene change
        /// destroys every patrol vehicle, a respawn destroys none. So the
        /// teardown waits until no tracked vehicle is left - which is still
        /// immediate on a real scene change and on an empty list.
        /// </summary>
        static void WeltWeg()
        {
            if (_abgeraeumt) return;
            if (NochFahrzeugeDa()) return;   // player dead, vehicles still standing

            _abgeraeumt = true;
            bool etwasDa = _units.Count > 0 || _owned.Count > 0;
            // The scene is gone and so is everything that stood in it. The units
            // would be a list of destroyed GameObjects, and the next world would
            // inherit them.
            _units.Clear();
            _owned.Clear();
            _refill = true;
            // Said only when there was something to clear. Before the world
            // comes up for the first time both lists are empty, and "the scene
            // is gone" would be a confusing thing to read at startup.
            if (etwasDa)
                RevivalPlugin.L.LogInfo("Patrol: the scene is gone - the unit list "
                    + "is cleared for the next world.");
        }

        /// <summary>Is at least one tracked vehicle still a live GameObject?
        /// False after a scene change, true while the player is only dead.</summary>
        static bool NochFahrzeugeDa()
        {
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].Car != null) return true;
            return false;
        }

        /// <summary>
        /// The route most in need of a vehicle, or null when every one of them
        /// has what it asked for.
        ///
        /// EVERY route is a patrol now, not just the one named in the config.
        /// The user's plan is patrols at many places on the map - a looter
        /// patrol outside the looter base, a civilian one around the civilian
        /// base - and that means the automatic has to keep several routes
        /// stocked at once instead of one. Each route says how many it wants
        /// (`count=` in its first waypoint's flags, 1 when it does not say),
        /// and `patrol capacity` is the ceiling over the whole map.
        ///
        /// "Most in need" is the largest shortfall, so a route that wants
        /// three and has none is filled before one that wants one and has
        /// none. A tie goes to the route that comes first in the file, which
        /// makes the order predictable while a route is being tuned.
        /// </summary>
        static Route Duenn()
        {
            Route best = null;
            int bestFehlt = 0;
            for (int i = 0; i < _order.Count; i++)
            {
                Route r = _routes[_order[i]];
                if (r.IsConvoy) continue;   // NDR convoy: driven by the convoy event, not the auto-patrol
                if (!r.Here) continue;      // a route of another region is not driven here
                if (!r.Enabled || r.Count <= 0 || r.P.Count < 3) continue;
                int fehlt = r.Count - Fahren(r.Name);
                if (fehlt <= 0) continue;
                if (best == null || fehlt > bestFehlt) { best = r; bestFehlt = fehlt; }
            }
            return best;
        }

        /// <summary>How many patrols are on this route right now, wrecks
        /// included - a burning BTR still counts as that route's vehicle
        /// until `RespawnSeconds` says otherwise.</summary>
        static int Fahren(string name)
        {
            Dictionary<int, bool> groups = new Dictionary<int, bool>();
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].Car != null && _units[i].Route != null
                    && _units[i].Route.Name == name && _units[i].ConvoyId == 0)
                    groups[_units[i].PatrolGroupId] = true;
            return groups.Count;
        }

        /// <summary>Capacity requested by all enabled, usable patrol routes ON
        /// THIS MAP. Recomputed from the current snapshot so editor changes
        /// apply live. Legacy routes without a composition use one vehicle per
        /// patrol. Routes of another region ask for nothing here - otherwise the
        /// capacity ceiling of the region on screen would be spent on patrols
        /// that this map never spawns.</summary>
        static int PatrolVehicleLimit()
        {
            Load(false);
            long total = 0;
            for (int i = 0; i < _order.Count; i++)
            {
                Route r = _routes[_order[i]];
                if (!r.Here) continue;
                if (r.IsConvoy || !r.Enabled || r.Count <= 0 || r.P.Count < 3) continue;
                RevivalComposition.Composition composition = RevivalComposition.Of(r.Name);
                int vehicles = composition == null || composition.Vehicles.Count == 0
                    ? 1 : composition.Vehicles.Count;
                total += (long)r.Count * vehicles;
                if (total >= int.MaxValue) return int.MaxValue;
            }
            return (int)total;
        }

        /// <summary>Units the auto-patrol accounting owns - convoy vehicles
        /// (ConvoyId != 0) are managed by the convoy event and must not consume
        /// a patrol capacity slot from the ordinary patrols. NDR convoy.</summary>
        static int PatrolUnitCount()
        {
            int n = 0;
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].ConvoyId == 0) n++;
            return n;
        }

        /// <summary>
        /// A patrol is gone, and the clock for its replacement starts here.
        /// Called the moment a vehicle is destroyed, not when the wreck is
        /// removed - `WreckSeconds` and `RespawnSeconds` are two waits that
        /// must not add up.
        /// </summary>
        static void Verloren()
        {
            if (!RevivalPlugin.CfgPatrolAuto.Value || !_auto) return;
            float wait = RevivalPlugin.CfgPatrolRespawn.Value;
            if (wait <= 0f)
            {
                if (_refill)
                    RevivalPlugin.L.LogInfo("Patrol: RespawnSeconds is 0 - this one "
                        + "is not replaced.");
                _refill = false;
                return;
            }
            float when = Time.time + wait;
            if (when > _nextAuto) _nextAuto = when;
            RevivalPlugin.L.LogInfo("Patrol: the next patrol goes out in "
                + wait.ToString("0") + " s.");
        }

        public static void StopAll()
        {
            // Shift takes them off AND keeps them off. A road cleared by hand
            // that fills itself again within a minute is not a stop button.
            _auto = false;

            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                Forget(u);
                Weg(u.Car);
            }
            RevivalPlugin.L.LogInfo("Patrol: " + _units.Count + " vehicle(s) taken off the road.");
            _units.Clear();
            Crew.StopAll();
            // NDR technical crew: the trucks are gone, so the men riding them
            // are no longer anybody's. Crew.StopAll has just taken their
            // settlements; this only drops the book-keeping that pointed at them.
            TechnicalCrew.StopAll();
        }

        // =====================================================================
        //  NDR convoy seam (RevivalConvoy.cs)
        //
        //  A convoy vehicle is an ordinary patrol Unit tagged with a non-zero
        //  ConvoyId, so it is driven, gunned, crewed and wrecked by exactly the
        //  code above. The convoy layer only decides WHICH vehicles spawn WHERE,
        //  keeps their spacing (Hold), reads their state for the behaviour agent,
        //  and clears them when the next convoy comes. Everything here is a thin
        //  handle over Unit; the convoy never sees the Unit type itself.
        // =====================================================================

        /// <summary>
        /// Spawn one convoy vehicle of a forced kind, lined up
        /// <paramref name="backMetres"/> behind the column head ON THE RECORDED
        /// LINE, tagged with a convoy id. Returns an opaque handle (the Unit) or
        /// null if the route is unusable or the spawn was refused (e.g. this
        /// client is not the master).
        ///
        /// Unlike a patrol, a convoy vehicle drives the recorded route ONE WAY:
        /// no OutAndBack u-turn, no lap loop. It rolls from the start line to the
        /// last waypoint and then vanishes (Unit.Arrived).
        ///
        /// THE START LINE RUNS ALONG THE ROAD, NOT OFF IT. Up to 6.8.3 the column
        /// was laid out on a straight line extrapolated BACKWARDS from waypoint 0,
        /// which on the user's Convoy route walked 96 m up a hillside: the tail
        /// vehicles spawned about 23 m above the road surface, wedged in terrain,
        /// and spent minutes freeing themselves 30 m at a time. The head now
        /// starts <paramref name="headArc"/> metres INTO the route and every
        /// follower sits on the same recorded line behind it, so every vehicle
        /// starts on the road the admin drew, in the editor's order.
        /// </summary>
        internal static object SpawnConvoyUnit(string routeName, bool tank,
                                               float headArc, float backMetres,
                                               int convoyId,
                                               int compositionVehicle)
        {
            return SpawnConvoyUnit(routeName, tank ? "tank" : "btr", headArc,
                                   backMetres, convoyId, compositionVehicle);
        }

        /// <summary>
        /// Kind-aware convoy spawn: the vehicle kind ("btr"/"tank"/"ural") flows
        /// through the authoritative <see cref="VehicleRegistry"/>, so a convoy
        /// of 15-seat Urals drives the recorded one-way route with the SAME Unit
        /// flags (OneWay, column slot, arrive-and-vanish) as a tank/APC convoy -
        /// the one-way behaviour is set here, not by the prefab, and is therefore
        /// not bypassed by choosing a different vehicle.
        /// </summary>
        internal static object SpawnConvoyUnit(string routeName, string kind,
                                               float headArc, float backMetres,
                                               int convoyId,
                                               int compositionVehicle)
        {
            Load(false);
            Route src;
            if (!_routes.TryGetValue(routeName, out src) || src == null
                || src.P.Count < 3) return null;

            Route r = CopyRoute(src);          // one-way private copy, no u-turn
            int n = r.P.Count;
            Metrics(r);

            float arc = Mathf.Clamp(headArc - Mathf.Max(0f, backMetres),
                                    0f, Mathf.Max(0f, r.Length - 1f));
            int seg;
            Vector3 line = PointOnRoute(r, arc, out seg);
            Vector3 ahead = HeadingOnRoute(r, arc);

            // Same surface rule as the column placement, so a vehicle in a road
            // tunnel is put on the tunnel floor and not on the hill above it.
            Vector3 pos;
            if (!GroundSpot(line, null, 1.6f, out pos))
            {
                // Nothing under this exact slot. The height of the nearest
                // metre of the SAME line that does have ground is the honest
                // guess - the column is carried on that line anyway, so a slot
                // that starts a metre out is corrected on the first physics
                // step. A slot dropped onto an authored height with nothing
                // under it is not: it falls, and the driver reads the fall as
                // being stuck.
                float lineY;
                if (!LineHeight(r, arc, out lineY))
                {
                    RevivalPlugin.L.LogWarning("Convoy " + convoyId + ": no ground "
                        + "within 60 m of " + arc.ToString("0") + " m on "
                        + routeName + " - the slot is not filled. The colliders "
                        + "around that stretch are probably not loaded.");
                    return null;
                }
                pos = new Vector3(line.x, lineY + 1.6f, line.z);
            }

            bool tank;
            GameObject car = VehicleRegistry.Spawn(kind, pos,
                Quaternion.LookRotation(ahead, Vector3.up), out tank);
            if (car == null) return null;

            Unit u = new Unit();
            u.Car = car;
            u.Route = r;
            u.Tank = tank;
            u.Seite = r.Seite;
            u.Next = seg;                      // the waypoint ahead of the slot
            u.Truck = kind == "ural" || kind == "truck";
            u.ConvoyId = convoyId;
            u.CompositionVehicle = compositionVehicle;
            u.CrewSnapshot = compositionVehicle < 0 ? null : RevivalComposition.CrewOf(routeName, compositionVehicle);
            u.ColumnIndex = compositionVehicle < 0 ? 0 : compositionVehicle;
            u.Column = RevivalConvoy.ColumnLock;
            u.OneWay = true;
            u.Cols = CollectCols(car);

            // Line-mates pass right through each other: a tight column does not
            // explode, and after the column breaks a faster survivor does not
            // knock the one ahead off the road.
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].ConvoyId == convoyId)
                    GhostPair(u.Cols, _units[i].Cols);

            if (u.Column)
            {
                PlaceInColumn(u, r, arc, 0f);
                ColumnStart(convoyId, headArc);
            }

            _units.Add(u);
            _spawned++;
            RevivalPlugin.L.LogInfo("Convoy " + convoyId + ": " + kind
                + " (" + u.Seite + ") on slot " + u.ColumnIndex + " at "
                + arc.ToString("0") + " m of " + r.Name + " (" + n
                + " waypoints, " + r.Length.ToString("0") + " m, one-way, "
                + (u.Column ? "column locked" : "free driving") + ").");
            return u;
        }

        /// <summary>A plain forward copy of a route: same points, same flags, no
        /// out-and-back tail. A convoy drives this once and stops.</summary>
        static Route CopyRoute(Route r)
        {
            Route c = new Route();
            c.Name = r.Name;
            c.Fraction = r.Fraction;
            c.Vehicle = r.Vehicle;
            c.Count = r.Count;
            c.Enabled = r.Enabled;
            c.Scene = r.Scene;
            for (int i = 0; i < r.P.Count; i++) c.P.Add(r.P[i]);
            return c;
        }

        /// <summary>The flat direction of the first non-degenerate leg (wp0
        /// onward). The convoy start line runs along this, and every vehicle
        /// faces it.</summary>
        static Vector3 FirstLegDir(Route r)
        {
            int n = r.P.Count;
            for (int i = 1; i < n; i++)
            {
                Vector3 d = r.P[i].Pos - r.P[0].Pos;
                d.y = 0f;
                if (d.sqrMagnitude > 0.0001f) return d.normalized;
            }
            return Vector3.forward;
        }

        /// <summary>A convoy vehicle that has driven its whole route to the last
        /// waypoint is taken out of the world there - it simply vanishes, as the
        /// user wants, rather than looping or lingering. Called from FixedTick,
        /// which iterates the unit list top-down, so removing by index is safe.</summary>
        static void ArriveEnd(Unit u, int index)
        {
            RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": " + (u.Tank ? "tank" : "APC")
                + " reached the end of " + u.Route.Name + " - removed.");
            Forget(u);
            Weg(u.Car);
            if (index >= 0 && index < _units.Count && _units[index] == u)
                _units.RemoveAt(index);
            else
                _units.Remove(u);
        }

        /// <summary>The convoy vehicle is on the road and not yet a wreck.</summary>
        internal static bool ConvoyAlive(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Car != null && u.Died <= 0f && !u.Arrived;
        }

        /// <summary>The vehicle still exists in the world - alive OR a lingering
        /// wreck. Used to know when a whole convoy has been fully cleared.</summary>
        internal static bool ConvoyExists(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Car != null && !u.Arrived;
        }

        internal static bool ConvoyArrived(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Arrived;
        }

        internal static bool ConvoyTruck(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Truck;
        }

        internal static bool ConvoyTank(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Tank;
        }

        internal static Vector3 ConvoyPos(object handle)
        {
            Unit u = handle as Unit;
            return (u == null || u.Car == null) ? Vector3.zero
                                                : u.Car.transform.position;
        }

        /// <summary>Progress along the route in waypoints (laps * count + next),
        /// so "the road ahead is blocked by a wreck" is a comparison of numbers.
        /// Higher means further along.</summary>
        internal static float ConvoyArc(object handle)
        {
            Unit u = handle as Unit;
            if (u == null || u.Route == null) return 0f;
            return u.Lap * u.Route.P.Count + u.Next;
        }

        /// <summary>Stop this convoy vehicle where it stands, or release it back
        /// to driving. The gun keeps working while held.</summary>
        internal static void ConvoyHold(object handle, bool hold)
        {
            Unit u = handle as Unit;
            if (u != null)
            {
                u.Hold = hold;
                if (!hold)
                {
                    u.Deploy = false;
                    u.DeployPosted = false;
                }
            }
        }

        // =====================================================================
        //  NDR convoy BREAK-UP (what happens after the first loss)
        //
        //  The column is over the moment a vehicle is destroyed, and from then
        //  on the survivors belong to RevivalConvoy.Behaviour: one escapee
        //  drives on, everybody else fights. "Fights" used to mean one sidestep
        //  of 18 m onto a fixed shoulder, decided once and never revisited, with
        //  nothing but ConvoyBlocked between hulls that ignore each other's
        //  colliders. Three things came out of that, all of them visible in the
        //  game as a convoy driving into itself instead of spreading out:
        //
        //    - The shoulder was only ever tried on ONE side (the slot's parity)
        //      at four distances. A ditch, a wall or a slope on that side left
        //      the vehicle standing in the column line, and it was never asked
        //      again, because the deployment was a one-shot.
        //    - The escapee's road runs THROUGH the wreck of the vehicle in front
        //      of it. ConvoyBlocked brakes for it and resets the stuck timer, so
        //      the escapee stood nose to tail against a burning hull for the rest
        //      of the event - with the rest of the convoy piling up behind it.
        //    - Convoy hulls are deliberately ghosted against each other, so two
        //      of them that do end up in the same place simply interpenetrate,
        //      and nothing ever pushes them apart again.
        //
        //  The three answers are DeployPost (both shoulders, three distances,
        //  three offsets along the road, biased onto the side the shooting comes
        //  from, retried until one is found), StartBypass (drive AROUND a mate's
        //  wreck instead of queueing into it, and if that is impossible, tell the
        //  behaviour layer that this vehicle cannot escape and must fight) and
        //  ConvoySeparate (two convoy hulls are never allowed to overlap).
        //
        //  Retrying is not the same as arriving, and the first version of that
        //  search could hand out positions no vehicle would ever drive to. Three
        //  rules keep a "firing position" a place the vehicle actually reaches,
        //  because a defender that does not reach one is a defender parked in the
        //  column line - which is the same picture from the outside as no fix at
        //  all:
        //
        //    1. Only positions the vehicle can drive to (PostCone). It has no
        //       reverse, so a shoulder behind it is not a shoulder.
        //    2. The search always answers. It walks its ring three times, giving
        //       up the lane reservation and then the comfortable spacing, and only
        //       calls a position impossible when even bare hull clearance fails.
        //    3. A hull in the way costs THIS position, not the event: LegBlocked
        //       looks along the leg being driven rather than down the nose, and a
        //       few seconds of it sends the vehicle looking for another shoulder.
        // =====================================================================

        /// <summary>Metres to the side of the road the first firing position is
        /// looked for. Further candidates step out from here.</summary>
        const float DeploySpread = 20f;

        /// <summary>Seconds between two attempts to find a firing position for a
        /// vehicle that could not be given one. A defender standing in the middle
        /// of the road is the failure the user saw; it has to keep trying.</summary>
        const float DeployRetryEvery = 4f;

        /// <summary>Degrees per second a defender at its post turns its hull onto
        /// the threat. It is standing on the brake, so the heading is eased by
        /// hand, exactly the way a carried column hull is.</summary>
        const float DeployTurnRate = 25f;

        /// <summary>
        /// Degrees off its own nose a defender will still drive to a firing
        /// position, and the narrower cone the search hands one out in.
        ///
        /// These two belong together, and the gap between them is the whole
        /// point. There is no reverse gear here, so a post BEHIND the vehicle is
        /// not a post: DeployStep abandons it the moment it sees it. The search
        /// did not know that. Its candidates ran 14 m back, level, 14 m forward
        /// in that order, so the FIRST one offered was a shoulder 125 degrees off
        /// the nose - abandoned in the same physics step it was given, then
        /// offered again four seconds later, because the ring is fixed geometry
        /// and nothing about it had changed. A defender can leave that loop only
        /// by turning its hull far enough onto the threat first. That is one of
        /// the two ways a survivor ends up parked in the column line (the other
        /// is DeployLane refusing every candidate), which is the convoy driving
        /// into itself that the user reported.
        /// </summary>
        const float DeployCone = 110f;
        const float PostCone = 95f;

        /// <summary>Metres a firing position keeps from every other hull of the
        /// same convoy, and from the position another defender was already given.
        /// PostClearLast is the last-resort minimum: standing in the column line
        /// is worse than a tight pair of posts.</summary>
        const float PostClear = 22f;
        const float PostClearLast = 14f;

        /// <summary>Metres of clear air between two defenders' drive legs.</summary>
        const float LaneClear = 14f;

        /// <summary>Seconds a defender brakes for a hull in the way of its leg
        /// before it gives up on THIS position and asks for another one. Standing
        /// still is never the answer; a different shoulder always is.</summary>
        const float BlockedPost = 3f;

        /// <summary>A defender that is farther than this from the shooting closes
        /// in on it in bounds of <see cref="EngageStep"/> metres.</summary>
        const float EngageWithin = 120f;

        const float EngageStep = 35f;

        /// <summary>Seconds a defender stays at a post before taking the next
        /// bound forward.</summary>
        const float EngagePause = 5f;

        /// <summary>Seconds of braking for a burning convoy mate before the
        /// survivor tries to drive round it.</summary>
        const float BypassAfter = 2f;

        /// <summary>Seconds a bypass aim stays latched. Dropping it the moment
        /// the corridor looks clear steers the vehicle straight back behind the
        /// wreck it just started to pass.</summary>
        const float BypassSeconds = 14f;

        /// <summary>Metres of clear air between the two hulls while passing, on
        /// top of both measured half widths.</summary>
        const float BypassClear = 10f;

        /// <summary>Metres beyond the wreck the bypass point sits, and the
        /// distance at which the bypass counts as done.</summary>
        const float BypassAhead = 16f;
        const float BypassReached = 10f;

        /// <summary>km/h a vehicle passes a wreck at.</summary>
        const float BypassSpeed = 18f;

        /// <summary>Metres ahead a burning convoy mate is looked for.</summary>
        const float WreckLookAhead = 60f;

        /// <summary>Seconds of being stopped by a wreck after which this vehicle
        /// is written off as unable to escape and joins the fight instead.</summary>
        const float EscapeGiveUp = 8f;

        /// <summary>Metres of clear air between two convoy hulls, on top of both
        /// measured half lengths.</summary>
        const float SeparateGap = 3f;

        /// <summary>Metres per second two overlapping hulls are eased apart.
        /// A shove, not a teleport - it has to look like manoeuvring.</summary>
        const float SeparateStep = 4f;

        /// <summary>Seconds a remembered threat position is still worth facing.</summary>
        const float ThreatMemory = 90f;

        // ------------------------------------------------- ordinary patrol traffic
        //
        //  The vehicles of one editor composition ignore each other's colliders
        //  from the moment they are put down (see Spawn), exactly as a convoy
        //  does, and for the same reason: a line-up must not explode and a
        //  faster mate must not shove the one ahead off the road. The convoy
        //  pays the price of that - physics can no longer separate two hulls
        //  either - with ConvoyBlocked and ConvoySeparate. An ordinary patrol
        //  had neither, which is the reported bug: a tank standing INSIDE the
        //  APC it patrols with, the pair unable to drive and unable to see a
        //  target past each other's plate. These five numbers are that price.
        //
        //  THE SECOND ROUND (6.39.0). That fix held the hulls apart but the pair
        //  still stood on the road, because the RECOVERY refused to run. The log
        //  of 2026-09-21 has the proof, twice every ten seconds for a whole
        //  session and never anything else: "no waypoint from 559 on has ground
        //  under it". Three faults behind one message, all of them fixed above:
        //
        //    - The search budget was twelve WAYPOINTS. The live recordings
        //      average 11.5 m per waypoint on the looter route and 3.7 m on the
        //      civilian one, so the same twelve waypoints are 138 m of one road
        //      and 44 m of the other - and the looter patrol recovered from
        //      every stop in that log while the civilian patrol never moved
        //      again. GroundReach makes the budget metres.
        //    - Two vehicles of one composition meet head on - an out-and-back
        //      route guarantees it - and each then refuses to warp because the
        //      other is standing on every free waypoint. HoldsBeforeForce ends
        //      that standoff; landing on a mate cannot weld the pair, because
        //      they ignore each other's colliders.
        //    - The message named the wrong reason. A spot held by a VEHICLE was
        //      reported as a spot with no GROUND, which is why the first round
        //      looked at the hulls and not at the recovery.
        //
        //  And an ordinary patrol now ghosts through world props like a convoy
        //  (GhostAhead in Drive): steering is the first answer, but a dirt road
        //  between two rows of trees has nowhere to steer to.

        /// <summary>Metres of clear air between two hulls of one patrol group,
        /// on top of both measured footprints. Small on purpose: this is an
        /// overlap test, not a comfort distance, and a vehicle squeezing past
        /// its own wreck must not be shoved off the road for it.</summary>
        const float PatrolOverlapGap = 0.5f;

        /// <summary>Metres ahead a patrol looks for the mate it is driving
        /// behind, and the distance at which it stops closing up.</summary>
        const float QueueLook = 26f;
        const float QueueGap = 14f;

        /// <summary>Metres a mate may be off this vehicle's nose line and still
        /// count as the vehicle ahead rather than as passing traffic.</summary>
        const float QueueLateral = 5f;

        /// <summary>How much of one nose direction has to point the way of the
        /// other for the two to be driving the SAME way. An out-and-back route
        /// carries both directions on one road, and two vehicles that brake for
        /// each other head on never move again.</summary>
        const float QueueSameWay = 0.3f;

        /// <summary>Seconds a patrol queues behind a mate before the ordinary
        /// stuck escalation is allowed to treat the queue as a blocked road.
        /// Without a limit, one vehicle that never moves again stops the whole
        /// group for the rest of the session.</summary>
        const float QueuePatience = 20f;

        /// <summary>Metres a patrol has to cover inside
        /// <see cref="ProgressSeconds"/> to count as driving. 10 m in 10 s is
        /// 3.6 km/h - well under anything a working patrol does, and the one
        /// test a pair of shaking, interpenetrating hulls cannot pass.</summary>
        const float ProgressMetres = 10f;
        const float ProgressSeconds = 10f;

        /// <summary>Metres of clear air a stuck vehicle's warp target keeps from
        /// the place it got stuck, and from every other hull on the road. The
        /// first stops the warp dropping the hull straight back onto the vehicle
        /// it is stuck against; the second stops two mates stuck at the same
        /// obstacle being warped onto the same waypoint, which is the shortest
        /// way to weld two vehicles together there is.</summary>
        const float FreeClear = 15f;
        const float FreeRoom = 12f;

        /// <summary>Refusals to warp in a row after which a MATE of this
        /// vehicle's own composition stops counting as an occupied spot.
        ///
        /// WHY THIS EXISTS. An out-and-back route carries both directions on one
        /// road, so the two vehicles of one composition are guaranteed to meet
        /// head on somewhere on it. When they stop there, each one refuses to
        /// warp because the other is standing on every free waypoint - and then
        /// NEITHER of them ever moves again. That is the deadlock in the log of
        /// 2026-09-21: two civilian-patrol hulls, the same two warnings every ten
        /// seconds, for the whole session. Landing on a mate cannot weld the pair
        /// - they ignore each other's colliders and PatrolSeparate eases them
        /// apart within a second - so after three refusals it is the better
        /// outcome. A FOREIGN hull is solid and still never landed on.</summary>
        const int HoldsBeforeForce = 3;

        /// <summary>Metres a patrol may be from its own recorded line, and the
        /// seconds it may stay there, before it is put back on the road. The
        /// recording IS the road: 35 m off it is a field or a wood.</summary>
        const float LeashMetres = 35f;
        const float LeashSeconds = 8f;

        // ------------------------------------------------------------- the rail
        //
        //  WHY THIS EXISTS, in the user's own words (2026-09-22): "wenn am Ende
        //  die Fahrzeuge wieder TP'd werden, sind sie halt falsch herum, dann
        //  versuchen sie ne Kurve zu fahren, bleiben immer stuck, werden wieder
        //  zurueck TP'd, versuchen wieder auf natuerlichem Weg zurueckzufinden
        //  und es klappt nie."
        //
        //  That is one loop with two halves, and BOTH halves are the recovery's
        //  own doing. A warp puts the hull ON a waypoint and then asks the
        //  driver to take over from a standstill; the first thing the driver
        //  wants is the corner the waypoint sits in, it steers into whatever
        //  stopped the vehicle the first time, and the stuck timer fills again.
        //  Every lap round that loop the vehicle is somewhere slightly worse.
        //
        //  So a confirmed stop no longer ends in a warp and a prayer. The
        //  vehicle is CARRIED along its own recorded line - the same placement
        //  the convoy column has used since the column existed: on the surface,
        //  facing the way the road runs, wheels turning at road speed, physics
        //  unable to hold it. It cannot end up facing the wrong way because the
        //  heading is read off the road at the point it stands on, and it cannot
        //  wedge on anything because nothing it drives through is allowed a vote.
        //  After RailMetres it is handed back to the driver at speed, pointing
        //  down the road - which is the hand-over the warp never managed.
        //
        //  And if that hand-back fails RailRuns times inside RailForget, the
        //  pretence stops: the vehicle stays on the rail for the rest of its
        //  life. A patrol that drives its route through a rock is worth more to
        //  the game than a patrol that is honest about physics and never moves.

        /// <summary>Metres of recorded line a carried vehicle covers before the
        /// driver is offered the wheel again. Long enough to be past the thing
        /// that stopped it and up to road speed when it takes over - fifteen
        /// metres, the old warp's distance, is neither.</summary>
        const float RailMetres = 60f;

        /// <summary>Seconds a carry lasts at the very least, so a vehicle
        /// carried downhill does not cover its sixty metres in one breath and
        /// get the wheel back before it is going anywhere.</summary>
        const float RailSeconds = 4f;

        /// <summary>Carries inside <see cref="RailForget"/> after which this
        /// vehicle is not handed back at all. Three is "the road here is beyond
        /// it", not "it was unlucky".</summary>
        const int RailRunsStick = 3;
        const float RailForget = 120f;

        /// <summary>Legs either side of the one it was driving that the arc
        /// lookup considers. A lap route can pass within a few metres of itself,
        /// and the nearest leg IN THE WORLD is then the other carriageway - which
        /// would face the vehicle back the way it came, the very fault this
        /// exists to end.</summary>
        const int RailLook = 4;

        /// <summary>Metres inside which something the SHOULDER rays find - and
        /// the centre ray does not - is worth steering away from, and how much
        /// lock that is worth at contact. A forest road grazes those rays with
        /// every tree; swerving a fifth of a turn for each one is what walked
        /// patrols off the road and into the wood.</summary>
        const float SideDodgeAt = 6f;
        const float SideDodge = 0.35f;

        /// <summary>Metres between two hull centres that mean "the same piece of
        /// road", not "cover". Two patrol vehicles cannot stand this close
        /// without overlapping; see Gun.Welded.</summary>
        const float WeldedWithin = 6f;

        /// <summary>
        /// Send this convoy vehicle to a firing position and keep it there.
        ///
        /// Called every tick by the behaviour layer for every survivor that is
        /// not the escapee, so it is also the place that RETRIES: a vehicle that
        /// could not be given a position keeps asking, and a vehicle that has
        /// taken its position asks again once it wants the next bound forward
        /// (DeployStep clears DeployPosted for that).
        /// </summary>
        internal static void ConvoyDeploy(object handle)
        {
            Unit u = handle as Unit;
            if (u == null || u.Car == null || u.Died > 0f || u.Arrived) return;
            Transform t = u.Car.transform;
            if (!u.Deploy)
            {
                u.Deploy = true;
                u.Hold = true;
                u.DeployPosted = false;
                u.DeployRetry = 0f;
                u.DeployTries = 0;
                u.DeployAnchor = t.position;
                u.DeployTarget = t.position;
                u.BypassUntil = 0f;
            }
            if (u.DeployPosted || Time.time < u.DeployRetry) return;
            u.DeployRetry = Time.time + DeployRetryEvery;

            Vector3 post;
            if (!DeployPost(u, t, out post))
            {
                RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": slot "
                    + u.ColumnIndex + " found no firing position - holding, "
                    + "next attempt in " + DeployRetryEvery.ToString("0") + " s.");
                return;
            }
            u.DeployTarget = post;
            u.DeployPosted = true;
            RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": slot "
                + u.ColumnIndex + " deploying to " + u.DeployTarget + " ("
                + FlatDistance(t.position, post).ToString("0") + " m out, "
                + FlatDistance(u.DeployAnchor, post).ToString("0")
                + " m from the column).");
        }

        /// <summary>
        /// A firing position off the road line: both shoulders, three distances
        /// out and three offsets along the road, forward and abeam before
        /// backward, and only ever one the vehicle can actually drive to
        /// (<see cref="Reachable"/>). When the threat is known and still far away
        /// the whole ring is carried a bound towards it, which is what turns
        /// "spread out" into "close in" without the vehicle ever leaving the area
        /// of its convoy. The ring is walked three times with one requirement
        /// dropped each time, because the alternative to a cramped position is
        /// not a better one - it is a vehicle parked in the middle of the road.
        /// </summary>
        static bool DeployPost(Unit u, Transform t, out Vector3 post)
        {
            post = t.position;
            Vector3 road = u.Route == null ? t.forward
                                           : RouteDirection(u.Route, u.Next, u.OneWay);
            road.y = 0f;
            if (road.sqrMagnitude < 0.0001f) road = t.forward;
            road.y = 0f;
            if (road.sqrMagnitude < 0.0001f) return false;
            road = road.normalized;
            Vector3 side = Vector3.Cross(Vector3.up, road);
            side.y = 0f;
            if (side.sqrMagnitude < 0.0001f) return false;
            side = side.normalized;

            Vector3 threat;
            bool aimed = ConvoyThreat(u, out threat);
            Vector3 toThreat = Vector3.zero;
            float first = u.ColumnIndex % 2 == 0 ? 1f : -1f;
            float bound = 0f;
            if (aimed)
            {
                toThreat = threat - t.position;
                toThreat.y = 0f;
                if (toThreat.sqrMagnitude > 1f)
                {
                    float gap = toThreat.magnitude;
                    // Swing towards the shooting only while the shooting is off
                    // to one SIDE of the road. When it comes from straight down
                    // the road, every vehicle of the column would otherwise pick
                    // the same shoulder and queue up on it exactly as it stood
                    // before - so the even and the odd slots split onto opposite
                    // sides instead, which is what fanning out looks like.
                    float sideways = Vector3.Dot(toThreat, side) / gap;
                    if (Mathf.Abs(sideways) > 0.5f) first = sideways >= 0f ? 1f : -1f;
                    float room = RevivalConvoy.EngageAdvance
                               - FlatDistance(u.DeployAnchor, t.position);
                    if (gap > EngageWithin && room > 10f)
                        bound = Mathf.Min(EngageStep, Mathf.Min(gap - EngageWithin, room));
                    toThreat = toThreat.normalized;
                }
                else aimed = false;
            }

            // Three passes over the same ring, each giving up one requirement.
            // A defender that finds nothing holds where it stands, and where it
            // stands is the column line in the middle of the road - so "nothing
            // found" has to be the rarest answer this can give, not the usual
            // one. Pass 0 reserves the drive to the post as well as the post,
            // pass 1 only the post, pass 2 takes any ground that is merely clear
            // of a hull. Forward and abeam before backward, because a shoulder
            // behind the vehicle cannot be driven to at all.
            for (int relax = 0; relax < 3; relax++)
            {
                for (int s = 0; s < 2; s++)
                {
                    float sign = s == 0 ? first : -first;
                    for (int outward = 0; outward < 3; outward++)
                    {
                        // One notch further out per position this vehicle failed
                        // to reach, so a shoulder it cannot get to is not offered
                        // to it again and again.
                        float reach = DeploySpread + ((outward + u.DeployTries) % 3) * 9f;
                        for (int along = 0; along < 3; along++)
                        {
                            Vector3 cand = t.position
                                + side * (sign * reach)
                                + road * (along == 0 ? 14f : (along == 1 ? 0f : -14f));
                            if (aimed && bound > 0f)
                            {
                                // Pressing forward only counts if the vehicle can
                                // drive there. Shooting from BEHIND the column
                                // would otherwise carry every candidate behind the
                                // hull and the search would answer "nothing" - so
                                // the vehicle swings out onto its shoulder first,
                                // turns onto the threat there, and presses on the
                                // bound after that.
                                Vector3 pressed = cand + toThreat * bound;
                                if (Reachable(t, pressed)) cand = pressed;
                            }
                            if (!Reachable(t, cand)) continue;
                            bool clear = relax < 2
                                ? DeployRoom(u, cand)
                                : DeployClear(u, cand, PostClearLast);
                            if (!clear) continue;
                            if (relax < 1 && !DeployLane(u, cand)) continue;
                            float y;
                            Vector3 normal;
                            if (!RoadUnder(cand, t, out y, out normal) || normal.y < 0.8f
                                || Mathf.Abs(y - t.position.y) > 6f) continue;
                            cand.y = y;
                            post = cand;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>Can this vehicle drive to that point at all? A convoy vehicle
        /// has no reverse and no turn on the spot, so a point behind it is not
        /// somewhere it can be sent. Deliberately narrower than the cone
        /// <see cref="DeployStep"/> abandons a post at, so a position is never
        /// handed out and thrown away in the same physics step.</summary>
        static bool Reachable(Transform t, Vector3 point)
        {
            Vector3 local = t.InverseTransformPoint(point);
            return Mathf.Abs(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg) <= PostCone;
        }

        /// <summary>
        /// Where the shooting is coming from, as far as this convoy knows it.
        /// The vehicle's own gun target first; otherwise the freshest position
        /// any member of the same convoy last had a target at - including the
        /// vehicle that has just been destroyed, which is usually the only one
        /// that ever saw the attacker.
        /// </summary>
        static bool ConvoyThreat(Unit u, out Vector3 point)
        {
            point = Vector3.zero;
            if (u == null) return false;
            if (u.Target != null) { point = u.Target.position; return true; }
            if (u.ConvoyId == 0) return false;
            bool found = false;
            float freshest = 0f;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other.ConvoyId != u.ConvoyId || other.ThreatAt <= 0f) continue;
                if (Time.time - other.ThreatAt > ThreatMemory) continue;
                if (found && other.ThreatAt <= freshest) continue;
                freshest = other.ThreatAt;
                point = other.Threat;
                found = true;
            }
            return found;
        }

        /// <summary>The nearest BURNING vehicle of this convoy in the forward
        /// corridor. What <see cref="ConvoyBlocked"/> is braking for when it is
        /// braking for something that will never move again.</summary>
        static Unit ConvoyWreckAhead(Unit self, Vector3 direction)
        {
            if (self.ConvoyId == 0 || self.Car == null) return null;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return null;
            direction = direction.normalized;
            Vector3 pos = self.Car.transform.position;
            Unit best = null;
            float bestAhead = 0f;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.ConvoyId != self.ConvoyId
                    || other.Car == null || other.Arrived || other.Died <= 0f) continue;
                Vector3 delta = other.Car.transform.position - pos;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, direction);
                if (ahead <= 0f || ahead > WreckLookAhead) continue;
                Vector3 lateral = delta - direction * ahead;
                if (lateral.sqrMagnitude >= 196f) continue;
                if (best != null && ahead >= bestAhead) continue;
                best = other;
                bestAhead = ahead;
            }
            return best;
        }

        /// <summary>Has this vehicle been stopped by a wreck of its own convoy
        /// long enough to give up on getting past it? The behaviour layer asks
        /// before it makes a vehicle the escapee: the road out is blocked by a
        /// hull that will never move, so this one fights instead of queueing.
        /// Deliberately not cleared again - a road that is shut stays shut for
        /// this event, and a flag that flickers would swap escapees every
        /// second.</summary>
        internal static bool ConvoyEscapeBlocked(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Blocked >= EscapeGiveUp;
        }

        /// <summary>Has this convoy vehicle been given a real firing position?
        /// False while it is still holding where it stands and looking for one -
        /// and a survivor holding where it stands is a survivor standing in the
        /// column line, which is the thing the break-up exists to end. The
        /// behaviour layer reports the count.</summary>
        internal static bool ConvoyHasPost(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Deploy && u.DeployPosted;
        }

        /// <summary>
        /// Latch an aim point that leads PAST a burning convoy mate instead of
        /// into it: clear of both measured hull widths, on the side the survivor
        /// is already on, a little beyond the wreck. Latched for
        /// <see cref="BypassSeconds"/>, because an aim that is dropped as soon as
        /// the forward corridor looks clear puts the vehicle straight back behind
        /// the wreck.
        /// </summary>
        static bool StartBypass(Unit u, Transform t, Unit wreck)
        {
            if (wreck == null || wreck.Car == null) return false;
            if (u.ColumnLift <= 0f) ColumnFootprint(u);
            if (wreck.ColumnLift <= 0f) ColumnFootprint(wreck);
            Vector3 forward = t.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return false;
            forward = forward.normalized;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            side.y = 0f;
            if (side.sqrMagnitude < 0.0001f) return false;
            side = side.normalized;

            Vector3 wreckPos = wreck.Car.transform.position;
            Vector3 delta = wreckPos - t.position;
            delta.y = 0f;
            // Pass on the side the wreck is NOT on, so the vehicle keeps the line
            // it already has instead of crossing in front of the hull.
            float first = Vector3.Dot(delta, side) >= 0f ? -1f : 1f;
            float room = u.ColumnHalfWidth + wreck.ColumnHalfWidth + BypassClear;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                float sign = attempt == 0 ? first : -first;
                Vector3 point = wreckPos + side * (sign * room) + forward * BypassAhead;
                float y;
                Vector3 normal;
                if (!RoadUnder(point, t, out y, out normal) || normal.y < 0.7f) continue;
                point.y = y;
                u.BypassPoint = point;
                u.BypassUntil = Time.time + BypassSeconds;
                u.Blocked = 0f;
                RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": slot "
                    + u.ColumnIndex + " drives around the wreck on slot "
                    + wreck.ColumnIndex + " at " + room.ToString("0") + " m.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Two convoy hulls are never allowed to occupy the same place.
        ///
        /// Every vehicle of one convoy is made to ignore every other one's
        /// colliders at spawn, so a tight column does not explode and a faster
        /// survivor does not shove the one ahead off the road. The price is that
        /// physics can no longer separate them either: once the column is broken
        /// and each vehicle steers for itself, two of them that meet simply
        /// interpenetrate and stay that way. This eases the later slot (and
        /// always the living vehicle, never the wreck) out of the overlap at
        /// walking pace, and only onto ground a raycast actually found - a push
        /// into thin air would be worse than the overlap.
        /// </summary>
        static void ConvoySeparate(Unit u)
        {
            if (u.ConvoyId == 0 || u.Car == null || u.Column
                || u.Died > 0f || u.Arrived) return;
            if (u.ColumnLift <= 0f) ColumnFootprint(u);
            Transform t = u.Car.transform;
            Vector3 push = Vector3.zero;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == u || other.ConvoyId != u.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                // Exactly one of a pair gives way, or they would shove each other
                // back and forth forever. A wreck never gives way at all.
                if (other.Died <= 0f && u.ColumnIndex <= other.ColumnIndex) continue;
                if (other.ColumnLift <= 0f) ColumnFootprint(other);
                Vector3 delta = t.position - other.Car.transform.position;
                delta.y = 0f;
                float clear = u.ColumnHalfLength + other.ColumnHalfLength + SeparateGap;
                float gap = delta.magnitude;
                if (gap >= clear) continue;
                // Perfectly stacked hulls have no direction to separate along;
                // step out sideways so the pair does not stay welded together.
                Vector3 away = gap > 0.05f ? delta * (1f / gap) : t.right;
                away.y = 0f;
                if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
                push += away.normalized * (clear - gap);
            }
            if (push.sqrMagnitude < 0.0001f) return;

            float step = Mathf.Min(push.magnitude, SeparateStep * Time.fixedDeltaTime);
            Vector3 want = t.position + push.normalized * step;
            float y;
            Vector3 normal;
            if (!RoadUnder(want, t, out y, out normal)) return;
            Vector3 bottom = t.rotation * new Vector3(0f, -u.ColumnLift, 0f);
            want.y = y - bottom.y + 0.15f;
            t.position = want;
            if (Time.time >= u.ColumnGroundLog)
            {
                u.ColumnGroundLog = Time.time + 10f;
                RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": slot "
                    + u.ColumnIndex + " eased out of an overlapping hull.");
            }
        }

        /// <summary>
        /// The same for two hulls of one ORDINARY patrol, which is where the
        /// user found them welded together.
        ///
        /// Only a pair that ignores each other's colliders is this method's
        /// business - the vehicles of ONE editor composition, ghosted to each
        /// other in <see cref="Spawn"/>. Any other pair on the road is solid and
        /// is pushed apart by the physics engine itself; shoving those would
        /// only steer them into the ditch.
        ///
        /// The clearance is an OVERLAP test and nothing more. Two hulls lie
        /// inside each other when their centres are closer than the two
        /// footprints measured in the direction that joins them - nose to tail
        /// that is both half lengths, side by side both half widths - so a
        /// vehicle squeezing past its own wreck is left alone while a tank
        /// standing in an APC is eased out at walking pace, and only onto ground
        /// a raycast actually found.
        /// </summary>
        static void PatrolSeparate(Unit u)
        {
            // A carried hull is put on its line every step, so easing it sideways
            // out of a mate only fights the rail - and the rail is the one that
            // wins, every step, by a whole placement.
            if (u.ConvoyId != 0 || u.Car == null || u.Column || u.Rail
                || u.Died > 0f || u.Arrived || u.PatrolGroupId == 0) return;
            if (u.ColumnLift <= 0f) ColumnFootprint(u);
            Transform t = u.Car.transform;
            Vector3 push = Vector3.zero;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == u || other.Car == null || other.Arrived) continue;
                if (other.ConvoyId != 0 || other.PatrolGroupId != u.PatrolGroupId)
                    continue;
                // Exactly one of a pair gives way, or the two shove each other
                // back and forth forever. A wreck never gives way at all, and
                // the instance id is a stable order for as long as both hulls
                // exist - which is exactly as long as this pair can overlap.
                if (other.Died <= 0f
                    && u.Car.GetInstanceID() < other.Car.GetInstanceID()) continue;
                if (other.ColumnLift <= 0f) ColumnFootprint(other);
                Vector3 delta = t.position - other.Car.transform.position;
                delta.y = 0f;
                float gap = delta.magnitude;
                Vector3 away = gap > 0.05f ? delta * (1f / gap) : t.right;
                away.y = 0f;
                if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
                away = away.normalized;
                float clear = Reach(u, away) + Reach(other, away) + PatrolOverlapGap;
                if (gap >= clear) continue;
                push += away * (clear - gap);
            }
            if (push.sqrMagnitude < 0.0001f) return;

            float step = Mathf.Min(push.magnitude, SeparateStep * Time.fixedDeltaTime);
            Vector3 want = t.position + push.normalized * step;
            float y;
            Vector3 normal;
            if (!RoadUnder(want, t, out y, out normal)) return;
            Vector3 bottom = t.rotation * new Vector3(0f, -u.ColumnLift, 0f);
            want.y = y - bottom.y + 0.15f;
            t.position = want;
            // Deliberately NOT progress: being eased out of another hull takes a
            // second or two, and a pair this cannot free - one on ground the
            // lookup refuses, say - must still reach the stuck escalation.
            if (Time.time >= u.ColumnGroundLog)
            {
                u.ColumnGroundLog = Time.time + 10f;
                RevivalPlugin.L.LogInfo("Patrol: a vehicle on " + u.Route.Name
                    + " was eased out of a mate's hull.");
            }
        }

        /// <summary>How far this hull reaches from its own centre in one flat
        /// direction: half its width across, half its length along the nose, and
        /// the honest mixture of the two in between. Enough to tell two hulls
        /// that overlap from two that merely drive close.</summary>
        static float Reach(Unit u, Vector3 direction)
        {
            if (u.Car == null) return 0f;
            Vector3 nose = u.Car.transform.forward;
            nose.y = 0f;
            if (nose.sqrMagnitude < 0.0001f) return u.ColumnHalfLength;
            float along = Mathf.Abs(Vector3.Dot(nose.normalized, direction));
            return u.ColumnHalfWidth
                 + (u.ColumnHalfLength - u.ColumnHalfWidth) * along;
        }

        /// <summary>Is any other vehicle this class knows standing at this point?
        /// A warp target that is not is the whole difference between a patrol
        /// that gets going again and a pair of hulls welded into each other.
        /// Convoy vehicles count too: a patrol warped into a passing convoy is
        /// the same bug with a different owner.</summary>
        static bool SpotTaken(Unit self, Vector3 target, float room)
        {
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.Car == null || other.Arrived) continue;
                if (FlatDistance(target, other.Car.transform.position) < room)
                    return true;
            }
            return false;
        }

        /// <summary>The same question as <see cref="SpotTaken"/>, asked only of
        /// the hulls this vehicle CANNOT drive through. A mate of its own
        /// composition ignores its colliders and is eased back out of it by
        /// <see cref="PatrolSeparate"/> within a second, so landing on one is a
        /// state that resolves itself; landing on a foreign hull is the weld the
        /// whole section exists to prevent. Used once the ordinary answer has
        /// refused <see cref="HoldsBeforeForce"/> times running.</summary>
        static bool SpotBlocked(Unit self, Vector3 target, float room)
        {
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.Car == null || other.Arrived) continue;
                bool mate = self.ConvoyId == 0 && other.ConvoyId == 0
                            && self.PatrolGroupId != 0
                            && other.PatrolGroupId == self.PatrolGroupId;
                if (mate) continue;
                if (FlatDistance(target, other.Car.transform.position) < room)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Drive behind a group mate, not into it.
        ///
        /// The hulls of one patrol group pass through each other, so nothing
        /// physical stops the second vehicle of a composition from ending up
        /// inside the first the moment the first slows for a corner. This is
        /// the brake that would have been unnecessary if they were solid: the
        /// speed of the mate ahead while it is inside <see cref="QueueLook"/>,
        /// and a stop once the gap is down to <see cref="QueueGap"/>.
        ///
        /// Only a mate driving the SAME way counts. On an out-and-back route the
        /// other direction uses the same road, and two vehicles braking for each
        /// other head on would stand there until the session ended.
        ///
        /// Waiting in a queue is not being stuck, so the timers are held back
        /// while it lasts - but only for <see cref="QueuePatience"/> seconds.
        /// After that the vehicle ahead is treated as what it has proved to be,
        /// a blocked road, and the ordinary escalation may move this one past it.
        /// </summary>
        static float QueueBehind(Unit u, Transform t, float want, float dt)
        {
            if (u.ConvoyId != 0 || u.PatrolGroupId == 0) return want;
            Vector3 forward = t.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return want;
            forward = forward.normalized;
            Vector3 pos = t.position;

            float slowest = -1f;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == u || other.Car == null || other.Arrived) continue;
                if (other.ConvoyId != 0 || other.PatrolGroupId != u.PatrolGroupId)
                    continue;
                // A burning mate never moves again. Queueing behind that is
                // standing still for nothing; the avoider steers round it and
                // the hulls ignore each other anyway.
                if (other.Died > 0f) continue;
                Transform ot = other.Car.transform;
                Vector3 delta = ot.position - pos;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, forward);
                if (ahead <= 0f || ahead > QueueLook) continue;
                Vector3 lateral = delta - forward * ahead;
                if (lateral.sqrMagnitude > QueueLateral * QueueLateral) continue;
                Vector3 hers = ot.forward;
                hers.y = 0f;
                if (hers.sqrMagnitude < 0.0001f) continue;
                if (Vector3.Dot(forward, hers.normalized) < QueueSameWay) continue;

                float allow = ahead <= QueueGap
                    ? 0f
                    : Mathf.Min(want, Velocity(other.Body).magnitude * 3.6f);
                if (slowest < 0f || allow < slowest) slowest = allow;
            }

            if (slowest < 0f)
            {
                u.Queued = 0f;
                return want;
            }
            u.Queued += dt;
            if (u.Queued < QueuePatience)
            {
                u.Stuck = 0f;
                MadeProgress(u, pos);
            }
            return slowest;
        }

        /// <summary>A firing position is clear of every other hull of this convoy
        /// AND of the position another defender has already been given. Two
        /// defenders sent to the same shoulder are one defender.</summary>
        static bool DeployRoom(Unit self, Vector3 target)
        {
            if (!DeployClear(self, target, PostClear)) return false;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.ConvoyId != self.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                if (other.Deploy && FlatDistance(target, other.DeployTarget) < PostClear)
                    return false;
            }
            return true;
        }

        /// <summary>Metres of clear air between a point and every other hull of
        /// the same convoy - the one requirement a firing position never gives
        /// up, because the convoy's hulls ignore each other's colliders.</summary>
        static bool DeployClear(Unit self, Vector3 target, float room)
        {
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.ConvoyId != self.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                if (FlatDistance(target, other.Car.transform.position) < room) return false;
            }
            return true;
        }

        /// <summary>
        /// Reserve the approach as well as the endpoint. A clear shoulder is
        /// useless if reaching it cuts through another defender or across the leg
        /// another defender is driving.
        ///
        /// What this must NOT do is refuse everything. The first version compared
        /// the two legs' midpoints against half of their summed length, which is
        /// larger than the 45 m column gap as soon as the defenders start bounding
        /// towards the shooting - so every candidate was rejected exactly when the
        /// convoy was supposed to spread out, and every vehicle stayed on the
        /// road. This measures the real distance between the two legs instead.
        /// </summary>
        static bool DeployLane(Unit self, Vector3 target)
        {
            Vector3 start = self.Car.transform.position;
            Vector3 leg = target - start; leg.y = 0f;
            if (leg.sqrMagnitude < 0.01f) return false;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.ConvoyId != self.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                Vector3 offset = other.Car.transform.position - start; offset.y = 0f;
                float along = Mathf.Clamp01(Vector3.Dot(offset, leg) / leg.sqrMagnitude);
                if ((offset - leg * along).sqrMagnitude < 144f) return false;
                // A defender without a position of its own is standing still, and
                // standing still is already covered by the hull distance above.
                if (!other.Deploy || !other.DeployPosted) continue;
                if (LegGap(start, target, other.Car.transform.position,
                           other.DeployTarget) < LaneClear) return false;
            }
            return true;
        }

        /// <summary>The smallest flat distance between two drive legs, sampled in
        /// five steps along each. Exact enough at convoy spacing: two legs that
        /// really cross come out well under <see cref="LaneClear"/> even when the
        /// crossing itself falls between two samples.</summary>
        static float LegGap(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            float best = float.MaxValue;
            for (int i = 0; i <= 4; i++)
            {
                Vector3 a = Vector3.Lerp(a0, a1, i / 4f);
                for (int k = 0; k <= 4; k++)
                {
                    float d = FlatDistance(a, Vector3.Lerp(b0, b1, k / 4f));
                    if (d < best) best = d;
                }
            }
            return best;
        }

        // Braking applies to free drivers AND deploying defenders, including
        // wrecks. Ghosted collision pairs must never mean overlapping hulls.
        static bool ConvoyBlocked(Unit self, Vector3 direction)
        {
            if (self.ConvoyId == 0) return false;
            direction.y = 0f;
            direction = direction.normalized;
            Vector3 pos = self.Car.transform.position;
            float speed = Velocity(self.Body).magnitude;
            float stop = Mathf.Max(18f, 12f + speed * 1.5f + speed * speed / 8f);
            // While this vehicle is driving AROUND a burning mate, braking for
            // that mate is the one thing it must not do - it would stop halfway
            // past the wreck and stand there. ConvoySeparate keeps the hulls
            // apart in the meantime; a LIVING mate still stops it.
            bool passing = Time.time < self.BypassUntil;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == self || other.ConvoyId != self.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                if (passing && other.Died > 0f) continue;
                Vector3 delta = other.Car.transform.position - pos;
                delta.y = 0f;
                float ahead = Vector3.Dot(delta, direction);
                // Predict crossing traffic too, not just hulls already inside
                // the forward corridor. Only the later slot yields at a future
                // crossing; both stop for an immediate collision.
                Vector3 relative = Velocity(other.Body) - direction * Mathf.Max(speed, 3f);
                relative.y = 0f;
                float closing = Vector3.Dot(delta, relative);
                float when = relative.sqrMagnitude < 0.01f ? 0f
                    : Mathf.Clamp(-closing / relative.sqrMagnitude, 0f, 2f);
                Vector3 nearest = delta + relative * when;
                if (closing < 0f && nearest.sqrMagnitude < 144f
                    && (delta.sqrMagnitude < 324f || other.Died > 0f
                        || Velocity(other.Body).sqrMagnitude < 1f
                        || self.ColumnIndex > other.ColumnIndex))
                {
                    Roll(self.Body, Vector3.zero);
                    return true;
                }
                if (ahead <= 0f || ahead >= stop) continue;
                Vector3 lateral = delta - direction * ahead;
                if (lateral.sqrMagnitude < 144f)
                {
                    if (ahead < 18f) Roll(self.Body, Vector3.zero);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Is another hull of this convoy - burning or not - standing between the
        /// defender and the position it was given?
        ///
        /// Only the LEG counts. The general brake (<see cref="ConvoyBlocked"/>)
        /// looks a stopping distance down the nose, which for a vehicle swinging
        /// 20 m sideways is most of the old column line: it braked for the mate it
        /// was driving away from, and because it never got moving it never turned
        /// away either.
        /// </summary>
        static bool LegBlocked(Unit u, Vector3 target)
        {
            Vector3 start = u.Car.transform.position;
            Vector3 leg = target - start;
            leg.y = 0f;
            float len = leg.magnitude;
            if (len < 0.01f) return false;
            Vector3 dir = leg / len;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == u || other.ConvoyId != u.ConvoyId
                    || other.Car == null || other.Arrived) continue;
                Vector3 offset = other.Car.transform.position - start;
                offset.y = 0f;
                float ahead = Vector3.Dot(offset, dir);
                if (ahead <= 0f || ahead > len + 6f) continue;
                if ((offset - dir * ahead).sqrMagnitude < 121f) return true;
            }
            return false;
        }

        static void DeployStep(Unit u)
        {
            Transform t = u.Car.transform;
            Vector3 delta = u.DeployTarget - t.position;
            delta.y = 0f;
            if (delta.sqrMagnitude <= 16f)
            {
                HoldStill(u);
                Roll(u.Body, Vector3.zero);
                if (u.Truck) UnloadCrew(u);
                u.DeployTries = 0;      // it got there: the ring starts over
                u.Stuck = 0f;           // and the next bound starts with a clean timer
                DeployFight(u, t);
                return;
            }
            // Brake for what is in the way of THIS leg, not for whatever happens
            // to lie off the nose. A defender swinging out sideways still has the
            // vehicle ahead of it in the old column line: braking for that one
            // stops it before it has turned, and a stopped vehicle never turns
            // either - the two of them then stand in the road until the event is
            // over. A hull that really is in the way costs this position, not the
            // whole event, so it asks for another one within seconds.
            if (LegBlocked(u, u.DeployTarget))
            {
                HoldStill(u);
                Roll(u.Body, Vector3.zero);
                u.Stuck += Time.fixedDeltaTime;
                if (u.Stuck > BlockedPost) DeployGiveUp(u, t);
                return;
            }
            Vector3 local = t.InverseTransformPoint(u.DeployTarget);
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            // If the vehicle overshot, stop and fight instead of circling back
            // through the formation. No waypoint recovery can teleport it.
            // DeployPost only hands out positions inside PostCone, so this fires
            // on a position that DRIFTED behind, never on a fresh one.
            if (Mathf.Abs(angle) > DeployCone)
            {
                DeployGiveUp(u, t);
                return;
            }
            float gas, brake;
            Throttle(Mathf.Min(12f, delta.magnitude), Velocity(u.Body).magnitude * 3.6f,
                out gas, out brake);
            SetFloat(u.Rcc, "gasInput", gas);
            SetFloat(u.Rcc, "brakeInput", brake);
            SetFloat(u.Rcc, "steerInput", Mathf.Clamp(angle / FullLockAt, -1f, 1f));
            SetFloat(u.Rcc, "handbrakeInput", 0f);
            if (Velocity(u.Body).magnitude < 0.8f) u.Stuck += Time.fixedDeltaTime;
            else u.Stuck = 0f;
            if (u.Stuck > 8f) DeployGiveUp(u, t);
        }

        /// <summary>The position could not be reached. Stand and fight where the
        /// vehicle is, but do NOT settle for it: ask for another one shortly, or
        /// a defender that was blocked once spends the whole event in the middle
        /// of the road, which is what the column used to look like.</summary>
        static void DeployGiveUp(Unit u, Transform t)
        {
            u.DeployTarget = t.position;
            u.DeployPosted = false;
            u.DeployRetry = Time.time + DeployRetryEvery;
            u.DeployTries++;
            u.Stuck = 0f;
        }

        /// <summary>
        /// A defender that has reached its position: put the hull on the threat,
        /// and take the next bound towards it while it is still far away.
        ///
        /// The vehicle is standing on its brakes, so the heading is eased by hand
        /// - the same thing PlaceInColumn does with a carried hull. Turning the
        /// hull matters beyond looks: the gun traverses from where the hull
        /// points, and the armour policy is written for a vehicle facing what
        /// shoots at it.
        /// </summary>
        static void DeployFight(Unit u, Transform t)
        {
            Vector3 threat;
            if (!ConvoyThreat(u, out threat)) return;
            Vector3 to = threat - t.position;
            to.y = 0f;
            if (to.sqrMagnitude < 4f) return;

            Quaternion want = Quaternion.LookRotation(to.normalized, t.up);
            if (Quaternion.Angle(t.rotation, want) > 3f)
                t.rotation = Quaternion.RotateTowards(t.rotation, want,
                    DeployTurnRate * Time.fixedDeltaTime);

            // Press the attack. DeployPost carries the whole candidate ring a
            // bound closer when the threat is beyond EngageWithin, and refuses to
            // go further than Convoy/EngageAdvanceMetres from where the column
            // broke, so this closes in without ever becoming a chase.
            if (!u.DeployPosted || Time.time < u.DeployRetry) return;
            if (to.magnitude <= EngageWithin) return;
            if (FlatDistance(u.DeployAnchor, t.position)
                >= RevivalConvoy.EngageAdvance - 10f) return;
            u.DeployPosted = false;
            u.DeployRetry = Time.time + EngagePause;
        }

        static void UnloadCrew(Unit u)
        {
            if (u.CrewOut || u.CrewSize <= 0) return;
            // The men of a technical are already ON it, and have been since it
            // was armed (RevivalTechnicalCrew.cs). They are released where they
            // stand instead of being replaced: spawning the wreck crew here as
            // well would put six men on the ground beside a three-seat truck,
            // and the three who were visible a frame earlier would have to
            // vanish to do it. Marking the vehicle empty keeps the rest of the
            // crew logic - Besetzt, CrewedSide - honest about it.
            if (TechnicalCrew.ReleaseRiders(u.Car, u.Seite))
            {
                u.CrewOut = true;
                return;
            }
            u.CrewOut = true;
            List<RevivalComposition.CrewMan> crew = u.CrewSnapshot;
            Crew.Aussteigen(u.Car, u.Vgs, u.CrewSize, u.Tank, u.Seite, crew);
        }

        /// <summary>Remove every vehicle of one convoy - living stragglers and
        /// lingering wrecks alike - from the road. Called when the next convoy
        /// spawns (the wrecks linger until then) or when a convoy is written
        /// off as escaped/despawned.</summary>
        internal static void ConvoyClearAll(int convoyId)
        {
            _columns.Remove(convoyId);     // NDR convoy column: state goes with it
            int removed = 0;
            for (int i = _units.Count - 1; i >= 0; i--)
            {
                Unit u = _units[i];
                if (u.ConvoyId != convoyId) continue;
                Forget(u);
                Weg(u.Car);
                _units.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
                RevivalPlugin.L.LogInfo("Convoy " + convoyId + ": " + removed
                    + " vehicle(s)/wreck(s) cleared from the road.");
        }

        /// <summary>The names of every route marked as a convoy route (kind=
        /// convoy) with enough waypoints to drive ON THE MAP THAT IS LOADED. The
        /// convoy event picks from these, so a convoy road of another region
        /// never sends a column down a road that is not in this world.</summary>
        internal static List<string> ConvoyRouteNames()
        {
            Load(false);
            List<string> names = new List<string>();
            for (int i = 0; i < _order.Count; i++)
            {
                Route r;
                if (_routes.TryGetValue(_order[i], out r) && r != null
                    && r.Here && r.IsConvoy && r.Enabled && r.P.Count >= 3)
                    names.Add(r.Name);
            }
            return names;
        }

        /// <summary>The world positions of a route's ORIGINAL waypoints (not the
        /// out-and-back copy). The convoy event reads these to choose a spawn
        /// waypoint at the right distance from the player and to name the map
        /// square. Indices line up with the forward leg the spawned vehicles
        /// drive. Null if the route is unknown.</summary>
        internal static List<Vector3> ConvoyRoutePoints(string name)
        {
            Load(false);
            Route r;
            if (!_routes.TryGetValue(name, out r) || r == null) return null;
            List<Vector3> pts = new List<Vector3>(r.P.Count);
            for (int i = 0; i < r.P.Count; i++) pts.Add(r.P[i].Pos);
            return pts;
        }

        /// <summary>The driven length of a route in metres. The convoy event
        /// needs it to keep a start line-up from running off the end of a short
        /// road. 0 when the route is unknown.</summary>
        internal static float ConvoyRouteLength(string name)
        {
            Load(false);
            Route r;
            if (!_routes.TryGetValue(name, out r) || r == null || r.P.Count < 2)
                return 0f;
            Metrics(r);
            return r.Length;
        }

        // =====================================================================
        //  NDR convoy COLUMN (the formation lock)
        //
        //  WHY THIS EXISTS. Up to 6.8.3 every convoy vehicle was an independent
        //  RCC car chasing the same waypoints. Five independent cars on one road
        //  never stay a column: one wedges on a kerb, the recovery teleport moves
        //  it 30 m at a time, and the others drive on. The recorded evidence is
        //  in the 6.8.3 log - five vehicles at waypoints 0, 2, 5 and 7 at the
        //  same moment, each freeing itself on its own clock. The editor order
        //  was gone within seconds of the spawn.
        //
        //  WHAT IT DOES INSTEAD. An intact convoy is not driven, it is CARRIED.
        //  The column has one number: Arc, the distance in metres its head has
        //  travelled along the recorded line. Vehicle k sits at Arc - k * gap on
        //  that same line, every physics step, put there by hand. That makes the
        //  three things the user asked for true by construction and not by luck:
        //
        //    - the column drives as one body, at one speed,
        //    - the editor order can not change, because a slot IS the order,
        //    - the spacing is exact, because it is a subtraction.
        //
        //  Nothing about the vehicles themselves changes: they are still shot,
        //  still burn, still carry their crew and their loot, and their turrets
        //  still track and fire while the column rolls (the gun runs on its own
        //  frame-rate tick and does not care how the hull is moved).
        //
        //  WHEN IT STOPS. The moment a convoy vehicle is destroyed the column is
        //  broken for good (ColumnBreak) and every survivor goes back to driving
        //  itself under the ordinary patrol driver, which is what the behaviour
        //  layer in RevivalConvoy expects - hold and search around the wreck, one
        //  escapee driving on. That is exactly the line the user drew: in
        //  formation until the first loss, free after it.
        // =====================================================================

        /// <summary>The shared state of one convoy column.</summary>
        class Column
        {
            public int Id;           // the convoy id this column belongs to
            public float Arc;        // metres the head has travelled
            public float Speed;      // current column speed in m/s
            public bool Broken;      // a vehicle was lost - never re-forms
            public float Ready;      // Time.time the last member finished arming
            public float NextLog;    // Time.time of the next formation report
        }

        static readonly Dictionary<int, Column> _columns = new Dictionary<int, Column>();
        static readonly Dictionary<int, List<Unit>> _columnGroups =
            new Dictionary<int, List<Unit>>();
        static readonly List<int> _columnDrop = new List<int>();

        /// <summary>Front to tail. The unit list is in spawn order, which is the
        /// same thing today, but the slot is the authority on who drives where -
        /// the editor's order must not depend on a list happening to agree.</summary>
        static readonly Comparison<Unit> _bySlot = new Comparison<Unit>(CompareSlot);

        static int CompareSlot(Unit a, Unit b)
        {
            return a.ColumnIndex.CompareTo(b.ColumnIndex);
        }

        /// <summary>Metres per second squared the column works up to its cruise
        /// speed with. A column that jumped to full speed in one step would tear
        /// its own tail off the start line on the first frame.</summary>
        const float ColumnAccel = 5f;

        /// <summary>Seconds the formed-up column waits on the start line after
        /// the last vehicle is armed, so the whole column rolls off together.</summary>
        const float ColumnSettle = 1.5f;

        /// <summary>Degrees per second the hull heading is eased towards the
        /// direction of the road. Turning it instantly would snap the vehicle
        /// sideways at every waypoint of a dense recording.</summary>
        const float ColumnTurnRate = 150f;

        /// <summary>Arc length of a route, built once. Cum[i] is the flat
        /// distance from waypoint 0 to waypoint i.</summary>
        static void Metrics(Route r)
        {
            if (r == null) return;
            int n = r.P.Count;
            if (n == 0) return;
            if (r.Cum != null && r.Cum.Length == n) return;
            float[] cum = new float[n];
            float sum = 0f;
            cum[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                sum += FlatDistance(r.P[i - 1].Pos, r.P[i].Pos);
                cum[i] = sum;
            }
            r.Cum = cum;
            r.Length = sum;
        }

        /// <summary>The point <paramref name="arc"/> metres along the recorded
        /// line, and the index of the waypoint it is driving towards. Clamped to
        /// both ends, so a caller never has to range-check first.</summary>
        static Vector3 PointOnRoute(Route r, float arc, out int seg)
        {
            Metrics(r);
            int n = r.P.Count;
            seg = n > 1 ? 1 : 0;
            if (n == 0) return Vector3.zero;
            if (n == 1) return r.P[0].Pos;
            if (arc <= 0f) return r.P[0].Pos;
            if (arc >= r.Length) { seg = n - 1; return r.P[n - 1].Pos; }

            int i = 1;
            while (i < n - 1 && r.Cum[i] < arc) i++;
            seg = i;
            float len = r.Cum[i] - r.Cum[i - 1];
            float t = len > 0.001f ? (arc - r.Cum[i - 1]) / len : 0f;
            return Vector3.Lerp(r.P[i - 1].Pos, r.P[i].Pos, Mathf.Clamp01(t));
        }

        /// <summary>The direction of travel at <paramref name="arc"/>, measured
        /// over a span of the line instead of over one leg. A dense recording has
        /// legs of a few metres, and a heading taken from one of those turns the
        /// hull with every waypoint.</summary>
        static Vector3 HeadingOnRoute(Route r, float arc)
        {
            int ignore;
            Vector3 back = PointOnRoute(r, arc - 7f, out ignore);
            Vector3 fwd = PointOnRoute(r, arc + 7f, out ignore);
            Vector3 d = fwd - back;
            d.y = 0f;
            if (d.sqrMagnitude > 0.0001f) return d.normalized;
            d = PointOnRoute(r, arc + 25f, out ignore)
              - PointOnRoute(r, arc, out ignore);
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
        }

        /// <summary>Metres from the hull origin down to the lowest point of the
        /// model - what has to be added to a road surface so the vehicle stands
        /// on it. Measured from the renderers, because the prefabs disagree about
        /// where their origin sits (the Ural has it near the wheel bottoms, the
        /// tank does not).</summary>
        static float HullDrop(GameObject car)
        {
            if (car == null) return 1f;
            Renderer[] rs = car.GetComponentsInChildren<Renderer>(true);
            float lowest = float.MaxValue;
            for (int i = 0; i < rs.Length; i++)
            {
                if (!(rs[i] is MeshRenderer) && !(rs[i] is SkinnedMeshRenderer)) continue;
                Bounds b = rs[i].bounds;
                if (b.size.sqrMagnitude < 0.0001f) continue;
                if (b.min.y < lowest) lowest = b.min.y;
            }
            if (lowest == float.MaxValue) return 1f;
            return Mathf.Clamp(car.transform.position.y - lowest, 0.05f, 3f);
        }

        /// <summary>Prefer the local road deck, including a tunnel floor. If
        /// the recorded line is buried, retry from progressively higher origins.
        /// A failed ray must not permanently pin the column under the terrain.
        /// Never use another vehicle or a character as ground.</summary>
        static bool RoadUnder(Vector3 point, Transform own, out float y,
                              out Vector3 normal)
        {
            y = point.y;
            normal = Vector3.up;
            bool legacy = point.y < 10f;
            for (int pass = 0; pass < (legacy ? 1 : 5); pass++)
            {
                float rise = 3f * (1 << pass);
                Vector3 from = point + Vector3.up * rise;
                if (legacy) from.y = 2500f;
                float rest = legacy ? 3000f : rise * 4f;
                for (int step = 0; step < 16 && rest > 0.1f; step++)
                {
                    Vector3 hit, hitNormal;
                    GameObject go = Turret.RaycastObject(from, Vector3.down, rest,
                                                         out hit, out hitNormal);
                    if (go == null) break;
                    bool mine = own != null && go.transform.IsChildOf(own);
                    bool nearRoad = legacy || pass > 0 || hit.y <= point.y + 1.5f;
                    if (!mine && nearRoad && !Lebendig(go.transform) && hitNormal.y >= 0.35f
                        && IsDriveSurface(go, hitNormal))
                    {
                        y = hit.y;
                        normal = hitNormal.normalized;
                        return true;
                    }
                    float used = Mathf.Max(0.2f, from.y - hit.y + 0.2f);
                    rest -= used;
                    from = hit + Vector3.down * 0.2f;
                }
            }
            return false;
        }

        /// <summary>Measure the support footprint once in the level spawn pose.
        /// Use mesh-local bounds so world-axis boxes do not grow with yaw.
        /// Only geometry reaching the lower hull contributes: turret, barrel
        /// and particle effects must not widen the ground contact rectangle.</summary>
        static void ColumnFootprint(Unit u)
        {
            u.ColumnLift = HullDrop(u.Car);
            float halfLength = 1f, halfWidth = 0.5f;
            Renderer[] rs = u.Car.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer renderer = rs[i];
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;
                if (renderer.bounds.min.y > u.Car.transform.position.y - u.ColumnLift + 0.5f)
                    continue;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                MeshFilter mesh = renderer.GetComponent<MeshFilter>();
                if (skinned == null && (mesh == null || mesh.sharedMesh == null)) continue;
                Bounds b = skinned != null ? skinned.localBounds : mesh.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 vertex = b.center + new Vector3(
                        (corner & 1) == 0 ? -b.extents.x : b.extents.x,
                        (corner & 2) == 0 ? -b.extents.y : b.extents.y,
                        (corner & 4) == 0 ? -b.extents.z : b.extents.z);
                    Vector3 local = u.Car.transform.InverseTransformPoint(
                        renderer.transform.TransformPoint(vertex));
                    halfLength = Mathf.Max(halfLength, Mathf.Abs(local.z));
                    halfWidth = Mathf.Max(halfWidth, Mathf.Abs(local.x));
                }
            }
            u.ColumnHalfLength = halfLength;
            u.ColumnHalfWidth = halfWidth;
        }

        /// <summary>Put one column vehicle on its slot: exact position on the
        /// recorded line, standing on the road, facing the way the road runs.
        /// The vehicle is not asked to drive there - it is put there.</summary>
        static void PlaceInColumn(Unit u, Route r, float arc, float speed)
        {
            int seg;
            Vector3 line = PointOnRoute(r, arc, out seg);
            Vector3 dir = HeadingOnRoute(r, arc);
            Carry(u, line, dir, speed,
                  "Convoy " + u.ConvoyId + ": ground slot " + u.ColumnIndex);
            u.Stuck = 0f;
            u.Next = seg;
        }

        /// <summary>Put a hull on one point of a recorded line by hand: standing
        /// on the surface under it, facing the way the road runs there, with its
        /// wheels turning at carry speed instead of sliding along locked.
        ///
        /// This is the ONE placement in this class that moves a vehicle every
        /// physics step, and both carriers use it: the convoy column for each
        /// slot of an intact column, and the patrol rail for a vehicle that has
        /// proved it cannot drive this stretch itself. <paramref name="who"/>
        /// names the caller in the ground report, which is the only line that
        /// tells the two apart.</summary>
        static void Carry(Unit u, Vector3 line, Vector3 dir, float speed, string who)
        {
            if (u.ColumnLift <= 0f) ColumnFootprint(u);

            float y;
            Vector3 normal;
            bool found = RoadUnder(line, u.Car.transform, out y, out normal);
            // THE LOOKUP STARTS AT THE RECORDED LINE, AND THE LINE CAN BE FAR
            // BELOW THE GROUND. RoadUnder rises at most 48 m over the point it
            // is given before it casts down, so a route whose y is more than
            // that under the real surface answers "nothing here" - and the rule
            // further down then lets the hull KEEP the height it already has.
            // For a vehicle that is being carried for good, that is a hull
            // flying along its own route: the run of 2026-09-22 has one on R5 at
            // hullY 517 over a routeY of 433, found=False, lap after lap, while
            // the terrain it is drawn over lies 80 m lower. So before the hull
            // is allowed to keep its height, the same lookup is asked once more
            // FROM THAT HEIGHT, at the line's own x and z. One extra probe, only
            // on a step that has already failed, and only when the hull is not
            // where the line says it should be anyway.
            if (!found && Mathf.Abs(u.Car.transform.position.y - line.y) > 3f)
                found = RoadUnder(new Vector3(line.x, u.Car.transform.position.y,
                                              line.z),
                                  u.Car.transform, out y, out normal);
            if (!found) { y = line.y; normal = Vector3.up; }
            Vector3 flat = dir - normal * Vector3.Dot(dir, normal);
            if (flat.sqrMagnitude < 0.0001f) { flat = dir; normal = Vector3.up; }
            Quaternion want = Quaternion.LookRotation(flat.normalized, normal);
            Transform t = u.Car.transform;
            Quaternion rotation = !u.Placed ? want : Quaternion.RotateTowards(
                t.rotation, want, ColumnTurnRate * Time.fixedDeltaTime);

            // Compute clearance for the ACTUAL eased rotation. Raising only the
            // origin by a fixed lift leaves the nose/tail buried on a slope.
            Vector3 bottom = rotation * new Vector3(0f, -u.ColumnLift, 0f);
            float targetY = y - bottom.y + 0.15f;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 offset = rotation * new Vector3(
                    (corner & 1) == 0 ? -u.ColumnHalfWidth : u.ColumnHalfWidth,
                    -u.ColumnLift,
                    (corner & 2) == 0 ? -u.ColumnHalfLength : u.ColumnHalfLength);
                Vector3 probe = new Vector3(line.x + offset.x, line.y, line.z + offset.z);
                float supportY;
                Vector3 supportNormal;
                if (RoadUnder(probe, t, out supportY, out supportNormal))
                {
                    targetY = Mathf.Max(targetY, supportY - offset.y + 0.15f);
                    found = true;
                }
            }
            // Missing collision data must not pull a grounded hull down into an
            // untrusted route. Keep moving; retry at the next slot. This holds
            // for the FIRST placement too: the spawn only puts a vehicle where
            // the same lookup found a surface (SpawnConvoyUnit), so the height
            // it already stands at is measured, while the route's own y is
            // whatever the recorder's camera was at.
            if (!found) targetY = Mathf.Max(targetY, t.position.y);
            Vector3 target = new Vector3(line.x, targetY, line.z);
            if (Time.time >= u.ColumnGroundLog
                && (!found || Mathf.Abs(y - line.y) > 3f
                    || (u.Placed && Mathf.Abs(t.position.y - targetY) > 2f)))
            {
                u.ColumnGroundLog = Time.time + 10f;
                RevivalPlugin.L.LogInfo(who + " routeY=" + line.y.ToString("0.0")
                    + " surfaceY=" + y.ToString("0.0") + " hullY="
                    + targetY.ToString("0.0") + " found=" + found);
            }
            t.rotation = rotation;
            t.position = target;
            u.Placed = true;

            // The hull is moved by hand, so no throttle and no steering lock
            // may still act on it. The velocity is not zeroed but SET to what the
            // column is doing, so the wheels turn and the tank tracks scroll at
            // road speed instead of the whole column sliding on locked wheels.
            Roll(u.Body, flat.normalized * speed);
            if (u.Rcc != null)
            {
                SetFloat(u.Rcc, "gasInput", 0f);
                SetFloat(u.Rcc, "brakeInput", 0f);
                SetFloat(u.Rcc, "steerInput", 0f);
                SetFloat(u.Rcc, "handbrakeInput", 0f);
            }
        }

        /// <summary>How much the column eases off for the corner it is in.
        ///
        /// <see cref="CornerFactor"/> reads the leg BEFORE and the leg AFTER the
        /// waypoint and wraps both indices around the route, which is right for a
        /// patrol driving laps and wrong for a one-way column: at the last
        /// waypoint the "next" leg is the jump back to waypoint 0, an angle of
        /// almost 180 degrees, and the column would crawl the last stretch of
        /// every route at the 3 m/s floor. The two end waypoints are simply
        /// straight here.</summary>
        static float ColumnCorner(Route r, float arc)
        {
            int n = r.P.Count;
            if (n < 3) return 1f;
            int seg;
            PointOnRoute(r, arc, out seg);
            if (seg <= 0 || seg >= n - 1) return 1f;
            return CornerFactor(r, seg);
        }

        /// <summary>Register a new column and its start arc. Called once per
        /// convoy, by the first vehicle that spawns on it.</summary>
        static void ColumnStart(int convoyId, float headArc)
        {
            if (convoyId == 0) return;
            Column col;
            if (!_columns.TryGetValue(convoyId, out col))
            {
                col = new Column();
                _columns[convoyId] = col;
            }
            col.Id = convoyId;
            col.Arc = headArc;
            col.Speed = 0f;
            col.Broken = false;
            col.Ready = 0f;
            col.NextLog = 0f;
        }

        /// <summary>The column is over. Every survivor goes back to driving
        /// itself from where it stands; the behaviour layer owns them from here.
        /// Idempotent, so the loss of the second and third vehicle costs
        /// nothing.</summary>
        internal static void ColumnBreak(int convoyId, string why)
        {
            if (convoyId == 0) return;
            Column col;
            if (_columns.TryGetValue(convoyId, out col))
            {
                if (col.Broken) return;
                col.Broken = true;
            }
            int freed = 0;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.ConvoyId != convoyId || !u.Column) continue;
                u.Column = false;
                u.Stuck = 0f;
                // The break-up state starts here, not at the spawn.
                u.Blocked = 0f;
                u.BypassUntil = 0f;
                freed++;
            }
            if (freed > 0)
                RevivalPlugin.L.LogInfo("Convoy " + convoyId + ": column broken ("
                    + why + ") - " + freed + " vehicle(s) drive on by themselves.");
        }

        /// <summary>Is this convoy vehicle currently carried by its column? The
        /// convoy layer asks before it applies its own spacing holds, which a
        /// locked column does not need and must not receive.</summary>
        internal static bool ConvoyInColumn(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Column;
        }

        /// <summary>
        /// Move every intact column one physics step. Runs BEFORE the per-unit
        /// driver, so a vehicle that belongs to a column is already standing on
        /// its slot by the time the driver would have touched it - and the driver
        /// then skips it.
        /// </summary>
        static void Columns()
        {
            if (_columns.Count == 0) return;
            float dt = Time.fixedDeltaTime;

            _columnGroups.Clear();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.ConvoyId == 0 || !u.Column) continue;
                if (u.Car == null) continue;
                List<Unit> g;
                if (!_columnGroups.TryGetValue(u.ConvoyId, out g))
                {
                    g = new List<Unit>();
                    _columnGroups[u.ConvoyId] = g;
                }
                g.Add(u);
            }

            _columnDrop.Clear();
            foreach (KeyValuePair<int, Column> kv in _columns)
            {
                List<Unit> mem;
                if (!_columnGroups.TryGetValue(kv.Key, out mem) || mem.Count == 0)
                {
                    _columnDrop.Add(kv.Key);
                    continue;
                }
                if (kv.Value.Broken) continue;
                ColumnStep(kv.Value, mem, dt);
            }
            for (int i = 0; i < _columnDrop.Count; i++)
                _columns.Remove(_columnDrop[i]);
        }

        /// <summary>One step of one column: wait until everybody is armed, then
        /// roll, and put every member on its slot.</summary>
        static void ColumnStep(Column col, List<Unit> mem, float dt)
        {
            mem.Sort(_bySlot);                 // front to tail, by editor slot
            Route r = mem[0].Route;
            if (r == null || r.P.Count < 2) return;
            Metrics(r);

            bool ready = true;
            for (int k = 0; k < mem.Count; k++)
            {
                if (mem[k].Died > 0f)
                {
                    ColumnBreak(mem[k].ConvoyId, "vehicle lost");
                    return;
                }
                if (!mem[k].Armed) ready = false;
            }

            if (!ready) col.Ready = 0f;
            else if (col.Ready <= 0f) col.Ready = Time.time;

            bool rolling = ready && Time.time - col.Ready >= ColumnSettle;
            if (rolling)
            {
                float want = RevivalConvoy.CruiseSpeed / 3.6f;
                want *= ColumnCorner(r, col.Arc);
                if (want < 3f) want = 3f;
                col.Speed = Mathf.MoveTowards(col.Speed, want, ColumnAccel * dt);
                col.Arc += col.Speed * dt;
            }
            else col.Speed = 0f;

            float gap = RevivalConvoy.LineupGap;
            for (int k = 0; k < mem.Count; k++)
            {
                Unit u = mem[k];
                int slot = u.ColumnIndex < 0 ? k : u.ColumnIndex;
                float arc = col.Arc - gap * slot;

                // The head reaches the last waypoint first; the tail keeps
                // rolling until its own slot gets there. Arrive-and-vanish stays
                // in route order, exactly like the drive.
                if (arc >= r.Length) { u.Arrived = true; continue; }
                if (arc < 0f) arc = 0f;
                PlaceInColumn(u, r, arc, col.Speed);
            }

            if (rolling && Time.time >= col.NextLog)
            {
                col.NextLog = Time.time + 10f;
                ColumnReport(col, mem, r);
            }
        }

        /// <summary>
        /// What the column looks like in the WORLD, written to the log every ten
        /// seconds while it rolls: the order the vehicles are actually standing
        /// in and the real distance between each pair. This is the evidence that
        /// closes this feature without a pair of eyes in the game - if the line
        /// reads "slots 0 1 2 3 4" with even gaps for the whole drive, the convoy
        /// held its formation and its editor order.
        /// </summary>
        static void ColumnReport(Column col, List<Unit> mem, Route r)
        {
            string order = "";
            string gaps = "";
            for (int k = 0; k < mem.Count; k++)
            {
                Unit u = mem[k];
                order += (k == 0 ? "" : " ") + u.ColumnIndex
                       + (u.Tank ? "T" : "V");
                if (k > 0 && mem[k - 1].Car != null && u.Car != null)
                    gaps += (gaps.Length == 0 ? "" : " ")
                          + FlatDistance(mem[k - 1].Car.transform.position,
                                         u.Car.transform.position).ToString("0.0");
            }
            RevivalPlugin.L.LogInfo("Convoy " + col.Id + ": column at "
                + col.Arc.ToString("0") + " / " + r.Length.ToString("0") + " m, "
                + (col.Speed * 3.6f).ToString("0") + " km/h, slots " + order
                + ", gaps " + gaps + " m.");
        }

        /// <summary>
        /// Does this VehicleGameSystem belong to a patrol whose crew is still
        /// aboard? `Turret.FreeSeatPostfix` asks, and the answer decides
        /// whether a player may climb in. Cheap: an empty dictionary is the
        /// normal case and returns on the first line.
        /// </summary>
        internal static bool Besetzt(object vgs)
        {
            if (_units.Count == 0) return false;
            UnityEngine.Object o = vgs as UnityEngine.Object;
            if (o == null) return false;
            int id = o.GetInstanceID();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Vgs == null || u.Vgs.GetInstanceID() != id) continue;
                return RevivalPlugin.CfgPatrolCrew.Value && !u.CrewOut && u.CrewSize > 0;
            }
            return false;
        }

        /// <summary>
        /// The side (Fraktion name) of a patrol or convoy vehicle whose crew is
        /// still aboard, or null for anything else. An active AI vehicle keeps
        /// its side when dismounted crew spawning is disabled in the config.
        /// </summary>
        internal static string CrewedSide(Component vgs)
        {
            if (vgs == null || _units.Count == 0) return null;
            int id = vgs.GetInstanceID();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Vgs == null || u.Vgs.GetInstanceID() != id) continue;
                return u.Died <= 0f && !u.CrewOut && (u.Armed || u.CrewSize > 0)
                    ? u.Seite : null;
            }
            return null;
        }

        /// <summary>
        /// The EDITOR LOADOUT of the men this vehicle carries - one entry per
        /// role, with the main weapon and the uniform the admin chose - or null
        /// when the route has no composition and the men are dressed by the
        /// config instead. The head count is NOT this list's length: a vehicle
        /// is manned by its seats and the roles repeat around them (Besatzung).
        ///
        /// The sibling of <see cref="CrewedSide"/>, and there for the same
        /// reason: a feature that puts this vehicle's men somewhere other than
        /// on the ground at its wreck - the technical's riding crew
        /// (RevivalTechnicalCrew.cs) - has to dress them the way the wreck crew
        /// would have been dressed, or the same men change clothes when the
        /// truck burns.
        /// </summary>
        internal static List<RevivalComposition.CrewMan> CrewedList(Component vgs)
        {
            if (vgs == null || _units.Count == 0) return null;
            int id = vgs.GetInstanceID();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Vgs == null || u.Vgs.GetInstanceID() != id) continue;
                return u.CrewSnapshot;
            }
            return null;
        }

        /// <summary>How many men this vehicle is manned by right now, or 0 when
        /// it is not one of ours or its crew is already on the ground. The
        /// riding crew asks, so it puts exactly as many men on a technical as
        /// would otherwise have climbed out of it.</summary>
        internal static int CrewedCount(Component vgs)
        {
            if (vgs == null || _units.Count == 0) return 0;
            int id = vgs.GetInstanceID();
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Vgs == null || u.Vgs.GetInstanceID() != id) continue;
                return u.CrewOut ? 0 : u.CrewSize;
            }
            return 0;
        }

        static MethodInfo _photonDestroy;
        static bool _photonDestroyLookedUp;

        /// <summary>
        /// Take a patrol vehicle out of the world - on EVERY client.
        ///
        /// It was put there with `PhotonNetwork.InstantiateSceneObject`
        /// (CarSpawn), so a plain `Object.Destroy` removes it on this machine
        /// and leaves a BTR standing on the road of every other one. Photon's
        /// own Destroy is only allowed to the master client, which is the only
        /// machine this class runs on anyway; if it is not to be had, the
        /// local Destroy is still better than a vehicle that stays.
        /// </summary>
        static void Weg(GameObject car)
        {
            if (car == null) return;
            if (!_photonDestroyLookedUp)
            {
                _photonDestroyLookedUp = true;
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon != null)
                    _photonDestroy = AccessTools.Method(photon, "Destroy",
                        new Type[] { typeof(GameObject) }, null);
                if (_photonDestroy == null)
                    RevivalPlugin.L.LogWarning("Patrol: PhotonNetwork.Destroy(GameObject) "
                        + "not found - a removed patrol vehicle stays standing on the "
                        + "other clients.");
            }
            if (_photonDestroy != null)
            {
                try
                {
                    _photonDestroy.Invoke(null, new object[] { car });
                    return;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Patrol: PhotonNetwork.Destroy refused ("
                        + ex.Message + ") - removing it locally instead.");
                }
            }
            UnityEngine.Object.Destroy(car);
        }

        /// <summary>Give a vehicle back to the game: out of the owner list, so
        /// its sleep mode works again the moment we stop steering it.</summary>
        static void Forget(Unit u)
        {
            if (u.Vgs == null) return;
            int id = u.Vgs.GetInstanceID();
            if (_owned.ContainsKey(id)) _owned.Remove(id);
        }

        /// <summary>
        /// NDR convoy repair seam (RevivalConvoyRepair.cs). The player has
        /// repaired a destroyed patrol vehicle, so Patrol must stop managing it:
        /// out of the owner list AND out of the unit list, so no despawn timer
        /// runs and no driver steers it. It is left standing in the world as an
        /// ordinary vehicle - NOT destroyed. Returns true if this car was one of
        /// ours. Additive; touches no existing line.
        /// </summary>
        internal static bool ReleaseRepaired(GameObject car)
        {
            if (car == null) return false;
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i].Car != car) continue;
                Forget(_units[i]);
                _units.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Put one patrol vehicle on the route.
        ///
        /// `auto` decides WHERE, and the two answers are opposites on purpose.
        /// A patrol asked for with the key should be visible at once, so it
        /// starts at the nearest waypoint that is not on top of the man who
        /// pressed it. A patrol the automatic puts out should NOT appear in
        /// front of anybody - it starts as far from the player and from every
        /// other patrol as the route allows, and drives to him.
        /// </summary>
        static void Spawn(Route src, bool auto)
        {
            Route r = OutAndBack(src);

            int start = 0;
            for (int i = 0; i < r.P.Count; i++)
                if (HasFlag(r.P[i], "spawn")) { start = i; break; }

            Vector3 me = Where();
            float away = -1f;

            int pick = auto ? Verteilt(r, me) : -1;
            if (pick >= 0)
            {
                start = pick;
                if (me != Vector3.zero) away = Flat(r.P[start].Pos - me);
            }
            // The spawn flag sits where the RECORDING began, which can be a
            // kilometre from where the key is pressed - and a patrol nobody
            // ever sees is indistinguishable from one that never started. So
            // it starts at the nearest waypoint instead, but not one so close
            // that the vehicle lands on the man watching.
            else if (me != Vector3.zero)
            {
                int near = -1;
                float best = 0f;
                for (int i = 0; i < r.P.Count; i++)
                {
                    float d = Flat(r.P[i].Pos - me);
                    if (d < 20f) continue;
                    if (near < 0 || d < best) { near = i; best = d; }
                }
                if (near >= 0 && best <= 400f) { start = near; away = best; }
                else
                    RevivalPlugin.L.LogInfo("Patrol: no waypoint of " + r.Name
                        + " within 400 m - starting at waypoint " + start
                        + ", where the recording began.");
            }

            // Nothing is put down where there is nothing to stand on. A patrol
            // dropped into a place whose colliders are not loaded falls through
            // the world, and the automatic that replaces it drops the next one
            // into the same hole - which is the loop this guard ends. The
            // automatic starts patrols at the waypoint FARTHEST from every
            // player (Verteilt), so this is not a rare case.
            Vector3 startPos;
            int firm = GroundedWaypoint(r, start, 1.6f, true, out startPos);
            if (firm < 0)
            {
                RevivalPlugin.L.LogWarning("Patrol: no ground under waypoint "
                    + start + " of " + r.Name + " or the " + GroundTries
                    + " after it - nothing is put down there. The colliders "
                    + "around that stretch are probably not loaded; the "
                    + "automatic tries again later.");
                return;
            }
            if (firm != start)
            {
                RevivalPlugin.L.LogInfo("Patrol: waypoint " + start + " of "
                    + r.Name + " has nothing under it - starting at waypoint "
                    + firm + " instead.");
                start = firm;
                away = -1f;
            }

            Vector3 ahead = r.P[(start + 1) % r.P.Count].Pos - r.P[start].Pos;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;
            ahead.Normalize();

            RevivalComposition.Composition composition = RevivalComposition.Of(r.Name);
            int vehicleCount = composition == null || composition.Vehicles.Count == 0
                ? 1 : composition.Vehicles.Count;
            int max = PatrolVehicleLimit();
            if ((long)PatrolUnitCount() + vehicleCount > max)
            {
                RevivalPlugin.L.LogWarning("Patrol: editor composition on " + r.Name
                    + " needs " + vehicleCount + " vehicle slots, but only "
                    + (max - PatrolUnitCount()) + " remain under patrol capacity.");
                return;
            }

            int groupId = _nextPatrolGroupId++;
            List<Unit> made = new List<Unit>();
            for (int k = 0; k < vehicleCount; k++)
            {
                // Per-vehicle kind: the editor composition names each vehicle
                // (tank/BTR/Ural); without a composition the route's own kind
                // (WagenKind, Ural-aware, "mixed" still alternates) applies.
                // VehicleRegistry.Spawn reports whether the result counts as a
                // tank so Unit.Tank stays correct without hard-coding the mapping.
                string kind = composition == null
                    ? WagenKind(r) : composition.Vehicles[k].Kind;
                // A configured patrol is a small road column. It keeps the
                // ordinary patrol route behaviour, but starts front-to-tail
                // BACK ALONG THE RECORDED ROUTE instead of on a straight line
                // drawn backwards from the head: that line leaves the road at
                // the first bend, and at 45 m a slot the third vehicle of a
                // composition was put down ninety metres into whatever happened
                // to be there - a field, a yard, a wood. Every slot also drives
                // to the waypoint at the far end of ITS OWN leg, or the ones at
                // the back would aim straight at the head's next corner and cut
                // the road between.
                Vector3 spot = r.P[start].Pos;
                Vector3 face = ahead;
                int firstWaypoint = (start + 1) % r.P.Count;
                if (k > 0)
                {
                    Vector3 back;
                    Vector3 dir;
                    int nextWaypoint;
                    if (LineupSlot(r, start, RevivalConvoy.LineupGap * k,
                                   out back, out dir, out nextWaypoint))
                    {
                        spot = back;
                        face = dir;
                        firstWaypoint = nextWaypoint;
                    }
                    else
                    {
                        // A route too short to walk back that far. The straight
                        // line is all there is; the driver closes the gap.
                        spot = r.P[start].Pos - ahead * (RevivalConvoy.LineupGap * k);
                    }
                }
                Vector3 pos;
                if (!GroundSpot(spot, null, 1.6f, out pos))
                {
                    // The slot behind the start has no surface of its own (a
                    // bridge gap, an unloaded prop). The start waypoint HAS one
                    // and is at most a few dozen metres away, so its height is
                    // the honest guess - an authored y is not.
                    pos = new Vector3(spot.x, startPos.y, spot.z);
                    RevivalPlugin.L.LogInfo("Patrol: no ground under line-up slot "
                        + k + " on " + r.Name + " - it stands at the height of "
                        + "waypoint " + start + ".");
                }
                bool tank;
                GameObject car = VehicleRegistry.Spawn(kind,
                    pos, Quaternion.LookRotation(face, Vector3.up), out tank);
                if (car == null)
                {
                    for (int q = made.Count - 1; q >= 0; q--)
                    {
                        Forget(made[q]);
                        Weg(made[q].Car);
                        _units.Remove(made[q]);
                    }
                    RevivalPlugin.L.LogWarning("Patrol: rolled back partial editor "
                        + "composition on " + r.Name + ".");
                    return;
                }

                Unit u = new Unit();
                u.Car = car;
                u.Route = r;
                u.Tank = tank;
                u.Seite = r.Seite;
                u.Next = firstWaypoint;
                MadeProgress(u, pos);
                u.CompositionVehicle = composition == null ? -1 : k;
                u.CrewSnapshot = composition == null ? null : RevivalComposition.CrewOf(r.Name, k);
                u.PatrolGroupId = groupId;
                u.Cols = CollectCols(car);
                for (int q = 0; q < made.Count; q++) GhostPair(u.Cols, made[q].Cols);
                _units.Add(u);
                made.Add(u);
                _spawned++;
            }

            RevivalPlugin.L.LogInfo("Patrol: " + (auto ? "automatic " : "")
                + vehicleCount + "-vehicle "
                + (composition == null ? "patrol" : "editor mini-convoy")
                + " (" + r.Seite + ") put down on " + r.Name
                + " at waypoint " + start + ", driving to "
                + ((start + 1) % r.P.Count)
                + (away >= 0f ? ", " + away.ToString("0") + " m from the player" : "")
                + ".");
            Turret.Hinweis(away >= 0f
                ? Loc.T("Патруль ", "Patrol ") + r.Name + " (" + r.Seite + "), "
                  + away.ToString("0") + Loc.T(" м от игрока", " m away")
                : Loc.T("Патруль ", "Patrol ") + r.Name + " (" + r.Seite + ")"
                  + Loc.T(" запущен", " started"), 4f);
        }

        /// <summary>
        /// The point <paramref name="back"/> metres BACK ALONG THE ROUTE from a
        /// waypoint, the direction the road runs there, and the waypoint a
        /// vehicle standing there drives to first.
        ///
        /// This is where the second and third vehicle of one composition line
        /// up. Measured in metres along the recorded legs, so the spacing is
        /// exact however the waypoints are distributed, and never off the road,
        /// which a straight line drawn backwards from the head is as soon as the
        /// road bends.
        ///
        /// False when the route is shorter than the distance asked for. It walks
        /// at most one lap; a looping route wraps, and so does the mirrored
        /// out-and-back one, because both are closed lines by the time a patrol
        /// drives them.
        /// </summary>
        static bool LineupSlot(Route r, int start, float back, out Vector3 point,
                               out Vector3 forward, out int next)
        {
            point = Vector3.zero;
            forward = Vector3.forward;
            next = start;
            int n = r.P.Count;
            if (n < 2 || back <= 0f) return false;
            int at = ((start % n) + n) % n;
            float rest = back;
            for (int step = 0; step < n - 1; step++)
            {
                int prev = (at - 1 + n) % n;
                Vector3 leg = r.P[at].Pos - r.P[prev].Pos;
                leg.y = 0f;
                float len = leg.magnitude;
                if (len < 0.01f) { at = prev; continue; }
                if (len >= rest)
                {
                    point = r.P[at].Pos - leg * (rest / len);
                    forward = leg / len;
                    next = at;
                    return true;
                }
                rest -= len;
                at = prev;
            }
            return false;
        }

        /// <summary>
        /// The waypoint an AUTOMATIC patrol starts at: the one whose nearest
        /// neighbour - the player, or another patrol already on the road - is
        /// as far away as the route allows.
        ///
        /// One number decides it, the smallest of those distances, and taking
        /// the largest of THOSE spreads the vehicles over the route by itself:
        /// with an empty road it is the far end, with one patrol out it is the
        /// opposite side, with three it is whatever gap is left. No slots, no
        /// division of the route into sectors, and nothing to keep in sync
        /// when a vehicle is lost.
        ///
        /// Returns -1 when the whole route lies inside `AutoAway` of the
        /// player. Then the caller falls back to the near rule - a patrol on a
        /// short route in front of the player is still better than none.
        /// </summary>
        static int Verteilt(Route r, Vector3 me)
        {
            int best = -1;
            float bestScore = 0f;
            for (int i = 0; i < r.P.Count; i++)
            {
                Vector3 pos = r.P[i].Pos;

                float score = 100000f;
                if (me != Vector3.zero)
                {
                    float d = Flat(pos - me);
                    if (d < AutoAway) continue;
                    score = d;
                }
                for (int k = 0; k < _units.Count; k++)
                {
                    if (_units[k].Car == null) continue;
                    float d = Flat(pos - _units[k].Car.transform.position);
                    if (d < score) score = d;
                }
                if (best < 0 || score > bestScore) { best = i; bestScore = score; }
            }
            return best;
        }

        /// <summary>How many patrols have been put down since the game
        /// started. Only "mixed" reads it, and only to alternate.</summary>
        static int _spawned;

        /// <summary>
        /// btr, tank, or mixed - and mixed ALTERNATES instead of rolling a
        /// die. Two dice throws in a row give two BTRs often enough that a
        /// player would report "the tank patrol does not work"; alternating
        /// means the second key press is always the other kind.
        /// </summary>
        static bool TankThisTime(Route r)
        {
            string want = r.Wagen;
            if (want == "tank") return true;
            if (want != "mixed") return false;
            return (_spawned % 2) == 1;
        }

        /// <summary>A route's Vehicle flag when it NAMES a registered kind that
        /// the tank/BTR pair cannot express - a truck route, a technical route.
        /// Empty for "tank", "btr", "mixed" and anything unreadable, which are
        /// the cases TankThisTime settles.</summary>
        static string NamedKind(Route r)
        {
            string w = r == null ? "mixed" : r.Wagen;
            if (w == "mixed" || w == "tank" || w == "btr") return "";
            return VehicleRegistry.Contains(w) ? w : "";
        }

        /// <summary>The registry kind a route spawns: the kind the route names
        /// when it names one - a truck route spawns Urals, a technical route gun
        /// trucks - and otherwise "tank"/"btr" resolved through
        /// <see cref="TankThisTime"/>, so "mixed" still alternates. This is the
        /// single place patrol maps a route's Vehicle flag to a registry kind,
        /// and it is the FALLBACK: a route with an editor composition is driven
        /// by that composition's per-vehicle kinds instead.</summary>
        static string WagenKind(Route r)
        {
            string named = NamedKind(r);
            if (named.Length > 0) return named;
            return TankThisTime(r) ? "tank" : "btr";
        }

        /// <summary>The vehicle kind a NAMED route requests, for the convoy event
        /// to honour (a "ural" route becomes a truck convoy, a "technical" route
        /// a column of gun trucks). Empty when the route is unknown or leaves the
        /// choice to the composition.</summary>
        internal static string RouteVehicle(string routeName)
        {
            Load(false);
            Route r;
            if (routeName != null && _routes.TryGetValue(routeName, out r) && r != null)
                return NamedKind(r);
            return "";
        }

        /// <summary>
        /// A recorded route is OPEN: it ends where the driver stopped, and the
        /// leg from the last waypoint back to the first is a line across
        /// country that nobody drove. On R1, recorded 2026-08-30, that line is
        /// 1998 m long - driving it is the difference between a patrol and a
        /// vehicle disappearing into the woods.
        ///
        /// An open route is therefore mirrored: out along the road, and back
        /// the same way. The driver stays a pure loop driver, needs no second
        /// code path, and the two ends become u-turns - which is what the
        /// stuck escalation is there for. A route whose ends already meet is
        /// left alone.
        ///
        /// The copy belongs to the patrol. What stands in `_routes` stays as
        /// recorded, because the recorder writes THAT back to the file.
        /// </summary>
        static Route OutAndBack(Route r)
        {
            int n = r.P.Count;
            if (n < 3) return r;

            float sum = 0f;
            for (int i = 0; i < n - 1; i++)
                sum += Flat(r.P[i + 1].Pos - r.P[i].Pos);
            float avg = sum / (n - 1);
            float closing = Flat(r.P[0].Pos - r.P[n - 1].Pos);
            if (closing <= Mathf.Max(80f, 3f * avg)) return r;

            Route back = new Route();
            back.Name = r.Name;
            back.Fraction = r.Fraction;
            back.Vehicle = r.Vehicle;
            back.Count = r.Count;
            back.Enabled = r.Enabled;
            back.Scene = r.Scene;
            for (int i = 0; i < n; i++) back.P.Add(r.P[i]);
            for (int i = n - 2; i >= 1; i--) back.P.Add(r.P[i]);

            RevivalPlugin.L.LogInfo("Patrol: " + r.Name + " is open - the two ends "
                + "are " + closing.ToString("0") + " m apart while the legs average "
                + avg.ToString("0") + " m. Driving it out and back: "
                + back.P.Count + " waypoints, a u-turn at each end.");
            return back;
        }

        /// <summary>Length in the ground plane. Height is never a distance here:
        /// the waypoints carry camera height, the vehicle carries its own.</summary>
        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        /// <summary>
        /// Put an authored X/Z point on a surface a vehicle may stand on, and
        /// say whether such a surface was FOUND.
        ///
        /// The lookup is <see cref="RoadUnder"/> - the same one the convoy
        /// column uses: it prefers the local road deck, retries from
        /// progressively higher origins when the recorded line is buried, and
        /// never accepts a wall, a vehicle or a character as ground. Up to here
        /// this method cast ONE unfiltered ray from 30 m above the point and
        /// took whatever it hit first, so a tree crown, a shed roof or another
        /// patrol counted as road - and on a miss it returned the authored point
        /// unchanged, which on a legacy route with y=0 is hundreds of metres
        /// under the terrain.
        ///
        /// A false return means there is nothing here to stand on. Nothing may
        /// be placed on the point it writes then; the callers look for another
        /// waypoint or refuse to place at all.
        /// </summary>
        static bool GroundSpot(Vector3 point, Transform own, float lift,
                               out Vector3 placed)
        {
            float y;
            Vector3 normal;
            if (RoadUnder(point, own, out y, out normal))
            {
                placed = new Vector3(point.x, y + lift, point.z);
                return true;
            }
            placed = point + Vector3.up * lift;
            return false;
        }

        /// <summary>The first waypoint at or after <paramref name="start"/> with
        /// ground under it, and where a vehicle stands on it; -1 when neither the
        /// next <see cref="GroundTries"/> waypoints nor the next
        /// <see cref="GroundReach"/> metres of the recorded line have any. Far
        /// from every player the colliders around a route are not necessarily
        /// loaded, and then the honest answer is "not here" - not a vehicle
        /// dropped into nothing.
        ///
        /// THE BUDGET IS METRES, not waypoints, for the reason written at
        /// <see cref="GroundReach"/>: a count punishes a densely recorded route
        /// for being densely recorded. The waypoint count stays as the FLOOR, so
        /// a sparse route never gets a shorter search than it had before.
        ///
        /// <paramref name="wrap"/> is false for a one-way convoy: past the last
        /// waypoint there is nothing farther along, and waypoint 0 is the start
        /// of the route, a kilometre BACK down the road.</summary>
        static int GroundedWaypoint(Route r, int start, float lift, bool wrap,
                                    out Vector3 placed)
        {
            placed = Vector3.zero;
            int n = r.P.Count;
            if (n <= 0) return -1;
            start = ((start % n) + n) % n;
            int tries = GroundWalk < n ? GroundWalk : n;
            float walked = 0f;
            int last = start;
            for (int step = 0; step < tries; step++)
            {
                int at = start + step;
                if (at >= n)
                {
                    if (!wrap) return -1;
                    at -= n;
                }
                if (step > 0)
                {
                    walked += FlatDistance(r.P[last].Pos, r.P[at].Pos);
                    // Both budgets have to be spent. Whichever of the two is the
                    // generous one for THIS recording is the one that decides.
                    if (step >= GroundTries && walked >= GroundReach) return -1;
                }
                last = at;
                if (GroundSpot(r.P[at].Pos, null, lift, out placed)) return at;
            }
            return -1;
        }

        /// <summary>The surface height of the nearest point of this recorded
        /// line that has one, searched outwards from <paramref name="arc"/> in
        /// 5 m steps to 60 m either way. False when that whole stretch has no
        /// ground under it, which means the area is not loaded rather than that
        /// the route is wrong.</summary>
        static bool LineHeight(Route r, float arc, out float y)
        {
            y = 0f;
            int seg;
            for (float step = 5f; step <= 60f; step += 5f)
            {
                float found;
                Vector3 normal;
                if (RoadUnder(PointOnRoute(r, arc + step, out seg), null,
                              out found, out normal)
                    || RoadUnder(PointOnRoute(r, arc - step, out seg), null,
                                 out found, out normal))
                {
                    y = found;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The waypoint this hull is nearest to IN THE DIRECTION IT IS
        /// DRIVING - which is not the same question as <see cref="Nearest"/>.
        ///
        /// WHY THIS EXISTS. An open recording is driven out and back
        /// (<see cref="OutAndBack"/>), so every metre of that road carries TWO
        /// waypoints, one per direction, a few metres apart. Which of the two is
        /// nearest to a stopped hull is a coin toss, and the losing half of the
        /// time the recovery faces the vehicle back the way it came. It then
        /// drives the road in reverse until the leash or the next stop puts it
        /// back - facing forwards again, or not. That is the "wrong way round
        /// after a teleport, then it tries to turn, then it is stuck again" the
        /// user reported on 2026-09-22.
        ///
        /// The waypoint it was driving to is the tiebreak, exactly as in
        /// <see cref="RailArcAt"/>, so the answer is always on the carriageway
        /// the vehicle was actually on.</summary>
        static int NearestOn(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            if (r == null || r.P.Count < 2) return 0;
            if (RailSpan(r, u.OneWay) < 1f) return Nearest(r, pos);
            int seg;
            RailPoint(r, RailArcAt(u, pos), u.OneWay, out seg);
            return seg;
        }

        /// <summary>The waypoint of this route nearest to a position, measured
        /// in the ground plane - where a vehicle that has left the road belongs
        /// back on it.</summary>
        static int Nearest(Route r, Vector3 pos)
        {
            int best = 0;
            float bestDistance = 0f;
            for (int i = 0; i < r.P.Count; i++)
            {
                float d = FlatDistance(r.P[i].Pos, pos);
                if (i == 0 || d < bestDistance) { best = i; bestDistance = d; }
            }
            return best;
        }

        // =====================================================================
        //  Arming: the four settings that make an empty vehicle drivable
        // =====================================================================

        static void Arm(Unit u)
        {
            u.Wait += Time.fixedDeltaTime;

            Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (vgsType == null) { Drop(u, "VehicleGameSystem not found"); return; }

            if (u.Vgs == null) u.Vgs = u.Car.GetComponent(vgsType);
            if (u.Vgs == null) { Drop(u, "the spawned object has no VehicleGameSystem"); return; }

            // The vehicle needs a few frames. Until IsInitialized is true,
            // SetSleepModeEnabled returns without doing anything and the
            // component references are not filled in yet.
            if (!GetBool(u.Vgs, "IsInitialized", false))
            {
                if (u.Wait > 15f)
                    Drop(u, "IsInitialized stayed false for 15 s");
                return;
            }

            u.Rcc = GetField(u.Vgs, "_carController") as Component;
            if (u.Rcc == null) { Drop(u, "_carController is empty"); return; }
            u.Body = GetField(u.Vgs, "_rigidbody");

            // From here on the sleep prefixes protect this vehicle. Entering it
            // in the owner list BEFORE waking it is deliberate: EnablePhys is
            // safe, but anything that runs in between must not put it back to
            // sleep.
            _owned[u.Vgs.GetInstanceID()] = true;

            // The one bool that takes the car out of every input path the game
            // has (REVERSE_ENGINEERING.md 20.1). The two next to it are what
            // RCCAICarController::Awake sets on a car it is going to drive.
            SetBool(u.Rcc, "AIController", true);
            SetBool(u.Rcc, "autoReverse", true);
            SetBool(u.Rcc, "canEngineStall", false);
            SetBool(u.Rcc, "automaticGear", true);

            // Physics on. NOT SetSleepModeEnabled(false) - EnablePhys is the
            // call that puts the wheels back and sets IsMine, and our own
            // prefix would swallow nothing of it (20.5).
            Invoke(u.Vgs, "EnablePhys");

            SetFloat(u.Vgs, "Fuel", 4000f);
            SetBool(u.Rcc, "engineRunning", true);

            Gun.Collect(u);
            u.CrewSize = Besatzung(u);
            List<RevivalComposition.CrewMan> crew = u.CrewSnapshot;
            // The editor's crew list is the LOADOUT of this vehicle's men, not
            // its head count: one role line used to clamp the whole crew to a
            // single man, so every convoy vehicle in the user's five-vehicle
            // column put exactly one crewman on the ground. The vehicle is
            // manned by its seats (Besatzung, capped by CrewLimit) and the
            // listed roles repeat around that number; a list LONGER than the
            // seats still gets every role out, up to its vehicle-specific cap.
            if (crew != null && crew.Count > 0)
                u.CrewSize = Mathf.Min(Mathf.Max(u.CrewSize, crew.Count),
                                       CrewLimit(u));

            u.Armed = true;
            VehicleModules.StockTrunk(u.Car.transform, u.Tank);   // NDR vehicle modules: trunk loot
            // NDR convoy: a convoy vehicle carries about double a patrol's loot -
            // the same module pool, stocked ExtraTrunkFills more times.
            if (u.ConvoyId != 0)
                for (int k = 0; k < RevivalConvoy.ExtraTrunkFills; k++)
                    VehicleModules.StockTrunk(u.Car.transform, u.Tank);
            RevivalPlugin.L.LogInfo("Patrol: vehicle armed on " + u.Route.Name
                + " - AIController set, physics on, engine running, "
                + u.Turrets.Length + " turret object(s), " + u.CrewSize
                + " man crew.");
        }

        /// <summary>
        /// One man per seat, minus the gunner's seat, which is not a seat the
        /// game hands out (Turret.FreeSeatPostfix) - the gunner is the turret
        /// code itself. Armor uses CrewMax; a convoy transport carries its
        /// full squad of up to fifteen men.
        /// </summary>
        static int Besatzung(Unit u)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value) return 0;
            Transform seats = GetField(u.Vgs, "SeatPoints") as Transform;
            if (seats == null) return 0;
            int n = 0;
            for (int i = 0; i < seats.childCount; i++)
                if (seats.GetChild(i).name != Turret.SeatName) n++;
            return Mathf.Clamp(n, 0, CrewLimit(u));
        }

        static int CrewLimit(Unit u)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value) return 0;
            // The convoy Ural carries a full infantry squad. Patrol caps remain
            // unchanged; the transport's real seats determine the head count.
            return u.ConvoyId != 0 && u.Truck ? 15
                : Mathf.Max(0, RevivalPlugin.CfgPatrolCrewMax.Value);
        }

        static void Drop(Unit u, string why)
        {
            RevivalPlugin.L.LogWarning("Patrol: giving up on this vehicle - " + why + ".");
            Forget(u);
            Weg(u.Car);
            _units.Remove(u);
            Verloren();
        }

        /// <summary>
        /// Is this vehicle dead, and what happens then.
        ///
        /// `VehicleGameSystem::SetDurabilityValue` kills the engine at
        /// `Durability &lt;= 0`, turns the damage smoke on and starts a respawn
        /// timer (RE 20.9). It does NOT destroy the object - the wreck stands
        /// there. That is the moment the crew climbs out: whoever killed the
        /// vehicle is standing within a few dozen metres, and now there are
        /// men on the ground who want a word.
        ///
        /// The wreck is removed after WreckSeconds so a long session does not
        /// leave the road lined with burnt out BTRs. The crew stays - they are
        /// ordinary NPCs from that moment on, and they die like ordinary NPCs.
        /// </summary>
        static bool Gefallen(Unit u)
        {
            if (u.Died <= 0f)
            {
                if (GetFloat(u.Vgs, "Durability", 1f) > 0f) return false;

                u.Died = Time.time;
                u.Target = null;
                SetFloat(u.Rcc, "gasInput", 0f);
                SetFloat(u.Rcc, "brakeInput", 1f);
                SetFloat(u.Rcc, "steerInput", 0f);
                RevivalPlugin.L.LogInfo("Patrol: the " + (u.Tank ? "tank" : "BTR")
                    + " on " + u.Route.Name + " is destroyed after " + u.Lap
                    + " lap(s), " + u.Shots + " shot(s), " + u.Hits + " hit(s).");

                // The game's DamageSmoke is a dense cloud close to the hull.
                // Keep it, and add the part visible from down the road: fire on
                // the deck and a tall column that lives exactly as long as the
                // wreck. The effect is a child, so Weg(u.Car) removes it too.
                FireEffect.SpawnWreck(u.Car, u.Tank);
                Turret.Net.PublishWreck(u.Car.transform, u.Tank);

                UnloadCrew(u);

                // NDR convoy column: the first loss ends the formation for
                // good. From here the survivors drive themselves again and the
                // behaviour layer in RevivalConvoy owns their holds.
                if (u.ConvoyId != 0)
                {
                    u.Column = false;
                    ColumnBreak(u.ConvoyId, "vehicle destroyed");
                }

                if (u.ConvoyId == 0) Verloren();   // NDR convoy: convoy losses are not auto-refilled
            }

            // NDR convoy: a convoy wreck does NOT despawn on the WreckSeconds
            // clock. The user wants the burning column to stay on the road until
            // the NEXT convoy event spawns, which is when RevivalConvoy calls
            // ConvoyClearAll for the previous convoy. So a convoy unit lingers
            // here forever and is only removed by that call.
            if (u.ConvoyId != 0) return true;

            // NDR vehicle modules: a wreck whose trunk still holds module loot
            // lingers longer so the loot can be recovered (additive bonus).
            float bleibt = RevivalPlugin.CfgPatrolWreck.Value
                         + VehicleModules.WreckBonus(u.Car.transform);
            if (bleibt > 0f && Time.time - u.Died >= bleibt)
            {
                RevivalPlugin.L.LogInfo("Patrol: wreck on " + u.Route.Name
                    + " removed after " + bleibt.ToString("0") + " s.");
                Forget(u);
                Weg(u.Car);
                _units.Remove(u);
            }
            return true;
        }

        // =====================================================================
        //  The ground guard: a vehicle that is falling is not a vehicle that is
        //  stuck
        // =====================================================================

        /// <summary>
        /// Is there a surface under this hull that it could be standing on, and
        /// is the hull on the right side of it?
        ///
        /// The lookup is <see cref="RoadUnder"/> with the vehicle's own
        /// transform excluded, so neither the hull itself nor another vehicle
        /// nor a man counts as ground. Two different failures matter:
        ///
        ///   BURIED - a surface is found and the hull sits more than
        ///   <see cref="BuriedBy"/> metres below it. That is a vehicle inside
        ///   the terrain, whatever its speed.
        ///
        ///   FALLING - no surface at all AND the hull is dropping faster than
        ///   <see cref="FallSpeed"/>. The speed matters: a hull resting on
        ///   something the lookup does not recognise is not in trouble and must
        ///   not be picked up and moved.
        /// </summary>
        static bool Supported(Unit u)
        {
            Transform t = u.Car.transform;
            float surface;
            Vector3 normal;
            if (RoadUnder(t.position, t, out surface, out normal))
                return t.position.y >= surface - BuriedBy;
            return Velocity(u.Body).y > -FallSpeed;
        }

        /// <summary>
        /// A hull with nothing under it, handled before the driver sees it.
        ///
        /// WHY THIS EXISTS. <see cref="Drive"/> measures being stuck in the
        /// GROUND PLANE (`groundVel.y = 0f`), and a vehicle falling through the
        /// world falls straight down - so it reads as standing still, the stuck
        /// escalation warps it onto a waypoint, it falls through again, and the
        /// two repeat for as long as the session lasts. That pair is the
        /// reported symptom: a vehicle that appears somewhere, drops, appears
        /// above the map again, on repeat.
        ///
        /// So a falling hull is taken away from the stuck timer and handled
        /// here instead: it is put back on verified ground at most
        /// <see cref="FallRecoveries"/> times in <see cref="FallForget"/>
        /// seconds, and if it keeps leaving the world after that it is given up
        /// - which starts the ordinary replacement WAIT rather than another
        /// immediate attempt at the same place.
        ///
        /// The lookup runs at <see cref="GroundEvery"/>, not per physics step:
        /// it is several raycasts per vehicle, and a fall lasts seconds.
        ///
        /// Returns true when this unit is handled for this step - it is
        /// falling, has just been put back, or has been removed - and the
        /// driver must not run.
        /// </summary>
        static bool GroundGuard(Unit u)
        {
            // A column vehicle is not driven and does not fall: Columns() puts
            // it on its slot every physics step and never lets a placed hull
            // sink. Nothing here applies to it.
            if (u.Column) return false;

            if (Time.time >= u.NextGround)
            {
                u.NextGround = Time.time + GroundEvery;
                bool firm = Supported(u);
                if (firm)
                {
                    u.Airborne = false;
                    u.FallSince = 0f;
                    // A vehicle that has driven normally since the last
                    // recovery is not the vehicle this guard gives up on.
                    if (u.Recoveries > 0 && Time.time - u.RecoverAt > FallForget)
                        u.Recoveries = 0;
                }
                else
                {
                    if (!u.Airborne) u.FallSince = Time.time;
                    u.Airborne = true;
                }
            }
            if (!u.Airborne) return false;

            // Falling is not being stuck. Without this the FREE warp fires into
            // the same hole every StuckSeconds.
            u.Stuck = 0f;
            if (Time.time - u.FallSince < FallSeconds) return true;

            Recover(u);
            return true;
        }

        /// <summary>Put a fallen vehicle back on the nearest waypoint of its own
        /// route that has ground under it, or give it up. The unit may be gone
        /// when this returns.</summary>
        static void Recover(Unit u)
        {
            Transform t = u.Car.transform;
            Route r = u.Route;
            u.FallSince = Time.time;      // one attempt per FallSeconds
            u.Recoveries++;
            u.RecoverAt = Time.time;

            if (u.Recoveries > FallRecoveries)
            {
                Drop(u, "it left the ground " + (u.Recoveries - 1) + " times in "
                    + FallForget.ToString("0") + " s and every recovery failed");
                return;
            }

            Vector3 target;
            int at = GroundedWaypoint(r, NearestOn(u, t.position), 1.5f,
                                      !u.OneWay, out target);
            if (at < 0)
            {
                if (Time.time >= u.FallLog)
                {
                    u.FallLog = Time.time + 10f;
                    RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name
                        + " is at " + t.position + " with nothing under it, and no "
                        + "waypoint of its route has ground either - the colliders "
                        + "around it are probably not loaded. Waiting.");
                }
                return;
            }

            Stop(u.Body);
            SetFloat(u.Rcc, "gasInput", 0f);
            SetFloat(u.Rcc, "brakeInput", 0f);
            SetFloat(u.Rcc, "steerInput", 0f);
            SetFloat(u.Rcc, "handbrakeInput", 0f);
            t.position = target;
            t.rotation = Quaternion.LookRotation(
                RouteDirection(r, at, u.OneWay).normalized, Vector3.up);

            u.Next = at;
            u.Stuck = 0f;
            u.Queued = 0f;
            u.OffRouteSince = 0f;
            u.Airborne = false;
            u.FallSince = 0f;
            // The hull did not drive here, it was carried. Every clock that
            // measures where it has got to starts again from this waypoint,
            // exactly as after the two other recoveries (Free, BackOnRoute) -
            // otherwise a hull that sank through the road next to its own
            // waypoint is put back within ProgressMetres of where it fell and
            // reads as "has not moved for ten seconds" on arrival.
            MadeProgress(u, target);
            // Let it settle on the road before the guard judges it again.
            u.NextGround = Time.time + FallSeconds;

            RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name
                + " had no ground under it - put back on waypoint " + at
                + " (" + u.Recoveries + " of " + FallRecoveries + ").");
        }

        /// <summary>Everything that has to be held every step, because the game
        /// keeps undoing it.</summary>
        static void Keep(Unit u)
        {
            // NDR convoy break-up: remember where the shooting came from. The gun
            // drops its target the moment it loses sight of it, but the survivors
            // of a broken column still have to know which way to spread out and
            // which way to face - and usually the only vehicle that ever saw the
            // attacker is the one that is now burning.
            if (u.Target != null)
            {
                u.Threat = u.Target.position;
                u.ThreatAt = Time.time;
            }

            // ExpendFuel drains an unmanned vehicle exactly as fast as a driven
            // one and kills the engine at zero (20.6). A patrol meant to run
            // for hours needs its tank held up.
            if (GetFloat(u.Vgs, "Fuel", 1f) < 500f) SetFloat(u.Vgs, "Fuel", 4000f);

            // StartEngine is a TOGGLE, not a start - calling it on a running
            // engine switches it off. Write the field.
            if (!GetBool(u.Rcc, "engineRunning", true)) SetBool(u.Rcc, "engineRunning", true);

            // Belt and braces: if some path we have not read got the physics
            // off anyway, the vehicle is a statue until this puts it back.
            if (!GetBool(u.Vgs, "EnabledPhys", true)) Invoke(u.Vgs, "EnablePhys");
        }

        /// <summary>Hold the vehicle where it stands: no gas, full brake, wheels
        /// straight. The gun runs on its own frame-rate tick, so a held vehicle
        /// still scans and fires. NDR convoy: used for column spacing and for the
        /// behaviour agent's hold-and-search.</summary>
        static void HoldStill(Unit u)
        {
            // Waiting on purpose is not failing to get anywhere. Every
            // legitimate standstill goes through here, so this is the one place
            // the progress clock has to be told.
            if (u.Car != null) MadeProgress(u, u.Car.transform.position);
            if (u.Rcc == null) return;
            SetFloat(u.Rcc, "gasInput", 0f);
            SetFloat(u.Rcc, "brakeInput", 1f);
            SetFloat(u.Rcc, "steerInput", 0f);
            SetFloat(u.Rcc, "handbrakeInput", 1f);
        }

        // =====================================================================
        //  The driver
        // =====================================================================

        static void Drive(Unit u)
        {
            Transform t = u.Car.transform;
            Vector3 pos = t.position;
            Route r = u.Route;
            int n = r.P.Count;
            float dt = Time.fixedDeltaTime;

            Vector3 vel = Velocity(u.Body);
            float kmh = vel.magnitude * 3.6f;
            Vector3 groundVel = vel;
            groundVel.y = 0f;
            float groundKmh = groundVel.magnitude * 3.6f;

            Advance(u, pos);
            if (u.Arrived) { HoldStill(u); return; }

            // --- where to aim ------------------------------------------------
            float look = Mathf.Clamp(vel.magnitude * 1.1f, 10f, 35f);
            Vector3 aim = LookAhead(r, u.Next, pos, look, u.OneWay);

            // NDR convoy break-up: the recorded road of a convoy runs straight
            // THROUGH the vehicle that has just been destroyed on it. Braking for
            // that wreck also resets the stuck timer, so a survivor used to stand
            // against a burning hull for the whole event and everything behind it
            // piled up in the same place. Give it BypassAfter seconds, then steer
            // round the wreck on a latched point clear of both hulls.
            bool passing = Time.time < u.BypassUntil;
            if (passing && FlatDistance(pos, u.BypassPoint) < BypassReached)
            {
                u.BypassUntil = 0f;
                passing = false;
            }
            if (ConvoyBlocked(u, t.forward))
            {
                Unit wreck = ConvoyWreckAhead(u, t.forward);
                if (wreck == null) u.Blocked = 0f;
                else u.Blocked += dt;
                if (wreck == null || u.Blocked < BypassAfter
                    || !StartBypass(u, t, wreck))
                {
                    HoldStill(u);
                    u.Stuck = 0f;
                    return;
                }
                passing = true;
            }
            else if (!passing) u.Blocked = 0f;
            if (passing) aim = u.BypassPoint;

            Vector3 local = t.InverseTransformPoint(aim);
            local.y = 0f;
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;

            // --- how fast ----------------------------------------------------
            float want;
            if (u.ConvoyId != 0)
            {
                // A convoy runs at full gas. It still eases for a hard corner so
                // pure pursuit can hold the recorded line, but it never crawls.
                want = RevivalConvoy.CruiseSpeed * CornerFactor(r, u.Next);
                want = Mathf.Max(want, 20f);
                // Squeezing past a burning mate is a crawl, not a cruise.
                if (passing) want = BypassSpeed;
            }
            else
            {
                want = r.P[u.Next].Speed;
                if (want <= 0f) want = RevivalPlugin.CfgPatrolSpeed.Value;
                want *= CornerFactor(r, u.Next);
                want = Mathf.Max(want, 12f);
                // The mates of one composition pass through each other, so the
                // gap to the vehicle ahead is kept by the driver or not at all.
                want = QueueBehind(u, t, want, dt);
            }

            float steer = Mathf.Clamp(angle / FullLockAt, -1f, 1f);
            float gas, brake;
            Throttle(want, kmh, out gas, out brake);

            // --- what is in the way ------------------------------------------
            if (u.ConvoyId != 0)
            {
                // World props may be ghosted, but convoy hulls still need the
                // spacing checks above. Waypoint recovery must not warp a free
                // driver through a defender or into another APC.
                GhostAhead(u, t, vel.magnitude);
                GhostAround(u, t, vel.magnitude, groundKmh < 3f);
                if (groundKmh < 3f) u.Stuck += dt; else u.Stuck = 0f;
                // Free checks the destination and intervening convoy lanes.
                if (Escalate(u, pos)) return;
            }
            else
            {
                // The same "ghost through" the convoy has had since
                // feature/convoy-oneway-drive, and for the same reason: steering
                // is the first answer and a good one, but a dirt road between two
                // rows of trees has nowhere to steer TO, and a patrol that snags
                // on the wreck parked across it stands there for the rest of the
                // session. The prop stays solid for everyone else, terrain, mesh
                // roads and tunnel floors are never ghosted, and anything alive -
                // player, NPC, animal, vehicle - stays solid, so a patrol can
                // still run a man over. Avoid runs anyway: passing through
                // scenery is the safety net, not the normal way to drive.
                GhostAhead(u, t, vel.magnitude);
                // And the same thing again for everything the rays miss: what
                // is lower than they are, what the hull catches at the flank,
                // and what the nose is already inside. Faster while the vehicle
                // is not moving, because then it is standing on an obstacle
                // that has already won.
                GhostAround(u, t, vel.magnitude, groundKmh < 3f);
                float dodge = Avoid(u, t, vel.magnitude);
                if (dodge != 0f)
                {
                    steer = Mathf.Clamp(steer + dodge, -1f, 1f);
                    if (want > 25f) { gas *= 0.5f; }
                }

                // --- stuck? ---------------------------------------------------
                // A patrol is stuck because it is not moving, regardless of what
                // the throttle happens to say. The old throttle condition let the
                // obstacle avoidance reset this timer indefinitely.
                if (groundKmh < 3f) u.Stuck += dt; else u.Stuck = 0f;
                // --- still on its own road? -----------------------------------
                // A vehicle in the woods is not stuck, so nothing above would
                // ever bring it back. This does, and it runs first: the nearest
                // waypoint is a better answer for a lost vehicle than the next
                // one along from wherever its index happens to stand.
                if (Leashed(u, pos)) return;
                if (Escalate(u, pos)) return;
            }

            // Braking under walking pace is a gear change, not a brake
            // (see the class comment). Coast instead.
            if (kmh < CoastBelow && brake > 0f) { brake = 0f; }

            SetFloat(u.Rcc, "gasInput", Mathf.Clamp01(gas));
            SetFloat(u.Rcc, "brakeInput", Mathf.Clamp01(brake));
            SetFloat(u.Rcc, "steerInput", Mathf.Clamp(steer, -1f, 1f));
            SetFloat(u.Rcc, "handbrakeInput", 0f);

            if (u.Lap != u.Reported)
            {
                u.Reported = u.Lap;
                RevivalPlugin.L.LogInfo("Patrol: " + r.Name + " lap " + u.Lap
                    + " done, " + u.Frees + " free event(s) so far.");
            }
        }

        /// <summary>Walk the waypoint index forward past everything we have
        /// already reached or driven past.</summary>
        static void Advance(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            int n = r.P.Count;
            float pass = RevivalPlugin.CfgPatrolPassRadius.Value;
            int moved = 0;

            while (moved < n)
            {
                Vector3 w = r.P[u.Next].Pos;
                Vector3 to = w - pos; to.y = 0f;
                bool close = to.sqrMagnitude < pass * pass;

                bool past = false;
                // One-way convoy at the very start: the "previous" of waypoint 0
                // is the wrap-around leg from the LAST waypoint, which is
                // meaningless here and could falsely read as "already past". A
                // vehicle queued behind the start line advances by proximity
                // only until it has rolled onto the route.
                if (!(u.OneWay && u.Next == 0 && u.Lap == 0))
                {
                    Vector3 prev = r.P[(u.Next - 1 + n) % n].Pos;
                    Vector3 leg = w - prev; leg.y = 0f;
                    if (leg.sqrMagnitude > 0.01f)
                    {
                        Vector3 back = pos - w; back.y = 0f;
                        past = Vector3.Dot(back, leg.normalized) > 0f;
                    }
                }

                if (!close && !past) break;

                u.Next++;
                moved++;
                if (u.Next >= n)
                {
                    // One-way convoy: the recorded route is driven ONCE. Reaching
                    // the end means "arrive and vanish", not loop back to wp0.
                    if (u.OneWay) { u.Arrived = true; u.Next = n - 1; break; }
                    u.Next = 0; u.Lap++;
                }
            }

            if (moved >= n)
                RevivalPlugin.L.LogWarning("Patrol: " + r.Name + " skipped a whole "
                    + "lap in one step - the vehicle is nowhere near its route. "
                    + "PassRadius too large, or the route has duplicate points.");
        }

        /// <summary>A point <paramref name="dist"/> metres along the route,
        /// measured from the vehicle. Pure pursuit aims at this, not at the
        /// waypoint: aiming straight at a waypoint makes a vehicle hunt.</summary>
        static Vector3 LookAhead(Route r, int next, Vector3 pos, float dist, bool oneWay)
        {
            int n = r.P.Count;
            Vector3 cur = pos;
            Vector3 target = r.P[next].Pos;
            target.y = pos.y;
            float rest = dist;
            int i = next;

            for (int step = 0; step < n; step++)
            {
                Vector3 w = r.P[i].Pos;
                // Steering is a ground-plane problem. This also keeps migrated
                // manual routes with y=0 from becoming vertical lookahead legs.
                w.y = pos.y;
                Vector3 seg = w - cur;
                float len = seg.magnitude;
                if (len > 0.001f)
                {
                    if (len >= rest) return cur + seg * (rest / len);
                    rest -= len;
                }
                cur = w;
                target = w;
                if (oneWay && i == n - 1) return target;
                i = (i + 1) % n;
            }
            return target;
        }

        /// <summary>1 on a straight, less the sharper the next corner is.</summary>
        static float CornerFactor(Route r, int next)
        {
            int n = r.P.Count;
            Vector3 a = r.P[(next - 1 + n) % n].Pos;
            Vector3 b = r.P[next].Pos;
            Vector3 c = r.P[(next + 1) % n].Pos;
            Vector3 u = b - a; u.y = 0f;
            Vector3 v = c - b; v.y = 0f;
            if (u.sqrMagnitude < 0.01f || v.sqrMagnitude < 0.01f) return 1f;
            float deg = Vector3.Angle(u, v);
            return Mathf.Clamp(1f - deg / 120f, 0.25f, 1f);
        }

        static void Throttle(float wantKmh, float isKmh, out float gas, out float brake)
        {
            float err = wantKmh - isKmh;
            gas = Mathf.Clamp01(err / 8f);
            brake = Mathf.Clamp01(-err / 8f);
        }

        // =====================================================================
        //  Obstacles
        // =====================================================================

        /// <summary>Three rays as RCC casts them. Returns a steering correction,
        /// 0 when the road ahead is clear.</summary>
        static float Avoid(Unit u, Transform t, float speed)
        {
            float range = Mathf.Clamp(speed * 1.5f, 8f, 25f);
            Vector3 nose = t.position + t.forward * NoseOffset + Vector3.up * 1.2f;

            float wide = Hit(u, nose, t.forward, range);
            float left = Hit(u, nose, Quaternion.AngleAxis(-25f, t.up) * t.forward, range * 0.7f);
            float right = Hit(u, nose, Quaternion.AngleAxis(25f, t.up) * t.forward, range * 0.7f);

            if (wide < 0f && left < 0f && right < 0f) return 0f;

            if (wide < 0f)
            {
                // Nothing in the PATH: only the shoulder rays found something.
                // A forest road grazes those rays with every tree it passes,
                // and the old answer was a fifth of a turn away from whichever
                // side hit - held for as long as the trees lasted, which is the
                // whole road. That is how a patrol ends up driving around in a
                // wood its route never goes near. Now only something the hull
                // is about to scrape counts, and it counts gently.
                float nearLeft = left < 0f ? range : left;
                float nearRight = right < 0f ? range : right;
                float near = Mathf.Min(nearLeft, nearRight);
                if (near >= SideDodgeAt) return 0f;
                float urgency = 1f - near / SideDodgeAt;
                return (nearLeft <= nearRight ? 1f : -1f) * SideDodge * urgency;
            }

            // Something IS in the path. Steer towards whichever side has more
            // room, harder the closer it is. Both sides blocked and the
            // escalation takes over on its own, because we will stop moving.
            float freeLeft = left < 0f ? range : left;
            float freeRight = right < 0f ? range : right;
            float push = freeLeft > freeRight ? -0.6f : 0.6f;
            push *= Mathf.Clamp01(1f - wide / range) + 0.4f;
            return push;
        }

        /// <summary>Distance to the first thing that is not this vehicle and
        /// not small enough to drive through, or -1 for a clear ray.</summary>
        static float Hit(Unit u, Vector3 origin, Vector3 dir, float range)
        {
            Vector3 point;
            GameObject go = Turret.RaycastObject(origin, dir, range, out point);
            if (go == null) return -1f;
            if (u.Car != null && go.transform.IsChildOf(u.Car.transform)) return -1f;
            float d = (point - origin).magnitude;

            Transform klein = Kleinkram(go);
            if (klein == null) return d;
            // Small enough to drive through: never steer around it, and take
            // it out of the way once the hull is actually against it.
            if (d <= CrushReach) Zerbrechen(u, klein);
            return -1f;
        }

        // =====================================================================
        //  The small stuff on the road
        // =====================================================================

        /// <summary>Metres in front of the nose at which a crushable thing is
        /// actually crushed. Anything further off is only ignored - a fence
        /// that vanishes twenty metres before the vehicle reaches it is a
        /// bug report.</summary>
        const float CrushReach = 6f;

        /// <summary>The answer for one hit object, so the same fence is not
        /// measured again on every ray of every physics step. Value null means
        /// "not crushable", which is the expensive answer and the common
        /// one.</summary>
        static Dictionary<int, Transform> _klein = new Dictionary<int, Transform>();

        /// <summary>Ids of things already crushed. Keeps the log to one line
        /// each and the work to one pass.</summary>
        static Dictionary<int, bool> _zerbrochen = new Dictionary<int, bool>();

        static Type _colliderType;
        static PropertyInfo _colliderEnabled;
        static bool _physLookedUp;

        /// <summary>
        /// Is this hit a knee-high fence, a post, a bit of road junk - the kind
        /// of thing twelve tons goes through rather than around?
        ///
        /// WHY THIS EXISTS. The map is full of small obstacles, and a driver
        /// that treats every one of them as a wall does two wrong things at
        /// once: it steers off the road for a fence it would have flattened,
        /// and having steered off the road it gets stuck for real. Every FREE
        /// event in the run of 2026-08-30 - waypoints 67, 68, 71, 72, 73, 74 -
        /// began that way.
        ///
        /// The test is SIZE and nothing else, because size is the one thing
        /// that means the same for every prop on the map. It walks up from the
        /// hit object while the whole candidate still fits inside CrushHeight
        /// and CrushWidth, so a fence hit on one plank gives up the whole
        /// fence and not just that plank. A candidate that grows too large
        /// ends the walk, which is what keeps a house from being crushed
        /// because a doorstep was hit.
        ///
        /// What is NEVER crushed, whatever its size: anything belonging to a
        /// player, an NPC, an animal or a vehicle. Those are small and would
        /// pass the size test easily, and taking a man's collider away is not
        /// a driving aid, it is a hole in the game.
        /// </summary>
        static Transform Kleinkram(GameObject go)
        {
            if (!RevivalPlugin.CfgPatrolCrush.Value || go == null) return null;

            int id = go.GetInstanceID();
            Transform found;
            if (_klein.TryGetValue(id, out found)) return found;
            if (_klein.Count > 4096) _klein.Clear();

            found = Suchen(go);
            _klein[id] = found;
            return found;
        }

        /// <summary>
        /// The largest thing around this hit that is still small enough to
        /// drive through, or null.
        ///
        /// THE WALK UP STOPS AT A CONTAINER. Every step up is an
        /// `Ausmasse` over a bigger subtree, and a map has objects whose
        /// parent is a bin holding a thousand props. Two things keep that
        /// from being the next E-032: a parent with more than
        /// `ContainerAb` children is taken as a bin and ends the walk before
        /// it is measured, and `Ausmasse` stops counting the moment the box
        /// is already too big. Nothing here runs twice for the same object -
        /// the answer is cached in `_klein`.
        /// </summary>
        static Transform Suchen(GameObject go)
        {
            if (Lebendig(go.transform)) return null;

            float hoch = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolCrushHeight.Value);
            float breit = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolCrushWidth.Value);

            Transform best = null;
            Transform t = go.transform;
            for (int i = 0; i < 4 && t != null; i++)
            {
                if (!Passt(t, hoch, breit)) break;
                best = t;
                Transform hoeher = t.parent;
                if (hoeher == null) break;
                if (hoeher.childCount > ContainerAb) break;
                t = hoeher;
            }
            return best;
        }

        /// <summary>Direct children from which a transform is taken for a bin
        /// of props rather than one prop. A fence has planks, a car has
        /// wheels; nothing that is ONE thing has two dozen children.</summary>
        const int ContainerAb = 24;

        /// <summary>
        /// Does everything under this transform fit inside the two limits?
        /// False also when there is nothing measurable - a bare collider with
        /// no mesh is a thing we cannot size up, and a thing we cannot size up
        /// is a thing we do not crush.
        /// </summary>
        static bool Passt(Transform t, float hoch, float breit)
        {
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = new Bounds();
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null || !rs[i].enabled) continue;
                if (!any) { b = rs[i].bounds; any = true; }
                else b.Encapsulate(rs[i].bounds);
                Vector3 s = b.size;
                if (s.y > hoch || s.x > breit || s.z > breit) return false;
            }
            return any;
        }

        /// <summary>
        /// Is this part of something alive or something driven?
        ///
        /// Walked UP a few levels with `GetComponent`, not down from the root
        /// with `GetComponentInChildren`. The root of a scene prop can be a
        /// container holding half the map, and searching that five times per
        /// obstacle is exactly the shape of mistake that put the driver at
        /// 3 FPS once (E-032). A vehicle, an NPC and a player all carry their
        /// marker component within a few levels of any collider they own.
        ///
        /// THE ONE COLLIDER THAT BREAKS THAT RULE IS A RAGDOLL BONE. The game
        /// hangs a collider on every bone of a character - that is what a bullet
        /// and an explosion actually hit (Revival.Crew.cs: ExplosionPhysicsEffect
        /// damages an NPC through a collider tagged `RagdollBone`, the head
        /// through `BloodHead`) - and a bone sits at the bottom of a skeleton,
        /// far more than six levels under the NPC_AI2 the walk looks for. So the
        /// walk answered "not alive" for a man's chest, and the patrol treated
        /// his limbs as scenery: the run of 2026-09-22 has a technical ghosting
        /// a rider's chest, shin and upper arm out of its way, fifteen to
        /// twenty-four of them at every spawn. The tag is asked FIRST, because
        /// it is one native compare and it is the exact marker.
        /// </summary>
        static bool Lebendig(Transform t)
        {
            if (Knochen(t)) return true;
            Type[] typen = Marker();
            for (int hoehe = 0; hoehe < 6 && t != null; hoehe++)
            {
                for (int i = 0; i < typen.Length; i++)
                {
                    if (typen[i] == null) continue;
                    if (t.GetComponent(typen[i]) != null) return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>Is this collider a piece of a character's ragdoll?
        ///
        /// `CompareTag` is the non-allocating test, and it THROWS when the tag
        /// it is given is not defined in the build - so each of the two names is
        /// tried on its own and gives up on its own. A build that knows neither
        /// falls back to the marker walk, which is exactly what happened before
        /// this test existed.</summary>
        static int _tagBone, _tagHead;   // 0 unknown, 1 usable, -1 not defined

        static bool Knochen(Transform t)
        {
            if (t == null) return false;
            if (_tagBone >= 0 && Markiert(t, "RagdollBone", ref _tagBone)) return true;
            if (_tagHead >= 0 && Markiert(t, "BloodHead", ref _tagHead)) return true;
            return false;
        }

        static bool Markiert(Transform t, string tag, ref int state)
        {
            try
            {
                bool hit = t.CompareTag(tag);
                state = 1;
                return hit;
            }
            catch
            {
                state = -1;
                RevivalPlugin.L.LogWarning("Patrol: the tag " + tag + " is not "
                    + "defined in this build - a man's limbs are told from "
                    + "scenery by the marker walk alone, which cannot reach a "
                    + "collider deeper than six levels under his NPC_AI2.");
                return false;
            }
        }

        static Type[] _marker;

        static Type[] Marker()
        {
            if (_marker != null) return _marker;
            _marker = new Type[Unantastbar.Length];
            for (int i = 0; i < Unantastbar.Length; i++)
                _marker[i] = RevivalPlugin.TypeByName(Unantastbar[i]);
            return _marker;
        }

        /// <summary>Types whose objects are never crushed. Names, not types:
        /// the plugin references no Assembly-CSharp.</summary>
        static readonly string[] Unantastbar = new string[] {
            "NPC_AI2", "PlayerNetworkController", "VehicleGameSystem",
            "Animal_AI", "ItemSpawned",
        };

        /// <summary>
        /// Take the colliders off, and only the colliders. The prop stays
        /// where it is and stays visible - a fence that disappears is a
        /// glitch, a fence a BTR drives through is a BTR driving through a
        /// fence. Local only: the patrol runs on the master client, the other
        /// machines never had a reason to steer around it.
        /// </summary>
        static void Zerbrechen(Unit u, Transform was)
        {
            if (was == null) return;
            int id = was.GetInstanceID();
            if (_zerbrochen.ContainsKey(id)) return;
            _zerbrochen[id] = true;

            if (!PhysLookUp()) return;
            try
            {
                Component[] cs = was.GetComponentsInChildren(_colliderType, true);
                int n = 0;
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null) continue;
                    object on = _colliderEnabled.GetValue(cs[i], null);
                    if (on is bool && !(bool)on) continue;
                    _colliderEnabled.SetValue(cs[i], false, null);
                    n++;
                }
                if (n > 0)
                    RevivalPlugin.L.LogInfo("Patrol: drove through \"" + was.name
                        + "\" on " + u.Route.Name + " - " + n + " collider(s) off.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Patrol: \"" + was.name + "\" not "
                    + "crushed - " + ex.Message);
            }
        }

        static bool PhysLookUp()
        {
            if (_physLookedUp) return _colliderEnabled != null;
            _physLookedUp = true;
            _colliderType = RevivalPlugin.TypeByName("UnityEngine.Collider");
            if (_colliderType != null)
                _colliderEnabled = _colliderType.GetProperty("enabled",
                    BindingFlags.Public | BindingFlags.Instance);
            if (_colliderEnabled == null)
                RevivalPlugin.L.LogWarning("Patrol: UnityEngine.Collider.enabled not "
                    + "found - patrols cannot drive through anything and will "
                    + "steer around every fence on the map.");
            return _colliderEnabled != null;
        }

        // =====================================================================
        //  NDR convoy "ghost through" (feature/convoy-oneway-drive)
        //
        //  A convoy vehicle passes through the world instead of steering around
        //  it or getting stuck on it. Not by disabling the prop's collider (that
        //  is global and would drop other props and NPCs through a building), but
        //  by Physics.IgnoreCollision between THIS car's colliders and the one
        //  obstacle it is about to touch: local, reversible, and the obstacle
        //  stays fully solid for everyone and everything else. The car's own body
        //  collider is never touched, so bullets still hit it and it still dies.
        //  Terrain and anything alive (player, NPC, animal, vehicle) are left
        //  solid - a convoy runs on the ground and can run a man over.
        // =====================================================================

        static MethodInfo _ignoreColl;
        static bool _ignoreLookedUp;
        static Type _terrainType;
        static bool _terrainLookedUp;

        static bool IgnoreLookUp()
        {
            if (_ignoreLookedUp) return _ignoreColl != null;
            _ignoreLookedUp = true;
            Type phys = RevivalPlugin.TypeByName("UnityEngine.Physics");
            Type col = PhysLookUp() ? _colliderType : null;
            if (phys != null && col != null)
                _ignoreColl = AccessTools.Method(phys, "IgnoreCollision",
                    new Type[] { col, col, typeof(bool) }, null);
            if (_ignoreColl == null)
                RevivalPlugin.L.LogWarning("Patrol: Physics.IgnoreCollision(Collider,"
                    + "Collider,bool) not found - convoy vehicles cannot ghost "
                    + "through obstacles and may snag on the map.");
            return _ignoreColl != null;
        }

        /// <summary>The colliders of a spawned car, for ghosting. Null when the
        /// Collider type could not be resolved.</summary>
        static Component[] CollectCols(GameObject car)
        {
            if (car == null || !PhysLookUp()) return null;
            return car.GetComponentsInChildren(_colliderType, true);
        }

        /// <summary>Make every collider in <paramref name="a"/> ignore every
        /// collider in <paramref name="b"/> and vice versa. Silent no-op if the
        /// reflection seam is missing.</summary>
        static void GhostPair(Component[] a, Component[] b)
        {
            if (a == null || b == null || !IgnoreLookUp()) return;
            object[] args = new object[3];
            args[2] = true;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null) continue;
                for (int j = 0; j < b.Length; j++)
                {
                    if (b[j] == null) continue;
                    args[0] = a[i];
                    args[1] = b[j];
                    try { _ignoreColl.Invoke(null, args); }
                    catch { /* one bad collider pair must not stop the rest */ }
                }
            }
        }

        static bool IsTerrain(GameObject go)
        {
            if (!_terrainLookedUp)
            {
                _terrainLookedUp = true;
                _terrainType = RevivalPlugin.TypeByName("UnityEngine.Terrain");
            }
            return _terrainType != null && go.GetComponent(_terrainType) != null;
        }

        /// <summary>Mesh roads are not Unity Terrain components. They are still
        /// drive surfaces and must remain solid for the convoy. The hit normal
        /// covers slopes; the ancestor-name check also covers a road edge hit
        /// whose normal points sideways.</summary>
        static bool IsDriveSurface(GameObject go, Vector3 normal)
        {
            if (go == null) return false;
            if (IsTerrain(go) || normal.y >= 0.45f) return true;

            Transform t = go.transform;
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
            {
                string name = t.name == null ? "" : t.name.ToLowerInvariant();
                if (name.IndexOf("road") >= 0 || name.IndexOf("ground") >= 0
                    || name.IndexOf("terrain") >= 0 || name.IndexOf("asphalt") >= 0
                    // Drive surfaces the map names in Russian transliteration, and
                    // the structures a road runs THROUGH or OVER. The 6.8.3 log
                    // has a convoy ghosting "tonel_avto_LOD0" - the road tunnel it
                    // was driving in - and then dropping out of the world through
                    // its own floor.
                    || name.IndexOf("doroga") >= 0 || name.IndexOf("asfalt") >= 0
                    || name.IndexOf("tonel") >= 0 || name.IndexOf("tunnel") >= 0
                    || name.IndexOf("bridge") >= 0 || name.IndexOf("estakada") >= 0
                    || name.IndexOf("trotuar") >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>Cast the same three feeler rays the avoider uses, but instead
        /// of steering, make the convoy car pass through whatever solid, dead,
        /// non-terrain prop each ray finds. Every obstacle is handled once
        /// (Unit.Ghosted), so the per-step cost is a few short raycasts.</summary>
        static void GhostAhead(Unit u, Transform t, float speed)
        {
            if (u.Cols == null) return;
            float range = Mathf.Clamp(speed * 1.5f, 10f, 30f);
            Vector3 nose = t.position + t.forward * NoseOffset + Vector3.up * 1.2f;
            GhostRay(u, nose, t.forward, range);
            GhostRay(u, nose, Quaternion.AngleAxis(-25f, t.up) * t.forward, range * 0.7f);
            GhostRay(u, nose, Quaternion.AngleAxis(25f, t.up) * t.forward, range * 0.7f);
        }

        static void GhostRay(Unit u, Vector3 origin, Vector3 dir, float range)
        {
            Vector3 point;
            Vector3 normal;
            GameObject go = Turret.RaycastObject(origin, dir, range, out point,
                                                  out normal);
            if (go == null) return;
            if (u.Car != null && go.transform.IsChildOf(u.Car.transform)) return;

            int id = go.GetInstanceID();
            if (u.Ghosted == null) u.Ghosted = new Dictionary<int, bool>();
            // A convoy is one event; a patrol drives all session and meets
            // thousands of props. Same cap and same reason as _klein.
            if (u.Ghosted.Count > 4096) u.Ghosted.Clear();
            if (u.Ghosted.ContainsKey(id)) return;
            u.Ghosted[id] = true;                 // decided once, either way

            if (Lebendig(go.transform)) return;   // player, NPC, animal, vehicle: solid
            if (IsDriveSurface(go, normal)) return; // never ghost terrain or mesh roads
            if (!PhysLookUp()) return;

            Component[] cols = go.GetComponentsInChildren(_colliderType, true);
            GhostPair(u.Cols, cols);
            u.Ghosts++;
            if (u.ConvoyId != 0)
            {
                RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId
                    + ": ghosting through \"" + go.name + "\" on "
                    + u.Route.Name + ".");
                return;
            }
            // One line a minute per vehicle, with the running count. Naming
            // every prop a patrol passes through would be the whole log.
            if (Time.time < u.GhostLog) return;
            u.GhostLog = Time.time + 60f;
            RevivalPlugin.L.LogInfo("Patrol: a vehicle on " + u.Route.Name
                + " is ghosting through \"" + go.name + "\" (" + u.Ghosts
                + " obstacle(s) so far).");
        }

        /// <summary>Seconds between two hull sweeps while the vehicle is moving,
        /// and while it is not. Ghosting a prop is permanent for this vehicle,
        /// so a sweep only ever looks for NEW obstacles: half a second is one
        /// sweep every six metres at patrol speed, and a hull that is already
        /// standing against something is asked five times a second.</summary>
        const float SweepEvery = 0.5f;
        const float SweepStuckEvery = 0.2f;

        /// <summary>How far past the hull one sweep reaches: a fixed margin plus
        /// the ground the vehicle covers in SweepLead seconds, so two sweeps in
        /// a row overlap at road speed instead of leaving a gap between them.</summary>
        const float SweepMargin = 2.5f;
        const float SweepLead = 0.4f;

        /// <summary>The band of height, measured from the bottom of the hull, in
        /// which a prop can stop this vehicle.
        ///
        /// It is not a nicety, it is the safety rail. The road under the wheels
        /// is a collider like any other, and a vehicle that ignores the road
        /// falls out of the world. Anything whose top is below SweepFloor is
        /// driven over - a kerb, a rail, a road edge - and anything whose bottom
        /// is above SweepRoof is a canopy, a wire or a balcony the hull passes
        /// under. Only what stands in between is in the way.</summary>
        const float SweepFloor = 0.35f;
        const float SweepRoof = 4.5f;

        /// <summary>Metres across, above which a collider is scenery rather than
        /// an obstacle: a ground plate, a hillside, a whole streamed chunk.
        /// Nothing that size is what a patrol is snagged on, and driving through
        /// one is how a vehicle leaves the map.</summary>
        const float SweepBiggest = 60f;

        /// <summary>Metres across, below which a thing UNDER the hull may be
        /// passed through after all - but only for a vehicle that has stopped
        /// moving. A block, a barrier, a pile of tyres is something a hull can
        /// climb onto and then sit on with its wheels in the air, and while it
        /// sits there nothing in front of it is the problem. A surface the size
        /// of a yard is not that; it is what the vehicle drives on, whatever the
        /// map happens to call it, and it stays solid.</summary>
        const float SweepStuckSmall = 6f;

        /// <summary>Props one sweep may hand to Physics.IgnoreCollision. Each
        /// costs a reflected call per collider of this vehicle, and a yard full
        /// of scenery would otherwise be paid for in a single frame. What is
        /// left over is not recorded as decided, so the next sweep - two tenths
        /// of a second later - takes the rest.</summary>
        const int SweepAtOnce = 24;

        /// <summary>The collector of one sweep, and the props it decided to pass
        /// through. Both are reused: a sweep runs twice a second per vehicle and
        /// must not hand the garbage collector an array each time.</summary>
        static readonly Collider[] _sweepHits = new Collider[256];
        static readonly List<Component> _sweepThrough = new List<Component>();

        /// <summary>
        /// The obstacles the three feeler rays cannot see.
        ///
        /// A ray finds what stands in front of the NOSE, at 1.2 m, along one of
        /// three lines. Three things it therefore never finds, and all three are
        /// how a patrol vehicle actually comes to a stop:
        ///
        ///   - a prop lower than the ray. A concrete block, a bollard, a pile of
        ///     tyres: the hull catches it, the rays pass over it.
        ///   - a prop at the flank. The rays leave the nose; the corner of a
        ///     seven-metre hull does not follow them.
        ///   - a prop the nose is ALREADY inside. A ray that starts inside a
        ///     collider reports no hit at all, so the one obstacle that has
        ///     certainly stopped the vehicle is the one obstacle the ghosting
        ///     never hears about. That is the stop that lasts all session, and
        ///     it is the one the user keeps reporting.
        ///
        /// So the hull sweeps for itself instead of waiting to be pointed at
        /// something: everything solid within reach is made to ignore this
        /// vehicle. The ray's rules still hold - terrain, mesh roads, tunnel
        /// floors and everything alive stay solid - and two more are added,
        /// because a sphere drawn around a vehicle contains the road it stands
        /// on: the height band above, and the surface the vehicle is resting on,
        /// which stays solid whatever it happens to be called.
        ///
        /// The one exception to "alive stays solid" is another vehicle of this
        /// class, and only once this one has STOPPED. Two hulls that are both
        /// driving are traffic: physics shoves them past each other and it looks
        /// like a road with vehicles on it. Two that have come to a halt nose to
        /// nose, one patrol against another, are the one pair on the map that
        /// nothing separates - PatrolSeparate eases apart mates of the same
        /// group, and neither driver will ever give way - so at that point they
        /// pass through each other instead of holding the road.
        /// </summary>
        static void GhostAround(Unit u, Transform t, float speed, bool stuck)
        {
            if (u.Cols == null || !PhysLookUp() || !IgnoreLookUp()) return;
            if (Time.time < u.GhostSweep) return;
            u.GhostSweep = Time.time + (stuck ? SweepStuckEvery : SweepEvery);

            if (u.ColumnLift <= 0f) ColumnFootprint(u);
            float hull = Mathf.Sqrt(u.ColumnHalfLength * u.ColumnHalfLength
                                    + u.ColumnHalfWidth * u.ColumnHalfWidth);
            float reach = hull + SweepMargin + Mathf.Max(0f, speed) * SweepLead;
            float bottom = t.position.y - u.ColumnLift;

            int found;
            try
            {
                // Every layer, not the raycast default: a prop on IgnoreRaycast
                // is invisible to the feeler rays and still perfectly solid to
                // the hull, which is precisely the case this sweep is for.
                // Triggers stay out - a zone volume is not an obstacle, and
                // ignoring one would cost the game the event it fires.
                found = Physics.OverlapSphereNonAlloc(t.position, reach,
                    _sweepHits, Physics.AllLayers,
                    QueryTriggerInteraction.Ignore);
            }
            catch (Exception ex)
            {
                // A physics call that throws is not worth retrying every step.
                u.GhostSweep = Time.time + 30f;
                RevivalPlugin.L.LogWarning("Patrol: the hull sweep on "
                    + u.Route.Name + " failed - " + ex.Message);
                return;
            }
            if (found <= 0) return;
            if (found > _sweepHits.Length) found = _sweepHits.Length;

            // Whatever the vehicle is standing on is never an obstacle, whatever
            // it is called. Deliberately NOT written down as decided: twenty
            // metres on, the same object can be the wall beside the road.
            Vector3 hit;
            GameObject floor = Turret.RaycastObject(t.position + Vector3.up * 0.5f,
                Vector3.down, u.ColumnLift + 2f, out hit);
            if (floor != null && u.Car != null
                && floor.transform.IsChildOf(u.Car.transform)) floor = null;

            if (u.Ghosted == null) u.Ghosted = new Dictionary<int, bool>();
            if (u.Ghosted.Count > 4096) u.Ghosted.Clear();

            _sweepThrough.Clear();
            for (int i = 0; i < found; i++)
            {
                // Stop BEFORE recording anything: what this sweep does not get
                // to must stay undecided, or it would never be looked at again.
                if (_sweepThrough.Count >= SweepAtOnce) break;
                Collider c = _sweepHits[i];
                _sweepHits[i] = null;                 // hold nothing between sweeps
                if (c == null) continue;
                Transform ct = c.transform;
                if (u.Car != null && ct.IsChildOf(u.Car.transform)) continue;
                if (floor != null && ct.IsChildOf(floor.transform)) continue;
                int id = c.GetInstanceID();
                if (u.Ghosted.ContainsKey(id)) continue;

                // Cheap, and true only for where the vehicle stands right now,
                // so it is asked every sweep and never recorded.
                Bounds b = c.bounds;
                if (b.min.y > bottom + SweepRoof) continue;
                if (b.size.x > SweepBiggest || b.size.z > SweepBiggest) continue;
                if (b.max.y < bottom + SweepFloor
                    && (!stuck || b.size.x > SweepStuckSmall
                        || b.size.z > SweepStuckSmall)) continue;

                // One of ours? Asked first, and the answer is never written
                // down: two vehicles that are only passing each other must be
                // able to meet again later as two that have stopped.
                Unit mate = DrivenByUs(ct);
                if (mate != null)
                {
                    if (stuck) Mates(u, mate);
                    continue;
                }
                u.Ghosted[id] = true;                 // decided once, either way
                if (Lebendig(ct)) continue;           // player, NPC, animal
                if (IsDriveSurface(c.gameObject, Vector3.zero)) continue;
                _sweepThrough.Add(c);
            }
            if (_sweepThrough.Count == 0) return;

            GhostPair(u.Cols, _sweepThrough.ToArray());
            u.Ghosts += _sweepThrough.Count;
            string first = _sweepThrough[0] == null ? "?" : _sweepThrough[0].name;
            int n = _sweepThrough.Count;
            _sweepThrough.Clear();

            // One line a minute per vehicle, same clock and same reason as the
            // ray: a sweep can pass through a whole yard at once, and naming
            // every piece of it would be the entire log.
            if (Time.time < u.GhostLog) return;
            u.GhostLog = Time.time + 60f;
            RevivalPlugin.L.LogInfo((u.ConvoyId != 0
                    ? "Convoy " + u.ConvoyId : "Patrol: a vehicle")
                + " on " + u.Route.Name + " swept " + n + " obstacle(s) out of "
                + "its way, \"" + first + "\" among them (" + u.Ghosts
                + " so far).");
        }

        /// <summary>The unit this collider belongs to, or null when the thing is
        /// alive but none of ours - the player, his vehicle, an NPC, an animal.
        /// Walked with IsChildOf rather than by root, so it holds however the
        /// game parents a spawned car.</summary>
        static Unit DrivenByUs(Transform t)
        {
            for (int i = 0; i < _units.Count; i++)
            {
                GameObject car = _units[i].Car;
                if (car != null && t.IsChildOf(car.transform)) return _units[i];
            }
            return null;
        }

        /// <summary>Two vehicles of this class pass through each other from here
        /// on. Line-mates are paired at spawn already; this is the pair that
        /// meets by accident and then stops - two patrols of different groups on
        /// one road, nose to nose, with nothing on either side that ever gives
        /// way. Their whole collider sets are paired, not only the parts in
        /// reach, and every one of them is recorded so the sweep does not ask
        /// again.</summary>
        static void Mates(Unit u, Unit mate)
        {
            if (mate.Cols == null) return;
            // A convoy's burning mate belongs to StartBypass, which drives AROUND
            // it and looks like a convoy doing so. That layer exists, it works,
            // and a wreck is the one obstacle worth the detour.
            if (mate.Died > 0f && u.ConvoyId != 0) return;
            bool known = (u.ConvoyId != 0 && mate.ConvoyId == u.ConvoyId)
                || (u.PatrolGroupId != 0
                    && mate.PatrolGroupId == u.PatrolGroupId);
            GhostPair(u.Cols, mate.Cols);
            for (int i = 0; i < mate.Cols.Length; i++)
                if (mate.Cols[i] != null)
                    u.Ghosted[mate.Cols[i].GetInstanceID()] = true;
            // Line-mates were paired at spawn; pairing them again is free and
            // saying so every time they touch is not.
            if (known) return;
            RevivalPlugin.L.LogInfo("Patrol: the vehicle on " + u.Route.Name
                + " and the one on " + mate.Route.Name + " met on the same road "
                + "- they pass through each other instead of holding it.");
        }

        // =====================================================================
        //  Fail-fast recovery
        // =====================================================================

        /// <summary>A confirmed stop has one outcome: move forward along the
        /// route. There is deliberately no reverse or ramming stage. A blocked
        /// patrol is worse for the game than a vehicle passing through scenery.
        ///
        /// TWO things count as a confirmed stop. The speedometer under 3 km/h
        /// for StuckSeconds is the old one and catches a vehicle against a wall.
        /// <see cref="NoProgress"/> is the second, and it exists because the
        /// first misses the case the user reported: hulls that lie inside each
        /// other are shoved apart by the physics engine every step, so they
        /// SHAKE, read as moving, and hold a road for as long as the session
        /// lasts without the timer ever filling.</summary>
        static bool Escalate(Unit u, Vector3 pos)
        {
            float stuckFor = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolStuck.Value);
            // A vehicle that has already had to be carried once does not get the
            // full benefit of the doubt again. Three seconds of standing is a
            // fair wait for a patrol that might still free itself; for one that
            // stopped on this same stretch a minute ago it is three seconds of
            // the thing the user is complaining about.
            if (u.RailRuns > 0 && Time.time - u.RailAt <= RailForget) stuckFor *= 0.5f;
            bool slow = u.Stuck >= stuckFor;
            // The convoy has its own spacing, braking and separation layer and
            // its own reasons to stand still; this second test is the patrol's.
            if (!slow && (u.ConvoyId != 0 || !NoProgress(u, pos))) return false;
            // An ordinary patrol is carried out of it along its own line. The
            // warp below is what produced the loop the rail exists to end, and
            // it stays only for the convoy - whose column, deploy and lane layer
            // owns where its vehicles may be put - and for a route with no line.
            if (u.ConvoyId == 0
                && RailOn(u, pos, slow
                    ? "under 3 km/h for " + u.Stuck.ToString("0") + " s"
                    : "has not covered " + ProgressMetres.ToString("0")
                      + " m in " + ProgressSeconds.ToString("0") + " s"))
                return true;
            Free(u, pos);
            return true;
        }

        /// <summary>This is where the vehicle has got to, as of now.</summary>
        static void MadeProgress(Unit u, Vector3 pos)
        {
            u.ProgressPos = pos;
            u.ProgressAt = Time.time;
        }

        /// <summary>Has this vehicle failed to get anywhere at all? True once it
        /// has stayed inside <see cref="ProgressMetres"/> of the same spot for
        /// <see cref="ProgressSeconds"/> while the driver was driving. Anything
        /// that stands still on purpose goes through <see cref="HoldStill"/>,
        /// which resets the clock, so waiting never reads as failing.</summary>
        static bool NoProgress(Unit u, Vector3 pos)
        {
            if (u.ProgressAt <= 0f)
            {
                MadeProgress(u, pos);
                return false;
            }
            if (FlatDistance(pos, u.ProgressPos) > ProgressMetres)
            {
                MadeProgress(u, pos);
                // Ten metres of real ground ends the refusal streak. Deliberately
                // NOT the speedometer: two hulls inside each other shake, so a
                // speed test would clear the streak every step and the deadlock
                // break below would never be reached.
                u.Refusals = 0;
                return false;
            }
            return Time.time - u.ProgressAt >= ProgressSeconds;
        }

        /// <summary>
        /// The leash: a patrol that has left its own recorded line for good.
        ///
        /// Everything else in this class measures whether the vehicle is MOVING.
        /// A tank driving through a wood is moving perfectly well - it is simply
        /// nowhere near the road it is supposed to patrol, and once the avoider
        /// has walked it off the line nothing used to walk it back. It is put
        /// back on the nearest waypoint that has ground and no other vehicle
        /// standing on it; the log says how far out it was, because a patrol
        /// that needs this often has a route with a bad stretch in it.
        ///
        /// Returns true when the vehicle was moved and the driver must stop for
        /// this step.
        /// </summary>
        static bool Leashed(Unit u, Vector3 pos)
        {
            float off = OffRoute(u.Route, u.Next, pos);
            if (off <= LeashMetres)
            {
                u.OffRouteSince = 0f;
                return false;
            }
            if (u.OffRouteSince <= 0f)
            {
                u.OffRouteSince = Time.time;
                return false;
            }
            if (Time.time - u.OffRouteSince < LeashSeconds) return false;
            u.OffRouteSince = 0f;
            // Off the line and finding its own way back is exactly what never
            // worked: it is put back on the line and CARRIED along it until it
            // is somewhere it can drive again. BackOnRoute stays for the route
            // the rail cannot use.
            if (RailOn(u, pos, off.ToString("0") + " m off its own route for "
                               + LeashSeconds.ToString("0") + " s"))
                return true;
            return BackOnRoute(u, pos, off);
        }

        /// <summary>Flat distance from a position to the stretch of route the
        /// vehicle is driving: the leg it is on and the one after it. Two legs
        /// are enough - the driver never aims past the next corner - and it is
        /// two dot products, which is what a per-step test may cost.</summary>
        static float OffRoute(Route r, int next, Vector3 pos)
        {
            int n = r.P.Count;
            if (n < 2) return 0f;
            next = ((next % n) + n) % n;
            float here = LegDistance(r.P[(next - 1 + n) % n].Pos, r.P[next].Pos, pos);
            float after = LegDistance(r.P[next].Pos, r.P[(next + 1) % n].Pos, pos);
            return Mathf.Min(here, after);
        }

        /// <summary>Flat distance from a point to one leg of a route.</summary>
        static float LegDistance(Vector3 from, Vector3 to, Vector3 pos)
        {
            Vector3 leg = to - from;
            leg.y = 0f;
            Vector3 rel = pos - from;
            rel.y = 0f;
            float len = leg.sqrMagnitude;
            if (len < 0.01f) return rel.magnitude;
            float along = Mathf.Clamp01(Vector3.Dot(rel, leg) / len);
            return (rel - leg * along).magnitude;
        }

        /// <summary>Put a lost vehicle back on the nearest waypoint of its own
        /// route that has ground under it and room for a hull. False when the
        /// route has no such waypoint near it right now - then the vehicle keeps
        /// driving and the leash asks again in <see cref="LeashSeconds"/>, which
        /// is better than warping it into whatever is standing there.</summary>
        static bool BackOnRoute(Unit u, Vector3 pos, float off)
        {
            Route r = u.Route;
            Vector3 target;
            int at = GroundedWaypoint(r, NearestOn(u, pos), 1.5f, !u.OneWay, out target);
            for (int tries = 0; at >= 0 && tries < GroundTries
                                && SpotTaken(u, target, FreeRoom); tries++)
                at = GroundedWaypoint(r, at + 1, 1.5f, !u.OneWay, out target);
            if (at < 0 || SpotTaken(u, target, FreeRoom))
            {
                if (Time.time >= u.FallLog)
                {
                    u.FallLog = Time.time + 10f;
                    RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name
                        + " is " + off.ToString("0") + " m off its route, but no "
                        + "waypoint near it has free ground - it keeps driving.");
                }
                return false;
            }

            Stop(u.Body);
            SetFloat(u.Rcc, "gasInput", 0f);
            SetFloat(u.Rcc, "brakeInput", 0f);
            SetFloat(u.Rcc, "steerInput", 0f);
            SetFloat(u.Rcc, "handbrakeInput", 0f);
            u.Car.transform.position = target;
            u.Car.transform.rotation = Quaternion.LookRotation(
                RouteDirection(r, at, u.OneWay).normalized, Vector3.up);

            u.Next = at;
            u.Stuck = 0f;
            u.Queued = 0f;
            MadeProgress(u, target);

            RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name + " was "
                + off.ToString("0") + " m off its own route for "
                + LeashSeconds.ToString("0") + " s - put back on waypoint " + at
                + ".");
            return true;
        }

        /// <summary>Put the vehicle on the first waypoint at least five metres
        /// farther along the route AND <see cref="FreeClear"/> metres from the
        /// place it got stuck, then face it down the following leg. Dense
        /// recordings may need several points to cover that distance.
        ///
        /// THE SECOND CONDITION IS THE FIX FOR "stuck on a vehicle". Whatever
        /// stopped the hull is within a few metres of it, and a car parked on
        /// the road stands ON the recorded line: five metres along that line is
        /// the middle of the obstacle, so the warp used to drop the patrol
        /// INSIDE the thing it was stuck against, every StuckSeconds, for the
        /// rest of the session. Fifteen metres is past anything that is parked
        /// there.</summary>
        static void Free(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            int n = r.P.Count;
            int from = u.Next;
            int to = from;
            float advanced = 0f;
            int steps = 0;
            // The clearance is the ordinary patrol's. A convoy keeps the five
            // metres it always had: its own spacing, braking and firing-position
            // layer (ConvoyBlocked, DeployRoom, DeployLane) already decides what
            // a convoy vehicle may be moved onto.
            float clearOf = u.ConvoyId == 0 ? FreeClear : 0f;
            while (steps < n - 1
                   && (advanced < 5f || FlatDistance(r.P[to].Pos, pos) < clearOf))
            {
                // A one-way convoy never wraps back to waypoint 0: if it is stuck
                // near the end there is nothing farther along, so it clamps to the
                // last waypoint and Advance turns that into arrive-and-vanish next
                // tick. A looping patrol wraps as before.
                int next = u.OneWay ? Mathf.Min(to + 1, n - 1) : (to + 1) % n;
                if (next == to) break;
                advanced += FlatDistance(r.P[to].Pos, r.P[next].Pos);
                to = next;
                steps++;
            }

            // Only onto a waypoint that HAS ground, and that nothing is standing
            // on. Warping the hull onto an authored point with nothing under it
            // is what turned one stuck vehicle into a vehicle that falls, is
            // warped up, and falls again every StuckSeconds; warping it onto a
            // point another vehicle occupies is how two hulls of one patrol end
            // up inside each other, which neither of them can drive out of.
            Vector3 target;
            int landed = GroundedWaypoint(r, to, 1.5f, !u.OneWay, out target);
            // WHICH of the two requirements was never met. The retry below
            // overwrites `landed` with -1 as soon as it walks off the end of the
            // search, so the old code reported a spot held by another VEHICLE as
            // a spot with no GROUND. Those are different faults with different
            // fixes, and the one the civilian patrol actually had was the second
            // reported as the first - which is why the log never named it.
            bool anyGround = landed >= 0;
            if (u.ConvoyId == 0)
                for (int tries = 0; landed >= 0 && tries < GroundTries
                                    && SpotTaken(u, target, FreeRoom); tries++)
                    landed = GroundedWaypoint(r, landed + 1, 1.5f, !u.OneWay,
                                              out target);
            // The deadlock: every candidate is free ground held by a MATE, and
            // the mate is refusing for the same reason. See HoldsBeforeForce.
            bool forced = false;
            if (landed < 0 && anyGround && u.ConvoyId == 0
                && u.Refusals >= HoldsBeforeForce)
            {
                landed = GroundedWaypoint(r, to, 1.5f, !u.OneWay, out target);
                for (int tries = 0; landed >= 0 && tries < GroundTries
                                    && SpotBlocked(u, target, FreeRoom); tries++)
                    landed = GroundedWaypoint(r, landed + 1, 1.5f, !u.OneWay,
                                              out target);
                if (landed >= 0 && !SpotBlocked(u, target, FreeRoom))
                {
                    forced = true;
                    RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name
                        + " was held " + u.Refusals + " times running by a mate "
                        + "standing on every free waypoint - warping onto one of "
                        + "them, the two ignore each other's colliders.");
                }
                else landed = -1;
            }
            if (landed < 0)
            {
                FreeHold(u, from, to, anyGround
                    ? "is free of other vehicles - holding where it stands "
                      + "instead of warping into one of them"
                    : "has ground under it - holding where it stands instead of "
                      + "warping into nothing");
                return;
            }
            if (u.ConvoyId == 0 && !forced && SpotTaken(u, target, FreeRoom))
            {
                FreeHold(u, from, to, "is free of other vehicles - holding where "
                    + "it stands instead of warping into one of them");
                return;
            }
            to = landed;
            Vector3 ahead = RouteDirection(r, to, u.OneWay);

            if (u.ConvoyId != 0 && (!DeployRoom(u, target) || !DeployLane(u, target)))
            {
                HoldStill(u);
                Roll(u.Body, Vector3.zero);
                u.Stuck = 0f;
                return;
            }

            Stop(u.Body);
            SetFloat(u.Rcc, "gasInput", 0f);
            SetFloat(u.Rcc, "brakeInput", 0f);
            SetFloat(u.Rcc, "steerInput", 0f);
            SetFloat(u.Rcc, "handbrakeInput", 0f);
            u.Car.transform.position = target;
            u.Car.transform.rotation = Quaternion.LookRotation(ahead.normalized, Vector3.up);

            u.Frees++;
            u.Next = to;
            u.Stuck = 0f;
            u.Queued = 0f;
            u.Refusals = 0;
            u.OffRouteSince = 0f;
            MadeProgress(u, target);

            RevivalPlugin.L.LogWarning("Patrol: FREE on " + r.Name + " - stuck at "
                + pos + " near waypoint " + from + ", moved "
                + advanced.ToString("0.0") + " m forward onto waypoint " + to
                + ". (" + u.Frees + " so far)");
        }

        /// <summary>The stuck recovery found nowhere to put the hull: hold it
        /// where it stands and say why, at most once every ten seconds. Standing
        /// still is a bad outcome; warping into nothing, or into another
        /// vehicle, is a worse one and lasts the rest of the session.</summary>
        static void FreeHold(Unit u, int from, int to, string why)
        {
            HoldStill(u);
            Roll(u.Body, Vector3.zero);
            u.Stuck = 0f;
            // Counted, not just logged: the same refusal over and over is the
            // deadlock, and Free needs to know how deep in it this vehicle is.
            u.Refusals++;
            if (Time.time < u.FallLog) return;
            u.FallLog = Time.time + 10f;
            RevivalPlugin.L.LogWarning("Patrol: stuck on " + u.Route.Name
                + " near waypoint " + from + ", but no waypoint from " + to
                + " on " + why + ".");
        }

        // =====================================================================
        //  The rail: a patrol that cannot drive its road is carried along it
        // =====================================================================

        /// <summary>Arc length of the whole line a vehicle drives: the recorded
        /// points for a one-way convoy, and the closing leg from the last
        /// waypoint back to waypoint 0 on top of that for a patrol driving laps.
        /// <see cref="Metrics"/> measures the recording, which does not include
        /// that closing leg - and a lap route is nothing BUT a loop, so the rail
        /// has to know how long the loop really is.</summary>
        static float RailSpan(Route r, bool oneWay)
        {
            Metrics(r);
            int n = r.P.Count;
            if (n < 2) return 0f;
            if (oneWay) return r.Length;
            return r.Length + FlatDistance(r.P[n - 1].Pos, r.P[0].Pos);
        }

        /// <summary>The point <paramref name="arc"/> metres along the line, with
        /// the lap route's closing leg included and the arc wrapped into the
        /// loop. <see cref="PointOnRoute"/> clamps at both ends, which is right
        /// for the one-way column it was written for and would park a carried
        /// patrol on its last waypoint forever.</summary>
        static Vector3 RailPoint(Route r, float arc, bool oneWay, out int seg)
        {
            Metrics(r);
            int n = r.P.Count;
            seg = 0;
            if (n == 0) return Vector3.zero;
            if (n == 1) return r.P[0].Pos;
            if (oneWay) return PointOnRoute(r, arc, out seg);

            float span = RailSpan(r, false);
            if (span < 0.01f) return r.P[0].Pos;
            arc -= Mathf.Floor(arc / span) * span;
            if (arc < r.Length) return PointOnRoute(r, arc, out seg);

            // On the closing leg, which Cum does not cover: it runs from the
            // last waypoint to waypoint 0, so 0 is what it drives towards.
            float len = span - r.Length;
            float t = len > 0.001f ? (arc - r.Length) / len : 0f;
            return Vector3.Lerp(r.P[n - 1].Pos, r.P[0].Pos, Mathf.Clamp01(t));
        }

        /// <summary>The direction of travel at <paramref name="arc"/>, measured
        /// over a span of the line for the reason <see cref="HeadingOnRoute"/>
        /// gives: a dense recording has legs of a few metres, and a heading taken
        /// from one of those turns the hull with every waypoint.</summary>
        static Vector3 RailHeading(Route r, float arc, bool oneWay)
        {
            int ignore;
            Vector3 back = RailPoint(r, arc - 7f, oneWay, out ignore);
            Vector3 fwd = RailPoint(r, arc + 7f, oneWay, out ignore);
            Vector3 d = fwd - back;
            d.y = 0f;
            if (d.sqrMagnitude > 0.0001f) return d.normalized;
            d = RailPoint(r, arc + 25f, oneWay, out ignore)
              - RailPoint(r, arc, oneWay, out ignore);
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
        }

        /// <summary>Where on the line this hull is standing, in metres along it.
        /// The waypoint it was driving to is the hint, and only the legs around
        /// that one are considered: see <see cref="RailLook"/> for why the
        /// nearest leg in the WORLD is the wrong answer.</summary>
        static float RailArcAt(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            Metrics(r);
            int n = r.P.Count;
            if (n < 2) return 0f;
            float span = RailSpan(r, u.OneWay);
            int legs = u.OneWay ? n - 1 : n;      // the lap route closes, the convoy does not
            if (legs < 1) return 0f;

            float best = 0f;
            float bestOff = 0f;
            bool have = false;
            for (int k = 0; k <= 2 * RailLook; k++)
            {
                // The legs in the order 0, -1, +1, -2, +2 ... , counted from the
                // one the vehicle was driving, and a later leg has to be
                // STRICTLY closer to the hull to beat an earlier one.
                //
                // Both halves matter. On an out-and-back road the outbound leg
                // and the return leg lie on top of each other, so the hull is
                // zero metres from BOTH and the answer would otherwise be
                // whichever index the loop reached first - which is the outbound
                // one, the wrong carriageway, the reported bug.
                int step = ((k + 1) / 2) * ((k % 2) == 0 ? 1 : -1);
                int j = u.Next - 1 + step;        // leg j runs P[j] -> P[j+1]
                if (u.OneWay) { if (j < 0 || j >= legs) continue; }
                else j = ((j % legs) + legs) % legs;

                Vector3 from = r.P[j].Pos;
                Vector3 to = r.P[(j + 1) % n].Pos;
                float off = LegDistance(from, to, pos);
                if (have && off >= bestOff) continue;

                Vector3 leg = to - from; leg.y = 0f;
                Vector3 rel = pos - from; rel.y = 0f;
                float len = leg.sqrMagnitude;
                float along = len < 0.01f ? 0f : Mathf.Clamp01(Vector3.Dot(rel, leg) / len);
                float start = r.Cum[j];
                best = start + (j < n - 1 ? r.Cum[j + 1] - start : span - start) * along;
                bestOff = off;
                have = true;
            }
            return best;
        }

        /// <summary>Take the wheel away from this vehicle and put it on the rail,
        /// starting where it stands. False when the route has no line to be
        /// carried along, which is the one case the old warp still has to answer.
        ///
        /// The carry starts at the vehicle's OWN arc, not fifteen metres further
        /// on: it does not blink somewhere else, it simply begins to move, and
        /// whatever is holding it is gone within a second because the hull is now
        /// going through it. That is the whole point of the thing.</summary>
        static bool RailOn(Unit u, Vector3 pos, string why)
        {
            Route r = u.Route;
            if (r == null || r.P.Count < 2) return false;
            if (RailSpan(r, u.OneWay) < 1f) return false;
            if (u.Rail) return true;

            // Carries close together are the loop the user reported; carries an
            // hour apart are two ordinary bad moments on a long shift.
            if (u.RailRuns > 0 && Time.time - u.RailAt <= RailForget) u.RailRuns++;
            else u.RailRuns = 1;
            u.RailAt = Time.time;

            u.Rail = true;
            u.RailArc = RailArcAt(u, pos);
            u.RailFrom = u.RailArc;
            u.RailSince = Time.time;
            // It keeps the speed it had, which after a confirmed stop is nearly
            // none - the carry eases up to road speed like a column instead of
            // snapping there.
            u.RailSpeed = Velocity(u.Body).magnitude;
            u.Placed = false;                 // the first placement snaps, then it eases
            u.Frees++;
            u.Stuck = 0f;
            u.Queued = 0f;
            u.Refusals = 0;
            u.OffRouteSince = 0f;
            MadeProgress(u, pos);

            if (!u.RailForever && u.RailRuns >= RailRunsStick)
            {
                u.RailForever = true;
                RevivalPlugin.L.LogWarning("Patrol: the vehicle on " + r.Name
                    + " has been carried " + u.RailRuns + " times in "
                    + RailForget.ToString("0") + " s - it stays on its line for "
                    + "good now. This stretch of the route is beyond it ("
                    + why + ").");
            }
            else
            {
                RevivalPlugin.L.LogWarning("Patrol: RAIL on " + r.Name + " at "
                    + pos + " - " + why + ". Carried along the recorded line from "
                    + u.RailArc.ToString("0") + " m (carry " + u.RailRuns + ").");
            }
            return true;
        }

        /// <summary>Hand the wheel back: the hull is on the road, pointing down
        /// it, at road speed. Everything that measures failure starts again from
        /// here, because none of it happened to the vehicle that is driving
        /// now.</summary>
        static void RailOff(Unit u, string why)
        {
            if (!u.Rail) return;
            u.Rail = false;
            u.Placed = false;
            u.Stuck = 0f;
            u.Queued = 0f;
            u.OffRouteSince = 0f;
            u.Airborne = false;
            u.FallSince = 0f;
            u.NextGround = Time.time + FallSeconds;   // let it settle before the guard judges it
            if (u.Car != null) MadeProgress(u, u.Car.transform.position);
            RevivalPlugin.L.LogInfo("Patrol: the vehicle on " + u.Route.Name
                + " drives itself again - " + why + ".");
        }

        /// <summary>One step of one carried vehicle: how fast the road says it
        /// may go here, that much further along the line, and put down on it.
        /// The driver does not run for this vehicle at all.</summary>
        static void RailStep(Unit u, float dt)
        {
            Route r = u.Route;
            Transform t = u.Car.transform;
            float span = RailSpan(r, u.OneWay);
            if (span < 1f) { RailOff(u, "its route has no line left to carry it along"); return; }

            int seg;
            RailPoint(r, u.RailArc, u.OneWay, out seg);

            // The recorded speed of the waypoint it is heading for, eased for the
            // corner exactly as the driver would ease for it - a carried vehicle
            // that takes a hairpin at fifty looks like a bug even when it is on
            // the line to the centimetre.
            float want = r.P[seg].Speed;
            if (want <= 0f) want = RevivalPlugin.CfgPatrolSpeed.Value;
            want *= u.OneWay ? ColumnCorner(r, u.RailArc) : CornerFactor(r, seg);
            want = Mathf.Max(want, 12f);
            // Behind a group mate, not through it. The rail cannot be blocked by
            // anything else, but two vehicles of one composition share a road and
            // the gap between them is the driver's job in both modes.
            want = QueueBehind(u, t, want, dt);

            u.RailSpeed = Mathf.MoveTowards(u.RailSpeed, want / 3.6f, ColumnAccel * dt);
            u.RailArc += u.RailSpeed * dt;

            // A one-way convoy arrives at the end of its line and vanishes there,
            // carried or not. A patrol wraps, and the lap counts.
            if (u.OneWay && u.RailArc >= span) { u.Arrived = true; return; }
            if (!u.OneWay && u.RailArc >= span)
            {
                u.RailArc -= span;
                u.RailFrom -= span;
                u.Lap++;
            }

            Vector3 line = RailPoint(r, u.RailArc, u.OneWay, out seg);
            Carry(u, line, RailHeading(r, u.RailArc, u.OneWay), u.RailSpeed,
                  "Patrol rail on " + r.Name + ":");

            u.Next = seg;
            u.Stuck = 0f;
            u.OffRouteSince = 0f;
            u.Airborne = false;
            u.FallSince = 0f;
            MadeProgress(u, t.position);

            float went = u.RailArc - u.RailFrom;
            if (u.RailForever) return;
            if (went < RailMetres || Time.time - u.RailSince < RailSeconds) return;
            RailOff(u, "it is " + went.ToString("0") + " m further down its route "
                + "and doing " + (u.RailSpeed * 3.6f).ToString("0") + " km/h");
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            return d.magnitude;
        }

        static Vector3 RouteDirection(Route r, int at, bool oneWay)
        {
            int n = r.P.Count;
            // Forward: the next non-degenerate leg from 'at'. A one-way convoy
            // does not wrap past the last waypoint (that would face it back down
            // the route towards wp0), so at the end it falls back to the leg it
            // arrived on - the direction it was already travelling.
            int limit = oneWay ? (n - at) : n;
            for (int step = 1; step < limit; step++)
            {
                Vector3 ahead = r.P[(at + step) % n].Pos - r.P[at].Pos;
                ahead.y = 0f;
                if (ahead.sqrMagnitude >= 0.0001f) return ahead;
            }
            if (oneWay)
            {
                for (int step = 1; step <= at; step++)
                {
                    Vector3 ahead = r.P[at].Pos - r.P[at - step].Pos;
                    ahead.y = 0f;
                    if (ahead.sqrMagnitude >= 0.0001f) return ahead;
                }
            }
            return Vector3.forward;
        }

        // =====================================================================
        //  The gunner
        // =====================================================================

        /// <summary>
        /// The gun of a patrol vehicle, aimed by a state machine instead of by
        /// a mouse. It is the SAME gun the player mans - the same turret
        /// transforms, the same two value profiles out of [Turret] and [Tank],
        /// the same tracer - and this class adds only the three things a human
        /// brings: it looks for a target, it turns the barrel, and it misses.
        ///
        /// HOW IT MISSES, AND WHY EXACTLY LIKE THIS (read out of the game,
        /// 2026-08-30: NPC_AI2::ShootToTarget, NPC_AI2::CalcChancesToHit,
        /// NPC_FirearmWeaponController::GetSqrDistanceModifier)
        ///
        ///   The game's own NPCs do not spray a cone. They roll ONE chance per
        ///   shot and then displace the aim point by whole metres:
        ///
        ///       chance = base by target stance (0.5 crouched to 1.0 standing)
        ///                minus the distance loss, clamped to 0..1
        ///       loss   = 0 inside the weapon's effective range, rising along
        ///                a cosine to 1 at maximum range, and a loss over 0.95
        ///                sets the chance to zero outright
        ///       offset = 0.2 m under 30 m - practically a hit
        ///                a rolled hit:  up to 2.5 m
        ///                a rolled miss: 3 m
        ///
        ///   That is the "factor common among the NPCs" this gun uses. THREE
        ///   things are deliberately different. The offset here goes in a
        ///   random DIRECTION around the aim point instead of into +x and +y
        ///   together, because the game's version puts every miss of every NPC
        ///   on the same diagonal. A rolled hit is displaced by 0.25 m, not by
        ///   2.5: the game gets away with the large number because
        ///   NPC_FirearmWeaponController::FireTo decides the damage itself,
        ///   while this gun is a raycast - at 150 m a 2.5 m offset would miss
        ///   a man every time and the chance above would mean nothing.
        ///
        ///   And the third, added on 2026-08-30 after the user reported a
        ///   patrol as "an 80 percent death sentence": the free ring under
        ///   30 m is GONE as a constant. It is `GunPointBlank` now, 12 m by
        ///   default, and the chance is rolled inside it like anywhere else.
        ///   That one line was the whole difficulty problem - a road vehicle
        ///   and a man on foot end up inside 30 m of each other in every
        ///   fight, and inside that ring the gun was perfect no matter what
        ///   GunAccuracy said.
        ///
        /// COST. Player, vehicle and NPC candidates are shared by all guns.
        /// NPC scene discovery is shared with NpcWar every two seconds; target
        /// scans are staggered at half-second intervals. Distance and faction
        /// are tested before sight. Every fired round rechecks its firing line.
        /// </summary>
        sealed class CombatTarget
        {
            public Transform Tr;
            public Component States, Npc, Vehicle;
        }

        static class Gun
        {
            /// <summary>Degrees between barrel and target inside which the
            /// gun considers itself laid and pulls the trigger.</summary>
            const float FireWithin = 2.5f;

            /// <summary>Seconds between two target scans of one vehicle. The
            /// scan is the expensive half - it casts a ray.</summary>
            const float ScanEvery = 0.5f;

            // ------------------------------------------------------- targets

            class Spieler
            {
                public Transform Tr;
                public Component States;     // PlayerStatesController, may be null
            }

            static List<Spieler> _players = new List<Spieler>();
            static float _nextRefresh;
            static readonly List<CombatTarget> _targets = new List<CombatTarget>();
            static float _nextTargets;

            static void RefreshTargets()
            {
                if (Time.time < _nextTargets) return;
                _nextTargets = Time.time + ScanEvery;
                _targets.Clear();
                for (int i = 0; i < _players.Count; i++)
                {
                    Spieler s = _players[i];
                    if (s.Tr == null) continue;
                    CombatTarget c = new CombatTarget();
                    c.Tr = s.Tr; c.States = s.States;
                    _targets.Add(c);
                }
                List<Component> npcs = NpcWar.PatrolTargets();
                for (int i = 0; i < npcs.Count; i++)
                {
                    Component npc = npcs[i];
                    if (npc == null) continue;
                    CombatTarget c = new CombatTarget();
                    c.Tr = npc.transform; c.Npc = npc;
                    _targets.Add(c);
                }
                Component[] vehicles = VehicleScan.All();
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Component v = vehicles[i];
                    if (v == null) continue;
                    CombatTarget c = new CombatTarget();
                    c.Tr = v.transform; c.Vehicle = v;
                    _targets.Add(c);
                }
            }

            static bool Lebt(CombatTarget c)
            {
                if (c == null || c.Tr == null || !c.Tr.gameObject.activeInHierarchy) return false;
                if (c.Npc != null) return NpcWar.PatrolTarget(c.Npc);
                if (c.Vehicle != null) return NpcWar.PatrolVehicleAlive(c.Vehicle);
                return c.States == null || _stateField == null || _death == null
                    || !_death.Equals(_stateField.GetValue(c.States));
            }

            static bool Feind(Unit u, CombatTarget c)
            {
                if (!Lebt(c) || c.Tr.IsChildOf(u.Car.transform)) return false;
                if (c.Npc != null) return Fraktion.Feind(u.Seite, NpcWar.PatrolFaction(c.Npc));
                if (c.Vehicle == null)
                    return Fraktion.Feind(u.Seite, Fraktion.Spielerseite(c.Tr.gameObject));

                string side = CrewedSide(c.Vehicle);
                if (side != null) return Fraktion.Feind(u.Seite, Fraktion.Eigene(side));
                // A parked empty hull is not a combatant. A friendly occupant
                // vetoes a shot even when an enemy shares that vehicle.
                FieldInfo f = AccessTools.Field(c.Vehicle.GetType(), "Passengers");
                Array seats = f == null ? null : f.GetValue(c.Vehicle) as Array;
                bool enemy = false;
                if (seats == null) return false;
                for (int i = 0; i < seats.Length; i++)
                {
                    object seat = seats.GetValue(i);
                    GameObject go = seat as GameObject;
                    Component passenger = seat as Component;
                    if (go == null && passenger != null) go = passenger.gameObject;
                    if (go == null) continue;
                    string faction = Fraktion.Spielerseite(go);
                    if (!Fraktion.Feind(u.Seite, faction)) return false;
                    enemy = true;
                }
                return enemy;
            }

            static CombatTarget AmTreffer(GameObject go)
            {
                if (go == null) return null;
                for (int i = 0; i < _targets.Count; i++)
                {
                    CombatTarget c = _targets[i];
                    if (c.Tr != null && go.transform.IsChildOf(c.Tr)) return c;
                }
                return null;
            }

            // Rolling phase so the per-vehicle target scans (the half that can
            // cast a ray) do not all fall on the same frame. Each vehicle gets
            // its first scan at a different point inside the ScanEvery window;
            // the constant ScanEvery cadence then keeps them spread. Without
            // this a group filled in the same second raycasts in lockstep and
            // the frame overlay shows Patrol.Tick as a spike every half second.
            static float _scanPhase;

            static Type _ngs, _statesType;
            static PropertyInfo _instance;
            static FieldInfo _networkPlayers, _stateField;
            static object _death;
            static bool _lookedUp;

            /// <summary>
            /// The list of players, refreshed at most twice a second and
            /// shared by every patrol vehicle.
            ///
            /// NetworkGameServer.Instance.NetworkPlayers is the game's own
            /// list of player GameObjects - the same one
            /// NPC_Settlement::PlayersDistanceControll walks. Reading it costs
            /// two field accesses; FindObjectsOfType, which would be the
            /// obvious way, walks every object in the scene and is the kind of
            /// call that put the driver at 3 FPS once already (E-032).
            /// </summary>
            static void Refresh()
            {
                if (Time.time < _nextRefresh) return;
                _nextRefresh = Time.time + ScanEvery;
                _players.Clear();

                if (!LookUp()) return;
                object server = _instance.GetValue(null, null);
                if (server == null) return;
                IList list = _networkPlayers.GetValue(server) as IList;
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    GameObject go = list[i] as GameObject;
                    if (go == null) continue;
                    Spieler s = new Spieler();
                    s.Tr = go.transform;
                    if (_statesType != null) s.States = go.GetComponent(_statesType);
                    _players.Add(s);
                }
            }

            static bool LookUp()
            {
                if (_lookedUp) return _networkPlayers != null;
                _lookedUp = true;

                _ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                if (_ngs == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: NetworkGameServer not found - "
                        + "the gun has no way to see a player and stays quiet.");
                    return false;
                }
                _instance = _ngs.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                _networkPlayers = AccessTools.Field(_ngs, "NetworkPlayers");
                if (_instance == null || _networkPlayers == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: NetworkGameServer.Instance or "
                        + ".NetworkPlayers is missing - the gun stays quiet.");
                    _networkPlayers = null;
                    return false;
                }

                // Dead players are not targets. The state lives on
                // PlayerStatesController; a missing type is not fatal, it only
                // means a corpse is shot at once more.
                _statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                if (_statesType != null)
                {
                    _stateField = AccessTools.Field(_statesType, "_characterState");
                    if (_stateField != null && _stateField.FieldType.IsEnum)
                    {
                        try { _death = Enum.Parse(_stateField.FieldType, "Death"); }
                        catch { _death = null; }
                    }
                }
                return true;
            }

            /// <summary>
            /// Is the world up - is there a player in the game's own list?
            /// Empty in the menu, empty in the loading screen, one entry the
            /// moment the local player exists. The automatic patrol start asks
            /// this before it puts anything down; the answer is at most half a
            /// second old, because `Refresh` is what limits it.
            /// </summary>
            internal static bool WeltLaeuft()
            {
                Refresh();
                for (int i = 0; i < _players.Count; i++)
                    if (_players[i].Tr != null) return true;
                return false;
            }

            static bool Lebt(Spieler s)
            {
                if (s.States == null || _stateField == null || _death == null) return true;
                return !_death.Equals(_stateField.GetValue(s.States));
            }

            // --------------------------------------------------------- frame

            public static void Tick(List<Unit> units)
            {
                if (!RevivalPlugin.CfgPatrolGun.Value) return;
                if (units.Count == 0) return;
                Refresh();
                RefreshTargets();

                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (!u.Armed || u.Died > 0f || u.Car == null) continue;
                    if (u.Turrets.Length == 0) continue;
                    Einer(u);
                }
            }

            /// <summary>Turret objects of one vehicle. Four of them - the
            /// LODGroup swaps between them, and a barrel that only turns on
            /// LOD0 stands still as soon as the player steps back.</summary>
            public static void Collect(Unit u)
            {
                List<Transform> found = new List<Transform>();
                Transform[] all = u.Car.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i].name == "turret") found.Add(all[i]);
                u.Turrets = found.ToArray();
                u.TurretRend = null;
                for (int i = 0; i < u.Turrets.Length; i++)
                {
                    Renderer r = u.Turrets[i].GetComponent<Renderer>();
                    if (r != null) { u.TurretRend = r; break; }
                }

                // Spread this vehicle's first target scan across the window so
                // a group armed together does not scan in lockstep. The step is
                // an irrational fraction of the window, so successive vehicles
                // land far apart rather than folding back onto each other.
                _scanPhase += ScanEvery * 0.618034f;
                if (_scanPhase >= ScanEvery) _scanPhase -= ScanEvery;
                u.NextLook = Time.time + _scanPhase;
            }

            static void Einer(Unit u)
            {
                float dt = Time.deltaTime;

                if (Time.time >= u.NextLook)
                {
                    u.NextLook = Time.time + ScanEvery;
                    Suchen(u);
                }

                if (u.Target == null || !Feind(u, u.GunTarget))
                {
                    u.Target = null;
                    u.GunTarget = null;
                    u.Held = 0f;
                    u.GunStabilized = false;
                    Ruhen(u, dt);
                    return;
                }

                if (u.Lost <= 0f) u.Held += dt;

                Vector3 ziel = Zielpunkt(u.Target);
                if (!Winkel(u, ziel, out u.Yaw, out u.Pitch)) return;
                Drehen(u, dt);

                if (u.Held < RevivalPlugin.CfgPatrolGunNotice.Value) return;
                if (Time.time < u.NextShot) return;
                if (Vector3.Angle(Rohrrichtung(u), ziel - Muendung(u)) > FireWithin) return;
                if (Vector3.Distance(Muendung(u), ziel) > Suchweite(u)) return;
                if (!Sicht(u, u.Target)) return;

                if (Schiessen(u, ziel)) Nachladen(u);
            }

            /// <summary>
            /// When the gun may fire again. A fast gun fires a BURST and then
            /// pauses; without that a BTR at 0.12 s a shot empties a man in a
            /// tenth of a second and there is nothing to react to. A gun that
            /// reloads for a second or more - the tank - is its own pause and
            /// is left alone.
            /// </summary>
            static void Nachladen(Unit u)
            {
                float delay = Ladezeit(u);
                int burst = delay >= 1f ? 1 : Mathf.Max(1, RevivalPlugin.CfgPatrolGunBurst.Value);

                u.Burst++;
                if (u.Burst < burst)
                {
                    u.NextShot = Time.time + delay;
                    return;
                }
                u.Burst = 0;
                u.NextShot = Time.time + Mathf.Max(delay,
                    RevivalPlugin.CfgPatrolGunBurstPause.Value);
            }

            /// <summary>Barrel back to straight ahead. Costs nothing once it
            /// is there, which is the normal case.</summary>
            static void Ruhen(Unit u, float dt)
            {
                if (Mathf.Abs(u.Yaw) < 0.5f && Mathf.Abs(u.Pitch) < 0.5f) return;
                float step = Drehgeschwindigkeit(u) * dt;
                u.Yaw = Mathf.MoveTowards(u.Yaw, 0f, step);
                u.Pitch = Mathf.MoveTowards(u.Pitch, 0f, step);
                Drehen(u, dt);
            }

            // -------------------------------------------------------- target

            /// <summary>
            /// Pick the nearest visible hostile, or keep a living hostile
            /// already tracked. Distance and faction precede the sight ray.
            /// </summary>
            static void Suchen(Unit u)
            {
                float range = Suchweite(u);
                Vector3 from = Muendung(u);

                // Keep the current target while it is alive, near and visible.
                if (u.Target != null)
                {
                    bool weg = !Feind(u, u.GunTarget)
                        || Vector3.Distance(Zielpunkt(u.Target), from) > range;
                    if (!weg && Sicht(u, u.Target))
                    {
                        u.Lost = 0f;
                        return;
                    }
                    u.Lost += ScanEvery;
                    if (!weg && u.Lost < RevivalPlugin.CfgPatrolGunForget.Value) return;

                    RevivalPlugin.L.LogInfo("Patrol gun: target lost on " + u.Route.Name
                        + " after " + u.Held.ToString("0.0") + " s.");
                    u.Target = null;
                    u.GunTarget = null;
                    u.Held = 0f;
                    u.Lost = 0f;
                    u.Burst = 0;
                }

                CombatTarget best = null;
                float bestDist = 0f;
                for (int i = 0; i < _targets.Count; i++)
                {
                    CombatTarget s = _targets[i];
                    if (s.Tr == null) continue;
                    float d = Vector3.Distance(Zielpunkt(s.Tr), from);
                    if (d > range) continue;
                    if (best != null && d >= bestDist) continue;
                    if (!Feind(u, s)) continue;
                    if (!Sicht(u, s.Tr)) continue;
                    best = s;
                    bestDist = d;
                }
                if (best == null) return;

                u.GunTarget = best;
                u.Target = best.Tr;
                u.Held = 0f;
                u.Lost = 0f;
                RevivalPlugin.L.LogInfo("Patrol gun: " + (u.Tank ? "tank" : "BTR")
                    + " on " + u.Route.Name + " engages "
                    + (best.Vehicle != null ? "vehicle" : best.Npc != null ? "NPC" : "player")
                    + " at "
                    + bestDist.ToString("0") + " m.");
            }

            /// <summary>One ray from the muzzle to the target's chest. Hits on
            /// our own vehicle are stepped over - the BTR's muzzle sits inside
            /// its own bow plate (RE 18).</summary>
            static bool Sicht(Unit u, Transform ziel)
            {
                Vector3 from = Muendung(u);
                Vector3 to = Zielpunkt(ziel);
                Vector3 dir = to - from;
                float dist = dir.magnitude;
                if (dist < 0.5f) return true;
                dir /= dist;

                Vector3 point;
                GameObject hit = Strahl(u, from, dir, dist, out point);
                if (hit == null) return true;           // nothing in between
                // The target itself is allowed to be in the way of itself.
                if (hit.transform.IsChildOf(ziel)) return true;
                return false;
            }

            /// <summary>Chest height. The transform of a player sits at his
            /// feet, and a gun that aims there shoots the ground in front of
            /// him at any distance.</summary>
            static Vector3 Zielpunkt(Transform t)
            {
                return t.position + Vector3.up * 1.1f;
            }

            // --------------------------------------------------------- aiming

            /// <summary>
            /// World point to the turret's own two angles. The inverse of
            /// Turret.LocalRotationFor: in turret space -Y is the barrel and
            /// +Z is up, so a local direction d means pitch = asin(d.z) and
            /// yaw = atan2(-d.x, -d.y).
            /// </summary>
            static bool Winkel(Unit u, Vector3 world, out float yaw, out float pitch)
            {
                yaw = u.Yaw;
                pitch = u.Pitch;
                Transform turm = u.Turrets[0];
                if (turm == null) return false;
                Transform parent = turm.parent;
                if (parent == null) return false;

                Vector3 d = parent.InverseTransformPoint(world) - turm.localPosition;
                if (d.sqrMagnitude < 0.0001f) return false;
                d.Normalize();

                pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(d.z, -1f, 1f)) * Mathf.Rad2Deg,
                                    Turret.PitchMin(u.Tank),
                                    Turret.PitchMax(u.Tank));
                yaw = Mathf.Atan2(-d.x, -d.y) * Mathf.Rad2Deg;
                return true;
            }

            static void Drehen(Unit u, float dt)
            {
                Quaternion want = Turret.LocalRotationFor(u.Yaw, u.Pitch);
                float step = Drehgeschwindigkeit(u) * dt;
                // Keep the engaged convoy gun's world bearing through hull
                // turns. Traverse still has its normal speed and pitch limits.
                bool stabilize = u.ConvoyId != 0 && u.Target != null;
                Transform primary = u.Turrets[0];
                if (stabilize && u.GunStabilized && primary != null && primary.parent != null)
                {
                    Quaternion local = Quaternion.Inverse(primary.parent.rotation) * u.GunWorldRotation;
                    for (int i = 0; i < u.Turrets.Length; i++)
                        if (u.Turrets[i] != null) u.Turrets[i].localRotation = local;
                }
                for (int i = 0; i < u.Turrets.Length; i++)
                {
                    if (u.Turrets[i] == null) continue;
                    u.Turrets[i].localRotation =
                        Quaternion.RotateTowards(u.Turrets[i].localRotation, want, step);
                }
                u.GunStabilized = stabilize && primary != null;
                if (u.GunStabilized) u.GunWorldRotation = primary.rotation;

                // The barrel above turns every frame; the NETWORK readout does
                // not need to. Turret.Net.Publish already drops sends between
                // its own 0.08 s slots, but reading the angles back
                // (Turret.AnglesFor) and the view-id lookup inside Publish ran
                // every frame per engaged vehicle regardless. Gate the whole
                // readout to the same 0.08 s so a firefight with several
                // vehicles no longer does that trig and lookup 120 times a
                // second. Remote clients interpolate between sends, so the
                // picture on the other machines is unchanged.
                if (Time.time < u.NextNet) return;
                u.NextNet = Time.time + 0.08f;
                float actualYaw, actualPitch;
                if (Turret.AnglesFor(u.Turrets[0], out actualYaw, out actualPitch))
                    Turret.Net.Publish(u.Car.transform, actualYaw, actualPitch);
            }

            static Vector3 Rohrrichtung(Unit u)
            {
                return u.Turrets[0].TransformDirection(new Vector3(0f, -1f, 0f)).normalized;
            }

            /// <summary>Muzzle out of the WORLD bounds of the turret renderer,
            /// exactly as the manned gun does it (Turret.Muzzle).</summary>
            static Vector3 Muendung(Unit u)
            {
                Vector3 dir = Rohrrichtung(u);
                if (u.TurretRend == null) return u.Turrets[0].position + dir;
                Bounds b = u.TurretRend.bounds;
                float reach = Vector3.Dot(b.extents, new Vector3(
                    Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
                return b.center + dir * (reach + 0.5f);
            }

            // -------------------------------------------------------- firing

            static bool Schiessen(Unit u, Vector3 ziel)
            {
                Vector3 from = Muendung(u);
                float dist = Vector3.Distance(from, ziel);
                Vector3 aim = ziel + Streuung(u, dist);

                Vector3 dir = aim - from;
                if (dir.sqrMagnitude < 0.0001f) return false;
                dir.Normalize();

                float range = Reichweite(u);
                Vector3 impact;
                GameObject struck = Strahl(u, from, dir, range, out impact);
                Vector3 ende = struck == null ? from + dir * range : impact;

                // Check the actual dispersed round, too. A friendly crossing
                // the muzzle must not be hit by a nominally hostile shot.
                CombatTarget hit = AmTreffer(struck);
                if (hit != null && !Feind(u, hit)) return false;
                VehicleShotSound.Play(from, u.Tank);
                Turret.Net.PublishShot(from, u.Tank);

                Spur(u, from + dir * 2f, ende);
                u.Shots++;
                if (struck == null) return true;

                if (u.Tank && RevivalPlugin.CfgTankExplosion.Value)
                {
                    try
                    {
                        // NOT the player's shell. [Tank] is 1600 damage in a
                        // 16 m radius, which is artillery and is meant to be -
                        // but a blast does not care how well the gun was
                        // pointed, so an AI firing it kills with no counter
                        // and GunAccuracy cannot soften it. The AI gets its
                        // own two numbers; a player in the same tank keeps the
                        // artillery.
                        float scha = RevivalPlugin.CfgPatrolShellDamage.Value;
                        float rad = RevivalPlugin.CfgPatrolShellRadius.Value;
                        if (scha <= 0f) scha = RevivalPlugin.CfgTankExplosionDamage.Value;
                        if (rad <= 0f) rad = RevivalPlugin.CfgTankExplosionRadius.Value;
                        // Native blast damage has no faction filter. Keep its
                        // networked effect and apply each hostile victim once.
                        Vector3 blast = impact - dir * 0.15f;
                        RocketHook.Detonate(blast, 0f, rad, 3f);
                        Sprengschaden(u, hit, blast, scha, rad, from);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogError("Patrol gun: impact without explosion - "
                            + ex.Message);
                    }
                    return true;
                }

                if (Schaden(u, struck, Schadenswert(u), impact, from))
                {
                    u.Hits++;
                    RevivalPlugin.L.LogInfo("Patrol gun: hit at "
                        + dist.ToString("0") + " m (" + u.Hits + " of " + u.Shots + ").");
                }
                return true;
            }

            static void Sprengschaden(Unit u, CombatTarget direct, Vector3 point,
                                       float damage, float radius, Vector3 from)
            {
                int before = u.Hits;
                for (int i = 0; i < _targets.Count; i++)
                {
                    CombatTarget c = _targets[i];
                    if (!Feind(u, c)) continue;
                    Vector3 to = Zielpunkt(c.Tr);
                    if (c != direct && Vector3.Distance(point, to) > radius + 20f) continue;
                    Collider[] hull = c.Tr.GetComponentsInChildren<Collider>();
                    float dist = Vector3.Distance(point, to);
                    for (int k = 0; k < hull.Length; k++)
                    {
                        if (!hull[k].enabled || hull[k].isTrigger) continue;
                        Vector3 near = hull[k].ClosestPointOnBounds(point);
                        float d = Vector3.Distance(point, near);
                        if (d < dist) { dist = d; to = near; }
                    }
                    if (c == direct) dist = 0f;
                    if (dist > radius) continue;
                    if (c != direct && dist > 0.2f)
                    {
                        Vector3 ignored;
                        GameObject cover = Strahl(u, point, (to - point).normalized,
                                                   dist, out ignored);
                        if (cover != null && !cover.transform.IsChildOf(c.Tr)) continue;
                    }
                    float amount = damage * (1f - Mathf.Clamp01(dist / Mathf.Max(0.1f, radius)));
                    if (amount <= 0f) continue;
                    bool hurt;
                    if (c.Vehicle != null) hurt = FahrzeugSchaden(c.Vehicle, amount, 14);
                    else if (c.Npc != null)
                        hurt = Turret.TryDamage(c.Tr.gameObject, "NPC_AI2", "ApplyDamage", amount / 3f);
                    else hurt = SpielerSchaden(c.Tr.gameObject, amount, point, from);
                    if (hurt) u.Hits++;
                }
                RevivalPlugin.L.LogInfo("Patrol gun: " + u.Seite + " shell hit "
                    + (u.Hits - before) + " hostile actor(s) at " + point + ".");
            }

            static bool FahrzeugSchaden(Component vehicle, float damage, int part)
            {
                MethodInfo apply = AccessTools.Method(vehicle.GetType(), "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply == null) return false;
                apply.Invoke(vehicle, new object[] { damage, part });
                return true;
            }

            /// <summary>
            /// The miss. See the class comment for where the numbers come
            /// from - this is the game's own model with the direction made
            /// random, the hit case tightened, and ONE deliberate departure.
            ///
            /// THE DEPARTURE (2026-08-30). The game's model gives every shot
            /// under 30 m a free pass: offset 0.2 m, no roll, accuracy not
            /// consulted. That single line is why a patrol read as an 80
            /// percent death sentence - every fight with a road vehicle
            /// happens inside 30 m sooner or later, and inside it the gun was
            /// perfect no matter what GunAccuracy said. So the free ring is a
            /// setting now (`GunPointBlank`, 12 m) and the roll happens at
            /// EVERY distance. Point blank a rolled miss still lands close,
            /// because a man standing at arm's length from a BTR should not be
            /// safe either.
            /// </summary>
            static Vector3 Streuung(Unit u, float dist)
            {
                float weit = Mathf.Max(1f, Suchweite(u));
                float nah = Mathf.Clamp(Mathf.Max(RevivalPlugin.CfgPatrolGunEffective.Value,
                                                  weit * 0.5f), 1f, weit);

                float loss;
                if (dist <= nah) loss = 0f;
                else if (dist >= weit) loss = 1f;
                else loss = 0.5f * (1f - Mathf.Cos(Mathf.PI * (dist - nah) / (weit - nah)));

                float chance = loss > 0.95f
                    ? 0f
                    : Mathf.Clamp01((1f - loss) * RevivalPlugin.CfgPatrolGunAccuracy.Value);

                bool nahdran = dist < RevivalPlugin.CfgPatrolGunPointBlank.Value;
                float betrag;
                if (UnityEngine.Random.value <= chance) betrag = nahdran ? 0.2f : 0.25f;
                else betrag = nahdran ? 1.1f : 3f;

                // A direction perpendicular to the shot, so a miss goes past
                // the man or over him and never falls short and hits anyway.
                Vector3 achse = Rohrrichtung(u);
                Vector3 seite = Vector3.Cross(Vector3.up, achse);
                if (seite.sqrMagnitude < 0.0001f) seite = Vector3.right;
                seite.Normalize();
                Vector3 hoch = Vector3.Cross(achse, seite).normalized;
                float a = UnityEngine.Random.value * Mathf.PI * 2f;
                return (seite * Mathf.Cos(a) + hoch * Mathf.Sin(a)) * betrag;
            }

            /// <summary>Ray that steps over our own vehicle, up to four
            /// times. Same reason as Turret.RaycastPastVehicle.</summary>
            static GameObject Strahl(Unit u, Vector3 from, Vector3 dir, float range,
                                     out Vector3 point)
            {
                point = Vector3.zero;
                Vector3 start = from;
                float rest = range;
                for (int i = 0; i < 4 && rest > 0f; i++)
                {
                    Vector3 hit;
                    GameObject go = Turret.RaycastObject(start, dir, rest, out hit);
                    if (go == null) return null;
                    if (u.Car == null
                        || (!go.transform.IsChildOf(u.Car.transform)
                            && !Welded(u, go)))
                    {
                        point = hit;
                        return go;
                    }
                    rest -= Vector3.Distance(start, hit) + 0.25f;
                    start = hit + dir * 0.25f;
                }
                return null;
            }

            /// <summary>
            /// Is this hit the hull of a vehicle this one is standing INSIDE?
            ///
            /// PatrolSeparate pulls an overlapping pair apart within a second or
            /// two, but while they do overlap the other hull sits over the muzzle
            /// and every line of sight ends on it: no target is ever taken and
            /// the gun never fires. That was half of the reported bug - "they do
            /// not move and they do not shoot" - and the half that outlives the
            /// driving fix, because any warp can still put two hulls together for
            /// a moment. A hull whose centre is inside <see cref="WeldedWithin"/>
            /// metres of our own is not cover, it is the same piece of road, and
            /// it is stepped over exactly like this vehicle's own bow plate.
            /// </summary>
            static bool Welded(Unit u, GameObject go)
            {
                if (u.Car == null || go == null) return false;
                Vector3 mine = u.Car.transform.position;
                for (int i = 0; i < _units.Count; i++)
                {
                    Unit other = _units[i];
                    if (other == u || other.Car == null) continue;
                    if (FlatDistance(mine, other.Car.transform.position)
                        >= WeldedWithin) continue;
                    if (go.transform.IsChildOf(other.Car.transform)) return true;
                }
                return false;
            }

            static void Spur(Unit u, Vector3 von, Vector3 bis)
            {
                try
                {
                    List<Vector3> bahn = new List<Vector3>();
                    bahn.Add(von);
                    bahn.Add(bis);
                    if (u.Tank)
                    {
                        RocketHook.SpawnTracer(bahn, 1.20f, 0.50f, SpurHof, SpurHof, 0.30f);
                        RocketHook.SpawnTracer(bahn, 0.44f, 0.17f, SpurKern, SpurEnde, 0.55f);
                    }
                    else
                    {
                        RocketHook.SpawnTracer(bahn, 0.34f, 0.14f, SpurHof, SpurHof, 0.10f);
                        RocketHook.SpawnTracer(bahn, 0.13f, 0.05f, SpurKern, SpurEnde, 0.18f);
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Patrol gun: tracer - " + ex.Message);
                }
            }

            static readonly Color SpurKern = new Color(1.00f, 0.96f, 0.78f, 1.0f);
            static readonly Color SpurEnde = new Color(1.00f, 0.62f, 0.20f, 1.0f);
            static readonly Color SpurHof = new Color(1.00f, 0.38f, 0.10f, 1.0f);

            // -------------------------------------------------------- damage

            static Type _pncType;
            static PropertyInfo _photonPlayer;
            static MethodInfo _getView, _rpc;
            static bool _damageLookedUp;

            /// <summary>
            /// Damage on the struck object, by the game's own three roads.
            ///
            /// A PLAYER is hit over the wire, not by hand: FireOneShot does
            /// Extensions.GetPhotonView(go).RPC("PlayerApplyDamage", victim,
            /// damage, partType, 4, hitPoint, shooterPosition), and health
            /// lives on the victim's own client. Calling the method directly
            /// on our copy of a remote player would change a number nobody
            /// reads.
            /// </summary>
            static bool Schaden(Unit shooter, GameObject struck, float damage,
                                Vector3 point, Vector3 from)
            {
                CombatTarget c = AmTreffer(struck);
                if (c == null || !Feind(shooter, c)) return false;
                if (PanzerSchaden(shooter, struck)) return true;
                if (c.Vehicle != null)
                    return FahrzeugSchaden(c.Vehicle,
                        RevivalPlugin.CfgPatrolGunTankDamage.Value, 10);
                if (SpielerSchaden(struck, damage, point, from)) return true;
                if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)) return true;
                if (Turret.TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) return true;
                return false;
            }

            /// <summary>
            /// APC armour damage is separate from anti-personnel damage. The
            /// T-72 has 2000 durability and VehicleGameSystem accepts one hit
            /// every 0.3 seconds; a three-shot 0.12 second burst therefore
            /// becomes one configured armour hit. Calling the game's own
            /// ApplyDamage keeps durability, smoke, destruction and Photon
            /// synchronization on the established vehicle path.
            /// </summary>
            static bool PanzerSchaden(Unit shooter, GameObject struck)
            {
                if (shooter == null) return false;
                // Use the mounted BTR's armour profile. Tank blast damage goes
                // through Sprengschaden; non-explosive hits use the fallback.
                return VehicleArmor.GunHit(struck, shooter.Tank);
            }

            static bool SpielerSchaden(GameObject struck, float damage, Vector3 point,
                                       Vector3 from)
            {
                if (!DamageLookUp()) return false;
                Component pnc = struck.GetComponentInParent(_pncType);
                if (pnc == null) return false;

                object victim = _photonPlayer.GetValue(pnc, null);
                if (victim == null) return false;
                object view = _getView.Invoke(null, new object[] { pnc.gameObject });
                if (view == null) return false;

                // partType 0 is the body, 4 is the damage kind FireOneShot
                // passes for a bullet. Both were read out of its IL.
                _rpc.Invoke(view, new object[] {
                    "PlayerApplyDamage", victim,
                    new object[] { damage, 0, 4, point, from } });
                return true;
            }

            static bool DamageLookUp()
            {
                if (_damageLookedUp) return _rpc != null;
                _damageLookedUp = true;

                _pncType = RevivalPlugin.TypeByName("PlayerNetworkController");
                Type ext = RevivalPlugin.TypeByName("Extensions");
                Type viewType = RevivalPlugin.TypeByName("PhotonView");
                if (_pncType == null || ext == null || viewType == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: PlayerNetworkController, "
                        + "Extensions or PhotonView not found - the gun cannot hurt "
                        + "a player.");
                    return false;
                }

                _photonPlayer = _pncType.GetProperty("GetPhotonPlayer",
                    BindingFlags.Public | BindingFlags.Instance);
                _getView = AccessTools.Method(ext, "GetPhotonView",
                    new Type[] { typeof(GameObject) }, null);

                MethodInfo[] ms = viewType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name != "RPC") continue;
                    ParameterInfo[] ps = ms[i].GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    if (ps[2].ParameterType != typeof(object[])) continue;
                    if (ps[1].ParameterType.Name != "PhotonPlayer") continue;
                    _rpc = ms[i];
                    break;
                }

                if (_photonPlayer == null || _getView == null || _rpc == null)
                {
                    RevivalPlugin.L.LogWarning("Patrol gun: the player damage road is "
                        + "incomplete (GetPhotonPlayer " + (_photonPlayer != null)
                        + ", GetPhotonView " + (_getView != null)
                        + ", RPC " + (_rpc != null) + ") - the gun stays harmless "
                        + "to players.");
                    _rpc = null;
                    return false;
                }
                return true;
            }

            // ------------------------------------------------- value profile

            static float Schadenswert(Unit u)
            {
                float own = RevivalPlugin.CfgPatrolGunDamage.Value;
                if (own > 0f) return own;
                return u.Tank ? RevivalPlugin.CfgTankDamage.Value
                              : RevivalPlugin.CfgTurretDamage.Value;
            }

            static float Reichweite(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankRange.Value
                              : RevivalPlugin.CfgTurretRange.Value;
            }

            static float Suchweite(Unit u)
            {
                float combat = u.Tank ? RevivalPlugin.CfgPatrolTankCombatRange.Value
                                      : RevivalPlugin.CfgPatrolCombatRange.Value;
                return Mathf.Max(0f, Mathf.Min(Reichweite(u),
                    Mathf.Max(RevivalPlugin.CfgPatrolGunRange.Value, combat)));
            }

            static float Ladezeit(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankDelay.Value
                              : RevivalPlugin.CfgTurretDelay.Value;
            }

            static float Drehgeschwindigkeit(Unit u)
            {
                return u.Tank ? RevivalPlugin.CfgTankTurnSpeed.Value
                              : RevivalPlugin.CfgTurretTurnSpeed.Value;
            }
        }

        // =====================================================================
        //  The recorder
        // =====================================================================

        static void ToggleRecording()
        {
            _recording = !_recording;
            _haveLastRecorded = false;
            _nextRecord = 0f;
            if (_recording)
            {
                Load(false);
                RevivalPlugin.L.LogInfo("Patrol: recording route \""
                    + RevivalPlugin.CfgPatrolRoute.Value + "\" - a waypoint every "
                    + RevivalPlugin.CfgPatrolRecordSeconds.Value.ToString("0.#")
                    + " s. F5 again to stop.");
                Turret.Hinweis(Loc.T("Запись ", "Recording ") + RevivalPlugin.CfgPatrolRoute.Value, 3f);
            }
            else
            {
                Route r = Active();
                int count = r == null ? 0 : r.P.Count;
                RevivalPlugin.L.LogInfo("Patrol: recording stopped, "
                    + RevivalPlugin.CfgPatrolRoute.Value + " has " + count + " waypoints.");
                Turret.Hinweis(Loc.T("Записано точек: ", "Recorded ") + count
                               + Loc.T("", " waypoints"), 3f);
            }
        }

        /// <summary>
        /// A waypoint every `RecordSeconds`, which is the clock and not the
        /// tape measure. `RecordSeconds` now defaults to 0.2, so a driven route
        /// is captured at five waypoints a second - dense enough that the drawn
        /// line traces the road the driver actually followed instead of a
        /// smoothed guess between sparse points. Where the driver slows, in a
        /// corner, the clock naturally puts the points closer together, which is
        /// exactly where a route wants them.
        ///
        /// `MinStep` is the only distance rule left, and it is a hair (5 cm):
        /// without it a recorder left running while its owner stands still would
        /// write the same point five times a second until the file is full. Any
        /// real driving clears it on the very next frame.
        /// </summary>
        static void RecordWhileWalking()
        {
            if (Time.time < _nextRecord) return;
            Vector3 p = Where();
            if (p == Vector3.zero) return;
            if (_haveLastRecorded)
            {
                Vector3 d = p - _lastRecorded; d.y = 0f;
                if (d.sqrMagnitude < MinStep * MinStep) return;
            }
            _nextRecord = Time.time
                        + Mathf.Max(0.2f, RevivalPlugin.CfgPatrolRecordSeconds.Value);
            RecordHere(false);
        }

        static void RecordHere(bool loud)
        {
            Vector3 p = Where();
            if (p == Vector3.zero)
            {
                RevivalPlugin.L.LogWarning("Patrol: no camera - nothing to record.");
                return;
            }

            Load(false);
            string name = RevivalPlugin.CfgPatrolRoute.Value;
            Route r;
            if (!_routes.TryGetValue(name, out r))
            {
                r = new Route();
                r.Name = name;
                // A route is recorded by driving it, so the map it lies on is
                // simply the one the driver is standing in. Recording in the
                // starting region writes no flag at all (MapScene.Store).
                r.Scene = MapScene.Current;
                _routes[name] = r;
                _order.Add(name);
            }

            Point pt = new Point();
            pt.Pos = p;
            pt.Speed = 0f;
            pt.Flags = r.P.Count == 0 ? "spawn" : "";
            r.P.Add(pt);

            _lastRecorded = p;
            _haveLastRecorded = true;

            Save();
            if (loud)
            {
                RevivalPlugin.L.LogInfo("Patrol: " + name + " waypoint "
                    + (r.P.Count - 1) + " at " + p);
                Turret.Hinweis(name + " #" + (r.P.Count - 1), 2f);
            }
        }

        /// <summary>Where the player stands. The camera sits at his head, which
        /// is close enough for a road.</summary>
        static Vector3 Where()
        {
            Camera cam = Camera.main;
            if (cam == null) return Vector3.zero;
            return cam.transform.position;
        }

        // =====================================================================
        //  File
        // =====================================================================

        public static void Load(bool force)
        {
            if (_loaded && !force) return;
            if (force) RevivalComposition.Load(true);
            _loaded = true;
            _routes.Clear();
            _order.Clear();

            string path = Path.Combine(RevivalPlugin.AssetDir,
                                       RevivalPlugin.CfgPatrolFile.Value);
            if (LiveRoutes.Current == null && !File.Exists(path))
            {
                RevivalPlugin.L.LogWarning("Patrol: " + path + " does not exist. "
                    + "No route until one is recorded.");
                return;
            }

            try
            {
                string[] lines = LiveRoutes.Current == null ? File.ReadAllLines(path) : LiveRoutes.Current.Routes;
                int bad = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    if (raw == null) continue;
                    string trimmed = raw.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                    string[] c = raw.Split('\t');
                    if (c.Length < 5) { bad++; continue; }

                    float x, y, z;
                    int index;
                    if (!int.TryParse(c[1].Trim(), out index)) { bad++; continue; }
                    if (!float.TryParse(c[2].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out x)
                        || !float.TryParse(c[3].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out y)
                        || !float.TryParse(c[4].Trim(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out z))
                    { bad++; continue; }

                    float speed = 0f;
                    if (c.Length > 5 && c[5].Trim().Length > 0)
                        float.TryParse(c[5].Trim(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out speed);

                    string name = c[0].Trim();
                    if (name.Length == 0) { bad++; continue; }

                    Route r;
                    if (!_routes.TryGetValue(name, out r))
                    {
                        r = new Route();
                        r.Name = name;
                        _routes[name] = r;
                        _order.Add(name);
                    }
                    Point p = new Point();
                    p.Pos = new Vector3(x, y, z);
                    p.Speed = speed;
                    p.Flags = c.Length > 6 ? c[6].Trim() : "";
                    r.P.Add(p);
                }

                for (int i = 0; i < _order.Count; i++)
                {
                    Route r = _routes[_order[i]];
                    MetaLesen(r);
                    RevivalPlugin.L.LogInfo("Patrol: route " + r.Name + ", "
                        + r.P.Count + " waypoints, " + r.Seite + " ("
                        + Fraktion.Erklaerung(r.Seite) + "), " + r.Wagen + ", "
                        + r.Count + " patrol(s), map " + MapScene.Label(r.Scene)
                        + (r.Enabled ? "" : ", SWITCHED OFF") + ".");
                }
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Patrol: " + bad + " line(s) in "
                        + RevivalPlugin.CfgPatrolFile.Value + " were not readable.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol: reading routes: " + ex); }
        }

        // Only replace a FUTURE suffix. The prefix, column arc and member slots
        // remain byte-for-byte geometrically identical, so no car teleports.
        // Removed routes and routes without a shared forward junction finish
        // on their old snapshot. Composition is always captured at spawn.
        internal static void ApplyLiveRoutes()
        {
            HashSet<int> columns = new HashSet<int>();
            foreach (Unit u in _units)
            {
                if (u.Car == null || u.Route == null || u.Arrived) continue;
                Route source;
                if (!_routes.TryGetValue(u.Route.Name, out source) || !source.Enabled) continue;
                int first = Math.Max(1, u.Next + 1);
                Column column;
                bool grouped = u.Column && _columns.TryGetValue(u.ConvoyId, out column);
                if (grouped)
                {
                    if (!columns.Add(u.ConvoyId)) continue;
                    int segment;
                    PointOnRoute(u.Route, _columns[u.ConvoyId].Arc, out segment);
                    first = segment + 2;
                }
                Route target = u.OneWay ? CopyRoute(source) : OutAndBack(source);
                Dictionary<Vector3, List<int>> junctions = new Dictionary<Vector3, List<int>>();
                for (int i = 1; i < target.P.Count - 1; i++)
                {
                    List<int> indices;
                    if (!junctions.TryGetValue(target.P[i].Pos, out indices))
                    {
                        indices = new List<int>();
                        junctions[target.P[i].Pos] = indices;
                    }
                    indices.Add(i);
                }
                Route replacement = null;
                for (int oldIndex = first; oldIndex < u.Route.P.Count && replacement == null; oldIndex++)
                {
                    List<int> matches;
                    if (!junctions.TryGetValue(u.Route.P[oldIndex].Pos, out matches)) continue;
                    foreach (int newIndex in matches)
                    {
                        Vector3 incoming = u.Route.P[oldIndex].Pos - u.Route.P[oldIndex - 1].Pos;
                        Vector3 outgoing = target.P[newIndex + 1].Pos - target.P[newIndex].Pos;
                        if (Vector3.Dot(incoming.normalized, outgoing.normalized) < 0f) continue;
                        replacement = CopyRoute(u.Route);
                        replacement.P.Clear();
                        for (int i = 0; i <= oldIndex; i++) replacement.P.Add(u.Route.P[i]);
                        for (int i = newIndex + 1; i < target.P.Count; i++) replacement.P.Add(target.P[i]);
                        break;
                    }
                }
                if (replacement == null) continue;
                if (grouped)
                {
                    foreach (Unit member in _units)
                        if (member.Column && member.ConvoyId == u.ConvoyId) member.Route = replacement;
                }
                else u.Route = replacement;
                RevivalPlugin.L.LogInfo("LiveRoutes: future junction updated on " + source.Name);
            }
        }

        static void Save()
        {
            string path = Path.Combine(RevivalPlugin.AssetDir,
                                       RevivalPlugin.CfgPatrolFile.Value);
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# ndr_routes.tsv - written by the in-game recorder.");
                lines.Add("# route\tindex\tx\ty\tz\tspeed\tflags");
                lines.Add("# Pull it into the repository with: python routecheck.py --pull");
                for (int i = 0; i < _order.Count; i++)
                {
                    Route r = _routes[_order[i]];
                    MetaSchreiben(r);
                    for (int k = 0; k < r.P.Count; k++)
                    {
                        Point p = r.P[k];
                        lines.Add(r.Name + "\t" + k.ToString(CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.x.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.y.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Pos.z.ToString("0.00", CultureInfo.InvariantCulture)
                            + "\t" + p.Speed.ToString("0.#", CultureInfo.InvariantCulture)
                            + "\t" + (p.Flags == null ? "" : p.Flags));
                    }
                }
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol: writing routes: " + ex); }
        }

        static Route Active()
        {
            Route r;
            return _routes.TryGetValue(RevivalPlugin.CfgPatrolRoute.Value, out r) ? r : null;
        }

        static bool HasFlag(Point p, string flag)
        {
            if (p.Flags == null || p.Flags.Length == 0) return false;
            string[] parts = p.Flags.Split(',');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Trim() == flag) return true;
            return false;
        }

        /// <summary>The value of a `key=value` flag, empty when it is not
        /// there.</summary>
        static string FlagValue(Point p, string key)
        {
            if (p.Flags == null || p.Flags.Length == 0) return "";
            string[] parts = p.Flags.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string one = parts[i].Trim();
                if (one.Length <= key.Length + 1) continue;
                if (!one.StartsWith(key + "=")) continue;
                return one.Substring(key.Length + 1).Trim();
            }
            return "";
        }

        /// <summary>
        /// The route's own settings, out of the flags of its first waypoint.
        /// A route recorded before 2026-08-30 has none of them and gets the
        /// values from the config - which is what it did all along.
        /// </summary>
        static void MetaLesen(Route r)
        {
            if (r.P.Count == 0) return;
            Point p = r.P[0];
            r.Fraction = Fraktion.Sauber(FlagValue(p, "fraction"));
            r.Vehicle = FlagValue(p, "vehicle");
            r.Enabled = !HasFlag(p, "off");
            int n;
            if (int.TryParse(FlagValue(p, "count"), out n)) r.Count = Mathf.Clamp(n, 0, 16);
            else r.Count = 1;
            r.Kind = FlagValue(p, "kind").Trim().ToLowerInvariant();   // NDR convoy
            // Which map the route lies on. Absent = the home map, which is what
            // every route written before this flag existed means.
            r.Scene = MapScene.Clean(FlagValue(p, "scene"));
        }

        /// <summary>
        /// The other direction: the settings back into the flags, replacing
        /// whatever stood there. `spawn` is kept because the driver reads it,
        /// and any flag this class does not know is kept too - somebody may
        /// have written it by hand.
        /// </summary>
        static void MetaSchreiben(Route r)
        {
            if (r.P.Count == 0) return;
            Point p = r.P[0];
            List<string> keep = new List<string>();
            keep.Add("spawn");
            if (p.Flags != null)
            {
                string[] parts = p.Flags.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string one = parts[i].Trim();
                    if (one.Length == 0 || one == "spawn" || one == "off") continue;
                    if (one.StartsWith("fraction=") || one.StartsWith("vehicle=")
                        || one.StartsWith("count=") || one.StartsWith("kind=")
                        || one.StartsWith("scene=")) continue;
                    keep.Add(one);
                }
            }
            string f = Fraktion.Sauber(r.Fraction);
            if (f.Length > 0) keep.Add("fraction=" + f);
            string v = r.Vehicle == null ? "" : r.Vehicle.Trim().ToLowerInvariant();
            if (v == "btr" || v == "tank" || v == "mixed") keep.Add("vehicle=" + v);
            keep.Add("count=" + r.Count.ToString(CultureInfo.InvariantCulture));
            if (r.IsConvoy) keep.Add("kind=convoy");   // NDR convoy
            // Only a route that is NOT on the home map names its map, so a file
            // of routes from the starting region keeps exactly the shape it has
            // today and no older reader sees a flag it has to skip.
            string scene = MapScene.Store(r.Scene);
            if (scene.Length > 0) keep.Add("scene=" + scene);
            if (!r.Enabled) keep.Add("off");
            p.Flags = string.Join(",", keep.ToArray());
        }


        // =====================================================================
        //  Map overlay
        // =====================================================================

        // ------------------------------------------------------ layout signature
        //
        // WHY THIS EXISTS. The overlay used to work the whole map out again on
        // every single repaint: each route's thousand-point line re-projected
        // into a fresh list, a clearance grid rebuilt dash by dash, every dash
        // re-submitted through four reflected property writes, one reserved
        // rectangle per line point, and then a few hundred rectangle tests for
        // each name against that grid. With the map open that measured about
        // seven milliseconds a frame in the F6 overlay - by a wide margin the
        // most expensive thing the toolkit did.
        //
        // None of it depends on where the map is SCROLLED. Ink and names are
        // native NGUI widgets placed in the map picture's own coordinates and
        // parented beside the map texture, so panning and zooming carry them
        // along by themselves. What the result does depend on is the map's
        // scale, the scene, the panel's alpha, and which routes are drawn in
        // what state. That is the signature below; while it holds, the widgets
        // from the last build simply stay where they are.
        static float _layW, _layH, _layWorldX, _layWorldY, _layAlpha;
        static int _layHash;
        static float _layAt;
        static bool _layValid;

        // A slow heartbeat on top of the signature, and the reason the signature
        // is allowed to be a short list rather than an exhaustive one. Native
        // markers - the player above all - move without any of it changing, and
        // a name has to give way to one that has moved under it; the same goes
        // for anything the signature does not name, such as a route's faction
        // colour or an edited Places line. One rebuild a second is invisible to
        // the eye and costs a sixtieth of what doing it every frame did.
        const float MapLayoutHeartbeat = 1f;

        /// <summary>True when the overlay has to be worked out again, which
        /// also records the new signature. False means every widget on the map
        /// is still correct and this frame only has to re-test the cursor.
        /// </summary>
        static bool MapLayoutChanged(Rect full, Vector2 world, float alpha)
        {
            int hash = 17;
            for (int i = 0; i < _order.Count; i++)
            {
                Route r;
                if (!_routes.TryGetValue(_order[i], out r) || r == null) continue;
                // The route OBJECT, so a reload that keeps the names still
                // rebuilds; the waypoint count, because that is what rebuilds
                // the cached world line; and every flag the drawing reads.
                hash = hash * 31 + r.GetHashCode();
                hash = hash * 31 + r.P.Count;
                if (r.Enabled) hash += 7;
                if (r.Here) hash += 13;
                if (r.IsConvoy) hash += ConvoyRouteActive(r.Name) ? 29 : 43;
            }

            if (_layValid && hash == _layHash
                && Mathf.Abs(full.width - _layW) < 0.5f
                && Mathf.Abs(full.height - _layH) < 0.5f
                && world.x == _layWorldX && world.y == _layWorldY
                && Mathf.Abs(alpha - _layAlpha) < 0.01f
                && Time.unscaledTime - _layAt < MapLayoutHeartbeat)
                return false;

            _layHash = hash;
            _layW = full.width; _layH = full.height;
            _layWorldX = world.x; _layWorldY = world.y;
            _layAlpha = alpha;
            _layAt = Time.unscaledTime;
            _layValid = true;
            return true;
        }

        /// <summary>Drops the cached layout, so the next repaint builds the
        /// overlay from scratch. Every path that takes the overlay off the map
        /// goes through this - what is cached describes widgets that are no
        /// longer there.</summary>
        static void MapLayoutDrop()
        {
            _layValid = false;
            MapInkLayer.Hide("patrol");
            MapLabels.Hide();
        }

        /// <summary>
        /// Draws every recorded route as one faction-coloured dashed line ALONG
        /// the road it drives, running down the middle of that road. A convoy
        /// route is drawn only while a convoy is actually out on it. Editing and
        /// deletion stay in the existing F4 route editor, whose confirmation
        /// protects the file.
        /// </summary>
        public static void DrawMap()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (RevivalPlugin.CfgPatrol == null || !RevivalPlugin.CfgPatrol.Value)
            { MapLayoutDrop(); return; }

            Component manager, texture;
            Camera camera;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out camera,
                                  out world, out map))
            { MapLayoutDrop(); return; }
            Load(false);

            // The texture bounds register artwork and hover coordinates. Ink
            // now lives in native NGUI widgets; the map panel supplies its soft
            // clipping, while MapInkLayer crops at the artwork's own boundary.
            Rect clip;
            bool mapRect = MapTools.MapScreenRect(texture, camera, out clip);
            // Cached masks need the real texture bounds. Never substitute the
            // entire screen and stretch road ink across an unrelated UI region.
            if (!mapRect) { MapLayoutDrop(); return; }

            // The WHOLE map texture, before the visible window trims it below.
            // The picture covers the whole world, so the registration
            // correction in MapArt measures against this rectangle and not
            // against the scrolled window.
            Rect full = clip;

            // The map texture SCROLLS inside a clipping NGUI UIPanel (a
            // UIScrollView). MapScreenRect is the WHOLE texture, which when the
            // map is panned reaches far beyond the visible window - so dashes on
            // the off-window part used to paint over the surrounding UI. Keep
            // the viewport for hover/label bounds; native ink uses NGUI clipping.
            Rect view;
            if (MapTools.MapViewportRect(texture, camera, out view))
                clip = Intersect(clip, view);

            Color old = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            MapLabels labels = null;
            try
            {
                MapInkLayer inkLayer = MapInkLayer.Begin("patrol", texture);
                if (inkLayer == null) { MapLayoutDrop(); return; }

                // Everything below is either a REBUILD - the full projection,
                // clearance and placement work - or a re-arm, which keeps the
                // widgets the last build left on the map and only re-tests the
                // cursor against them. See MapLayoutChanged, which is asked
                // FIRST and unconditionally, because it also records the
                // signature the next frame compares against.
                bool changed = MapLayoutChanged(full, world, inkLayer.Alpha);
                bool rebuild = changed || !MapLabels.HasLayer || !inkLayer.CanKeep;

                labels = MapLabels.Begin(texture, camera, full,
                    world.x >= 4900f && world.y >= 4900f, !rebuild);
                if (rebuild && labels != null)
                {
                    NewSettlement.ReserveMapLabels(labels, texture, camera, world, map, full);
                    ArtyBattery.ReserveMapLabels(labels, texture, camera, world, map);
                }

                // Hovering a route pops a note about the patrols it carries.
                // The note itself is drawn in absolute coordinates so it may sit
                // over the map edge and is never scissored.
                Vector2 mouseAbs = Event.current.mousePosition;
                string hoverText = null;
                Vector2 hoverAt = Vector2.zero;
                Color hoverColor = Color.white;

                if (rebuild) BuildRouteInk(inkLayer, labels, texture, camera,
                                           world, map, full, clip, mapRect);
                else inkLayer.Keep();
                inkLayer.End();

                // The cursor is the one thing that does change every frame. It
                // is tested against the projected lines the build left behind,
                // and a route whose extent does not reach the cursor at all is
                // dismissed on its bounding box.
                RouteHover(full, clip, mouseAbs,
                           ref hoverText, ref hoverAt, ref hoverColor);

                if (rebuild)
                    DrawRouteNames(labels, texture, camera, world, map, full, clip,
                                   mapRect, mouseAbs,
                                   ref hoverText, ref hoverAt, ref hoverColor);
                else
                    KeepRouteNames(labels, full, clip, mouseAbs,
                                   ref hoverText, ref hoverAt, ref hoverColor);

                GUI.color = new Color(1f, 0.65f, 0.22f, 0.95f);
                GUI.Label(new Rect(18f, Screen.height - 48f, 310f, 25f),
                          Loc.T("F4: изменить или удалить маршруты патрулей",
                                "F4: edit or delete patrol routes"));

                // The hover note, on top of everything, beside the cursor and
                // clamped onto the screen.
                if (hoverText != null)
                    DrawHoverNote(hoverText, hoverAt, hoverColor);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Patrol map overlay: " + ex.Message);
                // A half-finished build must not be re-armed next frame.
                _layValid = false;
            }
            finally
            {
                if (labels != null) labels.End();
                GUI.matrix = oldMatrix;
                GUI.color = old;
            }
        }

        /// <summary>
        /// One full build of the route ink: project every drawn route's cached
        /// world line, reserve it for the name placement, run the clearance
        /// pass and submit the surviving dashes. Called only when the overlay's
        /// layout signature has actually moved.
        /// </summary>
        static void BuildRouteInk(MapInkLayer inkLayer, MapLabels labels,
                                  Component texture, Camera camera,
                                  Vector2 world, Vector2 map, Rect full, Rect clip,
                                  bool mapRect)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                Route r;
                if (_routes.TryGetValue(_order[i], out r) && r != null)
                { r.MapInked = false; r.MapNamed = false; }
            }

            MapInk.Begin();
            GUI.BeginClip(clip);
            try
            {
                // Every dash point drawn so far, so a later route's ring is
                // trimmed where it crosses one drawn earlier. Routes are
                // drawn in file order; the earlier ring keeps its line. The
                // grid works in the SCALED PICTURE's own frame - screen pixels
                // with the map's origin taken out - so the clearance radius is
                // still the screen distance it always was, while the decisions
                // no longer depend on where the map is scrolled.
                ClearGrid grid = new ClearGrid(RouteClearance);
                Vector2 scale = new Vector2(full.width / 1024f, full.height / 1024f);

                // TWO passes: every ACTIVE convoy road first, the standing
                // patrol roads after it. A convoy often shares tarmac with a
                // patrol, and the clearance grid drops whichever line comes
                // second at a crossing - so the rare, time-limited convoy
                // gets the road and the patrol is the one that opens a gap,
                // never the other way round. NDR convoy.
                for (int pass = 0; pass < 2; pass++)
                for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
                {
                    Route route;
                    if (!_routes.TryGetValue(_order[routeIndex], out route)
                        || route == null || route.P.Count < 2) continue;

                    // REGION GATE. The map on screen belongs to ONE scene,
                    // and a route belongs to one scene too. Drawing the
                    // routes of the starting region onto the map of Primorye
                    // or the toxic swamp is what this stops - those patrols
                    // are not there, and FitsScene below cannot tell, because
                    // two surface maps are of a similar size. See MapScene.
                    if (!route.Here) continue;

                    // A convoy route is an EVENT, not a standing road on
                    // the map: it is drawn only while a convoy is actually
                    // driving it. NDR convoy.
                    bool convoy = route.IsConvoy && ConvoyRouteActive(route.Name);
                    if (route.IsConvoy && !convoy) continue;
                    if (convoy != (pass == 0)) continue;

                    // FOLLOW the driven road instead of encircling the run.
                    // The line is built ONCE in world space (WorldLine,
                    // cached on the route) and only PROJECTED here, so it
                    // does not jitter as the map/camera micro-moves.
                    // Project every point; if any falls behind the UI
                    // camera the line is skipped this frame rather than
                    // drawn broken.
                    List<Vector3> wline = WorldLine(route);
                    if (wline == null || wline.Count < 2) continue;

                    // SCENE GATE. The map shows the CURRENT scene, and its
                    // WORLD_SIZE is that scene's terrain size. A route lives
                    // on one terrain (the overworld), so on any smaller map -
                    // a bunker or other interior - its coordinates fall many
                    // terrain-widths outside and the ring smears across the
                    // wrong map. Draw the route only where it can fit.
                    if (!FitsScene(wline, world)) continue;

                    // The projected line is kept RELATIVE to the map picture's
                    // own origin, and the list is reused: that is what lets the
                    // cursor test and the reserved road survive a pan without
                    // being rebuilt, and it keeps a few hundred kilobytes a
                    // second out of the collector.
                    List<Vector2> line = route.MapProj;
                    if (line == null)
                    { line = new List<Vector2>(wline.Count); route.MapProj = line; }
                    line.Clear();
                    bool lineOk = true;
                    for (int i = 0; i < wline.Count; i++)
                    {
                        Vector2 g;
                        if (!MapTools.WorldToGui(wline[i], texture, camera,
                                                 world, map, out g))
                        { lineOk = false; break; }
                        line.Add(MapArt(g, full, mapRect) - full.position);
                    }
                    if (!lineOk || line.Count < 2) continue;
                    route.MapBounds = Extent(line, RouteHoverPx);
                    route.MapInked = true;
                    // Reserves the ink AND hands the placement pass the line
                    // this route's name belongs to.
                    if (labels != null) labels.BlockRoute(route.Name, line, full.position);

                    // Colour is the patrol's faction: looter and traitor
                    // red, civilian green, neutral white. A convoy route is
                    // amber, to read apart from the patrol areas. NDR convoy.
                    GUI.color = route.IsConvoy ? ConvoyColor(route.Enabled)
                                               : RouteColor(route.Seite, route.Enabled);

                    // This line's own dash points, added to the grid only
                    // after it is fully drawn so it never clears itself.
                    List<Vector2> ink = new List<Vector2>();
                    DrawRoadInk(route.Name, wline, route.MapLineLoop,
                                scale, grid, ink, inkLayer);
                    grid.Add(ink);
                }
            }
            finally { GUI.EndClip(); MapInk.End(); }
        }

        /// <summary>The bounding box of a projected line, grown by
        /// <paramref name="slack"/>. The cursor test uses it to dismiss a route
        /// that is nowhere near the pointer without walking its thousand
        /// segments.</summary>
        static Rect Extent(List<Vector2> line, float slack)
        {
            float x0 = line[0].x, x1 = x0, y0 = line[0].y, y1 = y0;
            for (int i = 1; i < line.Count; i++)
            {
                Vector2 p = line[i];
                if (p.x < x0) x0 = p.x; else if (p.x > x1) x1 = p.x;
                if (p.y < y0) y0 = p.y; else if (p.y > y1) y1 = p.y;
            }
            return new Rect(x0 - slack, y0 - slack,
                            x1 - x0 + slack * 2f, y1 - y0 + slack * 2f);
        }

        /// <summary>
        /// The note that pops when the cursor comes near a drawn road. The
        /// first line under the cursor wins it, in the order the ink was laid
        /// down: the active convoys first, the standing patrols after them.
        /// This runs on EVERY repaint - the cursor is the one thing that moves
        /// when nothing else does - against the lines the last build left.
        /// </summary>
        static void RouteHover(Rect full, Rect clip, Vector2 mouseAbs,
                               ref string hoverText, ref Vector2 hoverAt,
                               ref Color hoverColor)
        {
            if (!clip.Contains(mouseAbs)) return;
            Vector2 mouseRel = mouseAbs - full.position;
            for (int pass = 0; pass < 2; pass++)
            for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
            {
                Route route;
                if (!_routes.TryGetValue(_order[routeIndex], out route)
                    || route == null || !route.MapInked || route.MapProj == null) continue;
                if (route.IsConvoy != (pass == 0)) continue;
                if (!route.MapBounds.Contains(mouseRel)) continue;
                if (!NearPolyline(route.MapProj, mouseRel, RouteHoverPx)) continue;

                hoverText = route.IsConvoy
                    ? Loc.T(
                        "По этой дороге сейчас идёт военный конвой: "
                        + "2 танка и 2 БТР с ценным грузом. "
                        + "Они опасны и хорошо вооружены.",
                        "A military convoy is on this road RIGHT NOW: "
                        + "2 tanks and 2 APCs carrying valuable cargo. "
                        + "They are dangerous and heavily armed.")
                    : Loc.T(
                        "Здесь регулярно проходят патрули. Возможно, "
                        + "они везут ценный груз, но они опасны, хорошо "
                        + "вооружены и имеют FPV-дрон.",
                        "Regular patrols pass through here. They may be "
                        + "carrying valuable cargo, but they are dangerous, "
                        + "heavily armed, and have an FPV drone.");
                hoverText = route.Name + "\n" + hoverText;
                hoverAt = mouseAbs;
                hoverColor = route.IsConvoy ? ConvoyColor(route.Enabled)
                                            : RouteColor(route.Seite, route.Enabled);
                return;
            }
        }

        /// <summary>
        /// THREE passes. Names the player placed by hand go down first, so
        /// every automatic name gives way to them; then the rare, time-limited
        /// convoys; then the standing patrols. All lines and native markers are
        /// already reserved; every accepted name reserves its full bounds.
        /// </summary>
        static void DrawRouteNames(MapLabels labels, Component texture, Camera camera,
                                   Vector2 world, Vector2 map, Rect full, Rect clip,
                                   bool mapRect, Vector2 mouseAbs,
                                   ref string hoverText, ref Vector2 hoverAt,
                                   ref Color hoverColor)
        {
            for (int labelPass = 0; labelPass < 3; labelPass++)
            for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
            {
                Route route;
                if (!_routes.TryGetValue(_order[routeIndex], out route)
                    || route == null || route.P.Count < 1) continue;
                if (!route.Here) continue;          // same region gate as the ring loop
                if (route.IsConvoy && !ConvoyRouteActive(route.Name)) continue;
                int wantedPass = labels != null && labels.Pinned(route.Name)
                    ? 0 : (route.IsConvoy ? 1 : 2);
                if (wantedPass != labelPass) continue;
                if (!FitsScene(WorldLine(route), world)) continue;
                Vector2 label;
                if (!MapTools.WorldToGui(PatrolMapRoads.Correct(route.P[0].Pos), texture, camera,
                                         world, map, out label)) continue;
                label = MapArt(label, full, mapRect);
                route.MapAnchor = label - full.position;
                route.MapNamed = true;
                // The start point is the FALLBACK anchor and the hover spot
                // only. A route whose road was drawn above gets its name
                // beside that road, wherever the road is open and straight,
                // and a route named in MapLabels/Places gets the place the
                // player picked for it instead.
                string title = MapLabels.DisplayName(route.Name);
                if (!route.Enabled) title += Loc.T(" (\u0412\u042b\u041a\u041b)", " (DISABLED)");
                bool overName = labels != null && labels.Draw(route.Name, title, label,
                    route.Enabled, mouseAbs);
                // Hidden names remain discoverable at their route start as
                // well as anywhere along the road. Keep original names here.
                if (clip.Contains(mouseAbs) && (overName || (mouseAbs - label).sqrMagnitude < 196f))
                    NameHover(route, mouseAbs, ref hoverText, ref hoverAt, ref hoverColor);
            }
        }

        /// <summary>
        /// The same pass on a frame that changed nothing: every name the last
        /// build placed keeps its widget and the spot the placement search gave
        /// it, and only the cursor is tested against it. The names are walked in
        /// file order, which within a pass is the order the build used, so the
        /// same name wins the note as before.
        /// </summary>
        static void KeepRouteNames(MapLabels labels, Rect full, Rect clip, Vector2 mouseAbs,
                                   ref string hoverText, ref Vector2 hoverAt,
                                   ref Color hoverColor)
        {
            bool overMap = clip.Contains(mouseAbs);
            Vector2 mouseRel = mouseAbs - full.position;
            for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
            {
                Route route;
                if (!_routes.TryGetValue(_order[routeIndex], out route)
                    || route == null || !route.MapNamed) continue;
                bool overName = labels != null && labels.Keep(route.Name, mouseAbs);
                if (overMap && (overName
                        || (mouseRel - route.MapAnchor).sqrMagnitude < 196f))
                    NameHover(route, mouseAbs, ref hoverText, ref hoverAt, ref hoverColor);
            }
        }

        /// <summary>The note a route's NAME pops: the route, and whether it is
        /// switched off.</summary>
        static void NameHover(Route route, Vector2 mouseAbs, ref string hoverText,
                              ref Vector2 hoverAt, ref Color hoverColor)
        {
            hoverText = route.Name + (route.Enabled ? "" : Loc.T(" (\u0432\u044b\u043a\u043b)", " (disabled)"));
            hoverAt = mouseAbs;
            hoverColor = route.IsConvoy ? ConvoyColor(route.Enabled)
                : RouteColor(route.Seite, route.Enabled);
        }

        /// <summary>
        /// The dash colour for a patrol route, by its faction: looter and
        /// traitor draw the game's own target-ring red (measured RGB
        /// 183,33,32), civilian green, neutral white. A disabled route keeps
        /// its hue but drops to a faint alpha.
        /// </summary>
        static Color RouteColor(string faction, bool enabled)
        {
            Color c;
            switch (faction)
            {
                case "civilian":
                    c = new Color(0.22f, 0.78f, 0.30f); break;   // green
                case "neutral":
                    c = new Color(0.95f, 0.95f, 0.95f); break;   // white
                case "looter":
                case "traitor":
                default:
                    c = new Color(0.72f, 0.13f, 0.125f); break;  // Locator red
            }
            c.a = enabled ? 0.95f : 0.42f;
            return c;
        }

        /// <summary>The dash colour for a convoy route: amber, so it reads apart
        /// from the faction-coloured patrol areas. NDR convoy.</summary>
        static Color ConvoyColor(bool enabled)
        {
            Color c = new Color(1f, 0.62f, 0.12f);   // amber
            c.a = enabled ? 0.95f : 0.42f;
            return c;
        }

        // Dash cadence in SCREEN pixels. Dash and gap are the on/off run
        // lengths walked along the LINE's arc length; stroke is the line
        // thickness. Each dash is a short chain of ANTIALIASED feathered bars
        // (see <see cref="Bar"/>) that FOLLOWS the line's arc, so the dash
        // itself curves smoothly with the road; the long SIDES are feathered
        // (smooth, not pixelated) and constant in thickness, while the two ENDS
        // stay hard and FLAT (kantig) - no round caps. RouteDash/RouteGap are
        // fixed on every route. Leftover length stays at the route ends (or
        // the loop seam); it never stretches the dashes or their spacing.
        const float RouteDash = 22f;
        const float RouteGap = 12f;
        const float RouteStroke = 3f;

        // The length of each straight bar inside a curved dash, and the small
        // overlap that keeps consecutive bars meeting without a notch on the
        // outside of a bend. Shorter step -> smoother curve. A dash FOLLOWS the
        // line's arc, so its ends are tangent to the road and each dash points
        // at the next - the eye draws one continuous line through them - and a
        // dash only curves where the road actually bends; on a straight run it
        // stays straight.
        const float RouteCurveStep = 1.5f;
        const float RouteSegOverlap = 1.2f;

        // World-space spacing (metres) the cached line is resampled to before it
        // is projected each frame. Dense enough that the projected polyline
        // reads as a smooth road at the fixed map scale; built once, so the
        // count is cheap.
        const float RouteResampleWorld = 4f;

        // The line runs through the waypoints as a centripetal Catmull-Rom
        // sampled this many times per leg. Eight is enough for the ~30 m legs
        // the route editor records: the curve reads as a road rather than as a
        // chain of straight hops, and the spline INTERPOLATES, so it never
        // leaves the road the way corner-cutting would.
        const int RouteSplineSteps = 8;

        // A route whose last waypoint comes back within this many metres of its
        // first is a loop, and is dashed as a closed ring so the seam where it
        // closes carries a proper gap.
        const float RouteLoopClose = 45f;

        // How near the cursor has to come to the line, in screen pixels, before
        // the route's note pops.
        const float RouteHoverPx = 12f;

        // A route line that crosses one drawn earlier is trimmed back within
        // this radius of the earlier line, reopening a clean gap instead of
        // letting the two sets of dashes pile into a blob at the crossing.
        const float RouteClearance = 14f;

        // How far a route's world extent may exceed the shown scene's terrain
        // before the route is treated as belonging to a DIFFERENT scene and
        // dropped. WORLD_SIZE is the current scene's terrain size (see
        // MapUIManager.InitWorldSize, which reads MainTerrain) and the map is a
        // pure scale of it, so a route recorded on one terrain never exceeds
        // that terrain: on the overworld the ring already sits inside the map,
        // ratio well under 1. An interior map (a bunker terrain is tens of
        // metres) is many times smaller, so the overworld coordinates land many
        // terrain-widths off and smear across the window - the reported "patrol
        // area covers half the map and shows in other regions" bug. The slack
        // above 1 also absorbs a tiled overworld whose MainTerrain is one tile
        // smaller than the driven area; interiors are culled with wide margin.
        const float RouteSceneFit = 2.5f;

        /// <summary>
        /// True when the route's world XZ extent can plausibly sit on the
        /// currently shown scene's terrain. The map is a pure scale of
        /// WORLD_SIZE (that scene's terrain size), so a route far wider than the
        /// terrain belongs to another scene and must not be projected onto this
        /// one - otherwise it stretches across the wrong map. Fails OPEN (true)
        /// when the size is unknown, so a working overlay is never blanked by a
        /// missing value.
        /// </summary>
        static bool FitsScene(List<Vector3> ring, Vector2 world)
        {
            if (world.x <= 0f || world.y <= 0f || ring == null || ring.Count == 0)
                return true;
            float minX = ring[0].x, maxX = ring[0].x;
            float minZ = ring[0].z, maxZ = ring[0].z;
            for (int i = 1; i < ring.Count; i++)
            {
                float x = ring[i].x, z = ring[i].z;
                if (x < minX) minX = x; else if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; else if (z > maxZ) maxZ = z;
            }
            return (maxX - minX) <= world.x * RouteSceneFit
                && (maxZ - minZ) <= world.y * RouteSceneFit;
        }

        /// <summary>
        /// The route's cached DISPLAY line. XZ encodes the measured artwork
        /// centre before MapArt registration; Y carries width in artwork pixels
        /// and WorldToGui ignores it. Only explicitly tagged road routes snap
        /// to the display guide. Driving vertices and steering remain unchanged.
        /// The cache is rebuilt with the route object or waypoint count.
        /// </summary>
        static List<Vector3> WorldLine(Route r)
        {
            if (r.MapLine != null && r.MapLineN == r.P.Count) return r.MapLine;

            r.MapLineN = r.P.Count;
            r.MapLine = null;
            r.MapLineLoop = false;
            if (r.P.Count < 2) return null;

            List<Vector2> xz = new List<Vector2>(r.P.Count);
            for (int i = 0; i < r.P.Count; i++)
                xz.Add(new Vector2(r.P[i].Pos.x, r.P[i].Pos.z));

            bool loop = xz.Count > 2
                && (xz[xz.Count - 1] - xz[0]).magnitude < RouteLoopClose;

            // Display follows the measured artwork centre with one uniform ink width.
            // Explicit road routes snap for display only; driving points stay intact.
            List<Vector3> worldLine = MapInk.BuildWorld(xz, loop,
                FlagValue(r.P[0], "road").Length > 0);
            r.MapLine = worldLine;
            r.MapLineLoop = loop;
            return worldLine;
        }

        /// <summary>A centripetal Catmull-Rom spline through EVERY point. It
        /// INTERPOLATES, so the curve passes exactly through each waypoint and
        /// cannot drift off the road the way corner-cutting (the ring's old
        /// Chaikin pass) does; the centripetal knot spacing is what keeps it
        /// from overshooting into a loop at a sharp turn. A closed input wraps
        /// its end tangents so the seam bends like every other corner.
        /// </summary>
        static List<Vector2> CatmullRom(List<Vector2> pts, bool closed, int steps)
        {
            int n = pts.Count;
            if (n < 3 || steps < 1) return new List<Vector2>(pts);
            List<Vector2> outp = new List<Vector2>(n * steps + 1);
            int last = closed ? n : n - 1;
            for (int i = 0; i < last; i++)
            {
                Vector2 p0 = closed ? pts[(i - 1 + n) % n] : pts[Mathf.Max(i - 1, 0)];
                Vector2 p1 = pts[i % n];
                Vector2 p2 = pts[(i + 1) % n];
                Vector2 p3 = closed ? pts[(i + 2) % n] : pts[Mathf.Min(i + 2, n - 1)];
                for (int st = 0; st < steps; st++)
                    outp.Add(Spline(p0, p1, p2, p3, st / (float)steps));
            }
            outp.Add(closed ? pts[0] : pts[n - 1]);
            return outp;
        }

        /// <summary>One point of the centripetal Catmull-Rom segment p1..p2, at
        /// <paramref name="t"/> in 0..1. Barry-Goldman pyramid form, so the
        /// knot spacing is honoured without solving for coefficients.</summary>
        static Vector2 Spline(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
                              float t)
        {
            float t0 = 0f;
            float t1 = Knot(t0, p0, p1);
            float t2 = Knot(t1, p1, p2);
            float t3 = Knot(t2, p2, p3);
            float tt = t1 + (t2 - t1) * t;
            Vector2 a1 = Mix(p0, p1, t0, t1, tt);
            Vector2 a2 = Mix(p1, p2, t1, t2, tt);
            Vector2 a3 = Mix(p2, p3, t2, t3, tt);
            Vector2 b1 = Mix(a1, a2, t0, t2, tt);
            Vector2 b2 = Mix(a2, a3, t1, t3, tt);
            return Mix(b1, b2, t1, t2, tt);
        }

        /// <summary>The next centripetal knot: the previous one plus the square
        /// root of the chord length. Coincident points get a tiny step instead
        /// of a zero one, so the pyramid never divides by zero.</summary>
        static float Knot(float t, Vector2 a, Vector2 b)
        {
            float d = (b - a).magnitude;
            return d > 1e-6f ? t + Mathf.Sqrt(d) : t + 1e-4f;
        }

        /// <summary>Linear blend of two points over a knot interval.</summary>
        static Vector2 Mix(Vector2 a, Vector2 b, float ta, float tb, float t)
        {
            if (Mathf.Abs(tb - ta) < 1e-9f) return a;
            float w = (tb - t) / (tb - ta);
            return a * w + b * (1f - w);
        }

        // The hand-painted map PICTURE is not drawn at the terrain's scale: it
        // is about 0.5 percent larger than the terrain and sits roughly 10 m
        // east and 20 m north of it. That is measured, not guessed - fitting the
        // terrain's splat road centrelines onto the picture's own road ridges at
        // 1024 px locks in at this scale and shift (docs/ai/
        // REVERSE_ENGINEERING.md 33.1). MapTools.WorldToGui projects as if
        // picture and terrain were the same thing, so a world-true line lands
        // BESIDE the road the picture draws - which is exactly the "the marker
        // has to sit in the middle of the road" complaint. The correction below
        // is DISPLAY ONLY; no route waypoint is touched. Set RouteMapRegister to
        // false to draw world-true again.
        const bool RouteMapRegister = true;
        const float MapArtFit = 1024f;      // the fit was measured at this size
        const float MapArtScale = 1.005f;
        const float MapArtShiftX = 2f;      // picture roads sit +2 px east ...
        const float MapArtShiftY = -4f;     // ... and 4 px north (GUI y is down)

        /// <summary>Moves a world-true projected point onto the map picture's
        /// own road, undoing the picture's registration error (see the MapArt*
        /// constants). <paramref name="full"/> is the whole map texture's screen
        /// rectangle - the picture covers the whole world, so the shift measured
        /// at 1024 px scales with it. Returns the point unchanged when the
        /// texture rectangle is unknown, so a missing bound never moves the
        /// overlay somewhere arbitrary.</summary>
        static Vector2 MapArt(Vector2 g, Rect full, bool known)
        {
            if (!known || !RouteMapRegister || full.width < 1f) return g;
            float factor = full.width / MapArtFit;
            float cx = full.x + full.width * 0.5f;
            float cy = full.y + full.height * 0.5f;
            return new Vector2(
                (g.x - cx) * MapArtScale + cx + MapArtShiftX * factor,
                (g.y - cy) * MapArtScale + cy + MapArtShiftY * factor);
        }

        /// <summary>Is a convoy actually driving this route right now? A convoy
        /// route is an event, not a standing road on the map, so it is only
        /// drawn while one is out. The convoy list is kept by the MASTER CLIENT
        /// that spawns and drives them (RevivalConvoy.DoSpawn returns early on
        /// anything else), so on a joined client this is always false and the
        /// route stays hidden - the same scope the rest of the convoy feature
        /// already has. NDR convoy.</summary>
        static bool ConvoyRouteActive(string name)
        {
            if (name == null) return false;
            try
            {
                List<RevivalConvoy.Convoy> live = RevivalConvoy.ActiveConvoys();
                if (live == null) return false;
                for (int i = 0; i < live.Count; i++)
                    if (live[i] != null && live[i].Route == name) return true;
            }
            catch { }
            return false;
        }

        /// <summary>The overlap of two GUI rectangles. An empty overlap returns a
        /// zero-size rect - the overlay then simply draws nothing - rather than
        /// either input, so a stray panel state can never leak dashes over the
        /// UI.</summary>
        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin);
            float y0 = Mathf.Max(a.yMin, b.yMin);
            float x1 = Mathf.Min(a.xMax, b.xMax);
            float y1 = Mathf.Min(a.yMax, b.yMax);
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        // Each curved dash is one cached coverage mask, with map-relative
        // dimensions. Zoom scales the road and its ink together; pan only moves
        // it. The clearance points are therefore measured in the SCALED
        // PICTURE's own frame, with the map's screen origin left out: the
        // distances - and so every decision here - are the same screen
        // distances as before, but they no longer change when the map is
        // scrolled, which is what lets a panned map keep the whole pass.
        static void DrawRoadInk(string name, List<Vector3> world, bool loop,
                                Vector2 scale, ClearGrid grid, List<Vector2> ink,
                                MapInkLayer inkLayer)
        {
            MapInk.Cache cache = MapInk.Get(name, world, loop);
            for (int i = 0; i < cache.Dashes.Count; i++)
            {
                MapInk.Dash dash = cache.Dashes[i];
                // Submit the full map geometry, even outside the viewport.
                // NGUI clips per pixel and follows scroll transforms immediately;
                // screen culling here would leave missing dashes after fast pans.
                bool blocked = false;
                for (int j = 0; j < dash.Points.Count; j += 8)
                {
                    Vector2 p = Vector2.Scale(dash.Points[j], scale);
                    if (grid != null && grid.Blocked(p)) { blocked = true; break; }
                }
                if (blocked) continue;
                inkLayer.Draw(dash.Bounds, dash.Texture, GUI.color);
                for (int j = 0; j < dash.Points.Count; j += 4)
                    ink.Add(Vector2.Scale(dash.Points[j], scale));
            }
        }

        /// <summary>Fixed dash cadence on a loop. The closing gap absorbs
        /// unused length so full dashes and ordinary gaps match open routes.
        /// A loop too short for one dash and gap is left unmarked.</summary>
        static void DashClosed(List<Vector2> pts, Rect clip,
                               ClearGrid grid, List<Vector2> ink)
        {
            int n = pts.Count;
            if (n < 2) return;
            float[] cum = new float[n];
            for (int i = 1; i < n; i++)
                cum[i] = cum[i - 1] + (pts[i] - pts[i - 1]).magnitude;
            float total = cum[n - 1];
            if (total < 1f) return;

            float period = RouteDash + RouteGap;
            int count = Mathf.FloorToInt(total / period);
            float offset = (total - count * period) * 0.5f;
            for (int k = 0; k < count; k++)
            {
                float start = offset + k * period;
                DrawCurvedDash(pts, cum, start, start + RouteDash, clip, grid, ink);
            }
        }

        /// <summary>Fixed dash length and spacing, shared by every open
        /// route. Centre the full dashes on the road; unused length stays at
        /// the endpoints instead of changing the Locator cadence. A road
        /// shorter than one full dash is left unmarked.</summary>
        static void DashOpen(List<Vector2> pts, Rect clip,
                             ClearGrid grid, List<Vector2> ink)
        {
            int n = pts.Count;
            if (n < 2) return;
            float[] cum = new float[n];
            for (int i = 1; i < n; i++)
                cum[i] = cum[i - 1] + (pts[i] - pts[i - 1]).magnitude;
            float total = cum[n - 1];
            if (total < 1f) return;

            float period = RouteDash + RouteGap;
            int count = Mathf.FloorToInt((total + RouteGap) / period);
            if (count < 1) return;
            float used = count * RouteDash + (count - 1) * RouteGap;
            float offset = (total - used) * 0.5f;
            for (int k = 0; k < count; k++)
                DrawCurvedDash(pts, cum, offset + k * period,
                               offset + k * period + RouteDash, clip, grid, ink);
        }

        /// <summary>Resamples a polyline to a uniform arc-length spacing, so the
        /// number of output points depends on the line's LENGTH, never on how
        /// many points described it. Endpoints are kept.</summary>
        static List<Vector2> Resample(List<Vector2> pts, float spacing)
        {
            if (pts.Count < 2 || spacing <= 0.01f) return pts;
            List<Vector2> outp = new List<Vector2>();
            outp.Add(pts[0]);
            Vector2 cur = pts[0];
            float need = spacing;             // distance left to the next sample
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 next = pts[i];
                Vector2 seg = next - cur;
                float d = seg.magnitude;
                if (d < 1e-6f) { cur = next; continue; }
                Vector2 dir = seg / d;
                while (need <= d)
                {
                    cur = cur + dir * need;
                    outp.Add(cur);
                    d -= need;
                    need = spacing;
                }
                need -= d;
                cur = next;
            }
            Vector2 last = pts[pts.Count - 1];
            if ((outp[outp.Count - 1] - last).magnitude > spacing * 0.5f)
                outp.Add(last);
            return outp;
        }

        /// <summary>Draw one dash by FOLLOWING the ring's arc between its two
        /// arc-length points: it samples the smoothed ring itself, so the dash
        /// is straight where the ring is straight and curves only where the ring
        /// bends, and its two ends are TANGENT to the ring - each dash points at
        /// the next, so the eye draws one continuous line through the whole loop.
        /// It is tessellated into a chain of antialiased <see cref="Bar"/>
        /// segments (smooth sides, constant thickness) with the two outer ends
        /// left hard and FLAT. Dropped if its midpoint falls within an earlier
        /// route's clearance; its sample points feed <paramref name="ink"/> so
        /// later routes clear against it. The caller has set <see
        /// cref="GUI.color"/> and a hard map clip.</summary>
        static void DrawCurvedDash(List<Vector2> pts, float[] cum,
                                   float start, float end, Rect clip,
                                   ClearGrid grid, List<Vector2> ink)
        {
            Vector2 mid = PointAtArc(pts, cum, (start + end) * 0.5f);
            // Cheap cull; the surrounding GUI.BeginClip is the real boundary.
            if (!clip.Contains(mid)) return;
            if (grid != null && grid.Blocked(mid)) return;

            float len = end - start;
            if (len < 0.5f) return;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / RouteCurveStep));
            Vector2 prev = PointAtArc(pts, cum, start);
            if (ink != null) ink.Add(prev);
            for (int s = 1; s <= steps; s++)
            {
                float d = start + len * (s / (float)steps);
                Vector2 p = PointAtArc(pts, cum, d);
                DrawBar(prev, p, s > 1, s < steps);
                if (ink != null) ink.Add(p);
                prev = p;
            }
        }

        /// <summary>One antialiased bar of a curved dash, from <paramref
        /// name="a"/> to <paramref name="b"/>, rotated to the chord. It is grown
        /// by half the overlap on any INNER end so consecutive bars meet with no
        /// notch on the outside of a bend; the dash's two OUTER ends are left
        /// flush so they stay flat and crisp. The <see cref="Bar"/> texture
        /// feathers the long sides.</summary>
        static void DrawBar(Vector2 a, Vector2 b, bool growA, bool growB)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            if (len < 0.25f) return;
            Vector2 u = dir / len;
            if (growA) { a -= u * (RouteSegOverlap * 0.5f); }
            if (growB) { b += u * (RouteSegOverlap * 0.5f); }
            dir = b - a;
            len = dir.magnitude;
            Vector2 mid = (a + b) * 0.5f;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, mid);
            GUI.DrawTexture(new Rect(mid.x - len * 0.5f, mid.y - RouteStroke * 0.5f,
                                     len, RouteStroke), Bar());
            GUI.matrix = m;
        }

        /// <summary>A cached bar texture for one dash: fully opaque across its
        /// width so the dash ENDS stay hard and flat, with the top and bottom
        /// rows feathered to zero alpha so the long SIDES are antialiased when
        /// the bar is stretched to the stroke height and rotated. Bilinear
        /// filtering and clamp wrapping keep the sides smooth and the ends crisp.
        /// White RGB, so <see cref="GUI.color"/> tints it to the faction hue.
        /// </summary>
        static Texture2D _bar;
        static Texture2D Bar()
        {
            if (_bar != null) return _bar;
            const int w = 4, h = 16;
            const float feather = 3f;      // rows faded at each side
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            Color[] px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float edge = Mathf.Min(y, h - 1 - y) + 0.5f;   // dist to nearer side
                float a = Mathf.Clamp01(edge / feather);
                a = a * a * (3f - 2f * a);                      // smoothstep
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
            t.SetPixels(px);
            t.Apply();
            _bar = t;
            return t;
        }

        /// <summary>Is the cursor within <paramref name="px"/> pixels of the
        /// line? This replaces the enclosed-area test the ring needed: the
        /// marker is a road now, so the note belongs to the road under the
        /// cursor and not to a whole region of the map.</summary>
        static bool NearPolyline(List<Vector2> pts, Vector2 p, float px)
        {
            float best = px * px;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1];
                Vector2 ab = pts[i] - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 < 1e-6f ? 0f
                    : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                Vector2 d = p - (a + ab * t);
                if (d.sqrMagnitude <= best) return true;
            }
            return false;
        }

        /// <summary>The hover note beside the cursor: a dark panel with a
        /// faction-coloured hairline border and word-wrapped text, clamped so it
        /// stays on the screen. Drawn in ABSOLUTE GUI coordinates, after the map
        /// clip is closed, so it may sit over the map's edge.</summary>
        static void DrawHoverNote(string text, Vector2 at, Color accent)
        {
            const float w = 260f;
            GUIStyle body = new GUIStyle(GUI.skin.label);
            body.wordWrap = true;
            body.padding = new RectOffset(9, 9, 8, 8);
            body.normal.textColor = new Color(0.96f, 0.96f, 0.96f);
            float h = body.CalcHeight(new GUIContent(text), w);

            float x = at.x + 16f;
            float y = at.y + 16f;
            if (x + w > Screen.width) x = at.x - w - 16f;
            if (x < 2f) x = 2f;
            if (y + h > Screen.height) y = Screen.height - h - 2f;
            if (y < 2f) y = 2f;
            Rect box = new Rect(x, y, w, h);

            GUI.color = new Color(0.05f, 0.05f, 0.06f, 0.9f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            Color border = accent; border.a = 0.95f;
            GUI.color = border;
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.yMax - 1f, box.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x, box.y, 1f, box.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.xMax - 1f, box.y, 1f, box.height), Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.Label(box, text, body);
        }

        /// <summary>The point at arc length <paramref name="d"/> along the
        /// screen polyline, interpolated within the segment it falls in.</summary>
        static Vector2 PointAtArc(List<Vector2> pts, float[] cum, float d)
        {
            int n = pts.Count;
            if (d <= 0f) return pts[0];
            if (d >= cum[n - 1]) return pts[n - 1];
            int i = 1;
            while (i < n - 1 && cum[i] < d) i++;
            float segLen = cum[i] - cum[i - 1];
            float t = segLen > 1e-4f ? (d - cum[i - 1]) / segLen : 0f;
            return Vector2.Lerp(pts[i - 1], pts[i], t);
        }


        /// <summary>
        /// A coarse spatial hash of the dash centres drawn so far, so a route
        /// can ask whether a candidate stamp lands within the clearance of any
        /// line drawn before it. Cells are one clearance wide, so a 3x3
        /// neighbourhood covers the whole clearance radius around a point.
        /// </summary>
        sealed class ClearGrid
        {
            readonly float _cell;
            readonly float _r2;
            readonly Dictionary<long, List<Vector2>> _cells =
                new Dictionary<long, List<Vector2>>();

            public ClearGrid(float clearance)
            {
                _cell = Mathf.Max(4f, clearance);
                _r2 = clearance * clearance;
            }

            static long Key(int cx, int cy)
            {
                // Offset well clear of zero so negative cells stay distinct.
                return ((long)(cx + 1048576)) * 4194304L + (cy + 1048576);
            }

            public void Add(List<Vector2> pts)
            {
                if (pts == null) return;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 p = pts[i];
                    long k = Key((int)Mathf.Floor(p.x / _cell),
                                 (int)Mathf.Floor(p.y / _cell));
                    List<Vector2> list;
                    if (!_cells.TryGetValue(k, out list))
                    {
                        list = new List<Vector2>();
                        _cells[k] = list;
                    }
                    list.Add(p);
                }
            }

            public bool Blocked(Vector2 p)
            {
                if (_r2 <= 0f || _cells.Count == 0) return false;
                int cx = (int)Mathf.Floor(p.x / _cell);
                int cy = (int)Mathf.Floor(p.y / _cell);
                for (int gx = cx - 1; gx <= cx + 1; gx++)
                    for (int gy = cy - 1; gy <= cy + 1; gy++)
                    {
                        List<Vector2> list;
                        if (!_cells.TryGetValue(Key(gx, gy), out list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float dx = p.x - list[i].x;
                            float dy = p.y - list[i].y;
                            if (dx * dx + dy * dy < _r2) return true;
                        }
                    }
                return false;
            }
        }

        // =====================================================================
        //  The route editor
        // =====================================================================

        /// <summary>
        /// The window that makes a route without a text file.
        ///
        /// Everything it does was already possible from `nextday.revival.toolkit.cfg`
        /// plus three keys, and that is exactly the problem it solves: the
        /// settings that matter are PER ROUTE - which side patrols it, what
        /// drives it, how many - and a config file has one of each. The user's
        /// plan is patrols at many places on the map, a looter patrol outside
        /// the looter base and a civilian one around the civilian base, and
        /// that plan needs a place to say so per route. This is that place,
        /// and it writes what it is told straight into the flags of each
        /// route's first waypoint (see <see cref="Route"/>).
        ///
        /// It is nested inside Patrol on purpose: it works on `Route`, which
        /// is Patrol's own type, and a second class outside would have needed
        /// a translation layer of strings for no gain.
        /// </summary>
        internal static class Editor
        {
            const int FensterId = 0x4E445242;

            static bool _offen;
            static bool _fokusLoesen;
            static Rect _fenster = new Rect(60f, 60f, 470f, 0f);
            static Vector2 _rollen;
            static string _neu = "";
            static string _status = "";
            static string _loeschFrage = "";

            public static bool IsOpen { get { return _offen; } }

            public static void Tick()
            {
                if (!Input.GetKeyDown(_editKey)) return;
                _offen = !_offen;
                if (_offen) Load(false);
                else { _fokusLoesen = true; CursorZurueck(); }
                RevivalPlugin.L.LogInfo("Patrol editor " + (_offen ? "open" : "closed") + ".");
            }

            static void CursorZurueck()
            {
                if (!CursorTracker.SawCall) return;
                CursorTracker.Restoring = true;
                try
                {
                    Cursor.lockState = CursorTracker.DesiredLock;
                    Cursor.visible = CursorTracker.DesiredVisible;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Patrol editor cursor: " + ex.Message);
                }
                finally { CursorTracker.Restoring = false; }
            }

            public static void Draw()
            {
                if (_fokusLoesen)
                {
                    _fokusLoesen = false;
                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
                if (!_offen || !RevivalPlugin.CfgPatrol.Value) return;

                CursorTracker.Restoring = true;
                try
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                finally { CursorTracker.Restoring = false; }

                _fenster = GUILayout.Window(FensterId, _fenster, Inhalt,
                                            Loc.T("Revival - Маршруты патрулей",
                                                  "Revival - Patrol routes"));
            }

            static void Inhalt(int id)
            {
                // ------------------------------------------------ recording
                GUILayout.Label(_recording
                    ? Loc.T("ЗАПИСЬ в \"" + RevivalPlugin.CfgPatrolRoute.Value
                          + "\" - точка каждые "
                          + RevivalPlugin.CfgPatrolRecordSeconds.Value.ToString("0.#")
                          + " с во время езды.",
                            "RECORDING into \"" + RevivalPlugin.CfgPatrolRoute.Value
                          + "\" - a waypoint every "
                          + RevivalPlugin.CfgPatrolRecordSeconds.Value.ToString("0.#")
                          + " s while you drive.")
                    : Loc.T("Запись не идёт. Проедьте дорогу для патруля, затем остановитесь.",
                            "Not recording. Drive the road you want patrolled, then stop."));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_recording ? Loc.T("стоп запись", "stop recording")
                                                : Loc.T("запись", "record"),
                                     GUILayout.Width(130f)))
                {
                    ToggleRecording();
                    Melde(_recording ? Loc.T("запись ", "recording ") + RevivalPlugin.CfgPatrolRoute.Value
                                     : Loc.T("запись остановлена", "recording stopped"));
                }
                if (GUILayout.Button(Loc.T("точка здесь", "waypoint here"), GUILayout.Width(120f)))
                {
                    RecordHere(true);
                    Melde(Loc.T("точка добавлена в ", "waypoint added to ") + RevivalPlugin.CfgPatrolRoute.Value);
                }
                if (GUILayout.Button(Loc.T("отменить последнюю", "undo last"), GUILayout.Width(90f))) Zurueck();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("Новый маршрут:", "New route:"), GUILayout.Width(70f));
                _neu = GUILayout.TextField(_neu, 24, GUILayout.Width(140f));
                if (GUILayout.Button(Loc.T("создать и записать", "create and record"), GUILayout.Width(150f))) Anlegen();
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);

                // --------------------------------------------------- routes
                // WHICH MAP THIS IS. The name the game loads the scene by, so
                // an admin who walks into another region can read it off here
                // and write it into the browser editor's map table
                // (assets/editor/maps.json). Routes of another map are listed
                // greyed out below and are neither drawn nor driven here.
                GUILayout.Label(Loc.T("Эта карта (сцена): ", "This map (scene): ")
                    + MapScene.Current);
                GUILayout.Label(Loc.T("Маршруты - каждый это отдельный патруль",
                                      "Routes - each one is its own patrol"));
                _rollen = GUILayout.BeginScrollView(_rollen, GUILayout.Height(260f));
                for (int i = 0; i < _order.Count; i++) Zeile(_routes[_order[i]]);
                if (_order.Count == 0)
                    GUILayout.Label(Loc.T("Пока пусто. Впишите имя выше, нажмите "
                        + "\"создать и записать\" и проедьте дорогу.",
                            "None yet. Type a name above, press "
                        + "\"create and record\", and drive the road."));
                GUILayout.EndScrollView();

                GUILayout.Space(6f);

                // ------------------------------------------------ the road
                int max = PatrolVehicleLimit();
                GUILayout.Label(Loc.T("На дороге: ", "On the road: ") + PatrolUnitCount()
                    + Loc.T(" из ", " of ") + max
                    + Loc.T(" (patrol capacity). Автоматика: ", " (patrol capacity). Automatic: ")
                    + (_auto ? Loc.T("вкл", "on") : Loc.T("ВЫКЛ", "OFF")));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_auto ? Loc.T("автоматика выкл", "automatic off")
                                           : Loc.T("автоматика вкл", "automatic on"),
                                     GUILayout.Width(130f)))
                {
                    if (_auto) { StopAll(); Melde(Loc.T("все патрули убраны с дороги", "all patrols off the road")); }
                    else { Toggle(); Melde(Loc.T("автоматика включена", "automatic on")); }
                }
                if (GUILayout.Button(Loc.T("убрать с дороги", "clear the road"), GUILayout.Width(120f)))
                {
                    StopAll();
                    Melde(Loc.T("все патрули убраны с дороги", "all patrols off the road"));
                }
                if (GUILayout.Button(Loc.T("сохранить файл", "save file"), GUILayout.Width(90f)))
                {
                    Save();
                    Melde(Loc.T("записано в ", "written to ") + RevivalPlugin.CfgPatrolFile.Value);
                }
                if (GUILayout.Button(Loc.T("перезагрузить", "reload"), GUILayout.Width(70f)))
                {
                    Load(true);
                    Melde(Loc.T("прочитано из ", "read back from ") + RevivalPlugin.CfgPatrolFile.Value);
                }
                GUILayout.EndHorizontal();

                if (_status.Length > 0) GUILayout.Label(_status);
                GUILayout.Label(Loc.T(
                    "civilian бьёт всех, кроме civilian. looter всех, кроме looter. "
                    + "traitor бьёт ВСЕХ. neutral бьёт только traitor.",
                    "civilian attacks everyone but civilians. looter "
                    + "everyone but looters. traitor attacks EVERYONE. neutral "
                    + "attacks traitors only."));

                if (GUILayout.Button(Loc.T("закрыть", "close"))) { _offen = false; CursorZurueck(); }
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
            }

            /// <summary>
            /// One route. Every change here is written to the file at once:
            /// the alternative is a window whose state is lost when the world
            /// ends, and a route editor that loses routes is worse than no
            /// route editor. `Save` is a few dozen lines of text - it can be
            /// afforded on a button press.
            /// </summary>
            static void Zeile(Route r)
            {
                bool aktiv = RevivalPlugin.CfgPatrolRoute.Value == r.Name;
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label((aktiv ? "> " : "  ") + r.Name + "  "
                    + r.P.Count + Loc.T(" тчк  ", " wp  ") + Fahren(r.Name) + "/" + r.Count
                    + Loc.T(" в рейсе", " out")
                    // A route of ANOTHER map says so, and says which. Without
                    // this line a route that is simply not in this region reads
                    // as a route that is broken.
                    + (r.Here ? "" : "  [" + MapScene.Label(r.Scene) + "]"),
                    GUILayout.Width(190f));
                bool an = GUILayout.Toggle(r.Enabled, Loc.T("вкл", "on"), GUILayout.Width(45f));
                if (an != r.Enabled)
                {
                    r.Enabled = an;
                    Sichern(r.Name + (an ? Loc.T(" включён", " is on")
                                         : Loc.T(" выключен - автоматика его не трогает",
                                                 " is off - the automatic leaves it alone")));
                }
                if (GUILayout.Button("-", GUILayout.Width(24f)) && r.Count > 0)
                {
                    r.Count--;
                    Sichern(r.Name + Loc.T(" несёт патрулей: ", " carries ") + r.Count
                            + Loc.T("", " patrol(s)"));
                }
                GUILayout.Label(r.Count.ToString(), GUILayout.Width(20f));
                if (GUILayout.Button("+", GUILayout.Width(24f)) && r.Count < 16)
                {
                    r.Count++;
                    Sichern(r.Name + Loc.T(" несёт патрулей: ", " carries ") + r.Count
                            + Loc.T("", " patrol(s)"));
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("сторона", "side"), GUILayout.Width(34f));
                for (int i = 0; i < Fraktion.Namen.Length; i++)
                {
                    string n = Fraktion.Namen[i];
                    // `Seite` is the EFFECTIVE side, so a route that never
                    // chose one shows the config's choice as selected. Pressing
                    // that same button is therefore not a no-op: it pins the
                    // choice to the route, which is what the user meant by
                    // pressing it.
                    if (GUILayout.Toggle(r.Seite == n, n, GUI.skin.button,
                                         GUILayout.Width(72f))
                        && r.Fraction != n)
                    {
                        r.Fraction = n;
                        Sichern(r.Name + Loc.T(" - ", " is ") + n + " - " + Fraktion.Erklaerung(n));
                    }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("техника", "car"), GUILayout.Width(34f));
                Wagenknopf(r, "btr");
                Wagenknopf(r, "tank");
                Wagenknopf(r, "mixed");
                if (GUILayout.Button(Loc.T("писать сюда", "record into"), GUILayout.Width(90f)))
                {
                    RevivalPlugin.CfgPatrolRoute.Value = r.Name;
                    if (!_recording) ToggleRecording();
                    Melde(Loc.T("запись в ", "recording into ") + r.Name);
                }
                if (GUILayout.Button(Loc.T("патруль сейчас", "patrol now"), GUILayout.Width(85f))) Jetzt(r);
                GUILayout.EndHorizontal();

                // NDR convoy: mark this route as a convoy route (a column of
                // 2 tanks + 2 APCs that the convoy event drives), and force one
                // out now for testing.
                GUILayout.BeginHorizontal();
                bool conv = GUILayout.Toggle(r.IsConvoy, Loc.T("конвой", "convoy"),
                                             GUI.skin.button, GUILayout.Width(80f));
                if (conv != r.IsConvoy)
                {
                    r.Kind = conv ? "convoy" : "";
                    Sichern(conv
                        ? r.Name + Loc.T(" - маршрут конвоя (танк-БТР-БТР-танк)",
                                         " is a convoy route (tank-APC-APC-tank)")
                        : r.Name + Loc.T(" - обычный патруль", " is an ordinary patrol"));
                }
                if (r.IsConvoy && GUILayout.Button(Loc.T("конвой сейчас", "convoy now"),
                                                   GUILayout.Width(110f)))
                    Melde(RevivalConvoy.SpawnOn(r.Name)
                        ? Loc.T("конвой выехал на ", "convoy sent out on ") + r.Name
                        : Loc.T("конвой не удалось запустить (см. лог)",
                                "convoy could not start (see log)"));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (_loeschFrage == r.Name)
                {
                    GUILayout.Label(Loc.T("Удалить ", "Delete ") + r.Name
                        + Loc.T(" и его точек: ", " and its ") + r.P.Count
                        + Loc.T("?", " waypoints?"), GUILayout.Width(250f));
                    if (GUILayout.Button(Loc.T("да, удалить", "yes, delete"), GUILayout.Width(90f))) Loeschen(r);
                    if (GUILayout.Button(Loc.T("нет", "no"), GUILayout.Width(40f))) _loeschFrage = "";
                }
                else
                {
                    if (GUILayout.Button(Loc.T("удалить маршрут", "delete route"), GUILayout.Width(100f)))
                        _loeschFrage = r.Name;
                }
                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }

            static void Wagenknopf(Route r, string was)
            {
                if (GUILayout.Toggle(r.Wagen == was, was, GUI.skin.button,
                                     GUILayout.Width(54f))
                    && r.Vehicle != was)
                {
                    r.Vehicle = was;
                    Sichern(r.Name + Loc.T(" - техника ", " drives ") + was);
                }
            }

            /// <summary>Say it and write it. Every setting in this window goes
            /// through here.</summary>
            static void Sichern(string text)
            {
                Save();
                Melde(text);
            }

            static void Anlegen()
            {
                string name = _neu == null ? "" : _neu.Trim();
                if (name.Length == 0) { Melde(Loc.T("маршруту нужно имя", "a route needs a name")); return; }
                if (name.IndexOf('\t') >= 0 || name[0] == '#')
                {
                    Melde(Loc.T("без табов и без # в начале - файл это TSV",
                                "no tabs and no leading # - the file is a TSV"));
                    return;
                }
                Load(false);
                if (_routes.ContainsKey(name))
                {
                    Melde("\"" + name + "\"" + Loc.T(" уже есть - используйте \"писать сюда\"",
                                                     " is already there - use \"record into\""));
                    return;
                }
                Route r = new Route();
                r.Name = name;
                r.Scene = MapScene.Current;   // the map the admin is standing in
                _routes[name] = r;
                _order.Add(name);
                RevivalPlugin.CfgPatrolRoute.Value = name;
                _neu = "";
                if (!_recording) ToggleRecording();
                Melde(Loc.T("запись " + name + " - проедьте дорогу, затем стоп",
                            "recording " + name + " - drive the road, then press stop"));
            }

            static void Zurueck()
            {
                Route r = Active();
                if (r == null || r.P.Count == 0) { Melde(Loc.T("отменять нечего", "nothing to undo")); return; }
                r.P.RemoveAt(r.P.Count - 1);
                Save();
                Melde(r.Name + Loc.T(" теперь имеет точек: ", " now has ") + r.P.Count
                      + Loc.T("", " waypoints"));
            }

            static void Loeschen(Route r)
            {
                _loeschFrage = "";
                _routes.Remove(r.Name);
                _order.Remove(r.Name);
                Save();
                Melde(r.Name + Loc.T(" удалён", " deleted"));
                RevivalPlugin.L.LogInfo("Patrol: route " + r.Name + " deleted from "
                    + RevivalPlugin.CfgPatrolFile.Value + ".");
            }

            /// <summary>One patrol on this route now, wherever the player is
            /// standing. It goes through the same Spawn the automatic uses, so
            /// a route that works here works there.</summary>
            static void Jetzt(Route r)
            {
                if (r.P.Count < 3) { Melde(r.Name + Loc.T(" нужно минимум три точки", " needs at least three waypoints")); return; }
                if (!r.Here)
                {
                    Melde(r.Name + Loc.T(" на другой карте (", " is on another map (")
                          + MapScene.Label(r.Scene) + Loc.T(") - здесь его дорог нет",
                                                            ") - its roads are not in this region"));
                    return;
                }
                int max = PatrolVehicleLimit();
                if (PatrolUnitCount() >= max)
                {
                    Melde(Loc.T("patrol capacity = " + max + ", в рейсе " + PatrolUnitCount()
                          + " - сначала уберите с дороги",
                            "patrol capacity is " + max + " and " + PatrolUnitCount()
                          + " are out - clear the road first"));
                    return;
                }
                int before = _units.Count;
                Spawn(r, false);
                Melde(_units.Count > before
                    ? r.Name + Loc.T(": один патруль (" + r.Seite + ") на дороге",
                                     ": one " + r.Seite + " patrol on the road")
                    : Loc.T("технику не удалось выставить - см. лог",
                            "the vehicle could not be put down - see the log"));
            }

            static void Melde(string text)
            {
                _status = text;
                RevivalPlugin.L.LogInfo("Patrol editor: " + text);
            }
        }

        // =====================================================================
        //  Reflection, cached. The plugin references no Assembly-CSharp and no
        //  PhysicsModule, so every field here goes through AccessTools - and a
        //  fresh lookup per physics step for six vehicles is not free.
        // =====================================================================

        static Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        static Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        static PropertyInfo _velocity, _angular;
        static bool _bodyLookedUp;

        static FieldInfo Field(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            string key = t.FullName + "|" + name;
            FieldInfo fi;
            if (_fieldCache.TryGetValue(key, out fi)) return fi;
            fi = AccessTools.Field(t, name);
            _fieldCache[key] = fi;
            if (fi == null)
                RevivalPlugin.L.LogWarning("Patrol: field " + name + " not on " + t.Name + ".");
            return fi;
        }

        static object GetField(object o, string name)
        {
            FieldInfo fi = Field(o, name);
            return fi == null ? null : fi.GetValue(o);
        }

        static float GetFloat(object o, string name, float fallback)
        {
            FieldInfo fi = Field(o, name);
            if (fi == null || fi.FieldType != typeof(float)) return fallback;
            return (float)fi.GetValue(o);
        }

        static void SetFloat(object o, string name, float value)
        {
            FieldInfo fi = Field(o, name);
            if (fi != null && fi.FieldType == typeof(float)) fi.SetValue(o, value);
        }

        static bool GetBool(object o, string name, bool fallback)
        {
            FieldInfo fi = Field(o, name);
            if (fi == null || fi.FieldType != typeof(bool)) return fallback;
            return (bool)fi.GetValue(o);
        }

        static void SetBool(object o, string name, bool value)
        {
            FieldInfo fi = Field(o, name);
            if (fi != null && fi.FieldType == typeof(bool)) fi.SetValue(o, value);
        }

        static void Invoke(object o, string name)
        {
            if (o == null) return;
            Type t = o.GetType();
            string key = t.FullName + "|()" + name;
            MethodInfo mi;
            if (!_methodCache.TryGetValue(key, out mi))
            {
                mi = AccessTools.Method(t, name, null, null);
                _methodCache[key] = mi;
                if (mi == null)
                    RevivalPlugin.L.LogWarning("Patrol: method " + name + " not on " + t.Name + ".");
            }
            if (mi != null) mi.Invoke(o, null);
        }

        static void LookUpBody()
        {
            if (_bodyLookedUp) return;
            _bodyLookedUp = true;
            Type rb = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
            if (rb == null)
            {
                RevivalPlugin.L.LogWarning("Patrol: UnityEngine.Rigidbody not found - "
                    + "speed will be read as zero and the driver will floor it.");
                return;
            }
            _velocity = rb.GetProperty("velocity", BindingFlags.Public | BindingFlags.Instance);
            _angular = rb.GetProperty("angularVelocity", BindingFlags.Public | BindingFlags.Instance);
        }

        static Vector3 Velocity(object body)
        {
            LookUpBody();
            if (body == null || _velocity == null) return Vector3.zero;
            return (Vector3)_velocity.GetValue(body, null);
        }

        static void Stop(object body)
        {
            LookUpBody();
            if (body == null) return;
            if (_velocity != null) _velocity.SetValue(body, Vector3.zero, null);
            if (_angular != null) _angular.SetValue(body, Vector3.zero, null);
        }

        /// <summary>Tell the body how fast it is going without letting it decide
        /// where it goes. The column moves the hull by hand, but the wheels and
        /// the tank tracks are animated from motion - a carried vehicle with a
        /// zeroed velocity would slide down the road on locked wheels.</summary>
        static void Roll(object body, Vector3 velocity)
        {
            LookUpBody();
            if (body == null) return;
            if (_velocity != null) _velocity.SetValue(body, velocity, null);
            if (_angular != null) _angular.SetValue(body, Vector3.zero, null);
        }

        // =====================================================================
        //  Keys
        // =====================================================================

        static void ParseKeys()
        {
            if (_keysParsed) return;
            _keysParsed = true;
            _key = ParseKey(RevivalPlugin.CfgPatrolKey.Value, KeyCode.F11, "Key");
            _autoKey = ParseKey(RevivalPlugin.CfgPatrolAutoKey.Value, KeyCode.F5, "RecordAutoKey");
            _recKey = ParseKey(RevivalPlugin.CfgPatrolRecordKey.Value, KeyCode.F6, "RecordKey");
            _editKey = ParseKey(RevivalPlugin.CfgPatrolEditorKey.Value, KeyCode.F4, "EditorKey");
        }

        static KeyCode ParseKey(string text, KeyCode fallback, string which)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), text, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Patrol: " + which + " \"" + text
                    + "\" is not a KeyCode, using " + fallback + ".");
                return fallback;
            }
        }
    }

    // ------------------------------------------------------- who shoots whom

    /// <summary>
    /// The four sides a patrol can be on, and what each of them attacks.
    ///
    /// The game already has the enum - `Fraction`, eight values, Neutral 0,
    /// Marauder 1, Peace 2, Hermit 3, Wildman 4, Military 5, Traitor 6,
    /// MilitaryNeutral 7 - and it already has the one method that decides a
    /// fight (read out of Assembly-CSharp.dll on 2026-08-30, RE 23):
    ///
    ///     NPC_AI2::IsEnemyFraction(player)
    ///         fraction = PlayerStatisticsManager.GetPlayerInfo(player).fraction
    ///         walk THIS NPC's MainOptions.HatedFractions
    ///         return true if the player's fraction is in it
    ///
    /// So enmity is not a table somewhere: it is an ARRAY on the NPC, and
    /// whoever fills that array decides who is shot at. `NPC_Settlement::
    /// SetNpcParams` copies it from the settlement's `FractionOptions` (or the
    /// spawn point's `IndividualFraction` where `UseIndividualFraction` is
    /// set), and both of those are ours to write.
    ///
    /// The game's own hardcoded relations (PlayerStatisticsManager::
    /// GetHatedFractionsOfNPC) are only used for the map markers and are NOT
    /// what an NPC fights by. They are listed here because they are the reason
    /// the four names below map onto the enum values they do - a patrol whose
    /// marker colour disagreed with its trigger finger would be a bug report:
    ///
    ///     Neutral   hates Marauder
    ///     Marauder  hates everyone except Marauder and MilitaryNeutral
    ///     Peace     hates Marauder, Military, Traitor
    ///     Hermit    hates Traitor
    ///     Wildman   hates Marauder, Traitor
    ///     Military  hates everyone except Military and MilitaryNeutral
    ///     Traitor   hates EVERYONE, itself included
    ///     MilitaryNeutral returns null - never use it, IsHatedFraction does
    ///                     ldlen on the result and would throw.
    ///
    /// The four names the user asked for, and what each becomes:
    ///
    ///     civilian  Peace     hates all seven others - everyone but civilians
    ///     looter    Marauder  hates all seven others - everyone but looters
    ///     traitor   Traitor   hates all seven others, never its own faction
    ///     neutral   Neutral   hates all seven others, never its own faction
    ///
    /// The hated arrays are written out in full rather than taken from the
    /// game's own table: every mod side now attacks all other factions and
    /// never itself. This includes neutral and traitor patrols and crews.
    /// </summary>
    public static class Fraktion
    {
        /// <summary>The enum values, by name, so nothing here depends on a
        /// number staying where it is.</summary>
        const string Neutral = "Neutral";
        const string Marauder = "Marauder";
        const string Peace = "Peace";
        const string Hermit = "Hermit";
        const string Wildman = "Wildman";
        const string Military = "Military";
        const string Traitor = "Traitor";
        const string MilitaryNeutral = "MilitaryNeutral";

        /// <summary>The four names a route may carry, in the order the editor
        /// shows them.</summary>
        public static readonly string[] Namen =
            new string[] { "civilian", "looter", "traitor", "neutral" };

        /// <summary>One line each, for the editor and the log.</summary>
        public static string Erklaerung(string name)
        {
            switch (Sauber(name))
            {
                case "civilian": return Loc.T("бьёт всех, кроме civilian", "attacks everyone but civilians");
                case "looter": return Loc.T("бьёт всех, кроме looter", "attacks everyone but looters");
                case "traitor": return Loc.T("бьёт всех, кроме traitor", "attacks everyone but traitors");
                default: return Loc.T("бьёт всех, кроме neutral", "attacks everyone but neutrals");
            }
        }

        /// <summary>A name the rest of the code can rely on. Anything
        /// unreadable becomes the configured default, and that is said once
        /// where it happens, not once a frame.</summary>
        public static string Sauber(string name)
        {
            if (name == null) return "neutral";
            string n = name.Trim().ToLowerInvariant();
            for (int i = 0; i < Namen.Length; i++)
                if (Namen[i] == n) return n;
            return "";
        }

        /// <summary>The game's own enum value this side is.</summary>
        internal static string Eigene(string name)
        {
            switch (name)
            {
                case "civilian": return Peace;
                case "looter": return Marauder;
                case "traitor": return Traitor;
                default: return Neutral;
            }
        }

        /// <summary>Everything this side shoots at.</summary>
        static string[] Gehasste(string name)
        {
            switch (name)
            {
                case "civilian":
                    return new string[] { Neutral, Marauder, Hermit, Wildman,
                                          Military, Traitor, MilitaryNeutral };
                case "looter":
                    return new string[] { Neutral, Peace, Hermit, Wildman,
                                          Military, Traitor, MilitaryNeutral };
                case "traitor":
                    return new string[] { Neutral, Marauder, Peace, Hermit,
                                          Wildman, Military,
                                          MilitaryNeutral };
                default:
                    return new string[] { Marauder, Peace, Hermit, Wildman,
                                          Military, Traitor, MilitaryNeutral };
            }
        }

        /// <summary>Unknown factions are not permission to open fire.</summary>
        internal static bool Feind(string side, string faction)
        {
            string name = Sauber(side);
            if (name.Length == 0 || string.IsNullOrEmpty(faction)) return false;
            if (Eigene(name) == faction) return false;
            return faction == Neutral || faction == Marauder || faction == Peace
                || faction == Hermit || faction == Wildman || faction == Military
                || faction == Traitor || faction == MilitaryNeutral;
        }

        internal static string Spielerseite(GameObject player)
        {
            int faction = Mortar.FactionShield.FactionOf(player);
            Type type = RevivalPlugin.TypeByName("Fraction");
            return faction < 0 || type == null || !type.IsEnum
                ? null : Enum.GetName(type, faction);
        }

        /// <summary>
        /// Build one `NPCMainOptions` for this side: MyFraction, the hated
        /// array, and an EMPTY friendly array.
        ///
        /// The empty array is not tidiness. `FriendlyFractions` is never read
        /// by the AI - only `HatedFractions` is - but a null array in a field
        /// the game may one day walk is a crash waiting for a patch, and an
        /// empty one costs nothing. `HatedFractions` itself MUST be an array:
        /// `IsEnemyFraction` does `ldlen` on it with no null check.
        /// </summary>
        public static object Optionen(string name)
        {
            Type t = RevivalPlugin.TypeByName("NPCMainOptions");
            Type f = RevivalPlugin.TypeByName("Fraction");
            if (t == null || f == null || !f.IsEnum)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions or the "
                    + "Fraction enum is missing - a patrol crew will use "
                    + "whatever the game gives it.");
                return null;
            }

            string wer = Sauber(name);
            if (wer.Length == 0) wer = "neutral";

            object o;
            try { o = Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions could not "
                    + "be built - " + ex.Message);
                return null;
            }

            Feld(o, "MyFraction", Wert(f, Eigene(wer)));
            Feld(o, "HatedFractions", Liste(f, Gehasste(wer)));
            Feld(o, "FriendlyFractions", Array.CreateInstance(f, 0));
            Feld(o, "ID", "");
            Feld(o, "GroupID", "");
            return o;
        }

        static object Wert(Type enumType, string name)
        {
            try { return Enum.Parse(enumType, name, true); }
            catch
            {
                RevivalPlugin.L.LogWarning("Fraktion: the Fraction enum has no "
                    + name + " - falling back to the first value.");
                return Enum.ToObject(enumType, 0);
            }
        }

        static Array Liste(Type enumType, string[] namen)
        {
            Array a = Array.CreateInstance(enumType, namen.Length);
            for (int i = 0; i < namen.Length; i++)
                a.SetValue(Wert(enumType, namen[i]), i);
            return a;
        }

        static void Feld(object o, string name, object value)
        {
            FieldInfo fi = AccessTools.Field(o.GetType(), name);
            if (fi == null)
            {
                RevivalPlugin.L.LogWarning("Fraktion: NPCMainOptions has no "
                    + name + ".");
                return;
            }
            try { fi.SetValue(o, value); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Fraktion: " + name + " not set - "
                    + ex.Message);
            }
        }
    }
}

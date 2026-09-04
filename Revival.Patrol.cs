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
            /// automatic keeps that many alive on it, inside the global
            /// MaxVehicles.</summary>
            public int Count = 1;

            /// <summary>Does the automatic use this route at all? A route
            /// being recorded, or one whose waypoints are wrong, is switched
            /// off without being deleted.</summary>
            public bool Enabled = true;

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
                    if (v == "btr" || v == "tank" || v == "mixed" || v == "ural") return v;
                    v = RevivalPlugin.CfgPatrolVehicle.Value;
                    if (v == null) return "mixed";
                    v = v.Trim().ToLowerInvariant();
                    return (v == "btr" || v == "tank" || v == "ural") ? v : "mixed";
                }
            }

            // Cached map ring in WORLD space (XZ; y is 0 and ignored by
            // WorldToGui). Built once from the waypoints and only PROJECTED each
            // frame, so the encircling ring no longer jitters as the map or
            // camera micro-moves - the convex hull is computed in one fixed
            // world frame, not per frame in screen space where a waypoint
            // flipping on or off the hull made the whole ring pop. Rebuilt only
            // when the waypoint count or the padding changes.
            internal List<Vector3> MapRing;
            internal int MapRingN = -1;
            internal float MapRingPad = -1f;

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

            // ---------------------------------------------------- the gun
            public bool Tank;            // which of the two value profiles
            public Transform[] Turrets = new Transform[0];
            public Renderer TurretRend;  // for the muzzle, see Muendung
            public float Yaw, Pitch;     // where the barrel is being sent
            public Transform Target;     // the player being engaged
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
            public float Died;           // Time.time the vehicle was killed
            public int CompositionVehicle = -1; // editor vehicle index, or legacy
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
            // Colliders of this convoy car, cached for "ghost through": convoy
            // vehicles pass through world props and each other via
            // Physics.IgnoreCollision, so they never crash, snag, or shove one
            // another off the road. Their own body collider stays live, so
            // bullets still hit and kill them. Ghosted holds the ids already made
            // non-colliding, so each obstacle is handled at most once.
            public Component[] Cols;
            public Dictionary<int, bool> Ghosted;

            // NDR convoy column (Columns(), ColumnLock). While Column is true
            // this vehicle is NOT driven by RCC at all: it is placed on the
            // route line every physics step at its own slot behind the column
            // head, so the editor order and the spacing hold exactly and no
            // amount of terrain, props or physics can break the formation.
            // Index is the slot (0 = front). Lift is the metres from the hull
            // origin down to the lowest point of the model, measured once, so
            // the vehicle stands ON the road instead of hovering or sinking.
            // Placed marks that the first exact placement has happened; after
            // that the heading is eased instead of snapped.
            public bool Column;
            public int ColumnIndex = -1;
            public float ColumnLift;
            public bool Placed;
        }

        static List<Unit> _units = new List<Unit>();
        static int _nextPatrolGroupId = 1;

        // --------------------------------------------------- the automatic

        /// <summary>Seconds between two vehicles while the road is being
        /// FILLED. A replacement waits `RespawnSeconds` instead; this is only
        /// so the first MaxVehicles do not all appear in the same second.</summary>
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
                    // NDR convoy one-way: a convoy that has driven its whole
                    // recorded route to the last waypoint vanishes there.
                    if (u.Arrived) { ArriveEnd(u, i); continue; }
                    Keep(u);
                    // NDR convoy column: Columns() has already put this vehicle
                    // where it belongs. It is not driven and it is not held.
                    if (u.Column) continue;
                    if (u.Hold) { HoldStill(u); continue; }   // NDR convoy: spacing / hold-and-search
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

            int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
            if (PatrolUnitCount() >= max)   // NDR convoy: convoy vehicles do not count
            {
                RevivalPlugin.L.LogInfo("Patrol: " + _units.Count + " vehicle(s) are "
                    + "already out, MaxVehicles is " + max
                    + ". Shift plus the key takes them off the road.");
                Turret.Hinweis(_units.Count + Loc.T(" патрулей в рейсе - Shift+клавиша убирает",
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
        /// finds the route, `MaxVehicles` bounds the road.
        ///
        /// EVERY ROUTE IS A PATROL. Since 2026-08-30 the automatic does not
        /// serve one route out of the config but every route in the file:
        /// each says how many patrols it wants (`count=` in its first
        /// waypoint's flags) and which side drives it, `Duenn` picks the one
        /// furthest short of its number, and `MaxVehicles` is the ceiling over
        /// all of them together. That is what makes a looter patrol outside
        /// the looter base and a civilian one around the civilian base a
        /// setting rather than a rebuild.
        ///
        /// TWO CLOCKS, and they mean different things. The road is FILLED at
        /// `FillEvery` - a short interval, so the first MaxVehicles are out
        /// within a minute of the world coming up. A LOSS is replaced after
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
                    // The scene is gone and so is everything that stood in it.
                    // The units would be a list of destroyed GameObjects, and
                    // the next world would inherit them.
                    _units.Clear();
                    _owned.Clear();
                    _refill = true;
                }
                return;
            }
            if (!welt) return;

            int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
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
        /// The route most in need of a vehicle, or null when every one of them
        /// has what it asked for.
        ///
        /// EVERY route is a patrol now, not just the one named in the config.
        /// The user's plan is patrols at many places on the map - a looter
        /// patrol outside the looter base, a civilian one around the civilian
        /// base - and that means the automatic has to keep several routes
        /// stocked at once instead of one. Each route says how many it wants
        /// (`count=` in its first waypoint's flags, 1 when it does not say),
        /// and `MaxVehicles` is the ceiling over the whole map.
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

        /// <summary>Units the auto-patrol accounting owns - convoy vehicles
        /// (ConvoyId != 0) are managed by the convoy event and must not consume
        /// a MaxVehicles slot from the ordinary patrols. NDR convoy.</summary>
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
            float roadY;
            Vector3 roadNormal;
            Vector3 pos = RoadUnder(line, null, out roadY, out roadNormal)
                        ? new Vector3(line.x, roadY + 1.6f, line.z)
                        : Grounded(line, 1.6f);

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
            u.ConvoyId = convoyId;
            u.CompositionVehicle = compositionVehicle;
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

            if (u.Column) ColumnStart(convoyId, headArc);

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
            return u != null && u.Car != null && u.Died <= 0f;
        }

        /// <summary>The vehicle still exists in the world - alive OR a lingering
        /// wreck. Used to know when a whole convoy has been fully cleared.</summary>
        internal static bool ConvoyExists(object handle)
        {
            Unit u = handle as Unit;
            return u != null && u.Car != null;
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
            if (u != null) u.Hold = hold;
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
        /// convoy) with enough waypoints to drive. The convoy event picks from
        /// these.</summary>
        internal static List<string> ConvoyRouteNames()
        {
            Load(false);
            List<string> names = new List<string>();
            for (int i = 0; i < _order.Count; i++)
            {
                Route r;
                if (_routes.TryGetValue(_order[i], out r) && r != null
                    && r.IsConvoy && r.Enabled && r.P.Count >= 3)
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
                if (rs[i] == null) continue;
                Bounds b = rs[i].bounds;
                if (b.size.sqrMagnitude < 0.0001f) continue;
                if (b.min.y < lowest) lowest = b.min.y;
            }
            if (lowest == float.MaxValue) return 1f;
            return Mathf.Clamp(car.transform.position.y - lowest, 0.05f, 3f);
        }

        /// <summary>
        /// The road surface under a route point.
        ///
        /// The ray is SHORT and starts three metres above the recorded line, for
        /// two reasons: a tunnel roof or a bridge deck above the road can not be
        /// mistaken for the ground, and the terrain far below a viaduct can not
        /// either. Hits on the convoy's own hull (it is standing on that very
        /// spot from the last step) and on anything that happens to be lying on
        /// the road are skipped, so a man walking in front of the column does not
        /// lift a tank onto his head.
        ///
        /// A route recorded before the editor carried heights has y near zero.
        /// There is no local surface to find, so the long cast of
        /// <see cref="Grounded"/> answers instead - the same convention the
        /// spawn code has always used.
        ///
        /// False means nothing usable was found; the recorded y is then the best
        /// answer there is.
        /// </summary>
        static bool RoadUnder(Vector3 point, Transform own, out float y,
                              out Vector3 normal)
        {
            y = point.y;
            normal = Vector3.up;

            if (point.y < 10f)
            {
                y = Grounded(point, 0f).y;
                return true;
            }

            Vector3 from = point + Vector3.up * 3f;
            float rest = 12f;
            for (int step = 0; step < 4 && rest > 0.1f; step++)
            {
                Vector3 hit, hitNormal;
                GameObject go = Turret.RaycastObject(from, Vector3.down, rest,
                                                     out hit, out hitNormal);
                if (go == null) return false;

                // Skipped: the convoy's own hull, and anything standing well
                // ABOVE the recorded line - a man on the road, a crate, a fence
                // rail. A hit BELOW the line is accepted whatever the drop is,
                // because that is what a route whose recorded height is a little
                // optimistic looks like from up here.
                bool mine = own != null && go.transform.IsChildOf(own);
                if (!mine && hit.y <= point.y + 1.5f)
                {
                    y = hit.y;
                    normal = hitNormal.sqrMagnitude > 0.01f ? hitNormal : Vector3.up;
                    return true;
                }
                rest -= (from.y - hit.y) + 0.2f;
                from = hit + Vector3.down * 0.2f;
            }
            return false;
        }

        /// <summary>Put one column vehicle on its slot: exact position on the
        /// recorded line, standing on the road, facing the way the road runs.
        /// The vehicle is not asked to drive there - it is put there.</summary>
        static void PlaceInColumn(Unit u, Route r, float arc, float speed)
        {
            int seg;
            Vector3 line = PointOnRoute(r, arc, out seg);
            Vector3 dir = HeadingOnRoute(r, arc);

            // Measured exactly once, on the FIRST placement, while the hull
            // still carries the level spawn rotation. A later measurement on a
            // slope would read a tilted bounding box and lift the vehicle off
            // the road by the tilt. The wheel MESHES exist from instantiation -
            // EnablePhys switches wheel COLLIDERS on, not the models - so there
            // is nothing to wait for.
            if (u.ColumnLift <= 0f) u.ColumnLift = HullDrop(u.Car);

            float y;
            Vector3 normal;
            if (!RoadUnder(line, u.Car.transform, out y, out normal))
            {
                y = line.y;
                normal = Vector3.up;
            }
            if (normal.y < 0.35f) normal = Vector3.up;

            Vector3 target = new Vector3(line.x, y + u.ColumnLift, line.z);

            Vector3 flat = dir - normal * Vector3.Dot(dir, normal);
            if (flat.sqrMagnitude < 0.0001f) { flat = dir; normal = Vector3.up; }
            Quaternion want = Quaternion.LookRotation(flat.normalized, normal);

            Transform t = u.Car.transform;
            t.position = target;
            if (!u.Placed)
            {
                t.rotation = want;
                u.Placed = true;
            }
            else
            {
                t.rotation = Quaternion.RotateTowards(t.rotation, want,
                    ColumnTurnRate * Time.fixedDeltaTime);
            }

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

            u.Stuck = 0f;
            u.Next = seg;
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

            Vector3 ahead = r.P[(start + 1) % r.P.Count].Pos - r.P[start].Pos;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f) ahead = Vector3.forward;
            ahead.Normalize();

            RevivalComposition.Composition composition = RevivalComposition.Of(r.Name);
            int vehicleCount = composition == null || composition.Vehicles.Count == 0
                ? 1 : composition.Vehicles.Count;
            int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
            if (PatrolUnitCount() + vehicleCount > max)
            {
                RevivalPlugin.L.LogWarning("Patrol: editor composition on " + r.Name
                    + " needs " + vehicleCount + " vehicle slots, but only "
                    + (max - PatrolUnitCount()) + " remain under MaxVehicles.");
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
                // ordinary patrol route behavior, but starts front-to-tail on
                // the first-leg centreline instead of stacking vehicles.
                Vector3 spot = r.P[start].Pos - ahead * (RevivalConvoy.LineupGap * k);
                Vector3 pos = Grounded(spot, 1.6f);
                bool tank;
                GameObject car = VehicleRegistry.Spawn(kind,
                    pos, Quaternion.LookRotation(ahead, Vector3.up), out tank);
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
                u.Next = (start + 1) % r.P.Count;
                u.CompositionVehicle = composition == null ? -1 : k;
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
            if (want == "btr") return false;
            if (want == "ural") return false;
            return (_spawned % 2) == 1;
        }

        /// <summary>The registry kind a route spawns: "ural" for a truck route,
        /// otherwise "tank"/"btr" resolved through <see cref="TankThisTime"/>
        /// (so "mixed" still alternates). This is the single place patrol maps a
        /// route's Vehicle flag to a registry kind.</summary>
        static string WagenKind(Route r)
        {
            if (r != null && r.Wagen == "ural") return "ural";
            return TankThisTime(r) ? "tank" : "btr";
        }

        /// <summary>The vehicle kind a NAMED route requests, for the convoy event
        /// to honour (a "ural" route becomes a truck convoy). Empty when the
        /// route is unknown or uses the default composition.</summary>
        internal static string RouteVehicle(string routeName)
        {
            Load(false);
            Route r;
            if (routeName != null && _routes.TryGetValue(routeName, out r) && r != null)
            {
                string w = r.Wagen;
                if (w == "ural") return "ural";
            }
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

        /// <summary>Put an authored X/Z point on the world surface. Roadnet v2
        /// carries terrain y, so its short local ray is precise. Manual and
        /// migrated routes may still have y=0; for those, start above the full
        /// terrain height range instead of spawning below the map.</summary>
        static Vector3 Grounded(Vector3 point, float lift)
        {
            Vector3 origin = point + Vector3.up * 30f;
            float range = 200f;
            if (point.y < 10f)
            {
                origin.y = 2500f;
                range = 3000f;
            }
            Vector3 ground;
            GameObject under = Turret.RaycastObject(origin, Vector3.down,
                                                    range, out ground);
            return under == null ? point + Vector3.up * lift
                                 : ground + Vector3.up * lift;
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
            List<RevivalComposition.CrewMan> crew = u.CompositionVehicle < 0
                ? null : RevivalComposition.CrewOf(u.Route.Name,
                                                    u.CompositionVehicle);
            // The editor's crew list is the LOADOUT of this vehicle's men, not
            // its head count: one role line used to clamp the whole crew to a
            // single man, so every convoy vehicle in the user's five-vehicle
            // column put exactly one crewman on the ground. The vehicle is
            // manned by its seats (Besatzung, already capped by CrewMax) and the
            // listed roles repeat around that number; a list LONGER than the
            // seats still gets every role out, up to CrewMax.
            if (crew != null && crew.Count > 0)
                u.CrewSize = Mathf.Min(Mathf.Max(u.CrewSize, crew.Count),
                                       Mathf.Max(1, RevivalPlugin.CfgPatrolCrewMax.Value));

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
        /// code itself. Capped by CrewMax, because six marauders climbing out
        /// of one BTR is not a fight, it is a verdict.
        /// </summary>
        static int Besatzung(Unit u)
        {
            if (!RevivalPlugin.CfgPatrolCrew.Value) return 0;
            Transform seats = GetField(u.Vgs, "SeatPoints") as Transform;
            if (seats == null) return 0;
            int n = 0;
            for (int i = 0; i < seats.childCount; i++)
                if (seats.GetChild(i).name != Turret.SeatName) n++;
            return Mathf.Clamp(n, 0, Mathf.Max(0, RevivalPlugin.CfgPatrolCrewMax.Value));
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

                if (!u.CrewOut && u.CrewSize > 0)
                {
                    u.CrewOut = true;
                    List<RevivalComposition.CrewMan> crew = u.CompositionVehicle < 0
                        ? null : RevivalComposition.CrewOf(u.Route.Name,
                                                          u.CompositionVehicle);
                    Crew.Aussteigen(u.Car, u.Vgs, u.CrewSize, u.Tank, u.Seite,
                                    crew);
                }

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

        /// <summary>Everything that has to be held every step, because the game
        /// keeps undoing it.</summary>
        static void Keep(Unit u)
        {
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
            if (u.Rcc == null) return;
            SetFloat(u.Rcc, "gasInput", 0f);
            SetFloat(u.Rcc, "brakeInput", 1f);
            SetFloat(u.Rcc, "steerInput", 0f);
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

            // --- where to aim ------------------------------------------------
            float look = Mathf.Clamp(vel.magnitude * 1.1f, 10f, 35f);
            Vector3 aim = LookAhead(r, u.Next, pos, look);

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
            }
            else
            {
                want = r.P[u.Next].Speed;
                if (want <= 0f) want = RevivalPlugin.CfgPatrolSpeed.Value;
                want *= CornerFactor(r, u.Next);
                want = Mathf.Max(want, 12f);
            }

            float steer = Mathf.Clamp(angle / FullLockAt, -1f, 1f);
            float gas, brake;
            Throttle(want, kmh, out gas, out brake);

            // --- what is in the way ------------------------------------------
            if (u.ConvoyId != 0)
            {
                // A convoy does not steer around anything: it ghosts straight
                // through props and its line-mates and drives its waypoints
                // bluntly (no Avoid). But ghosting covers only world props -
                // terrain and things it may not ghost can still wedge a vehicle,
                // and one stuck column member (very often the front, at the spawn
                // line) blocks the whole convoy. So, exactly like a patrol, a
                // convoy vehicle that has stopped moving is teleported a few
                // metres forward onto the next waypoint (Escalate/Free), which
                // also spreads out a column that spawned piled up on the start
                // line. Hold is handled before Drive, so a legitimately held
                // vehicle never reaches this.
                GhostAhead(u, t, vel.magnitude);
                if (groundKmh < 3f) u.Stuck += dt; else u.Stuck = 0f;
                if (Escalate(u, pos)) return;
            }
            else
            {
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
        static Vector3 LookAhead(Route r, int next, Vector3 pos, float dist)
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

            // Steer towards whichever side has more room. Both blocked and the
            // escalation takes over on its own, because we will stop moving.
            float freeLeft = left < 0f ? range : left;
            float freeRight = right < 0f ? range : right;
            float push = freeLeft > freeRight ? -0.6f : 0.6f;
            if (wide >= 0f) push *= Mathf.Clamp01(1f - wide / range) + 0.4f;
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
        /// </summary>
        static bool Lebendig(Transform t)
        {
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
            if (u.Ghosted.ContainsKey(id)) return;
            u.Ghosted[id] = true;                 // decided once, either way

            if (Lebendig(go.transform)) return;   // player, NPC, animal, vehicle: solid
            if (IsDriveSurface(go, normal)) return; // never ghost terrain or mesh roads
            if (!PhysLookUp()) return;

            Component[] cols = go.GetComponentsInChildren(_colliderType, true);
            GhostPair(u.Cols, cols);
            RevivalPlugin.L.LogInfo("Convoy " + u.ConvoyId + ": ghosting through \""
                + go.name + "\" on " + u.Route.Name + ".");
        }

        // =====================================================================
        //  Fail-fast recovery
        // =====================================================================

        /// <summary>A confirmed stop has one outcome: move forward along the
        /// route. There is deliberately no reverse or ramming stage. A blocked
        /// patrol is worse for the game than a vehicle passing through scenery.</summary>
        static bool Escalate(Unit u, Vector3 pos)
        {
            float stuckFor = Mathf.Max(0.1f, RevivalPlugin.CfgPatrolStuck.Value);
            if (u.Stuck < stuckFor) return false;
            Free(u, pos);
            return true;
        }

        /// <summary>Put the vehicle on the first waypoint at least five metres
        /// farther along the route, then face it down the following leg. Dense
        /// recordings may need several points to cover those five metres.</summary>
        static void Free(Unit u, Vector3 pos)
        {
            Route r = u.Route;
            int n = r.P.Count;
            int from = u.Next;
            int to = from;
            float advanced = 0f;
            int steps = 0;
            while (advanced < 5f && steps < n - 1)
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

            Vector3 target = Grounded(r.P[to].Pos, 1.5f);
            Vector3 ahead = RouteDirection(r, to, u.OneWay);

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

            RevivalPlugin.L.LogWarning("Patrol: FREE on " + r.Name + " - stuck at "
                + pos + " near waypoint " + from + ", moved "
                + advanced.ToString("0.0") + " m forward onto waypoint " + to
                + ". (" + u.Frees + " so far)");
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
        /// COST. A patrol with no player within GunRange does nothing at all
        /// beyond one square distance per player per half second, and that
        /// player list is fetched once for every vehicle on the road. The line
        /// of sight ray is cast for ONE candidate, not for all of them, and
        /// only twice a second. The turret is turned only while there is a
        /// target or the barrel is not yet back at rest.
        /// </summary>
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

                if (u.Target == null)
                {
                    Ruhen(u, dt);
                    return;
                }

                u.Held += dt;

                Vector3 ziel = Zielpunkt(u.Target);
                if (!Winkel(u, ziel, out u.Yaw, out u.Pitch)) return;
                Drehen(u, dt);

                if (u.Held < RevivalPlugin.CfgPatrolGunNotice.Value) return;
                if (Time.time < u.NextShot) return;
                if (Vector3.Angle(Rohrrichtung(u), ziel - Muendung(u)) > FireWithin) return;

                Nachladen(u);
                Schiessen(u, ziel);
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
            /// Pick a target, or keep the one we have. Cheap test first:
            /// distance, then the line of sight, and the ray is cast for one
            /// candidate only.
            /// </summary>
            static void Suchen(Unit u)
            {
                float range = RevivalPlugin.CfgPatrolGunRange.Value;
                Vector3 from = Muendung(u);

                // Keep the current target while it is alive, near and visible.
                if (u.Target != null)
                {
                    bool weg = Flat(u.Target.position - from) > range * 1.15f;
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
                    u.Held = 0f;
                    u.Lost = 0f;
                    u.Burst = 0;
                }

                Transform best = null;
                float bestDist = 0f;
                for (int i = 0; i < _players.Count; i++)
                {
                    Spieler s = _players[i];
                    if (s.Tr == null) continue;
                    float d = Flat(s.Tr.position - from);
                    if (d > range) continue;
                    if (!Lebt(s)) continue;
                    if (best != null && d >= bestDist) continue;
                    best = s.Tr;
                    bestDist = d;
                }
                if (best == null) return;
                if (!Sicht(u, best)) return;

                u.Target = best;
                u.Held = 0f;
                u.Lost = 0f;
                RevivalPlugin.L.LogInfo("Patrol gun: " + (u.Tank ? "tank" : "BTR")
                    + " on " + u.Route.Name + " has a target at "
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
                return (to - point).sqrMagnitude < 2.25f;
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
                for (int i = 0; i < u.Turrets.Length; i++)
                {
                    if (u.Turrets[i] == null) continue;
                    u.Turrets[i].localRotation =
                        Quaternion.RotateTowards(u.Turrets[i].localRotation, want, step);
                }

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

            static void Schiessen(Unit u, Vector3 ziel)
            {
                Vector3 from = Muendung(u);
                float dist = Vector3.Distance(from, ziel);
                Vector3 aim = ziel + Streuung(u, dist);

                Vector3 dir = aim - from;
                if (dir.sqrMagnitude < 0.0001f) return;
                dir.Normalize();

                VehicleShotSound.Play(from, u.Tank);
                Turret.Net.PublishShot(from, u.Tank);

                float range = Reichweite(u);
                Vector3 impact;
                GameObject struck = Strahl(u, from, dir, range, out impact);
                Vector3 ende = struck == null ? from + dir * range : impact;

                Spur(u, from + dir * 2f, ende);
                u.Shots++;
                if (struck == null) return;

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
                        RocketHook.Detonate(impact - dir * 0.15f, scha, rad, 3f);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogError("Patrol gun: impact without explosion - "
                            + ex.Message);
                    }
                }

                if (Schaden(u, struck, Schadenswert(u), impact, from))
                {
                    u.Hits++;
                    RevivalPlugin.L.LogInfo("Patrol gun: hit at "
                        + dist.ToString("0") + " m (" + u.Hits + " of " + u.Shots + ").");
                }
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
                float weit = Mathf.Max(1f, RevivalPlugin.CfgPatrolGunRange.Value);
                float nah = Mathf.Clamp(RevivalPlugin.CfgPatrolGunEffective.Value, 1f, weit);

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
                    if (u.Car == null || !go.transform.IsChildOf(u.Car.transform))
                    {
                        point = hit;
                        return go;
                    }
                    rest -= Vector3.Distance(start, hit) + 0.25f;
                    start = hit + dir * 0.25f;
                }
                return null;
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
                if (PanzerSchaden(shooter, struck)) return true;
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
                // Unified with the mounted turret in VehicleArmor.GunHit: the
                // BTR autocannon eats a tank OR another APC on the game's armour
                // path. A tank SHOOTER fired an explosive shell above, so GunHit
                // no-ops for it and returns false.
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
            if (!File.Exists(path))
            {
                RevivalPlugin.L.LogWarning("Patrol: " + path + " does not exist. "
                    + "No route until one is recorded.");
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
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
                        + r.Count + " patrol(s)"
                        + (r.Enabled ? "" : ", SWITCHED OFF") + ".");
                }
                if (bad > 0)
                    RevivalPlugin.L.LogWarning("Patrol: " + bad + " line(s) in "
                        + RevivalPlugin.CfgPatrolFile.Value + " were not readable.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Patrol: reading routes: " + ex); }
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
                        || one.StartsWith("count=") || one.StartsWith("kind=")) continue;
                    keep.Add(one);
                }
            }
            string f = Fraktion.Sauber(r.Fraction);
            if (f.Length > 0) keep.Add("fraction=" + f);
            string v = r.Vehicle == null ? "" : r.Vehicle.Trim().ToLowerInvariant();
            if (v == "btr" || v == "tank" || v == "mixed") keep.Add("vehicle=" + v);
            keep.Add("count=" + r.Count.ToString(CultureInfo.InvariantCulture));
            if (r.IsConvoy) keep.Add("kind=convoy");   // NDR convoy
            if (!r.Enabled) keep.Add("off");
            p.Flags = string.Join(",", keep.ToArray());
        }


        // =====================================================================
        //  Map overlay
        // =====================================================================

        /// <summary>
        /// Draws every recorded route as one faction-coloured dashed line along
        /// its waypoints. Editing and deletion stay in the existing F4 route
        /// editor, whose confirmation protects the file.
        /// </summary>
        public static void DrawMap()
        {
            if (RevivalPlugin.CfgPatrol == null || !RevivalPlugin.CfgPatrol.Value)
                return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            Component manager, texture;
            Camera camera;
            Vector2 world, map;
            if (!MapTools.Context(out manager, out texture, out camera,
                                  out world, out map)) return;
            Load(false);

            // The visible map rectangle. Dashes are HARD-clipped to it with
            // GUI.BeginClip so a route that runs off the shown map cannot paint
            // over the game scene around the panel - the earlier per-point test
            // let a stroke leak past the map edge into the grass. If the bounds
            // are unavailable, fall back to the whole screen rather than draw
            // nothing.
            Rect clip;
            if (!MapTools.MapScreenRect(texture, camera, out clip))
                clip = new Rect(0f, 0f, Screen.width, Screen.height);

            // The map texture SCROLLS inside a clipping NGUI UIPanel (a
            // UIScrollView). MapScreenRect is the WHOLE texture, which when the
            // map is panned reaches far beyond the visible window - so dashes on
            // the off-window part were painted over the surrounding UI (the "it
            // lies over the whole UI when I scroll" bug). Clip to the panel's
            // actual viewport as well, so nothing outside the map window draws.
            Rect view;
            if (MapTools.MapViewportRect(texture, camera, out view))
                clip = Intersect(clip, view);

            Color old = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            try
            {
                // BeginClip scissors every following draw to the map and moves
                // the origin to the map's top-left, so all dash coordinates are
                // LOCAL to the clip. Projected points are offset by -clip.pos to
                // match, and the dash routines cull against the local rect.
                Rect localClip = new Rect(0f, 0f, clip.width, clip.height);

                // Hovering the enclosed area of a route pops a note about the
                // patrols it carries. The ring is tested in clip-LOCAL space, so
                // the absolute cursor is shifted by -clip.position to match; the
                // box itself is drawn after EndClip in absolute coordinates so it
                // can sit over the map edge and is never scissored.
                Vector2 mouseAbs = Event.current.mousePosition;
                Vector2 mouseLocal = mouseAbs - clip.position;
                string hoverText = null;
                Vector2 hoverAt = Vector2.zero;
                Color hoverColor = Color.white;

                GUI.BeginClip(clip);
                try
                {
                    // Every dash point drawn so far, so a later route's ring is
                    // trimmed where it crosses one drawn earlier. Routes are
                    // drawn in file order; the earlier ring keeps its line.
                    ClearGrid grid = new ClearGrid(RouteClearance);

                    for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
                    {
                        Route route;
                        if (!_routes.TryGetValue(_order[routeIndex], out route)
                            || route == null || route.P.Count < 2) continue;

                        // ENCIRCLE the run with ONE smooth closed boundary. The
                        // ring is built ONCE in world space (WorldRing, cached on
                        // the route) and only PROJECTED here, so it no longer
                        // jitters as the map/camera micro-moves - the convex hull
                        // lives in a fixed world frame, not per frame in screen
                        // space. Project every ring point; if any falls behind
                        // the UI camera the ring is skipped this frame rather
                        // than drawn broken.
                        List<Vector3> wring = WorldRing(route);
                        if (wring == null || wring.Count < 3) continue;

                        // SCENE GATE. The map shows the CURRENT scene, and its
                        // WORLD_SIZE is that scene's terrain size. A route lives
                        // on one terrain (the overworld), so on any smaller map -
                        // a bunker or other interior - its coordinates fall many
                        // terrain-widths outside and the ring smears across the
                        // wrong map. Draw the route only where it can fit.
                        if (!FitsScene(wring, world)) continue;

                        List<Vector2> ring = new List<Vector2>(wring.Count);
                        bool ringOk = true;
                        for (int i = 0; i < wring.Count; i++)
                        {
                            Vector2 g;
                            if (!MapTools.WorldToGui(wring[i], texture, camera,
                                                     world, map, out g))
                            { ringOk = false; break; }
                            ring.Add(g - clip.position);
                        }
                        if (!ringOk || ring.Count < 3) continue;

                        // Colour is the patrol's faction: looter and traitor
                        // red, civilian green, neutral white. A convoy route is
                        // amber, to read apart from the patrol areas. NDR convoy.
                        Color col = route.IsConvoy ? ConvoyColor(route.Enabled)
                                                   : RouteColor(route.Seite, route.Enabled);
                        GUI.color = col;

                        // This ring's own dash points, added to the grid only
                        // after it is fully drawn so it never clears itself.
                        List<Vector2> ink = new List<Vector2>();
                        DashClosed(ring, localClip, grid, ink);
                        grid.Add(ink);

                        // First ring under the cursor wins the note.
                        if (hoverText == null && PointInPolygon(ring, mouseLocal))
                        {
                            hoverText = route.IsConvoy
                                ? Loc.T(
                                    "Маршрут военного конвоя: 2 танка и 2 БТР с "
                                    + "ценным грузом. Появляется время от времени - "
                                    + "следите за оповещением о квадрате.",
                                    "Military convoy route: 2 tanks and 2 APCs "
                                    + "carrying valuable cargo. Appears from time to "
                                    + "time - watch for the square alert.")
                                : Loc.T(
                                    "Здесь регулярно проходят патрули. Возможно, "
                                    + "они везут ценный груз, но они опасны, хорошо "
                                    + "вооружены и имеют FPV-дрон.",
                                    "Regular patrols pass through here. They may be "
                                    + "carrying valuable cargo, but they are dangerous, "
                                    + "heavily armed, and have an FPV drone.");
                            hoverAt = mouseAbs;
                            hoverColor = col;
                        }
                    }
                }
                finally { GUI.EndClip(); }

                // Labels belong above the lines, including lines from routes
                // later in the file.
                for (int routeIndex = 0; routeIndex < _order.Count; routeIndex++)
                {
                    Route route;
                    if (!_routes.TryGetValue(_order[routeIndex], out route)
                        || route == null || route.P.Count < 1) continue;
                    // Same scene gate as the rings: a label for a route that
                    // belongs to another scene must not sit on this map.
                    if (route.P.Count >= 2 && !FitsScene(WorldRing(route), world))
                        continue;
                    Vector2 label;
                    if (!MapTools.WorldToGui(route.P[0].Pos, texture, camera,
                                             world, map, out label)
                        || !clip.Contains(label)) continue;
                    GUI.color = RouteColor(route.Seite, route.Enabled);
                    GUI.Label(new Rect(label.x + 7f, label.y - 12f, 230f, 22f),
                              route.Name + (route.Enabled ? "" : Loc.T(" (выкл)", " (disabled)")));
                }

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
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = old;
            }
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
        // lengths walked along the enclosing RING's arc length; stroke is the
        // line thickness. Each dash is a short chain of ANTIALIASED feathered
        // bars (see <see cref="Bar"/>) that FOLLOWS the ring's arc, so the dash
        // itself curves smoothly with the boundary; the long SIDES are feathered
        // (smooth, not pixelated) and constant in thickness, while the two ENDS
        // stay hard and FLAT (kantig) - no round caps. RouteDash/RouteGap are
        // nominal: the ring is tiled with a whole number of them so the gaps are
        // even the whole way round with no seam (see <see cref="DashClosed"/>).
        const float RouteDash = 40f;
        const float RouteGap = 28f;
        const float RouteStroke = 4.5f;

        // The length of each straight bar inside a curved dash, and the small
        // overlap that keeps consecutive bars meeting without a notch on the
        // outside of a bend. Shorter step -> smoother curve. A dash FOLLOWS the
        // ring's arc, so its ends are tangent to the ring and each dash points
        // at the next - the eye draws one continuous line through them - and a
        // dash only curves where the ring actually bends; on a straight run it
        // stays straight.
        const float RouteCurveStep = 5f;
        const float RouteSegOverlap = 1.2f;

        // The enclosing ring is resampled to this even spacing before dashing,
        // and corner-cut this many times, so the hull reads as a smooth loop
        // rather than a polygon and the bars have a clean arc to follow.
        const float RouteResample = 6f;
        const int RouteRingSmooth = 4;

        // World-space spacing (metres) the cached ring is resampled to before it
        // is projected each frame. Dense enough that the projected polygon reads
        // as a smooth loop at the fixed map scale; built once, so the count is
        // cheap.
        const float RouteResampleWorld = 4f;

        // Half-width, in metres, of the ring around a route when the config is
        // unavailable, and the pixel range the metre padding is clamped to so a
        // near-collinear route still gets a visible ring and a sprawling one
        // does not balloon off the map.
        const float RoutePadMetres = 45f;
        const float RoutePadMinPx = 22f;
        const float RoutePadMaxPx = 140f;

        // A route ring that crosses one drawn earlier is trimmed back within
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

        /// <summary>The measured screen-pixels-per-metre of the map, from the
        /// total projected pixel length of the run over its total world (XZ)
        /// length. The map does not zoom, so one number holds for the whole
        /// overlay; averaging the whole run shrugs off any single bad pair. A
        /// safe fallback is returned when the run has no measurable length.
        /// </summary>
        static float PixelsPerMetre(List<Vector2> proj, List<Vector3> wpos)
        {
            double pix = 0.0, met = 0.0;
            int n = Mathf.Min(proj.Count, wpos.Count);
            for (int i = 1; i < n; i++)
            {
                pix += (proj[i] - proj[i - 1]).magnitude;
                float dx = wpos[i].x - wpos[i - 1].x;
                float dz = wpos[i].z - wpos[i - 1].z;
                met += Mathf.Sqrt(dx * dx + dz * dz);
            }
            if (met < 1e-3) return 0.35f;
            return (float)(pix / met);
        }

        /// <summary>The outward padding of the ring in PIXELS: the configured
        /// half-width in metres (falling back to a default) turned into pixels
        /// by the measured scale, then clamped so a near-straight route still
        /// gets a visible ring and a sprawling one does not balloon.</summary>
        static float RouteMapPad(float ppm)
        {
            float metres = RoutePadMetres;
            if (RevivalPlugin.CfgPatrolRouteMapWidth != null)
                metres = RevivalPlugin.CfgPatrolRouteMapWidth.Value;
            return Mathf.Clamp(metres * ppm, RoutePadMinPx, RoutePadMaxPx);
        }

        /// <summary>
        /// Builds ONE smooth closed boundary around a projected run: the convex
        /// hull of the waypoints (a rectangle capsule when they are collinear),
        /// pushed outward by <paramref name="pad"/>, corner-cut into a rounded
        /// loop and resampled to an even spacing. The returned list is CLOSED
        /// (its last point repeats the first) so the dasher can walk it as a
        /// ring. Coordinates are LOCAL to the map clip.
        /// </summary>
        static List<Vector2> EncircleRun(List<Vector2> pts, float pad)
        {
            List<Vector2> hull = ConvexHull(pts);
            List<Vector2> ring = hull.Count < 3 ? CapsuleRing(pts, pad)
                                                : ExpandHull(hull, pad);
            if (ring == null || ring.Count < 3) return null;
            ring = ChaikinClosed(ring, RouteRingSmooth);
            ring.Add(ring[0]);                       // close the loop
            ring = Resample(ring, RouteResample);
            return ring;
        }

        /// <summary>
        /// The route's encircling ring in WORLD space (XZ; the stored y is 0 and
        /// WorldToGui ignores it). Built once - convex hull of the waypoints,
        /// pushed outward by the configured half-width in METRES, corner-cut and
        /// resampled - and cached on the route, rebuilt only when the waypoint
        /// count or the padding changes. DrawMap projects this each frame; doing
        /// the hull in one fixed world frame instead of per frame in screen space
        /// is what stops the ring jittering as the map or camera micro-moves.
        /// </summary>
        static List<Vector3> WorldRing(Route r)
        {
            float pad = RoutePadMetres;
            if (RevivalPlugin.CfgPatrolRouteMapWidth != null)
                pad = RevivalPlugin.CfgPatrolRouteMapWidth.Value;
            if (r.MapRing != null && r.MapRingN == r.P.Count
                && Mathf.Abs(r.MapRingPad - pad) < 0.01f)
                return r.MapRing;

            r.MapRingN = r.P.Count;
            r.MapRingPad = pad;
            r.MapRing = null;
            if (r.P.Count < 2) return null;

            List<Vector2> xz = new List<Vector2>(r.P.Count);
            for (int i = 0; i < r.P.Count; i++)
                xz.Add(new Vector2(r.P[i].Pos.x, r.P[i].Pos.z));

            List<Vector2> hull = ConvexHull(xz);
            List<Vector2> ring = hull.Count < 3 ? CapsuleRing(xz, pad)
                                                : ExpandHull(hull, pad);
            if (ring == null || ring.Count < 3) return null;
            ring = ChaikinClosed(ring, RouteRingSmooth);
            ring.Add(ring[0]);                       // close the loop
            ring = Resample(ring, RouteResampleWorld);

            List<Vector3> worldRing = new List<Vector3>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
                worldRing.Add(new Vector3(ring[i].x, 0f, ring[i].y));
            r.MapRing = worldRing;
            return worldRing;
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

        /// <summary>Andrew's monotone-chain convex hull. Returns the hull
        /// vertices in order without the closing repeat; fewer than three means
        /// the input was collinear.</summary>
        static List<Vector2> ConvexHull(List<Vector2> points)
        {
            int n = points.Count;
            if (n < 3) return new List<Vector2>(points);
            List<Vector2> pts = new List<Vector2>(points);
            pts.Sort(CompareVec);
            Vector2[] h = new Vector2[2 * n];
            int k = 0;
            for (int i = 0; i < n; i++)                 // lower hull
            {
                while (k >= 2 && Cross(h[k - 2], h[k - 1], pts[i]) <= 0f) k--;
                h[k++] = pts[i];
            }
            for (int i = n - 2, t = k + 1; i >= 0; i--) // upper hull
            {
                while (k >= t && Cross(h[k - 2], h[k - 1], pts[i]) <= 0f) k--;
                h[k++] = pts[i];
            }
            List<Vector2> res = new List<Vector2>(k - 1);
            for (int i = 0; i < k - 1; i++) res.Add(h[i]);
            return res;
        }

        static int CompareVec(Vector2 a, Vector2 b)
        {
            if (a.x < b.x) return -1;
            if (a.x > b.x) return 1;
            if (a.y < b.y) return -1;
            if (a.y > b.y) return 1;
            return 0;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        /// <summary>Pushes every hull vertex outward along the average of its
        /// two edge normals by <paramref name="pad"/> pixels, expanding the
        /// convex polygon. The centroid disambiguates which way is out, so the
        /// result never collapses inward. Chaikin later rounds the corners.
        /// </summary>
        static List<Vector2> ExpandHull(List<Vector2> hull, float pad)
        {
            int n = hull.Count;
            Vector2 c = Vector2.zero;
            for (int i = 0; i < n; i++) c += hull[i];
            c /= n;
            List<Vector2> outp = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = hull[(i - 1 + n) % n];
                Vector2 cur = hull[i];
                Vector2 next = hull[(i + 1) % n];
                Vector2 n0 = Outward(new Vector2((cur - prev).y, -(cur - prev).x), cur, c);
                Vector2 n1 = Outward(new Vector2((next - cur).y, -(next - cur).x), cur, c);
                Vector2 nrm = n0 + n1;
                if (nrm.sqrMagnitude < 1e-6f) nrm = cur - c;
                if (nrm.sqrMagnitude < 1e-6f) nrm = new Vector2(0f, -1f);
                nrm.Normalize();
                outp.Add(cur + nrm * pad);
            }
            return outp;
        }

        static Vector2 Outward(Vector2 nrm, Vector2 at, Vector2 centre)
        {
            if (nrm.sqrMagnitude < 1e-9f) return nrm;
            nrm.Normalize();
            return Vector2.Dot(nrm, at - centre) < 0f ? -nrm : nrm;
        }

        /// <summary>A four-corner rectangle around a collinear run: the two end
        /// waypoints extended by <paramref name="pad"/> and offset to both
        /// sides by the same, so a dead-straight route still encloses an area.
        /// </summary>
        static List<Vector2> CapsuleRing(List<Vector2> pts, float pad)
        {
            Vector2 a = pts[0], b = pts[pts.Count - 1];
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1f) { b = a + new Vector2(1f, 0f); d = b - a; }
            Vector2 dir = d.normalized;
            Vector2 nrm = new Vector2(-dir.y, dir.x);
            List<Vector2> r = new List<Vector2>(4);
            r.Add(a - dir * pad + nrm * pad);
            r.Add(b + dir * pad + nrm * pad);
            r.Add(b + dir * pad - nrm * pad);
            r.Add(a - dir * pad - nrm * pad);
            return r;
        }

        /// <summary>Chaikin corner-cutting over a CLOSED polygon: every edge,
        /// including the wrap from last vertex to first, is cut a quarter in
        /// from each end. No endpoint is special, so the whole loop rounds
        /// evenly with no seam.</summary>
        static List<Vector2> ChaikinClosed(List<Vector2> pts, int iters)
        {
            List<Vector2> cur = new List<Vector2>(pts);
            for (int it = 0; it < iters; it++)
            {
                int n = cur.Count;
                if (n < 3) break;
                List<Vector2> next = new List<Vector2>(n * 2);
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = cur[i], b = cur[(i + 1) % n];
                    next.Add(a * 0.75f + b * 0.25f);
                    next.Add(a * 0.25f + b * 0.75f);
                }
                cur = next;
            }
            return cur;
        }

        /// <summary>Walks the closed ring's arc length and lays down evenly
        /// spaced curved dashes. The whole loop is tiled with a WHOLE number of
        /// dash+gap periods, so the gap is even the whole way round and the seam
        /// where the loop closes carries a proper gap too, not a doubled-up dash
        /// (the "gap not kept at the top-left" the user saw). Each dash curves
        /// gently along the boundary (see <see cref="DrawCurvedDash"/>). Dashes
        /// within an earlier route's clearance are dropped, and the survivors
        /// feed <paramref name="ink"/> for later routes. Coordinates are LOCAL
        /// to the map clip.</summary>
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
            int count = Mathf.Max(1, Mathf.RoundToInt(total / period));
            float step = total / count;                   // even, seam-free
            float dash = Mathf.Min(RouteDash, step - 4f);  // keep a real gap
            if (dash < 2f) dash = step;
            for (int k = 0; k < count; k++)
            {
                float start = k * step;
                DrawCurvedDash(pts, cum, start, start + dash, clip, grid, ink);
            }
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

        /// <summary>Even-odd ray cast: is the point inside the polygon? The ring
        /// may carry a closing repeat of its first vertex; the degenerate edge
        /// that makes is harmless here.</summary>
        static bool PointInPolygon(List<Vector2> poly, Vector2 p)
        {
            int n = poly.Count;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
                    inside = !inside;
            }
            return inside;
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
                int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
                GUILayout.Label(Loc.T("На дороге: ", "On the road: ") + _units.Count
                    + Loc.T(" из ", " of ") + max
                    + Loc.T(" (MaxVehicles). Автоматика: ", " (MaxVehicles). Automatic: ")
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
                    + Loc.T(" в рейсе", " out"),
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
                int max = Mathf.Max(1, RevivalPlugin.CfgPatrolMax.Value);
                if (_units.Count >= max)
                {
                    Melde(Loc.T("MaxVehicles = " + max + ", в рейсе " + _units.Count
                          + " - сначала уберите с дороги",
                            "MaxVehicles is " + max + " and " + _units.Count
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
    ///     traitor   Traitor   hates all EIGHT, itself included
    ///     neutral   Neutral   hates Traitor only
    ///
    /// The hated arrays are written out in full rather than taken from the
    /// game's own table, because the user's rule and the game's table differ
    /// in one place that matters: the game's Peace tolerates Hermits and
    /// Wildmen, and "attacks everyone but civilians" does not.
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
                case "traitor": return Loc.T("бьёт ВСЕХ, включая traitor", "attacks EVERYONE, traitors included");
                default: return Loc.T("бьёт только traitor", "attacks traitors only");
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
        static string Eigene(string name)
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
                                          Wildman, Military, Traitor,
                                          MilitaryNeutral };
                default:
                    return new string[] { Traitor };
            }
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

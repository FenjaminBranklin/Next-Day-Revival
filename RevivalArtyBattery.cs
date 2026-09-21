// Next Day: Survival - Revival Toolkit
//
// The settlement artillery battery: the crew that stands around the gun, the
// recon drone their operator keeps in the air, and the fire missions the drone
// buys them. The gun itself - loading, the map fire control, the flight of a
// shell and the impact - stays in RevivalMortar.cs; this file is everything
// AROUND it. Design notes: docs/ai/tasks/arty-vehicle-drone.md.
//
// WHAT A PLAYER MEETS, IN ORDER
//
//   1. Every settlement has a self-propelled howitzer instead of the old M1943
//      tube (ArtyModel below builds it; Mortar.Raise stands it up).
//   2. TWO MEN belong to it: a gunner at the sight and a drone operator. They
//      are ordinary game NPCs, spawned once by the master client through
//      Crew.DropSquad and wearing the FACTION OF THE SETTLEMENT they stand in,
//      so they do not open fire on their own village. They are POSTED at the
//      vehicle and do not walk anywhere: the gunner works the target computer
//      on the side of the hull, the operator squats beside him flying the
//      drone (Posted, further down).
//   3. The operator flies a real recon drone in a wide circle around the
//      settlement. It is the same airframe the player's own surveillance drone
//      uses, and it is drawn on the M map.
//   4. What the drone flies over, it sees. A hostile man under it is reported
//      to the gunner - not instantly: a sighting takes seconds to travel, the
//      gun has to be laid, and only then does the salvo leave. That delay is
//      the whole point; a walking target is somewhere else by then.
//   5. The salvo does NOT land on the spot. The whole mission carries one
//      random aim error, and every shell inside it keeps the gun's ordinary
//      dispersion - so the ground around the reported point is beaten, which is
//      what artillery does.
//   6. Kill the crew and it all stops. The operator's death takes the drone out
//      of the sky, the gunner's death silences the gun - and only then can the
//      player use the sight himself.
//
// WHO COMPUTES WHAT
//
//   MASTER CLIENT   spawns the crew, runs the spotting, decides every fire
//                   mission and applies the impact to the NPCs and vehicles it
//                   owns. There is exactly one master, so no mission is ever
//                   run twice.
//   EVERY CLIENT    flies the drone MODEL from a shared clock (PhotonNetwork.time
//                   plus a phase derived from the settlement's own position), so
//                   all clients draw the same drone in the same place without a
//                   single byte of traffic. Each client also decides for ITSELF
//                   whether it is standing under the drone, and applies an
//                   incoming shell to its OWN player only. No client ever
//                   damages another client's player, so nobody can be credited
//                   with a casualty he did not cause.
//
// C# 3.0. This file is ASCII ONLY - it is written by tooling that cannot
// guarantee a BOM-less UTF-8 file, and build.ps1 requires BOM-less sources. The
// two bilingual lines this feature shows the player therefore live next to the
// other Russian strings in RevivalMortar.cs (Mortar.TextCrewAtGun,
// Mortar.TextSpotted) and are only called from here.
//
// SEAMS OUTSIDE THIS FILE (all marked "NDR settlement artillery"):
//   RevivalPlugin.cs BindConfig -> ArtyBattery.BindConfig(Config)
//   RevivalPlugin.cs Update     -> ArtyBattery.Tick()
//   RevivalPlugin.cs OnGUI      -> ArtyBattery.Draw()
//   RevivalMortar.cs Raise/Place -> ArtyBattery.GunRaised / GunLost
//   RevivalMortar.cs Place       -> ArtyBattery.GunExpected (the drone of a
//                                  settlement whose gun is still to come)
//   RevivalMortar.cs Ground      -> ArtyBattery.CrewHoldsGun

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>
    /// The crew, the recon drone and the automatic fire missions of every
    /// settlement gun. The gun itself is <see cref="Mortar"/>; the vehicle's
    /// geometry is <see cref="ArtyModel"/>.
    /// </summary>
    public static class ArtyBattery
    {
        // ------------------------------------------------------------- config

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<bool> _cfgCrew;
        static ConfigEntry<bool> _cfgCrewHolds;
        static ConfigEntry<bool> _cfgDrone;
        static ConfigEntry<bool> _cfgAutoFire;
        static ConfigEntry<string> _cfgFaction;
        static ConfigEntry<int> _cfgCrewWeapon;
        static ConfigEntry<bool> _cfgCrewPosted;
        static ConfigEntry<int> _cfgWorkState;
        static ConfigEntry<string> _cfgWorkClip;
        static ConfigEntry<string> _cfgGunnerPost;
        static ConfigEntry<string> _cfgOperatorPost;
        static ConfigEntry<int> _cfgOperatorPose;
        static ConfigEntry<string> _cfgOperatorClip;

        static ConfigEntry<float> _cfgOrbitRadius;
        static ConfigEntry<float> _cfgOrbitHeight;
        static ConfigEntry<float> _cfgOrbitSpeed;
        static ConfigEntry<float> _cfgOrbitSweep;
        static ConfigEntry<float> _cfgSweepLaps;
        static ConfigEntry<float> _cfgModelRange;
        static ConfigEntry<float> _cfgModelScale;

        static ConfigEntry<float> _cfgSpotRadius;
        static ConfigEntry<float> _cfgSpotSeconds;
        static ConfigEntry<float> _cfgReportDelay;
        static ConfigEntry<float> _cfgReportJitter;
        static ConfigEntry<float> _cfgAimError;
        static ConfigEntry<float> _cfgCooldown;
        static ConfigEntry<float> _cfgGuardRadius;
        static ConfigEntry<int> _cfgMagazine;
        static ConfigEntry<float> _cfgResupply;

        static bool Enabled { get { return _cfgEnabled == null || _cfgEnabled.Value; } }
        internal static bool Shootable { get { return Enabled && B(_cfgDrone, true); } }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }
        static bool B(ConfigEntry<bool> c, bool fallback) { return c == null ? fallback : c.Value; }

        /// <summary>Metres around the gun in which a living man counts as its
        /// crew. Since 6.26 the two men are posted at the hull (Posted) and
        /// stand 8 and 14 units off its centre, but this stays wide: the count
        /// is also what a JOINED client goes by, and a settlement's own men
        /// standing at the vehicle are crew enough for it.</summary>
        internal static float GuardRadius { get { return Mathf.Max(4f, F(_cfgGuardRadius, 14f)); } }

        /// <summary>The weapon both crewmen carry. Anything above zero keeps
        /// Crew.DropSquad out of its LAW branch; Crew.UsableWeapon still falls
        /// back to a weapon that has a model if this id has none.</summary>
        static int CrewWeapon
        {
            get { return _cfgCrewWeapon == null ? 1001 : Mathf.Max(1, _cfgCrewWeapon.Value); }
        }

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Artillery", "Enabled", true,
                "The settlement artillery battery: a gunner and a drone operator "
                + "at every gun, a recon drone in the air and the fire missions "
                + "it buys. false leaves the bare gun for the player.");
            _cfgCrew = cfg.Bind("Artillery", "SpawnCrew", true,
                "Spawn the two men (gunner, drone operator) next to every gun. "
                + "They are spawned by the master client only and take the "
                + "faction of the settlement they stand in.");
            _cfgCrewHolds = cfg.Bind("Artillery", "CrewHoldsTheGun", true,
                "While a man who is hostile to you is alive at the gun, the sight "
                + "is his: [F] is refused. Kill the crew and the gun is yours. "
                + "false lets you push him aside and aim over his shoulder.");
            _cfgDrone = cfg.Bind("Artillery", "Drone", true,
                "The operator keeps a recon drone circling the settlement.");
            _cfgAutoFire = cfg.Bind("Artillery", "AutoFire", true,
                "The gunner answers what the drone reports. false keeps the drone "
                + "in the air as a pure warning - useful for testing.");
            _cfgFaction = cfg.Bind("Artillery", "CrewFaction", "looter",
                "Fallback side of the crew when the settlement's own faction "
                + "cannot be read: civilian, looter, traitor or neutral.");
            _cfgCrewWeapon = cfg.Bind("Artillery", "CrewWeapon", 1001,
                "Waffe der beiden Kanoniere. Ohne einen Eintrag hier greift "
                + "Patrol/CrewLawCount, und das gibt dem ersten Mann jeder "
                + "Gruppe einen M72 LAW - an einer Haubitze nicht erwuenscht. "
                + "1001 ist das Sturmgewehr des Spiels; 1160 waere das MG42.");
            _cfgCrewPosted = cfg.Bind("Artillery", "CrewStandsAtTheGun", true,
                "The gunner works the target computer on the side of the "
                + "vehicle and the drone operator squats beside him. "
                + "Neither of them walks anywhere: they are posted, not "
                + "patrolling. false gives back the crew that wandered the "
                + "nine-metre ring Crew.DropSquad lays out for every squad.");
            _cfgWorkState = cfg.Bind("Artillery", "CrewWorkState", 10,
                "The NPCMainState the GUNNER is held in. 10 is the game's "
                + "own Working state - what an NPC busy with something in front "
                + "of him is in; 0 is a plain standing idle. A state the "
                + "installed game does not animate simply leaves the men "
                + "standing, which is still motionless.");
            _cfgWorkClip = cfg.Bind("Artillery", "CrewWorkClip", "",
                "The animation clip the GUNNER loops when the state above plays "
                + "none. Empty picks the first clip of the model's own set "
                + "whose name reads like work AT CHEST HEIGHT - a man at a "
                + "console, not one kneeling in a berry bush - and writes the "
                + "choice, and every clip the model carries, to the log. \"-\" "
                + "asks for no clip at all.");
            // THE TWO STATIONS ARE DATA, NOT CODE (field 2026-09-19: "auf
            // crewrightside siehst du die ANDERE seite des fahrzeugs, dort ist
            // bereits der zusehende target computer angebracht, da soll der
            // gunner bis ganz kurz vorm fahrzeug stehen"). Where exactly that
            // box sits on the model is something only a player standing at the
            // vehicle can see, so the two posts are config: a man can be moved
            // onto the console to the centimetre without a new build.
            _cfgGunnerPost = cfg.Bind("Artillery", "CrewGunnerPost", "8,0,-8",
                "Where the gunner stands, in the VEHICLE's own space: x,y,z, "
                + "+z is the way the hull points and +x is its right side - the "
                + "side the target computer is on. 8 is right at the hull (the "
                + "widest point of the model, the deployed stabilizers, is 7.3 "
                + "out), so he works the box instead of standing off it. The "
                + "man always faces the hull, whichever side he is put on.");
            _cfgOperatorPost = cfg.Bind("Artillery", "CrewOperatorPost", "8,0,-12",
                "Where the drone operator crouches, same frame as above: beside "
                + "the gunner on the same side of the vehicle, four units (a "
                + "pace and a half) further back.");
            _cfgOperatorPose = cfg.Bind("Artillery", "CrewOperatorPose", 1,
                "The pose the drone operator is held in. 1 is the game's own "
                + "Crouch - he squats over his controller and does not move "
                + "(NPCPoseState: Normal 0, Crouch 1, Crawl 2; the crouch clips "
                + "are the game's own, CONFIRMED IL). 0 leaves him standing.");
            _cfgOperatorClip = cfg.Bind("Artillery", "CrewOperatorClip", "-",
                "An animation clip looped over the operator's crouch. \"-\" "
                + "keeps the game's own crouch idle, which is what the pose "
                + "above already plays; a name here replaces it. Empty searches "
                + "the model's set the way CrewWorkClip does - only useful when "
                + "the crouch pose does not hold on this build, because a "
                + "standing work clip stands the man back up.");

            _cfgOrbitRadius = cfg.Bind("Artillery", "OrbitRadius", 600f,
                "Metres from the settlement centre the drone circles at - the "
                + "OUTER edge of its search, because OrbitSweep spirals it in "
                + "from here and back out again. The band is the battery's "
                + "warning line, not its reach: the gun itself carries to "
                + "Mortar/MaxRange (1200 m), so a sighting at the far edge of "
                + "the camera's circle is still a mission.");
            // Migrate BOTH released defaults; retain deliberate custom radii.
            // The order of 2026-09-18 was "der drohnen radius soll weiter
            // erweitert werden, 600m oder noch mehr" - and a default change
            // alone reaches nobody who already has a config file, because
            // Config.Bind takes the value out of it (CLAUDE.md, point 4).
            if (_cfgOrbitRadius.Value == 240f || _cfgOrbitRadius.Value == 300f)
                _cfgOrbitRadius.Value = 600f;
            _cfgOrbitHeight = cfg.Bind("Artillery", "OrbitHeight", 85f,
                "Metres above the ground under it. High enough to be a dot, low "
                + "enough to be seen against the sky.");
            _cfgOrbitSpeed = cfg.Bind("Artillery", "OrbitSpeed", 16f,
                "Metres per second along the circle. At 600 m radius one lap "
                + "takes about 236 s. The speed is left alone by the wider "
                + "orbit on purpose: how long a man stays inside the camera's "
                + "circle - 600 m across it at the default SpotRadius, some "
                + "37 s - is what decides a sighting, and that depends on this "
                + "number, not on the radius. With OrbitSweep on, it is the "
                + "speed at the nominal radius: the angle turned per second is "
                + "what stays fixed, so the drone runs slower the further in "
                + "the spiral carries it.");
            _cfgOrbitSweep = cfg.Bind("Artillery", "OrbitSweep", 0.55f,
                "How far INSIDE OrbitRadius the drone spirals, as a fraction "
                + "of it. 0 is the old fixed ring, which is why a man was only "
                + "ever seen when he stood under one particular circle: a ring "
                + "searches the ring and nothing else. 0.55 at a 600 m orbit "
                + "works the band from 270 m out to 600 m, and with SpotRadius "
                + "on top of it the search covers the settlement itself, its "
                + "approaches and the ground the gun can reach beyond them.");
            _cfgSweepLaps = cfg.Bind("Artillery", "OrbitSweepLaps", 1.618f,
                "Laps of the circle per in-and-out of the spiral - about six "
                + "and a half minutes at the default orbit and speed. NOT a "
                + "whole number, and that is the whole point: at 2 the spiral "
                + "reaches its inner edge at a = 2*pi*k, which is the same "
                + "bearing every single time, and the settlement keeps "
                + "permanent blind sectors between those passes. The golden "
                + "ratio never lands twice on the same bearing and spreads the "
                + "inner passes evenly round the centre (measured in "
                + "research/arty_drone_orbit_check.py: 60 of 60 distinct, "
                + "against 1 of 60 at OrbitSweepLaps 2).");
            _cfgModelRange = cfg.Bind("Artillery", "ModelRange", 1000f,
                "Metres from the player at which the drone gets a visible model. "
                + "Beyond it the orbit is still computed - only the GameObject is "
                + "not built, so a map full of settlements costs nothing. It has "
                + "to clear the orbit by the width of a settlement, or the drone "
                + "vanishes for part of every lap for a player standing at his "
                + "own gun: at a 600 m ring the far side is 600 m from the "
                + "centre and 800 from a man 200 m off it.");
            // The same migration, and for the same reason: 800 was the released
            // default and belonged to a 240-300 m ring.
            if (_cfgModelRange.Value == 800f) _cfgModelRange.Value = 1000f;
            _cfgModelScale = cfg.Bind("Artillery", "ModelScale", 10f,
                "Size of the recon drone model. The player's own surveillance "
                + "drone uses 12.");

            _cfgSpotRadius = cfg.Bind("Artillery", "SpotRadius", 300f,
                "Metres around the point under the drone in which it sees a "
                + "man. A gimbal camera is not a hole in the floor: from "
                + "OrbitHeight, 300 m of ground is a 16 degree depression, "
                + "which is what a recon drone actually searches. The old 110 m "
                + "was a footprint, and a footprint is why the battery saw "
                + "nobody but the man standing directly underneath.");
            // FIELD 2026-09-20: "sie soll agressiver scannen nicht nur wenn man
            // direkt unter ihr ist". Migrate the released default - Config.Bind
            // takes the value out of an existing file, so a new default on its
            // own reaches nobody who has already played (CLAUDE.md, point 4).
            if (_cfgSpotRadius.Value == 110f) _cfgSpotRadius.Value = 300f;
            _cfgSpotSeconds = cfg.Bind("Artillery", "SpotSeconds", 3f,
                "Seconds a man has to stay inside the footprint before the "
                + "operator is sure of him. Running through the edge of a pass "
                + "is not a sighting.");
            _cfgReportDelay = cfg.Bind("Artillery", "ReportSeconds", 9f,
                "Seconds between the sighting and the gun being laid on it: the "
                + "operator reads off the grid, the gunner writes it down and "
                + "turns the turret. The turret's own travel is on top of this.");
            _cfgReportJitter = cfg.Bind("Artillery", "ReportJitterSeconds", 5f,
                "Random extra seconds on top of ReportSeconds, so a battery is "
                + "never a metronome.");
            _cfgAimError = cfg.Bind("Artillery", "AimErrorMetres", 28f,
                "How far the WHOLE mission may sit off the reported point. Every "
                + "shell then keeps the gun's own dispersion inside that - which "
                + "is why a salvo beats the ground around a man instead of "
                + "landing on his head.");
            _cfgCooldown = cfg.Bind("Artillery", "MissionCooldownSeconds", 40f,
                "Seconds after a mission before the same battery fires again.");
            _cfgGuardRadius = cfg.Bind("Artillery", "CrewRadius", 14f,
                "Metres around the gun in which a living man counts as its crew.");
            _cfgMagazine = cfg.Bind("Artillery", "CrewRounds", 14,
                "Shells the crew has to itself. The player's own loaded rounds "
                + "are a separate count and are never fired by the NPC.");
            _cfgResupply = cfg.Bind("Artillery", "CrewResupplySeconds", 120f,
                "Seconds per shell the crew brings up from the ammunition point. "
                + "A battery that is kept busy runs dry.");
        }

        // -------------------------------------------------------------- state

        /// <summary>One gun and everything that belongs to it. Plain data: the
        /// whole battery is driven from <see cref="Tick"/>, so nothing here needs
        /// a MonoBehaviour and no settlement costs an Update of its own.</summary>
        class Post
        {
            public int SettlementId;
            public GameObject Gun;          // the vehicle Mortar raised
            public Vector3 Centre;          // the settlement centre, the orbit's middle
            public string Name;
            public bool Safe;               // a trader camp: scenery, nothing more
            public float Phase;             // where on the circle this drone starts

            // A KNOWN SETTLEMENT WITHOUT A GUN YET (see GunExpected). Gun, crew
            // and fire missions wait for a player to come within Mortar's
            // PlaceRange; the drone does not, so a ghost post flies the orbit and
            // is drawn on the map from the moment the level is loaded. Site is
            // the settlement itself: it dies with the scene, which is what tells
            // the ghost to go.
            public bool Ghost;
            public Component Site;
            public float SeenAtScan;        // Time.time Mortar last announced it

            // crew, master client only
            public GameObject CrewSettlement;
            public Component Gunner;
            public Component Operator;
            public bool CrewAsked;
            public bool FactionSet;
            public int FactionTries;
            public float FactionNextTry;
            public float CrewTryAt;
            public int CrewTries;

            // the two stations at the vehicle, and when each man was last put
            // back on his: he stands in the ordinary idle for a moment before
            // he goes to work, so the whole-body clip is the standing one
            // underneath (Stand).
            public float NextPost;
            public float GunnerSince;
            public float OperatorSince;

            // The two stations as Posted last measured them, and whether they
            // have been measured at all. Held is what the per-frame Hold reads:
            // it is not allowed to cast a ray or touch the AI, it only compares
            // two heights (see Hold).
            public Vector3 GunnerAt;
            public Vector3 OperatorAt;
            public bool StationsSet;
            public bool GunnerLives;        // ... as of the last posting pass
            public bool OperatorLives;

            // crew as every client sees it: living men standing at the gun
            public int MenNear;
            public bool HostileNear;        // ... and at least one of them hates us
            public int CountedAt = -1;      // the NPC scan this count belongs to

            // the drone
            public bool DroneUp;
            public Vector3 DroneAt;
            public GameObject DroneModel;
            public float Ground;            // terrain height the drone flies over NOW
            public float GroundWant;        // ... and the last one actually measured
            public bool GroundSet;          // false until the first measurement
            public float GroundAt;          // Time.time it was last sampled
            public int DroneHits = 3;
            public double DroneReadyAt;
            public float NextDroneState;
            public string DroneKey;

            // spotting and the mission
            public float SeenSince;         // when the current candidate came into view
            public float LastSeenAt;        // last scan the candidate was actually found on
            public Vector3 SeenAt;
            public bool Sighting;
            public Vector3 Point;           // the reported point
            public Vector2 Error;           // the mission's own aim error
            public float ReportAt;
            public float NextMissionAt;
            public float NextScan;
            public GameObject Target;
            public Component TargetNpc;
            public GameObject Candidate;

            // the local player's own warning, on every client
            public float LocalSpotAt;
            public Vector3 LocalSpotPoint;
            public float NextWarn;

            // the crew's shells
            public int Rounds;
            public float NextShell;
        }

        static readonly List<Post> _posts = new List<Post>();
        static readonly Dictionary<int, Post> _byId = new Dictionary<int, Post>();

        // The settlements that are GOING to get a gun, with a drone in the air
        // already. They are kept apart from _posts on purpose: everything in
        // _posts has a vehicle standing in the world, and the crew, the posting,
        // the spotting and the fire missions all read it.
        static readonly List<Post> _ghosts = new List<Post>();
        static readonly Dictionary<int, Post> _ghostById = new Dictionary<int, Post>();

        /// <summary>Seconds a ghost post survives without Mortar naming it
        /// again. Mortar rescans every one to five seconds, so this is only
        /// reached when the settlement stopped being a candidate at all - it was
        /// written off for want of free ground, or a setting turned the guns
        /// off. A scene change is caught by the dead Site reference long before
        /// this runs out.</summary>
        const float GhostTimeout = 30f;

        // The living NPCs, gathered ONCE for every post instead of once per post:
        // FindObjectsOfType walks the whole scene, and a map with a dozen guns on
        // it would otherwise walk it a dozen times a second.
        static readonly List<Component> _npcs = new List<Component>();
        static float _nextNpcScan;
        static int _npcStamp;

        // The map is only asked whether it is open a few times a second - the
        // question is reflection. Drawing uses the flight position already
        // computed by Fly, so a moving icon needs no extra scene queries.
        static float _nextMapCheck;
        static bool _mapOpen;

        /// <summary>Visible batteries and recent sightings, refreshed while
        /// the map is open. The drone position comes from the live post.</summary>
        class Mark
        {
            public Post Source;
            public Vector3 Centre;
            public bool Spotted;
            public Vector3 SpotPoint;
        }

        static readonly List<Mark> _marks = new List<Mark>();

        static Texture2D _px;
        static Texture2D _ring;
        static Texture2D _droneIcon;
        const float DroneIconSize = 18f;

        // ------------------------------------------------- the gun's own seams

        /// <summary>Mortar raised a gun for this settlement. A SAFE settlement -
        /// a trader camp - keeps the vehicle as scenery and gets nothing else:
        /// no crew, no drone, no fire missions.</summary>
        internal static void GunRaised(int settlementId, GameObject gun,
                                       Vector3 centre, string name, bool safe)
        {
            if (!Enabled || gun == null) return;
            if (_byId.ContainsKey(settlementId)) return;
            // The drone that was already circling this settlement hands over to
            // the real post. Same centre, same phase, same shared clock, so the
            // airframe does not move by a metre across the handover - only the
            // owner of the orbit changes.
            DropGhost(settlementId);
            Post p = new Post();
            p.SettlementId = settlementId;
            p.Gun = gun;
            p.Centre = centre;
            p.Safe = safe;
            p.Name = name == null ? "" : name;
            p.DroneKey = ArtyRoom.Key(centre);
            // The phase comes from the settlement's POSITION, not from an
            // instance id or a random draw: two clients agree on the position to
            // the centimetre and on nothing else, and this is what makes both of
            // them draw the drone at the same point of the same circle.
            p.Phase = Mathf.Repeat(centre.x * 0.0131f + centre.z * 0.0177f,
                                   Mathf.PI * 2f);
            p.Rounds = Mathf.Clamp(_cfgMagazine == null ? 14 : _cfgMagazine.Value, 0, 60);
            p.CrewTryAt = Time.time + 1.5f;
            p.NextScan = Time.time + UnityEngine.Random.value;
            _posts.Add(p);
            _byId[settlementId] = p;
            RevivalPlugin.L.LogInfo("ArtyBattery: gun for \"" + p.Name + "\" taken over, "
                + "orbit " + Orbit().ToString("0") + " m around " + centre.ToString("0") + ".");
        }

        /// <summary>The scene changed under us - the gun is gone and so is
        /// everything that stood around it.</summary>
        internal static void GunLost(int settlementId)
        {
            Post p;
            if (!_byId.TryGetValue(settlementId, out p)) return;
            Drop(p);
            _byId.Remove(settlementId);
            _posts.Remove(p);
            // A settlement whose gun was lost with the scene keeps no ghost
            // either: Mortar rescans the NEW scene and names its settlements.
            DropGhost(settlementId);
        }

        /// <summary>
        /// THE DRONE DOES NOT WAIT FOR THE PLAYER (order of 2026-09-18: "ich
        /// will das die drohne auch angezeigt wird ohne das man vorher bei dem
        /// settlement war").
        ///
        /// Mortar's scan sees every settlement in the level from the first
        /// frame - FindObjectsOfType has no range - but it only RAISES the gun
        /// once a player is within PlaceRange, because the free-ground search
        /// needs loaded terrain colliders (E-059). The battery hung off that
        /// gun, so the drone existed only for settlements the player had
        /// already walked into, and the map icon appeared after the visit
        /// instead of telling him where not to go.
        ///
        /// A battery that is going to be built therefore gets its drone now.
        /// The orbit needs nothing but the centre, the shared clock and the
        /// phase derived from that centre, so a ghost post is the same circle
        /// the real post will fly, on every client, to the metre - the handover
        /// in GunRaised is invisible.
        ///
        /// What a ghost does NOT get, and why: no crew (the men are spawned at
        /// the vehicle, which does not exist), no spotting and no fire mission
        /// (InReach measures from the gun, and a settlement with no gun has
        /// nothing to answer with), and therefore no "you are being watched"
        /// warning either - a warning the battery cannot follow up on would be
        /// a lie, and it would put the orange sighting mark on the map for a
        /// mission that can never come. The drone is shot down exactly as any
        /// other: Shoot and HitDrone walk the ghosts too, and the hit lives in
        /// the same room property, keyed by the same centre, so it survives the
        /// handover in both directions.
        /// </summary>
        internal static void GunExpected(int settlementId, Component settlement,
                                         Vector3 centre, string name, bool safe)
        {
            if (!Enabled || settlement == null) return;
            if (_byId.ContainsKey(settlementId)) return;   // the gun stands: not a ghost
            Post p;
            if (_ghostById.TryGetValue(settlementId, out p))
            {
                p.Site = settlement;
                p.SeenAtScan = Time.time;
                return;
            }
            p = new Post();
            p.Ghost = true;
            p.Site = settlement;
            p.SeenAtScan = Time.time;
            p.SettlementId = settlementId;
            p.Centre = centre;
            p.Safe = safe;
            p.Name = name == null ? "" : name;
            p.DroneKey = ArtyRoom.Key(centre);
            // The SAME phase as the real post - see GunRaised.
            p.Phase = Mathf.Repeat(centre.x * 0.0131f + centre.z * 0.0177f,
                                   Mathf.PI * 2f);
            _ghosts.Add(p);
            _ghostById[settlementId] = p;
            RevivalPlugin.L.LogInfo("ArtyBattery: recon drone for \"" + p.Name
                + "\" is up before the gun is, orbit " + Orbit().ToString("0")
                + " m around " + centre.ToString("0") + ".");
        }

        /// <summary>The ghost is done: its gun was raised, its settlement is
        /// gone, or it stopped being a candidate for a battery.</summary>
        static void DropGhost(int settlementId)
        {
            Post g;
            if (!_ghostById.TryGetValue(settlementId, out g)) return;
            Drop(g);
            _ghostById.Remove(settlementId);
            _ghosts.Remove(g);
        }

        static void Drop(Post p)
        {
            if (p.DroneModel != null)
            {
                UnityEngine.Object.Destroy(p.DroneModel);
                p.DroneModel = null;
            }
            // The men are the game's own NPCs and are left exactly where they
            // are: a scene change removes them with everything else, and a crew
            // settlement we tear down by hand would take its men's death
            // bookkeeping with it. Crew's OWN list is a different matter: it
            // keeps every settlement it ever built so it can exempt them from
            // the game's distance culling, and a battery that dropped its crew
            // without saying so left an entry there for a destroyed object.
            if (p.CrewSettlement != null) Crew.Forget(p.CrewSettlement);
            p.CrewSettlement = null;
            p.Gunner = null;
            p.Operator = null;
            p.StationsSet = false;
            p.GunnerLives = false;
            p.OperatorLives = false;
            p.DroneUp = false;
        }

        /// <summary>Does a crew that is hostile to the local player hold this
        /// gun? Mortar asks before it lets the player take the sight.</summary>
        internal static bool CrewHoldsGun(int settlementId)
        {
            if (!Enabled || !B(_cfgCrewHolds, true)) return false;
            Post p;
            if (!_byId.TryGetValue(settlementId, out p)) return false;
            return p.HostileNear;
        }

        // --------------------------------------------------------------- tick

        public static void Tick()
        {
            if (!Enabled) return;
            try
            {
                float now = Time.time;

                // Before anything reads it: every orbit this frame is measured
                // from the same smooth clock. It is advanced even with no
                // batteries standing, so the first frame after one appears is
                // not a snap from a clock that stopped minutes ago.
                AdvanceFlightClock();

                // NO BATTERIES, NO OVERLAY. The early return used to sit above
                // this, and the map keeps what it was last given: after a level
                // change - which empties this list, because the guns are local
                // objects that die with the scene - the drone circles of the
                // level we left were still painted over the one we are in, and
                // nothing could ever clear them again. Reported as "countless
                // drone circles on the map" (2026-09-17). The ghosts are in the
                // question because they draw on the same map, and they die with
                // the scene the same way: their settlement is a scene object.
                if (_posts.Count == 0 && _ghosts.Count == 0)
                {
                    if (_marks.Count > 0) _marks.Clear();
                    _mapOpen = false;
                    return;
                }
                bool master = RevivalTroopInsertion.MasterClient();

                ScanNpcs(now);
                MapSnapshot(now);

                GameObject me = MapTools.LocalPlayer();
                Vector3 mine = me == null ? Vector3.zero : me.transform.position;

                for (int i = _posts.Count - 1; i >= 0; i--)
                {
                    Post p = _posts[i];
                    if (p.Gun == null)
                    {
                        Drop(p);
                        _byId.Remove(p.SettlementId);
                        _posts.RemoveAt(i);
                        continue;
                    }
                    Manning(p, now, master);
                    Posted(p, now, master);
                    // ... and every frame between two postings, the height
                    // alone: a man who is lifted must not be seen to rise at
                    // all, let alone climb (Hold).
                    Hold(p, master);
                    Fly(p, now, me != null, mine);
                    Warn(p, now, me, mine);
                    if (master)
                    {
                        Spot(p, now);
                        Mission(p, now);
                    }
                    Resupply(p, now);
                }

                // The settlements whose gun is still to come. Fly is the whole
                // battery they have: it reads the shared clock, the centre and
                // the room's drone state, and touches nothing that belongs to a
                // vehicle - see GunExpected.
                for (int i = _ghosts.Count - 1; i >= 0; i--)
                {
                    Post g = _ghosts[i];
                    if (g.Site == null || now - g.SeenAtScan > GhostTimeout
                        || _byId.ContainsKey(g.SettlementId))
                    {
                        Drop(g);
                        _ghostById.Remove(g.SettlementId);
                        _ghosts.RemoveAt(i);
                        continue;
                    }
                    Fly(g, now, me != null, mine);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ArtyBattery.Tick: " + ex);
            }
        }

        // --------------------------------------------------------------- crew

        /// <summary>The two men, and who of them is still alive. The head count
        /// is tied to the NPC scan rather than to the frame: it walks every man
        /// in range and reads his hated list, which is reflection, and the answer
        /// cannot change between two scans anyway.</summary>
        static void Manning(Post p, float now, bool master)
        {
            // Everybody counts the living men standing at the gun. It is the only
            // crew state a joined client can see at all (it did not spawn them),
            // and it is what decides whether the player may take the sight.
            if (p.CountedAt != _npcStamp)
            {
                p.CountedAt = _npcStamp;
                p.MenNear = 0;
                p.HostileNear = false;
                object myFaction = LocalFaction();
                Vector3 gun = p.Gun.transform.position;
                for (int i = 0; i < _npcs.Count; i++)
                {
                    Component ai = _npcs[i];
                    if (ai == null) continue;
                    float radius = Mathf.Max(GuardRadius, 24f * p.Gun.transform.localScale.x);
                    if (Flat(ai.transform.position - gun) > radius) continue;
                    p.MenNear++;
                    if (myFaction != null && Hostile(HatedOf(ai), myFaction)) p.HostileNear = true;
                }
            }

            if (p.Safe || !master || !B(_cfgCrew, true)) return;
            if (p.CrewAsked)
            {
                if (p.CrewSettlement == null) return;
                if (p.Gunner == null || p.Operator == null) Resolve(p);
                // THE MATCH IS RETRIED UNTIL IT LANDS. It used to be latched on
                // the first attempt whether or not a template man had been
                // found, and a failed attempt is not rare: the gun is raised at
                // PlaceRange and the crew follows 1.5 s later, which can be
                // before the settlement's own men are on their feet. The crew
                // then kept the CONFIGURED side for the rest of the level, and
                // "looter" standing in Peaces or Military1 - both of which hate
                // Marauders (measured hated lists, REVERSE_ENGINEERING) - is
                // shot by its own village within seconds. A dead operator is a
                // drone that never flies, which is exactly what was reported.
                if (!p.FactionSet && p.Gunner != null && now >= p.FactionNextTry)
                {
                    p.FactionSet = MatchFaction(p);
                    if (!p.FactionSet)
                    {
                        p.FactionTries++;
                        p.FactionNextTry = now + 2f;
                        if (p.FactionTries >= 30)
                        {
                            p.FactionSet = true;
                            RevivalPlugin.L.LogWarning("ArtyBattery: no living man of \""
                                + p.Name + "\" to copy a side from after "
                                + p.FactionTries + " tries - its crew keeps the "
                                + "configured one.");
                        }
                    }
                }
                return;
            }
            if (now < p.CrewTryAt) return;
            Spawn(p, now);
        }

        /// <summary>The editor side that comes closest to a game faction, for
        /// the two seconds before <see cref="MatchFaction"/> copies the real
        /// one. Only four of the game's eight factions have an editor side at
        /// all - a Military settlement has none - so this is the opening bid,
        /// never the answer.</summary>
        static string SideFor(Component template)
        {
            object mine = template == null ? null : FactionOf(template);
            string name = mine == null ? "" : mine.ToString();
            if (name == "Peace") return "civilian";
            if (name == "Marauder") return "looter";
            if (name == "Traitor") return "traitor";
            // Military, Hermit, Wildman, MilitaryNeutral and Neutral all land
            // here. Neutral hates Traitor and nobody else, and only the neutral
            // base hates Neutral back, so it is the side that gets shot at least
            // while the real one is being copied on.
            if (name.Length > 0) return "neutral";
            return _cfgFaction == null ? "looter" : _cfgFaction.Value;
        }

        static void Spawn(Post p, float now)
        {
            p.CrewTries++;
            p.CrewTryAt = now + 5f;
            try
            {
                // THE SIDE BEFORE THE MEN. Spawning first and correcting after
                // leaves a window in which two Marauders stand in a settlement
                // that shoots Marauders. The village is given up to four tries
                // (20 s) to have somebody on his feet to read; after that the
                // crew is raised anyway, because a battery is worth more than a
                // perfect uniform and MatchFaction keeps trying.
                Component template = SettlementMan(p);
                if (template == null && p.CrewTries < 4) return;

                Transform gun = p.Gun.transform;
                // AT THE VEHICLE, not behind it. They used to be set down 19.5
                // units off the tail and then wandered the ring Crew.DropSquad
                // lays out for a squad; they are now spawned on their own
                // stations and held there (Posted). Station puts them on the
                // ground itself.
                //
                // BOTH STATIONS GO OVER, not just the gunner's. FIELD
                // 2026-09-18 ("sie werden zwar wieder auf den boden tp'd, aber
                // davor fliegen sie erstmal ne runde"): handed a single point,
                // Crew.Ausstiege laid its own 4.5-unit ring around it and put
                // one man on each side - and one of those sides is the vehicle.
                // He was spawned on the hull, in the air, and stayed there
                // until Posted warped him down 1.5 s later, which is the "lap"
                // of the report. Given the stations themselves there is no
                // ring, no guess and nothing left to undo: the first position
                // each man ever holds is the one he keeps.
                Vector3[] posts =
                    new Vector3[] { Station(gun, true), Station(gun, false) };
                Vector3 at = posts[0];

                // BOTH MEN CARRY A RIFLE, AND THE LOADOUT SAYS SO. Crew.DropSquad
                // arms a man from the editor loadout and falls back to
                // Patrol/CrewLawCount when the loadout names no weapon - and
                // that key defaults to 1, so the FIRST man out of every squad
                // gets an M72 LAW. For a patrol crew that is the point; for the
                // two men standing at a howitzer it meant every battery on the
                // map came with a rocketeer firing an endless supply of rockets
                // from the village (field report 2026-09-17). A named weapon
                // takes the branch that never looks at the LAW count.
                List<RevivalComposition.CrewMan> loadout =
                    new List<RevivalComposition.CrewMan>();
                RevivalComposition.CrewMan gunner = new RevivalComposition.CrewMan();
                gunner.Role = "gunner";
                gunner.Weapons = new int[] { CrewWeapon };
                RevivalComposition.CrewMan spotter = new RevivalComposition.CrewMan();
                spotter.Role = "drone_operator";
                spotter.Weapons = new int[] { CrewWeapon };
                loadout.Add(gunner);
                loadout.Add(spotter);

                string side = SideFor(template);
                // Facing the hull from the off, and from the LEVEL frame the
                // stations are built in: a gun stood on a ground normal has a
                // Y euler that is not its heading at all. Both men work the
                // same side of the vehicle, so one yaw is both their yaws.
                GameObject crew = Crew.DropSquadAt(at, posts,
                    StationYaw(gun, PostOf(true)), side, loadout);
                if (crew == null)
                {
                    if (p.CrewTries >= 4)
                    {
                        p.CrewAsked = true;
                        RevivalPlugin.L.LogWarning("ArtyBattery: no crew could be spawned "
                            + "for \"" + p.Name + "\" after " + p.CrewTries + " tries - that "
                            + "gun stays unmanned.");
                    }
                    return;
                }
                p.CrewSettlement = crew;
                p.CrewAsked = true;
                // The stations the men were BUILT on are what Hold measures
                // against until the first Posted pass, 1.5 s from now: the
                // height guard must not have a blind first second and a half,
                // which is where the 2026-09-18 report lived.
                p.GunnerAt = posts[0];
                p.OperatorAt = posts[1];
                p.StationsSet = true;
                // The men are not posted in the frame they were made. NPC_AI2
                // .Start still has FindMySpawnPointAndSet to run, which puts a
                // man on his spawn point, and a station warp that raced it
                // would simply be undone.
                p.NextPost = now + 1.5f;
                Resolve(p);
                p.GunnerLives = p.Gunner != null;
                p.OperatorLives = p.Operator != null;
                if (p.Gunner != null) p.FactionSet = MatchFaction(p);
                // The two stations go into the log with the crew: a report that
                // a man stands in the wrong place, or over the ground, is read
                // against these two numbers and the hull's own position.
                RevivalPlugin.L.LogInfo("ArtyBattery: crew for \"" + p.Name
                    + "\" on its feet (gunner " + (p.Gunner != null)
                    + ", operator " + (p.Operator != null) + ") - gunner at "
                    + posts[0].ToString("0.0") + ", operator at "
                    + posts[1].ToString("0.0") + ", hull at "
                    + gun.position.ToString("0.0") + ".");
            }
            catch (Exception ex)
            {
                p.CrewAsked = p.CrewTries >= 4;
                RevivalPlugin.L.LogWarning("ArtyBattery: crew spawn failed for \""
                    + p.Name + "\": " + ex.Message);
            }
        }

        /// <summary>Pick the gunner and the operator out of the spawned men. The
        /// array is the crew settlement's own NpcAI list, in spawn-point order,
        /// so element 0 is the man built from the "gunner" role.</summary>
        static void Resolve(Post p)
        {
            if (p.CrewSettlement == null) return;
            try
            {
                Array men = Crew.Men(p.CrewSettlement);
                if (men == null) return;
                for (int i = 0; i < men.Length; i++)
                {
                    Component ai = men.GetValue(i) as Component;
                    if (ai == null) continue;
                    if (i == 0 && p.Gunner == null) p.Gunner = ai;
                    else if (p.Operator == null && !ReferenceEquals(ai, p.Gunner)) p.Operator = ai;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ArtyBattery: crew list of \"" + p.Name
                    + "\": " + ex.Message);
            }
        }

        /// <summary>
        /// Put the crew on the settlement's own side.
        ///
        /// Crew.DropSquad builds a squad from one of the four editor sides, and
        /// any of them can be the WRONG one here: a looter crew standing in a
        /// military settlement starts a firefight inside the village within
        /// seconds, and the first thing the player would see is his artillery
        /// crew being shot by the people it belongs to. So the faction is copied
        /// off a living man of the real settlement - MyFraction and the hated
        /// list both, because the AI reads the hated list and nothing else
        /// (NPC_AI2.IsEnemyFraction). Without a readable template the configured
        /// side stands, which is what the fallback is for.
        /// </summary>
        static bool MatchFaction(Post p)
        {
            Component template = SettlementMan(p);
            if (template == null) return false;
            object mine = FactionOf(template);
            Array hated = HatedOf(template);
            if (mine == null && hated == null) return false;
            int done = 0;
            done += Apply(p.Gunner, mine, hated) ? 1 : 0;
            done += Apply(p.Operator, mine, hated) ? 1 : 0;
            if (done > 0)
                RevivalPlugin.L.LogInfo("ArtyBattery: crew of \"" + p.Name + "\" put on the "
                    + "settlement's own side (" + (mine == null ? "?" : mine.ToString())
                    + ") - " + done + " man(men).");
            return done > 0;
        }

        static bool Apply(Component ai, object mine, Array hated)
        {
            if (ai == null) return false;
            try
            {
                object opt = Options(ai);
                if (opt == null) return false;
                if (mine != null && _fMyFraction != null) _fMyFraction.SetValue(opt, mine);
                // A COPY of the list, not the template's own array. Other parts
                // of the toolkit rewrite a settlement's hated list in place (the
                // traitor camp does), and a shared reference would carry that
                // edit into the men it was copied from.
                if (hated != null && _fHated != null)
                    _fHated.SetValue(opt, hated.Clone() as Array);
                return true;
            }
            catch { return false; }
        }

        /// <summary>A living man of the REAL settlement near the gun, to copy a
        /// faction from. Our own two are skipped, and so is anyone further away
        /// than the settlement itself is wide.</summary>
        static Component SettlementMan(Post p)
        {
            Component best = null;
            float bestD = 120f;
            for (int i = 0; i < _npcs.Count; i++)
            {
                Component ai = _npcs[i];
                if (ai == null) continue;
                if (ReferenceEquals(ai, p.Gunner) || ReferenceEquals(ai, p.Operator)) continue;
                float d = Flat(ai.transform.position - p.Centre);
                if (d > bestD) continue;
                bestD = d;
                best = ai;
            }
            return best;
        }

        static bool Alive(Component ai)
        {
            if (ai == null) return false;
            try
            {
                if (!Look() || _mIsAlive == null) return true;
                object r = _mIsAlive.Invoke(ai, null);
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        // ------------------------------------------------- the men at the gun

        // WHERE THE TWO MEN STAND, in the vehicle's own space: +Z is the way
        // the hull faces, +X its right side. A man is five units tall and the
        // hull is 31.6 units long, so these are paces and not metres.
        //
        // FIELD 2026-09-19: "auf crewrightside siehst du die ANDERE seite des
        // fahrzeugs, dort ist bereits der zusehende target computer angebracht,
        // da soll der gunner bis ganz kurz vorm fahrzeug stehen, also richtig
        // dran. der drone operator soll daneben dauerhaft hocken". Both men
        // therefore move from the left side of the hull to the RIGHT one, the
        // side that carries the target computer, and in from 10.5 to 8: the
        // widest the model ever gets is 7.3 (14.579 units across with the
        // stabilizers deployed, arty-appearance.md), so 8 is a hand's breadth
        // off the vehicle - at the box, not beside it - and still outside the
        // geometry, which matters because a man's own capsule is 0.75 wide.
        //
        // The operator crouches beside him, four units further back: he is
        // flying the drone, not working the gun, and a pace and a half is close
        // enough to read as one crew and far enough not to share his mate's
        // capsule. The two still STRADDLE the player's own use point
        // (ArtyModel.UsePoint, z -10.5) rather than sitting on it, so a player
        // who takes the sight from this side stands between his crew instead
        // of inside one of them - and the other side of the hull is now
        // completely free, which is the side UsePoint gives him if he walks up
        // from there. Both are defaults only - Artillery/CrewGunnerPost and
        // CrewOperatorPost move them without a new build, which is the only way
        // a box whose exact place on the model nobody here can see gets its man
        // standing squarely at it.
        static readonly Vector3 GunnerPost = new Vector3(8f, 0f, -8f);
        static readonly Vector3 OperatorPost = new Vector3(8f, 0f, -12f);

        /// <summary>One of the two posts in the vehicle's own space, from the
        /// config when it carries a readable "x,y,z" and from the defaults
        /// above otherwise.</summary>
        static Vector3 PostOf(bool gunner)
        {
            return ParseVec3(gunner ? _cfgGunnerPost : _cfgOperatorPost,
                             gunner ? GunnerPost : OperatorPost);
        }

        static Vector3 ParseVec3(ConfigEntry<string> entry, Vector3 fallback)
        {
            if (entry == null || entry.Value == null) return fallback;
            string[] parts = entry.Value.Split(',');
            if (parts.Length != 3) return fallback;
            float x, y, z;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out z)) return fallback;
            return new Vector3(x, y, z);
        }

        /// <summary>Seconds a man stands in the ordinary idle after he has been
        /// put on his station, before he goes to work. The shooting and working
        /// clips of this game are upper-body layers over a whole-body one
        /// (NPC_AI2.SetBlendingAnimLayers, CONFIRMED IL), and the whole-body
        /// clip underneath has to be the STANDING one - straight out of a walk
        /// the legs would keep walking on the spot. CrossFade's own fade is
        /// 0.3 s.</summary>
        const float SettleSeconds = 0.6f;

        /// <summary>
        /// One of the two stations at the vehicle, ON THE GROUND.
        ///
        /// FIELD 2026-09-18 ("die arty crew bugged rum, fliegt teilweise in der
        /// luft, anstatt auf dem boden an dem fahrzeug dran zu stehen") and
        /// 2026-09-19. Three separate reasons a posted man ended up in the air,
        /// and this takes all three away:
        ///
        ///   THE HULL IS TILTED. Mortar.Raise stands the vehicle on the ground
        ///   NORMAL (transform.up = normal, the same way the anti-tank mine is
        ///   laid). TransformPoint therefore carries the station up the slope
        ///   with it, and 10.5 units out on a 20 degree bank is 3.6 units -
        ///   over a metre - of height the man never had any ground under. The
        ///   station is now built in a LEVEL frame: the hull's yaw only, from
        ///   the hull's own position. A leaning gun no longer lifts its crew.
        ///
        ///   THE GROUND RAY HIT THE GUN. RevivalTroopInsertion.GroundY casts
        ///   from 1500 units down and takes the FIRST collider, which next to a
        ///   vehicle is the vehicle - and with a traversing turret the barrel
        ///   sweeps over both stations, which is exactly the "sometimes" of the
        ///   report. GunGround starts just over the deck, stops well under any
        ///   roof, and skips anything that belongs to the gun or is a man.
        ///
        ///   THE STATION CANNOT BE OVER THE VEHICLE'S OWN FEET. FIELD
        ///   2026-09-19: "der hochflieg bug ist immernoch nicht geloest, es
        ///   dauert nur einfach paar sekunden, dann steigen sie in stufen
        ///   wieder gen himmel auf, und werden wieder runter tp'd". A staircase
        ///   with a period of half a second is the posting loop feeding itself:
        ///   the ray that measures the station is cast straight down through
        ///   the man who is standing ON that station, and every collider it
        ///   accepts that is not the floor lifts him by its own height - his
        ///   capsule is five units tall, so four passes carry him past
        ///   DeckReach, the cast then starts BELOW him, finds the real ground,
        ///   and he is dropped. That is the climb and the teleport, both.
        ///   Rather than trusting a filter to recognise every kind of thing a
        ///   man can be mistaken for (IsMan reads a NavMeshAgent off the
        ///   collider's ancestors, and a hit on a bone deep in a skeleton, or
        ///   on a model whose agent sits on a child, is not recognised at all),
        ///   the station now takes the one fact that cannot be argued with: the
        ///   GUN stands on the ground these men stand on. Nothing more than
        ///   StandMaxRise over the hull's own base is their floor.
        ///
        /// Finally the point is put on the NavMesh where there is one within
        /// reach: that surface IS "where a man can stand", and the agent that
        /// is warped onto it needs it anyway.
        /// </summary>
        static Vector3 Station(Transform gun, bool gunner)
        {
            Vector3 post = PostOf(gunner);
            float scale = Mathf.Abs(gun.lossyScale.x);
            if (scale < 0.01f) scale = 1f;
            Vector3 at = gun.position
                       + Quaternion.Euler(0f, HullYaw(gun), 0f) * (post * scale);
            float y;
            if (GunGround(at, gun, out y)) at.y = y;

            // The NavMesh is the game's own answer to "can a man stand here",
            // and an agent warped off it keeps the position it was dropped at
            // for good. Only a sample that is genuinely at this station is
            // taken: a far or much higher one is the wrong ground.
            try
            {
                NavMeshHit nav;
                if (NavMesh.SamplePosition(at, out nav, 5f, NavMesh.AllAreas)
                    && Flat(nav.position - at) <= 4f
                    && Mathf.Abs(nav.position.y - at.y) <= 3f)
                    at = nav.position;
            }
            catch { }
            // The last word, over the ray and over the NavMesh both: a crewman
            // does not stand above the vehicle he serves.
            float ceiling = StandCeiling(at, gun);
            if (at.y > ceiling) at.y = ceiling;
            return at;
        }

        /// <summary>The highest a crewman's feet may be at that point: the
        /// higher of the vehicle's own base and the terrain under the station,
        /// plus StandMaxRise. The terrain is in it because an emplacement is
        /// allowed to lean (Mortar.Clear takes a normal down to 0.80), and on
        /// such a patch the ground eight units out really is above the hull's
        /// origin; the hull is in it because a scene with no terrain data still
        /// has to answer. Neither is a ray, so this costs nothing.</summary>
        static float StandCeiling(Vector3 at, Transform gun)
        {
            float scale = Mathf.Abs(gun.lossyScale.x);
            if (scale < 0.01f) scale = 1f;
            float floor = gun.position.y;
            float terrain;
            if (RevivalTroopInsertion.TerrainHeight(at, out terrain) && terrain > floor)
                floor = terrain;
            return floor + StandMaxRise * scale;
        }

        /// <summary>The ground under a point beside the gun, ignoring the gun
        /// itself, anyone standing there, and anything else that is above the
        /// vehicle's own base - a deck, a crate, a man's head. The cast starts
        /// just over the deck rather than at 1500 units, so a roof or a branch
        /// overhead is not mistaken for the floor either; with nothing below it
        /// the ordinary terrain answer still applies.</summary>
        static bool GunGround(Vector3 at, Transform gun, out float y)
        {
            y = at.y;
            try
            {
                float ceiling = StandCeiling(at, gun);
                Vector3 from = new Vector3(at.x, at.y + DeckReach, at.z);
                float rest = DeckReach + DigReach;
                // Six, not four: everything between the start of the cast and
                // the ceiling is now walked past, and beside a vehicle that can
                // be the deck, a stabilizer, a man and his weapon in a row.
                for (int i = 0; i < 6 && rest > 0f; i++)
                {
                    Vector3 hit;
                    GameObject go = Turret.RaycastObject(from, Vector3.down, rest, out hit);
                    if (go == null) break;
                    if (hit.y <= ceiling && !PartOfGun(go, gun) && !IsMan(go))
                    {
                        y = hit.y;
                        return true;
                    }
                    rest -= Vector3.Distance(from, hit) + 0.25f;
                    from = hit + Vector3.down * 0.25f;
                }
            }
            catch { }
            // No usable collider under the station: the height data still knows
            // where the terrain is, and away from the player the whole-map
            // TerrainColliders are off (E-059).
            return RevivalTroopInsertion.TerrainHeight(at, out y);
        }

        /// <summary>How far above and below a station the ground is looked
        /// for. Above: over the deck of the vehicle, so the cast starts clear
        /// of it, but under any roof. Below: down a bank the gun is parked on
        /// the edge of.</summary>
        const float DeckReach = 14f;
        const float DigReach = 40f;

        /// <summary>How far over the vehicle's own base a crewman's floor may
        /// be. One unit is a third of a metre; three is the kerb the gun itself
        /// could be standing on while his own feet are beside it. A man is five
        /// units tall, a deck several more, so nothing a man can be stood ON
        /// fits under this - which is the whole point.</summary>
        const float StandMaxRise = 3f;

        /// <summary>Does that collider belong to the gun, turret or barrel?</summary>
        static bool PartOfGun(GameObject go, Transform gun)
        {
            if (go == null || gun == null) return false;
            Transform t = go.transform;
            while (t != null)
            {
                if (t == gun) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>A man is not ground. His own capsule stands at the station
        /// from the frame he is planted on it, and the ray that measures the
        /// station is cast straight down through him.
        ///
        /// The whole chain of ancestors is walked, not four of them: an NPC is
        /// hit on whatever collider happens to be uppermost, and that can be a
        /// bone six or eight levels below the root of the model. The AI
        /// component is asked for as well as the agent, because which of the
        /// two sits on which object is a property of the prefab and not
        /// something this file may assume.</summary>
        static bool IsMan(GameObject go)
        {
            if (go == null) return false;
            Type ai = Look() ? _npcType : null;
            Transform t = go.transform;
            while (t != null)
            {
                if (t.GetComponent<NavMeshAgent>() != null) return true;
                if (ai != null && t.GetComponent(ai) != null) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>The hull's heading with the lean taken out of it. Both the
        /// stations and the way the men face are built from this, so a gun
        /// standing on a slope still has its crew upright beside it.</summary>
        static float HullYaw(Transform gun)
        {
            Vector3 ahead = gun.TransformDirection(Vector3.forward);
            ahead.y = 0f;
            // Only a hull standing on its nose or its tail has no flattened
            // forward at all, which Mortar.Raise cannot produce from a ground
            // normal. The Y euler is then as good an answer as any - the worst
            // it costs is which side of the hull the two men work on, and they
            // are still standing on the ground beside it.
            if (ahead.sqrMagnitude < 0.0001f) return gun.eulerAngles.y;
            return Mathf.Atan2(ahead.x, ahead.z) * Mathf.Rad2Deg;
        }

        /// <summary>Which way a man looks: AT the side of the hull he is
        /// working on, in the same level frame the stations themselves are
        /// built in. A quarter turn off the hull's heading, and which way round
        /// follows the side he was put on - the crew moved from the hull's left
        /// to its right in 6.29.1 and a fixed quarter turn would have left both
        /// men working with their backs to the vehicle.</summary>
        static float StationYaw(Transform gun, Vector3 post)
        {
            return HullYaw(gun) + (post.x >= 0f ? -90f : 90f);
        }

        /// <summary>
        /// THE CREW IS POSTED, NOT PATROLLING (order 2026-09-18: the gunner
        /// should really stand AT the artillery thing, in a permanent animation
        /// of working at the computer on the side of it, moving not at all; the
        /// drone operator stands beside him and does not move either, because
        /// he is flying the drone).
        ///
        /// Crew.DropSquad builds every squad a ring of eight walk points and
        /// the game's own IdleStateAction walks the men round it, so the two
        /// men drifted away from the vehicle they belong to and the "crew at
        /// the gun" the player meets was whoever happened to be on the near
        /// side of the ring. Two halves, and each of them runs where it can:
        ///
        ///   THE MASTER puts each man on his station and keeps the vanilla idle
        ///   logic off him. IdleStateAction returns while GetCalculatedPauseTime
        ///   is positive (CONFIRMED IL), so a pause refreshed twice a second
        ///   holds him without touching the AI anywhere else, and the state
        ///   itself goes out through the game's own SetStateWithAnimAndSync -
        ///   which is an RPC, so every other client sees the same man in the
        ///   same pose at the same place.
        ///
        ///   EVERY CLIENT paints the working clip if the state alone plays
        ///   none. A joined client never spawned these two and cannot tell them
        ///   from any other NPC, so it takes whoever is standing on the
        ///   station - which, because the master holds them there, is the right
        ///   man.
        ///
        /// The two men are NOT held the same way (order 2026-09-19). The gunner
        /// works the target computer standing, because that is where the box
        /// is; the operator squats over his controller beside him and stays
        /// squatting, which is the game's own Crouch pose and its own
        /// crouch_idle clip (GetAnimationNameCrouchPose, CONFIRMED IL).
        /// </summary>
        static void Posted(Post p, float now, bool master)
        {
            if (p.Gun == null || p.Safe || !B(_cfgCrewPosted, true)) return;
            if (now < p.NextPost) return;
            p.NextPost = now + 0.5f;
            try
            {
                Transform gun = p.Gun.transform;
                Vector3 gunnerAt = Station(gun, true);
                Vector3 operatorAt = Station(gun, false);
                p.GunnerAt = gunnerAt;
                p.OperatorAt = operatorAt;
                p.StationsSet = true;
                Component gunner = StationMan(p.Gunner, gunnerAt, master);
                Component spotter = StationMan(p.Operator, operatorAt, master);
                // Whether each man is alive is settled HERE, once every half
                // second, because IsAlive is reflection - and the per-frame
                // height guard must not pay for it, nor push a dead man's
                // ragdoll around.
                p.GunnerLives = gunner != null;
                p.OperatorLives = spotter != null;
                p.GunnerSince = Stand(gunner, gunnerAt,
                                      StationYaw(gun, PostOf(true)), master,
                                      p.GunnerSince, now, 0f, _gunnerHold);
                p.OperatorSince = Stand(spotter, operatorAt,
                                        StationYaw(gun, PostOf(false)), master,
                                        p.OperatorSince, now, 0.37f, _operatorHold);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ArtyBattery: the crew of \"" + p.Name
                    + "\" could not be posted: " + ex.Message);
            }
        }

        /// <summary>
        /// EVERY FRAME, AND ONLY THE HEIGHT. Posted runs twice a second, which
        /// is often enough to put a man back but not to keep anybody from
        /// SEEING him leave: half a second of whatever lifted him is already a
        /// jump, and the 2026-09-19 report is a staircase of them. This is the
        /// backstop for anything that ever lifts a posted man again, whatever
        /// it turns out to be - it costs two subtractions per battery per frame,
        /// it never moves a man sideways, it never touches the AI or the agent,
        /// and it only fires above the same tolerance Plant uses, so a man
        /// standing where he belongs is not touched at all.
        ///
        /// Master only. On a joined client the man's position comes off the
        /// wire, and a local correction would fight the replication that is
        /// already carrying the master's answer.
        /// </summary>
        static void Hold(Post p, bool master)
        {
            if (!master || !p.StationsSet || p.Safe || !B(_cfgCrewPosted, true)) return;
            if (p.GunnerLives) KeepDown(p.Gunner, p.GunnerAt);
            if (p.OperatorLives) KeepDown(p.Operator, p.OperatorAt);
        }

        static void KeepDown(Component ai, Vector3 at)
        {
            if (ai == null) return;
            Transform t = ai.transform;
            Vector3 now = t.position;
            if (now.y - at.y <= StationRise) return;
            t.position = new Vector3(now.x, at.y, now.z);
        }

        /// <summary>The man on that station. The master knows his own two by
        /// name and takes NOBODY else: before his crew is up, and after it is
        /// dead, the station stays empty rather than drafting the first
        /// villager who walked past it. A client that joined never spawned
        /// these two and has no way of telling them apart, so it takes whoever
        /// is standing there - which, because the master holds them there, is
        /// the right man.</summary>
        static Component StationMan(Component known, Vector3 at, bool master)
        {
            if (known != null) return Alive(known) ? known : null;
            if (master) return null;
            Component best = null;
            float bestD = 4f;
            for (int i = 0; i < _npcs.Count; i++)
            {
                Component ai = _npcs[i];
                if (ai == null) continue;
                float d = Flat(ai.transform.position - at);
                if (d > bestD) continue;
                bestD = d;
                best = ai;
            }
            return best;
        }

        /// <summary>One man, held on one station in the way his job asks for.
        /// Returns when he was last put back on it, which is what decides
        /// whether he is still settling into the standing clip or already at
        /// work.</summary>
        static float Stand(Component ai, Vector3 at, float yaw, bool master,
                           float since, float now, float phase, StateHold hold)
        {
            if (ai == null) return 0f;
            // Only a WALK costs him the standing clip. A man nudged half a pace
            // is pushed back without being sent to the back of the queue for
            // it, or two crewmen leaning on each other would keep each other in
            // the settling idle for good.
            if (master && Plant(ai, at, yaw) > WalkedOff) since = 0f;
            if (since <= 0f) since = now;
            bool settled = now - since >= SettleSeconds;
            // The settling second is the plain standing idle for BOTH men, the
            // crouching one included: the crouch is a whole-body clip too, and
            // crossing into it out of a walk is what the settle is for.
            if (master)
                Drive(ai, at, settled ? hold.Main() : MainIdle,
                      settled ? hold.Pose() : PoseStand, yaw, hold);
            if (settled) Loop(ai, phase, hold.Clip);
            return since;
        }

        /// <summary>Units a man is out of place before he is pushed back at
        /// all, and the distance above which the push counts as a walk.</summary>
        const float StationSlack = 1.2f;
        const float WalkedOff = 4f;

        /// <summary>Units off the station's own height before he is put back
        /// down. This was three units - a whole metre of daylight under a man
        /// who was then left standing in it, half of the 2026-09-18 report.
        /// Half a metre is kept, not less: an agent carries a base offset of
        /// its own and would be pushed down and resolve back up twice a second
        /// under a tighter bound, which is a jitter, not a fix. A vertical
        /// correction alone never counts as a walk - the settle timer is fed
        /// from the FLAT distance - so this costs nothing but the drop.</summary>
        const float StationRise = 1.5f;

        /// <summary>Master only: on the station, facing the hull, with the
        /// game's idle logic held off him. Returns how far he had to be
        /// carried.</summary>
        static float Plant(Component ai, Vector3 at, float yaw)
        {
            Quiet(ai);
            Transform t = ai.transform;
            float away = Flat(t.position - at);
            bool sunk = Mathf.Abs(t.position.y - at.y) > StationRise;
            bool turned = Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.y, yaw)) > 10f;
            if (away <= StationSlack && !sunk)
            {
                if (turned) t.rotation = Quaternion.Euler(0f, yaw, 0f);
                return 0f;
            }
            // The agent is WARPED, not teleported: it holds a path of its own
            // and would drag him back along it from wherever we dropped him.
            NavMeshAgent agent = Agent(ai);
            try
            {
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    agent.Warp(at);
                }
            }
            catch { }
            t.position = at;
            t.rotation = Quaternion.Euler(0f, yaw, 0f);
            return away;
        }

        /// <summary>Keep the vanilla idle logic off a posted man. NPC_AI2
        /// .IdleStateAction queues its own intentions on every idle pass and
        /// returns early while GetCalculatedPauseTime is positive, so a short
        /// pause refreshed twice a second is the whole hold - the same one
        /// NpcWar uses to keep a man in an aim pose.</summary>
        static void Quiet(Component ai)
        {
            try
            {
                if (_mClearIntentions != null) _mClearIntentions.Invoke(ai, null);
                if (_mPauseTime != null) _mPauseTime.Invoke(ai, new object[] { 1.4f });
            }
            catch { }
        }

        /// <summary>The state the GUNNER works in. 10 is NPCMainState.Working -
        /// what an NPC busy with something in front of him is in. A number the
        /// installed game does not animate costs nothing: SwitchAnimationByStates
        /// skips a CrossFade whose clip does not exist (CONFIRMED IL), the man
        /// keeps the standing clip he was settled into, and he still does not
        /// move.</summary>
        static int WorkState()
        {
            return _cfgWorkState == null ? MainWorking
                : Mathf.Clamp(_cfgWorkState.Value, 0, 12);
        }

        /// <summary>The pose the OPERATOR is held in. 1 is NPCPoseState.Crouch,
        /// and the crouch clips are the game's own: GetAnimationNameCrouchPose
        /// maps Idle to "crouch_idle" (CONFIRMED IL, REVERSE_ENGINEERING). He
        /// squats over his controller and stays squatting, which is the order
        /// of 2026-09-19 ("der drone operator soll daneben dauerhaft
        /// hocken").</summary>
        static int OperatorPose()
        {
            return _cfgOperatorPose == null ? PoseCrouch
                : Mathf.Clamp(_cfgOperatorPose.Value, 0, 2);
        }

        /// <summary>
        /// How one of the two men is held, and what this build has shown about
        /// it. A state the game writes and then takes back is not a pose, it is
        /// a packet storm: every attempt is an RPC to every player in the room.
        /// Twelve in a row that do not stick and THAT man keeps the plain
        /// standing idle for the rest of the session.
        ///
        /// ONCE IT HAS STUCK IT IS NEVER GIVEN UP AGAIN. The counter is there to
        /// catch a build whose NPCs do not know this state at all; a firefight
        /// at the battery also knocks a man out of it, over and over, and that
        /// must not be read as the same thing. The two men count separately -
        /// the gunner asks for a main state and the operator for a pose, and a
        /// build that refuses one may well hold the other.
        /// </summary>
        sealed class StateHold
        {
            public readonly string Who;
            public readonly bool Gunner;
            public readonly ClipPick Clip;
            public int Misses;
            public bool Held;
            public bool Broken;

            public StateHold(string who, bool gunner, ClipPick clip)
            {
                Who = who;
                Gunner = gunner;
                Clip = clip;
            }

            public int Main() { return Broken || !Gunner ? MainIdle : WorkState(); }
            public int Pose() { return Broken || Gunner ? PoseStand : OperatorPose(); }
        }

        static readonly StateHold _gunnerHold =
            new StateHold("gunner", true, new ClipPick("gunner", true));
        static readonly StateHold _operatorHold =
            new StateHold("drone operator", false, new ClipPick("drone operator", false));

        /// <summary>NPC_AI2.SetStateWithAnimAndSync(position, main, additional,
        /// pose, walk point index, use temporary points, temporary task, rotY)
        /// with the game's own enum types built from the numbers. Sent only
        /// when the man is not already in that state: every call is an RPC to
        /// every player in the room and restarts the clip.</summary>
        static void Drive(Component ai, Vector3 at, int main, int pose, float yaw,
                          StateHold hold)
        {
            // Without MainState there is no way to tell whether the state took,
            // and a state re-sent every half second is an RPC storm. The man is
            // planted and paused either way, which is most of the order.
            if (!Look() || _mStateSync == null || _fMainState == null) return;
            // "Special" is anything that is not the plain standing idle - the
            // gunner's working state, the operator's crouch, or both.
            bool special = main != MainIdle || pose != PoseStand;
            if (IntField(ai, _fMainState, -1) == main
                && IntField(ai, _fAddState, -1) == AddEmpty
                && IntField(ai, _fPoseState, -1) == pose)
            {
                if (special) { hold.Misses = 0; hold.Held = true; }
                return;
            }
            if (special && !hold.Held && ++hold.Misses >= 12)
            {
                hold.Broken = true;
                RevivalPlugin.L.LogWarning("ArtyBattery: state " + main + "/pose " + pose
                    + " does not hold on this build - the " + hold.Who + " keeps the "
                    + "standing idle. He still stands at the gun and still does not "
                    + "move.");
                return;
            }
            try
            {
                bool useTemp = _fUseTemp == null || !(_fUseTemp.GetValue(ai) is bool)
                    || (bool)_fUseTemp.GetValue(ai);
                int task = IntField(ai, _fTempTask, 2);
                _mStateSync.Invoke(ai, new object[] {
                    at, Arg(_mStateSync, 1, main), Arg(_mStateSync, 2, AddEmpty),
                    Arg(_mStateSync, 3, pose), Arg(_mStateSync, 4, -1), useTemp,
                    Arg(_mStateSync, 6, task), yaw });
            }
            catch (Exception ex)
            {
                if (_stateWarned) return;
                _stateWarned = true;
                RevivalPlugin.L.LogWarning("ArtyBattery: a crewman could not be put "
                    + "into state " + main + "/pose " + pose + " ("
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message)
                    + ") - the crew stands at the gun without the working pose.");
            }
        }

        static object Arg(MethodInfo m, int index, int value)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (index >= ps.Length) return value;
            Type t = ps[index].ParameterType;
            return t.IsEnum ? Enum.ToObject(t, value) : (object)value;
        }

        /// <summary>
        /// The picture, on every client: if the state alone leaves the man in a
        /// plain standing idle, a clip that LOOKS like work is looped over him
        /// here. Nothing is invented - only a clip the model already carries is
        /// played, it is found by name in the man's own animation set, and a
        /// model that carries none leaves him standing, which is still exactly
        /// what the order asked for: he does not move.
        ///
        /// The two men are given different phases of the same clip, or a
        /// battery would be two identical puppets working in lockstep.
        /// </summary>
        static void Loop(Component ai, float phase, ClipPick pick)
        {
            try
            {
                Component anim = AnimationOf(ai);
                if (anim == null) return;
                string clip = WorkClip(anim, pick);
                if (clip == null) return;
                object playing = _mIsPlaying == null ? null
                    : _mIsPlaying.Invoke(anim, new object[] { clip });
                if (playing is bool && (bool)playing) return;
                object state = _mClipOf == null ? null
                    : _mClipOf.Invoke(anim, new object[] { clip });
                if (state == null) return;
                LookAtState(state);
                if (_pWrapMode != null && _loopWrap != null)
                    _pWrapMode.SetValue(state, _loopWrap, null);
                if (_mCrossFade != null)
                    _mCrossFade.Invoke(anim, new object[] { clip, 0.35f });
                else if (_mPlay != null) _mPlay.Invoke(anim, new object[] { clip });
                // ... and out of step with the other man. Set AFTER the fade is
                // started: CrossFade restarts the clip at zero.
                if (phase > 0f && _pNormalizedTime != null)
                    _pNormalizedTime.SetValue(state, phase, null);
            }
            catch { }
        }

        // THE GUNNER'S WORDS, best first. FIELD 2026-09-19: "der gunner kann
        // auch eine passende animation dauerhaft ausfuehren aber sie muss zu
        // der position des target computers passen, also nicht das er da unten
        // irgendwo rumfummelt". The target computer is a box on the side of the
        // hull, at chest height, so a clip is only a candidate here if its name
        // reads like a man working IN FRONT OF HIM - and the ground-level
        // chores the first list was full of (berry picking was the order's own
        // example in 6.26, and it is a man on his knees) are now the operator's
        // business, not the gunner's.
        static readonly string[] WorkWords = {
            "panel", "console", "comput", "terminal", "radio", "device",
            "lever", "valve", "button", "repair", "fix", "weld",
            "hammer", "craft", "work"
        };

        // ... and the crouching man's, for the build where the crouch POSE does
        // not hold and Artillery/CrewOperatorClip is left empty. Best first
        // again: a real crouch clip beats a chore that merely happens near the
        // ground.
        static readonly string[] CrouchWords = {
            "crouch", "squat", "kneel", "knee", "sit", "berr", "bush",
            "gather", "harvest", "pick", "dig", "repair", "work"
        };

        // ... and what is never either of them, whatever else the name says.
        // "die" is deliberately not in the list: it is a substring of
        // "soldier".
        static readonly string[] NotWork = {
            "death", "dead", "hit", "shoot", "fire", "reload", "aim",
            "walk", "run", "crawl", "swim", "jump", "fall", "wound", "attack",
            "remove", "get_", "throw", "sleep", "guitar", "regen", "melee",
            "punch", "kick", "hook", "jab", "butt", "cough", "door",
            // Movement under another name: crouch_forward is a crouch-WALK and
            // would have the operator creeping on the spot. And handling the
            // rifle is not work either - "switch_weapon" would otherwise read
            // as a man switching something on.
            "forward", "backward", "strafe", "turn",
            "weapon", "equip", "holster"
        };

        // ... and what the GUNNER may never be given on top of that: a man at a
        // console does not reach for the ground.
        static readonly string[] NotUpright = {
            "crouch", "squat", "kneel", "knee", "sit", "lay", "lie", "prone",
            "berr", "bush", "gather", "harvest", "pick", "dig", "loot",
            "search", "ground", "floor", "grass", "mushroom", "herb", "plant",
            "seed", "water", "fish", "chop", "wood"
        };

        /// <summary>One man's looping clip: which words his name is judged by,
        /// which config key names one by hand, and what was decided once for
        /// the whole session. The two men keep SEPARATE answers - the gunner
        /// works at chest height and the operator squats, and one clip cannot
        /// be both.</summary>
        sealed class ClipPick
        {
            public readonly string Who;
            public readonly bool Gunner;
            public string Chosen;
            public bool Looked;

            public ClipPick(string who, bool gunner) { Who = who; Gunner = gunner; }

            public string Key { get { return Gunner ? "CrewWorkClip" : "CrewOperatorClip"; } }

            public string Want
            {
                get
                {
                    ConfigEntry<string> c = Gunner ? _cfgWorkClip : _cfgOperatorClip;
                    return c == null || c.Value == null ? "" : c.Value.Trim();
                }
            }

            public string[] Words { get { return Gunner ? WorkWords : CrouchWords; } }
        }

        // Every clip the crew's model carries, written to the log ONCE. Which
        // names this build uses is the one thing a sealed worktree cannot know
        // and a player at the vehicle can read off in a second - and with the
        // list in front of him, CrewWorkClip and CrewOperatorClip name the
        // right one without a new build.
        static bool _clipsLogged;

        /// <summary>The clip one of the men loops, decided once for the whole
        /// session off the first animation set we are shown.</summary>
        static string WorkClip(Component anim, ClipPick pick)
        {
            if (pick.Looked)
                return pick.Chosen != null && Has(anim, pick.Chosen) ? pick.Chosen : null;

            string want = pick.Want;
            if (want == "-")
            {
                pick.Looked = true;
                RevivalPlugin.L.LogInfo("ArtyBattery: " + pick.Key + " is \"-\" - the "
                    + pick.Who + " keeps whatever his state and pose give him.");
                return null;
            }
            if (want.Length > 0)
            {
                pick.Looked = true;
                pick.Chosen = Has(anim, want) ? want : null;
                if (pick.Chosen == null)
                    RevivalPlugin.L.LogWarning("ArtyBattery: no clip \"" + want
                        + "\" on the crew's model - the " + pick.Who + " stands still "
                        + "instead.");
                return pick.Chosen;
            }

            System.Collections.IEnumerable states = anim as System.Collections.IEnumerable;
            if (states == null) return null;
            string[] words = pick.Words;
            int best = words.Length;
            int seen = 0;
            bool readable = true;
            string all = "";
            foreach (object state in states)
            {
                LookAtState(state);
                if (_pStateName == null) { readable = false; break; }
                string name = _pStateName.GetValue(state, null) as string;
                if (name == null) continue;
                seen++;
                if (!_clipsLogged && all.Length < 1800)
                    all += (all.Length == 0 ? "" : ", ") + name;
                int rank = Rank(name, words, pick.Gunner);
                if (rank < 0 || rank >= best) continue;
                best = rank;
                pick.Chosen = name;
            }
            if (!readable)
            {
                // The set is there but its states will not tell us their names:
                // asking again every half second would only repeat that.
                pick.Looked = true;
                pick.Chosen = null;
                return null;
            }
            if (seen == 0) return null;          // an empty set: ask again later
            pick.Looked = true;
            if (!_clipsLogged && all.Length > 0)
            {
                _clipsLogged = true;
                RevivalPlugin.L.LogInfo("ArtyBattery: the crew's model carries these "
                    + seen + " clips - " + all + ". Artillery/CrewWorkClip (gunner) and "
                    + "CrewOperatorClip (drone operator) take one of these names.");
            }
            if (pick.Chosen == null)
                RevivalPlugin.L.LogInfo("ArtyBattery: none of the " + seen + " clips on "
                    + "the crew's model suits the " + pick.Who + " - he stands still at "
                    + "the gun. Artillery/" + pick.Key + " names one by hand.");
            else
                RevivalPlugin.L.LogInfo("ArtyBattery: the " + pick.Who + " loops the clip "
                    + "\"" + pick.Chosen + "\" (" + seen + " clips on the model).");
            return pick.Chosen;
        }

        /// <summary>Where a clip name stands in that man's word list, or -1
        /// when it is nothing he could be doing.</summary>
        static int Rank(string name, string[] words, bool upright)
        {
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < NotWork.Length; i++)
                if (lower.IndexOf(NotWork[i], StringComparison.Ordinal) >= 0) return -1;
            if (upright)
                for (int i = 0; i < NotUpright.Length; i++)
                    if (lower.IndexOf(NotUpright[i], StringComparison.Ordinal) >= 0) return -1;
            for (int i = 0; i < words.Length; i++)
                if (lower.IndexOf(words[i], StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        static bool Has(Component anim, string clip)
        {
            try
            {
                return _mClipOf != null
                    && _mClipOf.Invoke(anim, new object[] { clip }) != null;
            }
            catch { return false; }
        }

        // -------------------------------------------------------------- drone

        static float Orbit() { return Mathf.Clamp(F(_cfgOrbitRadius, 600f), 40f, 1500f); }

        /// <summary>The radius the drone is working at this instant.
        ///
        /// FIELD 2026-09-20: "sie soll agressiver scannen nicht nur wenn man
        /// direkt unter ihr ist". A fixed ring is the reason. Whatever the
        /// camera's footprint is widened to, a drone that only ever flies one
        /// circle only ever looks at that circle: at a 600 m orbit a man 200 m
        /// from the settlement centre was never seen at all, and the gun can
        /// reach him from 80 m out. So the ring breathes. The drone spirals in
        /// to OrbitRadius * (1 - OrbitSweep), back out to OrbitRadius, and the
        /// band it searches becomes the whole area the gun can shoot into.
        ///
        /// It stays a pure function of the shared clock and the settlement's
        /// own phase, which is the one property the whole drone depends on:
        /// every client computes the same aircraft without anybody sending
        /// anything.
        ///
        /// OrbitSweepLaps is not a whole number on purpose - see its own
        /// description. A rosette that closes is a rosette with permanent
        /// holes in it.</summary>
        static float OrbitAt(float angle)
        {
            float outer = Orbit();
            float sweep = Mathf.Clamp01(F(_cfgOrbitSweep, 0.55f));
            if (sweep <= 0.001f) return outer;
            float laps = Mathf.Clamp(F(_cfgSweepLaps, 1.618f), 0.25f, 20f);
            float half = outer * sweep * 0.5f;
            // Cosine, so the sweep starts at the outer edge: the line the
            // player learns to watch is where the drone is first seen.
            return outer - half + half * Mathf.Cos(angle / laps);
        }

        /// <summary>How far a bullet is allowed to reach the drone. It used to
        /// be a flat 600 m, which was a third again as far as the old 240-300 m
        /// ring; at a 600 m ring the same number would put the drone exactly on
        /// the edge for a player standing at his own gun, and the counterplay
        /// that was shipped with the drone's three hit points would be gone for
        /// the settlement it circles. So it follows the orbit and keeps a
        /// settlement's width on top of it.</summary>
        static float ShootReach() { return Mathf.Max(600f, Orbit() + 200f); }

        // The clock the drones actually fly on: the shared one, made smooth.
        static float _flightClock;
        static bool _flightClockSet;

        /// <summary>
        /// FIELD 2026-09-17: "the drone moves very jerkily when it circles, it
        /// does not look smooth from below". Two steps, one cause each.
        ///
        ///   1. THE CLOCK IS NOT CONTINUOUS. PhotonNetwork.time is an INTEGER
        ///      MILLISECOND count divided by 1000 (PhotonNetwork::get_time ->
        ///      get_ServerTimestamp in IL), and offline it is Environment
        ///      .TickCount, whose resolution on Windows is the system timer
        ///      tick - about 15.6 ms. A position derived straight from it
        ///      therefore steps about 64 times a second while the game draws 60
        ///      to 144 frames, so the drone holds still for a frame or two and
        ///      then jumps. Online the same number is a server offset that is
        ///      re-measured on every ping, which adds a jump in both directions.
        ///   2. IT IS ONLY THE PICTURE THAT NEEDS TO BE SMOOTH. The reason the
        ///      shared clock is used at all is that two clients must agree on
        ///      where the drone is. They still do: this clock is advanced by the
        ///      frame time and pulled back towards the shared one continuously,
        ///      so it tracks it to a few milliseconds - under a centimetre of
        ///      orbit at 16 m/s, against an aim error measured in tens of metres.
        ///
        /// A difference above a second is not drift, it is the room clock's own
        /// wrap at 100000 s or a client that was paused, and is taken in one step.
        /// </summary>
        static void AdvanceFlightClock()
        {
            float net = Clock();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            if (!_flightClockSet)
            {
                _flightClockSet = true;
                _flightClock = net;
                return;
            }
            _flightClock += dt;
            float drift = net - _flightClock;
            if (drift > 1f || drift < -1f) { _flightClock = net; return; }
            _flightClock += drift * Mathf.Clamp01(dt * 2f);
        }

        /// <summary>Where this drone is right now. Pure function of the shared
        /// clock and the settlement's own position, so every client gets the
        /// same answer without anybody sending anything. The ground under the
        /// orbit is measured four times a second, not every frame - GroundY
        /// casts a 3000 m ray - and the height the drone actually holds is
        /// eased onto that measurement instead of stepping onto it, because a
        /// step every quarter second over rolling ground is the second half of
        /// the reported stutter.</summary>
        static Vector3 DronePoint(Post p, float now)
        {
            // The ANGLE turns at the nominal radius, on the smoothed clock -
            // verify.py reads this expression to prove the orbit never went
            // back onto the raw network clock, so leave it as it stands. The
            // radius the drone is actually at is then the swept one, which is
            // why r is read twice: once as the circle the angle belongs to,
            // once as the spiral the aircraft flies.
            float r = Orbit();
            float speed = Mathf.Clamp(F(_cfgOrbitSpeed, 16f), 1f, 60f);
            float a = p.Phase + _flightClock * speed / r;
            r = OrbitAt(a);
            Vector3 flat = new Vector3(p.Centre.x + Mathf.Cos(a) * r, 0f,
                                       p.Centre.z + Mathf.Sin(a) * r);
            if (!p.GroundSet || now - p.GroundAt > 0.25f)
            {
                p.GroundAt = now;
                float y;
                // THE TERRAIN, NOT WHAT IS STANDING ON IT. GroundY casts a ray
                // with no layer mask, so a roof, a truck or a treetop under the
                // orbit answers as "ground" and the drone hops over every
                // building it passes - even the old 240 m ring passed several a
                // lap, and a 600 m one crosses more ground still.
                // TerrainHeight reads the height data, which is the ground and
                // nothing else, costs no ray at all, and is the same number
                // away from every player (E-059). The ray is only the fallback
                // for a scene that has no terrain.
                if (!RevivalTroopInsertion.TerrainHeight(flat, out y)
                    && !RevivalTroopInsertion.GroundY(flat, out y)) y = p.Centre.y;
                p.GroundWant = y;
                if (!p.GroundSet) { p.GroundSet = true; p.Ground = y; }
            }
            p.Ground = Mathf.Lerp(p.Ground, p.GroundWant,
                                  Mathf.Clamp01(Mathf.Min(Time.unscaledDeltaTime, 0.25f) * 4f));
            flat.y = p.Ground + Mathf.Clamp(F(_cfgOrbitHeight, 85f), 20f, 400f);
            return flat;
        }

        static void Fly(Post p, float now, bool havePlayer, Vector3 mine)
        {
            DroneState(p, now);
            bool want = B(_cfgDrone, true) && p.DroneHits > 0 && OperatorFlies(p);
            p.DroneUp = want;
            if (!want)
            {
                if (p.DroneModel != null)
                {
                    UnityEngine.Object.Destroy(p.DroneModel);
                    p.DroneModel = null;
                }
                return;
            }

            p.DroneAt = DronePoint(p, now);

            // A model only where somebody could see it. The orbit itself is four
            // lines of arithmetic and is computed everywhere, so the map and the
            // spotting do not care whether a GameObject exists.
            //
            // The two distances differ by a tenth on purpose. A single threshold
            // is a coin toss for a player standing exactly at it: the model is
            // built and destroyed on alternate frames, and the drone flickers.
            float range = Mathf.Max(100f, F(_cfgModelRange, 1000f));
            float d = havePlayer ? Flat(p.DroneAt - mine) : float.MaxValue;
            bool near = p.DroneModel != null ? d <= range * 1.1f : d <= range;
            if (!near)
            {
                if (p.DroneModel != null)
                {
                    UnityEngine.Object.Destroy(p.DroneModel);
                    p.DroneModel = null;
                }
                return;
            }
            if (p.DroneModel == null) p.DroneModel = BuildDrone();
            if (p.DroneModel == null) return;
            p.DroneModel.transform.position = p.DroneAt;
            // Nose along the circle: the tangent is the direction it flies.
            Vector3 radial = p.DroneAt - p.Centre;
            radial.y = 0f;
            Vector3 tangent = new Vector3(-radial.z, 0f, radial.x);
            if (tangent.sqrMagnitude > 0.01f)
                p.DroneModel.transform.rotation =
                    Quaternion.LookRotation(tangent.normalized, Vector3.up);
        }

        /// <summary>Is there anybody left to fly it? On the master that is the
        /// operator himself; a joined client never spawned him and cannot tell
        /// the two men apart, so it asks whether ANY of the gun's men is still
        /// standing. The two answers differ only in the seconds between the
        /// operator's death and the gunner's, and the drone is a dot in the sky
        /// either way.</summary>
        static bool OperatorFlies(Post p)
        {
            if (p.Safe) return false;                // a trader camp keeps no drone up
            // A settlement nobody has reached yet has its operator BY
            // ASSUMPTION: the men are spawned at the vehicle, the vehicle is
            // built when a player comes close, and until then counting living
            // crew at a gun that does not exist would answer "none" and ground
            // every drone on the map - which is the very bug this is for.
            if (p.Ghost) return true;
            if (!B(_cfgCrew, true)) return true;     // no crew asked for: the drone is the battery
            if (p.CrewSettlement != null) return Alive(p.Operator);
            return p.MenNear > 0;
        }

        static bool GunnerServes(Post p)
        {
            if (!B(_cfgCrew, true)) return true;
            if (p.CrewSettlement != null) return Alive(p.Gunner);
            return p.MenNear > 0;
        }

        static bool _modelBroken;

        // Room properties survive late joins and master-client changes. The
        // master is the only writer; local model culling never resets health.
        static void DroneState(Post p, float now)
        {
            if (now < p.NextDroneState) return;
            p.NextDroneState = now + 0.5f;
            int previousHits = p.DroneHits;
            double[] state = ArtyRoom.Read(p.DroneKey);
            if (state != null)
            {
                p.DroneHits = (int)state[0];
                p.DroneReadyAt = state[1];
            }
            if (ReplacementDue(p.DroneHits, p.DroneReadyAt, ArtyRoom.Now())
                && RevivalTroopInsertion.MasterClient() && OperatorFlies(p))
            {
                p.DroneHits = 3;
                ArtyRoom.Write(p.DroneKey, p.DroneHits, p.DroneReadyAt);
            }
            if (p.DroneHits <= 0 && previousHits > 0) CancelRecon(p);
        }

        internal static bool ReplacementDue(int hits, double readyAt, double now)
        {
            return hits <= 0 && ArtyRoom.Elapsed(now, readyAt) >= 0.0;
        }

        static void CancelRecon(Post p)
        {
            p.Sighting = false;
            p.SeenSince = 0f;
            p.Candidate = null;
            p.LocalSpotAt = 0f;
            _marks.Clear();
            _mapOpen = false;
        }

        internal static void Shoot(Vector3 from, Vector3 direction)
        {
            if (!Shootable) return;
            Post best = null;
            float nearest = ShootReach();
            direction.Normalize();
            // Both lists: a drone the player can see is a drone he can shoot,
            // and half of them belong to settlements he has never entered.
            Aimed(_posts, from, direction, ref best, ref nearest);
            Aimed(_ghosts, from, direction, ref best, ref nearest);
            if (best == null) return;
            if (RevivalTroopInsertion.MasterClient())
                HitDrone(best.DroneKey, from, direction, best.DroneReadyAt, ArtyRoom.Now());
            else Mortar.Net.SendArty(new object[] { "arty-v1", 1, best.DroneKey,
                new float[] { from.x, from.y, from.z, direction.x, direction.y, direction.z },
                best.DroneReadyAt, ArtyRoom.Now() });
        }

        /// <summary>The nearest drone of one list the shot line passes through,
        /// kept only if it beats what the caller already had.</summary>
        static void Aimed(List<Post> list, Vector3 from, Vector3 direction,
                          ref Post best, ref float nearest)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Post p = list[i];
                if (!p.DroneUp || p.DroneHits <= 0) continue;
                float d;
                if (!DroneRay(from, direction, p.DroneAt, out d) || d >= nearest) continue;
                nearest = d;
                best = p;
            }
        }

        /// <summary>The post, ghost or real, whose drone lives under this room
        /// key. The key is the settlement centre, so exactly one of them can
        /// hold it (GunRaised retires the ghost before it takes over).</summary>
        static Post ByKey(string key)
        {
            for (int i = 0; i < _posts.Count; i++)
                if (_posts[i].DroneKey == key) return _posts[i];
            for (int i = 0; i < _ghosts.Count; i++)
                if (_ghosts[i].DroneKey == key) return _ghosts[i];
            return null;
        }

        static bool DroneRay(Vector3 from, Vector3 direction, Vector3 at, out float distance)
        {
            Vector3 delta = at - from;
            distance = Vector3.Dot(delta, direction);
            float radius = Mathf.Clamp(F(_cfgModelScale, 10f) * 0.14f, 0.4f, 4f);
            if (distance < 1f || distance > ShootReach()
                || (delta - direction * distance).sqrMagnitude > radius * radius) return false;
            // World geometry blocks fire. Start past the camera/body, and stop
            // at the airframe's near surface rather than its centre.
            return !Physics.Raycast(from + direction * 0.5f, direction,
                Mathf.Max(0f, distance - radius - 0.5f), ~0, QueryTriggerInteraction.Ignore);
        }

        internal static void HitDrone(string key, Vector3 from, Vector3 direction, double generation, double shotTime)
        {
            if (!Shootable || !RevivalTroopInsertion.MasterClient()) return;
            if (!ArtyRoom.Finite(from) || !ArtyRoom.Finite(direction)
                || direction.sqrMagnitude < 0.9f || direction.sqrMagnitude > 1.1f) return;
            Post p = ByKey(key);
            if (p == null) return;
            p.NextDroneState = 0f;
            DroneState(p, Time.time);
            if (p.DroneHits <= 0 || !OperatorFlies(p) || generation != p.DroneReadyAt) return;
            float distance;
            double age = ArtyRoom.Elapsed(ArtyRoom.Now(), shotTime);
            if (double.IsNaN(age) || age < -0.1 || age > 0.75) return;
            // Rewind the deterministic orbit to the shooter's timestamp.
            // Keep the measured terrain height: only a sub-second correction.
            float speed = Mathf.Clamp(F(_cfgOrbitSpeed, 16f), 1f, 60f);
            float angle = (float)Math.Max(0.0, age) * speed / Orbit();
            // The sweep is part of where the drone was, not just the bearing:
            // rewind the radius by the same age. It is under two metres over
            // the 0.75 s this method accepts, but it costs one multiplication
            // and the hit sphere is not much bigger than that.
            float here = p.Phase + _flightClock * speed / Orbit();
            float scale = OrbitAt(here - angle) / Mathf.Max(1f, OrbitAt(here));
            Vector3 radial = p.DroneAt - p.Centre;
            Vector3 rewind = new Vector3(
                (radial.x * Mathf.Cos(angle) + radial.z * Mathf.Sin(angle)) * scale,
                radial.y,
                (-radial.x * Mathf.Sin(angle) + radial.z * Mathf.Cos(angle)) * scale) + p.Centre;
            if (!DroneRay(from, direction, rewind, out distance)) return;
            p.DroneHits--;
            if (p.DroneHits == 0)
            {
                p.DroneReadyAt = ArtyRoom.Now() + 1800.0;
                p.DroneUp = false;
                CancelRecon(p);
                RevivalPlugin.L.LogInfo("ArtyBattery: recon down at " + p.Name
                    + "; replacement in 1800 seconds, only with a living operator.");
            }
            ArtyRoom.Write(p.DroneKey, p.DroneHits, p.DroneReadyAt);
        }

        /// <summary>The recon airframe, the same one the player's own
        /// surveillance drone flies. A failure is said ONCE and then never
        /// tried again: this runs every frame for every battery in range, and a
        /// warning a frame would bury the log that explains it.</summary>
        static GameObject BuildDrone()
        {
            if (_modelBroken) return null;
            try
            {
                GameObject go = Drone.Modell.Bauen();
                go.name = "NDR_ArtyReconDrone";
                float s = Mathf.Clamp(F(_cfgModelScale, 10f), 1f, 40f);
                go.transform.localScale = new Vector3(s, s, s);
                return go;
            }
            catch (Exception ex)
            {
                _modelBroken = true;
                RevivalPlugin.L.LogWarning("ArtyBattery: no recon drone model (" + ex.Message
                    + ") - the drones still fly and still spot, they are simply not "
                    + "drawn in the world.");
                return null;
            }
        }

        // ----------------------------------------------------------- spotting

        /// <summary>The local player's own warning. Every client runs this for
        /// ITSELF - it needs no authority, it is the only way a joined client
        /// learns that it is being watched, and it is what puts the mark on his
        /// map.</summary>
        static void Warn(Post p, float now, GameObject me, Vector3 mine)
        {
            if (me == null || !p.DroneUp) return;
            if (!HostileToBattery(p, PlayerFaction(me), true)) return;
            if (Flat(p.DroneAt - mine) > SpotRadius()) return;
            p.LocalSpotAt = now;
            p.LocalSpotPoint = mine;
            if (now < p.NextWarn) return;
            p.NextWarn = now + 25f;
            Turret.Hinweis(Mortar.TextSpotted(), 3.5f);
        }

        static float SpotRadius() { return Mathf.Clamp(F(_cfgSpotRadius, 300f), 20f, 600f); }

        /// <summary>Could the gun reach that point at all? Asked BEFORE the
        /// sighting rather than after it: a man the gun cannot touch is not a
        /// target, and reporting him would only put the battery through the
        /// whole drill for a refusal at the end of it. The dead zone under the
        /// gun matters here - a drone whose orbit passes over its own settlement
        /// would otherwise keep reporting people standing next to the
        /// vehicle.</summary>
        static bool InReach(Post p, Vector3 at)
        {
            if (p.Gun == null) return false;
            float d = Flat(at - p.Gun.transform.position);
            return d >= Mortar.MinRange && d <= Mortar.MaxRange;
        }

        /// <summary>Master only: what the drone sees and hands to the gunner.
        /// One candidate at a time - a battery has one gun, and a spotter who
        /// keeps changing his mind never gets a mission off.</summary>
        static void Spot(Post p, float now)
        {
            if (p.Safe || !B(_cfgAutoFire, true)) return;
            if (p.Sighting || !p.DroneUp) return;
            if (now < p.NextScan) return;
            p.NextScan = now + 0.5f;
            if (now < p.NextMissionAt) return;

            float radius = SpotRadius();
            Vector3 found = Vector3.zero;
            bool have = false;
            GameObject target = null;
            Component targetNpc = null;

            // Players first: they are the point of the whole feature, and the
            // list is two field reads rather than a scene walk.
            List<GameObject> players = Mortar.PlayerList();
            for (int i = 0; i < players.Count && !have; i++)
            {
                GameObject go = players[i];
                if (go == null) continue;
                Vector3 at = go.transform.position;
                if (Flat(p.DroneAt - at) > radius) continue;
                if (!InReach(p, at)) continue;
                if (!HostileToBattery(p, PlayerFaction(go), true)) continue;
                found = at;
                target = go;
                have = true;
            }
            // Then the NPCs the drone can see - a squad that landed out there is
            // as good a target as a player, and the order asked for both.
            for (int i = 0; i < _npcs.Count && !have; i++)
            {
                Component ai = _npcs[i];
                if (ai == null) continue;
                if (ReferenceEquals(ai, p.Gunner) || ReferenceEquals(ai, p.Operator)) continue;
                Vector3 at = ai.transform.position;
                if (Flat(p.DroneAt - at) > radius) continue;
                if (!InReach(p, at)) continue;
                if (!HostileToBattery(p, FactionOf(ai), false)) continue;
                found = at;
                target = ai.gameObject;
                targetNpc = ai;
                have = true;
            }

            // FIELD 2026-09-21: "erkennt immer noch nicht zuverlaessig, teilweise
            // sogar regression". A drone circles at 16 m/s: it swings its own
            // footprint across a stationary man in a couple of scans, a rise in
            // the ground can drop him off a single 0.5 s sample, and the NPC
            // scan list is walked in the same order every tick, so a single
            // frame where a closer hostile briefly wins the "one candidate"
            // slot used to zero the timer outright. A camera does not forget a
            // man it saw half a second ago, so losing him for one scan is not
            // losing him - only losing him for a real interval is.
            const float ContactGrace = 1.5f;      // three scans at the 0.5 s cadence
            if (!have)
            {
                if (p.LastSeenAt > 0f && now - p.LastSeenAt <= ContactGrace) return;
                p.SeenSince = 0f;
                p.Candidate = null;
                p.LastSeenAt = 0f;
                return;
            }
            p.LastSeenAt = now;

            // A man has to stay under the drone before the operator is sure of
            // him. Somebody who crosses the edge of the footprint is not a
            // sighting, and without this the battery would fire at every shadow.
            if (p.Candidate != target || p.SeenSince <= 0f || Flat(found - p.SeenAt) > 45f)
            {
                p.Candidate = target;
                p.SeenSince = now;
                p.SeenAt = found;
                return;
            }
            p.SeenAt = found;
            if (now - p.SeenSince < Mathf.Max(0f, F(_cfgSpotSeconds, 3f))) return;

            p.Sighting = true;
            p.Target = target;
            p.TargetNpc = targetNpc;
            p.Point = found;
            p.SeenSince = 0f;
            // ONE error for the whole mission, drawn now: the shells that follow
            // add the gun's own dispersion around this offset point, so the
            // salvo beats an area near the man instead of on him.
            Vector2 e = UnityEngine.Random.insideUnitCircle * Mathf.Max(0f, F(_cfgAimError, 28f));
            p.Error = e;
            p.ReportAt = now + Mathf.Max(0f, F(_cfgReportDelay, 9f))
                + UnityEngine.Random.value * Mathf.Max(0f, F(_cfgReportJitter, 5f));
            RevivalPlugin.L.LogInfo("ArtyBattery: \"" + p.Name + "\" drone reports "
                + found.ToString("0") + ", gun laid in "
                + (p.ReportAt - now).ToString("0.0") + " s, aim error "
                + e.magnitude.ToString("0") + " m.");
        }

        /// <summary>Master only: the report reaches the gunner, he lays the gun
        /// and fires the moment it is on. The turret's travel is a real part of
        /// the delay - a target across the settlement waits longer than one in
        /// front of the muzzle.</summary>
        static void Mission(Post p, float now)
        {
            if (!p.Sighting) return;
            // Recheck allegiance after the report delay, before laying/firing.
            if (p.Target == null || !HostileToBattery(p, p.TargetNpc == null
                ? PlayerFaction(p.Target) : FactionOf(p.TargetNpc), p.TargetNpc == null))
            { p.Sighting = false; return; }
            if (now < p.ReportAt) return;
            // A report that could not be answered inside a minute is stale. The
            // turret needs twenty seconds for a half turn, so nothing legitimate
            // reaches this - what does is a gun the player held the sight of
            // while the mission waited, and a sighting nobody ever clears would
            // block every later report from that battery for good.
            if (now > p.ReportAt + 60f)
            {
                p.Sighting = false;
                p.NextMissionAt = now + 10f;
                return;
            }
            if (!GunnerServes(p)) { p.Sighting = false; return; }
            if (Mortar.PlayerAiming(p.SettlementId)) return;   // the player has the sight
            if (p.Rounds <= 0)
            {
                p.Sighting = false;
                p.NextMissionAt = now + 20f;
                return;
            }

            Vector3 point = new Vector3(p.Point.x + p.Error.x, p.Point.y,
                                        p.Point.z + p.Error.y);
            float y;
            if (RevivalTroopInsertion.GroundY(point, out y)) point.y = y;

            Mortar.Lay(p.SettlementId, point);
            if (!Mortar.Laid(p.SettlementId, point)) return;   // still turning

            int fired = Mortar.NpcFire(p.SettlementId, point, p.Rounds);
            p.Sighting = false;
            if (fired <= 0)
            {
                p.NextMissionAt = now + 10f;
                return;
            }
            p.Rounds -= fired;
            p.NextMissionAt = now + Mathf.Max(5f, F(_cfgCooldown, 40f));
        }

        static void Resupply(Post p, float now)
        {
            int cap = Mathf.Clamp(_cfgMagazine == null ? 14 : _cfgMagazine.Value, 0, 60);
            if (p.Rounds >= cap) { p.NextShell = 0f; return; }
            float per = Mathf.Max(5f, F(_cfgResupply, 120f));
            if (p.NextShell <= 0f) { p.NextShell = now + per; return; }
            if (now < p.NextShell) return;
            p.NextShell = now + per;
            p.Rounds++;
        }

        // ----------------------------------------------------- faction reading

        /// <summary>Is this faction one the battery shoots at? The battery's own
        /// hated list is the gunner's. Unknown identities are never targets,
        /// and an explicit own-side match wins over even a malformed hated list.</summary>
        static bool HostileToBattery(Post p, object faction, bool isPlayer)
        {
            Component owner = p.Gunner != null ? p.Gunner : p.Operator;
            if (owner == null) owner = SettlementMan(p);
            return EnemyFaction(FactionOf(owner), HatedOf(owner), faction);
        }

        internal static bool EnemyFaction(object own, Array hated, object target)
        {
            if (own == null || target == null || hated == null) return false;
            try { if (Convert.ToInt32(own) == Convert.ToInt32(target)) return false; }
            catch { return false; }
            return Hostile(hated, target);
        }

        /// <summary>The same test as NPC_AI2.IsEnemyFraction: is that faction in
        /// this hated list? Compared as NUMBERS, not with Equals. An NPC's side
        /// is the Fraction enum and a player's comes out of PlayerInfo.fraction,
        /// which the game may hold as the enum or as a plain int - and boxed,
        /// Equals between the two is false whatever the value is. That would
        /// leave a battery that never fires at anybody and no line in the log to
        /// say why.</summary>
        static bool Hostile(Array hated, object faction)
        {
            if (hated == null || faction == null) return false;
            int want;
            try { want = Convert.ToInt32(faction); }
            catch { return false; }
            for (int i = 0; i < hated.Length; i++)
            {
                object h = hated.GetValue(i);
                if (h == null) continue;
                try { if (Convert.ToInt32(h) == want) return true; }
                catch { if (h.Equals(faction)) return true; }
            }
            return false;
        }

        static Type _npcType;
        static Type _optType;
        static MethodInfo _mIsAlive;
        static FieldInfo _fOptions, _fMyFraction, _fHated;
        static bool _looked;

        // The posted crew. NPCMainState: Idle 0 ... Working 10.
        // NPCAdditionalState: Empty 0. NPCPoseState: Normal 0, Crouch 1,
        // Crawl 2 (CONFIRMED IL, REVERSE_ENGINEERING).
        const int MainIdle = 0;
        const int MainWorking = 10;
        const int AddEmpty = 0;
        const int PoseStand = 0;
        const int PoseCrouch = 1;

        static FieldInfo _fMainState, _fAddState, _fPoseState, _fUseTemp, _fTempTask;
        static FieldInfo _fNavAgent;
        static MethodInfo _mStateSync, _mClearIntentions, _mPauseTime;
        static bool _stateWarned;

        static bool Look()
        {
            if (_looked) return _npcType != null;
            _looked = true;
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            _optType = RevivalPlugin.TypeByName("NPCMainOptions");
            if (_npcType == null)
            {
                RevivalPlugin.L.LogWarning("ArtyBattery: NPC_AI2 not found - the guns "
                    + "stay unmanned and nothing is spotted.");
                return false;
            }
            _mIsAlive = AccessTools.Method(_npcType, "IsAlive", null, null);
            _fOptions = AccessTools.Field(_npcType, "MainOptions");
            if (_optType != null)
            {
                _fMyFraction = AccessTools.Field(_optType, "MyFraction");
                _fHated = AccessTools.Field(_optType, "HatedFractions");
            }
            if (_fOptions == null || _fMyFraction == null || _fHated == null)
                RevivalPlugin.L.LogWarning("ArtyBattery: NPCMainOptions.MyFraction or "
                    + "HatedFractions missing - the crew keeps the configured side and "
                    + "only players are spotted.");

            // ... and what it takes to post the two men at the vehicle.
            _fMainState = AccessTools.Field(_npcType, "MainState");
            _fAddState = AccessTools.Field(_npcType, "AdditionalState");
            _fPoseState = AccessTools.Field(_npcType, "PoseState");
            _fUseTemp = AccessTools.Field(_npcType, "_useTemporaryWalkPoints");
            _fTempTask = AccessTools.Field(_npcType, "TemporaryTask");
            _fNavAgent = AccessTools.Field(_npcType, "_navAgent");
            if (_fNavAgent != null && !typeof(NavMeshAgent).IsAssignableFrom(_fNavAgent.FieldType))
                _fNavAgent = null;
            _mStateSync = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            _mClearIntentions = AccessTools.Method(_npcType, "ClearIntentions",
                Type.EmptyTypes, null);
            _mPauseTime = AccessTools.Method(_npcType, "SetPauseTime",
                new Type[] { typeof(float) }, null);
            if (_mStateSync == null || _mPauseTime == null)
                RevivalPlugin.L.LogWarning("ArtyBattery: NPC_AI2.SetStateWithAnimAndSync "
                    + "or SetPauseTime missing - the crew cannot be held at the gun and "
                    + "walks the ring its squad was given.");
            return true;
        }

        static int IntField(Component ai, FieldInfo f, int fallback)
        {
            if (ai == null || f == null) return fallback;
            try
            {
                object v = f.GetValue(ai);
                return v == null ? fallback : Convert.ToInt32(v);
            }
            catch { return fallback; }
        }

        static NavMeshAgent Agent(Component ai)
        {
            if (ai == null || !Look() || _fNavAgent == null) return null;
            try { return _fNavAgent.GetValue(ai) as NavMeshAgent; }
            catch { return null; }
        }

        // The legacy Animation component the game's NPCs are animated with
        // (NPC_AI2.SwitchAnimationByStates calls Animation.CrossFade and
        // Animation.Blend). build.ps1 references no AnimationModule, so the
        // type is reached by name - once, and then cached like every other.
        static Type _animType;
        static MethodInfo _mIsPlaying, _mCrossFade, _mPlay, _mClipOf;
        static PropertyInfo _pStateName, _pWrapMode, _pNormalizedTime;
        static object _loopWrap;
        static bool _animLooked;
        static bool _animWarned;

        /// <summary>The man's own animation set, or null when this build keeps
        /// its NPCs on something else - in which case the crew simply stands.</summary>
        static Component AnimationOf(Component ai)
        {
            if (ai == null) return null;
            if (!_animLooked)
            {
                _animLooked = true;
                _animType = RevivalPlugin.TypeByName("UnityEngine.Animation");
                if (_animType != null)
                {
                    _mIsPlaying = AccessTools.Method(_animType, "IsPlaying",
                        new Type[] { typeof(string) }, null);
                    _mCrossFade = AccessTools.Method(_animType, "CrossFade",
                        new Type[] { typeof(string), typeof(float) }, null);
                    _mPlay = AccessTools.Method(_animType, "Play",
                        new Type[] { typeof(string) }, null);
                    _mClipOf = AccessTools.Method(_animType, "get_Item",
                        new Type[] { typeof(string) }, null);
                }
                if (_animType == null || _mClipOf == null)
                    RevivalPlugin.L.LogWarning("ArtyBattery: UnityEngine.Animation or its "
                        + "clip lookup is missing - the crew stands at the gun without a "
                        + "working loop.");
            }
            if (_animType == null || _mClipOf == null) return null;
            try { return ai.GetComponentInChildren(_animType); }
            catch { return null; }
        }

        /// <summary>The three AnimationState members this needs, read off the
        /// first state object the set hands out.</summary>
        static void LookAtState(object state)
        {
            if (state == null || _pStateName != null) return;
            try
            {
                Type t = state.GetType();
                _pStateName = t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                _pWrapMode = t.GetProperty("wrapMode", BindingFlags.Public | BindingFlags.Instance);
                _pNormalizedTime = t.GetProperty("normalizedTime",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_pWrapMode != null)
                {
                    // WrapMode.Loop. By name, because a number is a guess.
                    try { _loopWrap = Enum.Parse(_pWrapMode.PropertyType, "Loop"); }
                    catch { _loopWrap = null; }
                }
            }
            catch (Exception ex)
            {
                if (_animWarned) return;
                _animWarned = true;
                RevivalPlugin.L.LogWarning("ArtyBattery: AnimationState cannot be read ("
                    + ex.Message + ") - the crew stands still instead of working.");
            }
        }

        static object Options(Component ai)
        {
            if (ai == null || !Look() || _fOptions == null) return null;
            try { return _fOptions.GetValue(ai); }
            catch { return null; }
        }

        static object FactionOf(Component ai)
        {
            object opt = Options(ai);
            if (opt == null || _fMyFraction == null) return null;
            try { return _fMyFraction.GetValue(opt); }
            catch { return null; }
        }

        static Array HatedOf(Component ai)
        {
            object opt = Options(ai);
            if (opt == null || _fHated == null) return null;
            try { return _fHated.GetValue(opt) as Array; }
            catch { return null; }
        }

        static object LocalFaction()
        {
            GameObject me = MapTools.LocalPlayer();
            return me == null ? null : PlayerFaction(me);
        }

        static MethodInfo _getInfo;
        static Type _statsType;
        static FieldInfo _fFraction;
        static PropertyInfo _pFraction;
        static bool _statsLooked;

        /// <summary>A player's faction as the Fraction enum value the NPC lists
        /// are written in - the same field the mortar's own faction net reads
        /// (player.GetComponent(PlayerStatisticsManager).GetPlayerInfo().fraction).</summary>
        static object PlayerFaction(GameObject player)
        {
            if (player == null) return null;
            try
            {
                if (!_statsLooked)
                {
                    _statsLooked = true;
                    _statsType = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                    if (_statsType != null)
                    {
                        _getInfo = AccessTools.Method(_statsType, "GetPlayerInfo", Type.EmptyTypes, null);
                        if (_getInfo != null)
                        {
                            Type ret = _getInfo.ReturnType;
                            _fFraction = AccessTools.Field(ret, "fraction");
                            if (_fFraction == null) _fFraction = AccessTools.Field(ret, "Fraction");
                            if (_fFraction == null)
                                _pFraction = ret.GetProperty("fraction",
                                    BindingFlags.Public | BindingFlags.Instance);
                        }
                    }
                    if (_getInfo == null || (_fFraction == null && _pFraction == null))
                        RevivalPlugin.L.LogWarning("ArtyBattery: a player's faction cannot be "
                            + "read - unknown players are excluded from artillery targets.");
                }
                if (_getInfo == null) return null;
                Component stats = player.GetComponent(_statsType);
                if (stats == null) return null;
                object info = _getInfo.Invoke(stats, null);
                if (info == null) return null;
                if (_fFraction != null) return _fFraction.GetValue(info);
                if (_pFraction != null) return _pFraction.GetValue(info, null);
                return null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------ plumbing

        static void ScanNpcs(float now)
        {
            if (now < _nextNpcScan) return;
            _nextNpcScan = now + 1.5f;
            _npcStamp++;
            _npcs.Clear();
            if (!Look()) return;
            try
            {
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_npcType);
                for (int i = 0; i < all.Length; i++)
                {
                    Component ai = all[i] as Component;
                    if (ai == null || ai.gameObject == null) continue;
                    if (!Alive(ai)) continue;
                    _npcs.Add(ai);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ArtyBattery: NPC scan: " + ex.Message);
            }
        }

        static MethodInfo _clockGetter;
        static bool _clockLooked;

        /// <summary>
        /// The clock every client agrees on. PhotonNetwork.time is the room's
        /// own server time in seconds, the same number on every machine in the
        /// session, which is exactly what a drone position derived from nothing
        /// but arithmetic needs. Time.time is the fallback and is per-client, so
        /// without Photon two players see the same drone on different parts of
        /// the same circle - harmless, because nothing but the picture depends
        /// on it. The modulo keeps a four-day-old room inside float precision.
        /// </summary>
        static float Clock()
        {
            try
            {
                if (!_clockLooked)
                {
                    _clockLooked = true;
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null)
                        _clockGetter = AccessTools.PropertyGetter(photon, "time");
                }
                if (_clockGetter != null)
                {
                    object v = _clockGetter.Invoke(null, null);
                    if (v is double)
                    {
                        double d = (double)v;
                        if (d > 0.0) return (float)(d % 100000.0);
                    }
                }
            }
            catch { }
            return Time.time;
        }

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        // ---------------------------------------------------------- the map

        /// <summary>
        /// Refresh visible batteries and sightings at the map polling rate.
        /// Icons follow the existing flight state on every repaint.
        /// </summary>
        static void MapSnapshot(float now)
        {
            if (now < _nextMapCheck) return;
            _nextMapCheck = now + 0.2f;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            bool open = MapTools.Context(out manager, out texture, out cam, out world, out map);
            if (!open)
            {
                if (_mapOpen) _marks.Clear();
                _mapOpen = false;
                return;
            }
            _mapOpen = open;

            _marks.Clear();
            for (int i = 0; i < _posts.Count; i++)
            {
                Post p = _posts[i];
                if (!p.DroneUp) continue;
                Mark m = new Mark();
                m.Source = p;
                m.Centre = p.Centre;
                m.Spotted = p.LocalSpotAt > 0f && now - p.LocalSpotAt < 90f;
                m.SpotPoint = p.LocalSpotPoint;
                _marks.Add(m);
            }
            // The drones of the settlements the player has not been to. They
            // carry no sighting: a ghost never warns, because its battery has
            // nothing to fire (GunExpected).
            for (int i = 0; i < _ghosts.Count; i++)
            {
                Post g = _ghosts[i];
                if (!g.DroneUp) continue;
                Mark m = new Mark();
                m.Source = g;
                m.Centre = g.Centre;
                _marks.Add(m);
            }
        }

        // IMGUI drone marks are not visible to the native-widget scan. Reserve
        // the same icon bounds before patrol and convoy names are placed.
        internal static void ReserveMapLabels(MapLabels labels, Component texture,
            Camera camera, Vector2 world, Vector2 map)
        {
            if (!Enabled) return;
            for (int i = 0; i < _marks.Count; i++)
            {
                Mark mark = _marks[i];
                if (!mark.Source.DroneUp) continue;
                if (Mathf.Abs(mark.Centre.x) > world.x * .75f || Mathf.Abs(mark.Centre.z) > world.y * .75f) continue;
                Vector2 point;
                if (MapTools.WorldToGui(mark.Source.DroneAt, texture, camera, world, map, out point))
                    labels.BlockScreen(DroneIconRect(point));
                if (mark.Spotted && MapTools.WorldToGui(mark.SpotPoint, texture, camera, world, map, out point))
                    labels.BlockScreen(new Rect(point.x - 10f, point.y - 10f, 20f, 20f));
            }
        }

        public static void Draw()
        {
            if (!Enabled || _marks.Count == 0) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try
            {
                Component manager, texture;
                Camera cam;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out cam, out world, out map))
                    return;

                Rect clip;
                if (!MapTools.MapScreenRect(texture, cam, out clip)) return;
                Rect view;
                if (MapTools.MapViewportRect(texture, cam, out view)) clip = Intersect(clip, view);
                if (clip.width < 2f || clip.height < 2f) return;

                Color old = GUI.color;
                GUI.BeginClip(clip);
                try
                {
                    for (int i = 0; i < _marks.Count; i++)
                        DrawMark(_marks[i], texture, cam, world, map, clip);
                }
                finally
                {
                    GUI.EndClip();
                    GUI.color = old;
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ArtyBattery.Draw: " + ex);
            }
        }

        /// <summary>
        /// A compact quadcopter follows the drone, plus the point where this
        /// player was last seen from the air. Icon size stays fixed on screen.
        /// </summary>
        static void DrawMark(Mark m, Component texture, Camera cam,
                             Vector2 world, Vector2 map, Rect clip)
        {
            if (!m.Source.DroneUp) return;
            // A drone whose settlement is not on this map at all (an interior
            // scene has its own, much smaller map) is not drawn.
            if (Mathf.Abs(m.Centre.x) > world.x * 0.75f
                || Mathf.Abs(m.Centre.z) > world.y * 0.75f) return;

            Vector2 dot;
            if (MapTools.WorldToGui(m.Source.DroneAt, texture, cam, world, map, out dot))
            {
                GUI.color = Color.white;
                GUI.DrawTexture(DroneIconRect(dot - clip.position), DroneIcon());
            }

            // Where the drone last had this player. A cross inside a ring, so it
            // reads as "they know about this spot" rather than as a waypoint.
            if (!m.Spotted) return;
            Vector2 s;
            if (!MapTools.WorldToGui(m.SpotPoint, texture, cam, world, map, out s)) return;
            Vector2 q = s - clip.position;
            GUI.color = new Color(1f, 0.55f, 0.20f, 0.90f);
            GUI.DrawTexture(new Rect(q.x - 9f, q.y - 9f, 18f, 18f), Ring());
            GUI.DrawTexture(new Rect(q.x - 6f, q.y - 1f, 13f, 2f), Px());
            GUI.DrawTexture(new Rect(q.x - 1f, q.y - 6f, 2f, 13f), Px());
        }

        static Rect DroneIconRect(Vector2 centre)
        {
            return new Rect(centre.x - DroneIconSize * 0.5f,
                centre.y - DroneIconSize * 0.5f, DroneIconSize, DroneIconSize);
        }

        // A top-down quadcopter: four rotors, diagonal arms and a narrow body.
        // Generate once at double display resolution for clean small edges.
        static bool DroneInk(float x, float y)
        {
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
            float rotor = (ax - 9f) * (ax - 9f) + (ay - 9f) * (ay - 9f);
            return (ax <= 2.5f && ay <= 5f)
                || (Mathf.Abs(ax - ay) <= 1.5f && ax <= 9f && ay <= 9f)
                || (rotor >= 6.25f && rotor <= 20.25f);
        }

        // THE COLOUR OF THE PRINTED MAP, NOT OF A GAME MARKER (order of
        // 2026-09-18: "ich haette die farbe gerne als grau/weiss wie in der og
        // karten beschriftungen"). The icon used to be drawn in the Locator red
        // of the patrol border, which reads as the toolkit's own overlay; the
        // map's baked place names and the route labels next to it (MapLabels,
        // .64/.61/.51) are a pale warm grey, so the quadcopter is now drawn in
        // the same family, one step towards white because an 18 px silhouette
        // carries less of its colour than a letter does.
        static Texture2D DroneIcon()
        {
            if (_droneIcon != null) return _droneIcon;
            const int size = 36;
            Color[] pixels = new Color[size * size];
            Color ink = new Color(0.88f, 0.87f, 0.83f, 0.95f);      // label grey/white
            Color outline = new Color(0.12f, 0.11f, 0.09f, 0.85f);  // printer's black
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - (size - 1) * 0.5f;
                    float dy = y - (size - 1) * 0.5f;
                    if (DroneInk(dx, dy)) pixels[y * size + x] = ink;
                    else
                    {
                        // The outline is the DARK one now: a pale halo around a
                        // pale silhouette is no silhouette at all, and the map
                        // has light roads and light lettering on it as well as
                        // dark terrain.
                        for (int oy = -1; oy <= 1; oy++)
                            for (int ox = -1; ox <= 1; ox++)
                                if (DroneInk(dx + ox, dy + oy))
                                    pixels[y * size + x] = outline;
                    }
                }
            _droneIcon = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _droneIcon.name = "NDR_ArtyDroneMapIcon";
            _droneIcon.wrapMode = TextureWrapMode.Clamp;
            _droneIcon.filterMode = FilterMode.Bilinear;
            _droneIcon.SetPixels(pixels);
            _droneIcon.Apply();
            return _droneIcon;
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.x, b.x), y0 = Mathf.Max(a.y, b.y);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

        static Texture2D Px()
        {
            if (_px == null) _px = Mortar.PxTexture();
            return _px;
        }

        static Texture2D Ring()
        {
            if (_ring == null) _ring = Mortar.RingTexture();
            return _ring;
        }
    }

    /// <summary>The supplied Bohdana model, imported by arty_import.py.
    /// Required assets only: missing art must never silently become a different
    /// vehicle. Shared meshes and material keep each settlement inexpensive.</summary>
    internal static class ArtyModel
    {
        // Imported metres converted to the native vehicle scale (3 units/m).
        static readonly Vector3 TurretAt = new Vector3(0f, 2.09076942f, -3.4798f) * 3f;
        static readonly Vector3 TrunnionAt = new Vector3(0.35473356f, 1.4097f, -0.32180553f) * 3f;
        internal static Vector3 MuzzleLocal
        {
            get { return new Vector3(-0.01442866f, -0.01224359f, 6.83341194f) * 3f; }
        }

        internal static Vector3 UsePoint(Transform vehicle, Vector3 player)
        {
            // Both sides of the gun deck, outside the deployed stabilizers.
            float side = vehicle.InverseTransformPoint(player).x < 0f ? -8.5f : 8.5f;
            return vehicle.TransformPoint(new Vector3(side, 0f, -10.5f));
        }

        static Mesh _hull, _turret, _barrel, _recoil;
        static Material _material;
        static bool _loaded;

        internal static GameObject Build(out Transform turret, out Transform barrel)
        {
            turret = null;
            barrel = null;
            if (!Load()) return null;
            GameObject root = new GameObject("NDR Arty Vehicle");
            try
            {
                Part(root, _hull);
                GameObject t = new GameObject("Turret");
                t.transform.SetParent(root.transform, false);
                t.transform.localPosition = TurretAt;
                Part(t, _turret);
                GameObject b = new GameObject("Barrel");
                b.transform.SetParent(t.transform, false);
                b.transform.localPosition = TrunnionAt;
                Part(b, _barrel);
                GameObject sliding = new GameObject("Recoil tube and breech");
                sliding.transform.SetParent(b.transform, false);
                Part(sliding, _recoil);
                b.AddComponent<ArtyRecoil>().Slide = sliding.transform;
                turret = t.transform;
                barrel = b.transform;
                return root;
            }
            catch
            {
                UnityEngine.Object.Destroy(root);
                throw;
            }
        }

        static void Part(GameObject go, Mesh mesh)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
        }

        static bool Load()
        {
            if (_loaded) return _hull != null && _turret != null
                && _barrel != null && _recoil != null && _material != null;
            _loaded = true;
            try
            {
                _hull = Assets.Load("arty_hull.ndmesh");
                _turret = Assets.Load("arty_turret.ndmesh");
                _barrel = Assets.Load("arty_barrel.ndmesh");
                _recoil = Assets.Load("arty_recoil.ndmesh");
                // Albedo is sRGB colour, never linear mask/normal-map data.
                Texture2D tex = Assets.Texture("arty_diffuse.png", false, true);
                Texture2D metal = Assets.Texture("arty_metal.png", true, true);
                Texture2D normal = Assets.Texture("arty_normal.png", true, true);
                if (_hull == null || _turret == null || _barrel == null || _recoil == null
                    || tex == null || metal == null || normal == null)
                    throw new InvalidOperationException("Bohdana assets missing; repair the client package");
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _material = new Material(shader);
                _material.name = "NDR_Bohdana_Material";
                _material.mainTexture = tex;
                _material.color = Color.white;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = metal.anisoLevel = normal.anisoLevel = 8;
                tex.filterMode = metal.filterMode = normal.filterMode = FilterMode.Trilinear;
                // These shared 4K maps are uploaded once for every battery.
                // Block compression plus releasing CPU pixels avoids retaining
                // half a gigabyte of uncompressed readable texture data.
                tex.Compress(true); tex.Apply(true, true);
                metal.Compress(true); metal.Apply(true, true);
                normal.Compress(true); normal.Apply(true, true);
                if (_material.HasProperty("_MetallicGlossMap"))
                {
                    _material.SetTexture("_MetallicGlossMap", metal);
                    _material.SetFloat("_GlossMapScale", 1f);
                    _material.EnableKeyword("_METALLICGLOSSMAP");
                }
                if (_material.HasProperty("_BumpMap"))
                {
                    _material.SetTexture("_BumpMap", normal);
                    // Same normal strength as the native BTR exterior donor.
                    _material.SetFloat("_BumpScale", 1f);
                    _material.EnableKeyword("_NORMALMAP");
                }
                if (_material.HasProperty("_SpecularHighlights")) _material.SetFloat("_SpecularHighlights", 1f);
                if (_material.HasProperty("_GlossyReflections")) _material.SetFloat("_GlossyReflections", 1f);
                if (_material.HasProperty("_EmissionColor")) _material.SetColor("_EmissionColor", Color.black);
                _material.DisableKeyword("_EMISSION");
                RevivalPlugin.L.LogInfo("ArtyModel: supplied Bohdana model loaded");
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("ArtyModel: " + ex.Message);
                return false;
            }
        }
    }

    // Reflection over the installed PUN version, with no compile-time game DLL.
    internal static class ArtyRoom
    {
        static Type Photon { get { return RevivalPlugin.TypeByName("PhotonNetwork"); } }
        static object Room()
        {
            Type t = Photon;
            return t == null ? null : AccessTools.PropertyGetter(t, "room").Invoke(null, null);
        }
        internal static string Key(Vector3 centre)
        {
            return "ndr.arty." + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                + "." + Mathf.RoundToInt(centre.x * 10f) + "." + Mathf.RoundToInt(centre.z * 10f);
        }
        internal static double Now()
        {
            try
            {
                Type t = Photon;
                if (t != null) return Convert.ToDouble(AccessTools.PropertyGetter(t, "time").Invoke(null, null));
            }
            catch { }
            return Time.realtimeSinceStartup;
        }
        internal static double Elapsed(double now, double then)
        {
            // PUN converts its uint32 millisecond timestamp to seconds. Handle
            // its 49.7-day wrap for both a shot rewind and the replacement timer.
            double delta = (now - then) % 4294967.296;
            if (delta < -2147483.648) delta += 4294967.296;
            if (delta > 2147483.648) delta -= 4294967.296;
            return delta;
        }
        internal static bool Finite(Vector3 p)
        {
            return !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)
                || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
        }
        internal static double[] Read(string key)
        {
            try
            {
                object room = Room();
                if (room == null) return null;
                object props = AccessTools.PropertyGetter(room.GetType(), "CustomProperties").Invoke(room, null);
                System.Collections.IDictionary table = props as System.Collections.IDictionary;
                double[] state = table == null ? null : table[key] as double[];
                if (state == null || state.Length != 2 || state[0] < 0 || state[0] > 3
                    || double.IsNaN(state[0]) || double.IsInfinity(state[0])
                    || double.IsNaN(state[1]) || double.IsInfinity(state[1])) return null;
                return state;
            }
            catch (Exception ex) { Warn(ex); return null; }
        }
        internal static void Write(string key, int hits, double readyAt)
        {
            if (!RevivalTroopInsertion.MasterClient()) return;
            try
            {
                object room = Room();
                if (room == null) return;
                MethodInfo set = AccessTools.Method(room.GetType(), "SetCustomProperties", null, null);
                Type ht = set.GetParameters()[0].ParameterType;
                System.Collections.IDictionary table = Activator.CreateInstance(ht) as System.Collections.IDictionary;
                table[key] = new double[] { hits, readyAt };
                // Installed PUN signature: Hashtable, expected Hashtable, bool.
                set.Invoke(room, new object[] { table, null, false });
            }
            catch (Exception ex) { Warn(ex); }
        }
        static bool _warned;
        static void Warn(Exception ex)
        {
            if (_warned) return;
            _warned = true;
            RevivalPlugin.L.LogError("Artillery room state unavailable: " + ex.Message);
        }
    }

    /// <summary>Fast hydraulic recoil, slower return and a delayed powder cloud.</summary>
    public sealed class ArtyRecoil : MonoBehaviour
    {
        public Transform Slide;
        float _shot = -100f;
        static Material _smoke;
        public void Kick() { _shot = Time.time; }
        internal static float Stroke(float age)
        {
            if (age < 0f || age >= 0.95f) return 0f;
            if (age < 0.085f) return 1.65f * Mathf.Sin(age / 0.085f * Mathf.PI * 0.5f);
            float t = (age - 0.085f) / 0.865f;
            return 1.65f * (1f - t * t * (3f - 2f * t));
        }
        void LateUpdate()
        {
            // Local +Z is the bore; elevation may change while it returns.
            if (Slide != null) Slide.localPosition = -Vector3.forward * Stroke(Time.time - _shot);
        }
        internal static void Smoke(Vector3 muzzle, Vector3 forward)
        {
            if (_smoke == null)
            {
                Shader shader = Shader.Find("Particles/Alpha Blended");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                if (shader == null) return;
                _smoke = new Material(shader);
                Texture2D texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                Color[] pixels = new Color[64 * 64];
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        float dx = (x - 31.5f) / 31.5f, dy = (y - 31.5f) / 31.5f;
                        float a = Mathf.Clamp01(1f - dx * dx - dy * dy);
                        pixels[y * 64 + x] = new Color(1f, 1f, 1f, a * a);
                    }
                texture.SetPixels(pixels);
                texture.Apply();
                texture.wrapMode = TextureWrapMode.Clamp;
                _smoke.mainTexture = texture;
            }
            GameObject cloud = new GameObject("NDR artillery muzzle smoke");
            cloud.transform.position = muzzle;
            cloud.transform.rotation = Quaternion.LookRotation(forward);
            ParticleSystem ps = cloud.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 0.35f;
            main.startDelay = new ParticleSystem.MinMaxCurve(0.055f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.3f, 2.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(7f, 17f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.1f, 2.8f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.57f, 0.56f, 0.52f, 0.48f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.04f);
            main.maxParticles = 48;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 32) });
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = 0.32f;
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.55f, 1f, 3.2f));
            ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
            color.enabled = true;
            Gradient fade = new Gradient();
            fade.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f) }, new GradientAlphaKey[] {
                new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.06f),
                new GradientAlphaKey(0.6f, 0.45f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(fade);
            ParticleSystemRenderer renderer = cloud.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _smoke;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ps.Play();
            UnityEngine.Object.Destroy(cloud, 3.5f);
        }
    }
}

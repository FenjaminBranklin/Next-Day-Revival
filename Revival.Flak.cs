// Next Day: Survival - Revival Toolkit
//
// THE AIRFIELD FLAK (P4): the two gunned AA positions of the east airfield
// fight as ZU-23-2 twin 23 mm guns.
//
// WHAT THIS IS, and what it is built from:
//
//   1  THE GUN. The AA positions of east_af_shelters (unity/EastTile/Tools/
//      airfield_assembly.py: "AA position north" at (4385, 1320) and "AA
//      position S2-S3" at (4300, -550), both yaw 90) carry a derelict ZU-23-2
//      merged into the ring's mesh. At runtime that mesh is swapped for the
//      bundle's own gunless twin (the renderers of "AA position V3 (empty)",
//      aa_position_empty.fbx: same ring, bare platform with four bolts) and a
//      live gun is built on the platform from primitives, to the measures of
//      the derelict (zu23_parts in assets/src/airfield_shelters.py), metres x
//      2.8. It has the moving parts the derelict could not have: the upper
//      carriage traverses (360 degrees), the cradle elevates (-10 to +90), the
//      two barrels recoil ALTERNATELY and the handwheels turn. No bundle
//      rebuild and no pivots from a kit are needed: every pivot is built here.
//      Without the shelters bundle (greybox fallback) the gun stands on the
//      ground at the same two points.
//   2  THE ROUNDS are the Gepard's round loop (GepardShots.Fire with a Spec):
//      real projectiles at 970 m/s with gravity, in world units (1 m = 2.8 u),
//      tracer on every round, a direct hit within Fuze of an aircraft, and a
//      timed burst: the gunner sets the fuze to the range he aims at, so every
//      round that misses bursts as a black puff near the target and its
//      fragments hit within Splash. Damage per target class is the Gepard's
//      (GepardGun.Hit): a drone dies to one hit, a helicopter or the An-2 to
//      HeliHits of them, the kill travels on the Gepard's own event.
//   3  THE CREW. Two men of the airfield's defender faction ([Airfield]
//      DefenderFaction), spawned by the master with the keys "flak/<id>/g" and
//      "flak/<id>/c" (THE SLASH IS LOAD BEARING: Revival.GroundEnemies.cs
//      deletes keyed NPCs without one). Every client puts them on the two
//      seats of the traversing carriage after the animator and samples the
//      game's seated clip onto them (TechnicalCrew.Sitzen) - the gunner sits
//      at the handwheels, the loader beside him, and both turn with the gun.
//   4  THE GUNNER (master only). Air targets only - a helicopter or the An-2
//      with a player of a hostile faction aboard, or a drone flown by one -
//      airborne and within EngageRange. Never an aircraft with a player of
//      his own faction aboard. He lays the mount at the handwheels' rates
//      (TraverseSpeed, ElevationSpeed, SlewAcceleration), leads the target by
//      eye from a lagging velocity estimate, and fires bursts. His first
//      burst is far off (InitialError); after every burst he corrects by what
//      he saw (WalkFactor) down to ErrorFloor - the puffs visibly walk in. A
//      pilot who turns hard (a change of velocity since the last burst) throws
//      the correction off again (EvadeFactor), so evading works and flying
//      straight is deadly.
//   5  A PLAYER takes the gun over when its crew is dead: walk up to the
//      gunner's seat, press the turret key (G). The mouse cranks the mount at
//      the same slew limits, the left button fires, the right button zooms
//      the sight, R reloads, G leaves. The body stays standing beside the gun
//      (movement is locked while at the sight).
//   6  THE WIRE. One Photon event (FlakNet, code 198): the pose, trigger and
//      fuze range of a gun ten times a second from whoever lays it (the master
//      for a crew, the manning player's client), so every client sees the gun
//      turn, the barrels recoil and the same bursts in the sky. Rounds are
//      drawn by every client; only the layer's rounds do damage.
//   7  THE API for P5 (tower radar) and P6a (no-fly zones): Flak.Guns() lists
//      each gun with its state; AssignTarget / ClearTarget / WeaponsFree /
//      HoldFire / ZoneDefence command one gun or all. Commands act where the
//      fire control runs - on the master (Flak.Authority).
//
// WHAT IS NOT PROVEN WITHOUT THE GAME: how the gun sits in the ring, whether
// the seated clip reaches the handwheels, the look of the puffs and the feel
// of the walking bursts. docs/ai/tasks/airfield-flak-p4.md lists the checks.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only, no BOM.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs          BindConfig / Install / Tick / LateUpdate / OnGUI.
//   Revival.CameraTurret.cs   CameraOwner.Flak and its LateTick dispatch.
//   RevivalGepard.cs          GepardShots.Spec + Fire(Spec, ...), the Proximity
//                             fuze overload, GepardGun.Nearest, Hit(heliHits),
//                             GepardFx.Flak, the once-a-frame round Tick.
//   RevivalGepardCrew.cs      GepardAir.Hit(gunHits); Steht/Parken/Agent/Ruhig/
//                             Spieler internal.
//   RevivalTechnicalCrew.cs   Sitzen internal (the seated clip).
//   Revival.Airfield.cs       Faction internal (the crew's side).

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
    /// <summary>What a flak gun does with targets (Flak.WeaponsFree,
    /// Flak.HoldFire, Flak.ZoneDefence).</summary>
    public enum FlakMode
    {
        /// <summary>Engage every hostile aircraft in range.</summary>
        WeaponsFree = 0,
        /// <summary>Lay on the target but never fire.</summary>
        HoldFire = 1,
        /// <summary>Engage only aircraft inside the gun's zone.</summary>
        ZoneDefence = 2,
        /// <summary>Engage only the assigned target (P5 console: hold fire on
        /// everything else, weapons free on one blip).</summary>
        AssignedOnly = 3,
    }

    /// <summary>What a flak gun is doing right now (FlakGunInfo.State).</summary>
    public enum FlakState
    {
        /// <summary>Nobody at the gun: crew dead or not spawned, no player.</summary>
        NoCrew = 0,
        /// <summary>Manned, no target.</summary>
        Idle = 1,
        /// <summary>Laying on a target, not firing (reaction, hold fire, out of reach).</summary>
        Tracking = 2,
        /// <summary>Firing.</summary>
        Firing = 3,
        /// <summary>Changing the ammunition boxes.</summary>
        Reloading = 4,
        /// <summary>A player is at the sight.</summary>
        PlayerManned = 5,
    }

    /// <summary>A snapshot of one gun for the P5 radar and the P6a zones.</summary>
    public sealed class FlakGunInfo
    {
        public string Id;                 // "AA-N", "AA-S"
        public string Name;               // the emplacement's name in the assembly
        public Vector3 Position;          // the mount's pivot, world units
        public float Yaw, Pitch;          // mount bearing (world, degrees from +z) and elevation
        public FlakState State;
        public FlakMode Mode;
        public GameObject Target;         // what it lays on now; null = none
        public GameObject Assigned;       // the target an API caller assigned; null = none
        public int Rounds;                // in the two boxes
        public int CrewAlive;             // 0..2
        public bool PlayerManned;
        public int ManActor = -1;         // the player at the sight (actor number), -1 none
        public float Range;               // engagement range, world units
        public Vector3 ZoneCentre;        // ZoneDefence: the zone (horizontal circle, ceiling above ground)
        public float ZoneRadius, ZoneCeiling;
        public bool ZoneStrict;
    }

    /// <summary>
    /// The airfield's ZU-23-2 guns: config, the public API, and the frame
    /// (find the emplacements, build the guns, crews, fire control, a manning
    /// player, the wire).
    /// </summary>
    public static class Flak
    {
        /// <summary>World units per real metre (the east tile's 2.8,
        /// BuildContent.UnitsPerMetre).</summary>
        internal const float K = 2.8f;

        // ------------------------------------------------------------ config

        internal static ConfigEntry<bool> CfgEnabled, CfgNpcCrew, CfgTakeover,
            CfgTracers, CfgMuzzleFlash, CfgRecoil, CfgCasings, CfgPuffs, CfgGunSound,
            CfgBurstSound, CfgSeated, CfgHandwheels;
        internal static ConfigEntry<float> CfgVelocity, CfgRange, CfgSelfDestruct, CfgCeiling,
            CfgRpm, CfgDispersion, CfgFuze, CfgSplash, CfgTurn, CfgElev, CfgAccel, CfgPitchMin,
            CfgPitchMax, CfgInitialError, CfgWalk, CfgFloor, CfgEvade, CfgLag, CfgReaction,
            CfgBurst, CfgPause, CfgReload, CfgRespawn, CfgSeatDrop, CfgReach, CfgSensitivity,
            CfgMinHeight;
        internal static ConfigEntry<int> CfgHeliHits, CfgRoundsPerLoad, CfgEventCode;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "Flak";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The ZU-23-2 guns on the east airfield's AA positions (acting only with "
                + "[World] EastTile and [Airfield] Enabled).");
            CfgNpcCrew = cfg.Bind(S, "NpcCrew", true,
                "Two men of the airfield's defender faction man each gun (master spawns them).");
            CfgTakeover = cfg.Bind(S, "PlayerTakeover", true,
                "A player can take a gun over when its crew is dead (turret key, G).");
            CfgTracers = cfg.Bind(S, "Tracers", true, "Effect: a tracer line on every round.");
            CfgMuzzleFlash = cfg.Bind(S, "MuzzleFlash", true, "Effect: muzzle flash and gun smoke.");
            CfgRecoil = cfg.Bind(S, "BarrelRecoil", true,
                "Animation: the two barrels recoil alternately with every round.");
            CfgCasings = cfg.Bind(S, "Casings", true, "Effect: spent cases thrown out of the breeches.");
            CfgPuffs = cfg.Bind(S, "FlakPuffs", true,
                "Effect: the black puff of a bursting round in the sky.");
            CfgGunSound = cfg.Bind(S, "GunSound", true, "Sound: the report of every round.");
            CfgBurstSound = cfg.Bind(S, "BurstSound", true,
                "Sound: the far-carrying bang of a burst, delayed by the speed of sound.");
            CfgSeated = cfg.Bind(S, "SeatedCrew", true,
                "Animation: the crew sit on the gun's seats with the game's seated clip "
                + "and turn with the mount. Off: they stand on the seat points.");
            CfgHandwheels = cfg.Bind(S, "HandwheelAnimation", true,
                "Animation: the traverse and elevation handwheels turn while the gun slews.");
            CfgVelocity = cfg.Bind(S, "MuzzleVelocity", 970f,
                "Muzzle velocity in metres per second (the real HEI-T: 970). World speed is x 2.8.");
            CfgRange = cfg.Bind(S, "EngageRange", 2000f,
                "Slant range in metres the crew opens fire at (the real effective range "
                + "against aircraft: 2000 m).");
            CfgSelfDestruct = cfg.Bind(S, "SelfDestructRange", 2500f,
                "A round that meets nothing bursts after this many metres at the latest.");
            CfgCeiling = cfg.Bind(S, "Ceiling", 1500f,
                "Highest target altitude above the gun in metres (the real effective ceiling).");
            CfgMinHeight = cfg.Bind(S, "MinTargetHeight", 3f,
                "An aircraft lower than this over the ground (metres) is not airborne and "
                + "not engaged - the guns are for air targets only.");
            CfgRpm = cfg.Bind(S, "RateOfFire", 1600f,
                "Rounds per minute from both barrels together (2 x 800, the real cyclic "
                + "rate). The barrels alternate.");
            CfgDispersion = cfg.Bind(S, "Dispersion", 3.5f,
                "Dispersion of a single round in milliradians (the Gepard: 1.5).");
            CfgFuze = cfg.Bind(S, "DirectHitDistance", 1.5f,
                "A round passing this close to an aircraft (beyond its own size, world "
                + "units) hits it.");
            CfgSplash = cfg.Bind(S, "BurstRadius", 2.5f,
                "Metres from a burst within which its fragments hit an aircraft (beyond "
                + "the aircraft's size).");
            CfgHeliHits = cfg.Bind(S, "HeliHits", 12,
                "23 mm hits a helicopter or the An-2 survives (the Gepard's 35 mm: 10).");
            CfgTurn = cfg.Bind(S, "TraverseSpeed", 70f, "Highest traverse rate, degrees per second.");
            CfgElev = cfg.Bind(S, "ElevationSpeed", 50f, "Highest elevation rate, degrees per second.");
            CfgAccel = cfg.Bind(S, "SlewAcceleration", 140f,
                "How fast the hand-cranked mount gets up to speed, degrees per second squared.");
            CfgPitchMin = cfg.Bind(S, "PitchMin", -10f, "Lowest elevation, degrees (the real -10).");
            CfgPitchMax = cfg.Bind(S, "PitchMax", 90f, "Highest elevation, degrees (the real +90).");
            CfgInitialError = cfg.Bind(S, "InitialError", 45f,
                "The crew's aiming error on a new target, milliradians of range.");
            CfgWalk = cfg.Bind(S, "WalkFactor", 0.55f,
                "The part of the aiming error left after each burst's correction (0..1). "
                + "Smaller: the bursts walk in faster.");
            CfgFloor = cfg.Bind(S, "ErrorFloor", 7f,
                "The aiming error the crew never gets under, milliradians of range.");
            CfgEvade = cfg.Bind(S, "EvadeFactor", 0.9f,
                "How much a target's change of velocity since the last burst throws the "
                + "correction off (0 = evading does not help).");
            CfgLag = cfg.Bind(S, "TrackingLag", 2.0f,
                "How fast the crew's estimate of the target's velocity follows it, per "
                + "second (the Gepard's radar: 5).");
            CfgReaction = cfg.Bind(S, "Reaction", 1.5f, "Seconds on a new target before the first burst.");
            CfgBurst = cfg.Bind(S, "Burst", 0.6f, "Length of a crew's burst in seconds.");
            CfgPause = cfg.Bind(S, "BurstPause", 1.4f, "Pause between bursts in seconds.");
            CfgRoundsPerLoad = cfg.Bind(S, "Rounds", 100, "Rounds in the two boxes (2 x 50).");
            CfgReload = cfg.Bind(S, "ReloadSeconds", 12f,
                "Changing both boxes, seconds (one man left alone: x 1.5).");
            CfgRespawn = cfg.Bind(S, "CrewRespawnMinutes", 35f,
                "A dead crew is replaced after this many minutes, never with a player "
                + "within 150 u or at the gun.");
            CfgSeatDrop = cfg.Bind(S, "SeatDrop", 2.3f,
                "World units the seated man's root lies below the seat cushion (the "
                + "game's sit clip floats the body above its root).");
            CfgReach = cfg.Bind(S, "TakeoverDistance", 7f,
                "How close to the gunner's seat a player must stand to man the gun, world units.");
            CfgSensitivity = cfg.Bind(S, "Sensitivity", 2.0f,
                "At the sight: degrees of laying per unit of mouse movement.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 198,
                "Photon event code for the guns' pose and trigger. Must not overlap any "
                + "other channel (0..199).");
        }

        internal static bool On
        {
            get { return Airfield.On && (CfgEnabled == null || CfgEnabled.Value); }
        }

        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        internal static bool B2(ConfigEntry<bool> c) { return c == null || c.Value; }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }

        internal static float Speed { get { return Mathf.Max(100f, F(CfgVelocity, 970f)) * K; } }
        internal static float Gravity { get { return 9.81f * K; } }
        internal static float RangeU { get { return Mathf.Max(100f, F(CfgRange, 2000f)) * K; } }
        static float SelfDestructU { get { return Mathf.Max(F(CfgRange, 2000f), F(CfgSelfDestruct, 2500f)) * K; } }
        static float CeilingU { get { return Mathf.Max(50f, F(CfgCeiling, 1500f)) * K; } }
        static int RoundsFull { get { return Mathf.Max(2, CfgRoundsPerLoad == null ? 100 : CfgRoundsPerLoad.Value); } }

        // ------------------------------------------------------ the emplacements

        /// <summary>The gunned AA positions of the assembly (airfield_assembly.py):
        /// id, holder name, x, z, yaw. The third, "AA position V3 (empty)",
        /// has no gun and lends its mesh.</summary>
        static readonly string[] Ids = { "AA-N", "AA-S" };
        static readonly string[] Names = { "AA position north", "AA position S2-S3" };
        static readonly Vector2[] Spots = { new Vector2(4385f, 1320f), new Vector2(4300f, -550f) };
        const float SpotYaw = 90f;
        const string EmptyName = "AA position V3 (empty)";

        // ------------------------------------------------------------- the gun

        internal sealed class Gun
        {
            public int Index;
            public string Id, Name;
            public Transform Holder;            // the emplacement; null in the greybox fallback
            public Transform Root, Mount, Cradle;
            public readonly Transform[] Barrel = new Transform[2];
            public readonly Transform[] Muzzle = new Transform[2];
            public Transform SeatGunner, SeatLoader, Eye, WheelTraverse, WheelElevation;
            public Transform Owner;             // what the rounds step past

            // pose (every client)
            public float Yaw, Pitch, YawVel, PitchVel;
            public float WantYaw, WantPitch = 12f;
            public float LastWantYaw, LastWantPitch, WantYawRate, WantPitchRate;   // the target's angular rate (feed-forward)
            public readonly float[] Recoil = new float[2];
            public float WheelT, WheelE;

            // crew (every client finds them by key; the master spawns them)
            public Component Gunner, Loader;
            public GameObject GunSquad, LoadSquad;
            public bool Asked;
            public int Tries;
            public float NextTry, BuiltAt, DeadSince = -1f;

            // fire control (the layer: master for a crew, the manning client)
            public FlakMode Mode = FlakMode.WeaponsFree;
            public GameObject Assigned;
            public Vector3 ZoneCentre;
            public float ZoneRadius, ZoneCeiling;
            public bool ZoneStrict;
            public readonly List<GepardGun.Contact> Air = new List<GepardGun.Contact>();
            public readonly List<GepardGun.Contact> Hostile = new List<GepardGun.Contact>();
            public GepardGun.Contact Target;
            public bool Laying, Firing, SentFiring, Engaged, Reloading;
            public float Held, NextLook, BurstUntil, PauseUntil, NextShot, LastContact, NextPublish, ReloadUntil;
            public int GunIdx, Rounds = -1, Fired;
            public Vector3 Err, LastVel, Aim;
            public float FuzeRange;

            // the wire (what the others see)
            public bool RemoteFiring;
            public float RemoteAt = -100f, RemoteFuze, RemoteNextShot;
            public int RemoteGun;
            public float ClaimedUntil = -100f;  // a player at this gun on another client
            public int ClaimActor;
        }

        static readonly List<Gun> _guns = new List<Gun>();
        static float _nextFind, _airfieldSince = -1f;
        static bool _fallbackSaid;
        static GepardShots.Spec _spec;

        // --------------------------------------------------------------- the API

        /// <summary>True where commands act: the master runs every crew's fire
        /// control. A command on another client is kept there and does nothing
        /// until that client becomes master.</summary>
        public static bool Authority { get { return Crocodile.IsMaster(); } }

        /// <summary>The engagement range in world units (P5 draws it).</summary>
        public static float Range { get { return RangeU; } }

        /// <summary>Raised on the master when a crew opens fire on a new target:
        /// gun id and the target.</summary>
        public static event Action<string, GameObject> Engaging;

        /// <summary>Every gun built in this world, as a fresh snapshot.</summary>
        public static List<FlakGunInfo> Guns()
        {
            List<FlakGunInfo> list = new List<FlakGunInfo>();
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Root == null) continue;
                FlakGunInfo f = new FlakGunInfo();
                f.Id = g.Id;
                f.Name = g.Name;
                f.Position = g.Mount != null ? g.Mount.position : g.Root.position;
                f.Yaw = g.Root.eulerAngles.y + g.Yaw;
                if (f.Yaw >= 360f) f.Yaw -= 360f;
                f.Pitch = g.Pitch;
                f.State = State(g);
                f.Mode = g.Mode;
                f.Target = g.Target != null ? g.Target.Go : null;
                f.Assigned = g.Assigned;
                f.Rounds = g.Rounds < 0 ? RoundsFull : g.Rounds;
                f.CrewAlive = (Up(g.Gunner) ? 1 : 0) + (Up(g.Loader) ? 1 : 0);
                f.PlayerManned = PlayerAt(g);
                f.ManActor = g == _manned ? Crocodile.LocalActor() : Time.time < g.ClaimedUntil ? g.ClaimActor : -1;
                f.Range = RangeU;
                f.ZoneCentre = g.ZoneCentre;
                f.ZoneRadius = g.ZoneRadius;
                f.ZoneCeiling = g.ZoneCeiling;
                f.ZoneStrict = g.ZoneStrict;
                list.Add(f);
            }
            return list;
        }

        /// <summary>One gun's snapshot by id; null if there is none.</summary>
        public static FlakGunInfo Get(string id)
        {
            List<FlakGunInfo> all = Guns();
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        /// <summary>Lay gun <paramref name="id"/> (null = every gun) on
        /// <paramref name="target"/>: it is engaged before anything else while it
        /// is a valid air target - still never one with the crew's own faction
        /// aboard. HoldFire still holds fire. Null clears.</summary>
        public static bool AssignTarget(string id, GameObject target)
        {
            bool any = false;
            for (int i = 0; i < _guns.Count; i++)
            {
                if (!Match(_guns[i], id)) continue;
                _guns[i].Assigned = target;
                _guns[i].NextLook = 0f;
                any = true;
            }
            if (any) Log("gun " + (id ?? "all") + ": target " + (target == null ? "cleared" : "assigned: " + target.name) + ".");
            return any;
        }

        public static bool ClearTarget(string id) { return AssignTarget(id, null); }

        /// <summary>Engage every hostile aircraft in range.</summary>
        public static bool WeaponsFree(string id) { return SetMode(id, FlakMode.WeaponsFree); }

        /// <summary>Track, never fire.</summary>
        public static bool HoldFire(string id) { return SetMode(id, FlakMode.HoldFire); }

        /// <summary>Engage only the assigned target (AssignTarget); every
        /// other aircraft is held.</summary>
        public static bool AssignedOnly(string id) { return SetMode(id, FlakMode.AssignedOnly); }

        static float _dirReaction = 1f, _dirError = 1f, _dirTracking = 1f;

        /// <summary>
        /// Fire direction from outside the gun (P5 tower radar): scales on the
        /// crews' Reaction, InitialError and TrackingLag rate. 1 / 1 / 1 is
        /// the crews by eye (the P4 numbers); the radar HQ sets smaller
        /// reaction and error and a faster tracking with an operator at the
        /// console, larger ones when it is destroyed. Acts on the master
        /// (Authority), for every gun.
        /// </summary>
        public static void SetFireDirection(float reactionScale, float errorScale, float trackingScale)
        {
            _dirReaction = Mathf.Clamp(reactionScale, 0.05f, 10f);
            _dirError = Mathf.Clamp(errorScale, 0.05f, 10f);
            _dirTracking = Mathf.Clamp(trackingScale, 0.05f, 10f);
        }

        /// <summary>P5: the radar HQ's order for a gun (by id) shown in the
        /// sight of the player manning it; null or empty = no line.</summary>
        internal static Func<string, string> SightOrder;

        internal static float DirReaction { get { return _dirReaction; } }
        internal static float DirError { get { return _dirError; } }
        internal static float DirTracking { get { return _dirTracking; } }

        /// <summary>
        /// Zone defence (P6a): engage only aircraft inside the horizontal circle
        /// round <paramref name="centre"/> of <paramref name="radius"/> world
        /// units, up to <paramref name="ceiling"/> units above the ground (0 = the
        /// gun's own ceiling). <paramref name="strict"/>: any aircraft in the zone
        /// that has nobody of the crew's own faction aboard is a target, hostile
        /// or not (a no-fly zone); otherwise only hostile ones.
        /// </summary>
        public static bool ZoneDefence(string id, Vector3 centre, float radius, float ceiling, bool strict)
        {
            bool any = false;
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (!Match(g, id)) continue;
                g.ZoneCentre = centre;
                g.ZoneRadius = Mathf.Max(0f, radius);
                g.ZoneCeiling = Mathf.Max(0f, ceiling);
                g.ZoneStrict = strict;
                g.Mode = FlakMode.ZoneDefence;
                g.NextLook = 0f;
                any = true;
            }
            if (any) Log("gun " + (id ?? "all") + ": zone defence, radius "
                + radius.ToString("0", CultureInfo.InvariantCulture) + " u" + (strict ? ", strict." : "."));
            return any;
        }

        static bool SetMode(string id, FlakMode mode)
        {
            bool any = false;
            for (int i = 0; i < _guns.Count; i++)
            {
                if (!Match(_guns[i], id)) continue;
                _guns[i].Mode = mode;
                _guns[i].NextLook = 0f;
                any = true;
            }
            if (any) Log("gun " + (id ?? "all") + ": " + mode + ".");
            return any;
        }

        static bool Match(Gun g, string id)
        {
            return id == null || string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase);
        }

        static FlakState State(Gun g)
        {
            if (PlayerAt(g)) return FlakState.PlayerManned;
            if (!Up(g.Gunner) && !Up(g.Loader)) return FlakState.NoCrew;
            bool firing = g.Laying ? g.Firing : (g.RemoteFiring && Time.time - g.RemoteAt < 0.5f);
            if (g.Reloading) return FlakState.Reloading;
            if (firing) return FlakState.Firing;
            if (g.Target != null && g.Target.Go != null) return FlakState.Tracking;
            return FlakState.Idle;
        }

        static bool PlayerAt(Gun g)
        {
            return g == _manned || Time.time < g.ClaimedUntil;
        }

        internal static void Log(string s) { RevivalPlugin.L.LogInfo("Flak: " + s); }

        // ------------------------------------------------------------ install

        /// <summary>The input lock of the player at the sight: the game's own
        /// "cannot move / rotate / shoot" predicates, forced true while a
        /// player mans a gun - the door the FPV drone uses (DroneInputHook).</summary>
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
            HarmonyMethod post = new HarmonyMethod(typeof(Flak).GetMethod("LockPostfix"));
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
                    RevivalPlugin.L.LogWarning("Flak: input lock " + Locks[i] + ": " + ex.Message);
                }
            }
            Log("input lock on " + n + " of " + Locks.Length + " game predicates.");
        }

        public static void LockPostfix(ref bool __result)
        {
            if (_manned != null) __result = true;
        }

        // ---------------------------------------------------------- the frame

        /// <summary>Every client, every frame.</summary>
        internal static void Tick()
        {
            if (!On)
            {
                if (_guns.Count > 0) Clear("off");
                return;
            }
            try
            {
                FlakNet.EnsureHooked();
                GepardNet.EnsureHooked();     // the helicopter kill travels on the Gepard's event
                Find();
                Prune();
                if (_guns.Count == 0) return;
                FlakCrew.Resolve(_guns);
                bool master = Crocodile.IsMaster();
                float dt = Time.deltaTime;
                for (int i = 0; i < _guns.Count; i++)
                {
                    Gun g = _guns[i];
                    try
                    {
                        if (master) FlakCrew.Spawn(g);
                        if (g == _manned) FlakPlayer.Lay(g, dt);
                        else if (master && B(CfgNpcCrew) && Time.time >= g.ClaimedUntil) FlakFire.Control(g, dt);
                        else FlakFire.Release(g);
                        if (!g.Laying && g != _manned) Remote(g, dt);
                        Slew(g, dt);
                        TickReload(g);
                    }
                    catch (Exception ex)
                    {
                        RevivalPlugin.L.LogError("Flak gun " + g.Id + ": " + ex);
                    }
                }
                FlakPlayer.Tick(_guns);
                GepardShots.Tick();
                FlakSound.Tick();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Flak: " + ex);
            }
        }

        /// <summary>Every client, after the animator: mount, cradle, barrels,
        /// handwheels, and the crew on their seats.</summary>
        internal static void LateFrame()
        {
            if (_guns.Count == 0) return;
            try
            {
                for (int i = 0; i < _guns.Count; i++)
                {
                    Gun g = _guns[i];
                    if (g.Root == null) continue;
                    Pose(g);
                    FlakCrew.Hold(g);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Flak late frame: " + ex);
            }
        }

        internal static void LateTick() { FlakPlayer.LateTick(); }

        internal static void Draw() { FlakPlayer.Draw(_guns); }

        // ------------------------------------------------------ finding the guns

        // Q1 perf: one time-sliced pass over the east scenes answers every
        // name (the empty and all AA positions) instead of one full recursive
        // walk per name inside a single frame, every 5 s until all guns stand.
        static readonly SceneSweep _sweep = new SceneSweep();
        static readonly SceneSweep.Visitor _visit = FindVisit;
        static Transform _sweepEmpty;
        static Transform[] _sweepHolders;

        static bool FindVisit(Transform t, string name)
        {
            if (_sweepEmpty == null && name == EmptyName) _sweepEmpty = t;
            for (int k = 0; k < Ids.Length; k++)
                if (_sweepHolders[k] == null && name == Names[k]) _sweepHolders[k] = t;
            return true;
        }

        static void Find()
        {
            if (!_sweep.Active)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 5f;
                if (_guns.Count >= Ids.Length) return;
                Scene tile0 = SceneManager.GetSceneByName(EastWorld.SceneName);
                if (!tile0.isLoaded) { _airfieldSince = -1f; return; }
                if (_airfieldSince < 0f) _airfieldSince = Time.realtimeSinceStartup;
                _sweepEmpty = null;
                _sweepHolders = new Transform[Ids.Length];
                _sweep.Begin("East");
            }
            if (!_sweep.Step(1.5, _visit)) return;     // continues next frame

            Scene airfield = SceneManager.GetSceneByName("EastAirfield");
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) { _airfieldSince = -1f; return; }
            Transform empty = _sweepEmpty;
            Transform[] holders = _sweepHolders;
            // The greybox fallback has no named emplacement: after 40 s of a
            // loaded tile the guns stand on the ground at the assembly's spots.
            bool fallback = Time.realtimeSinceStartup - _airfieldSince > 40f;
            for (int k = 0; k < Ids.Length; k++)
            {
                if (Has(Ids[k])) continue;
                if (holders[k] == null && !fallback) continue;
                if (holders[k] == null && !_fallbackSaid)
                {
                    _fallbackSaid = true;
                    RevivalPlugin.L.LogWarning("Flak: the shelters bundle's AA positions are not in the "
                        + "world - the guns stand on the ground at the assembly's spots.");
                }
                Gun g = FlakModel.Build(k, Ids[k], Names[k], holders[k], empty, Spots[k], SpotYaw,
                    airfield.isLoaded ? airfield : tile);
                if (g != null) _guns.Add(g);
            }
        }

        static bool Has(string id)
        {
            for (int i = 0; i < _guns.Count; i++) if (_guns[i].Id == id) return true;
            return false;
        }

        /// <summary>A gun whose scene went away (tile unloaded) is forgotten.</summary>
        static void Prune()
        {
            for (int i = _guns.Count - 1; i >= 0; i--)
            {
                if (_guns[i].Root != null) continue;
                if (_manned == _guns[i]) FlakPlayer.Leave("the gun is gone");
                _guns.RemoveAt(i);
            }
        }

        static void Clear(string why)
        {
            if (_manned != null) FlakPlayer.Leave(why);
            for (int i = 0; i < _guns.Count; i++)
                if (_guns[i].Root != null) UnityEngine.Object.Destroy(_guns[i].Root.gameObject);
            _guns.Clear();
            Log("guns cleared (" + why + ").");
        }

        internal static Gun ByIndex(int index)
        {
            for (int i = 0; i < _guns.Count; i++) if (_guns[i].Index == index) return _guns[i];
            return null;
        }

        // ------------------------------------------------------- the manned gun

        internal static Gun _manned;

        // ------------------------------------------------------------- pose

        /// <summary>The hand-cranked mount: toward WantYaw / WantPitch at the
        /// handwheels' rates, getting up to speed at SlewAcceleration. Every
        /// client runs it, on its own target (the layer's or the wire's).</summary>
        static void Slew(Gun g, float dt)
        {
            if (dt <= 0f) return;
            float turn = Mathf.Max(1f, F(CfgTurn, 70f));
            float elev = Mathf.Max(1f, F(CfgElev, 50f));
            float acc = Mathf.Max(1f, F(CfgAccel, 140f));
            float min = F(CfgPitchMin, -10f), max = F(CfgPitchMax, 90f);

            // Q2: the gunner cranks WITH the target, not after it. The rate at
            // which the laying point moves (smoothed over ~0.3 s, so the ten
            // poses a second a remote client gets read as a steady rate) is
            // fed forward, and only the remaining error is closed by the
            // braking law - a crossing aircraft is tracked without the lag that
            // kept the bore behind it, and the whole motion stays within the
            // handwheels' rate and acceleration.
            float k = 1f - Mathf.Exp(-dt / 0.3f);
            float ry = Mathf.Clamp(Mathf.DeltaAngle(g.LastWantYaw, g.WantYaw) / dt, -turn, turn);
            float rp = Mathf.Clamp((g.WantPitch - g.LastWantPitch) / dt, -elev, elev);
            if (Mathf.Abs(Mathf.DeltaAngle(g.LastWantYaw, g.WantYaw)) > 20f) ry = 0f;   // a new target, not a rate
            if (Mathf.Abs(g.WantPitch - g.LastWantPitch) > 20f) rp = 0f;
            g.WantYawRate = Mathf.Lerp(g.WantYawRate, ry, k);
            g.WantPitchRate = Mathf.Lerp(g.WantPitchRate, rp, k);
            g.LastWantYaw = g.WantYaw;
            g.LastWantPitch = g.WantPitch;

            float dy = Mathf.DeltaAngle(g.Yaw, g.WantYaw);
            // Brake in time: never faster than what still stops on the mark.
            float stop = Mathf.Sqrt(2f * acc * Mathf.Abs(dy));
            float want = Mathf.Clamp(g.WantYawRate + Mathf.Clamp(dy * 4f, -stop, stop), -turn, turn);
            g.YawVel = Mathf.MoveTowards(g.YawVel, want, acc * dt);
            g.Yaw += g.YawVel * dt;
            if (g.Yaw > 180f) g.Yaw -= 360f;
            if (g.Yaw < -180f) g.Yaw += 360f;

            float wp = Mathf.Clamp(g.WantPitch, min, max);
            float dp = wp - g.Pitch;
            float stopP = Mathf.Sqrt(2f * acc * Mathf.Abs(dp));
            float ff = (wp <= min && g.WantPitchRate < 0f) || (wp >= max && g.WantPitchRate > 0f) ? 0f : g.WantPitchRate;
            float wantP = Mathf.Clamp(ff + Mathf.Clamp(dp * 4f, -stopP, stopP), -elev, elev);
            g.PitchVel = Mathf.MoveTowards(g.PitchVel, wantP, acc * dt);
            g.Pitch = Mathf.Clamp(g.Pitch + g.PitchVel * dt, min, max);
            if ((g.Pitch <= min && g.PitchVel < 0f) || (g.Pitch >= max && g.PitchVel > 0f)) g.PitchVel = 0f;

            // One turn of a handwheel is a few degrees of gun.
            g.WheelT += g.YawVel * dt * 20f;
            g.WheelE += g.PitchVel * dt * 25f;
            // The run-out is shorter than one barrel's own interval (two rounds
            // of the pair), so each barrel is home before it fires again and the
            // pair visibly works left-right-left instead of both hanging back.
            float runOut = Mathf.Clamp(Interval() * 1.6f, 0.04f, 0.11f);
            for (int i = 0; i < 2; i++) g.Recoil[i] = Mathf.MoveTowards(g.Recoil[i], 0f, dt / runOut);
        }

        static void Pose(Gun g)
        {
            if (g.Mount != null) g.Mount.localRotation = Quaternion.Euler(0f, g.Yaw, 0f);
            if (g.Cradle != null) g.Cradle.localRotation = Quaternion.Euler(-g.Pitch, 0f, 0f);
            bool recoil = B(CfgRecoil);
            for (int i = 0; i < 2; i++)
            {
                if (g.Barrel[i] == null) continue;
                Vector3 p = g.Barrel[i].localPosition;
                // A short sharp kick and a slower run-out: the curve of a recoil.
                float r = g.Recoil[i];
                p.z = recoil ? -0.13f * K * r * r * (3f - 2f * r) : 0f;
                g.Barrel[i].localPosition = p;
            }
            if (B(CfgHandwheels))
            {
                if (g.WheelTraverse != null) g.WheelTraverse.localRotation = Quaternion.Euler(0f, 0f, g.WheelT) * Quaternion.Euler(90f, 0f, 0f);
                if (g.WheelElevation != null) g.WheelElevation.localRotation = Quaternion.Euler(0f, 0f, g.WheelE) * Quaternion.Euler(90f, 0f, 0f);
            }
        }

        /// <summary>Yaw (relative to the emplacement) and pitch of a world
        /// direction.</summary>
        internal static void Angles(Gun g, Vector3 world, out float yaw, out float pitch)
        {
            Vector3 l = g.Root.InverseTransformDirection(world);
            yaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
            pitch = Mathf.Atan2(l.y, Mathf.Sqrt(l.x * l.x + l.z * l.z)) * Mathf.Rad2Deg;
        }

        internal static Vector3 Mid(Gun g)
        {
            return g.Cradle != null ? g.Cradle.position : g.Root.position;
        }

        /// <summary>Where the target will be when a round gets there, plus the
        /// drop - the Gepard's fire control (GepardGun.Intercept) with this
        /// gun's velocity and the world's real gravity.</summary>
        internal static Vector3 Intercept(Vector3 from, Vector3 p, Vector3 v, out float tof)
        {
            float speed = Speed;
            tof = Vector3.Distance(from, p) / speed;
            Vector3 aim = p;
            for (int i = 0; i < 4; i++)
            {
                aim = p + v * tof;
                tof = Vector3.Distance(from, aim) / speed;
            }
            return aim + Vector3.up * (0.5f * Gravity * tof * tof);
        }

        // ------------------------------------------------------------- rounds

        internal static GepardShots.Spec Spec()
        {
            if (_spec == null) _spec = new GepardShots.Spec();
            _spec.Speed = Speed;
            _spec.Gravity = -Gravity;
            _spec.Dispersion = Mathf.Max(0f, F(CfgDispersion, 3.5f));
            _spec.Fuze = Mathf.Max(0f, F(CfgFuze, 1.5f));
            _spec.Splash = Mathf.Max(0f, F(CfgSplash, 2.5f)) * K;
            _spec.HeliHits = Mathf.Max(1, CfgHeliHits == null ? 12 : CfgHeliHits.Value);
            _spec.Tracer = B(CfgTracers);
            _spec.Flak = true;
            _spec.PuffFx = B(CfgPuffs);
            _spec.BurstSound = B(CfgBurstSound) ? (Action<Vector3>)FlakSound.Burst : null;
            return _spec;
        }

        /// <summary>One round from the barrel whose turn it is, with its
        /// flash, recoil, case and report. <paramref name="fuze"/> is the range
        /// the round bursts at if it meets nothing (world units).</summary>
        internal static void Shoot(Gun g, int barrel, Vector3 aim, bool aimValid, float fuze, bool live,
                                   List<GepardGun.Contact> contacts)
        {
            Transform m = g.Muzzle[barrel];
            if (m == null) return;
            Vector3 muzzle = m.position;
            Vector3 dir = g.Cradle != null ? g.Cradle.forward : m.forward;
            if (aimValid)
            {
                Vector3 want = (aim - muzzle).normalized;
                if (Vector3.Angle(dir, want) < 1f) dir = want;
            }
            float range = Mathf.Clamp(fuze, 30f, SelfDestructU);
            // The fuze is set, not exact: a few per cent either way.
            float life = range / Speed * UnityEngine.Random.Range(0.97f, 1.03f);
            GepardShots.Fire(Spec(), g.Owner, muzzle, dir, life, live, contacts);
            g.Recoil[barrel] = 1f;
            if (B(CfgMuzzleFlash)) GepardFx.Muzzle(muzzle, dir);
            if (B(CfgCasings) && g.Barrel[barrel] != null)
                GepardFx.Casing(g.Barrel[barrel], barrel == 0 ? -1f : 1f);
            if (B(CfgGunSound)) FlakSound.Report(muzzle);
        }

        internal static float Interval()
        {
            return 60f / Mathf.Max(60f, F(CfgRpm, 1600f));
        }

        internal static float MaxFuze { get { return SelfDestructU; } }

        /// <summary>Another client lays this gun: follow its pose and draw its
        /// rounds (not live) at the same rate and fuze range.</summary>
        static void Remote(Gun g, float dt)
        {
            bool fresh = Time.time - g.RemoteAt < 0.5f;
            if (!fresh || !g.RemoteFiring) return;
            float now = Time.time;
            float interval = Interval();
            if (g.RemoteNextShot < now - 0.1f) g.RemoteNextShot = now;
            int n = 0;
            while (now >= g.RemoteNextShot && n < 4)
            {
                int b = g.RemoteGun;
                g.RemoteGun = 1 - b;
                Shoot(g, b, Vector3.zero, false, g.RemoteFuze, false, null);
                g.RemoteNextShot += interval;
                n++;
            }
        }

        internal static void OnPose(int index, float yaw, float pitch, bool firing, float fuze, bool player, int sender)
        {
            Gun g = ByIndex(index);
            if (g == null) return;
            if (player)
            {
                g.ClaimedUntil = Time.time + 1.5f;
                g.ClaimActor = sender;
                if (_manned == g) FlakPlayer.Leave("another player is at this gun");
            }
            if (g.Laying) return;      // this client lays it (master with a crew)
            if (_manned == g) return;
            g.WantYaw = yaw;
            g.WantPitch = pitch;
            g.RemoteFiring = firing;
            g.RemoteFuze = fuze;
            g.RemoteAt = Time.time;
        }

        internal static void Publish(Gun g, bool player, bool now)
        {
            if (!now && g.Firing == g.SentFiring && Time.time < g.NextPublish) return;
            g.NextPublish = Time.time + 0.1f;
            g.SentFiring = g.Firing;
            FlakNet.SendPose(g.Index, g.WantYaw, g.WantPitch, g.Firing, g.FuzeRange, player);
        }

        // ------------------------------------------------------------- reload

        internal static void StartReload(Gun g, float seconds)
        {
            if (g.Reloading) return;
            g.Reloading = true;
            g.Firing = false;
            g.ReloadUntil = Time.time + Mathf.Max(0.5f, seconds);
        }

        static void TickReload(Gun g)
        {
            if (!g.Reloading || Time.time < g.ReloadUntil) return;
            g.Reloading = false;
            g.Rounds = RoundsFull;
        }

        internal static float ReloadSeconds() { return Mathf.Max(0.5f, F(CfgReload, 12f)); }

        internal static bool Up(Component ai) { return ai != null && GepardCrew.Steht(ai); }

        internal static void RaiseEngaging(Gun g, GameObject target)
        {
            Action<string, GameObject> h = Engaging;
            if (h == null) return;
            try { h(g.Id, target); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Flak: Engaging handler: " + ex.Message); }
        }
    }

    // =====================================================================
    // The model

    /// <summary>
    /// The live ZU-23-2 on an emplacement's platform: the derelict swapped
    /// out of the ring's mesh, the gun built from primitives to the
    /// derelict's measures (zu23_parts, metres; x K here) with its pivots.
    /// </summary>
    internal static class FlakModel
    {
        const float K = Flak.K;
        static Material _olive, _rust, _steel, _black;

        internal static Flak.Gun Build(int index, string id, string name, Transform holder, Transform empty,
                                       Vector2 spot, float yaw, Scene scene)
        {
            try
            {
                Materials();
                Flak.Gun g = new Flak.Gun();
                g.Index = index;
                g.Id = id;
                g.Name = name;
                g.Holder = holder;

                GameObject root = new GameObject("NDR Flak ZU-23-2 " + id);
                if (holder != null)
                {
                    root.transform.SetParent(holder, false);
                    root.transform.localPosition = Vector3.zero;
                    root.transform.localRotation = Quaternion.identity;
                    string swapped = Swap(holder, empty);
                    Flak.Log(id + ": built on \"" + name + "\" at " + holder.position + ", derelict " + swapped + ".");
                }
                else
                {
                    SceneManager.MoveGameObjectToScene(root, scene);
                    Vector3 at = new Vector3(spot.x, 0f, spot.y);
                    RaycastHit hit;
                    if (Physics.Raycast(at + Vector3.up * 3000f, Vector3.down, out hit, 6000f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        at.y = hit.point.y;
                    else
                    {
                        float y;
                        if (EastWorld.TerrainHeight(at, out y)) at.y = y;
                    }
                    root.transform.position = at;
                    root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    Flak.Log(id + ": built on the ground at " + at + " (no emplacement).");
                }
                g.Root = root.transform;
                g.Owner = holder != null ? holder : root.transform;
                Parts(g);
                g.BuiltAt = Time.time;
                g.Rounds = -1;
                return g;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Flak: building gun " + id + " failed: " + ex);
                return null;
            }
        }

        /// <summary>The ring's renderers get the gunless twin's mesh and
        /// materials, LOD level by LOD level (nodes *_LOD&lt;n&gt;).</summary>
        static string Swap(Transform holder, Transform empty)
        {
            if (empty == null) return "kept (no \"AA position V3 (empty)\" to lend the gunless mesh)";
            Dictionary<int, MeshRenderer> donors = new Dictionary<int, MeshRenderer>();
            MeshRenderer[] ds = empty.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < ds.Length; i++)
            {
                int lod = Lod(ds[i].gameObject.name);
                if (lod >= 0 && !donors.ContainsKey(lod)) donors[lod] = ds[i];
            }
            int n = 0;
            MeshRenderer[] rs = holder.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                MeshRenderer r = rs[i];
                if (r.transform.IsChildOf(holder) && r.name.StartsWith("NDR Flak", StringComparison.Ordinal)) continue;
                int lod = Lod(r.gameObject.name);
                MeshRenderer d;
                if (lod < 0 || !donors.TryGetValue(lod, out d)) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                MeshFilter df = d.GetComponent<MeshFilter>();
                if (mf == null || df == null || df.sharedMesh == null) continue;
                mf.sharedMesh = df.sharedMesh;
                r.sharedMaterials = d.sharedMaterials;
                n++;
            }
            return n > 0 ? "swapped on " + n + " LOD renderer(s)" : "kept (no LOD renderer matched)";
        }

        static int Lod(string name)
        {
            int i = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            int k;
            string rest = name.Substring(i + 4);
            int end = 0;
            while (end < rest.Length && char.IsDigit(rest[end])) end++;
            return end > 0 && int.TryParse(rest.Substring(0, end), out k) ? k : -1;
        }

        static void Materials()
        {
            if (_olive != null) return;
            _olive = Mat("NDR_Flak_Olive", new Color(0.29f, 0.32f, 0.21f), 0.15f, 0.25f);
            _rust = Mat("NDR_Flak_Rust", new Color(0.27f, 0.2f, 0.14f), 0.1f, 0.15f);
            _steel = Mat("NDR_Flak_Steel", new Color(0.13f, 0.13f, 0.13f), 0.6f, 0.45f);
            _black = Mat("NDR_Flak_Black", new Color(0.07f, 0.07f, 0.06f), 0.0f, 0.2f);
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

        static Transform Node(Transform parent, string name, Vector3 metres)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = metres * K;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        /// <summary>A box from its centre and size in metres.</summary>
        static Transform Box(Transform parent, Vector3 centre, Vector3 size, Material m)
        {
            return Prim(PrimitiveType.Cube, parent, centre, size, Quaternion.identity, m);
        }

        /// <summary>A cylinder along local z from its centre, radius and length in metres.</summary>
        static Transform Tube(Transform parent, Vector3 centre, float radius, float length, Material m)
        {
            return Prim(PrimitiveType.Cylinder, parent, centre, new Vector3(radius * 2f, length * 0.5f, radius * 2f),
                        Quaternion.Euler(90f, 0f, 0f), m);
        }

        static Transform Prim(PrimitiveType type, Transform parent, Vector3 centre, Vector3 scale,
                              Quaternion rot, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = "NDR Flak part";
            Collider c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre * K;
            go.transform.localRotation = rot;
            go.transform.localScale = scale * K;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            return go.transform;
        }

        /// <summary>
        /// The measures of zu23_parts (metres, y up from the ground under the
        /// emplacement, +z the bore at rest), split at the real pivots: the
        /// lower carriage stands, the upper carriage traverses on the pivot
        /// above it, the cradle elevates on the trunnions 1.35 m up, each
        /// barrel slides back in the cradle.
        /// </summary>
        static void Parts(Flak.Gun g)
        {
            Transform root = g.Root;
            // lower carriage on the platform: base and three legs, rust
            Box(root, new Vector3(0f, 0.23f, 0f), new Vector3(1.6f, 0.22f, 1.6f), _rust);
            for (int i = 0; i < 3; i++)
            {
                Transform leg = Node(root, "leg", Vector3.zero);
                leg.localRotation = Quaternion.Euler(0f, i * 120f, 0f);
                Box(leg, new Vector3(0f, 0.19f, 1.1f), new Vector3(0.2f, 0.14f, 1.0f), _rust);
                Box(leg, new Vector3(0f, 0.13f, 1.55f), new Vector3(0.3f, 0.04f, 0.3f), _rust);   // foot pad
            }

            // upper carriage: traverses about the vertical axis on the base
            Transform mount = Node(root, "mount", new Vector3(0f, 0.34f, 0f));
            g.Mount = mount;
            Tube(mount, new Vector3(0f, 0.04f, 0f), 0.55f, 0.08f, _olive).localRotation = Quaternion.identity;
            Box(mount, new Vector3(0f, 0.3f, 0f), new Vector3(1.2f, 0.52f, 1.0f), _olive);
            for (int s = -1; s <= 1; s += 2)
                Box(mount, new Vector3(s * 0.42f, 0.78f, 0.0f), new Vector3(0.08f, 0.5f, 0.6f), _olive);   // trunnion cheeks
            // the two seats: gunner left (the sight's side), loader right
            for (int s = -1; s <= 1; s += 2)
            {
                Box(mount, new Vector3(s * 0.85f, 0.445f, -0.425f), new Vector3(0.4f, 0.07f, 0.35f), _black);
                Box(mount, new Vector3(s * 0.85f, 0.64f, -0.61f), new Vector3(0.4f, 0.32f, 0.05f), _black);
                Box(mount, new Vector3(s * 0.85f, 0.2f, -0.42f), new Vector3(0.06f, 0.41f, 0.06f), _rust);
                Box(mount, new Vector3(s * 0.72f, 0.3f, -0.42f), new Vector3(0.28f, 0.05f, 0.05f), _rust);
            }
            g.SeatGunner = Node(mount, "seat gunner", new Vector3(-0.85f, 0.48f, -0.4f));
            g.SeatLoader = Node(mount, "seat loader", new Vector3(0.85f, 0.48f, -0.4f));
            // the handwheels in front of the gunner: traverse (outer) and elevation (inner)
            g.WheelTraverse = Node(mount, "wheel traverse", new Vector3(-1.02f, 0.92f, -0.08f));
            g.WheelTraverse.localRotation = Quaternion.Euler(90f, 0f, 0f);   // the disc faces the gunner
            Tube(g.WheelTraverse, Vector3.zero, 0.13f, 0.03f, _black).localRotation = Quaternion.identity;
            Box(g.WheelTraverse, new Vector3(0.1f, 0f, 0f), new Vector3(0.1f, 0.025f, 0.025f), _steel);        // spoke
            Box(g.WheelTraverse, new Vector3(0.12f, 0.05f, 0f), new Vector3(0.025f, 0.1f, 0.025f), _steel);    // crank handle
            g.WheelElevation = Node(mount, "wheel elevation", new Vector3(-0.64f, 0.92f, -0.08f));
            g.WheelElevation.localRotation = Quaternion.Euler(90f, 0f, 0f);   // the disc faces the gunner
            Tube(g.WheelElevation, Vector3.zero, 0.13f, 0.03f, _black).localRotation = Quaternion.identity;
            Box(g.WheelElevation, new Vector3(0.1f, 0f, 0f), new Vector3(0.1f, 0.025f, 0.025f), _steel);        // spoke
            Box(g.WheelElevation, new Vector3(0.12f, 0.05f, 0f), new Vector3(0.025f, 0.1f, 0.025f), _steel);    // crank handle
            Box(mount, new Vector3(-0.83f, 0.78f, -0.02f), new Vector3(0.44f, 0.08f, 0.08f), _olive);   // wheel shafts housing

            // cradle: elevates about the trunnions
            Transform cradle = Node(mount, "cradle", new Vector3(0f, 1.01f, 0f));
            g.Cradle = cradle;
            Box(cradle, new Vector3(0f, 0f, 0.15f), new Vector3(0.7f, 0.36f, 1.5f), _olive);
            Box(cradle, new Vector3(0f, 0.2f, -0.35f), new Vector3(0.5f, 0.06f, 0.5f), _olive);
            for (int s = -1; s <= 1; s += 2)
                Box(cradle, new Vector3(s * 0.49f, 0.08f, 0.1f), new Vector3(0.24f, 0.38f, 0.55f), _olive);   // ammunition boxes
            // the sight on the gunner's side, moving with the guns
            Box(cradle, new Vector3(-0.55f, 0.28f, -0.1f), new Vector3(0.06f, 0.3f, 0.06f), _steel);
            Box(cradle, new Vector3(-0.55f, 0.46f, -0.1f), new Vector3(0.12f, 0.12f, 0.22f), _black);
            g.Eye = Node(cradle, "eye", new Vector3(-0.55f, 0.5f, -0.45f));

            // the barrels: left 0, right 1; each slides back in its cradle slot
            for (int b = 0; b < 2; b++)
            {
                float x = b == 0 ? -0.2f : 0.2f;
                Transform barrel = Node(cradle, b == 0 ? "barrel left" : "barrel right", new Vector3(x, 0.05f, 0f));
                g.Barrel[b] = barrel;
                Box(barrel, new Vector3(0f, 0f, 0.45f), new Vector3(0.14f, 0.16f, 0.9f), _steel);   // receiver
                Tube(barrel, new Vector3(0f, 0f, 1.25f), 0.07f, 0.7f, _steel);                       // barrel jacket
                Tube(barrel, new Vector3(0f, 0f, 2.25f), 0.045f, 1.9f, _steel);                      // barrel
                Tube(barrel, new Vector3(0f, 0f, 3.3f), 0.07f, 0.3f, _black);                        // flash hider
                Transform muzzle = Node(barrel, "muzzle", new Vector3(0f, 0f, 3.47f));
                g.Muzzle[b] = muzzle;
            }
        }
    }

    // =====================================================================
    // The crew

    /// <summary>
    /// Two men per gun, keyed "flak/&lt;id&gt;/g" (gunner) and "flak/&lt;id&gt;/c"
    /// (loader): spawned by the master, found by key on every client, held
    /// on their seats after the animator.
    /// </summary>
    internal static class FlakCrew
    {
        internal const string KeyPrefix = "flak/";
        const string KeyGunner = "/g";
        const string KeyLoader = "/c";

        static float _nextResolve;
        static readonly Dictionary<string, List<Component>> _byKey = new Dictionary<string, List<Component>>();

        static string Key(Flak.Gun g) { return KeyPrefix + g.Id.ToLowerInvariant(); }

        /// <summary>Every client, twice a second: the men of each gun by the
        /// key in their Photon instantiation data - a joiner and a new master
        /// know them the same way. The living one wins over a corpse.</summary>
        internal static void Resolve(List<Flak.Gun> guns)
        {
            if (Time.time < _nextResolve) return;
            _nextResolve = Time.time + 0.5f;
            Type npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (npcType == null) return;
            _byKey.Clear();
            UnityEngine.Object[] actors = NpcScan.All();
            for (int i = 0; i < actors.Length; i++)
            {
                Component ai = actors[i] as Component;
                if (ai == null) continue;
                string key = Crew.GroundKey(ai);
                if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                List<Component> men;
                if (!_byKey.TryGetValue(key, out men)) { men = new List<Component>(); _byKey[key] = men; }
                men.Add(ai);
            }
            for (int i = 0; i < guns.Count; i++)
            {
                Flak.Gun g = guns[i];
                g.Gunner = Pick(Key(g) + KeyGunner, g.Gunner);
                g.Loader = Pick(Key(g) + KeyLoader, g.Loader);
            }
        }

        static Component Pick(string key, Component had)
        {
            List<Component> men;
            if (!_byKey.TryGetValue(key, out men) || men.Count == 0) return had;
            for (int i = 0; i < men.Count; i++) if (Flak.Up(men[i])) return men[i];
            return men[0];
        }

        /// <summary>Master: man a gun that has nobody - at the start once no
        /// player is within 60 u, after a dead crew's CrewRespawnMinutes once
        /// no player is within 150 u and nobody is at the sight.</summary>
        internal static void Spawn(Flak.Gun g)
        {
            if (!Flak.B2(Flak.CfgNpcCrew) || g.Root == null) return;
            bool alive = Flak.Up(g.Gunner) || Flak.Up(g.Loader);
            if (alive) { g.DeadSince = -1f; return; }
            if (Time.time < g.NextTry) return;
            if (g == Flak._manned || Time.time < g.ClaimedUntil) return;
            bool known = g.Gunner != null || g.Loader != null || g.GunSquad != null;
            if (known || g.Asked)
            {
                if (g.DeadSince < 0f) g.DeadSince = Time.time;
                float wait = Mathf.Clamp(Flak.CfgRespawn == null ? 35f : Flak.CfgRespawn.Value, 1f, 600f) * 60f;
                if (Time.time - g.DeadSince < wait) return;
                if (PlayerNear(g.Root.position, 150f)) { g.NextTry = Time.time + 10f; return; }
            }
            else
            {
                // A fresh gun: the men of an earlier master may not be resolved
                // yet, and nobody should see them appear.
                if (Time.time - g.BuiltAt < 8f) return;
                if (PlayerNear(g.Root.position, 60f)) { g.NextTry = Time.time + 5f; return; }
            }
            g.NextTry = Time.time + 6f;
            g.Tries++;
            try
            {
                string side = Airfield.Faction();
                Vector3 pg = Ground(g, g.SeatGunner);
                Vector3 pl = Ground(g, g.SeatLoader);
                g.GunSquad = Crew.DropGroundSquad(pg, new Vector3[] { pg }, side, null, Key(g) + KeyGunner);
                g.LoadSquad = Crew.DropGroundSquad(pl, new Vector3[] { pl }, side, null, Key(g) + KeyLoader);
                // A spawn that produced nobody is tried again (five times).
                g.Asked = g.GunSquad != null || g.LoadSquad != null || g.Tries >= 5;
                g.DeadSince = -1f;
                g.Rounds = -1;
                Fill(g);
                Flak.Log(g.Id + ": crew of two (" + side + ") spawned at the gun - gunner "
                    + (g.Gunner != null) + ", loader " + (g.Loader != null) + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Flak: crew spawn at " + g.Id + " failed: " + ex.Message);
            }
        }

        static void Fill(Flak.Gun g)
        {
            Array a = Crew.Men(g.GunSquad);
            if (a != null && a.Length > 0) g.Gunner = a.GetValue(0) as Component;
            a = Crew.Men(g.LoadSquad);
            if (a != null && a.Length > 0) g.Loader = a.GetValue(0) as Component;
        }

        static Vector3 Ground(Flak.Gun g, Transform seat)
        {
            Vector3 p = seat != null ? seat.position : g.Root.position;
            p.y = g.Root.position.y + 0.12f * Flak.K;
            return p;
        }

        static bool PlayerNear(Vector3 p, float r)
        {
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && (players[i].transform.position - p).sqrMagnitude < r * r) return true;
            return false;
        }

        /// <summary>Every client, late: each living man on his seat, turned
        /// with the mount, his own navigation and idle logic held off, and -
        /// SeatedCrew - the game's seated clip sampled onto him.</summary>
        internal static void Hold(Flak.Gun g)
        {
            Seat(g, g.Gunner, g.SeatGunner, 0);
            Seat(g, g.Loader, g.SeatLoader, 1);
        }

        static void Seat(Flak.Gun g, Component ai, Transform seat, int clip)
        {
            if (seat == null || !Flak.Up(ai)) return;
            bool sit = Flak.B2(Flak.CfgSeated);
            Vector3 at = seat.position;
            if (sit) at -= Vector3.up * Mathf.Max(0f, Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value);
            else at.y = g.Root.position.y + 0.12f * Flak.K;
            Vector3 fwd = g.Mount != null ? g.Mount.forward : g.Root.forward;
            fwd.y = 0f;
            Quaternion rot = fwd.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(fwd.normalized, Vector3.up);
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
            if (sit) TechnicalCrew.Sitzen(ai, clip);
            TechnicalCrew.Unbewaffnet(ai);     // hands on the handwheels, no rifle
        }
    }

    // =====================================================================
    // The crew's fire control

    /// <summary>
    /// The master's gunner: finds hostile air targets, lays the mount, fires
    /// bursts whose aim point walks in on the target, and publishes the pose.
    /// </summary>
    internal static class FlakFire
    {
        static readonly List<GameObject> _heli = new List<GameObject>();
        static readonly List<GepardAir.Found> _planes = new List<GepardAir.Found>();

        internal static void Control(Flak.Gun g, float dt)
        {
            bool gunner = Flak.Up(g.Gunner), loader = Flak.Up(g.Loader);
            if (!gunner && !loader) { Release(g); return; }
            if (!g.Laying)
            {
                g.Laying = true;
                g.Rounds = g.Rounds < 0 ? Mathf.Max(2, Flak.CfgRoundsPerLoad == null ? 100 : Flak.CfgRoundsPerLoad.Value) : g.Rounds;
            }
            if (Time.time >= g.NextLook)
            {
                g.NextLook = Time.time + 0.5f;
                Search(g);
            }
            Follow(g, dt);

            GepardGun.Contact t = g.Target;
            if (t == null || t.Go == null)
            {
                g.Firing = false;
                if (g.Engaged && Time.time - g.LastContact > 4f)
                {
                    g.Engaged = false;
                    Flak.Log(g.Id + ": target lost - " + g.Fired + " round(s) fired.");
                }
                // Three quiet seconds: back to the ready position.
                if (Time.time - g.LastContact > 3f)
                {
                    g.WantYaw = 0f;
                    g.WantPitch = 12f;
                }
                g.FuzeRange = Flak.MaxFuze;
                Flak.Publish(g, false, false);
                return;
            }
            g.LastContact = Time.time;
            Vector3 mid = Flak.Mid(g);
            float dist = Vector3.Distance(mid, t.Pos);
            if (!g.Engaged)
            {
                g.Engaged = true;
                g.Fired = 0;
                g.Held = 0f;
                // The first burst is laid by eye: far off.
                g.Err = Offset(t, mid, dist * Mathf.Max(0f, Flak.CfgInitialError == null ? 45f : Flak.CfgInitialError.Value)
                    * Flak.DirError * 0.001f);
                g.LastVel = t.Vel;
                Flak.Log(g.Id + ": engages " + Describe(t) + " at "
                    + dist.ToString("0", CultureInfo.InvariantCulture) + " u (" + g.Mode + ").");
                Flak.RaiseEngaging(g, t.Go);
            }

            float tof;
            Vector3 aim = Flak.Intercept(mid, t.Pos, t.Vel, out tof) + g.Err;
            float wantYaw, wantPitch;
            Flak.Angles(g, aim - mid, out wantYaw, out wantPitch);
            float min = Flak.CfgPitchMin == null ? -10f : Flak.CfgPitchMin.Value;
            float max = Flak.CfgPitchMax == null ? 90f : Flak.CfgPitchMax.Value;
            bool reach = wantPitch >= min - 0.5f && wantPitch <= max + 0.5f;
            g.WantYaw = wantYaw;
            g.WantPitch = Mathf.Clamp(wantPitch, min, max);
            g.Aim = aim;
            g.FuzeRange = Vector3.Distance(mid, aim);

            Vector3 bore = g.Cradle != null ? g.Cradle.forward : g.Root.forward;
            float error = Vector3.Angle(bore, aim - mid);
            g.Held += dt;
            float reaction = Mathf.Max(0f, Flak.CfgReaction == null ? 1.5f : Flak.CfgReaction.Value) * Flak.DirReaction;
            bool ready = reach && g.Held >= reaction && error < 3f && g.Mode != FlakMode.HoldFire
                && !g.Reloading;
            Trigger(g, ready, aim, t, mid, tof, gunner && loader);
            Flak.Publish(g, false, false);
        }

        internal static void Release(Flak.Gun g)
        {
            if (!g.Laying) return;
            g.Laying = false;
            g.Firing = false;
            g.Target = null;
            g.Engaged = false;
            g.Held = 0f;
            Flak.Publish(g, false, true);
        }

        /// <summary>Bursts at the gun's rate, the barrels in turn. Between
        /// bursts the gunner corrects on what he saw - the error shrinks by
        /// WalkFactor - and a target that changed its velocity since the last
        /// burst throws the correction off again.</summary>
        static void Trigger(Flak.Gun g, bool ready, Vector3 aim, GepardGun.Contact t, Vector3 mid, float tof, bool both)
        {
            float now = Time.time;
            float dist = Vector3.Distance(mid, t.Pos);
            if (g.Firing && now >= g.BurstUntil)
            {
                g.Firing = false;
                g.PauseUntil = now + Mathf.Max(0.2f, Flak.CfgPause == null ? 1.4f : Flak.CfgPause.Value)
                    * UnityEngine.Random.Range(0.8f, 1.3f);
                float walk = Mathf.Clamp01(Flak.CfgWalk == null ? 0.55f : Flak.CfgWalk.Value);
                float floor = Mathf.Max(0f, Flak.CfgFloor == null ? 7f : Flak.CfgFloor.Value) * 0.001f * dist;
                g.Err = g.Err * walk + Scatter(t, mid, floor);
            }
            if (!g.Firing)
            {
                if (!ready || now < g.PauseUntil) return;
                if (g.Rounds <= 0)
                {
                    Flak.StartReload(g, Flak.ReloadSeconds() * (both ? 1f : 1.5f));
                    return;
                }
                // A new burst: how much did the target change course since the last?
                float evade = Mathf.Max(0f, Flak.CfgEvade == null ? 0.9f : Flak.CfgEvade.Value);
                float dv = (t.Vel - g.LastVel).magnitude;
                g.LastVel = t.Vel;
                if (dv > 0.5f) g.Err += UnityEngine.Random.onUnitSphere * dv * tof * evade;
                g.Firing = true;
                g.BurstUntil = now + Mathf.Max(0.1f, Flak.CfgBurst == null ? 0.6f : Flak.CfgBurst.Value)
                    * UnityEngine.Random.Range(0.8f, 1.25f);
                g.NextShot = now;
            }
            float interval = Flak.Interval();
            if (g.NextShot < now - 0.1f) g.NextShot = now;
            int n = 0;
            while (now >= g.NextShot && n < 4 && g.Rounds > 0)
            {
                int b = g.GunIdx;
                g.GunIdx = 1 - b;
                Flak.Shoot(g, b, aim, true, g.FuzeRange, true, g.Hostile);
                g.Rounds--;
                g.Fired++;
                g.NextShot += interval;
                n++;
            }
            if (g.Rounds <= 0)
            {
                g.Firing = false;
                Flak.StartReload(g, Flak.ReloadSeconds() * (both ? 1f : 1.5f));
            }
        }

        /// <summary>A random aiming error of about <paramref name="size"/>,
        /// across the line of sight and along the target's track (where a
        /// misjudged lead puts it).</summary>
        static Vector3 Scatter(GepardGun.Contact t, Vector3 mid, float size)
        {
            Vector3 los = (t.Pos - mid).normalized;
            Vector3 e = UnityEngine.Random.insideUnitSphere;
            e -= los * Vector3.Dot(e, los) * 0.7f;
            return e * size;
        }

        /// <summary>An aiming error of EXACTLY about <paramref name="size"/>
        /// (0.85..1.15), across the line of sight: the first burst of a new
        /// engagement is always visibly off - Scatter's random length could
        /// put it on the aircraft by chance (Q2: "bursts start off-target and
        /// walk closer").</summary>
        internal static Vector3 Offset(GepardGun.Contact t, Vector3 mid, float size)
        {
            Vector3 los = (t.Pos - mid).normalized;
            Vector3 e = Vector3.ProjectOnPlane(UnityEngine.Random.onUnitSphere, los);
            if (e.sqrMagnitude < 1e-4f) e = Vector3.ProjectOnPlane(Vector3.up, los);
            if (e.sqrMagnitude < 1e-4f) e = Vector3.right;
            return e.normalized * size * UnityEngine.Random.Range(0.85f, 1.15f);
        }

        static string Describe(GepardGun.Contact c)
        {
            switch (c.Kind)
            {
                case 0: return "a helicopter";
                case 6: return "an aircraft (" + (c.Src != null ? c.Src.Name : "?") + ")";
                case 2: return "an FPV drone";
                case 3: return "a reconnaissance drone";
                default: return "a target";
            }
        }

        // ------------------------------------------------------------ targets

        /// <summary>Every air target in reach, and of those the ones this crew
        /// may fire on; the closest valid one (the assigned one first).</summary>
        static void Search(Flak.Gun g)
        {
            Vector3 eye = Flak.Mid(g);
            float range = Flak.RangeU;
            Collect(g.Air, eye, range);

            string side = Airfield.Faction();
            g.Hostile.Clear();
            GepardGun.Contact best = null, assigned = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < g.Air.Count; i++)
            {
                GepardGun.Contact c = g.Air[i];
                if (c.Go == null) continue;
                bool hostile, friendly;
                Allegiance(c, side, out hostile, out friendly);
                if (friendly) continue;
                bool zone = g.Mode == FlakMode.ZoneDefence;
                bool inZone = zone && InZone(g, c.Pos);
                bool candidate = hostile || (zone && g.ZoneStrict && inZone) || (c.Go == g.Assigned && !friendly);
                if (!candidate) continue;
                if (zone && !inZone && c.Go != g.Assigned) continue;
                if (g.Mode == FlakMode.AssignedOnly && c.Go != g.Assigned) continue;
                if (!Airborne(c)) continue;
                g.Hostile.Add(c);
                float d = Vector3.Distance(eye, c.Pos);
                if (d > range || c.Pos.y - eye.y > Flak_Ceiling()) continue;
                if (!Sight(g, eye, c)) continue;
                if (c.Go == g.Assigned) assigned = c;
                if (d < bestD) { best = c; bestD = d; }
            }
            if (assigned != null) best = assigned;
            // Stay on the target being walked in unless the assigned one appears.
            if (g.Target != null && g.Target.Go != null && best != null && best != g.Target && assigned == null
                && g.Hostile.Contains(g.Target) && Vector3.Distance(eye, g.Target.Pos) <= range)
                best = g.Target;
            if (best != g.Target)
            {
                g.Held = 0f;
                g.Engaged = false;
            }
            g.Target = best;
        }

        static float Flak_Ceiling()
        {
            return Mathf.Max(50f, Flak.CfgCeiling == null ? 1500f : Flak.CfgCeiling.Value) * Flak.K;
        }

        internal static void Collect(List<GepardGun.Contact> air, Vector3 eye, float range)
        {
            float now = Time.time;
            _heli.Clear();
            PlayerHeli.MissileTargets(_heli);
            for (int i = 0; i < _heli.Count; i++) Offer(air, _heli[i], 0, 0, null, eye, range);
            _planes.Clear();
            GepardAir.Collect(_planes);
            for (int i = 0; i < _planes.Count; i++) Offer(air, _planes[i].Go, 6, 0, _planes[i].Src, eye, range);
            Drones(air, typeof(Drone.Net), "_fremde", 2, eye, range);
            Drones(air, typeof(SurvNet), "_ghosts", 3, eye, range);
            for (int i = air.Count - 1; i >= 0; i--)
            {
                GepardGun.Contact c = air[i];
                bool gone = c.Go == null || !c.Go.activeInHierarchy || now - c.SeenAt > 1.2f
                    || (c.Kind == 0 && PlayerHeli.MissileTarget(c.HeliView) == null)
                    || (c.Kind == 6 && !GepardAir.Alive(c));
                if (gone) air.RemoveAt(i);
            }
        }

        static void Drones(List<GepardGun.Contact> air, Type type, string field, int kind, Vector3 eye, float range)
        {
            FieldInfo f = type == null ? null : AccessTools.Field(type, field);
            System.Collections.IDictionary dict = f == null ? null : f.GetValue(null) as System.Collections.IDictionary;
            if (dict == null) return;
            foreach (System.Collections.DictionaryEntry pair in dict)
            {
                object item = pair.Value;
                if (item == null) continue;
                FieldInfo gf = AccessTools.Field(item.GetType(), "Go");
                GameObject go = gf == null ? null : gf.GetValue(item) as GameObject;
                Offer(air, go, kind, pair.Key is int ? (int)pair.Key : 0, null, eye, range);
            }
        }

        static void Offer(List<GepardGun.Contact> air, GameObject go, int kind, int actor, GepardAir.Source src,
                          Vector3 eye, float range)
        {
            if (go == null || !go.activeInHierarchy) return;
            GepardGun.Contact c = null;
            for (int i = 0; i < air.Count; i++)
                if (air[i].Go == go) { c = air[i]; break; }
            if (c == null)
            {
                if ((go.transform.position - eye).sqrMagnitude > range * range * 1.3f) return;
                c = new GepardGun.Contact();
                c.Go = go;
                c.Kind = kind;
                c.Actor = actor;
                c.Src = src;
                if (kind == 0) c.HeliView = PlayerHeli.MissileView(go);
                GepardGun.Measure(c);
                c.Pos = go.transform.TransformPoint(c.LocalCentre);
                air.Add(c);
            }
            c.SeenAt = Time.time;
        }

        /// <summary>Position and a LAGGING velocity estimate - a crew leads by
        /// eye, not by radar (TrackingLag, the Gepard's radar: 5).</summary>
        static void Follow(Flak.Gun g, float dt)
        {
            float lag = Mathf.Max(0.2f, Flak.CfgLag == null ? 2f : Flak.CfgLag.Value) * Flak.DirTracking;
            FollowAll(g.Air, dt, lag);
        }

        internal static void FollowAll(List<GepardGun.Contact> air, float dt, float rate)
        {
            if (dt <= 0f) return;
            float k = 1f - Mathf.Exp(-dt * rate);
            for (int i = 0; i < air.Count; i++)
            {
                GepardGun.Contact c = air[i];
                if (c.Go == null) continue;
                Vector3 p = c.Go.transform.TransformPoint(c.LocalCentre);
                Vector3 v = (p - c.Pos) / dt;
                if (v.sqrMagnitude > 4000000f) v = c.Vel;   // a teleport, not a speed
                c.Vel = c.VelInit ? Vector3.Lerp(c.Vel, v, k) : v;
                c.VelInit = true;
                c.Pos = p;
            }
        }

        /// <summary>Who is aboard. <paramref name="friendly"/>: a player of the
        /// crew's own faction - never fired on, whoever else is aboard.
        /// <paramref name="hostile"/>: a player of a hostile faction (for a
        /// drone: its pilot).</summary>
        static void Allegiance(GepardGun.Contact c, string side, out bool hostile, out bool friendly)
        {
            hostile = false;
            friendly = false;
            if (c.Kind == 2 || c.Kind == 3)
            {
                GameObject pilot = Crocodile.PlayerByActor(c.Actor);
                string f = pilot == null ? null : Fraktion.Spielerseite(pilot);
                if (f == null) return;
                if (f == Fraktion.Eigene(side)) friendly = true;
                else hostile = Fraktion.Feind(side, f);
                return;
            }
            float reach = Mathf.Max(4f, c.Radius * 1.5f) + 2f;
            List<GameObject> players = GepardCrew.Spieler();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null || (go.transform.position - c.Pos).sqrMagnitude > reach * reach) continue;
                string f = Fraktion.Spielerseite(go);
                if (f == null) continue;
                if (f == Fraktion.Eigene(side)) friendly = true;
                else if (Fraktion.Feind(side, f)) hostile = true;
            }
        }

        static bool InZone(Flak.Gun g, Vector3 p)
        {
            Vector3 d = p - g.ZoneCentre;
            d.y = 0f;
            if (d.sqrMagnitude > g.ZoneRadius * g.ZoneRadius) return false;
            if (g.ZoneCeiling <= 0f) return true;
            float ground;
            if (!EastWorld.TerrainHeight(p, out ground)) ground = g.ZoneCentre.y;
            return p.y - ground <= g.ZoneCeiling;
        }

        /// <summary>In the air: more than MinTargetHeight over whatever is
        /// under it. A landed helicopter is not an air target.</summary>
        internal static bool Airborne(GepardGun.Contact c)
        {
            float min = Mathf.Max(0f, Flak.CfgMinHeight == null ? 3f : Flak.CfgMinHeight.Value) * Flak.K;
            if (min <= 0f) return true;
            Vector3 origin = c.Pos;
            float rest = min + c.Radius + 2f;
            for (int i = 0; i < 4 && rest > 0f; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, Vector3.down, out hit, rest,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return true;
                if (hit.transform == c.Go.transform || hit.transform.IsChildOf(c.Go.transform))
                {
                    rest -= hit.distance + 0.1f;
                    origin = hit.point + Vector3.down * 0.1f;
                    continue;
                }
                return hit.point.y < c.Pos.y - c.Radius * 0.5f - min;
            }
            return true;
        }

        /// <summary>A clear line from the gun to the target: terrain and
        /// buildings hide it; the emplacement and the target itself do not.</summary>
        static bool Sight(Flak.Gun g, Vector3 from, GepardGun.Contact c)
        {
            Vector3 d = c.Pos - from;
            float dist = d.magnitude;
            if (dist < 1f) return true;
            Vector3 dir = d / dist;
            Vector3 origin = from;
            float rest = dist;
            for (int i = 0; i < 5 && rest > 0.5f; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, rest, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return true;
                Transform t = hit.transform;
                if (t == c.Go.transform || t.IsChildOf(c.Go.transform)) return true;
                if (g.Owner != null && t.IsChildOf(g.Owner) || Crewman(g, t))
                {
                    rest -= hit.distance + 0.2f;
                    origin = hit.point + dir * 0.2f;
                    continue;
                }
                return Vector3.Distance(hit.point, c.Pos) < c.Radius + 2f;
            }
            return true;
        }

        static bool Crewman(Flak.Gun g, Transform t)
        {
            return (g.Gunner != null && t.IsChildOf(g.Gunner.transform))
                || (g.Loader != null && t.IsChildOf(g.Loader.transform));
        }
    }

    // =====================================================================
    // A player at the gun

    /// <summary>
    /// Taking a gun over when its crew is dead: the prompt, the sight camera
    /// (CameraOwner.Flak), laying by mouse at the mount's slew limits,
    /// firing, reloading, leaving.
    /// </summary>
    internal static class FlakPlayer
    {
        static Flak.Gun _near;
        static string _nearWhy;
        static float _cmdYaw, _cmdPitch;
        static bool _zoom;
        static Vector3 _stoodAt;
        static readonly List<GepardGun.Contact> _air = new List<GepardGun.Contact>();
        static float _nextLook, _nextShot;
        static bool _firing;
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

        static void Hint(string s, float seconds) { _hint = s; _hintUntil = Time.time + seconds; }

        /// <summary>Every client: which gun the local player stands at, and
        /// the key that mans or leaves it.</summary>
        internal static void Tick(List<Flak.Gun> guns)
        {
            GameObject me = MapTools.LocalPlayer();
            if (Flak._manned != null)
            {
                if (me == null) { Leave("no local player"); return; }
                if ((me.transform.position - _stoodAt).sqrMagnitude > 25f * 25f) { Leave("moved away"); return; }
                if (Input.GetKeyDown(Key()) || Input.GetKeyDown(KeyCode.Escape)) { Leave("left the sight"); return; }
                return;
            }
            _near = null;
            _nearWhy = null;
            if (me == null || !(Flak.CfgTakeover == null || Flak.CfgTakeover.Value)) return;
            float reach = Mathf.Max(2f, Flak.CfgReach == null ? 7f : Flak.CfgReach.Value);
            for (int i = 0; i < guns.Count; i++)
            {
                Flak.Gun g = guns[i];
                if (g.SeatGunner == null) continue;
                Vector3 d = me.transform.position - g.SeatGunner.position;
                d.y *= 0.5f;
                if (d.sqrMagnitude > reach * reach) continue;
                _near = g;
                if (Flak.Up(g.Gunner) || Flak.Up(g.Loader)) _nearWhy = "The crew is at this gun.";
                else if (Time.time < g.ClaimedUntil) _nearWhy = "Another player is at this gun.";
                break;
            }
            if (_near == null || _nearWhy != null) return;
            if (GameUi.KeyDown(Key())) Enter(_near, me);
        }

        static void Enter(Flak.Gun g, GameObject me)
        {
            if (!CameraOwner.Request(CameraOwner.Flak, true, "ZU-23-2")) { Hint("The camera is busy.", 2f); return; }
            Flak._manned = g;
            _stoodAt = me.transform.position;
            _cmdYaw = g.Yaw;
            _cmdPitch = g.Pitch;
            _zoom = false;
            _firing = false;
            _air.Clear();
            if (g.Rounds < 0) g.Rounds = Mathf.Max(2, Flak.CfgRoundsPerLoad == null ? 100 : Flak.CfgRoundsPerLoad.Value);
            g.WantYaw = g.Yaw;
            g.WantPitch = g.Pitch;
            Flak.Log("the local player mans " + g.Id + " (" + g.Rounds + " rounds).");
        }

        internal static void Leave(string why)
        {
            Flak.Gun g = Flak._manned;
            if (g == null) return;
            Flak._manned = null;
            g.Firing = false;
            Flak.Publish(g, true, true);
            _firing = false;
            CameraOwner.Release(CameraOwner.Flak);
            Flak.Log("the local player left " + g.Id + " (" + why + ").");
        }

        /// <summary>The manning player's laying and trigger, every frame.</summary>
        internal static void Lay(Flak.Gun g, float dt)
        {
            float sens = Mathf.Max(0.1f, Flak.CfgSensitivity == null ? 2f : Flak.CfgSensitivity.Value) * (_zoom ? 0.35f : 1f);
            _cmdYaw += GameUi.Axis("Mouse X") * sens;
            _cmdPitch += GameUi.Axis("Mouse Y") * sens;
            float min = Flak.CfgPitchMin == null ? -10f : Flak.CfgPitchMin.Value;
            float max = Flak.CfgPitchMax == null ? 90f : Flak.CfgPitchMax.Value;
            _cmdPitch = Mathf.Clamp(_cmdPitch, min, max);
            if (_cmdYaw > 180f) _cmdYaw -= 360f;
            if (_cmdYaw < -180f) _cmdYaw += 360f;
            // The crank cannot outrun the gun by more than a few degrees.
            float dy = Mathf.DeltaAngle(g.Yaw, _cmdYaw);
            if (Mathf.Abs(dy) > 25f) _cmdYaw = g.Yaw + Mathf.Sign(dy) * 25f;
            if (Mathf.Abs(_cmdPitch - g.Pitch) > 20f) _cmdPitch = g.Pitch + Mathf.Sign(_cmdPitch - g.Pitch) * 20f;
            g.WantYaw = _cmdYaw;
            g.WantPitch = _cmdPitch;
            _zoom = GameUi.Button(1);

            if (Time.time >= _nextLook)
            {
                _nextLook = Time.time + 0.4f;
                FlakFire.Collect(_air, Flak.Mid(g), Flak.RangeU * 1.2f);
            }
            FlakFire.FollowAll(_air, dt, 5f);

            if (GameUi.KeyDown(KeyCode.R) && !g.Reloading && g.Rounds < Mathf.Max(2, Flak.CfgRoundsPerLoad == null ? 100 : Flak.CfgRoundsPerLoad.Value))
                Flak.StartReload(g, Flak.ReloadSeconds() * 1.5f);
            bool held = GameUi.Button(0) && !g.Reloading;
            g.Firing = false;
            g.FuzeRange = Flak.MaxFuze;
            if (held && g.Rounds <= 0) { Flak.StartReload(g, Flak.ReloadSeconds() * 1.5f); held = false; }
            if (held)
            {
                float now = Time.time;
                float interval = Flak.Interval();
                if (_nextShot < now - 0.1f) _nextShot = now;
                int n = 0;
                while (now >= _nextShot && n < 4 && g.Rounds > 0)
                {
                    int b = g.GunIdx;
                    g.GunIdx = 1 - b;
                    // A player's rounds are not set: they burst at the end of their flight.
                    Flak.Shoot(g, b, Vector3.zero, false, Flak.MaxFuze, true, _air);
                    g.Rounds--;
                    _nextShot += interval;
                    n++;
                }
                g.Firing = g.Rounds > 0;
            }
            if (_firing != g.Firing) { _firing = g.Firing; Flak.Publish(g, true, true); }
            else Flak.Publish(g, true, false);
        }

        /// <summary>The sight camera, after the game has written its own: at
        /// the collimator on the cradle, looking along the bore.</summary>
        internal static void LateTick()
        {
            Flak.Gun g = Flak._manned;
            if (g == null || g.Eye == null) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                cam.transform.position = g.Eye.position;
                cam.transform.rotation = g.Cradle != null ? g.Cradle.rotation : g.Eye.rotation;
                cam.fieldOfView = _zoom ? 18f : 55f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Flak camera: " + ex);
                Leave("camera error");
            }
        }

        // ---------------------------------------------------------------- HUD

        static Texture2D _white;
        static GUIStyle _text;
        static readonly Color Amber = new Color(1f, 0.78f, 0.3f, 0.95f);

        internal static void Draw(List<Flak.Gun> guns)
        {
            try
            {
                if (Flak._manned == null && _near == null && (_hint == null || Time.time > _hintUntil)) return;
                if (_white == null)
                {
                    _white = new Texture2D(1, 1);
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply();
                }
                if (_text == null)
                {
                    _text = new GUIStyle(GUI.skin.label);
                    _text.fontSize = 15;
                }
                float w = Screen.width, h = Screen.height;
                Flak.Gun g = Flak._manned;
                if (g != null)
                {
                    Reticle(w * 0.5f, h * 0.5f, Mathf.Min(w, h) * (_zoom ? 0.3f : 0.1f));
                    string line = "ZU-23-2   " + (g.Reloading
                        ? "RELOADING " + Mathf.Max(0f, g.ReloadUntil - Time.time).ToString("0", CultureInfo.InvariantCulture) + " s"
                        : g.Rounds + " rds") + "   elev " + g.Pitch.ToString("0", CultureInfo.InvariantCulture)
                        + "   LMB fire  RMB sight  R reload  " + Key() + " leave";
                    Label(new Rect(0f, h - 70f, w, 26f), line, Amber);
                    string order = null;
                    try { if (Flak.SightOrder != null) order = Flak.SightOrder(g.Id); }
                    catch { }
                    if (!string.IsNullOrEmpty(order)) Label(new Rect(0f, h - 100f, w, 26f), order, Color.white);
                }
                else if (_near != null)
                {
                    string s = _nearWhy ?? ("[" + Key() + "] Man the ZU-23-2");
                    Label(new Rect(0f, h * 0.62f, w, 26f), s, Color.white);
                }
                if (_hint != null && Time.time <= _hintUntil)
                    Label(new Rect(0f, h * 0.66f, w, 26f), _hint, Amber);
            }
            catch { }
        }

        static void Label(Rect r, string s, Color c)
        {
            // Centred by hand: the alignment enum lives in a module the build
            // does not reference.
            float tw = _text.CalcSize(new GUIContent(s)).x;
            r = new Rect(r.x + (r.width - tw) * 0.5f, r.y, tw + 4f, r.height);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), s, _text);
            GUI.color = c;
            GUI.Label(r, s, _text);
            GUI.color = Color.white;
        }

        /// <summary>The collimator sight: a centre cross and two lead rings.</summary>
        static void Reticle(float cx, float cy, float r)
        {
            GUI.color = Amber;
            GUI.DrawTexture(new Rect(cx - 12f, cy - 1f, 24f, 2f), _white);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 12f, 2f, 24f), _white);
            Ring(cx, cy, r * 0.5f);
            Ring(cx, cy, r);
            GUI.color = Color.white;
        }

        static void Ring(float cx, float cy, float r)
        {
            int n = 48;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                GUI.DrawTexture(new Rect(cx + Mathf.Cos(a) * r - 1f, cy + Mathf.Sin(a) * r - 1f, 2f, 2f), _white);
            }
        }
    }

    // =====================================================================
    // Sound

    /// <summary>
    /// The 23 mm report at the gun and the far-carrying bang of a burst,
    /// synthesised once (the game has no flak sound) and played on a small
    /// pool of 3D sources. A burst is heard when its sound arrives: delayed
    /// by the speed of sound (343 m/s x 2.8).
    /// </summary>
    internal static class FlakSound
    {
        static AudioClip _report, _burst;
        static readonly List<AudioSource> _pool = new List<AudioSource>();
        static GameObject _host;
        static int _next;
        static float _lastBurst;

        struct Pending { public float At; public Vector3 Pos; }
        static readonly List<Pending> _pending = new List<Pending>();

        internal static void Report(Vector3 at)
        {
            if (_report == null) _report = Make(false);
            Play(_report, at, 25f, 2800f, 0.85f);
        }

        internal static void Burst(Vector3 at)
        {
            // Twenty-seven puffs a second would be a roar, not bangs: a few a second.
            if (Time.time - _lastBurst < 0.09f) return;
            _lastBurst = Time.time;
            Camera cam = Camera.main;
            float d = cam == null ? 0f : Vector3.Distance(cam.transform.position, at);
            Pending p;
            p.At = Time.time + d / (343f * Flak.K);
            p.Pos = at;
            if (_pending.Count < 64) _pending.Add(p);
        }

        internal static void Tick()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pending[i].At) continue;
                if (_burst == null) _burst = Make(true);
                Play(_burst, _pending[i].Pos, 120f, 9000f, 1f);
                _pending.RemoveAt(i);
            }
        }

        static void Play(AudioClip clip, Vector3 at, float min, float max, float volume)
        {
            try
            {
                if (clip == null) return;
                if (_host == null)
                {
                    _host = new GameObject("NDR Flak sound");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _pool.Clear();
                }
                AudioSource s = null;
                for (int i = 0; i < _pool.Count; i++)
                {
                    AudioSource c = _pool[(_next + i) % _pool.Count];
                    if (c != null && !c.isPlaying) { s = c; break; }
                }
                if (s == null && _pool.Count < 20)
                {
                    GameObject go = new GameObject("NDR Flak voice");
                    go.transform.SetParent(_host.transform, false);
                    s = go.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.loop = false;
                    s.spatialBlend = 1f;
                    s.dopplerLevel = 0f;
                    s.rolloffMode = AudioRolloffMode.Logarithmic;
                    _pool.Add(s);
                }
                if (s == null) { s = _pool[_next % _pool.Count]; _next++; }
                s.transform.position = at;
                s.minDistance = min;
                s.maxDistance = max;
                s.volume = volume * UnityEngine.Random.Range(0.85f, 1f);
                s.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
                s.clip = clip;
                s.Play();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Flak sound: " + ex.Message);
            }
        }

        /// <summary>The report: a sharp crack over a short low thump. The
        /// burst: a crack and a long rolling rumble.</summary>
        static AudioClip Make(bool burst)
        {
            const int rate = 44100;
            float seconds = burst ? 2.2f : 0.32f;
            float[] data = new float[Mathf.RoundToInt(rate * seconds)];
            int seed = burst ? 2311 : 23;
            float low = 0f, low2 = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float t = (float)i / rate;
                seed = seed * 1103515245 + 12345;
                float noise = (((seed >> 16) & 0x7fff) / 16383.5f) - 1f;
                low = low * 0.93f + noise * 0.07f;
                low2 = low2 * 0.985f + noise * 0.015f;
                float v;
                if (burst)
                {
                    float crack = noise * 1.3f * Mathf.Exp(-t * 60f);
                    float body = low * 2.6f * Mathf.Exp(-t * 7f);
                    float rumble = low2 * 7f * Mathf.Exp(-t * 1.6f);
                    float thump = Mathf.Sin(2f * Mathf.PI * 48f * t) * 0.6f * Mathf.Exp(-t * 4f);
                    v = crack + body + rumble + thump;
                }
                else
                {
                    float crack = noise * 1.2f * Mathf.Exp(-t * 90f);
                    float body = low * 2.2f * Mathf.Exp(-t * 22f);
                    float thump = Mathf.Sin(2f * Mathf.PI * 95f * t) * 0.7f * Mathf.Exp(-t * 26f);
                    v = crack + body + thump;
                }
                data[i] = Mathf.Clamp(v * 0.6f, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create(burst ? "NDR flak burst" : "NDR flak report", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    // =====================================================================
    // Network

    /// <summary>
    /// One Photon event (Flak NetworkEventCode, 198): { 1, gun, yaw, pitch,
    /// firing, fuze range, player } ten times a second and on every trigger
    /// change, unreliable, from whoever lays the gun. The same reflection path
    /// as GepardNet (PhotonNetwork.RaiseEvent, the OnEventCall field).
    /// </summary>
    internal static class FlakNet
    {
        const int Pose = 1;
        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optType;

        static int Code() { return Flak.CfgEventCode == null ? 198 : Flak.CfgEventCode.Value; }

        /// <summary>The other channels, as configured on this client.</summary>
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
                && code <= PlayerAn2.CfgEventCode.Value + 5) return true;
            // crocodile, mortar (190..195), Stinger, AP mine
            return code == 164 || (code >= 190 && code <= 195) || code == 191 || code == 196;
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
                MethodInfo mine = typeof(FlakNet).GetMethod("OnPhotonEvent", BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                onEvent.SetValue(null, Delegate.Combine(onEvent.GetValue(null) as Delegate, handler));
                _hooked = true;
                Flak.Log("event code " + code + " hooked.");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("Flak network not hooked - the guns are not shown to other "
                    + "players: " + ex.Message);
            }
        }

        internal static void SendPose(int gun, float yaw, float pitch, bool firing, float fuze, bool player)
        {
            if (!_hooked) return;
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                float[] data = new float[] { Pose, gun, yaw, pitch, firing ? 1f : 0f, fuze, player ? 1f : 0f };
                _raise.Invoke(null, new object[] { (byte)Code(), data, false, opts });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Flak network send: " + ex.Message); }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 7) return;
                for (int i = 0; i < f.Length; i++)
                    if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                if (Mathf.RoundToInt(f[0]) != Pose) return;
                Flak.OnPose(Mathf.RoundToInt(f[1]), Mathf.Repeat(f[2] + 180f, 360f) - 180f,
                    Mathf.Clamp(f[3], -15f, 90f), f[4] > 0.5f, Mathf.Clamp(f[5], 30f, 20000f), f[6] > 0.5f, sender);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Flak network receive: " + ex.Message);
            }
        }
    }
}

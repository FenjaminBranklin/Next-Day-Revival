// Next Day: Survival - Revival Toolkit
//
// THE HEAVY FLAK (P4, N6): the two gunned AA positions of the east airfield
// and a two-gun battery in the military town fight as Soviet 85 mm 52-K
// (M1939) anti-aircraft guns. The Gepard stays the short-range gun.
//
// WHAT THIS IS, and what it is built from:
//
//   1  THE GUN. The AA positions of east_af_shelters (unity/EastTile/Tools/
//      airfield_assembly.py: "AA position north" at (4385, 1320) and "AA
//      position S2-S3" at (4300, -550), both yaw 90) carry a derelict ZU-23-2
//      merged into the ring's mesh. At runtime that mesh is swapped for the
//      bundle's own gunless twin (the renderers of "AA position V3 (empty)")
//      and a live 52-K is set on the platform: the meshes of k52_build.py
//      (assets/k52_{base,mount,cradle,barrel}_lod0-3.ndmesh, one texture
//      atlas, pivots in k52_rig.txt), four parts at their real pivots under
//      one LODGroup: the cruciform carriage with its levelling jacks stands,
//      the top carriage with the small shield traverses (360 degrees), the
//      cradle elevates (-3 to +82), the long L/55 barrel RECOILS along the
//      cradle and runs out again (B2: the cradle jumps a little with it).
//      Box colliders from k52_rig.txt: the carriage static, mount + cradle
//      one kinematic compound; the ring's leftover derelict ZU box is
//      switched off. The military town's battery (MT-AA2a/b, on
//      the school sports ground) stands on the ground. Without the shelters
//      bundle (greybox fallback) the airfield guns stand on the ground at the
//      same two points.
//   2  THE ROUNDS are the Gepard's round loop (GepardShots.Fire with a Spec):
//      real projectiles with gravity in world units (1 m = 2.8 u), one every
//      few seconds, deliberately SLOW (MuzzleVelocity) so the fight can be
//      read from the cockpit. Every round is time-fused: the crew sets the
//      fuze to the range it aims at, and a round that meets nothing bursts
//      there as a big black puff with a far-carrying bang (delayed by the
//      speed of sound); its fragments hit an aircraft within BurstRadius.
//      Damage per target class is the Gepard's (GepardGun.Hit): a helicopter
//      or the An-2 goes down after HeliHits of them.
//   3  THE CREW. Two men of the defender faction ([Airfield] DefenderFaction,
//      the town's [MilitaryTown] faction for its guns), spawned by the master
//      with the keys "flak/<id>/g" and "flak/<id>/c" (THE SLASH IS LOAD
//      BEARING: Revival.GroundEnemies.cs deletes keyed NPCs without one).
//      Every client puts them on the layers' seats of the top carriage after
//      the animator with the game's seated clip, turning with the gun.
//   4  THE GUNNER (master only). Air targets only - a helicopter or the An-2
//      with a player of a hostile faction aboard, a drone flown by one, or
//      the admin's NPC flyover - airborne and within range. BRACKETING: the
//      first shot is far off (InitialError); after every shot the crew
//      corrects by the puff it saw (the error shrinks by the walk factor)
//      down to ErrorFloor, so the puffs visibly walk toward the aircraft,
//      seconds apart. A pilot who changes course between two shots throws
//      the correction off again (EvadeFactor). With nothing to shoot at the
//      crew sweeps the sky ahead (B2, FlakFire.Watch) - the gun never parks.
//   5  THE RADAR (Revival.TowerRadar.cs). With an operator at the tower's
//      console the guns are radar-directed (SetRadarDirected + the direction
//      scales): RadarRange and RadarWalkFactor - long range, fast bracketing.
//      With the radar down, dark or unmanned (a dead or WOUNDED operator
//      counts as gone - Up) the crews lay by eye: VisualRange and
//      WalkFactor - visual range only, slow bracketing.
//   6  A PLAYER takes a gun over when its crew is dead: walk up to the
//      gunner's seat, press the turret key (G). The mouse cranks the mount at
//      the same slew limits, the left button fires. B2 camera: over the
//      shoulder behind the breech by default, the right button holds the
//      zoomed sight above the shield; both look along the commanded lay, a
//      ring shows the bore. The mouse wheel sets the fuze range (F: back to the
//      automatic fuze, the range of the aircraft nearest the bore), R
//      brings up a fresh rack, G leaves.
//   7  THE WIRE. One Photon event (FlakNet, code 198): pose, shot counter
//      and fuze range of a gun ten times a second and on every shot from
//      whoever lays it (the master for a crew, the manning player's client),
//      so every client sees the gun turn, the barrel recoil and the same
//      puffs in the sky. Rounds are drawn by every client; only the layer's
//      rounds do damage.
//   8  THE API for P5 (tower radar) and P6a (no-fly zones): Flak.Guns() lists
//      each gun with its state; AssignTarget / ClearTarget / WeaponsFree /
//      HoldFire / ZoneDefence command one gun or all. Commands act where the
//      fire control runs - on the master (Flak.Authority).
//
// WHAT IS NOT PROVEN WITHOUT THE GAME: how the gun sits in the ring and on
// the sports ground, whether the seated clip reaches the handwheels, the look
// of the recoil and the puffs and the feel of the bracketing.
// docs/ai/tasks/k52-flak.md lists the checks and the tuning numbers.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only, no BOM.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalPlugin.cs          BindConfig / Install / Tick / LateUpdate / OnGUI.
//   Revival.CameraTurret.cs   CameraOwner.Flak and its LateTick dispatch.
//   RevivalGepard.cs          GepardShots.Spec + Fire(Spec, ...), the Proximity
//                             fuze overload, GepardGun.Nearest, Hit(heliHits),
//                             GepardFx.Flak(at, scale), Spec.PuffScale, the
//                             once-a-frame round Tick.
//   RevivalGepardCrew.cs      GepardAir.Hit(gunHits); Steht/Parken/Agent/Ruhig/
//                             Spieler internal.
//   RevivalTechnicalCrew.cs   Sitzen internal (the seated clip).
//   Revival.Airfield.cs       Faction internal (the crew's side).
//   Revival.MilitaryTown.cs   On, Faction (the town battery's side).
//   Revival.TowerRadar.cs     SetFireDirection + SetRadarDirected by tier.
//   Revival.NpcCombat.cs      NpcWar.GroundDowned (the wounded state, N6).

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
    /// The 52-K 85 mm guns of the airfield and the military town: config,
    /// the public API, and the frame
    /// (find the emplacements, build the guns, crews, fire control, a manning
    /// player, the wire).
    /// </summary>
    public static class Flak
    {
        /// <summary>World units per real metre (the east tile's 2.8,
        /// BuildContent.UnitsPerMetre).</summary>
        internal const float K = 2.8f;

        // ------------------------------------------------------------ config

        internal static ConfigEntry<bool> CfgEnabled, CfgTown, CfgNpcCrew, CfgTakeover,
            CfgTracers, CfgMuzzleFlash, CfgRecoil, CfgCasings, CfgPuffs, CfgGunSound,
            CfgBurstSound, CfgSeated;
        internal static ConfigEntry<float> CfgVelocity, CfgRange, CfgRadarRange, CfgSelfDestruct, CfgCeiling,
            CfgRpm, CfgDispersion, CfgFuze, CfgSplash, CfgTurn, CfgElev, CfgAccel, CfgPitchMin,
            CfgPitchMax, CfgInitialError, CfgWalk, CfgRadarWalk, CfgFloor, CfgEvade, CfgLag, CfgReaction,
            CfgReload, CfgRespawn, CfgSeatDrop, CfgReach, CfgSensitivity, CfgMinHeight;
        internal static ConfigEntry<int> CfgHeliHits, CfgRoundsPerLoad, CfgEventCode;

        public static void BindConfig(ConfigFile cfg)
        {
            // [Flak]: switches and plumbing. [Flak52K]: the gun's numbers - a
            // section of its own since N6, so a config file that still holds
            // the ZU-23-2's numbers (1600 rounds a minute...) cannot drive the
            // 85 mm gun.
            const string S = "Flak";
            const string G = "Flak52K";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "The 52-K 85 mm guns on the east airfield's AA positions (acting only with "
                + "[World] EastTile and [Airfield] Enabled).");
            CfgTown = cfg.Bind(S, "MilitaryTownBattery", true,
                "Two more 52-Ks on the military town's school sports ground (with [MilitaryTown] Enabled).");
            CfgNpcCrew = cfg.Bind(S, "NpcCrew", true,
                "Two men of the defender faction man each gun (master spawns them).");
            CfgTakeover = cfg.Bind(S, "PlayerTakeover", true,
                "A player can take a gun over when its crew is dead (turret key, G).");
            CfgTracers = cfg.Bind(S, "Tracers", true,
                "Effect: a short streak on every shell in flight, so it can be seen coming.");
            CfgMuzzleFlash = cfg.Bind(S, "MuzzleFlash", true,
                "Effect: muzzle flash, the muzzle brake's side blast, gun smoke and ground dust.");
            CfgRecoil = cfg.Bind(S, "BarrelRecoil", true,
                "Animation: the barrel runs back with every shot and out again.");
            CfgCasings = cfg.Bind(S, "Casings", true, "Effect: the spent case thrown out of the breech.");
            CfgPuffs = cfg.Bind(S, "FlakPuffs", true,
                "Effect: the black puff of a bursting shell in the sky.");
            CfgGunSound = cfg.Bind(S, "GunSound", true, "Sound: the report of every shot.");
            CfgBurstSound = cfg.Bind(S, "BurstSound", true,
                "Sound: the far-carrying bang of a burst, delayed by the speed of sound.");
            CfgSeated = cfg.Bind(S, "SeatedCrew", true,
                "Animation: the crew sit on the gun's seats with the game's seated clip "
                + "and turn with the mount. Off: they stand on the seat points.");
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
                "Photon event code for the guns' pose and shots. Must not overlap any "
                + "other channel (0..199).");

            CfgVelocity = cfg.Bind(G, "MuzzleVelocity", 550f,
                "Muzzle velocity in metres per second. The real 52-K: 800; slowed on "
                + "purpose so a pilot sees the puffs come (5 s to 2750 m). World speed is x 2.8.");
            CfgRange = cfg.Bind(G, "VisualRange", 1800f,
                "Slant range in metres the crews open fire at when they lay by eye (radar "
                + "down, dark or unmanned).");
            CfgRadarRange = cfg.Bind(G, "RadarRange", 4000f,
                "Slant range in metres with the tower radar manned (radar-directed).");
            CfgSelfDestruct = cfg.Bind(G, "MaxFuzeRange", 4500f,
                "The longest fuze setting: a shell that meets nothing bursts after this many metres at the latest.");
            CfgCeiling = cfg.Bind(G, "Ceiling", 3000f,
                "Highest target altitude above the gun in metres engaged.");
            CfgMinHeight = cfg.Bind(G, "MinTargetHeight", 3f,
                "An aircraft lower than this over the ground (metres) is not airborne and "
                + "not engaged - the guns are for air targets only.");
            CfgRpm = cfg.Bind(G, "RateOfFire", 15f,
                "Shots per minute (the real 52-K: 15 to 20). Every shot is one bracketing step.");
            CfgDispersion = cfg.Bind(G, "Dispersion", 2.0f,
                "Dispersion of a single shell in milliradians (the Gepard: 1.5).");
            CfgFuze = cfg.Bind(G, "DirectHitDistance", 1.5f,
                "A shell passing this close to an aircraft (beyond its own size, world "
                + "units) hits it.");
            CfgSplash = cfg.Bind(G, "BurstRadius", 12f,
                "Metres from a burst within which its fragments hit an aircraft (beyond "
                + "the aircraft's size).");
            CfgHeliHits = cfg.Bind(G, "HeliHits", 2,
                "85 mm hits (direct or fragments) that bring down a helicopter or the An-2.");
            CfgTurn = cfg.Bind(G, "TraverseSpeed", 20f, "Highest traverse rate, degrees per second.");
            CfgElev = cfg.Bind(G, "ElevationSpeed", 12f, "Highest elevation rate, degrees per second.");
            CfgAccel = cfg.Bind(G, "SlewAcceleration", 40f,
                "How fast the heavy mount gets up to speed, degrees per second squared.");
            CfgPitchMin = cfg.Bind(G, "PitchMin", -3f, "Lowest elevation, degrees (the real -3).");
            CfgPitchMax = cfg.Bind(G, "PitchMax", 82f, "Highest elevation, degrees (the real +82).");
            CfgInitialError = cfg.Bind(G, "InitialError", 60f,
                "The crew's aiming error on a new target, milliradians of range.");
            CfgWalk = cfg.Bind(G, "WalkFactor", 0.75f,
                "Laying by eye: the part of the aiming error left after each shot's "
                + "correction (0..1). Larger: the puffs walk in slower.");
            CfgRadarWalk = cfg.Bind(G, "RadarWalkFactor", 0.45f,
                "Radar-directed: the part of the aiming error left after each shot (0..1).");
            CfgFloor = cfg.Bind(G, "ErrorFloor", 5f,
                "The aiming error the crew never gets under, milliradians of range.");
            CfgEvade = cfg.Bind(G, "EvadeFactor", 1.0f,
                "How much a target's change of velocity since the last shot throws the "
                + "correction off (0 = evading does not help).");
            CfgLag = cfg.Bind(G, "TrackingLag", 1.5f,
                "How fast the crew's estimate of the target's velocity follows it, per "
                + "second (the Gepard's radar: 5).");
            CfgReaction = cfg.Bind(G, "Reaction", 3f, "Seconds on a new target before the first shot.");
            CfgRoundsPerLoad = cfg.Bind(G, "Rounds", 20, "Ready rounds at the gun before the next rack is brought up.");
            CfgReload = cfg.Bind(G, "ReloadSeconds", 25f,
                "Bringing up the next rack, seconds (one man left alone: x 1.5).");
        }

        internal static bool On
        {
            get { return Airfield.On && (CfgEnabled == null || CfgEnabled.Value); }
        }

        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        internal static bool B2(ConfigEntry<bool> c) { return c == null || c.Value; }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }

        internal static float Speed { get { return Mathf.Max(100f, F(CfgVelocity, 550f)) * K; } }
        internal static float Gravity { get { return 9.81f * K; } }
        /// <summary>The engagement range now: the radar's with an operator at
        /// the console, the eye's without (SetRadarDirected).</summary>
        internal static float RangeU { get { return RangeMetres(_radar) * K; } }

        /// <summary>The engagement range in metres, radar-directed or by eye.</summary>
        internal static float RangeMetres(bool radar)
        {
            float eye = Mathf.Max(100f, F(CfgRange, 1800f));
            return radar ? Mathf.Max(eye, F(CfgRadarRange, 4000f)) : eye;
        }
        static float SelfDestructU
        {
            get { return Mathf.Max(Mathf.Max(F(CfgRange, 1800f), F(CfgRadarRange, 4000f)), F(CfgSelfDestruct, 4500f)) * K; }
        }
        internal static float CeilingU { get { return Mathf.Max(50f, F(CfgCeiling, 3000f)) * K; } }
        static int RoundsFull { get { return Mathf.Max(1, CfgRoundsPerLoad == null ? 20 : CfgRoundsPerLoad.Value); } }
        internal static int RoundsPerLoad { get { return RoundsFull; } }
        /// <summary>The walk factor now: fast bracketing radar-directed, slow by eye.</summary>
        internal static float Walk
        {
            get { return Mathf.Clamp01(_radar ? F(CfgRadarWalk, 0.45f) : F(CfgWalk, 0.75f)); }
        }

        // ------------------------------------------------------ the emplacements

        /// <summary>The gunned AA positions of the assembly (airfield_assembly.py):
        /// id, holder name, x, z, yaw. The third, "AA position V3 (empty)",
        /// has no gun and lends its mesh. The last two are the military
        /// town's battery on the school sports ground (MT-S1f, 50 x 80 u open
        /// pad at (5605, 660), docs/ai/tasks/military-town.md): no holder,
        /// they stand on the ground facing west, toward the airfield.</summary>
        static readonly string[] Ids = { "AA-N", "AA-S", "MT-AA2a", "MT-AA2b" };
        static readonly string[] Names = { "AA position north", "AA position S2-S3",
            "Town battery west", "Town battery east" };
        static readonly Vector2[] Spots = { new Vector2(4385f, 1320f), new Vector2(4300f, -550f),
            new Vector2(5598f, 642f), new Vector2(5612f, 686f) };
        static readonly float[] SpotYaws = { 90f, 90f, 270f, 270f };
        static readonly bool[] TownSpot = { false, false, true, true };
        const string EmptyName = "AA position V3 (empty)";

        // ------------------------------------------------------------- the gun

        internal sealed class Gun
        {
            public int Index;
            public string Id, Name;
            public bool Town;                   // the military town's battery (its faction)
            public Transform Holder;            // the emplacement; null on the ground
            public Transform Root, Mount, Cradle, Barrel, Muzzle;
            public Transform SeatGunner, SeatLoader, Eye;
            public Transform Shoulder, Sight;   // B2: the gunner's two camera marks, on the mount
            public Transform Owner;             // what the rounds step past
            public float Stroke;                // recoil stroke, world units (k52_rig.txt)

            // pose (every client)
            public float Yaw, Pitch, YawVel, PitchVel;
            public float WantYaw, WantPitch = 12f;
            public float LastWantYaw, LastWantPitch, WantYawRate, WantPitchRate;   // the target's angular rate (feed-forward)
            public float Recoil;                // 1 at the shot, runs out to 0
            public bool CaseDue;                // the case comes out as the barrel runs out

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
            public bool Laying, Firing, Engaged, Reloading;
            public float Held, NextLook, NextShot, LastContact, LastShot = -100f, NextPublish, ReloadUntil;
            public float ScanFrom = -1f;        // B2: when the idle crew started watching the sky
            public int Rounds = -1, Fired, Seq;
            public Vector3 Err, LastVel, Aim;
            public float FuzeRange;

            // the wire (what the others see)
            public float RemoteAt = -100f, RemoteFuze, RemoteShotAt = -100f;
            public int RemoteSeq = -1;
            public float ClaimedUntil = -100f;  // a player at this gun on another client
            public int ClaimActor;
        }

        static readonly List<Gun> _guns = new List<Gun>();
        static float _nextFind, _airfieldSince = -1f;
        static bool _fallbackSaid, _radar;
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

        /// <summary>
        /// N6 radar coupling (Revival.TowerRadar.cs, tier 2): with an operator
        /// at the console the guns fight at RadarRange and bracket with
        /// RadarWalkFactor; without (radar down, dark, operator dead or
        /// wounded) at VisualRange with WalkFactor. Acts on the master.
        /// </summary>
        public static void SetRadarDirected(bool on)
        {
            if (_radar == on) return;
            _radar = on;
            for (int i = 0; i < _guns.Count; i++) _guns[i].NextLook = 0f;
            Log(on ? "radar-directed: range " + (RangeU / K).ToString("0", CultureInfo.InvariantCulture)
                    + " m, walk factor " + Walk.ToString("0.00", CultureInfo.InvariantCulture) + "."
                : "laying by eye: range " + (RangeU / K).ToString("0", CultureInfo.InvariantCulture)
                    + " m, walk factor " + Walk.ToString("0.00", CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>True while the guns are radar-directed.</summary>
        public static bool RadarDirected { get { return _radar; } }

        /// <summary>The side a gun's crew fights for: the town's battery the
        /// military town's faction, the airfield guns the airfield's.</summary>
        internal static string Side(Gun g)
        {
            return g != null && g.Town ? MilitaryTown.Faction() : Airfield.Faction();
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
            bool firing = Time.time - Mathf.Max(g.LastShot, g.RemoteShotAt) < 1.5f;
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
                if (!TownSpot[k] && _sweepHolders[k] == null && name == Names[k]) _sweepHolders[k] = t;
            return true;
        }

        static void Find()
        {
            if (!_sweep.Active)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 5f;
                if (_guns.Count >= Wanted()) return;
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
                if (TownSpot[k])
                {
                    // The town battery stands on the ground: once the tile's
                    // content had the same 40 s to load as the fallback.
                    if (!fallback || !B(CfgTown) || !MilitaryTown.On) continue;
                    Gun t = FlakModel.Build(k, Ids[k], Names[k], null, null, Spots[k], SpotYaws[k], tile);
                    if (t != null) { t.Town = true; _guns.Add(t); }
                    continue;
                }
                if (holders[k] == null && !fallback) continue;
                if (holders[k] == null && !_fallbackSaid)
                {
                    _fallbackSaid = true;
                    RevivalPlugin.L.LogWarning("Flak: the shelters bundle's AA positions are not in the "
                        + "world - the guns stand on the ground at the assembly's spots.");
                }
                Gun g = FlakModel.Build(k, Ids[k], Names[k], holders[k], empty, Spots[k], SpotYaws[k],
                    airfield.isLoaded ? airfield : tile);
                if (g != null) _guns.Add(g);
            }
        }

        /// <summary>How many guns this world should have: the airfield's two,
        /// and the town's two while its battery is on.</summary>
        static int Wanted()
        {
            int n = 0;
            bool town = B(CfgTown) && MilitaryTown.On;
            for (int k = 0; k < Ids.Length; k++) if (!TownSpot[k] || town) n++;
            return n;
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
            float turn = Mathf.Max(1f, F(CfgTurn, 20f));
            float elev = Mathf.Max(1f, F(CfgElev, 12f));
            float acc = Mathf.Max(1f, F(CfgAccel, 40f));
            float min = F(CfgPitchMin, -3f), max = F(CfgPitchMax, 82f);

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

            // The 85 mm barrel: back in a blink, out again in RunOut seconds
            // (the recuperator), the case thrown out halfway home.
            if (g.Recoil > 0f)
            {
                g.Recoil = Mathf.MoveTowards(g.Recoil, 0f, dt / RunOut);
                if (g.CaseDue && g.Recoil < 0.55f)
                {
                    g.CaseDue = false;
                    if (B(CfgCasings) && g.Barrel != null && g.Cradle != null)
                    {
                        Vector3 breech = g.Cradle.position - g.Cradle.forward * 0.9f * K;
                        Vector3 v = -g.Cradle.forward * UnityEngine.Random.Range(4f, 6f)
                            + g.Mount.right * UnityEngine.Random.Range(-1f, 1f) + Vector3.up * 1.5f;
                        GepardFx.Casing(breech, v, 0.35f);
                    }
                }
            }
        }

        /// <summary>Seconds the barrel takes to run out again after a shot.</summary>
        const float RunOut = 0.9f;

        /// <summary>Degrees the cradle jumps at the shot (B2).</summary>
        internal const float Kick = 0.8f;

        /// <summary>World direction of a lay (yaw, pitch relative to the
        /// emplacement) - the inverse of Angles.</summary>
        internal static Vector3 Direction(Gun g, float yaw, float pitch)
        {
            return g.Root.rotation * (Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
        }

        static void Pose(Gun g)
        {
            float r = B(CfgRecoil) ? g.Recoil : 0f;
            if (g.Mount != null) g.Mount.localRotation = Quaternion.Euler(0f, g.Yaw, 0f);
            // B2: the shot jerks the cradle up a little on its trunnions
            // (gone within a quarter of the run-out).
            if (g.Cradle != null) g.Cradle.localRotation = Quaternion.Euler(-g.Pitch - Kick * r * r * r * r, 0f, 0f);
            if (g.Barrel != null)
            {
                // The recoil curve: the shot throws the barrel the whole stroke
                // back at once, the recuperator eases it home (smoothstep).
                g.Barrel.localPosition = new Vector3(0f, 0f, -g.Stroke * r * r * (3f - 2f * r));
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
            _spec.Dispersion = Mathf.Max(0f, F(CfgDispersion, 2f));
            _spec.Fuze = Mathf.Max(0f, F(CfgFuze, 1.5f));
            _spec.Splash = Mathf.Max(0f, F(CfgSplash, 12f)) * K;
            _spec.HeliHits = Mathf.Max(1, CfgHeliHits == null ? 2 : CfgHeliHits.Value);
            _spec.Tracer = B(CfgTracers);
            _spec.Flak = true;
            _spec.PuffFx = B(CfgPuffs);
            _spec.PuffScale = PuffScale;
            _spec.BurstSound = B(CfgBurstSound) ? (Action<Vector3>)FlakSound.Burst : null;
            return _spec;
        }

        /// <summary>The 85 mm puff against the 23 mm one (GepardFx.Flak): a
        /// ball of black smoke some 15 m across that hangs for seconds.</summary>
        const float PuffScale = 2.4f;

        /// <summary>One shell, with its flash, the muzzle brake's side blast,
        /// smoke, dust off the ground, the recoil and the report.
        /// <paramref name="fuze"/> is the range the shell bursts at if it
        /// meets nothing (world units).</summary>
        internal static void Shoot(Gun g, Vector3 aim, bool aimValid, float fuze, bool live,
                                   List<GepardGun.Contact> contacts)
        {
            Transform m = g.Muzzle;
            if (m == null) return;
            Vector3 muzzle = m.position;
            Vector3 dir = g.Cradle != null ? g.Cradle.forward : m.forward;
            if (aimValid)
            {
                Vector3 want = (aim - muzzle).normalized;
                if (Vector3.Angle(dir, want) < 1f) dir = want;
            }
            float range = Mathf.Clamp(fuze, 30f, SelfDestructU);
            // The fuze is set by hand, not exact: a few per cent either way.
            float life = range / Speed * UnityEngine.Random.Range(0.97f, 1.03f);
            GepardShots.Fire(Spec(), g.Owner, muzzle, dir, life, live, contacts);
            g.Recoil = 1f;
            g.CaseDue = true;
            g.LastShot = Time.time;
            if (B(CfgMuzzleFlash))
            {
                // The Gepard's flash, twice over for the bigger gun, and the
                // muzzle brake throws its blast out to both sides.
                GepardFx.Muzzle(muzzle, dir);
                GepardFx.Muzzle(muzzle + dir * 0.6f * K, dir);
                Vector3 side = g.Cradle != null ? g.Cradle.right : Vector3.Cross(Vector3.up, dir).normalized;
                GepardFx.Smoke(muzzle, side, 2.2f);
                GepardFx.Smoke(muzzle, -side, 2.2f);
                GepardFx.Smoke(muzzle, dir, 3.2f);
                // Dust kicked up under the muzzle by the blast.
                Vector3 under = new Vector3(muzzle.x, g.Root.position.y + 0.2f * K, muzzle.z);
                GepardFx.Dust(under, Vector3.up, 3f);
                GepardFx.Dust(under + dir * 2f * K, Vector3.up, 2.5f);
            }
            if (B(CfgGunSound)) FlakSound.Report(muzzle);
        }

        internal static float Interval()
        {
            return 60f / Mathf.Clamp(F(CfgRpm, 15f), 1f, 60f);
        }

        internal static float MaxFuze { get { return SelfDestructU; } }

        /// <summary>Another client lays this gun: follow its pose and fire the
        /// shells it fired (not live), one per step of its shot counter.</summary>
        static void Remote(Gun g, float dt)
        {
            if (g.RemoteSeq < 0 || g.Seq == g.RemoteSeq) return;
            int behind = g.RemoteSeq - g.Seq;
            // A first sample, a new layer or a counter that went back: take it
            // as it is, fire nothing.
            if (g.Seq < 0 || behind < 0 || behind > 3) { g.Seq = g.RemoteSeq; return; }
            g.Seq++;
            g.RemoteShotAt = Time.time;
            Shoot(g, Vector3.zero, false, g.RemoteFuze, false, null);
        }

        internal static void OnPose(int index, float yaw, float pitch, int seq, float fuze, bool player, int sender)
        {
            Gun g = ByIndex(index);
            if (g == null) return;
            // B1: a claim from a dead gunner is no claim (his lease runs out).
            if (player && Crocodile.ActorDown(sender)) player = false;
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
            if (g.RemoteAt < -50f) g.Seq = seq;     // the first word from the layer: nothing to replay
            g.RemoteSeq = seq;
            g.RemoteFuze = fuze;
            g.RemoteAt = Time.time;
        }

        /// <summary>The layer's pose ten times a second, and at once after a
        /// shot (<paramref name="now"/>).</summary>
        internal static void Publish(Gun g, bool player, bool now)
        {
            if (!now && Time.time < g.NextPublish) return;
            g.NextPublish = Time.time + 0.1f;
            FlakNet.SendPose(g.Index, g.WantYaw, g.WantPitch, g.Seq, g.FuzeRange, player);
        }

        /// <summary>The layer fires: one shell, the shot counter on, the wire
        /// told at once.</summary>
        internal static void Fire(Gun g, Vector3 aim, bool aimValid, float fuze, List<GepardGun.Contact> contacts,
                                  bool player)
        {
            if (g.Seq < 0) g.Seq = 0;
            g.Seq++;
            g.FuzeRange = fuze;
            Shoot(g, aim, aimValid, fuze, true, contacts);
            g.Rounds--;
            g.Fired++;
            Publish(g, player, true);
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

        /// <summary>A crewman (or the radar operator) at his post: alive AND
        /// not lying in the game's wounded state - a wounded man has IsAlive()
        /// true but is out of the fight for good (N6 radar fix).</summary>
        internal static bool Up(Component ai)
        {
            return ai != null && GepardCrew.Steht(ai) && !NpcWar.GroundDowned(ai);
        }

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
    /// The live 52-K: on an emplacement's platform (the derelict ZU swapped
    /// out of the ring's mesh) or on the ground. The meshes and pivots of
    /// k52_build.py, already in world units (x 2.8): base, mount, cradle and
    /// barrel, four LODs each under one LODGroup.
    /// </summary>
    internal static class FlakModel
    {
        const float K = Flak.K;
        const int Lods = 4;
        static readonly string[] PartNames = { "base", "mount", "cradle", "barrel" };
        /// <summary>Screen heights the LODs hand over at, the last one culls
        /// (the gun is ~6 m: LOD0 inside ~60 m, culled beyond ~2 km).</summary>
        static readonly float[] LodHeights = { 0.35f, 0.14f, 0.05f, 0.008f };

        static bool _loaded, _ok;
        static Mesh[,] _mesh;
        static Material _skin, _olive, _steel;
        static readonly Dictionary<string, Vector3> _rig = new Dictionary<string, Vector3>();

        /// <summary>B2: a collider box of k52_rig.txt ("box &lt;part&gt; c s"),
        /// game units, in the frame of the part it rides on.</summary>
        struct Box3 { public int Part; public Vector3 Centre, Size; }
        static readonly List<Box3> _boxes = new List<Box3>();

        internal static Flak.Gun Build(int index, string id, string name, Transform holder, Transform empty,
                                       Vector2 spot, float yaw, Scene scene)
        {
            try
            {
                Flak.Gun g = new Flak.Gun();
                g.Index = index;
                g.Id = id;
                g.Name = name;
                g.Holder = holder;

                GameObject root = new GameObject("NDR Flak 52-K " + id);
                if (holder != null)
                {
                    root.transform.SetParent(holder, false);
                    root.transform.localPosition = Vector3.zero;
                    root.transform.localRotation = Quaternion.identity;
                    string swapped = Swap(holder, empty);
                    int unblocked = Unblock(holder);
                    Flak.Log(id + ": built on \"" + name + "\" at " + holder.position + ", derelict " + swapped
                        + ", " + unblocked + " derelict collider(s) off.");
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
                    Flak.Log(id + ": built on the ground at " + at + " (" + name + ").");
                }
                g.Root = root.transform;
                g.Owner = holder != null ? holder : root.transform;
                if (Load()) Parts(g);
                else Fallback(g);
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
                if (IsOurs(r.transform, holder)) continue;
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

        /// <summary>B2: the ring still carried the derelict ZU's box collider
        /// (1.6 x 1.4 x 1.6 m at the platform centre, airfield_shelters.py
        /// aa_colliders "gun") after its mesh was swapped out: an invisible
        /// block through the 52-K's pedestal. Its NavMeshObstacle stays - the
        /// live gun stands on that spot.</summary>
        static int Unblock(Transform holder)
        {
            int n = 0;
            Vector3 o = holder.position;
            BoxCollider[] bs = holder.GetComponentsInChildren<BoxCollider>(true);
            for (int i = 0; i < bs.Length; i++)
            {
                BoxCollider b = bs[i];
                if (b == null || !b.enabled || IsOurs(b.transform, holder)) continue;
                Vector3 c = b.transform.TransformPoint(b.center) - o;
                float h = b.size.y * Mathf.Abs(b.transform.lossyScale.y);
                if (new Vector2(c.x, c.z).magnitude > 0.6f * K) continue;
                if (c.y < 0.4f * K || c.y > 1.2f * K || h < 1.0f * K || h > 1.8f * K) continue;
                b.enabled = false;
                n++;
            }
            return n;
        }

        /// <summary>A renderer of our own gun under the holder (its part
        /// names end in _LOD&lt;n&gt; too and must not be swapped).</summary>
        static bool IsOurs(Transform t, Transform holder)
        {
            for (Transform p = t; p != null && p != holder; p = p.parent)
                if (p.name.StartsWith("NDR Flak", StringComparison.Ordinal)) return true;
            return false;
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

        /// <summary>The sixteen meshes, the atlas and the rig, once.</summary>
        static bool Load()
        {
            if (_loaded) return _ok;
            _loaded = true;
            try
            {
                ReadRig(System.IO.Path.Combine(RevivalPlugin.AssetDir, "k52_rig.txt"));
                _mesh = new Mesh[PartNames.Length, Lods];
                for (int p = 0; p < PartNames.Length; p++)
                    for (int l = 0; l < Lods; l++)
                    {
                        _mesh[p, l] = Assets.Load("k52_" + PartNames[p] + "_lod" + l + ".ndmesh");
                        if (_mesh[p, l] == null && l == 0)
                            throw new InvalidOperationException("k52_" + PartNames[p] + "_lod0.ndmesh missing");
                    }
                Texture2D tex = Assets.Texture("k52_diffuse.png", false, true);
                Texture2D nrm = Assets.Texture("k52_normal.png", true, true);
                if (tex == null) throw new InvalidOperationException("k52_diffuse.png missing");
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _skin = new Material(shader);
                _skin.name = "NDR_Flak_52K";
                tex.anisoLevel = 4;
                tex.filterMode = FilterMode.Trilinear;
                _skin.mainTexture = tex;
                _skin.color = Color.white;
                if (nrm != null && _skin.HasProperty("_BumpMap"))
                {
                    _skin.SetTexture("_BumpMap", nrm);
                    _skin.EnableKeyword("_NORMALMAP");
                }
                if (_skin.HasProperty("_Glossiness")) _skin.SetFloat("_Glossiness", 0.25f);
                if (_skin.HasProperty("_Metallic")) _skin.SetFloat("_Metallic", 0.15f);
                _skin.hideFlags = HideFlags.HideAndDontSave;
                _ok = true;
                Flak.Log("52-K model loaded: " + _mesh[0, 0].vertexCount + " base vertices, rig of " + _rig.Count + " marks.");
            }
            catch (Exception ex)
            {
                _ok = false;
                RevivalPlugin.L.LogError("Flak: the 52-K model did not load (" + ex.Message
                    + ") - the guns are drawn as plain boxes; repair the client package (python k52_build.py).");
            }
            return _ok;
        }

        static void ReadRig(string path)
        {
            _rig.Clear();
            _boxes.Clear();
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException("k52_rig.txt missing");
            string[] lines = System.IO.File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "box")
                {
                    // box <part> cx cy cz sx sy sz
                    int part = p.Length == 8 ? Array.IndexOf(PartNames, p[1]) : -1;
                    float[] v = new float[6];
                    bool ok = part >= 0;
                    for (int k = 0; ok && k < 6; k++)
                        ok = float.TryParse(p[2 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
                    if (!ok) continue;
                    Box3 b;
                    b.Part = part;
                    b.Centre = new Vector3(v[0], v[1], v[2]);
                    b.Size = new Vector3(v[3], v[4], v[5]);
                    _boxes.Add(b);
                    continue;
                }
                if (p.Length < 4) continue;
                float x, y, z;
                if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    _rig[p[0]] = new Vector3(x, y, z);
            }
        }

        /// <summary>A rig mark in world units; the metres of k52_build.py x K
        /// if the file lacks it.</summary>
        static Vector3 Rig(string key, Vector3 metres)
        {
            Vector3 v;
            return _rig.TryGetValue(key, out v) ? v : metres * K;
        }

        static Transform Node(Transform parent, string name, Vector3 local)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        /// <summary>
        /// The gun at its real pivots (k52_rig.txt): the carriage stands, the
        /// mount traverses on the pedestal, the cradle elevates on the
        /// trunnions, the barrel slides back along the cradle's +z.
        /// </summary>
        static void Parts(Flak.Gun g)
        {
            Transform root = g.Root;
            Transform mount = Node(root, "mount", Rig("mount", new Vector3(0f, 0.92f, 0f)));
            Transform cradle = Node(mount, "cradle", Rig("cradle", new Vector3(0f, 0.78f, 0.18f)));
            Transform barrel = Node(cradle, "barrel", Rig("barrel", Vector3.zero));
            g.Mount = mount;
            g.Cradle = cradle;
            g.Barrel = barrel;
            g.Muzzle = Node(barrel, "muzzle", Rig("muzzle", new Vector3(0f, 0f, 4.55f)));
            g.SeatGunner = Node(mount, "seat gunner", Rig("seat_gunner", new Vector3(-0.78f, 0.47f, -0.36f)));
            g.SeatLoader = Node(mount, "seat loader", Rig("seat_loader", new Vector3(0.78f, 0.47f, -0.36f)));
            g.Eye = Node(cradle, "eye", Rig("eye", new Vector3(-0.46f, 0.38f, -0.25f)));
            g.Shoulder = Node(mount, "shoulder", Rig("shoulder", ShoulderM));
            g.Sight = Node(mount, "sight", Rig("sight", SightM));
            g.Stroke = Rig("recoil", new Vector3(0f, 0f, 0.65f)).z;

            Transform[] parents = { root, mount, cradle, barrel };
            Colliders(g, parents);
            List<Renderer>[] byLod = new List<Renderer>[Lods];
            for (int l = 0; l < Lods; l++) byLod[l] = new List<Renderer>();
            for (int p = 0; p < PartNames.Length; p++)
                for (int l = 0; l < Lods; l++)
                {
                    Mesh mesh = _mesh[p, l];
                    if (mesh == null) continue;
                    GameObject go = new GameObject("k52_" + PartNames[p] + "_LOD" + l);
                    go.transform.SetParent(parents[p], false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = _skin;
                    r.shadowCastingMode = l >= 2 ? UnityEngine.Rendering.ShadowCastingMode.Off
                        : UnityEngine.Rendering.ShadowCastingMode.On;
                    byLod[l].Add(r);
                }
            LODGroup group = root.gameObject.AddComponent<LODGroup>();
            LOD[] lods = new LOD[Lods];
            for (int l = 0; l < Lods; l++) lods[l] = new LOD(LodHeights[l], byLod[l].ToArray());
            group.SetLODs(lods);
            group.RecalculateBounds();
        }

        /// <summary>k52_build.py SHOULDER / SIGHT, metres in the mount frame,
        /// if the rig file lacks them.</summary>
        static readonly Vector3 ShoulderM = new Vector3(-1.10f, 2.25f, -2.70f);
        static readonly Vector3 SightM = new Vector3(-0.62f, 1.86f, 0.30f);

        /// <summary>
        /// B2: the rig's boxes as BoxColliders - the carriage static on the
        /// root, the mount and cradle one kinematic compound (a moving collider
        /// without a body is re-inserted into the static tree every frame it
        /// turns). None on the seats, none that recoils. The gun's rounds step
        /// past everything under g.Owner, and so does FlakFire.Sight.
        /// </summary>
        static void Colliders(Flak.Gun g, Transform[] parents)
        {
            if (_boxes.Count == 0) return;
            int layer = g.Holder != null ? g.Holder.gameObject.layer : g.Root.gameObject.layer;
            bool moving = false;
            for (int i = 0; i < _boxes.Count; i++)
            {
                Box3 b = _boxes[i];
                Transform parent = parents[b.Part];
                if (parent == null) continue;
                GameObject go = new GameObject("k52_collider_" + PartNames[b.Part] + "_" + i);
                go.layer = layer;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = b.Centre;
                go.transform.localRotation = Quaternion.identity;
                go.AddComponent<BoxCollider>().size = b.Size;
                if (b.Part > 0) moving = true;
            }
            if (moving && g.Mount != null)
            {
                Rigidbody rb = g.Mount.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
        }

        /// <summary>Without the model files: the same pivots with plain boxes,
        /// so the guns still fight and can be seen.</summary>
        static void Fallback(Flak.Gun g)
        {
            if (_olive == null)
            {
                _olive = Mat("NDR_Flak_Olive", new Color(0.29f, 0.32f, 0.21f));
                _steel = Mat("NDR_Flak_Steel", new Color(0.13f, 0.13f, 0.13f));
            }
            Transform root = g.Root;
            Box(root, new Vector3(0f, 0.3f, 0f), new Vector3(0.35f, 0.3f, 5.8f), _olive);
            Box(root, new Vector3(0f, 0.3f, 0f), new Vector3(5.4f, 0.3f, 0.35f), _olive);
            Transform mount = Node(root, "mount", new Vector3(0f, 0.92f, 0f) * K);
            Box(mount, new Vector3(0f, 0.35f, 0f), new Vector3(1.4f, 0.7f, 1.4f), _olive);
            Box(mount, new Vector3(0f, 1.0f, 1.0f), new Vector3(1.9f, 1.25f, 0.05f), _olive);   // shield
            Transform cradle = Node(mount, "cradle", new Vector3(0f, 0.78f, 0.18f) * K);
            Transform barrel = Node(cradle, "barrel", Vector3.zero);
            Box(barrel, new Vector3(0f, 0f, 2.1f), new Vector3(0.14f, 0.14f, 5.1f), _steel);
            g.Mount = mount;
            g.Cradle = cradle;
            g.Barrel = barrel;
            g.Muzzle = Node(barrel, "muzzle", new Vector3(0f, 0f, 4.55f) * K);
            g.SeatGunner = Node(mount, "seat gunner", new Vector3(-0.78f, 0.47f, -0.36f) * K);
            g.SeatLoader = Node(mount, "seat loader", new Vector3(0.78f, 0.47f, -0.36f) * K);
            g.Eye = Node(cradle, "eye", new Vector3(-0.46f, 0.38f, -0.25f) * K);
            g.Shoulder = Node(mount, "shoulder", ShoulderM * K);
            g.Sight = Node(mount, "sight", SightM * K);
            g.Stroke = 0.65f * K;
        }

        static Material Mat(string name, Color c)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            Material m = new Material(shader);
            m.name = name;
            m.color = c;
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        /// <summary>A box from its centre and size in metres.</summary>
        static void Box(Transform parent, Vector3 centre, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "NDR Flak part";
            Collider c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre * K;
            go.transform.localScale = size * K;
            Renderer r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = m;
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
                string side = Flak.Side(g);
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
                g.Rounds = g.Rounds < 0 ? Flak.RoundsPerLoad : g.Rounds;
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
                // Three quiet seconds: the crew watches the sky (B2).
                if (Time.time - g.LastContact > 3f) Watch(g);
                else g.ScanFrom = -1f;
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
                // The first shot is laid on a rough range and lead: far off.
                g.Err = Offset(t, mid, dist * Mathf.Max(0f, Flak.CfgInitialError == null ? 60f : Flak.CfgInitialError.Value)
                    * Flak.DirError * 0.001f);
                g.LastVel = t.Vel;
                Flak.Log(g.Id + ": engages " + Describe(t) + " at "
                    + dist.ToString("0", CultureInfo.InvariantCulture) + " u (" + g.Mode + ", "
                    + (Flak.RadarDirected ? "radar-directed" : "by eye") + ").");
                Flak.RaiseEngaging(g, t.Go);
            }

            float tof;
            Vector3 aim = Flak.Intercept(mid, t.Pos, t.Vel, out tof) + g.Err;
            float wantYaw, wantPitch;
            Flak.Angles(g, aim - mid, out wantYaw, out wantPitch);
            float min = Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value;
            float max = Flak.CfgPitchMax == null ? 82f : Flak.CfgPitchMax.Value;
            bool reach = wantPitch >= min - 0.5f && wantPitch <= max + 0.5f;
            g.WantYaw = wantYaw;
            g.WantPitch = Mathf.Clamp(wantPitch, min, max);
            g.Aim = aim;
            g.FuzeRange = Vector3.Distance(mid, aim);

            Vector3 bore = g.Cradle != null ? g.Cradle.forward : g.Root.forward;
            float error = Vector3.Angle(bore, aim - mid);
            g.Held += dt;
            float reaction = Mathf.Max(0f, Flak.CfgReaction == null ? 3f : Flak.CfgReaction.Value) * Flak.DirReaction;
            bool ready = reach && g.Held >= reaction && error < 1.5f && g.Mode != FlakMode.HoldFire
                && !g.Reloading;
            Trigger(g, ready, aim, t, mid, tof, gunner && loader);
            Flak.Publish(g, false, false);
        }

        /// <summary>
        /// B2: a crew with nothing to shoot at does not park the gun - the
        /// layers crank it slowly round the sector ahead of the emplacement
        /// (+-60 deg, a sweep every ~80 s, 20-40 deg up), each gun on its own
        /// phase. Arithmetic only; the pose reaches the other clients on the
        /// 10 Hz publish the idle gun sends anyway.
        /// </summary>
        static void Watch(Flak.Gun g)
        {
            if (g.ScanFrom < 0f) g.ScanFrom = Time.time;
            float t = (Time.time - g.ScanFrom) * 0.0785f + g.Index * 2.1f;
            g.WantYaw = Mathf.Sin(t) * 60f;
            g.WantPitch = 30f + Mathf.Sin(t * 0.7f + 1f) * 10f;
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

        /// <summary>BRACKETING, one shell a step (RateOfFire). After every
        /// shot the crew corrects by the puff it saw - the error shrinks by the
        /// walk factor (fast radar-directed, slow by eye) down to ErrorFloor -
        /// and a target that changed its velocity since the last shot throws
        /// the correction off again. The puffs walk toward the aircraft
        /// seconds apart: the pilot sees them come.</summary>
        static void Trigger(Flak.Gun g, bool ready, Vector3 aim, GepardGun.Contact t, Vector3 mid, float tof, bool both)
        {
            float now = Time.time;
            g.Firing = ready && !g.Reloading;
            if (!g.Firing || now < g.NextShot) return;
            if (g.Rounds <= 0)
            {
                g.Firing = false;
                Flak.StartReload(g, Flak.ReloadSeconds() * (both ? 1f : 1.5f));
                return;
            }
            // How much did the target change course since the last shot?
            float evade = Mathf.Max(0f, Flak.CfgEvade == null ? 1f : Flak.CfgEvade.Value);
            float dv = (t.Vel - g.LastVel).magnitude;
            g.LastVel = t.Vel;
            if (g.Fired > 0 && dv > 0.5f) g.Err += UnityEngine.Random.onUnitSphere * dv * tof * evade;
            Flak.Fire(g, aim, true, g.FuzeRange, g.Hostile, false);
            // One man alone loads and lays: half the rate.
            g.NextShot = now + Flak.Interval() * (both ? 1f : 2f) * UnityEngine.Random.Range(0.9f, 1.15f);
            // The correction for the next shot, from where this one bursts.
            float dist = Vector3.Distance(mid, t.Pos);
            float floor = Mathf.Max(0f, Flak.CfgFloor == null ? 5f : Flak.CfgFloor.Value) * 0.001f * dist;
            g.Err = g.Err * Flak.Walk + Scatter(t, mid, floor);
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

            string side = Flak.Side(g);
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

        static float Flak_Ceiling() { return Flak.CeilingU; }

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
            float lag = Mathf.Max(0.2f, Flak.CfgLag == null ? 1.5f : Flak.CfgLag.Value) * Flak.DirTracking;
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
            // An NPC intruder (the admin's test flyover, Revival.NpcAircraft.cs).
            if (NpcAircraft.Hostile(c.Go)) { hostile = true; return; }
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

        internal static bool Crewman(Flak.Gun g, Transform t)
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
        static float _nextLook, _nextShot, _fuzeM;
        static bool _autoLocked;
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
        static float _nextUpCheck;

        internal static void Tick(List<Flak.Gun> guns)
        {
            GameObject me = MapTools.LocalPlayer();
            if (Flak._manned != null)
            {
                if (me == null) { Leave("no local player"); return; }
                if ((me.transform.position - _stoodAt).sqrMagnitude > 25f * 25f) { Leave("moved away"); return; }
                if (Input.GetKeyDown(Key()) || Input.GetKeyDown(KeyCode.Escape)) { Leave("left the sight"); return; }
                // B1: a dead gunner lets go of the gun (his poses would keep
                // it claimed for every other client). Twice a second.
                if (Time.time >= _nextUpCheck)
                {
                    _nextUpCheck = Time.time + 0.5f;
                    if (!Crocodile.PlayerUp(me)) { Leave("the local player is down"); return; }
                }
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
            if (!CameraOwner.Request(CameraOwner.Flak, true, "52-K")) { Hint("The camera is busy.", 2f); return; }
            Flak._manned = g;
            _stoodAt = me.transform.position;
            _cmdYaw = g.Yaw;
            _cmdPitch = g.Pitch;
            _zoom = false;
            _fuzeM = 0f;
            _air.Clear();
            if (g.Rounds < 0) g.Rounds = Flak.RoundsPerLoad;
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
            CameraOwner.Release(CameraOwner.Flak);
            Flak.Log("the local player left " + g.Id + " (" + why + ").");
        }

        /// <summary>The manning player's laying and trigger, every frame.</summary>
        internal static void Lay(Flak.Gun g, float dt)
        {
            float sens = Mathf.Max(0.1f, Flak.CfgSensitivity == null ? 2f : Flak.CfgSensitivity.Value) * (_zoom ? 0.35f : 1f);
            _cmdYaw += GameUi.Axis("Mouse X") * sens;
            _cmdPitch += GameUi.Axis("Mouse Y") * sens;
            float min = Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value;
            float max = Flak.CfgPitchMax == null ? 82f : Flak.CfgPitchMax.Value;
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

            if (GameUi.KeyDown(KeyCode.R) && !g.Reloading && g.Rounds < Flak.RoundsPerLoad)
                Flak.StartReload(g, Flak.ReloadSeconds() * 1.5f);

            // The fuze setter. Automatic: the range of the aircraft nearest
            // the bore (within 4 degrees), else the longest setting. The
            // mouse wheel sets it by hand in 100 m steps; F goes back to auto.
            float wheel = GameUi.Axis("Mouse ScrollWheel");
            if (wheel > 0.01f || wheel < -0.01f)
            {
                if (_fuzeM <= 0f) _fuzeM = Mathf.Round(Mathf.Min(AutoFuze(g), Flak.MaxFuze) / Flak.K / 100f) * 100f;
                _fuzeM = Mathf.Clamp(_fuzeM + (wheel > 0f ? 100f : -100f), 200f, Flak.MaxFuze / Flak.K);
            }
            if (GameUi.KeyDown(KeyCode.F)) _fuzeM = 0f;
            g.FuzeRange = _fuzeM > 0f ? _fuzeM * Flak.K : AutoFuze(g);

            bool held = GameUi.Button(0) && !g.Reloading;
            if (held && g.Rounds <= 0) { Flak.StartReload(g, Flak.ReloadSeconds() * 1.5f); held = false; }
            g.Firing = held;
            if (held && Time.time >= _nextShot)
            {
                // The player is gunner and loader in one: half the crew's rate.
                Flak.Fire(g, Vector3.zero, false, g.FuzeRange, _air, true);
                _nextShot = Time.time + Flak.Interval() * 2f;
            }
            else Flak.Publish(g, true, false);
        }

        /// <summary>The automatic fuze: slant range to the aircraft nearest
        /// the bore within 4 degrees, else the longest setting.</summary>
        static float AutoFuze(Flak.Gun g)
        {
            Vector3 mid = Flak.Mid(g);
            Vector3 bore = g.Cradle != null ? g.Cradle.forward : g.Root.forward;
            float best = 4f, range = Flak.MaxFuze;
            _autoLocked = false;
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null) continue;
                float tof;
                Vector3 aim = Flak.Intercept(mid, c.Pos, c.Vel, out tof);
                float a = Vector3.Angle(bore, aim - mid);
                if (a >= best) continue;
                best = a;
                range = Vector3.Distance(mid, aim);
                _autoLocked = true;
            }
            return range;
        }

        /// <summary>
        /// The gunner camera, after the game has written its own (B2; the old
        /// eye on the cradle looked into the back of the shield). Default:
        /// over the shoulder, behind and above the breech on the traversing
        /// mount - the whole gun in view as it cranks, recoils and fires.
        /// RMB: the sight, left of the barrel above the shield top, zoomed.
        /// Both look along the COMMANDED lay to the fuze range, like the
        /// Gepard's sight: the centre cross is where the gun is told to point
        /// and the burst range, the small ring is where the bore is (it
        /// catches up at the mount's slew rate). One ray a frame, and only
        /// while a player mans the gun, keeps the shoulder camera out of
        /// walls, trees and sandbags.
        /// </summary>
        internal static void LateTick()
        {
            Flak.Gun g = Flak._manned;
            if (g == null || g.Mount == null) return;
            try
            {
                Camera cam = CameraOwner.ViewCamera();
                if (cam == null) return;
                Vector3 mid = Flak.Mid(g);
                Vector3 los = Flak.Direction(g, _cmdYaw, _cmdPitch);
                Vector3 aim = mid + los * ViewRange(g);
                Vector3 eye = _zoom && g.Sight != null ? g.Sight.position
                    : Clear(g, g.Shoulder != null ? g.Shoulder.position : g.Mount.TransformPoint(new Vector3(-1.1f, 2.25f, -2.7f) * Flak.K));
                Vector3 look = aim - eye;
                if (look.sqrMagnitude < 1f) look = los;
                Quaternion want = Quaternion.LookRotation(look, g.Mount.up);
                // The shot shakes the view: a short jolt up, gone as the barrel runs out.
                float r = g.Recoil;
                if (r > 0f) want = want * Quaternion.Euler(-(_zoom ? 0.6f : 1.4f) * r * r * r, 0f, 0f);
                cam.transform.position = eye;
                cam.transform.rotation = want;
                cam.fieldOfView = _zoom ? 18f : 60f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Flak camera: " + ex);
                Leave("camera error");
            }
        }

        /// <summary>How far out the view converges: the fuze range (the
        /// burst is at the centre cross), never nearer than 300 m.</summary>
        static float ViewRange(Flak.Gun g)
        {
            return Mathf.Clamp(g.FuzeRange, 300f * Flak.K, Mathf.Max(300f * Flak.K, Flak.MaxFuze));
        }

        static readonly RaycastHit[] _hits = new RaycastHit[12];

        /// <summary>The shoulder camera, pulled in front of the first thing
        /// between the mount's axis and it - not this gun, its crew or the
        /// local player.</summary>
        static Vector3 Clear(Flak.Gun g, Vector3 want)
        {
            Vector3 up = g.Mount.up;
            Vector3 from = g.Mount.position + up * Vector3.Dot(want - g.Mount.position, up);
            Vector3 d = want - from;
            float dist = d.magnitude;
            if (dist < 0.01f) return want;
            d /= dist;
            float margin = 0.3f * Flak.K;
            int n = Physics.RaycastNonAlloc(from, d, _hits, dist + margin, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            GameObject me = MapTools.LocalPlayer();
            float best = dist + margin;
            for (int i = 0; i < n; i++)
            {
                Transform t = _hits[i].transform;
                if (t == null || _hits[i].distance >= best) continue;
                if (t.IsChildOf(g.Root) || FlakFire.Crewman(g, t)) continue;
                if (me != null && t.IsChildOf(me.transform)) continue;
                best = _hits[i].distance;
            }
            if (best >= dist + margin) return want;
            return from + d * Mathf.Max(0.2f * Flak.K, best - margin);
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
                    // B2: where the bore points at the same range (the camera
                    // looks along the commanded lay; the gun catches up).
                    Camera cam = CameraOwner.ViewCamera();
                    if (cam != null && g.Cradle != null)
                    {
                        Vector3 p = cam.WorldToScreenPoint(Flak.Mid(g) + g.Cradle.forward * ViewRange(g));
                        if (p.z > 0f)
                        {
                            GUI.color = Mathf.Abs(Mathf.DeltaAngle(g.Yaw, _cmdYaw)) < 1f
                                && Mathf.Abs(g.Pitch - _cmdPitch) < 1f ? Color.green : Amber;
                            Ring(p.x, h - p.y, 7f);
                            GUI.color = Color.white;
                        }
                    }
                    string fuze = "fuze " + (g.FuzeRange / Flak.K).ToString("0", CultureInfo.InvariantCulture) + " m "
                        + (_fuzeM > 0f ? "(set)" : _autoLocked ? "(auto: on target)" : "(auto: max)");
                    string line = "52-K 85 mm   " + (g.Reloading
                        ? "NEXT RACK " + Mathf.Max(0f, g.ReloadUntil - Time.time).ToString("0", CultureInfo.InvariantCulture) + " s"
                        : g.Rounds + " rds") + "   elev " + g.Pitch.ToString("0", CultureInfo.InvariantCulture)
                        + "   " + fuze + "   LMB fire  RMB sight  wheel fuze  F auto fuze  R rack  " + Key() + " leave";
                    Label(new Rect(0f, h - 70f, w, 26f), line, Amber);
                    string order = null;
                    try { if (Flak.SightOrder != null) order = Flak.SightOrder(g.Id); }
                    catch { }
                    if (!string.IsNullOrEmpty(order)) Label(new Rect(0f, h - 100f, w, 26f), order, Color.white);
                }
                else if (_near != null)
                {
                    string s = _nearWhy ?? ("[" + Key() + "] Man the 52-K 85 mm gun");
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
            // An 85 mm gun: heard across the whole airfield.
            Play(_report, at, 90f, 12000f, 1f);
        }

        internal static void Burst(Vector3 at)
        {
            // Four guns can burst in the same instant: one bang per 0.09 s.
            if (Time.time - _lastBurst < 0.09f) return;
            _lastBurst = Time.time;
            Camera cam = CameraOwner.MainCamera();
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

        /// <summary>The report of the 85 mm gun: a hard crack, a deep blast
        /// and a rolling tail. The burst: a crack and a long rolling
        /// rumble.</summary>
        static AudioClip Make(bool burst)
        {
            const int rate = 44100;
            float seconds = burst ? 2.2f : 1.6f;
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
                    float crack = noise * 1.4f * Mathf.Exp(-t * 70f);
                    float body = low * 3.2f * Mathf.Exp(-t * 9f);
                    float rumble = low2 * 6f * Mathf.Exp(-t * 2.4f);
                    float thump = Mathf.Sin(2f * Mathf.PI * (38f + 30f * Mathf.Exp(-t * 12f)) * t) * 0.9f * Mathf.Exp(-t * 7f);
                    v = crack + body + rumble + thump;
                }
                data[i] = Mathf.Clamp(v * 0.6f, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create(burst ? "NDR flak burst" : "NDR 52-K report", data.Length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    // =====================================================================
    // Network

    /// <summary>
    /// One Photon event (Flak NetworkEventCode, 198): { 1, gun, yaw, pitch,
    /// shot counter, fuze range, player } ten times a second and at once on
    /// every shot, unreliable, from whoever lays the gun - a lost message is
    /// caught up by the next one's counter. The same reflection path
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

        internal static void SendPose(int gun, float yaw, float pitch, int seq, float fuze, bool player)
        {
            if (!_hooked) return;
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                float[] data = new float[] { Pose, gun, yaw, pitch, seq, fuze, player ? 1f : 0f };
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
                    Mathf.Clamp(f[3], -15f, 90f), Mathf.Max(0, Mathf.RoundToInt(f[4])),
                    Mathf.Clamp(f[5], 30f, 20000f), f[6] > 0.5f, sender);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Flak network receive: " + ex.Message);
            }
        }
    }
}

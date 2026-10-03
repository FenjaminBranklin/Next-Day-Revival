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
//      with a hostile pilot, a drone flown by one, or an NPC raider - airborne
//      and within range. Town guns engage only over MT. BRACKETING: the
//      first shot is far off (InitialError); after every shot the crew
//      corrects by the puff it saw (the error shrinks by the walk factor)
//      down to ErrorFloor, so the puffs visibly walk toward the aircraft,
//      seconds apart. A pilot who changes course between two shots throws
//      the correction off again (EvadeFactor). With nothing to shoot at the
//      crew sweeps the sky ahead (B2, FlakFire.Watch) - the gun never parks.
//   5  THE RADAR (Revival.TowerRadar.cs). With an operator at the tower's
//      console its own-side airfield guns are radar-directed (DirectOwned):
//      RadarRange and RadarWalkFactor - long range, fast bracketing.
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
        /// <summary>W AA7: wrecked; tools and time restore this gun.</summary>
        Destroyed = 6,
    }

    /// <summary>A snapshot of one gun for the P5 radar and the P6a zones.</summary>
    public sealed class FlakGunInfo
    {
        public string Id;                 // "AA-N", "AA-S"
        public string Name;               // the emplacement's name in the assembly
        public Vector3 Position;          // the mount's pivot, world units
        public float Yaw, Pitch;          // mount bearing (world, degrees from +z) and elevation
        public FlakState State;
        public float Health;              // W AA7: 0 destroyed, 1 intact
        public FlakMode Mode;
        public GameObject Target;         // what it lays on now; null = none
        public GameObject Assigned;       // the target an API caller assigned; null = none
        public int Rounds;                // total stock; -1 garrison, -2 unknown
        public int CrewAlive;             // 0..2
        public int MercActor = -1, CrewSide = -1;
        public bool PlayerManned, ShortRange;
        public int ManActor = -1;         // the player at the sight (actor number), -1 none
        public int OwnerSide = -1;        // persistent faction, independent of crew presence
        public float Range;               // engagement range, world units
        public float AirfieldReach;       // A L2: reach on an aircraft in the airfield zone (0: not an airfield 52-K)
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
            CfgReload, CfgRespawn, CfgSeatDrop, CfgReach, CfgSensitivity, CfgMinHeight,
            CfgManualRpm, CfgManualReload, CfgCrewRpm, CfgProximity, CfgImpactDamage, CfgImpactRadius,
            CfgAirfieldZone;
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
            CfgRadarRange = cfg.Bind(G, "RadarRange", 7000f,
                "Slant range in metres with the tower radar manned (radar-directed).");
            CfgSelfDestruct = cfg.Bind(G, "MaxFuzeRange", 8000f,
                "The longest fuze setting: a shell that meets nothing bursts after this many metres at the latest.");
            CfgAirfieldZone = cfg.Bind(G, "AirfieldZone", FlakEngageCore.ZoneMetres,
                "Radius in metres of the east airfield's engagement zone around the C1 tower: "
                + "a manned airfield 52-K opens fire on any hostile aircraft inside it, by eye "
                + "too (the radar adds range beyond it and accuracy). Smallest 1800.");
            CfgCeiling = cfg.Bind(G, "Ceiling", 3000f,
                "Highest target altitude above the gun in metres engaged.");
            CfgMinHeight = cfg.Bind(G, "MinTargetHeight", 3f,
                "An aircraft lower than this over the ground (metres) is not airborne and "
                + "not engaged - the guns are for air targets only.");
            CfgRpm = cfg.Bind(G, "RateOfFire", 15f,
                "Shots per minute of ONE man alone at the gun (he lays and loads himself). "
                + "Every shot is one bracketing step. Gunner and loader: FullCrewRateOfFire.");
            CfgCrewRpm = cfg.Bind(G, "FullCrewRateOfFire", 30f,
                "Shots per minute with gunner and loader at the gun, 1..60. A gameplay "
                + "tuning (one shot every 2 s), not the historical 15..20.");
            CfgDispersion = cfg.Bind(G, "Dispersion", 2.0f,
                "Dispersion of a single shell in milliradians (the Gepard: 1.5).");
            CfgFuze = cfg.Bind(G, "DirectHitDistance", 1.5f,
                "A shell passing this close to an aircraft (beyond its own size, world "
                + "units) hits it.");
            CfgSplash = cfg.Bind(G, "BurstRadius", 12f,
                "Metres from a burst within which its fragments hit an aircraft (beyond "
                + "the aircraft's size).");
            CfgProximity = cfg.Bind(G, "ProximityFuze", 10f,
                "Metres: an armed shell passing this close to an aircraft (beyond its size) "
                + "bursts at once, and its fragments reach it. Never wider than BurstRadius. "
                + "0 = no proximity fuze (direct hits and the time fuze only).");
            CfgImpactDamage = cfg.Bind(G, "ImpactDamage", 1200f,
                "Blast damage of an armed shell bursting on the ground, a building or a "
                + "vehicle (the game's own explosion; the LAW is 900).");
            CfgImpactRadius = cfg.Bind(G, "ImpactRadius", 7f,
                "Metres of full blast damage around such a burst (the game's explosion "
                + "falls off to zero at twice this).");
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
            ShortRange.BindConfig(cfg);
            CfgReload = cfg.Bind(G, "ReloadSeconds", 25f,
                "Bringing up the next rack, seconds (one man left alone: x 1.5).");
            CfgManualRpm = cfg.Bind(G, "ManualRateOfFire", 30f,
                "Player cadence, 10..40 rounds/minute (30: one shot every 2 s). The ready rack supplies the loader.");
            CfgManualReload = cfg.Bind(G, "ManualReloadSeconds", 6f,
                "Player ready-rack refill in seconds. R refills early; an empty rack refills automatically.");
            MigrateCadence(Settings.FileLayout(cfg));
        }

        /// <summary>
        /// Settings layout 4: the 52-K's cadence. A file from before it holds
        /// RateOfFire as the FULL crew's rate (one man alone fired at half of
        /// it) and ManualRateOfFire under a 15..20 clamp. The shipped defaults
        /// (15 and 18) take the new numbers - full crew 30, one man 15, player
        /// 30. Any other value was a choice and keeps its effective cadence:
        /// full crew X, one man X/2, the player's clamped to the old 15..20.
        /// </summary>
        internal static void MigrateCadence(int fileLayout)
        {
            if (fileLayout >= Settings.FlakCadenceLayout || CfgRpm == null || CfgCrewRpm == null || CfgManualRpm == null) return;
            float crew = CfgRpm.Value, manual = CfgManualRpm.Value;
            if (Mathf.Abs(crew - 15f) > 0.01f)
            {
                CfgCrewRpm.Value = crew;
                CfgRpm.Value = crew * 0.5f;
            }
            float fresh = (float)CfgManualRpm.DefaultValue;   // already the new number (retune.py): kept
            if (Mathf.Abs(manual - 18f) < 0.01f) CfgManualRpm.Value = fresh;
            else if ((manual < 15f || manual > 20f) && Mathf.Abs(manual - fresh) > 0.01f)
                CfgManualRpm.Value = Mathf.Clamp(manual, 15f, 20f);
            if (RevivalPlugin.L != null)
                Log("[Flak52K] cadence from layout " + fileLayout + ": full crew " + crew + " -> " + CfgCrewRpm.Value
                    + " rpm (FullCrewRateOfFire), one man " + (crew * 0.5f) + " -> " + CfgRpm.Value
                    + " rpm (RateOfFire), player " + manual + " -> " + CfgManualRpm.Value + " rpm (ManualRateOfFire).");
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
            return radar ? Mathf.Max(eye, F(CfgRadarRange, 7000f)) : eye;
        }
        static float SelfDestructU
        {
            get { return Mathf.Max(Mathf.Max(F(CfgRange, 1800f), Mathf.Max(MercAACore.RadarMetres, F(CfgRadarRange, 7000f))), F(CfgSelfDestruct, 8000f)) * K; }
        }
        internal static float CeilingU { get { return Mathf.Max(50f, F(CfgCeiling, 3000f)) * K; } }
        internal static float ZoneMetres { get { return Mathf.Max(1800f, F(CfgAirfieldZone, FlakEngageCore.ZoneMetres)); } }

        /// <summary>A L2: engagement range in world units for this gun and this
        /// aircraft - the crew's calibration, or the airfield zone's reach when
        /// a heavy airfield gun sees it inside the zone.</summary>
        internal static float ReachU(Gun g, Vector3 p, float calibrationMetres)
        {
            bool zone = !g.ShortRange && !g.Town && FlakEngageCore.InZone(p.x, p.z, ZoneMetres, K);
            return FlakEngageCore.Reach(calibrationMetres, zone, ZoneMetres, MaxFuze / K) * K;
        }
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
        static readonly string[] Ids = { "AA-N", "AA-S", "MT-AA2a", "MT-AA2b", "ZU-N", "AA-NE" };
        static readonly string[] Names = { "AA position north", "AA position S2-S3",
            "Town battery west", "Town battery east", "Airfield ZU west", "Airfield AA northeast" };
        static readonly Vector2[] Spots = { new Vector2(4045f, 1608f), new Vector2(4370f, 860f),
            new Vector2(5598f, 642f), new Vector2(5612f, 686f),
            new Vector2(4100f, 1000f), new Vector2(4375f, 1580f) };
        static readonly float[] SpotYaws = { 90f, 90f, 270f, 270f, 90f, 90f };
        static readonly bool[] TownSpot = { false, false, true, true, false, false };
        const string EmptyName = "AA position V3 (empty)";

        // ------------------------------------------------------------- the gun

        internal sealed class Gun
        {
            public FlakGunInfo Info;            // W Perf1: the reused snapshot (Flak.Guns / Get)
            public int Index;
            internal int NaturalSide = -2;
            public string Id, Name;
            public bool ShortRange, Awake;
            public float NextWake;
            public ShortBurst ShortBurst;
            public string CrewGKey, CrewCKey;
            public Transform BarrelRight, MuzzleRight;
            public Vector3 BarrelHome, BarrelRightHome;
            public bool Town;                   // the military town's battery (its faction)
            public bool RadarDirected;
            public int DirectionTier = 1;
            public int TownSide = -1;
            public Transform Holder;            // the emplacement; null on the ground
            public Transform Earthwork;         // Z L1b: static berm/pad, follows gun lifecycle
            public Transform Root, Mount, Cradle, Barrel, Muzzle;
            public Transform SeatGunner, SeatLoader, Eye;
            public Transform Shoulder, Sight;   // B2: the gunner's two camera marks, on the mount
            public Transform Owner;             // what the rounds step past
            public float Stroke;                // recoil stroke, world units (k52_rig.txt)

            // pose (every client)
            public float Yaw, Pitch, YawVel, PitchVel;
            internal float PosedYaw, PosedPitch, PosedRecoil;
            internal bool PoseValid, PosedDead;
            internal int PosedSeq;
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
            public GameObject RadarAssigned;     // master radar order provenance
            public Vector3 ZoneCentre;
            public float ZoneRadius, ZoneCeiling;
            public bool ZoneStrict;
            public readonly List<GepardGun.Contact> Air = new List<GepardGun.Contact>();
            public readonly List<GepardGun.Contact> Hostile = new List<GepardGun.Contact>();
            public GepardGun.Contact Target;
            public GameObject LostGo;           // A L2: the aircraft lost a moment ago
            public float LastSight = -100f;     // E L1: the last clear ray to Target
            public GepardGun.Contact Cue;       // E L1: radar cue beyond reach (laying only)
            public GameObject CueGo;
            public float CueHeld;               // E L1: seconds laid on CueGo
            public bool DrySaid;                // E L1: the empty-stock line of this engagement
            public bool Laying, Firing, Engaged, Reloading;
            public float Held, NextLook, NextShot, LastContact, LastShot = -100f, NextPublish, ReloadUntil;
            public float ScanFrom = -1f;        // B2: when the idle crew started watching the sky
            public int Rounds = -1, Fired, Seq;
            public Vector3 Err, LastVel, Aim;
            public float FuzeRange;

            // the wire (what the others see)
            public float RemoteAt = -100f, RemoteFuze, RemoteShotAt = -100f;
            public int RemoteSeq = -1;
            public bool ExactShots;
            public int ShotSender = -1, ShotSeq = -1;
            public float ClaimedUntil = -100f;  // a player at this gun on another client
            public float NextHold;              // W Perf1: next crew hold of a far gun (FlakCrew.Hold)
            public int ClaimActor;
            public readonly FlakAmmoStock Ammo = new FlakAmmoStock();
            public int AmmoSent = -1, AmmoSender = -1, AmmoTextCount = int.MinValue;
            public float AmmoHeartbeat, AmmoNextShot, AmmoNextSearch, AmmoNextReload;
            public string AmmoText, AmmoPrompt;
            public int AmmoLevel;               // B3: 0 fine, 1 low, 2 empty (the last warned level)
            public string AmmoLowText, AmmoDryText;
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

        // W Perf1: one snapshot object per gun, refilled on every call - NoFly
        // asked Get for each of its guns every frame, and each Get built a new
        // list with a new snapshot of every gun. Get needs no list at all now;
        // Guns still hands out its own list (a caller may loop over it while
        // it calls into code that asks again), holding the per-gun objects.
        // Every caller reads the values at once.

        /// <summary>Every gun built in this world, as a snapshot (the values
        /// are refreshed by the next Guns/Get call - read them, do not keep them).</summary>
        public static List<FlakGunInfo> Guns()
        {
            List<FlakGunInfo> list = new List<FlakGunInfo>(_guns.Count);
            FillGuns(list);
            return list;
        }

        // Caller-owned snapshot for periodic UI refreshes, without a new list.
        internal static void FillGuns(List<FlakGunInfo> list)
        {
            list.Clear();
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Root == null) continue;
                list.Add(Snapshot(g));
            }
        }

        static FlakGunInfo Snapshot(Gun g)
        {
            FlakGunInfo f = g.Info;
            if (f == null) { f = new FlakGunInfo(); g.Info = f; }
            f.Id = g.Id;
            f.Name = g.Name;
            f.Position = g.Mount != null ? g.Mount.position : g.Root.position;
            f.Yaw = g.Root.eulerAngles.y + g.Yaw;
            if (f.Yaw >= 360f) f.Yaw -= 360f;
            f.Pitch = g.Pitch;
            f.State = State(g);
            f.Health = AirDefenceDamage.Hp(g.Index);
            f.Mode = g.Mode;
            f.Target = g.Target != null ? g.Target.Go : null;
            f.Assigned = g.Assigned;
            f.Rounds = FlakAmmo.DisplayRounds(g);
            MercAAPost merc = MercAA.Gun(g.Index);
            f.CrewAlive = merc != null ? 1 : (Up(g.Gunner) ? 1 : 0) + (Up(g.Loader) ? 1 : 0);
            f.MercActor = merc == null ? -1 : merc.Actor;
            f.CrewSide = merc == null ? TowerRadar.SideId(Fraktion.Eigene(Side(g))) : merc.Side;
            f.ShortRange = g.ShortRange;
            f.OwnerSide = OwnerSide(g);
            f.PlayerManned = PlayerAt(g);
            f.ManActor = g == _manned ? Crocodile.LocalActor() : Time.time < g.ClaimedUntil ? g.ClaimActor : -1;
            f.Range = GunRange(g);
            f.AirfieldReach = g.ShortRange || g.Town ? 0f : ReachU(g, new Vector3(FlakEngageCore.CentreX, 0f, FlakEngageCore.CentreZ), 0f);
            f.ZoneCentre = g.ZoneCentre;
            f.ZoneRadius = g.ZoneRadius;
            f.ZoneCeiling = g.ZoneCeiling;
            f.ZoneStrict = g.ZoneStrict;
            return f;
        }

        /// <summary>One gun's snapshot by id; null if there is none.</summary>
        public static FlakGunInfo Get(string id)
        {
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Root == null) continue;
                if (string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) return Snapshot(g);
            }
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
                _guns[i].RadarAssigned = null;   // an independent API order replaces radar focus
                _guns[i].NextLook = 0f;
                any = true;
            }
            if (any) Log("gun " + (id ?? "all") + ": target " + (target == null ? "cleared" : "assigned: " + target.name) + ".");
            return any;
        }

        public static bool ClearTarget(string id) { return AssignTarget(id, null); }

        internal static void TrackRadarTarget(string id, GameObject target)
        {
            for (int i = 0; i < _guns.Count; i++)
                if (Match(_guns[i], id) && object.ReferenceEquals(_guns[i].Assigned, target))
                    _guns[i].RadarAssigned = target;
        }

        // Master radar focus is a priority, never an accuracy modifier. Refresh at
        // the radar's existing 2 Hz tick; range/ceiling/elevation still apply.
        internal static void FocusRadar(int side, GepardGun.Contact target)
        {
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Root == null) continue;
                FlakGunInfo info = Snapshot(g);
                bool eligible = TowerRadar.Follows(info, side) && info.Health > 0f
                    && info.CrewAlive > 0 && !info.PlayerManned;
                bool hostile, friendly;
                FlakFire.Allegiance(target, OwnerFaction(g), out hostile, out friendly);
                Vector3 d = target.Pos - Mid(g);
                MercAAPost man = MercAA.Gun(g.Index);
                float range = g.ShortRange ? ShortRangeCore.RangeM * K : ReachU(g, target.Pos, MercAACore.Calibrate(man != null,
                    MercAA.DirectionAvailable(g) && RadarShadow.Visible(target.Go), man == null ? 0 : man.Trait).Range);
                bool reach = RadarClarityCore.Reach(d.x, d.y, d.z, range,
                    g.ShortRange ? ShortRangeCore.CeilingM * K : CeilingU,
                    g.ShortRange ? -10f : F(CfgPitchMin, -3f), g.ShortRange ? 90f : F(CfgPitchMax, 82f));
                if (eligible && hostile && !friendly && reach)
                {
                    g.RadarAssigned = target.Go;
                    if (g.Assigned != target.Go || g.Mode != FlakMode.WeaponsFree)
                    { g.Assigned = target.Go; g.Mode = FlakMode.WeaponsFree; g.NextLook = 0f; }
                }
                else if (object.ReferenceEquals(g.RadarAssigned, target.Go))
                {
                    g.RadarAssigned = null;
                    if (object.ReferenceEquals(g.Assigned, target.Go))
                    { g.Assigned = null; g.NextLook = 0f; }
                }
            }
        }

        internal static void ClearRadarFocus(GameObject target)
        {
            if (object.ReferenceEquals(target, null)) return;
            for (int i = 0; i < _guns.Count; i++)
                if (object.ReferenceEquals(_guns[i].RadarAssigned, target))
                {
                    _guns[i].RadarAssigned = null;
                    if (!object.ReferenceEquals(_guns[i].Assigned, target)) continue;
                    _guns[i].Assigned = null;
                    if (_guns[i].Mode == FlakMode.AssignedOnly) _guns[i].Mode = FlakMode.WeaponsFree;
                    _guns[i].NextLook = 0f;
                }
        }

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
        /// (Authority); DirectOwned filters the affected guns.
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
            MercAAPost merc = g == null ? null : MercAA.Gun(g.Index);
            return merc == null ? (g != null && g.Town ? MilitaryTown.Faction() : AirfieldOwnership.CrewSide) : merc.SideName;
        }

        internal static int OwnerSide(Gun g)
        {
            return g.Town ? g.TownSide : AirfieldOwnership.Holder;
        }

        internal static string OwnerFaction(Gun g)
        {
            return g.Town ? Fraktion.Eigene(MilitaryTown.Faction()) : AirfieldOwnership.FactionName;
        }

        /// <summary>This gun's engagement range, world units (the ZU-23's own,
        /// else the W AA3 calibration: radar-directed or by eye).</summary>
        internal static float GunRange(Gun g)
        {
            return (g.ShortRange ? ShortRangeCore.RangeM : MercAA.Calibration(g).Range) * K;
        }

        // Only consulted by the master's existing 2 Hz target scan. No new
        // discovery, allocations or LOS probes; explicit assignments bypass it.
        internal static int TargetCover(Gun gun, GameObject target)
        {
            int count = 0;
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun other = _guns[i];
                if (other == gun || other.ShortRange || other.Town != gun.Town || !other.Laying
                    || other.Mode == FlakMode.HoldFire || other.Target == null) continue;
                if (other.Target.Go == target) count++;
            }
            return count;
        }

        // Called on the existing 2 Hz HQ tick. Town crews always work by eye.
        internal static void DirectOwned(int side, int tier)
        {
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                bool radar = AirDefencePolicy.Radar(g.Town, OwnerSide(g), side, tier);
                int direction = !g.Town && OwnerSide(g) == side ? tier : 1;
                if (g.RadarDirected == radar && g.DirectionTier == direction) continue;
                g.RadarDirected = radar;
                g.DirectionTier = direction;
                g.NextLook = 0f;
            }
        }

        internal static bool AirfieldClear()
        {
            int count = 0;
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Town || g.Root == null) continue;
                count++;
                if (Up(g.Gunner) || Up(g.Loader) || PlayerAt(g)) return false;
            }
            return count == (ShortRange.On ? 4 : 3); // Missing positions cannot grant capture.
        }

        internal static bool AirfieldManned()
        {
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (!g.Town && g.Root != null && (Up(g.Gunner) || Up(g.Loader) || PlayerAt(g))) return true;
            }
            return false;
        }

        internal static bool TownManned()
        {
            for (int i = 0; i < _guns.Count; i++)
            {
                Gun g = _guns[i];
                if (g.Town && g.Root != null && (Up(g.Gunner) || Up(g.Loader) || PlayerAt(g))) return true;
            }
            return false;
        }

        internal static void ClearAirfieldOrders()
        {
            for (int i = 0; i < _guns.Count; i++)
                if (!_guns[i].Town)
                {
                    Gun g = _guns[i];
                    g.Assigned = null;
                    g.Target = null;
                    g.Hostile.Clear();
                    g.Mode = FlakMode.WeaponsFree;
                    g.NextLook = 0f;
                }
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
        /// gun's own ceiling). <paramref name="strict"/> is retained for API
        /// compatibility; assignments and strict zones never bypass pilot IFF.
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
            if (!AirDefenceDamage.Alive(g.Index)) return FlakState.Destroyed;
            if (PlayerAt(g)) return FlakState.PlayerManned;
            if (MercAA.Gun(g.Index) == null && !Up(g.Gunner) && !Up(g.Loader)) return FlakState.NoCrew;
            bool firing = Time.time - Mathf.Max(g.LastShot, g.RemoteShotAt) < 1.5f;
            if (g.Reloading) return FlakState.Reloading;
            if (firing) return FlakState.Firing;
            if (g.Target != null && g.Target.Go != null) return FlakState.Tracking;
            return FlakState.Idle;
        }

        internal static bool PlayerAt(Gun g)
        {
            if (!AirDefenceDamage.Alive(g.Index)) return false;
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
            AirDefenceDamage.Install(harmony);
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            FlakPositions.Install(harmony);
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
            if (_manned != null || AirDefenceDamage.Repairing) __result = true;
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
                if (_guns.Count == 0) { FlakAmmo.Reset(); return; }
                FlakCrew.Resolve(_guns);
                bool master = Crocodile.IsMaster();
                FlakAmmo.Tick(_guns, master);
                float dt = Time.deltaTime;
                for (int i = 0; i < _guns.Count; i++)
                {
                    Gun g = _guns[i];
                    try
                    {
                        if (!AirDefenceDamage.Alive(g.Index))
                        {
                            if (g == _manned) FlakPlayer.Leave("the gun is destroyed");
                            FlakFire.Release(g); continue;
                        }
                        if (g.ShortRange && !ShortRange.Awake(g, master))
                        { FlakFire.Release(g); continue; }
                        if (!g.ShortRange && g.Earthwork != null && !FlakPositions.Active(g, master))
                        { FlakFire.Release(g); continue; }
                        if (master) FlakCrew.Spawn(g);
                        if (g == _manned) FlakPlayer.Lay(g, dt);
                        else if (master && (B(CfgNpcCrew) || MercAA.Gun(g.Index) != null) && Time.time >= g.ClaimedUntil)
                        {
                            if (g.ShortRange)
                            {
                                FrameProf.S(FrameProf.S_ShortRangeT);
                                ShortRange.Control(g, dt);
                                FrameProf.E(FrameProf.S_ShortRangeT);
                            }
                            else FlakFire.Control(g, dt);
                        }
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
                    if (g.Root == null || ((g.ShortRange || g.Earthwork != null) && !g.Awake && g != _manned)) continue;
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

        // Z L1b: every gun now has a deterministic ground position. No scene
        // name search is needed. Build one model per frame after field cleanup.
        static int _buildCursor = -1;

        static void Find()
        {
            if (_buildCursor < 0)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 5f;
                if (_guns.Count >= Wanted()) return;
                Scene tile0 = SceneManager.GetSceneByName(EastWorld.SceneName);
                if (!tile0.isLoaded) { _airfieldSince = -1f; return; }
                if (_airfieldSince < 0f) _airfieldSince = Time.realtimeSinceStartup;
                _buildCursor = 0;
            }

            Scene airfield = SceneManager.GetSceneByName("EastAirfield");
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) { _airfieldSince = -1f; _buildCursor = -1; return; }
            // The greybox fallback has no named emplacement: after 40 s of a
            // loaded tile the guns stand on the ground at the assembly's spots.
            bool fallback = Time.realtimeSinceStartup - _airfieldSince > 40f;
            for (int k = _buildCursor; k < Ids.Length; k++)
            {
                _buildCursor = k + 1;
                if (Has(Ids[k])) continue;
                if (!TownSpot[k])
                {
                    // F3 removes the old concrete/sandbag posts. Build the
                    // field positions only after its complete collider pass.
                    if (!fallback || !EastWorld.ContentReady || AirfieldObjects.Pending > 0 || (k == 4 && !ShortRange.On)) continue;
                    int index = k >= 4 ? k + 1 : k;
                    Scene scene = airfield.isLoaded ? airfield : tile;
                    Transform pit = FlakPositions.Build(index, scene);
                    if (pit == null) continue;
                    Gun field = k == 4
                        ? Zu23Model.Build(index, Ids[k], Spots[k], SpotYaws[k], scene)
                        : FlakModel.Build(index, Ids[k], Names[k], null, null, Spots[k], SpotYaws[k], scene);
                    if (field != null)
                    {
                        FlakPositions.Attach(field, pit);
                        _guns.Add(field);
                        return; // One gun per frame, never a six-model burst.
                    }
                    UnityEngine.Object.Destroy(pit.gameObject);
                    continue;
                }
                if (TownSpot[k])
                {
                    // The town battery stands on the ground: once the tile's
                    // content had the same 40 s to load as the fallback.
                    if (!fallback || !B(CfgTown) || !MilitaryTown.On) continue;
                    Gun t = FlakModel.Build(k, Ids[k], Names[k], null, null, Spots[k], SpotYaws[k], tile);
                    if (t != null) { t.Town = true; t.TownSide = TowerRadar.SideId(Fraktion.Eigene(MilitaryTown.Faction())); _guns.Add(t); return; }
                    continue;
                }
            }
            _buildCursor = -1;
        }

        /// <summary>How many guns this world should have: the airfield's two,
        /// and the town's two while its battery is on.</summary>
        static int Wanted()
        {
            int n = 0;
            bool town = B(CfgTown) && MilitaryTown.On;
            for (int k = 0; k < Ids.Length; k++) if (k == 4 ? ShortRange.On : (!TownSpot[k] || town)) n++;
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
                if (_guns[i].Earthwork != null) UnityEngine.Object.Destroy(_guns[i].Earthwork.gameObject);
                else if (_guns[i].Root != null) UnityEngine.Object.Destroy(_guns[i].Root.gameObject);
            _guns.Clear();
            FlakAmmo.Reset();
            _buildCursor = -1;
            AirDefenceDamage.Reset(0, 6);
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
            float turn = g.ShortRange ? 70f : Mathf.Max(1f, F(CfgTurn, 20f));
            float elev = g.ShortRange ? 55f : Mathf.Max(1f, F(CfgElev, 12f));
            float acc = g.ShortRange ? 180f : Mathf.Max(1f, F(CfgAccel, 40f));
            // W AA2: assisted handwheels while a player uses a 52-K (remote players
            // too); the NPC bracketing/traverse tuning stays.
            if (!g.ShortRange && PlayerAt(g)) { turn = Mathf.Max(turn, 30f); elev = Mathf.Max(elev, 24f); acc = Mathf.Max(acc, 90f); }
            float min = g.ShortRange ? ZuGroundCore.MinPitch : F(CfgPitchMin, -3f), max = g.ShortRange ? 90f : F(CfgPitchMax, 82f);

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
                g.Recoil = Mathf.MoveTowards(g.Recoil, 0f, dt / (g.ShortRange ? 0.08f : RunOut));
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
            bool dead = !AirDefenceDamage.Alive(g.Index);
            float r = !dead && B(CfgRecoil) ? g.Recoil : 0f;
            if (g.PoseValid && g.PosedYaw == g.Yaw && g.PosedPitch == g.Pitch
                && g.PosedRecoil == r && g.PosedDead == dead && g.PosedSeq == g.Seq) return;
            g.PoseValid = true; g.PosedYaw = g.Yaw; g.PosedPitch = g.Pitch;
            g.PosedRecoil = r; g.PosedDead = dead;
            g.PosedSeq = g.Seq;
            if (g.Mount != null) g.Mount.localRotation = Quaternion.Euler(0f, g.Yaw, 0f);
            // B2: the shot jerks the cradle up a little on its trunnions
            // (gone within a quarter of the run-out).
            if (g.Cradle != null) g.Cradle.localRotation = Quaternion.Euler(dead ? 12f : -g.Pitch - Kick * r * r * r * r, 0f, dead ? 8f : 0f);
            if (g.Barrel != null)
            {
                // The recoil curve: the shot throws the barrel the whole stroke
                // back at once, the recuperator eases it home (smoothstep).
                if (g.ShortRange) g.Barrel.localPosition = g.BarrelHome
                    - Vector3.forward * (g.Stroke * r * r * (3f - 2f * r) * ((g.Seq & 1) == 0 ? 1f : 0f));
                else g.Barrel.localPosition = new Vector3(0f, 0f, -g.Stroke * r * r * (3f - 2f * r));
                if (g.BarrelRight != null) g.BarrelRight.localPosition = g.BarrelRightHome
                    - Vector3.forward * (g.Stroke * r * r * (3f - 2f * r) * ((g.Seq & 1) == 1 ? 1f : 0f));
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
            Vector3 d = p - from;
            tof = MercAACore.FlightTime(d.x, d.y, d.z, v.x, v.y, v.z, speed, Gravity);
            Vector3 aim = p + v * tof;
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
            // Metres in the config, world units from here on. The proximity
            // fuze never reaches past the cloud: its burst always hits its trigger.
            _spec.Proximity = Mathf.Min(Mathf.Max(0f, F(CfgProximity, 10f)) * K, _spec.Splash);
            _spec.ArmDistance = ArmMetres * K;
            _spec.GroundBurst = GroundBurst;
            _spec.Burst = FlakNet.SendBurst;
            _spec.HeliHits = Mathf.Max(1, CfgHeliHits == null ? 2 : CfgHeliHits.Value);
            _spec.Tracer = B(CfgTracers);
            _spec.Flak = true;
            _spec.Exact = true; // dispersion is chosen once in Shoot and sent on the wire
            _spec.PuffFx = B(CfgPuffs);
            _spec.PuffScale = PuffScale;
            _spec.BurstSound = B(CfgBurstSound) ? (Action<Vector3>)FlakSound.Burst : null;
            return _spec;
        }

        /// <summary>The 85 mm puff against the 23 mm one (GepardFx.Flak): a
        /// ball of black smoke some 15 m across that hangs for seconds.</summary>
        const float PuffScale = 2.4f;

        /// <summary>A shell arms this far from the muzzle: no proximity burst
        /// and no ground blast nearer the crew (a dud strikes with a spark).</summary>
        internal const float ArmMetres = 30f;

        /// <summary>An armed shell struck the ground, a building or a vehicle,
        /// on the shot's authority: the game's own networked explosion carries
        /// the blast to players, NPCs, vehicles and every explosion hook (AA
        /// objects, fuel, NPC aircraft); frozen NPCs the native blast cannot
        /// reach get the same profile (the BTR's HE rule).</summary>
        static void GroundBurst(Vector3 at)
        {
            float dmg = Mathf.Max(0f, F(CfgImpactDamage, 1200f));
            float radius = Mathf.Max(0f, F(CfgImpactRadius, 7f)) * K;
            if (dmg <= 0f || radius <= 0f) return;
            try
            {
                RocketHook.Detonate(at, dmg, radius, 3f);
                ShellSplash.SweepFrozen(at, dmg, radius);
            }
            catch (Exception ex)
            {
                if (_blastWarned) return;
                _blastWarned = true;
                RevivalPlugin.L.LogWarning("Flak: 85 mm ground burst without the game's explosion - " + ex.Message);
            }
        }
        static bool _blastWarned;

        /// <summary>The replication tag of a gun's shot: the same on the
        /// shooter and on every peer's picture of it.</summary>
        internal static int Tag(Gun g, int seq) { return seq * 8 + (g.Index & 7); }

        /// <summary>One shell, with its flash, the muzzle brake's side blast,
        /// smoke, dust off the ground, the recoil and the report.
        /// <paramref name="fuze"/> is the range the shell bursts at if it
        /// meets nothing (world units).</summary>
        internal static void Shoot(Gun g, Vector3 aim, bool aimValid, float fuze, bool live,
                                   List<GepardGun.Contact> contacts)
        {
            if (g.ShortRange) { ShortRange.Shoot(g, aim, aimValid, fuze, live, contacts); return; }
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
            // A range-set time fuze. The player's is exact (W AA2: a random error
            // moved the burst tens of metres past a correctly led aircraft); an
            // NPC or merc crew keeps its W AA3 calibrated error.
            float fuzeError = live && !PlayerAt(g) ? MercAA.Calibration(g).FuzeError : 0f;
            float life = range / Speed * (fuzeError > 0f ? UnityEngine.Random.Range(1f - fuzeError, 1f + fuzeError) : 1f);
            Vector2 spread = UnityEngine.Random.insideUnitCircle * (Spec().Dispersion * 0.001f);
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            dir = (dir + right * spread.x + Vector3.Cross(dir, right) * spread.y).normalized;
            // W AA4: a kill pays the player at the gun, or the merc crew's
            // owner; a garrison crew's kill pays nobody.
            AirKills.NextShotCredit = FlakAmmo.ShotActor(g);
            GepardShots.NextTag = Tag(g, g.Seq);
            try { GepardShots.Fire(Spec(), g.Owner, muzzle, dir, life, live, contacts); }
            finally { AirKills.NextShotCredit = -1; GepardShots.NextTag = -1; }
            if (live) FlakNet.SendShot(g, muzzle, dir, life);
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

        /// <summary>One man alone at the gun (RateOfFire).</summary>
        internal static float Interval()
        {
            return 60f / Mathf.Clamp(F(CfgRpm, 15f), 1f, 60f);
        }

        /// <summary>Gunner and loader (FullCrewRateOfFire).</summary>
        internal static float CrewInterval()
        {
            return 60f / Mathf.Clamp(F(CfgCrewRpm, 30f), 1f, 60f);
        }

        internal static float ManualInterval()
        {
            return 60f / Mathf.Clamp(F(CfgManualRpm, 30f), 10f, 40f);
        }

        /// <summary>The next shot's time. A shot taken on the first frame it
        /// was due keeps the schedule (a frame's lateness is not added to every
        /// interval); after a pause the count starts from now.</summary>
        internal static float NextShot(float due, float now, float dt, float interval)
        {
            return (now - due <= Mathf.Max(0f, dt) ? due : now) + interval;
        }

        internal static float ManualReloadSeconds() { return Mathf.Max(0.5f, F(CfgManualReload, 6f)); }

        internal static float MaxFuze { get { return SelfDestructU; } }

        /// <summary>Another client lays this gun: follow its pose and fire the
        /// shells it fired (not live), one per step of its shot counter.</summary>
        static void Remote(Gun g, float dt)
        {
            if (!AirDefenceDamage.Alive(g.Index)) { g.Seq = Mathf.Max(g.Seq, g.RemoteSeq); return; }
            if (g.ExactShots) return; // reliable shot packets carry the actual launch, not the desired pose
            if (g.RemoteSeq < 0 || g.Seq == g.RemoteSeq) return;
            int behind = g.RemoteSeq - g.Seq;
            // A first sample, a new layer or a counter that went back: take it
            // as it is, fire nothing.
            if (g.Seq < 0 || behind < 0 || behind > (g.ShortRange ? 8 : 3)) { g.Seq = g.RemoteSeq; return; }
            g.Seq++;
            g.RemoteShotAt = Time.time;
            Shoot(g, Vector3.zero, false, g.RemoteFuze, false, null);
        }

        internal static void OnPose(int index, float yaw, float pitch, int seq, float fuze, bool player, int sender)
        {
            Gun g = ByIndex(index);
            if (g == null || !AirDefenceDamage.Alive(index)) return;
            if (g.ShortRange && !player && sender != MercAA.MasterActor()) return;
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

        internal static void OnShot(Gun g, int seq, int sender, Vector3 muzzle, Vector3 velocity,
                                    float life, float stamp, float gravity)
        {
            if (g == null || Crocodile.IsMaster()) return;
            if (g.ShotSender == sender && seq <= g.ShotSeq) return;
            g.ShotSender = sender; g.ShotSeq = seq;
            g.Seq = seq;
            float age = Mathf.Repeat(RadarClock.Now - stamp + 100000f, 100000f);
            if (age > 10f) age = 0f; // clock fallback before the radar's first tick
            GepardShots.NextTag = Tag(g, seq);
            try
            {
                if (g.ShortRange) ShortRange.Replay(g, muzzle, velocity, life, age, gravity);
                else GepardShots.FireExact(Spec(), g.Owner, muzzle, velocity, life, false, null, age, gravity);
            }
            finally { GepardShots.NextTag = -1; }
            g.Recoil = 1f; g.CaseDue = true; g.RemoteShotAt = g.LastShot = Time.time;
            if (B(CfgMuzzleFlash)) GepardFx.Muzzle(muzzle, velocity.normalized);
            if (B(CfgGunSound))
            {
                if (g.ShortRange) VehicleShotSound.Play(muzzle, false);
                else FlakSound.Report(muzzle);
            }
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
            if (!AirDefenceDamage.Alive(g.Index)) return;
            g.FuzeRange = fuze;
            if (!FlakAmmo.BeginFire(g, player)) return;
            if (g.Seq < 0) g.Seq = 0;
            g.Seq++;
            Shoot(g, aim, aimValid, fuze, true, contacts);
            g.Rounds--;
            g.Fired++;
            Publish(g, player && g == _manned, !g.ShortRange);
        }

        // ------------------------------------------------------------- reload

        internal static void StartReload(Gun g, float seconds)
        {
            if (!Crocodile.IsMaster()) { FlakAmmo.RequestReload(g); return; }
            if (g.Reloading || !FlakAmmo.Ready(g)) return;
            g.Reloading = true;
            g.Ammo.Revision++;
            g.Firing = false;
            g.ReloadUntil = Time.time + Mathf.Max(0.5f, seconds);
        }

        static void TickReload(Gun g)
        {
            if (!Crocodile.IsMaster() || !g.Reloading || Time.time < g.ReloadUntil) return;
            g.Reloading = false;
            g.Rounds = g.Ammo.Rack(g.ShortRange ? ShortRangeCore.Magazine : RoundsFull);
            g.Ammo.Revision++;
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
                g.CrewGKey = "flak/" + id.ToLowerInvariant() + "/g";
                g.CrewCKey = "flak/" + id.ToLowerInvariant() + "/c";
                g.Holder = holder;

                GameObject root = new GameObject("NDR Flak 52-K " + id);
                if (holder != null)
                {
                    root.transform.SetParent(holder, false);
                    root.transform.localPosition = Vector3.zero;
                    root.transform.localRotation = Quaternion.identity;
                    AirKills.AddPit(holder);   // W AA4: the ring shelters the crew
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
            foreach (List<Component> cached in _byKey.Values) cached.Clear();
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
                g.Gunner = Pick(g.CrewGKey, g.Gunner);
                g.Loader = Pick(g.CrewCKey, g.Loader);
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
            if (MercAA.Gun(g.Index) != null) return;
            if (!Flak.B2(Flak.CfgNpcCrew) || g.Root == null) return;
            if (Flak.Side(g) == null) return;
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
        // Parked navigation and the 1.4 s AI pause only need 5 Hz renewal.
        // Near, visible seated poses still run after the animator each frame;
        // distant bodies skip skeleton sampling (including shadow-only draws).
        const float HoldNearU = 150f * Flak.K;
        static int _camFrame = -1;
        static Vector3 _camPos;
        static bool _camOk;

        static bool NearCamera(Vector3 p)
        {
            if (_camFrame != Time.frameCount)
            {
                _camFrame = Time.frameCount;
                Camera cam = CameraOwner.ViewCamera();
                _camOk = cam != null;
                if (_camOk) _camPos = cam.transform.position;
            }
            return !_camOk || (p - _camPos).sqrMagnitude < HoldNearU * HoldNearU;
        }

        internal static void Hold(Flak.Gun g)
        {
            if (g == null || g.Root == null) return;
            bool full = Time.time >= g.NextHold || Flak.PlayerAt(g);
            if (full && g != null) g.NextHold = Time.time + 0.2f;
            Seat(g, g.Gunner, g.SeatGunner, 0, full);
            Seat(g, g.Loader, g.SeatLoader, 1, full);
        }

        static void Seat(Flak.Gun g, Component ai, Transform seat, int clip, bool full)
        {
            if (seat == null || ai == null) return;
            bool sit = Flak.B2(Flak.CfgSeated);
            if (!full && !NearCamera(g.Root.position)) return;
            if (!Flak.Up(ai)) return;
            Vector3 at = seat.position;
            if (sit) at -= Vector3.up * Mathf.Max(0f, Flak.CfgSeatDrop == null ? 2.3f : Flak.CfgSeatDrop.Value);
            else at.y = g.Root.position.y + 0.12f * Flak.K;
            Vector3 fwd = g.Mount != null ? g.Mount.forward : g.Root.forward;
            fwd.y = 0f;
            Quaternion rot = fwd.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(fwd.normalized, Vector3.up);
            Transform tr = ai.transform;
            if (full) GepardCrew.Parken(ai);
            float away = (tr.position - at).sqrMagnitude;
            if (full && away > 64f)
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
            if (full) GepardCrew.Ruhig(ai);
            if (sit) TechnicalCrew.Sitzen(ai, clip);
            if (full) TechnicalCrew.Unbewaffnet(ai);     // hands on the handwheels, no rifle
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
            MercAAPost operatorMerc = MercAA.Gun(g.Index);
            if (operatorMerc != null && operatorMerc.Peaceful && !Flak.Up(g.Gunner)) { Release(g); return; }
            bool gunner = MercAA.Held(g.Index) != null || Flak.Up(g.Gunner), loader = MercAA.Held(g.Index + 7) != null || Flak.Up(g.Loader);
            if (!gunner && !loader) { Release(g); return; }
            if (!g.Laying)
            {
                g.Laying = true;
                g.Rounds = g.Rounds < 0 ? Flak.RoundsPerLoad : g.Rounds;
                g.NextLook = Time.time + (g.Index & 3) * 0.125f;
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
                if (g.Engaged && Time.time - g.LastContact > FlakEngageCore.ResumeSeconds)
                {
                    g.Engaged = false;
                    Flak.Log(g.Id + ": target lost - " + g.Fired + " round(s) fired.");
                }
                // E L1: the radar's cue lays the gun on the coming aircraft.
                if (Cue(g, dt)) g.ScanFrom = -1f;
                // Three quiet seconds: the crew watches the sky (B2).
                else if (Time.time - g.LastContact > 3f) Watch(g);
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
                // E L1: a crew laid on this aircraft by the radar cue has its
                // reaction behind it; the first burst is still laid off.
                g.Held = FlakEngageCore.CueHeld(object.ReferenceEquals(t.Go, g.CueGo), g.CueHeld);
                g.CueGo = null;
                g.CueHeld = 0f;
                g.DrySaid = false;
                // The first shot is laid on a rough range and lead: far off.
                g.Err = Offset(t, mid, dist * MercAA.Calibration(g).InitialMil * 0.001f);
                g.LastVel = t.Vel;
                Flak.Log(g.Id + ": engages " + Describe(t) + " at "
                    + dist.ToString("0", CultureInfo.InvariantCulture) + " u (" + g.Mode + ", "
                    + (Flak.RadarDirected ? "radar-directed" : "by eye") + ").");
                Flak.RaiseEngaging(g, t.Go);
            }
            Dry(g);

            float tof;
            Vector3 aim = Flak.Intercept(mid, t.Pos, t.Vel, out tof) + g.Err;
            float wantYaw, wantPitch;
            Flak.Angles(g, aim - mid, out wantYaw, out wantPitch);
            float min = g.ShortRange ? ZuGroundCore.MinPitch : Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value;
            float max = g.ShortRange ? 90f : Flak.CfgPitchMax == null ? 82f : Flak.CfgPitchMax.Value;
            bool reach = wantPitch >= min - 0.5f && wantPitch <= max + 0.5f;
            g.WantYaw = wantYaw;
            g.WantPitch = Mathf.Clamp(wantPitch, min, max);
            g.Aim = aim;
            g.FuzeRange = Vector3.Distance(mid, aim);

            Vector3 bore = g.Cradle != null ? g.Cradle.forward : g.Root.forward;
            float error = Vector3.Angle(bore, aim - mid);
            g.Held += dt;
            float reaction = MercAA.Calibration(g).Reaction;
            bool ready = reach && g.Held >= reaction && error < 1.5f && g.Mode != FlakMode.HoldFire
                && !g.Reloading;
            Trigger(g, ready, aim, t, mid, tof, gunner && loader);
            Flak.Publish(g, false, false);
        }

        /// <summary>E L1: no target in reach, but the manned radar shows a
        /// hostile beyond it (Search's Cue): the layers put the gun on that
        /// aircraft's track - lead included, no fire - and the seconds spent on
        /// it count against the crew's reaction once it enters reach.</summary>
        internal static bool Cue(Flak.Gun g, float dt)
        {
            GepardGun.Contact c = g.Cue;
            if (c == null || c.Go == null) { g.CueGo = null; g.CueHeld = 0f; return false; }
            if (!object.ReferenceEquals(c.Go, g.CueGo)) { g.CueGo = c.Go; g.CueHeld = 0f; }
            g.CueHeld += dt;
            Vector3 mid = Flak.Mid(g);
            float tof, yaw, pitch;
            Vector3 aim = g.ShortRange ? ShortRange.Intercept(mid, c.Pos, c.Vel, out tof) : Flak.Intercept(mid, c.Pos, c.Vel, out tof);
            Flak.Angles(g, aim - mid, out yaw, out pitch);
            float min = g.ShortRange ? ZuGroundCore.MinPitch : Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value;
            float max = g.ShortRange ? 90f : Flak.CfgPitchMax == null ? 82f : Flak.CfgPitchMax.Value;
            g.WantYaw = yaw;
            g.WantPitch = Mathf.Clamp(pitch, min, max);
            return true;
        }

        /// <summary>E L1: an engaged crew without a round in the gun's stock
        /// says so once (6.68.0 crews laid on raids with an empty stock and the
        /// log only showed "0 round(s) fired").</summary>
        internal static void Dry(Flak.Gun g)
        {
            if (g.DrySaid || FlakAmmo.Ready(g) || !g.Ammo.Known) return;
            g.DrySaid = true;
            Flak.Log(g.Id + ": engaged with an empty gun stock - no rounds to fire (bring "
                + (g.ShortRange ? "ZU belt boxes" : "52-K shells") + ", L at the gun).");
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
            g.Cue = null;
            g.CueGo = null;
            g.CueHeld = 0f;
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
            g.Firing = ready && !g.Reloading && FlakAmmo.Ready(g);
            if (!g.Firing || now < g.NextShot) return;
            bool merc = MercAA.Gun(g.Index) != null;
            float reloadScale = MercAACore.ReloadScale(merc, both);
            if (g.Rounds <= 0)
            {
                g.Firing = false;
                Flak.StartReload(g, Flak.ReloadSeconds() * reloadScale);
                return;
            }
            // How much did the target change course since the last shot?
            float evade = Mathf.Max(0f, Flak.CfgEvade == null ? 1f : Flak.CfgEvade.Value);
            float dv = (t.Vel - g.LastVel).magnitude;
            g.LastVel = t.Vel;
            if (g.Fired > 0 && dv > 0.5f) g.Err += UnityEngine.Random.onUnitSphere * dv * tof * evade;
            Flak.Fire(g, aim, true, g.FuzeRange, g.Hostile, false);
            // One merc is a complete crew, including a legacy loader-seat order.
            // Unmanned radar retains automatic fire at a slower visual cadence.
            g.NextShot = Flak.NextShot(g.NextShot, now, Time.deltaTime,
                MercAACore.CrewInterval(Flak.Interval(), Flak.CrewInterval(), merc,
                    MercAA.Directed(g), both) * UnityEngine.Random.Range(0.92f, 1.08f));
            // The correction for the next shot, from where this one bursts.
            float dist = Vector3.Distance(mid, t.Pos);
            AACalibration calibration = MercAA.Calibration(g);
            float floor = calibration.FloorMil * 0.001f * dist;
            g.Err = g.Err * calibration.Walk + Scatter(t, mid, floor);
            if (g.Rounds <= 0)
            {
                g.Firing = false;
                Flak.StartReload(g, Flak.ReloadSeconds() * reloadScale);
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
        /// may fire on; the largest network threat (the assigned one first).
        /// Without a manned radar retain the crew's visual nearest choice.</summary>
        internal static void Search(Flak.Gun g)
        {
            Vector3 eye = Flak.Mid(g);
            bool direction = MercAA.DirectionAvailable(g);
            MercAAPost man = MercAA.Gun(g.Index);
            float range = (g.ShortRange ? ShortRangeCore.RangeM : MercAACore.Calibrate(man != null, direction, man == null ? 0 : man.Trait).Range) * Flak.K;
            // A L2: a heavy airfield gun also gathers what flies in the airfield zone;
            // E L1: a radar-cued gun what the radar shows out to CueMetres.
            Collect(g.Air, eye, FlakEngageCore.CueCollectMetres(FlakEngageCore.CollectMetres(range / Flak.K,
                !g.ShortRange && !g.Town, Flak.ZoneMetres, Flak.MaxFuze / Flak.K), direction) * Flak.K);

            string side = Flak.OwnerFaction(g);
            g.Hostile.Clear();
            GepardGun.Contact best = null, assigned = null, cue = null;
            GepardGun.Contact current = g.Target != null && g.Target.Go != null ? g.Target : null;
            bool currentReach = false, currentRayed = false, currentSeen = false;
            float bestD = float.MaxValue, cueD = float.MaxValue;
            int bestThreat = int.MinValue;
            for (int i = 0; i < g.Air.Count; i++)
            {
                GepardGun.Contact c = g.Air[i];
                if (c.Go == null) continue;
                bool hostile, friendly;
                Allegiance(c, side, out hostile, out friendly);
                if (friendly || !hostile) continue;
                if (g.Town && !NoFly.TownContains(c.Pos)) continue;
                bool zone = g.Mode == FlakMode.ZoneDefence;
                bool inZone = zone && InZone(g, c.Pos);
                if (zone && !inZone) continue;
                if (g.Mode == FlakMode.AssignedOnly && c.Go != g.Assigned) continue;
                if (g.ShortRange && ShortRange.Drone(c))
                { if (!Airborne(c, 0.5f * Flak.K)) continue; }
                else if (!Airborne(c)) continue;
                g.Hostile.Add(c);
                float d = Vector3.Distance(eye, c.Pos);
                float targetRange = g.ShortRange ? range : Flak.ReachU(g, c.Pos, MercAACore.Calibrate(man != null,
                    direction && RadarShadow.Visible(c.Go), man == null ? 0 : man.Trait).Range);
                if (d > targetRange || c.Pos.y - eye.y > (g.ShortRange ? ShortRangeCore.CeilingM * Flak.K : Flak_Ceiling()))
                {
                    // E L1: the radar's nearest hostile beyond reach is the cue.
                    if (direction && d < cueD && d <= FlakEngageCore.CueMetres * Flak.K && RadarShadow.Visible(c.Go))
                    { cue = c; cueD = d; }
                    continue;
                }
                if (c == current) currentReach = true;
                int cover = !g.ShortRange && direction ? Flak.TargetCover(g, c.Go) : 0;
                float score = !g.ShortRange && direction && RadarShadow.Visible(c.Go)
                    ? MercAACore.TargetScore(d / Flak.K, cover) : d / Flak.K;
                // The contact list still holds every valid target for shell hits.
                // Only a target that can beat the current choice needs a LOS ray.
                int threat = direction ? MercAACore.CoveredThreat(Threat(c), cover) : 0;
                if (c.Go != g.Assigned && (threat < bestThreat || (threat == bestThreat && score >= bestD))) continue;
                bool seen = Sight(g, eye, c);
                if (c == current) { currentRayed = true; currentSeen = seen; }
                if (!seen) continue;
                if (c.Go == g.Assigned) assigned = c;
                if (threat > bestThreat || (threat == bestThreat && score < bestD))
                { best = c; bestD = score; bestThreat = threat; }
            }
            if (assigned != null) best = assigned;
            // Stay on the target being walked in unless the assigned one appears
            // or the radar shows a clearly more urgent one (A L2: a drifting ETA
            // score no longer swaps similar aircraft and restarts the bracketing).
            // E L1: nor does a brief loss of sight drop it (FlakEngageCore.Hold).
            if (current != null && best != current && assigned == null && currentReach)
            {
                if (!currentRayed) { currentRayed = true; currentSeen = Sight(g, eye, current); }
                if (best == null)
                {
                    if (FlakEngageCore.Hold(g.Engaged, true, Time.time - (currentSeen ? Time.time : g.LastSight)))
                        best = current;
                }
                else if (!g.ShortRange && currentSeen
                    && FlakEngageCore.Keep(direction ? MercAACore.CoveredThreat(Threat(current), Flak.TargetCover(g, current.Go)) : 0,
                        bestThreat, direction))
                    best = current;
            }
            if (best != null && (best != current || !currentRayed || currentSeen)) g.LastSight = Time.time;
            if (best != g.Target)
            {
                // A L2: a target gone for one scan keeps the solution (Control
                // ends the engagement after ResumeSeconds); found again, the
                // crew fires on without a new reaction or first-shot offset.
                if (best == null) g.LostGo = g.Target.Go;
                else if (!FlakEngageCore.Resume(g.Engaged,
                    g.Target == null && object.ReferenceEquals(best.Go, g.LostGo), Time.time - g.LastContact))
                {
                    g.Held = 0f;
                    g.Engaged = false;
                }
            }
            g.Target = best;
            g.Cue = best == null ? cue : null;
        }

        static float Flak_Ceiling() { return Flak.CeilingU; }

        static int Threat(GepardGun.Contact c)
        {
            Vector3 d = c.Pos - TowerRadar.RadarPos;
            int eta = RadarClarityCore.Eta(d.x, d.z, c.Vel.x, c.Vel.z);
            int type = AirPicturePolicy.Type(c.Kind, c.Kind == 6 && NpcAircraft.IsTu95(c.Go));
            int range = Mathf.RoundToInt(new Vector2(d.x, d.z).magnitude / Flak.K / 100f);
            return RadarClarityCore.Threat(-1, type, eta, range);
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
            Drone.Net.AirContacts(air, eye, range);
            SurvNet.AirContacts(air, eye, range);
            for (int i = air.Count - 1; i >= 0; i--)
            {
                GepardGun.Contact c = air[i];
                bool gone = c.Go == null || !c.Go.activeInHierarchy || now - c.SeenAt > 1.2f
                    || (c.Kind == 0 && PlayerHeli.MissileTarget(c.HeliView) == null)
                    || (c.Kind == 6 && !GepardAir.Alive(c));
                if (gone) air.RemoveAt(i);
            }
        }

        internal static void Offer(List<GepardGun.Contact> air, GameObject go, int kind, int actor, GepardAir.Source src,
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
                if (kind == 2 || kind == 3)
                {
                    c.LocalCentre = Vector3.zero;
                    c.Radius = kind == 2 ? Mathf.Max(0.30f, 0.18f * RevivalPlugin.CfgDroneModelScale.Value) : SurvDrone.HitRadius;
                }
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
            if (Flak.RadarDirected && !MercAA.Directed(g)) lag /= Mathf.Max(1f, Flak.DirTracking);
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

        /// <summary>Pilot IFF: passengers and nearby players do not change it.
        /// Unknown pilots are never permission to fire.</summary>
        internal static void Allegiance(GepardGun.Contact c, string side, out bool hostile, out bool friendly)
        {
            string pilot = AirPilot.Faction(c);
            friendly = side != null && pilot == side;
            hostile = AirDefencePolicy.Hostile(side, pilot);
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
        internal static bool Airborne(GepardGun.Contact c) { return Airborne(c, -1f); }

        internal static bool Airborne(GepardGun.Contact c, float overrideMin)
        {
            float min = overrideMin >= 0f ? overrideMin : Mathf.Max(0f, Flak.CfgMinHeight == null ? 3f : Flak.CfgMinHeight.Value) * Flak.K;
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
        internal static bool Sight(Flak.Gun g, Vector3 from, GepardGun.Contact c)
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
            MercAAPost merc = MercAA.Gun(g.Index);
            if (merc != null && t.IsChildOf(merc.Ai.transform)) return true;
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
        static float _nextLook, _fuzeM;
        static bool _autoLocked;
        static GepardGun.Contact _leadTarget;
        static float _leadTof, _leadRange, _nextLead, _nextClear, _clearDistance;
        static string _order;
        static int _orderMode = -1, _orderSide = -2, _orderOwner = -2, _orderHolder = -2;
        static bool _orderWorking;
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
                if (!AirDefenceDamage.Alive(g.Index)) _nearWhy = AirDefenceDamage.DestroyedPrompt;
                else if (Flak.Up(g.Gunner) || Flak.Up(g.Loader)) _nearWhy = "The crew is at this gun.";
                else if (Time.time < g.ClaimedUntil) _nearWhy = "Another player is at this gun.";
                break;
            }
            if (_near == null || _nearWhy != null) return;
            if (GameUi.KeyDown(Key())) Enter(_near, me);
        }

        static void Enter(Flak.Gun g, GameObject me)
        {
            if (!CameraOwner.Request(CameraOwner.Flak, true, g.ShortRange ? "ZU-23-2" : "52-K")) { Hint("The camera is busy.", 2f); return; }
            Flak._manned = g;
            FlakAmmo.Observe(g);
            _stoodAt = me.transform.position;
            _cmdYaw = g.Yaw;
            _cmdPitch = g.Pitch;
            _zoom = false;
            _fuzeM = 0f;
            _leadTarget = null;
            _autoLocked = false;
            _nextLead = _nextLook = _nextClear = 0f;
            _orderOwner = -2;
            _air.Clear();
            if (g.Rounds < 0) g.Rounds = g.Ammo.Rack(g.ShortRange ? ShortRangeCore.Magazine : Flak.RoundsPerLoad);
            g.WantYaw = g.Yaw;
            g.WantPitch = g.Pitch;
            Flak.Log("the local player mans " + g.Id + " (" + g.Rounds + " rounds).");
        }

        internal static void Leave(string why)
        {
            Flak.Gun g = Flak._manned;
            if (g == null) return;
            Flak._manned = null;
            _leadTarget = null;
            _autoLocked = false;
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
            float min = g.ShortRange ? ZuGroundCore.MinPitch : Flak.CfgPitchMin == null ? -3f : Flak.CfgPitchMin.Value;
            float max = g.ShortRange ? 90f : Flak.CfgPitchMax == null ? 82f : Flak.CfgPitchMax.Value;
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
                FlakFire.Collect(_air, Flak.Mid(g), Flak.GunRange(g));
                string owner = Flak.OwnerFaction(g);
                for (int i = _air.Count - 1; i >= 0; i--)
                {
                    GepardGun.Contact c = _air[i];
                    if (!AirDefencePolicy.Hostile(owner, AirPilot.Faction(c))
                        || (g.Town && !NoFly.TownContains(c.Pos)) || !FlakFire.Airborne(c)
                        || !FlakFire.Sight(g, Flak.Mid(g), c))
                        _air.RemoveAt(i);
                }
            }
            FlakFire.FollowAll(_air, dt, 5f);
            UpdateLead(g);
            UpdateOrder(g);

            if (GameUi.KeyDown(KeyCode.R) && !g.Reloading && g.Rounds < (g.ShortRange ? ShortRangeCore.Magazine : Flak.RoundsPerLoad))
                Flak.StartReload(g, ManualReload(g));

            // The fuze setter. Automatic: the selected lead solution, else
            // the longest setting. Selection follows the player's sight. The
            // mouse wheel sets it by hand in 100 m steps; F goes back to auto.
            float wheel = GameUi.Axis("Mouse ScrollWheel");
            if (wheel > 0.01f || wheel < -0.01f)
            {
                if (_fuzeM <= 0f) _fuzeM = Mathf.Round(Mathf.Min(AutoFuze(g), (g.ShortRange ? ShortRangeCore.RangeM * Flak.K : Flak.MaxFuze)) / Flak.K / 100f) * 100f;
                _fuzeM = Mathf.Clamp(_fuzeM + (wheel > 0f ? 100f : -100f), 200f, g.ShortRange ? ShortRangeCore.RangeM : Flak.MaxFuze / Flak.K);
            }
            if (GameUi.KeyDown(KeyCode.F)) _fuzeM = 0f;
            g.FuzeRange = _fuzeM > 0f ? _fuzeM * Flak.K : AutoFuze(g);

            bool held = GameUi.Button(0) && !g.Reloading && FlakAmmo.Ready(g);
            if (held && g.Rounds <= 0) { Flak.StartReload(g, ManualReload(g)); held = false; }
            g.Firing = held;
            if (held && Time.time >= g.NextShot)
            {
                // Solve again at launch, using the muzzle rather than the
                // cradle: the cached cue is advisory, never an aim snap.
                if (_fuzeM <= 0f && _autoLocked && _leadTarget != null)
                {
                    float tof, fuze;
                    Lead(g, g.Muzzle != null ? g.Muzzle.position : Flak.Mid(g), _leadTarget, out tof, out fuze);
                    g.FuzeRange = Mathf.Clamp(fuze, 30f, MaxFuze(g));
                }
                Flak.Fire(g, Vector3.zero, false, g.FuzeRange, _air, true);
                g.NextShot = g.ShortRange ? Time.time + ShortRangeCore.ShotSeconds
                    : Flak.NextShot(g.NextShot, Time.time, Time.deltaTime, Flak.ManualInterval());
                if (g.Rounds <= 0) Flak.StartReload(g, ManualReload(g));
            }
            else Flak.Publish(g, true, false);
        }

        /// <summary>The automatic fuze uses the same flight time as the cue.</summary>
        static float AutoFuze(Flak.Gun g)
        {
            return _autoLocked ? Mathf.Clamp(_leadRange, 30f, MaxFuze(g)) : MaxFuze(g);
        }

        // W AA5: the ZU-23's own range, reload and ballistics on the manual path.
        static float MaxFuze(Flak.Gun g) { return g.ShortRange ? ShortRangeCore.RangeM * Flak.K : Flak.MaxFuze; }

        static float ManualReload(Flak.Gun g)
        {
            return g.ShortRange ? ShortRangeCore.ReloadSeconds * 1.5f : Flak.ManualReloadSeconds();
        }

        /// <summary>Lead point and the fuze range for it (the 52-K: flight time x
        /// shell speed; the ZU-23: the distance to the lead point).</summary>
        static Vector3 Lead(Flak.Gun g, Vector3 from, GepardGun.Contact c, out float tof, out float fuze)
        {
            Vector3 aim = g.ShortRange ? ShortRange.Intercept(from, c.Pos, c.Vel, out tof) : Flak.Intercept(from, c.Pos, c.Vel, out tof);
            fuze = g.ShortRange ? Vector3.Distance(from, aim) : tof * Flak.Speed;
            return aim;
        }

        // Only the manned gun, 10 Hz. Retain the selected contact while it
        // stays near the sight so crossing a wingman does not switch the fuze.
        static void UpdateLead(Flak.Gun g)
        {
            if (Time.time < _nextLead) return;
            _nextLead = Time.time + 0.1f;
            Vector3 mid = g.Muzzle != null ? g.Muzzle.position : Flak.Mid(g);
            Vector3 bore = Flak.Direction(g, _cmdYaw, _cmdPitch);
            float best = 12f;
            GepardGun.Contact chosen = null;
            for (int i = 0; i < _air.Count; i++)
            {
                GepardGun.Contact c = _air[i];
                if (c.Go == null) continue;
                float tof;
                float fuze;
                Vector3 aim = Lead(g, mid, c, out tof, out fuze);
                if (fuze > MaxFuze(g) || float.IsNaN(tof) || float.IsInfinity(tof)) continue;
                float a = Mathf.Min(Vector3.Angle(bore, aim - mid), Vector3.Angle(bore, c.Pos - mid));
                if (c == _leadTarget && a < 14f) { chosen = c; break; }
                if (a >= best) continue;
                best = a;
                chosen = c;
            }
            _leadTarget = chosen;
            _autoLocked = chosen != null;
            if (_autoLocked) Lead(g, mid, chosen, out _leadTof, out _leadRange);
        }

        static void UpdateOrder(Flak.Gun g)
        {
            int owner = Flak.OwnerSide(g), holder = AirfieldOwnership.Holder;
            bool working = TowerRadar.Built && TowerRadar.Working;
            if (owner == _orderOwner && holder == _orderHolder && TowerRadar.Mode == _orderMode
                && TowerRadar.ControlSide == _orderSide && working == _orderWorking) return;
            _orderOwner = owner; _orderHolder = holder; _orderWorking = working;
            _orderMode = TowerRadar.Mode; _orderSide = TowerRadar.ControlSide;
            _order = Flak.SightOrder == null ? null : Flak.SightOrder(g.Id);
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
        /// catches up at the mount's slew rate). A cached ray at 10 Hz, only
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
            if (Time.time < _nextClear) return from + d * Mathf.Min(dist, _clearDistance);
            _nextClear = Time.time + 0.1f;
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
            _clearDistance = best >= dist + margin ? dist : Mathf.Max(0.2f * Flak.K, best - margin);
            return from + d * _clearDistance;
        }

        // ---------------------------------------------------------------- HUD

        static Texture2D _white;
        static GUIStyle _text;
        static readonly GUIContent _label = new GUIContent();
        static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        static string _prompt, _promptZu;
        static string _controls;
        static readonly Vector2[] RingPoints = MakeRing();
        static readonly Color Amber = new Color(1f, 0.78f, 0.3f, 0.95f);

        static Vector2[] MakeRing()
        {
            Vector2[] points = new Vector2[16];
            for (int i = 0; i < points.Length; i++)
            {
                float a = i * Mathf.PI * 2f / points.Length;
                points[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            return points;
        }

        internal static void Draw(List<Flak.Gun> guns)
        {
            try
            {
                FlakAmmo.Draw();
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
                    _prompt = "[" + Key() + "] Man the 52-K 85 mm gun";
                    _promptZu = "[" + Key() + "] Man the ZU-23-2 23 mm gun";
                    _controls = "LMB fire   RMB sight   wheel fuze   F auto   R rack   " + Key() + " / Esc leave";
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
                        if (_autoLocked && _leadTarget != null && _leadTarget.Go != null)
                        {
                            // Extrapolate the cached flight time with the current
                            // contact position; no solver or query in OnGUI.
                            Vector3 aim = _leadTarget.Pos + _leadTarget.Vel * _leadTof
                                + Vector3.up * (0.5f * Flak.Gravity * _leadTof * _leadTof);
                            Vector3 cue = cam.WorldToScreenPoint(aim);
                            if (cue.z > 0f)
                            {
                                bool inside = cue.x > 18f && cue.x < w - 18f && h - cue.y > 18f && h - cue.y < h - 110f;
                                float x = Mathf.Clamp(cue.x, 18f, w - 18f), y = Mathf.Clamp(h - cue.y, 18f, h - 110f);
                                GUI.color = inside ? Color.cyan : Amber;
                                // Four corners distinguish the lead cue from
                                // the bore ring. Put the green bore in this box.
                                VanillaUi.Texture(new Rect(x - 10f, y - 10f, 6f, 2f), _white);
                                VanillaUi.Texture(new Rect(x + 4f, y - 10f, 6f, 2f), _white);
                                VanillaUi.Texture(new Rect(x - 10f, y + 8f, 6f, 2f), _white);
                                VanillaUi.Texture(new Rect(x + 4f, y + 8f, 6f, 2f), _white);
                                Label(new Rect(x - 30f, y + 12f, 60f, 22f), "LEAD", GUI.color);
                                GUI.color = Color.white;
                            }
                        }
                    }
                    float left = w * 0.5f - 260f;
                    Label(new Rect(left, h - 70f, 95f, 24f), g.ShortRange ? "ZU-23-2 23 mm" : "52-K 85 mm", Amber);
                    Label(new Rect(left + 100f, h - 70f, 95f, 24f), g.Reloading ? "NEXT RACK" : "ROUNDS", Amber);
                    Number(left + 200f, h - 70f, g.Reloading ? Mathf.CeilToInt(Mathf.Max(0f, g.ReloadUntil - Time.time)) : g.Rounds);
                    Label(new Rect(left + 240f, h - 70f, 110f, 24f), _fuzeM > 0f ? "FUZE SET" : _autoLocked ? "FUZE AUTO" : "FUZE MAX", Amber);
                    Number(left + 355f, h - 70f, Mathf.RoundToInt(g.FuzeRange / Flak.K));
                    Label(new Rect(left + 410f, h - 70f, 22f, 24f), "m", Amber);
                    Label(new Rect(0f, h - 40f, w, 24f), _controls, Amber);
                    float wait = g.Reloading ? g.ReloadUntil - Time.time : g.NextShot - Time.time;
                    GUI.color = wait > 0f ? Amber : Color.green;
                    VanillaUi.Texture(new Rect(w * 0.5f - 60f, h - 82f, 120f * (1f - Mathf.Clamp01(wait /
                        (g.Reloading ? ManualReload(g) : g.ShortRange ? ShortRangeCore.ShotSeconds : Flak.ManualInterval()))), 3f), _white);
                    GUI.color = Color.white;
                    if (!string.IsNullOrEmpty(_order)) Label(new Rect(0f, h - 108f, w, 26f), _order, Color.white);
                }
                else if (_near != null)
                {
                    string s = _nearWhy ?? (_near.ShortRange ? _promptZu : _prompt);
                    VanillaUi.Prompt(s, h * 0.62f);
                }
                if (_hint != null && Time.time <= _hintUntil)
                    VanillaUi.PromptAbove(_hint, h * 0.62f - 6f);
            }
            catch { }
        }

        static void Label(Rect r, string s, Color c)
        {
            // Centred by hand: the alignment enum lives in a module the build
            // does not reference.
            _label.text = s;
            float tw = _text.CalcSize(_label).x;
            r = new Rect(r.x + (r.width - tw) * 0.5f, r.y, tw + 4f, r.height);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            VanillaUi.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), _label, _text);
            GUI.color = c;
            VanillaUi.Label(r, _label, _text);
            GUI.color = Color.white;
        }

        // Reuse digit strings and GUIContent, including moving ranges/countdowns.
        static void Number(float x, float y, int value)
        {
            value = Mathf.Clamp(value, 0, 99999);
            int divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            do
            {
                Label(new Rect(x, y, 10f, 24f), Digits[value / divisor], Amber);
                value %= divisor; divisor /= 10; x += 10f;
            } while (divisor > 0);
        }

        /// <summary>The collimator sight: a centre cross and two lead rings.</summary>
        static void Reticle(float cx, float cy, float r)
        {
            GUI.color = Amber;
            VanillaUi.Texture(new Rect(cx - 12f, cy - 1f, 24f, 2f), _white);
            VanillaUi.Texture(new Rect(cx - 1f, cy - 12f, 2f, 24f), _white);
            Ring(cx, cy, r * 0.5f);
            Ring(cx, cy, r);
            GUI.color = Color.white;
        }

        static void Ring(float cx, float cy, float r)
        {
            for (int i = 0; i < RingPoints.Length; i++)
            {
                Vector2 p = RingPoints[i];
                VanillaUi.Texture(new Rect(cx + p.x * r - 1f, cy + p.y * r - 1f, 2f, 2f), _white);
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
        const int Burst = 2;
        static readonly float[] _burst = new float[8];
        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optType;
        static readonly float[] _poseData = new float[8];
        static object[] _poseArgs;

        delegate void PoseSend(byte code, object data, bool reliable, object options);
        static PoseSend _send;
        static object _options;
        static readonly float[][] _packets = MakePackets();
        static readonly float[] _shot = new float[22];
        static float[][] MakePackets()
        {
            float[][] p = new float[7][];
            for (int i = 0; i < p.Length; i++) p[i] = new float[8];
            return p;
        }

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
                object poseOpts = _optType == null ? null : Activator.CreateInstance(_optType);
                _poseArgs = new object[] { (byte)code, _poseData, false, poseOpts };
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
                if (gun < 0 || gun >= _packets.Length) return;
                float[] data = _packets[gun];
                data[0] = Pose; data[1] = gun; data[2] = yaw; data[3] = pitch;
                data[4] = seq; data[5] = fuze; data[6] = player ? 1f : 0f;
                // Master exact shots for 52-K and finite ZU stocks. Native
                // garrison ZU keeps its existing lightweight pose replay.
                Flak.Gun gun_ = Flak.ByIndex(gun);
                data[7] = gun_ != null && (!gun_.ShortRange || gun_.Ammo.Limited) ? 1f : 0f;
                PrepareSender();
                _send((byte)Code(), data, false, _options);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Flak network send: " + ex.Message); }
        }

        static void PrepareSender()
        {
                if (_send == null)
                {
                    _options = Activator.CreateInstance(_optType);
                    System.Reflection.Emit.DynamicMethod dm = new System.Reflection.Emit.DynamicMethod(
                        "FlakPublish", typeof(void), new Type[] { typeof(byte), typeof(object), typeof(bool), typeof(object) },
                        typeof(FlakNet), true);
                    System.Reflection.Emit.ILGenerator il = dm.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
                    il.Emit(System.Reflection.Emit.OpCodes.Castclass, _optType);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, _raise);
                    if (_raise.ReturnType != typeof(void)) il.Emit(System.Reflection.Emit.OpCodes.Pop);
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    _send = (PoseSend)dm.CreateDelegate(typeof(PoseSend));
                }
        }

        // W AA2: reliable, once per shot. The first seven fields retain the legacy
        // pose format; upgraded peers use the exact launch instead of replay.
        internal static void SendShot(Flak.Gun g, Vector3 muzzle, Vector3 dir, float life)
        {
            if (!_hooked) return;
            try
            {
                Vector3 v = dir * (g.ShortRange ? ShortRangeCore.SpeedM * Flak.K : Flak.Speed);
                // Reused buffer: RaiseEvent serializes before it returns.
                float[] data = _shot;
                data[0] = Pose; data[1] = g.Index; data[2] = g.Yaw; data[3] = g.Pitch; data[4] = g.Seq;
                data[5] = g.FuzeRange; data[6] = 0f; data[7] = 1f; // master launch, including remote player triggers
                data[8] = muzzle.x; data[9] = muzzle.y; data[10] = muzzle.z;
                data[11] = v.x; data[12] = v.y; data[13] = v.z;
                data[14] = life; data[15] = RadarClock.Now; data[16] = -Flak.Gravity;
                // Carry the post-shot stock in this reliable launch, without
                // a second message per shell. Loaded count is decremented next.
                data[17] = g.Ammo.Stock; data[18] = g.Ammo.Revision;
                data[19] = g.Ammo.Limited ? 1f : 0f; data[20] = Mathf.Max(0, g.Rounds - 1); data[21] = 0f;
                PrepareSender();
                _send((byte)Code(), data, true, _options);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Flak shot send: " + ex.Message); }
        }

        // A live 52-K round's proximity burst: { 2, gun, seq, x, y, z, 0, 0 },
        // reliable, from the shot's owner (after its reliable shot packet).
        // A peer bursts its picture of that shot there, once; an older peer
        // drops kind 2 and draws the shell on to its time fuze.
        internal static void SendBurst(int tag, Vector3 at)
        {
            if (!_hooked || tag < 0) return;
            try
            {
                float[] data = _burst;
                data[0] = Burst; data[1] = tag & 7; data[2] = tag >> 3;
                data[3] = at.x; data[4] = at.y; data[5] = at.z; data[6] = data[7] = 0f;
                PrepareSender();
                _send((byte)Code(), data, true, _options);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Flak burst send: " + ex.Message); }
        }

        /// <summary>Only the sender of that gun's exact shots is believed, a
        /// picture only ever bursts (Terminate): no damage on this client.</summary>
        internal static void OnBurst(float[] f, int sender)
        {
            if (f == null || f.Length < 6) return;
            Flak.Gun g = Flak.ByIndex(Mathf.RoundToInt(f[1]));
            if (g == null || Crocodile.IsMaster() || sender != MercAA.MasterActor() || g.ShotSender != sender) return;
            Vector3 at = new Vector3(f[3], f[4], f[5]);
            float reach = Flak.MaxFuze * 1.2f;
            if ((at - Flak.Mid(g)).sqrMagnitude > reach * reach) return;
            GepardShots.Terminate(Flak.Tag(g, Mathf.RoundToInt(f[2])), at);
        }

        internal static void SendPacket(float[] data, bool reliable)
        {
            if (!_hooked) return;
            try
            {
                PrepareSender();
                _send((byte)Code(), data, reliable, _options);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AA network send: " + ex.Message); }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 5) return;
                for (int i = 0; i < f.Length; i++)
                    if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                int kind = Mathf.RoundToInt(f[0]);
                if (kind >= AmmoDepot.State && kind <= AmmoDepot.Loot)
                { AmmoDepot.OnPacket(f, sender); return; }
                if (kind == ZuGroundCore.ModePacket) { ZuGround.OnPacket(f, sender); return; }
                if (f.Length < 7) return;
                if (kind >= AirDefenceDamage.BlastMsg && kind <= AirDefenceDamage.RepairMsg)
                { AirDefenceDamage.OnPacket(f, sender); return; }
                if (kind >= FlakAmmo.State && kind <= FlakAmmo.ReloadAsk)
                { FlakAmmo.OnPacket(f, sender); return; }
                if (kind == Burst) { OnBurst(f, sender); return; }
                if (kind != Pose) return;
                Flak.Gun g = Flak.ByIndex(Mathf.RoundToInt(f[1]));
                if (g == null) return;
                // A player still needs the uncrewed post. NPC launches come
                // only from the master; ownership remains W AA1's authority.
                bool player = f[6] > 0.5f;
                if (player && (Crocodile.ActorDown(sender) || Flak.Up(g.Gunner) || Flak.Up(g.Loader))) return;
                if (!player && !AirfieldOwnership.MasterSender(sender)) return;
                if (player && (f.Length >= 17 || (Crocodile.IsMaster() && !FlakAmmo.PlayerAllowed(g, sender)))) return;
                g.ExactShots = player || (f.Length >= 8 && f[7] > 0.5f);
                Flak.OnPose(Mathf.RoundToInt(f[1]), Mathf.Repeat(f[2] + 180f, 360f) - 180f,
                    Mathf.Clamp(f[3], -15f, 90f), player ? g.Seq : Mathf.Max(0, Mathf.RoundToInt(f[4])),
                    Mathf.Clamp(f[5], 30f, 20000f), f[6] > 0.5f, sender);
                if (f.Length == 22 && f[14] > 0f && f[14] <= 60f && f[16] <= 0f && f[16] >= -100f)
                {
                    Vector3 muzzle = new Vector3(f[8], f[9], f[10]);
                    Vector3 velocity = new Vector3(f[11], f[12], f[13]);
                    if ((muzzle - Flak.Mid(g)).sqrMagnitude > 40f * 40f || velocity.sqrMagnitude < 10000f) return;
                    if (!FlakAmmo.ReceiveState(g, sender, f[17], f[18], f[19], f[20], f[21])) return;
                    Flak.OnShot(g, Mathf.RoundToInt(f[4]), sender, muzzle, velocity, f[14], f[15], f[16]);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Flak network receive: " + ex.Message);
            }
        }
    }
}

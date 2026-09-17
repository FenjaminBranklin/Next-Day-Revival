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
//      so they do not open fire on their own village.
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
//   RevivalMortar.cs Ground      -> ArtyBattery.CrewHoldsGun

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

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

        static ConfigEntry<float> _cfgOrbitRadius;
        static ConfigEntry<float> _cfgOrbitHeight;
        static ConfigEntry<float> _cfgOrbitSpeed;
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
        /// crew. The crew wanders a nine-metre ring (Crew.RingRadius), so this
        /// has to be wider than that or a gunner who took three steps would be
        /// declared dead.</summary>
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

            _cfgOrbitRadius = cfg.Bind("Artillery", "OrbitRadius", 300f,
                "Metres from the settlement centre the drone circles at.");
            // Migrate the released default; retain deliberate custom radii.
            if (_cfgOrbitRadius.Value == 240f) _cfgOrbitRadius.Value = 300f;
            _cfgOrbitHeight = cfg.Bind("Artillery", "OrbitHeight", 85f,
                "Metres above the ground under it. High enough to be a dot, low "
                + "enough to be seen against the sky.");
            _cfgOrbitSpeed = cfg.Bind("Artillery", "OrbitSpeed", 16f,
                "Metres per second along the circle. At 300 m radius one lap "
                + "takes about 118 s.");
            _cfgModelRange = cfg.Bind("Artillery", "ModelRange", 800f,
                "Metres from the player at which the drone gets a visible model. "
                + "Beyond it the orbit is still computed - only the GameObject is "
                + "not built, so a map full of settlements costs nothing.");
            _cfgModelScale = cfg.Bind("Artillery", "ModelScale", 10f,
                "Size of the recon drone model. The player's own surveillance "
                + "drone uses 12.");

            _cfgSpotRadius = cfg.Bind("Artillery", "SpotRadius", 110f,
                "Metres around the point under the drone in which it sees a man. "
                + "The drone's camera looks straight down, so this is a footprint "
                + "on the ground, not a view range.");
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

        // The living NPCs, gathered ONCE for every post instead of once per post:
        // FindObjectsOfType walks the whole scene, and a map with a dozen guns on
        // it would otherwise walk it a dozen times a second.
        static readonly List<Component> _npcs = new List<Component>();
        static float _nextNpcScan;
        static int _npcStamp;

        // The map is only asked whether it is open a few times a second - the
        // question is reflection, and the answer is used for a snapshot that is
        // deliberately not live.
        static float _nextMapCheck;
        static bool _mapOpen;

        /// <summary>What the map draws: where every drone was when the map was
        /// opened. The order asked for the LAST position, not a live feed, and
        /// freezing it is also what keeps the map cheap.</summary>
        class Mark
        {
            public Vector3 Drone;
            public Vector3 Centre;
            public float Radius;
            public bool Spotted;
            public Vector3 SpotPoint;
        }

        static readonly List<Mark> _marks = new List<Mark>();

        static Texture2D _px;
        static Texture2D _ring;

        // ------------------------------------------------- the gun's own seams

        /// <summary>Mortar raised a gun for this settlement. A SAFE settlement -
        /// a trader camp - keeps the vehicle as scenery and gets nothing else:
        /// no crew, no drone, no fire missions.</summary>
        internal static void GunRaised(int settlementId, GameObject gun,
                                       Vector3 centre, string name, bool safe)
        {
            if (!Enabled || gun == null) return;
            if (_byId.ContainsKey(settlementId)) return;
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
                // drone circles on the map" (2026-09-17).
                if (_posts.Count == 0)
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
                    Fly(p, now, me != null, mine);
                    Warn(p, now, me, mine);
                    if (master)
                    {
                        Spot(p, now);
                        Mission(p, now);
                    }
                    Resupply(p, now);
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
                // Behind the gun, where a crew stands: out of the muzzle's way
                // and close enough that they read as ITS men.
                Vector3 at = gun.TransformPoint(new Vector3(0f, 0f, -19.5f));
                float y;
                if (RevivalTroopInsertion.GroundY(at, out y)) at.y = y;

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
                GameObject crew = Crew.DropSquad(at, gun.eulerAngles.y, 2, side, loadout);
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
                Resolve(p);
                if (p.Gunner != null) p.FactionSet = MatchFaction(p);
                RevivalPlugin.L.LogInfo("ArtyBattery: crew for \"" + p.Name
                    + "\" on its feet (gunner " + (p.Gunner != null)
                    + ", operator " + (p.Operator != null) + ").");
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

        // -------------------------------------------------------------- drone

        static float Orbit() { return Mathf.Clamp(F(_cfgOrbitRadius, 300f), 40f, 1500f); }

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
            float r = Orbit();
            float speed = Mathf.Clamp(F(_cfgOrbitSpeed, 16f), 1f, 60f);
            float a = p.Phase + _flightClock * speed / r;
            Vector3 flat = new Vector3(p.Centre.x + Mathf.Cos(a) * r, 0f,
                                       p.Centre.z + Mathf.Sin(a) * r);
            if (!p.GroundSet || now - p.GroundAt > 0.25f)
            {
                p.GroundAt = now;
                float y;
                // THE TERRAIN, NOT WHAT IS STANDING ON IT. GroundY casts a ray
                // with no layer mask, so a roof, a truck or a treetop under the
                // orbit answers as "ground" and the drone hops over every
                // building it passes - at 240 m radius it passes several a lap.
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
            float range = Mathf.Max(100f, F(_cfgModelRange, 800f));
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
            float nearest = 600f;
            direction.Normalize();
            for (int i = 0; i < _posts.Count; i++)
            {
                Post p = _posts[i];
                if (!p.DroneUp || p.DroneHits <= 0) continue;
                float d;
                if (!DroneRay(from, direction, p.DroneAt, out d) || d >= nearest) continue;
                nearest = d;
                best = p;
            }
            if (best == null) return;
            if (RevivalTroopInsertion.MasterClient())
                HitDrone(best.DroneKey, from, direction, best.DroneReadyAt, ArtyRoom.Now());
            else Mortar.Net.SendArty(new object[] { "arty-v1", 1, best.DroneKey,
                new float[] { from.x, from.y, from.z, direction.x, direction.y, direction.z },
                best.DroneReadyAt, ArtyRoom.Now() });
        }

        static bool DroneRay(Vector3 from, Vector3 direction, Vector3 at, out float distance)
        {
            Vector3 delta = at - from;
            distance = Vector3.Dot(delta, direction);
            float radius = Mathf.Clamp(F(_cfgModelScale, 10f) * 0.14f, 0.4f, 4f);
            if (distance < 1f || distance > 600f
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
            for (int i = 0; i < _posts.Count; i++)
            {
                Post p = _posts[i];
                if (p.DroneKey != key) continue;
                p.NextDroneState = 0f;
                DroneState(p, Time.time);
                if (p.DroneHits <= 0 || !OperatorFlies(p) || generation != p.DroneReadyAt) return;
                float distance;
                double age = ArtyRoom.Elapsed(ArtyRoom.Now(), shotTime);
                if (double.IsNaN(age) || age < -0.1 || age > 0.75) return;
                // Rewind the deterministic orbit to the shooter's timestamp.
                // Keep the measured terrain height: only a sub-second correction.
                float angle = (float)Math.Max(0.0, age) * Mathf.Clamp(F(_cfgOrbitSpeed, 16f), 1f, 60f) / Orbit();
                Vector3 radial = p.DroneAt - p.Centre;
                Vector3 rewind = new Vector3(radial.x * Mathf.Cos(angle) + radial.z * Mathf.Sin(angle),
                    radial.y, -radial.x * Mathf.Sin(angle) + radial.z * Mathf.Cos(angle)) + p.Centre;
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
                return;
            }
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

        static float SpotRadius() { return Mathf.Clamp(F(_cfgSpotRadius, 110f), 20f, 400f); }

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

            if (!have) { p.SeenSince = 0f; p.Candidate = null; return; }

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
            return true;
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
        /// Freeze what the map shows the moment it is opened.
        ///
        /// The order was explicit that this does not have to be live - "the last
        /// position as the map was opened" - and taking it at its word is what
        /// keeps the overlay cheap: the snapshot is a handful of vectors, and a
        /// repaint touches no reflection at all.
        /// </summary>
        static void MapSnapshot(float now)
        {
            if (now < _nextMapCheck) return;
            _nextMapCheck = now + 0.2f;
            Component manager, texture;
            Camera cam;
            Vector2 world, map;
            bool open = MapTools.Context(out manager, out texture, out cam, out world, out map);
            if (open == _mapOpen) return;
            _mapOpen = open;
            if (!open) return;

            _marks.Clear();
            float r = Orbit();
            for (int i = 0; i < _posts.Count; i++)
            {
                Post p = _posts[i];
                if (!p.DroneUp) continue;
                Mark m = new Mark();
                m.Drone = p.DroneAt;
                m.Centre = p.Centre;
                m.Radius = r;
                m.Spotted = p.LocalSpotAt > 0f && now - p.LocalSpotAt < 90f;
                m.SpotPoint = p.LocalSpotPoint;
                _marks.Add(m);
            }
        }

        // IMGUI drone marks are not visible to the native-widget scan. Reserve
        // the same frozen snapshot before patrol and convoy names are placed.
        internal static void ReserveMapLabels(MapLabels labels, Component texture,
            Camera camera, Vector2 world, Vector2 map)
        {
            if (!Enabled) return;
            for (int i = 0; i < _marks.Count; i++)
            {
                Mark mark = _marks[i];
                if (Mathf.Abs(mark.Centre.x) > world.x * .75f || Mathf.Abs(mark.Centre.z) > world.y * .75f) continue;
                Vector2 centre, east, north, point;
                if (!MapTools.WorldToGui(mark.Centre, texture, camera, world, map, out centre)
                    || !MapTools.WorldToGui(mark.Centre + new Vector3(mark.Radius, 0f, 0f),
                        texture, camera, world, map, out east)
                    || !MapTools.WorldToGui(mark.Centre + new Vector3(0f, 0f, mark.Radius),
                        texture, camera, world, map, out north)) continue;
                labels.BlockOrbit(centre, Mathf.Abs(east.x - centre.x), Mathf.Abs(north.y - centre.y));
                if (MapTools.WorldToGui(mark.Drone, texture, camera, world, map, out point))
                    labels.BlockScreen(new Rect(point.x - 6f, point.y - 6f, 12f, 12f));
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
        /// One battery on the map: the orbit as SEPARATE dashes and the drone
        /// itself as a small block, both in the Locator red the patrol border
        /// already uses, plus the point where this player was last seen from the
        /// air. Dashes rather than a ring on purpose - that is the house style
        /// for every border drawn on this map, and a solid circle reads as a
        /// zone the game itself drew.
        /// </summary>
        static void DrawMark(Mark m, Component texture, Camera cam,
                             Vector2 world, Vector2 map, Rect clip)
        {
            // A drone whose settlement is not on this map at all (an interior
            // scene has its own, much smaller map) is not drawn.
            if (Mathf.Abs(m.Centre.x) > world.x * 0.75f
                || Mathf.Abs(m.Centre.z) > world.y * 0.75f) return;

            Vector2 centre, rim, dot;
            if (!MapTools.WorldToGui(m.Centre, texture, cam, world, map, out centre)) return;
            if (!MapTools.WorldToGui(m.Centre + new Vector3(m.Radius, 0f, 0f),
                                     texture, cam, world, map, out rim)) return;
            float hw = Mathf.Abs(rim.x - centre.x);
            if (hw < 3f) return;

            Color red = new Color(0.72f, 0.13f, 0.125f, 0.95f);   // Locator red

            // The orbit: 30 dashes of two dots each, with the gap between them
            // left empty.
            GUI.color = red;
            Vector2 c = centre - clip.position;
            float squash = 1f;
            Vector2 rimZ;
            if (MapTools.WorldToGui(m.Centre + new Vector3(0f, 0f, m.Radius),
                                    texture, cam, world, map, out rimZ))
            {
                float hh = Mathf.Abs(rimZ.y - centre.y);
                if (hh > 1f) squash = hh / hw;
            }
            for (int i = 0; i < 30; i++)
            {
                float a0 = i * Mathf.PI * 2f / 30f;
                for (int k = 0; k < 2; k++)
                {
                    float a = a0 + k * 0.035f;
                    float x = c.x + Mathf.Cos(a) * hw;
                    float y = c.y + Mathf.Sin(a) * hw * squash;
                    GUI.DrawTexture(new Rect(x - 1f, y - 1f, 2f, 2f), Px());
                }
            }

            // The drone itself, where it was when the map went up.
            if (MapTools.WorldToGui(m.Drone, texture, cam, world, map, out dot))
            {
                Vector2 d = dot - clip.position;
                GUI.color = new Color(0.05f, 0.05f, 0.05f, 0.85f);
                GUI.DrawTexture(new Rect(d.x - 5f, d.y - 5f, 10f, 10f), Px());
                GUI.color = red;
                GUI.DrawTexture(new Rect(d.x - 4f, d.y - 4f, 8f, 8f), Px());
                GUI.color = new Color(1f, 0.92f, 0.85f, 0.95f);
                GUI.DrawTexture(new Rect(d.x - 1f, d.y - 1f, 2f, 2f), Px());
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
                    _material.SetFloat("_BumpScale", 0.65f);
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

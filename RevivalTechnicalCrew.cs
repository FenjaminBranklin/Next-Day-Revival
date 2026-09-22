// Next Day: Survival - Revival Toolkit
//
// THE CREW THAT RIDES THE TECHNICAL.
//
// A technical in a convoy or on a patrol route is the first vehicle of this
// toolkit whose crew has to be VISIBLE while it drives. Every other one is a
// closed hull: Revival.Crew.cs says in its own class comment that a patrol crew
// is a NUMBER until the vehicle burns, and gives two measured reasons for it.
// Both still hold and neither is broken here:
//
//   1  VehicleGameSystem::SetDamageToAllPassengers walks `Passengers` and calls
//      GetComponent<PlayerNetworkController>().GetPhotonPlayer on every entry.
//      On an NPC that GetComponent returns null and the call throws - in the one
//      moment that must not fail. So NOTHING here ever writes an NPC into
//      `Passengers`. The men ride beside the seat system, not inside it.
//   2  A body PARENTED to a seat is parented on the host alone; the other
//      clients keep getting the NPC's own position sync and would watch the crew
//      trail the truck down the road. So nothing here parents anything either.
//      Instead EVERY client works out where the three men belong from the
//      TRUCK'S OWN TRANSFORM, once a frame in LateUpdate, and writes them there.
//      That is the same answer TechnicalGun.Stellung already gives for a player
//      gunner ("every client computes the same circle from the same mount"), and
//      it is why the men do not lag: their place is derived from a transform the
//      vehicle's own replication already carries smoothly, not from a position
//      that has to travel the wire on its own.
//
// WHICH MAN IS WHICH, ON A MACHINE THAT DID NOT SPAWN THEM. A client that joined
// never made these NPCs and cannot tell them from any other marauder standing
// near a truck. Two facts settle it without a single new network message:
//
//   - The men carry a KEY in their Photon instantiation data. Crew.DropGroundSquad
//     writes it, Crew.GroundKey reads it back on every client, and the editor's
//     ground groups already rely on that channel surviving a master handover.
//     Ours reads "tech/<view id of the truck>/g" for the gunner and ".../c" for
//     the cab, so a man names his own truck AND his own job. The slash is also
//     what keeps Revival.GroundEnemies.cs from reconciling these men as a stale
//     ground group - see the guard there.
//   - The gunner is therefore spawned as a squad of ONE. That is the whole
//     reason for the second settlement per truck, and it buys something a
//     geometric rule cannot: the man at the pintle is still the man at the
//     pintle after one of the other two has been shot. "Whoever stands furthest
//     back" stops being the gunner the moment the gunner is the one who dies,
//     and then a driver gets dragged onto the gun in front of everybody.
//
// WHAT THE MASTER OWNS. Spawning the men, holding them still, aiming the gun and
// firing it. The aim is published the cheapest way there is: the master turns
// the GUNNER'S BODY towards the target, and TechnicalGun.SlewRemote - which
// already exists, and already runs on every client - turns the mount to match
// whatever the man at that station is facing. So the bearing crosses the wire as
// the man's own rotation and nothing else. Elevation does not: SlewRemote reads
// it out of the body's forward, and the body is kept upright, so on a machine
// that is not the master the barrel sits level while the shot itself is computed
// from the true angle here. That is a few degrees of barrel on somebody else's
// screen, and it is written down rather than hidden.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only, no BOM - every player-facing string of this feature lives in
// RevivalUralTruck.cs (class TechnicalText), the same split the rest of the
// technical uses.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalTechnical.cs        Technical.BindConfig / Tick / Pflege / LateFrame,
//                              and TechnicalGun.NpcGunner, which is how the
//                              existing station code finds a gunner who is not
//                              a passenger.
//                              Technical.Install installs BoundPrefix.
//   Revival.Patrol.cs          Patrol.CrewedSide / CrewedList / CrewedCount say
//                              whose truck this is and how its men are dressed;
//                              Patrol.UnloadCrew calls ReleaseRiders instead of
//                              spawning a second crew at the wreck.
//                              Patrol.Verwaist asks Driverless / Wiped and
//                              stops a truck whose cab or whole crew is dead.
//   Revival.GroundEnemies.cs   the guard that leaves our spawn keys alone.
//   Revival.Crew.cs            Crew.DropGroundSquad / Men / GroundKey / Forget,
//                              used unchanged.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>
    /// The three men of one technical: two in the cab, one at the machine gun.
    /// </summary>
    public static class TechnicalCrew
    {
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<bool> CfgCabCrew;
        public static ConfigEntry<bool> CfgGunnerFires;
        public static ConfigEntry<float> CfgEngageRange;
        public static ConfigEntry<float> CfgReaction;
        public static ConfigEntry<float> CfgSpread;
        public static ConfigEntry<int> CfgBurst;
        public static ConfigEntry<float> CfgBurstPause;
        public static ConfigEntry<float> CfgTraverse;
        public static ConfigEntry<float> CfgCabDrop;
        public static ConfigEntry<bool> CfgGunnerBound;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("TechnicalCrew", "Enabled", true,
                "Man a technical that drives a convoy or patrol route. Three "
                + "men ride it: a driver and a co-driver in the cab and a "
                + "gunner standing at the machine gun. Their weapon and uniform "
                + "come from the route editor, exactly as for every other "
                + "vehicle in the column; when the route has no composition "
                + "they are dressed by the Patrol keys instead. Switching this "
                + "off leaves the truck driving empty and its crew appears at "
                + "the wreck, which is what every other patrol vehicle does.");
            CfgCabCrew = cfg.Bind("TechnicalCrew", "CabCrew", true,
                "Show the two men in the cab. The driver is always one of them: "
                + "a truck steering itself down a road with nobody behind the "
                + "wheel is the thing this setting exists to avoid. Off leaves "
                + "the gunner alone on the bed and keeps the other two a number "
                + "until the truck is destroyed.");
            CfgGunnerFires = cfg.Bind("TechnicalCrew", "GunnerFires", true,
                "Let the gunner open fire on his own. He shoots at players and "
                + "NPCs of a HOSTILE faction only - the route's faction decides "
                + "who that is, the same table the patrol gun and the dismounted "
                + "crew read (Fraktion.Feind). Off leaves him standing at the "
                + "gun without using it.");
            CfgEngageRange = cfg.Bind("TechnicalCrew", "EngageRange", 220f,
                "How far the gunner opens fire, in metres. Well under the "
                + "machine gun's own reach (TechnicalGun/Range, 600) and under "
                + "a BTR's: a man behind a pintle sight on a moving truck is "
                + "not a stabilised autocannon, and a technical that opens up "
                + "at half a kilometre is a technical nobody can drive away "
                + "from.");
            CfgReaction = cfg.Bind("TechnicalCrew", "ReactionSeconds", 0.8f,
                "How long the gunner holds a new target in his sight before the "
                + "first round. Without it he answers in the frame a man steps "
                + "out of cover, which reads as a machine and not as a man.");
            CfgSpread = cfg.Bind("TechnicalCrew", "Spread", 1.6f,
                "Scatter of his fire in degrees. Shooting off a moving bed, so "
                + "wider than the BTR gun's. Zero makes him perfect.");
            CfgBurst = cfg.Bind("TechnicalCrew", "BurstRounds", 8,
                "Rounds in one burst before he pauses. A belt fed in bursts is "
                + "what a machine gun sounds like; continuous fire is what a "
                + "script sounds like.");
            CfgBurstPause = cfg.Bind("TechnicalCrew", "BurstPause", 1.1f,
                "Seconds between two bursts.");
            CfgTraverse = cfg.Bind("TechnicalCrew", "TraverseSpeed", 150f,
                "Degrees per second the gunner swings the gun onto a target. "
                + "Slower than a player at the same gun (TechnicalGun/TurnSpeed, "
                + "260): he is holding on to a truck with one hand.");
            CfgCabDrop = cfg.Bind("TechnicalCrew", "CabSeatDrop", 0f,
                "How far the two cab men are lowered from the seat point, in "
                + "metres. The default puts them exactly ON it, which is where "
                + "the game itself puts a passenger's root "
                + "(VehicleGameSystem::SitToPassengerPlace). The difference is "
                + "that the game then plays a SITTING clip over that root and "
                + "an NPC has no such clip in a vehicle, so he keeps a standing "
                + "pose there. This key is the correction for what that looks "
                + "like in the cab: positive sinks him, negative lifts him. It "
                + "is deliberately not a guessed number - it is zero until "
                + "somebody has looked at a technical from the outside and "
                + "measured what it wants.");
            CfgGunnerBound = cfg.Bind("TechnicalCrew", "GunnerDiesWithVehicle", true,
                "The gunner lives and dies with his truck. While the technical "
                + "is whole no round hurts him - he is part of the vehicle, "
                + "behind the plate on the pintle - and when it is destroyed he "
                + "dies with it instead of climbing down to fight on foot. When "
                + "the truck is removed he goes with it. Off makes him an "
                + "ordinary man again: he can be shot off the gun, and he walks "
                + "away from the wreck.");
        }

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        static bool CabCrew { get { return CfgCabCrew == null || CfgCabCrew.Value; } }
        static bool Shoots { get { return CfgGunnerFires == null || CfgGunnerFires.Value; } }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }

        /// <summary>The prefix of our Photon spawn key. The slash is load
        /// bearing: a ground group's key can never contain one (grounddef.py
        /// allows [A-Za-z0-9_.-] in a name and hex in the digest), which is how
        /// Revival.GroundEnemies.cs tells our men from a stale group of its
        /// own.</summary>
        const string KeyPrefix = "tech/";
        const string KeyGunner = "/g";
        const string KeyCab = "/c";

        /// <summary>Units of slack before a rider is put back on his place. A
        /// man who is where he belongs is not written to at all.</summary>
        const float Slack = 0.05f;

        /// <summary>Seconds between two passes of <see cref="Durchlassen"/>.
        /// Not per frame and not once: an ignored collider pair is forgotten
        /// when one of the two is switched off and on again, which is what the
        /// game does to every NPC no player is near, and a patrol crew is far
        /// from every player for most of its life.</summary>
        const float PassEvery = 2f;

        // --------------------------------------------------------------- data

        /// <summary>One manned technical. Everything expensive - the mount, the
        /// seat points, who the men are - is found on the shared 5 Hz vehicle
        /// scan and only READ in the frame loop.</summary>
        sealed class Truck
        {
            public Component Vgs;
            public int Id;                       // Vgs.GetInstanceID, kept for the index
            public Transform Root;
            public Transform Mount, Gun;
            public Transform Seats;              // VehicleGameSystem.SeatPoints
            public int View;                     // PhotonView id, 0 when unknown
            public string Key;                   // KeyPrefix + View
            public string Side = "neutral";      // the route's faction

            // master only
            public GameObject GunSquad, CabSquad;
            public bool Asked;
            public int Tries;
            public float NextTry;
            public bool Released;                // the men are the wreck crew now

            // every client
            public readonly List<Component> Men = new List<Component>();
            public Component Gunner;
            public readonly List<Component> Cab = new List<Component>();

            // The truck's own colliders and the clock of the pass that makes
            // its men go through them (Durchlassen). Found once, laid again and
            // again: Unity forgets an ignored pair when either collider is
            // switched off and on, and the game does that to every NPC no
            // player is standing near.
            public Collider[] Cols;
            public float NextPass;
            public int PassMen;                  // men the last pass covered
            public bool PassLogged;

            // the gun
            public float Yaw, Pitch;             // where the barrel is being sent
            public Transform Target;
            public Component TargetNpc;
            public float Held;                   // seconds this target has been held
            public float NextShot;
            public int Burst;
            public float NextLook;
            public bool Engaged;                 // a contact is running (log only)
            public float LastContact;            // Time.time a target was last held
            public int Rounds, Hits;             // of the running contact

            // Who is still standing, counted on the 5 Hz scan (Zaehlen) and
            // read by Patrol (Driverless, Wiped). A place only counts as lost
            // once a living man was seen on it: a truck whose men have not
            // arrived yet is not a truck whose men are dead.
            public int CabUp, GunUp;
            public bool CabSeen, GunSeen;

            // The gunner's own rifle, switched off while he works the MG
            // (Entwaffnen): the bone it hangs on, found once per man, and the
            // renderers this client turned off, so they can be turned back on.
            public Component HandOf;
            public Transform Hand;
            public readonly List<Renderer> Hidden = new List<Renderer>();
        }

        static readonly List<Truck> _trucks = new List<Truck>();
        static readonly Dictionary<int, Truck> _byVgs = new Dictionary<int, Truck>();
        static float _scanPhase;
        static bool _warnedMount;

        // ---------------------------------------------------------- the scan

        /// <summary>
        /// The 5 Hz pass, from Technical.Pflege - the shared vehicle scan. It
        /// keeps the truck list, finds the pivots and the seats, asks Patrol who
        /// owns each truck, and on the master spawns the men. Nothing in here
        /// runs per frame; walking a vehicle hierarchy for a mount is exactly
        /// the work that must not.
        /// </summary>
        internal static void Scan(Component[] all)
        {
            if (!Enabled) return;
            try
            {
                for (int i = _trucks.Count - 1; i >= 0; i--)
                {
                    Truck t = _trucks[i];
                    if (t.Vgs != null && t.Root != null) continue;
                    // He goes with his truck: a gunner left behind by a truck
                    // that was removed would stand in the air where the bed was.
                    if (Bound && Steht(t.Gunner) && NpcWar.GroundOwned(t.Gunner))
                        NpcWar.RemoveGroundActor(t.Gunner);
                    Verliere(t, "the vehicle is gone");
                    _byVgs.Remove(t.Id);
                    _trucks.RemoveAt(i);
                }
                if (all == null) return;

                bool master = RevivalTroopInsertion.MasterClient();
                for (int i = 0; i < all.Length; i++)
                {
                    Component vgs = all[i];
                    if (vgs == null || !Technical.IstTechnical(vgs.transform)) continue;
                    Truck t = Find(vgs);
                    if (t == null)
                    {
                        t = new Truck();
                        t.Vgs = vgs;
                        t.Id = vgs.GetInstanceID();
                        t.Root = vgs.transform;
                        // Spread the first target scan across the window so a
                        // column armed together does not all look in lockstep.
                        _scanPhase += 0.5f * 0.618034f;
                        if (_scanPhase >= 0.5f) _scanPhase -= 0.5f;
                        t.NextLook = Time.time + _scanPhase;
                        _trucks.Add(t);
                        _byVgs[t.Id] = t;
                    }
                    // The view id is asked for until it answers, not once: a
                    // vehicle is rebuilt in the same breath as it is network
                    // instantiated, and a scan that caught it half a step early
                    // would otherwise leave that truck with no key and therefore
                    // with no crew for the rest of its life.
                    if (t.View == 0)
                    {
                        t.View = ViewId(vgs.gameObject);
                        if (t.View != 0)
                            t.Key = KeyPrefix
                                + t.View.ToString(CultureInfo.InvariantCulture);
                    }
                    Teile(t);
                }

                // ONE scene walk for every truck, not one per truck:
                // FindObjectsOfType is a full scene pass and a six-vehicle column
                // would otherwise make six of them twice a second.
                //
                // It runs on the MASTER TOO, and that is not waste - it is how a
                // master who has just inherited the room finds the men another
                // machine spawned. Without it he would look at a truck he has no
                // settlement for and man it a second time, and the editor's
                // ground groups solved exactly this problem the same way.
                Zuordnen();
                for (int i = 0; i < _trucks.Count; i++)
                {
                    if (_trucks[i].Released) continue;
                    Zaehlen(_trucks[i]);
                    Entwaffnen(_trucks[i]);
                }

                // EVERY CLIENT, before the master's own work: the men and the
                // truck that carries them must not collide. The physics step
                // runs on every machine that has the hull, so the pass does
                // too - see Durchlassen for why it is repeated rather than done
                // once.
                for (int i = 0; i < _trucks.Count; i++)
                {
                    Truck t = _trucks[i];
                    if (t.Vgs == null || t.Root == null || t.Released) continue;
                    if (Time.time < t.NextPass) continue;
                    t.NextPass = Time.time + PassEvery;
                    Durchlassen(t);
                }

                if (!master) return;
                for (int i = 0; i < _trucks.Count; i++)
                    if (_trucks[i].Vgs != null) Bemannen(_trucks[i]);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalCrew, scan: " + ex.Message);
            }
        }

        static Truck Find(Component vgs)
        {
            if (vgs == null) return null;
            Truck t;
            if (_byVgs.TryGetValue(vgs.GetInstanceID(), out t)
                && t.Vgs != null && ReferenceEquals(t.Vgs, vgs)) return t;
            for (int i = 0; i < _trucks.Count; i++)
                if (ReferenceEquals(_trucks[i].Vgs, vgs)) return _trucks[i];
            return null;
        }

        /// <summary>The pivots and the seat points, found once and kept.</summary>
        static void Teile(Truck t)
        {
            if (t.Mount == null) t.Mount = Technical.MountOf(t.Root);
            if (t.Gun == null && t.Mount != null) t.Gun = t.Mount.Find(Technical.GunName);
            if (t.Seats == null)
            {
                Component vgs;
                t.Seats = Technical.FindSeatPoints(t.Root.gameObject, out vgs);
            }
            if (t.Mount == null && !_warnedMount)
            {
                _warnedMount = true;
                RevivalPlugin.L.LogWarning("TechnicalCrew: a technical without a "
                    + "gun mount - its crew rides, but nobody works the gun. The "
                    + "rebuild could not measure a place for the pintle.");
            }
        }

        // ------------------------------------------------------- the spawning

        /// <summary>
        /// MASTER ONLY. Put three men on a technical that is driving one of our
        /// routes, once it is armed. Patrol answers all three questions: whose
        /// truck it is, which side it is on, and how its men are dressed. A
        /// truck nobody owns - an admin's own, a player's - gets nobody.
        /// </summary>
        static void Bemannen(Truck t)
        {
            if (t.Released) return;
            string side = Patrol.CrewedSide(t.Vgs);
            if (side == null)
            {
                // Not ours, not armed yet, or its crew is already on the ground.
                if (t.CabSquad != null || t.GunSquad != null)
                    Verliere(t, "the vehicle is no longer crewed");
                return;
            }
            t.Side = side;
            if (t.Asked || t.CabSquad != null || t.GunSquad != null) return;
            // Already manned - by us before a reload, or by the machine that was
            // master until a moment ago. Adopted, not doubled.
            if (t.Men.Count > 0)
            {
                t.Asked = true;
                RevivalPlugin.L.LogInfo("TechnicalCrew: adopting the " + t.Men.Count
                    + " men already riding the technical with key " + t.Key
                    + " - gunner " + (t.Gunner != null) + ". Their settlements "
                    + "belong to the machine that spawned them, so when this "
                    + "truck burns they are released where they stand rather "
                    + "than handed to a squad here.");
                return;
            }
            if (Time.time < t.NextTry) return;
            if (t.Seats == null || t.Seats.childCount < Technical.SeatTotal) return;
            if (t.Key == null) return;

            int count = Patrol.CrewedCount(t.Vgs);
            if (count <= 0) return;
            count = Mathf.Clamp(count, 1, Technical.SeatTotal);

            t.Tries++;
            t.NextTry = Time.time + 4f;
            try
            {
                // THE DRIVER IS THE FIRST MAN ON, ALWAYS. A truck rolling down a
                // road with a gunner on the bed and nobody behind the wheel is
                // the one arrangement this feature must never produce, so the
                // cab is filled before the gun: one man is a driver, two are a
                // driver and a gunner, three are the full crew.
                int cab = Mathf.Max(1, count - 1);
                bool gunner = count >= 2;

                // WHERE EACH MAN STANDS, given as the caller's own points. The
                // ring Crew.Ausstiege would otherwise lay around a single drop
                // point puts half of its men ON the vehicle and in the air; the
                // artillery battery learned that in the field on 2026-09-18 and
                // the answer was the same one used here - hand it the stations.
                List<RevivalComposition.CrewMan> loadout = Patrol.CrewedList(t.Vgs);
                Vector3[] cabPosts = new Vector3[cab];
                for (int i = 0; i < cab; i++) cabPosts[i] = Platz(t, i);
                t.CabSquad = Crew.DropGroundSquad(cabPosts[0], cabPosts, t.Side,
                    Teil(loadout, 0, cab), t.Key + KeyCab);
                if (t.CabSquad == null)
                {
                    if (t.Tries >= 4)
                    {
                        t.Asked = true;
                        RevivalPlugin.L.LogWarning("TechnicalCrew: no crew could be "
                            + "spawned for the technical on " + t.Side + " after "
                            + t.Tries + " tries - that truck drives empty.");
                    }
                    return;
                }
                if (gunner)
                {
                    Vector3[] gunPost = new Vector3[] { Platz(t, Technical.GunnerSeat) };
                    t.GunSquad = Crew.DropGroundSquad(gunPost[0], gunPost, t.Side,
                        Teil(loadout, Technical.GunnerSeat, 1), t.Key + KeyGunner);
                    if (t.GunSquad == null)
                        RevivalPlugin.L.LogWarning("TechnicalCrew: the cab is manned "
                            + "but the gun is not - the machine gun on this technical "
                            + "stays silent.");
                }
                t.Asked = true;
                // Who they are is known here without waiting for the key pass,
                // so the very first frame after the spawn already holds them -
                // and the very first PHYSICS step already has them going through
                // the hull they were just put inside. A pass that waited for the
                // next scan would be two tenths of a second too late, and two
                // tenths of a second is a hundred physics steps of a truck
                // pushing itself out of its own driver.
                Sammeln(t);
                t.NextPass = Time.time + PassEvery;
                Durchlassen(t);
                RevivalPlugin.L.LogInfo("TechnicalCrew: " + t.Men.Count + " man crew ("
                    + t.Side + ") riding the technical, key " + t.Key
                    + " - gunner " + (t.Gunner != null) + ", cab " + t.Cab.Count + ".");
            }
            catch (Exception ex)
            {
                t.Asked = t.Tries >= 4;
                RevivalPlugin.L.LogWarning("TechnicalCrew: crew spawn failed: "
                    + ex.Message);
            }
        }

        /// <summary>The slice of the editor loadout that belongs to one squad -
        /// the cab's men or the gunner alone - so each man is dressed and armed
        /// as the role on HIS line in the composition says. A list shorter than
        /// the seats is not an error: Crew.Absetzen repeats it around the men,
        /// which is what dresses a whole crew from one "crew" line.</summary>
        static List<RevivalComposition.CrewMan> Teil(
            List<RevivalComposition.CrewMan> loadout, int from, int count)
        {
            if (loadout == null || loadout.Count == 0) return null;
            List<RevivalComposition.CrewMan> part =
                new List<RevivalComposition.CrewMan>();
            for (int i = 0; i < count; i++)
                part.Add(loadout[(from + i) % loadout.Count]);
            return part;
        }

        /// <summary>Master only: collect the men the two settlements built. The
        /// gunner is the single man of his own squad, so naming him costs no
        /// guess here and none on any other client either.</summary>
        static void Sammeln(Truck t)
        {
            t.Men.Clear();
            t.Cab.Clear();
            t.Gunner = null;
            Fuellen(Crew.Men(t.CabSquad), t.Cab);
            List<Component> gun = new List<Component>();
            Fuellen(Crew.Men(t.GunSquad), gun);
            if (gun.Count > 0) t.Gunner = gun[0];
            for (int i = 0; i < t.Cab.Count; i++) t.Men.Add(t.Cab[i]);
            if (t.Gunner != null) t.Men.Add(t.Gunner);
        }

        static void Fuellen(Array men, List<Component> into)
        {
            if (men == null) return;
            for (int i = 0; i < men.Length; i++)
            {
                Component ai = men.GetValue(i) as Component;
                if (ai != null) into.Add(ai);
            }
        }

        // ----------------------------------------------- who is who elsewhere

        /// <summary>
        /// EVERY OTHER CLIENT. Find the men of this truck by the key they carry
        /// in their Photon instantiation data, then name the gunner: he is the
        /// man furthest BACK in the truck's own local frame, which is where the
        /// bed is. That ordering survives the lag, because every one of the
        /// three lags by the same distance in the same direction at once.
        ///
        /// Twice a second, not per frame: it walks the scene's NPCs.
        /// </summary>
        static float _nextResolveAll;
        static readonly Dictionary<string, List<Component>> _byKey =
            new Dictionary<string, List<Component>>();

        static void Zuordnen()
        {
            if (Time.time < _nextResolveAll) return;
            _nextResolveAll = Time.time + 0.5f;

            Type npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (npcType == null) return;
            _byKey.Clear();
            UnityEngine.Object[] actors = UnityEngine.Object.FindObjectsOfType(npcType);
            for (int i = 0; i < actors.Length; i++)
            {
                Component ai = actors[i] as Component;
                if (ai == null) continue;
                string key = Crew.GroundKey(ai);
                if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    continue;
                List<Component> men;
                if (!_byKey.TryGetValue(key, out men))
                {
                    men = new List<Component>();
                    _byKey[key] = men;
                }
                men.Add(ai);
            }

            for (int i = 0; i < _trucks.Count; i++)
            {
                Truck t = _trucks[i];
                if (t.Key == null || t.Root == null) continue;
                List<Component> cab, gun;
                bool haveCab = _byKey.TryGetValue(t.Key + KeyCab, out cab)
                               && cab.Count > 0;
                bool haveGun = _byKey.TryGetValue(t.Key + KeyGunner, out gun)
                               && gun.Count > 0;
                if (!haveCab && !haveGun)
                {
                    // Nothing answers to this truck's key. On the machine that
                    // SPAWNED the men that is not an answer, it is a question
                    // the spawn data has not got round to yet - so the master
                    // keeps what it knows and tries again next pass. Anywhere
                    // else, an empty result is the truth: the men are gone.
                    if (t.CabSquad != null || t.GunSquad != null) Sammeln(t);
                    else { t.Men.Clear(); t.Cab.Clear(); t.Gunner = null; }
                    continue;
                }
                t.Men.Clear(); t.Cab.Clear(); t.Gunner = null;
                if (haveCab)
                    for (int k = 0; k < cab.Count; k++)
                    { t.Cab.Add(cab[k]); t.Men.Add(cab[k]); }
                if (haveGun)
                { t.Gunner = gun[0]; t.Men.Add(gun[0]); }
                // A man this machine has just recognized has never been let
                // through this truck, so the pass is asked for NOW - it runs in
                // the same scan, a few lines further on, and therefore still
                // before the next physics step. Only when the crew has actually
                // changed, though: this pass rebuilds its lists twice a second
                // and the ordinary refresh is on its own clock.
                if (t.Men.Count != t.PassMen) t.NextPass = 0f;
            }
        }

        // ------------------------------------------------------------- places

        /// <summary>The world place of one seat. A seat POINT is not a floor:
        /// the game's sit clip floats the whole body above the root it is given,
        /// which is why Technical measures FeetAboveSeat and why a man stood on
        /// a raw seat point sinks to the knees. The gunner is not asked for
        /// here at all - TechnicalGun.Stellung walks him around his own pintle
        /// and owns that place.</summary>
        static Vector3 Platz(Truck t, int seat)
        {
            if (t.Seats != null && seat >= 0 && seat < t.Seats.childCount)
            {
                Transform sp = t.Seats.GetChild(seat);
                if (sp != null)
                {
                    Vector3 at = sp.position;
                    if (seat != Technical.GunnerSeat)
                        at -= t.Root.up * (F(CfgCabDrop, 0f) * Einheiten(t));
                    return at;
                }
            }
            return t.Root.position;
        }

        /// <summary>Model units per metre, read back off the scale the rebuild
        /// put on the mount's base node - the same way TechnicalGun gets it. The
        /// vehicle models are not metric, so every distance this class states in
        /// metres goes through here.</summary>
        static float Einheiten(Truck t)
        {
            if (t.Mount != null && t.Mount.parent != null)
            {
                float s = t.Mount.parent.localScale.x;
                if (s > 0.0001f) return s;
            }
            return 1f;
        }

        /// <summary>The donor's length in world units. 4.03 m is the UAZ-3151,
        /// the one real-world number RevivalTechnical.cs carries.</summary>
        static float Laenge(Truck t)
        {
            return 4.03f * Einheiten(t);
        }

        // ------------------------------------------- through their own truck

        /// <summary>
        /// THE MEN AND THE TRUCK THAT CARRIES THEM MUST NOT TOUCH.
        ///
        /// A rider stands INSIDE the hull he rides: the two in the cab are in
        /// the cabin, the gunner is on the bed. Neither side knows about the
        /// arrangement. The truck is a driven rigidbody; a man is a main
        /// collider plus a full set of ragdoll bones (Revival.Crew.cs, on
        /// SetPhysActive). So the hull spends every physics step pushing itself
        /// out of three bodies that the next LateUpdate puts straight back where
        /// they were, and the field report of 2026-09-22 describes both ends of
        /// that: a truck shoved off its road and finally off the ground, and men
        /// who tip over, come off the bed and are run over by their own vehicle.
        ///
        /// The answer is the one Revival.Patrol.cs already gives for the world a
        /// patrol drives through: Physics.IgnoreCollision between THIS truck and
        /// THESE men. Local, exact, and it costs the riders nothing that matters
        /// - a raycast is not a collision, so every bullet still hits them and
        /// every headshot still counts - while they stay solid for the world,
        /// for other vehicles and for each other.
        ///
        /// Both colliders have to be switched on for Unity to accept the pair,
        /// and the pair is forgotten again when either of them is switched off
        /// and on, which is exactly what the game's distance optimization does
        /// to a crew no player is near. That is why this is a pass on a clock
        /// (<see cref="PassEvery"/>) and not a one-off at the spawn.
        /// </summary>
        static void Durchlassen(Truck t)
        {
            if (t.Root == null || t.Men.Count == 0) return;
            try
            {
                if (t.Cols == null || t.Cols.Length == 0 || t.Cols[0] == null)
                    t.Cols = t.Root.GetComponentsInChildren<Collider>(true);
                if (t.Cols.Length == 0) return;

                int pairs = 0;
                for (int m = 0; m < t.Men.Count; m++)
                {
                    Component ai = t.Men[m];
                    if (ai == null) continue;
                    Parken(ai);
                    Collider[] seine = ai.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < seine.Length; i++)
                    {
                        Collider a = seine[i];
                        // TRIGGERS ARE LEFT ALONE, on both sides. A zone volume
                        // is not a collision, and an ignored pair costs the game
                        // the event it fires - the same rule the patrol's own
                        // hull sweep keeps (QueryTriggerInteraction.Ignore).
                        if (a == null || a.isTrigger || !a.enabled
                            || !a.gameObject.activeInHierarchy) continue;
                        for (int j = 0; j < t.Cols.Length; j++)
                        {
                            Collider b = t.Cols[j];
                            if (b == null || b.isTrigger || !b.enabled
                                || !b.gameObject.activeInHierarchy) continue;
                            try { Physics.IgnoreCollision(a, b, true); pairs++; }
                            catch { /* one bad pair must not stop the rest */ }
                        }
                    }
                }
                t.PassMen = t.Men.Count;
                if (pairs > 0 && !t.PassLogged)
                {
                    t.PassLogged = true;
                    RevivalPlugin.L.LogInfo("TechnicalCrew: the " + t.Men.Count
                        + " men of the technical with key " + t.Key + " go through "
                        + "its own hull now (" + pairs + " collider pair(s) over "
                        + t.Cols.Length + " of the truck's). They stay solid for "
                        + "everything else, and a bullet is a ray and not a "
                        + "collision, so they can still be shot off it.");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalCrew, letting the crew "
                    + "through the truck: " + ex.Message);
            }
        }

        /// <summary>
        /// Take the man's own navigation off his transform while he rides.
        ///
        /// A NavMeshAgent with updatePosition writes the body to ITS position
        /// every simulation step, and its position is wherever the agent last
        /// walked to - which for a man on a moving truck is the road behind it.
        /// Putting him back in LateUpdate does not settle that argument; it only
        /// decides who wrote last, and the other writer is still moving him
        /// between the frames the physics step sees. Warping the agent instead
        /// is what <see cref="Setzen"/> does on a real jump, but a truck is a
        /// fraction of a metre further on in EVERY frame and an agent warped
        /// sixty times a second is sixty NavMesh queries for a man who is not
        /// walking anywhere.
        ///
        /// So while he rides, the agent simulates and keeps quiet, and
        /// <see cref="Absteigen"/> hands the transform back to it - at the place
        /// the man actually stands - the moment he stops being a rider.
        /// </summary>
        static void Parken(Component ai)
        {
            NavMeshAgent agent = Agent(ai);
            if (agent == null) return;
            try
            {
                if (agent.updatePosition) agent.updatePosition = false;
                if (agent.updateRotation) agent.updateRotation = false;
            }
            catch { }
        }

        /// <summary>The other half of <see cref="Parken"/>: the agent drives the
        /// body again, starting from where the body actually is. Without the
        /// warp it would carry him back to wherever it was simulating while he
        /// rode, which is the far end of the route.</summary>
        static void Absteigen(Truck t)
        {
            Bewaffnen(t);
            for (int i = 0; i < t.Men.Count; i++)
            {
                Component ai = t.Men[i];
                if (ai == null) continue;
                NavMeshAgent agent = Agent(ai);
                if (agent == null) continue;
                try
                {
                    agent.updatePosition = true;
                    agent.updateRotation = true;
                    if (agent.isActiveAndEnabled)
                        agent.Warp(ai.transform.position);
                }
                catch { }
            }
        }

        // -------------------------------------------------------- every frame

        /// <summary>
        /// The frame pass, from Technical.LateFrame - and it MUST be a late one.
        /// The game's animator writes a character's root between Update and
        /// LateUpdate, so a man placed in Update is back where the animation put
        /// him before anything is drawn. It runs on EVERY client: this is the
        /// half that makes the crew ride the truck instead of trailing it.
        ///
        /// Ordered before TechnicalGun.LateAll on purpose - LateAll's Stellung
        /// puts the gunner behind the pintle and solves his hands onto the
        /// grips, and it has to see the gun where this pass has just aimed it.
        /// </summary>
        internal static void LateFrame()
        {
            if (!Enabled) return;
            try
            {
                bool master = RevivalTroopInsertion.MasterClient();
                for (int i = 0; i < _trucks.Count; i++)
                {
                    Truck t = _trucks[i];
                    if (t.Vgs == null || t.Root == null || t.Released) continue;
                    if (master) Zielen(t);
                    Halten(t);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalCrew, frame: " + ex.Message);
            }
        }

        /// <summary>The two men in the cab, put where the truck is right now.
        /// The gunner is left to TechnicalGun.Stellung, which walks him around
        /// the pintle instead of nailing him to a point.
        ///
        /// A DEAD MAN IS LET GO. Crew.Men hands back the settlement's whole
        /// list, alive or dead, and holding a corpse on a seat point would drive
        /// a ragdoll down the road with its own physics fighting the write every
        /// frame. He falls off the truck, which is what a shot driver does.</summary>
        static void Halten(Truck t)
        {
            // THE GUNNER IS PLACED ELSEWHERE, BUT HE IS HELD HERE. Stellung
            // writes him a position and a heading and knows nothing about
            // NPC_AI2, so nothing ever held the game's idle logic off the one
            // man who is standing up in the open. He queued his own walk
            // intentions all the way down the road and stepped off the bed in
            // the first frame that did not overwrite him.
            if (Lebt(t.Gunner)) Ruhig(t.Gunner);

            if (!CabCrew) return;
            // Upright on the truck's heading, SEATED as well: verify.py rule 12
            // holds the field report of 2026-09-22 ("the NPCs in it fall over").
            // On a slope that leaves a seated man a few degrees off his seat,
            // which is the smaller fault of the two.
            Quaternion aufrecht = Aufrecht(t);
            int seat = 0;
            for (int i = 0; i < t.Cab.Count && seat < Technical.SeatTotal - 1; i++)
            {
                Component ai = t.Cab[i];
                if (ai == null || !Lebt(ai)) continue;
                Setzen(t, ai, Platz(t, seat), aufrecht);
                Sitzen(ai, seat);
                seat++;
            }
        }

        /// <summary>The rotation a rider is given: the truck's HEADING, and
        /// nothing else of its attitude.
        ///
        /// A man is not cargo. Writing the hull's full rotation onto him lays
        /// him over with every slope, kerb and bump the truck takes, and the
        /// whole cab lies down at once the moment the truck tips - which is the
        /// "the NPCs in it fall over while it drives" of the field report of
        /// 2026-09-22. The gunner has always been treated this way
        /// (TechnicalGun.Stellung flattens the mount's forward and turns him
        /// with LookRotation(dir, Vector3.up)); the cab simply never was.</summary>
        static Quaternion Aufrecht(Truck t)
        {
            Vector3 dir = t.Root.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.000001f)
                return Quaternion.Euler(0f, t.Root.eulerAngles.y, 0f);
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        /// <summary>Is this man still on his feet? NpcWar owns the answer - it
        /// is the same test the ground squads and the patrol gun use, so a man
        /// counts as dead here exactly when he counts as dead there.</summary>
        static bool Lebt(Component ai)
        {
            return ai != null && ai.gameObject.activeInHierarchy
                && NpcWar.GroundAlive(ai);
        }

        /// <summary>Is this man still alive at all - the question Patrol's
        /// driver needs, which is not <see cref="Lebt"/>'s. The game switches
        /// off every NPC no player is near, and a crew switched off far down
        /// the road is asleep, not dead: a truck that stopped for that would
        /// stop wherever nobody is watching it. A destroyed body is dead.</summary>
        static bool Steht(Component ai)
        {
            return ai != null && NpcWar.GroundAlive(ai);
        }

        /// <summary>Count the living men in the cab and at the gun.</summary>
        static void Zaehlen(Truck t)
        {
            int cab = 0;
            for (int i = 0; i < t.Cab.Count; i++)
                if (Steht(t.Cab[i])) cab++;
            int gun = Steht(t.Gunner) ? 1 : 0;
            t.CabUp = cab;
            t.GunUp = gun;
            if (cab > 0) t.CabSeen = true;
            if (gun > 0) t.GunSeen = true;
        }

        /// <summary>
        /// Patrol seam. Nobody alive is left in the cab of this technical -
        /// the truck must stop where it is. A truck steering itself down the
        /// road with nobody behind the wheel is the one arrangement the whole
        /// riding crew exists to avoid (CfgCabCrew), and until 6.44.1 it was
        /// exactly what a shot-up crew left behind. The gunner, if he still
        /// lives, keeps working the gun from the standing truck.
        /// </summary>
        internal static bool Driverless(Component vgs)
        {
            if (!Enabled || vgs == null) return false;
            Truck t = Find(vgs);
            return t != null && !t.Released && t.CabSeen && t.CabUp == 0;
        }

        /// <summary>Patrol seam. Every man of this technical is dead: the
        /// truck is abandoned, not destroyed. Patrol stops it for good and
        /// spawns no wreck crew for it - its crew is the dead on the road.
        /// With GunnerDiesWithVehicle on, a truck that has a gunner never gets
        /// here while it is whole: he cannot be shot, so a dead cab only
        /// stops it (Driverless) and he fights on from the standing truck.</summary>
        internal static bool Wiped(Component vgs)
        {
            if (!Enabled || vgs == null) return false;
            Truck t = Find(vgs);
            return t != null && !t.Released && t.CabSeen && t.CabUp == 0
                && t.GunUp == 0;
        }

        // ------------------------------------------ one with the vehicle

        static bool Bound { get { return CfgGunnerBound == null || CfgGunnerBound.Value; } }

        /// <summary>
        /// The gunner lives and dies with his truck (field request 2026-09-22,
        /// after the half-damage plate of c8828a2: "der Schuetze lebt und
        /// stirbt mit dem Fahrzeug"). A prefix on NPC_AI2.ApplyDamage skips the
        /// whole call for a RIDING gunner - players' rounds, NPC fire and blasts
        /// alike, and with it the hit animation and the wounded state, which
        /// would pull him off the pintle. ReleaseRiders kills him when the truck
        /// is destroyed; Scan removes him when the truck is removed.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            if (!Enabled) return;
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo apply = npc == null ? null
                    : AccessTools.Method(npc, "ApplyDamage", null, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("TechnicalCrew: NPC_AI2.ApplyDamage not "
                        + "found - the technical's gunner can be shot off his gun.");
                    return;
                }
                harmony.Patch(apply, new HarmonyMethod(typeof(TechnicalCrew).GetMethod(
                    "BoundPrefix", BindingFlags.Public | BindingFlags.Static)),
                    null, null, null, null);
                RevivalPlugin.L.LogInfo("TechnicalCrew: the gunner lives and dies with "
                    + "his technical (GunnerDiesWithVehicle " + Bound + ").");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TechnicalCrew: gunner guard not installed - " + ex);
            }
        }

        /// <summary>Prefix on NPC_AI2.ApplyDamage: false - the original does not
        /// run - for the gunner of a technical that is still whole.</summary>
        public static bool BoundPrefix(object __instance)
        {
            if (_trucks.Count == 0 || !Bound) return true;
            try
            {
                for (int i = 0; i < _trucks.Count; i++)
                {
                    Truck t = _trucks[i];
                    if (t.Released || t.Gunner == null) continue;
                    if (ReferenceEquals(t.Gunner, __instance)) return false;
                }
            }
            catch { }
            return true;
        }

        /// <summary>The gunner dies with the truck. Called by ReleaseRiders
        /// after Released is set, so BoundPrefix lets this one round through.
        /// ApplyDamage only takes health off on the owner - the machine that
        /// spawned him, which is the one whose Patrol saw the truck destroyed;
        /// a crew adopted after a master handover is owned elsewhere and keeps
        /// its gunner, which the log then says.</summary>
        static bool Toeten(Component ai)
        {
            if (!Steht(ai)) return false;
            try { Turret.TryDamage(ai.gameObject, "NPC_AI2", "ApplyDamage", 100000f); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TechnicalCrew: the gunner could not be "
                    + "killed with his truck - " + ex.Message);
            }
            return !Steht(ai);
        }

        // ------------------------------------------------ the rifle put away

        static FieldInfo _fWm, _fHelper;
        static Type _fWmOwner, _fHelperOwner;

        /// <summary>
        /// The gunner's own rifle is not shown while he works the machine gun:
        /// his hands are on its grips (TechnicalGun), and the rifle the game
        /// hung on his right hand stuck out beside them. The model lives under
        /// NPC_WeaponsManager.Weapons_HelperR (REVERSE_ENGINEERING "The weapon
        /// draw aborts silently on a missing model":
        /// NetworkShowWeapon instantiates `*_Weapon` there), so its renderers
        /// are switched off - on EVERY client, locally, with no RPC and no
        /// remove clip over the standing pose. Re-applied on the 5 Hz scan,
        /// because a redraw instantiates a fresh model. Absteigen turns them
        /// back on.
        /// </summary>
        static void Entwaffnen(Truck t)
        {
            if (t.Gunner == null || !Steht(t.Gunner)) return;
            if (!ReferenceEquals(t.HandOf, t.Gunner))
            {
                Bewaffnen(t);
                t.HandOf = t.Gunner;
                t.Hand = WeaponHand(t.Gunner);
            }
            if (t.Hand == null) return;
            Renderer[] rs = t.Hand.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                if (!t.Hidden.Contains(r)) t.Hidden.Add(r);
            }
        }

        static void Bewaffnen(Truck t)
        {
            for (int i = 0; i < t.Hidden.Count; i++)
                if (t.Hidden[i] != null) t.Hidden[i].enabled = true;
            t.Hidden.Clear();
            t.HandOf = null;
            t.Hand = null;
        }

        static Transform WeaponHand(Component ai)
        {
            try
            {
                Type at = ai.GetType();
                if (!ReferenceEquals(at, _fWmOwner))
                {
                    _fWmOwner = at;
                    _fWm = AccessTools.Field(at, "_weaponsManager");
                }
                Component wm = _fWm == null ? null : _fWm.GetValue(ai) as Component;
                if (wm != null)
                {
                    Type wt = wm.GetType();
                    if (!ReferenceEquals(wt, _fHelperOwner))
                    {
                        _fHelperOwner = wt;
                        _fHelper = AccessTools.Field(wt, "Weapons_HelperR");
                    }
                    object v = _fHelper == null ? null : _fHelper.GetValue(wm);
                    Transform h = v as Transform;
                    if (h == null && v is GameObject) h = ((GameObject)v).transform;
                    if (h != null) return h;
                }
            }
            catch { }
            return Suche(ai.transform, "Weapons_HelperR");
        }

        static Transform Suche(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = Suche(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// One man, on one place, this frame. The agent is warped and its path
        /// reset rather than the transform alone being written: a NavMeshAgent
        /// holds a path of its own and would drag him back off the truck along
        /// it. The game's idle logic is held off him for the same reason
        /// ArtyBattery holds it off a posted man - a short pause refreshed every
        /// pass is the whole hold.
        /// </summary>
        static void Setzen(Truck t, Component ai, Vector3 at, Quaternion rot)
        {
            Transform tr = ai.transform;
            float away = (tr.position - at).sqrMagnitude;
            if (away <= Slack * Slack) { tr.rotation = rot; return; }

            // THE AGENT IS WARPED ONLY ON A REAL JUMP. A man riding a truck is
            // a fraction of a metre out of place in every single frame, and
            // warping an agent sixty times a second is a NavMesh query sixty
            // times a second for a man who is not walking anywhere. Beyond a
            // truck's own length he genuinely has to be carried - he has just
            // been spawned, or the truck teleported - and then the agent must
            // come with him, or it drags him back along a path of its own.
            float jump = Laenge(t);
            if (away > jump * jump)
            {
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
            }
            tr.position = at;
            tr.rotation = rot;
            Ruhig(ai);
        }

        static FieldInfo _fAgent;
        static Type _fAgentOwner;

        static NavMeshAgent Agent(Component ai)
        {
            if (ai == null) return null;
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _fAgentOwner))
            {
                _fAgentOwner = t;
                // `_navAgent` is the CONFIRMED name (REVERSE_ENGINEERING: the
                // NavMeshAgent field of NPC_AI2), and it is what NpcWar and the
                // artillery battery read. The other spelling stays as a second
                // try, and the GetComponent below still answers when neither
                // field exists.
                _fAgent = AccessTools.Field(t, "_navAgent");
                if (_fAgent == null) _fAgent = AccessTools.Field(t, "_navMeshAgent");
            }
            try
            {
                NavMeshAgent a = _fAgent == null ? null
                    : _fAgent.GetValue(ai) as NavMeshAgent;
                return a != null ? a : ai.GetComponent<NavMeshAgent>();
            }
            catch { return ai.GetComponent<NavMeshAgent>(); }
        }

        static MethodInfo _mClearIntentions, _mPauseTime;
        static Type _mQuietOwner;

        /// <summary>Keep the vanilla idle logic off a man who is riding.
        /// NPC_AI2.IdleStateAction queues its own intentions on every idle pass
        /// and returns early while GetCalculatedPauseTime is positive, so a
        /// short pause refreshed every frame is the whole hold. It is the same
        /// one ArtyBattery uses on a posted crewman and NpcWar on a man held in
        /// an aim pose.</summary>
        static void Ruhig(Component ai)
        {
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _mQuietOwner))
            {
                _mQuietOwner = t;
                _mClearIntentions = AccessTools.Method(t, "ClearIntentions", null, null);
                _mPauseTime = AccessTools.Method(t, "SetCalculatedPauseTime",
                                                 new Type[] { typeof(float) }, null);
                if (_mPauseTime == null)
                    _mPauseTime = AccessTools.Method(t, "SetPauseTime",
                                                     new Type[] { typeof(float) }, null);
            }
            try
            {
                if (_mClearIntentions != null) _mClearIntentions.Invoke(ai, null);
                if (_mPauseTime != null) _mPauseTime.Invoke(ai, new object[] { 1.4f });
            }
            catch { }
        }

        // ------------------------------------------------------ sitting down

        /// <summary>The game's own passenger clips: what a PLAYER in the
        /// driver's and in any other seat of this very truck is posed with.
        /// They live in the 495-clip Animation of the player model
        /// (Male_01_v78/v80, Male_UKB_v70); the NPC model's 96-clip set has
        /// neither, but it has the bench sit, which is the fallback.</summary>
        const string ClipDriver = "vehicle_driver_idle";
        const string ClipPassenger = "vehicle_passenger_idle";
        const string ClipBench = "npc_seat_bench";

        static UnityEngine.Object _clipDriver, _clipPassenger, _clipBench;
        static float _nextClipSearch;
        static int _clipSearches;
        static bool _clipsLogged;
        static MethodInfo _sample;
        static PropertyInfo _clipLength;
        static FieldInfo _fAnim;
        static Type _fAnimOwner;
        static readonly object[] _sampleArgs = new object[2];

        /// <summary>
        /// Pose one cab man SITTING, this frame, after the game's animation ran.
        ///
        /// Up to 6.43.0 he kept the NPC's own pose - standing - on a seat point,
        /// and a seat point is not a cushion: the game's sit clips bend the legs
        /// under hips that stay where a standing man's are, so the ROOT sits
        /// near the floor of the cab (UAZ seat root y -0.17, REVERSE_ENGINEERING
        /// "How far a seated body reaches above its seat root"). A standing man
        /// on that root has his legs through the floor and his head in the
        /// roof.
        ///
        /// The clips are sampled onto the NPC's own legacy Animation object
        /// (NPC_AI2.Anim), the same late-frame trick TechnicalGun.StandingPose
        /// plays on a gunner. It works across the two models because every one
        /// of them binds by the same paths - MainChar_Skeleton/MainChar_Based/...
        /// - and the hips sit at the same place in all five clips measured
        /// (idle_alert, npc_idle_alert_01, npc_seat_bench, vehicle_driver_idle,
        /// vehicle_passenger_idle: MainChar_Hips within 0.04 of the origin), so
        /// no clip needs an offset of its own. Every client runs it, because
        /// every client draws the man.
        /// </summary>
        static void Sitzen(Component ai, int seat)
        {
            try
            {
                if (!SitClips()) return;
                UnityEngine.Object clip = seat == 0 ? _clipDriver : _clipPassenger;
                if (clip == null) clip = _clipBench;
                if (clip == null) return;
                GameObject model = Modell(ai);
                if (model == null) return;

                float laenge = 0f;
                if (_clipLength != null)
                {
                    object v = _clipLength.GetValue(clip, null);
                    if (v is float) laenge = (float)v;
                }
                // Each man on his own phase of the loop: two men breathing in
                // step read as one animation copied twice.
                float t = laenge > 0.01f
                    ? Mathf.Repeat(Time.time + (ai.GetInstanceID() & 255) * 0.13f, laenge)
                    : 0f;
                _sampleArgs[0] = model;
                _sampleArgs[1] = t;
                _sample.Invoke(clip, _sampleArgs);
            }
            catch { }
        }

        /// <summary>Find the three clips among everything loaded, once. They are
        /// looked for again every ten seconds while the driver's is missing:
        /// the player model that carries it is loaded with the local player,
        /// and a crew can be spawned before he is.</summary>
        static bool SitClips()
        {
            if (_clipDriver != null && _sample != null) return true;
            // A minute of looking is enough: past that the bench sit is the
            // answer for this session, and a scene-wide clip walk every ten
            // seconds would be a cost with no prospect of a result.
            if (Time.time < _nextClipSearch || (_clipSearches >= 6 && _clipBench != null))
                return _sample != null && _clipBench != null;
            _nextClipSearch = Time.time + 10f;
            _clipSearches++;

            Type clipType = RevivalPlugin.TypeByName("UnityEngine.AnimationClip");
            if (clipType == null) return false;
            if (_sample == null)
            {
                _sample = clipType.GetMethod("SampleAnimation",
                    new Type[] { typeof(GameObject), typeof(float) });
                _clipLength = clipType.GetProperty("length");
            }
            if (_sample == null) return false;

            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(clipType);
            for (int i = 0; i < all.Length; i++)
            {
                UnityEngine.Object c = all[i];
                if (c == null) continue;
                string name = c.name;
                if (_clipDriver == null && name == ClipDriver) _clipDriver = c;
                else if (_clipPassenger == null && name == ClipPassenger) _clipPassenger = c;
                else if (_clipBench == null && name == ClipBench) _clipBench = c;
            }
            if (!_clipsLogged && (_clipDriver != null || _clipBench != null))
            {
                _clipsLogged = true;
                RevivalPlugin.L.LogInfo("TechnicalCrew: the cab men sit - driver "
                    + (_clipDriver != null ? ClipDriver : ClipBench) + ", co-driver "
                    + (_clipPassenger != null ? ClipPassenger : ClipBench)
                    + ", sampled after the game's animation every frame.");
            }
            return _clipDriver != null || _clipBench != null;
        }

        /// <summary>The object the man's legacy Animation sits on - the one the
        /// clip paths start from. NPC_AI2.Anim is the CONFIRMED field
        /// (REVERSE_ENGINEERING, AutoDisableControl: Anim.Stop()).</summary>
        static GameObject Modell(Component ai)
        {
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _fAnimOwner))
            {
                _fAnimOwner = t;
                _fAnim = AccessTools.Field(t, "Anim");
            }
            if (_fAnim == null) return null;
            Component anim = _fAnim.GetValue(ai) as Component;
            return anim == null ? null : anim.gameObject;
        }

        // ---------------------------------------------------------- the gun

        /// <summary>The body the station code should treat as this truck's
        /// gunner when no player sits in the gunner's place. TechnicalGun asks,
        /// so its own Stellung, Hands and SlewRemote work on an NPC exactly as
        /// they already work on a remote player.</summary>
        internal static GameObject GunnerBody(Component vgs)
        {
            if (!Enabled) return null;
            Truck t = Find(vgs);
            if (t == null || t.Released || !Lebt(t.Gunner)) return null;
            return t.Gunner.gameObject;
        }

        /// <summary>True on the machine that is LAYING this gun itself - the
        /// master of a technical with a live NPC gunner. TechnicalGun.SlewRemote
        /// asks so it does not read the bearing back off the man we have just
        /// turned; on every other machine this is false and that slew is exactly
        /// what reproduces the aim.</summary>
        internal static bool Aiming(Component vgs)
        {
            if (!Enabled || !Shoots) return false;
            Truck t = Find(vgs);
            if (t == null || t.Released || t.Mount == null || !Lebt(t.Gunner))
                return false;
            return RevivalTroopInsertion.MasterClient();
        }

        /// <summary>Is this body one of the men riding that truck? Asked where a
        /// distance test would otherwise disown a rider whose replicated
        /// position is trailing the vehicle.</summary>
        internal static bool IsRider(Component vgs, GameObject body)
        {
            if (!Enabled || body == null) return false;
            Truck t = Find(vgs);
            if (t == null) return false;
            for (int i = 0; i < t.Men.Count; i++)
                if (t.Men[i] != null && ReferenceEquals(t.Men[i].gameObject, body))
                    return true;
            return false;
        }

        /// <summary>
        /// MASTER ONLY. Pick a target, lay the gun on it and fire.
        ///
        /// The GUN is written here and the GUNNER'S BODY is turned to match, not
        /// the other way round: the body's rotation is what crosses the wire, so
        /// every other client's TechnicalGun.SlewRemote reproduces the same
        /// bearing from it without a message of our own. The body is kept
        /// UPRIGHT - a man tilted to his gun's elevation reads as a bug - which
        /// is why elevation is the one thing that does not travel.
        /// </summary>
        static void Zielen(Truck t)
        {
            if (t.Mount == null || !Shoots) return;
            // A gun whose gunner is dead is a gun that stops. It is left lying
            // wherever he left it: recentring it would be the truck tidying up
            // after the man who was shot off it.
            if (!Lebt(t.Gunner))
            {
                if (t.Engaged) Abbrechen(t);
                t.Target = null;
                t.Held = 0f;
                return;
            }
            // A PLAYER in the gunner's place owns this mount (GunnerBody: a
            // player always wins). Now that the crew lays the gun in quiet
            // spells too, laying it under a player would be a fight over the
            // same pivot in every frame.
            if (!TechnicalGun.NpcManned(t.Vgs)) return;
            if (Time.time >= t.NextLook)
            {
                t.NextLook = Time.time + 0.5f;
                Suchen(t);
            }
            if (t.Target == null || !Feind(t, t.Target, t.TargetNpc))
            {
                // Three quiet seconds end a contact, not one lost sight ray: a
                // man ducking behind a fence is the same fight.
                if (t.Engaged && Time.time - t.LastContact > 3f) Abbrechen(t);
                t.Target = null;
                t.TargetNpc = null;
                t.Held = 0f;
                Ruhelage(t);
                return;
            }
            t.LastContact = Time.time;
            if (!t.Engaged)
            {
                t.Engaged = true;
                t.Rounds = 0;
                t.Hits = 0;
                RevivalPlugin.L.LogInfo("TechnicalCrew: the gunner of " + t.Key
                    + " (" + t.Side + ") engages "
                    + (t.TargetNpc != null
                        ? "an NPC (" + NpcWar.PatrolFaction(t.TargetNpc) + ")"
                        : "a player")
                    + " at " + Vector3.Distance(t.Target.position, t.Root.position)
                        .ToString("0", CultureInfo.InvariantCulture) + " m.");
            }

            Vector3 muzzle = Muendung(t);
            Vector3 to = t.Target.position + Vector3.up * 0.9f - muzzle;
            if (to.sqrMagnitude < 0.0001f) return;
            Vector3 dir = to.normalized;

            // The bearing in the MOUNT'S PARENT frame, which is the frame the
            // pintle's base put the vehicle's own up axis into, so zero really
            // is straight ahead however the chassis node is rotated.
            Transform frame = t.Mount.parent;
            if (frame == null) return;
            Vector3 local = frame.InverseTransformDirection(dir);
            if (local.sqrMagnitude < 0.000001f) return;
            local.Normalize();
            float wantPitch = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 flat = new Vector3(local.x, 0f, local.z);
            if (flat.sqrMagnitude < 0.000001f) return;
            float wantYaw = Quaternion.LookRotation(flat.normalized, Vector3.up).eulerAngles.y;

            float step = Mathf.Max(1f, F(CfgTraverse, 150f)) * Time.deltaTime;
            t.Mount.localRotation = Quaternion.RotateTowards(t.Mount.localRotation,
                Quaternion.Euler(0f, wantYaw, 0f), step);
            float min = TechnicalGun.CfgPitchMin == null ? -12f : TechnicalGun.CfgPitchMin.Value;
            float max = TechnicalGun.CfgPitchMax == null ? 55f : TechnicalGun.CfgPitchMax.Value;
            wantPitch = Mathf.Clamp(wantPitch, min, max);
            if (t.Gun != null)
                t.Gun.localRotation = Quaternion.RotateTowards(t.Gun.localRotation,
                    Quaternion.Euler(-wantPitch, 0f, 0f), step);

            // The man follows his gun, flat. TechnicalGun.Stellung then puts him
            // on the far side of the pintle from the muzzle, so he is behind the
            // weapon he is facing.
            Vector3 face = t.Mount.forward;
            face.y = 0f;
            if (face.sqrMagnitude > 0.000001f && t.Gunner != null)
                t.Gunner.transform.rotation =
                    Quaternion.LookRotation(face.normalized, Vector3.up);

            t.Held += Time.deltaTime;
            if (t.Held < Mathf.Max(0f, F(CfgReaction, 0.8f))) return;
            if (Time.time < t.NextShot) return;

            // Laid on? The barrel itself answers, not the wish above it.
            Vector3 bore = t.Gun == null ? t.Mount.forward : t.Gun.forward;
            if (Vector3.Angle(bore, dir) > 3.5f) return;
            Feuern(t, muzzle, bore);
        }

        /// <summary>The line that says how a contact ended. Without it the log
        /// could not tell a gunner who fired and missed from one who never
        /// fired, and the 6.43.0 field log is exactly that silence: a technical
        /// destroyed on R5 with nothing written about its gun at all.</summary>
        static void Abbrechen(Truck t)
        {
            t.Engaged = false;
            RevivalPlugin.L.LogInfo("TechnicalCrew: the gunner of " + t.Key
                + " lost his target - " + t.Rounds + " round(s), " + t.Hits
                + " hit(s).");
        }

        /// <summary>
        /// MASTER ONLY. No target: the man stays WITH HIS GUN.
        ///
        /// Before, nothing turned him while there was nobody to shoot at, so he
        /// faced wherever NPC_AI2 last turned him - at the player walking past,
        /// at the road behind - and TechnicalGun.Stellung, which stands him
        /// behind the pintle, then solved his hands onto grips beside or behind
        /// him. That is a man who is at the MG by position only. Here he faces
        /// the barrel every frame, and after a few quiet seconds the barrel
        /// comes back to straight ahead at half traverse speed, which is where a
        /// gunner on a moving truck watches. The mount and the gun are built with
        /// identity rotation as their rest (TechnicalGun.Aim), so identity is
        /// "ahead and level".
        /// </summary>
        static void Ruhelage(Truck t)
        {
            if (Time.time - t.LastContact > 3f)
            {
                float step = Mathf.Max(1f, F(CfgTraverse, 150f)) * 0.5f * Time.deltaTime;
                t.Mount.localRotation = Quaternion.RotateTowards(t.Mount.localRotation,
                    Quaternion.identity, step);
                if (t.Gun != null)
                    t.Gun.localRotation = Quaternion.RotateTowards(t.Gun.localRotation,
                        Quaternion.identity, step);
            }
            Vector3 face = t.Mount.forward;
            face.y = 0f;
            if (face.sqrMagnitude > 0.000001f && t.Gunner != null)
                t.Gunner.transform.rotation =
                    Quaternion.LookRotation(face.normalized, Vector3.up);
        }

        static Vector3 Muendung(Truck t)
        {
            if (t.Gun != null) return t.Gun.TransformPoint(TechnicalModel.MuzzleLocal);
            return t.Mount == null ? t.Root.position : t.Mount.position;
        }

        /// <summary>
        /// One round out of the barrel. Deliberately NOT TechnicalGun.Fire: that
        /// one shoots down the player's camera axis because his crosshair must
        /// not lie, and an NPC has no camera. This one starts at the muzzle and
        /// follows the bore, which is the honest line for a gun nobody is
        /// looking through.
        ///
        /// No VehicleArmor hit, for the same reason the player's machine gun
        /// does not take one: this is an anti-personnel weapon and armour is as
        /// unaffected by it as by rifle fire.
        /// </summary>
        static void Feuern(Truck t, Vector3 muzzle, Vector3 bore)
        {
            float pause = TechnicalGun.CfgDelay == null ? 0.11f : TechnicalGun.CfgDelay.Value;
            t.Burst++;
            int burst = CfgBurst == null ? 8 : Mathf.Max(1, CfgBurst.Value);
            if (t.Burst >= burst)
            {
                t.Burst = 0;
                t.NextShot = Time.time + Mathf.Max(pause, F(CfgBurstPause, 1.1f));
            }
            else t.NextShot = Time.time + pause;

            Vector3 dir = Streuen(bore, F(CfgSpread, 1.6f));
            float range = TechnicalGun.CfgRange == null ? 600f : TechnicalGun.CfgRange.Value;

            VehicleShotSound.PlayTechnical(muzzle);
            Turret.Net.PublishTechnicalShot(muzzle);

            Vector3 impact;
            GameObject struck = Strahl(t, muzzle, dir, range, out impact);
            Vector3 ende = struck == null ? muzzle + dir * range : impact;
            Spur(muzzle, ende);
            t.Rounds++;
            if (struck == null) return;

            float damage = TechnicalGun.CfgDamage == null ? 85f : TechnicalGun.CfgDamage.Value;
            if (Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)) { t.Hits++; return; }
            if (Turret.TryDamage(struck, "Animal_AI", "NetworkApplyDamage", damage)) { t.Hits++; return; }
            if (Turret.TryDamage(struck, "PlayerNetworkController", "PlayerApplyDamage", damage))
                t.Hits++;
        }

        static Vector3 Streuen(Vector3 dir, float degrees)
        {
            if (degrees <= 0f) return dir.normalized;
            Vector3 up = Mathf.Abs(dir.y) > 0.95f ? Vector3.forward : Vector3.up;
            Vector3 right = Vector3.Cross(up, dir).normalized;
            Vector3 over = Vector3.Cross(dir, right).normalized;
            float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float r = Mathf.Tan(UnityEngine.Random.Range(0f, degrees) * Mathf.Deg2Rad);
            return (dir.normalized + right * (Mathf.Cos(a) * r)
                                   + over * (Mathf.Sin(a) * r)).normalized;
        }

        /// <summary>The ray, stepped past our own truck and past our own men: a
        /// muzzle a hand's breadth in front of the gunner's chest would
        /// otherwise put every burst into his back.</summary>
        static GameObject Strahl(Truck t, Vector3 from, Vector3 dir, float range,
                                 out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 start = from;
            float rest = range;
            for (int step = 0; step < 5 && rest > 0f; step++)
            {
                Vector3 hit;
                GameObject go = Turret.RaycastObject(start, dir, rest, out hit);
                if (go == null) return null;
                if (!Eigen(t, go)) { point = hit; return go; }
                rest -= Vector3.Distance(start, hit) + 0.25f;
                start = hit + dir * 0.25f;
            }
            return null;
        }

        static bool Eigen(Truck t, GameObject go)
        {
            if (go == null) return false;
            Transform tr = go.transform;
            while (tr != null)
            {
                if (tr == t.Root) return true;
                for (int i = 0; i < t.Men.Count; i++)
                    if (t.Men[i] != null && tr == t.Men[i].transform) return true;
                tr = tr.parent;
            }
            return false;
        }

        static void Spur(Vector3 von, Vector3 bis)
        {
            try
            {
                float tempo = TechnicalGun.CfgTracerSpeed == null
                    ? 700f : TechnicalGun.CfgTracerSpeed.Value;
                float laenge = TechnicalGun.CfgTracerLength == null
                    ? 6f : TechnicalGun.CfgTracerLength.Value;
                TechnicalTracerStreak.Spawn(von, bis, tempo, laenge * 1.6f,
                    0.24f, 0.10f, new Color(1.00f, 0.38f, 0.10f, 1f),
                    new Color(1.00f, 0.38f, 0.10f, 1f));
                TechnicalTracerStreak.Spawn(von, bis, tempo, laenge, 0.11f, 0.04f,
                    new Color(1.00f, 0.96f, 0.78f, 1f),
                    new Color(1.00f, 0.62f, 0.20f, 1f));
            }
            catch { }
        }

        // ------------------------------------------------------------ targets

        /// <summary>
        /// Who this gun may shoot at: a living player or NPC of a HOSTILE
        /// faction, inside EngageRange, that the barrel can actually see. The
        /// faction table is Fraktion's - the same one the patrol gun, the
        /// dismounted crew and the NPC squads read - so "different faction"
        /// means here what it means everywhere else in the toolkit.
        ///
        /// Vehicles are NOT targets. The machine gun does no armour damage by
        /// design (TechnicalGun.Fire says so), so shooting at a hull would be a
        /// gunner emptying belts into steel for nothing.
        /// </summary>
        /// <summary>The nearest hostiles, cheapest test first, and a SIGHT RAY
        /// FOR AT MOST THREE OF THEM. Distance and faction cost arithmetic; a
        /// ray costs the physics scene, and a road with thirty NPCs on it would
        /// otherwise cast thirty rays per truck twice a second. The patrol gun
        /// makes the same trade for the same reason: it casts for one candidate,
        /// not for all of them.</summary>
        const int SightTries = 3;

        static readonly List<Transform> _candTr = new List<Transform>();
        static readonly List<Component> _candNpc = new List<Component>();
        static readonly List<float> _candD = new List<float>();

        static void Suchen(Truck t)
        {
            Vector3 muzzle = Muendung(t);
            float reach = Mathf.Max(10f, F(CfgEngageRange, 220f));
            _candTr.Clear(); _candNpc.Clear(); _candD.Clear();

            List<Component> npcs = NpcWar.PatrolTargets();
            for (int i = 0; i < npcs.Count; i++)
            {
                Component npc = npcs[i];
                if (npc == null) continue;
                Transform tr = npc.transform;
                float d = Vector3.Distance(tr.position, muzzle);
                if (d > reach) continue;
                if (!Feind(t, tr, npc)) continue;
                Merken(tr, npc, d);
            }

            List<GameObject> players = Spieler();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null) continue;
                Transform tr = go.transform;
                float d = Vector3.Distance(tr.position, muzzle);
                if (d > reach) continue;
                if (!Feind(t, tr, null)) continue;
                Merken(tr, null, d);
            }

            Transform bestTr = null;
            Component bestNpc = null;
            for (int i = 0; i < _candTr.Count && i < SightTries; i++)
            {
                if (!Sicht(t, muzzle, _candTr[i])) continue;
                bestTr = _candTr[i];
                bestNpc = _candNpc[i];
                break;
            }

            if (bestTr != t.Target) t.Held = 0f;
            t.Target = bestTr;
            t.TargetNpc = bestNpc;
        }

        /// <summary>Keep the nearest few candidates, nearest first. An insertion
        /// into a list of three beats sorting the whole road.</summary>
        static void Merken(Transform tr, Component npc, float d)
        {
            int at = _candD.Count;
            for (int i = 0; i < _candD.Count; i++)
                if (d < _candD[i]) { at = i; break; }
            if (at >= SightTries) return;
            _candD.Insert(at, d);
            _candTr.Insert(at, tr);
            _candNpc.Insert(at, npc);
            while (_candD.Count > SightTries)
            {
                _candD.RemoveAt(_candD.Count - 1);
                _candTr.RemoveAt(_candTr.Count - 1);
                _candNpc.RemoveAt(_candNpc.Count - 1);
            }
        }

        static bool Feind(Truck t, Transform tr, Component npc)
        {
            if (tr == null || !tr.gameObject.activeInHierarchy) return false;
            if (tr.IsChildOf(t.Root)) return false;
            for (int i = 0; i < t.Men.Count; i++)
                if (t.Men[i] != null && (tr == t.Men[i].transform
                                         || tr.IsChildOf(t.Men[i].transform)))
                    return false;
            if (npc != null)
            {
                if (!NpcWar.PatrolTarget(npc)) return false;
                return Fraktion.Feind(t.Side, NpcWar.PatrolFaction(npc));
            }
            return Fraktion.Feind(t.Side, Fraktion.Spielerseite(tr.gameObject));
        }

        /// <summary>Is the target actually reachable from the muzzle? Asked
        /// before a target is taken, not only before a round, so the gunner does
        /// not swing onto a man behind a wall and sit there aiming at masonry.</summary>
        static bool Sicht(Truck t, Vector3 muzzle, Transform target)
        {
            Vector3 aim = target.position + Vector3.up * 0.9f;
            Vector3 dir = aim - muzzle;
            float range = dir.magnitude;
            if (range < 0.5f) return true;
            Vector3 hit;
            GameObject struck = Strahl(t, muzzle, dir / range, range + 1f, out hit);
            if (struck == null) return false;
            Transform tr = struck.transform;
            while (tr != null)
            {
                if (tr == target) return true;
                tr = tr.parent;
            }
            // Close enough to the man counts: a rifle in his hands or the bag on
            // his back is a separate object and the ray stops on it.
            return Vector3.Distance(hit, aim) < 1.2f;
        }

        static readonly List<GameObject> _players = new List<GameObject>();
        static float _nextPlayers;
        static Type _ngs;
        static PropertyInfo _ngsInstance;
        static FieldInfo _ngsPlayers;
        static bool _ngsLooked;

        static List<GameObject> Spieler()
        {
            if (Time.time < _nextPlayers) return _players;
            _nextPlayers = Time.time + 0.5f;
            _players.Clear();
            try
            {
                if (!_ngsLooked)
                {
                    _ngsLooked = true;
                    _ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                    if (_ngs != null)
                    {
                        _ngsInstance = _ngs.GetProperty("Instance",
                            BindingFlags.Public | BindingFlags.Static);
                        _ngsPlayers = AccessTools.Field(_ngs, "NetworkPlayers");
                    }
                    if (_ngsInstance == null || _ngsPlayers == null)
                        RevivalPlugin.L.LogWarning("TechnicalCrew: NetworkGameServer"
                            + ".Instance or .NetworkPlayers is missing - the gunner "
                            + "sees NPCs only.");
                }
                if (_ngsInstance == null || _ngsPlayers == null) return _players;
                object server = _ngsInstance.GetValue(null, null);
                if (server == null) return _players;
                System.Collections.IList list = _ngsPlayers.GetValue(server)
                    as System.Collections.IList;
                if (list == null) return _players;
                for (int i = 0; i < list.Count; i++)
                {
                    GameObject go = list[i] as GameObject;
                    if (go != null) _players.Add(go);
                }
            }
            catch { }
            return _players;
        }

        // ------------------------------------------------------ the wreck

        /// <summary>
        /// Patrol seam. The truck is destroyed and Patrol is about to put its
        /// crew on the ground - but this crew is ALREADY on the ground, in the
        /// uniform the editor chose, standing on a truck that has just stopped.
        /// They are handed to NpcWar as an ordinary ground squad instead of
        /// being replaced, and true is returned so Patrol spawns nobody.
        ///
        /// False when this vehicle has no riders, which is every vehicle that is
        /// not a manned technical - then Patrol does what it always did.
        /// </summary>
        internal static bool ReleaseRiders(GameObject car, string side)
        {
            if (!Enabled || car == null) return false;
            Truck t = null;
            for (int i = 0; i < _trucks.Count; i++)
                if (_trucks[i].Root != null && _trucks[i].Root == car.transform)
                { t = _trucks[i]; break; }
            if (t == null || t.Released || t.Men.Count == 0) return false;

            t.Released = true;
            // They stop being riders here: their own navigation gets the body
            // back before NpcWar is asked to make them fight on foot.
            Absteigen(t);
            List<RevivalComposition.CrewMan> loadout = Patrol.CrewedList(t.Vgs);
            int handed = 0;
            if (Abgeben(t, t.CabSquad, "cab", loadout)) handed++;
            // The gunner lives and dies with the truck: he goes down with it
            // at the gun instead of climbing off to fight on foot.
            string gunner = "no gunner";
            if (Bound && t.Gunner != null)
                gunner = Toeten(t.Gunner) ? "the gunner died with it"
                                          : "the gunner could not be killed here";
            else if (Abgeben(t, t.GunSquad, "gun", loadout)) handed++;
            RevivalPlugin.L.LogInfo("TechnicalCrew: the technical on " + side
                + " is destroyed - " + gunner + ", " + handed + " squad(s) of its "
                + t.Men.Count + " men fighting on foot.");
            return true;
        }

        /// <summary>Hand one of the truck's two squads to NPC combat, so the men
        /// who were riding a minute ago take cover and shoot back instead of
        /// standing where the bed used to be.</summary>
        static bool Abgeben(Truck t, GameObject settlement, string what,
                            List<RevivalComposition.CrewMan> loadout)
        {
            if (settlement == null) return false;
            Array men = Crew.Men(settlement);
            if (men == null) return false;
            if (NpcWar.StartGround("technical-" + what + "-" + settlement.GetInstanceID(),
                    settlement, men, t.Root.position, false, 0f, loadout))
                return true;
            RevivalPlugin.L.LogWarning("TechnicalCrew: the " + what + " crew of the "
                + "wrecked technical could not be handed to NPC combat; they stay "
                + "as ordinary settlement NPCs.");
            return false;
        }

        /// <summary>This truck is no longer ours. On the master the men are let
        /// go rather than destroyed: they were spawned over the network and are
        /// now ordinary NPCs, which is exactly what the crew of any other
        /// abandoned patrol vehicle becomes.</summary>
        static void Verliere(Truck t, string why)
        {
            // Whatever the reason, they are not riding anything any more: the
            // agent that was parked while they rode gets the body back, or they
            // would stand rooted wherever the truck left them.
            Absteigen(t);
            if (t.CabSquad != null || t.GunSquad != null)
            {
                Crew.Forget(t.CabSquad);
                Crew.Forget(t.GunSquad);
                RevivalPlugin.L.LogInfo("TechnicalCrew: giving up a technical's crew - "
                    + why + ".");
            }
            t.CabSquad = null;
            t.GunSquad = null;
            t.Men.Clear();
            t.Cab.Clear();
            t.Gunner = null;
        }

        /// <summary>Everything goes when the scene does. Called from
        /// Technical.Tick's scene guard, the same place the rest of the feature
        /// gives up its per-scene state.</summary>
        internal static void StopAll()
        {
            for (int i = 0; i < _trucks.Count; i++)
            {
                Truck t = _trucks[i];
                t.CabSquad = null;
                t.GunSquad = null;
                t.Men.Clear();
                t.Cab.Clear();
                t.Gunner = null;
            }
            _trucks.Clear();
            _byVgs.Clear();
        }

        // --------------------------------------------------------------- misc

        static Type _viewType;
        static MethodInfo _viewId;
        static bool _viewLooked;

        static int ViewId(GameObject go)
        {
            if (go == null) return 0;
            try
            {
                if (!_viewLooked)
                {
                    _viewLooked = true;
                    _viewType = RevivalPlugin.TypeByName("PhotonView");
                    if (_viewType != null)
                        _viewId = AccessTools.PropertyGetter(_viewType, "viewID");
                }
                if (_viewType == null || _viewId == null) return 0;
                Component view = go.GetComponent(_viewType);
                if (view == null) return 0;
                object v = _viewId.Invoke(view, null);
                return v == null ? 0 : (int)v;
            }
            catch { return 0; }
        }
    }
}

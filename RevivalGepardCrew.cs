// Next Day: Survival - Revival Toolkit
//
// THE GEPARD AS AN NPC VEHICLE: its crew, its gunner and the aircraft it hunts.
//
// A Gepard on an editor route (compdef "gepard", RevivalComposition asks the
// VehicleRegistry) is driven by Patrol like every other column vehicle. This
// file is what makes it an ANTI-AIRCRAFT gun there and not a BTR with a
// different body:
//
//   1  THE CREW RIDES. Three men - driver, gunner, commander, the real crew
//      and the three seats RevivalGepard.cs builds under the turret ring - are
//      spawned by the master once Patrol has armed the vehicle, exactly the way
//      RevivalTechnicalCrew.cs mans a technical: Crew.DropGroundSquad with a
//      KEY in the Photon instantiation data, "gep/<view id>/g" for the gunner
//      (a squad of one, so he is still the gunner after a master handover) and
//      "gep/<view id>/c" for the other two. THE SLASH IS LOAD BEARING:
//      Revival.GroundEnemies.cs reconciles every keyed NPC without one and
//      would delete these men as a stale ground group.
//      The same two rules as the technical hold: nothing is ever written into
//      VehicleGameSystem.Passengers (SetDamageToAllPassengers throws on an NPC
//      entry) and nothing is parented - every client puts the men on their
//      seat points from the hull's own transform in LateUpdate. Unlike the
//      technical the Gepard is a closed hull: the men are hidden while they
//      ride, cannot be hurt through the armour, and climb out beside the wreck
//      as its wreck crew (Patrol.UnloadCrew -> ReleaseRiders) instead of a
//      second crew being spawned there.
//   2  THE GUNNER FIGHTS AIR FIRST. On the master a living gunner runs the
//      fire control of every Gepard he sits in: the search radar reports every
//      aircraft in AirRange - the player-flown Mi-8s (PlayerHeli) and every
//      aircraft registered with GepardAir (the An-2) - and an aircraft with a
//      player of a hostile faction aboard is ALWAYS taken before anything on
//      the ground. Only with the sky clear does he look for hostile players and
//      NPCs within GroundRange. The guns are laid through GepardGun.Intercept
//      (lead and superelevation), fired as real GepardShots rounds whose
//      damage is judged against this gunner's own contacts, and the turret pose
//      and trigger go to every other client on the Gepard's own event
//      (GepardNet.SendPose) - so the others see the turret swing and the
//      tracers fly with no new network message.
//   3  GEPARDAIR. A tiny registry: any aircraft of this plugin can hand in a
//      list of its airframes and a "shot down" call, and both the NPC gunner
//      and a player's radar then track and kill it. The An-2 lives on another
//      branch (Revival.PlayerAn2.cs) and is bound by reflection when present:
//      its `_all` list, `Burning(GameObject)` and `Crash(GameObject, Vector3)`,
//      which burns it on every client through its own Crashed event. If that
//      shape changes the probe logs it and the An-2 is simply not a target.
//
// The stationary "AA site" is not in here: it is a route option (`hold` in the
// first waypoint's flags, the editor's "Hold position" box), and Patrol keeps
// such a vehicle braked on its spawn point for its whole life. This gunner
// does not care whether the hull moves.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only, no BOM.
//
// SEAMS OUTSIDE THIS FILE (all marked there):
//   RevivalGepard.cs        Gepard.Tick -> GepardCrew.Tick; GepardShots.Fire
//                           with a contact list; GepardGun.Proximity / Struck /
//                           Hit overloads; contact kind 6 (GepardAir).
//   RevivalPlugin.cs        BindConfig / Install / LateUpdate.
//   Revival.Patrol.cs       UnloadCrew -> ReleaseRiders, StopAll, Gun.Collect
//                           leaves the donor's hidden BTR turret alone, the
//                           route option `hold`.
//   Revival.GroundEnemies.cs  the slash guard that leaves our keys alone.

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
    // =====================================================================
    // Aircraft the Gepard may shoot at, beyond the Mi-8

    /// <summary>
    /// Aircraft of this plugin that are not PlayerHeli: each registers a list
    /// of its airframes and the call that brings one down. Contact kind 6 in
    /// GepardGun, read by a player's radar and by the NPC gunner alike.
    /// </summary>
    internal static class GepardAir
    {
        internal sealed class Source
        {
            public string Name;
            public Action<List<GameObject>> List;
            public Action<GameObject, Vector3> Kill;
            public Func<GameObject, bool> Down;     // burning or falling; null = never
            public int Hits;                        // hits it survives; 0 = Gepard/HeliHits
        }

        internal struct Found
        {
            public GameObject Go;
            public Source Src;
        }

        static readonly List<Source> _sources = new List<Source>();
        static readonly Dictionary<int, float> _hits = new Dictionary<int, float>();
        static readonly List<GameObject> _tmp = new List<GameObject>();
        static bool _probed;

        /// <summary>Register an aircraft kind. A second registration under the
        /// same name replaces the first.</summary>
        internal static void Register(string name, Action<List<GameObject>> list,
            Action<GameObject, Vector3> kill, Func<GameObject, bool> down, int hits)
        {
            if (name == null || list == null || kill == null) return;
            for (int i = _sources.Count - 1; i >= 0; i--)
                if (_sources[i].Name == name) _sources.RemoveAt(i);
            Source s = new Source();
            s.Name = name; s.List = list; s.Kill = kill; s.Down = down; s.Hits = hits;
            _sources.Add(s);
            RevivalPlugin.L.LogInfo("GepardAir: " + name + " registered as an air target.");
        }

        internal static void Collect(List<Found> into)
        {
            Probe();
            for (int i = 0; i < _sources.Count; i++)
            {
                Source s = _sources[i];
                _tmp.Clear();
                try { s.List(_tmp); }
                catch { continue; }
                for (int k = 0; k < _tmp.Count; k++)
                {
                    GameObject go = _tmp[k];
                    if (go == null || !go.activeInHierarchy || IsDown(s, go)) continue;
                    Found f;
                    f.Go = go;
                    f.Src = s;
                    into.Add(f);
                }
            }
        }

        static bool IsDown(Source s, GameObject go)
        {
            if (s.Down == null) return false;
            try { return s.Down(go); }
            catch { return false; }
        }

        internal static bool Alive(GepardGun.Contact c)
        {
            if (c == null || c.Go == null || !c.Go.activeInHierarchy || c.Src == null) return false;
            return !IsDown(c.Src, c.Go);
        }

        /// <summary>One hit on a registered aircraft, by whoever fired the
        /// round on this machine. The kill is the aircraft's own call, which
        /// is responsible for showing it on every client.</summary>
        internal static void Hit(GepardGun.Contact c, Vector3 point)
        {
            Hit(c, point, 0);
        }

        /// <summary><paramref name="gunHits"/> &gt; 0: the hits this aircraft
        /// takes from the firing gun when its source names none (the ZU-23,
        /// Revival.Flak.cs); 0 = [Gepard] HeliHits.</summary>
        internal static void Hit(GepardGun.Contact c, Vector3 point, int gunHits)
        {
            if (!Alive(c)) return;
            int id = c.Go.GetInstanceID();
            float n;
            _hits.TryGetValue(id, out n);
            int need = c.Src.Hits > 0 ? c.Src.Hits : gunHits > 0 ? gunHits
                : Mathf.Max(1, Gepard.CfgHeliHits == null ? 10 : Gepard.CfgHeliHits.Value);
            // W AA4: an NPC aeroplane's hit points live in one ledger on the
            // master, so rifle rounds, flak and the Gepard add up and persist.
            if (NpcAircraft.Is(c.Go))
            {
                NpcAircraft.Damage(c.Go, point, AirKillCore.GunHitDamage(need), AirKills.Credit());
                return;
            }
            n = ShortRangeCore.AddHit(n, need);
            if (n < 0.99999f) { _hits[id] = n; return; }
            _hits.Remove(id);
            try
            {
                AircraftCrashFx.MarkKill(c.Go, "GepardAir.Hit/" + c.Src.Name);
                c.Src.Kill(c.Go, point);
                RevivalPlugin.L.LogInfo("GepardAir: " + c.Src.Name + " shot down (airframe damage "
                    + n + ").");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GepardAir: " + c.Src.Name + " could not be "
                    + "shot down - " + ex.Message);
            }
        }

        /// <summary>A weapon that destroys a registered aircraft in one hit (the
        /// Stinger, an NPC flyover's lethal small-arms count): the owning
        /// source's own kill, whatever its hit count.</summary>
        internal static bool Kill(GameObject go, Vector3 point)
        {
            if (go == null) return false;
            // W AA4: through the NPC aeroplane's ledger (the master decides
            // and pays the bounty), which ends in KillNow.
            if (NpcAircraft.Is(go))
            {
                if (PlayerAn2.Down(go)) return false;
                NpcAircraft.Damage(go, point, 1f, AirKills.Credit());
                return true;
            }
            return KillNow(go, point);
        }

        // Player rifles and native blasts use the same registered destruction
        // entry as flak/Stinger. NPC fixed-wing damage keeps its master ledger.
        internal static void WeaponHit(GameObject go, Vector3 point, bool lethal)
        {
            if (go == null) return;
            if (NpcAircraft.Is(go)) { NpcAircraft.Damage(go, point, lethal); return; }
            if (PlayerHeli.Contains(go))
            {
                int view = PlayerHeli.MissileView(go);
                if (view == 0 || PlayerHeli.MissileTarget(view) == null) return;
                int id = go.GetInstanceID();
                float hits;
                _hits.TryGetValue(id, out hits);
                hits = lethal ? 1f : ShortRangeCore.AddHit(hits, 30);
                if (hits < 0.99999f) { _hits[id] = hits; return; }
                _hits.Remove(id);
                AircraftCrashFx.MarkKill(go, lethal ? "player-blast/Mi-8" : "player-rifle/Mi-8");
                GepardNet.SendHeliKill(view, point);
                PlayerHeli.MissileImpact(view, point);
                return;
            }
            Probe();
            for (int i = 0; i < _sources.Count; i++)
            {
                Source s = _sources[i];
                _tmp.Clear(); s.List(_tmp);
                if (!_tmp.Contains(go) || IsDown(s, go)) continue;
                if (lethal) KillNow(go, point);
                else
                {
                    GepardGun.Contact c = new GepardGun.Contact();
                    c.Go = go; c.Src = s;
                    Hit(c, point, 30);
                }
                return;
            }
        }

        /// <summary>The owning source's own kill, at once.</summary>
        internal static bool KillNow(GameObject go, Vector3 point)
        {
            if (go == null) return false;
            Probe();
            for (int i = 0; i < _sources.Count; i++)
            {
                Source s = _sources[i];
                _tmp.Clear();
                try { s.List(_tmp); }
                catch { continue; }
                if (!_tmp.Contains(go) || IsDown(s, go)) continue;
                _hits.Remove(go.GetInstanceID());
                try
                {
                    AircraftCrashFx.MarkKill(go, "GepardAir.KillNow/" + s.Name);
                    s.Kill(go, point);
                    RevivalPlugin.L.LogInfo("GepardAir: " + s.Name + " destroyed outright.");
                    return true;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("GepardAir: " + s.Name + " could not be "
                        + "destroyed - " + ex.Message);
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// The An-2 (Revival.PlayerAn2.cs, another branch at the time of
        /// writing) is bound by reflection when the class is in this assembly:
        /// its airframe list `_all` and the networked ShotDown/Crash call.
        /// Down-state collection uses the typed PlayerAn2.Down delegate. Once,
        /// and never again if the shape is not there.
        /// </summary>
        static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                Type t = typeof(GepardAir).Assembly.GetType("NextDayRevival.PlayerAn2");
                if (t == null) return;
                FieldInfo all = AccessTools.Field(t, "_all");
                // ShotDown: an aeroplane hit in the air falls under its smoke
                // with the crew aboard; Crash (older builds) burns it in place.
                MethodInfo crash = AccessTools.Method(t, "ShotDown",
                    new Type[] { typeof(GameObject), typeof(Vector3) }, null)
                    ?? AccessTools.Method(t, "Crash",
                    new Type[] { typeof(GameObject), typeof(Vector3) }, null);
                List<GameObject> list = all == null ? null : all.GetValue(null) as List<GameObject>;
                if (list == null || crash == null)
                {
                    RevivalPlugin.L.LogWarning("GepardAir: PlayerAn2 is present but its "
                        + "_all list or Crash(GameObject, Vector3) is not - the An-2 is "
                        + "not an air target for the Gepard.");
                    return;
                }
                Register("An-2",
                    delegate(List<GameObject> into)
                    {
                        // NPC carriers have their own explicit source on every client.
                        for (int i = 0; i < list.Count; i++)
                            if (list[i] != null && !NpcAircraft.Is(list[i])) into.Add(list[i]);
                    },
                    delegate(GameObject go, Vector3 at) { crash.Invoke(null, new object[] { go, at }); },
                    PlayerAn2.Down,
                    0);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GepardAir: An-2 probe failed - " + ex.Message);
            }
        }
    }

    // =====================================================================
    // The crew and the NPC gunner

    /// <summary>
    /// The three men inside a Gepard that drives an editor route, and the
    /// master's fire control that their gunner runs.
    /// </summary>
    public static class GepardCrew
    {
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<bool> CfgGunnerFires;
        public static ConfigEntry<bool> CfgGroundTargets;
        public static ConfigEntry<bool> CfgHideCrew;
        public static ConfigEntry<float> CfgAirRange;
        public static ConfigEntry<float> CfgGroundRange;
        public static ConfigEntry<float> CfgReaction;
        public static ConfigEntry<float> CfgBurst;
        public static ConfigEntry<float> CfgBurstPause;
        public static ConfigEntry<float> CfgAirInitialError, CfgAirWalk, CfgAirErrorFloor, CfgAirEvade;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "GepardCrew";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "Man a Gepard that drives a convoy or patrol route with three NPCs "
                + "(driver, gunner, commander), dressed and armed from the route "
                + "editor like every other column vehicle. They ride inside the "
                + "hull and climb out beside the wreck. Off: the Gepard carries "
                + "its crew as a number, like a BTR, and its gun still fires.");
            CfgGunnerFires = cfg.Bind(S, "GunnerFires", true,
                "Let the NPC gunner work the twin 35 mm guns. Aircraft first: a "
                + "helicopter or aeroplane with a player of a hostile faction "
                + "aboard is always engaged before anything on the ground.");
            CfgGroundTargets = cfg.Bind(S, "GroundTargets", true,
                "With the sky clear, also engage hostile players and NPCs on the "
                + "ground. Off makes a pure anti-aircraft gun.");
            CfgHideCrew = cfg.Bind(S, "HideCrew", true,
                "Hide the riding men. They stand on seat points under the turret "
                + "ring without a seated pose, and a closed hull shows nobody.");
            CfgAirRange = cfg.Bind(S, "AirRange", 1400f,
                "How far the NPC gunner engages aircraft, world units.");
            CfgGroundRange = cfg.Bind(S, "GroundRange", 700f,
                "How far the NPC gunner engages ground targets, world units.");
            CfgReaction = cfg.Bind(S, "ReactionSeconds", 1.2f,
                "Seconds a new target is tracked before the first burst.");
            CfgBurst = cfg.Bind(S, "BurstSeconds", 0.6f,
                "Length of one burst, seconds (at Gepard/RateOfFire).");
            CfgBurstPause = cfg.Bind(S, "BurstPause", 1.0f,
                "Seconds between two bursts.");
            CfgAirInitialError = cfg.Bind(S, "AirInitialError", 30f,
                "Air targets: the first burst's aiming error, milliradians of range. The "
                + "rounds burst at the laid range as black puffs, so the pilot sees them.");
            CfgAirWalk = cfg.Bind(S, "AirWalkFactor", 0.55f,
                "Air targets: the part of the error left after each burst's correction "
                + "(0..1) - the puffs walk in burst by burst.");
            CfgAirErrorFloor = cfg.Bind(S, "AirErrorFloor", 4f,
                "Air targets: the error the gunner never gets under, milliradians of range.");
            CfgAirEvade = cfg.Bind(S, "AirEvadeFactor", 0.9f,
                "Air targets: how much a change of the aircraft's velocity since the last "
                + "burst throws the correction off (0 = evading does not help).");
        }

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        static bool Fires { get { return CfgGunnerFires == null || CfgGunnerFires.Value; } }
        static bool HideCrew { get { return CfgHideCrew == null || CfgHideCrew.Value; } }
        static float F(ConfigEntry<float> c, float fallback) { return c == null ? fallback : c.Value; }

        /// <summary>The prefix of our Photon spawn key. The slash keeps
        /// Revival.GroundEnemies.cs from deleting these men as a stale ground
        /// group (a ground group key can never contain one).</summary>
        const string KeyPrefix = "gep/";
        const string KeyGunner = "/g";
        const string KeyCrew = "/c";

        /// <summary>Seat indices of the two men who are not the gunner.</summary>
        static readonly int[] CrewSeats = new int[] { Gepard.DriverSeat, 2 };

        sealed class Hull
        {
            public Component Vgs;
            public int Id;
            public Transform Root;
            public Transform Seats;
            public GepardRig Rig;
            public int View;
            public string Key;
            public string Side = "neutral";

            // master only
            public GameObject CrewSquad, GunSquad;
            public bool Asked;
            public int Tries;
            public float NextTry;
            public bool Released;
            public float DeadSince;              // Time.time the hull was first seen destroyed

            // every client
            public bool Freed;                   // the men have climbed out; never held again
            public readonly List<Component> Men = new List<Component>();
            public readonly List<Component> Crew = new List<Component>();
            public Component Gunner;
            public Collider[] Cols;
            public float NextPass;
            public int PassMen;
            public readonly List<Renderer> Hidden = new List<Renderer>();

            // the gun (master only)
            public bool Laying;
            public readonly List<GepardGun.Contact> Air = new List<GepardGun.Contact>();
            public GepardGun.Contact AirTarget;
            public Transform Ground;
            public Component GroundNpc;
            public float NextLook, Held, BurstUntil, PauseUntil, NextShot, NextPublish, LastContact;
            public int GunIdx, Rounds;
            public bool Firing, SentFiring, Engaged;
            // Q2: the air gunner's walking-in error (world units, added to the
            // intercept), for which target, and the fuze range it lays at.
            public GepardGun.Contact ErrFor;
            public Vector3 AirErr, AirLastVel;
            public float AirFuze;
        }

        static readonly List<Hull> _hulls = new List<Hull>();
        static float _nextScan;
        static float _phase;

        // ---------------------------------------------------------- the frame

        /// <summary>From Gepard.Tick, every client: the 5 Hz scan, and on the
        /// master the fire control of every manned Gepard.</summary>
        internal static void Tick()
        {
            try
            {
                if (Time.time >= _nextScan)
                {
                    _nextScan = Time.time + 0.2f;
                    Scan(VehicleScan.All());
                }
                if (_hulls.Count == 0 || !RevivalTroopInsertion.MasterClient()) return;
                for (int i = 0; i < _hulls.Count; i++)
                {
                    Hull h = _hulls[i];
                    if (h.Vgs == null || h.Root == null) continue;
                    Gun(h, Time.deltaTime);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GepardCrew: " + ex.Message);
            }
        }

        /// <summary>From RevivalPlugin.LateUpdate, every client: the men are
        /// put on their seats after the game's animator has written them.</summary>
        internal static void LateFrame()
        {
            if (!Enabled || _hulls.Count == 0) return;
            try
            {
                for (int i = 0; i < _hulls.Count; i++)
                {
                    Hull h = _hulls[i];
                    if (h.Vgs == null || h.Root == null || Out(h)) continue;
                    Halten(h);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GepardCrew, frame: " + ex.Message);
            }
        }

        // ---------------------------------------------------------- the scan

        static void Scan(Component[] all)
        {
            for (int i = _hulls.Count - 1; i >= 0; i--)
            {
                Hull h = _hulls[i];
                if (h.Vgs != null && h.Root != null) continue;
                // Removed, not destroyed: the men inside go with it - they
                // would stand in the air where the hull was.
                if (!h.Freed) Verliere(h, "the vehicle is gone");
                _hulls.RemoveAt(i);
            }
            if (all == null) return;

            for (int i = 0; i < all.Length; i++)
            {
                Component vgs = all[i];
                if (vgs == null || !Gepard.IstGepard(vgs.transform)) continue;
                Hull h = Find(vgs);
                if (h == null)
                {
                    h = new Hull();
                    h.Vgs = vgs;
                    h.Id = vgs.GetInstanceID();
                    h.Root = vgs.transform;
                    _phase += 0.5f * 0.618034f;
                    if (_phase >= 0.5f) _phase -= 0.5f;
                    h.NextLook = Time.time + _phase;
                    _hulls.Add(h);
                }
                if (h.View == 0)
                {
                    h.View = ViewId(vgs.gameObject);
                    if (h.View != 0)
                        h.Key = KeyPrefix + h.View.ToString(CultureInfo.InvariantCulture);
                }
                if (h.Seats == null)
                {
                    Component unused;
                    h.Seats = Gepard.FindSeatPoints(h.Root.gameObject, out unused);
                }
                if (h.Rig == null) h.Rig = Gepard.Rig(h.Root);
            }
            if (!Enabled || _hulls.Count == 0) return;

            Zuordnen();
            bool master = RevivalTroopInsertion.MasterClient();
            for (int i = 0; i < _hulls.Count; i++)
            {
                Hull h = _hulls[i];
                if (h.Vgs == null || h.Root == null) continue;
                if (Out(h))
                {
                    // The master waits for Patrol.UnloadCrew to hand the men
                    // over (ReleaseRiders); should that never come, he lets
                    // them out himself. Everybody else just stops holding them.
                    if (!master) Aussteigen(h);
                    else if (!h.Released)
                    {
                        if (h.DeadSince <= 0f) h.DeadSince = Time.time;
                        else if (Time.time - h.DeadSince > 8f)
                            ReleaseRiders(h.Root.gameObject, h.Side);
                    }
                    continue;
                }
                Verstecken(h);
                if (Time.time >= h.NextPass)
                {
                    h.NextPass = Time.time + 2f;
                    Durchlassen(h);
                }
                if (master) Bemannen(h);
            }
        }

        static Hull Find(Component vgs)
        {
            if (vgs == null) return null;
            for (int i = 0; i < _hulls.Count; i++)
                if (ReferenceEquals(_hulls[i].Vgs, vgs)) return _hulls[i];
            return null;
        }

        static Hull FindRoot(Transform root)
        {
            if (root == null) return null;
            for (int i = 0; i < _hulls.Count; i++)
                if (_hulls[i].Root == root) return _hulls[i];
            return null;
        }

        /// <summary>Are the men no longer riders? Released by the master at
        /// the wreck - or, on every other client, the hull is simply destroyed,
        /// which every client sees for itself.</summary>
        static bool Out(Hull h)
        {
            if (h.Released) return true;
            object d = Field(h.Vgs, "Durability");
            return d is float && (float)d <= 0f;
        }

        // ------------------------------------------------------ the spawning

        /// <summary>MASTER ONLY. Three men on a Gepard that Patrol has armed
        /// on one of its routes; nobody on an admin's or a player's own.</summary>
        static void Bemannen(Hull h)
        {
            string side = Patrol.CrewedSide(h.Vgs);
            if (side == null)
            {
                if (h.CrewSquad != null || h.GunSquad != null)
                    Verliere(h, "the vehicle is no longer crewed");
                return;
            }
            h.Side = side;
            if (h.Asked || h.CrewSquad != null || h.GunSquad != null) return;
            if (h.Men.Count > 0)
            {
                h.Asked = true;
                RevivalPlugin.L.LogInfo("GepardCrew: adopting the " + h.Men.Count
                    + " men already inside the Gepard with key " + h.Key + ".");
                return;
            }
            if (Time.time < h.NextTry || h.Key == null) return;
            if (h.Seats == null || h.Seats.childCount < Gepard.SeatTotal) return;
            int count = Patrol.CrewedCount(h.Vgs);
            if (count <= 0) return;
            count = Mathf.Clamp(count, 1, Gepard.SeatTotal);

            h.Tries++;
            h.NextTry = Time.time + 4f;
            try
            {
                // THE GUNNER IS THE FIRST MAN IN. A Gepard with one man is an
                // anti-aircraft gun that shoots; the hull drives on Patrol's
                // own controller whoever sits in it, and the driver is the
                // second man and the commander the third.
                List<RevivalComposition.CrewMan> loadout = Patrol.CrewedList(h.Vgs);
                Vector3[] gunPost = new Vector3[] { Platz(h, Gepard.GunnerSeat) };
                h.GunSquad = Crew.DropGroundSquad(gunPost[0], gunPost, h.Side,
                    Teil(loadout, new int[] { Gepard.GunnerSeat }), h.Key + KeyGunner);
                if (h.GunSquad == null)
                {
                    if (h.Tries >= 4)
                    {
                        h.Asked = true;
                        RevivalPlugin.L.LogWarning("GepardCrew: no gunner could be "
                            + "spawned for the Gepard on " + h.Side + " after " + h.Tries
                            + " tries - its guns stay silent.");
                    }
                    return;
                }
                int rest = count - 1;
                if (rest > 0)
                {
                    int[] seats = new int[rest];
                    Vector3[] posts = new Vector3[rest];
                    for (int i = 0; i < rest; i++)
                    {
                        seats[i] = CrewSeats[i];
                        posts[i] = Platz(h, seats[i]);
                    }
                    h.CrewSquad = Crew.DropGroundSquad(posts[0], posts, h.Side,
                        Teil(loadout, seats), h.Key + KeyCrew);
                }
                h.Asked = true;
                Sammeln(h);
                h.NextPass = Time.time + 2f;
                Durchlassen(h);
                RevivalPlugin.L.LogInfo("GepardCrew: " + h.Men.Count + " man crew ("
                    + h.Side + ") inside the Gepard, key " + h.Key + " - gunner "
                    + (h.Gunner != null) + ".");
            }
            catch (Exception ex)
            {
                h.Asked = h.Tries >= 4;
                RevivalPlugin.L.LogWarning("GepardCrew: crew spawn failed: " + ex.Message);
            }
        }

        /// <summary>The editor lines of these seats: the composition lists a
        /// Gepard's crew as driver, gunner, commander (compdef VEHICLE_SEATS),
        /// and a shorter list repeats around them.</summary>
        static List<RevivalComposition.CrewMan> Teil(List<RevivalComposition.CrewMan> loadout, int[] seats)
        {
            if (loadout == null || loadout.Count == 0) return null;
            List<RevivalComposition.CrewMan> part = new List<RevivalComposition.CrewMan>();
            for (int i = 0; i < seats.Length; i++) part.Add(loadout[seats[i] % loadout.Count]);
            return part;
        }

        static void Sammeln(Hull h)
        {
            h.Men.Clear();
            h.Crew.Clear();
            h.Gunner = null;
            Fuellen(Crew.Men(h.CrewSquad), h.Crew);
            List<Component> gun = new List<Component>();
            Fuellen(Crew.Men(h.GunSquad), gun);
            if (gun.Count > 0) h.Gunner = gun[0];
            if (h.Gunner != null) h.Men.Add(h.Gunner);
            for (int i = 0; i < h.Crew.Count; i++) h.Men.Add(h.Crew[i]);
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

        static float _nextResolve;
        static readonly Dictionary<string, List<Component>> _byKey =
            new Dictionary<string, List<Component>>();

        /// <summary>Every client, twice a second: the men of each hull by the
        /// key in their Photon instantiation data. How a client that joined -
        /// or a master that has just inherited the room - knows them.</summary>
        static void Zuordnen()
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
                if (!_byKey.TryGetValue(key, out men))
                {
                    men = new List<Component>();
                    _byKey[key] = men;
                }
                men.Add(ai);
            }
            for (int i = 0; i < _hulls.Count; i++)
            {
                Hull h = _hulls[i];
                if (h.Key == null || h.Root == null || h.Freed) continue;
                List<Component> crew, gun;
                bool haveCrew = _byKey.TryGetValue(h.Key + KeyCrew, out crew) && crew.Count > 0;
                bool haveGun = _byKey.TryGetValue(h.Key + KeyGunner, out gun) && gun.Count > 0;
                if (!haveCrew && !haveGun)
                {
                    // On the machine that spawned them the spawn data may not
                    // be readable yet; anywhere else nothing means nobody.
                    if (h.CrewSquad != null || h.GunSquad != null) Sammeln(h);
                    else { h.Men.Clear(); h.Crew.Clear(); h.Gunner = null; }
                    continue;
                }
                h.Men.Clear(); h.Crew.Clear(); h.Gunner = null;
                if (haveGun) { h.Gunner = gun[0]; h.Men.Add(gun[0]); }
                if (haveCrew)
                    for (int k = 0; k < crew.Count; k++) { h.Crew.Add(crew[k]); h.Men.Add(crew[k]); }
                if (h.Men.Count != h.PassMen) h.NextPass = 0f;
            }
        }

        // ------------------------------------------------------------- places

        static Vector3 Platz(Hull h, int seat)
        {
            if (h.Seats != null && seat >= 0 && seat < h.Seats.childCount)
            {
                Transform sp = h.Seats.GetChild(seat);
                if (sp != null) return sp.position;
            }
            return h.Root.position;
        }

        /// <summary>Every client, late in the frame: each man on his seat,
        /// with the hull's full attitude, his own navigation held off.</summary>
        static void Halten(Hull h)
        {
            Quaternion rot = h.Root.rotation;
            if (Steht(h.Gunner))
            {
                Setzen(h, h.Gunner, Platz(h, Gepard.GunnerSeat), rot);
                SeatBinding.BindSeat(h.Gunner.transform, h.Seats.GetChild(Gepard.GunnerSeat), h.Root);
            }
            else if (h.Gunner != null) SeatBinding.Detach(h.Gunner.transform);
            int k = 0;
            for (int i = 0; i < h.Crew.Count && k < CrewSeats.Length; i++)
            {
                Component ai = h.Crew[i];
                if (!Steht(ai)) { if (ai != null) SeatBinding.Detach(ai.transform); continue; }
                Setzen(h, ai, Platz(h, CrewSeats[k]), rot);
                SeatBinding.BindSeat(ai.transform, h.Seats.GetChild(CrewSeats[k]), h.Root);
                k++;
            }
        }

        static void Setzen(Hull h, Component ai, Vector3 at, Quaternion rot)
        {
            Transform tr = ai.transform;
            Parken(ai);
            float away = (tr.position - at).sqrMagnitude;
            if (away > 64f)
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
            if (away > 0.0025f) tr.position = at;
            tr.rotation = rot;
            Ruhig(ai);
        }

        /// <summary>Nothing of the men is drawn while they ride - a closed hull
        /// shows nobody, and a standing pose on a seat point would put a head
        /// through the turret roof. Re-applied on every scan because a weapon
        /// redraw instantiates a fresh model.</summary>
        static void Verstecken(Hull h)
        {
            if (!HideCrew) { Zeigen(h); return; }
            for (int m = 0; m < h.Men.Count; m++)
            {
                Component ai = h.Men[m];
                if (ai == null || !Steht(ai)) continue;
                Renderer[] rs = ai.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rs.Length; i++)
                {
                    Renderer r = rs[i];
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    if (!h.Hidden.Contains(r)) h.Hidden.Add(r);
                }
            }
        }

        static void Zeigen(Hull h)
        {
            for (int i = 0; i < h.Hidden.Count; i++)
                if (h.Hidden[i] != null) h.Hidden[i].enabled = true;
            h.Hidden.Clear();
        }

        /// <summary>The men and the hull they sit in must not collide: the
        /// hull would push itself off the road out of its own crew. Repeated
        /// because an ignored pair is forgotten when either collider is
        /// switched off and on (RevivalTechnicalCrew.Durchlassen).</summary>
        static void Durchlassen(Hull h)
        {
            if (h.Root == null || h.Men.Count == 0) return;
            try
            {
                if (h.Cols == null || h.Cols.Length == 0 || h.Cols[0] == null)
                    h.Cols = h.Root.GetComponentsInChildren<Collider>(true);
                for (int m = 0; m < h.Men.Count; m++)
                {
                    Component ai = h.Men[m];
                    if (ai == null) continue;
                    Collider[] own = ai.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < own.Length; i++)
                    {
                        Collider a = own[i];
                        if (a == null || a.isTrigger || !a.enabled || !a.gameObject.activeInHierarchy) continue;
                        for (int j = 0; j < h.Cols.Length; j++)
                        {
                            Collider b = h.Cols[j];
                            if (b == null || b.isTrigger || !b.enabled || !b.gameObject.activeInHierarchy) continue;
                            try { Physics.IgnoreCollision(a, b, true); }
                            catch { }
                        }
                    }
                }
                h.PassMen = h.Men.Count;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("GepardCrew, letting the crew through the hull: " + ex.Message);
            }
        }

        // ------------------------------------------------------ the wreck

        /// <summary>
        /// Patrol seam (UnloadCrew): the Gepard is destroyed and its riders are
        /// its wreck crew. They are shown again, set down on the ground beside
        /// the hull, get their own navigation back and are handed to NPC
        /// combat. False when nobody rode it - Patrol then spawns the ordinary
        /// wreck crew.
        /// </summary>
        internal static bool ReleaseRiders(GameObject car, string side)
        {
            if (!Enabled || car == null) return false;
            Hull h = FindRoot(car.transform);
            if (h == null || h.Released || h.Men.Count == 0) return false;
            h.Released = true;
            int riders = h.Men.Count;
            Stop(h);
            Aussteigen(h);
            List<RevivalComposition.CrewMan> loadout = Patrol.CrewedList(h.Vgs);
            int handed = 0;
            if (Abgeben(h, h.GunSquad, "gun", loadout)) handed++;
            if (Abgeben(h, h.CrewSquad, "crew", loadout)) handed++;
            RevivalPlugin.L.LogInfo("GepardCrew: the Gepard on " + side + " is destroyed - "
                + riders + " men climb out, " + handed + " squad(s) fighting on foot.");
            return true;
        }

        /// <summary>Every client once the men stop riding: seen again, beside
        /// the hull instead of inside it, their navigation back.</summary>
        static void Aussteigen(Hull h)
        {
            if (h.Freed) return;
            h.Freed = true;
            Zeigen(h);
            bool mine = RevivalTroopInsertion.MasterClient();
            for (int i = 0; i < h.Men.Count; i++)
            {
                Component ai = h.Men[i];
                if (ai == null) continue;
                SeatBinding.Detach(ai.transform);
                Vector3 at = ai.transform.position;
                // Out of the hatch onto the ground: only the machine that
                // runs the man moves him; everybody else gets the move from
                // his own position sync.
                if (mine && h.Root != null && (at - h.Root.position).sqrMagnitude < 36f)
                    at = Boden(h, i);
                NavMeshAgent agent = Agent(ai);
                try
                {
                    if (mine) ai.transform.position = at;
                    if (agent != null)
                    {
                        agent.updatePosition = true;
                        agent.updateRotation = true;
                        if (agent.isActiveAndEnabled) agent.Warp(at);
                    }
                }
                catch { }
            }
            h.Men.Clear();
            h.Crew.Clear();
            h.Gunner = null;
        }

        /// <summary>A spot on the ground beside the hull for man
        /// <paramref name="i"/>: left, right and behind, found by a ray from
        /// above so nobody climbs out into a wall or a hole.</summary>
        static Vector3 Boden(Hull h, int i)
        {
            Transform r = h.Root;
            Vector3[] around = new Vector3[] {
                r.position + r.right * 3.5f,
                r.position - r.right * 3.5f,
                r.position - r.forward * 5f,
                r.position + r.right * 3.5f - r.forward * 2f };
            Vector3 p = around[i % around.Length];
            Vector3 ground;
            GameObject under = Turret.RaycastObject(p + Vector3.up * 20f, Vector3.down, 60f, out ground);
            return under != null && !under.transform.IsChildOf(r) ? ground + Vector3.up * 0.1f : p;
        }

        static bool Abgeben(Hull h, GameObject settlement, string what,
                            List<RevivalComposition.CrewMan> loadout)
        {
            if (settlement == null) return false;
            Array men = Crew.Men(settlement);
            if (men == null) return false;
            if (NpcWar.StartGround("gepard-" + what + "-" + settlement.GetInstanceID(),
                    settlement, men, h.Root.position, false, 0f, loadout))
                return true;
            RevivalPlugin.L.LogWarning("GepardCrew: the " + what + " of the wrecked Gepard "
                + "could not be handed to NPC combat; they stay ordinary settlement NPCs.");
            return false;
        }

        /// <summary>The hull is gone or no longer one of ours: men who were
        /// hidden inside it would be left standing where its seats were, so
        /// the machine that owns them removes them.</summary>
        static void Verliere(Hull h, string why)
        {
            Stop(h);
            Zeigen(h);
            for (int i = 0; i < h.Men.Count; i++)
            {
                Component ai = h.Men[i];
                if (Steht(ai) && NpcWar.GroundOwned(ai)) { NpcWar.RemoveGroundActor(ai); continue; }
                NavMeshAgent agent = Agent(ai);
                try
                {
                    if (agent != null) { agent.updatePosition = true; agent.updateRotation = true; }
                }
                catch { }
            }
            if (h.CrewSquad != null || h.GunSquad != null)
            {
                Crew.Forget(h.CrewSquad);
                Crew.Forget(h.GunSquad);
                RevivalPlugin.L.LogInfo("GepardCrew: giving up a Gepard's crew - " + why + ".");
            }
            h.CrewSquad = null;
            h.GunSquad = null;
            h.Men.Clear();
            h.Crew.Clear();
            h.Gunner = null;
        }

        /// <summary>Patrol.StopAll: every patrol vehicle is taken off the
        /// road, and the men riding inside them go too.</summary>
        internal static void StopAll()
        {
            for (int i = 0; i < _hulls.Count; i++)
                if (!_hulls[i].Freed) Verliere(_hulls[i], "all patrols stopped");
            _hulls.Clear();
        }

        // ---------------------------------------------- armour around them

        /// <summary>A prefix on NPC_AI2.ApplyDamage: a man inside a Gepard that
        /// is still whole cannot be hurt - he is behind its armour, and a round
        /// that reached him went through a hull that stopped it.</summary>
        internal static void Install(Harmony harmony)
        {
            if (!Enabled) return;
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                MethodInfo apply = npc == null ? null : AccessTools.Method(npc, "ApplyDamage", null, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("GepardCrew: NPC_AI2.ApplyDamage not found - "
                        + "the men inside a Gepard can be hurt through the hull.");
                    return;
                }
                harmony.Patch(apply, new HarmonyMethod(typeof(GepardCrew).GetMethod(
                    "ArmourPrefix", BindingFlags.Public | BindingFlags.Static)),
                    null, null, null, null);
                RevivalPlugin.L.LogInfo("GepardCrew: installed - key prefix " + KeyPrefix
                    + ", air first, crew armour on.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("GepardCrew: armour guard not installed - " + ex);
            }
        }

        public static bool ArmourPrefix(object __instance)
        {
            if (_hulls.Count == 0) return true;
            try
            {
                for (int i = 0; i < _hulls.Count; i++)
                {
                    Hull h = _hulls[i];
                    if (h.Released || h.Freed) continue;
                    for (int k = 0; k < h.Men.Count; k++)
                        if (ReferenceEquals(h.Men[k], __instance)) return Out(h);
                }
            }
            catch { }
            return true;
        }

        // ------------------------------------------------------------ the gun

        /// <summary>
        /// MASTER ONLY. One Gepard's fire control for this frame: find a
        /// target (aircraft first), lay the turret and guns on the intercept
        /// point, fire in bursts, and tell every other client.
        /// </summary>
        static void Gun(Hull h, float dt)
        {
            GepardRig rig = h.Rig;
            string side = Fires && rig != null ? Patrol.CrewedSide(h.Vgs) : null;
            if (side == null || !rig.Alive || Out(h) || !Manned(h) || PlayerAtGun(h))
            {
                Stop(h);
                return;
            }
            h.Side = side;
            if (!h.Laying)
            {
                h.Laying = true;
                rig.LocalControl = true;
            }

            if (Time.time >= h.NextLook)
            {
                h.NextLook = Time.time + 0.5f;
                Suchen(h);
            }
            Follow(h, dt);

            Vector3 mid = rig.GunCentre();
            Vector3 p, v;
            bool air = h.AirTarget != null && h.AirTarget.Go != null;
            if (air) { p = h.AirTarget.Pos; v = h.AirTarget.Vel; }
            else if (h.Ground != null && h.Ground.gameObject.activeInHierarchy)
            {
                p = h.Ground.position + Vector3.up * 0.9f;
                v = Vector3.zero;
            }
            else
            {
                h.Ground = null;
                h.GroundNpc = null;
                h.AirFuze = 0f;
                if (h.Engaged && Time.time - h.LastContact > 3f) Abbrechen(h);
                h.Held = 0f;
                Ruhe(h, dt);
                Publish(h, false);
                return;
            }
            h.LastContact = Time.time;
            if (!h.Engaged)
            {
                h.Engaged = true;
                h.Rounds = 0;
                RevivalPlugin.L.LogInfo("GepardCrew: the gunner of " + h.Key + " (" + h.Side
                    + ") engages " + (air ? (h.AirTarget.Kind == 0 ? "a helicopter" : "an aircraft")
                        : h.GroundNpc != null ? "an NPC" : "a player")
                    + " at " + Vector3.Distance(p, mid).ToString("0", CultureInfo.InvariantCulture)
                    + " m" + (air ? " (air first)." : "."));
            }

            float tof;
            Vector3 aim = GepardGun.Intercept(mid, p, v, out tof);
            if (air)
            {
                // Q2: the radar gun is not a laser. The first burst goes off
                // the aircraft by AirInitialError; every burst's puffs are
                // corrected by AirWalkFactor, down to AirErrorFloor; a pilot who
                // changes course between bursts throws the correction off.
                float dist = Vector3.Distance(mid, p);
                if (h.ErrFor != h.AirTarget)
                {
                    h.ErrFor = h.AirTarget;
                    h.AirErr = FlakFire.Offset(h.AirTarget, mid,
                        dist * Mathf.Max(0f, F(CfgAirInitialError, 30f)) * (ShortRange.Drone(h.AirTarget) ? 0.35f : 1f) * 0.001f);
                    h.AirLastVel = v;
                }
                aim += h.AirErr;
                h.AirFuze = Vector3.Distance(mid, aim);
            }
            else
            {
                h.ErrFor = null;
                h.AirFuze = 0f;
            }
            float wantYaw, wantPitch;
            Angles(rig, aim - mid, out wantYaw, out wantPitch);
            float min = Gepard.CfgPitchMin == null ? -5f : Gepard.CfgPitchMin.Value;
            float max = Gepard.CfgPitchMax == null ? 85f : Gepard.CfgPitchMax.Value;
            bool reach = wantPitch >= min - 0.5f && wantPitch <= max + 0.5f;
            wantPitch = Mathf.Clamp(wantPitch, min, max);
            Lay(h, wantYaw, wantPitch, p, dt);

            float error = Vector3.Angle(rig.Bore(0), aim - mid);
            h.Held += dt;
            bool ready = reach && h.Held >= Mathf.Max(0f, F(CfgReaction, 1.2f))
                && error < (air ? 2.5f : 1.5f);
            bool was = h.Firing;
            Trigger(h, ready, aim);
            if (air && was && !h.Firing)
            {
                // The burst is over: correct on what he saw.
                float dist = Vector3.Distance(mid, p);
                float walk = Mathf.Clamp01(F(CfgAirWalk, 0.55f));
                float floor = (ShortRange.Drone(h.AirTarget) ? 0.7f : Mathf.Max(0f, F(CfgAirErrorFloor, 4f))) * 0.001f * dist;
                float dv = (v - h.AirLastVel).magnitude;
                h.AirLastVel = v;
                h.AirErr = h.AirErr * walk
                    + FlakFire.Offset(h.AirTarget, mid, floor) * UnityEngine.Random.value;
                if (dv > 0.5f)
                    h.AirErr += UnityEngine.Random.onUnitSphere * dv * tof
                        * Mathf.Max(0f, F(CfgAirEvade, 0.9f));
            }
            Publish(h, false);
        }

        /// <summary>Is there a gunner? With riding crews on, the living man
        /// under the key; with them off, a crewed Patrol vehicle is enough.</summary>
        static bool Manned(Hull h)
        {
            if (!Enabled) return Patrol.CrewedCount(h.Vgs) > 0;
            return Steht(h.Gunner);
        }

        /// <summary>A player in the gunner's seat owns the gun (GepardGun).</summary>
        static bool PlayerAtGun(Hull h)
        {
            Array seats = Field(h.Vgs, "Passengers") as Array;
            if (seats == null || Gepard.GunnerSeat >= seats.Length) return false;
            // B1: only a living player holds the gun against the crew.
            GameObject p = seats.GetValue(Gepard.GunnerSeat) as GameObject;
            if (p != null) return Crocodile.PlayerUp(p);
            return seats.GetValue(Gepard.GunnerSeat) as UnityEngine.Object != null;
        }

        static void Stop(Hull h)
        {
            h.AirTarget = null;
            h.Ground = null;
            h.GroundNpc = null;
            h.Held = 0f;
            h.Firing = false;
            h.ErrFor = null;
            h.AirFuze = 0f;
            if (!h.Laying) return;
            h.Laying = false;
            if (h.Engaged) Abbrechen(h);
            Publish(h, true);
            if (h.Rig != null) h.Rig.LocalControl = false;
        }

        static void Abbrechen(Hull h)
        {
            h.Engaged = false;
            RevivalPlugin.L.LogInfo("GepardCrew: the gunner of " + h.Key + " lost his target - "
                + h.Rounds + " round(s) fired.");
        }

        static void Angles(GepardRig rig, Vector3 world, out float yaw, out float pitch)
        {
            Vector3 l = rig.transform.InverseTransformDirection(world);
            yaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
            pitch = Mathf.Atan2(l.y, Mathf.Sqrt(l.x * l.x + l.z * l.z)) * Mathf.Rad2Deg;
        }

        /// <summary>Turret and guns at the real rates, the tracking radar on
        /// the target - the same moves GepardGun.Aim makes for a player.</summary>
        static void Lay(Hull h, float wantYaw, float wantPitch, Vector3 target, float dt)
        {
            GepardRig rig = h.Rig;
            float turn = Gepard.CfgTurnSpeed == null ? 90f : Gepard.CfgTurnSpeed.Value;
            float elev = Gepard.CfgElevSpeed == null ? 60f : Gepard.CfgElevSpeed.Value;
            float yaw = Mathf.MoveTowardsAngle(rig.Yaw, wantYaw, turn * dt);
            float pitch = Mathf.MoveTowards(rig.Pitch, wantPitch, elev * dt);
            float track = 0f;
            if (rig.RadarTrack != null)
            {
                float by, bp;
                Angles(rig, target - rig.RadarTrack.position, out by, out bp);
                track = Mathf.Clamp(Mathf.DeltaAngle(yaw, by), -75f, 75f);
            }
            track = Mathf.MoveTowardsAngle(rig.TrackYaw, track, 240f * dt);
            if (yaw > 180f) yaw -= 360f;
            if (yaw < -180f) yaw += 360f;
            rig.Apply(yaw, pitch, track);
        }

        /// <summary>No target: after three quiet seconds the guns go back to
        /// straight ahead at a ready elevation, the tracking radar home.</summary>
        static void Ruhe(Hull h, float dt)
        {
            h.Firing = false;
            if (Time.time - h.LastContact < 3f) return;
            GepardRig rig = h.Rig;
            float turn = (Gepard.CfgTurnSpeed == null ? 90f : Gepard.CfgTurnSpeed.Value) * 0.4f;
            float elev = (Gepard.CfgElevSpeed == null ? 60f : Gepard.CfgElevSpeed.Value) * 0.4f;
            float yaw = Mathf.MoveTowardsAngle(rig.Yaw, 0f, turn * dt);
            float pitch = Mathf.MoveTowards(rig.Pitch, 15f, elev * dt);
            float track = Mathf.MoveTowardsAngle(rig.TrackYaw, 0f, 120f * dt);
            if (Mathf.Abs(yaw - rig.Yaw) < 0.001f && Mathf.Abs(pitch - rig.Pitch) < 0.001f
                && Mathf.Abs(track - rig.TrackYaw) < 0.001f) return;
            rig.Apply(yaw, pitch, track);
        }

        /// <summary>Bursts at the gun's own rate of fire, both barrels in
        /// turn, as live rounds judged against this gunner's contacts.</summary>
        static void Trigger(Hull h, bool ready, Vector3 aim)
        {
            float now = Time.time;
            if (h.Firing && now >= h.BurstUntil)
            {
                h.Firing = false;
                h.PauseUntil = now + Mathf.Max(0.1f, F(CfgBurstPause, 1.0f));
            }
            if (!h.Firing)
            {
                if (!ready || now < h.PauseUntil) return;
                h.Firing = true;
                h.BurstUntil = now + Mathf.Max(0.1f, F(CfgBurst, 0.6f));
                h.NextShot = now;
            }
            float rpm = Gepard.CfgRpm == null ? 1100f : Gepard.CfgRpm.Value;
            float interval = 60f / Mathf.Max(60f, rpm);
            if (h.NextShot < now - 0.1f) h.NextShot = now;
            int n = 0;
            while (now >= h.NextShot && n < 4)
            {
                int gun = h.GunIdx;
                h.GunIdx = 1 - h.GunIdx;
                Vector3 muzzle = h.Rig.Muzzle(gun);
                Vector3 dir = h.Rig.Bore(gun);
                Vector3 want = (aim - muzzle).normalized;
                if (Vector3.Angle(dir, want) < 1f) dir = want;
                if (h.AirFuze > 0f) GepardShots.FireTimed(h.Rig, gun, dir, h.AirFuze, true, h.Air);
                else GepardShots.Fire(h.Rig, gun, dir, true, h.Air);
                h.Rounds++;
                h.NextShot += interval;
                n++;
            }
        }

        static void Publish(Hull h, bool now)
        {
            if (h.Rig == null || h.Root == null) return;
            if (!now && h.Firing == h.SentFiring && Time.time < h.NextPublish) return;
            h.NextPublish = Time.time + 0.1f;
            h.SentFiring = h.Firing;
            GepardNet.SendPose(h.Root, h.Rig.Yaw, h.Rig.Pitch, h.Rig.TrackYaw, h.Firing, h.Laying, h.AirFuze);
        }

        // ------------------------------------------------------------ targets

        static readonly List<GameObject> _heli = new List<GameObject>();
        static readonly List<GepardAir.Found> _planes = new List<GepardAir.Found>();

        /// <summary>
        /// AIR FIRST. Every aircraft in AirRange that carries a player of a
        /// hostile faction and can be seen from the radar is a candidate, the
        /// nearest wins, and while there is one the ground is not even looked
        /// at. With the sky clear: the nearest hostile player or NPC in
        /// GroundRange with a clear line.
        /// </summary>
        static void Suchen(Hull h)
        {
            GepardRig rig = h.Rig;
            Vector3 eye = rig.RadarSearch != null ? rig.RadarSearch.position : rig.GunCentre();
            float airRange = Mathf.Max(100f, F(CfgAirRange, 1400f));
            float now = Time.time;

            _heli.Clear();
            PlayerHeli.MissileTargets(_heli);
            for (int i = 0; i < _heli.Count; i++) Offer(h, _heli[i], 0, null, eye, airRange);
            _planes.Clear();
            GepardAir.Collect(_planes);
            for (int i = 0; i < _planes.Count; i++) Offer(h, _planes[i].Go, 6, _planes[i].Src, eye, airRange);
            Drone.Net.AirContacts(h.Air, eye, airRange);
            SurvNet.AirContacts(h.Air, eye, airRange);
            for (int i = h.Air.Count - 1; i >= 0; i--)
            {
                GepardGun.Contact c = h.Air[i];
                bool gone = c.Go == null || !c.Go.activeInHierarchy || now - c.SeenAt > 1.2f
                    || (c.Kind == 0 && PlayerHeli.MissileTarget(c.HeliView) == null)
                    || (c.Kind == 6 && !GepardAir.Alive(c));
                if (!gone && Feindlich(h, c)
                    && FlakFire.Airborne(c, ShortRange.Drone(c) ? 0.5f * Flak.K : -1f)) continue;
                if (h.AirTarget == c) h.AirTarget = null;
                h.Air.RemoveAt(i);
            }

            GepardGun.Contact best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < h.Air.Count; i++)
            {
                GepardGun.Contact c = h.Air[i];
                if (c.Go == null) continue;
                float d = Vector3.Distance(eye, c.Pos);
                if (d > airRange || d >= bestD) continue;
                if (!Feindlich(h, c)) continue;
                if (!Sicht(h, eye, c.Pos, c.Go.transform, c.Radius + 1.5f)) continue;
                best = c;
                bestD = d;
            }
            if (best != h.AirTarget) h.Held = 0f;
            h.AirTarget = best;
            if (best != null)
            {
                h.Ground = null;
                h.GroundNpc = null;
                return;
            }

            if (CfgGroundTargets != null && !CfgGroundTargets.Value) { h.Ground = null; return; }
            float groundRange = Mathf.Max(20f, F(CfgGroundRange, 700f));
            Vector3 mid = rig.GunCentre();
            Transform bestTr = null;
            Component bestNpc = null;
            bestD = float.MaxValue;
            List<Component> npcs = NpcWar.PatrolTargets();
            for (int i = 0; i < npcs.Count; i++)
            {
                Component npc = npcs[i];
                if (npc == null) continue;
                float d = Vector3.Distance(npc.transform.position, mid);
                if (d > groundRange || d >= bestD) continue;
                if (!Feind(h, npc.transform, npc)) continue;
                if (!Sicht(h, mid, npc.transform.position + Vector3.up * 0.9f, npc.transform, 1.5f)) continue;
                bestTr = npc.transform;
                bestNpc = npc;
                bestD = d;
            }
            List<GameObject> players = Spieler();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null) continue;
                float d = Vector3.Distance(go.transform.position, mid);
                if (d > groundRange || d >= bestD) continue;
                if (!Feind(h, go.transform, null)) continue;
                if (!Sicht(h, mid, go.transform.position + Vector3.up * 0.9f, go.transform, 1.5f)) continue;
                bestTr = go.transform;
                bestNpc = null;
                bestD = d;
            }
            if (bestTr != h.Ground) h.Held = 0f;
            h.Ground = bestTr;
            h.GroundNpc = bestNpc;
        }

        static void Offer(Hull h, GameObject go, int kind, GepardAir.Source src, Vector3 eye, float range)
        {
            if (go == null || !go.activeInHierarchy) return;
            GepardGun.Contact c = null;
            for (int i = 0; i < h.Air.Count; i++)
                if (h.Air[i].Go == go) { c = h.Air[i]; break; }
            if (c == null)
            {
                if ((go.transform.position - eye).sqrMagnitude > range * range * 1.2f) return;
                c = new GepardGun.Contact();
                c.Go = go;
                c.Kind = kind;
                c.Src = src;
                if (kind == 0) c.HeliView = PlayerHeli.MissileView(go);
                GepardGun.Measure(c);
                c.Pos = go.transform.TransformPoint(c.LocalCentre);
                h.Air.Add(c);
            }
            c.SeenAt = Time.time;
        }

        /// <summary>Position and a smoothed velocity of every aircraft this
        /// gunner holds, each frame - the lead is only as good as the velocity.</summary>
        static void Follow(Hull h, float dt)
        {
            if (dt <= 0f) return;
            float k = 1f - Mathf.Exp(-dt * 5f);
            for (int i = 0; i < h.Air.Count; i++)
            {
                GepardGun.Contact c = h.Air[i];
                if (c.Go == null) continue;
                Vector3 p = c.Go.transform.TransformPoint(c.LocalCentre);
                Vector3 v = (p - c.Pos) / dt;
                if (v.sqrMagnitude > 250000f) v = c.Vel;
                c.Vel = c.VelInit ? Vector3.Lerp(c.Vel, v, k) : v;
                c.VelInit = true;
                c.Pos = p;
            }
        }

        /// <summary>An aircraft is hostile when a player of a hostile faction
        /// is aboard. An empty one - parked, or abandoned and falling - is not
        /// worth a round.</summary>
        static bool Feindlich(Hull h, GepardGun.Contact c)
        {
            bool hostile, friendly;
            FlakFire.Allegiance(c, h.Side, out hostile, out friendly);
            if (friendly) return false;
            if (NpcAircraft.Hostile(c.Go)) return true;
            if (h.Root != null && NoFly.Engaged(c.Go, h.Side, h.Root.position)) return true;
            return hostile;
        }

        static bool Feind(Hull h, Transform tr, Component npc)
        {
            if (tr == null || !tr.gameObject.activeInHierarchy) return false;
            if (tr.IsChildOf(h.Root)) return false;
            for (int i = 0; i < h.Men.Count; i++)
                if (h.Men[i] != null && (tr == h.Men[i].transform || tr.IsChildOf(h.Men[i].transform)))
                    return false;
            if (npc != null)
            {
                if (!NpcWar.PatrolTarget(npc)) return false;
                return Fraktion.Feind(h.Side, NpcWar.PatrolFaction(npc));
            }
            return Fraktion.Feind(h.Side, Fraktion.Spielerseite(tr.gameObject));
        }

        /// <summary>A clear line from <paramref name="from"/> to the target:
        /// the ray steps past this hull and its own men, and counts as a
        /// sighting when it ends on the target or close enough to it.</summary>
        static bool Sicht(Hull h, Vector3 from, Vector3 to, Transform target, float slack)
        {
            Vector3 d = to - from;
            float dist = d.magnitude;
            if (dist < 1f) return true;
            Vector3 dir = d / dist;
            Vector3 origin = from;
            float rest = dist;
            for (int i = 0; i < 5 && rest > 0.5f; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, rest,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    return true;
                Transform t = hit.transform;
                if (t == target || t.IsChildOf(target)) return true;
                if (t.IsChildOf(h.Root) || Eigen(h, t))
                {
                    rest -= hit.distance + 0.2f;
                    origin = hit.point + dir * 0.2f;
                    continue;
                }
                return Vector3.Distance(hit.point, to) < slack;
            }
            return true;
        }

        static bool Eigen(Hull h, Transform t)
        {
            for (int i = 0; i < h.Men.Count; i++)
                if (h.Men[i] != null && (t == h.Men[i].transform || t.IsChildOf(h.Men[i].transform)))
                    return true;
            return false;
        }

        // ------------------------------------------------------------ players

        static readonly List<GameObject> _players = new List<GameObject>();
        static float _nextPlayers;
        static PropertyInfo _ngsInstance;
        static FieldInfo _ngsPlayers;
        static bool _ngsLooked;

        /// <summary>P6b: a Gepard hull within <paramref name="reach"/> of the
        /// point, not burnt out, its gun alive and its gunner in his seat.
        /// Every client can answer it (no side, no laying state - those are
        /// the master's), so a zone that depends on its Gepard warns only
        /// while one stands (Revival.NoFly.cs, MilitaryTown.NoFlyArmed).</summary>
        internal static bool Standing(Vector3 at, float reach)
        {
            for (int i = 0; i < _hulls.Count; i++)
            {
                Hull h = _hulls[i];
                if (h == null || h.Vgs == null || h.Root == null || Out(h)) continue;
                if (h.Rig != null && !h.Rig.Alive) continue;
                Vector3 d = h.Root.position - at;
                d.y = 0f;
                if (d.sqrMagnitude <= reach * reach && Manned(h)) return true;
            }
            return false;
        }

        /// <summary>P6a no-fly zones: a Gepard of <paramref name="side"/>
        /// laying its guns (manned, alive, master) within
        /// <paramref name="reach"/> of the point - it takes over the zone's
        /// defence (Revival.NoFly.cs).</summary>
        internal static bool Defends(string side, Vector3 at, float reach)
        {
            string own = Fraktion.Eigene(side);
            for (int i = 0; i < _hulls.Count; i++)
            {
                Hull h = _hulls[i];
                if (h == null || !h.Laying || h.Root == null || Fraktion.Eigene(h.Side) != own) continue;
                Vector3 d = h.Root.position - at;
                d.y = 0f;
                if (d.sqrMagnitude <= reach * reach) return true;
            }
            return false;
        }

        internal static List<GameObject> Spieler()
        {
            if (Time.time < _nextPlayers) return _players;
            _nextPlayers = Time.time + 0.5f;
            _players.Clear();
            try
            {
                if (!_ngsLooked)
                {
                    _ngsLooked = true;
                    Type ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                    if (ngs != null)
                    {
                        _ngsInstance = ngs.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                        _ngsPlayers = AccessTools.Field(ngs, "NetworkPlayers");
                    }
                    if (_ngsInstance == null || _ngsPlayers == null)
                        RevivalPlugin.L.LogWarning("GepardCrew: NetworkGameServer.Instance or "
                            + ".NetworkPlayers is missing - the gunner sees no players.");
                }
                if (_ngsInstance == null || _ngsPlayers == null) return _players;
                object server = _ngsInstance.GetValue(null, null);
                System.Collections.IList list = server == null ? null
                    : _ngsPlayers.GetValue(server) as System.Collections.IList;
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

        // --------------------------------------------------------------- misc

        internal static bool Steht(Component ai)
        {
            return ai != null && NpcWar.GroundAlive(ai);
        }

        internal static void Parken(Component ai)
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

        static FieldInfo _fAgent;
        static Type _fAgentOwner;

        internal static NavMeshAgent Agent(Component ai)
        {
            if (ai == null) return null;
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _fAgentOwner))
            {
                _fAgentOwner = t;
                _fAgent = AccessTools.Field(t, "_navAgent");
                if (_fAgent == null) _fAgent = AccessTools.Field(t, "_navMeshAgent");
            }
            try
            {
                NavMeshAgent a = _fAgent == null ? null : _fAgent.GetValue(ai) as NavMeshAgent;
                return a != null ? a : ai.GetComponent<NavMeshAgent>();
            }
            catch { return ai.GetComponent<NavMeshAgent>(); }
        }

        const float PauseSeconds = 1.4f;
        static MethodInfo _mClearIntentions, _mPauseTime;
        static Type _mQuietOwner;

        /// <summary>The vanilla idle logic held off a riding man: the same
        /// short pause refreshed every frame that the technical's crew and the
        /// artillery battery use.</summary>
        internal static void Ruhig(Component ai)
        {
            Type t = ai.GetType();
            if (!ReferenceEquals(t, _mQuietOwner))
            {
                _mQuietOwner = t;
                _mClearIntentions = AccessTools.Method(t, "ClearIntentions", null, null);
                _mPauseTime = AccessTools.Method(t, "SetCalculatedPauseTime", new Type[] { typeof(float) }, null);
                if (_mPauseTime == null)
                    _mPauseTime = AccessTools.Method(t, "SetPauseTime", new Type[] { typeof(float) }, null);
            }
            try
            {
                // P1b: compiled calls, no reflection Invoke per man per frame.
                if (_mClearIntentions != null) FastCall.Void(_mClearIntentions, ai);
                if (_mPauseTime != null) FastCall.VoidFloat(_mPauseTime, ai, PauseSeconds);
            }
            catch { }
        }

        static object Field(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = FastField.Find(instance.GetType(), name);
            return f == null ? null : f.GetValue(instance);
        }

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
                    if (_viewType != null) _viewId = AccessTools.PropertyGetter(_viewType, "viewID");
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

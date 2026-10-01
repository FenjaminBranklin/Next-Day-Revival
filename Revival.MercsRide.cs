// B3c mercenaries, phase 4: FOLLOW MY VEHICLE (docs/ai/tasks/mercenaries.md
// section 3.5, D14, D15, 4.3; report docs/ai/tasks/b3c-mercs-vehicle.md).
//
//   ORDER      MercOrder.Vehicle ("vehicle" in the roster row). With the owner
//              on foot it is FOLLOW ME. With the owner in a vehicle - any
//              VehicleGameSystem (Ural, UAZ, technical, MTW, T-72, Gepard,
//              ...), the player-flown Mi-8 or the An-2 - every merc with the
//              order within 60 m runs to it while it stands (under 2.5 m/s,
//              and on the ground for an aircraft) and takes a free seat: the
//              gunner's seat of an armed vehicle first, then the others in seat
//              order after the driver's. NOT ENOUGH SEATS: the rest follow on
//              foot at their FOLLOW slot (and are put beside the owner when
//              lost, as FOLLOW does), and board at the next stop when a seat
//              has come free. Waiting at the spot was the other choice; it
//              leaves a man behind for good after one full truck.
//   SEATS      a merc is NEVER written into VehicleGameSystem.Passengers
//              (SetDamageToAllPassengers throws on an NPC). Each peer parents him to his seat.
//              A seat is free when Passengers[i] is null and no merc - ours or
//              another owner's - claims it. Players always win (D15): a player
//              in a merc's seat sends him to the next free seat, else out on
//              foot; the game's own seat hand-out prefers unclaimed seats
//              (FreeSeatPostfix), so that rarely happens.
//   RIDING     every client puts each rider on his seat from the vehicle's own
//              transform in LateUpdate ([[crew-rides-via-spawn-key]]): agent
//              parked, idle logic held, collisions with THIS hull ignored (so
//              nothing pushes the vehicle and nothing flings the man), the
//              game's passenger clip sampled. Closed hulls (MTW, T-72,
//              Gepard) hide him and nothing can hurt him (D14); an aircraft
//              carries him as held, unhittable cargo. Other clients know the
//              man by his Photon spawn key merc/<actor>/<id>.
//   GETTING OUT  owner out (or in another vehicle) and the vehicle standing
//              (on the ground): each merc is put on a NavMesh spot beside it
//              (the game's GetOutPoints first, then a ring outside the hull),
//              shielded for 3 s, FOLLOW resumes; the order stays, so the next
//              vehicle the owner takes is boarded again. Destroyed: a closed
//              hull or an aircraft kills him with it; an open vehicle sets him
//              down beside the wreck.
//   GUNNER     a merc in the gunner's seat of the technical (DShKM), the MTW
//              (KPVT), the T-72 (HE shell) or the Gepard (35 mm) lays and fires
//              that weapon at hostiles under the merc rules - never the owner
//              or a whitelisted player, PEACEFUL only at whoever hit him - with
//              the player gunner's damage, range and cadence and the guns' own
//              effects (BtrGun, VehicleShotSound, GepardShots). Damage goes by
//              NpcWar's road (RemoteHit for an NPC another client owns), sight
//              and spread by GunnerAI. The turret pose reaches the others on
//              the channel each gun already has (Turret.Net, GepardNet); the
//              technical's pintle has none and rides on this file's event.
//              X (docs/ai/tasks/x-merc-technical-gunner.md): full-auto bursts
//              at the gun's cadence, belt and reload (MercGunDrill,
//              Revival.MercGunDrillCore.cs). At the technical's open MG he is
//              TechnicalGun's third gunner body (player, crew, merc): laid and
//              placed in LateTechnical before TechnicalGun.LateAll stands him
//              behind the pintle, turns him with it and puts his hands on the
//              grips - on every client - with his rifle holstered; badly hurt
//              he leaves the open MG for a cab seat.
//   NETWORK    Photon event 199 ([Mercs] NetworkEventCode): the owner's riders
//              {1, n, (id, kind, view, seat, flags, yaw, pitch) x n} on every
//              change (reliable) and once a second while anyone rides (5 Hz
//              while a gunner works). A client that hears nothing of a rider
//              for 4 s lets him go; PUN removes the bodies when the owner
//              leaves, and the roster brings them back after a relog.
//
// SEAMS: Revival.Mercs.cs (MercOrder.Vehicle, MercUnit.Ride, Step ->
// StepOwner, DamagePrefix -> Shielded, KillTargetPrefix, Despawn / OnDeath ->
// Forget, OrderVehicle, Install -> Install), Revival.MercsWar.cs (MercStep ->
// MercBoard, MercMayStand, MercInCombat), Revival.NpcCombat.cs (RunGround ->
// MercSeated, PickTargetForMan -> HiddenRider), Revival.MercsUi.cs (wheel 5,
// K K in a vehicle, list), RevivalPlugin.cs (LateUpdate -> LateFrame),
// Revival.PlayerHeli.cs / Revival.PlayerAn2.cs (cabin seat seams),
// RevivalFrameProfiler.cs (Mercs.LateFrame).
//
// Units: the world is modelled ~2.8x real size. C# 3.0, ASCII only (the
// Cyrillic lives in Loc.T player strings).
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>B3c: one vehicle mercs can ride, as this client knows it.</summary>
    internal sealed class MercCarrier
    {
        internal const int Ground = 0, Heli = 1, Plane = 2;
        internal const int GunNone = 0, GunTechnical = 1, GunBtr = 2, GunTank = 3, GunGepard = 4;

        internal int Kind;
        internal int View;
        internal Component Vgs;               // Ground only: VehicleGameSystem
        internal GameObject Go;
        internal Transform Root;
        internal Transform SeatPoints, GetOut;
        internal int Seats;
        internal int GunKind, GunSeat = -1;
        internal bool Closed, Air;
        internal float Radius = 8f;
        internal Collider[] Cols;
        internal Transform[] Turrets;         // MTW / T-72
        internal Renderer TurretRenderer;
        internal Transform Mount, Gun;        // technical
        internal GepardRig Rig;               // Gepard
        internal int GunIdx;
        internal Vector3 LastPos;
        internal float LastAt = -1f, Speed, GroundAt = -1f;
        internal bool Grounded;
        internal string Ru = "машина", En = "vehicle";
    }

    /// <summary>B3c: one merc's ride - the owner's own (MercUnit.Ride) or
    /// another owner's merc as this client heard it on event 199.</summary>
    internal sealed class MercSeat
    {
        internal MercCarrier Carrier;         // the vehicle he sits in; null = on foot
        internal int Seat = -1;
        internal bool Hidden, Gunner, Firing, DriveEscort;
        internal float GunYaw, GunPitch;      // technical pintle, sent to the others
        // owner: getting in
        internal MercCarrier Boarding;
        internal int BoardSeat = -1;
        internal Vector3 BoardAt;
        internal float BoardUntil, NextBoard, ShieldUntil, SeatedAt;
        internal bool Dying, NoSeatSaid;
        // every client
        internal Component Ai;
        internal string Key;
        internal int Actor;
        internal float Heard, NextPass;
        internal int WantKind = -1, WantView, WantSeat = -1;
        internal MercOrder AssignedOrder;
        internal MercCarrier AssignedCarrier;
        internal float NextAssignedSearch;
        internal readonly List<Renderer> Off = new List<Renderer>();
        // owner: the gun
        internal Transform Target;
        internal Component TargetNpc, TargetCarrier;
        internal bool TargetPlayer, Suppress;
        internal Vector3 LastKnown, SpeedFrom;
        internal float NextScan, Held, LastSeen, LastContact, Vis, SpeedAt, TargetSpeed, NextPose;
        internal int Rounds, Hits;
        internal int TargetHits;             // W: hits on this target (a kill toast needs one)
        // X: bursts, belt and reload (Revival.MercGunDrillCore.cs); the
        // rifle put away while he works the technical's MG (every client).
        internal readonly MercGunDrill Drill = new MercGunDrill();
        internal readonly List<Renderer> Holstered = new List<Renderer>();
        internal Transform Hand;
        internal float NextHolster;
        internal int PlacedFrame = -1;        // frame the early technical pass placed him

        internal bool Seated { get { return Carrier != null; } }
    }

    internal static partial class MercRide
    {
        const float BoardReach = 168f;        // 60 m: run to the vehicle from this far
        const float BoardSpeed = 7f;          // units/s (2.5 m/s): board while it is slower
        const float LeaveSpeed = 4.2f;        // units/s (1.5 m/s): get out while it is slower
        const float BoardSeconds = 25f;
        const float ShieldSeconds = 3f;
        const float LandedGap = 8.4f;         // 3 m: an aircraft this close above ground stands
        const float HeardSeconds = 4f;

        internal static ConfigEntry<int> CfgEventCode;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEventCode = cfg.Bind("Mercs", "NetworkEventCode", 199,
                "Photon event code (0..199) of the mercenaries riding in vehicles (who sits where, the "
                + "technical's pintle). Must be the same on every client.");
        }

        static int Code()
        {
            return Mathf.Clamp(CfgEventCode == null ? 199 : CfgEventCode.Value, 0, 199);
        }

        // ============================================================ state
        static readonly Dictionary<int, MercCarrier> _carriers = new Dictionary<int, MercCarrier>();
        static readonly Dictionary<string, MercSeat> _remote = new Dictionary<string, MercSeat>();
        static readonly List<MercUnit> _riders = new List<MercUnit>();
        static readonly HashSet<int> _hidden = new HashSet<int>();
        static readonly List<Vector3> _outSpots = new List<Vector3>();
        static MercCarrier _ownerCarrier;
        static bool _dirty;
        static int _emptySends;
        static float _nextSend, _nextToast, _nextKeyScan, _nextPurge;
        static string _lastErr;

        /// <summary>The vehicle the owner sits in (4 Hz), or null.</summary>
        internal static bool OwnerInVehicle { get { return _ownerCarrier != null; } }

        internal static string OwnerVehicleLabel
        {
            get { return _ownerCarrier == null ? null : Loc.T(_ownerCarrier.Ru, _ownerCarrier.En); }
        }

        /// <summary>A rider of a closed hull or an aircraft: no NPC takes him
        /// as a target (NpcWar.PickTargetForMan), on any client.</summary>
        internal static bool HiddenRider(Component ai)
        {
            return _hidden.Count > 0 && ai != null && _hidden.Contains(ai.GetInstanceID());
        }

        /// <summary>D14 and the dismount grace: nothing hurts him now.</summary>
        internal static bool Shielded(MercUnit u)
        {
            if (u == null) return false;
            MercSeat st = u.Ride;
            if (st.Dying) return false;
            if (st.Carrier != null && (st.Carrier.Closed || st.Carrier.Air)) return true;
            return Time.time < st.ShieldUntil;
        }

        /// <summary>For the list: riding / on the gun / boarding, else null.</summary>
        internal static string StateText(MercUnit u)
        {
            if (u == null) return null;
            string trip = MercDrive.State(u);
            if (trip != null) return trip;
            MercSeat st = u.Ride;
            if (st.Carrier != null)
                return st.Gunner ? Loc.T("у орудия", "on the gun") : Loc.T("едет", "riding");
            if (st.Boarding != null) return Loc.T("садится", "boarding");
            return null;
        }

        // ============================================================ install
        static Type _vgsType;
        static FieldInfo _fPassengers, _fGetOut;
        static bool _vgsLooked;

        static bool VgsLook()
        {
            if (_vgsLooked) return _vgsType != null;
            _vgsLooked = true;
            _vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (_vgsType == null) return false;
            _fPassengers = FastField.Find(_vgsType, "Passengers");
            _fGetOut = FastField.Find(_vgsType, "GetOutPoints");
            return true;
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                if (!VgsLook()) return;
                MethodInfo free = AccessTools.Method(_vgsType, "GetFreePassengerPlaceId", null, null);
                if (free != null)
                    harmony.Patch(free, null, new HarmonyMethod(typeof(MercRide).GetMethod("FreeSeatPostfix")), null, null, null);
                RevivalPlugin.L.LogInfo("Mercs: vehicle seats hooked (free seat hand-out " + (free != null) + ").");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Mercs: vehicle seat hook - " + ex.Message); }
        }

        /// <summary>Postfix on VehicleGameSystem.GetFreePassengerPlaceId: a
        /// player boarding is handed a seat no merc sits in when there is one
        /// (a player still gets a merc's seat when nothing else is free - D15).
        /// The MTW / T-72 gunner's seat stays out of it, as Turret keeps it.</summary>
        public static void FreeSeatPostfix(object __instance, ref int __result)
        {
            try
            {
                if (__result < 0 || (_riders.Count == 0 && _remote.Count == 0)) return;
                Component vgs = __instance as Component;
                if (vgs == null) return;
                MercCarrier c;
                if (!_carriers.TryGetValue(vgs.gameObject.GetInstanceID(), out c)) return;
                if (Claimant(c, __result, null) == null) return;
                Array pass = Passengers(vgs);
                if (pass == null) return;
                for (int i = 1; i < pass.Length && i < c.Seats; i++)
                {
                    if (pass.GetValue(i) as UnityEngine.Object != null) continue;
                    if (Claimant(c, i, null) != null) continue;
                    if (i == c.GunSeat && c.GunKind != MercCarrier.GunTechnical) continue;
                    if (c.SeatPoints != null && i < c.SeatPoints.childCount
                        && c.SeatPoints.GetChild(i).name == Turret.SeatName) continue;
                    __result = i;
                    return;
                }
            }
            catch { }
        }

        // =========================================================== carriers
        static Array Passengers(Component vgs)
        {
            if (vgs == null || !VgsLook() || _fPassengers == null) return null;
            try { return _fPassengers.GetValue(vgs) as Array; }
            catch { return null; }
        }

        static MercCarrier CarrierOf(int kind, GameObject go, Component vgs)
        {
            if (go == null) return null;
            int id = go.GetInstanceID();
            MercCarrier c;
            if (_carriers.TryGetValue(id, out c) && c.Go != null) return c;
            c = Build(kind, go, vgs);
            if (c != null) _carriers[id] = c;
            return c;
        }

        static MercCarrier Build(int kind, GameObject go, Component vgs)
        {
            MercCarrier c = new MercCarrier();
            c.Kind = kind; c.Go = go; c.Root = go.transform; c.Vgs = vgs;
            c.Air = kind != MercCarrier.Ground;
            if (kind == MercCarrier.Heli)
            {
                c.View = PlayerHeli.ViewOf(go);
                c.Seats = 8;
                c.Ru = "Ми-8"; c.En = "Mi-8";
            }
            else if (kind == MercCarrier.Plane)
            {
                c.View = PlayerAn2.View(go);
                c.Seats = Mathf.Max(1, An2Model.CabinSeats);
                c.Ru = "Ан-2"; c.En = "An-2";
            }
            else
            {
                if (vgs == null) return null;
                c.View = ViewOf(go);
                Transform root = c.Root;
                Component found;
                bool gepard = Gepard.IstGepard(root);
                c.SeatPoints = gepard ? Gepard.FindSeatPoints(go, out found) : Technical.FindSeatPoints(go, out found);
                Array pass = Passengers(vgs);
                if (c.SeatPoints == null || pass == null) return null;
                c.Seats = Mathf.Min(pass.Length, c.SeatPoints.childCount);
                c.GetOut = _fGetOut == null ? null : _fGetOut.GetValue(vgs) as Transform;
                c.Closed = GunnerAI.Armoured(vgs);
                if (gepard)
                {
                    c.Ru = "Гепард"; c.En = "Gepard";
                    c.Rig = Gepard.Rig(root);
                    if (c.Rig != null && Gepard.GunnerSeat < c.Seats)
                    { c.GunKind = MercCarrier.GunGepard; c.GunSeat = Gepard.GunnerSeat; }
                }
                else if (Technical.IstTechnical(root))
                {
                    c.Ru = "техничка"; c.En = "technical";
                    c.Mount = Technical.MountOf(root);
                    c.Gun = Technical.GunOf(root);
                    if (c.Mount != null && c.Mount.parent != null && Technical.GunnerSeat < c.Seats)
                    { c.GunKind = MercCarrier.GunTechnical; c.GunSeat = Technical.GunnerSeat; }
                }
                else
                {
                    bool tank = Tank.IstPanzer(root);
                    if (tank) { c.Ru = "Т-72"; c.En = "T-72"; }
                    else if (root.name.IndexOf("btr", StringComparison.OrdinalIgnoreCase) >= 0) { c.Ru = "БТР"; c.En = "MTW"; }
                    else if (UralTruck.IstUral(root)) { c.Ru = "Урал"; c.En = "Ural"; }
                    int g = -1;
                    for (int i = 0; i < c.SeatPoints.childCount; i++)
                        if (c.SeatPoints.GetChild(i).name == Turret.SeatName) { g = i; break; }
                    if (g >= 0 && g < c.Seats)
                    {
                        c.Turrets = Turret.FindTurrets(root);
                        if (c.Turrets.Length > 0 && c.Turrets[0].parent != null)
                        {
                            c.GunKind = tank ? MercCarrier.GunTank : MercCarrier.GunBtr;
                            c.GunSeat = g;
                            for (int i = 0; i < c.Turrets.Length && c.TurretRenderer == null; i++)
                                c.TurretRenderer = c.Turrets[i].GetComponent<Renderer>();
                        }
                    }
                }
            }
            c.Cols = go.GetComponentsInChildren<Collider>(true);
            c.Radius = RadiusOf(c);
            RevivalPlugin.L.LogInfo("Mercs: " + c.En + " (view " + c.View + ") rideable - " + c.Seats + " seats, gun "
                + c.GunKind + " at seat " + c.GunSeat + (c.Closed ? ", closed hull" : "") + (c.Air ? ", aircraft" : "")
                + ", radius " + c.Radius.ToString("0.0") + ".");
            return c;
        }

        /// <summary>Half the hull's footprint from its solid colliders.</summary>
        static float RadiusOf(MercCarrier c)
        {
            bool any = false;
            Bounds b = new Bounds(c.Root.position, Vector3.zero);
            for (int i = 0; i < c.Cols.Length; i++)
            {
                Collider col = c.Cols[i];
                if (col == null || col.isTrigger || !col.enabled) continue;
                if (!any) { b = col.bounds; any = true; }
                else b.Encapsulate(col.bounds);
            }
            if (!any) return 8f;
            return Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z), 3f, 40f);
        }

        static bool Alive(MercCarrier c)
        {
            if (c == null || c.Go == null || c.Root == null) return false;
            if (c.Kind == MercCarrier.Heli) return !PlayerHeli.FindWreck(c.Go);
            if (c.Kind == MercCarrier.Plane) return !PlayerAn2.Down(c.Go);
            return NpcWar.PatrolVehicleAlive(c.Vgs);
        }

        /// <summary>Speed (units/s) at the 4 Hz step, and for an aircraft
        /// whether it stands on something.</summary>
        static void Motion(MercCarrier c, float now)
        {
            if (c == null || c.Root == null || c.LastAt == now) return;
            Vector3 p = c.Root.position;
            float dt = now - c.LastAt;
            if (c.LastAt >= 0f && dt > 0.01f && dt < 2f)
                c.Speed = Mathf.Lerp(c.Speed, (p - c.LastPos).magnitude / dt, 0.6f);
            else c.Speed = 0f;
            c.LastPos = p; c.LastAt = now;
            if (!c.Air) { c.Grounded = true; return; }
            float gap;
            c.Grounded = Below(c, out gap) && gap < LandedGap;
        }

        static bool Below(MercCarrier c, out float gap)
        {
            gap = 9999f;
            Vector3 start = c.Root.position + Vector3.up * 4f;
            float rest = 4f + LandedGap + 30f;
            for (int step = 0; step < 5 && rest > 0f; step++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(start, Vector3.down, out hit, rest, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore)) return false;
                if (hit.collider.transform.IsChildOf(c.Root))
                {
                    float adv = hit.distance + 0.1f;
                    start += Vector3.down * adv; rest -= adv;
                    continue;
                }
                gap = Mathf.Max(0f, c.Root.position.y - hit.point.y);
                return true;
            }
            return false;
        }

        static bool Standing(MercCarrier c, float maxSpeed)
        {
            return c != null && c.Speed <= maxSpeed && (!c.Air || c.Grounded);
        }

        /// <summary>The vehicle the owner sits in: the Mi-8 or An-2 he is
        /// aboard, else the VehicleGameSystem his body hangs under.</summary>
        static MercCarrier OwnerCarrier(GameObject owner)
        {
            GameObject heli = PlayerHeli.Machine;
            if (heli != null) return CarrierOf(MercCarrier.Heli, heli, null);
            GameObject plane = PlayerAn2.Plane;
            if (plane != null) return CarrierOf(MercCarrier.Plane, plane, null);
            if (owner == null || Mercs.PlayerDead(owner)) return null;
            Component vgs = GunnerAI.Carrier(owner.transform);
            return vgs == null ? null : CarrierOf(MercCarrier.Ground, vgs.gameObject, vgs);
        }

        // ============================================================== seats
        /// <summary>Who claims this seat, other than <paramref name="self"/>.</summary>
        static MercSeat Claimant(MercCarrier c, int seat, MercSeat self)
        {
            for (int i = 0; i < _riders.Count; i++)
            {
                MercSeat st = _riders[i].Ride;
                if (st != self && st.Carrier == c && st.Seat == seat) return st;
            }
            foreach (MercSeat st in _remote.Values)
                if (st != self && st.Carrier == c && st.Seat == seat) return st;
            return null;
        }

        static bool PlayerIn(MercCarrier c, int seat)
        {
            if (c.Kind != MercCarrier.Ground) return false;
            Array pass = Passengers(c.Vgs);
            if (pass == null || seat >= pass.Length) return true;
            return pass.GetValue(seat) as UnityEngine.Object != null;
        }

        /// <summary>The cabin seat the local player holds in this aircraft.</summary>
        static int LocalCabinSeat(MercCarrier c)
        {
            if (c.Kind == MercCarrier.Heli) return ReferenceEquals(PlayerHeli.Machine, c.Go) ? PlayerHeli.CabinSeatTaken : -1;
            if (c.Kind == MercCarrier.Plane) return ReferenceEquals(PlayerAn2.Plane, c.Go) ? PlayerAn2.CabinSeatTaken : -1;
            return -1;
        }

        static bool SeatFree(MercCarrier c, int seat, MercSeat self)
        {
            if (c == null || seat < 0 || seat >= c.Seats) return false;
            if (c.Kind == MercCarrier.Ground) { if ((seat == 0 && !MercDrive.DriverClaim(c, self)) || PlayerIn(c, seat)) return false; }
            else if (seat == LocalCabinSeat(c)) return false;
            if (c.Kind == MercCarrier.Ground)
            {
                if (Patrol.CrewedCount(c.Vgs) > 0) return false;
                if (seat == c.GunSeat && c.GunKind == MercCarrier.GunTechnical && TechnicalCrew.GunnerBody(c.Vgs) != null) return false;
            }
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++)
            {
                Mercs.Record r = roster[i];
                if (r.Dead || r.Deserted || (r.Unit != null && r.Unit.Ride == self)) continue;
                if (MercAA.IsVehicle(r.Order) && -Mathf.RoundToInt(r.Order.Facing.x) == c.View
                    && Mathf.RoundToInt(r.Order.Facing.z) == seat + 1) return false;
                if (r.Order.Mode == MercOrder.Drive && -Mathf.RoundToInt(r.Order.Facing.x) == c.View
                    && Mathf.RoundToInt(r.Order.Facing.z) == seat + 1) return false;
            }
            MercSeat other = Claimant(c, seat, self);
            if (other == null) return true;
            // Two owners' mercs on one seat (a race over the wire): the one
            // already on it with the lower actor number keeps it.
            return self != null && self.Carrier == c && self.Seat == seat
                && self.Actor > 0 && other.Actor > self.Actor;
        }

        /// <summary>The gunner's seat of an armed vehicle first, then the rest
        /// in seat order after the driver's; -1 when none is free.</summary>
        static int PickSeat(MercCarrier c, MercSeat st)
        {
            if (c.GunKind != MercCarrier.GunNone && SeatFree(c, c.GunSeat, st) && !TooHurtForGun(c, st, HurtReturn)) return c.GunSeat;
            for (int i = c.Kind == MercCarrier.Ground ? 1 : 0; i < c.Seats; i++)
                if (SeatFree(c, i, st)) return i;
            return -1;
        }

        /// <summary>The full seat attitude, including hull pitch and roll.</summary>
        static bool SeatPose(MercSeat st, out Vector3 pos, out Quaternion rot)
        {
            MercCarrier c = st.Carrier;
            pos = Vector3.zero; rot = Quaternion.identity;
            if (c == null || c.Root == null || st.Seat < 0) return false;
            Transform root = c.Root;
            rot = root.rotation;
            if (c.Kind == MercCarrier.Heli) { pos = PlayerHeli.CabinSeatWorld(c.Go, st.Seat); return true; }
            if (c.Kind == MercCarrier.Plane)
            {
                pos = root.position + root.rotation * (An2Model.Seat(st.Seat) * PlayerAn2.K);
                return true;
            }
            if (c.SeatPoints == null || st.Seat >= c.SeatPoints.childCount) return false;
            Transform sp = c.SeatPoints.GetChild(st.Seat);
            pos = sp.position;
            rot = c.GunKind == MercCarrier.GunTechnical && st.Seat == c.GunSeat && c.Mount != null
                ? c.Mount.rotation : sp.rotation;
            return true;
        }

        /// <summary>Where a merc walks to get in: the get-out point nearest his
        /// seat, else the hull's side toward him.</summary>
        static Vector3 BoardPoint(MercCarrier c, int seat, Vector3 from)
        {
            Vector3 seatAt = c.Root.position;
            MercSeat probe = new MercSeat();
            probe.Carrier = c; probe.Seat = seat;
            Vector3 p; Quaternion r;
            if (SeatPose(probe, out p, out r)) seatAt = p;
            if (c.GetOut != null && c.GetOut.childCount > 0)
            {
                Transform best = null;
                float bestSqr = float.MaxValue;
                for (int i = 0; i < c.GetOut.childCount; i++)
                {
                    Transform t = c.GetOut.GetChild(i);
                    float d = (t.position - seatAt).sqrMagnitude;
                    if (d < bestSqr) { bestSqr = d; best = t; }
                }
                if (best != null) return best.position;
            }
            Vector3 side = from - c.Root.position; side.y = 0f;
            if (side.sqrMagnitude < 0.01f) side = c.Root.right;
            return c.Root.position + side.normalized * (c.Radius + 3f);
        }

        /// <summary>A NavMesh spot beside the vehicle for the k-th man out:
        /// the game's get-out points, then a ring outside the hull. Never a
        /// point under or on it, never one another man was given this pass.</summary>
        static Vector3 OutSpot(MercCarrier c, int k, Vector3 fallback)
        {
            if (c == null || c.Root == null) return fallback;
            Vector3 centre = c.Root.position;
            List<Vector3> cand = new List<Vector3>();
            if (c.GetOut != null)
            {
                int n = c.GetOut.childCount;
                for (int i = 0; i < n; i++) cand.Add(c.GetOut.GetChild((i + k) % n).position);
            }
            Vector3 fwd = c.Root.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            for (int i = 0; i < 12; i++)
            {
                float a = (90f + 30f * ((i + k) % 12)) * Mathf.Deg2Rad;
                Vector3 dir = Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.up) * fwd;
                cand.Add(centre + dir * (c.Radius + 5f + 3f * (i / 12)));
            }
            for (int i = 0; i < cand.Count; i++)
            {
                Vector3 g;
                if (!RevivalGroundEnemies.TryGround(cand[i], 6f, out g)) continue;
                Vector3 off = g - centre; off.y = 0f;
                if (off.magnitude < c.Radius * 0.7f + 1.5f) continue;
                bool used = false;
                for (int j = 0; j < _outSpots.Count && !used; j++)
                    if ((_outSpots[j] - g).sqrMagnitude < 4f) used = true;
                if (used) continue;
                _outSpots.Add(g);
                return g;
            }
            Vector3 f;
            if (RevivalGroundEnemies.TryGround(centre + c.Root.right * (c.Radius + 8f), 12f, out f)) return f;
            return RevivalGroundEnemies.TryGround(fallback, 12f, out f) ? f : fallback;
        }

        // ========================================================= owner step
        /// <summary>From Mercs.Step (4 Hz), on the owner's client.</summary>
        internal static void StepOwner(float now, GameObject owner)
        {
            EnsureHooked();
            _outSpots.Clear();
            _riders.Clear();
            List<Mercs.Record> roster = Mercs.Roster;
            for (int i = 0; i < roster.Count; i++)
            {
                MercUnit u = roster[i].Unit;
                if (u == null || roster[i].Down.Down) continue;
                UnityEngine.Object live = u.Ai;
                if (live == null) continue;
                u.Ride.Ai = u.Ai;
                u.Ride.Actor = Mercs.LocalActor;
                _riders.Add(u);
            }
            MercCarrier oc = null;
            try { oc = owner == null ? null : OwnerCarrier(owner); }
            catch (Exception ex) { Warn("owner vehicle", ex); }
            if (oc != _ownerCarrier && oc == null)
                for (int i = 0; i < _riders.Count; i++) _riders[i].Ride.NoSeatSaid = false;
            _ownerCarrier = oc;
            if (oc != null) Motion(oc, now);
            for (int i = 0; i < _riders.Count; i++)
            {
                MercUnit u = _riders[i];
                try { StepUnit(u, u.Ride, oc, now, i); }
                catch (Exception ex) { Warn(u.Name, ex); }
            }
            StepRemote(now);
            Publish(now);
            if (now >= _nextPurge) Purge(now);
        }

        static void StepUnit(MercUnit u, MercSeat st, MercCarrier oc, float now, int k)
        {
            bool station = MercAA.IsVehicle(u.Order);
            if (station) oc = Assigned(u, now);
            bool drive = u.Order.Mode == MercOrder.Drive;
            if (drive) oc = MercDrive.Carrier(u);
            bool want = (u.Order.Mode == MercOrder.Vehicle || station || (drive && oc != null)) && !u.Deserting && !u.Rally;
            if (station && MercStationPlan.Retreat(NpcWar.MercSeatHealth(u.Ai), u.AARetreat, false))
            { u.AARetreat = true; want = false; }
            else if (station) u.AARetreat = false;
            MercCarrier c = st.Carrier;
            if (c != null)
            {
                Motion(c, now);
                if (!Alive(c)) { Lost(u, st, now, k); return; }
                Maintain(st, now);
                if (!want || oc != c)
                {
                    if (station && u.AARetreat && st.Gunner && c.GunKind == MercCarrier.GunTechnical)
                    {
                        int shelter = CabSeat(c, st);
                        if (shelter >= 0) { MoveSeat(u, st, shelter); st.SeatedAt = now; return; }
                    }
                    // Out when it stands; an aircraft in the air keeps him as
                    // cargo until it is down.
                    if (Standing(c, LeaveSpeed)) GetOut(u, st, k, now, false);
                    return;
                }
                if (!SeatFree(c, st.Seat, st))
                {
                    int seat = PickSeat(c, st);
                    if (seat >= 0) MoveSeat(u, st, seat);
                    else GetOut(u, st, k, now, true);
                    return;
                }
                // X, survival first: a badly hurt merc does not stay up at the
                // technical's open MG - he takes a free cab seat (the gun is
                // left to a fitter man) and does not come back to it hurt.
                if (st.Gunner && TooHurtForGun(c, st, HurtLeave))
                {
                    int cab = CabSeat(c, st);
                    if (cab >= 0)
                    {
                        Toast(u.Name + Loc.T(" ранен и уходит от пулемёта", " is hurt and leaves the MG"));
                        MoveSeat(u, st, cab);
                        st.SeatedAt = now;
                        return;
                    }
                }
                // The gun came free (the owner left the turret for the wheel,
                // a gunner got out): the first of ours on a passenger seat
                // takes it after a moment, so an armed vehicle is not ridden
                // with its gun idle.
                if (!drive && !st.Gunner && c.GunKind != MercCarrier.GunNone && now - st.SeatedAt > 2f
                    && SeatFree(c, c.GunSeat, st) && !TooHurtForGun(c, st, HurtReturn))
                {
                    MoveSeat(u, st, c.GunSeat);
                    st.SeatedAt = now;
                }
                return;
            }
            if (!want || oc == null || now < st.NextBoard || !Standing(oc, BoardSpeed))
            {
                st.Boarding = null;
                return;
            }
            Vector3 at = u.Ai.transform.position;
            float d = Flat(at - oc.Root.position);
            if (d > BoardReach + oc.Radius) { st.Boarding = null; return; }
            int desired = Mathf.RoundToInt(u.Order.Facing.z) - 1;
            int free = station || drive ? (SeatFree(oc, desired, st) ? desired : -1) : PickSeat(oc, st);
            if (free < 0)
            {
                st.Boarding = null;
                st.NextBoard = now + 3f;
                if (!st.NoSeatSaid)
                {
                    st.NoSeatSaid = true;
                    Toast(u.Name + Loc.T(": нет свободного места - идёт пешком.", ": no free seat - follows on foot."));
                }
                return;
            }
            if (st.Boarding != oc) { st.Boarding = oc; st.BoardUntil = now + BoardSeconds + d / 8f; }
            st.BoardSeat = free;
            st.BoardAt = BoardPoint(oc, free, at);
            if (Flat(at - st.BoardAt) < 7f || d < oc.Radius + 6f) { Board(u, st, oc, free, now); return; }
            if (now > st.BoardUntil)
            {
                st.Boarding = null;
                st.NextBoard = now + 6f;
            }
        }

        static void Board(MercUnit u, MercSeat st, MercCarrier c, int seat, float now)
        {
            st.Boarding = null;
            st.Carrier = c; st.Seat = seat; st.SeatedAt = now;
            st.Hidden = c.Closed;
            st.Gunner = c.GunKind != MercCarrier.GunNone && seat == c.GunSeat;
            st.DriveEscort = u.Order.Mode == MercOrder.Drive && seat > 0;
            st.NoSeatSaid = false;
            ClearGun(st);
            st.Drill.Reset();
            if (st.Gunner) Drill(c, st.Drill);
            NpcWar.MercRideHold(u);
            ApplySeat(st, true);
            Place(st, true);
            _dirty = true;
            Toast(u.Name + Loc.T(" садится: ", " boards the ") + Loc.T(c.Ru, c.En)
                + (st.Gunner ? Loc.T(" (за оружием)", " (on the gun)") : ""));
            RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " boarded the " + c.En + " (view " + c.View + ") seat "
                + seat + (st.Gunner ? ", gunner" : "") + ".");
        }

        static void MoveSeat(MercUnit u, MercSeat st, int seat)
        {
            MercCarrier c = st.Carrier;
            if (st.Gunner) StandDownGun(st);
            st.Seat = seat;
            st.Gunner = c.GunKind != MercCarrier.GunNone && seat == c.GunSeat;
            ClearGun(st);
            st.Drill.Reset();
            if (st.Gunner) Drill(c, st.Drill);
            _dirty = true;
            RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " moved to seat " + seat + " of the " + c.En
                + (st.Gunner ? " - the gun." : "."));
        }

        static void GetOut(MercUnit u, MercSeat st, int k, float now, bool noSeat)
        {
            MercCarrier c = st.Carrier;
            Vector3 spot = OutSpot(c, k, u.Ai.transform.position);
            Unseat(u, st, spot, ShieldSeconds, now);
            if (noSeat)
            {
                st.NextBoard = now + 5f;
                Toast(u.Name + Loc.T(": место занял игрок - идёт пешком.", ": a player took his seat - follows on foot."));
            }
            RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " got out of the " + c.En + " at " + spot + (noSeat ? " (no seat left)." : "."));
        }

        /// <summary>The vehicle is gone: a closed hull or an aircraft takes
        /// him with it (D14); an open one sets him down beside the wreck.</summary>
        static void Lost(MercUnit u, MercSeat st, float now, int k)
        {
            MercCarrier c = st.Carrier;
            // Taken away (admin, cleanup, its owner left) is not destroyed:
            // he is set down where he sat, alive.
            bool removed = c == null || c.Go == null || c.Root == null;
            bool die = !removed && (c.Closed || c.Air);
            Vector3 here = u.Ai.transform.position, spot;
            if (removed) spot = RevivalGroundEnemies.TryGround(here, 12f, out spot) ? spot : here;
            else spot = OutSpot(c, k, here);
            string what = removed ? "vehicle" : c.En;
            Unseat(u, st, spot, removed ? ShieldSeconds : 0f, now);
            RevivalPlugin.L.LogInfo("Mercs: " + u.Name + "'s " + what + (removed ? " is gone - set down where he sat."
                : " was destroyed - " + (die ? "he dies with it." : "set down beside it.")));
            if (!die) return;
            st.Dying = true;
            try
            {
                for (int i = 0; i < 3 && NpcWar.MercAlive(u.Ai); i++)
                    Turret.TryDamage(u.Ai.gameObject, "NPC_AI2", "ApplyDamage", 100000f);
            }
            finally { st.Dying = false; }
        }

        /// <summary>Owner side: off the seat, onto <paramref name="spot"/>,
        /// his own navigation back from where he now stands.</summary>
        static void Unseat(MercUnit u, MercSeat st, Vector3 spot, float shield, float now)
        {
            if (st.Gunner) StandDownGun(st);
            ReleaseSeat(st);
            ClearGun(st);
            Component ai = u.Ai;
            UnityEngine.Object live = ai;
            if (live != null)
            {
                Transform tr = ai.transform;
                tr.position = spot;
                Vector3 f = tr.forward; f.y = 0f;
                if (f.sqrMagnitude > 0.0001f) tr.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
                NavMeshAgent agent = GepardCrew.Agent(ai);
                try
                {
                    if (agent != null)
                    {
                        agent.updatePosition = true;
                        agent.updateRotation = true;
                        if (agent.isActiveAndEnabled) agent.Warp(spot);
                    }
                }
                catch { }
                if (NpcWar.MercAlive(ai)) NpcWar.MercRideHold(u);
            }
            st.ShieldUntil = now + shield;
            _dirty = true;
        }

        /// <summary>From Mercs.Despawn / OnDeath: never leave a body on a seat.</summary>
        internal static void Forget(MercUnit u)
        {
            if (u == null) return;
            MercDrive.Forget(u);
            MercSeat st = u.Ride;
            st.Boarding = null;
            if (st.Carrier == null) return;
            UnityEngine.Object live = u.Ai;
            try
            {
                if (live != null && st.Carrier.Root != null)
                    Unseat(u, st, OutSpot(st.Carrier, 0, u.Ai.transform.position), 0f, Time.time);
                else ReleaseSeat(st);
            }
            catch (Exception ex) { Warn("forget " + u.Name, ex); ReleaseSeat(st); }
            _dirty = true;
        }

        // ======================================================= every client
        /// <summary>On the seat: agent parked, collisions with the hull off,
        /// hidden in a closed hull. Owner and peers alike.</summary>
        static void ApplySeat(MercSeat st, bool owner)
        {
            Component ai = st.Ai;
            UnityEngine.Object live = ai;
            if (live == null || st.Carrier == null) return;
            GepardCrew.Parken(ai);
            if (owner)
            {
                NavMeshAgent agent = GepardCrew.Agent(ai);
                try { if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.ResetPath(); }
                catch { }
            }
            if (st.Hidden || st.Carrier.Air) _hidden.Add(ai.GetInstanceID());
            st.NextPass = 0f;
            Maintain(st, Time.time);
        }

        /// <summary>Re-applied every 2 s: the game switches colliders and
        /// renderers of far NPCs off and on, which forgets both.</summary>
        static void Maintain(MercSeat st, float now)
        {
            if (now < st.NextPass) return;
            st.NextPass = now + 2f;
            Component ai = st.Ai;
            UnityEngine.Object live = ai;
            MercCarrier c = st.Carrier;
            if (live == null || c == null || c.Go == null) return;
            GepardCrew.Parken(ai);
            // THE MAN AND THIS HULL DO NOT COLLIDE. A body held inside a hull's
            // colliders is what shoves a vehicle off the road and throws a man
            // off it; a bullet is a ray, not a collision, so he can still be
            // shot where he is visible. The pairs are left ignored after the
            // ride: restoring them next to the hull could push him away, and the
            // game forgets them anyway when either collider is switched off.
            try
            {
                if (c.Cols == null || c.Cols.Length == 0 || c.Cols[0] == null)
                    c.Cols = c.Go.GetComponentsInChildren<Collider>(true);
                Collider[] mine = ai.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < mine.Length; i++)
                {
                    Collider a = mine[i];
                    if (a == null || a.isTrigger || !a.enabled || !a.gameObject.activeInHierarchy) continue;
                    for (int j = 0; j < c.Cols.Length; j++)
                    {
                        Collider b = c.Cols[j];
                        if (b == null || b.isTrigger || !b.enabled || !b.gameObject.activeInHierarchy) continue;
                        try { Physics.IgnoreCollision(a, b, true); }
                        catch { }
                    }
                }
            }
            catch { }
            if (!st.Hidden) return;
            Renderer[] rs = ai.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                if (!st.Off.Contains(r)) st.Off.Add(r);
            }
        }

        static void ReleaseSeat(MercSeat st)
        {
            for (int i = 0; i < st.Off.Count; i++) if (st.Off[i] != null) st.Off[i].enabled = true;
            st.Off.Clear();
            Unholster(st);
            UnityEngine.Object live = st.Ai;
            if (live != null) _hidden.Remove(st.Ai.GetInstanceID());
            if (live != null) SeatBinding.Detach(st.Ai.transform);
            st.Carrier = null; st.Seat = -1;
            st.Hidden = false; st.Gunner = false; st.Firing = false; st.DriveEscort = false;
        }

        /// <summary>Another owner's merc off his seat on this client: his
        /// navigation back from where he is (TechnicalCrew.Absteigen's way);
        /// his owner's position sync carries him on from there.</summary>
        static void ReleaseRemote(MercSeat st)
        {
            bool seated = st.Carrier != null;
            ReleaseSeat(st);
            Component ai = st.Ai;
            UnityEngine.Object live = ai;
            if (!seated || live == null) return;
            NavMeshAgent agent = GepardCrew.Agent(ai);
            try
            {
                if (agent == null) return;
                agent.updatePosition = true;
                agent.updateRotation = true;
                if (agent.isActiveAndEnabled) agent.Warp(ai.transform.position);
            }
            catch { }
        }

        /// <summary>From RevivalPlugin.LateUpdate, every client: each rider on
        /// his seat after the animator; the owner's gunners at work.</summary>
        internal static void LateFrame()
        {
            if (_riders.Count == 0 && _remote.Count == 0) return;
            float now = Time.time;
            try
            {
                int frame = Time.frameCount;
                for (int i = 0; i < _riders.Count; i++)
                {
                    MercUnit u = _riders[i];
                    MercSeat st = u.Ride;
                    if (st.Carrier == null || st.PlacedFrame == frame) continue;
                    UnityEngine.Object live = u.Ai;
                    if (live == null || st.Carrier.Root == null) continue;
                    OwnerFrame(u, st, now);
                }
                foreach (MercSeat st in _remote.Values)
                {
                    if (st.Carrier == null || st.PlacedFrame == frame) continue;
                    UnityEngine.Object live = st.Ai;
                    if (live == null || st.Carrier.Root == null) continue;
                    RemoteFrame(st);
                }
            }
            catch (Exception ex) { Warn("frame", ex); }
        }

        /// <summary>X: from Technical.LateFrame, BEFORE TechnicalGun.LateAll -
        /// the merc on a technical's MG: his gun laid and he placed first, so
        /// TechnicalGun walks him behind the pintle, stands him up and solves
        /// his hands onto the grips of THIS frame's mount, as it does for a
        /// player and a crew gunner. LateFrame skips whom this pass did; with
        /// the technical switched off LateFrame does him itself.</summary>
        internal static void LateTechnical()
        {
            if (_riders.Count == 0 && _remote.Count == 0) return;
            FrameProf.S(FrameProf.S_MercsL);
            try
            {
                float now = Time.time;
                int frame = Time.frameCount;
                for (int i = 0; i < _riders.Count; i++)
                {
                    MercUnit u = _riders[i];
                    MercSeat st = u.Ride;
                    if (!AtTechnicalGun(st)) continue;
                    UnityEngine.Object live = u.Ai;
                    if (live == null || st.Carrier.Root == null) continue;
                    st.PlacedFrame = frame;
                    OwnerFrame(u, st, now);
                }
                foreach (MercSeat st in _remote.Values)
                {
                    if (!AtTechnicalGun(st)) continue;
                    UnityEngine.Object live = st.Ai;
                    if (live == null || st.Carrier.Root == null) continue;
                    st.PlacedFrame = frame;
                    RemoteFrame(st);
                }
            }
            catch (Exception ex) { Warn("technical frame", ex); }
            finally { FrameProf.E(FrameProf.S_MercsL); }
        }

        /// <summary>The gun laid first, then the man put on his seat facing
        /// it: his heading is this frame's mount, not the last one's.</summary>
        static void OwnerFrame(MercUnit u, MercSeat st, float now)
        {
            if (st.Gunner)
            {
                try { Gun(u, st, now); }
                catch (Exception ex) { Warn("gun " + u.Name, ex); ClearGun(st); }
            }
            Place(st, true);
        }

        static void RemoteFrame(MercSeat st)
        {
            if (st.Gunner && st.Carrier.GunKind == MercCarrier.GunTechnical) SlewPintle(st.Carrier, st.GunYaw, st.GunPitch);
            Place(st, false);
        }

        // X: a merc leaves the technical's MG under this much health and does
        // not take it (again) under the second - survival first.
        const float HurtLeave = 0.35f, HurtReturn = 0.6f;

        /// <summary>Only the technical's gunner stands in the open; a closed
        /// hull's gunner cannot be hurt (D14).</summary>
        static bool TooHurtForGun(MercCarrier c, MercSeat st, float below)
        {
            if (c.GunKind != MercCarrier.GunTechnical) return false;
            Component ai = st.Ai;
            return ai != null && NpcWar.MercSeatHealth(ai) < below;
        }

        /// <summary>A free seat that is not the gun's (the driver's excepted).</summary>
        static int CabSeat(MercCarrier c, MercSeat st)
        {
            for (int i = 1; i < c.Seats; i++)
                if (i != c.GunSeat && SeatFree(c, i, st)) return i;
            return -1;
        }

        /// <summary>Standing at the technical's MG (not a closed hull).</summary>
        static bool AtTechnicalGun(MercSeat st)
        {
            MercCarrier c = st.Carrier;
            return c != null && st.Gunner && !st.Hidden && c.GunKind == MercCarrier.GunTechnical && st.Seat == c.GunSeat;
        }

        /// <summary>X: TechnicalGun.GunnerBody's third source, after a player
        /// and the riding crew - the merc (ours or another owner's) at this
        /// technical's MG, so the standing pose, the place behind the pintle
        /// and the hands on the grips are his too. Null: none.</summary>
        internal static GameObject TechnicalGunner(Component vgs)
        {
            if (ReferenceEquals(vgs, null) || (_riders.Count == 0 && _remote.Count == 0)) return null;
            for (int i = 0; i < _riders.Count; i++)
            {
                MercSeat st = _riders[i].Ride;
                if (!AtTechnicalGun(st) || !ReferenceEquals(st.Carrier.Vgs, vgs)) continue;
                Component ai = st.Ai;
                if (ai != null) return ai.gameObject;
            }
            foreach (MercSeat st in _remote.Values)
            {
                if (!AtTechnicalGun(st) || !ReferenceEquals(st.Carrier.Vgs, vgs)) continue;
                Component ai = st.Ai;
                if (ai != null) return ai.gameObject;
            }
            return null;
        }

        // ============================================================ holster
        static readonly List<Renderer> _weapon = new List<Renderer>();

        /// <summary>X: the merc's own rifle is not shown while his hands are
        /// on the MG's grips - its renderers under Weapons_HelperR go off on
        /// every client (TechnicalCrew.Entwaffnen's way, no RPC), looked at
        /// five times a second because a redraw instantiates a fresh model,
        /// and come back on when he leaves the gun.</summary>
        static void Holster(MercSeat st, bool want, float now)
        {
            if (!want)
            {
                if (st.Holstered.Count > 0 || st.Hand != null) Unholster(st);
                return;
            }
            if (now < st.NextHolster) return;
            st.NextHolster = now + 0.2f;
            Component ai = st.Ai;
            if (ai == null) return;
            if (st.Hand == null) st.Hand = TechnicalCrew.WeaponHand(ai);
            if (st.Hand == null) return;
            _weapon.Clear();
            st.Hand.GetComponentsInChildren<Renderer>(true, _weapon);
            for (int i = 0; i < _weapon.Count; i++)
            {
                Renderer r = _weapon[i];
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                st.Holstered.Add(r);
            }
            _weapon.Clear();
            for (int i = st.Holstered.Count - 1; i >= 0; i--)
                if (st.Holstered[i] == null) st.Holstered.RemoveAt(i);
        }

        static void Unholster(MercSeat st)
        {
            for (int i = 0; i < st.Holstered.Count; i++)
                if (st.Holstered[i] != null) st.Holstered[i].enabled = true;
            st.Holstered.Clear();
            st.Hand = null;
            st.NextHolster = 0f;
        }

        static void Place(MercSeat st, bool owner)
        {
            Vector3 pos; Quaternion rot;
            if (!SeatPose(st, out pos, out rot)) return;
            Component ai = st.Ai;
            Transform tr = ai.transform;
            if (owner && (tr.position - pos).sqrMagnitude > 900f)
            {
                // A real jump (the moment he gets in, or the vehicle was put
                // somewhere): the parked agent comes along, or it would drag
                // him back along a path of its own when he gets out.
                NavMeshAgent agent = GepardCrew.Agent(ai);
                try { if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) { agent.ResetPath(); agent.Warp(pos); } }
                catch { }
            }
            MercCarrier c = st.Carrier;
            Transform anchor = c.Air ? SeatBinding.AircraftSeat(c.Root, c.Kind == MercCarrier.Plane, st.Seat)
                : c.SeatPoints.GetChild(st.Seat);
            if (AtTechnicalGun(st)) SeatBinding.BindWorld(tr, anchor, c.Root, pos, rot);
            else SeatBinding.BindSeat(tr, anchor, c.Root);
            if (owner && !DriveRifle(st)) GepardCrew.Ruhig(ai);
            Holster(st, AtTechnicalGun(st), Time.time);
            if (st.Hidden) return;
            // The technical's gunner stands at the pintle (TechnicalGun poses
            // him and puts his hands on the grips); everybody else sits.
            if (st.Carrier.GunKind == MercCarrier.GunTechnical && st.Seat == st.Carrier.GunSeat) return;
            if (DriveRifle(st)) return; // Native upper-body rifle aim/fire owns the pose.
            TechnicalCrew.Sitzen(ai, st.Seat == 0 ? 0 : 1);
        }

        // ================================================================ gun
        static void ClearGun(MercSeat st)
        {
            st.Target = null; st.TargetNpc = null; st.TargetCarrier = null;
            st.TargetPlayer = false; st.Suppress = false; st.Firing = false; st.TargetHits = 0;
            st.Held = 0f; st.NextScan = 0f;
            st.Drill.BreakOff(Time.time);
        }

        static void StandDownGun(MercSeat st)
        {
            MercCarrier c = st.Carrier;
            if (c == null || c.Root == null) return;
            if (c.GunKind == MercCarrier.GunGepard && c.Rig != null)
            {
                c.Rig.LocalControl = false;
                GepardNet.SendPose(c.Root, c.Rig.Yaw, c.Rig.Pitch, c.Rig.TrackYaw, false, false);
            }
        }

        static float GunRange(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical: return TechnicalGun.CfgRange == null ? 600f : TechnicalGun.CfgRange.Value;
                case MercCarrier.GunBtr: return RevivalPlugin.CfgTurretRange == null ? 800f : RevivalPlugin.CfgTurretRange.Value;
                case MercCarrier.GunTank: return RevivalPlugin.CfgTankRange == null ? 1000f : RevivalPlugin.CfgTankRange.Value;
                case MercCarrier.GunGepard:
                    // Ground work only: the Gepard's reach against a man is a
                    // rifle-and-a-half, not its 2.6 km air envelope.
                    return Mathf.Min(1400f, Gepard.CfgMaxRange == null ? 2600f : Gepard.CfgMaxRange.Value);
            }
            return 0f;
        }

        static float Traverse(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical: return TechnicalGun.CfgTurnSpeed == null ? 120f : Mathf.Max(10f, TechnicalGun.CfgTurnSpeed.Value);
                case MercCarrier.GunBtr: return RevivalPlugin.CfgTurretTurnSpeed == null ? 60f : Mathf.Max(5f, RevivalPlugin.CfgTurretTurnSpeed.Value);
                case MercCarrier.GunTank: return RevivalPlugin.CfgTankTurnSpeed == null ? 30f : Mathf.Max(5f, RevivalPlugin.CfgTankTurnSpeed.Value);
                default: return Gepard.CfgTurnSpeed == null ? 90f : Mathf.Max(5f, Gepard.CfgTurnSpeed.Value);
            }
        }

        static Vector3 Muzzle(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical:
                    {
                        Vector3 at, bore;
                        if (BtrGun.TechnicalActive && BtrGun.TechnicalMuzzle(c.Root, out at, out bore)) return at;
                        if (c.Gun != null) return c.Gun.TransformPoint(TechnicalModel.MuzzleLocal);
                        return c.Mount.position;
                    }
                case MercCarrier.GunGepard:
                    return c.Rig.Muzzle(c.GunIdx);
                default:
                    {
                        Vector3 dir = Bore(c);
                        if (c.TurretRenderer == null) return c.Turrets[0].position + dir;
                        Bounds b = c.TurretRenderer.bounds;
                        float reach = Vector3.Dot(b.extents, new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
                        return b.center + dir * (reach + 0.5f);
                    }
            }
        }

        static Vector3 Bore(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical: return c.Gun != null ? c.Gun.forward : c.Mount.forward;
                case MercCarrier.GunGepard: return c.Rig.Bore(c.GunIdx);
                default: return c.Turrets[0].TransformDirection(new Vector3(0f, -1f, 0f)).normalized;
            }
        }

        static bool GunReady(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical: return c.Mount != null && c.Mount.parent != null;
                case MercCarrier.GunGepard: return c.Rig != null;
                case MercCarrier.GunBtr:
                case MercCarrier.GunTank: return c.Turrets != null && c.Turrets.Length > 0 && c.Turrets[0] != null && c.Turrets[0].parent != null;
            }
            return false;
        }

        /// <summary>One gunner, one frame: pick (3 Hz), lay, hold for the
        /// reaction time, fire when laid and seen - GunnerAI's rules, the
        /// player gun's numbers.</summary>
        static void Gun(MercUnit u, MercSeat st, float now)
        {
            MercCarrier c = st.Carrier;
            MercGunDrill drill = st.Drill;
            if (MercAA.IsVehicle(u.Order) && u.AARetreat) { drill.BreakOff(now); st.Firing = false; return; }
            if (!GunReady(c) || PlayerIn(c, st.Seat)) { drill.BreakOff(now); st.Firing = false; return; }
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float range = GunRange(c);
            Vector3 muzzle = Muzzle(c);
            if (now >= st.NextScan)
            {
                st.NextScan = now + 0.3f + UnityEngine.Random.value * 0.1f;
                GunnerAI.Sync();
                if (!drill.InBurst) Drill(c, drill);
                Pick(u, st, c, muzzle, range, now);
            }
            if (st.Target == null || !st.Target.gameObject.activeInHierarchy
                || (st.TargetNpc != null && !NpcWar.MercAlive(st.TargetNpc)))
            {
                if (st.Target != null)
                {
                    GunKilled(u, st, st.Target, st.TargetNpc, st.TargetPlayer);
                    st.Target = null; st.Held = 0f;
                }
                st.Firing = false;
                drill.BreakOff(now);
                // Quiet: a fresh belt now rather than an empty one in the next fight.
                if (drill.Idle(now, now - st.LastContact))
                    RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " loads a fresh belt on the " + c.En + "'s gun.");
                drill.Ready(now);
                Rest(c, st, dt, now);
                PublishPose(c, st, now, false);
                return;
            }
            st.LastContact = now;
            Vector3 real = AimPoint(st.Target, st.TargetNpc, st.TargetCarrier);
            TrackSpeed(st, real, now);
            Vector3 point = st.Suppress ? st.LastKnown : real;
            Vector3 to = point - muzzle;
            if (to.sqrMagnitude < 0.0001f) return;
            Vector3 dir = to.normalized;
            Lay(c, st, dir, dt);
            PublishPose(c, st, now, true);
            if (!st.Suppress) st.Held += dt;
            float dist = to.magnitude;
            if (st.Held < GunnerAI.Reaction(st.TargetPlayer, dist, st.Vis)) return;
            // X: bursts at the gun's cadence, a belt and its reload
            // (Revival.MercGunDrillCore.cs). A burst opens laid within the
            // gun's tolerance and runs on within twice that, walked onto
            // the target by the laying above.
            drill.Ready(now);                       // a reload that has run out is done
            if (drill.Reloading || now < drill.NextShot) { st.Firing = drill.InBurst; return; }
            Vector3 bore = Bore(c);
            if (!drill.Laid(Vector3.Angle(bore, dir))) { drill.BreakOff(now); st.Firing = false; return; }
            if (!drill.InBurst)
            {
                // Sight once a burst: its rounds go where it was opened.
                float vis = Sight(c, st, muzzle, real);
                if (vis > 0f)
                {
                    st.Suppress = false; st.Vis = vis; st.LastSeen = now; st.LastKnown = real;
                }
                else
                {
                    if (now - st.LastSeen > GunnerAI.T.SuppressSeconds) { st.Target = null; return; }
                    st.Suppress = true;
                }
                if (c.GunKind == MercCarrier.GunTank && !BlastSafe(u, c, point))
                {
                    drill.NextShot = now + 0.5f;
                    return;
                }
            }
            Shoot(u, st, c, muzzle, bore, dist, now);
        }

        /// <summary>X: the gun's numbers for the drill - the player gun's
        /// cadence, the NPC gunners' burst lengths, the gun's belt and reload.
        /// Read on boarding and on the 3 Hz scan between bursts.</summary>
        static void Drill(MercCarrier c, MercGunDrill d)
        {
            int lo, hi;
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical:
                    BtrGun.TechnicalBurstRange(TechnicalCrew.CfgBurst == null ? 8 : TechnicalCrew.CfgBurst.Value, out lo, out hi);
                    d.Configure(BtrGun.TechnicalInterval(TechnicalGun.CfgDelay == null ? 0.11f : TechnicalGun.CfgDelay.Value),
                        lo, hi, TechnicalCrew.CfgBurstPause == null ? 1.1f : TechnicalCrew.CfgBurstPause.Value,
                        TechnicalGun.CfgBelt == null ? 50 : TechnicalGun.CfgBelt.Value,
                        TechnicalGun.CfgReload == null ? 4.5f : TechnicalGun.CfgReload.Value, LaidWithin(c));
                    return;
                case MercCarrier.GunBtr:
                    BtrGun.NpcBurstRange(5, out lo, out hi);
                    d.Configure(BtrGun.ShotInterval(RevivalPlugin.CfgTurretDelay == null ? 0.12f : RevivalPlugin.CfgTurretDelay.Value),
                        lo, hi, 1.1f, KpvtBelt, KpvtReload, LaidWithin(c));
                    return;
                case MercCarrier.GunTank:
                    {
                        // One shell, then the loader: the reload IS the interval.
                        float load = Mathf.Max(1f, RevivalPlugin.CfgTankDelay == null ? 6f : RevivalPlugin.CfgTankDelay.Value);
                        d.Configure(load, 1, 1, load, 1, load, LaidWithin(c));
                        return;
                    }
                default:
                    d.Configure(60f / Mathf.Max(60f, Gepard.CfgRpm == null ? 1100f : Gepard.CfgRpm.Value),
                        6, 10, 1.1f,
                        Gepard.CfgRoundsPerBelt == null ? 200 : Gepard.CfgRoundsPerBelt.Value,
                        Gepard.CfgReloadSeconds == null ? 6f : Gepard.CfgReloadSeconds.Value, LaidWithin(c));
                    return;
            }
        }

        // The MTW's KPVT has no belt of its own in the player turret: the
        // real one's 50-round belt, and a reload in a closed turret.
        const int KpvtBelt = 50;
        const float KpvtReload = 5.5f;

        static float LaidWithin(MercCarrier c)
        {
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical: return 3.5f;
                case MercCarrier.GunTank: return 1.5f;
                default: return 2.5f;
            }
        }

        /// <summary>A man's chest: NpcWar's height on an NPC capsule, the
        /// technical gunner's on a player (lower when he sits in a car).</summary>
        static Vector3 AimPoint(Transform t, Component npc, Component carrier)
        {
            if (npc != null) return t.position + Vector3.up * 3.0f;
            return t.position + Vector3.up * (carrier != null ? 0.8f : 0.9f);
        }

        static void TrackSpeed(MercSeat st, Vector3 p, float now)
        {
            float dt = now - st.SpeedAt;
            if (dt < 0.25f) return;
            float v = dt < 2f ? Vector3.Distance(p, st.SpeedFrom) / dt / 2.8f : 0f;
            st.TargetSpeed = dt < 2f ? (st.TargetSpeed + v) * 0.5f : 0f;
            st.SpeedFrom = p; st.SpeedAt = now;
        }

        /// <summary>Target choice: NpcWar lists the hostiles under the merc
        /// rules, nearest first; the first one in sight from the muzzle wins.</summary>
        static readonly Transform[] _cand = new Transform[6];
        static readonly Component[] _candNpc = new Component[6];
        static readonly bool[] _candPlayer = new bool[6];

        static void Pick(MercUnit u, MercSeat st, MercCarrier c, Vector3 muzzle, float range, float now)
        {
            bool keep = st.Target != null && NpcWar.MercGunKeep(u, st.Target, st.TargetNpc, st.TargetPlayer);
            if (keep && now - st.LastSeen < 1.5f
                && (st.Target.position - muzzle).sqrMagnitude < range * range * 1.2f) return;
            int n = NpcWar.MercGunCandidates(u, muzzle, range, _cand, _candNpc, _candPlayer);
            Transform had = st.Target;
            Component hadNpc = st.TargetNpc;
            bool hadPlayer = st.TargetPlayer;
            st.Target = null;
            for (int i = 0; i < n && i < 3; i++)
            {
                Component carrier = _candPlayer[i] ? GunnerAI.Carrier(_cand[i]) : null;
                float vis = Sight(c, st, muzzle, AimPoint(_cand[i], _candNpc[i], carrier), _cand[i], carrier);
                if (vis <= 0f) continue;
                st.Target = _cand[i]; st.TargetNpc = _candNpc[i]; st.TargetPlayer = _candPlayer[i];
                st.TargetCarrier = carrier;
                st.Vis = vis; st.LastSeen = now; st.Suppress = false;
                break;
            }
            if (st.Target != had)
            {
                if (had != null) GunKilled(u, st, had, hadNpc, hadPlayer);
                st.Held = 0f; st.TargetHits = 0;
                st.Drill.BreakOff(now);
                if (st.Target != null) MercNotify.GunTarget(u, st.Target.position);
                if (st.Target != null)
                    RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " on the " + c.En + "'s gun engages "
                        + (st.TargetPlayer ? "a player" : "an NPC") + " at "
                        + (Vector3.Distance(st.Target.position, muzzle) / 2.8f).ToString("0") + " m.");
            }
        }

        static float Sight(MercCarrier c, MercSeat st, Vector3 from, Vector3 to)
        {
            return Sight(c, st, from, to, st.Target, st.TargetCarrier);
        }

        /// <summary>0 = blocked, else how much of him shows through the
        /// vegetation (GunnerAI.Transmit). Our own hull, our own men and a
        /// target's own car do not block.</summary>
        static float Sight(MercCarrier c, MercSeat st, Vector3 from, Vector3 to, Transform target, Component carrier)
        {
            if (target == null) return 0f;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len > 0.5f)
            {
                Vector3 hit;
                float at;
                GameObject go = Ray(c, from, d / len, len + 1.5f, out hit, out at);
                if (go != null && at < len - 1.2f)
                {
                    Transform t = go.transform;
                    bool on = t == target || t.IsChildOf(target) || (carrier != null && GunnerAI.OnCarrier(go, carrier));
                    if (!on) return 0f;
                }
            }
            float vis = GunnerAI.Transmit(from, to);
            return vis >= GunnerAI.T.SightThreshold ? vis : 0f;
        }

        /// <summary>A ray stepped past our own hull and every merc riding it.</summary>
        static GameObject Ray(MercCarrier c, Vector3 from, Vector3 dir, float range, out Vector3 point, out float dist)
        {
            point = Vector3.zero; dist = 0f;
            Vector3 start = from;
            float rest = range, done = 0f;
            for (int step = 0; step < 6 && rest > 0f; step++)
            {
                // The same cast as Turret.RaycastObject, which NpcWar's rifle
                // and every vehicle gun use: what they hit, this hits.
                RaycastHit hit;
                if (!Physics.Raycast(start, dir, out hit, rest)) return null;
                Transform t = hit.collider.transform;
                if (t.IsChildOf(c.Root) || RiderOf(c, t))
                {
                    float adv = hit.distance + 0.25f;
                    start += dir * adv; rest -= adv; done += adv;
                    continue;
                }
                point = hit.point; dist = done + hit.distance;
                return hit.collider.gameObject;
            }
            return null;
        }

        static bool RiderOf(MercCarrier c, Transform t)
        {
            for (int i = 0; i < _riders.Count; i++)
            {
                MercSeat st = _riders[i].Ride;
                UnityEngine.Object live = st.Ai;
                if (st.Carrier == c && live != null && t.IsChildOf(st.Ai.transform)) return true;
            }
            foreach (MercSeat st in _remote.Values)
            {
                UnityEngine.Object live = st.Ai;
                if (st.Carrier == c && live != null && t.IsChildOf(st.Ai.transform)) return true;
            }
            return false;
        }

        /// <summary>The tank's HE shell is not fired where its blast would
        /// reach the owner, a whitelisted player, one of our mercs, or our own
        /// hull.</summary>
        static bool BlastSafe(MercUnit u, MercCarrier c, Vector3 point)
        {
            float r = (RevivalPlugin.CfgTankExplosionRadius == null ? 10f : RevivalPlugin.CfgTankExplosionRadius.Value) + 14f;
            float rr = r * r;
            if ((c.Root.position - point).sqrMagnitude < (r + c.Radius) * (r + c.Radius)) return false;
            if (u.Owner != null && (u.Owner.position - point).sqrMagnitude < rr) return false;
            for (int i = 0; i < _riders.Count; i++)
            {
                UnityEngine.Object live = _riders[i].Ai;
                if (live != null && (_riders[i].Ai.transform.position - point).sqrMagnitude < rr) return false;
            }
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject p = players[i];
                if (p == null || (p.transform.position - point).sqrMagnitude >= rr) continue;
                string steam = Mercs.SteamOf(p);
                if (steam != null && Mercs.Whitelisted(steam)) return false;
            }
            return true;
        }

        /// <summary>Turn the gun toward <paramref name="dir"/> at its own rate.</summary>
        static void Lay(MercCarrier c, MercSeat st, Vector3 dir, float dt)
        {
            float step = Traverse(c) * dt;
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical:
                    {
                        Vector3 local = c.Mount.parent.InverseTransformDirection(dir);
                        if (local.sqrMagnitude < 0.000001f) return;
                        local.Normalize();
                        float wantPitch = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
                        Vector3 flat = new Vector3(local.x, 0f, local.z);
                        if (flat.sqrMagnitude < 0.000001f) return;
                        float wantYaw = Quaternion.LookRotation(flat.normalized, Vector3.up).eulerAngles.y;
                        float min = TechnicalGun.CfgPitchMin == null ? -12f : TechnicalGun.CfgPitchMin.Value;
                        float max = TechnicalGun.CfgPitchMax == null ? 55f : TechnicalGun.CfgPitchMax.Value;
                        SlewPintle(c, wantYaw, Mathf.Clamp(wantPitch, min, max), step);
                        st.GunYaw = c.Mount.localRotation.eulerAngles.y;
                        st.GunPitch = c.Gun == null ? 0f : -Mathf.DeltaAngle(0f, c.Gun.localRotation.eulerAngles.x);
                        return;
                    }
                case MercCarrier.GunGepard:
                    {
                        GepardRig rig = c.Rig;
                        rig.LocalControl = true;
                        Vector3 l = rig.transform.InverseTransformDirection(dir);
                        float wantYaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
                        float wantPitch = Mathf.Atan2(l.y, Mathf.Sqrt(l.x * l.x + l.z * l.z)) * Mathf.Rad2Deg;
                        float pmin = Gepard.CfgPitchMin == null ? -5f : Gepard.CfgPitchMin.Value;
                        float pmax = Gepard.CfgPitchMax == null ? 85f : Gepard.CfgPitchMax.Value;
                        float elev = (Gepard.CfgElevSpeed == null ? 60f : Gepard.CfgElevSpeed.Value) * dt;
                        float yaw = Mathf.MoveTowardsAngle(rig.Yaw, wantYaw, step);
                        float pitch = Mathf.MoveTowards(rig.Pitch, Mathf.Clamp(wantPitch, pmin, pmax), elev);
                        if (yaw > 180f) yaw -= 360f;
                        if (yaw < -180f) yaw += 360f;
                        rig.Apply(yaw, pitch, Mathf.MoveTowardsAngle(rig.TrackYaw, 0f, 240f * dt));
                        return;
                    }
                default:
                    {
                        Transform t0 = c.Turrets[0];
                        Vector3 l = t0.parent.InverseTransformDirection(dir);
                        if (l.sqrMagnitude < 0.000001f) return;
                        l.Normalize();
                        bool tank = c.GunKind == MercCarrier.GunTank;
                        float wantPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(l.z, -1f, 1f)) * Mathf.Rad2Deg,
                            Turret.PitchMin(tank), Turret.PitchMax(tank));
                        float wantYaw = Mathf.Atan2(-l.x, -l.y) * Mathf.Rad2Deg;
                        float yaw, pitch;
                        if (!Turret.AnglesFor(t0, out yaw, out pitch)) { yaw = wantYaw; pitch = wantPitch; }
                        yaw = Mathf.MoveTowardsAngle(yaw, wantYaw, step);
                        pitch = Mathf.MoveTowards(pitch, wantPitch, step);
                        Quaternion q = Turret.LocalRotationFor(yaw, pitch);
                        for (int i = 0; i < c.Turrets.Length; i++) if (c.Turrets[i] != null) c.Turrets[i].localRotation = q;
                        st.GunYaw = yaw; st.GunPitch = pitch;
                        return;
                    }
            }
        }

        /// <summary>The technical's pintle toward a bearing - the owner's
        /// laying and every other client's copy of it (this file's event).</summary>
        static void SlewPintle(MercCarrier c, float yaw, float pitch)
        {
            SlewPintle(c, yaw, pitch, Traverse(c) * Time.deltaTime);
        }

        static void SlewPintle(MercCarrier c, float yaw, float pitch, float step)
        {
            if (c.Mount == null) return;
            c.Mount.localRotation = Quaternion.RotateTowards(c.Mount.localRotation, Quaternion.Euler(0f, yaw, 0f), step);
            if (c.Gun != null)
                c.Gun.localRotation = Quaternion.RotateTowards(c.Gun.localRotation, Quaternion.Euler(-pitch, 0f, 0f), step);
        }

        /// <summary>No target: after three quiet seconds the technical's gun
        /// and the Gepard's go back to ahead; a turret stays where it looks.</summary>
        static void Rest(MercCarrier c, MercSeat st, float dt, float now)
        {
            if (now - st.LastContact < 3f) return;
            float step = Traverse(c) * 0.5f * dt;
            if (c.GunKind == MercCarrier.GunTechnical)
            {
                SlewPintle(c, 0f, 0f, step);
                st.GunYaw = c.Mount.localRotation.eulerAngles.y;
                st.GunPitch = c.Gun == null ? 0f : -Mathf.DeltaAngle(0f, c.Gun.localRotation.eulerAngles.x);
            }
            else if (c.GunKind == MercCarrier.GunGepard)
            {
                GepardRig rig = c.Rig;
                float yaw = Mathf.MoveTowardsAngle(rig.Yaw, 0f, step);
                float pitch = Mathf.MoveTowards(rig.Pitch, 15f, step);
                if (Mathf.Abs(yaw - rig.Yaw) < 0.001f && Mathf.Abs(pitch - rig.Pitch) < 0.001f) return;
                rig.LocalControl = true;
                rig.Apply(yaw, pitch, rig.TrackYaw);
            }
        }

        /// <summary>The turret pose to the others on the gun's own channel;
        /// the technical's pintle goes with this file's event instead.</summary>
        static void PublishPose(MercCarrier c, MercSeat st, float now, bool laying)
        {
            if (now < st.NextPose) return;
            st.NextPose = now + (laying ? 0.1f : 1f);
            if (c.GunKind == MercCarrier.GunGepard && c.Rig != null)
                GepardNet.SendPose(c.Root, c.Rig.Yaw, c.Rig.Pitch, c.Rig.TrackYaw, st.Firing, false);
            else if ((c.GunKind == MercCarrier.GunBtr || c.GunKind == MercCarrier.GunTank) && st.Target != null)
                Turret.Net.Publish(c.Root, st.GunYaw, st.GunPitch);
        }

        static void Shoot(MercUnit u, MercSeat st, MercCarrier c, Vector3 muzzle, Vector3 bore, float dist, float now)
        {
            float damage, range = GunRange(c);
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical:
                    damage = TechnicalGun.CfgDamage == null ? 85f : TechnicalGun.CfgDamage.Value;
                    break;
                case MercCarrier.GunBtr:
                    damage = RevivalPlugin.CfgTurretDamage == null ? 120f : RevivalPlugin.CfgTurretDamage.Value;
                    break;
                case MercCarrier.GunTank:
                    damage = RevivalPlugin.CfgTankDamage == null ? 400f : RevivalPlugin.CfgTankDamage.Value;
                    break;
                default:
                    damage = Gepard.CfgInfantryDamage == null ? 150f : Gepard.CfgInfantryDamage.Value;
                    break;
            }
            MercGunDrill drill = st.Drill;
            drill.Fire(now, UnityEngine.Random.value);
            st.Firing = drill.InBurst;
            if (drill.Reloading && drill.Belt > 1)
                RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " reloads the " + c.En + "'s gun ("
                    + drill.Reload.ToString("0.0") + " s).");

            float spread = GunnerAI.Spread(st.TargetPlayer, dist, st.TargetSpeed, st.Suppress ? 0f : st.Vis, st.Suppress);
            Vector3 dir = (bore.normalized * Mathf.Max(1f, dist) + GunnerAI.Offset(bore, spread)).normalized;
            Vector3 impact;
            float at;
            GameObject struck = Ray(c, muzzle, dir, range, out impact, out at);
            Vector3 end = struck == null ? muzzle + dir * range : impact;
            switch (c.GunKind)
            {
                case MercCarrier.GunTechnical:
                    if (!BtrGun.TechnicalActive || !BtrGun.FireTechnical(c.Root, end, struck != null))
                    {
                        VehicleShotSound.PlayTechnical(muzzle);
                        Turret.Net.PublishTechnicalShot(muzzle);
                    }
                    break;
                case MercCarrier.GunBtr:
                    if (BtrGun.Active) BtrGun.Fire(c.Root, c.Turrets, end, struck != null);
                    else { VehicleShotSound.Play(muzzle, false); Turret.Net.PublishShot(muzzle, false); }
                    break;
                case MercCarrier.GunTank:
                    VehicleShotSound.Play(muzzle, true);
                    Turret.Net.PublishShot(muzzle, true);
                    break;
                default:
                    // The picture and the sound of the round (an unlive round:
                    // its damage is the hitscan below, on NpcWar's road).
                    GepardShots.Fire(c.Rig, c.GunIdx, dir, false);
                    c.GunIdx = 1 - c.GunIdx;
                    break;
            }
            st.Rounds++;
            if (struck == null) return;
            if (c.GunKind == MercCarrier.GunTank && RevivalPlugin.CfgTankExplosion != null && RevivalPlugin.CfgTankExplosion.Value)
            {
                try
                {
                    float boomDmg = RevivalPlugin.CfgTankExplosionDamage == null ? 300f : RevivalPlugin.CfgTankExplosionDamage.Value;
                    float boomR = RevivalPlugin.CfgTankExplosionRadius == null ? 10f : RevivalPlugin.CfgTankExplosionRadius.Value;
                    RocketHook.Detonate(impact - dir * 0.15f, boomDmg, boomR, 3f);
                    ShellSplash.SweepFrozen(impact - dir * 0.15f, boomDmg, boomR);
                }
                catch (Exception ex) { Warn("tank shell", ex); }
            }
            bool heavy = c.GunKind == MercCarrier.GunBtr || c.GunKind == MercCarrier.GunTank;
            if (NpcWar.MercGunHit(u, struck, damage, impact, muzzle, st.Target, st.TargetCarrier, heavy,
                c.GunKind == MercCarrier.GunTank)) { st.Hits++; st.TargetHits++; }
        }

        /// <summary>W: the target he left was hit by this gun and is dead -
        /// the owner's kill toast (Revival.MercNotify.cs). Event time only.</summary>
        static void GunKilled(MercUnit u, MercSeat st, Transform t, Component npc, bool player)
        {
            int hits = st.TargetHits;
            st.TargetHits = 0;
            if (hits <= 0) return;
            // An NPC (MercAlive is false for a destroyed one too), else a player.
            bool dead = !ReferenceEquals(npc, null) ? !NpcWar.MercAlive(npc)
                : player && t != null && Mercs.PlayerDead(t.gameObject);
            if (!dead) return;
            MercNotify.GunKill(u, t != null ? t.position : Muzzle(st.Carrier));
        }

        // ============================================================ network
        static bool _hooked, _hookFailed;
        static MethodInfo _raise;
        static Type _optType;
        static FieldInfo _onEventCall;
        static Type _viewType;
        static MethodInfo _viewFind, _viewIdGet;
        static bool _viewLooked;

        static void EnsureHooked()
        {
            if (_hooked || _hookFailed) return;
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) throw new Exception("PhotonNetwork missing");
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                _onEventCall = AccessTools.Field(photon, "OnEventCall");
                if (_raise == null || _onEventCall == null) throw new Exception("RaiseEvent or OnEventCall missing");
                MethodInfo own = typeof(MercRide).GetMethod("OnPhotonEvent", BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(_onEventCall.FieldType, own);
                _onEventCall.SetValue(null, Delegate.Combine(_onEventCall.GetValue(null) as Delegate, handler));
                _hooked = true;
                RevivalPlugin.L.LogInfo("Mercs: vehicle seats on Photon event " + Code() + ".");
            }
            catch (Exception ex)
            {
                _hookFailed = true;
                RevivalPlugin.L.LogWarning("Mercs: vehicle seat network - " + ex.Message + " (riders are only seen by their owner).");
            }
        }

        static bool ViewLook()
        {
            if (_viewLooked) return _viewType != null;
            _viewLooked = true;
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_viewType == null) return false;
            _viewIdGet = AccessTools.PropertyGetter(_viewType, "viewID");
            _viewFind = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            return true;
        }

        static int ViewOf(GameObject go)
        {
            try
            {
                if (go == null || !ViewLook() || _viewIdGet == null) return 0;
                Component view = go.GetComponent(_viewType);
                if (view == null) view = go.GetComponentInParent(_viewType);
                return view == null ? 0 : Convert.ToInt32(_viewIdGet.Invoke(view, null));
            }
            catch { return 0; }
        }

        static MercCarrier CarrierByView(int kind, int view)
        {
            if (view <= 0) return null;
            foreach (MercCarrier c in _carriers.Values)
                if (c.View == view && c.Kind == kind && c.Go != null) return c;
            try
            {
                if (kind == MercCarrier.Heli) return CarrierOf(kind, PlayerHeli.MachineByView(view), null);
                if (kind == MercCarrier.Plane) return CarrierOf(kind, PlayerAn2.ByViewId(view), null);
                if (!ViewLook() || _viewFind == null || !VgsLook()) return null;
                Component view0 = _viewFind.Invoke(null, new object[] { view }) as Component;
                if (view0 == null) return null;
                Component vgs = view0.gameObject.GetComponentInParent(_vgsType);
                if (vgs == null) vgs = view0.gameObject.GetComponentInChildren(_vgsType, true);
                return vgs == null ? null : CarrierOf(kind, vgs.gameObject, vgs);
            }
            catch { return null; }
        }

        /// <summary>The owner's riders to everybody else: on a change at
        /// once and reliably, else once a second (5 Hz while a gunner works);
        /// an empty list three times after the last one got out.</summary>
        static void Publish(float now)
        {
            if (!_hooked) return;
            int n = 0;
            bool working = false;
            for (int i = 0; i < _riders.Count; i++)
            {
                MercSeat st = _riders[i].Ride;
                if (st.Carrier == null || st.Carrier.View <= 0) continue;
                n++;
                if (st.Gunner && st.Carrier.GunKind == MercCarrier.GunTechnical && now - st.LastContact < 4f) working = true;
            }
            if (!_dirty && now < _nextSend) return;
            if (n == 0)
            {
                if (_emptySends <= 0 && !_dirty) return;
                _emptySends--;
                Raise(new float[] { 1f, 0f }, true);
                _nextSend = now + 1f;
                _dirty = false;
                return;
            }
            _emptySends = 3;
            float[] data = new float[2 + n * 7];
            data[0] = 1f; data[1] = n;
            int o = 2;
            for (int i = 0; i < _riders.Count; i++)
            {
                MercUnit u = _riders[i];
                MercSeat st = u.Ride;
                if (st.Carrier == null || st.Carrier.View <= 0) continue;
                data[o++] = u.Id;
                data[o++] = st.Carrier.Kind;
                data[o++] = st.Carrier.View;
                data[o++] = st.Seat;
                data[o++] = (st.Hidden ? 1f : 0f) + (st.Gunner ? 2f : 0f) + (st.Firing ? 4f : 0f) + (st.DriveEscort ? 8f : 0f);
                data[o++] = st.GunYaw;
                data[o++] = st.GunPitch;
            }
            Raise(data, _dirty);
            _nextSend = now + (working ? 0.2f : 1f);
            _dirty = false;
        }

        static object _aaOpts;
        delegate void AASend(byte code, object data, bool reliable, object options);
        static AASend _aaSend;
        static bool _aaFailed;
        internal static void SendAAPacket(float[] data)
        {
            if (_aaFailed) return;
            try
            {
            EnsureHooked();
            if (!_hooked) return;
            if (_aaSend == null)
            {
                _aaOpts = Activator.CreateInstance(_optType);
                // Bind once: a reflection return would box RaiseEvent's bool each heartbeat.
                System.Reflection.Emit.DynamicMethod dm = new System.Reflection.Emit.DynamicMethod(
                    "MercAAPublish", typeof(void), new Type[] { typeof(byte), typeof(object), typeof(bool), typeof(object) },
                    typeof(MercRide), true);
                System.Reflection.Emit.ILGenerator il = dm.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
                il.Emit(System.Reflection.Emit.OpCodes.Castclass, _optType);
                il.Emit(System.Reflection.Emit.OpCodes.Call, _raise);
                if (_raise.ReturnType != typeof(void)) il.Emit(System.Reflection.Emit.OpCodes.Pop);
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                _aaSend = (AASend)dm.CreateDelegate(typeof(AASend));
            }
            _aaSend((byte)Code(), data, true, _aaOpts);
            }
            catch (Exception ex) { _aaFailed = true; Warn("AA publish", ex); }
        }

        static void Raise(float[] data, bool reliable)
        {
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] { (byte)Code(), data, reliable, opts });
            }
            catch (Exception ex) { Warn("send", ex); }
        }

        /// <summary>PhotonNetwork.OnEventCall: another owner's riders.</summary>
        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try
            {
                float[] d = content as float[];
                if (d != null && d.Length > 0 && d[0] == 7f) { MercDownPose.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && d[0] == 8f) { MercResupply.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && (d[0] == 106f || d[0] == 107f)) { MercDrive.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && d[0] == 102f) { VehicleSeats.OnVitals(d, sender); return; }
                if (d != null && d.Length > 0 && d[0] == 103f) { MercMoveShootPose.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && d[0] == 104f) { NpcVaultMotion.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && d[0] == 4f) { MercMedPose.OnPacket(d, sender); return; }
                if (d != null && d.Length > 0 && (d[0] == 2f || d[0] == 3f || d[0] == 6f)) { MercAA.OnPacket(d, sender); return; }
                if (d == null || d.Length < 2 || Mathf.RoundToInt(d[0]) != 1) return;
                int n = Mathf.RoundToInt(d[1]);
                if (n < 0 || n > 16 || d.Length < 2 + n * 7) return;
                for (int i = 0; i < d.Length; i++) if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return;
                float now = Time.time;
                string prefix = Mercs.KeyPrefix + sender + "/";
                List<string> seen = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    int o = 2 + i * 7;
                    string key = prefix + Mathf.RoundToInt(d[o]);
                    seen.Add(key);
                    MercSeat st;
                    if (!_remote.TryGetValue(key, out st))
                    {
                        st = new MercSeat();
                        st.Key = key; st.Actor = sender;
                        _remote[key] = st;
                    }
                    st.Heard = now;
                    st.WantKind = Mathf.RoundToInt(d[o + 1]);
                    st.WantView = Mathf.RoundToInt(d[o + 2]);
                    st.WantSeat = Mathf.RoundToInt(d[o + 3]);
                    int flags = Mathf.RoundToInt(d[o + 4]);
                    st.Hidden = (flags & 1) != 0;
                    st.Gunner = (flags & 2) != 0;
                    st.Firing = (flags & 4) != 0;
                    st.DriveEscort = (flags & 8) != 0;
                    st.GunYaw = d[o + 5]; st.GunPitch = d[o + 6];
                }
                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, MercSeat> pair in _remote)
                    if (pair.Value.Actor == sender && !seen.Contains(pair.Key)) gone.Add(pair.Key);
                for (int i = 0; i < gone.Count; i++) { ReleaseRemote(_remote[gone[i]]); _remote.Remove(gone[i]); }
                if (n > 0) _nextKeyScan = Mathf.Min(_nextKeyScan, now);
            }
            catch (Exception ex) { Warn("receive", ex); }
        }

        /// <summary>4 Hz: put the riders we have heard of on their seats.</summary>
        static void StepRemote(float now)
        {
            if (_remote.Count == 0) return;
            List<string> drop = null;
            bool missing = false;
            foreach (KeyValuePair<string, MercSeat> pair in _remote)
            {
                MercSeat st = pair.Value;
                UnityEngine.Object live = st.Ai;
                if (now - st.Heard > HeardSeconds || ((object)st.Ai != null && live == null))
                {
                    ReleaseRemote(st);
                    if (drop == null) drop = new List<string>();
                    drop.Add(pair.Key);
                    continue;
                }
                if (live == null) { missing = true; continue; }
                MercCarrier c = CarrierByView(st.WantKind, st.WantView);
                if (c == null || !Alive(c)) { if (st.Carrier != null) ReleaseRemote(st); continue; }
                if (st.Carrier != c || st.Seat != st.WantSeat)
                {
                    bool hidden = st.Hidden, gunner = st.Gunner, escort = st.DriveEscort;
                    if (st.Carrier != null) ReleaseSeat(st);
                    st.Carrier = c; st.Seat = Mathf.Clamp(st.WantSeat, 0, Mathf.Max(0, c.Seats - 1));
                    st.Hidden = hidden; st.Gunner = gunner; st.DriveEscort = escort;
                    ApplySeat(st, false);
                }
                else Maintain(st, now);
            }
            if (drop != null) for (int i = 0; i < drop.Count; i++) _remote.Remove(drop[i]);
            if (missing && now >= _nextKeyScan) { _nextKeyScan = now + 1f; FindBodies(); }
        }

        static readonly Dictionary<int, string> _keyOf = new Dictionary<int, string>();

        /// <summary>Each NPC's spawn key is read once; the riders we heard of
        /// but have no body for are matched by it.</summary>
        static void FindBodies()
        {
            Component[] all = NpcScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component ai = all[i];
                if (ai == null) continue;
                int id = ai.GetInstanceID();
                string key;
                if (!_keyOf.TryGetValue(id, out key))
                {
                    key = Crew.GroundKey(ai) ?? "";
                    if (_keyOf.Count > 4096) _keyOf.Clear();
                    _keyOf[id] = key;
                }
                if (key.Length == 0 || !key.StartsWith(Mercs.KeyPrefix, StringComparison.Ordinal)) continue;
                MercSeat st;
                if (_remote.TryGetValue(key, out st) && (UnityEngine.Object)st.Ai == null) st.Ai = ai;
            }
        }

        static void Purge(float now)
        {
            _nextPurge = now + 10f;
            List<int> dead = null;
            foreach (KeyValuePair<int, MercCarrier> pair in _carriers)
                if (pair.Value.Go == null) { if (dead == null) dead = new List<int>(); dead.Add(pair.Key); }
            if (dead != null) for (int i = 0; i < dead.Count; i++) _carriers.Remove(dead[i]);
            // A body's key is read again now and then (a fresh body is not
            // missed for good), and the no-target set is rebuilt from the
            // seats, so a destroyed body's id does not linger in it.
            _keyOf.Clear();
            _hidden.Clear();
            for (int i = 0; i < _riders.Count; i++) Rehide(_riders[i].Ride);
            foreach (MercSeat st in _remote.Values) Rehide(st);
        }

        static void Rehide(MercSeat st)
        {
            UnityEngine.Object live = st.Ai;
            if (live != null && st.Carrier != null && (st.Hidden || st.Carrier.Air)) _hidden.Add(st.Ai.GetInstanceID());
        }

        // ============================================================ helpers
        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        static void Toast(string text)
        {
            MercUi.Toast(text, false);
        }

        static void Warn(string where, Exception ex)
        {
            string line = where + ": " + ex.Message;
            if (line == _lastErr && Time.time < _nextToast) return;
            _lastErr = line; _nextToast = Time.time + 10f;
            RevivalPlugin.L.LogWarning("Mercs ride, " + line);
        }
    }

    // ===================================================================
    // NpcWar's half: the seated merc is left alone, and the gunner picks and
    // hits by the same hostility rules as the man on foot.
    public static partial class NpcWar
    {
        /// <summary>RunGround seam: a merc on a seat is the ride's
        /// (Revival.MercsRide.cs). No target, no rifle, no walking.</summary>
        static bool MercSeated(Fighter f, MercUnit u, float now)
        {
            if (u.Ride.Carrier == null) return false;
            if (MercRide.DriveRifle(u.Ride)) { MercDriveRifle(f, u, now); return true; }
            f.Target = null; f.Sees = false; f.TargetIsPlayer = false;
            f.HasOrder = false;
            u.PlayerTarget = null;
            if (u.KillTargetSet != null) SetMercKillTarget(f, u, null);
            return true;
        }

        static Fighter _seatHealth;

        /// <summary>X: health 0..1 of a merc on a seat (1 when unreadable);
        /// MercHealth without its new Fighter per call.</summary>
        internal static float MercSeatHealth(Component ai)
        {
            if (ai == null || !LookUp()) return 1f;
            if (_seatHealth == null) _seatHealth = new Fighter();
            _seatHealth.Ai = ai;
            float h = HealthFraction(_seatHealth);
            _seatHealth.Ai = null;
            return h;
        }

        static Fighter MercFighter(MercUnit u)
        {
            for (int q = 0; q < _squads.Count; q++)
                if (_squads[q].Merc == u && _squads[q].Men.Count > 0) return _squads[q].Men[0];
            return null;
        }

        /// <summary>Getting in or out: stand, drop the rifle's target, so the
        /// next order is issued fresh.</summary>
        internal static void MercRideHold(MercUnit u)
        {
            if (u == null || !LookUp()) return;
            Fighter f = MercFighter(u);
            if (f == null || f.Ai == null || !Alive(f.Ai)) return;
            try
            {
                f.Target = null; f.Sees = false; f.TargetIsPlayer = false;
                u.PlayerTarget = null;
                SetMercKillTarget(f, u, null);
                Hold(f, null, Time.time);
            }
            catch { }
        }

        /// <summary>A player the merc may fire at: not his owner, not us, not
        /// whitelisted, alive, of a faction he hates; PEACEFUL: only the one
        /// who hit him, while he defends himself.</summary>
        static bool MercGunPlayer(MercUnit u, Fighter f, GameObject go, float now)
        {
            if (go == null || go == u.OwnerGo) return false;
            int actor = Mercs.ActorOf(go);
            if (actor == Mercs.LocalActor) return false;
            if (u.Peaceful && (now >= u.DefendUntil || actor != u.LastAttacker)) return false;
            if (Mercs.PlayerDead(go)) return false;
            string steam = Mercs.SteamOf(go);
            if (steam != null && Mercs.Whitelisted(steam)) return false;
            int faction = Mercs.FactionOf(go);
            return faction >= 0 && HatedValue(f.Hated, faction);
        }

        static bool MercGunNpc(MercUnit u, Fighter f, Component c)
        {
            if (c == null || c == u.Ai || !c.gameObject.activeInHierarchy) return false;
            if (Mercs.UnitOf(c) != null || MercRide.HiddenRider(c)) return false;
            if (!Alive(c) || !Hostile(f.Hated, FactionOf(c))) return false;
            Fighter other = FighterOf(c);
            return (other != null && other.Squad != null) || Targetable(c);
        }

        /// <summary>Is the gunner's target still one he may shoot?</summary>
        internal static bool MercGunKeep(MercUnit u, Transform t, Component npc, bool player)
        {
            if (t == null || !LookUp() || !MercMayEngage(u, Time.time)) return false;
            Fighter f = MercFighter(u);
            if (f == null) return false;
            if (player) return MercGunPlayer(u, f, t.gameObject, Time.time);
            return MercGunNpc(u, f, npc);
        }

        /// <summary>The hostiles within range of the gun, nearest first:
        /// players under the merc rules, then NPCs his faction hates.</summary>
        internal static int MercGunCandidates(MercUnit u, Vector3 from, float range,
            Transform[] tr, Component[] npc, bool[] player)
        {
            float now = Time.time;
            if (!LookUp() || !MercMayEngage(u, now)) return 0;
            Fighter f = MercFighter(u);
            if (f == null) return 0;
            int n = 0, cap = tr.Length;
            float[] sqr = new float[cap];
            float rangeSqr = range * range;
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null) continue;
                float d = (go.transform.position - from).sqrMagnitude;
                if (d >= rangeSqr || (n == cap && d >= sqr[n - 1])) continue;
                if (!MercGunPlayer(u, f, go, now)) continue;
                n = GunInsert(n, cap, go.transform, null, true, d, tr, npc, player, sqr);
            }
            for (int i = 0; i < _scene.Count; i++)
            {
                Component c = _scene[i];
                if (c == null) continue;
                float d = (c.transform.position - from).sqrMagnitude;
                if (d >= rangeSqr || (n == cap && d >= sqr[n - 1])) continue;
                if (!MercGunNpc(u, f, c)) continue;
                n = GunInsert(n, cap, c.transform, c, false, d, tr, npc, player, sqr);
            }
            return n;
        }

        static int GunInsert(int n, int cap, Transform t, Component c, bool isPlayer, float d,
            Transform[] tr, Component[] npc, bool[] player, float[] sqr)
        {
            int at = n < cap ? n : cap - 1;
            while (at > 0 && sqr[at - 1] > d)
            {
                if (at < cap) { tr[at] = tr[at - 1]; npc[at] = npc[at - 1]; player[at] = player[at - 1]; sqr[at] = sqr[at - 1]; }
                at--;
            }
            tr[at] = t; npc[at] = c; player[at] = isPlayer; sqr[at] = d;
            return Mathf.Min(cap, n + 1);
        }

        /// <summary>One round of a merc's vehicle gun struck something: the
        /// damage by NpcWar's road, and only on what the merc rules let him
        /// fight - a stray round into a friend does nothing.</summary>
        internal static bool MercGunHit(MercUnit u, GameObject struck, float damage, Vector3 impact, Vector3 muzzle,
            Transform target, Component targetCarrier, bool heavy, bool tank)
        {
            if (u == null || struck == null || !LookUp()) return false;
            Fighter f = MercFighter(u);
            if (f == null) return false;
            float now = Time.time;
            Component hitAi = struck.GetComponentInParent(_npcType);
            if (hitAi != null)
            {
                if (!MercGunNpc(u, f, hitAi)) return false;
                BreakKillStreak(hitAi);
                try
                {
                    return IsMine(hitAi) ? Turret.TryDamage(struck, "NPC_AI2", "ApplyDamage", damage)
                        : RemoteHit(hitAi, damage, impact);
                }
                catch (Exception ex)
                {
                    if (CfgDebug.Value) RevivalPlugin.L.LogWarning("NpcWar: merc gun damage - " + ex.Message);
                    return !Alive(hitAi);
                }
            }
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject p = players[i];
                if (p == null || !(struck.transform == p.transform || struck.transform.IsChildOf(p.transform))) continue;
                if (!MercGunPlayer(u, f, p, now)) return false;
                return Turret.TryDamage(struck, "PlayerNetworkController", "PlayerApplyDamage", damage);
            }
            if (target != null && targetCarrier != null && GunnerAI.OnCarrier(struck, targetCarrier))
            {
                if (heavy && VehicleArmor.GunHit(struck, tank)) return true;
                if (!GunnerAI.Armoured(targetCarrier))
                    return GunnerAI.PlayerDamage(target.gameObject, damage * GunnerAI.T.SoftPassThrough, impact, muzzle);
            }
            return false;
        }
    }
}

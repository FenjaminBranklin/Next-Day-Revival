// Z M5a: one active round trip per merc owner. Event 199 kinds 8/9 carry
// master-validated short driver leases; vehicle motion uses native Photon sync.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercDrive
    {
        sealed class Host
        {
            internal readonly MercDriveLease Lease = new MercDriveLease();
            internal MercCarrier Car;
            internal MercDriveNative Native;
            internal Component Driver;
            internal bool ParkPending;
            internal float ParkUntil;
        }
        static readonly Host[] Hosts = MakeHosts();
        static Host[] MakeHosts() { Host[] a = new Host[8]; for (int i = 0; i < a.Length; i++) a[i] = new Host(); return a; }
        static readonly float[] Request = new float[6], Reply = new float[6];
        static readonly RaycastHit[] Hits = new RaycastHit[48];
        static readonly Collider[] Overlaps = new Collider[48];
        static List<Mercs.Record> Riders = new List<Mercs.Record>(10);
        static Dictionary<int, MercSeat> RiderColliders = new Dictionary<int, MercSeat>();
        static GameObject[] PassengerPlayers = new GameObject[32];
        static MercDriveRun _run;
        static MercCarrier _car;
        static MercDriveNative _native;
        static Mercs.Record _driver;
        static int _token, _driverView, _master = -1;
        static float _leaseUntil, _nextRequest, _nextState, _nextProbe, _nextHost;
        static float _front = 1000f, _left = 1000f, _right = 1000f, _rear = 1000f;
        static bool _ready, _alive, _driverAlive, _playerDriver, _reportedPoint;
        static bool _hostBusy;
        static int _lastRecoveries;
        static int _probeSide;
        static MethodInfo _find;

        internal static bool Active { get { return _run != null && _run.Active; } }
        internal static bool Owns(object vgs) { return EscortOwns(vgs) || _native != null && _native.Enabled && ReferenceEquals(_native.Vgs, vgs); }
        internal static MercCarrier Carrier(MercUnit u)
        {
            return Active && u != null && u.Order.Mode == MercOrder.Drive
                && Mathf.RoundToInt(-u.Order.Facing.x) == _car.View ? _car : EscortCarrier(u);
        }
        internal static bool DriverClaim(MercCarrier c, MercSeat st)
        {
            return EscortDriverClaim(c, st) || Active && c == _car && _driver != null && _driver.Unit != null && _driver.Unit.Ride == st;
        }
        internal static string State(MercUnit u)
        {
            if (Carrier(u) == null) return null;
            MercDriveRun run = EscortCarrier(u) != null ? Other._run : _run;
            return run.Phase == MercDriveRun.Boarding ? Loc.T("подготовка поездки", "boarding for trip")
                : run.Phase == MercDriveRun.AtPoint ? Loc.T("у цели, затем назад", "at destination, then return")
                : run.Phase == MercDriveRun.Returning ? Loc.T("возвращается на машине", "returning by vehicle")
                : Loc.T("едет к цели", "driving to destination");
        }

        internal static bool Start(List<Mercs.Record> selection, Vector3 target)
        {
            if (Active) { Say(Loc.T("Сначала верните машину или отдайте водителю новый приказ.",
                "Return the vehicle first, or give its driver another order."), true); return false; }
            if (selection.Count == 0) return false;
            if (float.IsNaN(target.x) || float.IsNaN(target.y) || float.IsNaN(target.z)
                || float.IsInfinity(target.x) || float.IsInfinity(target.y) || float.IsInfinity(target.z)) return false;
            Mercs.Record driver = null;
            for (int i = 0; i < selection.Count; i++)
            {
                Mercs.Record r = selection[i];
                if (r.Unit == null || r.Unit.Ai == null || r.Dead || r.Deserted || r.Unit.Deserting
                    || !NpcWar.MercAlive(r.Unit.Ai)) continue;
                if (driver == null || NpcWar.MercSeatHealth(r.Unit.Ai) > NpcWar.MercSeatHealth(driver.Unit.Ai)) driver = r;
            }
            if (driver == null) { Say(Loc.T("Нет готового водителя.", "No deployed driver available."), true); return false; }
            MercCarrier car; MercDriveNative native;
            if (!MercRide.DriveVehicle(driver.Unit, out car, out native))
            { Say(driver.Name + Loc.T(": нет исправной свободной машины в 60 м (ключ, батарея, свечи, топливо).",
                ": no ready free vehicle within 60 m (key, battery, plugs, fuel)."), true); return false; }
            if (MercDriveRun.Flat(target - car.Root.position) < 14f)
            { Say(Loc.T("Выберите цель дальше 5 м от машины.", "Choose a destination more than 5 m from the vehicle."), true); return false; }
            _run = new MercDriveRun(); Patrol.MercRoad(car.Root.position, target, _run.Path);
            _run.Begin(Time.time, MercDriveRun.Flat(driver.Unit.Ai.transform.position - car.Root.position));
            _car = car; _native = native; _driver = driver;
            _token = _token >= 1000000 ? 1 : _token + 1;
            _driverView = MercRide.DriveView(driver.Unit.Ai.gameObject);
            if (_driverView <= 0) { _run.Fail(MercDriveRun.NoAuthority); Say(Loc.T("Нет сетевого идентификатора водителя.", "Driver Photon view unavailable."), true); return false; }
            _leaseUntil = _nextRequest = _nextState = _nextProbe = 0f;
            _reportedPoint = false; _lastRecoveries = 0;
            _probeSide = 0;
            _front = _left = _right = _rear = 1000f;
            Riders.Clear(); RiderColliders.Clear();
            Mercs.GiveDrive(driver, car, 0, target); Riders.Add(driver);
            for (int i = 0; i < selection.Count; i++)
            {
                Mercs.Record r = selection[i];
                if (r == driver || r.Unit == null || r.Unit.Ai == null || r.Dead || r.Deserted || r.Unit.Deserting
                    || !NpcWar.MercAlive(r.Unit.Ai)) continue;
                int seat = MercRide.DrivePassengerSeat(car, r.Unit.Ride);
                if (seat < 0) { Say(r.Name + Loc.T(": нет места для сопровождения.", ": no escort seat available."), true); continue; }
                Mercs.GiveDrive(r, car, seat, target); Riders.Add(r);
            }
            for (int i = 0; i < Riders.Count; i++)
            {
                Collider[] cols = Riders[i].Unit.Ai.GetComponentsInChildren<Collider>(true);
                for (int j = 0; j < cols.Length; j++) if (cols[j] != null) RiderColliders[cols[j].GetInstanceID()] = Riders[i].Unit.Ride;
            }
            Mercs.SaveStationOrders(Riders);
            MercRide.StepOwner(Time.time, Mercs.OwnerObject);
            for (int i = 0; i < Riders.Count; i++) NpcWar.MercOrderWake(Riders[i].Unit);
            Say(driver.Name + Loc.T(": везу к точке и обратно, ", ": driving to point and back, ")
                + (MercDriveRun.Flat(target - car.Root.position) / 2.8f).ToString("0") + " m", false);
            return true;
        }

        internal static void Return()
        {
            if (!Active) { Say(Loc.T("Нет активной поездки.", "No active vehicle trip."), true); return; }
            if (_run.Phase == MercDriveRun.Boarding)
            { _run.Fail(MercDriveRun.Cancelled); Finish(); return; }
            if (_run.Phase != MercDriveRun.Returning) _run.Return(Time.time);
            Say(_driver.Name + Loc.T(": возвращаю машину.", ": returning the vehicle."), false);
        }

        internal static void Forget(MercUnit u)
        {
            if (Active && _driver != null && _driver.Unit == u) { _run.Fail(MercDriveRun.NoDriver); Finish(); }
        }

        internal static void Tick()
        {
            TickOne(); TickEscort();
        }
        static void TickOne()
        {
            if (!Active && !_hostBusy) return;
            float now = Time.time;
            HostTick(now);
            if (!Active) return;
            try
            {
                if (_driver == null || _driver.Unit == null || _driver.Unit.Order.Mode != MercOrder.Drive
                    || !MapScene.Owns(_driver.Unit.Order.Scene) || _driver.Unit.Deserting)
                { _run.Fail(MercDriveRun.Cancelled); Finish(); return; }
                MercUnit u = _driver.Unit;
                if (now >= _nextState)
                {
                    _nextState = now + 0.25f;
                    _ready = _native.Ready; _alive = MercRide.DriveAlive(_car);
                    _driverAlive = u.Ai != null && NpcWar.MercAlive(u.Ai);
                    _playerDriver = MercRide.DrivePlayer(_car);
                    MercRide.DrivePlayers(_car, PassengerPlayers);
                    bool seated = u.Ride.Carrier == _car && u.Ride.Seat == 0;
                    if (seated && now >= _nextRequest)
                    { _nextRequest = now + 0.5f; SendRequest(1); }
                    if (!Active || _native == null) return; // A synchronous master refusal ended the mission.
                    bool auth = now < _leaseUntil && _native.Mine;
                    if (auth && !_native.Enabled) _native.Start();
                    // Do not leave escorts stranded: wait for claimed living men
                    // to board, at most the same bounded boarding deadline.
                    bool crewReady = seated;
                    for (int i = 0; i < Riders.Count && crewReady; i++)
                    {
                        MercUnit mate = Riders[i].Unit;
                        if (mate != null && mate.Order.Mode == MercOrder.Drive && mate.Ai != null && NpcWar.MercAlive(mate.Ai)
                            && mate.Ride.Carrier != _car) crewReady = false;
                    }
                    _run.Step(now, _car.Root == null ? Vector3.zero : _car.Root.position,
                        _native.Body == null ? 0f : _native.Body.velocity.magnitude, _run.Phase == MercDriveRun.Boarding ? crewReady : seated, auth,
                        _driverAlive, _alive, _ready, _playerDriver, _rear > 9f);
                    if (!Active) { Finish(); return; }
                    if (_run.Phase == MercDriveRun.AtPoint && !_reportedPoint)
                    { _reportedPoint = true; Say(_driver.Name + Loc.T(": на месте. Через 8 с назад.", ": arrived. Returning in 8 s."), false); }
                    if (_run.Recoveries > _lastRecoveries)
                    { _lastRecoveries = _run.Recoveries; Say(_driver.Name + Loc.T(": препятствие, освобождаю машину.", ": obstacle, freeing the vehicle."), false); }
                }
                if (!_native.Enabled || !_native.Mine) return;
                if (!_run.Driving) { _native.Inputs(0f, 0f, 0f, 1f); return; }
                // One swept direction per frame, each direction at <=5 Hz.
                if (now >= _nextProbe) { _nextProbe = now + 0.05f; Probe(); }
                Vector3 at = _car.Root.position;
                Vector3 aim = _run.Aim(at, Mathf.Clamp(_native.Body.velocity.magnitude * 1.1f, 10f, 35f));
                Vector3 local = _car.Root.InverseTransformPoint(aim); local.y = 0f;
                float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float gas, brake, steer;
                Patrol.MercInputs(angle, Vector3.Dot(_native.Body.velocity, _car.Root.forward), _run.Remaining(at),
                    _front, _left, _right, now < _run.ReverseUntil, _rear > 9f, _run.Recoveries,
                    out steer, out gas, out brake);
                // RCC autoReverse turns a low-speed brake into reverse. A stop
                // at a wall or endpoint uses handbrake; reversing is explicit
                // and only while the rear sweep is clear.
                bool hold = _run.Remaining(at) <= 5.6f || (_front <= 3f && now >= _run.ReverseUntil);
                _native.Inputs(hold ? 0f : gas, hold ? 0f : brake, steer, hold ? 1f : 0f);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MercDrive: " + ex.Message);
                _run.Fail(MercDriveRun.Blocked); Finish();
            }
        }

        static void Probe()
        {
            float range = Mathf.Clamp(_native.Body.velocity.magnitude * 1.5f + 8f, 12f, 40f);
            Transform t = _car.Root;
            switch (_probeSide)
            {
                case 0: _front = Sweep(t.forward, range); break;
                case 1: _left = Sweep(Quaternion.AngleAxis(-32f, t.up) * t.forward, range); break;
                case 2: _right = Sweep(Quaternion.AngleAxis(32f, t.up) * t.forward, range); break;
                default: _rear = Sweep(-t.forward, 12f); break;
            }
            _probeSide = (_probeSide + 1) & 3;
        }

        static float Sweep(Vector3 dir, float range)
        {
            Transform t = _car.Root;
            Vector3 centre = t.TransformPoint(_native.Centre);
            int count = Physics.BoxCastNonAlloc(centre, _native.Half, dir, Hits, t.rotation, range, ~0, QueryTriggerInteraction.Ignore);
            if (count >= Hits.Length) return 0f;
            float distance = range;
            for (int i = 0; i < count; i++)
            {
                Collider c = Hits[i].collider;
                if (Ignore(c) || Hits[i].normal.y > 0.65f) continue;
                distance = Mathf.Min(distance, Hits[i].distance);
            }
            // BoxCast can miss shapes already overlapping its start volume.
            // Test a short forward body volume too, including every world prop.
            count = Physics.OverlapBoxNonAlloc(centre + dir * 2.8f, _native.Half * 0.9f,
                Overlaps, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            if (count >= Overlaps.Length) return 0f;
            for (int i = 0; i < count; i++)
            {
                Collider c = Overlaps[i];
                if (Ignore(c) || c is TerrainCollider) continue;
                if (c.bounds.max.y < centre.y - _native.Half.y + 0.5f) continue;
                distance = Mathf.Min(distance, 2.8f);
            }
            // Negative obstacles count too: no driving over an unsupported edge.
            Vector3 point = t.position + dir * Mathf.Min(range, 12f);
            int n = Physics.RaycastNonAlloc(point + Vector3.up * 8.4f, Vector3.down, Hits, 25.2f, ~0, QueryTriggerInteraction.Ignore);
            bool supported = false;
            if (n >= Hits.Length) return 0f;
            for (int i = 0; i < n; i++)
                if (!Ignore(Hits[i].collider) && Hits[i].normal.y > 0.65f && Mathf.Abs(Hits[i].point.y - t.position.y) < 12f)
                { supported = true; break; }
            return supported ? distance : 0f;
        }

        static bool Ignore(Collider c)
        {
            MercSeat rider;
            if (c == null || c.isTrigger || c.transform.IsChildOf(_car.Root)
                || (RiderColliders.TryGetValue(c.GetInstanceID(), out rider) && rider.Carrier == _car)) return true;
            // Native passenger physics roots need not be parented to the hull.
            // Read their actual seat membership at 4 Hz; no component discovery.
            for (int i = 0; i < PassengerPlayers.Length && PassengerPlayers[i] != null; i++)
                if (c.transform.IsChildOf(PassengerPlayers[i].transform)) return true;
            return false;
        }

        static void Finish()
        {
            if (_native == null) return;
            bool player = MercRide.DrivePlayer(_car);
            _native.Stop(player);
            SendRequest(0);
            bool complete = _run.Phase == MercDriveRun.Complete;
            Say((_driver == null ? "Merc" : _driver.Name) + (complete
                ? Loc.T(": машина вернулась.", ": vehicle returned.") : Loc.T(": поездка прекращена: ", ": vehicle trip stopped: ") + Reason(_run.Reason)), !complete);
            Mercs.EndDrive(Riders, _car); EscortFinished(complete);
            Riders.Clear(); RiderColliders.Clear(); _native = null; _car = null; _driver = null;
            Array.Clear(PassengerPlayers, 0, PassengerPlayers.Length);
        }

        static string Reason(int reason)
        {
            switch (reason)
            {
                case MercDriveRun.NoDriver: return Loc.T("водитель недоступен", "driver unavailable");
                case MercDriveRun.NoVehicle: return Loc.T("машина потеряна", "vehicle lost");
                case MercDriveRun.NotReady: return Loc.T("топливо или детали", "fuel or parts missing");
                case MercDriveRun.Taken: return Loc.T("место занял игрок", "player took driver seat");
                case MercDriveRun.NoAuthority: return Loc.T("нет разрешения мастера или экипаж не сел", "master control or boarding timed out");
                case MercDriveRun.Cancelled: return Loc.T("новый приказ", "superseded by order");
                case MercDriveRun.TimedOut: return Loc.T("истекло время поездки", "trip deadline exceeded");
                default: return Loc.T("путь заблокирован после трёх попыток", "path blocked after three recovery attempts");
            }
        }
        static void Say(string text, bool error) { MercUi.OrderReply(text, error); RevivalPlugin.L.LogInfo("MercDrive: " + text); }

        static void SendRequest(int action)
        {
            if (_car == null || _driverView <= 0) return;
            Request[0] = 106f; Request[1] = action; Request[2] = _car.View;
            Request[3] = _driverView; Request[4] = _token; Request[5] = 0f;
            if (Mercs.LocalActor == MercAA.MasterActor()) OnPacket(Request, Mercs.LocalActor);
            else MercRide.SendAAPacket(Request);
        }

        static Component View(int id)
        {
            if (_find == null)
            {
                Type t = RevivalPlugin.TypeByName("PhotonView");
                if (t != null) _find = AccessTools.Method(t, "Find", new Type[] { typeof(int) }, null);
            }
            return _find == null ? null : _find.Invoke(null, new object[] { id }) as Component;
        }

        static int ViewOwner(Component v)
        {
            MethodInfo m = v == null ? null : AccessTools.PropertyGetter(v.GetType(), "ownerId");
            return m == null ? -1 : FastCall.Int(m, v);
        }

        internal static void OnPacket(float[] d, int sender)
        {
            if (!_inEscort && d != null && d.Length == 6 && d[0] == 107f && EscortActive
                && d[2] == Escort._car.View && d[3] == Escort._driverView)
            { EnterEscort(); try { OnPacketOne(d, sender); } finally { LeaveEscort(); } return; }
            OnPacketOne(d, sender);
        }
        static void OnPacketOne(float[] d, int sender)
        {
            if (d == null || d.Length == 0) return;
            if (_master < 0) _master = MercAA.MasterActor();
            if (d[0] == 107f)
            {
                if (!MercDriveLease.Packet(d, 107, 6) || sender != MercAA.MasterActor() || !Active
                    || d[2] != _car.View || d[3] != _driverView || d[4] != _token || d[5] != Mercs.LocalActor) return;
                if (d[1] == 0f) _leaseUntil = Time.time + 2f;
                else { _run.Fail(Mathf.RoundToInt(d[1])); Finish(); }
                return;
            }
            if (!MercDriveLease.Packet(d, 106, 6) || Mercs.LocalActor != MercAA.MasterActor() || sender <= 0
                || (d[1] != 0f && d[1] != 1f)) return;
            int vehicle = (int)d[2], driver = (int)d[3], token = (int)d[4];
            Host host = null, empty = null; int ownerLeases = 0;
            for (int i = 0; i < Hosts.Length; i++)
            {
                Host h = Hosts[i];
                if (h.Lease.Live(Time.time))
                { if (h.Lease.Vehicle == vehicle) host = h; if (h.Lease.Actor == sender) ownerLeases++; }
                if (!h.Lease.Live(Time.time) && !h.ParkPending && empty == null)
                {
                    if (h.Lease.Actor > 0) ReleaseHost(h);
                    if (!h.ParkPending) empty = h;
                }
            }
            if (d[1] == 0f)
            {
                if (host != null && host.Lease.Matches(sender, vehicle, driver, token)) ReleaseHost(host);
                return;
            }
            if (host != null && !host.Lease.Matches(sender, vehicle, driver, token))
            { Respond(sender, vehicle, driver, token, MercDriveRun.Taken); return; }
            bool fresh = host == null;
            if (fresh && ownerLeases >= 2) { Respond(sender, vehicle, driver, token, MercDriveRun.Taken); return; }
            if (host == null) host = empty;
            if (host == null) { Respond(sender, vehicle, driver, token, MercDriveRun.NoAuthority); return; }
            if (fresh)
            {
                Component view = View(driver);
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Component ai = view == null || npc == null ? null : view.GetComponent(npc);
                string key = ai == null ? null : Crew.GroundKey(ai);
                if (ViewOwner(view) != sender || key == null || !key.StartsWith(Mercs.KeyPrefix + sender + "/", StringComparison.Ordinal)) return;
                MercCarrier car = MercRide.DriveByView(vehicle);
                if (car == null || car.Kind != MercCarrier.Ground || !MercRide.DriveUnclaimed(car))
                { Respond(sender, vehicle, driver, token, MercDriveRun.Taken); return; }
                MercDriveNative native = MercDriveNative.Bind(car);
                if (native == null || (native.Owner > 0 && native.Owner != sender && native.Owner != MercAA.MasterActor()))
                { Respond(sender, vehicle, driver, token, MercDriveRun.NoAuthority); return; }
                host.Car = car; host.Native = native; host.Driver = ai;
            }
            if (!NpcWar.MercAlive(host.Driver) || !MercRide.DriveAlive(host.Car)
                || host.Car.Root == null || (host.Driver.transform.position - host.Car.Root.position).sqrMagnitude > (host.Car.Radius + 12f) * (host.Car.Radius + 12f))
            { Respond(sender, vehicle, driver, token, MercDriveRun.NoDriver); return; }
            if (MercRide.DrivePlayer(host.Car)) { Respond(sender, vehicle, driver, token, MercDriveRun.Taken); return; }
            if (!host.Native.Ready) { Respond(sender, vehicle, driver, token, MercDriveRun.NotReady); return; }
            if (!host.Lease.Grant(Time.time, sender, vehicle, driver, token)) return;
            _hostBusy = true;
            if (fresh && host.Native.Owner != sender) host.Native.Transfer(sender);
            Respond(sender, vehicle, driver, token, 0);
        }

        static void Respond(int actor, int vehicle, int driver, int token, int reason)
        {
            Reply[0] = 107f; Reply[1] = reason; Reply[2] = vehicle; Reply[3] = driver; Reply[4] = token; Reply[5] = actor;
            if (actor == Mercs.LocalActor) OnPacket(Reply, Mercs.LocalActor);
            else MercRide.SendAAPacket(Reply);
        }

        static void ReleaseHost(Host h)
        {
            if (h.Native != null && h.Native.View != null && !MercRide.DrivePlayer(h.Car))
            {
                bool reclaim = h.Native.Owner == h.Lease.Actor || h.Native.Owner == MercAA.MasterActor();
                if (reclaim)
                {
                    if (h.Native.Owner != MercAA.MasterActor()) h.Native.Transfer(MercAA.MasterActor());
                    h.ParkPending = true; h.ParkUntil = Time.time + 3f;
                }
            }
            h.Lease.Clear(); h.Driver = null;
            ParkHost(h, Time.time);
        }

        static void ParkHost(Host h, float now)
        {
            // Photon ownership can arrive after TransferOwnership returns.
            // Keep the cached controller until the master can actually stop it.
            if (h.ParkPending && h.Native != null && h.Native.Mine && !MercRide.DrivePlayer(h.Car))
            { h.Native.Park(); h.ParkPending = false; }
            if (now >= h.ParkUntil || MercRide.DrivePlayer(h.Car)) h.ParkPending = false;
            if (!h.ParkPending) { h.Car = null; h.Native = null; }
        }

        static void HostTick(float now)
        {
            if (now < _nextHost) return; _nextHost = now + 0.5f;
            int master = MercAA.MasterActor();
            if (_master != master)
            {
                // Do not carry a previous master's grants into the new room authority.
                for (int i = 0; i < Hosts.Length; i++)
                { Hosts[i].Lease.Clear(); Hosts[i].ParkPending = false; Hosts[i].Car = null; Hosts[i].Native = null; }
                _master = master;
                _hostBusy = false;
                if (Active) _leaseUntil = 0f;
            }
            if (Mercs.LocalActor != master) return;
            bool busy = false;
            for (int i = 0; i < Hosts.Length; i++)
            {
                Host h = Hosts[i];
                if (h.ParkPending) { ParkHost(h, now); busy |= h.ParkPending; continue; }
                if (h.Lease.Actor > 0 && (!h.Lease.Live(now) || !NpcWar.MercAlive(h.Driver) || MercRide.DrivePlayer(h.Car)))
                    ReleaseHost(h);
                busy |= h.Lease.Actor > 0 || h.ParkPending;
            }
            _hostBusy = busy;
        }
    }
}

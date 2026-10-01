// Z M3: walk to finite storage, carry cargo back, then load at the post.
// Event 199 kind 8 is master-authorized; kind 7 belongs to downed poses.
using System;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercSupplyTrip
    {
        internal int Seq, Gun, Stage, Item, Amount;
        internal float OrderAt, Started, NextSend, NextTry, LoadAt, LastProgress;
        internal Vector3 ReturnAt, LastAt;
        internal bool Pending, Active, EmptyNotice;
    }

    internal static class MercResupply
    {
        const int Code = 8, Begin = 0, Pickup = 1, Load = 2, Finish = 3, Cancel = 4, State = 5;
        static readonly MercSupplyLedger Ledger = new MercSupplyLedger();
        static readonly MercSupplyTicket Refused = new MercSupplyTicket();
        static readonly float[] Request = new float[6], Reply = new float[10];
        static readonly Component[] Bodies = new Component[MercSupplyLedger.Capacity], Owners = new Component[MercSupplyLedger.Capacity];
        static readonly Vector3[] Returns = new Vector3[MercSupplyLedger.Capacity];
        static readonly GameObject[] Crates = new GameObject[MercSupplyLedger.Capacity];
        static readonly int[] CrateViews = new int[MercSupplyLedger.Capacity];
        static readonly int[] CrateActors = new int[MercSupplyLedger.Capacity];
        static readonly int[] CrateSeq = new int[MercSupplyLedger.Capacity], CrateStage = new int[MercSupplyLedger.Capacity];
        static Material _crateMaterial;
        static float _next, _sendAt;
        static int _master = -1, _seq;
        static int _createdFrame = -1;
        static Func<object> _roomGetter;
        static object _room;

        internal static void DepotRestocked()
        {
            _next = 0f;
            for (int i = 0; i < Mercs.Roster.Count; i++)
            {
                MercUnit u = Mercs.Roster[i].Unit; if (u == null || u.Supply.Active || u.Supply.Pending) continue;
                u.Supply.NextTry = 0f; u.Supply.EmptyNotice = false;
            }
        }
        internal static void Tick()
        {
            if (Time.time < _next) return;
            _next = Time.time + .25f;
            FrameProf.S(FrameProf.S_MercResupplyT);
            try { Step(Time.time); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Merc resupply: " + ex.Message); }
            finally { FrameProf.E(FrameProf.S_MercResupplyT); }
        }
        static void Step(float now)
        {
            if (_roomGetter == null)
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                System.Reflection.MethodInfo getter = photon == null ? null : HarmonyLib.AccessTools.PropertyGetter(photon, "room");
                if (getter == null) return;
                _roomGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), getter);
            }
            object room = _roomGetter(); int host = MercAA.MasterActor();
            if (!object.ReferenceEquals(room, _room) || host != _master)
            {
                _room = room; _master = host; Ledger.Reset();
                for (int i = 0; i < Bodies.Length; i++)
                { Bodies[i] = Owners[i] = null; HideCrate(i); CrateViews[i] = CrateSeq[i] = CrateStage[i] = 0; CrateActors[i] = -1; }
                for (int i = 0; i < Mercs.Roster.Count; i++)
                    if (Mercs.Roster[i].Unit != null) StopLocal(Mercs.Roster[i].Unit, now, true);
            }
            if (room == null) return;
            MercSupplyDepot.Bind();
            if (!MercSupplyDepot.Available) return;
            MercSupplyDepot.RequestKnowledge(now);
            if (Crocodile.IsMaster())
            {
                if (now >= _sendAt)
                {
                    _sendAt = now + .5f;
                    for (int i = 0; i < Ledger.Rows.Length; i++)
                    {
                        MercSupplyTicket t = Ledger.Rows[i];
                        if (t.Actor < 0 || !MercSupplyLedger.Active(t.Stage)) continue;
                        if (now >= t.Until || !Valid(Bodies[i], Owners[i], t.Actor, t.Gun)
                            || (MercSupplyLedger.Repair(t.Gun) && t.Revision != AirDefenceDamage.Revision(t.Gun - 100))) Ledger.Cancel(t);
                        if (MercSupplyLedger.Repair(t.Gun) && t.Stage == MercSupplyTicket.Loading
                            && !AtReturn(t, Bodies[i], Returns[i])) t.LoadAt = now + MercSupplyLedger.Seconds(t.Gun);
                        SendState(t);
                    }
                }
            }
            for (int i = 0; i < Mercs.Roster.Count; i++)
            {
                Mercs.Record r = Mercs.Roster[i]; MercUnit u = r.Unit;
                if (u == null || u.Ai == null || r.Dead || r.Deserted) continue;
                MercSupplyTrip trip = u.Supply;
                if (trip.Active || trip.Pending)
                {
                    if (u.Deserting || u.Order.IssuedAt != trip.OrderAt || now - trip.Started > 180f
                        || MercSupplyDepot.Busy(r) || u.Ride.Carrier != null || u.Ride.Boarding != null)
                    { StopLocal(u, now, false); continue; }
                    // A partial/blocked path produces a reason, then retries the post.
                    if ((u.Ai.transform.position - trip.LastAt).sqrMagnitude > 2f)
                    { trip.LastAt = u.Ai.transform.position; trip.LastProgress = now; }
                    if (trip.Stage != MercSupplyTicket.Loading && now - trip.LastProgress > 15f)
                    {
                        MercUi.OrderReply(r.Name + Loc.T(": путь к складу заблокирован; повторю позже.",
                            ": depot path blocked; retrying later."), true);
                        StopLocal(u, now, false);
                    }
                    continue;
                }
                if (now < trip.NextTry || MercSupplyDepot.Busy(r) || u.Deserting || u.AARetreat
                    || u.Ride.Carrier != null || u.Ride.Boarding != null || u.Fight.Health < .5f) continue;
                int post = MercAA.PostOf(u), gun = post >= 7 && post < 14 ? post - 7 : post;
                Flak.Gun g = gun >= 0 && gun < 7 && gun != MercAA.Radar ? Flak.ByIndex(gun) : null;
                bool medic = MercSupplyDepot.Medic != null && MercSupplyDepot.Medic(u)
                    && u.Medicine != null && u.Medicine.Known && !u.Medicine.GiftPending && u.Medicine.Count < MercMedicine.Loadout;
                int repair = post == MercAA.Radar ? AirDefenceDamage.Radar : gun;
                bool fixing = MercAA.IsOrder(u.Order) && AirDefenceDamage.DepotRepairNeeded(repair);
                if (fixing)
                {
                    gun = 100 + repair;
                    bool assigned = false;
                    for (int j = 0; j < Mercs.Roster.Count; j++)
                    {
                        MercUnit other = Mercs.Roster[j].Unit;
                        if (other != null && other != u && (other.Supply.Active || other.Supply.Pending) && other.Supply.Gun == gun)
                        { assigned = true; break; }
                    }
                    if (assigned || (g != null && (g.Engaged || g.Firing))) continue;
                }
                else if (g != null)
                {
                    bool assigned = false;
                    for (int j = 0; j < Mercs.Roster.Count; j++)
                    {
                        MercUnit other = Mercs.Roster[j].Unit;
                        if (other != null && other != u && (other.Supply.Active || other.Supply.Pending) && other.Supply.Gun == gun)
                        { assigned = true; break; }
                    }
                    if (assigned) continue;
                    MercAAPost held = MercAA.Held(post);
                    if (held == null || held.Ai != u.Ai || !MercSupplyDepot.Finite(g)) continue;
                    int stock = MercSupplyDepot.Stock(g);
                    if (!MercSupplyLedger.Depart(MercSupplyLedger.Low(g.ShortRange, stock), g.Engaged || g.Firing, stock == 0)) continue;
                }
                else
                {
                    if (!medic || MercAA.IsOrder(u.Order) || u.Order.Mode == MercOrder.Attack || u.Order.Survive || u.Rally) continue;
                    gun = -1;
                }
                if (!MercSupplyDepot.Ready || (u.Ai.transform.position - MercSupplyDepot.At).sqrMagnitude > 420f * 420f) continue;
                int item = MercSupplyDepot.Item(gun);
                trip.NextTry = now + 5f;
                if (!MercSupplyDepot.Has(item))
                { Empty(r, trip, gun); continue; }
                if (u.Fight.Sees || now - u.LastHit.At < 2f || u.Sense.Exposed) continue;
                trip.EmptyNotice = false; trip.Seq = ++_seq; trip.Gun = gun; trip.Stage = 0;
                trip.Item = trip.Amount = 0; trip.OrderAt = u.Order.IssuedAt;
                trip.Started = trip.LastProgress = now; trip.LastAt = trip.ReturnAt = u.Ai.transform.position;
                trip.Pending = true; trip.NextSend = 0f;
                Send(u, Begin, now);
            }
        }
        static void Empty(Mercs.Record r, MercSupplyTrip t, int gun)
        {
            if (t.EmptyNotice) return;
            t.EmptyNotice = true;
            MercUi.OrderReply(r.Name + (MercSupplyLedger.Repair(gun) ? Loc.T(": на складе нет инструментов.", ": depot has no toolkits.") : gun < 0 ? Loc.T(": на складе нет аптечек.", ": depot has no medkits.")
                : Loc.T(": на складе нет боеприпасов для орудия.", ": depot has no ammunition for this gun.")), true);
        }
        internal static void StopLocal(MercUnit u, float now, bool migration)
        {
            MercSupplyTrip t = u.Supply;
            if (!t.Active && !t.Pending) return;
            if (!migration) { t.NextSend = 0f; Send(u, Cancel, now); }
            t.Active = t.Pending = false; t.Stage = 0; t.NextTry = now + 5f;
            u.NextOrder = 0f; u.GoalFor = null;
        }
        internal static void Send(MercUnit u, int action, float now)
        {
            MercSupplyTrip t = u.Supply;
            if (now < t.NextSend) return;
            int view = MercAA.ViewOf(u); if (view <= 0) return;
            t.NextSend = now + .5f;
            Request[0] = Code; Request[1] = action; Request[2] = view;
            Request[3] = t.Seq; Request[4] = t.Gun; Request[5] = _master;
            if (Crocodile.IsMaster()) OnPacket(Request, MercAA.LocalActor());
            else MercRide.SendAAPacket(Request);
        }
        static bool Valid(Component ai, Component owner, int actor, int gun)
        {
            if (ai == null || !MercAA.Owns(owner, actor) || !Flak.Up(ai) || !AirfieldHold.State.ByPlayers
                || TowerRadar.PlayerSide(actor) != AirfieldHold.State.Holder) return false;
            if (MercSupplyLedger.Repair(gun)) return AirDefenceDamage.DepotRepairNeeded(gun - 100);
            if (gun < 0) return true;
            Flak.Gun g = Flak.ByIndex(gun);
            return g != null && g.Root != null && MercSupplyDepot.Finite(g)
                && Flak.OwnerSide(g) == AirfieldHold.State.Holder && AirDefenceDamage.Alive(gun);
        }
        static bool Close(Component ai, Vector3 at)
        { return ai != null && (ai.transform.position - at).sqrMagnitude <= 4.2f * 4.2f; }
        internal static void OnPacket(float[] f, int sender)
        {
            if (f == null || f.Length < 6 || f[0] != Code || !MercSupplyDepot.Available) return;
            int action = MercSupplyLedger.Integer(f[1]), view = MercSupplyLedger.Integer(f[2]);
            int seq = MercSupplyLedger.Integer(f[3]), gun = MercSupplyLedger.Integer(f[4]);
            int host = MercSupplyLedger.Integer(f[5]);
            if (host != MercAA.MasterActor() || view <= 0 || seq <= 0 || seq > 16000000 || !MercSupplyLedger.Destination(gun)) return;
            if (action == State)
            {
                if (sender != host || f.Length != 10) return;
                int actor = MercSupplyLedger.Integer(f[6]), stage = MercSupplyLedger.Integer(f[7]);
                int item = MercSupplyLedger.Integer(f[8]), amount = MercSupplyLedger.Integer(f[9]);
                if (actor < 0 || stage < 1 || stage > 7 || amount < 0 || amount > 1000
                    || (item != 0 && item != 2076 && item != 2077 && item != 10005 && item != 2064 && !MercMedicine.Item(item))) return;
                if ((stage == MercSupplyTicket.Carrying || stage == MercSupplyTicket.Loading || stage == MercSupplyTicket.Done)
                    && (item == 0 || amount == 0)) return;
                Receive(actor, view, seq, gun, stage, item, amount);
                return;
            }
            if (!Crocodile.IsMaster() || sender < 0 || action < Begin || action > Cancel || f.Length != 6) return;
            MercSupplyTicket t = Ledger.Find(sender, view);
            if (action == Begin)
            {
                if (t != null && t.Seq == seq) { SendState(t); return; }
                Component ai = MercAA.Resolve(view, sender); // cold, owner/key-validated
                Component owner = MercAA.ViewComponent(ai);
                if (!Valid(ai, owner, sender, gun)) return;
                Vector3 ret = ai.transform.position;
                if ((ret - MercSupplyDepot.At).sqrMagnitude > 420f * 420f) return;
                if (MercSupplyLedger.Repair(gun))
                {
                    Vector3 point;
                    if (!AirDefenceDamage.DepotRepairPoint(gun - 100, out point)
                        || (ai.transform.position - point).sqrMagnitude > 56f * 56f)
                    { Reject(sender, view, seq, gun); return; }
                }
                else if (gun >= 0)
                {
                    Flak.Gun g = Flak.ByIndex(gun); MercAAPost crew = MercAA.Gun(gun);
                    MercAAPost loader = MercAA.Held(gun + 7);
                    if ((crew == null || crew.Ai != ai) && (loader == null || loader.Ai != ai)) { Reject(sender, view, seq, gun); return; }
                    if (!MercSupplyLedger.Depart(MercSupplyLedger.Low(g.ShortRange, MercSupplyDepot.Stock(g)), g.Engaged || g.Firing, MercSupplyDepot.Stock(g) == 0))
                    { Reject(sender, view, seq, gun); return; }
                }
                t = Ledger.Begin(sender, view, seq, gun, Time.time);
                if (t == null) { Reject(sender, view, seq, gun); return; }
                if (MercSupplyLedger.Repair(gun)) t.Revision = AirDefenceDamage.Revision(gun - 100);
                int row = Row(t); Bodies[row] = ai; Owners[row] = owner; Returns[row] = ret;
                SendState(t); return;
            }
            if (t == null || t.Seq != seq || t.Gun != gun) return;
            int index = Row(t); Component body = Bodies[index];
            if (!Valid(body, Owners[index], sender, gun) || (MercSupplyLedger.Repair(gun) && t.Revision != AirDefenceDamage.Revision(gun - 100))) { Ledger.Cancel(t); SendState(t); return; }
            if (action == Cancel) Ledger.Cancel(t);
            else if (action == Pickup && t.Stage == MercSupplyTicket.Going && Close(body, MercSupplyDepot.At))
            {
                int item = MercSupplyDepot.Item(gun), amount = MercSupplyDepot.Take(gun, item, Time.time);
                if (amount == 0) t.Stage = MercSupplyTicket.Empty;
                else Ledger.Pickup(t, item, amount);
            }
            else if ((action == Load || action == Finish) && AtReturn(t, body, Returns[index]))
            {
                if (action == Load) Ledger.Load(t, Time.time);
                else if (MercSupplyLedger.Repair(gun))
                {
                    if (Time.time >= t.LoadAt && AirDefenceDamage.DepotRepair(gun - 100, t.Revision)) Ledger.Finish(t, Time.time);
                }
                else if (gun < 0 || (!MercSupplyDepot.Locked(Flak.ByIndex(gun), Time.time) && t.Item == MercSupplyDepot.Item(gun)))
                {
                    int amount = Ledger.Finish(t, Time.time);
                    if (amount > 0 && gun >= 0) MercSupplyDepot.Credit(gun, amount);
                }
            }
            SendState(t);
        }
        static bool AtReturn(MercSupplyTicket t, Component body, Vector3 back)
        {
            if (MercSupplyLedger.Repair(t.Gun))
            { Vector3 point; return AirDefenceDamage.DepotRepairPoint(t.Gun - 100, out point) && Close(body, point); }
            if (t.Gun < 0) return Close(body, back);
            Vector3 at; Quaternion rot;
            return (MercAA.Pose(t.Gun, out at, out rot) && Close(body, at))
                || (MercAA.Pose(t.Gun + 7, out at, out rot) && Close(body, at));
        }
        static int Row(MercSupplyTicket t)
        { for (int i = 0; i < Ledger.Rows.Length; i++) if (Ledger.Rows[i] == t) return i; return -1; }
        static void Reject(int actor, int view, int seq, int gun)
        {
            Refused.Actor = actor; Refused.View = view; Refused.Seq = seq; Refused.Gun = gun;
            Refused.Stage = MercSupplyTicket.Unavailable; Refused.Item = Refused.Amount = 0;
            SendState(Refused);
        }
        static void SendState(MercSupplyTicket t)
        {
            Reply[0] = Code; Reply[1] = State; Reply[2] = t.View; Reply[3] = t.Seq;
            Reply[4] = t.Gun; Reply[5] = _master; Reply[6] = t.Actor; Reply[7] = t.Stage;
            Reply[8] = t.Item; Reply[9] = t.Amount;
            MercRide.SendAAPacket(Reply);
            OnPacket(Reply, _master);
        }
        static void Receive(int actor, int view, int seq, int gun, int stage, int item, int amount)
        {
            Visual(actor, view, seq, stage);
            if (actor != MercAA.LocalActor()) return;
            for (int i = 0; i < Mercs.Roster.Count; i++)
            {
                Mercs.Record r = Mercs.Roster[i]; MercUnit u = r.Unit;
                if (u == null || u.AAView != view) continue;
                MercSupplyTrip t = u.Supply;
                if ((!t.Active && !t.Pending) || t.Seq != seq || t.Gun != gun) return;
                if (stage < t.Stage) return;
                bool departing = stage == MercSupplyTicket.Going && t.Stage == 0;
                t.Pending = false; t.Active = MercSupplyLedger.Active(stage);
                if (stage != t.Stage) { t.LastProgress = Time.time; t.NextSend = 0f; u.NextOrder = 0f; }
                if (stage == MercSupplyTicket.Loading && t.Stage != stage) t.LoadAt = Time.time + MercSupplyLedger.Seconds(gun);
                t.Stage = stage; t.Item = item; t.Amount = amount;
                if (t.Active) MercAA.Release(u);
                if (departing)
                    MercUi.OrderReply(r.Name + (MercSupplyLedger.Repair(gun) ? Loc.T(": иду за инструментами на склад.", ": fetching repair toolkit from depot.") : gun < 0 ? Loc.T(": иду за аптечками на склад.", ": fetching medkits from depot.")
                        : Loc.T(": иду за боеприпасами на склад.", ": fetching ammunition from depot.")), false);
                if (!t.Active)
                {
                    t.NextTry = Time.time + 5f;
                    if (stage == MercSupplyTicket.Empty) Empty(r, t, gun);
                    else if (stage == MercSupplyTicket.Done && gun < 0 && MercMedicine.Item(item) && amount == 1)
                        Mercs.DepotMedkitGranted(r, item);
                    else if (stage == MercSupplyTicket.Done && MercSupplyLedger.Repair(gun))
                        MercUi.OrderReply(r.Name + Loc.T(": позиция отремонтирована.", ": post repaired."), false);
                    else if (stage == MercSupplyTicket.Done && gun >= 0)
                        MercUi.OrderReply(r.Name + Loc.T(": орудие пополнено.", ": gun supplied."), false);
                    else if (stage == MercSupplyTicket.Unavailable)
                        MercUi.OrderReply(r.Name + Loc.T(": доставка пока недоступна; остаюсь на посту.", ": supply run unavailable; staying at post."), true);
                    else if (stage == MercSupplyTicket.Lost)
                        MercUi.OrderReply(r.Name + (amount > 0 ? Loc.T(": доставка прервана; груз утрачен.", ": supply run interrupted; cargo lost.")
                            : Loc.T(": доставка прервана; повторю позже.", ": supply run interrupted; retrying later.")), true);
                }
                return;
            }
        }
        static void HideCrate(int row)
        {
            if (Crates[row] != null) Crates[row].SetActive(false);
        }
        static void Visual(int actor, int view, int seq, int stage)
        {
            int row = -1;
            for (int i = 0; i < Crates.Length; i++)
                if (CrateViews[i] == view && CrateActors[i] == actor) { row = i; break; }
            if (row < 0)
            {
                for (int i = 0; i < Crates.Length; i++) if (CrateViews[i] == 0 || !MercSupplyLedger.Active(CrateStage[i])) { row = i; break; }
                if (row < 0) return;
                CrateViews[row] = view; CrateActors[row] = actor; CrateSeq[row] = CrateStage[row] = 0;
                HideCrate(row);
                if (Crates[row] != null) Crates[row].transform.SetParent(null, false);
            }
            if (seq < CrateSeq[row] || (seq == CrateSeq[row] && stage < CrateStage[row])) return;
            CrateSeq[row] = seq; CrateStage[row] = stage;
            bool carry = stage == MercSupplyTicket.Carrying || stage == MercSupplyTicket.Loading;
            if (!carry) { HideCrate(row); return; }
            if (Crates[row] == null || Crates[row].transform.parent == null)
            {
                Component ai = MercAA.Resolve(view, actor); if (ai == null) return;
                if (Crates[row] == null)
                {
                    // Native primitive creation is a cold transition, spread
                    // over frames even when a whole squad picks up together.
                    if (_createdFrame == Time.frameCount) return;
                    _createdFrame = Time.frameCount;
                    if (_crateMaterial == null)
                    { _crateMaterial = new Material(Shader.Find("Standard")); _crateMaterial.color = new Color(.28f, .25f, .12f); }
                    GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.name = "Merc supply crate";
                    Collider collider = box.GetComponent<Collider>(); if (collider != null) UnityEngine.Object.Destroy(collider);
                    box.GetComponent<Renderer>().sharedMaterial = _crateMaterial;
                    Crates[row] = box;
                }
                Crates[row].transform.SetParent(ai.transform, false);
                Crates[row].transform.localScale = new Vector3(1.4f, .8f, 1f);
                Crates[row].transform.localRotation = Quaternion.identity;
            }
            if (Crates[row] == null) { HideCrate(row); return; }
            Crates[row].transform.localPosition = stage == MercSupplyTicket.Loading ? new Vector3(1.2f, .4f, .5f) : new Vector3(.9f, 2.2f, .4f);
            Crates[row].SetActive(true);
        }
    }

    internal static partial class Mercs
    {
        // The world master already removed exactly one kit. Paid grants still
        // pass through the existing authenticated roster capacity/owner checks.
        internal static void DepotMedkitGranted(Record r, int item)
        {
            if (r == null || r.Dead || r.Deserted || !MercMedicine.Item(item)) return;
            if (r.Session) { r.Medicine.Give(item); return; }
            if (!_medicineServer || _support != 1 || r.Medicine.GiftPending)
            {
                MercUi.OrderReply(r.Name + Loc.T(": выдача аптечки не подтверждена; груз утрачен.",
                    ": medkit grant unconfirmed; cargo lost."), true); return;
            }
            r.Medicine.GiftPending = true;
            Enqueue("med-give", "id=" + N(r.Id) + "\nitem=" + N(item) + "\n", delegate(string result)
            {
                r.Medicine.GiftPending = false;
                if (result != "ok")
                {
                    RequestRoster(0f);
                    MercUi.OrderReply(r.Name + Loc.T(": выдача аптечки не подтверждена; проверьте запас.",
                        ": medkit grant unconfirmed; check supplies."), true);
                }
            }, 0f);
        }
    }

    public static partial class NpcWar
    {
        static bool MercResupplyDuty(Fighter f, MercUnit u, float now)
        {
            MercSupplyTrip t = u.Supply;
            if (!t.Active && !t.Pending) return false;
            if (t.Pending) { MercResupply.Send(u, 0, now); return false; }
            // M2/M3 remain the combat overlay during the journey. Rifle, M1
            // cover, self defence and urgent retreat are never replaced by duty.
            if (MercFight(f, u, now)) { if (t.Stage == MercSupplyTicket.Loading) t.LoadAt = now + MercSupplyLedger.Seconds(t.Gun); return true; }
            Vector3 goal = t.Stage == MercSupplyTicket.Going ? MercSupplyDepot.At : t.ReturnAt;
            if (MercSupplyLedger.Repair(t.Gun) && t.Stage != MercSupplyTicket.Going)
            { Vector3 point; if (AirDefenceDamage.DepotRepairPoint(t.Gun - 100, out point)) goal = point; }
            else if (t.Gun >= 0 && t.Stage != MercSupplyTicket.Going)
            {
                Quaternion rot; Vector3 at;
                if (MercAA.Pose(MercAA.PostOf(u), out at, out rot)) goal = at;
            }
            if ((goal - f.Tr.position).sqrMagnitude > 3.5f * 3.5f)
            {
                if (t.Stage == MercSupplyTicket.Loading) t.LoadAt = now + MercSupplyLedger.Seconds(t.Gun);
                Vector3 cover = u.Sense.Pick.Point.Pos;
                if (u.Sense.Count > 0 && u.Sense.Pick.Found && (cover - f.Tr.position).sqrMagnitude > 16f
                    && (cover - goal).sqrMagnitude + 16f < (f.Tr.position - goal).sqrMagnitude) goal = cover;
                MercMove(f, u, goal, false, now);
            }
            else
            {
                MercCrouch(f, now); // native synchronized short loading pose
                if (t.Stage == MercSupplyTicket.Loading)
                { if (now >= t.LoadAt) MercResupply.Send(u, 3, now); }
                else MercResupply.Send(u, t.Stage == MercSupplyTicket.Going ? 1 : 2, now);
            }
            return true;
        }
    }
}

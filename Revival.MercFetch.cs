// Z M5b: selected mercs fetch one paid crate, return to the finite depot.
// G O1: with no pick in L one merc goes, the nearest free one.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercFetch
    {
        sealed class World : IMercFetchWorld
        {
            public int Validate(MercFetchJob j, bool loading)
            { return MercFetchNative.Resolve(j) ? MercFetchNative.Validate(j, loading) : MercFetchJob.Missing; }
            public void Carry(MercFetchJob j) { MercFetchNative.Apply(j); }
            public void Release(MercFetchJob j) { MercFetchNative.Release(j); }
            public bool AtDepot(MercFetchJob j) { return MercFetchNative.AtDepot(j); }
            public int UnloadOne(MercFetchJob j) { return MercFetchNative.UnloadOne(j); }
            public void Changed(MercFetchJob j)
            {
                if (j.Active) MercFetchNative.Apply(j);
                Publish(j); LocalState(j);
            }
        }
        sealed class Local
        {
            internal object Run;
            internal int Serial, Crate, Vehicle, Driver, Stage, Moved;
            internal bool WithEscort, Delivered;
            internal readonly List<Mercs.Record> Transport = new List<Mercs.Record>(6), Escort = new List<Mercs.Record>(2);
            internal float Next, Deadline, Await;
            internal Vector3 Home;
            internal readonly List<Mercs.Record> Selected = new List<Mercs.Record>(6);
            internal readonly List<MercOrder> Before = new List<MercOrder>(6), Driving = new List<MercOrder>(6);
        }
        static MercFetchHost Host = new MercFetchHost(new World());
        static readonly float[] Request = new float[8], State = new float[11];
        static ConfigEntry<bool> _enabled;
        static Local _local;
        static float _nextHost;
        static int _master = -1, _serial, _generation = -1;
        static string _scene;
        internal static bool NeedSnapshot;
        internal static bool EscortRequested;
        internal static bool Enabled { get { return _enabled == null || _enabled.Value; } }

        internal static void BindConfig(ConfigFile cfg)
        { _enabled = cfg.Bind("MercFetch", "Enabled", true, "Selected mercs fetch a paid airdrop using MercDrive and unload into AmmoDepot."); }
        internal static void Install(Harmony h)
        {
            MercFetchBridge.Install(h); MercFetchNative.Install(h);
            h.Patch(typeof(MercRide).GetMethod("OnPhotonEvent"), null,
                new HarmonyMethod(typeof(MercRide).GetMethod("FetchEventPostfix")), null, null, null);
        }

        internal static void Start()
        {
            if (!Enabled) { Say(Loc.T("Получение грузов отключено.", "Airdrop retrieval disabled."), true); return; }
            if (!MercFetchBridge.Available || !MercFetchNative.Available)
            {
                RevivalPlugin.L.LogWarning("MercFetch: prerequisite integration required (M5a/M2/M4).");
                Say(Loc.T("Получение груза недоступно в этой версии плагина.", "Airdrop fetching unavailable in this plugin build."), true); return;
            }
            ResetWorld();
            MercFetchBridge.Snapshot();
            if (_local != null || MercFetchBridge.Active)
            { Say(Loc.T("Уже есть поездка: сначала верните машину.", "A vehicle trip is already active: return it first."), true); return; }
            if (!MercFetchBridge.DepotReady || MercFetchBridge.DepotFull)
            { Say(Loc.T("Склад недоступен или заполнен.", "Depot unavailable or full."), true); return; }
            if (!AirfieldHold.State.ByPlayers || AirfieldHold.State.Holder < 0
                || Mortar.FactionShield.FactionOf(Crocodile.PlayerByActor(Crocodile.LocalActor())) != AirfieldHold.State.Holder)
            { Say(Reason(MercFetchJob.WrongSide), true); return; }
            Local l = new Local();
            List<Mercs.Record> roster = Mercs.Roster;
            l.WithEscort = EscortRequested;
            bool pick = Mercs.PickActive();
            if (pick)
                for (int i = 0; i < roster.Count && l.Selected.Count < 6; i++)
                {
                    Mercs.Record r = roster[i];
                    if (!r.Selected || !Deployed(r)) continue;
                    l.Selected.Add(r); l.Before.Add(r.Order);
                }
            else PickNearest(roster, l, l.WithEscort ? 3 : 1);
            if (l.Selected.Count == 0)
            {
                if (pick) Say(Loc.T("Выберите хотя бы одного живого наёмника в L.", "Select at least one deployed living merc in L."), true);
                else Say(Loc.T("Нет свободного бойца: расчёты пушек и радара остаются на постах - отметьте бойца в L.",
                    "No free deployed merc: gun and radar crews keep their posts - check one in L to send him."), true);
                return;
            }
            if (l.WithEscort && l.Selected.Count < 3)
            { Say(pick ? Loc.T("Для сопровождения выберите минимум 3 бойцов: последний 2 - водитель и стрелок.",
                "Escort needs at least 3 selected mercs: the last two are its driver and gunner.")
                : Loc.T("Для сопровождения нужны 3 свободных бойца (не у пушки/радара) - или отметьте их в L.",
                "Escort needs 3 free mercs (not on a gun or the radar) - or check them in L."), true); return; }
            for (int i = 0; i < l.Selected.Count; i++)
                if (l.WithEscort && i >= l.Selected.Count - 2) l.Escort.Add(l.Selected[i]);
                else l.Transport.Add(l.Selected[i]);
            MercFetchNative.Crate best = null; Vector3 target = Vector3.zero; float distance = 840f * 840f;
            for (int i = 0; i < MercFetchNative.Crates.Count; i++)
            {
                MercFetchNative.Crate c = MercFetchNative.Crates[i]; if (c.Root == null || c.Locked) continue;
                float d = (c.Root.position - Mercs.OwnerPosition).sqrMagnitude;
                if (d >= distance) continue;
                Vector3 at; if (!MercFetchNative.Target(c, out at)) continue;
                best = c; distance = d; target = at;
            }
            if (best == null)
            { Say(Loc.T("Нет приземлившегося непустого воздушного груза в 300 м.", "No landed nonempty supply airdrop within 300 m."), true); return; }
            Vector3 parking;
            if (!MercFetchNative.Parking(target, Mercs.OwnerPosition, out parking)
                || !MercFetchNative.Parking(MercFetchBridge.Depot, target, out l.Home))
            { Say(Loc.T("Нет свободной площадки для машины у ящика или склада.", "No clear vehicle parking lane at the crate or depot."), true); return; }
            if (!MercFetchBridge.Start(l.Transport, parking)) return; // Native named no-car/driver reason.
            if (!MercFetchBridge.Active || MercFetchBridge.Car == null || MercFetchBridge.Driver == null
                || MercFetchBridge.Driver.Unit == null || MercFetchBridge.Driver.Unit.Ai == null) return;
            if (l.WithEscort)
            {
                Vector3 escortParking;
                if (!MercFetchNative.Parking(parking + Vector3.right * 84f, Mercs.OwnerPosition, out escortParking)
                    || (escortParking - parking).sqrMagnitude < 56f * 56f
                    || !MercDrive.StartEscort(l.Escort, escortParking))
                {
                    MercFetchBridge.Finish(MercFetchBridge.Run, false);
                    for (int i = 0; i < l.Selected.Count; i++) Mercs.FetchRestore(l.Selected[i], l.Before[i]);
                    Say(Loc.T("Нет свободной исправной машины сопровождения со стрелковым местом или площадки.",
                        "No separate ready escort vehicle with a gunner seat or clear parking lane."), true); return;
                }
            }
            l.Run = MercFetchBridge.Run; l.Serial = ++_serial; l.Crate = best.View;
            l.Vehicle = MercFetchBridge.Car.View; l.Driver = Crocodile.ViewId(MercFetchBridge.Driver.Unit.Ai.gameObject);
            l.Deadline = Time.time + 1300f;
            for (int i = 0; i < l.Selected.Count; i++) l.Driving.Add(l.Selected[i].Order);
            _local = l; _scene = MapScene.Current;
            string names = "";
            for (int i = 0; i < l.Selected.Count; i++) names += (i == 0 ? "" : ", ") + l.Selected[i].Name;
            Say(Loc.T("ЗАБРАТЬ ГРУЗ: ", "FETCH AIRDROP: ") + names + Loc.T(" - едут; остальные держат посты.",
                " travel; the rest hold their posts."), false);
        }

        static bool Deployed(Mercs.Record r)
        {
            return !r.Dead && !r.Deserted && r.Unit != null && r.Unit.Ai != null && !r.Unit.Deserting && NpcWar.MercAlive(r.Unit.Ai);
        }

        static readonly Mercs.Record[] PickRows = new Mercs.Record[16];
        static readonly float[] PickDistance = new float[16];
        static readonly bool[] PickCar = new bool[16], PickTaken = new bool[16];

        // G O1: no pick in L - the nearest free merc to the player (paid, not
        // crewing a gun or the radar), one beside a ready vehicle first; an
        // escort takes the next two nearest.
        static void PickNearest(List<Mercs.Record> roster, Local l, int want)
        {
            int n = 0;
            for (int i = 0; i < roster.Count && n < PickRows.Length; i++)
            {
                Mercs.Record r = roster[i];
                if (!Deployed(r) || r.Unpaid || MercAA.IsOrder(r.Order)) continue;
                PickRows[n] = r; PickTaken[n] = false;
                PickDistance[n] = (r.Unit.Ai.transform.position - Mercs.OwnerPosition).sqrMagnitude;
                PickCar[n] = MercDrive.CanDrive(r.Unit);
                n++;
            }
            for (int k = 0; k < want; k++)
            {
                int best = MercTargetPlan.Nearest(PickDistance, PickCar, PickTaken, n, k == 0);
                if (best < 0) break;
                PickTaken[best] = true;
                l.Selected.Add(PickRows[best]); l.Before.Add(PickRows[best].Order);
            }
            for (int i = 0; i < n; i++) PickRows[i] = null;
        }

        // The existing drive step retains movement/combat/lease failure gates.
        // Its two stopped endpoints now wait for authoritative cargo receipts.
        public static void DriveStepPostfix(object __instance)
        {
            Local l = _local; if (l == null || !ReferenceEquals(l.Run, __instance)) return;
            int phase = MercFetchBridge.Phase(__instance);
            if ((l.Stage <= 1 && phase == 2) || (l.Stage >= 2 && phase == 4) || (l.Stage >= 3 && phase == 2))
                MercFetchBridge.Hold(__instance, Time.time);
        }

        internal static void Tick()
        {
            if (!MercFetchBridge.Available || !MercFetchNative.Available) return;
            ResetWorld();
            bool busy = _local != null;
            for (int i = 0; i < Host.Jobs.Length; i++) busy |= Host.Jobs[i].Active;
            if (!busy && !NeedSnapshot) return;
            float now = Time.time;
            if (!Enabled || (_scene != null && !MapScene.Owns(_scene)))
            { Stop(false, MercFetchJob.Cancelled); Host.NewMaster(); NeedSnapshot = false; return; }
            int master = MercAA.MasterActor();
            if (_master >= 0 && _master != master)
            {
                // Both peers release the real crate; the new master cannot replay a stale trip.
                Stop(false, MercFetchJob.Expired);
                for (int i = 0; i < Host.Jobs.Length; i++)
                    if (Host.Jobs[i].Active) { MercFetchNative.Release(Host.Jobs[i]); Host.Jobs[i].Phase = MercFetchJob.Failed; }
                NeedSnapshot = false;
            }
            _master = master;
            if (NeedSnapshot) { NeedSnapshot = false; MercFetchBridge.Snapshot(); Send(4); }
            MercFetchNative.Animate();
            if (Crocodile.IsMaster() && now >= _nextHost)
            {
                _nextHost = now + 0.25f;
                try { Host.Step(now); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("MercFetch transfer: " + ex.Message);
                    Host.NewMaster(); Stop(false, MercFetchJob.Unsupported);
                }
            }
            Local l = _local; if (l == null) return;
            if (l.WithEscort && MercDrive.EscortFailure != 0)
            { Stop(false, MercDrive.EscortFailure); return; }
            if (l.Delivered)
            {
                if (l.WithEscort && MercDrive.EscortActive) return;
                Stop(true, l.Moved); return;
            }
            if (!MercFetchBridge.Active || !ReferenceEquals(l.Run, MercFetchBridge.Run) || now >= l.Deadline)
            { Stop(false, ReferenceEquals(l.Run, MercFetchBridge.Run) ? MercFetchBridge.Failure(l.Run) : MercFetchJob.Cancelled); return; }
            if (l.Await > 0f && now >= l.Await) { Stop(false, MercFetchJob.Expired); return; }
            if (now < l.Next) return;
            l.Next = now + 1f;
            Send(5); // Master expires cargo within 12 s of owner silence.
            if (MercFetchBridge.Phase(l.Run) != 2) return;
            if (l.Stage <= 1)
            { if (l.Stage == 0) l.Await = now + 10f; l.Stage = 1; Send(1); }
            else if (l.Stage <= 3)
            { if (l.Stage == 2) l.Await = now + 10f; l.Stage = 3; Send(2); }
        }

        static void LocalState(MercFetchJob j)
        {
            Local l = _local;
            if (l == null || !j.Matches(Crocodile.LocalActor(), l.Serial, l.Crate, l.Vehicle, l.Driver)) return;
            if (j.Phase == MercFetchJob.Carried && l.Stage <= 1)
            {
                l.Await = 0f; l.Stage = 2; MercFetchBridge.ReturnToDepot(l.Run, l.Home);
                Say(Loc.T("Груз погружен. Возвращаемся на склад.", "Cargo loaded. Returning to the depot."), false);
            }
            else if (j.Phase == MercFetchJob.Unloading && l.Stage != 4)
            { l.Await = 0f; l.Stage = 4; Say(Loc.T("Разгружаем на склад.", "Unloading into the depot."), false); }
            else if (j.Phase == MercFetchJob.Complete)
            {
                if (l.WithEscort)
                { l.Delivered = true; l.Moved = j.Moved; MercDrive.ReturnEscort(); }
                else Stop(true, j.Moved);
            }
            else if (j.Phase == MercFetchJob.Failed) Stop(false, j.Reason);
        }

        static void Stop(bool success, int reason)
        { Stop(success, reason, true); }
        static void Stop(bool success, int reason, bool cancel)
        {
            Local l = _local; if (l == null) return;
            _local = null; if (cancel) Send(3, l);
            // Keep manual replacement orders, including mercs refused a seat.
            bool[] restore = new bool[l.Selected.Count];
            for (int i = 0; i < restore.Length; i++) restore[i] = l.Before[i] != l.Driving[i] && (ReferenceEquals(l.Selected[i].Order, l.Driving[i]) || (l.WithEscort && MercDrive.EscortMayRestore(l.Selected[i])));
            if (l.WithEscort && !success) MercDrive.CancelEscort();
            if (MercFetchBridge.Active && ReferenceEquals(l.Run, MercFetchBridge.Run)) MercFetchBridge.Finish(l.Run, success);
            if (success)
                for (int i = 0; i < restore.Length; i++) if (restore[i]) Mercs.FetchRestore(l.Selected[i], l.Before[i]);
            if (success) MercResupply.DepotRestocked();
            Say(success ? Loc.T("Воздушный груз доставлен на склад: ", "Airdrop delivered to depot: ") + reason
                : Loc.T("Получение груза прекращено: ", "Airdrop retrieval stopped: ") + Reason(reason), !success);
        }
        static void ResetWorld()
        {
            if (_generation == TowerSupport.WorldGeneration) return;
            _generation = TowerSupport.WorldGeneration;
            Stop(false, MercFetchJob.Expired, false);
            for (int i = 0; i < Host.Jobs.Length; i++) if (Host.Jobs[i].Active) MercFetchNative.Release(Host.Jobs[i]);
            Host = new MercFetchHost(new World());
            _master = -1; _nextHost = 0f; _scene = null; NeedSnapshot = true;
        }
        static string Reason(int reason)
        {
            switch (reason)
            {
                case MercFetchJob.Missing: return Loc.T("ящик исчез или ещё не приземлился", "crate gone or not landed");
                case MercFetchJob.VehicleLost: return Loc.T("машина уничтожена", "vehicle destroyed");
                case MercFetchJob.DriverLost: return Loc.T("водитель недоступен", "driver unavailable");
                case MercFetchJob.Taken: return Loc.T("машина или ящик заняты", "vehicle or crate taken");
                case MercFetchJob.WrongSide: return Loc.T("аэродром принадлежит другой стороне", "airfield held by another faction");
                case MercFetchJob.DepotLost: return Loc.T("склад недоступен или машина не у склада", "depot unavailable or vehicle not at depot");
                case MercFetchJob.Full: return Loc.T("склад заполнен; остаток в настоящем ящике", "depot full; remainder stays in the real crate");
                case MercFetchJob.Expired: return Loc.T("потеря связи или смена мастера", "connection lost or master changed");
                case MercFetchJob.Cancelled: return Loc.T("новый приказ или поездка прекращена", "new order or vehicle trip stopped");
                case MercFetchJob.Busy: return Loc.T("машина движется или склад занят", "vehicle moving or depot busy");
                case MercFetchJob.Blocked: return Loc.T("путь заблокирован", "route blocked");
                default: return Loc.T("несовместимые модули", "incompatible prerequisites");
            }
        }
        static void Say(string message, bool warn)
        { MercUi.OrderReply(message, warn); RevivalPlugin.L.LogInfo("MercFetch: " + message); }

        static void Send(int action)
        {
            Send(action, _local);
        }
        static void Send(int action, Local l)
        {
            if (l == null && action != 4) return;
            Request[0] = 10; Request[1] = action; Request[2] = l == null ? 0 : l.Serial;
            Request[3] = l == null ? 0 : l.Crate; Request[4] = l == null ? 0 : l.Vehicle; Request[5] = l == null ? 0 : l.Driver;
            if (Crocodile.IsMaster()) OnPacket(Request, Crocodile.LocalActor()); else MercRide.SendAAPacket(Request);
        }
        static void Publish(MercFetchJob j)
        {
            State[0] = 11; State[1] = j.Phase; State[2] = j.Actor; State[3] = j.Serial;
            State[4] = j.Crate; State[5] = j.Vehicle; State[6] = j.Driver; State[7] = j.Moved;
            State[8] = j.Reason; State[9] = j.Revision;
            MercRide.SendAAPacket(State);
        }
        internal static void OnPacket(float[] f, int sender)
        {
            if (!Enabled || !MercFetchBridge.Available || !MercFetchNative.Available || f == null || f.Length == 0) return;
            if (f[0] == 10 && Crocodile.IsMaster() && sender > 0 && MercFetchHost.Packet(f, 10, 8))
            {
                int action = (int)f[1], serial = (int)f[2], crate = (int)f[3], vehicle = (int)f[4], driver = (int)f[5];
                if (action == 4) { for (int i = 0; i < Host.Jobs.Length; i++) if (Host.Jobs[i].Active) Publish(Host.Jobs[i]); return; }
                if (serial <= 0 || crate <= 0 || vehicle <= 0 || driver <= 0) return;
                int reason = 0;
                if (action == 1) reason = Host.Begin(sender, serial, crate, vehicle, driver, Time.time);
                else if (action == 2) reason = Host.Unload(sender, serial, crate, vehicle, driver, Time.time);
                else if (action == 3) { Host.Cancel(sender, serial); return; }
                else if (action == 5) { Host.Touch(sender, serial, crate, vehicle, driver, Time.time); return; }
                else return;
                if (reason != 0)
                {
                    MercFetchJob reply = new MercFetchJob(); reply.Actor = sender; reply.Serial = serial; reply.Crate = crate;
                    reply.Vehicle = vehicle; reply.Driver = driver; reply.Phase = MercFetchJob.Failed; reply.Reason = reason;
                    reply.Revision = 1; Publish(reply); LocalState(reply);
                }
            }
            else if (f[0] == 11 && sender == MercAA.MasterActor() && !Crocodile.IsMaster() && MercFetchHost.Packet(f, 11, 11))
            {
                if (f[1] < 1 || f[1] > 5 || f[2] <= 0 || f[3] <= 0 || f[4] <= 0 || f[5] <= 0 || f[6] <= 0 || f[7] > 42 || f[8] > 12) return;
                MercFetchJob j = Host.Find((int)f[2]);
                if (j == null) for (int i = 0; i < Host.Jobs.Length; i++) if (Host.Jobs[i].Actor == 0) { j = Host.Jobs[i]; break; }
                if (j == null || (j.Serial > f[3]) || (j.Serial == f[3] && j.Revision > f[9])) return;
                if (j.Serial == f[3] && j.Revision == f[9])
                {
                    // A native spawn may arrive after its first snapshot.
                    if (j.Active && j.Matches((int)f[2], (int)f[3], (int)f[4], (int)f[5], (int)f[6])
                        && MercFetchNative.Resolve(j)) MercFetchNative.Apply(j);
                    return;
                }
                j.Actor = (int)f[2]; j.Serial = (int)f[3]; j.Crate = (int)f[4]; j.Vehicle = (int)f[5]; j.Driver = (int)f[6];
                j.Phase = (int)f[1]; j.Moved = (int)f[7]; j.Reason = (int)f[8]; j.Revision = (int)f[9];
                if (j.Active && MercFetchNative.Resolve(j)) MercFetchNative.Apply(j); else if (!j.Active) MercFetchNative.Release(j);
                LocalState(j);
            }
        }
    }

    internal static partial class Mercs
    {
        internal static void FetchRestore(Record record, MercOrder before)
        { if (!record.Dead && !record.Deserted) Give(new List<Record>(new Record[] { record }), before); }
    }
}

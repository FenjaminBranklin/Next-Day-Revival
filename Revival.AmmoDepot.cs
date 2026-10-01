// Z M2: D2a is storage, never an ammo generator. Photon master owns the ledger.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AmmoDepot
    {
        internal const int State = 21, Ask = 22, Work = 23, Accepted = 24, Item = 25, Done = 26, Cancel = 27, SnapshotAsk = 28, Loot = 29;
        const string Progress = "ammo-depot-unload";
        const float K = 2.8f;
        internal static DepotStore Store = new DepotStore();
        internal static Vector3 At = new Vector3(4090f, 0f, -1040f); // F3 all-collider surveyed D2a anchor
        static readonly float[] Packet = new float[16];
        // Prewarm once. Stock/active-combat HUD changes never format numbers.
        static readonly string[][] LabelsRu = Labels(true), LabelsEn = Labels(false);
        static readonly Dictionary<int, float> RequestNext = new Dictionary<int, float>();
        static float _next, _snapshotNext, _jobEnd, _lootNext;
        static int _host = -1, _sent = -1, _request, _job, _jobTruck, _workTicket, _workGun, _workLimit, _workCount;
        static int _workActor, _workHost, _workSeenHost = -1, _workSeenTicket;
        static bool _ready, _near, _dispatched;
        static AmmoDepotCargo.Truck _truck, _working;
        static string _stock, _cargo, _prompt;
        static readonly string[] StockLabels = new string[4], CargoLabels = new string[4];
        static int _textRevision = -1, _cargoShell = -1, _cargoBelt = -1, _cargoMeds = -1, _cargoTools = -1;
        static GUIStyle _label;
        static Func<object> _roomGetter;
        static object _room;
        static int _snapshotHost = -1;

        internal static void Warm() { Label(0, 0); }

        static string[][] Labels(bool ru)
        {
            string[] suffix = ru ? new string[] { " снарядов", " лент", " аптечек", " ремкомплектов" }
                : new string[] { " shells", " belt boxes", " medkits", " toolkits" };
            string[][] labels = new string[4][];
            for (int k = 0; k < 4; k++)
            {
                labels[k] = new string[DepotStore.Capacity + 1];
                for (int n = 0; n < labels[k].Length; n++) labels[k][n] = n.ToString() + suffix[k];
            }
            return labels;
        }
        static string Label(int category, int count)
        { int n = Math.Min(DepotStore.Capacity, Math.Max(0, count)); return Loc.T(LabelsRu[category][n], LabelsEn[category][n]); }

        internal static void Reset()
        {
            Store = new DepotStore(); _ready = _near = _dispatched = false;
            _host = _sent = _textRevision = -1; _next = _snapshotNext = 0f;
            _truck = _working = null; _stock = _cargo = _prompt = null; _job = 0;
            _workSeenHost = -1; _workSeenTicket = 0;
            _snapshotHost = -1; RequestNext.Clear();
            NativeActionProgress.End(Progress); AmmoDepotCargo.Reset();
        }
        internal static bool WorksOn(object container)
        { return _working != null && object.ReferenceEquals(_working.Container, container); }
        static void Send(int kind, int ticket, int actor, int request, int truck, int gun, int limit)
        {
            // Interaction-only private packets: nested local callbacks cannot
            // overwrite the outer request before it reaches other clients.
            float[] f = new float[16];
            f[0] = kind; f[1] = ticket; f[2] = actor; f[3] = request;
            f[4] = truck; f[5] = gun; f[6] = limit; f[7] = MercAA.MasterActor();
            // Explicit local delivery: FlakNet's Photon path uses Others.
            FlakNet.SendPacket(f, true);
            OnPacket(f, Crocodile.LocalActor());
        }
        static bool Close(int actor, Vector3 at, float reach)
        {
            GameObject p = Crocodile.PlayerByActor(actor);
            return Crocodile.PlayerUp(p) && (p.transform.position - at).sqrMagnitude <= reach * reach;
        }
        static bool OwnerMayUnload(int actor)
        {
            return AirfieldHold.State.ByPlayers && AirfieldHold.State.Holder >= 0
                && TowerRadar.PlayerSide(actor) == AirfieldHold.State.Holder;
        }
        static bool SupplyGun(Flak.Gun g, int actor)
        {
            if (g == null || g.Root == null || !g.Ammo.Known || !g.Ammo.Limited || !AirDefenceDamage.Alive(g.Index)) return false;
            MercAAPost merc = MercAA.Gun(g.Index);
            if (merc == null) merc = MercAA.Held(g.Index + 7);
            return merc != null ? merc.Actor == actor || merc.Side == TowerRadar.PlayerSide(actor)
                : FlakAmmo.MaySupply(g, actor);
        }
        internal static bool TrySupply(Flak.Gun g, int actor)
        { return TrySupply(g, actor, false); }
        static bool TrySupply(Flak.Gun g, int actor, bool bulk)
        {
            if (!Crocodile.IsMaster() || !_ready || !SupplyGun(g, actor)
                || (g.Ammo.Actor >= 0 && Time.time < g.Ammo.Until)) return false;
            int item = g.ShortRange ? 2077 : 2076, amount = g.ShortRange ? 100 : 1;
            if (g.Ammo.Stock > FlakAmmoStock.Capacity(g.ShortRange) - amount) return false;
            int load = bulk && !g.ShortRange ? Math.Min(Flak.RoundsPerLoad, FlakAmmoStock.Capacity(false) - g.Ammo.Stock) : 1;
            MercAAPost merc = MercAA.Gun(g.Index); if (merc == null) merc = MercAA.Held(g.Index + 7);
            int side = merc == null ? TowerRadar.PlayerSide(actor) : merc.Side;
            int slot = Store.Find(item);
            if (Store.Known && Store.Hp > 0f && slot >= 0 && (g.Root.position - At).sqrMagnitude <= (25f * K) * (25f * K)
                && side == AirfieldHold.State.Holder)
            {
                int moved = 0;
                while (slot >= 0 && moved < load) { Store.Remove(slot); moved++; slot = Store.Find(item); }
                Broadcast(); Credit(g, moved * amount); return true;
            }
            if (Store.Actor >= 0 && Time.time < Store.Until) return Store.Gun == g.Index;
            AmmoDepotCargo.Truck truck = AmmoDepotCargo.Near(g.Root.position, 25f * K, item);
            if (truck == null) return false;
            int ticket = Store.Begin(actor, ++_request, truck.Owner, truck.View, g.Index, load, Time.time, true);
            if (ticket == 0) return false;
            _dispatched = true;
            Send(Work, ticket, actor, 0, truck.View, g.Index, load); return true;
        }
        static void Credit(Flak.Gun g, int amount)
        {
            g.Ammo.Stock += amount; g.Ammo.Revision++;
            if (g.Rounds <= 0 && !g.Reloading) Flak.StartReload(g, g.ShortRange ? ShortRangeCore.ReloadSeconds : Flak.ReloadSeconds());
            FlakAmmo.SendState(g);
        }
        internal static void Tick()
        {
            FrameProf.S(FrameProf.S_AmmoDepotT);
            try { Step(); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Ammo depot: " + ex.Message); StopWork(); }
            finally { FrameProf.E(FrameProf.S_AmmoDepotT); }
        }
        static void Step()
        {
            if (Time.time >= _next)
            {
                _next = Time.time + 0.5f;
                if (_roomGetter == null)
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    MethodInfo m = photon == null ? null : AccessTools.PropertyGetter(photon, "room");
                    if (m == null) return;
                    _roomGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), m);
                }
                object room = _roomGetter();
                if (!object.ReferenceEquals(room, _room)) { Reset(); _room = room; }
                if (room == null || !Airfield.On || !EastWorld.Extends || !AirfieldObjects.Ready) { _near = false; return; }
                FlakNet.EnsureHooked();
                if (!_ready)
                {
                    Vector3 c, h; Quaternion r; float y;
                    if (!EastZones.Find("D2a", out c, out h, out r) || !EastWorld.TerrainHeight(At, out y)) return;
                    At.y = y + 0.5f; _ready = true;
                }
                int host = MercAA.MasterActor();
                if (host != _host)
                {
                    bool first = _host < 0;
                    _host = host; Store.NewMaster(); _working = null; _dispatched = false;
                    _sent = -1; _job = 0; NativeActionProgress.End(Progress);
                    // First room authority starts empty; migration keeps received goods.
                    if (first && Crocodile.IsMaster() && !Store.Known && Store.Revision == 0) Store.Known = true;
                }
                GameObject me = MapTools.LocalPlayer();
                _near = me != null && Crocodile.PlayerUp(me) && (me.transform.position - At).sqrMagnitude <= (8f*K)*(8f*K);
                // Cargo discovery/read is asleep unless near storage, a gun or a merc post needs ammo.
                bool usingCargo = _near || _working != null || Store.Actor >= 0;
                for (int i = 0; i < 7; i++)
                {
                    Flak.Gun g = Flak.ByIndex(i); if (g == null || g.Root == null) continue;
                    if (me != null && (me.transform.position - g.Root.position).sqrMagnitude <= 100f) usingCargo = true;
                    MercAAPost merc = MercAA.Gun(i); if (merc == null) merc = MercAA.Held(i + 7);
                    if (merc != null && g.Ammo.Limited && g.Ammo.Stock == 0 && AirDefenceDamage.Alive(i)) usingCargo = true;
                }
                if (usingCargo) AmmoDepotCargo.Sample();
                _truck = _near ? AmmoDepotCargo.Near(At, 18f*K, 0) : null;
                if (Crocodile.IsMaster())
                {
                    Store.Capture(AirfieldHold.State.ByPlayers, true);
                    MasterWork();
                    for (int i = 0; i < 7; i++)
                    {
                        Flak.Gun g = Flak.ByIndex(i); MercAAPost merc = MercAA.Gun(i);
                        if (merc == null) merc = MercAA.Held(i + 7);
                        if (g != null && merc != null && g.Ammo.Stock == 0) TrySupply(g, merc.Actor, true);
                    }
                    if (Store.Known && Store.Revision != _sent) Broadcast();
                }
                else if (_near && !Store.Known && Time.time >= _snapshotNext)
                { _snapshotNext = Time.time + 3f; Send(SnapshotAsk, 0, Crocodile.LocalActor(), 0, 0, -1, 0); }
                CacheText();
            }
            if (!_ready) return;
            OwnerWork();
            if (_job != 0)
            {
                if (!_near || Time.time > _jobEnd + 28f || (_jobEnd > Time.time && !NativeActionProgress.IsActive(Progress)))
                {
                    Send(Cancel, _job, Crocodile.LocalActor(), 0, _jobTruck, -1, 0);
                    _job = 0; NativeActionProgress.End(Progress);
                }
                else if (Time.time >= _jobEnd) NativeActionProgress.End(Progress);
                return;
            }
            if (!_near || RadarScope.InView || !Store.Known) return;
            if (GameUi.KeyDown(KeyCode.U) && _truck != null && Store.Hp > 0f)
                Send(Ask, 0, Crocodile.LocalActor(), ++_request, _truck.View, -1, 0);
            // Select category; one genuine Photon pickup per interaction, no burst.
            int category = GameUi.KeyDown(KeyCode.Alpha1) ? 0 : GameUi.KeyDown(KeyCode.Alpha2) ? 1
                : GameUi.KeyDown(KeyCode.Alpha3) ? 2 : GameUi.KeyDown(KeyCode.Alpha4) ? 3 : -1;
            if (category >= 0 && Time.time >= _lootNext)
            { _lootNext = Time.time + 0.5f; Send(Loot, 0, Crocodile.LocalActor(), ++_request, 0, category, 0); }
        }
        static bool UnloadValid(AmmoDepotCargo.Truck truck, int actor)
        {
            return truck != null && truck.Parked && OwnerMayUnload(actor) && Close(actor, At, 8f*K)
                && (truck.Root.position - At).sqrMagnitude <= (18f*K)*(18f*K);
        }
        static bool GunTruckValid(AmmoDepotCargo.Truck truck, int gun, int actor)
        {
            Flak.Gun g = Flak.ByIndex(gun);
            return truck != null && SupplyGun(g, actor) && (truck.Root.position - g.Root.position).sqrMagnitude <= (25f*K)*(25f*K);
        }
        static void MasterWork()
        {
            if (Store.Actor < 0) return;
            AmmoDepotCargo.Truck truck = AmmoDepotCargo.ByView(Store.Truck);
            if (Time.time >= Store.Until || truck == null || truck.Owner != Store.Owner || !truck.Parked
                || (Store.Gun < 0 ? !UnloadValid(truck, Store.Actor) : !GunTruckValid(truck, Store.Gun, Store.Actor)))
            { Send(Done, Store.Ticket, Store.Actor, 0, Store.Truck, Store.Gun, Store.Received); Store.Cancel(); _dispatched = false; return; }
            if (!_dispatched && Time.time >= Store.Ready)
            {
                _dispatched = true;
                Send(Work, Store.Ticket, Store.Actor, 0, Store.Truck, Store.Gun, Store.Limit);
            }
        }
        static void StopWork() { _working = null; }
        static void OwnerWork()
        {
            if (_working == null) return;
            if (_workHost != MercAA.MasterActor() || _working.Owner != Crocodile.LocalActor()
                || !_working.Parked || _working.Busy || (_workGun < 0 ? !UnloadValid(_working, _workActor) : !GunTruckValid(_working, _workGun, _workActor)))
            { FinishWork(); return; }
            if (_workCount >= _workLimit || _working.Ids == null) { FinishWork(); return; }
            int item = _workGun < 0 ? 0 : Flak.ByIndex(_workGun) == null ? -1 : Flak.ByIndex(_workGun).ShortRange ? 2077 : 2076;
            int walked = 0;
            while (_working.Scan < _working.Ids.Length && walked++ < 16)
            {
                int slot = _working.Scan++;
                DepotGood good = _working.Good(slot);
                if (!DepotStore.Valid(good) || (item != 0 && good.Id != item)) continue;
                if (!_working.Remove(slot)) { FinishWork(); return; }
                // At most one native RPC/removal per frame. No rounds loop.
                Array.Clear(Packet, 0, Packet.Length);
                Packet[0] = Item; Packet[1] = _workTicket; Packet[2] = _workActor; Packet[4] = _working.View;
                Packet[5] = _workGun; Packet[7] = _workHost; Packet[8] = ++_workCount;
                WriteGood(Packet, 9, good);
                OnPacket(Packet, Crocodile.LocalActor()); FlakNet.SendPacket(Packet, true); return;
            }
            if (_working.Scan >= _working.Ids.Length) FinishWork();
        }
        static void FinishWork()
        {
            if (_working == null) return;
            int view = _working.View; _working = null;
            Send(Done, _workTicket, _workActor, 0, view, _workGun, _workCount);
        }
        static void WriteGood(float[] f, int at, DepotGood g)
        { f[at] = g.Id; f[at+1] = g.Bullets; f[at+2] = g.Clip; f[at+3] = g.Condition; f[at+4] = g.Water; f[at+5] = g.Energy; }
        static DepotGood ReadGood(float[] f, int at)
        {
            DepotGood g = new DepotGood((int)f[at], (int)f[at+1], (int)f[at+2], f[at+3], f[at+4]); g.Energy = f[at+5]; return g;
        }
        static bool Integers(float[] f, int at)
        { return f[at] == (int)f[at] && f[at+1] == (int)f[at+1] && f[at+2] == (int)f[at+2]; }
        static void Broadcast()
        {
            float[] f = new float[5 + Store.Count * 6];
            f[0] = State; f[1] = Store.Revision; f[2] = Store.Hp; f[3] = Store.Seeded ? 1f : 0f; f[4] = Store.Count;
            for (int i = 0; i < Store.Count; i++) WriteGood(f, 5+i*6, Store.Goods[i]);
            _sent = Store.Revision; FlakNet.SendPacket(f, true);
        }
        internal static void OnPacket(float[] f, int sender)
        {
            if (f == null || f.Length < 5) return;
            for (int i = 0; i < f.Length; i++) if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
            if (f[0] != (int)f[0]) return;
            int kind = (int)f[0], host = MercAA.MasterActor(); bool master = Crocodile.IsMaster();
            if (kind == State)
            {
                if (master || sender != host || f[1] < 0f || f[1] != (int)f[1] || f[2] < 0f || f[2] > 1f
                    || (f[3] != 0f && f[3] != 1f) || f[4] < 0f || f[4] > DepotStore.Capacity || f[4] != (int)f[4]
                    || f.Length != 5+(int)f[4]*6 || (_snapshotHost == sender && Store.Known && f[1] < Store.Revision)) return;
                for (int i = 0; i < (int)f[4]; i++) if (!Integers(f, 5+i*6) || !DepotStore.Valid(ReadGood(f, 5+i*6))) return;
                Store.Count = (int)f[4]; Store.Hp = f[2]; Store.Seeded = f[3] == 1f; Store.Revision = (int)f[1]; Store.Known = true;
                _snapshotHost = sender;
                for (int i = 0; i < Store.Count; i++) Store.Goods[i] = ReadGood(f, 5+i*6);
                _textRevision = -1; return;
            }
            if (f.Length != 16 || f[7] != host) return;
            for (int i = 1; i <= 8; i++) if (f[i] != (int)f[i]) return;
            int ticket = (int)f[1], actor = (int)f[2], view = (int)f[4], gun = (int)f[5];
            if (kind == Work && sender == host)
            {
                AmmoDepotCargo.Truck truck = AmmoDepotCargo.Resolve(view);
                if (truck == null || truck.Owner != Crocodile.LocalActor() || !truck.Parked || truck.Busy || _working != null
                    || ticket <= 0 || (gun < 0 ? !UnloadValid(truck, actor) : !GunTruckValid(truck, gun, actor))
                    || f[6] < 1f || f[6] > DepotStore.Capacity) return;
                if (_workSeenHost == host && ticket <= _workSeenTicket) return;
                _workSeenHost = host; _workSeenTicket = ticket;
                _working = truck; _working.Scan = 0; _workTicket = ticket; _workGun = gun; _workActor = actor;
                _workHost = host; _workLimit = (int)f[6]; _workCount = 0; return;
            }
            if (kind == Accepted && sender == host && actor == Crocodile.LocalActor())
            {
                if (ticket <= 0 || _job != 0) return;
                if (!NativeActionProgress.Begin(Progress, Loc.T("Разгрузка грузовика", "Unloading truck"), 4f, true, "repair", "vehicle_repair_01"))
                { Send(Cancel, ticket, actor, 0, view, -1, 0); return; }
                _job = ticket; _jobTruck = view; _jobEnd = Time.time + 4f; return;
            }
            if (kind == Done && sender == host && actor == Crocodile.LocalActor() && ticket == _job)
            { _job = 0; NativeActionProgress.End(Progress); if (!master) return; }
            if (kind == Done && sender == host && _working != null && ticket == _workTicket)
            { StopWork(); if (!master) return; }
            if (!master || !_ready || !Store.Known) return;
            if (kind == SnapshotAsk || kind == Ask || kind == Loot)
            {
                float next;
                if (RequestNext.TryGetValue(sender, out next) && Time.time < next) return;
                RequestNext[sender] = Time.time + 0.5f;
            }
            if (kind == SnapshotAsk)
            { if (Close(sender, At, 12f*K)) Broadcast(); return; }
            if (kind == Ask)
            {
                if (actor != sender || gun != -1 || f[3] <= 0f) return;
                AmmoDepotCargo.Truck truck = AmmoDepotCargo.ByView(view);
                if (!UnloadValid(truck, actor)) return;
                int token = Store.Begin(actor, (int)f[3], truck.Owner, view, -1, DepotStore.Capacity, Time.time, true);
                if (token > 0) { _dispatched = false; Send(Accepted, token, actor, (int)f[3], view, -1, Store.Limit); }
            }
            else if (kind == Item)
            {
                if (view != Store.Truck || gun != Store.Gun || actor != Store.Actor || !Integers(f, 9)) return;
                AmmoDepotCargo.Truck truck = AmmoDepotCargo.ByView(view);
                if (truck == null || truck.Owner != sender || !truck.Parked
                    || (gun < 0 ? !UnloadValid(truck, actor) : !GunTruckValid(truck, gun, actor))) return;
                DepotGood good = ReadGood(f, 9);
                Flak.Gun g = gun < 0 ? null : Flak.ByIndex(gun);
                if (gun >= 0 && (!SupplyGun(g, actor) || good.Id != (g.ShortRange ? 2077 : 2076)
                    || g.Ammo.Stock > FlakAmmoStock.Capacity(g.ShortRange) - (g.ShortRange ? 100 : 1))) return;
                if (Store.Receipt(sender, ticket, (int)f[8], Time.time, good))
                { if (g != null) Credit(g, g.ShortRange ? 100 : 1); else Broadcast(); }
            }
            else if (kind == Done || kind == Cancel)
            {
                if (ticket != Store.Ticket || Store.Actor < 0 || (sender != Store.Owner && sender != Store.Actor)) return;
                int a = Store.Actor, v = Store.Truck, g = Store.Gun, n = Store.Received;
                Store.Cancel(); _dispatched = false;
                Send(Done, ticket, a, 0, v, g, n);
            }
            else if (kind == Loot && actor == sender && gun >= 0 && gun <= 3 && Close(sender, At, 8f*K))
            {
                for (int i = 0; i < Store.Count; i++)
                {
                    int id = Store.Goods[i].Id;
                    int category = id == 2076 ? 0 : id == 2077 ? 1 : id >= 7011 && id <= 7016 ? 2 : 3;
                    if (category != gun) continue;
                    // Enemy players may loot storage/wreck, using native pickups.
                    GameObject player = Crocodile.PlayerByActor(sender);
                    if (AmmoDepotCargo.Drop(Store.Goods[i], player.transform.position + player.transform.forward * 2f + Vector3.up) != null)
                    { Store.Remove(i); Broadcast(); }
                    break;
                }
            }
        }
        internal static void Blast(Vector3 at, float radius, float peak)
        {
            if (!_ready || !Crocodile.IsMaster() || !Store.Known) return;
            int actor = Store.Actor, ticket = Store.Ticket, truck = Store.Truck, gun = Store.Gun;
            float amount = AaDamageCore.Blast(Vector3.Distance(At, at), radius, 8f*K, peak);
            if (Store.Hit(amount, true))
            {
                _textRevision = -1;
                if (actor >= 0 && Store.Actor < 0) { _dispatched = false; Send(Done, ticket, actor, 0, truck, gun, 0); }
                Broadcast();
            }
        }
        static void CacheText()
        {
            if (!_near) return;
            if (_textRevision != Store.Revision || _stock == null)
            {
                int shells = 0, belts = 0, meds = 0, tools = 0;
                for (int i = 0; i < Store.Count; i++)
                {
                    int id = Store.Goods[i].Id;
                    if (id == 2076) shells++; else if (id == 2077) belts++;
                    else if (id >= 7011 && id <= 7016) meds++; else tools++;
                }
                _stock = Store.Hp <= 0f ? Loc.T("Склад - РАЗРУШЕН (добыча)", "Depot - WRECK (loot)") : Loc.T("Склад", "Depot");
                StockLabels[0] = Label(0, shells); StockLabels[1] = Label(1, belts);
                StockLabels[2] = Label(2, meds); StockLabels[3] = Label(3, tools);
                _textRevision = Store.Revision;
            }
            _prompt = Loc.T("[1-4] Забрать: снаряд / лента / аптечка / ремкомплект. [U] Разгрузить грузовик (4 с)",
                "[1-4] Loot: shell / belt / medkit / toolkit. [U] Unload truck (4 s)");
            if (_truck == null) { _cargo = null; return; }
            int s = _truck.Count(2076), b = _truck.Count(2077), m = 0, t = _truck.Count(10005)+_truck.Count(2064);
            for (int id = 7011; id <= 7016; id++) m += _truck.Count(id);
            if (_cargo == null || s != _cargoShell || b != _cargoBelt || m != _cargoMeds || t != _cargoTools)
            {
                _cargoShell = s; _cargoBelt = b; _cargoMeds = m; _cargoTools = t;
                _cargo = Loc.T("Грузовик", "Parked truck");
                CargoLabels[0] = Label(0, s); CargoLabels[1] = Label(1, b);
                CargoLabels[2] = Label(2, m); CargoLabels[3] = Label(3, t);
            }
        }
        internal static void Draw()
        {
            if (!_near || !Store.Known || RadarScope.InView) return;
            if (_label == null) { _label = new GUIStyle(GUI.skin.label); _label.alignment = TextAnchor.MiddleCenter; }
            float y = Screen.height * 0.65f;
            GUI.Label(new Rect(0f, y, Screen.width, 25f), _stock, _label);
            float width = 180f, left = (Screen.width - 4f*width)*0.5f;
            for (int i = 0; i < 4; i++) GUI.Label(new Rect(left+i*width, y+25f, width, 25f), StockLabels[i], _label);
            if (_cargo != null)
            {
                GUI.Label(new Rect(0f, y+50f, Screen.width, 25f), _cargo, _label);
                for (int i = 0; i < 4; i++) GUI.Label(new Rect(left+i*width, y+75f, width, 25f), CargoLabels[i], _label);
            }
            GUI.Label(new Rect(0f, y+100f, Screen.width, 25f), _prompt, _label);
        }
    }
}

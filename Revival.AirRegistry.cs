// Air bridge AB1: the aircraft registry on the master server (docs/ai/AIR_BRIDGE.md
// 4.2/4.5). Exactly one An-2 and one Mi-8 exist; the master server keeps them
// (docs/ai/tasks/air-bridge/server/AirRegistry.cs), never the Photon room - the
// rooms of empty regions reset, the registry does not.
//
//   TRANSPORT  storageId 7702 over the game's own storage message, like the
//              merc roster (7700) and the tower payments (7701): a command is
//              a playerStorage bag whose description is the op text, the
//              answer a storageListResponse with one 7702 bag, taken off
//              BackendManager.MsgStorageListRecieve before the stash sees it.
//   PROBE      every client asks once. An answer: live. No connection for 60 s
//              or plain storage bags instead of ours: keep asking, with all
//              local aircraft spawns blocked until the registry answers.
//   ROOM       only the Photon master acts. Per aircraft it follows
//              AirPlan.Decide: claim a parked/flying one of this region and
//              build it at the stored pose with fuel, parts and cargo; spawn
//              a new one at the MAIN airfield only when the registry says none
//              or destroyed (+ delay); drop a local copy the registry no
//              longer gives to this room. The object's view id is a room
//              property (ndr.air.an2 / ndr.air.mi8), so a new master adopts it
//              instead of building a second.
//   WRITES     AirWriteGate: on park/land, damage (parts), cargo (batched),
//              parked refuelling, lease renewal (30 s flying, 60 s parked);
//              burning = destroy. One op in flight.
//   GATES      PlayerAn2.ApronSpawn and VehicleFinds' Mi-8 spots yield
//              (Owns); direct spawns, including old clients' requests, yield.
//   ADMIN      panel buttons: show, reset An-2 / Mi-8 (the server checks the
//              admin list), probe again.
//
// Cost: Tick returns at once between its 0.5 s steps; a step reads two
// transforms and the write gate; the Mi-8 hold is hashed every 5 s through
// emitted array readers. No scan, no per-frame allocation (op texts and
// answers allocate per op only). F6 slot "AirRegistry.Tick".
//
// C# 3.0 (.NET 3.5). ASCII only; player text through Loc.T.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class AirRegistry
    {
        internal const int StorageId = 7702;
        const float StepEvery = 0.5f, PollLive = 30f, PollProbe = 10f, ProbeGiveUp = 60f, CargoEvery = 5f;
        const float SettleAfterJoin = 20f;               // objects of a previous master arrive first
        const int RestorePerStep = 20;
        static readonly Vector3 Mi8Stand = new Vector3(4510f, 0f, 250f);   // vehicle_spawns.json af-apron-mi8
        const float Mi8StandHeading = 90f;

        internal static ConfigEntry<bool> CfgEnabled;

        enum Mode { Probing, Live, Off }
        static Mode _mode = Mode.Probing;
        static string _offReason = "";

        static readonly AirBook _book = new AirBook();
        static bool _haveBook;
        static double _serverOffset;
        static int _seq = -1;
        static readonly AirOpLink _link = new AirOpLink();
        const int OpClaim = 1, OpSpawn = 2, OpSnap = 3, OpDestroy = 4, OpAdmin = 5;
        static int _opKind;
        static Craft _opCraft;
        static string _adminPending, _adminNote = "";

        static float _next, _nextPoll, _pollSentAt = -1f, _firstTry = -1f, _playerSince = -1f;
        static bool _pollPending, _wasMaster, _warned;
        static int _plain;
        static string _room = "", _region;
        static readonly HashSet<int> _legacy = new HashSet<int>();
        static bool _legacyTaken;
        static readonly List<GameObject> _scratch = new List<GameObject>();

        sealed class Craft
        {
            public readonly string Id;
            public readonly bool An2;
            public GameObject Go;
            public int View, Tok = -1;
            public readonly AirWriteGate Gate = new AirWriteGate();
            // Last read state (reused).
            public Vector3 Pos;
            public float Yaw, Fuel01, Wear;
            public int Parts;
            public bool InAir;
            public readonly int[] Slot = new int[AirCraft.MaxCargo], Item = new int[AirCraft.MaxCargo], Count = new int[AirCraft.MaxCargo], Clip = new int[AirCraft.MaxCargo];
            public readonly float[] Cond = new float[AirCraft.MaxCargo], Water = new float[AirCraft.MaxCargo], Energy = new float[AirCraft.MaxCargo];
            public int CargoN = -1, CargoHash;
            public float NextCargo;
            // Cargo to put back after a rehydrate.
            public readonly int[] RItem = new int[AirCraft.MaxCargo], RCount = new int[AirCraft.MaxCargo], RClip = new int[AirCraft.MaxCargo];
            public readonly float[] RCond = new float[AirCraft.MaxCargo], RWater = new float[AirCraft.MaxCargo], REnergy = new float[AirCraft.MaxCargo];
            public int RN, RAt, RFree = -1;
            public bool Restoring;
            public bool RClear;
            public int RClearAt;
            public float RestoreAfter;
            // A spawn being asked for: the values are fixed once and resent as they are.
            public bool Pending;
            public Vector3 PAt;
            public float PYaw, PFuel01;
            public int PParts;
            public GameObject PAdopt;
            public bool DropWarned, Destroying;
            public Craft(string id) { Id = id; An2 = id == "an2"; Key = "ndr.air." + id; }
            public readonly string Key;
        }

        static readonly Craft[] _crafts = { new Craft("an2"), new Craft("mi8") };

        internal static void BindConfig(ConfigFile cfg)
        {
            AirBridge.BindConfig(cfg);
            CfgEnabled = cfg.Bind("AirRegistry", "Enabled", true,
                "Air bridge AB1: exactly one An-2 and one Mi-8 exist, kept by the master "
                + "server (docs/ai/AIR_BRIDGE.md). A region that loads shows its parked "
                + "aircraft where they were left, with fuel, parts and cargo; the airfield "
                + "builds a new one only after the old one was destroyed. An unavailable "
                + "registry blocks spawns and keeps asking. Disabled explicitly: legacy local spawns.");
        }

        // ================================================================ gates

        /// <summary>True while the registry decides about this aircraft (live,
        /// or still asking the server): the old local spawns hold back.</summary>
        internal static bool Owns(string id)
        {
            return CfgEnabled != null && CfgEnabled.Value && AirBook.IndexOf(id) >= 0;
        }

        /// <summary>Direct spawn keys yield while the registry owns the aircraft.</summary>
        internal static bool SpawnKeyAllowed()
        {
            return CfgEnabled == null || !CfgEnabled.Value;
        }

        internal static string SpawnKeyRefused()
        {
            return Loc.T("Ан-2 и Ми-8 - по одному на весь мир. Ищите их на аэродроме.",
                         "There is one An-2 and one Mi-8 in the whole world. Look for them at the airfield.");
        }

        // ================================================================ admin

        internal static string Describe()
        {
            string head = "Aircraft registry (storage 7702): " + (CfgEnabled == null || !CfgEnabled.Value ? "switched off"
                : _mode == Mode.Live ? "live" : _mode == Mode.Probing ? "asking the master server" : "waiting - " + _offReason);
            head += "\nThis room: " + (_room.Length == 0 ? "-" : _room) + ", region " + (_region ?? "-")
                + (RevivalTroopInsertion.MasterClient() ? ", master" : ", not the master");
            for (int i = 0; i < _crafts.Length; i++)
            {
                Craft c = _crafts[i];
                head += "\n  " + c.Id + " here: " + (c.Go == null ? "none" : "view " + c.View + ", token " + c.Tok
                    + (c.Restoring ? ", restoring cargo " + c.RAt + "/" + c.RN : ""));
            }
            if (_haveBook) head += "\n" + _book.Describe(ServerNow());
            if (_adminNote.Length > 0) head += "\nLast admin op: " + _adminNote;
            return head;
        }

        /// <summary>id: an2, mi8 or all. The server checks the admin list.</summary>
        internal static string AdminReset(string id)
        {
            if (_mode != Mode.Live) return "The registry is not live" + (_offReason.Length > 0 ? ": " + _offReason : ".");
            _adminPending = id;
            _adminNote = "reset " + id + " sent";
            return "Registry reset of " + id + " sent - the airfield builds a new one; 'show' has the answer.";
        }

        internal static string Resync()
        {
            _mode = Mode.Probing; _offReason = ""; _plain = 0; _firstTry = -1f; _nextPoll = 0f; _pollPending = false;
            return "Asking the master server for the aircraft registry again.";
        }

        // ================================================================ tick

        internal static void Tick()
        {
            AirBridge.Frame();
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + StepEvery;
            try { AirLanding.Tick(); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirBridge landing sites: " + ex.Message); }
            if (CfgEnabled == null || !CfgEnabled.Value) return;
            try { Step(Time.realtimeSinceStartup); }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("AirRegistry: " + ex); }
            }
        }

        static void Step(float now)
        {
            int st = _link.Step(now);
            if (st == 1) Transmit(null);
            else if (st == 2) Transmit(_link.Text);
            else if (st == 3)
            {
                RevivalPlugin.L.LogWarning("AirRegistry: op " + _opKind + " timed out - the next answer decides again.");
                if (_opCraft != null) { _opCraft.Destroying = false; _opCraft.Gate.Reset(); }
                _opKind = 0; _opCraft = null;
            }

            if (_mode != Mode.Live)
            {
                // Admin resync also keeps the old lease deadline in force.
                // Waiting for the backend must never keep a stale copy alive.
                if (_haveBook && RevivalTroopInsertion.MasterClient() && AirPlan.SafeRoom(RoomName()) == _room)
                    for (int i = 0; i < _crafts.Length; i++)
                    {
                        Craft c = _crafts[i];
                        if (c.Go != null && AirPlan.Decide(_book.Get(c.Id), ServerNow(), _book.Delay,
                            _region, _room, true, c.Tok, false) == AirAction.Drop) Plan(c, now);
                    }
                Probe(now); return;
            }

            string region = AirPlan.RegionOf(MapScene.Current);
            string room = AirPlan.SafeRoom(RoomName());
            if (room != _room || region != _region)
            {
                ForgetAll();
                _link.Done(); _opKind = 0; _opCraft = null;
                _room = room; _region = region; _legacy.Clear(); _legacyTaken = false; _playerSince = -1f;
                for (int i = 0; i < _crafts.Length; i++) _crafts[i].Pending = false;
            }
            bool master = RevivalTroopInsertion.MasterClient();
            if (master != _wasMaster)
            {
                _wasMaster = master;
                if (!master) ForgetAll();
                else _nextPoll = 0f;
            }
            if (!_link.Busy && _adminPending != null)
            {
                Begin(OpAdmin, null, AirText.AdminReset(++_seq, _adminPending), now);
                _adminPending = null;
            }
            if (!_link.Busy && now >= _nextPoll) Poll(now);

            if (_haveBook) TravelStep(now, master);

            if (!master || region == null || room.Length == 0) return;
            if (MapTools.LocalPlayer() == null) { _playerSince = -1f; return; }
            if (_playerSince < 0f) _playerSince = now;

            // Lease expiry cannot wait for an unrelated operation's retries.
            for (int i = 0; i < _crafts.Length; i++)
            {
                Craft c = _crafts[i];
                if (_haveBook && c.Go != null && AirPlan.Decide(_book.Get(c.Id), ServerNow(), _book.Delay,
                    _region, _room, true, c.Tok, false) == AirAction.Drop) Plan(c, now);
                Watch(c, now);
            }
            if (_haveBook && now - _playerSince >= SettleAfterJoin)
            {
                if (!_legacyTaken) TakeLegacy();
                for (int i = 0; i < _crafts.Length && !_link.Busy; i++) Plan(_crafts[i], now);
            }
        }

        static void Probe(float now)
        {
            // The clock starts in the world, not in the menus (no stats there yet).
            if (Admin.LocalStats() == null) { _firstTry = -1f; return; }
            if (_firstTry < 0f) _firstTry = now;
            if (!Connect())
            {
                if (now - _firstTry > ProbeGiveUp) GoOff("no master-server connection");
                return;
            }
            if (_pollPending && now - _pollSentAt > 3f * PollProbe)
            {
                GoOff("the master server does not answer on storage 7702");
                return;
            }
            if (now >= _nextPoll) Poll(now);
        }

        static void GoOff(string why)
        {
            _mode = Mode.Off;
            _offReason = why;
            _firstTry = Time.realtimeSinceStartup;
            _pollPending = false;
            _nextPoll = _firstTry + PollProbe;
            if (!_warned)
            {
                _warned = true;
                RevivalPlugin.L.LogWarning("AirRegistry: waiting - " + why
                    + ". Aircraft spawns remain blocked until the registry answers.");
            }
        }

        static void ForgetAll()
        {
            for (int i = 0; i < _crafts.Length; i++)
            {
                Craft c = _crafts[i];
                c.Go = null; c.View = 0; c.Tok = -1; c.Restoring = false; c.Destroying = false; c.CargoN = -1;
                c.Gate.Reset();
            }
        }

        // ================================================================ per aircraft

        /// <summary>Read the tracked aircraft and write when the gate says so.</summary>
        static void Watch(Craft c, float now)
        {
            if (c.Go == null)
            {
                if (c.View != 0)
                {
                    // Gone without burning (removed by hand, or a scene unload):
                    // the plan claims it again from the last snapshot.
                    RevivalPlugin.L.LogInfo("AirRegistry: the " + c.Id + " object " + c.View + " is gone - asking the registry again.");
                    c.View = 0; c.Tok = -1; c.Restoring = false; c.Gate.Reset();
                }
                return;
            }
            AirCraft rec = _book.Get(c.Id);
            if (rec.State == "in_transit" && rec.Phase == "arriving" && rec.Room == _room && rec.Tok == c.Tok)
            {
                if (c.Restoring && now >= c.RestoreAfter) RestoreStep(c);
                return;
            }
            // Do not renew from an old answer, a former master's token, or an
            // expired lease. Plan removes stale objects before claiming anew.
            if (!rec.Present || rec.Room != _room || rec.Tok != c.Tok || rec.Lease <= ServerNow() + AirPlan.LeaseGuard) return;
            if (c.An2 ? PlayerAn2.Burning(c.Go) : PlayerHeli.Wrecked(c.Go))
            {
                if (!c.Destroying && !_link.Busy && c.Tok >= 0)
                {
                    c.Destroying = true;
                    Vector3 p = c.Go.transform.position;
                    Begin(OpDestroy, c, AirText.Destroy(++_seq, c.Id, c.Tok, _room, p.x, p.y, p.z), now);
                    RevivalPlugin.L.LogInfo("AirRegistry: the " + c.Id + " burns - destroyed in the registry.");
                }
                return;
            }
            if (c.Restoring && now >= c.RestoreAfter) RestoreStep(c);
            Read(c, now);
            if (_link.Busy || c.Tok < 0) return;
            if (!c.Gate.Due(now, c.InAir, c.Parts, c.Fuel01, c.CargoHash, c.Wear)) return;
            int n = c.Restoring ? -1 : c.CargoN;
            Begin(OpSnap, c, AirText.Snap(++_seq, c.Id, c.Tok, _room, c.InAir, c.Pos.x, c.Pos.y, c.Pos.z, c.Yaw, c.Fuel01,
                c.Parts, AirPlan.DamageOf(c.Parts, c.An2 ? An2Repair.AllParts : HeliCondition.AllParts),
                c.Slot, c.Item, c.Count, c.Cond, c.Clip, c.Water, c.Energy, n)
                + "wear=" + c.Wear.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "\n", now);
            c.Gate.Written(now, c.InAir, c.Parts, c.Fuel01, c.CargoHash, c.Wear);
            if (c.Gate.Reason.Length > 0 && c.Gate.Reason != "lease")
                RevivalPlugin.L.LogInfo("AirRegistry: " + c.Id + " written (" + c.Gate.Reason + ").");
        }

        static void Read(Craft c, float now)
        {
            Transform t = c.Go.transform;
            c.Pos = t.position;
            c.Yaw = t.eulerAngles.y;
            if (c.An2)
            {
                c.InAir = PlayerAn2.InAir(c.Go);
                c.Parts = An2Repair.MaskOf(c.Go);
                c.Fuel01 = PlayerAn2.FuelOf(c.Go) / Mathf.Max(1f, PlayerAn2.Capacity);
                c.Wear = PlayerAn2.WearOf(c.Go);
            }
            else
            {
                c.InAir = PlayerHeli.InAir(c.Go);
                int mask; float fuel;
                HeliCondition.Read(c.Go, out mask, out fuel);
                c.Parts = mask;
                c.Fuel01 = fuel / HeliCondition.Capacity;
                c.Wear = 0f;
            }
            if (c.Restoring || now < c.NextCargo) return;
            c.NextCargo = now + CargoEvery;
            c.CargoN = c.An2 ? ReadBombs(c) : ReadHold(c);
            c.CargoHash = c.CargoN < 0 ? 0 : AirPlan.CargoHash(c.Slot, c.Item, c.Count, c.Cond, c.Clip, c.Water, c.Energy, c.CargoN);
        }

        /// <summary>The room master's decision for one aircraft (one op at most).</summary>
        static void Plan(Craft c, float now)
        {
            AirCraft rec = _book.Get(c.Id);
            if (c.Go == null)
            {
                // A previous master's object of this room: adopt it, never a second.
                GameObject g = RoomObject(c);
                if (g != null && !(c.An2 ? PlayerAn2.Burning(g) : PlayerHeli.Wrecked(g)))
                {
                    Track(c, g, -1);
                    RevivalPlugin.L.LogInfo("AirRegistry: adopted the " + c.Id + " " + c.View + " of this room (master change).");
                }
            }
            AirAction a = AirPlan.Decide(rec, ServerNow(), _book.Delay, _region, _room, c.Go != null, c.Tok, CanSpawn());
            if (a == AirAction.Drop)
            {
                RevivalPlugin.L.LogInfo("AirRegistry: the " + c.Id + " " + c.View + " is not this room's any more (registry: "
                    + rec.State + " " + rec.Region + " room " + rec.Room + ") - removed.");
                GameObject go = c.Go;
                c.Go = null; c.View = 0; c.Tok = -1; c.Restoring = false; c.Gate.Reset(); c.DropWarned = false;
                SetRoomView(c, 0);
                if (c.An2) PlayerAn2.RegistryRemove(go); else PlayerHeli.RegistryRemove(go);
                return;
            }
            if (a == AirAction.Claim)
            {
                Begin(OpClaim, c, AirText.Claim(++_seq, c.Id, _room, _region, rec.Tok), now);
                return;
            }
            if (a == AirAction.Spawn)
            {
                if (!c.Pending && !PrepareSpawn(c)) return;
                Begin(OpSpawn, c, AirText.Spawn(++_seq, c.Id, _room, _region, c.PAt.x, c.PAt.y, c.PAt.z, c.PYaw, c.PFuel01,
                    c.PParts, AirPlan.DamageOf(c.PParts, c.An2 ? An2Repair.AllParts : HeliCondition.AllParts)), now);
            }
        }

        static bool CanSpawn()
        {
            return EastWorld.On && MapScene.AtHome && AirfieldHold.SpawnAllowed();
        }

        /// <summary>The values of the new aircraft, fixed once: an aircraft of
        /// this room from before the registry (migration), else the stand.</summary>
        static bool PrepareSpawn(Craft c)
        {
            c.PAdopt = null;
            bool migrate = AirPlan.CanMigrate(_book.Get(c.Id));
            GameObject old = migrate ? LegacyOf(c) : null;
            if (old != null)
            {
                c.PAdopt = old;
                c.Go = old;           // read it like a tracked one, without a token
                Read(c, Time.realtimeSinceStartup);
                c.Go = null;
                c.PAt = c.Pos; c.PYaw = c.Yaw; c.PFuel01 = c.Fuel01; c.PParts = c.Parts;
                c.Pending = true;
                return true;
            }
            if (c.An2)
            {
                Vector3 at; float heading, litres; int mask;
                if (!PlayerAn2.RegistryStand(migrate, out at, out heading, out litres, out mask)) return false;
                c.PAt = at; c.PYaw = heading; c.PFuel01 = litres / Mathf.Max(1f, PlayerAn2.Capacity); c.PParts = mask;
            }
            else
            {
                Vector3 at = Mi8Stand, pad;
                float heading = Mi8StandHeading, padHeading, y;
                if (Helipads.Snap(at, out pad, out padHeading)) { at = pad; heading = padHeading; }
                else if (RevivalTroopInsertion.GroundY(at, out y) || RevivalTroopInsertion.TerrainHeight(at, out y)) at.y = y;
                else return false;
                int mask; float fuel;
                HeliCondition.FoundRoll(out mask, out fuel);
                c.PAt = at; c.PYaw = heading; c.PFuel01 = fuel / HeliCondition.Capacity; c.PParts = mask;
            }
            c.Pending = true;
            return true;
        }

        static void Track(Craft c, GameObject go, int tok)
        {
            c.Go = go; c.View = PlayerHeli.ViewOf(go); c.Tok = tok; c.Destroying = false; c.DropWarned = false;
            c.Gate.Reset(); c.CargoN = -1; c.NextCargo = 0f;
        }

        // ================================================================ ops

        static void Begin(int kind, Craft c, string text, float now)
        {
            _opKind = kind; _opCraft = c;
            _link.Begin(text, _seq, now);
            Transmit(text);
        }

        static void Finish(string result)
        {
            int kind = _opKind;
            Craft c = _opCraft;
            _opKind = 0; _opCraft = null;
            bool ok = result.StartsWith("ok", StringComparison.Ordinal);
            if (kind >= OpDepart) { TravelFinish(kind, c, result); return; }
            if (kind == OpAdmin) { _adminNote = result; RevivalPlugin.L.LogInfo("AirRegistry: admin op -> " + result); return; }
            if (c == null) return;
            if (kind == OpSnap)
            {
                if (!ok) { _depart = null; c.Gate.Reset(); RevivalPlugin.L.LogInfo("AirRegistry: " + c.Id + " snapshot -> " + result); }
                return;
            }
            if (kind == OpDestroy)
            {
                if (!ok) { c.Destroying = false; return; }
                if (!RevivalTroopInsertion.MasterClient() || AirPlan.SafeRoom(RoomName()) != _room) return;
                SetRoomView(c, 0);
                c.Go = null; c.View = 0; c.Tok = -1; c.Restoring = false; c.Gate.Reset();
                return;
            }
            AirCraft rec = _book.Get(c.Id);
            if (!ok || !rec.Present || rec.Room != _room)
            {
                if (kind == OpSpawn && !result.StartsWith("err:wait", StringComparison.Ordinal)) c.Pending = false;
                RevivalPlugin.L.LogInfo("AirRegistry: " + (kind == OpSpawn ? "spawn" : "claim") + " of the " + c.Id + " -> " + result);
                return;
            }
            // A late answer from a room/master we left cannot materialise a copy.
            if (!RevivalTroopInsertion.MasterClient() || AirPlan.SafeRoom(RoomName()) != _room
                || AirPlan.RegionOf(MapScene.Current) != _region || rec.Lease <= ServerNow() + AirPlan.LeaseGuard) return;
            if (kind == OpSpawn)
            {
                c.Pending = false;
                GameObject go = c.PAdopt;
                if (go != null && (c.An2 ? PlayerAn2.Burning(go) : PlayerHeli.Wrecked(go))) go = null;
                if (go != null) RevivalPlugin.L.LogInfo("AirRegistry: the " + c.Id + " already in this room is now the registry's (migration).");
                else go = Build(c, rec, false);
                c.PAdopt = null;
                if (go == null) return;      // the lease lapses; the next claim builds it
                Track(c, go, rec.Tok);
                SetRoomView(c, c.View);
                RemoveLegacy(c);
                RevivalPlugin.L.LogInfo("AirRegistry: the airfield's new " + c.Id + " (token " + rec.Tok + ").");
                return;
            }
            // Claim: adopt the room's object if a previous master built it, else build.
            GameObject own = RoomObject(c);
            bool ditched = result == "ok:ditched" || rec.State == "flying";
            GameObject built = own != null ? own : Build(c, rec, ditched);
            if (built == null) return;
            if (own != null && !c.An2) RestoreEnergy(rec, own);
            Track(c, built, rec.Tok);
            SetRoomView(c, c.View);
            if (own == null && rec.CargoCount > 0)
            {
                int n = Math.Min(rec.CargoCount, AirCraft.MaxCargo);
                Array.Copy(rec.Item, c.RItem, n); Array.Copy(rec.Count, c.RCount, n); Array.Copy(rec.Cond, c.RCond, n);
                Array.Copy(rec.Clip, c.RClip, n); Array.Copy(rec.Water, c.RWater, n); Array.Copy(rec.Energy, c.REnergy, n);
                c.RN = n; c.RAt = 0; c.RFree = -1; c.Restoring = true; c.RestoreAfter = Time.realtimeSinceStartup + 2f;
            }
            RemoveLegacy(c);
            RevivalPlugin.L.LogInfo("AirRegistry: the " + c.Id + " is back in " + _region + " at " + rec.X.ToString("0") + ", "
                + rec.Z.ToString("0") + " (fuel " + Mathf.RoundToInt(rec.Fuel * 100f) + "%, damage " + Mathf.RoundToInt(rec.Dmg * 100f)
                + "%, cargo " + rec.CargoCount + (ditched ? ", came down unflown" : "") + ", token " + rec.Tok + ").");
        }

        /// <summary>The registry's aircraft as stored. Ditched or still flying:
        /// on the ground below, with the ditch damage in its parts.</summary>
        static GameObject Build(Craft c, AirCraft rec, bool ground)
        {
            Vector3 at = new Vector3(rec.X, rec.Y, rec.Z);
            if (!c.An2 && rec.Region == "SWAMP" && !AirLanding.PlatformReady) return null;
            float afloat;
            if (c.An2 && rec.State == "parked" && AirLanding.An2Floor(at, out afloat)) at.y = afloat;
            if (!c.An2 && rec.State == "parked" && AirLanding.Deck(at, out afloat)) at.y = afloat;
            if (ground)
            {
                float y;
                if ((c.An2 && AirLanding.An2Floor(at, out y)) || (!c.An2 && AirLanding.Deck(at, out y))
                    || RevivalTroopInsertion.GroundY(at, out y) || RevivalTroopInsertion.TerrainHeight(at, out y)) at.y = y;
            }
            int all = c.An2 ? An2Repair.AllParts : HeliCondition.AllParts;
            int parts = AirPlan.PartsFor(rec.Parts < 0 ? all : rec.Parts, all, rec.Dmg);
            if (c.An2)
            {
                GameObject go = PlayerAn2.BuildRestored(at, rec.Yaw, rec.Fuel * PlayerAn2.Capacity, parts);
                PlayerAn2.RestoreWear(go, rec.Wear);
                if (go != null)
                    for (int i = 0; i < rec.CargoCount; i++)
                        if (rec.Item[i] == An2Bombs.ItemId) An2Bombs.Restore(go, rec.Count[i], Mathf.RoundToInt(rec.Cond[i]));
                return go;
            }
            GameObject heli = PlayerHeli.BuildRegistered(at, rec.Yaw);
            if (heli != null) HeliCondition.Restore(heli, parts, rec.Fuel * HeliCondition.Capacity);
            return heli;
        }

        // ================================================================ legacy (before the registry)

        static void TakeLegacy()
        {
            _legacyTaken = true;
            PlayerAn2.PlayerPlanes(_scratch);
            for (int i = 0; i < _scratch.Count; i++) _legacy.Add(PlayerHeli.ViewOf(_scratch[i]));
            PlayerHeli.Machines(_scratch);
            for (int i = 0; i < _scratch.Count; i++) _legacy.Add(PlayerHeli.ViewOf(_scratch[i]));
            for (int i = 0; i < _crafts.Length; i++)
            {
                if (_crafts[i].View != 0) _legacy.Remove(_crafts[i].View);
                GameObject registered = RoomObject(_crafts[i]);     // a previous master's registry aircraft
                if (registered != null) _legacy.Remove(PlayerHeli.ViewOf(registered));
            }
            _legacy.Remove(0);
            _scratch.Clear();
            if (_legacy.Count > 0) RevivalPlugin.L.LogInfo("AirRegistry: " + _legacy.Count + " aircraft in this room from before the registry.");
        }

        static GameObject LegacyOf(Craft c)
        {
            if (_legacy.Count == 0) return null;
            if (c.An2) PlayerAn2.PlayerPlanes(_scratch); else PlayerHeli.Machines(_scratch);
            GameObject best = null;
            for (int i = 0; i < _scratch.Count && best == null; i++)
            {
                GameObject g = _scratch[i];
                if (!_legacy.Contains(PlayerHeli.ViewOf(g))) continue;
                if (c.An2 ? PlayerAn2.Burning(g) : PlayerHeli.Wrecked(g)) continue;
                best = g;
            }
            _scratch.Clear();
            return best;
        }

        /// <summary>Remove other pre-registry aircraft once the registry's
        /// instance exists, so an older client cannot retain a copy.</summary>
        static void RemoveLegacy(Craft c)
        {
            if (_legacy.Count == 0) return;
            if (c.An2) PlayerAn2.PlayerPlanes(_scratch); else PlayerHeli.Machines(_scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                GameObject g = _scratch[i];
                int view = PlayerHeli.ViewOf(g);
                if (g == c.Go || !_legacy.Contains(view)) continue;
                _legacy.Remove(view);
                if (c.An2 ? PlayerAn2.Burning(g) : PlayerHeli.Wrecked(g)) continue;
                RevivalPlugin.L.LogInfo("AirRegistry: an older " + c.Id + " " + view + " removed - the registry's is the one.");
                if (c.An2) PlayerAn2.RegistryRemove(g); else PlayerHeli.RegistryRemove(g);
            }
            _scratch.Clear();
        }

        // ================================================================ cargo

        static Type _tContainer;
        static FieldInfo _fData, _fIds, _fBullets, _fCond, _fClip, _fWater, _fEnergy;
        static MethodInfo _mFree, _mAdd, _mSync, _mClear;
        static Func<Array, int, int> _readInt;
        static Func<Array, int, float> _readFloat;
        static bool _holdLooked;

        static bool HoldLook()
        {
            if (_holdLooked) return _readInt != null;
            _holdLooked = true;
            try
            {
                _tContainer = RevivalPlugin.TypeByName("ItemsContainer");
                Type data = RevivalPlugin.TypeByName("ContainerData");
                if (_tContainer == null || data == null) return false;
                _fData = AccessTools.Field(_tContainer, "_containerData");
                _fIds = AccessTools.Field(data, "ItemID");
                _fBullets = AccessTools.Field(data, "ItemBullets");
                _fCond = AccessTools.Field(data, "ItemCondition");
                _fClip = AccessTools.Field(data, "ClipItemID");
                _fWater = AccessTools.Field(data, "ItemWater");
                _fEnergy = AccessTools.Field(data, "ItemEnergy");
                _mFree = AccessTools.Method(_tContainer, "GetContainerFreeSlot", Type.EmptyTypes, null);
                _mAdd = AccessTools.Method(_tContainer, "AddNewContainerItemFromResources", new Type[] { typeof(int) }, null);
                _mSync = AccessTools.Method(_tContainer, "NetworkSendAllContainerData", Type.EmptyTypes, null);
                _mClear = AccessTools.Method(_tContainer, "NeedClearContainerSlot", new Type[] { typeof(int) }, null);
                if (_fData == null || _fIds == null || _fBullets == null || _fCond == null || _fClip == null
                    || _fWater == null || _fEnergy == null || _mFree == null || _mAdd == null || _mSync == null) return false;
                _readInt = (Func<Array, int, int>)AmmoDepotCargo.Reader(_fIds.FieldType, typeof(int), typeof(Func<Array, int, int>));
                _readFloat = (Func<Array, int, float>)AmmoDepotCargo.Reader(_fCond.FieldType, typeof(float), typeof(Func<Array, int, float>));
                return true;
            }
            catch (Exception ex)
            {
                _readInt = null;
                RevivalPlugin.L.LogWarning("AirRegistry: the Mi-8 hold cannot be read - cargo is not kept (" + ex.Message + ").");
                return false;
            }
        }

        /// <summary>The Mi-8 hold into the reused arrays; -1: no hold.</summary>
        static int ReadHold(Craft c)
        {
            if (!HoldLook()) return -1;
            Component hold = c.Go.GetComponent(_tContainer);
            if (hold == null) return -1;
            object data = _fData.GetValue(hold);
            if (data == null) return -1;
            Array ids = _fIds.GetValue(data) as Array, bullets = _fBullets.GetValue(data) as Array, cond = _fCond.GetValue(data) as Array;
            Array clip = _fClip.GetValue(data) as Array, water = _fWater.GetValue(data) as Array, energy = _fEnergy.GetValue(data) as Array;
            if (ids == null || bullets == null || cond == null || clip == null || water == null || energy == null) return -1;
            int n = 0, len = Math.Min(ids.Length, Math.Min(bullets.Length, cond.Length));
            for (int i = 0; i < len && n < AirCraft.MaxCargo; i++)
            {
                int id = _readInt(ids, i);
                if (id <= 0) continue;
                c.Slot[n] = i; c.Item[n] = id; c.Count[n] = _readInt(bullets, i); c.Cond[n] = _readFloat(cond, i);
                c.Clip[n] = _readInt(clip, i); c.Water[n] = _readFloat(water, i); c.Energy[n] = _readFloat(energy, i);
                n++;
            }
            return n;
        }

        /// <summary>The An-2's "cargo" is its bomb load: one line, the rack's kg as the condition.</summary>
        static int ReadBombs(Craft c)
        {
            int n = An2Bombs.CountOf(c.Go);
            if (n <= 0) return 0;
            c.Slot[0] = 0; c.Item[0] = An2Bombs.ItemId; c.Count[0] = n; c.Cond[0] = An2Bombs.RackKg(c.Go);
            c.Clip[0] = 0; c.Water[0] = c.Energy[0] = 0f;
            return 1;
        }

        /// <summary>Put the stored hold back, a few items per step, through the
        /// container's own add (it tells every client), then the count and the
        /// condition into the master's slot.</summary>
        static void RestoreStep(Craft c)
        {
            if (c.An2) { c.Restoring = false; return; }
            if (!HoldLook()) { c.RestoreAfter = Time.realtimeSinceStartup + 5f; return; }
            Component hold = c.Go.GetComponent(_tContainer);
            if (hold == null) { c.RestoreAfter = Time.realtimeSinceStartup + 1f; return; }
            try
            {
                if (c.RClear)
                {
                    if (_mClear == null) return;
                    object oldData = _fData.GetValue(hold);
                    Array oldIds = _fIds.GetValue(oldData) as Array;
                    if (oldIds == null) return;
                    int stop = Math.Min(oldIds.Length, c.RClearAt + RestorePerStep);
                    while (c.RClearAt < stop)
                    {
                        int slot = c.RClearAt;
                        if (_readInt(oldIds, slot) > 0)
                        {
                            _mClear.Invoke(hold, new object[] { slot });
                            if (_readInt(oldIds, slot) > 0) throw new InvalidOperationException("container did not clear slot " + slot);
                        }
                        c.RClearAt++;
                    }
                    if (c.RClearAt < oldIds.Length) return;
                    c.RClear = false;
                }
                for (int k = 0; k < RestorePerStep && c.RAt < c.RN; k++)
                {
                    int i = c.RAt;
                    int free = c.RFree >= 0 ? c.RFree : Convert.ToInt32(_mFree.Invoke(hold, null));
                    if (free < 0)
                    {
                        RevivalPlugin.L.LogWarning("AirRegistry: the Mi-8 hold is full - " + (c.RN - i) + " stored items not put back.");
                        c.RestoreAfter = Time.realtimeSinceStartup + 5f;
                        return; // never snapshot a partial restore over the durable cargo
                    }
                    object data = _fData.GetValue(hold);
                    Array ids = _fIds.GetValue(data) as Array;
                    c.RFree = free;
                    if (_readInt(ids, free) != c.RItem[i]) _mAdd.Invoke(hold, new object[] { c.RItem[i] });
                    if (_readInt(ids, free) != c.RItem[i]) throw new InvalidOperationException("container did not add item " + c.RItem[i]);
                    WriteSlot(_fBullets.GetValue(data) as Array, free, c.RCount[i]);
                    WriteSlot(_fCond.GetValue(data) as Array, free, c.RCond[i]);
                    WriteSlot(_fClip.GetValue(data) as Array, free, c.RClip[i]);
                    WriteSlot(_fWater.GetValue(data) as Array, free, c.RWater[i]);
                    WriteSlot(_fEnergy.GetValue(data) as Array, free, c.REnergy[i]);
                    c.RAt++; c.RFree = -1;
                }
                if (c.RAt >= c.RN) _mSync.Invoke(hold, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("AirRegistry: putting the Mi-8 cargo back failed - " + ex.Message);
                c.RestoreAfter = Time.realtimeSinceStartup + 5f;
                return;
            }
            if (c.RAt < c.RN) return;
            c.Restoring = false;
            c.NextCargo = 0f;
            // The native add has already sent default values. Broadcast the
            // final counts/conditions/water/clips through its own peer RPC.
            RevivalPlugin.L.LogInfo("AirRegistry: the Mi-8 hold has its " + c.RN + " stored items back.");
        }

        static void WriteSlot(Array a, int i, object value)
        {
            if (a == null || i < 0 || i >= a.Length) return;
            Type t = a.GetType().GetElementType();
            if (t == value.GetType()) { a.SetValue(value, i); return; }
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int m = 0; m < ms.Length; m++)
            {
                if (ms[m].Name != "op_Implicit" || ms[m].ReturnType != t) continue;
                ParameterInfo[] ps = ms[m].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == value.GetType()) { a.SetValue(ms[m].Invoke(null, new object[] { value }), i); return; }
            }
        }

        // Native NetworkAddNewContainerItem carries six fields, including
        // water/condition/clips, but omits ItemEnergy. The master-server
        // snapshot supplies that field on adoption and on peer answer polls.
        static void RestoreEnergy(AirCraft rec, GameObject go)
        {
            if (go == null || !HoldLook()) return;
            Component hold = go.GetComponent(_tContainer);
            if (hold == null) return;
            object data = _fData.GetValue(hold);
            if (data == null) return;
            Array ids = _fIds.GetValue(data) as Array, count = _fBullets.GetValue(data) as Array,
                energy = _fEnergy.GetValue(data) as Array;
            if (ids == null || count == null || energy == null) return;
            for (int i = 0; i < rec.CargoCount; i++)
            {
                int slot = rec.Slot[i];
                if (slot < ids.Length && slot < count.Length && slot < energy.Length
                    && _readInt(ids, slot) == rec.Item[i] && _readInt(count, slot) == rec.Count[i])
                    WriteSlot(energy, slot, rec.Energy[i]);
            }
        }

        // ================================================================ room

        static Type _tPhoton, _tView;
        static MethodInfo _mRoom, _mFind;

        static object Room()
        {
            if (_tPhoton == null) _tPhoton = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_tPhoton == null) return null;
            if (_mRoom == null) _mRoom = AccessTools.PropertyGetter(_tPhoton, "room");
            return _mRoom == null ? null : _mRoom.Invoke(null, null);
        }

        static string RoomName()
        {
            object room = Room();
            if (room == null) return "";
            PropertyInfo p = room.GetType().GetProperty("Name") ?? room.GetType().GetProperty("name");
            object v = p == null ? null : p.GetValue(room, null);
            return v as string ?? "";
        }

        static GameObject RoomObject(Craft c)
        {
            object room = Room();
            if (room == null) return null;
            PropertyInfo p = room.GetType().GetProperty("CustomProperties");
            IDictionary props = p == null ? null : p.GetValue(room, null) as IDictionary;
            if (props == null || !props.Contains(c.Key) || !(props[c.Key] is int)) return null;
            int view = (int)props[c.Key];
            if (view <= 0) return null;
            if (_tView == null) _tView = RevivalPlugin.TypeByName("PhotonView");
            if (_tView == null) return null;
            if (_mFind == null) _mFind = AccessTools.Method(_tView, "Find", new Type[] { typeof(int) }, null);
            Component v = _mFind == null ? null : _mFind.Invoke(null, new object[] { view }) as Component;
            if (v == null) return null;
            GameObject go = v.gameObject;
            bool ours = c.An2 ? PlayerAn2.Contains(go) : PlayerHeli.Contains(go);
            return ours ? go : null;
        }

        static void SetRoomView(Craft c, int view)
        {
            try
            {
                object room = Room();
                if (room == null) return;
                MethodInfo set = AccessTools.Method(room.GetType(), "SetCustomProperties", null, null);
                if (set == null) return;
                IDictionary data = (IDictionary)Activator.CreateInstance(set.GetParameters()[0].ParameterType);
                data[c.Key] = view;
                object[] args = set.GetParameters().Length == 3 ? new object[] { data, null, false } : new object[] { data };
                set.Invoke(room, args);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirRegistry: room property " + c.Key + " - " + ex.Message); }
        }

        static long ServerNow() { return (long)(Time.realtimeSinceStartup + _serverOffset); }

        // ================================================================ transport

        static object _backend, _connection;
        static Type _bag, _container, _request;
        static MethodInfo _sendBag, _send;

        static object Get(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p != null) return p.GetValue(obj, null);
            FieldInfo f = FastField.Find(obj.GetType(), name);
            return f == null ? null : f.GetValue(obj);
        }

        static void Set(object obj, string name, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p != null) { p.SetValue(obj, Convert.ChangeType(value, p.PropertyType), null); return; }
            FieldInfo f = FastField.Find(obj.GetType(), name);
            if (f == null) throw new MissingFieldException(name);
            f.SetValue(obj, Convert.ChangeType(value, f.FieldType));
        }

        static bool Connect()
        {
            try
            {
                Component stats = Admin.LocalStats();
                if (stats == null) return false;
                _backend = Get(stats, "_backendManager");
                _connection = Get(_backend, "_connection");
                if (_connection == null) return false;
                MethodInfo connected = AccessTools.Method(_connection.GetType(), "isConnected", Type.EmptyTypes, null);
                if (connected == null || !(bool)connected.Invoke(_connection, null)) return false;
                if (_sendBag == null)
                {
                    _sendBag = AccessTools.Method(_backend.GetType(), "MessagePlayerStorage", null, null);
                    if (_sendBag == null) return false;
                    _bag = _sendBag.GetParameters()[0].ParameterType;
                }
                if (_send == null)
                {
                    for (Type t = _connection.GetType(); t != null && _send == null; t = t.BaseType)
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (m.Name == "sendMessage" && m.GetParameters().Length == 1) { _send = m; break; }
                    if (_send == null) return false;
                    _container = _send.GetParameters()[0].ParameterType;
                    PropertyInfo p = _container.GetProperty("storageListRequest");
                    FieldInfo f = p == null ? AccessTools.Field(_container, "storageListRequest") : null;
                    _request = p != null ? p.PropertyType : f == null ? null : f.FieldType;
                }
                return _request != null;
            }
            catch { return false; }
        }

        /// <summary>The command (if any), then the request for the answer.</summary>
        static void Transmit(string text)
        {
            try
            {
                if (!Connect()) return;
                if (text != null)
                {
                    object bag = Activator.CreateInstance(_bag);
                    Set(bag, "storageId", StorageId); Set(bag, "storageDescription", text);
                    _sendBag.Invoke(_backend, new object[] { bag });
                }
                object request = Activator.CreateInstance(_request); Set(request, "storageId", StorageId);
                object container = Activator.CreateInstance(_container); Set(container, "storageListRequest", request);
                _send.Invoke(_connection, new object[] { container });
                if (!_pollPending) _pollSentAt = Time.realtimeSinceStartup;
                _pollPending = true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirRegistry send: " + ex.Message); }
        }

        static void Poll(float now)
        {
            _nextPoll = now + (_mode == Mode.Live ? PollLive : PollProbe);
            Transmit(null);
        }

        internal static void Install(Harmony harmony)
        {
            AirBridgeCargo.Install(harmony);
            Type backend = RevivalPlugin.TypeByName("BackendManager");
            MethodInfo receive = backend == null ? null : AccessTools.Method(backend, "MsgStorageListRecieve", null, null);
            if (receive == null)
            {
                RevivalPlugin.L.LogWarning("AirRegistry: BackendManager.MsgStorageListRecieve not found - no registry.");
                GoOff("the game's storage answer was not found");
                return;
            }
            HarmonyMethod prefix = new HarmonyMethod(typeof(AirRegistry).GetMethod("StoragePrefix"));
            prefix.priority = Priority.First;
            harmony.Patch(receive, prefix, null, null, null, null);
        }

        /// <summary>Prefix on BackendManager.MsgStorageListRecieve(list): the
        /// 7702 bag is ours and never reaches the stash code.</summary>
        public static bool StoragePrefix(object __instance, object __0)
        {
            IList list = __0 as IList;
            if (list == null) return true;
            try
            {
                string mine = null;
                bool plain = list.Count > 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object id = Get(list[i], "storageId");
                    if (id == null) { plain = false; continue; }
                    int sid = Convert.ToInt32(id);
                    if (sid != StorageId) { if (sid != 0 && sid != 1) plain = false; continue; }
                    mine = Get(list[i], "storageDescription") as string ?? "";
                    list.RemoveAt(i);
                    plain = false;
                }
                if (mine != null)
                {
                    OnAnswer(mine);
                    return list.Count > 0;
                }
                if (plain && _pollPending && _mode != Mode.Live && Get(__instance, "currentStorage") == null && ++_plain >= 2)
                    GoOff("the master server has no aircraft registry (it answers storage 7702 with plain bags)");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirRegistry: storage answer - " + ex.Message); }
            return true;
        }

        static void OnAnswer(string text)
        {
            if (!_book.Parse(text))
            {
                RevivalPlugin.L.LogWarning("AirRegistry: unknown answer on storage 7702.");
                return;
            }
            _haveBook = true;
            // Use the whole oldest outstanding round trip as a conservative
            // age bound. A delayed answer must not extend a local lease.
            double age = _pollPending ? Math.Max(0.0, Time.realtimeSinceStartup - _pollSentAt) : 0.0;
            _pollPending = false;
            _serverOffset = _book.Now + age - (double)Time.realtimeSinceStartup;
            if (_mode != Mode.Live)
            {
                _mode = Mode.Live;
                _nextPoll = 0f;
                RevivalPlugin.L.LogInfo("AirRegistry: live - the master server keeps the An-2 and the Mi-8.\n"
                    + _book.Describe(_book.Now));
            }
            if (_book.Seq > _seq) _seq = _book.Seq;
            AirBridge.Observe(_book, AirPlan.RegionOf(MapScene.Current), ServerNow());
            if (PlayerAn2.Aboard || PlayerHeli.Aboard || HasTransit()) _nextPoll = Math.Min(_nextPoll, Time.realtimeSinceStartup + 1f);
            if (!RevivalTroopInsertion.MasterClient())
            {
                AirCraft mi8 = _book.Get("mi8");
                if (mi8.Present && mi8.Region == AirPlan.RegionOf(MapScene.Current)
                    && mi8.Room == AirPlan.SafeRoom(RoomName())) RestoreEnergy(mi8, RoomObject(_crafts[1]));
            }
            if (_link.Busy && _link.Settles(_book.Seq))
            {
                string result = _book.Seq == _link.Seq ? _book.Result : "err:lost";
                _link.Done();
                Finish(result);
            }
        }
    }
}

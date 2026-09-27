// Next Day: Survival - Revival Toolkit
//
// THE AN-2 REPAIR LOOP (phase 2 of the flyable An-2, Revival.PlayerAn2.cs).
// With [An2Repair] Enabled (off by default) the An-2 on the H1 apron starts
// BROKEN and only flies once four stages are done - the four stages of
// docs/ai/tasks/airfield-gameplay.md section 4:
//
//   1 Controls and instruments  control cable set (2069, new) + hose (10006)
//   2 Engine                    magneto (2070, new) + storage battery (10002)
//                               + spark plugs (10004)
//   3 Propeller                 propeller assembly (2071, new, the bulky one)
//   4 Fuel                      canister (10001, the game's jerrycan, +60 l)
//                               or the pump of the D1 fuel depot
//
// A toolkit (10005, the game's) or the heavy tool kit (2064) is needed for
// stages 1-3 and is kept. Every part is fitted on its own, on foot next to the
// aeroplane, with the key (G), through the native interaction presentation the
// convoy repair uses (NativeActionProgress + ConvoyFreezeHook): the part is
// taken only when the bar is full, so an interrupted fitting never eats it.
// The panel beside the aeroplane shows every stage and its parts.
//
// The parts come from the airfield's own loot tiers (Revival.Airfield.cs,
// see Pool): the tier 3 "parts" points of H1, H2, D3 and W1 roll the magneto,
// the propeller and the engine's small parts, the salvage points the cable
// set, the tool points toolkits and hoses, the D1 fuel points canisters. The
// slots themselves do not change.
//
// AUTHORITY. The master owns the state of every An-2 - a bit mask of the six
// installed parts, the fuel in its tanks, the litres left in the D1 depot -
// the same way it owns the carrier's transform. A client that finishes a
// fitting asks the master (event +7); the master applies it, answers with the
// state (event +6) and repeats it every five seconds in PlayerAn2's own
// heartbeat, so a late joiner or a new master learns it. The spawn data of a
// new An-2 carries the mask too. The master also writes the state of the
// aeroplane - parts, fuel, depot and where it is parked - to
// BepInEx/config/ndr_an2_state.txt, and the next session's first An-2 comes
// back with it. A wreck starts over: the next one is broken again.
//
// NOT HERE: the flight model and the aeroplane itself (Revival.PlayerAn2.cs,
// untouched but for small seams), cargo and drops (later).
//
// C# 3.0 (csc from .NET 3.5). ASCII-only comments and logs; player-facing
// strings go through Loc.T (real Cyrillic), so this file is UTF-8 without BOM.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>The An-2 repair loop: parts, fuel, the state, the panel.</summary>
    internal static class An2Repair
    {
        // ------------------------------------------------------------- items

        // New ids after the parachute (2067) and the Stinger's round (2068).
        // 2065 is skipped on purpose (the old anti-tank mine id, RE 2800).
        internal const int CableId = 2069;
        internal const int MagnetoId = 2070;
        internal const int PropId = 2071;
        const int DONOR = 2030;              // the generic carryable, like 2063/2064

        // The game's own vehicle items (vehicleitems, GAMEPLAY_CONTENT_CATALOGUE).
        internal const int CanisterId = 10001;
        internal const int BatteryId = 10002;
        internal const int PlugsId = 10004;
        internal const int ToolkitId = 10005;
        internal const int HoseId = 10006;

        /// <summary>The six parts in bit order: their item and their stage.</summary>
        internal static readonly int[] PartItem = new int[] { CableId, HoseId, MagnetoId, BatteryId, PlugsId, PropId };
        internal static readonly int[] PartStage = new int[] { 0, 0, 1, 1, 1, 2 };
        internal const int AllParts = 63;
        const int PropBit = 5;

        /// <summary>D1c, the pump house of the fuel depot (east_airfield.json).</summary>
        internal static readonly Vector3 Depot = new Vector3(4140f, 0f, 515f);

        // ------------------------------------------------------------ config

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgKey;
        internal static ConfigEntry<float> CfgFitSeconds;
        internal static ConfigEntry<float> CfgFuelSeconds;
        internal static ConfigEntry<float> CfgCanisterLitres;
        internal static ConfigEntry<float> CfgMinFuel;
        internal static ConfigEntry<float> CfgDepotFill;
        internal static ConfigEntry<float> CfgDepotLitres;
        internal static ConfigEntry<float> CfgDepotRefill;
        internal static ConfigEntry<float> CfgDepotRange;
        internal static ConfigEntry<bool> CfgSave;

        internal static bool Enabled { get { return CfgEnabled != null && CfgEnabled.Value; } }

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "An2Repair";
            CfgEnabled = cfg.Bind(S, "Enabled", false,
                "Die An-2 auf dem H1-Vorfeld steht kaputt da und muss in vier Stufen "
                + "repariert werden (Steuerung/Instrumente, Motor, Propeller, Treibstoff). "
                + "Braucht [PlayerAn2] Enabled. Aus: die An-2 fliegt sofort wie bisher.");
            CfgKey = cfg.Bind(S, "Key", "G",
                "Taste zu Fuss neben der An-2: das naechste getragene Teil einbauen bzw. tanken.");
            CfgFitSeconds = cfg.Bind(S, "FitSeconds", 8f, "Dauer des Einbaus eines Teils in Sekunden.");
            CfgFuelSeconds = cfg.Bind(S, "FuelSeconds", 6f, "Dauer eines Tankvorgangs (Kanister oder Pumpe).");
            CfgCanisterLitres = cfg.Bind(S, "CanisterLitres", 60f,
                "Liter, die ein Kanister (10001) in die Tanks bringt. Der Kanister wird verbraucht.");
            CfgMinFuel = cfg.Bind(S, "MinFuel", 10f,
                "So viele Liter muessen im Tank sein, damit Stufe 4 erfuellt ist und der Motor startet.");
            CfgDepotFill = cfg.Bind(S, "DepotFill", 150f, "Liter pro Pumpvorgang am Tanklager D1.");
            CfgDepotLitres = cfg.Bind(S, "DepotLitres", 1200f,
                "Vorrat des Tanklagers D1 in Litern (voll bei Sitzungsbeginn).");
            CfgDepotRefill = cfg.Bind(S, "DepotRefillMinutes", 120f,
                "In so vielen Minuten fuellt sich der leere Vorrat wieder ganz auf.");
            CfgDepotRange = cfg.Bind(S, "DepotRange", 70f,
                "Meter vom Pumpenhaus D1c, in denen die abgestellte An-2 betankt werden kann.");
            CfgSave = cfg.Bind(S, "Save", true,
                "Der Host speichert Teile, Treibstoff, Depotvorrat und Standplatz in "
                + "BepInEx/config/ndr_an2_state.txt und stellt sie in der naechsten Sitzung wieder her.");
        }

        static float F(ConfigEntry<float> e, float fallback) { return e == null ? fallback : e.Value; }

        /// <summary>The body freeze while fitting is the convoy repair's
        /// (ConvoyFreezeHook), installed once whichever repair asks first.</summary>
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            if (!Enabled) return;
            try
            {
                ConvoyFreezeHook.Install(harmony);
                RevivalPlugin.L.LogInfo("An2Repair: on - the An-2 starts broken (parts "
                    + CableId + "/" + MagnetoId + "/" + PropId + ", key " + (CfgKey == null ? "G" : CfgKey.Value) + ").");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("An2Repair install: " + ex); }
        }
        static float K { get { return PlayerAn2.K; } }

        // ------------------------------------------------------------- items

        internal static void AddItems(List<ItemDef> items)
        {
            items.Add(new ItemDef(
                CableId, DONOR, false,
                "Тросы управления Ан-2", "An-2 control cable set",
                "Катушка стального троса с тандерами - новая проводка рулей и "
                + "элеронов для Ан-2 на аэродроме. Поставьте её у самолёта "
                + "(нужен набор инструментов).",
                "A reel of steel control cable with turnbuckles - new rigging for the "
                + "An-2's rudder, elevator and ailerons at the airfield. Fit it at the "
                + "aeroplane (a toolkit is needed).",
                "an2part_cable.ndmesh", "an2part_cable_diffuse.png", "an2part_cable_normal.png",
                "an2part_cable_icon.png", null,
                1, 0, 3.0f));
            items.Add(new ItemDef(
                MagnetoId, DONOR, false,
                "Магнето АШ-62", "ASh-62 magneto",
                "Магнето девятицилиндрового звездообразного мотора Ан-2. Без него "
                + "мотор не заведётся. Поставьте у самолёта вместе с аккумулятором "
                + "и свечами.",
                "The ignition magneto of the An-2's nine-cylinder radial. The engine "
                + "does not start without it. Fit it at the aeroplane together with "
                + "a battery and spark plugs.",
                "an2part_magneto.ndmesh", "an2part_magneto_diffuse.png", "an2part_magneto_normal.png",
                "an2part_magneto_icon.png", null,
                1, 0, 6.0f));
            items.Add(new ItemDef(
                PropId, DONOR, false,
                "Втулка винта Ан-2", "An-2 propeller assembly",
                "Втулка четырёхлопастного винта с комлями лопастей. Тяжёлая и "
                + "громоздкая - вдвоём или на машине нести проще.",
                "The hub of the four-blade propeller with its blade roots. Heavy and "
                + "bulky - easier with a friend or a vehicle.",
                "an2part_prop.ndmesh", "an2part_prop_diffuse.png", "an2part_prop_normal.png",
                "an2part_prop_icon.png", null,
                1, 0, 18.0f));
        }

        /// <summary>The airfield's loot pools with the repair items in them
        /// (Airfield.Draw). Unchanged with the repair off. The tier 3 "parts"
        /// points (H1 cage, H2, D3, W1) roll the two signature parts twice as
        /// often as the engine's small parts and keep one draw of the game's
        /// vehicle items; the salvage points (H1, H2, W1, shelters) add the
        /// cable set, the tool points the toolkit, D1 more canisters.</summary>
        internal static string[] Pool(string pool, string[] entries)
        {
            if (!Enabled || entries == null) return entries;
            if (pool == "parts")
                return new string[] { "2070", "2071", "2070", "2071", "10002", "10004", "VehicleItems" };
            string[] extra = null;
            if (pool == "salvage")
                extra = new string[] { "2069" };
            else if (pool == "fuel")
                extra = new string[] { "10001", "10001" };
            else if (pool == "tools")
                extra = new string[] { "10005", "10006" };
            if (extra == null) return entries;
            string key = pool + "|" + entries.Length;
            string[] cached;
            if (_pools.TryGetValue(key, out cached)) return cached;
            string[] all = new string[entries.Length + extra.Length];
            entries.CopyTo(all, 0);
            extra.CopyTo(all, entries.Length);
            _pools[key] = all;
            return all;
        }

        static readonly Dictionary<string, string[]> _pools = new Dictionary<string, string[]>();

        // ------------------------------------------------------------- state

        // Mask of installed parts per aeroplane; absent = not tracked (spawned
        // without the repair, or by a host that has it off): airworthy.
        static readonly Dictionary<GameObject, int> _mask = new Dictionary<GameObject, int>();
        static float _depot = -1f;                // litres in D1, master's truth
        static float _depotAt;

        internal static int MaskOf(GameObject go)
        {
            int m;
            return go != null && _mask.TryGetValue(go, out m) ? m : -1;
        }

        internal static bool Airworthy(GameObject go)
        {
            int m = MaskOf(go);
            return m < 0 || (m & AllParts) == AllParts;
        }

        /// <summary>PlayerAn2.SetEngine asks before the engine starts.</summary>
        internal static bool CanStart(GameObject go, float fuel, out string why)
        {
            why = null;
            int m = MaskOf(go);
            if (m < 0) return true;
            if ((m & AllParts) != AllParts)
            {
                why = Loc.T("Самолёт не готов: ", "Not airworthy: ") + Missing(m);
                return false;
            }
            if (fuel < F(CfgMinFuel, 10f))
            {
                why = Loc.T("Слишком мало топлива - заправьте", "Too little fuel - refuel first");
                return false;
            }
            return true;
        }

        /// <summary>The fuel and mask a new An-2 is built with. The first one
        /// of a session gets the saved state (once); every later one, and the
        /// first without a save, is broken and dry.</summary>
        internal static void NewPlane(ref float fuel, out int mask)
        {
            mask = -1;
            if (!Enabled) return;
            Load();
            if (_saved && !_restored)
            {
                _restored = true;
                mask = _savedMask & AllParts;
                fuel = Mathf.Clamp(_savedFuel, 0f, Capacity);
                return;
            }
            _restored = true;
            mask = 0;
            fuel = 0f;
        }

        /// <summary>Where the first An-2 of a session stands: the saved
        /// parking place, if there is one.</summary>
        internal static bool SavedPlace(out Vector3 at, out float heading)
        {
            at = Vector3.zero;
            heading = 0f;
            if (!Enabled) return false;
            Load();
            if (!_saved || _restored || !_savedHasPos) return false;
            at = _savedPos;
            heading = _savedHeading;
            return true;
        }

        /// <summary>Every client, from PlayerAn2.Prepare: the mask out of the
        /// spawn data (index 2).</summary>
        internal static void Prepared(GameObject go, object[] data)
        {
            if (go == null) return;
            if (data != null && data.Length >= 3 && data[2] is int)
            {
                _mask[go] = (int)data[2];
                ShowProp(go);
            }
        }

        /// <summary>Every client, from PlayerAn2.Burn: a wreck is done with.
        /// The master forgets the saved aeroplane; the next one is broken.</summary>
        internal static void Wrecked(GameObject go)
        {
            if (go == null) return;
            bool tracked = _mask.ContainsKey(go);
            _mask.Remove(go);
            if (tracked && Enabled && RevivalTroopInsertion.MasterClient())
            {
                _savedHasPos = false;
                Save(null);
                RevivalPlugin.L.LogInfo("An2Repair: the An-2 is a wreck - the next one starts broken.");
            }
        }

        static float Capacity { get { return Mathf.Max(1f, PlayerAn2.Capacity); } }

        // ---------------------------------------------------------- network

        internal const int NetState = 6;          // master -> all: view, mask, fuel, depot
        internal const int NetRequest = 7;        // any -> master: view, what, litres
        const int FuelCan = 10, FuelDepot = 11;

        /// <summary>From PlayerAn2's 5 s heartbeat (master) and after every
        /// change: the state of one aeroplane to everybody.</summary>
        internal static void Broadcast(GameObject go, int view)
        {
            int m = MaskOf(go);
            if (m < 0 || view == 0) return;
            PlayerAn2.Net.Send(NetState, new float[] { view, m, PlayerAn2.FuelOf(go), Reserve() }, true);
        }

        internal static void OnNet(int kind, float[] f, int sender)
        {
            if (f == null || f.Length < 3) return;
            GameObject go = PlayerAn2.ByViewId((int)f[0]);
            if (kind == NetState)
            {
                // A client that becomes master refills from here, not from
                // the start of its session.
                if (f.Length >= 4) { _depot = f[3]; _depotAt = Time.time; }
                if (go == null) return;
                _mask[go] = (int)f[1];
                if (!PlayerAn2.Flown(go)) PlayerAn2.SetFuel(go, f[2]);
                ShowProp(go);
                return;
            }
            if (kind == NetRequest && RevivalTroopInsertion.MasterClient() && go != null)
                Apply(go, (int)f[1], f[2], sender);
        }

        /// <summary>The master applies a finished fitting or refuelling.</summary>
        static void Apply(GameObject go, int what, float litres, int sender)
        {
            if (go == null || PlayerAn2.Burning(go)) return;
            int m = MaskOf(go);
            if (m < 0) return;
            string did;
            if (what >= 0 && what < PartItem.Length)
            {
                m |= 1 << what;
                _mask[go] = m;
                did = "part " + what + " (item " + PartItem[what] + ")";
            }
            else if (what == FuelCan || what == FuelDepot)
            {
                float fuel = PlayerAn2.FuelOf(go);
                float add = Mathf.Clamp(litres, 0f, Capacity - fuel);
                if (what == FuelDepot) add = Mathf.Min(add, Reserve());
                if (add <= 0f) return;
                // With the POL depot of Revival.Fuel.cs the pump draws from its pool.
                if (what == FuelDepot && global::NextDayRevival.FuelDepot.Active) add = global::NextDayRevival.FuelDepot.Draw(add);
                else if (what == FuelDepot) _depot = Reserve() - add;
                if (add <= 0f) return;
                PlayerAn2.SetFuel(go, fuel + add);
                did = Mathf.RoundToInt(add) + " l from " + (what == FuelDepot ? "the D1 depot" : "a canister");
            }
            else return;
            ShowProp(go);
            RevivalPlugin.L.LogInfo("An2Repair: " + did + " on An-2 " + PlayerAn2.View(go)
                + " for player " + sender + " - parts " + Count(m) + "/6, fuel "
                + Mathf.RoundToInt(PlayerAn2.FuelOf(go)) + " l.");
            Broadcast(go, PlayerAn2.View(go));
            Save(go);
        }

        static void Request(GameObject go, int what, float litres)
        {
            int view = PlayerAn2.View(go);
            if (RevivalTroopInsertion.MasterClient()) { Apply(go, what, litres, -1); return; }
            if (view == 0) return;
            PlayerAn2.Net.Send(NetRequest, new float[] { view, what, litres }, true);
        }

        /// <summary>The depot's litres now; it fills up again over
        /// DepotRefillMinutes (master-side, lazily).</summary>
        static float Reserve()
        {
            if (global::NextDayRevival.FuelDepot.Active) return global::NextDayRevival.FuelDepot.Pool;   // the airfield POL depot (Revival.Fuel.cs)
            float full = Mathf.Max(0f, F(CfgDepotLitres, 1200f));
            if (_depot < 0f) { _depot = full; _depotAt = Time.time; }
            if (RevivalTroopInsertion.MasterClient())
            {
                float minutes = Mathf.Max(1f, F(CfgDepotRefill, 120f));
                _depot = Mathf.Min(full, _depot + full * (Time.time - _depotAt) / (minutes * 60f));
                _depotAt = Time.time;
            }
            return _depot;
        }

        /// <summary>Master only, from Revival.An2Bombs.cs: a bomb that bursts
        /// within <paramref name="reach"/> world units of the D1 pump house
        /// sets the depot's fuel off. The reserve goes to zero (it refills
        /// over DepotRefillMinutes as ever) and the caller shows a second
        /// blast. False when the repair loop is off or the depot is dry.</summary>
        internal static bool DepotHit(Vector3 point, float reach)
        {
            if (!Enabled || !RevivalTroopInsertion.MasterClient()) return false;
            Vector3 d = point - Depot;
            d.y = 0f;
            if (d.magnitude > reach) return false;
            if (Reserve() < 1f) return false;
            RevivalPlugin.L.LogInfo("An2Repair: a bomb set the D1 depot off - "
                + Mathf.RoundToInt(_depot) + " l burnt.");
            _depot = 0f;
            _depotAt = Time.time;
            return true;
        }

        // ------------------------------------------------------------ frame

        enum Job { None, Fit, Canister, Pump }

        const string ProgressOwner = "an2-repair";
        static Job _job = Job.None;
        static int _jobPart;
        static GameObject _jobPlane;
        static float _jobStart, _jobLen;
        static GameObject _near;
        static string _prompt;
        static float _nextVisual, _nextSave;

        /// <summary>The body freeze of ConvoyFreezeHook holds while this is
        /// true, as for the convoy repair.</summary>
        internal static bool Busy { get { return _job != Job.None; } }

        internal static void Tick()
        {
            if (!Enabled || !PlayerAn2.Enabled)
            {
                if (_job != Job.None) Cancel(null);
                _near = null;
                _prompt = null;
                return;
            }
            try
            {
                Sweep();
                if (RevivalTroopInsertion.MasterClient() && Time.time > _nextSave)
                {
                    _nextSave = Time.time + 20f;
                    GameObject tracked = Tracked();
                    if (tracked != null) Save(tracked);
                }
                if (_job != Job.None) { TickJob(); return; }
                TickIdle();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("An2Repair.Tick: " + ex);
                Cancel(null);
            }
        }

        static void Sweep()
        {
            if (Time.time < _nextVisual) return;
            _nextVisual = Time.time + 1f;
            List<GameObject> gone = null;
            foreach (KeyValuePair<GameObject, int> e in _mask)
            {
                if (e.Key != null) { ShowProp(e.Key); continue; }
                if (gone == null) gone = new List<GameObject>();
                gone.Add(e.Key);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) _mask.Remove(gone[i]);
        }

        /// <summary>A broken An-2 has no propeller on its shaft.</summary>
        static void ShowProp(GameObject go)
        {
            if (go == null) return;
            An2Visual vis = go.GetComponent<An2Visual>();
            if (vis == null) return;
            int m = MaskOf(go);
            vis.ShowProp(m < 0 || (m & (1 << PropBit)) != 0);
        }

        static void TickIdle()
        {
            _prompt = null;
            _near = null;
            if (PlayerAn2.Aboard || PlayerHeli.Aboard) return;
            GameObject go = PlayerAn2.NearestPlane();
            if (go == null || MaskOf(go) < 0) return;
            if (ConvoyRepair.InVehicle()) return;
            _near = go;

            int m = MaskOf(go);
            string key = PlayerAn2.KeyOf(CfgKey, KeyCode.G).ToString();
            bool pressed = Input.GetKeyDown(PlayerAn2.KeyOf(CfgKey, KeyCode.G));
            bool tool = Turret.HasItem(ToolkitId) || Turret.HasItem(ConvoyRepair.DEF_TOOLKIT);

            int part = -1;
            for (int i = 0; i < PartItem.Length && part < 0; i++)
                if ((m & (1 << i)) == 0 && Turret.HasItem(PartItem[i])) part = i;
            if (part >= 0)
            {
                if (!tool) { _prompt = Loc.T("Нужен набор инструментов", "A toolkit is needed"); return; }
                _prompt = "[" + key + "] " + Loc.T("Установить: ", "Fit: ") + PartName(part);
                if (pressed) Begin(Job.Fit, part, go);
                return;
            }

            float fuel = PlayerAn2.FuelOf(go);
            bool room = fuel < Capacity - 1f;
            if (room && Turret.HasItem(CanisterId))
            {
                if (PlayerAn2.Occupied(go)) { _prompt = Loc.T("Кто-то в кабине", "Somebody is in the cockpit"); return; }
                _prompt = "[" + key + "] " + Loc.T("Залить канистру", "Pour in a canister")
                    + " (+" + Mathf.RoundToInt(Mathf.Min(F(CfgCanisterLitres, 60f), Capacity - fuel)) + " l)";
                if (pressed) Begin(Job.Canister, -1, go);
                return;
            }
            if (room && AtDepot(go))
            {
                if (Reserve() < 1f) { _prompt = Loc.T("Склад ГСМ пуст", "The fuel depot is dry"); return; }
                if (PlayerAn2.Occupied(go)) { _prompt = Loc.T("Кто-то в кабине", "Somebody is in the cockpit"); return; }
                _prompt = "[" + key + "] " + Loc.T("Заправить со склада ГСМ", "Pump fuel from the depot");
                if (pressed) Begin(Job.Pump, -1, go);
                return;
            }
            if ((m & AllParts) != AllParts)
                _prompt = Loc.T("Нужно: ", "Needed: ") + Missing(m);
            else if (fuel < F(CfgMinFuel, 10f))
                _prompt = Loc.T("Нужно топливо: канистра или склад ГСМ D1", "Needs fuel: a canister or the D1 depot");
        }

        static bool AtDepot(GameObject go)
        {
            Vector3 d = go.transform.position - Depot;
            d.y = 0f;
            return d.magnitude < Mathf.Max(5f, F(CfgDepotRange, 70f)) * K;
        }

        static void Begin(Job job, int part, GameObject go)
        {
            float seconds = job == Job.Fit ? Mathf.Max(1f, F(CfgFitSeconds, 8f))
                                           : Mathf.Max(1f, F(CfgFuelSeconds, 6f));
            string label = job == Job.Fit ? Loc.T("Ремонт Ан-2", "Repairing the An-2")
                                          : Loc.T("Заправка Ан-2", "Refuelling the An-2");
            if (!NativeActionProgress.Begin(ProgressOwner, label, seconds, true, "repair", "vehicle_repair_01"))
                return;
            _job = job;
            _jobPart = part;
            _jobPlane = go;
            _jobStart = Time.time;
            _jobLen = seconds;
            _prompt = null;
        }

        static void Cancel(string why)
        {
            if (!string.IsNullOrEmpty(why))
            {
                Turret.Hinweis(why, 2f);
                RevivalPlugin.L.LogInfo("An2Repair: action cancelled - " + why + ".");
            }
            NativeActionProgress.End(ProgressOwner);
            _job = Job.None;
            _jobPlane = null;
        }

        static void TickJob()
        {
            GameObject go = _jobPlane;
            if (!NativeActionProgress.IsActive(ProgressOwner)) { Cancel(Loc.T("Прервано", "Interrupted")); return; }
            if (go == null || PlayerAn2.Burning(go) || !ReferenceEquals(PlayerAn2.NearestPlane(), go))
            { Cancel(Loc.T("Самолёт вне досягаемости", "The aeroplane is out of reach")); return; }
            if (_job != Job.Fit && PlayerAn2.Occupied(go))
            { Cancel(Loc.T("Кто-то в кабине", "Somebody is in the cockpit")); return; }
            if (Time.time - _jobStart < _jobLen) return;

            Job job = _job;
            int part = _jobPart;
            NativeActionProgress.End(ProgressOwner);
            _job = Job.None;
            _jobPlane = null;

            if (job == Job.Fit)
            {
                // Installed by somebody else meanwhile: keep the part.
                if ((MaskOf(go) & (1 << part)) != 0) { Turret.Hinweis(Loc.T("Уже установлено", "Already fitted"), 2f); return; }
                if (!Turret.TakeItem(PartItem[part], "An2Repair")) { Turret.Hinweis(Loc.T("Детали нет", "The part is gone"), 2f); return; }
                Request(go, part, 0f);
                Turret.Hinweis(Loc.T("Установлено: ", "Fitted: ") + PartName(part), 3f);
            }
            else if (job == Job.Canister)
            {
                if (!Turret.TakeItem(CanisterId, "An2Repair")) { Turret.Hinweis(Loc.T("Канистры нет", "The canister is gone"), 2f); return; }
                Request(go, FuelCan, F(CfgCanisterLitres, 60f));
                Turret.Hinweis(Loc.T("Канистра залита", "Canister poured in"), 3f);
            }
            else
            {
                Request(go, FuelDepot, Mathf.Max(1f, F(CfgDepotFill, 150f)));
                Turret.Hinweis(Loc.T("Заправлено со склада", "Fuel pumped from the depot"), 3f);
            }
        }

        // ------------------------------------------------------------- texts

        internal static string PartName(int bit)
        {
            switch (bit)
            {
                case 0: return Loc.T("тросы управления", "control cables");
                case 1: return Loc.T("шланг (приборы)", "hose (instruments)");
                case 2: return Loc.T("магнето", "magneto");
                case 3: return Loc.T("аккумулятор", "storage battery");
                case 4: return Loc.T("свечи", "spark plugs");
                default: return Loc.T("втулка винта", "propeller assembly");
            }
        }

        static string StageName(int stage)
        {
            switch (stage)
            {
                case 0: return Loc.T("1 Управление и приборы", "1 Controls and instruments");
                case 1: return Loc.T("2 Двигатель", "2 Engine");
                case 2: return Loc.T("3 Винт", "3 Propeller");
                default: return Loc.T("4 Топливо", "4 Fuel");
            }
        }

        static string Missing(int m)
        {
            string s = "";
            for (int i = 0; i < PartItem.Length; i++)
            {
                if ((m & (1 << i)) != 0) continue;
                s += (s.Length > 0 ? ", " : "") + PartName(i);
            }
            return s;
        }

        static int Count(int m)
        {
            int n = 0;
            for (int i = 0; i < PartItem.Length; i++) if ((m & (1 << i)) != 0) n++;
            return n;
        }

        // --------------------------------------------------------------- HUD

        internal static void Draw()
        {
            if (!Enabled || !PlayerAn2.Enabled) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            try
            {
                GameObject go = _job != Job.None ? _jobPlane : _near;
                if (go == null) return;
                Panel(go);
                if (_job == Job.None && !string.IsNullOrEmpty(_prompt) && Hints.Prompts) Prompt(_prompt);   // NDR P9
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("An2Repair.Draw: " + ex); }
        }

        /// <summary>The four stages beside the aeroplane: a tick per part,
        /// the stage's count, the fuel against its minimum.</summary>
        static void Panel(GameObject go)
        {
            int m = MaskOf(go);
            if (m < 0) return;
            float fuel = PlayerAn2.FuelOf(go);
            float min = F(CfgMinFuel, 10f);
            bool ready = (m & AllParts) == AllParts && fuel >= min;

            const float w = 330f;
            float x = Screen.width - w - 24f;
            float y = Screen.height * 0.30f;
            float h = 26f + 4 * 38f + 30f;
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 8f, y - 6f, w + 16f, h), Texture2D.whiteTexture);
            GUI.color = keep;

            Label(x, y, ready ? Loc.T("Ан-2 - готов к полёту", "An-2 - airworthy")
                              : Loc.T("Ан-2 - неисправен", "An-2 - not airworthy"),
                  ready ? new Color(0.55f, 0.9f, 0.5f, 1f) : new Color(1f, 0.7f, 0.4f, 1f), 15);
            y += 26f;
            for (int stage = 0; stage < 4; stage++)
            {
                int have = 0, need = 0;
                string parts = "";
                if (stage < 3)
                {
                    for (int i = 0; i < PartItem.Length; i++)
                    {
                        if (PartStage[i] != stage) continue;
                        need++;
                        bool on = (m & (1 << i)) != 0;
                        if (on) have++;
                        parts += (parts.Length > 0 ? "  " : "") + (on ? "[x] " : "[ ] ") + PartName(i);
                    }
                }
                else
                {
                    need = 1;
                    have = fuel >= min ? 1 : 0;
                    parts = Mathf.RoundToInt(fuel) + " / " + Mathf.RoundToInt(Capacity) + " l"
                        + (AtDepot(go) ? Loc.T("  - склад ГСМ рядом (", "  - depot in reach (")
                                         + Mathf.RoundToInt(global::NextDayRevival.FuelDepot.Active ? global::NextDayRevival.FuelDepot.Pool : Mathf.Max(0f, _depot < 0f ? F(CfgDepotLitres, 1200f) : _depot)) + " l)" : "");
                }
                bool done = have >= need;
                Label(x, y, StageName(stage) + "   " + have + "/" + need,
                      done ? new Color(0.55f, 0.9f, 0.5f, 1f) : new Color(0.92f, 0.92f, 0.86f, 1f), 13);
                Label(x + 14f, y + 17f, parts, new Color(0.78f, 0.80f, 0.78f, 1f), 11);
                y += 38f;
            }
            if ((m & AllParts) != AllParts)
                Label(x, y + 2f, Loc.T("Ст. 1-3: нужен набор инструментов", "Stages 1-3 need a toolkit (kept)"),
                      new Color(0.75f, 0.78f, 0.85f, 1f), 11);
        }

        static void Label(float x, float y, string text, Color colour, int size)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.normal.textColor = colour;
            GUI.Label(new Rect(x, y, 360f, 24f), text, style);
        }

        static void Prompt(string text)
        {
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
            float w = size.x + 24f, h = Mathf.Max(24f, size.y + 8f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.62f;
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = keep;
            GUI.Label(new Rect(x + 12f, y + (h - size.y) * 0.5f, size.x + 4f, size.y), text);
        }

        // ------------------------------------------------------ persistence

        const string FileName = "ndr_an2_state.txt";
        static bool _loaded, _saved, _restored, _savedHasPos;
        static int _savedMask;
        static float _savedFuel, _savedHeading;
        static Vector3 _savedPos;
        static string _lastWritten;

        static string StatePath()
        {
            return Path.Combine(BepInEx.Paths.ConfigPath, FileName);
        }

        /// <summary>The aeroplane whose state is saved: the first tracked one.</summary>
        static GameObject Tracked()
        {
            foreach (KeyValuePair<GameObject, int> e in _mask)
                if (e.Key != null && !PlayerAn2.Burning(e.Key)) return e.Key;
            return null;
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            if (CfgSave != null && !CfgSave.Value) return;
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                bool plane = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] p = lines[i].Trim().Split(' ');
                    if (p.Length < 2 || p[0].StartsWith("#")) continue;
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    if (p[0] == "plane") plane = p[1] == "1";
                    else if (p[0] == "parts") _savedMask = int.Parse(p[1], ci);
                    else if (p[0] == "fuel") _savedFuel = float.Parse(p[1], ci);
                    else if (p[0] == "depot") _depot = float.Parse(p[1], ci);
                    else if (p[0] == "heading") _savedHeading = float.Parse(p[1], ci);
                    else if (p[0] == "pos" && p.Length >= 4)
                    {
                        _savedPos = new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci));
                        _savedHasPos = true;
                    }
                }
                _depotAt = Time.time;
                _saved = plane;
                if (!plane) _savedHasPos = false;
                RevivalPlugin.L.LogInfo("An2Repair: saved state read - "
                    + (plane ? "parts " + Count(_savedMask) + "/6, fuel " + Mathf.RoundToInt(_savedFuel) + " l"
                             + (_savedHasPos ? ", parked at " + _savedPos.ToString("0") : "")
                             : "no An-2 (the last one was wrecked)")
                    + (_depot >= 0f ? ", depot " + Mathf.RoundToInt(_depot) + " l." : "."));
            }
            catch (Exception ex)
            {
                _saved = false;
                _savedHasPos = false;
                RevivalPlugin.L.LogWarning("An2Repair: " + FileName + " unreadable, starting fresh - " + ex.Message);
            }
        }

        /// <summary>Master only. `go` null = no aeroplane (wrecked). The
        /// parking place is only written while nobody is aboard and it is not
        /// gliding down or burning - that is, while it stands.</summary>
        static void Save(GameObject go)
        {
            if (CfgSave != null && !CfgSave.Value) return;
            if (!RevivalTroopInsertion.MasterClient()) return;
            if (!EastWorld.On) return;
            try
            {
                CultureInfo ci = CultureInfo.InvariantCulture;
                List<string> lines = new List<string>();
                lines.Add("# ndr_an2_state.txt - the An-2's repair state, written by the host ([An2Repair] Save).");
                lines.Add("plane " + (go != null ? "1" : "0"));
                lines.Add("depot " + Reserve().ToString("0", ci));
                if (go != null)
                {
                    lines.Add("parts " + (MaskOf(go) & AllParts).ToString(ci));
                    lines.Add("fuel " + PlayerAn2.FuelOf(go).ToString("0.0", ci));
                    if (PlayerAn2.Parked(go))
                    {
                        _savedPos = go.transform.position;
                        Vector3 f = go.transform.forward;
                        _savedHeading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                        _savedHasPos = true;
                    }
                    if (_savedHasPos)
                    {
                        lines.Add("pos " + _savedPos.x.ToString("0.00", ci) + " " + _savedPos.y.ToString("0.00", ci)
                            + " " + _savedPos.z.ToString("0.00", ci));
                        lines.Add("heading " + _savedHeading.ToString("0.0", ci));
                    }
                }
                string text = string.Join("\n", lines.ToArray()) + "\n";
                // The depot line creeps with the refill; only write real changes.
                if (text == _lastWritten) return;
                _lastWritten = text;
                File.WriteAllText(StatePath(), text);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("An2Repair: saving the state failed - " + ex.Message);
            }
        }
    }
}

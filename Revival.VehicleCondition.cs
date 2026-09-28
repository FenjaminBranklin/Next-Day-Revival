// Next Day: Survival - Revival Toolkit
//
// THE NEW VEHICLES ON THE VANILLA CONDITION SYSTEM (task N8,
// docs/ai/tasks/n08-vehicles-vanilla.md).
//
// WHAT THE GAME DOES (IL, 2026-09-28):
//   VehicleSpawnPoint::InstantiateCar
//       PhotonNetwork.InstantiateSceneObject("VehicleSpawn\<name>", ...)
//       VehicleInventoryManager.SetPartSpawn(10002 battery,     point.Battery)
//       VehicleInventoryManager.SetPartSpawn(10004 spark plugs, point.Spark)
//       VehicleInventoryManager.SetPartSpawn(10003 key,         point.Keys)
//   SetPartSpawn(id, mode), owner only: mode 0 fits the part, mode 3 fits it
//   with 50 percent, anything else leaves the slot empty. Fitting is
//   VehicleGameSystem.LocalSetVehicleComponent(id, bool) - the RPC
//   "SetVehicleComponent" to everybody, and NetworkSendVehicleDataToNewClient
//   repeats every slot to a late joiner.
//   The world's own points (level7, the main map) are all 3/3/3 with a custom
//   fuel of 20..250 and full durability; the start-area points are 0/0/0.
//   The engine box has three slots: [10002], [10004], [10003 or 1306].
//   KillOrStartEngine: no key or no battery -> "$HUD_Msg_VehicleComponentsNotFound";
//   CheckAdditionalPartsReady: fuel > 0 and spark plugs.
//   Repair in the game is exactly this: drag the part into the engine box
//   (ItemSlotUI::SlotEventManager -> LocalSetVehicleComponent). There is no
//   durability repair in the game; a car at 0 durability respawns only when it
//   came from a VehicleSpawnPoint (MySpawnPoint) - one without keeps its wreck.
//
// WHAT IS OURS:
//   * Every ground vehicle of the mod (T-72, BTR-80A/MTW, Gepard, technical,
//     Ural, drivable howitzer) is the game's VehicleGameSystem with the game's
//     engine box, so it already takes vanilla parts and canisters. What it
//     never had is a vanilla START: CarSpawn.Prepare fits all three parts and
//     fills the tank. Found() gives it the world's own 3/3/3 roll and a
//     world-like tank instead - through the game's own calls. AfterSpawn()
//     applies it to every spawn made inside AsAdmin() while the admin switch
//     "as found" is on; N9 world spawns call Found() directly. Convoys and
//     the F7/F9 keys are never touched: an NPC driver cannot fit a battery.
//   * No MySpawnPoint, so a mod vehicle is never respawned or taken away by
//     the game - it stays where it was left for the session, as a found
//     vanilla car without a point does.
//   * Trucks get a big trunk (TrunkFor): the game's own trunk container, only
//     MaxSlots/MaxWeight raised, on every client, arrays grown in place so no
//     item is lost. The window that shows more than 42 slots is the Mi-8
//     hold's (HeliHold.InstallWindow).
//   * The Mi-8 (PlayerHeli) is not a VehicleGameSystem. HeliCondition gives it
//     the same four things with the same items: battery 10002, spark plugs
//     10004 (igniters), key 10003 or 1306, fuel from the game's canister 10001.
//   * The An-2 already runs on the same items (Revival.An2Repair.cs: battery
//     10002, spark plugs 10004, canister 10001, toolkit 10005) - unchanged.
//
// No new config: everything here is gameplay and therefore simply on.
//
// C# 3.0 (csc from .NET 3.5). ASCII-only comments and logs; player-facing
// strings go through Loc.T (real Cyrillic), so this file is UTF-8 without BOM.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Vanilla parts, fuel and trunk for the mod's ground vehicles.</summary>
    public static class VehicleCondition
    {
        // The game's own vehicle items (VehicleInventoryManager constants).
        internal const int BatteryId = 10002;
        internal const int KeyId = 10003;
        internal const int SparkId = 10004;
        internal const int KeyAltId = 1306;      // the key slot's second item
        internal const int CanisterId = 10001;

        static readonly int[] Parts = new int[] { BatteryId, SparkId, KeyId };

        // A found vehicle's tank: the world's points give 20..250 against the
        // game's small tanks; against the mod's class tanks (900..1200) that
        // is this fraction.
        const float FoundFuelMin = 0.05f, FoundFuelMax = 0.25f, FoundFuelFloor = 20f;

        // ------------------------------------------------------------- trunk

        // Vanilla trunks (resources.assets, ItemsContainer on
        // InteractColliders/BagaggeContainer): VAZ-1111 24 slots / 80 kg,
        // ZAZ-968 28 / 80, UAZ-3151 32 / 100, Ural-375, BTR-80A and PAZ-672
        // 42 / 100. A 122 mm shell is 15.4 kg, a canister 10, a Stinger round
        // 10.1: a vanilla 100 kg trunk holds six shells.
        internal const int TruckSlots = 120;
        internal const float TruckKg = 1500f;
        internal const int TechnicalSlots = 64;
        internal const float TechnicalKg = 400f;

        /// <summary>The admin panel's switch: vehicles it spawns come as found
        /// (vanilla roll) instead of ready. Off = ready, as before.</summary>
        internal static bool SpawnFound;
        static bool _scope;

        /// <summary>True only inside an admin spawn with the switch on.</summary>
        internal static bool FoundNow { get { return _scope && SpawnFound; } }

        /// <summary>Run one admin spawn; while it runs, AfterSpawn and the
        /// helicopter know it is the admin's and may apply "as found".</summary>
        internal static string AsAdmin(Func<string> spawn)
        {
            _scope = true;
            try { return spawn(); }
            finally { _scope = false; }
        }

        internal static void Install(Harmony harmony)
        {
            HeliHold.InstallWindow(harmony);
        }

        /// <summary>CarSpawn's last step, on the master.</summary>
        internal static void AfterSpawn(GameObject car)
        {
            if (car == null || !FoundNow) return;
            Found(car);
        }

        // ------------------------------------------------------ reflection

        static Type _tVgs;
        static MethodInfo _mSet;
        static MethodInfo _gBattery, _gSpark, _gKeys;
        static FieldInfo _fDur, _fDurMax;
        static bool _looked;

        static bool Look()
        {
            if (_looked) return _mSet != null;
            _looked = true;
            _tVgs = RevivalPlugin.TypeByName("VehicleGameSystem");
            if (_tVgs == null) return false;
            _mSet = AccessTools.Method(_tVgs, "LocalSetVehicleComponent", null, null);
            _gBattery = AccessTools.PropertyGetter(_tVgs, "Battery");
            _gSpark = AccessTools.PropertyGetter(_tVgs, "SparkPlugs");
            _gKeys = AccessTools.PropertyGetter(_tVgs, "Keys");
            _fDur = AccessTools.Field(_tVgs, "Durability");
            _fDurMax = AccessTools.Field(_tVgs, "DurabilityMax");
            if (_mSet == null)
                RevivalPlugin.L.LogWarning("VehicleCondition: VehicleGameSystem."
                    + "LocalSetVehicleComponent not found - parts cannot be set.");
            return _mSet != null;
        }

        static Component Vgs(GameObject car)
        {
            if (car == null || !Look()) return null;
            Component v = car.GetComponent(_tVgs);
            if (v == null) v = car.GetComponentInChildren(_tVgs);
            return v;
        }

        static bool SetPart(Component v, int id, bool on)
        {
            try
            {
                ParameterInfo[] ps = _mSet.GetParameters();
                object second = ps.Length > 1 && ps[1].ParameterType == typeof(bool)
                    ? (object)on : (object)(on ? 1 : 0);
                _mSet.Invoke(v, new object[] { id, second });
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("VehicleCondition: part " + id + ": " + ex.Message);
                return false;
            }
        }

        static bool Has(Component v, MethodInfo getter)
        {
            if (v == null || getter == null) return false;
            try { return (bool)getter.Invoke(v, null); }
            catch { return false; }
        }

        // ------------------------------------------------------ conditions

        /// <summary>
        /// The world's own spawn-point roll (3/3/3): battery, spark plugs and
        /// key each fitted with 50 percent, a low tank. Master only, like
        /// SetPartSpawn. Durability stays as the spawn left it - the game's
        /// found cars are at full durability too.
        /// </summary>
        internal static bool Found(GameObject car)
        {
            if (!RevivalTroopInsertion.MasterClient()) return false;
            Component v = Vgs(car);
            if (v == null) return false;
            string rolled = "";
            for (int i = 0; i < Parts.Length; i++)
            {
                bool fit = UnityEngine.Random.value > 0.5f;
                SetPart(v, Parts[i], fit);
                rolled += (i > 0 ? ", " : "") + PartLog(Parts[i]) + (fit ? " in" : " missing");
            }
            float tank = FuelBalance.TankSize(v);
            float fuel = Mathf.Max(FoundFuelFloor,
                tank * UnityEngine.Random.Range(FoundFuelMin, FoundFuelMax));
            if (tank > 0f) fuel = Mathf.Min(fuel, tank);
            FuelBalance.SetFuel(v, fuel);
            RevivalPlugin.L.LogInfo("VehicleCondition: " + car.name + " as found - "
                + rolled + ", fuel " + Mathf.RoundToInt(fuel) + "/" + Mathf.RoundToInt(tank) + ".");
            return true;
        }

        /// <summary>All three parts in and a full tank - what CarSpawn.Prepare
        /// gives, for the admin's "make ready" on a vehicle already standing.</summary>
        internal static bool Ready(GameObject car)
        {
            if (!RevivalTroopInsertion.MasterClient()) return false;
            Component v = Vgs(car);
            if (v == null) return false;
            for (int i = 0; i < Parts.Length; i++) SetPart(v, Parts[i], true);
            float tank = FuelBalance.TankSize(v);
            if (tank > 0f) FuelBalance.SetFuel(v, tank);
            RevivalPlugin.L.LogInfo("VehicleCondition: " + car.name + " made ready.");
            return true;
        }

        static string PartLog(int id)
        {
            return id == BatteryId ? "battery" : id == SparkId ? "spark plugs" : "key";
        }

        internal static string PartName(int id)
        {
            if (id == BatteryId) return Loc.T("аккумулятор", "battery");
            if (id == SparkId) return Loc.T("свечи", "spark plugs");
            return Loc.T("ключ", "key");
        }

        /// <summary>One line for the admin panel: parts, fuel, durability, trunk.</summary>
        internal static string Describe(GameObject car)
        {
            Component v = Vgs(car);
            if (v == null) return Loc.T("не машина", "not a vehicle");
            string s = car.name + ": "
                + PartName(BatteryId) + (Has(v, _gBattery) ? " +" : " -") + ", "
                + PartName(SparkId) + (Has(v, _gSpark) ? " +" : " -") + ", "
                + PartName(KeyId) + (Has(v, _gKeys) ? " +" : " -")
                + "; " + Loc.T("топливо ", "fuel ") + Mathf.RoundToInt(FuelBalance.Fuel(v))
                + "/" + Mathf.RoundToInt(FuelBalance.TankSize(v));
            try
            {
                if (_fDur != null && _fDurMax != null)
                    s += "; " + Loc.T("прочность ", "durability ")
                        + Mathf.RoundToInt(Convert.ToSingle(_fDur.GetValue(v))) + "/"
                        + Mathf.RoundToInt(Convert.ToSingle(_fDurMax.GetValue(v)));
            }
            catch { }
            int slots; float kg;
            if (TrunkSize(car, out slots, out kg))
                s += "; " + Loc.T("багажник ", "trunk ") + slots + " / "
                    + Mathf.RoundToInt(kg) + Loc.T(" кг", " kg");
            return s;
        }

        /// <summary>The nearest drivable vehicle within reach of the local
        /// player, from the shared VehicleScan registry.</summary>
        internal static GameObject Nearest(float metres)
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return null;
            Vector3 p = me.transform.position;
            Component[] all = VehicleScan.All();
            GameObject best = null;
            float nearest = metres;
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i];
                if (v == null) continue;
                float d = Vector3.Distance(p, v.transform.position);
                if (d > nearest) continue;
                nearest = d;
                best = v.transform.root.gameObject;
            }
            return best;
        }

        /// <summary>Put a registered vehicle kind down in front of the camera -
        /// the technical's own placement, for the kinds the admin panel had
        /// no button for (Ural, T-72, BTR-80A/MTW).</summary>
        internal static string SpawnKindInFront(string kind)
        {
            if (!RevivalTroopInsertion.MasterClient())
                return Loc.T("спавн техники - только хост", "vehicle spawns are host only");
            Camera cam = Camera.main;
            if (cam == null) return "No player camera available.";
            Vector3 ahead = cam.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.000001f) ahead = Vector3.forward;
            ahead.Normalize();
            Vector3 above = cam.transform.position + ahead * 10f + Vector3.up * 30f;
            Vector3 ground;
            GameObject under = Turret.RaycastObject(above, Vector3.down, 200f, out ground);
            if (under == null) return "No ground found ahead of the player.";
            bool isTank;
            GameObject car = VehicleRegistry.Spawn(kind, ground + Vector3.up * 1.6f,
                Quaternion.LookRotation(ahead, Vector3.up), out isTank);
            if (car == null) return kind + ": spawn failed; see the game log.";
            VehicleRegistry.Entry e = VehicleRegistry.Get(kind);
            return (e == null ? kind : e.Label) + Loc.T(" создан", " spawned")
                + (FoundNow ? Loc.T(" (как найденный)", " (as found)") : "");
        }

        // ============================================================ trunk

        static Type _tContainer, _tData;
        static FieldInfo _fData, _fMaxSlots, _fMaxWeight;
        static bool _trunkLooked;
        static float _nextTrunk;
        static readonly HashSet<int> _sized = new HashSet<int>();

        static bool TrunkLook()
        {
            if (_trunkLooked) return _fMaxSlots != null;
            _trunkLooked = true;
            _tContainer = RevivalPlugin.TypeByName("ItemsContainer");
            _tData = RevivalPlugin.TypeByName("ContainerData");
            if (_tContainer == null || _tData == null) return false;
            _fData = AccessTools.Field(_tContainer, "_containerData");
            _fMaxSlots = AccessTools.Field(_tData, "MaxSlots");
            _fMaxWeight = AccessTools.Field(_tData, "MaxWeight");
            if (_fData == null || _fMaxSlots == null || _fMaxSlots.FieldType != typeof(int))
            {
                RevivalPlugin.L.LogWarning("VehicleCondition: ContainerData.MaxSlots "
                    + "not found - the trucks keep the game's trunk.");
                _fMaxSlots = null;
            }
            return _fMaxSlots != null;
        }

        /// <summary>The trunk a vehicle of ours should have; false = keep the
        /// game's. Trucks by the fuel balance's own class (the Ural, the
        /// drivable howitzer, any Ural-375), the technical as a pickup.</summary>
        internal static bool TrunkFor(Transform root, out int slots, out float kg)
        {
            slots = 0; kg = 0f;
            if (root == null) return false;
            if (Technical.IstTechnical(root)) { slots = TechnicalSlots; kg = TechnicalKg; return true; }
            if (FuelBalance.ClassOf(root) == "truck") { slots = TruckSlots; kg = TruckKg; return true; }
            return false;
        }

        /// <summary>The vehicle's trunk: the container on
        /// InteractColliders/BagaggeContainer (Awake renames it
        /// "BagaggeContainer_&lt;view&gt;"), else its only container.</summary>
        static Component Trunk(GameObject car)
        {
            if (car == null || !TrunkLook()) return null;
            Component[] all = car.GetComponentsInChildren(_tContainer, true);
            if (all == null || all.Length == 0) return null;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name.StartsWith("BagaggeContainer", StringComparison.Ordinal))
                    return all[i];
            return all.Length == 1 ? all[0] : null;
        }

        static bool TrunkSize(GameObject car, out int slots, out float kg)
        {
            slots = 0; kg = 0f;
            Component c = Trunk(car);
            object data = c == null ? null : _fData.GetValue(c);
            if (data == null) return false;
            slots = (int)_fMaxSlots.GetValue(data);
            if (_fMaxWeight != null) kg = Convert.ToSingle(_fMaxWeight.GetValue(data));
            return true;
        }

        /// <summary>Every three seconds, on every client: a vehicle not yet
        /// looked at gets its trunk. Each client grows its own copy - the
        /// window, the free-slot search and the weight check all read the
        /// local ContainerData.</summary>
        static void TickTrunks()
        {
            if (Time.time < _nextTrunk) return;
            _nextTrunk = Time.time + 3f;
            if (!TrunkLook()) return;
            Component[] all = VehicleScan.All();
            for (int i = 0; i < all.Length; i++)
            {
                Component v = all[i];
                if (v == null) continue;
                int id = v.GetInstanceID();
                if (_sized.Contains(id)) continue;
                _sized.Add(id);
                int slots; float kg;
                if (!TrunkFor(v.transform.root, out slots, out kg)) continue;
                try { Grow(v.transform.root.gameObject, slots, kg); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("VehicleCondition: trunk of "
                        + v.transform.root.name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Raise MaxSlots and MaxWeight. Every per-slot array of ContainerData
        /// (the eighteen SetContainerDataArraysLenght builds) is copied into a
        /// longer one instead of being rebuilt, so a trunk that already holds
        /// items - a late joiner's copy, a second pass - keeps every one of them.
        /// </summary>
        static void Grow(GameObject car, int slots, float kg)
        {
            Component c = Trunk(car);
            if (c == null) return;
            object data = _fData.GetValue(c);
            if (data == null) return;
            int have = (int)_fMaxSlots.GetValue(data);
            if (have < slots)
            {
                FieldInfo[] fs = _tData.GetFields(BindingFlags.Instance | BindingFlags.Public
                                                  | BindingFlags.NonPublic);
                for (int i = 0; i < fs.Length; i++)
                {
                    if (!fs[i].FieldType.IsArray) continue;
                    Array old = fs[i].GetValue(data) as Array;
                    if (old == null || old.Length != have) continue;
                    Array grown = Array.CreateInstance(fs[i].FieldType.GetElementType(), slots);
                    Array.Copy(old, grown, have);
                    fs[i].SetValue(data, grown);
                }
                _fMaxSlots.SetValue(data, slots);
            }
            if (_fMaxWeight != null && _fMaxWeight.FieldType == typeof(float)
                && (float)_fMaxWeight.GetValue(data) < kg)
                _fMaxWeight.SetValue(data, kg);
            RevivalPlugin.L.LogInfo("VehicleCondition: trunk of " + car.name + " "
                + have + " -> " + Math.Max(have, slots) + " slots, " + Mathf.RoundToInt(kg) + " kg.");
        }

        // ============================================================ loop

        internal static void Tick()
        {
            try { TickTrunks(); }
            catch (Exception ex) { RevivalPlugin.L.LogError("VehicleCondition.Tick: " + ex); }
            HeliCondition.Tick();
        }

        internal static void Draw()
        {
            HeliCondition.Draw();
        }
    }

    /// <summary>
    /// The Mi-8's vanilla condition. Unknown machine = ready (every part in, a
    /// full tank): the admin's ready spawn and every machine from before this
    /// change behave as they did, except that the tank now empties.
    ///
    /// AUTHORITY. The master owns parts; the pilot owns the fuel while his
    /// engine runs. Both travel on the helicopter's own EngineState message
    /// with a negative second value (PlayerHeli.Net, no new event code):
    ///   { view, -1, mask, fuel }     the state, from the master (every 5 s
    ///                                and on change) and from a flying pilot
    ///   { view, -2, what, amount }   a request to the master: what 0..2 fit
    ///                                part bit, 3 pour fuel
    /// </summary>
    internal static class HeliCondition
    {
        internal const int BatteryBit = 0, SparkBit = 1, KeyBit = 2, AllParts = 7;
        const int FuelWhat = 3;
        internal const float Capacity = 1000f;         // fuel units, a canister is 100
        const float CanisterUnits = 100f;
        const float BurnIdle = 12f, BurnFull = 60f;     // units per minute
        const float FoundFuelMin = 0.15f, FoundFuelMax = 0.35f;
        const float Reach = 12f;                        // metres from the machine's root

        sealed class State
        {
            public int Mask = AllParts;
            public float Fuel = Capacity;
        }

        static readonly Dictionary<int, State> _state = new Dictionary<int, State>();
        static float _nextBeat, _nextSend;
        static string _prompt;

        static State Get(GameObject go, bool create)
        {
            int view = PlayerHeli.ViewOf(go);
            if (view == 0) return null;
            State s;
            if (_state.TryGetValue(view, out s)) return s;
            if (!create) return null;
            s = new State();
            _state[view] = s;
            return s;
        }

        static void Send(int view, float a, float b, float c)
        {
            PlayerHeli.Net.Send(PlayerHeli.Net.EngineState, new float[] { view, a, b, c }, true);
        }

        static void Broadcast(int view, State s)
        {
            if (view != 0 && s != null) Send(view, -1f, s.Mask, s.Fuel);
        }

        // --------------------------------------------------------- the rules

        internal static bool CanStart(GameObject go, out string why)
        {
            why = null;
            State s = go == null ? null : Get(go, false);
            if (s == null) return true;
            if ((s.Mask & AllParts) != AllParts)
            {
                why = Loc.T("Двигатель не запускается. Нет: ", "The engine will not start. Missing: ")
                    + Missing(s.Mask) + Loc.T(" - установите снаружи (", " - fit it on foot (")
                    + Key() + ")";
                return false;
            }
            if (s.Fuel <= 0f)
            {
                why = Loc.T("Бак пуст - залейте канистру снаружи (", "The tank is empty - pour in a canister on foot (")
                    + Key() + ")";
                return false;
            }
            return true;
        }

        /// <summary>The pilot's fuel burn; true once, when the tank runs dry.</summary>
        internal static bool Burn(GameObject go, float power, float dt)
        {
            if (go == null || dt <= 0f) return false;
            State s = Get(go, true);
            if (s == null) return false;
            if (s.Fuel <= 0f) return true;
            float perMinute = Mathf.Lerp(BurnIdle, BurnFull, Mathf.Clamp01(power));
            s.Fuel = Mathf.Max(0f, s.Fuel - perMinute / 60f * dt);
            if (Time.time >= _nextSend || s.Fuel <= 0f)
            {
                _nextSend = Time.time + 2f;
                Broadcast(PlayerHeli.ViewOf(go), s);
            }
            return s.Fuel <= 0f;
        }

        internal static string FuelLine(GameObject go, out bool low)
        {
            low = false;
            if (go == null) return null;
            State s = Get(go, false);
            float fuel = s == null ? Capacity : s.Fuel;
            low = fuel < Capacity * 0.1f;
            return Loc.T("Топливо ", "Fuel ") + Mathf.RoundToInt(fuel) + " / "
                + Mathf.RoundToInt(Capacity) + (low ? Loc.T(" - МАЛО", " - LOW") : "");
        }

        internal static string EmptyText()
        {
            return Loc.T("Топливо кончилось - двигатель встал", "Out of fuel - the engine has stopped");
        }

        /// <summary>The world's 3/3/3 roll on a helicopter: each part 50
        /// percent, a partly filled tank. Master only.</summary>
        internal static void MakeFound(GameObject go)
        {
            if (go == null || !RevivalTroopInsertion.MasterClient()) return;
            State s = Get(go, true);
            if (s == null) return;
            s.Mask = 0;
            if (UnityEngine.Random.value > 0.5f) s.Mask |= 1 << BatteryBit;
            if (UnityEngine.Random.value > 0.5f) s.Mask |= 1 << SparkBit;
            if (UnityEngine.Random.value > 0.5f) s.Mask |= 1 << KeyBit;
            s.Fuel = Mathf.Round(Capacity * UnityEngine.Random.Range(FoundFuelMin, FoundFuelMax));
            int view = PlayerHeli.ViewOf(go);
            Broadcast(view, s);
            RevivalPlugin.L.LogInfo("HeliCondition: helicopter " + view + " as found - mask "
                + s.Mask + ", fuel " + Mathf.RoundToInt(s.Fuel) + ".");
        }

        /// <summary>The admin's "make ready" for a helicopter.</summary>
        internal static void MakeReady(GameObject go)
        {
            if (go == null || !RevivalTroopInsertion.MasterClient()) return;
            State s = Get(go, true);
            if (s == null) return;
            s.Mask = AllParts;
            s.Fuel = Capacity;
            Broadcast(PlayerHeli.ViewOf(go), s);
        }

        internal static string Describe(GameObject go)
        {
            State s = go == null ? null : Get(go, false);
            int m = s == null ? AllParts : s.Mask;
            float fuel = s == null ? Capacity : s.Fuel;
            return "Mi-8: " + PartName(BatteryBit) + ((m & 1 << BatteryBit) != 0 ? " +" : " -") + ", "
                + PartName(SparkBit) + ((m & 1 << SparkBit) != 0 ? " +" : " -") + ", "
                + PartName(KeyBit) + ((m & 1 << KeyBit) != 0 ? " +" : " -") + "; "
                + Loc.T("топливо ", "fuel ") + Mathf.RoundToInt(fuel) + "/" + Mathf.RoundToInt(Capacity);
        }

        // --------------------------------------------------------- network

        internal static void OnNet(float[] f, int sender)
        {
            if (f == null || f.Length < 4) return;
            int view = (int)f[0];
            if (f[1] > -1.5f)
            {
                // { view, -1, mask, fuel }
                State s;
                if (!_state.TryGetValue(view, out s)) { s = new State(); _state[view] = s; }
                s.Mask = (int)f[2] & AllParts;
                GameObject mine = PlayerHeli.FlownMachine;
                bool flyingIt = mine != null && PlayerHeli.EngineRunning
                                && PlayerHeli.ViewOf(mine) == view;
                if (!flyingIt) s.Fuel = Mathf.Clamp(f[3], 0f, Capacity);
                return;
            }
            // { view, -2, what, amount } - the master applies it.
            if (!RevivalTroopInsertion.MasterClient()) return;
            GameObject go = PlayerHeli.MachineByView(view);
            if (go == null) return;
            Apply(go, (int)f[2], f[3]);
            RevivalPlugin.L.LogInfo("HeliCondition: player " + sender + " - helicopter "
                + view + ", " + ((int)f[2] == FuelWhat ? "fuel +" + Mathf.RoundToInt(f[3]) : "part " + (int)f[2]) + ".");
        }

        static void Apply(GameObject go, int what, float amount)
        {
            State s = Get(go, true);
            if (s == null) return;
            if (what == FuelWhat) s.Fuel = Mathf.Clamp(s.Fuel + amount, 0f, Capacity);
            else if (what >= 0 && what <= KeyBit) s.Mask |= 1 << what;
            else return;
            Broadcast(PlayerHeli.ViewOf(go), s);
        }

        static void Request(GameObject go, int what, float amount)
        {
            if (RevivalTroopInsertion.MasterClient()) { Apply(go, what, amount); return; }
            int view = PlayerHeli.ViewOf(go);
            if (view != 0) Send(view, -2f, what, amount);
            // Show it at once; the master's answer confirms it.
            State s = Get(go, true);
            if (s == null) return;
            if (what == FuelWhat) s.Fuel = Mathf.Clamp(s.Fuel + amount, 0f, Capacity);
            else s.Mask |= 1 << what;
        }

        // ------------------------------------------------------ on foot

        static string Key()
        {
            return PlayerAn2.KeyOf(PlayerHeli.CfgEngineKey, KeyCode.G).ToString();
        }

        internal static void Tick()
        {
            _prompt = null;
            if (!PlayerHeli.Enabled) return;
            try
            {
                if (RevivalTroopInsertion.MasterClient() && Time.time >= _nextBeat)
                {
                    _nextBeat = Time.time + 5f;
                    foreach (KeyValuePair<int, State> e in _state)
                        if (PlayerHeli.MachineByView(e.Key) != null) Broadcast(e.Key, e.Value);
                }
                if (PlayerHeli.Aboard || PlayerAn2.Aboard || ConvoyRepair.InVehicle()) return;
                GameObject go = PlayerHeli.NearestMachine(Reach);
                if (go == null) return;
                State s = Get(go, false);
                if (s == null) return;                       // ready and full

                bool pressed = Input.GetKeyDown(PlayerAn2.KeyOf(PlayerHeli.CfgEngineKey, KeyCode.G));
                for (int bit = 0; bit <= KeyBit; bit++)
                {
                    if ((s.Mask & 1 << bit) != 0) continue;
                    int item = ItemFor(bit);
                    if (item == 0) continue;
                    _prompt = "[" + Key() + "] " + Loc.T("Установить: ", "Fit: ") + PartName(bit);
                    if (!pressed) return;
                    if (!Turret.TakeItem(item, "HeliCondition"))
                    { Turret.Hinweis(Loc.T("Детали нет", "The part is gone"), 2f); return; }
                    Request(go, bit, 0f);
                    Turret.Hinweis(Loc.T("Установлено: ", "Fitted: ") + PartName(bit), 3f);
                    return;
                }
                if (s.Fuel < Capacity - 1f && Turret.HasItem(VehicleCondition.CanisterId))
                {
                    float add = Mathf.Min(CanisterUnits, Capacity - s.Fuel);
                    _prompt = "[" + Key() + "] " + Loc.T("Залить канистру", "Pour in a canister")
                        + " (+" + Mathf.RoundToInt(add) + ")";
                    if (!pressed) return;
                    if (!Turret.TakeItem(VehicleCondition.CanisterId, "HeliCondition"))
                    { Turret.Hinweis(Loc.T("Канистры нет", "The canister is gone"), 2f); return; }
                    Request(go, FuelWhat, add);
                    Turret.Hinweis(Loc.T("Канистра залита", "Canister poured in"), 3f);
                    return;
                }
                if ((s.Mask & AllParts) != AllParts)
                    _prompt = Loc.T("Вертолёт. Нужно: ", "Helicopter. Needed: ") + Missing(s.Mask);
                else if (s.Fuel < Capacity * 0.1f)
                    _prompt = Loc.T("Вертолёт: мало топлива - нужна канистра", "Helicopter: low fuel - a canister is needed");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("HeliCondition.Tick: " + ex);
            }
        }

        /// <summary>The item the player carries for a part, 0 = none. The key
        /// slot takes the game's key or its second item, as the car's does.</summary>
        static int ItemFor(int bit)
        {
            if (bit == BatteryBit) return Turret.HasItem(VehicleCondition.BatteryId) ? VehicleCondition.BatteryId : 0;
            if (bit == SparkBit) return Turret.HasItem(VehicleCondition.SparkId) ? VehicleCondition.SparkId : 0;
            if (Turret.HasItem(VehicleCondition.KeyId)) return VehicleCondition.KeyId;
            return Turret.HasItem(VehicleCondition.KeyAltId) ? VehicleCondition.KeyAltId : 0;
        }

        static string PartName(int bit)
        {
            return VehicleCondition.PartName(bit == BatteryBit ? VehicleCondition.BatteryId
                : bit == SparkBit ? VehicleCondition.SparkId : VehicleCondition.KeyId);
        }

        static string Missing(int m)
        {
            string s = "";
            for (int bit = 0; bit <= KeyBit; bit++)
            {
                if ((m & 1 << bit) != 0) continue;
                s += (s.Length > 0 ? ", " : "") + PartName(bit);
            }
            return s;
        }

        internal static void Draw()
        {
            if (string.IsNullOrEmpty(_prompt) || !Hints.Prompts || GameUi.WindowOpen) return;
            if (Event.current != null && Event.current.type != EventType.Repaint) return;
            Vector2 size = GUI.skin.label.CalcSize(new GUIContent(_prompt));
            float w = size.x + 24f, h = Mathf.Max(24f, size.y + 8f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.62f;
            Color keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = keep;
            GUI.Label(new Rect(x + 12f, y + (h - size.y) * 0.5f, size.x + 4f, size.y), _prompt);
        }
    }
}

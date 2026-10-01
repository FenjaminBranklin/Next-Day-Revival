// Optional binding to Z M2. The fallback-main queue lacks its source files.
// Bind the measured contracts once; never invent a replacement depot/stock.
using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercSupplyDepot
    {
        static bool _tried;
        internal static bool Available;
        static Func<object, object> _store, _ammo;
        static Func<object, Vector3> _at;
        static Func<object, bool> _ready, _known, _limited, _storeKnown;
        static Func<object, int> _stock, _actor;
        static Func<object, float> _hp, _until;
        static Func<object, int, int> _find;
        static Action<object, int> _remove;
        static Action<Flak.Gun, int> _credit;
        static Action _broadcast;
        static readonly float[] Snapshot = new float[16];
        static float _snapshotAt;
        internal static Func<MercUnit, bool> Medic;
        static Func<object, object> _down, _rescue;
        static Func<object, bool> _downFlag;

        internal static void Bind()
        {
            if (_tried) return;
            _tried = true;
            Type depot = typeof(MercSupplyDepot).Assembly.GetType("NextDayRevival.AmmoDepot");
            if (depot == null) return;
            try
            {
                FieldInfo store = AccessTools.Field(depot, "Store"), ammo = AccessTools.Field(typeof(Flak.Gun), "Ammo");
                if (store == null || ammo == null) return;
                Type st = store.FieldType, am = ammo.FieldType;
                _store = Read<object>(store); _ammo = Read<object>(ammo);
                _at = Read<Vector3>(AccessTools.Field(depot, "At"));
                _ready = Read<bool>(AccessTools.Field(depot, "_ready"));
                _known = Read<bool>(AccessTools.Field(am, "Known"));
                _limited = Read<bool>(AccessTools.Field(am, "Limited"));
                _stock = Read<int>(AccessTools.Field(am, "Stock"));
                _actor = Read<int>(AccessTools.Field(am, "Actor"));
                _until = Read<float>(AccessTools.Field(am, "Until"));
                _hp = Read<float>(AccessTools.Field(st, "Hp"));
                _storeKnown = Read<bool>(AccessTools.Field(st, "Known"));
                _find = (Func<object, int, int>)Instance(AccessTools.Method(st, "Find", null, null), typeof(Func<object, int, int>), typeof(int));
                _remove = (Action<object, int>)Instance(AccessTools.Method(st, "Remove", null, null), typeof(Action<object, int>), typeof(void));
                _credit = (Action<Flak.Gun, int>)Delegate.CreateDelegate(typeof(Action<Flak.Gun, int>), AccessTools.Method(depot, "Credit", null, null));
                _broadcast = (Action)Delegate.CreateDelegate(typeof(Action), AccessTools.Method(depot, "Broadcast", null, null));
                MethodInfo medic = AccessTools.Method(typeof(Mercs), "IsMedic", null, null);
                if (medic != null) Medic = (Func<MercUnit, bool>)Delegate.CreateDelegate(typeof(Func<MercUnit, bool>), medic);
                FieldInfo down = AccessTools.Field(typeof(Mercs.Record), "Down");
                if (down != null)
                { _down = Read<object>(down); _downFlag = Read<bool>(AccessTools.Field(down.FieldType, "Down")); }
                FieldInfo rescue = AccessTools.Field(typeof(MercUnit), "RescueTarget");
                if (rescue != null) _rescue = Read<object>(rescue);
                // M2's bulk retry otherwise teleports stock to an unattended gun.
                // Keep manual player supplies and truck transfers untouched.
                MethodInfo supply = AccessTools.Method(depot, "TrySupply", new Type[] { typeof(Flak.Gun), typeof(int), typeof(bool) }, null);
                if (supply == null) return;
                new Harmony("nextday.revival.merc-resupply").Patch(supply,
                    new HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(MercSupplyDepot), "PhysicalCrewSupply", null, null)), null, null, null, null);
                Available = true;
            }
            catch (Exception ex)
            { Available = false; RevivalPlugin.L.LogWarning("Merc resupply: prerequisite contract unavailable: " + ex.Message); }
        }
        static bool PhysicalCrewSupply(bool __2, ref bool __result)
        { if (!__2) return true; __result = false; return false; }
        internal static bool Busy(Mercs.Record r)
        {
            return r == null || r.Unit == null || (r.Unit.Medicine != null && r.Unit.Medicine.Active != 0)
                || (_down != null && _downFlag(_down(r))) || (_rescue != null && _rescue(r.Unit) != null);
        }
        internal static Vector3 At { get { return _at(null); } }
        internal static bool Ready
        { get { return Available && _ready(null) && _storeKnown(_store(null)) && _hp(_store(null)) > 0f; } }
        internal static int Stock(Flak.Gun g)
        { return g == null || !Available ? -1 : _stock(_ammo(g)); }
        internal static bool Finite(Flak.Gun g)
        { return g != null && Available && _known(_ammo(g)) && _limited(_ammo(g)); }
        internal static bool Locked(Flak.Gun g, float now)
        { object a = _ammo(g); return _actor(a) >= 0 && now < _until(a); }
        internal static int Item(int gun)
        { Flak.Gun g = Flak.ByIndex(gun); return MercSupplyLedger.Repair(gun) ? ToolkitItem() : gun < 0 ? MedicalItem() : g == null ? 0 : g.ShortRange ? 2077 : 2076; }
        internal static int ToolkitItem()
        { return Has(10005) ? 10005 : Has(2064) ? 2064 : 0; }
        internal static int MedicalItem()
        {
            if (!Ready) return 0;
            object s = _store(null);
            for (int id = 7013; id <= 7016; id++) if (_find(s, id) >= 0) return id;
            for (int id = 7011; id <= 7012; id++) if (_find(s, id) >= 0) return id;
            return 0;
        }
        internal static bool Has(int item)
        { return Ready && item > 0 && _find(_store(null), item) >= 0; }
        internal static void RequestKnowledge(float now)
        {
            if (!Available || Crocodile.IsMaster() || _storeKnown(_store(null)) || now < _snapshotAt || !Mercs.Any) return;
            _snapshotAt = now + 3f;
            // Late joiners standing at their guns may never approach storage.
            // Use M2's existing snapshot request instead of waiting for a new
            // inventory revision or requiring the player to visit the depot.
            Snapshot[0] = 28f; Snapshot[2] = MercAA.LocalActor(); Snapshot[5] = -1f; Snapshot[7] = MercAA.MasterActor();
            FlakNet.SendPacket(Snapshot, true);
        }
        // Master-only, called after validating the carrier at the actual depot.
        internal static int Take(int gun, int item, float now)
        {
            if (!Crocodile.IsMaster() || !Has(item)) return 0;
            if (MercSupplyLedger.Repair(gun))
            {
                if ((item != 10005 && item != 2064) || !AirDefenceDamage.DepotRepairNeeded(gun - 100)) return 0;
                int slot = _find(_store(null), item); if (slot < 0) return 0;
                _remove(_store(null), slot); _broadcast(); return 1;
            }
            Flak.Gun g = gun < 0 ? null : Flak.ByIndex(gun);
            if (gun >= 0 && (!Finite(g) || Locked(g, now) || !AirDefenceDamage.Alive(gun))) return 0;
            int perBox = item == 2077 ? 100 : 1;
            int wanted = gun < 0 ? 1 : g.ShortRange ? 2 : Math.Min(20, Flak.RoundsPerLoad);
            int capacity = gun < 0 ? 1 : (g.ShortRange ? 1000 : 60) - Stock(g);
            int amount = 0; object store = _store(null);
            while (wanted-- > 0 && amount + perBox <= capacity)
            {
                int slot = _find(store, item); if (slot < 0) break;
                _remove(store, slot); amount += perBox;
            }
            if (amount > 0) _broadcast();
            return amount;
        }
        internal static void Credit(int gun, int amount)
        {
            if (!Crocodile.IsMaster()) return;
            Flak.Gun g = Flak.ByIndex(gun);
            if (!Finite(g) || !AirDefenceDamage.Alive(gun)) return;
            amount = Math.Min(amount, (g.ShortRange ? 1000 : 60) - Stock(g));
            if (amount > 0) _credit(g, amount);
        }
        // These delegates are generated once. No boxed field reads or Invoke
        // arrays occur during sensing, trip updates, or master validation.
        static Func<object, T> Read<T>(FieldInfo f)
        {
            if (f == null) throw new InvalidOperationException("missing prerequisite field");
            DynamicMethod dm = new DynamicMethod("MercSupplyRead", typeof(T), new Type[] { typeof(object) }, typeof(MercSupplyDepot), true);
            ILGenerator il = dm.GetILGenerator();
            if (!f.IsStatic) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, f.DeclaringType); }
            il.Emit(f.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, f); il.Emit(OpCodes.Ret);
            return (Func<object, T>)dm.CreateDelegate(typeof(Func<object, T>));
        }
        static Delegate Instance(MethodInfo m, Type delegateType, Type result)
        {
            if (m == null) throw new InvalidOperationException("missing prerequisite method");
            DynamicMethod dm = new DynamicMethod("MercSupplyCall", result, new Type[] { typeof(object), typeof(int) }, typeof(MercSupplyDepot), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, m.DeclaringType);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, m);
            if (result == typeof(void) && m.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret); return dm.CreateDelegate(delegateType);
        }
    }
}

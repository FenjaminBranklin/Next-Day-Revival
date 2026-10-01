// Optional prerequisite bindings. All reflection/delegate creation is cold.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal struct MercFetchGood
    {
        internal int Id, Bullets, Clip;
        internal float Condition, Water, Energy;
        internal bool Valid
        {
            get
            {
                return (Id == 2076 || Id == 2077 || Id == 10005 || Id == 2064 || (Id >= 7011 && Id <= 7016))
                    && Bullets >= 0 && Bullets <= 100000 && Clip >= 0 && Clip <= 100000
                    && Finite(Condition, 100f) && Finite(Water, 10000f) && Finite(Energy, 10000f);
            }
        }
        static bool Finite(float n, float max) { return !float.IsNaN(n) && !float.IsInfinity(n) && n >= 0f && n <= max; }
    }

    internal static class MercFetchBridge
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        internal static bool Available;
        static Func<object> _run;
        static Func<MercCarrier> _car;
        static Func<Mercs.Record> _driver;
        static Func<bool> _active, _known;
        static Func<float> _hp;
        static Func<int> _count, _depotActor;
        static Func<Vector3> _depot;
        static Func<List<Mercs.Record>, Vector3, bool> _start;
        static Action _finish, _broadcast;
        static Action<Vector3, Vector3, List<Vector3>> _road;
        static Action<object, float> _return;
        static Func<object, List<Vector3>> _path;
        static Func<MercFetchGood, bool> _add;
        static Action<object, int> _setPhase, _setNext, _setReason;
        static FieldInfo _phase, _wait, _deadline, _next, _reason;
        static FieldInfo _reported;
        delegate void DepotMessage(int kind, int ticket, int actor, int request, int truck, int gun, int limit);
        static DepotMessage _ask;

        internal static void Install(Harmony h)
        {
            try
            {
                Assembly a = typeof(MercFetchBridge).Assembly;
                Type drive = a.GetType("NextDayRevival.MercDrive"), run = a.GetType("NextDayRevival.MercDriveRun");
                Type depot = a.GetType("NextDayRevival.AmmoDepot"), store = a.GetType("NextDayRevival.DepotStore");
                Type good = a.GetType("NextDayRevival.DepotGood");
                if (drive == null || run == null || depot == null || store == null || good == null) return;
                _run = StaticGetter<Func<object>>(drive.GetField("_run", Flags));
                _car = StaticGetter<Func<MercCarrier>>(drive.GetField("_car", Flags));
                _driver = StaticGetter<Func<Mercs.Record>>(drive.GetField("_driver", Flags));
                _active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), drive.GetProperty("Active", Flags).GetGetMethod(true));
                _start = (Func<List<Mercs.Record>, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<List<Mercs.Record>, Vector3, bool>), drive.GetMethod("Start", Flags));
                _finish = (Action)Delegate.CreateDelegate(typeof(Action), drive.GetMethod("Finish", Flags));
                _reported = drive.GetField("_reportedPoint", Flags);
                _road = (Action<Vector3, Vector3, List<Vector3>>)Delegate.CreateDelegate(typeof(Action<Vector3, Vector3, List<Vector3>>), typeof(Patrol).GetMethod("MercRoad", Flags));
                _return = (Action<object, float>)InstanceCall(run.GetMethod("Return", Flags), typeof(Action<object, float>));
                _path = (Func<object, List<Vector3>>)InstanceGetter(run.GetField("Path", Flags), typeof(Func<object, List<Vector3>>));
                _phase = run.GetField("Phase", Flags); _wait = run.GetField("WaitUntil", Flags);
                _deadline = run.GetField("Deadline", Flags); _next = run.GetField("Next", Flags); _reason = run.GetField("Reason", Flags);
                _setPhase = IntSetter(_phase); _setNext = IntSetter(_next); _setReason = IntSetter(_reason);
                FieldInfo ds = depot.GetField("Store", Flags);
                _known = StoreGetter<Func<bool>>(ds, store.GetField("Known", Flags));
                _hp = StoreGetter<Func<float>>(ds, store.GetField("Hp", Flags));
                _count = StoreGetter<Func<int>>(ds, store.GetField("Count", Flags));
                _depotActor = StoreGetter<Func<int>>(ds, store.GetField("Actor", Flags));
                _depot = StaticGetter<Func<Vector3>>(depot.GetField("At", Flags));
                _broadcast = (Action)Delegate.CreateDelegate(typeof(Action), depot.GetMethod("Broadcast", Flags));
                _ask = (DepotMessage)Delegate.CreateDelegate(typeof(DepotMessage), depot.GetMethod("Send", Flags));
                _add = AddGood(ds, store.GetMethod("Add", Flags), good);
                if (_phase == null || _wait == null || _deadline == null || _next == null || _reason == null) return;
                h.Patch(run.GetMethod("Step", Flags), null,
                    new HarmonyMethod(typeof(MercFetch).GetMethod("DriveStepPostfix")), null, null, null);
                Available = true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercFetch prerequisites unavailable: " + ex.Message); }
        }

        internal static bool Active { get { return Available && _active(); } }
        internal static bool DepotReady { get { return Available && _known() && _hp() > 0f; } }
        internal static void Snapshot() { if (Available && !_known()) _ask(28, 0, Crocodile.LocalActor(), 0, 0, -1, 0); }
        internal static bool DepotFull { get { return !DepotReady || _count() >= 256; } }
        internal static bool DepotBusy { get { return _depotActor() >= 0; } }
        internal static Vector3 Depot { get { return _depot(); } }
        internal static MercCarrier Car { get { return _car(); } }
        internal static Mercs.Record Driver { get { return _driver(); } }
        internal static object Run { get { return _run(); } }
        internal static int Phase(object run) { return FastField.GetInt(_phase, run); }
        internal static int Failure(object run)
        {
            if (Phase(run) != 5) return MercFetchJob.Cancelled;
            switch (FastField.GetInt(_reason, run))
            {
                case 1: return MercFetchJob.DriverLost;
                case 2: return MercFetchJob.VehicleLost;
                case 3: return MercFetchJob.Busy;
                case 4: return MercFetchJob.Taken;
                case 5: case 7: return MercFetchJob.Expired;
                case 6: return MercFetchJob.Blocked;
                default: return MercFetchJob.Cancelled;
            }
        }
        internal static bool Start(List<Mercs.Record> selection, Vector3 target)
        {
            bool started = _start(selection, target);
            // Fetch receipts replace the general trip's "back in 8 seconds".
            if (started && _reported != null) _reported.SetValue(null, true);
            return started;
        }

        internal static void Hold(object run, float now)
        {
            _setPhase(run, 2); // AtPoint: native handbrake, lease stays renewed.
            FastField.SetFloat(_wait, run, now + 4f); FastField.SetFloat(_deadline, run, now + 5f);
        }

        internal static void ReturnToDepot(object run, Vector3 parking)
        {
            List<Vector3> path = _path(run);
            _road(parking, Car.Root.position, path);
            _setNext(run, path.Count - 1);
            _setPhase(run, 2);
            _return(run, Time.time);
        }

        internal static void Finish(object run, bool success)
        { _setPhase(run, success ? 4 : 5); _setReason(run, 8); _finish(); }

        internal static bool Deposit(MercFetchGood good)
        {
            if (!good.Valid || !DepotReady || DepotFull || DepotBusy || !Crocodile.IsMaster()) return false;
            if (!_add(good)) return false;
            _broadcast(); return true;
        }

        static DynamicMethod Method(string name, Type result, Type[] args)
        { return new DynamicMethod(name, result, args, typeof(MercFetchBridge), true); }
        static Action<object, int> IntSetter(FieldInfo field)
        {
            if (field == null || field.FieldType != typeof(int)) throw new MissingFieldException();
            DynamicMethod dm = Method("FetchWrite", typeof(void), new Type[] { typeof(object), typeof(int) });
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
            return (Action<object, int>)dm.CreateDelegate(typeof(Action<object, int>));
        }
        internal static Delegate InstanceCall(MethodInfo method, Type type)
        {
            if (method == null) throw new MissingMethodException();
            MethodInfo sig = type.GetMethod("Invoke"); ParameterInfo[] p = sig.GetParameters();
            Type[] args = new Type[p.Length]; for (int i = 0; i < p.Length; i++) args[i] = p[i].ParameterType;
            DynamicMethod dm = Method("FetchCall", sig.ReturnType, args); ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, method.DeclaringType);
            for (int i = 1; i < args.Length; i++) il.Emit(OpCodes.Ldarg, i);
            il.Emit(OpCodes.Callvirt, method);
            if (sig.ReturnType == typeof(void) && method.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            else if (!sig.ReturnType.IsAssignableFrom(method.ReturnType)) throw new InvalidOperationException("Fetch call signature changed");
            il.Emit(OpCodes.Ret); return dm.CreateDelegate(type);
        }
        internal static Delegate InstanceGetter(FieldInfo field, Type type)
        {
            if (field == null) throw new MissingFieldException();
            DynamicMethod dm = Method("FetchRead", type.GetMethod("Invoke").ReturnType, new Type[] { typeof(object) });
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret); return dm.CreateDelegate(type);
        }
        static T StaticGetter<T>(FieldInfo field) where T : class
        {
            if (field == null) throw new MissingFieldException();
            DynamicMethod dm = Method("FetchStatic", typeof(T).GetMethod("Invoke").ReturnType, Type.EmptyTypes);
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(T)) as T;
        }
        static T StoreGetter<T>(FieldInfo store, FieldInfo field) where T : class
        {
            if (store == null || field == null) throw new MissingFieldException();
            DynamicMethod dm = Method("FetchDepot", typeof(T).GetMethod("Invoke").ReturnType, Type.EmptyTypes);
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldsfld, store); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(T)) as T;
        }
        static Func<MercFetchGood, bool> AddGood(FieldInfo store, MethodInfo add, Type good)
        {
            if (store == null || add == null) throw new MissingMethodException();
            DynamicMethod dm = Method("FetchDeposit", typeof(bool), new Type[] { typeof(MercFetchGood) });
            ILGenerator il = dm.GetILGenerator(); LocalBuilder local = il.DeclareLocal(good);
            il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Initobj, good);
            string[] names = { "Id", "Bullets", "Clip", "Condition", "Water", "Energy" };
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo dst = good.GetField(names[i], Flags), src = typeof(MercFetchGood).GetField(names[i], Flags);
                if (dst == null || dst.FieldType != src.FieldType) throw new MissingFieldException(names[i]);
                il.Emit(OpCodes.Ldloca, local); il.Emit(OpCodes.Ldarga_S, (byte)0); il.Emit(OpCodes.Ldfld, src); il.Emit(OpCodes.Stfld, dst);
            }
            il.Emit(OpCodes.Ldsfld, store); il.Emit(OpCodes.Ldloc, local); il.Emit(OpCodes.Callvirt, add); il.Emit(OpCodes.Ret);
            return (Func<MercFetchGood, bool>)dm.CreateDelegate(typeof(Func<MercFetchGood, bool>));
        }
    }
}

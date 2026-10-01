// Native cargo bridge. Discovery is sliced; reads use unboxed array delegates.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AmmoDepotCargo
    {
        internal sealed class Truck
        {
            internal Transform Root;
            internal Component Container;
            internal Rigidbody Body;
            internal int View, Owner, Scan;
            internal Array Ids, Bullets, Clips, Conditions, Waters, Energies;
            internal int Count(int id)
            {
                int n = 0;
                if (Ids != null) for (int i = 0; i < Ids.Length; i++) if (ReadInt(Ids, i) == id) n++;
                return n;
            }
            internal bool Parked { get { return Root != null && Body != null && Body.velocity.sqrMagnitude < 1f; } }
            internal bool Busy { get { return Container == null || FastCall.Bool(_busy, Container); } }
            internal void Refresh()
            {
                if (Container == null) return;
                object data = _data.GetValue(Container);
                Ids = data == null ? null : _ids.GetValue(data) as Array;
                Bullets = data == null ? null : _bullets.GetValue(data) as Array;
                Clips = data == null ? null : _clips.GetValue(data) as Array;
                Conditions = data == null ? null : _conditions.GetValue(data) as Array;
                Waters = data == null ? null : _waters.GetValue(data) as Array;
                Energies = data == null ? null : _energies.GetValue(data) as Array;
                int actor = Crocodile.ActorOf(Container.gameObject);
                Owner = actor <= 0 ? MercAA.MasterActor() : actor;
            }
            internal DepotGood Good(int slot)
            {
                DepotGood g = new DepotGood(ReadInt(Ids, slot), ReadInt(Bullets, slot), ReadInt(Clips, slot),
                    ReadFloat(Conditions, slot), ReadFloat(Waters, slot));
                g.Energy = ReadFloat(Energies, slot); return g;
            }
            internal bool Remove(int slot)
            {
                Refresh();
                if (Container == null || Owner != Crocodile.LocalActor() || Busy || !Parked) return false;
                int id = ReadInt(Ids, slot);
                _clear.Invoke(Container, new object[] { slot });
                return id > 0 && ReadInt(Ids, slot) == 0;
            }
        }
        internal static readonly List<Truck> Trucks = new List<Truck>();
        static readonly HashSet<int> Seen = new HashSet<int>();
        static Type _container;
        static FieldInfo _data, _ids, _bullets, _clips, _conditions, _waters, _energies;
        static MethodInfo _busy, _clear;
        static Func<Array, int, int> ReadInt;
        static Func<Array, int, float> ReadFloat;
        static int _scan;
        static bool _looked, _hooked;

        internal static void Install(Harmony h)
        {
            _container = RevivalPlugin.TypeByName("ItemsContainer");
            MethodInfo m = _container == null ? null : AccessTools.Method(_container, "NetworkInteractingContainerRequest", null, null);
            if (m == null) return;
            h.Patch(m, new HarmonyMethod(typeof(AmmoDepotCargo).GetMethod("InteractionPrefix")), null, null, null, null);
            _hooked = true;
            Look(); // Emit/cache native readers during startup, before Update.
        }
        public static bool InteractionPrefix(object __instance, bool __0)
        {
            // The native owner arbitrates both native UI and our transfer.
            // Closing remains allowed; opening during removal is refused.
            return !__0 || !AmmoDepot.WorksOn(__instance);
        }
        static bool Look()
        {
            if (_looked) return _clear != null && _hooked;
            _looked = true;
            if (_container == null) return false;
            Type data = RevivalPlugin.TypeByName("ContainerData");
            _data = AccessTools.Field(_container, "_containerData");
            _ids = AccessTools.Field(data, "ItemID"); _bullets = AccessTools.Field(data, "ItemBullets");
            _clips = AccessTools.Field(data, "ClipItemID"); _conditions = AccessTools.Field(data, "ItemCondition");
            _waters = AccessTools.Field(data, "ItemWater");
            _energies = AccessTools.Field(data, "ItemEnergy");
            _busy = AccessTools.PropertyGetter(_container, "IsInteracting");
            _clear = AccessTools.Method(_container, "NeedClearContainerSlot", new Type[] { typeof(int) }, null);
            if (_data == null || _ids == null || _bullets == null || _clips == null || _conditions == null || _waters == null || _energies == null || _busy == null)
            { _clear = null; return false; }
            ReadInt = (Func<Array, int, int>)Reader(_ids.FieldType, typeof(int), typeof(Func<Array, int, int>));
            ReadFloat = (Func<Array, int, float>)Reader(_conditions.FieldType, typeof(float), typeof(Func<Array, int, float>));
            return _clear != null && _hooked;
        }
        static Delegate Reader(Type array, Type result, Type delegateType)
        {
            Type element = array.GetElementType();
            MethodInfo cast = null;
            if (element != result)
                foreach (MethodInfo m in element.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "op_Implicit" && m.ReturnType == result && p.Length == 1 && p[0].ParameterType == element) { cast = m; break; }
                }
            if (element != result && cast == null) throw new InvalidOperationException("Depot cargo conversion missing");
            DynamicMethod dm = new DynamicMethod("DepotSlot", result, new Type[] { typeof(Array), typeof(int) }, typeof(AmmoDepotCargo), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, array); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldelem, element); if (cast != null) il.Emit(OpCodes.Call, cast); il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(delegateType);
        }
        internal static void Reset() { Trucks.Clear(); Seen.Clear(); _scan = 0; }
        internal static void Sample()
        {
            if (!Look()) return;
            Component[] all = VehicleScan.All();
            // One vehicle discovery per half-second; never a scene-wide component scan.
            if (all.Length > 0)
            {
                if (_scan >= all.Length) _scan = 0;
                Component v = all[_scan++];
                if (v != null && Seen.Add(v.GetInstanceID()) && FuelBalance.ClassOf(v.transform.root) == "truck")
                {
                    Component[] containers = v.transform.root.GetComponentsInChildren(_container, true);
                    for (int i = 0; i < containers.Length; i++)
                        if (containers[i].name.StartsWith("BagaggeContainer", StringComparison.Ordinal))
                        {
                            Truck t = new Truck(); t.Root = v.transform.root; t.Container = containers[i];
                            t.View = Crocodile.ViewId(t.Container.gameObject); t.Body = t.Root.GetComponent<Rigidbody>();
                            if (t.View > 0 && ByView(t.View) == null) { t.Refresh(); Trucks.Add(t); }
                            break;
                        }
                }
            }
            for (int i = Trucks.Count - 1; i >= 0; i--)
            {
                if (Trucks[i].Root == null || Trucks[i].Container == null) { Trucks.RemoveAt(i); continue; }
                Trucks[i].Refresh();
            }
        }
        internal static Truck ByView(int view)
        { for (int i = 0; i < Trucks.Count; i++) if (Trucks[i].View == view) return Trucks[i]; return null; }
        internal static Truck Resolve(int view)
        {
            Truck known = ByView(view); if (known != null) return known;
            // A truck's native owner may be far away. The authenticated master
            // wakes that exact existing view, without a world/component scan.
            if (!Look() || view <= 0) return null;
            Type photonView = RevivalPlugin.TypeByName("PhotonView");
            MethodInfo find = photonView == null ? null : AccessTools.Method(photonView, "Find", new Type[] { typeof(int) }, null);
            Component v = find == null ? null : find.Invoke(null, new object[] { view }) as Component;
            if (v == null || FuelBalance.ClassOf(v.transform.root) != "truck") return null;
            Component container = v.gameObject.GetComponent(_container);
            if (container == null) return null;
            Truck t = new Truck(); t.Root = v.transform.root; t.Container = container; t.View = view;
            t.Body = t.Root.GetComponent<Rigidbody>(); t.Refresh(); Trucks.Add(t); return t;
        }
        internal static Truck Near(Vector3 at, float reach, int item)
        {
            Truck best = null; float distance = reach * reach;
            for (int i = 0; i < Trucks.Count; i++)
            {
                Truck t = Trucks[i]; if (!t.Parked || (item > 0 && t.Count(item) == 0)) continue;
                float d = (t.Root.position - at).sqrMagnitude;
                if (d <= distance) { distance = d; best = t; }
            }
            return best;
        }

        // Cold native loot creation, mirroring ItemsContainer.DropItemFromContainer.
        internal static GameObject Drop(DepotGood good, Vector3 at)
        {
            Type itemData = RevivalPlugin.TypeByName("ItemDataManager");
            MethodInfo cat = itemData == null ? null : AccessTools.Method(itemData, "GetItemCatData", new Type[] { typeof(int) }, null);
            if (cat == null) return null;
            object item = cat.Invoke(null, new object[] { good.Id });
            if (item == null) return null;
            // The category data owns condition, not the spawn prefab path.
            FieldInfo instance = AccessTools.Field(itemData, "Instance");
            MethodInfo byId = AccessTools.Method(itemData, "GetItemByID", new Type[] { typeof(int) }, null);
            object spawn = byId == null || instance == null ? null : byId.Invoke(instance.GetValue(null), new object[] { good.Id });
            FieldInfo path = spawn == null ? null : AccessTools.Field(spawn.GetType(), "Path");
            FieldInfo euler = spawn == null ? null : AccessTools.Field(spawn.GetType(), "EulerAngles");
            string resource = path == null ? null : path.GetValue(spawn) as string;
            if (string.IsNullOrEmpty(resource)) return null;
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            MethodInfo instantiate = photon == null ? null : AccessTools.Method(photon, "InstantiateSceneObject",
                new Type[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(byte), typeof(object[]) }, null);
            if (instantiate == null) return null;
            GameObject go = instantiate.Invoke(null, new object[] { resource, at,
                euler == null ? Quaternion.identity : Quaternion.Euler((Vector3)euler.GetValue(spawn)), (byte)0, null }) as GameObject;
            if (go == null) return null;
            try
            {
                Component spawned = go.GetComponent(RevivalPlugin.TypeByName("ItemSpawned"));
                if (spawned != null)
                {
                    MethodInfo sync = AccessTools.Method(itemData, "isWeaponParametersSync", null, null);
                    if (sync != null && (bool)sync.Invoke(null, new object[] { item }))
                        AccessTools.Method(spawned.GetType(), "SetLocalItemWeaponParameters", null, null).Invoke(spawned,
                            new object[] { good.Bullets, good.Clip, good.Condition });
                    else
                    {
                        // Medkits/toolkits also retain condition, even where the
                        // vanilla drop helper omits their category from its switch.
                        AccessTools.Method(spawned.GetType(), "SetLocalItemAdditionalParameters", null, null).Invoke(spawned,
                            new object[] { good.Energy, good.Condition });
                    }
                }
            }
            catch (Exception ex)
            {
                // The genuine item already exists. Debit it even if parameter
                // replay fails, rather than leaving both pickup and depot copy.
                RevivalPlugin.L.LogWarning("Depot loot parameter replay: " + ex.Message);
            }
            return go;
        }
    }
}

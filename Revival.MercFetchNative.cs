// Native paid crates stay the sole inventory during transport and failure.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercFetchNative
    {
        internal sealed class Crate
        {
            internal int View;
            internal Component Container, Drop;
            internal Rigidbody Body;
            internal Transform Root, PreviousParent;
            internal MercCarrier Car;
            internal Component Driver, DriverView, VehicleView;
            internal Rigidbody VehicleBody;
            internal int DriverId, Actor;
            internal string DriverKey, ExpectedKey;
            internal MercFetchJob Job;
            internal Collider[] Colliders;
            internal bool[] ColliderOn;
            internal Vector3 Scale, From;
            internal bool Kinematic, Locked;
            internal float LiftAt;
        }
        internal static readonly List<Crate> Crates = new List<Crate>(32);
        static readonly RaycastHit[] Hits = new RaycastHit[32];
        static readonly Collider[] ParkingHits = new Collider[32];
        static readonly float[] ParkingRanges = { 22.4f, 30.8f, 42f };
        static Type _containerType, _dropType, _viewType, _npcType;
        static MethodInfo _find, _busy;
        static FieldInfo _animation, _owner;
        static PropertyInfo _spawn;
        static Func<object, object> _data;
        static Func<object, Array>[] _arrays;
        static Func<Array, int, int>[] _ints;
        static Func<Array, int, float>[] _floats;
        static Action<object, int> _clear;
        internal static bool Available;
        static int _locked;

        internal static void Install(Harmony h)
        {
            try
            {
                _containerType = RevivalPlugin.TypeByName("ItemsContainer");
                _dropType = RevivalPlugin.TypeByName("AirDropObject");
                _viewType = RevivalPlugin.TypeByName("PhotonView"); _npcType = RevivalPlugin.TypeByName("NPC_AI2");
                if (_containerType == null || _dropType == null || _viewType == null || _npcType == null) return;
                _find = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
                _owner = AccessTools.Field(_viewType, "ownerId");
                _spawn = AccessTools.Property(_viewType, "instantiationData");
                _busy = AccessTools.PropertyGetter(_containerType, "IsInteracting");
                _animation = AccessTools.Field(_dropType, "animationState");
                FieldInfo data = AccessTools.Field(_containerType, "_containerData");
                _data = (Func<object, object>)MercFetchBridge.InstanceGetter(data, typeof(Func<object, object>));
                string[] names = { "ItemID", "ItemBullets", "ClipItemID", "ItemCondition", "ItemWater", "ItemEnergy" };
                _arrays = new Func<object, Array>[6]; _ints = new Func<Array, int, int>[3]; _floats = new Func<Array, int, float>[3];
                for (int i = 0; i < names.Length; i++)
                {
                    FieldInfo f = AccessTools.Field(data.FieldType, names[i]);
                    _arrays[i] = (Func<object, Array>)MercFetchBridge.InstanceGetter(f, typeof(Func<object, Array>));
                    if (i < 3) _ints[i] = (Func<Array, int, int>)Reader(f.FieldType, typeof(int), typeof(Func<Array, int, int>));
                    else _floats[i - 3] = (Func<Array, int, float>)Reader(f.FieldType, typeof(float), typeof(Func<Array, int, float>));
                }
                _clear = (Action<object, int>)MercFetchBridge.InstanceCall(AccessTools.Method(_containerType,
                    "NeedClearContainerSlot", new Type[] { typeof(int) }, null), typeof(Action<object, int>));
                if (_find == null || _owner == null || _spawn == null || _busy == null || _animation == null) return;
                h.Patch(AccessTools.Method(_containerType, "Start", Type.EmptyTypes, null), null,
                    new HarmonyMethod(typeof(MercFetchNative).GetMethod("StartPostfix")), null, null, null);
                h.Patch(AccessTools.Method(_containerType, "OnDestroy", Type.EmptyTypes, null),
                    new HarmonyMethod(typeof(MercFetchNative).GetMethod("DestroyPrefix")), null, null, null, null);
                h.Patch(AccessTools.Method(_containerType, "NetworkInteractingContainerRequest", null, null),
                    new HarmonyMethod(typeof(MercFetchNative).GetMethod("InteractionPrefix")), null, null, null, null);
                h.Patch(AccessTools.Method(_dropType, "Update", Type.EmptyTypes, null),
                    new HarmonyMethod(typeof(MercFetchNative).GetMethod("DropPrefix")), null, null, null, null);
                Available = true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercFetch native crate unavailable: " + ex.Message); }
        }

        public static void StartPostfix(Component __instance)
        {
            if (!Available || !MercFetch.Enabled || Crates.Count >= 32) return;
            try
            {
                Component view = __instance.GetComponent(_viewType);
                object[] spawn = view == null ? null : _spawn.GetValue(view, null) as object[];
                if (spawn == null || spawn.Length != 3 || !"ndr-airdrop-m4".Equals(spawn[0])) return;
                Crate c = new Crate(); c.Container = __instance; c.Root = __instance.transform;
                c.Drop = __instance.GetComponent(_dropType); c.Body = __instance.GetComponent<Rigidbody>();
                c.View = Crocodile.ViewId(__instance.gameObject);
                if (c.Drop == null || c.Body == null || c.View <= 0 || ByView(c.View) != null) return;
                c.Colliders = __instance.GetComponentsInChildren<Collider>(true); c.ColliderOn = new bool[c.Colliders.Length];
                Crates.Add(c); MercFetch.NeedSnapshot = true;
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercFetch crate discovery: " + ex.Message); }
        }
        public static void DestroyPrefix(Component __instance)
        { for (int i = Crates.Count - 1; i >= 0; i--) if (Crates[i].Container == __instance)
            { if (Crates[i].Locked) _locked = Math.Max(0, _locked - 1); Crates.RemoveAt(i); } }
        public static bool InteractionPrefix(object __instance, bool __0)
        {
            if (!__0) return true;
            for (int i = 0; i < Crates.Count; i++) if (ReferenceEquals(Crates[i].Container, __instance) && Crates[i].Locked) return false;
            return true;
        }
        public static bool DropPrefix(Component __instance)
        {
            if (_locked == 0) return true;
            FrameProf.S(FrameProf.S_MercFetchGate);
            try
            {
                for (int i = 0; i < Crates.Count; i++) if (Crates[i].Drop == __instance && Crates[i].Locked) return false;
                return true;
            }
            finally { FrameProf.E(FrameProf.S_MercFetchGate); }
        }
        internal static Crate ByView(int view)
        { for (int i = 0; i < Crates.Count; i++) if (Crates[i].View == view) return Crates[i]; return null; }

        internal static bool Target(Crate c, out Vector3 at)
        {
            at = Vector3.zero;
            if (c == null || c.Root == null || c.Body == null || c.Locked || FastCall.Bool(_busy, c.Container)
                || FastField.GetInt(_animation, c.Drop) != 4 || c.Body.velocity.sqrMagnitude >= 1f) return false;
            // Native landed state 4 becomes kinematic on sleep. Remote peers
            // are always kinematic; landing state plus ground/velocity is the gate.
            // All layers, including slabs/props/fences. Saturation refuses selection.
            int n = Physics.RaycastNonAlloc(c.Root.position + Vector3.up * 2.8f, Vector3.down, Hits, 11.2f, ~0, QueryTriggerInteraction.Ignore);
            if (n == Hits.Length) return false;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider col = Hits[i].collider;
                if (col == null || col.transform.IsChildOf(c.Root) || Hits[i].normal.y < 0.5f) continue;
                if (Hits[i].distance < best) { best = Hits[i].distance; at = Hits[i].point; }
            }
            return best < float.MaxValue && HasGoods(c);
        }
        internal static bool HasGoods(Crate c)
        {
            object data = _data(c.Container); Array ids = _arrays[0](data);
            if (ids == null) return false;
            for (int i = 0; i < ids.Length; i++) if (Good(data, i).Valid) return true;
            return false;
        }
        internal static bool Parking(Vector3 anchor, Vector3 from, out Vector3 at)
        {
            at = anchor;
            Vector3 radial = from - anchor; radial.y = 0f;
            if (radial.sqrMagnitude < 1f) radial = new Vector3(1f, 0f, 0f);
            radial = radial / Mathf.Sqrt(radial.sqrMagnitude);
            for (int k = 0; k < 6; k++)
            {
                Vector3 dir = (k & 1) == 0 ? radial : new Vector3(-radial.z, 0f, radial.x);
                Vector3 candidate = anchor + dir * ParkingRanges[k / 2];
                int n = Physics.RaycastNonAlloc(candidate + Vector3.up * 16.8f, Vector3.down, Hits, 22.4f, ~0, QueryTriggerInteraction.Ignore);
                if (n == Hits.Length) continue;
                float best = float.MaxValue; Vector3 ground = candidate;
                for (int i = 0; i < n; i++)
                    if (Hits[i].collider != null && Hits[i].normal.y > 0.6f && Mathf.Abs(Hits[i].point.y - anchor.y) <= 5.6f && Hits[i].distance < best)
                    { best = Hits[i].distance; ground = Hits[i].point; }
                if (best == float.MaxValue) continue;
                // Conservative 10 m square fits ground vehicles at any heading.
                // Include the crate itself, props, slabs, fences and every solid layer.
                int blocked = Physics.OverlapBoxNonAlloc(ground + Vector3.up * 4.76f,
                    new Vector3(14f, 4.2f, 14f), ParkingHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                if (blocked != 0) continue;
                at = ground; return true;
            }
            return false;
        }
        static MercFetchGood Good(object data, int slot)
        {
            MercFetchGood g = new MercFetchGood();
            g.Id = _ints[0](_arrays[0](data), slot); g.Bullets = _ints[1](_arrays[1](data), slot); g.Clip = _ints[2](_arrays[2](data), slot);
            g.Condition = _floats[0](_arrays[3](data), slot); g.Water = _floats[1](_arrays[4](data), slot); g.Energy = _floats[2](_arrays[5](data), slot);
            return g;
        }
        internal static bool Resolve(MercFetchJob j)
        {
            Crate c = ByView(j.Crate); if (c == null || c.Root == null) return false;
            if (c.Car != null && c.Car.View == j.Vehicle && c.DriverView != null && c.DriverId == j.Driver && c.Actor == j.Actor) return true;
            c.Car = MercRide.FetchCarrier(j.Vehicle);
            c.DriverView = _find.Invoke(null, new object[] { j.Driver }) as Component;
            c.Driver = c.DriverView == null ? null : c.DriverView.GetComponent(_npcType);
            c.VehicleView = _find.Invoke(null, new object[] { j.Vehicle }) as Component;
            c.VehicleBody = c.Car == null || c.Car.Go == null ? null : c.Car.Go.GetComponent<Rigidbody>();
            c.DriverId = j.Driver; c.Actor = j.Actor; c.DriverKey = Crew.GroundKey(c.Driver);
            c.ExpectedKey = Mercs.KeyPrefix + j.Actor + "/";
            return c.Car != null && c.Driver != null && c.VehicleView != null;
        }

        internal static int Validate(MercFetchJob j, bool loading)
        {
            Crate c = ByView(j.Crate);
            if (c != null && c.Car != null && (c.Car.Root == null || !NpcWar.PatrolVehicleAlive(c.Car.Vgs))) return MercFetchJob.VehicleLost;
            if (c == null || c.Root == null || c.Container == null) return MercFetchJob.Missing;
            if (c.Car == null || c.Car.Root == null || c.Car.Air || !NpcWar.PatrolVehicleAlive(c.Car.Vgs)) return MercFetchJob.VehicleLost;
            if (c.Driver == null || c.DriverView == null || !NpcWar.MercAlive(c.Driver) || FastField.GetInt(_owner, c.DriverView) != j.Actor) return MercFetchJob.DriverLost;
            if (c.DriverKey == null || !c.DriverKey.StartsWith(c.ExpectedKey, StringComparison.Ordinal)) return MercFetchJob.DriverLost;
            if (c.VehicleView == null || FastField.GetInt(_owner, c.VehicleView) != j.Actor) return MercFetchJob.Taken;
            if ((c.Driver.transform.position - c.Car.Root.position).sqrMagnitude > (c.Car.Radius + 12f) * (c.Car.Radius + 12f)) return MercFetchJob.DriverLost;
            if (!MercFetchBridge.DepotReady) return MercFetchJob.DepotLost;
            if (!AirfieldHold.State.ByPlayers || AirfieldHold.State.Holder < 0
                || Mortar.FactionShield.FactionOf(Crocodile.PlayerByActor(j.Actor)) != AirfieldHold.State.Holder) return MercFetchJob.WrongSide;
            if (FastCall.Bool(_busy, c.Container)) return MercFetchJob.Taken;
            if (loading)
            {
                if (c.VehicleBody == null) return MercFetchJob.VehicleLost;
                if ((c.Root.position - c.Car.Root.position).sqrMagnitude > 50.4f * 50.4f) return MercFetchJob.Missing;
                if (!Parked(c)) return MercFetchJob.Busy;
                if (!c.Locked) { Vector3 at; if (!Target(c, out at)) return MercFetchJob.Missing; }
            }
            return 0;
        }
        internal static bool Parked(Crate c)
        { return c.VehicleBody != null && c.VehicleBody.velocity.sqrMagnitude < 4f; }
        internal static bool AtDepot(MercFetchJob j)
        { Crate c = ByView(j.Crate); return c != null && c.Car != null && c.Car.Root != null && Parked(c)
            && (c.Car.Root.position - MercFetchBridge.Depot).sqrMagnitude <= 50.4f * 50.4f; }

        internal static void Apply(MercFetchJob j)
        {
            Crate c = ByView(j.Crate); if (c == null || c.Root == null || c.Body == null || c.Car == null || c.Car.Root == null) return;
            c.Job = j;
            if (!c.Locked)
            {
                c.PreviousParent = c.Root.parent; c.Kinematic = c.Body.isKinematic; c.Scale = c.Root.localScale;
                c.From = c.Root.position; c.LiftAt = Time.time; c.Locked = true; _locked++; c.Body.isKinematic = true;
                for (int i = 0; i < c.Colliders.Length; i++) if (c.Colliders[i] != null)
                { c.ColliderOn[i] = c.Colliders[i].enabled; c.Colliders[i].enabled = false; }
            }
            if (j.Phase != MercFetchJob.Loading)
            {
                c.Root.parent = c.Car.Root;
                c.Root.localPosition = new Vector3(0f, 5.6f, -4.2f);
                c.Root.localRotation = Quaternion.identity; c.Root.localScale = c.Scale * 0.35f;
            }
        }
        internal static void Animate()
        {
            for (int i = 0; i < Crates.Count; i++)
            {
                Crate c = Crates[i];
                if (!c.Locked || c.Job == null || c.Job.Phase != MercFetchJob.Loading || c.Root == null || c.Car == null || c.Car.Root == null) continue;
                float t = Mathf.Clamp01((Time.time - c.LiftAt) / 4f); t = t * t * (3f - 2f * t);
                c.Root.position = Vector3.Lerp(c.From, c.Car.Root.TransformPoint(new Vector3(0f, 5.6f, -4.2f)), t);
                c.Root.localScale = c.Scale * Mathf.Lerp(1f, 0.35f, t);
            }
        }
        internal static void Release(MercFetchJob j)
        {
            Crate c = ByView(j.Crate); if (c == null || !c.Locked) return;
            if (c.Root != null)
            {
                c.Root.parent = c.PreviousParent; c.Root.localScale = c.Scale;
                // Existing crate falls to genuine ground; no inventory is synthesized.
                for (int i = 0; i < c.Colliders.Length; i++) if (c.Colliders[i] != null) c.Colliders[i].enabled = c.ColliderOn[i];
            }
            if (c.Body != null) c.Body.isKinematic = c.Kinematic;
            c.Locked = false; _locked = Math.Max(0, _locked - 1); c.Job = null;
        }
        internal static int UnloadOne(MercFetchJob j)
        {
            if (!Crocodile.IsMaster()) return -MercFetchJob.Taken;
            Crate c = ByView(j.Crate); if (c == null || c.Container == null) return -MercFetchJob.Missing;
            if (MercFetchBridge.DepotFull) return -MercFetchJob.Full;
            if (MercFetchBridge.DepotBusy) return -MercFetchJob.Busy;
            object data = _data(c.Container); Array ids = _arrays[0](data);
            if (ids == null) return -MercFetchJob.Missing;
            for (int i = 0; i < ids.Length; i++)
            {
                MercFetchGood good = Good(data, i); if (!good.Valid) continue;
                _clear(c.Container, i);
                if (_ints[0](ids, i) != 0) return -MercFetchJob.Taken;
                // Master-only, serialized frame: capacity/readiness checked above;
                // M2 Add changes only finite stock and broadcasts its own snapshot.
                if (!MercFetchBridge.Deposit(good)) throw new InvalidOperationException("Depot changed during serialized transfer");
                return 1;
            }
            return 0;
        }
        static Delegate Reader(Type array, Type result, Type type)
        {
            Type element = array.GetElementType(); MethodInfo cast = null;
            if (element != result)
                foreach (MethodInfo m in element.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (m.Name == "op_Implicit" && m.ReturnType == result && p.Length == 1 && p[0].ParameterType == element) { cast = m; break; }
                }
            if (element != result && cast == null) throw new InvalidOperationException("Native cargo conversion unavailable");
            DynamicMethod dm = new DynamicMethod("FetchSlot", result, new Type[] { typeof(Array), typeof(int) }, typeof(MercFetchNative), true);
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, array);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldelem, element); if (cast != null) il.Emit(OpCodes.Call, cast); il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(type);
        }
    }

    internal static partial class MercRide
    {
        internal static MercCarrier FetchCarrier(int view) { return CarrierByView(MercCarrier.Ground, view); }
        public static void FetchEventPostfix(byte code, object content, int sender)
        {
            if (code != (byte)Code()) return;
            try { MercFetch.OnPacket(content as float[], sender); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("MercFetch packet refused: " + ex.Message); }
        }
    }
}

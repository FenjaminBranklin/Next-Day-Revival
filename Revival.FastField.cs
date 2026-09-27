// Next Day: Survival - Revival Toolkit
//
// FastField - allocation-free reads and writes of float/bool game fields.
//
// The plugin references no Assembly-CSharp, so every game field goes through
// FieldInfo.GetValue/SetValue - and each of those BOXES the value: a fresh heap
// object per read and per write. Patrol's driver does about a dozen of them per
// vehicle per physics step (fuel, durability, pedals, engine flag), two to four
// physics steps per frame at low frame rates. That is a steady stream of tiny
// garbage, and Unity 2018's Boehm collector pays for garbage in one stop-the-
// world pause whose length grows with the heap - the long frames in the F6
// overlay (Q1 perf).
//
// A DynamicMethod per field compiles "((T)o).field" once; after that a read or
// a write is a delegate call with no allocation. Anything unusual (a value-type
// owner, a static field, an emit failure) falls back to plain reflection, so the
// result is always the same as before - only cheaper.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. UTF-8 (no BOM), compiled with /codepage:65001.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace NextDayRevival
{
    public static class FastField
    {
        delegate float GetF(object o);
        delegate void SetF(object o, float v);
        delegate bool GetB(object o);
        delegate void SetB(object o, bool v);

        static readonly Dictionary<FieldInfo, GetF> _getF = new Dictionary<FieldInfo, GetF>();
        static readonly Dictionary<FieldInfo, SetF> _setF = new Dictionary<FieldInfo, SetF>();
        static readonly Dictionary<FieldInfo, GetB> _getB = new Dictionary<FieldInfo, GetB>();
        static readonly Dictionary<FieldInfo, SetB> _setB = new Dictionary<FieldInfo, SetB>();
        static bool _emitBroken;
        static readonly Dictionary<Type, Dictionary<string, FieldInfo>> _fields =
            new Dictionary<Type, Dictionary<string, FieldInfo>>();

        /// <summary>AccessTools.Field, remembered per (type, name) without
        /// building a string key. A missing field is remembered as null.</summary>
        public static FieldInfo Find(Type t, string name)
        {
            if (t == null) return null;
            Dictionary<string, FieldInfo> byName;
            if (!_fields.TryGetValue(t, out byName))
            {
                byName = new Dictionary<string, FieldInfo>();
                _fields[t] = byName;
            }
            FieldInfo fi;
            if (byName.TryGetValue(name, out fi)) return fi;
            fi = HarmonyLib.AccessTools.Field(t, name);
            byName[name] = fi;
            return fi;
        }

        public static float GetFloat(FieldInfo fi, object o)
        {
            GetF d;
            if (!_getF.TryGetValue(fi, out d)) { d = (GetF)Getter(fi, typeof(float), typeof(GetF)); _getF[fi] = d; }
            return d != null ? d(o) : (float)fi.GetValue(o);
        }

        public static void SetFloat(FieldInfo fi, object o, float v)
        {
            SetF d;
            if (!_setF.TryGetValue(fi, out d)) { d = (SetF)Setter(fi, typeof(float), typeof(SetF)); _setF[fi] = d; }
            if (d != null) d(o, v); else fi.SetValue(o, v);
        }

        public static bool GetBool(FieldInfo fi, object o)
        {
            GetB d;
            if (!_getB.TryGetValue(fi, out d)) { d = (GetB)Getter(fi, typeof(bool), typeof(GetB)); _getB[fi] = d; }
            return d != null ? d(o) : (bool)fi.GetValue(o);
        }

        public static void SetBool(FieldInfo fi, object o, bool v)
        {
            SetB d;
            if (!_setB.TryGetValue(fi, out d)) { d = (SetB)Setter(fi, typeof(bool), typeof(SetB)); _setB[fi] = d; }
            if (d != null) d(o, v); else fi.SetValue(o, v);
        }

        static bool Emittable(FieldInfo fi, Type want)
        {
            return !_emitBroken && fi != null && !fi.IsStatic && fi.FieldType == want
                && fi.DeclaringType != null && !fi.DeclaringType.IsValueType;
        }

        static Delegate Getter(FieldInfo fi, Type want, Type del)
        {
            if (!Emittable(fi, want)) return null;
            try
            {
                DynamicMethod dm = new DynamicMethod("ndr_get_" + fi.Name, want,
                    new Type[] { typeof(object) }, fi.DeclaringType, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, fi.DeclaringType);
                il.Emit(OpCodes.Ldfld, fi);
                il.Emit(OpCodes.Ret);
                return dm.CreateDelegate(del);
            }
            catch (Exception ex) { Broken(ex); return null; }
        }

        static Delegate Setter(FieldInfo fi, Type want, Type del)
        {
            if (!Emittable(fi, want)) return null;
            try
            {
                DynamicMethod dm = new DynamicMethod("ndr_set_" + fi.Name, typeof(void),
                    new Type[] { typeof(object), want }, fi.DeclaringType, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, fi.DeclaringType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, fi);
                il.Emit(OpCodes.Ret);
                return dm.CreateDelegate(del);
            }
            catch (Exception ex) { Broken(ex); return null; }
        }

        static void Broken(Exception ex)
        {
            // One failure means this runtime cannot emit; stop trying and use
            // reflection everywhere (same results, the old cost).
            _emitBroken = true;
            RevivalPlugin.L.LogWarning("FastField: DynamicMethod unavailable, reflection fallback: " + ex.Message);
        }
    }
}

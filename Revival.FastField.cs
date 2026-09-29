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
        delegate int GetI(object o);

        static readonly Dictionary<FieldInfo, GetF> _getF = new Dictionary<FieldInfo, GetF>();
        static readonly Dictionary<FieldInfo, SetF> _setF = new Dictionary<FieldInfo, SetF>();
        static readonly Dictionary<FieldInfo, GetB> _getB = new Dictionary<FieldInfo, GetB>();
        static readonly Dictionary<FieldInfo, SetB> _setB = new Dictionary<FieldInfo, SetB>();
        static readonly Dictionary<FieldInfo, GetI> _getI = new Dictionary<FieldInfo, GetI>();
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

        /// <summary>An int or int-backed enum field, read without boxing
        /// (n01 perf: NpcWar's behaviour enum). Same value as
        /// Convert.ToInt32(fi.GetValue(o)).</summary>
        public static int GetInt(FieldInfo fi, object o)
        {
            GetI d;
            if (!_getI.TryGetValue(fi, out d))
            {
                Type ft = fi.FieldType;
                bool plain = ft == typeof(int) || (ft.IsEnum && Enum.GetUnderlyingType(ft) == typeof(int));
                d = plain && Emittable(fi, ft) ? IntGetter(fi) : null;
                _getI[fi] = d;
            }
            return d != null ? d(o) : Convert.ToInt32(fi.GetValue(o));
        }

        static GetI IntGetter(FieldInfo fi)
        {
            try
            {
                // An int-backed enum is an int32 on the evaluation stack.
                DynamicMethod dm = new DynamicMethod("ndr_geti_" + fi.Name, typeof(int),
                    new Type[] { typeof(object) }, fi.DeclaringType, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, fi.DeclaringType);
                il.Emit(OpCodes.Ldfld, fi);
                il.Emit(OpCodes.Ret);
                return (GetI)dm.CreateDelegate(typeof(GetI));
            }
            catch (Exception ex) { Broken(ex); return null; }
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

        internal static bool EmitBroken { get { return _emitBroken; } }

        internal static void Broken(Exception ex)
        {
            // One failure means this runtime cannot emit; stop trying and use
            // reflection everywhere (same results, the old cost).
            if (_emitBroken) return;
            _emitBroken = true;
            RevivalPlugin.L.LogWarning("FastField: DynamicMethod unavailable, reflection fallback: " + ex.Message);
        }
    }

    /// <summary>
    /// n01 perf: allocation-free calls of game methods found by reflection.
    /// MethodInfo.Invoke boxes a value-type result (IsAlive, isMine,
    /// isMasterClient, a clip's length) and a call with arguments needs a
    /// fresh object[]; NpcWar alone made hundreds of such calls a frame
    /// (24.8 KB/frame in Kevin's 6.59.0 F6). A DynamicMethod per MethodInfo
    /// compiles the call once. A static method ignores the target. Anything
    /// the emitter does not cover (a value-type owner, other parameters, an
    /// emit failure) goes through Invoke exactly as before.
    /// </summary>
    public static class FastCall
    {
        delegate bool CallB(object o);
        delegate float CallF(object o);
        delegate UnityEngine.Vector3 CallV(object o);
        delegate void CallOF(object o, object a, float b);
        delegate void Call0(object o);
        delegate void CallF1(object o, float a);

        static readonly Dictionary<MethodInfo, CallB> _b = new Dictionary<MethodInfo, CallB>();
        static readonly Dictionary<MethodInfo, CallF> _f = new Dictionary<MethodInfo, CallF>();
        static readonly Dictionary<MethodInfo, CallV> _v = new Dictionary<MethodInfo, CallV>();
        static readonly Dictionary<MethodInfo, CallOF> _of = new Dictionary<MethodInfo, CallOF>();
        static readonly Dictionary<MethodInfo, Call0> _v0 = new Dictionary<MethodInfo, Call0>();
        static readonly Dictionary<MethodInfo, CallF1> _vf = new Dictionary<MethodInfo, CallF1>();
        static readonly object[] _oneArg = new object[1];

        /// <summary>A bool method (or property getter) without arguments; the
        /// same as <c>r = m.Invoke(o, null); r is bool &amp;&amp; (bool)r</c>.</summary>
        public static bool Bool(MethodInfo m, object o)
        {
            CallB d;
            if (!_b.TryGetValue(m, out d)) { d = (CallB)Emit(m, typeof(bool), null, typeof(CallB)); _b[m] = d; }
            if (d != null)
            {
                try { return d(o); }
                catch (Exception ex) { if (!Unusable(ex)) throw; _b[m] = null; }
            }
            object r = m.Invoke(o, null);
            return r is bool && (bool)r;
        }

        /// <summary>A float method (or property getter) without arguments.</summary>
        public static float Float(MethodInfo m, object o)
        {
            CallF d;
            if (!_f.TryGetValue(m, out d)) { d = (CallF)Emit(m, typeof(float), null, typeof(CallF)); _f[m] = d; }
            if (d != null)
            {
                try { return d(o); }
                catch (Exception ex) { if (!Unusable(ex)) throw; _f[m] = null; }
            }
            object r = m.Invoke(o, null);
            return r is float ? (float)r : 0f;
        }

        /// <summary>A Vector3 method (or property getter) without arguments.</summary>
        public static UnityEngine.Vector3 Vector3(MethodInfo m, object o)
        {
            CallV d;
            if (!_v.TryGetValue(m, out d)) { d = (CallV)Emit(m, typeof(UnityEngine.Vector3), null, typeof(CallV)); _v[m] = d; }
            if (d != null)
            {
                try { return d(o); }
                catch (Exception ex) { if (!Unusable(ex)) throw; _v[m] = null; }
            }
            object r = m.Invoke(o, null);
            return r is UnityEngine.Vector3 ? (UnityEngine.Vector3)r : UnityEngine.Vector3.zero;
        }

        /// <summary>A void method taking (reference type, float), e.g.
        /// AnimationClip.SampleAnimation(GameObject, float).</summary>
        public static void ObjFloat(MethodInfo m, object o, object a, float b)
        {
            CallOF d;
            if (!_of.TryGetValue(m, out d)) { d = (CallOF)Emit(m, typeof(void), typeof(float), typeof(CallOF)); _of[m] = d; }
            if (d != null)
            {
                try { d(o, a, b); return; }
                catch (Exception ex) { if (!Unusable(ex)) throw; _of[m] = null; }
            }
            m.Invoke(o, new object[] { a, b });
        }

        /// <summary>A void method without arguments (P1b: the crews' per-frame
        /// ClearIntentions); the same as <c>m.Invoke(o, null)</c>.</summary>
        public static void Void(MethodInfo m, object o)
        {
            Call0 d;
            if (!_v0.TryGetValue(m, out d)) { d = (Call0)EmitOne(m, null, typeof(Call0)); _v0[m] = d; }
            if (d != null)
            {
                try { d(o); return; }
                catch (Exception ex) { if (!Unusable(ex)) throw; _v0[m] = null; }
            }
            m.Invoke(o, null);
        }

        /// <summary>A void method taking one float (P1b: the crews' per-frame
        /// SetCalculatedPauseTime); the same as <c>m.Invoke(o, new object[] { a })</c>.</summary>
        public static void VoidFloat(MethodInfo m, object o, float a)
        {
            CallF1 d;
            if (!_vf.TryGetValue(m, out d)) { d = (CallF1)EmitOne(m, typeof(float), typeof(CallF1)); _vf[m] = d; }
            if (d != null)
            {
                try { d(o, a); return; }
                catch (Exception ex) { if (!Unusable(ex)) throw; _vf[m] = null; }
            }
            _oneArg[0] = a;
            m.Invoke(o, _oneArg);
        }

        /// <summary>Compiles a void call with no argument (<paramref name="arg"/>
        /// null) or one argument of exactly that value type; null when it cannot.</summary>
        static Delegate EmitOne(MethodInfo m, Type arg, Type del)
        {
            if (FastField.EmitBroken || m == null || m.ReturnType != typeof(void) || m.IsGenericMethodDefinition) return null;
            Type owner = m.DeclaringType;
            if (owner == null || (!m.IsStatic && owner.IsValueType)) return null;
            ParameterInfo[] ps = m.GetParameters();
            if (arg == null ? ps.Length != 0 : (ps.Length != 1 || ps[0].ParameterType != arg)) return null;
            try
            {
                Type[] args = arg == null ? new Type[] { typeof(object) } : new Type[] { typeof(object), arg };
                DynamicMethod dm = new DynamicMethod("ndr_call_" + m.Name, typeof(void), args, owner, true);
                ILGenerator il = dm.GetILGenerator();
                if (!m.IsStatic)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, owner);
                }
                if (arg != null) il.Emit(OpCodes.Ldarg_1);
                il.Emit(m.IsStatic ? OpCodes.Call : OpCodes.Callvirt, m);
                il.Emit(OpCodes.Ret);
                return dm.CreateDelegate(del);
            }
            catch (Exception ex) { FastField.Broken(ex); return null; }
        }

        /// <summary>The compiled call itself failed (the runtime refused the
        /// IL or the access) - not an exception of the game method: that
        /// MethodInfo goes back to Invoke for good.</summary>
        static bool Unusable(Exception ex)
        {
            bool bad = ex is InvalidProgramException || ex is MethodAccessException
                || ex is FieldAccessException || ex is System.Security.VerificationException
                || ex is TypeLoadException;
            if (bad) RevivalPlugin.L.LogWarning("FastCall: compiled call refused, reflection fallback: " + ex.Message);
            return bad;
        }

        /// <summary>Compiles the call; null when it cannot (the caller then
        /// uses Invoke). <paramref name="second"/> null: no arguments; else the
        /// method takes (reference type, second).</summary>
        static Delegate Emit(MethodInfo m, Type ret, Type second, Type del)
        {
            if (FastField.EmitBroken || m == null || m.ReturnType != ret || m.IsGenericMethodDefinition) return null;
            Type owner = m.DeclaringType;
            if (owner == null || (!m.IsStatic && owner.IsValueType)) return null;
            ParameterInfo[] ps = m.GetParameters();
            if (second == null ? ps.Length != 0
                : (ps.Length != 2 || ps[0].ParameterType.IsValueType || ps[1].ParameterType != second)) return null;
            try
            {
                Type[] args = second == null ? new Type[] { typeof(object) }
                    : new Type[] { typeof(object), typeof(object), second };
                DynamicMethod dm = new DynamicMethod("ndr_call_" + m.Name, ret, args, owner, true);
                ILGenerator il = dm.GetILGenerator();
                if (!m.IsStatic)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, owner);
                }
                if (second != null)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Castclass, ps[0].ParameterType);
                    il.Emit(OpCodes.Ldarg_2);
                }
                il.Emit(m.IsStatic ? OpCodes.Call : OpCodes.Callvirt, m);
                il.Emit(OpCodes.Ret);
                return dm.CreateDelegate(del);
            }
            catch (Exception ex) { FastField.Broken(ex); return null; }
        }
    }
}

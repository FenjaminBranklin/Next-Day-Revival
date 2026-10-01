// Z M5a: cached native vehicle controls; cold binding only, no hot reflection boxing.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercDriveNative
    {
        internal Component Vgs, Rcc, View;
        internal Rigidbody Body;
        internal bool Enabled;
        internal Vector3 Half, Centre;
        FieldInfo _gas, _brake, _steer, _hand, _ai, _reverse, _stall, _gear, _engine;
        MethodInfo _main, _extra, _mine, _owner, _wake, _enable, _transfer;
        bool _wasAi, _wasReverse, _wasStall, _wasGear, _wasEngine;

        internal static MercDriveNative Bind(MercCarrier c)
        {
            if (c == null || c.Vgs == null || c.Go == null) return null;
            MercDriveNative n = new MercDriveNative(); n.Vgs = c.Vgs;
            Type v = c.Vgs.GetType();
            FieldInfo rcc = FastField.Find(v, "_carController"), rb = FastField.Find(v, "_rigidbody");
            n.Rcc = rcc == null ? null : rcc.GetValue(c.Vgs) as Component;
            n.Body = rb == null ? null : rb.GetValue(c.Vgs) as Rigidbody;
            Type view = RevivalPlugin.TypeByName("PhotonView");
            n.View = view == null ? null : c.Go.GetComponent(view);
            if (n.Rcc == null || n.Body == null || n.View == null) return null;
            Type r = n.Rcc.GetType();
            n._gas = FastField.Find(r, "gasInput"); n._brake = FastField.Find(r, "brakeInput");
            n._steer = FastField.Find(r, "steerInput"); n._hand = FastField.Find(r, "handbrakeInput");
            n._ai = FastField.Find(r, "AIController"); n._reverse = FastField.Find(r, "autoReverse");
            n._stall = FastField.Find(r, "canEngineStall"); n._gear = FastField.Find(r, "automaticGear");
            n._engine = FastField.Find(r, "engineRunning");
            n._main = AccessTools.Method(v, "CheckMainPartsReady", Type.EmptyTypes, null);
            n._extra = AccessTools.Method(v, "CheckAdditionalPartsReady", Type.EmptyTypes, null);
            n._wake = AccessTools.Method(v, "SetSleepModeEnabled", new Type[] { typeof(bool) }, null);
            n._enable = AccessTools.Method(v, "EnablePhys", Type.EmptyTypes, null);
            n._transfer = AccessTools.Method(view, "TransferOwnership", new Type[] { typeof(int) }, null);
            n._mine = AccessTools.PropertyGetter(view, "isMine");
            n._owner = AccessTools.PropertyGetter(view, "ownerId");
            if (n._gas == null || n._brake == null || n._steer == null || n._hand == null
                || n._ai == null || n._reverse == null || n._stall == null || n._gear == null
                || n._engine == null || n._main == null || n._extra == null || n._mine == null
                || n._owner == null || n._wake == null || n._enable == null || n._transfer == null) return null;
            n.Shape(c);
            return n;
        }

        internal bool Mine { get { return View != null && FastCall.Bool(_mine, View); } }
        internal int Owner { get { return View == null ? -1 : FastCall.Int(_owner, View); } }
        internal bool Ready { get { return Vgs != null && FastCall.Bool(_main, Vgs) && FastCall.Bool(_extra, Vgs); } }
        internal void Transfer(int actor) { _transfer.Invoke(View, new object[] { actor }); }

        void Shape(MercCarrier c)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            bool any = false;
            // All solid hull colliders contribute; never size a truck from its cab only.
            for (int i = 0; i < c.Cols.Length; i++)
            {
                Collider col = c.Cols[i]; if (col == null || col.isTrigger || !col.enabled) continue;
                Bounds b = col.bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = c.Root.InverseTransformPoint(b.center + Vector3.Scale(b.extents,
                        new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
                }
            }
            if (!any) { min = new Vector3(-3f, 0f, -5f); max = new Vector3(3f, 5f, 5f); }
            // Probe the body above its ground-contact band; walking ground normals
            // are ignored, but slabs, fences, props and other hulls stay solid.
            min.y += 0.8f;
            Centre = (min + max) * 0.5f; Half = (max - min) * 0.5f;
            Half.x = Mathf.Max(0.8f, Half.x); Half.y = Mathf.Max(0.5f, Half.y); Half.z = Mathf.Max(1.5f, Half.z);
            Vector3 scale = c.Root.lossyScale;
            Half = Vector3.Scale(Half, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        }

        internal void Start()
        {
            if (Enabled || !Mine || !Ready) return;
            _wasAi = FastField.GetBool(_ai, Rcc); _wasReverse = FastField.GetBool(_reverse, Rcc);
            _wasStall = FastField.GetBool(_stall, Rcc); _wasGear = FastField.GetBool(_gear, Rcc);
            _wasEngine = FastField.GetBool(_engine, Rcc);
            Enabled = true;
            FastField.SetBool(_ai, Rcc, true); FastField.SetBool(_reverse, Rcc, true);
            FastField.SetBool(_stall, Rcc, false); FastField.SetBool(_gear, Rcc, true);
            // Native wake turns sync on for peers; do not replace it with EnablePhys.
            _wake.Invoke(Vgs, new object[] { false });
            // Already-awake remote hulls need their wheels enabled after the
            // ownership transfer too: native wake returns early if unchanged.
            FastCall.Void(_enable, Vgs);
            FastField.SetBool(_engine, Rcc, true);
        }

        internal void Inputs(float gas, float brake, float steer, float hand)
        {
            if (!Enabled || Rcc == null || !Mine) return;
            FastField.SetFloat(_gas, Rcc, gas); FastField.SetFloat(_brake, Rcc, brake);
            FastField.SetFloat(_steer, Rcc, steer); FastField.SetFloat(_hand, Rcc, hand);
        }

        internal void Stop(bool playerDriver)
        {
            if (!Enabled) return;
            Inputs(0f, 0f, 0f, playerDriver ? 0f : 1f);
            if (Rcc != null)
            {
                FastField.SetBool(_ai, Rcc, _wasAi); FastField.SetBool(_reverse, Rcc, _wasReverse);
                FastField.SetBool(_stall, Rcc, _wasStall); FastField.SetBool(_gear, Rcc, _wasGear);
                if (!playerDriver) FastField.SetBool(_engine, Rcc, _wasEngine);
            }
            Enabled = false;
        }

        internal void Park()
        {
            if (!Mine || Rcc == null) return;
            FastField.SetFloat(_gas, Rcc, 0f); FastField.SetFloat(_brake, Rcc, 0f);
            FastField.SetFloat(_steer, Rcc, 0f); FastField.SetFloat(_hand, Rcc, 1f);
            FastField.SetBool(_engine, Rcc, false); FastField.SetBool(_ai, Rcc, false);
        }
    }
}

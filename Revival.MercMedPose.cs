// Y S5: native legacy medkit clips, upper body over the NPC crouch pose.
// Event 199 kind 4: owner/view/item/serial/elapsed. Health uses the stock RPC.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public sealed class MercMedPose : MonoBehaviour
    {
        const string PoseName = "ndr_merc_medkit";
        static AnimationClip _soviet, _military;
        static bool _clipsTried;
        static Type _viewType;
        static MethodInfo _findView, _getViewId;
        static FieldInfo _ownerId;
        static readonly Dictionary<int, MercMedPose> Remote = new Dictionary<int, MercMedPose>();
        Animation _anim;
        AnimationState _state;
        Transform _spine;
        Component _ai;
        Component _photonView;
        int _view, _item, _serial;
        float _started, _sendAt;
        bool _local, _active, _warned;
        readonly float[] _packet = new float[6]; // reused while active (1 Hz)

        static void Clips()
        {
            if (_clipsTried) return;
            _clipsTried = true;
            // One cold lookup per game process, no loading custom art/assets.
            UnityEngine.Object[] clips = Resources.FindObjectsOfTypeAll(typeof(AnimationClip));
            for (int i = 0; i < clips.Length; i++)
            {
                AnimationClip c = clips[i] as AnimationClip;
                if (c == null) continue;
                if (c.name == "use_soviet_medkit") _soviet = c;
                if (c.name == "use_military_medkit") _military = c;
            }
        }
        static void ViewLook()
        {
            if (_viewType != null) return;
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_viewType == null) return;
            _findView = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            _getViewId = AccessTools.PropertyGetter(_viewType, "viewID");
            _ownerId = AccessTools.Field(_viewType, "ownerId");
        }
        static MercMedPose Of(Component ai)
        {
            if (ai == null) return null;
            MercMedPose p = ai.GetComponent<MercMedPose>();
            if (p != null) return p;
            p = ai.gameObject.AddComponent<MercMedPose>();
            p._ai = ai;
            p._anim = ai.GetComponentInChildren<Animation>();
            Transform[] bones = ai.GetComponentsInChildren<Transform>();
            for (int i = 0; i < bones.Length; i++)
                if (bones[i].name.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0)
                { p._spine = bones[i]; break; }
            ViewLook();
            Component v = _viewType == null ? null : ai.GetComponent(_viewType);
            if (v != null && _getViewId != null) p._view = Convert.ToInt32(_getViewId.Invoke(v, null));
            return p;
        }

        internal static void Start(Component ai, int item, float now)
        {
            MercMedPose p = Of(ai);
            if (p == null) return;
            p._local = true; p._serial++; p.Apply(item, now); p.Publish(now);
        }
        internal static void Stop(Component ai)
        {
            if (ai == null) return;
            MercMedPose p = ai.GetComponent<MercMedPose>();
            if (p == null || !p._active) return;
            p._serial++; p.Clear();
            if (p._local) p.Publish(Time.time);
        }
        void Apply(int item, float started)
        {
            _item = item; _started = started; _active = item != 0;
            if (!_active) { Clear(); return; }
            Clips();
            AnimationClip clip = item < 7013 ? _soviet : _military;
            if (_anim == null || _spine == null || clip == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("Merc medkit: native clip/spine unavailable; crouch and heal timer retained.");
                }
                return;
            }
            // Change only on start/item change; cache AnimationState afterward.
            if (_state != null) _state.enabled = false;
            _anim.RemoveClip(PoseName);
            _anim.AddClip(clip, PoseName);
            _state = _anim[PoseName];
            _state.layer = 8; _state.wrapMode = WrapMode.ClampForever;
            _state.AddMixingTransform(_spine, true);
            _state.speed = 0f;
        }
        void Clear()
        {
            _active = false; _item = 0;
            if (_state != null) { _state.enabled = false; _state.weight = 0f; }
        }
        void OnDestroy()
        {
            MercMedPose p;
            if (Remote.TryGetValue(_view, out p) && p == this) Remote.Remove(_view);
        }
        void Publish(float now)
        {
            if (_view <= 0) return;
            _packet[0] = 4f; _packet[1] = _view; _packet[2] = _item;
            _packet[3] = _serial; _packet[4] = _active ? now - _started : 0f;
            _packet[5] = 1f;
            MercRide.SendAAPacket(_packet);
            _sendAt = now + 1f;
        }
        void LateUpdate()
        {
            if (!_active) return;
            FrameProf.S(FrameProf.S_MercMedPose_LateUpdate);
            try
            {
                float now = Time.time, elapsed = now - _started;
                // Silence/death/owner logout never leaves a remote pose stuck.
                if (_ai == null || elapsed > MercMedicine.Seconds(_item) + 2f) { Clear(); return; }
                if (_state != null)
                {
                    _state.enabled = true; _state.weight = 1f;
                    _state.time = Mathf.Min(elapsed, _state.length);
                }
                if (_local && now >= _sendAt) Publish(now);
            }
            finally { FrameProf.E(FrameProf.S_MercMedPose_LateUpdate); }
        }
        internal static void OnPacket(float[] d, int sender)
        {
            if (d.Length != 6 || d[5] != 1f) return;
            for (int i = 0; i < d.Length; i++) if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return;
            int view = Mathf.RoundToInt(d[1]), item = Mathf.RoundToInt(d[2]), serial = Mathf.RoundToInt(d[3]);
            if (view <= 0 || serial <= 0 || (item != 0 && !MercMedicine.Item(item))
                || d[4] < 0f || d[4] > 26f) return;
            ViewLook();
            if (_findView == null || _ownerId == null) return;
            // After the first packet: no reflection call, component search,
            // boxed view id or key string on the 1 Hz heartbeat.
            MercMedPose cached;
            if (Remote.TryGetValue(view, out cached) && cached != null && cached._photonView != null)
            {
                if (FastField.GetInt(_ownerId, cached._photonView) != sender || serial < cached._serial) return;
                if (serial == cached._serial && cached._active) return;
                cached._serial = serial; cached.Apply(item, Time.time - d[4]); return;
            }
            if (item == 0) return;
            Component v = _findView.Invoke(null, new object[] { view }) as Component;
            if (v == null || FastField.GetInt(_ownerId, v) != sender) return;
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            Component ai = npc == null ? null : v.GetComponent(npc);
            if (ai == null) return;
            string key = Crew.GroundKey(ai);
            if (key == null || !key.StartsWith(Mercs.KeyPrefix + sender + "/", StringComparison.Ordinal)) return;
            MercMedPose p = Of(ai);
            if (p == null || p._local || serial < p._serial) return;
            p._photonView = v; Remote[view] = p;
            if (serial == p._serial && p._active) return; // no animation restart on heartbeat
            p._serial = serial; p.Apply(item, Time.time - d[4]);
        }
    }
}

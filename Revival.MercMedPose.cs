// Native Soviet chest layer / military body action, fitted to the kit timer.
// Event 199 kind 4 carries duration; health still uses the stock RPC.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public sealed class MercMedPose : MonoBehaviour
    {
        const string SovietName = "ndr_merc_medkit_soviet", MilitaryName = "ndr_merc_medkit_military";
        const float Fade = 0.15f, Lease = 2.5f;
        static AnimationClip _soviet, _military;
        static bool _clipsTried;
        static Type _viewType;
        static MethodInfo _findView, _getViewId;
        static FieldInfo _ownerId;
        static FieldInfo _animField, _spineField, _chestField, _ikField;
        static readonly Dictionary<int, MercMedPose> Remote = new Dictionary<int, MercMedPose>();
        Animation _anim;
        AnimationState _state;
        Transform _chest, _hips, _hand;
        AnimationState _sovietState, _militaryState;
        Behaviour _ik;
        object _solver;
        FieldInfo _weight;
        MercMoveShootPose _movePose;
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly List<Renderer> _holstered = new List<Renderer>();
        Component _ai;
        Component _photonView;
        int _view, _item, _serial;
        float _started, _seconds, _sendAt, _received, _nextHolster;
        bool _local, _active, _warned, _rigOwned, _ikEnabled;
        readonly float[] _packet = new float[7]; // reused while active (1 Hz)
        internal bool Active { get { return _active; } }

        internal void BindMovement(MercMoveShootPose pose)
        { _movePose = pose; pose.Medical(_active); }

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
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            if (npc == null) return;
            _animField = AccessTools.Field(npc, "Anim");
            _spineField = AccessTools.Field(npc, "_mainCharSpine");
            _chestField = AccessTools.Field(npc, "MainChar_\u0421hest");
            _ikField = AccessTools.Field(npc, "_aimIk");
        }
        static MercMedPose Of(Component ai)
        {
            if (ai == null) return null;
            MercMedPose p = ai.GetComponent<MercMedPose>();
            if (p != null) return p;
            p = ai.gameObject.AddComponent<MercMedPose>();
            p._ai = ai;
            ViewLook();
            p._anim = _animField == null ? null : _animField.GetValue(ai) as Animation;
            Transform spine = _spineField == null ? null : _spineField.GetValue(ai) as Transform;
            p._hips = spine == null ? null : spine.parent;
            p._chest = _chestField == null ? null : _chestField.GetValue(ai) as Transform;
            p._ik = _ikField == null ? null : _ikField.GetValue(ai) as Behaviour;
            if (p._ik != null)
            {
                FieldInfo solver = AccessTools.Field(p._ik.GetType(), "solver");
                p._solver = solver == null ? null : solver.GetValue(p._ik);
                p._weight = p._solver == null ? null : AccessTools.Field(p._solver.GetType(), "IKPositionWeight");
            }
            p._movePose = ai.GetComponent<MercMoveShootPose>();
            Component v = _viewType == null ? null : ai.GetComponent(_viewType);
            if (v != null && _getViewId != null) p._view = Convert.ToInt32(_getViewId.Invoke(v, null));
            p.enabled = false;
            return p;
        }

        internal static void Start(Component ai, int item, float now)
        { Start(ai, item, now, MercMedicine.Seconds(item)); }
        internal static void Start(Component ai, int item, float now, float seconds)
        {
            if (!MercMedicine.Item(item) || MercDownPose.Down(ai)) return;
            MercMedPose p = Of(ai);
            if (p == null) return;
            p._local = true; p._serial++; p.Apply(item, now, seconds); p.Publish(now);
        }
        internal static void Stop(Component ai)
        {
            if (ai == null) return;
            MercMedPose p = ai.GetComponent<MercMedPose>();
            if (p == null || !p._active) return;
            p.End(Time.time);
        }
        void End(float now)
        {
            if (!_active) return;
            if (_local) _serial++;
            Clear();
            if (_local) Publish(now);
        }
        AnimationState State(int item)
        {
            bool soviet = item < 7013;
            AnimationState state = soviet ? _sovietState : _militaryState;
            if (state != null) return state;
            Clips();
            AnimationClip clip = soviet ? _soviet : _military;
            Transform mask = soviet ? _chest : _hips;
            if (_anim == null || mask == null || clip == null) return null;
            string name = soviet ? SovietName : MilitaryName;
            _anim.AddClip(clip, name);
            state = _anim[name];
            state.layer = 8; state.wrapMode = WrapMode.ClampForever;
            // Military action includes the knees/pelvis; never mask the NPC root.
            state.AddMixingTransform(mask, true); state.speed = 0f;
            state.enabled = false; state.weight = 0f;
            if (soviet) _sovietState = state; else _militaryState = state;
            return state;
        }
        void Apply(int item, float started, float seconds)
        {
            Clear();
            if (item != 0 && MercDownPose.Down(_ai)) return;
            _item = item; _started = started; _seconds = seconds; _received = Time.time; _active = item != 0;
            if (!_active) { Clear(); return; }
            enabled = true;
            _state = State(item);
            if (_movePose != null) _movePose.Medical(true);
            if (_state == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("Merc medkit: native clip/rig unavailable; crouch and heal timer retained.");
                }
                return;
            }
            _rigOwned = true;
            if (_ik != null) { _ikEnabled = _ik.enabled; _ik.enabled = false; }
            if (_solver != null && _weight != null) FastField.SetFloat(_weight, _solver, 0f);
            if (_hand == null) _hand = TechnicalCrew.WeaponHand(_ai);
            _nextHolster = 0f; Holster(Time.time);
            Sample(Time.time);
        }
        void Clear()
        {
            _active = false; _item = 0;
            if (_state != null) { _state.enabled = false; _state.weight = 0f; }
            if (_rigOwned)
            {
                if (_ik != null) _ik.enabled = _ikEnabled;
                // A fresh weapon action supplies a fresh aim, never the old target.
                if (_solver != null && _weight != null) FastField.SetFloat(_weight, _solver, 0f);
                for (int i = 0; i < _holstered.Count; i++)
                    if (_holstered[i] != null) _holstered[i].enabled = true;
                _holstered.Clear(); _rigOwned = false;
            }
            if (_movePose != null) _movePose.Medical(false);
            enabled = false;
        }
        void Holster(float now)
        {
            if (_hand == null || now < _nextHolster) return;
            _nextHolster = now + 0.2f; _renderers.Clear();
            _hand.GetComponentsInChildren<Renderer>(true, _renderers);
            for (int i = 0; i < _renderers.Count; i++)
            {
                Renderer r = _renderers[i];
                if (r == null || !r.enabled) continue;
                if (!_holstered.Contains(r)) _holstered.Add(r);
                r.enabled = false;
            }
            _renderers.Clear();
        }
        void Sample(float now)
        {
            if (_state == null) return;
            float elapsed = Mathf.Max(0f, now - _started);
            _state.enabled = true;
            _state.time = Mathf.Clamp01(elapsed / _seconds) * _state.length;
            _state.weight = Mathf.Min(Mathf.Clamp01(elapsed / Fade), Mathf.Clamp01((_seconds - elapsed) / Fade));
        }
        void OnDisable() { End(Time.time); }
        void OnDestroy()
        {
            End(Time.time);
            MercMedPose p;
            if (Remote.TryGetValue(_view, out p) && p == this) Remote.Remove(_view);
        }
        void Publish(float now)
        {
            if (_view <= 0) return;
            _packet[0] = 4f; _packet[1] = _view; _packet[2] = _item;
            _packet[3] = _serial; _packet[4] = _active ? now - _started : 0f;
            _packet[5] = _seconds; _packet[6] = 2f;
            MercRide.SendAAPacket(_packet);
            _sendAt = now + 1f;
        }
        void Update()
        {
            if (!_active) return;
            FrameProf.S(FrameProf.S_MercMedPose_LateUpdate);
            try
            {
                float now = Time.time;
                // Silence/death/owner logout never leaves a remote pose stuck.
                if (_ai == null || now >= _started + _seconds || (!_local && now - _received > Lease))
                { End(now); return; }
                Sample(now);
                if (_rigOwned) Holster(now);
                if (_local && now >= _sendAt) Publish(now);
            }
            finally { FrameProf.E(FrameProf.S_MercMedPose_LateUpdate); }
        }
        internal static void OnPacket(float[] d, int sender)
        {
            if (d == null || (d.Length != 6 && d.Length != 7) || d[0] != 4f
                || !((d.Length == 6 && d[5] == 1f) || (d.Length == 7 && d[6] == 2f))) return;
            for (int i = 0; i < d.Length; i++) if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return;
            int view = Mathf.RoundToInt(d[1]), item = Mathf.RoundToInt(d[2]), serial = Mathf.RoundToInt(d[3]);
            if (view <= 0 || serial <= 0 || d[1] != view || d[2] != item || d[3] != serial
                || serial >= 16000000 || (item != 0 && !MercMedicine.Item(item))
                || d[4] < 0f || d[4] > 26f) return;
            float seconds = d.Length == 7 ? d[5] : MercMedicine.Seconds(item);
            if (item != 0 && seconds != 4f && seconds != MercMedicine.Seconds(item)) return;
            ViewLook();
            if (_findView == null || _ownerId == null) return;
            // After the first packet: no reflection call, component search,
            // boxed view id or key string on the 1 Hz heartbeat.
            MercMedPose cached;
            if (Remote.TryGetValue(view, out cached) && cached != null && cached._photonView != null)
            {
                if (cached._local || FastField.GetInt(_ownerId, cached._photonView) != sender || serial < cached._serial) return;
                if (serial == cached._serial)
                {
                    if (cached._active && item == cached._item && seconds == cached._seconds) cached._received = Time.time;
                    return; // a stop/expiry is a tombstone, even for a late heartbeat
                }
                cached._serial = serial; cached.Apply(item, Time.time - d[4], seconds); return;
            }
            Component v = _findView.Invoke(null, new object[] { view }) as Component;
            if (v == null || FastField.GetInt(_ownerId, v) != sender) return;
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            Component ai = npc == null ? null : v.GetComponent(npc);
            if (ai == null) return;
            string key = Crew.GroundKey(ai);
            if (key == null || !key.StartsWith(Mercs.KeyPrefix + sender + "/", StringComparison.Ordinal)) return;
            MercMedPose p = Of(ai);
            if (p == null || p._local || serial <= p._serial) return;
            p._photonView = v; Remote[view] = p;
            p._serial = serial; p.Apply(item, Time.time - d[4], seconds);
        }
    }
}

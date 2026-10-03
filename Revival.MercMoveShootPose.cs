// K3a runtime legacy layers, from K0. Player clips share the NPC skeleton.
// Mask pelvis/legs/spine, never the Animation root; native navigation owns it.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    [DefaultExecutionOrder(100)] // after native Update, before FinalIK LateUpdate
    public sealed class MercMoveShootPose : MonoBehaviour
    {
        static readonly string[,] DonorNames = {
            { "asr_walk_aiming", "asr_walk_back_aiming", "asr_walk_left_aiming", "asr_walk_right_aiming" },
            { "rifle_walk_aiming", "rifle_walk_back_aiming", "rifle_walk_left_aiming", "rifle_walk_right_aiming" },
            { "hg_walk_aiming", "hg_walk_back_aiming", "hg_walk_left_aiming", "hg_walk_right_aiming" }
        };
        static readonly string[] Aliases = { "ndr_k0_forward", "ndr_k0_back", "ndr_k0_left", "ndr_k0_right" };
        const string ShotAlias = "ndr_k0_recoil";
        static readonly AnimationClip[,] Clips = new AnimationClip[3, 4];
        static readonly Dictionary<int, MercMoveShootPose> Remote = new Dictionary<int, MercMoveShootPose>();
        static bool _clipsTried;
        static int _setupFrame = -1;
        static Type _npcType, _viewType;
        static FieldInfo _animField, _spineField, _ikField, _lookField, _mainField, _poseField, _addField,
            _aimField, _ownerField;
        static MethodInfo _findView, _viewId;

        Component _ai, _view, _ik;
        Animation _anim;
        bool _medical;
        NavMeshAgent _agent;
        Transform _spine, _hips, _left, _right, _look;
        object _solver;
        FieldInfo _weight;
        readonly AnimationState[] _walk = new AnimationState[4];
        AnimationState _shot;
        readonly float[] _packet = new float[10];
        int _id, _weapon, _failedWeapon, _sequence, _shots;
        bool _local, _active, _savedRotation, _rotationTaken, _warned;
        float _savedStopping;
        float _started, _touched, _received, _sendAt, _shotAt = -100f, _phase, _ikWeight;
        Vector3 _aim, _lastPosition;

        static void Lookup()
        {
            if (_npcType != null) return;
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_npcType == null || _viewType == null) return;
            _animField = AccessTools.Field(_npcType, "Anim");
            _spineField = AccessTools.Field(_npcType, "_mainCharSpine");
            _ikField = AccessTools.Field(_npcType, "_aimIk");
            _lookField = AccessTools.Field(_npcType, "LookAtIKTarget");
            _mainField = AccessTools.Field(_npcType, "MainState");
            _poseField = AccessTools.Field(_npcType, "PoseState");
            _addField = AccessTools.Field(_npcType, "AdditionalState");
            _aimField = AccessTools.Field(_npcType, "_aimingPoint");
            _ownerField = AccessTools.Field(_viewType, "ownerId");
            _findView = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            _viewId = AccessTools.PropertyGetter(_viewType, "viewID");
        }
        static void Donors()
        {
            if (_clipsTried) return;
            _clipsTried = true;
            // One cold scan. No per-tick global queries and no bundles/editor.
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(AnimationClip));
            for (int i = 0; i < all.Length; i++)
            {
                AnimationClip c = all[i] as AnimationClip;
                if (c == null) continue;
                string name = c.name;
                for (int t = 0; t < 3; t++) for (int d = 0; d < 4; d++)
                    if (name == DonorNames[t, d]) Clips[t, d] = c;
            }
        }
        internal static MercMoveShootPose Create(Component ai, NavMeshAgent agent)
        {
            if (ai == null) return null;
            Lookup();
            MercMoveShootPose p = ai.GetComponent<MercMoveShootPose>();
            if (p == null) p = ai.gameObject.AddComponent<MercMoveShootPose>();
            p.enabled = false; p._ai = ai; p._agent = agent;
            try
            {
                p._anim = _animField.GetValue(ai) as Animation;
                p._spine = _spineField.GetValue(ai) as Transform;
                p._hips = p._spine == null ? null : p._spine.parent;
                if (p._hips != null)
                {
                    p._left = p._hips.Find("MainChar_Thigh.L");
                    p._right = p._hips.Find("MainChar_Thigh.R");
                }
                p._ik = _ikField.GetValue(ai) as Component;
                p._look = _lookField.GetValue(ai) as Transform;
                if (p._ik != null)
                {
                    FieldInfo solver = AccessTools.Field(p._ik.GetType(), "solver");
                    p._solver = solver == null ? null : solver.GetValue(p._ik);
                    p._weight = p._solver == null ? null : AccessTools.Field(p._solver.GetType(), "IKPositionWeight");
                }
                p._view = ai.GetComponent(_viewType);
                if (p._view != null) p._id = Convert.ToInt32(_viewId.Invoke(p._view, null));
            }
            catch { p.Warn(); return null; }
            if (p._anim == null || p._spine == null || p._hips == null || p._left == null || p._right == null
                || p._look == null || p._solver == null || p._weight == null || p._weight.FieldType != typeof(float))
            { p.Warn(); return null; }
            MercMedPose medicine = ai.GetComponent<MercMedPose>();
            if (medicine != null) medicine.BindMovement(p);
            return p;
        }
        internal static bool MaySetup()
        {
            // A squad's first contact must not initialize six bodies at once.
            if (_setupFrame == Time.frameCount) return false;
            _setupFrame = Time.frameCount;
            return true;
        }
        void Warn()
        {
            if (_warned) return;
            _warned = true;
            RevivalPlugin.L.LogWarning("Merc moving fire: matching player gait/NPC rig/IK unavailable; original movement retained.");
        }
        bool Prepare(int weapon)
        {
            if (_weapon == weapon && _shot != null) return true;
            if (_failedWeapon == weapon) return false;
            string prefix = MercMoveShootPolicy.Prefix(weapon);
            if (prefix == null) return false;
            Donors();
            int type = prefix == "asr" ? 0 : prefix == "rifle" ? 1 : 2;
            AnimationState nativeShot = _anim[prefix == "rifle" ? "rifle_shoot_samopal" : prefix + "_shoot_auto"];
            if (nativeShot == null) { _failedWeapon = weapon; Warn(); return false; }
            for (int d = 0; d < 4; d++) if (Clips[type, d] == null) { _failedWeapon = weapon; Warn(); return false; }
            ClearStates();
            for (int d = 0; d < 4; d++)
            {
                _anim.RemoveClip(Aliases[d]); _anim.AddClip(Clips[type, d], Aliases[d]);
                AnimationState s = _anim[Aliases[d]];
                s.layer = 7; s.wrapMode = WrapMode.Loop; s.speed = 0f;
                s.AddMixingTransform(_hips, false);
                s.AddMixingTransform(_left, true); s.AddMixingTransform(_right, true);
                s.AddMixingTransform(_spine, true);
                _walk[d] = s;
            }
            _anim.RemoveClip(ShotAlias); _anim.AddClip(nativeShot.clip, ShotAlias);
            _shot = _anim[ShotAlias]; _shot.layer = 8; _shot.speed = 0f;
            _shot.wrapMode = WrapMode.ClampForever; _shot.AddMixingTransform(_spine, true);
            _weapon = weapon;
            return true;
        }
        internal bool Touch(int weapon, Vector3 aim, float now)
        {
            if (_medical) { Stop(); return false; }
            if (!Prepare(weapon)) { Stop(); return false; }
            _local = true; _aim = aim; _touched = now;
            if (!_active)
            {
                _active = true; enabled = true; _started = now; _phase = 0f; _shotAt = -100f;
                _lastPosition = transform.position; _sendAt = 0f; _ikWeight = 0f;
                if (_agent != null)
                {
                    _savedRotation = _agent.updateRotation;
                    _savedStopping = _agent.stoppingDistance;
                    _rotationTaken = true; _agent.updateRotation = false;
                }
            }
            // K3b: reach the measured edge instead of braking behind it. The
            // brain reverses before arrival; restore the native radius on exit.
            if (_agent != null && _agent.stoppingDistance > 0.1f) _agent.stoppingDistance = 0.1f;
            return true;
        }
        bool Walking()
        {
            return _ai != null && FastField.GetInt(_mainField, _ai) == 1
                && FastField.GetInt(_poseField, _ai) == 0 && FastField.GetInt(_addField, _ai) == 0;
        }
        internal bool Ready(float now)
        { return _active && Walking() && now - _started >= MercMoveShootPolicy.Fade && _ikWeight >= 0.6f; }
        // Capability is queried before the brain chooses its first moving
        // action. Waiting for Touch here made AttackFire depend on itself.
        internal bool Supports(int weapon) { return Prepare(weapon); }
        internal void Recoil(float now) { _shotAt = now; _shots++; }

        void ClearStates()
        {
            for (int i = 0; i < 4; i++) if (_walk[i] != null) { _walk[i].enabled = false; _walk[i].weight = 0f; }
            if (_shot != null) { _shot.enabled = false; _shot.weight = 0f; }
        }
        internal void Stop()
        {
            if (!_active) return;
            _active = false; enabled = false; ClearStates();
            if (_rotationTaken && _agent != null)
            { _agent.updateRotation = _savedRotation; _agent.stoppingDistance = _savedStopping; }
            _rotationTaken = false;
            if (_solver != null && _weight != null) FastField.SetFloat(_weight, _solver, 0f);
            if (_local) Publish(Time.time);
        }
        internal void Medical(bool active)
        { _medical = active; if (active) Stop(); }
        void Publish(float now)
        {
            if (_id <= 0 || _sequence >= 16000000) return;
            _packet[0] = 103f; _packet[1] = _id; _packet[2] = ++_sequence; _packet[3] = _weapon;
            _packet[4] = _active ? 1f : 0f; _packet[5] = _aim.x; _packet[6] = _aim.y;
            _packet[7] = _aim.z; _packet[8] = _shots; _packet[9] = 1f;
            MercRide.SendAAPacket(_packet); _sendAt = now + MercMoveShootPolicy.Heartbeat;
        }
        void Update()
        {
            if (!_active) return;
            FrameProf.S(FrameProf.S_MercMoveShootPose_Update);
            try
            {
                float now = Time.time;
                if (!MercMoveShoot.Enabled || _ai == null || _anim == null || _look == null || _ik == null
                    || (_local ? now - _touched > 0.3f : now - _received > MercMoveShootPolicy.Lease))
                { Stop(); return; }
                if (!Walking())
                {
                    ClearStates();
                    if (now - _started > 0.5f) Stop(); // allow reordered state RPC / pose event
                    return;
                }
                Vector3 at = transform.position;
                Vector3 velocity = _local && _agent != null ? _agent.velocity
                    : (at - _lastPosition) / Mathf.Max(0.001f, Time.deltaTime);
                _lastPosition = at;
                velocity.y = 0f;
                Vector3 local = transform.InverseTransformDirection(velocity);
                int direction = MercMoveShootPolicy.Direction(local.x, local.z);
                AnimationState state = _walk[direction];
                _phase += Time.deltaTime * Mathf.Clamp(velocity.magnitude / MercMoveShootPolicy.Speed, 0f, 1.5f);
                if (_phase >= state.length && state.length > 0f) _phase %= state.length;
                float blend = Mathf.Clamp01((now - _started) / MercMoveShootPolicy.Fade);
                for (int d = 0; d < 4; d++)
                {
                    AnimationState gait = _walk[d];
                    float want = d == direction ? blend : 0f;
                    gait.weight = Mathf.MoveTowards(gait.weight, want, Time.deltaTime / MercMoveShootPolicy.Fade);
                    gait.enabled = gait.weight > 0f; gait.time = _phase;
                }
                float recoil = now - _shotAt;
                _shot.enabled = recoil >= 0f && recoil < Mathf.Min(0.22f, _shot.length);
                _shot.weight = _shot.enabled ? 1f : 0f; _shot.time = Mathf.Max(0f, recoil);
                if (!_ik.gameObject.activeSelf) _ik.gameObject.SetActive(true);
                _ikWeight = Mathf.Min(1f, _ikWeight + Time.deltaTime * 5f);
                _look.position = _aim; FastField.SetFloat(_weight, _solver, _ikWeight);
                FastField.SetVector3(_aimField, _ai, _aim);
                if (_local && now >= _sendAt) Publish(now);
            }
            finally { FrameProf.E(FrameProf.S_MercMoveShootPose_Update); }
        }
        void OnDisable() { Stop(); }
        void OnDestroy()
        {
            Stop();
            MercMoveShootPose p;
            if (Remote.TryGetValue(_id, out p) && p == this) Remote.Remove(_id);
        }
        internal static void OnPacket(float[] d, int sender)
        {
            if (!MercMoveShootPolicy.Packet(d)) return;
            Lookup();
            if (_ownerField == null || _findView == null) return;
            int view = (int)d[1], sequence = (int)d[2];
            MercMoveShootPose p;
            if (!Remote.TryGetValue(view, out p) || p == null || p._view == null)
            {
                if (d[4] == 0f || !MercMoveShoot.Enabled) return;
                Component v = _findView.Invoke(null, new object[] { view }) as Component;
                if (v == null || FastField.GetInt(_ownerField, v) != sender) return;
                Component ai = v.GetComponent(_npcType);
                string key = ai == null ? null : Crew.GroundKey(ai);
                if (key == null || !key.StartsWith(Mercs.KeyPrefix + sender + "/", StringComparison.Ordinal)) return;
                p = Create(ai, null);
                if (p == null) return;
                Remote[view] = p;
            }
            if (!MercMoveShootPolicy.Accept(sender, FastField.GetInt(_ownerField, p._view), sequence, p._sequence, p._local)) return;
            p._sequence = sequence;
            if (d[4] == 0f || !MercMoveShoot.Enabled || p._medical) { p.Stop(); return; }
            if (!p.Prepare((int)d[3])) { p.Stop(); return; }
            float now = Time.time;
            if (!p._active) { p._started = now; p._phase = 0f; p._ikWeight = 0f; p._lastPosition = p.transform.position; }
            p._active = true; p.enabled = true; p._received = now;
            p._aim = new Vector3(d[5], d[6], d[7]);
            if ((int)d[8] > p._shots) { p._shots = (int)d[8]; p._shotAt = now; }
        }
    }
}

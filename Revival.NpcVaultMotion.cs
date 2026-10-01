// Native jump_run clip + short owner-driven arc. Reliable event 199 kind 7
// carries start/end pose to peers; NPC native sync resumes after landing.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    public sealed class NpcVaultMotion : MonoBehaviour
    {
        const string ClipName = "ndr_npc_vault";
        static AnimationClip _clip;
        static bool _clipTried;
        static Type _viewType, _npcType;
        static MethodInfo _findView, _viewId, _isMine, _ownerActive, _master;
        static FieldInfo _ownerId, _actorId;
        delegate Component ViewFinder(int id);
        delegate object MasterReader();
        static ViewFinder _find;
        static MasterReader _readMaster;
        readonly float[] _packet = new float[13];
        Component _ai, _view;
        Transform _body;
        Animation _animation;
        AnimationState _state;
        VaultPlan _plan;
        float _since, _nextCheck, _checkedTime, _retryUntil;
        bool _local, _busy, _pos, _rot, _stopped;
        int _id, _serial;
        internal NavMeshAgent Agent;
        internal bool Finished;
        internal bool Busy { get { return _busy; } }

        static void Look()
        {
            if (_viewType != null) return;
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_viewType == null) return;
            _findView = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            _viewId = AccessTools.PropertyGetter(_viewType, "viewID");
            _isMine = AccessTools.PropertyGetter(_viewType, "isMine");
            _ownerActive = AccessTools.PropertyGetter(_viewType, "isOwnerActive");
            _ownerId = AccessTools.Field(_viewType, "ownerId");
            _master = AccessTools.PropertyGetter(RevivalPlugin.TypeByName("PhotonNetwork"), "masterClient");
            _actorId = AccessTools.Field(RevivalPlugin.TypeByName("PhotonPlayer"), "actorID");
            if (_findView != null) _find = (ViewFinder)Delegate.CreateDelegate(typeof(ViewFinder), _findView);
            if (_master != null) _readMaster = (MasterReader)Delegate.CreateDelegate(typeof(MasterReader), _master);
        }
        internal static NpcVaultMotion Of(Component ai, NavMeshAgent agent)
        {
            NpcVaultMotion motion = ai.GetComponent<NpcVaultMotion>();
            if (motion != null) { if (agent != null) motion.Agent = agent; return motion; }
            motion = ai.gameObject.AddComponent<NpcVaultMotion>(); motion.enabled = false;
            motion._ai = ai; motion._body = ai.transform; motion.Agent = agent;
            motion._animation = ai.GetComponentInChildren<Animation>();
            Look();
            motion._view = _viewType == null ? null : ai.GetComponent(_viewType);
            if (motion._view != null && _viewId != null)
                motion._id = Convert.ToInt32(_viewId.Invoke(motion._view, null));
            if (!_clipTried)
            {
                _clipTried = true;
                // Reuse a loaded player clip; no process-wide resource scan.
                GameObject player = MapTools.LocalPlayer();
                Animation[] animations = player == null ? null : player.GetComponentsInChildren<Animation>(true);
                for (int i = 0; animations != null && i < animations.Length; i++)
                {
                    AnimationState jump = animations[i]["jump_run"];
                    if (jump != null) { _clip = jump.clip; break; }
                }
            }
            if (motion._animation != null && _clip != null)
            {
                motion._animation.AddClip(_clip, ClipName);
                motion._state = motion._animation[ClipName];
                motion._state.layer = 9; motion._state.wrapMode = WrapMode.ClampForever;
                motion._state.blendMode = AnimationBlendMode.Blend;
            }
            else RevivalPlugin.L.LogWarning("NpcVault: native jump_run clip unavailable; safe arc retained.");
            return motion;
        }
        internal void Begin(VaultPlan plan, float now)
        {
            _local = true; _serial++; Apply(plan, now);
            Publish(1f);
        }
        void Apply(VaultPlan plan, float now)
        {
            _plan = plan; _since = now; _nextCheck = now; _checkedTime = 0f;
            _retryUntil = now + plan.Duration + 1f;
            _busy = true; Finished = false;
            if (_local && Agent != null)
            {
                _pos = Agent.updatePosition; _rot = Agent.updateRotation; _stopped = Agent.isStopped;
                Agent.isStopped = true; Agent.updatePosition = false; Agent.updateRotation = false;
            }
            enabled = true;
        }
        void Publish(float phase)
        {
            if (_id <= 0) return;
            _packet[0] = 104f; _packet[1] = _id; _packet[2] = _serial; _packet[3] = phase;
            _packet[4] = _plan.Start.x; _packet[5] = _plan.Start.y; _packet[6] = _plan.Start.z;
            _packet[7] = _plan.End.x; _packet[8] = _plan.End.y; _packet[9] = _plan.End.z;
            _packet[10] = _plan.Lift; _packet[11] = _plan.Duration; _packet[12] = 1f;
            MercRide.SendAAPacket(_packet);
        }
        bool Authority()
        {
            return _view == null || _isMine == null || FastCall.Bool(_isMine, _view);
        }
        bool ValidateAhead(float elapsed)
        {
            PhysicsVaultWorld world = PhysicsVaultWorld.Instance;
            world.Ignore = _body; world.Area = Agent == null ? NavMesh.AllAreas : Agent.areaMask;
            float until = Mathf.Min(_plan.Duration, elapsed + NpcVaultCore.Interval);
            Vector3 previous = NpcVaultCore.At(_plan, _checkedTime);
            // Sweeps follow the curve instead of a chord across the sandbags.
            while (_checkedTime < until)
            {
                float next = Mathf.Min(until, _checkedTime + _plan.Duration / 10f);
                Vector3 at = NpcVaultCore.At(_plan, next);
                if (!world.Sweep(previous, at)) return false;
                previous = at; _checkedTime = next;
            }
            return true;
        }
        void LateUpdate()
        {
            if (!_busy) return;
            FrameProf.S(FrameProf.S_NpcVaultL);
            try
            {
                if (_ai == null || _body == null) { Cancel(); return; }
                float now = Time.time, elapsed = now - _since;
                if (_local)
                {
                    if (!Authority() || !NpcWar.VaultAlive(_ai)) { Cancel(); return; }
                    if (now >= _nextCheck)
                    {
                        _nextCheck = now + NpcVaultCore.Interval;
                        bool clear = !_plan.RecoveryOnly && ValidateAhead(elapsed);
                        if (!clear || elapsed >= _plan.Duration)
                        {
                            PhysicsVaultWorld world = PhysicsVaultWorld.Instance;
                            world.Ignore = _body; world.Area = Agent == null ? NavMesh.AllAreas : Agent.areaMask;
                            Vector3 end;
                            if (NpcVaultCore.Recover(world, _plan, out end))
                            { Finish(end); return; }
                            // A transient occupant at BOTH ends cannot authorize
                            // teleporting into solid geometry. Hold at the last
                            // checked arc point briefly, then hand back to AI.
                            if (now >= _retryUntil) { Cancel(); Finished = true; return; }
                            return;
                        }
                    }
                    elapsed = Mathf.Min(elapsed, _checkedTime);
                }
                else if (elapsed >= _plan.Duration) { Cancel(); return; }
                _body.position = NpcVaultCore.At(_plan, elapsed);
                Vector3 direction = _plan.End - _plan.Start; direction.y = 0f;
                if (direction.sqrMagnitude > 0.01f) _body.rotation = Quaternion.LookRotation(direction);
                if (_state != null)
                {
                    _state.enabled = true; _state.weight = 1f; _state.speed = 0f;
                    _state.normalizedTime = Mathf.Clamp01(elapsed / _plan.Duration);
                }
            }
            finally { FrameProf.E(FrameProf.S_NpcVaultL); }
        }
        void Finish(Vector3 end)
        {
            _body.position = end;
            if (Agent != null && Agent.isActiveAndEnabled) Agent.Warp(end);
            _plan.End = end; Publish(0f); Cancel(); Finished = true;
        }
        void Cancel()
        {
            _busy = false;
            if (_state != null) { _state.enabled = false; _state.weight = 0f; }
            if (_local && Agent != null)
            {
                Agent.updatePosition = _pos; Agent.updateRotation = _rot;
                if (Agent.isActiveAndEnabled && Agent.isOnNavMesh) Agent.isStopped = _stopped;
            }
            enabled = false;
        }
        void OnDisable() { if (_busy) Cancel(); }

        internal static void OnPacket(float[] data, int sender)
        {
            if (data.Length != 13 || data[12] != 1f) return;
            for (int i = 0; i < data.Length; i++) if (float.IsNaN(data[i]) || float.IsInfinity(data[i])) return;
            if (data[1] <= 0f || data[1] != Mathf.RoundToInt(data[1]) || data[2] <= 0f
                || data[2] != Mathf.RoundToInt(data[2]) || (data[3] != 0f && data[3] != 1f)
                || data[10] < 0.35f || data[10] > NpcVaultCore.Hip + 0.35f
                || data[11] != 0.72f) return;
            VaultPlan plan = new VaultPlan();
            plan.Start = new Vector3(data[4], data[5], data[6]);
            plan.End = new Vector3(data[7], data[8], data[9]); plan.Lift = data[10]; plan.Duration = data[11];
            Vector3 span = plan.End - plan.Start;
            if (span.sqrMagnitude > NpcVaultCore.MaxSpan * NpcVaultCore.MaxSpan + 1f
                || Mathf.Abs(span.y) > 1f) return;
            Look();
            if (_find == null || _ownerId == null || _npcType == null) return;
            Component view = _find((int)data[1]);
            if (view == null || (_isMine != null && FastCall.Bool(_isMine, view))) return;
            int owner = FastField.GetInt(_ownerId, view);
            if (owner != sender)
            {
                // Scene NPCs and abandoned views are controlled by the current
                // master, matching PUN isMine. Active player ownership wins.
                if (owner != 0 && (_ownerActive == null || FastCall.Bool(_ownerActive, view))) return;
                object master = _readMaster == null ? null : _readMaster();
                if (master == null || _actorId == null || FastField.GetInt(_actorId, master) != sender) return;
            }
            Component ai = view.GetComponent(_npcType);
            if (ai == null || (plan.Start - ai.transform.position).sqrMagnitude > 144f) return;
            NpcVaultMotion motion = Of(ai, null);
            if (motion._local)
            {
                if (motion.Authority()) return;
                motion.Cancel(); motion._local = false; motion._serial = 0;
            }
            if (data[2] < motion._serial) return;
            if (data[3] == 0f)
            {
                motion._serial = (int)data[2]; motion.Cancel(); ai.transform.position = plan.End; return;
            }
            if (data[2] == motion._serial) return;
            motion._serial = (int)data[2]; motion.Apply(plan, Time.time);
        }
    }
}

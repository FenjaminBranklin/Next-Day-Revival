// Z K7: reversible ground pose, retained hit colliders and owner-validated Photon state.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    public sealed class MercDownPose : MonoBehaviour
    {
        static readonly Dictionary<int, MercDownPose> Poses = new Dictionary<int, MercDownPose>();
        static readonly Dictionary<int, MercDownPose> Bodies = new Dictionary<int, MercDownPose>();
        static Type _viewType;
        static MethodInfo _findView, _viewId;
        static FieldInfo _ownerId;
        Behaviour _npc;
        NavMeshAgent _nav;
        Animation _anim;
        Transform _hips;
        Behaviour[] _ik;
        bool[] _ikEnabled;
        Component _viewComponent;
        bool _active, _local, _npcEnabled, _navStopped;
        int _view, _serial, _body;
        float _sendAt;
        Vector3 _hipPosition;
        Quaternion _hipRotation;
        readonly float[] _packet = new float[5];
        internal bool Active { get { return _active; } }

        static void ViewLook()
        {
            if (_viewType != null) return;
            _viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_viewType == null) return;
            _findView = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            _viewId = AccessTools.PropertyGetter(_viewType, "viewID");
            _ownerId = AccessTools.Field(_viewType, "ownerId");
        }
        static MercDownPose Of(Component ai)
        {
            if (ai == null) return null;
            MercDownPose p = ai.GetComponent<MercDownPose>();
            if (p != null) return p;
            p = ai.gameObject.AddComponent<MercDownPose>();
            p._body = ai.GetInstanceID(); Bodies[p._body] = p;
            p._npc = ai as Behaviour;
            p._nav = ai.GetComponent<NavMeshAgent>();
            p._anim = ai.GetComponentInChildren<Animation>();
            Transform[] bones = ai.GetComponentsInChildren<Transform>();
            for (int i = 0; i < bones.Length; i++)
                if (bones[i].name == "Bip01" || bones[i].name == "Hips" || bones[i].name.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0)
                { p._hips = bones[i]; break; }
            Behaviour[] all = ai.GetComponentsInChildren<Behaviour>();
            List<Behaviour> ik = new List<Behaviour>();
            for (int i = 0; i < all.Length; i++)
            {
                string name = all[i].GetType().Name;
                if (name == "AimIK" || name == "LookAtIK" || name == "FullBodyBipedIK") ik.Add(all[i]);
            }
            p._ik = ik.ToArray(); p._ikEnabled = new bool[p._ik.Length];
            ViewLook();
            p._viewComponent = _viewType == null ? null : ai.GetComponent(_viewType);
            if (p._viewComponent != null && _viewId != null)
                p._view = Convert.ToInt32(_viewId.Invoke(p._viewComponent, null));
            if (p._view > 0) Poses[p._view] = p;
            return p;
        }
        internal static void Set(Component ai, bool down, float now)
        {
            MercDownPose p = down ? Of(ai) : ai == null ? null : ai.GetComponent<MercDownPose>();
            if (p == null || p._active == down) return;
            p._local = true; p._serial++; p.Apply(down); p.Publish(now);
        }
        void Apply(bool down)
        {
            if (_active == down) return;
            _active = down;
            if (down)
            {
                _npcEnabled = _npc != null && _npc.enabled;
                if (_npc != null)
                {
                    MonoBehaviour npc = _npc as MonoBehaviour;
                    if (npc != null) npc.StopAllCoroutines();
                    _npc.enabled = false;
                }
                if (_nav != null && _nav.enabled && _nav.isOnNavMesh)
                { _navStopped = _nav.isStopped; _nav.isStopped = true; _nav.ResetPath(); }
                for (int i = 0; i < _ik.Length; i++)
                { _ikEnabled[i] = _ik[i].enabled; _ik[i].enabled = false; }
                if (_anim != null) _anim.Stop();
                if (_hips != null)
                {
                    _hipPosition = _hips.localPosition; _hipRotation = _hips.localRotation;
                    _hips.rotation = Quaternion.AngleAxis(85f, transform.forward) * _hips.rotation;
                    Vector3 at = transform.position; at.y += 0.7f; _hips.position = at;
                }
                else RevivalPlugin.L.LogWarning("Merc downed: native skeleton root not found; immobilized pose only.");
                // No ragdoll/death action: retain every native damage collider and all gear.
            }
            else
            {
                if (_hips != null) { _hips.localPosition = _hipPosition; _hips.localRotation = _hipRotation; }
                for (int i = 0; i < _ik.Length; i++) if (_ik[i] != null) _ik[i].enabled = _ikEnabled[i];
                if (_npc != null) _npc.enabled = _npcEnabled;
                if (_nav != null && _nav.enabled && _nav.isOnNavMesh) _nav.isStopped = _navStopped;
            }
        }
        void Publish(float now)
        {
            if (_view <= 0) return;
            _packet[0] = 7f; _packet[1] = _view; _packet[2] = _active ? 1f : 0f;
            _packet[3] = _serial; _packet[4] = 1f;
            MercRide.SendAAPacket(_packet); _sendAt = now + 1f;
        }
        internal static void Tick(float now)
        {
            foreach (KeyValuePair<int, MercDownPose> pair in Poses)
            {
                MercDownPose p = pair.Value;
                if (p != null && p._local && p._active && now >= p._sendAt) p.Publish(now);
            }
        }
        internal static bool Down(Component ai)
        {
            MercDownPose p;
            return ai != null && Bodies.TryGetValue(ai.GetInstanceID(), out p) && p != null && p._active;
        }
        internal static void OnPacket(float[] d, int sender)
        {
            if (d.Length != 5 || d[4] != 1f || (d[2] != 0f && d[2] != 1f)) return;
            for (int i = 0; i < d.Length; i++) if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return;
            int view = Mathf.RoundToInt(d[1]), serial = Mathf.RoundToInt(d[3]);
            if (view <= 0 || serial <= 0) return;
            ViewLook(); if (_findView == null || _ownerId == null) return;
            MercDownPose p;
            if (!Poses.TryGetValue(view, out p) || p == null)
            {
                Component v = _findView.Invoke(null, new object[] { view }) as Component;
                if (v == null || FastField.GetInt(_ownerId, v) != sender) return;
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                Component ai = npc == null ? null : v.GetComponent(npc);
                if (ai == null) return;
                string key = Crew.GroundKey(ai);
                if (key == null || !key.StartsWith(Mercs.KeyPrefix + sender + "/", StringComparison.Ordinal)) return;
                p = Of(ai);
            }
            if (p == null || p._local || p._viewComponent == null || FastField.GetInt(_ownerId, p._viewComponent) != sender || serial < p._serial) return;
            p._serial = serial; p.Apply(d[2] == 1f);
        }
        void OnDestroy()
        {
            MercDownPose p;
            if (Poses.TryGetValue(_view, out p) && p == this) Poses.Remove(_view);
            if (Bodies.TryGetValue(_body, out p) && p == this) Bodies.Remove(_body);
        }
    }
}

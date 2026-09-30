// Native NPCs held in the game's own parachute pose until their feet land.
// No cloned character, skin replacement, per-frame animation or scene scan.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class ParaPose
    {
        const string HangName = "ndr_paratrooper_hang";
        static readonly Dictionary<Component, ParaPose> Held = new Dictionary<Component, ParaPose>();
        static Type _npcType, _viewType;
        static MethodInfo _findView, _destroy, _activate, _visualize;
        static Func<double> _clock;
        static Func<bool> _master;
        static bool _installed, _clockLooked;
        static AnimationClip _clip;
        static bool _clipLooked;

        internal readonly Component Ai;
        internal readonly Transform Root;
        internal GameObject CanopyObject;
        Animation _animation;
        Animator _animator;
        NavMeshAgent _agent;
        Rigidbody _rigidbody;
        Renderer[] _renderers;
        bool[] _rendererEnabled;
        readonly List<Behaviour> _ik = new List<Behaviour>();
        readonly List<bool> _ikEnabled = new List<bool>();
        bool _agentEnabled, _kinematic, _animationEnabled, _animatorEnabled;
        bool _ready, _released, _visible;
        float _readyAt;

        internal static void Install()
        {
            if (_installed) return;
            _npcType = RevivalPlugin.TypeByName("NPC_AI2");
            if (_npcType == null) throw new InvalidOperationException("NPC_AI2 not found");
            Harmony h = new Harmony("nextday.revival.paratroopers");
            string[] methods = new string[] { "Update", "NavAgentSetActive", "SetActiveAI" };
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = AccessTools.Method(_npcType, methods[i], null, null);
                if (m == null) throw new MissingMethodException("NPC_AI2", methods[i]);
                h.Patch(m, new HarmonyMethod(typeof(ParaPose).GetMethod("RunPrefix")), null, null, null, null);
            }
            MethodInfo state = AccessTools.Method(_npcType, "SetStateWithAnimAndSync", null, null);
            if (state == null) throw new MissingMethodException("NPC_AI2", "SetStateWithAnimAndSync");
            h.Patch(state, new HarmonyMethod(typeof(ParaPose).GetMethod("StatePrefix")), null, null, null, null);
            _activate = AccessTools.Method(_npcType, "SetActiveAI", new Type[] { typeof(bool) }, null);
            _visualize = AccessTools.Method(_npcType, "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
            if (_activate == null || _visualize == null) throw new MissingMethodException("NPC visualization/AI switch");
            h.Patch(_visualize, new HarmonyMethod(typeof(ParaPose).GetMethod("VisualPrefix")), null, null, null, null);
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            MethodInfo master = photon == null ? null : AccessTools.PropertyGetter(photon, "isMasterClient");
            if (master != null) _master = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), master);
            _installed = true;
        }

        internal static bool MasterClient() { return _master == null || _master(); }

        // The ordinary NPC Update is skipped only while that exact NPC is held.
        public static bool RunPrefix(object __instance)
        {
            Component ai = __instance as Component;
            return Held.Count == 0 || ai == null || !Held.ContainsKey(ai);
        }

        public static bool VisualPrefix(object __instance, bool __0)
        {
            ParaPose p;
            Component ai = __instance as Component;
            return ai == null || !Held.TryGetValue(ai, out p) || (__0 && !p._ready);
        }

        // Damage/death still belongs to the native NPC. Do not freeze a corpse.
        public static bool StatePrefix(object __instance)
        {
            Component ai = __instance as Component;
            ParaPose p;
            if (Held.Count == 0 || ai == null || !Held.TryGetValue(ai, out p)) return true;
            if (NpcWar.GroundAlive(ai)) return false;
            p.Release(false);
            return true;
        }

        internal static double Clock()
        {
            if (!_clockLooked)
            {
                _clockLooked = true;
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                MethodInfo getter = photon == null ? null : AccessTools.PropertyGetter(photon, "time");
                if (getter != null) _clock = (Func<double>)Delegate.CreateDelegate(typeof(Func<double>), getter);
            }
            return _clock == null ? Time.time : _clock();
        }

        internal static Component Find(int view)
        {
            if (view <= 0) return null;
            if (_viewType == null) _viewType = RevivalPlugin.TypeByName("PhotonView");
            if (_viewType == null) return null;
            if (_findView == null) _findView = AccessTools.Method(_viewType, "Find", new Type[] { typeof(int) }, null);
            if (_findView == null) return null;
            Component pv = _findView.Invoke(null, new object[] { view }) as Component;
            return pv == null ? null : pv.GetComponent(_npcType);
        }

        internal static ParaPose Hold(Component ai)
        {
            if (ai == null) return null;
            ParaPose p;
            if (Held.TryGetValue(ai, out p)) return p;
            p = new ParaPose(ai);
            Held.Add(ai, p);
            return p;
        }

        ParaPose(Component ai)
        {
            Ai = ai;
            Root = ai.transform;
            _agent = ai.GetComponent<NavMeshAgent>();
            _agentEnabled = _agent != null && _agent.enabled;
            if (_agent != null) _agent.enabled = false;
            _rigidbody = ai.GetComponent<Rigidbody>();
            _kinematic = _rigidbody == null || _rigidbody.isKinematic;
            if (_rigidbody != null) _rigidbody.isKinematic = true;
            CaptureRenderers();
            // Native Start and the remote appearance repair must run first.
            _readyAt = Time.time + 0.5f;
        }

        void CaptureRenderers()
        {
            // Restore before refreshing: saved flags must not become our hidden flags.
            if (_renderers != null) SetVisible(true);
            _renderers = Ai.GetComponentsInChildren<Renderer>(true);
            _rendererEnabled = new bool[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++) _rendererEnabled[i] = _renderers[i].enabled;
            _visible = true;
            SetVisible(false);
        }

        internal bool Gone { get { return _released || Ai == null || Root == null; } }

        // Called by the 5 Hz alive/near check, never by the transform loop.
        internal void Refresh()
        {
            if (Gone) return;
            if (!NpcWar.GroundAlive(Ai)) { Release(false); return; }
            if (_ready || Time.time < _readyAt) return;
            _readyAt = Time.time + 0.1f;
            _animation = Ai.GetComponentInChildren<Animation>();
            if (_animation == null) return;
            if (!_clipLooked)
            {
                _clipLooked = true;
                // Confirmed in resources.assets: this prefab has the player
                // parachute clips and the same MainChar_Skeleton as the NPC.
                GameObject model = Resources.Load("NPCSpawn/NPC_Models/Male_01_v78") as GameObject;
                Animation source = model == null ? null : model.GetComponentInChildren<Animation>();
                _clip = source == null ? null : source.GetClip("parashute_fly_idle");
                if (_clip == null) RevivalPlugin.L.LogWarning("Paratroopers: game parachute pose clip missing.");
            }
            if (_clip == null) return;
            CaptureRenderers();
            _animationEnabled = _animation.enabled;
            _animator = Ai.GetComponentInChildren<Animator>();
            _animatorEnabled = _animator != null && _animator.enabled;
            if (_animator != null) _animator.enabled = false;
            Behaviour[] behaviours = Ai.GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour b = behaviours[i];
                if (b.GetType().Namespace != "RootMotion.FinalIK") continue;
                _ik.Add(b); _ikEnabled.Add(b.enabled); b.enabled = false;
            }
            _animation.AddClip(_clip, HangName);
            _animation.enabled = true;
            _animation.Play(HangName);
            AnimationState hang = _animation[HangName];
            hang.time = _clip.length * 0.5f;
            hang.speed = 0f;
            _animation.Sample();
            _animation.enabled = false;
            CanopyObject = Canopy.Make();
            CanopyObject.transform.SetParent(Root, false);
            CanopyObject.SetActive(false);
            _ready = true;
        }

        void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = visible && _rendererEnabled[i];
        }

        // Value types only; no query, animation evaluation or allocation here.
        internal void Drive(Vector3 position, float yaw, float sway, bool visible)
        {
            if (Gone) return;
            Root.position = position;
            Root.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetVisible(visible && _ready);
            if (CanopyObject == null) return;
            if (CanopyObject.activeSelf != visible) CanopyObject.SetActive(visible);
            CanopyObject.transform.localRotation = Quaternion.Euler(sway, 0f, sway * 0.6f);
        }

        internal void Land(Vector3 ground, float yaw)
        {
            if (Gone) { Release(false); return; }
            Root.position = ground;
            Root.rotation = Quaternion.Euler(0f, yaw, 0f);
            Release(true);
        }

        internal void Release(bool landed)
        {
            if (_released) return;
            _released = true;
            Held.Remove(Ai);
            if (CanopyObject != null) UnityEngine.Object.Destroy(CanopyObject);
            CanopyObject = null;
            SetVisible(true);
            for (int i = 0; i < _ik.Count; i++) if (_ik[i] != null) _ik[i].enabled = _ikEnabled[i];
            if (landed && Ai != null)
            {
                // CrewAlarm may have tried these native setters while we were
                // holding the NPC. Re-enter them after removing the hold.
                _activate.Invoke(Ai, new object[] { true });
                _visualize.Invoke(Ai, new object[] { true });
            }
            if (_animator != null) _animator.enabled = _animatorEnabled;
            if (_animation != null && _ready)
            {
                _animation.enabled = landed || _animationEnabled;
                // Blend on the SAME skeleton out of the held pose, not a mesh swap.
                if (landed && _animation.GetClip("idle_alert") != null)
                {
                    _animation.Play(HangName);
                    _animation[HangName].time = _clip.length * 0.5f;
                    _animation.CrossFade("idle_alert", 0.25f);
                }
            }
            if (_rigidbody != null) _rigidbody.isKinematic = _kinematic;
            if (_agent != null && landed && _agentEnabled)
            {
                _agent.enabled = true;
                if (_agent.isOnNavMesh) _agent.Warp(Root.position);
            }
        }

        internal void DestroyAboard()
        {
            Release(false);
            if (Ai == null) return;
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (_destroy == null && photon != null)
                _destroy = AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            if (_destroy != null) _destroy.Invoke(null, new object[] { Ai.gameObject });
        }
    }
}

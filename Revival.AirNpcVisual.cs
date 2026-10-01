// Local aircraft NPC silhouettes. No AI, settlement, collider or Photon writes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class AirNpcVisual
    {
        static Type _groupType;
        static FieldInfo _levels;
        static MethodInfo _show;
        static bool _ready;
        static readonly Dictionary<int, State> Owned = new Dictionary<int, State>();
        static readonly Dictionary<int, State> Actors = new Dictionary<int, State>();

        internal static void Install(Harmony harmony, Type aiType)
        {
            _groupType = RevivalPlugin.TypeByName("CharacterLODGroup");
            _levels = _groupType == null ? null : AccessTools.Field(_groupType, "LODLevels");
            _show = _groupType == null ? null : AccessTools.Method(_groupType, "ShowLOD", new Type[] { typeof(int) }, null);
            MethodInfo vis = AccessTools.Method(aiType, "SetPlayVisualizationValue", new Type[] { typeof(bool) }, null);
            if (_levels == null || _show == null || vis == null)
            {
                RevivalPlugin.L.LogWarning("AirNpcVisual: LOD/visualization seam missing; aircraft silhouettes unavailable.");
                return;
            }
            harmony.Patch(_show, new HarmonyMethod(typeof(AirNpcVisual).GetMethod("ShowPrefix")), null, null, null, null);
            harmony.Patch(vis, null, new HarmonyMethod(typeof(AirNpcVisual).GetMethod("VisualizationPostfix")), null, null, null);
            MethodInfo active = AccessTools.Method(aiType, "SetActiveAI", new Type[] { typeof(bool) }, null);
            if (active != null)
                harmony.Patch(active, null, new HarmonyMethod(typeof(AirNpcVisual).GetMethod("ActivePostfix")), null, null, null);
            _ready = true;
        }

        // Vanilla LOD messages can hide the frozen body between our sliced passes.
        public static bool ShowPrefix(object __instance)
        {
            if (Owned.Count == 0) return true;
            Component c = __instance as Component;
            State s;
            return c == null || !Owned.TryGetValue(c.GetInstanceID(), out s) || s.Writing;
        }

        public static void VisualizationPostfix(object __instance, bool __0)
        {
            if (Actors.Count == 0) return;
            Component c = __instance as Component;
            State s;
            if (c != null && Actors.TryGetValue(c.GetInstanceID(), out s))
            {
                // Remember the game's latest intent, rather than restoring a stale
                // enabled flag after a player enters/leaves the settlement.
                s.AnimEnabled = __0;
                s.Freeze();
            }
        }

        public static void ActivePostfix(object __instance, bool __0)
        {
            if (Actors.Count == 0) return;
            Component c = __instance as Component;
            State s;
            if (c != null && Actors.TryGetValue(c.GetInstanceID(), out s))
            {
                s.AnimPlaying = __0;
                s.SampleStoppedPose();
            }
        }

        sealed class Group
        {
            internal Component Component;
            internal Action<int> Show;
            internal int Lowest;
        }

        sealed class Skin
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal bool Enabled, Active, Offscreen;
            internal SkinQuality Quality;
        }

        internal sealed class State
        {
            internal bool Active, Writing, AnimEnabled, AnimPlaying;
            readonly Component _ai;
            readonly Component _lod;
            readonly FieldInfo _currentLevel;
            readonly Animation _anim;
            readonly List<Group> _groups = new List<Group>();
            readonly List<Skin> _skins = new List<Skin>();
            readonly List<Renderer> _renderers = new List<Renderer>();
            ShadowCastingMode[] _shadows;
            bool[] _receive;

            internal State(Component ai, Animation anim, Component lod)
            {
                _ai = ai; _anim = anim; _id = ai.GetInstanceID();
                _lod = lod;
                _currentLevel = lod == null ? null : AccessTools.Field(lod.GetType(), "currentLODLevel");
                if (!_ready) return;
                Component[] groups = ai.GetComponentsInChildren(_groupType, true);
                for (int i = 0; i < groups.Length; i++)
                {
                    IList levels = _levels.GetValue(groups[i]) as IList;
                    if (levels == null) continue;
                    int lowest = -1;
                    for (int j = 0; j < levels.Count; j++)
                    {
                        object level = levels[j];
                        if (level == null) continue;
                        Type t = level.GetType();
                        FieldInfo sf = AccessTools.Field(t, "skinnedMesh"), mf = AccessTools.Field(t, "sharedMesh");
                        SkinnedMeshRenderer r = sf == null ? null : sf.GetValue(level) as SkinnedMeshRenderer;
                        Mesh mesh = mf == null ? null : mf.GetValue(level) as Mesh;
                        if (r == null || mesh == null) continue;
                        lowest = j;
                        bool known = false;
                        for (int k = 0; k < _skins.Count; k++) if (_skins[k].Renderer == r) { known = true; break; }
                        if (!known) { Skin skin = new Skin(); skin.Renderer = r; _skins.Add(skin); }
                    }
                    if (lowest < 0) continue;
                    Group g = new Group(); g.Component = groups[i]; g.Lowest = lowest;
                    g.Show = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), groups[i], _show);
                    _groups.Add(g);
                    _groupIds.Add(groups[i].GetInstanceID());
                }
                ai.GetComponentsInChildren<Renderer>(true, _renderers);
                _shadows = new ShadowCastingMode[_renderers.Count];
                _receive = new bool[_renderers.Count];
            }

            internal bool Available { get { return _groups.Count > 0; } }

            internal void Enter()
            {
                if (Active || !Available || _ai == null) return;
                // Snapshots are refreshed on EVERY entry, including game clothing
                // LOD state and engine shadow choices that changed since last flight.
                AnimEnabled = _anim != null && _anim.enabled;
                AnimPlaying = _anim != null && _anim.isPlaying;
                for (int i = 0; i < _skins.Count; i++)
                {
                    Skin s = _skins[i]; SkinnedMeshRenderer r = s.Renderer;
                    if (r == null) continue;
                    s.Mesh = r.sharedMesh; s.Enabled = r.enabled; s.Active = r.gameObject.activeSelf;
                    s.Offscreen = r.updateWhenOffscreen; s.Quality = r.quality;
                }
                for (int i = 0; i < _renderers.Count; i++)
                {
                    Renderer r = _renderers[i]; if (r == null) continue;
                    _shadows[i] = r.shadowCastingMode; _receive[i] = r.receiveShadows;
                    if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                    r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                }
                Active = true;
                Actors[_ai.GetInstanceID()] = this;
                for (int i = 0; i < _groups.Count; i++) Owned[_groups[i].Component.GetInstanceID()] = this;
                Writing = true;
                try
                {
                    for (int i = 0; i < _groups.Count; i++) _groups[i].Show(_groups[i].Lowest);
                }
                finally { Writing = false; }
                for (int i = 0; i < _skins.Count; i++)
                {
                    SkinnedMeshRenderer r = _skins[i].Renderer;
                    if (r == null) continue;
                    r.updateWhenOffscreen = false; r.quality = SkinQuality.Bone1;
                    // Some groups use SetActive but retain a previously disabled renderer.
                    if (r.sharedMesh != null && r.gameObject.activeSelf) r.enabled = true;
                }
                // Sample one authored pose if vanilla stopped the animation. Do not
                // turn on visualization, AI or collision to obtain a visible body.
                SampleStoppedPose();
                Freeze();
            }

            internal void SampleStoppedPose()
            {
                if (_anim != null && !_anim.isPlaying && _anim.clip != null) { _anim.Play(); _anim.Sample(); }
                Freeze();
            }

            internal void Freeze() { if (_anim != null) _anim.enabled = false; }

            internal void Exit()
            {
                if (!Active) return;
                Active = false;
                // Keys remain valid even when Unity objects were destroyed.
                Actors.Remove(_id);
                for (int i = 0; i < _groupIds.Count; i++) Owned.Remove(_groupIds[i]);
                for (int i = 0; i < _skins.Count; i++)
                {
                    Skin s = _skins[i]; SkinnedMeshRenderer r = s.Renderer; if (r == null) continue;
                    r.sharedMesh = s.Mesh; r.enabled = s.Enabled;
                    r.updateWhenOffscreen = s.Offscreen; r.quality = s.Quality;
                    r.gameObject.SetActive(s.Active);
                }
                for (int i = 0; i < _renderers.Count; i++)
                {
                    Renderer r = _renderers[i]; if (r == null) continue;
                    if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                    r.shadowCastingMode = _shadows[i]; r.receiveShadows = _receive[i];
                }
                // The controller continued recording its desired level while its
                // ShowLOD messages were suppressed. Replay that latest intent;
                // otherwise its equal-level early return can retain stale meshes.
                if (_lod != null && _currentLevel != null)
                {
                    int level = FastField.GetInt(_currentLevel, _lod);
                    for (int i = 0; i < _groups.Count; i++)
                        if (_groups[i].Component != null) _groups[i].Show(level);
                }
                if (_anim != null)
                {
                    if (!AnimPlaying) _anim.Stop();
                    _anim.enabled = AnimEnabled;
                }
            }

            readonly int _id;
            readonly List<int> _groupIds = new List<int>();
        }
    }
}

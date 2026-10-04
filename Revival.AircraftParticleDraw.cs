using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    // Reuse the existing heli blast/wreck systems at combat distance. Simulation
    // and light/damage positions stay real; only cached billboard particles are
    // projected for a camera, without changing its far clip or shared materials.
    public sealed class AircraftParticleDraw : MonoBehaviour
    {
        sealed class Part
        {
            internal ParticleSystem Source, Draw;
            internal ParticleSystemRenderer SourceRenderer, DrawRenderer;
            internal ParticleSystem.Particle[] Buffer;
            internal bool World;
        }
        static readonly List<AircraftParticleDraw> Live = new List<AircraftParticleDraw>();
        static bool _hooked;
        Part[] _parts;
        GameObject _drawRoot;
        float _simulatedAt;
        float _occludeAt;
        bool _hidden;

        internal static void Attach(GameObject root)
        {
            AircraftParticleDraw v = root.GetComponent<AircraftParticleDraw>();
            if (v == null) { v = root.AddComponent<AircraftParticleDraw>(); v.Build(); }
            v._simulatedAt = Time.time;
            for (int i = 0; i < v._parts.Length; i++) v._parts[i].Source.Pause(false);
        }

        void Build()
        {
            ParticleSystem[] sources = GetComponentsInChildren<ParticleSystem>(true);
            _parts = new Part[sources.Length];
            _drawRoot = new GameObject("NDR_AircraftImpactDraw");
            for (int i = 0; i < sources.Length; i++)
            {
                Part p = new Part();
                p.Source = sources[i];
                p.SourceRenderer = p.Source.GetComponent<ParticleSystemRenderer>();
                p.World = p.Source.main.simulationSpace == ParticleSystemSimulationSpace.World;
                int cap = Mathf.Min(256, p.Source.main.maxParticles);
                p.Buffer = new ParticleSystem.Particle[cap];
                GameObject child = new GameObject(p.Source.name);
                child.transform.SetParent(_drawRoot.transform, false);
                p.Draw = child.AddComponent<ParticleSystem>();
                p.Draw.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ParticleSystem.MainModule main = p.Draw.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = cap; main.loop = true; main.simulationSpeed = 0f;
                ParticleSystem.EmissionModule emission = p.Draw.emission;
                emission.enabled = false;
                p.DrawRenderer = p.Draw.GetComponent<ParticleSystemRenderer>();
                p.DrawRenderer.sharedMaterials = p.SourceRenderer.sharedMaterials;
                p.DrawRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                p.DrawRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                p.DrawRenderer.enabled = false;
                p.Draw.Play(false);
                _parts[i] = p;
            }
            Live.Add(this);
            if (!_hooked) { _hooked = true; Camera.onPreCull += PreCull; }
        }

        static void PreCull(Camera cam)
        {
            if (cam == null || cam.orthographic || (cam.cullingMask & 1) == 0) return;
            FrameProf.S(FrameProf.S_AirKillsT);
            try
            {
                for (int i = 0; i < Live.Count; i++)
                    if (Live[i] != null && Live[i].gameObject.activeInHierarchy) Live[i].Place(cam);
            }
            finally { FrameProf.E(FrameProf.S_AirKillsT); }
        }

        void Place(Camera cam)
        {
            Vector3 eye = cam.transform.position;
            float distance = (transform.position - eye).magnitude;
            float near = Mathf.Min(Tu95Visual.ProxyU(cam.farClipPlane), cam.farClipPlane * 0.45f);
            bool proxy = distance > near;
            if (proxy && Time.unscaledTime >= _occludeAt)
            {
                _occludeAt = Time.unscaledTime + 0.25f;
                _hidden = AircraftCrashFx.TerrainHidden(eye, transform.position, distance);
            }
            float scale = proxy ? near / Mathf.Max(1f, distance) : 1f;
            float dt = Mathf.Max(0f, Time.time - _simulatedAt);
            _simulatedAt = Time.time;
            for (int i = 0; i < _parts.Length; i++)
            {
                Part p = _parts[i];
                p.Source.Simulate(dt, false, false, false);
                p.SourceRenderer.enabled = !proxy;
                p.DrawRenderer.enabled = proxy && !_hidden;
                if (!proxy || _hidden) continue;
                int count = p.Source.GetParticles(p.Buffer);
                for (int n = 0; n < count; n++)
                {
                    Vector3 real = p.World ? p.Buffer[n].position : p.Source.transform.TransformPoint(p.Buffer[n].position);
                    float size = p.Buffer[n].GetCurrentSize(p.Source);
                    Color32 color = p.Buffer[n].GetCurrentColor(p.Source);
                    p.Buffer[n].position = AircraftCrashFx.DrawPosition(real, eye, scale);
                    p.Buffer[n].startSize = size * scale;
                    p.Buffer[n].startColor = color;
                    p.Buffer[n].velocity = Vector3.zero;
                }
                p.Draw.SetParticles(p.Buffer, count);
            }
        }

        void OnDisable()
        {
            if (_drawRoot != null) _drawRoot.SetActive(false);
        }

        void OnEnable()
        {
            if (_drawRoot != null) _drawRoot.SetActive(true);
            _simulatedAt = Time.time;
        }

        void OnDestroy()
        {
            Live.Remove(this);
            if (_drawRoot != null) Destroy(_drawRoot);
        }
    }
}

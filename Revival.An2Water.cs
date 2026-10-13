// Cached float probes and a small pooled surface wake. No scene discovery here.
using UnityEngine;

namespace NextDayRevival
{
    internal static class An2Water
    {
        internal static bool Support(Vector3 at, Quaternion rotation, out float floor)
        {
            floor = float.MinValue;
            bool left = false, right = false;
            for (int i = 0; i < An2Amphibious.FloatBottomCount; i++)
            {
                Vector3 local = An2Amphibious.FloatBottom(i);
                Vector3 arm = rotation * (local * PlayerAn2.K);
                float water;
                if (!AirLanding.Water(at + arm, out water, .3f * PlayerAn2.K)) continue;
                if (local.x < 0) left = true; else right = true;
                floor = Mathf.Max(floor, water - arm.y - An2WaterCore.Draft * PlayerAn2.K);
            }
            // One-sided shallows retain wheel/ground collision, not magic buoyancy.
            return left && right;
        }
        internal static float RadarFactor(GameObject go)
        {
            if (go == null || !PlayerAn2.Contains(go)) return 1f;
            float y; Vector3 at = go.transform.position;
            return AirLanding.Water(at, out y)
                ? An2WaterCore.RadarFactor(true, (at.y - y) / PlayerAn2.K) : 1f;
        }
    }

    internal sealed class An2WaterWake
    {
        readonly ParticleSystem _spray;
        readonly ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[32];
        Vector3 _last;
        float _next;
        int _count = -1;
        bool _started;
        static Material _foam;
        internal An2WaterWake(Transform carrier)
        {
            if (_foam == null)
            {
                _foam = new Material(Shader.Find("Particles/Alpha Blended"));
                Texture2D dot = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                Color[] pixels = new Color[256];
                for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                {
                    float radius = new Vector2((x - 7.5f) / 7.5f, (z - 7.5f) / 7.5f).magnitude;
                    pixels[z * 16 + x] = new Color(1, 1, 1, Mathf.Clamp01(1 - radius));
                }
                dot.SetPixels(pixels); dot.Apply(); _foam.mainTexture = dot;
            }
            GameObject go = new GameObject("NDR_An2WaterWake");
            go.transform.SetParent(carrier, false);
            _spray = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = _spray.main;
            main.maxParticles = _particles.Length;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            ParticleSystem.EmissionModule emission = _spray.emission; emission.enabled = false;
            ParticleSystem.ShapeModule shape = _spray.shape; shape.enabled = false;
            _spray.GetComponent<ParticleSystemRenderer>().sharedMaterial = _foam;
            _spray.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            _spray.Play(); _spray.Pause();
            Fx.Apply(_spray);
        }
        internal void Tick(Transform carrier, float dt, bool dead)
        {
            Vector3 at = carrier.position;
            float speed = _started && dt > 0 ? Vector3.Distance(at, _last) / (dt * PlayerAn2.K) : 0;
            _last = at; _started = true;
            if (Time.time < _next) return;
            _next = Time.time + .1f;
            int count = Fx.Count(_particles.Length);
            float floor;
            if (count < 2 || dead || speed < .7f || speed > 45f || !An2Water.Support(at, carrier.rotation, out floor)
                || Mathf.Abs(at.y - floor) > .35f * PlayerAn2.K)
            { _spray.Clear(); return; }
            // Render-only particles on every peer follow the master's carrier.
            // All storage is allocated at model construction, not while taxiing.
            if (_count != count)
            {
                _count = count;
                ParticleSystem.MainModule main = _spray.main; main.maxParticles = count;
                _spray.Play(); _spray.Pause();
            }
            for (int i = 0; i < count; i++)
            {
                float age = (i / 2) / (float)(count / 2 - 1);
                Vector3 p = at - carrier.forward * (age * speed * .8f * PlayerAn2.K)
                    + carrier.right * ((i % 2 == 0 ? -1f : 1f) * (2f + age * .6f) * PlayerAn2.K);
                float water;
                if (!AirLanding.Water(p, out water)) { _particles[i].remainingLifetime = 0; continue; }
                p.y = water + .03f + (speed > 11f && age < .15f ? .3f * PlayerAn2.K : 0);
                _particles[i].position = p;
                _particles[i].startSize = (.2f + age * .8f) * PlayerAn2.K;
                _particles[i].startColor = new Color(.82f, .86f, .85f, .35f * (1f - age));
                _particles[i].startLifetime = _particles[i].remainingLifetime = .2f;
                _particles[i].velocity = Vector3.zero;
            }
            _spray.SetParticles(_particles, count);
        }
    }
}

using System;
using UnityEngine;

namespace NextDayRevival
{
    // Damage reports often use the aircraft centre. Keep a local engine site
    // until the pilot/master starts the fall, then send it in the fall event.
    public sealed class AircraftDamageSite : MonoBehaviour
    {
        internal Vector3 Local;
        internal Quaternion Rotation;
        internal double Clock;
        internal bool HasFallStart;
    }

    internal static class AircraftCrashFx
    {
        const int MaxTrails = 24;
        sealed class Trail
        {
            internal GameObject Aircraft, Root;
            internal Vector3 Local;
            internal ParticleSystem Flame, Smoke;
            internal float Factor = -1f, CheckAt;
            internal bool Emitting;
        }
        static readonly Trail[] Trails = new Trail[MaxTrails];
        static int _count;
        static GameObject _flame, _smoke;
        static bool _looked;

        internal static void Hit(GameObject go, Vector3 world)
        {
            if (go == null) return;
            AircraftDamageSite site = go.GetComponent<AircraftDamageSite>();
            if (site == null) site = go.AddComponent<AircraftDamageSite>();
            site.Local = EngineSite(go, world);
        }

        internal static Vector3 Site(GameObject go)
        {
            AircraftDamageSite site = go.GetComponent<AircraftDamageSite>();
            return site == null ? EngineSite(go, go.transform.position) : site.Local;
        }

        internal static void RememberFall(GameObject go, Quaternion rotation, Vector3 local, double clock)
        {
            AircraftDamageSite site = go.GetComponent<AircraftDamageSite>();
            if (site == null) site = go.AddComponent<AircraftDamageSite>();
            site.Local = local; site.Rotation = rotation; site.Clock = clock; site.HasFallStart = true;
        }

        internal static void FallStart(GameObject go, out Quaternion rotation, out Vector3 local, out double clock)
        {
            AircraftDamageSite site = go.GetComponent<AircraftDamageSite>();
            if (site != null && site.HasFallStart)
            {
                rotation = site.Rotation; local = site.Local; clock = site.Clock;
                site.HasFallStart = false;
                return;
            }
            rotation = go.transform.rotation; local = Site(go); clock = ParaPose.Clock();
        }

        static Vector3 EngineSite(GameObject go, Vector3 hit)
        {
            Transform tr = go.transform;
            Transform bomber = tr.Find("NDR_Tu95/NDR_Tu95Fly");
            if (bomber != null && Tu95Model.Props.Count > 0)
            {
                Vector3 wingHit = bomber.InverseTransformPoint(hit);
                // Preserve real wing hits inside the measured wing hulls.
                // Centre-based AA reports select the nearest engine below.
                for (int i = 0; i < Tu95Model.Boxes.Count; i++)
                {
                    Bounds b = Tu95Model.Boxes[i];
                    if (Mathf.Abs(wingHit.x) <= 1.5f || b.center.x * wingHit.x <= 0f
                        || b.size.x <= b.size.y * 4f) continue;
                    Vector3 onWing = b.ClosestPoint(wingHit);
                    if ((onWing - wingHit).sqrMagnitude <= 0.25f)
                        return tr.InverseTransformPoint(bomber.TransformPoint(onWing));
                }
                Vector3 best = Vector3.zero;
                float distance = float.MaxValue;
                for (int i = 0; i < Tu95Model.Props.Count; i++)
                {
                    Vector3 engine = bomber.TransformPoint(Tu95Model.Props[i].Value - Vector3.forward);
                    float d = (engine - hit).sqrMagnitude;
                    if (d < distance) { distance = d; best = engine; }
                }
                return tr.InverseTransformPoint(best);
            }
            Transform prop = tr.Find("NDR_An2/Pivot_prop");
            if (prop != null)
            {
                Vector3 local = tr.InverseTransformPoint(hit);
                // A rifle hit on either wing burns at that hit, rather than at
                // the nose. Centre-based AA reports fall back to the engine.
                if (Mathf.Abs(local.x) > 1.5f * PlayerAn2.K && An2ModelBodyBounds(go, ref local))
                    return local;
                return tr.InverseTransformPoint(prop.position - tr.forward * PlayerAn2.K);
            }
            float k = PlayerHeli.K;
            float side = tr.InverseTransformPoint(hit).x < 0f ? -1f : 1f;
            return new Vector3(side * 0.7f, 3.2f, -2.5f) * k;
        }

        static bool An2ModelBodyBounds(GameObject go, ref Vector3 local)
        {
            Transform body = go.transform.Find("NDR_An2/Body");
            MeshFilter mf = body == null ? null : body.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            Vector3 at = body.InverseTransformPoint(go.transform.TransformPoint(local));
            local = go.transform.InverseTransformPoint(body.TransformPoint(mf.sharedMesh.bounds.ClosestPoint(at)));
            return true;
        }

        internal static Vector3 Position(Vector3 start, Vector3 metresPerSecond, float k, double age, float terminal)
        {
            float horizontal = (float)AircraftFallCore.HorizontalTravel(age) * k;
            return start + new Vector3(metresPerSecond.x * horizontal,
                (float)AircraftFallCore.VerticalTravel(metresPerSecond.y, age, terminal) * k,
                metresPerSecond.z * horizontal);
        }

        internal static Quaternion Rotation(Quaternion start, float side, double age)
        {
            Vector3 e = start.eulerAngles;
            return Quaternion.Euler((float)AircraftFallCore.Pitch(Mathf.DeltaAngle(0f, e.x), age),
                e.y + side * 8f * (float)age,
                (float)AircraftFallCore.Bank(Mathf.DeltaAngle(0f, e.z), side, age));
        }

        internal static float Side(GameObject go, Vector3 local)
        {
            if (Mathf.Abs(local.x) > 0.01f) return local.x < 0f ? 1f : -1f;
            return (PlayerAn2.View(go) & 1) == 0 ? 1f : -1f;
        }

        static bool Templates()
        {
            if (_looked) return _flame != null && _smoke != null;
            _looked = true;
            _smoke = Resources.Load("Particles/Vehicles/VehicleSmoke_01", typeof(GameObject)) as GameObject;
            GameObject camp = Resources.Load("LootSpawn/Crafting/Crafted/5001_Spawn", typeof(GameObject)) as GameObject;
            // Clone only the native particle child: no campfire gameplay,
            // audio, multiplier, destroyer, light or legacy emitters.
            Transform flame = camp == null ? null : camp.transform.Find(
                "FireEmitObjects/CampfireElements/FireComplex_old/Flames");
            _flame = flame == null ? null : flame.gameObject;
            if (_flame == null || _smoke == null)
                RevivalPlugin.L.LogWarning("Aircraft crash: native flame/smoke resource missing.");
            return _flame != null && _smoke != null;
        }

        internal static void Start(GameObject go, Vector3 local, float scale)
        {
            if (go == null || _count >= MaxTrails) return;
            for (int i = 0; i < _count; i++) if (Trails[i].Aircraft == go) return;
            // Setup runs once per destruction, never in the frame/draw loop.
            if (!Templates()) return;
            Trail t = new Trail();
            t.Aircraft = go; t.Local = local;
            t.Root = new GameObject("NDR_AircraftCrashTrail");
            t.Root.transform.position = go.transform.TransformPoint(local);
            t.Flame = Clone(_flame, t.Root.transform, scale, 96);
            t.Smoke = Clone(_smoke, t.Root.transform, scale, 160);
            Trails[_count++] = t;
        }

        static ParticleSystem Clone(GameObject prefab, Transform root, float scale, int cap)
        {
            GameObject go = UnityEngine.Object.Instantiate(prefab) as GameObject;
            go.transform.SetParent(root, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = prefab.transform.localRotation;
            go.transform.localScale = Vector3.one * scale;
            ParticleSystem ps = go.GetComponent<ParticleSystem>();
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true; main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = cap;
            main.startLifetimeMultiplier = Mathf.Min(main.startLifetimeMultiplier, 5f);
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false; em.SetBursts(new ParticleSystem.Burst[0]);
            em.rateOverDistance = 0f;
            go.SetActive(true);
            return ps;
        }

        // Called inside the existing F6 AirKills.Tick slot. Cached systems,
        // bounded work, no Instantiate, reflection, arrays or strings here.
        internal static void Tick(float now)
        {
            if (_count == 0) return;
            Camera cam = CameraOwner.MainCamera();
            for (int i = _count - 1; i >= 0; i--)
            {
                Trail t = Trails[i];
                if (t.Aircraft == null) { Remove(i); continue; }
                Vector3 at = t.Aircraft.transform.TransformPoint(t.Local);
                t.Root.transform.position = at;
                if (now < t.CheckAt) continue;
                t.CheckAt = now + 0.25f;
                float factor = Fx.Factor;
                bool visible = factor > 0f && cam != null
                    && (cam.transform.position - at).sqrMagnitude < 100000000f;
                if (factor == t.Factor && visible == t.Emitting) continue;
                t.Factor = factor; t.Emitting = visible;
                Density(t.Flame, factor, visible, 96, 24f);
                Density(t.Smoke, factor, visible, 160, 16f);
            }
        }

        static void Density(ParticleSystem ps, float factor, bool play, int cap, float rate)
        {
            ParticleSystem.MainModule main = ps.main;
            main.maxParticles = Mathf.Max(1, Mathf.RoundToInt(cap * factor));
            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = rate * factor; em.enabled = play;
            if (play) ps.Play(false);
            else ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        internal static void Stop(GameObject go)
        {
            for (int i = _count - 1; i >= 0; i--) if (Trails[i].Aircraft == go) Remove(i);
        }

        static void Remove(int i)
        {
            Trail t = Trails[i];
            if (t.Flame != null) t.Flame.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            if (t.Smoke != null) t.Smoke.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            if (t.Root != null) UnityEngine.Object.Destroy(t.Root, 6f);
            Trails[i] = Trails[--_count]; Trails[_count] = null;
        }
    }
}

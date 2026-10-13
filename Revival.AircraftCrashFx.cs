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
        internal string KillPath;
        internal double KillClock;
    }

    internal static class AircraftCrashFx
    {
        const int MaxTrails = 24;
        const int FlameCap = 96, SmokeCap = 768;
        const float FlameRate = 64f, SmokeRate = 36f, SmokeSeconds = 20f;
        static readonly AircraftCrashEvent[] Reports = new AircraftCrashEvent[MaxTrails * 2];
        static int _reports;
        sealed class Trail
        {
            internal GameObject Aircraft, Root;
            internal Vector3 Local;
            internal ParticleSystem Flame, Smoke;
            internal ParticleSystem FlameDraw, SmokeDraw;
            internal ParticleSystemRenderer FlameRenderer, SmokeRenderer, FlameDrawRenderer, SmokeDrawRenderer;
            internal ParticleSystem.Particle[] FlameBuffer = new ParticleSystem.Particle[FlameCap];
            internal ParticleSystem.Particle[] SmokeBuffer = new ParticleSystem.Particle[SmokeCap];
            internal AircraftCrashVisual Visual;
            internal float Factor = -1f, CheckAt, OccludeAt, SimulatedAt;
            internal bool Occluded, Ended;
            internal bool Emitting;
            internal bool SmokeOnly;    // Power 3: a fuel tank wreck's column, no flame
        }
        static readonly Trail[] Trails = new Trail[MaxTrails];
        static int _count;
        static GameObject _flame, _smoke;
        static bool _looked;
        static bool _hooked;
        static int _terrainMask;
        static int _rayFrame = -1, _rayUsed;
        static int _thinkFrame = -1, _thinks;

        internal static void MarkKill(GameObject go, string path)
        {
            if (go == null) return;
            AircraftDamageSite site = go.GetComponent<AircraftDamageSite>();
            if (site == null) site = go.AddComponent<AircraftDamageSite>();
            if (site.KillPath != null) return;
            site.KillPath = path; site.KillClock = ParaPose.Clock();
        }

        internal static void BeginFall(GameObject go, float vertical, float k, double clock, float terminal)
        {
            AircraftCrashEvent report = go.GetComponent<AircraftCrashEvent>();
            if (report == null) report = go.AddComponent<AircraftCrashEvent>();
            report.Begin(vertical, k, clock, terminal);
        }

        internal static void Watch(AircraftCrashEvent report)
        {
            for (int i = 0; i < _reports; i++) if (Reports[i] == report) return;
            for (int i = _reports - 1; i >= 0; i--)
                if (Reports[i] == null || Reports[i].Finished)
                { Reports[i] = Reports[--_reports]; Reports[_reports] = null; }
            if (_reports < Reports.Length) Reports[_reports++] = report;
            else RevivalPlugin.L.LogWarning("AircraftCrash monitor capacity reached.");
        }

        internal static void Impact(GameObject go)
        {
            AircraftCrashVisual visual = go == null ? null : go.GetComponent<AircraftCrashVisual>();
            if (visual != null) visual.Stop();
            AircraftCrashEvent report = go == null ? null : go.GetComponent<AircraftCrashEvent>();
            if (report != null) report.Impact();
        }

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
                // NDR_Tu95Fly is a camera proxy, often kilometres away from
                // its carrier and scaled down. Damage must use the canonical
                // carrier frame, never that render-only transform.
                Vector3 wingHit = tr.InverseTransformPoint(hit) / PlayerAn2.K;
                // Preserve real wing hits inside the measured wing hulls.
                // Centre-based AA reports select the nearest engine below.
                for (int i = 0; i < Tu95Model.Boxes.Count; i++)
                {
                    Bounds b = Tu95Model.Boxes[i];
                    if (Mathf.Abs(wingHit.x) <= 1.5f || b.center.x * wingHit.x <= 0f
                        || b.size.x <= b.size.y * 4f) continue;
                    Vector3 onWing = b.ClosestPoint(wingHit);
                    if ((onWing - wingHit).sqrMagnitude <= 0.25f)
                        return onWing * PlayerAn2.K;
                }
                Vector3 best = Vector3.zero;
                float distance = float.MaxValue;
                for (int i = 0; i < Tu95Model.Props.Count; i++)
                {
                    Vector3 engine = tr.TransformPoint((Tu95Model.Props[i].Value - Vector3.forward) * PlayerAn2.K);
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
            Begin(go, local, scale, false);
        }

        /// <summary>Power 3: the same plume as a smoke column only (no flame,
        /// no aircraft visual) on a static anchor, e.g. a destroyed fuel tank.
        /// Stop(anchor) lets it fade out.</summary>
        internal static void StartSmoke(GameObject anchor, float scale)
        {
            Begin(anchor, Vector3.zero, scale, true);
        }

        static void Begin(GameObject go, Vector3 local, float scale, bool smokeOnly)
        {
            if (go == null) return;
            if (_count >= MaxTrails)
            {
                // Expired falls must not consume the next aircraft's budget.
                for (int i = 0; i < _count; i++)
                {
                    if (!Trails[i].Ended) continue;
                    UnityEngine.Object.Destroy(Trails[i].Root);
                    Trails[i] = Trails[--_count]; Trails[_count] = null;
                    break;
                }
            }
            if (_count >= MaxTrails || !Templates())
            {
                AircraftCrashEvent failed = go.GetComponent<AircraftCrashEvent>();
                if (failed != null) failed.FxStarted(0);
                return;
            }
            for (int i = 0; i < _count; i++) if (Trails[i].Aircraft == go) return;
            // Setup runs once per destruction, never in the frame/draw loop.
            Terrain[] terrains = Terrain.activeTerrains;
            _terrainMask = 0;
            for (int i = 0; terrains != null && i < terrains.Length; i++)
                if (terrains[i] != null) _terrainMask |= 1 << terrains[i].gameObject.layer;
            Trail t = new Trail();
            t.Aircraft = go; t.Local = local;
            t.Root = new GameObject("NDR_AircraftCrashTrail");
            t.Root.transform.position = go.transform.TransformPoint(local);
            t.SmokeOnly = smokeOnly;
            t.Visual = smokeOnly ? null : AircraftCrashVisual.Attach(go);
            t.SimulatedAt = Time.time;
            t.Flame = Clone(_flame, t.Root.transform, FlameCap, false);
            t.Smoke = Clone(_smoke, t.Root.transform, SmokeCap, true);
            // Render-only twins keep native materials and billboard animation.
            // Simulation remains at the real engine in world space. A camera
            // projects cached particles just as Tu95Visual projects its mesh.
            t.FlameDraw = Clone(_flame, t.Root.transform, FlameCap, false);
            t.SmokeDraw = Clone(_smoke, t.Root.transform, SmokeCap, true);
            Freeze(t.FlameDraw); Freeze(t.SmokeDraw);
            t.FlameRenderer = t.Flame.GetComponent<ParticleSystemRenderer>();
            t.SmokeRenderer = t.Smoke.GetComponent<ParticleSystemRenderer>();
            t.FlameDrawRenderer = t.FlameDraw.GetComponent<ParticleSystemRenderer>();
            t.SmokeDrawRenderer = t.SmokeDraw.GetComponent<ParticleSystemRenderer>();
            t.FlameRenderer.enabled = t.SmokeRenderer.enabled = false;
            t.FlameDrawRenderer.enabled = t.SmokeDrawRenderer.enabled = false;
            Trails[_count++] = t;
            if (!_hooked) { _hooked = true; Camera.onPreCull += PreCull; }
            Tick(Time.time);
            AircraftCrashEvent report = go.GetComponent<AircraftCrashEvent>();
            if (report != null) report.FxStarted(4);
        }

        static ParticleSystem Clone(GameObject prefab, Transform root, int cap, bool smoke)
        {
            GameObject go = UnityEngine.Object.Instantiate(prefab) as GameObject;
            go.transform.SetParent(root, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = prefab.transform.localRotation;
            go.transform.localScale = Vector3.one;
            ParticleSystem ps = go.GetComponent<ParticleSystem>();
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true; main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.maxParticles = cap;
            main.startLifetime = smoke ? SmokeSeconds : 1.2f;
            main.startSpeed = smoke ? 5f : 1f;
            main.startColor = smoke ? new Color(0.12f, 0.12f, 0.12f, 0.9f) : Color.white;
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false; em.SetBursts(new ParticleSystem.Burst[0]);
            em.rateOverDistance = 0f;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Keep native startSize/size-over-life. Neither the aircraft scale
            // nor distance may enlarge a particle quad or its screen footprint.
            renderer.minParticleSize = 0f;
            go.SetActive(true);
            return ps;
        }

        static void Freeze(ParticleSystem ps)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpeed = 0f;
            ps.Play(false);
        }

        // Called inside the existing F6 AirKills.Tick slot. Cached systems,
        // bounded work, no Instantiate, reflection, arrays or strings here.
        internal static void Tick(float now)
        {
            // Aircraft behaviour activation cannot starve the diagnostic. Also
            // runs with ParticleDensity Off, missing templates or no FX trails.
            if (_reports != 0)
            {
                double clock = ParaPose.Clock();
                for (int i = _reports - 1; i >= 0; i--)
                {
                    AircraftCrashEvent report = Reports[i];
                    if (report != null) report.Sample(clock);
                    if (report != null && !report.Finished) continue;
                    Reports[i] = Reports[--_reports]; Reports[_reports] = null;
                }
            }
            if (_count == 0) return;
            Camera cam = CameraOwner.MainCamera();
            int frame = Time.frameCount;
            if (_thinkFrame != frame) { _thinkFrame = frame; _thinks = 0; }
            for (int i = _count - 1; i >= 0; i--)
            {
                Trail t = Trails[i];
                // This Unity version has no cullingMode API. Manual simulation
                // of paused emitters guarantees the off-screen world trail.
                float dt = Mathf.Max(0f, now - t.SimulatedAt);
                t.SimulatedAt = now;
                t.Flame.Simulate(dt, false, false, false);
                t.Smoke.Simulate(dt, false, false, false);
                if (t.Ended)
                {
                    if (now >= t.CheckAt)
                    {
                        UnityEngine.Object.Destroy(t.Root);
                        Trails[i] = Trails[--_count]; Trails[_count] = null;
                    }
                    continue;
                }
                if (t.Aircraft == null) { Remove(i); continue; }
                Vector3 at = t.Aircraft.transform.TransformPoint(t.Local);
                t.Root.transform.position = at;
                if (now < t.CheckAt || _thinks >= 1) continue;
                _thinks++;
                t.CheckAt = now + ThinkInterval(cam, at, t.Occluded);
                float factor = Fx.Factor;
                bool visible = factor > 0f;
                if (factor == t.Factor && visible == t.Emitting) continue;
                t.Factor = factor; t.Emitting = visible;
                Density(t.Flame, factor, visible && !t.SmokeOnly, FlameCap, FlameRate);
                Density(t.Smoke, factor, visible, SmokeCap, SmokeRate);
            }
        }

        static float ThinkInterval(Camera cam, Vector3 at, bool hidden)
        {
            if (cam == null || hidden) return 5f;
            Vector3 view = cam.WorldToViewportPoint(at);
            // Margin includes a trail whose emitter just left the viewport.
            if (view.z <= 0f || view.x < -0.5f || view.x > 1.5f
                || view.y < -0.5f || view.y > 1.5f) return 5f;
            float near = 1500f * PlayerAn2.K;
            return (at - cam.transform.position).sqrMagnitude <= near * near ? 0.25f : 1f;
        }

        // Called by the camera after fall transforms moved. Cached buffers and
        // capped particles; no collections, materials or objects allocated.
        static void PreCull(Camera cam)
        {
            if (_count == 0 || cam == null || cam.orthographic || (cam.cullingMask & 1) == 0) return;
            FrameProf.S(FrameProf.S_AirKillsT);
            try
            {
                Vector3 eye = cam.transform.position;
                for (int i = 0; i < _count; i++)
                {
                    Trail t = Trails[i];
                    if (t.Root == null) continue;
                    Vector3 at = t.Aircraft == null ? t.Root.transform.position
                        : t.Aircraft.transform.TransformPoint(t.Local);
                    t.Root.transform.position = at;
                    float distance = (at - eye).magnitude;
                    float near = Mathf.Min(Tu95Visual.ProxyU(cam.farClipPlane), cam.farClipPlane * 0.45f);
                    bool proxy = distance > near;
                    bool hidden = proxy && Occluded(t, cam, eye, at, distance);
                    if (t.Visual != null) t.Visual.Place(cam, hidden);
                    t.FlameRenderer.enabled = t.SmokeRenderer.enabled = !proxy && !hidden;
                    t.FlameDrawRenderer.enabled = t.SmokeDrawRenderer.enabled = proxy && !hidden;
                    if (!proxy || hidden) continue;
                    float scale = near / Mathf.Max(1f, distance);
                    Project(t.Flame, t.FlameDraw, t.FlameBuffer, eye, scale);
                    Project(t.Smoke, t.SmokeDraw, t.SmokeBuffer, eye, scale);
                }
            }
            finally { FrameProf.E(FrameProf.S_AirKillsT); }
        }

        internal static Vector3 DrawPosition(Vector3 real, Vector3 eye, float scale)
        {
            return eye + (real - eye) * scale;
        }

        static void Project(ParticleSystem source, ParticleSystem draw, ParticleSystem.Particle[] buffer,
            Vector3 eye, float scale)
        {
            int n = source.GetParticles(buffer);
            for (int i = 0; i < n; i++)
            {
                buffer[i].position = DrawPosition(buffer[i].position, eye, scale);
                buffer[i].startSize *= scale;
                buffer[i].velocity = Vector3.zero;
            }
            draw.SetParticles(buffer, n);
        }

        static bool Occluded(Trail t, Camera cam, Vector3 eye, Vector3 at, float distance)
        {
            float now = Time.unscaledTime;
            if (now < t.OccludeAt) return t.Occluded;
            bool hidden;
            if (!TryTerrainHidden(eye, at, distance, out hidden)) return t.Occluded;
            t.OccludeAt = now + ThinkInterval(cam, at, hidden);
            t.Occluded = hidden;
            return t.Occluded;
        }

        internal static bool TryTerrainHidden(Vector3 eye, Vector3 at, float distance, out bool hidden)
        {
            hidden = false;
            if (_terrainMask == 0 || distance <= 128f) return true;
            // One terrain cast per frame shared by falling trails and impacts,
            // including multiple cameras. Exhaustion preserves cached visibility.
            int frame = Time.frameCount;
            if (_rayFrame != frame) { _rayFrame = frame; _rayUsed = 0; }
            if (_rayUsed >= 1) return false;
            _rayUsed++;
            hidden = Physics.Raycast(eye,
                (at - eye) / distance, distance - 120f, _terrainMask, QueryTriggerInteraction.Ignore);
            return true;
        }

        static void Density(ParticleSystem ps, float factor, bool play, int cap, float rate)
        {
            ParticleSystem.MainModule main = ps.main;
            main.maxParticles = Mathf.Max(1, Mathf.RoundToInt(cap * factor));
            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = rate * factor; em.enabled = play;
            if (play) { ps.Play(false); ps.Pause(false); }
            else ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        internal static void Stop(GameObject go)
        {
            for (int i = _count - 1; i >= 0; i--) if (Trails[i].Aircraft == go) Remove(i);
        }

        static void Remove(int i)
        {
            Trail t = Trails[i];
            if (t.Ended) return;
            if (t.Flame != null) t.Flame.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            if (t.Smoke != null) t.Smoke.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            ParticleSystem.EmissionModule flameEmission = t.Flame.emission;
            ParticleSystem.EmissionModule smokeEmission = t.Smoke.emission;
            flameEmission.enabled = smokeEmission.enabled = false;
            // Leave smoke in the sky after impact. Tick retires the detached
            // simulation only after its longest native particle has expired.
            t.Ended = true; t.Aircraft = null; t.CheckAt = Time.time + SmokeSeconds;
        }
    }

    // Three event records per destruction. No strings or arrays in the wait
    // frames; the 2-second sample reads the actual carrier, not its proxy.
    public sealed class AircraftCrashEvent : MonoBehaviour
    {
        float _height, _previousHeight, _k;
        double _clock, _killClock, _previousClock, _sampleAt;
        int _view;
        bool _begun, _sampled, _impact, _logged;
        string _path, _name;

        internal void Begin(float vertical, float k, double clock, float terminal)
        {
            if (_begun) return;
            _begun = true;
            _height = _previousHeight = transform.position.y; _k = k;
            _clock = _previousClock = clock;
            _sampleAt = ParaPose.Clock() + 0.25;
            _view = PlayerAn2.View(gameObject);
            NpcAircraft.Flight flight = NpcAircraft.Find(gameObject);
            _name = flight == null || string.IsNullOrEmpty(flight.Label) ? gameObject.name : flight.Label;
            AircraftDamageSite site = GetComponent<AircraftDamageSite>();
            _path = site == null || site.KillPath == null ? "fall-event" : site.KillPath;
            _killClock = site == null || site.KillPath == null ? clock : site.KillClock;
            enabled = false;
            AircraftCrashFx.Watch(this);
        }

        internal void FxStarted(int systems)
        {
            if (_logged) return;
            _logged = true;
            RevivalPlugin.L.LogInfo("AircraftCrash kill: name=" + _name
                + " view=" + _view + " path=" + _path
                + " fall=" + (_begun ? "yes" : "no") + " fx=" + systems
                + " sim=World scale=1.0 nativeSize=yes density=" + Fx.Level);
        }

        internal bool Finished { get { return _sampled || _impact; } }

        internal void Sample(double now)
        {
            if (!_begun || _sampled || _impact) return;
            double age = now - _clock;
            if (now < _sampleAt && age < 2.0) return;
            _sampleAt = now + 0.25;
            float height = transform.position.y;
            double vertical = now > _previousClock
                ? (height - _previousHeight) / ((now - _previousClock) * _k) : 0.0;
            _previousHeight = height; _previousClock = now;
            if (age < 2.0) return;
            _sampled = true;
            RevivalPlugin.L.LogInfo("AircraftCrash after2s: name=" + _name
                + " view=" + _view + " sample_s=" + age.ToString("0.00") + " lost_m="
                + ((_height - transform.position.y) / _k).ToString("0.0")
                + " vertical_mps=" + vertical.ToString("0.0"));
        }

        internal void Impact()
        {
            if (_impact) return;
            // Flush a due 2s sample if impact precedes this frame's central tick.
            if (!_sampled && _begun) Sample(ParaPose.Clock());
            _impact = true; enabled = false;
            RevivalPlugin.L.LogInfo("AircraftCrash impact: name=" + _name
                + " view=" + _view + " seconds="
                + Math.Max(0.0, ParaPose.Clock() - _killClock).ToString("0.00")
                + " fall_seconds=" + Math.Max(0.0, ParaPose.Clock() - _clock).ToString("0.00")
                + " lost_m=" + ((_height - transform.position.y) / _k).ToString("0.0"));
        }
    }
}

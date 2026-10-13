// Next Day: Survival - Revival Toolkit
//
// POWER 4: AIRFIELD LIGHTS. Consumer "lights" of the power network (Power 2,
// docs/ai/POWER.md). Every declared site (Revival.AirfieldLightsData.cs) gets
//   - the runway edge lights of its props scene: the shipped vanilla-style
//     models (east_af_props, retained by AirfieldObjectsCore since Power 4);
//     the lens of every intact light swaps to a glowing lens, plus one merged
//     glow mesh for the whole runway (one renderer, no real light);
//   - wall lamps over doors and a red roof beacon, pressed onto the real
//     wall/roof by one short ray at bind time;
//   - floodlights on the existing power poles.
// The lamp bodies are low-poly boxes in the game's own kit material (the
// anchor building's metal, as the bunker's door sign); no new textures.
//
// ON/OFF. A site's lamps burn while [AirfieldLights] Enabled, the sun is down
// (with hysteresis) and PowerNet says the site feeding "lights" has power.
// The power state is master-authoritative and synced, the sun is the game's,
// so every client shows the same. Only the declared Real lamps (at most
// AirfieldLightsCore.MaxReal per site; 7 on the main airfield) carry a
// real-time light, shadowless, and only while the local player is within
// [AirfieldLights] NearMetres of the site. The rest is emissive only.
//
// COST. Per frame: one float compare (F6 "AirfieldLights.Tick"). Every 2 s
// (and at once after a PowerNet change): the sun read and one distance test
// per site; renderers and lights are touched only when a site's state
// changes, swapping prebuilt material arrays (no allocation). Binding runs
// once per content scene load.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class AirfieldLights
    {
        const float K = AirfieldLightsCore.K;
        const string G = "AirfieldLights";

        internal static ConfigEntry<bool> CfgEnabled, CfgReal;
        internal static ConfigEntry<float> CfgNear;

        sealed class Lamp
        {
            internal int Site;
            internal string Name;
            internal Renderer R;
            internal Material[] On, Off;   // swapped on change (null: toggle R.enabled)
            internal Light L;
        }

        static readonly LightSite[] Sites = AirfieldLightsData.Sites;
        static readonly List<Lamp> _lamps = new List<Lamp>();
        static readonly int[] _state = new int[Sites.Length];
        static float _next, _nextSun;
        static bool _night;
        static Light _sun;
        static Material _body;
        static readonly Material[] _lensOn = new Material[AirfieldLightsCore.KindCount];
        static readonly Material[] _lensOff = new Material[AirfieldLightsCore.KindCount];
        static readonly Material[][] _arrOn = new Material[AirfieldLightsCore.KindCount][];
        static readonly Material[][] _arrOff = new Material[AirfieldLightsCore.KindCount][];
        static readonly Mesh[] _mesh = new Mesh[AirfieldLightsCore.KindCount];
        static readonly RaycastHit[] _hits = new RaycastHit[16];

        static readonly Color[] LensColor = {
            new Color(1f, 0.86f, 0.62f),     // door: incandescent
            new Color(0.95f, 0.93f, 0.84f),  // flood: mercury white
            new Color(1f, 0.14f, 0.08f),     // beacon: obstruction red
            new Color(1f, 0.9f, 0.7f),       // edge: runway white
        };

        internal static bool On { get { return CfgEnabled == null || CfgEnabled.Value; } }

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind(G, "Enabled", true,
                "Airfield lights (runway edge lights, door lamps, pole floodlights, tower beacon) burn at night while the power network feeds them.");
            CfgReal = cfg.Bind(G, "RealLights", true,
                "A few lamps (pole floodlights, three door lamps) also light their surroundings with a shadowless real-time light; off = emissive lenses only.");
            CfgNear = cfg.Bind(G, "NearMetres", AirfieldLightsCore.NearMetres,
                "Real-time lights are switched on only while you are within this many metres of the lit site.");
            for (int s = 0; s < Sites.Length; s++)
            {
                string bad = AirfieldLightsCore.Problem(Sites[s]);
                if (bad != null) RevivalPlugin.L.LogWarning("AirfieldLights: " + bad);
            }
        }

        // ------------------------------------------------------------ users

        /// <summary>PowerNet.Apply: a generator changed state - apply at once.</summary>
        internal static void PowerChanged() { _next = 0f; }

        // ------------------------------------------------------------ binding

        /// <summary>Scene load (AirfieldGround.Loaded): bind the lamps that
        /// live in this content scene.</summary>
        internal static void Loaded(Scene scene)
        {
            if (!On) return;
            try
            {
                Purge();
                int before = _lamps.Count;
                for (int s = 0; s < Sites.Length; s++)
                {
                    LightSite site = Sites[s];
                    if (AirfieldLightsCore.Problem(site) != null) continue;
                    if (site.PropsScene == scene.name) BindEdges(s, scene);
                    for (int i = 0; i < site.Lamps.Length; i++)
                        if (site.Lamps[i].Scene == scene.name && !Bound(site.Lamps[i].Name)) BindLamp(s, site.Lamps[i], scene);
                }
                if (_lamps.Count == before) return;
                for (int s = 0; s < _state.Length; s++) _state[s] = -1;
                _next = 0f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("AirfieldLights: binding " + scene.name + " failed: " + ex.Message);
            }
        }

        static void Purge()
        {
            for (int i = _lamps.Count - 1; i >= 0; i--)
                if (_lamps[i].R == null) _lamps.RemoveAt(i);
        }

        static bool Bound(string name)
        {
            for (int i = 0; i < _lamps.Count; i++) if (_lamps[i].Name == name && _lamps[i].R != null) return true;
            return false;
        }

        static Transform Anchor(Scene scene, string path)
        {
            string root, child;
            if (!AirfieldLightsCore.SplitAnchor(path, out root, out child)) return null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
                if (roots[r].name == root) return child.Length == 0 ? roots[r].transform : roots[r].transform.Find(child);
            for (int r = 0; r < roots.Length; r++)
            {
                Transform t = child.Length == 0 ? null : roots[r].transform.Find(child);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>The runway edge lights: the lens slot (the model's last
        /// material) of every intact light, and one merged glow mesh.</summary>
        static void BindEdges(int s, Scene scene)
        {
            LightSite site = Sites[s];
            if (Bound(site.Id + " runway glow")) return;
            Transform box = Anchor(scene, site.EdgeContainer);
            if (box == null) { RevivalPlugin.L.LogWarning("AirfieldLights: " + site.EdgeContainer + " not in " + scene.name + "."); return; }
            Material lit = LensOn(AirfieldLightsCore.Edge);
            List<Vector3> glow = new List<Vector3>();
            Transform keeper = null;
            int snapped = 0;
            for (int c = 0; c < box.childCount; c++)
            {
                Transform model = box.GetChild(c);
                if (!model.name.StartsWith(site.EdgePrefix, StringComparison.Ordinal)) continue;
                MeshRenderer[] rs = model.GetComponentsInChildren<MeshRenderer>(true);
                MeshRenderer lens = null;
                bool intact = true;
                for (int i = 0; i < rs.Length; i++)
                {
                    MeshFilter mf = rs[i].GetComponent<MeshFilter>();
                    string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : rs[i].name;
                    if (!AirfieldLightsCore.Intact(mesh) || !AirfieldLightsCore.Intact(rs[i].name)) intact = false;
                    else if (lens == null && rs[i].sharedMaterials.Length >= 2) lens = rs[i];
                }
                if (!intact || lens == null) { snapped++; continue; }
                if (_body == null) Body(lens.sharedMaterials[1]);
                Material[] off = lens.sharedMaterials, on = (Material[])off.Clone();
                on[on.Length - 1] = lit;
                Lamp l = new Lamp();
                l.Site = s; l.Name = site.Id + " " + model.name; l.R = lens; l.On = on; l.Off = off;
                _lamps.Add(l);
                glow.Add(model.position + Vector3.up * (0.42f * K));
                if (keeper == null) keeper = model;
            }
            if (glow.Count > 0)
            {
                // Under a retained edge light model: AirfieldObjectsJob keeps a
                // retained model's descendants (any other child of the props
                // containers would be taken for a removed model and hidden).
                GameObject g = new GameObject("NDR runway glow");
                g.transform.SetParent(keeper, false);
                g.transform.position = Vector3.zero;
                g.transform.rotation = Quaternion.identity;
                MeshFilter f = g.AddComponent<MeshFilter>();
                f.sharedMesh = GlowMesh(glow, g.transform);
                MeshRenderer r = g.AddComponent<MeshRenderer>();
                r.sharedMaterial = lit;
                Quiet(r);
                r.enabled = false;
                Lamp l = new Lamp();
                l.Site = s; l.Name = site.Id + " runway glow"; l.R = r;
                _lamps.Add(l);
            }
            RevivalPlugin.L.LogInfo("AirfieldLights: " + site.Id + " runway: " + glow.Count + " edge light(s) wired, "
                + snapped + " snapped (dark), one glow mesh.");
        }

        static void BindLamp(int s, LampDecl d, Scene scene)
        {
            Transform anchor = Anchor(scene, d.Anchor);
            if (anchor == null) { RevivalPlugin.L.LogWarning("AirfieldLights: " + d.Name + ": " + d.Anchor + " not in " + scene.name + "."); return; }
            if (_body == null) Body(Donor(anchor));
            Vector3 at, forward;
            if (!Mount(d, anchor, out at, out forward))
            {
                RevivalPlugin.L.LogWarning("AirfieldLights: " + d.Name + ": no surface of " + scene.name + " at the declared spot - lamp left out.");
                return;
            }
            GameObject go = new GameObject("NDR lamp " + d.Name);
            go.transform.SetParent(anchor, true);
            go.transform.position = at;
            go.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            go.AddComponent<MeshFilter>().sharedMesh = LampMesh(d.Kind);
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            Quiet(r);
            Lamp l = new Lamp();
            l.Site = s; l.Name = d.Name; l.R = r; l.On = Arr(d.Kind, true); l.Off = Arr(d.Kind, false);
            r.sharedMaterials = l.Off;
            if (d.Real) l.L = RealLight(d, go.transform, at);
            _lamps.Add(l);
        }

        /// <summary>Where the lamp sits and which way it faces (world).</summary>
        static bool Mount(LampDecl d, Transform anchor, out Vector3 at, out Vector3 forward)
        {
            if (d.Mount == AirfieldLightsCore.Pole)
            {
                Vector3 foot = anchor.position;
                float yaw = AirfieldLightsCore.AimYaw(foot.x, foot.z, d.AimX, d.AimZ);
                forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                at = foot + Vector3.up * (d.Y * K);
                return true;
            }
            Quaternion rot = Quaternion.Euler(0f, anchor.eulerAngles.y, 0f);
            Vector3 p = anchor.position + rot * (new Vector3(d.X, d.Y, d.Z) * K);
            float fx, fz;
            AirfieldLightsCore.Facing(d.Yaw, out fx, out fz);
            forward = rot * new Vector3(fx, 0f, fz);
            Vector3 from, dir;
            float reach;
            if (d.Mount == AirfieldLightsCore.Roof)
            {
                from = p + Vector3.up * (AirfieldLightsCore.RoofProbeM * K);
                dir = Vector3.down; reach = 2f * AirfieldLightsCore.RoofProbeM * K;
            }
            else
            {
                from = p + forward * (AirfieldLightsCore.WallProbeM * K);
                dir = -forward; reach = 2f * AirfieldLightsCore.WallProbeM * K;
            }
            at = p;
            int n = Physics.RaycastNonAlloc(from, dir, _hits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Scene own = anchor.gameObject.scene;
            for (int i = 0; i < n; i++)
                if (_hits[i].collider != null && _hits[i].collider.gameObject.scene == own && _hits[i].distance < best)
                { best = _hits[i].distance; at = _hits[i].point; }
            return best < float.MaxValue;
        }

        static Light RealLight(LampDecl d, Transform lamp, Vector3 at)
        {
            GameObject go = new GameObject("NDR lamp light");
            go.transform.SetParent(lamp, false);
            Light l = go.AddComponent<Light>();
            l.shadows = LightShadows.None;
            l.color = LensColor[d.Kind];
            if (d.Kind == AirfieldLightsCore.Flood)
            {
                go.transform.localPosition = new Vector3(0f, 0.16f, 1.25f) * K;
                Vector3 aim = new Vector3(d.AimX, at.y - d.Y * K, d.AimZ);
                go.transform.rotation = Quaternion.LookRotation(aim - go.transform.position, Vector3.up);
                l.type = LightType.Spot;
                l.spotAngle = 80f;
                l.range = 18f * K;
                l.intensity = 1.5f;
            }
            else
            {
                go.transform.localPosition = new Vector3(0f, -0.08f, 0.3f) * K;
                l.type = LightType.Point;
                l.range = 5f * K;
                l.intensity = 1.1f;
            }
            l.enabled = false;
            return l;
        }

        // ------------------------------------------------------------ tick

        internal static void Tick()
        {
            if (_lamps.Count == 0 || Time.time < _next) return;   // per frame: this compare only
            _next = Time.time + AirfieldLightsCore.StepSeconds;
            try { Step(); }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirfieldLights: " + ex.Message); }
        }

        static void Step()
        {
            Light sun = Sun();
            _night = AirfieldLightsCore.Night(_night, sun != null, sun != null ? sun.transform.forward.y : 0f,
                sun != null ? sun.intensity : 0f);
            bool real = CfgReal == null || CfgReal.Value;
            float near = CfgNear == null ? AirfieldLightsCore.NearMetres : Mathf.Max(0f, CfgNear.Value);
            for (int s = 0; s < Sites.Length; s++)
            {
                LightSite site = Sites[s];
                bool lit = AirfieldLightsCore.Lit(On, _night, PowerNet.SiteHas(site.Id, PowerNetCore.Lights));
                bool close = CombatLoad.LocalNear(new Vector3(site.X, 0f, site.Z), near);
                bool realOn = AirfieldLightsCore.RealOn(lit, true, real, close);
                int st = AirfieldLightsCore.State(lit, realOn);
                if (st == _state[s]) continue;
                bool wasLit = _state[s] >= 0 && (_state[s] & 1) != 0;
                _state[s] = st;
                Apply(s, lit, realOn);
                if (lit != wasLit) RevivalPlugin.L.LogInfo("AirfieldLights: " + site.Id + " lights " + (lit ? "on" : "off") + ".");
            }
        }

        static void Apply(int s, bool lit, bool realOn)
        {
            for (int i = 0; i < _lamps.Count; i++)
            {
                Lamp l = _lamps[i];
                if (l.Site != s || l.R == null) continue;
                if (l.On == null) { if (l.R.enabled != lit) l.R.enabled = lit; }
                else l.R.sharedMaterials = lit ? l.On : l.Off;
                if (l.L != null && l.L.enabled != realOn) l.L.enabled = realOn;
            }
        }

        /// <summary>The scene's sun (RenderSettings.sun, else the brightest
        /// directional light); searched at most every 10 s while missing.</summary>
        static Light Sun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            _sun = RenderSettings.sun;
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            if (Time.time < _nextSun) return null;
            _nextSun = Time.time + 10f;
            Light[] all = UnityEngine.Object.FindObjectsOfType<Light>();
            float best = -1f;
            _sun = null;
            for (int i = 0; i < all.Length; i++)
                if (all[i].type == LightType.Directional && all[i].intensity > best) { best = all[i].intensity; _sun = all[i]; }
            return _sun;
        }

        // ------------------------------------------------------------ looks

        static void Quiet(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>The anchor building's own metal kit material (as the
        /// bunker's door sign), else its first material.</summary>
        static Material Donor(Transform anchor)
        {
            Renderer[] rs = anchor.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Material m = rs[i].sharedMaterial;
                if (m != null && m.name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0) return m;
            }
            return rs.Length > 0 ? rs[0].sharedMaterial : null;
        }

        static void Body(Material donor)
        {
            _body = donor != null ? new Material(donor) : new Material(Shader.Find("Standard"));
            _body.name = "NDR lamp body";
            _body.color = new Color(0.4f, 0.43f, 0.4f);
            if (_body.HasProperty("_EmissionColor")) { _body.DisableKeyword("_EMISSION"); _body.SetColor("_EmissionColor", Color.black); }
        }

        /// <summary>The burning lens: unlit colour (emissive Standard where
        /// the unlit shader is not in the build).</summary>
        static Material LensOn(int kind)
        {
            if (_lensOn[kind] != null) return _lensOn[kind];
            Color c = LensColor[kind];
            Shader unlit = Shader.Find("Unlit/Color");
            Material m;
            if (unlit != null) { m = new Material(unlit); m.color = c; }
            else
            {
                m = new Material(Shader.Find("Standard"));
                m.color = c;
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 1.5f);
            }
            m.name = "NDR lamp lens on " + AirfieldLightsCore.KindName[kind];
            _lensOn[kind] = m;
            return m;
        }

        /// <summary>The dark lens: dull glass from the body material.</summary>
        static Material LensOff(int kind)
        {
            if (_lensOff[kind] != null) return _lensOff[kind];
            Material m = _body != null ? new Material(_body) : new Material(Shader.Find("Standard"));
            m.name = "NDR lamp lens off " + AirfieldLightsCore.KindName[kind];
            m.mainTexture = null;
            m.color = kind == AirfieldLightsCore.Beacon ? new Color(0.32f, 0.07f, 0.05f) : new Color(0.58f, 0.57f, 0.5f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.8f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            _lensOff[kind] = m;
            return m;
        }

        static Material[] Arr(int kind, bool on)
        {
            Material[][] cache = on ? _arrOn : _arrOff;
            if (cache[kind] == null) cache[kind] = new Material[] { _body, on ? LensOn(kind) : LensOff(kind) };
            return cache[kind];
        }

        // ------------------------------------------------------------ meshes

        /// <summary>Lamp meshes (metres, +z out from the wall / toward the aim,
        /// submesh 0 body, 1 lens), built once per kind.</summary>
        static Mesh LampMesh(int kind)
        {
            if (_mesh[kind] != null) return _mesh[kind];
            Boxes b = new Boxes();
            if (kind == AirfieldLightsCore.Door)
            {
                // Wall bracket lamp: plate, arm, flat shade, glass under it.
                b.Add(0, new Vector3(0f, 0f, 0.015f), new Vector3(0.14f, 0.2f, 0.03f), 0f);
                b.Add(0, new Vector3(0f, 0.04f, 0.13f), new Vector3(0.04f, 0.04f, 0.22f), 0f);
                b.Add(0, new Vector3(0f, 0.02f, 0.3f), new Vector3(0.24f, 0.05f, 0.24f), 0f);
                b.Add(1, new Vector3(0f, -0.06f, 0.3f), new Vector3(0.16f, 0.11f, 0.16f), 0f);
            }
            else if (kind == AirfieldLightsCore.Flood)
            {
                // Pole floodlight: clamp round the pole, rising arm, tilted head, glass underneath.
                b.Add(0, new Vector3(0f, 0f, 0f), new Vector3(0.36f, 0.16f, 0.36f), 0f);
                b.Add(0, new Vector3(0f, 0.11f, 0.62f), new Vector3(0.07f, 0.07f, 0.92f), -14f);
                b.Add(0, new Vector3(0f, 0.25f, 1.25f), new Vector3(0.34f, 0.16f, 0.62f), 10f);
                b.Add(1, new Vector3(0f, 0.16f, 1.27f), new Vector3(0.27f, 0.03f, 0.5f), 10f);
            }
            else
            {
                // Roof obstruction beacon: base and red glass.
                b.Add(0, new Vector3(0f, 0.04f, 0f), new Vector3(0.22f, 0.08f, 0.22f), 0f);
                b.Add(1, new Vector3(0f, 0.16f, 0f), new Vector3(0.15f, 0.17f, 0.15f), 0f);
            }
            Mesh m = b.Mesh("NDR lamp " + AirfieldLightsCore.KindName[kind]);
            _mesh[kind] = m;
            return m;
        }

        /// <summary>One mesh of small glow octahedra at the given world
        /// points, local to the holder.</summary>
        static Mesh GlowMesh(List<Vector3> at, Transform holder)
        {
            float r = 0.24f * K;
            Vector3[] v = new Vector3[at.Count * 6];
            int[] t = new int[at.Count * 24];
            Vector3[] o = { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            int[] faces = { 0, 4, 2, 0, 2, 5, 0, 5, 3, 0, 3, 4, 1, 2, 4, 1, 5, 2, 1, 3, 5, 1, 4, 3 };
            for (int i = 0; i < at.Count; i++)
            {
                Vector3 c = holder.InverseTransformPoint(at[i]);
                for (int k = 0; k < 6; k++) v[i * 6 + k] = c + o[k] * r;
                for (int k = 0; k < 24; k++) t[i * 24 + k] = i * 6 + faces[k];
            }
            Mesh m = new Mesh();
            m.name = "NDR runway glow";
            m.vertices = v;
            m.triangles = t;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Box mesh builder: two submeshes, per-face normals, UVs
        /// one tile per metre (the kit materials' measured repeat).</summary>
        sealed class Boxes
        {
            readonly List<Vector3> _v = new List<Vector3>();
            readonly List<Vector3> _n = new List<Vector3>();
            readonly List<Vector2> _uv = new List<Vector2>();
            readonly List<int>[] _t = { new List<int>(), new List<int>() };

            internal void Add(int sub, Vector3 centre, Vector3 size, float pitch)
            {
                Quaternion q = Quaternion.Euler(pitch, 0f, 0f);
                Vector3 h = size * 0.5f;
                for (int f = 0; f < 6; f++)
                {
                    int axis = f / 2;
                    float sign = (f % 2 == 0) ? 1f : -1f;
                    Vector3 n = Vector3.zero; n[axis] = sign;
                    Vector3 u = Vector3.zero, w = Vector3.zero;
                    u[(axis + 1) % 3] = 1f; w[(axis + 2) % 3] = 1f;
                    if (sign < 0f) { Vector3 tmp = u; u = w; w = tmp; }
                    int baseIndex = _v.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        float a = (k == 1 || k == 2) ? 1f : -1f, c = (k >= 2) ? 1f : -1f;
                        Vector3 p = Vector3.Scale(n + u * a + w * c, h);
                        _v.Add((centre + q * p) * K);
                        _n.Add(q * n);
                        _uv.Add(new Vector2(Vector3.Dot(p, u) + 0.5f, Vector3.Dot(p, w) + 0.5f));
                    }
                    List<int> t = _t[sub];
                    t.Add(baseIndex); t.Add(baseIndex + 1); t.Add(baseIndex + 2);
                    t.Add(baseIndex); t.Add(baseIndex + 2); t.Add(baseIndex + 3);
                }
            }

            internal Mesh Mesh(string name)
            {
                Mesh m = new Mesh();
                m.name = name;
                m.vertices = _v.ToArray();
                m.normals = _n.ToArray();
                m.uv = _uv.ToArray();
                m.subMeshCount = 2;
                m.SetTriangles(_t[0].ToArray(), 0);
                m.SetTriangles(_t[1].ToArray(), 1);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}

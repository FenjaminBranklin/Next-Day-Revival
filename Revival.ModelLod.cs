// Next Day: Survival - Revival Toolkit
//
// MODEL LOD (P2, docs/ai/tasks/p2-render-batching.md): distance LODs for the
// models this plugin builds in code - the Tu-95 and its wreck, the An-2, the
// Gepard, the technical, the howitzer, the T-72's running gear, the Katyusha
// launcher, the arty battery guns. The frame is CPU bound, so what counts is
// how many renderers are drawn, not their triangles: one LODGroup on the
// model node, LOD0 = every part, LOD1 = the parts that carry the silhouette
// (small parts - wheels, rockets, hatches, glass, props - go), then culled.
// Parts under ShadowMaxU stop casting shadows. The 52-K has its own four
// authored LODs (Revival.Flak.cs) and is not touched here.
//
// Distances are camera distances in game units (2.8 u = 1 m) with the lodBias
// of the moment folded in; a later lodBias change scales them with it.
//   aircraft  small parts off past 700 u, never culled: the far clip and the
//             haze end it (ViewDistance), so a bomber stays visible.
//   vehicle   small parts off past 300 u, culled past 2800 u (1 km).
//   gun       small parts off past 250 u, culled past 2200 u.
//   wreck     small parts off past 250 u, culled past 2200 u.
//
// Left alone: renderers another LODGroup already owns (a donor's own LODs),
// anything under an Animator, Animation or Rigidbody below the node (seated
// crew, dropped items), and a node that has a LODGroup already. Renderers
// the owning code switches on and off (rockets on the rails, glass, bay
// doors) keep working: a LODGroup never touches Renderer.enabled.
//
// Also here: SharedCopy, one tinted material per source material and look
// (a scorched wreck used to clone the material once per renderer).
//
// Cost: one GetComponentsInChildren per model build; nothing per frame.
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class ModelLod
    {
        internal enum Kind { Aircraft, Vehicle, Gun, Wreck }

        const float SmallShare = 0.12f;     // a part under this share of the model's size is small
        const float ShadowMaxU = 4f;        // parts under this cast no shadow (as ContentPerf)

        static Type _animator, _animation;
        static bool _typesLooked;
        static int _applied;

        internal static int Applied { get { return _applied; } }

        static void Distances(Kind kind, out float detail, out float cull)
        {
            switch (kind)
            {
                case Kind.Aircraft: detail = 700f; cull = 0f; break;
                case Kind.Vehicle: detail = 300f; cull = 2800f; break;
                default: detail = 250f; cull = 2200f; break;
            }
        }

        /// <summary>One LODGroup on node over its own mesh parts; null when
        /// there is nothing to do. Call once, after the model is complete.</summary>
        internal static LODGroup Apply(GameObject node, Kind kind)
        {
            if (node == null) return null;
            try
            {
                if (node.GetComponent<LODGroup>() != null) return null;
                LookTypes();
                // renderers some other LODGroup owns: the donor's, above or below
                HashSet<Renderer> taken = new HashSet<Renderer>();
                Take(node.GetComponentsInParent<LODGroup>(true), taken);
                Take(node.GetComponentsInChildren<LODGroup>(true), taken);

                MeshRenderer[] all = node.GetComponentsInChildren<MeshRenderer>(true);
                List<MeshRenderer> parts = new List<MeshRenderer>();
                List<float> sizes = new List<float>();
                Bounds model = new Bounds();
                bool any = false;
                for (int i = 0; i < all.Length; i++)
                {
                    MeshRenderer r = all[i];
                    if (r == null || taken.Contains(r) || Moving(r.transform, node.transform)) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    Bounds mb = mf.sharedMesh.bounds;
                    Vector3 ls = r.transform.lossyScale;
                    Vector3 sz = Vector3.Scale(mb.size, new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                    float size = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
                    Vector3 c = r.transform.TransformPoint(mb.center);
                    Bounds pb = new Bounds(c, sz);
                    if (!any) { model = pb; any = true; }
                    else model.Encapsulate(pb);
                    parts.Add(r);
                    sizes.Add(size);
                }
                if (parts.Count == 0) return null;

                float whole = Mathf.Max(model.size.x, Mathf.Max(model.size.y, model.size.z));
                List<Renderer> lod0 = new List<Renderer>(), lod1 = new List<Renderer>();
                int shadows = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    lod0.Add(parts[i]);
                    if (sizes[i] >= SmallShare * whole) lod1.Add(parts[i]);
                    if (sizes[i] < ShadowMaxU && parts[i].shadowCastingMode != ShadowCastingMode.Off)
                    {
                        parts[i].shadowCastingMode = ShadowCastingMode.Off;
                        shadows++;
                    }
                }

                float detail, cull;
                Distances(kind, out detail, out cull);
                LODGroup lg = node.AddComponent<LODGroup>();
                lg.fadeMode = LODFadeMode.None;
                bool two = lod1.Count > 0 && lod1.Count < lod0.Count;
                lg.SetLODs(two
                    ? new LOD[] { new LOD(0.5f, lod0.ToArray()), new LOD(0.1f, lod1.ToArray()) }
                    : new LOD[] { new LOD(0.1f, lod0.ToArray()) });
                lg.RecalculateBounds();
                Vector3 s = lg.transform.lossyScale;
                float ws = lg.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                LOD[] lods = lg.GetLODs();
                float hCull = cull > 0f ? Height(ws, cull) : 0.0002f;
                if (two)
                {
                    lods[0].screenRelativeTransitionHeight = Mathf.Max(Height(ws, detail), hCull * 1.5f);
                    lods[1].screenRelativeTransitionHeight = hCull;
                }
                else lods[0].screenRelativeTransitionHeight = hCull;
                lg.SetLODs(lods);
                _applied++;
                RevivalPlugin.L.LogInfo("ModelLod: " + node.name + " (" + kind + ", " + ws.ToString("0") + " u): "
                    + lod0.Count + " parts, " + (two ? lod1.Count + " past " + detail.ToString("0") + " u" : "one level")
                    + (cull > 0f ? ", culled past " + cull.ToString("0") + " u" : ", never culled") + ", "
                    + shadows + " stop casting shadows.");
                return lg;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("ModelLod " + node.name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Screen relative height of a cull at camera distance d for
        /// a group this big (Unity: size / (2 d tan(fov/2)) x lodBias).</summary>
        static float Height(float size, float d)
        {
            Camera cam = CameraOwner.MainCamera();
            float fov = cam != null ? Mathf.Clamp(cam.fieldOfView, 20f, 120f) : 60f;
            float tan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            float bias = Mathf.Clamp(QualitySettings.lodBias, 0.25f, 8f);
            return Mathf.Clamp(size * bias / (2f * Mathf.Max(d, 1f) * tan), 0.0002f, 0.95f);
        }

        static void Take(LODGroup[] groups, HashSet<Renderer> into)
        {
            for (int g = 0; g < groups.Length; g++)
            {
                if (groups[g] == null) continue;
                LOD[] lods = groups[g].GetLODs();
                for (int i = 0; i < lods.Length; i++)
                    foreach (Renderer r in lods[i].renderers) if (r != null) into.Add(r);
            }
        }

        static void LookTypes()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            // Animator/Animation live in UnityEngine.AnimationModule, which
            // build.ps1 does not reference (the ViewDistance way).
            _animator = Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
            _animation = Type.GetType("UnityEngine.Animation, UnityEngine.AnimationModule");
        }

        /// <summary>Something below the node that moves on its own: a person,
        /// a ragdoll bone, a dropped item.</summary>
        static bool Moving(Transform t, Transform node)
        {
            for (; t != null && t != node; t = t.parent)
            {
                if (t.GetComponent<Rigidbody>() != null) return true;
                if (_animator != null && t.GetComponent(_animator) != null) return true;
                if (_animation != null && t.GetComponent(_animation) != null) return true;
            }
            return false;
        }

        // ------------------------------------------------------ shared tints

        static readonly Dictionary<string, Material> _copies = new Dictionary<string, Material>();

        /// <summary>One copy of src per look, painted once - instead of a
        /// Renderer.material clone per renderer and wreck.</summary>
        internal static Material SharedCopy(Material src, string look, Action<Material> paint)
        {
            if (src == null) return null;
            string key = look + "|" + src.GetInstanceID();
            Material m;
            if (_copies.TryGetValue(key, out m) && m != null) return m;
            m = new Material(src);
            m.name = src.name + " (" + look + ")";
            if (paint != null) paint(m);
            _copies[key] = m;
            return m;
        }
    }
}

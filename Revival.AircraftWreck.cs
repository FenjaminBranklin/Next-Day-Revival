using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Crash-only placement. No Update, polling or persistent allocations.</summary>
    internal static class AircraftWreck
    {
        internal const float Bed = 0.15f * 2.8f;

        internal static void Matte(Material m)
        {
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            m.DisableKeyword("_EMISSION");
        }

        /// <summary>All solid layers, including slabs, fences and props. The
        /// complete carrier hierarchy is excluded, not just the visible mesh.
        /// Unity SampleHeight is the fallback; raw heightmaps are x-major.</summary>
        internal static bool Surface(Vector3 at, Transform aircraft, out float y)
        {
            bool found = RevivalTroopInsertion.TerrainHeight(at, out y);
            float top = Mathf.Max(1500f, at.y + 300f);
            RaycastHit[] hits = Physics.RaycastAll(new Vector3(at.x, top, at.z),
                Vector3.down, top + 3000f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.isTrigger || (aircraft != null
                    && (c.transform == aircraft || c.transform.IsChildOf(aircraft)))) continue;
                // Vertical walls are obstacles, not a resting surface.
                if (hits[i].normal.y < 0.25f) continue;
                if (!found || hits[i].point.y > y) { y = hits[i].point.y; found = true; }
            }
            return found;
        }

        internal static Quaternion Slope(Vector3 at, Transform aircraft)
        {
            const float e = 5f * 2.8f;
            float w, east, s, n;
            if (!Surface(at + new Vector3(-e, 0f, 0f), aircraft, out w)
                || !Surface(at + new Vector3(e, 0f, 0f), aircraft, out east)
                || !Surface(at + new Vector3(0f, 0f, -e), aircraft, out s)
                || !Surface(at + new Vector3(0f, 0f, e), aircraft, out n)) return Quaternion.identity;
            Vector3 normal = new Vector3(w - east, 2f * e, s - n).normalized;
            float tilt = Vector3.Angle(Vector3.up, normal);
            if (tilt > 28f) normal = Vector3.Slerp(Vector3.up, normal, 28f / tilt).normalized;
            return Quaternion.FromToRotation(Vector3.up, normal);
        }

        /// <summary>Nine occupied footprint cells, each with an actual bottom
        /// vertex. Empty AABB corners must not balance an aircraft on a fence
        /// beside a wing. Move the carrier too, so loot and fire follow it.</summary>
        internal static void Place(Transform carrier, Transform visual, bool align)
        {
            if (carrier == null || visual == null) return;
            if (align) visual.rotation = Slope(visual.position, carrier) * visual.rotation;
            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>(false);
            Bounds bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshRenderer r = filters[i].GetComponent<MeshRenderer>();
                if (r == null || !r.enabled || filters[i].sharedMesh == null) continue;
                if (!any) bounds = r.bounds;
                else bounds.Encapsulate(r.bounds);
                any = true;
            }
            if (!any) return;
            Vector3[] support = new Vector3[9];
            bool[] used = new bool[9];
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter f = filters[i];
                MeshRenderer r = f.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled || f.sharedMesh == null) continue;
                // Read once at the crash/settle boundary, never every frame.
                Vector3[] vertices;
                if (f.sharedMesh.isReadable) vertices = f.sharedMesh.vertices;
                else
                {
                    // Native donor meshes may have Read/Write disabled. Their
                    // local bounds remain available; do not abort Mi-8 settling.
                    Bounds b = f.sharedMesh.bounds;
                    vertices = new Vector3[8];
                    for (int corner = 0; corner < vertices.Length; corner++)
                        vertices[corner] = new Vector3(
                            (corner & 1) == 0 ? b.min.x : b.max.x,
                            (corner & 2) == 0 ? b.min.y : b.max.y,
                            (corner & 4) == 0 ? b.min.z : b.max.z);
                }
                Matrix4x4 matrix = f.transform.localToWorldMatrix;
                for (int v = 0; v < vertices.Length; v++)
                {
                    Vector3 p = matrix.MultiplyPoint3x4(vertices[v]);
                    int x = Mathf.Clamp((int)((p.x - bounds.min.x) / Mathf.Max(0.01f, bounds.size.x) * 3f), 0, 2);
                    int z = Mathf.Clamp((int)((p.z - bounds.min.z) / Mathf.Max(0.01f, bounds.size.z) * 3f), 0, 2);
                    int cell = x * 3 + z;
                    if (!used[cell] || p.y < support[cell].y) support[cell] = p;
                    used[cell] = true;
                }
            }
            bool grounded = false;
            float shift = 0f;
            for (int i = 0; i < support.Length; i++)
            {
                float y;
                if (!used[i] || !Surface(support[i], carrier, out y)) continue;
                float needed = y - support[i].y;
                if (!grounded || needed > shift) shift = needed;
                grounded = true;
            }
            if (grounded) carrier.position += Vector3.up * (shift - Bed);
        }
    }
}

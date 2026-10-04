using UnityEngine;

namespace NextDayRevival
{
    // An-2 and Mi-8 have no Tu95Visual camera proxy. During destruction only,
    // draw their existing meshes/materials inside the far clip as well. The
    // carrier, collider and fall component always stay at the real position.
    public sealed class AircraftCrashVisual : MonoBehaviour
    {
        GameObject _draw;
        MeshRenderer[] _source;
        bool _stopped;

        internal static AircraftCrashVisual Attach(GameObject aircraft)
        {
            if (NpcAircraft.IsTu95(aircraft)) return null;
            AircraftCrashVisual v = aircraft.GetComponent<AircraftCrashVisual>();
            if (v != null) return v;
            v = aircraft.AddComponent<AircraftCrashVisual>();
            v.Build();
            return v;
        }

        void Build()
        {
            MeshRenderer[] candidates = GetComponentsInChildren<MeshRenderer>(true);
            int count = 0;
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i].enabled && candidates[i].gameObject.activeInHierarchy
                    && candidates[i].GetComponent<MeshFilter>() != null) count++;
            _source = new MeshRenderer[count];
            _draw = new GameObject("NDR_AircraftCrashVisual");
            Transform root = _draw.transform;
            root.position = transform.position; root.rotation = transform.rotation;
            root.localScale = transform.lossyScale;
            int n = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                MeshRenderer r = candidates[i];
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!r.enabled || !r.gameObject.activeInHierarchy || mf == null) continue;
                _source[n++] = r;
                GameObject part = new GameObject(r.name);
                part.transform.SetParent(root, false);
                part.transform.position = r.transform.position;
                part.transform.rotation = r.transform.rotation;
                Vector3 scale = r.transform.lossyScale;
                Vector3 carrier = transform.lossyScale;
                part.transform.localScale = new Vector3(scale.x / carrier.x, scale.y / carrier.y, scale.z / carrier.z);
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                MeshRenderer copy = part.AddComponent<MeshRenderer>();
                copy.sharedMaterials = r.sharedMaterials;
                copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            _draw.SetActive(false);
        }

        internal void Place(Camera cam, bool hidden)
        {
            if (_stopped || _draw == null) return;
            Vector3 eye = cam.transform.position;
            float distance = (transform.position - eye).magnitude;
            float near = Mathf.Min(Tu95Visual.ProxyU(cam.farClipPlane), cam.farClipPlane * 0.45f);
            bool proxy = distance > near;
            _draw.SetActive(proxy && !hidden);
            for (int i = 0; i < _source.Length; i++)
                if (_source[i] != null) _source[i].enabled = !proxy;
            if (!proxy) return;
            float scale = near / Mathf.Max(1f, distance);
            _draw.transform.position = AircraftCrashFx.DrawPosition(transform.position, eye, scale);
            _draw.transform.rotation = transform.rotation;
            _draw.transform.localScale = transform.lossyScale * scale;
        }

        internal void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            if (_source != null)
                for (int i = 0; i < _source.Length; i++)
                    if (_source[i] != null) _source[i].enabled = true;
            if (_draw != null) { _draw.SetActive(false); Destroy(_draw); }
        }

        void OnDestroy() { Stop(); }
    }
}

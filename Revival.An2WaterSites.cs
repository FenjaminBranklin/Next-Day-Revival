// Fixed design sites, fitted once to the loaded water/relief. Local scenery only.
using UnityEngine;

namespace NextDayRevival
{
    internal static class An2WaterSites
    {
        static GameObject _root;
        static readonly GameObject[] _surfaces = new GameObject[2];
        static Vector3 _moor, _exit;
        static bool _ready;
        internal static bool Surface(GameObject go) { return go == _surfaces[0] || go == _surfaces[1]; }
        internal static void Reset()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null; _ready = false; _surfaces[0] = _surfaces[1] = null;
        }
        internal static bool Exit(Vector3 aircraft, out Vector3 at)
        {
            at = _exit;
            float water;
            return _ready && An2WaterCore.NearJetty(aircraft.x, aircraft.z, _moor.x, _moor.z)
                && AirLanding.Water(aircraft, out water) && Mathf.Abs(aircraft.y - water) < 3f * PlayerAn2.K;
        }
        static GameObject Beam(string name, Vector3 start, Vector3 end, float width, float thickness)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.SetParent(_root.transform, false);
            go.transform.position = (start + end) * .5f - Vector3.up * thickness * .5f;
            go.transform.rotation = Quaternion.LookRotation(end - start);
            go.transform.localScale = new Vector3(width, thickness, Vector3.Distance(start, end));
            go.GetComponent<Renderer>().sharedMaterial = AirSwampPlatform.Wood;
            return go;
        }
        internal static void Build()
        {
            if (_root != null) return;
            string region = AirPlan.RegionOf(MapScene.Current);
            Vector3 shore, dock; float water, ground;
            if (region == "PRIM")
            {
                // J-P1, neutral harbour; moor a little SW of the shallow waterline.
                _moor = new Vector3(899.5f, 0, 960f);
                if (!AirLanding.Water(_moor, out water)) return;
                dock = new Vector3(908f, water + 1.8f, 952f);
                shore = new Vector3(956.9f, 0, 1041f);
            }
            else if (region == "MAIN")
            {
                // Design centre (3715,1100), west of the airfield. Resolve the
                // measured relief centreline; do not flatten/rebuild the river.
                float best = float.MaxValue; Vector3 chosen = Vector3.zero; water = 0;
                for (int z = 1080; z <= 1120; z += 4)
                    for (int x = 3590; x <= 3850; x += 2)
                    {
                        float h, bed; Vector3 p = new Vector3(x, 0, z);
                        if (!AirLanding.Water(p, out h) || !AirLanding.Ground(p, out bed)) continue;
                        float support;
                        if (!An2Water.Support(p, Quaternion.identity, out support)) continue;
                        float score = (x - 3715) * (x - 3715) + (z - 1100) * (z - 1100) - (h - bed) * 20f;
                        if (score < best) { best = score; chosen = p; water = h; }
                    }
                if (best == float.MaxValue) return;
                _moor = chosen;
                // Moor northbound with the cargo door toward the west bank.
                dock = new Vector3(chosen.x - 8f, water + 1.8f, chosen.z);
                shore = new Vector3(chosen.x - 32f, 0, chosen.z);
            }
            else return;
            if (!AirLanding.Ground(shore, out ground)) return;
            shore.y = Mathf.Max(ground + .12f, dock.y);
            _root = new GameObject("NDR_An2Jetty_" + region);
            Vector3 direction = (dock - shore).normalized;
            // End at the dock point, outside the port float. Extending the
            // head toward the mooring would put a deck through that float.
            Vector3 head = dock;
            _surfaces[0] = Beam("JettyAccess", shore, dock, 5.6f, .5f);
            _surfaces[1] = Beam("CargoLanding", dock - direction * 10f, head, 8.4f, .5f);
            for (int i = 0; i < 4; i++)
            {
                Vector3 top = Vector3.Lerp(shore, head, i / 3f); float bed;
                if (!AirLanding.Ground(top, out bed)) continue;
                GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.name = "JettyPile"; post.transform.SetParent(_root.transform, false);
                post.transform.position = new Vector3(top.x, (top.y + bed) * .5f, top.z);
                post.transform.localScale = new Vector3(.7f, Mathf.Max(.5f, top.y - bed), .7f);
                post.GetComponent<Renderer>().sharedMaterial = AirSwampPlatform.Wood;
            }
            _exit = dock + Vector3.up * .12f; _ready = true;
            RevivalPlugin.L.LogInfo("An2Water: " + region + " cargo jetty at " + dock
                + "; moor " + _moor + (region == "MAIN" ? " (taxi/mooring reach; main arrival stays on runway)." : "."));
        }
    }
}

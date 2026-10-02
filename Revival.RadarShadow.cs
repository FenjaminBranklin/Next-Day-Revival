// W AA6: exact terrain-collider LOS, cached on the radar's existing 4 Hz scan.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RadarShadow
    {
        const int Capacity = 64;
        static GameObject[] Targets = new GameObject[Capacity];
        static bool[] Seen = new bool[Capacity];
        static float[] Heights = new float[Capacity];
        static float[] CheckedAt = new float[Capacity];
        static Terrain[] _terrain;
        static TerrainCollider[] _colliders;
        static int _count;
        static int _cursor;
        static int _scene = -1;
        static int _scenes = -1;
        static float _maxAge = 1f;

        internal static bool Visible(GameObject go)
        {
            for (int i = 0; i < _count; i++)
                if (object.ReferenceEquals(Targets[i], go)) return Seen[i] && Time.time - CheckedAt[i] <= _maxAge;
            return false; // Unscanned targets never receive a radar solution.
        }

        internal static float Height(GameObject go)
        {
            for (int i = 0; i < _count; i++) if (object.ReferenceEquals(Targets[i], go)) return Heights[i];
            return 0f;
        }

        internal static void Scan(List<GepardGun.Contact> contacts, Vector3 eye)
        {
            FrameProf.S(FrameProf.S_RadarShadowT);
            try
            {
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
                int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
                // East terrain loads additively; the active scene's buildIndex
                // does not change. Refresh only when scene topology changes.
                if (_terrain == null || _scene != scene || _scenes != scenes
                    || (_terrain.Length > 0 && _terrain[0] == null))
                {
                    _scene = scene;
                    _scenes = scenes;
                    _terrain = Terrain.activeTerrains;
                    _colliders = new TerrainCollider[_terrain.Length];
                    for (int i = 0; i < _terrain.Length; i++)
                        if (_terrain[i] != null)
                            _colliders[i] = _terrain[i].GetComponent<TerrainCollider>();
                }
                // Grow only when a spawn exceeds the previous high-water mark.
                // No hard contact cutoff and no steady-state allocation.
                if (contacts.Count > Targets.Length)
                {
                    int size = contacts.Count > Targets.Length * 2 ? contacts.Count : Targets.Length * 2;
                    System.Array.Resize(ref Targets, size);
                    System.Array.Resize(ref Seen, size);
                    System.Array.Resize(ref Heights, size);
                    System.Array.Resize(ref CheckedAt, size);
                }
                _count = contacts.Count;
                // Eight LOS checks per 4 Hz slice; a large formation must not
                // expire before its next scheduled visit. Still reject stale data.
                _maxAge = Mathf.Max(1f, ((_count + 7) / 8 + 1) * 0.25f);
                for (int i = 0; i < _count; i++)
                {
                    if (object.ReferenceEquals(Targets[i], contacts[i].Go)) continue;
                    Targets[i] = contacts[i].Go; Seen[i] = false; Heights[i] = 0f; CheckedAt[i] = -10f;
                }
                // At most eight targets per quarter-second, irrespective of raid size.
                // Expired solutions fail closed while the next slice is pending.
                int budget = Mathf.Min(8, _count);
                for (int n = 0; n < budget; n++)
                {
                    int i = _cursor++ % _count;
                    if (_cursor >= _count) _cursor = 0;
                    GepardGun.Contact c = contacts[i];
                    Seen[i] = false; Heights[i] = 0f; CheckedAt[i] = Time.time;
                    if (c.Go == null) continue;
                    float ground = 0f;
                    bool known = false, blocked = false;
                    Vector3 d = c.Pos - eye;
                    float length = d.magnitude;
                    Ray ray = new Ray(eye, d.normalized);
                    for (int k = 0; k < _terrain.Length; k++)
                    {
                        Terrain t = _terrain[k];
                        if (t == null || !t.drawHeightmap) continue;
                        Vector3 origin = t.GetPosition(), size = t.terrainData.size;
                        if (c.Pos.x >= origin.x && c.Pos.z >= origin.z
                            && c.Pos.x <= origin.x + size.x && c.Pos.z <= origin.z + size.z)
                        { ground = origin.y + t.SampleHeight(c.Pos); known = true; }
                        RaycastHit hit;
                        if (_colliders[k] != null && _colliders[k].Raycast(ray, out hit, length)) blocked = true;
                    }
                    // Raid approaches intentionally extend beyond the map edge.
                    // Their synchronized path carries AGL even where no terrain
                    // exists. Still raycast every loaded terrain: a ridge between
                    // the antenna and the off-map aircraft must mask it.
                    NpcAircraft.Flight flight = known ? null : NpcAircraft.Find(c.Go);
                    bool approach = !known && _terrain.Length > 0 && flight != null && flight.Path != null;
                    Heights[i] = known ? c.Pos.y - ground : approach ? flight.Path.Agl : 0f;
                    Seen[i] = (known || approach) && AirDefenceCore.RadarVisible(Heights[i], blocked);
                }
            }
            finally { FrameProf.E(FrameProf.S_RadarShadowT); }
        }
    }
}

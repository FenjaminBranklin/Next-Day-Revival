// W AA6: exact terrain-collider LOS, cached on the radar's existing 4 Hz scan.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RadarShadow
    {
        const int Capacity = 64;
        static readonly GameObject[] Targets = new GameObject[Capacity];
        static readonly bool[] Seen = new bool[Capacity];
        static readonly float[] Heights = new float[Capacity];
        static readonly float[] CheckedAt = new float[Capacity];
        static Terrain[] _terrain;
        static TerrainCollider[] _colliders;
        static int _count;
        static int _cursor;
        static int _scene = -1;
        static int _scenes = -1;

        internal static bool Visible(GameObject go)
        {
            for (int i = 0; i < _count; i++)
                if (object.ReferenceEquals(Targets[i], go)) return Seen[i] && Time.time - CheckedAt[i] <= 1f;
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
                _count = Mathf.Min(Capacity, contacts.Count);
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
                    // Unknown terrain fails closed, rather than seeing through hills.
                    Heights[i] = known ? c.Pos.y - ground : 0f;
                    Seen[i] = known && AirDefenceCore.RadarVisible(Heights[i], blocked);
                }
            }
            finally { FrameProf.E(FrameProf.S_RadarShadowT); }
        }
    }
}

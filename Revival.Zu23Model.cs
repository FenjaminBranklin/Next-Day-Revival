// W AA5: shipped Blender ZU-23 model, separate cache from the 52-K.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace NextDayRevival
{
    internal static class Zu23Model
    {
        const float K = Flak.K;
        const int Lods = 4;
        static readonly string[] PartNames = { "base", "mount", "cradle", "barrel_l", "barrel_r" };
        static readonly float[] LodHeights = { 0.35f, 0.14f, 0.05f, 0.008f };
        static bool _loaded, _ok;
        static Mesh[,] _mesh;
        static Material _skin;
        static readonly Dictionary<string, Vector3> _rig = new Dictionary<string, Vector3>();
        struct Box3 { public int Part; public Vector3 Centre, Size; }
        static readonly List<Box3> _boxes = new List<Box3>();
        static readonly Vector3 ShoulderM = new Vector3(-0.95f, 1.45f, -1.55f);
        static readonly Vector3 SightM = new Vector3(-0.62f, 1.14f, -0.26f);
        internal static Flak.Gun Build(int index, string id, Vector2 spot, float yaw, Scene scene)
        {
            if (!Load()) return null;
            GameObject root = new GameObject("NDR Flak ZU-23-2 " + id);
            SceneManager.MoveGameObjectToScene(root, scene);
            Vector3 at = new Vector3(spot.x, 0f, spot.y);
            RaycastHit hit;
            float height;
            if (Physics.Raycast(at + Vector3.up * 3000f, Vector3.down, out hit, 6000f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) at.y = hit.point.y;
            else if (EastWorld.TerrainHeight(at, out height)) at.y = height;
            root.transform.position = at;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Flak.Gun g = new Flak.Gun();
            g.Index = index; g.Id = id; g.Name = "Airfield ZU-23-2";
            g.ShortRange = true; g.Root = root.transform; g.Owner = root.transform;
            g.BuiltAt = Time.time; g.Rounds = ShortRangeCore.Magazine;
            g.CrewGKey = "flak/" + id.ToLowerInvariant() + "/g";
            g.CrewCKey = "flak/" + id.ToLowerInvariant() + "/c";
            Parts(g);
            GepardShots.WarmShortRange();
            Flak.Log(id + ": ZU-23-2 built at " + at + ".");
            return g;
        }
        static bool Load()
        {
            if (_loaded) return _ok;
            _loaded = true;
            try
            {
                ReadRig(System.IO.Path.Combine(RevivalPlugin.AssetDir, "zu23_rig.txt"));
                _mesh = new Mesh[PartNames.Length, Lods];
                for (int p = 0; p < PartNames.Length; p++)
                    for (int l = 0; l < Lods; l++)
                    {
                        _mesh[p, l] = Assets.Load("zu23_" + PartNames[p] + "_lod" + l + ".ndmesh");
                        if (_mesh[p, l] == null && l == 0)
                            throw new InvalidOperationException("zu23_" + PartNames[p] + "_lod0.ndmesh missing");
                    }
                Texture2D tex = Assets.Texture("zu23_diffuse.png", false, true);
                Texture2D nrm = Assets.Texture("zu23_normal.png", true, true);
                if (tex == null) throw new InvalidOperationException("zu23_diffuse.png missing");
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                _skin = new Material(shader);
                _skin.name = "NDR_Flak_ZU23";
                tex.anisoLevel = 4;
                tex.filterMode = FilterMode.Trilinear;
                _skin.mainTexture = tex;
                _skin.color = Color.white;
                if (nrm != null && _skin.HasProperty("_BumpMap"))
                {
                    _skin.SetTexture("_BumpMap", nrm);
                    _skin.EnableKeyword("_NORMALMAP");
                }
                if (_skin.HasProperty("_Glossiness")) _skin.SetFloat("_Glossiness", 0.25f);
                if (_skin.HasProperty("_Metallic")) _skin.SetFloat("_Metallic", 0.15f);
                _skin.hideFlags = HideFlags.HideAndDontSave;
                _ok = true;
                Flak.Log("ZU-23-2 model loaded: " + _mesh[0, 0].vertexCount + " base vertices, rig of " + _rig.Count + " marks.");
            }
            catch (Exception ex)
            {
                _ok = false;
                RevivalPlugin.L.LogError("Flak: the ZU-23-2 model did not load (" + ex.Message
                    + ") - repair the client package (python zu23_build.py).");
            }
            return _ok;
        }

        static void ReadRig(string path)
        {
            _rig.Clear();
            _boxes.Clear();
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException("zu23_rig.txt missing");
            string[] lines = System.IO.File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "box")
                {
                    // box <part> cx cy cz sx sy sz
                    int part = p.Length == 8 ? Array.IndexOf(PartNames, p[1]) : -1;
                    float[] v = new float[6];
                    bool ok = part >= 0;
                    for (int k = 0; ok && k < 6; k++)
                        ok = float.TryParse(p[2 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
                    if (!ok) continue;
                    Box3 b;
                    b.Part = part;
                    b.Centre = new Vector3(v[0], v[1], v[2]);
                    b.Size = new Vector3(v[3], v[4], v[5]);
                    _boxes.Add(b);
                    continue;
                }
                if (p.Length < 4) continue;
                float x, y, z;
                if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    _rig[p[0]] = new Vector3(x, y, z);
            }
        }

        /// <summary>A rig mark in world units; the metres of zu23_build.py x K
        /// if the file lacks it.</summary>
        static Vector3 Rig(string key, Vector3 metres)
        {
            Vector3 v;
            return _rig.TryGetValue(key, out v) ? v : metres * K;
        }

        static Transform Node(Transform parent, string name, Vector3 local)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        /// <summary>
        /// The gun at its real pivots (zu23_rig.txt): the carriage stands, the
        /// mount traverses on the pedestal, the cradle elevates on the
        /// trunnions, the barrel slides back along the cradle's +z.
        /// </summary>
        static void Parts(Flak.Gun g)
        {
            Transform root = g.Root;
            Transform mount = Node(root, "mount", Rig("mount", new Vector3(0f, 0.92f, 0f)));
            Transform cradle = Node(mount, "cradle", Rig("cradle", new Vector3(0f, 0.78f, 0.18f)));
            Transform barrel = Node(cradle, "barrel_l", Rig("barrel_l", Vector3.zero));
            Transform right = Node(cradle, "barrel_r", Rig("barrel_r", Vector3.zero));
            g.BarrelRight = right;
            g.MuzzleRight = Node(right, "muzzle_r", Rig("muzzle_r", new Vector3(0f, 0f, 1.86f)));
            g.Mount = mount;
            g.Cradle = cradle;
            g.Barrel = barrel;
            g.BarrelHome = barrel.localPosition;
            g.BarrelRightHome = right.localPosition;
            g.Muzzle = Node(barrel, "muzzle", Rig("muzzle_l", new Vector3(0f, 0f, 1.86f)));
            g.SeatGunner = Node(mount, "seat gunner", Rig("seat_gunner", new Vector3(-0.78f, 0.47f, -0.36f)));
            g.SeatLoader = Node(mount, "seat loader", Rig("seat_loader", new Vector3(0.78f, 0.47f, -0.36f)));
            g.Eye = Node(cradle, "eye", Rig("eye", new Vector3(-0.46f, 0.38f, -0.25f)));
            g.Shoulder = Node(mount, "shoulder", Rig("shoulder", ShoulderM));
            g.Sight = Node(mount, "sight", Rig("sight", SightM));
            g.Stroke = Rig("recoil", new Vector3(0f, 0f, 0.65f)).z;

            Transform[] parents = { root, mount, cradle, barrel, right };
            Colliders(g, parents);
            List<Renderer>[] byLod = new List<Renderer>[Lods];
            for (int l = 0; l < Lods; l++) byLod[l] = new List<Renderer>();
            for (int p = 0; p < PartNames.Length; p++)
                for (int l = 0; l < Lods; l++)
                {
                    Mesh mesh = _mesh[p, l];
                    if (mesh == null) continue;
                    GameObject go = new GameObject("zu23_" + PartNames[p] + "_LOD" + l);
                    go.transform.SetParent(parents[p], false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = _skin;
                    r.shadowCastingMode = l >= 2 ? UnityEngine.Rendering.ShadowCastingMode.Off
                        : UnityEngine.Rendering.ShadowCastingMode.On;
                    byLod[l].Add(r);
                }
            LODGroup group = root.gameObject.AddComponent<LODGroup>();
            LOD[] lods = new LOD[Lods];
            for (int l = 0; l < Lods; l++) lods[l] = new LOD(LodHeights[l], byLod[l].ToArray());
            group.SetLODs(lods);
            group.RecalculateBounds();
        }

        static void Colliders(Flak.Gun g, Transform[] parents)
        {
            if (_boxes.Count == 0) return;
            int layer = g.Holder != null ? g.Holder.gameObject.layer : g.Root.gameObject.layer;
            bool moving = false;
            for (int i = 0; i < _boxes.Count; i++)
            {
                Box3 b = _boxes[i];
                Transform parent = parents[b.Part];
                if (parent == null) continue;
                GameObject go = new GameObject("zu23_collider_" + PartNames[b.Part] + "_" + i);
                go.layer = layer;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = b.Centre;
                go.transform.localRotation = Quaternion.identity;
                go.AddComponent<BoxCollider>().size = b.Size;
                if (b.Part > 0) moving = true;
            }
            if (moving && g.Mount != null)
            {
                Rigidbody rb = g.Mount.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
        }

    }
}

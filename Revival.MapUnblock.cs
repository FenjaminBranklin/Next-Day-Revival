using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    // Scene-only changes: never edits assets, terrain, saves or network actors.
    internal sealed class MapUnblock : MonoBehaviour
    {
        sealed class Entry
        {
            internal string Path;
            internal Vector3 Position;
        }
        readonly Dictionary<string, List<Entry>> entries = new Dictionary<string, List<Entry>>();
        readonly HashSet<Scene> running = new HashSet<Scene>();
        readonly List<GameObject> hidden = new List<GameObject>();
        readonly List<GameObject> holders = new List<GameObject>();
        ConfigEntry<bool> enabledConfig;
        ConfigEntry<bool> linksConfig;

        internal static void Install(GameObject owner, ConfigFile config)
        {
            MapUnblock instance = owner.AddComponent<MapUnblock>();
            instance.enabledConfig = config.Bind("MapUnblock", "Enabled", true,
                "Remove selected outdoor fence/barrier segments. Building areas are protected by the shipped position list. Requires scene reload after changes.");
            instance.linksConfig = config.Bind("MapUnblock", "NavigationLinks", true,
                "Bridge short baked navigation gaps at removed segments after ground and clearance checks. Requires scene reload after changes.");
            foreach (string row in MapUnblockData.Rows)
            {
                string[] fields = row.Split('\t');
                List<Entry> list;
                if (!instance.entries.TryGetValue(fields[0], out list))
                {
                    list = new List<Entry>();
                    instance.entries.Add(fields[0], list);
                }
                Entry entry = new Entry();
                entry.Path = fields[1];
                entry.Position = new Vector3(Parse(fields[2]), Parse(fields[3]), Parse(fields[4]));
                list.Add(entry);
            }
            SceneManager.sceneLoaded += instance.Loaded;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                instance.Loaded(SceneManager.GetSceneAt(i), LoadSceneMode.Additive);
        }

        static float Parse(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }

        void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (!enabledConfig.Value || !entries.ContainsKey(scene.name) || !scene.isLoaded) return;
            if (running.Add(scene)) StartCoroutine(Apply(scene));
        }

        static string PathOf(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        IEnumerator Apply(Scene scene)
        {
            // Scene callbacks include additive streaming and return visits.
            yield return null;
            if (!scene.isLoaded) { running.Remove(scene); yield break; }
            List<Entry> wanted = entries[scene.name];
            Dictionary<string, List<Entry>> paths = new Dictionary<string, List<Entry>>();
            foreach (Entry entry in wanted)
            {
                List<Entry> group;
                if (!paths.TryGetValue(entry.Path, out group))
                { group = new List<Entry>(); paths.Add(entry.Path, group); }
                group.Add(entry);
            }
            List<Transform> removed = new List<Transform>();
            List<Bounds> bounds = new List<Bounds>();
            int scanned = 0;
            int hiddenCount = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    List<Entry> group;
                    if (paths.TryGetValue(PathOf(t), out group))
                    {
                        bool match = false;
                        foreach (Entry entry in group)
                            if ((entry.Position - t.position).sqrMagnitude < 0.0625f) { match = true; break; }
                        if (match && t.gameObject.activeSelf)
                        {
                            Bounds box;
                            bool hasBounds = LocalBounds(t, out box);
                            hidden.Add(t.gameObject);
                            t.gameObject.SetActive(false); // Includes every LOD, collider and carving obstacle.
                            hiddenCount++;
                            if (hasBounds) { removed.Add(t); bounds.Add(box); }
                        }
                    }
                    if (++scanned % 256 == 0) yield return null;
                }
            }
            // Let dynamic carving update before inspecting the remaining baked gaps.
            yield return new WaitForSeconds(0.5f);
            int links = 0;
            if (linksConfig.Value && scene.isLoaded)
                for (int i = 0; i < removed.Count; i++)
                {
                    if (removed[i] != null) links += Bridge(scene, removed[i], bounds[i]);
                    if (i % 16 == 0) yield return null;
                }
            RevivalPlugin.L.LogInfo("MapUnblock: " + scene.name + ": removed " + hiddenCount
                + "/" + wanted.Count + " selected segments, " + removed.Count
                + " with bounds; added " + links + " checked navigation links.");
            running.Remove(scene);
            hidden.RemoveAll(delegate(GameObject go) { return go == null; });
            holders.RemoveAll(delegate(GameObject go) { return go == null; });
        }

        static bool LocalBounds(Transform root, out Bounds result)
        {
            result = new Bounds();
            bool any = false;
            // Static batching can replace render meshes with a whole chunk mesh.
            // Physics geometry still describes the individual fence segment.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger) continue;
                BoxCollider box = collider as BoxCollider;
                MeshCollider mesh = collider as MeshCollider;
                CapsuleCollider capsule = collider as CapsuleCollider;
                SphereCollider sphere = collider as SphereCollider;
                if (box != null) IncludeBounds(root, box.transform, new Bounds(box.center, box.size), ref result, ref any);
                else if (mesh != null && mesh.sharedMesh != null)
                    IncludeBounds(root, mesh.transform, mesh.sharedMesh.bounds, ref result, ref any);
                else if (capsule != null)
                {
                    Vector3 size = Vector3.one * capsule.radius * 2;
                    size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2);
                    IncludeBounds(root, capsule.transform, new Bounds(capsule.center, size), ref result, ref any);
                }
                else if (sphere != null)
                    IncludeBounds(root, sphere.transform, new Bounds(sphere.center, Vector3.one * sphere.radius * 2), ref result, ref any);
            }
            if (any) return true;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                IncludeBounds(root, filter.transform, filter.sharedMesh.bounds, ref result, ref any);
            }
            return any;
        }

        static void IncludeBounds(Transform root, Transform child, Bounds b, ref Bounds result, ref bool any)
        {
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = root.InverseTransformPoint(child.TransformPoint(corner));
                if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                else result.Encapsulate(p);
            }
        }

        int Bridge(Scene scene, Transform root, Bounds box)
        {
            bool alongX = box.size.x >= box.size.z;
            Vector3 axis = alongX ? Vector3.right : Vector3.forward;
            Vector3 normal = alongX ? Vector3.forward : Vector3.right;
            float axisScale = root.TransformVector(axis).magnitude;
            float normalScale = root.TransformVector(normal).magnitude;
            float length = (alongX ? box.size.x : box.size.z) * axisScale;
            float width = (alongX ? box.size.z : box.size.x) * normalScale;
            if (axisScale < 0.001f || normalScale < 0.001f || length > 25 || width > 3) return 0;
            int count = Mathf.Clamp(Mathf.CeilToInt(length / 3f), 1, 8);
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 middle = box.center + axis * ((i + 0.5f) / count - 0.5f) * length / axisScale;
                middle.y = box.min.y;
                Vector3 offset = normal * (width * 0.5f + 1.5f) / normalScale;
                Vector3 a, b;
                if (!Ground(root.TransformPoint(middle - offset), out a)
                    || !Ground(root.TransformPoint(middle + offset), out b)) continue;
                Vector3 across = root.TransformVector(normal).normalized;
                if (Mathf.Abs(a.y - b.y) > 0.8f || Vector3.Dot(b - a, across) < width + 0.5f) continue;
                Vector3 delta = b - a;
                if (delta.magnitude > 8 || delta.magnitude < 0.5f) continue;
                NavMeshHit hit;
                if (!NavMesh.Raycast(a, b, out hit, NavMesh.AllAreas)) continue; // Already walkable.
                bool safe = true;
                for (int step = 1; step < 8; step++)
                {
                    Vector3 point = Vector3.Lerp(a, b, step / 8f);
                    RaycastHit ground;
                    if (!Physics.Raycast(point + Vector3.up, Vector3.down, out ground, 2f,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                        || ground.normal.y < 0.7f || Mathf.Abs(ground.point.y - point.y) > 0.5f)
                    { safe = false; break; }
                }
                RaycastHit clearance;
                if (!safe || Physics.CheckCapsule(a + Vector3.up * 0.4f, a + Vector3.up * 1.4f,
                        0.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    || Physics.CheckCapsule(b + Vector3.up * 0.4f, b + Vector3.up * 1.4f,
                        0.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    || Physics.SphereCast(a + Vector3.up, 0.35f, delta.normalized, out clearance,
                        delta.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
                GameObject holder = new GameObject("NDR_MapPassage");
                SceneManager.MoveGameObjectToScene(holder, scene);
                holders.Add(holder);
                Transform start = new GameObject("Start").transform;
                Transform end = new GameObject("End").transform;
                start.SetParent(holder.transform, false); end.SetParent(holder.transform, false);
                start.position = a; end.position = b;
                OffMeshLink link = holder.AddComponent<OffMeshLink>();
                link.startTransform = start; link.endTransform = end;
                link.biDirectional = true; link.area = 0; link.autoUpdatePositions = false;
                link.UpdatePositions();
                made++;
            }
            return made;
        }

        static bool Ground(Vector3 point, out Vector3 result)
        {
            result = Vector3.zero;
            RaycastHit ground;
            NavMeshHit nav;
            if (!Physics.Raycast(point + Vector3.up * 5, Vector3.down, out ground, 10,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || ground.normal.y < 0.7f) return false;
            if (!NavMesh.SamplePosition(ground.point, out nav, 1.25f, NavMesh.AllAreas)
                || Mathf.Abs(nav.position.y - ground.point.y) > 0.5f) return false;
            result = nav.position;
            return true;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= Loaded;
            foreach (GameObject holder in holders) if (holder != null) Destroy(holder);
            foreach (GameObject go in hidden) if (go != null) go.SetActive(true);
        }
    }
}

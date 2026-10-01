// Z F3: deterministic scene-load work, no permanent Update or network spawns.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class AirfieldObjects
    {
        internal static int Pending;
        internal static bool Ready;

        internal static void Loaded(Scene scene)
        {
            if (!EastWorld.On) return;
            if (scene.name == EastWorld.SceneName)
            {
                Ready = false;
                GameObject[] roots = scene.GetRootGameObjects();
                if (roots.Length > 0)
                {
                    roots[0].AddComponent<AirfieldNavigationJob>();
                    roots[0].AddComponent<ParkedTu95>();
                }
                return;
            }
            if (scene.name != "EastAirfield" && !scene.name.StartsWith("EastAf", StringComparison.Ordinal)) return;
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == "EastAirfieldRoot" || root.name.StartsWith("EastAf", StringComparison.Ordinal))
                    root.AddComponent<AirfieldObjectsJob>().Begin(scene.name);
        }

        internal static bool Container(string scene, string name)
        {
            return name == scene + "Root" || name == "Apron" || name == "Runway" || name == "Compound"
                || name == "Depot" || name == "Plant";
        }

        internal static void Seat(Transform node, float x, float z)
        {
            Vector3 old = node.position, next = new Vector3(x, old.y, z);
            float before, after;
            if (EastWorld.TerrainHeight(old, out before) && EastWorld.TerrainHeight(next, out after))
                next.y += after - before; // retain the original floor/centre seating offset
            node.position = next;
        }

        internal static void Marker(Transform node)
        {
            if (!node.name.StartsWith("D1|", StringComparison.Ordinal)
                && !node.name.StartsWith("L3|", StringComparison.Ordinal)) return;
            Seat(node, 4125f, 550f);
            node.localScale = new Vector3(90f, node.localScale.y, 86f);
            node.name = node.name.StartsWith("D1|", StringComparison.Ordinal)
                ? "D1|Field fuel depot" : "L3|Loot: field fuel";
        }

        internal static void CompactRadar(Transform root, Transform head)
        {
            if (root == null || head == null || root.Find("FieldSupport") != null) return;
            FrameProf.S(FrameProf.S_AirfieldObjectsLoad);
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != head) child.gameObject.SetActive(false);
            }
            Collider[] old = root.GetComponents<Collider>();
            for (int i = 0; i < old.Length; i++) old[i].enabled = false;
            root.localScale = Vector3.one * AirfieldObjectsCore.RadarScale;
            root.position = TowerRadar.TowerPoint(new Vector3(7.9f,
                TowerRadar.RoofM + AirfieldObjectsCore.RadarLiftM - 8f * AirfieldObjectsCore.RadarScale, 0f));
            root.rotation = Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f);
            // One short support. Mesh and physical box share the damage target.
            GameObject support = GameObject.CreatePrimitive(PrimitiveType.Cube);
            support.name = "FieldSupport";
            support.transform.SetParent(root, false);
            float s = AirfieldObjectsCore.RadarScale;
            support.transform.localPosition = new Vector3(0f, (1.5f - (3f - 8f*s))/s, 0f) * TowerRadar.K;
            support.transform.localScale = new Vector3(0.24f/s, 3f/s, 0.24f/s) * TowerRadar.K;
            Renderer donor = head.GetComponentInChildren<Renderer>();
            if (donor != null) support.GetComponent<Renderer>().sharedMaterial = donor.sharedMaterial;
            if (head.GetComponent<BoxCollider>() == null)
            {
                BoxCollider box = head.gameObject.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, 8f, 1.4f) * TowerRadar.K;
                box.size = new Vector3(9f, 2.7f, 4.2f) * TowerRadar.K;
            }
            FrameProf.E(FrameProf.S_AirfieldObjectsLoad);
        }
    }

    internal sealed class AirfieldObjectsJob : MonoBehaviour
    {
        bool finished;
        internal void Begin(string scene) { AirfieldObjects.Pending++; StartCoroutine(Apply(scene)); }
        IEnumerator Apply(string scene)
        {
            Stack<Transform> pending = new Stack<Transform>();
            pending.Push(transform);
            int hidden = 0, moved = 0;
            bool selective = scene == "EastAfProps" || scene == "EastAfFuelWater" || scene == "EastAfShelters";
            while (pending.Count > 0)
            {
                FrameProf.S(FrameProf.S_AirfieldObjectsLoad);
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    Transform node = pending.Pop();
                    bool model = selective && !AirfieldObjects.Container(scene, node.name);
                    if ((scene == "EastAirfield" && AirfieldObjectsCore.RemoveBase(node.name))
                        || AirfieldObjectsCore.RemoveModel(scene, node.name, model))
                    { node.gameObject.SetActive(false); hidden++; continue; }
                    float x, z;
                    if (AirfieldObjectsCore.Move(node.name, out x, out z))
                    { AirfieldObjects.Seat(node, x, z); moved++; }
                    if (scene == "EastAirfield") AirfieldObjects.Marker(node);
                    if (model) continue; // preserve all descendants of retained models
                    for (int i = 0; i < node.childCount; i++) pending.Push(node.GetChild(i));
                }
                while (pending.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - begin
                    < System.Diagnostics.Stopwatch.Frequency / 4000);
                FrameProf.E(FrameProf.S_AirfieldObjectsLoad);
                yield return null;
            }
            RevivalPlugin.L.LogInfo("AirfieldObjects: " + scene + " hidden roots " + hidden + ", moved " + moved + ".");
            finished = true; AirfieldObjects.Pending--;
            Destroy(this);
        }
        void OnDestroy() { if (!finished) AirfieldObjects.Pending--; }
    }

    // Kept only to own/release finite native nav data until the tile unloads.
    internal sealed class AirfieldNavigationJob : MonoBehaviour
    {
        readonly List<NavMeshData> data = new List<NavMeshData>();
        readonly List<NavMeshDataInstance> instances = new List<NavMeshDataInstance>();
        readonly List<NavMeshLinkInstance> links = new List<NavMeshLinkInstance>();
        IEnumerator Start()
        {
            yield return new WaitForSeconds(15f);
            while (!EastWorld.ContentReady || AirfieldObjects.Pending > 0)
                yield return new WaitForSeconds(0.5f);
            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
            List<NavMeshBuildMarkup> marks = new List<NavMeshBuildMarkup>();
            for (int i = 0; i < AirfieldObjectsCore.Holes.Length; i++)
            {
                float[] h = AirfieldObjectsCore.Holes[i];
                Vector3 centre = new Vector3((h[0]+h[2])*0.5f, 0f, (h[1]+h[3])*0.5f);
                float y;
                if (!EastWorld.TerrainHeight(centre, out y)) yield break;
                centre.y = y;
                Bounds bounds = new Bounds(centre, new Vector3(h[2]-h[0]+16f, 100f, h[3]-h[1]+16f));
                FrameProf.S(FrameProf.S_AirfieldObjectsNav);
                sources.Clear();
                // ALL current physics sources, including terrain, props and
                // fences. No renderer-only bake or synthetic flat floor.
                NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders,
                    CrossingCore.PatchArea, marks, sources);
                // Player/vehicle colliders must never become a static nav floor.
                for (int n = sources.Count - 1; n >= 0; n--)
                {
                    Component c = sources[n].component;
                    Collider collider = c as Collider;
                    if (c != null && (!c.gameObject.scene.name.StartsWith("East", StringComparison.Ordinal)
                        || (collider != null && (collider.isTrigger || collider.attachedRigidbody != null))))
                        sources.RemoveAt(n);
                }
                NavMeshData d = new NavMeshData(0);
                data.Add(d);
                NavMeshBuildSettings settings = CrossingCore.Settings();
                settings.agentRadius = 0.8f; settings.agentSlope = 45f; settings.agentClimb = 0.6f;
                AsyncOperation op = NavMeshBuilder.UpdateNavMeshDataAsync(d, settings, sources, bounds);
                FrameProf.E(FrameProf.S_AirfieldObjectsNav);
                while (op != null && !op.isDone) yield return null;
                FrameProf.S(FrameProf.S_AirfieldObjectsNav);
                instances.Add(NavMesh.AddNavMeshData(d));
                FrameProf.E(FrameProf.S_AirfieldObjectsNav);
                // Additive NavMeshData does not stitch itself to the tile.
                // Short bidirectional links cross each hole's old bake edge.
                int before = links.Count;
                for (int side = 0; side < 4; side++)
                {
                    FrameProf.S(FrameProf.S_AirfieldObjectsNav);
                    Vector3 inner = centre, outer = centre;
                    if (side < 2)
                    {
                        inner.x = side == 0 ? h[0] + 3f : h[2] - 3f;
                        outer.x = side == 0 ? h[0] - 4f : h[2] + 4f;
                    }
                    else
                    {
                        inner.z = side == 2 ? h[1] + 3f : h[3] - 3f;
                        outer.z = side == 2 ? h[1] - 4f : h[3] + 4f;
                    }
                    NavMeshHit a, b;
                    if (EastWorld.TerrainHeight(inner, out y)) inner.y = y;
                    if (EastWorld.TerrainHeight(outer, out y)) outer.y = y;
                    if (NavMesh.SamplePosition(inner, out a, 2f, CrossingCore.PatchMask)
                        && NavMesh.SamplePosition(outer, out b, 2f, CrossingCore.VanillaMask)
                        && Mathf.Abs(a.position.y - b.position.y) < 1.2f)
                        links.Add(CrossingCore.Link(a.position, b.position, 2f));
                    FrameProf.E(FrameProf.S_AirfieldObjectsNav);
                    yield return null;
                }
                if (links.Count == before)
                {
                    RevivalPlugin.L.LogWarning("AirfieldObjects: hole " + i
                        + " has no tile connection; holding defenders/loot for this load.");
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            AirfieldObjects.Ready = true;
            RevivalPlugin.L.LogInfo("AirfieldObjects: five removed building holes patched from current physics, "
                + links.Count + " tile links; defenders/loot ready.");
        }
        void OnDestroy()
        {
            AirfieldObjects.Ready = false;
            for (int i = 0; i < links.Count; i++) links[i].Remove();
            for (int i = 0; i < instances.Count; i++) instances[i].Remove();
            for (int i = 0; i < data.Count; i++)
            { NavMeshBuilder.Cancel(data[i]); Destroy(data[i]); }
        }
    }
}

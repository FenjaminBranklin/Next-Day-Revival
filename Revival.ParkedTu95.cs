// Z V2: load-only abandoned bomber. No flight carrier, rigidbody or ticks.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class ParkedTu95 : MonoBehaviour
    {
        internal const float K = 2.8f, X = 4525f, Z = -700f;
        internal static Transform Root;
        internal static bool Ready;
        readonly List<NavMeshLinkInstance> links = new List<NavMeshLinkInstance>();
        NavMeshData data;
        NavMeshDataInstance instance;
        Mesh rampMesh;

        IEnumerator Start()
        {
            // Content filtering/terrain paint must finish before surveying physics.
            while (!EastWorld.ContentReady || !AirfieldObjects.Ready)
                yield return new WaitForSeconds(0.5f);
            if (!Tu95Model.Load()) yield break;
            float y;
            if (!EastWorld.TerrainHeight(new Vector3(X, 0f, Z), out y)) yield break;
            GameObject plane = new GameObject("NDR_ParkedTu95");
            plane.transform.SetParent(transform, false);
            plane.transform.position = new Vector3(X, y, Z);
            plane.transform.localScale = Vector3.one * K;
            Root = plane.transform;
            // Trees on the wing/access corridor must not grow through the model.
            List<Terrain> terrains = new List<Terrain>();
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
                terrains.AddRange(root.GetComponentsInChildren<Terrain>(true));
            foreach (Terrain terrain in terrains)
            {
                TerrainData td = terrain.terrainData;
                TreeInstance[] trees = td.treeInstances;
                List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
                for (int i = 0; i < trees.Length; i++)
                {
                    Vector3 p = terrain.transform.position + Vector3.Scale(trees[i].position, td.size);
                    if (Mathf.Abs(p.x-X) > 76f || Mathf.Abs(p.z-Z) > 76f) keep.Add(trees[i]);
                    if (i % 512 == 511) yield return null;
                }
                if (keep.Count != trees.Length) td.treeInstances = keep.ToArray();
                yield return null;
            }
            List<Renderer> near = new List<Renderer>();
            near.Add(Part("Body", Tu95Model.Body, Tu95Model.Skin, Vector3.zero, true));
            yield return null;
            near.Add(Part("Glass", Tu95Model.Glass, Tu95Model.Clear, Vector3.zero, false));
            near.Add(Part("Decals", Tu95Model.Decals, Tu95Model.Cutout, Vector3.zero, false));
            for (int i = 0; i < Tu95Model.Props.Count; i++)
            {
                near.Add(Part("Stopped prop", Tu95Model.Prop, Tu95Model.Skin, Tu95Model.Props[i].Value, false));
                yield return null;
            }
            // Bay doors left open, no moving components.
            Part("Open bay L", Tu95Model.BayL, Tu95Model.Skin, Tu95Model.BayLAt, false).transform.localRotation = Quaternion.Euler(0f,0f,-80f);
            Part("Open bay R", Tu95Model.BayR, Tu95Model.Skin, Tu95Model.BayRAt, false).transform.localRotation = Quaternion.Euler(0f,0f,80f);
            Box("Main gear L", new Vector3(-3f,1.2f,0f), new Vector3(0.5f,2.4f,1.7f));
            Box("Main gear R", new Vector3(3f,1.2f,0f), new Vector3(0.5f,2.4f,1.7f));
            Box("Nose gear", new Vector3(0f,1.3f,16f), new Vector3(0.4f,2.6f,1f));
            // Accessible salvage crates in the open hold beneath the fuselage.
            Box("Hold parts", new Vector3(0f,0.25f,3f), new Vector3(1.2f,0.5f,1.2f));
            Box("Hold ammunition", new Vector3(0f,0.25f,6f), new Vector3(1.2f,0.5f,1.2f));
            float foot;
            EastWorld.TerrainHeight(Root.TransformPoint(new Vector3(8f,0f,-22f)), out foot);
            Ramp((foot-y)/K);
            // Native LODGroup: detail drops at distance, body-only silhouette,
            // then cull. Colliders are independent of renderer visibility.
            Renderer far = Part("Far body", Tu95Model.Body, Tu95Model.Skin, Vector3.zero, false);
            LODGroup lod = plane.AddComponent<LODGroup>();
            near.Clear();
            foreach (Renderer renderer in plane.GetComponentsInChildren<Renderer>())
                if (renderer != far) near.Add(renderer);
            lod.SetLODs(new LOD[] { new LOD(0.12f, near.ToArray()), new LOD(0.015f, new Renderer[] { far }) });
            lod.RecalculateBounds();
            yield return null;
            Bounds bounds = new Bounds(new Vector3(X,y+20f,Z),new Vector3(160f,100f,170f));
            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
            FrameProf.S(FrameProf.S_ParkedTu95Nav);
            NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders,
                CrossingCore.PatchArea, new List<NavMeshBuildMarkup>(), sources);
            for (int i=sources.Count-1;i>=0;i--)
            {
                Collider c = sources[i].component as Collider;
                if (c != null && (c.isTrigger || c.attachedRigidbody != null
                    || !c.gameObject.scene.name.StartsWith("East",StringComparison.Ordinal))) sources.RemoveAt(i);
            }
            data = new NavMeshData(0);
            NavMeshBuildSettings settings = CrossingCore.Settings();
            settings.agentRadius=0.8f; settings.agentSlope=45f; settings.agentClimb=0.6f;
            AsyncOperation op = NavMeshBuilder.UpdateNavMeshDataAsync(data,settings,sources,bounds);
            FrameProf.E(FrameProf.S_ParkedTu95Nav);
            while (op != null && !op.isDone) yield return null;
            instance = NavMesh.AddNavMeshData(data);
            // Connect patch ground to the existing tile from all four sides.
            for (int i=0;i<4;i++)
            {
                Vector3 a=new Vector3(X,y,Z), b=a;
                if (i<2) { a.x += i==0 ? -75f : 75f; b.x += i==0 ? -84f : 84f; }
                else { a.z += i==2 ? -80f : 80f; b.z += i==2 ? -89f : 89f; }
                if (EastWorld.TerrainHeight(a,out foot)) a.y=foot;
                if (EastWorld.TerrainHeight(b,out foot)) b.y=foot;
                NavMeshHit ha,hb;
                if (NavMesh.SamplePosition(a,out ha,3f,CrossingCore.PatchMask)
                    && NavMesh.SamplePosition(b,out hb,3f,CrossingCore.VanillaMask))
                    links.Add(CrossingCore.Link(ha.position,hb.position,3f));
                yield return null;
            }
            Ready = true;
            RevivalPlugin.L.LogInfo("ParkedTu95: static bomber, wing ramp, hold loot anchors and all-collider navigation ready; tile links " + links.Count + ".");
            // No Update/FixedUpdate/LateUpdate. Component only owns native nav lifetime.
        }

        static Renderer Part(string name, Mesh mesh, Material material, Vector3 at, bool physical)
        {
            if (mesh == null) return null;
            GameObject go = new GameObject(name);
            go.transform.SetParent(Root,false); go.transform.localPosition=at;
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            MeshRenderer r=go.AddComponent<MeshRenderer>(); r.sharedMaterial=material;
            if (physical) go.AddComponent<MeshCollider>().sharedMesh=mesh;
            return r;
        }
        static void Box(string name,Vector3 at,Vector3 size)
        {
            GameObject go=GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name=name; go.transform.SetParent(Root,false);
            go.transform.localPosition=at; go.transform.localScale=size;
            go.GetComponent<Renderer>().sharedMaterial=Tu95Model.Skin;
        }
        void Ramp(float foot)
        {
            // Actual wing mesh at x=8,z=0 is y=3.912 m. Width 2 m.
            Mesh mesh=new Mesh();
            rampMesh=mesh;
            mesh.vertices=new Vector3[] { new Vector3(7f,foot+0.03f,-22f),new Vector3(9f,foot+0.03f,-22f),
                new Vector3(7f,3.94f,0f),new Vector3(9f,3.94f,0f) };
            mesh.triangles=new int[] {0,2,1,1,2,3}; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            Part("Salvage wing ramp",mesh,Tu95Model.Skin,Vector3.zero,true);
        }
        internal static bool Loot(int index,out Vector3 at)
        {
            at=Vector3.zero;
            if (!Ready || Root == null) return false;
            at=Root.TransformPoint(new Vector3(0f,0.5f,index==0 ? 3f : 6f));
            return true;
        }
        void OnDestroy()
        {
            Ready=false; Root=null;
            for (int i=0;i<links.Count;i++) links[i].Remove();
            instance.Remove();
            if (data != null) { NavMeshBuilder.Cancel(data); Destroy(data); }
            if (rampMesh != null) Destroy(rampMesh);
        }
    }
}

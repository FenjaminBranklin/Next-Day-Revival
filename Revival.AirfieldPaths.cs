// C W4: finite, profiled scene-load construction using the shipped surface kit.
// No Update, network traffic, permanent search or steady-state allocations.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal sealed class AirfieldPathsJob : MonoBehaviour
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        Material earth, concrete, cracked;
        Vector3 origin;
        float footY;
        float[] pitY;

        IEnumerator Start()
        {
            // Wait for the actual additive scenes and the object-removal jobs,
            // including fallback selection. Never race against partial content.
            while (!EastWorld.ContentReady || AirfieldObjects.Pending!=0) yield return new WaitForSeconds(0.25f);
            Scene scene=SceneManager.GetSceneByName("EastAirfield");
            if (!scene.isLoaded) { Fail("EastAirfield scene missing"); yield break; }
            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
            GameObject[] roots=scene.GetRootGameObjects();
            Stack<Transform> todo=new Stack<Transform>();
            foreach (GameObject r in roots) if (r.name=="EastAirfieldRoot")
            {
                Transform surfaces=r.transform.Find("Surfaces");
                if (surfaces!=null) todo.Push(surfaces);
            }
            FrameProf.E(FrameProf.S_AirfieldGroundLoad);
            // Search only the surface kit hierarchy, in bounded load slices.
            while (todo.Count>0)
            {
                FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                long begin=System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    Transform t=todo.Pop(); Renderer r=t.GetComponent<Renderer>();
                    if (r!=null) foreach (Material m in r.sharedMaterials)
                    {
                        if (m==null) continue;
                        if (m.name=="surf_earth") earth=m;
                        else if (m.name=="surf_pag") concrete=m;
                        else if (m.name=="surf_pag_cracked") cracked=m;
                    }
                    for (int i=0;i<t.childCount;i++) todo.Push(t.GetChild(i));
                } while (todo.Count>0 && System.Diagnostics.Stopwatch.GetTimestamp()-begin
                    < System.Diagnostics.Stopwatch.Frequency/4000);
                FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                if (earth!=null && concrete!=null && cracked!=null) break;
                yield return null;
            }
            if (earth==null || concrete==null || cracked==null) { Fail("shipped surface kit materials missing"); yield break; }
            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
            origin=new Vector3(4250f,0f,1335f);
            float y;
            if (!EastWorld.TerrainHeight(origin,out y)) { FrameProf.E(FrameProf.S_AirfieldGroundLoad); Fail("terrain missing"); yield break; }
            origin.y=y;
            Vector3 foot=new Vector3(AirfieldPathsCore.FootX,0f,AirfieldPathsCore.FootZ);
            EastWorld.TerrainHeight(foot,out footY);
            // The stair approach is on terrain, west of its first tread.
            // Use static terrain only, so a player/vehicle there during load
            // cannot change the path height on one multiplayer client.
            pitY=new float[FlakPositionsCore.X.Length];
            for (int p=0;p<pitY.Length;p++) EastWorld.TerrainHeight(
                new Vector3(FlakPositionsCore.X[p],0f,FlakPositionsCore.Z[p]),out pitY[p]);
            GameObject root=new GameObject("NDR tower ground paths");
            SceneManager.MoveGameObjectToScene(root,gameObject.scene);
            root.transform.position=origin;
            // This owner survives only to destroy its native meshes on unload.
            root.AddComponent<AirfieldPathsMeshes>().Meshes=meshes;
            FrameProf.E(FrameProf.S_AirfieldGroundLoad);
            yield return null;
            for (int edge=0;edge<AirfieldPathsCore.Edges.Length;edge++)
                for (int layer=0;layer<2;layer++)
                {
                    List<Vector3> vertices=new List<Vector3>();
                    List<Vector2> uv=new List<Vector2>();
                    List<int>[] triangles={new List<int>(),new List<int>(),new List<int>()};
                    int step=0,chunk=0;
                    while (step<AirfieldPathsCore.Steps(edge))
                    {
                        FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                        long begin=System.Diagnostics.Stopwatch.GetTimestamp();
                        do
                        {
                            List<AirfieldPathsCore.Point[]> panels=AirfieldPathsCore.Triangles(edge,step,layer);
                            int mat=AirfieldPathsCore.Material(edge,step,layer);
                            foreach (AirfieldPathsCore.Point[] panel in panels)
                                Add(panel,layer,mat,vertices,uv,triangles);
                            step++;
                        } while (step<AirfieldPathsCore.Steps(edge) && vertices.Count<2400
                            && System.Diagnostics.Stopwatch.GetTimestamp()-begin<System.Diagnostics.Stopwatch.Frequency/4000);
                        FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                        yield return null;
                        // Small spatial chunks retain normal frustum culling.
                        if (vertices.Count>=2400 || step==AirfieldPathsCore.Steps(edge))
                        {
                            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                            if (vertices.Count>0) Put(root.transform,edge,layer,chunk++,vertices,uv,triangles);
                            vertices.Clear(); uv.Clear(); foreach (List<int> t in triangles) t.Clear();
                            FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                            yield return null;
                        }
                    }
                }
            RevivalPlugin.L.LogInfo("AirfieldPaths: tower north stair connected to apron, AA-NW, AA-NE, AA-S and ZU-W; "
                +meshes.Count+" terrain-draped kit chunks, load job complete.");
            Destroy(this);
        }

        void Fail(string reason) { RevivalPlugin.L.LogError("AirfieldPaths: "+reason); Destroy(this); }
        void Add(AirfieldPathsCore.Point[] p,int layer,int mat,List<Vector3> v,List<Vector2> uv,List<int>[] tris)
        {
            foreach (AirfieldPathsCore.Point point in p)
            {
                Vector3 w=new Vector3((float)point.X,0f,(float)point.Z); float y;
                EastWorld.TerrainHeight(w,out y);
                w.y=y+AirfieldPathsCore.Lift(point.X,point.Z,y,footY,pitY)
                    +(layer==0 ? AirfieldPathsCore.DirtLift : AirfieldPathsCore.SlabLift);
                tris[mat].Add(v.Count); v.Add(w-origin);
                float tile=mat==0 ? 15f : mat==1 ? 23.75f : 25.62f;
                uv.Add(new Vector2(w.x/tile,w.z/tile));
            }
        }
        void Put(Transform parent,int edge,int layer,int chunk,List<Vector3> v,List<Vector2> uv,List<int>[] tris)
        {
            Mesh mesh=new Mesh(); mesh.name="Tower path "+edge+" "+layer+" "+chunk;
            mesh.vertices=v.ToArray(); mesh.uv=uv.ToArray(); mesh.subMeshCount=3;
            for (int i=0;i<3;i++) mesh.SetTriangles(tris[i].ToArray(),i);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); meshes.Add(mesh);
            GameObject go=new GameObject(mesh.name); go.transform.SetParent(parent,false);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            MeshRenderer r=go.AddComponent<MeshRenderer>(); r.sharedMaterials=new Material[] {earth,concrete,cracked};
            r.shadowCastingMode=ShadowCastingMode.Off; r.receiveShadows=true;
            int end=AirfieldPathsCore.Edges[edge][1];
            if (layer==0 && (end==6 || end==8 || end==11 || end==15))
                go.AddComponent<MeshCollider>().sharedMesh=mesh;
            // The four short gate ramps bridge the existing 0.4 u pad lip.
            // All other paths use the terrain/pavement floor, like the kit.
        }
    }
    internal sealed class AirfieldPathsMeshes : MonoBehaviour
    {
        internal List<Mesh> Meshes;
        void OnDestroy() { if (Meshes!=null) foreach (Mesh mesh in Meshes) if (mesh!=null) Destroy(mesh); }
    }
}

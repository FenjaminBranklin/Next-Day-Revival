// Z TC1: static Soviet command post, batched during a finite load coroutine.
// No Update/LateUpdate, physics scans, network messages or steady allocations.
// Every client builds the same recipe in the console's scene/lifetime.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal sealed class TowerCommandRoom : MonoBehaviour
    {
        const float K = TowerRadar.K;
        readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>(16);
        readonly List<Vector3> _vertices = new List<Vector3>(2048);
        readonly List<Vector2> _uv = new List<Vector2>(2048);
        readonly List<int> _indices = new List<int>(3072);

        internal static void Attach(Transform console)
        {
            // A fallback greybox has no real cab. Do not furnish thin air.
            if (TowerRadar.Tower != null) console.gameObject.AddComponent<TowerCommandRoom>();
        }

        IEnumerator Start()
        {
            // Return before any work; the existing console load frame stays small.
            yield return null;
            Transform frame;
            List<TowerCommandRoomCore.Piece> pieces;
            FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
            try {
                GameObject room = new GameObject("NDR C1 command room");
                room.transform.SetParent(transform, false);
                room.transform.localPosition = -TowerRadar.ConsoleLocalM * K;
                // The radar console y is the measured cab floor, not an estimate.
                frame = room.transform;
                pieces = TowerCommandRoomCore.Pieces();
            } finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
            yield return null;
            Material[] mats=new Material[8];
            for (int i=0;i<mats.Length;i++) {
                FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
                try { mats[i]=MaterialFor(i); }
                finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
                yield return null;
            }
            // One collider per major prop, not per knob/leg/bag. Carving only
            // once while stationary keeps merc navigation out of furniture.
            for (int i=0;i<pieces.Count;i++) {
                TowerCommandRoomCore.Piece p=pieces[i];
                if (!p.Solid) continue;
                FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
                try {
                    GameObject go=new GameObject(p.Name);
                    go.transform.SetParent(frame,false);
                    go.transform.localPosition=new Vector3(p.X,p.Y,p.Z)*K;
                    go.isStatic=true;
                    Vector3 size=new Vector3(p.SX,p.SY,p.SZ)*K;
                    go.AddComponent<BoxCollider>().size=size;
                    NavMeshObstacle obstacle=go.AddComponent<NavMeshObstacle>();
                    obstacle.shape=NavMeshObstacleShape.Box;
                    obstacle.size=size;
                    obstacle.carving=true;
                    obstacle.carveOnlyStationary=true;
                } finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
                yield return null;
            }
            // Small mesh chunks spread construction across frames. Rendering
            // ends with at most one mesh per material, zero primitive objects.
            for (int material=0;material<mats.Length;material++) {
                _vertices.Clear(); _uv.Clear(); _indices.Clear();
                int n=0;
                for (int i=0;i<pieces.Count;i++) {
                    TowerCommandRoomCore.Piece p=pieces[i];
                    if (p.Solid || p.Material!=material) continue;
                    FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
                    try { Geometry(p); }
                    finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
                    if (++n%8==0) yield return null;
                }
                FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
                try {
                    if (_vertices.Count>0) {
                        Mesh mesh=new Mesh(); mesh.name="C1 command room batch "+material;
                        _owned.Add(mesh);
                        mesh.SetVertices(_vertices); mesh.SetUVs(0,_uv);
                        mesh.SetTriangles(_indices,0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                        GameObject go=new GameObject(mesh.name);
                        go.transform.SetParent(frame,false); go.isStatic=true;
                        go.AddComponent<MeshFilter>().sharedMesh=mesh;
                        MeshRenderer renderer=go.AddComponent<MeshRenderer>();
                        renderer.sharedMaterial=mats[material];
                        if (material==TowerCommandRoomCore.Lamp) renderer.shadowCastingMode=ShadowCastingMode.Off;
                    }
                } finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
                yield return null;
            }
            _vertices.Clear(); _uv.Clear(); _indices.Clear();
            RevivalPlugin.L.LogInfo("TowerCommandRoom: static command post ready; radar access retained, east desk reserved for Z M4.");
        }

        Material Own(string name, Color color, Material donor)
        {
            Material m=donor!=null?new Material(donor):new Material(Shader.Find("Standard"));
            m.name=name; m.color=color;
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic",.1f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness",.3f);
            _owned.Add(m); return m;
        }

        Material MaterialFor(int id)
        {
            if (id==TowerCommandRoomCore.Olive) return RadarModel.Sheet;
            if (id==TowerCommandRoomCore.Steel) return RadarModel.Steel;
            if (id==TowerCommandRoomCore.Wood) return RadarModel.Wood;
            if (id==TowerCommandRoomCore.Black) return RadarModel.Knob;
            if (id==TowerCommandRoomCore.Paper) return Own("C1 faded paper",new Color(.52f,.49f,.37f),null);
            if (id==TowerCommandRoomCore.Canvas) {
                Material canvas=Own("C1 sandbag canvas",Color.white,null);
                Texture2D weave=new Texture2D(32,32,TextureFormat.RGBA32,true);
                Color32[] pixels=new Color32[32*32];
                for (int y=0;y<32;y++) for (int x=0;x<32;x++) {
                    int shade=(x*3+y*7+x*y)%14+((x+y)%2==0?0:9);
                    pixels[y*32+x]=new Color32((byte)(112+shade),(byte)(99+shade),(byte)(73+shade),255);
                }
                weave.name="C1 coarse canvas"; weave.SetPixels32(pixels); weave.Apply(true,true);
                _owned.Add(weave); canvas.mainTexture=weave; return canvas;
            }
            if (id==TowerCommandRoomCore.Lamp) {
                Material lamp=Own("C1 warm lamp diffuser",new Color(.67f,.55f,.32f),null);
                // No realtime lights or shadows; a subdued emissive diffuser.
                lamp.EnableKeyword("_EMISSION"); lamp.SetColor("_EmissionColor",new Color(.22f,.16f,.07f));
                return lamp;
            }
            Material map=Own("C1 field chart",Color.white,null);
            Texture2D texture=Chart(); _owned.Add(texture); map.mainTexture=texture;
            return map;
        }

        static Texture2D Chart()
        {
            // Small procedural tactical chart, not a map/HUD/target selector.
            // The concept's single strip in a grassy clearing is schematic.
            const int size=128;
            Color32[] pixels=new Color32[size*size];
            for (int y=0;y<size;y++) for (int x=0;x<size;x++) {
                int noise=(x*13+y*7+x*y)%13;
                byte b=(byte)(151+noise);
                Color32 c=new Color32(b,(byte)(b-5),(byte)(b-29),255);
                if (x%16==0 || y%16==0) c=new Color32(119,124,100,255);
                bool clearing=x>42 && x<88 && y>8 && y<119;
                if (clearing && ((x+y)%5==0)) c=new Color32(133,139,109,255);
                if (x>=62 && x<=67 && y>=14 && y<=112) c=new Color32(97,91,75,255);
                if ((x==44 || x==86) && y>9 && y<118 || (y==10 || y==117) && x>44 && x<86)
                    c=new Color32(121,101,75,255);
                // Three pencil bearing/route lines to the retained tower.
                if (y==65 && x>29 && x<61 || x==35 && y>52 && y<64)
                    c=new Color32(107,61,49,255);
                pixels[y*size+x]=c;
            }
            Texture2D texture=new Texture2D(size,size,TextureFormat.RGBA32,true);
            texture.name="C1 worn field chart"; texture.wrapMode=TextureWrapMode.Clamp;
            texture.SetPixels32(pixels); texture.Apply(true,true); return texture;
        }

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int n=_vertices.Count;
            _vertices.Add(a); _vertices.Add(b); _vertices.Add(c); _vertices.Add(d);
            _uv.Add(new Vector2(0,0)); _uv.Add(new Vector2(1,0));
            _uv.Add(new Vector2(1,1)); _uv.Add(new Vector2(0,1));
            _indices.Add(n); _indices.Add(n+1); _indices.Add(n+2);
            _indices.Add(n); _indices.Add(n+2); _indices.Add(n+3);
        }

        void Geometry(TowerCommandRoomCore.Piece p)
        {
            Vector3 center=new Vector3(p.X,p.Y,p.Z)*K;
            Vector3 h=new Vector3(p.SX,p.SY,p.SZ)*(K*.5f);
            if (p.Bag) {
                // Beveled rectangular sack: octagonal horizontal section,
                // smaller top/bottom rings; no expensive sphere primitives.
                for (int i=0;i<8;i++) {
                    int j=(i+1)%8;
                    Vector3 a=Ring(i,h),b=Ring(j,h);
                    Face(center+new Vector3(a.x*.8f,h.y,a.z*.8f),
                        center+new Vector3(b.x*.8f,h.y,b.z*.8f),center+b,center+a);
                    Face(center+a,center+b,center+new Vector3(b.x*.8f,-h.y,b.z*.8f),
                        center+new Vector3(a.x*.8f,-h.y,a.z*.8f));
                    Triangle(center+new Vector3(0,h.y,0),center+new Vector3(b.x*.8f,h.y,b.z*.8f),
                        center+new Vector3(a.x*.8f,h.y,a.z*.8f));
                    Triangle(center+new Vector3(0,-h.y,0),center+new Vector3(a.x*.8f,-h.y,a.z*.8f),
                        center+new Vector3(b.x*.8f,-h.y,b.z*.8f));
                }
                return;
            }
            Vector3 l=center-h, u=center+h;
            Face(new Vector3(l.x,u.y,l.z),new Vector3(l.x,u.y,u.z),new Vector3(u.x,u.y,u.z),new Vector3(u.x,u.y,l.z));
            Face(new Vector3(l.x,l.y,u.z),new Vector3(l.x,l.y,l.z),new Vector3(u.x,l.y,l.z),new Vector3(u.x,l.y,u.z));
            Face(new Vector3(l.x,l.y,l.z),new Vector3(l.x,u.y,l.z),new Vector3(u.x,u.y,l.z),new Vector3(u.x,l.y,l.z));
            Face(new Vector3(u.x,l.y,u.z),new Vector3(u.x,u.y,u.z),new Vector3(l.x,u.y,u.z),new Vector3(l.x,l.y,u.z));
            Face(new Vector3(l.x,l.y,u.z),new Vector3(l.x,u.y,u.z),new Vector3(l.x,u.y,l.z),new Vector3(l.x,l.y,l.z));
            Face(new Vector3(u.x,l.y,l.z),new Vector3(u.x,u.y,l.z),new Vector3(u.x,u.y,u.z),new Vector3(u.x,l.y,u.z));
        }

        static Vector3 Ring(int i, Vector3 h)
        {
            switch (i) {
                case 0:return new Vector3(-h.x,0,-h.z*.65f);
                case 1:return new Vector3(-h.x*.65f,0,-h.z);
                case 2:return new Vector3(h.x*.65f,0,-h.z);
                case 3:return new Vector3(h.x,0,-h.z*.65f);
                case 4:return new Vector3(h.x,0,h.z*.65f);
                case 5:return new Vector3(h.x*.65f,0,h.z);
                case 6:return new Vector3(-h.x*.65f,0,h.z);
                default:return new Vector3(-h.x,0,h.z*.65f);
            }
        }

        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            int n=_vertices.Count;
            _vertices.Add(a); _vertices.Add(b); _vertices.Add(c);
            _uv.Add(new Vector2(.5f,.5f)); _uv.Add(new Vector2(1,0)); _uv.Add(new Vector2(0,0));
            _indices.Add(n); _indices.Add(n+1); _indices.Add(n+2);
        }

        void OnDestroy()
        {
            for (int i=0;i<_owned.Count;i++) if (_owned[i]!=null) Destroy(_owned[i]);
            _owned.Clear();
        }
    }
}

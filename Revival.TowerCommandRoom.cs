// Z TC1: static Soviet command post, batched during a finite load coroutine.
// No Update/LateUpdate, physics scans, network messages or steady allocations.
// Every client builds the same recipe in the console's scene/lifetime.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        static TowerCommandRoom _active;
        bool _terminal;
        Transform _supplyKit;
        bool _supplyWasOn;
        TowerCommandRoomCore.Piece _piece;
        Quaternion _turn;
        Material _surface;
        static readonly float[] Density = { 60f,87f,36.3f,78.5f,123f,131f,23f,446f,62.9f };
        readonly List<Renderer> _screens = new List<Renderer>(3);
        readonly List<Material[]> _lit = new List<Material[]>(3), _dim = new List<Material[]>(3);

        internal static void AttachTerminal(Transform root)
        {
            TowerCommandRoom room = root.gameObject.AddComponent<TowerCommandRoom>();
            room._terminal = true;
        }

        internal static void ScreenState(bool lit, bool dead)
        {
            if (_active == null) return;
            for (int i = 0; i < _active._screens.Count; i++)
                if (_active._screens[i] != null)
                    _active._screens[i].sharedMaterials = lit && !dead ? _active._lit[i] : _active._dim[i];
        }

        Transform TerminalKit()
        {
            if (TowerRadar.KitRoom == null) return null;
            _supplyKit=TowerRadar.KitRoom.Find("Supply");
            if (_supplyKit == null) return null;
            _supplyWasOn=_supplyKit.gameObject.activeSelf;
            Vector3 at=TowerRadar.KitRoom.TransformPoint(new Vector3(TowerCommandRoomCore.SupplyX,TowerCommandRoomCore.SupplyH*.5f,TowerCommandRoomCore.SupplyZ)*K);
            bool match=(at-transform.position).sqrMagnitude<.01f*K*K;
            _supplyKit.gameObject.SetActive(match);
            return match ? _supplyKit : null;
        }

        internal static void Attach(Transform console)
        {
            // A fallback greybox has no real cab. Do not furnish thin air.
            if (TowerRadar.Tower != null) console.gameObject.AddComponent<TowerCommandRoom>();
        }

        Transform Frame(string name, float y)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            if (_terminal) {
                go.transform.localPosition = Vector3.down * (TowerCommandRoomCore.SupplyH * .5f * K);
                return go.transform;
            }
            Vector3 origin = TowerRadar.TowerPoint(Vector3.zero);
            origin.y = y;
            go.transform.position = origin;
            go.transform.rotation = Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f);
            return go.transform;
        }

        IEnumerator Start()
        {
            // Return before any work; the existing console load frame stays small.
            yield return null;
            Transform frame;
            List<TowerCommandRoomCore.Piece> pieces;
            FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
            try {
                // C W3: the command room stays INSIDE the cab at its measured
                // floor in the tower frame; E W1: the radar console stands in
                // it too. The room shares the console's lifetime, not its position.
                frame = Frame("NDR C1 command room", TowerRadar.CabFloorY);
                pieces = _terminal ? TowerCommandRoomCore.Terminal() : TowerCommandRoomCore.Pieces();
                if (!_terminal) _active = this;
            } finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
            yield return null;
            Transform kit = _terminal ? TerminalKit() : TowerRadar.KitRoom;
            Material[] mats=new Material[TowerCommandRoomCore.Materials];
            for (int i=0;i<mats.Length;i++) {
                FrameProf.S(FrameProf.S_TowerCommandRoomLoad);
                try { if (kit == null) mats[i]=MaterialFor(i); }
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
                    go.transform.localRotation=Quaternion.Euler(p.Tilt,p.Yaw,0f);
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
            if (kit != null) {
                if (_terminal) yield break;
                Renderer[] renderers = kit.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i].name == "Screen") RegisterScreen(renderers[i]);
                WarmLight(frame);
                ScreenState(TowerRadar.Working && TowerRadar.B(TowerRadar.CfgGlow), !TowerRadar.ConsoleAlive);
                yield break;
            }
            // Small mesh chunks spread construction across frames. Rendering
            // ends with at most one mesh per material, zero primitive objects.
            for (int material=0;material<mats.Length;material++) {
                _surface=mats[material];
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
                        if (material==TowerCommandRoomCore.Screen) RegisterScreen(renderer);
                        if (material==TowerCommandRoomCore.Lamp || material==TowerCommandRoomCore.Screen
                            || material==TowerCommandRoomCore.Paper || material==TowerCommandRoomCore.Glass)
                            renderer.shadowCastingMode=ShadowCastingMode.Off;
                    }
                } finally { FrameProf.E(FrameProf.S_TowerCommandRoomLoad); }
                yield return null;
            }
            _vertices.Clear(); _uv.Clear(); _indices.Clear();
            if (!_terminal) {
                WarmLight(frame);
                ScreenState(TowerRadar.Working && TowerRadar.B(TowerRadar.CfgGlow), !TowerRadar.ConsoleAlive);
            }
            RevivalPlugin.L.LogInfo("TowerCommandRoom: static command post ready in the cab with the radar console; east desk reserved for Z M4.");
        }

        void RegisterScreen(Renderer renderer)
        {
            Material[] lit = renderer.sharedMaterials, dim = new Material[lit.Length];
            for (int i = 0; i < lit.Length; i++) {
                dim[i] = Own("C1 unpowered CRT", new Color(.08f,.08f,.08f), lit[i]);
                if (dim[i].HasProperty("_EmissionColor")) dim[i].SetColor("_EmissionColor",Color.black);
                dim[i].DisableKeyword("_EMISSION");
            }
            _screens.Add(renderer); _lit.Add(lit); _dim.Add(dim);
            if (TowerRadar.Screen == null) TowerRadar.Screen = renderer;
        }

        static void WarmLight(Transform frame)
        {
            GameObject go = new GameObject("C1 warm ceiling light");
            go.transform.SetParent(frame,false);
            go.transform.localPosition = new Vector3(7.2f,2.65f,0f)*K;
            Light lamp = go.AddComponent<Light>();
            lamp.type = LightType.Point; lamp.range=6f*K; lamp.intensity=.32f;
            lamp.color=new Color(1f,.76f,.48f); lamp.shadows=LightShadows.None;
            lamp.renderMode=LightRenderMode.ForceVertex;
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
            string[] names = { "metal_painted_dk", "veh_kung", "Metal_Ext_1", "wood_painted_03",
                "Metal_Bare", "C1C_Papers", "C1C_Lamp", "C1C_Screens", "glass_clear" };
            Material kit = RadarModel.Pick(TowerRadar.KitRoom,names[id]);
            if (kit != null) return kit;
            if (id==TowerCommandRoomCore.Grey) return RadarModel.Grey;
            if (id==TowerCommandRoomCore.Olive) return RadarModel.Sheet;
            if (id==TowerCommandRoomCore.Steel) return RadarModel.Steel;
            if (id==TowerCommandRoomCore.Wood) return RadarModel.Wood;
            if (id==TowerCommandRoomCore.Black) return RadarModel.Knob;
            if (id==TowerCommandRoomCore.Glass) {
                Material glass = Own("C1 perspex",new Color(.7f,.75f,.72f,.20f),RadarModel.DarkGlass);
                glass.SetFloat("_Mode",3f); glass.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);
                glass.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha); glass.SetInt("_ZWrite",0);
                glass.EnableKeyword("_ALPHABLEND_ON"); glass.renderQueue=3000;
                return glass;
            }
            string name = id==TowerCommandRoomCore.Paper ? "papers" : id==TowerCommandRoomCore.Screen ? "screens" : "lamp";
            Material m = Own("C1 " + name,Color.white,null);
            Texture2D texture = new Texture2D(4,4,TextureFormat.RGBA32,true);
            texture.LoadImage(File.ReadAllBytes(Path.Combine(RevivalPlugin.AssetDir,"c1_room_"+name+".png")));
            texture.wrapMode=TextureWrapMode.Clamp; _owned.Add(texture); m.mainTexture=texture;
            if (id==TowerCommandRoomCore.Paper) {
                m.SetFloat("_Mode",1f); m.SetFloat("_Cutoff",.15f);
                m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue=2450;
            } else {
                m.EnableKeyword("_EMISSION"); m.SetTexture("_EmissionMap",texture);
                m.SetColor("_EmissionColor",id==TowerCommandRoomCore.Screen ? new Color(.7f,.7f,.7f) : new Color(.35f,.27f,.17f));
            }
            return m;
        }

        Vector3 Point(Vector3 v)
        {
            return (new Vector3(_piece.X,_piece.Y,_piece.Z)+_turn*v)*K;
        }

        Vector2 Planar(Vector3 v, Vector3 n)
        {
            if (Mathf.Abs(n.y) >= Mathf.Abs(n.x) && Mathf.Abs(n.y) >= Mathf.Abs(n.z)) return new Vector2(v.x,n.y>0f ? v.z : -v.z);
            if (Mathf.Abs(n.z) >= Mathf.Abs(n.x)) return new Vector2(n.z > 0f ? -v.x : v.x,v.y);
            return new Vector2(n.x > 0f ? v.z : -v.z,v.y);
        }

        void Polygon(Vector3[] local, Vector3 normal)
        {
            Vector3 nrm=_turn*normal;
            Vector2[] uv=new Vector2[local.Length];
            Vector2 lo=new Vector2(float.MaxValue,float.MaxValue),hi=new Vector2(float.MinValue,float.MinValue);
            for (int i=0;i<local.Length;i++) {
                uv[i]=Planar(_turn*local[i],nrm);
                lo=Vector2.Min(lo,uv[i]); hi=Vector2.Max(hi,uv[i]);
            }
            int first=_vertices.Count, region=_piece.Region*4;
            for (int i=0;i<local.Length;i++) {
                _vertices.Add(Point(local[i]));
                if (_piece.Region>0) {
                    float u=hi.x-lo.x>.000001f ? (uv[i].x-lo.x)/(hi.x-lo.x) : .5f;
                    float v=hi.y-lo.y>.000001f ? (uv[i].y-lo.y)/(hi.y-lo.y) : .5f;
                    uv[i]=new Vector2(Mathf.Lerp(TowerCommandRoomCore.Region[region],TowerCommandRoomCore.Region[region+2],u),
                        Mathf.Lerp(1f-TowerCommandRoomCore.Region[region+3],1f-TowerCommandRoomCore.Region[region+1],v));
                } else if (_surface.mainTexture != null) {
                    Texture texture=_surface.mainTexture;
                    Vector2 scale=_surface.mainTextureScale;
                    uv[i]=new Vector2(uv[i].x*K*Density[_piece.Material]/texture.width/Mathf.Max(.001f,scale.x),
                        uv[i].y*K*Density[_piece.Material]/texture.height/Mathf.Max(.001f,scale.y));
                }
                _uv.Add(uv[i]);
            }
            bool forward=Vector3.Dot(Vector3.Cross(local[1]-local[0],local[2]-local[0]),normal)>0f;
            for (int i=1;i<local.Length-1;i++) {
                _indices.Add(first); _indices.Add(first+(forward ? i : i+1)); _indices.Add(first+(forward ? i+1 : i));
            }
        }

        void Geometry(TowerCommandRoomCore.Piece p)
        {
            _piece=p; _turn=Quaternion.Euler(p.Tilt,p.Yaw,0f);
            Vector3 h=new Vector3(p.SX,p.SY,p.SZ)*.5f;
            if (p.Shape==TowerCommandRoomCore.Box) {
                Polygon(new Vector3[] { new Vector3(-h.x,h.y,-h.z),new Vector3(-h.x,h.y,h.z),new Vector3(h.x,h.y,h.z),new Vector3(h.x,h.y,-h.z) },Vector3.up);
                Polygon(new Vector3[] { new Vector3(-h.x,-h.y,h.z),new Vector3(-h.x,-h.y,-h.z),new Vector3(h.x,-h.y,-h.z),new Vector3(h.x,-h.y,h.z) },Vector3.down);
                Polygon(new Vector3[] { new Vector3(-h.x,-h.y,-h.z),new Vector3(-h.x,h.y,-h.z),new Vector3(h.x,h.y,-h.z),new Vector3(h.x,-h.y,-h.z) },Vector3.back);
                Polygon(new Vector3[] { new Vector3(h.x,-h.y,h.z),new Vector3(h.x,h.y,h.z),new Vector3(-h.x,h.y,h.z),new Vector3(-h.x,-h.y,h.z) },Vector3.forward);
                Polygon(new Vector3[] { new Vector3(-h.x,-h.y,h.z),new Vector3(-h.x,h.y,h.z),new Vector3(-h.x,h.y,-h.z),new Vector3(-h.x,-h.y,-h.z) },Vector3.left);
                Polygon(new Vector3[] { new Vector3(h.x,-h.y,-h.z),new Vector3(h.x,h.y,-h.z),new Vector3(h.x,h.y,h.z),new Vector3(h.x,-h.y,h.z) },Vector3.right);
                return;
            }
            int count=TowerCommandRoomCore.Sides(p);
            Vector3 axis=p.Shape==TowerCommandRoomCore.CylY ? Vector3.up : p.Shape==TowerCommandRoomCore.CylZ ? Vector3.forward : Vector3.right;
            Vector3 a=p.Shape==TowerCommandRoomCore.CylX ? Vector3.up : Vector3.right;
            Vector3 b=p.Shape==TowerCommandRoomCore.CylY ? Vector3.forward : p.Shape==TowerCommandRoomCore.CylZ ? Vector3.up : Vector3.forward;
            float radius=p.Shape==TowerCommandRoomCore.CylX ? h.y : h.x;
            float length=p.Shape==TowerCommandRoomCore.CylY ? h.y : p.Shape==TowerCommandRoomCore.CylZ ? h.z : h.x;
            Vector3[] lower=new Vector3[count],upper=new Vector3[count];
            for (int i=0;i<count;i++) {
                float angle=2f*Mathf.PI*i/count;
                Vector3 ring=(a*Mathf.Cos(angle)+b*Mathf.Sin(angle))*radius;
                lower[i]=ring-axis*length; upper[i]=ring+axis*length;
            }
            for (int i=0;i<count;i++) {
                int j=(i+1)%count;
                Polygon(new Vector3[] { lower[i],lower[j],upper[j],upper[i] },((lower[i]+upper[i]+lower[j]+upper[j])*.25f).normalized);
            }
            Polygon(lower,-axis); Polygon(upper,axis);
        }

        void OnDestroy()
        {
            if (_supplyKit != null) _supplyKit.gameObject.SetActive(_supplyWasOn);
            if (_active == this) { ScreenState(true,false); _active=null; }
            for (int i=0;i<_owned.Count;i++) if (_owned[i]!=null) Destroy(_owned[i]);
            _owned.Clear();
        }
    }
}

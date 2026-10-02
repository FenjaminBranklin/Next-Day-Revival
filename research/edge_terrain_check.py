"""Offline check of the *production C#* edge geometry using real, read-only terrain data.

Extracts the height/UV/row/stitch math, compiles it with small math-only Unity
shims and runs without Unity or the game. Outputs build time, triangle count,
all-edge seam heights/colours, winding, coverage and pilot query timing.
The cache is decoded by terrainmap.decode_heights (serialized x-major).
Also executes the production edge-forest coroutine and shared card geometry.
Its density input uses real forest masks and the observed Normal far-card
count (74601; override with --far-cards=N after a fresh runtime log).
Native mesh uploads, shader rendering and F6 timing still need Kevin's game.
"""
from pathlib import Path
import os
import re
import subprocess
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "research"))
import terrainmap


def block(text, anchor):
    start = text.index(anchor)
    brace = text.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


SHIM = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
class Terrain { }
class TerrainData { public int alphamapLayers; }
class Material { }
class Texture2D { }
struct Vector2 {
 public float x,y;
 public Vector2(float a,float b) { x=a; y=b; }
 public float sqrMagnitude { get { return x*x+y*y; } }
 public float magnitude { get { return Mathf.Sqrt(x*x+y*y); } }
 public Vector2 normalized { get { float m=magnitude; return m==0?new Vector2():this/m; } }
 public static Vector2 operator +(Vector2 a,Vector2 b) { return new Vector2(a.x+b.x,a.y+b.y); }
 public static Vector2 operator -(Vector2 a,Vector2 b) { return new Vector2(a.x-b.x,a.y-b.y); }
 public static Vector2 operator *(Vector2 a,float b) { return new Vector2(a.x*b,a.y*b); }
 public static Vector2 operator /(Vector2 a,float b) { return new Vector2(a.x/b,a.y/b); }
 public static Vector2 Lerp(Vector2 a,Vector2 b,float t) { return a+(b-a)*Mathf.Clamp01(t); }
}
struct Vector3 {
 public float x,y,z;
 public Vector3(float a,float b,float c) { x=a; y=b; z=c; }
 public static Vector3 up { get { return new Vector3(0,1,0); } }
 public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
}
struct Rect {
 public float xMin,yMin,width,height;
 public Rect(float x,float y,float w,float h) { xMin=x; yMin=y; width=w; height=h; }
 public float xMax { get { return xMin+width; } }
 public float yMax { get { return yMin+height; } }
 public static bool operator ==(Rect a,Rect b) { return a.xMin==b.xMin && a.yMin==b.yMin && a.width==b.width && a.height==b.height; }
 public static bool operator !=(Rect a,Rect b) { return !(a==b); }
 public override bool Equals(object a) { return a is Rect && this==(Rect)a; }
 public override int GetHashCode() { return xMin.GetHashCode(); }
}
static class Mathf {
 public const float PI=(float)Math.PI;
 public static float Cos(float a) { return (float)Math.Cos(a); }
 public static float Sin(float a) { return (float)Math.Sin(a); }
 public static int RoundToInt(float a) { return (int)Math.Round(a); }
 public static float Min(float a,float b) { return Math.Min(a,b); }
 public static int Min(int a,int b) { return Math.Min(a,b); }
 public static int Max(int a,int b) { return Math.Max(a,b); }
 public static float Clamp(float a,float lo,float hi) { return Math.Max(lo,Math.Min(hi,a)); }
 public static float Max(float a,float b) { return Math.Max(a,b); }
 public static float Abs(float a) { return Math.Abs(a); }
 public static float Clamp01(float a) { return Math.Max(0,Math.Min(1,a)); }
 public static float Lerp(float a,float b,float t) { return a+(b-a)*Clamp01(t); }
 public static float Sqrt(float a) { return (float)Math.Sqrt(a); }
 public static float Exp(float a) { return (float)Math.Exp(a); }
 public static float Pow(float a,float b) { return (float)Math.Pow(a,b); }
 public static int FloorToInt(float a) { return (int)Math.Floor(a); }
 public static float Floor(float a) { return (float)Math.Floor(a); }
 public static int CeilToInt(float a) { return (int)Math.Ceiling(a); }
}
static class FrameProf {
 public const int S_AirBoundaryH=0;
 public static void S(int slot) { }
 public static void E(int slot) { }
}
class Edge {
 static object _skirt=new object(); static Source[] _sources; static Rect _skirtRect;
 static Patch[] _patches; static float _mean;
 static Rect Play { get { return _skirtRect; } }
 static bool OverBudget() { return false; }

'''

HARNESS = r'''
 static List<Patch> Patches(Rect r,Source[] sources) {
 PATCH_CODE
 return patches;
 }
 static int failures;
 static void Check(bool ok,string message) {
  Console.WriteLine((ok?"PASS ":"FAIL ")+message); if(!ok) failures++;
 }
 static string F(double x) { return x.ToString("0.000000",CultureInfo.InvariantCulture); }
 static Source[] Read(string file) {
  using(BinaryReader br=new BinaryReader(File.OpenRead(file))) {
   Source[] src=new Source[br.ReadInt32()];
   for(int k=0;k<src.Length;k++) {
    Source s=new Source(); src[k]=s; s.N=br.ReadInt32();
    s.O=new Vector3(br.ReadSingle(),br.ReadSingle(),br.ReadSingle());
    s.Size=new Vector3(br.ReadSingle(),br.ReadSingle(),br.ReadSingle());
    br.ReadInt32(); s.H=new float[s.N,s.N]; // old splat pass count, unused since Y lite
    for(int z=0;z<s.N;z++) for(int x=0;x<s.N;x++) s.H[z,x]=br.ReadSingle();
   }
   return src;
  }
 }
 static int Sim(Rect r, Source[] sources, string label) {
  Stopwatch sw=Stopwatch.StartNew();
  float mean=0;
  for(int side=0;side<4;side++) for(int k=0;k<100;k++) {
   Vector2 p=Point(r,side,k/100f); mean+=Find(sources,p.x,p.y).Height(p.x,p.y)/400f;
  }
  List<Patch> patches=Patches(r,sources); List<Vector3> probes=new List<Vector3>();
  int verts=0,tris=0,wrongWinding=0,unused=0,cracks=0; int[] sideVerts=new int[4];
  double[] maxStep=new double[4]; double maxUV=0; double guideGap=0; int lookupMisses=0;
  for(int part=0;part<patches.Count;part++) {
   Patch p=patches[part]; List<Vector3> v=new List<Vector3>(); List<int> ti=new List<int>();
   List<float> prev=null; int prevStart=0;
   for(int row=0;row<Rings.Length;row++) {
    List<float> at=Row(p,row); int begin=v.Count;
    for(int c=0;c<at.Count;c++) {
     float t=at[c]; Vector2 pt=Vector2.Lerp(p.A,p.B,t);
     Vector2 n=Vector2.Lerp(p.NA,p.NB,t).normalized; pt+=n*(p.Corner?Mathf.Max(0f,Rings[row]):Rings[row]);
     float h=Skirt(r,sources,mean,pt.x,pt.y); if(row==0) h-=5;
     v.Add(new Vector3(pt.x,h,pt.y));
     if(row==1 && !p.Corner) {
      int side=Math.Abs(p.NA.y)>0.5?(p.NA.y<0?0:2):(p.NA.x>0?1:3);
      maxStep[side]=Math.Max(maxStep[side],Math.Abs(h-p.Src.Height(pt.x,pt.y)));
      Vector2 mirrored=Mirror(r,pt.x,pt.y); Vector2 uv=p.Src.UV(mirrored.x,mirrored.y);
      maxUV=Math.Max(maxUV,(uv-p.Src.UV(pt.x,pt.y)).magnitude);
      if(c>0) {
       Vector3 before=v[v.Count-2]; float mx=(before.x+pt.x)*0.5f,mz=(before.z+pt.y)*0.5f;
       maxStep[side]=Math.Max(maxStep[side],Math.Abs((before.y+h)*0.5f-p.Src.Height(mx,mz)));
      }
     }
    }
    if(prev!=null) Stitch(prev,at,prevStart,begin,ti);
    prev=at; prevStart=begin;
   }
   p.V=v.ToArray(); p.I=ti.ToArray();
   IEnumerator index=IndexPatch(r,p); while(index.MoveNext()) { }
   int[] used=new int[v.Count]; Dictionary<string,int> edges=new Dictionary<string,int>();
   for(int i=0;i<ti.Count;i+=3) {
    int ia=ti[i],ib=ti[i+1],ic=ti[i+2]; Vector3 a=v[ia],b=v[ib],c=v[ic];
    double area=(b.z-a.z)*(c.x-a.x)-(b.x-a.x)*(c.z-a.z);
    if(area < -0.01) wrongWinding++;
    used[ia]++;used[ib]++;used[ic]++;
    int[] ids={ia,ib,ic};
    for(int j=0;j<3;j++) {
     int e0=Math.Min(ids[j],ids[(j+1)%3]),e1=Math.Max(ids[j],ids[(j+1)%3]);
     string key=e0+"/"+e1; if(!edges.ContainsKey(key)) edges[key]=0; edges[key]++;
    }
    // Cached analytic pilot clearance must stay within the existing 30 u margin.
    float cx=(a.x+b.x+c.x)/3,cz=(a.z+b.z+c.z)/3;
    if(Depth(r,cx,cz)>0) {
     if(area>0.01) probes.Add(new Vector3(cx,(a.y+b.y+c.y)/3,cz));
     float actual; if(!p.Height(cx,cz,Depth(r,cx,cz),out actual)) lookupMisses++;
     else guideGap=Math.Max(guideGap,Math.Abs((a.y+b.y+c.y)/3-actual));
    }
   }
   foreach(int count in used) if(count==0) unused++;
   foreach(int count in edges.Values) if(count>2) cracks++;
   verts+=v.Count; tris+=ti.Count/3;
   for(int k=0;k<4;k++) if((p.NB-Normals[k]).sqrMagnitude<0.0001f) sideVerts[k]+=v.Count;
  }
  sw.Stop(); Console.WriteLine(label+": "+patches.Count+" lookup patches in 4 renderers x 1 pass, "+verts+" vertices, "+tris+
   " triangles, offline build "+F(sw.Elapsed.TotalMilliseconds)+" ms");
  Console.WriteLine("RENDERERS "+label.Replace(" ","_")+" 4 "+verts+" "+tris+" "+sideVerts[0]+" "+sideVerts[1]+" "+sideVerts[2]+" "+sideVerts[3]);
  Check(sideVerts[0]+sideVerts[1]+sideVerts[2]+sideVerts[3]==verts && Math.Max(Math.Max(sideVerts[0],sideVerts[1]),Math.Max(sideVerts[2],sideVerts[3]))<65000,
   label+" every patch in one side renderer, largest side "+Math.Max(Math.Max(sideVerts[0],sideVerts[1]),Math.Max(sideVerts[2],sideVerts[3]))+" vertices (16-bit index limit 65535)");
  for(int side=0;side<4;side++) Check(maxStep[side]<0.002,label+" edge "+side+" max height step "+F(maxStep[side])+" u");
  Check(maxUV<0.0000001,label+" all-edge colour UV step "+F(maxUV)+" (mirror is the identity on the seam row)");
  Check(wrongWinding==0 && unused==0 && cracks==0,label+" winding/unused/nonmanifold "+wrongWinding+"/"+unused+"/"+cracks);
  Check(tris<100000,label+" low-resolution geometry budget");
  Check(lookupMisses==0 && guideGap<0.01,label+" cached mesh height max error "+F(guideGap)+" u, missed triangles "+lookupMisses);
  // Adjacent patch sides/corner fans must meet at every ring, including decimation rows.
  int joins=0;
  for(int a=0;a<patches.Count;a++) for(int b=a+1;b<patches.Count;b++) {
   Patch pa=patches[a],pb=patches[b];
   for(int ea=0;ea<2;ea++) for(int eb=0;eb<2;eb++) {
    Vector2 p0=ea==0?pa.A:pa.B,p1=eb==0?pb.A:pb.B;
    Vector2 n0=ea==0?pa.NA:pa.NB,n1=eb==0?pb.NA:pb.NB;
    if((p0-p1).magnitude<0.01 && (n0-n1).magnitude<0.01) {
     for(int k=0;k<Rings.Length;k++) {
      Vector2 x0=p0+n0*(pa.Corner?Mathf.Max(0f,Rings[k]):Rings[k]),x1=p1+n1*(pb.Corner?Mathf.Max(0f,Rings[k]):Rings[k]);
      if(k==0 && (pa.Corner||pb.Corner)) continue; // covered overlap ends at the corner
      if((x0-x1).magnitude>0.01 || Math.Abs(Skirt(r,sources,mean,x0.x,x0.y)-Skirt(r,sources,mean,x1.x,x1.y))>0.002) cracks++;
     }
     joins++;
    }
   }
  }
  Check(cracks==0 && joins==patches.Count,label+" stitched side/corner joins "+joins+", gap count "+cracks);
  _sources=sources; _skirtRect=r; _patches=patches.ToArray(); _mean=mean;
  float hit; double globalError=0; int globalMiss=0;
  for(int i=0;i<probes.Count;i++) {
   Vector3 probe=probes[i]; if(!Height(probe.x,probe.z,out hit)) globalMiss++;
   else globalError=Math.Max(globalError,Math.Abs(hit-probe.y));
  }
  Check(globalMiss==0 && globalError<0.01,label+" complete pilot query max error "+F(globalError)+" u, misses "+globalMiss);
  double dummy=0; for(int i=0;i<1000;i++) { Height(r.xMax+500,r.yMin+600,out hit); dummy+=hit; }
  int collections=GC.CollectionCount(0); long memory=GC.GetTotalMemory(false);
  sw.Restart();
  for(int i=0;i<100000;i++) { Height(r.xMax+200+i%1500,r.yMin+i%5000,out hit); dummy+=hit; }
  sw.Stop();
  // Read the GC state before the log line below builds its strings.
  bool clean=GC.CollectionCount(0)==collections && GC.GetTotalMemory(false)<=memory;
  Console.WriteLine(label+" cached height query avg "+F(sw.Elapsed.TotalMilliseconds/100000)+" ms; accumulator "+F(dummy));
  Check(clean,label+" 100000 cached height queries: zero managed allocation / gen0 collections");
  failures+=FarForest.CheckStrip(r,label);
  return failures;
 }
 static int Main(string[] args) {
  CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
  Source[] sources=Read(args[0]);
  FarForest.Input=args[1];
  Sim(new Rect(-2500,-2500,5000,5000),new Source[]{sources[0]},"vanilla");
  Sim(new Rect(-2500,-2500,10000,5000),sources,"east world");
  // Asymmetric small terrain catches x/z transpose independently of real data.
  Source asymmetric=new Source(); asymmetric.N=3; asymmetric.Size=new Vector3(2,100,2);
  asymmetric.H=new float[,]{{0,0.1f,0.2f},{0.3f,0.4f,0.5f},{0.6f,0.7f,0.8f}};
  Check(Math.Abs(asymmetric.Height(1.5f,0.5f)-30)<0.00001,"Unity API [z,x] cache preserves serialized x-major orientation");
  Console.WriteLine(failures+" FAIL"); return failures==0?0:1;
 }
}
'''


# Unity rendering is deliberately a no-op. The iterator, placement, height
# lookups, card vertices, indices and idle path below are production C#.
FOREST_SHIM = r'''
class GameObject {
 public static List<GameObject> All=new List<GameObject>();
 public Transform transform=new Transform();
 public Mesh Mesh;
 public GameObject(string n) { All.Add(this); }
 public void SetActive(bool a) { }
 public T AddComponent<T>() where T:new() {
  T c=new T();if(c is MeshFilter) ((MeshFilter)(object)c).Owner=this;return c;
 }
}
class Transform {
 public Vector3 position;
 public void SetParent(Transform p,bool b) { }
}
internal class MonoBehaviour { }
class MeshFilter {
 public GameObject Owner;
 public Mesh sharedMesh { set { Owner.Mesh=value; } }
}
class MeshRenderer { public Material sharedMaterial; }
internal class Mesh {
 public string name;
 public float X0,X1,Z0,Z1;public int Tris;
 public void SetVertices(List<Vector3> v) {
  if(v.Count>=65536) throw new Exception("16-bit vertices");
  X0=Z0=float.MaxValue;X1=Z1=float.MinValue;
  foreach(Vector3 p in v) {X0=Math.Min(X0,p.x);X1=Math.Max(X1,p.x);Z0=Math.Min(Z0,p.z);Z1=Math.Max(Z1,p.z);}
 }
 public void SetNormals(List<Vector3> v) { }
 public void SetUVs(int ch,List<Vector2> v) { }
 public void SetTriangles(List<int> t,int ch) {
  Tris=t.Count/3;
  for(int i=0;i<t.Count;i++) if(t[i]<0 || t[i]>=65536) throw new Exception("16-bit indices");
 }
 public void RecalculateBounds() { }
 public void UploadMeshData(bool b) { }
}
static class Time {
 public static float unscaledTime=1f;
 public static float realtimeSinceStartup { get { return (float)Stopwatch.GetTimestamp()/Stopwatch.Frequency; } }
}
static class ViewDistance { public static void KeepVisible(GameObject o) { } }
static class RevivalPlugin {
 public static Log L=new Log();
 public class Log {
  public void LogInfo(string s) { Console.WriteLine(s); }
  public void LogWarning(string s) { throw new Exception(s); }
 }
}
static class AirBoundary {
 public static GameObject SkirtRoot=new GameObject("skirt");
 public static int Queries,Misses;public static int[] Sectors=new int[8];
 public static Rect SkirtRect { get { return _skirtRect; } }
 public static float BufferU=2000;
 public static bool SkirtHeight(float x,float z,out float y) {
  Queries++;bool hit=Edge.SkirtHeight(x,z,out y);if(!hit) Misses++;
  Rect r=_skirtRect;int sx=x<r.xMin?-1:x>r.xMax?1:0,sz=z<r.yMin?-1:z>r.yMax?1:0;
  int sector=sx==0?(sz<0?2:3):sz==0?(sx<0?0:1):4+(sx>0?2:0)+(sz>0?1:0);
  Sectors[sector]++;return hit;
 }
}
'''

FOREST_GLUE = r'''
 const float K=2.8f;
 ATLAS_CONSTANTS
 const int MaxSlots=AtlasCols*AtlasRows;
 sealed class Slot { public int Index; public float X1,Y0,Y1; }
 struct Card { public Vector3 P; public float Ws,Hs,Yaw; public Slot S; }
 sealed class Ground { public object Canopy; public int Layer; }
 static Dictionary<TerrainData,Ground> _grounds=new Dictionary<TerrainData,Ground>();
 static Stopwatch _sw=new Stopwatch();
 static GameObject _root;
 static Material _cardMat=new Material();
 static bool Wanted { get { return true; } }
 static void Quiet(MeshRenderer r) { }
 internal static string Input;
 internal static int CheckStrip(Rect r,string label) {
  GameObject.All.Clear();
  int fail=0; EdgeMeasureStart();
  using(BinaryReader br=new BinaryReader(File.OpenRead(Input))) {
   _edgeArea=br.ReadDouble(); int far=br.ReadInt32();
   _edgeDensity=(float)(far/_edgeArea);
   int pool=br.ReadInt32();
   for(int i=0;i<pool;i++) {
    Slot s=new Slot(); s.Index=i%MaxSlots; s.X1=br.ReadSingle(); s.Y0=br.ReadSingle(); s.Y1=br.ReadSingle();
    EdgeSample(s,br.ReadSingle(),br.ReadSingle());
   }
  }
  _root=new GameObject("forest"); _edgeParent=_root; _edgeSkirt=AirBoundary.SkirtRoot;
  _edgeBuffer=AirBoundary.BufferU;
  AirBoundary.Queries=AirBoundary.Misses=0;Array.Clear(AirBoundary.Sectors,0,8);
  _edgeBuild=BuildEdge(r,_edgeBuffer);
  int frames=0; double max=0,sum=0; Stopwatch total=Stopwatch.StartNew();
  while(true) {
   _sw.Reset(); _sw.Start(); bool more=_edgeBuild.MoveNext(); _sw.Stop();
   double ms=_sw.Elapsed.TotalMilliseconds; max=Math.Max(max,ms);sum+=ms;frames++;
   if(!more) break;
  }
  _edgeBuild=null; total.Stop();
  double expected=EdgeExpected(r,_edgeBuffer,_edgeDensity);
  int limit=(int)Math.Min(expected,EdgeMaxCards);
  Console.WriteLine("FOREST "+label+": "+_edgeCards+" trees, "+_edgeTris+" triangles, "+_edgeMeshes+
   " maximum draws (all chunks), shared atlas/material, 0 colliders/shadows/LODGroups; mesh bytes ~"+(_edgeCards*304L));
  Console.WriteLine("FOREST "+label+" density "+(_edgeDensity*1000).ToString("0.000")+
   "/1000 u2, expected/cap "+expected.ToString("0")+"/"+EdgeMaxCards+", managed build "+sum.ToString("0.00")+
   " ms over "+frames+" slices (max "+max.ToString("0.000")+" ms; mock native calls)");
  if(_edgeCards<limit*0.94 || _edgeCards>EdgeMaxCards || _edgeTris!=8*_edgeCards || _edgeMeshes>200) fail++;
  if(AirBoundary.Queries!=_edgeCards || AirBoundary.Misses!=0) fail++;
  for(int i=0;i<8;i++) if(AirBoundary.Sectors[i]<100) fail++;
  Console.WriteLine("COVERAGE "+label+": all 4 sides + 4 corners planted; "+AirBoundary.Queries+" exact skirt queries, "+AirBoundary.Misses+" misses");
  for(int scenario=0;scenario<2;scenario++) {
   float cx=r.xMax-400,cz=scenario==0?r.yMin+r.height/2:r.yMax-400;
   float dx=scenario==0?1:0.7071068f,dz=scenario==0?0:0.7071068f;
   int draws=0,triangles=0;
   foreach(GameObject go in GameObject.All) {
    Mesh m=go.Mesh;if(m==null) continue;
    float vx=(m.X0+m.X1)/2+go.transform.position.x-cx,vz=(m.Z0+m.Z1)/2+go.transform.position.z-cz;
    double radius=Math.Sqrt((m.X1-m.X0)*(m.X1-m.X0)+(m.Z1-m.Z0)*(m.Z1-m.Z0))/2;
    double depth=vx*dx+vz*dz,lateral=Math.Abs(vx*dz-vz*dx);
    // Conservative horizontal 103-degree cone; ignores vertical culling.
    if(depth+radius<0 || Math.Sqrt(vx*vx+vz*vz)-radius>4480 || lateral-radius>(depth+radius)*1.25) continue;
    draws++;triangles+=m.Tris;
   }
   Console.WriteLine("DRAW ESTIMATE "+label+" "+(scenario==0?"side":"corner")+": "+draws+" draws, "+triangles+
    " triangles; GPU geometry "+(triangles/1e6).ToString("0.00")+"-"+(triangles/2e5).ToString("0.00")+
    " ms at assumed 0.2-1.0 Gtri/s, plus cutout shading (~0.2-1.0 ms budget estimate; hardware dependent)");
  }
  if(EdgeShare(0,_edgeBuffer)!=0 || EdgeShare(100,_edgeBuffer)!=1 ||
   Math.Abs(EdgeShare(_edgeBuffer,_edgeBuffer)-EdgeOuterShare)>0.00001 || EdgeShare(_edgeBuffer+EdgeTaperU,_edgeBuffer)!=0) fail++;
  if(EdgeCanopyU(0)!=0 || EdgeCanopyU(100)<=0) fail++;
  int badFeet=0,badUV=0; List<Vector3> vs=new List<Vector3>();
  List<Vector3> ns=new List<Vector3>();List<Vector2> uv=new List<Vector2>();List<int> ts=new List<int>();
  // Independently exercise shared card geometry with every sampled real size.
  for(int i=0;i<_edgePool.Count;i++) {
   Card c=_edgePool[i]; float y; float x=r.xMax+200+i%1700,z=r.yMin+i%4000;
   if(!AirBoundary.SkirtHeight(x,z,out y)) { badFeet++; continue; }
   c.P=new Vector3(x,y-EdgeSinkU,z);c.Yaw=i*0.37f;
   vs.Clear();ns.Clear();uv.Clear();ts.Clear();AddCard(c,new Vector3(),vs,ns,uv,ts);
   if(vs.Count!=8 || ns.Count!=8 || uv.Count!=8 || ts.Count!=24 || Math.Abs(vs[0].y-(y-EdgeSinkU+c.S.Y0*c.Hs))>0.001) badFeet++;
   for(int j=0;j<uv.Count;j++) if(uv[j].x<0 || uv[j].x>1 || uv[j].y<0 || uv[j].y>1) badUV++;
  }
  if(badFeet!=0 || badUV!=0) fail++;
  // Warm JIT then measure production idle tick and canopy clearance together.
  _edgeNext=0;for(int i=0;i<10000;i++) { Time.unscaledTime=i/60f;EdgeStep();EdgeCanopyU(100); }
  Stopwatch idle=new Stopwatch();double peak=0;
  GC.Collect(); int gc=GC.CollectionCount(0);long mem=GC.GetTotalMemory(false);idle.Start();
  for(int batch=0;batch<1000;batch++) {
   long start=Stopwatch.GetTimestamp();
   for(int i=0;i<1000;i++) {Time.unscaledTime=(10000+batch*1000+i)/60f;EdgeStep();EdgeCanopyU(100);}
   peak=Math.Max(peak,(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency/1000);
  }
  idle.Stop(); bool clean=gc==GC.CollectionCount(0) && GC.GetTotalMemory(false)<=mem;
  Console.WriteLine("FOREST "+label+" idle + canopy: "+(idle.Elapsed.TotalMilliseconds/1e6).ToString("0.000000")+
   " ms/call, max 1000-call mean "+peak.ToString("0.000000")+" ms, zero allocation/gen0 "+clean);
  if(!clean || idle.Elapsed.TotalMilliseconds/1e6>=0.1 || peak>=0.5) fail++;
  GameObject pending=new GameObject("pending");_edgePending=pending;
  _edgeParent=new GameObject("changed parent");if(!EdgeStale(pending)) fail++;
  _edgeParent=_root;AirBoundary.BufferU=2200;if(!EdgeStale(pending)) fail++;AirBoundary.BufferU=2000;
  _edgeSkirt=new GameObject("old skirt");if(!EdgeStale(pending)) fail++;
  EdgeDrop();if(_edgeBuild!=null || _edgePending!=null || _edgeRoot!=null || _edgeTopU!=0) fail++;
  _edgeParent=_root;_edgeSkirt=AirBoundary.SkirtRoot;
  _edgeBuild=BuildEdge(r,2000);_sw.Reset();_sw.Start();_edgeBuild.MoveNext();
  _edgeSkirt=new GameObject("replaced during build");
  if(_edgeBuild.MoveNext() || _edgePending!=null || _edgeBuild!=null) fail++;
  Console.WriteLine((fail==0?"PASS ":"FAIL ")+label+" forest: density, strip taper, 16-bit meshes, planted cards/UV, idle budget and stale-build cleanup");
  return fail;
 }
'''


def forest_input(v, out):
    """Reconstruct real NpcDistance masks, including mesh height/name filters.

    Alternative native billboard terrains overlap: OR their masks, as ForestAt
    does. Density is calibrated to the observed field far-card count, not a
    promise that every future tree edit produces that count. Runtime measures
    its actual built cards and union forest area instead.
    """
    import mono
    cache = {}

    def resolve(obj, ref):
        if not ref["m_PathID"]:
            return None
        file = obj.assets_file
        if ref["m_FileID"]:
            name = Path(file.externals[ref["m_FileID"] - 1].path).name
            env = mono.env(name)
        else:
            env = file
        key = id(env)
        if key not in cache:
            objects = env.objects
            cache[key] = objects if isinstance(objects, dict) else {o.path_id: o for o in objects}
        return cache[key][ref["m_PathID"]]

    def bounds(go, parent_scale=1.0):
        data = go.read_typetree()
        components = [resolve(go, c["component"]) for c in data["m_Component"]]
        tr = next(c for c in components if c.type.name == "Transform")
        td = tr.read_typetree()
        scale = parent_scale * td["m_LocalScale"]["y"]
        top, width = 0.0, 0.0
        for c in components:
            if c.type.name == "MeshFilter":
                mesh = resolve(c, c.read_typetree()["m_Mesh"])
                if mesh:
                    b = mesh.read_typetree()["m_LocalAABB"]
                    top = max(top, (b["m_Center"]["y"] + b["m_Extent"]["y"]) * scale)
                    width = max(width, max(b["m_Extent"]["x"], b["m_Extent"]["z"]) * abs(scale))
        for ref in td["m_Children"]:
            child = resolve(tr, ref)
            cg = resolve(child, child.read_typetree()["m_GameObject"])
            t, w = bounds(cg, scale)
            top, width = max(top, t), max(width, w)
        return top, width

    masks, trees, measured = [], [], {}
    cell = 11.2
    size = int(np.ceil(v.size / cell))

    def square_sum(a, radius):
        a = np.pad(a, radius)
        integral = np.pad(a.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
        k = radius * 2 + 1
        return integral[k:, k:] - integral[:-k, k:] - integral[k:, :-k] + integral[:-k, :-k]

    for obj in v._env.objects:
        if obj.type.name != "TerrainData":
            continue
        d = obj.read_typetree()
        db = d["m_DetailDatabase"]
        inst = db["m_TreeInstances"]
        if not inst:
            continue
        proto = []
        for p in db["m_TreePrototypes"]:
            go = resolve(obj, p["prefab"])
            key = (go.assets_file.name, go.path_id) if go else None
            if key not in measured:
                name = go.read_typetree()["m_Name"].lower() if go else ""
                h, w = bounds(go) if go else (0, 0)
                excluded = any(s in name for s in ("rock", "stone", "grass", "kamen", "bush", "kust"))
                measured[key] = (-1 if excluded else h, w)
            proto.append(measured[key])
        count = np.zeros((size, size), dtype=np.int64)
        eligible = []
        for t in inst:
            h, w = proto[t["index"]]
            if h < 0 or (h > 0 and h * t["heightScale"] < 11.2):
                continue
            x, z = t["position"]["x"] * v.size, t["position"]["z"] * v.size
            ix, iz = int(x / cell), int(z / cell)
            if not (0 <= ix < size and 0 <= iz < size):
                continue
            count[iz, ix] = min(255, count[iz, ix] + 1)
            eligible.append((ix, iz, w, h, t["widthScale"], t["heightScale"]))
        mask = (square_sum(count, 1) >= 2) & (square_sum(count, 3) >= 10)
        masks.append(mask)
        trees.extend(t for t in eligible if mask[t[1], t[0]])
        print("FOREST DATA %s: %d instances, %d tall forest trees, %d forest cells" %
              (d["m_Name"], len(inst), sum(bool(mask[t[1], t[0]]) for t in eligible), mask.sum()), flush=True)
    union = np.logical_or.reduce(masks)
    step = np.float32(22.4)
    # Match C# repeated float additions rather than sampling a different grid.
    positions, x = [], np.float32(step * 0.5)
    while x < size * cell:
        positions.append(int(x / cell))
        x = np.float32(x + step)
    area = float(union[np.ix_(positions, positions)].sum()) * float(step) ** 2
    far_cards = next((int(a.split("=", 1)[1]) for a in sys.argv if a.startswith("--far-cards=")), 74601)
    assert area > 0 and trees and far_cards > 0
    file = out / "forest.bin"
    with file.open("wb") as f:
        f.write(np.array([area], dtype="<f8").tobytes())
        f.write(np.array([far_cards, len(trees)], dtype="<i4").tobytes())
        for ix, iz, w, h, ws, hs in trees:
            f.write(np.array([w, 0, h, ws, hs], dtype="<f4").tobytes())
    print("FOREST INPUT: union %.3f M u2 (real prototype bounds/name filters); field Normal seed %d far cards; runtime recalibrates per world" %
          (area / 1e6, far_cards), flush=True)
    return file


def main():
    code = (ROOT / "Revival.AirBoundary.cs").read_text(encoding="utf-8")
    pieces = [block(code, "sealed class Source"), block(code, "sealed class Patch")]
    for name in ("Depth", "Find", "Point", "Inset", "Mirror", "Skirt", "Smooth", "Hash", "Value", "Fbm", "Row", "Stitch", "Band", "EdgeDistance", "IndexPatch"):
        match = re.search(r"        static [^\n]+\b" + name + r"\(", code)
        assert match, name
        pieces.append(block(code, match.group(0)))
    pieces.append(block(code, "internal static bool Height("))
    pieces.append(block(code, "internal static bool SkirtHeight("))
    rings = re.search(r"static readonly float\[\] Rings = \{.*?\};", code, re.S).group()
    normals = re.search(r"static readonly Vector2\[\] Normals = \{.*?\};", code, re.S).group()
    constants = "const float HillU=90f; const int FanSteps=6; const float Tuck=25f;\n" + rings + normals
    patch_code = code[code.index("            List<Patch> patches ="):code.index("            int vertices =")]
    # Source checks protect the Y lite render path (one material, one pass, no
    # terrain splat shaders or shadows) and the allocation-free steady state.
    expected = [
        'Shader.Find("Mobile/Diffuse")', 'Shader.Find("Legacy Shaders/VertexLit")',
        'mat.renderQueue = SkirtQueue', 'const int SkirtQueue = 2490;',
        'mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;',
        'LightProbeUsage.Off', 'ReflectionProbeUsage.Off', 'mr.sharedMaterial = mat;',
        'src.D.splatPrototypes', 'src.D.alphamapTextures', 'tex.Apply(true, true)',
        'mesh.triangles = tris.ToArray();', 'FrameProf.S(FrameProf.S_AirBoundaryT)',
        'FrameProf.E(FrameProf.S_AirBoundaryT)', 'if (!FarForest.GroundReady) return;',
        'if (_skirt == null) { _sources = null; _patches = null; }', 'mat.enableInstancing = false',
        'for (int side = 0; side < 4; side++)', '"SkirtLite"',
    ]
    for token in ("Nature/Terrain", "Splatmap", "SetTriangles(", "sharedMaterials", "basemapDistance"):
        assert token not in code, token
    for token in expected:
        assert token in code, token
    assert "AddComponent<TerrainCollider>" not in code and "AddComponent<MeshCollider>" not in code
    height = block(code, "internal static bool Height(") + block(code, "internal static bool SkirtHeight(")
    assert not any(token in height for token in ("new float", "new int", "new List", "SampleHeight", "GetHeights", "Physics.", "FindObjects"))
    print("PASS lite skirt: one single-pass material, baked splat colour, queue 2490, no shadows/probes/colliders/splat shaders, cached query, F6 registration", flush=True)

    out = ROOT / "build" / "edge-terrain-check"
    out.mkdir(parents=True, exist_ok=True)
    v = terrainmap.terrain("GW_Scene_1")
    e = terrainmap.use_east_tile()
    forest = forest_input(v, out)
    with (out / "heights.bin").open("wb") as f:
        f.write(np.array([2], dtype="<i4").tobytes())
        for t in (v, e):
            f.write(np.array([t.samples], dtype="<i4").tobytes())
            sx = getattr(t, "size_x", v.size)
            sz = getattr(t, "size_z", v.size)
            origin = getattr(t, "origin", (t.corner[0], 0, t.corner[1]))
            f.write(np.array(list(origin) + [sx, t.height_scale, sz], dtype="<f4").tobytes())
            layers = len(t._data["m_SplatDatabase"]["m_Splats"]) + 1  # default P3 canopy
            f.write(np.array([(layers + 3) // 4], dtype="<i4").tobytes())
            f.write((t.heights / terrainmap.HEIGHT_DIV).astype("<f4").tobytes())
    edge_code = (ROOT / "Revival.EdgeForest.cs").read_text(encoding="ascii")
    far_code = (ROOT / "Revival.FarForest.cs").read_text(encoding="utf-8")
    forest_class = block(edge_code, "internal static partial class FarForest")
    # Mask union is reconstructed from serialized data above; the simulator
    # has no live Terrain/NpcDistance registry to call this measurement seam.
    forest_class = forest_class.replace(block(edge_code, "static IEnumerator EdgeMeasureArea("), "")
    add_card = block(far_code, "static void AddCard(Card c, Vector3 origin, List<Vector3>")
    brace = forest_class.index("{") + 1
    atlas = re.search(r"const int SlotW = [^;]+;", far_code).group()
    forest_class = forest_class[:brace] + FOREST_GLUE.replace("ATLAS_CONSTANTS", atlas) + add_card + forest_class[brace:]
    for token in ("AddComponent<TerrainCollider>", "AddComponent<MeshCollider>", "Physics.", "FindObjects", "new LODGroup"):
        assert token not in edge_code, token
    assert "EdgeStep();" in block(far_code, "static void Step()")
    assert "EdgeDrop();" in block(far_code, "static void ClearCards()")
    assert "mr.sharedMaterial = _cardMat;" in edge_code and "Quiet(mr);" in edge_code
    assert "FrameProf.S(FrameProf.S_FarForestT)" in (ROOT / "RevivalPlugin.cs").read_text(encoding="utf-8")
    assert 'cfg.Bind("FarForest", "Quality", "Normal"' in far_code
    idle = block(edge_code, "static void EdgeStep()")
    assert "_edgeNext = now + 2f;" in idle and "Find" not in idle and "Physics." not in idle
    generated = SHIM + constants + "\n".join(pieces) + FOREST_SHIM + forest_class
    generated += block(edge_code, "internal sealed class EdgeForestAssets")
    generated += HARNESS.replace("PATCH_CODE", patch_code)
    generated += "\nnamespace UnityEngine { static class Object { public static void Destroy(object o) { } } }\n"
    # C# 3.0: avoid newer Stopwatch.Restart and readonly CurrentCulture setter.
    generated = generated.replace("sw.Restart();", "sw.Reset(); sw.Start();")
    generated = generated.replace("CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;", "System.Threading.Thread.CurrentThread.CurrentCulture=CultureInfo.InvariantCulture;")
    cs = out / "EdgeCheck.cs"
    cs.write_text(generated, encoding="ascii")
    csc = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework/v3.5/csc.exe"
    exe = out / "EdgeCheck.exe"
    subprocess.run([str(csc), "/nologo", "/warn:0", "/out:" + str(exe), str(cs)], check=True, cwd=ROOT)
    proc = subprocess.run([str(exe), str(out / "heights.bin"), str(forest)], cwd=ROOT)
    if proc.returncode == 0:
        print("COST MODEL: all-chunk draws are a conservative ceiling; DRAW ESTIMATE uses actual generated mesh bounds "
              "in a horizontal 103-degree cone to 4480 u (vertical culling ignored). GPU throughput and cutout shading "
              "are assumptions, not GPU timings; Unity render submission needs game measurement. "
              "Expected steady F6 FarForest.Tick increment <0.01 ms, 0 B/frame; managed build slice 0.08 ms. "
              "Native upload/startup peaks are not represented by mocks.", flush=True)
    return proc.returncode


if __name__ == "__main__":
    raise SystemExit(main())

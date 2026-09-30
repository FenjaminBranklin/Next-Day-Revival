"""Compile the real C# seat binder against deterministic hierarchy doubles.

No game/Unity process, Steam writes, network or external temporary directory.
The doubles implement rotation, translation and nonuniform donor scale; they
test binding mechanics, not renderer clearance or Unity's frame scheduling.
"""
from pathlib import Path
import os
import subprocess

ROOT = Path(__file__).resolve().parents[1]
STUBS = r'''
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace HarmonyLib {
 public class Harmony { public void Patch(MethodInfo m, object a, object b, object c, object d, object e) { if(m==null)throw new Exception("missing native hook"); } }
 public class HarmonyMethod { public HarmonyMethod(MethodInfo m) {} }
 public static class AccessTools {
  public static FieldInfo Field(Type t,string n) { return t.GetField(n); }
  public static MethodInfo Method(Type t,string n,object a,object b) { return t.GetMethod(n); }
 }
}
namespace UnityEngine {
 public class Object {}
 public class Component : Object {
  public GameObject gameObject;
  public Transform transform { get {return gameObject.transform;} }
  public T GetComponent<T>() where T:Component {return gameObject.GetComponent<T>();}
 }
 public class MonoBehaviour : Component {}
 public class GameObject : Object {
  public Transform transform; public bool activeInHierarchy=true;
  Dictionary<Type,Component> components=new Dictionary<Type,Component>();
  public GameObject(string n) {transform=new Transform(this);}
  public T GetComponent<T>() where T:Component {Component c;return components.TryGetValue(typeof(T),out c)?(T)c:null;}
  public T AddComponent<T>() where T:Component,new() {T c=new T();c.gameObject=this;components[typeof(T)]=c;return c;}
 }
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get{return new Vector3();}}
  public static Vector3 up {get{return new Vector3(0,1,0);}}
  public static Vector3 forward {get{return new Vector3(0,0,1);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}}
  public Vector3 normalized {get{return this/(float)Math.Sqrt(sqrMagnitude);}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
  public static Vector3 operator /(Vector3 a,float b){return a*(1/b);}
  public static Vector3 Scale(Vector3 a,Vector3 b){return new Vector3(a.x*b.x,a.y*b.y,a.z*b.z);}
  public static Vector3 Divide(Vector3 a,Vector3 b){return new Vector3(a.x/b.x,a.y/b.y,a.z/b.z);}
  public static Vector3 Cross(Vector3 a,Vector3 b){return new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
 }
 public struct Quaternion {
  public float x,y,z,w; public Quaternion(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}
  public static Quaternion identity {get{return new Quaternion(0,0,0,1);}}
  public static Quaternion operator *(Quaternion a,Quaternion b){return new Quaternion(a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w,a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z);}
  public static Vector3 operator *(Quaternion q,Vector3 v){Vector3 a=new Vector3(q.x,q.y,q.z);Vector3 t=Vector3.Cross(a,v)*2;return v+t*q.w+Vector3.Cross(a,t);}
  public static Quaternion Inverse(Quaternion q){return new Quaternion(-q.x,-q.y,-q.z,q.w);}
  static Quaternion Axis(float angle,Vector3 a){double h=angle*Math.PI/360;float s=(float)Math.Sin(h);return new Quaternion(a.x*s,a.y*s,a.z*s,(float)Math.Cos(h));}
  public static Quaternion Euler(float x,float y,float z){return Axis(y,Vector3.up)*Axis(x,new Vector3(1,0,0))*Axis(z,Vector3.forward);}
  public static Quaternion LookRotation(Vector3 f,Vector3 up){return Euler(0,(float)(Math.Atan2(f.x,f.z)*180/Math.PI),0);}
 }
 public class Transform : Component {
  public Transform parent; public Vector3 localPosition;public Quaternion localRotation=Quaternion.identity;public Vector3 localScale=new Vector3(1,1,1);
  List<Transform> kids=new List<Transform>();public Transform(GameObject g){gameObject=g;}
  public int childCount {get{return kids.Count;}}public Transform GetChild(int i){return kids[i];}
  public Vector3 lossyScale {get{return parent==null?localScale:Vector3.Scale(parent.lossyScale,localScale);}}
  public Vector3 position {get{return parent==null?localPosition:parent.TransformPoint(localPosition);}set{localPosition=parent==null?value:parent.InverseTransformPoint(value);}}
  public Quaternion rotation {get{return parent==null?localRotation:parent.rotation*localRotation;}set{localRotation=parent==null?value:Quaternion.Inverse(parent.rotation)*value;}}
  public Vector3 forward {get{return rotation*Vector3.forward;}}
  public Vector3 TransformPoint(Vector3 p){return position+rotation*Vector3.Scale(p,lossyScale);}
  public Vector3 InverseTransformPoint(Vector3 p){return Vector3.Divide(Quaternion.Inverse(rotation)*(p-position),lossyScale);}
  public void SetParent(Transform p,bool world){Vector3 pos=position,scale=lossyScale;Quaternion rot=rotation;if(parent!=null)parent.kids.Remove(this);parent=p;if(p!=null)p.kids.Add(this);if(world){position=pos;rotation=rot;localScale=p==null?scale:Vector3.Divide(scale,p.lossyScale);}}
  public bool IsChildOf(Transform p){for(Transform t=this;t!=null;t=t.parent)if(t==p)return true;return false;}
 }
 public static class Mathf {public static int Clamp(int v,int a,int b){return Math.Min(b,Math.Max(a,v));}public static int RoundToInt(float f){return (int)Math.Round(f);}}
 public static class Time {public static float time;}
}
namespace NextDayRevival {
 public class VehicleGameSystem : Component {public Transform SeatPoints; public GameObject[] Passengers;public void SitToPassengerPlace(){}public void ChangeToPassengerPlace(){}}
 public static class RevivalPlugin {public static Type TypeByName(string n){return typeof(VehicleGameSystem);}}
 public static class PlayerAn2 {public const float K=2.8f;}
 public static class PlayerHeli {public const float K=2.8f;public static Vector3 CabinSeatLocal(int i){return new Vector3(0.9f+(i<0?0:(i%2==0?-0.8f:0.8f)),1.2f,i<0?5:2-i/2*1.4f);}}
 public static class An2Model {public static int CabinSeats=6;public static Vector3 Seat(int i){return new Vector3(i<0?-0.55f:(i%2==0?-0.6f:0.6f),0.7f,i<0?2.5f:-i/2f);}}
 public static class MapTools {public static GameObject Viewer;public static GameObject LocalPlayer(){return Viewer;}}
 public static class Crocodile {public static int LocalActor(){return 1;}public static Dictionary<int,GameObject> Players=new Dictionary<int,GameObject>();public static GameObject PlayerByActor(int i){GameObject p;return Players.TryGetValue(i,out p)?p:null;}public static bool PlayerUp(GameObject p){return p.activeInHierarchy;}}
}
'''

HARNESS = r'''
namespace NextDayRevival {
 class Check {
  static int checks;
  static void Assert(bool b,string s){checks++;if(!b)throw new Exception(s);}
  static bool Near(Vector3 a,Vector3 b){return (a-b).sqrMagnitude<0.00002f;}
  static void Same(Transform body,Transform seat){Assert(body.parent==seat,"direct seat parent");Assert(Near(body.position,seat.position),"seat position");Assert(Near(body.forward,seat.forward),"seat forward");Assert(Near(body.rotation*Vector3.up,seat.rotation*Vector3.up),"seat up axis incl bank");}
  static Transform New(string n){return new GameObject(n).transform;}
  static void Main(){
   SeatBinding.Install(new HarmonyLib.Harmony());
   MapTools.Viewer=new GameObject("local");
   string[] kinds={"An-2","Mi-8","car","technical","MTW","Ural","tank"};
   string[] roles={"crew","merc","player","paratrooper"};
   foreach(string kind in kinds){
    Transform vehicle=New(kind),seat=New("seat"),owner=New("scene parent");
    vehicle.localScale=new Vector3(1.8f,2.8f,2.2f);seat.SetParent(vehicle,false);seat.localPosition=new Vector3(0.4f,1.1f,-0.7f);seat.localRotation=Quaternion.Euler(0,90,0);
    foreach(string role in roles){
     Transform body=New(role);body.SetParent(owner,false);body.localScale=new Vector3(1.2f,1.2f,1.2f);
     SeatBinding.BindSeat(body,seat,vehicle);Same(body,seat);Assert(Near(body.lossyScale,new Vector3(1.2f,1.2f,1.2f)),"world body scale preserved on bind");
     for(int i=0;i<180;i++){
      vehicle.position=new Vector3(i*.17f,70+i*.1f,-i*.2f);vehicle.rotation=Quaternion.Euler(i*.2f,i*2,i*1.5f);
      Same(body,seat); // No frame/tick: hierarchy alone must carry the body.
      body.localPosition=new Vector3(99,99,99);body.localRotation=Quaternion.identity;
      SeatBinding.LateFrame();Same(body,seat);
     }
     Transform next=New("next seat");next.SetParent(vehicle,false);next.localPosition=new Vector3(-1,1,2);
     SeatBinding.BindSeat(body,next,vehicle);Same(body,next);
     Vector3 at=body.position;SeatBinding.Detach(body);Assert(body.parent==owner,"scene parent restored");Assert(Near(at,body.position),"exit keeps world position");Assert(Near(body.localScale,new Vector3(1.2f,1.2f,1.2f)),"exit scale restored");
     vehicle.position=new Vector3(999,999,999);SeatBinding.LateFrame();Assert(Near(at,body.position),"no follow after exit/jump");
    }
    Console.WriteLine("PASS "+kind+": 4 roles, 180 combined yaw/pitch/roll steps, zero follow lag, seat switch and exit");
   }
   Transform hull=New("native car");VehicleGameSystem v=hull.gameObject.AddComponent<VehicleGameSystem>();v.SeatPoints=New("SeatPoints");v.SeatPoints.SetParent(hull,false);
   for(int i=0;i<3;i++){Transform s=New("native seat");s.SetParent(v.SeatPoints,false);s.localPosition=new Vector3(i,1,0);}
   GameObject player=new GameObject("native player");hull.localScale=new Vector3(2.8f,2.8f,2.8f);Transform passengersRoot=New("passengers root");passengersRoot.SetParent(hull,false);player.transform.SetParent(passengersRoot,true);v.Passengers=new GameObject[3];v.Passengers[0]=player;
   SeatBinding.NativeSeated(v);Same(player.transform,v.SeatPoints.GetChild(0));v.Passengers[0]=null;v.Passengers[2]=player;SeatBinding.NativeSeated(v);Same(player.transform,v.SeatPoints.GetChild(2));
   v.Passengers[2]=null;SeatBinding.LateFrame();Assert(player.transform.parent==null,"native exit array releases binding");Assert(Near(player.transform.lossyScale,new Vector3(1,1,1)),"native exit preserves body size from scaled passengers root");
   Transform plane=New("scaled aircraft");plane.localScale=new Vector3(2.8f,2.8f,2.8f);plane.rotation=Quaternion.Euler(15,45,70);
   Transform pilot=SeatBinding.AircraftSeat(plane,true,-1);Assert(Near(pilot.position,plane.position+plane.rotation*(An2Model.Seat(-1)*2.8f)),"aircraft donor scale not multiplied twice");Assert(pilot==SeatBinding.AircraftSeat(plane,true,-1),"seat transform reused");
   GameObject remote=new GameObject("remote player");Crocodile.Players[7]=remote;
   SeatBinding.AircraftBoard(plane.gameObject,7,new float[]{50,1,0,3},true);Time.time=1;SeatBinding.LateFrame();Same(remote.transform,SeatBinding.AircraftSeat(plane,true,3));
   SeatBinding.AircraftBoard(plane.gameObject,7,new float[]{50,1,1,-1},true);Time.time=2;SeatBinding.LateFrame();Same(remote.transform,pilot);
   SeatBinding.AircraftBoard(plane.gameObject,7,new float[]{50,0,0},true);Assert(remote.transform.parent==null,"remote exit immediate");
   SeatBinding.AircraftBoard(plane.gameObject,7,new float[]{50,1,0},true);Time.time=3;SeatBinding.LateFrame();Same(remote.transform,SeatBinding.AircraftSeat(plane,true,0));
   Time.time=20;SeatBinding.LateFrame();Assert(remote.transform.parent==null,"lost heartbeat releases remote");
   SeatBinding.BindSeat(remote.transform,pilot,plane);SeatBinding.ReleaseVehicle(plane);Assert(remote.transform.parent==null,"vehicle disable/destruction releases rider");
   // A dynamic technical gunner remains parented to his seat with a local orbit.
   Transform mount=New("pintle"),gunner=New("gunner");mount.SetParent(hull,false);mount.localRotation=Quaternion.Euler(0,65,0);hull.rotation=Quaternion.Euler(20,90,35);
   Vector3 stand=mount.TransformPoint(new Vector3(0,0,-1));SeatBinding.BindWorld(gunner,v.SeatPoints.GetChild(2),hull,stand,mount.rotation);Assert(Near(gunner.position,stand),"gunner orbit position");Assert(Near(gunner.rotation*Vector3.up,mount.rotation*Vector3.up),"gunner uses hull up axis");
   SeatBinding.ReleaseVehicle(hull);
   // Warm steady-state path, then verify no managed heap growth / collections.
   Transform perfHull=New("perf"),perfSeat=New("perf seat"),perfBody=New("perf body");perfSeat.SetParent(perfHull,false);SeatBinding.BindSeat(perfBody,perfSeat,perfHull);
   for(int i=0;i<1000;i++)SeatBinding.LateFrame();GC.Collect();long before=GC.GetTotalMemory(true);int collections=GC.CollectionCount(0);
   for(int i=0;i<10000;i++)SeatBinding.LateFrame();long after=GC.GetTotalMemory(false);Assert(after<=before,"zero steady state managed heap growth");Assert(GC.CollectionCount(0)==collections,"zero steady state GC collections");
   SeatBinding.ReleaseVehicle(perfHull);
   Console.WriteLine("PASS native player boarding/seat change/exit, remote aircraft seat/heartbeat/exit, donor scale, vehicle release, gunner orbit");
   Console.WriteLine("PASS steady state: 10000 ticks, heap growth "+(after-before)+" bytes, 0 GC collections");
   Console.WriteLine("PASS real C# 3.0 SeatBinding: "+checks+" assertions");
  }
 }
}
'''


def source_checks():
    def require(path, *needles):
        text = (ROOT / path).read_text(encoding="utf-8")
        for needle in needles:
            assert needle in text, (path, needle)
    require("RevivalPlugin.cs", "SeatBinding.Install(_harmony)",
            "FrameProf.S(FrameProf.S_SeatBindingL); SeatBinding.LateFrame()")
    for path in ("Revival.PlayerAn2.cs", "Revival.PlayerHeli.cs"):
        require(path, "SeatBinding.BindSeat(_body,", "SeatBinding.Detach(_body);",
                "SeatBinding.AircraftBoard(ByView(view), sender, f,", "_pilot ? 1f : 0f, _seat }")
    require("Revival.MercsRide.cs", "SeatBinding.BindWorld(tr, anchor, c.Root, pos, rot);",
            "SeatBinding.Detach(st.Ai.transform);", "rot = root.rotation;", 
            "? c.Mount.rotation : sp.rotation;")
    require("RevivalTechnicalCrew.cs", "SeatBinding.BindSeat(ai.transform, t.Seats.GetChild(seat), t.Root);",
            "SeatBinding.Detach(ai.transform);", "return t.Root.rotation;")
    require("RevivalGepardCrew.cs", "SeatBinding.BindSeat(ai.transform, h.Seats.GetChild(CrewSeats[k]), h.Root);",
            "SeatBinding.Detach(ai.transform);")
    require("RevivalTechnical.cs", "Quaternion rotation = st.Mount.rotation;", "rotation = st.Vgs.transform.rotation * Quaternion.LookRotation(facing, Vector3.up);",
            "SeatBinding.BindWorld(body.transform, st.RiderSeat,")
    require("Revival.AirEvents.cs", "j.Body.Aboard(s.Plane, k)")
    require("Revival.Paratroopers.cs", "SeatBinding.BindSeat(Root,", "SeatBinding.Detach(Root);")
    print("PASS source wiring: native vehicles, local/remote aircraft, mercs, crews, technical gunner, paratroopers, exits and F6")


def main():
    source_checks()
    work = ROOT / "build" / "seated_bodies_check"
    work.mkdir(parents=True, exist_ok=True)
    source = work / "check.cs"
    source.write_bytes((STUBS + (ROOT / "Revival.SeatBinding.cs").read_text(encoding="utf-8")
                        .replace("using System;", "").replace("using System.Collections.Generic;", "")
                        .replace("using System.Reflection;", "").replace("using HarmonyLib;", "")
                        .replace("using UnityEngine;", "") + HARNESS).replace(
                            "new HarmonyMethod(", "new HarmonyLib.HarmonyMethod(").replace(
                                "Install(Harmony harmony)", "Install(HarmonyLib.Harmony harmony)").replace(
                                    "AccessTools.", "HarmonyLib.AccessTools.").encode("ascii"))
    compiler = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework/v3.5/csc.exe"
    exe = work / "check.exe"
    subprocess.run([str(compiler), "/nologo", "/warn:0", "/out:" + str(exe), str(source)], check=True)
    subprocess.run([str(exe)], check=True)


if __name__ == "__main__":
    main()

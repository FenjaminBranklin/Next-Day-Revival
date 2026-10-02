"""K3a: compile production policy, pose and moving-shot adapter unchanged.

Unity/Photon doubles exercise masks, gait direction, IK, ammo/fire vetoes,
cleanup and packet ownership. Native scene animation/PhysX remain game QA.
"""
from pathlib import Path
import json
import os
import subprocess
import sys
import re
import uuid

ROOT = Path(__file__).resolve().parents[1]
CSC = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v3.5/csc.exe"

SUPPORT = r'''
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UnityEngine {
 public class Object { public string name; public static implicit operator bool(Object o){return o!=null;} }
 public class Component:Object {
  public GameObject gameObject; public Transform transform { get { return gameObject.transform; } }
  public T GetComponent<T>() where T:Component { return gameObject.GetComponent(typeof(T)) as T; }
  public Component GetComponent(Type t){return gameObject.GetComponent(t);}
 }
 public class MonoBehaviour:Component { public bool enabled; }
 public sealed class DefaultExecutionOrder:Attribute { public DefaultExecutionOrder(int i){} }
 public class GameObject:Object {
  public Transform transform; public bool activeSelf=true;
  public Dictionary<Type,Component> parts=new Dictionary<Type,Component>();
  public GameObject(){transform=new Transform();transform.gameObject=this;}
  public void SetActive(bool value){activeSelf=value;}
  public Component GetComponent(Type t){Component c;parts.TryGetValue(t,out c);return c;}
  public T AddComponent<T>() where T:Component,new(){T c=new T();c.gameObject=this;parts[typeof(T)]=c;return c;}
 }
 public class Transform:Component {
  public Vector3 position,forward=new Vector3(0,0,1); public Transform parent;
  public Dictionary<string,Transform> children=new Dictionary<string,Transform>();
  public Transform Find(string n){Transform t;children.TryGetValue(n,out t);return t;}
  public Vector3 InverseTransformDirection(Vector3 v){return v;}
  public bool IsChildOf(Transform t){for(Transform p=this;p!=null;p=p.parent)if(p==t)return true;return false;}
 }
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public float magnitude{get{return (float)Math.Sqrt(x*x+y*y+z*z);}}
  public float sqrMagnitude{get{return x*x+y*y+z*z;}}
  public Vector3 normalized{get{return this/Math.Max(.00001f,magnitude);}}
  public static Vector3 zero{get{return new Vector3();}}
  public static float Dot(Vector3 a,Vector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static bool operator ==(Vector3 a,Vector3 b){return (a-b).sqrMagnitude<.000001f;}
  public static bool operator !=(Vector3 a,Vector3 b){return !(a==b);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator /(Vector3 a,float b){return new Vector3(a.x/b,a.y/b,a.z/b);}
 }
 public enum WrapMode { Loop,ClampForever }
 public class AnimationClip:Object{}
 public class AnimationState {
  public AnimationClip clip; public bool enabled; public float weight,time,speed,length=1f;
  public int layer; public WrapMode wrapMode;
  public Dictionary<Transform,bool> mask=new Dictionary<Transform,bool>();
  public void AddMixingTransform(Transform t,bool recursive){mask[t]=recursive;}
 }
 public class Animation:Component {
  public Dictionary<string,AnimationState> states=new Dictionary<string,AnimationState>();
  public AnimationState this[string n]{get{AnimationState s;states.TryGetValue(n,out s);return s;}}
  public void AddClip(AnimationClip c,string n){states[n]=new AnimationState();states[n].clip=c;}
  public void RemoveClip(string n){states.Remove(n);}
 }
 public static class Resources { public static Object[] clips; public static int scans;
  public static Object[] FindObjectsOfTypeAll(Type t){scans++;return clips;}
 }
 public static class Time { public static float time,deltaTime=1f/60f; public static int frameCount; }
 public static class Mathf {
  public static float Min(float a,float b){return Math.Min(a,b);} public static float Max(float a,float b){return Math.Max(a,b);}
  public static float Clamp(float v,float a,float b){return Math.Max(a,Math.Min(b,v));}
  public static float Clamp01(float v){return Clamp(v,0f,1f);}
  public static float MoveTowards(float a,float b,float d){return a<b?Math.Min(b,a+d):Math.Max(b,a-d);}
 }
 public enum QueryTriggerInteraction{Ignore}
 public class Collider:Component{public Vector3 centre,half;public int layer;public bool trigger;public Collider(){gameObject=new GameObject();}}
 public struct RaycastHit{public Collider collider;}
 // All scene colliders, irrespective of type/layer. Analytic intersection is
 // used only by the physics double; production still uses Unity PhysX.
 public static class Physics {
  public static List<Collider> scene=new List<Collider>();public static int calls,lastMask;
  public static int OverlapSphereNonAlloc(Vector3 p,float r,Collider[] b,int mask,QueryTriggerInteraction q){
   calls++;lastMask=mask;int n=0;foreach(Collider c in scene){if(c.trigger)continue;
    if(Math.Abs(c.centre.x-p.x)<=c.half.x+r&&Math.Abs(c.centre.y-p.y)<=c.half.y+r&&Math.Abs(c.centre.z-p.z)<=c.half.z+r){if(n==b.Length)return n;b[n++]=c;}}
   return n;
  }
  public static int RaycastNonAlloc(Vector3 p,Vector3 d,RaycastHit[] b,float dist,int mask,QueryTriggerInteraction q){
   calls++;lastMask=mask;int n=0;foreach(Collider c in scene){if(c.trigger)continue;float low=0,high=dist;
    if(!Axis(p.x,d.x,c.centre.x,c.half.x,ref low,ref high)||!Axis(p.y,d.y,c.centre.y,c.half.y,ref low,ref high)||!Axis(p.z,d.z,c.centre.z,c.half.z,ref low,ref high))continue;
    if(n==b.Length)return n;b[n++].collider=c;}
   return n;
  }
  static bool Axis(float p,float d,float c,float h,ref float low,ref float high){
   if(Math.Abs(d)<.000001f)return Math.Abs(p-c)<=h;
   float a=(c-h-p)/d,z=(c+h-p)/d;if(a>z){float t=a;a=z;z=t;}
   low=Math.Max(low,a);high=Math.Min(high,z);return low<=high;
  }
 }
}
namespace UnityEngine.AI { public class NavMeshAgent:Component {
 public bool updateRotation=true;public float stoppingDistance=1.5f;public Vector3 velocity=new Vector3(0,0,4.2f);
}}
namespace HarmonyLib { public static class AccessTools {
 public static FieldInfo Field(Type t,string n){return t.GetField(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);}
 public static MethodInfo PropertyGetter(Type t,string n){return t.GetProperty(n).GetGetMethod();}
 public static MethodInfo Method(Type t,string n,Type[] p,object g){return t.GetMethod(n,p);}
}}
namespace BepInEx.Configuration {
 public class ConfigEntry<T>{public T Value;}
 public class ConfigFile{public ConfigEntry<T> Bind<T>(string s,string k,T v,string d){return new ConfigEntry<T>{Value=v};}}
}
namespace NextDayRevival {
 public class Solver{public float IKPositionWeight;}
 public class IK:Component{public Solver solver=new Solver();}
 public class NPC_AI2:Component {
  public Animation Anim=new Animation(); public Transform _mainCharSpine,LookAtIKTarget=new Transform();
  public IK _aimIk;public Vector3 _aimingPoint; public int MainState=1,PoseState,AdditionalState;
  public float RateDelay,ShotTimer;
 }
 public class PhotonView:Component {
  public int ownerId=2,id;public int viewID{get{return id;}}
  public static Dictionary<int,PhotonView> views=new Dictionary<int,PhotonView>();
  public static PhotonView Find(int n){PhotonView v;views.TryGetValue(n,out v);return v;}
 }
 public class Log{public void LogWarning(string s){}}
 public static class RevivalPlugin{public static Log L=new Log();public static Type TypeByName(string n){return n=="NPC_AI2"?typeof(NPC_AI2):typeof(PhotonView);}}
 public static class MercRide{public static int sends;public static float[] last;public static void SendAAPacket(float[] d){sends++;last=d;}}
 public static class Mercs{public const string KeyPrefix="merc/";}
 public static class Crew{public static string GroundKey(Component ai){return "merc/2/test";}}
 public static class FrameProf{public const int S_MercMoveShootPose_Update=185,S_MercMoveShoot_Fire=187;public static void S(int i){}public static void E(int i){}}
 // Typed doubles mirror FastField's emitted allocation-free accessors.
 public static class FastField {
  public static int GetInt(FieldInfo f,object o){
   if(o is PhotonView)return ((PhotonView)o).ownerId;
   NPC_AI2 a=(NPC_AI2)o;return f.Name=="MainState"?a.MainState:f.Name=="PoseState"?a.PoseState:a.AdditionalState;
  }
  public static void SetFloat(FieldInfo f,object o,float v){((Solver)o).IKPositionWeight=v;}
  public static float GetFloat(FieldInfo f,object o){NPC_AI2 a=(NPC_AI2)o;return f.Name=="RateDelay"?a.RateDelay:a.ShotTimer;}
  public static void SetVector3(FieldInfo f,object o,Vector3 v){((NPC_AI2)o)._aimingPoint=v;}
 }
 public static class FastCall { public static bool Bool(MethodInfo m,object o){return NpcWar.AmmoReady;} }
 internal struct CoverPoint{internal Vector3 Pos;}
 internal struct CoverPick{internal bool Found,Confirmed;internal CoverPoint Point;}
 internal class MercBrain{internal const byte Normal=0,Dash=1,PeekOut=3,PeekBack=5,Snap=10,AttackFire=11,Evade=8;internal byte Mode;internal CoverPick Cover=new CoverPick{Found=true,Confirmed=true};internal bool SurvivalFire;}
 internal class Medicine{internal int Active;}
 internal class MercSense{internal int Count=1;internal CoverPick Pick;}
 internal class MercOrder{internal const byte Attack=5;internal byte Mode;internal Vector3 Centre;}
 internal class MercUnit{internal Medicine Medicine=new Medicine();internal MercSense Sense=new MercSense();internal MercOrder Order=new MercOrder();}
 internal struct FightOut{internal byte Act;internal bool NoShot,Suppress;internal Vector3 Dest;}
 internal static class FightAct{internal const byte Run=1,Step=2,Fire=4;}
 internal struct FightIn{internal bool Danger,Survive,PickFresh,Attack,MoveShoot;}
 internal static class MercReaction{internal const byte Muzzle=1;}
 internal class Reaction{internal bool Open=true;internal int decisions,fires;internal void Decided(float n){decisions++;}internal void Fired(float n){fires++;Open=false;}internal void Held(byte why){}}
 internal class MercFight{
  internal MercBrain Brain=new MercBrain();internal byte State=MercBrain.PeekOut,Gate;internal float Health=1;
  internal FightIn In;internal bool MovePoseTried;internal int MovingShots;internal float LastShotAt;internal MercMoveShootPose MovePose;internal Reaction React=new Reaction();
 }
 public static partial class NpcWar {
  class Squad{public int Shots;}
  class Fighter{public Component Ai;public Transform Tr,Target;public bool Armed=true,TargetIsPlayer,Sees=true;public int WeaponId=1007;public float ReactUntil,NextShot,Suppression,MuzzleBlockedSince;public Squad Squad=new Squad();}
  static BepInEx.Configuration.ConfigEntry<bool> CfgDebug=new BepInEx.Configuration.ConfigEntry<bool>();
  static bool Reload,Friend,MuzzleOpen=true;static int Ammo=30,Shots;static UnityEngine.AI.NavMeshAgent AgentValue;
  static bool Reloading(Fighter f){return Reload;}
  static bool MercMayFireAt(Fighter f,Vector3 at,float now){return Flat(at-f.Tr.position)<=500f;}
  static MethodInfo _mHasBullets=typeof(NpcWar).GetMethod("HasBullets",BindingFlags.Static|BindingFlags.NonPublic);
  static FieldInfo _fRofDelay=typeof(NPC_AI2).GetField("RateDelay"),_mercLineShotTimer=typeof(NPC_AI2).GetField("ShotTimer");
  static Action<object,Transform> _mercPositionShot=PlayerShot;
  static bool HasBullets(Component w){return Ammo>0;}
  static void PlayerShot(object ai,Transform target){if(!MuzzleOpen||Ammo==0)return;Ammo--;Shots++;((NPC_AI2)ai).RateDelay=Time.time+.25f;}
  static void StartReload(Fighter f){Reload=true;}
  public static bool AmmoReady{get{return Ammo>0;}}
  static UnityEngine.AI.NavMeshAgent Agent(Fighter f){return AgentValue;}
  static Vector3 AimWorld(Fighter f){return f.Target.position;}
  static Component WeaponOf(Fighter f){return f.Ai;}
  static Vector3 Muzzle(Component w,Fighter f){return new Vector3(0,3.75f,.5f);}
  static float Flat(Vector3 v){return (float)Math.Sqrt(v.x*v.x+v.z*v.z);}
  static void Face(Fighter f){}static void MercTraceLog(MercUnit u,Reaction r){}
  static bool MercFriendInLine(Fighter f,Vector3 a,Vector3 b){return Friend;}
  static float ShotDelay(Fighter f){return 0.25f;}
  static bool Shoot(Fighter f){if(!MuzzleOpen||Ammo==0)return false;Ammo--;Shots++;return true;}
  internal static void Adapter(NPC_AI2 ai,UnityEngine.AI.NavMeshAgent agent){
   Fighter f=new Fighter{Ai=ai,Tr=ai.transform,Target=new Transform()};f.Target.position=new Vector3(0,3.75f,100);
   AgentValue=agent;MercFight ft=new MercFight();MercUnit u=new MercUnit();FightOut act=new FightOut{Act=2};
   Time.time=50;Test.Ok(MercWalkingPose(f,u,ft,ref act,50),"adapter enables confirmed peek step");
   Action tick=Test.Tick(ft.MovePose);for(int j=0;j<20;j++){Time.frameCount++;Time.time=50+j/60f;MercWalkingPose(f,u,ft,ref act,Time.time);tick();MercWalkingFire(f,u,ft,act,Time.time);}
   Test.Ok(Shots>0&&Ammo<30&&ai.MainState==1,"rounds consume ammunition while native walk state stays active");
   int shots=Shots;Friend=true;Time.time+=1;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots,"live friendly veto holds walking shots");Friend=false;
   MuzzleOpen=false;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots&&f.NextShot>=Time.time+.19f,"blocked muzzle holds and throttles retry");MuzzleOpen=true;
   f.NextShot=0;f.Sees=false;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots,"unseen target receives no moving suppression");f.Sees=true;
   act.NoShot=true;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots,"brain NoShot veto remains authoritative");act.NoShot=false;
    f.TargetIsPlayer=true;Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time)&&!agent.updateRotation,"player targets use the installed K1b native bridge while walking");
    Time.frameCount++;f.NextShot=0;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots>shots,"native player-target round fires without planting");shots=Shots;f.TargetIsPlayer=false;
   Reload=true;Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"reload is never overridden");Reload=false;
   ft.In.Danger=true;Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"blast danger keeps existing escape loop");ft.In.Danger=false;
   ft.Health=.3f;Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"wounded merc keeps survival movement");ft.Health=1;
   u.Medicine.Active=7013;Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"medkit pose cannot share walking-fire layers");u.Medicine.Active=0;
    ft.Brain.Cover.Confirmed=false;Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time),"existing combat step needs a clear muzzle, not a confirmed cover flag");ft.Brain.Cover.Confirmed=true;
   act.Act=FightAct.Run;act.Dest=new Vector3(0,0,12);ft.State=MercBrain.Dash;
   Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time),"short calm M1 cover bound retains aim and walking fire");
    f.Suppression=.4f;Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time),"incoming pressure does not silence an existing short bound");f.Suppression=0;
   act.Dest=new Vector3(0,0,30);Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"long bound retains fast sprint");
   ft.Brain.Mode=2;act.Dest=new Vector3(0,0,12);Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time),"team fallback cannot be slowed by moving fire");ft.Brain.Mode=0;
   u.Order.Mode=MercOrder.Attack;u.Order.Centre=new Vector3(0,0,100);ft.State=MercBrain.Snap;act.Act=FightAct.Fire;ft.In.PickFresh=true;
   u.Sense.Pick=new CoverPick{Found=true,Confirmed=true,Point=new CoverPoint{Pos=new Vector3(0,0,12)}};
   Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time)&&act.Act==FightAct.Run&&act.Dest.z==12,"ATTACK opening burst pushes toward confirmed forward cover");
   act.Act=FightAct.Fire;u.Sense.Pick.Point.Pos=new Vector3(0,0,-12);
   Test.Ok(!MercWalkingPose(f,u,ft,ref act,Time.time)&&act.Act==FightAct.Fire,"ATTACK never substitutes backward cover for forward progress");
   act.Act=FightAct.Step;ft.State=MercBrain.PeekBack;Test.Ok(MercWalkingPose(f,u,ft,ref act,Time.time),"return step keeps the same walking aim");
   for(int j=0;j<20;j++){Time.frameCount++;Time.time+=1f/60;MercWalkingPose(f,u,ft,ref act,Time.time);tick();}
   shots=Shots;f.NextShot=0;f.Tr.forward=new Vector3(1,0,0);MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots,"turning more than 37 degrees holds fire to avoid torso twisting");f.Tr.forward=new Vector3(0,0,1);
   Physics.scene.Add(new Collider{centre=new Vector3(0,3.75f,10),half=new Vector3(2,2,1),layer=2});
   Time.frameCount++;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots==shots&&f.MuzzleBlockedSince>0,"actual moving shot refuses an IgnoreRaycast-layer slab");Physics.scene.Clear();
   f.NextShot=0;Time.frameCount++;MercWalkingFire(f,u,ft,act,Time.time);Test.Ok(Shots>shots&&ft.MovingShots==Shots,"clear return step fires and counts only real rounds");
  }
  internal static void Budget(){
   const int Count=6,Frames=20000;
   Fighter[] fighters=new Fighter[Count];MercFight[] fights=new MercFight[Count];MercUnit[] units=new MercUnit[Count];Action[] ticks=new Action[Count];
   FightOut act=new FightOut{Act=FightAct.Step};AgentValue=new UnityEngine.AI.NavMeshAgent();
   Physics.scene.Clear();Ammo=1000000;Time.time=2000;
   for(int i=0;i<Count;i++){
    Time.frameCount++;NPC_AI2 ai=Test.Body(100+i);
    fighters[i]=new Fighter{Ai=ai,Tr=ai.transform,Target=new Transform()};fighters[i].Target.position=new Vector3(0,3.75f,100);
    fights[i]=new MercFight();units[i]=new MercUnit();
    Test.Ok(MercWalkingPose(fighters[i],units[i],fights[i],ref act,Time.time),"six-body setup is spread over frames");ticks[i]=Test.Tick(fights[i].MovePose);
   }
   for(int frame=0;frame<1000;frame++){Time.frameCount++;Time.time+=1f/60;for(int i=0;i<Count;i++){
    MercWalkingPose(fighters[i],units[i],fights[i],ref act,Time.time);ticks[i]();MercWalkingFire(fighters[i],units[i],fights[i],act,Time.time);}}
   System.Diagnostics.Stopwatch sw=new System.Diagnostics.Stopwatch();sw.Start();sw.Stop();sw.Reset();
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long memory=GC.GetTotalMemory(false);int gc=GC.CollectionCount(0),calls=Physics.calls;long peak=0;sw.Start();
   for(int frame=0;frame<Frames;frame++){long start=System.Diagnostics.Stopwatch.GetTimestamp();Time.frameCount++;Time.time+=1f/60;
    for(int i=0;i<Count;i++){MercWalkingPose(fighters[i],units[i],fights[i],ref act,Time.time);ticks[i]();MercWalkingFire(fighters[i],units[i],fights[i],act,Time.time);}
    long elapsed=System.Diagnostics.Stopwatch.GetTimestamp()-start;if(elapsed>peak)peak=elapsed;}
   sw.Stop();long delta=GC.GetTotalMemory(false)-memory;int collections=GC.CollectionCount(0)-gc;int geometry=Physics.calls-calls;
   Console.WriteLine("BUDGET six moving mercs avg_ms="+(sw.Elapsed.TotalMilliseconds/Frames).ToString("F6")+" peak_ms="+(peak*1000.0/System.Diagnostics.Stopwatch.Frequency).ToString("F4")+" heap_delta="+delta+" gen0="+collections+" physics_calls="+geometry);
   Test.Ok(delta==0&&collections==0,"20000 production pose/adapter frames for six mercs retain zero bytes and collect nothing");
   Test.Ok(geometry<=Frames*2,"six mercs share at most one all-collider two-query shot job per frame");
   bool served=true;for(int i=0;i<Count;i++)if(fights[i].MovingShots==0)served=false;
   Test.Ok(served,"synchronized sustained moving fire never starves a squad member");
   MercMoveShoot.CfgEnabled.Value=false;calls=Physics.calls;
   for(int i=0;i<Count;i++)Test.Ok(!MercWalkingPose(fighters[i],units[i],fights[i],ref act,Time.time),"config off ends each owned pose");
   Test.Ok(Physics.calls==calls,"disabled feature performs no geometry work");MercMoveShoot.CfgEnabled.Value=true;
  }
 }
 internal static class Test {
  static int passed,failed;
  public static void Ok(bool value,string message){Console.WriteLine((value?"PASS ":"FAIL ")+message);if(value)passed++;else failed++;}
  public static Action Tick(MercMoveShootPose p){return (Action)Delegate.CreateDelegate(typeof(Action),p,typeof(MercMoveShootPose).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic));}
  internal static NPC_AI2 Body(int id){
   GameObject g=new GameObject();NPC_AI2 a=g.AddComponent<NPC_AI2>();PhotonView v=g.AddComponent<PhotonView>();v.id=id;PhotonView.views[id]=v;
   a._mainCharSpine=new Transform();Transform hips=new Transform();a._mainCharSpine.parent=hips;
   hips.children["MainChar_Thigh.L"]=new Transform();hips.children["MainChar_Thigh.R"]=new Transform();
   a._aimIk=new GameObject().AddComponent<IK>();
   foreach(string t in new string[]{"asr","rifle","hg"})a.Anim.AddClip(new AnimationClip{name=t+"_shoot"},t=="rifle"?"rifle_shoot_samopal":t+"_shoot_auto");
   return a;
  }
  static void Geometry(){
   Transform self=new Transform(),target=new Transform();Vector3 from=new Vector3(0,3.75f,0),to=new Vector3(0,3.75f,40);
   Collider own=new Collider{centre=from,half=new Vector3(.5f,2,.5f)};own.transform.parent=self;
   Collider trigger=new Collider{centre=new Vector3(0,3.75f,6),half=new Vector3(2,2,1),trigger=true};
   Collider wall=new Collider{centre=new Vector3(0,3.75f,10),half=new Vector3(2,4,1)};
   Collider slab=new Collider{centre=new Vector3(5.6f,3.75f,14),half=new Vector3(2,4,1),layer=2};
   Collider fence=new Collider{centre=new Vector3(-5.6f,3.75f,18),half=new Vector3(2,4,.1f),layer=2};
   Collider sandbag=new Collider{centre=new Vector3(11.2f,3.75f,22),half=new Vector3(1,2,1)};
   Physics.scene.AddRange(new Collider[]{own,trigger,wall,slab,fence,sandbag});
   Ok(!MercMovingLane.Clear(from,to,self,target),"combined area includes wall, slabs, fence, props and own body");
   Ok(!MercMovingLane.Clear(new Vector3(5.6f,3.75f,0),new Vector3(5.6f,3.75f,40),self,target),"moving sideways past wall still rejects concrete slab on layer 2");
   Ok(!MercMovingLane.Clear(new Vector3(-5.6f,3.75f,0),new Vector3(-5.6f,3.75f,40),self,target),"all-layer fence veto is independent of cover-model boxes");
   Ok(!MercMovingLane.Clear(new Vector3(11.2f,3.75f,0),new Vector3(11.2f,3.75f,40),self,target),"sandbag prop is part of the same area query");
   Ok(MercMovingLane.Clear(new Vector3(20,3.75f,0),new Vector3(20,3.75f,40),self,target)&&Physics.lastMask==-1,"clear lane with all scene colliders checked");
   Physics.scene.Clear();Physics.scene.Add(own);Physics.scene.Add(trigger);
   Ok(MercMovingLane.Clear(from,to,self,target),"only self hierarchy and triggers can be excluded");
   Collider at=new Collider{centre=from,half=new Vector3(.1f,.1f,.1f)};Physics.scene.Add(at);
   Ok(!MercMovingLane.Clear(from,to,self,target),"muzzle inside a solid collider is refused");Physics.scene.Clear();
   for(int i=0;i<64;i++)Physics.scene.Add(new Collider{centre=new Vector3(0,3.75f,10+i*.2f),half=new Vector3(.1f,.1f,.05f)});
   Ok(!MercMovingLane.Clear(from,to,self,target),"saturated ray buffer fails closed");Physics.scene.Clear();
   for(int i=0;i<32;i++)Physics.scene.Add(own);
   Ok(!MercMovingLane.Clear(from,to,self,target),"saturated origin buffer fails closed even with self hits");Physics.scene.Clear();
   bool[] served=new bool[6];int total=0;
   for(int frame=0;frame<6;frame++){Time.frameCount++;int won=0;for(int merc=0;merc<6;merc++){if(served[merc])continue;if(MercMovingLane.MayCheck()){served[merc]=true;total++;won++;}}Ok(won==1,"global moving-shot budget admits one due merc/frame");}
   Ok(total==6,"six synchronized due mercs receive a turn within six frames");
  }
  public static int Main(){
   System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
   List<UnityEngine.Object> clips=new List<UnityEngine.Object>();foreach(string t in new string[]{"asr","rifle","hg"})foreach(string gait in new string[]{"walk_aiming","walk_back_aiming","walk_left_aiming","walk_right_aiming"})clips.Add(new AnimationClip{name=t+"_"+gait});Resources.clips=clips.ToArray();
   Time.frameCount=1;Ok(MercMoveShootPose.MaySetup()&&!MercMoveShootPose.MaySetup(),"cold owner body setup is limited to one per frame");Time.frameCount++;
   NPC_AI2 a=Body(10);UnityEngine.AI.NavMeshAgent agent=new UnityEngine.AI.NavMeshAgent();MercMoveShootPose p=MercMoveShootPose.Create(a,agent);
   Time.time=1;Ok(p.Touch(1007,new Vector3(0,8,100),1)&&!agent.updateRotation,"pose takes rotation without stopping navigation");Action tick=Tick(p);
   Ok(agent.stoppingDistance==.1f,"walking peek does not brake behind the measured edge");
   p.Touch(1007,new Vector3(0,8,100),1.01f);p.Stop();
   Ok(agent.stoppingDistance==1.5f,"repeated touches preserve and restore the original native stopping radius");
   p.Touch(1007,new Vector3(0,8,100),1);
   AnimationState walk=a.Anim["ndr_k0_forward"],shot=a.Anim["ndr_k0_recoil"];
   Ok(walk.layer==7&&walk.mask.Count==4&&!walk.mask[a._mainCharSpine.parent]&&shot.layer==8&&shot.mask.Count==1&&shot.mask[a._mainCharSpine],"pelvis/legs/spine masks exclude root; recoil excludes legs");
   Ok(a.Anim["asr_shoot_auto"].layer==0,"private aliases preserve native AnimationState");
   for(int j=1;j<=15;j++){Time.time=1+j/60f;p.Touch(1007,new Vector3(0,8,100),Time.time);tick();}
   Ok(p.Ready(Time.time)&&a._aimIk.solver.IKPositionWeight>=.6f&&a.LookAtIKTarget.position.y==8,"walking aim ramps IK and binds elevation");
   agent.velocity=new Vector3(-4,0,0);tick();Ok(a.Anim["ndr_k0_left"].enabled&&walk.enabled,"direction change blends legs instead of snapping");
   for(int j=0;j<10;j++)tick();Ok(!walk.enabled&&a.Anim["ndr_k0_left"].weight==1,"left strafe selects player left gait after the fade");
   agent.velocity=new Vector3(0,0,-4);tick();Ok(a.Anim["ndr_k0_back"].enabled,"returning step selects backward gait");
   p.Recoil(Time.time);tick();Ok(shot.enabled,"real round pulses upper-body recoil");
   Time.time+=.23f;p.Touch(1007,new Vector3(),Time.time);tick();Ok(!shot.enabled,"recoil expires independently of walking legs");
   p.Stop();Ok(agent.updateRotation&&!p.enabled&&!shot.enabled&&!a.Anim["ndr_k0_back"].enabled&&a._aimIk.solver.IKPositionWeight==0,"stop restores rotation, IK and all private layers");
   agent.updateRotation=false;p.Touch(1007,new Vector3(),Time.time);p.Stop();Ok(!agent.updateRotation,"pre-existing disabled agent rotation is preserved");agent.updateRotation=true;
   p.Touch(1007,new Vector3(),Time.time);Time.time+=.31f;tick();Ok(agent.updateRotation&&!p.enabled,"owner lease clears bypassed fight/order/seat paths");
   p.Touch(1007,new Vector3(),Time.time);a.MainState=7;Time.time+=.6f;p.Touch(1007,new Vector3(),Time.time);tick();Ok(!p.enabled,"wounded/death/native state mismatch clears pose");a.MainState=1;
   int scans=Resources.scans;p.Touch(1010,new Vector3(),Time.time);p.Stop();p.Touch(1201,new Vector3(),Time.time);p.Stop();Ok(Resources.scans==scans,"weapon family changes reuse one cold donor lookup");
   Ok(!p.Touch(1162,new Vector3(),Time.time),"LAW keeps its native firing pose");
   float[] d={103,11,1,1007,1,0,8,100,1,1};NPC_AI2 peer=Body(11);MercMoveShootPose.OnPacket(d,3);Ok(peer.GetComponent<MercMoveShootPose>()==null,"foreign actor cannot create remote pose");
   MercMoveShootPose.OnPacket(d,2);MercMoveShootPose remote=peer.GetComponent<MercMoveShootPose>();Action remoteTick=Tick(remote);remoteTick();Ok(remote.enabled,"body owner starts remote pose");
   d[2]=2;d[6]=12;MercMoveShootPose.OnPacket(d,2);remoteTick();Ok(peer.LookAtIKTarget.position.y==12,"heartbeat updates peer aim without restarting phase");
   d[2]=1;d[6]=2;MercMoveShootPose.OnPacket(d,2);remoteTick();Ok(peer.LookAtIKTarget.position.y==12,"old reordered packet cannot overwrite aim");
   Time.time+=1.51f;remoteTick();Ok(!remote.enabled,"lost stop/disconnect expires remote layers");
   d[2]=3;MercMoveShootPose.OnPacket(d,2);d[2]=4;d[4]=0;MercMoveShootPose.OnPacket(d,2);Ok(!remote.enabled,"owner stop packet clears remote pose");
   d[2]=3;d[4]=1;MercMoveShootPose.OnPacket(d,2);Ok(!remote.enabled,"old active packet cannot resurrect a stopped pose");
   d[4]=1;d[2]=5;d[6]=float.NaN;Ok(!MercMoveShootPolicy.Packet(d),"malformed nonfinite packet is rejected");d[6]=1;d[2]=5.5f;Ok(!MercMoveShootPolicy.Packet(d),"fractional sequence is rejected");
   Ok(!MercMoveShootPolicy.Accept(2,2,1,1,false)&&!MercMoveShootPolicy.Accept(2,2,2,1,true),"duplicates and local authority are protected");
   MercMoveShoot.BindConfig(new BepInEx.Configuration.ConfigFile());Ok(MercMoveShoot.Enabled,"walking-fire config is on by default");
   p.Touch(1007,new Vector3(),Time.time);MercMoveShoot.CfgEnabled.Value=false;tick();Ok(!p.enabled&&agent.updateRotation,"config off restores native movement immediately");MercMoveShoot.CfgEnabled.Value=true;
   NPC_AI2 bad=Body(14);bad._mainCharSpine.parent.children.Remove("MainChar_Thigh.L");Ok(MercMoveShootPose.Create(bad,null)==null,"missing rig bone falls back without a partial pose");
   a=Body(12);agent=new UnityEngine.AI.NavMeshAgent();NpcWar.Adapter(a,agent);
   a=Body(13);agent=new UnityEngine.AI.NavMeshAgent();p=MercMoveShootPose.Create(a,agent);tick=Tick(p);Time.time=100;p.Touch(1007,new Vector3(),Time.time);
   for(int j=0;j<1000;j++){Time.time+=1f/60;p.Touch(1007,new Vector3(),Time.time);tick();}
   System.Diagnostics.Stopwatch sw=new System.Diagnostics.Stopwatch();sw.Start();sw.Stop();sw.Reset();
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long memory=GC.GetTotalMemory(false);int collections=GC.CollectionCount(0),sent=MercRide.sends;sw.Start();
   for(int j=0;j<20000;j++){Time.time+=1f/60;p.Touch(1007,new Vector3(),Time.time);tick();}
   sw.Stop();long delta=GC.GetTotalMemory(false)-memory;int gc=GC.CollectionCount(0)-collections;
   Console.WriteLine("BUDGET pose avg_us="+(sw.Elapsed.TotalMilliseconds*1000/20000).ToString("F3")+" heap_delta="+delta+" gen0="+gc+" packets="+(MercRide.sends-sent));
   Ok(gc==0&&delta==0,"warm production pose retains zero bytes and triggers zero collections in doubles");
   Ok(MercRide.sends-sent<700,"pose heartbeat stays at or below 2 Hz");
   Geometry();
   NpcWar.Budget();
   Console.WriteLine("K3a pose/adapter simulation: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
  }
 }
}
'''


BRAIN = r'''
    static class MovingBrainCheck
    {
        static int pass, fail;
        static void Ok(bool value, string name) {
            Console.WriteLine((value ? "PASS " : "FAIL ") + name);
            if (value) pass++; else fail++;
        }
        static void Set(MercBrain b, string name, object value) {
            typeof(MercBrain).GetField(name, System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).SetValue(b, value);
        }
        static MercBrain Peek() {
            MercBrain b = new MercBrain(1);
            b.State = MercBrain.PeekOut; b.Since = 1f; b.PeekAt = new Vector3(6f,0f,0f);
            b.PeekSide = CoverPeek.Right;
            b.Cover = new CoverPick { Found=true, Confirmed=true,
                Point=new CoverPoint { Pos=Vector3.zero, Normal=new Vector3(0,0,-1),
                    Top=8, Depth=1, Left=5, Right=5, Peek=CoverPeek.Right } };
            Set(b,"_until",5f); Set(b,"_upSince",1f); Set(b,"_burstLen",.6f);
            return b;
        }
        static FightIn Input() {
            return new FightIn { Now=1.2f, Me=new Vector3(2,0,0), Count=1,
                Threats=new Vector3[]{new Vector3(6,0,100)}, Target=true, Sees=true,
                MayFight=true, Disengage=8, Health=1, Rounds=30, MaxRounds=30,
                SensedAt=1.2f, MoveShoot=true, Planted=false };
        }
        public static int Main() {
            World world=new World(); CoverField field=new CoverField(world,64);
            MercBrain b=Peek(); FightIn i=Input(); FightOut o;
            b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekOut,
                "continuous outward peek does not require planting");
            i.Now=1.4f; i.Me=new Vector3(4,0,0); b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekOut,
                "outward travel without a real round keeps moving aim instead of faking a burst");
            i.Now=1.5f; i.Me=b.PeekAt; i.MovingShots=1; b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekBack && o.Dest.sqrMagnitude==0,
                "actual outward shot turns directly into armed return without stationary Burst");
            i.Now=1.6f; i.Me=new Vector3(3,0,0); b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekBack,
                "return remains a continuous walking step");
            i.Now=1.7f; i.Me=new Vector3(.5f,0,0); b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Hold && o.Low && b.State==MercBrain.Hide,
                "arrival in M1 cover ducks and ends the exposure");
            b=Peek(); i=Input(); i.MoveShoot=false; i.Me=b.PeekAt; b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Fire && b.State==MercBrain.Burst,
                "switch off or missing gait retains original planted burst");
            b=Peek(); i=Input(); i.Now=5.1f; b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekBack && b.Blind==1,
                "blocked or empty moving peek returns within the existing deadline");
            b=Peek(); i=Input(); i.Reloading=true; b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Step && b.State==MercBrain.PeekBack,
                "native reload returns to shelter before the next burst");
            b=Peek(); i=Input(); i.Danger=true; i.DangerAt=i.Me; b.Think(ref i,field,out o);
            Ok(o.Act==FightAct.Run && b.State==MercBrain.Flee,
                "blast danger immediately overrides moving fire with escape");
            b=Peek(); i=Input(); i.HasOwner=true; i.Owner=new Vector3(4,0,50); b.Think(ref i,field,out o);
            Ok(o.NoShot,"owner in current moving lane still vetoes a round");
            Console.WriteLine("K3a production brain: "+pass+" passed, "+fail+" failed");
            return fail==0?0:1;
        }
    }
'''


def run_brain(out):
    # Reuse the M1 world and compile production M1/M2/M3 cores unchanged.
    import merc_fight_check as m2
    head = m2.harness_source().split(m2.SIM)[0]
    path = out / "brain.cs"
    path.write_text(head + BRAIN + "}\n", encoding="ascii")
    exe = out / "brain.exe"
    r = subprocess.run([str(CSC), "/nologo", "/warn:0", "/optimize+",
                        "/out:" + str(exe), str(path)], capture_output=True, text=True)
    if r.returncode:
        print(r.stdout, r.stderr)
        return 1
    r = subprocess.run([str(exe)], capture_output=True, text=True)
    print(r.stdout)
    return r.returncode


def main():
    build = ROOT / "build"
    build.mkdir(parents=True, exist_ok=True)
    # Use inherited workspace permissions; Python's Windows TEMP mode 0700
    # excludes the restricted sandbox token. Never change existing output ACLs.
    out = build / ("k3a-check-" + uuid.uuid4().hex)
    out.mkdir()
    gate = (ROOT / "Revival.MercCombatResponseCore.cs").read_text(encoding="utf-8")
    start = gate.index("internal static class MercFireGate")
    end = gate.index("    /// <summary>How far", start)
    support = out / "support.cs"
    support.write_text(SUPPORT + "\nnamespace NextDayRevival {\n" + gate[start:end] + "\n}\n", encoding="ascii")
    exe = out / "k3a.exe"
    sources = [ROOT / p for p in ("Revival.MercMoveShootCore.cs", "Revival.MercMoveShootPose.cs", "Revival.MercMoveShoot.cs")]
    result = subprocess.run([str(CSC), "/nologo", "/warn:0", "/optimize+", "/out:" + str(exe), str(support)]
                            + [str(p) for p in sources], capture_output=True, text=True, errors="replace")
    if result.returncode:
        print(result.stdout, result.stderr)
        return 1
    result = subprocess.run([str(exe)], capture_output=True, text=True)
    print(result.stdout)
    if result.returncode:
        print(result.stderr)
        return 1
    if run_brain(out):
        return 1
    data = json.loads((ROOT / "research/merc_moving_fire_evidence.json").read_text())
    assert data["source_commit"] == "e211c21ecd7be05fc976e01c66ceac3d6a622e84"
    assert data["player_sets"] == 4 and data["npc_sets"] == 8
    assert len(data["directional_gaits"]) == 12 and data["masked_rig_checks"] == 144
    assert data["legacy_only"] and data["clip_legacy"] and not data["unmatched_body_paths"]
    pose = sources[1].read_text()
    adapter = sources[2].read_text()
    fight = (ROOT / "Revival.MercFight.cs").read_text(encoding="utf-8")
    native = (ROOT / "Revival.NpcCombat.cs").read_text(encoding="utf-8")
    assert "MercWalkingPose(f, u, ft, ref act, now)" in fight and "if (walking) MercWalkingFire" in fight
    assert "moveState = walking ? MainWalk : MainRun" in fight and "if (walking) want = Mathf.Min" in fight
    assert "Shoot(f)" in adapter and "Drive(" not in adapter and "OrderMove(" not in adapter
    assert "Clear(from, aimAt, f.Target)" in native and "MercFriendInLine(f, from, aimAt)" in native
    assert "MercMoveShootPose.OnPacket(d, sender)" in (ROOT / "Revival.MercsRide.cs").read_text(encoding="utf-8")
    assert "MercMoveShoot.BindConfig(cfg)" in (ROOT / "Revival.Mercs.cs").read_text(encoding="utf-8")
    assert '"MoveShoot", true' in adapter and "DefaultExecutionOrder(100)" in pose
    assert "ft.MovePose.Supports(f.WeaponId)" in fight and "ft.In.MovingShots = ft.MovingShots" in fight
    assert "Physics.OverlapSphereNonAlloc" in adapter and "QueryTriggerInteraction.Ignore" in adapter
    assert "Physics.RaycastNonAlloc" in adapter and "Hits, distance, -1" in adapter
    assert "S_MercMoveShoot_Fire" in adapter
    prof = (ROOT / "RevivalFrameProfiler.cs").read_text()
    slots = re.findall(r"public const int (\w+) = (\d+);", prof)
    assert len({value for name, value in slots if name.startswith("S_")}) == len([name for name, value in slots if name.startswith("S_")])
    # slot numbers move when branches are composed; check the names behind the constants
    names = prof.split('static readonly string[] Names = new string[]')[1].split('};')[0]
    listed = re.findall(r'"([^"]+)"', names)
    assert len(listed) == int(dict(slots)["Count"])
    at = dict(slots)
    assert listed[int(at["S_MercMoveShootPose_Update"])].strip() == "MercMoveShootPose.Update"
    assert "S_MercMoveShoot_Fire" in at and "S_MercPositionT" in at
    manifest = (ROOT / "sync_public.py").read_text(encoding="utf-8")
    assert all(p.name in manifest for p in sources)
    assert all(p.read_bytes().isascii() for p in sources)
    print("PASS K0 shared-rig evidence, 12 directional gaits, continuous brain/adapter wiring, owner transport, all-collider geometry, ON config and unique F6 slots")
    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())

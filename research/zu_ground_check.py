"""Compile production ZU ground policy/control against deterministic scene doubles.

No Unity, game, sockets or TEMP dependency. Physics doubles test all collider
classes through the same ray interface, including self exclusions and triggers.
Production ground/air controllers, air search, settlement registry, selection,
wire and crew priority are compiled unchanged against scene doubles.
"""
from pathlib import Path
import os
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using NextDayRevival;
namespace BepInEx.Configuration {
 public class ConfigEntry<T> {public T Value;}
 public class ConfigFile {public ConfigEntry<T> Bind<T>(string section,string key,T value,string description){return new ConfigEntry<T>{Value=value};}}
}
namespace HarmonyLib {
 public class Harmony {public void Patch(System.Reflection.MethodInfo m,HarmonyMethod a,HarmonyMethod b,object c,object d,object e){}}
 public class HarmonyMethod {public HarmonyMethod(System.Reflection.MethodInfo m){}}
 public static class AccessTools {public static System.Reflection.MethodInfo DeclaredMethod(Type t,string name,Type[] args,object extra){return t.GetMethod(name);}}
}
namespace UnityEngine {
 public class Object {static int next;readonly int id=++next;public int GetInstanceID(){return id;}
  public static int Scans;public static Object[] Seed=new Object[0];public static Object[] FindObjectsOfType(Type t){Scans++;return Seed;}}
 public struct Vector2 {public float x,y;public Vector2(float a,float b){x=a;y=b;}public static Vector2 operator *(Vector2 v,float f){return new Vector2(v.x*f,v.y*f);}}
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get{return new Vector3();}} public static Vector3 up {get{return new Vector3(0,1,0);}}
  public static Vector3 right {get{return new Vector3(1,0,0);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}} public float magnitude {get{return (float)Math.Sqrt(sqrMagnitude);}}
  public Vector3 normalized {get{return magnitude>0?this/magnitude:zero;}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float s){return new Vector3(a.x*s,a.y*s,a.z*s);}
  public static Vector3 operator /(Vector3 a,float s){return a*(1/s);}
  public static float Distance(Vector3 a,Vector3 b){return (a-b).magnitude;}
  public static Vector3 Cross(Vector3 a,Vector3 b){return new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
  public void Normalize(){this=normalized;}
  public static float Angle(Vector3 a,Vector3 b){return (float)(Math.Acos(Math.Max(-1,Math.Min(1,(a.x*b.x+a.y*b.y+a.z*b.z)/(a.magnitude*b.magnitude))))*180/Math.PI);}
 }
 public class Transform : Object {public Vector3 position,forward=new Vector3(0,0,1);public Transform parent;public GameObject gameObject;
  public bool IsChildOf(Transform t){for(Transform v=this;v!=null;v=v.parent)if(v==t)return true;return false;}}
 public class GameObject : Object {public Transform transform;public Component Npc;public bool activeInHierarchy=true,Dead,White;public int Side=1,Actor=7;
  string label="Native";public static int NameReads;public string name {get{NameReads++;return new string(label.ToCharArray());}set{label=value;}}
  public GameObject(){transform=new Transform();transform.gameObject=this;}
  public Component GetComponentInParent(Type type){for(Transform t=transform;t!=null;t=t.parent)if(t.gameObject.Npc!=null)return t.gameObject.Npc;return null;}}
 public class Component : Object {public GameObject gameObject=new GameObject();public Component(){gameObject.Npc=this;}public Transform transform {get{return gameObject.transform;}}
  public bool Alive=true,Hidden,Safe,God,Down,Talk,Remote,Hurtable=true;public int Side=1;}
 public class Collider {public Transform transform;public Vector3 Lo,Hi;public bool Trigger;}
 public struct RaycastHit {public Transform transform;public Vector3 point;public float distance;}
 public enum QueryTriggerInteraction {Ignore}
 public static class Physics {
  public const int DefaultRaycastLayers=-5;public static readonly List<Collider> All=new List<Collider>();public static int Calls;
  public static bool Raycast(Vector3 from,Vector3 dir,out RaycastHit hit,float length,int mask,QueryTriggerInteraction ignore){
   Calls++;hit=new RaycastHit();float nearest=length+1;Collider best=null;
   for(int i=0;i<All.Count;i++){Collider c=All[i];if(c.Trigger)continue;
    // Unity raycasts do not report a collider containing the ray origin.
    if(from.x>=c.Lo.x&&from.x<=c.Hi.x&&from.y>=c.Lo.y&&from.y<=c.Hi.y&&from.z>=c.Lo.z&&from.z<=c.Hi.z)continue;
    float lo=0,hi=length;bool ok=true;
    for(int axis=0;axis<3;axis++){float p=axis==0?from.x:axis==1?from.y:from.z,d=axis==0?dir.x:axis==1?dir.y:dir.z;
     float mn=axis==0?c.Lo.x:axis==1?c.Lo.y:c.Lo.z,mx=axis==0?c.Hi.x:axis==1?c.Hi.y:c.Hi.z;
     if(Math.Abs(d)<1e-8){if(p<mn||p>mx){ok=false;break;}}
     else{float a=(mn-p)/d,b=(mx-p)/d;if(a>b){float temp=a;a=b;b=temp;}lo=Math.Max(lo,a);hi=Math.Min(hi,b);if(lo>hi){ok=false;break;}}}
    if(ok&&lo<nearest){nearest=lo;best=c;}}
   if(best==null)return false;hit.transform=best.transform;hit.distance=nearest;hit.point=from+dir*nearest;return true;
  }
 }
 public static class Mathf {public static int RoundToInt(float f){return (int)Math.Round(f);}public static float Min(float a,float b){return Math.Min(a,b);}
  public static float Clamp(float f,float a,float b){return Math.Max(a,Math.Min(f,b));}}
 public static class Time {public static float time;}
 public static class Random {public static Vector3 onUnitSphere {get{return Vector3.zero;}}public static Vector2 insideUnitCircle {get{return new Vector2();}}}
}
namespace NextDayRevival {
 public class NativeSettlement : Component {public void OnEnable(){}public void OnDestroy(){}}
 public class Logger {public void LogInfo(string s){}public void LogWarning(string s){}}
 public static class RevivalPlugin {public static Logger L=new Logger();public static Type TypeByName(string name){return name=="NPC_Settlement"?typeof(NativeSettlement):null;}}
 internal static class MapScene {public static bool AtHome;}
 internal static class MilitaryTown {public static Vector3 Centre;public const float MapRingRadius=380f;
  public static bool Inside(Vector3 p,float margin){return Math.Abs(p.x-Centre.x)<=300+margin&&Math.Abs(p.z-Centre.z)<=300+margin;}}
 internal static class NewSettlement {public static bool Present;public static Vector3 At;public static bool Here(){return Present;}
  public static Vector3 Centre(){return At;}public static float MapRingRadius(){return 200;}}
 internal static class MapTools {public static GameObject LocalPlayer(){return null;}}
 internal static class GepardCrew {public static List<GameObject> Spieler(){return Crocodile.Live;}}
 internal static class NoFly {public static bool TownContains(Vector3 p){return true;}}
 internal static class RadarShadow {public static bool Visible(GameObject go){return true;}}
 internal static class AirKills {public static int NextShotCredit;}
 internal static class GepardFx {public static void Muzzle(Vector3 p,Vector3 dir){}}
 internal static class VehicleShotSound {public static void Play(Vector3 p,bool heavy){}}
 internal static class GepardShots {public class Spec {public bool Tracer,PuffFx,Flak;public float Speed,Gravity,Dispersion,Fuze,Splash,CollisionSeconds,InfantryDamage,PuffScale;public int HeliHits;}
  public static void Fire(Spec s,Transform owner,Vector3 p,Vector3 dir,float life,bool live,List<GepardGun.Contact> contacts){}
  public static void FireExact(Spec s,Transform owner,Vector3 p,Vector3 velocity,float life,bool live,List<GepardGun.Contact> contacts,float age,float gravity){}}
 internal enum FlakMode {WeaponsFree,HoldFire,AssignedOnly,ZoneDefence}
 internal class MercAAPost {public int Actor=3,View=9,Trait;public bool Peaceful;public Component Ai=new Component();}
 internal class MercUnit {public int AAView=9;public Fight Fight=new Fight();}
 internal class Fight {public MercCrewPhase Crew=new MercCrewPhase();public Component CrewThreat;}
 internal static class MercAA {public static bool Authority=true;public static MercAAPost Post,Operator;public static bool Radar;
  public static MercAAPost Gun(int i){return i==5?Post:null;}public static MercAAPost Held(int i){return Gun(i);}
  public static bool Directed(Flak.Gun g){return Radar;}public static bool DirectionAvailable(Flak.Gun g){return Radar;}}
 internal static class Mercs {public const int LocalActor=3;public static int ActorOf(GameObject c){return c.Actor;}
  public static bool PlayerDead(GameObject c){return c.Dead;}public static string SteamOf(GameObject c){return c.White?"white":null;}
  public static bool Whitelisted(string c){return c=="white";}public static int FactionOf(GameObject c){return c.Side;}}
 internal static class FlakNet {public static int Sends;public static void SendPacket(float[] p,bool reliable){Sends++;}
  public static void SendShot(Flak.Gun g,Vector3 p,Vector3 dir,float life){}}
 internal class GunAmmo {public bool Limited;}
 internal static class FlakAmmo {public static bool Ready(Flak.Gun g){return true;}public static int ShotActor(Flak.Gun g){return 3;}}
 internal static class Flak {
  public const float K=2.8f;public static Gun Current,_manned;public static int Shots,Publishes,Reloads,AirShots,GroundShots;
  public static GameObject LastShot;public const float ZoneMetres=4500,MaxFuze=20000;
  public static bool CfgTracers,CfgPuffs,CfgMuzzleFlash,CfgGunSound;
  public class Gun {public int Index=5,Rounds=100;public bool ShortRange=true,Engaged,Firing,Reloading;public float Held,FuzeRange;
   public float WantYaw,WantPitch;public Vector3 Aim;public Transform Root=new GameObject().transform,Cradle=new GameObject().transform,Muzzle=new GameObject().transform,Owner;
   public Component Gunner,Loader;public readonly List<GepardGun.Contact> Air=new List<GepardGun.Contact>(),Hostile=new List<GepardGun.Contact>();
   public GepardGun.Contact Target,Cue;public ShortBurst ShortBurst;public FlakMode Mode;public GameObject Assigned,CueGo,LostGo;
   public Vector3 ZoneCentre,Err,LastVel;public float ZoneRadius,NextLook,NextWake,CueHeld,LastSight,LastContact,Recoil,LastShot;
   public bool Awake,Laying,DrySaid,Town;public int Seq;public Transform MuzzleRight;public GunAmmo Ammo=new GunAmmo();}
  public static Gun ByIndex(int i){return Current!=null&&Current.Index==i?Current:null;}
  public static bool Up(Component c){return c!=null&&c.Alive&&!c.Down;}
  public static Vector3 Mid(Gun g){return g.Cradle.position;}
  public static void Angles(Gun g,Vector3 d,out float yaw,out float pitch){yaw=(float)(Math.Atan2(d.x,d.z)*180/Math.PI);pitch=(float)(Math.Atan2(d.y,Math.Sqrt(d.x*d.x+d.z*d.z))*180/Math.PI);}
  public static void RaiseEngaging(Gun g,GameObject c){}
  public static bool B2(bool b){return b;}public static string OwnerFaction(Gun g){return "military";}
  public static float ReachU(Gun g,Vector3 p,float reach){return reach*K;}public static int TargetCover(Gun g,GameObject go){return 0;}
  public static void StartReload(Gun g,float seconds){Reloads++;g.Reloading=true;}
  public static void Fire(Gun g,Vector3 aim,bool valid,float fuze,List<GepardGun.Contact> contacts,bool player){Shots++;g.Rounds--;LastShot=g.Target==null?null:g.Target.Go;if(g.Target==null)GroundShots++;else AirShots++;}
  public static void Publish(Gun g,bool player,bool now){Publishes++;}
 }
 internal static class GepardGun {public class Contact {public GameObject Go=new GameObject();public Vector3 Pos,Vel;public int Kind;public bool Hostile=true,Visible=true;}}
 internal static partial class FlakFire {public static readonly List<GepardGun.Contact> Contacts=new List<GepardGun.Contact>();
  public static bool Crewman(Flak.Gun g,Transform tr){return g.Gunner!=null&&tr.IsChildOf(g.Gunner.transform);}
  public static void Release(Flak.Gun g){g.Target=null;g.Engaged=g.Firing=false;}
  public static void FollowAll(List<GepardGun.Contact> contacts,float dt,float lag){for(int i=0;i<contacts.Count;i++)contacts[i].Pos=contacts[i].Go.transform.position;}
  public static bool Cue(Flak.Gun g,float dt){return false;}public static void Dry(Flak.Gun g){}
  public static Vector3 Offset(GepardGun.Contact c,Vector3 from,float error){return Vector3.right*error;}
  static void Collect(List<GepardGun.Contact> contacts,Vector3 eye,float range){contacts.Clear();for(int i=0;i<Contacts.Count;i++){
   GepardGun.Contact c=Contacts[i];c.Pos=c.Go.transform.position;if(Vector3.Distance(eye,c.Pos)<=range)contacts.Add(c);}}
  static void Allegiance(GepardGun.Contact c,string side,out bool hostile,out bool friendly){hostile=c.Hostile;friendly=!hostile;}
  static bool InZone(Flak.Gun g,Vector3 p){return (p-g.ZoneCentre).sqrMagnitude<=g.ZoneRadius*g.ZoneRadius;}
  static bool Airborne(GepardGun.Contact c){return Airborne(c,10*Flak.K);}static bool Airborne(GepardGun.Contact c,float floor){return c.Pos.y>floor;}
  static bool Sight(Flak.Gun g,Vector3 from,GepardGun.Contact c){return c.Visible;}static int Threat(GepardGun.Contact c){return 0;}
  static float Flak_Ceiling(){return 3000*Flak.K;}}
 internal static class FrameProf {public const int S_ZuGround=190;public static int Starts,Ends;public static void S(int i){Starts++;}public static void E(int i){Ends++;}}
 internal static class AirDefenceDamage {public static bool Alive(int i){return true;}}
 internal static class MercRide {public static bool HiddenRider(Component c){return c.Hidden;}}
 internal static class Crocodile {public static readonly List<GameObject> Live=new List<GameObject>();public static List<GameObject> Players(){return Live;}}
 public static partial class NpcWar {
  internal class Squad {public object Merc;}
  internal class Fighter {public Component Ai;public Transform Tr;public Squad Squad;public int[] Hated={1};public Transform Target;
   public bool TargetIsPlayer;public float AimHeight,LastSeen,NextLos;}
  static readonly List<Component> _scene=new List<Component>();static readonly object[] Sides={0,1,2};static readonly int[] Hate={1};
  static readonly Type _npcType=typeof(Component);
  public static List<Component> Scene {get{return _scene;}}
  static bool LookUp(){return true;}static Array GetHated(Component c){return Hate;}
  public static int RemoteCalls;static bool IsMine(Component c){return !c.Remote;}static void BreakKillStreak(Component c){}
  static bool RemoteHit(Component c,float damage,Vector3 point){RemoteCalls++;return true;}
  static bool Alive(Component c){return c.Alive;}static bool Hurtable(Component c){return c.Hurtable;}
  static bool GroundDowned(Component c){return c.Down;}static object FactionOf(Component c){return Sides[c.Side];}
  static bool Hostile(Array a,object b){return b!=null&&HatedValue(a,(int)b);}static bool HatedValue(Array a,int b){return a!=null&&Array.IndexOf((int[])a,b)>=0;}
  static bool Bool(Component c,string field){return field=="GodModeEnabled"?c.God:field=="_isSafeSettlement"?c.Safe:c.Talk;}
  static Fighter FighterOf(Component c){return null;}static bool Targetable(Component c){return c.Hurtable&&!c.Safe&&!c.God;}
  static float RangeOf(Fighter f){return 300*2.8f;}static bool AimPoint(Fighter f,Transform t,out float height){height=2.8f;return !BlockedSight;}
  public static bool BlockedSight;static Component MercPlayerTarget(Fighter f,float range,float now){return null;}
  internal static bool Priority(Fighter f,float now){return ZuCrewPriority(f,now);}
  internal static bool Threat(Fighter f,MercUnit u,Flak.Gun g,float now){return ZuCrewThreat(f,u,g,now);}
 }
}
class Check {
 static int count,fail;static void Ok(bool b,string name){count++;if(!b){fail++;Console.WriteLine("FAIL "+name);}}
 static Component Enemy(float metres){Component c=new Component();c.transform.position=new Vector3(0,0,metres*2.8f);return c;}
 static Flak.Gun Gun(){Flak.Gun g=new Flak.Gun();g.Cradle.position=new Vector3(0,1.8f*2.8f,0);g.Muzzle.position=g.Cradle.position;g.Owner=g.Root;
  g.Gunner=new Component();g.Gunner.Side=2;Flak.Current=g;return g;}
 static void Box(Transform tr,float z,bool trigger){Physics.All.Add(new Collider{transform=tr,Lo=new Vector3(-10,0,z),Hi=new Vector3(10,10,z+1),Trigger=trigger});}
 static int Main(){
  for(int m=0;m<=1100;m++){bool expected=m>=25&&m<=300;Ok(ZuGroundCore.Envelope(0,0,m*2.8f)==expected,"range "+m);}
  Ok(!ZuGroundCore.Envelope(0,-30,280),"depression dead zone");Ok(!ZuGroundCore.Envelope(0,80,280),"ground ceiling angle");
  Ok(ZuGroundCore.Rifle(false,true,true),"close assault dismounts despite inbound air");
  Ok(!ZuGroundCore.Rifle(true,true,false)&&ZuGroundCore.Rifle(true,false,false),"far gun / unreachable rifle");
  Ok(!ZuGroundCore.GroundAllowed(1,false)&&!ZuGroundCore.GroundAllowed(0,true)&&!ZuGroundCore.GroundAllowed(2,true)
   &&ZuGroundCore.GroundAllowed(2,false),"aircraft priority also applies to explicit GROUND duty");
  Flak.Gun g=Gun();Component enemy=Enemy(100);NpcWar.Scene.Add(enemy);ZuGround.State state=new ZuGround.State();
  Ok(!ZuGround.Reachable(g,enemy.transform.position),"missing settlement hooks fail closed for ground only");
  NativeSettlement seeded=new NativeSettlement();seeded.transform.position=new Vector3(10000,0,0);
  UnityEngine.Object.Seed=new UnityEngine.Object[]{seeded};SettlementScan.Registry.Install(new HarmonyLib.Harmony());UnityEngine.Object.Seed=new UnityEngine.Object[0];
  Ok(SettlementScan.Registry.NativeSettlementNear(seeded.transform.position,340),"seeded native settlement cached");SceneRegistry.RemoveHook(seeded);
  NativeSettlement town=new NativeSettlement();town.transform.position=enemy.transform.position;town.gameObject.activeInHierarchy=false;SceneRegistry.AddHook(town);
  Ok(!ZuGround.Reachable(g,enemy.transform.position),"inactive native settlement excludes ground engagement");
  int names=GameObject.NameReads,scans=UnityEngine.Object.Scans;
  for(int i=0;i<1000;i++)SettlementScan.Registry.NativeSettlementNear(enemy.transform.position,340);
  Ok(GameObject.NameReads==names&&UnityEngine.Object.Scans==scans,"settlement query never reads Unity names or scans scene");
  SceneRegistry.RemoveHook(town);Ok(ZuGround.Reachable(g,enemy.transform.position),"removed native settlement no longer blocks");
  town.gameObject.name="NDR_Airfield";SceneRegistry.AddHook(town);Ok(ZuGround.Reachable(g,enemy.transform.position),"airfield combat pocket is not a native town");SceneRegistry.RemoveHook(town);
  MilitaryTown.Centre=enemy.transform.position;MapScene.AtHome=true;Ok(!ZuGround.Reachable(g,enemy.transform.position),"military town excludes ground even inside 300 m");MapScene.AtHome=false;
  NewSettlement.Present=true;NewSettlement.At=enemy.transform.position;Ok(!ZuGround.Reachable(g,enemy.transform.position),"new settlement excludes ground");NewSettlement.Present=false;
  NpcWar.ZuSelect(g,state);Ok(state.Target==enemy.transform,"master selects hostile");
  enemy.Safe=true;NpcWar.ZuSelect(g,state);Ok(state.Target==null,"safe exclusion");enemy.Safe=false;
  enemy.Hidden=true;NpcWar.ZuSelect(g,state);Ok(state.Target==null,"hidden rider exclusion");enemy.Hidden=false;
  enemy.Down=true;NpcWar.ZuSelect(g,state);Ok(state.Target==null,"wounded exclusion");enemy.Down=false;
  enemy.Side=2;NpcWar.ZuSelect(g,state);Ok(state.Target==null,"friendly exclusion");enemy.Side=1;
  enemy.transform.position=new Vector3(0,0,24*2.8f);NpcWar.ZuSelect(g,state);Ok(state.Target==null,"near target never gun engaged");enemy.transform.position=new Vector3(0,0,280);
  GameObject player=new GameObject();player.transform.position=new Vector3(0,0,140);Crocodile.Live.Add(player);
  NpcWar.ZuSelect(g,state);Ok(state.Target==player.transform,"hostile player");player.White=true;NpcWar.ZuSelect(g,state);Ok(state.Target==enemy.transform,"whitelist respected");Crocodile.Live.Clear();
  g.Mode=FlakMode.AssignedOnly;NpcWar.ZuSelect(g,state);Ok(state.Target==null,"no autonomous ground fire on assigned-only");
  g.Assigned=enemy.gameObject;NpcWar.ZuSelect(g,state);Ok(state.Target==enemy.transform,"explicit ground assignment");g.Mode=FlakMode.WeaponsFree;
  Component far=Enemy(200);NpcWar.Scene.Add(far);g.Mode=FlakMode.AssignedOnly;g.Assigned=far.gameObject;
  NpcWar.ZuSelect(g,state);Ok(state.Target==far.transform,"assigned far target beats unassigned near target");g.Mode=FlakMode.WeaponsFree;
  state.Blocked=enemy.transform;state.BlockedUntil=2;NpcWar.ZuSelect(g,state);Ok(state.Target==far.transform,"masked target yields to another candidate");NpcWar.Scene.Remove(far);
  Vector3 point=enemy.transform.position+Vector3.up*2.8f;Ok(ZuGround.Lane(g,enemy.transform,point),"open lane");
  string[] kinds={"prop","concrete slab","fence","earth wall","terrain"};
  foreach(string name in kinds){Physics.All.Clear();Box(new GameObject().transform,80,false);Ok(!ZuGround.Lane(g,enemy.transform,point),name+" blocks");}
  Physics.All.Clear();Box(new GameObject().transform,80,true);Ok(ZuGround.Lane(g,enemy.transform,point),"triggers ignored");
  Physics.All.Clear();Box(g.Root,80,false);Ok(ZuGround.Lane(g,enemy.transform,point),"own carriage skipped");
  Physics.All.Clear();Box(enemy.transform,270,false);Ok(ZuGround.Lane(g,enemy.transform,point),"target collider accepted");Physics.All.Clear();
  NpcWar.Fighter fighter=new NpcWar.Fighter{Ai=g.Gunner,Tr=g.Gunner.transform,Squad=new NpcWar.Squad()};MercUnit u=new MercUnit();
  Ok(NpcWar.Threat(fighter,u,g,0)&&u.Fight.CrewThreat==enemy,"owner retains far threat");Component close=Enemy(10);NpcWar.Scene.Add(close);
  Ok(NpcWar.Threat(fighter,u,g,.25f)&&u.Fight.CrewThreat==close,"close attacker beats remembered far target");NpcWar.Scene.Remove(close);
  g.Target=new GepardGun.Contact();Time.time=1;Ok(!ZuGround.Control(g,.0167f,true,false)&&Flak.Shots==0,"AUTO air wins over ground");
  MercAA.Post=new MercAAPost();MercAA.Post.Ai.Side=2;float[] packet={13,5,9,2,0,0,0};
  ZuGround.OnPacket(packet,7);Ok(ZuGround.Duty(g)==0,"foreign actor rejected");packet[2]=8;ZuGround.OnPacket(packet,3);Ok(ZuGround.Duty(g)==0,"foreign view rejected");
  packet[2]=9;packet[3]=float.NaN;ZuGround.OnPacket(packet,3);Ok(ZuGround.Duty(g)==0,"NaN rejected");packet[3]=2;
  ZuGround.OnPacket(packet,3);Ok(ZuGround.Duty(g)==2,"owner Ground accepted");
  Ok(!ZuGround.Control(g,.0167f,true,false),"GROUND duty yields to aircraft");Time.time=3;Ok(ZuGround.Duty(g)==0,"expired duty falls back AUTO");
  enemy.Remote=true;Ok(ZuGround.RemoteImpact(enemy.gameObject,90,enemy.transform.position)&&NpcWar.RemoteCalls==1,"master ground hit routes to remote NPC owner");
  enemy.Remote=false;Ok(!ZuGround.RemoteImpact(enemy.gameObject,90,enemy.transform.position),"local NPC uses direct native damage");
  Component manual=Enemy(150);manual.Remote=true;Ok(ZuGround.RemoteImpact(manual.gameObject,90,manual.transform.position)&&NpcWar.RemoteCalls==2,"manual player shot resolves remote owner without air/ground target list");
  g.Target=null;MercAA.Post=null;g=Gun();Flak.Shots=0;int maxRays=0;
  for(int frame=0;frame<1200;frame++){Time.time=10+frame/60f;int rays=Physics.Calls;ZuGround.Control(g,1f/60f,true,false);maxRays=Math.Max(maxRays,Physics.Calls-rays);}
  Ok(Flak.Shots==100&&g.Rounds==0&&g.Reloading,"ground consumes finite 100-round magazine");Ok(maxRays<=8,"clear lane scan plus shot check bounded to eight rays/frame");
  Ok(Flak.Reloads>0,"empty gun reloads via existing gun path");
  NpcWar.Fighter attacker=new NpcWar.Fighter{Ai=new Component(),Tr=new GameObject().transform,Squad=new NpcWar.Squad(),Hated=new int[]{2}};attacker.Tr.position=new Vector3(0,0,100);
  Ok(NpcWar.Priority(attacker,Time.time)&&attacker.Target==g.Gunner.transform,"open gunner first hostile target");
  NpcWar.BlockedSight=true;Ok(!NpcWar.Priority(attacker,Time.time),"crew priority requires visibility");NpcWar.BlockedSight=false;
  attacker.Squad.Merc=new object();Ok(!NpcWar.Priority(attacker,Time.time),"player merc orders not overridden");
  // Scan-to-shot races: each controller retains the actual target between scans.
  g=Gun();Time.time=35;ZuGround.Control(g,.01f,true,false);g.Held=1;int shots=Flak.Shots;
  enemy.transform.position=new Vector3(0,0,1000*Flak.K);Time.time=35.01f;ShortRange.Control(g,.0167f);
  Ok(!g.Engaged&&!g.Firing&&Flak.Shots==shots,"cached ground target moving to 1 km rejected before next scan");enemy.transform.position=new Vector3(0,0,100*Flak.K);
  g=Gun();Time.time=36;ZuGround.Control(g,.01f,true,false);g.Held=1;shots=Flak.Shots;
  Box(new GameObject().transform,80,false);Time.time=36.01f;ShortRange.Control(g,.0167f);
  Ok(!g.Firing&&Flak.Shots==shots,"new LOS blocker rejects round before next scan");Physics.All.Clear();
  g=Gun();Time.time=37;ZuGround.Control(g,.01f,true,false);g.Held=1;shots=Flak.Shots;
  town.gameObject.name="Native";town.transform.position=enemy.transform.position;SceneRegistry.AddHook(town);Time.time=37.01f;ShortRange.Control(g,.0167f);
  Ok(!g.Firing&&Flak.Shots==shots,"new settlement registration rejects cached ground target");SceneRegistry.RemoveHook(town);
  g=Gun();Time.time=38;ZuGround.Control(g,.01f,true,false);enemy.transform.position=new Vector3(0,0,299*Flak.K);
  Time.time=38.5f;ZuGround.Control(g,.01f,true,false);g.Held=1;shots=Flak.Shots;Time.time=38.51f;ShortRange.Control(g,.0167f);
  Ok(!g.Firing&&Flak.Shots==shots,"led impact outside 300 m rejected");enemy.transform.position=new Vector3(0,0,100*Flak.K);
  // Compile and exercise real ShortRange.Control and real FlakFire.Search.
  NpcWar.Scene.Clear();Component kilometre=Enemy(1000);NpcWar.Scene.Add(kilometre);g=Gun();shots=Flak.Shots;
  for(int i=0;i<600;i++){Time.time=50+i/60f;ShortRange.Control(g,1f/60f);}
  Ok(!g.Engaged&&!g.Firing&&Flak.Shots==shots,"1 km ground target never selected or fired at by AA controller");
  int[] kindsAir={0,6,2,3};
  foreach(int kind in kindsAir){g=Gun();MercAA.Post=new MercAAPost();MercAA.Post.Ai.Side=2;
   GepardGun.Contact aircraft=new GepardGun.Contact();aircraft.Kind=kind;aircraft.Go.transform.position=new Vector3(0,100*Flak.K,1500*Flak.K);
   FlakFire.Contacts.Add(aircraft);float[] groundDuty={13,5,9,2,0,0,0};Time.time=70;ZuGround.OnPacket(groundDuty,3);
   shots=Flak.Shots;for(int frame=0;frame<180;frame++){Time.time=70+frame/60f;if(g.Target!=null)g.Cradle.forward=g.Aim-g.Cradle.position;ShortRange.Control(g,1f/60f);}
   Ok(g.Target==aircraft&&g.Engaged&&Flak.Shots>shots&&Flak.LastShot==aircraft.Go,"1.5 km aircraft fired at, kind "+kind);
   FlakFire.Contacts.Clear();}
  // Even a visible, nearer ground attacker must yield to a hostile aircraft.
  NpcWar.Scene.Add(enemy);g=Gun();GepardGun.Contact inbound=new GepardGun.Contact();inbound.Go.transform.position=new Vector3(0,280,4200);FlakFire.Contacts.Add(inbound);
  int groundShots=Flak.GroundShots,airShots=Flak.AirShots;
  for(int frame=0;frame<180;frame++){Time.time=80+frame/60f;if(g.Target!=null)g.Cradle.forward=g.Aim-g.Cradle.position;ShortRange.Control(g,1f/60f);}
  Ok(Flak.GroundShots==groundShots&&Flak.AirShots>airShots,"near ground attacker cannot suppress aircraft duty");
  FlakFire.Contacts.Clear();MercAA.Post=null;NpcWar.Scene.Clear();NpcWar.Scene.Add(enemy);g=Gun();g.Rounds=0;g.Reloading=true;
  town.transform.position=new Vector3(10000,0,0);SceneRegistry.AddHook(town);
  for(int i=0;i<10000;i++){Time.time=40+i/60f;ZuGround.Control(g,1f/60f,true,false);}
  Stopwatch sw=new Stopwatch();GC.Collect();long bytes=GC.GetTotalMemory(false);int gc=GC.CollectionCount(0);sw.Start();
  for(int i=0;i<200000;i++){Time.time=1000+i/60f;ZuGround.Control(g,1f/60f,true,false);}
  sw.Stop();long growth=GC.GetTotalMemory(false)-bytes;Console.WriteLine("production ground controller: {0:F6} ms/frame, heap delta {1}, Gen0 delta {2}",sw.Elapsed.TotalMilliseconds/200000,growth,GC.CollectionCount(0)-gc);
  Ok(growth==0&&GC.CollectionCount(0)==gc,"steady controller with native settlement zero allocations");Ok(FrameProf.Starts==FrameProf.Ends,"balanced profiler exits");
  Console.WriteLine("{0}: {1} production ZU assertions; 1 km ground rejected; 1.5 km aircraft fired; 100 rounds exhausted; max {2} clear-lane rays/frame",fail==0?"PASS":"FAIL",count,maxRays);return fail==0?0:1;
 }
}
'''


def main():
    out = ROOT / 'build' / 'zu_ground_check'
    out.mkdir(parents=True, exist_ok=True)
    harness = out / 'Harness.cs'
    harness.write_text(HARNESS, encoding='ascii')
    exe = out / 'Check.exe'
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    sources = ['Revival.ZuGround.cs', 'Revival.ZuGroundCore.cs', 'Revival.ShortRange.cs',
               'Revival.VehicleScan.cs', 'Revival.MercCrewPhasesCore.cs',
               'Revival.ShortRangeCore.cs', 'Revival.MercAACore.cs',
               'Revival.AARaidBalanceCore.cs', 'Revival.FlakEngageCore.cs']
    flak_source = (ROOT / 'Revival.Flak.cs').read_text(encoding='utf-8')
    start = flak_source.index('        internal static void Search(Flak.Gun g)')
    end = flak_source.index('        static float Flak_Ceiling()', start)
    search = out / 'FlakSearch.cs'
    search.write_text('using System; using UnityEngine; namespace NextDayRevival { '
                      'internal static partial class FlakFire {\n' + flak_source[start:end]
                      + '\n}}', encoding='ascii')
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe),
                            str(harness), str(search)] + [str(ROOT / p) for p in sources],
                           capture_output=True, text=True, encoding='utf-8', errors='replace')
    if built.returncode:
        print(built.stdout + built.stderr)
        return 1
    run = subprocess.run([str(exe)], capture_output=True, text=True,
                         encoding='utf-8', errors='replace')
    print(run.stdout + run.stderr, end='')
    if run.returncode:
        return run.returncode
    read = lambda p: (ROOT / p).read_text(encoding='utf-8')
    ground, short, flak, crew = map(read, ('Revival.ZuGround.cs', 'Revival.ShortRange.cs',
                                         'Revival.Flak.cs', 'Revival.MercCrewPhases.cs'))
    assert 'ZuGround.Control(g, dt, gunner, loader)' in short
    assert 's[i].InfantryDamage = ZuGroundCore.Damage' in short
    assert 'spec.HeliHits, spec.InfantryDamage)' in read('RevivalGepard.cs')
    assert 'r.Npc ?? EmptyContacts' in read('RevivalGepard.cs')
    assert 'ZuGround.OnPacket(f, sender)' in flak and 'merc.Actor != sender || merc.View != view' in ground
    assert 'ZuGround.OwnerMode(g, u, phase.Override)' in crew and 'ZuGroundCore.Rifle' in crew
    assert 'else Flak.Fire(g, aim, true, g.FuzeRange, g.Hostile, false)' in ground
    assert 'ZuCrewPriority(f, now)' in read('Revival.NpcCombat.cs')
    assert 'Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore' in ground
    assert all(s not in ground for s in ('FindObjects', 'GetComponents', 'Overlap', '=>'))
    assert 's.NextScan = Time.time + 0.5f' in ground and 'n < 32' in ground
    assert 'ShortRange.Awake(g, master)' in flak
    assert 'Mathf.Clamp(pitch, ZuGroundCore.MinPitch, 90f)' in short
    assert 'pitch >= ZuGroundCore.MinPitch' in ground
    assert 'g.ShortRange ? ZuGroundCore.MinPitch' in flak
    prof = read('RevivalFrameProfiler.cs')
    slots = re.findall(r'public const int S_\w+ = (\d+);', prof)
    assert len(slots) == len(set(slots)) and 'ZuGround.Control.Sub' in prof
    for source in ('Revival.ZuGround.cs', 'Revival.ZuGroundCore.cs'):
        assert (ROOT / source).read_bytes().isascii() and source in read('sync_public.py')
    print('PASS wiring: master-only tick, owner/view lease validation, shared ammo and Photon shots, calibre damage, all colliders, sleep gate, 2 Hz/32-entry budget, F6 slot 190')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

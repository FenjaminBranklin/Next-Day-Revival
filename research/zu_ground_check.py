"""Compile production ZU ground policy/control against deterministic scene doubles.

No Unity, game, sockets or TEMP dependency. Physics doubles test all collider
classes through the same ray interface, including self exclusions and triggers.
Production controller/selection/wire/crew priority are compiled unchanged.
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
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get{return new Vector3();}} public static Vector3 up {get{return new Vector3(0,1,0);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}} public float magnitude {get{return (float)Math.Sqrt(sqrMagnitude);}}
  public Vector3 normalized {get{return magnitude>0?this/magnitude:zero;}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float s){return new Vector3(a.x*s,a.y*s,a.z*s);}
  public static Vector3 operator /(Vector3 a,float s){return a*(1/s);}
  public static float Angle(Vector3 a,Vector3 b){return (float)(Math.Acos(Math.Max(-1,Math.Min(1,(a.x*b.x+a.y*b.y+a.z*b.z)/(a.magnitude*b.magnitude))))*180/Math.PI);}
 }
 public class Transform {public Vector3 position,forward=new Vector3(0,0,1);public Transform parent;public GameObject gameObject;
  public bool IsChildOf(Transform t){for(Transform v=this;v!=null;v=v.parent)if(v==t)return true;return false;}}
 public class GameObject {public Transform transform;public Component Npc;public bool activeInHierarchy=true,Dead,White;public int Side=1,Actor=7;
  public GameObject(){transform=new Transform();transform.gameObject=this;}
  public Component GetComponentInParent(Type type){for(Transform t=transform;t!=null;t=t.parent)if(t.gameObject.Npc!=null)return t.gameObject.Npc;return null;}}
 public class Component {public GameObject gameObject=new GameObject();public Component(){gameObject.Npc=this;}public Transform transform {get{return gameObject.transform;}}
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
}
namespace NextDayRevival {
 internal enum FlakMode {WeaponsFree,HoldFire,AssignedOnly,ZoneDefence}
 internal class MercAAPost {public int Actor=3,View=9;public Component Ai=new Component();}
 internal class MercUnit {public int AAView=9;public Fight Fight=new Fight();}
 internal class Fight {public MercCrewPhase Crew=new MercCrewPhase();public Component CrewThreat;}
 internal static class MercAA {public static bool Authority=true;public static MercAAPost Post;
  public static MercAAPost Gun(int i){return i==5?Post:null;}}
 internal static class Mercs {public const int LocalActor=3;public static int ActorOf(GameObject c){return c.Actor;}
  public static bool PlayerDead(GameObject c){return c.Dead;}public static string SteamOf(GameObject c){return c.White?"white":null;}
  public static bool Whitelisted(string c){return c=="white";}public static int FactionOf(GameObject c){return c.Side;}}
 internal static class FlakNet {public static int Sends;public static void SendPacket(float[] p,bool reliable){Sends++;}}
 internal static class Flak {
  public const float K=2.8f;public static Gun Current;public static int Shots,Publishes,Reloads;
  public class Gun {public int Index=5,Rounds=100;public bool ShortRange=true,Engaged,Firing,Reloading;public float Held,FuzeRange;
   public float WantYaw,WantPitch;public Vector3 Aim;public Transform Root=new GameObject().transform,Cradle=new GameObject().transform,Muzzle=new GameObject().transform,Owner;
   public Component Gunner,Loader;public object Hostile;public GepardGun.Contact Target;public ShortBurst ShortBurst;public FlakMode Mode;public GameObject Assigned;
   public Vector3 ZoneCentre;public float ZoneRadius;}
  public static Gun ByIndex(int i){return Current!=null&&Current.Index==i?Current:null;}
  public static bool Up(Component c){return c!=null&&c.Alive&&!c.Down;}
  public static Vector3 Mid(Gun g){return g.Cradle.position;}
  public static void Angles(Gun g,Vector3 d,out float yaw,out float pitch){yaw=(float)(Math.Atan2(d.x,d.z)*180/Math.PI);pitch=(float)(Math.Atan2(d.y,Math.Sqrt(d.x*d.x+d.z*d.z))*180/Math.PI);}
  public static void RaiseEngaging(Gun g,GameObject c){}
  public static void StartReload(Gun g,float seconds){Reloads++;g.Reloading=true;}
  public static void Fire(Gun g,Vector3 aim,bool valid,float fuze,object contacts,bool player){Shots++;g.Rounds--;}
  public static void Publish(Gun g,bool player,bool now){Publishes++;}
 }
 internal static class GepardGun {public class Contact {public GameObject Go=new GameObject();}}
 internal static class FlakFire {public static bool Crewman(Flak.Gun g,Transform tr){return g.Gunner!=null&&tr.IsChildOf(g.Gunner.transform);}}
 internal static class ShortRange {public static Vector3 Intercept(Vector3 from,Vector3 pos,Vector3 velocity,out float tof){
  tof=(pos-from).magnitude/(ShortRangeCore.SpeedM*Flak.K);return pos+velocity*tof+Vector3.up*(.5f*9.81f*Flak.K*tof*tof);}}
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
  for(int m=0;m<=650;m++){bool expected=m>=25&&m<=600;Ok(ZuGroundCore.Envelope(0,0,m*2.8f)==expected,"range "+m);}
  Ok(!ZuGroundCore.Envelope(0,-30,280),"depression dead zone");Ok(!ZuGroundCore.Envelope(0,80,280),"ground ceiling angle");
  Ok(ZuGroundCore.Rifle(false,true,true),"close assault dismounts despite inbound air");
  Ok(!ZuGroundCore.Rifle(true,true,false)&&ZuGroundCore.Rifle(true,false,false),"far gun / unreachable rifle");
  Ok(!ZuGroundCore.GroundAllowed(1,false)&&!ZuGroundCore.GroundAllowed(0,true)&&ZuGroundCore.GroundAllowed(2,true),"manual and AUTO priority");
  Flak.Gun g=Gun();Component enemy=Enemy(100);NpcWar.Scene.Add(enemy);ZuGround.State state=new ZuGround.State();
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
  Ok(ZuGround.Control(g,.0167f,true,false),"GROUND excludes simultaneous air engagement");Time.time=3;Ok(ZuGround.Duty(g)==0,"expired duty falls back AUTO");
  enemy.Remote=true;Ok(ZuGround.RemoteImpact(enemy.gameObject,90,enemy.transform.position)&&NpcWar.RemoteCalls==1,"master ground hit routes to remote NPC owner");
  enemy.Remote=false;Ok(!ZuGround.RemoteImpact(enemy.gameObject,90,enemy.transform.position),"local NPC uses direct native damage");
  Component manual=Enemy(150);manual.Remote=true;Ok(ZuGround.RemoteImpact(manual.gameObject,90,manual.transform.position)&&NpcWar.RemoteCalls==2,"manual player shot resolves remote owner without air/ground target list");
  g.Target=null;MercAA.Post=null;g=Gun();Flak.Shots=0;int maxRays=0;
  for(int frame=0;frame<1200;frame++){Time.time=10+frame/60f;int rays=Physics.Calls;ZuGround.Control(g,1f/60f,true,false);maxRays=Math.Max(maxRays,Physics.Calls-rays);}
  Ok(Flak.Shots==100&&g.Rounds==0&&g.Reloading,"ground consumes finite 100-round magazine");Ok(maxRays<=3,"one lane of three rays only on scan");
  Ok(Flak.Reloads>0,"empty gun reloads via existing gun path");
  NpcWar.Fighter attacker=new NpcWar.Fighter{Ai=new Component(),Tr=new GameObject().transform,Squad=new NpcWar.Squad(),Hated=new int[]{2}};attacker.Tr.position=new Vector3(0,0,100);
  Ok(NpcWar.Priority(attacker,Time.time)&&attacker.Target==g.Gunner.transform,"open gunner first hostile target");
  NpcWar.BlockedSight=true;Ok(!NpcWar.Priority(attacker,Time.time),"crew priority requires visibility");NpcWar.BlockedSight=false;
  attacker.Squad.Merc=new object();Ok(!NpcWar.Priority(attacker,Time.time),"player merc orders not overridden");
  for(int i=0;i<10000;i++){Time.time=40+i/60f;ZuGround.Control(g,1f/60f,true,false);}
  Stopwatch sw=new Stopwatch();GC.Collect();long bytes=GC.GetTotalMemory(false);int gc=GC.CollectionCount(0);sw.Start();
  for(int i=0;i<200000;i++){Time.time=1000+i/60f;ZuGround.Control(g,1f/60f,true,false);}
  sw.Stop();long growth=GC.GetTotalMemory(false)-bytes;Console.WriteLine("production ground controller: {0:F6} ms/frame, heap delta {1}, Gen0 delta {2}",sw.Elapsed.TotalMilliseconds/200000,growth,GC.CollectionCount(0)-gc);
  Ok(growth==0&&GC.CollectionCount(0)==gc,"steady controller zero allocations");Ok(FrameProf.Starts==FrameProf.Ends,"balanced profiler exits");
  Console.WriteLine("{0}: {1} production ZU assertions; 100 rounds exhausted; all blocker classes; max {2} lane rays/scan",fail==0?"PASS":"FAIL",count,maxRays);return fail==0?0:1;
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
    sources = ['Revival.ZuGround.cs', 'Revival.ZuGroundCore.cs',
               'Revival.MercCrewPhasesCore.cs', 'Revival.ShortRangeCore.cs']
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe),
                            str(harness)] + [str(ROOT / p) for p in sources],
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
    assert '-10f' not in '\n'.join(line for line in flak.splitlines() if 'g.ShortRange ?' in line)
    prof = read('RevivalFrameProfiler.cs')
    slots = re.findall(r'public const int S_\w+ = (\d+);', prof)
    assert len(slots) == len(set(slots)) and 'ZuGround.Control.Sub' in prof
    for source in ('Revival.ZuGround.cs', 'Revival.ZuGroundCore.cs'):
        assert (ROOT / source).read_bytes().isascii() and source in read('sync_public.py')
    print('PASS wiring: master-only tick, owner/view lease validation, shared ammo and Photon shots, calibre damage, all colliders, sleep gate, 2 Hz/32-entry budget, F6 slot 190')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

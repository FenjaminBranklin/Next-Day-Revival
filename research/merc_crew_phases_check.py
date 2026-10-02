"""Z K5a1: compile and exercise production crew policy/adapters offline.

Unity/Photon/scene dependencies are deterministic doubles. This does not claim
live navigation, multiplayer visuals, combat results or F6 acceptance.
"""
from pathlib import Path
import os
import subprocess
import sys

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
  public static Vector3 forward {get{return new Vector3(0,0,1);}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
 }
 public class Transform {public Vector3 position;}
 public class Component {public Transform transform=new Transform();public bool Alive=true,Hidden,Targetable=true;public int Side=1;}
 public class GameObject {public bool Visible=true;public float Height=100;}
 public static class Mathf {public static float Sqrt(float x){return (float)Math.Sqrt(x);}}
 public static class Time {public static float time;}
}
namespace UnityEngine.AI {
 public struct NavMeshHit {public Vector3 position;}
 public class NavMeshAgent {public bool isActiveAndEnabled=true;public int Warps;public void Warp(Vector3 p){Warps++;}}
 public static class NavMesh {
  public const int AllAreas=-1;public static int Samples;public static Vector3 At;public static bool Valid=true;
  public static bool SamplePosition(Vector3 p,out NavMeshHit h,float r,int mask){Samples++;h=new NavMeshHit();h.position=At;return Valid;}
 }
}
namespace NextDayRevival {
 class MercOrder {public const int ManGun=5,ManRadar=6,Follow=0;public int Mode=ManGun,Post;public bool Vehicle;}
 struct FightOut {} enum FightAct {None,Fire}
 struct FightIn {public bool Danger;}
 struct CoverPoint {public Vector3 Pos;}
 struct CoverPick {public bool Found;public CoverPoint Point;}
 class MercSense {public CoverPick Pick;public float PickAt,NextSense,NextPick;}
 class Brain {public int Leaves;public void Leave(object field){Leaves++;}}
 class MercFight {
  public readonly MercCrewPhase Crew=new MercCrewPhase();public MercOrder CrewOrder;
  public Component CrewThreat;public Brain Brain=new Brain();public FightOut Out;
  public FightIn In;public FightAct LastAct;public float FallbackAt;
 }
 class MercUnit {
  public MercOrder Order=new MercOrder();public MercFight Fight=new MercFight();public MercSense Sense=new MercSense();
  public Component Ai=new Component();public float NextOrder,AANextSend;public bool AARetreat;public string Name="merc";
  public Vector3 Approach;
 }
 static class Mercs {
  public class Record {public MercUnit Unit;}
  public static List<Record> Selected=new List<Record>();public static List<Record> Selection(){return Selected;}
  public static string SideOf(int s){return s==2?"friendly":"enemy";}
 }
 static class MercUi {public static void OrderReply(string text,bool warn){}}
 static class Loc {public static string T(string ru,string en){return en;}}
 static class MercCoverService {public static readonly object Field=new object();}
 class MercAAPost {public int Side=2;}
 static class MercAA {
  public const int Radar=4;public static MercAAPost Operator=new MercAAPost();public static int Releases;
  public static bool IsVehicle(MercOrder o){return o.Vehicle;}public static int PostOf(MercUnit u){return u.Order.Post;}
  public static void Release(MercUnit u){Releases++;}
 }
 static class Flak {
  public const float K=2.8f;public class Gun {public bool ShortRange;public Transform Earthwork=new Transform();}
  public static Gun[] Guns=new Gun[7];public static Gun ByIndex(int i){return Guns[i];}
 }
 static class TowerRadar {public static bool Built=true,Working=true;public static int Tier=2,ControlSide=2;public static object CfgMinHeight;public static float F(object cfg,float f){return f;}}
 static class GepardGun {public class Contact {public GameObject Go=new GameObject();public Vector3 Pos,Vel;public string Side="enemy";}}
 static class RadarScope {public static List<GepardGun.Contact> Air=new List<GepardGun.Contact>();public static float SampleAt;}
 static class ZuGround {
  public static bool CanReach,IsClose;public static Component Threat;
  public static void OwnerMode(Flak.Gun g,MercUnit u,int mode){}
  public static bool Reachable(Flak.Gun g,Vector3 p){return CanReach;}
  public static bool Close(Flak.Gun g,Vector3 p){return IsClose;}
 }
 static class RadarShadow {public static bool Visible(GameObject go){return go.Visible;}public static float Height(GameObject go){return go.Height;}}
 static class FlakFire {public static void Allegiance(GepardGun.Contact c,string side,out bool h,out bool f){f=c.Side==side;h=c.Side!=null&&!f;}}
 static class AirDefencePolicy {public static bool Hostile(string mine,string pilot){return mine!=pilot;}}
 static class MercRide {public static bool HiddenRider(Component c){return c.Hidden;}}
 static class FrameProf {public const int S_MercCrewPhase=189;public static int Starts,Ends;public static void S(int s){Starts++;}public static void E(int s){Ends++;}}
 public static partial class NpcWar {
  static bool ZuCrewThreat(Fighter f,MercUnit u,Flak.Gun g,float now){u.Fight.CrewThreat=ZuGround.Threat;return ZuGround.Threat!=null;}
  internal class Fighter {
   public Component Ai;public Transform Tr;public object Faction=2;public int[] Hated={1};public object Squad;
   public Transform Target;public float LastSeen;public bool HasOrder;public UnityEngine.AI.NavMeshAgent Agent=new UnityEngine.AI.NavMeshAgent();
  }
  static readonly List<Component> _scene=new List<Component>();
  static readonly object[] Sides={0,1,2};public static Component Player;public static int Moves,Crouches,Wakes;
  static bool FacValue(object f,out int side){side=f==null?-1:(int)f;return side>=0;}
  static object FactionOf(Component c){return Sides[c.Side];}
  static bool Alive(Component c){return c.Alive;}
  static bool Hostile(int[] hated,object side){return (int)side==1;}
  static Fighter FighterOf(Component c){return null;}
  static bool Targetable(Component c){return c.Targetable;}
  static Component MercPlayerTarget(Fighter f,float range,float now){return Player;}
  static UnityEngine.AI.NavMeshAgent Agent(Fighter f){return f.Agent;}
  static float Flat(Vector3 p){return (float)Math.Sqrt(p.x*p.x+p.z*p.z);}
  static void MercMove(Fighter f,MercUnit u,Vector3 p,bool run,float now){Moves++;}
  static void MercCrouch(Fighter f,float now){Crouches++;}
  internal static void MercStationWake(MercUnit u){Wakes++;}
  internal static bool Step(Fighter f,MercUnit u,float now){Time.time=now;RadarScope.SampleAt=now;return MercCrewUpdate(f,u,now);}
  internal static bool Stale(Fighter f,MercUnit u,float now){Time.time=now;return MercCrewUpdate(f,u,now);}
  internal static void Hold(Fighter f,MercUnit u,float now){MercCrewHold(f,u,now);}
  internal static List<Component> Scene {get{return _scene;}}
 }
}
class Check {
 static int checks,failures;
 static void Ok(bool yes,string label){checks++;if(!yes){failures++;Console.WriteLine("FAIL "+label);}}
 static NpcWar.Fighter Fighter(MercUnit u){return new NpcWar.Fighter{Ai=u.Ai,Tr=u.Ai.transform};}
 static int Main(){
  for(int i=0;i<7;i++)Flak.Guns[i]=new Flak.Gun();
  MercCrewPhase p=new MercCrewPhase();
  Ok(!p.Step(0,true,false,true)&&p.Step(1,true,false,true),"one second radar all-clear debounce");
  Ok(!p.Step(1.01f,true,true,true),"inbound overrides ground immediately");
  Ok(!p.Step(2,false,false,true),"missing/stale radar conservatively mans gun");
  Ok(!p.Step(3,true,false,false),"no ground enemies stays on gun");
  p.Override=MercCrewPhase.Ground;Ok(p.Step(3.01f,false,true,false),"manual GROUND wins");
  p.Override=MercCrewPhase.Air;Ok(!p.Step(3.02f,true,false,true),"manual AIR wins");
  p.Reset();Ok(p.Override==0&&!p.OnGround&&p.Cursor==0,"reset returns AUTO");
  Ok(MercCrewPhase.NearGround(840,0)&&!MercCrewPhase.NearGround(840.1f,0)&&!MercCrewPhase.NearGround(600,600),"300 metre radial boundary");
  Ok(MercCrewPhase.InPost(16.8f,0)&&!MercCrewPhase.InPost(17,0),"6 metre wall leash");
  MercUnit u=new MercUnit();NpcWar.Fighter f=Fighter(u);
  Component enemy=new Component();enemy.transform.position=new Vector3(800,0,0);NpcWar.Scene.Add(enemy);
  Ok(!NpcWar.Step(f,u,10)&&NpcWar.Step(f,u,11),"live adapter detects beyond rifle range and dismounts");
  Ok(u.Order.Mode==MercOrder.ManGun&&MercCrewPhases.Ground(u)&&f.Agent.Warps==1,"preserve order and restore navigable feet");
  Ok(u.Approach.x==800,"quiet M1 shelter faces approaching ground enemy");
  RadarScope.Air.Add(new GepardGun.Contact{Pos=new Vector3(100,0,0),Vel=new Vector3(-.1f,0,0)});
  Ok(!NpcWar.Step(f,u,11.25f),"slow aircraft already inside bubble immediately remounts");
  Ok(!MercCrewPhases.Ground(u),"ground gate clears for inherited station remount");
  RadarScope.Air[0].Pos=new Vector3(5000,0,0);RadarScope.Air[0].Vel=new Vector3(-100,0,0);
  Ok(MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"incoming transport before jump");
  RadarScope.Air[0].Vel=new Vector3(100,0,0);Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"outside bubble and departing ignored");
  RadarScope.Air[0].Vel=new Vector3(0,0,100);Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"wide passing track ignored");
  RadarScope.Air[0].Pos=new Vector3(100,0,0);RadarScope.Air[0].Side="friendly";
  Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"friendly aircraft ignored");
  RadarScope.Air[0].Side=null;Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"unknown IFF ignored");
  RadarScope.Air[0].Side="enemy";RadarScope.Air[0].Go.Visible=false;
  Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"shadowed aircraft absent from radar picture");
  RadarScope.Air[0].Go.Visible=true;RadarScope.Air[0].Go.Height=1;
  Ok(!MercCrewPhases.Inbound(Flak.Guns[0],"friendly"),"landed aircraft ignored");RadarScope.Air.Clear();
  TowerRadar.ControlSide=1;MercAA.Operator.Side=2;
  Ok(!NpcWar.Step(f,u,12)&&NpcWar.Step(f,u,13),"merc radar side survives released gun lease and foreign field holder");
  MercAA.Operator=null;Ok(!NpcWar.Step(f,u,13.25f),"loss of matching radar remounts");
  TowerRadar.ControlSide=2;Ok(!NpcWar.Step(f,u,14)&&NpcWar.Step(f,u,15),"matching staffed player radar works without merc radar");
  RadarScope.SampleAt=0;Ok(NpcWar.Stale(f,u,15.1f),"phase sampling throttled between quarter second ticks");
  Ok(!NpcWar.Stale(f,u,15.25f),"stale radar never interpreted as all-clear");
  u.Fight.Crew.NextSample=100;Ok(!NpcWar.Step(f,u,16),"cached phase has no resample");u.Fight.Crew.NextSample=0;
  Mercs.Selected.Add(new Mercs.Record{Unit=u});MercCrewPhases.OverrideSelected(MercCrewPhase.Ground);
  Ok(NpcWar.Step(f,u,17)&&u.Fight.Crew.Override==2&&NpcWar.Wakes==1,"selected override is immediate owner command");
  Vector3 bounded=MercCrewPhases.Bound(u,new Vector3(100,7,0));
  Ok(Math.Abs(bounded.x-16.8f)<.001f&&bounded.y==7,"fight strafe/flank bound at wall");
  u.AARetreat=true;Ok(MercCrewPhases.Bound(u,new Vector3(100,0,0)).x==100,"injury retreat free to leave wall");u.AARetreat=false;
  u.Fight.In.Danger=true;Ok(MercCrewPhases.Bound(u,new Vector3(100,0,0)).x==100,"blast evasion free to leave wall");u.Fight.In.Danger=false;
  u.Sense.Pick.Found=true;u.Sense.Pick.Point.Pos=new Vector3(10,0,0);u.Sense.PickAt=17;
  NpcWar.Hold(f,u,17);Ok(NpcWar.Moves==1,"quiet ground duty moves to M1 wall shelter");
  u.Ai.transform.position=new Vector3(10,0,0);NpcWar.Hold(f,u,17);Ok(NpcWar.Crouches==1,"at wall duty crouches");
  MercOrder original=u.Order;u.Order=new MercOrder();NpcWar.Step(f,u,18);
  Ok(u.Fight.Crew.Override==0&&u.Order!=original,"fresh order resets manual override");
  u.Order.Mode=MercOrder.ManRadar;Ok(!NpcWar.Step(f,u,19)&&MercCrewPhases.Gun(u)==null,"radar duty never dismounts for crew phase");
  u.Order.Mode=MercOrder.ManGun;u.Order.Vehicle=true;Ok(MercCrewPhases.Gun(u)==null,"vehicle crew excluded");u.Order.Vehicle=false;
  u.Order.Post=100;Ok(MercCrewPhases.Gun(u)==null,"artillery excluded");u.Order.Post=4;Ok(MercCrewPhases.Gun(u)==null,"radar identity excluded");
  u.Order.Post=7;Ok(MercCrewPhases.Gun(u)==Flak.Guns[0],"loader shares correct earthwork");
  Flak.Guns[0].Earthwork=null;Ok(MercCrewPhases.Gun(u)==null,"non-earthwork guns excluded");Flak.Guns[0].Earthwork=new Transform();u.Order.Post=0;
  NpcWar.Scene.Clear();for(int i=0;i<1000;i++){Component c=new Component();c.Side=2;NpcWar.Scene.Add(c);}NpcWar.Scene[999]=enemy;
  for(int i=0;i<40;i++)NpcWar.Step(f,u,20+i*.25f);
  Ok(MercCrewPhases.Ground(u)&&u.Fight.CrewThreat==enemy,"bounded dense scene walk finds last enemy");
  for(int i=0;i<40;i++)NpcWar.Step(f,u,30+i*.25f);
  Ok(MercCrewPhases.Ground(u),"cached enemy prevents seat churn during long scene walk");
  enemy.Alive=false;Ok(!NpcWar.Step(f,u,44),"dead cached enemy expires ground duty");
  enemy.Alive=true;enemy.Hidden=true;for(int i=0;i<40;i++)NpcWar.Step(f,u,45+i*.25f);
  Ok(!MercCrewPhases.Ground(u),"hidden closed-hull riders are not ground threats");enemy.Hidden=false;
  NpcWar.Scene.Clear();NpcWar.Player=new Component();NpcWar.Player.transform.position=new Vector3(800,0,0);
  Ok(NpcWar.Step(f,u,56),"cached hostile player counts");NpcWar.Player=null;
  u.Fight.Crew.Override=2;u.Fight.Crew.NextSample=0;f.Agent.isActiveAndEnabled=false;
  int samples=UnityEngine.AI.NavMesh.Samples;NpcWar.Step(f,u,60);Ok(UnityEngine.AI.NavMesh.Samples==samples,"disabled navigation never sampled/warped");
  MercUnit[] units=new MercUnit[6];NpcWar.Fighter[] fs=new NpcWar.Fighter[6];
  for(int i=0;i<6;i++){units[i]=new MercUnit();units[i].Order.Post=i<3?i:7+i-3;fs[i]=Fighter(units[i]);}
  NpcWar.Scene.Add(enemy);for(int i=0;i<6;i++){NpcWar.Step(fs[i],units[i],70);Ok(NpcWar.Step(fs[i],units[i],71),"six crews independent "+i);}
  RadarScope.Air.Add(new GepardGun.Contact{Pos=new Vector3(100,0,0)});
  for(int i=0;i<6;i++)Ok(!NpcWar.Step(fs[i],units[i],71.25f),"new wave returns each crew "+i);
  for(int frame=0;frame<10000;frame++)for(int i=0;i<6;i++)NpcWar.Step(fs[i],units[i],100+frame/60f);
  Stopwatch sw=new Stopwatch();GC.Collect();long heap=GC.GetTotalMemory(false);int collections=GC.CollectionCount(0);sw.Start();
  for(int frame=0;frame<100000;frame++)for(int i=0;i<6;i++)NpcWar.Step(fs[i],units[i],300+frame/60f);
  sw.Stop();long delta=GC.GetTotalMemory(false)-heap;
  Console.WriteLine("six crew adapter: "+(sw.Elapsed.TotalMilliseconds/100000).ToString("F6")+" ms/frame; heap delta "+delta+" bytes; gen0 delta "+(GC.CollectionCount(0)-collections));
  Ok(GC.CollectionCount(0)==collections&&delta<1024,"steady state fake-world adapters allocate no managed objects");
  Ok(FrameProf.Starts==FrameProf.Ends,"profiler balances every early exit");
  MercUnit zu=new MercUnit();zu.Order.Post=5;NpcWar.Fighter zf=Fighter(zu);Flak.Guns[5].ShortRange=true;
  RadarScope.Air.Clear();ZuGround.Threat=enemy;ZuGround.CanReach=true;ZuGround.IsClose=false;
  NpcWar.Step(zf,zu,3000);int leases=MercAA.Releases;
  Ok(!NpcWar.Step(zf,zu,3001)&&MercAA.Releases==leases,"ZU reachable AUTO ground threat keeps physical gun lease");
  ZuGround.IsClose=true;Ok(NpcWar.Step(zf,zu,3001.25f)&&MercAA.Releases>leases,"ZU close attacker releases lease for rifle defense");
  RadarScope.Air.Add(new GepardGun.Contact{Pos=new Vector3(100,0,0)});
  Ok(NpcWar.Step(zf,zu,3001.5f),"close ZU defense survives inbound air interruption");
  ZuGround.IsClose=false;Ok(!NpcWar.Step(zf,zu,3001.75f),"safe distant ZU returns to inbound air");
  zu.Fight.Crew.Override=2;Ok(!NpcWar.Step(zf,zu,3002),"manual GROUND retains reachable ZU gun");
  ZuGround.CanReach=false;Ok(NpcWar.Step(zf,zu,3002.25f),"manual GROUND unreachable target uses rifles");
  zu.Fight.Crew.Override=1;ZuGround.IsClose=true;Ok(!NpcWar.Step(zf,zu,3002.5f),"explicit AIR override honored");
  Console.WriteLine((failures==0?"PASS":"FAIL")+": "+checks+" production crew phase assertions");return failures==0?0:1;
 }
}
'''


def main():
    work = ROOT / 'build' / 'merc_crew_phases_check'
    work.mkdir(parents=True, exist_ok=True)
    harness = work / 'Harness.cs'
    harness.write_text(HARNESS, encoding='ascii')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    exe = work / 'Check.exe'
    args = [str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe), str(harness)]
    args += [str(ROOT / name) for name in ('Revival.MercCrewPhasesCore.cs',
             'Revival.MercCrewPhases.cs', 'Revival.AirPicturePolicy.cs', 'Revival.ZuGroundCore.cs')]
    built = subprocess.run(args, text=True, encoding='utf-8', errors='replace', capture_output=True)
    if built.returncode:
        print(built.stdout + built.stderr)
        return 1
    checked = subprocess.run([str(exe)], text=True, capture_output=True)
    print(checked.stdout + checked.stderr, end='')
    if checked.returncode:
        return checked.returncode
    read = lambda name: (ROOT / name).read_text(encoding='utf-8')
    crew = read('Revival.MercCrewPhases.cs')
    adapters = read('Revival.MercStationsAdapters.cs')
    combat = read('Revival.NpcCombat.cs')
    aa = read('Revival.MercAA.cs')
    assert combat.index('MercStationDuty(f, s.Merc, now)') < combat.index('else if (MercFight(f, s.Merc, now))')
    assert adapters.index('MercStationPlan.Retreat(health') < adapters.index('if (groundDuty)')
    post = aa[aa.index('static void MercPostStep'):]
    assert post.index('MercStationPlan.Retreat') < post.index('MercCrewHold(f, u, now)')
    assert 'MercCrewPhases.Ground(local)' in aa and 'sender == MasterActor()' in aa
    assert 'phase.NextSample = now + 0.25f' in crew and 'n < 32' in crew
    assert all(x not in crew for x in ('Physics.', 'FindObjects', 'GetComponent', '=>'))
    assert crew.count('NavMesh.SamplePosition') == 1
    assert 'MercCrewPhases.Bound(u, goal)' in read('Revival.MercFight.cs')
    cover = read('Revival.MercCover.cs')
    assert 'quietLeash, quietRadius' in cover and 'radius = MercCrewPhase.PostRadius' in cover
    assert 'MercCrewPhases.Ground(u)' in read('Revival.MercCombatResponse.cs')
    # A S6 removed the assignment picker; new station orders retain AUTO and
    # reset prior manual phases at the production crew-order boundary.
    assert 'ft.Crew.Reset();' in read('Revival.MercCrewPhases.cs')
    assert 'Mercs.ToggleAirDefence();' in read('Revival.MercsUi.cs')
    radar = read('Revival.TowerRadar.cs')
    assert radar.index('RadarShadow.Scan(_air, eye)') < radar.index('SampleAt = now')
    prof = read('RevivalFrameProfiler.cs')
    import re
    slots = re.findall(r'public const int S_\w+ = (\d+);', prof)
    assert len(slots) == len(set(slots)) and 'MercCrewPhase.Think.Sub' in prof
    # Existing 52-K ground exclusion remains in its fire selector.
    flak = read('Revival.Flak.cs')
    search = flak[flak.index('internal static void Search(Flak.Gun g)'):flak.index('internal static void Collect(')]
    assert 'else if (!Airborne(c)) continue;' in search
    for name in ('Revival.MercCrewPhases.cs', 'Revival.MercCrewPhasesCore.cs'):
        raw = (ROOT / name).read_bytes()
        assert not raw.startswith(b'\xef\xbb\xbf') and '\\u04' not in raw.decode('utf-8')
    print('PASS wiring: owner fight loop, survival first, master leases/snapshots, wall cover, manual phases, unique F6 slot, 4 Hz bounded cached queries, air-only 52-K path')
    return 0


if __name__ == '__main__':
    sys.exit(main())

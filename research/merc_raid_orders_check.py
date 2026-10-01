"""Z K8: production raid adapter/core, real Give/For/GiveStation and M1 geometry.

C# 3.0 offline simulation; all outputs stay in this worktree. No game or UI.
The analytic world includes all colliders in each fixture, including slabs,
fences, props and moving objects. Runtime uses M1's complete PhysX world.
"""
from pathlib import Path
import os
import re
import subprocess
import sys

import merc_cover_check as m1

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def method(src, anchor):
    start = src.index(anchor)
    brace = src.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (src[end] == '{') - (src[end] == '}')
        end += 1
    return src[start:end]


SUPPORT = r'''
namespace UnityEngine {
 public struct Quaternion {}
 public class Transform {public Vector3 position,forward=new Vector3(0,0,1);}
 public class Component {public Transform transform=new Transform();}
 public static class Time {public static float time;}
}
namespace NextDayRevival {
 using UnityEngine;
 static class Loc {public static string T(string ru,string en){return en;}}
 static class MapScene {public static string Current="fixture";}
 static class FrameProf {public const int S_MercRaidT=185,S_MercRaidCoverT=186;public static void S(int n){} public static void E(int n){}}
 static class TowerRadar {public static bool On=true,Built=true,SirenOn;public static Vector3 At;public static Vector3 TowerPoint(Vector3 p){return At+p;}}
 sealed class MercOrder {
  public const int Follow=0,Stay=1,Patrol=2,Perimeter=3,Vehicle=4,ManGun=5,ManRadar=6,Attack=7;
  public int Mode,K,N;public bool Survive;public string Scene;public float IssuedAt;
  public Vector3 Facing;public Vector3[] Points=new Vector3[0];
  public Vector3 Centre{get{return Points.Length==0?Vector3.zero:Points[0];}}
  FOR_SOURCE
 }
 sealed class MercCarrier{public int View;}
 sealed class MercStation {public int Post;public int Seat=-1;public MercCarrier Carrier;public Vector3 At;}
 sealed class Sense {public float NextPick,PickAt;}
 sealed class AttackState {public void Reset(){}}
 sealed class Ride {public object Boarding;public float NextBoard;public MercOrder AssignedOrder;public MercCarrier AssignedCarrier;}
 sealed class Brain {public CoverPick Cover;public int Leaves;public void Leave(CoverField f){Leaves++;}}
 struct FightOut {}
 sealed class Fight {public Brain Brain=new Brain();public FightOut Out;public bool Holding;}
 sealed class MercUnit {
  public int Id,RaidProbe;public bool Deserting,RaidCover,Rally,Chasing,RaidProbeDeferred;
  public float NextOrder,AANextSend,RaidRoofAt;public Vector3 Approach;
  public Component Ai=new Component();public MercOrder Order,GoalFor;
  public Sense Sense=new Sense();public AttackState Attack=new AttackState();
  public Ride Ride=new Ride();public Fight Fight=new Fight();public CoverPick RaidRoof;
 }
 static class MercCoverService {public static CoverField Field;public static ICoverWorld World;}
 static class MercUi {public static int Replies;public static string Last;public static void OrderReply(string s,bool error){Replies++;Last=s;}}
 static class NpcWar {public static int Wakes;public static void MercStationWake(MercUnit u){if(u!=null)Wakes++;}}
 static class Mortar {public static void MercPrepare(int p){}}
 static class Flak {
  public static bool[] Guns=new bool[7];
  public static object ByIndex(int n){return n>=0&&n<7&&Guns[n]?Token:null;}
  static readonly object Token=new object();
 }
 static class MercAA {
  public const int Radar=4;public static Vector3[] Places=new Vector3[7];
  public static bool[] Free=new bool[7];public static int Releases;
  public static bool IsOrder(MercOrder o){return o.Mode==MercOrder.ManGun||o.Mode==MercOrder.ManRadar;}
  public static void Release(MercUnit u){Releases++;}
  public static bool Pose(int n,out Vector3 at,out Quaternion rot){at=Places[n];rot=new Quaternion();return n>=0&&n<7&&Flak.Guns[n];}
  public static bool CanApproach(int n,Component c){return Free[n];}
 }
 static partial class Mercs {
  internal sealed class Record {
   public int Id;public string Name="merc";public bool Dead,Deserted,Unpaid,Selected=true;
   public MercOrder Order=new MercOrder();public MercUnit Unit;
   public readonly MercRaidState Raid=new MercRaidState();
  }
  static readonly List<Record> _roster=new List<Record>(10);
  internal static Vector3 OwnerPosition;
  public static int Saves,Acks;
  internal static Record Add(Vector3 at,int mode){Record r=new Record();r.Id=_roster.Count+1;
   r.Unit=new MercUnit();r.Unit.Id=r.Id;r.Unit.Ai.transform.position=at;
   r.Order.Mode=mode;r.Unit.Order=r.Order;_roster.Add(r);return r;}
  internal static void Reset(){_roster.Clear();RaidClock.Clear();_raidScene=MapScene.Current;Saves=0;Acks=0;}
  internal static void TestTick(float t){Time.time=t;RaidTick(t);}
  internal static List<Record> Selection(){List<Record> a=new List<Record>();foreach(Record r in _roster)if(r.Selected&&!r.Dead&&!r.Deserted)a.Add(r);return a;}
  internal static List<Record> StationSelection(bool radar){List<Record>a=Selection();a.RemoveAll(delegate(Record r){return r.Unpaid;});return a;}
  internal static void SendOrders(List<Record> r){Saves++;}
  internal static void SaveStationOrders(List<Record> r){SendOrders(r);}
  static void OrderReceived(Record r,bool focus,Vector3 point){RaidReceived(r);Acks++;NpcWar.Wakes++;}
  internal static void NewOrder(Record r,int mode){List<Record>a=new List<Record>();a.Add(r);MercOrder o=new MercOrder();o.Mode=mode;Give(a,o);}
  GIVE_SOURCE
  STATION_SOURCE
 }
}
'''

TEST = r'''
namespace NextDayRevival {
 using UnityEngine;
 static class RaidTest {
  sealed class PitWorld : ICoverWorld,IMercRaidProtection {
   public World World=new World();
   public bool Revetment(Vector3 p){return AirKillCore.Sheltered(p.x,p.y,p.z,p.x+280,p.z,2.8f)
    && AirKillCore.Sheltered(p.x,p.y,p.z,p.x-280,p.z,2.8f);}
   public bool Cast(Vector3 p,Vector3 d,float r,out Vector3 h,out Vector3 n,out bool moving){return World.Cast(p,d,r,out h,out n,out moving);}
   public bool Ground(Vector3 p,float d,out Vector3 h,out Vector3 n){return World.Ground(p,d,out h,out n);}
   public bool Stand(Vector3 p,float r,out Vector3 at){return World.Stand(p,r,out at);}
  }
  static int N;
  static void Ok(bool v,string s){if(!v)throw new Exception(s);N++;}
  static void Reset(){Mercs.Reset();Time.time=10;TowerRadar.SirenOn=false;TowerRadar.At=Vector3.zero;Mercs.OwnerPosition=Vector3.zero;
   for(int i=0;i<7;i++){Flak.Guns[i]=false;MercAA.Free[i]=true;MercAA.Places[i]=new Vector3(10*i,0,0);}}
  static void Orders(){
   Reset();Mercs.Record foot=Mercs.Add(Vector3.zero,MercOrder.Patrol);foot.Order.K=3;foot.Order.N=6;
   foot.Order.Points=new Vector3[]{new Vector3(30,0,40)};MercOrder prior=foot.Order;
   Mercs.Record gun=Mercs.Add(Vector3.zero,MercOrder.ManGun),radar=Mercs.Add(Vector3.zero,MercOrder.ManRadar);
   Mercs.Record far=Mercs.Add(new Vector3(1261,0,0),MercOrder.Attack);
   Mercs.Record dead=Mercs.Add(Vector3.zero,MercOrder.Follow);dead.Dead=true;
   Mercs.Record desert=Mercs.Add(Vector3.zero,MercOrder.Follow);desert.Deserted=true;
   Mercs.RaidWarning(Vector3.zero,20);
   Ok(foot.Order.Survive&&foot.Unit.RaidCover,"default shelters foot immediately");
   Ok(foot.Raid.Previous==prior&&Mercs.Acks==1,"one acknowledgement and exact saved order");
   Ok(gun.Order.Mode==MercOrder.ManGun&&radar.Order.Mode==MercOrder.ManRadar,"gunners and radar stay");
   Ok(!far.Order.Survive&&!dead.Order.Survive&&!desert.Order.Survive,"distant/dead/deserted excluded");
   Mercs.TestTick(29);Ok(foot.Order.Survive,"cover lasts through warning");
   Mercs.TestTick(31);Ok(foot.Order==prior&&foot.Order.K==3&&foot.Order.N==6,"all clear restores exact formation");
   Ok(!foot.Unit.RaidCover&&!foot.Unit.Rally,"all clear releases shelter overlay");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.RaidWarning(Vector3.zero,20);
   Mercs.NewOrder(foot,MercOrder.Attack);Mercs.TestTick(11);
   Ok(foot.Order.Mode==MercOrder.Attack&&!foot.Unit.RaidCover,"new order overrides alarm without delay");
   Mercs.TestTick(31);Ok(foot.Order.Mode==MercOrder.Attack,"all clear never undoes explicit attack");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Perimeter);prior=foot.Order;Mercs.RaidWarning(Vector3.zero,20);
   Time.time=15;Mercs.RaidWarning(new Vector3(20,0,0),30);Mercs.TestTick(31);
   Ok(foot.Order.Survive&&foot.Raid.Previous==prior,"overlap extends shelter without replacing saved order");
   Mercs.TestTick(46);Ok(foot.Order==prior,"restore after last overlapping warning");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);TowerRadar.SirenOn=true;Mercs.TestTick(10);
   Ok(foot.Order.Survive,"radar master snapshot triggers defaults");
   Mercs.TestTick(12);TowerRadar.SirenOn=false;Mercs.TestTick(15);Ok(!foot.Order.Survive,"radar all clear resumes");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.OrderRaidCover();Mercs.TestTick(999);
   Ok(foot.Order.Survive,"explicit cover outside raid remains until new command");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);gun=Mercs.Add(Vector3.zero,MercOrder.ManGun);gun.Order.Facing.x=1;
   Mercs.RaidWarning(Vector3.zero,20);foot.Selected=false;Mercs.OrderRaidCover();
   Ok(gun.Order.Survive&&gun.Raid.Previous.Mode==MercOrder.ManGun,"selected gunner can choose cover");
   Mercs.TestTick(31);Ok(gun.Order.Mode==MercOrder.ManGun,"selected gunner returns after alarm");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.RaidWarning(Vector3.zero,20);
   MercOrder death=new MercOrder();death.Mode=MercOrder.Stay;death.Survive=true;foot.Order=death;foot.Unit.Order=death;
   Mercs.TestTick(31);Ok(foot.Order==death,"owner-death shelter is not undone");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);foot.Unit=null;Mercs.RaidWarning(Vector3.zero,20);
   Ok(foot.Order.Survive,"not-yet-deployed owned roster receives cover");
   Reset();foot=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.RaidWarning(Vector3.zero,20);
   MapScene.Current="other";Mercs.TestTick(11);Ok(foot.Raid.Previous==null&&!foot.Unit.RaidCover,"region change clears old raid state");
   MapScene.Current="fixture";
  }
  static void Guns(){
   Reset();Flak.Guns[0]=true;Flak.Guns[1]=true;Flak.Guns[2]=true;
   Mercs.Record a=Mercs.Add(Vector3.zero,MercOrder.Follow),b=Mercs.Add(Vector3.zero,MercOrder.Follow),c=Mercs.Add(Vector3.zero,MercOrder.Follow),d=Mercs.Add(Vector3.zero,MercOrder.Follow);
   Mercs.RaidWarning(Vector3.zero,20);Mercs.OrderRaidGuns();
   Ok(a.Order.Mode==MercOrder.ManGun&&b.Order.Mode==MercOrder.ManGun&&c.Order.Mode==MercOrder.ManGun,"man guns starts all free firing posts");
   Ok(a.Order.Facing.x==1&&b.Order.Facing.x==2&&c.Order.Facing.x==3,"one gunner per gun before loaders");
   Ok(d.Order.Survive&&MercUi.Last.Contains("no free AA gun"),"overflow keeps cover with named failure");
   Mercs.TestTick(31);Ok(a.Order.Mode==MercOrder.ManGun&&!a.Unit.RaidCover,"gun command survives all clear");
   Reset();Flak.Guns[0]=true;MercAA.Free[0]=false;a=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.OrderRaidGuns();
   Ok(a.Order.Mode==MercOrder.Follow,"occupied native/player station refused");
   MercAA.Free[0]=true;MercAA.Places[0]=new Vector3(421,0,0);Mercs.OrderRaidGuns();Ok(a.Order.Mode==MercOrder.Follow,"150 m range respected");
   MercAA.Places[0]=Vector3.zero;a.Unpaid=true;Mercs.OrderRaidGuns();Ok(a.Order.Mode==MercOrder.Follow,"existing unpaid work rule retained");
   a.Unpaid=false;a.Selected=false;b=Mercs.Add(Vector3.zero,MercOrder.Follow);Mercs.OrderRaidGuns();Ok(a.Order.Mode==MercOrder.Follow&&b.Order.Mode==MercOrder.ManGun,"selection leaves other merc unchanged");
  }
  static void Geometry(){
   PitWorld pit=new PitWorld();
   Ok(MercRaidShelter.Protected(pit,Vector3.zero)&&pit.World.Rays==0,"existing master blast-protected pit accepted without physics");
   Ok(!MercRaidShelter.Protected(pit,new Vector3(100,0,0)),"outside pit is not granted protection");
   Ok(!AirKillCore.Sheltered(0,0,0,0,0,2.8f),"direct pit hit remains damaging, bombs fall freely");
   World w=new World();
   // All objects coexist, so no model-only collider filtering is possible.
   w.Wall(-25,2,25,2,6,1,0); // fence/wall
   w.Box(8,1,0,2,1,2,0,false); // prop / sandbags
   w.Box(0,10,0,5,1,5,0,false); // concrete roof slab
   w.Box(20,10,0,5,1,5,0,true); // movable roof / vehicle
   w.Box(35,10,0,0.8f,1,0.8f,0,false); // narrow overhead prop
   int rays=w.Rays;
   Ok(MercRaidShelter.Roof(w,Vector3.zero),"solid slab covers entire crouched footprint");
   Ok(w.Rays-rays==5,"roof proof costs exactly five rays");
   Ok(!MercRaidShelter.Roof(w,new Vector3(8,0,0)),"open sandbags are not a roof");
   Ok(!MercRaidShelter.Roof(w,new Vector3(20,0,0)),"moving vehicle is not a shelter");
   Ok(!MercRaidShelter.Roof(w,new Vector3(35,0,0)),"narrow prop fails full footprint proof");
   Ok(!MercRaidShelter.Roof(w,new Vector3(0,12,0)),"standing atop slab has no overhead shelter");
   CoverField f=new CoverField(w,12);Vector3 origin=new Vector3(0,0,0);
   f.Want(origin,1);for(int i=0;i<2500;i++)f.Build(1+i*.001f,24);
   CoverCell cell=f.Cell(0,0);Ok(cell!=null,"M1 cell ready");
   // Deterministic candidate fixture atop the actual M1 cell/cache.
   for(int x=-1;x<=1;x++)for(int z=-1;z<=1;z++){CoverCell other=f.Cell(x,z);if(other!=null)other.Count=0;}
   cell.Count=5;
   for(int i=0;i<4;i++){cell.Points[i]=new CoverPoint();cell.Points[i].Pos=new Vector3(8+i,0,0);}
   cell.Points[4]=new CoverPoint();cell.Points[4].Pos=Vector3.zero;
   CoverPick p;int cursor=0;rays=w.Rays;
   Ok(!MercRaidShelter.Pick(f,w,origin,1,5,ref cursor,out p),"first bounded slice skips open props");
   Ok(w.Rays-rays<=15&&cursor>0,"three candidates per slice bound");
   Ok(MercRaidShelter.Pick(f,w,origin,1,6,ref cursor,out p)&&p.Confirmed,"later roof remains discoverable");
   int second=0;Ok(!MercRaidShelter.Pick(f,w,origin,2,6,ref second,out p),"M1 claim prevents stacking");
   Ok(f.Claimed(Vector3.zero,2,6),"claimed roof excluded from other mercs");
   // Reuse all C1 colliders plus field bunds/fences/props in one world.
   World all=new World(); ALL_LAYOUT
   Ok(all.Boxes.Count>500,"combined tower and field collider set loaded");
   int found=0;
   foreach(Obb box in all.Boxes){Vector3 sample=box.C-new Vector3(0,box.Hy+3.1f,0);
    if(MercRaidShelter.Roof(all,sample))found++;}
   Ok(found>0,"combined area contains physically roofed candidates");
   Console.WriteLine("geometry: "+all.Boxes.Count+" colliders, "+found+" roofed slab samples; max 3 candidates / 15 rays per scan");
  }
  static void ClockAndPerf(){
   MercRaidClock clock=new MercRaidClock();clock.Warn(Vector3.zero,10,20);
   Ok(clock.Until(new Vector3(1260,900,0),10)==30,"450 m horizontal warning radius, height independent");
   Ok(clock.Until(new Vector3(1261,0,0),10)==0,"outside radius unaffected");
   clock.Warn(new Vector3(2000,0,0),10,40);Ok(clock.Until(Vector3.zero,10)==30&&clock.Until(new Vector3(2000,0,0),10)==50,"separate concurrent raid areas");
   clock.Warn(new Vector3(float.NaN,0,0),10,20);clock.Warn(Vector3.zero,10,float.NaN);Ok(clock.Until(Vector3.zero,10)==30,"malformed warning ignored");
   clock.Clear();Ok(clock.Until(Vector3.zero,10)==0,"scene reset clears windows");
   Reset();for(int i=0;i<6;i++)Mercs.Add(Vector3.zero,MercOrder.ManGun);
   TowerRadar.SirenOn=true;Mercs.TestTick(10);
   for(int i=0;i<10000;i++)Mercs.TestTick(10+i*.25f);
   Stopwatch timer=new Stopwatch();timer.Start();timer.Stop();timer.Reset();
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=GC.GetTotalMemory(false);int gen=GC.CollectionCount(0);
   timer.Start();for(int i=0;i<1000000;i++)Mercs.TestTick(3000+i*.25f);timer.Stop();
   long delta=GC.GetTotalMemory(false)-before;
   Console.WriteLine("raid tick six posted mercs: "+(timer.Elapsed.TotalMilliseconds*1000/1000000).ToString("0.000")+" us/tick; heap delta "+delta+" bytes; gen0 delta "+(GC.CollectionCount(0)-gen));
   Ok(GC.CollectionCount(0)==gen,"zero steady-state gen0 allocations");
   Ok(delta<8192,"bounded steady heap");
  }
  static void FairBudget(){
   World world=new World();CoverField field=new CoverField(world,12);
   MercCoverService.Field=field;MercCoverService.World=world;
   MercUnit[] units=new MercUnit[6];float[] due=new float[6];
   for(int i=0;i<6;i++){
    Vector3 at=new Vector3(i*15,0,0);world.Box(at.x,10,0,5,1,5,0,false);
    units[i]=new MercUnit();units[i].Id=20+i;units[i].RaidCover=true;
    units[i].Ai.transform.position=at;units[i].RaidRoof.Found=true;units[i].RaidRoof.Confirmed=true;units[i].RaidRoof.Point.Pos=at;
   }
   // Model M1's sense gate/NextPick and global one-query-per-frame gate.
   int peak=0;for(int step=0;step<160;step++){
    float now=100+step*.05f;int rays=world.Rays;
    for(int i=0;i<6;i++)if(now>=due[i]){
     CoverPick p;MercRaidCover.Pick(units[i],units[i].Ai.transform.position,now,out p);
     due[i]=now+(units[i].RaidProbeDeferred?.35f:1.5f);break;
    }
    peak=Math.Max(peak,world.Rays-rays);
   }
   for(int i=0;i<6;i++)Ok(units[i].RaidRoofAt>=100,"shared budget reaches merc "+i);
   Ok(peak<=20&&world.Rays<=80,"2 Hz squad roof proof remains bounded");
   Console.WriteLine("shelter budget: all 6 mercs serviced; "+world.Rays+" rays in 8 s, max "+peak+" rays/frame");
   for(int step=0;step<10000;step++)for(int i=0;i<6;i++){
    CoverPick p;MercRaidCover.Pick(units[i],units[i].Ai.transform.position,200+step*.05f,out p);}
   Stopwatch timer=new Stopwatch();timer.Start();timer.Stop();timer.Reset();
   GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long heap=GC.GetTotalMemory(false);int gen=GC.CollectionCount(0);
   timer.Start();for(int step=0;step<100000;step++)for(int i=0;i<6;i++){
    CoverPick p;MercRaidCover.Pick(units[i],units[i].Ai.transform.position,800+step*.05f,out p);}
   timer.Stop();long delta=GC.GetTotalMemory(false)-heap;
   Ok(GC.CollectionCount(0)==gen&&delta==0,"roof queue/cache/queries have zero steady-state allocations");
   Console.WriteLine("six-merc shelter adapter: "+(timer.Elapsed.TotalMilliseconds/100000).ToString("0.000000")+" ms/step; heap delta "+delta+" bytes; gen0 delta "+(GC.CollectionCount(0)-gen));
  }
  public static int Main(){try{Orders();Guns();Geometry();ClockAndPerf();FairBudget();Console.WriteLine("PASS: "+N+" production assertions");return 0;}catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}}
 }
}
'''


def main():
    mercs = read('Revival.Mercs.cs')
    stations = read('Revival.MercStations.cs')
    support = SUPPORT.replace('FOR_SOURCE', method(mercs, 'internal MercOrder For('))
    support = support.replace('GIVE_SOURCE', method(mercs, 'static void Give('))
    support = support.replace('STATION_SOURCE', method(stations, 'internal static void GiveStation('))
    field, _ = m1.greybox('all', 'unity/EastTile/Content/east_airfield.json')
    c1, _, _ = m1.c1_layout('all')
    source = m1.harness_source() + support + TEST.replace('ALL_LAYOUT', field + '\n' + c1)
    for path in ('Revival.MercRaidCore.cs', 'Revival.MercRaid.cs', 'Revival.AirKillCore.cs'):
        source += '\n' + '\n'.join(line for line in read(path).splitlines() if not line.startswith('using '))
    source = source.replace('public static Vector3 zero', 'public static Vector3 forward {get{return new Vector3(0,0,1);}}\n        public static Vector3 zero', 1)
    source = source.replace('public static float Max(float a', 'public static int Max(int a,int b){return a>b?a:b;}\n        public static float Max(float a', 1)
    work = ROOT / 'build' / 'merc_raid_orders_check'
    work.mkdir(parents=True, exist_ok=True)
    cs, exe = work / 'check.cs', work / 'check.exe'
    cs.write_text(source, encoding='utf-8')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    result = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/codepage:65001',
                             '/main:NextDayRevival.RaidTest', '/out:' + str(exe), str(cs)], capture_output=True)
    if result.returncode:
        print(result.stdout.decode('utf-8', 'replace'))
        return 1
    result = subprocess.run([str(exe)], capture_output=True)
    print(result.stdout.decode('utf-8', 'replace').strip())
    if result.returncode:
        return 1
    orders = read('Revival.MercOrders.cs')
    assert 'RaidReceived(r);' in method(orders, 'static void OrderReceived(')
    assert 'Mercs.RaidWarning(at, siren);' in read('Revival.AirEvents.cs')
    assert 'TowerSupport.FromMaster(sender)' in read('Revival.AirEvents.cs')
    assert 'case 11: Mercs.OrderRaidCover();' in read('Revival.MercsUi.cs')  # sector 11 after M5b/medic joined the wheel
    assert 'u.RaidCover = r.Raid.Cover == order;' in mercs
    assert 'n == 10 ? KeyCode.Alpha0' in read('Revival.MercQuickOrders.cs')
    assert 'case 6: Mercs.OrderRaidCover();' in read('Revival.MercPage.cs')
    assert 'case 7: Mercs.OrderRaidGuns();' in read('Revival.MercPage.cs')
    assert 'MercRaidCover.Sheltered(u, me, now)' in read('Revival.MercCover.cs')
    assert 'AirKills.Sheltered(at, at + Vector3.right * 280f)' in read('Revival.MercCover.cs')
    raid = read('Revival.MercRaid.cs')
    assert '_nextQuery = now + 0.5f' in raid
    assert 'Physics.' not in raid and 'FindObjects' not in raid and 'GetComponents' not in raid
    prof = read('RevivalFrameProfiler.cs')
    count = int(re.search(r'public const int Count = (\d+)', prof).group(1))
    names = re.findall(r'"([^"]+)"', method(prof, 'static readonly string[] Names'))
    assert len(names) == count
    # slot numbers move when branches are composed; follow the constants
    orders = int(re.search(r'public const int S_MercRaidT = (\d+);', prof).group(1))
    shelter = int(re.search(r'public const int S_MercRaidCoverT = (\d+);', prof).group(1))
    assert names[orders] == '  MercRaid.Orders.Sub' and names[shelter] == '  MercRaid.Shelter.Sub'
    print('PASS: common acknowledgement, master warning, both UI paths, key 0, M1/M2 shelter and F6 wiring')
    return 0


if __name__ == '__main__':
    sys.exit(main())

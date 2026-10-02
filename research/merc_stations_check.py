"""Y S4: compile production allocator, order codec and master lease rules offline.

Unity/Photon adapters are deterministic fakes, not an in-game acceptance claim.
No game process, installation or network is used.
"""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def block(source, anchor):
    start = source.index(anchor)
    at = source.index('{', start)
    end, depth = at + 1, 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Diagnostics;
using UnityEngine;
using NextDayRevival;
namespace UnityEngine {
 public class Component { public Transform transform=new Transform(); public bool Up=true; public int Owner; public float Health=1; }
 public class Transform { public Vector3 position; public Quaternion rotation; public Vector3 TransformPoint(Vector3 p){return position+p;} }
 public struct Quaternion { public static Quaternion identity {get{return new Quaternion();}} }
 public class NavMeshAgent {} public class Animation {} public class Animator {} public class Renderer {}
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get{return new Vector3();}} public static Vector3 up {get{return new Vector3(0,1,0);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}}
  public static Vector3 operator+(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator-(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator*(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
 }
 public static class Mathf {
  public static int RoundToInt(float v){return (int)Math.Round(v);} public static int Max(int a,int b){return Math.Max(a,b);}
  public static int Clamp(int a,int b,int c){return Math.Max(b,Math.Min(c,a));}
  public static float Clamp(float a,float b,float c){return Math.Max(b,Math.Min(c,a));}
 }
 public static class Time {public static float time;}
}
namespace NextDayRevival {
 ORDER
 POST
 class MercUnit {public MercOrder Order=new MercOrder();public bool AARetreat,GroundPhase;}
 static class MercCrewPhases {public static bool Ground(MercUnit u){return u.GroundPhase;}}
 static class Mercs {
  public static bool Any=true;public static string SideOf(int s){return "side";}
  public static Component LocalBody;public static MercUnit LocalUnit;
  public static MercUnit UnitOf(Component c){return c==LocalBody?LocalUnit:null;}
 }
 static class Flak {
  public class Gun {public int Index;public Transform SeatGunner=new Transform(),SeatLoader=new Transform(),Mount=new Transform(); public Component Gunner,Loader;public float ClaimedUntil;}
  public static Gun _manned;public static float? CfgSeatDrop=null;public const float K=2.8f;
  public static Gun[] guns=new Gun[7];public static Gun ByIndex(int i){return i>=0&&i<guns.Length?guns[i]:null;}
  public static bool Up(Component c){return c!=null&&c.Up;}
 }
 static class AirDefenceDamage {public static bool Alive(int i){return true;}}
 static class TowerRadar {public static bool Built=true,Working=true;public static int OperatorActor=-1;public static Transform ConsoleRoot=new Transform();public static int PlayerSide(int a){return 2;}}
 static class RadarOperator {public static bool Alive;}
 static class Mortar {
  public static bool MercPose(int p,out Vector3 at,out Quaternion rot){at=new Vector3(20,0,0);rot=Quaternion.identity;return p==100||p==101;}
  public static void MercPrepare(int p){}
  public static bool MercAvailable(int p){return p==100||p==101;}
 }
 static class GepardCrew {public static NavMeshAgent Agent(Component c){return null;}}
 static class NpcWar {public static float MercHealthForPost(Component c){return c.Health;}}
 static class MercRide {public static float[] Last;public static void SendAAPacket(float[] p){Last=p;}}
 static class MercAA {
  public const int Radar=4;public static bool Authority=true;public static int Actor=1;
  static int LocalActor(){return Actor;}static int MasterActor(){return 1;}
  static float _nextTick,_nextSend;static int _master=1;static bool _sent;
  static readonly MercAAPost[] Posts=MakePosts();static readonly float[] Snapshot=new float[1+46*5];
  public static Dictionary<int,Component> Bodies=new Dictionary<int,Component>();
  static Component Resolve(int view,int actor){Component c;return Bodies.TryGetValue(view,out c)&&c.Owner==actor?c:null;}
  static void Clear(MercAAPost p){p.Actor=-1;p.View=0;p.Ai=null;p.Until=0;}
  RULES
 }
}
class Check {
 static int failures,checks;
 static void Ok(bool yes,string why){checks++;if(!yes){failures++;Console.WriteLine("FAIL "+why);}}
 static void Packet(int seat,int view,int actor){MercAA.OnPacket(new float[]{2,seat,view,30,0},actor);}
 static void Codec(int post,float z,int mode) {
  MercOrder o=new MercOrder();o.Mode=mode;o.Scene="East";o.Points=new Vector3[]{new Vector3(1,2,3)};o.Facing=new Vector3(post,0,z);
  MercOrder d=MercOrder.Decode(o.Encode());Ok(d.Mode==mode&&d.Facing.x==post&&d.Facing.z==z,"station order roundtrip "+post);
 }
 static int Main() {
  // Mixed battery: two seats each in two AA guns, technical gun and passenger,
  // Gepard three crew places, MTW gun, artillery two stations, radar separately.
  MercStationChoice[] seats=new MercStationChoice[13];
  int[] groups={1,1,2,2,-10,-10,-11,-11,-11,-12,100,100,5};
  for(int i=0;i<seats.Length;i++){seats[i].Group=groups[i];seats[i].Distance=(i+1)*(i+1)*10;seats[i].Free=true;}
  bool[] used=new bool[13];
  for(int i=0;i<12;i++){int p=MercStationPlan.Take(seats,12,0);Ok(p>=0&&!used[p],"all selected mercs get distinct seats");used[p]=true;}
  Ok(MercStationPlan.Take(seats,12,0)==-1,"overflow cannot duplicate a seat");
  for(int i=0;i<12;i++)Ok(used[i],"fill BOTH seats and multiple mixed guns");
  for(int i=0;i<13;i++)seats[i].Used=false;
  Ok(MercStationPlan.Take(seats,12,100)==10&&MercStationPlan.Take(seats,12,100)==11,"explicit gun fills its two seats first");
  Ok(MercStationPlan.Take(seats,12,100)==0,"explicit gun then spills to other free guns");
  for(int i=0;i<13;i++){seats[i].Used=false;seats[i].Free=i==12;}
  Ok(MercStationPlan.Take(seats,13,5)==12,"radar can be picked without aiming");
  for(int i=0;i<13;i++)seats[i].Free=false;
  seats[0].Free=true;seats[0].Distance=168*168+1;
  Ok(MercStationPlan.Take(seats,13,0)==-1,"60 m uses 2.8 units/metre");
  for(int i=0;i<13;i++){seats[i].Free=true;seats[i].Distance=10;seats[i].Used=false;}
  seats[0].Free=false;Ok(MercStationPlan.Take(seats,13,0)==1,"occupied/player-reserved seats skipped");
  Ok(MercStationPlan.Retreat(.34f,false,false)&&!MercStationPlan.Retreat(.35f,false,false),"leave below 35 percent");
  Ok(MercStationPlan.Retreat(.49f,true,false)&&!MercStationPlan.Retreat(.50f,true,false),"retreat hysteresis until 50 percent");
  Ok(MercStationPlan.Retreat(1,false,true),"imminent blast uses M1-M3 safety");
  for(int p=1;p<=14;p++)if(p!=5&&p!=12)Codec(p,0,MercOrder.ManGun);
  Codec(5,0,MercOrder.ManRadar);Codec(-12345,3,MercOrder.ManGun);
  int tube=MercStationPlan.TubeKey(4567,-9876);
  Codec(tube+1,0,MercOrder.ManGun);Codec(tube+2,0,MercOrder.ManGun);
  Ok(tube>=100&&tube+1<16777216&&tube%2==0,"stable tube IDs exactly representable in Photon floats");
  Ok(MercOrder.Decode("gun~East~0~1~25~1,2,3~-123,0").Mode==MercOrder.Follow,"invalid vehicle seat rejected");
  Ok(MercOrder.Decode("gun~East~0~1~25~1,2,3~5,0").Mode==MercOrder.Follow,"radar identity cannot become gun");
  for(int i=0;i<7;i++)if(i!=4){Flak.guns[i]=new Flak.Gun();Flak.guns[i].Index=i;}
  for(int i=1;i<=8;i++)MercAA.Bodies[i]=new Component();
  MercAA.Bodies[1].Owner=1;MercAA.Bodies[2].Owner=2;MercAA.Bodies[3].Owner=1;
  Packet(0,1,1);Packet(7,3,1);
  Ok(MercAA.Held(0).View==1&&MercAA.Held(7).View==3,"master accepts distinct gunner + loader on same gun");
  Packet(0,2,2);Ok(MercAA.Held(0).View==1,"cross-owner race preserves first live lease");
  Packet(1,2,1);Ok(MercAA.Held(1)==null,"foreign Photon body refused");
  MercAA.Bodies[2].Health=.2f;Packet(1,2,2);Ok(MercAA.Held(1)==null,"master refuses wounded crew");
  MercAA.Bodies[2].Health=1;MercAA.Bodies[2].transform.position=new Vector3(100,0,0);
  Packet(1,2,2);Ok(MercAA.Held(1)==null,"remote body cannot grab gun from afar");
  MercAA.Bodies[2].transform.position=Vector3.zero;Packet(1,2,2);Ok(MercAA.Held(1).View==2,"second gun filled concurrently");
  Flak.guns[0].Gunner=new Component();Ok(!MercAA.CanApproach(0,null),"native gunner reserves own seat");
  Ok(MercAA.CanApproach(8,null),"other gun loader remains free");
  Flak._manned=Flak.guns[1];Ok(!MercAA.CanApproach(8,null),"player has priority over both crew places");Flak._manned=null;
  MercAA.Bodies[4].Owner=2;MercAA.Bodies[4].transform.position=new Vector3(20,0,0);Packet(100,4,2);
  MercAA.Bodies[5].Owner=1;MercAA.Bodies[5].transform.position=new Vector3(20,0,0);Packet(101,5,1);
  Ok(MercAA.Held(100)!=null&&MercAA.Held(101)!=null,"both artillery stations accepted independently");
  Packet(-1,4,2);Ok(MercAA.Held(100)==null&&MercAA.Held(101)!=null,"release only affects sender's own body");
  MercAA.Tick();Time.time=2;MercAA.Tick();Ok(MercAA.Held(1)==null&&MercAA.Held(7)==null,"expired/dead leases reopen seats");
  Ok(MercRide.Last!=null&&MercRide.Last.Length==231&&MercRide.Last[0]==6,"master publishes explicit station identities to peers");
  Time.time=3;Flak.guns[0].Gunner=null;MercAA.Bodies[1].transform.position=Vector3.zero;
  MercAA.OnPacket(new float[]{2,0,1,30,1},1);
  Ok(MercAA.Held(0)!=null&&MercAA.Held(0).Peaceful,"peaceful crews execute orders but retain hold-fire");
  Packet(-1,1,1);MercAA.OnPacket(new float[]{2,.2f,1,30,0},1);
  Ok(MercAA.Held(0)==null,"fractional/malformed station identity refused");
  float[] remote=new float[231];remote[0]=6;
  for(int i=0;i<46;i++)remote[1+i*5+1]=-1;
  remote[1]=1;remote[2]=2;remote[3]=2;remote[4]=30;remote[5]=2;
  MercAA.Authority=false;
  MercAA.OnPacket(remote,2);Ok(MercAA.Held(1)==null,"non-master snapshot cannot claim crew");
  MercAA.OnPacket(remote,1);Ok(MercAA.Held(1)!=null&&MercAA.Held(1).Actor==2,"master snapshot places remote crew by stable ID");
  Mercs.LocalBody=MercAA.Bodies[2];Mercs.LocalUnit=new MercUnit();
  Mercs.LocalUnit.Order.Mode=MercOrder.ManGun;Mercs.LocalUnit.Order.Facing=new Vector3(2,0,0);
  Mercs.LocalUnit.GroundPhase=true;MercAA.OnPacket(remote,1);
  Ok(MercAA.Held(1)==null,"stale master seat snapshot cannot repark dismounted local ground crew");
  Mercs.LocalUnit.GroundPhase=false;MercAA.OnPacket(remote,1);
  Ok(MercAA.Held(1)!=null,"fresh air phase accepts master lease again");
  Mercs.LocalBody=null;Mercs.LocalUnit=null;
  Ok(MercOrder.Decode("gun~East~0~1~25~1,2,3~50,0").Mode==MercOrder.Follow,"unregistered fixed post rejected");
  // Stable working buffers: no heap traffic or GC over one million allocations.
  for(int i=0;i<13;i++){seats[i].Free=true;seats[i].Distance=i;}
  for(int warm=0;warm<1000;warm++){for(int i=0;i<13;i++)seats[i].Used=false;MercStationPlan.Take(seats,13,0);}
  Stopwatch sw=new Stopwatch();sw.Start();sw.Stop();
  GC.Collect();int gen=GC.CollectionCount(0);long before=GC.GetTotalMemory(false);
  sw.Reset();sw.Start();
  for(int n=0;n<1000000;n++){for(int i=0;i<13;i++)seats[i].Used=false;MercStationPlan.Take(seats,13,0);}
  sw.Stop();long delta=GC.GetTotalMemory(false)-before;
  Ok(GC.CollectionCount(0)==gen&&delta<=4096,"allocator steady loop has zero managed allocations/collections");
  Console.WriteLine("allocator: "+(sw.Elapsed.TotalMilliseconds/1000000*1000).ToString("0.000",CultureInfo.InvariantCulture)+" us/call; heap delta "+delta+" bytes; gen0 delta "+(GC.CollectionCount(0)-gen));
  Console.WriteLine("PASS scenarios: mixed guns, full crews, picker priority, radar, range, occupancy, order codec, master authority, HP, expiry, broadcast");
  Console.WriteLine("RESULT: "+(failures==0?"PASS":"FAIL")+" ("+checks+" assertions)");return failures==0?0:1;
 }
}
'''


def main():
    aa = (ROOT / 'Revival.MercAA.cs').read_text(encoding='utf-8')
    order = block((ROOT / 'Revival.Mercs.cs').read_text(encoding='utf-8'), 'internal sealed class MercOrder')
    post = block(aa, 'internal sealed class MercAAPost')
    anchors = ['static MercAAPost[] MakePosts', 'internal static MercAAPost Gun(',
               'internal static MercAAPost Held(', 'static MercAAPost Slot(',
               'static MercAAPost Live(', 'internal static bool IsOrder(',
               'internal static bool IsVehicle(', 'internal static bool Pose(',
               'static bool Available(', 'internal static bool CanApproach(',
               'internal static int PostOf(', 'internal static void OnPacket(',
               'internal static void Tick(']
    rules = '\n'.join(block(aa, anchor) for anchor in anchors)
    work = ROOT / 'build' / 'merc_stations_check'
    work.mkdir(parents=True, exist_ok=True)
    harness = work / 'Harness.cs'
    harness.write_text(HARNESS.replace('ORDER', order).replace('POST', post).replace('RULES', rules), encoding='ascii')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    exe = work / 'Check.exe'
    args = [str(compiler), '/nologo', '/warn:0', '/out:' + str(exe), str(harness), str(ROOT / 'Revival.MercStationsCore.cs')]
    built = subprocess.run(args, capture_output=True, text=True)
    if built.returncode:
        print(built.stdout + built.stderr)
        return 1
    checked = subprocess.run([str(exe)], text=True, capture_output=True)
    print(checked.stdout + checked.stderr, end='')
    # Adapter and entry-point contracts, separate from fake-world simulation.
    ui = (ROOT / 'Revival.MercsUi.cs').read_text(encoding='utf-8')
    issue = block(ui, 'static void Issue(')
    gun_sector = issue[issue.index('case 6:'):issue.index('case AttackSector:')]
    assert 'CrosshairPoint' not in gun_sector and 'ToggleAirDefence()' in gun_sector
    assert 'OpenStations' not in ui and 'StationRows(r)' not in ui and 'Man air defence' in ui
    combat = (ROOT / 'Revival.NpcCombat.cs').read_text(encoding='utf-8')
    assert combat.index('MercStationDuty(f, s.Merc, now)') < combat.index('else if (MercFight(f, s.Merc, now))')
    stations = (ROOT / 'Revival.MercStations.cs').read_text(encoding='utf-8')
    adapters = (ROOT / 'Revival.MercStationsAdapters.cs').read_text(encoding='utf-8')
    assert 'FindObjectsOfType' not in stations + adapters
    assert 'MercRide.StepOwner(Time.time' in adapters and 'MercStationApproach' in adapters
    assert 'MercStationPlan.Retreat' in adapters and 'MercAA.Release(u); return false;' in adapters
    assert 'MercAA.Held(g.Index + 7)' in (ROOT / 'Revival.Flak.cs').read_text(encoding='utf-8')
    assert 'MercAA.Held(g.Index + 7)' in (ROOT / 'Revival.ShortRange.cs').read_text(encoding='utf-8')
    ride = (ROOT / 'Revival.MercsRide.cs').read_text(encoding='utf-8')
    relay = block(ride, 'public static void OnPhotonEvent(')
    assert 'd[0] == 6f' in relay and 'MercAA.OnPacket(d, sender)' in relay
    assert 'Resolve(view, sender)' in aa and 'sender == MasterActor()' in aa
    for name in ('MercStationDuty', 'MercStationApproach'):
        body = block(adapters, 'static ' + ('bool ' if name.endswith('Duty') else 'void ') + name)
        assert not any(bad in body for bad in ('new ', 'Physics.', 'GetComponent', 'FindObjects', 'ToString', '=>'))
    print('PASS source wiring: aim-free radial/list, immediate execution, cached M1 approach, M2-M3 retreat, both crew roles, Photon authority; no new tick/hot queries')
    return checked.returncode


if __name__ == '__main__':
    sys.exit(main())

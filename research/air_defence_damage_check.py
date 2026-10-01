#!/usr/bin/env python3
"""W AA7: compile the unchanged production core + adapter with deterministic
Unity/Photon doubles using C# 3 / CLR 3.5. No game, TEMP or server writes.
"""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build' / 'aa7-offline'
HARNESS = r'''using System;
using System.Collections.Generic;
using System.Reflection;
using NextDayRevival;
using UnityEngine;
namespace UnityEngine {
 public class Object {}
 public class Component : Object {public GameObject gameObject;public Transform transform {get{return gameObject.transform;}}}
 public class Transform : Component {public Vector3 position;public Quaternion localRotation;public T[] GetComponentsInChildren<T>(bool b){return new T[0];}}
 public class GameObject : Object {public bool activeInHierarchy=true;public Transform transform;public GameObject(){transform=new Transform();transform.gameObject=this;}}
 public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}public static Vector3 up {get{return new Vector3(0,1,0);}}public float sqrMagnitude {get{return x*x+y*y+z*z;}}public static float Distance(Vector3 a,Vector3 b){return (float)Math.Sqrt((a-b).sqrMagnitude);}public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}}
 public struct Vector4 {public float x,y,z,w;public Vector4(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}public static Vector4 zero {get{return new Vector4();}}}
 public struct Quaternion {public static Quaternion Euler(float a,float b,float c){return new Quaternion();}}
 public struct Color {public Color(float a,float b,float c){}}
 public class Material {public Color color;public Material(Shader s){}}
 public class Shader {public static Shader Find(string s){return new Shader();}}
 public class Renderer : Component {public Material[] sharedMaterials=new Material[0];}
 public struct Rect {public Rect(float a,float b,float c,float d){}}
 public static class Screen {public static int width=1280,height=720;}
 public static class GUI {public static void Label(Rect r,string s){}}
 public static class Time {public static float time;}
 public enum KeyCode {R}
 public static class Input {public static bool Held,Down;public static bool GetKey(KeyCode k){return Held;}public static bool GetKeyDown(KeyCode k){return Down;}}
 public static class Mathf {public static float Clamp(float f,float a,float b){return Math.Max(a,Math.Min(b,f));}public static int Max(int a,int b){return Math.Max(a,b);}public static int RoundToInt(float f){return (int)Math.Round(f);}}
}
namespace UnityEngine.SceneManagement {public struct Scene {public int buildIndex;}public static class SceneManager {public static Scene GetActiveScene(){Scene s=new Scene();s.buildIndex=1;return s;}}}
namespace HarmonyLib {
 public class Harmony {public void Patch(MethodInfo m,object a,object b,object c,object d,object e){}}
 public class HarmonyMethod {public HarmonyMethod(MethodInfo m){}}
 public static class AccessTools {public static MethodInfo PropertyGetter(Type t,string n){PropertyInfo p=t.GetProperty(n);return p==null?null:p.GetGetMethod();}public static FieldInfo Field(Type t,string n){return t.GetField(n,BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);}public static MethodInfo Method(Type t,string n,Type[] p,object g){return p==null?t.GetMethod(n):t.GetMethod(n,p);}}
}
namespace NextDayRevival {
 static class RevivalPlugin {public static Log L=new Log();public static Type TypeByName(string s){return null;}}
 class Log {public void LogWarning(string s){}}
 static class Crocodile {internal static bool Master=true;internal static int Actor=1;internal static Dictionary<int,GameObject> Players=new Dictionary<int,GameObject>();internal static bool IsMaster(){return Master;}internal static GameObject PlayerByActor(int a){GameObject g;return Players.TryGetValue(a,out g)?g:null;}internal static bool PlayerUp(GameObject p){return p!=null&&p.activeInHierarchy;}internal static int LocalActor(){return Actor;}}
 static class MapTools {internal static GameObject LocalPlayer(){return Crocodile.PlayerByActor(Crocodile.Actor);}}
 static class Flak {internal static bool On=true;internal static Gun _manned;internal class Gun {public Transform Root,Cradle;public bool Reloading,CaseDue;public float ClaimedUntil,Recoil;public int ClaimActor,Seq,RemoteSeq;}internal static Gun[] Guns=new Gun[7];internal static Gun ByIndex(int id){return id<0||id>=7?null:Guns[id];}}
 static class FlakNet {internal static int Sent;internal static float[] Last;internal static void SendPacket(float[] p,bool r){Sent++;Last=(float[])p.Clone();}}
 static class FlakPlayer {internal static void Leave(string s){Flak._manned=null;}}
 static class FlakFire {internal static void Release(Flak.Gun g){}}
 static class TowerRadar {internal static bool On=true,Built=true;internal static Transform RadarRoot=new GameObject().transform,ConsoleRoot=new GameObject().transform;internal static int CfgRadarHits=30,CfgConsoleHits=12;internal static bool CfgDamageLook=true;internal static float RadarHp=1f,ConsoleHp=1f;internal static bool B(bool b){return b;}internal static int I(int v,int d){return v;}internal static void SetDamageHealth(float a,float b){RadarHp=a;ConsoleHp=b;}}
 static class RadarDamage {internal static float Radius(Component c){return 6f;}internal static void Blast(Vector3 at,float r,float peak){if(Vector3.Distance(at,TowerRadar.RadarRoot.position)<r)AirDefenceDamage.RadarHit(0,40f*peak);}}
 static class RadarScope {internal static bool InView;}
 static class MercAA {internal static int MasterActor(){return 1;}}
 static class GameUi {internal static bool WindowOpen;}
 static class NativeActionProgress {internal static bool Active;internal static bool IsActive(string s){return Active;}internal static bool Begin(string a,string b,float c,bool d,string e,string f){Active=true;return true;}internal static void End(string s){Active=false;}}
 static class RepairTap {internal static bool CanStart {get{return !NativeActionProgress.Active;}}}
 static class Loc {internal static string T(string a,string b){return b;}}
 static class ConvoyRepair {internal const int DEF_TOOLKIT=2064;internal static bool InVehicle(){return false;}}
 static class PlayerHeli {internal static bool Aboard;}
 static class PlayerAn2 {internal static bool Aboard;}
 static class Turret {internal static List<object> Inventories=new List<object>();internal static List<object> PlayerInventories(){return Inventories;}internal static void Hinweis(string s,float f){}}
}
struct Obscured {public int Value;public static implicit operator int(Obscured o){return o.Value;}public static implicit operator float(Obscured o){return o.Value;}public static implicit operator Obscured(int i){Obscured o=new Obscured();o.Value=i;return o;}}
class ToolSlots {public Obscured[] ItemID=new Obscured[]{10005};}
class Inventory {public ToolSlots _backpackData=new ToolSlots(),_gearsData=new ToolSlots(),_weaponsData=new ToolSlots();}
class Check {
 static int Bad,Checks;
 static void Ok(bool b,string s){Checks++;Console.WriteLine((b?"PASS ":"FAIL ")+s);if(!b)Bad++;}
 static AaDamageState[] States(){return (AaDamageState[])typeof(AirDefenceDamage).GetField("States",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
 static void Repair(int id,int actor,bool on){float[] p=new float[]{12,id,on?1:0,States()[id].Revision,0,0,0};AirDefenceDamage.OnPacket(p,actor);}
 static GameObject Player(int id,Vector3 p){GameObject g=new GameObject();g.transform.position=p;Crocodile.Players[id]=g;return g;}
 static void Core(){
  AaDamageState s=AaDamageCore.Fresh();
  Ok(s.Hp==1&&s.RepairActor==-1,"new asset operational");
  Ok(!AaDamageCore.Hit(ref s,float.NaN)&&!AaDamageCore.Hit(ref s,-1),"invalid damage rejected");
  AaDamageCore.Hit(ref s,.25f);Ok(s.Hp==.75f&&s.Revision==1,"partial damage + revision");
  AaDamageCore.Hit(ref s,1);Ok(s.Hp==0,"one direct AT/tank blast destroys");
  int rev=s.Revision;
  AaDamageCore.Pulse(ref s,1,rev,0,45,true);
  Ok(!AaDamageCore.Pulse(ref s,2,rev,.5f,45,true)&&s.RepairActor==1,"single repair owner");
  for(int i=1;i<90;i++)AaDamageCore.Pulse(ref s,1,rev,i*.5f,45,true);
  Ok(s.Hp==0&&s.Work==44.5f,"cannot finish before 45 host seconds");
  Ok(AaDamageCore.Pulse(ref s,1,rev,45,45,true)&&s.Hp==1,"45 s repair restores full operation");
  Ok(!AaDamageCore.Pulse(ref s,1,rev,46,45,true),"old completion revision cannot apply twice");
  AaDamageCore.Hit(ref s,1);rev=s.Revision;
  AaDamageCore.Pulse(ref s,1,rev,50,60,true);AaDamageCore.Pulse(ref s,1,rev,51,60,true);
  Ok(AaDamageCore.Hit(ref s,.1f)&&s.Work==0&&s.RepairActor==-1,"damage on wreck interrupts work");
  rev=s.Revision;AaDamageCore.Pulse(ref s,1,rev,52,60,true);
  Ok(AaDamageCore.Expire(ref s,54,true)&&s.RepairActor==-1,"disconnect heartbeat lease expires");
  AaDamageCore.Pulse(ref s,1,rev,55,60,true);
  Ok(AaDamageCore.Expire(ref s,55.5f,false),"death/range/tool eligibility cancels");
  AaDamageCore.Pulse(ref s,1,rev,60,60,true);AaDamageCore.Pulse(ref s,1,rev,1000,60,true);
  Ok(s.Hp==0&&s.Work==0,"long interruption cannot fast-forward repair");
  AaDamageCore.Cancel(ref s);AaDamageCore.Pulse(ref s,2,rev,1001,60,true);
  for(int i=1;i<=120;i++)AaDamageCore.Pulse(ref s,2,rev,1001+i*.5f,60,true);
  Ok(s.Hp==1,"radar 60 s tools/time restore");
  AaDamageState peer=AaDamageCore.Fresh();
  Ok(AaDamageCore.Snapshot(ref peer,0,12,-1,0,60)&&!AaDamageCore.Snapshot(ref peer,1,11,-1,0,60),"late join and stale state rejection");
  Ok(!AaDamageCore.Snapshot(ref peer,float.NaN,13,-1,0,60)&&!AaDamageCore.Snapshot(ref peer,2,13,-1,0,60),"malformed HP rejected");
  Ok(AaDamageCore.Blast(1.7f*2.8f,6,2*2.8f,1)==1&&AaDamageCore.Blast(20,6,2*2.8f,1)==0,"ground/direct hit lethal, distant blast harmless (2.8 u/m)");
  Ok(AaDamageCore.Blast(9,6,2*2.8f,1)>0&&AaDamageCore.Blast(9,6,2*2.8f,1)<1,"near miss falloff");
  var watch=System.Diagnostics.Stopwatch.StartNew();
  int gc=GC.CollectionCount(0);
  for(int i=0;i<1000000;i++){AaDamageCore.Expire(ref peer,i*.016f,true);AaDamageCore.Blast(20,6,5.6f,1);}
  watch.Stop();Ok(GC.CollectionCount(0)==gc,"million steady core ticks: zero Gen0 collections");
  Console.WriteLine("CORE benchmark: "+(watch.Elapsed.TotalMilliseconds/1000000).ToString("F6")+" ms/iteration (not Unity F6)");
 }
 static void Adapter(){
  Time.time=10;Player(1,new Vector3(0,0,0));Player(2,new Vector3(0,0,0));
  Flak.Guns[0]=new Flak.Gun();Flak.Guns[0].Root=new GameObject().transform;Flak.Guns[0].Cradle=Flak.Guns[0].Root;
  TowerRadar.RadarRoot.position=new Vector3(100,0,0);TowerRadar.ConsoleRoot.position=new Vector3(100,34,0);
  AirDefenceDamage.Tick();
  Flak._manned=Flak.Guns[0];AirDefenceDamage.ReportBlast(new Vector3(0,0,0),6,1);
  Ok(!AirDefenceDamage.Alive(0)&&Flak._manned==null,"production adapter destroys gun and ejects gunner");
  Ok(FlakNet.Last[0]==11&&FlakNet.Last[2]==0,"host publishes reliable HP/revision snapshot");
  Time.time=11;Repair(0,1,true);Repair(0,2,true);Ok(States()[0].RepairActor==1,"host refuses competing repair actor");
  Repair(0,2,false);Ok(States()[0].RepairActor==1,"foreign cancel cannot clear owner");
  for(int i=1;i<90;i++){Time.time=11+i*.5f;Repair(0,1,true);}
  Ok(!AirDefenceDamage.Alive(0),"adapter enforces repair duration");
  Time.time=56;Repair(0,1,true);Ok(AirDefenceDamage.Alive(0),"adapter repair completes on host");
  Time.time=57;AirDefenceDamage.ReportBlast(new Vector3(0,0,0),6,1);Repair(0,1,true);
  Crocodile.Players[1].transform.position=new Vector3(50,0,0);Time.time=57.5f;AirDefenceDamage.Tick();
  Ok(States()[0].RepairActor==-1,"moving out of reach cancels host repair");
  Repair(0,1,true);Ok(States()[0].RepairActor==-1,"remote actor outside range cannot begin");
  Crocodile.Players[1].transform.position=new Vector3();Crocodile.Players[1].activeInHierarchy=false;
  Repair(0,1,true);Ok(States()[0].RepairActor==-1,"dead actor cannot repair");
  Crocodile.Players[1].activeInHierarchy=true;
  Time.time=59;AirDefenceDamage.ReportBlast(new Vector3(100,0,0),6,1);
  Ok(TowerRadar.RadarHp==0&&!AirDefenceDamage.Alive(7),"native/mod blast routes radar to dark/by-eye health");
  int rev=States()[7].Revision;AirDefenceDamage.ReportBlast(new Vector3(100,0,0),6,1);
  Ok(States()[7].Revision==rev,"duplicate impact not billed twice");
  Crocodile.Master=false;AirDefenceDamage.OnPacket(new float[]{11,0,1,100,-1,0,0},2);
  Ok(!AirDefenceDamage.Alive(0),"peer rejects forged non-master snapshot");
  AirDefenceDamage.OnPacket(new float[]{11,0,.5f,100,-1,0,0},1);
  Ok(AirDefenceDamage.Hp(0)==.5f,"peer applies master snapshot");
  Time.time=60;AirDefenceDamage.Tick();Crocodile.Master=true;Time.time=61;AirDefenceDamage.Tick();
  Ok(AirDefenceDamage.Hp(0)==.5f&&!AirDefenceDamage.Alive(7)&&States()[0].RepairActor==-1,"host migration preserves damage, clears repair lease");
  Inventory inv=new Inventory();Turret.Inventories.Add(inv);
  MethodInfo tools=typeof(AirDefenceDamage).GetMethod("Tools",BindingFlags.NonPublic|BindingFlags.Static);
  Ok((bool)tools.Invoke(null,null),"production emitted reader recognises toolkit ObscuredInt");
  inv._backpackData.ItemID[0]=0;inv._gearsData.ItemID[0]=0;inv._weaponsData.ItemID[0]=0;
  Ok(!(bool)tools.Invoke(null,null),"cached reader detects toolkit removal without stale item cache");
  inv._backpackData.ItemID=new Obscured[]{2064};Ok((bool)tools.Invoke(null,null),"replacement inventory array and heavy toolkit accepted");
  AirDefenceDamage.Reset(0,8);Ok(AirDefenceDamage.Alive(0)&&AirDefenceDamage.Alive(7),"world unload resets damage state");
  Time.time=65;AirDefenceDamage.ReportBlast(new Vector3(),6,1);Input.Held=true;Input.Down=true;AirDefenceDamage.Tick();Input.Down=false;
  Ok(AirDefenceDamage.Repairing&&NativeActionProgress.Active&&States()[0].RepairActor==1,"tap R acquires native HUD and host repair lease");
  inv._backpackData.ItemID[0]=0;Time.time=65.5f;AirDefenceDamage.Tick();
  Ok(!AirDefenceDamage.Repairing&&!NativeActionProgress.Active&&States()[0].RepairActor==-1,"tool loss ends native action and host lease");
  inv._backpackData.ItemID[0]=10005;Time.time=66;AirDefenceDamage.Tick();
  Ok(!AirDefenceDamage.Repairing,"cancelled action waits for key release before restarting");
  Input.Held=false;Time.time=66.5f;AirDefenceDamage.Tick();Input.Held=true;Input.Down=true;Time.time=67;AirDefenceDamage.Tick();Input.Down=false;
  Ok(AirDefenceDamage.Repairing,"fresh tap permits repair with retained tools");
  Input.Held=false;Time.time=67.1f;AirDefenceDamage.Tick();
  Ok(AirDefenceDamage.Repairing,"releasing R keeps repair running");
  NativeActionProgress.Active=false;Time.time=67.2f;AirDefenceDamage.Tick();
  Ok(!AirDefenceDamage.Repairing&&States()[0].Work==0,"shared cancellation immediately resets host work");
  Input.Down=true;Time.time=67.3f;AirDefenceDamage.Tick();Input.Down=false;Time.time=67.4f;AirDefenceDamage.Tick();
  Time.time=67.5f;AirDefenceDamage.Tick();
  Ok(AirDefenceDamage.Repairing,"short tap between 2 Hz scan ticks is retained");
 }
 public static int Main(){Core();Adapter();Console.WriteLine("AA7 RESULT: "+Checks+" checks, "+Bad+" failures");return Bad==0?0:1;}
}
'''

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    harness = OUT / 'Harness.cs'
    harness.write_text(HARNESS, encoding='ascii')
    compiler = Path(r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe')
    exe = OUT / 'check.exe'
    r = subprocess.run([str(compiler), '/nologo', '/utf8output', '/codepage:65001', '/langversion:Default', '/target:exe',
                        '/out:' + str(exe), str(ROOT / 'Revival.AirDefenceDamageCore.cs'),
                        str(ROOT / 'Revival.AirDefenceDamage.cs'), str(harness)],
                       text=True, encoding="utf-8", errors="replace", capture_output=True)
    if r.returncode:
        print(r.stdout + r.stderr)
        return r.returncode
    r = subprocess.run([str(exe)], text=True, encoding="utf-8", errors="replace", capture_output=True)
    print(r.stdout, end='')
    if r.returncode:
        print(r.stderr)
        return r.returncode
    # Check seams that doubles intentionally do not implement (actual gun,
    # native explosion, radar tier and Photon receiver/profiler integration).
    contracts = {
        'Revival.Flak.cs': ['Destroyed = 6', 'if (!AirDefenceDamage.Alive(g.Index)) return;',
                            'g == null || !AirDefenceDamage.Alive(index)',
                            'AirDefenceDamage.OnPacket(f, sender)', 'PrepareSender();', '_send((byte)Code(), data, false, _options)'],
        'Revival.MercAA.cs': ['g != null && AirDefenceDamage.Alive(g.Index)'],
        'Revival.NoFly.cs': ['info != null && info.Health > 0f'],
        'Revival.TowerRadar.cs': ['if (!Working) tier = 0;', 'Flak.SetRadarDirected(tier == 2)',
                                 'AirfieldOwnership.MasterSender(sender)', 'AirDefenceDamage.RadarHit',
                                 'dead == _screenDead'],
        'RevivalMortar.cs': ['if (shooter) AirDefenceDamage.ReportBlast'],
        'RevivalPlugin.cs': ['S_AaDamageT); AirDefenceDamage.Tick()', 'AirDefenceDamage.Draw(); FrameProf.E(FrameProf.S_AaDamageD)'],
        'sync_public.py': ['Revival.AirDefenceDamageCore.cs', 'research/air_defence_damage_check.py'],
    }
    for f, needles in contracts.items():
        s = (ROOT / f).read_text(encoding='utf-8')
        for n in needles:
            assert n in s, (f, n)
    radar = (ROOT / 'Revival.TowerRadar.cs').read_text(encoding='utf-8')
    assert '_radarDeadAt' not in radar and '_consoleDeadAt' not in radar, 'no automatic repair'
    print('PASS production integration contracts; no automatic repair; F6 and public sync wired')
    return 0

if __name__ == '__main__':
    sys.exit(main())

"""W AA6: compile unchanged production core, radar adapter and flare authority.

Unity/Photon are deterministic test doubles; terrain is x-major. No game or
TEMP access. Timings are not Unity/F6 measurements. Verbose output is small.
"""
from pathlib import Path
import os
import subprocess

ROOT = Path(__file__).resolve().parents[1]
HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using NextDayRevival;
using UnityEngine;
namespace UnityEngine {
 public class Object { public static void Destroy(object o) {} }
 public class Component { public GameObject gameObject; public Transform transform {get{return gameObject.transform;}} }
 public class Transform { public Vector3 position,localScale; public Vector3 forward= new Vector3(0,0,1),right=new Vector3(1,0,0); }
 public class GameObject { public int View; public string name; public Transform transform=new Transform(); public bool activeSelf=true; public void SetActive(bool b){activeSelf=b;} public T GetComponent<T>() where T:new(){return new T();} public static GameObject CreatePrimitive(PrimitiveType t){return new GameObject();} }
 public enum PrimitiveType { Sphere }
 public class Collider {}
 public class Renderer { public Material sharedMaterial; }
 public class Shader {public static Shader Find(string s){return new Shader();}}
 public class Material { public Color color; public Material(Shader s){} }
 public struct Color {public Color(float a,float b,float c,float d){} }
 public static class Time {public static float time;}
 public static class Application {public static string loadedLevelName="test";}
 public static class Mathf {public static int Min(int a,int b){return Math.Min(a,b);} public static float Max(float a,float b){return Math.Max(a,b);} public static int Clamp(int a,int l,int h){return Math.Max(l,Math.Min(h,a));} public static float Clamp(float a,float l,float h){return Math.Max(l,Math.Min(h,a));}}
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get{return new Vector3();}} public static Vector3 up {get{return new Vector3(0,1,0);}} public static Vector3 down {get{return new Vector3(0,-1,0);}} public static Vector3 one {get{return new Vector3(1,1,1);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}} public float magnitude {get{return (float)Math.Sqrt(sqrMagnitude);}} public Vector3 normalized {get{return magnitude>0?this*(1/magnitude):zero;}}
  public static float Dot(Vector3 a,Vector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);} public static Vector3 operator -(Vector3 a,Vector3 b){return a+b*(-1);} public static Vector3 operator *(Vector3 a,float s){return new Vector3(a.x*s,a.y*s,a.z*s);}
 }
 public struct Ray {public Vector3 origin,direction; public Ray(Vector3 p,Vector3 d){origin=p;direction=d;}}
 public struct RaycastHit {}
 public class TerrainData {public Vector3 size=new Vector3(2800,2800,2800);}
 public class Terrain {
  public static Terrain[] activeTerrains;
  public TerrainData terrainData=new TerrainData(); public bool drawHeightmap=true;
  public Vector3 origin; public float[,] heights=new float[101,101]; public TerrainCollider collider;
  public Terrain(){collider=new TerrainCollider();collider.terrain=this;}
  public Vector3 GetPosition(){return origin;}
  public T GetComponent<T>() where T:class{return collider as T;}
  public float SampleHeight(Vector3 p){int x=Math.Max(0,Math.Min(100,(int)((p.x-origin.x)/28f)));int z=Math.Max(0,Math.Min(100,(int)((p.z-origin.z)/28f)));return heights[x,z];}
 }
 public class TerrainCollider {
  public Terrain terrain; public static int Calls;
  public bool Raycast(Ray r,out RaycastHit hit,float length){Calls++;hit=new RaycastHit();for(int i=1;i<=4096;i++){Vector3 p=r.origin+r.direction*(length*i/4096f);if(p.x<terrain.origin.x||p.z<terrain.origin.z||p.x>terrain.origin.x+2800||p.z>terrain.origin.z+2800)continue;if(p.y<=terrain.origin.y+terrain.SampleHeight(p))return true;}return false;}
 }
}
namespace UnityEngine.SceneManagement {
 public struct Scene {public int buildIndex;}
 public static class SceneManager {public static int sceneCount=1;public static Scene GetActiveScene(){Scene s=new Scene();s.buildIndex=1;return s;}}
}
namespace NextDayRevival {
 static class NpcAircraft {
  internal class Flight {internal Path Path;}
  internal class Path {internal float Agl;}
  internal static Flight Find(UnityEngine.GameObject go){return null;}
 }
 static class FrameProf {internal const int S_RadarShadowT=0,S_Mi8FlaresT=1;internal static void S(int s){} internal static void E(int s){} }
 static class GepardGun {internal class Contact {internal GameObject Go;internal Vector3 Pos;}}
 static class PlayerAn2 {internal static int View(GameObject go){return go.View;}}
 static class PlayerHeli {internal static GameObject FlownMachine;internal static List<GameObject> All=new List<GameObject>();internal static GameObject MissileTarget(int v){for(int i=0;i<All.Count;i++)if(All[i].View==v)return All[i];return null;}internal static Vector3 CabinSeatWorld(GameObject g,int n){return g.transform.position;}}
 static class MercAA {internal static bool Authority;}
 static class Mercs {internal static int LocalActor=1;}
 static class Crocodile {internal static GameObject Player;internal static GameObject PlayerByActor(int a){return a==1?Player:null;}internal static bool PlayerUp(GameObject p){return p!=null&&p.activeSelf;}}
 static class RevivalTroopInsertion {internal static List<GameObject> All=new List<GameObject>();internal static void TroopHelis(List<GameObject> l){l.AddRange(All);}}
 static class MapTools {internal static GameObject LocalPlayer(){return Crocodile.Player;}}
 static class Loc {internal static string T(string ru,string en){return en;}}
 static class Stinger {internal static int Packets;internal static void CountermeasurePacket(int a,int b,int c,Vector3 d,Vector3 e){Packets++;}}
 static class FastField {
  static Dictionary<Type,Dictionary<string,FieldInfo>> cache=new Dictionary<Type,Dictionary<string,FieldInfo>>();
  internal static FieldInfo Find(Type t,string n){Dictionary<string,FieldInfo> d;if(!cache.TryGetValue(t,out d)){d=new Dictionary<string,FieldInfo>();cache[t]=d;}FieldInfo f;if(!d.TryGetValue(n,out f)){f=t.GetField(n,BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);d[n]=f;}return f;}
 }
}
namespace HarmonyLib {static class AccessTools {internal static FieldInfo Field(Type t,string n){return t.GetField(n,BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);}}}
// Extracted unchanged production native-reader methods are appended below.
struct Obscured {
 public int Value;public static implicit operator int(Obscured v){return v.Value;}
 public static implicit operator Obscured(int v){Obscured o=new Obscured();o.Value=v;return o;}
}
class FakeData {public Obscured ItemID=1165;}
class FakeWeapons {public Obscured CurrentSlotID=0;public Obscured[] Bullets=new Obscured[]{1};}
class FakeInventory {public FakeWeapons _weaponsData=new FakeWeapons();}
class FakeController {public FakeData _weaponFirearmData=new FakeData();public FakeInventory _plrInventoryManager=new FakeInventory();}
class Check {
 static int bad;
 static void Ok(bool b,string s){Console.WriteLine((b?"PASS ":"FAIL ")+s);if(!b)bad++;}
 static GameObject Go(int view,Vector3 at){GameObject g=new GameObject();g.View=view;g.transform.position=at;return g;}
 static void Main(){
  float held=0;for(int i=0;i<90;i++)held=AirDefenceCore.Lock(held,1f/60f,true,true,1.5f);
  Ok(held>1.499f,"continuous player lock reaches 1.5 s");
  Ok(NativeReadCheck.Run(),"production dynamic readers: obscured item ID, ammo and reload without boxing");
  Ok(AirDefenceCore.Lock(held,.016f,true,false,1.5f)==0,"terrain/LOS loss clears lock");
  Ok(AirDefenceCore.Lock(held,.016f,false,true,1.5f)<.02f,"switching target restarts lock");
  FlareSupply supply=new FlareSupply();supply.Remaining=6;
  Ok(AirDefenceCore.Dispense(ref supply,0)&&!AirDefenceCore.Dispense(ref supply,3.99f),"cooldown blocks repeated flare requests");
  for(int i=1;i<6;i++)AirDefenceCore.Dispense(ref supply,i*4);
  Ok(supply.Remaining==0&&supply.Sequence==6&&!AirDefenceCore.Dispense(ref supply,100),"six bursts, no landing/time refill");
  Ok(AirDefenceCore.Decoy(1,2.5f,600*600,1)&&!AirDefenceCore.Decoy(3,2.5f,600*600,1)&&!AirDefenceCore.Decoy(1,2.5f,600*600,-1),"live flare cone/range/expiry gates");
  bool diverted=false;float until=.45f+2.5f;
  for(float t=0;t<7.5f;t+=.1f){float distance=1200-160*t;if(!diverted)diverted=AirDefenceCore.Decoy(t,until,distance*distance,1);}
  Ok(diverted,"NPC reaction 0.45 s decoys a 1200 u launch; no reacquisition after expiry");
  Terrain terrain=new Terrain();Terrain.activeTerrains=new Terrain[]{terrain};
  // X-major ridge; transposing this heightmap would miss the east-west ray.
  for(int x=49;x<=51;x++)for(int z=0;z<=100;z++)terrain.heights[x,z]=60*2.8f;
  List<GepardGun.Contact> air=new List<GepardGun.Contact>();
  GameObject low=Go(10,new Vector3(2400,20*2.8f,560));
  GameObject high=Go(11,new Vector3(2400,150*2.8f,560));
  air.Add(new GepardGun.Contact{Go=low,Pos=low.transform.position});air.Add(new GepardGun.Contact{Go=high,Pos=high.transform.position});
  RadarShadow.Scan(air,new Vector3(280,8*2.8f,560));
  Ok(!RadarShadow.Visible(low)&&RadarShadow.Visible(high),"production radar adapter: 20 m aircraft behind 60 m ridge masked; high aircraft clear");
  Time.time=1.01f;Ok(!RadarShadow.Visible(high),"expired radar solution fails closed");Time.time=0;
  int calls=TerrainCollider.Calls;for(int i=0;i<10000;i++)RadarShadow.Visible(low);
  Ok(TerrainCollider.Calls==calls,"cached radar reads perform no per-frame LOS query");
  for(int x=49;x<=51;x++)for(int z=0;z<=100;z++)terrain.heights[x,z]=0;
  RadarShadow.Scan(air,new Vector3(280,8*2.8f,560));
  Ok(RadarShadow.Visible(low),"low flight over open terrain remains detectable (no blanket altitude cutoff)");
  air[0].Pos.y=2*2.8f;RadarShadow.Scan(air,new Vector3(280,8*2.8f,560));
  Ok(!RadarShadow.Visible(low)&&!RadarShadow.Visible(Go(99,Vector3.zero)),"ground clutter and unknown contact fail closed");
  List<GepardGun.Contact> raid=new List<GepardGun.Contact>();for(int i=0;i<64;i++){GameObject g=Go(100+i,new Vector3(2000,420,560));raid.Add(new GepardGun.Contact{Go=g,Pos=g.transform.position});}
  calls=TerrainCollider.Calls;RadarShadow.Scan(raid,new Vector3(280,22.4f,560));Ok(TerrainCollider.Calls-calls==8,"large raid: eight LOS queries per radar slice");
  GameObject heli=Go(20,new Vector3(0,100,600));PlayerHeli.All.Add(heli);PlayerHeli.FlownMachine=heli;Crocodile.Player=Go(1,heli.transform.position);
  Time.time=10;MercAA.Authority=false;Mi8Flares.RequestFrom(20,1);
  Ok(Mi8Flares.Remaining(heli)==6,"non-master cannot spend flare supply");
  MercAA.Authority=true;Mi8Flares.RequestFrom(20,2);Ok(Mi8Flares.Remaining(heli)==6,"unauthorized pilot request rejected");
  Mi8Flares.RequestFrom(20,1);Mi8Flares.RequestFrom(20,1);Ok(Mi8Flares.Remaining(heli)==5,"master accepts one burst and rejects cooldown replay");
  Vector3 point;Ok(Mi8Flares.Decoy(heli,new Vector3(0,100,0),new Vector3(0,0,1),out point),"production flare adapter guides seeker toward replicated flare point");
  int sent=Stinger.Packets;Mi8Flares.Snapshot();Ok(Stinger.Packets==sent+1,"late-join snapshot retains master supply");
  Mi8Flares.Clear();MercAA.Authority=false;Mi8Flares.Apply(20,2,new Vector3(0,100,600),new Vector3(4,2.5f,4));Mi8Flares.Apply(20,1,Vector3.zero,new Vector3(5,2.5f,4));
  Ok(Mi8Flares.Remaining(heli)==4,"out-of-order snapshot cannot refill supply");
  PlayerHeli.FlownMachine=null;Mi8Flares.Pilot(20,1,true,false);MercAA.Authority=true;Time.time=20;Mi8Flares.RequestFrom(20,1);
  Ok(Mi8Flares.Remaining(heli)==4,"passenger cannot claim flare controls");
  Mi8Flares.Pilot(20,1,true,true);Mi8Flares.RequestFrom(20,1);Ok(Mi8Flares.Remaining(heli)==3,"remote pilot lease accepted by new master without supply reset");
  Time.time=24;Mi8Flares.RequestFrom(20,1);Ok(Mi8Flares.Remaining(heli)==3,"expired pilot lease rejected");
  GameObject npc=Go(77,new Vector3(100,150,100));RevivalTroopInsertion.All.Add(npc);Time.time=30;Mi8Flares.Threat(77);Mi8Flares.Tick();Ok(Mi8Flares.Remaining(npc)==6,"NPC flare reaction waits 0.45 s");Time.time=30.46f;Mi8Flares.Tick();Ok(Mi8Flares.Remaining(npc)==5,"NPC Mi-8 deploys limited synchronized countermeasure");
  // Warm the static pools; exercise actual production flare tick and radar reads.
  Time.time=100;Mi8Flares.Tick();GC.Collect();long before=GC.GetTotalMemory(true);int gc=GC.CollectionCount(0);
  for(int i=0;i<100000;i++){Mi8Flares.Tick();RadarShadow.Visible(high);Mi8Flares.Hud(heli);}
  long after=GC.GetTotalMemory(false);
  Ok(after-before<1024&&GC.CollectionCount(0)==gc,"100000 warmed flare/cache/HUD ticks: no managed allocation growth or GC");
  Console.WriteLine("offline checks: "+(bad==0?"ALL PASS":bad+" FAILED"));Environment.ExitCode=bad>0?1:0;
 }
}
'''


def source_checks():
    stinger = (ROOT / 'Revival.Stinger.cs').read_text(encoding='utf-8')
    radar = (ROOT / 'Revival.TowerRadar.cs').read_text(encoding='utf-8')
    merc = (ROOT / 'Revival.MercStinger.cs').read_text(encoding='utf-8')
    checks = {
        'seeker and render use bounded 5 Hz visibility cache': '_visibilityAt = Time.time + 0.2f' in stinger and 't.Go == null || !Seen(t)' in stinger and 'int budget = 8;' in stinger,
        'missile sweeps and nonalloc physics': 'Physics.RaycastNonAlloc' in stinger and 'f.NextCollision = Time.time + 0.1f' in stinger and 'Physics.RaycastAll' not in stinger,
        'one-way diverted missile cannot damage locked target': 'if (!f.Diverted && Time.time >= f.NextSeeker)' in stinger and 'Finish(f, !f.Diverted, !f.Diverted)' in stinger,
        'master approval rejects active flare stale impact': 'go == null || Mi8Flares.Protects(go)' in stinger,
        'flare state accepts master only': 'if (FromMaster(sender)) Mi8Flares.Apply' in stinger,
        'radar mask applies to sweep, siren and command resolution': radar.count('!RadarShadow.Visible(c.Go)') == 3,
        'guns use per-target radar calibration': 'direction && RadarShadow.Visible(c.Go)' in (ROOT / 'Revival.Flak.cs').read_text(encoding='utf-8') and 'RadarShadow.Visible(g.Target.Go)' in (ROOT / 'Revival.MercAA.cs').read_text(encoding='utf-8'),
        'merc ammo consumed once with native reload': '_fBullets.SetValue(u.Fight.Weapon, rounds - 1)' in merc and 'StartReload(f)' in merc and 'if (f.Manpads != null) return 0;' in (ROOT / 'Revival.NpcCombat.cs').read_text(encoding='utf-8'),
        'merc respects friendly IFF, bounded LOS and owner orders': 'if (!hostile || friendly' in merc and 'int rays = 4;' in merc and 'MercMayEngage(u, now)' in merc and 'MercMayStand(f, u)' in merc,
        'C flares do not collide with X jump': 'Input.GetKeyDown(KeyCode.C)) Mi8Flares.Request' in (ROOT / 'Revival.PlayerHeli.cs').read_text(encoding='utf-8'),
        'existing supplied-model import and reload are integrated': 'fim-92_stinger.glb' in (ROOT / 'stinger_build.py').read_text(encoding='utf-8') and (ROOT / 'assets/stinger.ndmesh').stat().st_size > 1000 and '1, 2068, 15.2f' in (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8'),
    }
    for name, passed in checks.items():
        print(('PASS ' if passed else 'FAIL ') + name)
    return all(checks.values())


def main():
    work = ROOT / 'build/air_defence_check'
    work.mkdir(parents=True, exist_ok=True)
    harness = work / 'Harness.cs'
    stinger = (ROOT / 'Revival.Stinger.cs').read_text(encoding='utf-8')

    def method(signature):
        start = stinger.index(signature)
        brace = stinger.index('{', start)
        depth = 1
        end = brace + 1
        while depth:
            depth += (stinger[end] == '{') - (stinger[end] == '}')
            end += 1
        return stinger[start:end]

    readers = r'''
class NativeReadCheck {
 const int ItemId=1165;
 static object _controller;
 static readonly Dictionary<Type,Func<object,int>> ItemReaders=new Dictionary<Type,Func<object,int>>();
 static readonly Dictionary<FieldInfo,Func<object,int>> IntReaders=new Dictionary<FieldInfo,Func<object,int>>();
 static Func<Array,int,int> _roundReader;static Type _roundArrayType;
 internal static bool Run(){FakeController c=new FakeController();_controller=c;
  if(!IsStinger(c)||Loaded()!=1)return false;c._plrInventoryManager._weaponsData.Bullets[0]=0;
  if(Loaded()!=0)return false;c._plrInventoryManager._weaponsData.Bullets[0]=1;
  if(Loaded()!=1)return false;c._weaponFirearmData.ItemID=1162;if(IsStinger(c))return false;
  c._weaponFirearmData.ItemID=1165;GC.Collect();long before=GC.GetTotalMemory(true);int gc=GC.CollectionCount(0);
  for(int i=0;i<100000;i++){IsStinger(c);Loaded();}
  return GC.GetTotalMemory(false)-before<1024&&GC.CollectionCount(0)==gc;
 }
'''
    readers += '\n'.join(method(s) for s in ('static object Field(', 'internal static MethodInfo ToInt(',
                                          'static Func<object, int> ItemReader(', 'public static bool IsStinger(',
                                          'static int Loaded(', 'static void EmitInt(')) + '\n}\n'
    harness.write_text(HARNESS + readers.replace('AccessTools.', 'HarmonyLib.AccessTools.'), encoding='ascii')
    exe = work / 'check.exe'
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    args = [str(compiler), '/nologo', '/warn:0', '/codepage:65001', '/optimize+', '/out:' + str(exe)]
    args += [str(ROOT / f) for f in ('Revival.AirDefenceCore.cs', 'Revival.RadarShadow.cs', 'Revival.Mi8Flares.cs')]
    result = subprocess.run(args + [str(harness)], capture_output=True)
    if result.returncode:
        print(result.stdout.decode('utf-8', 'replace'))
        return 1
    run = subprocess.run([str(exe)], capture_output=True)
    print(run.stdout.decode('utf-8', 'replace').rstrip())
    return 0 if source_checks() and run.returncode == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())

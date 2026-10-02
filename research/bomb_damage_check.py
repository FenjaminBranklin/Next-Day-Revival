"""Compile the production blast queue against native-contract fakes, no game.

The fake NPC mirrors the confirmed isMine guard and body/explosion calculation.
PhotonView records the actual targeted RPC and delivers it on its owner. This
proves production routing/control flow; it does not prove live Photon delivery.
Compiler/harness output stays in this worktree's ignored build directory.
"""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def method(source, signature):
    start = source.index(signature)
    brace = source.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


HARNESS = r'''
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
namespace UnityEngine {
    public static class Time { public static int frameCount; }
    public struct Vector3 {
        public float x,y,z;
        public Vector3(float a,float b,float c) { x=a;y=b;z=c; }
        public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator +(Vector3 a,Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator *(Vector3 a,float s) { return new Vector3(a.x*s,a.y*s,a.z*s); }
        public float magnitude { get { return Distance(this,new Vector3()); } }
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t) { return a+(b-a)*t; }
        public static float Distance(Vector3 a,Vector3 b) { Vector3 d=a-b;return (float)Math.Sqrt(d.x*d.x+d.y*d.y+d.z*d.z); }
    }
    public class Transform { public Vector3 position; public bool IsChildOf(Transform other) { return this==other; } }
    public class GameObject {
        public Transform transform=new Transform();
        public NextDayRevival.PhotonView View;
        public Component Actor;
    }
    public class Component {
        public GameObject gameObject=new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
    public static class Mathf {
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
        public static float Max(float a,float b) { return Math.Max(a,b); }
        public static float Abs(float a) { return Math.Abs(a); }
        public static float Clamp01(float a) { return Math.Max(0f,Math.Min(1f,a)); }
        public static float Pow(float a,float b) { return (float)Math.Pow(a,b); }
        public const float PI=(float)Math.PI;
        public static float Cos(float a) { return (float)Math.Cos(a); }
    }
}
namespace HarmonyLib {
    public class HarmonyMethod { public MethodInfo Method; public HarmonyMethod(MethodInfo m) { Method=m; } }
    public class Harmony {
        public void Patch(MethodInfo m,HarmonyMethod p,object a,object b,object c,object d) {
            NextDayRevival.NPC_AI2.Prefix=(Action<object,int,int>)Delegate.CreateDelegate(typeof(Action<object,int,int>),p.Method);
        }
    }
    public static class AccessTools {
        const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        public static MethodInfo Method(Type t,string n,Type[] p,object unused) { return p==null?t.GetMethod(n,Flags):t.GetMethod(n,Flags,null,p,null); }
        public static MethodInfo PropertyGetter(Type t,string n) { return t.GetProperty(n,Flags).GetGetMethod(true); }
        public static FieldInfo Field(Type t,string n) { return t.GetField(n,Flags); }
    }
}
namespace NextDayRevival {
    // Crew handoff is exercised with real production hooks by crew_blast_check.
    // This older person-routing harness deliberately has no vehicle lifecycle.
    internal static class CrewBlast {
        public struct Profile { }
        internal static void Install(Harmony h,MethodInfo m) { }
        internal static Profile Begin(Vector3 p,float r,float peak,bool n) { return new Profile(); }
        internal static void End(Profile p) { }
    }
    public class Log { public void LogWarning(string s) { Console.WriteLine(s); } public void LogError(string s) { throw new Exception(s); } }
    public static class RevivalPlugin {
        public static Log L=new Log();
        public static Type TypeByName(string n) { return Type.GetType("NextDayRevival."+n); }
    }
    public static class PhotonNetwork { public static bool Master=true; public static int ActorId=1; public static bool isMasterClient { get { return Master; } } }
    public static class RevivalTroopInsertion {
        public static bool TerrainHeight(Vector3 p,out float y) { y=0;return true; }
    }
    public static class An2Flight {
        const float G=9.81f, K=2.8f;
        static object CfgDrag;
        static float F(object unused,float fallback) { return fallback; }
        static bool Ground(Vector3 at,out float y) { return RevivalTroopInsertion.TerrainHeight(at,out y); }
        /*FLIGHT*/
    }
    public struct PhotonMessageInfo { }
    public class PhotonPlayer { public int ID; public PhotonPlayer(int i) { ID=i; } }
    public enum PhotonTargets { All }
    public class PhotonView {
        public bool Mine;
        public bool isMine { get { return Mine; } }
        public PhotonPlayer Owner;
        public PhotonPlayer owner { get { return Owner; } }
        public Component Actor;
        public int Calls;
        public string LastRpc;
        public PhotonPlayer Recipient;
        public object[] Args;
        public int SentBy;
        public void RPC(string name,PhotonTargets target,object[] args) { throw new Exception("wrong RPC overload"); }
        public void RPC(string name,PhotonPlayer target,object[] args) {
            if (target!=Owner) throw new Exception("wrong health owner");
            Calls++;LastRpc=name;Recipient=target;Args=args;SentBy=PhotonNetwork.ActorId;
            bool was=Mine;Mine=true;
            if (name=="ApplyDamage") ((NPC_AI2)Actor).ApplyDamage((float)args[0],(int)args[1],(int)args[2],(int)args[3],(Vector3)args[4],new PhotonMessageInfo());
            else if (name=="PlayerApplyDamage") ((Player)Actor).PlayerApplyDamage((float)args[0],(int)args[1],(int)args[2],(Vector3)args[3],(Vector3)args[4]);
            else throw new Exception("unknown RPC");
            Mine=was;
        }
    }
    public static class Extensions { public static PhotonView GetPhotonView(GameObject go) { return go.View; } }
    public class Home { public bool IsSafeSettlement; public int _lastKillerId; }
    public class NPC_AI2 : Component {
        public static Action<object,int,int> Prefix;
        public bool IsInitialized=true, GodModeEnabled, IsTalkActive, _isSafeSettlement;
        public int BehaviorPattern;
        public Home MySettlement=new Home();
        public float Health=100;
        public int Calls;
        public bool IsAlive() { return Health>0; }
        public void ApplyDamage(float damage,int part,int type,int owner,Vector3 direction,PhotonMessageInfo info) {
            Prefix(this,type,owner);
            if (!IsInitialized || !gameObject.View.isMine || MySettlement.IsSafeSettlement || GodModeEnabled || !IsAlive()) return;
            if (part!=1 || type!=14 || owner!=0) throw new Exception("incorrect blast parameters");
            if (MySettlement._lastKillerId==0) throw new Exception("anonymous kill-streak crash");
            Calls++;Health-=damage; // native body/explosion multiplier is 1
        }
    }
    public class Player : Component {
        public float Health=100;
        public int Calls;
        public bool SameFaction=true;
        public void PlayerApplyDamage(float damage,int part,int type,Vector3 hit,Vector3 from) {
            if (!gameObject.View.isMine || part!=1 || type!=14) throw new Exception("incorrect player blast");
            Calls++;Health-=damage;
        }
    }
    public class VehicleGameSystem : Component {
        public int Calls;
        public float Health=5000;
        public void ApplyDamage(float damage,int part) { if (!PhotonNetwork.Master || part!=14) throw new Exception("vehicle authority");Calls++;Health-=damage; }
    }
    public class NetworkGameServer {
        static NetworkGameServer One=new NetworkGameServer();
        public static NetworkGameServer Instance { get { return One; } }
        public ArrayList NetworkPlayers=new ArrayList();
    }
    public static class NpcScan { public static Component[] Items=new Component[0]; public static Component[] All() { return Items; } public static Component[] BlastTargets() { return Items; } }
    public static class VehicleScan { public static Component[] Items=new Component[0]; public static Component[] All() { return Items; } }
    public static class PlayerScan {
        public static Component[] BlastTargets() {
            ArrayList players=NetworkGameServer.Instance.NetworkPlayers;
            Component[] result=new Component[players.Count];
            for(int i=0;i<players.Count;i++) result[i]=((GameObject)players[i]).Actor;
            return result;
        }
    }
    public static class AirKills { public static bool Sheltered(Vector3 a,Vector3 b) { return false; } }
    public static class NpcWar { public static string PatrolFaction(Component npc) { return "enemy"; } }
    public static class Fraktion {
        public static string Spielerseite(GameObject go) { return ((Player)go.Actor).SameFaction?"ally":"enemy"; }
        public static bool Feind(string a,string b) { return a!=b; }
    }
    public static class GunnerAI {
        public static Component Carrier(Transform t) { return null; }
        public static bool Armoured(Component c) { return false; }
    }
    public static class FrameProf { public const int S_OrdnanceBlastT=140; public static int Ticks; public static void S(int n) { Ticks++; } public static void E(int n) { } }
    // W AA7 (merged): Enqueue also reports the blast to the fixed AA.
    public static class AirDefenceDamage { public static int Reports; public static void ReportBlast(Vector3 at, float radius, float peak) { Reports++; } }
    public static class Mortar {
        public static class FactionShield { public static bool SameFactionAsLocal(GameObject go) { return ((Player)go.Actor).SameFaction; } }
        static bool _streakLooked, _killerLooked;
        static FieldInfo _mySettlement,_lastKillerId;
        /*GUARDS*/
    }
    public static class Check {
        static void Assert(bool b,string s) { if (!b) throw new Exception(s); }
        static void Near(float a,float b,string s) { Assert(Math.Abs(a-b)<0.01f,s); }
        static void Place(Component c,float m,bool mine) {
            c.transform.position=new Vector3(m*2.8f,0,0);
            c.gameObject.Actor=c;
            c.gameObject.View=new PhotonView();c.gameObject.View.Actor=c;
            c.gameObject.View.Mine=mine;c.gameObject.View.Owner=new PhotonPlayer(mine?1:2);
        }
        static void Drain() { for(int i=0;i<5000;i++) OrdnanceBlast.Tick(); }
        static void Reset() { NpcScan.Items=new Component[0];VehicleScan.Items=new Component[0];NetworkGameServer.Instance.NetworkPlayers.Clear(); }
        public static void Main() {
            System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
            OrdnanceBlast.Install(new HarmonyLib.Harmony());
            Vector3 impact;float fall;
            Assert(An2Flight.Predict(new Vector3(0,550*2.8f,0),new Vector3(50,0,0),out impact,out fall),"high An2 drop reaches impact");
            Near(impact.y,0,"impact and visual terrain point");
            Assert(fall>10 && fall<15 && impact.x>1000,"metres/second ballistic flight from 550 m");
            NPC_AI2 highDrop=new NPC_AI2();Place(highDrop,0,false);highDrop.transform.position=impact;
            NpcScan.Items=new Component[] {highDrop};
            OrdnanceBlast.Enqueue(impact,25*2.8f,500,0,300);Drain();
            Assert(highDrop.Calls==1 && highDrop.Health<=0,"high altitude impact reaches NPC damage");Reset();
            Console.WriteLine("PASS production An-2 fall from 550 m: {0:0.00} s, impact x {1:0.00} m, NPC at impact killed",fall,impact.x/2.8f);
            float r=OrdnanceBlast.RadiusForMass(100);
            Near(r,32,"100 kg radius in metres");
            float baseline=OrdnanceBlast.ReferencePersonPeak;
            float previous=999;
            for(int m=0;m<=33;m++) {
                float d=OrdnanceBlast.Damage(m*2.8f,r*2.8f,baseline);
                Assert(d<=previous,"monotonic falloff");previous=d;
                if(m<=15) Assert(d>=100,"standing target lethal within 15 m");
                if(m==30) Assert(d>0 && d<100,"wound at 30 m");
                if(m>=32) Near(d,0,"outside radius");
            }
            Console.WriteLine("PASS 100 kg baseline: 15 m = {0:0.00}, 30 m = {1:0.00}, 32 m = 0 body damage",
                OrdnanceBlast.Damage(15*2.8f,r*2.8f,baseline),OrdnanceBlast.Damage(30*2.8f,r*2.8f,baseline));
            Assert(OrdnanceBlast.RadiusForMass(50)>25 && OrdnanceBlast.RadiusForMass(50)<26,"FAB50 scale");
            Assert(OrdnanceBlast.RadiusForMass(250)>43 && OrdnanceBlast.RadiusForMass(250)<44,"FAB250 scale");
            NPC_AI2 local=new NPC_AI2(), remote=new NPC_AI2(), rim=new NPC_AI2(), safe=new NPC_AI2(), god=new NPC_AI2(), trader=new NPC_AI2(), talk=new NPC_AI2(), uninit=new NPC_AI2();
            Place(local,15,true);Place(remote,15,false);Place(rim,33,true);Place(safe,0,true);Place(god,0,true);Place(trader,0,true);Place(talk,0,true);Place(uninit,0,true);
            safe.MySettlement.IsSafeSettlement=true;god.GodModeEnabled=true;trader.BehaviorPattern=1;talk.IsTalkActive=true;uninit.IsInitialized=false;
            Player victim=new Player(), wounded=new Player(), outside=new Player();Place(victim,15,false);Place(wounded,30,true);Place(outside,33,false);
            VehicleGameSystem car=new VehicleGameSystem();Place(car,0,true);
            NpcScan.Items=new Component[] {local,remote,rim,safe,god,trader,talk,uninit};VehicleScan.Items=new Component[] {car};
            NetworkGameServer.Instance.NetworkPlayers.Add(victim.gameObject);NetworkGameServer.Instance.NetworkPlayers.Add(wounded.gameObject);NetworkGameServer.Instance.NetworkPlayers.Add(outside.gameObject);
            PhotonNetwork.Master=false;
            OrdnanceBlast.Enqueue(new Vector3(),r*2.8f,240,1000,240);Drain();
            Assert(local.Calls==0 && remote.Calls==0 && victim.Calls==0 && car.Calls==0,"nonmaster cannot duplicate damage");
            PhotonNetwork.Master=true;
            OrdnanceBlast.Enqueue(new Vector3(),r*2.8f,240,1000,240);Drain();
            Assert(local.Calls==1 && remote.Calls==1 && local.Health<=0 && remote.Health<=0,"local and remote NPC call reached once");
            Assert(remote.gameObject.View.LastRpc=="ApplyDamage" && remote.gameObject.View.Calls==1,"NPC owner RPC reached");
            Assert(victim.Calls==1 && wounded.Calls==1 && victim.Health<=0 && wounded.Health>0 && wounded.Health<100,"player victim RPC and falloff reached");
            Assert(victim.SameFaction && victim.gameObject.View.LastRpc=="PlayerApplyDamage","same faction physical blast must hit");
            Assert(outside.Calls==0 && rim.Calls==0 && safe.Calls==0 && god.Calls==0 && trader.Calls==0 && talk.Calls==0 && uninit.Calls==0,"rim and native safety guards");
            Assert(car.Calls==1 && car.Health==4000,"master vehicle explosion once");
            Console.WriteLine("PASS master-only: local NPC call + remote NPC owner RPC + both player victim RPCs + vehicle, exactly once; faction-independent; native safety guards");
            Reset();
            Player shotPlayer=new Player();Place(shotPlayer,15,false);shotPlayer.gameObject.View.Owner=new PhotonPlayer(3);
            NPC_AI2 shotNpc=new NPC_AI2();Place(shotNpc,15,false);
            VehicleGameSystem shotCar=new VehicleGameSystem();Place(shotCar,0,true);
            NetworkGameServer.Instance.NetworkPlayers.Add(shotPlayer.gameObject);
            NpcScan.Items=new Component[] {shotNpc};VehicleScan.Items=new Component[] {shotCar};
            PhotonNetwork.Master=false;PhotonNetwork.ActorId=2;
            OrdnanceBlast.EnqueuePlayers(new Vector3(),32*2.8f,240);Drain();
            Assert(shotPlayer.Calls==1 && shotPlayer.gameObject.View.SentBy==2,"shooter must send player RPC, not host");
            Assert(shotNpc.Calls==0 && shotCar.Calls==0,"shooter player job cannot hurt NPCs/vehicles");
            PhotonNetwork.Master=true;PhotonNetwork.ActorId=1;
            OrdnanceBlast.Enqueue(new Vector3(),32*2.8f,240,1000,0);Drain();
            Assert(shotNpc.Calls==1 && shotCar.Calls==1 && shotPlayer.Calls==1,"master cannot duplicate shooter player damage");
            Console.WriteLine("PASS non-master shooter sends player RPC with correct sender; master separately damages NPC/vehicle without duplicate player hit");
            Reset();
            float[] radii=new float[] {25,OrdnanceBlast.RadiusForMass(250),18};
            float[] peaks=new float[] {300,450,350};
            float[] npcPeaks=new float[] {500,900,700};
            for(int i=0;i<3;i++) {
                Player p=new Player();Place(p,radii[i]*0.5f,false);NetworkGameServer.Instance.NetworkPlayers.Add(p.gameObject);
                NPC_AI2 n=new NPC_AI2();Place(n,radii[i]*0.5f,false);NpcScan.Items=new Component[] {n};
                OrdnanceBlast.Enqueue(new Vector3(),radii[i]*2.8f,npcPeaks[i],0,peaks[i]);Drain();
                Assert(p.Calls==1 && n.Calls==1,"each live profile reaches both target classes");
                Near(p.Health,100-peaks[i]*0.5f,"player profile damage");
                Near(n.Health,100-npcPeaks[i]*0.5f,"NPC profile damage");Reset();
            }
            Console.WriteLine("PASS FAB-50 25 m / FAB-250 {0:0.00} m / M-13 18 m: NPC and player paths reached",radii[1]);
            // Many far targets verify bounded traversal and no scene/physics query.
            Component[] far=new Component[10000];for(int i=0;i<far.Length;i++) { far[i]=new NPC_AI2();Place(far[i],1000,true); }
            NpcScan.Items=far;
            OrdnanceBlast.Enqueue(new Vector3(),32*2.8f,240,0,0);
            int before=FrameProf.Ticks;Drain();
            Assert(FrameProf.Ticks-before>=157,"64-target cap must time slice dense scenes");
            before=FrameProf.Ticks;Drain();Assert(FrameProf.Ticks==before,"idle queue never enters profiler/native work");
            Console.WriteLine("PASS 10000 far targets time sliced; idle fast return; F6 slot registered");
            Reset();
            NPC_AI2 cancel=new NPC_AI2();Place(cancel,0,true);NpcScan.Items=new Component[] {cancel};
            OrdnanceBlast.Enqueue(new Vector3(),32*2.8f,240,0,0);PhotonNetwork.Master=false;Drain();PhotonNetwork.Master=true;Drain();
            Assert(cancel.Calls==0,"former master must cancel pending damage");
            Console.WriteLine("PASS authority loss cancels outstanding work");
        }
    }
}
'''


def main():
    mortar = (ROOT / 'RevivalMortar.cs').read_text(encoding='utf-8')
    an2 = (ROOT / 'Revival.An2Bombs.cs').read_text(encoding='utf-8')
    guards = '\n'.join(method(mortar, signature) for signature in (
        'internal static void BreakKillStreak(', 'internal static bool Hurtable(', 'static bool Bool('))
    build = ROOT / 'build' / 'bomb-check'
    build.mkdir(parents=True, exist_ok=True)
    cs, exe = build / 'Harness.cs', build / 'Harness.exe'
    flight = '\n'.join(method(an2, signature) for signature in (
        'static void Advance(', 'static bool OnPlane(', 'internal static bool Predict('))
    cs.write_text(HARNESS.replace('/*GUARDS*/', guards).replace('/*FLIGHT*/', flight), encoding='ascii')
    compiler = Path(r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe')
    compile_run = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe),
                                  str(cs), str(ROOT / 'Revival.OrdnanceBlast.cs'),
                                  str(ROOT / 'Revival.An2BombCurve.cs')], capture_output=True, text=True,
                                 encoding='utf-8', errors='replace')
    if compile_run.returncode:
        raise AssertionError(compile_run.stdout + compile_run.stderr)
    result = subprocess.run([str(exe)], capture_output=True, text=True)
    print(result.stdout, end='')
    if result.returncode:
        raise AssertionError(result.stderr)
    # Seams: exercise the actual queue above, then pin all three producers to it.
    an2 = (ROOT / 'Revival.An2Bombs.cs').read_text(encoding='utf-8')
    air = (ROOT / 'Revival.AirEvents.cs').read_text(encoding='utf-8')
    kat = (ROOT / 'RevivalKatyusha.cs').read_text(encoding='utf-8')
    for source in (an2, air, kat):
        assert 'OrdnanceBlast.Enqueue(' in source
        assert 'Mortar.Sweep(' not in source
        assert 'FactionShield.Arm(' not in source
    assert 'QueueBlast(point, radius, kg, id);' in method(an2, 'static void Burst(')
    assert 'QueueBlast(point, radius, kg, id);' in method(an2, 'static void RemoteBurst(')
    # A-L1: An-2 jobs carry the lethal core, a report label and the bomb id.
    assert '0f, LethalCore, Label(kg), id);' in method(an2, 'static void QueueBlast(')
    assert 'OrdnanceBlast.EnqueuePlayers(' in method(an2, 'static void Burst(')
    assert 'OrdnanceBlast.EnqueuePlayers(' not in method(an2, 'static void RemoteBurst(')
    assert 'f[5] < 0.5f && RevivalTroopInsertion.MasterClient()' in an2  # dud excluded
    assert 'Net.Send(new float[] { 1f, id, point.x, point.y, point.z, dud ? 1f : 0f, kg }, true)' in an2
    assert 'RemoteBurst(new Vector3(f[2], f[3], f[4]), An2BombLoad.KgAt(f, 6), id)' in an2  # Y B4: the dropper's mass
    assert 'Burst(b.Impact, master);' in air and 'OrdnanceBlast.RadiusForMass(250f)' in air
    assert 'BurstFx.Play(at);' in method(air, 'static void Burst(')
    assert 'if (!mine) return;' in method(kat, 'internal static void Burst(')
    assert 'OrdnanceBlast.EnqueuePlayers(' in method(kat, 'internal static void Burst(')
    assert 'OrdnanceBlast.EnqueuePlayers(' not in method(kat, 'internal static void ReceiveImpact(')
    assert 'KatyushaNet.Send(new float[] { 3f, 0f, point.x, point.y, point.z }, true)' in kat
    assert 'kind == 3 && f.Length >= 5' in kat and 'ReceiveImpact(new Vector3(f[2], f[3], f[4]))' in kat
    assert 'BlastRadiusMetres' in kat and 'legacyRadius.Value / OrdnanceBlast.UnitsPerMetre' in kat
    assert '* OrdnanceBlast.UnitsPerMetre' in method(kat, 'internal static float Radius')
    plugin = (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8')
    prof = (ROOT / 'RevivalFrameProfiler.cs').read_text(encoding='utf-8')
    assert 'OrdnanceBlast.Install(_harmony);' in plugin and 'OrdnanceBlast.Tick();' in plugin
    # (the slot number moves as modules merge; the id must name its own entry)
    _names = re.findall(r'"([^"]+)"', prof.split('static readonly string[] Names')[1].split('};')[0])
    _id = re.search(r'public const int S_OrdnanceBlastT = (\d+);', prof)
    assert _id is not None and _names[int(_id.group(1))] == 'OrdnanceBlast.Tick'
    package = (ROOT / 'sync_public.py').read_text(encoding='utf-8')
    assert '"Revival.OrdnanceBlast.cs"' in package and '"research/bomb_damage_check.py"' in package
    for source, defaults in ((an2, {'BlastRadius': 25, 'NpcDamage': 500, 'PlayerDamage': 300}),
                             (kat, {'NpcDamage': 700, 'PlayerDamage': 350})):
        for key, expected in defaults.items():
            match = re.search(r'cfg\.Bind\(S, "' + key + r'", ([0-9.]+)f', source)
            assert match and float(match.group(1)) == expected, key
    assert 'BombNpcPeak = 900f, BombVehiclePeak = 2400f, BombPlayerPeak = 450f' in air
    print('PASS producer seams: armed An-2 reliable xyz -> master, Tu-95 shared impact, Katyusha reliable final xyz -> FX + master; metre conversion and F6')
    print('BOMB DAMAGE CHECK PASS')


if __name__ == '__main__':
    main()

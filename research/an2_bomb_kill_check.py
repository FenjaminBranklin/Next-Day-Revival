"""A-L1: An-2 bombs kill NPCs - run the real explosion code offline, no game.

Compiles the production Revival.OrdnanceBlast.cs and Revival.BlastKill.cs plus
the An-2 producer (Burst, RemoteBurst, QueueBlast, the result tally and
An2BombLoad, cut from Revival.An2Bombs.cs) against native-contract fakes.

The fake NPC_AI2.ApplyDamage follows the shipped IL order (REVERSE_ENGINEERING
35, 36): IsInitialized, photonView.isMine, MySettlement.IsSafeSettlement,
GodModeEnabled / _isWoundedAction / !IsAlive, StoreKeeper skips DecreaseHealth,
Boss adaptive scaling, Body x1, death at Health <= 0, and the wounded entry
below 25 HP. Harmony prefixes run in priority order like HarmonyX. A fake
PhotonView delivers the ApplyDamage RPC on the NPC's owner.

Cases: a profile table at 0/5/10/20/30 m for FAB-50 and FAB-100 (new and
the 6.66 linear falloff), the host and a non-master pilot with local and
remote NPC owners, the wounded-immunity finish, the throttled report line, an
idle tick, and Kevin's eight 6.66 bursts over Locator's real spawn and walk
points (research/an2_bomb_kill_locator.json, read from level7).
This proves the production control flow and numbers; it does not prove live
Photon delivery or what a pilot sees.
"""
from pathlib import Path
import json
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bomb_damage_check as bomb  # noqa: E402  (shared brace-matching extractor)

HARNESS = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
namespace UnityEngine {
    public static class Time { public static int frameCount; public static float time; public static float realtimeSinceStartup; }
    public struct Vector3 {
        public float x,y,z;
        public Vector3(float a,float b,float c) { x=a;y=b;z=c; }
        public static Vector3 up { get { return new Vector3(0,1,0); } }
        public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator +(Vector3 a,Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator *(Vector3 a,float s) { return new Vector3(a.x*s,a.y*s,a.z*s); }
        public static float Distance(Vector3 a,Vector3 b) { Vector3 d=a-b;return (float)Math.Sqrt(d.x*d.x+d.y*d.y+d.z*d.z); }
        public string ToString(string f) { return "("+x.ToString(f)+", "+y.ToString(f)+", "+z.ToString(f)+")"; }
    }
    public class Transform { public Vector3 position; public bool IsChildOf(Transform other) { return this==other; } }
    public class GameObject { public Transform transform=new Transform(); public NextDayRevival.PhotonView View; public string name="go"; }
    public class Component {
        public GameObject gameObject=new GameObject();
        public Transform transform { get { return gameObject.transform; } }
        public string name { get { return gameObject.name; } }
    }
    public static class Mathf {
        public static float Clamp(float v,float lo,float hi) { return v<lo?lo:v>hi?hi:v; }
        public static float Max(float a,float b) { return Math.Max(a,b); }
        public static float Min(float a,float b) { return Math.Min(a,b); }
        public static float Abs(float a) { return Math.Abs(a); }
        public static float Clamp01(float a) { return Math.Max(0f,Math.Min(1f,a)); }
        public static float Pow(float a,float b) { return (float)Math.Pow(a,b); }
        public const float PI=(float)Math.PI;
        public static float Cos(float a) { return (float)Math.Cos(a); }
        public static int RoundToInt(float a) { return (int)Math.Round(a); }
    }
}
namespace HarmonyLib {
    public class HarmonyMethod { public MethodInfo method; public int priority=-1; public HarmonyMethod(MethodInfo m) { method=m; } }
    public static class Priority { public const int Last=0, Low=200, Normal=400, First=800; }
    public class Harmony {
        public static List<HarmonyMethod> NpcPrefixes=new List<HarmonyMethod>();
        public void Patch(MethodInfo m,HarmonyMethod prefix,HarmonyMethod postfix,object t,object f,object d) {
            if (m.DeclaringType.Name=="NPC_AI2" && m.Name=="ApplyDamage") {
                NpcPrefixes.Add(prefix);
                NpcPrefixes.Sort(delegate(HarmonyMethod a,HarmonyMethod b) { return Rank(b).CompareTo(Rank(a)); });
            } else throw new Exception("unexpected patch "+m.Name);
        }
        static int Rank(HarmonyMethod h) { return h.priority<0?Priority.Normal:h.priority; }
        // HarmonyX: every prefix runs; a false return skips the original.
        public static bool RunPrefixes(object instance,object[] args) {
            bool run=true;
            foreach (HarmonyMethod h in NpcPrefixes) {
                ParameterInfo[] ps=h.method.GetParameters();
                object[] call=new object[ps.Length];
                for (int i=0;i<ps.Length;i++)
                    call[i]=ps[i].Name=="__instance"?instance:args[int.Parse(ps[i].Name.Substring(2))];
                object r=h.method.Invoke(null,call);
                for (int i=0;i<ps.Length;i++) if (ps[i].ParameterType.IsByRef) args[int.Parse(ps[i].Name.Substring(2))]=call[i];
                if (r is bool && !(bool)r) run=false;
            }
            return run;
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
    internal static class CrewBlast {
        public struct Profile { }
        internal static void Install(Harmony h,MethodInfo m) { }
        internal static Profile Begin(Vector3 p,float r,float peak,bool n) { return new Profile(); }
        internal static void End(Profile p) { }
    }
    public class Log {
        public List<string> Info=new List<string>();
        public void LogInfo(string s) { Info.Add(s); }
        public void LogWarning(string s) { Console.WriteLine("WARNING "+s); }
        public void LogError(string s) { throw new Exception(s); }
    }
    public static class RevivalPlugin {
        public static Log L=new Log();
        public static Type TypeByName(string n) { return Type.GetType("NextDayRevival."+n); }
    }
    public static class FastField {
        public static bool GetBool(FieldInfo f,object o) { return (bool)f.GetValue(o); }
        public static void SetBool(FieldInfo f,object o,bool v) { f.SetValue(o,v); }
        public static float GetFloat(FieldInfo f,object o) { return (float)f.GetValue(o); }
    }
    internal static class Mercs { internal static object UnitOf(object ai) { return null; } }
    public static class Loc { public static string T(string ru,string en) { return en; } }
    public static class RocketHook { public static int Pictures; public static void Detonate(Vector3 p,float dmg,float r,float life) { if (dmg!=0f) throw new Exception("picture must carry no damage"); Pictures++; } }
    public class ConfigEntry<T> { public T Value; public ConfigEntry(T v) { Value=v; } }
    public static class PhotonNetwork { public static bool Master=true; public static int ActorId=1; public static bool isMasterClient { get { return Master; } } }
    public struct PhotonMessageInfo { }
    public class PhotonPlayer { int _id; public int ID { get { return _id; } } public PhotonPlayer(int i) { _id=i; } }
    public class PhotonView {
        public PhotonPlayer Owner;
        public bool isMine { get { return Owner.ID==PhotonNetwork.ActorId; } }
        public PhotonPlayer owner { get { return Owner; } }
        public NPC_AI2 Actor;
        public int Rpcs;
        public void RPC(string name,PhotonPlayer target,object[] args) {
            if (target!=Owner || name!="ApplyDamage") throw new Exception("wrong RPC/owner");
            Rpcs++;
            int was=PhotonNetwork.ActorId;bool wasMaster=PhotonNetwork.Master;
            PhotonNetwork.ActorId=Owner.ID;PhotonNetwork.Master=Owner.ID==1;   // runs on the owner's machine
            Actor.ApplyDamage((float)args[0],(int)args[1],(int)args[2],(int)args[3],(Vector3)args[4],new PhotonMessageInfo());
            PhotonNetwork.ActorId=was;PhotonNetwork.Master=wasMaster;
        }
    }
    public static class Extensions { public static PhotonView GetPhotonView(GameObject go) { return go.View; } }
    public class Home { public bool IsSafeSettlement; public int _lastKillerId=-1; public int Enemies; }
    public class Specs { public float Health, HealthMax; }
    public class NPC_AI2 : Component {
        public bool IsInitialized=true, GodModeEnabled, IsTalkActive, _isSafeSettlement, _isWoundedAction;
        public int BehaviorPattern, MainState, MaxEnemiesCount=3;
        public bool WoundRoll=true;           // native: Random.value > 0.5
        public Home MySettlement=new Home();
        public Specs Specifications=new Specs();
        public int Applied;
        public bool IsAlive() { return MainState!=3 && Specifications.Health>0; }
        public void ApplyDamage(float damage,int part,int type,int owner,Vector3 direction,PhotonMessageInfo info) {
            object[] args=new object[] { damage,part,type,owner };
            if (!Harmony.RunPrefixes(this,args)) return;
            damage=(float)args[0];
            if (!IsInitialized || !gameObject.View.isMine || MySettlement.IsSafeSettlement) return;   // IL_0000-IL_002D
            if (GodModeEnabled || _isWoundedAction || !IsAlive()) return;                           // IL_0034-IL_0055
            if (part!=1 || type!=14 || owner!=0) throw new Exception("incorrect blast parameters");
            Applied++;
            if (BehaviorPattern!=1) {                                                                // IL_00BF
                float v=damage;
                if (BehaviorPattern==3) v=v*MaxEnemiesCount/Mathf.Clamp(MySettlement.Enemies,1,MaxEnemiesCount); // CalcAdaptiveDecreaseHealth
                Specifications.Health-=v;                                                            // Body x1
                if (Specifications.Health<=0f) { _isWoundedAction=false; MainState=3; return; }       // SetHealthValue -> DeathAction
            }
            if (MainState!=7 && Specifications.Health>0f && Specifications.Health<25f && WoundRoll) { _isWoundedAction=true; MainState=7; }
        }
    }
    public class VehicleGameSystem : Component { public void ApplyDamage(float damage,int part) { } }
    public static class NpcScan { public static Component[] Items=new Component[0]; public static Component[] BlastTargets() { return Items; } }
    public static class VehicleScan { public static Component[] All() { return new Component[0]; } }
    public static class PlayerScan { public static Component[] BlastTargets() { return new Component[0]; } }
    public static class AirKills { public static bool Sheltered(Vector3 a,Vector3 b) { return false; } }
    public static class NpcWar { public static string PatrolFaction(Component npc) { return "enemy"; } }
    public static class Fraktion { public static string Spielerseite(GameObject go) { return "x"; } public static bool Feind(string a,string b) { return a!=b; } }
    public static class GunnerAI { public static Component Carrier(Transform t) { return null; } public static bool Armoured(Component c) { return false; } }
    public static class FrameProf { public const int S_OrdnanceBlastT=140; public static int Ticks; public static void S(int n) { Ticks++; } public static void E(int n) { } }
    public static class AirDefenceDamage { public static void ReportBlast(Vector3 at,float radius,float peak) { } }
    public static class Mortar {
        public static class FactionShield { public static bool SameFactionAsLocal(GameObject go) { return false; } }
        static bool _streakLooked, _killerLooked;
        static FieldInfo _mySettlement,_lastKillerId;
        /*GUARDS*/
    }
    internal static class An2Bombs {
        static float K { get { return 2.8f; } }
        internal static ConfigEntry<float> CfgRadius=new ConfigEntry<float>(25f), CfgNpcDamage=new ConfigEntry<float>(500f),
            CfgVehicleDamage=new ConfigEntry<float>(1200f), CfgPlayerDamage=new ConfigEntry<float>(300f),
            CfgLethalCore=new ConfigEntry<float>(0.5f);
        static float F(ConfigEntry<float> e,float fallback) { return e==null?fallback:e.Value; }
        public static string LastHint="";
        static void Hint(string text,float seconds) { LastHint=text; }
        static void Depot(Vector3 point,int kg) { }
        internal static class Net { public static List<float[]> Sent=new List<float[]>(); public static void Send(float[] c,bool reliable) { Sent.Add(c); } }
        internal static void ForgetOwn() { for (int i=0;i<_ownIds.Length;i++) _ownIds[i]=0; _tallyUntil=-1f; }
        internal static void Drop(int id,Vector3 point,int kg) { Burst(id,point,false,kg); }
        internal static void MasterOf(Vector3 point,int kg,int id) { RemoteBurst(point,kg,id); }
        internal static void Result(int killed,int hit) { ShowResult(killed,hit); }
        internal static bool Own(int id) { return IsOwn(id); }
        internal static void Remember(int id) { RememberOwn(id); }
        internal static float Radius(int kg) { return RadiusU(kg); }
        internal static float NpcPeak(int kg) { return Peak(CfgNpcDamage,500f,kg); }
        /*AN2*/
    }
    /*LOAD*/
    public static class Check {
        static void Assert(bool b,string s) { if (!b) throw new Exception("FAIL: "+s); }
        static int _ids=1000;
        static NPC_AI2 Man(float hp,float metres,int owner,int pattern) {
            NPC_AI2 n=new NPC_AI2();
            n.gameObject.name="npc"+(++_ids)+"_hp"+hp;
            n.transform.position=new Vector3(metres*2.8f,0,0);
            n.Specifications.Health=hp;n.Specifications.HealthMax=hp;n.BehaviorPattern=pattern;
            n.gameObject.View=new PhotonView();n.gameObject.View.Owner=new PhotonPlayer(owner);n.gameObject.View.Actor=n;
            return n;
        }
        static void Drain() { for (int i=0;i<5000;i++) { Time.frameCount++; OrdnanceBlast.Tick(); } }
        static void Host() { PhotonNetwork.Master=true;PhotonNetwork.ActorId=1; }
        static void Pilot() { PhotonNetwork.Master=false;PhotonNetwork.ActorId=2; }
        static int _bomb=100;
        static int Bomb(float metres,int kg) {
            int id=++_bomb;Host();An2Bombs.Drop(id,new Vector3(0,0,0),kg);Drain();return id;
        }
        static bool Dead(NPC_AI2 n) { return !n.IsAlive(); }

        static void Table(int kg,float[] hps,float[] metres,float core,out string text) {
            An2Bombs.CfgLethalCore.Value=core;
            System.Text.StringBuilder s=new System.Text.StringBuilder();
            for (int d=0;d<metres.Length;d++) {
                NPC_AI2[] men=new NPC_AI2[hps.Length];
                for (int h=0;h<hps.Length;h++) men[h]=Man(hps[h],metres[d],1,hps[h]>=1000?3:0);
                NpcScan.Items=men;
                Bomb(metres[d],kg);
                s.AppendFormat("    {0,4} m  dmg {1,6:0} ",metres[d],hps[0]-men[0].Specifications.Health);
                for (int h=0;h<hps.Length;h++) s.Append(Dead(men[h])?"  KILL":men[h]._isWoundedAction?"  wnd ":men[h].Specifications.Health<hps[h]?"  hurt":"  -   ");
                s.AppendLine();
            }
            text=s.ToString();
        }

        public static void Main() {
            System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
            Harmony h=new Harmony();
            OrdnanceBlast.Install(h);BlastKill.Install(h);
            Assert(Harmony.NpcPrefixes.Count==2 && Harmony.NpcPrefixes[1].method.Name=="FinishWoundedPrefix","wounded finish runs after the other ApplyDamage prefixes");

            // 1. Profile at 0/5/10/20/30 m, host, men of Locator's health classes and a 7500 HP boss.
            float[] hps=new float[] { 100,150,200,250,7500 };
            float[] metres=new float[] { 0,5,10,20,30 };
            foreach (int kg in new int[] { 100,50 }) {
                float R=An2Bombs.Radius(kg)/2.8f, peak=An2Bombs.NpcPeak(kg), core=0.5f*R, certain=0.5f*core;
                string fresh, old;
                Table(kg,hps,metres,0.5f,out fresh);Table(kg,hps,metres,0f,out old);
                Console.WriteLine("FAB-{0}: radius {1:0.0} m, NPC peak {2:0}; new: full damage to {3:0.0} m, certain kill to {4:0.0} m",kg,R,peak,core,certain);
                Console.WriteLine("    dist     first-man dmg   100HP 150HP 200HP 250HP boss7500   (new profile)");
                Console.Write(fresh);
                Console.WriteLine("    dist     first-man dmg   100HP 150HP 200HP 250HP boss7500   (6.66 linear falloff)");
                Console.Write(old);
                // Every man (and the boss inside the certain kill) dies inside his lethal radius.
                An2Bombs.CfgLethalCore.Value=0.5f;
                for (int d=0;d<metres.Length;d++)
                    for (int i=0;i<hps.Length;i++) {
                        float lethal=hps[i]>=1000?certain:hps[i]>=peak?core:core+(1f-hps[i]/peak)*(R-core);
                        if (Math.Abs(metres[d]-lethal)<0.5f) continue;   // borderline float compare
                        NPC_AI2 n=Man(hps[i],metres[d],1,hps[i]>=1000?3:0);NpcScan.Items=new Component[] { n };
                        Bomb(metres[d],kg);
                        Assert(Dead(n)==(metres[d]<lethal),"FAB-"+kg+" "+hps[i]+" HP at "+metres[d]+" m: lethal radius "+lethal.ToString("0.0")+" m");
                    }
            }
            Console.WriteLine("PASS lethal radius: FAB-100 kills 100-250 HP men to 29.0-25.3 m and any NPC to 7.9 m; FAB-50 to 22.5-18.8 m and 6.3 m");

            // 2. Host drops a bomb: local NPCs, one RPC owner; outcome counts back as a hint.
            Host();An2Bombs.ForgetOwn();RevivalPlugin.L.Info.Clear();
            NPC_AI2 local=Man(200,10,1,0), remote=Man(150,12,3,0), far=Man(150,40,1,0), god=Man(150,2,1,0);
            god.GodModeEnabled=true;
            NpcScan.Items=new Component[] { far,remote,god,local };
            Time.realtimeSinceStartup=100f;
            int hostBomb=Bomb(0,100);
            Assert(Dead(local) && Dead(remote) && !Dead(far) && !Dead(god),"host: local and RPC-owned men die, god mode refuses, 40 m is out of reach");
            Assert(remote.gameObject.View.Rpcs==1 && local.gameObject.View.Rpcs==0,"master-owned NPC damaged locally, the other owner by one RPC");
            Assert(An2Bombs.LastHint=="Bombs: 1 killed, 1 hit (1 burst)","host pilot sees the tally: "+An2Bombs.LastHint);
            string line=null;foreach (string l in RevivalPlugin.L.Info) if (l.StartsWith("BlastKill: An-2 FAB-100 #"+hostBomb)) line=l;
            Assert(line!=null && line.Contains("3 NPC(s) in reach - killed 1, hurt 0, other owner 1, refused 1")
                && line.Contains("-> killed") && line.Contains("-> sent to owner") && line.Contains("-> shielded")
                && line.Contains("owner actor 3") && line.Contains("Nearest outside reach: "+far.name+" 40.0 m"),"report line: "+line);
            Console.WriteLine("PASS host: local kill, owner RPC kill, god mode refused; tally hint; report line:");
            Console.WriteLine("    "+line);

            // 3. Non-master pilot: his client queues nothing; the master runs the same profile.
            An2Bombs.ForgetOwn();An2Bombs.Net.Sent.Clear();
            NPC_AI2 hostMan=Man(250,15,1,0), pilotMan=Man(180,18,2,0), thirdMan=Man(100,4,3,0);
            thirdMan.Specifications.Health=20f;thirdMan._isWoundedAction=true;thirdMan.MainState=7;   // natively wounded earlier
            NpcScan.Items=new Component[] { hostMan,pilotMan,thirdMan };
            Pilot();int id=++_bomb;An2Bombs.Drop(id,new Vector3(0,0,0),100);Drain();
            Assert(hostMan.Applied+pilotMan.Applied+thirdMan.Applied==0,"pilot client cannot queue NPC damage");
            float[] sent=An2Bombs.Net.Sent[An2Bombs.Net.Sent.Count-1];
            Assert(sent[0]==1f && (int)sent[1]==id && sent[5]==0f && (int)sent[6]==100,"armed burst xyz/mass/id to the master");
            An2Bombs.ForgetOwn();Host();An2Bombs.MasterOf(new Vector3(sent[2],sent[3],sent[4]),(int)sent[6],(int)sent[1]);Drain();
            Assert(Dead(hostMan) && Dead(pilotMan) && Dead(thirdMan),"master: local kill, pilot-owned kill, wounded remote man finished by his owner");
            float[] back=An2Bombs.Net.Sent[An2Bombs.Net.Sent.Count-1];
            Assert(back[0]==3f && (int)back[1]==id && back[2]==1f && back[3]==2f && back[4]==3f,"master returns killed/hit/reached to the dropper");
            Pilot();An2Bombs.Remember(id);Assert(An2Bombs.Own((int)back[1]),"pilot recognises his bomb id");
            An2Bombs.Result((int)back[2],(int)back[3]);
            Assert(An2Bombs.LastHint=="Bombs: 1 killed, 2 hit (1 burst)","non-master pilot sees the tally: "+An2Bombs.LastHint);
            Console.WriteLine("PASS non-master pilot: master applies the same profile; host-, pilot- and third-owned men die; result event back to the pilot");

            // 4. Native wounded immunity, without and with the owner-side finish.
            Host();
            NPC_AI2 a=Man(150,28,1,0);NpcScan.Items=new Component[] { a };
            Bomb(28,100);
            Assert(a._isWoundedAction && a.Specifications.Health>0f && a.Specifications.Health<25f,"28 m near miss leaves a 150 HP man natively wounded");
            HarmonyMethod finish=Harmony.NpcPrefixes[1];Harmony.NpcPrefixes.RemoveAt(1);
            float hp=a.Specifications.Health;Bomb(28,100);
            Assert(!Dead(a) && a.Specifications.Health==hp,"6.66 behaviour: a wounded man ignores the second bomb");
            Harmony.NpcPrefixes.Add(finish);Bomb(28,100);
            Assert(Dead(a),"A-L1: the second bomb finishes him");
            Console.WriteLine("PASS wounded immunity: 6.66 second bomb refused at IL_0040; the lethal-explosion prefix finishes the man");

            // 5. Throttled report: one line per explosion, at most 12 per 10 s, then a suppressed count.
            RevivalPlugin.L.Info.Clear();Time.realtimeSinceStartup=500f;
            for (int i=0;i<20;i++) { NpcScan.Items=new Component[] { Man(150,5,1,0) }; Bomb(5,50); }
            int lines=0;foreach (string l in RevivalPlugin.L.Info) if (l.StartsWith("BlastKill: An-2 FAB-50")) lines++;
            Assert(lines==12,"throttle 12 per window, got "+lines);
            Time.realtimeSinceStartup=511f;RevivalPlugin.L.Info.Clear();NpcScan.Items=new Component[0];Bomb(0,50);
            List<string> next=new List<string>();foreach (string l in RevivalPlugin.L.Info) if (l.StartsWith("BlastKill: ")) next.Add(l);
            Assert(next.Count==1 && next[0].Contains("0 NPC(s) in reach") && next[0].Contains("(8 report(s) suppressed)"),"suppressed count: "+string.Join("|",next.ToArray()));
            int before=FrameProf.Ticks;Drain();
            Assert(FrameProf.Ticks==before,"idle queue: no F6 slot entry, no work");
            Console.WriteLine("PASS report: one line per explosion, 12 per 10 s, suppressed count; idle tick does nothing");
        }

        // 6. Kevin's eight 6.66 bursts over Locator, men at their real spawn/walk points.
        public static int Locator(float[] xyz,float[] hp,int[] pattern,float[] bursts,float core,bool everyOther,out int wounded) {
            Host();An2Bombs.CfgLethalCore.Value=core;
            NPC_AI2[] men=new NPC_AI2[hp.Length];
            for (int i=0;i<hp.Length;i++) {
                men[i]=Man(hp[i],0,1,pattern[i]);men[i].transform.position=new Vector3(xyz[3*i],xyz[3*i+1],xyz[3*i+2]);
                men[i].WoundRoll=!everyOther || i%2==0;
            }
            NpcScan.Items=men;
            for (int b=0;b<bursts.Length/3;b++) { An2Bombs.Drop(++_bomb,new Vector3(bursts[3*b],bursts[3*b+1],bursts[3*b+2]),100);Drain(); }
            int dead=0;wounded=0;
            for (int i=0;i<men.Length;i++) { if (Dead(men[i])) dead++; else if (men[i]._isWoundedAction) wounded++; }
            return dead;
        }
    }
}
'''

DRIVER = r'''
namespace NextDayRevival {
    public static class Driver {
        public static void Main() {
            Check.Main();
            /*LOCATOR*/
        }
    }
}
'''


def main():
    an2 = (ROOT / 'Revival.An2Bombs.cs').read_text(encoding='utf-8')
    mortar = (ROOT / 'RevivalMortar.cs').read_text(encoding='utf-8')
    guards = '\n'.join(bomb.method(mortar, sig) for sig in (
        'internal static void BreakKillStreak(', 'internal static bool Hurtable(', 'static bool Bool('))
    start = an2.index('        // A-L1: full NPC damage in the core')
    block = an2[start:an2.index('        static void Depot(', start)]
    producer = '\n'.join(bomb.method(an2, sig) for sig in (
        'static float RadiusU(int kg)', 'static float Peak(ConfigEntry<float> e', 'static float LethalCore',
        'static string Label(int kg)', 'static void Burst(int id, Vector3 point, bool dud, int kg)',
        'static void RemoteBurst(Vector3 point, int kg, int id)')) + '\n' + block
    load = bomb.method(an2, 'internal static class An2BombLoad')

    data = json.loads((ROOT / 'research' / 'an2_bomb_kill_locator.json').read_text(encoding='utf-8'))
    bursts = [c for b in data['bursts_6_66'] for c in b]

    def arr(values, kind='f'):
        if kind == 'f':
            return 'new float[] { ' + ','.join('%gf' % v for v in values) + ' }'
        return 'new int[] { ' + ','.join(str(int(v)) for v in values) + ' }'

    men = [s for s in data['spawn_points'] if s['pattern'] != 1 and not s['god']]  # the trader cannot lose health
    spawn_xyz = [c for s in men for c in (s['x'], s['y'], s['z'])]
    walk = data['walk_points']
    walk_xyz = [c for p in walk for c in p]
    locator = []
    for label, xyz, hp, pat in (
            ('spawn', spawn_xyz, [s['health'] for s in men], [s['pattern'] for s in men]),
            ('walk150', walk_xyz, [150] * len(walk), [0] * len(walk)),
            ('walk250', walk_xyz, [250] * len(walk), [0] * len(walk))):
        for core, name in ((0.0, 'old'), (0.5, 'new')):
            locator.append('{ int w; int d=Check.Locator(%s,%s,%s,%s,%gf,true,out w); '
                           'System.Console.WriteLine("LOCATOR %s %s {0} {1} {2}",d,w,%d); }'
                           % (arr(xyz), arr(hp), arr(pat, 'i'), arr(bursts), core, label, name, len(hp)))
    source = HARNESS.replace('/*GUARDS*/', guards).replace('/*AN2*/', producer).replace('/*LOAD*/', load)
    source += DRIVER.replace('/*LOCATOR*/', '\n            '.join(locator))
    build = ROOT / 'build' / 'an2-bomb-kill-check'
    build.mkdir(parents=True, exist_ok=True)
    cs, exe = build / 'Harness.cs', build / 'Harness.exe'
    cs.write_text(source, encoding='utf-8')
    compiler = Path(r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe')
    compiled = subprocess.run([str(compiler), '/nologo', '/warn:0', '/codepage:65001', '/main:NextDayRevival.Driver',
                               '/out:' + str(exe), str(cs), str(ROOT / 'Revival.OrdnanceBlast.cs'),
                               str(ROOT / 'Revival.BlastKill.cs')],
                              capture_output=True, text=True, encoding='utf-8', errors='replace')
    if compiled.returncode:
        raise AssertionError(compiled.stdout + compiled.stderr)
    result = subprocess.run([str(exe)], capture_output=True, text=True)
    if result.returncode:
        print(result.stdout, end='')
        raise AssertionError(result.stderr)
    rows = {}
    for line in result.stdout.splitlines():
        if line.startswith('LOCATOR '):
            _, label, name, dead, wounded, total = line.split()
            rows[(label, name)] = (int(dead), int(wounded), int(total))
        else:
            print(line)
    for label, title in (('spawn', 'men at Locator spawn points (real HP 100-250, bosses 7500/8500; trader excluded)'),
                         ('walk150', '150 HP man on each of Locator\'s %d walk points' % len(walk)),
                         ('walk250', '250 HP man on each of Locator\'s %d walk points' % len(walk))):
        old, new = rows[(label, 'old')], rows[(label, 'new')]
        print('LOCATOR 6.66 bursts, %s: 6.66 profile %d/%d killed (%d left wounded and immune), '
              'A-L1 %d/%d killed (%d wounded)' % (title, old[0], old[2], old[1], new[0], new[2], new[1]))
        assert new[0] > old[0], label
    print('PASS Locator geometry: the eight logged 6.66 bursts against every real spawn and walk point')

    # Seams of the live game that the harness cannot execute.
    plugin = (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8')
    assert plugin.index('OrdnanceBlast.Install(_harmony);') < plugin.index('BlastKill.Install(_harmony);')
    receive = bomb.method(an2, 'public static void OnPhotonEvent(')
    assert 'RemoteBurst(new Vector3(f[2], f[3], f[4]), An2BombLoad.KgAt(f, 6), id)' in receive
    assert 'kind == 3 && f.Length >= 5' in receive and 'if (IsOwn(Mathf.RoundToInt(f[1])))' in receive
    assert 'RememberOwn(id);' in bomb.method(an2, 'static void Burst(')
    assert 'cfg.Bind(S, "LethalCore", 0.5f,' in an2
    kill = (ROOT / 'Revival.BlastKill.cs').read_text(encoding='utf-8')
    assert all(ord(ch) < 128 for ch in kill)
    for forbidden in ('Physics.', 'FindObjects', 'GetComponents', 'void Update(', 'FrameProf.S('):
        assert forbidden not in kill, forbidden
    print('PASS seams: BlastKill after OrdnanceBlast, kind 1 id to the master, kind 3 result to the dropper, '
          'LethalCore on by default, no query/tick in BlastKill')
    print('AN2 BOMB KILL CHECK PASS')


if __name__ == '__main__':
    main()

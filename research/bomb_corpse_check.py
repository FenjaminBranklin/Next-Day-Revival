"""C-W1: bomb kills leave lootable corpses - run the real death path offline, no game.

Compiles the production Revival.OrdnanceBlast.cs, Revival.BlastKill.cs and
Revival.BlastCorpse.cs plus the An-2 producer (cut from Revival.An2Bombs.cs,
as research/an2_bomb_kill_check.py does) against native-contract fakes that
follow the shipped IL (research/ilq.py, 2026-10-02):

  NPC_AI2.ApplyDamage -> DecreaseHealth -> SetHealthValue(h, type, part, dir)
    (RPC to every other client, then locally) -> DeathAction(type, part, dir):
    SetRagdollActive(true) (returns early while ActivePhysic is false),
    aim IK object off, StartRespawnTimer (settlement RespawnTimeInSec),
    MainState = 3, RagdollController.ApplyPhysicsForce(part, dir,
    GetPhysicsForceByDamageType(type)) -> DamagedPart.ApplyForce:
    body.velocity = dir * force (zero dir -> Vector3.down), ReGrantItems.

Harmony runs every prefix in priority order (a false return skips only the
original) and writes __args back after the patch that takes it, as HarmonyX
does - the reason BlastCorpse's prefix is Priority.Last behind Crew's.

Physics is a stand-in: an 11-bone position-based ragdoll (hips, chest, head,
arms, legs; distance joints, masses 15.1 kg with the chest 20 %), gravity
9.81 u/s^2 (Unity default, the longest flights), Coulomb friction 0.6 and a
discrete contact that loses a bone moving more than one radius under the
surface in a step (tunnelling). The ground is the real terrain grid around
Locator (research/bomb_corpse_terrain.json, x-major heights read from the
installed GW_Scene_1). Burst 5 went off 7 m over the terrain, so the proxy
gives it a flat 10 m x 10 m roof at the burst height; no other building
colliders are modelled.

Cases: Kevin's eight 6.67 kills (burst, logged distance) at 8 bearings each,
without and with BlastCorpse; the light-limb sensitivity; forced sink, throw,
cellar, a spot that keeps rejecting the corpse, the frozen settlement, the
remote client, zero/native/non-explosion directions, the Crew __args prefix,
the 64-slot table, idle cost, steady-state allocation and Tick timing.
This proves the production control flow and numbers; it does not prove Unity
PhysX, the real ragdoll masses or what a player sees.
"""
from pathlib import Path
import json
import math
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
        public static Vector3 zero { get { return new Vector3(0,0,0); } }
        public static Vector3 up { get { return new Vector3(0,1,0); } }
        public static Vector3 down { get { return new Vector3(0,-1,0); } }
        public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x,-a.y,-a.z); }
        public static Vector3 operator +(Vector3 a,Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator *(Vector3 a,float s) { return new Vector3(a.x*s,a.y*s,a.z*s); }
        public float magnitude { get { return (float)Math.Sqrt(x*x+y*y+z*z); } }
        public static float Distance(Vector3 a,Vector3 b) { return (a-b).magnitude; }
        public string ToString(string f) { return "("+x.ToString(f)+", "+y.ToString(f)+", "+z.ToString(f)+")"; }
        public override string ToString() { return ToString("0.0"); }
    }
    public class Transform { public Vector3 position; public Vector3 forward=new Vector3(0,0,1); public bool IsChildOf(Transform other) { return this==other; } }
    public class GameObject { public Transform transform=new Transform(); public NextDayRevival.PhotonView View; public string name="go"; }
    public class Component {
        public GameObject gameObject=new GameObject();
        public Transform transform { get { return gameObject.transform; } }
        public string name { get { return gameObject.name; } }
        public T GetComponent<T>() where T : class { return this as T; }
    }
    public class Rigidbody : Component {
        public Vector3 position, velocity, angularVelocity;
        public bool isKinematic=true;
        public float mass=1f, Radius=0.3f;
        public Vector3 Prev; public float Push; public bool Lost;    // proxy physics state
    }
    public static class Mathf {
        public static float Clamp(float v,float lo,float hi) { return v<lo?lo:v>hi?hi:v; }
        public static int Clamp(int v,int lo,int hi) { return v<lo?lo:v>hi?hi:v; }
        public static float Max(float a,float b) { return Math.Max(a,b); }
        public static float Min(float a,float b) { return Math.Min(a,b); }
        public static float Abs(float a) { return Math.Abs(a); }
        public static float Sqrt(float a) { return (float)Math.Sqrt(a); }
        public static float Clamp01(float a) { return Math.Max(0f,Math.Min(1f,a)); }
        public static float Pow(float a,float b) { return (float)Math.Pow(a,b); }
        public const float PI=(float)Math.PI;
        public static float Cos(float a) { return (float)Math.Cos(a); }
        public static float Sin(float a) { return (float)Math.Sin(a); }
        public static int RoundToInt(float a) { return (int)Math.Round(a); }
        public static int FloorToInt(float a) { return (int)Math.Floor(a); }
    }
}
namespace HarmonyLib {
    public class HarmonyMethod { public MethodInfo method; public int priority=-1; public HarmonyMethod(MethodInfo m) { method=m; } }
    public static class Priority { public const int Last=0, Low=200, Normal=400, High=600, First=800; }
    public class Harmony {
        public static Dictionary<string,List<HarmonyMethod>> Pre=new Dictionary<string,List<HarmonyMethod>>(), Post=new Dictionary<string,List<HarmonyMethod>>();
        public static string Off;                     // a patched method run without its patches (the 6.67 state)
        public void Patch(MethodInfo m,HarmonyMethod prefix,HarmonyMethod postfix,object t,object f,object d) {
            string key=m.DeclaringType.Name+"."+m.Name;
            if (key!="NPC_AI2.ApplyDamage" && key!="NPC_AI2.DeathAction") throw new Exception("unexpected patch "+key);
            if (prefix!=null) Insert(Pre,key,prefix);
            if (postfix!=null) Insert(Post,key,postfix);
        }
        public static int Rank(HarmonyMethod h) { return h.priority<0?Priority.Normal:h.priority; }
        public static void Insert(Dictionary<string,List<HarmonyMethod>> d,string key,HarmonyMethod h) {
            List<HarmonyMethod> l;
            if (!d.TryGetValue(key,out l)) { l=new List<HarmonyMethod>(); d[key]=l; }
            int i=0;while (i<l.Count && Rank(l[i])>=Rank(h)) i++;     // stable: equal priority keeps patch order
            l.Insert(i,h);
        }
        public static bool Prefixes(string key,object instance,object[] args) { return Run(Pre,key,instance,args); }
        public static void Postfixes(string key,object instance,object[] args) { Run(Post,key,instance,args); }
        // HarmonyX: every patch runs in priority order; a false prefix skips the
        // original only. __args is filled once at entry and copied back into the
        // arguments after each patch that takes it.
        static bool Run(Dictionary<string,List<HarmonyMethod>> d,string key,object instance,object[] args) {
            List<HarmonyMethod> l;
            if (key==Off || !d.TryGetValue(key,out l)) return true;
            object[] entry=(object[])args.Clone();
            bool run=true;
            foreach (HarmonyMethod h in l) {
                ParameterInfo[] ps=h.method.GetParameters();
                object[] call=new object[ps.Length];bool usesArgs=false;
                for (int i=0;i<ps.Length;i++) {
                    string n=ps[i].Name;
                    if (n=="__instance") call[i]=instance;
                    else if (n=="__args") { call[i]=entry;usesArgs=true; }
                    else if (n=="__originalMethod") call[i]=null;
                    else call[i]=args[int.Parse(n.Substring(2))];
                }
                object r=h.method.Invoke(null,call);
                for (int i=0;i<ps.Length;i++) if (ps[i].ParameterType.IsByRef) args[int.Parse(ps[i].Name.Substring(2))]=call[i];
                if (usesArgs) for (int i=0;i<args.Length;i++) args[i]=entry[i];
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
    public static class FastCall { public static bool Bool(MethodInfo m,object o) { object r=m.Invoke(o,null);return r is bool && (bool)r; } }
    internal static class Mercs { internal static object UnitOf(object ai) { return null; } }
    public static class Loc { public static string T(string ru,string en) { return en; } }
    public static class RocketHook { public static int Pictures; public static void Detonate(Vector3 p,float dmg,float r,float life) { Pictures++; } }
    public class ConfigEntry<T> { public T Value; public ConfigEntry(T v) { Value=v; } }
    public static class PhotonNetwork { public static bool Master=true; public static int ActorId=1; public static bool isMasterClient { get { return Master; } } }
    public struct PhotonMessageInfo { }
    public class PhotonPlayer { int _id; public int ID { get { return _id; } } public PhotonPlayer(int i) { _id=i; } }
    public class PhotonView {
        public PhotonPlayer Owner;
        public bool isMine { get { return Owner.ID==PhotonNetwork.ActorId; } }
        public PhotonPlayer owner { get { return Owner; } }
        public NPC_AI2 Actor;
        public void RPC(string name,PhotonPlayer target,object[] args) { throw new Exception("this check keeps every NPC on the master"); }
    }
    public static class Extensions { public static PhotonView GetPhotonView(GameObject go) { return go.View; } }
    public class Home { public bool IsSafeSettlement; public int _lastKillerId=-1; public int Enemies; public float RespawnTimeInSec=1800f; }
    public class Specs { public float Health, HealthMax; }

    // Native contract, IL order (see the module docstring).
    public class PlayerRagdollController {
        public bool ActivePhysic=true, Active;
        public Rigidbody[] RigidBodies;
        public Component MainChar_Hips;
        public Dictionary<int,Rigidbody> DamagedParts=new Dictionary<int,Rigidbody>();
        public int[] JA, JB; public float[] JL;     // proxy joints
        public void SetRagdollActive(bool on) {
            if (!ActivePhysic) return;
            Active=on;
            foreach (Rigidbody b in RigidBodies) b.isKinematic=!on;
            if (on && !Sim.Live.Contains(this)) Sim.Live.Add(this);
        }
        public static float GetPhysicsForceByDamageType(int t) {
            switch (t) {
                case 0: case 1: return 10f;
                case 2: case 3: case 8: case 17: return 15f;
                case 4: case 18: return 20f;
                case 5: case 13: return 40f;
                case 14: return 60f;
                default: return 0f;
            }
        }
        public void ApplyPhysicsForce(int part,Vector3 dir,float force) {
            if (!DamagedParts.ContainsKey(part)) return;
            if (dir.x*dir.x+dir.y*dir.y+dir.z*dir.z<9.99999944E-11f) dir=Vector3.down;   // Vector3 == zero
            DamagedParts[part].velocity=dir*force;                                         // DamagedPart.ApplyForce
        }
    }
    public class NPC_AI2 : Component {
        public bool IsInitialized=true, GodModeEnabled, IsTalkActive, _isSafeSettlement, _isWoundedAction;
        public int BehaviorPattern, MainState, MaxEnemiesCount=3;
        public bool WoundRoll=true;
        public Home MySettlement=new Home();
        public Specs Specifications=new Specs();
        public PlayerRagdollController RagdollController;
        public int Applied, Deaths, Items=5, ReGranted;
        public bool AimIk=true, Destroyed;
        public float RespawnAt=-1f;
        public Vector3 AppliedDirection;
        public static List<object[]> Broadcast=new List<object[]>();
        public bool IsAlive() { return MainState!=3 && Specifications.Health>0; }
        public void ApplyDamage(float damage,int part,int type,int owner,Vector3 direction,PhotonMessageInfo info) {
            object[] args=new object[] { damage,part,type,owner,direction,info };
            if (!Harmony.Prefixes("NPC_AI2.ApplyDamage",this,args)) return;
            damage=(float)args[0];direction=(Vector3)args[4];
            if (!IsInitialized || !gameObject.View.isMine || MySettlement.IsSafeSettlement) return;   // IL_0000-IL_002D
            if (GodModeEnabled || _isWoundedAction || !IsAlive()) return;                           // IL_0034-IL_0055
            if (part!=1 || type!=14 || owner!=0) throw new Exception("incorrect blast parameters");
            Applied++;AppliedDirection=direction;
            if (BehaviorPattern!=1) {
                float v=damage;
                if (BehaviorPattern==3) v=v*MaxEnemiesCount/Mathf.Clamp(MySettlement.Enemies,1,MaxEnemiesCount);
                float h=Specifications.Health-v;                                                       // DecreaseHealth
                if (h<=0f) { SetHealthValue(0f,type,part,direction);return; }
                Specifications.Health=h;
            }
            if (MainState!=7 && Specifications.Health>0f && Specifications.Health<25f && WoundRoll) { _isWoundedAction=true;MainState=7; }
        }
        public void SetHealthValue(float h,int type,int part,Vector3 dir) {
            if (gameObject.View.isMine) Broadcast.Add(new object[] { this,h,type,part,dir });            // RPC to the others
            Specifications.Health=h;
            if (h<=0f && MainState!=3) DeathAction(type,part,dir);
        }
        public void DeathAction(int type,int part,Vector3 dir) {
            object[] args=new object[] { type,part,dir };
            if (Harmony.Prefixes("NPC_AI2.DeathAction",this,args)) {
                type=(int)args[0];part=(int)args[1];dir=(Vector3)args[2];
                Deaths++;
                RagdollController.SetRagdollActive(true);
                AimIk=false;                                                                           // _aimIk object, not the body
                RespawnAt=Time.time+MySettlement.RespawnTimeInSec;
                MainState=3;
                RagdollController.ApplyPhysicsForce(part,dir,PlayerRagdollController.GetPhysicsForceByDamageType(type));
                if (gameObject.View.isMine) ReGranted++;
            }
            Harmony.Postfixes("NPC_AI2.DeathAction",this,args);
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
    public static class FrameProf {
        public const int S_OrdnanceBlastT=140, S_BlastCorpseT=232;
        public static int Corpse, Open;
        public static void S(int n) { if (n==S_BlastCorpseT) { Corpse++;Open++; } }
        public static void E(int n) { if (n==S_BlastCorpseT) Open--; }
    }
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
        internal static void Drop(int id,Vector3 point,int kg) { Burst(id,point,false,kg); }
        /*AN2*/
    }
    /*LOAD*/

    // Terrain.SampleHeight over the real grid: bilinear, rows z, columns x.
    internal static class EastWorld {
        internal static float X0,Z0,Step; internal static int Nx,Nz; internal static float[] H;
        internal static bool TerrainHeight(Vector3 p,out float y) {
            y=0f;
            float fx=(p.x-X0)/Step, fz=(p.z-Z0)/Step;
            int ix=Mathf.FloorToInt(fx), iz=Mathf.FloorToInt(fz);
            if (ix<0 || iz<0 || ix>=Nx-1 || iz>=Nz-1) return false;
            float tx=fx-ix, tz=fz-iz;
            float a=H[iz*Nx+ix], b=H[iz*Nx+ix+1], c=H[(iz+1)*Nx+ix], d=H[(iz+1)*Nx+ix+1];
            y=(a*(1f-tx)+b*tx)*(1f-tz)+(c*(1f-tx)+d*tx)*tz;
            return true;
        }
    }

    // Proxy physics: position-based ragdoll over terrain (+ one optional roof).
    public static class Sim {
        public static List<PlayerRagdollController> Live=new List<PlayerRagdollController>();
        public static float G=9.81f, Mu=0.6f;
        public static bool Roof; public static float RoofX,RoofZ,RoofHalf,RoofTop;
        public static float Bias;      // forced sink per step (the "spot keeps rejecting him" case)
        public static bool Surface(Vector3 p,out float s) {
            bool ok=EastWorld.TerrainHeight(p,out s);
            if (Roof && Math.Abs(p.x-RoofX)<=RoofHalf && Math.Abs(p.z-RoofZ)<=RoofHalf && p.y>=RoofTop-1.5f) { s=ok?Math.Max(s,RoofTop):RoofTop;ok=true; }
            return ok;
        }
        public static void Step(float dt) { foreach (PlayerRagdollController rc in Live) if (rc.Active) Doll(rc,dt); }
        static void Doll(PlayerRagdollController rc,float dt) {
            Rigidbody[] b=rc.RigidBodies;
            for (int i=0;i<b.Length;i++) {
                if (b[i].isKinematic) continue;
                b[i].Prev=b[i].position;b[i].Push=0f;
                b[i].velocity.y-=G*dt;
                b[i].position=b[i].position+b[i].velocity*dt;
                b[i].position.y-=Bias;
            }
            for (int it=0;it<8;it++) {
                for (int j=0;j<rc.JA.Length;j++) {
                    Rigidbody p=b[rc.JA[j]], q=b[rc.JB[j]];
                    Vector3 d=q.position-p.position;float len=d.magnitude;
                    if (len<1e-6f) continue;
                    float wp=p.isKinematic?0f:1f/p.mass, wq=q.isKinematic?0f:1f/q.mass;
                    if (wp+wq<=0f) continue;
                    Vector3 c=d*((len-rc.JL[j])/len/(wp+wq));
                    p.position=p.position+c*wp;q.position=q.position-c*wq;
                }
                for (int i=0;i<b.Length;i++) {
                    if (b[i].isKinematic) continue;
                    float s;if (!Surface(b[i].position,out s)) continue;
                    float above=b[i].position.y-s;
                    if (above>=b[i].Radius || above<=-b[i].Radius) continue;   // free, or tunnelled through
                    float push=b[i].Radius-above;b[i].position.y+=push;b[i].Push+=push;
                }
            }
            for (int i=0;i<b.Length;i++) {
                if (b[i].isKinematic) continue;
                if (b[i].Push>0f) {                                            // Coulomb friction from the contact depth
                    float dx=b[i].position.x-b[i].Prev.x, dz=b[i].position.z-b[i].Prev.z;
                    float t=(float)Math.Sqrt(dx*dx+dz*dz), lim=Mu*b[i].Push;
                    float k=t<=lim?0f:(t-lim)/t;
                    b[i].position.x=b[i].Prev.x+dx*k;b[i].position.z=b[i].Prev.z+dz*k;
                }
                b[i].velocity=(b[i].position-b[i].Prev)*(1f/dt);
            }
        }
    }

    public static class Check {
        static void Assert(bool b,string s) { if (!b) throw new Exception("FAIL: "+s); }
        const float K=2.8f, Dt=0.02f;
        static int _ids=1000, _bomb=100;
        static readonly float[] BoneXYZ={ 0,1.0f,0, 0,1.35f,0, 0,1.68f,0, -0.22f,1.3f,0, -0.22f,1.0f,0, 0.22f,1.3f,0, 0.22f,1.0f,0, -0.1f,0.72f,0, -0.1f,0.3f,0, 0.1f,0.72f,0, 0.1f,0.3f,0 };
        static readonly float[] BoneMass={ 3f,3f,1.1f,0.8f,0.6f,0.8f,0.6f,1.6f,1f,1.6f,1f };
        static readonly float[] BoneR={ 0.15f,0.15f,0.11f,0.07f,0.06f,0.07f,0.06f,0.08f,0.07f,0.08f,0.07f };
        static readonly int[] JointA={ 0,1,1,3,1,5,0,7,0,9 };
        static readonly int[] JointB={ 1,2,3,4,5,6,7,8,9,10 };

        static void Host() { PhotonNetwork.Master=true;PhotonNetwork.ActorId=1; }
        static void Drain() { for (int i=0;i<2000;i++) { Time.frameCount++; OrdnanceBlast.Tick(); } }
        static void Frames(int n) { for (int f=0;f<n;f++) { Time.time+=Dt;Time.frameCount++;Sim.Step(Dt);BlastCorpse.Tick(); } }

        static NPC_AI2 Man(Vector3 feet,float hp,float limbs) {
            NPC_AI2 n=new NPC_AI2();
            n.gameObject.name="Marauder_NPC_01(Clone)_"+(++_ids);
            n.transform.position=feet;
            n.Specifications.Health=hp;n.Specifications.HealthMax=hp;
            n.gameObject.View=new PhotonView();n.gameObject.View.Owner=new PhotonPlayer(1);n.gameObject.View.Actor=n;
            PlayerRagdollController rc=new PlayerRagdollController();
            Rigidbody[] bodies=new Rigidbody[BoneMass.Length];
            for (int i=0;i<bodies.Length;i++) {
                Rigidbody b=new Rigidbody();
                b.position=feet+new Vector3(BoneXYZ[3*i]*K,BoneXYZ[3*i+1]*K,BoneXYZ[3*i+2]*K);
                b.mass=BoneMass[i]*(i<2?1f:limbs);b.Radius=BoneR[i]*K;
                bodies[i]=b;
            }
            rc.RigidBodies=bodies;rc.MainChar_Hips=bodies[0];
            rc.DamagedParts[1]=bodies[1];rc.DamagedParts[0]=bodies[2];   // Body -> chest, Head -> head
            rc.JA=JointA;rc.JB=JointB;rc.JL=new float[JointA.Length];
            for (int j=0;j<JointA.Length;j++) rc.JL[j]=(bodies[JointB[j]].position-bodies[JointA[j]].position).magnitude;
            n.RagdollController=rc;
            return n;
        }
        static NPC_AI2 Kill(Vector3 burst,Vector3 feet,float limbs) {
            NPC_AI2 n=Man(feet,150f,limbs);
            NpcScan.Items=new Component[] { n };
            Host();An2Bombs.Drop(++_bomb,burst,100);Drain();
            Assert(!n.IsAlive(),n.name+" survived the FAB-100 at "+(Vector3.Distance(feet,burst)/K).ToString("0.0")+" m");
            return n;
        }
        static Vector3 Hips(NPC_AI2 n) { return n.RagdollController.RigidBodies[0].position; }
        static float Clear(Vector3 p) { float s;return Sim.Surface(p,out s)?p.y-s:-1e6f; }
        static float TerrainClear(Vector3 p) { float s;return EastWorld.TerrainHeight(p,out s)?p.y-s:-1e6f; }
        static float MinBone(NPC_AI2 n,bool terrainOnly) {
            float m=1e9f;
            foreach (Rigidbody b in n.RagdollController.RigidBodies) m=Math.Min(m,terrainOnly?TerrainClear(b.position):Clear(b.position));
            return m;
        }
        static float Flat(Vector3 a,Vector3 b) { float dx=a.x-b.x,dz=a.z-b.z;return (float)Math.Sqrt(dx*dx+dz*dz); }

        // Feet on the surface at 'metres' 3D distance from the burst, along 'bearing'.
        static Vector3 Place(Vector3 burst,float metres,float bearing) {
            float r=metres*K, s=0f;
            for (int i=0;i<30;i++) {
                Vector3 p=new Vector3(burst.x+r*Mathf.Cos(bearing),burst.y+50f,burst.z+r*Mathf.Sin(bearing));
                Assert(Sim.Surface(p,out s),"placement off the terrain grid");
                float dy=s-burst.y;r=(float)Math.Sqrt(Math.Max(0f,metres*K*metres*K-dy*dy));
            }
            return new Vector3(burst.x+r*Mathf.Cos(bearing),s,burst.z+r*Mathf.Sin(bearing));
        }

        // One man, one burst, 10 s of proxy physics. Prints a RUN line.
        public static void Run(string mode,int kill,float[] burstXyz,float metres,int bearings,float limbs) {
            Harmony.Off=mode=="old"?"NPC_AI2.DeathAction":null;
            Vector3 burst=new Vector3(burstXyz[0],burstXyz[1],burstXyz[2]);
            float ground;EastWorld.TerrainHeight(burst,out ground);
            Sim.Roof=burst.y-ground>3f*K;Sim.RoofX=burst.x;Sim.RoofZ=burst.z;Sim.RoofHalf=5f*K;Sim.RoofTop=burst.y;
            for (int k=0;k<bearings;k++) {
                Sim.Live.Clear();
                Vector3 feet=Place(burst,metres,k*2f*Mathf.PI/bearings);
                int recalled=BlastCorpse.Recalled;
                NPC_AI2 n=Kill(burst,feet,limbs);
                Vector3 kick=n.RagdollController.RigidBodies[1].velocity;
                Frames(500);
                Vector3 h=Hips(n);float speed=0f;
                foreach (Rigidbody b in n.RagdollController.RigidBodies) speed=Math.Max(speed,b.velocity.magnitude);
                bool kept=!n.Destroyed && !n.IsAlive() && n.Items==5 && n.RagdollController.Active && n.RespawnAt>=Time.time-10.1f+1799f;
                Console.WriteLine("RUN {0} {1} {2} {3:0.###} {4:0.###} {5:0.###} {6:0.###} {7:0.###} {8} {9}",
                    mode,kill,k,kick.magnitude,Flat(h,feet)/K,Clear(h)/K,MinBone(n,false)/K,speed,BlastCorpse.Recalled-recalled,kept?1:0);
            }
            Harmony.Off=null;Sim.Roof=false;
        }

        static void Unit() {
            Sim.Live.Clear();Sim.Roof=false;
            // Seams: patch order on DeathAction and ApplyDamage.
            List<HarmonyMethod> pre=Harmony.Pre["NPC_AI2.DeathAction"];
            Assert(pre.Count==1 && pre[0].method.Name=="NativeForcePrefix" && pre[0].priority==Priority.Last,"DeathAction prefix is BlastCorpse's, Priority.Last");
            Assert(Harmony.Post["NPC_AI2.DeathAction"][0].method.Name=="WatchPostfix","DeathAction postfix watches the corpse");

            Vector3 flatBurst=new Vector3(-1364f,531f,1742f);
            // 1. Contract: ApplyDamage keeps the raw displacement (FlakPositions rebuilds the burst
            //    point from it); DeathAction gets the native unit direction; the chest gets 60 u/s.
            Vector3 feet=Place(flatBurst,12f,0.3f);
            NPC_AI2 a=Kill(flatBurst,feet,1f);
            Vector3 raw=feet-flatBurst, v=a.RagdollController.RigidBodies[1].velocity;
            Assert(Vector3.Distance(a.AppliedDirection,raw)<1e-3f,"ApplyDamage receives the full displacement");
            Assert(Math.Abs(v.magnitude-60f)<0.01f && Math.Abs(v.y)<1e-4f,"explosion corpse thrown at the native 60 u/s horizontal, got "+v);
            float rawX=raw.x/(float)Math.Sqrt(raw.x*raw.x+raw.z*raw.z);
            Assert(Math.Abs(v.x/60f-rawX)<1e-4f,"away from the burst");
            Assert(a.Deaths==1 && a.RespawnAt>0f && !a.AimIk && a.ReGranted==1 && a.Items==5 && a.RagdollController.Active,"native death: ragdoll, respawn timer, loot regranted");
            Assert(BlastCorpse.Straightened>0 && RevivalPlugin.L.Info.Exists(delegate(string l) { return l.StartsWith("BlastCorpse: ") && l.Contains("killed by a blast 12.0 m away"); }),"force line logged");
            Console.WriteLine("PASS contract: ApplyDamage keeps the {0:0} u displacement; DeathAction throws the chest at {1:0.0} u/s (6.67: {2:0} u/s)",raw.magnitude,v.magnitude,raw.magnitude*60f);

            // 2. Remote client: the owner's SetHealthValue RPC carries the raw vector; the
            //    replica's DeathAction straightens it too.
            object[] rpc=NPC_AI2.Broadcast[NPC_AI2.Broadcast.Count-1];
            Assert(rpc[0]==a && Vector3.Distance((Vector3)rpc[4],raw)<1e-3f,"SetHealthValue RPC carries the raw direction");
            NPC_AI2 replica=Man(feet,150f,1f);PhotonNetwork.ActorId=2;PhotonNetwork.Master=false;
            replica.SetHealthValue((float)rpc[1],(int)rpc[2],(int)rpc[3],(Vector3)rpc[4]);Host();
            Vector3 rv=replica.RagdollController.RigidBodies[1].velocity;
            Assert(!replica.IsAlive() && Math.Abs(rv.magnitude-60f)<0.01f && replica.ReGranted==0,"remote corpse thrown at 60 u/s, items stay with the owner");
            Console.WriteLine("PASS remote client: SetHealthValue replay gives the replica the same 60 u/s throw");

            // 3. Directions: zero (burst at the feet), native unit, non-explosion untouched.
            NPC_AI2 z=Man(feet,150f,1f);z.transform.forward=new Vector3(0,0,1);
            z.SetHealthValue(0f,14,1,Vector3.zero);
            Vector3 zv=z.RagdollController.RigidBodies[1].velocity;
            Assert(Math.Abs(zv.z+60f)<1e-3f && Math.Abs(zv.y)<1e-4f,"burst under the man: thrown backwards, not Vector3.down x 60 into the ground, got "+zv);
            NPC_AI2 nat=Man(feet,150f,1f);nat.SetHealthValue(0f,14,1,new Vector3(0.6f,0f,0.8f));
            Assert(Vector3.Distance(nat.RagdollController.RigidBodies[1].velocity,new Vector3(36f,0f,48f))<1e-3f,"native explosion direction unchanged");
            NPC_AI2 shot=Man(feet,150f,1f);shot.SetHealthValue(0f,0,1,new Vector3(0f,-3f,4f));
            Assert(Vector3.Distance(shot.RagdollController.RigidBodies[1].velocity,new Vector3(0f,-30f,40f))<1e-3f,"bullet death untouched (type 0, force 10)");
            Console.WriteLine("PASS directions: zero -> backwards 60 u/s, native unit vector unchanged, bullet deaths untouched");

            // 4. Crew.RemoteMessagePrefix takes __args at Normal priority; Harmony writes the
            //    entry array back after it. BlastCorpse runs Last and keeps its change.
            HarmonyMethod crew=new HarmonyMethod(typeof(Check).GetMethod("CrewLike"));
            Harmony.Insert(Harmony.Pre,"NPC_AI2.DeathAction",crew);
            NPC_AI2 c=Man(feet,150f,1f);c.SetHealthValue(0f,14,1,raw);
            Assert(Math.Abs(c.RagdollController.RigidBodies[1].velocity.magnitude-60f)<0.01f && _crewSaw>0,"Last prefix survives the __args write-back");
            pre.Remove(crew);
            Console.WriteLine("PASS Harmony order: BlastCorpse (Last) runs after the __args prefix, its unit vector reaches the original");

            // 5. Frozen settlement: SetRagdollActive returns early, the man stays standing; never recalled.
            Sim.Live.Clear();
            NPC_AI2 f=Man(feet,150f,1f);f.RagdollController.ActivePhysic=false;
            int before=BlastCorpse.Recalled;f.SetHealthValue(0f,14,1,raw);Frames(500);
            Assert(Flat(Hips(f),feet)<1e-3f && BlastCorpse.Recalled==before && !f.IsAlive(),"frozen corpse stays where he stood");
            Console.WriteLine("PASS frozen settlement: kinematic corpse left standing (native), no recall");
        }

        static int _crewSaw;
        public static bool CrewLike(object __instance,object[] __args) { _crewSaw++;return true; }

        // Forced losses on the slope south-west of the grid: the watch must lay the
        // corpse back on the death spot, every bone above the terrain.
        static void Losses() {
            Sim.Live.Clear();Sim.Roof=false;
            Vector3 slope=new Vector3(SlopeX,0f,SlopeZ);float g;
            Assert(EastWorld.TerrainHeight(slope,out g),"slope on the grid");
            Vector3 feet=new Vector3(slope.x,g,slope.z), burst=feet+new Vector3(-20f,0.2f,5f);

            // a) sunk: every bone 3 m under the terrain (a tunnelled ragdoll).
            NPC_AI2 s=Man(feet,150f,1f);s.SetHealthValue(0f,14,1,feet-burst);Frames(20);
            Vector3[] pose=Offsets(s);
            foreach (Rigidbody b in s.RagdollController.RigidBodies) { b.position.y-=3f*K;b.velocity=new Vector3(0,-30f,0); }
            int r0=BlastCorpse.Recalled;Sim.Live.Clear();
            for (int i=0;i<15;i++) { Time.time+=Dt;BlastCorpse.Tick(); }
            Vector3 h=Hips(s);
            Assert(BlastCorpse.Recalled==r0+1,"sunk corpse recalled once");
            Assert(Flat(h,feet)<1e-3f,"hips back over the death spot");
            Assert(MinBone(s,true)>=BlastCorpse.ClearU-1e-3f,"every bone at least ClearU over the terrain: min "+(MinBone(s,true)/K).ToString("0.00")+" m");
            Assert(TerrainClear(h)>=BlastCorpse.LiftU-1e-3f,"hips at least LiftU over the terrain");
            Assert(SamePose(s,pose),"pose kept (one offset for every bone)");
            foreach (Rigidbody b in s.RagdollController.RigidBodies) Assert(b.velocity.magnitude==0f,"velocities cleared");
            Console.WriteLine("PASS sunk: on the {0:0} deg slope a corpse 3 m under the terrain -> hips {1:0.00} m over it at the death spot, lowest bone {2:0.00} m over",
                SlopeDeg,TerrainClear(h)/K,MinBone(s,true)/K);
            Sim.Live.Add(s.RagdollController);Frames(300);
            Assert(Flat(Hips(s),feet)<2f*K && MinBone(s,true)>=0f,"settles on the spot, above the terrain");
            Console.WriteLine("PASS sunk settle: after 6 s the corpse lies {0:0.0} m from the spot, lowest bone {1:0.00} m over the terrain",
                Flat(Hips(s),feet)/K,MinBone(s,true)/K);

            // b) thrown 60 m.
            Sim.Live.Clear();
            NPC_AI2 t=Man(feet,150f,1f);t.SetHealthValue(0f,14,1,feet-burst);Frames(20);
            foreach (Rigidbody b in t.RagdollController.RigidBodies) b.position=b.position+new Vector3(60f*K,0f,0f);
            r0=BlastCorpse.Recalled;Sim.Live.Clear();
            for (int i=0;i<15;i++) { Time.time+=Dt;BlastCorpse.Tick(); }
            Assert(BlastCorpse.Recalled==r0+1 && Flat(Hips(t),feet)<1e-3f && MinBone(t,true)>=BlastCorpse.ClearU-1e-3f,"thrown corpse laid back on the spot");
            Console.WriteLine("PASS thrown: 60 m away -> back on the death spot, lowest bone {0:0.00} m over the terrain",MinBone(t,true)/K);

            // c) cellar: died 3 m under the terrain - never lifted to the surface; a throw comes back to the cellar.
            Sim.Live.Clear();
            Vector3 cellar=feet-new Vector3(0,3f*K,0);
            NPC_AI2 u=Man(cellar,150f,1f);u.SetHealthValue(0f,14,1,new Vector3(1,0,0));
            Sim.Live.Clear();
            foreach (Rigidbody b in u.RagdollController.RigidBodies) b.position.y-=0.5f*K;     // lies on the cellar floor
            r0=BlastCorpse.Recalled;
            for (int i=0;i<15;i++) { Time.time+=Dt;BlastCorpse.Tick(); }
            Assert(BlastCorpse.Recalled==r0 && TerrainClear(Hips(u))<0f,"cellar corpse under the terrain is not 'sunk'");
            foreach (Rigidbody b in u.RagdollController.RigidBodies) b.position=b.position+new Vector3(0,0,20f*K);
            for (int i=0;i<15;i++) { Time.time+=Dt;BlastCorpse.Tick(); }
            float lowest=1e9f;foreach (Rigidbody b in u.RagdollController.RigidBodies) lowest=Math.Min(lowest,b.position.y);
            Assert(BlastCorpse.Recalled==r0+1 && Flat(Hips(u),cellar)<1e-3f && Hips(u).y>=cellar.y+BlastCorpse.LiftU-1e-3f,"thrown cellar corpse back over his cellar spot");
            Assert(lowest>=cellar.y+BlastCorpse.ClearU-1e-3f && TerrainClear(Hips(u))<0f,"every bone over the cellar floor, the corpse still under the terrain (not lifted out)");
            Console.WriteLine("PASS cellar: a man dead 3 m under the terrain stays there; a throw returns him to his cellar floor");

            // d) a spot that keeps rejecting him: three recalls, then the watch lets go.
            Time.time+=9f;BlastCorpse.Tick();
            Assert(BlastCorpse.Watched==0,"earlier watches ended");
            Sim.Live.Clear();Sim.Bias=2f*K;
            NPC_AI2 w=Man(feet,150f,1f);r0=BlastCorpse.Recalled;int watched=0;
            w.SetHealthValue(0f,14,1,feet-burst);
            Frames(150);Sim.Bias=0f;
            Assert(BlastCorpse.Recalled-r0==BlastCorpse.MaxRecalls,"three recalls, got "+(BlastCorpse.Recalled-r0));
            Assert(BlastCorpse.Watched==watched,"then dropped from the watch");
            Console.WriteLine("PASS give-up: a spot that sinks him every frame gets {0} recalls, then the game keeps him",BlastCorpse.MaxRecalls);
        }

        static Vector3[] Offsets(NPC_AI2 n) {
            Rigidbody[] b=n.RagdollController.RigidBodies;Vector3[] o=new Vector3[b.Length];
            for (int i=0;i<b.Length;i++) o[i]=b[i].position-b[0].position;
            return o;
        }
        static bool SamePose(NPC_AI2 n,Vector3[] o) {
            Vector3[] now=Offsets(n);
            for (int i=0;i<o.Length;i++) if (Vector3.Distance(now[i],o[i])>1e-3f) return false;
            return true;
        }

        // Slots, idle cost, allocation and time per Tick.
        static void Budget() {
            Sim.Live.Clear();Sim.Roof=false;
            Time.time+=20f;for (int i=0;i<3;i++) BlastCorpse.Tick();
            Assert(BlastCorpse.Watched==0,"every watch ended after 8 s");
            int s0=FrameProf.Corpse;
            for (int i=0;i<10000;i++) BlastCorpse.Tick();
            Assert(FrameProf.Corpse==s0,"idle: no F6 slot entry while no corpse is watched");

            Vector3 burst=new Vector3(-1298f,531f,1727f);
            Component[] ring=new Component[70];
            for (int i=0;i<ring.Length;i++) ring[i]=Man(Place(burst,8f+0.1f*(i%10),i*2f*Mathf.PI/ring.Length),150f,1f);
            NpcScan.Items=ring;Host();An2Bombs.Drop(++_bomb,burst,100);Drain();
            foreach (Component m in ring) Assert(!((NPC_AI2)m).IsAlive(),"ring man killed");
            Assert(BlastCorpse.Watched==BlastCorpse.Slots,"70 deaths fill the 64 slots, got "+BlastCorpse.Watched);
            Frames(100);                                            // settle 2 s
            Sim.Live.Clear();
            int recalled=BlastCorpse.Recalled;
            System.Diagnostics.Stopwatch sw=new System.Diagnostics.Stopwatch();long worst=0;
            Time.time+=0.001f;BlastCorpse.Tick();                  // warm (JIT) before the reading
            long gc0=GC.GetTotalMemory(true);
            for (int i=0;i<5000;i++) {
                Time.time+=0.001f;
                long t0=sw.ElapsedTicks;sw.Start();BlastCorpse.Tick();sw.Stop();
                worst=Math.Max(worst,sw.ElapsedTicks-t0);
            }
            long gc1=GC.GetTotalMemory(false);
            Assert(BlastCorpse.Recalled==recalled,"resting corpses are not recalled");
            Assert(BlastCorpse.Watched==BlastCorpse.Slots,"still watched during the measurement");
            Assert(FrameProf.Open==0,"F6 slot balanced");
            double avgUs=sw.Elapsed.TotalMilliseconds*1000.0/5000.0, peakUs=worst*1e6/System.Diagnostics.Stopwatch.Frequency;
            Assert(gc1-gc0<=0,"steady-state Tick allocates nothing: "+(gc1-gc0)+" bytes over 5000 ticks");
            Console.WriteLine("PASS budget: 64 watched corpses, 5000 ticks over 5 s (about 1280 checks at 4 Hz): {0} bytes allocated; avg {1:0.00} us, peak {2:0.0} us per Tick (.NET CLR)",
                gc1-gc0,avgUs,peakUs);
            Console.WriteLine("BUDGET {0:0.000} {1:0.000}",avgUs,peakUs);
            Time.time+=20f;BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();BlastCorpse.Tick();
            Assert(BlastCorpse.Watched==0,"ring watches end");
        }

        public static float SlopeX, SlopeZ, SlopeDeg;
        public static void Setup(float x0,float z0,float step,int nx,int nz,float[] h) {
            System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
            EastWorld.X0=x0;EastWorld.Z0=z0;EastWorld.Step=step;EastWorld.Nx=nx;EastWorld.Nz=nz;EastWorld.H=h;
            Harmony harmony=new Harmony();
            OrdnanceBlast.Install(harmony);BlastKill.Install(harmony);BlastCorpse.Install(harmony);
            Assert(RevivalPlugin.L.Info.Exists(delegate(string l) { return l.StartsWith("BlastCorpse: explosion deaths get the native unit force; corpses watched 8 s"); }),"install line: watch ON");
            Time.time=100f;
        }
        public static void Tests() { Unit();Losses();Budget(); }
    }
}
'''

DRIVER = r'''
namespace NextDayRevival {
    public static class Driver {
        public static void Main() {
            Check.Setup(/*GRID*/);
            /*SLOPE*/
            Check.Tests();
            /*RUNS*/
        }
    }
}
'''


def floats(values):
    return 'new float[] { ' + ','.join('%gf' % v for v in values) + ' }'


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

    data = json.loads((ROOT / 'research' / 'bomb_corpse_terrain.json').read_text(encoding='utf-8'))
    heights = [h for row in data['heights'] for h in row]
    assert len(heights) == data['nx'] * data['nz']
    grid = '%gf,%gf,%gf,%d,%d,%s' % (data['x0'], data['z0'], data['step'], data['nx'], data['nz'], floats(heights))
    bursts = data['bursts_6_67']
    kills = data['kills_6_67']
    bearings = 8
    runs = []
    for mode, limbs in (('old', 1.0), ('new', 1.0), ('light', 0.3)):
        for i, (b, metres, _name) in enumerate(kills):
            runs.append('Check.Run("%s",%d,%s,%gf,%d,%gf);' % (
                'old' if mode == 'old' else mode, i, floats(bursts[b]), metres, bearings, limbs))
    source = HARNESS.replace('/*GUARDS*/', guards).replace('/*AN2*/', producer).replace('/*LOAD*/', load)
    # Death spot for the forced losses: the grid point whose slope is closest to 15 degrees.
    hs, nx, nz, step = data['heights'], data['nx'], data['nz'], data['step']
    best = None
    for iz in range(2, nz - 3):
        for ix in range(2, nx - 3):
            gx = (hs[iz][ix + 1] - hs[iz][ix - 1]) / (2 * step)
            gz = (hs[iz + 1][ix] - hs[iz - 1][ix]) / (2 * step)
            deg = math.degrees(math.atan(math.hypot(gx, gz)))
            if best is None or abs(deg - 15.0) < abs(best[0] - 15.0):
                best = (deg, data['x0'] + ix * step, data['z0'] + iz * step)
    print('Forced-loss spot: x %.0f z %.0f, terrain slope %.1f deg' % (best[1], best[2], best[0]))
    slope = 'Check.SlopeX=%gf;Check.SlopeZ=%gf;Check.SlopeDeg=%gf;' % (best[1], best[2], best[0])
    source += DRIVER.replace('/*GRID*/', grid).replace('/*SLOPE*/', slope).replace(
        '/*RUNS*/', '\n            '.join(runs))

    build = ROOT / 'build' / 'bomb-corpse-check'
    build.mkdir(parents=True, exist_ok=True)
    cs, exe = build / 'Harness.cs', build / 'Harness.exe'
    cs.write_text(source, encoding='utf-8')
    compiler = Path(r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe')
    compiled = subprocess.run([str(compiler), '/nologo', '/warn:0', '/codepage:65001', '/main:NextDayRevival.Driver',
                               '/out:' + str(exe), str(cs), str(ROOT / 'Revival.OrdnanceBlast.cs'),
                               str(ROOT / 'Revival.BlastKill.cs'), str(ROOT / 'Revival.BlastCorpse.cs')],
                              capture_output=True, text=True, encoding='utf-8', errors='replace')
    if compiled.returncode:
        raise AssertionError(compiled.stdout + compiled.stderr)
    result = subprocess.run([str(exe)], capture_output=True, text=True)
    if result.returncode:
        print(result.stdout, end='')
        raise AssertionError(result.stderr)

    assert 'WARNING' not in result.stdout, result.stdout
    rows = {}
    budget = None
    for line in result.stdout.splitlines():
        if line.startswith('RUN '):
            f = line.split()
            rows.setdefault(f[1], []).append(dict(
                kill=int(f[2]), kick=float(f[4]), dist=float(f[5]), hips=float(f[6]), low=float(f[7]),
                speed=float(f[8]), recalls=int(f[9]), kept=f[10] == '1'))
        elif line.startswith('BUDGET '):
            budget = [float(x) for x in line.split()[1:]]

    def found(r):
        # A lootable corpse: within 15 m of where he died, on the ground (hips centre
        # over the surface, no bone under it), at rest, NPC object dead and intact.
        return r['dist'] <= 15.0 and r['hips'] >= 0.0 and r['low'] >= -0.01 and r['speed'] < 1.0 and r['kept']

    print('6.67 kills (burst, logged distance) x %d bearings, 10 s after the burst; distance/height of the hips in m' % bearings)
    print('  kill  burst  dist |  6.67 throw  found  median dist |  C-W1 throw found  max dist  min bone over ground  recalls')
    for i, (b, metres, name) in enumerate(kills):
        old = [r for r in rows['old'] if r['kill'] == i]
        new = [r for r in rows['new'] if r['kill'] == i]
        od = sorted(r['dist'] for r in old)
        print('  %4d  %5d  %4.1f | %8.0f u/s  %d/%d  %10s |  %5.0f u/s  %d/%d  %6.1f m  %8.2f m  %10d   %s' % (
            i, b, metres, max(r['kick'] for r in old), sum(found(r) for r in old), len(old),
            ('%.0f m' % od[len(od) // 2]) if od[len(od) // 2] < 1e5 else 'gone',
            max(r['kick'] for r in new), sum(found(r) for r in new), len(new),
            max(r['dist'] for r in new), min(r['low'] for r in new), sum(r['recalls'] for r in new), name))
    total = lambda mode: (sum(found(r) for r in rows[mode]), len(rows[mode]))
    o, n, light = total('old'), total('new'), total('light')
    print('6.67 death path: %d/%d corpses found; C-W1: %d/%d; C-W1 with 30 %% limb masses (harder throw): %d/%d' % (
        o[0], o[1], n[0], n[1], light[0], light[1]))
    assert n[0] == n[1], 'every C-W1 corpse must be found'
    assert light[0] == light[1], 'every light-limb C-W1 corpse must be found'
    assert o[0] < o[1] // 4, 'the 6.67 path must reproduce the missing bodies'
    for r in rows['new'] + rows['light']:
        assert r['low'] >= -0.01 and r['hips'] >= 0.0, r
    print('PASS 6.67 bursts: no body near the death spot before; every body lootable on the ground after')
    for line in result.stdout.splitlines():
        if line.startswith('PASS'):
            print(line)
    assert budget is not None

    # Seams of the live game that the harness cannot execute.
    plugin = (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8')
    assert plugin.index('BlastKill.Install(_harmony);') < plugin.index('BlastCorpse.Install(_harmony);')
    tick = plugin.index('OrdnanceBlast.Tick();')
    assert tick < plugin.index('BlastCorpse.Tick();') < tick + 400
    prof = (ROOT / 'RevivalFrameProfiler.cs').read_text(encoding='utf-8')
    slot = int(prof.split('public const int S_BlastCorpseT = ')[1].split(';')[0])
    count = int(prof.split('public const int Count = ')[1].split(';')[0])
    names = prof[prof.index('static readonly string[] Names'):]
    names = names[:names.index('};')]
    assert slot == count - 1 and names.count('"') // 2 == count and names.rstrip().endswith('"BlastCorpse.Tick",')
    corpse = (ROOT / 'Revival.BlastCorpse.cs').read_text(encoding='utf-8')
    assert all(ord(ch) < 128 for ch in corpse) and '\r\n' not in corpse
    for path in ('Revival.BlastCorpse.cs', 'Revival.BlastKill.cs', 'Revival.OrdnanceBlast.cs'):
        text = (ROOT / path).read_text(encoding='utf-8')
        for forbidden in ('Destroy(', 'SetActive(', 'PhotonNetwork.Destroy'):
            assert forbidden not in text, (path, forbidden)
    code = '\n'.join(line.split('//')[0] for line in corpse.splitlines())
    for forbidden in ('Physics.', 'FindObjects', 'GetComponents', 'void Update(', '.Bind('):
        assert forbidden not in code, forbidden
    print('PASS seams: installed after BlastKill, ticked after OrdnanceBlast in its own F6 slot %d "BlastCorpse.Tick", '
          'no destroy/deactivate in the death path, no physics query, no config switch (always on)' % slot)
    print('BOMB CORPSE CHECK PASS')


if __name__ == '__main__':
    main()

// Deterministic engine/prerequisite doubles. Production fetch code is unchanged.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace UnityEngine
{
    public class Object
    {
        static int seq; int id = ++seq; public bool Dead;
        public int GetInstanceID() { return id; }
        public static bool operator ==(Object a, Object b)
        { bool an = ReferenceEquals(a, null) || a.Dead, bn = ReferenceEquals(b, null) || b.Dead; return an || bn ? an == bn : ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) { return !(a == b); }
        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return id; }
    }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 right { get { return new Vector3(1,0,0); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 down { get { return new Vector3(0, -1, 0); } }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x*b,a.y*b,a.z*b); }
        public static Vector3 operator /(Vector3 a, float b) { return a*(1f/b); }
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a+(b-a)*Mathf.Clamp01(t); }
    }
    public struct Quaternion { public static Quaternion identity { get { return new Quaternion(); } } }
    public static class Mathf
    {
        public static float Clamp01(float f) { return Math.Max(0, Math.Min(1, f)); }
        public static float Sqrt(float f) { return (float)Math.Sqrt(f); }
        public static float Abs(float f) { return Math.Abs(f); }
        public static float Lerp(float a, float b, float t) { return a+(b-a)*Clamp01(t); }
    }
    public class Transform : Object
    {
        Transform p; Vector3 local;
        public Vector3 localScale = new Vector3(1,1,1); public Quaternion localRotation;
        public Transform parent { get { return p; } set { Vector3 pos=position;p=value;position=pos; } }
        public Vector3 position { get { return p == null ? local : p.position+local; } set { local=p==null?value:value-p.position; } }
        public Vector3 localPosition { get { return local; } set { local=value; } }
        public Vector3 TransformPoint(Vector3 v) { return position+v; }
        public bool IsChildOf(Transform t) { for(Transform a=this;a!=null;a=a.parent)if(ReferenceEquals(a,t))return true;return false; }
    }
    public class Component : Object
    {
        public GameObject gameObject; public Transform transform { get { return gameObject.transform; } }
        public Component GetComponent(Type t) { return gameObject.GetComponent(t); }
        public T GetComponent<T>() where T : Component { return gameObject.GetComponent<T>(); }
        public T[] GetComponentsInChildren<T>(bool all) where T : Component { return gameObject.GetComponentsInChildren<T>(all); }
    }
    public class GameObject : Object
    {
        public Transform transform = new Transform(); readonly List<Component> parts=new List<Component>();
        public T Add<T>() where T : Component,new() { T t=new T();t.gameObject=this;parts.Add(t);return t; }
        public Component GetComponent(Type t) { for(int i=0;i<parts.Count;i++)if(t.IsInstanceOfType(parts[i]))return parts[i];return null; }
        public T GetComponent<T>() where T : Component { return GetComponent(typeof(T)) as T; }
        public T[] GetComponentsInChildren<T>(bool all) where T : Component
        { List<T> v=new List<T>();for(int i=0;i<parts.Count;i++)if(parts[i] is T)v.Add((T)parts[i]);return v.ToArray(); }
    }
    public class Rigidbody : Component { public bool isKinematic; public Vector3 velocity; }
    public class Collider : Component { public bool enabled=true; }
    public struct RaycastHit { public Collider collider;public Vector3 point,normal;public float distance; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics
    {
        public static RaycastHit[] Scene=new RaycastHit[0]; public static Collider[] Blockers=new Collider[0];public static int Layers, Calls;
        public static int RaycastNonAlloc(Vector3 p,Vector3 d,RaycastHit[] hits,float range,int mask,QueryTriggerInteraction q)
        { Layers=mask;Calls++;int n=Math.Min(Scene.Length,hits.Length);Array.Copy(Scene,hits,n);if(n==1){hits[0].point=new Vector3(p.x,80,p.z);hits[0].distance=p.y-80;}return n; }
        public static int OverlapBoxNonAlloc(Vector3 p,Vector3 size,Collider[] hits,Quaternion q,int mask,QueryTriggerInteraction trigger)
        { Layers=mask;int n=Math.Min(hits.Length,Blockers.Length);Array.Copy(Blockers,hits,n);return n; }
    }
    public static class Time { public static float time; }
}
namespace HarmonyLib
{
    public class HarmonyMethod { public HarmonyMethod(MethodInfo m) { if(m==null)throw new Exception("missing hook"); } }
    public class Harmony
    { public int Patches;public void Patch(MethodInfo m,HarmonyMethod a,HarmonyMethod b,HarmonyMethod c,HarmonyMethod d,HarmonyMethod e)
        { if(m==null)throw new Exception("missing native method");Patches++; } }
    public static class AccessTools
    {
        const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        public static FieldInfo Field(Type t,string n) { return t==null?null:t.GetField(n,F); }
        public static PropertyInfo Property(Type t,string n) { return t==null?null:t.GetProperty(n,F); }
        public static MethodInfo PropertyGetter(Type t,string n) { PropertyInfo p=Property(t,n);return p==null?null:p.GetGetMethod(true); }
        public static MethodInfo Method(Type t,string n,Type[] args,Type[] gen)
        { return args==null?t.GetMethod(n,F):t.GetMethod(n,F,null,args,null); }
    }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile
    { public ConfigEntry<T> Bind<T>(string section,string key,T value,string doc) { ConfigEntry<T> e=new ConfigEntry<T>();e.Value=value;return e; } }
}

// Native types match the fields/methods used by the game adapter.
public struct ObscuredInt
{ int v;public static implicit operator int(ObscuredInt n){return n.v;}public static implicit operator ObscuredInt(int n){ObscuredInt a=new ObscuredInt();a.v=n;return a;} }
public struct ObscuredFloat
{ float v;public static implicit operator float(ObscuredFloat n){return n.v;}public static implicit operator ObscuredFloat(float n){ObscuredFloat a=new ObscuredFloat();a.v=n;return a;} }
public sealed class ContainerData
{
    public ObscuredInt[] ItemID=new ObscuredInt[42],ItemBullets=new ObscuredInt[42],ClipItemID=new ObscuredInt[42];
    public ObscuredFloat[] ItemCondition=new ObscuredFloat[42],ItemWater=new ObscuredFloat[42],ItemEnergy=new ObscuredFloat[42];
}
public sealed class ItemsContainer : UnityEngine.Component
{
    public ContainerData _containerData=new ContainerData(); public bool Busy; public int Clears;
    public bool IsInteracting { get { return Busy; } }
    public void Start() {} public void OnDestroy() {} public void NetworkInteractingContainerRequest(bool open) {}
    public void NeedClearContainerSlot(int slot) { _containerData.ItemID[slot]=0;Clears++; }
}
public sealed class AirDropObject : UnityEngine.Component { public enum AnimationState { Free=0, Parachute=2, Landing=3, Landed=4 } public AnimationState animationState=AnimationState.Landed;public void Update() {} }
public sealed class NPC_AI2 : UnityEngine.Component { public bool Alive=true;public string Key; }
public sealed class VehicleGameSystem : UnityEngine.Component { public bool Alive=true, Ready=true; }
public sealed class PhotonView : UnityEngine.Component
{
    public static readonly Dictionary<int,PhotonView> Views=new Dictionary<int,PhotonView>();
    public int Id, ownerId;public int Owner {get{return ownerId;}set{ownerId=value;}} public object[] Spawn;
    public object[] instantiationData { get { return Spawn; } }
    public static PhotonView Find(int id) { PhotonView p;Views.TryGetValue(id,out p);return p; }
}
namespace NextDayRevival
{
    using UnityEngine;
    internal static class Loc { internal static string T(string ru,string en) { return en; } }
    internal sealed class Log { internal int Warnings;internal void LogWarning(string s){Warnings++;}internal void LogInfo(string s){} }
    internal static class RevivalPlugin
    {
        internal static readonly Log L=new Log();
        internal static Type TypeByName(string s) { return typeof(RevivalPlugin).Assembly.GetType(s); }
    }
    internal static class MapScene { internal static string Current="test";internal static bool Owns(string s){return Current==s;} }
    internal static class TowerSupport { internal static int WorldGeneration; }
    internal static class MercAA { internal static int Master=1;internal static int MasterActor(){return Master;} }
    internal static class FrameProf { public const int S_MercFetchGate=198;internal static void S(int n){}internal static void E(int n){} }
    internal static class Crocodile
    {
        internal static int Actor=1;internal static int LocalActor(){return Actor;}internal static bool IsMaster(){return Actor==MercAA.Master;}
        internal static readonly Dictionary<int,GameObject> Players=new Dictionary<int,GameObject>();
        internal static GameObject PlayerByActor(int id){GameObject p;Players.TryGetValue(id,out p);return p;}
        internal static int ViewId(GameObject go){PhotonView p=go.GetComponent<PhotonView>();return p==null?0:p.Id;}
    }
    internal static class Crew { internal static string GroundKey(Component c){NPC_AI2 ai=c as NPC_AI2;return ai==null?null:ai.Key;} }
    internal static class NpcWar
    { internal static bool MercAlive(Component c){return c is NPC_AI2&&((NPC_AI2)c).Alive;}internal static bool PatrolVehicleAlive(Component c){return c is VehicleGameSystem&&((VehicleGameSystem)c).Alive;} }
    internal static class Mortar { internal static class FactionShield { internal static int FactionOf(GameObject p){return p==null?-1:1;} } }
    internal sealed class Hold { internal bool ByPlayers=true;internal int Holder=1; }
    internal static class AirfieldHold { internal static Hold State=new Hold(); }
    internal sealed class MercOrder { internal int Mode; }
    internal sealed class MercUnit { internal NPC_AI2 Ai;internal bool Deserting;internal MercOrder Order; }
    internal static partial class Mercs
    {
        internal const string KeyPrefix="merc/";
        internal sealed class Record { internal MercUnit Unit;internal MercOrder Order;internal bool Dead,Deserted,Selected; }
        internal static readonly List<Record> Roster=new List<Record>();internal static Vector3 OwnerPosition;
        static void Give(List<Record> rs,MercOrder o) { for(int i=0;i<rs.Count;i++){rs[i].Order=o;rs[i].Unit.Order=o;} }
    }
    internal static class MercUi { internal static string Last;internal static void OrderReply(string s,bool warn){Last=s;} }
    internal sealed class MercCarrier
    { internal const int Ground=0;internal int Kind,View;internal bool Air;internal float Radius=8f;internal GameObject Go;internal Transform Root;internal Component Vgs; }
    internal static partial class MercRide
    {
        internal static readonly Dictionary<int,MercCarrier> Cars=new Dictionary<int,MercCarrier>();
        internal static int Packets;static int Code(){return 199;}
        static MercCarrier CarrierByView(int kind,int view){MercCarrier c;Cars.TryGetValue(view,out c);return c;}
        internal static void SendAAPacket(float[] f){Packets++;}
        public static void OnPhotonEvent(byte code,object content,int sender){}
    }
    internal static class Patrol
    { internal static void MercRoad(Vector3 from,Vector3 to,List<Vector3> path){path.Clear();path.Add(from);path.Add((from+to)*0.5f);path.Add(to);} }
    internal sealed class MercDriveRun
    {
        internal readonly List<Vector3> Path=new List<Vector3>();internal int Phase=1,Next=2,Reason;
        internal float WaitUntil,Deadline;
        internal void Return(float now){Phase=3;Next=Path.Count-2;Deadline=now+1200;}
        internal void Step(){}
    }
    internal static class MercDrive
    {
        static MercDriveRun _run;static MercCarrier _car;static Mercs.Record _driver;
        static List<Mercs.Record> _riders;internal static MercCarrier Candidate;
        internal static bool EscortActive;
        internal static int EscortFailure;
        internal static bool StartEscort(List<Mercs.Record> selected,Vector3 point){EscortActive=true;return true;}
        internal static void ReturnEscort(){EscortActive=false;}
        internal static void CancelEscort(){EscortActive=false;}
        internal static bool EscortMayRestore(Mercs.Record r){return false;}
        internal static bool Active { get { return _run!=null&&_run.Phase<4; } }
        internal static bool Start(List<Mercs.Record> selection,Vector3 target)
        {
            if(Candidate==null||!((VehicleGameSystem)Candidate.Vgs).Ready){MercUi.Last="no ready vehicle";return false;}
            _car=Candidate;_driver=selection[0];_riders=selection;
            _run=new MercDriveRun();Patrol.MercRoad(_car.Root.position,target,_run.Path);
            for(int i=0;i<selection.Count;i++){selection[i].Order=new MercOrder();selection[i].Order.Mode=8;selection[i].Unit.Order=selection[i].Order;}
            return true;
        }
        static void Finish()
        { for(int i=0;i<_riders.Count;i++)if(_riders[i].Order.Mode==8){_riders[i].Order=new MercOrder();_riders[i].Order.Mode=2;_riders[i].Unit.Order=_riders[i].Order;}_car=null;_driver=null; }
    }
    internal static class MercResupply { internal static int Restocks; internal static void DepotRestocked(){Restocks++;} }
    internal struct DepotGood { internal int Id,Bullets,Clip;internal float Condition,Water,Energy; }
    internal sealed class DepotStore
    {
        internal bool Known=true;internal float Hp=1f;internal int Count,Actor=-1;internal DepotGood[] Goods=new DepotGood[256];
        internal bool Add(DepotGood g){if(Count==256)return false;Goods[Count++]=g;return true;}
    }
    internal static class AmmoDepot
    { internal static DepotStore Store=new DepotStore();internal static Vector3 At=new Vector3(10,80,10);internal static int Broadcasts,Asks;static void Broadcast(){Broadcasts++;}
      static void Send(int kind,int ticket,int actor,int request,int truck,int gun,int limit){if(kind==28){Asks++;Store.Known=true;}} }

    internal sealed class FetchFixture : IMercFetchWorld
    {
        internal int Reason,Goods=38,Stock,Releases,Carries,ChangedCount;internal bool Near=true;
        public int Validate(MercFetchJob j,bool loading){return Reason;}
        public void Carry(MercFetchJob j){Carries++;}
        public void Release(MercFetchJob j){Releases++;}
        public bool AtDepot(MercFetchJob j){return Near;}
        public int UnloadOne(MercFetchJob j){if(Goods==0)return 0;if(Stock>=256)return -MercFetchJob.Full;Goods--;Stock++;return 1;}
        public void Changed(MercFetchJob j){ChangedCount++;}
    }

    internal static class FetchHarness
    {
        static int assertions,failures,viewSeq=100;
        static void Ok(bool yes,string name){assertions++;if(!yes){failures++;Console.WriteLine("FAIL "+name);}}
        static PhotonView View(GameObject go,int owner)
        { PhotonView v=go.Add<PhotonView>();v.Id=++viewSeq;v.Owner=owner;PhotonView.Views[v.Id]=v;return v; }
        static MercCarrier Car(int actor,Vector3 pos)
        { GameObject go=new GameObject();go.transform.position=pos;go.Add<Rigidbody>();VehicleGameSystem vg=go.Add<VehicleGameSystem>();PhotonView pv=View(go,actor);
          MercCarrier c=new MercCarrier();c.Go=go;c.Root=go.transform;c.Vgs=vg;c.View=pv.Id;MercRide.Cars[c.View]=c;return c; }
        static Mercs.Record Merc(int actor,Vector3 pos)
        { GameObject go=new GameObject();go.transform.position=pos;NPC_AI2 ai=go.Add<NPC_AI2>();ai.Key=Mercs.KeyPrefix+actor+"/unit";View(go,actor);
          Mercs.Record r=new Mercs.Record();r.Selected=true;r.Order=new MercOrder();r.Order.Mode=6;r.Unit=new MercUnit();r.Unit.Ai=ai;r.Unit.Order=r.Order;return r; }
        static MercFetchNative.Crate Crate(Vector3 pos,int goods)
        { GameObject go=new GameObject();go.transform.position=pos;go.Add<Rigidbody>();go.Add<AirDropObject>();ItemsContainer c=go.Add<ItemsContainer>();go.Add<Collider>();PhotonView v=View(go,1);
          v.Spawn=new object[]{"ndr-airdrop-m4",15,1};for(int i=0;i<goods;i++){c._containerData.ItemID[i]=i<24?2076:i<30?2077:i<36?7013:10005;c._containerData.ItemBullets[i]=i<24?1:200;
          c._containerData.ClipItemID[i]=2030;c._containerData.ItemCondition[i]=77f;c._containerData.ItemWater[i]=3f;c._containerData.ItemEnergy[i]=4f;}
          MercFetchNative.StartPostfix(c);return MercFetchNative.ByView(v.Id); }
        static int Goods(MercFetchNative.Crate c)
        { int n=0;foreach(ObscuredInt id in ((ItemsContainer)c.Container)._containerData.ItemID)if((int)id!=0)n++;return n; }
        static void Scene()
        { GameObject floor=new GameObject();Collider col=floor.Add<Collider>();Physics.Scene=new RaycastHit[]{new RaycastHit{collider=col,normal=Vector3.up,point=new Vector3(100,80,100),distance=3}}; }
        static void Move(MercCarrier car,Mercs.Record driver,Vector3 at)
        { car.Root.position=at;driver.Unit.Ai.transform.position=at; }
        static void Frame(float now)
        { Time.time=now;if(MercFetchBridge.Run!=null)MercFetch.DriveStepPostfix(MercFetchBridge.Run);MercFetch.Tick(); }
        static MercFetchJob RuntimeJob(int actor)
        { FieldInfo f=typeof(MercFetch).GetField("Host",BindingFlags.Static|BindingFlags.NonPublic);return ((MercFetchHost)f.GetValue(null)).Find(actor); }

        static void CoreCases()
        {
            FetchFixture w=new FetchFixture();MercFetchHost h=new MercFetchHost(w);
            Ok(h.Begin(1,1,10,20,30,0)==0,"begin");MercFetchJob j=h.Find(1);
            Ok(h.Begin(1,1,10,20,30,2)==0&&j.Ready==4,"retry keeps loading deadline");
            Ok(h.Begin(2,1,10,21,31,0)==MercFetchJob.Taken,"crate exclusive");
            Ok(h.Begin(2,1,11,20,31,0)==MercFetchJob.Taken,"vehicle exclusive");
            h.Step(3.9f);Ok(j.Phase==1&&w.Goods==38,"short load before movement");h.Step(4);Ok(j.Phase==2&&w.Carries==1&&w.Goods==38,"whole real crate, no clones");
            w.Near=false;Ok(h.Unload(1,1,10,20,30,5)==MercFetchJob.DepotLost,"remote depot rejected");w.Near=true;
            Ok(h.Unload(2,1,10,20,30,5)!=0,"other actor cannot unload");
            Ok(h.Unload(1,1,10,20,30,5)==0,"unload");h.Unload(1,1,10,20,30,6);Ok(j.Ready==9,"unload retry does not reset animation");
            for(int i=0;i<40;i++){float now=9+i*0.25f;h.Touch(1,1,10,20,30,now);h.Step(now);Ok(w.Stock+w.Goods==38,"conservation per removal");}
            Ok(j.Phase==4&&j.Moved==38&&w.Stock==38&&w.Releases==1,"complete trip");
            Ok(h.Begin(1,1,10,20,30,20)==MercFetchJob.Expired,"closed replay refused");
            for(int reason=1;reason<=11;reason++)
            { w=new FetchFixture();h=new MercFetchHost(w);h.Begin(1,1,10,20,30,0);w.Reason=reason;h.Step(1);Ok(h.Find(1).Phase==5&&w.Goods==38&&w.Releases==1,"failure keeps real inventory "+reason); }
            w=new FetchFixture();h=new MercFetchHost(w);h.Begin(1,1,10,20,30,0);h.Step(12);Ok(h.Find(1).Reason==MercFetchJob.Expired,"owner timeout");
            w=new FetchFixture();h=new MercFetchHost(w);h.Begin(1,1,10,20,30,0);h.Step(4);h.NewMaster();Ok(w.Releases==1&&w.Goods==38,"handoff releases source");
            w=new FetchFixture();w.Stock=255;h=new MercFetchHost(w);h.Begin(1,1,10,20,30,0);h.Step(4);h.Unload(1,1,10,20,30,5);h.Step(9);h.Step(9.25f);
            Ok(w.Stock==256&&w.Goods==37&&h.Find(1).Reason==MercFetchJob.Full,"partial unload preserves rest");
            w=new FetchFixture();h=new MercFetchHost(w);for(int i=1;i<=8;i++)Ok(h.Begin(i,1,10+i,20+i,30+i,0)==0,"bounded slot");
            Ok(h.Begin(9,1,99,100,101,0)==MercFetchJob.Busy,"overflow refused");h.Step(4);for(int i=1;i<=8;i++)h.Unload(i,1,10+i,20+i,30+i,5);h.Step(9);Ok(w.Stock==1,"one removal globally per turn");
            float[] packet={10,1,2,3,4,5,0,0};Ok(MercFetchHost.Packet(packet,10,8),"valid packet");
            for(int i=0;i<packet.Length;i++){float old=packet[i];packet[i]=float.NaN;Ok(!MercFetchHost.Packet(packet,10,8),"NaN refused");packet[i]=0.5f;Ok(!MercFetchHost.Packet(packet,10,8),"fraction refused");packet[i]=old;}
            // Warm retained allocations and collection count; pure arithmetic fixture.
            w=new FetchFixture();h=new MercFetchHost(w);h.Begin(1,1,10,20,30,0);h.Step(4);
            for(int i=0;i<1000;i++){h.Touch(1,1,10,20,30,5);h.Step(5);}
            Stopwatch sw=new Stopwatch();sw.Start();sw.Stop();double warm=sw.Elapsed.TotalMilliseconds;sw.Reset();
            GC.Collect();long mem=GC.GetTotalMemory(true);int gen=GC.CollectionCount(0);
            sw.Start();for(int i=0;i<200000;i++){h.Touch(1,1,10,20,30,5);h.Step(5);}sw.Stop();
            long delta=GC.GetTotalMemory(false)-mem;int gc=GC.CollectionCount(0)-gen;
            Console.WriteLine("BENCH host active: "+(sw.Elapsed.TotalMilliseconds/200000).ToString("0.000000")+" ms, retained="+delta+" B, Gen0="+gc);
            Ok(delta<256&&gc==0,"warm core no managed churn");
        }

        static void RuntimeCases()
        {
            MercFetch.Start();Ok(!MercFetchBridge.Available&&MercUi.Last.Contains("unavailable"),"fallback checkout fails with a reason");
            HarmonyLib.Harmony hm=new HarmonyLib.Harmony();MercFetch.Install(hm);
            Ok(MercFetchBridge.Available&&MercFetchNative.Available&&hm.Patches==6,"actual emitted bindings and hooks");
            AmmoDepot.Store.Known=false;MercFetchBridge.Snapshot();Ok(AmmoDepot.Store.Known&&AmmoDepot.Asks==1,"late join requests existing depot authority snapshot");
            GameObject player=new GameObject();Crocodile.Players[1]=player;Crocodile.Players[2]=new GameObject();
            Scene();Vector3 crateAt=new Vector3(100,80,100);MercCarrier car=Car(1,new Vector3(20,80,20));MercDrive.Candidate=car;
            Mercs.OwnerPosition=car.Root.position;Mercs.Record merc=Merc(1,car.Root.position);Mercs.Roster.Add(merc);
            MercFetchNative.Crate c=Crate(crateAt,38);Ok(c!=null,"native paid crate discovered");
            Vector3 ground;Ok(MercFetchNative.Target(c,out ground)&&Physics.Layers==-1,"all layers landing support");
            Vector3 park;Ok(MercFetchNative.Parking(crateAt,car.Root.position,out park)&&(park-crateAt).sqrMagnitude>14*14,"park outside physical source footprint");
            string[] props={"slab","fence","sandbag"};foreach(string prop in props){GameObject obj=new GameObject();Physics.Blockers=new Collider[]{obj.Add<Collider>()};Ok(!MercFetchNative.Parking(crateAt,car.Root.position,out park),"all-collider parking veto "+prop);}Physics.Blockers=new Collider[0];
            RaycastHit[] saved=Physics.Scene;Physics.Scene=new RaycastHit[32];Ok(!MercFetchNative.Target(c,out ground),"saturated physics refuses");Physics.Scene=saved;
            c.Body.velocity=new Vector3(0,-2,0);Ok(!MercFetchNative.Target(c,out ground),"descending source refused");c.Body.velocity=Vector3.zero;
            merc.Selected=false;MercFetch.Start();Ok(!MercFetchBridge.Active&&MercUi.Last.Contains("Select at least"),"no silent all-squad fallback");merc.Selected=true;
            ((VehicleGameSystem)car.Vgs).Ready=false;MercFetch.Start();Ok(!MercFetchBridge.Active,"no ready vehicle preserves orders");((VehicleGameSystem)car.Vgs).Ready=true;
            MercFetch.Start();Ok(MercFetchBridge.Active&&merc.Order.Mode==8,"start drives selected squad");
            Move(car,merc,crateAt);object run=MercFetchBridge.Run;SetPhase(run,2);Frame(1);MercFetchJob j=RuntimeJob(1);
            Ok(j!=null&&j.Phase==1&&c.Locked,"authoritative load claims real source");Ok(!MercFetchNative.InteractionPrefix(c.Container,true)&&MercFetchNative.InteractionPrefix(c.Container,false),"loot lock permits close");
            Frame(3);Ok(Goods(c)==38&&AmmoDepot.Store.Count==0,"animation keeps goods in source");
            Frame(5);Ok(j.Phase==2&&MercFetchBridge.Phase(run)==3&&c.Root.parent==car.Root,"loaded source and depot return");
            BenchRuntime(j);
            List<Vector3> path=((MercDriveRun)run).Path;Ok((path[0]-AmmoDepot.At).sqrMagnitude<=50.4f*50.4f&&(path[0]-car.Root.position).sqrMagnitude>1,"home is a clear depot lane");
            Move(car,merc,AmmoDepot.At);SetPhase(run,4);Frame(6);
            if(j.Phase!=3)Console.WriteLine("TRACE home: job="+j.Phase+" reason="+j.Reason+" run="+MercFetchBridge.Phase(run)+" reply="+MercUi.Last);
            Ok(j.Phase==3&&MercFetchBridge.Phase(run)==2,"stop and wait for unload");
            for(int i=0;i<44;i++)Frame(10+i*0.25f);
            Ok(j.Phase==4&&Goods(c)==0&&AmmoDepot.Store.Count==38,"production runtime round trip conserves all goods");
            Ok(((ItemsContainer)c.Container).Clears==38&&AmmoDepot.Broadcasts==38,"one native clear and depot sync per item");
            Ok(AmmoDepot.Store.Goods[0].Id==2076&&AmmoDepot.Store.Goods[0].Bullets==1&&AmmoDepot.Store.Goods[0].Clip==2030
                &&AmmoDepot.Store.Goods[0].Condition==77&&AmmoDepot.Store.Goods[0].Water==3&&AmmoDepot.Store.Goods[0].Energy==4,"actual native metadata preserved");
            Ok(!MercFetchBridge.Active&&!c.Locked&&merc.Order.Mode==6,"successful original post restored");
            // Attack/replacement cancels promptly and retains both order and cargo.
            Time.time=30;AmmoDepot.Store.Count=0;Move(car,merc,new Vector3(20,80,20));MercFetchNative.Crate c2=Crate(crateAt,2);
            MercFetch.Start();Move(car,merc,crateAt);SetPhase(MercFetchBridge.Run,2);Frame(31);Frame(35);
            MercOrder replacement=new MercOrder();replacement.Mode=9;merc.Order=replacement;merc.Unit.Order=replacement;SetPhase(MercFetchBridge.Run,5);Frame(36);
            Ok(ReferenceEquals(merc.Order,replacement)&&Goods(c2)==2&&!c2.Locked,"cancel retains replacement and real crate");
            // Forged cargo packets cannot claim another owner's driver.
            float[] ask={10,1,77,c2.View,car.View,Crocodile.ViewId(merc.Unit.Ai.gameObject),0,0};MercFetch.OnPacket(ask,2);
            Ok(!c2.Locked&&Goods(c2)==2,"forged driver owner rejected");
            // Remote snapshot authenticity and master handoff release.
            Crocodile.Actor=2;float[] state={11,2,2,100,c2.View,car.View,Crocodile.ViewId(merc.Unit.Ai.gameObject),0,0,3,0};
            MercFetch.OnPacket(state,3);Ok(!c2.Locked,"nonmaster snapshot ignored");MercFetch.OnPacket(state,1);Ok(c2.Locked,"late join authoritative cargo pose");
            MercFetchNative.Release(RuntimeJob(2));MercFetch.OnPacket(state,1);Ok(c2.Locked,"same revision retries delayed native spawn binding");
            MercAA.Master=3;Frame(40);Ok(!c2.Locked&&Goods(c2)==2,"handoff unlocks genuine remaining source");MercAA.Master=1;Crocodile.Actor=1;
            TowerSupport.WorldGeneration++;Frame(41);Ok(RuntimeJob(1)==null&&RuntimeJob(2)==null,"new room clears stale actors/replay state");
            // Array delegates also reject incompatible contents without inventing stock.
            MercFetchGood invalid=new MercFetchGood();invalid.Id=9999;Ok(!invalid.Valid,"unknown item not gifted");invalid.Id=2076;invalid.Condition=float.NaN;Ok(!invalid.Valid,"bad native metadata refused");
        }
        static void BenchRuntime(MercFetchJob j)
        {
            for(int i=0;i<1000;i++)MercFetchNative.Validate(j,false);
            Stopwatch sw=new Stopwatch();sw.Start();sw.Stop();double warm=sw.Elapsed.TotalMilliseconds;sw.Reset();
            GC.Collect();long mem=GC.GetTotalMemory(true);int gen=GC.CollectionCount(0),bad=0;
            sw.Start();for(int i=0;i<200000;i++)if(MercFetchNative.Validate(j,false)!=0)bad++;sw.Stop();
            long delta=GC.GetTotalMemory(false)-mem;int gc=GC.CollectionCount(0)-gen;
            Console.WriteLine("BENCH native authority check: "+(sw.Elapsed.TotalMilliseconds/200000).ToString("0.000000")+" ms, retained="+delta+" B, Gen0="+gc);
            Ok(bad==0&&delta<256&&gc==0,"warm production native checks no managed churn");
        }
        static void SetPhase(object run,int n){typeof(MercDriveRun).GetField("Phase",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(run,n);}
        public static int Main()
        { try{CoreCases();RuntimeCases();}catch(Exception ex){failures++;Console.WriteLine(ex);}
          Console.WriteLine("Z M5b production simulation: "+assertions+" assertions, "+failures+" failures");return failures==0?0:1; }
    }
}

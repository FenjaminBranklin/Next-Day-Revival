// Small engine stubs; ENTRY markers receive unchanged production methods.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityEngine.Rendering { public enum ShadowCastingMode { Off, On } }
namespace UnityEngine
{
    public class Object
    {
        public static Object Instantiate(Object o) { return ((GameObject)o).Clone(); }
        public static void Destroy(Object o) { }
        public static void Destroy(Object o, float delay) { }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public string name { get { return gameObject.name; } }
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : class { return gameObject.GetComponent<T>(); }
        public T[] GetComponentsInChildren<T>(bool inactive) where T : class
        { return gameObject.GetComponentsInChildren<T>(inactive); }
    }
    public class MonoBehaviour : Component { public bool enabled = true; }
    public sealed class GameObject : Object
    {
        static int ids;
        readonly int id = ++ids;
        readonly List<Component> components = new List<Component>();
        public string name;
        public Transform transform;
        public bool activeSelf = true;
        public int layer;
        public bool activeInHierarchy { get { return activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy); } }
        public GameObject(string value) { name = value; transform = new Transform(this); }
        public int GetInstanceID() { return id; }
        public void SetActive(bool value)
        {
            activeSelf = value;
            if (activeInHierarchy) foreach (Component c in components)
            {
                FakeContainer hold = c as FakeContainer;
                if (hold != null && !hold.AwakeRan) hold.Awake();
            }
        }
        public Component AddComponent(Type t)
        {
            Component c = (Component)Activator.CreateInstance(t);
            c.gameObject = this; components.Add(c);
            if (t == typeof(ParticleSystem)) AddComponent(typeof(ParticleSystemRenderer));
            FakeContainer hold = c as FakeContainer;
            if (hold != null && activeInHierarchy) hold.Awake();
            return c;
        }
        public T AddComponent<T>() where T : Component, new() { return (T)AddComponent(typeof(T)); }
        public Component GetComponent(Type type)
        { foreach (Component c in components) if (type.IsInstanceOfType(c)) return c; return null; }
        public T GetComponent<T>() where T : class { return GetComponent(typeof(T)) as T; }
        public T[] GetComponentsInChildren<T>(bool inactive) where T : class
        {
            List<T> all = new List<T>();
            foreach (Component c in components) if (c is T) all.Add(c as T);
            foreach (Transform child in transform.Children)
                if (inactive || child.gameObject.activeInHierarchy) all.AddRange(child.gameObject.GetComponentsInChildren<T>(inactive));
            return all.ToArray();
        }
        public GameObject Clone()
        {
            GameObject copy = new GameObject(name);
            if (GetComponent<ParticleSystem>() != null) copy.AddComponent<ParticleSystem>();
            foreach (Transform child in transform.Children) child.gameObject.Clone().transform.SetParent(copy.transform, false);
            return copy;
        }
    }
    public sealed class Transform
    {
        public readonly GameObject gameObject;
        public readonly List<Transform> Children = new List<Transform>();
        public Transform parent;
        public Vector3 localPosition, localScale = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;
        public string name { get { return gameObject.name; } }
        public Transform(GameObject go) { gameObject = go; }
        public Vector3 position { get { return parent == null ? localPosition : parent.TransformPoint(localPosition); }
            set { localPosition = parent == null ? value : parent.InverseTransformPoint(value); } }
        public Quaternion rotation { get { return parent == null ? localRotation : parent.rotation * localRotation; }
            set { localRotation = parent == null ? value : Quaternion.Inverse(parent.rotation) * value; } }
        public Vector3 lossyScale { get { return parent == null ? localScale : Vector3.Scale(parent.lossyScale, localScale); } }
        public Vector3 forward { get { return rotation * Vector3.forward; } }
        public void SetParent(Transform target, bool stay)
        {
            Vector3 old = position;
            if (parent != null) parent.Children.Remove(this);
            parent = target;
            if (target != null) target.Children.Add(this);
            if (stay) position = old;
        }
        public Transform Find(string path)
        {
            string[] bits = path.Split('/'); Transform t = this;
            foreach (string bit in bits) { Transform found = null; foreach (Transform child in t.Children) if (child.name == bit) found = child; if (found == null) return null; t = found; }
            return t;
        }
        public Vector3 TransformPoint(Vector3 p) { return position + rotation * Vector3.Scale(p, lossyScale); }
        public Vector3 InverseTransformPoint(Vector3 p) { Vector3 d = Quaternion.Inverse(rotation) * (p - position); Vector3 s = lossyScale; return new Vector3(d.x / s.x, d.y / s.y, d.z / s.z); }
        public T GetComponent<T>() where T : class { return gameObject.GetComponent<T>(); }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 one { get { return new Vector3(1, 1, 1); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 forward { get { return new Vector3(0, 0, 1); } }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator *(Vector3 a, float s) { return new Vector3(a.x*s,a.y*s,a.z*s); }
        public static Vector3 operator /(Vector3 a, float s) { return a*(1/s); }
        public static Vector3 Scale(Vector3 a, Vector3 b) { return new Vector3(a.x*b.x,a.y*b.y,a.z*b.z); }
        public string ToString(string format) { return x.ToString(format)+","+y.ToString(format)+","+z.ToString(format); }
    }
    public struct Quaternion
    {
        public float x,y,z,w;
        public Vector3 eulerAngles { get { return new Vector3((float)(Math.Asin(Math.Max(-1,Math.Min(1,2*(w*x-y*z))))*180/Math.PI),(float)(Math.Atan2(2*(w*y+x*z),1-2*(x*x+y*y))*180/Math.PI),(float)(Math.Atan2(2*(w*z+x*y),1-2*(x*x+z*z))*180/Math.PI)); } }
        public static Quaternion identity { get { return new Quaternion { w=1 }; } }
        public static Quaternion Euler(float x,float y,float z)
        {
            double rx=x*Math.PI/360,ry=y*Math.PI/360,rz=z*Math.PI/360;
            Quaternion a=new Quaternion { x=(float)Math.Sin(rx),w=(float)Math.Cos(rx) };
            Quaternion b=new Quaternion { y=(float)Math.Sin(ry),w=(float)Math.Cos(ry) };
            Quaternion c=new Quaternion { z=(float)Math.Sin(rz),w=(float)Math.Cos(rz) };
            return b*a*c;
        }
        public static Quaternion Inverse(Quaternion q) { float n=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w; return new Quaternion { x=-q.x/n,y=-q.y/n,z=-q.z/n,w=q.w/n }; }
        public static Quaternion operator *(Quaternion a, Quaternion b) { return new Quaternion { x=a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,y=a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,z=a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w,w=a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z }; }
        public static Vector3 operator *(Quaternion q, Vector3 p)
        {
            Quaternion point=new Quaternion { x=p.x,y=p.y,z=p.z };
            Quaternion result=q*point*Inverse(q);
            return new Vector3(result.x,result.y,result.z);
        }
        public static Quaternion Slerp(Quaternion a,Quaternion b,float f) { return b; }
    }
    public struct Bounds { public Vector3 center,size; public Vector3 ClosestPoint(Vector3 p) { return p; } }
    public class Mesh { public Bounds bounds; }
    public class Material { }
    public class Renderer : Component { public bool enabled=true; public Material[] sharedMaterials=new Material[0]; public Rendering.ShadowCastingMode shadowCastingMode; }
    public class MeshRenderer : Renderer { }
    public class MeshFilter : Component { public Mesh sharedMesh = new Mesh(); }
    public class ParticleSystemRenderer : Renderer { public ParticleSystemRenderMode renderMode; public float minParticleSize; }
    public struct Color { public Color(float r,float g,float b,float a) { } public static Color white { get { return new Color(); } } }
    public struct Color32 { public static implicit operator Color32(Color c) { return new Color32(); } }
    public enum ParticleSystemRenderMode { Billboard }
    public enum ParticleSystemStopBehavior { StopEmitting, StopEmittingAndClear }
    public enum ParticleSystemSimulationSpace { World, Local }
    public enum ParticleSystemScalingMode { Shape, Hierarchy }
    public class ParticleSystem : Component
    {
        public class MainModule { public bool loop,playOnAwake; public float startLifetime=1,startLifetimeMultiplier=1,startSpeed,startSize=1,simulationSpeed=1; public Color startColor; public int maxParticles=256; public ParticleSystemSimulationSpace simulationSpace; public ParticleSystemScalingMode scalingMode; }
        public class EmissionModule { public bool enabled; public float rateOverTime,rateOverDistance; public void SetBursts(Burst[] b) { } }
        public struct Burst { }
        public struct Particle { public Vector3 position,velocity; public float startSize,remainingLifetime; public Color32 startColor; public float GetCurrentSize(ParticleSystem p) { return startSize; } public Color32 GetCurrentColor(ParticleSystem p) { return startColor; } }
        public readonly MainModule main=new MainModule();
        public readonly EmissionModule emission=new EmissionModule();
        readonly List<Particle> particles=new List<Particle>();
        float fraction;
        public int Simulations,Plays;
        public void Stop(bool child,ParticleSystemStopBehavior behavior) { if(behavior==ParticleSystemStopBehavior.StopEmittingAndClear) particles.Clear(); }
        public void Play(bool child) { Plays++; }
        public void Pause(bool child) { }
        public void Simulate(float dt,bool child,bool restart,bool fixedStep)
        {
            Simulations++;
            for(int i=particles.Count-1;i>=0;i--) { Particle p=particles[i]; p.remainingLifetime-=dt; if(p.remainingLifetime<=0) particles.RemoveAt(i); else particles[i]=p; }
            if(!emission.enabled) return;
            fraction+=dt*emission.rateOverTime;
            while(fraction>=1 && particles.Count<main.maxParticles) { fraction--; particles.Add(new Particle { position=transform.position,startSize=main.startSize,remainingLifetime=main.startLifetime }); }
        }
        public int GetParticles(Particle[] buffer) { int n=Math.Min(buffer.Length,particles.Count); for(int i=0;i<n;i++) buffer[i]=particles[i]; return n; }
        public void SetParticles(Particle[] buffer,int count) { particles.Clear(); for(int i=0;i<count;i++) particles.Add(buffer[i]); }
    }
    public class Camera : Component
    {
        public bool orthographic; public int cullingMask=1; public float farClipPlane=1000;
        public static event Action<Camera> onPreCull;
        public static void Render(Camera c) { if(onPreCull!=null) onPreCull(c); }
    }
    public class Terrain : Component { public static Terrain[] activeTerrains=new Terrain[0]; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics { public static bool Raycast(Vector3 p,Vector3 d,float reach,int mask,QueryTriggerInteraction trigger) { return false; } }
    public static class Resources
    {
        public static Object Load(string path,Type type)
        {
            if(path.Contains("VehicleSmoke")) { GameObject go=new GameObject("native smoke"); go.AddComponent<ParticleSystem>(); return go; }
            GameObject root=new GameObject("camp"); Transform tr=root.transform;
            foreach(string part in new string[]{"FireEmitObjects","CampfireElements","FireComplex_old","Flames"}) { GameObject child=new GameObject(part); child.transform.SetParent(tr,false); tr=child.transform; }
            tr.gameObject.AddComponent<ParticleSystem>(); return root;
        }
    }
    public static class Time { public static float time,unscaledTime,deltaTime=1f/60; }
    public static class Mathf
    {
        public static float Abs(float f) { return Math.Abs(f); }
        public static float Min(float a,float b) { return Math.Min(a,b); }
        public static int Min(int a,int b) { return Math.Min(a,b); }
        public static float Max(float a,float b) { return Math.Max(a,b); }
        public static int Max(int a,int b) { return Math.Max(a,b); }
        public static float Clamp(float f,float a,float b) { return Max(a,Min(b,f)); }
        public static float Clamp01(float f) { return Clamp(f,0,1); }
        public static int RoundToInt(float f) { return (int)Math.Round(f); }
        public static float DeltaAngle(float a,float b) { return b-a; }
    }
}

public sealed class FakeData { public int MaxSlots; public float MaxWeight; public string Name; public int[] Slots; }
public sealed class FakeContainer : Component
{
    public FakeData _containerData;
    public bool IsSpawnedData,AwakeRan;
    public static int Exceptions;
    public void Awake() { AwakeRan=true; if(_containerData==null) { Exceptions++; throw new NullReferenceException("SetContainerData"); } SetContainerDataArraysLenght(_containerData.MaxSlots); }
    public void SetContainerDataArraysLenght(int n) { _containerData.Slots=new int[n]; }
}

namespace NextDayRevival
{
    internal sealed class Config<T> { internal T Value; internal Config(T v) { Value=v; } }
    internal static class Loc { internal static string T(string ru,string en) { return en; } }
    internal sealed class Logger { internal readonly List<string> Lines=new List<string>(); public void LogInfo(string s) { Lines.Add(s); } public void LogWarning(string s) { Lines.Add(s); } public void LogError(object s) { Lines.Add(s.ToString()); } }
    internal static class RevivalPlugin { internal static Logger L=new Logger(); }
    internal static class FrameProf { public const int S_An2Glide_Update=1,S_AirKillsT=2; public static void S(int n) { } public static void E(int n) { } }
    internal static class ParaPose { public static double Clock() { return Time.time; } }
    internal static class CameraOwner { internal static Camera Main; public static Camera MainCamera() { return Main; } }
    internal static class Fx { internal static float Factor=1; }
    internal static class AircraftAudio { internal static void StopEngines(GameObject go) { } }
    internal static class RevivalTroopInsertion { internal static bool Master=true; internal static bool MasterClient() { return Master; } }
    internal static class RetakeRaids { internal const string NamePrefix="raid"; }
    internal static class AirEvents { internal const string BomberTag="tu95:",TransportTag="an2t:"; internal static void StopCarpet(int view) { } }
    internal static class Tu95Model { internal static List<KeyValuePair<string,Vector3>> Props=new List<KeyValuePair<string,Vector3>>(); internal static List<Bounds> Boxes=new List<Bounds>(); }
    internal static class Flyover { internal static void Tick() { } }
    internal static class AARaidBalanceCore { internal static float GunDamage(float f,int t) { return f; } }
    internal static class Gepard { internal static Config<int> CfgHeliHits=new Config<int>(10); }
    internal static class ShortRangeCore { internal static float AddHit(float a,int n) { return a+1f/n; } }
    internal static class GepardNet { internal static void SendHeliKill(int n,Vector3 p) { } }
    internal static class GepardGun { internal sealed class Contact { internal GameObject Go; internal GepardAir.Source Src; } }
    internal static class PlayerHeli
    {
        internal const float K=3.84f;
        internal static bool Contains(GameObject go) { return false; }
        internal static GameObject MissileTarget(int view) { return null; }
        internal static int MissileView(GameObject go) { return 0; }
        internal static void MissileImpact(int view,Vector3 p) { }
    }
    internal static class PlayerAn2
    {
        internal const float K=2.8f;
        internal static Config<bool> CfgCrash=new Config<bool>(true);
        static GameObject _plane;
        static bool _pilot;
        static Vector3 _vel;
        static Quaternion _rot;
        static Dictionary<int,float> _shotDown=new Dictionary<int,float>(),_busyUntil=new Dictionary<int,float>();
        internal static readonly HashSet<GameObject> Burnt=new HashSet<GameObject>();
        internal static bool Burning(GameObject go) { return Burnt.Contains(go); }
        internal static bool Down(GameObject go) { return Burning(go)||Gliding(go); }
        internal static bool Contains(GameObject go) { return NpcAircraft.Is(go); }
        internal static int View(GameObject go) { return go.GetInstanceID(); }
        static int ViewId(GameObject go) { return View(go); }
        internal static bool Airborne(GameObject go) { return go.transform.position.y>1.5f*K; }
        static void Hint(string s,float f) { }
        static void Crash(GameObject go,Vector3 p) { FinishGlide(go,p); }
        static void Interpolator(GameObject go,bool on) { go.GetComponent<FakeInterpolator>().enabled=on; }
        internal static void RemoveNpc(GameObject go) { throw new Exception("removed airborne aircraft"); }
        internal static void GlideFloor(Vector3 p,out float floor,out bool solid) { floor=0; solid=true; }
        internal static void FinishGlide(GameObject go,Vector3 p) { AircraftCrashFx.Impact(go); AircraftCrashFx.Stop(go); Burnt.Add(go); }
        internal static class Net { internal const int Crashed=5; internal static void Send(int id,float[] f,bool reliable) { } }
        /* AN2_ENTRY */
    }
    public sealed class FakeInterpolator : MonoBehaviour { }
    internal sealed class FlightPath
    {
        internal float Speed=70*2.8f,Length=100000,Seconds=1000;
        internal float Project(Vector3 p) { return p.z; }
        internal Vector3 At(float s) { return new Vector3(0,550*2.8f,s); }
        internal Quaternion Attitude(float s) { return Quaternion.identity; }
    }
    internal static class NpcAircraft
    {
        internal sealed class Flight { internal GameObject Go; internal string Label; internal int Toughness=1,Hits; internal DamageLedger Ledger=new DamageLedger(); internal FlightPath Path=new FlightPath(); internal bool Driving; internal float S,Born; internal Action<GameObject> OnEnd; internal Action<GameObject,float> OnTick; }
        static readonly Dictionary<GameObject,Flight> _flights=new Dictionary<GameObject,Flight>();
        static readonly List<GameObject> _drop=new List<GameObject>();
        static readonly List<Flight> _tmp=new List<Flight>();
        static int _errors;
        internal static Flight Add(GameObject go,string label) { Flight f=new Flight { Go=go,Label=label }; _flights[go]=f; return f; }
        internal static Flight Find(GameObject go) { return _flights.ContainsKey(go)?_flights[go]:null; }
        internal static bool Is(GameObject go) { return _flights.ContainsKey(go); }
        internal static bool IsTu95(GameObject go) { Flight f=Find(go); return f!=null&&f.Label.StartsWith("tu95:"); }
        internal static bool Velocity(GameObject go,out Vector3 v) { v=new Vector3(0,0,70*2.8f); return true; }
        internal static void Damage(GameObject go,Vector3 at,bool lethal) { Apply(Find(go),at,lethal?1f:-1f,1); }
        internal static void Damage(GameObject go,Vector3 at,float amount,int actor) { Apply(Find(go),at,amount,actor); }
        internal static void Shot(Flight f,float amount) { Apply(f,f.Go.transform.position,amount,1); }
        /* NPC_ENTRY */
    }
    internal static class AirKills
    {
        internal static int Credit() { return 1; }
        static void Pay(int actor,int what,Vector3 at) { }
        internal static void Damaged(NpcAircraft.Flight f) { }
        /* KILL_ENTRY */
    }
    internal static class GepardAir
    {
        internal sealed class Source { public string Name; public Action<List<GameObject>> List; public Action<GameObject,Vector3> Kill; public Func<GameObject,bool> Down; public int Hits; }
        internal struct Found { internal GameObject Go; internal Source Src; }
        static readonly List<Source> _sources=new List<Source>();
        static readonly Dictionary<int,float> _hits=new Dictionary<int,float>();
        static readonly List<GameObject> _tmp=new List<GameObject>();
        static void Probe() { }
        /* GEPARD_ENTRY */
    }
    internal static class HeliHold
    {
        static bool On=true;
        static Type _tContainer=typeof(FakeContainer),_tData=typeof(FakeData);
        static FieldInfo _fData=_tContainer.GetField("_containerData"),_fSpawned=_tContainer.GetField("IsSpawnedData"),_fMaxSlots=_tData.GetField("MaxSlots"),_fMaxWeight=_tData.GetField("MaxWeight"),_fName=_tData.GetField("Name");
        static MethodInfo _mLength=_tContainer.GetMethod("SetContainerDataArraysLenght");
        static bool Look() { return true; }
        static void Plate(GameObject go) { }
        static int Slots() { return 200; }
        static float Weight() { return 4000; }
        static void Reheard(GameObject go) { }
        /* HOLD_ENTRY */
    }
    public sealed class Tu95Visual : MonoBehaviour
    {
        internal const float ProxyShare=0.45f,ProxyMinU=300,ProxyMaxU=600;
        Transform _fly;
        bool _dead,_proxied,_hidden;
        List<Renderer> _drawn=new List<Renderer>();
        bool Occluded(Vector3 eye,Vector3 d,float dist) { return false; }
        internal void Build(Transform fly) { _fly=fly; }
        internal void Draw(Camera cam) { Place(cam); }
        /* TU95_PLACE */
    }
    internal static class HeliDrop { const float Gravity=(float)AircraftFallCore.Gravity,Terminal=65; /* HELI_DROP */ }
    /* GLIDE */

    internal static class EntryCheck
    {
        static int checks;
        static void Check(bool ok,string text) { checks++; if(!ok) throw new Exception(text); }
        static void Call(object instance,string name) { instance.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(instance,null); }
        static GameObject Aircraft(string label,bool bomber)
        {
            GameObject go=new GameObject(label); go.transform.position=new Vector3(0,550*PlayerAn2.K,0); go.AddComponent<FakeInterpolator>();
            GameObject model=new GameObject(bomber?"NDR_Tu95":"NDR_An2"); model.transform.SetParent(go.transform,false);
            if(bomber) { GameObject fly=new GameObject("NDR_Tu95Fly"); fly.transform.SetParent(model.transform,false); fly.transform.localScale=Vector3.one*PlayerAn2.K; model.AddComponent<Tu95Visual>().Build(fly.transform); }
            else { model.AddComponent<MeshFilter>(); model.AddComponent<MeshRenderer>(); }
            NpcAircraft.Add(go,label); return go;
        }
        static void Fall(GameObject go,Camera cam)
        {
            An2Glide glide=go.GetComponent<An2Glide>();
            Check(glide!=null,"real kill did not attach An2Glide");
            Check(go.GetComponent<AircraftCrashEvent>()!=null,"fall telemetry missing");
            Check(!go.GetComponent<FakeInterpolator>().enabled,"interpolator still writes fall pose");
            float y=go.transform.position.y;
            for(int i=1;i<=120;i++)
            {
                Time.time=Time.unscaledTime=i/60f;
                Call(glide,"Update"); NpcAircraft.Tick(); AircraftCrashFx.Tick(Time.time);
                Call(go.GetComponent<AircraftCrashEvent>(),"Update"); Camera.Render(cam);
            }
            Check(y-go.transform.position.y>30*PlayerAn2.K,"path driver overwrote the falling carrier");
            Check(!NpcAircraft.Find(go).Driving,"NPC flight driver retained ownership");
            GameObject trail=TrailRoot(go);
            ParticleSystem[] ps=trail.GetComponentsInChildren<ParticleSystem>(true);
            Check(ps.Length==4,"native trail/render twins missing");
            Check(ps[0].main.simulationSpace==ParticleSystemSimulationSpace.World,"flame not world space");
            Check(ps[1].main.startLifetime>=20,"short smoke lifetime");
            Check(ps[1].Simulations>=120,"off-screen smoke not simulated");
            Check(ps[1].main.startSize>=35,"combat smoke too small");
            ParticleSystem.Particle[] actual=new ParticleSystem.Particle[256],draw=new ParticleSystem.Particle[256];
            int n=ps[1].GetParticles(actual),m=ps[3].GetParticles(draw);
            Check(n>0&&n==m,"far smoke particles missing");
            Check((draw[0].position-cam.transform.position).magnitude<cam.farClipPlane,"smoke stayed outside far clip");
            Vector3 a=actual[0].position-cam.transform.position,b=draw[0].position-cam.transform.position;
            Check((a/a.magnitude-b/b.magnitude).magnitude<0.001f,"proxy changed smoke sight line");
            Check(actual[0].startSize>draw[0].startSize,"proxy angular scaling missing");
            Check(ps[0].GetParticles(actual)>0&&ps[2].GetParticles(draw)>0,"flame core missing at combat distance");
            Check(RevivalPlugin.L.Lines.Exists(delegate(string line){return line.Contains("after2s")&&line.Contains("lost_m=");}),"2s event diagnostic missing");
            Tu95Visual visual=go.transform.Find("NDR_Tu95")==null?null:go.transform.Find("NDR_Tu95").gameObject.GetComponent<Tu95Visual>();
            if(visual!=null)
            {
                visual.Draw(cam); Transform fly=go.transform.Find("NDR_Tu95/NDR_Tu95Fly");
                Vector3 carrier=go.transform.position-cam.transform.position,render=fly.position-cam.transform.position;
                Check((carrier/carrier.magnitude-render/render.magnitude).magnitude<0.001f,"Tu95 visual followed stale flight pose");
                Check(Math.Abs(fly.rotation.eulerAngles.x-go.transform.rotation.eulerAngles.x)<0.001f,"Tu95 visual lost fall attitude");
            }
            for(int i=121;i<=720&&!PlayerAn2.Burning(go);i++) { Time.time=Time.unscaledTime=i/60f; Call(glide,"Update"); }
            Check(PlayerAn2.Burning(go)&&Time.time<12,"550m descent exceeded 12s");
            Check(RevivalPlugin.L.Lines.Exists(delegate(string line){return line.Contains("AircraftCrash impact:");}),"impact diagnostic missing");
            AircraftCrashFx.Tick(Time.time+21);
        }
        static GameObject TrailRoot(GameObject go)
        {
            Type fx=typeof(AircraftCrashFx); Array trails=(Array)fx.GetField("Trails",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            foreach(object trail in trails) if(trail!=null) { Type type=trail.GetType(); if(type.GetField("Aircraft",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(trail)==go) return (GameObject)type.GetField("Root",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(trail); }
            throw new Exception("no FX for actual kill");
        }
        public static void Main()
        {
            Camera cam=new GameObject("camera").AddComponent<Camera>(); cam.transform.position=new Vector3(0,200,-3000*PlayerAn2.K); CameraOwner.Main=cam;
            GameObject bomber=Aircraft("tu95:entry",true);
            Tu95Model.Props.Add(new KeyValuePair<string,Vector3>("Front",new Vector3(8,2,0)));
            bomber.transform.Find("NDR_Tu95").gameObject.GetComponent<Tu95Visual>().Draw(cam);
            AircraftCrashFx.Hit(bomber,bomber.transform.position);
            Vector3 site=AircraftCrashFx.Site(bomber);
            Check((site-new Vector3(8,2,-1)*PlayerAn2.K).magnitude<0.001f,"engine site used camera proxy transform");
            AirKills.Downed(NpcAircraft.Find(bomber),bomber.transform.position);
            Check(RevivalPlugin.L.Lines.Exists(delegate(string line){return line.Contains("path=AirKills.Downed")&&line.Contains("fall=yes fx=4 sim=World");}),"kill diagnostic missing");
            An2Glide once=bomber.GetComponent<An2Glide>(); PlayerAn2.ShotDown(bomber,bomber.transform.position); Check(bomber.GetComponent<An2Glide>()==once,"duplicate fall");
            Fall(bomber,cam);
            Time.time=Time.unscaledTime=0;
            GameObject escort=Aircraft("escort",false);
            GepardAir.Register("NPC aircraft",delegate(List<GameObject> list){list.Add(escort);},PlayerAn2.ShotDown,PlayerAn2.Down,0);
            Check(GepardAir.KillNow(escort,escort.transform.position),"outright kill entry not reached");
            Check(escort.GetComponent<AircraftCrashVisual>()!=null,"An2 far visual missing");
            Fall(escort,cam);
            Time.time=Time.unscaledTime=0;
            GameObject rifle=Aircraft("an2t:rifle",false); NpcAircraft.Flight rf=NpcAircraft.Find(rifle);
            for(int i=0;i<29;i++) NpcAircraft.Shot(rf,-1); Check(rifle.GetComponent<An2Glide>()==null,"rifle killed too early");
            NpcAircraft.Shot(rf,-1); Check(rifle.GetComponent<An2Glide>()!=null,"rifle ledger kill did not fall");
            GameObject blast=new GameObject("existing heli blast");
            ParticleSystem burst=blast.AddComponent<ParticleSystem>(); burst.main.simulationSpace=ParticleSystemSimulationSpace.World; burst.main.startLifetime=8; burst.main.startSize=50; burst.emission.enabled=true; burst.emission.rateOverTime=12;
            AircraftParticleDraw.Attach(blast);
            Time.time=Time.unscaledTime=1; Camera.Render(cam);
            AircraftParticleDraw impact=blast.GetComponent<AircraftParticleDraw>();
            GameObject proxyRoot=(GameObject)typeof(AircraftParticleDraw).GetField("_drawRoot",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(impact);
            ParticleSystem projection=proxyRoot.GetComponentsInChildren<ParticleSystem>(true)[0];
            ParticleSystem.Particle[] impactBuffer=new ParticleSystem.Particle[256];
            Check(projection.GetParticles(impactBuffer)>0,"existing blast/wreck particles have no far proxy");
            Check((impactBuffer[0].position-cam.transform.position).magnitude<cam.farClipPlane,"impact still outside far clip");
            Check(!burst.GetComponent<ParticleSystemRenderer>().enabled,"real far blast rendered twice");
            cam.transform.position=new Vector3(0,100,0); Time.time=Time.unscaledTime=1.1f; Camera.Render(cam);
            Check(burst.GetComponent<ParticleSystemRenderer>().enabled&&!projection.GetComponent<ParticleSystemRenderer>().enabled,"near impact did not restore native renderer");
            Check(-AircraftFallCore.VerticalSpeed(0,4,85)>=50,"vertical speed below 50m/s after4s");
            Check(Math.Abs(AircraftFallCore.VerticalTravel(0,4,65)*PlayerHeli.K-HeliDrop.Drop(0,PlayerHeli.K,4))<0.001,"heli fall differs from shared curve");
            GameObject wreck=new GameObject("wreck"); HeliHold.Attach(wreck); FakeContainer hold=wreck.GetComponent<FakeContainer>();
            Check(FakeContainer.Exceptions==0&&hold.AwakeRan,"wreck Awake exception");
            Check(hold._containerData.MaxSlots==200&&hold._containerData.Slots.Length==200&&hold.IsSpawnedData,"wreck hold not configured before Awake");
            Check(wreck.activeSelf,"wreck activation lost");
            GameObject inactive=new GameObject("inactive"); inactive.SetActive(false); HeliHold.Attach(inactive); Check(!inactive.activeSelf,"inactive carrier was activated"); inactive.SetActive(true); Check(FakeContainer.Exceptions==0,"deferred Awake failed");
            Console.WriteLine("PASS real C# kill entries/fall/FX/visual/hold: "+checks+" assertions");
        }
    }
}

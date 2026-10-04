"""Z K2: production C# 3.0 core/adapter with T2's all-collider doubles.

No game launch. Route topology is controlled independently of physics so a
free endpoint cannot stand in for a safe detour/warp. PhysX/F6 remain live QA.
"""
from pathlib import Path
import os
import re
import subprocess
from npc_vault_check import HARNESS as VAULT_HARNESS

ROOT = Path(__file__).resolve().parents[1]

EXTRA = r'''
namespace UnityEngine
{
    public class Object {
        public static void Destroy(GameObject g) { g.Destroyed=true; }
        public static void Destroy(Component c) { c.Destroyed=true; }
    }
    public class MonoBehaviour:Component {}
    public class GameObject
    {
        public Transform transform=new Transform(); public bool Destroyed;
        public GameObject(string name) { }
        public T AddComponent<T>() where T:Component,new() { T c=new T(); c.gameObject=this; return c; }
    }
    public struct Quaternion { public static Quaternion identity { get { return new Quaternion(); } } }
    public class TerrainCollider:Collider {}
    public class CharacterController:Collider {}
}
namespace UnityEngine.AI
{
    public enum NavMeshPathStatus { PathComplete,PathPartial }
    public class NavMeshPath { public NavMeshPathStatus status; }
    public enum NavMeshObstacleShape { Box }
    public class NavMeshObstacle:Component
    {
        public NavMeshObstacleShape shape; public Vector3 center,size;
        public bool carving,carveOnlyStationary,enabled=true; public float carvingTimeToStationary;
    }
}
namespace NextDayRevival
{
    class CoverPoint { public Vector3 Pos; }
    class CoverPick { public bool Found,Confirmed; public CoverPoint Point=new CoverPoint(); }
    class MercSense { public CoverPick Pick=new CoverPick(); public float PickAt; }
    class MercUnit { public MercSense Sense=new MercSense(); }
    public static partial class NpcWar
    {
        static System.Type _npcType;
        // i-m3 move-owner hooks (Revival.MercMoveOwner.cs; research/merc_tick_check.py covers them).
        static bool MercOwnerDetourPick(MercUnit u,Vector3 me,Vector3 pick){ return true; }
        static void MercOwnerEvent(MercUnit u,string what,Vector3 from,Vector3 to,float now){ }
        static int Issues;
        static bool Retarget(Fighter f,Vector3 goal,float now)
        { f.NavGoal=NavDestination(f,goal,now); Issues++; return true; }
        public static void NavAdapterTest()
        {
            Physics.Reset(); Issues=0; _navCount=0; _navHead=0; _navBudgetAt=0;
            Fighter calm=new Fighter(12,false); NavigationStep(calm,0); NavigationStep(calm,2);
            Check.Need(Physics.Calls==0 && Issues==0,"calm NPC did nav work");
            Fighter merc=new Fighter(13,true); merc.Ordered=new Vector3(30,0,0);
            NavigationStep(merc,3); Check.Need(Issues==0,"early recovery");
            NavigationStep(merc,4.5f);
            Check.Need(Issues==1 && merc.Navigation.Detouring,"first stall did not detour");
            Check.Need(merc.Ordered.x==30 && merc.WantMain==MainRun,"changed tactical order/state");
            Check.Need(merc.NavGoal.z!=0,"native goal did not use detour");
            NavigationStep(merc,6f);
            Check.Need(merc.Nav.Warps==1 && !merc.HasOrder && !merc.Navigation.Detouring,"second stall did not nudge/resume");
            Check.Need(merc.Nav.LastWarp.sqrMagnitude<=17.64f,"unbounded nudge");
            Fighter shooting=new Fighter(14,true); shooting.WantMain=MainIdle;
            int calls=Physics.Calls; NavigationStep(shooting,10); NavigationStep(shooting,12);
            Check.Need(Physics.Calls==calls && !shooting.Navigation.Watching,"shooting caused recovery");
            Fighter reload=new Fighter(15,true); reload.Reload=true;
            NavigationStep(reload,13); NavigationStep(reload,15);
            Check.Need(!reload.Navigation.Watching,"reload counted as stall");
            Fighter enemy=new Fighter(16,false); enemy.Target=new Transform(); enemy.LastSeen=20;
            NavigationStep(enemy,20); NavigationStep(enemy,21.5f);
            Check.Need(enemy.Navigation.Detouring,"ordinary combat NPC missed recovery");
            Fighter offMesh=new Fighter(17,true); offMesh.Nav.isOnNavMesh=false;
            NavigationStep(offMesh,24); NavigationStep(offMesh,25.5f); NavigationStep(offMesh,27);
            Check.Need(offMesh.Nav.Warps==1 && !offMesh.HasOrder,"off-mesh actor could not reattach safely");
            Fighter[] men=new Fighter[6];
            _navCount=0; _navHead=0; _navBudgetAt=0;
            for(int i=0;i<6;i++) men[i]=new Fighter(i+1,true);
            int served=0; int[] visits=new int[6];
            for(int frame=0;frame<120;frame++)
                for(int i=0;i<6;i++) if(NavTurn(men[i],frame/60f)) { served++; visits[i]++; }
            Check.Need(served<=20,"aggregate recovery budget");
            for(int i=0;i<6;i++) Check.Need(visits[i]>=2,"FIFO starvation");
            Console.WriteLine("PASS production AI: calm/shoot/reload excluded; merc + combat NPC detour then nudge; goal preserved; six-actor FIFO <=10/s");
        }
        public static void ObstacleTest()
        {
            Physics.Reset();
            Box fence=Physics.Add(3,0,-2,3.3f,6,2);
            fence.Collider.transform.lossyScale=new Vector3(2,3,4);
            Physics.Add(5,0,-2,5.3f,2.5f,2); // low -> T2, not carved
            Box vehicle=Physics.Add(7,0,-2,9,7,2); vehicle.Collider.attachedRigidbody=new object();
            Box floor=Physics.Add(-9,-1,-9,9,0,9);
            Box actor=Physics.Add(-4,0,-2,-3,6,2); actor.Collider.Person=true;
            Box existing=Physics.Add(-7,0,-2,-6,6,2); existing.Collider.HasObstacle=true;
            Transform sensor=new Transform();
            for(int i=0;i<60;i++) NpcNavObstacles.Step(Vector3.zero,sensor,null,30+i*0.11f);
            var field=typeof(NpcNavObstacles).GetField("Owned",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            NavMeshObstacle[] owned=(NavMeshObstacle[])field.GetValue(null); int n=0;
            for(int i=0;i<owned.Length;i++) if(owned[i]!=null) { n++; Check.Need(owned[i].carving,"not carving"); }
            Check.Need(n==1,"wrong obstacle classification: "+n);
            Check.Need(Physics.SphereMask==-1,"obstacle sensing omitted layers");
            NavMeshObstacle carve=null;
            for(int i=0;i<owned.Length;i++) if(owned[i]!=null) carve=owned[i];
            Check.Need(carve.gameObject.transform.localScale.sqrMagnitude==3
                && carve.size.x==fence.Collider.bounds.size.x,"carve lost world bounds");
            var ownerField=typeof(NpcNavObstacles).GetField("Owners",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            NpcNavCarveOwner[] owners=(NpcNavCarveOwner[])ownerField.GetValue(null); NpcNavCarveOwner life=null;
            for(int i=0;i<owners.Length;i++) if(owners[i]!=null) life=owners[i];
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(NpcNavCarveOwner).GetMethod("OnDisable",flags).Invoke(life,null);
            Check.Need(!carve.enabled,"disabled source still carved");
            typeof(NpcNavCarveOwner).GetMethod("OnEnable",flags).Invoke(life,null);
            Check.Need(carve.enabled,"reenabled source lost carve");
            typeof(NpcNavCarveOwner).GetMethod("OnDestroy",flags).Invoke(life,null);
            Check.Need(carve.gameObject.Destroyed,"unloaded source left ghost carve");
            Transform[] six=new Transform[6];
            for(int i=0;i<6;i++) { six[i]=new Transform(); six[i].position=new Vector3((i+1)*20,0,0); }
            Physics.SphereSensors=0; int before=Physics.SphereCalls;
            for(int frame=0;frame<60;frame++)
                for(int i=0;i<6;i++) NpcNavObstacles.Step(six[i].position,six[i],null,50+frame*0.11f);
            Check.Need(Physics.SphereSensors==63,"obstacle sensing starved a distant merc");
            Check.Need(Physics.SphereCalls-before<=14,"aggregate overlap cadence exceeded 2 Hz");
            Console.WriteLine("PASS carve lifecycle: disable/enable/destroy; exact world bounds independent of prefab scale");
            Console.WriteLine("PASS obstacle sensing: six separated mercs served fairly; <=2 Hz aggregate overlaps");
            Console.WriteLine("PASS production obstacle registry: tall fence carved, low lip delegated to T2; floor/vehicle/person/existing carve excluded; ALL layers");
        }
        public static void SteadyCost()
        {
            Physics.Reset(); Fighter[] men=new Fighter[6];
            for(int i=0;i<6;i++) { men[i]=new Fighter(i+1,true); men[i].Ordered=new Vector3(100000,0,0); }
            for(int frame=0;frame<1000;frame++)
                for(int i=0;i<6;i++) { men[i].Tr.position=new Vector3(frame*0.2f,0,i*5); NavigationStep(men[i],100+frame/60f); }
            var sw=new Stopwatch(); sw.Start(); sw.Stop(); sw.Reset(); GC.Collect();
            long bytes=GC.GetTotalMemory(false); int gc=GC.CollectionCount(0);
            sw.Start();
            for(int frame=1000;frame<21000;frame++)
                for(int i=0;i<6;i++) { men[i].Tr.position=new Vector3(frame*0.2f,0,i*5); NavigationStep(men[i],100+frame/60f); }
            sw.Stop(); long growth=GC.GetTotalMemory(false)-bytes;
            Check.Need(GC.CollectionCount(0)==gc && growth==0,"steady adapter allocation");
            Console.WriteLine("PASS six-actor production nav ticks: "+(sw.Elapsed.TotalMilliseconds/20000).ToString("F4")+" ms/frame analytic; GC0=0; retained delta=0 bytes (not Unity/F6)");
        }
    }
}
static class NavCheck
{
    static PhysicsNpcNavWorld World=PhysicsNpcNavWorld.Instance;
    static Vector3 Goal=new Vector3(30,0,0);
    static void State()
    {
        NpcNavState s=new NpcNavState();
        Check.Need(s.Observe(Vector3.zero,true,0)==0,"initial clock");
        Check.Need(s.Observe(new Vector3(1.39f,0,0),true,1.49f)==0,"early/slow progress");
        Check.Need(s.Observe(new Vector3(1.39f,0,0),true,1.5f)==1,"1.5 second detour threshold");
        s.Tried(1,Vector3.zero,1.5f);
        Check.Need(s.Observe(Vector3.zero,true,2.99f)==0,"early escalation");
        Check.Need(s.Observe(Vector3.zero,true,3f)==2,"second window nudge");
        Check.Need(s.Observe(new Vector3(1.4f,0,0),true,3f)==0 && s.Stage==0,"progress did not reset");
        s.Detouring=true; s.Goal=Goal; s.Via=new Vector3(0,0,8.4f); s.Until=6;
        Check.Need(s.Destination(Goal,Vector3.zero,4).z==8.4f,"detour lost");
        Check.Need(s.Destination(new Vector3(50,0,0),Vector3.zero,4).x==50 && !s.Detouring,"fresh order ignored");
        s.Detouring=true; s.Until=6; s.Destination(Goal,Vector3.zero,6);
        Check.Need(!s.Detouring,"finite detour timeout");
        s.Detouring=true; s.Until=9; s.Destination(Goal,s.Via,7);
        Check.Need(!s.Detouring,"arrival did not resume");
        s.Observe(Vector3.zero,false,8); Check.Need(!s.Watching && s.Stage==0,"idle clock leaked");
        Console.WriteLine("PASS progress: <0.5 m/1.5 s -> detour; next window -> nudge; progress/hold reset; command/arrival/timeout resume");
    }
    static bool Plan(bool nudge,out Vector3 via)
    { return NpcNavCore.Plan(World,Vector3.zero,Goal,Vector3.zero,false,nudge,0,out via); }
    static void Dense(Vector3 via)
    {
        for(int sample=0;sample<=500;sample++)
        {
            Vector3 p=via*(sample/500f);
            for(int i=0;i<Physics.Count;i++) Check.Need(!Physics.Touch(p,NpcVaultCore.Radius,NpcVaultCore.Height,Physics.Boxes[i]),"independent dense body intersection");
        }
    }
    static void Geometry()
    {
        Vector3 via; Physics.Reset();
        Physics.Add(1.6f,0,-2,2.1f,6,2); // forward wall
        Check.Need(Plan(false,out via),"wall detour rejected"); Dense(via);
        // Every side candidate blocked: props + slabs + fence, not just the wall.
        Physics.Add(-1,0,1,1,6,9); Physics.Add(-1,0,-9,1,6,-1);
        Physics.Add(-4,0,-10,-1,6,10);
        Check.Need(!Plan(false,out via),"ignored neighbouring props/slabs/fence");
        Check.Need(!Plan(true,out via),"unsafe emergency teleport");
        Physics.Reset(); Physics.NavOff=true; Check.Need(!Plan(false,out via),"no mesh accepted");
        Physics.Reset(); Physics.GroundOff=true; Check.Need(!Plan(true,out via),"void warp accepted");
        Physics.Reset(); Physics.Saturate=true; Check.Need(!Plan(true,out via),"filled query buffer accepted");
        Physics.Reset(); NavMesh.RouteOff=true; Check.Need(!Plan(false,out via),"incomplete route accepted"); NavMesh.RouteOff=false;
        NavMesh.EdgeBlocked=true; Check.Need(!Plan(true,out via),"blocked mesh edge warp"); NavMesh.EdgeBlocked=false;
        Physics.Reset(); Physics.Add(-12,3,-12,12,3.2f,12);
        Check.Need(!Plan(true,out via),"low ceiling ignored");
        Physics.Reset(); Check.Need(Plan(true,out via) && via.sqrMagnitude<=17.64f,"safe bounded nudge rejected"); Dense(via);
        Vector3 preferred=new Vector3(0,0,-6);
        Check.Need(NpcNavCore.Plan(World,Vector3.zero,Goal,preferred,true,false,0,out via) && via.z==-6,"M1 cached cover not preferred");
        for(int seed=0;seed<240;seed++)
        {
            Physics.Reset(); float offset=1.4f+(seed%12)*0.1f;
            Physics.Add(offset,0,-2,offset+0.6f,6,2);
            Physics.Add(-1,0,2,1,6,4+(seed%5));
            if(Plan(false,out via)) Dense(via);
        }
        Console.WriteLine("PASS ALL-collider geometry + dense body checks: wall/prop/slab/fence/ceiling, void/mesh/topology/saturation, bounded nudge, M1 cover preference; 240 fixtures");
    }
    static void Cost()
    {
        Physics.Reset(); Vector3 via; for(int i=0;i<1000;i++) Plan(false,out via);
        var sw=new Stopwatch(); sw.Start(); sw.Stop(); sw.Reset();
        GC.Collect(); int gc=GC.CollectionCount(0); long bytes=GC.GetTotalMemory(false);
        sw.Start(); for(int i=0;i<20000;i++) Plan(false,out via); sw.Stop();
        long growth=GC.GetTotalMemory(false)-bytes;
        Check.Need(GC.CollectionCount(0)==gc,"steady GC");
        Check.Need(growth==0,"steady retained growth: "+growth);
        Console.WriteLine("PASS 20000 production core/adapter plans: "+(sw.Elapsed.TotalMilliseconds/20000).ToString("F4")+" ms/plan analytic; GC0=0; retained delta="+growth+" bytes (not PhysX/F6)");
    }
    public static int Main()
    {
        try { State(); Geometry(); NpcWar.NavAdapterTest(); NpcWar.ObstacleTest(); Cost(); NpcWar.SteadyCost(); return 0; }
        catch(Exception e) { Console.WriteLine("FAIL "+e); return 1; }
    }
}
'''


def harness():
    # Extend T2's independent conservative collider scene rather than inventing
    # another geometry model. Production core + both adapters compile unchanged.
    s = VAULT_HARNESS
    s = s.replace('public class Component { public Transform transform = new Transform(); }', '''public class Component {
        public Transform transform = new Transform(); public GameObject gameObject; public bool Destroyed;
        public bool Person,HasObstacle;
        public T GetComponentInParent<T>() where T:class {
            if(Person && typeof(T)==typeof(CharacterController)) return new CharacterController() as T;
            if(HasObstacle && typeof(T)==typeof(NavMeshObstacle)) return new NavMeshObstacle() as T;
            return null;
        }
        public Component GetComponentInParent(System.Type t) { return null; }
    }''')
    s = s.replace('public Vector3 position;', '''public Vector3 position;
        public Vector3 localScale=new Vector3(1,1,1),lossyScale=new Vector3(1,1,1); public Quaternion rotation;
        public void SetParent(Transform t,bool keep) { }''', 1)
    s = s.replace('public struct Bounds { public Vector3 max; }', '''public struct Bounds {
        public Vector3 min,max;
        public Vector3 center { get { return (min+max)*0.5f; } }
        public Vector3 size { get { return max-min; } }
    }''')
    s = s.replace('public bool Trigger; public object attachedRigidbody;',
                  'public bool Trigger; public bool enabled=true; public bool isTrigger { get { return Trigger; } } public object attachedRigidbody;')
    s = s.replace('Collider.bounds.max=hi;', 'Collider.bounds.max=hi; Collider.bounds.min=lo;')
    s = s.replace('Collider.bounds.min=lo;', 'Collider.bounds.min=lo; Collider.gameObject=new GameObject("fixture");')
    s = s.replace('public static float Min(float a,float b)', 'public static float Max(float a,float b) { return Math.Max(a,b); }\n        public static float Min(float a,float b)')
    s = s.replace('public static int Count, Calls,', '''public static int SphereMask,SphereSensors,SphereCalls;
        public static int OverlapSphereNonAlloc(Vector3 p,float radius,Collider[] hits,int mask,QueryTriggerInteraction q) {
            SphereMask=mask; Calls++; SphereCalls++; int n=0;
            int sensor=(int)(p.x/20)-1; if(sensor>=0 && sensor<6) SphereSensors|=1<<sensor;
            for(int i=0;i<Count && n<hits.Length;i++) hits[n++]=Boxes[i].Collider;
            return n;
        }
        public static int Count, Calls,''')
    s = s.replace('public const int AllAreas=-1;', '''public static bool RouteOff,EdgeBlocked;
        public static bool CalculatePath(Vector3 a,Vector3 b,int area,NavMeshPath p) {
            p.status=RouteOff?NavMeshPathStatus.PathPartial:NavMeshPathStatus.PathComplete; return !RouteOff;
        }
        public static bool Raycast(Vector3 a,Vector3 b,out NavMeshHit h,int area) { h=new NavMeshHit(); return EdgeBlocked; }
        public const int AllAreas=-1;''')
    s = s.replace('public int areaMask=-1;', 'public int areaMask=-1,Warps; public Vector3 LastWarp; public bool Warp(Vector3 p) { Warps++; LastWarp=p; return true; }')
    s = s.replace('public const int S_NpcVaultT=185;', 'public const int S_NpcVaultT=185,S_NpcNavT=187;')
    s = s.replace('class Squad { public object Merc; }', 'class Squad { public MercUnit Merc; }')
    s = s.replace('public NpcVaultMotion Vault;', 'public NpcVaultMotion Vault; public NpcNavState Navigation; public Vector3 NavGoal; public bool Reload;')
    s = s.replace('Squad.Merc=new object();', 'Squad.Merc=new MercUnit();')
    s = s.replace('static bool Reloading(Fighter f) { return false; }', 'static bool Reloading(Fighter f) { return f.Reload; }')
    s = s.replace('public static int Main()', 'public static int VaultMain()')
    return s + EXTRA


def main():
    work = ROOT / 'build/npc_nav_check'
    work.mkdir(parents=True, exist_ok=True)
    src = work / 'check.cs'
    src.write_text(harness(), encoding='ascii')
    exe = work / 'check.exe'
    csc = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    files = ['Revival.NpcVaultCore.cs', 'Revival.NpcVault.cs', 'Revival.NpcNavCore.cs', 'Revival.NpcNav.cs']
    result = subprocess.run([str(csc), '/nologo', '/warn:0', '/optimize+', '/main:NavCheck', '/out:' + str(exe),
                             str(src)] + [str(ROOT / f) for f in files], capture_output=True)
    if result.returncode:
        print(result.stdout.decode('utf-8', 'replace'))
        return 1
    result = subprocess.run([str(exe)], capture_output=True)
    print(result.stdout.decode('utf-8', 'replace').rstrip())
    if result.returncode:
        return result.returncode
    npc = (ROOT / 'Revival.NpcCombat.cs').read_text(encoding='utf-8')
    prof = (ROOT / 'RevivalFrameProfiler.cs').read_text(encoding='utf-8')
    adapter = (ROOT / 'Revival.NpcNav.cs').read_text(encoding='ascii')
    # Merged queues append profiler slots and ladder cleanup to the guard.
    # Validate the actual named slot and guarded continue, not old formatting.
    slot = int(re.search(r'const int S_NpcNavT = (\d+)', prof).group(1))
    names = re.findall(r'"([^"\n]*)"', prof.split('static readonly string[] Names', 1)[1].split('};', 1)[0])
    checks = {
        'three existing combat loops + both native movement seams':
            npc.count('NavigationStep(') == 3 and npc.count('NavDestination(') == 2,
        'legacy recovery cannot compete with fast combat recovery': 'if (NavigationEligible(f, now)) return;' in npc,
        'new unique F6 slot': names[slot].strip() == 'NpcNav.Recovery.Sub' and names.count(names[slot]) == 1,
        'bounded/nonalloc queries, no scene scans/corners/reflection invokes':
            'OverlapSphereNonAlloc' in adapter and not any(x in adapter for x in ['FindObjectsOfType', '.corners', '.Invoke(', 'Physics.OverlapSphere(']),
        'native authority guards remain in caller': bool(re.search(
            r'if \(!IsMine\(f\.Ai\)\)\s*(?:continue;|\{[^}]*\bcontinue;[^}]*\})', npc)),
    }
    for label, passed in checks.items():
        print(('PASS ' if passed else 'FAIL ') + label)
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    raise SystemExit(main())

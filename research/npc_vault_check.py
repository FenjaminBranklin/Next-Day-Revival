"""Z T2: compile unchanged vault core/physics/AI adapter with C# 3.0.

Analytic fixtures contain all solids in each scene: pit bags, fences, gun,
slabs, props, roof/ceiling and occupants. A separate dense capsule check
validates accepted arcs. No Unity, game launch, network or Steam writes.
"""
from pathlib import Path
import os
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]

HARNESS = r'''
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using NextDayRevival;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float a,float b,float c) { x=a; y=b; z=c; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0,1,0); } }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
        public static Vector3 operator +(Vector3 a,Vector3 b) { return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z); }
        public static Vector3 operator -(Vector3 a,Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public static Vector3 operator -(Vector3 a) { return new Vector3(-a.x,-a.y,-a.z); }
        public static Vector3 operator *(Vector3 a,float b) { return new Vector3(a.x*b,a.y*b,a.z*b); }
        public static Vector3 operator /(Vector3 a,float b) { return a*(1f/b); }
    }
    public static class Mathf
    {
        public static float Min(float a,float b) { return Math.Min(a,b); }
        public static float Abs(float a) { return Math.Abs(a); }
        public static float Clamp01(float a) { return Math.Max(0,Math.Min(1,a)); }
    }
    public class Component { public Transform transform = new Transform(); }
    public class Transform
    {
        public Vector3 position;
        public int Id;
        public bool IsChildOf(Transform other) { return this==other; }
    }
    public struct Bounds { public Vector3 max; }
    public class Collider : Component { public bool Trigger; public object attachedRigidbody; public Bounds bounds; }
    public struct RaycastHit { public Collider collider; public float distance; public Vector3 point; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Time { public static float time; }
    public sealed class Box
    {
        public Vector3 Lo,Hi;
        public Collider Collider=new Collider();
        public Box(Vector3 lo,Vector3 hi) { Lo=lo; Hi=hi; Collider.bounds=new Bounds(); Collider.bounds.max=hi; }
    }
    public static class Physics
    {
        public static readonly Box[] Boxes=new Box[128];
        public static readonly int[] Probes=new int[128];
        public static int Count, Calls, MaxCalls, RayMask, OverlapMask, SweepMask;
        public static bool GroundOff, NavOff, Saturate;
        public static float NavOffset;
        public static void Reset()
        {
            Count=0; Calls=0; GroundOff=false; NavOff=false; Saturate=false; NavOffset=0;
            Array.Clear(Probes,0,Probes.Length);
        }
        public static Box Add(float x0,float y0,float z0,float x1,float y1,float z1)
        {
            Box b=new Box(new Vector3(x0,y0,z0),new Vector3(x1,y1,z1)); Boxes[Count++]=b; return b;
        }
        static bool Axis(float p,float d,float lo,float hi,ref float enter,ref float leave)
        {
            if(Math.Abs(d)<1e-7f) return p>=lo && p<=hi;
            float a=(lo-p)/d,b=(hi-p)/d;
            if(a>b) { float c=a; a=b; b=c; }
            enter=Math.Max(enter,a); leave=Math.Min(leave,b); return leave>=enter;
        }
        public static bool Hit(Vector3 p,Vector3 d,float length,Vector3 lo,Vector3 hi,out float at)
        {
            float enter=0,leave=length;
            bool yes=Axis(p.x,d.x,lo.x,hi.x,ref enter,ref leave)
                && Axis(p.y,d.y,lo.y,hi.y,ref enter,ref leave)
                && Axis(p.z,d.z,lo.z,hi.z,ref enter,ref leave);
            at=enter; return yes;
        }
        public static int RaycastNonAlloc(Vector3 p,Vector3 d,RaycastHit[] hits,float length,int mask,QueryTriggerInteraction ignore)
        {
            Calls++; RayMask=mask;
            if(d.y==0 && p.y<1f && PhysicsVaultWorld.Instance.Ignore!=null)
                Probes[PhysicsVaultWorld.Instance.Ignore.Id]++;
            if(Saturate) return hits.Length;
            int n=0;
            for(int i=0;i<Count && n<hits.Length;i++)
            {
                Box b=Boxes[i]; float at;
                if(b.Collider.Trigger || !Hit(p,d,length,b.Lo,b.Hi,out at)) continue;
                hits[n].collider=b.Collider; hits[n].distance=at; hits[n].point=p+d*at; n++;
            }
            if(!GroundOff && d.y<0 && p.y>=0 && -p.y/d.y<=length && n<hits.Length)
            {
                hits[n].collider=Floor; hits[n].distance=-p.y/d.y; hits[n].point=p+d*hits[n].distance; n++;
            }
            return n;
        }
        static readonly Collider Floor=new Collider();
        // Conservative box of a vertical body capsule for swept intersection.
        public static bool Touch(Vector3 feet,float radius,float height,Box b)
        {
            return feet.x+radius>b.Lo.x+0.0001f && feet.x-radius<b.Hi.x-0.0001f
                && feet.z+radius>b.Lo.z+0.0001f && feet.z-radius<b.Hi.z-0.0001f
                && feet.y+height>b.Lo.y+0.0001f && feet.y+0.12f<b.Hi.y-0.0001f;
        }
        public static int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float radius,Collider[] hits,int mask,QueryTriggerInteraction ignore)
        {
            Calls++; OverlapMask=mask; if(Saturate) return hits.Length;
            Vector3 feet=a-Vector3.up*(radius+0.12f); float height=b.y-feet.y+radius;
            int n=0;
            for(int i=0;i<Count && n<hits.Length;i++)
                if(!Boxes[i].Collider.Trigger && Touch(feet,radius,height,Boxes[i])) hits[n++]=Boxes[i].Collider;
            return n;
        }
        public static int CapsuleCastNonAlloc(Vector3 a,Vector3 b,float radius,Vector3 d,RaycastHit[] hits,float length,int mask,QueryTriggerInteraction ignore)
        {
            Calls++; SweepMask=mask; if(Saturate) return hits.Length;
            Vector3 feet=a-Vector3.up*(radius+0.12f); float height=b.y-feet.y+radius;
            int n=0;
            for(int i=0;i<Count && n<hits.Length;i++)
            {
                Box box=Boxes[i]; float at;
                Vector3 lo=box.Lo-new Vector3(radius,height,radius);
                Vector3 hi=box.Hi+new Vector3(radius,-0.12f,radius);
                // Small tolerance: mere tangency to ground/side is clear.
                lo=lo+new Vector3(0.0001f,0.0001f,0.0001f); hi=hi-new Vector3(0.0001f,0.0001f,0.0001f);
                if(box.Collider.Trigger || !Hit(feet,d,length,lo,hi,out at)) continue;
                hits[n].collider=box.Collider; hits[n].distance=at; n++;
            }
            return n;
        }
    }
}
namespace UnityEngine.AI
{
    public struct NavMeshHit { public Vector3 position; }
    public static class NavMesh
    {
        public const int AllAreas=-1;
        public static bool SamplePosition(Vector3 p,out NavMeshHit hit,float radius,int area)
        { hit=new NavMeshHit(); hit.position=p+Vector3.up*Physics.NavOffset; return !Physics.NavOff; }
    }
    public class NavMeshAgent
    {
        public bool isActiveAndEnabled=true,isOnNavMesh=true,pathPending;
        public int areaMask=-1;
    }
}
namespace NextDayRevival
{
    public sealed class NpcVaultMotion
    {
        public bool Busy,Finished;
        public NavMeshAgent Agent;
        public static NpcVaultMotion Of(Component ai,NavMeshAgent agent) { NpcVaultMotion m=new NpcVaultMotion(); m.Agent=agent; return m; }
        internal void Begin(VaultPlan plan,float now) { Busy=true; }
    }
    public static class FrameProf
    {
        public const int S_NpcVaultT=185;
        public static void S(int n) {} public static void E(int n) {}
    }
    public static class TowerRoof { public static bool Climbing(Transform t) { return false; } }
    public static partial class NpcWar
    {
        const int MainIdle=0,MainRun=2,MainWalk=1,AddNone=0,PoseStand=0;
        class Squad { public object Merc; }
        class Fighter
        {
            public Component Ai=new Component(); public Transform Tr;
            public Squad Squad; public Transform Target;
            public float LastSeen,NextMove,NextState,MovedAt,NextVault,PlantedSince;
            public bool HasOrder=true;
            public int WantMain=MainRun;
            public Vector3 LastPos,Ordered=new Vector3(9,0,0);
            public NpcVaultMotion Vault;
            public NavMeshAgent Nav=new NavMeshAgent();
            public Fighter(int id,bool merc) { Tr=Ai.transform; Tr.Id=id; Squad=new Squad(); if(merc) Squad.Merc=new object(); }
        }
        static bool Alive(Component ai) { return ai!=null; }
        static bool Reloading(Fighter f) { return false; }
        static NavMeshAgent Agent(Fighter f) { return f.Nav; }
        static void ReleaseAim(Fighter f) {}
        static void Drive(Fighter f,int main,int add,int pose,float now,bool hold) { f.WantMain=main; }
        public static void AdapterTest()
        {
            Physics.Reset(); _vaultCount=0; _vaultHead=0; _vaultBudgetAt=0;
            Fighter[] men=new Fighter[6]; for(int i=0;i<men.Length;i++) men[i]=new Fighter(i+1,true);
            for(int frame=0;frame<120;frame++)
            {
                float now=frame/60f; Time.time=now;
                for(int i=0;i<men.Length;i++) VaultStep(men[i],now);
            }
            for(int i=0;i<men.Length;i++) Check.Need(Physics.Probes[i+1]>=2,"FIFO starvation merc "+i);
            int total=0; for(int i=1;i<=6;i++) total+=Physics.Probes[i];
            Check.Need(total<=20,"aggregate probe limit");
            Physics.Reset(); Fighter calm=new Fighter(7,false); _vaultBudgetAt=0;
            VaultStep(calm,5f); Check.Need(Physics.Calls==0,"calm NPC queried geometry");
            // Compile the actual AI seam, including movement completion reset.
            Physics.Add(1.6f,0,-4,2.1f,2.52f,4);
            Fighter merc=new Fighter(8,true); _vaultCount=0; _vaultBudgetAt=0;
            Check.Need(VaultStep(merc,6f) && merc.Vault.Busy,"AI did not start vault");
            merc.Vault.Busy=false; merc.Vault.Finished=true;
            VaultStep(merc,7f);
            Check.Need(!merc.HasOrder && merc.NextMove==0 && merc.WantMain==-1,"AI did not resume original order");
            Console.WriteLine("PASS production AI/FIFO: six mercs served, <=10 aggregate probes/s; calm NPC zero calls; order resumes");
        }
    }
}
static class Check
{
    static int checks;
    public static void Need(bool value,string why) { checks++; if(!value) throw new Exception(why); }
    static PhysicsVaultWorld World=PhysicsVaultWorld.Instance;
    static Vector3 Goal=new Vector3(10,0,0);
    static VaultPlan Plan(float height,float width,bool yes,string name)
    {
        Physics.Reset(); Physics.Add(1.6f,0,-4,1.6f+width,height,4);
        VaultPlan p; bool found=NpcVaultCore.Plan(World,Vector3.zero,Goal,out p);
        Need(found==yes,name);
        if(found) Dense(p,name);
        return p;
    }
    static void Dense(VaultPlan p,string name)
    {
        Need((NpcVaultCore.At(p,0)-p.Start).sqrMagnitude<1e-6f,"start endpoint");
        Need((NpcVaultCore.At(p,p.Duration)-p.End).sqrMagnitude<1e-6f,"end endpoint");
        for(int step=0;step<=1000;step++)
        {
            Vector3 at=NpcVaultCore.At(p,p.Duration*step/1000f);
            for(int b=0;b<Physics.Count;b++)
            {
                Box box=Physics.Boxes[b];
                if(box.Collider.Trigger || box.Collider.transform==World.Ignore) continue;
                Need(!Physics.Touch(at,NpcVaultCore.Radius,NpcVaultCore.Height,box),"dense ALL-collider collision: "+name);
            }
        }
        Physics.MaxCalls=Math.Max(Physics.MaxCalls,Physics.Calls);
    }
    static void Geometry()
    {
        World.Ignore=new Transform();
        VaultPlan p=Plan(2.52f,0.55f,true,"0.9 m sandbag");
        Plan(2.8f,0.10f,true,"1 m fence");
        Plan(NpcVaultCore.Hip,0.3f,true,"hip limit");
        Plan(NpcVaultCore.Hip+0.01f,0.3f,false,"above hip");
        Plan(5.6f,0.4f,false,"building wall");
        Plan(0.2f,0.4f,false,"kerb");
        Plan(2.52f,5.0f,false,"wide obstacle");
        Physics.Reset(); Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"open ground jumped");
        Physics.Add(1.6f,0,-4,2.1f,2.52f,4);
        Physics.Add(2.2f,0,-0.5f,7,2f,0.5f); // AA carriage blocks both landings
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"gun carriage landing");
        Physics.Reset(); Physics.Add(1.6f,0,-4,2.1f,2.52f,4);
        Physics.Add(0,5.0f,-4,7,5.5f,4); // low ceiling above bags
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"ceiling across arc");
        Physics.Reset(); Physics.Add(1.6f,0,-4,2.1f,2.52f,4);
        Physics.Add(2.2f,0,-4,7,1.8f,4); // slab ABOVE nominal NavMesh, all colliders
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"concrete slab behind fence");
        Physics.Reset(); Physics.Add(1.6f,0,-4,2.1f,2.52f,4); Physics.GroundOff=true;
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"no physical landing");
        Physics.GroundOff=false; Physics.NavOff=true;
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"missing NavMesh");
        Physics.NavOff=false; Physics.NavOffset=0.8f;
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"NavMesh under slab projection");
        Physics.Reset(); Physics.Add(1.6f,0,-4,2.1f,2.52f,4);
        Box own=Physics.Add(-0.3f,0,-0.3f,0.3f,4.7f,0.3f); own.Collider.transform=World.Ignore;
        Box trigger=Physics.Add(0,0,-5,7,9,5); trigger.Collider.Trigger=true;
        Need(NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"own body / trigger ignored"); Dense(p,"own/trigger");
        Need(Physics.RayMask==-1 && Physics.OverlapMask==-1 && Physics.SweepMask==-1,"all collider layers");
        Physics.Saturate=true;
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"saturated buffers accepted");
        Physics.Reset(); Physics.Add(0.6f,0,-4,1.2f,2.52f,4);
        Need(NpcVaultCore.Plan(World,Vector3.zero,Goal,out p) && p.RecoveryOnly,"already stuck lip did not authorize safe recovery");
        Vector3 recovery;
        Need(NpcVaultCore.Recover(World,p,out recovery) && (recovery-p.End).sqrMagnitude<1e-6f,"stuck-lip recovery destination");
        Physics.Add(-1,5f,-4,7,5.5f,4);
        Need(!NpcVaultCore.Plan(World,Vector3.zero,Goal,out p),"stuck lip waived ceiling");
        Console.WriteLine("PASS already-stuck feet: raised crossing/landing proven, safe teleport; ceiling still rejected");
        Console.WriteLine("PASS geometry: sandbags/fence/hip height; walls, ceiling, gun, slabs, void/nav rejected; ALL layers and saturation");
        // Independent dense check on 240 height/width/approach fixtures.
        int accepted=0;
        for(int h=0;h<12;h++) for(int w=0;w<10;w++) for(int side=0;side<2;side++)
        {
            Physics.Reset(); float height=0.4f+h*0.28f,width=0.1f+w*0.22f;
            Physics.Add(1.6f,0,-4,1.6f+width,height,4);
            Vector3 start=side==0?Vector3.zero:new Vector3(8,0,0);
            Vector3 goal=side==0?Goal:new Vector3(-2,0,0);
            if(NpcVaultCore.Plan(World,start,goal,out p)) { accepted++; Dense(p,"fuzz"); }
        }
        Need(accepted>60,"too few useful crossings");
        Console.WriteLine("PASS dense independent capsule checks: 240 fixtures, "+accepted+" accepted arcs, 1001 samples/arc; maximum planner world calls="+Physics.MaxCalls);
    }
    static void Recovery()
    {
        VaultPlan p=Plan(2.52f,0.55f,true,"fallback setup"); Vector3 end;
        Need(NpcVaultCore.Recover(World,p,out end) && (end-p.End).sqrMagnitude<1e-6f,"short teleport to proven landing");
        Physics.Add(p.End.x-0.3f,0,-0.3f,p.End.x+0.3f,4.0f,0.3f);
        Need(NpcVaultCore.Recover(World,p,out end) && (end-p.Start).sqrMagnitude<1e-6f,"occupied landing must return");
        Physics.Add(-0.3f,0,-0.3f,0.3f,4.0f,0.3f);
        Need(!NpcVaultCore.Recover(World,p,out end),"both ends occupied cannot teleport");
        Physics.Reset(); Physics.GroundOff=true;
        Need(!NpcVaultCore.Recover(World,p,out end),"removed floor cannot teleport");
        Console.WriteLine("PASS failed-jump fallback: proven landing, occupied end returns, both occupied/removed floor refuse teleport");
    }
    static void Cost()
    {
        Plan(2.52f,0.55f,true,"cost warmup"); VaultPlan p;
        for(int i=0;i<1000;i++) NpcVaultCore.Plan(World,Vector3.zero,Goal,out p);
        Stopwatch sw=new Stopwatch(); sw.Start(); sw.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before=GC.GetTotalMemory(false); int gc=GC.CollectionCount(0);
        sw.Reset(); sw.Start();
        for(int i=0;i<20000;i++) NpcVaultCore.Plan(World,Vector3.zero,Goal,out p);
        sw.Stop(); long growth=GC.GetTotalMemory(false)-before;
        Need(GC.CollectionCount(0)==gc && growth<=1024,"steady core/physics allocations: "+growth+" bytes, GC="+(GC.CollectionCount(0)-gc));
        Console.WriteLine("PASS 20000 production core + physics plans: "+(sw.Elapsed.TotalMilliseconds/20000).ToString("F4")+" ms/plan analytic; GC0=0; retained growth="+growth+" bytes");
    }
    static int Main()
    {
        try { Geometry(); Recovery(); NpcWar.AdapterTest(); Cost(); Console.WriteLine("PASS "+checks+" assertions; live PhysX/animation/Photon/F6 excluded"); return 0; }
        catch(Exception e) { Console.WriteLine("FAIL "+e.Message); return 1; }
    }
}
'''


def main():
    work = ROOT / 'build' / 'npc_vault_check'
    work.mkdir(parents=True, exist_ok=True)
    harness = work / 'check.cs'
    harness.write_text(HARNESS, encoding='ascii')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    exe = work / 'check.exe'
    cmd = [str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe), str(harness),
           str(ROOT / 'Revival.NpcVaultCore.cs'), str(ROOT / 'Revival.NpcVault.cs')]
    result = subprocess.run(cmd, capture_output=True)
    if result.returncode:
        print(result.stdout.decode('utf-8', 'replace'))
        return 1
    result = subprocess.run([str(exe)], capture_output=True)
    print(result.stdout.decode('utf-8', 'replace').rstrip())
    if result.returncode:
        return result.returncode
    motion = (ROOT / 'Revival.NpcVaultMotion.cs').read_text(encoding='utf-8')
    npc = (ROOT / 'Revival.NpcCombat.cs').read_text(encoding='utf-8')
    ride = (ROOT / 'Revival.MercsRide.cs').read_text(encoding='utf-8')
    prof = (ROOT / 'RevivalFrameProfiler.cs').read_text(encoding='utf-8')
    checks = {
        'all three combat loops gate vaults': npc.count('VaultStep(') == 3,
        '4 Hz motion collision checks': '_nextCheck = now + NpcVaultCore.Interval;' in motion,
        'safe fallback used at landing/failure': 'NpcVaultCore.Recover(world, _plan, out end)' in motion,
        'finite peer timeout and dead/ownership cancellation': 'elapsed >= _plan.Duration' in motion and '!Authority() || !NpcWar.VaultAlive(_ai)' in motion,
        'owner and inactive-owner/master packet checks': 'owner != sender' in motion and 'FastCall.Bool(_ownerActive, view)' in motion and 'FastField.GetInt(_actorId, master) != sender' in motion,
        'packet bounds, nonfinite and duplicate rejection': 'data.Length != 13' in motion and 'float.IsNaN' in motion and 'data[2] == motion._serial' in motion and 'NpcVaultCore.MaxSpan' in motion,
        'reliable existing transport, no new event id': 'MercRide.SendAAPacket(_packet)' in motion and 'd[0] == 104f' in ride,
        'native cached jump_run + cleanup': 'animations[i]["jump_run"]' in motion and '_state.enabled = false' in motion and 'Agent.updatePosition = _pos' in motion,
        'two unique F6 slots': 'S_NpcVaultT = 185;' in prof and 'S_NpcVaultL = 186;' in prof and 'NpcVault.Probe.Sub' in prof and 'NpcVault.LateUpdate' in prof,
        'no allocating steady LateUpdate operations': not re.search(r'new\s+(?:\w+\[|object|string|List|Dictionary)|GetComponents|FindObjects', motion.split('void LateUpdate()', 1)[1].split('void Finish(', 1)[0]),
    }
    for label, passed in checks.items():
        print(('PASS ' if passed else 'FAIL ') + label)
    return 0 if all(checks.values()) else 1


if __name__ == '__main__':
    raise SystemExit(main())

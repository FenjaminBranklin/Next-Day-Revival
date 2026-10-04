"""H M3 man air defence: from the C1 tower roof to a seat that fires.

Compiles the production pure cores (Revival.TowerRoofCore.cs,
Revival.FlakPositionsCore.cs, Revival.MercDefenceCore.cs) with csc 3.5 and
drives mercs like NpcWar.MercPostStep / TowerRoof.Leg+StartClimb+LateFrame /
MercAA.OnPacket:

1. starts ON THE TOWER (the five roof posts, both catwalk ladder posts, the
   stair top, inside the cab, the console seat) at five tower yaws, goals
   are the REAL airfield posts from the east tile data (FlakPositionsCore:
   52-K AA-NW/AA-NE/AA-S, ZU-23 ZU-W; earthwork pits with the gate towards
   the tower), the gun's seat at twelve mount headings;
2. roof leg: production Leg (the wrapper turns a near LegWalk into a climb
   down), Path down and the bounded Step walk to the stair foot; with the
   climb refused, the production stall watchdog puts him at the foot;
3. ground leg: stair foot -> pit gate (outside, inside) -> the NavMesh's
   closest stop beside the carriage (carriage radius 0 / 3 / 5.5 u, agent
   stopping distance 0 / 2.8 / 5.6 u), never through a berm;
4. at the post: production AtPost / Note / Rescue on the owner, LeaseNear on
   the master: every merc is seated (the lease seats him, the gun fires with
   a held lease). Negative control: the 6.71 rule (ask within 4 u 3D,
   master 6 u) leaves men standing in front of the gun - Kevin's report;
5. Kevin's squad: six mercs on the roof, nearest-first plan over the four
   real posts: four seated, two extras keep FOLLOW;
6. pins the production seams (MercPostStep, OnPacket, TowerRoof.Leg climb
   rule, ToggleAirDefence extras, MercAD log lines, crew spawn guard).

Code only: no Unity, no game, no renders. Exit 0 and "RESULT: PASS".
"""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
FAILS = []
PASSES = [0]


def ok(cond, what):
    if cond:
        PASSES[0] += 1
    else:
        FAILS.append(what)
        print("FAIL " + what)


def core(name, first):
    text = (ROOT / name).read_text(encoding="utf-8")
    body = text[text.index(first):]
    return body[:body.rindex("}")]


HARNESS = r'''
using System;
using System.Collections.Generic;
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z;
  public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero {get {return new Vector3(0,0,0);}}
 }
 public static class Mathf {
  public static float Sqrt(float f){return (float)Math.Sqrt(f);}
  public static float Abs(float f){return Math.Abs(f);}
  public static float Min(float a,float b){return Math.Min(a,b);}
  public static float Max(float a,float b){return Math.Max(a,b);}
 }
}
namespace NextDayRevival {
using UnityEngine;
static class Sim {
 static int Pass, Fail;
 static void Ok(bool c,string what){ if(c) Pass++; else { Fail++; if(Fail<40) Console.WriteLine("FAIL "+what); } }
 const float K=2.8f;                          // world units per metre (TowerRadar.K, Flak.K)
 const float TowerX=4272.12f, TowerZ=1335.05f; // C1 (FlakPositionsCore.Heading, FlakEngageCore.Centre)
 const float Run=4f*K;                         // 4 m/s
 static float Yaw;
 static Vector3 V(float x,float y,float z){return new Vector3(x,y,z);}
 static float Flat(Vector3 a,Vector3 b){float dx=a.x-b.x,dz=a.z-b.z;return (float)Math.Sqrt(dx*dx+dz*dz);}
 static float D3(Vector3 a,Vector3 b){float dx=a.x-b.x,dy=a.y-b.y,dz=a.z-b.z;return (float)Math.Sqrt(dx*dx+dy*dy+dz*dz);}
 static Vector3 World(Vector3 l){double a=Yaw*Math.PI/180; float c=(float)Math.Cos(a),s=(float)Math.Sin(a);
  return V(TowerX+(l.x*c+l.z*s)*K,l.y*K,TowerZ+(-l.x*s+l.z*c)*K);}
 static Vector3 Local(Vector3 w){double a=Yaw*Math.PI/180; float c=(float)Math.Cos(a),s=(float)Math.Sin(a);
  float x=(w.x-TowerX)/K,z=(w.z-TowerZ)/K; return V(x*c-z*s,w.y/K,x*s+z*c);}

 // ------------------------------------------------ the real pits (east tile)
 const float PadY=FlakPositionsCore.PadLift*K;             // pad top over the terrain
 static Vector3 Pit(int p){return V(FlakPositionsCore.X[p],0f,FlakPositionsCore.Z[p]);}
 static Vector3 Dir(float deg){double a=deg*Math.PI/180; return V((float)Math.Sin(a),0f,(float)Math.Cos(a));}
 // Flak.Build: mount 0.92 m over the root, seat gunner (-0.78, 0.47, -0.36) m
 // on the mount; MercAA.Pose: the seat node minus SeatDrop 2.3 u.
 static Vector3 Seat(int p,float heading){
  Vector3 c=Pit(p); double a=heading*Math.PI/180; float cs=(float)Math.Cos(a),sn=(float)Math.Sin(a);
  float lx=-0.78f*K, lz=-0.36f*K;
  return V(c.x+lx*cs+lz*sn, PadY+(0.92f+0.47f)*K-2.3f, c.z-lx*sn+lz*cs);
 }

 // ------------------------------------------------ tower leg (production)
 static int Hops, Falls;
 static float Tower(ref Vector3 man,Vector3 goalW,int slot,bool climbOk,out bool down){
  down=false;
  Vector3 foot=TowerRoofCore.Foot(0f), exit=TowerRoofCore.Exit(), leg, goal=Local(goalW);
  int r=TowerRoofCore.Leg(man,goal,slot,foot,exit,out leg);
  Ok(r==TowerRoofCore.LegWalk||r==TowerRoofCore.LegClimbDown,"roof start "+slot+": a ground post is a walk/climb down (Leg "+r+")");
  // TowerRoof.Leg (wrapper): near the tower a LegWalk is the bounded climb.
  bool climbDown=r==TowerRoofCore.LegClimbDown||(r==TowerRoofCore.LegWalk&&!TowerRoofCore.GoalUp(goal));
  Ok(climbDown,"roof start "+slot+": the post below means the stairs down");
  float t=0f;
  if(!climbOk){
   // StartClimb refused (all climb slots busy, no agent): MercMove holds
   // him; the production watchdog notices no progress and puts him down.
   MercPostTrack tr=new MercPostTrack(); tr.Reset(0,null,0f);
   for(;t<60f;t+=0.1f){
    MercDefenceCore.Note(tr,Flat(World(man),goalW),0f,t);
    if(MercDefenceCore.Rescue(tr,t,-1f,true)==MercDefenceCore.RescueDescend){ man=foot; down=true; break; }
   }
   return t;
  }
  Vector3[] pts=new Vector3[TowerRoofCore.PathMax];
  int n=TowerRoofCore.Path(false,man,foot,exit,pts);
  TowerRoofCore.Walk w=new TowerRoofCore.Walk(); TowerRoofCore.Begin(ref w,pts,n,0f);
  Vector3 at=man;
  for(int i=0;i<4000;i++){
   Vector3 next; float now=i*0.05f;
   int a=TowerRoofCore.Step(ref w,pts,n,at,now,0.05f,true,out next);
   at=next; t=now;
   if(a==TowerRoofCore.Hop) Hops++;
   if(a==TowerRoofCore.Fallback){ Falls++; at=pts[n-1]; break; }
   if(a==TowerRoofCore.Finished) break;
  }
  man=at; down=Flat(man,foot)<0.05f&&Math.Abs(man.y-foot.y)<0.05f;
  return t;
 }

 // ------------------------------------------------ ground leg and the post
 // The stop the NavMesh gives beside the carriage: the seat itself, or the
 // closest point outside the carriage radius, then the agent's stopping
 // distance short along his approach.
 static Vector3 Stop(int p,Vector3 seat,Vector3 from,float carriage,float stopping){
  Vector3 c=Pit(p), s=V(seat.x,PadY,seat.z);
  float r=Flat(s,c);
  if(r<carriage){ float k=r<1e-3f?0f:carriage/r; s=V(c.x+(s.x-c.x)*k,PadY,c.z+(s.z-c.z)*k); if(r<1e-3f) s=V(c.x,PadY,c.z+carriage); }
  float d=Flat(s,from);
  if(d>1e-3f&&stopping>0f){ float k=Math.Min(stopping,d)/d; s=V(s.x+(from.x-s.x)*k,PadY,s.z+(from.z-s.z)*k); }
  return s;
 }
 static bool Leg(ref Vector3 man,Vector3 to,ref float t,int pit){
  // never through a berm: the straight leg may not cross another pit's wall
  for(int q=0;q<4;q++){
   if(q==pit) continue;
   Vector3 c=Pit(q); float ax=man.x-c.x,az=man.z-c.z,bx=to.x-c.x,bz=to.z-c.z,dx=bx-ax,dz=bz-az;
   float len=dx*dx+dz*dz, u=len<1e-6f?0f:Math.Max(0f,Math.Min(1f,-(ax*dx+az*dz)/len));
   float px=ax+dx*u,pz=az+dz*u; if(px*px+pz*pz<FlakPositionsCore.Outer*K*FlakPositionsCore.Outer*K) return false;
  }
  t+=Flat(man,to)/Run; man=to; return true;
 }

 // One merc: tower -> foot -> gate -> stop; then the production post step.
 // rule 0: H M3 (AtPost / Rescue / LeaseNear); rule 1: 6.71 (4 u 3D ask, 6 u master).
 static float Crew(Vector3 start,int slot,int pit,float heading,float carriage,float stopping,bool climbOk,int rule,out string why){
  why="";
  Vector3 seat=Seat(pit,heading);
  Vector3 man=start; bool down;
  float t=Tower(ref man,seat,slot,climbOk,out down);
  if(!down){ why="never reached the stair foot"; return -1f; }
  Vector3 w=World(man); w.y=0f;
  float gate=FlakPositionsCore.Heading(pit);
  Vector3 c=Pit(pit), g=Dir(gate);
  Vector3 outside=V(c.x+g.x*(FlakPositionsCore.Outer*K+2f),0f,c.z+g.z*(FlakPositionsCore.Outer*K+2f));
  Vector3 inside=V(c.x+g.x*(FlakPositionsCore.Inner*K-1f),PadY,c.z+g.z*(FlakPositionsCore.Inner*K-1f));
  if(!Leg(ref w,outside,ref t,pit)){ why="the way to the gate crosses another pit"; return -1f; }
  Leg(ref w,inside,ref t,pit);
  Vector3 stop=Stop(pit,seat,inside,carriage,stopping);
  Leg(ref w,stop,ref t,pit);
  // At the stop the agent does not get closer: the owner's step at 10 Hz.
  MercPostTrack tr=new MercPostTrack(); tr.Reset(pit,null,t-1f);
  for(float s=0f;s<60f;s+=0.1f){
   float now=t+s, flat=Flat(w,seat), rise=w.y-seat.y, d3=D3(w,seat);
   bool ask;
   if(rule==1) ask=d3<=4f;
   else {
    MercDefenceCore.Note(tr,flat,rise,now);
    ask=MercDefenceCore.AtPost(flat,rise)||MercDefenceCore.Rescue(tr,now,Flat(w,c),false)==MercDefenceCore.RescueWalkIn;
   }
   if(!ask) continue;
   bool lease=rule==1? d3<=6f : MercDefenceCore.LeaseNear(d3*d3,Flat(w,c),rise);
   if(lease) return now+0.5f;                 // the next 0.5 s request is granted
  }
  why="stood "+Flat(w,seat).ToString("0.0")+" u flat ("+D3(w,seat).ToString("0.0")+" u 3D) from the seat, never seated";
  return -1f;
 }

 static void Main(){
  TowerRoofCore.Init();
  // starts up on the tower (tower frame, metres)
  List<Vector3> starts=new List<Vector3>(); List<string> names=new List<string>(); List<int> slots=new List<int>();
  for(int s=0;s<TowerRoofCore.Posts;s++){ starts.Add(TowerRoofCore.Post(s)); names.Add("roof post "+s); slots.Add(s); }
  for(int s=0;s<2;s++){ starts.Add(TowerRoofCore.LadderPost(s)); names.Add("catwalk ladder post "+s); slots.Add(s); }
  starts.Add(TowerRoofCore.Exit()); names.Add("stair top"); slots.Add(0);
  starts.Add(TowerRoofCore.Inner[0]); names.Add("cab door"); slots.Add(1);
  starts.Add(TowerRoofCore.Seat()); names.Add("console seat"); slots.Add(2);
  for(int i=0;i<starts.Count;i++) Ok(TowerRoofCore.OnRoof(starts[i]),names[i]+" counts as up on the tower");
  Ok(FlakPositionsCore.GunIndex.Length==4&&FlakPositionsCore.GunIndex[3]==5,"real layout: three 52-K and the ZU-23 (gun 5)");
  float[] yaws={0f,33f,90f,180f,270f}; float[] carriages={0f,3f,5.5f}; float[] stops={0f,2.8f,5.6f};
  int runs=0, seated=0, oldStand=0, oldRuns=0; float worst=0f; string worstWhat="";
  foreach(float y in yaws){ Yaw=y;
   for(int i=0;i<starts.Count;i++) for(int p=0;p<4;p++) for(int h=0;h<360;h+=30) foreach(float cr in carriages) foreach(float st in stops) {
    string tag="yaw "+y+" "+names[i]+" -> "+FlakPositionsCore.Name[p]+" heading "+h+" carriage "+cr+" stop "+st;
    string why; runs++;
    float done=Crew(starts[i],slots[i],p,h,cr,st,true,0,out why);
    Ok(done>0f&&done<=120f,tag+": seated within 120 s ("+(done>0f?done.ToString("0.0")+" s":why)+")");
    if(done>0f){ seated++; if(done>worst){worst=done;worstWhat=tag;} }
    if(i==0){ oldRuns++; if(Crew(starts[i],slots[i],p,h,cr,st,true,1,out why)<0f) oldStand++; }
   }
   // the climb refused: the watchdog takes him down
   for(int p=0;p<4;p++){ string why; float done=Crew(starts[0],0,p,0f,3f,2.8f,false,0,out why);
    Ok(done>0f&&done<=120f,"yaw "+y+": climb refused, watchdog -> "+FlakPositionsCore.Name[p]+" seated ("+(done>0f?done.ToString("0.0")+" s":why)+")"); }
  }
  Ok(Falls==0,"no stair walk fell back to the 40 s limit ("+Falls+")");
  Ok(oldStand>0,"negative control: the 6.71 4 u rule leaves men standing at the gun ("+oldStand+" of "+oldRuns+")");
  Console.WriteLine("H M3 routes: "+seated+"/"+runs+" seated, worst "+worst.ToString("0.0")+" s ("+worstWhat+"), stair hops "+Hops+"; 6.71 rule: "+oldStand+"/"+oldRuns+" never seated");

  // Kevin's squad: six mercs up on the roof, the four real posts. Nearest
  // pair first (MercAirfield.DefenceReconcile), two extras keep FOLLOW.
  foreach(float y in yaws){ Yaw=y;
   Vector3[] men={TowerRoofCore.Post(0),TowerRoofCore.Post(1),TowerRoofCore.Post(2),TowerRoofCore.Post(3),TowerRoofCore.Post(4),TowerRoofCore.LadderPost(0)};
   int[] slot={0,1,2,3,4,0}; int[] post=new int[6]; for(int i=0;i<6;i++) post[i]=-1;
   bool[] taken=new bool[4];
   for(;;){ int bm=-1,bp=-1; float bd=float.MaxValue;
    for(int i=0;i<6;i++){ if(post[i]>=0) continue; for(int p=0;p<4;p++){ if(taken[p]) continue;
     float d=Flat(World(men[i]),Seat(p,0f)); if(d<bd){bd=d;bm=i;bp=p;} } }
    if(bm<0) break; post[bm]=bp; taken[bp]=true; }
   int crewed=0, extras=0;
   for(int i=0;i<6;i++){
    if(post[i]<0){ extras++; continue; }
    string why; float done=Crew(men[i],slot[i],post[i],0f,3f,2.8f,true,0,out why);
    if(done>0f&&done<=120f) crewed++; else Console.WriteLine("SQUAD yaw "+y+" merc "+i+": "+why);
   }
   Ok(crewed==4&&extras==2,"yaw "+y+": six mercs from the roof - four posts seated ("+crewed+"), two extras keep their order ("+extras+")");
  }
  Console.WriteLine("H M3 simulation: "+Pass+" PASS, "+Fail+" FAIL");
  if(Fail>0) Environment.Exit(1);
 }
}
// CORE
}
'''


def body_of(text, sig):
    i = text.index(sig)
    j = text.index("{", i)
    depth = 0
    for k in range(j, len(text)):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                return text[i:k + 1]
    raise ValueError(sig)


def simulate():
    body = (core("Revival.TowerRoofCore.cs", "    internal static class TowerRoofCore") + "\n"
            + core("Revival.FlakPositionsCore.cs", "    internal static class FlakPositionsCore") + "\n"
            + core("Revival.MercDefenceCore.cs", "    /// <summary>One merc's progress"))
    src = HARNESS.replace("// CORE", body)
    out = ROOT / ".agent-runtime" / "h-m3-defence-route"
    out.mkdir(parents=True, exist_ok=True)
    cs, exe = out / "check.cs", out / "check.exe"
    cs.write_text(src, encoding="utf-8", newline="\n")
    csc = Path("C:/Windows/Microsoft.NET/Framework64/v3.5/csc.exe")
    c = subprocess.run([str(csc), "/nologo", "/codepage:65001", "/out:" + str(exe), str(cs)],
                       capture_output=True, text=True)
    if c.returncode:
        ok(False, "C# 3.0 compile: " + (c.stdout + c.stderr)[-1200:])
        return
    r = subprocess.run([str(exe)], capture_output=True, text=True)
    for line in r.stdout.splitlines():
        if line.startswith("FAIL") or line.startswith("H M3") or line.startswith("SQUAD"):
            print(line)
    ok(r.returncode == 0, "C# simulation (roof to seat, real pits)")


def seams():
    aa = (ROOT / "Revival.MercAA.cs").read_text(encoding="utf-8")
    step = body_of(aa, "static void MercPostStep(")
    ok("MercDefenceCore.AtPost(flat, rise)" in step and "MercDefenceCore.Rescue(" in step,
       "MercPostStep asks for the seat by AtPost and the stall rescue")
    ok("TowerRoof.Descend(f.Tr, Agent(f))" in step and "MercAA.PitFlat(post, me)" in step,
       "MercPostStep: tower stall descends, pit walk-in by the pit's flat distance")
    ok("sqrMagnitude > 16f" not in step, "the 6.71 4 u 3D arrival rule is gone")
    packet = body_of(aa, "internal static void OnPacket(")
    ok("MercDefenceCore.LeaseNear(" in packet and "> 36f" not in packet, "master lease gate is MercDefenceCore.LeaseNear")
    ok("internal static string LeaseWhy(" in aa, "MercAA.LeaseWhy names the refusing lease rule")
    roof = (ROOT / "Revival.TowerRoof.cs").read_text(encoding="utf-8")
    ok("return TowerRoofCore.GoalUp(lg) ? LegClimbUp : LegClimbDown;" in roof,
       "TowerRoof.Leg turns a near LegWalk into the climb (as simulated)")
    ok("internal static bool Descend(" in roof and "internal static bool ManUp(" in roof, "TowerRoof.Descend/ManUp")
    war = (ROOT / "Revival.MercsWar.cs").read_text(encoding="utf-8")
    move = body_of(war, "static void MercMove(")
    ok("TowerRoof.Leg(f.Tr, goal, u.Slot, out leg)" in move and "TowerRoof.StartClimb(" in move,
       "MercMove takes the tower leg first (station orders included)")
    air = (ROOT / "Revival.MercAirfield.cs").read_text(encoding="utf-8")
    toggle = body_of(air, "internal static void ToggleAirDefence(")
    ok("Give(DefenceOne, MercOrder.FollowMe())" not in toggle and "MercDefenceCore.KeepsOrder(" in toggle,
       "ToggleAirDefence: extras keep their order")
    ok("DefenceLogOrder();" in toggle and "DefenceLogTick(now, moved);" in body_of(air, "static void AirDefenceTick("),
       "the order and the 2 Hz tick write the MercAD lines")
    log = (ROOT / "Revival.MercDefenceLog.cs").read_text(encoding="utf-8")
    for needle in ['"MercAD: "', "NavMesh.CalculatePath(", "MercDefenceCore.LogDue(t, now)", "seat TAKEN",
                   "seat NOT taken", "(OVERRIDES the post)", "DefencePostState(", "DefenceWhyNone("]:
        ok(needle in log, "MercAD log: " + needle)
    tick = body_of(log, "static void DefenceLogTick(")
    ok("new " not in tick.replace("new StringBuilder", ""), "MercAD tick: no allocation outside a written line")
    flak = (ROOT / "Revival.Flak.cs").read_text(encoding="utf-8")
    ok("Mercs.AirDefenceWants(g.Index)" in body_of(flak, "internal static void Spawn(Flak.Gun g)"),
       "no native crew spawns onto a gun a defence crewman walks to")
    sync = (ROOT / "sync_public.py").read_text(encoding="utf-8")
    ok('"Revival.MercDefenceCore.cs"' in sync and '"Revival.MercDefenceLog.cs"' in sync, "sync_public ships both new files")


def main():
    simulate()
    seams()
    print("%d passed, %d failed" % (PASSES[0], len(FAILS)))
    print("RESULT: " + ("PASS" if not FAILS else "FAIL"))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())

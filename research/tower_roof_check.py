"""Z T1a tower stairs: compile real C# 3.0 routing, simulate both directions,
all follow slots and invalid goals, benchmark eight actors, and validate every
waypoint/path sample against ALL shipped colliders within 50 m plus runtime
geometry. tower_stairs_check.py owns the independent world geometry proof.
No game, installation, Unity licence or bundle rebuild required.
"""
from pathlib import Path
import json
import math
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
CORE = (ROOT / "Revival.TowerRoofCore.cs").read_text(encoding="utf-8")
RUNTIME = (ROOT / "Revival.TowerRoof.cs").read_text(encoding="utf-8")
K = 2.8
FAILS = []
PASSES = [0]


def ok(cond, what):
    if cond:
        PASSES[0] += 1
    else:
        FAILS.append(what)
        print("FAIL " + what)


# ---------------------------------------------------------------- 1 harness

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
 }
}
namespace NextDayRevival {
using UnityEngine;
static class Sim {
 static int Pass, Fail;
 static void Ok(bool c,string what){ if(c) Pass++; else { Fail++; Console.WriteLine("FAIL "+what); } }
 static float Flat(Vector3 a,Vector3 b){float dx=a.x-b.x,dz=a.z-b.z;return (float)Math.Sqrt(dx*dx+dz*dz);}
 static string V(Vector3 v){return v.x.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" "+v.y.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" "+v.z.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture);}

 // One merc driven by the production Leg/Path/At: walks at 4 m/s (flat) to
 // each leg, climbs the path, stops at the first Hold / None.
 static List<int> Run(ref Vector3 man, Vector3 goal, int slot, float footY, out float seconds, bool dump, string tag){
  List<int> seq=new List<int>();
  Vector3[] pts=new Vector3[TowerRoofCore.PathMax];
  seconds=0f;
  for(int step=0;step<4000;step++){
   Vector3 leg;
   int r=TowerRoofCore.Leg(man,goal,slot,TowerRoofCore.Foot(footY),TowerRoofCore.Exit(),out leg);
   if(seq.Count==0||seq[seq.Count-1]!=r) seq.Add(r);
   if(r==TowerRoofCore.LegNone||r==TowerRoofCore.LegHold) return seq;
   if(r==TowerRoofCore.LegWalk){
    float d=Flat(man,leg);
    float s=Math.Min(d,0.4f);
    if(d>1e-4f){ man.x+=(leg.x-man.x)/d*s; man.z+=(leg.z-man.z)/d*s; }
    if(d<=0.4f) man.y=leg.y;          // arrived on the NavMesh spot
    seconds+=0.1f;
    continue;
   }
   int n=TowerRoofCore.Path(r==TowerRoofCore.LegClimbUp,man,TowerRoofCore.Foot(footY),TowerRoofCore.Exit(),pts);
   float dur=TowerRoofCore.Duration(pts,n);
   if(dump){
    System.Text.StringBuilder sb=new System.Text.StringBuilder();
    for(float t=0f;t<=dur+1e-4f;t+=0.02f) sb.Append(V(TowerRoofCore.At(pts,n,t))).Append(';');
    sb.Append(V(TowerRoofCore.At(pts,n,dur)));
    Console.WriteLine("PATH "+tag+" "+sb.ToString());
    Console.WriteLine("CLIMB "+tag+" "+dur.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));
   }
   // monotonic time, continuous path, ends where it should
   Vector3 prev=TowerRoofCore.At(pts,n,0f);
   float maxJump=0f;
   for(float t=0.02f;t<=dur;t+=0.02f){ Vector3 p=TowerRoofCore.At(pts,n,t); float j=(float)Math.Sqrt((p.x-prev.x)*(p.x-prev.x)+(p.y-prev.y)*(p.y-prev.y)+(p.z-prev.z)*(p.z-prev.z)); if(j>maxJump) maxJump=j; prev=p; }
   Ok(maxJump<0.2f,tag+": climb path continuous (max step "+maxJump+" m in 20 ms)");
   man=TowerRoofCore.At(pts,n,dur+1f);
   seconds+=dur;
  }
  Ok(false,tag+": did not settle");
  return seq;
 }

 static string S(List<int> q){string s="";for(int i=0;i<q.Count;i++) s+=(i>0?",":"")+new string[]{"None","Walk","Hold","ClimbUp","ClimbDown"}[q[i]];return s;}

 static void Main(){
  float foot=0f;
  Vector3 roofGoal=new Vector3(8f,TowerRoofCore.RoofY,0f);
  // Pure routing hot-loop: eight simultaneous mercs, two At() calls each.
  Vector3[] bench=new Vector3[TowerRoofCore.PathMax];
  int bn=TowerRoofCore.Path(true,TowerRoofCore.Foot(foot),TowerRoofCore.Foot(foot),TowerRoofCore.Exit(),bench);
  float bd=TowerRoofCore.Duration(bench,bn), checksum=0f;
  System.Diagnostics.Stopwatch watch=System.Diagnostics.Stopwatch.StartNew();
  for(int frame=0;frame<100000;frame++) for(int merc=0;merc<8;merc++) {
   float bt=bd*((frame+merc)%1000)/1000f;
   checksum+=TowerRoofCore.At(bench,bn,bt).x+TowerRoofCore.At(bench,bn,bt+0.1f).z;
  }
  watch.Stop();
  Console.WriteLine("BENCH eight mercs / two At calls: "+(watch.Elapsed.TotalMilliseconds/100000).ToString("0.0000",System.Globalization.CultureInfo.InvariantCulture)+" ms/frame; checksum "+checksum);
  // 1. from the apron (40 m east, 20 m south) onto the roof
  Vector3 man=new Vector3(40f,0f,-20f); float t;
  List<int> q=Run(ref man,roofGoal,0,foot,out t,true,"up");
  Console.WriteLine("SEQ up "+S(q)+" in "+t.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" s");
  Ok(S(q)=="Walk,ClimbUp,Walk,Hold","up: walk to the foot, climb, walk to the post, hold");
  Ok(TowerRoofCore.OnRoof(man),"up: he ends on the roof");
  Ok(Flat(man,TowerRoofCore.Post(0))<=TowerRoofCore.PostArrive,"up: he ends at his post");
  Ok(t<90f,"up: full walking stair route under 90 s ("+t+")");
  // 2. down to a point on the airfield
  Vector3 ground=new Vector3(30f,0f,12f);
  q=Run(ref man,ground,0,foot,out t,true,"down");
  Console.WriteLine("SEQ down "+S(q)+" in "+t.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" s");
  Ok(S(q)=="Walk,ClimbDown,None","down: walk to the top, climb down, the normal move again");
  Ok(Math.Abs(man.y-foot)<0.01f && !TowerRoofCore.OnRoof(man),"down: he stands on the ground");
  Ok(Flat(man,TowerRoofCore.Foot(foot))<0.01f,"down: at the ladder's foot");
  // 3. every merc slot of an owner standing on the roof (MercHalt.Slot, u -> m)
  Vector3 owner=new Vector3(8f,TowerRoofCore.RoofY,0f);
  float[][] fw={new float[]{1,0},new float[]{0,1},new float[]{-1,0},new float[]{0,-1},new float[]{0.7071f,0.7071f}};
  List<string> posts=new List<string>();
  for(int f=0;f<fw.Length;f++) for(int slot=0;slot<5;slot++){
   float fx=fw[f][0],fz=fw[f][1];
   int rank=slot/2;
   float lat=slot>=4?0f:(slot%2==0?-1f:1f)*(7f+5f*rank);
   Vector3 g=new Vector3(owner.x+(-fx*(12f+8f*rank)+fz*lat)/2.8f,owner.y,owner.z+(-fz*(12f+8f*rank)-fx*lat)/2.8f);
   Ok(TowerRoofCore.GoalUp(g),"follow slot "+slot+" facing "+f+" counts as the roof");
   Vector3 m2=new Vector3(-20f,0f,30f);
   List<int> q2=Run(ref m2,g,slot,foot,out t,false,"slot"+slot);
   Ok(S(q2)=="Walk,ClimbUp,Walk,Hold","follow slot "+slot+" facing "+f+": onto the roof ("+S(q2)+")");
   if(f==0) posts.Add(V(m2));
  }
  for(int i=0;i<posts.Count;i++) for(int j=i+1;j<posts.Count;j++) Ok(posts[i]!=posts[j],"slots "+i+"/"+j+" hold different posts");
  // 4. goals that must not start a climb
  Vector3 dummy; Vector3 F=TowerRoofCore.Foot(foot), E=TowerRoofCore.Exit();
  Ok(TowerRoofCore.Leg(new Vector3(30f,0f,0f),new Vector3(8f,0f,0f),0,F,E,out dummy)==TowerRoofCore.LegNone,"a goal on the ground floor under the cab: no climb");
  Ok(TowerRoofCore.Leg(new Vector3(30f,0f,0f),new Vector3(0f,9f,0f),0,F,E,out dummy)==TowerRoofCore.LegNone,"a goal on the main roof (9 m): no climb");
  Ok(TowerRoofCore.Leg(new Vector3(30f,0f,0f),new Vector3(8f,40f,0f),0,F,E,out dummy)==TowerRoofCore.LegNone,"a goal in the air over the tower (a helicopter): no climb");
  Ok(TowerRoofCore.Leg(new Vector3(30f,0f,0f),new Vector3(40f,TowerRoofCore.RoofY,0f),0,F,E,out dummy)==TowerRoofCore.LegNone,"roof height 28 m away from the roof: no climb");
  Ok(TowerRoofCore.Leg(new Vector3(8f,0f,0f),roofGoal,0,F,E,out dummy)==TowerRoofCore.LegWalk,"under the roof inside: walks to the foot first");
  Ok(TowerRoofCore.Split(new Vector3(8f,0f,0f),roofGoal),"under the roof: Split (a flat arrival check must fail)");
  Ok(!TowerRoofCore.Split(TowerRoofCore.Post(1),roofGoal),"on the roof with a roof goal: no Split");
  Ok(TowerRoofCore.Leg(new Vector3(13.4f,5f,-2.8f),roofGoal,0,F,E,out dummy)!=TowerRoofCore.LegClimbUp,"at the foot's x/z but 5 m up: no climb start");
  Ok(Flat(TowerRoofCore.Post(-1),TowerRoofCore.Post(4))<1e-4f && Math.Abs(Flat(TowerRoofCore.Post(7),TowerRoofCore.Post(2))-.65f)<1e-4f,"overflow slots share walls with separate body spots");
  // 5. a merc standing on the roof, goal elsewhere on the roof: to his post only
  Vector3 m3=TowerRoofCore.Exit();
  List<int> q3=Run(ref m3,roofGoal,3,foot,out t,false,"roofwalk");
  Ok(S(q3)=="Walk,Hold","on the roof to a roof goal: walk to the post, no climb");
  for(int i=0;i<TowerRoofCore.Posts;i++) Console.WriteLine("POST "+i+" "+V(TowerRoofCore.Post(i))+" face "+V(TowerRoofCore.Face(i)));
  // Z T1b: all six exact endpoints enter the ALL-area collision/support proof.
  string endpoints="";
  for(int i=0;i<6;i++)endpoints+=V(TowerRoofCore.Post(i))+";";
  Console.WriteLine("PATH posts "+endpoints);
  Console.WriteLine("Tower roof merc simulation: "+Pass+" PASS, "+Fail+" FAIL");
  if(Fail>0) Environment.Exit(1);
 }
}
// CORE
}
'''


def harness():
    body = CORE[CORE.index("    internal static class TowerRoofCore"):]
    body = body[:body.rindex("}")]           # drop the namespace's closing brace
    src = HARNESS.replace("// CORE", body)
    out = ROOT / ".agent-runtime" / "tower-roof-check"
    out.mkdir(parents=True, exist_ok=True)
    cs, exe = out / "check.cs", out / "check.exe"
    cs.write_text(src, encoding="utf-8", newline="\n")
    csc = Path("C:/Windows/Microsoft.NET/Framework64/v3.5/csc.exe")
    c = subprocess.run([str(csc), "/nologo", "/codepage:65001", "/out:" + str(exe), str(cs)], capture_output=True, text=True)
    if c.returncode:
        raise RuntimeError(c.stdout + c.stderr)
    r = subprocess.run([str(exe)], capture_output=True, text=True)
    paths, climbs = {}, {}
    for line in r.stdout.splitlines():
        if line.startswith("PATH "):
            _, tag, pts = line.split(" ", 2)
            paths[tag] = [tuple(float(v) for v in p.split()) for p in pts.split(";") if p.strip()]
        elif line.startswith("CLIMB "):
            _, tag, d = line.split()
            climbs[tag] = float(d)
            print("climb %-4s %5.2f s" % (tag, float(d)))
        else:
            print(line)
    ok(r.returncode == 0, "C# merc simulation")
    return paths, climbs


# ------------------------------------------------------------- 2 geometry

def const(name):
    m = re.search(r"\b" + name + r"\s*=\s*([-0-9.]+)f", CORE)
    if m:
        return float(m.group(1))
    m = re.search(r"\b" + name + r"\s*=\s*RoofY\s*\+\s*([-0-9.]+)f", CORE)
    return const("RoofY") + float(m.group(1))


def arr(name):
    m = re.search(r"float\[\] " + name + r" = \{([^}]*)\}", CORE)
    return [float(v.strip().rstrip("f")) for v in m.group(1).split(",")]


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    # v' = v + 2w(q x v) + 2 q x (q x v)
    cx, cy, cz = y * vz - z * vy, z * vx - x * vz, x * vy - y * vx
    cx2, cy2, cz2 = y * cz - z * cy, z * cx - x * cz, x * cy - y * cx
    return (vx + 2 * (w * cx + cx2), vy + 2 * (w * cy + cy2), vz + 2 * (w * cz + cz2))


def boxes():
    d = json.loads((ROOT / "assets/airfield/c1/c1_colliders.json").read_text(encoding="utf-8"))
    out = []
    for b in d["boxes"]:
        c = [v / K for v in b["center"]]
        h = [v / K / 2 for v in b["size"]]
        q = b["rotation_xyzw"]
        lo, hi = [1e9] * 3, [-1e9] * 3
        for sx in (-1, 1):
            for sy in (-1, 1):
                for sz in (-1, 1):
                    p = qrot(q, (sx * h[0], sy * h[1], sz * h[2]))
                    for i in range(3):
                        lo[i] = min(lo[i], c[i] + p[i])
                        hi[i] = max(hi[i], c[i] + p[i])
        out.append((b["name"], b["kind"], lo, hi))
    return out


def overlap(alo, ahi, blo, bhi, eps=1e-4):
    return all(alo[i] < bhi[i] - eps and ahi[i] > blo[i] + eps for i in range(3))


def hits(lo, hi, bs, skip=()):
    return [b[0] for b in bs if b[1] not in skip and overlap(lo, hi, b[2], b[3])]


def box_dist(p, lo, hi):
    return math.sqrt(sum(max(lo[i] - p[i], 0, p[i] - hi[i]) ** 2 for i in range(3)))


def geometry(paths):
    from tower_stairs_check import geometry as stair_geometry
    stair_geometry(paths)
    ok(True, "ALL area colliders and full stair sweep checked")


def wiring():
    plugin = (ROOT / "RevivalPlugin.cs").read_text(encoding="utf-8")
    prof = (ROOT / "RevivalFrameProfiler.cs").read_text(encoding="utf-8")
    war = (ROOT / "Revival.MercsWar.cs").read_text(encoding="utf-8")
    combat = (ROOT / "Revival.NpcCombat.cs").read_text(encoding="utf-8")
    fight = (ROOT / "Revival.MercFight.cs").read_text(encoding="utf-8")
    ladders = (ROOT / "Revival.EastLadders.cs").read_text(encoding="utf-8")
    sync = (ROOT / "sync_public.py").read_text(encoding="utf-8")
    ok("TowerRoof.BindConfig(Config);" in plugin, "config bound")
    ok('cfg.Bind("TowerRadar", "RoofLadder", true' in RUNTIME, "[TowerRadar] RoofLadder default ON")
    ok("FrameProf.S(FrameProf.S_TowerRoofT); TowerRoof.Tick(); FrameProf.E(FrameProf.S_TowerRoofT);" in plugin, "Tick in F6 slot TowerRoof.Tick")
    ok("FrameProf.S(FrameProf.S_TowerRoofL); TowerRoof.LateFrame(); FrameProf.E(FrameProf.S_TowerRoofL);" in plugin, "LateFrame in F6 slot TowerRoof.LateFrame")
    names = re.search(r"string\[\] Names = new string\[\]\s*\{(.*?)\};", prof, re.S).group(1)
    listed = re.findall(r'"([^"]+)"', names)
    count = int(re.search(r"const int Count = (\d+)", prof).group(1))
    ok(len(listed) == count, "F6 names %d = Count %d" % (len(listed), count))
    for slot, name in (("S_TowerRoofT", "TowerRoof.Tick"), ("S_TowerRoofL", "TowerRoof.LateFrame")):
        idx = int(re.search(slot + r" = (\d+);", prof).group(1))
        ok(listed[idx] == name, "F6 slot %s = \"%s\"" % (slot, name))
    ok("GameLadder(" not in RUNTIME and "LadderMesh(" not in RUNTIME
       and "EastLadders.Wire(" not in RUNTIME, "B1 ladder completely removed")
    ok("OverlapCapsuleNonAlloc" in RUNTIME and "_hits, ~0" in RUNTIME
       and "ValidateNext();" in RUNTIME and "!_routeReady" in RUNTIME,
       "ALL-layer fail-closed diagnostic and active queries, no model whitelist")
    ok("int roof = TowerRoof.Leg(f.Tr, goal, u.Slot, out leg);" in war and "TowerRoof.StartClimb(f.Tr, Agent(f), roof == TowerRoof.LegClimbUp, u.Slot)" in war,
       "NpcWar.MercMove routes through TowerRoof.Leg")
    ok("if (roof == TowerRoof.LegWalk) dest = goal;" in war, "roof legs skip the ground projection")
    ok("TowerRoof.Split(f.Tr.position, u.Goal)" in war and "TowerRoof.GoalUp(o.Centre) ? o.Centre : Beside(o.Centre, o.K)" in war,
       "STAY: roof goal kept, no flat arrival from below")
    ok("!roof && MercHaltCover(" in war and "(marksman ? 2f : 7f) && !roof)" in war, "FOLLOW: no flat arrival from below, roof posts instead of halt cover")
    ok("if (s.Merc != null && TowerRoof.Climbing(f.Tr)) continue;" in combat, "RunGround leaves a climbing merc to the climb")
    ok("TowerRoof.KeepUp(f.Tr.position, goal, u.Slot, out post)" in fight, "a fight on the roof stays on the roof")
    for f in ("Revival.TowerRoof.cs", "Revival.TowerRoofCore.cs", "research/tower_roof_check.py"):
        ok('"%s"' % f in sync, "sync_public ships " + f)
    for src, label in ((RUNTIME, "TowerRoof.cs"), (CORE, "TowerRoofCore.cs")):
        ok("System.Linq" not in src and "FindObjectsOfType" not in src , label + ": no LINQ / scene scans")
        raw = src.encode("utf-8")
        ok(all(b < 128 for b in raw) and not raw.startswith(b"\xef\xbb\xbf") and b"\r" not in raw, label + ": ASCII, LF, no BOM")
    late = RUNTIME[RUNTIME.index("internal static void LateFrame()"):RUNTIME.index("// ------------------------------------------------------ merc routing")]
    ok("if (_active == 0) return;" in late and "new " not in late.replace("new Vector3", ""), "LateFrame: nothing when nobody climbs, no allocation")
    leg = RUNTIME[RUNTIME.index("internal static int Leg("):RUNTIME.index("static int Wrap(")]
    ok("new " not in leg and "Near * Near" in leg, "Leg: early flat-distance exit, no allocation")
    tick = RUNTIME[RUNTIME.index("internal static void Tick()"):RUNTIME.index("internal static void LateFrame()")]
    ok("_next = now + 1f;" in tick, "Tick throttled to 1 Hz")


def main():
    paths, climbs = harness()
    geometry(paths)
    wiring()
    print("Tower roof check: %d PASS, %d FAIL" % (PASSES[0], len(FAILS)))
    if FAILS:
        sys.exit(1)


if __name__ == "__main__":
    main()

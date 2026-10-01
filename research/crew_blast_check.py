"""D1b: execute real fatal-blast hooks and time-sliced owner damage offline.

Native-contract fakes replace Unity/Photon; the production queue and handoff
are compiled unchanged by the .NET 3.5 compiler. All output stays in build/.
"""
from pathlib import Path

import bomb_damage_check as bomb
import blast_position_check as position

ROOT = Path(__file__).resolve().parents[1]

CASES = r'''
        static GameObject Spawn(params float[] metres) {
            GameObject settlement=new GameObject();
            Component[] men=new Component[metres.Length];
            for(int i=0;i<men.Length;i++) {
                NPC_AI2 ai=new NPC_AI2();ai.Health=120;Place(ai,metres[i],i%2==0);
                ai.IsInitialized=false;men[i]=ai;
            }
            settlement.Men=men;return settlement;
        }
        static NPC_AI2 Man(GameObject s,int i) { return (NPC_AI2)s.Men.GetValue(i); }
        static VehicleGameSystem Hull(float metres,float durability) {
            VehicleGameSystem v=new VehicleGameSystem();Place(v,metres,true);
            v.Durability=durability;VehicleScan.Items=new Component[] {v};
            Seats(v,metres);return v;
        }
        static void Seats(VehicleGameSystem v,params float[] metres) {
            v.SeatPoints=new Transform();
            foreach(float m in metres) {
                Transform seat=new Transform();seat.position=new Vector3(m*2.8f,0,0);
                v.SeatPoints.Children.Add(seat);
            }
        }
        static void StartMen(GameObject s) {
            foreach(NPC_AI2 ai in s.Men) ai.IsInitialized=true;
            Time.frameCount++;Drain();
        }
        public static void Main() {
            System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
            OrdnanceBlast.Install(new Harmony());
            Assert(OrdnanceBlast.Ready && Harmony.VehiclePatched,"real hooks installed");
            // Registry NPCs are hit first; the hidden crew does not exist yet.
            NPC_AI2 onFoot=new NPC_AI2();Place(onFoot,0,false);NpcScan.Items=new Component[] {onFoot};
            VehicleGameSystem v=Hull(0,9000);
            Seats(v,-1.6f,0,1.6f);
            OrdnanceBlast.Enqueue(new Vector3(),25*2.8f,500,1200,0);Drain();
            Assert(v.Durability<=0 && onFoot.Calls==1,"bomb kills hull and existing NPC once");
            GameObject crew=Spawn(-1.6f,0,1.6f);
            v.transform.position=new Vector3(10000,0,0); // wreck moved before patrol observed death
            CrewBlast.Release(v.gameObject,crew);CrewBlast.Release(v.gameObject,crew);
            Drain();Assert(Man(crew,0).Calls==0,"wait for Unity Start");
            // Movement after creation cannot escape a historical impulse.
            foreach(NPC_AI2 ai in crew.Men) ai.transform.position=new Vector3(10000,0,0);
            StartMen(crew);
            foreach(NPC_AI2 ai in crew.Men) Assert(ai.Health<=0 && ai.Calls==1,"direct bomb wipes crew once");
            Assert(Man(crew,1).gameObject.View.Calls==1 &&
                Man(crew,1).gameObject.View.Recipient==Man(crew,1).gameObject.View.Owner,"remote owner RPC");
            Assert(onFoot.Calls==1,"no replay of existing victims");
            Console.WriteLine("PASS direct FAB-50: 3/3 crew dead, one hit each, delayed Start, frozen exposure, remote owner RPC, no replay");

            Reset();v=Hull(20,500);
            Seats(v,18.4f,20,21.6f);
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            // Simulate patrol detecting this wreck many frames later.
            Time.frameCount+=120;crew=Spawn(18.4f,20,21.6f);
            CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            Assert(Man(crew,0).Health<=0 && Man(crew,1).Health>0 && Man(crew,2).Health>0,"near partial deaths");
            Near(Man(crew,1).Health,20,"20 m falloff");Near(Man(crew,2).Health,52,"far exit falloff");
            Console.WriteLine("PASS near FAB-50 at 20 m: hull destroyed; 1 dead, 2 wounded (20/52 HP); delayed wreck detection retains profile");

            for(int mass=50;mass<=250;mass+=200) {
                Reset();float scale=(float)Math.Pow(mass/50f,1.0/3.0);
                v=Hull(20,500);OrdnanceBlast.Enqueue(new Vector3(),70*scale,500*scale,1200*scale,0);Drain();
                crew=Spawn(20);CrewBlast.Release(v.gameObject,crew);StartMen(crew);
                float expected=120-OrdnanceBlast.Damage(56,70*scale,500*scale);
                Near(Man(crew,0).Health,expected,"producer-scaled mass preserved");
                Assert((mass==50)==(Man(crew,0).Health>0),"larger bomb is lethal at same distance");
            }
            Console.WriteLine("PASS FAB-50/FAB-250: same 20 m exposure changes from wounded to dead using producer radius/peak scaling");

            Reset();v=Hull(0,100);v.SeatPoints=null;
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            float[] widelySpaced=new float[32];for(int i=0;i<32;i++) widelySpaced[i]=100+i;
            crew=Spawn(widelySpaced);CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            foreach(NPC_AI2 ai in crew.Men) Assert(ai.Health<=0 && ai.Calls==1,"no seats uses hull exposure, not dismount formation");
            Console.WriteLine("PASS missing-seat fallback and 32-man dismount: original hull exposure kills all despite distant formation");

            Reset();v=Hull(0,100000);
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            Assert(v.gameObject.GetComponent<CrewBlastImpact>()==null,"nonfatal explosion not stored");
            // An unrelated later firearm death must not inherit an old bomb.
            v.ApplyDamage(100000,4);crew=Spawn(0);CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            Assert(Man(crew,0).Calls==0,"firearm death has ordinary crew");
            Reset();v=Hull(0,100);v.ApplyDamage(100,14);crew=Spawn(0);
            CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            Assert(Man(crew,0).Calls==0,"context restored after bomb");
            Console.WriteLine("PASS nonfatal explosion, later firearm death, unrelated explosion: no stale blast inheritance");

            Reset();v=Hull(25,1);
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            Assert(v.Durability>0 && v.Calls==0,"radius edge is zero");
            Reset();v=Hull(0,100);OrdnanceBlast.Enqueue(new Vector3(),70,0,1200,0);Drain();
            Assert(v.gameObject.GetComponent<CrewBlastImpact>()==null,"zero person peak cannot poison crew");
            Console.WriteLine("PASS outside/edge exposure and zero-person-damage blasts: no crew damage");

            Reset();v=Hull(0,100);PhotonNetwork.Master=false;
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            Assert(v.Calls==0,"peer cannot damage vehicle or record fatal blast");PhotonNetwork.Master=true;
            OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();crew=Spawn(0);
            PhotonNetwork.Master=false;CrewBlast.Release(v.gameObject,crew);
            Assert(!v.gameObject.GetComponent<CrewBlastImpact>().Consumed,"peer cannot consume profile");
            PhotonNetwork.Master=true;CrewBlast.Release(v.gameObject,crew);
            PhotonNetwork.Master=false;StartMen(crew);
            Assert(Man(crew,0).Calls==0,"authority loss discards outstanding crew job");PhotonNetwork.Master=true;
            Console.WriteLine("PASS master-only capture/consume and cancellation on authority loss");

            Reset();v=Hull(0,100);OrdnanceBlast.Enqueue(new Vector3(),70,500,1200,0);Drain();
            v.Durability=100;crew=Spawn(0);CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            Assert(Man(crew,0).Calls==0,"repaired hull cannot release stale damage");
            OrdnanceBlast.Enqueue(new Vector3(56,0,0),70,500,1200,0);Drain();
            crew=Spawn(0);CrewBlast.Release(v.gameObject,crew);StartMen(crew);
            Near(Man(crew,0).Health,20,"new fatal blast replaces former wreck profile");
            int ticks=FrameProf.Ticks;Drain();Assert(ticks==FrameProf.Ticks,"idle fast return");
            Console.WriteLine("PASS repaired hull, new fatal impulse, idle fast return; existing F6 slot and traversal budget reused");
            Console.WriteLine("CREW BLAST CHECK PASS");
        }
'''


def main():
    harness = bomb.HARNESS
    stub = bomb.method(harness, 'internal static class CrewBlast')
    harness = harness.replace(stub, '')
    harness = harness.replace('namespace UnityEngine {', '''namespace UnityEngine {
    public class Object { public static void Destroy(Object o) { } }
    public class MonoBehaviour : Component { }
''')
    harness = harness.replace('public class Component {', 'public class Component : Object {')
    harness = harness.replace('public class Transform {', '''public class Transform {
        public System.Collections.Generic.List<Transform> Children=new System.Collections.Generic.List<Transform>();
        public int childCount { get { return Children.Count; } }
        public Transform GetChild(int i) { return Children[i]; }
''')
    harness = harness.replace('public class GameObject {', '''public class GameObject {
        public Array Men;
        System.Collections.Generic.Dictionary<Type,Component> Added=new System.Collections.Generic.Dictionary<Type,Component>();
        public T GetComponent<T>() where T:Component { Component c;return Added.TryGetValue(typeof(T),out c)?(T)c:null; }
        public T AddComponent<T>() where T:Component,new() { T c=new T();c.gameObject=this;Added[typeof(T)]=c;return c; }
''')
    patch = bomb.method(harness, 'public void Patch(')
    harness = harness.replace(patch, '''public static bool VehiclePatched;
        public void Patch(MethodInfo m,HarmonyMethod p,object post,object t,object f,object d) {
            if(m.DeclaringType.Name=="NPC_AI2")
                NextDayRevival.NPC_AI2.Prefix=(Action<object,int,int>)Delegate.CreateDelegate(typeof(Action<object,int,int>),p.Method);
            else if(m.DeclaringType.Name=="VehicleGameSystem") {
                if(p.Method.Name!="BeforeVehicle" || ((HarmonyMethod)post).Method.Name!="AfterVehicle")
                    throw new Exception("incorrect fatal transition hooks");
                VehiclePatched=true;
            } else throw new Exception("unexpected patch");
        }''')
    vehicle = bomb.method(harness, 'public class VehicleGameSystem')
    harness = harness.replace(vehicle, '''public class VehicleGameSystem : Component {
        public int Calls;
        public float Durability;
        public Transform SeatPoints;
        public void ApplyDamage(float damage,int part) {
            CrewBlast.Profile state;CrewBlast.BeforeVehicle(this,part,out state);
            Calls++;Durability-=damage*(part==14?9:1);
            CrewBlast.AfterVehicle(this,state);
        }
    }
    public static class Crew { public static Array Men(GameObject s) { return s.Men; } }''')
    harness = harness.replace(bomb.method(harness, 'public static void Main()'), CASES)
    mortar = (ROOT / 'RevivalMortar.cs').read_text(encoding='utf-8')
    guards = '\n'.join(bomb.method(mortar, sig) for sig in (
        'internal static void BreakKillStreak(', 'internal static bool Hurtable(', 'static bool Bool('))
    harness = harness.replace('/*GUARDS*/', guards)
    position.run('CrewBlast', harness, ['Revival.OrdnanceBlast.cs', 'Revival.CrewBlast.cs'])

    crew = (ROOT / 'Revival.Crew.cs').read_text(encoding='utf-8')
    # Locate the final overload, which owns creation (the preceding overload delegates).
    anchor = 'bool tank, string fraktion, List<RevivalComposition.CrewMan> composition, bool noFpv)'
    final = crew[crew.index(anchor):]
    assert final.index('settlement = Absetzen(') < final.index('CrewBlast.Release(car, settlement);') < final.index('NpcWar.StartGround(')
    queue = (ROOT / 'Revival.OrdnanceBlast.cs').read_text(encoding='utf-8')
    assert 'b.ReadyFrame = Time.frameCount + 1' in queue
    assert 'Stopwatch.Frequency / 12500L' in queue and 'visits < 64' in queue
    for forbidden in ('Physics.', 'FindObjectsOfType', 'GetComponents', 'void Update('):
        assert forbidden not in (ROOT / 'Revival.CrewBlast.cs').read_text(encoding='utf-8')
    print('PASS spawn seam, next-frame initialization, no physics/scene query/new tick, bounded traversal')


if __name__ == '__main__':
    main()

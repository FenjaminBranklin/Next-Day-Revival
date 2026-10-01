"""D1a: execute production blast/registry code with hidden, colliderless victims.

No Unity process, listening socket, Steam write or TEMP compiler output.
Fakes model native health-owner guards; live Photon/F6 remain human acceptance.
"""
from pathlib import Path
import re
import subprocess

import bomb_damage_check as bomb

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / 'build' / 'blast-position-check'
CSC = r'C:\Windows\Microsoft.NET\Framework64\v3.5\csc.exe'


def run(name, harness, sources):
    BUILD.mkdir(parents=True, exist_ok=True)
    cs, exe = BUILD / (name + '.cs'), BUILD / (name + '.exe')
    cs.write_text(harness, encoding='ascii', newline='\n')
    result = subprocess.run([CSC, '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe),
                             str(cs)] + [str(ROOT / s) for s in sources],
                            capture_output=True, text=True, errors='replace')
    assert result.returncode == 0, result.stdout + result.stderr
    result = subprocess.run([str(exe)], capture_output=True, text=True, errors='replace')
    print(result.stdout, end='')
    assert result.returncode == 0, result.stderr


EXTRA_TYPES = r'''
    public class ExplosionObject : Component {
        public float ExplosionDamage=240, ExplodeDamageRadius=28;
        public List<GameObject> damagedPlayers=new List<GameObject>();
        public void ExplosionPhysicsEffect() {
            NativeBlast.BeforePhysics(this);
            // Model the native collider branch: the transpiler must suppress
            // it even when the victims have enabled bones near this explosion.
            if (NativeBlast.PersonTag(this,"RagdollBone")) throw new Exception("native double hit");
        }
    }
    public class ConfigFloat { public float Value; public ConfigFloat(float v) { Value=v; } }
    public static class Gepard { public static ConfigFloat CfgInfantryDamage=new ConfigFloat(180); }
    public static class Flak { public static ConfigFloat CfgImpactDamage=new ConfigFloat(1200); }
    public static class GepardShots {
        public class Round { public bool Live; }
        public class Spec { public float Splash, InfantryDamage; public bool Exact; }
        /*PERSON_BURST*/
        public static void Burst(bool live,bool heavy,float radius) {
            PersonBurst(new Round { Live=live },new Spec { Exact=heavy,Splash=radius },new Vector3());
        }
    }
'''

CASES = r'''
            NativeBlast.Install(new HarmonyLib.Harmony());
            Assert(NativeBlast.Ready,"native hook installed");
            for(int height=100;height<=200;height+=100) {
                Reset();NPC_AI2 belowPlane=new NPC_AI2();Place(belowPlane,0,false);
                belowPlane.CollidersEnabled=false;belowPlane.RendererEnabled=false;belowPlane.AnimationEnabled=false;
                Vector3 belowImpact;float belowFall;
                Assert(An2Flight.Predict(new Vector3(0,height*2.8f,0),new Vector3(50,0,0),out belowImpact,out belowFall),"An2 100-200m prediction");
                belowPlane.transform.position=belowImpact;NpcScan.Items=new Component[] {belowPlane};
                OrdnanceBlast.Enqueue(belowImpact,25*2.8f,500,1200,0);Drain();
                Assert(belowPlane.Calls==1 && belowPlane.Health<=0,"An2 bomb kills hidden NPC below the 100-200m flight");
            }
            Console.WriteLine("PASS An-2 100 m and 200 m drops: production trajectory + registry impact kill with colliders/animation/rendering off");
            Reset();
            NPC_AI2 hidden=new NPC_AI2();Place(hidden,0,false);
            hidden.CollidersEnabled=false;hidden.RendererEnabled=false;hidden.AnimationEnabled=false;
            hidden.gameObject.activeInHierarchy=false;
            NPC_AI2 nativeFar=new NPC_AI2();Place(nativeFar,21,false);
            NPC_AI2 visibleBone=new NPC_AI2();Place(visibleBone,1,false);visibleBone.Health=10000;
            Player seated=new Player();Place(seated,5,false);seated.CollidersEnabled=false;
            seated.gameObject.activeInHierarchy=false;
            NpcScan.Items=new Component[] {hidden,nativeFar,visibleBone};
            NetworkGameServer.Instance.NetworkPlayers.Add(seated.gameObject);
            // Non-master native explosion authority, remote NPC owner and player.
            PhotonNetwork.Master=false;PhotonNetwork.ActorId=4;
            ExplosionObject explosion=new ExplosionObject();Place(explosion,0,true);
            explosion.ExplosionPhysicsEffect();Drain();
            explosion.ExplosionPhysicsEffect();Drain();
            Assert(hidden.Calls==1 && hidden.Health<=0,"colliderless hidden remote NPC killed once");
            Assert(visibleBone.Calls==1,"enabled-bone NPC hit once even on repeated native call");
            Assert(hidden.gameObject.View.Recipient==hidden.gameObject.View.Owner,"W owner RPC");
            Assert(seated.Calls==1 && seated.Health<=0,"colliderless seated player hit once");
            Assert(nativeFar.Calls==0,"native twice-radius outside ignored");
            Assert(!NativeBlast.PersonTag(hidden,"RagdollBone"),"enabled bones cannot double damage");
            Near(OrdnanceBlast.NativeModifier(28,28),1,"native full radius");
            Near(OrdnanceBlast.NativeModifier(42,28),0.5f,"native cosine shell");
            Near(OrdnanceBlast.NativeModifier(56,28),0,"native outer rim");
            Console.WriteLine("PASS native explosion: disabled colliders/animation/rendering, inactive NPC/player, non-master sender -> remote owners; no double hit; native falloff");
            Reset();
            NPC_AI2 zero=new NPC_AI2();Place(zero,0,false);NpcScan.Items=new Component[] {zero};
            explosion.ExplosionDamage=0;explosion.ExplosionPhysicsEffect();Drain();
            Assert(zero.Calls==0,"visual-only explosion must not queue damage");
            Console.WriteLine("PASS zero-damage bomb FX cannot duplicate registry damage");
            Reset();PhotonNetwork.Master=true;PhotonNetwork.ActorId=1;
            NPC_AI2 mortarNpc=new NPC_AI2();Place(mortarNpc,1,false);NpcScan.Items=new Component[] {mortarNpc};
            Player ally=new Player();Place(ally,1,false);
            Player enemy=new Player();Place(enemy,1,false);enemy.SameFaction=false;
            NetworkGameServer.Instance.NetworkPlayers.Add(ally.gameObject);
            NetworkGameServer.Instance.NetworkPlayers.Add(enemy.gameObject);
            OrdnanceBlast.EnqueueMortar(new Vector3(),true,28,450,700,260,false);Drain();
            Assert(mortarNpc.Calls==1 && mortarNpc.Health<=0,"mortar anonymous remote NPC owner hit");
            Assert(ally.Calls==0 && enemy.Calls==1,"mortar faction guard preserved");
            // Receiving master job does not repeat the shooter's player job.
            Reset();NpcScan.Items=new Component[] {mortarNpc};
            OrdnanceBlast.EnqueueMortar(new Vector3(),false,28,450,700,260,false);Drain();
            Assert(mortarNpc.Calls==1 && enemy.Calls==1,"dead victim and remote master job do not repeat damage");
            Console.WriteLine("PASS mortar: master NPC owner routing, shooter-only player damage, preserved faction guard");
            Reset();PhotonNetwork.Master=false;
            Player ownMortar=new Player();Place(ownMortar,1,true);ownMortar.CollidersEnabled=false;
            Player peerMortar=new Player();Place(peerMortar,1,false);
            NetworkGameServer.Instance.NetworkPlayers.Add(ownMortar.gameObject);
            NetworkGameServer.Instance.NetworkPlayers.Add(peerMortar.gameObject);
            OrdnanceBlast.EnqueueOwnPlayer(new Vector3(),28,260);Drain();
            Assert(ownMortar.Calls==1 && peerMortar.Calls==0,"NPC mortar event only damages receiving client's player");
            Console.WriteLine("PASS NPC mortar packet: colliderless own player from registry hit once; peers ignored");
            PhotonNetwork.Master=true;
            Reset();
            NPC_AI2 patrolNpc=new NPC_AI2();Place(patrolNpc,4,false);patrolNpc.CollidersEnabled=false;
            NpcScan.Items=new Component[] {patrolNpc};
            Player patrolAlly=new Player();Place(patrolAlly,1,false);
            Player patrolEnemy=new Player();Place(patrolEnemy,1,false);patrolEnemy.SameFaction=false;
            NetworkGameServer.Instance.NetworkPlayers.Add(patrolAlly.gameObject);
            NetworkGameServer.Instance.NetworkPlayers.Add(patrolEnemy.gameObject);
            OrdnanceBlast.EnqueuePatrolPeople(new Vector3(),"ally",null,240,28,5.6f,260,28);Drain();
            Near(patrolNpc.Health,-80,"patrol lethal-core to infantry rim profile preserved");
            Assert(patrolNpc.Calls==1 && patrolAlly.Calls==0 && patrolEnemy.Calls==1,"patrol owner RPC and enemy-only blast");
            Console.WriteLine("PASS patrol tank shell: position registry replaces visible targets, infantry core/rim preserved, owner RPC and faction filter");
            for(int profile=0;profile<3;profile++) {
                Reset();NPC_AI2 npc=new NPC_AI2();Place(npc,0,false);npc.CollidersEnabled=false;
                NpcScan.Items=new Component[] {npc};Player p=new Player();Place(p,0,false);p.CollidersEnabled=false;
                NetworkGameServer.Instance.NetworkPlayers.Add(p.gameObject);
                GepardShots.Burst(false,profile==0,profile==2?1.2f*2.8f:12*2.8f);Drain();
                Assert(npc.Calls==0 && p.Calls==0,"peer puff must never damage");
                GepardShots.Burst(true,profile==0,profile==2?1.2f*2.8f:12*2.8f);Drain();
                Assert(npc.Calls==1 && p.Calls==1,"52-K/Gepard/ZU23 person splash reaches disabled colliders once");
            }
            Console.WriteLine("PASS production AA burst helper: 52-K, Gepard, ZU23 colliderless NPC/player victims; peer pictures never damage");
'''


def damage_test():
    harness = bomb.HARNESS
    harness = harness.replace('using System.Reflection;', 'using System.Reflection;\nusing System.Reflection.Emit;\nusing System.Collections.Generic;')
    harness = harness.replace('public Transform transform=new Transform();',
                              'public bool activeInHierarchy=true; public Transform transform=new Transform();')
    harness = harness.replace('public class Component {', 'public class Component {\n        public bool CompareTag(string tag) { return true; }')
    harness = harness.replace('public class NPC_AI2 : Component {',
                              'public class NPC_AI2 : Component {\n        public bool CollidersEnabled=true,RendererEnabled=true,AnimationEnabled=true;')
    harness = harness.replace('public class Player : Component {',
                              'public class Player : Component { public bool CollidersEnabled=true;')
    harness = harness.replace('    public class Harmony {', '''    public class CodeInstruction {
        public OpCode opcode; public object operand;
        public List<int> labels=new List<int>();
        public CodeInstruction(OpCode op,object arg) { opcode=op;operand=arg; }
    }
    public class Harmony {''')
    old = 'NextDayRevival.NPC_AI2.Prefix=(Action<object,int,int>)Delegate.CreateDelegate(typeof(Action<object,int,int>),p.Method);'
    harness = harness.replace(old, '''if(m.DeclaringType==typeof(NextDayRevival.NPC_AI2)) {
                NextDayRevival.NPC_AI2.Prefix=(Action<object,int,int>)Delegate.CreateDelegate(typeof(Action<object,int,int>),p.Method);
            } else {
                MethodInfo compare=typeof(Component).GetMethod("CompareTag");
                CodeInstruction call=new CodeInstruction(OpCodes.Callvirt,compare);call.labels.Add(123);
                CodeInstruction vehicle=new CodeInstruction(OpCodes.Callvirt,compare);
                List<CodeInstruction> code=new List<CodeInstruction>(new CodeInstruction[] {
                    new CodeInstruction(OpCodes.Ldstr,"RagdollBone"),call,
                    new CodeInstruction(OpCodes.Ldstr,"Vehicle"),vehicle });
                List<CodeInstruction> patched=new List<CodeInstruction>(NextDayRevival.NativeBlast.ReplacePersonTag(code));
                if(patched[1]!=call || call.opcode!=OpCodes.Call || call.labels[0]!=123
                    || vehicle.opcode!=OpCodes.Callvirt || !Equals(vehicle.operand,compare))
                    throw new Exception("native transpiler altered vehicle branch or labels");
                bool failed=false;
                try { NextDayRevival.NativeBlast.ReplacePersonTag(new CodeInstruction[0]); }
                catch(Exception) { failed=true; }
                if(!failed) throw new Exception("missing bone branch must fail closed at install");
                Console.WriteLine("PASS native transpiler: only person tag replaced; labels and vehicle branch retained; missing branch rejected");
            }''')
    source = (ROOT / 'RevivalGepard.cs').read_text(encoding='utf-8')
    harness = harness.replace('    public static class Check {',
                              EXTRA_TYPES.replace('/*PERSON_BURST*/', bomb.method(source, 'static void PersonBurst('))
                              + '\n    public static class Check {')
    marker = 'Console.WriteLine("PASS authority loss cancels outstanding work");'
    harness = harness.replace(marker, marker + '\n' + CASES)
    mortar = (ROOT / 'RevivalMortar.cs').read_text(encoding='utf-8')
    guards = '\n'.join(bomb.method(mortar, sig) for sig in (
        'internal static void BreakKillStreak(', 'internal static bool Hurtable(', 'static bool Bool('))
    an2 = (ROOT / 'Revival.An2Bombs.cs').read_text(encoding='utf-8')
    flight = '\n'.join(bomb.method(an2, sig) for sig in (
        'static void Advance(', 'static bool OnPlane(', 'internal static bool Predict('))
    harness = harness.replace('/*GUARDS*/', guards).replace('/*FLIGHT*/', flight)
    run('Damage', harness, ['Revival.OrdnanceBlast.cs', 'Revival.BlastRegistry.cs', 'Revival.An2BombCurve.cs'])


REGISTRY_HARNESS = r'''
using System;
using System.Reflection;
namespace UnityEngine {
    public class Object {
        static int Next; readonly int Id=++Next;
        public int GetInstanceID() { return Id; }
        public static Object[] FindObjectsOfType(Type t) { return new Object[0]; }
    }
    public class GameObject { public bool activeInHierarchy=true; }
    public class Component : Object { public GameObject gameObject=new GameObject(); }
    public static class Time { public static float time; }
}
namespace HarmonyLib {
    public class HarmonyMethod { public HarmonyMethod(MethodInfo m) {} }
    public class Harmony { public void Patch(MethodInfo m,object p,object a,object b,object c,object d) {} }
    public static class AccessTools {
        public static MethodInfo DeclaredMethod(Type t,string n,Type[] p,object unused) { return t.GetMethod(n); }
    }
}
namespace NextDayRevival {
    public class Log { public void LogInfo(string s) {} public void LogWarning(string s) { throw new Exception(s); } }
    public static class RevivalPlugin {
        public static Log L=new Log();
        public static Type TypeByName(string n) { return Type.GetType("NextDayRevival."+n); }
    }
    public class NPC_AI2 : UnityEngine.Component { public void Awake() {} public void OnDestroy() {} }
    public class PlayerNetworkController : UnityEngine.Component { public void Awake() {} public void OnDestroy() {} }
    public static class Check {
        static void Assert(bool b,string s) { if(!b) throw new Exception(s); }
        public static void Main() {
            var h=new HarmonyLib.Harmony();
            NpcScan.Registry.Install(h); PlayerScan.Registry.Install(h);
            NPC_AI2 active=new NPC_AI2(),hidden=new NPC_AI2();hidden.gameObject.activeInHierarchy=false;
            SceneRegistry.AddHook(active);SceneRegistry.AddHook(hidden);
            PlayerNetworkController player=new PlayerNetworkController();player.gameObject.activeInHierarchy=false;
            SceneRegistry.AddHook(player);
            Assert(NpcScan.All().Length==1,"active scan compatibility");
            UnityEngine.Component[] snapshot=NpcScan.BlastTargets();
            Assert(snapshot.Length==2 && PlayerScan.BlastTargets().Length==1,"blast snapshots include inactive actors");
            Assert(Object.ReferenceEquals(snapshot,NpcScan.BlastTargets()),"stable snapshot reused without allocation");
            SceneRegistry.RemoveHook(hidden);
            Assert(NpcScan.BlastTargets().Length==1 && snapshot.Length==2,"membership change keeps queued snapshot immutable");
            Console.WriteLine("PASS production SceneRegistry: inactive NPC/player included, ordinary active queries unchanged, stable cached arrays, destruction updates without mutating queued snapshots");
        }
    }
}
'''


def seam_test():
    source = (ROOT / 'RevivalGepard.cs').read_text(encoding='utf-8')
    step = bomb.method(source, 'static bool Step(')
    assert step.count('PersonBurst(r, spec,') == 3  # proximity, impact, timed/self-destruct
    assert 'if (r.Terminal && spec != null) { Puff(spec, r.TerminalAt); return true; }' in step
    impact = bomb.method(source, 'static void ImpactBurst(')
    assert impact.count('PersonBurst(r, spec, at);') == 1
    assert 'else if (spec.GroundBurst != null) spec.GroundBurst(at);' in impact
    struck = bomb.method(source, 'internal static void Struck(List<Contact> contacts, Transform own, bool npc,\n                                    GameObject go, Vector3 point, Vector3 dir, int heliHits)')
    assert '"NPC_AI2", "ApplyDamage"' not in struck and '"PlayerApplyDamage"' not in struck
    mortar = (ROOT / 'RevivalMortar.cs').read_text(encoding='utf-8')
    sweep = bomb.method(mortar, 'internal static void Sweep(Vector3 point, bool shooter, float radius,')
    assert 'OrdnanceBlast.EnqueueMortar(' in sweep and 'Turret.TryDamage' not in sweep
    assert 'OrdnanceBlast.EnqueueOwnPlayer(' in bomb.method(mortar, 'internal static void Self(')
    queue = (ROOT / 'Revival.OrdnanceBlast.cs').read_text(encoding='utf-8')
    assert 'NpcScan.BlastTargets()' in queue and 'PlayerScan.BlastTargets()' in queue
    patrol = (ROOT / 'Revival.Patrol.cs').read_text(encoding='utf-8')
    splash = bomb.method(patrol, 'static void Sprengschaden(')
    assert 'OrdnanceBlast.EnqueuePatrolPeople(' in splash
    assert 'c.Tr == null || c.Vehicle == null' in splash  # old cache handles vehicles only
    assert not re.search(r'Physics\.|FindObjects|GetComponents', re.sub(r'//[^\n]*', '', queue))
    # Bind the transpiler assumption to the actual installed game IL, read only.
    import ilq
    il = '\n'.join(ilq.asm().dis('ExplosionObject::ExplosionPhysicsEffect'))
    assert il.count('"RagdollBone"') == 1 and 'Component::CompareTag' in il
    for field in ('ExplosionDamage', 'ExplodeDamageRadius', 'damagedPlayers'):
        assert field in ilq.fields('ExplosionObject')
    print('PASS producer seams and shipped native IL: one person branch, no physics/scene queries in queue, AA terminal paths queue once')


if __name__ == '__main__':
    damage_test()
    run('Registry', REGISTRY_HARNESS, ['Revival.VehicleScan.cs'])
    seam_test()
    print('BLAST POSITION CHECK PASS')

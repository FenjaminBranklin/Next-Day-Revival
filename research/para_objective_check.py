"""Compile and simulate the unchanged production paratroop/ground decision cores.

No game, Photon, Unity NavMesh or installation. Reuses the existing analytic
ground-world harness and runs its contact/cover/flank checks as well.
"""
from pathlib import Path
import copy
import os
import subprocess
import sys

import ground_alive_check as ground

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import airdef


CASES = r'''
        static void ParaMove(GroundBrain b, ParaObjective p, float dt, int blocked)
        {
            for (int i = 0; i < b.Count; i++)
            {
                GroundMan m = b.Men[i];
                if (!m.Alive || i == blocked) continue;
                Vector3 dest = b.Phase == GroundPhase.Calm ? p.Slots[i] : m.Dest;
                if (b.Phase != GroundPhase.Calm && !m.Moving) continue;
                Vector3 d = dest - m.Pos;
                float len = d.magnitude;
                float move = (p.Phase == ParaObjective.Hold ? 3.5f : 9f) * dt;
                if (len > 0.01f) m.Pos += d * (Math.Min(len, move) / len);
            }
        }

        static void ParaCase(string name, int count, Vector3 target, int casualty, int blocked, bool contact)
        {
            World w = new World();
            GroundBrain b = new GroundBrain(count, Vector3.zero, ParaObjective.MovingRadius, GroundDuty.Hold, 11);
            ParaObjective p = new ParaObjective(count, Vector3.zero, target, 120f);
            float firstAdvance = -1f, holdAt = -1f;
            int firstNear = 0, oldVersion = p.Version;
            bool interrupted = false;
            for (int i = 0; i < count; i++)
            {
                b.Men[i].Alive = true;
                b.Men[i].Pos = new Vector3((i - count * 0.5f) * 28f, 0f, (i % 3 - 1) * 40f);
            }
            for (int frame = 0; frame < 24000; frame++)
            {
                float now = frame / 60f;
                if (casualty >= 0 && now > 4f) b.Men[casualty].Alive = false;
                // Production brain enters contact/search, then returns to duty.
                if (contact && p.Phase == ParaObjective.Advance && !interrupted)
                {
                    interrupted = true;
                    b.Alert(p.Anchor + new Vector3(100f, 0f, 0f), now);
                }
                if (now >= b.NextThink) b.Think(w, now);
                int version = p.Version;
                byte before = p.Phase;
                if (p.Due(now)) p.Tick(b, now);
                if (b.Phase != GroundPhase.Calm && p.Version != version)
                    Ok(false, "combat must keep mission bounds paused");
                if (before == ParaObjective.Regroup && p.Phase != before)
                {
                    firstAdvance = now;
                    for (int i = 0; i < count; i++)
                        if (b.Men[i].Alive && (b.Men[i].Pos - Offset(Vector3.zero, i)).magnitude <= ParaObjective.Arrive)
                            firstNear++;
                }
                ParaMove(b, p, 1f / 60f, blocked);
                if (p.Phase == ParaObjective.Hold) { holdAt = now; break; }
            }
            Console.WriteLine(name + ": regroup " + firstAdvance.ToString("F1") + "s, " + firstNear
                + " at rally; hold " + holdAt.ToString("F1") + "s; bounds " + (p.Version - oldVersion));
            Ok(firstAdvance > 0f, name + " regroups before advancing");
            Ok(holdAt >= firstAdvance && (p.Anchor - target).magnitude < 0.1f, name + " reaches selected objective");
            Ok(p.HoldUntil == holdAt + 120f, name + " starts hold lifetime on arrival");
            int held = p.Version;
            p.Tick(b, holdAt + 100f);
            Ok(p.Phase == ParaObjective.Hold && p.Version == held, name + " holds instead of returning to DZ");
            if (contact) Ok(interrupted && b.Contacts > 0 && b.Phase == GroundPhase.Calm,
                "production contact/search completes and mission resumes");
        }

        static Vector3 Offset(Vector3 anchor, int i)
        {
            float a = i * 2.39996f, r = 14f * Mathf.Sqrt(i);
            return anchor + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }

        static void ParaCost()
        {
            GroundBrain b = Group(Vector3.zero, 12, 160f, GroundDuty.Hold, 7);
            ParaObjective p = new ParaObjective(12, Vector3.zero, new Vector3(840f, 0f, 280f), 120f);
            for (int i = 0; i < 12; i++) b.Men[i].Pos = p.Slots[i];
            for (int i = 0; i < 10000; i++) p.Tick(b, i * 0.5f);
            Stopwatch sw = new Stopwatch();
            sw.Start(); sw.Stop(); sw.Reset();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memory = GC.GetTotalMemory(false);
            sw.Start();
            for (int i = 0; i < 1000000; i++) p.Tick(b, 5001f + i * 0.5f);
            sw.Stop();
            long allocated = GC.GetTotalMemory(false) - memory;
            Console.WriteLine("CORE COST: " + (sw.Elapsed.TotalMilliseconds / 1000000).ToString("F6")
                + " ms/2Hz tick; managed growth " + allocated + " B");
            Ok(allocated <= 0, "mission decisions have zero steady-state managed allocation");
            Ok(sw.Elapsed.TotalMilliseconds / 1000000 < 0.1, "mission decision cost below 0.1 ms");
        }

        static void ParaCases()
        {
            ParaCase("EDITOR", 12, new Vector3(840f, 0f, 280f), -1, -1, false);
            ParaCase("CASUALTY", 8, new Vector3(560f, 0f, -280f), 7, -1, false);
            ParaCase("BLOCKED STRAGGLER", 12, new Vector3(560f, 0f, 0f), -1, 11, false);
            ParaCase("CONTACT RESUME", 8, new Vector3(560f, 0f, 0f), -1, -1, true);
            ParaCase("SECURE DZ", 1, Vector3.zero, -1, -1, false);
            for (int n = 2; n <= 12; n++)
                ParaCase("SIZE " + n, n, new Vector3(140f, 0f, -140f), -1, -1, false);
            GroundBrain stuck = Group(Vector3.zero, 4, 160f, GroundDuty.Hold, 11);
            ParaObjective fallback = new ParaObjective(4, Vector3.zero, new Vector3(280f, 0f, 0f), 120f);
            for (int i = 0; i < 4; i++) stuck.Men[i].Pos = new Vector3(1000f, 0f, 1000f);
            fallback.Tick(stuck, 0f); fallback.Tick(stuck, 89f);
            Ok(fallback.Phase == ParaObjective.Regroup, "unformed group waits for its rally");
            fallback.Tick(stuck, 90f);
            Ok(fallback.Phase == ParaObjective.Advance, "90s regroup limit prevents endless initial waiting");
            fallback.Tick(stuck, 180f);
            Ok(fallback.Phase != ParaObjective.Hold, "blocked group never claims arrival or teleports");
            ParaCost();
        }
'''


def main():
    # Execute the production cores, with the existing analytic terrain/world.
    harness = ground.HARNESS.replace('CORE_SOURCE', ground.CORE.replace('using System;', '').replace(
        'using System.Collections.Generic;', '').replace('using UnityEngine;', ''))
    harness = harness.replace('static int Main()', CASES + '\n        static int Main()')
    harness = harness.replace('            Spread();', '            ParaCases();\n            Spread();')
    work = ROOT / 'build' / 'para_objective_check'
    work.mkdir(parents=True, exist_ok=True)
    source = work / 'check.cs'
    source.write_text(harness, encoding='utf-8')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    exe = work / 'check.exe'
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/codepage:65001',
                            '/out:' + str(exe), str(source), str(ROOT / 'Revival.ParaObjectiveCore.cs')],
                           capture_output=True)
    if built.returncode:
        print(built.stdout.decode('utf-8', 'replace'))
        return 1
    ran = subprocess.run([str(exe)], capture_output=True)
    output = ran.stdout.decode('utf-8', 'replace')
    # Collapse repeated pause assertions, keep scenario summaries and failures.
    for line in output.splitlines():
        if 'combat keeps mission bounds paused' not in line or 'FAIL' in line:
            print(line)
    if ran.returncode:
        return 1

    adapter = ground.read('Revival.ParaObjective.cs')
    combat = ground.read('Revival.NpcCombat.cs')
    air = ground.read('Revival.AirEvents.cs')
    for signature in ['static void ParaObjectiveTick(', 'static void ParaObjectiveStep(']:
        body = ground.method(adapter, signature)
        ground.ok(body and not ground.allocates(body), signature + ' no per-frame allocations')
    ground.ok('!IsMaster()' in ground.method(adapter, 'internal static bool StartParatroopers('),
              'only master creates mission; native OrderMove/Drive provide Photon state sync')
    run = ground.method(combat, 'static void RunGround(')
    ground.ok(run.index('!GroundAliveGate') < run.index('ParaObjectiveTick(s, now)'), 'sleep gate precedes mission work')
    ground.ok(run.index('now >= s.HardEnd') < run.index('!GroundAliveGate'), 'expired missions are cleaned even while asleep')
    ground.ok('s.Para != null' in run and 'FrameProf.S(FrameProf.S_GroundAliveT)' in combat,
              'mission and reused tactics registered under F6 GroundAlive.Tick')
    ground.ok('now + 0.1f' in adapter and 'now + 3.5f' in adapter, 'projections/orders capped globally at 10 Hz')
    ground.ok('at.HasValue ? new Vector3(at.Value.x, 0f, at.Value.z) : attack' in air,
              'admin clicked point overrides template attack offset/rotation')
    ground.ok('NpcWar.StartParatroopers(' in ground.method(air, 'static void SpawnSquad('), 'landing hands survivors the mission')

    definition = {'airEvents': [copy.deepcopy(airdef.DEFAULT_EVENTS[0])]}
    event = definition['airEvents'][0]
    event['attackX'], event['attackZ'] = event['dropX'], event['dropZ']
    errors = []
    airdef.validate_events(definition, lambda where, why: errors.append(where + ': ' + why))
    ground.ok(not errors, 'editor accepts secure DZ (head == drop): ' + str(errors))
    del event['attackX'], event['attackZ']
    airdef.normalise(definition)
    ground.ok(event['attackX'] == event['dropX'] and event['attackZ'] == event['dropZ'],
              'missing objective defaults to DZ without changing authored arrows')
    cols = airdef.to_airevents_tsv(definition).splitlines()[-1].split('\t')
    ground.ok(cols[8:10] == cols[10:12], 'runtime TSV preserves secure-DZ objective')
    authored = {'airEvents': [copy.deepcopy(airdef.DEFAULT_EVENTS[0])]}
    before = (authored['airEvents'][0]['attackX'], authored['airEvents'][0]['attackZ'])
    airdef.normalise(authored)
    ground.ok(before == (authored['airEvents'][0]['attackX'], authored['airEvents'][0]['attackZ']),
              'existing authored objective is preserved')
    if ground.FAILS:
        return 1
    print('PASS: paratroops regroup, advance, resume after contact and hold; all sizes 1..12; zero steady-state core allocation')
    return 0


if __name__ == '__main__':
    sys.exit(main())

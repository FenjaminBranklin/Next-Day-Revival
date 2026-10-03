"""G O1: orders go to the picked mercs; FETCH sends one.

Compiles the production Revival.MercTargetCore.cs unchanged with csc 3.5 and
runs the rules (pick, squad minus gun/radar crews, nearest free merc), then
pins the wiring of every wheel/list order to them. No game process is used.
"""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def block(source, anchor):
    start = source.index(anchor)
    at = source.index('{', start)
    end, depth = at + 1, 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


HARNESS = r'''
using System;
using System.Collections.Generic;
using NextDayRevival;
class Merc { public string Name; public bool Selected = true, Duty; public float D; public bool Car; }
static class Check
{
    static int checks, failures;
    static void Ok(bool c, string what) { checks++; if (!c) { failures++; Console.WriteLine("  FAIL  " + what); } }
    // The production SquadSelection loop over a fake roster.
    static List<string> Squad(List<Merc> roster, bool picked, out List<string> kept)
    {
        int sel = 0; foreach (Merc m in roster) if (m.Selected) sel++;
        bool pick = MercTargetPlan.Explicit(picked, sel, roster.Count);
        List<string> got = new List<string>(); kept = new List<string>();
        foreach (Merc m in roster)
        {
            if (pick && !m.Selected) continue;
            if (MercTargetPlan.Takes(pick, m.Selected, m.Duty)) got.Add(m.Name); else if (m.Duty) kept.Add(m.Name);
        }
        return got;
    }
    static List<Merc> Roster()
    {
        List<Merc> r = new List<Merc>();
        string[] names = { "A", "B", "Gun", "Radar", "E" };
        float[] d = { 400, 90, 10, 5, 200 };
        for (int i = 0; i < names.Length; i++) { Merc m = new Merc(); m.Name = names[i]; m.D = d[i]; m.Duty = i == 2 || i == 3; r.Add(m); }
        return r;
    }
    static int Main()
    {
        Ok(!MercTargetPlan.Explicit(false, 6, 6), "all checked (default / Ctrl+0) is no pick");
        Ok(!MercTargetPlan.Explicit(false, 0, 6) && !MercTargetPlan.Explicit(true, 0, 6), "nothing checked is no pick");
        Ok(MercTargetPlan.Explicit(false, 2, 6), "a subset checked is a pick");
        Ok(MercTargetPlan.Explicit(true, 6, 6) && MercTargetPlan.Explicit(true, 1, 1), "rows chosen by the player are a pick, even all of them");
        Ok(!MercTargetPlan.Explicit(false, 1, 1), "a lone merc, untouched list: no pick");

        List<Merc> roster = Roster(); List<string> kept;
        List<string> got = Squad(roster, false, out kept);
        Ok(string.Join(",", got.ToArray()) == "A,B,E" && string.Join(",", kept.ToArray()) == "Gun,Radar",
            "no pick: FOLLOW/STAY/ATTACK reach everyone but the gun and radar crews, who are named");
        roster[2].Selected = false; roster[0].Selected = false;
        got = Squad(roster, true, out kept);
        Ok(string.Join(",", got.ToArray()) == "B,Radar,E" && kept.Count == 0, "pick: exactly the checked mercs, the radar crew too");
        foreach (Merc m in roster) m.Selected = m.Name == "Gun";
        got = Squad(roster, true, out kept);
        Ok(got.Count == 1 && got[0] == "Gun", "a gun crew checked alone is explicitly ordered off his gun");
        foreach (Merc m in roster) m.Duty = true;
        foreach (Merc m in roster) m.Selected = true;
        got = Squad(roster, false, out kept);
        Ok(got.Count == 0 && kept.Count == 5, "no pick, everyone crewing: nobody moves, all five named");

        // FETCH without a pick: the free candidates (no crews) by distance.
        float[] d = { 400, 90, 200, 30 }; bool[] car = { false, false, true, false }; bool[] taken = new bool[4];
        Ok(MercTargetPlan.Nearest(d, car, taken, 4, false) == 3, "nearest free merc");
        Ok(MercTargetPlan.Nearest(d, car, taken, 4, true) == 2, "a merc beside a ready vehicle beats a nearer one without");
        car[2] = false;
        Ok(MercTargetPlan.Nearest(d, car, taken, 4, true) == 3, "no vehicle anywhere: still the nearest (the trip names the reason)");
        taken[3] = true;
        Ok(MercTargetPlan.Nearest(d, car, taken, 4, false) == 1, "escort: the next nearest after the transport");
        Ok(MercTargetPlan.Nearest(d, car, taken, 0, true) == -1, "no free merc: -1");
        taken[0] = taken[1] = taken[2] = true;
        Ok(MercTargetPlan.Nearest(d, car, taken, 4, false) == -1, "all taken: -1");

        taken[0] = taken[1] = taken[2] = taken[3] = false;
        GC.Collect(); long before = GC.GetTotalMemory(true); int gen = GC.CollectionCount(0); int sum = 0;
        for (int i = 0; i < 200000; i++)
            sum += MercTargetPlan.Nearest(d, car, taken, 4, (i & 1) == 0) + (MercTargetPlan.Explicit(false, i & 7, 6) ? 1 : 0);
        Ok(GC.GetTotalMemory(false) - before < 256 && GC.CollectionCount(0) == gen && sum != 0, "the rules allocate nothing");
        Console.WriteLine("  PASS  G O1 target rules: " + checks + " assertions, " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }
}
'''


def wiring():
    fails = []

    def ok(cond, what):
        print(('  PASS  ' if cond else '  FAIL  ') + what)
        if not cond:
            fails.append(what)

    read = lambda n: (ROOT / n).read_text(encoding='utf-8')
    mercs, ui = read('Revival.Mercs.cs'), read('Revival.MercsUi.cs')
    issue = block(ui, 'static void Issue(')
    for call in ('Mercs.OrderFollow()', 'Mercs.OrderStay(point, facing)', 'Mercs.OrderPerimeter(point, facing)',
                 'Mercs.OrderVehicle()', 'Mercs.ToggleAirDefence()', 'Mercs.OrderRaidCover()',
                 'AttackAtCrosshair()', 'MercFetch.Start()', 'Mercs.ToggleMedics()'):
        ok(call in issue, 'wheel: ' + call)
    for name in ('internal static void OrderFollow()', 'internal static void OrderVehicle()',
                 'internal static void OrderStay(', 'internal static void OrderPatrol(',
                 'internal static void OrderPerimeter(', 'internal static void OrderAttack('):
        body = block(mercs, name)
        ok('SquadSelection()' in body and 'Announce(' in body and ' Selection()' not in body,
           name.split('(')[0].split()[-1] + ': squad selection + feedback naming who got it')
    for path, name in (('Revival.MercRaid.cs', 'internal static void OrderRaidCover()'),
                       ('Revival.MercMoveOrder.cs', 'internal static bool OrderMove('),
                       ('Revival.MercMedic.cs', 'internal static void ToggleMedics()')):
        body = block(read(path), name)
        ok('SquadSelection()' in body and 'Announce(' in body, path + ': squad selection + feedback')
    ok('Willing(SquadSelection(), "DRIVE")' in read('Revival.MercDriveRide.cs'), 'DRIVE: squad selection')
    quick = block(read('Revival.MercQuickOrders.cs'), 'static void QuickAttack()')
    ok('Mercs.SquadSelection()' in quick and 'Mercs.Announce(' in quick, 'click ATTACK: squad selection + feedback')
    sel = block(mercs, 'internal static List<Record> SquadSelection()')
    ok('PickActive()' in sel and 'MercTargetPlan.Takes(' in sel and '_onDuty.Add(' in sel, 'SquadSelection uses the core')
    ok('MercOrder.ManGun' in block(mercs, 'static bool OnDuty(Record r)')
       and 'MercOrder.ManRadar' in block(mercs, 'static bool OnDuty(Record r)'), 'gun and radar crews are on duty')
    ok('Picked = number != 0;' in block(mercs, 'internal static void Select(int number)'), 'Ctrl+1..5 pick, Ctrl+0 clears')
    ok('m.Selected = sel; Mercs.Picked = true;' in ui, 'an L checkbox is a pick')
    page = block(read('Revival.MercPage.cs'), 'static void Order(Mercs.Record target, int act)')
    ok('Mercs.Picked = target != null;' in page and 'Mercs.Picked = picked;' in page, "a merc's own row is a pick, restored after")
    air = block(read('Revival.MercAirfield.cs'), 'internal static void ToggleAirDefence()')
    ok('bool pick = PickActive();' in air and '(pick && !r.Selected)' in air and 'Announce(' in air,
       'MAN AIR DEFENCE: the pick only, else the whole squad')
    fetch = read('Revival.MercFetch.cs')
    start = block(fetch, 'internal static void Start()')
    ok('Mercs.PickActive()' in start and 'PickNearest(roster, l, l.WithEscort ? 3 : 1)' in start,
       'FETCH: the pick, else one merc (three with escort)')
    near = block(fetch, 'static void PickNearest(')
    ok('MercAA.IsOrder(r.Order)' in near and 'r.Unpaid' in near and 'MercTargetPlan.Nearest(' in near
       and 'MercDrive.CanDrive(' in near and 'new ' not in near, 'FETCH pick: nearest free paid merc, vehicle first, no allocation')
    ok('l.Selected[i].Name' in start, 'FETCH feedback names who travels')
    core = read('Revival.MercTargetCore.cs')
    ok(all(ord(c) < 128 for c in core) and 'UnityEngine' not in core and 'new ' not in core,
       'core: ASCII, Unity-free, allocation-free')
    ok('"Revival.MercTargetCore.cs"' in read('sync_public.py'), 'public package ships the core')
    return len(fails)


def main():
    work = ROOT / 'build' / 'merc_order_targets_check'
    work.mkdir(parents=True, exist_ok=True)
    src = work / 'Harness.cs'
    src.write_text(HARNESS, encoding='ascii')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework/v3.5/csc.exe'
    exe = work / 'Check.exe'
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/out:' + str(exe), str(src),
                            str(ROOT / 'Revival.MercTargetCore.cs')], capture_output=True, text=True, errors='replace')
    if built.returncode:
        print(built.stdout + built.stderr)
        print('  FAIL  the harness does not compile with csc 3.5')
        return 1
    ran = subprocess.run([str(exe)], capture_output=True, text=True, errors='replace')
    print((ran.stdout + ran.stderr).rstrip())
    fails = (1 if ran.returncode else 0) + wiring()
    print('RESULT: ' + ('PASS' if fails == 0 else 'FAIL (%d)' % fails))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())

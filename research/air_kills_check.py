"""W AA4 offline check: a kill must matter.

Compiles the UNCHANGED production core (Revival.AirKillCore.cs) with csc 3.5
and runs it against:

  A  the damage ledger: Tu-95 / An-2 hit points from flak, rifles, blasts;
     credit (owner of the final hit; NPC garrison kills pay nobody).
  B  the release decision: whole / damaged / badly damaged bombers over
     200000 rolls - share of sticks on the chosen line, wide, aborted.
  C  a sortie model that mirrors the runtime glue (AirEvents.LaunchBomber:
     onTick only while not down, the plan at the release point; TickBombs:
     no bomb leaves a bay once down): hits before / during / after release.
  D  the gun pit: a 48 x FAB-250 carpet (900 u line, the editor default) at
     several distances from a 52-K ring with its two-man crew, with and
     without the revetment rule, Mortar.Sweep's linear falloff.
  E  zero allocation / cost of the per-blast pit test and the plan.

and checks in the runtime source that the glue really is wired that way
(the sortie model is only as good as that mirror).

Run:  python research/air_kills_check.py      (exit 0 = every check PASS)
"""
from pathlib import Path
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

HARNESS = r'''
using System;
using NextDayRevival;

class Check {
 static int failures;
 static void Ok(bool good, string name) { Console.WriteLine((good ? "PASS " : "FAIL ") + name); if (!good) failures++; }
 const float K = 2.8f;
 const float FabRadiusU = 16f * K;           // AirEvents.BombRadiusM x K
 const float FabNpcPeak = 900f;              // AirEvents.BombNpcPeak
 const float NpcHealth = 100f;

 static double Gauss(Random r) {
  double u = Math.Max(1e-4, r.NextDouble()), v = r.NextDouble();
  return Math.Max(-2.5, Math.Min(2.5, Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v)));
 }

 // ------------------------------------------------------------------ A
 static void Ledger() {
  Console.WriteLine("-- A damage ledger");
  // Tu-95: toughness 3, 52-K HeliHits 2.
  DamageLedger t = new DamageLedger();
  bool down = t.Add(AirKillCore.GunHitDamage(2), 0);
  Ok(!down && Math.Abs(t.Damage - 0.5f) < 1e-4 && AirKillCore.Level(t.Damage) == 2, "Tu-95: one 85 mm hit = 50 %, badly damaged, still flying");
  Ok(t.Add(AirKillCore.GunHitDamage(2), 0), "Tu-95: the second 85 mm hit brings it down");
  DamageLedger r = new DamageLedger(); int n = 0;
  while (!r.Add(AirKillCore.RoundDamage(3), 7)) n++;
  Ok(n + 1 == 90, "Tu-95: 90 rifle rounds (30 x toughness 3), counted " + (n + 1));
  DamageLedger a = new DamageLedger(); n = 0;
  while (!a.Add(AirKillCore.RoundDamage(1), 7)) n++;
  Ok(n + 1 == 30, "An-2: 30 rifle rounds, counted " + (n + 1));
  DamageLedger m = new DamageLedger();
  m.Add(AirKillCore.GunHitDamage(2), 0); n = 0;
  while (!m.Add(AirKillCore.RoundDamage(3), 5)) n++;
  Ok(n + 1 == 45, "damage persists and adds up: 1 flak hit + " + (n + 1) + " rifle rounds down a Tu-95");
  Ok(m.Winner() == 5, "credit: the player who fired the last hit");
  DamageLedger g = new DamageLedger();
  g.Add(0.5f, 9); g.Add(0.5f, 0);
  Ok(g.Down && g.Winner() == 0, "credit: NPC garrison finishes, even a 50 % player assist is not paid");
  DamageLedger h = new DamageLedger();
  h.Add(0.1f, 9); h.Add(0.95f, 0);
  Ok(h.Down && h.Winner() == 0, "credit: a 10 % assist is not paid, an NPC kill pays nobody");
  DamageLedger b = new DamageLedger();
  b.Add(1f, 4);
  Ok(b.Down && b.Winner() == 4 && !b.Add(1f, 6) && b.Winner() == 4, "blast = all hit points; a dead airframe takes no more credit");
  DamageLedger s = new DamageLedger();
  for (int i = 1; i <= 6; i++) s.Add(0.05f, i);
  Ok(!s.Down && s.Winner() == 6, "six shooters in four slots: no overflow, last hitter credited");
  Ok(AirKillCore.Bounty(0, 150000, 60000, 80000) == 150000 && AirKillCore.Bounty(1, 150000, 60000, 80000) == 60000
     && AirKillCore.Bounty(2, 150000, 60000, 80000) == 80000 && AirKillCore.Bounty(3, 150000, 60000, 80000) == 0
     && AirKillCore.Bounty(0, -5, 0, 0) == 0, "bounty table: Tu-95 / An-2 / troop Mi-8, test flyover pays nothing, never negative");
 }

 // ------------------------------------------------------------------ B
 static void Plans() {
  Console.WriteLine("-- B release decision (200000 rolls each)");
  Random rnd = new Random(11);
  float[] dmg = { 0f, 0.1f, 1f / 90f * 14, 0.2f, 0.3f, 0.5f, 0.6f, 0.75f, 0.95f };
  string[] name = { "whole", "10 %", "14 rounds", "20 %", "30 %", "one 85 mm hit", "60 %", "75 %", "95 %" };
  for (int d = 0; d < dmg.Length; d++) {
   int normal = 0, wide = 0, abort = 0; double miss = 0, sx = 0, sz = 0;
   for (int i = 0; i < 200000; i++) {
    ReleasePlan p = AirKillCore.Plan(dmg[d], (float)rnd.NextDouble(), (float)rnd.NextDouble());
    if (p.Kind == ReleaseKind.Normal) normal++;
    else if (p.Kind == ReleaseKind.Abort) abort++;
    else { wide++; miss += Math.Sqrt(p.AlongM * p.AlongM + p.AcrossM * p.AcrossM); sx += p.AlongM; sz += p.AcrossM; }
   }
   Console.WriteLine(string.Format("   {0,-14} dmg {1,5:0.00}: on line {2,6:0.0} %  wide {3,6:0.0} % (mean miss {4,4:0} m)  abort {5,6:0.0} %",
     name[d], dmg[d], normal / 2000.0, wide / 2000.0, wide > 0 ? miss / wide : 0, abort / 2000.0));
   if (d == 0) Ok(normal == 200000, "a whole bomber always drops on the chosen line");
   if (d == 5) Ok(abort > 120000 && abort < 130000 && normal == 0, "one 85 mm hit: ~62 % abort, the rest go wide, none on the line");
   if (d == 3) Ok(normal == 0 && abort == 0 && miss / wide > 80, "20 % damage: every stick goes wide (> 80 m mean miss)");
   if (wide > 1000) Ok(Math.Abs(sx / wide) < 3 && Math.Abs(sz / wide) < 3, "   " + name[d] + ": the miss has no preferred direction (never toward the guns)");
  }
  ReleasePlan dead = AirKillCore.Plan(1f, 0.99f, 0f);
  Ok(dead.Kind == ReleaseKind.Abort, "a destroyed bomber never releases");
 }

 // ------------------------------------------------------------------ C
 // Mirrors AirEvents.LaunchBomber + NpcAircraft.Tick + AirEvents.TickBombs:
 // the flight's onTick (the release point) runs only while it is not down;
 // at release the ledger's damage picks the plan; a bomb still in the bay
 // when the bomber is down never falls.
 static int Sortie(Random rnd, float[] hitAt, float[] hitDmg, float release, int bombs, float stick, out ReleaseKind kind, out double missM) {
  DamageLedger L = new DamageLedger();
  kind = ReleaseKind.Abort; missM = 0;
  bool released = false; float downAt = float.MaxValue;
  float[] events = new float[hitAt.Length + 1];
  int hi = 0;
  for (float t = 0; t < release + stick + 1; t += 0.05f) {
   while (hi < hitAt.Length && hitAt[hi] <= t) { if (L.Add(hitDmg[hi], 3)) downAt = Math.Min(downAt, hitAt[hi]); hi++; }
   if (!released && !L.Down && t >= release) {
    released = true;
    ReleasePlan p = AirKillCore.Plan(L.Damage, (float)rnd.NextDouble(), (float)rnd.NextDouble());
    kind = p.Kind;
    missM = Math.Sqrt(p.AlongM * p.AlongM + p.AcrossM * p.AcrossM);
    if (p.Kind == ReleaseKind.Abort) return 0;
   }
  }
  if (!released) return 0;
  int dropped = 0;
  for (int i = 0; i < bombs; i++) if (release + i * stick / bombs < downAt) dropped++;
  return dropped;
 }

 static void Sorties() {
  Console.WriteLine("-- C sortie model (Tu-95, 48 FAB-250, release at t=60 s, stick 2.8 s; 20000 sorties per row)");
  Random rnd = new Random(5);
  float half = AirKillCore.GunHitDamage(2);
  string[] rows = { "no hit", "1 hit at 30 s", "2 hits at 30/50 s", "2 hits at 30/61 s (in the stick)", "1 hit after release (65 s)", "14 rifle rounds by 40 s", "Gepard 35 mm x3 (HeliHits 10)" };
  for (int row = 0; row < rows.Length; row++) {
   float[] at, d;
   switch (row) {
    case 0: at = new float[0]; d = new float[0]; break;
    case 1: at = new float[] { 30 }; d = new float[] { half }; break;
    case 2: at = new float[] { 30, 50 }; d = new float[] { half, half }; break;
    case 3: at = new float[] { 30, 61 }; d = new float[] { half, half }; break;
    case 4: at = new float[] { 65 }; d = new float[] { half }; break;
    case 5: at = new float[14]; d = new float[14]; for (int i = 0; i < 14; i++) { at[i] = 20 + i; d[i] = AirKillCore.RoundDamage(3); } break;
    default: at = new float[] { 30, 35, 40 }; d = new float[] { 0.1f, 0.1f, 0.1f }; break;
   }
   int n = 20000; long bombs = 0; int onLine = 0, wide = 0, aborted = 0, none = 0; double miss = 0;
   for (int i = 0; i < n; i++) {
    ReleaseKind k; double m;
    int b = Sortie(rnd, at, d, 60f, 48, 2.8f, out k, out m);
    bombs += b;
    if (b == 0) none++;
    if (b > 0 && k == ReleaseKind.Normal) onLine++;
    if (b > 0 && k == ReleaseKind.Wide) { wide++; miss += m; }
    if (k == ReleaseKind.Abort) aborted++;
   }
   Console.WriteLine(string.Format("   {0,-34} bombs/sortie {1,5:0.0}  on the line {2,5:0.0} %  wide {3,5:0.0} % (miss {4,3:0} m)  nothing {5,5:0.0} %",
     rows[row], bombs / (double)n, onLine * 100.0 / n, wide * 100.0 / n, wide > 0 ? miss / wide : 0, none * 100.0 / n));
   if (row == 0) Ok(bombs == 48L * n && onLine == n, "no hit: all 48 bombs on the editor's line");
   if (row == 2) Ok(bombs == 0, "shot down before its release point: drops nothing");
   if (row == 1) Ok(onLine == 0 && none > n * 0.55, "damaged before release: never on the line, mostly aborts");
   if (row == 3) Ok(bombs / (double)n < 48 * 0.4 && bombs > 0, "shot down during the stick: the rest stays in the bay");
   if (row == 4) Ok(bombs == 48L * n && onLine == n, "hit after the stick is away: the drop stands (it was already falling)");
   if (row == 5) Ok(onLine == 0 && wide == n, "14 rifle rounds (15 %): drops wide");
  }
 }

 // ------------------------------------------------------------------ D
 static void Pits() {
  Console.WriteLine("-- D gun pit vs. a 48 x FAB-250 carpet (900 u line, bombs 18.75 u apart, dispersion 3/4 m); 4000 carpets per row");
  // Crew seats of the 52-K relative to the ring centre, world units (~1.3 and 1.8 m).
  float[,] crew = { { -1.3f * K, 0.9f * K, 0.4f * K }, { 1.1f * K, 0.9f * K, -1.4f * K } };
  float[] offsets = { 0f, 8f * K, 12f * K, 18f * K, 30f * K, 60f * K };
  string[] label = { "line over the pit", "8 m beside", "12 m beside", "18 m (~50 u) beside", "30 m beside", "60 m beside" };
  Random rnd = new Random(3);
  for (int o = 0; o < offsets.Length; o++) {
   int deadOpen = 0, deadPit = 0, hurtOpen = 0, hurtPit = 0, pitHits = 0; int n = 4000;
   for (int c = 0; c < n; c++) {
    double phase = rnd.NextDouble() * 18.75;
    float[] open = new float[2], pit = new float[2];
    for (int b = 0; b < 48; b++) {
     double z = -450 + phase + b * 18.75 + Gauss(rnd) * 3 * K;
     double x = offsets[o] + Gauss(rnd) * 4 * K;
     if (x * x + z * z <= (AirKillCore.PitHitRadiusM * K) * (AirKillCore.PitHitRadiusM * K)) pitHits++;
     for (int m = 0; m < 2; m++) {
      double dx = crew[m, 0] - x, dz = crew[m, 2] - z, dy = crew[m, 1];
      double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
      if (dist > FabRadiusU) continue;
      float dmg = (float)(FabNpcPeak * Math.Max(0, 1 - dist / FabRadiusU));
      if (dmg < 1f) continue;
      open[m] += dmg;
      if (!AirKillCore.Sheltered(crew[m, 0], crew[m, 1], crew[m, 2], (float)x, (float)z, K)) pit[m] += dmg;
     }
    }
    for (int m = 0; m < 2; m++) {
     if (open[m] >= NpcHealth) deadOpen++; else if (open[m] > 0) hurtOpen++;
     if (pit[m] >= NpcHealth) deadPit++; else if (pit[m] > 0) hurtPit++;
    }
   }
   Console.WriteLine(string.Format("   {0,-22} crew killed: open {1,5:0.0} %  in the ring {2,5:0.0} %   (bombs in the pit per carpet {3:0.00})",
     label[o], deadOpen * 50.0 / n, deadPit * 50.0 / n, pitHits / (double)n));
   if (o == 0) Ok(deadPit > 0 && deadPit * 50.0 / n > 20, "a carpet straight over the ring still kills the crew when a bomb lands in the pit");
   if (o == 2) Ok(deadOpen * 50.0 / n > 80 && deadPit * 50.0 / n < 5, "12 m beside: ~all dead in the open, in the ring only a stray bomb in the pit kills");
   if (o == 3) Ok(deadOpen * 50.0 / n > 25 && deadPit == 0 && hurtPit == 0, "the ~50 u carpet: kills in the open, nobody even hurt in the ring");
   if (o >= 1) Ok(deadPit <= deadOpen, "   " + label[o] + ": the ring never makes it worse");
   if (o >= 3) Ok(deadPit == 0, "   " + label[o] + ": the crew in the ring survives");
  }
  // Somebody OUTSIDE the ring is not sheltered, and a bomb in the pit is not blocked.
  Ok(!AirKillCore.Sheltered(7f * K, 0, 0, 40f * K, 0, K), "a man outside the ring (7 m) is not sheltered");
  Ok(!AirKillCore.Sheltered(1f * K, 0, 0, 3f * K, 0, K), "a bomb 3 m from the pit centre (in the pit) is not blocked");
  Ok(AirKillCore.Sheltered(1f * K, 0, 0, 4.5f * K, 0, K), "a bomb on the sandbag wall (4.5 m) is blocked for the man inside");
  Ok(!AirKillCore.Sheltered(1f * K, 12f * K, 0, 20f * K, 0, K), "a man 12 m above the ring (tower, aircraft) is not sheltered");
 }

 // ------------------------------------------------------------------ E
 static void Cost() {
  Console.WriteLine("-- E cost");
  Random rnd = new Random(1);
  float[] xs = new float[1024];
  for (int i = 0; i < xs.Length; i++) xs[i] = (float)(rnd.NextDouble() * 60 - 30);
  int sink = 0;
  for (int i = 0; i < 100000; i++) if (AirKillCore.Sheltered(xs[i & 1023], 0, xs[(i + 7) & 1023], xs[(i + 3) & 1023], xs[(i + 5) & 1023], K)) sink++;
  System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
  DamageLedger L = new DamageLedger();
  L.Add(0.1f, 1);
  long mem0 = GC.GetTotalMemory(true); int gc0 = GC.CollectionCount(0);
  sw.Start();
  for (int i = 0; i < 8000000; i++) if (AirKillCore.Sheltered(xs[i & 1023], 0, xs[(i + 7) & 1023], xs[(i + 3) & 1023], xs[(i + 5) & 1023], K)) sink++;
  double ns = sw.Elapsed.TotalMilliseconds * 1e6 / 8000000;
  sw.Reset(); sw.Start();
  float acc = 0;
  for (int i = 0; i < 2000000; i++) { ReleasePlan p = AirKillCore.Plan(xs[i & 1023] / 60f + 0.5f, xs[(i + 1) & 1023] / 60f + 0.5f, 0.3f); acc += p.AlongM; }
  double plan = sw.Elapsed.TotalMilliseconds * 1e6 / 2000000;
  for (int i = 0; i < 2000000; i++) { L.Add(0.0001f, 1 + (i & 7)); if (L.Down) L.Damage = 0f; }
  long mem1 = GC.GetTotalMemory(false); int gc1 = GC.CollectionCount(0);
  Console.WriteLine(string.Format("   pit test {0:0.0} ns/call (x8 pits = one man in one blast), plan {1:0.0} ns, 12 M calls: {2} bytes, {3} GC   [{4}{5:0}]",
    ns, plan, mem1 - mem0, gc1 - gc0, sink > 0 ? "" : "-", acc > -1e30 ? 0 : 1));
  Ok(gc1 - gc0 == 0 && mem1 - mem0 < 4096, "the pit test, the plan and the ledger allocate nothing");
  Ok(ns < 200, "the pit test is far below a microsecond");
 }

 static int Main() {
  Ledger(); Plans(); Sorties(); Pits(); Cost();
  Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
  return failures == 0 ? 0 : 1;
 }
}
'''


def text(name):
    return (ROOT / name).read_text(encoding='utf-8')


def source_checks():
    """The runtime glue the sortie model mirrors, checked in the source."""
    bad = 0

    def ok(good, name):
        nonlocal bad
        print(('PASS ' if good else 'FAIL ') + name)
        if not good:
            bad += 1

    air = text('Revival.AirEvents.cs')
    launch = air[air.index('static GameObject LaunchBomber'):air.index('static GameObject LaunchTransport')]
    ok('AirKills.PlanRelease(damage)' in launch and 'ReleaseKind.Abort' in launch
       and launch.index('ReleaseKind.Abort') < launch.index('Release(go,'),
       'source: the bomber plans at its release point and an abort returns before Release')
    ok('r.Target + right * Offset' in launch and 'Flak.' not in launch and 'Gun(' not in launch,
       'source: the bomb line is the editor target (r.Target), no gun lookup in the bomber')
    rel = air[air.index('static void Release(GameObject plane'):air.index('static float Gauss()')]
    ok('+ miss' in rel and 'scatter' in rel and 'if (Safe(p))' in rel,
       'source: a wide stick moves and scatters, safe zones still hold every bomb')
    tb = air[air.index('static void TickBombs()'):air.index('static float _lastSweepLog')]
    ok('(plane != null && PlayerAn2.Down(plane))) { _bombs.RemoveAt(i); continue; }' in tb,
       'source: a bomb still in the bay of a downed bomber never falls')
    td = air[air.index('static void TickDrops()'):air.index('static void SpawnSquad(Stick s)')]
    ok('s.PlaneDown = s.Plane != null && PlayerAn2.Down(s.Plane);' in td and 'if (s.PlaneDown && master)' in td,
       'source: paratroopers still aboard a downed An-2 go down with it')
    npc = text('Revival.NpcAircraft.cs')
    tick = npc[npc.index('internal static void Tick()'):npc.index('static void Drive(Flight f')]
    ok('if (PlayerAn2.Down(f.Go)) { f.Driving = false; continue; }' in tick and tick.index('PlayerAn2.Down') < tick.index('Drive(f, dt)'),
       'source: a downed NPC aircraft is never driven, so its release / jump point never fires')
    ok('Ledger.Add(dmg, actor)' in npc and 'AirKills.Downed(f, point)' in npc and 'GepardAir.KillNow(f.Go, point)' in npc,
       'source: the master\'s ledger decides the kill and the bounty')
    ok('Damage(go, new Vector3(f[2], f[3], f[4]), amount, sender)' in npc,
       'source: a remote hit is credited to its Photon sender (not to a claim in the payload)')
    gep = text('RevivalGepardCrew.cs')
    hit = gep[gep.index('internal static void Hit(GepardGun.Contact c, Vector3 point, int gunHits)'):gep.index('internal static bool Kill(GameObject go')]
    ok('NpcAircraft.Is(c.Go)' in hit and 'AirKillCore.GunHitDamage(need)' in hit,
       'source: flak and Gepard hits on NPC aircraft go into the one ledger')
    troop = text('RevivalTroopInsertion.cs')
    tk = troop[troop.index('internal bool Tick()'):troop.index('switch (_stage)')]
    ok('if (!_dropped && !Down && _stage >= Stage.Descend) DropNow();' in tk and 'if (Down) return Fall(dt, now);' in tk
       and tk.index('if (Down) return Fall') < tk.index('now - _started > 300f'),
       'source: a shot-down troop Mi-8 never sets its squad down (not even on its timeout)')
    fall = troop[troop.index('bool Fall(float dt, float now)'):]
    fall = fall[:fall.index('void Next(Stage s)')]
    ok('DropNow' not in fall, 'source: the fall and the wreck never drop troops')
    mortar = text('RevivalMortar.cs')
    ok(mortar.count('AirKills.Sheltered(') == 2, 'source: Mortar.Sweep asks the pit rule for NPCs and players')
    kills = text('Revival.AirKills.cs')
    ok('sender != MercAA.MasterActor()' in kills, 'source: a bounty is booked only when the master sent it')
    flak = text('Revival.Flak.cs')
    ok('AirKills.NextShotCredit = g == _manned' in flak and 'merc.Actor' in flak,
       'source: flak kills credit the player at the gun or the merc crew\'s owner')
    prof = text('RevivalFrameProfiler.cs')
    ok('"AirKills.Tick"' in prof and 'S_AirKillsT' in text('RevivalPlugin.cs'), 'source: AirKills.Tick is an F6 slot')
    for name in ('Revival.AirKillCore.cs', 'Revival.AirKills.cs'):
        raw = (ROOT / name).read_bytes()
        ok(all(b < 127 for b in raw) and not raw.startswith(b'\xef\xbb\xbf'), 'source: ' + name + ' is ASCII without a BOM')
    ok(not re.search(r'\bLinq\b|=>', text('Revival.AirKills.cs') + text('Revival.AirKillCore.cs')),
       'source: no LINQ or lambdas (C# 3.0, no closures) in the new files')
    return bad


def main():
    work = ROOT / 'build' / 'air_kills_check'
    work.mkdir(parents=True, exist_ok=True)
    harness = work / 'Harness.cs'
    harness.write_text(HARNESS, encoding='ascii')
    compiler = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    exe = work / 'check.exe'
    built = subprocess.run([str(compiler), '/nologo', '/warn:0', '/optimize+', '/out:' + str(exe),
                            str(ROOT / 'Revival.AirKillCore.cs'), str(harness)], capture_output=True)
    if built.returncode:
        print(built.stdout.decode('utf-8', 'replace'))
        return 1
    run = subprocess.run([str(exe)], capture_output=True)
    print(run.stdout.decode('utf-8', 'replace').rstrip())
    bad = source_checks()
    print('source checks: ' + ('ALL PASS' if bad == 0 else str(bad) + ' FAILED'))
    return 1 if run.returncode or bad else 0


if __name__ == '__main__':
    raise SystemExit(main())

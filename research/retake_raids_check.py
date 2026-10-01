"""W Tower 4: offline proof of the retake raids (docs/ai/tasks/w-tower4-retake-raids.md).

1. Compiles the production core Revival.RetakeRaidsCore.cs UNCHANGED with the
   .NET 3.5 csc into a deterministic simulation and runs it:
   settings parse (defaults, the shipped row, bad rows, clamps), the hold
   clock (first raid, fixed interval, floor, frequency, busy, holder changes,
   switched off), the composition (growth, target rotation, troops held,
   escort, never silent), a six-hour hold with troop lifetimes (the NPC
   bound), and the step cost plus managed allocation of the per-second path.
2. Round-trips airdef.retake_row (the editor) through RetakeSettings.Parse
   (the plugin) for a few settings.
3. Checks the wiring in the sources (plugin seams, F6 slot, AirEvents escort
   and target damage, the Mi-8 seam, the per-frame early-out).

python research/retake_raids_check.py      -> exit 0 when everything passes.
Standard library only, ASCII only.
"""
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

FAILS = []


def ok(cond, what):
    print(("  PASS  " if cond else "  FAIL  ") + what)
    if not cond:
        FAILS.append(what)


def read(name):
    with open(os.path.join(ROOT, name), "rb") as f:
        return f.read().decode("utf-8")


SIM = r'''
namespace NextDayRevival
{
    public static class Sim
    {
        static int _pass, _fail;
        static void Ok(bool c, string what)
        {
            System.Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + what);
            if (c) _pass++; else _fail++;
        }

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--parse") return ParseRows(args);
            Settings();
            Clock();
            Compose();
            Hold();
            Cost();
            System.Console.WriteLine("Retake raid core simulation: " + _pass + " PASS, " + _fail + " FAIL");
            return _fail == 0 ? 0 : 1;
        }

        // Rows from a file, one per line: prints the parsed fields.
        static int ParseRows(string[] args)
        {
            string[] rows = System.IO.File.ReadAllLines(args[1]);
            for (int i = 0; i < rows.Length; i++)
            {
                string err;
                RetakeSettings s = RetakeSettings.Parse(rows[i], out err);
                if (s == null) { System.Console.WriteLine("ERR " + err); continue; }
                System.Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}|{1:0.00}|{2:0.00}|{3}|{4}|{5}{6}{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}",
                    s.Enabled ? 1 : 0, s.Frequency, s.Strength, s.Bombers, s.Bombs,
                    s.HitTower ? "t" : "", s.HitGuns ? "g" : "", s.HitFuel ? "f" : "",
                    s.Transports, s.Paratroopers, s.Helis, s.HeliTroops, s.Escort ? 1 : 0, s.Escorts, s.Faction));
            }
            return 0;
        }

        static void Settings()
        {
            System.Console.WriteLine("Settings");
            string err;
            RetakeSettings d = RetakeSettings.FromTable(new string[] { "# header", "Airfield-raid\t1\t..." }, out err);
            Ok(d != null && err == null && d.Enabled && d.Frequency == 1f && d.Strength == 1f && d.Bombers == 1
               && d.Transports == 2 && d.Helis == 1 && d.Escort && d.Escorts == 2 && d.Faction == "auto"
               && d.HitTower && d.HitGuns && d.HitFuel,
               "no #retake row: the defaults, ON, all three targets, escort on");
            d = RetakeSettings.FromTable(null, out err);
            Ok(d != null && d.Enabled, "no table at all: the defaults, ON");
            string[] shipped = System.IO.File.ReadAllLines(SHIPPED);
            d = RetakeSettings.FromTable(shipped, out err);
            Ok(d != null && err == null && d.Enabled && d.Bombs == 36 && d.Paratroopers == 8 && d.HeliTroops == 8,
               "the shipped assets/ndr_airevents.tsv row parses to the defaults");
            d = RetakeSettings.FromTable(new string[] { "#retake\t1\t1\t1" }, out err);
            Ok(d == null && err != null, "a short row is rejected (settings in force kept): " + err);
            d = RetakeSettings.FromTable(new string[] { "#retake\t1\tx\t1\t1\t36\ttgf\t2\t8\t1\t8\t1\t2\tauto" }, out err);
            Ok(d == null && err != null, "a bad number is rejected: " + err);
            d = RetakeSettings.FromTable(new string[] { "#retake\t1\t1\t1\t1\t36\ttgf\t2\t8\t1\t8\t1\t2\tpirates" }, out err);
            Ok(d == null && err != null, "an unknown side is rejected: " + err);
            d = RetakeSettings.FromTable(new string[] { "#retake\t0\t9\t0.1\t9\t5\tg\t-3\t40\t7\t0\t0\t0\tlooter\r" }, out err);
            Ok(d != null && !d.Enabled && d.Frequency == 4f && d.Strength == 0.5f && d.Bombers == 4 && d.Bombs == 20
               && !d.HitTower && d.HitGuns && !d.HitFuel && d.Transports == 0 && d.Paratroopers == 12 && d.Helis == 3
               && d.HeliTroops == 1 && !d.Escort && d.Escorts == 1 && d.Faction == "looter",
               "out-of-range values are clamped like the editor limits (and CR tolerated)");
            int[] scratch = new int[3];
            RetakeSettings none = new RetakeSettings(); none.HitTower = none.HitGuns = none.HitFuel = false;
            Ok(none.AllowedTargets(scratch) == 1 && scratch[0] == RetakePlan.TargetTower, "no target allowed: the tower");
        }

        static void Clock()
        {
            System.Console.WriteLine("Clock (frequency 1)");
            RetakeSettings s = new RetakeSettings();
            s.RandomWindowMinutes = 0f; // deterministic rhythm; window tested in raid_rhythm_check
            RetakeClock c = new RetakeClock();
            bool any = false;
            for (int t = 0; t < 7200; t++) any |= c.Step(t, -1, s, true, false, 0);
            Ok(!any && c.Next < 0f, "the garrison holds: no raid in 2 h");
            // Side 3 takes it at t = 10000.
            float cap = 10000f;
            int raids = 0; float first = -1f, last = -1f, minGap = 1e9f, firstGap = -1f;
            System.Text.StringBuilder times = new System.Text.StringBuilder();
            for (int t = (int)cap; t < cap + 6 * 3600; t++)
            {
                if (!c.Step(t, 3, s, true, false, 1)) continue;
                if (first < 0f) first = t; else { float g = t - last; if (firstGap < 0f) firstGap = g; if (g < minGap) minGap = g; }
                if (raids < 14) times.Append(((t - cap) / 60f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                last = t;
                raids++;
                c.Flown(t, s);
            }
            System.Console.WriteLine("        raid minutes after the capture: " + times.ToString() + "...");
            Ok(first - cap == 480f, "first raid 8 min after the capture (" + (first - cap) + " s)");
            Ok(System.Math.Abs(firstGap - 1800f) < 2f,
               "then 30 min after air clear (" + firstGap.ToString("0") + " s)");
            Ok(minGap >= 1800f && minGap <= 1802f, "holding longer preserves the interval (" + minGap + " s)");
            Ok(raids >= 11 && raids <= 13, "6 h held: " + raids + " raids (fixed rhythm)");

            RetakeSettings f2 = new RetakeSettings(); f2.Frequency = 2f;
            Ok(RetakePlan.FirstDelaySeconds(f2) == 240f && RetakePlan.GapSeconds(f2, 0f) == 900f
               && RetakePlan.GapSeconds(f2, 10f) == 900f, "frequency 2: first 4 min, fixed gap 15 min");
            RetakeSettings f4 = new RetakeSettings(); f4.Frequency = 4f;
            Ok(RetakePlan.GapSeconds(f4, 10f) == 600f && RetakePlan.FirstDelaySeconds(f4) == 180f,
               "frequency 4: quiet floor 10 min, first delay floor 3 min");
            RetakeSettings slow = new RetakeSettings(); slow.Frequency = 0.25f;
            Ok(RetakePlan.FirstDelaySeconds(slow) == 1920f && RetakePlan.GapSeconds(slow, 0f) == 7200f,
               "frequency 0.25: first 32 min, gap 2 h");

            RetakeClock b = new RetakeClock();
            b.Step(0f, 5, s, true, false, 0);
            Ok(!b.Step(480f, 5, s, true, true, 0) && b.AwaitClear, "a due raid while air is active waits for clear");
            Ok(!b.Step(540f, 5, s, true, false, 0) && b.Next == 2340f, "... and starts a full quiet interval on clear");
            Ok(b.Step(2340f, 5, s, true, false, 0), "... and goes after the quiet interval");
            b.Flown(2340f, s);
            Ok(b.Level == 1, "flown: level 1");
            Ok(!b.Step(600f, -1, s, true, false, 0) && b.Next < 0f && b.Level == 0, "garrison back: the raids stop, level reset");
            b.Step(700f, 5, s, true, false, 0);
            Ok(b.Next == 700f + 480f, "taken again: a fresh first delay");
            b.Step(710f, 6, s, true, false, 0);
            Ok(b.Next == 710f + 480f && b.Level == 0 && b.HeldSince == 710f, "another side takes it: starts over");
            RetakeSettings off = new RetakeSettings(); off.Enabled = false;
            RetakeClock o = new RetakeClock();
            bool fired = false;
            for (int t = 0; t < 7200; t++) fired |= o.Step(t, 2, off, true, false, 0);
            Ok(!fired, "off in the editor: no raid in 2 h held");
            o = new RetakeClock();
            for (int t = 0; t < 7200; t++) fired |= o.Step(t, 2, s, false, false, 0);
            Ok(!fired, "host switch off: no raid in 2 h held");
            fired = false;
            for (int t = 7200; t < 7200 + 481; t++) fired |= o.Step(t, 2, s, true, false, 0);
            Ok(fired, "switched on again while held: a raid a first delay later");
        }

        static void Compose()
        {
            System.Console.WriteLine("Composition (defaults, strength 1)");
            RetakeSettings s = new RetakeSettings();
            int[] scratch = new int[3];
            string seq = "";
            for (int l = 0; l < 8; l++)
            {
                RetakeRaid r = RetakePlan.Compose(s, l, 0, false, scratch);
                seq += "L" + l + ":" + r.Escorts + "e/" + r.Bombers + "b/" + r.Transports + "t/" + r.Helis + "h ";
            }
            System.Console.WriteLine("        " + seq);
            RetakeRaid r0 = RetakePlan.Compose(s, 0, 0, false, scratch);
            Ok(r0.Bombers == 1 && r0.Transports == 2 && r0.Helis == 1 && r0.Escorts == 2 && r0.Target == RetakePlan.TargetTower,
               "raid 1: 2 escort, 1 Tu-95 on the tower, 2 An-2, 1 Mi-8");
            RetakeRaid r6 = RetakePlan.Compose(s, 6, 0, false, scratch);
            RetakeRaid r20 = RetakePlan.Compose(s, 20, 0, false, scratch);
            Ok(r6.Bombers == 3 && r6.Escorts == 4 && r6.Transports >= 2 && r6.Helis >= 2 && r6.Troops <= RetakePlan.MaxTroops,
               "raid 7: 3 Tu-95, 4 escort, " + r6.Transports + " An-2 + " + r6.Helis + " Mi-8 (grown, troops capped)");
            Ok(r20.Bombers == r6.Bombers && r20.Transports == r6.Transports, "growth stops after six raids");
            Ok(r0.Troops == 24 && r6.Troops == 32, "troops per raid: " + r0.Troops + " .. " + r6.Troops + " (cap " + RetakePlan.MaxTroops + ")");
            string rot = "";
            for (int l = 0; l < 6; l++) rot += RetakePlan.TargetNames[RetakePlan.Compose(s, l, 1, false, scratch).Target] + ", ";
            Ok(rot == "guns, fuel depot, tower, guns, fuel depot, tower, ", "targets rotate from the hold's start: " + rot);
            RetakeSettings fuelOnly = new RetakeSettings(); fuelOnly.HitTower = false; fuelOnly.HitGuns = false;
            Ok(RetakePlan.Compose(fuelOnly, 3, 2, false, scratch).Target == RetakePlan.TargetFuel, "only the fuel depot allowed: always it");
            RetakeRaid held = RetakePlan.Compose(s, 2, 0, true, scratch);
            Ok(held.Transports == 0 && held.Helis == 0 && held.Bombers > 0 && held.Escorts > 0 && held.TroopsHeld,
               "troops of the last raid still fight: bombs and escort only");
            RetakeSettings noBomb = new RetakeSettings(); noBomb.Bombers = 0;
            RetakeRaid nb = RetakePlan.Compose(noBomb, 0, 0, true, scratch);
            Ok(nb.Bombers == 1, "nothing left to send (no bombers, troops held): one Tu-95, never silent");
            RetakeSettings noEsc = new RetakeSettings(); noEsc.Escort = false;
            Ok(RetakePlan.Compose(noEsc, 4, 0, false, scratch).Escorts == 0, "escort off: none");
            RetakeSettings strong = new RetakeSettings(); strong.Strength = 3f;
            RetakeRaid st = RetakePlan.Compose(strong, 6, 0, false, scratch);
            Ok(st.Bombers == RetakePlan.MaxBombers && st.Escorts == RetakePlan.MaxEscorts && st.Troops <= RetakePlan.MaxTroops
               && st.Transports >= 1 && st.Helis >= 1,
               "strength 3 at raid 7: 6 Tu-95, 4 escort, " + st.Transports + " An-2 + " + st.Helis + " Mi-8 = " + st.Troops + " troops (capped)");
            RetakeSettings heavy = new RetakeSettings(); heavy.Transports = 4; heavy.Paratroopers = 12; heavy.Helis = 3; heavy.HeliTroops = 12;
            RetakeRaid hv = RetakePlan.Compose(heavy, 0, 0, false, scratch);
            Ok(hv.Troops <= RetakePlan.MaxTroops && hv.Transports >= 1 && hv.Helis >= 1,
               "editor maxima (4 x 12 + 3 x 12): cut to " + hv.Transports + " An-2 + " + hv.Helis + " Mi-8 = " + hv.Troops);
            RetakeSettings weak = new RetakeSettings(); weak.Strength = 0.5f;
            RetakeRaid wk = RetakePlan.Compose(weak, 0, 0, false, scratch);
            Ok(wk.Bombers == 1 && wk.Transports == 1 && wk.Helis == 1 && wk.Escorts == 1, "strength 0.5: at least one of each asked for");
        }

        // Six hours held; each raid's troops fight for 30 min (25 min patrol
        // + the approach). How many retake NPCs are alive at once at most?
        static void Hold()
        {
            System.Console.WriteLine("Six-hour hold (troops live 30 min)");
            RetakeSettings[] cases = { new RetakeSettings(), new RetakeSettings(), new RetakeSettings() };
            cases[1].Frequency = 4f; cases[2].Strength = 3f; cases[2].Frequency = 2f;
            string[] names = { "defaults", "frequency 4", "strength 3 + frequency 2" };
            int[] scratch = new int[3];
            for (int k = 0; k < cases.Length; k++)
            {
                RetakeSettings s = cases[k];
                RetakeClock c = new RetakeClock();
                System.Collections.Generic.List<float> until = new System.Collections.Generic.List<float>();
                System.Collections.Generic.List<int> men = new System.Collections.Generic.List<int>();
                int raids = 0, peak = 0, withTroops = 0;
                for (int t = 0; t < 6 * 3600; t++)
                {
                    for (int i = until.Count - 1; i >= 0; i--) if (t >= until[i]) { until.RemoveAt(i); men.RemoveAt(i); }
                    bool busyTroops = until.Count > 0;
                    // an air raid is in the air ~4 min after it starts
                    if (!c.Step(t, 1, s, true, false, 0)) continue;
                    RetakeRaid r = RetakePlan.Compose(s, c.Level, c.Start, busyTroops, scratch);
                    c.Flown(t, s);
                    raids++;
                    if (r.Troops > 0) { until.Add(t + 1800f); men.Add(r.Troops); withTroops++; }
                    int alive = 0; for (int i = 0; i < men.Count; i++) alive += men[i];
                    if (alive > peak) peak = alive;
                }
                Ok(peak <= RetakePlan.MaxTroops, names[k] + ": " + raids + " raids, " + withTroops
                   + " with troops, at most " + peak + " retake NPCs at once (one raid's worth)");
            }
        }

        static void Cost()
        {
            System.Console.WriteLine("Cost of the master's 1 Hz step");
            RetakeSettings s = new RetakeSettings();
            RetakeClock c = new RetakeClock();
            int[] scratch = new int[3];
            // warm up
            for (int i = 0; i < 1000; i++) { c.Step(i, 2, s, true, false, 0); RetakePlan.Compose(s, i & 7, 0, false, scratch); }
            const int N = 1000000;
            System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
            long before = System.GC.GetTotalMemory(false);
            int gen0 = System.GC.CollectionCount(0);
            System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
            int due = 0;
            for (int i = 0; i < N; i++)
            {
                if (c.Step(1000 + i, 2, s, true, false, 0)) { due++; c.Flown(1000 + i, s); }
            }
            w.Stop();
            double stepUs = w.Elapsed.TotalMilliseconds * 1000.0 / N;
            w = System.Diagnostics.Stopwatch.StartNew();
            int sum = 0;
            for (int i = 0; i < N; i++) sum += RetakePlan.Compose(s, i & 7, i, (i & 1) == 0, scratch).Bombers;
            w.Stop();
            double composeUs = w.Elapsed.TotalMilliseconds * 1000.0 / N;
            long after = System.GC.GetTotalMemory(false);
            int gcs = System.GC.CollectionCount(0) - gen0;
            System.Console.WriteLine("        Step " + stepUs.ToString("0.0000") + " us, Compose " + composeUs.ToString("0.0000")
                + " us, " + (after - before) + " bytes, " + gcs + " gen0 GCs over 2x" + N + " calls (" + due + " due, " + sum + ")");
            Ok(stepUs < 1.0, "clock step < 1 us (" + stepUs.ToString("0.0000") + " us; runs once a second)");
            Ok(composeUs < 2.0, "compose < 2 us (" + composeUs.ToString("0.0000") + " us; once per raid)");
            // GetTotalMemory moves in 8 KB allocation quanta (the two Stopwatch
            // objects); one byte per call would be 2 MB and several gen0 GCs.
            Ok(gcs == 0 && after - before <= 8192, "no managed allocation in step or compose (0 GCs, <= one 8 KB quantum over 2M calls)");
        }
    }
}
'''


def compile_and_run():
    compiler = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Microsoft.NET", "Framework", "v3.5", "csc.exe")
    if not os.path.exists(compiler):
        ok(False, "csc 3.5 found at %s" % compiler)
        return None
    work = os.path.join(ROOT, "build", "retake_raids_check")    # not TEMP: its ACL fails under the runner
    if os.path.isdir(work):
        shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    core = read("Revival.RetakeRaidsCore.cs")
    shipped = os.path.join(ROOT, "assets", "ndr_airevents.tsv").replace("\\", "\\\\")
    sim = SIM.replace("SHIPPED", '"' + shipped + '"')
    cs_core = os.path.join(work, "core.cs")
    cs_sim = os.path.join(work, "sim.cs")
    exe = os.path.join(work, "sim.exe")
    with open(cs_core, "wb") as f:
        f.write(core.encode("utf-8"))      # PRODUCTION, unchanged
    with open(cs_sim, "wb") as f:
        f.write(sim.encode("utf-8"))
    built = subprocess.run([compiler, "/nologo", "/warn:0", "/optimize+", "/codepage:65001",
                            "/main:NextDayRevival.Sim", "/out:" + exe, cs_core, cs_sim],
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if built.returncode != 0:
        print(built.stdout.decode("utf-8", "replace"))
        ok(False, "the production core compiles with csc 3.5")
        return None
    ok(True, "the production core Revival.RetakeRaidsCore.cs compiles unchanged with csc 3.5")
    ran = subprocess.run([exe], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    print(ran.stdout.decode("utf-8", "replace").rstrip())
    ok(ran.returncode == 0, "the core simulation passes")
    return exe


def round_trip(exe):
    print("Editor -> plugin round trip (airdef.retake_row -> RetakeSettings.Parse)")
    import airdef
    cases = [
        {},
        {"retakeRaids": {"enabled": False}},
        {"retakeRaids": {"frequency": 2.5, "strength": 0.75, "bombers": 3, "bombs": 60, "targets": ["guns", "fuel"],
                         "transports": 0, "paratroopers": 12, "helis": 3, "heliTroops": 4, "escort": False,
                         "escorts": 4, "faction": "neutral"}},
        {"retakeRaids": {"targets": ["fuel"], "faction": "traitor", "bombers": 0}},
    ]
    rows, want = [], []
    for d in cases:
        probs = []
        airdef.validate_events(d, lambda w, m: probs.append(w + ": " + m))
        ok(not probs, "editor settings validate: %s" % (d.get("retakeRaids") or "defaults"))
        r = airdef.retake(d)
        rows.append(airdef.retake_row(d))
        letters = "".join(x[0] for x in airdef.RETAKE_TARGETS if x in r["targets"])
        want.append("%d|%.2f|%.2f|%d|%d|%s|%d|%d|%d|%d|%d|%d|%s" % (
            1 if r["enabled"] else 0, r["frequency"], r["strength"], r["bombers"], r["bombs"], letters,
            r["transports"], r["paratroopers"], r["helis"], r["heliTroops"], 1 if r["escort"] else 0,
            r["escorts"], r["faction"]))
    path = os.path.join(os.path.dirname(exe), "rows.tsv")
    with open(path, "wb") as f:
        f.write(("\n".join(rows) + "\n").encode("ascii"))
    ran = subprocess.run([exe, "--parse", path], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    got = ran.stdout.decode("utf-8", "replace").strip().splitlines()
    for i in range(len(cases)):
        ok(i < len(got) and got[i].strip() == want[i], "row %d: %s" % (i + 1, got[i].strip() if i < len(got) else "missing"))
    bad = []
    airdef.validate_events({"retakeRaids": {"frequency": 9, "targets": [], "faction": "x", "escort": 1}},
                           lambda w, m: bad.append(m))
    ok(len(bad) == 4, "the editor refuses bad settings (%d problems)" % len(bad))
    shipped = read(os.path.join("assets", "ndr_airevents.tsv"))
    ok("\n#retake\t" in shipped, "assets/ndr_airevents.tsv carries the #retake row")
    ok(all(not l.startswith("#retake") or l.startswith("#") for l in shipped.splitlines()),
       "the row is a '#' comment for an older plugin (AirEvents.Parse skips it)")


def wiring():
    print("Wiring")
    plug = read("RevivalPlugin.cs")
    prof = read("RevivalFrameProfiler.cs")
    air = read("Revival.AirEvents.cs")
    mod = read("Revival.RetakeRaids.cs")
    troop = read("RevivalTroopInsertion.cs")
    radar = read("Revival.TowerRadar.cs")
    ok("RetakeRaids.BindConfig(Config);" in plug, "config bound by the plugin")
    ok("FrameProf.S(FrameProf.S_RetakeRaidsT); RetakeRaids.Tick(); FrameProf.E(FrameProf.S_RetakeRaidsT);" in plug,
       "ticked inside its own F6 slot")
    count = int(re.search(r"public const int Count = (\d+);", prof).group(1))
    slot = int(re.search(r"public const int S_RetakeRaidsT = (\d+);", prof).group(1))
    names = re.search(r"static readonly string\[\] Names = new string\[\]\s*\{(.*?)\};", prof, re.S).group(1)
    listed = re.findall(r'"([^"]+)"', names)
    ok(slot < count and len(listed) == count and listed[slot] == "RetakeRaids.Tick",
       "F6 slot %d 'RetakeRaids.Tick', %d names for %d slots" % (slot, len(listed), count))
    tick = mod[mod.index("internal static void Tick()"):]
    head = tick[:tick.index("try")]
    ok("if (now < _nextStep) return;" in head and "new " not in head and "+" not in head.replace("+ 1f", ""),
       "per frame: an enabled check and one time compare before the 1 Hz step, no allocation")
    ok("RetakeRaids.Table(lines, label);" in air and "RetakeRaids.Table(null, label);" in air,
       "the air event table's #retake row reaches the module (live and offline)")
    ok("RetakeRaids.RequestTemplate" in air and "RetakeRaids.Now(" in air and "RetakeRaids.Ask(Quick)" in air,
       "admin: Retake raid now (a non-host admin asks the host)")
    ok("LaunchEscort(r, s)" in air and "EscortTag" in air and "b.Light = _light.Contains(view);" in air,
       "AirEvents: escort An-2 wave with light bombs over each gun")
    # Merged with W AA7 / W bomb2: the radar HQ (and the guns) are billed by
    # OrdnanceBlast.Enqueue -> AirDefenceDamage.ReportBlast, not a second call here.
    ordnance = read("Revival.OrdnanceBlast.cs")
    ok("FuelDepot.Blast(at, vehPeak, radius);" in air and "OrdnanceBlast.Enqueue(at, radius" in air
       and "AirDefenceDamage.ReportBlast(point, radiusU" in ordnance,
       "bombs damage the POL depot and (through the ordnance queue) the tower radar HQ and the guns")
    ok("internal static bool LaunchExternal(Landing d)" in troop and "RevivalTroopInsertion.LaunchExternal(h.Landing)" in mod,
       "Mi-8 troop landings through the heli landing's own path")
    ok("internal static int ActiveWithPrefix(string prefix)" in read("Revival.NpcCombat.cs"),
       "NpcWar counts the retake squads (no new troops while they fight)")
    ok(all(ord(ch) < 128 for ch in mod) and all(ord(ch) < 128 for ch in read("Revival.RetakeRaidsCore.cs")),
       "new sources are ASCII")
    sync = read("sync_public.py")
    ok('"Revival.RetakeRaids.cs", "Revival.RetakeRaidsCore.cs"' in sync, "the public package ships both sources")
    for name in ("Revival.RetakeRaids.cs", "Revival.RetakeRaidsCore.cs", "research/retake_raids_check.py",
                 "airdef.py", "editor/air.js"):
        ok(b"\r\n" not in open(os.path.join(ROOT, name), "rb").read(), name + " is LF")


def main():
    print("W Tower 4 retake raids - offline check")
    exe = compile_and_run()
    if exe:
        round_trip(exe)
    wiring()
    print("")
    if FAILS:
        print("RESULT: %d FAIL" % len(FAILS))
        for f in FAILS:
            print("  - " + f)
        return 1
    print("RESULT: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())

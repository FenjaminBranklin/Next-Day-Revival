"""C4 demolition charge: compile the production pure core (Revival.C4Core.cs)
with the headless test tests/c4_core_test.cs on csc 3.5 and run it - chain
radius, per-player limit, timer steps and beep cadence, object charge shares,
defuse time. Then pin the runtime seams in Revival.C4.cs to that core.
No game, installation or Unity needed. Output stays in build/c4-check.
Prints "N PASS, M FAIL" as its last line; exit code 0 only without FAIL.
"""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def compiler():
    win = os.environ.get("WINDIR", r"C:\Windows")
    for fw in ("Framework64", "Framework"):
        p = Path(win) / "Microsoft.NET" / fw / "v3.5" / "csc.exe"
        if p.exists():
            return p
    return None


def main():
    fails, passes = [], [0]

    def ok(cond, what):
        if cond:
            passes[0] += 1
        else:
            fails.append(what)
            print("FAIL " + what)

    csc = compiler()
    if csc is None:
        print("SKIP .NET 3.5 csc not found")
        print("0 PASS, 0 FAIL")
        return 0
    build = ROOT / "build" / "c4-check"
    build.mkdir(parents=True, exist_ok=True)
    exe = build / "C4CoreTest.exe"
    r = subprocess.run([str(csc), "/nologo", "/warn:0", "/out:" + str(exe),
                        str(ROOT / "Revival.C4Core.cs"), str(ROOT / "tests" / "c4_core_test.cs")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode:
        print("FAIL compile: " + (r.stdout + r.stderr).strip()[-600:])
        print("0 PASS, 1 FAIL")
        return 1
    run = subprocess.run([str(exe)], capture_output=True, text=True)
    out = run.stdout.strip().splitlines()
    for line in out[:-1]:
        print(line)
    last = out[-1] if out else ""
    ok(run.returncode == 0 and last.endswith(" 0 FAIL"), "core test: " + last)
    try:
        passes[0] += int(last.split(" PASS")[0])
    except ValueError:
        pass

    # Seams: the runtime decides with the core, not with a private copy.
    src = (ROOT / "Revival.C4.cs").read_text(encoding="utf-8")
    for needle, what in (
            ("C4Core.Chain(", "chain reaction through C4Core.Chain"),
            ("C4Core.MayPlace(", "per-player limit through C4Core.MayPlace"),
            ("C4Core.StepTimer(", "mouse-wheel timer through C4Core.StepTimer"),
            ("C4Core.ClampTimer(", "packet timer clamped by C4Core.ClampTimer"),
            ("C4Core.BeepInterval(", "beep cadence from C4Core.BeepInterval"),
            ("C4Core.ChargeShare(", "object damage from C4Core.ChargeShare"),
            ("C4Core.DefuseSeconds(", "defuse time from C4Core.DefuseSeconds"),
            ("C4Core.InRange(", "radio range from C4Core.InRange")):
        ok(needle in src, what)
    ok('"MaxPerPlayer", 5' in src, "default limit is 5 charges per player")
    ok('"ChainRadius", 8f' in src, "default chain radius is 8 m")
    print("%d PASS, %d FAIL" % (passes[0], len(fails)))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())

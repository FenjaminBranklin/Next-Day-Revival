"""Compile production fetch sources unchanged with deterministic engine doubles."""
from pathlib import Path
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build" / "merc-fetch-check"
SOURCES = ["Revival.MercFetchCore.cs", "Revival.MercTargetCore.cs", "Revival.MercFetchBridge.cs",
           "Revival.MercFetchNative.cs", "Revival.MercFetch.cs",
           "Revival.FastField.cs",
           "research/merc_fetch_harness.cs"]


def main():
    BUILD.mkdir(parents=True, exist_ok=True)
    csc = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v3.5/csc.exe"
    exe = BUILD / "merc-fetch.exe"
    result = subprocess.run([str(csc), "/nologo", "/optimize+", "/warn:0",
                             "/codepage:65001", "/out:" + str(exe)] +
                            [str(ROOT / s) for s in SOURCES], capture_output=True, text=True, errors="replace")
    (BUILD / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        print(result.stdout + result.stderr)
        return result.returncode
    run = subprocess.run([str(exe)], capture_output=True, text=True, errors="replace")
    (BUILD / "simulation.log").write_text(run.stdout + run.stderr, encoding="utf-8")
    print(run.stdout + run.stderr, end="")
    if run.returncode:
        return run.returncode
    ui = (ROOT / "Revival.MercsUi.cs").read_text(encoding="utf-8")
    plugin = (ROOT / "RevivalPlugin.cs").read_text(encoding="utf-8")
    quick = (ROOT / "Revival.MercQuickOrders.cs").read_text(encoding="utf-8")
    profiler = (ROOT / "RevivalFrameProfiler.cs").read_text(encoding="utf-8")
    assert '"FETCH AIRDROP"' in ui and "case 9: MercFetch.Start();" in ui
    # G O2 uses local submenu shortcuts; FETCH retains dispatch ID 9.
    radial = (ROOT / "Revival.MercRadialCore.cs").read_text(encoding="ascii")
    assert "new int[] { 10, 4, 9 }" in radial
    assert "n <= count" in quick and "Input.GetKeyDown(KeyCode.Alpha0 + n)" in quick
    assert "Issue(MercRadialPlan.Order(_wheelGroup, n - 1))" in quick
    assert "MercFetch.BindConfig(Config)" in plugin and "MercFetch.Install(_harmony)" in plugin
    assert "S_MercFetchT); MercFetch.Tick();" in plugin
    assert "S_MercFetchT" in profiler and '"MercFetch.Tick"' in profiler
    slots = re.findall(r"public const int S_\w+ = (\d+);", profiler)
    assert len(slots) == len(set(slots)), "duplicate profiler slot"
    native = (ROOT / "Revival.MercFetchNative.cs").read_text(encoding="utf-8")
    assert "FindObjectsOfType" not in native and "QueryTriggerInteraction.Ignore" in native
    assert "Hits, 11.2f, ~0" in native and "n == Hits.Length" in native
    assert "Physics.OverlapBoxNonAlloc" in native and "Quaternion.identity, ~0" in native
    print("PASS wiring: selected-only command, SUPPORT 3/3 shortcut, optional prerequisites, unique F6 slot, all-collider nonalloc landing, native cargo/depot sync")
    return 0


if __name__ == "__main__":
    sys.exit(main())

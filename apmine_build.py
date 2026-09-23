"""Build the PMN-2 anti-personnel mine (item 1492) with Blender.

Blender is the modelling source of truth, as for the crocodile:
assets/src/apmine_blender.py builds the mesh, unwraps it, bakes its
procedural materials into the runtime maps, writes apmine.ndmesh, renders the
inventory icon and a review image from the exported maps, and saves the
editable assets/src/apmine.blend plus a portable GLB in one background run.

    python apmine_build.py

Then build.ps1 and verify.py.
"""

import glob
import os
import shutil
import subprocess
import sys


ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(ROOT, "assets", "src", "apmine_blender.py")
OUTPUTS = (
    os.path.join(ROOT, "assets", "apmine.ndmesh"),
    os.path.join(ROOT, "assets", "apmine_diffuse.png"),
    os.path.join(ROOT, "assets", "apmine_normal.png"),
    os.path.join(ROOT, "assets", "apmine_metal.png"),
    os.path.join(ROOT, "assets", "apmine_rough.png"),
    os.path.join(ROOT, "assets", "apmine_icon.png"),
    os.path.join(ROOT, "assets", "src", "apmine.blend"),
    os.path.join(ROOT, "assets", "src", "apmine.glb"),
    os.path.join(ROOT, "assets", "src", "apmine_preview.png"),
)


def blender_executable():
    configured = os.environ.get("BLENDER_EXE", "").strip()
    candidates = []
    if configured:
        candidates.append(configured)
    found = shutil.which("blender")
    if found:
        candidates.append(found)
    for base in (os.environ.get("ProgramFiles", r"C:\Program Files"),
                 os.environ.get("ProgramW6432", r"C:\Program Files")):
        candidates.extend(sorted(glob.glob(os.path.join(
            base, "Blender Foundation", "Blender *", "blender.exe")), reverse=True))
    for candidate in candidates:
        if candidate and os.path.isfile(candidate):
            return candidate
    raise SystemExit(
        "Blender not found. Install Blender or set BLENDER_EXE to blender.exe."
    )


def main():
    blender = blender_executable()
    print("Blender: " + blender)
    result = subprocess.run([
        blender,
        "--background",
        "--factory-startup",
        "--python-exit-code",
        "1",
        "--python",
        SCRIPT,
    ], cwd=ROOT)
    if result.returncode != 0:
        raise SystemExit("PMN-2 Blender build failed with code %d" % result.returncode)

    missing = [path for path in OUTPUTS if not os.path.isfile(path)]
    if missing:
        raise SystemExit("PMN-2 Blender build omitted: " + ", ".join(missing))
    for path in OUTPUTS:
        print("  %-40s %8d bytes" %
              (os.path.relpath(path, ROOT).replace("\\", "/"), os.path.getsize(path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

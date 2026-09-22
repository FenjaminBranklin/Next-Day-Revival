"""Build the toxic crocodile with Blender.

The runtime asset is an ndmesh because the Unity 2018 client cannot load a
modern blend or glTF directly.  Blender remains the modelling source of truth:
assets/src/crocodile_blender.py creates the mesh, UVs, materials, textures,
preview, and assets/src/crocodile.blend in one deterministic background run.
"""

import glob
import os
import shutil
import subprocess
import sys


ROOT = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(ROOT, "assets", "src", "crocodile_blender.py")
OUTPUTS = (
    os.path.join(ROOT, "assets", "crocodile.ndmesh"),
    os.path.join(ROOT, "assets", "crocodile_rig.bin"),
    os.path.join(ROOT, "assets", "crocodile_diffuse.png"),
    os.path.join(ROOT, "assets", "crocodile_normal.png"),
    os.path.join(ROOT, "assets", "src", "crocodile.blend"),
    os.path.join(ROOT, "assets", "src", "crocodile_preview.png"),
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
        raise SystemExit("crocodile Blender build failed with code %d" % result.returncode)

    missing = [path for path in OUTPUTS if not os.path.isfile(path)]
    if missing:
        raise SystemExit("crocodile Blender build omitted: " + ", ".join(missing))
    for path in OUTPUTS:
        print("  %-45s %8d bytes" %
              (os.path.relpath(path, ROOT).replace("\\", "/"), os.path.getsize(path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

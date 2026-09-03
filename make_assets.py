"""Erzeugt alle Assets des Toolkits in der richtigen Reihenfolge.

Reihenfolge ist nicht beliebig: die Icons rendern das Mesh MIT der Diffusetextur,
also muessen Mesh und Textur vorher fertig sein. Wer nur mg42_icon.py laufen
laesst, bekommt ein Icon des vorigen Modells.

    python make_assets.py            alles neu bauen
    python make_assets.py mg42       nur eine Gruppe (mg42, sniper50, ammo, scope)

Danach:
    powershell -File build.ps1       uebersetzen und installieren
    python verify.py                 statische Kontrolle
"""

import os
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.abspath(__file__))

GROUPS = [
    # mg42/sniper50 now come from REAL imported models via textured_import.py
    # (barrett_build.py -> the TAC-50's .50 cal look, mg42real_build.py -> a real
    # MG42), replacing the procedural *_mesh/_texture/_icon generators, which
    # stay on disk (like t72_mesh/_texture) but no longer run.
    ("mg42", ["mg42real_build.py"]),
    ("sniper50", ["barrett_build.py"]),
    # m7_build.py imports a real CC-BY SIG XM7 model (assets/src/sig_xm7.glb)
    # and bakes mesh + palette texture + icons in one pass - see CREDITS.md.
    ("m7", ["m7_build.py"]),
    # 6.8x51 box + drum magazines for the M7 - procedural, FDE-tan polymer.
    ("mag68", ["mag68_build.py"]),
    ("ammo", ["ammo_mesh.py", "ammo_texture.py", "ammo_icon.py"]),
    ("law", ["law_mesh.py", "law_texture.py", "law_icon.py"]),
    ("rocket", ["rocket_mesh.py", "rocket_texture.py", "rocket_icon.py"]),
    ("drone", ["drone_mesh.py", "drone_texture.py", "drone_icon.py"]),
    ("jammer", ["jammer_mesh.py", "jammer_texture.py", "jammer_icon.py"]),
    # t72_import.py hat t72_mesh.py und t72_texture.py abgeloest: der Panzer
    # kommt seit 0.5.3 als Modell UND Textur aus dem Spiel selbst. Die beiden
    # Generatoren bleiben liegen, laufen aber nicht mehr mit.
    ("t72", ["t72_import.py", "t72_track_texture.py", "t72_scope.py",
             "shell125_mesh.py", "shell125_texture.py", "shell125_icon.py",
             "mesh_preview.py t72"]),
    # SWAT uniform gear (helmet + carved top/bottom/backpack) from swat.glb.
    ("swat", ["swat_build.py"]),
    ("scope", ["scope50.py", "apc_scope.py"]),
    ("preview", ["mesh_preview.py"]),
]


def run(script):
    """Ein Eintrag ist "skript.py" oder "skript.py argument"."""
    print("-" * 74)
    print(">>> " + script)
    print("-" * 74)
    teile = script.split()
    r = subprocess.run([sys.executable, os.path.join(ROOT, teile[0])] + teile[1:],
                       cwd=ROOT, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if r.stdout:
        print(r.stdout.rstrip())
    if r.returncode != 0:
        if r.stderr:
            print(r.stderr.rstrip())
        raise SystemExit("ABBRUCH: %s endete mit Code %d" % (script, r.returncode))


if __name__ == "__main__":
    wanted = sys.argv[1:]
    ran = 0
    for name, scripts in GROUPS:
        if wanted and name not in wanted:
            continue
        for s in scripts:
            run(s)
            ran += 1
    if ran == 0:
        raise SystemExit("nichts zu tun - bekannte Gruppen: "
                         + ", ".join(g[0] for g in GROUPS))
    print("=" * 74)
    print("%d Skripte gelaufen. Weiter mit:" % ran)
    print("  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1")
    print("  python verify.py")

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
    ("mg42", ["mg42_mesh.py", "mg42_texture.py", "mg42_icon.py"]),
    ("sniper50", ["sniper50_mesh.py", "sniper50_texture.py", "sniper50_icon.py"]),
    ("ammo", ["ammo_mesh.py", "ammo_texture.py", "ammo_icon.py"]),
    ("law", ["law_mesh.py", "law_texture.py", "law_icon.py"]),
    ("rocket", ["rocket_mesh.py", "rocket_texture.py", "rocket_icon.py"]),
    ("scope", ["scope50.py"]),
    ("preview", ["mesh_preview.py"]),
]


def run(script):
    print("-" * 74)
    print(">>> " + script)
    print("-" * 74)
    r = subprocess.run([sys.executable, os.path.join(ROOT, script)],
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

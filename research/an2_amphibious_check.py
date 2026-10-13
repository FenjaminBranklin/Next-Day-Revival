"""Run geometry tests and the production C# 3.0 amphibious loader harness."""
from pathlib import Path
import shutil
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'build/an2-amphibious-check'


def run():
    subprocess.run([sys.executable, '-m', 'unittest', 'tests.test_an2_amphibious'], cwd=ROOT, check=True)
    OUT.mkdir(parents=True, exist_ok=True)
    source = (ROOT/'Revival.PlayerAn2.cs').read_text(encoding='utf-8')
    start = source.index('                Vector3 offset = An2Amphibious.Offset;')
    end = source.index('\n\n', start)
    adjustment = source[start:end]
    harness = (ROOT/'tests/an2_amphibious_harness.cs').read_text(encoding='ascii')
    generated = OUT/'Harness.cs'
    generated.write_text(harness.replace('/*RIG_ADJUSTMENT*/', adjustment), encoding='ascii')
    exe = OUT/'amphibious.exe'
    subprocess.run(['C:/Windows/Microsoft.NET/Framework64/v3.5/csc.exe',
                    '/nologo', '/out:'+str(exe),
                    str(ROOT/'Revival.An2Amphibious.cs'), str(generated)], check=True)
    subprocess.run([str(exe), str(ROOT/'assets')], check=True)
    # Each negative case runs in a fresh process (no prior loader cache).
    names = ['an2_body.ndmesh', 'an2_amphib_rig.txt', 'an2_amphib_floats.ndmesh',
             'an2_amphib_main.ndmesh', 'an2_amphib_rear.ndmesh',
             'an2_amphib_diffuse.png', 'an2_amphib_normal.png']
    rig = (ROOT/'assets/an2_amphib_rig.txt').read_text(encoding='ascii')
    cases = [('body-revision', rig.replace('body ', 'body 0', 1), 'body changed', None),
             ('scale', rig.replace('scale 2.8 5.0', 'scale 3.84 5.0'), 'incomplete amphibious rig', None),
             ('overlap', re.sub(r'^remove \d+', 'remove 0', rig, flags=re.MULTILINE), 'invalid land-gear mask range', None),
             ('wheel-count', '\n'.join(r for r in rig.splitlines() if not r.startswith('wheel rear_l')), 'incomplete amphibious rig', None),
             ('missing-mesh', rig, 'missing amphibious kit assets', 'an2_amphib_main.ndmesh'),
             ('missing-texture', rig, 'missing amphibious kit assets', 'an2_amphib_normal.png')]
    for label, text, message, missing in cases:
        assets = OUT/label
        assets.mkdir(exist_ok=True)
        for name in names:
            if name == missing:
                continue
            shutil.copyfile(ROOT/'assets'/name, assets/name)
        (assets/'an2_amphib_rig.txt').write_text(text, encoding='ascii')
        # Prior case folders are reused safely; omitted files are not created.
        subprocess.run([str(exe), str(assets), message], check=True)
    print('PASS production C# 3 loader, gear, contact/seat/hull alignment and failure paths')


if __name__ == '__main__':
    run()

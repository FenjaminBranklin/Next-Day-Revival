"""Compile the air gunners core with csc 3.5 and run its offline test.

Revival.AirGunnersCore.cs (weapon rating, station arcs, orbit guidance) plus
tests/air_gunners_test.cs. No Unity, game, install or network; the output
stays under build/air-gunners-check/. Also holds the live file to the rules
the core cannot: no per-frame scan, the seams it needs, ASCII.
"""
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build' / 'air-gunners-check'
OUT.mkdir(parents=True, exist_ok=True)
CSC = Path('C:/Windows/Microsoft.NET/Framework64/v3.5/csc.exe')
if not CSC.exists():
    CSC = Path('C:/Windows/Microsoft.NET/Framework/v3.5/csc.exe')

exe = OUT / 'air-gunners-test.exe'
r = subprocess.run([str(CSC), '/nologo', '/optimize+', '/out:' + str(exe),
                    str(ROOT / 'Revival.AirGunnersCore.cs'), str(ROOT / 'tests' / 'air_gunners_test.cs')],
                   capture_output=True, text=True, errors='replace')
if r.returncode != 0:
    print(r.stdout + r.stderr)
    sys.exit(1)
r = subprocess.run([str(exe)], capture_output=True, text=True, errors='replace')
print(r.stdout.strip())
if r.returncode != 0:
    sys.exit(1)

live = (ROOT / 'Revival.AirGunners.cs').read_text(encoding='utf-8')
core = (ROOT / 'Revival.AirGunnersCore.cs').read_text(encoding='utf-8')
for name, text in (('Revival.AirGunners.cs', live), ('Revival.AirGunnersCore.cs', core)):
    odd = sorted(set(c for c in text if ord(c) > 126 and not 0x400 <= ord(c) <= 0x4FF))
    assert not odd, name + ': non-ASCII outside Cyrillic: ' + ' '.join('U+%04X' % ord(c) for c in odd)
    assert 'FindObjectsOfType' not in text and 'GameObject.Find(' not in text, name + ': a scene search'


def body(text, sig):
    start = text.index(sig)
    brace = text.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]


# The per-frame paths allocate nothing and scan nothing.
for sig in ('internal static void Tick()', 'internal static void LateTick()', 'internal static bool OrbitSteer('):
    b = body(live, sig)
    assert 'new List<' not in b and 'new float[' not in b and '.ToString(' not in b, sig + ': allocates per frame'
    assert 'NpcScan.All()' not in b, sig + ': scans every frame'
# The merc target search is throttled.
assert re.search(r'_nextMercScan = now \+ ', live), 'the merc target search is not throttled'
assert 'NpcScan.All()' in body(live, 'static Component MercTarget('), 'merc targets do not come from the registry'
# Seams outside the file.
plug = (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8')
an2 = (ROOT / 'Revival.PlayerAn2.cs').read_text(encoding='utf-8')
cam = (ROOT / 'Revival.CameraTurret.cs').read_text(encoding='utf-8')
quick = (ROOT / 'Revival.MercQuickOrders.cs').read_text(encoding='utf-8')
for seam in ('AirGunners.BindConfig(Config);', 'AirGunners.Tick();', 'AirGunners.LateFrame();', 'AirGunners.Draw();'):
    assert seam in plug, 'RevivalPlugin.cs lacks ' + seam
assert 'AirGunners.OrbitSteer(' in an2 and 'AirGunners.OrbitThrottle(' in an2, 'the An-2 autopilot seam is missing'
assert 'public const int AirGun = 9;' in cam and 'else if (_owner == AirGun) AirGunners.LateTick();' in cam, \
    'CameraOwner does not know the air gunner'
assert 'AirGunners.OnNet(data, sender)' in cam, 'the turret shot channel does not carry the air gunner actions'
assert '!AirGunners.PilotOwnsMark' in quick, 'the merc quick order still takes the pilot\'s mouse wheel click'
assert re.search(r'cfg\.Bind\(S, "Enabled", true,', live), '[AirGunners] Enabled is not on by default'
print('PASS live seams, ASCII, no per-frame scan or allocation in Tick/LateTick/OrbitSteer')

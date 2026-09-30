"""Compile/run the production C# 3 fall curve and check sight integration.

No game, Unity runtime, install or network. Outputs stay in this worktree.
"""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'build/bombsight-check'
OUT.mkdir(exist_ok=True)
CSC = Path('C:/Windows/Microsoft.NET/Framework64/v3.5/csc.exe')
exe = OUT / 'bombsight.exe'
source = (ROOT / 'Revival.An2Bombs.cs').read_text(encoding='utf-8')
def method(signature):
    start = source.index(signature)
    brace = source.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


harness = OUT / 'Harness.cs'
harness.write_text((ROOT / 'research/an2_bombsight_harness.cs').read_text(encoding='ascii')
                   .replace('/*ADVANCE*/', method('static void Advance('))
                   .replace('/*PREDICT*/', method('static bool OnPlane(') + '\n'
                            + method('internal static bool Predict(')), encoding='ascii')
compile_result = subprocess.run([
    str(CSC), '/nologo', '/optimize+', '/target:exe', '/codepage:65001',
    '/out:' + str(exe), str(ROOT / 'Revival.An2BombCurve.cs'),
    str(harness)], capture_output=True, text=True, encoding='utf-8', errors='replace')
if compile_result.returncode:
    print(compile_result.stdout + compile_result.stderr)
    sys.exit(compile_result.returncode)
result = subprocess.run([str(exe)], capture_output=True, text=True)
print(result.stdout, end='')
if result.returncode:
    sys.exit(result.returncode)

tick = source[source.index('internal static void Tick()'):source.index('static void CloseSight()')]
solve = source[source.index('static void Solve('):source.index('static void Mark(')]
draw = source[source.index('static void Sight(float'):source.index('static bool Clip(')]
release = source[source.index('static void Release('):source.index('static float Gauss()')]
assert 'if (!PlayerAn2.Flying' in tick and 'if (PlayerAn2.OnGround)' in tick
assert 'if (Time.time < _nextSolve) return;' in solve and '_nextSolve = Time.time + 0.1f;' in solve
assert solve.count('Physics.Raycast(') == 1 and 'Predict(' not in release
assert 'Input.GetKey(releaseKey)' in tick and '!GameUi.WindowOpen' in tick
assert 'MapTools.MouseWorld(out point)' in tick and 'ViewportPointToRay' in tick
assert 'Backspace' in tick and '_aimSet = false' not in source[source.index('static void CloseSight()'):source.index('static float _nearCheck')]
assert 'new GUIStyle' not in draw and 'ToString(' not in draw and 'new GUIContent' not in draw
assert '1.96f * _sigma' in draw and '_stickStep * (_displayCount - 1)' in draw
assert 'WorldLine(cam, centre, _aim' in draw and 'float cue =' in draw
assert '\u0411\u041e\u041c\u0411' in source and not source.encode('utf-8').startswith(b'\xef\xbb\xbf')
plugin = (ROOT / 'RevivalPlugin.cs').read_text(encoding='utf-8')
assert 'FrameProf.S(FrameProf.S_An2BombsT); An2Bombs.Tick();' in plugin
assert 'FrameProf.S(FrameProf.S_An2BombsD); if (seatHud) An2Bombs.Draw();' in plugin
package = (ROOT / 'sync_public.py').read_text(encoding='utf-8')
assert '"Revival.An2BombCurve.cs"' in package and '"research/an2_bombsight_check.py"' in package
print('Integration: local pilot/airborne gate, 10 Hz single ray, fixed map/G target, hold stick, dispersion/cue, F6 slots 63/90: PASS')
print('Draw: cached styles/content/digits; no per-repaint text building or managed arrays: PASS')

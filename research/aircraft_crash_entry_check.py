"""Compile real destruction entry points against small Unity/Photon stubs.

No Unity editor, game process, deployment or Python flight substitute. The
C# executable drives AirKills.Downed, NpcAircraft.Apply/Tick, GepardAir.KillNow,
PlayerAn2.ShotDown/Abandon/An2Glide, actual FX/proxies and HeliHold.Attach.
"""
from pathlib import Path
import os
import subprocess

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / '.agent-tmp' / 'aircraft-entry-check'


def block(path, anchor):
    source = (ROOT / path).read_text(encoding='utf-8')
    start = source.index(anchor)
    first = source.index('{', start)
    depth, end = 1, first + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


def methods(path, anchors):
    return '\n'.join(block(path, anchor) for anchor in anchors)


def main():
    fixture = (ROOT / 'tests/aircraft_crash_entry_harness.cs').read_text(encoding='utf-8')
    spans = {
        'AN2_ENTRY': methods('Revival.PlayerAn2.cs', [
            'internal static void ShotDown(', 'static void ShotDownHere(',
            'static void Abandon(GameObject go, Vector3 vel, Quaternion rot, bool broadcast)',
            'static void Abandon(GameObject go, Vector3 vel, Quaternion rot, bool broadcast,',
            'static bool Gliding(']),
        'GLIDE': block('Revival.PlayerAn2.cs', 'public sealed class An2Glide'),
        'NPC_ENTRY': methods('Revival.NpcAircraft.cs', [
            'static void Apply(', 'internal static void Tick()', 'static void Drive(']),
        'KILL_ENTRY': block('Revival.AirKills.cs', 'internal static void Downed('),
        'GEPARD_ENTRY': methods('RevivalGepardCrew.cs', [
            'internal static void Register(', 'static bool IsDown(',
            'internal static bool KillNow(', 'internal static bool Alive(',
            'internal static void Hit(GepardGun.Contact c, Vector3 point)',
            'internal static void Hit(GepardGun.Contact c, Vector3 point, int gunHits)',
            'internal static void WeaponHit(',
            'internal static void Collect(']),
        'HOLD_ENTRY': block('Revival.PlayerHeli.cs', 'internal static void Attach(GameObject go)'),
        'TU95_PLACE': methods('Revival.AirEvents.cs', [
            'internal static float ProxyU(', 'void Place(Camera cam)']),
        'HELI_DROP': block('Revival.PlayerHeli.cs', 'internal static float Drop('),
    }
    for key, value in spans.items():
        fixture = fixture.replace('/* ' + key + ' */', value)
    OUT.mkdir(parents=True, exist_ok=True)
    source = OUT / 'EntryCheck.cs'
    source.write_bytes(fixture.encode('utf-8'))
    exe = OUT / 'EntryCheck.exe'
    compiler = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    production = ['Revival.AircraftFallCore.cs', 'Revival.AirKillCore.cs',
                  'Revival.AircraftCrashFx.cs', 'Revival.AircraftCrashVisual.cs',
                  'Revival.AircraftParticleDraw.cs']
    subprocess.run([str(compiler), '/nologo', '/codepage:65001', '/out:' + str(exe),
                    str(source)] + [str(ROOT / p) for p in production], check=True)
    subprocess.run([str(exe)], check=True)


if __name__ == '__main__':
    main()

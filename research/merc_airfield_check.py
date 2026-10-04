"""A S6: execute the unchanged production defence adapter with seat doubles.

No Unity process, installation, UI or network. Existing merc_stations_check
separately exercises the actual master lease/owner validation rules.
"""
from pathlib import Path
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def block(source, anchor):
    start = source.index(anchor)
    at = source.index('{', start)
    depth = 1
    end = at + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


def main():
    stage = ROOT / 'build' / 'merc_airfield_check'
    stage.mkdir(parents=True, exist_ok=True)
    stations = (ROOT / 'Revival.MercStations.cs').read_text(encoding='utf-8')
    harness = (ROOT / 'tests/merc_airfield_test.cs').read_text(encoding='utf-8')
    harness = harness.replace('// PRODUCTION_GIVE_STATION', block(stations, 'internal static void GiveStation('))
    harness = harness.replace('// PRODUCTION_MATCHES', block(stations, 'internal static bool Matches('))
    mercs = (ROOT / 'Revival.Mercs.cs').read_text(encoding='utf-8')
    harness = harness.replace('// PRODUCTION_GIVE', block(mercs, 'static void Give('))
    aa = (ROOT / 'Revival.MercAA.cs').read_text(encoding='utf-8')
    harness = harness.replace('// PRODUCTION_AVAILABLE', block(aa, 'static bool Available('))
    harness = harness.replace('// PRODUCTION_CAN_APPROACH', block(aa, 'internal static bool CanApproach('))
    harness = harness.replace('// PRODUCTION_HOSTILE_CREW', block(aa, 'internal static Component HostileCrew('))
    ui = (ROOT / 'Revival.MercsUi.cs').read_text(encoding='utf-8')
    harness = harness.replace('// PRODUCTION_ORDER_TEXT', block(ui, 'internal static string OrderText('))
    src = stage / 'Harness.cs'
    src.write_text(harness, encoding='utf-8')
    compiler = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    exe = stage / 'Check.exe'
    commands = [[str(compiler), '/nologo', '/warn:0', '/optimize+', '/codepage:65001',
                 '/out:' + str(exe), str(src), str(ROOT / 'Revival.MercAirfield.cs'),
                 str(ROOT / 'Revival.MercTargetCore.cs'), str(ROOT / 'Revival.MercStationsCore.cs')], [str(exe)]]
    for args in commands:
        result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        print(result.stdout.decode('utf-8', errors='replace').strip())
        if result.returncode:
            return result.returncode
    ui = (ROOT / 'Revival.MercsUi.cs').read_text(encoding='utf-8')
    page = (ROOT / 'Revival.MercPage.cs').read_text(encoding='utf-8')
    issue = block(ui, 'static void Issue(')
    assert issue.count('ToggleAirDefence()') == 1
    assert 'OpenStations' not in ui + stations and 'StationRows' not in ui + stations
    assert 'MAN GUNS' not in ui and 'MAN NEAREST RADAR' not in ui
    assert 'OrderRaidGuns()' not in ui + page and 'OrderAAPost(' not in ui + page
    assert 'Mercs.ToggleAirDefence()' in page
    en = ui[ui.index('string[] SectorEn'):ui.index('const int AttackSector')]
    assert en.count('"Man air defence"') == 1
    assert en.count('"') == 44  # Eleven labels in each language, equal wheel sizes.
    assert 'AirDefenceTick(now);' in (ROOT / 'Revival.Mercs.cs').read_text(encoding='utf-8')
    assert 'if (Mercs.AirDefenceManaged(u)) return false;' in block(stations, 'internal static bool Replace(')
    radar = (ROOT / 'Revival.TowerRadar.cs').read_text(encoding='utf-8')
    console = block(radar, 'internal static Transform Console(')
    # E W1: inside the cab (the glazed command room) on its measured floor,
    # not the main roof (C W3) or the cab roof (6.67).
    assert 'p.y = TowerRadar.CabFloorY;' in console and 'RoofM' not in console
    assert 'MainRoofY' not in console
    # Both native and merc operator poses continue to use this one console.
    assert 'c.TransformPoint(new Vector3(0f, 0.02f, 0.85f)' in radar
    aa = (ROOT / 'Revival.MercAA.cs').read_text(encoding='utf-8')
    assert 'TowerRadar.OperatorSeat(true)' in aa  # G R2: the operator sits on the console chair
    assert 'sender == MasterActor()' in aa and 'Resolve(view, sender)' in aa
    adapter = (ROOT / 'Revival.MercAirfield.cs').read_text(encoding='utf-8')
    tick = block(adapter, 'static void AirDefenceTick(')
    assert '_defenceAt = now + 0.5f;' in tick
    assert 'FrameProf.S(FrameProf.S_MercAirfieldT)' in tick and 'finally' in tick
    assert 'Physics.' not in adapter and 'FindObjectsOfType' not in adapter
    profile = (ROOT / 'RevivalFrameProfiler.cs').read_text(encoding='utf-8')
    assert 'public const int S_MercAirfieldT = ' in profile and 'MercAirfield.Orders.Sub' in profile
    assert 'MercStationApproach(f, u, at, now)' in aa and 'MercStationPlan.Retreat' in aa
    # H M2: the production post step clears enemy crews, waits out other
    # holders, walks the direct path, and duty orders reply only by summary.
    post = block(aa, 'static void MercPostStep(')
    assert 'MercStationPlan.PostAction(' in post and 'MercClearPost(f, u, occupant, at, now)' in post
    assert 'Mercs.AirDefenceManaged(u)' in post
    clear = block(aa, 'static void MercClearPost(')
    assert 'u.ClearAimAt = now + 0.5f;' in clear and 'new ' not in clear
    adapters = (ROOT / 'Revival.MercStationsAdapters.cs').read_text(encoding='utf-8')
    approach = block(adapters, 'static void MercStationApproach(')
    assert '!Mercs.AirDefenceManaged(u)' in approach
    received = block((ROOT / 'Revival.MercOrders.cs').read_text(encoding='utf-8'), 'static void OrderReceived(')
    assert 'if (_orderQuiet) { r.Receipt.Pending = false;' in received
    assert 'internal static Component Man { get { return _man; } }' in radar
    print('PASS: shared K/MMB toggle/page, removed picker, cab console, 2 Hz/F6, existing master leases and M1/M2/M3 wiring')
    return 0


if __name__ == '__main__':
    sys.exit(main())

"""Code-only shot-down descent proof; no Unity editor or game writes.

Compile the actual C# 3.0 fall core and density parser, check their behavior,
and read the installed native particle prefabs to verify the runtime sources.
"""
from pathlib import Path
import os
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / '.agent-tmp' / 'aircraft-crash-check'


def method(source, anchor):
    start = source.index(anchor)
    first = source.index('{', start)
    depth = 1
    end = first + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


def compiled_behavior():
    settings = (ROOT / 'Revival.Settings.cs').read_text(encoding='utf-8')
    parser = method(settings, 'internal static int ParseLevel(')
    parser += '\n' + method(settings, 'static bool LevelName(')
    heli = (ROOT / 'Revival.PlayerHeli.cs').read_text(encoding='utf-8')
    drop = method(heli, 'internal static float Drop(').replace('Mathf.Max', 'Math.Max')
    harness = r'''
using System;
namespace NextDayRevival {
static class Density { PARSER }
static class HeliDrop {
    const float Gravity = (float)AircraftFallCore.Gravity, Terminal = 65f;
    HELI_DROP
}
static class FallCheck {
    static int checks;
    static void Check(bool ok, string name) {
        if (!ok) throw new Exception(name); checks++;
    }
    static void Near(double actual, double expected, double tolerance, string name) {
        Check(Math.Abs(actual - expected) <= tolerance, name);
    }
    static void Main() {
        // Physical acceptance: no lift from the first second, also at high
        // cruise speed (horizontal speed is deliberately absent from gravity).
        Near(AircraftFallCore.VerticalTravel(0, 1, 85), -8, 1e-8, "first second destruction dive");
        Near(AircraftFallCore.VerticalTravel(0, 3, 85), -72, 1e-8, "three second destruction dive");
        Near(AircraftFallCore.VerticalTravel(0, 5, 85), -200, 1e-8, "five second destruction dive");
        Near(AircraftFallCore.VerticalTravel(0, -1, 85), 0, 1e-8, "future start waits");
        Check(AircraftFallCore.VerticalTravel(0, 8, 85) < -300, "300 m fall under 8 s");
        Check(AircraftFallCore.VerticalTravel(0, 10.5, 85) < -500, "500 m fall under 10.5 s");
        Near(AircraftFallCore.VerticalTravel(20, 1, 85), 12, 1e-8, "preserve upward momentum");
        double terminalAt = 85 / AircraftFallCore.Gravity;
        Near(AircraftFallCore.VerticalTravel(0, terminalAt + 2, 85)
            - AircraftFallCore.VerticalTravel(0, terminalAt + 1, 85), -85, 1e-8, "terminal descent");
        Near(AircraftFallCore.VerticalTravel(-150, 1, 85), -85, 1e-8, "bounded initial descent");
        // World scale conversion preserves NPC path speed instead of applying
        // the An-2 scale twice. Helicopter world velocities use their own K.
        double dt = 0.000001;
        Near((168 / 2.8) * AircraftFallCore.HorizontalTravel(dt) * 2.8 / dt,
            168, 0.0001, "NPC velocity units");
        Near((153.6 / 3.84) * AircraftFallCore.HorizontalTravel(dt) * 3.84 / dt,
            153.6, 0.0001, "helicopter velocity units");
        // Late reception and different frame rates land at the same Photon
        // age. A hitches' full time advances the fall, not a capped delta.
        double at30 = 0, at144 = 0;
        for (int i = 0; i < 150; i++) at30 += 1.0 / 30;
        for (int i = 0; i < 720; i++) at144 += 1.0 / 144;
        Near(AircraftFallCore.VerticalTravel(0, at30, 85),
            AircraftFallCore.VerticalTravel(0, at144, 85), 1e-8, "30/144 fps peer agreement");
        double clock = 1234567.890123;
        float high = (float)clock, low = (float)(clock - high);
        Near((double)high + low, clock, 0.000001, "Photon clock precision");
        Check(AircraftFallCore.Pitch(0, 3) > 40, "nose down within three seconds");
        Near(AircraftFallCore.Bank(0, 1, 5),
            -AircraftFallCore.Bank(0, -1, 5), 1e-8, "hit-side tumble");
        Check(Math.Abs(AircraftFallCore.Bank(0, 1, 5)) > 90, "wing rolls over");
        Near(AircraftFallCore.HorizontalTravel(0), 0, 1e-8, "initial pose exact");
        Check(Density.ParseLevel(null) == 3, "default high");
        Check(Density.ParseLevel(" OFF ") == 0, "off whitespace/case");
        Check(Density.ParseLevel("false") == 0, "legacy off alias");
        Check(Density.ParseLevel("None") == 0, "none alias");
        Check(Density.ParseLevel(" LOW\t") == 1, "low");
        Check(Density.ParseLevel("mEdIuM") == 2, "medium");
        Check(Density.ParseLevel(" mid ") == 2, "mid alias");
        Check(Density.ParseLevel("High") == 3, "high");
        Check(Density.ParseLevel("0") == 0 && Density.ParseLevel("1") == 1
            && Density.ParseLevel("2") == 2, "numeric aliases");
        Check(Density.ParseLevel(" ") == 3 && Density.ParseLevel("offline") == 3,
            "unknown values keep high");
        for (int i = 0; i <= 300; i++) {
            double age = i * 0.1;
            Near(HeliDrop.Drop(0, 3.84f, age), AircraftFallCore.VerticalTravel(0, age, 65) * 3.84,
                0.001, "actual helicopter gravity/terminal agreement");
        }
        Console.WriteLine("PASS actual C# 3.0 fall/density: " + checks + " behavioral checks");
    }
}}
'''.replace('PARSER', parser).replace('HELI_DROP', drop)
    OUT.mkdir(parents=True, exist_ok=True)
    source = OUT / 'FallCheck.cs'
    source.write_text(harness, encoding='ascii')
    exe = OUT / 'FallCheck.exe'
    csc = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Microsoft.NET/Framework64/v3.5/csc.exe'
    subprocess.run([str(csc), '/nologo', '/langversion:Default', '/out:' + str(exe),
                    str(ROOT / 'Revival.AircraftFallCore.cs'), str(source)], check=True)
    subprocess.run([str(exe)], check=True)
    assert 'ToLower' not in parser and '.Trim(' not in parser


def native_sources():
    sys.path.insert(0, str(ROOT / 'research'))
    import mono
    index = {o.path_id: o for o in mono.env('resources.assets').objects}
    paths = {}
    for row in (ROOT / 'research/resource_paths.tsv').read_text(encoding='utf-8').splitlines():
        cols = row.split('\t')
        if len(cols) == 3 and cols[1] == '9':
            paths[cols[0]] = int(cols[2])

    def tree(pid):
        return index[pid].read_typetree()

    def components(pid):
        return [index[c['component']['m_PathID']] for c in tree(pid)['m_Component']]

    smoke = paths['particles/vehicles/vehiclesmoke_01']
    camp = paths['lootspawn/crafting/crafted/5001_spawn']
    flame = camp
    for name in ['FireEmitObjects', 'CampfireElements', 'FireComplex_old', 'Flames']:
        tr = next(c for c in components(flame) if c.type.name == 'Transform')
        children = [tree(t['m_PathID'])['m_GameObject']['m_PathID'] for t in tr.read_typetree()['m_Children']]
        flame = next(pid for pid in children if tree(pid)['m_Name'] == name)
    for pid in (smoke, flame):
        kinds = [c.type.name for c in components(pid)]
        assert sorted(kinds) == ['ParticleSystem', 'ParticleSystemRenderer', 'Transform'], kinds
        ps = next(c for c in components(pid) if c.type.name == 'ParticleSystem')
        renderer = next(c for c in components(pid) if c.type.name == 'ParticleSystemRenderer')
        assert renderer.read_typetree()['m_Materials'], 'native particle material missing'
        assert ps.read_typetree()['InitialModule'], 'native particle setup missing'
    print('PASS installed vanilla sources: flame %s, smoke %s, particles only' % (flame, smoke))

    # The crash-only Mi-8 camera proxy copies actual MeshRenderer/MeshFilter
    # geometry. Verify that prerequisite against the installed carrier prefab.
    pending = [paths['gameplayobjects/helicopters/mi-8_mchs']]
    mesh_renderers = skinned_renderers = 0
    while pending:
        pid = pending.pop()
        comps = components(pid)
        kinds = [c.type.name for c in comps]
        if 'MeshRenderer' in kinds:
            assert 'MeshFilter' in kinds, 'Mi-8 renderer has no mesh'
            mesh_renderers += 1
        skinned_renderers += kinds.count('SkinnedMeshRenderer')
        tr = next(c for c in comps if c.type.name == 'Transform')
        pending.extend(tree(t['m_PathID'])['m_GameObject']['m_PathID']
                       for t in tr.read_typetree()['m_Children'])
    assert mesh_renderers > 0 and skinned_renderers == 0, 'unsupported Mi-8 geometry'
    print('PASS installed Mi-8 proxy prerequisite: %s mesh renderers, no skinned geometry' % mesh_renderers)


def plumbing():
    an2 = (ROOT / 'Revival.PlayerAn2.cs').read_text(encoding='utf-8')
    heli = (ROOT / 'Revival.PlayerHeli.cs').read_text(encoding='utf-8')
    troops = (ROOT / 'RevivalTroopInsertion.cs').read_text(encoding='utf-8')
    fx = (ROOT / 'Revival.AircraftCrashFx.cs').read_text(encoding='utf-8')
    kills = (ROOT / 'Revival.AirKills.cs').read_text(encoding='utf-8')
    for src in (an2, heli):
        assert 'high, (float)(clock - high)' in src, 'reliable fall event clock missing'
        assert '(double)f[13] + f[14]' in src, 'peer clock decode missing'
        assert 'new Vector3(f[10], f[11], f[12])' in src, 'peer damage site missing'
    assert 'AircraftCrashFx.Position(' in an2 and 'AircraftCrashFx.Rotation(' in an2
    assert 'Drop(_velocity.y, k, age)' in heli and 'AircraftFallCore.Pitch(' in heli
    glide = an2[an2.index('public sealed class An2Glide'):]
    assert 'Random' not in glide and 'carry' not in glide and 'Time.deltaTime' not in glide
    assert 'out vel)) vel /= K' in an2
    assert 'AircraftCrashFx.Position(_fallFrom, _fallVel / k, k' in troops
    assert '0f, site.x, site.y, site.z' in kills and 'new Vector3(f[6], f[7], f[8])' in kills
    assert 'tr.Find("NDR_An2/Pivot_prop")' in fx
    assert 'new GameObject("Pivot_" + part)' in an2 and 'Hinge(root.transform, "prop"' in an2
    tick = method(fx, 'internal static void Tick(')
    assert not re.search(r'new\s|Instantiate|GetComponents|\.Find\(', tick), 'frame-path allocation/search'
    assert 'main.simulationSpace = ParticleSystemSimulationSpace.World' in fx
    assert 'const int MaxTrails = 24' in fx
    fire = (ROOT / 'Revival.WreckFire.cs').read_text(encoding='utf-8')
    assert 'AircraftCrashFx.Start(root, aircraft.TrailSite' in fire
    assert 'AircraftCrashFx.Stop(root)' in method(fire, 'public static void StopEmitting(')
    print('PASS Photon fall payloads, helicopter path, bounded allocation-free effect tick')


if __name__ == '__main__':
    compiled_behavior()
    native_sources()
    plumbing()

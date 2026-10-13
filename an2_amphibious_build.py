"""Permanent An-2 amphibious kit for the shipped plane, without rebuilding it.

All mesh coordinates are real metres in the existing flight-line frame.
The runtime raises the complete model by 1.25 m so extended tyres touch y=0.
The tracked GLB is used ONLY to identify the old land-gear index ranges;
every shipped body vertex/normal/UV/index is checked before emitting a mask.
Run: python an2_amphibious_build.py [--check]
"""
import hashlib
from pathlib import Path
import sys

import numpy as np
from PIL import Image

import an2_import as importer
import gltf_read
from ndmesh import Mesh, load as read_mesh

ROOT = Path(__file__).resolve().parent
ASSETS = ROOT / 'assets'
OFFSET = (0.0, 1.25, 0.0)
K, CAPSULE_U = 2.8, 5.0
FLOAT_X = 2.0
# z, half width, deck y, keel y. The transverse step is at z=-1.82.
SECTIONS = [(-5.8, .18, -.05, -.30), (-5.3, .48, -.05, -.65),
            (-2.0, .58, -.05, -.72), (-1.85, .58, -.05, -.72),
            (-1.82, .58, -.05, -.95), (.55, .57, -.04, -.90),
            (1.4, .42, .08, -.50), (2.1, .07, .12, .0)]
# name, kind, pivot x/y/z, tyre radius, wheel centre below pivot, retract deg.
WHEELS = [('main_l', 'main', -2.68, -.15, 0., .40, -.70, -105.),
          ('main_r', 'main', 2.68, -.15, 0., .40, -.70, 105.),
          ('rear_l', 'rear', -2.68, -.15, -4.8, .25, -.85, -105.),
          ('rear_r', 'rear', 2.68, -.15, -4.8, .25, -.85, 105.)]
# Conservative boxes inside the hull. Centres/sizes are in model metres.
FLOAT_BOXES = [(0., -.50, -4.0, .12, .18, 2.6),
               (0., -.77, -.65, .12, .18, 2.30),
               (0., -.39, 1.15, .08, .18, .45)]


def face(mesh, points, outward, region):
    """Emit outward Unity triangles with a stable per-panel atlas mapping."""
    points = list(points)
    n = np.cross(np.subtract(points[1], points[0]), np.subtract(points[2], points[0]))
    if np.dot(n, outward) < 0:
        points.reverse()
        n = -n
    n /= np.linalg.norm(n)
    # Four independent, padded material cells; same worn palette as the plane.
    cells = {'paint': (.02, .52, .48, .98), 'metal': (.52, .52, .98, .98),
             'rubber': (.02, .02, .48, .48), 'deck': (.52, .02, .98, .48)}
    u0, v0, u1, v1 = cells[region]
    uv = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
    base = len(mesh.V)
    mesh.V.extend(tuple(float(v) for v in p) for p in points)
    mesh.N.extend([tuple(n)] * len(points))
    mesh.T.extend(uv[:len(points)])
    for i in range(1, len(points) - 1):
        mesh.IDX.extend((base, base + i, base + i + 1))


def float_hull(x):
    mesh = Mesh('An2AmphibFloat')
    rings = []
    for z, w, top, bottom in SECTIONS:
        rings.append([(x - .75*w, top, z), (x + .75*w, top, z),
                      (x + w, top-(top-bottom)*.22, z),
                      (x + .9*w, bottom+(top-bottom)*.18, z),
                      (x, bottom, z), (x - .9*w, bottom+(top-bottom)*.18, z),
                      (x - w, top-(top-bottom)*.22, z)])
    for a, b in zip(rings, rings[1:]):
        for i in range(7):
            j = (i + 1) % 7
            centre = (np.mean(a, axis=0) + np.mean(b, axis=0))*.5
            middle = np.mean([a[i], a[j], b[j], b[i]], axis=0)
            out = (middle[0]-centre[0], middle[1]-centre[1], 0.)
            face(mesh, [a[i], a[j], b[j], b[i]], out,
                 'deck' if i == 0 else 'paint')
    for ring, direction in ((rings[0], -1.), (rings[-1], 1.)):
        centre = np.mean(ring, axis=0)
        for i in range(7):
            face(mesh, [centre, ring[i], ring[(i+1) % 7]], (0., 0., direction), 'paint')
    return mesh


def rod(mesh, start, end, radius, region='metal'):
    """Cylinder on an arbitrary axis, with outward caps."""
    a, b = np.array(start), np.array(end)
    axis = b-a
    axis /= np.linalg.norm(axis)
    other = np.array([1., 0., 0.]) if abs(axis[0]) < .8 else np.array([0., 1., 0.])
    u = np.cross(axis, other)
    u /= np.linalg.norm(u)
    v = np.cross(axis, u)
    rim = [radius*(u*np.cos(i*np.pi/4) + v*np.sin(i*np.pi/4)) for i in range(8)]
    for i in range(8):
        j = (i+1) % 8
        face(mesh, [a+rim[i], a+rim[j], b+rim[j], b+rim[i]], rim[i]+rim[j], region)
        face(mesh, [a, a+rim[j], a+rim[i]], -axis, region)
        face(mesh, [b, b+rim[i], b+rim[j]], axis, region)


def kit_meshes():
    floats = Mesh('An2AmphibFloats')
    for side in (-1., 1.):
        floats.merge(float_hull(side*FLOAT_X))
        # Two V-braced mount stations, fixed to fuselage and float deck.
        for z in (-3.7, .45):
            for dz in (-.45, .45):
                rod(floats, (side*.78, 1.12, z+dz), (side*FLOAT_X, -.05, z), .055)
        # Deck rails and cross ties make the kit readable at vanilla detail.
        rod(floats, (side*FLOAT_X, .01, -4.7), (side*FLOAT_X, .01, .8), .025, 'deck')
    for z in (-3.7, .45):
        rod(floats, (-FLOAT_X, -.01, z), (FLOAT_X, -.01, z), .045)
    output = {'floats': floats}
    for kind, radius, cy, width in (('main', .4, -.7, .26), ('rear', .25, -.85, .20)):
        m = Mesh('An2AmphibWheel_' + kind)
        # Rounded tyre, axle along X. Wheel and swing arm share one pivot.
        profile = [(-width*.5, radius*.78), (-width*.40, radius*.96),
                   (width*.40, radius*.96), (width*.5, radius*.78)]
        for (x0, r0), (x1, r1) in zip(profile, profile[1:]):
            for i in range(24):
                a, b = i*2*np.pi/24, (i+1)*2*np.pi/24
                points = [(x0, cy+r0*np.cos(a), r0*np.sin(a)),
                          (x0, cy+r0*np.cos(b), r0*np.sin(b)),
                          (x1, cy+r1*np.cos(b), r1*np.sin(b)),
                          (x1, cy+r1*np.cos(a), r1*np.sin(a))]
                face(m, points, (0., np.cos((a+b)*.5), np.sin((a+b)*.5)), 'rubber')
        # True maximum radius at vertical tread: exact bottom for contact tests.
        # Increase the tread rings from .96*r to r without changing the shoulders.
        for i, p in enumerate(m.V):
            if abs(abs(p[0])-width*.40) < 1e-8:
                m.V[i] = (p[0], cy+(p[1]-cy)/.96, p[2]/.96)
        # Sidewalls close the tyre; hub and linkage use worn metal cells.
        for side in (-1., 1.):
            x = side*width*.5
            for i in range(24):
                a, b = i*2*np.pi/24, (i+1)*2*np.pi/24
                face(m, [(x, cy, 0.), (x, cy+radius*.78*np.cos(a), radius*.78*np.sin(a)),
                         (x, cy+radius*.78*np.cos(b), radius*.78*np.sin(b))],
                     (side, 0., 0.), 'rubber')
        rod(m, (-width*.6, cy, 0.), (width*.6, cy, 0.), radius*.46)
        rod(m, (0., 0., 0.), (0., cy, 0.), .045)
        rod(m, (0., -.03, .08), (0., cy*.85, .08), .023)
        output[kind] = m
    return output


def land_gear_mask():
    """Replay importer ordering and prove exact identity against shipped data."""
    js, buffers = gltf_read._load_glb(str(ASSETS/'an2_gameready/an2_flyable.glb'))
    nodes = js['nodes']
    attitude = next(n for n in nodes if n.get('name') == 'Attitude')
    body = importer.Chunks()
    ranges, index_count = [], 0
    for ci in attitude['children']:
        node = nodes[ci]
        name = node.get('name', '')
        if name.startswith('Socket_') or name in importer.MOVING:
            continue
        r, t = importer.local(node)
        for mi in node.get('children', []):
            child = nodes[mi]
            if 'mesh' not in child or not child.get('name', '').endswith('_LOD0'):
                continue
            for p, n, uv, f, mat in importer.primitives(js, buffers, child['mesh']):
                if mat == 1:
                    continue
                pp, nn = p @ r.T + t, n @ r.T
                chunk = importer.Chunks()
                chunk.add(pp, nn, uv, f)
                count = len(importer.to_mesh(name, chunk).IDX)
                if name.startswith(('Part_Gear_', 'Pivot_Wheel_')):
                    ranges.append((index_count, count, name))
                index_count += count
                body.add(pp, nn, uv, f)
    expected = importer.to_mesh('OriginalBody', body)
    shipped = read_mesh(str(ASSETS/'an2_body.ndmesh'))
    for want, actual in zip((expected.V, expected.N, expected.T, expected.IDX), shipped):
        if np.shape(want) != np.shape(actual) or not np.allclose(want, actual, rtol=0., atol=2e-6):
            raise ValueError('Tracked GLB and shipped body differ; refusing guessed gear mask')
    if len(ranges) != 6:
        raise ValueError('Expected two main legs, tail leg and three original tyres')
    return shipped, ranges


def rig_text(shipped, ranges):
    sha = hashlib.sha256((ASSETS/'an2_body.ndmesh').read_bytes()).hexdigest()
    lines = ['# Generated by an2_amphibious_build.py; original model metres.',
             'version 1', 'scale 2.8 5.0',
             'body %s %d %d' % (sha, len(shipped[0]), len(shipped[3])),
             'offset 0 1.25 0', 'parked 0',
             'contact l -2.68 0 0', 'contact r 2.68 0 0', 'contact tail 0 0 -4.8']
    lines.extend('remove %d %d %s' % row for row in ranges)
    for name, kind, x, y, z, radius, cy, angle in WHEELS:
        lines.append('wheel %s %s %g %g %g %g %g %g' % (name, kind, x, y, z, radius, cy, angle))
    for side, letter in ((-1., 'l'), (1., 'r')):
        for i, (_, y, z, sx, sy, sz) in enumerate(FLOAT_BOXES):
            lines.append('floatbox %s%d %g %g %g %g %g %g' %
                         (letter, i, side*FLOAT_X, y, z, sx, sy, sz))
    return '\n'.join(lines)+'\n'


def texture():
    # Deterministic hand-authored atlas: matte olive paint, aged steel,
    # dark tyre rubber and worn deck. Neutral normal map, no glossy plastic.
    rng = np.random.default_rng(2792)
    atlas = np.zeros((256, 256, 3), dtype=np.uint8)
    for x, y, base in ((0, 0, (89, 94, 73)), (128, 0, (98, 101, 91)),
                       (0, 128, (30, 32, 29)), (128, 128, (77, 82, 65))):
        noise = rng.normal(0, 2.2, (128, 128, 1))
        tile = np.clip(np.array(base)+noise, 0, 255).astype(np.uint8)
        tile[::32, :, :] = np.clip(tile[::32, :, :].astype(int)-8, 0, 255)
        for pos in range(8, 128, 24):
            tile[4:6, pos:pos+2, :] = (112, 111, 96)
        atlas[y:y+128, x:x+128] = tile
    return Image.fromarray(atlas)


def main():
    shipped, ranges = land_gear_mask()
    meshes = kit_meshes()
    for name, mesh in meshes.items():
        mesh.validate()
        print('%s: %d vertices, %d triangles' % (name, len(mesh.V), len(mesh.IDX)//3))
        if len(mesh.V) > 65535:
            raise ValueError('16-bit vertex budget exceeded')
    rig = rig_text(shipped, ranges)
    if '--check' not in sys.argv:
        for name, mesh in meshes.items():
            mesh.write(str(ASSETS/('an2_amphib_%s.ndmesh' % name)))
        texture().save(ASSETS/'an2_amphib_diffuse.png')
        Image.new('RGB', (16, 16), (128, 128, 255)).save(ASSETS/'an2_amphib_normal.png')
        (ASSETS/'an2_amphib_rig.txt').write_text(rig, encoding='ascii')
    print('Scale: 5 u capsule = %.6f m; span 18.18 m = %.3f u = %.4f capsules' %
          (CAPSULE_U/K, 18.18*K, 18.18*K/CAPSULE_U))
    print('Float length 7.9 m = 22.12 u = 4.424 capsules; extended tyres at y=0')
    print('Old land gear: %d ranges, %d triangles removed; original assets untouched' %
          (len(ranges), sum(r[1] for r in ranges)//3))


if __name__ == '__main__':
    main()

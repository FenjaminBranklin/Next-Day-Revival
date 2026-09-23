"""Turn the Blender Gepard (assets/gepard/gepard.glb) into game meshes.

Run: python gepard_import.py [--check]

The model is Codex's original Blender build (gepard_build.py,
assets/src/gepard_blender.py); this script does not model anything. It reads
the GLB's eight pivoted groups and writes one ndmesh per moving part, in the
part's OWN pivot frame, so RevivalGepard.cs can turn the turret, elevate each
gun and spin both radars by rotating a transform:

    gepard_hull.ndmesh          Hull (chassis, deck, engine grills)
    gepard_tracks.ndmesh        Track_L + Track_R with their wheels (static)
    gepard_turret.ndmesh        Turret_Azimuth
    gepard_gun_r.ndmesh         the right-hand cannon (elevation pivot)
    gepard_gun_l.ndmesh         the left-hand cannon
    gepard_radar_search.ndmesh  rear search radar (spins about local Y)
    gepard_radar_track.ndmesh   front tracking radar (turns about local Y)
    gepard_diffuse.png          the model's own 4K atlas, unchanged pixels
    gepard_metal.png            metallic (R) and smoothness (A), rasterised
                                from the GLB's per-material PBR factors
    gepard_rig.txt              pivot of every part relative to its parent,
                                plus the measured muzzle of each gun

COORDINATES. glTF is right-handed with +Y up and the asset's front at +Z, so
its +X is the asset's LEFT. Unity is left-handed with +X right. Taking the
numbers over unchanged would mirror the vehicle, so x is negated on every
point, normal and pivot, and every triangle is re-wound (a, c, b): a mirror
turns the winding inside out, and verify.py [9] measures that the right-hand
normal of the written winding agrees with the stored normal (Unity's own
convention, measured on the game's meshes).

UNITS stay METRES. RevivalGepard.cs fits the whole model to the donor
vehicle's own measured length, exactly like the drivable howitzer, so no scale
of the game world is assumed here.

The 12 degree rest elevation the Blender pose gives both guns is NOT baked in:
a gun mesh is written in its node's local frame, where the bore is local +Z at
zero elevation, and the game sets the elevation itself.
"""
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)
import gltf_read  # noqa: E402
from ndmesh import Mesh  # noqa: E402

SOURCE = os.path.join(ROOT, 'assets', 'gepard', 'gepard.glb')
OUT = os.path.join(ROOT, 'assets')
METAL_SIZE = 1024

# glTF node name -> (output part, parent part). Tracks join into one mesh.
PARTS = [
    ('Hull', 'hull', None),
    ('Track_L', 'tracks', 'hull'),
    ('Track_R', 'tracks', 'hull'),
    ('Turret_Azimuth', 'turret', 'hull'),
    ('Search_Radar_Azimuth', 'radar_search', 'turret'),
    ('Tracking_Radar_Yaw', 'radar_track', 'turret'),
]
# The two guns are named by the side they end up on IN UNITY (after the
# mirror): the GLB's "Gun_L" sits at glTF x -1.34, which is Unity x +1.34.
GUNS = ['Gun_L_Elevation', 'Gun_R_Elevation']


def unity_point(p):
    return (-float(p[0]), float(p[1]), float(p[2]))


def read_node_mesh(js, buffers, mesh_index):
    """All primitives of one glTF mesh, in the node's local frame, as
    (positions, normals, uvs, triangles, material per triangle)."""
    pos, nrm, uv, tri, mat = [], [], [], [], []
    for prim in js['meshes'][mesh_index]['primitives']:
        if prim.get('mode', 4) != 4:
            raise ValueError('Gepard: non-triangle primitive')
        a = prim['attributes']
        p = gltf_read._accessor(js, buffers, a['POSITION'])
        n = gltf_read._accessor(js, buffers, a['NORMAL'])
        t = gltf_read._accessor(js, buffers, a['TEXCOORD_0'])
        idx = gltf_read._accessor(js, buffers, prim['indices'])
        base = len(pos)
        pos.extend(p)
        nrm.extend(n)
        uv.extend(t)
        for i in range(0, len(idx), 3):
            tri.append((base + idx[i], base + idx[i + 1], base + idx[i + 2]))
            mat.append(prim.get('material', 0))
    return pos, nrm, uv, tri, mat


def to_mesh(name, chunks):
    """Mirror x, re-wind, flip v (glTF's image origin is top-left, Unity's
    texture origin bottom-left), and drop degenerate triangles."""
    m = Mesh(name)
    dropped = 0
    for pos, nrm, uv, tri, _ in chunks:
        base = len(m.V)
        for p in pos:
            m.V.append(unity_point(p))
        for n in nrm:
            v = np.array([-n[0], n[1], n[2]], dtype=float)
            ln = np.linalg.norm(v)
            m.N.append(tuple(v / ln) if ln > 1e-9 else (0.0, 1.0, 0.0))
        for t in uv:
            m.T.append((min(1.0, max(0.0, float(t[0]))),
                        min(1.0, max(0.0, 1.0 - float(t[1])))))
        for a, b, c in tri:
            pa = np.array(m.V[base + a])
            pb = np.array(m.V[base + b])
            pc = np.array(m.V[base + c])
            if np.linalg.norm(np.cross(pb - pa, pc - pa)) * 0.5 < 1e-10:
                dropped += 1
                continue
            m.IDX.extend((base + a, base + c, base + b))
    m.dropped = dropped
    return m


def metal_map(js, chunks_all):
    """R metallic, A smoothness, rasterised per triangle in UV space from the
    material factors. Everything the atlas does not cover keeps the painted
    hull's values, so a mip-map filter bleeding over an island edge lands on
    plausible paint rather than on black."""
    mats = js['materials']
    paint = mats[0].get('pbrMetallicRoughness', {})
    fill = (int(255 * paint.get('metallicFactor', 0.2)), 0, 0,
            int(255 * (1.0 - paint.get('roughnessFactor', 0.75))))
    img = Image.new('RGBA', (METAL_SIZE, METAL_SIZE), fill)
    draw = ImageDraw.Draw(img)
    # Paint first, then everything else on top: the camouflage islands are the
    # largest, and a detail island that overlaps one must win.
    order = sorted(range(len(mats)), key=lambda k: 0 if k == 0 else 1)
    for want in order:
        pbr = mats[want].get('pbrMetallicRoughness', {})
        colour = (int(round(255 * pbr.get('metallicFactor', 1.0))), 0, 0,
                  int(round(255 * (1.0 - pbr.get('roughnessFactor', 1.0)))))
        for pos, nrm, uv, tri, mat in chunks_all:
            for k, (a, b, c) in enumerate(tri):
                if mat[k] != want:
                    continue
                poly = [(uv[i][0] * METAL_SIZE, uv[i][1] * METAL_SIZE)
                        for i in (a, b, c)]
                draw.polygon(poly, fill=colour)
    return img


def main():
    check = '--check' in sys.argv
    js, buffers = gltf_read._load_glb(SOURCE)
    nodes = {n['name']: n for n in js['nodes']}
    for name, _, _ in PARTS:
        if name not in nodes:
            raise ValueError('Gepard GLB lacks node ' + name)
    for name in GUNS:
        if name not in nodes:
            raise ValueError('Gepard GLB lacks node ' + name)

    grouped = {}
    rig = []
    everything = []
    for node_name, part, parent in PARTS:
        node = nodes[node_name]
        chunk = read_node_mesh(js, buffers, node['mesh'])
        everything.append(chunk)
        grouped.setdefault(part, []).append(chunk)
        if node.get('rotation') not in (None, [0, 0, 0, 1]) or node.get('scale'):
            raise ValueError('Gepard: %s carries a rotation or scale the rig '
                             'does not expect' % node_name)
        t = unity_point(node.get('translation', [0.0, 0.0, 0.0]))
        if part not in [r[0] for r in rig]:
            rig.append((part, parent or '-', tuple(c + 0.0 for c in t)))

    guns = {}
    for node_name in GUNS:
        node = nodes[node_name]
        chunk = read_node_mesh(js, buffers, node['mesh'])
        everything.append(chunk)
        t = unity_point(node['translation'])
        side = 'gun_r' if t[0] > 0 else 'gun_l'
        guns[side] = chunk
        grouped[side] = [chunk]
        rig.append((side, 'turret', t))
    if sorted(guns) != ['gun_l', 'gun_r']:
        raise ValueError('Gepard: both guns on one side')

    meshes = {}
    for part, chunks in grouped.items():
        meshes[part] = to_mesh('Gepard ' + part, chunks)

    # The muzzle: the far end of each gun along its own bore (+Z), centred on
    # the barrel. Only the forward-most tenth of the gun is the barrel tip, so
    # its centre is the bore axis even though the receiver is wider.
    muzzles = {}
    for side in ('gun_r', 'gun_l'):
        v = np.array(meshes[side].V)
        zmax = v[:, 2].max()
        tip = v[v[:, 2] > zmax - 0.08]
        muzzles[side] = (float(tip[:, 0].mean()), float(tip[:, 1].mean()), float(zmax))

    # Measured interior numbers the vehicle needs for its crew places, in the
    # turret's and hull's frames: turret roof (highest turret point near its
    # centre line, which leaves out optics sticking up at the edges), and the
    # hull belly under the turret ring.
    tv = np.array(meshes['turret'].V)
    centre = tv[(np.abs(tv[:, 0]) < 0.5) & (np.abs(tv[:, 2]) < 0.5)]
    roof = float(centre[:, 1].max()) if len(centre) else float(tv[:, 1].max())
    hv = np.array(meshes['hull'].V)
    ring = hv[(np.abs(hv[:, 0]) < 0.6) & (np.abs(hv[:, 2] - 0.1) < 0.6)]
    belly = float(ring[:, 1].min()) if len(ring) else float(hv[:, 1].min())

    for part, m in sorted(meshes.items()):
        m.validate()
        (x0, x1), (y0, y1), (z0, z1) = m.bounds()
        print('%-14s %6d vert %6d tri  x %6.2f..%6.2f  y %6.2f..%6.2f  z %6.2f..%6.2f'
              % (part, len(m.V), len(m.IDX) // 3, x0, x1, y0, y1, z0, z1))
        if len(m.V) > 65535:
            raise ValueError('Gepard %s exceeds a 16-bit index buffer' % part)
    for side in ('gun_r', 'gun_l'):
        print('muzzle %s (gun frame) %.3f %.3f %.3f' % ((side,) + muzzles[side]))
    print('turret roof %.3f m above the turret pivot, hull belly %.3f m' % (roof, belly))

    if check:
        return 0

    for part, m in meshes.items():
        m.write(os.path.join(OUT, 'gepard_%s.ndmesh' % part))

    atlas = Image.open(os.path.join(ROOT, 'assets', 'gepard', 'gepard_basecolor.png'))
    atlas.convert('RGB').save(os.path.join(OUT, 'gepard_diffuse.png'), optimize=True)
    # The metal map is rasterised in glTF UV space (origin top-left), which is
    # exactly PNG row order - no flip, the same way the atlas itself is stored.
    metal_map(js, everything).save(os.path.join(OUT, 'gepard_metal.png'), optimize=True)

    with open(os.path.join(OUT, 'gepard_rig.txt'), 'w', encoding='ascii', newline='\n') as f:
        f.write('# Written by gepard_import.py from assets/gepard/gepard.glb. Do not edit.\n')
        f.write('# part parent x y z   pivot in METRES, Unity axes, relative to the parent pivot\n')
        for part, parent, t in rig:
            f.write('part %s %s %.5f %.5f %.5f\n' % (part, parent, t[0], t[1], t[2]))
        f.write('# muzzle gun x y z   in the gun\'s own frame, bore along +Z\n')
        for side in ('gun_r', 'gun_l'):
            f.write('muzzle %s %.5f %.5f %.5f\n' % ((side,) + muzzles[side]))
        f.write('# measured interior: turret roof above its pivot, hull belly under the ring\n')
        f.write('roof %.5f\n' % roof)
        f.write('belly %.5f\n' % belly)
    print('written: %d meshes, gepard_diffuse.png, gepard_metal.png, gepard_rig.txt'
          % len(meshes))
    return 0


if __name__ == '__main__':
    sys.exit(main())

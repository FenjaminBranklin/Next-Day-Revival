"""Turn the game-ready An-2 (assets/an2_gameready/an2_flyable.glb) into game meshes.

Run: python an2_import.py [--check]

The model is the queue build of task 711339fad0 (an2_gameready_build.py on
that branch, docs/ai/tasks/an2-gameready.md); this script does not model
anything. It reads the LOD0 of every part under the GLB's `Attitude` node,
treats that node as identity (the FLIGHT LINE, not the parked three-point
pose - Revival.PlayerAn2.cs pitches the whole aircraft itself), and writes:

    an2_body.ndmesh        every static part: fuselage, wings, struts, tail,
                           cowling, engine, gear, closed doors and bays
    an2_glass.ndmesh       the greenhouse and cabin windows (drawn transparent)
    an2_prop.ndmesh        the AV-2 propeller, spins about its rig axis
    an2_aileron_l.ndmesh   \
    an2_aileron_r.ndmesh    | control surfaces, each in a frame with its origin
    an2_elevator.ndmesh     | at the hinge and the body's axes; the hinge axis
    an2_rudder.ndmesh      /  is in the rig
    an2_diffuse.png        the skin atlas, 1024 (the Mi-8's own albedo size -
                           option C3 of an2-gameready.md: same texel density)
    an2_normal.png         the tangent-space normal map, 1024
    an2_rig.txt            pivots, hinge axes, contact points, seats, hull
                           boxes and the scale proof

UNITS. The GLB is authored in GAME UNITS (u = real metres x 2.8, the
manifest's "units"). Everything written here is in REAL METRES: the plugin
multiplies by its own K = 2.8, so the one number that decides the size in the
world sits in the code next to the capsule it is measured against.

SCALE PROOF. The game world is 2.8 x real size: every NPC and the player carry
a 5.0 u CapsuleCollider (airfield-greybox.md section 0, REVERSE_ENGINEERING.md)
- a 1.79 m person. The Mi-8 prefab is NOT at that scale (its 18.3 m real
length is 70 u = 3.84 u/m) - which is why nothing here is fitted to the
Mi-8. The check below stops the import if the span or the length is off the
published An-2 data by more than 0.5 %, and prints what the aircraft measures
in player capsules.

COORDINATES. glTF is right-handed, +Y up, nose +Z, +X PORT. Unity is
left-handed, +X starboard. x is negated on every point and normal and every
triangle is re-wound (a, c, b) - the same mirror as gepard_import.py. A hinge
axis a becomes (ax, -ay, -az) in the rig: Unity's AngleAxis(t, that) is then
exactly the mirrored glTF rotation by t, so the manifest's senses ("+:
trailing edge up") hold unchanged in the game.
"""
import math
import os
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)
import gltf_read  # noqa: E402
from ndmesh import Mesh  # noqa: E402

SOURCE = os.path.join(ROOT, 'assets', 'an2_gameready', 'an2_flyable.glb')
OUT = os.path.join(ROOT, 'assets')
U_PER_M = 2.8                     # the world scale, measured on the capsule
CAPSULE_U = 5.0                   # player / NPC CapsuleCollider height
ATLAS = 1024

PUBLISHED = {'span_upper_m': 18.18, 'length_m': 12.74}

# glTF pivot node -> output part. Everything else under Attitude is body.
MOVING = {
    'Pivot_Propeller': 'prop',
    'Pivot_Aileron_L': 'aileron_l',
    'Pivot_Aileron_R': 'aileron_r',
    'Pivot_Elevator': 'elevator',
    'Pivot_Rudder': 'rudder',
}


def quat_matrix(q):
    x, y, z, w = [float(c) for c in q]
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def local(node):
    r = quat_matrix(node.get('rotation', [0, 0, 0, 1]))
    if 'scale' in node:
        r = r @ np.diag([float(s) for s in node['scale']])
    t = np.array(node.get('translation', [0.0, 0.0, 0.0]), dtype=float)
    return r, t


def primitives(js, buffers, mesh_index):
    """(positions, normals, uvs, triangles, material) per primitive."""
    out = []
    for prim in js['meshes'][mesh_index]['primitives']:
        if prim.get('mode', 4) != 4:
            raise ValueError('An-2: non-triangle primitive')
        a = prim['attributes']
        p = np.array(gltf_read._accessor(js, buffers, a['POSITION']), dtype=float)
        n = np.array(gltf_read._accessor(js, buffers, a['NORMAL']), dtype=float)
        t = np.array(gltf_read._accessor(js, buffers, a['TEXCOORD_0']), dtype=float)
        idx = np.array(gltf_read._accessor(js, buffers, prim['indices']), dtype=int)
        out.append((p, n, t, idx.reshape(-1, 3), prim.get('material', 0)))
    return out


class Chunks(object):
    """Triangles collected in the glTF body frame (u), before the mirror."""

    def __init__(self):
        self.P, self.N, self.T, self.F = [], [], [], []
        self.count = 0

    def add(self, p, n, t, f):
        self.P.append(p)
        self.N.append(n)
        self.T.append(t)
        self.F.append(f + self.count)
        self.count += len(p)

    def arrays(self):
        if not self.P:
            return (np.zeros((0, 3)), np.zeros((0, 3)), np.zeros((0, 2)),
                    np.zeros((0, 3), dtype=int))
        return (np.vstack(self.P), np.vstack(self.N), np.vstack(self.T),
                np.vstack(self.F))


def to_mesh(name, chunks):
    """Metres, mirrored x, re-wound, v flipped (glTF image origin is top-left,
    Unity's texture origin bottom-left), degenerate triangles dropped."""
    p, n, t, f = chunks.arrays()
    m = Mesh(name)
    p = p / U_PER_M
    for v in p:
        m.V.append((-float(v[0]), float(v[1]), float(v[2])))
    for v in n:
        v = np.array([-v[0], v[1], v[2]], dtype=float)
        ln = np.linalg.norm(v)
        m.N.append(tuple(v / ln) if ln > 1e-9 else (0.0, 1.0, 0.0))
    for uv in t:
        m.T.append((min(1.0, max(0.0, float(uv[0]))),
                    min(1.0, max(0.0, 1.0 - float(uv[1])))))
    dropped = 0
    V = np.array(m.V) if m.V else np.zeros((0, 3))
    for a, b, c in f:
        if np.linalg.norm(np.cross(V[b] - V[a], V[c] - V[a])) * 0.5 < 1e-10:
            dropped += 1
            continue
        m.IDX.extend((int(a), int(c), int(b)))
    m.dropped = dropped
    return m


def unity(v):
    """A glTF point in u -> a Unity point in metres."""
    return (-float(v[0]) / U_PER_M, float(v[1]) / U_PER_M, float(v[2]) / U_PER_M)


def unity_axis(a):
    a = np.array(a, dtype=float)
    a = a / np.linalg.norm(a)
    return (float(a[0]), -float(a[1]), -float(a[2]))


def main():
    check = '--check' in sys.argv
    js, buffers = gltf_read._load_glb(SOURCE)
    nodes = js['nodes']
    by_name = {n.get('name'): i for i, n in enumerate(nodes)}
    if 'Attitude' not in by_name:
        raise ValueError('An-2 GLB lacks the Attitude node')
    attitude = nodes[by_name['Attitude']]
    q = attitude.get('rotation', [0, 0, 0, 1])
    parked_deg = math.degrees(2.0 * math.asin(abs(float(q[0]))))

    body = Chunks()
    glass = Chunks()
    parts = {}
    pivots = {}
    fuse = Chunks()          # the fuselage alone, for seats and the hull box
    lower = Chunks()         # the lower wing, for its box
    wheels = {}              # wheel pivot -> (centre u, radius u)

    def lod0_meshes(i):
        for c in nodes[i].get('children', []):
            nm = nodes[c].get('name', '')
            if 'mesh' in nodes[c] and (nm.endswith('_LOD0') or nm == 'Airscrew_LOD0'):
                yield c

    for ci in attitude.get('children', []):
        node = nodes[ci]
        name = node.get('name', '')
        if name.startswith('Socket_'):
            continue
        r, t = local(node)
        moving = MOVING.get(name)
        meshes = list(lod0_meshes(ci))
        if not meshes:
            raise ValueError('An-2: %s has no LOD0 mesh' % name)
        for mi in meshes:
            for p, n, uv, f, mat in primitives(js, buffers, nodes[mi]['mesh']):
                if moving:
                    # Hinge frame: origin at the pivot, the body's axes.
                    pp = p @ r.T
                    nn = n @ r.T
                else:
                    pp = p @ r.T + t
                    nn = n @ r.T
                if mat == 1:
                    if moving:
                        raise ValueError('An-2: glass on a moving part ' + name)
                    glass.add(pp, nn, uv, f)
                    continue
                if moving:
                    parts.setdefault(moving, Chunks()).add(pp, nn, uv, f)
                else:
                    body.add(pp, nn, uv, f)
                if name == 'Part_Fuselage':
                    fuse.add(pp, nn, uv, f)
                if name.startswith('Part_Wing_Lower'):
                    lower.add(pp, nn, uv, f)
                if name.startswith('Pivot_Wheel'):
                    wheels.setdefault(name, []).append(pp)
        if moving:
            axis = r @ np.array([1.0, 0.0, 0.0])
            pivots[moving] = (unity(t), unity_axis(axis))

    for want in MOVING.values():
        if want not in parts:
            raise ValueError('An-2: moving part missing: ' + want)

    meshes = {'body': to_mesh('An-2 body', body), 'glass': to_mesh('An-2 glass', glass)}
    for part, ch in parts.items():
        meshes[part] = to_mesh('An-2 ' + part, ch)

    # ---------------------------------------------------------- measurements
    bv = np.array(meshes['body'].V)
    span = bv[:, 0].max() - bv[:, 0].min()
    # Moving parts are stored about their hinge; put them back for the length.
    allv = np.vstack([np.array(m.V) + (np.array(pivots[k][0]) if k in pivots else 0.0)
                      for k, m in meshes.items()])
    length = allv[:, 2].max() - allv[:, 2].min()
    height = bv[:, 1].max()
    for what, got in (('span_upper_m', span), ('length_m', length)):
        want = PUBLISHED[what]
        off = abs(got - want) / want * 100.0
        print('%-14s model %.3f m, published %.3f m, off %.2f %%' % (what, got, want, off))
        if off > 0.5:
            raise ValueError('An-2 %s is off by %.2f %% - the GLB is not the '
                             'expected scale (u = m x %.1f)' % (what, off, U_PER_M))

    # Contact points: the bottom of each wheel in the flight-line frame.
    contact = {}
    for name, chunks in wheels.items():
        v = np.vstack(chunks) / U_PER_M
        c = (-(v[:, 0].max() + v[:, 0].min()) * 0.5, float(v[:, 1].min()),
             (v[:, 2].max() + v[:, 2].min()) * 0.5)
        contact[name.replace('Pivot_Wheel_', '').lower()] = c
    if sorted(contact) != ['l', 'r', 'tail']:
        raise ValueError('An-2: wheels found: %s' % sorted(contact))
    tail = contact['tail']
    main_y = (contact['l'][1] + contact['r'][1]) * 0.5
    # The pitch at which the tail wheel touches the ground plane through the
    # main wheels' contact: that is the parked three-point attitude.
    geo_pitch = math.degrees(math.atan2(tail[1] - main_y, -tail[2]))
    print('parked pitch: Attitude node %.2f deg, from the wheels %.2f deg'
          % (parked_deg, geo_pitch))

    # Fuselage: belly and roof along its length.
    fv = np.array(to_mesh('fuse', fuse).V)
    def slice_at(z, half=0.25):
        s = fv[np.abs(fv[:, 2] - z) < half]
        return float(s[:, 1].min()), float(s[:, 1].max()), float(np.abs(s[:, 0]).max())
    gv = np.array(meshes['glass'].V)
    # The cockpit greenhouse: the glass forward of the wing's leading edge.
    front = gv[gv[:, 2] > gv[:, 2].max() - 1.4]
    cockpit_z = float(front[:, 2].mean())
    cb, cr, cw = slice_at(cockpit_z - 0.4)
    floor_y = cb + 0.30
    pilot = (-0.42, floor_y, cockpit_z - 0.55)
    cabin_z = [-0.6 - 0.75 * i for i in range(6)]
    kb, kr, kw = slice_at(-2.5)
    cabin_floor = kb + 0.30
    fz0, fz1 = float(fv[:, 2].min()), float(fv[:, 2].max())
    fy0, fy1 = float(fv[:, 1].min()), float(fv[:, 1].max())
    fx = float(np.abs(fv[:, 0]).max())
    lv = np.array(to_mesh('lower', lower).V)
    door_node = nodes[by_name['Pivot_Door_Cargo']]
    door_at = unity(door_node['translation'])

    print('cockpit glass z %.2f m, cockpit belly %.2f roof %.2f, cabin belly %.2f roof %.2f'
          % (cockpit_z, cb, cr, kb, kr))
    print('pilot seat (%.2f, %.2f, %.2f) m; cargo door hinge (%.2f, %.2f, %.2f) m'
          % (pilot + door_at))
    print('scale: capsule %.1f u = %.2f m person; span %.1f u = %.1f capsules; '
          'cabin %.2f m = %.2f capsules; door %.2f m high'
          % (CAPSULE_U, CAPSULE_U / U_PER_M, span * U_PER_M, span * U_PER_M / CAPSULE_U,
             kr - kb, (kr - kb) * U_PER_M / CAPSULE_U, 1.46))

    for part, m in sorted(meshes.items()):
        m.validate()
        (x0, x1), (y0, y1), (z0, z1) = m.bounds()
        print('%-10s %6d vert %6d tri  x %6.2f..%6.2f  y %6.2f..%6.2f  z %6.2f..%6.2f'
              % (part, len(m.V), len(m.IDX) // 3, x0, x1, y0, y1, z0, z1))
        if len(m.V) > 65535:
            raise ValueError('An-2 %s exceeds a 16-bit index buffer' % part)

    if check:
        return 0

    for part, m in meshes.items():
        m.write(os.path.join(OUT, 'an2_%s.ndmesh' % part))

    # The atlas and the normal map come straight out of the GLB's images.
    def image(index):
        import io
        img = js['images'][index]
        view = js['bufferViews'][img['bufferView']]
        buf = buffers[view.get('buffer', 0)]
        start = view.get('byteOffset', 0)
        return Image.open(io.BytesIO(bytes(buf[start:start + view['byteLength']])))
    mat = js['materials'][0]
    albedo = image(js['textures'][mat['pbrMetallicRoughness']['baseColorTexture']['index']]['source'])
    albedo.convert('RGB').resize((ATLAS, ATLAS), Image.LANCZOS).save(
        os.path.join(OUT, 'an2_diffuse.png'), optimize=True)
    normal = image(js['textures'][mat['normalTexture']['index']]['source'])
    normal = normal.convert('RGB')
    if normal.size != (ATLAS, ATLAS):
        normal = normal.resize((ATLAS, ATLAS), Image.LANCZOS)
    # Mirror: the model's x is negated, so the tangent frame's handedness flips
    # with it - the red (tangent-x) channel is inverted to keep the lighting.
    r, g, b = normal.split()
    normal = Image.merge('RGB', (r.point(lambda c: 255 - c), g, b))
    normal.save(os.path.join(OUT, 'an2_normal.png'), optimize=True)

    with open(os.path.join(OUT, 'an2_rig.txt'), 'w', encoding='ascii', newline='\n') as f:
        f.write('# Written by an2_import.py from assets/an2_gameready/an2_flyable.glb. Do not edit.\n')
        f.write('# REAL METRES, Unity axes (+x starboard, +y up, +z nose), flight-line frame,\n')
        f.write('# origin on the ground under the middle of the main axle.\n')
        f.write('scale %.3f %.3f\n' % (U_PER_M, CAPSULE_U))
        f.write('# pivot part x y z  ax ay az   (AngleAxis(angle, axis) about the pivot)\n')
        for part in ('prop', 'aileron_l', 'aileron_r', 'elevator', 'rudder'):
            (x, y, z), (ax, ay, az) = pivots[part]
            f.write('pivot %s %.5f %.5f %.5f %.5f %.5f %.5f\n' % (part, x, y, z, ax, ay, az))
        f.write('# contact wheel x y z   bottom of each tyre\n')
        for w in ('l', 'r', 'tail'):
            f.write('contact %s %.5f %.5f %.5f\n' % ((w,) + tuple(contact[w])))
        f.write('parked %.4f\n' % parked_deg)
        f.write('# seat index x y z   feet of the man; -1 is the pilot (port seat)\n')
        f.write('seat -1 %.4f %.4f %.4f\n' % pilot)
        for i, z in enumerate(cabin_z):
            f.write('seat %d %.4f %.4f %.4f\n' % (i, 0.45 if i % 2 else -0.45, cabin_floor, z))
        f.write('door %.4f %.4f %.4f\n' % door_at)
        f.write('# box name cx cy cz sx sy sz   hull colliders\n')
        f.write('box fuselage %.4f %.4f %.4f %.4f %.4f %.4f\n'
                % (0.0, (fy0 + fy1) * 0.5, (fz0 + fz1) * 0.5, fx * 2.0, fy1 - fy0, fz1 - fz0))
        f.write('box lowerwing %.4f %.4f %.4f %.4f %.4f %.4f\n'
                % (0.0, (lv[:, 1].min() + lv[:, 1].max()) * 0.5,
                   (lv[:, 2].min() + lv[:, 2].max()) * 0.5,
                   lv[:, 0].max() - lv[:, 0].min(),
                   max(0.2, lv[:, 1].max() - lv[:, 1].min()),
                   lv[:, 2].max() - lv[:, 2].min()))
        f.write('# measured: span length height (m), cockpit and cabin belly/roof (m)\n')
        f.write('span %.4f\nlength %.4f\nheight %.4f\n' % (span, length, height))
        f.write('cockpit %.4f %.4f\ncabin %.4f %.4f\n' % (cb, cr, kb, kr))
        f.write('wingtip %.4f %.4f %.4f\n' % (span * 0.5, float(bv[:, 1].max()) - 0.4,
                                              float(pivots['aileron_r'][0][2])))
    print('written: %d meshes, an2_diffuse.png, an2_normal.png, an2_rig.txt' % len(meshes))
    return 0


if __name__ == '__main__':
    sys.exit(main())

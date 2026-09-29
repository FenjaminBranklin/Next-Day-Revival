"""The Soviet 85 mm anti-aircraft gun 52-K (M1939) for the east airfield.

Run: python k52_build.py [--preview]

Task N6 (docs/ai/tasks/k52-flak.md). Builds the gun from its parts in real
metres, the way the gun is put together, and writes it in GAME UNITS (x 2.8,
docs/ai/style-bible.md section 1) as one ndmesh per moving part and LOD:

    k52_base_lod<n>.ndmesh     the cruciform carriage on the ground: the long
                               beam, the two side outriggers, four levelling
                               jacks with their foot plates, the pedestal
    k52_mount_lod<n>.ndmesh    the traversing top carriage: cheeks, the two
                               spring equilibrators, the small shield, the
                               layers' seats and handwheels, the fuze setter
    k52_cradle_lod<n>.ndmesh   the elevating cradle: sleeve, recuperator
                               above and buffer below the barrel, trunnions
    k52_barrel_lod<n>.ndmesh   what recoils: the breech ring and block, the
                               long L/55 tube and the muzzle brake
    k52_diffuse.png            1024 atlas, four 512 regions (olive paint,
                               darker shield paint, gun steel, black fittings)
    k52_normal.png             tangent-space normal map of the same atlas
    k52_rig.txt                pivots and marks in the parent part's frame,
                               game units: mount, cradle, barrel, muzzle,
                               the two seats, the sight eye, recoil stroke

Four LODs at about 1 : 0.5 : 0.25 : 0.1 triangles (style bible, Geometry and
LODs): fewer cylinder segments and the small fittings dropped as the level
rises. The LODGroup is set up at runtime by Revival.Flak.cs.

COORDINATES are Unity's: +x right, +y up, +z the bore at zero traverse and
elevation. Every triangle is wound so that the right-hand normal of its
corners is the outward normal stored with it, and closed bodies have a
positive signed volume - the convention verify.py [9] measures (ndmesh.py).

MEASURES (real 52-K, rounded; where the photo is the only source the number
is a judgement and says so):
    barrel 4.70 m (L/55) + muzzle brake 0.42 m, bore 85 mm
    trunnion axis 1.70 m over the ground (judgement: firing height ~2.2 m)
    cruciform span 5.8 m front to back, 5.4 m side to side (fits the airfield
    AA ring, inner radius 3.15 m)
    elevation -3..+82 deg, traverse 360 deg, recoil 0.55-0.70 m
    shield 1.9 m wide, 1.25 m high (the small one of the later guns)

--preview also writes docs/ai/tasks/k52-flak/k52.glb (the whole gun at every
LOD as k52_LOD<n> nodes plus its UCX_ collider boxes, barrel at 20 deg) for
style_compare.py, and review/b2_poses.png: the rig in three poses beside the
1.8 m NPC figure (B2).
"""
import json
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, "assets")
REVIEW = os.path.join(ROOT, "docs", "ai", "tasks", "k52-flak")
K = 2.8                      # game units per metre
LODS = 4
SEG = [24, 12, 8, 6]         # cylinder segments per LOD
ATLAS = 1024

# Pivots in metres (parent frame). The mount turns on the pedestal top, the
# cradle on the trunnions, the barrel slides back along the cradle's +z.
MOUNT_AT = (0.0, 0.92, 0.0)
CRADLE_AT = (0.0, 0.78, 0.18)        # in the mount frame: trunnion 1.70 m up
BARREL_AT = (0.0, 0.0, 0.0)          # in the cradle frame
MUZZLE_Z = 4.55                      # muzzle brake face, barrel frame
RECOIL = 0.65                        # metres, at zero elevation

# B2: the gunner's two views, in the MOUNT frame (they traverse with the gun,
# they do not elevate): over the shoulder behind the breech, high enough to
# clear the shield and the AA ring's sandbags; the sight, left of the barrel
# and above the shield top (1.62 m), so no part of the gun is ever between
# the camera and its near plane.
SHOULDER = (-1.10, 2.25, -2.70)
SIGHT = (-0.62, 1.86, 0.30)

# B2: box colliders (centre, size), metres, in the frame of the part named.
# None on the seats (the crew sits there) and none that recoils: the barrel's
# boxes ride on the cradle. The mount's and the cradle's are one kinematic
# compound at runtime.
COLLIDERS = [
    ("base", (0.0, 0.31, 0.0), (0.72, 0.62, 6.20)),        # long beam, jacks and feet
    ("base", (0.0, 0.28, 0.0), (5.84, 0.56, 0.36)),        # side outriggers
    ("base", (0.0, 0.74, 0.0), (1.10, 0.36, 1.10)),        # pedestal and traverse ring
    ("mount", (0.0, 0.14, -0.125), (1.10, 0.28, 1.65)),    # turntable and deck
    ("mount", (0.0, 0.63, -0.175), (0.82, 0.70, 1.55)),    # the cheeks
    ("mount", (0.50, 0.81, 0.42), (0.20, 1.06, 0.20)),     # equilibrators
    ("mount", (-0.50, 0.81, 0.42), (0.20, 1.06, 0.20)),
    ("mount", (-0.645, 0.96, 0.785), (0.95, 1.32, 0.04)),  # shield left of the slot
    ("mount", (0.645, 0.96, 0.785), (0.95, 1.32, 0.04)),   # shield right of the slot
    ("mount", (0.0, 0.41, 0.785), (0.34, 0.22, 0.04)),     # shield under the slot
    ("mount", (0.60, 0.515, -0.875), (0.36, 0.47, 0.35)),  # fuze setter
    ("cradle", (0.0, 0.02, 0.50), (0.34, 0.64, 2.24)),     # sleeve, recuperator, buffer
    ("cradle", (0.0, 0.0, -0.93), (0.36, 0.36, 0.64)),     # breech ring and tray
    ("cradle", (0.0, 0.0, 2.90), (0.22, 0.22, 3.32)),      # the tube and muzzle brake
]

# UV regions in the 2 x 2 atlas (u0, v0, u1, v1), v up (Unity).
REG = {
    "paint": (0.01, 0.51, 0.49, 0.99),
    "shield": (0.51, 0.51, 0.99, 0.99),
    "steel": (0.01, 0.01, 0.49, 0.49),
    "black": (0.51, 0.01, 0.99, 0.49),
}
REGION_M = 6.2               # metres of surface one region holds edge to edge


# ======================================================================
# a small mesh kit in Unity axes

def _n(v):
    l = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    return (v[0] / l, v[1] / l, v[2] / l) if l > 1e-12 else None


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def rot_x(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return ((1, 0, 0), (0, c, -s), (0, s, c))


def rot_y(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return ((c, 0, s), (0, 1, 0), (-s, 0, c))


def rot_z(deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    return ((c, -s, 0), (s, c, 0), (0, 0, 1))


def mul(a, b):
    return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))


def apply(m, v):
    return (m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
            m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
            m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2])


IDENT = ((1, 0, 0), (0, 1, 0), (0, 0, 1))


class Part(object):
    """One mesh: vertices, normals, uvs, triangles; every face is added with
    the side it faces and wound to match it."""

    def __init__(self, name):
        self.name = name
        self.V, self.N, self.T, self.I = [], [], [], []

    def face(self, pts, out, region, normals=None):
        """A convex planar polygon (3+ corners) facing `out`."""
        pts = list(pts)
        fn = None
        for i in range(1, len(pts) - 1):
            fn = _n(_cross(_sub(pts[i], pts[0]), _sub(pts[i + 1], pts[0])))
            if fn is not None:
                break
        if fn is None:
            return
        if _dot(fn, out) < 0:
            pts.reverse()
            if normals is not None:
                normals = list(reversed(normals))
            fn = (-fn[0], -fn[1], -fn[2])
        uvs = self._uv(pts, fn, region)
        base = len(self.V)
        for k, p in enumerate(pts):
            self.V.append(p)
            self.N.append(_n(normals[k]) if normals is not None else fn)
            self.T.append(uvs[k])
        for i in range(1, len(pts) - 1):
            a, b, c = base, base + i, base + i + 1
            if _n(_cross(_sub(self.V[b], self.V[a]), _sub(self.V[c], self.V[a]))) is None:
                continue
            self.I.extend((a, b, c))

    @staticmethod
    def _uv(pts, fn, region):
        ax = max(range(3), key=lambda i: abs(fn[i]))
        a1, a2 = [(2, 1), (0, 2), (0, 1)][ax]
        u0, v0, u1, v1 = REG[region]
        ru = [p[a1] / REGION_M for p in pts]
        rv = [p[a2] / REGION_M for p in pts]
        ou, ov = math.floor(min(ru) * 4.0) / 4.0, math.floor(min(rv) * 4.0) / 4.0
        ru = [x - ou for x in ru]
        rv = [x - ov for x in rv]
        su, sv = max(max(ru), 1.0), max(max(rv), 1.0)
        return [(u0 + (a / su) * (u1 - u0), v0 + (b / sv) * (v1 - v0)) for a, b in zip(ru, rv)]

    # ------------------------------------------------------------ solids

    def box(self, c, size, region, m=IDENT, at=(0.0, 0.0, 0.0)):
        """A box, centre c and size in its own frame, then turned by m about
        its centre and moved by `at`."""
        hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0

        def P(x, y, z):
            q = apply(m, (x, y, z))
            return (c[0] + q[0] + at[0], c[1] + q[1] + at[1], c[2] + q[2] + at[2])

        def O(v):
            return apply(m, v)
        self.face([P(-hx, -hy, hz), P(hx, -hy, hz), P(hx, hy, hz), P(-hx, hy, hz)], O((0, 0, 1)), region)
        self.face([P(-hx, -hy, -hz), P(-hx, hy, -hz), P(hx, hy, -hz), P(hx, -hy, -hz)], O((0, 0, -1)), region)
        self.face([P(hx, -hy, -hz), P(hx, hy, -hz), P(hx, hy, hz), P(hx, -hy, hz)], O((1, 0, 0)), region)
        self.face([P(-hx, -hy, -hz), P(-hx, -hy, hz), P(-hx, hy, hz), P(-hx, hy, -hz)], O((-1, 0, 0)), region)
        self.face([P(-hx, hy, -hz), P(-hx, hy, hz), P(hx, hy, hz), P(hx, hy, -hz)], O((0, 1, 0)), region)
        self.face([P(-hx, -hy, -hz), P(hx, -hy, -hz), P(hx, -hy, hz), P(-hx, -hy, hz)], O((0, -1, 0)), region)

    def span(self, x0, x1, y0, y1, z0, z1, region):
        self.box(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0), region)

    def prism(self, profile, z0, z1, region, m=IDENT, at=(0.0, 0.0, 0.0)):
        """A convex 2D profile (x, y, counter-clockwise) extruded along z."""
        def P(x, y, z):
            q = apply(m, (x, y, z))
            return (q[0] + at[0], q[1] + at[1], q[2] + at[2])
        n = len(profile)
        self.face([P(x, y, z1) for x, y in profile], apply(m, (0, 0, 1)), region)
        self.face([P(x, y, z0) for x, y in profile], apply(m, (0, 0, -1)), region)
        cx = sum(p[0] for p in profile) / n
        cy = sum(p[1] for p in profile) / n
        for i in range(n):
            a, b = profile[i], profile[(i + 1) % n]
            ex, ey = b[0] - a[0], b[1] - a[1]
            out = (ey, -ex, 0.0)
            mid = ((a[0] + b[0]) / 2 - cx, (a[1] + b[1]) / 2 - cy)
            if out[0] * mid[0] + out[1] * mid[1] < 0:
                out = (-out[0], -out[1], 0.0)
            self.face([P(a[0], a[1], z0), P(b[0], b[1], z0), P(b[0], b[1], z1), P(a[0], a[1], z1)],
                      apply(m, out), region)

    def cyl(self, p0, p1, r0, r1, seg, region, caps=True, cap_region=None):
        """A (tapered) cylinder from p0 to p1, smooth sides."""
        axis = _sub(p1, p0)
        ax = _n(axis)
        ref = (0.0, 1.0, 0.0) if abs(ax[1]) < 0.9 else (1.0, 0.0, 0.0)
        u = _n(_cross(ref, ax))
        v = _cross(ax, u)
        ring0, ring1, nr = [], [], []
        slope = (r0 - r1) / max(1e-6, math.sqrt(_dot(axis, axis)))
        for i in range(seg):
            a = 2.0 * math.pi * i / seg
            d = (u[0] * math.cos(a) + v[0] * math.sin(a), u[1] * math.cos(a) + v[1] * math.sin(a),
                 u[2] * math.cos(a) + v[2] * math.sin(a))
            ring0.append((p0[0] + d[0] * r0, p0[1] + d[1] * r0, p0[2] + d[2] * r0))
            ring1.append((p1[0] + d[0] * r1, p1[1] + d[1] * r1, p1[2] + d[2] * r1))
            nr.append(_n((d[0] + ax[0] * slope, d[1] + ax[1] * slope, d[2] + ax[2] * slope)))
        for i in range(seg):
            j = (i + 1) % seg
            mid = _n((nr[i][0] + nr[j][0], nr[i][1] + nr[j][1], nr[i][2] + nr[j][2]))
            self.face([ring0[i], ring0[j], ring1[j], ring1[i]], mid, region, [nr[i], nr[j], nr[j], nr[i]])
        if caps:
            cr = cap_region or region
            if r1 > 1e-4:
                self.face(ring1, ax, cr)
            if r0 > 1e-4:
                self.face(list(reversed(ring0)), (-ax[0], -ax[1], -ax[2]), cr)

    def tube_z(self, x, y, z0, z1, r0, r1, seg, region, caps=True):
        self.cyl((x, y, z0), (x, y, z1), r0, r1, seg, region, caps)

    def post(self, x, z, y0, y1, r, seg, region, caps=True):
        self.cyl((x, y0, z), (x, y1, z), r, r, seg, region, caps)

    def wheel(self, c, normal, r, seg, region, spokes=True):
        """A handwheel: rim of short cylinders, hub and spokes, facing `normal`."""
        ax = _n(normal)
        ref = (0.0, 1.0, 0.0) if abs(ax[1]) < 0.9 else (1.0, 0.0, 0.0)
        u = _n(_cross(ref, ax))
        v = _cross(ax, u)
        n = max(6, seg)
        pts = []
        for i in range(n):
            a = 2.0 * math.pi * i / n
            pts.append((c[0] + (u[0] * math.cos(a) + v[0] * math.sin(a)) * r,
                        c[1] + (u[1] * math.cos(a) + v[1] * math.sin(a)) * r,
                        c[2] + (u[2] * math.cos(a) + v[2] * math.sin(a)) * r))
        for i in range(n):
            self.cyl(pts[i], pts[(i + 1) % n], 0.014, 0.014, 5, region, caps=False)
        self.cyl((c[0] - ax[0] * 0.03, c[1] - ax[1] * 0.03, c[2] - ax[2] * 0.03),
                 (c[0] + ax[0] * 0.03, c[1] + ax[1] * 0.03, c[2] + ax[2] * 0.03), 0.03, 0.03, 6, region)
        if spokes:
            for k in range(3):
                self.cyl(c, pts[k * n // 3], 0.009, 0.009, 4, region, caps=False)
            h0 = pts[0]
            self.cyl(h0, (h0[0] + ax[0] * 0.09, h0[1] + ax[1] * 0.09, h0[2] + ax[2] * 0.09), 0.012, 0.012, 5, region)

    # ------------------------------------------------------------ output

    def tris(self):
        return len(self.I) // 3

    def scaled(self, k):
        p = Part(self.name)
        p.V = [(x * k, y * k, z * k) for x, y, z in self.V]
        p.N, p.T, p.I = list(self.N), list(self.T), list(self.I)
        return p

    def validate(self):
        bad = 0
        for i in range(0, len(self.I), 3):
            a, b, c = self.V[self.I[i]], self.V[self.I[i + 1]], self.V[self.I[i + 2]]
            fn = _cross(_sub(b, a), _sub(c, a))
            if _dot(fn, self.N[self.I[i]]) < 0:
                bad += 1
        uv = sum(1 for t in self.T if not (0.0 <= t[0] <= 1.0 and 0.0 <= t[1] <= 1.0))
        if bad or uv:
            raise ValueError("%s: %d triangles against their normal, %d uvs outside 0..1" % (self.name, bad, uv))

    def write(self, path):
        self.validate()
        with open(path, "wb") as f:
            f.write(b"NDMS")
            f.write(struct.pack("<ii", 1, len(self.V)))
            f.write(np.asarray(self.V, dtype="<f4").tobytes())
            f.write(np.asarray(self.N, dtype="<f4").tobytes())
            f.write(np.asarray(self.T, dtype="<f4").tobytes())
            f.write(struct.pack("<i", len(self.I)))
            f.write(np.asarray(self.I, dtype="<i4").tobytes())


# ======================================================================
# the gun, part by part (metres, each part in its own pivot frame)

def base(L):
    """The cruciform carriage (the ZU-8 platform with the wheels taken off):
    the long beam fore and aft, the two side outriggers folded down, a
    levelling jack with a round foot plate at each of the four ends, the
    pedestal in the middle."""
    p = Part("k52_base_lod%d" % L)
    s = SEG[L]
    # long beam, tapering toward both ends
    for sgn in (1, -1):
        prof = [(-0.36, 0.30), (0.36, 0.30), (0.36, 0.62), (-0.36, 0.62)]
        p.prism(prof, 0.0, 1.0, "paint", m=IDENT, at=(0, 0, 0) if sgn > 0 else (0, 0, -1.0))
        tip = [(-0.36, 0.30), (0.36, 0.30), (0.26, 0.52), (-0.26, 0.52)]
        p.prism(tip, 0.0, 1.85, "paint", m=rot_y(0 if sgn > 0 else 180), at=(0, 0, 1.0 * sgn))
    # side outriggers
    for sgn in (1, -1):
        prof = [(-0.16, 0.30), (0.16, 0.30), (0.12, 0.50), (-0.12, 0.50)]
        p.prism(prof, 0.34, 2.55, "paint", m=rot_y(90 * sgn))
        if L == 0:
            # hinge block and locking strut
            p.span(0.30 * sgn - 0.08, 0.30 * sgn + 0.08, 0.28, 0.58, -0.22, 0.22, "steel") if sgn > 0 else \
                p.span(-0.38, -0.22, 0.28, 0.58, -0.22, 0.22, "steel")
            p.cyl((0.40 * sgn, 0.60, 0.30), (1.30 * sgn, 0.47, 0.06), 0.03, 0.03, max(5, s // 3), "steel")
            p.cyl((0.40 * sgn, 0.60, -0.30), (1.30 * sgn, 0.47, -0.06), 0.03, 0.03, max(5, s // 3), "steel")
    # the four levelling jacks
    ends = [(0.0, 2.80), (0.0, -2.80), (2.62, 0.0), (-2.62, 0.0)]
    for x, z in ends:
        p.cyl((x, 0.0, z), (x, 0.06, z), 0.30, 0.30, s, "steel")                  # foot plate
        p.post(x, z, 0.06, 0.62, 0.055, max(6, s // 2), "steel")                 # screw
        p.post(x, z, 0.36, 0.72, 0.105, max(6, s // 2), "paint")                  # housing
        if L <= 1:
            p.cyl((x - 0.20, 0.70, z), (x + 0.20, 0.70, z), 0.018, 0.018, 5, "black")  # crank bar
        if L == 0:
            p.cyl((x, 0.72, z), (x, 0.80, z), 0.06, 0.04, max(6, s // 3), "steel")
    # pedestal and the traverse ring under the mount
    p.post(0, 0, 0.55, 0.86, 0.50, s, "paint")
    if L <= 2:
        p.post(0, 0, 0.86, 0.92, 0.62, s, "steel")
    if L <= 1:
        # tool boxes on the long beam, tow eye at the front
        p.span(-0.30, 0.30, 0.62, 0.84, 1.20, 1.75, "paint")
        p.span(-0.30, 0.30, 0.62, 0.84, -1.75, -1.20, "paint")
        p.cyl((0, 0.42, 2.85), (0, 0.42, 3.05), 0.07, 0.07, max(6, s // 2), "steel")
        # axle brackets where the travelling bogies were
        for z in (2.05, -2.05):
            p.span(-0.52, 0.52, 0.34, 0.56, z - 0.10, z + 0.10, "steel")
    if L == 0:
        for x, z in ends:                                                          # bolt heads on the foot plates
            for k in range(4):
                a = math.radians(45 + 90 * k)
                p.post(x + 0.2 * math.cos(a), z + 0.2 * math.sin(a), 0.06, 0.09, 0.025, 6, "steel")
    return p


def mount(L):
    """The top carriage on the traverse ring: two cheeks carrying the
    trunnions, two spring equilibrators, the shield, the layers' seats with
    their handwheels, the sight, the fuze setter, the loader's step."""
    p = Part("k52_mount_lod%d" % L)
    s = SEG[L]
    p.post(0, 0, 0.0, 0.10, 0.66, s, "paint")                                    # turntable
    p.span(-0.55, 0.55, 0.10, 0.28, -0.95, 0.70, "paint")                        # deck
    # cheeks, trapezoid side plates carrying the trunnions at y 0.78, z 0.18
    cheek = [(-0.95, 0.28), (0.60, 0.28), (0.42, 0.98), (-0.05, 0.98)]
    for sgn in (1, -1):
        prof = [(z, y) for z, y in cheek]
        p.prism(prof, 0.0, 0.07, "paint", m=((0, 0, 1), (0, 1, 0), (1, 0, 0)), at=(0.34 * sgn - 0.035, 0, 0))
        p.cyl((0.30 * sgn, 0.78, 0.18), (0.44 * sgn, 0.78, 0.18), 0.12, 0.12, max(8, s // 2), "steel")  # trunnion bearing
    # spring equilibrators: two tall cylinders ahead of the cheeks
    for sgn in (1, -1):
        p.post(0.50 * sgn, 0.42, 0.28, 1.28, 0.10, max(8, s // 2), "shield")
        p.post(0.50 * sgn, 0.42, 1.28, 1.34, 0.07, max(6, s // 3), "steel")
        if L == 0:
            p.cyl((0.50 * sgn, 1.30, 0.42), (0.36 * sgn, 0.95, 0.30), 0.03, 0.03, 6, "steel")
    # the small shield: a front plate with the barrel slot and two raked wings
    t = 0.012
    sy0, sy1, sz, sw = 0.30, 1.62, 0.78, 1.12
    p.span(-sw, -0.17, sy0, sy1, sz, sz + t, "shield")
    p.span(0.17, sw, sy0, sy1, sz, sz + t, "shield")
    p.span(-0.17, 0.17, sy0, 0.52, sz, sz + t, "shield")                           # under the slot
    for sgn in (1, -1):
        w = [(0.0, sy0), (0.40, sy0), (0.40, sy1 - 0.16), (0.0, sy1)]
        p.prism(w, 0.0, t, "shield", m=mul(rot_y(90 * sgn + (-25 if sgn > 0 else 25)), IDENT),
                at=(sw * sgn, 0, sz))
        if L <= 2:
            p.cyl((0.70 * sgn, 0.30, sz - 0.02), (0.36 * sgn, 0.10, 0.30), 0.025, 0.025, 6, "steel")  # stay
    if L <= 1:
        p.span(-sw, sw, sy1 - 0.03, sy1, sz - 0.04, sz + t, "shield")             # top lip
    if L == 0:
        for x in (-0.95, -0.5, 0.5, 0.95):                                         # rivets
            p.cyl((x, sy1 - 0.1, sz + t), (x, sy1 - 0.1, sz + t + 0.015), 0.018, 0.018, 6, "shield")
    # the two layers: traverse layer left, elevation layer right
    for sgn in (1, -1):
        x = 0.78 * sgn
        p.span(x - 0.20, x + 0.20, 0.44, 0.50, -0.55, -0.17, "black")                 # seat pan
        p.span(x - 0.20, x + 0.20, 0.50, 0.82, -0.60, -0.55, "black")                 # back
        if L <= 2:
            p.cyl((0.34 * sgn, 0.40, -0.35), (x, 0.40, -0.35), 0.035, 0.035, 6, "steel")   # seat arm
            p.post(x, -0.36, 0.10, 0.44, 0.03, 6, "steel")                                # seat post
        if L <= 1:
            p.span(x - 0.12, x + 0.12, 0.10, 0.14, -0.10, 0.20, "steel")                  # foot rest
        # handwheel ahead of each seat, facing the layer
        c = (0.56 * sgn, 0.92, 0.04)
        p.cyl((0.38 * sgn, 0.92, 0.04), (c[0], c[1], c[2] - 0.06), 0.03, 0.03, 6, "steel")
        if L == 0:
            p.wheel((c[0], c[1], c[2] - 0.06), (0, 0, -1), 0.15, 12, "black")
        else:
            p.cyl((c[0], c[1], c[2] - 0.05), (c[0], c[1], c[2] - 0.08), 0.15, 0.15, max(6, s // 2), "black")
    # sight on the left cheek, fuze setter at the right rear
    p.span(-0.52, -0.40, 0.98, 1.22, 0.05, 0.25, "steel")
    if L <= 2:
        p.tube_z(-0.46, 1.16, -0.18, 0.05, 0.035, 0.035, max(6, s // 3), "black")
    p.span(0.42, 0.78, 0.28, 0.75, -1.05, -0.70, "paint")                         # fuze setter box
    if L <= 1:
        p.tube_z(0.60, 0.60, -1.12, -1.05, 0.09, 0.09, max(6, s // 3), "black")    # setter mouth
        p.span(-0.40, 0.40, 0.10, 0.16, -1.30, -0.95, "steel")                    # loader's step
        p.post(0.46, 0.16, 0.28, 0.95, 0.03, 6, "steel")                           # elevation drive shaft
        p.post(-0.46, 0.16, 0.28, 0.95, 0.03, 6, "steel")                          # traverse drive shaft
    return p


def cradle(L):
    """The cradle round the barrel, trunnion axis at the origin: the sleeve,
    the recuperator above and the buffer below, the trunnion pins."""
    p = Part("k52_cradle_lod%d" % L)
    s = SEG[L]
    p.tube_z(0, 0, -0.55, 1.45, 0.155, 0.145, s, "paint")                      # sleeve
    p.tube_z(0, 0.25, -0.62, 1.55, 0.085, 0.085, max(8, s // 2), "paint")      # recuperator
    p.tube_z(0, -0.22, -0.50, 1.25, 0.07, 0.07, max(8, s // 2), "paint")        # buffer
    if L <= 2:
        p.span(-0.10, 0.10, 0.10, 0.20, -0.45, 1.40, "paint")                 # web to the recuperator
        p.span(-0.08, 0.08, -0.20, -0.10, -0.40, 1.15, "paint")
    p.cyl((-0.30, 0, 0), (0.30, 0, 0), 0.075, 0.075, max(8, s // 2), "steel")  # trunnion pins
    if L <= 1:
        p.tube_z(0, 0.25, 1.55, 1.62, 0.06, 0.06, max(6, s // 3), "steel")     # recuperator cap
        p.span(-0.22, 0.22, -0.18, 0.18, -0.58, -0.50, "steel")               # rear guide band
        p.span(0.14, 0.26, -0.14, 0.02, -0.30, 0.15, "steel")                 # elevation arc bracket
    return p


def barrel(L):
    """What recoils: the breech ring and the block behind the trunnions, the
    L/55 tube, the multi-baffle muzzle brake."""
    p = Part("k52_barrel_lod%d" % L)
    s = SEG[L]
    p.box((0, 0, -0.66), (0.32, 0.32, 0.46), "steel")                            # breech ring
    if L <= 1:
        p.span(-0.12, 0.12, -0.14, 0.14, -0.92, -0.88, "steel")                 # block face
        p.cyl((0.16, 0.02, -0.75), (0.34, 0.02, -0.75), 0.02, 0.02, 6, "steel")  # breech lever
        p.span(0.30, 0.38, -0.12, 0.05, -0.80, -0.70, "black")
        p.span(-0.22, 0.22, -0.25, -0.18, -1.25, -0.88, "steel")                 # loading tray
    p.tube_z(0, 0, -0.43, 0.10, 0.125, 0.125, s, "steel")                         # jacket
    if L <= 2:
        p.tube_z(0, 0, 0.10, 1.90, 0.095, 0.082, s, "steel")                      # tube
        p.tube_z(0, 0, 1.90, 4.13, 0.082, 0.068, s, "steel")
    else:
        p.tube_z(0, 0, 0.10, 4.13, 0.095, 0.068, s, "steel")
    # muzzle brake: body with baffle slots (rings)
    p.tube_z(0, 0, 4.13, 4.55, 0.098, 0.098, s, "black")
    if L <= 1:
        for z in (4.20, 4.30, 4.40):
            p.span(-0.105, 0.105, -0.05, 0.05, z, z + 0.06, "black")
    if L <= 1:
        p.tube_z(0, 0, 4.55, 4.56, 0.042, 0.042, s, "black", caps=True)            # bore face
    return p


# ======================================================================
# texture

def _noise(size, scale, seed):
    rng = np.random.RandomState(seed)
    n = max(2, size // scale)
    small = rng.rand(n, n).astype(np.float32)
    img = Image.fromarray((small * 255).astype(np.uint8)).resize((size, size), Image.BICUBIC)
    return np.asarray(img, dtype=np.float32) / 255.0


def _region_px(name):
    u0, v0, u1, v1 = REG[name]
    x0, x1 = int(u0 * ATLAS), int(u1 * ATLAS)
    y0, y1 = int((1.0 - v1) * ATLAS), int((1.0 - v0) * ATLAS)
    return x0, y0, x1, y1


def texture():
    """Olive 4BO paint worn to primer at edges, dust at the foot, oily gun
    steel, black fittings. B2: tuned to the style bible's "military paint,
    worn" row (section 4: value p50 ~0.21-0.27, saturation ~0.2, grime
    patches and rust runs painted into the albedo, never in the mesh), the
    way the H1 hangar's kit sheets carry their wear."""
    img = np.zeros((ATLAS, ATLAS, 3), np.float32)
    height = np.zeros((ATLAS, ATLAS), np.float32)
    base = {
        "paint": np.array([0.285, 0.290, 0.222]),
        "shield": np.array([0.262, 0.268, 0.205]),
        "steel": np.array([0.195, 0.195, 0.186]),
        "black": np.array([0.095, 0.095, 0.090]),
    }
    for k, name in enumerate(("paint", "shield", "steel", "black")):
        x0, y0, x1, y1 = _region_px(name)
        w, h = x1 - x0, y1 - y0
        big = _noise(max(w, h), 96, 11 + k)[:h, :w]
        mid = _noise(max(w, h), 24, 21 + k)[:h, :w]
        fine = _noise(max(w, h), 4, 31 + k)[:h, :w]
        col = np.ones((h, w, 3), np.float32) * base[name]
        shade = 0.86 + 0.18 * big + 0.08 * mid + 0.05 * fine
        col *= shade[:, :, None]
        if name in ("paint", "shield"):
            # chipped paint: dark primer/rust flecks where the mid noise peaks
            chips = np.clip((mid * 0.6 + fine * 0.4 - 0.72) * 9.0, 0, 1)
            rust = np.array([0.24, 0.20, 0.16])
            col = col * (1 - chips[:, :, None] * 0.6) + rust * chips[:, :, None] * 0.6
            # dust toward the bottom of the region (the lower parts of the gun)
            ramp = np.linspace(0, 1, h, dtype=np.float32)[:, None] ** 3
            dust = np.array([0.40, 0.37, 0.31])
            col = col * (1 - ramp[:, :, None] * 0.35) + dust * ramp[:, :, None] * 0.35
            # streaks running down
            streak = _noise(max(w, h), 8, 41 + k)[:h, :w]
            streak = np.asarray(Image.fromarray((streak * 255).astype(np.uint8)).resize((w, h // 8 or 1)).resize((w, h)),
                                np.float32) / 255.0
            col *= (0.94 + 0.08 * streak)[:, :, None]
            # rust runs from the chips downwards (v runs top to bottom here)
            run = np.zeros_like(chips)
            for dy in range(1, 40, 3):
                run[dy:] = np.maximum(run[dy:], chips[:-dy] * (1.0 - dy / 40.0))
            run *= np.clip((streak - 0.35) * 2.5, 0, 1)
            rust2 = np.array([0.30, 0.19, 0.12])
            col = col * (1 - run[:, :, None] * 0.45) + rust2 * run[:, :, None] * 0.45
            # grime: oil and soot patches where the big noise is low
            grime = np.clip((0.34 - big) * 5.0, 0, 1) * np.clip((0.6 - mid) * 3.0, 0, 1)
            col *= (1.0 - 0.55 * grime)[:, :, None]
            hgt = 0.5 + 0.3 * mid - chips * 0.4
        elif name == "steel":
            oil = np.clip((big - 0.55) * 3.0, 0, 1)
            col = col * (1 - oil[:, :, None] * 0.45)
            hgt = 0.5 + 0.15 * fine
        else:
            hgt = 0.5 + 0.1 * mid
        img[y0:y1, x0:x1] = np.clip(col, 0, 1)
        height[y0:y1, x0:x1] = hgt
    rgb = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8), "RGB")
    # tangent-space normal from the height (strength kept low: paint, not relief)
    gy, gx = np.gradient(height * 3.0)
    nx, ny, nz = -gx, gy, np.ones_like(gx)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    nrm = np.stack([nx / ln, ny / ln, nz / ln], axis=2) * 0.5 + 0.5
    normal = Image.fromarray((nrm * 255).astype(np.uint8), "RGB").filter(ImageFilter.SMOOTH)
    return rgb, normal


# ======================================================================
# the preview GLB (right-handed glTF: x negated, winding reversed)

def glb(path, groups, tex_png):
    """`groups`: (node name, [parts in world metres x K]); one node and one
    mesh per group, so style_compare.py sees the LODs (*_LOD<n>) and the
    colliders (UCX_*) by their node names."""
    chunks, views, accessors, meshes, nodes = [], [], [], [], []
    off = [0]

    def view(b, target):
        pad = (4 - len(b) % 4) % 4
        v = {"buffer": 0, "byteOffset": off[0], "byteLength": len(b)}
        if target:
            v["target"] = target
        views.append(v)
        chunks.append(b + b"\0" * pad)
        off[0] += len(b) + pad
        return len(views) - 1

    for name, parts_world in groups:
        pos, nor, uv, idx = [], [], [], []
        for p in parts_world:
            base = len(pos)
            pos.extend([(-x, y, z) for x, y, z in p.V])
            nor.extend([(-x, y, z) for x, y, z in p.N])
            uv.extend([(u, 1.0 - v) for u, v in p.T])
            for i in range(0, len(p.I), 3):
                idx.extend((base + p.I[i], base + p.I[i + 2], base + p.I[i + 1]))
        P = np.asarray(pos, "<f4")
        Nn = np.asarray(nor, "<f4")
        U = np.asarray(uv, "<f4")
        Ix = np.asarray(idx, "<u4")
        a0 = len(accessors)
        accessors.append({"bufferView": view(P.tobytes(), 34962), "componentType": 5126, "count": len(P),
                          "type": "VEC3", "min": P.min(0).tolist(), "max": P.max(0).tolist()})
        accessors.append({"bufferView": view(Nn.tobytes(), 34962), "componentType": 5126, "count": len(Nn),
                          "type": "VEC3"})
        accessors.append({"bufferView": view(U.tobytes(), 34962), "componentType": 5126, "count": len(U),
                          "type": "VEC2"})
        accessors.append({"bufferView": view(Ix.tobytes(), 34963), "componentType": 5125, "count": len(Ix),
                          "type": "SCALAR"})
        meshes.append({"name": name, "primitives": [{"attributes": {"POSITION": a0, "NORMAL": a0 + 1,
                                                                    "TEXCOORD_0": a0 + 2},
                                                     "indices": a0 + 3, "material": 0}]})
        nodes.append({"name": name, "mesh": len(meshes) - 1})
    png = open(tex_png, "rb").read()
    img_view = view(png, None)
    binary = b"".join(chunks)
    doc = {
        "asset": {"version": "2.0", "generator": "k52_build.py"},
        "scene": 0, "scenes": [{"nodes": [len(nodes)]}],
        "nodes": nodes + [{"name": "52-K", "children": list(range(len(nodes)))}],
        "meshes": meshes,
        "materials": [{"name": "k52_paint", "pbrMetallicRoughness": {
            "baseColorTexture": {"index": 0}, "metallicFactor": 0.15, "roughnessFactor": 0.7}}],
        "textures": [{"source": 0}],
        "images": [{"bufferView": img_view, "mimeType": "image/png"}],
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{"byteLength": len(binary)}],
    }
    js = json.dumps(doc).encode("utf-8")
    js += b" " * ((4 - len(js) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(binary)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(binary), 0x004E4942) + binary)


def _moved(p, m, at):
    q = Part(p.name)
    q.V = [tuple(a + b for a, b in zip(apply(m, v), at)) for v in p.V]
    q.N = [apply(m, n) for n in p.N]
    q.T, q.I = list(p.T), list(p.I)
    return q


def _frames(pitch_deg, yaw_deg, recoil):
    """(rotation, origin) of each part in the gun's frame (metres)."""
    rm = rot_y(yaw_deg)
    rc = mul(rm, rot_x(-pitch_deg))
    ca = tuple(a + b for a, b in zip(MOUNT_AT, apply(rm, CRADLE_AT)))
    ba = tuple(a + b for a, b in zip(ca, apply(rc, (0.0, 0.0, -RECOIL * recoil))))
    return {"base": (IDENT, (0.0, 0.0, 0.0)), "mount": (rm, MOUNT_AT), "cradle": (rc, ca), "barrel": (rc, ba)}


def posed(pitch_deg, yaw_deg=0.0, recoil=0.0, L=0):
    """The whole gun at LOD `L` in one frame (metres): the parts at their
    pivots, the mount at `yaw_deg`, the cradle at `pitch_deg`, the barrel
    `recoil` (0..1) of its stroke back."""
    f = _frames(pitch_deg, yaw_deg, recoil)
    out = []
    for name, fn in (("base", base), ("mount", mount), ("cradle", cradle), ("barrel", barrel)):
        m, at = f[name]
        out.append(_moved(fn(L), m, at))
    return out


def posed_colliders(pitch_deg, yaw_deg=0.0):
    """COLLIDERS as box meshes in the gun's frame, posed like posed()."""
    f = _frames(pitch_deg, yaw_deg, 0.0)
    out = []
    for i, (part, c, sz) in enumerate(COLLIDERS):
        b = Part("UCX_k52_%s_%02d" % (part, i))
        b.box(c, sz, "black")
        m, at = f[part]
        out.append(_moved(b, m, at))
    return out


POSES = [
    ("ready: yaw 0, 12 deg", 12.0, 0.0, 0.0),
    ("firing: yaw 35, 45 deg, recoiled", 45.0, 35.0, 1.0),
    ("max: yaw -60, 82 deg", 82.0, -60.0, 0.0),
]


def preview_sheet(path):
    """B2 review: the rig in three poses beside the 1.8 m NPC figure (5.0 u),
    side and three-quarter views, one orthographic scale for every tile, the
    style_compare.py renderer (GW_Scene_1 light, flat shading)."""
    import tempfile
    import style_compare as sc
    from PIL import ImageDraw
    tmp = tempfile.mkdtemp(prefix="k52_")
    models = []
    for k, (label, pitch, yaw, rec) in enumerate(POSES):
        g = os.path.join(tmp, "pose%d.glb" % k)
        glb(g, [("k52_LOD0", [q.scaled(K) for q in posed(pitch, yaw, rec, 0)])],
            os.path.join(OUT, "k52_diffuse.png"))
        models.append((label, sc.load_gltf(g)))
    views = [("side", -np.pi / 2, 0.0), ("three-quarter", -np.pi / 2 + 0.62, 0.42)]
    px = 420
    fig_z = -3.1 * K - 3.5          # beside the rear foot, clear of the gun in every view
    lo_all = np.min([sc.bounds(sc.lod0(m))[0] for _l, m in models], axis=0)
    hi_all = np.max([sc.bounds(sc.lod0(m))[1] for _l, m in models], axis=0)
    lo_all[2] = min(lo_all[2], fig_z)
    ext = []
    for _n2, yaw, pitch in views:
        R = sc.view_matrix(yaw, pitch)
        corners = np.array([[x, y, z] for x in (lo_all[0], hi_all[0]) for y in (lo_all[1], hi_all[1])
                            for z in (lo_all[2], hi_all[2])])
        q = corners @ R.T
        ext.append(max(np.ptp(q[:, 0]), np.ptp(q[:, 1])))
    mpp = max(ext) / (px * 0.92)
    origin = (lo_all + hi_all) / 2
    W, H = px * len(models) + 20, px * len(views) + 90
    img = Image.new("RGB", (W, H), (24, 26, 28))
    d = ImageDraw.Draw(img)
    font = sc._font(15)
    d.text((10, 8), "52-K rig (B2): base / mount (yaw) / cradle (pitch) / barrel (recoil), LOD0, "
                    "red block = NPC 5.0 u (1.8 m x 2.8)", fill=(240, 200, 120), font=font)
    d.text((10, 28), "one orthographic scale %.3f u/px; trunnion 1.70 m, muzzle 4.74 m ahead of the pedestal, "
                     "shield top 2.54 m" % mpp, fill=(200, 200, 200), font=sc._font(13))
    for c, (label, m) in enumerate(models):
        d.text((10 + c * px, 52), label, fill=(150, 210, 240), font=font)
        fig = sc.figure_part(-0.75, fig_z, 0.0)
        for r, (vname, yaw, pitch) in enumerate(views):
            R = sc.view_matrix(yaw, pitch)
            tile, _ = sc.render_view(m, R, mpp, origin, px, px, [fig])
            img.paste(Image.fromarray(tile), (10 + c * px, 76 + r * px))
            d.text((14 + c * px, 80 + r * px), vname, fill=(20, 20, 20), font=sc._font(13))
    img.save(path)


def main():
    os.makedirs(OUT, exist_ok=True)
    builders = (("base", base), ("mount", mount), ("cradle", cradle), ("barrel", barrel))
    report = []
    for name, fn in builders:
        counts = []
        for L in range(LODS):
            p = fn(L).scaled(K)
            p.write(os.path.join(OUT, "k52_%s_lod%d.ndmesh" % (name, L)))
            counts.append(p.tris())
        report.append((name, counts))
    rgb, normal = texture()
    rgb.save(os.path.join(OUT, "k52_diffuse.png"), optimize=True)
    normal.save(os.path.join(OUT, "k52_normal.png"), optimize=True)
    with open(os.path.join(OUT, "k52_rig.txt"), "w", newline="\n") as f:
        f.write("# 52-K rig, game units (x 2.8), written by k52_build.py\n")
        f.write("# key x y z   (in the parent part's frame)\n")

        def line(key, v):
            f.write("%s %.4f %.4f %.4f\n" % (key, v[0] * K, v[1] * K, v[2] * K))
        line("mount", MOUNT_AT)
        line("cradle", CRADLE_AT)
        line("barrel", BARREL_AT)
        line("muzzle", (0.0, 0.0, MUZZLE_Z))
        line("seat_gunner", (-0.78, 0.47, -0.36))
        line("seat_loader", (0.78, 0.47, -0.36))
        line("eye", (-0.46, 0.38, -0.25))
        line("recoil", (0.0, 0.0, RECOIL))
        line("shoulder", SHOULDER)
        line("sight", SIGHT)
        f.write("# box <part> cx cy cz sx sy sz   (collider, in the part's frame)\n")
        for part, c, s in COLLIDERS:
            f.write("box %s %.4f %.4f %.4f %.4f %.4f %.4f\n"
                    % (part, c[0] * K, c[1] * K, c[2] * K, s[0] * K, s[1] * K, s[2] * K))
    total = [sum(c[L] for _n2, c in report) for L in range(LODS)]
    for name, counts in report:
        print("  %-7s %s" % (name, " / ".join(str(c) for c in counts)))
    print("  total   %s  (ratio %s)" % (" / ".join(str(t) for t in total),
                                       " : ".join("%.2f" % (t / float(total[0])) for t in total)))
    if "--preview" in sys.argv:
        os.makedirs(REVIEW, exist_ok=True)
        groups = [("k52_LOD%d" % L, [q.scaled(K) for q in posed(20.0, 0.0, 0.0, L)]) for L in range(LODS)]
        groups += [(c.name, [c.scaled(K)]) for c in posed_colliders(20.0)]
        glb(os.path.join(REVIEW, "k52.glb"), groups, os.path.join(OUT, "k52_diffuse.png"))
        print("  preview  %s" % os.path.join(REVIEW, "k52.glb"))
        os.makedirs(os.path.join(REVIEW, "review"), exist_ok=True)
        sheet = os.path.join(REVIEW, "review", "b2_poses.png")
        preview_sheet(sheet)
        print("  poses    %s" % sheet)


if __name__ == "__main__":
    main()

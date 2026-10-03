"""C W3 tower rebuild + E W1: offline proof that the command room is INSIDE
the cab, the radar console stands in it ON the measured cab floor (E W1; C W3
had it on the main roof), and nothing the mod builds on or in the tower
floats or cuts into anything.

Every box the mod adds there (stair threshold, five sandbag posts, the radar
console's own primitives and collider in the cab, the Z TC1 command room's props and colliders, the Z M4 supply order console,
and the antenna's support on the cab roof)
is tested against ALL shipped collider triangles within 50 m (exact
box/triangle separating-axis test) and against each other, and must rest on
the measured surface or on another box (2 cm). The reachable paths (stairs,
roof walk round every obstacle, console seat, cab) are the production C#
routes dumped by tower_roof_check.py and swept as capsules by
tower_stairs_check.py; main() runs that too.
"""
from pathlib import Path
import json
import re
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "research/out/c-w3-tower"
K = 2.8
GAP = 0.02          # metres: resting tolerance (no visible float)
SINK = 0.001        # metres: boxes shrink this much before the intersection test


def core_const(name):
    core = (ROOT / "Revival.TowerRoofCore.cs").read_text(encoding="ascii")
    return float(re.search(r"\b" + name + r" = ([-\d.]+)f?[,;]", core).group(1))


def box(name, centre, size, rot=None, group="", allow_floor=False):
    return dict(name=name, c=np.asarray(centre, float), h=np.asarray(size, float) / 2,
                R=np.eye(3) if rot is None else np.asarray(rot, float), group=group,
                allow_floor=allow_floor)


def console_boxes(roof):
    """TowerRadar RadarModel.Console(): its Box/Post primitives, tower metres."""
    src = (ROOT / "Revival.TowerRadar.cs").read_text(encoding="ascii")
    body = src[src.index("internal static Transform Console(Scene scene)"):]
    body = body[:body.index('Finish(b, "Console", true);')]
    num = r"(-?[\d.]+)f"
    vec = r"new Vector3\(" + num + r", " + num + r", " + num + r"\)"
    x0, z0 = core_const("ConsoleX"), core_const("ConsoleZ")
    out = []
    for m in re.finditer(r"\bBox\(b, " + vec + r", " + vec, body):
        v = [float(g) for g in m.groups()]
        out.append(box("console part %d" % len(out), (x0 + v[0], roof + v[1], z0 + v[2]), v[3:], group="console"))
    for m in re.finditer(r"\bPost\(b, " + vec + r", " + num + r", " + num, body):
        v = [float(g) for g in m.groups()]
        out.append(box("console chair post", (x0 + v[0], roof + v[1] + v[4] / 2, z0 + v[2]),
                       (2 * v[3], v[4], 2 * v[3]), group="console"))
    whole = src[src.index("internal static Transform Console(Scene scene)"):]
    m = re.search(r"bc.center = " + vec + r" . K;\s*bc.size = " + vec, whole)
    v = [float(g) for g in m.groups()]
    collider = box("radar console collider", (x0 + v[0], roof + v[1], z0 + v[2]), v[3:], group="console")
    return out, collider


def threshold_boxes():
    from tower_stairs_check import layout
    points = layout()
    width, thick = core_const("ThresholdWidth"), core_const("ThresholdThick")
    out = []
    for i in range(int(core_const("ThresholdFirst")), int(core_const("ThresholdLast")) + 1):
        a, b = points[i - 1], points[i]
        d = b - a
        axis = d / np.linalg.norm(d)
        side = np.cross(axis, [0, 1, 0]); side /= np.linalg.norm(side)
        normal = np.cross(side, axis)
        out.append(box("StairThreshold_%d" % i, (a + b) / 2 - normal * thick / 2,
                       (np.linalg.norm(d), thick, width), np.column_stack((axis, normal, side)),
                       group="threshold", allow_floor=True))
    return out


def antenna_support():
    """CompactRadar's physical/rendered FieldSupport, from its production recipe."""
    src = (ROOT / "Revival.AirfieldObjects.cs").read_text(encoding="ascii")
    core = (ROOT / "Revival.AirfieldObjectsCore.cs").read_text(encoding="ascii")
    radar = (ROOT / "Revival.TowerRadar.cs").read_text(encoding="ascii")
    assert "RoofM = TowerRoofCore.CabRoofY;" in radar
    scale = float(re.search(r"RadarScale = ([\d.]+)f", core).group(1))
    lift = float(re.search(r"RadarLiftM = ([\d.]+)f", core).group(1))
    root = re.search(r"root.position = TowerRadar.TowerPoint\(new Vector3\(([\d.]+)f,\s*"
                     r"TowerRadar.RoofM \+ AirfieldObjectsCore.RadarLiftM - ([\d.]+)f \* "
                     r"AirfieldObjectsCore.RadarScale, ([\d.]+)f\)\)", src)
    centre = re.search(r"support.transform.localPosition = new Vector3\(0f, "
                       r"\(([\d.]+)f - \(([\d.]+)f - ([\d.]+)f\*s\)\)/s, 0f\)", src)
    size = re.search(r"support.transform.localScale = new Vector3\(([\d.]+)f/s, "
                     r"([\d.]+)f/s, ([\d.]+)f/s\)", src)
    assert root and centre and size, "CompactRadar support recipe changed; update the geometry model"
    x, pivot, z = map(float, root.groups())
    local_y, bias, local_pivot = map(float, centre.groups())
    y = core_const("CabRoofY") + lift - pivot * scale + local_y - (bias - local_pivot * scale)
    return box("radar antenna support", (x, y, z), tuple(map(float, size.groups())), group="antenna")


def all_boxes(roof, floor):
    import tower_command_room_check as room
    from tower_stairs_check import core_array, supply_spot
    boxes = threshold_boxes()
    bag = core_const("BagH")
    for n, (x, z, sx, sz) in enumerate(zip(core_array("BagX"), core_array("BagZ"),
                                           core_array("BagSX"), core_array("BagSZ"))):
        boxes.append(box("post sandbags %d" % n, (x, roof + bag / 2, z), (sx, bag, sz), group="post%d" % n))
    parts, collider = console_boxes(floor)        # E W1: in the cab, on its floor
    boxes.extend(parts)
    cab, station = room.recipe()
    for p in cab:
        boxes.append(box("cab " + p["name"], p["center"] + [0, floor, 0], p["size"],
                         group="cab-solid" if p["solid"] else "cab"))
    sx, sz = supply_spot()
    boxes.append(box("supply order console", (sx, floor + .4, sz), (.55, .8, .3), group="supply"))
    # CompactRadar's FieldSupport is a physical/rendered box, not part of the
    # imported head. Its base must touch the measured cab roof as well.
    boxes.append(antenna_support())
    return boxes, collider, station


def shipped_triangles():
    import z_f3_field_objects as f3
    world, cols, trees, origin, manifest, radar = f3.survey()
    origin = np.asarray(origin)
    near = [c for c in cols if c["active"] and not c["trigger"] and "tris" in c
            and c["hi"][0] >= origin[0] - 50 * K and c["lo"][0] <= origin[0] + 50 * K
            and c["hi"][2] >= origin[2] - 50 * K and c["lo"][2] <= origin[2] + 50 * K]
    tri = np.concatenate([c["tris"][0][c["tris"][1]] for c in near])
    labels = np.concatenate([[c["path"]] * len(c["tris"][1]) for c in near])
    return (tri - origin) / K, labels, len(near)


def sat(b, tri):
    """Exact OBB/triangle overlap (separating axes), per triangle."""
    v = (tri - b["c"]) @ b["R"]                    # into the box frame
    h = b["h"] - SINK
    e = np.eye(3)
    f = [v[:, 1] - v[:, 0], v[:, 2] - v[:, 1], v[:, 0] - v[:, 2]]
    sep = np.zeros(len(v), bool)
    for i in range(3):                             # box faces
        sep |= (v[:, :, i].min(1) > h[i]) | (v[:, :, i].max(1) < -h[i])
    n = np.cross(f[0], f[1])                       # triangle plane
    d = np.einsum("ij,ij->i", n, v[:, 0])
    r = np.abs(n) @ h
    sep |= np.abs(d) > r
    for i in range(3):                             # edge x edge
        for j in range(3):
            a = np.cross(e[i], f[j])
            p = np.einsum("ij,ikj->ik", a, v)
            r = np.abs(a) @ h
            sep |= (p.min(1) > r) | (p.max(1) < -r)
    return ~sep


def corners(b):
    s = np.array([[x, y, z] for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)], float)
    return b["c"] + (s * b["h"]) @ b["R"].T


def box_triangles(b):
    c = corners(b)
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    return np.array([[c[i], c[j], c[k]] for f in faces for i, j, k in ((f[0], f[1], f[2]), (f[0], f[2], f[3]))])


def overlap(a, b):
    """Two mod boxes cut into each other (touching faces do not count)."""
    ca, cb = corners(a), corners(b)
    if np.any(ca.max(0) <= cb.min(0) + SINK) or np.any(cb.max(0) <= ca.min(0) + SINK):
        return False
    inside = np.all(np.abs((cb - a["c"]) @ a["R"]) < a["h"] - SINK, 1)
    return bool(np.any(sat(a, box_triangles(b))) or np.any(inside))


def surface_under(x, z, top, tri, boxes, skip):
    """Highest shipped surface or box top under (x, z) at or below top + GAP."""
    import east_crossing_check as cc
    best = -1e9
    sel = np.where((tri[:, :, 0].min(1) <= x) & (tri[:, :, 0].max(1) >= x)
                   & (tri[:, :, 2].min(1) <= z) & (tri[:, :, 2].max(1) >= z))[0]
    for t in tri[sel]:
        y = cc.height_on_tris(t[None], np.array([x]), np.array([z]))[0]
        if np.isfinite(y) and y <= top + GAP:
            best = max(best, y)
    for o in boxes:
        if o is skip or np.any(o["R"] != np.eye(3)):
            continue
        lo, hi = o["c"] - o["h"], o["c"] + o["h"]
        if lo[0] - 1e-6 <= x <= hi[0] + 1e-6 and lo[2] - 1e-6 <= z <= hi[2] + 1e-6 and hi[1] <= top + GAP:
            best = max(best, hi[1])
    return best


def cuts(b, tri, floor_tri):
    """Shipped triangles the box cuts into (a ramp's toe may lie in the slab)."""
    lo_t, hi_t = tri.min(1), tri.max(1)
    c = corners(b)
    idx = np.where(np.all(hi_t >= c.min(0), 1) & np.all(lo_t <= c.max(0), 1))[0]
    if not len(idx):
        return idx
    hit = idx[sat(b, tri[idx])]
    return hit[~floor_tri[hit]] if b["allow_floor"] else hit


def floating(flat, tri):
    """A box whose bottom lies on the shipped surface (2 cm, one sample of a
    5 x 5 footprint grid) is grounded; every other box must hang on a
    grounded one through touching boxes (a dial on its radio, the radio on
    its bench top, the top on its legs, the legs on the floor)."""
    grounded = set()
    for n, b in enumerate(flat):
        lo, hi = b["c"] - b["h"], b["c"] + b["h"]
        for x in np.linspace(lo[0] + 1e-3, hi[0] - 1e-3, 5):
            for z in np.linspace(lo[2] + 1e-3, hi[2] - 1e-3, 5):
                if abs(lo[1] - surface_under(x, z, lo[1], tri, [], None)) <= GAP:
                    grounded.add(n)
                    break
            if n in grounded:
                break
    seen, todo = set(grounded), list(grounded)
    while todo:
        a = flat[todo.pop()]
        alo, ahi = a["c"] - a["h"] - GAP, a["c"] + a["h"] + GAP
        for m, b in enumerate(flat):
            if m not in seen and np.all(alo <= b["c"] + b["h"]) and np.all(b["c"] - b["h"] <= ahi):
                seen.add(m)
                todo.append(m)
    return [b["name"] for n, b in enumerate(flat) if n not in seen]


def props():
    tri, labels, count = shipped_triangles()
    failures = []
    # 1. E W1: the shipped cab floor under the console's desk and chair is
    # flat at the floor TowerRadar.Place measures (x 7.4, from 13.5 m down),
    # with the cab's own ceiling/roof (not open sky) above it
    import east_crossing_check as cc
    x0, z0 = core_const("ConsoleX"), core_const("ConsoleZ")
    # the posts and the threshold stand on the main roof as measured on the
    # open roof west of the inner flight (8.7915 m on C1_LOD0)
    roof = surface_under(-6.0, 0.0, core_const("RoofY") + .5, tri, [], None)
    if abs(roof - core_const("RoofY")) > 0.005:
        failures.append("main roof %.4f is not RoofY" % roof)
    floor = surface_under(7.4, 0.0, 13.5, tri, [], None)
    if abs(floor - core_const("CabFloorY")) > 0.01:
        failures.append("cab floor %.3f is not CabFloorY" % floor)
    ys, ceil = [], []
    for x in np.linspace(x0 - .65, x0 + .65, 9):
        for z in np.linspace(z0 - .4, z0 + 1.07, 9):
            sel = np.where((tri[:, :, 0].min(1) <= x) & (tri[:, :, 0].max(1) >= x)
                           & (tri[:, :, 2].min(1) <= z) & (tri[:, :, 2].max(1) >= z))[0]
            h = [cc.height_on_tris(t[None], np.array([x]), np.array([z]))[0] for t in tri[sel]]
            h = [y for y in h if np.isfinite(y)]
            ys.append(max([y for y in h if y < floor + 1] or [np.nan]))
            ceil.append(min([y for y in h if y > floor + 1] or [np.nan]))
    ys, ceil = np.array(ys), np.array(ceil)
    if not (np.all(np.isfinite(ys)) and np.all(np.abs(ys - floor) < 0.005)):
        failures.append("cab floor under the console is not flat: %s..%s" % (np.nanmin(ys), np.nanmax(ys)))
    if not (np.all(np.isfinite(ceil)) and np.all(ceil < core_const("CabRoofY") + .01)):
        failures.append("the console is not under the cab's roof: ceiling %s..%s" % (np.nanmin(ceil), np.nanmax(ceil)))
    if not (core_const("CabMinX") < x0 < core_const("CabMaxX") and core_const("CabMinZ") < z0 < core_const("CabMaxZ")):
        failures.append("the console is not inside the cab rectangle")
    boxes, collider, station = all_boxes(roof, floor)
    # the console sits ON the cab floor: its collider's bottom is the floor
    if abs(collider["c"][1] - collider["h"][1] - floor) > 1e-4:
        failures.append("console collider bottom is not the cab floor")
    # 2. nothing cuts into the shipped tower (all colliders within 50 m)
    floor_tri = np.all(np.abs(tri[:, :, 1] - roof) < 3e-3, 1)       # horizontal main-roof/landing faces
    for b in boxes + [collider]:
        hit = cuts(b, tri, floor_tri)
        if len(hit):
            failures.append("%s cuts into %s" % (b["name"], sorted(set(labels[hit].tolist()))))
    # 3. nothing cuts into anything else the mod builds
    for i, a in enumerate(boxes):
        for b in boxes[i + 1:]:
            if a["group"] == b["group"] and a["group"] in ("threshold", "console", "cab"):
                continue                    # one prop's own parts touch by design
            if a["group"].startswith("cab") and b["group"].startswith("cab"):
                continue                    # Z TC1 visuals sit inside their own collider boxes
            if overlap(a, b):
                failures.append("%s overlaps %s" % (a["name"], b["name"]))
    # the console collider and the cab furniture / supply console stay apart
    for b in boxes:
        if b["group"] != "console" and overlap(collider, b):
            failures.append("radar console overlaps %s" % b["name"])
    # 4. nothing floats
    flat = [b for b in boxes + [collider] if not np.any(b["R"] != np.eye(3))]
    floats = floating(flat, tri)
    failures.extend("%s floats (touches nothing grounded)" % n for n in floats)
    # Negative controls: floating crate, intersecting crate, and both of
    # 6.67's floating attachments above the 15.14 m cab-roof collider.
    crate = box("negative crate", (-6, roof + .35, 0), (.5, .5, .5))
    assert floating(flat + [crate], tri) == floats + ["negative crate"], "floating negative control failed"
    wall = box("negative wall crate", (3.5, roof + .3, 8.85), (.3, .3, .3))
    assert len(cuts(wall, tri, floor_tri)), "intersection negative control failed"
    old = box("6.67 console", (7.4, 15.35 + .8, -2.95), (1.3, 1.6, .7))
    assert floating([old], tri) == ["6.67 console"], "the 6.67 cab-roof console should float"
    # E W1: the console pushed 3 cm into the south window sill must cut it
    sill = box("negative console in the sill", (x0, floor + .8, -3.368 - .03 + .35), (1.3, 1.6, .7))
    assert len(cuts(sill, tri, floor_tri)), "south sill negative control failed"
    old_support = box("6.67 antenna support", (7.9, 15.35 + 1.5, 0), (.24, 3, .24))
    assert floating([old_support], tri) == ["6.67 antenna support"], \
        "the 6.67 antenna support should float"
    # threshold ramps: both ends on the landing/roof (8.79), plateau over the face
    for b in boxes:
        if b["group"] != "threshold":
            continue
        top = b["c"] + b["R"][:, 1] * b["h"][1]
        ends = [top - b["R"][:, 0] * b["h"][0], top + b["R"][:, 0] * b["h"][0]]
        low = min(e[1] for e in ends)
        if abs(b["R"][1, 0]) > 0.1 and abs(low - roof) > 0.005:   # ramps; the plateau clears the face (SAT above)
            failures.append("%s does not start at the roof/landing" % b["name"])
    summary = dict(shipped_colliders_50m=count, shipped_triangles=int(len(tri)), mod_boxes=len(boxes) + 1,
                   console_floor_y=float(np.nanmean(ys)), console=[x0, z0], cab_floor=floor,
                   failures=failures)
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "props.json").write_text(json.dumps(summary, indent=1), encoding="ascii")
    assert not failures, "\n".join(failures)
    print("PASS E W1 props: radar console in the cab on its floor (%.2f m, flat under desk/chair, ceiling "
          "%.2f m above); %d mod boxes vs %d shipped triangles (%d colliders, 50 m) and each other: 0 intersecting, "
          "0 floating; negative controls (floating crate, crate in the parapet, console in the south sill, "
          "6.67 console/antenna support) caught" % (floor, float(np.nanmax(ceil)), len(boxes) + 1, len(tri), count))


def main():
    sys.path.insert(0, str(ROOT / "research"))
    import tower_roof_check
    tower_roof_check.main()      # production routes: stairs, roof, seat, cab, all posts; capsule sweep; props()


if __name__ == "__main__":
    sys.path.insert(0, str(ROOT / "research"))
    main()

"""Power 5 sabotage points: compile the production cores (Revival.SabotageCore.cs,
Revival.SabotageData.cs, PowerNetCore, PowerSitesData, FlakPositionsCore,
C4Core, FuelDepotCore) with tests/sabotage_test.cs on csc 3.5 and run it -
every point has a valid anchor, per-site coverage, the C4 rules (generator in
its house past the armour, tank demolish), frame maths and zero allocation.
Then check each anchor against the geometry it was measured from (bunker
recipe tool, fuel depot table, command room console, radar support, flak
pits) and pin the runtime seams. No game, installation or Unity needed.
Output stays in build/sabotage-check. Last line "N PASS, M FAIL"; exit 0
only without FAIL.
"""
from pathlib import Path
import os
import json
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
TOL = 0.011


def compiler():
    win = os.environ.get("WINDIR", r"C:\Windows")
    for fw in ("Framework64", "Framework"):
        p = Path(win) / "Microsoft.NET" / fw / "v3.5" / "csc.exe"
        if p.exists():
            return p
    return None


def src(name):
    return (ROOT / name).read_text(encoding="utf-8")


def num(text):
    return float(text.rstrip("fF"))


def main():
    fails, passes = [], [0]

    def ok(cond, what):
        if cond:
            passes[0] += 1
        else:
            fails.append(what)
            print("FAIL " + what)

    def near(a, b):
        return abs(a - b) <= TOL

    csc = compiler()
    if csc is None:
        print("SKIP .NET 3.5 csc not found")
        print("0 PASS, 0 FAIL")
        return 0
    build = ROOT / "build" / "sabotage-check"
    build.mkdir(parents=True, exist_ok=True)
    exe = build / "SabotageTest.exe"
    sources = ["Revival.SabotageCore.cs", "Revival.SabotageData.cs", "Revival.PowerNetCore.cs",
               "Revival.PowerSitesData.cs", "Revival.MercAACore.cs", "Revival.FlakPositionsCore.cs",
               "Revival.C4Core.cs", "Revival.FuelDepotCore.cs", "tests/sabotage_test.cs"]
    r = subprocess.run([str(csc), "/nologo", "/warn:0", "/out:" + str(exe)] + [str(ROOT / s) for s in sources],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode:
        print("FAIL compile: " + (r.stdout + r.stderr).strip()[-900:])
        print("0 PASS, 1 FAIL")
        return 1
    run = subprocess.run([str(exe)], capture_output=True, text=True)
    out = run.stdout.strip().splitlines()
    points = {}
    for line in out[:-1]:
        if line.startswith("POINT "):
            f = line.split()
            points[f[1]] = dict(kind=int(f[2]), frame=int(f[3]), index=int(f[4]), ref=f[5], charges=float(f[6]),
                                c=[float(x) for x in f[7:10]], h=[float(x) for x in f[10:13]],
                                a=[float(x) for x in f[13:16]], n=[float(x) for x in f[16:19]],
                                t=[float(x) for x in f[19:22]], floor=float(f[22]))
        else:
            print(line)
    last = out[-1] if out else ""
    ok(run.returncode == 0 and last.endswith(" 0 FAIL"), "core test: " + last)
    try:
        passes[0] += int(last.split(" PASS")[0])
    except ValueError:
        pass

    # ------------------------------------------------ geometry sources
    def box(lo_hi_list):
        lo = [min(b[0][i] for b in lo_hi_list) for i in range(3)]
        hi = [max(b[1][i] for b in lo_hi_list) for i in range(3)]
        return [(lo[i] + hi[i]) / 2 for i in range(3)], [(hi[i] - lo[i]) / 2 for i in range(3)]

    # pb-5 bunker: the pb-2 genset colliders (genset frame, u) placed by the
    # bunker recipe tool at GENSET_AT (model frame) + LIFT (site root = floor).
    gen_tool = src("unity/EastTile/Tools/airfield_generator.py")
    gs = json.loads(src("assets/power_bunker/genset/pb_genset_colliders.json"))
    at = re.search(r"GENSET_AT = \[([-\d., ]+)\]", gen_tool)
    lift = re.search(r"FLOOR_M = ([-\d.]+)", gen_tool)
    ok(at is not None and lift is not None, "bunker tool declares GENSET_AT and FLOOR_M")
    boxes = {}
    if at and lift:
        o = [float(x) / 2.8 for x in at.group(1).split(",")]
        o[1] -= float(lift.group(1))                  # model frame -> floor frame
        for b in gs["boxes"]:
            name = b["name"].split("_", 4)[4]
            c = [o[i] + b["center"][i] / 2.8 for i in range(3)]
            h = [b["size"][i] / 5.6 for i in range(3)]
            boxes[name] = ([c[i] - h[i] for i in range(3)], [c[i] + h[i] for i in range(3)])
    for name in ("frame_engine_alt", "radiator", "panel"):
        ok(name in boxes, "genset colliders declare the " + name)
    g = points.get("main_airfield/generator")
    ok(g is not None, "generator point printed")
    if g and all(n in boxes for n in ("frame_engine_alt", "radiator", "panel")):
        # Frame, engine and alternator; the radiator stands at the west end, in full share reach.
        c, h = box([boxes["frame_engine_alt"]])
        ok(all(near(g["c"][i], c[i]) and near(g["h"][i], h[i]) for i in range(3)), "generator box = genset frame/engine/alternator box")
        ok(boxes["radiator"][1][0] >= g["c"][0] - g["h"][0] - 1.5, "radiator within the full-share 1.5 m")
        lo, hi = boxes["frame_engine_alt"]
        ok(near(g["a"][2], lo[2]) and g["n"] == [0.0, 0.0, -1.0], "generator anchor on the set's south face")
        ok(lo[0] <= g["a"][0] <= hi[0] and lo[1] <= g["a"][1] <= hi[1], "generator anchor inside that face")
        ok(g["a"][0] > boxes["panel"][1][0], "generator anchor east of the control panel (alternator end)")
    sock = re.search(r'dict\(name="Diesel", position=m\(([-\d.]+), ([-\d.]+), ([-\d.]+)\)\)', gen_tool)
    ok(sock is not None and g is not None and abs(float(sock.group(1)) - g["c"][0]) <= g["h"][0]
       and abs(float(sock.group(2)) - g["c"][2]) <= g["h"][2] and float(sock.group(3)) <= g["c"][1] + g["h"][1] + 0.5,
       "bunker Diesel socket where the generator box is")

    fuel = src("Revival.Fuel.cs")
    defs = re.findall(r'Def\("(\w+)", ([\d.]+)f, ([\d.]+)f, (true|false)\)', fuel)
    ok([d[0] for d in defs] == ["V1", "V2", "H1", "H2", "H3"], "fuel depot tank table order V1 V2 H1 H2 H3")
    m = re.search(r"BigR = ([\d.]+)f, BigH = ([\d.]+)f, SmallHalfX = ([\d.]+)f, SmallHalfZ = ([\d.]+)f, SmallH = ([\d.]+)f", fuel)
    ok(m is not None, "fuel depot shell sizes found")
    centre = re.search(r"Centre = new Vector3\(([\d.]+)f, 0f, ([\d.]+)f\)", fuel)
    if m and centre:
        big_r, big_h, sx, sz, sh = [num(x) / 2.8 for x in m.groups()]
        cx, cz = float(centre.group(1)), float(centre.group(2))
        for i, (tid, x, z, big) in enumerate(defs):
            p = points.get("main_airfield/tank_" + tid)
            ok(p is not None and p["index"] == i and p["ref"] == tid, "tank " + tid + " point and index")
            if not p:
                continue
            if big == "true":
                ok(near(p["h"][0], big_r) and near(p["h"][2], big_r) and near(p["h"][1] * 2, big_h), tid + " box = vertical shell")
                ok(near(abs(p["a"][0]), big_r) and p["a"][2] == 0 and p["n"][1] == 0, tid + " anchor on the cylinder side")
            else:
                ok(near(p["h"][0], sx) and near(p["h"][2], sz) and near(p["h"][1] * 2, sh), tid + " box = horizontal shell")
            # The anchor faces the depot yard (or the open side between H2/H3).
            nx, nz = p["n"][0], p["n"][2]
            to_x, to_z = cx - float(x), cz - float(z)
            ok(nx * to_x + nz * to_z > 0 or tid == "H3", tid + " anchor faces the depot yard")
        h2, h3 = points.get("main_airfield/tank_H2"), points.get("main_airfield/tank_H3")
        if h2 and h3:
            ok(h2["n"][2] < 0 and h3["n"][2] > 0, "H2/H3 anchors on their open faces, not in the 1.8 m gap")

    room = src("Revival.TowerCommandRoomCore.cs")
    rx = re.search(r"RadarX = ([-\d.]+)f, RadarZ = ([-\d.]+)f", room)
    cab = re.search(r'B\(p, "console cabinet", x, ([-\d.]+)f, z - ([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, Grey\);', room)
    door = re.search(r'B\(p, "cabinet door", x \+ a \* ([-\d.]+)f, ([-\d.]+)f, z \+ ([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f, Grey\);', room)
    p = points.get("main_airfield/command_post")
    ok(rx is not None and cab is not None and door is not None and p is not None, "command room console geometry found")
    if rx and cab and door and p:
        x, z = float(rx.group(1)), float(rx.group(2))
        y, dz, w, hgt, d = [float(v) for v in cab.groups()]
        ok(near(p["c"][0], x) and near(p["c"][1], y) and near(p["c"][2], z - dz), "command post box = console cabinet centre")
        ok(near(p["h"][0], w / 2) and near(p["h"][1], hgt / 2) and near(p["h"][2], d / 2), "command post box = console cabinet size")
        off, dy, front, dw, dh, dd = [float(v) for v in door.groups()]
        ok(near(p["a"][0], x - off) and abs(p["a"][2] - (z + front + dd / 2)) <= 0.02 and p["n"] == [0.0, 0.0, 1.0],
           "command post anchor on the left cabinet door")
        ok(abs(p["a"][1] - dy) <= dh / 2, "command post anchor within the door's height")
        ok(p["frame"] == 3 and p["floor"] == 0.0, "command post in the cab frame, on the cab floor")

    objs = src("Revival.AirfieldObjects.cs")
    roof = src("Revival.TowerRoofCore.cs")
    cab_roof = re.search(r"CabRoofY = ([\d.]+)f", roof)
    p = points.get("main_airfield/radar_mast")
    ok("new Vector3(0.24f/s, 3f/s, 0.24f/s)" in objs and "TowerRadar.TowerPoint(new Vector3(7.9f," in objs,
       "radar field support 0.24 m x 3 m at tower x 7.9")
    ok(cab_roof is not None and p is not None, "radar mast point and cab roof found")
    if cab_roof and p:
        top = float(cab_roof.group(1))
        ok(near(p["c"][0], 7.9) and near(p["c"][2], 0.0) and near(p["h"][0], 0.12) and near(p["h"][2], 0.12),
           "radar mast box = the 0.24 m support")
        ok(near(p["c"][1] - p["h"][1], top) and near(p["c"][1] + p["h"][1], top + 3.0), "radar mast from the cab roof up 3 m")
        ok(near(p["floor"], top) and p["frame"] == 4, "radar mast reached from the cab roof, tower frame")

    flak = src("Revival.FlakPositionsCore.cs")
    idx = re.search(r"GunIndex = \{ ([\d, ]+) \}", flak)
    names = re.search(r'Name = \{ ([^}]+) \}', flak)
    ok(idx is not None and names is not None, "flak pit table found")
    if idx and names:
        gi = [int(v) for v in idx.group(1).split(",")]
        nm = [v.strip().strip('"') for v in names.group(1).split(",")]
        for gun, name in zip(gi, nm):
            p = points.get("main_airfield/gun_" + name)
            ok(p is not None and p["index"] == gun and p["frame"] == 2, "gun " + name + " point on gun " + str(gun))

    # ------------------------------------------------------- seams
    c4 = src("Revival.C4.cs")
    ok("Sabotage.C4Chain(_boomAt, count," in c4, "C4: every chain goes to the sabotage points")
    ok("AirDefenceDamage.ReportBlast(at, aaRadius, aaPeak, !_inHouse[k]);" in c4, "C4: a charge in the bunker costs no armour")
    ok(c4.index("Sabotage.C4Chain(") < c4.index("ReportBlast(at, aaRadius, aaPeak, !_inHouse[k])"), "C4: sabotage before the blast report")
    sab = src("Revival.Sabotage.cs")
    for needle, what in (("PowerNet.InsideHouse(at[k])", "inside-the-house test per charge"),
                         ("p.Kind == SabotageCore.Generator && !inHouse[k]", "only charges inside reach the generator"),
                         ("C4Core.ChargeShare(SabotageCore.BoxDistance(", "shares by the C4 rule against the target box"),
                         ("PowerNet.Demolition(p.Site, shares, p.Charges)", "generator damaged at once"),
                         ("FuelDepot.Demolish(p.Index)", "tank destroyed"),
                         ("AirDefenceDamage.Wreck(AirDefenceDamage.Console)", "command post console wrecked"),
                         ("AirDefenceDamage.Wreck(AirDefenceDamage.Radar)", "radar mast wrecked"),
                         ("AirDefenceDamage.Wreck(p.Index)", "AA gun wrecked"),
                         ("if (!Show || !Admin.IsOpen", "overlay only for admins")):
        ok(needle in sab, "runtime: " + what)
    net = src("Revival.PowerNet.cs")
    ok("PowerNetCore.Demolition(ref States[s], charges, need, Time.time)" in net and "Changed(s);" in net, "PowerNet: demolition synced")
    ok("Sabotage.PostDown(Sites[i].Id)" in net and "PostConsumer(consumer)" in net, "PowerNet: wrecked post turns screens/terminal off")
    ok("FuelDepotCore.Demolish(ref points)" in src("Revival.Fuel.cs"), "FuelDepot: demolish through the core")
    add = src("Revival.AirDefenceDamage.cs")
    ok("internal static bool Wreck(int id)" in add and "if (power) PowerNet.Strike(at, PowerNetCore.HitHeavy);" in add,
       "AirDefenceDamage: Wreck and the armour bypass")
    ok("PowerNet.CommandPostDown" in src("Revival.TowerDelivery.cs"), "terminal names the wrecked command post")
    ok("Sabotage.Show = UiKit.Toggle(" in src("Revival.Admin.cs"), "admin panel toggle")
    ok("Sabotage.Draw();" in src("RevivalPlugin.cs") and '"Sabotage.Draw"' in src("RevivalFrameProfiler.cs"), "overlay drawn in its F6 slot")
    for name in ("Revival.SabotageCore.cs", "Revival.SabotageData.cs", "Revival.Sabotage.cs", "tests/sabotage_test.cs"):
        ok(all(ord(ch) < 128 for ch in src(name)), name + " is ASCII")
    print("%d PASS, %d FAIL" % (passes[0], len(fails)))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())

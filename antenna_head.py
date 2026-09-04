"""Builds the deployed head of the mast antenna (item 2055).

The mast BODY is still a runtime telescope of grey cylinder segments in
RevivalDroneGear.cs: it has to grow during deploy and the emergence clip hides
its buried lower half, neither of which a single static mesh can do. What was
missing was a deliberate ANTENNA HEAD at the tip - the piece that makes the mast
read as a real recon/comms antenna instead of a bare pole. That head is this
mesh: a compact radio box, a central whip, and a short yagi element stack.

It is built in REAL METRES (no fit_box): the C# side parents it under the mast
root at world scale ~1 and sits it at the extended tip, so the geometry here is
exactly what the player sees. Y is up, the base sits at y=0 (the clamp onto the
mast tip), the whip tip is near y=0.62.

Conventions follow the other generators (ndmesh.Mesh, the rod() helper from
jammer_mesh.py, the four shared UV regions). It does NOT modify ndmesh.py or any
shared library. Preview and bounds check:

    python antenna_head.py
    python mesh_preview.py antenna_head
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "antenna_head.ndmesh")


def rod(length, radius, region, seg=12):
    """A tube along local Y, centred on the origin - rotate it to lie flat."""
    part = Mesh("rod")
    part.tube(0.0, 0.0, -length / 2.0, length / 2.0, radius, region, seg=seg)
    return part


def crossbar(head, y, half_len, radius, region):
    """A horizontal dipole/director element along X at height y, with two end
    finials so the elements read as tuned rods, not cut tubes."""
    bar = rod(half_len * 2.0, radius, region, seg=10)
    head.merge(bar, rot_deg=(0.0, 0.0, 90.0), offset=(0.0, y, 0.0))
    for x in (-half_len, half_len):
        head.cone(x, 0.0, y - radius, y + radius * 3.0,
                  radius * 1.6, radius * 0.4, region, seg=8)


def build():
    head = Mesh("NDR recon antenna head")

    # Clamp collar that grips the mast tip, and the sealed radio box above it.
    head.tube(0.0, 0.0, -0.03, 0.05, 0.032, "stock", seg=16)
    head.cbox(0.0, 0.115, 0.0, 0.150, 0.120, 0.104, "receiver", c=0.014)
    # A ribbed heat-sink face and a connector nub, so the box is not a plain
    # cube from every angle.
    for x in (-0.045, 0.0, 0.045):
        head.cbox(x, 0.115, 0.058, 0.024, 0.086, 0.014, "shroud", c=0.006)
    head.tube(0.0, -0.052, 0.170, 0.205, 0.010, "detail", seg=10)

    # Central whip: a thin tapered mast continuing up out of the box.
    head.tube(0.0, 0.0, 0.175, 0.560, 0.013, "detail", seg=12)
    head.cone(0.0, 0.0, 0.560, 0.625, 0.013, 0.004, "detail", seg=12)

    # Yagi element stack up the whip: a driven dipole low, two directors above,
    # each shorter than the last - the shape that says "aimed antenna".
    crossbar(head, 0.250, 0.230, 0.011, "stock")
    crossbar(head, 0.360, 0.175, 0.010, "detail")
    crossbar(head, 0.455, 0.120, 0.009, "detail")

    return head


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)

"""Erzeugt das Modell zu Item 1163: eine kleine bewaffnete FPV-Drohne."""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ndmesh import Mesh

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HIER = os.path.dirname(os.path.abspath(__file__))

ASSETS = os.path.join(HIER, "assets")
OUT = os.path.join(ASSETS, "drone.ndmesh")
REF_SIZE = (0.364, 0.150, 0.370)
REF_CENTER = (-0.022, -0.001, -0.206)


def rotor(blade_angle):
    """Baut einen Motor, seinen Schutzring und zwei Rotorblaetter."""
    part = Mesh("rotor")
    part.tube(0.0, 0.0, 0.035, 0.135, 0.055, "detail", seg=24)
    part.cone(0.0, 0.0, 0.135, 0.165, 0.055, 0.035,
              "detail", seg=24)

    ring_radius = 0.155
    ring_segments = 20
    segment_length = 2.0 * math.pi * ring_radius / ring_segments * 1.08
    for i in range(ring_segments):
        angle = 360.0 * i / ring_segments
        segment = Mesh("guard ring segment")
        segment.cbox(0.0, 0.125, ring_radius,
                     segment_length, 0.024, 0.020, "detail", c=0.004)
        part.merge(segment, rot_deg=(0.0, angle, 0.0))

    # Two separate halves keep the motor hub visible between the blades.
    for angle in (blade_angle, blade_angle + 180.0):
        blade = Mesh("rotor blade")
        blade.cbox(0.0, 0.174, 0.090, 0.050, 0.014, 0.145,
                   "stock", c=0.005)
        part.merge(blade, rot_deg=(0.0, angle, 0.0))
    return part


def build():
    drone = Mesh("FPV Attack Drone")

    # The airframe lies in X-Z with Y as its shallow vertical axis.
    drone.cbox(0.0, 0.0, 0.0, 0.340, 0.145, 0.440,
               "receiver", c=0.028)
    drone.cbox(0.0, 0.085, -0.015, 0.255, 0.045, 0.305,
               "receiver", c=0.018)

    arm = Mesh("arm")
    arm.cbox(0.0, 0.020, 0.330, 0.080, 0.060, 0.430,
             "stock", c=0.012)

    arm_angles = (45.0, 135.0, 225.0, 315.0)
    motor_distance = 0.525
    for i, angle in enumerate(arm_angles):
        drone.merge(arm, rot_deg=(0.0, angle, 0.0))
        a = math.radians(angle)
        x = math.sin(a) * motor_distance
        z = math.cos(a) * motor_distance
        drone.merge(rotor(20.0 + i * 17.0),
                    rot_deg=(0.0, angle, 0.0), offset=(x, 0.0, z))

    # The forward camera points along +Z and breaks the front/back symmetry.
    camera = Mesh("FPV camera")
    camera.cone(0.0, 0.0, 0.0, 0.125, 0.090, 0.062,
                "shroud", seg=24)
    camera.tube(0.0, 0.0, 0.118, 0.168, 0.050,
                "shroud", seg=24)
    drone.merge(camera, rot_deg=(90.0, 0.0, 0.0),
                offset=(0.0, 0.000, 0.205))

    # A tapered charge below the center makes the weapon role readable.
    drone.cone(0.0, -0.025, -0.255, -0.070, 0.045, 0.085,
               "shroud", seg=24)
    drone.tube(0.0, -0.025, -0.078, -0.045, 0.090,
               "detail", seg=24)

    drone.fit_box(REF_SIZE, REF_CENTER)
    return drone


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    mesh = build()
    mesh.write(OUT)
    mesh.report(OUT)
    print("  Auf magaz_l-Groesse und Mittelpunkt eingepasst")

"""Level-turn model of Revival.AirBoundary.cs's An-2 auto-turn (task B6).

Run: python research/air_boundary_sim.py

An An-2 crosses a straight map edge at a speed and an angle; past the edge
AirBoundary.Guide runs its countdown (WarnSeconds, shortened when depth + turn
radius + one second of roll-in would pass 75 % of BufferU) and then asks for a
bank back towards the map, which the plugin's flight assist flies (roll at
2.5 x the bank error, capped by the roll rate; a coordinated level turn,
omega = g tan(bank) / v). The pilot's A/D is held throughout (-1, 0, +1): he
keeps 15 degrees of it plus (1 - strength) of his own bank. Prints the
deepest point past the edge; everything should stay near or inside the
buffer. No game is needed; if Guide changes, change it here too.
"""
import math

G = 9.81
K = 2.8            # world units per metre (PlayerAn2.K)
BUFFER = 2000.0    # [AirBoundary] BufferU
WARN = 5.0         # [AirBoundary] WarnSeconds
TURN_BANK = 55.0   # AirBoundary.TurnBank
MAX_BANK = 40.0    # [PlayerAn2] MaxBank
ROLL_RATE = 75.0   # [PlayerAn2] RollRate
DT = 1.0 / 60.0


def fly(kmh, angle, pilot):
    """angle: degrees off the edge line (90 = straight out). Returns the
    deepest point past the edge (u) and when the auto-turn began (s)."""
    v = kmh / 3.6
    x = 0.0
    hd = angle
    bank = 0.0
    t = 0.0
    since = None
    turning = False
    sign = 0
    deepest = 0.0
    began = None
    while t < 120.0:
        out = v * math.sin(math.radians(hd)) * K
        d = max(0.0, x)
        want = pilot * MAX_BANK
        if d > 0.0:
            if since is None:
                since = t
            err = ((-90.0 - hd + 180.0) % 360.0) - 180.0
            if abs(err) > 150.0 and sign != 0 and (1 if err >= 0 else -1) != sign:
                err += sign * 360.0
            else:
                sign = 1 if err >= 0 else -1
            inbound = out < 0.0
            if inbound and not turning:
                since += DT
            left = WARN - (t - since)
            radius = v * v / (G * math.tan(math.radians(TURN_BANK))) * K
            room = 0.75 * BUFFER - radius - d - max(0.0, out)
            count = max(0.0, min(left, room / out if out > 1.0 else 999.0))
            if not turning and count <= 0.0 and not inbound:
                turning = True
                began = t
            if turning and abs(err) < 20.0 and out <= 0.0:
                turning = False
                since = t
            k = min(1.0, 0.6 + 0.4 * d / BUFFER) if (turning or d > BUFFER) else 0.0
            if k > 0.0:
                back = max(-TURN_BANK, min(TURN_BANK, err * 1.5))
                want = pilot * MAX_BANK * (1.0 - k) + back * k + pilot * 15.0 * k
                want = max(-TURN_BANK, min(TURN_BANK, want))
        else:
            since = None
            turning = False
            sign = 0
            if t > 1.0 and out < 0.0:
                break
        step = ROLL_RATE * DT
        bank += max(-step, min(step, (want - bank) * 2.5 * DT))
        hd += math.degrees(G * math.tan(math.radians(bank)) / v) * DT
        x += out * DT
        deepest = max(deepest, x)
        t += DT
    return deepest, began


def main():
    worst = 0.0
    for kmh in (120, 180, 250):
        for angle in (90, 45, 20):
            for pilot in (0.0, 1.0, -1.0):
                deep, began = fly(kmh, angle, pilot)
                worst = max(worst, deep)
                print('%3d km/h, %2d deg off the edge, A/D %+.0f: deepest %5.0f u, auto-turn %s'
                      % (kmh, angle, pilot, deep, '%.1f s' % began if began is not None else 'not needed'))
    print('worst %.0f u of a %.0f u buffer' % (worst, BUFFER))


if __name__ == '__main__':
    main()

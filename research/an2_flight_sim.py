"""Pitch-plane model of Revival.PlayerAn2.cs's flight equations.

Run: python research/an2_flight_sim.py

It mirrors the plugin's arithmetic (lift, drag, thrust, the weathervane
pitch stability, the tail-wheel ground attitude, rolling friction) with the
default config values, and flies the scripted manoeuvres the in-game
checklist asks for: takeoff on concrete and on grass, climb, level top speed,
power-off glide, a stall entry at idle and its recovery. The numbers it prints
are the tuning table of docs/ai/tasks/an2-flight.md. No game is needed; if the
equations in the plugin change, change them here too.
"""
import math

G = 9.81
CFG = {
    'StallSpeed': 60.0, 'CriticalAngle': 15.0, 'Thrust': 4.0, 'Power': 90.0,
    'TopSpeed': 250.0, 'PitchRate': 40.0, 'RollFriction': 0.04,
    'GrassFriction': 0.08, 'Parked': 8.21, 'CrashSinkRate': 4.0,
}
INDUCED = 0.3
DT = 1.0 / 60.0
# World units per metre (PlayerAn2.K): the map and the runway are measured in
# units, so the takeoff roll is printed in both.
K = 2.8
# B6: with the flight assist, W/S released and the throttle over 70 % the tail
# comes down (rotation) at this multiple of the stall speed (PlayerAn2.RotateAt).
ROTATE = 1.15


def vs():
    return CFG['StallSpeed'] / 3.6


def lift_shape(aoa, crit):
    zero = -2.0
    if aoa <= crit:
        return max(-0.6, min(1.0, (aoa - zero) / (crit - zero)))
    return max(0.45, 1.0 - (aoa - crit) * 0.07)


def thrust_at(v):
    return min(CFG['Thrust'], CFG['Power'] / max(1.0, v))


def cd0():
    vt = CFG['TopSpeed'] / 3.6
    c = (vs() / vt) ** 2
    return max(1e-5, (thrust_at(vt) - INDUCED * G * c) / (vt * vt))


class Plane(object):
    def __init__(self):
        self.x = 0.0
        self.h = 0.0
        self.vx = 0.0
        self.vy = 0.0
        self.theta = CFG['Parked']      # nose up, degrees
        self.ground = True
        self.max_aoa = 0.0
        self.stalled_s = 0.0

    def step(self, throttle, pitch_in, paved=True, assist=False):
        v = math.hypot(self.vx, self.vy)
        gamma = math.degrees(math.atan2(self.vy, self.vx)) if v > 0.5 else 0.0
        aoa = self.theta - gamma if self.vx > 1.0 else 0.0
        crit = CFG['CriticalAngle']
        thr = thrust_at(max(0.0, v * math.cos(math.radians(aoa)))) * throttle
        wash = thr / CFG['Thrust']
        q = min(1.0, v / 18.0)
        stalled = (not self.ground) and aoa > crit and v > 3.0
        auth = min(1.0, q + wash * 0.35) * (0.6 if stalled else 1.0)
        if self.ground:
            able = min(1.0, max(0.0, (v - 8.0) / 14.0))
            lifted = able * min(1.0, max(0.0, 0.45 - 0.55 * pitch_in))
            if assist and pitch_in > -0.1 and throttle > 0.7 and v > ROTATE * vs():
                lifted = 0.0
            want = CFG['Parked'] * (1.0 - lifted)
            step = 10.0 * DT
            self.theta += max(-step, min(step, want - self.theta))
            self.theta = max(0.0, min(CFG['Parked'], self.theta))
        else:
            aero = min(1.0, v / 20.0)
            rate = pitch_in * CFG['PitchRate'] * auth - 3.0 * (aoa - 2.0) * aero
            if stalled:
                rate -= 12.0
                self.stalled_s += DT
            self.theta += rate * DT
        self.max_aoa = max(self.max_aoa, aoa if not self.ground else 0.0)
        th = math.radians(self.theta)
        ax = thr * math.cos(th)
        ay = thr * math.sin(th) - G
        lift = 0.0
        if v > 0.5:
            c = lift_shape(aoa, crit)
            lift = max(-2 * G, min(3.5 * G, G * (v / vs()) ** 2 * c))
            vxh, vyh = self.vx / v, self.vy / v
            ax += -vyh * lift
            ay += vxh * lift
            drag = cd0() * v * v + INDUCED * abs(lift) * abs(c)
            ax -= vxh * drag
            ay -= vyh * drag
        self.vx += ax * DT
        self.vy += ay * DT
        self.x += self.vx * DT
        self.h += self.vy * DT
        arrival = self.vy
        if self.h <= 0.0:
            self.h = 0.0
            if self.vy < 0.0:
                self.vy = 0.0
            self.ground = True
            load = min(1.0, max(0.0, 1.0 - lift / G))
            mu = CFG['RollFriction'] if paved else CFG['GrassFriction']
            dec = mu * G * load * DT
            self.vx = max(0.0, self.vx - dec) if self.vx > 0 else self.vx
        else:
            self.ground = False
        return arrival


def takeoff(paved, stick, assist=False):
    """Full throttle from standstill until the wheels are 0.3 m up. With
    assist, the stick is the plugin's WASD assist: on the ground the rotation
    rule above; in the air a released W/S climbs out, flown here as a gentle
    back stick (the plugin's Assist asks for a climb angle instead)."""
    p = Plane()
    t = 0.0
    while t < 60.0:
        s = stick if (p.ground or not assist or stick != 0.0) else 0.5
        p.step(1.0, s, paved, assist)
        t += DT
        if not p.ground and p.h > 0.3:
            return p.x, p.vx * 3.6, t
    return None


def drag_level(v):
    """Drag of level flight at v: the lift carries the weight."""
    c = (vs() / v) ** 2
    return cd0() * v * v + INDUCED * G * c


def level_speed(throttle):
    """Fastest steady level speed at this throttle: thrust = drag."""
    best = None
    v = vs()
    while v < 120.0:
        if thrust_at(v) * throttle >= drag_level(v):
            best = v
        v += 0.05
    return None if best is None else best * 3.6


def climb():
    """Best steady climb at full throttle: max of (T - D) v / g."""
    best, at = 0.0, 0.0
    v = vs() * 1.05
    while v < 70.0:
        rate = (thrust_at(v) - drag_level(v)) * v / G
        if rate > best:
            best, at = rate, v
        v += 0.1
    return best, at * 3.6


def glide():
    """Best glide ratio, engine off: max of lift over drag."""
    best, at = 0.0, 0.0
    v = vs() * 1.05
    while v < 70.0:
        ld = G / drag_level(v)
        if ld > best:
            best, at = ld, v
        v += 0.1
    return best, at / best, at * 3.6


def stall():
    """Idle, the pilot holds the height with the stick while the speed
    bleeds off - the way a stall is flown - until the wing breaks. Then
    full throttle and the stick released: the height lost to recovery."""
    p = Plane()
    p.ground = False
    p.h = 800.0
    p.vx = 140 / 3.6
    p.theta = 0.0
    h0 = p.h
    t = 0.0
    stick = 0.0
    while p.stalled_s <= 0.0 and t < 120.0:
        stick = max(-1.0, min(1.0, stick + (0.4 * -p.vy + 0.1 * (h0 - p.h)) * DT))
        p.step(0.0, stick)
        t += DT
    stall_v = math.hypot(p.vx, p.vy) * 3.6
    h_stall = p.h
    low = p.h
    for _ in range(int(20 / DT)):
        p.step(1.0, 0.0)
        low = min(low, p.h)
    return stall_v, h_stall - low, t


def landing(sink):
    return 'crash' if sink > CFG['CrashSinkRate'] else ('hard' if sink > 0.6 * CFG['CrashSinkRate'] else 'ok')


def main():
    print('Cd0 %.3e (top speed %.0f km/h at full throttle)' % (cd0(), CFG['TopSpeed']))
    for paved in (True, False):
        for stick, what, assist in ((1.0, 'back stick', False), (0.0, 'neutral', False),
                                    (0.0, 'WASD free', True)):
            r = takeoff(paved, stick, assist)
            surface = 'concrete' if paved else 'grass   '
            if r:
                print('takeoff %s %-10s: roll %4.0f m (%4.0f u), liftoff %3.0f km/h after %4.1f s'
                      % (surface, what, r[0], r[0] * K, r[1], r[2]))
            else:
                print('takeoff %s %-10s: no liftoff in 60 s' % (surface, what))
    vz, v = climb()
    print('best climb, full throttle: %.1f m/s at %.0f km/h' % (vz, v))
    for thr in (1.0, 0.7, 0.5):
        v = level_speed(thr)
        print('level at throttle %3.0f%%: %s' % (thr * 100, '%.0f km/h' % v if v else 'cannot hold level'))
    ld, sink, v = glide()
    print('best glide, engine off: L/D %.1f, sink %.1f m/s at %.0f km/h' % (ld, sink, v))
    sv, lost, t = stall()
    print('stall at idle, height held: breaks at %.0f km/h after %.0f s, '
          'recovery (full throttle, stick free) loses %.0f m' % (sv, t, lost))
    for s in (1.5, 2.5, 3.0, 4.5):
        print('touchdown sink %.1f m/s: %s' % (s, landing(s)))


if __name__ == '__main__':
    main()

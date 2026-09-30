// Allocation-free fall curve for the An-2 CCIP. All distances are metres.
// Quadratic drag has no elementary 3D solution: freeze its coefficient at
// each panel's predicted midpoint and integrate linear drag + gravity
// analytically. No physics calls, terrain queries or tiny Euler steps here.
using System;

namespace NextDayRevival
{
    internal static class An2BombCurve
    {
        const double Gravity = 9.81;
        const double Panel = 0.25;

        static void Factors(double c, double dt, out double decay,
                            out double travel, out double gravityTravel)
        {
            double u = c * dt;
            if (u < 0.001)
            {
                // Stable limits at zero drag; avoid subtracting near equals.
                decay = 1.0 - u + u * u * 0.5 - u * u * u / 6.0;
                travel = dt * (1.0 - u * 0.5 + u * u / 6.0 - u * u * u / 24.0);
                gravityTravel = dt * dt * (0.5 - u / 6.0 + u * u / 24.0 - u * u * u / 120.0);
            }
            else
            {
                decay = Math.Exp(-u);
                travel = (1.0 - decay) / c;
                gravityTravel = (dt - travel) / c;
            }
        }

        // Return travel to the horizontal ground plane, including climb/sink.
        // Limit matches the live projectile's 60 s lifetime.
        internal static bool Fall(double height, double vx, double vy, double vz,
                                  double drag, out double x, out double z, out double seconds)
        {
            x = z = seconds = 0.0;
            if (height <= 0.0 && vy <= 0.0) return height == 0.0;
            drag = Math.Max(0.0, drag);
            double y = height;
            for (int i = 0; i < 240; i++)
            {
                double e, q, r;
                double c = drag * Math.Sqrt(vx * vx + vy * vy + vz * vz);
                Factors(c, Panel * 0.5, out e, out q, out r);
                double mx = vx * e, my = vy * e - Gravity * q, mz = vz * e;
                c = drag * Math.Sqrt(mx * mx + my * my + mz * mz);
                Factors(c, Panel, out e, out q, out r);
                double dy = vy * q - Gravity * r;
                // A climbing bomb can hit uphill terrain above release height.
                // In that case start below the candidate plane and use the
                // descending crossing, after clearing the plane.
                if (y > 0.0 && y + dy <= 0.0)
                {
                    // Exact root in the final analytic panel (12 bisections).
                    double lo = 0.0, hi = Panel;
                    for (int j = 0; j < 12; j++)
                    {
                        double t = (lo + hi) * 0.5;
                        Factors(c, t, out e, out q, out r);
                        if (y + vy * q - Gravity * r > 0.0) lo = t;
                        else hi = t;
                    }
                    double last = (lo + hi) * 0.5;
                    Factors(c, last, out e, out q, out r);
                    x += vx * q;
                    z += vz * q;
                    seconds += last;
                    return true;
                }
                x += vx * q;
                z += vz * q;
                y += dy;
                vx *= e;
                vy = vy * e - Gravity * q;
                vz *= e;
                seconds += Panel;
                if (y <= 0.0 && vy <= 0.0) return false;
            }
            return false;
        }
    }
}

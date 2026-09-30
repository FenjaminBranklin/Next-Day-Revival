// Compiles with the production curve under the same .NET 3.5 / C# 3 compiler.
using System;
using System.Diagnostics;
using NextDayRevival;

class BombsightCheck
{
    const float G = 9.81f, K = 2.8f;
    internal struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public float magnitude { get { return (float)Math.Sqrt(x * x + y * y + z * z); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x * b, a.y * b, a.z * b); }
    }

    /*ADVANCE*/

    static object CfgDrag;
    static float _testDrag, _testSlope;
    static float F(object unused, float fallback) { return _testDrag; }
    static bool Ground(Vector3 point, out float y)
    {
        y = _testSlope * (point.z - 5000f);
        return true;
    }
    static class Mathf
    {
        public static float Max(float a, float b) { return Math.Max(a, b); }
        public static float Abs(float a) { return Math.Abs(a); }
    }

    /*PREDICT*/

    static void Simulate(double height, double vx, double vy, double vz, double drag,
                         double slope, out double x, out double z, out double seconds)
    {
        const float dt = 0.02f;
        // Nonzero world coordinates also exercise live float precision and K.
        Vector3 pos = new Vector3(7000f, (float)height * K, 5000f);
        Vector3 vel = new Vector3((float)vx, (float)vy, (float)vz);
        x = z = seconds = 0;
        for (int i = 0; i < 3000; i++)
        {
            Vector3 was = pos;
            Advance(ref pos, ref vel, dt, (float)drag);
            seconds += dt;
            float floor = (float)slope * (pos.z - 5000f);
            if (pos.y <= floor)
            {
                // Exactly the live Fly ground crossing against the endpoint floor.
                double frac = (was.y - floor) / (was.y - pos.y);
                frac = Math.Max(0, Math.Min(1, frac));
                x = (was.x + (pos.x - was.x) * frac - 7000f) / K;
                z = (was.z + (pos.z - was.z) * frac - 5000f) / K;
                seconds -= dt * (1 - frac);
                return;
            }
        }
        throw new Exception("Live bomb exceeded 60 s lifetime");
    }

    static int Main()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        double[] heights = { 10, 20, 50, 100, 300, 550, 1000, 2000 };
        double[] speeds = { 0, 15, 35, 55, 70 };
        double[] sink = { -15, -5, 0, 10 };
        double[] drag = { 0, 0.0006, 0.0015, 0.003 };
        double[] slopes = { -0.1, 0, 0.1 };
        double worst = 0, timeError = 0, defaultWorst = 0;
        int cases = 0, hidden = 0;
        string worstCase = "";
        foreach (double h in heights)
        foreach (double speed in speeds)
        foreach (double vy in sink)
        foreach (double d in drag)
        foreach (double slope in slopes)
        {
            _testDrag = (float)d; _testSlope = (float)slope;
            Vector3 impact;
            float seconds;
            bool valid = Predict(new Vector3(7000f, (float)h * K, 5000f),
                new Vector3((float)speed * 0.3f, (float)vy, (float)speed), out impact, out seconds);
            if (!valid)
            {
                Console.WriteLine("Hidden: height={0} speed={1} vertical={2} drag={3} slope={4}", h, speed, vy, d, slope);
                hidden++; continue;
            }
            double x = (impact.x - 7000f) / K, z = (impact.z - 5000f) / K, time = seconds;
            double sx, sz, st;
            Simulate(h, speed * 0.3, vy, speed, d, slope, out sx, out sz, out st);
            double error = Math.Sqrt((x - sx) * (x - sx) + (z - sz) * (z - sz));
            if (error > worst)
            {
                worst = error;
                worstCase = string.Format("height={0}m speed={1}m/s vertical={2}m/s drag={3} slope={4}", h, speed, vy, d, slope);
            }
            if (d == 0.0006) defaultWorst = Math.Max(defaultWorst, error);
            timeError = Math.Max(timeError, Math.Abs(time - st));
            cases++;
        }
        Console.WriteLine("Trajectory: {0} solutions, {1} steep/low cases hidden (bounded refinement)", cases, hidden);
        Console.WriteLine("Max miss: {0:F3} m; default drag: {1:F3} m; time error: {2:F4} s", worst, defaultWorst, timeError);
        Console.WriteLine("Worst case: " + worstCase);
        if (worst > 3.0 || cases != 1920 || hidden != 0) return 1;

        double px, pz, pt;
        An2BombCurve.Fall(550, 12, -5, 40, 0.0006, out px, out pz, out pt);
        // Zero drag has an independent elementary reference, including climb.
        double t = (10 + Math.Sqrt(100 + 2 * 9.81 * 550)) / 9.81;
        An2BombCurve.Fall(550, 12, 10, 40, 0, out px, out pz, out pt);
        if (Math.Abs(pt - t) > 0.0001 || Math.Abs(pz - 40 * t) > 0.003) return 2;
        if (!An2BombCurve.Fall(0, 0, 0, 0, 0, out px, out pz, out pt) || pt != 0) return 3;
        if (An2BombCurve.Fall(100000, 0, 0, 0, 0.003, out px, out pz, out pt)) return 4;
        t = (10 + Math.Sqrt(100 - 2 * 9.81 * 2)) / 9.81;
        if (!An2BombCurve.Fall(-2, 0, 10, 40, 0, out px, out pz, out pt)
            || Math.Abs(pt - t) > 0.0001) return 5;
        if (An2BombCurve.Fall(-10, 0, 10, 40, 0, out px, out pz, out pt)) return 6;
        Console.WriteLine("Zero-drag closed-form, zero-height, uphill climb/apex and 60 s lifetime: PASS");

        // Warmed pure math cost. Report worst 100-solve batch, avoiding timer noise.
        Stopwatch watch = new Stopwatch();
        double peak = 0, total = 0;
        for (int batch = 0; batch < 100; batch++)
        {
            watch.Reset(); watch.Start();
            for (int i = 0; i < 100; i++)
                An2BombCurve.Fall(2000, 21, -15, 70, 0.003, out px, out pz, out pt);
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds / 100;
            total += ms; peak = Math.Max(peak, ms);
        }
        Console.WriteLine("Curve cost (2000 m, 10000 calls): avg {0:F4} ms, max batch avg {1:F4} ms", total / 100, peak);
        Console.WriteLine("PASS: compiled production curve vs live 0.02 s gravity/quadratic-drag trajectory <= 3 m");
        return 0;
    }
}

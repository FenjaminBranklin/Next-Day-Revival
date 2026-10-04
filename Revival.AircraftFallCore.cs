using System;

namespace NextDayRevival
{
    // Metres and seconds. Destruction removes lift immediately; total airspeed
    // never limits the vertical acceleration. Closed-form travel is frame-rate
    // independent and can be evaluated at the same Photon clock on every peer.
    internal static class AircraftFallCore
    {
        // A destroyed airframe has no lift. This visible dive reaches 64 m/s
        // in four seconds and hits from 550 m within ten seconds.
        internal const double Gravity = 16.0;
        const double Drag = 0.5;

        internal static double HorizontalTravel(double age)
        {
            return (1.0 - Math.Exp(-Drag * Math.Max(0.0, age))) / Drag;
        }

        internal static double VerticalTravel(double velocity, double age, double terminal)
        {
            age = Math.Max(0.0, age);
            velocity = Math.Max(-terminal, velocity);
            double accelerating = Math.Min(age, Math.Max(0.0, (velocity + terminal) / Gravity));
            return velocity * accelerating - 0.5 * Gravity * accelerating * accelerating
                - terminal * (age - accelerating);
        }

        internal static double VerticalSpeed(double velocity, double age, double terminal)
        {
            return Math.Max(-terminal, velocity - Gravity * Math.Max(0.0, age));
        }

        internal static double Pitch(double initial, double age)
        {
            double fall = 1.0 - Math.Exp(-Math.Max(0.0, age));
            return initial + (72.0 - initial + 5.0 * Math.Sin(age * 1.8)) * fall;
        }

        internal static double Bank(double initial, double side, double age)
        {
            age = Math.Max(0.0, age);
            double ramp = Math.Min(age, 4.0);
            return initial + side * (12.0 * ramp + 4.0 * ramp * ramp + 44.0 * (age - ramp));
        }
    }
}

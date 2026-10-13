// Toxic Swamp landing-site geometry. Engine-free, C# 3.0.
using System;

namespace NextDayRevival
{
    internal sealed class SwampPart
    {
        internal readonly string Name, Finish;
        internal readonly bool Round, Solid;
        internal readonly float X, Y, Z, Width, Height, Depth;
        internal SwampPart(string name, string finish, bool round, bool solid,
            float x, float y, float z, float width, float height, float depth)
        {
            Name = name; Finish = finish; Round = round; Solid = solid;
            X = x; Y = y; Z = z; Width = width; Height = height; Depth = depth;
        }
    }

    internal static class AirSwampCore
    {
        internal const string Scene = "GW_Scene_3", PoiId = "swamp_mi8_platform";
        // World units: 2.8 u/m. Keep the approach and the rotor centre clear.
        internal const float LaneHalf = 24f, LandingHalf = 24f;
        internal const int SockRings = 5, SockSides = 8;
        internal static readonly SwampPart[] Parts = MakeParts();
        static SwampPart[] MakeParts()
        {
            SwampPart[] parts = new SwampPart[18];
            parts[0] = new SwampPart("NorthBeam", "wood", false, true, 0, -1.7f, 30, 64, 1.4f, 2);
            parts[1] = new SwampPart("SouthBeam", "wood", false, true, 0, -1.7f, -30, 64, 1.4f, 2);
            parts[2] = new SwampPart("WestBeam", "wood", false, true, -30, -1.7f, 0, 2, 1.4f, 64);
            parts[3] = new SwampPart("EastBeam", "wood", false, true, 30, -1.7f, 0, 2, 1.4f, 64);
            parts[4] = new SwampPart("WindsockPole", "metal", true, true, 31, 7, 31, .35f, 14, .35f);
            parts[5] = new SwampPart("LampPole", "metal", true, true, -31, 6, -31, .45f, 12, .45f);
            parts[6] = new SwampPart("LampArm", "metal", false, false, -29.8f, 11.7f, -31, 2.8f, .3f, .3f);
            parts[7] = new SwampPart("LampShade", "metal", false, false, -28.6f, 11.4f, -31, 1.5f, .35f, 1.1f);
            parts[8] = new SwampPart("LampGlass", "glass", false, false, -28.6f, 11.15f, -31, 1.1f, .15f, .7f);
            for (int i = 0; i < 3; i++)
            {
                float x = -31 + i * 3.2f;
                parts[9 + i * 3] = new SwampPart("Barrel" + i, "rust", true, true, x, 1.25f, 31, 1.7f, 2.5f, 1.7f);
                parts[10 + i * 3] = new SwampPart("BarrelBandLower" + i, "metal", true, false, x, .65f, 31, 1.78f, .12f, 1.78f);
                parts[11 + i * 3] = new SwampPart("BarrelBandUpper" + i, "metal", true, false, x, 1.9f, 31, 1.78f, .12f, 1.78f);
            }
            return parts;
        }
        internal static bool Approach(float x, float z)
        {
            AirRoute r = AirTravel.Find("mi8-main-swamp");
            return x >= AirLandingCore.DeckX - LandingHalf && x <= r.X
                && Math.Abs(z - AirLandingCore.DeckZ) <= LaneHalf;
        }
        // Fixed, sagging windsock. No Update, renderer polling or allocations in flight.
        internal static void SockPoint(int ring, int side, out float x, out float y, out float z)
        {
            float t = (float)ring / (SockRings - 1);
            float radius = .95f - .65f * t;
            double angle = 2 * Math.PI * side / SockSides;
            x = 31 - 7 * t;
            y = 13.5f - t * t * 2 + radius * (float)Math.Sin(angle);
            z = 31 + radius * (float)Math.Cos(angle);
        }
        internal static int[] SockTriangles(int band)
        {
            int[] target = new int[(SockRings - 1) / 2 * SockSides * 12];
            int i = 0;
            for (int r = band; r < SockRings - 1; r += 2) for (int s = 0; s < SockSides; s++)
            {
                int a = r * SockSides + s, b = r * SockSides + (s + 1) % SockSides;
                int c = b + SockSides, d = a + SockSides;
                target[i] = a; target[i + 1] = b; target[i + 2] = c;
                target[i + 3] = a; target[i + 4] = c; target[i + 5] = d;
                int back = SockRings * SockSides;
                target[i + 6] = c + back; target[i + 7] = b + back; target[i + 8] = a + back;
                target[i + 9] = d + back; target[i + 10] = c + back; target[i + 11] = a + back;
                i += 12;
            }
            return target;
        }
    }
}

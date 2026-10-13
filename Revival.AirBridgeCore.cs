// Air bridge routes and corridor checks. Engine-free, C# 3.0.
using System;

namespace NextDayRevival
{
    internal sealed class AirRoute
    {
        internal readonly string Id, Craft, From, To, Label;
        internal readonly float Cost, MinX, MaxX, MinZ, MaxZ, Outward;
        internal readonly float X, Z, Heading, Height, ParkX, ParkZ, ParkY;
        internal AirRoute(string id, string craft, string from, string to, string label,
            float cost, float minX, float maxX, float minZ, float maxZ, float outward,
            float x, float z, float heading, float height, float parkX, float parkZ, float parkY)
        {
            Id = id; Craft = craft; From = from; To = to; Label = label; Cost = cost;
            MinX = minX; MaxX = maxX; MinZ = minZ; MaxZ = maxZ; Outward = outward;
            X = x; Z = z; Heading = heading; Height = height;
            ParkX = parkX; ParkZ = parkZ; ParkY = parkY;
        }
        internal bool Contains(float x, float z, float yaw, float agl)
        {
            return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ && agl >= 30f
                && AirTravel.Angle(yaw, Outward) <= 45f;
        }
        internal float Required { get { return Cost * (From == "MAIN" ? 2f : 1f) + 0.10f; } }
    }

    internal static class AirTravel
    {
        internal const int RecoverSeconds = 120;
        internal const int MaxPeople = 32;
        internal static readonly AirRoute[] Routes = {
            new AirRoute("an2-main-prim", "an2", "MAIN", "PRIM", "Primorye", .20f, 3800, 5000, 2300, 3200, 0, 1256.2f, 1288, 225, 168, 913.5f, 974, 40.74f),
            new AirRoute("an2-prim-main", "an2", "PRIM", "MAIN", "Airfield", .20f, 1000, 1500, 1250, 1500, 45, 4405, 2350, 180, 280, 4395, 1100, -1),
            new AirRoute("mi8-main-swamp", "mi8", "MAIN", "SWAMP", "Toxic Swamp", .30f, 3800, 5000, -3200, -2300, 180, 1100, 420.2f, 270, 168, 692.9f, 420.2f, 38),
            new AirRoute("mi8-swamp-main", "mi8", "SWAMP", "MAIN", "Airfield", .30f, 1050, 1204, 0, 900, 90, 4405, -2350, 0, 280, 4510, 250, -1),
            new AirRoute("mi8-main-prim", "mi8", "MAIN", "PRIM", "Primorye", .40f, 3800, 5000, 2300, 3200, 0, 1256.2f, 1288, 225, 168, 956.9f, 1041, -1),
            new AirRoute("mi8-prim-main", "mi8", "PRIM", "MAIN", "Airfield", .40f, 1000, 1500, 1250, 1500, 45, 4405, 2350, 180, 280, 4510, 250, -1)
        };
        internal static float Angle(float a, float b)
        {
            float d = (a - b) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return Math.Abs(d);
        }
        internal static AirRoute Find(string id)
        {
            for (int i = 0; i < Routes.Length; i++) if (Routes[i].Id == id) return Routes[i];
            return null;
        }
        internal static AirRoute Gate(string craft, string region, float x, float z, float yaw, float agl)
        {
            for (int i = 0; i < Routes.Length; i++)
            {
                AirRoute r = Routes[i];
                if (r.Craft == craft && r.From == region && r.Contains(x, z, yaw, agl)) return r;
            }
            return null;
        }
        internal static int Scene(string region) { return region == "MAIN" ? 5 : region == "PRIM" ? 6 : region == "SWAMP" ? 7 : -1; }
        internal static float HeliSpeedCap(float configuredWorldSpeed, float an2TopKmh)
        {
            // Compare motion in world units: Mi-8 and An-2 HUD/model scales differ.
            return Math.Min(configuredWorldSpeed, an2TopKmh / 3.6f * 2.8f * .85f);
        }
    }

    // Water geometry is indexed once on scene load, never read from meshes in flight.
    internal struct AirWaterFace
    {
        internal readonly float AX, AY, AZ, BX, BY, BZ, CX, CY, CZ;
        readonly float _inverse;
        internal AirWaterFace(float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz)
        {
            AX = ax; AY = ay; AZ = az; BX = bx; BY = by; BZ = bz; CX = cx; CY = cy; CZ = cz;
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            _inverse = Math.Abs(d) < .00001f ? 0 : 1f / d;
        }
        internal bool Height(float x, float z, out float y)
        {
            y = 0;
            if (_inverse == 0) return false;
            float a = ((BZ - CZ) * (x - CX) + (CX - BX) * (z - CZ)) * _inverse;
            float b = ((CZ - AZ) * (x - CX) + (AX - CX) * (z - CZ)) * _inverse;
            float c = 1 - a - b;
            if (!(a >= -.00001f && b >= -.00001f && c >= -.00001f)) return false;
            y = a * AY + b * BY + c * CY;
            return !float.IsNaN(y) && !float.IsInfinity(y);
        }
    }

    internal static class AirLandingCore
    {
        // Carrier origin is the old wheel axle; floats immerse below the prop.
        internal const float WaterlineMetres = .45f;
        internal const float DeckX = 692.9f, DeckZ = 420.2f, DeckY = 38f, DeckHalf = 35f;
        internal static bool Deck(float x, float z)
        {
            return Math.Abs(x - DeckX) <= DeckHalf && Math.Abs(z - DeckZ) <= DeckHalf;
        }
        internal static bool ClearTrees(float x, float z)
        {
            return Math.Abs(x - DeckX) <= DeckHalf + 14 && Math.Abs(z - DeckZ) <= DeckHalf + 14;
        }
        internal static bool Wet(float water, float ground)
        {
            return Wet(water, ground, 1.4f);
        }
        internal static bool Wet(float water, float ground, float minimumDepth)
        {
            return !float.IsNaN(water) && !float.IsInfinity(water)
                && !float.IsNaN(ground) && !float.IsInfinity(ground) && water - ground >= minimumDepth;
        }
        internal static float WaterDrag(float speed)
        {
            // Hull drag stays below available thrust through rotation speed.
            return .12f + .0015f * speed * speed;
        }
        internal static float FloatBottom(float localZ) { return .25f - .1443f * localZ; }
    }
}

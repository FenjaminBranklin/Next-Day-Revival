// Z F2: static field-airfield ground layout, in world units (2.8 u = 1 m).
// Pure C# 3.0 so the offline harness executes the runtime rules directly.
using System;

namespace NextDayRevival
{
    internal static class AirfieldGroundCore
    {
        internal const float XMin = 3990f, XMax = 4820f;
        internal const float ZMin = -1690f, ZMax = 1690f;
        internal const float RunwayX = 4632f, RunwaySouth = -1602f, RunwayNorth = 1483f;
        internal const float RunwayWidth = 56f, FlightWidth = 196f, TrackWidth = 11.2f;

        // Closed perimeter, arrival/spine, retained service branches, cross track.
        // Tracks are terrain paint: no raised intersections or collider drapes.
        internal static readonly float[][] Tracks = new float[][] {
            new float[] { 4005f,1660f, 4800f,1660f, 4800f,-1660f, 4005f,-1660f, 4005f,1660f },
            new float[] { 4100f,1759.46f, 4100f,1690f, 4100f,1640f, 4180f,1570f, 4180f,400f,
                          4165f,300f, 4165f,-850f, 4180f,-950f, 4180f,-1110f },
            new float[] { 4180f,1335f, 4208f,1335f },
            new float[] { 4180f,1100f, 4191f,1100f },
            new float[] { 4180f,550f, 4165f,550f },
            new float[] { 4180f,-1110f, 4130f,-1110f },
            new float[] { 4130f,-1040f, 4130f,-1180f },
            new float[] { 4130f,-1040f, 4125f,-1040f },
            new float[] { 4130f,-1180f, 4125f,-1180f },
            new float[] { 4005f,75f, 4800f,75f }
        };

        internal static readonly string[] RemovedSlabs = new string[] {
            "T1", "T2", "T3", "A1", "A2", "A3", "F1a", "V1h", "V2h", "V3h", "S3f", "P1t", "P2", "P3"
        };

        // Only obsolete clutter actually obstructing the surveyed dirt routes.
        internal static readonly string[] RemovedObstructions = new string[] {
            "Tyres_Leaf", "RSP beacon hut", "Wire D2 N0", "Wire D2 S0",
            "pol_gate_leaf 1", "pol_gate_leaf 2", "W2a Gate vehicle wreck"
        };

        internal static bool RemoveObstruction(string name)
        {
            for (int i = 0; i < RemovedObstructions.Length; i++)
                if (name == RemovedObstructions[i]) return true;
            return false;
        }

        internal static bool ClearTree(float x, float z)
        {
            if (!InField(x, z) && !(z >= ZMax && z <= 1760f && Math.Abs(x - 4100f) < 16f)) return false;
            if (z >= RunwaySouth && z <= RunwayNorth && Math.Abs(x - RunwayX) <= FlightWidth / 2f) return true;
            return TrackDistance(x, z) <= TrackWidth / 2f + 4f;
        }

        internal static bool PaintAt(float x, float z)
        {
            // Tie the north arrival ruts into the unchanged external road.
            return InField(x, z) || (z > ZMax && z < 1754f && TrackDistance(x, z) < TrackWidth / 2f);
        }

        // Name matching is restricted by the caller to EastAirfieldRoot.
        // It also matches invisible drapes, fallback descendants and markers.
        internal static bool RemoveNode(string name)
        {
            if (name == "Surfaces") return true;
            for (int i = 0; i < RemovedSlabs.Length; i++)
            {
                string id = RemovedSlabs[i];
                if (name == id || name.StartsWith(id + " ", StringComparison.Ordinal)
                    || name.StartsWith(id + "|", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static bool InField(float x, float z)
        {
            return x >= XMin && x <= XMax && z >= ZMin && z <= ZMax;
        }

        internal static float TrackDistance(float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Tracks.Length; i++)
            {
                float[] p = Tracks[i];
                for (int j = 0; j + 3 < p.Length; j += 2)
                {
                    float dx = p[j + 2] - p[j], dz = p[j + 3] - p[j + 1];
                    float t = ((x - p[j]) * dx + (z - p[j + 1]) * dz) / (dx * dx + dz * dz);
                    t = Math.Max(0f, Math.Min(1f, t));
                    float a = x - p[j] - dx * t, b = z - p[j + 1] - dz * t;
                    best = Math.Min(best, a * a + b * b);
                }
            }
            return (float)Math.Sqrt(best);
        }

        internal struct Paint
        {
            internal float Grass, DryGrass, Stone, Dirt;
        }

        internal static Paint Sample(float x, float z)
        {
            // Low-frequency mottling and small irregular breaks, without new textures.
            float n = (float)(0.5 + 0.5 * Math.Sin(x * 0.071 + Math.Sin(z * 0.043) * 2.0));
            float edge = (float)(1.4 * Math.Sin(z * 0.11) + 0.8 * Math.Sin(z * 0.027));
            float hard = Clamp((RunwayWidth / 2f - Math.Abs(x - RunwayX) + edge) / 2.8f);
            if (z < RunwaySouth || z > RunwayNorth) hard = 0f;
            // Dirt crossings interrupt the strip at grade. No airport paint or lights.
            float dirt = Clamp((TrackWidth / 2f - TrackDistance(x, z)) / 2.8f);
            float worn = hard * (0.65f + 0.25f * n) * (1f - dirt);
            Paint p = new Paint();
            p.Stone = worn * 0.72f;
            p.Dirt = dirt * 0.82f + worn * 0.18f + (1f - hard) * (1f - dirt) * 0.12f;
            p.DryGrass = (1f - p.Stone - p.Dirt) * (0.32f + 0.15f * n);
            p.Grass = 1f - p.Stone - p.Dirt - p.DryGrass;
            return p;
        }

        static float Clamp(float v) { return Math.Max(0f, Math.Min(1f, v)); }
    }
}

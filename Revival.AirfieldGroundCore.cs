// Airfield ground rules, in world units (2.8 u = 1 m). Pure C# 3.0 so the
// offline harness (research/a_a1_airfield_restore.py) executes them directly.
//
// A1 (6.66 findings): the original concrete - Slabs, the Surfaces kit, the
// 107 u runway paint and their zone markers - stays exactly as the bundles
// ship it. Z F2 switched those off and repainted the field; that is reverted.
// Kept from the field concept: an open grass edge around the outside, made
// by pruning trees along the perimeter track, the north arrival and the
// runway corridor, plus a small clearing around each flak pit so all four
// gun positions stand under open sky. No paint, height, mesh or collider is
// changed.
using System;

namespace NextDayRevival
{
    internal static class AirfieldGroundCore
    {
        internal const float XMin = 3990f, XMax = 4820f;
        internal const float ZMin = -1690f, ZMax = 1690f;
        internal const float RunwayX = 4632f, RunwaySouth = -1602f, RunwayNorth = 1483f;
        internal const float FlightWidth = 196f, ClearHalfWidth = 9.6f;
        // Trunks kept this far beyond a pit's sandbag ring (3 m).
        internal const float PitClearMargin = 8.4f;

        // Outer perimeter loop (along the original P1t track, closed across
        // both approaches) and the north arrival from the external road.
        internal static readonly float[][] Clearing = new float[][] {
            new float[] { 4005f,1660f, 4800f,1660f, 4800f,-1660f, 4005f,-1660f, 4005f,1660f },
            new float[] { 4100f,1759.46f, 4100f,1660f }
        };

        internal static bool ClearTree(float x, float z)
        {
            if (!InField(x, z) && !(z >= ZMax && z <= 1760f && Math.Abs(x - 4100f) < 16f)) return false;
            if (z >= RunwaySouth && z <= RunwayNorth && Math.Abs(x - RunwayX) <= FlightWidth / 2f) return true;
            float pit = FlakPositionsCore.Outer * FlakPositionsCore.K + PitClearMargin;
            for (int i = 0; i < FlakPositionsCore.X.Length; i++)
            {
                float dx = x - FlakPositionsCore.X[i], dz = z - FlakPositionsCore.Z[i];
                if (dx * dx + dz * dz <= pit * pit) return true;
            }
            return ClearingDistance(x, z) <= ClearHalfWidth;
        }

        internal static bool InField(float x, float z)
        {
            return x >= XMin && x <= XMax && z >= ZMin && z <= ZMax;
        }

        internal static float ClearingDistance(float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Clearing.Length; i++)
            {
                float[] p = Clearing[i];
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
    }
}

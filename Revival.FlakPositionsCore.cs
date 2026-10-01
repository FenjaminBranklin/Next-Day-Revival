// Z L1b: deterministic open earthworks, metres converted once at construction.
// No Unity calls: the offline check compiles this exact geometry/damage core.
using System;

namespace NextDayRevival
{
    internal static class FlakPositionsCore
    {
        internal const float K = 2.8f, Inner = 6f, Outer = 9f, Height = 1.2f;
        internal const float Gate = 4f, PadLift = 0.4f, Pivot = 1.8f;
        internal const int Segments = 24;
        // Preserve gun/repair/merc wire IDs. Radar lease is 4; town is 2/3.
        internal static readonly int[] GunIndex = { 0, 6, 1, 5 };
        internal static readonly float[] X = { 4030f, 4375f, 4370f, 4100f };
        internal static readonly float[] Z = { 1615f, 1580f, 915f, 1000f };
        internal static readonly string[] Name = { "AA-NW", "AA-NE", "AA-S", "ZU-W" };

        internal static int Position(int gun)
        {
            for (int i = 0; i < GunIndex.Length; i++) if (GunIndex[i] == gun) return i;
            return -1;
        }

        internal static float Heading(int pit)
        {
            return (float)(Math.Atan2(4272.12 - X[pit], 1335.05 - Z[pit]) * 180 / Math.PI);
        }

        // The extra 0.5 m at each gate edge preserves >=4 m even inside the
        // conservative carving boxes around each sloped mesh sector.
        internal static float Angle(int edge)
        {
            double gap = Math.Asin((Gate / 2 + 0.5) / Inner);
            return (float)(gap + (2 * Math.PI - 2 * gap) * edge / Segments);
        }

        // Six vertices, two radial cross sections. Flat crown, steep inside,
        // broad outside earth slope; no overhead faces across the clear pad.
        internal static float[] Sector(int segment)
        {
            float[] v = new float[18];
            for (int end = 0; end < 2; end++)
            {
                double angle = Angle(segment + end);
                for (int ring = 0; ring < 3; ring++)
                {
                    float radius = ring == 0 ? Inner : ring == 1 ? Inner + 0.65f : Outer;
                    int j = (end * 3 + ring) * 3;
                    v[j] = (float)Math.Sin(angle) * radius * K;
                    v[j + 1] = (ring == 1 ? Height : -0.25f) * K;
                    v[j + 2] = (float)Math.Cos(angle) * radius * K;
                }
            }
            return v;
        }

        // Local tangent box: centre radius, radial depth, tangential width.
        // Used by carving and included in the final offline route check.
        internal static float[] Carve(int segment)
        {
            double half = (Angle(segment + 1) - Angle(segment)) / 2;
            float low = Inner * (float)Math.Cos(half);
            return new float[] { (low + Outer) * K / 2,
                (Outer - low) * K, 2 * Outer * (float)Math.Sin(half) * K };
        }

        internal static bool Inside(float x, float y, float z)
        {
            return x * x + z * z <= Inner * Inner * K * K
                && y >= -K * 0.5f && y <= K * 3.5f;
        }

        internal static float Damage(float damage, float health, float blastX, float blastZ)
        {
            // Direct hits anywhere inside the pad keep the real damage.
            // Near misses injure, including repeated hits, but leave 1 HP:
            // the game's existing wounded RPC/medical systems decide downing.
            if (blastX * blastX + blastZ * blastZ <= Inner * Inner * K * K) return damage;
            return Math.Max(0f, Math.Min(damage * 0.18f, health - 1f));
        }
    }
}

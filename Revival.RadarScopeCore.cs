// Z R2a: map scope geometry, compiled unchanged by the offline simulation.
using System;

namespace NextDayRevival
{
    internal static class RadarScopeCore
    {
        internal const int NativeWidth = 1848, NativeHeight = 924;

        internal static float Coverage(float configured, float eyeX, float eyeZ,
                                       float west, float south, float width, float height)
        {
            float dx = Math.Max(Math.Abs(eyeX - west), Math.Abs(eyeX - west - width));
            float dz = Math.Max(Math.Abs(eyeZ - south), Math.Abs(eyeZ - south - height));
            return Math.Max(configured, (float)Math.Sqrt(dx * dx + dz * dz) + 1f);
        }

        internal static void Fit(float x, float y, float width, float height, float aspect,
                                 out float left, out float top, out float w, out float h)
        {
            // Native map artwork: never magnify a lower-resolution image.
            // Whole pixel edges and a whole pixel height give exact 1:1 sampling
            // of the cached viewport texture (world aspect is exactly 2:1).
            float pixelX = (float)Math.Ceiling(x), pixelY = (float)Math.Ceiling(y);
            float roomW = width - (pixelX - x), roomH = height - (pixelY - y);
            h = Math.Max(1f, (float)Math.Floor(Math.Min(NativeHeight,
                Math.Min(roomH, Math.Min(roomW, NativeWidth) / aspect))));
            w = (float)Math.Floor(h * aspect);
            left = pixelX + (float)Math.Floor((roomW - w) * 0.5f);
            top = pixelY + (float)Math.Floor((roomH - h) * 0.5f);
        }

        internal static float Heading(float vx, float vz)
        { return (float)(Math.Atan2(vx, vz) * 180.0 / Math.PI); }

        // Nose-up, top-down map silhouettes in a 40-unit design space.
        // Recon has wings; FPV has four rotors. Neither is the unknown diamond.
        internal static bool Ink(int type, float x, float y)
        {
            float ax = Math.Abs(x), ay = Math.Abs(y);
            switch (type)
            {
                case 0:
                    float r2 = x * x + (y - 3f) * (y - 3f);
                    return (r2 >= 11f * 11f && r2 <= 14f * 14f)
                        || (ax <= 4f && y >= -4f && y <= 12f)
                        || (ax <= 2f && y >= -17f && y <= -4f)
                        || (ax <= 5f && y >= -17f && y <= -14f);
                case 1:
                    return (ax <= 3f && y >= -15f && y <= 16f)
                        || (ax <= 17f && y >= 3f && y <= 8f)
                        || (ax <= 7f && y >= -15f && y <= -11f);
                case 2:
                    return (ax <= 3f && y >= -17f && y <= 18f)
                        || (y <= 6f - ax * 0.55f && y >= -ax * 0.55f && ax <= 18f)
                        || (y <= -10f - ax * 0.4f && y >= -14f - ax * 0.4f && ax <= 8f);
                case 3:
                    float rotor = (ax - 10f) * (ax - 10f) + (ay - 10f) * (ay - 10f);
                    return (ax <= 3f && ay <= 5f)
                        || (Math.Abs(ax - ay) <= 2f && ax <= 10f) || rotor <= 30f;
                case 4:
                    return (ax <= 3f && y >= -12f && y <= 14f)
                        || (ax <= 16f && y >= -4f && y <= 1f)
                        || (ax <= 7f && y >= -12f && y <= -9f);
                default: return ax + ay <= 13f && ax + ay >= 8f;
            }
        }

        internal static void Project(float x, float z, float west, float south,
                                     float width, float height, float left, float top,
                                     float w, float h, out float px, out float py)
        {
            px = left + (x - west) / width * w;
            py = top + (1f - (z - south) / height) * h;
        }

        internal static bool Inside(float x, float z, float west, float south, float width, float height)
        {
            return x >= west && x <= west + width && z >= south && z <= south + height;
        }
    }
}

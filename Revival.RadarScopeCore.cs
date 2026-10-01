// Z R2a: map scope geometry, compiled unchanged by the offline simulation.
using System;

namespace NextDayRevival
{
    internal static class RadarScopeCore
    {
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
            w = Math.Max(1f, Math.Min(width, height * aspect));
            h = w / aspect;
            left = x + (width - w) * 0.5f;
            top = y + (height - h) * 0.5f;
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

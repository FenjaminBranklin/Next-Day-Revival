// Z M4: fixed server-priced supply baskets. C# 3.0, ASCII only.
using System;

namespace NextDayRevival
{
    internal static class TowerDeliveryCore
    {
        internal const int ShellId = 2076, BeltId = 2077;
        internal const int Cooldown = 600;
        internal const float Units = 2.8f, RadiusMin = 65f, RadiusMax = 145f;
        internal static bool Service(int service) { return service >= 4 && service <= 18; }
        internal static int Mask(int service) { return Service(service) ? service - 3 : 0; }
        internal static int Price(int mask)
        {
            if (mask < 1 || mask > 15) return 0;
            return 10000 + ((mask & 1) != 0 ? 24000 : 0)
                + ((mask & 2) != 0 ? 18000 : 0) + ((mask & 4) != 0 ? 12000 : 0)
                + ((mask & 8) != 0 ? 10000 : 0);
        }
        internal static int Quantity(int kind)
        { return kind == 0 ? 24 : kind == 1 || kind == 2 ? 6 : kind == 3 ? 2 : 0; }
        internal static int Item(int kind)
        { return kind == 0 ? ShellId : kind == 1 ? BeltId : kind == 2 ? 7013 : kind == 3 ? 10005 : 0; }
        internal static int ItemAt(int mask, int slot)
        {
            if (mask < 1 || mask > 15 || slot < 0) return 0;
            for (int kind = 0; kind < 4; kind++)
                if ((mask & (1 << kind)) != 0)
                {
                    if (slot < Quantity(kind)) return Item(kind);
                    slot -= Quantity(kind);
                }
            return 0;
        }
        internal static bool CanOrder(bool enabled, bool alive, bool near, bool held,
            bool busy, int mask, float now, float ready)
        { return enabled && alive && near && held && !busy && Price(mask) > 0 && now >= ready; }
        internal static void Scatter(int seed, int attempt, out float x, out float z)
        {
            // No Unity random state changes; independent attempts in an annulus.
            uint n = unchecked((uint)seed + (uint)attempt * 747796405u + 2891336453u);
            n ^= n >> 16; n *= 2246822519u; n ^= n >> 13;
            double angle = (n & 65535u) * (Math.PI * 2.0 / 65536.0);
            double radius = Math.Sqrt(RadiusMin * RadiusMin
                + (RadiusMax * RadiusMax - RadiusMin * RadiusMin) * ((n >> 16) / 65535.0));
            x = (float)(Math.Cos(angle) * radius * Units);
            z = (float)(Math.Sin(angle) * radius * Units);
        }
    }
}

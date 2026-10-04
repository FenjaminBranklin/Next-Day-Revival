// Z M4: fixed server-priced supply baskets. C# 3.0, ASCII only.
using System;

namespace NextDayRevival
{
    internal static class TowerDeliveryCore
    {
        internal const int ShellId = 2076, BeltId = 2077;
        internal const int Cooldown = 600;
        internal const float Units = 2.8f;
        internal const int Sites = 12;
        // World coordinates: east_layout.json runway (107 u wide). These
        // inset points are east of the apron, away from its parked aircraft.
        internal static bool OnRunway(float x, float z)
        { return x >= 4604f && x <= 4660f && z >= 990f && z <= 1350f; }
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
        { return kind == 0 ? 28 : kind == 1 || kind == 2 ? 6 : kind == 3 ? 2 : 0; }
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
            // Visit all 12 candidates once, without changing Unity random state.
            int index = (int)(((uint)seed % Sites + (uint)attempt * 5u) % Sites);
            x = 4612f + (index % 3) * 20f;
            z = 1020f + (index / 3) * 100f;
        }
    }
}

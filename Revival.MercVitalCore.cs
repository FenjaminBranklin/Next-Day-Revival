// Z K6a: spawn-time merc vitality. No ticks, physics or managed allocations.
using System;
namespace NextDayRevival
{
    internal static class MercVital
    {
        internal static int Tier(float grade)
        { return grade < 0.2f ? 0 : grade < 0.45f ? 1 : grade < 0.7f ? 2 : 3; }

        internal static float HealthScale(float grade, int healthPercent)
        { return healthPercent == 0 ? 1.5f + Tier(grade) / 3f : Math.Max(100, Math.Min(500, healthPercent)) / 100f; }

        internal static float DamageScale(float grade, int armorPercent)
        { return 1f - (armorPercent < 0 ? 40 + 5 * Tier(grade) : Math.Max(0, Math.Min(90, armorPercent))) / 100f; }

        internal static float MaxHealth(float baseHealth, int tanky, float grade, int healthPercent)
        { return baseHealth * (1f + tanky / 100f) * HealthScale(grade, healthPercent); }

        // Ordinary defender weapons must not grow stronger when merc HP increases.
        internal static float DefenderDamage(bool merc, float full, float configured, float head, float hits)
        { return merc || full <= 0f ? configured : (full + 1f) / (head * hits); }
    }
}

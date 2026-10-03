// G A1: fire reach, airframe resilience and escort manoeuvres. Engine-free;
// research/aa_raid_balance_sim.py compiles this unchanged with csc 3.5.
using System;

namespace NextDayRevival
{
    internal static class AARaidBalanceCore
    {
        internal const float WorldPerMetre = 2.8f;
        internal const float HeavyRangeM = 3500f;
        internal const int BomberToughness = 3;
        // Retake formations fly at their nominal aircraft speed, rather than
        // inheriting the slow scenic flyover factor. Keep other events editable.
        internal const float RaidSpeedFactor = 1f;
        internal const float WeaveAmplitudeM = 90f, WeavePeriodSeconds = 24f;
        internal const float SettleSeconds = 14f;

        internal static float FireRange(bool shortRange)
        {
            return shortRange ? ShortRangeCore.RangeM : HeavyRangeM;
        }

        // Radar calibration/zone reach may restrict fire, never extend it.
        internal static float FireReach(float trackingMetres, bool shortRange)
        {
            return Math.Min(Math.Max(0f, trackingMetres), FireRange(shortRange));
        }

        internal static bool CanFire(float slantUnits, bool shortRange)
        {
            // One millimetre of world-unit tolerance for CLR float rounding at
            // the boundary (the .NET 3.5 JIT may retain intermediate precision).
            return slantUnits >= 0f && slantUnits <= FireRange(shortRange) * WorldPerMetre + 0.001f;
        }

        // Rifles already use RoundDamage(toughness). Direct lethal blasts stay
        // lethal; all nonlethal gun fractions share the tougher bomber ledger.
        internal static float GunDamage(float amount, int toughness)
        {
            return amount >= 1f ? amount : amount / Math.Max(1, toughness);
        }

        // Smooth bounded S-turns in the horizontal plane. Fade to the bomb
        // line before release: no teleport, off-axis perfect bombing or raycast.
        // along/release are metres on the straight path; speed is metres/s.
        internal static float Weave(float along, float release, float speed, float phase)
        {
            if (speed <= 0f || along >= release) return 0f;
            float remaining = (release - along) / speed;
            float envelope = Math.Min(1f, remaining / SettleSeconds);
            envelope = envelope * envelope * (3f - 2f * envelope);
            return WeaveAmplitudeM * envelope * (float)Math.Sin(along / speed
                * (2.0 * Math.PI / WeavePeriodSeconds) + phase);
        }

        internal static float WeaveSlope(float along, float release, float speed, float phase)
        {
            return (Weave(along + 0.5f, release, speed, phase)
                - Weave(along - 0.5f, release, speed, phase));
        }
    }
}

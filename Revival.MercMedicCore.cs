// Z K7b: bounded triage and treatment decisions, no world queries. C# 3.0.
namespace NextDayRevival
{
    internal static class MercMedicPolicy
    {
        internal const float ReviveSeconds = 4f, RunUnitsPerSecond = 20f;
        internal static bool Better(bool down, float until, float distance, int id,
            bool bestDown, float bestUntil, float bestDistance, int bestId)
        {
            if (bestId == 0) return true;
            if (down != bestDown) return down;
            if (down && until != bestUntil) return until < bestUntil;
            return distance < bestDistance || (distance == bestDistance && id < bestId);
        }
        internal static bool Reachable(float now, float until, float distance)
        { return now + distance / RunUnitsPerSecond + ReviveSeconds < until; }
        internal static bool MayApproach(bool alive, bool seated, bool deserting,
            float now, float next, bool supplied)
        { return alive && !seated && !deserting && now >= next && supplied; }
        internal static bool Calm(bool sees, bool exposed, bool fresh, float hp,
            float now, float lastHit, float suppression, bool danger)
        { return !sees && !exposed && fresh && hp > 0f && now - lastHit >= 4f && suppression < .15f && !danger; }
    }

    internal sealed class MercMedicAid
    {
        internal bool Pending, Active;
        internal int Healer, Item, Token;
        internal float Started;
        internal bool Ready(float now)
        { return Active && !Pending && now - Started >= MercMedicine.Seconds(Item); }
        internal void Cancel() { Pending = Active = false; Healer = Item = 0; Token++; }
    }
}

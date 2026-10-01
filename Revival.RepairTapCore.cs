// Q1: allocation-free cancellation policy, also exercised by the offline check.
namespace NextDayRevival
{
    internal static class RepairTapCore
    {
        internal static bool Cancel(int frame, int startedFrame, bool pressed,
            bool moving, float distanceSquared, float health, float previousHealth,
            bool available)
        {
            return !available || health <= 0f || health < previousHealth
                || distanceSquared > 2.8f * 2.8f
                || (frame > startedFrame && (pressed || moving));
        }
    }
}

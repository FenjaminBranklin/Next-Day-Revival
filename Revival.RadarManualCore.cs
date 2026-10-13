// Manual tracks are a superset of gun acquisitions. A terrain radar solution
// affects gun calibration, not permission to select a live registered aircraft.
using System;

namespace NextDayRevival
{
    internal struct RadarManualStats
    {
        internal int Frame, Candidates, Listed, Invalid, Offline, Range;
        internal int OldShadow, OldRange, OldHeight, OldSweep, Near, Far, Deferred;
        internal int PendingPool;
    }

    internal static class RadarManualCore
    {
        internal const int Accepted = 0, Invalid = 1, Offline = 2, Range = 3;
        internal const float NearUnits = 1800f * 2.8f;

        internal static int Reject(bool working, bool alive, float distanceSquared, float range)
        {
            if (!alive) return Invalid;
            if (!working) return Offline;
            return distanceSquared > range * range ? Range : Accepted;
        }

        // Triangle inequality: every point in a gun's engagement sphere is in
        // this radar-centred sphere. Pad the mount/cradle offset by 10 metres.
        internal static float Coverage(float scope, float gunDistance, float gunRange, float airfieldReach)
        {
            return Math.Max(scope, gunDistance + Math.Max(gunRange, airfieldReach) + 10f * 2.8f);
        }

        internal static float Interval(float distanceSquared, bool selected)
        {
            return selected || distanceSquared <= NearUnits * NearUnits ? 0.25f : 1f;
        }
    }
}

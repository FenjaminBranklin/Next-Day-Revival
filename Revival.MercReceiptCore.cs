// One command's bounded progress check. No queries or steady allocations.
using UnityEngine;

namespace NextDayRevival
{
    internal struct MercReceiptClock
    {
        internal bool Pending;
        internal float Deadline;
        internal Vector3 Origin;
        internal int Shots;

        internal void Begin(float now, Vector3 origin, int shots)
        {
            Pending = true; Deadline = now + 3f; Origin = origin; Shots = shots;
        }

        // Movement or actual firing proves execution started, not arrival.
        internal bool Check(float now, Vector3 at, int shots, bool settled)
        {
            if (!Pending) return false;
            Vector3 d = at - Origin; d.y = 0f;
            if (settled || d.sqrMagnitude >= 1.96f || shots > Shots)
            { Pending = false; return false; }
            if (now < Deadline) return false;
            Pending = false;
            return true;
        }
    }
}

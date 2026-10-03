// G O1: who an order reaches. Pure C# 3.0; tested unchanged offline.
namespace NextDayRevival
{
    internal static class MercTargetPlan
    {
        /// <summary>The player picked mercs: some but not all rows are
        /// checked in L, or rows were chosen (Ctrl+1..5, a checkbox, a merc's
        /// own row) since the last Ctrl+0. Nothing checked is no pick.</summary>
        internal static bool Explicit(bool picked, int selected, int alive)
        {
            return selected > 0 && (picked || selected < alive);
        }

        /// <summary>A squad order reaches a picked merc; with no pick every
        /// merc except a gun or radar crew, who keeps his post.</summary>
        internal static bool Takes(bool pick, bool selected, bool onDuty)
        {
            return pick ? selected : !onDuty;
        }

        /// <summary>The nearest candidate not yet taken; with preferCar one
        /// beside a ready vehicle beats any without. -1 when none is left.</summary>
        internal static int Nearest(float[] sqrDistance, bool[] car, bool[] taken, int count, bool preferCar)
        {
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (taken[i]) continue;
                if (best < 0) { best = i; continue; }
                if (preferCar && car[i] != car[best]) { if (car[i]) best = i; continue; }
                if (sqrDistance[i] < sqrDistance[best]) best = i;
            }
            return best;
        }
    }
}

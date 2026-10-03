// G O2: shared, allocation-free menu topology; order IDs retain their dispatch.
namespace NextDayRevival
{
    internal static class MercRadialPlan
    {
        internal const int Groups = 4;
        static readonly int[][] Orders = {
            new int[] { 0, 1, 2, 3 }, new int[] { 8, 7, 5 },
            new int[] { 10, 4, 9 }, new int[] { 6 }
        };

        internal static int Count(int group)
        { return group < 0 ? Groups : group < Groups ? Orders[group].Length : 0; }

        internal static int Order(int group, int slot)
        { return group >= 0 && group < Groups && slot >= 0 && slot < Orders[group].Length ? Orders[group][slot] : -1; }
    }
}

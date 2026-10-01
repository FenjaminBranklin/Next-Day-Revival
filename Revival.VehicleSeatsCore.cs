// Next Day: Survival - Revival Toolkit. Z K9b: cached, read-only seat cards.
using System;

namespace NextDayRevival
{
    internal struct VehicleSeatVital
    {
        internal int Kind, View, Seat;
        internal float Health, Seen;

        internal static bool Parse(float[] data, int sender, float now, out VehicleSeatVital vital)
        {
            vital = new VehicleSeatVital();
            if (sender <= 0 || data == null || data.Length != 5 || data[0] != 102f) return false;
            for (int i = 0; i < data.Length; i++)
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i])) return false;
            if (data[1] != (int)data[1] || data[1] < 0f || data[1] > 2f
                || data[2] != (int)data[2] || data[2] <= 0f || data[2] >= 2147483647f
                || data[3] != (int)data[3] || data[3] < -1f || data[3] > 63f
                || (data[1] == 0f && data[3] < 0f) || data[4] < 0f || data[4] > 1f) return false;
            vital.Kind = (int)data[1]; vital.View = (int)data[2]; vital.Seat = (int)data[3];
            vital.Health = data[4]; vital.Seen = now;
            return true;
        }

        internal float ForSeat(int kind, int view, int seat, float now)
        {
            return Kind == kind && View == view && Seat == seat && now - Seen <= 3f && now >= Seen ? Health : -1f;
        }
    }

    internal sealed class VehicleSeatCard
    {
        internal const int Free = 0, Player = 1, Merc = 2, Occupied = 3;
        internal readonly string Prefix;
        internal string Name, Caption;
        internal int Kind = -1;
        internal float Health = -1f;

        internal VehicleSeatCard(string prefix) { Prefix = prefix; }

        // Players win the same race as in the existing boarding code.
        internal static int Choose(bool player, bool ownMerc, bool remoteMerc)
        {
            return player ? Player : ownMerc ? Merc : remoteMerc ? Occupied : Free;
        }

        // Unknown health is distinct from both dead and healthy.
        internal static float Fraction(float have, float maximum)
        {
            if (float.IsNaN(have) || float.IsInfinity(have)
                || float.IsNaN(maximum) || float.IsInfinity(maximum) || maximum <= 0f) return -1f;
            return Math.Max(0f, Math.Min(1f, have / maximum));
        }

        internal bool Set(int kind, string name, float health)
        {
            Health = kind == Free ? -1f : Fraction(health, 1f);
            if (health < 0f) Health = -1f;
            if (kind == Kind && name == Name) return false;
            Kind = kind; Name = name; Caption = Prefix + name;
            return true;
        }

        internal static int Columns(int count) { return count <= 1 ? 1 : 2; }
        internal static int Rows(int count) { return (count + Columns(count) - 1) / Columns(count); }
    }
}

// Z M3: finite cargo escrow. No world, network, clock or inventory calls.
using System;

namespace NextDayRevival
{
    internal sealed class MercSupplyTicket
    {
        internal const int Going = 1, Carrying = 2, Loading = 3, Done = 4, Lost = 5, Empty = 6, Unavailable = 7;
        internal int Actor = -1, View, Seq, Gun, Stage, Item, Amount, Revision;
        internal float Until, LoadAt;
    }

    internal sealed class MercSupplyLedger
    {
        internal const int Capacity = 32;
        internal readonly MercSupplyTicket[] Rows = new MercSupplyTicket[Capacity];
        readonly int[] _actors = new int[Capacity], _seen = new int[Capacity];
        internal MercSupplyLedger()
        { for (int i = 0; i < Rows.Length; i++) { Rows[i] = new MercSupplyTicket(); _actors[i] = -1; } }
        internal static bool Active(int stage)
        { return stage >= MercSupplyTicket.Going && stage <= MercSupplyTicket.Loading; }
        internal static bool Repair(int gun) { return gun >= 100 && gun <= 108 && gun != 104; }
        internal static bool Destination(int gun) { return gun >= -1 && gun <= 6 && gun != 4 || Repair(gun); }
        internal static float Seconds(int gun) { return Repair(gun) ? gun >= 107 ? 60f : 45f : 3f; }
        internal static bool Low(bool belt, int stock)
        { return stock <= (belt ? 200 : 6); }
        internal static bool Depart(bool low, bool airPass, bool dry)
        { return low && (!airPass || dry); }
        internal MercSupplyTicket Find(int actor, int view)
        {
            for (int i = 0; i < Rows.Length; i++)
                if (Rows[i].Actor == actor && Rows[i].View == view) return Rows[i];
            return null;
        }
        internal MercSupplyTicket Begin(int actor, int view, int seq, int gun, float now)
        {
            if (actor < 0 || view <= 0 || seq <= 0 || seq > 16000000 || !Destination(gun)) return null;
            MercSupplyTicket own = Find(actor, view);
            if (own != null)
            {
                if (seq == own.Seq) return own; // retries never restart a finished trip
                if (seq < own.Seq || Active(own.Stage)) return null;
            }
            int replay = -1;
            for (int i = 0; i < _actors.Length; i++) if (_actors[i] == actor) { replay = i; break; }
            if (replay < 0)
                for (int i = 0; i < _actors.Length; i++) if (_actors[i] < 0) { replay = i; break; }
            if (replay < 0 || (_actors[replay] == actor && seq <= _seen[replay])) return null;
            for (int i = 0; i < Rows.Length; i++)
                if (gun >= 0 && Rows[i] != own && Rows[i].Gun == gun && Active(Rows[i].Stage)) return null;
            if (own == null)
            {
                // Actor high-water marks protect replay when a completed body
                // slot is reused after a respawn. Bound memory to room capacity.
                for (int i = 0; i < Rows.Length; i++)
                    if (Rows[i].Actor < 0 || !Active(Rows[i].Stage)) { own = Rows[i]; break; }
            }
            if (own == null) return null;
            _actors[replay] = actor; _seen[replay] = seq;
            own.Actor = actor; own.View = view; own.Seq = seq; own.Gun = gun;
            own.Stage = MercSupplyTicket.Going; own.Item = own.Amount = 0;
            own.Until = now + 180f; own.LoadAt = 0f;
            return own;
        }
        internal bool Pickup(MercSupplyTicket t, int item, int amount)
        {
            if (t == null || t.Stage != MercSupplyTicket.Going || amount <= 0) return false;
            if (Repair(t.Gun) ? item != 10005 && item != 2064 : t.Gun >= 0 ? (item != 2076 && item != 2077) : !MercMedicine.Item(item)) return false;
            t.Item = item; t.Amount = amount; t.Stage = MercSupplyTicket.Carrying;
            return true;
        }
        internal bool Load(MercSupplyTicket t, float now)
        {
            if (t == null || t.Stage != MercSupplyTicket.Carrying) return false;
            t.Stage = MercSupplyTicket.Loading; t.LoadAt = now + Seconds(t.Gun);
            return true;
        }
        internal int Finish(MercSupplyTicket t, float now)
        {
            if (t == null || t.Stage != MercSupplyTicket.Loading || now < t.LoadAt) return 0;
            int amount = t.Amount; t.Stage = MercSupplyTicket.Done;
            return amount; // caller credits only on this transition, exactly once
        }
        internal void Cancel(MercSupplyTicket t)
        { if (t != null && Active(t.Stage)) t.Stage = MercSupplyTicket.Lost; }
        internal void Expire(float now)
        {
            for (int i = 0; i < Rows.Length; i++)
                if (Active(Rows[i].Stage) && now >= Rows[i].Until) Rows[i].Stage = MercSupplyTicket.Lost;
        }
        internal void Reset()
        { for (int i = 0; i < Rows.Length; i++) { Rows[i].Actor = -1; Rows[i].Stage = 0; _actors[i] = -1; _seen[i] = 0; } }
        internal static int Integer(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < -1f || value > 16000000f) return int.MinValue;
            int n = (int)value; return value == n ? n : int.MinValue;
        }
    }
}

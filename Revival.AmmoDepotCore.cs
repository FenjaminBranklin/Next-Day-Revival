// Z M2: finite storage and one serialized, owner-authored cargo transaction.
using System;
using System.Collections.Generic;

namespace NextDayRevival
{
    internal struct DepotGood
    {
        internal int Id, Bullets, Clip;
        internal float Condition, Water, Energy;
        internal DepotGood(int id, int bullets, int clip, float condition, float water)
        { Id = id; Bullets = bullets; Clip = clip; Condition = condition; Water = water; Energy = 0f; }
    }

    internal sealed class DepotStore
    {
        internal const int Capacity = 256;
        internal readonly DepotGood[] Goods = new DepotGood[Capacity];
        internal int Count, Revision;
        internal bool Seeded, Known;
        internal float Hp = 1f;
        // A lease reserves capacity (or one gun load), never creates goods.
        internal int Actor = -1, Owner = -1, Truck, Gun = -1, Ticket, Received, Limit;
        internal float Ready, Until;
        readonly Dictionary<int, int> _seen = new Dictionary<int, int>();

        internal static bool Item(int id)
        { return id == 2076 || id == 2077 || id == 10005 || id == 2064 || (id >= 7011 && id <= 7016); }
        internal static bool Valid(DepotGood g)
        {
            return Item(g.Id) && g.Bullets >= 0 && g.Bullets <= 100000 && g.Clip >= 0 && g.Clip <= 100000
                && !float.IsNaN(g.Condition) && !float.IsInfinity(g.Condition) && g.Condition >= 0f && g.Condition <= 100f
                && !float.IsNaN(g.Water) && !float.IsInfinity(g.Water) && g.Water >= 0f && g.Water <= 10000f
                && !float.IsNaN(g.Energy) && !float.IsInfinity(g.Energy) && g.Energy >= 0f && g.Energy <= 10000f;
        }
        internal bool Add(DepotGood g)
        {
            if (!Valid(g) || Count >= Capacity) return false;
            Goods[Count++] = g; Revision++; return true;
        }
        internal int Find(int id)
        { for (int i = 0; i < Count; i++) if (Goods[i].Id == id) return i; return -1; }
        internal DepotGood Remove(int index)
        {
            DepotGood g = Goods[index];
            for (int i = index + 1; i < Count; i++) Goods[i - 1] = Goods[i];
            Goods[--Count] = new DepotGood(); Revision++; return g;
        }
        internal bool Capture(bool byPlayers, bool master)
        {
            if (!master || !byPlayers || Seeded || !Known) return false;
            Seeded = true; Revision++;
            // One room's garrison remainder: half the measured p90 standard raid
            // (Z K6b: 135 shells from three crewed 52-Ks) plus 600 ZU rounds.
            // Recapture does not mint a second lot.
            if (Hp <= 0f) return true;
            // Condition 0 is the native spawn default (research/items.tsv).
            for (int i = 0; i < 68; i++) Add(new DepotGood(2076, 1, 0, 0f, 0f));
            for (int i = 0; i < 6; i++) Add(new DepotGood(2077, 100, 0, 0f, 0f));
            for (int i = 0; i < 6; i++) Add(new DepotGood(7013, 0, 0, 0f, 0f));
            for (int i = 0; i < 2; i++) Add(new DepotGood(10005, 0, 0, 0f, 0f));
            return true;
        }
        internal bool Hit(float amount, bool master)
        {
            if (!master || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f || Hp <= 0f) return false;
            Hp = Math.Max(0f, Hp - amount); Revision++;
            if (Hp == 0f)
            {
                // Half survives as loot in the wreck. No instant spawn burst.
                for (int i = Count - 1; i >= 0; i -= 2) Remove(i);
                if (Gun < 0) Cancel();
            }
            return true;
        }
        internal int Begin(int actor, int request, int owner, int truck, int gun, int limit, float now, bool master)
        {
            if (!master || !Known || (Hp <= 0f && gun < 0) || actor < 0 || owner < 0 || truck <= 0 || request <= 0 || limit <= 0) return 0;
            int last;
            if (gun < 0)
            {
                if (_seen.TryGetValue(actor, out last) && request <= last) return 0;
                _seen[actor] = request;
            }
            if (Actor >= 0 && now < Until) return 0;
            if (gun < 0) limit = Math.Min(limit, Capacity - Count);
            if (limit <= 0 || Ticket >= 16000000) return 0;
            Actor = actor; Owner = owner; Truck = truck; Gun = gun; Limit = limit; Received = 0;
            Ready = now + (gun < 0 ? 4f : 0f); Until = now + 30f;
            return ++Ticket;
        }
        internal bool Receipt(int owner, int ticket, int sequence, float now, DepotGood good)
        {
            if (!Known || (Hp <= 0f && Gun < 0) || Actor < 0 || owner != Owner || ticket != Ticket
                || now < Ready || now >= Until || sequence != Received + 1 || Received >= Limit || !Valid(good)) return false;
            if (Gun < 0 && !Add(good)) return false;
            Received++; return true;
        }
        internal void Cancel() { Actor = Owner = -1; Truck = 0; Gun = -1; Limit = Received = 0; Ready = Until = 0f; }
        internal void NewMaster() { Cancel(); _seen.Clear(); }
    }
}

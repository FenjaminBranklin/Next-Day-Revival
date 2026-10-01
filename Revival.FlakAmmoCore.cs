// Z M1: engine-free gun stock and one-item supply reservation.
using System.Collections.Generic;

namespace NextDayRevival
{
    internal sealed class FlakAmmoStock
    {
        internal const int ShellItem = 2076, BeltItem = 2077;
        internal int Stock, Revision, Actor = -1, Ticket, Amount;
        internal bool Known, Limited;
        internal float Until;
        readonly Dictionary<int, int> _requests = new Dictionary<int, int>();

        internal static int Capacity(bool belt) { return belt ? 1000 : 60; }
        internal static int Supply(bool belt) { return belt ? 100 : 1; }

        internal bool Mode(bool limited, bool master)
        {
            if (!master) return false;
            bool changed = !Known || limited != Limited;
            Known = true;
            Limited = limited;
            if (changed) Revision++;
            return changed;
        }

        internal bool CanShoot { get { return Known && (!Limited || Stock > 0); } }

        internal bool Spend(bool master)
        {
            if (!master || !CanShoot) return false;
            if (Limited) { Stock--; Revision++; }
            return true;
        }

        internal int Rack(int size)
        {
            if (!Known) return 0;
            return Limited ? System.Math.Min(size, Stock) : size;
        }

        internal int Reserve(int actor, int request, float now, bool belt, bool master)
        {
            if (!master || !Known || !Limited || actor < 0 || request <= 0) return 0;
            int last;
            if (_requests.TryGetValue(actor, out last) && request <= last) return 0;
            _requests[actor] = request;
            int amount = Supply(belt);
            if (Stock > Capacity(belt) - amount || (Actor >= 0 && now < Until)) return 0;
            Actor = actor; Amount = amount; Until = now + 8f;
            // Exact integers on the float wire. Refuse rather than reuse a token.
            if (Ticket >= 16000000) { Actor = -1; return 0; }
            return ++Ticket;
        }

        internal bool Commit(int actor, int ticket, float now, bool master)
        {
            if (!master || Actor != actor || ticket != Ticket || now >= Until) return false;
            Stock += Amount; Revision++;
            Actor = -1; Amount = 0;
            return true;
        }

        internal void Cancel(int actor, int ticket)
        {
            if (Actor == actor && Ticket == ticket) { Actor = -1; Amount = 0; }
        }

        internal void NewMaster()
        {
            Actor = -1; Amount = 0; Until = 0f;
            _requests.Clear();
        }
    }
}

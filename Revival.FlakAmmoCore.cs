// Z M1: engine-free gun stock and one-item supply reservation.
using System.Collections.Generic;

namespace NextDayRevival
{
    internal sealed class FlakAmmoStock
    {
        internal const int ShellItem = 2076, BeltItem = 2077;
        internal int Stock, Revision, Actor = -1, Ticket, Amount;
        internal bool Known, Limited;
        // E L1: the emplacement's ready-use rounds, issued once per room.
        internal bool Issued;
        internal float Until;
        // B3: master clock of the garrison's next ready-use top-up (0: unset).
        internal float NextIssue;
        readonly Dictionary<int, int> _requests = new Dictionary<int, int>();

        internal static int Capacity(bool belt) { return belt ? 1000 : 160; }
        internal static int Supply(bool belt) { return belt ? 100 : 1; }
        // E L1: a crew that takes a gun over finds the garrison's ready-use
        // ammunition at the emplacement. Without it a merc-
        // or player-held gun started with an empty stock and laid on every
        // raid without a single shot (6.68.0: "0 round(s) fired"): the depot
        // stands 700-950 m from the pits, beyond Z M3's 150 m supply walk.
        // B3: the lot holds three raids without resupply - 150 shells against
        // ~45 a gun per p90 raid incl. escorts (Z K6b: 134 for three 52-Ks),
        // 600 ZU rounds against ~54 a crewed ZU. The pits hold up to 160.
        internal static int ReadyUse(bool belt) { return belt ? 600 : 150; }
        // B3: the garrison keeps topping the lot up between raids, one belt
        // box or shell at a time, never past ReadyUse: per 30-min raid
        // interval 3 x 24 shells + 3 airdrops (84) = 156 >= the p90 raid
        // (134). The trickle alone is 53 % of it, so the depot chain stays.
        internal static float IssueSeconds(bool belt) { return belt ? 900f : 75f; }
        // B3: under a third of a 52-K raid (one ZU magazine) the crews and
        // the scope warn.
        internal static int Low(bool belt) { return belt ? 100 : 15; }
        /// <summary>0 fine or unknown/garrison, 1 low, 2 empty (cannot fire).</summary>
        internal static int Level(int stock, bool belt) { return stock == 0 ? 2 : stock > 0 && stock <= Low(belt) ? 1 : 0; }

        internal bool Mode(bool limited, bool master)
        {
            if (!master) return false;
            bool changed = !Known || limited != Limited;
            Known = true;
            Limited = limited;
            if (changed) Revision++;
            return changed;
        }

        /// <summary>Issue the ready-use lot once: a finite gun, master only.
        /// Recapture or a new master (Issued arrives with the state) does not
        /// mint a second lot.</summary>
        internal bool Issue(bool belt, bool master)
        {
            if (!master || !Known || !Limited || Issued) return false;
            Issued = true;
            Stock = System.Math.Min(Capacity(belt), Stock + ReadyUse(belt));
            Revision++;
            return true;
        }

        /// <summary>B3: the garrison's top-up of an issued finite gun, master
        /// only, polled from FlakAmmo.Tick. The first call (and the first after
        /// a new master) only starts the clock. Nothing is banked while full.</summary>
        internal bool Replenish(float now, bool belt, bool master)
        {
            if (!master || !Known || !Limited || !Issued) return false;
            if (NextIssue <= 0f || Stock >= ReadyUse(belt)) { NextIssue = now + IssueSeconds(belt); return false; }
            if (now < NextIssue) return false;
            NextIssue = now + IssueSeconds(belt);
            Stock = System.Math.Min(ReadyUse(belt), Stock + Supply(belt));
            Revision++;
            return true;
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
            Actor = -1; Amount = 0; Until = 0f; NextIssue = 0f;
            _requests.Clear();
        }
    }
}

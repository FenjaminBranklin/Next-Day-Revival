// Next Day: Survival - Revival Toolkit
//
// MercPageCore - the Unity-free half of the trader's Mercenaries page (task
// W-UI3). The page itself (Revival.MercPage.cs) reads the roster, the hire
// profiles and each merc's fight brain and hands plain numbers to the
// classes here:
//
//   MercPageNote    the notification filter (all / important / deaths only) and
//               which merc message is which kind
//   MercHire    why a hire card cannot be hired right now (a code, so the
//               page shows a cached text instead of building one per frame)
//   MercPay     the payment state of one contract: due, queued, in flight
//               (the master server has not answered), failed (and why)
//   MercStatus  the one status a row shows, from the merc's own M2/M3 fight
//               loop: in cover, falling back, retreating at low health, ...
//   MercWhere   where he is from the player: metres and a compass point,
//               packed into one int so the text is built only on change
//   MercDue     upkeep due / grace left in tenths of an in-game hour
//
// Nothing here moves money or decides an order: hiring, paying, orders and
// dismissal stay Mercs (Revival.Mercs.cs) and the master server's roster.
// Plain C# 3.0 without a UnityEngine type, so research/merc_page_check.py
// compiles this file UNCHANGED with the .NET 3.5 csc and runs it.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;

namespace NextDayRevival
{
    /// <summary>Merc messages and the player's filter for them.</summary>
    public static class MercPageNote
    {
        // Kind of a message.
        public const int Info = 0;        // order acknowledged, paid, boarding ...
        public const int Important = 1;   // warnings: unpaid, payment failed, whitelist hit, joined
        public const int Death = 2;       // a merc died

        // The filter ([Mercs] Notifications).
        public const int ShowAll = 0;
        public const int ShowImportant = 1;
        public const int ShowDeaths = 2;
        public const int Modes = 3;

        /// <summary>The config text to a filter; anything unknown is "all"
        /// (a typo never silences deaths).</summary>
        public static int Parse(string s)
        {
            if (s == null) return ShowAll;
            s = s.Trim();
            if (string.Equals(s, "Important", StringComparison.OrdinalIgnoreCase)) return ShowImportant;
            if (string.Equals(s, "Deaths", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "DeathsOnly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Death", StringComparison.OrdinalIgnoreCase)) return ShowDeaths;
            return ShowAll;
        }

        public static string Name(int mode)
        {
            return mode == ShowImportant ? "Important" : mode == ShowDeaths ? "Deaths" : "All";
        }

        /// <summary>Does a message of this kind show under this filter? A
        /// reply to the player's own click or key always shows - a filter
        /// must never make a button look dead.</summary>
        public static bool Pass(int mode, int kind, bool reply)
        {
            if (reply) return true;
            if (mode == ShowImportant) return kind >= Important;
            if (mode == ShowDeaths) return kind == Death;
            return true;
        }
    }

    /// <summary>Why a hire card is blocked - the same order of tests as
    /// Mercs.HireBlock (which Mercs.Hire applies again on the click).</summary>
    public static class MercHire
    {
        public const int Ok = 0;
        public const int NoServer = 1;    // the roster link is not live
        public const int Full = 2;        // alive == cap
        public const int Hiring = 3;      // a hire is on the wire
        public const int Paying = 4;      // an upkeep payment is on the wire
        public const int NotLoaded = 5;   // no balance readable
        public const int NoMoney = 6;

        public static int Block(int support, int alive, int cap, bool hiring, bool moneyBusy, int money, int price)
        {
            if (support != 1) return NoServer;
            if (alive >= cap) return Full;
            if (hiring) return Hiring;
            if (moneyBusy) return Paying;
            if (money < 0) return NotLoaded;
            if (money < price) return NoMoney;
            return Ok;
        }
    }

    /// <summary>The payment state of one contract.</summary>
    public static class MercPay
    {
        public const int Idle = 0;        // paid up, nothing on the wire
        public const int Due = 1;         // unpaid, nothing on the wire
        public const int Queued = 2;      // the player asked; another payment is on the wire
        public const int InFlight = 3;    // money left the balance, the master server has not answered
        public const int Failed = 4;      // the last payment failed (money refunded)

        /// <summary>A failure stays on the row this long (s).</summary>
        public const float FailShow = 20f;

        public static int State(bool dead, bool unpaid, bool pending, bool wanted, bool failed, float failAt, float now)
        {
            if (dead) return Idle;
            if (pending) return InFlight;
            if (wanted) return Queued;
            if (failed && now >= failAt && now - failAt < FailShow) return Failed;
            return unpaid ? Due : Idle;
        }

        // Why a hire or payment failed (the master server's answer, or ours).
        public const int ROther = 0;
        public const int RTimeout = 1;    // no answer, the roster did not show it either
        public const int RNoMoney = 2;    // not enough money on this client
        public const int RMismatch = 3;   // err:money-mismatch / err:bad-money
        public const int RPending = 4;    // err:payment-pending after every retry
        public const int RCap = 5;        // err:cap
        public const int RUnknown = 6;    // err:unknown-merc / err:unknown-profile / err:no-profile
        public const int RNotDue = 7;     // err:not-due
        public const int RNoServer = 8;   // the master server has no roster module / no link
        public const int Reasons = 9;

        public static int Reason(string result)
        {
            if (result == null) return ROther;
            if (result == "timeout") return RTimeout;
            if (result == "no-money") return RNoMoney;
            if (result == "err:money-mismatch" || result == "err:bad-money") return RMismatch;
            if (result == "err:payment-pending") return RPending;
            if (result == "err:cap") return RCap;
            if (result == "err:unknown-merc" || result == "err:unknown-profile" || result == "err:no-profile") return RUnknown;
            if (result == "err:not-due") return RNotDue;
            if (result == "no-server" || result == "err:no-server-support") return RNoServer;
            return ROther;
        }
    }

    /// <summary>The one status a merc row shows. Survival first: what the
    /// M2/M3 fight loop is doing to stay alive wins over "in combat".</summary>
    public static class MercStatus
    {
        public const int Ready = 0;       // on his order, no contact
        public const int Dead = 1;
        public const int Deserting = 2;
        public const int Arriving = 3;    // not spawned yet (or respawning)
        public const int Riding = 4;      // in a vehicle seat
        public const int Retreating = 5;  // M3: low health, leaving the fight
        public const int FallingBack = 6; // M3: the team falls back on the owner
        public const int Evading = 7;     // M2: grenade/danger or flee
        public const int Healing = 8;     // M2: patching up in cover
        public const int Reloading = 9;   // M2: reloading in cover
        public const int Flanking = 10;   // M3
        public const int InCover = 11;    // M2: hiding between peeks
        public const int Fighting = 12;   // M2: peek / burst from cover
        public const int ToCover = 13;    // M2: dashing to cover
        public const int Combat = 14;     // contact, the brain not (yet) running
        public const int Gunner = 15;     // on a vehicle gun
        public const int Boarding = 16;   // walking to his seat
        public const int Count = 17;

        // Where he sits (MercSeat): on foot, riding, on the gun, boarding.
        public const int SeatNone = 0, SeatRiding = 1, SeatGunner = 2, SeatBoarding = 3;

        // MercBrain.State / MercBrain.Mode values (Revival.MercFightCore.cs;
        // the check compares them).
        public const byte BrainOff = 0, BrainDash = 1, BrainHide = 2, BrainPeekOut = 3, BrainBurst = 4,
            BrainPeekBack = 5, BrainReloading = 6, BrainHealing = 7, BrainEvade = 8, BrainFlee = 9;
        public const byte ModeNormal = 0, ModeFlanking = 1, ModeFalling = 2, ModeRetreating = 3;

        public static int Of(bool dead, bool deserting, bool spawned, int seat, bool combat, byte state, byte mode)
        {
            if (dead) return Dead;
            if (deserting) return Deserting;
            if (!spawned) return Arriving;
            if (seat == SeatGunner) return Gunner;
            if (seat == SeatRiding) return Riding;
            if (mode == ModeRetreating) return Retreating;
            if (mode == ModeFalling) return FallingBack;
            if (state == BrainEvade || state == BrainFlee) return Evading;
            if (seat == SeatBoarding) return Boarding;
            if (state == BrainHealing) return Healing;
            if (state == BrainReloading) return Reloading;
            if (mode == ModeFlanking && state != BrainOff) return Flanking;
            if (state == BrainHide) return InCover;
            if (state == BrainPeekOut || state == BrainBurst || state == BrainPeekBack) return Fighting;
            if (state == BrainDash) return ToCover;
            if (combat) return Combat;
            return Ready;
        }

        /// <summary>UiTone of a status chip.</summary>
        public static int Tone(int status)
        {
            switch (status)
            {
                case Dead:
                case Deserting:
                case Retreating: return UiTone.Error;
                case FallingBack:
                case Evading:
                case Healing:
                case Combat:
                case Gunner:
                case Fighting: return UiTone.Warning;
                case Arriving: return UiTone.Loading;
                case Ready: return UiTone.Success;
                default: return UiTone.Info;
            }
        }
    }

    /// <summary>Where a merc is from the player, as one int: metres (5 m
    /// steps below 100 m, 25 m beyond) * 8 + a compass point (0 N, 1 NE,
    /// 2 E ... 7 NW; +z is north, +x east). -1 = unknown.</summary>
    public static class MercWhere
    {
        public const float UnitsPerMetre = 2.8f;
        public const int Near = 8;        // below this many metres: "beside you"

        public static int Key(float dx, float dz)
        {
            float d = (float)Math.Sqrt(dx * dx + dz * dz) / UnitsPerMetre;
            int m;
            if (d < Near) m = 0;
            else if (d < 100f) m = (int)(d / 5f + 0.5f) * 5;
            else m = (int)(d / 25f + 0.5f) * 25;
            if (m > 99975) m = 99975;
            return m * 8 + Octant(dx, dz);
        }

        public static int Metres(int key) { return key < 0 ? -1 : key / 8; }
        public static int Point(int key) { return key < 0 ? -1 : key % 8; }

        /// <summary>0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW.</summary>
        public static int Octant(float dx, float dz)
        {
            if (dx == 0f && dz == 0f) return 0;
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;   // 0 = +z (north), 90 = +x (east)
            if (deg < 0.0) deg += 360.0;
            return (int)((deg + 22.5) / 45.0) & 7;
        }
    }

    /// <summary>Upkeep in tenths of an in-game hour.</summary>
    public static class MercDue
    {
        /// <summary>Paid up: tenths until the next bill. Unpaid: tenths of
        /// grace left before he deserts (never below 0).</summary>
        public static int Tenths(double deployed, double paidUntil, int graceHours, bool unpaid)
        {
            double h = unpaid ? graceHours - (deployed - paidUntil) : paidUntil - deployed;
            if (h < 0.0) h = 0.0;
            if (h > 99999.0) h = 99999.0;
            return (int)Math.Round(h * 10.0);
        }

        /// <summary>"13.2" - builds a string: only inside a memo Set.</summary>
        public static string Text(int tenths)
        {
            if (tenths < 0) tenths = 0;
            return UiNum.Of(tenths / 10) + "." + UiNum.Of(tenths % 10);
        }
    }
}

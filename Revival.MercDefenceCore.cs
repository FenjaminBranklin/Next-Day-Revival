// H M3: man air defence - a crewman's way to his post. Arrival, the lease
// gate, the walk-in inside the pit, the stall watchdog (tower roof, open
// ground) and the diagnostic log's clock. Pure C# (no Unity calls):
// research/h_m3_defence_route_check.py compiles it with TowerRoofCore and
// FlakPositionsCore and walks mercs from the C1 roof to the real pits.
// World units throughout (2.8 per metre). C# 3.0, ASCII.
using System;

namespace NextDayRevival
{
    /// <summary>One merc's progress towards his station post (owner side).</summary>
    internal sealed class MercPostTrack
    {
        internal int Post = -1;
        internal object For;                 // the order this track belongs to
        internal float Best, ProgressAt, Since, NextLog, Flat, Rise;
        internal int State, Rescue, Rescues, Refusal = -1;
        internal bool Arrived, Seated, SaidSeat;

        internal void Reset(int post, object order, float now)
        {
            Post = post; For = order;
            Best = float.MaxValue; ProgressAt = Since = now; NextLog = now + MercDefenceCore.LogEvery;
            Flat = Rise = 0f; State = MercDefenceCore.SNone; Rescue = MercDefenceCore.RescueNone;
            Rescues = 0; Refusal = -1; Arrived = Seated = SaidSeat = false;
        }
    }

    internal static class MercDefenceCore
    {
        // The master leases a seat to a body within LeaseReach of its pose,
        // or (an airfield pit) to one standing inside the pit's clear pad:
        // the seat then puts him on it (MercAA.LateFrame writes the root).
        internal const float LeaseReach = 6f;
        internal const float PitReach = FlakPositionsCore.Inner * FlakPositionsCore.K;   // 16.8 u
        internal const float PitRise = 6f;    // pad, wall crown and seat height
        // The owner asks from here. The seat pose lies 2.3 u under the seat
        // node, ~1.6 u over the pad; the old 4 u 3D rule needed a flat
        // 3.6 u the NavMesh around the carriage does not always give.
        internal const float SeatFlat = 5f, SeatRise = 3f;
        internal const float WalkInStall = 1.5f;   // s without progress inside the pit
        internal const float RoofStall = 8f;       // s up on the tower, not climbing, no progress
        internal const float GroundStall = 10f;    // s on open ground: a fresh path
        internal const float Progress = 1f;        // u closer counts as progress
        internal const float LogEvery = 5f;

        internal const int RescueNone = 0, RescueWalkIn = 1, RescueDescend = 2, RescueRepath = 3;

        // MercPostStep branch, for the log (one int write per step).
        internal const int SNone = 0, SWalk = 1, SRequest = 2, SSeated = 3, SWait = 4, SClear = 5,
            SReplace = 6, SRetreat = 7, SGround = 8, SNoPose = 9, SClimb = 10, SWalkIn = 11, SDescend = 12;

        internal static string StateName(int s)
        {
            switch (s)
            {
                case SWalk: return "WALK";
                case SRequest: return "AT POST, asking for the seat";
                case SSeated: return "SEATED";
                case SWait: return "WAIT (post held)";
                case SClear: return "CLEAR enemy crew";
                case SReplace: return "REPLACE (post taken)";
                case SRetreat: return "RETREAT (danger or low health)";
                case SGround: return "GROUND duty";
                case SNoPose: return "NO SEAT POSE";
                case SClimb: return "TOWER STAIRS";
                case SWalkIn: return "WALK-IN to the seat";
                case SDescend: return "DOWN the tower (stall)";
                default: return "not stepped";
            }
        }

        /// <summary>Close enough to ask for the seat.</summary>
        internal static bool AtPost(float flat, float rise)
        {
            return flat <= SeatFlat && Math.Abs(rise) <= SeatRise;
        }

        /// <summary>Master gate (MercAA.OnPacket): 3D distance to the seat pose,
        /// or flat distance to the pit centre (negative: no pit) and rise.</summary>
        internal static bool LeaseNear(float seatSq, float pitFlat, float rise)
        {
            if (seatSq <= LeaseReach * LeaseReach) return true;
            return pitFlat >= 0f && pitFlat <= PitReach && Math.Abs(rise) <= PitRise;
        }

        internal static bool InPit(float pitFlat) { return pitFlat >= 0f && pitFlat <= PitReach; }

        /// <summary>Progress bookkeeping: closer by Progress resets the stall clock.</summary>
        internal static void Note(MercPostTrack t, float flat, float rise, float now)
        {
            t.Flat = flat; t.Rise = rise;
            if (flat < t.Best - Progress || t.Best == float.MaxValue) { t.Best = flat; t.ProgressAt = now; }
        }

        /// <summary>What to do for a crewman not yet at his post. up: on the
        /// tower (roof, cab, catwalk); pitFlat: flat distance to his pit
        /// centre (negative without a pit).</summary>
        internal static int Rescue(MercPostTrack t, float now, float pitFlat, bool up)
        {
            float still = now - t.ProgressAt;
            if (InPit(pitFlat) && still >= WalkInStall) return RescueWalkIn;
            if (up && still >= RoofStall) { t.ProgressAt = now; return RescueDescend; }
            if (!up && still >= GroundStall) { t.ProgressAt = now; return RescueRepath; }
            return RescueNone;
        }

        /// <summary>The 5 s diagnostic clock (owner tick, at most one line per
        /// merc per LogEvery).</summary>
        internal static bool LogDue(MercPostTrack t, float now)
        {
            if (now < t.NextLog) return false;
            t.NextLog = now + LogEvery;
            return true;
        }

        // An extra man keeps his order unless it is an air defence post the
        // plan hands out (a gun or the radar); vehicle and mortar seats stay.
        internal static bool KeepsOrder(bool station, bool vehicle, int post)
        {
            return !station || vehicle || post < 0 || post >= 7;
        }
    }
}

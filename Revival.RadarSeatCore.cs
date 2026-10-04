// Next Day: Survival - Revival Toolkit
//
// H T2: the HQ radar operator's seat anchor. Pure rules, C# 3.0, no Unity
// types: RadarOperator.Hold (Revival.TowerRadar.cs) applies them every late
// frame, research/h_t2_tower_nav_check.py compiles and proves them.
//
// The seated clip floats the body SeatDrop above its root, so a seated
// man's root lies under the cab floor (as on the flak seats). Up to 6.70 the
// seat was written twice a second only: the game's standing clip played
// from that sunken root in between (the jitter), and a man who went down
// kept it - his wounded body lay under the floor, out of every bullet's
// reach. Now the seat owns him every frame he is up, and the moment he is
// not, his root goes back onto the floor anchor; a downed man found under
// the floor later is put back there too.
namespace NextDayRevival
{
    internal static class RadarSeatCore
    {
        internal const float FullEvery = 0.2f;     // s: agent park, AI pause, rifle, floor fallback
        internal const float FloorSlack = 0.15f;   // u a root not on the seat may lie under the floor anchor
        internal const float SeatSlack = 0.05f;    // u the held root may stray before it is written

        internal const int Keep = 0;   // not the seat's business this frame
        internal const int Hold = 1;   // on the seat: pose, anchor, rotation
        internal const int Lift = 2;   // off the seat: the root onto the floor anchor, once

        /// <summary>The frame's action. up: standing (alive, not wounded);
        /// alive: not dead; held: the seat held him on its last frame; full:
        /// a full pass (the floor fallback runs on those).</summary>
        internal static int Decide(bool up, bool alive, bool held, bool full, float rootY, float floorY)
        {
            if (up) return Hold;
            if (held) return Lift;
            if (alive && full && rootY < floorY - FloorSlack) return Lift;
            return Keep;
        }

        /// <summary>The held root's height: the chair's top less the measured
        /// drop when seated, the anchor's floor point when standing.</summary>
        internal static float RootY(float floorY, bool sit, float seatTop, float drop)
        {
            return sit ? seatTop + 0.05f - drop : floorY;
        }

        /// <summary>Write the root this frame? Only past the slack, so a
        /// held man is not dirtied every frame for nothing.</summary>
        internal static bool Move(float awaySq) { return awaySq > SeatSlack * SeatSlack; }
    }
}

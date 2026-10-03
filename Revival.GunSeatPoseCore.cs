// G C1: a manned AA seat owns the man's skeleton. Unity-free so the offline
// check (research/gun_seat_pose_check.py) compiles this exact file.
//
// The flicker: MercAA.LateFrame turned the legacy Animation off once at park
// and sampled the seated clip for ONE post every 20 ms. Between samples the
// pose was only held if nobody else wrote the bones - but the native aim IK
// (NPC_AI2.LookAtIkController, NpcWar.DriveAim, MercMoveShootPose) and a
// re-enabled Animation (SetPlayVisualizationValue(true), AirNpcVisual.Exit,
// NpcDistance unfreeze) do. The frame alternated seated / standing-aiming.
// Now every foreign writer is switched off again each frame and the seat
// samples at once whenever one had been on.
namespace NextDayRevival
{
    internal static class GunSeatPoseCore
    {
        // Foreign writers found on a seated man this frame.
        internal const int AnimationOn = 1, AnimatorOn = 2, AimIk = 4, Gait = 8;

        // Rendered pose classes for the switch meter.
        internal const int PoseNone = 0, PoseSeat = 1, PoseForeign = 2;

        /// <summary>Sample the seated clip this frame: on the budget turn, at
        /// once after a foreign writer moved the bones, and until the first
        /// sample of this park succeeded.</summary>
        internal static bool Sample(bool turn, int foreign, bool seated)
        {
            return turn || foreign != 0 || !seated;
        }

        /// <summary>The pose the frame shows after the seat ran: the seat's as
        /// soon as it sampled once and took back every foreign writer, which
        /// it re-samples over in the same frame.</summary>
        internal static int Shown(bool seated, int foreign, bool sampledNow)
        {
            if (!seated) return PoseForeign;
            return foreign == 0 || sampledNow ? PoseSeat : PoseForeign;
        }

        internal static string Writers(int foreign)
        {
            if (foreign == 0) return "none";
            string s = "";
            if ((foreign & AnimationOn) != 0) s += "animation ";
            if ((foreign & AnimatorOn) != 0) s += "animator ";
            if ((foreign & AimIk) != 0) s += "aim-ik ";
            if ((foreign & Gait) != 0) s += "move-shoot ";
            return s.TrimEnd();
        }
    }

    /// <summary>Counts changes of the rendered pose class over a sliding
    /// one-second window. Fixed arrays, no allocation per sample.</summary>
    internal sealed class PoseSwitchMeter
    {
        const int Slots = 64;
        readonly float[] _at = new float[Slots];
        int _head, _count, _last = GunSeatPoseCore.PoseNone;
        internal int Total;

        internal void Reset() { _head = _count = 0; _last = GunSeatPoseCore.PoseNone; }

        /// <summary>One rendered frame. The first pose after a reset is the
        /// start, not a switch.</summary>
        internal void Record(int pose, float now)
        {
            if (_last != GunSeatPoseCore.PoseNone && pose != _last)
            {
                _at[_head] = now; _head = (_head + 1) % Slots;
                if (_count < Slots) _count++;
                Total++;
            }
            _last = pose;
        }

        /// <summary>Switches inside the last second before now.</summary>
        internal int PerSecond(float now)
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                int k = (_head - 1 - i + Slots) % Slots;
                if (now - _at[k] <= 1f) n++; else break;
            }
            return n;
        }
    }
}

// Z H0a: time-based lifecycle, also compiled by offline_start_check.py.
// C# 3.0, ASCII. No Unity dependency or steady-state allocations.
using System;

namespace NextDayRevival
{
    internal enum OfflinePhase { Bootstrap, Loading, Waiting, Capture, Complete, Failed }

    internal sealed class OfflineRunClock
    {
        internal OfflinePhase Phase = OfflinePhase.Bootstrap;
        internal readonly double Duration, Deadline;
        internal double LoadedAt = -1, CaptureAt = -1;
        internal long Frames;
        internal double FrameTotal, FrameMax;

        internal OfflineRunClock(double started, double duration, double timeout)
        {
            if (duration < 1 || duration > 600 || timeout < duration + 30 || timeout > 1800
                || double.IsNaN(started) || double.IsNaN(duration) || double.IsNaN(timeout)
                || double.IsInfinity(started) || double.IsInfinity(duration) || double.IsInfinity(timeout))
                throw new ArgumentException("Invalid offline run times.");
            Duration = duration;
            Deadline = started + timeout;
        }

        internal void Joined() { if (Phase == OfflinePhase.Bootstrap) Phase = OfflinePhase.Loading; }

        internal void Loaded(double now)
        {
            if (Phase != OfflinePhase.Loading) return;
            LoadedAt = now;
            Phase = OfflinePhase.Waiting;
        }

        internal void Step(double now, double frameSeconds)
        {
            if (Phase == OfflinePhase.Complete || Phase == OfflinePhase.Failed) return;
            if (now >= Deadline) { Phase = OfflinePhase.Failed; return; }
            if (Phase != OfflinePhase.Waiting) return;
            Frames++;
            FrameTotal += frameSeconds;
            if (frameSeconds > FrameMax) FrameMax = frameSeconds;
            if (now - LoadedAt >= Duration) { CaptureAt = now; Phase = OfflinePhase.Capture; }
        }

        internal void Finish() { if (Phase == OfflinePhase.Capture) Phase = OfflinePhase.Complete; }
    }
}

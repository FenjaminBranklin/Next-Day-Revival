// Owner-relative catch-up policy. Arithmetic only; C# 3.0, ASCII.
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercCatchUp
    {
        internal const float SampleEvery = 0.5f, StartGap = 56f, StopGap = 16.8f;
        internal const float SprintFloor = 18f, SprintCeiling = 22.4f, SpeedMargin = 4.2f;
        internal const float Hop = 24f, Progress = 5.6f, BoundWait = 3f;
        internal bool Active, PaceApplied;
        internal Vector3 Goal;
        internal float Speed = SprintFloor, NextSample, NextBound;
        object _order;
        Vector3 _owner, _progress;
        float _sampleAt = -1f;

        internal void Observe(object order, bool enabled, byte role, int slot, bool medic,
            Vector3 me, Vector3 owner, Vector3 front, float now)
        {
            if (!enabled)
            { Active = false; _order = null; _sampleAt = -1f; NextSample = 0f; return; }
            bool changed = _order != order;
            if (!changed && now < NextSample) return;
            _order = order; NextSample = now + SampleEvery;
            front = MercRole.Front(front);
            Goal = MercRole.Slot(medic ? MercRole.Assault : role, owner, front, slot, 0);
            if (medic) Goal = owner - front * 16.8f
                + new Vector3(front.z, 0f, -front.x) * ((slot & 1) == 0 ? -8.4f : 8.4f);
            float dt = now - _sampleAt;
            Vector3 travel = owner - _owner; travel.y = 0f;
            // Relogs, warps and vehicle speeds cannot grant an unbounded pace.
            float pace = _sampleAt >= 0f && dt > 0.1f && dt < 2f ? travel.magnitude / dt : 0f;
            Speed = Mathf.Clamp(pace + SpeedMargin, SprintFloor, SprintCeiling);
            _sampleAt = now; _owner = owner;
            Vector3 to = Goal - me; to.y = 0f;
            float gap = to.magnitude;
            bool was = Active && !changed;
            bool displaced = medic || role != MercRole.Marksman || !MercRole.InPosition(me, owner, front);
            Active = was ? gap > StopGap : displaced && gap > StartGap;
            if (!Active) return;
            if (!was) { _progress = me; NextBound = now; }
            else if (Vector3.Dot(me - _progress, gap > 0.01f ? to / gap : front) >= Progress)
            { _progress = me; NextBound = now + BoundWait + (slot % 6) * 0.12f; }
        }

        internal Vector3 Bound(Vector3 me, CoverPick pick, bool fresh)
        {
            Vector3 to = Goal - me; to.y = 0f;
            float gap = to.magnitude;
            if (gap < 0.01f) return me;
            Vector3 dir = to / gap;
            Vector3 cover = pick.Point.Pos - me; cover.y = 0f;
            // Reuse M1's confirmed local shelter only when it gains ground.
            if (fresh && pick.Found && pick.Confirmed && cover.sqrMagnitude <= Hop * Hop
                && Vector3.Dot(cover, dir) >= Progress && (Goal - pick.Point.Pos).sqrMagnitude < to.sqrMagnitude)
                return pick.Point.Pos;
            return me + dir * Mathf.Min(Hop, gap);
        }
    }
}

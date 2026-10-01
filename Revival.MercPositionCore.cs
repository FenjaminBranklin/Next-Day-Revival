// Z K1b: bounded firing-position search. No Unity services in this core.
using UnityEngine;

namespace NextDayRevival
{
    internal interface IMercPositionWorld
    {
        bool Reach(Vector3 from, Vector3 near, out Vector3 at);
        bool Lane(Vector3 muzzle, Vector3 target);
    }

    internal sealed class MercPosition
    {
        internal const float Every = 0.1f, Min = 5.6f, Max = 22.4f;
        internal const int Capacity = 18;
        readonly Vector3[] _points = new Vector3[Capacity];
        readonly float[] _rank = new float[Capacity];
        int _count, _cursor;
        Vector3 _origin, _threat, _lastMe;
        float _started, _progress, _retry;
        internal float NextQuery;
        internal bool Searching, Moving, Opened, Clear;
        internal Vector3 Dest;

        internal void Reset()
        {
            Searching = Moving = Opened = Clear = false;
            _count = _cursor = 0; _retry = 0f;
        }

        internal void Begin(Vector3 me, Vector3 threat, ref CoverPick sense, ref CoverPick held,
            Vector3 leash, float radius, float now)
        {
            Reset();
            _origin = _lastMe = me; _threat = threat; _started = _progress = now;
            Hints(ref sense, leash, radius); Hints(ref held, leash, radius);
            Vector3 forward = threat - me; forward.y = 0f;
            float length = forward.magnitude;
            forward = length > 0.01f ? forward / length : new Vector3(0f, 0f, 1f);
            Vector3 side = new Vector3(forward.z, 0f, -forward.x);
            for (int i = 0; i < 3; i++)
            {
                float distance = i == 0 ? Min : (i == 1 ? 11.2f : Max);
                Offer(me + side * distance, false, leash, radius);
                Offer(me - side * distance, false, leash, radius);
                // Back-diagonals skirt a corner instead of crossing its wall.
                Offer(me + (side - forward) * (distance * 0.70710678f), false, leash, radius);
                Offer(me + (-side - forward) * (distance * 0.70710678f), false, leash, radius);
            }
            Searching = _count > 0;
        }

        void Hints(ref CoverPick pick, Vector3 leash, float radius)
        {
            if (!pick.Found) return;
            if ((pick.Point.Peek & CoverPeek.Left) != 0) Offer(pick.Point.PeekL, true, leash, radius);
            if ((pick.Point.Peek & CoverPeek.Right) != 0) Offer(pick.Point.PeekR, true, leash, radius);
            if ((pick.Point.Peek & CoverPeek.Over) != 0) Offer(pick.Point.Pos, true, leash, radius);
        }

        void Offer(Vector3 point, bool cover, Vector3 leash, float radius)
        {
            float distance = Flat(point - _origin);
            if (distance < Min - 0.01f || distance > Max + 0.01f || _count >= Capacity
                || (radius > 0f && Flat(point - leash) > radius)) return;
            for (int i = 0; i < _count; i++) if (Flat(point - _points[i]) < 0.5f) return;
            // Nearest first; shelter wins within 0.8 m, never a long detour.
            float rank = distance - (cover ? 2.24f : 0f);
            int at = _count++;
            while (at > 0 && _rank[at - 1] > rank + 0.01f)
            { _rank[at] = _rank[at - 1]; _points[at] = _points[at - 1]; at--; }
            _rank[at] = rank; _points[at] = point;
        }

        internal bool Due(float now) { return now >= NextQuery && now >= _retry; }

        // One job per call: one candidate, or the current muzzle while moving.
        // Prospective lanes NEVER grant permission to emit a round (K1a does).
        internal void Probe(IMercPositionWorld world, Vector3 me, Vector3 muzzleOffset, Vector3 endpoint,
            Vector3 leash, float radius, float now)
        {
            NextQuery = now + Every; Opened = false;
            if (Flat(endpoint - _threat) > 8.4f) { Reset(); return; }
            if (Moving)
            {
                if (Flat(me - _lastMe) > 0.5f) { _lastMe = me; _progress = now; }
                Clear = world.Lane(me + muzzleOffset, endpoint);
                if (Clear && Flat(me - Dest) < 1f)
                { Moving = false; Opened = true; return; }
                if (now - _progress >= 0.6f || now - _started > 3f || Flat(me - Dest) < 1f
                    || (radius > 0f && Flat(Dest - leash) > radius))
                { Moving = Clear = false; Searching = _cursor < _count; }
                return;
            }
            if (!Searching) return;
            if (Flat(me - _origin) > 5.6f)
            { Reset(); return; }
            Vector3 at;
            Vector3 point = _points[_cursor++];
            if (world.Reach(me, point, out at) && Flat(at - _origin) >= Min - 0.1f
                && Flat(at - _origin) <= Max + 0.1f && (radius <= 0f || Flat(at - leash) <= radius)
                && world.Lane(at + muzzleOffset, endpoint))
            {
                Dest = at; Moving = true; Searching = false;
                _started = _progress = now; _lastMe = me;
            }
            else if (_cursor >= _count) { Searching = false; _retry = now + 0.5f; }
        }

        internal static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }
    }

    internal sealed class MercPositionBudget
    {
        int _frame = -1;
        internal bool Take(int frame)
        { if (_frame == frame) return false; _frame = frame; return true; }
    }
}

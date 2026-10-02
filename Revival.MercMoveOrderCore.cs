// A point move uses the existing M1 field and M2/M3 fight loop. C# 3.0.
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercMovePlan
    {
        internal const float Radius = 42f, Spread = 7f, Arrive = 2f, Hop = 24f;
        internal MercOrder For;
        internal CoverPick Pick;
        internal Vector3 Goal;
        internal float NextQuery, NextWant, NextHold;
        internal bool Grounded, Arrived;
        bool _haveRoute;
        Vector3 _route, _routeGoal;
        int _hits;

        internal void Reset(CoverField field, int id)
        {
            field.ReleaseOrder(id);
            For = null; Pick = new CoverPick(); Grounded = false; Arrived = false; _haveRoute = false;
            NextQuery = NextWant = NextHold = 0f;
        }

        internal void Begin(MercOrder order, CoverField field, int id, int hits)
        {
            if (For == order) return;
            Reset(field, id); For = order; _hits = hits;
            // Open ground still has independent destinations. Cover replaces
            // these slots as the shared field finishes mapping the area.
            float angle = order.K * Mathf.PI * 2f / Mathf.Max(1, order.N);
            Goal = order.Centre + (order.N <= 1 ? Vector3.zero
                : new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * 11.2f);
        }

        internal void Hit(int hits, Vector3 me, CoverField field, int id, float now)
        {
            if (hits == _hits) return;
            _hits = hits;
            if (!Pick.Found || Flat(me - Goal) > 8.4f) return;
            field.MarkBad(ref Pick, now, 5f);
            Pick = new CoverPick(); field.ReleaseOrder(id); NextQuery = 0f;
        }

        internal bool Select(CoverField field, int id, Vector3[] threats, float[] weights, int count, float now)
        {
            NextQuery = now + 0.5f;
            CoverPick pick;
            if (!field.Best(For.Centre, threats, weights, count, Radius,
                For.Centre, Radius, id, now, out pick)) return false;
            Pick = pick; Goal = pick.Point.Pos; Grounded = true; Arrived = false; _haveRoute = false;
            field.ReserveOrder(id, Goal, now + 2f, now);
            return true;
        }

        internal Vector3 Waypoint(Vector3 me)
        {
            if (_haveRoute && Flat(_route - me) > Arrive && Flat(_routeGoal - Goal) < 0.1f) return _route;
            Vector3 to = Goal - me; to.y = 0f;
            float distance = to.magnitude;
            _route = distance <= Hop ? Goal : me + to * (Hop / distance);
            _routeGoal = Goal; _haveRoute = true;
            return _route;
        }

        internal bool Travelling(Vector3 me) { return Flat(Goal - me) > Arrive; }
        static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }
    }
}

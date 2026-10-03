// c-m2-close-the-distance - the pure part (docs/ai/tasks/c-m2-close-the-distance.md).
// It knows Vector3 and Mathf and nothing of the game, so
// research/merc_close_distance_check.py compiles this file UNCHANGED.
//
//   GOAL      MercCloseIn.Goal: a merc who sees a target past his fire range
//             (MercWeaponReach) and is not under fire walks up to it before
//             he opens deliberate fire - as far as his order lets him: a
//             circle round the owner (FOLLOW), the STAY point or the patrol
//             route. ATTACK closes on its own advance, PERIMETER on its
//             chase. No point of the circle brings the target into range:
//             he keeps his post and holds; incoming fire is still answered.
//   ANCHOR    MercCloseIn.RouteAnchor: the route point nearest him.
//   STATE     MercCloseRun: one merc's walk up, with the M1 cover pick on the
//             way (at most one query per CoverEvery seconds).
//
// No allocation. Units: game units (~2.8 per metre), seconds. C# 3.0, ASCII only.
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercCloseIn
    {
        internal const float Stop = 0.85f;        // walk until the target is this share of his fire range away
        internal const float Reach = 0.95f;       // a goal must bring it within this share (height, scatter)
        internal const float FollowLeash = 70f;   // 25 m round the owner (MercRole.MayFight: 75 calm, 120 fighting)
        internal const float StayLeash = 52f;     // 19 m round the STAY point (MercStayLeash 60)
        internal const float PatrolLeash = 80f;   // 29 m off the route (MercPatrolLeash 90)
        internal const float SeenSeconds = 3f;    // a target unseen longer is no reason to move
        internal const float Arrive = 4f;

        /// <summary>Where he walks to fire (flat; y his own): on the line to
        /// the target, Stop x fire short of it, pulled into the anchor circle.
        /// False: in range already on the flat, or no point of the circle that
        /// brings the target within Reach x fire (he holds his post).</summary>
        internal static bool Goal(Vector3 me, Vector3 target, float fire, Vector3 anchor, float leash, out Vector3 goal)
        {
            goal = me;
            float dx = target.x - me.x, dz = target.z - me.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            float stop = fire * Stop;
            if (d <= stop || d < 0.01f) return false;
            float t = (d - stop) / d;
            float wx = me.x + dx * t, wz = me.z + dz * t;
            float ax = wx - anchor.x, az = wz - anchor.z;
            float a = Mathf.Sqrt(ax * ax + az * az);
            if (a > leash && a > 0.01f) { wx = anchor.x + ax / a * leash; wz = anchor.z + az / a * leash; }
            float gx = target.x - wx, gz = target.z - wz;
            float reach = fire * Reach;
            if (gx * gx + gz * gz > reach * reach) return false;
            goal = new Vector3(wx, me.y, wz);
            return true;
        }

        /// <summary>May he fire at the target from p (flat, Reach x fire)?</summary>
        internal static bool InReach(Vector3 p, Vector3 target, float fire)
        {
            float gx = target.x - p.x, gz = target.z - p.z, reach = fire * Reach;
            return gx * gx + gz * gz <= reach * reach;
        }

        /// <summary>The point of the closed route (as RouteDistance walks it)
        /// nearest p; p itself when there is no route.</summary>
        internal static Vector3 RouteAnchor(Vector3[] points, Vector3 p)
        {
            int n = points == null ? 0 : points.Length;
            if (n == 0) return p;
            if (n == 1) return points[0];
            Vector3 best = points[0];
            float bestSqr = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % n];
                float abx = b.x - a.x, abz = b.z - a.z, apx = p.x - a.x, apz = p.z - a.z;
                float len = abx * abx + abz * abz;
                float t = len < 0.01f ? 0f : Mathf.Clamp01((apx * abx + apz * abz) / len);
                float cx = a.x + abx * t, cz = a.z + abz * t;
                float ex = p.x - cx, ez = p.z - cz, sqr = ex * ex + ez * ez;
                if (sqr < bestSqr) { bestSqr = sqr; best = new Vector3(cx, a.y + (b.y - a.y) * t, cz); }
            }
            return best;
        }
    }

    /// <summary>One merc's walk up to his firing distance.</summary>
    internal sealed class MercCloseRun
    {
        internal const float CoverEvery = 1.5f;  // M1 query at most this often per merc (and one a frame for all)
        internal bool Active, Covered;
        internal Vector3 Cover;
        internal float NextQuery;
        internal int Walks;                      // F8 / log: how often he closed in

        internal void Begin(float now)
        {
            if (Active) return;
            Active = true; Covered = false; NextQuery = now; Walks++;
        }

        internal void End() { Active = false; Covered = false; }
    }
}

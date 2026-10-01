// Next Day: Survival - Revival Toolkit
//
// Z T1a: regular north outside stairs are the sole route to the roof.
// Coordinates are metres in TowerRadar's C1 frame (world units = m * 2.8).
// Existing three flights reach the main roof at 9 m. A runtime upper flight
// and guarded crossover above the cab's north rail reach the 15.35 m posts.
// C# 3.0, ASCII. Pure layout/routing is compiled by tower_roof_check.py.
using UnityEngine;

namespace NextDayRevival
{
    internal static class TowerRoofCore
    {
        // ------------------------------------------------------------ layout

        internal const float RoofY = 15.35f;          // top of the cab roof (C1_COL_487)
        internal const float RailTop = 16.35f;        // top of its rail (C1_COL_488..491)
        internal const float RoofMinX = 4.30f, RoofMaxX = 11.95f;     // inside the rail
        internal const float RoofMinZ = -4.05f, RoofMaxZ = 4.05f;

        internal const float StepSpeed = 2.4f;        // brisk stair walk; ~20 s end to end
        internal const float StuckSeconds = 2f, MaxSeconds = 24f;
        internal const float FootArrive = 0.35f, TopArrive = 0.35f, PostArrive = 1.0f;
        internal const int PathMax = 24;
        internal const float CapsuleRadius = 0.27f, CapsuleHeight = 1.8f;
        // Feet clear a ramp tangent by r * (sec(36.87 degrees) - 1), plus margin.
        internal const float FootLift = 0.12f;
        internal static readonly Vector3[] Stair = {
            new Vector3(-10.9f, 0f, 9.65f), new Vector3(-10.5f, 0f, 9.65f),
            new Vector3(-6.5f, 3f, 9.65f), new Vector3(-5.1f, 3f, 9.65f),
            new Vector3(-1.1f, 6f, 9.65f), new Vector3(0.3f, 6f, 9.65f),
            new Vector3(4.3f, 9f, 9.65f), new Vector3(5f, 9f, 9.65f),
            new Vector3(5f, 9.4f, 9.05f), new Vector3(5f, 9.4f, 8.35f),
            new Vector3(5f, 9f, 7.7f), new Vector3(5f, 9f, 7.3f),
            new Vector3(-2.6f, 9f, 7.3f), new Vector3(-2.6f, 9f, 5.7f),
            new Vector3(-2f, 9f, 5.7f), new Vector3(7.35f, 16.5f, 5.7f), new Vector3(8f, 16.5f, 5.7f),
            new Vector3(8f, 16.5f, 3.7f), new Vector3(8f, 15.35f, 2.15f)
        };
        // Runtime geometry: slope, top landing, crossover, short descent.
        internal const int UpperFirst = 14;
        // who is "up": on the roof, or heading for a point on / beside it
        internal const float UpBelow = 1.3f;          // the roof counts from RoofY - this
        internal const float GoalBelow = 1.5f, GoalAbove = 3.0f;
        internal const float GoalReach = 10.0f;       // FOLLOW slots lie up to 10 m behind the owner

        // THE POSTS: five places behind a sandbag wall, one per merc slot,
        // looking out over the wall (face). Sandbags 0.9 m high, 0.5 m thick.
        internal const float BagH = 0.9f;
        internal static readonly float[] PostX = { 9.6f, 6.2f, 10.5f, 8.0f, 5.75f };
        internal static readonly float[] PostZ = { 2.6f, 2.6f, 1.0f, -2.6f, 0.0f };
        internal static readonly float[] FaceX = { 0f, 0f, 1f, 0f, -1f };
        internal static readonly float[] FaceZ = { 1f, 1f, 0f, -1f, 0f };
        internal static readonly float[] BagX = { 9.6f, 6.2f, 11.45f, 8.0f, 4.8f };
        internal static readonly float[] BagZ = { 3.55f, 3.55f, 1.0f, -3.55f, 0.0f };
        internal static readonly float[] BagSX = { 2.2f, 2.2f, 0.5f, 2.2f, 0.5f };
        internal static readonly float[] BagSZ = { 0.5f, 0.5f, 2.2f, 0.5f, 2.0f };

        internal static int Posts { get { return PostX.Length; } }

        internal static Vector3 Post(int slot)
        {
            int i = Wrap(slot);
            // A second row shares the existing walls, never the same body spot.
            float offset = slot >= Posts ? 0.65f : 0f;
            return new Vector3(PostX[i] + FaceZ[i] * offset, RoofY, PostZ[i] - FaceX[i] * offset);
        }

        internal static Vector3 Face(int slot)
        {
            int i = Wrap(slot);
            return new Vector3(FaceX[i], 0f, FaceZ[i]);
        }

        static int Wrap(int slot)
        {
            int n = PostX.Length;
            int i = slot % n;
            return i < 0 ? i + n : i;
        }

        internal static Vector3 Foot(float footY) { return new Vector3(Stair[0].x, footY, Stair[0].z); }
        internal static Vector3 Exit() { return Stair[Stair.Length - 1]; }

        // -------------------------------------------------------------- legs

        internal const int LegNone = 0;       // not the roof's business
        internal const int LegWalk = 1;       // walk to leg (exactly there, a NavMesh spot)
        internal const int LegHold = 2;       // at his post: hold, leg = the direction he looks
        internal const int LegClimbUp = 3;    // at the foot: climb up
        internal const int LegClimbDown = 4;  // at the top: climb down

        /// <summary>Standing on the roof (inside the rail, at roof height).</summary>
        internal static bool OnRoof(Vector3 l)
        {
            return l.y > RoofY - UpBelow && l.y < RoofY + GoalAbove
                && l.x > RoofMinX - 0.3f && l.x < RoofMaxX + 0.3f && l.z > RoofMinZ - 0.3f && l.z < RoofMaxZ + 0.3f;
        }

        /// <summary>A goal meant for the roof: at roof height over it or
        /// beside it (a FOLLOW slot behind an owner standing up there).</summary>
        internal static bool GoalUp(Vector3 l)
        {
            if (l.y < RoofY - GoalBelow || l.y > RoofY + GoalAbove) return false;
            float dx = l.x < RoofMinX ? RoofMinX - l.x : (l.x > RoofMaxX ? l.x - RoofMaxX : 0f);
            float dz = l.z < RoofMinZ ? RoofMinZ - l.z : (l.z > RoofMaxZ ? l.z - RoofMaxZ : 0f);
            return dx * dx + dz * dz <= GoalReach * GoalReach;
        }

        /// <summary>Man and goal on different sides of the stair: a flat
        /// arrival check must not count him there.</summary>
        internal static bool Split(Vector3 man, Vector3 goal) { return OnRoof(man) != GoalUp(goal); }

        static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>The leg of a merc's move from man towards goal (all in
        /// the tower frame). foot and exit are the NavMesh spots he walks to
        /// at the stair's foot and on the roof beside its top (Foot() and
        /// Exit() where the NavMesh has nothing better).</summary>
        internal static int Leg(Vector3 man, Vector3 goal, int slot, Vector3 foot, Vector3 exit, out Vector3 leg)
        {
            leg = goal;
            bool manUp = OnRoof(man), goalUp = GoalUp(goal);
            if (!manUp && !goalUp) return LegNone;
            if (manUp && goalUp)
            {
                leg = Post(slot);
                if (Flat(man, leg) <= PostArrive) { leg = Face(slot); return LegHold; }
                return LegWalk;
            }
            if (goalUp)
            {
                leg = foot;
                if (Flat(man, leg) <= FootArrive && man.y < foot.y + 1.5f) return LegClimbUp;
                return LegWalk;
            }
            leg = exit;
            if (Flat(man, leg) <= TopArrive) return LegClimbDown;
            return LegWalk;
        }

        // -------------------------------------------------------------- climb

        /// <summary>The stair walk as a polyline from where he stands: up = foot,
        /// the stair, over the rail, down onto the roof; down the same way
        /// back (foot and exit as in Leg). Returns the point count (at most
        /// PathMax).</summary>
        internal static int Path(bool up, Vector3 start, Vector3 foot, Vector3 exit, Vector3[] pts)
        {
            int n = 0;
            pts[n++] = start;
            if (up) {
                pts[n++] = foot;
                for (int i = 1; i < Stair.Length; i++) pts[n++] = Stair[i];
                pts[n++] = exit;
            } else {
                pts[n++] = exit;
                for (int i = Stair.Length - 1; i > 0; i--) pts[n++] = Stair[i];
                pts[n++] = foot;
            }
            return n;
        }

        static float SegTime(Vector3 a, Vector3 b)
        {
            float flat = Flat(a, b), dy = Mathf.Abs(b.y - a.y);
            return Mathf.Sqrt(flat * flat + dy * dy) / StepSpeed;
        }

        internal static float Duration(Vector3[] pts, int n)
        {
            float t = 0f;
            for (int i = 1; i < n; i++) t += SegTime(pts[i - 1], pts[i]);
            return t;
        }

        /// <summary>Where he is t seconds into the climb.</summary>
        internal static Vector3 At(Vector3[] pts, int n, float t)
        {
            if (n <= 0) return Vector3.zero;
            if (t <= 0f) return pts[0];
            for (int i = 1; i < n; i++)
            {
                float s = SegTime(pts[i - 1], pts[i]);
                if (t <= s)
                {
                    float k = s <= 1e-5f ? 1f : t / s;
                    Vector3 a = pts[i - 1], b = pts[i];
                    return new Vector3(a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k, a.z + (b.z - a.z) * k);
                }
                t -= s;
            }
            return pts[n - 1];
        }

        // ------------------------------------------------ bounded live walk

        internal struct Walk
        {
            internal int Next;
            internal float Began, ProgressAt, Best;
        }

        internal const int Walking = 0, Hop = 1, Finished = 2, Fallback = 3;

        static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static void Begin(ref Walk w, Vector3[] pts, int n, float now)
        {
            w.Next = 1;
            w.Began = w.ProgressAt = now;
            w.Best = n > 1 ? Distance(pts[0], pts[1]) : 0f;
        }

        internal static Vector3 Advance(Vector3 at, Vector3 target, float dt)
        {
            float d = Distance(at, target);
            float k = d <= 0.001f ? 1f : Mathf.Min(1f, StepSpeed * Mathf.Min(dt, 0.1f) / d);
            return new Vector3(at.x + (target.x - at.x) * k,
                at.y + (target.y - at.y) * k, at.z + (target.z - at.z) * k);
        }

        // Progress is measured against the ACTUAL body, not the intended move.
        // Recovery ignores the blocked sample only for explicitly allowed hops.
        internal static int Step(ref Walk w, Vector3[] pts, int n, Vector3 at,
            float now, float dt, bool clear, out Vector3 next)
        {
            next = at;
            if (n < 2) return Finished;
            if (now - w.Began >= MaxSeconds) { next = pts[n - 1]; return Fallback; }
            if (w.Next >= n) { next = pts[n - 1]; return Finished; }
            float d = Distance(at, pts[w.Next]);
            if (d <= 0.025f) {
                if (++w.Next >= n) { next = pts[n - 1]; return Finished; }
                w.ProgressAt = now;
                w.Best = Distance(at, pts[w.Next]);
                d = w.Best;
            }
            if (d < w.Best - 0.03f) { w.Best = d; w.ProgressAt = now; }
            if (now - w.ProgressAt >= StuckSeconds) {
                next = pts[w.Next++];
                w.ProgressAt = now;
                w.Best = w.Next < n ? Distance(next, pts[w.Next]) : 0f;
                return w.Next >= n ? Finished : Hop;
            }
            if (clear) next = Advance(at, pts[w.Next], dt);
            return Walking;
        }

    }
}

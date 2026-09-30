// Next Day: Survival - Revival Toolkit
//
// Y B1 TOWER ROOF, the pure half (no scene, no physics): the layout of the
// outside ladder and the roof posts in the tower C1's frame, the merc's leg
// decision (walk to the foot, climb, walk to his post, hold) and the climb
// path. Revival.TowerRoof.cs puts it into the world; research/
// tower_roof_check.py compiles this file and checks the layout against the
// C1 collider boxes (assets/airfield/c1/c1_colliders.json).
//
// THE FRAME: metres, x east, z north, y up from the tower's base - the frame
// of TowerRadar.TowerPoint and of c1_colliders.json (divided by 2.8).
//
//   cab roof   x 4.10..12.15, z -4.25..4.25, top 15.35; its 1 m rail stands
//              on the edge (inside x 4.30..11.95, z -4.05..4.05)
//   east face  the main block's wall x 11.70..12.00 (windows z -1.05..1.05
//              and 4.6..7.05 on both sides), the cab glazing x 11.45..11.90
//   ladder     on the east face at z -2.8, between the window columns, its
//              rails at x 12.35 (0.2 m clear of the roof's edge), from the
//              ground straight to the roof and 1.1 m on over the rail
//
// C# 3.0 (csc from .NET 3.5). ASCII only.
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

        internal const float LadderX = 12.35f;        // the rails' plane
        internal const float LadderZ = -2.8f;         // the ladder's centre line
        internal const float LadderHalfW = 0.22f;     // rail to centre
        internal const float LadderOver = 1.1f;       // the grab rails over the roof
        internal const float RungStep = 0.3f;
        internal const float WallX = 12.0f;           // main block east face (brackets)

        // the game's ladder (LadderObject, Revival.EastLadders.cs wires it)
        internal const float StartX = 13.1f;          // StartPoint: the foot, 0.75 m out
        internal const float BeupDrop = 2.9f;         // BeupPoint under the roof (as chimney B1c)
        internal const float BeupX = 12.65f;
        internal const float EndX = 11.2f;            // EndPoint: on the roof inside the rail
        internal const float DownX = 12.0f;           // ClimbDownPoint box: over the rail at the top
        internal const float DownW = 0.5f, DownH = 1.2f, DownD = 0.7f;
        internal const float ColW = 0.3f, ColD = 0.7f;            // Ladder box (x, z)

        // the NPC climb
        internal const float FootX = 13.4f;           // where he walks to at the foot
        internal const float ClimbX = 12.85f;         // his axis on the ladder (0.3 m capsule clear of it)
        internal const float OverX = 11.6f;           // over the rail, before he steps down
        internal const float OverY = RoofY + 1.15f;   // feet 0.15 m over the rail's top
        internal const float ClimbSpeed = 3.2f;       // m/s up and down the rungs: a short climb
        internal const float StepSpeed = 1.5f;        // m/s on and off the ladder
        // arrivals are generous: the agent stops short by its stopping distance
        internal const float FootArrive = 1.4f;       // at the foot: climb
        internal const float TopArrive = 1.2f;        // at the top: climb down
        internal const float PostArrive = 1.0f;       // at the roof post: hold
        internal const int PathMax = 6;

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
            return new Vector3(PostX[i], RoofY, PostZ[i]);
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

        internal static Vector3 Foot(float footY) { return new Vector3(FootX, footY, LadderZ); }
        internal static Vector3 Exit() { return new Vector3(EndX, RoofY, LadderZ); }

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

        /// <summary>Man and goal on different sides of the ladder: a flat
        /// arrival check must not count him there.</summary>
        internal static bool Split(Vector3 man, Vector3 goal) { return OnRoof(man) != GoalUp(goal); }

        static float Flat(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>The leg of a merc's move from man towards goal (all in
        /// the tower frame). foot and exit are the NavMesh spots he walks to
        /// at the ladder's foot and on the roof beside its top (Foot() and
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

        /// <summary>The climb as a polyline from where he stands: up = foot,
        /// the ladder, over the rail, down onto the roof; down the same way
        /// back (foot and exit as in Leg). Returns the point count (at most
        /// PathMax).</summary>
        internal static int Path(bool up, Vector3 start, Vector3 foot, Vector3 exit, Vector3[] pts)
        {
            Vector3 bottom = new Vector3(ClimbX, foot.y, LadderZ);
            Vector3 top = new Vector3(ClimbX, OverY, LadderZ);
            Vector3 over = new Vector3(OverX, OverY, LadderZ);
            int n = 0;
            pts[n++] = start;
            if (up)
            {
                pts[n++] = bottom; pts[n++] = top; pts[n++] = over; pts[n++] = exit;
            }
            else
            {
                pts[n++] = exit; pts[n++] = over; pts[n++] = top; pts[n++] = bottom; pts[n++] = foot;
            }
            return n;
        }

        static float SegTime(Vector3 a, Vector3 b)
        {
            float flat = Flat(a, b), dy = Mathf.Abs(b.y - a.y);
            // on the rungs (mostly vertical) at climb pace, else a step
            return flat < dy ? dy / ClimbSpeed : Mathf.Sqrt(flat * flat + dy * dy) / StepSpeed;
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

        /// <summary>On the rungs (facing the wall) rather than stepping.</summary>
        internal static bool OnRungs(Vector3 l, float footY)
        {
            return Mathf.Abs(l.x - ClimbX) < 0.05f && l.y > footY + 0.05f && l.y < OverY - 0.05f;
        }
    }
}

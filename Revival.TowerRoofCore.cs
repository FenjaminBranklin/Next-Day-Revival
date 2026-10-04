// Next Day: Survival - Revival Toolkit
//
// C W3: the main roof (design 9 m, 8.79 m on the shipped C1_LOD0 collider)
// is THE roof. The existing north outside stairs (three flights and a short
// level threshold in the parapet gap) reach it; the five sandbag
// posts stand on it; the cab (the glazed command room) is reached over the
// roof's own inner flight. E W1: the radar console stands INSIDE the cab, by
// its south window. The kit's player ladder reaches the antenna roof; mercs
// continue to use only the main roof and the cab. H T2: every post up there
// (roof posts, the console seat, the ladder posts on the command catwalk for
// an owner on the antenna roof) ends an explicit waypoint walk from the
// stairs; the cab NavMesh only serves cab spots that are no post.
// Coordinates are metres in TowerRadar's C1 frame (world units = m * 2.8),
// heights measured on the shipped collider. Walks across the roof follow a
// visibility graph over authored, capsule-inflated obstacle rectangles, so
// every leg is a straight line the offline check can prove.
// C# 3.0, ASCII. Pure layout/routing is compiled by tower_roof_check.py.
using UnityEngine;

namespace NextDayRevival
{
    internal static class TowerRoofCore
    {
        // ------------------------------------------------------------ layout

        internal const float RoofY = 8.79f;           // top of the main roof (C1_LOD0)
        internal const float CabFloorY = 11.79f;      // cab floor and the inner flight's landing
        internal const float CabRoofY = 15.142f;     // measured cab roof (C1_LOD0): the antenna only
        // capsule centres inside the parapet (all-collider free map)
        internal const float RoofMinX = -11.3f, RoofMaxX = 11.35f;
        internal const float RoofMinZ = -8.45f, RoofMaxZ = 8.35f;
        internal const float CabMinX = 4.3f, CabMaxX = 11.6f;
        internal const float CabMinZ = -3.6f, CabMaxZ = 3.6f;

        internal const float StepSpeed = 2.4f;        // brisk stair walk; ~10 s up the stairs
        internal const float StuckSeconds = 2f, MaxSeconds = 40f;   // approach, stairs and the walk round the cab
        internal const float FootArrive = 0.35f, TopArrive = 0.35f, PostArrive = 1.0f;
        internal const float DoorArrive = 0.8f;
        internal const int PathMax = 32;
        internal const float CapsuleRadius = 0.27f, CapsuleHeight = 1.8f;
        // Feet clear a ramp tangent by r * (sec(36.87 degrees) - 1), plus margin.
        internal const float FootLift = 0.12f;
        // The existing three flights: a line 5 cm over the measured nosings
        // (0.25 m treads, 0.1875 m risers), landings at their own height;
        // then level across the parapet gap on the building's own landing
        // slab (top 8.791). H T1 excludes the grime decal's collider face in
        // the building recipe; no raised barrier, no runtime threshold.
        internal static readonly Vector3[] Stair = {
            new Vector3(-10.9f, 0f, 9.65f), new Vector3(-10.56f, -0.02f, 9.65f),
            new Vector3(-6.81f, 2.79f, 9.65f), new Vector3(-5.41f, 2.79f, 9.65f),
            new Vector3(-1.41f, 5.79f, 9.65f), new Vector3(-0.01f, 5.79f, 9.65f),
            new Vector3(3.99f, 8.79f, 9.75f), new Vector3(5f, 8.79f, 9.75f),
            new Vector3(5f, 8.79f, 9.1f), new Vector3(5f, 8.79f, 8.88f),
            new Vector3(5f, 8.79f, 8.2f), new Vector3(5f, 8.79f, 7.6f)
        };
        // The cab's way out: inside the door, the door, the inner flight's
        // landing and foot (5 cm over its nosings), the roof west of it.
        internal static readonly Vector3[] Inner = {
            new Vector3(5.4f, CabFloorY, 0f), new Vector3(4.6f, CabFloorY, 0f),
            new Vector3(2.58f, CabFloorY, 0f), new Vector3(-1.42f, RoofY, 0f),
            new Vector3(-1.6f, RoofY, 0f)
        };
        // who is "up": on the roof (cab and inner flight included), or heading for a point on / beside it
        internal const float UpBelow = 1.3f;          // the roof counts from RoofY - this
        internal const float GoalBelow = 1.5f, GoalAbove = 4.0f;
        internal const float GoalReach = 10.0f;       // FOLLOW slots lie up to 10 m behind the owner

        // THE CONSOLE (E W1): inside the cab on its floor, the desk against
        // the south window under the antenna mast (x 7.9), its screen north;
        // the operator's chair north of it, so he faces the screen and the
        // window. The south sill's inner face is z -3.368 (C1_LOD0); the desk
        // collider (1.3 x 0.7, centre 5 cm north of the root) keeps 3 cm off it.
        // The baked, carved cab NavMesh takes him from the door to the chair.
        internal const float ConsoleX = 7.9f, ConsoleZ = -2.95f, SeatZ = ConsoleZ + 0.85f;
        internal const float SeatReach = 1.5f;        // a goal this close to the seat means the seat
        internal static Vector3 Seat() { return new Vector3(ConsoleX, CabFloorY, SeatZ); }

        // H T2: the cab's own waypoints, no free NavMesh to a post. The aisle
        // (z 0) runs from the door past every desk and chair (all at |z| >= 1.15,
        // TowerCommandRoomCore); the chair is reached from the north of it.
        internal const float AisleZ = 0f, AisleMinX = 5.4f, AisleMaxX = 9.35f;
        internal static Vector3 SeatApproach() { return new Vector3(ConsoleX, CabFloorY, SeatZ + 1.3f); }

        // H T2: the antenna roof's ladder (H T1 Ladders/C1Roof, StartPoint
        // 3.35/1.73 in this frame) stands on the command catwalk north of the
        // inner flight's landing (x 2.83..4.3, z 0.55..2.6 inside its rails).
        // Mercs do not climb it: a goal up there (an owner on the antenna
        // roof) means the two ladder posts beside its foot, which stays free
        // for a player, both facing the ladder (slots 0 and 1; the others
        // keep their roof posts). Reached from the landing.
        internal static readonly float[] LadderX = { 3.85f, 3.2f };
        internal static readonly float[] LadderZ = { 1.0f, 1.0f };
        internal static Vector3 Landing() { return new Vector3(3.3f, CabFloorY, 0.25f); }
        // the first two slots; the rest of his men hold their roof posts
        internal static bool Ladder(int slot) { return slot >= 0 && slot < LadderX.Length; }
        internal static Vector3 LadderPost(int slot)
        {
            int i = slot & 1;
            return new Vector3(LadderX[i], CabFloorY, LadderZ[i]);
        }

        // THE POSTS: five places behind a sandbag wall along the parapet, one
        // per merc slot, looking out over the wall (face). Sandbags 0.9 m high.
        internal const float BagH = 0.9f;
        internal static readonly float[] PostX = { -2.5f, 10.45f, -10.45f, -4.0f, 10.45f };
        internal static readonly float[] PostZ = { 7.55f, 6.6f, 2.0f, -7.6f, -6.4f };
        internal static readonly float[] FaceX = { 0f, 1f, -1f, 0f, 1f };
        internal static readonly float[] FaceZ = { 1f, 0f, 0f, -1f, 0f };
        internal static readonly float[] BagX = { -2.5f, 11.27f, -11.27f, -4.0f, 11.27f };
        internal static readonly float[] BagZ = { 8.33f, 6.6f, 2.0f, -8.38f, -6.4f };
        internal static readonly float[] BagSX = { 2.2f, 0.45f, 0.45f, 2.2f, 0.45f };
        internal static readonly float[] BagSZ = { 0.45f, 2.2f, 2.2f, 0.45f, 2.2f };

        // The shipped roof's own obstacles, min x, min z, max x, max z,
        // ALREADY inflated by the capsule (all-collider free map at 8.79 m).
        internal static readonly float[] Blocks = {
            -1.4f, -1.1f, 4.8f, 1.0f,         // inner flight and its pillars
            4.65f, -3.5f, 11.8f, 3.5f,        // the cab block
            5.1f, 4.25f, 9.8f, 6.25f,         // air conditioning units
            6.6f, 3.4f, 7.55f, 4.35f,         // their duct to the cab
            -4.6f, 4.75f, -3.2f, 6.1f,        // north vent
            0.85f, -6.8f, 2.35f, -5.3f,       // south vent
            -11.8f, -6.05f, -9.35f, -4.1f     // south-west box
        };
        internal const float Inflate = 0.3f;          // runtime boxes grow by this

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
        internal const int LegWalk = 1;       // walk to leg (the bounded direct walk)
        internal const int LegHold = 2;       // at his post: hold, leg = the direction he looks
        internal const int LegClimbUp = 3;    // at the foot: climb up
        internal const int LegClimbDown = 4;  // at the top: climb down
        internal const int LegNav = 5;        // plain NavMesh move to leg (cab inside, last steps to an exact spot)

        /// <summary>Up there: on the main roof, the inner flight or in the cab.</summary>
        internal static bool OnRoof(Vector3 l)
        {
            return l.y > RoofY - UpBelow && l.y < RoofY + GoalAbove
                && l.x > RoofMinX - 0.3f && l.x < RoofMaxX + 0.3f && l.z > RoofMinZ - 0.3f && l.z < RoofMaxZ + 0.3f;
        }

        /// <summary>Inside the cab (the command room), at its floor.</summary>
        internal static bool InCab(Vector3 l)
        {
            return l.y > RoofY + 2f && l.y < CabFloorY + 2.5f
                && l.x > CabMinX && l.x < CabMaxX && l.z > CabMinZ && l.z < CabMaxZ;
        }

        /// <summary>On the inner flight or its landing (above the roof, west of the cab).</summary>
        internal static bool OnFlight(Vector3 l)
        {
            return l.y > RoofY + 0.15f && l.y < CabFloorY + 1.5f
                && l.x > -1.4f && l.x <= CabMinX && l.z > -0.9f && l.z < 0.9f;
        }

        /// <summary>On the command catwalk or the inner flight's landing (cab
        /// floor height, west of the cab).</summary>
        internal static bool OnCatwalk(Vector3 l)
        {
            return l.y > RoofY + 2f && l.y < CabFloorY + 1.5f
                && l.x > Inner[2].x - 0.05f && l.x <= CabMinX && l.z > -0.9f && l.z < 2.6f;
        }

        /// <summary>H T2: up on the cab's roof (the antenna roof, above the
        /// cab's ceiling), reached only by the ladder.</summary>
        internal static bool OnAntenna(Vector3 l)
        {
            return l.y > CabFloorY + 2.5f && l.y < CabRoofY + 3f
                && l.x > CabMinX - 1f && l.x < CabMaxX + 1f && l.z > CabMinZ - 1f && l.z < CabMaxZ + 1f;
        }

        /// <summary>A goal meant for the roof: at roof height over it or
        /// beside it (a FOLLOW slot behind an owner standing up there), in
        /// the cab, or on the antenna roof (H T2: the ladder posts).</summary>
        internal static bool GoalUp(Vector3 l)
        {
            if (OnAntenna(l)) return true;
            if (l.y < RoofY - GoalBelow || l.y > RoofY + GoalAbove) return false;
            float dx = l.x < RoofMinX ? RoofMinX - l.x : (l.x > RoofMaxX ? l.x - RoofMaxX : 0f);
            float dz = l.z < RoofMinZ ? RoofMinZ - l.z : (l.z > RoofMaxZ ? l.z - RoofMaxZ : 0f);
            return dx * dx + dz * dz <= GoalReach * GoalReach;
        }

        /// <summary>The console seat (or a goal right at it).</summary>
        internal static bool AtSeat(Vector3 l)
        {
            return Flat(l, Seat()) <= SeatReach && Mathf.Abs(l.y - CabFloorY) < 1.5f;
        }

        /// <summary>A goal walked to exactly (the cab with the console seat);
        /// every other roof goal means his post.</summary>
        internal static bool Exact(Vector3 l) { return InCab(l) || AtSeat(l); }

        /// <summary>Where a roof goal ends his direct walk: the console seat
        /// itself (H T2), inside the cab's door (elsewhere in the cab), a
        /// ladder post (the antenna roof) or his roof post.</summary>
        internal static Vector3 Target(Vector3 goal, int slot)
        {
            if (AtSeat(goal)) return Seat();
            if (InCab(goal)) return Inner[0];
            if (OnAntenna(goal) && Ladder(slot)) return LadderPost(slot);
            return Post(slot);
        }

        /// <summary>The way his post faces: the ladder for a ladder post,
        /// out over the sandbags for a roof post.</summary>
        internal static Vector3 Face(Vector3 goal, int slot)
        {
            if (OnAntenna(goal) && Ladder(slot)) return new Vector3(0f, 0f, 1f);
            return Face(slot);
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
            if (manUp && InCab(man))
            {
                // H T2: the console seat is a post with its own waypoints
                // (aisle, north of the chair); at the chair MercAA takes him.
                if (goalUp && AtSeat(goal))
                {
                    leg = Seat();
                    if (Flat(man, leg) <= FootArrive) { leg = goal; return LegNav; }
                    return LegWalk;
                }
                // Any other spot in the cab is no post: the cab's NavMesh.
                if (goalUp && InCab(goal)) return LegNav;
                // Out of the cab: the aisle, the door and on (Route/Path).
                leg = goalUp ? Target(goal, slot) : exit;
                return LegWalk;
            }
            if (manUp && goalUp)
            {
                leg = Target(goal, slot);
                if (Exact(goal)) return LegWalk;
                if (Flat(man, leg) <= PostArrive && Mathf.Abs(man.y - leg.y) < 1f) { leg = Face(goal, slot); return LegHold; }
                return LegWalk;
            }
            if (goalUp)
            {
                leg = foot;
                if (Flat(man, leg) <= FootArrive && man.y < foot.y + 1.5f) return LegClimbUp;
                return LegWalk;
            }
            leg = exit;
            if (Flat(man, leg) <= TopArrive && Mathf.Abs(man.y - leg.y) < 1f) return LegClimbDown;
            return LegWalk;
        }

        // -------------------------------------------------------------- climb

        /// <summary>The stair walk as a polyline from where he stands: up = foot,
        /// the stair, over the threshold onto the roof; down = across the
        /// roof (out of the cab first) to the stair and down to the foot
        /// (foot and exit as in Leg). Returns the point count (at most
        /// PathMax). Route() then adds the roof walk to his end point.</summary>
        internal static int Path(bool up, Vector3 start, Vector3 foot, Vector3 exit, Vector3[] pts)
        {
            int n = 0;
            pts[n++] = start;
            if (up) {
                pts[n++] = foot;
                for (int i = 1; i < Stair.Length; i++) pts[n++] = Stair[i];
                pts[n++] = exit;
            } else {
                n = Leave(pts, n);
                n = Graph(pts, n, exit, PathMax - Stair.Length - 1);
                for (int i = Stair.Length - 1; i > 0; i--) pts[n++] = Stair[i];
                pts[n++] = foot;
            }
            return n;
        }

        /// <summary>Appends the roof walk from pts[n-1] to end: out of the
        /// cab or off the inner flight, around every obstacle, into the cab
        /// when end is inside its door. Returns the new count.</summary>
        internal static int Route(Vector3[] pts, int n, Vector3 end)
        {
            Vector3 s = pts[n - 1];
            bool seat = AtSeat(end), cab = seat || InCab(end), ladder = !cab && OnCatwalk(end);
            // H T2: already up at cab-floor height - the upper level's own
            // waypoints, never down onto the roof and back up the flight.
            if (cab && InCab(s)) return CabTail(pts, Aisle(pts, n), end, seat);
            if (ladder && InCab(s))
            {
                n = Aisle(pts, n);
                for (int i = Flat(pts[n - 1], Inner[0]) > 0.05f ? 0 : 1; i <= 1; i++) pts[n++] = Inner[i];
                pts[n++] = Landing();
                pts[n++] = end;
                return n;
            }
            if ((cab || ladder) && OnCatwalk(s))
            {
                if (s.z > 0.9f || ladder) pts[n++] = Landing();
                if (ladder) { pts[n++] = end; return n; }
                pts[n++] = Inner[1];
                pts[n++] = Inner[0];
                return CabTail(pts, n, end, seat);
            }
            n = Leave(pts, n);
            n = Graph(pts, n, cab || ladder ? Inner[Inner.Length - 1] : end, PathMax - (cab || ladder ? 6 : 0));
            if (ladder)
            {
                for (int i = Inner.Length - 2; i >= 2; i--) pts[n++] = Inner[i];
                pts[n++] = Landing();
                pts[n++] = end;
                return n;
            }
            if (!cab) return n;
            for (int i = Inner.Length - 2; i >= 0; i--) pts[n++] = Inner[i];
            return CabTail(pts, n, end, seat);
        }

        // In the cab: into the aisle unless he already stands in its open
        // band (no desk or chair between z -1.1 and 0.6, x 5.4..9.35).
        static int Aisle(Vector3[] pts, int n)
        {
            Vector3 s = pts[n - 1];
            if (s.z >= AisleZ - 1.1f && s.z <= AisleZ + 0.6f) return n;
            float x = s.x < AisleMinX ? AisleMinX : (s.x > AisleMaxX ? AisleMaxX : s.x);
            pts[n++] = new Vector3(x, CabFloorY, AisleZ);
            return n;
        }

        // From the aisle to the cab goal: the console seat over its approach
        // north of the chair, any other cab goal straight (Target = the door).
        static int CabTail(Vector3[] pts, int n, Vector3 end, bool seat)
        {
            if (seat && Flat(pts[n - 1], SeatApproach()) > 0.05f) pts[n++] = SeatApproach();
            if (Flat(pts[n - 1], end) > 0.01f || n == 1) pts[n++] = end;
            return n;
        }

        // Down the inner flight to the roof beside it: out of the cab over
        // the aisle and the door, off the catwalk over the landing.
        static int Leave(Vector3[] pts, int n)
        {
            Vector3 s = pts[n - 1];
            int from;
            if (InCab(s)) { n = Aisle(pts, n); from = Flat(pts[n - 1], Inner[0]) > 0.05f ? 0 : 1; }
            else if (OnCatwalk(s) && s.z > 0.9f) { pts[n++] = Landing(); from = 2; }
            else if (OnFlight(s)) from = s.x > Inner[2].x ? 2 : 3;
            else return n;
            for (int i = from; i < Inner.Length; i++) pts[n++] = Inner[i];
            return n;
        }

        // ------------------------------------------------- roof walk graph

        static float[] _ob;                   // obstacles: min x, min z, max x, max z
        static int _obN, _nodes;
        static float[] _nx, _nz, _dist;
        static int[] _prev, _chain;
        static bool[] _vis, _done;

        internal static int ObstacleCount { get { Init(); return _obN; } }

        internal static void Obstacle(int i, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            Init();
            minX = _ob[i * 4]; minZ = _ob[i * 4 + 1]; maxX = _ob[i * 4 + 2]; maxZ = _ob[i * 4 + 3];
        }

        static void Add(ref int k, float cx, float cz, float sx, float sz)
        {
            _ob[k++] = cx - sx * 0.5f - Inflate; _ob[k++] = cz - sz * 0.5f - Inflate;
            _ob[k++] = cx + sx * 0.5f + Inflate; _ob[k++] = cz + sz * 0.5f + Inflate;
        }

        /// <summary>Builds the obstacle list and the corner graph once (all
        /// static; the first climb or the roof's build pays it).</summary>
        internal static void Init()
        {
            if (_ob != null) return;
            int count = Blocks.Length / 4 + BagX.Length;
            float[] ob = new float[count * 4];
            _ob = ob;
            int k = 0;
            for (int i = 0; i < Blocks.Length; i++) ob[k++] = Blocks[i];
            for (int i = 0; i < BagX.Length; i++) Add(ref k, BagX[i], BagZ[i], BagSX[i], BagSZ[i]);
            _obN = count;
            int max = count * 4 + 2;
            _nx = new float[max]; _nz = new float[max]; _dist = new float[max];
            _prev = new int[max]; _chain = new int[max]; _done = new bool[max];
            int nodes = 0;
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 4; c++)
                {
                    float x = (c & 1) == 0 ? ob[i * 4] - 0.05f : ob[i * 4 + 2] + 0.05f;
                    float z = (c & 2) == 0 ? ob[i * 4 + 1] - 0.05f : ob[i * 4 + 3] + 0.05f;
                    if (x < RoofMinX || x > RoofMaxX || z < RoofMinZ || z > RoofMaxZ || Inside(x, z) >= 0) continue;
                    _nx[nodes] = x; _nz[nodes] = z; nodes++;
                }
            _nodes = nodes;
            _vis = new bool[nodes * nodes];
            for (int a = 0; a < nodes; a++)
                for (int b = a + 1; b < nodes; b++)
                    _vis[a * nodes + b] = _vis[b * nodes + a] = Clear(_nx[a], _nz[a], _nx[b], _nz[b]);
        }

        // The first obstacle that holds the point, -1 none.
        static int Inside(float x, float z)
        {
            for (int i = 0; i < _obN; i++)
                if (x > _ob[i * 4] && x < _ob[i * 4 + 2] && z > _ob[i * 4 + 1] && z < _ob[i * 4 + 3]) return i;
            return -1;
        }

        /// <summary>Does the flat segment a-b miss every obstacle? Obstacles
        /// holding either end are ignored (he walks out of them).</summary>
        internal static bool Clear(float ax, float az, float bx, float bz)
        {
            Init();
            for (int i = 0; i < _obN; i++)
            {
                float x0 = _ob[i * 4], z0 = _ob[i * 4 + 1], x1 = _ob[i * 4 + 2], z1 = _ob[i * 4 + 3];
                if ((ax > x0 && ax < x1 && az > z0 && az < z1) || (bx > x0 && bx < x1 && bz > z0 && bz < z1)) continue;
                if (Crosses(ax, az, bx, bz, x0, z0, x1, z1)) return false;
            }
            return true;
        }

        // Liang-Barsky against the open rectangle.
        static bool Crosses(float ax, float az, float bx, float bz, float x0, float z0, float x1, float z1)
        {
            float t0 = 0f, t1 = 1f, dx = bx - ax, dz = bz - az;
            if (!Clip(-dx, ax - x0, ref t0, ref t1) || !Clip(dx, x1 - ax, ref t0, ref t1)
                || !Clip(-dz, az - z0, ref t0, ref t1) || !Clip(dz, z1 - az, ref t0, ref t1)) return false;
            return t1 - t0 > 1e-4f;
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (p > -1e-7f && p < 1e-7f) return q > 0f;
            float r = q / p;
            if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }

        static bool Sees(int a, int b)
        {
            if (a < _nodes && b < _nodes) return _vis[a * _nodes + b];
            return Clear(_nx[a], _nz[a], _nx[b], _nz[b]);
        }

        /// <summary>Shortest obstacle-free walk on the roof from pts[n-1] to
        /// end (end included, at roof height between), at most up to the
        /// point count limit. No allocation.</summary>
        static int Graph(Vector3[] pts, int n, Vector3 end, int limit)
        {
            Init();
            Vector3 s = pts[n - 1];
            int S = _nodes, E = _nodes + 1, total = _nodes + 2;
            _nx[S] = s.x; _nz[S] = s.z; _nx[E] = end.x; _nz[E] = end.z;
            for (int i = 0; i < total; i++) { _dist[i] = float.MaxValue; _prev[i] = -1; _done[i] = false; }
            _dist[S] = 0f;
            while (true)
            {
                int u = -1;
                float best = float.MaxValue;
                for (int i = 0; i < total; i++)
                    if (!_done[i] && _dist[i] < best) { best = _dist[i]; u = i; }
                if (u < 0 || u == E) break;
                _done[u] = true;
                for (int v = 0; v < total; v++)
                {
                    if (_done[v] || v == u || !Sees(u, v)) continue;
                    float dx = _nx[v] - _nx[u], dz = _nz[v] - _nz[u];
                    float d = best + Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < _dist[v]) { _dist[v] = d; _prev[v] = u; }
                }
            }
            int c = 0;
            if (_prev[E] >= 0)
                for (int v = _prev[E]; v != S && v >= 0; v = _prev[v]) _chain[c++] = v;
            // corners from the start side; drop any beyond the point limit
            for (int i = c - 1; i >= 0 && n < limit - 1; i--)
                pts[n++] = new Vector3(_nx[_chain[i]], RoofY, _nz[_chain[i]]);
            pts[n++] = end;
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

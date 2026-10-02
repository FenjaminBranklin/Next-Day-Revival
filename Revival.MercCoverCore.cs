// M1 mercenaries, cover perception - the geometry core. Pure code: it knows
// Vector3 and Mathf and asks ICoverWorld for every ray, ground and standing
// probe, so research/merc_cover_check.py compiles this file unchanged against
// analytic test geometry. In the game Revival.MercCover.cs answers with
// Physics.Raycast and the NavMesh (docs/ai/tasks/m1-merc-cover-points.md).
//
//   CELLS     the world in 48 unit squares (about 17 m). A cell is built once,
//             time-sliced, when a merc comes near: a 4 x 4 grid of probes,
//             each casting eight low horizontal rays. Every hit is a face;
//             each distinct face is measured from where a man would crouch at
//             it: how far, how high, how wide to either side, where its edges
//             are. What hides a crouching man is kept as a COVER POINT with
//             its peek sides: LEFT / RIGHT (an edge he can lean past, and the
//             spot beside it he steps to) and OVER (the face is lower than a
//             standing man's eye). Too thin (a sapling, a post), too low (a
//             kerb) and unreachable faces are dropped. The probe spots are
//             kept as GROUND points: terrain crest candidates, judged per
//             threat because a crest only hides from what is below it.
//   CACHE     64 cells, the least recently wanted one goes first. A cell is
//             rebuilt after 180 s (45 s when a vehicle made one of its points)
//             into a spare slot and swapped in when done: a query never sees
//             a half-built cell.
//   QUERIES   Best: the best confirmed point near a man against up to four
//             threats. An analytic test on the cached faces ranks them, one
//             ray confirms the winner; terrain crests are tried with a ray
//             each. Exposed: can a threat see him (two rays). Peek: which side
//             of a point sees a threat and where he stands for it.
//
// Units: game units; the NPC capsule is 5.0 tall (NpcWar world scale, about
// 2.8 units per metre). C# 3.0, ASCII only.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>What the cover field asks the world.</summary>
    internal interface ICoverWorld
    {
        /// <summary>The first solid hit along a ray. People and triggers are
        /// no hit. dynamic: the thing can drive away (it has a rigidbody).</summary>
        bool Cast(Vector3 from, Vector3 dir, float range, out Vector3 point, out Vector3 normal, out bool dynamic);

        /// <summary>The first surface straight down from top.</summary>
        bool Ground(Vector3 top, float depth, out Vector3 point, out Vector3 normal);

        /// <summary>The nearest spot a man can stand on within reach.</summary>
        bool Stand(Vector3 near, float reach, out Vector3 at);
    }

    /// <summary>Peek sides of a cover point (flags).</summary>
    internal static class CoverPeek
    {
        internal const byte None = 0, Left = 1, Right = 2, Over = 4;

        static readonly string[] Names = new string[] { "-", "L", "R", "LR", "O", "LO", "RO", "LRO" };

        internal static string Name(byte flags) { return Names[flags & 7]; }
    }

    /// <summary>One cached place to crouch. Left and right are as he faces
    /// the face (the obstacle).</summary>
    internal struct CoverPoint
    {
        public Vector3 Pos;          // where he crouches, on the ground
        public Vector3 Normal;       // flat unit vector from the face toward him
        public float Depth;          // Pos to the face
        public float Left, Right;    // how far the face covers to either side of Pos
        public float Top;            // the highest height above Pos it was measured to block
        public Vector3 PeekL, PeekR; // where he stands to lean past that edge
        public byte Peek;            // CoverPeek flags
        public bool Tall;            // hides a standing man too (Top = TallHeight)
        public bool Dynamic;         // made by something that can drive away
        public float BadUntil;       // a confirming ray failed: skipped until then
        // An inside corner: a second wall across one side, which hides him
        // from a second direction (Side = true).
        public bool Side;
        public Vector3 Normal2;
        public float Depth2, Left2, Right2, Top2;
    }

    /// <summary>A query's answer: the point (a copy - the cell may be rebuilt
    /// meanwhile) and how to fight from it.</summary>
    internal struct CoverPick
    {
        public bool Found;
        public bool Confirmed;       // a ray proved it hides him from the primary threat
        public bool Ground;          // a terrain crest point, not a face
        public int Key, Index;       // the cell and the point in it (round cover: -1 - its index)
        public CoverPoint Point;
        public byte PeekSide;        // the side that sees the primary threat (None: none does)
        public Vector3 PeekPos;      // where he stands to fire from it
        public bool PeekVerified;    // a ray proved PeekPos sees the primary threat
        public int Covered;          // how many of the threats it hides him from
        public float Score;
    }

    /// <summary>One 48 unit square: its points and, while it is built, the
    /// build cursor.</summary>
    internal sealed class CoverCell
    {
        internal const int MaxPoints = 48, MaxPending = 40, Probes = 16, MaxRounds = 24, MaxEdges = 24;

        internal int Key = CoverField.NoKey, Cx, Cz;
        internal float RefY, BuiltAt, WantedAt;
        internal bool Ready, Building, HasDynamic;
        internal int Count;
        internal readonly CoverPoint[] Points = new CoverPoint[MaxPoints];
        internal int GroundCount;
        internal readonly Vector3[] GroundAt = new Vector3[Probes];
        // Round cover (a trunk, a post, a pillar, a boulder): its centre on
        // the ground, half width, height and the stand-off he keeps from it.
        // The point itself is placed at query time, opposite the threat.
        internal int RoundCount;
        internal readonly Vector3[] RoundAt = new Vector3[MaxRounds];
        internal readonly float[] RoundR = new float[MaxRounds];
        internal readonly float[] RoundOuter = new float[MaxRounds];   // at most this wide (peeks)
        internal readonly float[] RoundTop = new float[MaxRounds];
        internal readonly float[] RoundDepth = new float[MaxRounds];
        internal readonly bool[] RoundDyn = new bool[MaxRounds];
        internal readonly float[] RoundBad = new float[MaxRounds];

        // The build cursor: probe, its ray (-1 = its ground first), the faces
        // found so far and how many of them are measured.
        internal int Probe, Dir, Pending, Done;
        internal Vector3 ProbeAt;
        internal readonly Vector3[] HitAt = new Vector3[MaxPending];
        internal readonly Vector3[] HitNormal = new Vector3[MaxPending];
        internal readonly bool[] HitDynamic = new bool[MaxPending];
        // Faces that ran past the lateral probes on a side: their far edge
        // is hunted after all faces are measured (the point, the side).
        internal int EdgeCount, EdgeDone;
        internal readonly int[] EdgeOf = new int[MaxEdges];
        internal readonly bool[] EdgeRight = new bool[MaxEdges];

        internal void Clear()
        {
            Key = CoverField.NoKey;
            Ready = false; Building = false; HasDynamic = false;
            Count = 0; GroundCount = 0; RoundCount = 0; Pending = 0; Done = 0;
            EdgeCount = 0; EdgeDone = 0;
        }

        internal void Start(int key, float refY, float now)
        {
            Clear();
            Key = key;
            Cx = CoverField.CxOf(key); Cz = CoverField.CzOf(key);
            RefY = refY; WantedAt = now; BuiltAt = now;
            Building = true;
            Probe = 0; Dir = -1;
        }

        internal bool Inside(Vector3 p, float margin)
        {
            float x0 = Cx * CoverField.CellSize, z0 = Cz * CoverField.CellSize;
            return p.x >= x0 - margin && p.x < x0 + CoverField.CellSize + margin
                && p.z >= z0 - margin && p.z < z0 + CoverField.CellSize + margin;
        }
    }

    /// <summary>The cover cache and its queries. One per client; it only ever
    /// runs for that client's own mercs.</summary>
    internal sealed class CoverField
    {
        internal const int NoKey = int.MinValue;
        internal const float CellSize = 48f;
        const float ProbeSpacing = 12f;          // 4 x 4 probes per cell
        const float ProbeHeight = 1.4f;          // probe rays: knee to hip
        const float ProbeReach = 13f;            // a little past the next probe
        const float GroundFrom = 40f, GroundDepth = 100f;
        const float Standoff = 1.3f;             // his capsule (0.75) plus a margin from the face
        const float StandReach = 2f;
        const float DepthHeight = 1.2f, DepthReach = 4.5f;
        internal const float HideHeight = 3.0f;  // a face must reach this: a crouching man's head
        internal const float MidHeight = 3.75f;  // below this a standing man fires over it
        internal const float TallHeight = 4.5f;  // this hides a standing man's eye
        const float SideHeight = 2.0f;           // lateral rays: a crouching man's chest
        internal const float Body = 0.8f;        // half his width, with a margin
        const float NoEdge = 1e6f;
        static readonly float[] SideSteps = new float[] { 0.8f, 1.6f, 3.0f, 5.0f };
        // A long face: its edge is hunted this far along, then halved once.
        static readonly float[] EdgeSteps = new float[] { 8f, 12f, 17f };
        internal const int EdgeCost = 8;           // world calls one edge hunt can cost at most
        const float EdgeIn = 1.5f;                 // the corner point stands this far in from the edge
        const float PeekOut = 0.7f;              // past the first open lateral ray
        internal const float CrouchEye = 2.7f, CrouchChest = 2.0f, CrouchHead = 2.9f;
        internal const float StandEye = 4.2f, Chest = 3.3f, ThreatEye = 4.2f;
        internal const float MinThreat = 10f;    // closer than this he is beside it, not behind it
        const float CrestShort = 2f;             // terrain crest: the peek stands this far short of the crest probe
        const float StaticLife = 180f, DynamicLife = 45f, WantLife = 5f;
        const float DedupeFlat = 2.2f;
        const float ClaimRadius = 2.4f;
        internal const int CharacteriseCost = 22;  // world calls one face can cost at most
        const float CornerReach = 3.2f;            // a wall across this close: the point is an inside corner
        const float CornerFar = 10f;               // this close: a second point in the corner itself
        const float RoundWidth = 4.8f;             // narrower, both edges found: round cover
        internal const int QueryCost = 16;         // world calls one Best can cost at most
        const int CandidateCost = 4;               // one face candidate: confirm (round: stand + ray), two peek rays
        const int CrestCost = 4;                   // kept back for terrain crests

        readonly ICoverWorld _world;
        readonly CoverCell[] _slots;
        readonly Dictionary<int, int> _index;
        readonly int[] _queue = new int[48];
        readonly float[] _queueY = new float[48];
        readonly float[] _queueAt = new float[48];
        int _queued;
        int _building = -1;

        const int MaxClaims = 16;
        readonly int[] _claimBy = new int[MaxClaims];
        readonly Vector3[] _claimAt = new Vector3[MaxClaims];
        readonly float[] _claimUntil = new float[MaxClaims];
        readonly int[] _orderBy = new int[MaxClaims];
        readonly Vector3[] _orderAt = new Vector3[MaxClaims];
        readonly float[] _orderUntil = new float[MaxClaims];

        // Query scratch: the best faces and the two best crests.
        const int TopMax = 5;
        readonly int[] _topSlot = new int[TopMax], _topIndex = new int[TopMax];
        readonly float[] _topScore = new float[TopMax];
        readonly int[] _topCover = new int[TopMax];
        int _topN;
        readonly int[] _crestSlot = new int[2], _crestIndex = new int[2], _crestTop = new int[2];
        readonly float[] _crestScore = new float[2];
        int _crestN;

        /// <summary>World calls (rays, ground and standing probes) so far.</summary>
        internal int Calls;
        internal int CellsBuilt, Queries, Confirms, ConfirmFails;
        // Faces dropped while measuring: no standing spot, nothing ahead,
        // too low, too thin, the cell full.
        internal int RejectStand, RejectDepth, RejectLow, RejectThin, RejectFull;
        // Edge hunts: tried, held already, longer still, dropped, corner points made.
        internal int EdgeTried, EdgeKnown, EdgeLong, EdgeDropped, EdgeMade;

        internal CoverField(ICoverWorld world, int capacity)
        {
            _world = world;
            // One spare: a rebuild of a full cache always has somewhere to go.
            _slots = new CoverCell[capacity + 1];
            for (int i = 0; i < _slots.Length; i++) _slots[i] = new CoverCell();
            _index = new Dictionary<int, int>(capacity * 2);
            for (int i = 0; i < MaxClaims; i++) { _claimBy[i] = -1; _orderBy[i] = -1; }
        }

        internal int Capacity { get { return _slots.Length - 1; } }
        internal int Queued { get { return _queued; } }
        internal bool BuildingNow { get { return _building >= 0; } }

        // ------------------------------------------------------------ cells

        internal static int CellOf(float v) { return Mathf.FloorToInt(v / CellSize); }
        internal static int KeyOf(int cx, int cz) { return ((cx & 0xFFFF) << 16) | (cz & 0xFFFF); }
        internal static int CxOf(int key) { return (short)((key >> 16) & 0xFFFF); }
        internal static int CzOf(int key) { return (short)(key & 0xFFFF); }

        static float Life(CoverCell c) { return c.HasDynamic ? DynamicLife : StaticLife; }

        /// <summary>The ready cell at a cell index, or null.</summary>
        internal CoverCell Cell(int cx, int cz)
        {
            int slot;
            if (!_index.TryGetValue(KeyOf(cx, cz), out slot)) return null;
            CoverCell c = _slots[slot];
            return c.Ready ? c : null;
        }

        /// <summary>Is the cell under this point built?</summary>
        internal bool ReadyAt(Vector3 p) { return Cell(CellOf(p.x), CellOf(p.z)) != null; }

        /// <summary>Ready cells and their points (status line).</summary>
        internal void Count(out int cells, out int points, out int rounds, out int crests)
        {
            cells = 0; points = 0; rounds = 0; crests = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                CoverCell c = _slots[i];
                if (!c.Ready) continue;
                cells++; points += c.Count; rounds += c.RoundCount; crests += c.GroundCount;
            }
        }

        /// <summary>Forget everything (a new scene).</summary>
        internal void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i].Clear();
            _index.Clear();
            _queued = 0;
            _building = -1;
            for (int i = 0; i < MaxClaims; i++) { _claimBy[i] = -1; _orderBy[i] = -1; }
        }

        /// <summary>A merc is here: keep the 3 x 3 cells around him built,
        /// his own first.</summary>
        internal void Want(Vector3 pos, float now)
        {
            int cx = CellOf(pos.x), cz = CellOf(pos.z);
            WantCell(cx, cz, pos.y, now);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dx != 0 || dz != 0) WantCell(cx + dx, cz + dz, pos.y, now);
        }

        void WantCell(int cx, int cz, float y, float now)
        {
            int key = KeyOf(cx, cz);
            int slot;
            if (_index.TryGetValue(key, out slot))
            {
                CoverCell c = _slots[slot];
                c.WantedAt = now;
                if (now - c.BuiltAt < Life(c)) return;
            }
            if (_building >= 0 && _slots[_building].Key == key) { _slots[_building].WantedAt = now; return; }
            for (int i = 0; i < _queued; i++)
                if (_queue[i] == key) { _queueAt[i] = now; return; }
            if (_queued == _queue.Length) return;
            _queue[_queued] = key; _queueY[_queued] = y; _queueAt[_queued] = now;
            _queued++;
        }

        /// <summary>Spend at most budget world calls on building cells; the
        /// calls spent. Never more than the budget: a face is only measured
        /// when the whole of its worst case still fits.</summary>
        internal int Build(float now, int budget)
        {
            int start = Calls;
            while (true)
            {
                if (_building < 0 && !StartNext(now)) break;
                if (!Step(_slots[_building], now, budget - (Calls - start))) break;
            }
            return Calls - start;
        }

        bool StartNext(float now)
        {
            while (_queued > 0)
            {
                int key = _queue[0];
                float y = _queueY[0], at = _queueAt[0];
                int slot;
                bool drop = now - at > WantLife          // nobody is near it any more
                    || (_index.TryGetValue(key, out slot) && now - _slots[slot].BuiltAt < Life(_slots[slot]));
                int free = drop ? -1 : FreeSlot(now);
                if (!drop && free < 0) return false;    // every slot is wanted: it waits its turn
                for (int i = 1; i < _queued; i++)
                {
                    _queue[i - 1] = _queue[i]; _queueY[i - 1] = _queueY[i]; _queueAt[i - 1] = _queueAt[i];
                }
                _queued--;
                if (drop) continue;
                _slots[free].Start(key, y, now);
                _building = free;
                return true;
            }
            return false;
        }

        int FreeSlot(float now)
        {
            int lru = -1;
            float oldest = float.MaxValue;
            for (int i = 0; i < _slots.Length; i++)
            {
                CoverCell c = _slots[i];
                if (c.Key == NoKey) return i;
                if (c.Building || now - c.WantedAt < 2f) continue;
                if (c.WantedAt < oldest) { oldest = c.WantedAt; lru = i; }
            }
            if (lru < 0) return -1;
            int indexed;
            if (_index.TryGetValue(_slots[lru].Key, out indexed) && indexed == lru) _index.Remove(_slots[lru].Key);
            _slots[lru].Clear();
            return lru;
        }

        /// <summary>One unit of work on the cell being built. False when the
        /// budget left does not cover it.</summary>
        bool Step(CoverCell c, float now, int left)
        {
            if (c.Probe < CoverCell.Probes)
            {
                if (c.Dir < 0)
                {
                    if (left < 2) return false;
                    ProbeGround(c);
                    return true;
                }
                if (left < 1) return false;
                ProbeRay(c);
                return true;
            }
            if (c.Done < c.Pending)
            {
                if (left < CharacteriseCost) return false;
                Characterise(c, c.Done++);
                return true;
            }
            if (c.EdgeDone < c.EdgeCount)
            {
                if (left < EdgeCost) return false;
                Edge(c, c.EdgeDone++);
                return true;
            }
            Finish(c, now);
            return true;
        }

        void ProbeGround(CoverCell c)
        {
            int i = c.Probe;
            float x = c.Cx * CellSize + ((i % 4) + 0.5f) * ProbeSpacing;
            float z = c.Cz * CellSize + ((i / 4) + 0.5f) * ProbeSpacing;
            Vector3 hit, up, at;
            // A roof is no probe: the NavMesh is not up there.
            if (Ground(new Vector3(x, c.RefY + GroundFrom, z), GroundDepth, out hit, out up)
                && Stand(hit, StandReach, out at) && Mathf.Abs(at.y - hit.y) < 2.5f)
            {
                c.ProbeAt = at;
                if (c.GroundCount < CoverCell.Probes) c.GroundAt[c.GroundCount++] = at;
                c.Dir = 0;
                return;
            }
            c.Probe++;
        }

        void ProbeRay(CoverCell c)
        {
            // Odd probes turn their rose by 22.5 degrees: sixteen directions per cell.
            float a = (c.Dir * 45f + (((c.Probe % 4) + (c.Probe / 4)) % 2) * 22.5f) * Mathf.Deg2Rad;
            Vector3 d = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Vector3 p, n;
            bool dyn;
            if (Cast(c.ProbeAt + Vector3.up * ProbeHeight, d, ProbeReach, out p, out n, out dyn))
                AddFace(c, p, n, dyn);
            if (++c.Dir >= 8) { c.Dir = -1; c.Probe++; }
        }

        void AddFace(CoverCell c, Vector3 p, Vector3 n, bool dyn)
        {
            n.y = 0f;
            float l = n.magnitude;
            if (l < 0.35f) return;                      // a floor or a roof edge, not a face
            n /= l;
            // Each face belongs to the cell its crouching spot is in; the
            // neighbours find theirs.
            if (!c.Inside(p + n * Standoff, 1f)) return;
            for (int i = 0; i < c.Pending; i++)
            {
                Vector3 o = c.HitAt[i] - p;
                o.y = 0f;
                if (o.sqrMagnitude < DedupeFlat * DedupeFlat && Vector3.Dot(c.HitNormal[i], n) > 0.8f) return;
            }
            if (c.Pending >= CoverCell.MaxPending) return;
            c.HitAt[c.Pending] = p;
            c.HitNormal[c.Pending] = n;
            c.HitDynamic[c.Pending] = dyn;
            c.Pending++;
        }

        /// <summary>Measure one face from where a man would crouch at it.</summary>
        void Characterise(CoverCell c, int k)
        {
            if (c.Count >= CoverCell.MaxPoints) { RejectFull++; return; }
            Vector3 n = c.HitNormal[k];
            Vector3 dir = -n;
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            Vector3 at;
            if (!Stand(c.HitAt[k] - Vector3.up * ProbeHeight + n * Standoff, StandReach, out at)) { RejectStand++; return; }
            Vector3 p, fn;
            bool dyn;
            // How far the face is, then: does it hide a crouching man, a standing one?
            if (!Cast(at + Vector3.up * DepthHeight, dir, DepthReach, out p, out fn, out dyn)) { RejectDepth++; return; }
            Vector3 gap = p - at;
            gap.y = 0f;
            float depth = gap.magnitude;
            if (!CastAny(at + Vector3.up * HideHeight, dir, depth + 1.5f)) { RejectLow++; return; }
            bool mid = CastAny(at + Vector3.up * MidHeight, dir, depth + 3f);
            bool tall = mid && CastAny(at + Vector3.up * TallHeight, dir, depth + 3f);
            float missL, missR;
            float left = Extent(at, dir, -right, depth, out missL);
            float wide = Extent(at, dir, right, depth, out missR);
            if (left + wide < 2f * Body) { RejectThin++; return; }   // too thin: a sapling, a post
            bool moving = c.HitDynamic[k] || dyn;
            float top = tall ? TallHeight : mid ? MidHeight : HideHeight;
            // Narrow, both edges found: round cover, placed per threat. Its
            // edges lie between the last lateral hit and the first miss: the
            // middle of that is the width, the misses bound the peek.
            if (missL < NoEdge && missR < NoEdge && left + wide <= RoundWidth)
            {
                float lo = (left + missL) * 0.5f, ro = (wide + missR) * 0.5f;
                AddRound(c, at + right * ((ro - lo) * 0.5f), dir, depth, (lo + ro) * 0.5f,
                         (missL + missR) * 0.5f, top, moving);
                return;
            }
            // Slide along the face until both shoulders are behind it.
            float shift = Mathf.Clamp(0f, Body - left, wide - Body);
            if (shift != 0f)
            {
                at += right * shift;
                left += shift; wide -= shift;
                if (missL < NoEdge) missL += shift;
                if (missR < NoEdge) missR -= shift;
            }
            CoverPoint cp = new CoverPoint();
            cp.Pos = at; cp.Normal = n; cp.Depth = depth;
            cp.Left = left; cp.Right = wide;
            cp.Tall = tall;
            cp.Top = top;
            cp.Dynamic = moving;
            // Chest high or lower: he stands up and fires over it.
            if (!mid) cp.Peek |= CoverPeek.Over;
            Sides(c, ref cp, at, dir, right, missL, missR);
            c.Points[c.Count++] = cp;
            if (cp.Dynamic) c.HasDynamic = true;
            // A wall longer than the lateral probes: its corners are where he
            // can fire from. A vehicle is short; its edges are found already.
            else
            {
                if (missR >= NoEdge && c.EdgeCount < CoverCell.MaxEdges) { c.EdgeOf[c.EdgeCount] = c.Count - 1; c.EdgeRight[c.EdgeCount++] = true; }
                if (missL >= NoEdge && c.EdgeCount < CoverCell.MaxEdges) { c.EdgeOf[c.EdgeCount] = c.Count - 1; c.EdgeRight[c.EdgeCount++] = false; }
            }
        }

        /// <summary>The far edge of a long face on one side: stepped along it
        /// (8, 12, 17 units), the gap halved once. Found, with room to step
        /// out past it: a corner point just inside the edge, peeking round
        /// it. Skipped when another point of the cell already holds that edge.</summary>
        void Edge(CoverCell c, int k)
        {
            if (c.Count >= CoverCell.MaxPoints) { RejectFull++; return; }
            EdgeTried++;
            int src = c.EdgeOf[k];
            bool toRight = c.EdgeRight[k];
            byte flag = toRight ? CoverPeek.Right : CoverPeek.Left;
            Vector3 n = c.Points[src].Normal;
            Vector3 at = c.Points[src].Pos;
            float depth = c.Points[src].Depth;
            Vector3 dir = -n;
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            Vector3 side = toRight ? right : -right;
            float reach = EdgeSteps[EdgeSteps.Length - 1] + 1f;
            for (int j = 0; j < c.Count; j++)
            {
                if (j == src || (c.Points[j].Peek & flag) == 0) continue;
                if (c.Points[j].Normal.x * n.x + c.Points[j].Normal.z * n.z < 0.9f) continue;
                Vector3 o = c.Points[j].Pos - at;
                if (Mathf.Abs(o.x * n.x + o.z * n.z) > 1.2f) continue;
                float along = o.x * side.x + o.z * side.z;
                if (along > 0f && along < reach) { EdgeKnown++; return; }
            }
            float last = SideSteps[SideSteps.Length - 1], miss = NoEdge;
            for (int i = 0; i < EdgeSteps.Length; i++)
            {
                if (!CastAny(at + side * EdgeSteps[i] + Vector3.up * SideHeight, dir, depth + 1.2f)) { miss = EdgeSteps[i]; break; }
                last = EdgeSteps[i];
            }
            if (miss >= NoEdge) { EdgeLong++; return; }  // longer still: the cell of its end finds that
            float half = (last + miss) * 0.5f;
            if (CastAny(at + side * half + Vector3.up * SideHeight, dir, depth + 1.2f)) last = half; else miss = half;
            float step = miss + PeekOut;
            Vector3 hp, hn;
            bool hd;
            if (Cast(at + side * last + Vector3.up * SideHeight, side, step - last + 0.8f, out hp, out hn, out hd)) return;
            Vector3 pos;
            if (!Stand(at + side * (last - EdgeIn), 1.2f, out pos) || !c.Inside(pos, 2f)) return;
            Vector3 fp, fn;
            bool dyn;
            if (!Cast(pos + Vector3.up * DepthHeight, dir, DepthReach, out fp, out fn, out dyn)) return;
            fn.y = 0f;
            float fl = fn.magnitude;
            if (fl < 0.35f || (fn.x * n.x + fn.z * n.z) / fl < 0.9f) return;   // another face: not this wall's end
            Vector3 peek;
            if (!Stand(at + side * step, 1.2f, out peek)) return;
            for (int j = 0; j < c.Count; j++)
                if (Flat(c.Points[j].Pos - pos) < DedupeFlat && c.Points[j].Normal.x * n.x + c.Points[j].Normal.z * n.z > 0.8f) return;
            CoverPoint cp = new CoverPoint();
            cp.Pos = pos; cp.Normal = n; cp.Depth = Flat(fp - pos);
            Vector3 o2 = pos - at;
            float lat = Mathf.Abs(o2.x * side.x + o2.z * side.z);
            float inner = Mathf.Max(last - lat, 0f);
            if (toRight) { cp.Right = inner; cp.Left = 5f; cp.PeekR = peek; }
            else { cp.Left = inner; cp.Right = 5f; cp.PeekL = peek; }
            if (inner < Body) return;
            cp.Top = c.Points[src].Top; cp.Tall = c.Points[src].Tall;
            cp.Peek = (byte)((c.Points[src].Peek & CoverPeek.Over) | flag);
            c.Points[c.Count++] = cp;
            EdgeMade++;
        }

        void AddRound(CoverCell c, Vector3 faceAt, Vector3 dir, float depth, float half, float outer, float top, bool moving)
        {
            Vector3 centre = faceAt + dir * (depth + half);
            for (int i = 0; i < c.RoundCount; i++)
            {
                Vector3 o = c.RoundAt[i] - centre;
                o.y = 0f;
                if (o.magnitude < c.RoundR[i] + half + 0.5f)
                {
                    if (half > c.RoundR[i]) { c.RoundR[i] = half; c.RoundTop[i] = Mathf.Max(c.RoundTop[i], top); }
                    if (outer > c.RoundOuter[i]) c.RoundOuter[i] = outer;
                    return;
                }
            }
            if (c.RoundCount >= CoverCell.MaxRounds) { RejectFull++; return; }
            int k = c.RoundCount++;
            c.RoundAt[k] = centre; c.RoundR[k] = half; c.RoundOuter[k] = outer; c.RoundTop[k] = top;
            c.RoundDepth[k] = Mathf.Clamp(depth, 1.0f, 2.2f);
            c.RoundDyn[k] = moving; c.RoundBad[k] = 0f;
            if (moving) c.HasDynamic = true;
        }

        /// <summary>Round cover as a point for one threat: he stands opposite
        /// it, both edges peekable, OVER when it is chest high.</summary>
        internal static void RoundPoint(CoverCell c, int i, Vector3 threat, out CoverPoint cp)
        {
            cp = new CoverPoint();
            Vector3 centre = c.RoundAt[i];
            Vector3 v = threat - centre;
            v.y = 0f;
            float dist = v.magnitude;
            Vector3 u = dist > 0.01f ? v / dist : new Vector3(0f, 0f, 1f);
            float r = c.RoundR[i];
            cp.Depth = c.RoundDepth[i];
            cp.Normal = -u;
            cp.Pos = centre - u * (r + cp.Depth);
            // Hidden: the part of it measured solid (the lateral hits).
            float solid = Mathf.Max(r - 0.4f, Body);
            cp.Left = solid; cp.Right = solid;
            cp.Top = c.RoundTop[i];
            cp.Tall = cp.Top >= TallHeight;
            cp.Dynamic = c.RoundDyn[i];
            Vector3 right = new Vector3(u.z, 0f, -u.x);
            float step = c.RoundOuter[i] + PeekOut + 0.4f;
            cp.PeekR = cp.Pos + right * step;
            cp.PeekL = cp.Pos - right * step;
            cp.Peek = (byte)(CoverPeek.Left | CoverPeek.Right | (cp.Top < MidHeight ? CoverPeek.Over : 0));
        }

        /// <summary>How far the face goes to one side (0 .. 5 units), and the
        /// first lateral offset where it is open (NoEdge: none found).</summary>
        float Extent(Vector3 at, Vector3 dir, Vector3 side, float depth, out float miss)
        {
            float last = 0f;
            for (int i = 0; i < SideSteps.Length; i++)
            {
                if (!CastAny(at + side * SideSteps[i] + Vector3.up * SideHeight, dir, depth + 1.2f))
                {
                    miss = SideSteps[i];
                    return last;
                }
                last = SideSteps[i];
            }
            miss = NoEdge;
            return last;
        }

        /// <summary>Both sides at chest height. Open far enough past an edge:
        /// a peek, the spot beside the edge he steps to (standable). A wall
        /// across, facing him: an inside corner. Within 3.2 units the point
        /// itself gets it as a second face; further along (to 10 units) a
        /// second point is made in the corner.</summary>
        void Sides(CoverCell c, ref CoverPoint cp, Vector3 at, Vector3 dir, Vector3 right, float missL, float missR)
        {
            float nearest = float.MaxValue;
            Vector3 cornerN = dir;
            int cornerSide = 0;
            for (int s = 0; s < 2; s++)
            {
                Vector3 side = s == 0 ? right : -right;
                float miss = s == 0 ? missR : missL;
                float step = miss < NoEdge ? miss + PeekOut : 0f;
                Vector3 hp, hn;
                bool hd;
                if (Cast(at + Vector3.up * SideHeight, side, Mathf.Max(step, CornerFar) + 0.8f, out hp, out hn, out hd))
                {
                    float d = Flat(hp - at);
                    hn.y = 0f;
                    float hl = hn.magnitude;
                    if (hl > 0.35f && d <= CornerFar && d < nearest
                        && (hn.x * side.x + hn.z * side.z) / hl < -0.6f)
                    {
                        nearest = d;
                        cornerN = hn / hl;
                        cornerSide = s == 0 ? 1 : -1;
                    }
                    if (d < step + 0.8f) continue;       // no room to step out on this side
                }
                if (step <= 0f) continue;
                Vector3 peek;
                if (!Stand(at + side * step, 1.2f, out peek)) continue;
                if (s == 0) { cp.Peek |= CoverPeek.Right; cp.PeekR = peek; }
                else { cp.Peek |= CoverPeek.Left; cp.PeekL = peek; }
            }
            if (cornerSide == 0) return;
            if (nearest <= CornerReach)
            {
                SideFace(ref cp, at, cornerN, nearest, dir);
                return;
            }
            if (c.Count + 1 >= CoverCell.MaxPoints) return;
            Vector3 toward = cornerSide > 0 ? right : -right;
            Vector3 cat;
            if (!Stand(at + toward * (nearest - Standoff - 0.2f), 1.2f, out cat)) return;
            CoverPoint corner = cp;
            corner.Pos = cat;
            float moved = Lateral(ref cp, cat);
            corner.Left = Mathf.Min(cp.Left + moved, 5f);
            corner.Right = Mathf.Min(cp.Right - moved, 5f);
            // On the corner's side the second wall carries the cover on.
            if (cornerSide > 0) { corner.Right = 5f; corner.Peek = (byte)(corner.Peek & ~CoverPeek.Right); }
            else { corner.Left = 5f; corner.Peek = (byte)(corner.Peek & ~CoverPeek.Left); }
            if (corner.Left < Body || corner.Right < Body) return;
            SideFace(ref corner, cat, cornerN, Mathf.Max(nearest - Mathf.Abs(moved), 0.5f), dir);
            if (corner.Side) c.Points[c.Count++] = corner;
        }

        /// <summary>The second face of an inside corner: high enough, tall,
        /// and how far it runs away from the corner (0, 0.8 or 2.4 units; on
        /// the corner's side the first face takes over).</summary>
        void SideFace(ref CoverPoint cp, Vector3 at, Vector3 n2, float depth2, Vector3 dirA)
        {
            Vector3 dir2 = -n2;
            if (!CastAny(at + Vector3.up * HideHeight, dir2, depth2 + 1.5f)) return;
            bool tall2 = CastAny(at + Vector3.up * TallHeight, dir2, depth2 + 1.5f);
            Vector3 right2 = new Vector3(dir2.z, 0f, -dir2.x);
            bool cornerRight = dirA.x * right2.x + dirA.z * right2.z >= 0f;
            Vector3 away = cornerRight ? -right2 : right2;
            float ext = 0f;
            if (CastAny(at + away * 0.8f + Vector3.up * SideHeight, dir2, depth2 + 1.2f))
            {
                ext = 0.8f;
                if (CastAny(at + away * 2.4f + Vector3.up * SideHeight, dir2, depth2 + 1.2f)) ext = 2.4f;
            }
            cp.Side = true;
            cp.Normal2 = n2;
            cp.Depth2 = depth2;
            cp.Top2 = tall2 ? TallHeight : HideHeight;
            if (cornerRight) { cp.Right2 = 5f; cp.Left2 = ext; }
            else { cp.Left2 = 5f; cp.Right2 = ext; }
        }

        void Finish(CoverCell c, float now)
        {
            c.Building = false;
            c.Ready = true;
            c.BuiltAt = now;
            int old;
            if (_index.TryGetValue(c.Key, out old) && old != _building) _slots[old].Clear();
            _index[c.Key] = _building;
            _building = -1;
            CellsBuilt++;
        }

        // ---------------------------------------------------------- queries

        /// <summary>Does this point hide a crouching man from a threat standing
        /// there? Pure arithmetic on the measured faces: the line to the threat
        /// must cross a face inside its width (both shoulders) and below its
        /// height. An inside corner has two faces; either will do.</summary>
        internal static bool Hides(ref CoverPoint cp, Vector3 threat)
        {
            if (HidesFace(cp.Pos, cp.Normal, cp.Depth, cp.Left, cp.Right, cp.Top, threat)) return true;
            return cp.Side && HidesFace(cp.Pos, cp.Normal2, cp.Depth2, cp.Left2, cp.Right2, cp.Top2, threat);
        }

        static bool HidesFace(Vector3 pos, Vector3 normal, float depth, float left, float right, float top, Vector3 threat)
        {
            Vector3 v = threat - pos;
            float dy = v.y;
            v.y = 0f;
            float dist = v.magnitude;
            if (dist < MinThreat) return false;
            Vector3 u = v / dist;
            float cosF = -(u.x * normal.x + u.z * normal.z);
            if (cosF < 0.26f) return false;             // more than 75 degrees off: beside it, not behind it
            // With dir = -normal and right = (dir.z, 0, -dir.x): Dot(u, right).
            float sinR = -u.x * normal.z + u.z * normal.x;
            float lat = depth * sinR / cosF;
            if (lat - Body < -left || lat + Body > right) return false;
            float rise = (dy + ThreatEye - CrouchEye) * depth / (dist * cosF);
            return CrouchEye + rise <= top - 0.15f;
        }

        /// <summary>Which side of a point sees the threat (None: none), and
        /// where he stands for it. OVER first (no step), then the nearer edge.</summary>
        internal static byte PeekToward(ref CoverPoint cp, Vector3 threat, out Vector3 at)
        {
            at = cp.Pos;
            Vector3 v = threat - cp.Pos;
            float dy = v.y;
            v.y = 0f;
            float dist = v.magnitude;
            if (dist < 1f) return CoverPeek.None;
            Vector3 u = v / dist;
            float cosF = -(u.x * cp.Normal.x + u.z * cp.Normal.z);
            float sinR = -u.x * cp.Normal.z + u.z * cp.Normal.x;
            if ((cp.Peek & CoverPeek.Over) != 0)
            {
                float rise = cosF > 0.05f ? (dy + ThreatEye - StandEye) * cp.Depth / (dist * cosF) : 0f;
                if (StandEye + rise > MidHeight) return CoverPeek.Over;
            }
            float tan = cosF > 0.05f ? sinR / cosF : (sinR >= 0f ? 20f : -20f);
            byte best = CoverPeek.None;
            float bestStep = float.MaxValue;
            // Past an edge he sees what lies on that side: the line must clear
            // the edge at the face and must not turn back across the body of
            // the obstacle behind it (its depth is unknown - a house, not a plank).
            if ((cp.Peek & CoverPeek.Right) != 0 && sinR > -0.05f)
            {
                float x = Lateral(ref cp, cp.PeekR);
                float step = Flat(cp.PeekR - cp.Pos);
                if (x + cp.Depth * tan > cp.Right + 0.3f && step < bestStep) { best = CoverPeek.Right; bestStep = step; at = cp.PeekR; }
            }
            if ((cp.Peek & CoverPeek.Left) != 0 && sinR < 0.05f)
            {
                float x = Lateral(ref cp, cp.PeekL);
                float step = Flat(cp.PeekL - cp.Pos);
                if (x + cp.Depth * tan < -cp.Left - 0.3f && step < bestStep) { best = CoverPeek.Left; at = cp.PeekL; }
            }
            return best;
        }

        static float Lateral(ref CoverPoint cp, Vector3 p)
        {
            Vector3 o = p - cp.Pos;
            return -o.x * cp.Normal.z + o.z * cp.Normal.x;
        }

        internal static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        /// <summary>Can a threat standing at threat see a man at pos? Chest
        /// first, then head: at most two rays.</summary>
        internal bool Exposed(Vector3 pos, bool crouched, Vector3 threat)
        {
            Vector3 eye = threat + Vector3.up * ThreatEye;
            if (Sees(eye, pos + Vector3.up * (crouched ? CrouchChest : Chest))) return true;
            return Sees(eye, pos + Vector3.up * (crouched ? CrouchHead : StandEye + 0.3f));
        }

        /// <summary>Is the line from a to b open? People are no hit (the
        /// world says so), so it runs from just past a to just short of b: a
        /// wall edge right beside a peek spot must count.</summary>
        internal bool Sees(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float dist = d.magnitude;
            if (dist < 1f) return true;
            d /= dist;
            return !CastAny(a + d * 0.2f, d, dist - 0.5f);
        }

        /// <summary>The best point within radius of from that hides a
        /// crouching man from threats[0], ranked by the share of all threats
        /// it hides him from (weights), a peek toward threats[0], a tall face,
        /// the run there, a vehicle (it can leave) and a run toward the enemy.
        /// leashRadius 0 = no leash. claimant: points another merc claimed are
        /// skipped. At most QueryCost world calls: three tries of a confirming
        /// ray (round cover: a standing probe first), two rays proving its peek,
        /// two rays for terrain crests, a standing probe and a ray for a
        /// crest's peek.</summary>
        internal bool Best(Vector3 from, Vector3[] threats, float[] weights, int count, float radius,
            Vector3 leash, float leashRadius, int claimant, float now, out CoverPick pick)
        {
            pick = new CoverPick();
            if (count <= 0) return false;
            Queries++;
            Vector3 primary = threats[0];
            float all = 0f;
            for (int t = 0; t < count; t++) all += weights[t];
            if (all <= 0f) all = 1f;
            float dNow = Flat(primary - from);
            _topN = 0; _crestN = 0;
            int x0 = CellOf(from.x - radius), x1 = CellOf(from.x + radius);
            int z0 = CellOf(from.z - radius), z1 = CellOf(from.z + radius);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    int slot;
                    if (!_index.TryGetValue(KeyOf(cx, cz), out slot)) continue;
                    CoverCell c = _slots[slot];
                    if (!c.Ready) continue;
                    int n;
                    float score;
                    for (int i = 0; i < c.Count; i++)
                    {
                        if (c.Points[i].BadUntil > now) continue;
                        if (!Rank(ref c.Points[i], from, threats, weights, count, all, dNow, radius,
                                  leash, leashRadius, claimant, now, out score, out n)) continue;
                        Offer(slot, i, score, n);
                    }
                    for (int i = 0; i < c.RoundCount; i++)
                    {
                        if (c.RoundBad[i] > now) continue;
                        if (Flat(c.RoundAt[i] - from) > radius + 6f) continue;
                        CoverPoint rp;
                        RoundPoint(c, i, primary, out rp);
                        if (!Rank(ref rp, from, threats, weights, count, all, dNow, radius,
                                  leash, leashRadius, claimant, now, out score, out n)) continue;
                        Offer(slot, -1 - i, score, n);
                    }
                    for (int i = 0; i < c.GroundCount; i++)
                    {
                        Vector3 pos = c.GroundAt[i];
                        float travel = Flat(pos - from);
                        if (travel > radius) continue;
                        if (leashRadius > 0f && Flat(pos - leash) > leashRadius) continue;
                        if (Claimed(pos, claimant, now)) continue;
                        int top;
                        float margin = CrestMargin(c, i, primary, out top);
                        if (margin < -1f) continue;
                        score = 2.5f * weights[0] / all + 0.6f - 1.2f * travel / radius
                            - Toward(dNow, Flat(primary - pos)) + Mathf.Min(margin, 1f) * 0.2f;
                        OfferCrest(slot, i, top, score);
                    }
                }

            // Faces and round cover: best first, one ray each (round cover is
            // stood on first: its spot was made for this threat just now). A
            // point whose peek no ray proves is kept, and the next one tried
            // while the budget lasts: a man wants cover he can fire from.
            int calls0 = Calls;
            CoverPick cp1 = new CoverPick();
            for (int r = 0; r < _topN; r++)
            {
                if (Calls - calls0 + CandidateCost > QueryCost - CrestCost) break;
                if (pick.Found && _topCover[r] < pick.Covered) continue;
                CoverCell c = _slots[_topSlot[r]];
                int idx = _topIndex[r];
                CoverPoint cand;
                if (idx >= 0)
                {
                    if (!HiddenFrom(c.Points[idx].Pos, primary))
                    {
                        c.Points[idx].BadUntil = now + 4f;
                        continue;
                    }
                    cand = c.Points[idx];
                }
                else
                {
                    RoundPoint(c, -1 - idx, primary, out cand);
                    Vector3 stand;
                    if (!Stand(cand.Pos, StandReach, out stand) || !HiddenFrom(stand, primary))
                    {
                        c.RoundBad[-1 - idx] = now + 2f;
                        continue;
                    }
                    Vector3 moved = stand - cand.Pos;
                    cand.Pos = stand; cand.PeekL += moved; cand.PeekR += moved;
                }
                cp1 = new CoverPick();
                cp1.Found = true; cp1.Confirmed = true;
                cp1.Key = c.Key; cp1.Index = idx;
                cp1.Point = cand;
                cp1.Covered = _topCover[r];
                cp1.Score = _topScore[r];
                VerifiedPeek(ref cp1, primary);
                if (!pick.Found || cp1.PeekVerified) pick = cp1;
                if (pick.PeekVerified) break;
            }
            // Terrain crests, when they could beat that: a better score, or a
            // proven peek where the face has none.
            for (int r = 0; r < _crestN; r++)
            {
                if (pick.Found && (pick.PeekVerified || pick.Covered > 1) && _crestScore[r] <= pick.Score) break;
                CoverCell c = _slots[_crestSlot[r]];
                Vector3 pos = c.GroundAt[_crestIndex[r]];
                if (!HiddenFrom(pos, primary)) continue;
                cp1 = new CoverPick();
                cp1.Found = true; cp1.Confirmed = true; cp1.Ground = true;
                cp1.Key = c.Key; cp1.Index = _crestIndex[r];
                cp1.Point.Pos = pos;
                cp1.Point.Peek = CoverPeek.Over;
                Vector3 toward = primary - pos;
                toward.y = 0f;
                if (toward.sqrMagnitude > 0.01f) cp1.Point.Normal = -toward.normalized;
                cp1.Covered = 1;
                cp1.Score = _crestScore[r];
                // The peek: up to just short of the probe that makes the crest.
                Vector3 climb = c.GroundAt[_crestTop[r]] - pos;
                float d = Flat(climb);
                Vector3 up;
                cp1.PeekSide = CoverPeek.Over;
                cp1.PeekPos = pos;
                if (d > CrestShort && Stand(pos + climb * ((d - CrestShort) / d), 3f, out up)) cp1.PeekPos = up;
                cp1.PeekVerified = Sees(cp1.PeekPos + Vector3.up * StandEye, primary + Vector3.up * Chest);
                if (!cp1.PeekVerified) cp1.PeekSide = CoverPeek.None;
                if (!pick.Found || cp1.Score > pick.Score || (cp1.PeekVerified && !pick.PeekVerified)) pick = cp1;
                break;
            }
            return pick.Found;
        }

        /// <summary>One point's rank for a query (false: not a candidate). It
        /// must hide him from the primary threat; the share of all threats it
        /// hides him from counts most.</summary>
        bool Rank(ref CoverPoint cp, Vector3 from, Vector3[] threats, float[] weights, int count, float all,
            float dNow, float radius, Vector3 leash, float leashRadius, int claimant, float now,
            out float score, out int covered)
        {
            score = 0f; covered = 0;
            float travel = Flat(cp.Pos - from);
            if (travel > radius) return false;
            if (leashRadius > 0f && Flat(cp.Pos - leash) > leashRadius) return false;
            if (Claimed(cp.Pos, claimant, now)) return false;
            if (!Hides(ref cp, threats[0])) return false;
            float cov = weights[0];
            covered = 1;
            for (int t = 1; t < count; t++)
                if (Hides(ref cp, threats[t])) { cov += weights[t]; covered++; }
            Vector3 unused;
            score = 2.5f * cov / all
                + (cp.Tall ? 0.4f : 0f)
                + (PeekToward(ref cp, threats[0], out unused) != CoverPeek.None ? 0.6f : 0f)
                - 1.2f * travel / radius
                - (cp.Dynamic ? 0.3f : 0f)
                - Toward(dNow, Flat(threats[0] - cp.Pos));
            return true;
        }

        /// <summary>The pick's peek toward the primary threat, proven by a ray
        /// (standing eye to the threat's chest): the analytic choice first,
        /// then the other sides the point has. Two rays at most.</summary>
        void VerifiedPeek(ref CoverPick pick, Vector3 threat)
        {
            pick.PeekVerified = false;
            Vector3 at;
            byte first = PeekToward(ref pick.Point, threat, out at);
            int rays = 0;
            if (first != CoverPeek.None)
            {
                rays++;
                if (Sees(at + Vector3.up * StandEye, threat + Vector3.up * Chest))
                {
                    pick.PeekSide = first; pick.PeekPos = at; pick.PeekVerified = true;
                    return;
                }
            }
            for (int k = 0; k < 3 && rays < 2; k++)
            {
                byte side = k == 0 ? CoverPeek.Over : k == 1 ? CoverPeek.Right : CoverPeek.Left;
                if (side == first || (pick.Point.Peek & side) == 0) continue;
                // Only a side the threat could be seen from at all.
                Vector3 v = threat - pick.Point.Pos;
                float sinR = -v.x * pick.Point.Normal.z + v.z * pick.Point.Normal.x;
                if ((side == CoverPeek.Right && sinR < -0.05f * Flat(v)) || (side == CoverPeek.Left && sinR > 0.05f * Flat(v))) continue;
                at = side == CoverPeek.Over ? pick.Point.Pos : side == CoverPeek.Right ? pick.Point.PeekR : pick.Point.PeekL;
                rays++;
                if (Sees(at + Vector3.up * StandEye, threat + Vector3.up * Chest))
                {
                    pick.PeekSide = side; pick.PeekPos = at; pick.PeekVerified = true;
                    return;
                }
            }
            pick.PeekSide = CoverPeek.None;
            pick.PeekPos = pick.Point.Pos;
        }

        /// <summary>Which side of a pick sees the threat, and where he stands.
        /// verify: one ray from there (standing eye to the threat's chest).</summary>
        internal byte Peek(ref CoverPick pick, Vector3 threat, bool verify, out Vector3 at)
        {
            at = pick.Point.Pos;
            if (!pick.Found) return CoverPeek.None;
            byte side;
            if (pick.Ground)
            {
                side = CoverPeek.Over;
                at = pick.PeekPos;
            }
            else side = PeekToward(ref pick.Point, threat, out at);
            if (side == CoverPeek.None) return side;
            if (verify && !Sees(at + Vector3.up * StandEye, threat + Vector3.up * Chest)) return CoverPeek.None;
            return side;
        }

        /// <summary>Hold a point for a merc so the others choose another.</summary>
        internal void Claim(int who, Vector3 at, float until, float now)
        {
            int slot = -1;
            for (int i = 0; i < MaxClaims; i++)
            {
                if (_claimBy[i] == who) { slot = i; break; }
                if (slot < 0 && (_claimBy[i] < 0 || _claimUntil[i] <= now)) slot = i;
            }
            if (slot < 0) return;
            _claimBy[slot] = who; _claimAt[slot] = at; _claimUntil[slot] = until;
        }

        internal void Release(int who)
        {
            for (int i = 0; i < MaxClaims; i++)
                if (_claimBy[i] == who) _claimBy[i] = -1;
        }

        // Orders reserve destinations independently of a fighter's current
        // cover. Normal Best/Rank queries respect both sets; no second search.
        internal void ReserveOrder(int who, Vector3 at, float until, float now)
        {
            int slot = -1;
            for (int i = 0; i < MaxClaims; i++)
            {
                if (_orderBy[i] == who) { slot = i; break; }
                if (slot < 0 && (_orderBy[i] < 0 || _orderUntil[i] <= now)) slot = i;
            }
            if (slot < 0) return;
            _orderBy[slot] = who; _orderAt[slot] = at; _orderUntil[slot] = until;
        }

        internal void ReleaseOrder(int who)
        {
            for (int i = 0; i < MaxClaims; i++)
                if (_orderBy[i] == who) _orderBy[i] = -1;
        }

        internal bool Claimed(Vector3 p, int claimant, float now)
        {
            for (int i = 0; i < MaxClaims; i++)
            {
                if (_claimBy[i] < 0 || _claimBy[i] == claimant || _claimUntil[i] <= now) continue;
                if (Flat(_claimAt[i] - p) < ClaimRadius) return true;
            }
            for (int i = 0; i < MaxClaims; i++)
            {
                if (_orderBy[i] < 0 || _orderBy[i] == claimant || _orderUntil[i] <= now) continue;
                if (Flat(_orderAt[i] - p) < 7f) return true;
            }
            return false;
        }

        /// <summary>A point that failed in use (he was hit there): skipped a while.</summary>
        internal void MarkBad(ref CoverPick pick, float now, float seconds)
        {
            if (!pick.Found || pick.Ground) return;
            int slot;
            if (!_index.TryGetValue(pick.Key, out slot)) return;
            CoverCell c = _slots[slot];
            if (pick.Index < 0)
            {
                if (-1 - pick.Index < c.RoundCount) c.RoundBad[-1 - pick.Index] = now + seconds;
                return;
            }
            if (pick.Index < c.Count && Flat(c.Points[pick.Index].Pos - pick.Point.Pos) < 0.1f)
                c.Points[pick.Index].BadUntil = now + seconds;
        }

        /// <summary>A run of more than 8 units toward the primary threat costs.</summary>
        static float Toward(float dNow, float dThere)
        {
            float closer = dNow - 8f - dThere;
            return closer > 0f ? Mathf.Min(closer * 0.02f, 1f) : 0f;
        }

        /// <summary>The terrain model at probe spacing: how far the highest
        /// probe between this one and the threat (within 26 units, inside 45
        /// degrees) stands above the line from his crouched eye to the
        /// threat's eye. Positive: a crest in between may hide him.</summary>
        static float CrestMargin(CoverCell c, int i, Vector3 threat, out int top)
        {
            top = i;
            Vector3 g = c.GroundAt[i];
            Vector3 v = threat - g;
            float dy = v.y;
            v.y = 0f;
            float dist = v.magnitude;
            if (dist < MinThreat) return -10f;
            Vector3 u = v / dist;
            float best = -10f;
            for (int j = 0; j < c.GroundCount; j++)
            {
                if (j == i) continue;
                Vector3 o = c.GroundAt[j] - g;
                float oy = o.y;
                o.y = 0f;
                float d = o.magnitude;
                if (d < 1f || d > 26f || d >= dist) continue;
                if ((o.x * u.x + o.z * u.z) / d < 0.7f) continue;
                float line = CrouchEye + (dy + ThreatEye - CrouchEye) * d / dist;
                float m = oy - line;
                if (m > best) { best = m; top = j; }
            }
            return best;
        }

        /// <summary>One ray: is anything solid between his crouched eye there
        /// and the threat's eye?</summary>
        bool HiddenFrom(Vector3 pos, Vector3 threat)
        {
            Confirms++;
            Vector3 eye = pos + Vector3.up * CrouchEye;
            Vector3 d = threat + Vector3.up * ThreatEye - eye;
            float dist = d.magnitude;
            if (dist < 2.5f) { ConfirmFails++; return false; }
            d /= dist;
            if (CastAny(eye, d, dist - 1.2f)) return true;
            ConfirmFails++;
            return false;
        }

        void Offer(int slot, int index, float score, int covered)
        {
            int at = _topN < TopMax ? _topN : TopMax - 1;
            if (_topN == TopMax && score <= _topScore[TopMax - 1]) return;
            while (at > 0 && _topScore[at - 1] < score)
            {
                _topSlot[at] = _topSlot[at - 1]; _topIndex[at] = _topIndex[at - 1];
                _topScore[at] = _topScore[at - 1]; _topCover[at] = _topCover[at - 1];
                at--;
            }
            _topSlot[at] = slot; _topIndex[at] = index; _topScore[at] = score; _topCover[at] = covered;
            if (_topN < TopMax) _topN++;
        }

        void OfferCrest(int slot, int index, int top, float score)
        {
            int at = _crestN < 2 ? _crestN : 1;
            if (_crestN == 2 && score <= _crestScore[1]) return;
            while (at > 0 && _crestScore[at - 1] < score)
            {
                _crestSlot[at] = _crestSlot[at - 1]; _crestIndex[at] = _crestIndex[at - 1];
                _crestTop[at] = _crestTop[at - 1]; _crestScore[at] = _crestScore[at - 1];
                at--;
            }
            _crestSlot[at] = slot; _crestIndex[at] = index; _crestTop[at] = top; _crestScore[at] = score;
            if (_crestN < 2) _crestN++;
        }

        // ------------------------------------------------------ world calls

        bool Cast(Vector3 from, Vector3 dir, float range, out Vector3 point, out Vector3 normal, out bool dynamic)
        {
            Calls++;
            return _world.Cast(from, dir, range, out point, out normal, out dynamic);
        }

        bool CastAny(Vector3 from, Vector3 dir, float range)
        {
            Vector3 p, n;
            bool d;
            return Cast(from, dir, range, out p, out n, out d);
        }

        bool Ground(Vector3 top, float depth, out Vector3 point, out Vector3 normal)
        {
            Calls++;
            return _world.Ground(top, depth, out point, out normal);
        }

        bool Stand(Vector3 near, float reach, out Vector3 at)
        {
            Calls++;
            return _world.Stand(near, reach, out at);
        }
    }
}

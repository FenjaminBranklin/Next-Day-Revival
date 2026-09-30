// M3 mercenaries, team tactics - the pure core (docs/ai/tasks/m3-merc-tactics.md).
// It knows Vector3 and Mathf and nothing of the game, so the offline checks
// (research/merc_squad_check.py, research/merc_fight_check.py) compile it
// UNCHANGED. MercBrain (Revival.MercFightCore.cs) posts to the board every
// Think and reads his mates from it; Revival.MercFight.cs keeps one board
// for the mercs of this client (all of them belong to the local player).
//
//   BOARD     one row per merc: where he is, the cover he holds, whether he
//             is up at a peek (covering), running between covers, calling
//             for covering fire, what he sees, his health and mode. A row
//             older than Stale seconds is a merc who stopped thinking (dead,
//             despawned, seated): he no longer counts.
//   SPREAD    a cover point within Spread of a mate's point or body is not
//             taken (the field's claims keep the same distance).
//   COVER     a merc who wants to move calls for cover; a mate who is down
//             in cover answers with a peek at once (covering fire) - the
//             mover goes when someone is up, or after a short wait.
//   CALL-OUT  what one merc sees, the others know (the adapter notes the
//             mate's threats into his own sense).
//   FLANK     one merc at a time, one flank per FlankGap seconds per team.
//   LOSING    half the team gone, or the team's health low: fall back and
//             regroup on the owner.
//   LINE      nobody fires while a mate's or the owner's body is near the
//             line of fire.
//   LANES     (merc-combat-response) a merc up and aiming at a target posts
//             his line of fire (PostLane). A mate whose body stands in it -
//             or in the owner's held aim - steps out sideways on his own
//             side (MercLane.StepOut), never across another live line; the
//             shooter holds (the veto stays) while a mate is clearing. Two
//             mercs in each other's line: the lower id stays, the other moves.
//   GRADE     the merc's traits as one number 0..1 (tiers 0..3) that the
//             brain turns into reaction, peek pace, cover choice and range.
//
// No allocation after construction; every query is a scan of at most Max
// rows. Units: game units (~2.8 per metre), seconds. C# 3.0, ASCII only.
using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>A merc's traits as one grade (0..1) and a tier (0..3).</summary>
    internal static class MercGrade
    {
        /// <summary>precise and tanky count 0.8, fast 1; a level above the
        /// default 3 adds 6 points each. 60 points is the top grade: the
        /// default profiles run from Watchman / Deserter (0, tier 0) to
        /// Spetsnaz (0.83, tier 3).</summary>
        internal static float Of(int precise, int fast, int tanky, int level)
        {
            float points = 0.8f * Mathf.Max(0, precise) + Mathf.Max(0, fast) + 0.8f * Mathf.Max(0, tanky)
                + 6f * (Mathf.Clamp(level, 1, 10) - 3);
            return Mathf.Clamp(points / 60f, 0f, 1f);
        }

        internal static int Tier(float grade)
        {
            return grade < 0.2f ? 0 : grade < 0.45f ? 1 : grade < 0.7f ? 2 : 3;
        }

        /// <summary>A value that is lo at grade 0 and hi at grade 1.</summary>
        internal static float Lerp(float grade, float lo, float hi) { return lo + (hi - lo) * Mathf.Clamp(grade, 0f, 1f); }
    }

    /// <summary>The team board of the mercs of one client.</summary>
    internal sealed class MercSquad
    {
        internal const int Max = 8;
        internal const float Stale = 0.6f;         // a row older than this: he stopped thinking
        internal const float Spread = 7f;          // cover points of two mercs at least this far apart (2.5 m)
        internal const float LineClear = 2.2f;     // a friend this close to the line of fire blocks it
        internal const float CallReach = 150f;     // call-outs reach mates this near (54 m)
        internal const float FlankGap = 10f;       // one flank per team this often at most
        internal const float LosingHealth = 0.45f; // the team's mean health below this: losing
        internal const float Reset = 10f;          // nobody fought this long: a new fight

        readonly int[] _id = new int[Max];
        readonly float[] _at = new float[Max];
        readonly Vector3[] _pos = new Vector3[Max];
        readonly Vector3[] _cover = new Vector3[Max];
        readonly Vector3[] _threat = new Vector3[Max];
        readonly bool[] _fight = new bool[Max];
        readonly bool[] _holding = new bool[Max];
        readonly bool[] _up = new bool[Max];
        readonly bool[] _moving = new bool[Max];
        readonly bool[] _call = new bool[Max];
        readonly bool[] _sees = new bool[Max];
        readonly float[] _health = new float[Max];
        readonly byte[] _mode = new byte[Max];
        // Lanes: his live line of fire (up, aiming at a target) and whether
        // he is stepping out of a mate's line right now.
        readonly bool[] _lane = new bool[Max];
        readonly Vector3[] _laneTo = new Vector3[Max];
        readonly bool[] _clearing = new bool[Max];

        int _peak;                  // most mercs fighting at once in this fight
        float _lastFight = -1000f;
        int _flankBy = -1;
        float _flankUntil, _nextFlank;

        // Counters for F8 and the offline check.
        internal int FlanksGiven;

        internal MercSquad()
        {
            for (int k = 0; k < Max; k++) _id[k] = -1;
        }

        int Row(int id, float now, bool make)
        {
            int free = -1;
            for (int k = 0; k < Max; k++)
            {
                if (_id[k] == id) return k;
                if (free < 0 && (_id[k] < 0 || now - _at[k] > 30f)) free = k;
            }
            if (!make || free < 0) return -1;
            _id[free] = id;
            return free;
        }

        bool Live(int k, int self, float now)
        {
            return _id[k] >= 0 && _id[k] != self && now - _at[k] <= Stale;
        }

        /// <summary>His row this Think (MercBrain.Think calls it last).</summary>
        internal void Post(int id, float now, Vector3 pos, bool fighting, bool holding, Vector3 cover, bool up,
            bool moving, bool call, bool sees, bool hasThreat, Vector3 threat, float health, byte mode)
        {
            int k = Row(id, now, true);
            if (k < 0) return;
            _at[k] = now; _pos[k] = pos; _fight[k] = fighting; _holding[k] = holding; _cover[k] = cover;
            _up[k] = up; _moving[k] = moving; _call[k] = call; _sees[k] = sees && hasThreat;
            _threat[k] = threat; _health[k] = health; _mode[k] = mode;
            if (!fighting) return;
            _lastFight = now;
        }

        /// <summary>Lanes: his line of fire this Think (MercBrain.Think calls
        /// it right after Post). lane: up and aiming at to; clearing: he is
        /// stepping out of a mate's or the owner's line.</summary>
        internal void PostLane(int id, float now, bool lane, Vector3 to, bool clearing)
        {
            int k = Row(id, now, false);
            if (k < 0) return;
            _lane[k] = lane; _laneTo[k] = to; _clearing[k] = clearing;
        }

        /// <summary>The live line of fire of a mate that his body at me
        /// stands in (MercSquad.Near, LineClear wide).</summary>
        internal bool LaneOf(int self, Vector3 me, float now, out Vector3 from, out Vector3 to, out int shooter)
        {
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !_lane[k] || !Near(_pos[k], _laneTo[k], me)) continue;
                from = _pos[k]; to = _laneTo[k]; shooter = _id[k];
                return true;
            }
            from = me; to = me; shooter = -1;
            return false;
        }

        /// <summary>Is the mate shooter's body at p inside HIS line of fire
        /// (two mercs blocking each other)?</summary>
        internal bool InLineOf(int shooter, Vector3 lineFrom, Vector3 lineTo, float now)
        {
            for (int k = 0; k < Max; k++)
                if (_id[k] == shooter && now - _at[k] <= Stale) return Near(lineFrom, lineTo, _pos[k]);
            return false;
        }

        /// <summary>A mate is stepping out of a line of fire now.</summary>
        internal bool Clearing(int self, float now)
        {
            for (int k = 0; k < Max; k++) if (Live(k, self, now) && _clearing[k]) return true;
            return false;
        }

        /// <summary>A move a -> b that crosses a mate's live line of fire or
        /// the owner's held aim, or ends inside one.</summary>
        internal bool LaneHit(Vector3 a, Vector3 b, int self, float now, bool ownerAims, Vector3 aimFrom, Vector3 aimTo)
        {
            if (ownerAims && MercLane.Crosses(aimFrom, aimTo, a, b)) return true;
            for (int k = 0; k < Max; k++)
                if (Live(k, self, now) && _lane[k] && MercLane.Crosses(_pos[k], _laneTo[k], a, b)) return true;
            return false;
        }

        /// <summary>The body (a mate as posted) nearest to him along the
        /// line from -> to that blocks it.</summary>
        internal bool Blocker(Vector3 from, Vector3 to, int self, float now, out Vector3 at)
        {
            at = from;
            float best = float.MaxValue;
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !Near(from, to, _pos[k])) continue;
                float d = Flat(_pos[k] - from);
                if (d < best) { best = d; at = _pos[k]; }
            }
            return best < float.MaxValue;
        }

        /// <summary>He is gone (dismissed, dead, despawned).</summary>
        internal void Drop(int id)
        {
            for (int k = 0; k < Max; k++) if (_id[k] == id) { _id[k] = -1; _lane[k] = false; _clearing[k] = false; }
            if (_flankBy == id) _flankBy = -1;
        }

        /// <summary>Mercs in a fight now (self included when he posted).</summary>
        internal int Fighting(float now)
        {
            int n = 0;
            for (int k = 0; k < Max; k++) if (_id[k] >= 0 && now - _at[k] <= Stale && _fight[k]) n++;
            return n;
        }

        /// <summary>The team is losing: half of it is gone (dead, dropped)
        /// since the fight began, two or more at the start, or the mean
        /// health of the mercs still thinking is low. A merc who only left
        /// the fight (nothing seen) still counts.</summary>
        internal bool Losing(float now)
        {
            int n = 0;
            float health = 0f;
            bool fight = false;
            for (int k = 0; k < Max; k++)
            {
                if (_id[k] < 0 || now - _at[k] > Stale) continue;
                n++;
                health += _health[k];
                fight |= _fight[k];
            }
            if (n == 0 || !fight) return false;
            if (now - _lastFight > Reset) _peak = 0;
            if (n > _peak) _peak = n;
            if (_peak >= 2 && n * 2 <= _peak) return true;
            return health / n < LosingHealth;
        }

        /// <summary>x-merc-competence: how many mates still post (alive, thinking).</summary>
        internal int Mates(int self, float now)
        {
            int n = 0;
            for (int k = 0; k < Max; k++) if (Live(k, self, now)) n++;
            return n;
        }

        /// <summary>A mate fights here (a call-out brings him in).</summary>
        internal bool MateFighting(int self, Vector3 me, float now)
        {
            for (int k = 0; k < Max; k++)
                if (Live(k, self, now) && _fight[k] && Flat(_pos[k] - me) < CallReach) return true;
            return false;
        }

        /// <summary>A cover point (or a body) of a mate within r of p.</summary>
        internal bool Crowded(Vector3 p, int self, float r, float now)
        {
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !_fight[k]) continue;
                if (Flat(_pos[k] - p) < r) return true;
                if (_holding[k] && Flat(_cover[k] - p) < r) return true;
            }
            return false;
        }

        /// <summary>The mate who shares his cover (within r), the lower id
        /// keeps it: true when HE should move.</summary>
        internal bool Yield(Vector3 cover, int self, float r, float now)
        {
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !_fight[k] || !_holding[k]) continue;
                if (_id[k] < self && Flat(_cover[k] - cover) < r) return true;
            }
            return false;
        }

        /// <summary>A mate is up at a peek (covering).</summary>
        internal bool MateUp(int self, float now)
        {
            for (int k = 0; k < Max; k++) if (Live(k, self, now) && _up[k]) return true;
            return false;
        }

        /// <summary>A mate is running between covers or calls for cover.</summary>
        internal bool MateNeedsCover(int self, float now)
        {
            for (int k = 0; k < Max; k++) if (Live(k, self, now) && (_moving[k] || _call[k])) return true;
            return false;
        }

        /// <summary>A mate runs between covers (not only calls).</summary>
        internal bool MateMoving(int self, float now)
        {
            for (int k = 0; k < Max; k++) if (Live(k, self, now) && _moving[k]) return true;
            return false;
        }

        /// <summary>Mates in cover who could cover a move.</summary>
        internal int MatesDown(int self, float now)
        {
            int n = 0;
            for (int k = 0; k < Max; k++) if (Live(k, self, now) && _fight[k] && _holding[k] && !_moving[k]) n++;
            return n;
        }

        /// <summary>The nearest threat a mate within CallReach sees now.</summary>
        internal bool CallOut(int self, Vector3 me, float now, out Vector3 at)
        {
            at = me;
            float best = CallReach;
            bool found = false;
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !_sees[k]) continue;
                float d = Flat(_pos[k] - me);
                if (d >= best) continue;
                best = d; at = _threat[k]; found = true;
            }
            return found;
        }

        /// <summary>The mean flat direction from the threat to his mates
        /// (to flank to the other side). False without mates in a fight.</summary>
        internal bool MatesFrom(Vector3 threat, int self, float now, out Vector3 dir)
        {
            dir = Vector3.zero;
            int n = 0;
            for (int k = 0; k < Max; k++)
            {
                if (!Live(k, self, now) || !_fight[k]) continue;
                Vector3 v = _pos[k] - threat;
                v.y = 0f;
                float d = Flat(v);
                if (d < 0.5f) continue;
                dir += v / d;
                n++;
            }
            return n > 0;
        }

        /// <summary>The flank token: one flanker at a time, one flank per
        /// FlankGap seconds for the team.</summary>
        internal bool TakeFlank(int self, float now, float seconds)
        {
            if (_flankBy >= 0 && _flankBy != self && now < _flankUntil) return false;
            if (_flankBy != self && now < _nextFlank) return false;
            if (_flankBy != self) FlanksGiven++;
            _flankBy = self;
            _flankUntil = now + seconds;
            _nextFlank = now + FlankGap;
            return true;
        }

        /// <summary>A flank of the team is running (the team stays in the fight).</summary>
        internal bool Flanking(float now) { return _flankBy >= 0 && now < _flankUntil; }

        internal void EndFlank(int self, float now)
        {
            if (_flankBy != self) return;
            _flankBy = -1;
            _nextFlank = Mathf.Max(_nextFlank, now + FlankGap * 0.5f);
        }

        /// <summary>A mate's body (as posted) or the owner near the segment
        /// from -> to (flat distance under LineClear, past the first half unit and
        /// short of the last unit).</summary>
        internal bool Blocks(Vector3 from, Vector3 to, int self, float now, Vector3 owner, bool hasOwner)
        {
            if (hasOwner && Near(from, to, owner)) return true;
            for (int k = 0; k < Max; k++)
                if (Live(k, self, now) && Near(from, to, _pos[k])) return true;
            return false;
        }

        /// <summary>A body at p (feet) within LineClear of the line of fire.</summary>
        internal static bool Near(Vector3 from, Vector3 to, Vector3 p)
        {
            float ax = to.x - from.x, az = to.z - from.z;
            float len = Mathf.Sqrt(ax * ax + az * az);
            if (len < 4f) return false;
            ax /= len; az /= len;
            float px = p.x - from.x, pz = p.z - from.z;
            float t = px * ax + pz * az;
            if (t < 0.5f || t > len - 1f) return false;
            float ox = px - ax * t, oz = pz - az * t;
            return ox * ox + oz * oz < LineClear * LineClear;
        }

        static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }
    }

    /// <summary>Lanes: flat geometry of a line of fire (from -> to) and a
    /// body or a move beside it. Pure arithmetic, no allocation.</summary>
    internal static class MercLane
    {
        internal const float Goal = 5f;          // a step out ends this far from the line (1.8 m)
        internal const float MinStep = 1.5f;     // and moves him at least this far

        /// <summary>Where p is beside the line: along (units from 'from')
        /// and side (signed: + to the left of from -> to). False for a
        /// line shorter than 4 units.</summary>
        internal static bool Offset(Vector3 from, Vector3 to, Vector3 p, out float along, out float side)
        {
            float ax = to.x - from.x, az = to.z - from.z;
            float len = Mathf.Sqrt(ax * ax + az * az);
            along = 0f; side = 0f;
            if (len < 4f) return false;
            ax /= len; az /= len;
            float px = p.x - from.x, pz = p.z - from.z;
            along = px * ax + pz * az;
            side = ax * pz - az * px;
            return true;
        }

        /// <summary>The point Goal units beside the line on the side sign
        /// (+1 left, -1 right), level with p: his step out of it.</summary>
        internal static Vector3 StepOut(Vector3 from, Vector3 to, Vector3 p, float sign)
        {
            float along, side;
            if (!Offset(from, to, p, out along, out side)) return p;
            float ax = to.x - from.x, az = to.z - from.z;
            float len = Mathf.Sqrt(ax * ax + az * az);
            ax /= len; az /= len;
            // The left normal of (ax, az) is (-az, ax).
            float want = sign >= 0f ? Goal : -Goal;
            float move = want - side;
            if (Mathf.Abs(move) < MinStep) move = move >= 0f ? MinStep : -MinStep;
            return new Vector3(p.x - az * move, p.y, p.z + ax * move);
        }

        /// <summary>A move a -> b that ends inside the line of fire (within
        /// MercSquad.LineClear, MercSquad.Near) or crosses it between half a
        /// unit past the shooter and its end. A move that starts inside and
        /// leaves it does not count.</summary>
        internal static bool Crosses(Vector3 from, Vector3 to, Vector3 a, Vector3 b)
        {
            if (MercSquad.Near(from, to, b)) return true;
            float alongA, sideA, alongB, sideB;
            if (!Offset(from, to, a, out alongA, out sideA) || !Offset(from, to, b, out alongB, out sideB)) return false;
            if (MercSquad.Near(from, to, a)) return false;
            if ((sideA > 0f) == (sideB > 0f)) return false;
            // Where the move crosses the line, measured along it.
            float t = sideA / (sideA - sideB);
            float at = alongA + (alongB - alongA) * t;
            float len = Flat(to - from);
            return at >= 0.5f && at <= len - 1f;
        }

        static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }
    }
}

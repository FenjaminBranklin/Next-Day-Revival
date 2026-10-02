// merc-attack-orders, the pure half: the ATTACK corridor, one merc's run
// through the order and the group's shared state.
//
//   CORRIDOR   origin (where the attackers stood) -> objective. Merc k of n
//              has his own lane abreast (7 m apart) and his own hold spot and
//              watch sector at the objective - never one point for all.
//   ADVANCE    no contact in the group: the line sprints its lanes in steps of
//              30 m, nobody more than 14 m ahead of the slowest. Contact in
//              the last 15 s (or a hit): fire and movement - the odd or the
//              even half runs a bound 30 m past the others while they fight
//              or watch low, then the halves swap. Whoever has a target in
//              sight fires; while a mate has the enemy in sight, a runner
//              without a line of fire leaves the fight to that cover and runs
//              on to a position he can fire from. A single merc fights, then
//              advances.
//   FIGHT      the M2/M3 brain (with the merc-combat-response opening burst
//              and lane clearance) takes over for any visible enemy, even
//              outside the corridor. An unseen, safe new order starts moving
//              immediately in short M1 cover hops. Two fights without a round start an
//              8 s push in which only a target in sight (or a hit) stops him:
//              no advance/cover cycle without shots.
//   SEARCH     a fight that saw its threat ends in a short local search:
//              at most 20 m toward the last sighting, inside the corridor,
//              12 s, then the advance or the hold resumes.
//   CLEAR      at the objective: low at his spot, sweeping his sector. After
//              every active member holds and nobody fought for 10 s, marks
//              at least 100 m away continue in 30 m legs on the same heading.
//              Shorter attacks retain the original hold behavior.
//   STALLED    no progress for 2 x 20 s of stepping, or the step budget
//              (120 s + 0.5 s per unit of corridor) spent: he holds where he
//              is and the owner is told. Never an endless run, never a warp.
//
// Units are game units (1 m = 2.8 u). No Unity call beyond Vector3 values
// and no allocation after construction: research/merc_attack_check.py runs
// it offline under the production adapter's inputs (Revival.MercAttack.cs).
// C# 3.0, ASCII only.
using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Y S2: merc-only contact timing and short, immediate advances.</summary>
    internal static class MercAssault
    {
        internal const float Hop = 24f;           // 8.6 m between cover decisions
        internal const float Precise = 2f;        // 0.7 m at the assigned hold spot
        internal const float CoverEvery = 0.5f;   // at most 2 cover queries/s per mover
        internal const float ProgressGain = 5.6f; // 2 m forward; a lateral peek is not progress
        internal const float CoverLimit = 6f;     // healthy attackers must leave stagnant cover
        internal static float ScanGap(float jitter) { return 0.125f + jitter * 0.025f; }
        internal static float LosGap(float jitter) { return 0.15f + jitter * 0.03f; }
        internal static float Reach(float weapon, float ordinary)
        { return Math.Max(weapon * 1.2f, ordinary * 1.4f); }
        internal static float React(float grade) { return 0.32f - Math.Max(0f, Math.Min(1f, grade)) * 0.12f; }

        internal static Vector3 HopTo(Vector3 me, Vector3 goal)
        {
            float d = MercAttackGeo.Flat(goal - me);
            return d <= Hop ? goal : me + (goal - me) * (Hop / d);
        }
    }

    /// <summary>ATTACK geometry on the ground plane.</summary>
    internal static class MercAttackGeo
    {
        internal const float Spacing = 19.6f;       // 7 m between neighbours abreast
        internal const float CorridorMin = 84f;     // at least 30 m either side of the line
        internal const float Immediate = 112f;      // 40 m: answered anywhere
        internal const float Bound = 84f;           // 30 m: one bound / one advance step
        internal const float Arrive = MercAssault.Precise;
        internal const float Lead = 39.2f;          // 14 m: the line keeps together
        internal const float SectorDeg = 25f;       // watch sectors fan out by this

        internal static float Flat(Vector3 v) { return (float)Math.Sqrt(v.x * v.x + v.z * v.z); }

        /// <summary>Unit direction origin -> objective (the order's Facing).</summary>
        internal static Vector3 Dir(MercOrder o)
        {
            Vector3 d = o.Facing;
            float m = Flat(d);
            if (m < 0.01f) { d = o.Centre - o.Origin; m = Flat(d); }
            return m < 0.01f ? new Vector3(0f, 0f, 1f) : new Vector3(d.x / m, 0f, d.z / m);
        }

        internal static Vector3 Side(Vector3 dir) { return new Vector3(dir.z, 0f, -dir.x); }

        internal static float Length(MercOrder o) { return Flat(o.Centre - o.Origin); }

        internal static bool Continues(MercOrder o) { return Length(o) >= 280f; }

        /// <summary>Merc k of n: his offset across the line, the group centred.</summary>
        internal static float Lateral(int k, int n)
        {
            return n <= 1 ? 0f : (k - (n - 1) * 0.5f) * Spacing;
        }

        /// <summary>Half the corridor's width: the hold circle, and never less
        /// than 30 m past the outermost lane.</summary>
        internal static float HalfWidth(MercOrder o)
        {
            return Math.Max(o.RadiusUnits, CorridorMin + Math.Abs(Lateral(0, o.N)));
        }

        /// <summary>How far along the line p is (0 at the origin, Length at
        /// the objective). Long attacks may continue beyond the mark.</summary>
        internal static float Along(MercOrder o, Vector3 p)
        {
            Vector3 d = Dir(o);
            Vector3 a = p - o.Origin;
            float t = a.x * d.x + a.z * d.z;
            float len = Length(o);
            return t < 0f ? 0f : !Continues(o) && t > len ? len : t;
        }

        /// <summary>Distance from p to the corridor's centre line (the segment).</summary>
        internal static float Off(MercOrder o, Vector3 p)
        {
            Vector3 d = Dir(o);
            Vector3 q = o.Origin + d * Along(o, p);
            return Flat(p - q);
        }

        internal static bool Inside(MercOrder o, Vector3 p, float margin)
        {
            return Off(o, p) <= HalfWidth(o) + margin;
        }

        /// <summary>His lane at a distance along the line.</summary>
        internal static Vector3 Lane(MercOrder o, float along)
        {
            Vector3 d = Dir(o);
            float len = Length(o);
            along = along < 0f ? 0f : !Continues(o) && along > len ? len : along;
            Vector3 p = o.Origin + d * along + Side(d) * Lateral(o.K, o.N);
            p.y = o.Origin.y + (o.Centre.y - o.Origin.y) * (len < 0.01f ? 1f : Math.Min(1f, along / len));
            return p;
        }

        /// <summary>His spot at the objective: his lane across it (inside the
        /// hold circle), the odd ones a few metres further back.</summary>
        internal static Vector3 Hold(MercOrder o)
        {
            Vector3 d = Dir(o);
            float lat = Lateral(o.K, o.N);
            float lim = o.RadiusUnits * 0.8f;
            if (lat > lim) lat = lim; else if (lat < -lim) lat = -lim;
            float back = (o.K & 1) == 1 ? Spacing * 0.5f : 0f;
            Vector3 p = o.Centre - d * back + Side(d) * lat;
            p.y = o.Centre.y;
            return p;
        }

        /// <summary>His watch sector at the objective: the attack direction
        /// turned by his place in the group.</summary>
        internal static Vector3 HoldFacing(MercOrder o)
        {
            Vector3 d = Dir(o);
            float a = (o.N <= 1 ? 0f : (o.K - (o.N - 1) * 0.5f) * SectorDeg) * (float)(Math.PI / 180.0);
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new Vector3(d.x * c + d.z * s, 0f, -d.x * s + d.z * c);
        }
    }

    /// <summary>What the adapter reads for one merc (no allocation).</summary>
    internal struct MercAttackIn
    {
        public float Now;
        public Vector3 Me;
        public bool Fighting;           // the M2 brain runs a fight
        public int Shots;               // his rounds so far (a running count)
        public int Hits;                // hits taken (a running count)
        public bool Target, Sees;       // he has a target / a line of fire to it
        public Vector3 TargetAt;
        public bool Danger;             // a grenade or blast beside him
        public bool Protected;          // low HP, reload or exposure: M2/M3 keeps control
        public bool Maintenance;        // actual injury/reload/medicine; pressure still permits bounds
    }

    /// <summary>What he does out of a fight until the next step.</summary>
    internal struct MercAttackAct
    {
        public byte Act;                // MoveTo / HoldHere
        public Vector3 Dest;            // MoveTo
        public bool Run;
        public Vector3 Face;            // HoldHere: a direction
        public bool Low;
        public float Sweep;             // HoldHere: watch sweep in degrees (0 = face)

        internal const byte MoveTo = 1, HoldHere = 2;
    }

    /// <summary>The group's state, shared by the copies of one ATTACK order
    /// (MercOrder.Team). Rows by group index k.</summary>
    internal sealed class MercAttackTeam
    {
        internal const int Max = 10;
        internal const float Active = 3f;           // a row older than this is gone (dead, despawned)
        internal const float FireActive = 0.6f;     // stopped/dead/reloading men cannot pin the covering pair
        internal const float ContactKeep = 15f;
        internal const float ClearSeconds = 10f;
        internal const float StuckSeconds = 10f;    // no progress this long: he no longer holds the line back

        internal readonly int Size;
        readonly float[] _along = new float[Max];
        readonly float[] _seen = new float[Max];
        readonly byte[] _phase = new byte[Max];
        readonly bool[] _arrived = new bool[Max];
        readonly float[] _mark = new float[Max];     // along at his last progress
        readonly float[] _progAt = new float[Max];
        readonly float[] _coverAt = new float[Max]; // real covering fire, not just a sighting
        readonly bool[] _marksman = new bool[Max];
        readonly bool[] _ready = new bool[Max];
        int _coverStart;
        internal float LastContact = -1000f;
        internal float LastSight = -1000f;          // a member had a target in sight (covering fire is possible)
        internal int Moving;                        // alternating priority for the next covering pair
        internal int Swaps;
        internal bool Complete, CompleteSaid;
        internal bool Cleared;                      // original objective cleared once; order keeps running
        internal float CompleteAt = -1f;

        internal MercAttackTeam(int size)
        {
            Size = Math.Max(1, Math.Min(Max, size));
            for (int k = 0; k < Max; k++) { _seen[k] = -1000f; _coverAt[k] = -1000f; _ready[k] = true; }
        }

        internal bool Contact(float now) { return now - LastContact < ContactKeep; }

        internal void Report(int k, float now, float along, byte phase, bool contact, bool sight)
        {
            if (k < 0 || k >= Max) return;
            if (now - _seen[k] > Active || along > _mark[k] + 4f || along < _mark[k] - 20f) { _mark[k] = along; _progAt[k] = now; }
            _along[k] = along; _seen[k] = now; _phase[k] = phase;
            if (contact)
            {
                LastContact = now;
            }
            if (sight) LastSight = now;
            // Complete: every active member holds, nobody fought for a while.
            bool all = true;
            int active = 0;
            for (int i = 0; i < Size; i++)
            {
                if (now - _seen[i] > Active) continue;
                active++;
                if (_phase[i] != MercAttackRun.Holding) { all = false; break; }
            }
            bool done = all && active > 0 && now - LastContact >= ClearSeconds;
            if (done && !Complete) CompleteAt = now;
            Complete = done;
            if (done) Cleared = true;
        }

        /// <summary>The slowest active member still on his way (along); one
        /// who made no progress for StuckSeconds does not hold the line back.</summary>
        internal float Rear(float now, float fallback)
        {
            float rear = float.MaxValue;
            for (int i = 0; i < Size; i++)
            {
                if (_marksman[i] || now - _seen[i] > Active || now - _progAt[i] > StuckSeconds) continue;
                byte p = _phase[i];
                if (p == MercAttackRun.Holding || p == MercAttackRun.Stalled) continue;
                if (_along[i] < rear) rear = _along[i];
            }
            return rear == float.MaxValue ? fallback : rear;
        }

        /// <summary>The front of the watching half (-1: none active).</summary>
        internal float WatchFront(float now)
        {
            float front = -1f;
            for (int i = 0; i < Size; i++)
            {
                if (!Coverer(i, now) || _marksman[i] || now - _seen[i] > Active) continue;
                if (_along[i] > front) front = _along[i];
            }
            return front;
        }

        internal void Arrived(int k) { if (k >= 0 && k < Max) _arrived[k] = true; }

        internal void Station(int k, bool marksman, bool ready)
        {
            if (k < 0 || k >= Size) return;
            _marksman[k] = marksman; _ready[k] = ready;
            if (!ready) _coverAt[k] = -1000f;
        }

        // Two cover, the others advance. A pair uses one coverer; an isolated
        // fighter supplies his own walking fire. Rear marksmen keep overwatch.
        internal bool Coverer(int k, float now)
        {
            if (k < 0 || k >= Size || !_ready[k] || now - _seen[k] > FireActive) return false;
            if (_marksman[k]) return true;
            int active = 0, marksmen = 0;
            for (int n = 0; n < Size; n++)
                if (_ready[n] && now - _seen[n] <= FireActive) { active++; if (_marksman[n]) marksmen++; }
            int seats = Math.Min(2, active - 1) - marksmen;
            for (int pass = 0; pass < 2 && seats > 0; pass++)
                for (int n = 0; n < Size; n++)
                {
                    int slot = (_coverStart + n) % Size;
                    if (_marksman[slot] || !_ready[slot] || now - _seen[slot] > FireActive
                        || (((slot & 1) != Moving) != (pass == 0))) continue;
                    if (slot == k) return true;
                    if (--seats == 0) break;
                }
            return false;
        }

        internal void Covering(int k, float now, bool firing)
        {
            if (k < 0 || k >= Size) return;
            _coverAt[k] = firing ? now : -1000f;
        }

        internal bool Covered(int k, float now)
        {
            int firing = 0;
            for (int i = 0; i < Size; i++)
                if (i != k && Coverer(i, now) && now - _seen[i] < Active && now - _coverAt[i] < 0.6f) firing++;
            return firing > 0 && firing >= Math.Min(2, ReadyCount(now) - 1);
        }

        internal int ReadyCount(float now)
        {
            int count = 0;
            for (int i = 0; i < Size; i++) if (_ready[i] && now - _seen[i] <= FireActive) count++;
            return count;
        }

        internal bool Front(float now, out float along)
        {
            along = 0f; bool found = false;
            for (int i = 0; i < Size; i++)
                if (!_marksman[i] && now - _seen[i] <= Active)
                { along = found ? Math.Max(along, _along[i]) : _along[i]; found = true; }
            return found;
        }

        internal bool HasCover(float now)
        {
            int firing = 0;
            for (int i = 0; i < Size; i++)
                if (Coverer(i, now) && now - _seen[i] < Active && now - _coverAt[i] < 0.6f) firing++;
            return firing > 0 && firing >= Math.Min(2, ReadyCount(now) - 1);
        }

        /// <summary>Is k outside the covering pair under contact, not at
        /// his bound yet, with an active mate to cover him while someone has
        /// the enemy in sight?</summary>
        internal bool Runner(int k, float now)
        {
            if (Size < 2 || k < 0 || k >= Size || !_ready[k] || !Contact(now) || now - LastSight > 2f || Coverer(k, now) || _arrived[k]) return false;
            byte p = _phase[k];
            if (p == MercAttackRun.Holding || p == MercAttackRun.Stalled || p == MercAttackRun.Search) return false;
            return Covered(k, now);
        }

        /// <summary>Swap on arrival or lost mobility, never a fixed timer.</summary>
        internal void MaybeSwap(float now)
        {
            if (Size < 2) return;
            bool all = true;
            int runners = 0;
            for (int i = 0; i < Size; i++)
            {
                if (Coverer(i, now) || _marksman[i] || now - _seen[i] > Active || !_ready[i]) continue;
                byte p = _phase[i];
                if (p == MercAttackRun.Holding || p == MercAttackRun.Stalled) continue;
                runners++;
                if (now - _progAt[i] > 1.2f && now - _seen[i] < 0.6f) continue;
                if (!_arrived[i]) { all = false; break; }
            }
            if (!all || runners == 0 || !Contact(now)) return;
            Moving ^= 1;
            _coverStart = (_coverStart + 2) % Size;
            Swaps++;
            for (int i = 0; i < Max; i++) { _arrived[i] = false; _progAt[i] = now; _mark[i] = _along[i]; }
        }
    }

    /// <summary>One merc's run through an ATTACK order.</summary>
    internal sealed class MercAttackRun
    {
        internal const byte Idle = 0, Advance = 1, Overwatch = 2, Search = 3, Holding = 4, Stalled = 5;
        static readonly string[] Names = { "-", "ADVANCE", "OVERWATCH", "SEARCH", "HOLD", "STALLED" };
        internal static string Name(byte p) { return p < Names.Length ? Names[p] : "?"; }

        internal const float StallSeconds = 20f;
        internal const int StallsToStop = 2;
        internal const float PushSeconds = 8f;
        internal const float SearchSeconds = 12f;
        internal const float SearchReach = 56f;     // 20 m
        internal const float LookSeconds = 4f;
        internal const float HitKeep = 3f;
        internal const float LaunchSeconds = 0.8f; // unseen old fights must not delay a new attack

        // News for the owner (the adapter toasts and clears them).
        internal const byte NewsArrived = 1, NewsStalled = 2, NewsComplete = 4;

        internal byte Phase;
        internal MercOrder For;
        internal Vector3 HoldAt, HoldFace;
        internal bool Grounded;          // the adapter put HoldAt on the ground (once per order)
        internal Vector3 MoveAt;         // adapter's cached M1 cover waypoint
        internal bool HaveMove, MoveCovered;
        internal float NextCover;
        internal byte News;
        // Counters (F8, the offline check).
        internal int Fights, DryFights, Pushes, Searches, Stalls, Steps, Bounds;

        float _best, _bestAt, _lastStep, _stepTime, _budget;
        int _stalls;
        bool _wasFighting, _haveThreat, _arrivedOnce, _primed, _leftForBound;
        int _shotsAtStart, _hits, _dry;
        float _fightStart, _threatAt, _hitAt = -1000f, _pushUntil, _searchUntil, _lookUntil;
        float _launchUntil;
        float _advanceAt, _advanceMark, _motionAt, _extension;
        Vector3 _threat, _searchAt;
        byte _resume;

        internal bool Pushing(float now) { return now < _pushUntil; }
        internal Vector3 SearchAt { get { return _searchAt; } }
        internal float ForwardFloor { get { return _advanceMark; } }

        internal void Reset()
        {
            Phase = Idle; For = null; News = 0; _primed = false; _leftForBound = false;
            _stalls = 0; _dry = 0; _wasFighting = false; _haveThreat = false; _arrivedOnce = false;
            _pushUntil = 0f; _searchUntil = 0f; _lookUntil = 0f; _lastStep = 0f; _stepTime = 0f;
            HaveMove = false; MoveCovered = false; NextCover = 0f;
            _launchUntil = 0f;
            _advanceAt = _motionAt = _extension = 0f;
            _threatAt = _hitAt = -1000f;
        }

        /// <summary>A new order (or the first step of one): his hold spot and
        /// sector, the step budget. The adapter grounds HoldAt once (Grounded).</summary>
        internal void Begin(MercOrder o, float now, Vector3 me)
        {
            Reset();
            For = o;
            HoldAt = MercAttackGeo.Hold(o);
            HoldFace = MercAttackGeo.HoldFacing(o);
            Grounded = false;
            Phase = Advance;
            _bestAt = 0f;
            _budget = 120f + 0.5f * MercAttackGeo.Length(o);
            _launchUntil = now + LaunchSeconds;
            _advanceAt = _motionAt = now;
            _advanceMark = MercAttackGeo.Along(o, me);
        }

        MercAttackTeam Team(MercOrder o) { return o.Team as MercAttackTeam; }

        /// <summary>Every Think (MercMayStand) and every step: the fight
        /// bookkeeping, the group report, and whether the brain may fight
        /// where he is.</summary>
        internal bool Gate(MercOrder o, ref MercAttackIn i)
        {
            float now = i.Now;
            if (For != o) Begin(o, now, i.Me);
            // Count wall-clock time in the fight as well as order steps. Peeks
            // and shots cannot renew the lease on the same patch of ground.
            float motionDt = Math.Max(0f, Math.Min(0.5f, now - _motionAt));
            _motionAt = now;
            if (i.Maintenance || i.Danger) _advanceAt += motionDt;
            float advance = MercAttackGeo.Along(o, i.Me);
            if (advance >= _advanceMark + MercAssault.ProgressGain)
            { _advanceMark = advance; _advanceAt = now; }
            // The running counts from before this order are not news.
            if (!_primed) { _primed = true; _hits = i.Hits; _shotsAtStart = i.Shots; _wasFighting = i.Fighting; }
            if (i.Hits != _hits) { _hits = i.Hits; _hitAt = now; _dry = 0; }
            if (i.Target && i.Sees) { _threat = i.TargetAt; _threatAt = now; _haveThreat = true; }
            if (i.Fighting && !_wasFighting)
            {
                Fights++;
                _shotsAtStart = i.Shots;
                _fightStart = now;
            }
            else if (!i.Fighting && _wasFighting) FightOver(o, ref i);
            _wasFighting = i.Fighting;
            MercAttackTeam team = Team(o);
            if (team != null)
                team.Report(o.K, now, MercAttackGeo.Along(o, i.Me), Phase, i.Fighting || now - _hitAt < HitKeep, i.Target && i.Sees);

            bool hit = now - _hitAt < HitKeep;
            // A visible hostile always interrupts the advance, even outside
            // the corridor and beyond the old 40 m gate. Safety shapes the fight.
            if ((i.Target && i.Sees) || i.Danger || i.Protected || hit) return true;
            if (now < _launchUntil)
            {
                if (i.Fighting) _leftForBound = true; // not a dry fight or a search
                return false;
            }
            // Fire and movement: whoever has a target in sight shoots (2.5 s
            // of hysteresis); a runner without a line of fire leaves the
            // fight to his mates' actual covering fire and runs his bound.
            // Hits and protected states keep the safety loop above in control.
            bool sighted = i.Target && (i.Sees || (i.Fighting && now - _threatAt < 2.5f));
            if (!sighted && team != null && team.Runner(o.K, now) && MercAttackGeo.Inside(o, i.Me, 20f))
            {
                if (i.Fighting && !_leftForBound) { _leftForBound = true; Bounds++; }
                return false;
            }
            // Outside the corridor only the immediate threat is answered: the
            // way back in comes first.
            if (!MercAttackGeo.Inside(o, i.Me, 20f)) return false;
            // A push: only a target in sight stops him.
            if (now < _pushUntil && !(i.Target && i.Sees)) return false;
            return true;
        }

        internal bool NeedsBound(ref MercAttackIn i)
        {
            return Phase != Holding && Phase != Stalled && !i.Maintenance && !i.Danger
                && i.Now - _advanceAt >= MercAssault.CoverLimit + (For.K % 3) * 0.4f;
        }

        void FightOver(MercOrder o, ref MercAttackIn i)
        {
            float now = i.Now;
            // Left for a bound: no dry fight, no search - he runs.
            if (_leftForBound) { _leftForBound = false; _haveThreat = false; return; }
            bool dry = i.Shots == _shotsAtStart;
            if (dry)
            {
                DryFights++;
                if (++_dry >= 2) { _dry = 0; _pushUntil = now + PushSeconds; Pushes++; }
            }
            else _dry = 0;
            // A threat seen in this fight and lost: a short local search.
            if (_haveThreat && _threatAt >= _fightStart && Phase != Stalled && !MercAttackGeo.Continues(o)
                && MercAttackGeo.Inside(o, _threat, MercAttackGeo.Immediate))
            {
                Vector3 to = _threat - i.Me;
                to.y = 0f;
                float d = MercAttackGeo.Flat(to);
                float go = Math.Min(Math.Max(0f, d - 28f), SearchReach);
                Vector3 at = go < 4f ? i.Me : i.Me + to * (go / d);
                // Kept inside the corridor.
                float off = MercAttackGeo.Off(o, at), half = MercAttackGeo.HalfWidth(o);
                if (off > half)
                {
                    Vector3 q = o.Origin + MercAttackGeo.Dir(o) * MercAttackGeo.Along(o, at);
                    at = q + (at - q) * (half / off);
                }
                _searchAt = at;
                _resume = Phase == Search ? _resume : Phase;
                Phase = Search;
                _searchUntil = now + SearchSeconds;
                _lookUntil = 0f;
                Searches++;
            }
            _haveThreat = false;
        }

        /// <summary>Out of a fight: where he goes or how he holds.</summary>
        internal void Step(MercOrder o, ref MercAttackIn i, out MercAttackAct a)
        {
            a = new MercAttackAct();
            float now = i.Now;
            if (For != o) Begin(o, now, i.Me);
            // Fight time is not step time: the stall clock and the budget
            // stand still while the brain had him.
            float dt = _lastStep > 0f ? now - _lastStep : 0f;
            if (dt > 0.5f) { if (_bestAt > 0f) _bestAt += dt; dt = 0f; }
            _lastStep = now;
            Steps++;
            Vector3 me = i.Me;
            Vector3 dir = MercAttackGeo.Dir(o);
            float along = MercAttackGeo.Along(o, me);
            float len = MercAttackGeo.Length(o);
            MercAttackTeam team = Team(o);

            // A long attack clears its selected mark, then pushes successive
            // legs on the immutable order heading. The mark stays selectable.
            bool cleared = team != null ? team.Cleared
                : Phase == Holding && now - _threatAt >= MercAttackTeam.ClearSeconds
                    && now - _hitAt >= MercAttackTeam.ClearSeconds && !i.Fighting;
            if (MercAttackGeo.Continues(o) && cleared
                && (_extension == 0f || MercAttackGeo.Flat(HoldAt - me) <= MercAttackGeo.Arrive))
            {
                _extension += MercAttackGeo.Bound;
                HoldAt += dir * MercAttackGeo.Bound;
                Phase = Advance; HaveMove = false; MoveCovered = false;
                _bestAt = 0f; _stepTime = 0f; _stalls = 0;
                _advanceAt = now; _advanceMark = along;
                if (team == null || !team.CompleteSaid)
                { News |= NewsComplete; if (team != null) team.CompleteSaid = true; }
            }
            len += _extension;

            if (Phase == Search)
            {
                if (now >= _searchUntil) Phase = _resume == Holding ? Holding : Advance;
                else if (_lookUntil <= 0f && MercAttackGeo.Flat(_searchAt - me) > MercAttackGeo.Arrive)
                {
                    a.Act = MercAttackAct.MoveTo; a.Dest = _searchAt; a.Run = false;
                    return;
                }
                else
                {
                    if (_lookUntil <= 0f) _lookUntil = now + LookSeconds;
                    if (now < _lookUntil)
                    {
                        Hold(ref a, _threat - me, true, 35f);
                        return;
                    }
                    Phase = _resume == Holding ? Holding : Advance;
                }
            }
            if (Phase == Stalled)
            {
                Hold(ref a, dir, true, 45f);
                return;
            }
            if (Phase == Holding)
            {
                // Pulled off his spot by a fight or a search: back to it.
                if (MercAttackGeo.Flat(HoldAt - me) > MercAttackGeo.Arrive)
                {
                    a.Act = MercAttackAct.MoveTo; a.Dest = HoldAt; a.Run = false;
                    return;
                }
                Hold(ref a, HoldFace, true, 40f);
                if (team != null && team.Complete && !team.CompleteSaid) { team.CompleteSaid = true; News |= NewsComplete; }
                return;
            }

            // ADVANCE / OVERWATCH. Arrived at his spot: hold.
            if (MercAttackGeo.Flat(HoldAt - me) <= MercAttackGeo.Arrive)
            {
                Phase = Holding;
                if (!_arrivedOnce) { _arrivedOnce = true; News |= NewsArrived; }
                Hold(ref a, HoldFace, true, 40f);
                return;
            }
            _stepTime += dt;
            bool contact = team != null ? team.Contact(now) : now - _hitAt < MercAttackTeam.ContactKeep;
            float goalAlong;
            bool run;
            bool wait = false;
            bool mustMove = NeedsBound(ref i);
            if (team != null && team.Size >= 2 && contact && team.HasCover(now))
            {
                // Bounding overwatch.
                team.MaybeSwap(now);
                if (team.Coverer(o.K, now) && !mustMove)
                {
                    Phase = Overwatch;
                    Hold(ref a, dir, true, 30f);
                    Progress(me, now, true);
                    return;
                }
                float front = team.WatchFront(now);
                float boundFrom = front < 0f || mustMove ? Math.Max(along, front) : front;
                goalAlong = Math.Min(len, boundFrom + MercAttackGeo.Bound);
                // At his bound (the last one ends at his hold spot, above).
                if (goalAlong < len - MercAttackGeo.Arrive && along >= goalAlong - MercAttackGeo.Arrive && !mustMove)
                {
                    team.Arrived(o.K);
                    Phase = Overwatch;
                    Hold(ref a, dir, true, 30f);
                    Progress(me, now, true);
                    return;
                }
                run = true;
            }
            else
            {
                // The line walks its lanes together.
                float rear = team != null ? team.Rear(now, along) : along;
                wait = !mustMove && team != null && along > rear + MercAttackGeo.Lead && along < len - MercAttackGeo.Arrive;
                goalAlong = Math.Min(len, along + MercAttackGeo.Bound);
                run = true; // execute immediately; cover waypoints shape the route
            }
            Phase = Advance;
            if (wait)
            {
                Hold(ref a, dir, false, 0f);
                Progress(me, now, true);
                return;
            }
            a.Act = MercAttackAct.MoveTo;
            a.Dest = goalAlong >= len - MercAttackGeo.Arrive ? HoldAt : MercAttackGeo.Lane(o, goalAlong);
            a.Run = run;
            Progress(me, now, false);
            if (Phase == Stalled) Hold(ref a, dir, true, 45f);
        }

        /// <summary>The stall clock: 2 m closer to his hold spot (along his
        /// lane or across at the end) resets it; waiting does not count.</summary>
        void Progress(Vector3 me, float now, bool waiting)
        {
            float left = MercAttackGeo.Flat(HoldAt - me);
            if (_bestAt <= 0f) { _bestAt = now; _best = left; }
            if (left < _best - 5f) { _best = left; _bestAt = now; _stalls = 0; }
            else if (waiting) _bestAt = Math.Max(_bestAt, now - StallSeconds * 0.5f);
            else if (now - _bestAt > StallSeconds)
            {
                _stalls++; Stalls++;
                _bestAt = now;
                if (_stalls >= StallsToStop) StallHere();
            }
            if (!waiting && _stepTime > _budget) StallHere();
        }

        void StallHere()
        {
            if (Phase == Stalled) return;
            Phase = Stalled;
            News |= NewsStalled;
        }

        static void Hold(ref MercAttackAct a, Vector3 face, bool low, float sweep)
        {
            a.Act = MercAttackAct.HoldHere; a.Face = face; a.Low = low; a.Sweep = sweep;
        }

        /// <summary>Where his cover may be: around his hold spot at the
        /// objective, else the corridor near him.</summary>
        internal void Leash(MercOrder o, Vector3 me, out Vector3 centre, out float radius)
        {
            if (Phase == Holding || Phase == Stalled)
            {
                centre = Phase == Holding ? HoldAt : me;
                radius = o.RadiusUnits + 28f;
                return;
            }
            centre = o.Origin + MercAttackGeo.Dir(o) * MercAttackGeo.Along(o, me);
            radius = MercAttackGeo.HalfWidth(o);
        }
    }
}

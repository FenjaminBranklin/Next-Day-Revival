// i-m3-merc-advance-oscillation, the pure half: ONE owner of a merc's move
// target per tick, the hold between owners, the still-in-open watchdog,
// fire continuity across path changes, spacing on a mark and the change log.
//
// The writers (census, 6.71.0; the per-tick chain is NpcCombat RunGround):
//   Rescue / Tower / Resupply / Station duties   whole-tick, before the fight
//   M2 brain (MercFight)                         cover dash, peek, evade, fire
//                                                in the open, AttackFire walk
//   ATTACK gate (MercAttackRun.Gate, MayFight)   turned the brain off and on
//   MercStep: CloseIn (c-m2), ATTACK run lanes/bounds/standoff (e-m1, h-m1),
//             FOLLOW / catch-up / halt cover, STAY, PATROL, posts
//   K2 stuck recovery (NavigationStep, detour inside OrderMove)
//   vanilla NPC_AI2 idle think (held off by Quiet)
// The pair that fought: the ATTACK gate flipped MayFight with sight; the
// brain left at once (no grace on MayFight), MercFightEnd stopped the
// walking-fire pose, the run sprinted on (no fire), sight came back, the
// brain picked a cover behind him: forward, abort, loop back, forward. And
// CloseIn walked to a target while the run walked the lane. Go() released
// the aim on every new path.
//
// Priority: FALLBACK (health or heavy fire) > ORDER mark (ATTACK, move-to)
// > FIGHT (engaged brain) > REGROUP (follow, stay, patrol, close-in). An
// owner holds at least Hold s against the other; a new mark and a fall-back
// pre-empt. C# 3.0, Unity-free (the offline checks compile this file).
using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Owner layers, lowest first.</summary>
    internal static class MercLayer
    {
        internal const byte None = 0, Regroup = 1, Fight = 2, Order = 3, Fallback = 4;
        static readonly string[] Names = { "none", "REGROUP", "FIGHT", "ORDER", "FALLBACK" };
        internal static string Name(byte l) { return l < Names.Length ? Names[l] : "?"; }
    }

    /// <summary>The system that set the move target (for the log).</summary>
    internal static class MercWriter
    {
        internal const byte None = 0, AttackRun = 1, Overwatch = 2, MoveOrder = 3, CloseIn = 4, Follow = 5,
            CatchUp = 6, HaltCover = 7, Stay = 8, Patrol = 9, Post = 10, Desert = 11, Brain = 12,
            OpenCover = 13, ForwardCover = 14, Forward = 15, Duty = 16, Board = 17;
        static readonly string[] Names = { "none", "attack-run", "marksman-overwatch", "move-order", "close-in",
            "follow", "catch-up", "halt-cover", "stay", "patrol", "post", "desert", "brain", "open-still-cover",
            "forward-cover", "forward-step", "duty", "board" };
        internal static string Name(byte w) { return w < Names.Length ? Names[w] : "?"; }
    }

    /// <summary>Why a merc stopped firing (the log).</summary>
    internal static class MercFireStop
    {
        internal const byte None = 0, Reload = 1, NoTarget = 2, NoSight = 3, Range = 4, Friend = 5, Muzzle = 6,
            Pose = 7, Sprint = 8, Hold = 9, Medicine = 10, Danger = 11, Survive = 12, Hurt = 13, Stance = 14;
        static readonly string[] Names = { "none", "reload", "target lost", "no line of sight", "out of range",
            "friend in the line", "muzzle blocked", "walking-fire pose not ready", "order sprint (no pose)",
            "brain holds fire", "medicine", "danger (grenade)", "survive order", "badly hurt", "stance change" };
        internal static string Name(byte r) { return r < Names.Length ? Names[r] : "?"; }
    }

    /// <summary>One merc's tick as the arbiter sees it (no allocation).</summary>
    internal struct MercOwnerIn
    {
        public float Now;
        public Vector3 Me;
        public object Order;        // identity: a new object is a new order
        public bool Mark;           // ATTACK or a move-to mark: the ORDER layer
        public Vector3 Heading;     // flat unit direction of the mark (zero: none)
        public bool RawMayFight;    // the order's own gate (MercMayEngage && MercMayStand)
        public float Health;        // 0..1
        public float Pressure;      // suppression
        public int Hits;            // running hit count
        public bool Contact;        // a known or seen hostile, or hit lately
        public bool HasEnemy;
        public Vector3 EnemyAt;
        public bool InCover;        // at the brain's cover / a claimed point
        public bool Busy;           // reloading or dressing a wound: not a stand to fix
    }

    internal sealed class MercMoveOwner
    {
        internal const float Hold = 3f;            // an owner keeps the target this long against the other
        internal const float MarkHold = 3f;        // a new mark: everyone runs, the fight waits this long
        internal const float OpenStill = 1.5f;     // still in the open in contact this long: to cover (< 2 s)
        internal const float StillMove = 1.4f;     // 0.5 m: less than this is standing still
        internal const float CoverReach = 42f;     // 15 m: the cover he takes in contact
        internal const float CoverCommit = 4f;     // a watchdog/redirect cover run lasts this long at most
        internal const float StepCommit = 1.5f;    // a forward step (no cover) this long, then look again
        internal const float StepLength = 14f;     // 5 m
        internal const float BackSlack = 5.6f;     // 2 m back along the mark is not a reversal
        internal const float InCoverReach = 5.6f;  // 2 m from a cover point is in it
        internal const float MinSpread = 14f;      // 5 m between two moving mercs
        internal const float SpreadCap = 16.8f;    // a target is pushed aside at most 6 m
        internal const float FireKeep = 1.5f;      // a shot this recent: the fire state carries over a new path (= MercMoveShootPolicy.FireKeep)
        internal const float FallbackHealth = 0.5f, HeavyPressure = 0.8f, HitBurst = 1.5f;
        internal const float LogGap = 0.5f;        // one line per merc per this at most (changes coalesce)
        internal const float TurnCos = 0.5f;       // same writer: a turn over 60 deg is a change
        internal const float Metre = 2.8f;

        /// <summary>Verdicts of Decide.</summary>
        internal const byte KeepFight = 0, ToOrder = 1, ToCover = 2, Fallback = 3;

        internal MercOwnerIn In;                   // this tick's view (the adapter fills it)
        internal bool FallbackNow;                 // FallbackDue, once per tick
        internal byte Step;                        // the order-side writer running this tick
        internal string StepReason = "";
        internal byte CoverWhy;                    // ToCover: OpenCover (still in the open) or ForwardCover (no run back)
        internal byte Owner;
        internal float Since = -1000f;
        internal byte Verdict;
        internal int Switches;                     // owner changes (F8 / harness)
        object _order;
        float _markAt = -1000f, _rawAt = -1000f;
        // still-in-open
        // A 2 s trail of where he stood (0.25 s samples, one array per merc):
        // "still" = every sample of the last OpenStill s within 1 m of him,
        // whatever anchor anyone else measures from.
        const int Trail = 9;
        const float TrailGap = 0.25f;
        readonly Vector3[] _trail = new Vector3[Trail];
        readonly float[] _trailAt = new float[Trail];
        int _trailN, _trailHead;
        float _nextTrail;
        // fall-back
        int _hits;
        float _hitAt = -1000f, _hitBefore = -1000f;
        // watchdog / redirect commitment
        internal bool Committed;
        internal Vector3 CommitTo;
        internal float CommitUntil;
        internal bool CommitCover;
        internal byte CommitWhy;                   // MercWriter.OpenCover / ForwardCover / Forward
        internal Vector3 CoverAt;
        internal bool HasCoverAt;
        // fire
        internal float LastShot = -1000f;
        bool _firing;
        internal byte FireWhy;                     // latest gating reason while not shooting
        internal int FireStops;
        // log state: current versus logged
        internal byte Writer;
        internal Vector3 Target;
        internal bool Moving;
        internal string Reason = "";
        byte _loggedWriter = 255;
        Vector3 _loggedTarget, _loggedFrom, _armFrom;
        bool _loggedMoving;
        byte _loggedOwner;
        float _nextLog;
        internal float NotedAt = -1f;
        internal int Lines;                        // log lines produced (harness: no per-frame logging)

        // ------------------------------------------------------------ owner

        /// <summary>A new order object resets the arbiter; a new mark puts
        /// the ORDER layer in charge for MarkHold s.</summary>
        internal bool Begin(ref MercOwnerIn i)
        {
            if (ReferenceEquals(i.Order, _order)) return false;
            _order = i.Order;
            Committed = false; HasCoverAt = false;
            if (i.Mark) { _markAt = i.Now; SetOwner(MercLayer.Order, i.Now); }
            else SetOwner(MercLayer.Regroup, i.Now);
            return true;
        }

        internal bool FreshMark(float now) { return now - _markAt < MarkHold; }

        /// <summary>Health or heavy fire: the brain may fall back, whoever
        /// holds the target.</summary>
        internal bool FallbackDue(ref MercOwnerIn i)
        {
            if (i.Hits > _hits) { _hitBefore = _hitAt; _hitAt = i.Now; }
            _hits = i.Hits;
            return FallbackNow = i.Health < FallbackHealth || i.Pressure >= HeavyPressure
                || (i.Now - _hitAt < HitBurst && i.Now - _hitBefore < HitBurst);
        }

        /// <summary>The fight gate (FightIn.MayFight) with the hold: the ORDER
        /// layer keeps the brain out for Hold s after it took the target
        /// (MarkHold after a new mark); an engaged brain keeps fighting Hold s
        /// after the order's own gate closed (sight flicker no longer ends a
        /// fight). A fall-back always opens it.</summary>
        internal bool Gate(ref MercOwnerIn i, bool fallback)
        {
            Begin(ref i);
            if (fallback) return true;
            if (i.Mark && FreshMark(i.Now)) return false;
            if (Owner == MercLayer.Fight || Owner == MercLayer.Fallback)
            {
                if (i.RawMayFight) _rawAt = i.Now;
                return i.RawMayFight || i.Now - Math.Max(Since, _rawAt) < Hold;
            }
            if (Owner == MercLayer.Order && i.Now - Since < Hold) return false;
            return i.RawMayFight;
        }

        void SetOwner(byte layer, float now)
        {
            if (layer == Owner) return;
            Owner = layer; Since = now; Switches++;
        }

        /// <summary>The one decision per tick, after the brain's Think.
        /// fightAct: the brain's FightAct (0 none); moves: Run or Step to
        /// dest. Returns KeepFight, ToOrder, ToCover (run CommitTo; the
        /// adapter fills it with a cover from CoverQuery or a forward step)
        /// or Fallback.</summary>
        internal byte Decide(ref MercOwnerIn i, bool fallback, byte fightAct, bool moves, Vector3 dest)
        {
            Begin(ref i);
            float now = i.Now;
            Still(ref i);
            // A committed run that got him nowhere (a wall, a bad step) ends
            // at once; the next step goes the other side. The still clock
            // only ever restarts on real movement.
            if (Committed && now - _commitAt > 0.6f && Flat(i.Me - _commitFrom) < StillMove)
            { Committed = false; _flip = !_flip; }
            if (Committed && (now >= CommitUntil || Flat(CommitTo - i.Me) <= InCoverReach * 0.5f))
            {
                if (CommitCover && Flat(CommitTo - i.Me) <= InCoverReach) { CoverAt = CommitTo; HasCoverAt = true; }
                Committed = false;
            }
            if (HasCoverAt && Flat(CoverAt - i.Me) > InCoverReach * 2f) HasCoverAt = false;
            bool wants = fightAct != 0;
            if (fallback && wants) { Committed = false; SetOwner(MercLayer.Fallback, now); return Verdict = Fallback; }
            // A committed cover run / forward step finishes under the layer that
            // asked for it (the order's own stand fixed is still the order's).
            if (Committed) return Verdict = ToCover;
            byte orderLayer = i.Mark ? MercLayer.Order : MercLayer.Regroup;
            if (wants && Owner == MercLayer.Order && now - Since < Hold && !fallback) wants = false;
            if (wants && i.Mark && FreshMark(now)) wants = false;
            if (wants)
            {
                SetOwner(MercLayer.Fight, now);
                // Under a mark the fight serves the order: no run back down it.
                if (i.Mark && moves && Backward(i.Me, dest, i.Heading)) { CoverWhy = MercWriter.ForwardCover; return Verdict = ToCover; }
                if (OpenStillDue(ref i)) { CoverWhy = MercWriter.OpenCover; return Verdict = ToCover; }
                return Verdict = KeepFight;
            }
            SetOwner(orderLayer, now);
            if (OpenStillDue(ref i)) { CoverWhy = MercWriter.OpenCover; return Verdict = ToCover; }
            return Verdict = ToOrder;
        }

        void Still(ref MercOwnerIn i)
        {
            if (i.Busy) { _trailN = 0; return; }
            if (i.Now < _nextTrail) return;
            _nextTrail = i.Now + TrailGap;
            _trailHead = (_trailHead + 1) % Trail;
            _trail[_trailHead] = i.Me; _trailAt[_trailHead] = i.Now;
            if (_trailN < Trail) _trailN++;
        }

        /// <summary>In contact, out of cover, still OpenStill s.</summary>
        internal bool OpenStillDue(ref MercOwnerIn i)
        {
            if ((!i.Contact && i.Now - _hitAt >= 4f) || i.Busy || InCover(ref i)) return false;
            return StillFor(i.Me, i.Now) >= OpenStill;
        }

        /// <summary>How long every trail sample stayed within 1 m of me.</summary>
        internal float StillFor(Vector3 me, float now)
        {
            float since = now;
            for (int k = 0; k < _trailN; k++)
            {
                int at = (_trailHead - k + Trail) % Trail;
                if (Flat(_trail[at] - me) > StillMove * 2f) break;
                since = _trailAt[at];
            }
            return now - since;
        }

        internal bool InCover(ref MercOwnerIn i)
        {
            return i.InCover || (HasCoverAt && Flat(CoverAt - i.Me) <= InCoverReach);
        }

        /// <summary>Back down the mark further than BackSlack.</summary>
        internal static bool Backward(Vector3 me, Vector3 dest, Vector3 heading)
        {
            if (heading.x * heading.x + heading.z * heading.z < 0.01f) return false;
            return (dest.x - me.x) * heading.x + (dest.z - me.z) * heading.z < -BackSlack;
        }

        /// <summary>Where the adapter looks for the cover (centre, radius):
        /// half way toward the enemy (or along the mark), within CoverReach.</summary>
        internal static Vector3 CoverQuery(ref MercOwnerIn i, out float radius)
        {
            Vector3 dir = Toward(ref i);
            radius = CoverReach * 0.6f;
            return i.Me + dir * (CoverReach * 0.4f);
        }

        /// <summary>A cover he may take in contact: within CoverReach and not
        /// further from the enemy (nor back down the mark) by more than BackSlack.</summary>
        internal static bool CoverOk(ref MercOwnerIn i, Vector3 cover)
        {
            if (Flat(cover - i.Me) > CoverReach) return false;
            if (i.HasEnemy && Flat(i.EnemyAt - cover) > Flat(i.EnemyAt - i.Me) + BackSlack) return false;
            return !(i.Mark && Backward(i.Me, cover, i.Heading));
        }

        /// <summary>No cover in reach: keep moving - a short step toward the
        /// enemy (side-on when he is close), never a stand.</summary>
        internal static Vector3 ForwardStep(ref MercOwnerIn i, int slot)
        {
            Vector3 dir = Toward(ref i);
            Vector3 side = new Vector3(dir.z, 0f, -dir.x);
            float s = (slot & 1) == 0 ? 1f : -1f;
            bool close = i.HasEnemy && Flat(i.EnemyAt - i.Me) < StepLength * 2f;
            Vector3 p = close ? i.Me + side * (StepLength * s) : i.Me + dir * StepLength + side * (StepLength * 0.35f * s);
            // Under a mark never back down it: the step goes on along the mark instead.
            if (i.Mark && Backward(i.Me, p, i.Heading))
            {
                Vector3 h = new Vector3(i.Heading.z, 0f, -i.Heading.x);
                p = i.Me + i.Heading * StepLength + h * (StepLength * 0.35f * s);
            }
            return p;
        }

        static Vector3 Toward(ref MercOwnerIn i)
        {
            Vector3 d = i.HasEnemy ? i.EnemyAt - i.Me : i.Heading;
            d.y = 0f;
            float m = (float)Math.Sqrt(d.x * d.x + d.z * d.z);
            if (m < 0.01f) { d = i.Heading; d.y = 0f; m = (float)Math.Sqrt(d.x * d.x + d.z * d.z); }
            return m < 0.01f ? new Vector3(0f, 0f, 1f) : d * (1f / m);
        }

        /// <summary>The adapter's answer to ToCover: run there, committed.</summary>
        internal void Commit(Vector3 to, bool cover, byte why, float now)
        {
            Committed = true; CommitTo = to; CommitCover = cover; CommitWhy = why; _askSince = -1f;
            CommitUntil = now + (cover ? CoverCommit : StepCommit);
            _commitAt = now; _commitFrom = In.Me;
        }

        /// <summary>The side of the next forward step (flips when a step got
        /// him nowhere).</summary>
        internal int StepSide(int slot) { return slot + (_flip ? 1 : 0); }

        float _commitAt;
        Vector3 _commitFrom;
        bool _flip;

        internal void Drop() { Committed = false; }

        /// <summary>The cover budget (one query a frame for every merc) was
        /// denied: true once it has been denied AskWait s - then the step
        /// without a query, never a longer stand.</summary>
        internal bool AskTimedOut(float now)
        {
            if (_askSince < 0f || now - _askSince > 1f) _askSince = now;
            return now - _askSince >= AskWait;
        }

        internal const float AskWait = 0.3f;
        float _askSince = -1f;

        // ------------------------------------------------------------- fire

        internal void Shot(float now) { LastShot = now; }
        internal bool KeepFire(float now) { return now - LastShot <= FireKeep; }

        /// <summary>Once per tick: true when the fire state just ended (log
        /// it with FireWhy).</summary>
        internal bool FireTick(float now)
        {
            bool firing = KeepFire(now);
            bool stopped = _firing && !firing;
            _firing = firing;
            if (stopped) FireStops++;
            return stopped;
        }

        // ---------------------------------------------------------- spacing

        const int Slots = 32;
        static readonly int[] _ids = new int[Slots];
        static readonly Vector3[] _at = new Vector3[Slots];
        static readonly Vector3[] _to = new Vector3[Slots];
        static readonly float[] _seen = new float[Slots];
        static readonly bool[] _moving = new bool[Slots];

        static int SlotOf(int id, float now)
        {
            int free = -1;
            for (int k = 0; k < Slots; k++)
            {
                if (_ids[k] == id && now - _seen[k] < 2f) return k;
                if (free < 0 && (_ids[k] == 0 || now - _seen[k] >= 2f)) free = k;
            }
            if (free >= 0) { _ids[free] = id; _moving[free] = false; _to[free] = Vector3.zero; }
            return free;
        }

        /// <summary>Every merc posts where he is, once per tick.</summary>
        internal static void Post(int id, Vector3 me, float now)
        {
            int k = SlotOf(id, now);
            if (k < 0) return;
            _at[k] = me; _seen[k] = now;
        }

        /// <summary>A moving merc's leg target, pushed aside (at most
        /// SpreadCap) from any mate within MinSpread of him or of the target.
        /// Exact spots (closer than near) are never moved.</summary>
        internal static Vector3 Spread(int id, Vector3 me, Vector3 goal, float near, float now)
        {
            int self = SlotOf(id, now);
            Vector3 d = goal - me; d.y = 0f;
            float len = (float)Math.Sqrt(d.x * d.x + d.z * d.z);
            if (self >= 0) { _seen[self] = now; _at[self] = me; }
            if (len < near || len < 0.01f)
            {
                if (self >= 0) { _to[self] = goal; _moving[self] = len >= 0.01f; }
                return goal;
            }
            Vector3 dir = d * (1f / len);
            Vector3 side = new Vector3(dir.z, 0f, -dir.x);
            float push = 0f;
            for (int k = 0; k < Slots; k++)
            {
                if (k == self || _ids[k] == 0 || now - _seen[k] > 0.5f) continue;
                Vector3 q = _at[k];
                float gap = Flat(q - me);
                Vector3 qt = _moving[k] ? _to[k] : q;
                float tgap = Flat(qt - goal);
                // Legs that end close, or two men closer than MinSpread (lanes
                // are 7 m apart: running them never triggers it, no chain).
                bool legs = tgap < MinSpread, bodies = gap < MinSpread;
                if (!legs && !bodies) continue;
                Vector3 rel = legs ? qt - goal : q - me;
                float across = rel.x * side.x + rel.z * side.z;
                if (Math.Abs(across) < 0.5f) across = _ids[k] < id ? 1f : -1f;
                float need = (MinSpread - (legs ? tgap : gap)) * 2f;
                push += across > 0f ? -need : need;
            }
            if (push > SpreadCap) push = SpreadCap; else if (push < -SpreadCap) push = -SpreadCap;
            Vector3 g = goal + side * push;
            if (self >= 0) { _to[self] = g; _moving[self] = true; }
            return g;
        }

        internal static void ResetSpread()
        {
            for (int k = 0; k < Slots; k++) { _ids[k] = 0; _seen[k] = -1000f; _moving[k] = false; }
        }

        // -------------------------------------------------------------- log

        /// <summary>A sink reports the target it issues this tick. reason:
        /// a literal (no allocation).</summary>
        internal void Note(byte writer, Vector3 target, bool moving, string reason, float now)
        {
            NotedAt = now;
            Writer = writer; Target = target; Moving = moving; Reason = reason;
        }

        /// <summary>Once per tick: is there a change worth one log line now?
        /// Same writer, still moving: only a turn of the heading over 60
        /// degrees counts - a lane leg creeping forward is not a change.</summary>
        internal bool LogDue(Vector3 me, float now)
        {
            if (_loggedWriter == 255) return Arm(me, now);
            bool change = Writer != _loggedWriter || Moving != _loggedMoving || Owner != _loggedOwner;
            if (!change && Moving)
            {
                Vector3 a = _loggedTarget - _loggedFrom, b = Target - me;
                a.y = 0f; b.y = 0f;
                float la = (float)Math.Sqrt(a.x * a.x + a.z * a.z), lb = (float)Math.Sqrt(b.x * b.x + b.z * b.z);
                if (la > StillMove && lb > StillMove && (a.x * b.x + a.z * b.z) / (la * lb) < TurnCos) change = true;
            }
            if (!change) return false;
            if (now < _nextLog) return false;
            return Arm(me, now);
        }

        bool Arm(Vector3 me, float now) { _armFrom = me; _nextLog = now + LogGap; Lines++; return true; }

        /// <summary>After the line is written: what it said is now the logged state.</summary>
        internal void Logged()
        {
            _loggedWriter = Writer; _loggedTarget = Target; _loggedMoving = Moving; _loggedOwner = Owner;
            _loggedFrom = _armFrom;
        }

        internal Vector3 LoggedTarget { get { return _loggedTarget; } }
        internal byte LoggedWriter { get { return _loggedWriter == 255 ? MercWriter.None : _loggedWriter; } }

        internal static float Flat(Vector3 v) { return (float)Math.Sqrt(v.x * v.x + v.z * v.z); }

        /// <summary>The move-change line (metres, x/z).</summary>
        internal string MoveLine(string name, Vector3 me)
        {
            return "Mercs: move " + name + " [" + MercLayer.Name(Owner) + "] "
                + MercWriter.Name(LoggedWriter) + " " + P(LoggedTarget) + " -> "
                + MercWriter.Name(Writer) + " " + (Moving ? P(Target) : "stand " + P(me))
                + " (" + Reason + ")";
        }

        /// <summary>A one-off event line (a K2 detour, an ATTACK stuck re-path),
        /// at most one per LogGap per merc.</summary>
        internal bool EventDue(float now)
        {
            if (now < _nextEvent) return false;
            _nextEvent = now + LogGap; Lines++;
            return true;
        }

        float _nextEvent;

        internal string FireLine(string name)
        {
            return "Mercs: fire stop " + name + " [" + MercLayer.Name(Owner) + " " + MercWriter.Name(Writer)
                + "]: " + MercFireStop.Name(FireWhy);
        }

        static string P(Vector3 v)
        {
            return "(" + (v.x / Metre).ToString("0") + "," + (v.z / Metre).ToString("0") + ")";
        }
    }
}

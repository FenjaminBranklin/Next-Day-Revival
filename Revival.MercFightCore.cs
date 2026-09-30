// M2 mercenaries, the fight loop - the pure core (docs/ai/tasks/m2-merc-peek-fight.md).
// It knows Vector3, Mathf and the M1 cover field (Revival.MercCoverCore.cs)
// and nothing of the game, so research/merc_fight_check.py compiles this file
// UNCHANGED into a deterministic simulation. Revival.MercFight.cs feeds it
// from NpcWar and carries its orders out.
//
//   CONTACT   a merc is in a fight while his order lets him fight there and
//             he sees a target, a threat sees him, or he was hit. The fight
//             is an overlay: when it ends (nothing seen for Disengage
//             seconds, no threat left) the brain says None and his order
//             (follow, stay, patrol, perimeter, vehicle) runs again.
//   COVER     on contact he SPRINTS to the cover the M1 sense picked (hides a
//             crouching man from the primary threat, ranked over all
//             threats). There he crouches, facing the threat.
//   PEEK      after a varied wait he steps to a peek spot (LEFT / RIGHT of the
//             face, or stands up behind a low one: OVER), fires one short
//             burst once his feet are planted and he sees a target, and goes
//             back down. The side changes (never three times the same, never
//             the side he was just hit on), the wait is drawn per peek and
//             grows under fire. A side is proven with one ray before use.
//   RELOAD    in cover, crouched: when the magazine is low, never at a peek
//   HEAL      in cover, crouched: a field dressing below HealBelow health,
//             HealsPerFight per fight, HealGap seconds apart.
//   RELOCATE  when a threat sees him in his cover (flanked, or the cover
//             drove away), when he is hit while hidden (suppressed through
//             it), or when a blast or a live grenade is near: the point is
//             marked bad for everyone, the sense is told to pick again and he
//             sprints to the new point.
//   NO COVER  never stand in the open: strafe runs across the threat's line,
//             a short burst between them only when he sees a target, and
//             crouched when nobody sees him - the only place he reloads there.
//   COST      one Think every ThinkEvery seconds per merc (the adapter
//             staggers them): arithmetic, the analytic peek test on the held
//             point, and at most PeekRays rays when a peek starts. No
//             allocation after construction.
//
// M3 (docs/ai/tasks/m3-merc-tactics.md) - the team, on the board of
// Revival.MercSquadCore.cs (Squad null: one merc alone, as in M2):
//   SPREAD    a merc who shares a cover with a mate (the lower id keeps it)
//             takes another; planned moves never pick a point by a mate.
//   COVERING  a mate who runs between covers or calls for cover gets a peek
//             at once from a merc who is down (nobody else up): a longer
//             burst that keeps firing at the threat's last spot while it is
//             out of sight (Suppress). A PLANNED move (flank, better cover,
//             spread, fall-back) waits up to CallWait for that cover first.
//   CALL-OUT  a mate who sees a threat brings him into the fight.
//   FLANK     tier 1 and up, when his cover is stalled (blind peeks, no
//             target seen for 3 s): after FlankAfter seconds in it, one
//             merc of the team at a time (MercSquad.TakeFlank) asks for
//             cover at a point FlankTurn degrees round the threat, away from
//             his mates, and runs there under covering fire.
//   LINE      he never fires while a mate or the owner is near his line of
//             fire (NoShot); a peek side whose line a friend blocks is skipped.
//   FALL BACK the team is losing (MercSquad.Losing): cover near the owner,
//             on the side away from the threat; the fight goes on from there.
//   RETREAT   below RetreatBelow health: cover further back (toward the
//             owner), longer waits, a peek only when a threat comes close,
//             dressings; back to the fight at RetreatUntil.
//   GRADE     0..1 from his traits (MercGrade): reaction to being seen in
//             cover (ReactTime, SenseEvery), the wait between peeks, fewer blind peeks,
//             taking a better cover when one turns up (tier 1+), flanking.
//             His aim over range is the adapter's (Fighter.RangeFalloff).
//
// Units: game units (~2.8 per metre), seconds. C# 3.0, ASCII only.
using System;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>What the brain is told each Think (filled by the game
    /// adapter or the offline simulation; nothing here is kept).</summary>
    internal struct FightIn
    {
        public bool Survive, Rally;
        public Vector3 Watch;           // approach for quiet shelter selection
        public float Now;
        public Vector3 Me;              // his feet
        public int Count;               // threats sensed, primary first (MercSense)
        public Vector3[] Threats;       // their positions (MercSense.At, shared)
        public bool Exposed;            // a threat sees him as he is (MercSense)
        public float SensedAt;          // when Exposed was measured
        public CoverPick Pick;          // the sense's best cover
        public bool PickFresh;          // chosen against the current primary threat, recently
        public bool Target;             // he has a target
        public bool Sees;               // and a line of fire to it
        public float LastSeen;          // when he last had that line
        public bool Planted;            // feet planted in the standing aim clip (ready to fire)
        public float Health;            // 0..1
        public int Rounds, MaxRounds;   // the magazine; MaxRounds <= 0: unknown
        public bool Reloading;          // the game's reload is running
        public int Hits;                // hits taken, a running count
        public float Suppression;       // 0..1, rounds felt (optional)
        public bool Danger;             // a blast or a live grenade near him
        public Vector3 DangerAt;
        public bool MayFight;           // his order lets him fight where he is
        public float Disengage;         // seconds without sight before the fight is over
        // M3: the team (MercBrain.Squad) and the owner he regroups on.
        public Vector3 PickFrom;        // where the sense searched that pick from (his spot or FightOut.Anchor)
        public Vector3 Owner;           // the owner's feet
        public bool HasOwner;           // the owner is here: never fire through him
        public bool Regroup;            // his order lets him fall back on the owner (follow / vehicle)
    }

    /// <summary>What he does until the next Think.</summary>
    internal struct FightOut
    {
        public byte Act;                // FightAct
        public Vector3 Dest;            // Run / Step: where to
        public bool Low;                // Hold: crouched
        public Vector3 Face;            // Hold / Fire: toward this point
        public bool Kick;               // Reload: start the reload now (once)
        public bool HealNow;            // a dressing is done: add HealAmount health (once)
        public bool Repick;             // ask the sense for a new pick at once (once)
        // M3
        public bool NoShot;             // Fire: aim, do not fire - a mate or the owner is in the line
        public bool Suppress;           // Fire: covering fire at Face while the target is out of sight
        public bool AnchorOn;           // the sense searches cover from Anchor instead of his spot
        public Vector3 Anchor;          //   (a flank, the fall-back on the owner, a retreat)
    }

    internal static class FightAct
    {
        internal const byte None = 0;     // no fight: his order runs
        internal const byte Run = 1;      // sprint to Dest
        internal const byte Step = 2;     // a short run to a peek spot or back
        internal const byte Hold = 3;     // stand still (Low: crouched), face Face
        internal const byte Fire = 4;     // stand, aim at the target, fire when planted
        internal const byte Reload = 5;   // crouched; Kick starts the game's reload
        internal const byte Heal = 6;     // crouched, patching up

        static readonly string[] Names = { "-", "run", "step", "hold", "fire", "reload", "heal" };
        internal static string Name(byte a) { return a < Names.Length ? Names[a] : "?"; }
    }

    /// <summary>One merc's fight: a state machine over the M1 cover field.</summary>
    internal sealed class MercBrain
    {
        internal const byte Off = 0, Dash = 1, Hide = 2, PeekOut = 3, Burst = 4, PeekBack = 5,
            Reloading = 6, Healing = 7, Evade = 8, Flee = 9;
        static readonly string[] Names =
            { "OFF", "DASH", "HIDE", "PEEK", "BURST", "BACK", "RELOAD", "HEAL", "EVADE", "FLEE" };
        internal static string Name(byte s) { return s < Names.Length ? Names[s] : "?"; }

        internal const float ThinkEvery = 0.1f;
        internal const float ArriveCover = 1.0f;
        // At the peek spot: on it, or stopped near it (the agent's stopping
        // distance) - the brain must not call him there while he is still
        // behind the edge, one Think of running short of it.
        internal const float ArrivePeek = 0.3f, PeekStall = 1.5f;
        internal const float HideMin = 0.6f, HideMax = 1.6f;       // the wait between two peeks
        internal const float LongHide = 0.15f;                    // share of long waits (2.4..3.4 s)
        internal const float HideCap = 4.5f;                      // the longest wait, suppressed
        internal const float BurstMin = 0.45f, BurstMax = 1.0f;   // seconds of fire in one burst
        internal const float PeekCap = 3.0f;                      // longest time up at one peek
        internal const float PeekBlind = 0.9f;                    // planted, nothing seen: back down
        internal const int PeekRays = 2;                          // rays one peek may spend proving a side
        internal const float ReloadShare = 0.35f;                 // reload in cover below this share
        internal const float ReloadCap = 4.5f;
        internal const float RoundsPerSecond = 4f;                // estimate when the magazine is unknown
        internal const float EstimatedMagazine = 24f;
        internal const float HealBelow = 0.55f, HealSeconds = 4f, HealAmount = 0.10f, HealGap = 20f;
        internal const int HealsPerFight = 3;
        internal const float DangerRadius = 20f, FleeDistance = 26f;
        internal const float StrafeMin = 8f, StrafeMax = 14f, StrafeCap = 2.6f;
        internal const float EvadeBurstMin = 0.35f, EvadeBurstMax = 0.7f, EvadeUpCap = 1.5f;
        internal const float Grace = 1.2f;                        // contact gaps shorter than this keep the fight
        internal const float PickReach = 45f;                     // a pick further than this is not run for
        internal const float BadSeconds = 12f;                    // a failed point is skipped this long
        // M3: the team, the modes, the grade.
        internal const byte Normal = 0, Flanking = 1, Falling = 2, Retreating = 3;
        static readonly string[] ModeNames = { "", "FLANK", "FALLBACK", "RETREAT" };
        internal static string ModeName(byte m) { return m < ModeNames.Length ? ModeNames[m] : "?"; }
        internal const float RetreatBelow = 0.35f, RetreatUntil = 0.5f; // health: retreat below, back at
        internal const float RetreatDefend = 35f;                 // retreating: a peek only for a threat this near
        internal const float SuppressMin = 1.0f, SuppressMax = 1.7f; // covering fire: a longer burst
        internal const float CallWait = 0.9f;                     // a planned move waits this long for cover
        internal const float FlankAfter = 4f, FlankTurn = 55f, FlankGain = 25f; // s in cover, degrees
        internal const float FlankGrade = 0.2f, UpgradeGrade = 0.3f;
        internal const float UpgradeAfter = 4f;                   // s in a cover before a better one is taken
        internal const float FallNear = 22f;                      // regrouped: this near the owner (8 m)
        internal const float AnchorReach = 90f;                   // a pick searched from an anchor: run this far
        internal const float RetreatReach = 40f;                  // a retreat: the next cover back, not the far one
        internal const float StuckAfter = 0.6f;                   // a run that gets nowhere this long: another point
        internal const float BlockedCap = 0.6f;                   // a friend in the line this long: down again
        const byte PlanNone = 0, PlanMode = 1, PlanFlank = 2, PlanUpgrade = 3, PlanSpread = 4;

        readonly int _id;
        uint _rng;

        // M3: the team board (null: alone, as in M2) and his grade (0..1,
        // MercGrade.Of his traits; 0.5 leaves the M2 timings as they were).
        internal MercSquad Squad;
        internal float Grade = 0.5f;
        internal byte Mode;                  // Normal, Flanking, Falling, Retreating
        byte _plan;
        bool _anchorOn, _anchorSent, _call, _suppress, _regrouped;
        Vector3 _anchor, _anchorWas, _called;
        float _planUntil, _callSince, _nextTeam, _nextCover, _nextPlan, _nextFlankTry, _coverSince, _fightStart;
        float _alarmAt, _blockedFor, _calmSince, _lastMoveAt, _stuckSince, _lastSpotted = -1000f;
        // M3 counters (F8, the offline check).
        internal int Covers, CoveredMoves, PlannedMoves, FlankRuns, Falls, Retreats, Upgrades, Spreads, HeldFire, Joined;
        internal float SuppressTime;

        internal byte State;
        internal float Since;                // when the state was entered
        internal CoverPick Cover;            // the cover he holds or runs to (Found: he has one)
        internal Vector3 PeekAt;
        internal byte PeekSide, LastSide;
        internal Vector3 Dest;               // the move in force (dash, strafe, flee)
        internal FightOut Order;             // what he does until the next Think

        float _nextThink, _lastThink, _dt, _until, _nextPeek, _fired, _burstLen, _upSince;
        float _plantedAt, _seenAt, _lastEngaged, _lastHit, _pressure, _estFired, _nextClaim, _nextHeal, _kickAt;
        int _hits, _sameSide, _healsLeft = HealsPerFight, _blindPeeks, _evadeSign = 1;
        byte _hitSide;
        bool _evadeUp;
        Vector3 _bad0, _bad1, _lastThreat, _prevMe;
        float _moved;                        // how far he got since the last Think
        float _badUntil0, _badUntil1;

        // Counters for the F8 status line and the offline check.
        internal int Peeks, Bursts, Dashes, Relocations, Flanks, Reloads, Heals, Flees, Strafes, Blind;
        internal float FightTime, SeenStill;   // seconds in fights; seconds a threat saw him holding still outside a peek

        internal MercBrain(int id)
        {
            _id = id;
            _rng = unchecked((uint)id * 2654435761u) ^ 0x9E3779B9u;
            if (_rng == 0) _rng = 1;
        }

        internal int Id { get { return _id; } }
        internal bool Fighting { get { return State != Off; } }

        /// <summary>He holds a cover point (the sense must not move his claim).</summary>
        internal bool Holding { get { return State != Off && Cover.Found; } }

        /// <summary>Up at a peek or firing: exposed on purpose.</summary>
        internal bool Up
        {
            get { return State == PeekOut || State == Burst || State == PeekBack || (State == Evade && _evadeUp); }
        }

        /// <summary>Crouched at his cover point (hidden if the cover holds).</summary>
        internal bool Down
        {
            get { return State == Hide || State == Reloading || State == Healing; }
        }

        /// <summary>A Think is due; the first one is staggered by the merc id.</summary>
        internal bool Due(float now)
        {
            if (now < _nextThink) return false;
            _nextThink = (_nextThink <= 0f ? now + (_id & 7) * 0.0125f : now) + ThinkEvery;
            return true;
        }

        float Next01()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return (_rng & 0xFFFFFF) / 16777216f;
        }

        float Range(float a, float b) { return a + (b - a) * Next01(); }

        static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }

        Vector3 Primary(ref FightIn i) { return i.Count > 0 ? i.Threats[0] : _lastThreat; }

        // ------------------------------------------------------------ think

        /// <summary>Decide what he does until the next Think.</summary>
        internal void Think(ref FightIn i, CoverField field, out FightOut o)
        {
            o = new FightOut();
            _call = false;
            if (i.Survive) DecideSurvival(ref i, field, ref o);
            else Decide(ref i, field, ref o);
            // The anchor the sense searches cover from (a new one: search now).
            o.AnchorOn = _anchorOn && State != Off;
            o.Anchor = _anchor;
            if (o.AnchorOn != _anchorSent || (o.AnchorOn && Flat(_anchor - _anchorWas) > 3f))
            {
                o.Repick = true;
                _anchorSent = o.AnchorOn;
                _anchorWas = _anchor;
            }
            Order = o;
            if (Squad != null)
            {
                bool moving = State == Dash || State == Flee || State == Evade;
                Squad.Post(_id, i.Now, i.Me, State != Off, State != Off && State != Dash && Cover.Found,
                    Cover.Point.Pos, Up, moving, _call, i.Sees, i.Count > 0,
                    i.Count > 0 ? i.Threats[0] : _lastThreat, i.Health, Mode);
            }
        }

        internal const float SurvivalContact = 112f; // 40 m
        internal bool SurvivalFire;
        float _defendUntil = -100f, _nextBound, _retreatUntil;
        bool _survivalStarted;

        /// <summary>Shelter persists through respawn. M2 steps supply the poses,
        /// peeks and quiet maintenance; M1 supplies every bound's destination.</summary>
        void DecideSurvival(ref FightIn i, CoverField field, ref FightOut o)
        {
            _dt = _lastThink > 0f ? Mathf.Min(i.Now - _lastThink, 0.5f) : 0f;
            _lastThink = i.Now;
            _moved = Flat(i.Me - _prevMe); _prevMe = i.Me;
            bool hit = i.Hits != _hits; _hits = i.Hits;
            bool fresh = i.Now - i.SensedAt < 0.8f;
            _lastThreat = i.Count > 0 ? i.Threats[0] : i.Watch;
            // Exposure without a real threat is only a quiet shelter probe.
            if (hit || (i.Count > 0 && i.Exposed && fresh) || i.Suppression > 0.15f)
                _defendUntil = i.Now + 3f;
            SurvivalFire = i.Target && i.Count > 0 &&
                (Flat(i.Threats[0] - i.Me) <= SurvivalContact || i.Now < _defendUntil);
            _suppress = false;
            _plan = PlanNone;
            Mode = i.Health < RetreatBelow ? Retreating : Normal;
            if (!_survivalStarted || State == Off)
            {
                _survivalStarted = true; _healsLeft = HealsPerFight;
                _coverSince = i.Now; State = Evade;
                o.Repick = true;
            }
            // A position is overrun by a close threat, a hit or a failed face.
            bool overrun = Cover.Found && Down &&
                (hit || (fresh && i.Exposed) ||
                 (i.Count > 0 && Flat(i.Threats[0] - i.Me) < 28f));
            if (overrun || (Cover.Found && i.Health < RetreatBelow && i.Now >= _retreatUntil))
            {
                _retreatUntil = i.Now + 12f;
                SurvivalBack(ref i, field, ref o); return;
            }
            if (i.Danger && Flat(i.DangerAt - i.Me) < DangerRadius && State != Flee)
            {
                StartFlee(ref i, field, ref o); return;
            }
            if (State == Flee)
            {
                if (i.Danger && i.Now < _until) { o.Act = FightAct.Run; o.Dest = Dest; return; }
                State = Evade; Cover = new CoverPick(); o.Repick = true;
            }
            // Quiet rally: ask M1 for the next shelter a short bound toward owner.
            // No usable cover ahead means wait at this cover; never warp or cross
            // an unbounded open field just because the owner is far away.
            if (i.Rally && i.HasOwner && i.Now >= _nextBound && State == Hide && !SurvivalFire)
            {
                float d = Flat(i.Owner - i.Me);
                if (d > 22f)
                {
                    _anchorOn = true;
                    _anchor = i.Me + (i.Owner - i.Me) * (Mathf.Min(30f, d) / d);
                    if (Usable(ref i) && Flat(i.Pick.Point.Pos - i.Owner) + 4f < d &&
                        Flat(i.Pick.Point.Pos - i.Me) <= 45f)
                    {
                        StartDash(ref i, ref o); _nextBound = i.Now + 1f; return;
                    }
                    o.Repick = true;
                }
            }
            if (!Cover.Found)
            {
                if (Usable(ref i) && Flat(i.Pick.Point.Pos - i.Me) <= 45f) { StartDash(ref i, ref o); return; }
                o.Repick = i.Now >= _nextBound;
                if (o.Repick) _nextBound = i.Now + 1.5f;
                // Mapping delay / no cover: keep moving away from a visible
                // threat. Never stop upright, shoot, reload or heal in the open.
                if ((i.Count > 0 && (i.Exposed || hit)) || (_anchorOn && Flat(Dest - i.Me) > 2f && i.Now < _until))
                {
                    if (Flat(Dest - i.Me) < 2f || i.Now >= _until)
                    {
                        Dest = i.Me + Away(i.Me, _lastThreat) * 20f;
                        _until = i.Now + 2f;
                    }
                    o.Act = FightAct.Run; o.Dest = Dest;
                }
                else HoldLow(ref i, ref o);
                return;
            }
            if (i.Now >= _nextClaim)
            {
                _nextClaim = i.Now + 1f;
                field.Claim(_id, Cover.Point.Pos, i.Now + 5f, i.Now);
            }
            if (!SurvivalFire && (State == PeekOut || State == Burst))
            { StartBack(ref i, ref o); return; }
            switch (State)
            {
                case Dash: StepDash(ref i, field, ref o); break;
                case Hide: StepHide(ref i, field, ref o, hit); break;
                case PeekOut: StepPeekOut(ref i, ref o, hit); break;
                case Burst: StepBurst(ref i, ref o, hit); break;
                case PeekBack: StepPeekBack(ref i, field, ref o); break;
                case Reloading: StepReload(ref i, field, ref o, hit); break;
                case Healing: StepHeal(ref i, field, ref o, hit); break;
                default: StartHide(ref i, ref o, true); break;
            }
            o.Suppress = false;
            if (o.Act == FightAct.Fire) o.NoShot |= !SurvivalFire;
        }

        void SurvivalBack(ref FightIn i, CoverField field, ref FightOut o)
        {
            MarkBad(field, i.Now, BadSeconds);
            field.Release(_id); Cover = new CoverPick();
            Relocations++;
            _anchorOn = true;
            _anchor = i.Me + Away(i.Me, Primary(ref i)) * 20f;
            // Discard the stale, compromised pick before M1 supplies a new one.
            State = Evade; i.PickFresh = false;
            Dest = _anchor; _until = i.Now + 2f;
            o.Repick = true; o.AnchorOn = true; o.Anchor = _anchor;
            o.Act = FightAct.Run; o.Dest = Dest;
        }

        void Decide(ref FightIn i, CoverField field, ref FightOut o)
        {
            float now = i.Now;
            _dt = _lastThink > 0f ? Mathf.Min(now - _lastThink, 0.5f) : 0f;
            _lastThink = now;
            _moved = Flat(i.Me - _prevMe);
            _prevMe = i.Me;
            bool hit = i.Hits != _hits;
            if (hit) { _hits = i.Hits; _lastHit = now; _pressure = Mathf.Min(1f, _pressure + 0.45f); }
            _pressure = Mathf.Max(Mathf.Max(0f, _pressure - _dt * 0.28f), Mathf.Clamp(i.Suppression, 0f, 1f));
            if (i.Count > 0) _lastThreat = i.Threats[0];

            bool fresh = now - i.SensedAt < 0.8f;
            bool danger = i.Danger && Flat(i.DangerAt - i.Me) < DangerRadius;
            // M3: a threat that saw him keeps the fight on like a target he
            // saw (a group he cannot see from his cover is still there).
            if (i.Exposed && fresh && i.Count > 0) _lastSpotted = now;
            bool own = (i.Count > 0 && (i.Sees || (i.Target && now - i.LastSeen < i.Disengage) || (i.Exposed && fresh)
                    || now - _lastSpotted < i.Disengage))
                || now - _lastHit < 3f || danger;
            // M3: a mate who sees a threat calls it out.
            bool called = Squad != null && i.MayFight && Squad.CallOut(_id, i.Me, now, out _called);
            if (called && i.Count == 0) _lastThreat = _called;
            // A mate's flank keeps the team (those in the fight) in it while it runs.
            bool flank = State != Off && Squad != null && i.Count > 0 && Squad.Flanking(now);
            bool engaged = i.MayFight && (own || called || flank);
            if (engaged) _lastEngaged = now;
            if (!i.MayFight || (State != Off && !engaged && now - _lastEngaged > Grace))
            {
                if (State != Off) Leave(field);
                return;
            }
            if (State == Off)
            {
                if (!engaged) return;
                _healsLeft = HealsPerFight;
                _blindPeeks = 0;
                _fightStart = now;
                _coverSince = now;
                Mode = Normal;
                _plan = PlanNone;
                if (!own) Joined++;
                o.Repick = true;
                if (danger) StartFlee(ref i, field, ref o);
                else Choose(ref i, ref o);
                return;
            }
            FightTime += _dt;
            if (i.Exposed && fresh && (Down || (State == Evade
                && (Order.Act == FightAct.Hold || Order.Act == FightAct.Reload))))
                SeenStill += _dt;

            // A grenade or a blast beside him: away from it, whatever he does.
            if (danger && State != Flee)
            {
                StartFlee(ref i, field, ref o);
                return;
            }
            if (Cover.Found && now >= _nextClaim)
            {
                _nextClaim = now + 1f;
                field.Claim(_id, Cover.Point.Pos, now + 5f, now);
            }
            Team(ref i, ref o);

            switch (State)
            {
                case Dash: StepDash(ref i, field, ref o); break;
                case Hide: StepHide(ref i, field, ref o, hit); break;
                case PeekOut: StepPeekOut(ref i, ref o, hit); break;
                case Burst: StepBurst(ref i, ref o, hit); break;
                case PeekBack: StepPeekBack(ref i, field, ref o); break;
                case Reloading: StepReload(ref i, field, ref o, hit); break;
                case Healing: StepHeal(ref i, field, ref o, hit); break;
                case Evade: StepEvade(ref i, ref o, true); break;
                case Flee: StepFlee(ref i, ref o); break;
            }
        }

        /// <summary>The fight is over (or he left it for his order): his claim
        /// goes, the order runs again.</summary>
        internal void Leave(CoverField field)
        {
            if (field != null) field.Release(_id);
            State = Off;
            _survivalStarted = false; SurvivalFire = false; _defendUntil = -100f;
            Cover = new CoverPick();
            _evadeUp = false;
            Order = new FightOut();
            if (Squad != null) Squad.EndFlank(_id, _lastThink);
            Mode = Normal;
            _plan = PlanNone;
            _anchorOn = false;
            _call = false;
            _suppress = false;
            _regrouped = false;
        }

        // ------------------------------------------------------------ team

        /// <summary>Seconds between being seen in his cover and leaving it:
        /// 0.3 s at grade 0, none from grade 0.5 up (higher grades are told
        /// sooner instead: the adapter senses them more often, SenseEvery).</summary>
        float ReactTime() { return Mathf.Max(0f, 0.3f * (1f - 2f * Grade)); }

        /// <summary>M3: the sense interval for a grade (MercSense.Every 0.5 s
        /// up to grade 0.5, 0.35 s at grade 1).</summary>
        internal static float SenseEvery(float grade) { return Mathf.Min(0.5f, MercGrade.Lerp(grade, 0.65f, 0.35f)); }

        /// <summary>Retreat when hurt, fall back on the owner when the team is
        /// losing, and out of those modes again (twice a second).</summary>
        void Team(ref FightIn i, ref FightOut o)
        {
            float now = i.Now;
            if (now < _nextTeam) return;
            _nextTeam = now + 0.5f;
            bool losing = i.Regroup && Squad != null && Squad.Losing(now);
            if (Mode == Retreating && i.Health >= RetreatUntil) EndMode(now);
            if (Mode == Falling)
            {
                if (!i.Regroup) EndMode(now);
                else if (losing) _calmSince = now;
                else if (now - _calmSince > 8f) EndMode(now);
            }
            if (Mode != Retreating && i.Health > 0f && i.Health < RetreatBelow)
            {
                if (Mode == Flanking && Squad != null) Squad.EndFlank(_id, now);
                StartMode(Retreating, RetreatPoint(ref i), now);
                Retreats++;
                o.Repick = true;
                return;
            }
            if (Mode == Normal && losing && Flat(i.Me - i.Owner) > FallNear)
            {
                StartMode(Falling, FallPoint(ref i), now);
                _calmSince = now;
                Falls++;
                o.Repick = true;
                return;
            }
            if ((Mode == Flanking || _plan == PlanFlank) && Squad != null) Squad.TakeFlank(_id, now, 3f);
            if (Mode == Falling)
            {
                // The owner moves: so does the place to regroup.
                _anchor = FallPoint(ref i);
                if (_plan == PlanNone && State == Hide && Flat(i.Me - i.Owner) > FallNear + 15f)
                {
                    _plan = PlanMode;
                    _planUntil = now + 3f;
                    _regrouped = false;
                }
            }
        }

        void StartMode(byte mode, Vector3 anchor, float now)
        {
            Mode = mode;
            _anchor = anchor;
            _anchorOn = true;
            _plan = PlanMode;
            _planUntil = now + 3f;
            _regrouped = false;
        }

        void EndMode(float now)
        {
            if (Mode == Flanking && Squad != null) Squad.EndFlank(_id, now);
            Mode = Normal;
            _anchorOn = false;
            if (_plan == PlanMode || _plan == PlanFlank) _plan = PlanNone;
        }

        static Vector3 Away(Vector3 from, Vector3 threat)
        {
            Vector3 v = from - threat;
            v.y = 0f;
            float d = Flat(v);
            return d > 0.5f ? v / d : new Vector3(0f, 0f, -1f);
        }

        /// <summary>Behind the owner, seen from the threat.</summary>
        Vector3 FallPoint(ref FightIn i)
        {
            return i.Owner + Away(i.Owner, Primary(ref i)) * 6f;
        }

        /// <summary>Further back: toward the owner when he is further from
        /// the threat, else 30 units straight away from it.</summary>
        Vector3 RetreatPoint(ref FightIn i)
        {
            Vector3 t = Primary(ref i);
            Vector3 back = i.Me + Away(i.Me, t) * 20f;
            if (!i.Regroup || Flat(i.Owner - t) < Flat(i.Me - t) + 5f) return back;
            // Toward the owner, 20 units at most.
            Vector3 to = i.Owner - i.Me;
            to.y = 0f;
            float d = Flat(to);
            return d <= 20f ? i.Owner : i.Me + to * (20f / d);
        }

        /// <summary>A friend's body near the line from -> to.</summary>
        bool FriendInLine(ref FightIn i, Vector3 from, Vector3 to)
        {
            if (i.HasOwner && MercSquad.Near(from, to, i.Owner)) return true;
            return Squad != null && Squad.Blocks(from, to, _id, i.Now, i.Owner, false);
        }

        /// <summary>A threat within RetreatDefend: a retreating man defends himself.</summary>
        bool Pressed(ref FightIn i)
        {
            for (int t = 0; t < i.Count; t++) if (Flat(i.Threats[t] - i.Me) < RetreatDefend) return true;
            return false;
        }

        /// <summary>The pick fits the plan he waits for.</summary>
        bool PlanOk(ref FightIn i)
        {
            if (!Usable(ref i)) return false;
            Vector3 p = i.Pick.Point.Pos;
            if (Cover.Found && Flat(p - Cover.Point.Pos) < 3f) return false;
            if (Squad != null && Squad.Crowded(p, _id, MercSquad.Spread, i.Now)) return false;
            Vector3 t = Primary(ref i);
            switch (_plan)
            {
                case PlanMode:
                    if (Mode == Falling) return i.Regroup && Flat(p - i.Owner) < FallNear + 12f;
                    return Flat(p - t) >= Flat(i.Me - t) - 2f && Flat(p - i.Me) <= RetreatReach;
                case PlanFlank:
                {
                    Vector3 a = (Cover.Found ? Cover.Point.Pos : i.Me) - t, b = p - t;
                    a.y = 0f; b.y = 0f;
                    if (Flat(b) < 20f || Flat(a) < 1f) return false;
                    float cos = (a.x * b.x + a.z * b.z) / (Flat(a) * Flat(b));
                    return cos <= Mathf.Cos(FlankGain * Mathf.Deg2Rad);
                }
                case PlanUpgrade:
                    return Flat(p - i.Me) < 25f && Better(ref i.Pick);
                default:
                    return true;
            }
        }

        bool Better(ref CoverPick p)
        {
            return p.Covered > Cover.Covered || (p.PeekVerified && !Cover.PeekVerified && _blindPeeks > 0);
        }

        /// <summary>In cover, down: start a planned move when one is due.</summary>
        void MaybePlan(ref FightIn i, ref FightOut o)
        {
            float now = i.Now;
            if (_plan != PlanNone || now < _nextPlan || Mode == Retreating) return;
            _nextPlan = now + 0.5f;
            if (Squad != null && Squad.Yield(Cover.Point.Pos, _id, MercSquad.Spread * 0.7f, now))
            {
                _plan = PlanSpread; _planUntil = now + 3f; o.Repick = true;
                return;
            }
            if (Grade >= UpgradeGrade && now - _coverSince > UpgradeAfter && now - _lastMoveAt > 6f
                && Usable(ref i) && Flat(i.Pick.Point.Pos - Cover.Point.Pos) > 3f && Better(ref i.Pick))
            {
                _plan = PlanUpgrade; _planUntil = now + 1.5f;
                return;
            }
            // A flank only where this cover is stalled: his peeks find nobody,
            // or he has not seen a target for a while. From a cover he can
            // fire from, he fires.
            bool stalled = _blindPeeks > 0 || now - i.LastSeen > 3f;
            if (Squad == null || Mode != Normal || Grade < FlankGrade || i.Health < 0.6f || i.Count == 0 || !stalled
                || now - _coverSince < FlankAfter || now - _fightStart < 5f || now < _nextFlankTry
                || Squad.MatesDown(_id, now) < 1) return;
            Vector3 t = i.Threats[0];
            Vector3 v = i.Me - t;
            v.y = 0f;
            float d = Flat(v);
            if (d < 20f || !Squad.TakeFlank(_id, now, 6f)) { _nextFlankTry = now + 4f; return; }
            // Round the threat, to the side away from his mates.
            Vector3 mates;
            float sign = Next01() < 0.5f ? 1f : -1f;
            if (Squad.MatesFrom(t, _id, now, out mates))
                sign = (v.x * mates.z - v.z * mates.x) > 0f ? 1f : -1f;
            float ang = sign * FlankTurn * Mathf.Deg2Rad;
            float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
            float keep = Mathf.Max(d, 30f) / d;
            _anchor = t + new Vector3((v.x * cs - v.z * sn) * keep, 0f, (v.x * sn + v.z * cs) * keep);
            _anchorOn = true;
            _plan = PlanFlank;
            _planUntil = now + 4f;
            _nextFlankTry = now + 12f;
        }

        /// <summary>A planned move: its pick, cover for the run, and off.
        /// True: this Think is decided (waiting for cover, or running).</summary>
        bool StepPlan(ref FightIn i, CoverField field, ref FightOut o)
        {
            float now = i.Now;
            if (!PlanOk(ref i))
            {
                if (now < _planUntil) return false;
                // No point for it in time.
                byte gone = _plan;
                _plan = PlanNone;
                _callSince = 0f;
                if (gone == PlanFlank)
                {
                    _anchorOn = false;
                    if (Squad != null) Squad.EndFlank(_id, now);
                }
                else if (gone == PlanMode && Mode == Falling && i.Regroup)
                {
                    // Nothing near the owner: run to his side, cover from there.
                    StartRunTo(ref i, ref o, _anchor);
                    PlannedMoves++;
                    return true;
                }
                return false;
            }
            if (Squad != null && !Squad.MateUp(_id, now) && Squad.MatesDown(_id, now) > 0)
            {
                if (_callSince <= 0f) _callSince = now;
                if (now - _callSince < CallWait)
                {
                    _call = true;
                    HoldLow(ref i, ref o);
                    return true;
                }
            }
            if (Squad != null && Squad.MateUp(_id, now)) CoveredMoves++;
            PlannedMoves++;
            byte plan = _plan;
            _plan = PlanNone;
            _callSince = 0f;
            if (plan == PlanFlank) { Mode = Flanking; FlankRuns++; }
            else if (plan == PlanUpgrade) Upgrades++;
            else if (plan == PlanSpread) Spreads++;
            field.Release(_id);
            StartDash(ref i, ref o);
            return true;
        }

        /// <summary>A run to a point, then cover from there (the flight's run).</summary>
        void StartRunTo(ref FightIn i, ref FightOut o, Vector3 dest)
        {
            Cover = new CoverPick();
            Dest = dest;
            _until = i.Now + 2f + Flat(dest - i.Me) / 10f;
            _evadeUp = false;
            _lastMoveAt = i.Now;
            Enter(Flee, i.Now);
            o.Act = FightAct.Run;
            o.Dest = Dest;
        }

        void Enter(byte s, float now)
        {
            State = s;
            Since = now;
        }

        // ---------------------------------------------------------- choices

        bool Bad(Vector3 p, float now)
        {
            return (now < _badUntil0 && Flat(p - _bad0) < 3f) || (now < _badUntil1 && Flat(p - _bad1) < 3f);
        }

        void MarkBad(CoverField field, float now, float seconds)
        {
            if (!Cover.Found) return;
            field.MarkBad(ref Cover, now, seconds);
            // Terrain crests cannot be marked in the field: kept here too.
            if (now >= _badUntil0 || _badUntil0 <= _badUntil1) { _bad0 = Cover.Point.Pos; _badUntil0 = now + seconds; }
            else { _bad1 = Cover.Point.Pos; _badUntil1 = now + seconds; }
        }

        /// <summary>A sense pick worth running for now.</summary>
        bool Usable(ref FightIn i)
        {
            if (!i.Pick.Found || !i.Pick.Confirmed || !i.PickFresh) return false;
            Vector3 p = i.Pick.Point.Pos;
            if (Bad(p, i.Now)) return false;
            if (i.Survive && _anchorOn && !i.Rally && i.Count > 0 &&
                Flat(p - i.Threats[0]) < Flat(i.Me - i.Threats[0]) + 4f) return false;
            // M3: a pick searched from his anchor (flank, fall-back, retreat)
            // is his only while the anchor holds; else one from where he is.
            if (_anchorOn)
            {
                if (Flat(i.PickFrom - _anchor) > 3f || Flat(p - i.Me) > AnchorReach) return false;
            }
            else if (Flat(p - i.Me) > PickReach || Flat(i.PickFrom - i.Me) > 15f) return false;
            if (i.Danger && Flat(p - i.DangerAt) < DangerRadius) return false;
            return true;
        }

        /// <summary>Where to now: the sense's cover, else the open-ground drill.</summary>
        void Choose(ref FightIn i, ref FightOut o)
        {
            if (Usable(ref i)) { StartDash(ref i, ref o); return; }
            StartEvade(ref i, ref o);
        }

        void StartDash(ref FightIn i, ref FightOut o)
        {
            Cover = i.Pick;
            Dest = Cover.Point.Pos;
            _until = i.Now + 2.5f + Flat(Dest - i.Me) / 6f;
            _evadeUp = false;
            _nextClaim = 0f;
            _lastMoveAt = i.Now;
            Dashes++;
            Enter(Dash, i.Now);
            if (Flat(Dest - i.Me) < ArriveCover) { Arrived(ref i); StartHide(ref i, ref o, true); return; }
            o.Act = FightAct.Run;
            o.Dest = Dest;
        }

        void StartHide(ref FightIn i, ref FightOut o, bool arrived)
        {
            Enter(Hide, i.Now);
            _alarmAt = 0f;
            // Just arrived: a short look first; back from a peek: the drawn wait.
            _nextPeek = i.Now + (arrived ? Range(0.3f, 0.8f) * Pace() : HideTime());
            HoldLow(ref i, ref o);
        }

        /// <summary>At the end of a dash: in this cover since now; a flank
        /// is done, a fall-back has reached the owner.</summary>
        void Arrived(ref FightIn i)
        {
            _coverSince = i.Now;
            if (i.Survive) _anchorOn = false;
            if (Mode == Flanking) EndMode(i.Now);
            if (Mode == Falling && i.Regroup && Flat(i.Me - i.Owner) < FallNear + 12f) _regrouped = true;
        }

        /// <summary>M3: a higher grade peeks sooner (x1.25 at grade 0 .. x0.75 at 1).</summary>
        float Pace() { return MercGrade.Lerp(Grade, 1.25f, 0.75f); }

        float HideTime()
        {
            float t = Next01() < LongHide ? Range(2.4f, 3.4f) : Range(HideMin, HideMax);
            // Under fire he stays down longer - never so long that the
            // fight ends between two peeks (Disengage is 6..8 s). Retreating
            // he stays down longer still.
            t *= Pace() * (Mode == Retreating ? 1.8f : 1f);
            return Mathf.Min(t * (1f + 1.5f * _pressure), Mode == Retreating ? HideCap + 1.5f : HideCap);
        }

        void HoldLow(ref FightIn i, ref FightOut o)
        {
            o.Act = FightAct.Hold;
            o.Low = true;
            o.Face = Primary(ref i);
        }

        /// <summary>His cover failed (seen in it, hit in it, he could not get
        /// there): marked bad for everyone, a new pick asked for, and off.</summary>
        void Relocate(ref FightIn i, CoverField field, ref FightOut o)
        {
            if (i.Survive) { SurvivalBack(ref i, field, ref o); return; }
            MarkBad(field, i.Now, BadSeconds);
            Relocations++;
            Cover = new CoverPick();
            field.Release(_id);
            o.Repick = true;
            _callSince = 0f;
            // A planned flank is off; a retreat or a fall-back keeps its anchor.
            if (_plan == PlanFlank) { _anchorOn = false; if (Squad != null) Squad.EndFlank(_id, i.Now); }
            if (_plan != PlanMode) _plan = PlanNone;
            if (_anchorOn && !Usable(ref i)) { _anchorOn = false; _plan = PlanNone; }
            Choose(ref i, ref o);
        }

        // ------------------------------------------------------------ steps

        void StepDash(ref FightIn i, CoverField field, ref FightOut o)
        {
            float d = Flat(Cover.Point.Pos - i.Me);
            if (d < ArriveCover) { Arrived(ref i); StartHide(ref i, ref o, true); return; }
            if (i.Now >= _until || Stuck(ref i)) { Relocate(ref i, field, ref o); return; }
            // A clearly nearer cover turned up (a cell got mapped meanwhile).
            if (d > 6f && Usable(ref i) && Flat(i.Pick.Point.Pos - Cover.Point.Pos) > 3f
                && Flat(i.Pick.Point.Pos - i.Me) + 6f < d)
            {
                StartDash(ref i, ref o);
                return;
            }
            o.Act = FightAct.Run;
            o.Dest = Cover.Point.Pos;
        }

        /// <summary>M3: a run that gets nowhere (a wall the path does not go
        /// round, a mate in the way): StuckAfter seconds with no progress.</summary>
        bool Stuck(ref FightIn i)
        {
            if (i.Now - Since < 0.4f || _moved > 0.15f) { _stuckSince = 0f; return false; }
            if (_stuckSince <= 0f) _stuckSince = i.Now;
            if (i.Now - _stuckSince < StuckAfter) return false;
            _stuckSince = 0f;
            return true;
        }

        /// <summary>Seen crouched at his point (measured after he got there),
        /// or hit while down.</summary>
        bool Compromised(ref FightIn i, bool hit)
        {
            if (hit) { _alarmAt = 0f; return true; }
            bool seen = i.Exposed && i.SensedAt >= Since + 0.05f && i.Now - i.SensedAt < 0.8f
                && Flat(Cover.Point.Pos - i.Me) < ArriveCover + 0.5f;
            if (!seen) { _alarmAt = 0f; return false; }
            // M3: how fast he notices depends on his grade.
            if (_alarmAt <= 0f) _alarmAt = i.Now;
            if (i.Now - _alarmAt < ReactTime()) return false;
            _alarmAt = 0f;
            return true;
        }

        void StepHide(ref FightIn i, CoverField field, ref FightOut o, bool hit)
        {
            if (Compromised(ref i, hit)) { Flanks++; Relocate(ref i, field, ref o); return; }
            float off = Flat(Cover.Point.Pos - i.Me);
            if (off > ArriveCover + 0.5f)
            {
                // Pushed off his point (a mate, the NavMesh): back onto it.
                o.Act = FightAct.Step;
                o.Dest = Cover.Point.Pos;
                o.Face = Primary(ref i);
                return;
            }
            bool quiet = !i.Survive || (!SurvivalFire && !i.Exposed && i.Now >= _defendUntil);
            if (quiet && (i.Reloading || NeedReload(ref i)))
            {
                Enter(Reloading, i.Now);
                _until = i.Now + ReloadCap;
                Reloads++;
                HoldLow(ref i, ref o);
                o.Act = FightAct.Reload;
                o.Kick = !i.Reloading;
                return;
            }
            if (quiet && _healsLeft > 0 && i.Health > 0f && i.Health < HealBelow && i.Now >= _nextHeal)
            {
                Enter(Healing, i.Now);
                _until = i.Now + HealSeconds;
                HoldLow(ref i, ref o);
                o.Act = FightAct.Heal;
                return;
            }
            HoldLow(ref i, ref o);
            if (i.Survive)
            {
                if (SurvivalFire && i.Count > 0 && i.Now >= _nextPeek)
                    TryPeek(ref i, field, ref o, false);
                return;
            }
            // M3: covering fire for a mate who moves (nobody else is up).
            if (Squad != null && Mode != Retreating && _plan == PlanNone && i.Count > 0 && i.Now >= _nextCover
                && Squad.MateNeedsCover(_id, i.Now) && !Squad.MateUp(_id, i.Now))
            {
                _nextCover = i.Now + 1f;
                if (TryPeek(ref i, field, ref o, true)) { Covers++; return; }
            }
            // M3: a planned move (spread, better cover, flank, fall-back, retreat).
            MaybePlan(ref i, ref o);
            if (_plan != PlanNone && StepPlan(ref i, field, ref o)) return;
            // Retreating: down, unless a threat comes close.
            if (Mode == Retreating && !Pressed(ref i)) return;
            if (i.Now < _nextPeek || i.Count == 0) return;
            if (_blindPeeks < (Grade >= 0.67f ? 2 : 3) && TryPeek(ref i, field, ref o, false)) return;
            // No side sees anyone from here (the middle of a long wall, the
            // threat moved round) or three peeks found nobody: wait low, and
            // take a point with a proven peek when the sense has one.
            Blind++;
            _nextPeek = i.Now + Range(1.2f, 2.0f);
            o.Repick = true;
            if (Usable(ref i) && i.Pick.PeekVerified && Flat(i.Pick.Point.Pos - Cover.Point.Pos) > 3f)
            {
                _blindPeeks = 0;
                Relocations++;
                StartDash(ref i, ref o);
                return;
            }
            // Nothing better: try the peeks again in a while.
            if (_blindPeeks >= 3 && i.Now - Since > 6f) _blindPeeks = 0;
        }

        bool NeedReload(ref FightIn i)
        {
            if (i.MaxRounds > 0) return i.Rounds < Mathf.Max(3f, i.MaxRounds * ReloadShare);
            return _estFired >= EstimatedMagazine * (1f - ReloadShare);
        }

        // Scratch for the peek choice (no allocation per Think).
        readonly byte[] _optSide = new byte[3];
        readonly Vector3[] _optAt = new Vector3[3];
        readonly int[] _optThreat = new int[3];

        /// <summary>Pick a side for this peek and prove it with a ray: sides
        /// that see the primary threat first; not the side he was just hit
        /// on, not a third time the same one; the rest by chance.</summary>
        bool TryPeek(ref FightIn i, CoverField field, ref FightOut o, bool covering)
        {
            int n = 0;
            if (Cover.Ground)
            {
                for (int t = 0; t < i.Count && n == 0; t++)
                {
                    Vector3 at;
                    if (field.Peek(ref Cover, i.Threats[t], false, out at) == CoverPeek.None) continue;
                    if (FriendInLine(ref i, at, i.Threats[t])) continue;
                    _optSide[0] = CoverPeek.Over; _optAt[0] = at; _optThreat[0] = t; n = 1;
                }
            }
            else
            {
                for (int k = 0; k < 3; k++)
                {
                    byte flag = k == 0 ? CoverPeek.Over : k == 1 ? CoverPeek.Left : CoverPeek.Right;
                    if ((Cover.Point.Peek & flag) == 0) continue;
                    CoverPoint one = Cover.Point;
                    one.Peek = flag;
                    for (int t = 0; t < i.Count; t++)
                    {
                        Vector3 at;
                        if (CoverField.PeekToward(ref one, i.Threats[t], out at) == CoverPeek.None) continue;
                        if (FriendInLine(ref i, at, i.Threats[t])) continue;
                        _optSide[n] = flag; _optAt[n] = at; _optThreat[n] = t; n++;
                        break;
                    }
                }
            }
            for (int rays = 0; rays < PeekRays && rays < n; rays++)
            {
                int best = -1;
                float bestScore = float.MinValue;
                for (int k = 0; k < n; k++)
                {
                    if (_optSide[k] == CoverPeek.None) continue;
                    float s = (_optThreat[k] == 0 ? 2f : 0f) + Next01();
                    if (_optSide[k] == _hitSide) s -= 1.5f;
                    if (_optSide[k] == LastSide) s -= _sameSide >= 2 ? 4f : 0.7f;
                    if (s > bestScore) { bestScore = s; best = k; }
                }
                if (best < 0) break;
                byte side = _optSide[best];
                Vector3 at = _optAt[best];
                Vector3 threat = i.Threats[_optThreat[best]];
                _optSide[best] = CoverPeek.None;
                if (!field.Sees(at + Vector3.up * CoverField.StandEye, threat + Vector3.up * CoverField.Chest))
                    continue;
                _sameSide = side == LastSide ? _sameSide + 1 : 1;
                PeekSide = side;
                PeekAt = at;
                Peeks++;
                _fired = 0f;
                _plantedAt = 0f;
                _seenAt = 0f;
                _upSince = i.Now;
                _blockedFor = 0f;
                _suppress = covering;
                _burstLen = covering ? Range(SuppressMin, SuppressMax) : Range(BurstMin, BurstMax);
                o.Face = threat;
                if (Flat(at - Cover.Point.Pos) < 0.6f)
                {
                    // Over a low face: he only stands up.
                    Enter(Burst, i.Now);
                    o.Act = FightAct.Fire;
                    return true;
                }
                Enter(PeekOut, i.Now);
                _until = i.Now + 0.8f + Flat(at - i.Me) / 4f;
                o.Act = FightAct.Step;
                o.Dest = at;
                return true;
            }
            if (!covering) _blindPeeks++;
            return false;
        }

        void StepPeekOut(ref FightIn i, ref FightOut o, bool hit)
        {
            if (hit) { _hitSide = PeekSide; LastSide = PeekSide; StartBack(ref i, ref o); return; }
            o.Face = Primary(ref i);
            float d = Flat(PeekAt - i.Me);
            if (d < ArrivePeek || (d < PeekStall && _moved < 0.1f && i.Now - Since > 0.25f))
            {
                Enter(Burst, i.Now);
                o.Act = FightAct.Fire;
                return;
            }
            if (i.Now >= _until) { LastSide = PeekSide; StartBack(ref i, ref o); return; }
            o.Act = FightAct.Step;
            o.Dest = PeekAt;
        }

        /// <summary>Up and firing: a short burst once planted and seeing a
        /// target, then down again - early when hit, dry, or nothing is seen.</summary>
        void StepBurst(ref FightIn i, ref FightOut o, bool hit)
        {
            if (i.Planted && _plantedAt <= 0f) _plantedAt = i.Now;
            // M3: never through a mate or the owner.
            bool blocked = FriendInLine(ref i, i.Me, Primary(ref i));
            if (blocked) { _blockedFor += _dt; HeldFire++; } else _blockedFor = 0f;
            if (i.Planted && i.Sees && !blocked) { _fired += _dt; _estFired += _dt * RoundsPerSecond; _seenAt = i.Now; }
            else if (_suppress && i.Planted && !blocked && i.Count > 0)
            {
                // Covering fire: at the threat's last spot while it is out of sight.
                _fired += _dt; _estFired += _dt * RoundsPerSecond; SuppressTime += _dt;
            }
            float lastLook = Mathf.Max(_plantedAt, _seenAt);
            bool blind = !_suppress && i.Planted && !i.Sees && _plantedAt > 0f && i.Now - lastLook > PeekBlind;
            bool dry = (i.MaxRounds > 0 && i.Rounds <= 1) || i.Reloading;
            bool friend = _blockedFor > BlockedCap;
            if (hit || dry || blind || friend || _fired >= _burstLen || i.Now - _upSince > PeekCap)
            {
                if (_fired > 0f) { Bursts++; _blindPeeks = 0; }
                else { Blind++; if (!_suppress) _blindPeeks++; }
                _hitSide = hit || friend ? PeekSide : PeekSide == _hitSide ? CoverPeek.None : _hitSide;
                LastSide = PeekSide;
                _suppress = false;
                StartBack(ref i, ref o);
                return;
            }
            o.Act = FightAct.Fire;
            o.Face = Primary(ref i);
            o.NoShot = blocked;
            o.Suppress = _suppress && !blocked && !i.Sees;
        }

        void StartBack(ref FightIn i, ref FightOut o)
        {
            if (Flat(Cover.Point.Pos - i.Me) < ArriveCover)
            {
                StartHide(ref i, ref o, false);
                return;
            }
            Enter(PeekBack, i.Now);
            _until = i.Now + 1.2f + Flat(Cover.Point.Pos - i.Me) / 4f;
            o.Act = FightAct.Step;
            o.Dest = Cover.Point.Pos;
            o.Face = Primary(ref i);
        }

        void StepPeekBack(ref FightIn i, CoverField field, ref FightOut o)
        {
            float d = Flat(Cover.Point.Pos - i.Me);
            if (d < ArriveCover) { StartHide(ref i, ref o, false); return; }
            if (i.Now >= _until) { Relocate(ref i, field, ref o); return; }
            o.Act = FightAct.Step;
            o.Dest = Cover.Point.Pos;
            o.Face = Primary(ref i);
        }

        void StepReload(ref FightIn i, CoverField field, ref FightOut o, bool hit)
        {
            if (Compromised(ref i, hit)) { Flanks++; Relocate(ref i, field, ref o); return; }
            bool full = i.MaxRounds > 0 ? i.Rounds >= i.MaxRounds * 0.9f : i.Now - Since > 0.6f;
            if ((!i.Reloading && full && i.Now - Since > 0.3f) || i.Now >= _until)
            {
                _estFired = 0f;
                StartHide(ref i, ref o, true);
                return;
            }
            HoldLow(ref i, ref o);
            o.Act = FightAct.Reload;
            // The game did not take the first kick: once more after a second.
            o.Kick = !i.Reloading && !full && i.Now - Since >= 1f && i.Now - Since < 1f + _dt;
        }

        void StepHeal(ref FightIn i, CoverField field, ref FightOut o, bool hit)
        {
            if (Compromised(ref i, hit)) { Flanks++; Relocate(ref i, field, ref o); return; }
            if (i.Now >= _until)
            {
                Heals++;
                _healsLeft--;
                _nextHeal = i.Now + HealGap;
                StartHide(ref i, ref o, true);
                o.HealNow = true;
                return;
            }
            HoldLow(ref i, ref o);
            o.Act = FightAct.Heal;
        }

        // ------------------------------------------------------- no cover

        void StartEvade(ref FightIn i, ref FightOut o)
        {
            Enter(Evade, i.Now);
            _evadeUp = false;
            NewStrafe(ref i);
            StepEvade(ref i, ref o, false);
        }

        /// <summary>A strafe run across the primary threat's line, the side
        /// mostly alternating, pulled back from a threat nearer than 25 units.</summary>
        void NewStrafe(ref FightIn i)
        {
            Vector3 to = Primary(ref i) - i.Me;
            to.y = 0f;
            float d = Flat(to);
            Vector3 fwd = d > 0.5f ? to / d : new Vector3(0f, 0f, 1f);
            Vector3 side = new Vector3(fwd.z, 0f, -fwd.x);
            if (Next01() < 0.8f) _evadeSign = -_evadeSign;
            float len = Range(StrafeMin, StrafeMax);
            Vector3 move = side * (_evadeSign * len);
            if (d < 25f) move = move - fwd * (len * 0.5f);
            Dest = i.Me + move;
            _until = i.Now + StrafeCap;
            Strafes++;
        }

        void StepEvade(ref FightIn i, ref FightOut o, bool mayDash)
        {
            if (mayDash && Usable(ref i)) { StartDash(ref i, ref o); return; }
            if (_evadeUp)
            {
                if (i.Planted && _plantedAt <= 0f) _plantedAt = i.Now;
                bool blocked = FriendInLine(ref i, i.Me, Primary(ref i));
                if (blocked) HeldFire++;
                if (i.Planted && i.Sees && !blocked) { _fired += _dt; _estFired += _dt * RoundsPerSecond; _seenAt = i.Now; }
                bool blind = i.Planted && !i.Sees && _plantedAt > 0f && i.Now - Mathf.Max(_plantedAt, _seenAt) > 0.5f;
                if (_fired >= _burstLen || blind || i.Now - _upSince > EvadeUpCap || i.Reloading
                    || (i.MaxRounds > 0 && i.Rounds <= 1))
                {
                    if (_fired > 0f) Bursts++;
                    _evadeUp = false;
                    NewStrafe(ref i);
                }
                else
                {
                    o.Act = FightAct.Fire;
                    o.Face = Primary(ref i);
                    o.NoShot = blocked;
                    return;
                }
            }
            bool seen = (i.Exposed && i.Now - i.SensedAt < 0.8f) || i.Now - _lastHit < 2f;
            if (Order.Act == FightAct.Run && Stuck(ref i)) { _evadeSign = -_evadeSign; NewStrafe(ref i); Since = i.Now; }
            if (Flat(Dest - i.Me) < ArriveCover * 1.5f || i.Now >= _until)
            {
                bool low = NeedReload(ref i);
                if (i.Sees && !i.Reloading && !low && Order.Act == FightAct.Run
                    && (Mode != Retreating || Pressed(ref i)))
                {
                    // Between two runs one short burst, standing: he cannot fire lower.
                    _evadeUp = true; _upSince = i.Now; _fired = 0f; _plantedAt = 0f; _seenAt = 0f;
                    _burstLen = Range(EvadeBurstMin, EvadeBurstMax);
                    o.Act = FightAct.Fire;
                    o.Face = Primary(ref i);
                    return;
                }
                if (!seen && (!i.Sees || low || i.Reloading))
                {
                    // Nobody sees him: down, and ask again for cover now and
                    // then. A low magazine is filled here, never in a pop-up
                    // and never on the run (a move order cuts the game's reload).
                    HoldLow(ref i, ref o);
                    if (low || i.Reloading)
                    {
                        o.Act = FightAct.Reload;
                        if (!i.Reloading && i.Now - _kickAt > 1f)
                        {
                            o.Kick = true;
                            _kickAt = i.Now;
                            _estFired = 0f;
                            Reloads++;
                        }
                    }
                    if (i.Now - Since > 4f) { o.Repick = true; Since = i.Now; }
                    return;
                }
                NewStrafe(ref i);
            }
            o.Act = FightAct.Run;
            o.Dest = Dest;
        }

        // ---------------------------------------------------------- danger

        void StartFlee(ref FightIn i, CoverField field, ref FightOut o)
        {
            if (Cover.Found && Flat(Cover.Point.Pos - i.DangerAt) < DangerRadius)
            {
                MarkBad(field, i.Now, 20f);
                Cover = new CoverPick();
                field.Release(_id);
            }
            Flees++;
            o.Repick = true;
            _evadeUp = false;
            if (Usable(ref i)) { StartDash(ref i, ref o); return; }
            Vector3 away = i.Me - i.DangerAt;
            away.y = 0f;
            float d = Flat(away);
            if (d < 0.5f)
            {
                away = i.Me - Primary(ref i); away.y = 0f; d = Flat(away);
                if (d < 0.5f) { away = new Vector3(1f, 0f, 0f); d = 1f; }
            }
            Dest = i.Me + away / d * FleeDistance;
            _until = i.Now + 3f;
            _lastMoveAt = i.Now;
            Enter(Flee, i.Now);
            o.Act = FightAct.Run;
            o.Dest = Dest;
        }

        void StepFlee(ref FightIn i, ref FightOut o)
        {
            if (Usable(ref i)) { StartDash(ref i, ref o); return; }
            bool clear = !i.Danger || Flat(i.DangerAt - i.Me) >= DangerRadius;
            if ((clear && (Flat(Dest - i.Me) < ArriveCover * 2f || Stuck(ref i))) || i.Now >= _until)
            {
                Choose(ref i, ref o);
                return;
            }
            o.Act = FightAct.Run;
            o.Dest = Dest;
        }
    }
}

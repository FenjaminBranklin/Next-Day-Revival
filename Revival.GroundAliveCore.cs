// Editor ground groups, the alive layer - the decision core. Pure code: it
// knows Vector3 and Mathf and asks IGroundWorld for every ray, ground sample,
// path and player test, so research/ground_alive_check.py compiles this file
// unchanged into an offline simulation. In the game Revival.GroundAlive.cs
// answers with the M1 cover world's rays, the NavMesh and the player list
// (docs/ai/tasks/editor-npcs-alive.md).
//
//   BOARD    every editor group has a brain. A group with no player within
//            WakeRange sleeps: it costs one proximity test a second, and the
//            board looks at no more than ScanPerFrame brains a frame however
//            many groups there are. An awake group thinks four times a
//            second, at most ThinksPerFrame groups in one frame.
//   CALM     'roam' (guard with roam, the default): posts spread on a
//            sunflower inside the radius, at most a third of the men out on a
//            slow round of two or three points inside the radius, idle pauses
//            and looking around at the post. 'guard' (hold position),
//            'walking' (wander) and 'patrol' keep their NpcWar movement
//            while calm (GroundAct.Duty).
//   CONTACT  the group spreads: one man calls the friendly groups near by, a
//            quarter of the men flank wide of the threat, the rest take the
//            nearest cached cover that faces it or step apart from the group's
//            centre line; they crouch there and stand up to fire when they see
//            the threat. A called group sends half its men toward the threat.
//   SEARCH   ContactMemory seconds after the last sighting two men walk to
//            points around the last known threat position, the rest stand up
//            and watch it; after SearchTime everyone walks back to his post.
//   COVER    a simplified, cached variant of the M1 merc cover field: one
//            cache per group, built once when a player first comes near,
//            time-sliced by a world-call budget - 32 ground probes around the
//            home, eight crouch-height rays each; a face within reach is a
//            spot, a second ray at standing eye height tells a wall from a
//            low wall. A query is arithmetic only, no ray.
//
// Everything runs on the Photon master (NpcWar). No allocation after a brain
// is made. Units: game units, 2.8 per metre. C# 3.0, ASCII only.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>What the alive layer asks the world.</summary>
    internal interface IGroundWorld
    {
        /// <summary>The first solid hit along a flat ray (people are no hit).</summary>
        bool Cast(Vector3 from, Vector3 dir, float range, out float distance);

        /// <summary>The nearest spot a man can stand on within reach.</summary>
        bool Stand(Vector3 near, float reach, out Vector3 at);

        /// <summary>A complete walk from one point to another whose every
        /// corner lies within leash of home.</summary>
        bool Walk(Vector3 from, Vector3 to, Vector3 home, float leash);

        /// <summary>Is any player within range of a point?</summary>
        bool PlayerNear(Vector3 p, float range);
    }

    /// <summary>The calm duty a group was given in the editor.</summary>
    internal static class GroundDuty
    {
        internal const byte Roam = 0, Hold = 1, Wander = 2, Patrol = 3;
    }

    /// <summary>The group's state.</summary>
    internal static class GroundPhase
    {
        internal const byte Calm = 0, Contact = 1, Search = 2;

        static readonly string[] Names = new string[] { "calm", "contact", "search" };

        internal static string Name(byte p) { return p < Names.Length ? Names[p] : "?"; }
    }

    /// <summary>What one man is doing.</summary>
    internal static class GroundAct
    {
        // Duty: the NpcWar duty code moves him (hold / wander / patrol, calm).
        internal const byte Duty = 0, Post = 1, Round = 2, Cover = 3, Flank = 4, Spread = 5, Watch = 6, Search = 7;
    }

    /// <summary>One man as the brain sees him.</summary>
    internal sealed class GroundMan
    {
        // Inputs, written by the adapter before every think.
        // Hurt: his own AI is hunting a player (it was shot at or spotted
        // one) - a contact even while he cannot see him.
        public bool Alive, Sees, HasThreat, Hurt;
        public bool Fixed;              // a building post: he never leaves it
        public Vector3 Pos, Threat;

        // Outputs. A new move order is due while Order != Issued; the
        // adapter sets Issued when it has sent it.
        public byte Act;
        public bool Moving, Run, Crouch;
        public Vector3 Dest, Look;
        public int Order, Issued;

        // Own state.
        public Vector3 Post;
        public bool HasPost;
        public int Cover = -1;
        public float Until, NextLook, NextRound, Deadline;
        public int Legs;
        public bool Flanker;
    }

    /// <summary>A cached cover spot: where a man crouches and the way the
    /// face he crouches behind lies from him.</summary>
    internal struct GroundSpot
    {
        public Vector3 Pos, Face;
        public bool Tall;
        public int Owner;
    }

    /// <summary>The per-group cover cache (see the file head).</summary>
    internal sealed class GroundCoverCache
    {
        internal const int Probes = 32, Dirs = 8, MaxSpots = 64, PerProbe = 2;
        internal const float CrouchEye = 3.1f;   // 1.1 m: a crouching man's eye
        internal const float StandEye = 4.8f;    // 1.7 m: a standing man's eye
        internal const float FaceReach = 14f;    // 5 m: a face he can tuck in behind
        internal const float Tuck = 1.9f;        // his spot this far off the face

        public readonly GroundSpot[] Spots = new GroundSpot[MaxSpots];
        public int Count, Calls;
        public bool Done;

        Vector3 _home, _at, _first;
        float _reach;
        int _probe, _dir = -1, _taken;

        internal void Reset(Vector3 home, float reach)
        {
            _home = home; _reach = reach;
            Count = 0; Done = false; _probe = 0; _dir = -1; _taken = 0;
        }

        Vector3 ProbePoint(int k)
        {
            float r = _reach * Mathf.Sqrt((k + 0.5f) / Probes);
            float a = k * 2.39996f;
            return _home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }

        /// <summary>Map on, spending at most budget world calls; returns
        /// what it spent.</summary>
        internal int Build(IGroundWorld w, int budget)
        {
            int spent = 0;
            while (!Done && spent < budget)
            {
                if (_probe >= Probes || Count >= MaxSpots) { Done = true; break; }
                if (_dir < 0)
                {
                    spent++;
                    if (w.Stand(ProbePoint(_probe), 6f, out _at)) { _dir = 0; _taken = 0; }
                    else _probe++;
                    continue;
                }
                if (_dir >= Dirs || _taken >= PerProbe) { _probe++; _dir = -1; continue; }
                float a = _dir * (Mathf.PI * 2f / Dirs) + _probe * 0.37f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                _dir++;
                float d;
                spent++;
                if (!w.Cast(_at + Vector3.up * CrouchEye, dir, FaceReach, out d) || d < 0.8f) continue;
                if (_taken > 0 && Vector3.Dot(dir, _first) > 0.5f) continue;
                float high;
                spent++;
                bool tall = w.Cast(_at + Vector3.up * StandEye, dir, d + 1.5f, out high);
                Vector3 spot = _at + dir * Mathf.Max(0f, d - Tuck), snapped;
                spent++;
                if (!w.Stand(spot, 2f, out snapped)) snapped = _at;
                GroundSpot s;
                s.Pos = snapped; s.Face = dir; s.Tall = tall; s.Owner = -1;
                Spots[Count++] = s;
                if (_taken == 0) _first = dir;
                _taken++;
            }
            Calls += spent;
            return spent;
        }

        static float FlatLen(Vector3 v) { v.y = 0f; return v.magnitude; }

        /// <summary>How well spot i hides from a threat: the cosine between
        /// its face and the way to the threat (1: the face is square on).</summary>
        internal float Facing(int i, Vector3 threat)
        {
            Vector3 t = threat - Spots[i].Pos;
            t.y = 0f;
            float d = t.magnitude;
            return d < 0.01f ? -1f : Vector3.Dot(Spots[i].Face, t / d);
        }

        /// <summary>The best free spot within maxMove of from that faces the
        /// threat and lies at least minThreat from it; -1: none. No ray.</summary>
        internal int Best(Vector3 from, Vector3 threat, float maxMove, float minThreat, int who)
        {
            int best = -1;
            float score = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                if (Spots[i].Owner >= 0 && Spots[i].Owner != who) continue;
                if (FlatLen(threat - Spots[i].Pos) < minThreat) continue;
                float facing = Facing(i, threat);
                if (facing < 0.55f) continue;
                float move = FlatLen(Spots[i].Pos - from);
                if (move > maxMove) continue;
                float s = move + (Spots[i].Tall ? 0f : 8f) + (1f - facing) * 20f;
                if (s < score) { score = s; best = i; }
            }
            return best;
        }

        internal void Claim(int i, int who) { if (i >= 0 && i < Count) Spots[i].Owner = who; }

        internal void Release(int who)
        {
            for (int i = 0; i < Count; i++) if (Spots[i].Owner == who) Spots[i].Owner = -1;
        }
    }

    /// <summary>One editor group's decisions (see the file head).</summary>
    internal sealed class GroundBrain
    {
        internal const float WakeRange = 700f;      // 250 m: a player this close wakes the group
        internal const float NearEvery = 1f;        // proximity test interval
        internal const float ThinkEvery = 0.25f;    // 4 Hz
        internal const float CallRange = 350f;      // 125 m: friends a caller reaches
        internal const float CalledAwake = 60f;     // a called group stays awake this long
        internal const float ContactMemory = 15f;   // no sighting this long: search
        internal const float SearchTime = 30f;
        internal const float CoverMove = 45f;       // 16 m: the furthest a man goes for cover
        internal const float CoverMinThreat = 20f;  // 7 m: cover closer to the threat is none
        internal const float Spacing = 20f;         // 7 m between roam posts
        internal const float LeashExtra = 50f;      // 18 m past the radius in a fight
        internal const float Arrive = 3f;

        public readonly GroundMan[] Men;
        public readonly int Count;
        public readonly GroundCoverCache Cover = new GroundCoverCache();
        public Vector3 Home, Centre, Threat;
        public float Radius, Leash;
        public byte Duty, Phase;
        // Who counts as a friend for a call: groups with the same side. The
        // adapter gives every traitor group a side of its own (traitors shoot
        // traitors), the other factions share one per faction.
        public int Side;
        public bool Active, Ready;
        public float NextNear, NextThink, ActiveUntil, LastContact, PhaseSince, SearchUntil;
        // A caller's alarm for the board to spread; a friend's alarm for this
        // group; the duty code must re-issue its orders after a fight.
        public bool CallPending, AlertPending, Called, ResumePending;
        public int Alive;
        // Tallies for the status line and the offline check.
        public int Thinks, Contacts, Covers, Flanks, Spreads, Searches, Calls, WorldCalls;

        int _posts;
        uint _seed;

        internal GroundBrain(int count, Vector3 home, float radius, byte duty, int seed)
        {
            Count = Mathf.Max(0, count);
            Men = new GroundMan[Count];
            for (int i = 0; i < Count; i++) Men[i] = new GroundMan();
            Home = home; Centre = home; Radius = Mathf.Max(10f, radius);
            Leash = Radius + LeashExtra;
            Duty = duty;
            _seed = (uint)seed * 2654435761u + 12345u;
            if (_seed == 0) _seed = 1;
            Cover.Reset(home, Mathf.Clamp(Radius * 0.5f, 40f, 80f));
            for (int i = 0; i < Count; i++)
            {
                Men[i].Act = duty == GroundDuty.Roam ? GroundAct.Watch : GroundAct.Duty;
                Men[i].NextRound = 10f + Next01() * 30f;
            }
        }

        // ------------------------------------------------------------ helpers

        float Next01()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xFFFFFF) / 16777216f;
        }

        float Range(float a, float b) { return a + (b - a) * Next01(); }

        static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        static Vector3 FlatDir(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            float m = v.magnitude;
            return m > 0.01f ? v / m : fallback;
        }

        static Vector3 Turn(Vector3 v, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector3(v.x * c - v.z * s, 0f, v.x * s + v.z * c);
        }

        Vector3 Leashed(Vector3 p)
        {
            Vector3 v = p - Home;
            v.y = 0f;
            float d = v.magnitude;
            return d <= Leash ? p : Home + v * (Leash / d) + new Vector3(0f, p.y - Home.y, 0f);
        }

        void Send(GroundMan m, byte act, Vector3 dest, bool run, float now)
        {
            m.Act = act; m.Dest = dest; m.Run = run; m.Moving = true; m.Crouch = false;
            // A walk is 0.8 units a second at worst, a run about three times that.
            m.Deadline = now + (run ? 6f : 12f) + Flat(dest - m.Pos) / (run ? 2.5f : 0.8f);
            m.Order++;
        }

        void Stay(GroundMan m, byte act, bool crouch)
        {
            m.Act = act; m.Moving = false; m.Run = false; m.Crouch = crouch;
        }

        bool Arrived(GroundMan m, float now)
        {
            return Flat(m.Pos - m.Dest) <= Arrive || now >= m.Deadline;
        }

        // -------------------------------------------------------------- setup

        /// <summary>Roam posts, then the cover cache, spending at most budget
        /// world calls; returns what it spent. Ready once both are done.</summary>
        internal int Setup(IGroundWorld w, int budget)
        {
            int spent = 0;
            if (Duty == GroundDuty.Roam)
                while (_posts < Count && spent < budget)
                {
                    GroundMan m = Men[_posts];
                    spent++;
                    Vector3 at;
                    m.Post = w.Stand(PostPoint(_posts), 8f, out at) ? at : Home;
                    m.HasPost = true;
                    _posts++;
                }
            if (spent < budget && !Cover.Done) spent += Cover.Build(w, budget - spent);
            WorldCalls += spent;
            Ready = Cover.Done && (Duty != GroundDuty.Roam || _posts >= Count);
            return spent;
        }

        /// <summary>A sunflower inside the radius: every man his own post,
        /// Spacing apart where the radius allows it, never stacked.</summary>
        Vector3 PostPoint(int k)
        {
            if (Count <= 1) return Home;
            float c = Mathf.Min(Spacing * 0.65f, 0.6f * Radius / Mathf.Sqrt(Count - 0.5f));
            float r = c * Mathf.Sqrt(k + 0.5f), a = k * 2.39996f + 0.7f;
            return Home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }

        /// <summary>A friend called: the next think reacts to the threat.</summary>
        internal void Alert(Vector3 threat, float now)
        {
            if (Phase == GroundPhase.Contact) return;
            Threat = threat; LastContact = now; AlertPending = true;
            // The board wakes it on its next scan (and counts it awake).
            ActiveUntil = now + CalledAwake; NextNear = now; NextThink = now;
        }

        // -------------------------------------------------------------- think

        internal void Think(IGroundWorld w, float now)
        {
            Thinks++;
            NextThink = now + ThinkEvery;
            int alive = 0;
            Vector3 sum = Vector3.zero;
            bool seen = false, hurt = false;
            float nearest = float.MaxValue;
            Vector3 seenAt = Threat, hurtAt = Threat;
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive) { if (m.Cover >= 0) { Cover.Release(i); m.Cover = -1; } continue; }
                alive++;
                sum += m.Pos;
                if (!m.HasThreat) continue;
                float d = Flat(m.Threat - m.Pos);
                if (m.Sees && d < nearest) { nearest = d; seenAt = m.Threat; seen = true; }
                if (m.Hurt && !hurt) { hurt = true; hurtAt = m.Threat; }
            }
            Alive = alive;
            if (alive == 0) return;
            Centre = sum / alive;
            if (seen) { Threat = seenAt; LastContact = now; }
            else if (hurt) { Threat = hurtAt; LastContact = now; seen = true; }

            if (Phase != GroundPhase.Contact && (seen || AlertPending))
            {
                bool called = AlertPending && !seen;
                AlertPending = false;
                EnterContact(w, now, called);
                return;
            }
            AlertPending = false;
            if (Phase == GroundPhase.Contact)
            {
                if (now - LastContact > ContactMemory) EnterSearch(w, now);
                else HoldContact(now, seen);
                return;
            }
            if (Phase == GroundPhase.Search)
            {
                if (now >= SearchUntil) EnterCalm(now);
                else SearchStep(w, now);
                return;
            }
            CalmStep(w, now);
        }

        // --------------------------------------------------------------- calm

        void CalmStep(IGroundWorld w, float now)
        {
            if (Duty != GroundDuty.Roam) return;
            int out_ = 0;
            for (int i = 0; i < Count; i++) if (Men[i].Alive && Men[i].Act == GroundAct.Round) out_++;
            int maxOut = Mathf.Max(1, Alive / 3);
            // One round point - one NavMesh path at most - per think.
            bool pathed = false;
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive || !m.HasPost) continue;
                if (m.Moving)
                {
                    if (!Arrived(m, now)) continue;
                    m.Moving = false;
                    m.Until = now + Range(3f, 8f);
                    m.NextLook = now;
                }
                if (m.Act == GroundAct.Round)
                {
                    if (now < m.Until) { LookAround(m, now); continue; }
                    if (m.Legs > 1 && pathed) continue;
                    m.Legs--;
                    Vector3 next;
                    if (m.Legs > 0) pathed = true;
                    if (m.Legs > 0 && RoundPoint(w, m, out next)) { Send(m, GroundAct.Round, next, false, now); continue; }
                    Send(m, GroundAct.Post, m.Post, false, now);
                    m.NextRound = now + Range(30f, 75f);
                    out_--;
                    continue;
                }
                if (Flat(m.Pos - m.Post) > Arrive + 1f) { Send(m, GroundAct.Post, m.Post, false, now); continue; }
                m.Act = GroundAct.Post; m.Crouch = false;
                LookAround(m, now);
                if (now < m.NextRound || now < m.Until || pathed) continue;
                Vector3 dest;
                if (out_ < maxOut) pathed = true;
                if (out_ < maxOut && RoundPoint(w, m, out dest))
                {
                    m.Legs = Next01() < 0.5f ? 2 : 3;
                    Send(m, GroundAct.Round, dest, false, now);
                    out_++;
                }
                else m.NextRound = now + Range(5f, 12f);
            }
        }

        /// <summary>At a post or a round stop: a new way to look every few
        /// seconds, mostly outward from the home.</summary>
        void LookAround(GroundMan m, float now)
        {
            if (now < m.NextLook) return;
            m.NextLook = now + Range(3f, 8f);
            Vector3 outward = FlatDir(m.Pos - Home, FlatDir(m.Look, new Vector3(0f, 0f, 1f)));
            m.Look = Turn(outward, Range(-80f, 80f));
        }

        /// <summary>A round stop: inside the radius, away from where he is,
        /// on walkable ground with a walk to it that stays inside. Up to three
        /// ground samples, but one path: a refused one waits for a later think.</summary>
        bool RoundPoint(IGroundWorld w, GroundMan m, out Vector3 dest)
        {
            for (int k = 0; k < 3; k++)
            {
                float a = Next01() * Mathf.PI * 2f, r = Radius * Mathf.Sqrt(Range(0.09f, 0.8f));
                Vector3 p = Home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                WorldCalls++;
                if (!w.Stand(p, 6f, out dest) || Flat(dest - Home) > Radius || Flat(dest - m.Pos) < 15f) continue;
                WorldCalls++;
                if (w.Walk(m.Pos, dest, Home, Radius)) return true;
                break;
            }
            dest = m.Pos;
            return false;
        }

        // ------------------------------------------------------------ contact

        void EnterContact(IGroundWorld w, float now, bool called)
        {
            Phase = GroundPhase.Contact; PhaseSince = now; Contacts++;
            if (!called && !Called) CallPending = true;
            Called = called;
            Vector3 d = FlatDir(Threat - Centre, new Vector3(0f, 0f, 1f));
            Vector3 perp = new Vector3(-d.z, 0f, d.x);
            float dist = Flat(Threat - Centre);

            // Flankers: the men furthest out to either side, alternating. A
            // called group sends half its men toward the fight instead.
            int want = called ? (Alive >= 2 ? Mathf.Max(1, Alive / 2) : 0)
                : (Alive >= 3 ? Mathf.Max(1, Alive / 4) : 0);
            for (int i = 0; i < Count; i++) Men[i].Flanker = false;
            for (int k = 0; k < want; k++)
            {
                float side = (k & 1) == 0 ? 1f : -1f, best = -float.MaxValue;
                int pick = -1;
                for (int i = 0; i < Count; i++)
                {
                    GroundMan m = Men[i];
                    if (!m.Alive || m.Fixed || m.Flanker) continue;
                    float lateral = Vector3.Dot(m.Pos - Centre, perp) * side;
                    if (lateral > best) { best = lateral; pick = i; }
                }
                if (pick < 0) break;
                Men[pick].Flanker = true;
                Vector3 goal = called
                    ? Threat - d * Mathf.Clamp(dist * 0.4f, 30f, 60f) + perp * side * 15f
                    : Threat - d * Mathf.Clamp(dist * 0.5f, 25f, 90f) + perp * side * Mathf.Clamp(dist * 0.7f, 30f, 110f);
                Vector3 dest;
                WorldCalls += 2;
                if (w.Stand(Leashed(goal), 8f, out dest) && w.Walk(Men[pick].Pos, dest, Home, Leash + 10f))
                {
                    Cover.Release(pick); Men[pick].Cover = -1;
                    Send(Men[pick], GroundAct.Flank, dest, true, now);
                    Flanks++;
                }
                else Men[pick].Flanker = false;
            }

            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive || m.Flanker) continue;
                m.Look = Threat - m.Pos;
                if (m.Fixed) { Stay(m, GroundAct.Watch, false); continue; }
                if (TakeCover(m, i, now)) continue;
                // No cover: step apart from the group's centre line, a little
                // back, and get down.
                float side = Vector3.Dot(m.Pos - Centre, perp) >= 0f ? 1f : -1f;
                if ((i & 1) == 1 && Mathf.Abs(Vector3.Dot(m.Pos - Centre, perp)) < 2f) side = -side;
                Vector3 goal = m.Pos + perp * side * Range(10f, 16f) - d * 6f, dest;
                WorldCalls++;
                if (w.Stand(Leashed(goal), 5f, out dest)) { Send(m, GroundAct.Spread, dest, true, now); Spreads++; }
                else Stay(m, GroundAct.Spread, true);
            }
        }

        bool TakeCover(GroundMan m, int i, float now)
        {
            if (m.Cover >= 0) Cover.Release(i);
            m.Cover = Cover.Best(m.Pos, Threat, CoverMove, CoverMinThreat, i);
            if (m.Cover < 0) return false;
            Cover.Claim(m.Cover, i);
            Send(m, GroundAct.Cover, Cover.Spots[m.Cover].Pos, true, now);
            Covers++;
            return true;
        }

        /// <summary>In the fight: men who got there get down facing the
        /// threat; one man whose cover no longer faces it looks for another.</summary>
        void HoldContact(float now, bool seen)
        {
            bool repicked = false;
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive) continue;
                m.Look = Threat - m.Pos;
                if (m.Moving)
                {
                    if (!Arrived(m, now)) continue;
                    m.Moving = false; m.Run = false;
                    m.Crouch = m.Act == GroundAct.Cover || m.Act == GroundAct.Flank || m.Act == GroundAct.Spread;
                    continue;
                }
                if (!seen || repicked || m.Act != GroundAct.Cover || m.Cover < 0) continue;
                if (Cover.Facing(m.Cover, Threat) >= 0.3f) continue;
                repicked = true;
                if (!TakeCover(m, i, now)) { Cover.Release(i); m.Cover = -1; Stay(m, GroundAct.Spread, true); }
            }
        }

        // ------------------------------------------------------------- search

        void EnterSearch(IGroundWorld w, float now)
        {
            Phase = GroundPhase.Search; PhaseSince = now; SearchUntil = now + SearchTime; Searches++;
            int searchers = 0;
            // Flankers search first; then the men nearest the last sighting.
            for (int pass = 0; pass < 2 && searchers < 2; pass++)
                for (int k = 0; k < Count && searchers < 2; k++)
                {
                    int pick = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < Count; i++)
                    {
                        GroundMan m = Men[i];
                        if (!m.Alive || m.Fixed || m.Act == GroundAct.Search) continue;
                        if (pass == 0 && !m.Flanker) continue;
                        float d = Flat(m.Pos - Threat);
                        if (d < best) { best = d; pick = i; }
                    }
                    if (pick < 0) break;
                    GroundMan s = Men[pick];
                    Vector3 dest;
                    s.Act = GroundAct.Search;
                    s.Legs = 2;
                    if (SearchPoint(w, s, out dest)) Send(s, GroundAct.Search, dest, false, now);
                    else Stay(s, GroundAct.Search, false);
                    searchers++;
                }
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive || m.Act == GroundAct.Search) continue;
                Stay(m, GroundAct.Watch, false);
                m.Look = Threat - m.Pos;
            }
        }

        bool SearchPoint(IGroundWorld w, GroundMan m, out Vector3 dest)
        {
            for (int k = 0; k < 2; k++)
            {
                float a = Next01() * Mathf.PI * 2f, r = Range(10f, 30f);
                Vector3 p = Leashed(Threat + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                WorldCalls += 2;
                if (w.Stand(p, 6f, out dest) && w.Walk(m.Pos, dest, Home, Leash + 10f)) return true;
            }
            dest = m.Pos;
            return false;
        }

        void SearchStep(IGroundWorld w, float now)
        {
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (!m.Alive || m.Act != GroundAct.Search) continue;
                if (m.Moving)
                {
                    if (!Arrived(m, now)) continue;
                    m.Moving = false;
                    m.Until = now + Range(4f, 7f);
                    m.NextLook = now;
                }
                if (now < m.Until) { LookAround(m, now); continue; }
                m.Legs--;
                Vector3 dest;
                if (m.Legs > 0 && SearchPoint(w, m, out dest)) Send(m, GroundAct.Search, dest, false, now);
                else { Stay(m, GroundAct.Watch, false); m.Look = Threat - m.Pos; }
            }
        }

        // --------------------------------------------------------------- back

        void EnterCalm(float now)
        {
            Phase = GroundPhase.Calm; PhaseSince = now; Called = false;
            for (int i = 0; i < Count; i++)
            {
                GroundMan m = Men[i];
                if (m.Cover >= 0) { Cover.Release(i); m.Cover = -1; }
                m.Flanker = false; m.Crouch = false; m.Run = false;
                if (!m.Alive) continue;
                if (Duty == GroundDuty.Roam && m.HasPost)
                {
                    Send(m, GroundAct.Post, m.Post, false, now);
                    m.NextRound = now + Range(20f, 60f);
                }
                else { m.Act = Duty == GroundDuty.Roam ? GroundAct.Watch : GroundAct.Duty; m.Moving = false; }
            }
            ResumePending = Duty != GroundDuty.Roam;
        }
    }

    /// <summary>All brains and the frame budget: proximity tests, thinks and
    /// world calls per frame are capped by constants, not by group count.</summary>
    internal sealed class GroundBoard
    {
        internal const int ScanPerFrame = 8, ThinksPerFrame = 2, WorldBudget = 4;

        public readonly List<GroundBrain> Brains = new List<GroundBrain>(64);
        // What the last frame did (the F8 line and the offline check).
        public int FrameScans, FrameNear, FrameThinks, FrameCalls, AwakeCount;
        int _cursor;

        internal void Add(GroundBrain b) { if (b != null && !Brains.Contains(b)) Brains.Add(b); }

        internal void Remove(GroundBrain b)
        {
            if (b != null && Brains.Remove(b) && b.Active) AwakeCount--;
        }

        /// <summary>One man shouts: every group of the same side within
        /// CallRange reacts to the threat (a called group does not call on).
        /// Runs once per contact, so the walk over the brains is rare.</summary>
        internal void Call(GroundBrain from, float now)
        {
            from.CallPending = false;
            from.Calls++;
            for (int i = 0; i < Brains.Count; i++)
            {
                GroundBrain o = Brains[i];
                if (o == from || o.Side != from.Side) continue;
                Vector3 d = o.Centre - from.Centre;
                d.y = 0f;
                if (d.magnitude <= GroundBrain.CallRange) o.Alert(from.Threat, now);
            }
        }

        internal void Frame(IGroundWorld w, float now)
        {
            FrameScans = FrameNear = FrameThinks = FrameCalls = 0;
            int n = Brains.Count;
            if (n == 0) { AwakeCount = 0; return; }
            if (_cursor >= n) _cursor = 0;
            int scan = n < ScanPerFrame ? n : ScanPerFrame;
            for (int k = 0; k < scan; k++)
            {
                GroundBrain b = Brains[_cursor];
                _cursor = _cursor + 1 >= n ? 0 : _cursor + 1;
                FrameScans++;
                if (now >= b.NextNear)
                {
                    b.NextNear = now + GroundBrain.NearEvery;
                    FrameNear++;
                    bool was = b.Active;
                    b.Active = w.PlayerNear(b.Centre, GroundBrain.WakeRange) || now < b.ActiveUntil;
                    if (b.Active && !was) b.NextThink = now;
                    if (b.Active != was) AwakeCount += b.Active ? 1 : -1;
                }
                if (!b.Active) continue;
                if (!b.Ready && FrameCalls < WorldBudget) FrameCalls += b.Setup(w, WorldBudget - FrameCalls);
                if (FrameThinks < ThinksPerFrame && now >= b.NextThink)
                {
                    b.Think(w, now);
                    FrameThinks++;
                    if (b.CallPending) Call(b, now);
                }
            }
        }
    }
}

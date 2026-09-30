// W merc core fix - the roster link to the master server as a pure state
// machine (docs/ai/tasks/w-merc-core-fix.md). It knows nothing of Unity or the
// game, so research/merc_link_check.py compiles it UNCHANGED. Mercs
// (Revival.Mercs.cs) feeds it the owner's arrival, every roster answer and
// every failed roster request, asks it when to send the next "get", whether
// a stored roster may spawn yet, and what the UI should say.
//
//   ASK       2 s after the character settled. A request that gets no answer
//             (4 s x 3 on the wire, or the connection is not up) is retried
//             after 3, 6, 12, 24, then every 30 s - never given up. Live, the
//             roster is re-read every 60 s, so a grant made elsewhere (web
//             admin, another session) spawns without a relog. A server
//             without the roster module is probed again every 60 s: one
//             mixed-up plain answer does not switch mercs off for the session.
//   SPAWN     a roster answered live in this game run spawns at once (the
//             next answer corrects it). Before that the last known roster
//             (kept in memory, or the owner's local copy of the server's last
//             answer) spawns after Fallback seconds without an answer, or at
//             once on the admin's "bring my mercs". The first live answer
//             wins: rows it no longer lists are taken away again.
//   PHASE     Live / Asking / Silent (retrying, with the retry time) /
//             NoModule, for the trader tab, the L list and the F8 status.
//
// No allocation. Times are Time.time seconds. C# 3.0, ASCII only.
namespace NextDayRevival
{
    internal sealed class MercLink
    {
        internal const float FirstAsk = 2f;
        internal const float Fallback = 8f;
        internal const float Refresh = 60f;
        internal const float NoModuleProbe = 60f;
        internal const float MaxBackoff = 30f;
        internal const float Guard = 40f;      // a "get" always finishes well before this

        internal const int PhaseAsking = 0, PhaseLive = 1, PhaseNoModule = 2, PhaseSilent = 3;

        /// <summary>-1 unknown (asking), 0 the master server has no roster
        /// module, 1 it keeps the roster.</summary>
        internal int Support = -1;
        /// <summary>A live roster answer came in during this game run.</summary>
        internal bool Answered;
        /// <summary>A last known roster is held (live earlier or the local copy).</summary>
        internal bool Known;
        /// <summary>Roster requests in a row that got no answer.</summary>
        internal int Fails;
        internal float NextAsk = float.MaxValue;
        internal float LastAnswer = -1f;
        /// <summary>Since when this owner waits for his first answer (-1 none).</summary>
        internal float WaitingSince = -1f;
        /// <summary>The admin asked to spawn the last known roster now.</summary>
        internal bool Forced;
        bool _asking;

        /// <summary>The local character (re)spawned: ask soon.</summary>
        internal void OwnerArrived(float now)
        {
            if (!Answered) WaitingSince = now;
            _asking = false;
            if (NextAsk > now + FirstAsk) NextAsk = now + FirstAsk;
        }

        /// <summary>The character is gone (menu, death screen): no asking.</summary>
        internal void OwnerLeft()
        {
            _asking = false;
            NextAsk = float.MaxValue;
            if (!Answered) WaitingSince = -1f;
        }

        /// <summary>Time to send a roster request? True at most once until
        /// the result is reported (or Guard seconds, should it never be).</summary>
        internal bool Due(float now)
        {
            if (now < NextAsk) return false;
            _asking = true;
            NextAsk = now + Guard;
            return true;
        }

        /// <summary>Ask as soon as possible (hire click, admin, settle).</summary>
        internal void AskNow(float now)
        {
            if (!_asking && NextAsk > now) NextAsk = now;
        }

        /// <summary>A roster answer (from any op) arrived.</summary>
        internal void OnAnswer(float now)
        {
            Support = 1;
            Answered = Known = true;
            Fails = 0;
            _asking = false;
            LastAnswer = now;
            WaitingSince = -1f;
            Forced = false;
            NextAsk = now + Refresh;
        }

        /// <summary>The server answered with plain storage: no roster module.</summary>
        internal void OnNoModule(float now)
        {
            Support = 0;
            Fails = 0;
            _asking = false;
            NextAsk = now + NoModuleProbe;
        }

        /// <summary>A roster request ended without an answer (timeout,
        /// offline, send error).</summary>
        internal void OnFailed(float now)
        {
            _asking = false;
            Fails++;
            NextAsk = now + Backoff(Fails);
        }

        /// <summary>The local copy of the last server answer was read.</summary>
        internal void OnCacheLoaded() { Known = true; }

        internal static float Backoff(int fails)
        {
            float s = 3f;
            for (int i = 1; i < fails && s < MaxBackoff; i++) s *= 2f;
            return s < MaxBackoff ? s : MaxBackoff;
        }

        /// <summary>May a (non-session) roster row spawn now?</summary>
        internal bool SpawnAllowed(float now)
        {
            if (Answered || Support == 0) return true;
            if (!Known) return false;
            if (Forced) return true;
            return WaitingSince >= 0f && now - WaitingSince >= Fallback;
        }

        /// <summary>Spawning from the last known roster, no live answer yet.</summary>
        internal bool OnFallback(float now) { return !Answered && Support != 0 && Known && SpawnAllowed(now); }

        internal int Phase
        {
            get
            {
                if (Support == 1) return PhaseLive;
                if (Support == 0) return PhaseNoModule;
                return Fails > 0 ? PhaseSilent : PhaseAsking;
            }
        }

        /// <summary>Whole seconds to the next retry (0 = asking right now).</summary>
        internal int RetryIn(float now)
        {
            if (_asking || NextAsk == float.MaxValue) return 0;
            float s = NextAsk - now;
            return s <= 0f ? 0 : (int)(s + 0.999f);
        }
    }
}

// W merc notifications - the rules as a pure board (docs/ai/tasks/w-merc-notify.md).
// It knows nothing of Unity or the game, so research/merc_notify_check.py
// compiles it UNCHANGED. MercNotify (Revival.MercNotify.cs) posts what a merc
// just went through and takes back the toasts that are due.
//
//   KINDS     contact, under fire, wounded, target down, fallen; at a vehicle
//             gun: target acquired, kill.
//   FILTER    all / important (contact, wounded, target down, fallen, gun
//             kill) / deaths only. A death always passes.
//   ANTI-SPAM one kind per merc at most every Gap seconds (a death once). The
//             first post of a kind opens a bucket; mercs that post the same
//             kind before it is due join it ("3 mercs in contact"). A bucket is
//             due Window seconds after it opened (a death 0.5 s, the rest 1 s),
//             never sooner than Spacing seconds after the last toast of that
//             kind - so a team that meets a group one by one reads as one or
//             two lines - and never sooner than AnyGap after any toast (at most
//             30 a minute in the worst fight). Due buckets go by Priority
//             (deaths first); one left waiting past MaxAge is dropped, a late
//             "under fire" would only mislead. A death is never dropped.
//   SPOT      a bucket keeps the nearest spot (to the owner) of its posts:
//             the toast's direction and distance, and the map ping.
//
// No allocation after construction. Times are Time.time seconds. C# 3.0,
// ASCII only.
using System;

namespace NextDayRevival
{
    internal static class MercNote
    {
        internal const int Contact = 0, UnderFire = 1, Wounded = 2, TargetDown = 3, Fallen = 4,
            GunTarget = 5, GunKill = 6, Kinds = 7;
        internal const int FilterAll = 0, FilterImportant = 1, FilterDeaths = 2;

        // Per merc and kind: the shortest time between two of his posts.
        static readonly float[] GapS = { 12f, 8f, 6f, 4f, 0f, 8f, 4f };
        // Per kind, all mercs: the shortest time between two toasts.
        static readonly float[] SpacingS = { 6f, 10f, 4f, 5f, 0f, 8f, 5f };
        // Which due toast goes first: death, contact, wounded, gun kill,
        // target down, under fire, gun target.
        internal static readonly int[] Priority = { Fallen, Contact, Wounded, GunKill, TargetDown, UnderFire, GunTarget };
        internal const float AnyGap = 2f;       // between any two toasts
        internal const float MaxAge = 8f;       // a toast that waited longer is dropped (not a death)

        internal static float Gap(int kind) { return kind >= 0 && kind < Kinds ? GapS[kind] : 5f; }
        internal static float Spacing(int kind) { return kind >= 0 && kind < Kinds ? SpacingS[kind] : 5f; }
        internal static float Window(int kind) { return kind == Fallen ? 0.5f : 1.0f; }

        internal static bool Important(int kind)
        {
            return kind == Contact || kind == Wounded || kind == TargetDown || kind == Fallen || kind == GunKill;
        }

        internal static bool Passes(int kind, int filter)
        {
            if (kind == Fallen) return true;
            if (filter == FilterDeaths) return false;
            if (filter == FilterImportant) return Important(kind);
            return true;
        }

        /// <summary>A warning (red bar) rather than news (gold bar).</summary>
        internal static bool Warn(int kind) { return kind == UnderFire || kind == Wounded || kind == Fallen; }

        /// <summary>"all", "important", "deaths" (or 0/1/2), any case;
        /// anything else is important.</summary>
        internal static int ParseFilter(string s)
        {
            if (s == null) return FilterImportant;
            s = s.Trim();
            if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase) || s == "0") return FilterAll;
            if (string.Equals(s, "deaths", StringComparison.OrdinalIgnoreCase) || s == "2"
                || string.Equals(s, "deaths only", StringComparison.OrdinalIgnoreCase)) return FilterDeaths;
            return FilterImportant;
        }

        internal static string FilterName(int filter)
        {
            return filter == FilterAll ? "all" : filter == FilterDeaths ? "deaths" : "important";
        }

        /// <summary>0 N, 1 NE .. 7 NW for a flat offset (x east, z north, the
        /// game's heading sense); -1 when there is no offset.</summary>
        internal static int Compass8(float dx, float dz)
        {
            if (dx * dx + dz * dz < 1e-6f) return -1;
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (deg < 0.0) deg += 360.0;
            return (int)Math.Floor(deg / 45.0 + 0.5) % 8;
        }
    }

    /// <summary>One toast that is due: the kind, how many mercs, the first
    /// three of them, and the nearest spot.</summary>
    internal struct MercNoteOut
    {
        internal int Kind, Count, Id0, Id1, Id2, Dir;
        internal float X, Y, Z, Dist, Value;
        internal bool Severe;
    }

    internal sealed class MercNoteBoard
    {
        internal const int MaxMercs = 16;
        const float Never = -100000f;

        readonly int[] _ids = new int[MaxMercs];
        readonly bool[] _has = new bool[MaxMercs];
        readonly float[] _used = new float[MaxMercs];
        readonly float[] _last = new float[MercNote.Kinds * MaxMercs];

        readonly bool[] _open = new bool[MercNote.Kinds];
        readonly float[] _openAt = new float[MercNote.Kinds];
        readonly float[] _emitAt = new float[MercNote.Kinds];
        readonly MercNoteOut[] _bucket = new MercNoteOut[MercNote.Kinds];
        int _pending;
        float _anyAt = Never;

        internal int Posted, Filtered, Throttled, Merged, Emitted, Stale;

        internal MercNoteBoard() { Clear(); }

        internal void Clear()
        {
            for (int i = 0; i < MaxMercs; i++) { _has[i] = false; _ids[i] = 0; _used[i] = Never; }
            for (int i = 0; i < _last.Length; i++) _last[i] = Never;
            for (int k = 0; k < MercNote.Kinds; k++) { _open[k] = false; _emitAt[k] = Never; }
            _pending = 0;
            _anyAt = Never;
        }

        internal bool Pending { get { return _pending > 0; } }

        /// <summary>One event of one merc. dist is metres from the owner,
        /// dir Compass8 (or -1), value a kind's number (health left for
        /// wounded). False when the filter or the anti-spam drops it.</summary>
        internal bool Post(int id, int kind, int filter, float now, float x, float y, float z,
                           float dist, int dir, float value, bool severe)
        {
            if (kind < 0 || kind >= MercNote.Kinds) return false;
            if (!MercNote.Passes(kind, filter)) { Filtered++; return false; }
            int s = SlotOf(id, now);
            int k = kind * MaxMercs + s;
            float gap = MercNote.Gap(kind);
            if (kind == MercNote.Fallen ? _last[k] > Never : now - _last[k] < gap) { Throttled++; return false; }
            _last[k] = now;
            _used[s] = now;
            Posted++;
            if (!_open[kind])
            {
                _open[kind] = true;
                _openAt[kind] = now;
                _pending++;
                MercNoteOut o = new MercNoteOut();
                o.Kind = kind; o.Count = 1; o.Id0 = id; o.Id1 = 0; o.Id2 = 0;
                o.X = x; o.Y = y; o.Z = z; o.Dist = dist; o.Dir = dir; o.Value = value; o.Severe = severe;
                _bucket[kind] = o;
                return true;
            }
            Merged++;
            MercNoteOut b = _bucket[kind];
            b.Count++;
            if (b.Count == 2) b.Id1 = id;
            else if (b.Count == 3) b.Id2 = id;
            if (dist >= 0f && (b.Dist < 0f || dist < b.Dist))
            {
                b.X = x; b.Y = y; b.Z = z; b.Dist = dist; b.Dir = dir;
            }
            if (value < b.Value) b.Value = value;
            b.Severe |= severe;
            _bucket[kind] = b;
            return true;
        }

        /// <summary>When the open bucket of a kind is due (its own window and
        /// spacing; AnyGap is applied in Next).</summary>
        internal float DueAt(int kind)
        {
            float a = _openAt[kind] + MercNote.Window(kind), b = _emitAt[kind] + MercNote.Spacing(kind);
            return a > b ? a : b;
        }

        /// <summary>The next toast that is due, by Priority.</summary>
        internal bool Next(float now, out MercNoteOut o)
        {
            o = new MercNoteOut();
            if (_pending == 0) return false;
            bool gapOpen = now >= _anyAt + MercNote.AnyGap;
            for (int n = 0; n < MercNote.Kinds; n++)
            {
                int kind = MercNote.Priority[n];
                if (!_open[kind]) continue;
                if (kind != MercNote.Fallen && now - _openAt[kind] > MercNote.MaxAge)
                {
                    _open[kind] = false;
                    _pending--;
                    Stale++;
                    continue;
                }
                if (now < DueAt(kind) || (!gapOpen && kind != MercNote.Fallen)) continue;
                o = _bucket[kind];
                _open[kind] = false;
                _emitAt[kind] = now;
                _anyAt = now;
                _pending--;
                Emitted++;
                return true;
            }
            return false;
        }

        /// <summary>A merc left the roster: his slot is free again. An open
        /// bucket keeps his post.</summary>
        internal void Forget(int id)
        {
            for (int i = 0; i < MaxMercs; i++)
            {
                if (!_has[i] || _ids[i] != id) continue;
                _has[i] = false;
                _used[i] = Never;
                for (int k = 0; k < MercNote.Kinds; k++) _last[k * MaxMercs + i] = Never;
            }
        }

        int SlotOf(int id, float now)
        {
            int free = -1, old = 0;
            for (int i = 0; i < MaxMercs; i++)
            {
                if (_has[i] && _ids[i] == id) return i;
                if (!_has[i]) { if (free < 0) free = i; }
                else if (_used[i] < _used[old] || !_has[old]) old = i;
            }
            int s = free >= 0 ? free : old;
            _has[s] = true;
            _ids[s] = id;
            _used[s] = now;
            for (int k = 0; k < MercNote.Kinds; k++) _last[k * MaxMercs + s] = Never;
            return s;
        }
    }
}

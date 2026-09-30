// Bounded combat retention policy; independent of Unity for the hour simulation.
using System;

namespace NextDayRevival
{
    internal static class CombatLoadPolicy
    {
        internal const int CorpseCap = 64, WreckCap = 12, DebrisCap = 32;
        internal const int ParaCap = 64, SoundCap = 12, BlastCap = 8;
        internal const int EventNpcCap = 96;
        internal const float CorpseSeconds = 180f, WreckSeconds = 300f;
        internal const float RestSeconds = 6f, ForceRestSeconds = 15f;

        // A legacy setting of 4/16 still gets at most two steps per
        // frame. At a severe hitch one step prevents a second expensive solve.
        internal static int Steps(int configured, float frameSeconds)
        {
            if (configured <= 0) return 0;
            if (frameSeconds >= 0.04f) return 1;
            return Math.Min(configured, 2);
        }

        internal static bool Rest(float age, bool quiet, int quietSamples)
        {
            return age >= ForceRestSeconds || (age >= RestSeconds && quiet && quietSamples >= 2);
        }

        internal static bool CanDrop(int present, int men)
        {
            return men > 0 && men <= 12 && present >= 0 && present <= ParaCap - men;
        }

        internal static bool CanSpawnEvent(int present, int men)
        {
            return men > 0 && present >= 0 && present <= EventNpcCap - men;
        }
    }

    // Fixed storage, oldest-first eviction and absolute expiry. The adapter
    // disposes the returned victim before retaining the new object. No timers
    // can be renewed forever by a player standing beside a corpse or wreck.
    internal sealed class CombatLeaseBook
    {
        readonly int[] _keys;
        readonly float[] _born, _until;
        internal int Count;
        internal int Capacity { get { return _keys.Length; } }

        internal CombatLeaseBook(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _keys = new int[capacity]; _born = new float[capacity]; _until = new float[capacity];
        }

        internal int Put(int key, float now, float seconds, out int evicted)
        {
            if (key == 0) throw new ArgumentOutOfRangeException("key");
            int slot = SlotFor(key);
            if (_keys[slot] == key) { evicted = 0; return slot; }
            evicted = _keys[slot];
            if (evicted == 0) Count++;
            _keys[slot] = key; _born[slot] = now; _until[slot] = now + seconds;
            return slot;
        }

        internal int SlotFor(int key)
        {
            int empty = -1, oldest = 0;
            for (int i = 0; i < Capacity; i++)
            {
                // Repeated kill notifications must not extend a lease.
                if (_keys[i] == key) return i;
                if (_keys[i] == 0 && empty < 0) empty = i;
                if (_born[i] < _born[oldest]) oldest = i;
            }
            return empty >= 0 ? empty : oldest;
        }

        internal bool Due(int slot, float now) { return _keys[slot] != 0 && now >= _until[slot]; }
        internal int Key(int slot) { return _keys[slot]; }
        internal void Drop(int slot)
        {
            if (_keys[slot] != 0) Count--;
            _keys[slot] = 0;
        }
    }
}

// H S1: bounded, Unity-free trader spawn policy. Called only for pending hires.
using System;

namespace NextDayRevival
{
    internal struct MercSpawnPoint
    {
        internal float X, Y, Z;
        internal MercSpawnPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    internal interface IMercSpawnWorld
    {
        bool Ground(MercSpawnPoint near, out MercSpawnPoint feet);
        bool Clear(MercSpawnPoint feet);
        bool Outside(MercSpawnPoint feet);
        bool Route(MercSpawnPoint from, MercSpawnPoint to);
    }

    internal enum MercSpawnSearchResult { Pending, Found, Failed }

    internal struct MercTraderSpawnSearch
    {
        // Seven rays, all strictly on the buyer side; never a full trader ring.
        static readonly float[] Forward = { 1f, 0.8660254f, 0.8660254f, 0.5f, 0.5f, 0.3420201f, 0.3420201f };
        static readonly float[] Side = { 0f, 0.5f, -0.5f, 0.8660254f, -0.8660254f, 0.9396926f, -0.9396926f };
        static readonly float[] ExitRadii = { 6f, 10f, 16f, 24f, 36f, 48f };
        static readonly float[] SpawnRadii = { 2.5f, 4f, 6f, 9f };
        internal const int ProbeBudget = 8, PathBudget = 2;
        internal bool Started;
        MercSpawnPoint _trader, _start, _exit;
        float _fx, _fz;
        int _shift, _stage, _sample;

        internal bool Begin(IMercSpawnWorld world, MercSpawnPoint trader, MercSpawnPoint buyer, int occupied)
        {
            float fx = buyer.X - trader.X, fz = buyer.Z - trader.Z;
            float length = (float)Math.Sqrt(fx * fx + fz * fz);
            // No buyer side can be proved at coincident coordinates.
            if (length < 0.25f) return false;
            if (!world.Ground(buyer, out _start) || !Near(buyer, _start)) return false;
            _trader = trader; _fx = fx / length; _fz = fz / length;
            _shift = Math.Abs(occupied % Forward.Length);
            _stage = 0; _sample = 0; Started = true;
            return true;
        }

        // Search resumes in the record on the next spawn tick. Even a sealed
        // room cannot issue dozens of synchronous path calculations in Update.
        internal MercSpawnSearchResult Step(IMercSpawnWorld world, out MercSpawnPoint result)
        {
            result = _trader;
            if (!Started) return MercSpawnSearchResult.Failed;
            int probes = 0, paths = 0;
            while (probes < ProbeBudget && paths < PathBudget)
            {
                MercSpawnPoint near;
                if (_stage == 2) near = _exit;
                else
                {
                    float[] radii = _stage == 0 ? ExitRadii : SpawnRadii;
                    if (_sample == radii.Length * Forward.Length)
                    {
                        if (_stage == 0) return Finish(MercSpawnSearchResult.Failed);
                        _stage = 2; continue;
                    }
                    int angle = (_sample % Forward.Length + _shift) % Forward.Length;
                    near = Sample(radii[_sample / Forward.Length], angle);
                    _sample++;
                }
                probes++;
                MercSpawnPoint feet;
                bool usable = world.Ground(near, out feet) && Accept(near, feet)
                    && world.Clear(feet) && (_stage == 1 || world.Outside(feet));
                if (usable)
                {
                    paths++;
                    usable = _stage == 1 ? world.Route(feet, _exit) : world.Route(_start, feet);
                }
                if (_stage == 2)
                {
                    // Recheck a fallback that may have become occupied while
                    // we searched. No unchecked trader/owner-feet fallback.
                    if (usable) result = feet;
                    return Finish(usable ? MercSpawnSearchResult.Found : MercSpawnSearchResult.Failed);
                }
                if (!usable) continue;
                if (_stage == 0) { _exit = feet; _stage = 1; _sample = 0; }
                else { result = feet; return Finish(MercSpawnSearchResult.Found); }
            }
            return MercSpawnSearchResult.Pending;
        }

        MercSpawnSearchResult Finish(MercSpawnSearchResult result)
        {
            Started = false; return result;
        }

        MercSpawnPoint Sample(float radius, int angle)
        {
            return new MercSpawnPoint(_trader.X + (_fx * Forward[angle] + _fz * Side[angle]) * radius,
                _trader.Y, _trader.Z + (_fz * Forward[angle] - _fx * Side[angle]) * radius);
        }

        static bool Near(MercSpawnPoint near, MercSpawnPoint feet)
        {
            float dx = feet.X - near.X, dz = feet.Z - near.Z;
            return dx * dx + dz * dz <= 0.4225f && Math.Abs(feet.Y - near.Y) <= 2.5f;
        }

        bool Accept(MercSpawnPoint near, MercSpawnPoint feet)
        {
            return Near(near, feet) && (feet.X - _trader.X) * _fx + (feet.Z - _trader.Z) * _fz >= 1.8f;
        }
    }
}

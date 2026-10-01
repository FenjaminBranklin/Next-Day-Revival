// Z M5b: bounded authority over real crates; no virtual inventory or free ammo.
using System;
using System.Collections.Generic;

namespace NextDayRevival
{
    internal interface IMercFetchWorld
    {
        int Validate(MercFetchJob job, bool loading);
        void Carry(MercFetchJob job);
        void Release(MercFetchJob job);
        bool AtDepot(MercFetchJob job);
        int UnloadOne(MercFetchJob job); // 1 moved, 0 empty, negative reason
        void Changed(MercFetchJob job);
    }

    internal sealed class MercFetchJob
    {
        internal const int Loading = 1, Carried = 2, Unloading = 3, Complete = 4, Failed = 5;
        internal const int Missing = 1, VehicleLost = 2, DriverLost = 3, Taken = 4,
            WrongSide = 5, DepotLost = 6, Full = 7, Expired = 8, Cancelled = 9,
            Unsupported = 10, Busy = 11, Blocked = 12;
        internal int Actor, Serial, Crate, Vehicle, Driver, Phase, Reason, Moved, Revision;
        internal float Ready, Until, NextItem;
        internal bool Active { get { return Phase >= Loading && Phase <= Unloading; } }
        internal bool Matches(int actor, int serial, int crate, int vehicle, int driver)
        { return Actor == actor && Serial == serial && Crate == crate && Vehicle == vehicle && Driver == driver; }
    }

    internal sealed class MercFetchHost
    {
        internal readonly MercFetchJob[] Jobs = new MercFetchJob[8];
        readonly IMercFetchWorld _world;
        readonly Dictionary<int, int> _seen = new Dictionary<int, int>(64);
        internal MercFetchHost(IMercFetchWorld world)
        { _world = world; for (int i = 0; i < Jobs.Length; i++) Jobs[i] = new MercFetchJob(); }

        internal MercFetchJob Find(int actor)
        { for (int i = 0; i < Jobs.Length; i++) if (Jobs[i].Actor == actor) return Jobs[i]; return null; }

        internal int Begin(int actor, int serial, int crate, int vehicle, int driver, float now)
        {
            if (actor <= 0 || serial <= 0 || crate <= 0 || vehicle <= 0 || driver <= 0) return MercFetchJob.Unsupported;
            MercFetchJob j = Find(actor);
            if (j != null && j.Active)
            {
                if (!j.Matches(actor, serial, crate, vehicle, driver)) return MercFetchJob.Busy;
                _world.Changed(j); return 0; // Retry never restarts a timer.
            }
            int seen;
            if (_seen.TryGetValue(actor, out seen) && serial <= seen) return MercFetchJob.Expired;
            if (!_seen.ContainsKey(actor) && _seen.Count >= 64) return MercFetchJob.Busy;
            for (int i = 0; i < Jobs.Length; i++)
                if (Jobs[i].Active && (Jobs[i].Crate == crate || Jobs[i].Vehicle == vehicle)) return MercFetchJob.Taken;
            if (j == null)
                for (int i = 0; i < Jobs.Length; i++) if (!Jobs[i].Active) { j = Jobs[i]; break; }
            if (j == null) return MercFetchJob.Busy;
            j.Actor = actor; j.Serial = serial; j.Crate = crate; j.Vehicle = vehicle; j.Driver = driver;
            j.Phase = MercFetchJob.Loading; j.Moved = j.Reason = 0;
            int reason = _world.Validate(j, true);
            if (reason != 0) { j.Phase = MercFetchJob.Failed; j.Reason = reason; return reason; }
            _seen[actor] = serial;
            j.Ready = now + 4f; j.Until = now + 12f; j.NextItem = j.Ready;
            j.Revision++; _world.Changed(j); return 0;
        }

        internal bool Touch(int actor, int serial, int crate, int vehicle, int driver, float now)
        {
            MercFetchJob j = Find(actor);
            if (j == null || !j.Active || !j.Matches(actor, serial, crate, vehicle, driver) || now >= j.Until) return false;
            j.Until = now + 12f; return true;
        }

        internal int Unload(int actor, int serial, int crate, int vehicle, int driver, float now)
        {
            MercFetchJob j = Find(actor);
            if (j == null || !j.Active || now >= j.Until || !j.Matches(actor, serial, crate, vehicle, driver)) return MercFetchJob.Expired;
            if (j.Phase == MercFetchJob.Unloading) { _world.Changed(j); return 0; }
            if (j.Phase != MercFetchJob.Carried || !_world.AtDepot(j)) return MercFetchJob.DepotLost;
            int reason = _world.Validate(j, false);
            if (reason != 0) return reason;
            j.Phase = MercFetchJob.Unloading; j.Ready = j.NextItem = now + 4f;
            j.Until = now + 12f; j.Revision++; _world.Changed(j); return 0;
        }

        internal void Cancel(int actor, int serial)
        { MercFetchJob j = Find(actor); if (j != null && j.Active && j.Serial == serial) Fail(j, MercFetchJob.Cancelled); }

        internal void Fail(MercFetchJob j, int reason)
        { j.Phase = MercFetchJob.Failed; j.Reason = reason; _world.Release(j); j.Revision++; _world.Changed(j); }

        internal void Step(float now)
        {
            // One native removal per scheduler turn, across all eight jobs.
            bool moved = false;
            for (int i = 0; i < Jobs.Length; i++)
            {
                MercFetchJob j = Jobs[i]; if (!j.Active) continue;
                int reason = now >= j.Until ? MercFetchJob.Expired : _world.Validate(j, j.Phase == MercFetchJob.Loading);
                if (reason != 0) { Fail(j, reason); continue; }
                if (j.Phase == MercFetchJob.Loading && now >= j.Ready)
                { _world.Carry(j); j.Phase = MercFetchJob.Carried; j.Revision++; _world.Changed(j); }
                else if (j.Phase == MercFetchJob.Unloading && now >= j.Ready)
                {
                    if (!_world.AtDepot(j)) { Fail(j, MercFetchJob.DepotLost); continue; }
                    if (moved || now < j.NextItem) continue;
                    moved = true; j.NextItem = now + 0.25f;
                    int result = _world.UnloadOne(j);
                    if (result < 0) { Fail(j, -result); continue; }
                    if (result > 0) j.Moved++;
                    else { j.Phase = MercFetchJob.Complete; _world.Release(j); }
                    j.Revision++; _world.Changed(j);
                }
            }
        }

        internal void NewMaster()
        {
            // The source remains a real native crate, so handoff never erases goods.
            for (int i = 0; i < Jobs.Length; i++) if (Jobs[i].Active) Fail(Jobs[i], MercFetchJob.Expired);
        }

        internal static bool Packet(float[] f, int kind, int length)
        {
            if (f == null || f.Length != length || f[0] != kind) return false;
            for (int i = 0; i < f.Length; i++)
                if (float.IsNaN(f[i]) || float.IsInfinity(f[i]) || f[i] < 0f || f[i] > 16777215f || f[i] != (int)f[i]) return false;
            return true;
        }
    }
}

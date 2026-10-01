// Z M5a: bounded vehicle mission, independent of Unity physics and Photon.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercDriveRun
    {
        internal const int Boarding = 0, Outbound = 1, AtPoint = 2, Returning = 3,
            Complete = 4, Failed = 5;
        internal const int NoDriver = 1, NoVehicle = 2, NotReady = 3, Taken = 4,
            NoAuthority = 5, Blocked = 6, TimedOut = 7, Cancelled = 8;
        internal readonly List<Vector3> Path = new List<Vector3>(128);
        internal int Phase, Reason, Next, Recoveries;
        internal float Deadline, WaitUntil, ReverseUntil, RetryUntil;
        float _probeAt, _best = float.MaxValue, _progressAt;
        internal bool Active { get { return Phase < Complete; } }
        internal bool Driving { get { return Phase == Outbound || Phase == Returning; } }

        internal void Begin(float now, float boardingDistance)
        {
            Phase = Boarding; Reason = Next = Recoveries = 0;
            Deadline = now + 35f + boardingDistance / 8f;
            ReverseUntil = RetryUntil = 0f; _best = float.MaxValue;
        }

        internal void Fail(int reason) { Phase = Failed; Reason = reason; }

        internal void Return(float now)
        {
            if (!Active || Phase == Boarding || Phase == Returning || Path.Count < 2) return;
            Phase = Returning;
            Next = Mathf.Clamp(Next - 1, 0, Path.Count - 1);
            SetDeadline(now); ResetProgress(now);
        }

        void SetDeadline(float now)
        {
            float length = 0f;
            for (int i = 1; i < Path.Count; i++) length += Flat(Path[i] - Path[i - 1]);
            Deadline = now + Mathf.Clamp(60f + length / 4f, 120f, 1200f);
        }

        void ResetProgress(float now)
        {
            _probeAt = now; _progressAt = now; _best = float.MaxValue;
            ReverseUntil = RetryUntil = 0f; Recoveries = 0;
        }

        internal void Step(float now, Vector3 at, float speed, bool boarded, bool authority,
            bool driverAlive, bool vehicleAlive, bool ready, bool playerDriver, bool rearFree)
        {
            if (!Active) return;
            if (!driverAlive) { Fail(NoDriver); return; }
            if (!vehicleAlive) { Fail(NoVehicle); return; }
            if (!ready) { Fail(NotReady); return; }
            if (playerDriver) { Fail(Taken); return; }
            if (now > Deadline) { Fail(Phase == Boarding ? NoAuthority : TimedOut); return; }
            if (Phase == Boarding)
            {
                if (!boarded || !authority) return;
                if (Path.Count < 2) { Fail(Blocked); return; }
                Phase = Outbound; Next = 1; SetDeadline(now); ResetProgress(now);
            }
            else if (!authority) { Fail(NoAuthority); return; }
            else if (!boarded) { Fail(NoDriver); return; }
            if (Phase == AtPoint)
            {
                if (now >= WaitUntil) { Return(now); }
                else return;
            }
            int end = Phase == Returning ? 0 : Path.Count - 1;
            float distance = Flat(Path[Next] - at);
            // Stop on the actual endpoint before reporting arrival. No waypoint
            // pass-plane shortcut at the owner's selected destination or home.
            if (Next == end && distance <= 5.6f && speed < 2f)
            {
                Phase = end == 0 ? Complete : AtPoint;
                WaitUntil = now + 8f;
                if (Phase == AtPoint) Deadline = WaitUntil + 1f;
                return;
            }
            if (Next != end && distance < 8.4f)
            {
                Next += Phase == Returning ? -1 : 1;
                ResetProgress(now); distance = Flat(Path[Next] - at);
            }
            if (now < _probeAt) return;
            _probeAt = now + 0.5f;
            // Distance reduction, not wheel speed: driving circles is stuck too.
            if (distance < _best - 2.8f)
            { _best = distance; _progressAt = now; if (now >= RetryUntil) Recoveries = 0; }
            if (now - _progressAt < 4f || now < RetryUntil) return;
            if (Recoveries >= 3) { Fail(Blocked); return; }
            Recoveries++;
            ReverseUntil = rearFree ? now + 1.8f : now;
            RetryUntil = now + 5f;
            _progressAt = now;
        }

        internal Vector3 Aim(Vector3 at, float look)
        {
            int step = Phase == Returning ? -1 : 1;
            Vector3 cur = at;
            for (int k = Next; k >= 0 && k < Path.Count; k += step)
            {
                Vector3 target = Path[k]; target.y = at.y;
                float len = Flat(target - cur);
                if (len >= look && len > 0.001f) return cur + (target - cur) * (look / len);
                look -= len; cur = target;
            }
            return cur;
        }

        internal float Remaining(Vector3 at)
        {
            if (Path.Count == 0) return 0f;
            float d = Flat(Path[Next] - at);
            int step = Phase == Returning ? -1 : 1;
            for (int k = Next; k + step >= 0 && k + step < Path.Count; k += step)
                d += Flat(Path[k + step] - Path[k]);
            return d;
        }

        internal static float Flat(Vector3 v) { return Mathf.Sqrt(v.x * v.x + v.z * v.z); }
    }

    internal sealed class MercDriveLease
    {
        internal int Actor, Vehicle, Driver, Token;
        internal float Until;
        internal bool Live(float now) { return Actor > 0 && now < Until; }
        internal bool Matches(int actor, int vehicle, int driver, int token)
        { return Actor == actor && Vehicle == vehicle && Driver == driver && Token == token; }
        internal bool Grant(float now, int actor, int vehicle, int driver, int token)
        {
            if (actor <= 0 || vehicle <= 0 || driver <= 0 || token <= 0
                || (Live(now) && !Matches(actor, vehicle, driver, token))) return false;
            Actor = actor; Vehicle = vehicle; Driver = driver; Token = token; Until = now + 2.5f;
            return true;
        }
        internal void Clear() { Actor = Vehicle = Driver = Token = 0; Until = 0f; }
        internal static bool Packet(float[] d, int kind, int length)
        {
            if (d == null || d.Length != length || d[0] != kind) return false;
            for (int i = 0; i < d.Length; i++)
                if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) return false;
            for (int i = 1; i <= 5; i++)
                if (d[i] != Mathf.RoundToInt(d[i]) || d[i] < 0 || d[i] > 16777215f) return false;
            return d[2] > 0f && d[3] > 0f && d[4] > 0f;
        }
    }
}

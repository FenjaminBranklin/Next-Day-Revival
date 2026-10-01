// M5b recovery: two bounded local missions share the existing native driver.
using System;
using System.Collections.Generic;
using UnityEngine;
namespace NextDayRevival
{
    internal static partial class MercDrive
    {
        sealed class Mission
        {
            internal MercDriveRun _run;
            internal MercCarrier _car;
            internal MercDriveNative _native;
            internal Mercs.Record _driver;
            internal int _token;
            internal int _driverView;
            internal float _leaseUntil;
            internal float _nextRequest;
            internal float _nextState;
            internal float _nextProbe;
            internal float _front;
            internal float _left;
            internal float _right;
            internal float _rear;
            internal bool _ready;
            internal bool _alive;
            internal bool _driverAlive;
            internal bool _playerDriver;
            internal bool _reportedPoint;
            internal int _lastRecoveries;
            internal int _probeSide;
            internal List<Mercs.Record> Riders = new List<Mercs.Record>(6);
            internal Dictionary<int, MercSeat> RiderColliders = new Dictionary<int, MercSeat>();
            internal GameObject[] PassengerPlayers = new GameObject[32];
            internal readonly List<Mercs.Record> Finished = new List<Mercs.Record>(2);
            internal readonly List<MercOrder> FinishedOrders = new List<MercOrder>(2);
        }
        static readonly Mission Primary = new Mission(), Escort = new Mission();
        static bool _inEscort, _escortReturn;
        internal static bool EscortActive { get { return Escort._run != null && Escort._run.Active; } }
        internal static int EscortFailure { get { return Escort._run != null && Escort._run.Phase == MercDriveRun.Failed ? Escort._run.Reason : 0; } }
        internal static bool NeedsEscortGun { get { return _inEscort; } }
        internal static bool Reserved(MercCarrier c)
        { return c != null && (c == (_inEscort ? Primary._car : Escort._car)); }
        static void StoreMission(Mission m)
        {
            m._run = _run;
            m._car = _car;
            m._native = _native;
            m._driver = _driver;
            m._token = _token;
            m._driverView = _driverView;
            m._leaseUntil = _leaseUntil;
            m._nextRequest = _nextRequest;
            m._nextState = _nextState;
            m._nextProbe = _nextProbe;
            m._front = _front;
            m._left = _left;
            m._right = _right;
            m._rear = _rear;
            m._ready = _ready;
            m._alive = _alive;
            m._driverAlive = _driverAlive;
            m._playerDriver = _playerDriver;
            m._reportedPoint = _reportedPoint;
            m._lastRecoveries = _lastRecoveries;
            m._probeSide = _probeSide;
            m.Riders = Riders; m.RiderColliders = RiderColliders; m.PassengerPlayers = PassengerPlayers;
        }
        static void LoadMission(Mission m)
        {
            _run = m._run;
            _car = m._car;
            _native = m._native;
            _driver = m._driver;
            _token = m._token;
            _driverView = m._driverView;
            _leaseUntil = m._leaseUntil;
            _nextRequest = m._nextRequest;
            _nextState = m._nextState;
            _nextProbe = m._nextProbe;
            _front = m._front;
            _left = m._left;
            _right = m._right;
            _rear = m._rear;
            _ready = m._ready;
            _alive = m._alive;
            _driverAlive = m._driverAlive;
            _playerDriver = m._playerDriver;
            _reportedPoint = m._reportedPoint;
            _lastRecoveries = m._lastRecoveries;
            _probeSide = m._probeSide;
            Riders = m.Riders; RiderColliders = m.RiderColliders; PassengerPlayers = m.PassengerPlayers;
        }
        static void EnterEscort() { StoreMission(Primary); LoadMission(Escort); _inEscort = true; }
        static void LeaveEscort() { StoreMission(Escort); LoadMission(Primary); _inEscort = false; }
        internal static bool StartEscort(List<Mercs.Record> selection, Vector3 target)
        {
            if (EscortActive || selection.Count != 2) return false;
            _escortReturn = false; Escort.Finished.Clear(); Escort.FinishedOrders.Clear();
            EnterEscort();
            try { return Start(selection, target); }
            finally { LeaveEscort(); }
        }
        internal static void ReturnEscort()
        {
            _escortReturn = true; if (!EscortActive) return;
            EnterEscort(); try { Return(); } finally { LeaveEscort(); }
        }
        internal static void CancelEscort()
        {
            if (!EscortActive) return;
            EnterEscort(); try { _run.Fail(MercDriveRun.Cancelled); Finish(); } finally { LeaveEscort(); }
        }
        internal static bool EscortMayRestore(Mercs.Record record)
        {
            if (EscortFailure != 0) return false;
            for (int i = 0; i < Escort.Finished.Count; i++)
                if (Escort.Finished[i] == record) return ReferenceEquals(record.Order, Escort.FinishedOrders[i]);
            return false;
        }
        static void TickEscort()
        {
            if (!EscortActive) return;
            EnterEscort();
            try
            {
                if (!_escortReturn && _run.Phase == MercDriveRun.AtPoint)
                { _run.WaitUntil = Time.time + 10f; _run.Deadline = Time.time + 11f; }
                TickOne();
            }
            finally { LeaveEscort(); }
        }
        static Mission Other { get { return _inEscort ? Primary : Escort; } }
        static bool EscortOwns(object vgs)
        { return Other._native != null && Other._native.Enabled && ReferenceEquals(Other._native.Vgs, vgs); }
        static MercCarrier EscortCarrier(MercUnit u)
        {
            return Other._run != null && Other._run.Active && u != null && u.Order.Mode == MercOrder.Drive
                && Mathf.RoundToInt(-u.Order.Facing.x) == Other._car.View ? Other._car : null;
        }
        static bool EscortDriverClaim(MercCarrier c, MercSeat seat)
        { return Other._run != null && Other._run.Active && c == Other._car && Other._driver != null && Other._driver.Unit != null && Other._driver.Unit.Ride == seat; }
        static void EscortFinished(bool complete)
        {
            if (!_inEscort || !complete) return;
            for (int i = 0; i < Riders.Count; i++)
            { Escort.Finished.Add(Riders[i]); Escort.FinishedOrders.Add(Riders[i].Order); }
        }
    }
}

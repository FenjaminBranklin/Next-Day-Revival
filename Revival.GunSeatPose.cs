// G C1: exclusive pose ownership for a man on an AA gun seat (ZU-23, 52-K).
// Merc posts (MercAA.LateFrame) and garrison crews (FlakCrew.Seat) call this
// every frame they hold a man; the decision rules are GunSeatPoseCore.
//
// The writers it takes back, each once per frame and only when found on:
//   legacy Animation   (merc posts; MercAA keeps the arrays) NPC_AI2.
//                      SetPlayVisualizationValue(true), AirNpcVisual Exit and
//                      NpcDistance unfreeze switch it back on; it then plays
//                      the standing idle/aim clip between budgeted samples
//   aim IK             NPC_AI2.LookAtIkController (Update), NpcWar.DriveAim
//                      and MercMoveShootPose activate _aimIk; FinalIK then
//                      bends chest and arms to the rifle aim in LateUpdate
// No allocation per frame: cached FieldInfos, one dictionary probe per
// garrison man; the IK and its solver are read once when the seat takes him.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class GunSeatPose
    {
        static bool _looked, _solverLooked;
        static FieldInfo _fIk, _fSolver, _fWeight;

        // Diagnostics: foreign writes taken back, logged at most every 30 s.
        internal static int Reclaims;
        static int _bits, _logged;
        static float _logAt;

        sealed class Held
        {
            internal Component Ik;
            internal object Solver;
            internal float Retry;
        }
        static readonly Dictionary<int, Held> _held = new Dictionary<int, Held>();

        /// <summary>The man's native aim IK component (NPC_AI2._aimIk), read
        /// once when the seat takes him. Null when the NPC has none yet.</summary>
        internal static Component AimIk(Component ai)
        {
            if (ai == null) return null;
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    Type t = RevivalPlugin.TypeByName("NPC_AI2");
                    _fIk = t == null ? null : AccessTools.Field(t, "_aimIk");
                }
                return _fIk == null ? null : _fIk.GetValue(ai) as Component;
            }
            catch { return null; }
        }

        /// <summary>Its FinalIK solver (AimIK.solver), read with the IK.</summary>
        internal static object Solver(Component ik)
        {
            if (ik == null) return null;
            try
            {
                if (!_solverLooked)
                {
                    _solverLooked = true;
                    _fSolver = AccessTools.Field(ik.GetType(), "solver");
                    object probe = _fSolver == null ? null : _fSolver.GetValue(ik);
                    _fWeight = probe == null ? null : AccessTools.Field(probe.GetType(), "IKPositionWeight");
                    if (_fWeight != null && _fWeight.FieldType != typeof(float)) _fWeight = null;
                }
                return _fSolver == null ? null : _fSolver.GetValue(ik);
            }
            catch { return null; }
        }

        /// <summary>Once on park: a walking-fire gait layer still running from
        /// the approach stops and publishes its stop to the peers.</summary>
        internal static void StopGait(Component ai)
        {
            if (ai == null) return;
            MercMoveShootPose gait = ai.GetComponent<MercMoveShootPose>();
            if (gait != null) gait.Stop();
        }

        /// <summary>Every frame: an active aim IK is switched off (weight 0
        /// first, so a native re-activation fades in from nothing). Returns
        /// GunSeatPoseCore.AimIk when it had been on.</summary>
        internal static int Calm(Component ik, object solver)
        {
            if (ik == null) return 0;
            GameObject go = ik.gameObject;
            if (go == null || !go.activeSelf) return 0;
            if (solver != null && _fWeight != null) FastField.SetFloat(_fWeight, solver, 0f);
            go.SetActive(false);
            return GunSeatPoseCore.AimIk;
        }

        /// <summary>Garrison crew, each frame FlakCrew samples him seated: his
        /// aim IK stays down. His legacy Animation is left running - the
        /// garrison samples after it every visible near frame and never past
        /// 150 m, so it has no between-sample gap. Returns the writers that
        /// had been on.</summary>
        internal static int Hold(Component ai)
        {
            if (ai == null) return 0;
            int id = ai.GetInstanceID();
            Held h;
            if (!_held.TryGetValue(id, out h))
            {
                h = new Held();
                _held[id] = h;
                h.Ik = AimIk(ai); h.Solver = Solver(h.Ik);
                Calm(h.Ik, h.Solver);
                return 0;   // taking him is not a foreign write
            }
            if (h.Ik == null && Time.time >= h.Retry)
            {
                // Unity can wire _aimIk a frame or two after the NPC starts.
                h.Retry = Time.time + 1f;
                h.Ik = AimIk(ai); h.Solver = Solver(h.Ik);
            }
            return Calm(h.Ik, h.Solver);
        }

        /// <summary>Garrison man no longer seated (wounded, dead, seat option
        /// off): the native aim IK is his again.</summary>
        internal static void Release(Component ai)
        {
            if (ai == null || _held.Count == 0) return;
            _held.Remove(ai.GetInstanceID());
        }

        /// <summary>Forget men whose objects are gone. Called rarely.</summary>
        static readonly List<int> _drop = new List<int>();
        static float _purgeAt;
        internal static void Purge()
        {
            if (_held.Count == 0 || Time.time < _purgeAt) return;
            _purgeAt = Time.time + 30f;
            _drop.Clear();
            foreach (KeyValuePair<int, Held> e in _held)
                if (e.Value.Ik == null) _drop.Add(e.Key);
            for (int i = 0; i < _drop.Count; i++) _held.Remove(_drop[i]);
        }

        /// <summary>Count a frame that found foreign writers. The log line is
        /// the in-game proof: it should stay silent after a man settles.</summary>
        internal static void Note(int foreign)
        {
            if (foreign != 0) { Reclaims++; _bits |= foreign; }
            if (Time.time < _logAt) return;
            _logAt = Time.time + 30f;
            if (Reclaims == _logged) return;
            RevivalPlugin.L.LogInfo("Gun seat pose: took the pose back " + (Reclaims - _logged)
                + " time(s) in 30 s from " + GunSeatPoseCore.Writers(_bits) + ".");
            _logged = Reclaims; _bits = 0;
        }
    }
}

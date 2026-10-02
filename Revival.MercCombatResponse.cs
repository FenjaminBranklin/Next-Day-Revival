// merc-combat-response - the game side of Revival.MercCombatResponseCore.cs
// (docs/ai/tasks/merc-combat-response.md).
//
//   AIM       the owner's held aim (MercAimHold) from the local camera, once
//             a frame for every merc; Camera.main (a tag search) once a
//             second. Every merc of this client belongs to the local player.
//   LANES     who may step out of a line of fire: not a deserter, a man
//             running to his seat, a gun or radar crewman.
//   REACH     a merc's target reach by the weapon in his hands.
//   COST      per frame: one camera transform read; per merc no ray, no
//             allocation. The lane checks are the brain's (arithmetic over
//             at most MercSquad.Max rows each Think).
//
// Mercenaries only. Units: game units (~2.8 per metre). C# 3.0, ASCII only.
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static readonly MercAimHold _ownerAim = new MercAimHold();
        static int _aimFrame = -1;
        static bool _aimHeld;
        static Vector3 _aimFrom, _aimTo;
        static Camera _aimCam;
        static float _aimCamAt;

        /// <summary>The owner's held line of fire (flat, AimLength long), once
        /// a frame. False: he looks round, or there is no camera.</summary>
        static bool MercOwnerAim(float now, out Vector3 from, out Vector3 to)
        {
            int frame = Time.frameCount;
            if (frame != _aimFrame)
            {
                _aimFrame = frame;
                if (_aimCam == null || now >= _aimCamAt) { _aimCam = Camera.main; _aimCamAt = now + 1f; }
                _aimHeld = false;
                if (_aimCam != null && _aimCam)
                {
                    Transform t = _aimCam.transform;
                    _aimHeld = _ownerAim.Update(t.position, t.forward, now, out _aimFrom, out _aimTo);
                }
                else _ownerAim.Reset();
            }
            from = _aimFrom;
            to = _aimTo;
            return _aimHeld;
        }

        /// <summary>May he step out of the owner's or a mate's line of fire?
        /// Not a deserter, a man running to his seat, a gun or radar crewman.</summary>
        static bool MercLanesFree(MercUnit u)
        {
            if (MercCrewPhases.Ground(u) && !u.Deserting && u.Ride.Boarding == null) return true;
            if (u.Deserting || u.Ride.Boarding != null || u.AAView > 0) return false;
            return u.Order.Mode != MercOrder.ManGun && u.Order.Mode != MercOrder.ManRadar;
        }

        /// <summary>How far he looks for a target: his weapon's reach, never
        /// less than the squad's AssaultRange (RangeOf).</summary>
        static float MercReachUnits(Fighter f)
        {
            return MercWeaponReach.Units(f.WeaponId, RangeOf(f));
        }

        static bool MercDefending(Fighter f, float now)
        {
            MercUnit u = f.Squad == null ? null : f.Squad.Merc;
            return u != null && (now < u.DefendUntil || now < u.Fight.RangeDefendUntil
                || f.Suppression > 0.01f);
        }

        // Called only after an actual NpcWar round. A geometric sighting is
        // never incoming fire; remember the real shooter without a world scan.
        static void MercIncoming(Fighter victim, Transform shooter, float now)
        {
            MercUnit u = victim.Squad == null ? null : victim.Squad.Merc;
            if (u == null) return;
            u.Fight.RangeAttacker = shooter;
            u.Fight.RangeDefendUntil = now + MercDefendSeconds;
            u.DefendUntil = Mathf.Max(u.DefendUntil, u.Fight.RangeDefendUntil);
            victim.NextScan = now;
        }

        static bool MercMayFireAt(Fighter f, Vector3 at, float now)
        {
            if (f.Squad == null || f.Squad.Merc == null) return true;
            return MercWeaponReach.Allows(f.WeaponId, f.Tr.position, at, MercDefending(f, now));
        }

        static void MercRangeFightIn(Fighter f, MercUnit u, MercFight ft, float now)
        {
            // Resume ATTACK's existing M1 advance or the other order's post.
            // Maintenance and urgent protection remain in the M2 fight loop.
            if (f.Target != null && !MercMayFireAt(f, f.Target.position, now) && !ft.In.Danger
                && !ft.In.Reloading && ft.Health >= MercBrain.RetreatUntil
                && ft.Brain.Mode == MercBrain.Normal && !Mercs.MedicineWanted(u, now))
                ft.In.MayFight = false;
        }

        // Retaliation is not limited by the acquisition radius. Runs inside
        // the existing serialized 6.7..8 Hz target scan, with one sight test.
        static bool MercRetaliateTarget(Fighter f, float now)
        {
            MercUnit u = f.Squad.Merc;
            Transform at = u.Fight.RangeAttacker;
            if (at == null || now >= u.Fight.RangeDefendUntil) return false;
            Component ai = at.GetComponent(_npcType);
            if (ai == null || !Alive(ai) || !Hostile(f.Hated, FactionOf(ai)) || !MercNpcTargetable(f, ai)) return false;
            float height;
            f.Target = at; f.TargetIsPlayer = false;
            f.Sees = AimPoint(f, at, out height); f.AimHeight = height;
            if (f.Sees) f.LastSeen = now;
            f.NextLos = now + MercAssault.LosGap(UnityEngine.Random.value);
            u.Sense.Note(at, false, now);
            SetMercKillTarget(f, u, null);
            return true;
        }

        /// <summary>A threat sees him where he is - only within MercThreat.SightUnits
        /// (a threat further out is no reason to leave a firing position; a
        /// round from it still counts as a hit). No ray past that distance.</summary>
        static bool MercSeenFrom(Vector3 me, bool low, Vector3 threat)
        {
            return MercThreat.InSight(me, threat) && MercCoverService.Exposed(me, low, threat);
        }

        /// <summary>A FOLLOW slot inside the owner's held aim is moved out of
        /// it, on its own side (the lane step of MercBrain for a merc at rest).</summary>
        static Vector3 MercSlotOutOfAim(Vector3 goal, float now)
        {
            Vector3 from, to;
            if (!MercOwnerAim(now, out from, out to) || !MercSquad.Near(from, to, goal)) return goal;
            float along, side;
            MercLane.Offset(from, to, goal, out along, out side);
            return MercLane.StepOut(from, to, goal, side >= 0f ? 1f : -1f);
        }
    }
}

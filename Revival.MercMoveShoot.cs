// Moving fire in the existing M1/M2 peeks, strafes and short combat bounds.
// No new path search. Native shots retain profile accuracy, ammo and friends.
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static class MercMoveShoot
    {
        internal static ConfigEntry<bool> CfgEnabled;
        internal static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }
        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Mercs", "MoveShoot", true,
                "Walking fire during combat peeks, strafes and short advances, at NPCs or players. "
                + "Disable to restore planted fire. All clients need the same build for walking poses.");
        }
    }

    internal static class MercMovingLane
    {
        static readonly RaycastHit[] Hits = new RaycastHit[64];
        static readonly Collider[] Origins = new Collider[32];
        static int _frame = -1;

        // A failed/denied attempt never consumes ammo. One two-query job per
        // frame globally; the caller retries next frame if another merc won.
        internal static bool MayCheck()
        {
            if (_frame == Time.frameCount) return false;
            _frame = Time.frameCount; return true;
        }

        internal static bool Clear(Vector3 from, Vector3 to, Transform self, Transform target)
        {
            int n = Physics.OverlapSphereNonAlloc(from, 0.12f, Origins, -1, QueryTriggerInteraction.Ignore);
            if (n >= Origins.Length) return false;
            for (int i = 0; i < n; i++)
            {
                Collider c = Origins[i];
                if (c != null && !c.transform.IsChildOf(self)) return false;
            }
            Vector3 dir = to - from; float distance = dir.magnitude;
            if (distance < 0.01f) return false;
            n = Physics.RaycastNonAlloc(from, dir / distance, Hits, distance, -1, QueryTriggerInteraction.Ignore);
            if (n >= Hits.Length) return false;
            for (int i = 0; i < n; i++)
            {
                Collider c = Hits[i].collider;
                if (c == null || c.transform.IsChildOf(self) || c.transform.IsChildOf(target)) continue;
                return false;
            }
            return true;
        }
    }

    public static partial class NpcWar
    {
        static bool MercWalkingPose(Fighter f, MercUnit u, MercFight ft, ref FightOut act, float now)
        {
            FrameProf.S(FrameProf.S_MercMoveShoot_Fire);
            try
            {
                FightOut moving = act;
                MercBrain brain = ft.Brain;
                bool confirmed = brain != null && brain.Cover.Found && brain.Cover.Confirmed;
                bool peek = confirmed && (ft.State == MercBrain.PeekOut || ft.State == MercBrain.PeekBack);
                bool bound = confirmed && ft.State == MercBrain.Dash && act.Act == FightAct.Run
                    && MercMoveShootPolicy.Bound(true, Flat(act.Dest - f.Tr.position), ft.Health,
                        f.Suppression, u.Sense.Count, brain.Mode != MercBrain.Normal);
                bool advance = brain != null && ft.State == MercBrain.AttackFire && ft.In.Attack
                    && act.Act == FightAct.Step && ft.In.MoveShoot;
                bool travel = brain != null && f.Sees && MercMoveShootPolicy.Travel(
                    act.Act == FightAct.Step,
                    act.Act == FightAct.Run && (ft.State == MercBrain.Dash || ft.State == MercBrain.Evade),
                    Flat(act.Dest - f.Tr.position), brain.Mode != MercBrain.Normal);
                // ATTACK keeps its selected goal. A calm opening burst can
                // advance to the already confirmed nearby M1 cover en route.
                if (act.Act == FightAct.Fire && !act.NoShot && !act.Suppress && brain != null
                    && ft.State == MercBrain.Snap && u.Order.Mode == MercOrder.Attack && ft.In.PickFresh)
                {
                    CoverPick pick = u.Sense.Pick;
                    Vector3 me = f.Tr.position, goal = u.Order.Centre;
                    if (MercMoveShootPolicy.Bound(pick.Found && pick.Confirmed,
                        Flat(pick.Point.Pos - me), ft.Health, f.Suppression, u.Sense.Count,
                        brain.Mode != MercBrain.Normal) && Flat(pick.Point.Pos - goal) + 2f < Flat(me - goal))
                    { moving.Act = FightAct.Run; moving.Dest = pick.Point.Pos; bound = true; }
                }
                bool eligible = MercMoveShootPolicy.Eligible(MercMoveShoot.Enabled,
                    moving.Act == FightAct.Step || moving.Act == FightAct.Run, travel || peek || advance || (bound && f.Sees),
                    f.Armed, f.Target != null && f.Target,
                    Reloading(f), u.Medicine != null && u.Medicine.Active != 0,
                    ft.In.Danger, ft.In.Survive || (brain != null && brain.Mode != MercBrain.Normal), ft.Health);
                if (!eligible) { if (ft.MovePose != null) ft.MovePose.Stop(); return false; }
                if (!ft.MovePoseTried)
                {
                    if (!MercMoveShootPose.MaySetup()) return false;
                    ft.MovePoseTried = true;
                    ft.MovePose = MercMoveShootPose.Create(f.Ai, Agent(f));
                }
                if (ft.MovePose == null || !ft.MovePose.Touch(f.WeaponId, AimWorld(f), now)) return false;
                act = moving;
                return true;
            }
            finally { FrameProf.E(FrameProf.S_MercMoveShoot_Fire); }
        }

        static void MercWalkingFire(Fighter f, MercUnit u, MercFight ft, FightOut act, float now)
        {
            FrameProf.S(FrameProf.S_MercMoveShoot_Fire);
            try
            {
            // Pose owns agent rotation while active. Order and cover destinations
            // still belong to M2. No Idle/AddFire: that would ResetPath.
            Face(f);
            bool target = f.Target != null && f.Target;
            if (target && !MercMayFireAt(f, f.Target.position, now))
            { ft.Gate = MercFireGate.HoldRange; return; }
            bool friend = target && MercFriendInLine(f, f.Tr.position, AimWorld(f));
            ft.Gate = MercFireGate.Decide(target, f.Sees, act.NoShot, ft.In.Survive,
                ft.Brain.SurvivalFire, friend, false);
            if (ft.Gate != MercFireGate.Shoot || ft.MovePose == null || !ft.MovePose.Ready(now)
                || now < f.ReactUntil || now < f.NextShot) return;
            Vector3 aim = AimWorld(f), facing = aim - f.Tr.position; facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f || Vector3.Dot(f.Tr.forward, facing.normalized) < 0.8f) return;
            if (!MercMovingLane.MayCheck()) return;
            // At most 5 Hz, including blocked/empty/native rejected rounds.
            // Unlike the base Turret mask this includes IgnoreRaycast props too.
            f.NextShot = now + 0.2f;
            Component weapon = WeaponOf(f);
            Vector3 from = Muzzle(weapon, f);
            if (from == Vector3.zero || !MercMovingLane.Clear(from, aim, f.Tr, f.Target))
            {
                if (f.MuzzleBlockedSince <= 0f) f.MuzzleBlockedSince = now;
                ft.React.Held(MercReaction.Muzzle); return;
            }
            ft.React.Decided(now);
            if (MercWalkingShot(f, weapon, now))
            {
                f.NextShot = now + Mathf.Max(0.2f, ShotDelay(f));
                ft.MovingShots++;
                ft.LastShotAt = now;
                ft.MovePose.Recoil(now);
                bool first = ft.React.Open;
                ft.React.Fired(now);
                if (first && CfgDebug.Value) MercTraceLog(u, ft.React);
            }
            else f.NextShot = Mathf.Max(f.NextShot, now + 0.2f);
            }
            finally { FrameProf.E(FrameProf.S_MercMoveShoot_Fire); }
        }

        static bool MercWalkingShot(Fighter f, Component weapon, float now)
        {
            if (!f.TargetIsPlayer) return Shoot(f);
            // K1b's installed native bridge retains player hit-region/damage
            // and Photon effects. ShootingActions itself requires Idle.
            if (weapon == null || _mercPositionShot == null || _mHasBullets == null || _fRofDelay == null)
                return false;
            if (!FastCall.Bool(_mHasBullets, weapon)) { StartReload(f); return false; }
            float before = FastField.GetFloat(_fRofDelay, weapon);
            if (before >= now || (_mercLineShotTimer != null && FastField.GetFloat(_mercLineShotTimer, f.Ai) > 0f))
                return false;
            _mercPositionShot(f.Ai, f.Target);
            // A geometry veto is not a round, even if the NPC's attempt timer
            // advanced. Match VanillaShot's ammo/rate proof, then count recoil.
            if (FastField.GetFloat(_fRofDelay, weapon) == before) return false;
            if (f.Squad != null) f.Squad.Shots++;
            return true;
        }
    }
}

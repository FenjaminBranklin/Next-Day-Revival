// i-m3-merc-advance-oscillation, the game half: feeds MercMoveOwner
// (Revival.MercMoveOwnerCore.cs) once per tick from the merc chain in
// NpcCombat RunGround, runs its cover/step verdicts, keeps walking fire on
// the order's path and writes the change log (one line per change, LogGap).
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static readonly Vector3[] _ownThreat = new Vector3[1];
        static readonly float[] _ownWeight = { 1f };

        /// <summary>The arbiter's view of this tick, then the fall-back test
        /// (once per tick, before the brain's gate).</summary>
        static void MercOwnerInputs(Fighter f, MercUnit u, MercFight ft, float now)
        {
            MercMoveOwner own = u.Own;
            MercOrder o = u.Order;
            Vector3 me = f.Tr.position;
            own.In.Now = now;
            own.In.Me = me;
            own.In.Order = o;
            bool attack = o.Mode == MercOrder.Attack;
            own.In.Mark = (attack || o.MoveNear) && !u.Deserting;
            Vector3 heading = Vector3.zero;
            if (attack) heading = MercAttackGeo.Dir(o);
            else if (o.MoveNear)
            {
                heading = o.Centre - me; heading.y = 0f;
                heading = heading.sqrMagnitude > 1f ? heading.normalized : Vector3.zero;
            }
            own.In.Heading = heading;
            own.In.Health = ft.Health;
            own.In.Pressure = f.Suppression;
            own.In.Hits = ft.Hits;
            MercSense s = u.Sense;
            bool seen = f.Target != null && f.Target && f.Sees;
            own.In.HasEnemy = seen || s.Count > 0;
            own.In.EnemyAt = seen ? f.Target.position : s.Count > 0 ? s.At[0] : me;
            own.In.Contact = own.In.HasEnemy || (f.Target != null && f.Target && now - f.LastSeen < 3f)
                || (ft.Brain != null && ft.Brain.Fighting);
            MercBrain b = ft.Brain;
            own.In.InCover = f.InCover
                || (b != null && b.Cover.Found && Flat(b.Cover.Point.Pos - me) <= MercMoveOwner.InCoverReach)
                || (u.Close.Covered && Flat(u.Close.Cover - me) <= MercMoveOwner.InCoverReach)
                || (u.Attack.HaveMove && u.Attack.MoveCovered && Flat(u.Attack.MoveAt - me) <= MercMoveOwner.InCoverReach)
                || (u.Halt.Has && Flat(u.Halt.Pick.Point.Pos - me) <= MercMoveOwner.InCoverReach);
            own.In.Busy = Reloading(f) || (u.Medicine != null && u.Medicine.Active != 0)
                || (b != null && b.State == MercBrain.Healing);
            own.FallbackDue(ref own.In);
        }

        /// <summary>FightIn.MayFight through the hold (MercMoveOwner.Gate).</summary>
        static bool MercOwnerGate(MercUnit u, bool raw)
        {
            MercMoveOwner own = u.Own;
            own.In.RawMayFight = raw;
            return own.Gate(ref own.In, own.FallbackNow);
        }

        /// <summary>The one owner decision of this tick. False: the order
        /// (MercStep) runs. act may be replaced by a committed cover run or
        /// forward step (the watchdog, or a fight cover back down the mark).</summary>
        static byte MercOwnerDecide(Fighter f, MercUnit u, MercFight ft, ref FightOut act, float now)
        {
            MercMoveOwner own = u.Own;
            bool moves = act.Act == FightAct.Run || act.Act == FightAct.Step;
            byte v = own.Decide(ref own.In, own.FallbackNow, act.Act, moves, act.Dest);
            if (v != MercMoveOwner.ToCover) return v;
            if (!own.Committed && !MercOwnerCommit(f, u, now))
                return own.Owner == MercLayer.Fight || own.Owner == MercLayer.Fallback
                    ? MercMoveOwner.KeepFight : MercMoveOwner.ToOrder;
            FightOut run = new FightOut();
            // Walking fire if he is shooting or has the target in sight; a sprint otherwise.
            bool fire = f.Armed && f.Target != null && f.Target && (f.Sees || own.KeepFire(now))
                && Flat(own.CommitTo - f.Tr.position) <= MercAssault.Hop;   // a long run to cover stays a sprint
            run.Act = fire ? FightAct.Step : FightAct.Run;
            run.Dest = own.CommitTo;
            run.Face = own.In.EnemyAt;
            run.FireOnMove = true;
            act = run;
            return MercMoveOwner.ToCover;
        }

        /// <summary>ToCover: the nearest free cover within 15 m that is not
        /// further from the enemy (nor back down the mark); none - a short
        /// step toward the enemy. False: the cover budget is spent this
        /// frame (retry next frame).</summary>
        static bool MercOwnerCommit(Fighter f, MercUnit u, float now)
        {
            MercMoveOwner own = u.Own;
            bool query = MercCoverService.MayQuery();
            if (!query && !own.AskTimedOut(now)) return false;
            float radius;
            Vector3 centre = MercMoveOwner.CoverQuery(ref own.In, out radius);
            MercSense s = u.Sense;
            Vector3[] at = s.Count > 0 ? s.At : _ownThreat;
            float[] weight = s.Count > 0 ? s.Weight : _ownWeight;
            int count = s.Count > 0 ? s.Count : 1;
            _ownThreat[0] = own.In.EnemyAt;
            CoverPick pick;
            if (query && MercCoverService.Best(centre, at, weight, count, radius, own.In.Me, MercMoveOwner.CoverReach, u.Id, out pick)
                && MercMoveOwner.CoverOk(ref own.In, pick.Point.Pos)
                && !MercTeam.Board.Crowded(pick.Point.Pos, u.Id, MercMoveOwner.MinSpread * 0.8f, now))
            {
                MercCoverService.Field.Claim(u.Id, pick.Point.Pos, now + 5f, now);
                own.Commit(pick.Point.Pos, true, own.CoverWhy, now);
                return true;
            }
            Vector3 step = MercMoveOwner.ForwardStep(ref own.In, own.StepSide(u.Slot));
            Vector3 ground;
            // A ground spot that puts him back where he stands is no step.
            if (RevivalGroundEnemies.TryGround(step, 8f, out ground) && Flat(ground - own.In.Me) > MercMoveOwner.StepLength * 0.5f) step = ground;
            own.Commit(step, false, MercWriter.Forward, now);
            return true;
        }

        /// <summary>MercMove's leg: at least 5 m from a moving mate (exact
        /// spots and the last hop untouched), noted for the log.</summary>
        static Vector3 MercOwnerLeg(Fighter f, MercUnit u, Vector3 goal, float now)
        {
            MercMoveOwner own = u.Own;
            goal = MercMoveOwner.Spread(u.Id, f.Tr.position, goal, MercAssault.Hop * 0.5f, now);
            own.Note(own.Step, goal, true, own.StepReason, now);
            return goal;
        }

        static void MercOwnerCatchUp(MercUnit u)
        {
            u.Own.Step = MercWriter.CatchUp;
            u.Own.StepReason = "catch-up sprint";
        }

        /// <summary>MercRun's target: a long advance step (not a cover point)
        /// keeps 5 m from a moving mate; noted - a committed cover/step, else
        /// the brain (unless the order's walking-fire leg noted it this tick).</summary>
        static Vector3 MercOwnerRun(Fighter f, MercUnit u, MercFight ft, Vector3 goal, bool walking, float now)
        {
            MercMoveOwner own = u.Own;
            MercBrain b = ft.Brain;
            // Advance steps (AttackFire) and forward steps keep 5 m from a
            // mate; cover points, peek spots and evade strafes stay exact.
            bool advance = (own.Committed && !own.CommitCover) || (b != null && b.State == MercBrain.AttackFire);
            if (advance) goal = MercMoveOwner.Spread(u.Id, f.Tr.position, goal, MercMoveOwner.StillMove * 2f, now);
            if (own.Committed) own.Note(own.CommitWhy, goal, true, walking ? "contact: firing on the move" : "contact: sprint", now);
            else if (own.NotedAt != now) own.Note(MercWriter.Brain, goal, true, MercBrain.Name(ft.State), now);
            return goal;
        }

        /// <summary>The order's leg while he is shooting: the walking-fire
        /// pose carries over the changed path (no sprint, no aim reset). False:
        /// not shooting lately or no pose - the order's own run.</summary>
        static bool MercOrderFire(Fighter f, MercUnit u, Vector3 goal, float now)
        {
            MercFight ft = u.Fight;
            if (!MercMoveShoot.Enabled || ft.Brain == null || ft.MovePose == null || !u.Own.KeepFire(now)) return false;
            if (!f.Armed || f.Target == null || !f.Target || Reloading(f) || ft.Health < 0.35f) return false;
            if (u.Medicine != null && u.Medicine.Active != 0) return false;
            if (!ft.MovePose.Touch(f.WeaponId, AimWorld(f), now)) return false;
            FightOut act = new FightOut();
            act.Act = FightAct.Step;
            act.Dest = goal;
            act.FireOnMove = true;
            MercRun(f, u, ft, goal, false, true, now);
            MercWalkingFire(f, u, ft, act, now);
            return true;
        }

        /// <summary>End of the merc's tick: spacing post, a stand noted if no
        /// sink moved him, the move-change line and the fire-stop line.</summary>
        static void MercOwnerAfter(Fighter f, MercUnit u, float now)
        {
            MercMoveOwner own = u.Own;
            MercFight ft = u.Fight;
            Vector3 me = f.Tr.position;
            MercMoveOwner.Post(u.Id, me, now);
            if (own.NotedAt != now)
            {
                bool brain = own.Owner == MercLayer.Fight || own.Owner == MercLayer.Fallback;
                own.Note(brain ? MercWriter.Brain : own.Step, me, false,
                    brain ? MercBrain.Name(ft.State) : own.StepReason, now);
            }
            if (own.LogDue(me, now))
            {
                RevivalPlugin.L.LogInfo(own.MoveLine(u.Name, me));
                own.Logged();
            }
            if (ft.LastShotAt > own.LastShot) own.Shot(ft.LastShotAt);
            if (own.FireTick(now))
            {
                own.FireWhy = MercFireWhy(f, u, ft, now);
                RevivalPlugin.L.LogInfo(own.FireLine(u.Name));
            }
        }

        /// <summary>K2 (NavigationStep): may the detour use the sense's cover
        /// pick? Not when it lies back down his mark (a loop back).</summary>
        static bool MercOwnerDetourPick(MercUnit u, Vector3 me, Vector3 pick)
        {
            MercMoveOwner own = u.Own;
            return !(own.In.Mark && MercMoveOwner.Backward(me, pick, own.In.Heading));
        }

        /// <summary>A move-target writer outside the chain changed his path
        /// this tick: K2 detour/warp, the ATTACK run's stuck re-path.</summary>
        static void MercOwnerEvent(MercUnit u, string what, Vector3 from, Vector3 to, float now)
        {
            if (!u.Own.EventDue(now)) return;
            RevivalPlugin.L.LogInfo("Mercs: move " + u.Name + " [" + MercLayer.Name(u.Own.Owner) + "] " + what
                + " (" + (from.x / MercMoveOwner.Metre).ToString("0") + "," + (from.z / MercMoveOwner.Metre).ToString("0") + ") -> ("
                + (to.x / MercMoveOwner.Metre).ToString("0") + "," + (to.z / MercMoveOwner.Metre).ToString("0") + ")");
        }

        /// <summary>Why he is not shooting now (the fire-stop line).</summary>
        static byte MercFireWhy(Fighter f, MercUnit u, MercFight ft, float now)
        {
            if (Reloading(f)) return MercFireStop.Reload;
            if (u.Medicine != null && u.Medicine.Active != 0) return MercFireStop.Medicine;
            if (f.Target == null || !f.Target) return MercFireStop.NoTarget;
            if (ft.In.Danger) return MercFireStop.Danger;
            if (ft.In.Survive) return MercFireStop.Survive;
            if (ft.Health < 0.35f) return MercFireStop.Hurt;
            if (!f.Sees || ft.Gate == MercFireGate.HoldUnseen) return MercFireStop.NoSight;
            if (ft.Gate == MercFireGate.HoldRange) return MercFireStop.Range;
            if (ft.Gate == MercFireGate.HoldFriend) return MercFireStop.Friend;
            if (f.MuzzleBlockedSince > 0f) return MercFireStop.Muzzle;
            MercMoveOwner own = u.Own;
            bool fight = own.Owner == MercLayer.Fight || own.Owner == MercLayer.Fallback;
            if (!fight && own.Moving) return MercFireStop.Sprint;
            if (fight && (ft.LastAct == FightAct.Hold || ft.LastAct == FightAct.Reload || ft.LastAct == FightAct.Heal))
                return MercFireStop.Hold;
            if (ft.LastAct == FightAct.Run) return MercFireStop.Sprint;
            return MercFireStop.Pose;
        }
    }
}

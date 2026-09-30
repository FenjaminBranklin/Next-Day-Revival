// merc-attack-orders, the adapter: an ATTACK order on the owner's client.
//
//   ORDER     Mercs.OrderAttack (K wheel ATTACK at the crosshair or along a
//             bounded direction, a map click): Revival.Mercs.cs. The run
//             itself is Revival.MercAttackCore.cs.
//   GATE      MercMayStand -> MercAttackMayStand: the core decides whether
//             the M2 brain may fight where he stands (corridor, immediate
//             threats, the push after dry fights). The owner's distance and
//             the FOLLOW leash play no part.
//   STEP      MercStep -> MercAttackStep out of a fight: the core's move or
//             hold through MercMove / MercCrouch / MercLook (NavMesh moves,
//             throttled re-orders, grounded goals - never a warp).
//   LEASH     MercLeash -> the corridor near him, or his hold circle.
//   NEWS      arrival, completion and a stalled attack as owner toasts.
//
// Every read is a field of the fighter or the merc: no ray, no allocation
// per frame (the toast strings only when there is news). C# 3.0, ASCII only
// apart from the Russian Loc.T texts.
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static void MercVisibleTail(Fighter f, float rangeSqr, float now)
        {
            int rays = 0, count = Mathf.Min(_scene.Count, 32);
            for (int i = 0; i < count && rays < 2; i++)
            {
                if (f.MercLookCursor >= _scene.Count) f.MercLookCursor = 0;
                Component c = _scene[f.MercLookCursor++];
                if (c == null || c == f.Ai) continue;
                Transform tr = c.transform;
                if ((tr.position - f.Tr.position).sqrMagnitude >= rangeSqr) continue;
                bool tried = false;
                for (int k = 0; k < 3; k++) if (_cand[k] == tr) tried = true;
                if (tried) continue;
                Fighter other = FighterOf(c);
                if (other != null && other.Squad == f.Squad) continue;
                if (!Hostile(f.Hated, FactionOf(c)) || !Alive(c) || MercRide.HiddenRider(c)) continue;
                if ((other == null || other.Squad == null) && !Targetable(c)) continue;
                rays++;
                float height;
                if (!AimPoint(f, tr, out height)) continue;
                f.Target = tr; f.TargetIsPlayer = false; f.Sees = true;
                f.AimHeight = height; f.LastSeen = now;
                break;
            }
        }

        static void MercAttackInputs(Fighter f, MercUnit u, float now, out MercAttackIn i)
        {
            i = new MercAttackIn();
            i.Now = now;
            i.Me = f.Tr.position;
            MercFight ft = u.Fight;
            i.Fighting = ft.Brain != null && ft.Brain.Fighting;
            // Rounds at an NPC count in the squad; a player target is answered
            // by the game's own shot, counted by the reaction trace.
            i.Shots = (f.Squad == null ? 0 : f.Squad.Shots) + ft.React.Answered;
            i.Hits = ft.Hits;
            i.Target = f.Target != null && f.Target;
            i.Sees = i.Target && f.Sees;
            i.TargetAt = i.Target ? f.Target.position : i.Me;
            i.Danger = ft.In.Danger;
            i.Protected = ft.Health < MercBrain.RetreatBelow || Reloading(f)
                || (u.Sense.Exposed && now - u.Sense.ExposedAt < 0.8f);
            MercAttackTeam team = u.Order.Team as MercAttackTeam;
            if (team != null)
                team.Covering(u.Order.K, now, ft.Brain != null
                    && ft.Out.Act == FightAct.Fire && !ft.Out.NoShot && f.Armed
                    && ft.Health >= MercBrain.RetreatBelow && !Reloading(f)
                    && (ft.Brain.Cover.Found || !u.Sense.Exposed || now - u.Sense.ExposedAt >= 0.8f)
                    && f.PlantedSince > 0f && now - f.PlantedSince >= PlantSeconds
                    && (ft.Gate == MercFireGate.Shoot || ft.Gate == MercFireGate.Suppress)
                    && f.MuzzleBlockedSince <= 0f && (i.Sees || ft.Out.Suppress));
        }

        /// <summary>ATTACK: may the brain fight here (MercMayStand)?</summary>
        static bool MercAttackMayStand(Fighter f, MercUnit u)
        {
            MercAttackIn i;
            MercAttackInputs(f, u, Time.time, out i);
            return u.Attack.Gate(u.Order, ref i);
        }

        /// <summary>ATTACK out of a fight (MercStep).</summary>
        static void MercAttackStep(Fighter f, MercUnit u, float now)
        {
            MercOrder o = u.Order;
            MercAttackRun run = u.Attack;
            if (run.For != o) run.Begin(o, now, f.Tr.position);
            // The hold spot on the ground, once per order (MercMayStand may
            // have begun the run in the fight's Think).
            if (!run.Grounded)
            {
                run.Grounded = true;
                Vector3 g;
                if (RevivalGroundEnemies.TryGround(run.HoldAt, 10f, out g)) run.HoldAt = g;
            }
            MercAttackIn i;
            MercAttackInputs(f, u, now, out i);
            run.Gate(o, ref i);
            MercAttackAct a;
            run.Step(o, ref i, out a);
            if (run.News != 0) MercAttackNews(u, run);
            if (a.Act == MercAttackAct.MoveTo)
            {
                Vector3 dest = MercAttackWaypoint(f, u, a.Dest, now);
                // The same M3 board as the fight loop: a runner calls for
                // covering/suppressing fire rather than disappearing from it.
                MercSense sense = u.Sense;
                MercTeam.Board.Post(u.Id, now, i.Me, sense.Count > 0, false, dest, false,
                    true, true, false, sense.Count > 0, sense.Count > 0 ? sense.At[0] : dest,
                    u.Fight.Health, MercBrain.Normal);
                MercTeam.Board.PostLane(u.Id, now, false, dest, false);
                MercMove(f, u, dest, true, now);
                return;
            }
            if (a.Low) MercCrouch(f, now);
            else Hold(f, null, now);
            if (a.Sweep > 0f) MercLook(f, u, a.Face, a.Sweep, 2f, now);
            else FaceDir(f, a.Face);
        }

        /// <summary>M1 cover-to-cover advance. No cover: a short sprint,
        /// retrying while moving; an unavailable pick never vetoes the order.</summary>
        static Vector3 MercAttackWaypoint(Fighter f, MercUnit u, Vector3 goal, float now)
        {
            MercAttackRun run = u.Attack;
            Vector3 me = f.Tr.position;
            if (run.HaveMove && Flat(run.MoveAt - me) > MercAssault.Precise
                && Flat(run.MoveAt - goal) < Flat(me - goal) + 2f
                && (run.MoveCovered || now < run.NextCover || u.Sense.Count == 0)) return run.MoveAt;
            Vector3 hop = MercAssault.HopTo(me, goal);
            run.MoveCovered = false;
            // The final hold spot remains exact. Cover decisions use the same
            // globally throttled service and claims as M2, at most 2 Hz.
            if (u.Sense.Count > 0 && Flat(goal - me) > MercAssault.Hop
                && now >= run.NextCover && MercCoverService.MayQuery())
            {
                run.NextCover = now + MercAssault.CoverEvery;
                CoverPick pick;
                Vector3 leash; float radius;
                run.Leash(u.Order, me, out leash, out radius);
                Vector3 forward = goal - me; forward.y = 0f;
                if (MercCoverService.Best(hop, u.Sense.At, u.Sense.Weight, u.Sense.Count,
                    MercAssault.Hop, leash, radius, u.Id, out pick)
                    && Flat(pick.Point.Pos - me) <= MercAssault.Hop + 2f
                    && Flat(pick.Point.Pos - goal) + 2f < Flat(me - goal)
                    && Vector3.Dot(pick.Point.Pos - me, forward) <= forward.sqrMagnitude
                    && !MercTeam.Board.Crowded(pick.Point.Pos, u.Id, MercSquad.Spread, now))
                {
                    hop = pick.Point.Pos;
                    MercCoverService.Field.Claim(u.Id, hop, now + 5f, now);
                    run.MoveCovered = true;
                }
                else run.MoveCovered = false;
            }
            run.MoveAt = hop; run.HaveMove = true;
            return hop;
        }

        static void MercAttackLeash(Fighter f, MercUnit u, out Vector3 centre, out float radius)
        {
            u.Attack.Leash(u.Order, f.Tr.position, out centre, out radius);
        }

        /// <summary>The owner hears about an arrival once per merc, the
        /// objective held once per order, and every stalled attack.</summary>
        static void MercAttackNews(MercUnit u, MercAttackRun run)
        {
            byte news = run.News;
            run.News = 0;
            MercOrder o = u.Order;
            float metres = MercAttackGeo.Flat(o.Centre - Mercs.OwnerPosition) / 2.8f;
            if ((news & MercAttackRun.NewsStalled) != 0)
            {
                float left = MercAttackGeo.Flat(o.Centre - u.Ai.transform.position) / 2.8f;
                MercUi.Toast(u.Name + Loc.T(": не может пройти дальше - держится в ", ": cannot get further - holding ")
                    + left.ToString("0") + Loc.T(" м от цели атаки.", " m short of the attack objective."), true);
                RevivalPlugin.L.LogInfo("Mercs: " + u.Name + " stalled on the attack " + left.ToString("0") + " m short.");
            }
            if ((news & MercAttackRun.NewsComplete) != 0)
                MercUi.Toast(Loc.T("АТАКА: цель взята, наёмники держат её (", "ATTACK: objective taken, the mercs hold it (")
                    + metres.ToString("0") + Loc.T(" м от вас).", " m from you)."), false);
            else if ((news & MercAttackRun.NewsArrived) != 0 && o.N == 1)
                MercUi.Toast(u.Name + Loc.T(": на цели атаки, держит.", ": at the attack objective, holding."), false);
        }
    }
}

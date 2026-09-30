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
                MercMove(f, u, a.Dest, a.Run, now);
                return;
            }
            if (a.Low) MercCrouch(f, now);
            else Hold(f, null, now);
            if (a.Sweep > 0f) MercLook(f, u, a.Face, a.Sweep, 2f, now);
            else FaceDir(f, a.Face);
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

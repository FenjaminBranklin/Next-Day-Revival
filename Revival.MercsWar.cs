// B3 mercenaries, the fighting half: NpcWar drives a hired man exactly like
// an editor ground group (target choice, the planted shot, reloads, armour),
// and this file replaces the ground duty with the owner's orders.
//
//   OWNER CLIENT  every merc is a one-man ground squad whose Squad.Merc links
//                 to his MercUnit (Revival.Mercs.cs). NpcWar.Tick runs these
//                 squads on the owner whether or not he is the master
//                 (TickMercSquads); every other squad stays master-only.
//   ORDERS        FOLLOW: a wedge slot behind the owner, run when far, warp
//                 when lost (> WarpUnits). STAY: a ring slot at the chosen
//                 point. A merc breaks off a fight when his owner runs off.
//                 B3b PATROL: the owner's loop, each merc from his own leg.
//                 B3b PERIMETER: sector posts, longer reach, faster scans,
//                 flank sweeps, corner checks, a short pursuit.
//                 M2: in a fight every merc, whatever his order, runs to
//                 cover, peeks, fires short bursts and relocates
//                 (Revival.MercFight.cs); the order runs again after it.
//                 merc-attack-orders ATTACK: advance along a corridor to the
//                 objective, fight in it, hold the objective
//                 (Revival.MercAttack.cs / Revival.MercAttackCore.cs).
//                 B3c FOLLOW MY VEHICLE: on foot he runs to the seat
//                 Revival.MercsRide.cs gave him, else FOLLOW; seated, RunGround
//                 leaves him to the ride (MercSeated) and its vehicle gun.
//   TARGETS       hostile NPCs through PickTargetForMan unchanged; players
//                 through MercPlayerTarget (vanilla sensing only ever sees
//                 the local player, who is the owner): hostile faction, not
//                 the owner, not whitelisted, alive. The vanilla kill target is
//                 set to that player so NPC_AI2.ShootingActions fires the
//                 game's own shot. PEACEFUL: no target unless hit in the last
//                 DefendSeconds, and then only NPCs and the player who hit him.
//   REMOTE HITS   an NPC another client owns (a merc hit from the master, a
//                 settlement NPC hit from a non-master owner) takes the round
//                 as the game's own "ApplyDamage" RPC to its owner (RemoteHit).
//   MASTER        remote mercs are listed as Watch squads so master squads and
//                 defenders fight them; a settlement NPC hit by a merc enlists.
//
// Units: the world is modelled ~2.8x real size (NpcWar SCALE note).
// C# 3.0, ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        const float MercRunUnits = 45f;        // run to the slot beyond this
        const float MercBreakOffUnits = 75f;   // leave a fight to follow beyond this
        const float MercFightLeashUnits = 120f; // merc-combat-response: in a fight, follow only beyond this (43 m)
        const float MercWarpUnits = 1000f;     // lost: put him beside the owner
        const float MercDefendSeconds = 20f;   // peaceful: fire back this long
        const float MercStayLeash = 60f;       // STAY: return after a chase this far

        static int _watchCount;
        static float _nextWatch;
        static readonly HashSet<int> _notMerc = new HashSet<int>();

        // ------------------------------------------------------------ squads

        /// <summary>Take one freshly spawned merc under NpcWar control.</summary>
        internal static bool StartMerc(string tag, GameObject settlement, Component ai,
            MercUnit unit, RevivalComposition.CrewMan spec)
        {
            if (!LookUp() || settlement == null || ai == null || IsActive(tag)) return false;
            Squad s = new Squad();
            s.Tag = tag; s.Settlement = settlement; s.Lz = ai.transform.position;
            s.GroundGroup = true; s.GroundRadius = 10f;
            s.GroundDuty = GroundMode.Waiting;
            s.Centre = s.Lz; s.Front = ai.transform.forward;
            s.Merc = unit;
            CrewSector sector = ai.GetComponent<CrewSector>();
            if (sector != null) UnityEngine.Object.Destroy(sector);
            Fighter f = NewFighter(ai, s);
            f.GroundDest = f.Tr.position;
            Equip(f, spec);
            if (spec.Weapons != null && spec.Weapons.Length > 0 && spec.Weapons[0] == Stinger.ItemId)
                f.Manpads = new MercStingerState();
            // Traits (docs/ai/tasks/mercenaries.md 4.6): precise sharpens his
            // NPC-versus-NPC shot, tanky takes a further share off every hit.
            f.Skill = Mathf.Clamp(f.Skill * (1f + unit.Precise / 100f), 0.5f, 2f);
            f.ArmorScale = Mathf.Clamp(f.ArmorScale * (1f - unit.Tanky / 200f), 0.2f, 1f);
            // M3: his grade (MercGrade.Of his traits) - a higher tier keeps
            // more of his hit chance over range (all of the falloff at grade
            // 0, 40 % of it at grade 1).
            f.RangeFalloff = MercGrade.Lerp(unit.Grade, 1f, 0.4f);
            s.Men.Add(f); _armoured[ai.GetInstanceID()] = f;
            EnsurePointsRoot(); _squads.Add(s);
            return true;
        }

        /// <summary>Stop driving a merc (dismissed, deserted, despawned). The
        /// body is left alone: the caller destroys or keeps it.</summary>
        internal static void StopMerc(MercUnit unit)
        {
            for (int i = _squads.Count - 1; i >= 0; i--)
                if (_squads[i].Merc == unit) DropMercSquad(_squads[i]);
        }

        static void DropMercSquad(Squad s)
        {
            MercCoverService.Forget(s.Merc);
            for (int i = 0; i < s.Men.Count; i++)
            {
                Fighter f = s.Men[i];
                if (f.Point != null) UnityEngine.Object.Destroy(f.Point);
                if (f.Ai != null) _armoured.Remove(f.Ai.GetInstanceID());
            }
            if (s.Watch) _watchCount = Mathf.Max(0, _watchCount - 1);
            _squads.Remove(s);
        }

        /// <summary>Off-master: only the local player's own mercs are run.</summary>
        static void TickMercSquads(float now)
        {
            bool any = false;
            for (int q = 0; q < _squads.Count; q++)
                if (_squads[q].Merc != null) { any = true; break; }
            if (!any) return;
            PatrolTargets();
            for (int q = _squads.Count - 1; q >= 0; q--)
            {
                if (q >= _squads.Count) continue;
                Squad s = _squads[q];
                if (s.Merc == null) continue;
                try { RunSquad(s, now); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("NpcWar: merc " + s.Tag + " - " + ex);
                    DropMercSquad(s);
                }
            }
        }

        // ------------------------------------------------------------ orders

        // B3b. PATROL: each merc walks the owner's loop from his own start leg
        // (a group is spread along the route, never stacked on one point),
        // stands at every point looking out, and fights within PatrolLeash of
        // the route before he picks it up again. PERIMETER: posts spread in
        // sectors around the centre, a longer sensor reach and three times the
        // scan rate, flank sweeps and corner checks, cover in a fight, a short
        // pursuit, then back to the post. Every NavMesh sample is cached per
        // order and leg: nothing here samples per frame.
        const float MercPatrolLeash = 90f;     // fight this far off the route
        const float MercChaseUnits = 84f;      // perimeter: pursue 30 m past the edge
        const float MercChaseSeconds = 12f;

        internal static bool MercMayEngage(MercUnit u, float now)
        {
            if (u.Deserting) return false;
            return !u.Peaceful || now < u.DefendUntil;
        }

        /// <summary>May he stand and shoot here, or must he move?</summary>
        static bool MercMayStand(Fighter f, MercUnit u)
        {
            if (u.Deserting) return false;
            if (u.Order.Survive || u.Rally) return true;
            MercOrder o = u.Order;
            switch (o.Mode)
            {
                case MercOrder.Follow:
                case MercOrder.Vehicle:
                    // B3c: a man running to his seat does not stop for a fight.
                    if (u.Ride.Boarding != null) return false;
                    // merc-combat-response: a fight already running holds him
                    // to a longer leash (hysteresis) - a few steps of the owner
                    // no longer cancel the shots to regain the follow slot.
                    return u.Owner == null || Flat(u.Owner.position - f.Tr.position)
                        <= (u.Fight.Brain != null && u.Fight.Brain.Fighting ? MercFightLeashUnits : MercBreakOffUnits);
                case MercOrder.Patrol:
                    return RouteDistance(o, f.Tr.position) <= MercPatrolLeash;
                case MercOrder.Perimeter:
                    return Flat(o.Centre - f.Tr.position) <= o.RadiusUnits + MercChaseUnits + 20f;
                case MercOrder.Attack:
                    // merc-attack-orders: the corridor and the threats decide,
                    // never the owner's distance (Revival.MercAttack.cs).
                    return MercAttackMayStand(f, u);
                default:
                    return Flat(o.Centre - f.Tr.position) <= MercStayLeash;
            }
        }

        /// <summary>How far a merc looks for a target: his weapon's reach
        /// (merc-combat-response: a rifle 160 m, never less than the squad's
        /// AssaultRange), a perimeter guard clearly further, and at least to
        /// 50 m past his circle.</summary>
        static float MercSeekRange(Fighter f)
        {
            float r = RangeOf(f);
            float reach = MercReachUnits(f);
            MercUnit u = f.Squad == null ? null : f.Squad.Merc;
            if (u == null || !u.Alert) return reach;
            // From a sector post (0.55 R out) the far edge is 1.55 R away.
            float edge = u.Order.RadiusUnits * (u.Order.N > 1 ? 1.55f : 1f) + 140f;
            return Mathf.Max(reach, Mathf.Min(Mathf.Max(r * 1.6f, edge), 600f));
        }

        /// <summary>The target scan interval: three times the rate on a
        /// perimeter (about 7 a second instead of 2.7).</summary>
        static float MercScanGap(Fighter f)
        {
            MercUnit u = f.Squad == null ? null : f.Squad.Merc;
            float gap = 0.3f + UnityEngine.Random.value * 0.15f;
            return u != null && u.Alert ? gap / 3f : gap;
        }

        /// <summary>The moment between seeing a new target and the first
        /// shot, shortened on a perimeter and (M3) by his grade: x1.15 at
        /// grade 0 down to x0.7 at grade 1.</summary>
        static float MercReactScale(Fighter f)
        {
            MercUnit u = f.Squad == null ? null : f.Squad.Merc;
            if (u == null) return 1f;
            return (u.Alert ? 0.35f : 1f) * MercGrade.Lerp(u.Grade, 1.15f, 0.7f);
        }

        /// <summary>One merc out of contact: walk or run where his order
        /// puts him.</summary>
        static void MercStep(Fighter f, Squad s, float now)
        {
            MercUnit u = s.Merc;
            // The fight in between is not a stalled leg: the patrol clock
            // stands still while he was shooting instead of stepping.
            if (u.LastStep > 0f && now - u.LastStep > 0.5f)
            {
                u.LegUntil += now - u.LastStep;
            }
            u.LastStep = now;
            if (u.Deserting)
            {
                Vector3 away = u.DesertTo;
                if (Flat(away - f.Tr.position) < 4f) { Hold(f, null, now); return; }
                MercMove(f, u, away, false, now);
                return;
            }
            switch (u.Order.Mode)
            {
                case MercOrder.Follow: MercFollow(f, u, now); return;
                case MercOrder.Vehicle: MercBoard(f, u, now); return;
                case MercOrder.Patrol: MercPatrol(f, u, now); return;
                case MercOrder.Perimeter: MercPerimeter(f, u, now); return;
                case MercOrder.ManGun:
                case MercOrder.ManRadar: MercPostStep(f, u, now); return;
                case MercOrder.Attack: MercAttackStep(f, u, now); return;
                default: MercStay(f, u, now); return;
            }
        }

        static void MercFollow(Fighter f, MercUnit u, float now)
        {
            Transform owner = u.Owner;
            if (owner == null) { Hold(f, null, now); return; }
            Vector3 fwd = owner.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 side = Side(fwd);
            // The wedge: two per rank behind the owner, the fifth at the tail.
            int rank = u.Slot / 2;
            float lateral = u.Slot >= 4 ? 0f : (u.Slot % 2 == 0 ? -1f : 1f) * (7f + 5f * rank);
            Vector3 goal = owner.position - fwd * (12f + 8f * rank) + side * lateral;
            // merc-combat-response: never a slot inside the owner's held aim.
            goal = MercSlotOutOfAim(goal, now);
            float dist = Flat(owner.position - f.Tr.position);
            if (dist > MercWarpUnits && now >= u.NextWarp)
            {
                u.NextWarp = now + 10f;
                Vector3 spot;
                if (RevivalGroundEnemies.TryGround(goal, 12f, out spot)) MercWarp(f, spot);
                return;
            }
            // Close to the owner and near his slot: stand, watch his front.
            if (Flat(goal - f.Tr.position) < 7f && dist < 30f)
            {
                Hold(f, null, now); FaceDir(f, fwd); return;
            }
            MercMove(f, u, goal, dist > MercRunUnits, now);
        }

        /// <summary>B3c FOLLOW MY VEHICLE on foot: run to the door of the seat
        /// he was given (Revival.MercsRide.cs seats him there), else FOLLOW -
        /// which is also what the men without a seat do.</summary>
        static void MercBoard(Fighter f, MercUnit u, float now)
        {
            MercSeat st = u.Ride;
            if (st.Boarding == null) { MercFollow(f, u, now); return; }
            if (Flat(st.BoardAt - f.Tr.position) < 3f) { Hold(f, null, now); return; }
            MercMove(f, u, st.BoardAt, true, now);
        }

        static void MercStay(Fighter f, MercUnit u, float now)
        {
            MercOrder o = u.Order;
            if (u.GoalFor != o) { u.GoalFor = o; u.Goal = Beside(o.Centre, o.K); }
            float dist = Flat(u.Goal - f.Tr.position);
            if (dist < 4f)
            {
                Hold(f, null, now);
                FaceDir(f, o.Facing.sqrMagnitude > 0.01f ? o.Facing : f.Tr.forward);
                return;
            }
            MercMove(f, u, u.Goal, dist > 25f, now);
        }

        static void MercPatrol(Fighter f, MercUnit u, float now)
        {
            MercOrder o = u.Order;
            int n = o.Points.Length;
            if (n < 2) { MercStay(f, u, now); return; }
            if (u.GoalFor != o)
            {
                u.GoalFor = o;
                u.GoalLeg = -1; u.PauseUntil = 0f;
                if (o.N > 1) u.Leg = (o.K * n / o.N) % n;
                else
                {
                    // Alone: he joins the route at the point nearest to him.
                    float best = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        float d = Flat(o.Points[i] - f.Tr.position);
                        if (d < best) { best = d; u.Leg = i; }
                    }
                }
            }
            if (u.GoalLeg != u.Leg)
            {
                u.GoalLeg = u.Leg;
                // Two mercs who share a point stand side by side (sunflower
                // slot by group index), not on top of each other.
                u.Goal = Beside(o.Points[u.Leg], o.N > n ? o.K : 0);
                u.LegUntil = now + 30f + Flat(u.Goal - f.Tr.position) / 0.8f;
            }
            if (now < u.PauseUntil)
            {
                // At the point: stand and look out, then along the route.
                Hold(f, null, now);
                Vector3 centre = Vector3.zero;
                for (int i = 0; i < n; i++) centre += o.Points[i];
                centre /= n;
                MercLook(f, u, FlatV(o.Points[(u.Leg + n - 1) % n] - centre), 70f, 2.2f, now);
                return;
            }
            float dist = Flat(u.Goal - f.Tr.position);
            if (dist < 5f || now >= u.LegUntil)
            {
                if (dist < 5f) { u.PauseUntil = now + UnityEngine.Random.Range(4f, 8f); u.NextLook = now; }
                u.Leg = (u.Leg + 1) % n;
                Hold(f, null, now);
                return;
            }
            // Walk the route; run only to get back onto it.
            float off = SegmentDistance(f.Tr.position, o.Points[(u.Leg + n - 1) % n], o.Points[u.Leg]);
            MercMove(f, u, u.Goal, off > MercRunUnits, now);
        }

        static void MercPerimeter(Fighter f, MercUnit u, float now)
        {
            MercOrder o = u.Order;
            float radius = o.RadiusUnits;
            if (u.GoalFor != o)
            {
                u.GoalFor = o;
                u.Chasing = false; u.ProbePhase = 0;
                u.NextProbe = now + UnityEngine.Random.Range(15f, 25f);
                Vector3 look = FlatV(o.Facing);
                if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
                look.Normalize();
                Vector3 post = o.Centre;
                if (o.N > 1)
                {
                    // Sector k of n, the first one facing where the owner looked.
                    float a = Mathf.Atan2(look.x, look.z) + o.K * Mathf.PI * 2f / o.N;
                    look = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                    post = o.Centre + look * radius * 0.55f;
                }
                Vector3 g;
                u.Post = RevivalGroundEnemies.TryGround(post, 10f, out g) ? g : Beside(o.Centre, o.K);
                u.PostLook = look;
            }
            Vector3 me = f.Tr.position;
            // Contact: a short pursuit inside the chase ring, then back. M2:
            // the fight loop holds him in cover while the target shows; the
            // pursuit starts when it lets go (6 s unseen) and ends 12 s after
            // the last sight - seen again, he is back in the fight loop.
            bool contact = f.Target != null && f.Target && now - f.LastSeen < MercChaseSeconds;
            if (contact && now >= u.ChaseCooldown
                && Flat(f.Target.position - o.Centre) <= radius + MercChaseUnits)
            {
                if (!u.Chasing) { u.Chasing = true; u.ChaseUntil = now + MercChaseSeconds; }
                if (now < u.ChaseUntil)
                {
                    Vector3 to = FlatV(f.Target.position - me);
                    float d = to.magnitude;
                    Vector3 goal = d > 25f ? me + to / d * (d - 20f) : me;
                    Vector3 fromCentre = FlatV(goal - o.Centre);
                    float limit = radius + MercChaseUnits;
                    if (fromCentre.magnitude > limit) goal = o.Centre + fromCentre.normalized * limit;
                    if (Flat(goal - me) < 4f) { MercCrouch(f, now); FaceDir(f, to); return; }
                    MercMove(f, u, goal, true, now);
                    return;
                }
                u.Chasing = false;
                u.ChaseCooldown = now + 8f;
            }
            else if (u.Chasing && (!contact || now >= u.ChaseUntil))
            {
                u.Chasing = false;
                u.ChaseCooldown = now + 4f;
            }
            if (!contact && f.Cover != Vector3.zero) { f.Cover = Vector3.zero; f.InCover = false; }
            // Corner check: now and then out to the edge of his sector, a
            // look along the flanks there, and back to the post.
            if (!contact && u.ProbePhase == 0 && now >= u.NextProbe)
            {
                u.NextProbe = now + UnityEngine.Random.Range(25f, 40f);
                float spread = o.N > 1 ? 180f / o.N : 180f;
                float a = Mathf.Atan2(u.PostLook.x, u.PostLook.z) * Mathf.Rad2Deg
                    + UnityEngine.Random.Range(-spread, spread);
                Vector3 dir = new Vector3(Mathf.Sin(a * Mathf.Deg2Rad), 0f, Mathf.Cos(a * Mathf.Deg2Rad));
                Vector3 g;
                if (RevivalGroundEnemies.TryGround(o.Centre + dir * radius * 0.85f, 8f, out g))
                { u.Probe = g; u.ProbePhase = 1; u.ProbeUntil = now + 25f; }
            }
            if (contact) u.ProbePhase = 0;
            if (u.ProbePhase == 1)
            {
                if (Flat(u.Probe - me) < 4f) { u.ProbePhase = 2; u.ProbeUntil = now + UnityEngine.Random.Range(4f, 6f); u.NextLook = now; }
                else if (now >= u.ProbeUntil) u.ProbePhase = 0;
                else { MercMove(f, u, u.Probe, false, now); return; }
            }
            if (u.ProbePhase == 2)
            {
                if (now < u.ProbeUntil)
                {
                    MercCrouch(f, now);
                    MercLook(f, u, FlatV(u.Probe - o.Centre), 80f, 1.2f, now);
                    return;
                }
                u.ProbePhase = 0;
            }
            float dist = Flat(u.Post - me);
            if (dist > 4f) { MercMove(f, u, u.Post, dist > 25f, now); return; }
            // At the post: low, facing the last threat for a while, otherwise
            // sweeping his sector and both flanks (all round when alone).
            MercCrouch(f, now);
            if (f.Target != null && f.Target && now - f.LastSeen < 12f) { FaceDir(f, f.Target.position - me); return; }
            MercLook(f, u, u.PostLook, o.N > 1 ? 180f / o.N + 25f : 180f, 1.8f, now);
        }

        /// <summary>Stand still, low.</summary>
        static void MercCrouch(Fighter f, float now)
        {
            f.Stance = Stance.Hold;
            f.HasOrder = false;
            if (f.IkDriven) ReleaseAim(f);
            Drive(f, MainIdle, AddNone, PoseCrouch, now, true);
        }

        /// <summary>A watch: the look swings through -sweep, 0, +sweep, 0
        /// around the base direction every few seconds (180 = all round in
        /// quarter turns).</summary>
        static void MercLook(Fighter f, MercUnit u, Vector3 dir, float sweep, float every, float now)
        {
            if (dir.sqrMagnitude < 0.01f) dir = f.Tr.forward;
            if (now >= u.NextLook)
            {
                u.NextLook = now + every * UnityEngine.Random.Range(0.8f, 1.4f);
                u.LookStep = (u.LookStep + 1) % 4;
            }
            float off = sweep >= 179f ? u.LookStep * 90f
                : u.LookStep == 1 ? -sweep : u.LookStep == 3 ? sweep : 0f;
            FaceDir(f, Quaternion.Euler(0f, off, 0f) * dir);
        }

        /// <summary>To a point at a walk or a run; the move is re-issued only
        /// when it changes, never per frame.</summary>
        static void MercMove(Fighter f, MercUnit u, Vector3 goal, bool run, float now)
        {
            int state = run ? MainRun : MainWalk;
            bool reorder = !f.HasOrder || now >= f.MoveDeadline || Flat(f.Ordered - goal) > 6f
                || f.WantMain != state;
            if (reorder && now >= u.NextOrder)
            {
                u.NextOrder = now + 0.8f;
                Vector3 dest;
                if (!RevivalGroundEnemies.TryGround(goal, 8f, out dest)) dest = goal;
                Go(f, dest, state, PoseStand, now, Stance.Advance);
            }
            else if (f.HasOrder) Drive(f, state, AddNone, PoseStand, now, false);
            if (now >= u.NextSpeed) { u.NextSpeed = now + 1f; MercSpeed(f, u); }
        }

        static float RouteDistance(MercOrder o, Vector3 p)
        {
            int n = o.Points.Length;
            if (n == 0) return 0f;
            if (n == 1) return Flat(o.Points[0] - p);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                best = Mathf.Min(best, SegmentDistance(p, o.Points[i], o.Points[(i + 1) % n]));
            return best;
        }

        static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = FlatV(b - a), ap = FlatV(p - a);
            float len = ab.sqrMagnitude;
            float t = len < 0.01f ? 0f : Mathf.Clamp01(Vector3.Dot(ap, ab) / len);
            return (ap - ab * t).magnitude;
        }

        /// <summary>W, F8 "Bring my mercs to me": one merc put down at a spot;
        /// his cover claim and fight state start afresh there.</summary>
        internal static bool MercTeleport(MercUnit u, Vector3 spot)
        {
            for (int i = 0; i < _squads.Count; i++)
            {
                Squad s = _squads[i];
                if (s.Merc != u || s.Men.Count == 0) continue;
                Fighter f = s.Men[0];
                if (f.Tr == null) return false;
                MercCoverService.Forget(u);
                MercWarp(f, spot);
                u.NextWarp = Time.time + 10f;
                return true;
            }
            return false;
        }

        /// <summary>A lost merc is put down beside his owner.</summary>
        static void MercWarp(Fighter f, Vector3 spot)
        {
            NavMeshAgent agent = Agent(f);
            if (agent != null && agent.isActiveAndEnabled) agent.Warp(spot);
            else f.Tr.position = spot;
            f.HasOrder = false;
        }

        /// <summary>fast: the agent's speed, re-applied since the vanilla state
        /// machine rewrites it per state.</summary>
        static void MercSpeed(Fighter f, MercUnit u)
        {
            if (u.Fast <= 0) return;
            NavMeshAgent agent = Agent(f);
            if (agent == null || !agent.isActiveAndEnabled) return;
            float want = u.BaseSpeed(agent.speed) * (1f + u.Fast / 100f);
            if (Mathf.Abs(agent.speed - want) > 0.05f) agent.speed = want;
            u.LastSpeedSet = want;
        }

        // ----------------------------------------------------------- players

        static MethodInfo _mSetKillTarget, _mClearKillTarget;
        static bool _killLooked;

        /// <summary>The player a merc should fight, or null; also sets or
        /// clears his vanilla kill target so the game's own shot follows.</summary>
        static Component MercPlayerTarget(Fighter f, float range, float now)
        {
            MercUnit u = f.Squad.Merc;
            if (now < u.NextPlayerScan) return u.PlayerTarget;
            u.NextPlayerScan = now + (0.5f + UnityEngine.Random.value * 0.2f) * (u.Alert ? 0.4f : 1f);
            Transform best = null;
            float bestSqr = range * range;
            if (MercMayEngage(u, now))
            {
                List<GameObject> players = Crocodile.Players();
                Vector3 p = f.Tr.position;
                bool defendOnly = u.Peaceful;
                for (int i = 0; i < players.Count; i++)
                {
                    GameObject go = players[i];
                    if (go == null || go == u.OwnerGo) continue;
                    float d = (go.transform.position - p).sqrMagnitude;
                    if (d >= bestSqr) continue;
                    int actor = Mercs.ActorOf(go);
                    if (actor == Mercs.LocalActor) continue;
                    // B3d (D10): whoever just shot him is answered, whatever
                    // his faction - a merc is no free kill for a friendly side.
                    bool attacker = actor == u.LastAttacker && now < u.AttackerUntil;
                    if (defendOnly && !attacker) continue;
                    if (Mercs.PlayerDead(go)) continue;
                    string steam = Mercs.SteamOf(go);
                    if (steam != null && Mercs.Whitelisted(steam)) continue;
                    int faction = Mercs.FactionOf(go);
                    if (!attacker && (faction < 0 || !HatedValue(f.Hated, faction))) continue;
                    bestSqr = d; best = go.transform;
                }
            }
            u.PlayerTarget = best;
            SetMercKillTarget(f, u, best == null ? null : best.gameObject);
            return best;
        }

        static bool HatedValue(Array hated, int value)
        {
            if (hated == null) return false;
            // W Perf1: no box per element for an int-backed enum array (NpcWar.Hostile).
            int[] ids = hated as int[];
            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] == value) return true;
                return false;
            }
            for (int i = 0; i < hated.Length; i++)
            {
                object h = hated.GetValue(i);
                if (h != null && Convert.ToInt32(h) == value) return true;
            }
            return false;
        }

        static void SetMercKillTarget(Fighter f, MercUnit u, GameObject player)
        {
            if (u.KillTargetSet == player) return;
            if (!_killLooked)
            {
                _killLooked = true;
                _mSetKillTarget = AccessTools.Method(_npcType, "SetKillTarget", null, null);
                _mClearKillTarget = AccessTools.Method(_npcType, "ClearKillTarget", null, null);
            }
            try
            {
                if (player == null)
                {
                    if (_mClearKillTarget != null && _mClearKillTarget.GetParameters().Length == 0)
                        _mClearKillTarget.Invoke(f.Ai, null);
                    else if (_fKillTarget != null) _fKillTarget.SetValue(f.Ai, null);
                }
                else if (_mSetKillTarget != null)
                {
                    Type want = _mSetKillTarget.GetParameters()[0].ParameterType;
                    object arg = want.IsAssignableFrom(typeof(GameObject)) ? (object)player
                        : want.IsAssignableFrom(typeof(Transform)) ? (object)player.transform
                        : player.GetComponent(want);
                    Mercs.OwnKillTarget = true;
                    try { _mSetKillTarget.Invoke(f.Ai, new object[] { arg }); }
                    finally { Mercs.OwnKillTarget = false; }
                }
                u.KillTargetSet = player;
            }
            catch (Exception ex)
            {
                u.KillTargetSet = player;
                if (CfgDebug.Value) RevivalPlugin.L.LogWarning("NpcWar: merc kill target - " + ex.Message);
            }
        }

        // ------------------------------------------------------ remote hits

        static MethodInfo _mRpc, _mOwnerPlayer;
        static bool _rpcLooked;

        /// <summary>The round of an NPC shooter on an NPC another client owns:
        /// NPC_AI2's own "ApplyDamage" RPC to that owner, the same message a
        /// player's FireOneShot sends (CONFIRMED IL IL_04A4..IL_050A), with
        /// part 0 / type 0 / attacker 0 like the local Turret.TryDamage path.
        /// The receiving client's KillStatsPrefix covers attacker 0.</summary>
        internal static bool RemoteHit(Component ai, float damage, Vector3 point)
        {
            if (ai == null || damage <= 0f) return false;
            try
            {
                if (!_rpcLooked)
                {
                    _rpcLooked = true;
                    _mOwnerPlayer = AccessTools.Method(_npcType, "GetPhotonPlayer", Type.EmptyTypes, null);
                    Type viewType = RevivalPlugin.TypeByName("PhotonView");
                    if (viewType != null)
                        foreach (MethodInfo m in viewType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.Name != "RPC") continue;
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length == 3 && ps[0].ParameterType == typeof(string)
                                && ps[1].ParameterType.Name == "PhotonPlayer"
                                && ps[2].ParameterType == typeof(object[])) { _mRpc = m; break; }
                        }
                    if (_mOwnerPlayer == null || _mRpc == null)
                        RevivalPlugin.L.LogWarning("NpcWar: remote NPC hit road incomplete (GetPhotonPlayer "
                            + (_mOwnerPlayer != null) + ", RPC " + (_mRpc != null) + ").");
                }
                if (_mOwnerPlayer == null || _mRpc == null || _mPhotonView == null) return false;
                object owner = _mOwnerPlayer.Invoke(ai, null);
                object view = _mPhotonView.Invoke(ai, null);
                if (owner == null || view == null) return false;
                object[] args = new object[] { damage, 0, 0, 0, point };
                _mRpc.Invoke(view, new object[] { "ApplyDamage", owner, args });
                return true;
            }
            catch (Exception ex)
            {
                if (CfgDebug.Value) RevivalPlugin.L.LogWarning("NpcWar: remote hit - " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------ master

        /// <summary>Master: list other players' mercs as Watch squads, so the
        /// master's squads and defenders can see and fight them. Every 2 s,
        /// and each NPC's spawn key is read once.</summary>
        static void WatchRemoteMercs(float now)
        {
            if (now < _nextWatch) return;
            _nextWatch = now + 2f;
            for (int i = 0; i < _scene.Count; i++)
            {
                Component ai = _scene[i];
                if (ai == null) continue;
                int id = ai.GetInstanceID();
                if (_notMerc.Contains(id)) continue;
                string key = Crew.GroundKey(ai);
                if (key == null || !key.StartsWith(Mercs.KeyPrefix, StringComparison.Ordinal))
                { if (_notMerc.Count < 4096) _notMerc.Add(id); continue; }
                if (IsMine(ai) || FighterOf(ai) != null || !Alive(ai)) continue;
                Squad s = new Squad();
                s.Tag = "merc-watch:" + key; s.Settlement = ai.gameObject; s.Lz = ai.transform.position;
                s.GroundGroup = true; s.GroundRadius = 10f; s.GroundDuty = GroundMode.Waiting;
                s.Centre = s.Lz; s.Front = ai.transform.forward; s.Watch = true;
                Fighter f = NewFighter(ai, s);
                s.Men.Add(f);
                _squads.Add(s);
                _watchCount++;
            }
        }

        /// <summary>Master: a settlement NPC of ours just took a round with no
        /// player behind it. If a hostile remote merc stands within reach, the
        /// NPC (and his comrades) defend against him like against a squad.</summary>
        internal static void MercHitOnMaster(Component hit)
        {
            if (_watchCount == 0 || hit == null || !LookUp() || !IsMaster()) return;
            try
            {
                if (!IsMine(hit) || FighterOf(hit) != null || !Targetable(hit)) return;
                object faction = FactionOf(hit);
                Array hated = GetHated(hit);
                Vector3 p = hit.transform.position;
                for (int q = 0; q < _squads.Count; q++)
                {
                    Squad s = _squads[q];
                    if (!s.Watch || s.Men.Count == 0 || s.Men[0].Tr == null) continue;
                    if ((s.Men[0].Tr.position - p).sqrMagnitude > 250f * 250f) continue;
                    if (!Hostile(s.Men[0].Hated, faction) && !Hostile(hated, s.Men[0].Faction)) continue;
                    Enlist(hit);
                    return;
                }
            }
            catch { }
        }

        // ----------------------------------------------------------- helpers

        /// <summary>Health left, 0..1 (1 when unreadable).</summary>
        internal static float MercHealth(Component ai)
        {
            if (ai == null || !LookUp()) return 0f;
            Fighter f = new Fighter();
            f.Ai = ai;
            return HealthFraction(f);
        }

        /// <summary>B3d: an unreadable NPC type is not a death - "died" is
        /// permanent on the server, so only a read that says dead counts.</summary>
        internal static bool MercAlive(Component ai)
        {
            if (ai == null) return false;
            return !LookUp() || Alive(ai);
        }

        /// <summary>A merc's armour share as the NpcWar rule computes it, for
        /// the hire card's protection bar: 0 = no protection.</summary>
        internal static float MercProtection(RevivalComposition.CrewMan spec)
        {
            try { return Mathf.Clamp01(1f - ArmorScale(spec)); }
            catch { return 0f; }
        }

        /// <summary>Did he have something to shoot at in the last seconds?</summary>
        internal static bool MercInCombat(MercUnit u, float seconds)
        {
            // B3c: on a vehicle gun the fight is the gun's.
            if (u.Ride.Carrier != null)
                return u.Ride.Gunner && u.Ride.LastContact > 0f && Time.time - u.Ride.LastContact < seconds;
            for (int q = 0; q < _squads.Count; q++)
            {
                Squad s = _squads[q];
                if (s.Merc != u || s.Men.Count == 0) continue;
                Fighter f = s.Men[0];
                return f.Target != null && Time.time - f.LastSeen < seconds;
            }
            return false;
        }

        /// <summary>Owner changed faction: the merc's MainOptions follow.</summary>
        internal static void MercFaction(MercUnit u, string side)
        {
            if (u == null || u.Ai == null || !LookUp() || _fMainOptions == null) return;
            object options = Fraktion.Optionen(side);
            if (options == null) return;
            try
            {
                _fMainOptions.SetValue(u.Ai, options);
                for (int q = 0; q < _squads.Count; q++)
                    if (_squads[q].Merc == u)
                        for (int i = 0; i < _squads[q].Men.Count; i++)
                        {
                            Fighter f = _squads[q].Men[i];
                            f.Faction = FactionOf(f.Ai);
                            f.Hated = GetHated(f.Ai);
                        }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Mercs: faction rewrite failed - " + ex.Message);
            }
        }
    }
}

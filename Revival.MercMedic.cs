// Z K7b: extra medical duty; profile equipment and normal fight service remain.
using System;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercMedicRoute
    {
        internal Vector3 Goal;
        internal bool Have;
        internal float NextCover, FireUntil, NextFire;
        internal bool EmptyNotice;
    }

    internal static partial class Mercs
    {
        static bool _medicServer;
        static void MedicAnswer(string[] lines)
        {
            _medicServer = false;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i] == "medic=1") _medicServer = true;
                if (lines[i].StartsWith("aid=", StringComparison.Ordinal))
                {
                    string[] a = lines[i].Substring(4).Split('|'); int patient, token;
                    if (a.Length == 5 && int.TryParse(a[0], out patient) && int.TryParse(a[4], out token))
                    {
                        Record row = Find(patient);
                        if (row != null && !row.Aid.Pending && !row.Aid.Active && token > row.Aid.Token) row.Aid.Token = token;
                    }
                }
                if (!lines[i].StartsWith("duty=", StringComparison.Ordinal)) continue;
                string[] c = lines[i].Substring(5).Split('|'); int id;
                if (c.Length != 2 || !int.TryParse(c[0], out id)) continue;
                Record r = Find(id);
                if (r != null && !r.Session && !r.MedicPending) r.Medic = c[1] == "1";
            }
        }
        internal static bool CanMedic(Record r)
        { return r != null && !r.Dead && !r.Deserted && !r.MedicPending && (r.Session || (_medicServer && _support == 1)); }
        internal static string MedicLabel(Record r)
        { return r != null && r.Medic ? Loc.T("Санитар: ВКЛ", "Medic: ON") : Loc.T("Санитар: ВЫКЛ", "Medic: OFF"); }
        internal static void ToggleMedics()
        {
            System.Collections.Generic.List<Record> rows = Selection();
            if (rows.Count == 0) return;
            bool on = !rows[0].Medic;
            for (int i = 0; i < rows.Count; i++) SetMedic(rows[i], on);
        }
        internal static void SetMedic(Record r, bool on)
        {
            if (!CanMedic(r)) return;
            if (r.Session) { r.Medic = on; MedicChanged(r); return; }
            bool previous = r.Medic;
            r.Medic = on; MedicChanged(r); // local duty/movement responds to the click now
            r.MedicPending = true;
            Enqueue("medic", "id=" + N(r.Id) + "\non=" + (on ? "1" : "0") + "\n", delegate(string result)
            {
                r.MedicPending = false;
                if (result != "ok")
                {
                    r.Medic = previous; MedicChanged(r);
                    MercUi.OrderReply(r.Name + Loc.T(": роль санитара не подтверждена.", ": medic duty unconfirmed."), true);
                }
            }, 0f);
        }
        static void MedicChanged(Record r)
        {
            if (r.Unit != null)
            {
                RescueCancelFor(r.Unit, Time.time);
                r.Unit.NextRescue = Time.time; r.Unit.NextOrder = 0f;
                r.Unit.MedicRoute.Have = false; r.Unit.Halt.Has = false;
                r.Unit.Fight.Overwatch.Role = r.Medic ? MercRole.Assault : r.Unit.WeaponRole;
            }
            MercUi.OrderReply(r.Name + ": " + MedicLabel(r), false);
        }
        internal static bool IsMedic(MercUnit u)
        { Record r = RecordOf(u); return r != null && r.Medic && (r.Session || _medicServer); }
        static void MedicRole(MercUnit u, Record r)
        { u.Fight.Overwatch.Role = r.Medic && (r.Session || _medicServer) ? MercRole.Assault : u.WeaponRole; }
        internal static bool MedicPatient(MercUnit u, float now)
        {
            Record r = RecordOf(u);
            if (r == null || (!r.Aid.Active && !r.Aid.Pending)) return false;
            if (!MedicCalm(u, now)) { MedicCancel(r); return false; }
            return true;
        }

        // Runs at the inherited 4 Hz roster cadence; roster is capped at ten.
        static void MedicTick(float now)
        {
            FrameProf.S(FrameProf.S_MercMedicT);
            try
            {
                for (int i = 0; i < _roster.Count; i++)
                {
                    Record h = _roster[i]; MercUnit u = h.Unit;
                    if (!h.Medic || (!h.Session && !_medicServer) || u == null || u.Ai == null) continue;
                    u.Fight.Overwatch.Role = MercRole.Assault;
                    if (!MercMedicPolicy.MayApproach(!h.Dead && !h.Down.Down, u.Ride.Carrier != null || u.Ride.Boarding != null,
                        u.Deserting, now, u.NextRescue, u.Medicine.Known)) continue;
                    if (u.Medicine.Next == 0)
                    {
                        if (!u.MedicRoute.EmptyNotice)
                        { u.MedicRoute.EmptyNotice = true; MercUi.OrderReply(h.Name + Loc.T(": аптечки закончились.", ": no medkits left."), true); }
                        continue;
                    }
                    u.MedicRoute.EmptyNotice = false;
                    Record best = null; float bestDist = 0f;
                    for (int j = 0; j < _roster.Count; j++)
                    {
                        Record r = _roster[j];
                        if (r == h || r.Dead || r.Deserted || r.Unit == null || r.Unit.Ai == null || (!r.Session && h.Session)
                            || r.Down.Pending || r.Down.Healing || r.Aid.Pending || r.Aid.Active) continue;
                        if (r.Down.Down ? !r.DownConfirmed : (r.Hp <= 0f || r.Hp >= .8f || !MedicCalm(u, now) || !MedicCalm(r.Unit, now))) continue;
                        bool taken = false;
                        for (int k = 0; k < _roster.Count; k++)
                            if (_roster[k].Unit != null && _roster[k].Unit != u && _roster[k].Unit.RescueTarget == r) { taken = true; break; }
                        if (taken) continue;
                        float distance = Vector3.Distance(u.Ai.transform.position, r.Unit.Ai.transform.position);
                        if (r.Down.Down && !MercMedicPolicy.Reachable(now, r.Down.Until, distance)) continue;
                        if (MercMedicPolicy.Better(r.Down.Down, r.Down.Until, distance, r.Id,
                            best != null && best.Down.Down, best == null ? 0f : best.Down.Until, bestDist, best == null ? 0 : best.Id))
                        { best = r; bestDist = distance; }
                    }
                    if (best == null || best == u.RescueTarget) continue;
                    // An earlier bleed-out can supersede an approach, never an opened kit.
                    if (u.RescueTarget != null)
                    {
                        Record old = u.RescueTarget;
                        if (old.Down.Healing || old.Down.Pending || old.Aid.Active || old.Aid.Pending) continue;
                        CancelRescue(old);
                    }
                    MedicineCancel(u, now); MercAA.Release(u);
                    if (!best.Down.Down) { MedicineCancel(best.Unit, now); best.Unit.NextSelfHeal = now + 30f; }
                    u.RescueTarget = best; u.NextOrder = 0f; u.MedicRoute.Have = false;
                    if (u.Fight.Brain != null) u.Fight.Brain.Leave(MercCoverService.Field);
                    MercUi.OrderReply(h.Name + Loc.T(": санитар идёт к ", ": medic moving to ") + best.Name + ", "
                        + ((int)(bestDist / 2.8f + .5f)).ToString() + " m", false);
                }
                for (int i = 0; i < _roster.Count; i++)
                {
                    Record r = _roster[i];
                    if (!r.Aid.Active && !r.Aid.Pending) continue;
                    MercUnit helper = UnitFor(r.Aid.Healer);
                    if (r.Dead || r.Down.Down || helper == null || r.Unit == null || r.Unit.Ai == null
                        || !MedicCalm(helper, now) || !MedicCalm(r.Unit, now)
                        || Vector3.Distance(helper.Ai.transform.position, r.Unit.Ai.transform.position) > MercDownState.Reach)
                    { MedicCancel(r); continue; }
                    if (r.Aid.Ready(now)) MedicFinish(r);
                }
            }
            finally { FrameProf.E(FrameProf.S_MercMedicT); }
        }
        internal static bool MedicCalm(MercUnit u, float now)
        {
            Vector3 danger;
            return u != null && u.Ai != null && !IsDown(u) && !u.Deserting && u.Ride.Carrier == null && u.Ride.Boarding == null
                && MercMedicPolicy.Calm(u.Fight.Sees, u.Sense.Count > 0 && u.Sense.Exposed,
                    u.Sense.Count == 0 || now - u.Sense.ExposedAt < 1.5f, u.Fight.Health, now, u.LastHit.At,
                    u.Fight.In.Suppression, MercDanger.Near(u.Ai.transform.position, 20f, now, out danger));
        }
        internal static void MedicTreat(MercUnit helper, Record r, float now)
        {
            Record medic = RecordOf(helper);
            if (medic == null || medic.MedicPending) return; // stock waits for master role confirmation
            if (r.Aid.Active || r.Aid.Pending || !MedicCalm(helper, now) || !MedicCalm(r.Unit, now)) return;
            int item = helper.Medicine.Next;
            if (!helper.Medicine.Begin(now)) return;
            helper.Medicine.Cancel(); _nextState = 0f;
            r.Aid.Healer = helper.Id; r.Aid.Item = item; r.Aid.Pending = true;
            int ticket = ++r.Aid.Token;
            Action<string> start = delegate(string result)
            {
                if (ticket != r.Aid.Token) return;
                r.Aid.Pending = false;
                if (result != "ok" || r.Dead || r.Down.Down || helper.Ai == null || !MedicCalm(helper, Time.time)) { MedicCancel(r); return; }
                r.Aid.Active = true; r.Aid.Started = Time.time;
                MercMedPose.Start(helper.Ai, item, Time.time);
            };
            if (r.Session) start("ok");
            else Enqueue("medic-start", "id=" + N(r.Id) + "\nhealer=" + N(helper.Id) + "\nitem=" + N(item) + "\ntoken=" + N(ticket) + "\n", start, 0f);
        }
        static void MedicFinish(Record r)
        {
            r.Aid.Pending = true; int ticket = r.Aid.Token;
            Action<string> finish = delegate(string result)
            {
                if (ticket != r.Aid.Token) return;
                r.Aid.Pending = false;
                MercUnit helper = UnitFor(r.Aid.Healer);
                if (result != "ok" || r.Dead || r.Down.Down || helper == null || !MedicCalm(helper, Time.time)
                    || r.Unit == null || r.Unit.Ai == null || !MedicCalm(r.Unit, Time.time)
                    || Vector3.Distance(helper.Ai.transform.position, r.Unit.Ai.transform.position) > MercDownState.Reach)
                { MedicCancel(r); return; }
                float hp = NpcWar.MercHealth(r.Unit.Ai);
                if (hp > 0f)
                {
                    hp = Mathf.Min(1f, hp + MercMedicine.Share(r.Aid.Item) * 100f / r.Unit.MaxHealth);
                    SetHealth(r.Unit.Ai, hp * r.Unit.MaxHealth); r.Hp = hp;
                    r.Unit.Fight.Health = hp; r.Unit.Fight.NextHealth = 0f; _nextState = 0f;
                }
                MedicCancel(r);
            };
            if (r.Session) finish("ok");
            else Enqueue("medic-finish", "id=" + N(r.Id) + "\ntoken=" + N(ticket) + "\n", finish, 0f);
        }
        static void MedicCancel(Record r)
        {
            if ((r.Aid.Active || r.Aid.Pending) && !r.Session)
                Enqueue("medic-cancel", "id=" + N(r.Id) + "\ntoken=" + N(r.Aid.Token) + "\n", null, 0f);
            r.Aid.Cancel();
            for (int i = 0; i < _roster.Count; i++)
            {
                MercUnit u = _roster[i].Unit;
                if (u != null && u.RescueTarget == r)
                { u.RescueTarget = null; u.MedicRoute.Have = false; MercMedPose.Stop(u.Ai); u.NextSelfHeal = Time.time + 1f; }
            }
        }
    }

    public static partial class NpcWar
    {
        static Vector3 MercMedicWaypoint(Fighter f, MercUnit u, Vector3 at, float now)
        {
            MercMedicRoute route = u.MedicRoute; Vector3 me = f.Tr.position;
            if (route.Have && Flat(route.Goal - me) > 2f && now < route.NextCover) return route.Goal;
            Vector3 hop = MercAssault.HopTo(me, at);
            if (u.Sense.Count > 0 && now >= route.NextCover && MercCoverService.MayQuery())
            {
                route.NextCover = now + .5f; CoverPick pick;
                if (MercCoverService.Best(hop, u.Sense.At, u.Sense.Weight, u.Sense.Count, MercAssault.Hop,
                    me, MercAssault.Hop + 2f, u.Id, out pick) && pick.Confirmed
                    && Flat(pick.Point.Pos - at) + 2f < Flat(me - at)
                    && !MercTeam.Board.Crowded(pick.Point.Pos, u.Id, MercSquad.Spread, now))
                {
                    hop = pick.Point.Pos;
                    MercCoverService.Field.Claim(u.Id, hop, now + 2f, now);
                }
            }
            route.Have = true; route.Goal = hop; return hop;
        }
        static bool MercMedicStep(Fighter f, MercUnit u, Mercs.Record r, float now)
        {
            if (r.Dead || r.Unit == null || r.Unit.Ai == null || Mercs.IsDown(u) || u.Deserting)
            { Mercs.RescueCancelFor(u, now); return false; }
            if (!r.Down.Down && (!Mercs.MedicCalm(u, now) || !Mercs.MedicCalm(r.Unit, now)))
            { Mercs.RescueCancelFor(u, now); return false; }
            if (now >= u.Fight.NextHealth)
            { u.Fight.NextHealth = now + .5f; u.Fight.Health = HealthFraction(f); }
            u.Fight.In.Suppression = f.Suppression;
            Vector3 danger;
            if (u.Fight.Health < .35f || MercDanger.Near(f.Tr.position, 20f, now, out danger) || Reloading(f))
            { return MercFight(f, u, now); } // survival shapes the route; retain the patient
            Vector3 at = r.Unit.Ai.transform.position;
            if ((f.Tr.position - at).sqrMagnitude > MercDownState.Reach * MercDownState.Reach)
            {
                // Brief normal weapon bursts interleaved with forward hops; contact
                // cannot erase the mission or pin the medic indefinitely.
                MercMedicRoute route = u.MedicRoute;
                if (f.Sees && f.Target != null && MercMayEngage(u, now) && Flat(f.Target.position - f.Tr.position) <= RangeOf(f))
                {
                    if (now >= route.NextFire) { route.NextFire = now + 2.5f; route.FireUntil = now + .8f; }
                    if (now < route.FireUntil && !MercFriendInLine(f, f.Tr.position, f.Target.position))
                    {
                        if (f.Manpads != null) MercStingerFight(f, u, now); else Fire(f, now);
                        return true;
                    }
                    // Restore movement now when a live friendly blocks a native
                    // player-target burst, rather than leaving Shooting active.
                    u.NextOrder = 0f;
                }
                MercMove(f, u, MercMedicWaypoint(f, u, at, now), true, now);
            }
            else if ((r.Down.Down && Mercs.RescueSafe(u, now)) || (!r.Down.Down && Mercs.MedicCalm(u, now)))
            {
                Hold(f, null, now); Drive(f, MainIdle, AddNone, PoseCrouch, now, true);
                FaceDir(f, at - f.Tr.position);
                if (r.Down.Down) Mercs.TryMercRevive(u, r, now); else Mercs.MedicTreat(u, r, now);
            }
            else if (!MercFight(f, u, now))
            { MercMove(f, u, MercMedicWaypoint(f, u, at, now), true, now); }
            return true;
        }
    }
}

// Z K8: owner-local orders; native movement/stance/weapon replication remains.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercRaidState
    {
        internal float Until;
        internal MercOrder Previous, Cover;
    }

    internal static partial class Mercs
    {
        static readonly MercRaidClock RaidClock = new MercRaidClock();
        static readonly List<Record> RaidOne = new List<Record>(1);
        static string _raidScene;

        internal static void RaidWarning(Vector3 at, float seconds)
        {
            RaidScene();
            RaidClock.Warn(at, Time.time, seconds);
            RaidTick(Time.time);
        }

        static void RaidScene()
        {
            if (_raidScene == MapScene.Current) return;
            _raidScene = MapScene.Current; RaidClock.Clear();
            for (int i = 0; i < _roster.Count; i++)
            {
                _roster[i].Raid.Until = 0f;
                _roster[i].Raid.Previous = null; _roster[i].Raid.Cover = null;
                if (_roster[i].Unit != null) _roster[i].Unit.RaidCover = false;
            }
        }

        static float RaidUntil(Record r, float now)
        {
            Vector3 at = r.Unit == null || r.Unit.Ai == null ? OwnerPosition : r.Unit.Ai.transform.position;
            return RaidClock.Until(at, now);
        }

        static void RaidTick(float now)
        {
            FrameProf.S(FrameProf.S_MercRaidT);
            try
            {
                RaidScene();
                if (_roster.Count == 0) return;
                // SirenOn comes from the master's existing radar snapshot.
                // Sound preferences do not disable the defensive default.
                if (TowerRadar.On && TowerRadar.Built && TowerRadar.SirenOn)
                    RaidClock.Warn(TowerRadar.TowerPoint(Vector3.zero), now, 2f);
                for (int i = 0; i < _roster.Count; i++)
                {
                    Record r = _roster[i]; MercRaidState state = r.Raid;
                    if (r.Dead || r.Deserted || (r.Unit != null && r.Unit.Deserting))
                    { state.Until = 0f; state.Previous = null; state.Cover = null; continue; }
                    float until = RaidUntil(r, now);
                    if (until > now)
                    {
                        bool start = state.Until <= now;
                        state.Until = Mathf.Max(state.Until, until);
                        if (start && !MercAA.IsOrder(r.Order) && !r.Order.Survive)
                            RaidShelter(r, true);
                    }
                    else if (state.Until > 0f && now >= state.Until)
                    {
                        // Never undo a newer player command or owner-death order.
                        MercOrder resume = state.Cover == r.Order ? state.Previous : null;
                        state.Until = 0f; state.Previous = null; state.Cover = null;
                        if (r.Unit != null) r.Unit.RaidCover = false;
                        if (resume != null)
                        {
                            RaidRestore(r, resume);
                        }
                    }
                }
            }
            finally { FrameProf.E(FrameProf.S_MercRaidT); }
        }

        // Called by the common acknowledgement boundary, including station
        // commands. It suppresses defaults during this alarm, not future raids.
        static void RaidReceived(Record r)
        {
            r.Raid.Until = Mathf.Max(r.Raid.Until, RaidUntil(r, Time.time));
            if (r.Unit != null) r.Unit.RaidCover = r.Order == r.Raid.Cover;
        }

        static void RaidRestore(Record r, MercOrder resume)
        {
            // Preserve the exact prior formation slot and selected objective.
            r.Order = resume;
            MercUnit u = r.Unit;
            if (u != null)
            {
                MercAA.Release(u); u.Order = resume; u.Rally = false;
                u.NextOrder = 0f; u.Chasing = false; u.Attack.Reset();
                u.Sense.NextPick = 0f; u.Sense.PickAt = -1000f;
                if (u.Fight.Brain != null) u.Fight.Brain.Leave(MercCoverService.Field);
                u.Fight.Out = default(FightOut);
            }
            OrderReceived(r, false, resume.Mode == MercOrder.Follow || resume.Mode == MercOrder.Vehicle ? OwnerPosition : resume.Centre);
            RaidOne.Clear(); RaidOne.Add(r); SendOrders(RaidOne);
        }

        static void RaidShelter(Record r, bool temporary)
        {
            MercOrder previous = r.Raid.Cover == r.Order ? r.Raid.Previous : r.Order;
            MercOrder cover = new MercOrder(); cover.Mode = MercOrder.Stay; cover.Survive = true;
            Vector3 at = r.Unit == null || r.Unit.Ai == null ? OwnerPosition : r.Unit.Ai.transform.position;
            cover.Points = new Vector3[] { at };
            cover.Facing = r.Unit == null || r.Unit.Ai == null ? Vector3.forward : r.Unit.Ai.transform.forward;
            RaidOne.Clear(); RaidOne.Add(r); Give(RaidOne, cover);
            r.Raid.Previous = temporary ? previous : null;
            r.Raid.Cover = r.Order;
            if (r.Unit != null)
            {
                r.Unit.RaidCover = true; r.Unit.Rally = false;
                r.Unit.RaidRoof = new CoverPick(); r.Unit.RaidProbe = 0;
                if (r.Unit.Fight.Brain != null) r.Unit.Fight.Brain.Leave(MercCoverService.Field);
                r.Unit.Fight.Out = default(FightOut);
                r.Unit.Approach = at + cover.Facing * 280f;
                r.Unit.Ride.Boarding = null;
            }
        }

        internal static void OrderRaidCover()
        {
            List<Record> sel = Selection();
            for (int i = 0; i < sel.Count; i++)
            {
                Record r = sel[i];
                RaidShelter(r, r.Raid.Until > Time.time || RaidUntil(r, Time.time) > Time.time);
            }
        }

        // Fill firing places before loaders: one merc per available AA gun.
        // Existing leases validate body owner, faction, health and proximity.
        internal static void OrderRaidGuns()
        {
            List<Record> sel = StationSelection(false);
            for (int i = 0; i < sel.Count; i++)
            {
                Record r = sel[i];
                if (r.Order.Mode == MercOrder.ManGun && r.Order.Facing.x > 0f && r.Order.Facing.x < 8f)
                { OrderReceived(r, false, r.Order.Centre); continue; }
                Vector3 from = r.Unit == null || r.Unit.Ai == null ? OwnerPosition : r.Unit.Ai.transform.position;
                int best = -1; float distance = 420f * 420f; Vector3 place = Vector3.zero;
                for (int post = 0; post < 7; post++)
                {
                    if (post == MercAA.Radar || Flak.ByIndex(post) == null) continue;
                    Vector3 at; Quaternion rot;
                    if (!MercAA.Pose(post, out at, out rot) || !MercAA.CanApproach(post, r.Unit == null ? null : r.Unit.Ai)) continue;
                    bool reserved = false;
                    for (int n = 0; n < _roster.Count; n++)
                        if (!_roster[n].Dead && !_roster[n].Deserted && MercAA.IsOrder(_roster[n].Order)
                            && _roster[n].Order.Mode == MercOrder.ManGun && _roster[n].Order.Facing.x == post + 1)
                        { reserved = true; break; }
                    float d = (at - from).sqrMagnitude;
                    if (!reserved && d < distance) { best = post; distance = d; place = at; }
                }
                if (best < 0)
                {
                    RaidReceived(r);
                    MercUi.OrderReply(r.Name + Loc.T(": нет свободной зенитки в радиусе 150 м; прежний приказ", ": no free AA gun within 150 m; keeping order"), true);
                    continue;
                }
                MercStation seat = new MercStation(); seat.Post = best; seat.At = place;
                GiveStation(r, seat, false);
            }
            SaveStationOrders(sel);
        }
    }

    internal static class MercRaidCover
    {
        static float _nextQuery, _lastNow;
        static readonly MercUnit[] Pending = new MercUnit[10];
        static readonly float[] AskedAt = new float[10];
        static int _pending;

        static void Remove(int index)
        {
            for (int i = index; i < _pending - 1; i++)
            { Pending[i] = Pending[i + 1]; AskedAt[i] = AskedAt[i + 1]; }
            Pending[--_pending] = null;
        }

        static bool Turn(MercUnit u, float now)
        {
            if (now < _lastNow) { while (_pending > 0) Remove(0); _nextQuery = 0f; }
            _lastNow = now;
            for (int i = _pending - 1; i >= 0; i--)
                if (!Pending[i].RaidCover || Pending[i].Ai == null || now - AskedAt[i] > 2f) Remove(i);
            int slot = -1;
            for (int i = 0; i < _pending; i++) if (Pending[i] == u) { slot = i; break; }
            if (slot < 0 && _pending < Pending.Length) { slot = _pending++; Pending[slot] = u; }
            if (slot >= 0) AskedAt[slot] = now;
            if (slot != 0 || now < _nextQuery) return false;
            Remove(0); _nextQuery = now + 0.5f;
            return true;
        }
        internal static bool Sheltered(MercUnit u, Vector3 at, float now)
        {
            return u.RaidCover && u.RaidRoof.Found && now - u.RaidRoofAt < 3f
                && (u.RaidRoof.Point.Pos - at).sqrMagnitude < 3f * 3f;
        }

        internal static bool Pick(MercUnit u, Vector3 from, float now, out CoverPick pick)
        {
            pick = new CoverPick();
            u.RaidProbeDeferred = false;
            if (!u.RaidCover) return false;
            CoverPick cached = u.RaidRoof;
            bool usable = cached.Found && now - u.RaidRoofAt < 3f
                && (cached.Point.Pos - from).sqrMagnitude <= 40f * 40f
                && !MercCoverService.Field.Claimed(cached.Point.Pos, u.Id, now);
            if (usable && now - u.RaidRoofAt < 1.5f) { pick = cached; return true; }
            if (!Turn(u, now))
            {
                u.RaidProbeDeferred = true;
                if (usable) { pick = cached; return true; }
                return false;
            }
            FrameProf.S(FrameProf.S_MercRaidCoverT);
            try
            {
                CoverPick roof = u.RaidRoof;
                if (roof.Found && (roof.Point.Pos - from).sqrMagnitude <= 40f * 40f
                    && !MercCoverService.Field.Claimed(roof.Point.Pos, u.Id, now)
                    && MercRaidShelter.Protected(MercCoverService.World, roof.Point.Pos))
                { pick = roof; u.RaidRoofAt = now; MercCoverService.Field.Claim(u.Id, roof.Point.Pos, now + 6f, now); return true; }
                bool found = MercRaidShelter.Pick(MercCoverService.Field, MercCoverService.World, from, u.Id, now, ref u.RaidProbe, out pick);
                u.RaidRoof = pick;
                if (found) u.RaidRoofAt = now;
                return found;
            }
            finally { FrameProf.E(FrameProf.S_MercRaidCoverT); }
        }
    }
}

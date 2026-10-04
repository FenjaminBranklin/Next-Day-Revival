// A S6: one owner-local defence order, using the existing master seat leases.
// H M2: one nearest-first plan per order. A post stays with its merc until it
// is destroyed or he falls; an enemy crew on it is cleared, any other holder
// is waited out at the post. Duty orders are silent; one summary speaks.
// H M3: extras keep their own order; every step is logged ("MercAD",
// Revival.MercDefenceLog.cs).
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        sealed class DefenceMember
        {
            internal Record Record;
            internal MercOrder Expected;
            internal int Post = -1, Wanted = -1;
            internal bool Moved;
        }

        static readonly List<DefenceMember> Defence = new List<DefenceMember>(10);
        static readonly List<Record> DefenceChanged = new List<Record>(10);
        static readonly List<Record> DefenceOne = new List<Record>(1);
        static readonly MercStation[] DefenceSeats = DefenceMakeSeats();
        static readonly int[] DefencePosts = new int[7];
        static readonly int[] DefencePriority = new int[7];
        static readonly bool[] DefenceTake = new bool[7];
        static readonly StringBuilder DefenceNote = new StringBuilder(160);
        static bool _defenceActive;
        static string _defenceScene;
        static GameObject _defenceOwner;
        static float _defenceAt;

        internal static bool AirDefenceActive { get { return _defenceActive; } }

        static MercStation[] DefenceMakeSeats()
        {
            MercStation[] seats = new MercStation[7];
            for (int i = 0; i < seats.Length; i++)
            { seats[i] = new MercStation(); seats[i].Post = i; }
            return seats;
        }

        // The whole owned squad, or (G O1) only the mercs picked in L. Aiming
        // is irrelevant.
        internal static void ToggleAirDefence()
        {
            if (_defenceActive) { DefenceRelease(); return; }
            GameObject owner = OwnerObject;
            Vector3 delta = OwnerPosition - TowerRadar.TowerPoint(Vector3.zero);
            delta.y = 0f;
            if (owner == null || !TowerRadar.Built || delta.sqrMagnitude > 1680f * 1680f)
            {
                DefenceLogRefused(owner != null, TowerRadar.Built, delta.sqrMagnitude);
                MercUi.OrderReply(Loc.T("Подойдите к аэродрому для занятия ПВО.",
                    "Approach the airfield to man air defence."), true);
                return;
            }
            Defence.Clear();
            bool pick = PickActive();
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead || r.Deserted || r.Unpaid || (pick && !r.Selected)) continue;
                DefenceMember m = new DefenceMember(); m.Record = r; m.Expected = r.Order;
                Defence.Add(m);
            }
            if (Defence.Count == 0)
            { DefenceLogRefused(true, true, -1f); MercUi.OrderReply(Loc.T("Нет оплаченных наёмников для ПВО.", "No paid mercs for air defence."), true); return; }
            _defenceActive = true; _defenceScene = MapScene.Current; _defenceOwner = owner;
            DefenceOne.Clear();
            for (int i = 0; i < Defence.Count; i++) DefenceOne.Add(Defence[i].Record);
            _orderQuiet = true;
            try
            {
                // H M3: an extra keeps his order. Only a man whose order is an
                // air defence post the plan hands out is freed first (FOLLOW).
                DefenceChanged.Clear();
                for (int i = 0; i < Defence.Count; i++)
                {
                    MercOrder o = Defence[i].Record.Order;
                    if (!MercDefenceCore.KeepsOrder(MercAA.IsOrder(o), MercAA.IsVehicle(o), Mathf.RoundToInt(o.Facing.x) - 1))
                        DefenceChanged.Add(Defence[i].Record);
                }
                if (DefenceChanged.Count > 0) Give(DefenceChanged, MercOrder.FollowMe());
                DefenceChanged.Clear();
                for (int i = 0; i < Defence.Count; i++) Defence[i].Expected = Defence[i].Record.Order;
                DefenceReconcile();
            }
            finally { _orderQuiet = false; }
            DefenceLogOrder();
            _defenceAt = Time.time + 0.5f;
            _onDuty.Clear();
            Announce(DefenceSummary(), DefenceOne);
        }

        static bool DefenceUntouched(DefenceMember m)
        {
            Record r = m.Record;
            // An automatic siren shelter for a follower is not a new player order.
            return r.Order == m.Expected || (r.Order == r.Raid.Cover && r.Raid.Previous == m.Expected);
        }

        static void DefenceRelease()
        {
            DefenceOne.Clear();
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                if (!m.Record.Dead && !m.Record.Deserted && DefenceUntouched(m)) DefenceOne.Add(m.Record);
            }
            _defenceActive = false; Defence.Clear();
            _orderQuiet = true;
            try { if (DefenceOne.Count > 0) Give(DefenceOne, MercOrder.FollowMe()); }
            finally { _orderQuiet = false; }
            MercUi.OrderReply(Loc.T("ПВО освобождена; наёмники следуют за вами.", "Air defence released; mercs follow you."), false);
        }

        static void AirDefenceTick(float now)
        {
            if (!_defenceActive || now < _defenceAt) return;
            _defenceAt = now + 0.5f;
            FrameProf.S(FrameProf.S_MercAirfieldT);
            try
            {
                // Session-local automation must not survive a logout/region change.
                if (_defenceScene != MapScene.Current || OwnerObject != _defenceOwner || _defenceOwner == null)
                { _defenceActive = false; Defence.Clear(); return; }
                for (int i = Defence.Count - 1; i >= 0; i--)
                {
                    DefenceMember m = Defence[i];
                    if (!_roster.Contains(m.Record) || m.Record.Dead || m.Record.Deserted || !DefenceUntouched(m))
                        Defence.RemoveAt(i);
                }
                _orderQuiet = true;
                bool moved;
                try { moved = DefenceReconcile(); }
                finally { _orderQuiet = false; }
                if (moved) DefenceMovedNote();
                DefenceLogTick(now, moved);
            }
            finally { FrameProf.E(FrameProf.S_MercAirfieldT); }
        }

        static bool DefenceReady(DefenceMember m)
        {
            Record r = m.Record;
            return !r.Unpaid && !r.Down.Down && !r.Down.Final
                && (r.Unit == null || !r.Unit.Deserting);
        }

        static DefenceMember DefenceFor(Record r)
        {
            for (int i = 0; i < Defence.Count; i++) if (Defence[i].Record == r) return Defence[i];
            return null;
        }

        static DefenceMember DefenceOf(Component ai)
        {
            for (int i = 0; ai != null && i < Defence.Count; i++)
                if (Defence[i].Record.Unit != null && Defence[i].Record.Unit.Ai == ai) return Defence[i];
            return null;
        }

        static DefenceMember DefenceCrew(int post)
        {
            for (int i = 0; i < Defence.Count; i++) if (Defence[i].Wanted == post) return Defence[i];
            return null;
        }

        // Only destruction ends a post; every other holder is temporary.
        static bool DefenceStanding(int post)
        {
            return post == MercAA.Radar ? TowerRadar.Working : AirDefenceDamage.Alive(post);
        }

        // A seat this duty may plan: free, or held by an enemy crew that its
        // merc clears first. Players, friendly crews and foreign leases keep theirs.
        static bool DefenceOpen(int post, Component probe)
        {
            if (!DefenceStanding(post)) return false;
            if (probe != null && MercAA.HostileCrew(post, probe) != null) return true;
            if (!MercAA.AvailableForOrder(post)) return false;
            MercAAPost held = MercAA.Held(post);
            if (held != null && DefenceOf(held.Ai) == null) return false;
            // Respect another local order's reservation before physical arrival.
            for (int i = 0; i < _roster.Count; i++)
            {
                Record other = _roster[i];
                if (other.Dead || other.Deserted || other.Down.Down || DefenceFor(other) != null) continue;
                if (MercStations.Matches(other.Order, DefenceSeats[post])) return false;
            }
            return true;
        }

        static int DefenceDiscover()
        {
            int count = 0;
            for (int i = 0; i < DefencePriority.Length; i++) { DefencePriority[i] = -1; DefenceTake[i] = false; }
            // Registry lookups only: no scene search, rays or player-distance seat limit.
            for (int pass = 0; pass < 3; pass++)
                for (int post = 0; post < DefenceSeats.Length; post++)
                {
                    bool radar = post == MercAA.Radar;
                    Flak.Gun gun = radar ? null : Flak.ByIndex(post);
                    if (pass == 1 ? !radar : radar || gun == null || gun.Town || gun.ShortRange != (pass == 2)) continue;
                    Vector3 at; Quaternion rot;
                    if (!MercAA.Pose(post, out at, out rot)) continue;
                    DefenceSeats[post].At = at; DefencePosts[count] = post; DefencePriority[post] = count++;
                    if (pass == 2) break; // One ZU-23, no loader seats.
                }
            return count;
        }

        static Vector3 DefenceWhere(Record r)
        {
            return r.Unit == null || r.Unit.Ai == null ? OwnerPosition : r.Unit.Ai.transform.position;
        }

        // True when a living, ready merc changed post (for the one summary).
        static bool DefenceReconcile()
        {
            int count = DefenceDiscover();
            Component probe = null; // The squad shares the owner's faction.
            int free = 0;
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                m.Moved = false;
                m.Wanted = m.Post >= 0 && DefencePriority[m.Post] >= 0 && DefenceReady(m)
                    && DefenceStanding(m.Post) ? m.Post : -1;
                if (!DefenceReady(m)) continue;
                if (m.Wanted < 0) free++;
                if (probe == null && m.Record.Unit != null) probe = m.Record.Unit.Ai;
            }
            // 52-K > radar > ZU-23: open posts take the reserve; with none left
            // the crew of the lowest-priority post below moves up.
            for (int n = 0; n < count; n++)
            {
                int post = DefencePosts[n];
                if (DefenceCrew(post) != null || !DefenceOpen(post, probe)) continue;
                if (free > 0) { DefenceTake[post] = true; free--; continue; }
                for (int k = count - 1; k > n; k--)
                {
                    DefenceMember low = DefenceCrew(DefencePosts[k]);
                    if (low == null) continue;
                    low.Wanted = -1; DefenceTake[post] = true; break;
                }
            }
            // Nearest pair first; contract IDs break exact ties.
            for (;;)
            {
                DefenceMember best = null; int seat = -1; float near = float.MaxValue;
                for (int i = 0; i < Defence.Count; i++)
                {
                    DefenceMember m = Defence[i];
                    if (m.Wanted >= 0 || !DefenceReady(m)) continue;
                    Vector3 from = DefenceWhere(m.Record);
                    for (int n = 0; n < count; n++)
                    {
                        int post = DefencePosts[n];
                        if (!DefenceTake[post]) continue;
                        Vector3 d = DefenceSeats[post].At - from; d.y = 0f;
                        float s = d.sqrMagnitude;
                        if (best == null || s < near || (s == near && m.Record.Id < best.Record.Id))
                        { best = m; seat = post; near = s; }
                    }
                }
                if (best == null) break;
                best.Wanted = seat; DefenceTake[seat] = false;
            }
            // Release old claims before assigning replacements, including lower-priority crew.
            bool moved = false;
            DefenceChanged.Clear();
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                if (m.Post == m.Wanted) continue;
                if (m.Record.Unit != null) MercAA.Release(m.Record.Unit);
                if (m.Wanted < 0) DefenceChanged.Add(m.Record);
            }
            if (DefenceChanged.Count > 0) Give(DefenceChanged, MercOrder.FollowMe());
            DefenceChanged.Clear();
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                if (m.Post == m.Wanted) continue;
                if (m.Wanted >= 0)
                { GiveStation(m.Record, DefenceSeats[m.Wanted], m.Wanted == MercAA.Radar); DefenceChanged.Add(m.Record); }
                m.Post = m.Wanted; m.Expected = m.Record.Order;
                m.Moved = DefenceReady(m);
                if (m.Moved) moved = true;
            }
            if (DefenceChanged.Count > 0) SaveStationOrders(DefenceChanged);
            return moved;
        }

        static bool DefenceShort(int post)
        {
            Flak.Gun g = post == MercAA.Radar ? null : Flak.ByIndex(post);
            return g != null && g.ShortRange;
        }

        static string DefencePostName(int post)
        {
            return post == MercAA.Radar ? Loc.T("радар", "radar") : DefenceShort(post) ? "ZU-23" : "52-K";
        }

        static string DefenceSummary()
        {
            int guns = 0, enemy = 0, reserve = 0; bool radar = false, zu = false;
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                if (m.Post < 0) { reserve++; continue; }
                if (m.Post == MercAA.Radar) radar = true;
                else if (DefenceShort(m.Post)) zu = true;
                else guns++;
                if (m.Record.Unit != null && MercAA.HostileCrew(m.Post, m.Record.Unit.Ai) != null) enemy++;
            }
            DefenceNote.Length = 0;
            DefenceNote.Append(Loc.T("ЗАНЯТЬ ПВО - 52-К: ", "MAN AIR DEFENCE - 52-K: ")).Append(guns);
            if (radar) DefenceNote.Append(Loc.T(", радар", ", radar"));
            if (zu) DefenceNote.Append(", ZU-23");
            if (enemy > 0) DefenceNote.Append(Loc.T("; зачистка от врага: ", "; clearing enemy crews: ")).Append(enemy);
            if (reserve > 0) DefenceNote.Append(Loc.T("; за вами: ", "; following you: ")).Append(reserve);
            DefenceNote.Append(Loc.T("; ещё раз - освободить", "; click again to release"));
            return DefenceNote.ToString();
        }

        // One line for every re-crewing of a tick, instead of a reply per merc.
        static void DefenceMovedNote()
        {
            DefenceNote.Length = 0;
            DefenceNote.Append(Loc.T("ПВО: ", "Air defence: "));
            bool first = true;
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                if (!m.Moved) continue;
                if (!first) DefenceNote.Append(", ");
                first = false;
                DefenceNote.Append(m.Record.Name).Append(" - ")
                    .Append(m.Post < 0 ? Loc.T("за вами", "following you") : DefencePostName(m.Post));
            }
            MercUi.Toast(DefenceNote.ToString(), false);
        }

        /// <summary>H M3: a gun this client's defence order has a living crewman
        /// on the way to (the master spawns no native crew onto it).</summary>
        internal static bool AirDefenceWants(int gun)
        {
            if (!_defenceActive) return false;
            for (int i = 0; i < Defence.Count; i++)
                if (Defence[i].Post == gun && DefenceReady(Defence[i])) return true;
            return false;
        }

        internal static bool AirDefenceManaged(MercUnit u)
        {
            if (!_defenceActive) return false;
            for (int i = 0; i < Defence.Count; i++)
                if (Defence[i].Record.Unit == u && DefenceUntouched(Defence[i])) return true;
            return false;
        }
    }
}

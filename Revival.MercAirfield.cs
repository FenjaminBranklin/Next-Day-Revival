// A S6: one owner-local defence order, using the existing master seat leases.
using System.Collections.Generic;
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
        }

        static readonly List<DefenceMember> Defence = new List<DefenceMember>(10);
        static readonly List<Record> DefenceChanged = new List<Record>(10);
        static readonly List<Record> DefenceOne = new List<Record>(1);
        static readonly MercStation[] DefenceSeats = DefenceMakeSeats();
        static readonly int[] DefencePosts = new int[7];
        static readonly int[] DefencePriority = new int[7];
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

        // Always the whole owned squad: selection and aiming are irrelevant.
        internal static void ToggleAirDefence()
        {
            if (_defenceActive) { DefenceRelease(); return; }
            GameObject owner = OwnerObject;
            Vector3 delta = OwnerPosition - TowerRadar.TowerPoint(Vector3.zero);
            delta.y = 0f;
            if (owner == null || !TowerRadar.Built || delta.sqrMagnitude > 1680f * 1680f)
            {
                MercUi.OrderReply(Loc.T("Подойдите к аэродрому для занятия ПВО.",
                    "Approach the airfield to man air defence."), true);
                return;
            }
            Defence.Clear();
            for (int i = 0; i < _roster.Count; i++)
            {
                Record r = _roster[i];
                if (r.Dead || r.Deserted || r.Unpaid) continue;
                DefenceMember m = new DefenceMember(); m.Record = r; m.Expected = r.Order;
                Defence.Add(m);
            }
            if (Defence.Count == 0)
            { MercUi.OrderReply(Loc.T("Нет оплаченных наёмников для ПВО.", "No paid mercs for air defence."), true); return; }
            _defenceActive = true; _defenceScene = MapScene.Current; _defenceOwner = owner;
            // Replace previous individual assignments too; overflow follows.
            DefenceOne.Clear();
            for (int i = 0; i < Defence.Count; i++) DefenceOne.Add(Defence[i].Record);
            Give(DefenceOne, MercOrder.FollowMe());
            for (int i = 0; i < Defence.Count; i++) Defence[i].Expected = Defence[i].Record.Order;
            DefenceReconcile(); _defenceAt = Time.time + 0.5f;
            MercUi.OrderReply(Loc.T("ПВО: пушки, радар, затем ЗУ-23. Остальные за вами. Ещё раз - освободить.",
                "Air defence: guns, radar, then ZU-23. Others follow. Click again to release."), false);
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
            if (DefenceOne.Count > 0) Give(DefenceOne, MercOrder.FollowMe());
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
                DefenceReconcile();
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

        static bool DefenceSeat(int post, DefenceMember m, bool planning)
        {
            Record r = m == null ? null : m.Record;
            if (!MercAA.CanApproach(post, r == null || r.Unit == null ? null : r.Unit.Ai))
            {
                // A managed crewman moving to a higher-priority gun releases
                // his lease before we issue any of this plan's replacements.
                if (!planning || !MercAA.AvailableForOrder(post)) return false;
                MercAAPost held = MercAA.Held(post); bool leaving = false;
                for (int i = 0; held != null && i < Defence.Count; i++)
                {
                    DefenceMember other = Defence[i];
                    if (other.Post == post && other.Wanted != post
                        && other.Record.Unit != null && other.Record.Unit.Ai == held.Ai)
                    { leaving = true; break; }
                }
                if (!leaving) return false;
            }
            // Respect another local order's reservation before physical arrival.
            for (int i = 0; i < _roster.Count; i++)
            {
                Record other = _roster[i];
                if (other == r || other.Dead || other.Deserted || other.Down.Down) continue;
                if (!MercStations.Matches(other.Order, DefenceSeats[post])) continue;
                DefenceMember pending = planning ? DefenceFor(other) : null;
                if (pending == null || pending.Wanted == post) return false;
            }
            return true;
        }

        static int DefenceDiscover()
        {
            int count = 0;
            for (int i = 0; i < DefencePriority.Length; i++) DefencePriority[i] = -1;
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

        static int DefenceTrait(Record r)
        {
            if (r.Unit != null) return r.Unit.AAGunner;
            Profile p = ProfileById(r.ProfileId); return p == null ? 0 : p.AAGunner;
        }

        static DefenceMember DefencePick(int post)
        {
            DefenceMember best = null; int trait = -1;
            // Use followers first; only borrow a lower-priority post if necessary.
            for (int pass = 0; pass < 2 && best == null; pass++)
                for (int i = 0; i < Defence.Count; i++)
                {
                    DefenceMember m = Defence[i];
                    if (!DefenceReady(m) || !DefenceSeat(post, m, true)) continue;
                    if (pass == 0 ? m.Wanted >= 0 : m.Wanted < 0 || DefencePriority[m.Wanted] <= DefencePriority[post]) continue;
                    int score = DefenceTrait(m.Record);
                    if (post == MercAA.Radar) score = 50 - score; // Reserve specialists for guns.
                    // Equal specialists borrow ZU before radar. Contract IDs
                    // keep all other ties stable across roster reply ordering.
                    int priority = m.Wanted < 0 ? -1 : DefencePriority[m.Wanted];
                    int bestPriority = best == null || best.Wanted < 0 ? -1 : DefencePriority[best.Wanted];
                    if (best == null || score > trait || (score == trait
                        && (priority > bestPriority || (priority == bestPriority && m.Record.Id < best.Record.Id))))
                    { best = m; trait = score; }
                }
            return best;
        }

        static void DefenceReconcile()
        {
            int count = DefenceDiscover();
            for (int i = 0; i < Defence.Count; i++)
            {
                DefenceMember m = Defence[i];
                m.Wanted = m.Post >= 0 && DefencePriority[m.Post] >= 0 && DefenceReady(m)
                    && DefenceSeat(m.Post, m, false) ? m.Post : -1;
            }
            for (int n = 0; n < count; n++)
            {
                int post = DefencePosts[n]; bool staffed = false;
                for (int i = 0; i < Defence.Count; i++) if (Defence[i].Wanted == post) { staffed = true; break; }
                if (staffed) continue;
                DefenceMember next = DefencePick(post);
                if (next != null) next.Wanted = post;
            }
            // Release old claims before assigning replacements, including lower-priority crew.
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
            }
            if (DefenceChanged.Count > 0) SaveStationOrders(DefenceChanged);
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

// Owner death shelter lifecycle; no new wire protocol or server authority.
// Runs inside Mercs.T at 4 Hz, movement in the existing NpcWar F6 slot.
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class Mercs
    {
        static bool _survivalOwnerAlive, _survivalNotice;
        static float _nextShelterSave, _nextShelterLabel;
        static readonly List<Record> _shelterChanged = new List<Record>();
        internal static string ShelterLabel = "";
        internal static Vector3 ShelterAt;

        static readonly Vector3[] _spawnApproach = new Vector3[1];
        static readonly float[] _spawnWeight = new float[] { 1f };

        // A relog never places the man at a stale, now exposed death/save spot.
        // The existing cover mapper warms the cells while spawning waits.
        static bool ShelterSpawn(Record r, ref Vector3 want)
        {
            MercCoverService.Field.Want(want, Time.time);
            if (!MercCoverService.Field.ReadyAt(want) || !MercCoverService.MayQuery()) return false;
            Vector3 facing = r.Order.Facing;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward;
            _spawnApproach[0] = want + facing * 280f;
            CoverPick pick;
            if (!MercCoverService.Best(want, _spawnApproach, _spawnWeight, 1, 40f,
                want, 40f, r.Id, out pick) || !pick.Confirmed) return false;
            want = pick.Point.Pos;
            return true;
        }

        static void SurvivalOwner(GameObject owner, float now)
        {
            if (_units.Count == 0) { _survivalOwnerAlive = false; ShelterLabel = ""; return; }
            bool alive = owner != null && !PlayerDead(owner);
            _shelterChanged.Clear();
            if (_survivalOwnerAlive && !alive)
            {
                for (int n = 0; n < _roster.Count; n++)
                {
                    Record r = _roster[n]; MercUnit u = r.Unit;
                    if (u == null || u.Ai == null || r.Dead || u.Deserting) continue;
                    bool companion = r.Order.Mode == MercOrder.Follow || r.Order.Mode == MercOrder.Vehicle;
                    bool far = owner != null && Flat(u.Ai.transform.position - owner.transform.position) > 168f;
                    // merc-attack-orders: an ATTACK never outlives the owner. Nearby
                    // attackers shelter like companions; a distant one keeps a post
                    // where he stands (a STAY, as distant posts keep theirs) - no
                    // assault runs on or resumes after the respawn.
                    if (r.Order.Mode == MercOrder.Attack && far)
                    {
                        MercOrder post = new MercOrder(); post.Mode = MercOrder.Stay;
                        post.Scene = MapScene.Current; post.Points = new Vector3[] { u.Ai.transform.position };
                        post.Facing = r.Order.Facing;
                        r.Order = post; u.Order = post;
                        _shelterChanged.Add(r);
                        continue;
                    }
                    if (!companion && r.Order.Mode != MercOrder.Attack && far) continue;
                    MercOrder hold = new MercOrder(); hold.Mode = MercOrder.Stay; hold.Survive = true;
                    hold.Scene = MapScene.Current; hold.Points = new Vector3[] { u.Ai.transform.position };
                    hold.Facing = u.Sense.Count > 0 ? (u.Sense.At[0] - hold.Centre).normalized : u.Ai.transform.forward;
                    r.Order = hold; u.Order = hold; u.Rally = false;
                    u.Approach = u.Sense.Count > 0 ? u.Sense.At[0] : hold.Centre + hold.Facing * 280f;
                    if (u.Fight.Brain != null) u.Fight.Brain.Leave(MercCoverService.Field);
                    u.Fight.Out = new FightOut(); u.Sense.PickAt = -1000f; u.Sense.NextPick = 0f;
                    _shelterChanged.Add(r); _survivalNotice = true;
                }
                if (_shelterChanged.Count > 0) SendOrders(_shelterChanged);
            }
            _survivalOwnerAlive = alive;
            _shelterChanged.Clear();
            int count = 0; Vector3 first = Vector3.zero;
            for (int n = 0; n < _roster.Count; n++)
            {
                Record r = _roster[n]; MercUnit u = r.Unit;
                if (r.Dead || r.Down.Down || !r.Order.Survive || u == null || u.Ai == null) continue;
                if (count++ == 0) first = u.Ai.transform.position;
                if (now >= _nextShelterSave && u.Fight.Holding && u.Fight.Brain != null)
                {
                    Vector3 at = u.Fight.Brain.Cover.Point.Pos;
                    if (Flat(at - r.Order.Centre) > 2f)
                    {
                        r.Order.Points[0] = at; _shelterChanged.Add(r);
                    }
                }
            }
            if (now >= _nextShelterSave)
            {
                _nextShelterSave = now + 5f;
                if (_shelterChanged.Count > 0) SendOrders(_shelterChanged);
            }
            if (count == 0) { ShelterLabel = ""; return; }
            ShelterAt = first;
            if (now >= _nextShelterLabel && (GameUi.State == 8 || (alive && _survivalNotice)))
            {
                _nextShelterLabel = now + 1f;
                float km = owner == null ? 0f : Flat(first - owner.transform.position) / 2800f;
                ShelterLabel = count + Loc.T(" наемников в укрытии: ", " mercs holding at ")
                    + (first.x / 2.8f).ToString("0", CultureInfo.InvariantCulture) + ", "
                    + (first.z / 2.8f).ToString("0", CultureInfo.InvariantCulture) + " m, "
                    + km.ToString("0.0", CultureInfo.InvariantCulture) + Loc.T(" км", " km");
            }
            if (alive && _survivalNotice && ShelterLabel.Length > 0)
            {
                _survivalNotice = false;
                MercUi.Toast(ShelterLabel + Loc.T(". K K - собраться у меня.", ". K K - rally on me."), false);
            }
        }
    }
}

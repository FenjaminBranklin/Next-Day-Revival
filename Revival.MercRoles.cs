// Y S3: role formation adapter under the existing F6 NpcWar.Tick slot.
using UnityEngine;

namespace NextDayRevival
{
    public static partial class NpcWar
    {
        static readonly Vector3[] _roleWatch = new Vector3[1];
        static readonly float[] _roleWeight = { 1f };

        static bool MercRoleFollow(MercUnit u)
        {
            return u.Owner != null && !u.Order.Survive && !u.Rally && !u.Deserting
                && u.Ride.Boarding == null
                && (u.Order.Mode == MercOrder.Follow || u.Order.Mode == MercOrder.Vehicle);
        }

        static bool MercRoleProtected(MercUnit u, float now)
        {
            MercFight ft = u.Fight;
            return ft.Health < MercBrain.RetreatUntil || ft.In.Reloading || ft.In.Danger
                || (ft.Hits > 0 && now - u.LastHit.At < 1.5f)
                || (u.Sense.Exposed && now - u.Sense.ExposedAt < 0.8f)
                || (ft.Brain != null && (ft.Brain.Mode == MercBrain.Retreating || ft.Brain.Mode == MercBrain.Falling));
        }

        static bool MercRoleReposition(Fighter f, MercUnit u, float now)
        {
            if (u.Fight.Overwatch.Role != MercRole.Marksman || !MercRoleFollow(u)) return false;
            return MercRole.Reposition(u.Fight.Overwatch.Role, MercRoleProtected(u, now),
                MercRole.InPosition(f.Tr.position, u.Owner.position, u.Owner.forward), f.Sees,
                u.Sense.Count > 0, now - f.LastSeen);
        }

        /// <summary>Immediate role goal; at most one globally shared M1
        /// query per frame and two per second per marksman. Mapping or LOS
        /// failure never vetoes the command: move and try another lane.</summary>
        static Vector3 MercOverwatchGoal(Fighter f, MercUnit u, Vector3 front, float now)
        {
            MercOverwatch r = u.Fight.Overwatch;
            bool had = r.HasCover;
            if (r.Update(u.Order, u.Owner.position, front, u.Slot) && had)
                MercCoverService.Field.Release(u.Id);
            Vector3 watch = u.Sense.Count > 0 ? u.Sense.At[0] : MercHalt.Watch(u.Owner.position, front);
            bool changed = (watch - r.Watch).sqrMagnitude > 225f;
            if (now >= r.NextQuery && MercCoverService.MayQuery())
            {
                r.NextQuery = now + MercRole.QueryEvery;
                bool blind = !f.Sees && u.Sense.Count > 0 && now - f.LastSeen >= MercRole.BlindAfter
                    && Flat(r.Goal - f.Tr.position) <= 3f;
                if (changed || blind)
                {
                    if (r.HasCover && blind) r.NextLane(u.Slot);
                    r.HasCover = false; r.Clear = false;
                    MercCoverService.Field.Release(u.Id);
                }
                r.Watch = watch;
                if (!r.HasCover)
                {
                    Vector3 slot = MercRole.Slot(r.Role, r.OwnerAt, r.Front, u.Slot, r.Lane);
                    _roleWatch[0] = watch;
                    CoverPick pick;
                    if (MercCoverService.Best(slot, _roleWatch, _roleWeight, 1,
                        MercRole.CoverRadius, slot, MercRole.CoverRadius, u.Id, out pick)
                        && pick.Confirmed && pick.PeekVerified
                        && MercRole.InBand(pick.Point.Pos, u.Owner.position)
                        && MercRole.InBand(pick.PeekPos, u.Owner.position)
                        && !MercTeam.Board.Crowded(pick.Point.Pos, u.Id, MercSquad.Spread, now))
                    {
                        r.Pick = pick; r.HasCover = true; r.Clear = true;
                        r.Goal = pick.Point.Pos; r.NextClaim = 0f;
                    }
                    else
                    {
                        Vector3 ground;
                        if (RevivalGroundEnemies.TryGround(slot, 8f, out ground)
                            && MercRole.InBand(ground, u.Owner.position)) r.Goal = ground;
                        r.Clear = MercCoverService.Exposed(r.Goal, false, watch);
                        if (!r.Clear) r.NextLane(u.Slot);
                    }
                }
            }
            if (r.HasCover && now >= r.NextClaim)
            {
                r.NextClaim = now + 2f;
                MercCoverService.Field.Claim(u.Id, r.Goal, now + 5f, now);
            }
            return r.Goal;
        }
    }
}

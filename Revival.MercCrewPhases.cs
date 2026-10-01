// Z K5a1: owner AI leaves the gun lease to fight from its earthwork, then
// renews that same post on a new radar wave. No new money/ownership authority.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal static class MercCrewPhases
    {
        internal static Flak.Gun Gun(MercUnit u)
        {
            if (u == null || u.Order.Mode != MercOrder.ManGun || MercAA.IsVehicle(u.Order)) return null;
            int post = MercAA.PostOf(u);
            if (post < 0 || post >= 14 || post == MercAA.Radar || post == MercAA.Radar + 7) return null;
            Flak.Gun g = Flak.ByIndex(post >= 7 ? post - 7 : post);
            return g != null && g.Earthwork != null ? g : null;
        }

        internal static bool Ground(MercUnit u)
        {
            return u != null && u.Fight.CrewOrder == u.Order
                && u.Fight.Crew.OnGround && Gun(u) != null;
        }

        internal static Vector3 Bound(MercUnit u, Vector3 goal)
        {
            if (!Ground(u) || u.AARetreat || u.Fight.In.Danger) return goal;
            Vector3 centre = Gun(u).Earthwork.position;
            Vector3 delta = goal - centre;
            float distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
            if (distance > MercCrewPhase.PostRadius)
            {
                float scale = MercCrewPhase.PostRadius / distance;
                goal.x = centre.x + delta.x * scale; goal.z = centre.z + delta.z * scale;
            }
            return goal;
        }

        internal static bool Inbound(Flak.Gun g, string side)
        {
            List<GepardGun.Contact> air = RadarScope.Air;
            Vector3 centre = g.Earthwork.position;
            for (int i = 0; i < air.Count; i++)
            {
                GepardGun.Contact c = air[i];
                if (c.Go == null || !RadarShadow.Visible(c.Go)
                    || RadarShadow.Height(c.Go) < TowerRadar.F(TowerRadar.CfgMinHeight, 3f) * Flak.K) continue;
                bool hostile, friendly;
                FlakFire.Allegiance(c, side, out hostile, out friendly);
                if (!hostile) continue;
                float eta, miss;
                // Any hostile in the defended bubble, or entering it soon.
                // Includes transports before their jump, helis and drones.
                float dx = c.Pos.x - centre.x, dz = c.Pos.z - centre.z;
                if (dx * dx + dz * dz <= 1500f * Flak.K * 1500f * Flak.K
                    || AirPicturePolicy.Inbound(dx, dz,
                    c.Vel.x, c.Vel.z, 1500f * Flak.K, 120f, out eta, out miss)) return true;
            }
            return false;
        }

        internal static void OverrideSelected(int mode)
        {
            List<Mercs.Record> selected = Mercs.Selection();
            for (int i = 0; i < selected.Count; i++)
            {
                MercUnit u = selected[i].Unit;
                if (Gun(u) == null) continue;
                u.Fight.CrewOrder = u.Order;
                u.Fight.Crew.Override = mode; u.Fight.Crew.NextSample = 0f;
                u.NextOrder = 0f; u.AANextSend = 0f;
                NpcWar.MercStationWake(u);
                MercUi.OrderReply(u.Name + ": " + (mode == MercCrewPhase.Air
                    ? Loc.T("воздушная фаза", "air duty") : mode == MercCrewPhase.Ground
                    ? Loc.T("наземная фаза", "ground duty") : Loc.T("автоматические фазы", "automatic duty")), false);
            }
        }
    }

    public static partial class NpcWar
    {
        static bool MercCrewUpdate(Fighter f, MercUnit u, float now)
        {
            FrameProf.S(FrameProf.S_MercCrewPhase);
            try
            {
                MercFight ft = u.Fight;
                if (ft.CrewOrder != u.Order)
                { ft.Crew.Reset(); ft.CrewThreat = null; ft.CrewOrder = u.Order; }
                Flak.Gun g = MercCrewPhases.Gun(u);
                if (g == null) { ft.Crew.Reset(); ft.CrewThreat = null; return false; }
                MercCrewPhase phase = ft.Crew;
                if (now < phase.NextSample) return phase.OnGround;
                phase.NextSample = now + 0.25f;
                Vector3 centre = g.Earthwork.position;
                int side;
                bool knownSide = FacValue(f.Faction, out side);
                MercAAPost radar = MercAA.Operator;
                // A released gun lease must not erase the crew's radar side.
                bool fresh = knownSide && TowerRadar.Built && TowerRadar.Working
                    && ((radar != null && radar.Side == side)
                        || (TowerRadar.Tier == 2 && TowerRadar.ControlSide == side))
                    && now - RadarScope.SampleAt <= 0.75f;
                bool inbound = fresh && MercCrewPhases.Inbound(g, Mercs.SideOf(side));
                if (MercCrewGroundNear(f, u, centre, now)) phase.GroundUntil = now + 3f;
                bool before = phase.OnGround;
                phase.Step(now, fresh, inbound, now < phase.GroundUntil);
                if (g.ShortRange)
                {
                    ZuGround.OwnerMode(g, u, phase.Override);
                    Vector3 threat = ft.CrewThreat != null ? ft.CrewThreat.transform.position
                        : f.Target != null && now - f.LastSeen < 3f ? f.Target.position : centre;
                    bool close = (ft.CrewThreat != null || (f.Target != null && now - f.LastSeen < 3f))
                        && phase.Override != MercCrewPhase.Air && ZuGround.Close(g, threat);
                    phase.OnGround = ZuGroundCore.Rifle(phase.OnGround,
                        ZuGround.Reachable(g, threat), close);
                }
                if (phase.OnGround)
                    u.Approach = ft.CrewThreat != null ? ft.CrewThreat.transform.position
                        : f.Target != null ? f.Target.position : centre + Vector3.forward * 100f;
                if (phase.OnGround != before)
                {
                    MercAA.Release(u);
                    if (ft.Brain != null) ft.Brain.Leave(MercCoverService.Field);
                    ft.Out = new FightOut(); ft.LastAct = FightAct.None;
                    ft.FallbackAt = 0f; u.Sense.Pick = new CoverPick();
                    u.Sense.NextSense = u.Sense.NextPick = 0f;
                    u.NextOrder = u.AANextSend = 0f; f.HasOrder = false;
                    // The seated root may be above the carved NavMesh. Sample
                    // only on dismount, inside the already surveyed pad; never
                    // warp through the berm to an arbitrary external point.
                    if (phase.OnGround)
                    {
                        NavMeshAgent agent = Agent(f);
                        NavMeshHit hit;
                        if (agent != null && agent.isActiveAndEnabled
                            && NavMesh.SamplePosition(f.Tr.position, out hit, 6f, NavMesh.AllAreas)
                            && MercCrewPhase.InPost(hit.position.x - centre.x, hit.position.z - centre.z))
                            agent.Warp(hit.position);
                    }
                }
                return phase.OnGround;
            }
            finally { FrameProf.E(FrameProf.S_MercCrewPhase); }
        }

        static bool MercCrewGroundNear(Fighter f, MercUnit u, Vector3 centre, float now)
        {
            Flak.Gun g = MercCrewPhases.Gun(u);
            if (g != null && g.ShortRange) return ZuCrewThreat(f, u, g, now);
            Component remembered = u.Fight.CrewThreat;
            if (MercCrewEnemy(f, remembered, centre)) return true;
            u.Fight.CrewThreat = null;
            if (f.Target != null && MercCrewPhase.NearGround(f.Target.position.x - centre.x,
                f.Target.position.z - centre.z) && now - f.LastSeen < 3f) return true;
            // Cached scene list, bounded/time-sliced. No discovery, LOS or
            // terrain queries; ordinary Acquire still verifies rifle targets.
            for (int n = 0; n < 32 && n < _scene.Count; n++)
            {
                if (u.Fight.Crew.Cursor >= _scene.Count) u.Fight.Crew.Cursor = 0;
                Component c = _scene[u.Fight.Crew.Cursor++];
                if (MercCrewEnemy(f, c, centre))
                { u.Fight.CrewThreat = c; return true; }
            }
            // The normal cached owner/whitelist/peaceful player filter.
            Component player = MercPlayerTarget(f, MercCrewPhase.GroundRange, now);
            return player != null && MercCrewPhase.NearGround(player.transform.position.x - centre.x,
                player.transform.position.z - centre.z);
        }

        static bool MercCrewEnemy(Fighter f, Component c, Vector3 centre)
        {
            if (c == null || c == f.Ai || !MercCrewPhase.NearGround(c.transform.position.x - centre.x,
                c.transform.position.z - centre.z) || !Alive(c) || MercRide.HiddenRider(c)
                || !Hostile(f.Hated, FactionOf(c))) return false;
            Fighter other = FighterOf(c);
            return (other == null || other.Squad != f.Squad)
                && ((other != null && other.Squad != null) || Targetable(c));
        }

        static void MercCrewHold(Fighter f, MercUnit u, float now)
        {
            MercAA.Release(u);
            Flak.Gun g = MercCrewPhases.Gun(u);
            if (g == null) return;
            Vector3 centre = g.Earthwork.position;
            if (!MercCrewPhase.InPost(f.Tr.position.x - centre.x, f.Tr.position.z - centre.z))
            { MercMove(f, u, centre, true, now); return; }
            CoverPick pick = u.Sense.Pick;
            if (pick.Found && now - u.Sense.PickAt < 5f
                && MercCrewPhase.InPost(pick.Point.Pos.x - centre.x, pick.Point.Pos.z - centre.z)
                && Flat(pick.Point.Pos - f.Tr.position) > 2f)
            { MercMove(f, u, pick.Point.Pos, true, now); return; }
            // Quiet or beyond rifle reach: stay low at the wall, retaining
            // the profile weapon. M1 picks/peeks take over on contact.
            MercCrouch(f, now);
        }
    }
}

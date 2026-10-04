// Z K5a2: master-owned ground fire; owner crew AI keeps the ordinary seat lease.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal static class ZuGround
    {
        internal sealed class State
        {
            internal Flak.Gun Gun;
            internal Transform Target;
            internal Transform Blocked;
            internal Component Npc;
            internal GameObject Player;
            internal int Cursor, Duty, Actor = -1, View;
            internal float Until, NextScan, SampleAt, BlockedUntil;
            internal Vector3 Previous, Velocity;
        }
        static readonly State[] States = MakeStates();
        static readonly float[] Packet = new float[7];
        static float _priorityUntil;
        internal static bool PriorityActive { get { return Time.time < _priorityUntil; } }
        static State[] MakeStates()
        {
            State[] states = new State[7];
            for (int i = 0; i < states.Length; i++) states[i] = new State();
            return states;
        }
        static State For(Flak.Gun g)
        {
            State s = States[g.Index];
            if (s.Gun != g)
            {
                s.Gun = g; s.Target = null; s.Npc = null; s.Player = null;
                s.Blocked = null; s.BlockedUntil = 0f;
                s.Cursor = s.Duty = s.View = 0; s.Actor = -1;
                s.Until = s.NextScan = s.SampleAt = 0f; s.Velocity = Vector3.zero;
            }
            return s;
        }
        internal static int Duty(Flak.Gun g)
        {
            State s = For(g);
            MercAAPost merc = MercAA.Gun(g.Index);
            return merc != null && merc.Actor == s.Actor && merc.View == s.View && Time.time < s.Until
                ? s.Duty : MercCrewPhase.Auto;
        }
        // Called on the existing 4 Hz owner phase sample. No extra Update tick.
        internal static void OwnerMode(Flak.Gun g, MercUnit u, int duty)
        {
            if (!g.ShortRange) return;
            Packet[0] = ZuGroundCore.ModePacket; Packet[1] = g.Index;
            Packet[2] = u.AAView; Packet[3] = duty;
            if (MercAA.Authority) OnPacket(Packet, Mercs.LocalActor);
            else FlakNet.SendPacket(Packet, false);
        }
        internal static void OnPacket(float[] p, int sender)
        {
            if (!MercAA.Authority || p == null || p.Length != 7) return;
            for (int i = 0; i < p.Length; i++) if (float.IsNaN(p[i]) || float.IsInfinity(p[i])) return;
            if (p[0] != ZuGroundCore.ModePacket) return;
            int index = Mathf.RoundToInt(p[1]), view = Mathf.RoundToInt(p[2]), duty = Mathf.RoundToInt(p[3]);
            if (p[1] != index || p[2] != view || p[3] != duty || index < 0 || index >= States.Length
                || duty < MercCrewPhase.Auto || duty > MercCrewPhase.Ground) return;
            Flak.Gun g = Flak.ByIndex(index);
            MercAAPost merc = MercAA.Gun(index);
            if (g == null || !g.ShortRange || merc == null || merc.Actor != sender || merc.View != view) return;
            State s = For(g); s.Actor = sender; s.View = view; s.Duty = duty; s.Until = Time.time + 1.5f;
        }

        internal static bool Reachable(Flak.Gun g, Vector3 point)
        {
            Vector3 d = point + Vector3.up * Flak.K - Flak.Mid(g);
            return ZuGroundCore.Envelope(d.x, d.y, d.z) && !Settlement(point);
        }
        static bool Settlement(Vector3 point)
        {
            if (MapScene.AtHome)
            {
                Vector3 d = point - MilitaryTown.Centre;
                if (d.x * d.x + d.z * d.z <= MilitaryTown.MapRingRadius * MilitaryTown.MapRingRadius
                    || MilitaryTown.Inside(point, 0f)) return true;
                d = point - new Vector3(1446.6f, 0f, 1703.2f);
                if (d.x * d.x + d.z * d.z <= 340f * 340f) return true;
            }
            if (NewSettlement.Here())
            {
                Vector3 d = point - NewSettlement.Centre();
                float r = NewSettlement.MapRingRadius();
                if (d.x * d.x + d.z * d.z <= r * r) return true;
            }
            return SettlementScan.Registry.NativeSettlementNear(point, 340f);
        }
        internal static bool Close(Flak.Gun g, Vector3 point)
        {
            Vector3 d = point - g.Root.position;
            float r = ZuGroundCore.DeadM * Flak.K;
            return d.x * d.x + d.z * d.z < r * r;
        }
        // Known remote NPCs need the existing owner RPC; calling ApplyDamage
        // locally on the master would be ignored by their isMine guard.
        internal static bool RemoteImpact(GameObject struck, float damage, Vector3 point)
        {
            if (struck == null) return false;
            Transform tr = struck.transform;
            for (int i = 0; i < States.Length; i++)
            {
                Component npc = States[i].Npc;
                if (npc != null && tr.IsChildOf(npc.transform)) return NpcWar.ZuRemoteHit(npc, damage, point);
            }
            return NpcWar.ZuRemoteStruck(struck, damage, point);
        }
        // Ground and air use the SAME ready magazine. Empty boxes are costly
        // against both; the separate item-supply task owns replenishment.
        internal static bool Control(Flak.Gun g, float dt, bool gunner, bool loader)
        {
            FrameProf.S(FrameProf.S_ZuGround);
            try
            {
                State s = For(g);
                bool allowed = ZuGroundCore.GroundAllowed(Duty(g), g.Target != null && g.Target.Go != null);
                if (!allowed)
                {
                    if (s.Target != null) { g.Engaged = g.Firing = false; g.Held = 0f; g.ShortBurst = new ShortBurst(); }
                    s.Target = null; s.Npc = null; s.Player = null;
                    return false;
                }
                if (Time.time >= s.NextScan)
                {
                    s.NextScan = Time.time + 0.5f;
                    Transform before = s.Target;
                    NpcWar.ZuSelect(g, s);
                    if (s.Target != null && !Lane(g, s.Target, s.Target.position + Vector3.up * Flak.K))
                    {
                        s.Blocked = s.Target; s.BlockedUntil = Time.time + 2f;
                        s.Target = null; s.Npc = null; s.Player = null;
                    }
                    if (s.Target != before)
                    {
                        g.Engaged = false; g.Held = 0f; g.ShortBurst = new ShortBurst(); s.Velocity = Vector3.zero;
                        if (s.Target != null) Flak.RaiseEngaging(g, s.Target.gameObject);
                    }
                    if (s.Target != null)
                    {
                        Vector3 pos = s.Target.position;
                        float elapsed = Time.time - s.SampleAt;
                        s.Velocity = before == s.Target && elapsed > 0.01f ? (pos - s.Previous) / elapsed : Vector3.zero;
                        s.Previous = pos; s.SampleAt = Time.time;
                    }
                }
                if (s.Target == null) return false;
                // A cached target may leave the envelope between 2 Hz scans.
                if (!Reachable(g, s.Target.position))
                {
                    s.Target = null; s.Npc = null; s.Player = null;
                    g.Engaged = g.Firing = false; g.Held = 0f; g.ShortBurst = new ShortBurst();
                    return false;
                }
                Vector3 mid = Flak.Mid(g), point = s.Target.position + Vector3.up * Flak.K;
                float tof;
                Vector3 aim = ShortRange.Intercept(mid, point, s.Velocity, out tof);
                float yaw, pitch;
                Flak.Angles(g, aim - mid, out yaw, out pitch);
                g.Engaged = true; g.Held += dt;
                _priorityUntil = Time.time + 0.75f;
                g.WantYaw = yaw; g.WantPitch = Mathf.Clamp(pitch, ZuGroundCore.MinPitch, 90f); g.Aim = aim;
                // Fly beyond the chest: ground rounds must strike, not expire
                // as an airborne time puff just short of the body collider.
                g.FuzeRange = Mathf.Min((point - mid).magnitude + 15f * Flak.K, ShortRangeCore.RangeM * Flak.K);
                bool ready = pitch >= ZuGroundCore.MinPitch
                    && pitch <= ZuGroundCore.MaxPitch && g.Held >= 0.8f && !g.Reloading
                    && g.Mode != FlakMode.HoldFire && Vector3.Angle(g.Cradle.forward, aim - mid) < 1.5f;
                // Recheck sight and the led ballistic lane only when a round
                // can leave (20 Hz maximum), not on every laying frame.
                if (ready && g.Rounds > 0 && Time.time >= g.ShortBurst.Next
                    && Time.time >= g.ShortBurst.PauseUntil
                    && (!Reachable(g, s.Target.position + s.Velocity * tof)
                        || !Lane(g, s.Target, point, s.Velocity)))
                {
                    s.Blocked = s.Target; s.BlockedUntil = Time.time + 2f;
                    s.Target = null; s.Npc = null; s.Player = null;
                    g.Engaged = g.Firing = false; g.Held = 0f; g.ShortBurst = new ShortBurst();
                    Flak.Publish(g, false, false);
                    return false;
                }
                bool corrected;
                bool shot = ShortRangeCore.Shot(ref g.ShortBurst, Time.time, ready, out corrected);
                g.Firing = ready && g.ShortBurst.Active;
                if (shot)
                {
                    if (g.Rounds <= 0) Flak.StartReload(g, ShortRangeCore.ReloadSeconds * (gunner && loader ? 1f : 1.5f));
                    else Flak.Fire(g, aim, true, g.FuzeRange, g.Hostile, false);
                }
                Flak.Publish(g, false, false);
                return true;
            }
            finally { FrameProf.E(FrameProf.S_ZuGround); }
        }

        // Direct sight plus three ballistic segments, all scene colliders.
        // Props, slabs, fences and earthworks block both scan and shot gates.
        internal static bool Lane(Flak.Gun g, Transform target, Vector3 point)
        {
            return Lane(g, target, point, Vector3.zero);
        }
        static bool Lane(Flak.Gun g, Transform target, Vector3 point, Vector3 targetVelocity)
        {
            Vector3 from = g.Muzzle.position;
            if (!ClearSegment(g, target, from, point)) return false;
            float tof;
            Vector3 aim = ShortRange.Intercept(from, point, targetVelocity, out tof);
            if (tof <= 0f) return false;
            Vector3 velocity = (aim - from) / tof, previous = from;
            for (int segment = 1; segment <= 3; segment++)
            {
                float t = tof * segment / 3f;
                Vector3 next = from + velocity * t - Vector3.up * (0.5f * 9.81f * Flak.K * t * t);
                if (!ClearSegment(g, target, previous, next)) return false;
                previous = next;
            }
            return true;
        }
        static bool ClearSegment(Flak.Gun g, Transform target, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float rest = d.magnitude;
            Vector3 dir = d.normalized, origin = from;
            for (int skip = 0; rest > 0.05f; skip++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, rest, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) break;
                Transform tr = hit.transform;
                if (tr == target || tr.IsChildOf(target)) return true;
                if (skip >= 4 || !((g.Owner != null && tr.IsChildOf(g.Owner)) || FlakFire.Crewman(g, tr))) return false;
                rest -= hit.distance + 0.05f; origin = hit.point + dir * 0.05f;
            }
            return true;
        }
    }

    public static partial class NpcWar
    {
        internal static bool ZuRemoteHit(Component ai, float damage, Vector3 point)
        {
            if (!LookUp() || ai == null || IsMine(ai)) return false;
            BreakKillStreak(ai);
            return RemoteHit(ai, damage, point);
        }
        internal static bool ZuRemoteStruck(GameObject struck, float damage, Vector3 point)
        {
            // Manual gunfire has no preselected NPC contact. Resolve its
            // impact component once through the ordinary native hit path.
            if (struck == null || !LookUp() || _npcType == null) return false;
            return ZuRemoteHit(struck.GetComponentInParent(_npcType), damage, point);
        }
        internal static void ZuSelect(Flak.Gun g, ZuGround.State state)
        {
            MercAAPost merc = MercAA.Gun(g.Index);
            Component crew = merc != null ? merc.Ai : Flak.Up(g.Gunner) ? g.Gunner : g.Loader;
            if (crew == null || !LookUp()) { state.Target = null; return; }
            Array hated = GetHated(crew);
            Component best = state.Npc != null && !ZuMasked(state, state.Npc.transform)
                && ZuPermitted(g, state.Npc.transform) && ZuEnemy(g, state.Npc, hated) ? state.Npc : null;
            float distance = best != null ? (best.transform.position - g.Root.position).sqrMagnitude : float.MaxValue;
            for (int n = 0; n < 32 && n < _scene.Count; n++)
            {
                if (state.Cursor >= _scene.Count) state.Cursor = 0;
                Component c = _scene[state.Cursor++];
                if (c == null) continue;
                float d = (c.transform.position - g.Root.position).sqrMagnitude;
                if (d >= distance || ZuMasked(state, c.transform) || !ZuPermitted(g, c.transform) || !ZuEnemy(g, c, hated)) continue;
                best = c; distance = d;
            }
            GameObject player = null;
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject c = players[i];
                if (c == null || (merc != null && Mercs.ActorOf(c) == merc.Actor) || Mercs.PlayerDead(c)) continue;
                float d = (c.transform.position - g.Root.position).sqrMagnitude;
                if (d >= distance || ZuMasked(state, c.transform) || !ZuPermitted(g, c.transform)
                    || !ZuGround.Reachable(g, c.transform.position)) continue;
                string steam = Mercs.SteamOf(c);
                if (steam != null && Mercs.Whitelisted(steam)) continue;
                int side = Mercs.FactionOf(c);
                if (side < 0 || !HatedValue(hated, side)) continue;
                player = c; distance = d;
            }
            state.Npc = player == null ? best : null; state.Player = player;
            state.Target = player != null ? player.transform : best != null ? best.transform : null;
        }
        // Filter before ranking: an assigned distant target must not lose
        // to an unassigned nearer man. HOLD is enforced by the shot gate.
        static bool ZuPermitted(Flak.Gun g, Transform target)
        {
            return (g.Mode != FlakMode.AssignedOnly || target.gameObject == g.Assigned)
                && (g.Mode != FlakMode.ZoneDefence || (target.position - g.ZoneCentre).sqrMagnitude <= g.ZoneRadius * g.ZoneRadius);
        }
        static bool ZuEnemy(Flak.Gun g, Component c, Array hated)
        {
            return c != null && c.gameObject.activeInHierarchy && ZuGround.Reachable(g, c.transform.position)
                && Alive(c) && !MercRide.HiddenRider(c) && Hurtable(c) && !GroundDowned(c)
                && !Bool(c, "GodModeEnabled") && !Bool(c, "_isSafeSettlement") && !Bool(c, "IsTalkActive")
                && Hostile(hated, FactionOf(c));
        }
        static bool ZuMasked(ZuGround.State state, Transform tr)
        {
            return state.Blocked == tr && Time.time < state.BlockedUntil;
        }

        // A firing open ZU crew is an assault NPC's first visible target.
        // Uses the existing acquisition cadence and its ordinary faction/LOS.
        static bool ZuCrewPriority(Fighter f, float now)
        {
            if (!ZuGround.PriorityActive) return false;
            if (f.Squad != null && f.Squad.Merc != null) return false;
            Component best = null;
            float reach = RangeOf(f), distance = reach * reach;
            for (int i = 0; i < 7; i++)
            {
                Flak.Gun g = Flak.ByIndex(i);
                if (g == null || !g.ShortRange || !g.Engaged || !AirDefenceDamage.Alive(g.Index)) continue;
                MercAAPost merc = MercAA.Gun(g.Index);
                Component c = merc != null ? merc.Ai : Flak.Up(g.Gunner) ? g.Gunner : g.Loader;
                if (c == null || !Alive(c) || !Hostile(f.Hated, FactionOf(c))) continue;
                float d = (c.transform.position - f.Tr.position).sqrMagnitude;
                if (d < distance) { best = c; distance = d; }
            }
            float height;
            if (best == null || !AimPoint(f, best.transform, out height)) return false;
            f.Target = best.transform; f.TargetIsPlayer = false;
            f.AimHeight = height; f.LastSeen = now; f.NextLos = now + 0.3f;
            return true;
        }

        static bool ZuCrewThreat(Fighter f, MercUnit u, Flak.Gun g, float now)
        {
            Component best = ZuOwnerEnemy(f, u.Fight.CrewThreat, g) ? u.Fight.CrewThreat : null;
            float distance = best != null ? (best.transform.position - g.Root.position).sqrMagnitude : float.MaxValue;
            // Always walk a bounded slice, even with a remembered distant
            // target: a new close attacker must eventually force dismount.
            for (int n = 0; n < 32 && n < _scene.Count; n++)
            {
                if (u.Fight.Crew.Cursor >= _scene.Count) u.Fight.Crew.Cursor = 0;
                Component c = _scene[u.Fight.Crew.Cursor++];
                if (c == null) continue;
                float d = (c.transform.position - g.Root.position).sqrMagnitude;
                if (d >= distance || !ZuOwnerEnemy(f, c, g)) continue;
                best = c; distance = d;
            }
            Component player = MercPlayerTarget(f, ZuGroundCore.RangeM * Flak.K, now);
            if (player != null && (player.transform.position - g.Root.position).sqrMagnitude < distance) best = player;
            u.Fight.CrewThreat = best;
            return best != null;
        }
        static bool ZuOwnerEnemy(Fighter f, Component c, Flak.Gun g)
        {
            float range = ZuGroundCore.RangeM * Flak.K;
            if (c == null || c == f.Ai || (c.transform.position - g.Root.position).sqrMagnitude > range * range
                || !Alive(c) || MercRide.HiddenRider(c) || !Hostile(f.Hated, FactionOf(c))) return false;
            Fighter other = FighterOf(c);
            return (other == null || other.Squad != f.Squad)
                && ((other != null && other.Squad != null) || Targetable(c));
        }
    }
}

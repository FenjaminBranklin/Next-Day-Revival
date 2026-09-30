// W AA6: equipped merc MANPADS, owner-client flight and native NPC ammo/reload.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    internal sealed class MercStingerState
    {
        internal readonly List<GepardGun.Contact> Air = new List<GepardGun.Contact>(16);
        internal GepardGun.Contact Target;
        internal float NextScan, Held, LastTick;
    }

    public static partial class NpcWar
    {
        static bool MercStingerFight(Fighter f, MercUnit u, float now)
        {
            FrameProf.S(FrameProf.S_MercStingerT);
            try
            {
                MercStingerState s = f.Manpads;
                float dt = s.LastTick > 0f ? Mathf.Min(0.25f, now - s.LastTick) : 0f;
                s.LastTick = now;
                if (!MercMayEngage(u, now) || !MercMayStand(f, u) || u.Ride.Boarding != null
                    || u.Ride.Carrier != null
                    || u.Owner == null || (u.Owner.position - f.Tr.position).sqrMagnitude > 1000f * 1000f)
                { s.Held = 0f; s.Target = null; return false; }
                Vector3 origin = f.Tr.position + Vector3.up * (1.6f * 2.8f);
                FlakFire.FollowAll(s.Air, dt, 5f);
                if (now >= s.NextScan)
                {
                    s.NextScan = now + 0.2f;
                    for (int i = s.Air.Count - 1; i >= 0; i--)
                        if (s.Air[i].Go == null || (s.Air[i].Go.transform.position - origin).sqrMagnitude > 1200f * 1200f * 1.3f)
                            s.Air.RemoveAt(i);
                    FlakFire.Collect(s.Air, origin, 1200f);
                    GepardGun.Contact best = null;
                    float distance = float.MaxValue;
                    int rays = 4;
                    string side = Fraktion.Spielerseite(u.OwnerGo);
                    for (int i = 0; side != null && i < s.Air.Count; i++)
                    {
                        GepardGun.Contact c = s.Air[i];
                        if (c.Go == null || (c.Kind != 0 && (c.Src == null || c.Src.Name != "Mi-8 troops"))) continue;
                        bool hostile, friendly;
                        FlakFire.Allegiance(c, side, out hostile, out friendly);
                        if (!hostile || friendly) continue;
                        float d = (c.Pos - origin).sqrMagnitude;
                        if (d >= distance) continue;
                        if (rays-- <= 0) break;
                        if (!FlakFire.Airborne(c) || !Stinger.MercVisible(f.Ai, c, origin)) continue;
                        best = c; distance = d;
                    }
                    if (s.Target != best) s.Held = 0f;
                    s.Target = best;
                }
                if (s.Target == null || s.Target.Go == null) { s.Held = 0f; return false; }
                Hold(f, null, now); FaceDir(f, s.Target.Pos - f.Tr.position);
                if (!Planted(f, now) || !ReadArmed(f)) { s.Held = 0f; return true; }
                Drive(f, MainIdle, AddAim, PoseStand, now, true);
                s.Held = Mathf.Min(Stinger.LockSeconds, s.Held + dt);
                if (s.Held < Stinger.LockSeconds || now < f.NextShot) return true;
                int rounds, max;
                MercRounds(f, u.Fight, now, out rounds, out max);
                if (rounds == 0) { StartReload(f); return true; }
                if (rounds < 0 || CurrentItem(f) != Stinger.ItemId
                    || !Stinger.MercVisible(f.Ai, s.Target, origin)) { s.Held = 0f; return true; }
                if (!Stinger.LaunchMerc(s.Target, origin)) return true;
                _fBullets.SetValue(u.Fight.Weapon, rounds - 1);
                Drive(f, MainIdle, AddFire, PoseStand, now, true);
                f.NextShot = now + 8f; s.Held = 0f;
                return true;
            }
            finally { FrameProf.E(FrameProf.S_MercStingerT); }
        }
    }
}

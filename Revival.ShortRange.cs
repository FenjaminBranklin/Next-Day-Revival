// W AA5: ZU-23-2 short-range layer, using the existing Flak radar/crew/wire.
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class ShortRange
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void BindConfig(ConfigFile cfg)
        {
            Enabled = cfg.Bind("FlakZU23", "Enabled", true,
                "Two airfield ZU-23-2 guns for low helicopters and FPV/recon drones.");
        }
        internal static bool On { get { return Enabled == null || Enabled.Value; } }
        internal static bool Drone(GepardGun.Contact c) { return c.Kind == 2 || c.Kind == 3; }

        // Reuse arithmetic flight time (no Physics queries in laying).
        internal static Vector3 Intercept(Vector3 from, Vector3 p, Vector3 v, out float tof)
        {
            Vector3 d = p - from;
            float gravity = 9.81f * Flak.K;
            tof = MercAACore.FlightTime(d.x, d.y, d.z, v.x, v.y, v.z,
                ShortRangeCore.SpeedM * Flak.K, gravity);
            return p + v * tof + Vector3.up * (0.5f * gravity * tof * tof);
        }

        // Local presentation sleeps away from the battery. The master also
        // wakes near other players so a distant pilot gets authoritative fire.
        internal static bool Awake(Flak.Gun g, bool master)
        {
            if (!On) { g.Awake = false; return false; }
            if (g == Flak._manned) return true;
            if (Time.time < g.NextWake) return g.Awake;
            g.NextWake = Time.time + 0.5f;
            float radius = (ShortRangeCore.RangeM + 600f) * Flak.K;
            GameObject me = MapTools.LocalPlayer();
            g.Awake = me != null && (me.transform.position - g.Root.position).sqrMagnitude < radius * radius;
            if (!g.Awake && master)
            {
                List<GameObject> players = GepardCrew.Spieler();
                for (int i = 0; i < players.Count; i++)
                    if (players[i] != null && (players[i].transform.position - g.Root.position).sqrMagnitude < radius * radius)
                    { g.Awake = true; break; }
            }
            return g.Awake;
        }

        internal static void Control(Flak.Gun g, float dt)
        {
            MercAAPost operatorMerc = MercAA.Gun(g.Index);
            if (operatorMerc != null && operatorMerc.Peaceful && !Flak.Up(g.Gunner)) { FlakFire.Release(g); return; }
            bool gunner = MercAA.Held(g.Index) != null || Flak.Up(g.Gunner), loader = MercAA.Held(g.Index + 7) != null || Flak.Up(g.Loader);
            if (!gunner && !loader) { FlakFire.Release(g); return; }
            if (!g.Laying)
            {
                g.Laying = true;
                if (g.Rounds < 0) g.Rounds = ShortRangeCore.Magazine;
                g.NextLook = Time.time + (g.Index & 3) * 0.125f;
            }
            if (Time.time >= g.NextLook)
            {
                g.NextLook = Time.time + 0.5f;
                FlakFire.Search(g);
            }
            bool radar = MercAA.Directed(g);
            FlakFire.FollowAll(g.Air, dt, ShortRangeCore.Lag * (radar ? 1.5f : 1f));
            GepardGun.Contact t = g.Target;
            if (t == null || t.Go == null)
            {
                g.Engaged = false;
                g.Firing = false;
                g.ShortBurst = new ShortBurst();
                g.WantPitch = 20f;
                Flak.Publish(g, false, false);
                return;
            }
            Vector3 mid = Flak.Mid(g);
            float dist = Vector3.Distance(mid, t.Pos);
            bool drone = Drone(t);
            if (!g.Engaged)
            {
                g.Engaged = true;
                g.Held = 0f;
                g.ShortBurst = new ShortBurst();
                g.Err = FlakFire.Offset(t, mid, ShortRangeCore.Initial(dist, drone));
                g.LastVel = t.Vel;
                Flak.RaiseEngaging(g, t.Go);
            }
            float tof;
            Vector3 aim = Intercept(mid, t.Pos, t.Vel, out tof) + g.Err;
            float yaw, pitch;
            Flak.Angles(g, aim - mid, out yaw, out pitch);
            g.WantYaw = yaw;
            g.WantPitch = Mathf.Clamp(pitch, -10f, 90f);
            g.Aim = aim;
            g.FuzeRange = Mathf.Min(Vector3.Distance(mid, aim), ShortRangeCore.RangeM * Flak.K);
            g.Held += dt;
            bool ready = pitch >= -10f && pitch <= 90f && g.Held >= ShortRangeCore.Reaction(radar, drone)
                && g.Mode != FlakMode.HoldFire && !g.Reloading
                && Vector3.Angle(g.Cradle.forward, aim - mid) < 1.5f;
            bool corrected;
            bool shot = ShortRangeCore.Shot(ref g.ShortBurst, Time.time, ready, out corrected);
            if (corrected)
            {
                float size = ShortRangeCore.Correct(g.Err.magnitude, dist, radar, drone);
                g.Err = g.Err.sqrMagnitude > 0.001f ? g.Err.normalized * size : FlakFire.Offset(t, mid, size);
                g.Err += UnityEngine.Random.onUnitSphere * ShortRangeCore.Evasion((t.Vel - g.LastVel).magnitude, tof);
                g.LastVel = t.Vel;
            }
            g.Firing = ready && g.ShortBurst.Active;
            if (shot)
            {
                if (g.Rounds <= 0) Flak.StartReload(g, ShortRangeCore.ReloadSeconds * (gunner && loader ? 1f : 1.5f));
                else Flak.Fire(g, aim, true, g.FuzeRange, g.Hostile, false);
            }
            Flak.Publish(g, false, false);
        }

        internal static void Shoot(Flak.Gun g, Vector3 aim, bool aimValid, float fuze, bool live,
            List<GepardGun.Contact> contacts)
        {
            int barrel = g.Seq & 1;
            Transform muzzle = barrel == 0 ? g.Muzzle : g.MuzzleRight;
            if (muzzle == null) return;
            Vector3 dir = g.Cradle.forward;
            if (aimValid && Vector3.Angle(dir, aim - muzzle.position) < 1f) dir = (aim - muzzle.position).normalized;
            GepardShots.Spec spec = Specs[(g.Seq & 3) == 0 ? 1 : 0];
            spec.Tracer = Flak.B2(Flak.CfgTracers) && (g.Seq & 3) == 0;
            spec.PuffFx = Flak.B2(Flak.CfgPuffs) && (g.Seq & 3) == 0;
            MercAAPost merc = MercAA.Gun(g.Index);
            AirKills.NextShotCredit = g == Flak._manned ? Mathf.Max(0, Mercs.LocalActor)
                : merc != null ? Mathf.Max(0, merc.Actor) : 0;
            float life = Mathf.Clamp(fuze, 30f, ShortRangeCore.RangeM * Flak.K) / spec.Speed;
            try { GepardShots.Fire(spec, g.Owner, muzzle.position, dir, life, live, contacts); }
            finally { AirKills.NextShotCredit = -1; }
            g.Recoil = 1f;
            g.LastShot = Time.time;
            if (Flak.B2(Flak.CfgMuzzleFlash)) GepardFx.Muzzle(muzzle.position, dir);
            if (Flak.B2(Flak.CfgGunSound)) VehicleShotSound.Play(muzzle.position, false);
        }
        static readonly GepardShots.Spec[] Specs = MakeSpecs();
        static GepardShots.Spec[] MakeSpecs()
        {
            GepardShots.Spec[] s = new GepardShots.Spec[2];
            for (int i = 0; i < s.Length; i++)
            {
                s[i] = new GepardShots.Spec();
                s[i].Speed = ShortRangeCore.SpeedM * Flak.K;
                s[i].Gravity = -9.81f * Flak.K;
                s[i].Dispersion = 2.5f;
                s[i].Fuze = 0.4f * Flak.K;
                s[i].Splash = ShortRangeCore.SplashM * Flak.K;
                s[i].CollisionSeconds = 0.1f;
                s[i].HeliHits = ShortRangeCore.HeliHits;
                s[i].Flak = true;
                s[i].PuffScale = 0.4f;
            }
            return s;
        }
    }
}

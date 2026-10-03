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
                "Airfield ZU-23-2 for low aircraft and ground targets; earthworks limit depression.");
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

        // Local presentation sleeps away from the battery. The master wakes
        // for assigned mercs even with no player near the guns or roof.
        internal static bool Awake(Flak.Gun g, bool master)
        {
            if (!On) { g.Awake = false; return false; }
            if (g == Flak._manned) return true;
            if (MercAA.Gun(g.Index) != null || MercAA.Operator != null)
            { g.Awake = true; return true; }
            if (Time.time < g.NextWake) return g.Awake;
            g.NextWake = Time.time + 0.5f;
            if (master && (MercAA.Gun(g.Index) != null || MercAA.Operator != null))
            { g.Awake = true; return true; }
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
            if (ZuGround.Control(g, dt, gunner, loader)) return;
            GepardGun.Contact t = g.Target;
            if (ZuGround.Duty(g) == MercCrewPhase.Ground) t = null;
            if (t == null || t.Go == null)
            {
                g.Engaged = false;
                g.Firing = false;
                g.ShortBurst = new ShortBurst();
                // E L1: on the radar's cue, else the ready lay.
                if (!FlakFire.Cue(g, dt)) g.WantPitch = 20f;
                Flak.Publish(g, false, false);
                return;
            }
            Vector3 mid = Flak.Mid(g);
            float dist = Vector3.Distance(mid, t.Pos);
            bool drone = Drone(t);
            if (!g.Engaged)
            {
                g.Engaged = true;
                g.Held = FlakEngageCore.CueHeld(object.ReferenceEquals(t.Go, g.CueGo), g.CueHeld);
                g.CueGo = null;
                g.CueHeld = 0f;
                g.DrySaid = false;
                g.ShortBurst = new ShortBurst();
                g.Err = FlakFire.Offset(t, mid, ShortRangeCore.Initial(dist, drone));
                g.LastVel = t.Vel;
                Flak.RaiseEngaging(g, t.Go);
            }
            FlakFire.Dry(g);
            float tof;
            Vector3 aim = Intercept(mid, t.Pos, t.Vel, out tof) + g.Err;
            float yaw, pitch;
            Flak.Angles(g, aim - mid, out yaw, out pitch);
            g.WantYaw = yaw;
            g.WantPitch = Mathf.Clamp(pitch, ZuGroundCore.MinPitch, 90f);
            g.Aim = aim;
            g.FuzeRange = Mathf.Min(Vector3.Distance(mid, aim), ShortRangeCore.RangeM * Flak.K);
            g.Held += dt;
            bool ready = AARaidBalanceCore.CanFire(Vector3.Distance(mid, t.Go.transform.position), true)
                && FlakAmmo.Ready(g) && pitch >= ZuGroundCore.MinPitch && pitch <= 90f && g.Held >= ShortRangeCore.Reaction(radar, drone)
                && g.Mode != FlakMode.HoldFire && !g.Reloading
                && Vector3.Angle(g.Cradle.forward, aim - mid) < 1.5f;
            bool corrected;
            bool shot = ShortRangeCore.Shot(ref g.ShortBurst, Time.time, ready,
                MercAACore.CadenceScale(operatorMerc != null, radar), out corrected);
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
                if (g.Rounds <= 0) Flak.StartReload(g, ShortRangeCore.ReloadSeconds
                    * MercAACore.ReloadScale(operatorMerc != null, gunner && loader));
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
            AirKills.NextShotCredit = FlakAmmo.ShotActor(g);
            float life = Mathf.Clamp(fuze, 30f, ShortRangeCore.RangeM * Flak.K) / spec.Speed;
            try
            {
                if (live && g.Ammo.Limited)
                {
                    // Choose the same dispersion once for the authoritative
                    // round and its exact peer launch. Keep Spec.Exact false:
                    // that flag also selects the 52-K's heavy damage policy.
                    Vector2 spread = UnityEngine.Random.insideUnitCircle * (spec.Dispersion * 0.001f);
                    Vector3 right = Vector3.Cross(Vector3.up, dir);
                    if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                    right.Normalize();
                    dir = (dir + right * spread.x + Vector3.Cross(dir, right) * spread.y).normalized;
                    GepardShots.FireExact(spec, g.Owner, muzzle.position, dir * spec.Speed, life, live, contacts, 0f, spec.Gravity);
                }
                else GepardShots.Fire(spec, g.Owner, muzzle.position, dir, life, live, contacts);
            }
            finally { AirKills.NextShotCredit = -1; }
            if (live && g.Ammo.Limited) FlakNet.SendShot(g, muzzle.position, dir, life);
            g.Recoil = 1f;
            g.LastShot = Time.time;
            if (Flak.B2(Flak.CfgMuzzleFlash)) GepardFx.Muzzle(muzzle.position, dir);
            if (Flak.B2(Flak.CfgGunSound)) VehicleShotSound.Play(muzzle.position, false);
        }
        internal static void Replay(Flak.Gun g, Vector3 muzzle, Vector3 velocity, float life, float age, float gravity)
        {
            GepardShots.Spec spec = Specs[(g.Seq & 3) == 0 ? 1 : 0];
            spec.Tracer = Flak.B2(Flak.CfgTracers) && (g.Seq & 3) == 0;
            spec.PuffFx = Flak.B2(Flak.CfgPuffs) && (g.Seq & 3) == 0;
            GepardShots.FireExact(spec, g.Owner, muzzle, velocity, life, false, null, age, gravity);
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
                s[i].InfantryDamage = ZuGroundCore.Damage;
                s[i].Flak = true;
                s[i].PuffScale = 0.4f;
            }
            return s;
        }
    }
}

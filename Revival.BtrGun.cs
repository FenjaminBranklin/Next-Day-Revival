// Revival.BtrGun.cs - how the MTW (the game's BTR-80A) looks and sounds when
// its turret gun fires: the player-manned gun (Revival.CameraTurret.cs) and the
// NPC patrol and convoy BTRs (Revival.Patrol.cs, Patrol.Gun).
//
// WHAT WAS WRONG
// A BTR shot was two additive lines from the gunner's CAMERA (not the barrel)
// to the impact, a white stroke over the first metres as "muzzle flash", a
// generated noise burst, no smoke, no cases, no impact, a muzzle guessed half a
// metre in front of the turret's bounding box, and other players heard the
// shot but saw nothing.
//
// WHAT IT IS NOW - the game's own firearm effects, driven the way the Gepard's
// guns are (RevivalGepard.cs: GepardShots/GepardFx):
//   * muzzle   - measured on the turret mesh: the tip ring of the longest
//                barrel (the 14.5 mm KPVT), not a bounding-box guess;
//   * flash    - the donor weapon's own MuzzleFlash sub-prefab, played exactly
//                like NPC_FirearmWeaponController::OnShotMuzzleFlashPlay
//                (roll around the bore, world-space systems Emit a burst,
//                local ones restart), scaled up for 14.5 mm;
//   * smoke    - a gun-smoke puff off the muzzle (GepardFx);
//   * cases    - the game's pooled Capsule of the donor weapon, thrown out of
//                the turret's right side onto the hull (GepardFx fallback);
//   * sound    - the game's SVD report pitched down under the plugin's low
//                BTR report, so the heavy MG keeps its body;
//   * tracer   - the game's FirearmWeaponTracer prefab flying at its 1100 m/s
//                from the muzzle to the impact, every Nth round (mixed belt);
//   * impact   - PlayerFirearmWeaponController::OnShotImpactInstance with the
//                struck collider's tag (dirt, concrete, metal, blood...),
//                decal and ricochet sound included, plus 14.5 mm dust;
//   * recoil   - the turret mount kicks back along the bore and settles;
//   * cadence  - the KPVT's 550-600 rounds a minute, NPC gunners in short
//                bursts of 3 to 6.
// Every effect has its own switch in [BtrGun]. Photon: one small unreliable
// event per shot on the turret channel (action -3: view id, end point, hit,
// tracer); every peer rebuilds the whole shot on its own copy of the turret.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class BtrGun
    {
        // ------------------------------------------------------------ config

        static ConfigEntry<bool> CfgEnabled, CfgFlash, CfgSmoke, CfgCases, CfgSound,
            CfgSynthetic, CfgTracer, CfgImpact, CfgDecals, CfgDust, CfgRecoil,
            CfgCadence, CfgBursts, CfgSync;
        static ConfigEntry<int> CfgEffectWeapon, CfgSoundWeapon, CfgTracerEvery,
            CfgBurstMin, CfgBurstMax;
        static ConfigEntry<float> CfgFlashScale, CfgSoundPitch, CfgTracerWidth,
            CfgRecoilDistance, CfgRate;

        public static void BindConfig(ConfigFile cfg)
        {
            const string S = "BtrGun";
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "Realistic firing of the MTW (BTR-80A) turret gun, player and NPC: the game's "
                + "own firearm effects instead of the old line tracer. Off = the old look.");
            CfgFlash = cfg.Bind(S, "MuzzleFlash", true,
                "The game's muzzle flash (donor weapon's MuzzleFlash prefab) at the measured KPVT muzzle.");
            CfgFlashScale = cfg.Bind(S, "MuzzleFlashScale", 2.4f,
                "Size of that flash against the donor rifle's. 14.5 mm burns far more powder than 7.62 mm.");
            CfgSmoke = cfg.Bind(S, "Smoke", true, "Gun smoke drifting off the muzzle after each round.");
            CfgCases = cfg.Bind(S, "ShellEjection", true,
                "Spent cases thrown out of the turret's right side (the game's pooled Capsule of the donor weapon).");
            CfgSound = cfg.Bind(S, "NativeSound", true,
                "Play the game's own weapon report (SoundWeaponId), pitched down, for every round.");
            CfgSoundWeapon = cfg.Bind(S, "SoundWeaponId", 1010,
                "Game weapon whose shot sound is used. 1010 = SVD (7.62x54R), the heaviest native report.");
            CfgSoundPitch = cfg.Bind(S, "SoundPitch", 0.72f,
                "Pitch of that report. Below 1 makes the rifle crack read as a 14.5 mm heavy machine gun.");
            CfgSynthetic = cfg.Bind(S, "SyntheticBody", true,
                "Layer the plugin's low generated BTR report ([Turret] Sound) under the native one.");
            CfgEffectWeapon = cfg.Bind(S, "EffectWeaponId", 1023,
                "Game weapon whose muzzle flash, tracer and cases are used. 1023 = RPD, the native belt-fed MG.");
            CfgTracer = cfg.Bind(S, "Tracer", true,
                "The game's tracer round (FirearmWeaponTracer) from the muzzle to the impact.");
            CfgTracerEvery = cfg.Bind(S, "TracerEvery", 3,
                "One tracer every N rounds, like a mixed KPVT belt (B-32 with BZT). 1 = every round.");
            CfgTracerWidth = cfg.Bind(S, "TracerWidth", 2.2f,
                "Width of the tracer's trail against the rifle tracer's.");
            CfgImpact = cfg.Bind(S, "Impacts", true,
                "The game's own hit effects by surface (dust, sparks, splinters, blood) and impact sounds.");
            CfgDecals = cfg.Bind(S, "ImpactDecals", true, "Bullet-hole decals of those hit effects.");
            CfgDust = cfg.Bind(S, "HeavyImpactDust", true,
                "An extra dust puff at each impact: a 14.5 mm round throws more than a rifle bullet.");
            CfgRecoil = cfg.Bind(S, "Recoil", true,
                "The turret mount kicks back along the bore on every round and settles.");
            CfgRecoilDistance = cfg.Bind(S, "RecoilDistance", 0.035f,
                "Metres of that kick. Purely visual, it does not move the aim.");
            CfgCadence = cfg.Bind(S, "RealCadence", true,
                "The BTR gun fires at RateOfFire instead of [Turret] FireDelay (player and NPC).");
            CfgRate = cfg.Bind(S, "RateOfFire", 580f,
                "Rounds a minute with RealCadence. The KPVT is rated 550-600.");
            CfgBursts = cfg.Bind(S, "RealBursts", true,
                "NPC BTR gunners fire short bursts of BurstMin..BurstMax rounds instead of [Patrol] GunBurst.");
            CfgBurstMin = cfg.Bind(S, "BurstMin", 3, "Shortest NPC burst.");
            CfgBurstMax = cfg.Bind(S, "BurstMax", 6, "Longest NPC burst.");
            CfgSync = cfg.Bind(S, "NetworkSync", true,
                "Send every round to the other players (turret event, action -3) so they see flash, "
                + "tracer and impact on their copy of the BTR. Off = they only hear it, as before.");
        }

        public static bool Active { get { return CfgEnabled != null && CfgEnabled.Value; } }

        static bool On(ConfigEntry<bool> c) { return Active && c != null && c.Value; }

        /// <summary>Seconds between two BTR rounds.</summary>
        public static float ShotInterval(float configured)
        {
            if (!On(CfgCadence)) return configured;
            return 60f / Mathf.Clamp(CfgRate.Value, 60f, 1200f);
        }

        /// <summary>Length of the next NPC burst.</summary>
        public static int NpcBurst(int configured)
        {
            if (!On(CfgBursts)) return configured;
            int lo = Mathf.Max(1, CfgBurstMin.Value);
            int hi = Mathf.Max(lo, CfgBurstMax.Value);
            return UnityEngine.Random.Range(lo, hi + 1);
        }

        // ------------------------------------------------------------- state

        class Gun
        {
            public Transform Root;
            public Transform[] Turrets;
            public Vector3[] Base;       // localPosition of each turret before any kick
            public float Kick;           // 0..1, decays
            public int Rounds;
            public GameObject Holder;    // at the muzzle; carries the flash and the audio
            public GameObject Flash;
            public ParticleSystem[] WorldPs;
            public int[] WorldMin, WorldMax;
            public AudioSource Audio;
        }

        static readonly Dictionary<int, Gun> _guns = new Dictionary<int, Gun>();
        static readonly List<int> _dead = new List<int>();
        static readonly Dictionary<Mesh, Vector3> _tips = new Dictionary<Mesh, Vector3>();
        static float _nextSweep;

        static Gun For(Transform root, Transform[] turrets)
        {
            if (root == null) return null;
            int key = root.GetInstanceID();
            Gun g;
            if (_guns.TryGetValue(key, out g) && g.Root != null)
            {
                if (turrets != null && turrets.Length > 0 && turrets != g.Turrets
                    && (g.Turrets.Length == 0 || g.Turrets[0] == null)) Adopt(g, turrets);
                return g;
            }
            g = new Gun();
            g.Root = root;
            Adopt(g, turrets != null && turrets.Length > 0 ? turrets : Turret.FindTurrets(root));
            _guns[key] = g;
            return g;
        }

        static void Adopt(Gun g, Transform[] turrets)
        {
            g.Turrets = turrets ?? new Transform[0];
            g.Base = new Vector3[g.Turrets.Length];
            for (int i = 0; i < g.Turrets.Length; i++)
                if (g.Turrets[i] != null) g.Base[i] = g.Turrets[i].localPosition;
            g.Kick = 0f;
        }

        // ------------------------------------------------------------ muzzle

        /// <summary>Bore direction of the turret (local -Y, see Turret.LocalRotationFor).</summary>
        static Vector3 Bore(Transform turret)
        {
            return turret.TransformDirection(new Vector3(0f, -1f, 0f)).normalized;
        }

        /// <summary>
        /// The KPVT muzzle in the turret's own space, measured once per mesh:
        /// the vertices of the longest barrel's tip ring (minimum local y) and
        /// the middle of that ring. The BTR's turret and both barrels are one
        /// mesh on the `turret` object (REVERSE_ENGINEERING, BTR-80A), so this
        /// is the only place the real muzzle can be read from. An unreadable
        /// mesh falls back to the tip of its bounds at the bounds' mid height.
        /// </summary>
        static Vector3 LocalTip(Transform turret)
        {
            MeshFilter mf = turret.GetComponent<MeshFilter>();
            Mesh mesh = mf == null ? null : mf.sharedMesh;
            if (mesh == null) return new Vector3(0f, -1f, 0f);
            Vector3 tip;
            if (_tips.TryGetValue(mesh, out tip)) return tip;

            Bounds b = mesh.bounds;
            tip = new Vector3(b.center.x, b.min.y, b.center.z);
            string how = "bounds";
            try
            {
                Vector3[] v = mesh.vertices;
                if (v != null && v.Length > 0)
                {
                    float minY = float.MaxValue;
                    for (int i = 0; i < v.Length; i++) if (v[i].y < minY) minY = v[i].y;
                    float band = Mathf.Max(0.01f, b.size.y * 0.004f);
                    float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                    int n = 0;
                    for (int i = 0; i < v.Length; i++)
                    {
                        if (v[i].y > minY + band) continue;
                        x0 = Mathf.Min(x0, v[i].x); x1 = Mathf.Max(x1, v[i].x);
                        z0 = Mathf.Min(z0, v[i].z); z1 = Mathf.Max(z1, v[i].z);
                        n++;
                    }
                    // A barrel ring is small against the turret: anything wider
                    // than a fifth of it is a flat face, not a bore.
                    if (n > 0 && x1 - x0 < b.size.x * 0.2f && z1 - z0 < b.size.z * 0.2f)
                    {
                        tip = new Vector3((x0 + x1) * 0.5f, minY, (z0 + z1) * 0.5f);
                        how = "mesh, " + n + " tip vertices";
                    }
                }
            }
            catch (Exception) { }
            _tips[mesh] = tip;
            RevivalPlugin.L.LogInfo("BTR gun: muzzle of mesh " + mesh.name + " at " + tip
                + " (" + how + ").");
            return tip;
        }

        static Transform MeshTurret(Gun g)
        {
            Transform first = null;
            for (int i = 0; i < g.Turrets.Length; i++)
            {
                Transform t = g.Turrets[i];
                if (t == null || t.GetComponent<MeshFilter>() == null) continue;
                if (first == null) first = t;
                Renderer r = t.GetComponent<Renderer>();
                if (r != null && r.isVisible) return t;
            }
            return first;
        }

        /// <summary>World muzzle and bore of this BTR's gun, at the un-kicked mount.</summary>
        static bool Muzzle(Gun g, out Vector3 at, out Vector3 dir)
        {
            at = Vector3.zero;
            dir = Vector3.forward;
            Transform t = MeshTurret(g);
            if (t == null) return false;
            Vector3 local = LocalTip(t);
            at = t.TransformPoint(local);
            dir = Bore(t);
            // A kick that is still on the mount must not shift the muzzle.
            for (int i = 0; i < g.Turrets.Length; i++)
                if (g.Turrets[i] == t && t.parent != null)
                    at -= t.parent.TransformVector(t.localPosition - g.Base[i]);
            return true;
        }

        // -------------------------------------------------------------- shot

        /// <summary>
        /// One round of the BTR gun, called by whoever fired it (player gun or
        /// NPC gunner): all effects here, and the round sent to the other
        /// players. <paramref name="end"/> is the impact or the end of range.
        /// </summary>
        public static void Fire(Transform root, Transform[] turrets, Vector3 end, bool hit)
        {
            Gun g = For(root, turrets);
            if (g == null) return;
            g.Rounds++;
            int every = Mathf.Max(1, CfgTracerEvery.Value);
            bool tracer = g.Rounds % every == 1 % every;
            Vector3 muzzle = Play(g, end, hit, tracer);

            if (!On(CfgSync) || !Turret.Net.PublishBtrShot(root, end, hit, tracer))
                Turret.Net.PublishShot(muzzle, false);
        }

        /// <summary>A round another player's (or the master's NPC) BTR fired.</summary>
        public static void Remote(Transform root, Vector3 end, bool hit, bool tracer)
        {
            if (!Active)
            {
                VehicleShotSound.Play(root == null ? end : root.position, false);
                return;
            }
            Gun g = For(root, null);
            if (g == null) return;
            g.Rounds++;
            Play(g, end, hit, tracer);
        }

        static Vector3 Play(Gun g, Vector3 end, bool hit, bool tracer)
        {
            Vector3 muzzle, bore;
            if (!Muzzle(g, out muzzle, out bore)) muzzle = g.Root.position + Vector3.up * 2.5f;
            Vector3 dir = end - muzzle;
            dir = dir.sqrMagnitude > 1f ? dir.normalized : bore;

            try { if (On(CfgFlash)) PlayFlash(g, muzzle, dir); }
            catch (Exception ex) { Warn("flash", ex); }
            try { if (On(CfgSmoke)) GepardFx.Smoke(muzzle, dir, 1.3f); }
            catch (Exception ex) { Warn("smoke", ex); }
            try { if (On(CfgCases)) EjectCase(g, muzzle, dir); }
            catch (Exception ex) { Warn("cases", ex); }
            try { PlaySound(g, muzzle); }
            catch (Exception ex) { Warn("sound", ex); }
            try { if (tracer && On(CfgTracer)) PlayTracer(muzzle, dir, end); }
            catch (Exception ex) { Warn("tracer", ex); }
            try { if (hit && On(CfgImpact)) PlayImpact(g, muzzle, end); }
            catch (Exception ex) { Warn("impact", ex); }
            if (On(CfgRecoil)) g.Kick = 1f;
            return muzzle;
        }

        static readonly Dictionary<string, float> _warned = new Dictionary<string, float>();

        static void Warn(string what, Exception ex)
        {
            float next;
            if (_warned.TryGetValue(what, out next) && Time.time < next) return;
            _warned[what] = Time.time + 30f;
            RevivalPlugin.L.LogWarning("BTR gun " + what + ": " + ex.Message);
        }

        // ------------------------------------------------- the donor weapons

        class Donor
        {
            public GameObject Flash, Tracer;
            public AudioClip Shot;
            public bool Loaded;
        }

        static readonly Dictionary<int, Donor> _donors = new Dictionary<int, Donor>();

        static Donor Weapon(int id)
        {
            Donor d;
            if (_donors.TryGetValue(id, out d)) return d;
            d = new Donor();
            _donors[id] = d;
            try
            {
                object data = WeaponData.Get(id);
                if (data == null)
                {
                    RevivalPlugin.L.LogWarning("BTR gun: no firearm data for weapon " + id + ".");
                    return d;
                }
                Type t = data.GetType();
                Array subs = Get(t, data, "Weapon_SubPrefabs") as Array;
                d.Flash = Element(subs, ToInt(Get(t, data, "MuzzleFlash_Index"))) as GameObject;
                d.Tracer = Element(subs, ToInt(Get(t, data, "Tracer_Index"))) as GameObject;
                Array sounds = Get(t, data, "Weapon_Sounds") as Array;
                d.Shot = Element(sounds, ToInt(Get(t, data, "ShootSND_Index"))) as AudioClip;
                d.Loaded = true;
                RevivalPlugin.L.LogInfo("BTR gun: weapon " + id + " effects - flash "
                    + (d.Flash != null) + ", tracer " + (d.Tracer != null)
                    + ", shot sound " + (d.Shot != null) + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("BTR gun: weapon " + id + " effects unreadable - " + ex.Message);
            }
            return d;
        }

        static object Get(Type t, object o, string name)
        {
            FieldInfo f = AccessTools.Field(t, name);
            return f == null ? null : f.GetValue(o);
        }

        static object Element(Array a, int i)
        {
            if (a == null || i < 0 || i >= a.Length) return null;
            return a.GetValue(i);
        }

        /// <summary>An int field that may be a plain int or an ObscuredInt
        /// (see docs/ai/tasks/m72-law-raketenwerfer.md, section 5).</summary>
        static int ToInt(object v)
        {
            if (v == null) return -1;
            if (v is int) return (int)v;
            MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != typeof(int)) continue;
                ParameterInfo[] p = ms[i].GetParameters();
                if (p.Length == 1 && p[0].ParameterType == v.GetType())
                    return (int)ms[i].Invoke(null, new object[] { v });
            }
            try { return Convert.ToInt32(v); } catch (Exception) { return -1; }
        }

        // -------------------------------------------------------------- flash

        static GameObject Holder(Gun g)
        {
            if (g.Holder != null) return g.Holder;
            g.Holder = new GameObject("NDR BTR gun");
            return g.Holder;
        }

        /// <summary>
        /// The donor's flash, played like NPC_FirearmWeaponController does:
        /// its local +Z is the bore, it is rolled at random about it, world-
        /// space systems Emit a burst of their own size and the rest restart
        /// by toggling the object.
        /// </summary>
        static void PlayFlash(Gun g, Vector3 muzzle, Vector3 dir)
        {
            GameObject holder = Holder(g);
            holder.transform.position = muzzle;
            holder.transform.rotation = Quaternion.LookRotation(dir)
                * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 179f));

            if (g.Flash == null)
            {
                Donor d = Weapon(CfgEffectWeapon.Value);
                if (d.Flash == null)
                {
                    GepardFx.Muzzle(muzzle, dir);
                    return;
                }
                BuildFlash(g, holder, d.Flash);
            }
            float s = Mathf.Clamp(CfgFlashScale.Value, 0.2f, 8f);
            g.Flash.transform.localScale = new Vector3(s, s, s);
            for (int i = 0; i < g.WorldPs.Length; i++)
            {
                ParticleSystem ps = g.WorldPs[i];
                if (ps == null) continue;
                ps.transform.localScale = new Vector3(s, s, s);
                ps.Emit(UnityEngine.Random.Range(g.WorldMin[i], g.WorldMax[i] + 1));
            }
            g.Flash.SetActive(false);
            g.Flash.SetActive(true);
        }

        static void BuildFlash(Gun g, GameObject holder, GameObject prefab)
        {
            GameObject flash = (GameObject)UnityEngine.Object.Instantiate(prefab, holder.transform.position,
                holder.transform.rotation);
            flash.name = "NDR BTR muzzle flash";
            flash.transform.SetParent(holder.transform, false);
            flash.transform.localPosition = Vector3.zero;
            flash.transform.localRotation = Quaternion.identity;

            List<ParticleSystem> world = new List<ParticleSystem>();
            List<int> lo = new List<int>(), hi = new List<int>();
            ParticleSystem[] all = flash.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem ps = all[i];
                ParticleSystem.MainModule main = ps.main;
                main.simulationSpeed = 2f;                     // as the game does
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                if (main.simulationSpace != ParticleSystemSimulationSpace.World
                    || ps.transform == flash.transform) continue;
                // World-space smoke and sparks must not vanish when the flash
                // is toggled: they move to the holder and Emit a burst.
                int min = 1, max = 3;
                ParticleSystem.EmissionModule em = ps.emission;
                if (em.burstCount > 0)
                {
                    ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[em.burstCount];
                    em.GetBursts(bursts);
                    min = Mathf.Max(1, (int)bursts[0].minCount);
                    max = Mathf.Max(min, (int)bursts[0].maxCount);
                }
                main.playOnAwake = false;
                em.enabled = false;
                ps.transform.SetParent(holder.transform, true);
                ps.Play();
                world.Add(ps); lo.Add(min); hi.Add(max);
            }
            g.Flash = flash;
            g.WorldPs = world.ToArray();
            g.WorldMin = lo.ToArray();
            g.WorldMax = hi.ToArray();
        }

        // -------------------------------------------------------------- cases

        static Type _grmType;
        static MethodInfo _grmInstance, _getPooled, _hide;
        static bool _grmTried;

        static object Pools()
        {
            if (!_grmTried)
            {
                _grmTried = true;
                _grmType = RevivalPlugin.TypeByName("GameResourcesManager");
                if (_grmType != null)
                {
                    _grmInstance = AccessTools.PropertyGetter(_grmType, "Instance");
                    _getPooled = AccessTools.Method(_grmType, "GetPooled",
                        new Type[] { typeof(string), typeof(string) }, null);
                    _hide = AccessTools.Method(_grmType, "Hide",
                        new Type[] { typeof(GameObject), typeof(float) }, null);
                }
            }
            if (_grmInstance == null || _getPooled == null || _hide == null) return null;
            return _grmInstance.Invoke(null, null);
        }

        /// <summary>The case leaves the turret on its right, beside the
        /// mantlet, and drops onto the hull roof.</summary>
        static void EjectCase(Gun g, Vector3 muzzle, Vector3 dir)
        {
            Transform t = MeshTurret(g);
            Vector3 up = t == null ? Vector3.up : t.TransformDirection(new Vector3(0f, 0f, 1f)).normalized;
            Vector3 right = Vector3.Cross(up, dir).normalized;
            float back = t == null ? 2f : Vector3.Distance(t.position, muzzle) * 0.62f;
            Vector3 at = muzzle - dir * back + right * 0.45f - up * 0.1f;
            Vector3 v = right * UnityEngine.Random.Range(2.5f, 4f) + up * UnityEngine.Random.Range(0.8f, 1.8f)
                - dir * UnityEngine.Random.Range(0f, 0.8f);

            GameObject c = null;
            object pools = Pools();
            if (pools != null)
                c = _getPooled.Invoke(pools, new object[] { "Capsule",
                    CfgEffectWeapon.Value.ToString() }) as GameObject;
            if (c == null)
            {
                GepardFx.Casing(at, v, 0.16f);
                return;
            }
            c.SetActive(true);
            c.transform.position = at;
            c.transform.rotation = Quaternion.LookRotation(right, up);
            Rigidbody rb = c.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = v;
                rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 12f;
            }
            _hide.Invoke(pools, new object[] { c, UnityEngine.Random.Range(2f, 5f) });
        }

        // -------------------------------------------------------------- sound

        static void PlaySound(Gun g, Vector3 muzzle)
        {
            bool native = false;
            if (On(CfgSound) && RevivalPlugin.CfgTurretSound != null && RevivalPlugin.CfgTurretSound.Value)
            {
                Donor d = Weapon(CfgSoundWeapon.Value);
                if (d.Shot != null)
                {
                    GameObject holder = Holder(g);
                    holder.transform.position = muzzle;
                    if (g.Audio == null)
                    {
                        g.Audio = holder.AddComponent<AudioSource>();
                        g.Audio.playOnAwake = false;
                        g.Audio.spatialBlend = 1f;
                        g.Audio.rolloffMode = AudioRolloffMode.Logarithmic;
                        g.Audio.dopplerLevel = 0f;
                        g.Audio.minDistance = 14f;
                    }
                    g.Audio.maxDistance = RevivalPlugin.CfgTurretSoundRange == null ? 650f
                        : Mathf.Max(50f, RevivalPlugin.CfgTurretSoundRange.Value);
                    g.Audio.pitch = Mathf.Clamp(CfgSoundPitch.Value, 0.3f, 2f)
                        * UnityEngine.Random.Range(0.97f, 1.03f);
                    float vol = RevivalPlugin.CfgTurretSoundVolume == null ? 1f
                        : Mathf.Clamp01(RevivalPlugin.CfgTurretSoundVolume.Value);
                    g.Audio.PlayOneShot(d.Shot, vol);
                    native = true;
                }
            }
            if (!native || On(CfgSynthetic)) VehicleShotSound.Play(muzzle, false);
        }

        // ------------------------------------------------------------- tracer

        static Type _tracerType;
        static MethodInfo _setTrace;
        static readonly Color SpurKern = new Color(1.00f, 0.55f, 0.35f, 1f);
        static readonly Color SpurHof = new Color(1.00f, 0.25f, 0.10f, 1f);

        /// <summary>The game's tracer round, as OnShotTracerInstance builds it:
        /// the prefab at the muzzle along the bore, SetTraceVector(end point),
        /// and it flies there at 1100 m/s.</summary>
        static void PlayTracer(Vector3 muzzle, Vector3 dir, Vector3 end)
        {
            Donor d = Weapon(CfgEffectWeapon.Value);
            if (_tracerType == null)
            {
                _tracerType = RevivalPlugin.TypeByName("FirearmWeaponTracer");
                if (_tracerType != null)
                    _setTrace = AccessTools.Method(_tracerType, "SetTraceVector",
                        new Type[] { typeof(Vector3) }, null);
            }
            if (d.Tracer == null || _tracerType == null || _setTrace == null)
            {
                // No native tracer: the plugin's slim BTR streak, from the muzzle now.
                List<Vector3> bahn = new List<Vector3>();
                bahn.Add(muzzle + dir * 1.5f);
                bahn.Add(end);
                RocketHook.SpawnTracer(bahn, 0.30f, 0.12f, SpurHof, SpurHof, 0.10f);
                RocketHook.SpawnTracer(bahn, 0.12f, 0.05f, SpurKern, SpurHof, 0.16f);
                return;
            }
            GameObject go = (GameObject)UnityEngine.Object.Instantiate(d.Tracer, muzzle,
                Quaternion.LookRotation(dir));
            go.name = "NDR BTR tracer";
            float w = Mathf.Clamp(CfgTracerWidth.Value, 0.2f, 8f);
            TrailRenderer[] trails = go.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++) trails[i].widthMultiplier *= w;
            Component c = go.GetComponent(_tracerType);
            if (c != null) _setTrace.Invoke(c, new object[] { end });
            float flight = Vector3.Distance(muzzle, end) / 1100f;
            UnityEngine.Object.Destroy(go, flight + 2.5f);
        }

        // ------------------------------------------------------------- impact

        static MethodInfo _impact;
        static bool _impactTried;

        /// <summary>
        /// PlayerFirearmWeaponController::OnShotImpactInstance(string tag,
        /// Vector3 point, Vector3 normal, bool decal, bool sound): the pooled
        /// impact particles of the surface's tag, decal, impact and ricochet
        /// sound. The struck surface is found again with a short ray at the
        /// end point, so a remote peer needs only the point.
        /// </summary>
        static void PlayImpact(Gun g, Vector3 muzzle, Vector3 end)
        {
            Vector3 dir = end - muzzle;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            Vector3 normal = -dir;
            string tag = "Untagged";
            RaycastHit rh;
            if (Physics.Raycast(end - dir * 0.6f, dir, out rh, 1.2f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && (g.Root == null || !rh.transform.IsChildOf(g.Root)))
            {
                normal = rh.normal;
                end = rh.point;
                tag = rh.collider.tag;
            }
            bool flesh = tag == "RagdollBone" || tag == "BloodHead" || tag == "Animal";
            if (tag == "Vehicle") tag = "Metal";

            if (!_impactTried)
            {
                _impactTried = true;
                Type t = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                if (t != null)
                    _impact = AccessTools.Method(t, "OnShotImpactInstance", new Type[] {
                        typeof(string), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(bool) }, null);
                if (_impact == null || !_impact.IsStatic)
                {
                    _impact = null;
                    RevivalPlugin.L.LogWarning("BTR gun: OnShotImpactInstance(string, Vector3, Vector3, bool, bool) "
                        + "not found - plugin impacts only.");
                }
            }
            if (_impact != null)
                _impact.Invoke(null, new object[] { tag, end, normal, On(CfgDecals) && !flesh, true });
            else
                GepardFx.Impact(end, normal);
            if (On(CfgDust) && !flesh) GepardFx.Dust(end, normal, 1f);
        }

        // ------------------------------------------------------------- frames

        /// <summary>Update: the mount back at its rest before anything reads
        /// the turret (camera, aim, muzzle); decay; forget dead vehicles.</summary>
        public static void Tick()
        {
            if (_guns.Count == 0) return;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            foreach (KeyValuePair<int, Gun> kv in _guns)
            {
                Gun g = kv.Value;
                if (g.Root == null) { _dead.Add(kv.Key); continue; }
                if (g.Kick <= 0f) continue;
                for (int i = 0; i < g.Turrets.Length; i++)
                    if (g.Turrets[i] != null) g.Turrets[i].localPosition = g.Base[i];
                g.Kick = Mathf.Max(0f, g.Kick - dt * 14f);
            }
            if (Time.time >= _nextSweep)
            {
                _nextSweep = Time.time + 10f;
                foreach (KeyValuePair<int, Gun> kv in _guns)
                    if (kv.Value.Root == null && !_dead.Contains(kv.Key)) _dead.Add(kv.Key);
            }
            for (int i = 0; i < _dead.Count; i++)
            {
                Gun g;
                if (_guns.TryGetValue(_dead[i], out g) && g.Holder != null)
                    UnityEngine.Object.Destroy(g.Holder);
                _guns.Remove(_dead[i]);
            }
            _dead.Clear();
        }

        /// <summary>LateUpdate, after the camera: the kick drawn on the mount.
        /// Tick takes it off again next frame, so it never accumulates.</summary>
        public static void LateFrame()
        {
            if (_guns.Count == 0) return;
            float dist = CfgRecoilDistance == null ? 0f : Mathf.Clamp(CfgRecoilDistance.Value, 0f, 0.3f);
            foreach (KeyValuePair<int, Gun> kv in _guns)
            {
                Gun g = kv.Value;
                if (g.Root == null || g.Kick <= 0f) continue;
                // Sharp rearward, soft run-out: the kick's square.
                float k = g.Kick * g.Kick * dist;
                for (int i = 0; i < g.Turrets.Length; i++)
                {
                    Transform t = g.Turrets[i];
                    if (t == null || t.parent == null) continue;
                    Vector3 back = t.parent.InverseTransformVector(-Bore(t) * k);
                    t.localPosition = g.Base[i] + back;
                }
            }
        }
    }
}

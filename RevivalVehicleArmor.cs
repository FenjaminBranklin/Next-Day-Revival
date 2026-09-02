// Next Day: Survival - Revival Toolkit : vehicle armour balance
//
// One self-contained place that decides how many hits a MOD vehicle survives
// from each mod weapon. It exists because the two systems that decide a
// vehicle's fate live on opposite sides of the game:
//
//   * Explosions (the FPV drone, the M72 LAW, a tank shell) go through the
//     game's own ExplosionObject, which calls
//     VehicleGameSystem::ApplyDamage(damage, 14) ONCE per vehicle in radius.
//     For partType 14 the game removes damage * 9 from Durability (measured
//     from IL). A mod vehicle spawns with Durability 2000 (Prepare), so ANY
//     explosion (the weakest is the FPV drone at 550 -> 4950) already destroys
//     it in a single hit. That is exactly what we want for an APC and for
//     every ordinary world car, so those are left untouched. Only the TANK
//     needs to survive more, so the ApplyDamage prefix below REWRITES the
//     incoming damage for tanks only.
//
//   * The BTR autocannon ("APC Geschuetz") did NOT damage vehicles at all
//     (the mounted turret only hit NPCs/players; the AI patrol gun only hit
//     tanks). GunHit() is the source-side helper both call so the autocannon
//     eats a vehicle's Durability directly through ApplyDamage(perShot, 10).
//     The game rate-limits a vehicle to one accepted damage event every 0.3 s
//     (_canGetDamage), so kill time = hits * 0.3 s independent of fire rate;
//     the per-shot value is derived from a target time.
//
// The weapons keep their own damage numbers (anti-personnel value is
// untouched); only the durability portion is re-balanced here, per target.
//
// Isolation: this whole feature is this file plus three one-line seams in
// RevivalPlugin.cs (BindConfig, Install, and one GunHit call each in
// Turret.Fire and Patrol.PanzerSchaden). Nothing else is shared.

using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static class VehicleArmor
    {
        // ------------------------------------------------------------- config
        internal static ConfigEntry<float> CfgPool;
        internal static ConfigEntry<int> CfgTankFpvHits;
        internal static ConfigEntry<int> CfgTankLawHits;
        internal static ConfigEntry<int> CfgTankShellHits;
        internal static ConfigEntry<float> CfgGunTankSeconds;
        internal static ConfigEntry<float> CfgGunApcSeconds;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgPool = cfg.Bind("VehicleArmor", "Pool", 2000f,
                "Bezugs-Durability eines Mod-Fahrzeugs. Muss dem beim Spawn "
                + "gesetzten Wert entsprechen (Prepare: 2000). Aus diesem Wert "
                + "werden die Treffer- und Sekundenvorgaben unten in Schaden "
                + "umgerechnet.");
            CfgTankFpvHits = cfg.Bind("VehicleArmor", "TankFpvHits", 3,
                "Direkte FPV-Drohnentreffer, die ein Panzer aushaelt. Ein APC "
                + "und jedes andere Fahrzeug bleiben 1-Treffer (unveraendert).");
            CfgTankLawHits = cfg.Bind("VehicleArmor", "TankLawHits", 2,
                "M72-LAW-Schuesse, bis ein Panzer zerstoert ist. Ein APC bleibt "
                + "1-Schuss.");
            CfgTankShellHits = cfg.Bind("VehicleArmor", "TankShellHits", 2,
                "Panzergranaten (Spieler- oder KI-Panzer), bis ein anderer "
                + "Panzer zerstoert ist. Ein APC wird von einer Granate sofort "
                + "zerstoert (Standardverhalten, hier nicht angefasst).");
            CfgGunTankSeconds = cfg.Bind("VehicleArmor", "GunTankSeconds", 5f,
                "Dauerfeuer des BTR-Bordgeschuetzes in Sekunden, um einen "
                + "Panzer zu zerstoeren. Das Spiel nimmt hoechstens einen "
                + "Treffer je 0.3 s an, daher ist die Zeit weitgehend "
                + "feuerraten-unabhaengig. KI-Patrouillen feuern in Salven und "
                + "brauchen entsprechend laenger.");
            CfgGunApcSeconds = cfg.Bind("VehicleArmor", "GunApcSeconds", 3f,
                "Dauerfeuer des BTR-Bordgeschuetzes in Sekunden, um einen "
                + "anderen APC zu zerstoeren.");
        }

        // ---------------------------------------------------------- constants
        // partType values and the multipliers VehicleGameSystem::ApplyDamage
        // applies to Durability, read from IL:
        //   14 (explosion) -> Durability -= damage * 9
        //   10 (armour)    -> Durability -= damage * 1
        //    4 (bullet)    -> Durability -= damage * 3
        const int PART_EXPLOSION = 14;
        const int PART_ARMOUR = 10;
        const float EXPLOSION_MULT = 9f;
        // _canGetDamage cooldown between two accepted damage events.
        const float DAMAGE_COOLDOWN = 0.3f;
        // The M72 LAW blast damage RocketHook.Postfix hands to Detonate. Kept
        // in sync with that literal by hand (there is no config for it).
        const float LAW_EXPLOSION_DAMAGE = 900f;

        static MethodInfo _applyDamage;
        static bool _lookedUp;

        // ----------------------------------------------------------- install
        public static void Install(Harmony harmony)
        {
            try
            {
                Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (vgs == null)
                {
                    RevivalPlugin.L.LogWarning("VehicleArmor: VehicleGameSystem "
                        + "nicht gefunden - Fahrzeugpanzerung bleibt unveraendert.");
                    return;
                }

                MethodInfo apply = AccessTools.Method(vgs, "ApplyDamage",
                    new Type[] { typeof(float), typeof(int) }, null);
                if (apply == null)
                {
                    RevivalPlugin.L.LogWarning("VehicleArmor: ApplyDamage(float,int) "
                        + "fehlt - Fahrzeugpanzerung bleibt unveraendert.");
                    return;
                }

                harmony.Patch(apply,
                    new HarmonyMethod(typeof(VehicleArmor).GetMethod("Prefix")),
                    null, null, null, null);
                RevivalPlugin.L.LogInfo("VehicleArmor: Panzer-Explosionsschutz aktiv "
                    + "(FPV " + CfgTankFpvHits.Value + ", LAW " + CfgTankLawHits.Value
                    + ", Granate " + CfgTankShellHits.Value + " Treffer).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("VehicleArmor.Install: " + ex);
            }
        }

        // ------------------------------------------------- explosion re-balance
        /// <summary>
        /// Prefix on VehicleGameSystem::ApplyDamage. Only touches EXPLOSION
        /// damage (partType 14) against a TANK, and only for the recognised mod
        /// weapons. Everything else - APCs, world cars, bullets, the armour
        /// path, unrecognised explosions - runs unchanged, so vanilla one-hit
        /// behaviour is preserved for every vehicle except the buffed tank.
        /// </summary>
        public static bool Prefix(object __instance, ref float __0, int __1)
        {
            try
            {
                if (__1 != PART_EXPLOSION) return true;

                Component vgs = __instance as Component;
                if (vgs == null) return true;
                if (!Tank.IstPanzer(vgs.transform)) return true;   // only tanks

                int hits = HitsForExplosion(__0);
                if (hits <= 0) return true;                        // unrecognised

                float pool = CfgPool.Value;
                if (pool <= 0f) return true;

                // Durability we want this single hit to remove, converted back
                // through the game's x9 so ApplyDamage lands exactly on it.
                float loss = pool / hits;
                __0 = loss / EXPLOSION_MULT;
                return true;
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("VehicleArmor.Prefix: " + ex);
                return true;
            }
        }

        /// <summary>Maps an incoming explosion damage to the tank hit count, by
        /// matching the mod weapons' own blast values. 0 = not one of ours,
        /// leave it to vanilla.</summary>
        static int HitsForExplosion(float incoming)
        {
            if (Approx(incoming, RevivalPlugin.CfgDroneDamage.Value))
                return Mathf.Max(1, CfgTankFpvHits.Value);
            if (Approx(incoming, LAW_EXPLOSION_DAMAGE))
                return Mathf.Max(1, CfgTankLawHits.Value);
            if (Approx(incoming, RevivalPlugin.CfgTankExplosionDamage.Value))
                return Mathf.Max(1, CfgTankShellHits.Value);
            if (RevivalPlugin.CfgPatrolShellDamage != null
                && Approx(incoming, RevivalPlugin.CfgPatrolShellDamage.Value))
                return Mathf.Max(1, CfgTankShellHits.Value);
            return 0;
        }

        static bool Approx(float a, float b)
        {
            return a > 0f && Mathf.Abs(a - b) < 0.5f;
        }

        // ----------------------------------------------------- autocannon path
        /// <summary>
        /// Source-side hook for the BTR autocannon. The mounted turret
        /// (Turret.Fire) and the AI patrol gun (Patrol.PanzerSchaden) call this
        /// with whatever their shot struck. If it is a tank or an APC, the
        /// autocannon eats its Durability through the game's own armour path
        /// (partType 10) and true is returned so the caller stops. A tank
        /// SHOOTER never uses this - it fires an explosive shell instead - so
        /// shooterIsTank short-circuits to false and the caller keeps going.
        /// </summary>
        public static bool GunHit(GameObject struck, bool shooterIsTank)
        {
            try
            {
                if (shooterIsTank || struck == null) return false;
                if (!LookUp()) return false;

                Type vgsType = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (vgsType == null) return false;
                Component vehicle = struck.GetComponentInParent(vgsType);
                if (vehicle == null) return false;

                bool tank = Tank.IstPanzer(vehicle.transform);
                bool apc = !tank && IstApc(vehicle.transform);
                if (!tank && !apc) return false;      // world car: unchanged

                float seconds = tank ? CfgGunTankSeconds.Value : CfgGunApcSeconds.Value;
                if (seconds < DAMAGE_COOLDOWN) seconds = DAMAGE_COOLDOWN;
                float perShot = CfgPool.Value * DAMAGE_COOLDOWN / seconds;
                if (perShot <= 0f) return true;

                float before = GetFloat(vehicle, "Durability", -1f);
                _applyDamage.Invoke(vehicle, new object[] { perShot, PART_ARMOUR });
                float after = GetFloat(vehicle, "Durability", before);
                if (after < before)
                    RevivalPlugin.L.LogInfo("VehicleArmor: autocannon hit "
                        + (tank ? "tank " : "apc ") + before.ToString("0")
                        + " -> " + after.ToString("0") + ".");
                return true;
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("VehicleArmor.GunHit: " + ex);
                return false;
            }
        }

        /// <summary>A mod BTR that is not a tank. The spawn prefab is
        /// "btr-80a_spawn"; a tank keeps that in its name too, so IstPanzer is
        /// checked first by the caller. World cars do not carry "btr-80a".</summary>
        static bool IstApc(Transform root)
        {
            if (root == null) return false;
            return root.name.IndexOf("btr-80a", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool LookUp()
        {
            if (_lookedUp) return _applyDamage != null;
            _lookedUp = true;
            Type vgs = RevivalPlugin.TypeByName("VehicleGameSystem");
            _applyDamage = vgs == null ? null : AccessTools.Method(vgs, "ApplyDamage",
                new Type[] { typeof(float), typeof(int) }, null);
            if (_applyDamage == null)
                RevivalPlugin.L.LogWarning("VehicleArmor: ApplyDamage(float,int) fehlt - "
                    + "das Bordgeschuetz kann keinem Fahrzeug schaden.");
            return _applyDamage != null;
        }

        static float GetFloat(object obj, string field, float fallback)
        {
            try
            {
                FieldInfo f = AccessTools.Field(obj.GetType(), field);
                if (f == null) return fallback;
                object v = f.GetValue(obj);
                return v is float ? (float)v : fallback;
            }
            catch { return fallback; }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{

    /// <summary>
    /// Puts fire on the game's explosion. Sits as a postfix on
    /// `ExplosionObject::NetworkVisualizeExplode`, which is the RPC the game
    /// sends to everyone nearby - so this runs once on the client that fired
    /// and once on every other client with the plugin. Nothing of ours has to
    /// go over the wire.
    /// </summary>
    public static class FireHook
    {
        static bool _logged;

        public static void Postfix(object __instance)
        {
            try
            {
                Component c = __instance as Component;
                if (c == null) return;
                float radius = Radius(c);
                FireEffect.Spawn(c.transform.position, radius);
                if (!_logged && RevivalPlugin.L != null)
                {
                    _logged = true;
                    RevivalPlugin.L.LogInfo("Feuer: erste Explosion mit Radius "
                                            + radius + " m ausgeschmueckt.");
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Feuer fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// The blast radius of that explosion, so a grenade does not look like a
        /// 125 mm shell. The field may be an Obscured type - then the implicit
        /// conversion to float is asked for by name, the same way WeaponData
        /// reads the game's weapon values.
        /// </summary>
        static float Radius(Component c)
        {
            try
            {
                FieldInfo f = AccessTools.Field(c.GetType(), "ExplodeDamageRadius");
                if (f == null) return 6f;
                object v = f.GetValue(c);
                if (v == null) return 6f;
                if (v is float) return (float)v;
                MethodInfo[] ms = v.GetType().GetMethods(BindingFlags.Public
                                                         | BindingFlags.Static);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float))
                        return (float)ms[i].Invoke(null, new object[] { v });
                return 6f;
            }
            catch { return 6f; }
        }
    }

    /// <summary>
    /// Gives every destroyed driveable vehicle the patrol wreck presentation
    /// and makes remaining passengers take five health points per second.
    ///
    /// The damage is sent only by the vehicle owner. Every client runs the
    /// VehicleGameSystem update for its local copy, so omitting that gate would
    /// multiply the damage by the number of connected clients.
    /// </summary>
    public static class VehicleWreck
    {
        const float DamagePerSecond = 5f;

        static FieldInfo _durability;
        static FieldInfo _passengers;
        static MethodInfo _sameOwner;
        static MethodInfo _damagePassengers;
        static bool _damageLogged;

        public static void Install(Harmony harmony)
        {
            try
            {
                Type vehicle = RevivalPlugin.TypeByName("VehicleGameSystem");
                if (vehicle == null)
                {
                    RevivalPlugin.L.LogWarning("Vehicle wreck: VehicleGameSystem not found.");
                    return;
                }

                MethodInfo update = AccessTools.Method(vehicle, "Update", null, null);
                _durability = AccessTools.Field(vehicle, "Durability");
                _passengers = AccessTools.Field(vehicle, "Passengers");
                _sameOwner = AccessTools.Method(vehicle, "IsSameOwnerId", null, null);
                _damagePassengers = AccessTools.Method(vehicle,
                    "SetDamageToAllPassengers", new Type[] { typeof(float) }, null);
                if (update == null || _durability == null || _passengers == null)
                {
                    RevivalPlugin.L.LogWarning("Vehicle wreck: update, durability or "
                        + "passenger data not found.");
                    return;
                }

                harmony.Patch(update, null,
                    new HarmonyMethod(typeof(VehicleWreck).GetMethod("UpdatePostfix")),
                    null, null, null);
                RevivalPlugin.L.LogInfo("Vehicle wreck: patrol fire and 5 percent per "
                    + "second passenger damage active.");
                if (_sameOwner == null || _damagePassengers == null)
                    RevivalPlugin.L.LogWarning("Vehicle wreck: passenger damage path "
                        + "is incomplete; fire remains active.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Vehicle wreck install: " + ex);
            }
        }

        public static void UpdatePostfix(object __instance)
        {
            Component vehicle = __instance as Component;
            if (vehicle == null) return;
            try
            {
                if ((float)_durability.GetValue(__instance) > 0f)
                {
                    WreckBurnState oldState = vehicle.GetComponent<WreckBurnState>();
                    if (oldState != null)
                    {
                        Transform oldFire = vehicle.transform.Find(FireEffect.WreckName);
                        if (oldFire != null) UnityEngine.Object.Destroy(oldFire.gameObject);
                        UnityEngine.Object.Destroy(oldState);
                    }
                    return;
                }

                FireEffect.SpawnWreck(vehicle.gameObject,
                                     Tank.IstPanzer(vehicle.transform));

                WreckBurnState state = vehicle.GetComponent<WreckBurnState>();
                if (state == null) state = vehicle.gameObject.AddComponent<WreckBurnState>();

                Array passengers = _passengers.GetValue(__instance) as Array;
                if (!HasPassengers(passengers))
                {
                    state.NextDamage = 0f;
                    return;
                }

                if (state.NextDamage <= 0f)
                {
                    state.NextDamage = Time.time + 1f;
                    return;
                }
                if (Time.time < state.NextDamage) return;
                state.NextDamage = Time.time + 1f;

                if (_sameOwner == null || _damagePassengers == null) return;
                if (!(bool)_sameOwner.Invoke(__instance, null)) return;

                _damagePassengers.Invoke(__instance,
                    new object[] { DamagePerSecond });
                if (!_damageLogged && RevivalPlugin.L != null)
                {
                    _damageLogged = true;
                    RevivalPlugin.L.LogInfo("Vehicle wreck: first passenger burn tick "
                        + "sent (5 health).");
                }
            }
            catch (Exception ex)
            {
                WreckBurnState state = vehicle.GetComponent<WreckBurnState>();
                if (state != null && state.ErrorLogged) return;
                if (state != null) state.ErrorLogged = true;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Vehicle wreck update: " + ex.Message);
            }
        }

        static bool HasPassengers(Array passengers)
        {
            if (passengers == null) return false;
            for (int i = 0; i < passengers.Length; i++)
            {
                GameObject passenger = passengers.GetValue(i) as GameObject;
                if (passenger != null) return true;
            }
            return false;
        }
    }

    public sealed class WreckBurnState : MonoBehaviour
    {
        public float NextDamage;
        public bool ErrorLogged;
    }

    /// <summary>
    /// The fire itself: four particle systems and one light, all built at
    /// runtime. No asset and no Blender - a flame is a bright blob that grows,
    /// turns red and then goes transparent, and that is a texture of 64 by 64
    /// pixels and a gradient.
    ///
    /// Two materials for all of it, not one per explosion. The lesson is the
    /// one from the tracer (0.5.2): `Destroy(go)` does NOT take a material
    /// created at runtime with it, and explosions happen more often than LAW
    /// shots. `HideAndDontSave` carries them over a scene change.
    ///
    /// UNGEPRUEFT im Spiel - `Shader.Find` liefert nur, was auch im Build ist.
    /// "Particles/Additive" ist dieselbe Wahl wie bei der Leuchtspur.
    /// </summary>
    public static class FireEffect
    {
        internal const string WreckName = "NDR Wrackfeuer";

        static Material _additive;
        static Material _blended;
        static Texture2D _blob;
        static bool _noShader;

        public static void Spawn(Vector3 point, float radius)
        {
            if (RevivalPlugin.CfgFire == null || !RevivalPlugin.CfgFire.Value) return;
            if (_noShader) return;

            float scale = RevivalPlugin.CfgFireScale == null
                ? 1f : Mathf.Max(0.1f, RevivalPlugin.CfgFireScale.Value);
            float r = Mathf.Clamp(radius, 1.5f, 20f) * scale;

            Material add = Additive();
            Material blend = Blended();
            if (add == null || blend == null)
            {
                _noShader = true;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Feuer: kein Partikelshader im Build "
                        + "gefunden - die Explosion bleibt, wie sie war.");
                return;
            }

            GameObject root = new GameObject("NDR Feuerball");
            root.transform.position = point;

            Ball(root, r, add);
            Zungen(root, r, add);
            Funken(root, r, add);
            Rauch(root, r, blend);
            Blitz(root, r);

            UnityEngine.Object.Destroy(root, 8f);
        }

        /// <summary>
        /// Long-lived fire for a destroyed vehicle. The game's own
        /// DamageSmoke remains untouched near the hull; this adds the missing
        /// flames and the high column above it. The root is parented to the
        /// vehicle and has no timer. Patrol cleanup removes it with the wreck;
        /// the vehicle hook removes it when an ordinary vehicle respawns.
        /// </summary>
        public static void SpawnWreck(GameObject vehicle, bool tank)
        {
            if (vehicle == null || _noShader) return;
            if (vehicle.transform.Find(WreckName) != null) return;

            Material add = Additive();
            Material blend = Blended();
            if (add == null || blend == null)
            {
                _noShader = true;
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Wrackfeuer: kein Partikelshader im "
                        + "Build gefunden - es bleibt beim DamageSmoke des Spiels.");
                return;
            }

            GameObject root = new GameObject(WreckName);
            root.transform.position = vehicle.transform.position
                                    + Vector3.up * (tank ? 1.9f : 2.1f);
            root.transform.rotation = Quaternion.identity;
            root.transform.parent = vehicle.transform;

            WrackFlammen(root, add, -0.75f, tank ? 1.15f : 1.25f);
            WrackFlammen(root, add,  0.75f, tank ? 1.15f : 1.25f);
            WrackFeuerkrone(root, add, tank ? 1.35f : 1.50f);
            WrackRauch(root, blend, tank ? 1.15f : 1.30f);
            WrackLicht(root, tank ? 34f : 38f);

            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("Vehicle: tall smoke and fire attached to "
                    + (tank ? "tank" : "vehicle") + " wreck.");
        }

        /// <summary>One of two continuously burning patches on the deck.</summary>
        static void WrackFlammen(GameObject root, Material mat, float x, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackflammen", mat, true);
            ps.transform.localPosition = new Vector3(x, 0f, 0f);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 2f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.65f, 1.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 2.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.75f, r * 1.65f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.88f, 0.38f, 1f),
                new Color(1.00f, 0.30f, 0.03f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.10f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 110;

            Kegel(ps, r * 0.55f, 24f);
            Dauer(ps, 28f);
            Farbverlauf(ps, false);
            Groesse(ps, 0.80f, 0.08f);
            ps.Play();
        }

        /// <summary>
        /// The high part of the fire. The two deck patches give the wreck a
        /// burning base; this narrower and faster system throws visible flame
        /// tongues well above the turret or troop compartment.
        /// </summary>
        static void WrackFeuerkrone(GameObject root, Material mat, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackfeuerkrone", mat, true);
            ps.transform.localPosition = Vector3.up * 0.35f;

            ParticleSystem.MainModule main = ps.main;
            main.duration = 3f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 4.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.8f, 6.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 1.2f, r * 2.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.96f, 0.60f, 1f),
                new Color(1.00f, 0.24f, 0.02f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.18f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 190;

            Kegel(ps, r * 0.75f, 17f);
            Dauer(ps, 38f);
            Farbverlauf(ps, false);
            Groesse(ps, 0.85f, 0.06f);
            ps.Play();
        }

        /// <summary>
        /// The landmark: dark smoke rises roughly 140 to 300 metres before it
        /// fades, widening into a column that can be read from kilometres away.
        /// Ten particles per second keep that scale affordable even when all
        /// four patrol vehicles are burning at once.
        /// </summary>
        static void WrackRauch(GameObject root, Material mat, float r)
        {
            ParticleSystem ps = Neu(root, "Wrackrauchsaule", mat, false);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 16f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(18f, 28f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(7.5f, 11f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 2.2f, r * 4.5f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.06f, 0.055f, 0.05f, 0.98f),
                new Color(0.18f, 0.17f, 0.16f, 0.92f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.02f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 320;

            Kegel(ps, r * 1.15f, 7f);
            Dauer(ps, 10f);
            WrackRauchFarbe(ps);
            Groesse(ps, 0.55f, 3.10f);

            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.18f, 0.18f);
            ps.Play();
        }

        /// <summary>Opaque low down, slowly greyer, transparent only at the top.</summary>
        static void WrackRauchFarbe(ParticleSystem ps)
        {
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.07f, 0.06f, 0.055f), 0.00f),
                    new GradientColorKey(new Color(0.10f, 0.09f, 0.08f), 0.18f),
                    new GradientColorKey(new Color(0.17f, 0.16f, 0.15f), 0.62f),
                    new GradientColorKey(new Color(0.30f, 0.29f, 0.28f), 1.00f) },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0.00f, 0.00f),
                    new GradientAlphaKey(0.86f, 0.06f),
                    new GradientAlphaKey(0.72f, 0.68f),
                    new GradientAlphaKey(0.00f, 1.00f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        /// <summary>Warm light under the smoke; the particles provide motion.</summary>
        static void WrackLicht(GameObject root, float range)
        {
            GameObject go = new GameObject("Wrackglut");
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.up * 0.6f;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.40f, 0.10f, 1f);
            light.range = range;
            light.intensity = 4.0f;
            light.shadows = LightShadows.None;
        }

        // -------------------------------------------------- die fuenf Teile

        /// <summary>The bang itself: bright, fast, gone in half a second.</summary>
        static void Ball(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Ball", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.30f, 0.65f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.6f, r * 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.55f, r * 1.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.92f, 0.62f, 1f), new Color(1.00f, 0.62f, 0.16f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.12f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;

            Kugel(ps, r * 0.22f);
            Ausbruch(ps, 34);
            Farbverlauf(ps, false);
            Groesse(ps, 0.45f, 1.25f);
        }

        /// <summary>Tongues that stand and climb after the bang.</summary>
        static void Zungen(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Zungen", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.55f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.35f, r * 0.80f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.75f, 0.30f, 1f), new Color(1.00f, 0.40f, 0.08f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.28f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;

            Kegel(ps, r * 0.35f, 22f);
            Ausbruch(ps, 18);
            Farbverlauf(ps, false);
            Groesse(ps, 0.70f, 0.15f);
        }

        /// <summary>Sparks. Small, fast, and the only part that falls.</summary>
        static void Funken(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Funken", mat, true);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.4f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 1.2f, r * 3.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.03f, r * 0.08f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.95f, 0.70f, 1f), new Color(1.00f, 0.55f, 0.15f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(1.1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 160;

            Kugel(ps, r * 0.15f);
            Ausbruch(ps, 46);
            Farbverlauf(ps, false);
            Groesse(ps, 1.00f, 0.25f);
        }

        /// <summary>What is left over and stands in the air for a while.</summary>
        static void Rauch(GameObject root, float r, Material mat)
        {
            ParticleSystem ps = Neu(root, "Rauch", mat, false);
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.45f);
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.90f, r * 1.80f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.18f, 0.16f, 0.15f, 1f), new Color(0.42f, 0.39f, 0.36f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.06f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;

            Kegel(ps, r * 0.5f, 28f);
            Ausbruch(ps, 22);
            Farbverlauf(ps, true);
            Groesse(ps, 0.60f, 1.80f);

            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.7f, 0.7f);
        }

        /// <summary>
        /// A short flash of light. Its own child object, because NdrFlash
        /// destroys what it sits on when it is done - on the root that would
        /// take the fire with it.
        /// </summary>
        static void Blitz(GameObject root, float r)
        {
            GameObject go = new GameObject("Blitz");
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.up * (r * 0.25f);

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.38f, 1f);
            light.range = Mathf.Clamp(r * 5f, 8f, 90f);
            light.intensity = 7f;
            light.shadows = LightShadows.None;

            NdrFlash flash = go.AddComponent<NdrFlash>();
            flash.Life = 0.45f;
        }

        // ------------------------------------------------------- Bausteine

        static ParticleSystem Neu(GameObject root, string name, Material mat, bool vorn)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = root.transform;
            go.transform.localPosition = Vector3.zero;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            if (r != null)
            {
                // sharedMaterial, nicht material: `material` legt fuer JEDES
                // System eine eigene Kopie an, und die bleibt beim Destroy liegen.
                r.sharedMaterial = mat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.sortingFudge = vorn ? -2f : 0f;
            }
            return ps;
        }

        static void Ausbruch(ParticleSystem ps, int anzahl)
        {
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[1];
            bursts[0] = new ParticleSystem.Burst(0f, (short)anzahl);
            em.SetBursts(bursts);
        }

        static void Dauer(ParticleSystem ps, float proSekunde)
        {
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(proSekunde);
        }

        static void Kugel(ParticleSystem ps, float radius)
        {
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = Mathf.Max(0.05f, radius);
        }

        static void Kegel(ParticleSystem ps, float radius, float winkel)
        {
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.radius = Mathf.Max(0.05f, radius);
            sh.angle = winkel;
            sh.rotation = new Vector3(-90f, 0f, 0f);   // Kegel nach oben
        }

        /// <summary>
        /// White to yellow to orange to dark red, and out. That order is what
        /// makes a fire look like a fire and not like coloured dust.
        /// </summary>
        static void Farbverlauf(ParticleSystem ps, bool rauch)
        {
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;

            Gradient g = new Gradient();
            if (rauch)
            {
                g.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(0.55f, 0.42f, 0.30f), 0.00f),
                        new GradientColorKey(new Color(0.32f, 0.30f, 0.28f), 0.35f),
                        new GradientColorKey(new Color(0.22f, 0.21f, 0.20f), 1.00f) },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(0.00f, 0.00f),
                        new GradientAlphaKey(0.55f, 0.18f),
                        new GradientAlphaKey(0.35f, 0.60f),
                        new GradientAlphaKey(0.00f, 1.00f) });
            }
            else
            {
                g.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(1.00f, 0.98f, 0.85f), 0.00f),
                        new GradientColorKey(new Color(1.00f, 0.80f, 0.30f), 0.22f),
                        new GradientColorKey(new Color(1.00f, 0.42f, 0.08f), 0.60f),
                        new GradientColorKey(new Color(0.45f, 0.09f, 0.02f), 1.00f) },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(1.00f, 0.00f),
                        new GradientAlphaKey(0.95f, 0.45f),
                        new GradientAlphaKey(0.00f, 1.00f) });
            }
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        static void Groesse(ParticleSystem ps, float anfang, float ende)
        {
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            AnimationCurve curve = new AnimationCurve();
            curve.AddKey(0.00f, anfang);
            curve.AddKey(0.30f, Mathf.Max(anfang, ende) * 0.95f);
            curve.AddKey(1.00f, ende);
            size.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        // ------------------------------------------------------ Werkstoffe

        static Material Additive()
        {
            if (_additive != null) return _additive;
            Shader s = Finde(new string[] {
                "Particles/Additive", "Legacy Shaders/Particles/Additive",
                "Mobile/Particles/Additive" });
            if (s == null) return null;
            _additive = Werkstoff(s);
            return _additive;
        }

        static Material Blended()
        {
            if (_blended != null) return _blended;
            Shader s = Finde(new string[] {
                "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended",
                "Mobile/Particles/Alpha Blended", "Particles/Additive" });
            if (s == null) return null;
            _blended = Werkstoff(s);
            return _blended;
        }

        static Shader Finde(string[] namen)
        {
            for (int i = 0; i < namen.Length; i++)
            {
                Shader s = Shader.Find(namen[i]);
                if (s != null) return s;
            }
            return null;
        }

        static Material Werkstoff(Shader s)
        {
            Material m = new Material(s);
            Texture2D t = Blob();
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
            if (m.HasProperty("_TintColor"))
                m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        /// <summary>
        /// One soft blob, 64 by 64. Everything else - flame, spark, smoke - is
        /// this same blob in a different colour and a different size. A texture
        /// that lives in the plugin needs no asset file, no generator and no
        /// entry in verify.py.
        /// </summary>
        static Texture2D Blob()
        {
            if (_blob != null) return _blob;
            const int N = 64;
            Texture2D t = new Texture2D(N, N, TextureFormat.ARGB32, false);
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f;
                    float dy = (y + 0.5f) / N * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);              // weiche Kante
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.hideFlags = HideFlags.HideAndDontSave;
            _blob = t;
            return _blob;
        }
    }

    /// <summary>
    /// Fades a light out and then removes the object it sits on. A
    /// MonoBehaviour and not a coroutine: the fire has no MonoBehaviour of its
    /// own to hang one on, and the plugin's own one would keep the coroutine
    /// alive across a scene change.
    /// </summary>
    public class NdrFlash : MonoBehaviour
    {
        public float Life = 0.45f;

        float _t;
        float _start;
        Light _light;

        void Awake()
        {
            _light = GetComponent<Light>();
            if (_light != null) _start = _light.intensity;
        }

        void Update()
        {
            if (_light == null) { UnityEngine.Object.Destroy(gameObject); return; }
            _t += Time.deltaTime;
            if (_t >= Life) { UnityEngine.Object.Destroy(gameObject); return; }
            float k = 1f - _t / Life;
            _light.intensity = _start * k * k * (0.78f + 0.22f * Mathf.Sin(_t * 70f));
        }
    }
}

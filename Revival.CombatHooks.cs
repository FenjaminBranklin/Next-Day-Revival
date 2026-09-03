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
    /// Haengt die vorhandene Granatenexplosion an den Hitscan-Treffer der LAW.
    /// Alle Spieltypen bleiben absichtlich ueber Reflection angebunden.
    /// </summary>
    public static class RocketHook
    {
        const int LAW_ID = 1162;
        const string EXPLOSION_PREFAB = "PlayerDataPrefabs/Throw/1403_Throw";
        static bool _loggedId;

        public static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                object weaponData = GetField(__instance, "_weaponFirearmData");
                if (weaponData == null) return;

                int itemId = ObscuredInt(GetField(weaponData, "ItemID"));
                if (itemId != LAW_ID) return;
                if (!_loggedId)
                {
                    _loggedId = true;
                    RevivalPlugin.L.LogInfo("M72 LAW erkannt: ItemID " + itemId);
                }

                Transform cameraTransform = GetField(__instance, "MainCamera") as Transform;
                if (cameraTransform == null)
                    throw new InvalidOperationException("MainCamera ist null oder kein Transform.");

                float maximumRange = ObscuredFloat(GetField(weaponData, "MaximumRange"));
                Vector3 hitPoint, hitNormal;
                List<Vector3> trajectory;
                Vector3 launchPoint = cameraTransform.position
                    + cameraTransform.forward * 0.85f - cameraTransform.up * 0.34f;
                bool didHit = TraceTrajectory(launchPoint, cameraTransform.forward,
                                              maximumRange, out hitPoint, out hitNormal,
                                              out trajectory);
                SpawnTracer(trajectory);
                if (!didHit) return;

                Detonate(hitPoint + hitNormal * 0.15f, 900f, 12f, 3f);
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("M72-LAW-Einschlagexplosion fehlgeschlagen: " + ex);
            }
        }

        /// <summary>
        /// Zuendet eine Sprenggranate an einem Punkt. Herausgeloest, weil das
        /// Geschuetz des BTR dieselben sechs Schritte braucht - Prefab ueber
        /// PhotonNetwork erzeugen, Physik ruhigstellen, Collider auf
        /// IgnoreLocalPlayer, dann SetExplosionData und StartExplosion.
        ///
        /// `ExplosionObject::Explode` prueft `photonView.isMine`; ein blosses
        /// Object.Instantiate reicht deshalb nicht.
        /// </summary>
        internal static void Detonate(Vector3 point, float damage, float radius, float lifeTime)
        {
            GameObject spawned = PhotonInstantiate(EXPLOSION_PREFAB, point,
                                                   Quaternion.identity, (byte)0);
            if (spawned == null)
                throw new InvalidOperationException("PhotonNetwork.Instantiate lieferte null.");

            Type bodyType = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
            Component body = bodyType == null ? null : spawned.GetComponent(bodyType);
            if (body == null && bodyType != null)
                body = spawned.GetComponentInChildren(bodyType);
            if (body != null)
            {
                SetProperty(body, "velocity", Vector3.zero);
                SetProperty(body, "angularVelocity", Vector3.zero);
                SetProperty(body, "useGravity", false);
                SetProperty(body, "isKinematic", true);
            }

            int ignoreLayer = LayerMask.NameToLayer("IgnoreLocalPlayer");
            Type colliderType = RevivalPlugin.TypeByName("UnityEngine.Collider");
            Component[] colliders = colliderType == null
                ? new Component[0] : spawned.GetComponentsInChildren(colliderType, true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].gameObject.layer = ignoreLayer;

            Type explosionType = RevivalPlugin.TypeByName("ExplosionObject");
            if (explosionType == null)
                throw new MissingMemberException("ExplosionObject nicht gefunden.");
            Component explosion = spawned.GetComponent(explosionType);
            if (explosion == null)
                throw new MissingMemberException("ExplosionObject fehlt am Granaten-Prefab.");

            MethodInfo setData = AccessTools.Method(explosionType, "SetExplosionData",
                new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) }, null);
            MethodInfo start = AccessTools.Method(explosionType, "StartExplosion",
                new Type[] { typeof(float) }, null);
            if (setData == null || start == null)
                throw new MissingMethodException("ExplosionObject-Methoden nicht gefunden.");

            setData.Invoke(explosion, new object[] { damage, radius, lifeTime, 0f });
            IEnumerator routine = start.Invoke(explosion, new object[] { 0.02f }) as IEnumerator;
            MonoBehaviour behaviour = explosion as MonoBehaviour;
            if (routine == null || behaviour == null)
                throw new InvalidOperationException("StartExplosion lieferte keine Coroutine.");
            behaviour.StartCoroutine(routine);
        }

        static bool TraceTrajectory(Vector3 origin, Vector3 direction, float range,
                                    out Vector3 point, out Vector3 normal,
                                    out List<Vector3> path)
        {
            const float speed = 95.0f;
            const float gravity = 14.0f;
            const float stepTime = 0.08f;

            direction.Normalize();
            point = Vector3.zero;
            normal = Vector3.zero;
            path = new List<Vector3>();
            path.Add(origin);

            Vector3 previous = origin;
            int steps = Math.Max(1, (int)Math.Ceiling(range / (speed * stepTime)));
            for (int i = 1; i <= steps; i++)
            {
                float time = i * stepTime;
                Vector3 next = origin + direction * (speed * time)
                    + Vector3.down * (0.5f * gravity * time * time);
                Vector3 segment = next - previous;
                float length = segment.magnitude;
                if (length > 0.0001f
                    && Raycast(previous, segment / length, length, out point, out normal))
                {
                    path.Add(point);
                    return true;
                }
                path.Add(next);
                previous = next;
            }
            point = previous;
            return false;
        }

        // EIN Material fuer alle Leuchtspuren, nicht eines je Schuss.
        //
        // Bis 0.5.1 legte jeder Schuss ein eigenes `new Material(shader)` an.
        // Bei der LAW war das gleichgueltig - eine Rakete alle paar Sekunden.
        // Das BTR-Geschuetz schiesst seit 0.5.2 acht mal je Sekunde, und ein
        // zur Laufzeit erzeugtes Material wird von `Destroy(tracer)` NICHT
        // mitgenommen: das waeren knapp fuenfhundert liegengebliebene
        // Materialien je Minute Dauerfeuer. `HideAndDontSave` haelt das eine
        // ueber den Szenenwechsel; wird es doch einmal abgeraeumt, ist der
        // Unity-Vergleich mit null true und es entsteht ein neues.
        static Material _tracerMat;
        static readonly Color TracerFarbe = new Color(1.0f, 0.88f, 0.42f, 1.0f);

        static Material TracerMaterial()
        {
            if (_tracerMat != null) return _tracerMat;

            Shader shader = Shader.Find("Particles/Additive");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) return null;

            Material m = new Material(shader);
            if (m.HasProperty("_Color")) m.SetColor("_Color", TracerFarbe);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", TracerFarbe);
            m.hideFlags = HideFlags.HideAndDontSave;
            _tracerMat = m;
            return _tracerMat;
        }

        internal static void SpawnTracer(List<Vector3> points)
        {
            SpawnTracer(points, 0.035f, 0.012f, Color.white, TracerFarbe, 0.22f);
        }

        /// <summary>
        /// Leuchtspur mit vorgegebener Breite, Farbe und Standzeit.
        ///
        /// Die Rakete braucht einen duennen Faden, das Panzergeschuetz einen
        /// Balken - dieselbe Bahn, aber um mehr als eine Zehnerpotenz andere
        /// Masse. Bis 0.5.2 hatten beide dieselben festen Werte, und die waren
        /// an der Rakete ausgerichtet: 0.035 Spieleinheiten sind bei rund drei
        /// Einheiten je Meter gut ein Zentimeter Breite. Fuer ein
        /// 125-mm-Geschoss auf dreihundert Meter ist das nichts.
        ///
        /// Die Farbe kommt NICHT ueber das Material. Das ist seit 0.5.2 eines
        /// fuer alle Spuren und wird geteilt (`sharedMaterial`) - wer es faerbt,
        /// faerbt jede laufende Spur mit. Der LineRenderer hat dafuer eigene
        /// Eckfarben, und `Particles/Additive` multipliziert sie mit der
        /// Materialfarbe. Deshalb zwei Farben je Aufruf statt einer im Material.
        ///
        /// C# 3.0 kennt keine optionalen Argumente, deshalb eine Ueberladung
        /// statt Standardwerten.
        /// </summary>
        internal static void SpawnTracer(List<Vector3> points, float startWidth,
                                         float endWidth, Color vorn, Color hinten,
                                         float standzeit)
        {
            if (points == null || points.Count < 2) return;
            GameObject tracer = new GameObject("NDR Leuchtspur");
            LineRenderer line = tracer.AddComponent(typeof(LineRenderer)) as LineRenderer;
            if (line == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("LineRenderer konnte nicht erzeugt werden.");
            }

            Material material = TracerMaterial();
            if (material == null)
            {
                UnityEngine.Object.Destroy(tracer);
                throw new MissingMemberException("Shader fuer LAW-Leuchtspur nicht gefunden.");
            }

            line.sharedMaterial = material;
            line.useWorldSpace = true;
            line.positionCount = points.Count;
            line.startWidth = startWidth;
            line.endWidth = endWidth;
            line.startColor = vorn;
            line.endColor = hinten;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
            UnityEngine.Object.Destroy(tracer, standzeit);
        }

        static object GetField(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo f = AccessTools.Field(instance.GetType(), name);
            if (f == null) throw new MissingFieldException(instance.GetType().FullName, name);
            return f.GetValue(instance);
        }

        static int ObscuredInt(object value)
        {
            object result = InvokeImplicit(value, typeof(int));
            return (int)result;
        }

        static float ObscuredFloat(object value)
        {
            object result = InvokeImplicit(value, typeof(float));
            return (float)result;
        }

        static object InvokeImplicit(object value, Type returnType)
        {
            if (value == null) throw new ArgumentNullException("value");
            Type valueType = value.GetType();
            MethodInfo[] methods = valueType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "op_Implicit" || m.ReturnType != returnType) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == valueType)
                    return m.Invoke(null, new object[] { value });
            }
            throw new MissingMethodException(valueType.FullName,
                                             "op_Implicit -> " + returnType.FullName);
        }

        static bool Raycast(Vector3 origin, Vector3 direction, float range,
                            out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            Type physicsType = RevivalPlugin.TypeByName("UnityEngine.Physics");
            Type hitType = RevivalPlugin.TypeByName("UnityEngine.RaycastHit");
            if (physicsType == null || hitType == null)
                throw new MissingMemberException("UnityEngine.Physics oder RaycastHit nicht gefunden.");

            MethodInfo chosen = null;
            MethodInfo[] methods = physicsType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "Raycast" || m.ReturnType != typeof(bool)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(Vector3)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType.IsByRef
                    && ps[2].ParameterType.GetElementType() == hitType
                    && ps[3].ParameterType == typeof(float))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException("Physics.Raycast(Vector3,Vector3,out RaycastHit,float)");

            object boxedHit = Activator.CreateInstance(hitType);
            object[] args = new object[] { origin, direction, boxedHit, range };
            bool didHit = (bool)chosen.Invoke(null, args);
            if (!didHit) return false;
            boxedHit = args[2];

            PropertyInfo pointProperty = hitType.GetProperty("point", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo normalProperty = hitType.GetProperty("normal", BindingFlags.Public | BindingFlags.Instance);
            if (pointProperty == null || normalProperty == null)
                throw new MissingMemberException("RaycastHit.point oder normal nicht gefunden.");
            point = (Vector3)pointProperty.GetValue(boxedHit, null);
            normal = (Vector3)normalProperty.GetValue(boxedHit, null);
            return true;
        }

        static void SetProperty(object instance, string name, object value)
        {
            MethodInfo setter = AccessTools.PropertySetter(instance.GetType(), name);
            if (setter == null)
                throw new MissingMethodException(instance.GetType().FullName, "set_" + name);
            setter.Invoke(instance, new object[] { value });
        }

        static GameObject PhotonInstantiate(string path, Vector3 position,
                                            Quaternion rotation, byte group)
        {
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) throw new MissingMemberException("PhotonNetwork nicht gefunden.");
            MethodInfo chosen = null;
            MethodInfo[] methods = photon.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "Instantiate") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(Vector3)
                    && ps[2].ParameterType == typeof(Quaternion)
                    && ps[3].ParameterType == typeof(byte))
                {
                    chosen = m;
                    break;
                }
            }
            if (chosen == null)
                throw new MissingMethodException("PhotonNetwork.Instantiate(string,Vector3,Quaternion,byte)");
            return chosen.Invoke(null, new object[] { path, position, rotation, group }) as GameObject;
        }
    }

    /// <summary>
    /// Puts an item of ours on the ground. The game cannot do it for our ids.
    ///
    /// WHY (CONFIRMED, IL, 2026-08-29) - PlayerInventoryManager::
    /// DropInventoryItem builds the path
    /// `GetItemCatData(id).PrefabPatch + id + "_Spawn"`, loads it through
    /// Resources.Load(string) - which ResourceHook answers for our ids - and
    /// then hands the same PATH to PhotonNetwork::InstantiateSceneObject.
    /// Photon loads the prefab a SECOND time, through its own cache and its
    /// own overload, and wants a PhotonView on the result. That second load
    /// is not ours, so the drop ends in "DropItem null!" and nothing lies on
    /// the ground. Same story in DropWeaponFromHand, only there the null
    /// prefab is dereferenced and throws - which used to take PlayerDeath
    /// down with it, before the respawn screen.
    ///
    /// So we drop the item ourselves, locally: mesh, material, collider,
    /// rigidbody. It falls, it lies there, it is gone after five minutes.
    /// What it is NOT: pickupable, and nobody else sees it. A real drop needs
    /// a networked ItemSpawned and is a piece of work of its own.
    /// </summary>
    public static class DropHook
    {
        const int LAW_ID = 1162;

        /// <summary>How long a dropped item stays, in seconds.</summary>
        const float Liegezeit = 300f;

        /// <summary>
        /// The drop out of the inventory, for EVERY id of ours - since 0.5.1
        /// not the LAW alone. The drone hangs on exactly this: it is not a
        /// weapon, it only ever lies in the backpack, and the game's own path
        /// silently did nothing for it.
        /// </summary>
        public static bool InventoryPrefix(object __instance, int __0, int __1,
                                           string __2, bool __3)
        {
            ItemDef def = RevivalPlugin.FindItem(__1);
            if (def == null) return true;
            if (__2 != "WeaponSlot" && __2 != "BackpackSlot") return true;

            // Since 0.5.3 the game's own path can carry our ids, so this hook
            // steps aside. What lands on the ground is then a real scene object
            // with a PhotonView and an ItemSpawned - everybody sees it and it can
            // be picked up. See E-025: the only thing that was missing was the
            // second Resources.Load overload, the one Photon uses.
            //
            // If Photon is not in a room, InstantiateSceneObject returns null
            // without touching the inventory - the item would silently stay in
            // the backpack. In that case the local piece of scenery of 0.5.1 is
            // still better than nothing.
            if (RevivalPlugin.CfgNetDrop != null && RevivalPlugin.CfgNetDrop.Value
                && InRoom())
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + ": drop through the game's own "
                                            + "path (networked scene object).");
                return true;
            }

            try
            {
                FieldInfo spawnerField = AccessTools.Field(__instance.GetType(), "ObjectSpawner");
                Transform spawner = spawnerField == null
                    ? null : spawnerField.GetValue(__instance) as Transform;
                Vector3 position = spawner == null
                    ? Vector3.zero : spawner.position;
                Ablegen(def, position, Vector3.zero);

                if (__2 == "WeaponSlot")
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearWeaponSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearWeaponSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, def.Id, true, false });
                }
                else
                {
                    MethodInfo clear = AccessTools.Method(__instance.GetType(), "ClearBackpackSlot",
                        new Type[] { typeof(int), typeof(int), typeof(bool) }, null);
                    if (clear == null) throw new MissingMethodException("ClearBackpackSlot fehlt.");
                    clear.Invoke(__instance, new object[] { __0, def.Id, true });
                }

                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + " aus " + __2 + " " + __0
                                            + " entfernt und lokal abgelegt.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Ablegen aus dem Inventar fehlgeschlagen ("
                                             + def.Id + "): " + ex);
            }
            return false;
        }

        /// <summary>
        /// The drop out of the HAND, for every custom weapon. In a room the
        /// game's networked path is kept; outside a room the local visible
        /// fallback is the only path that can exist.
        /// </summary>
        public static bool Prefix(int __0, int __1, int __2, Vector3 __3,
                                  Quaternion __4, Vector3 __5)
        {
            ItemDef def = RevivalPlugin.FindItem(__0);
            if (def == null) return true;

            if (RevivalPlugin.CfgNetDrop != null && RevivalPlugin.CfgNetDrop.Value
                && InRoom())
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + ": hand/death drop through "
                                            + "the networked scene-object path.");
                return true;
            }

            Ablegen(def, __3, __5);
            return false;
        }

        /// <summary>
        /// A failed hand drop must never abort PlayerDeath. The 2026-08-31 log
        /// proves this exact exception prevented the crew-FPV victim from
        /// reaching the respawn UI. The inactive-template fix below makes the
        /// network path succeed; this finalizer is the last-resort guarantee
        /// that a future prefab failure still leaves one visible object and
        /// lets death finish.
        /// </summary>
        public static Exception HandFinalizer(Exception __exception, int __0,
                                              Vector3 __3, Vector3 __5)
        {
            if (__exception == null) return null;
            ItemDef def = RevivalPlugin.FindItem(__0);
            if (def == null) return __exception;
            Ablegen(def, __3, __5);
            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogWarning(def.Name + ": hand/death network drop "
                    + "failed (" + __exception.GetType().Name + "); local fallback "
                    + "created and PlayerDeath allowed to continue.");
            return null;
        }

        /// <summary>
        /// Is Photon in a room right now? Only then does
        /// PhotonNetwork.InstantiateSceneObject do anything at all - outside a
        /// room it logs and returns null.
        /// </summary>
        static bool InRoom()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PhotonNetwork");
                if (t == null) return false;
                MethodInfo getter = AccessTools.PropertyGetter(t, "inRoom");
                if (getter == null) return false;
                object v = getter.Invoke(null, null);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        /// <summary>
        /// Builds the thing that lies on the ground: one MeshFilter, one
        /// MeshRenderer, a box, a rigidbody. Nothing else.
        ///
        /// Deliberately NOT a clone of the spawn prefab: that one is full of
        /// MeshFilters, ItemSpawned and Photon components. The LAW attempt
        /// with it produced several tubes and a pickup prompt that could never
        /// succeed. Geometry comes from the model prefab (weapons) or from the
        /// inventory prefab (everything else - the drone).
        /// </summary>
        static void Ablegen(ItemDef def, Vector3 position, Vector3 velocity)
        {
            try
            {
                Mesh mesh;
                Material[] mats;
                if (!Quelle(def, out mesh, out mats))
                    throw new MissingMemberException(def.Id + ": keine Geometrie zum Ablegen.");

                GameObject drop = new GameObject(def.Name + " (abgelegt)");
                MeshFilter filter = drop.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = drop.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = mats;
                drop.transform.position = position + Vector3.up * 0.35f;
                drop.transform.rotation = def.Id == LAW_ID
                    ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.identity;

                Type colliderType = RevivalPlugin.TypeByName("UnityEngine.BoxCollider");
                Component collider = colliderType == null ? null : drop.AddComponent(colliderType);
                if (collider == null)
                    throw new MissingMemberException("BoxCollider fuer die Ablage fehlt.");
                SetProperty(collider, "center", mesh.bounds.center);
                SetProperty(collider, "size", mesh.bounds.size);

                Type bodyType = RevivalPlugin.TypeByName("UnityEngine.Rigidbody");
                Component body = bodyType == null ? null : drop.GetComponent(bodyType);
                if (body == null && bodyType != null) body = drop.AddComponent(bodyType);
                if (body != null)
                {
                    SetProperty(body, "useGravity", true);
                    SetProperty(body, "isKinematic", false);
                    Vector3 v = velocity;
                    if (v.magnitude > 15f) v = v.normalized * 15f;
                    SetProperty(body, "velocity", v);
                    SetProperty(body, "angularVelocity", new Vector3(0f, 0.8f, 0f));
                }

                UnityEngine.Object.Destroy(drop, Liegezeit);
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogInfo(def.Name + " liegt bei " + drop.transform.position
                                            + ", " + (int)Liegezeit + " s lang.");
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogError("Ablage fehlgeschlagen (" + def.Id + "): " + ex);
            }
        }

        /// <summary>
        /// Mesh and materials for the dropped object. Weapons have a model
        /// prefab for the hand; everything else has only the inventory prefab,
        /// and its first MeshFilter carries our own geometry (SwapGeometry put
        /// it there). Materials come from the SAME object as the mesh -
        /// otherwise a drop ends up wearing the donor's material.
        /// </summary>
        static bool Quelle(ItemDef def, out Mesh mesh, out Material[] mats)
        {
            mesh = null;
            mats = null;
            GameObject source = null;
            if (def.IsWeapon) source = def.Factory.GetModelPrefab();
            if (source == null) source = def.Factory.GetSpawnPrefab(null);
            if (source == null) return false;

            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i] == null || filters[i].sharedMesh == null) continue;
                MeshRenderer r = filters[i].GetComponent<MeshRenderer>();
                if (r == null) r = source.GetComponentInChildren<MeshRenderer>(true);
                if (r == null) continue;
                mesh = filters[i].sharedMesh;
                mats = r.sharedMaterials;
                return true;
            }
            return false;
        }

        static void SetProperty(object instance, string name, object value)
        {
            MethodInfo setter = AccessTools.PropertySetter(instance.GetType(), name);
            if (setter == null)
                throw new MissingMethodException(instance.GetType().FullName, "set_" + name);
            setter.Invoke(instance, new object[] { value });
        }
    }
}

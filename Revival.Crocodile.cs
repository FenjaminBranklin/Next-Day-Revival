// Next Day: Survival - Revival Toolkit
//
// TOXIC CROCODILE BOSS. The game already has a durable, networked animal boss
// contract. Resources contains AnimalsSpawn/BearBoss_Spawn and firearm damage
// reaches its Animal_AI through the native NetworkApplyDamage RPC. This feature
// keeps that contract for hit registration, health, death, loot, Photon late
// joins and the boss HUD, but replaces the skin and locomotion with an original
// Blender crocodile that stays on a measured loop inside the large lake south
// of Point 12.
//
// The lake centre is read from assets/editor/basemap.png. That artwork covers
// x/z -2500..2500; pixel (914, 328) maps to (1965, 900). The loop occupies the
// broad western lobe, away from the wooded island. Its Y comes from the actual
// water renderer/collider at runtime because the 2D artwork has no elevation.
//
// C# 3.0. Player-facing strings use Loc.T and real Cyrillic.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>
    /// Spawns and adopts one native bear-boss proxy, turns its visible body into
    /// the Blender crocodile, and keeps it swimming in the Point 12 lake.
    /// </summary>
    public static class Crocodile
    {
        const float DefaultX = 1965f;
        const float DefaultZ = 900f;
        const float AutoWater = -9999f;
        const float StartDelay = 24f;
        const float ScanRadius = 360f;

        const string MeshFile = "crocodile.ndmesh";
        const string DiffuseFile = "crocodile_diffuse.png";
        const string NormalFile = "crocodile_normal.png";

        static readonly string[] BossResources = new string[] {
            "AnimalsSpawn/BearBoss_Spawn",
            "animalsspawn/bearboss_spawn"
        };

        static ConfigEntry<bool> _enabled;
        static ConfigEntry<string> _scene;
        static ConfigEntry<float> _x;
        static ConfigEntry<float> _z;
        static ConfigEntry<float> _radiusX;
        static ConfigEntry<float> _radiusZ;
        static ConfigEntry<float> _speed;
        static ConfigEntry<float> _waterOverride;
        static ConfigEntry<float> _fallbackWater;
        static ConfigEntry<float> _damageTaken;
        static ConfigEntry<float> _respawnMinutes;
        static ConfigEntry<float> _toxicRadius;
        static ConfigEntry<float> _toxicityPerSecond;
        static ConfigEntry<float> _healthPerSecond;
        static ConfigEntry<float> _biteDamage;

        static bool _installed;
        static Component _animal;
        static CrocodileSwimmer _swimmer;
        static float _worldSince;
        static float _nextTick;
        static float _nextScan;
        static float _nextSpawn;
        static float _deadAt = -1f;
        static bool _spawnIssued;
        static bool _resourceWarned;
        static bool _spawnWarned;

        static float _waterY = 430f;
        static bool _waterMeasured;
        static float _nextWaterScan;
        static string _waterSource = "fallback";

        static Type _aliveType;
        static MethodInfo _aliveMethod;
        static PropertyInfo _aliveProperty;
        static FieldInfo _aliveField;
        static MethodInfo _deadMethod;
        static PropertyInfo _deadProperty;
        static FieldInfo _deadField;

        static Type _photonType;
        static MethodInfo _masterGetter;
        static MethodInfo _networkTimeGetter;

        static readonly Dictionary<MethodBase, int> DamageArguments =
            new Dictionary<MethodBase, int>();

        static GUIStyle _nameStyle;
        static GUIStyle _shadowStyle;
        static GUIStyle _warningStyle;
        static string _notice = "";
        static float _noticeUntil;

        public static void BindConfig(ConfigFile config)
        {
            _enabled = config.Bind("Crocodile", "Enabled", true,
                Loc.T("Токсичный крокодил-босс плавает в озере у нейтральной базы.",
                      "A toxic crocodile boss swims in the lake near the neutral base."));
            _scene = config.Bind("Crocodile", "Scene", MapScene.Home,
                Loc.T("Сцена озера. По умолчанию это стартовый регион.",
                      "Scene containing the lake; the default is the starting region."));
            _x = config.Bind("Crocodile", "LakeX", DefaultX,
                Loc.T("Мировая координата X центра маршрута по озеру.",
                      "World X of the swimming loop centre."));
            _z = config.Bind("Crocodile", "LakeZ", DefaultZ,
                Loc.T("Мировая координата Z центра маршрута по озеру.",
                      "World Z of the swimming loop centre."));
            _radiusX = config.Bind("Crocodile", "SwimRadiusX", 110f,
                Loc.T("Радиус маршрута по оси X в метрах.",
                      "Swimming-loop radius on X in metres."));
            _radiusZ = config.Bind("Crocodile", "SwimRadiusZ", 245f,
                Loc.T("Радиус маршрута по оси Z в метрах.",
                      "Swimming-loop radius on Z in metres."));
            _speed = config.Bind("Crocodile", "SwimSpeed", 3.2f,
                Loc.T("Скорость плавания в метрах в секунду.",
                      "Swimming speed in metres per second."));
            _waterOverride = config.Bind("Crocodile", "WaterY", AutoWater,
                Loc.T("Высота воды. -9999 определяет ее автоматически.",
                      "Water surface Y. -9999 detects it from the scene."));
            _fallbackWater = config.Bind("Crocodile", "FallbackWaterY", 430f,
                Loc.T("Запасная высота воды, если объект воды еще не загружен.",
                      "Fallback water Y if the scene water object is not loaded yet."));
            _damageTaken = config.Bind("Crocodile", "DamageTakenMultiplier", 0.20f,
                Loc.T("Доля входящего урона. 0.20 дает боссу пятикратную живучесть.",
                      "Incoming damage fraction; 0.20 gives five times effective health."));
            _respawnMinutes = config.Bind("Crocodile", "RespawnMinutes", 30f,
                Loc.T("Через сколько минут после смерти босс вернется. 0 = никогда.",
                      "Minutes before the defeated boss returns; 0 means never."));
            _toxicRadius = config.Bind("Crocodile", "ToxicRadius", 14f,
                Loc.T("Радиус токсичных испарений в метрах.",
                      "Radius of the toxic fumes in metres."));
            _toxicityPerSecond = config.Bind("Crocodile", "ToxicityPerSecond", 4f,
                Loc.T("Рост отравления в секунду без защиты.",
                      "Toxicity gained per second without protection."));
            _healthPerSecond = config.Bind("Crocodile", "ToxicDamagePerSecond", 2.5f,
                Loc.T("Урон здоровью от испарений в секунду без защиты.",
                      "Health damage per second from fumes without protection."));
            _biteDamage = config.Bind("Crocodile", "BiteDamage", 18f,
                Loc.T("Урон от укуса на очень близком расстоянии.",
                      "Bite damage at point-blank range."));
        }

        public static void Install(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            _installed = true;
            try
            {
                Type animal = RevivalPlugin.TypeByName("Animal_AI");
                if (animal == null)
                {
                    RevivalPlugin.L.LogWarning("Crocodile: Animal_AI not found; "
                        + "the model can swim but damage resistance is unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(Crocodile).GetMethod("DamagePrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo[] methods = animal.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                int patched = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "NetworkApplyDamage") continue;
                    int damageIndex = FindDamageArgument(method);
                    if (damageIndex < 0) continue;
                    DamageArguments[method] = damageIndex;
                    harmony.Patch(method, new HarmonyMethod(prefix), null,
                                  null, null, null);
                    patched++;
                }
                RevivalPlugin.L.LogInfo("Crocodile: native Animal_AI boss damage "
                    + "gate installed on " + patched + " overload(s).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crocodile damage gate: " + ex.Message);
            }
        }

        static int FindDamageArgument(MethodBase method)
        {
            ParameterInfo[] args = method.GetParameters();
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i].Name == null ? "" : args[i].Name.ToLowerInvariant();
                if ((name.Contains("damage") || name.Contains("dmg"))
                    && Numeric(args[i].ParameterType)) return i;
            }
            for (int i = 0; i < args.Length; i++)
                if (Numeric(args[i].ParameterType)) return i;
            return -1;
        }

        static bool Numeric(Type type)
        {
            return type == typeof(float) || type == typeof(double)
                || type == typeof(int) || type == typeof(short);
        }

        static void DamagePrefix(object __instance, object[] __args,
                                 MethodBase __originalMethod)
        {
            try
            {
                if (!IsCrocodile(__instance) || __args == null) return;
                int index;
                if (!DamageArguments.TryGetValue(__originalMethod, out index)
                    || index < 0 || index >= __args.Length) return;
                float multiplier = DamageTaken();
                object value = __args[index];
                if (value is float)
                    __args[index] = Mathf.Max(0.05f, (float)value * multiplier);
                else if (value is double)
                    __args[index] = Math.Max(0.05, (double)value * multiplier);
                else if (value is int)
                    __args[index] = Math.Max(1, Mathf.RoundToInt((int)value * multiplier));
                else if (value is short)
                    __args[index] = (short)Math.Max(1,
                        Mathf.RoundToInt((short)value * multiplier));
            }
            catch (Exception ex)
            {
                if (!_spawnWarned)
                {
                    _spawnWarned = true;
                    RevivalPlugin.L.LogWarning("Crocodile damage scaling: " + ex.Message);
                }
            }
        }

        static bool IsCrocodile(object instance)
        {
            Component component = instance as Component;
            if (component == null) return false;
            return component.GetComponentInParent<CrocodileSwimmer>() != null
                || component.GetComponentInChildren<CrocodileSwimmer>() != null;
        }

        public static void Tick()
        {
            bool here = Enabled() && MapScene.Owns(SceneTag())
                && MapTools.LocalPlayer() != null;
            if (!here)
            {
                _worldSince = 0f;
                ClearLocal(true);
                return;
            }

            if (_worldSince <= 0f)
            {
                _worldSince = Time.time;
                _nextTick = 0f;
                _waterMeasured = false;
                _nextWaterScan = 0f;
            }
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.75f;

            SurfaceY();

            if (_animal != null && !Alive(_animal))
            {
                if (_swimmer != null) _swimmer.Die();
                MarkDead();
            }

            if ((_animal == null || _swimmer == null) && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 2f;
                Component found = FindExisting();
                if (found != null) Attach(found);
            }

            if (_animal != null || Time.time - _worldSince < StartDelay
                || !IsMaster() || Time.time < _nextSpawn) return;

            float respawn = RespawnMinutes();
            if (_deadAt >= 0f && (respawn <= 0f
                || Time.time < _deadAt + respawn * 60f)) return;

            _nextSpawn = Time.time + 30f;
            Spawn();
        }

        static void Spawn()
        {
            GameObject prefab = null;
            string resource = null;
            for (int i = 0; i < BossResources.Length; i++)
            {
                prefab = Resources.Load(BossResources[i]) as GameObject;
                if (prefab != null)
                {
                    resource = BossResources[i];
                    break;
                }
            }
            if (prefab == null)
            {
                if (!_resourceWarned)
                {
                    _resourceWarned = true;
                    RevivalPlugin.L.LogError("Crocodile: native BearBoss_Spawn "
                        + "resource not found; no substitute animal was guessed.");
                }
                _nextSpawn = Time.time + 120f;
                return;
            }

            Vector3 position = SwimPoint(NetworkClock());
            position.y = SurfaceY() + 0.04f;
            Vector3 tangent = SwimTangent(NetworkClock());
            Quaternion rotation = tangent.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(tangent) : Quaternion.identity;
            GameObject spawned = null;
            Exception photonFailure = null;
            try
            {
                // The path goes to Photon UNCHANGED. Photon loads it a SECOND
                // time through Resources.Load(string, Type) - on the master and
                // on every receiving client - and a Resources path with a
                // backslash does not resolve there. See E-025 and DropHook.
                spawned = PhotonInstantiate(resource, position, rotation);
            }
            catch (Exception ex) { photonFailure = ex; }

            if (spawned == null && PhotonOffline())
                spawned = UnityEngine.Object.Instantiate(prefab, position, rotation)
                    as GameObject;

            if (spawned == null)
            {
                if (!_spawnWarned)
                {
                    _spawnWarned = true;
                    RevivalPlugin.L.LogWarning("Crocodile: native boss spawn failed"
                        + (photonFailure == null ? "." : (": " + photonFailure.Message)));
                }
                return;
            }

            _spawnIssued = true;
            _deadAt = -1f;
            Component animal = AnimalOn(spawned);
            if (animal != null) Attach(animal);
            RevivalPlugin.L.LogInfo("Crocodile: native bear-boss proxy spawned at "
                + position + "; Blender skin and swim controller "
                + (animal == null ? "will attach when Animal_AI starts." : "attached."));
        }

        static GameObject PhotonInstantiate(string path, Vector3 position,
                                             Quaternion rotation)
        {
            Type photon = PhotonType();
            if (photon == null) return null;
            MethodInfo selected = null;
            MethodInfo[] methods = photon.GetMethods(BindingFlags.Public
                | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "InstantiateSceneObject") continue;
                ParameterInfo[] args = method.GetParameters();
                if (args.Length == 5 && args[0].ParameterType == typeof(string)
                    && args[1].ParameterType == typeof(Vector3)
                    && args[2].ParameterType == typeof(Quaternion)
                    && args[3].ParameterType == typeof(byte))
                {
                    selected = method;
                    break;
                }
            }
            if (selected == null)
                throw new MissingMethodException("PhotonNetwork.InstantiateSceneObject");
            return selected.Invoke(null, new object[] {
                path, position, rotation, (byte)0, null }) as GameObject;
        }

        static Component FindExisting()
        {
            Type type = RevivalPlugin.TypeByName("Animal_AI");
            if (type == null) return null;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(type);
            Component best = null;
            float bestScore = float.MaxValue;
            Vector3 centre = Centre();
            for (int i = 0; i < all.Length; i++)
            {
                Component animal = all[i] as Component;
                if (animal == null || !Alive(animal)) continue;
                CrocodileSwimmer marked = animal.GetComponentInParent<CrocodileSwimmer>();
                if (marked == null)
                    marked = animal.GetComponentInChildren<CrocodileSwimmer>();
                Vector3 delta = animal.transform.position - centre;
                delta.y = 0f;
                float distance = delta.magnitude;
                bool bossName = NameContains(animal.transform, "boss");
                if (marked == null && distance > ScanRadius) continue;
                if (marked == null && !bossName && !_spawnIssued && distance > 90f)
                    continue;
                float score = distance - (marked != null ? 2000f : 0f)
                    - (bossName ? 500f : 0f);
                if (score >= bestScore) continue;
                bestScore = score;
                best = animal;
            }
            return best;
        }

        static bool NameContains(Transform transform, string token)
        {
            Transform current = transform;
            for (int i = 0; current != null && i < 7; i++)
            {
                if (current.name != null
                    && current.name.ToLowerInvariant().Contains(token)) return true;
                current = current.parent;
            }
            return false;
        }

        static Component AnimalOn(GameObject root)
        {
            if (root == null) return null;
            Type type = RevivalPlugin.TypeByName("Animal_AI");
            if (type == null) return null;
            Component animal = root.GetComponent(type);
            if (animal == null) animal = root.GetComponentInChildren(type);
            return animal;
        }

        static void Attach(Component animal)
        {
            if (animal == null || !Alive(animal)) return;
            GameObject root = NetworkRoot(animal);
            if (root == null) return;
            CrocodileSwimmer swimmer = root.GetComponent<CrocodileSwimmer>();
            if (swimmer == null) swimmer = root.AddComponent<CrocodileSwimmer>();
            swimmer.Setup(animal);
            _animal = animal;
            _swimmer = swimmer;
            _deadAt = -1f;
            _spawnIssued = true;
            RevivalPlugin.L.LogInfo("Crocodile: adopted " + animal.name
                + ", water Y " + SurfaceY().ToString("0.00")
                + " from " + _waterSource + ", damage x"
                + DamageTaken().ToString("0.00") + ".");
        }

        static GameObject NetworkRoot(Component animal)
        {
            Type photonView = RevivalPlugin.TypeByName("PhotonView");
            Transform current = animal.transform;
            Transform candidate = animal.transform;
            for (int i = 0; current != null && i < 8; i++)
            {
                if (photonView != null && current.GetComponent(photonView) != null)
                    candidate = current;
                current = current.parent;
            }
            return candidate.gameObject;
        }

        internal static bool Alive(Component animal)
        {
            if (animal == null || animal.gameObject == null
                || !animal.gameObject.activeInHierarchy) return false;
            try
            {
                CacheAlive(animal.GetType());
                if (_aliveMethod != null)
                    return Convert.ToBoolean(_aliveMethod.Invoke(animal, null));
                if (_aliveProperty != null)
                    return Convert.ToBoolean(_aliveProperty.GetValue(animal, null));
                if (_aliveField != null)
                    return Convert.ToBoolean(_aliveField.GetValue(animal));
                if (_deadMethod != null)
                    return !Convert.ToBoolean(_deadMethod.Invoke(animal, null));
                if (_deadProperty != null)
                    return !Convert.ToBoolean(_deadProperty.GetValue(animal, null));
                if (_deadField != null)
                    return !Convert.ToBoolean(_deadField.GetValue(animal));
            }
            catch { }
            return true;
        }

        static void CacheAlive(Type type)
        {
            if (_aliveType == type) return;
            _aliveType = type;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic;
            _aliveMethod = ZeroBool(type, flags, "IsAlive", "GetIsAlive");
            _aliveProperty = BoolProperty(type, flags, "IsAlive", "isAlive");
            _aliveField = BoolField(type, flags, "IsAlive", "isAlive", "_isAlive");
            _deadMethod = ZeroBool(type, flags, "IsDead", "GetIsDead");
            _deadProperty = BoolProperty(type, flags, "IsDead", "isDead", "Dead");
            _deadField = BoolField(type, flags, "IsDead", "isDead", "_isDead", "Dead");
        }

        static MethodInfo ZeroBool(Type type, BindingFlags flags,
                                   params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo method = type.GetMethod(names[i], flags, null,
                                                   Type.EmptyTypes, null);
                if (method != null && method.ReturnType == typeof(bool)) return method;
            }
            return null;
        }

        static PropertyInfo BoolProperty(Type type, BindingFlags flags,
                                         params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                PropertyInfo property = type.GetProperty(names[i], flags);
                if (property != null && property.PropertyType == typeof(bool)) return property;
            }
            return null;
        }

        static FieldInfo BoolField(Type type, BindingFlags flags,
                                   params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo field = type.GetField(names[i], flags);
                if (field != null && field.FieldType == typeof(bool)) return field;
            }
            return null;
        }

        static void MarkDead()
        {
            if (_deadAt < 0f)
            {
                _deadAt = Time.time;
                RevivalPlugin.L.LogInfo("Crocodile: boss defeated; respawn in "
                    + RespawnMinutes().ToString("0.#") + " minute(s).");
                Notice(Loc.T("ТОКСИЧНЫЙ КРОКОДИЛ ПОБЕЖДЕН",
                             "TOXIC CROCODILE DEFEATED"), 8f);
            }
            _animal = null;
            _swimmer = null;
        }

        internal static void Gone(CrocodileSwimmer swimmer, bool dead)
        {
            if (_swimmer != swimmer) return;
            if (dead) MarkDead();
            else
            {
                _animal = null;
                _swimmer = null;
            }
        }

        static void ClearLocal(bool detach)
        {
            if (detach && _swimmer != null) _swimmer.Detach();
            _animal = null;
            _swimmer = null;
            _spawnIssued = false;
        }

        internal static Vector3 Centre()
        {
            return new Vector3(_x == null ? DefaultX : _x.Value, 0f,
                               _z == null ? DefaultZ : _z.Value);
        }

        internal static Vector3 SwimPoint(double clock)
        {
            Vector3 centre = Centre();
            float phase = Phase(clock);
            return new Vector3(centre.x + Mathf.Sin(phase) * RadiusX(),
                               SurfaceY(),
                               centre.z + Mathf.Cos(phase) * RadiusZ());
        }

        internal static Vector3 SwimTangent(double clock)
        {
            float phase = Phase(clock);
            return new Vector3(Mathf.Cos(phase) * RadiusX(), 0f,
                               -Mathf.Sin(phase) * RadiusZ()).normalized;
        }

        static float Phase(double clock)
        {
            float radius = Mathf.Sqrt(RadiusX() * RadiusZ());
            float angular = Speed() / Mathf.Max(20f, radius);
            return (float)(clock * angular + 0.37);
        }

        internal static float SurfaceY()
        {
            float configured = _waterOverride == null ? AutoWater : _waterOverride.Value;
            if (configured > AutoWater + 1f)
            {
                _waterY = configured;
                _waterMeasured = true;
                _waterSource = "Crocodile.WaterY";
                return _waterY;
            }
            if (!_waterMeasured && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 12f;
                float measured;
                string source;
                if (FindWater(out measured, out source))
                {
                    _waterY = measured + 0.04f;
                    _waterMeasured = true;
                    _waterSource = source;
                    RevivalPlugin.L.LogInfo("Crocodile: water surface "
                        + _waterY.ToString("0.00") + " from " + source + ".");
                }
                else
                {
                    _waterY = _fallbackWater == null ? 430f : _fallbackWater.Value;
                    _waterSource = "FallbackWaterY";
                }
            }
            return _waterY;
        }

        static bool FindWater(out float y, out string source)
        {
            y = 0f;
            source = "";
            Vector3 centre = Centre();
            float best = float.MaxValue;
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !WaterRenderer(renderer)) continue;
                Bounds bounds = renderer.bounds;
                if (!ContainsXZ(bounds, centre, 8f)) continue;
                float area = Mathf.Max(1f, bounds.size.x * bounds.size.z);
                float score = area + Mathf.Abs(bounds.center.y) * 0.01f;
                if (score >= best) continue;
                best = score;
                y = bounds.size.y < 6f ? bounds.center.y : bounds.max.y;
                source = "renderer " + Path(renderer.transform);
            }
            if (best < float.MaxValue) return true;

            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !WaterName(collider.transform)) continue;
                Bounds bounds = collider.bounds;
                if (!ContainsXZ(bounds, centre, 8f)) continue;
                float area = Mathf.Max(1f, bounds.size.x * bounds.size.z);
                if (area >= best) continue;
                best = area;
                y = bounds.max.y;
                source = "collider " + Path(collider.transform);
            }
            if (best < float.MaxValue) return true;

            Ray ray = new Ray(new Vector3(centre.x, 1500f, centre.z), Vector3.down);
            RaycastHit[] hits = Physics.RaycastAll(ray, 3000f, ~0);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider == null || !WaterName(hits[i].collider.transform))
                    continue;
                if (source.Length == 0 || hits[i].point.y > y)
                {
                    y = hits[i].point.y;
                    source = "ray " + Path(hits[i].collider.transform);
                }
            }
            return source.Length > 0;
        }

        static bool ContainsXZ(Bounds bounds, Vector3 point, float margin)
        {
            return point.x >= bounds.min.x - margin && point.x <= bounds.max.x + margin
                && point.z >= bounds.min.z - margin && point.z <= bounds.max.z + margin;
        }

        static bool WaterName(Transform transform)
        {
            Transform current = transform;
            for (int i = 0; current != null && i < 7; i++)
            {
                string name = current.name == null ? "" : current.name.ToLowerInvariant();
                if (name.Contains("water") || name.Contains("lake")
                    || name.Contains("ozero") || name.Contains("voda")) return true;
                string layer = LayerMask.LayerToName(current.gameObject.layer);
                if (layer != null && layer.ToLowerInvariant().Contains("water")) return true;
                current = current.parent;
            }
            return false;
        }

        static bool WaterRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            if (WaterName(renderer.transform)) return true;
            try
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null) continue;
                    string name = material.name == null
                        ? "" : material.name.ToLowerInvariant();
                    string shader = material.shader == null || material.shader.name == null
                        ? "" : material.shader.name.ToLowerInvariant();
                    if (name.Contains("water") || name.Contains("lake")
                        || shader.Contains("water")) return true;
                }
            }
            catch { }
            return false;
        }

        static string Path(Transform transform)
        {
            string path = transform == null ? "?" : transform.name;
            Transform current = transform == null ? null : transform.parent;
            for (int i = 0; current != null && i < 4; i++)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        internal static double NetworkClock()
        {
            try
            {
                Type photon = PhotonType();
                if (photon != null)
                {
                    if (_networkTimeGetter == null)
                    {
                        _networkTimeGetter = AccessTools.PropertyGetter(photon, "time");
                        if (_networkTimeGetter == null)
                            _networkTimeGetter = AccessTools.PropertyGetter(photon, "Time");
                    }
                    if (_networkTimeGetter != null)
                        return Convert.ToDouble(_networkTimeGetter.Invoke(null, null));
                }
            }
            catch { }
            return Time.time;
        }

        static Type PhotonType()
        {
            if (_photonType == null) _photonType = RevivalPlugin.TypeByName("PhotonNetwork");
            return _photonType;
        }

        static bool IsMaster()
        {
            try
            {
                Type photon = PhotonType();
                if (photon == null) return true;
                if (_masterGetter == null)
                {
                    _masterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                    if (_masterGetter == null)
                        _masterGetter = AccessTools.PropertyGetter(photon, "IsMasterClient");
                }
                return _masterGetter == null
                    || Convert.ToBoolean(_masterGetter.Invoke(null, null));
            }
            catch { return false; }
        }

        static bool PhotonOffline()
        {
            try
            {
                Type photon = PhotonType();
                if (photon == null) return true;
                MethodInfo getter = AccessTools.PropertyGetter(photon, "offlineMode");
                if (getter == null) getter = AccessTools.PropertyGetter(photon, "OfflineMode");
                return getter != null && Convert.ToBoolean(getter.Invoke(null, null));
            }
            catch { return false; }
        }

        internal static void Exposure(bool protectedPlayer)
        {
            Notice(protectedPlayer
                ? Loc.T("ФИЛЬТР СДЕРЖИВАЕТ ТОКСИН", "FILTER BLOCKING TOXIN")
                : Loc.T("ТОКСИЧНЫЕ ИСПАРЕНИЯ!", "TOXIC FUMES!"), 0.65f);
        }

        static void Notice(string message, float seconds)
        {
            _notice = message == null ? "" : message;
            _noticeUntil = Time.time + seconds;
        }

        public static void Draw()
        {
            if (!Enabled()) return;
            EnsureStyles();
            if (_swimmer != null && _swimmer.gameObject != null)
            {
                GameObject player = MapTools.LocalPlayer();
                if (player != null)
                {
                    float distance = Vector3.Distance(player.transform.position,
                                                      _swimmer.transform.position);
                    if (distance < 280f)
                    {
                        Camera camera = Camera.main;
                        if (camera != null)
                        {
                            Vector3 screen = camera.WorldToScreenPoint(
                                _swimmer.transform.position + Vector3.up * 1.7f);
                            if (screen.z > 0f)
                            {
                                string text = Loc.T("ТОКСИЧНЫЙ КРОКОДИЛ",
                                                    "TOXIC CROCODILE")
                                    + "  " + distance.ToString("0") + " m";
                                // Centred by measurement, not GUIStyle.alignment: this
                                // build does not reference UnityEngine.TextRenderingModule
                                // (same constraint as RevivalTechnical.cs Zeile()).
                                Vector2 measured = _nameStyle.CalcSize(new GUIContent(text));
                                float cx = screen.x - measured.x * 0.5f;
                                float cy = Screen.height - screen.y - measured.y * 0.5f;
                                GUI.Label(new Rect(cx + 2f, cy + 2f, measured.x, measured.y),
                                          text, _shadowStyle);
                                GUI.Label(new Rect(cx, cy, measured.x, measured.y),
                                          text, _nameStyle);
                            }
                        }
                    }
                }
            }
            if (Time.time < _noticeUntil && _notice.Length > 0)
            {
                Vector2 measured = _warningStyle.CalcSize(new GUIContent(_notice));
                GUI.Label(new Rect(Screen.width * 0.5f - measured.x * 0.5f, 102f,
                                   measured.x, measured.y),
                          _notice, _warningStyle);
            }
        }

        static void EnsureStyles()
        {
            if (_nameStyle != null) return;
            _nameStyle = new GUIStyle(GUI.skin.label);
            _nameStyle.fontSize = 18;
            _nameStyle.normal.textColor = new Color(0.35f, 1f, 0.18f, 1f);
            _shadowStyle = new GUIStyle(_nameStyle);
            _shadowStyle.normal.textColor = Color.black;
            _warningStyle = new GUIStyle(_nameStyle);
            _warningStyle.fontSize = 22;
            _warningStyle.normal.textColor = new Color(0.55f, 1f, 0.08f, 1f);
        }

        static bool Enabled() { return _enabled == null || _enabled.Value; }
        static string SceneTag() { return _scene == null ? MapScene.Home : _scene.Value; }
        internal static float RadiusX() { return Mathf.Clamp(_radiusX == null ? 110f : _radiusX.Value, 20f, 350f); }
        internal static float RadiusZ() { return Mathf.Clamp(_radiusZ == null ? 245f : _radiusZ.Value, 20f, 350f); }
        internal static float Speed() { return Mathf.Clamp(_speed == null ? 3.2f : _speed.Value, 0.3f, 12f); }
        internal static float DamageTaken() { return Mathf.Clamp(_damageTaken == null ? 0.20f : _damageTaken.Value, 0.03f, 1f); }
        internal static float RespawnMinutes() { return Mathf.Max(0f, _respawnMinutes == null ? 30f : _respawnMinutes.Value); }
        internal static float ToxicRadius() { return Mathf.Clamp(_toxicRadius == null ? 14f : _toxicRadius.Value, 0f, 40f); }
        internal static float ToxicityRate() { return Mathf.Max(0f, _toxicityPerSecond == null ? 4f : _toxicityPerSecond.Value); }
        internal static float ToxicHealthRate() { return Mathf.Max(0f, _healthPerSecond == null ? 2.5f : _healthPerSecond.Value); }
        internal static float BiteDamage() { return Mathf.Max(0f, _biteDamage == null ? 18f : _biteDamage.Value); }
        internal static string MeshName() { return MeshFile; }
        internal static string DiffuseName() { return DiffuseFile; }
        internal static string NormalName() { return NormalFile; }
        internal static bool Primary(CrocodileSwimmer swimmer) { return _swimmer == swimmer; }
    }

    /// <summary>
    /// Visual and motion layer on the native Animal_AI/PhotonView root. Every
    /// client evaluates the same Photon clock ellipse, while only the native
    /// boss remains authoritative for health and death.
    /// </summary>
    public sealed class CrocodileSwimmer : MonoBehaviour
    {
        Component _animal;
        GameObject _model;
        Mesh _animatedMesh;
        Vector3[] _baseVertices;
        Vector3[] _workingVertices;
        SkinnedMeshRenderer[] _hidden;
        NavMeshAgent[] _agents;
        Rigidbody _body;
        ParticleSystem _fumes;
        Light _glow;
        readonly List<BoxCollider> _hitBoxes = new List<BoxCollider>();
        bool _ready;
        bool _dead;
        bool _detaching;
        float _nextExposure;
        float _nextBite;
        float _deadAt;
        int _normalFrame;

        internal void Setup(Component animal)
        {
            _animal = animal;
            if (_ready) return;
            _ready = true;
            _hidden = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < _hidden.Length; i++)
                if (_hidden[i] != null) _hidden[i].enabled = false;

            _agents = GetComponentsInChildren<NavMeshAgent>(true);
            for (int i = 0; i < _agents.Length; i++)
            {
                if (_agents[i] == null) continue;
                try
                {
                    _agents[i].updatePosition = false;
                    _agents[i].updateRotation = false;
                    _agents[i].speed = 0f;
                }
                catch { }
            }

            // No Animator disabling here: this build does not reference
            // UnityEngine.AnimationModule (see RevivalTechnical.cs Zeile()
            // for the same constraint on TextAnchor/FontStyle). The native
            // skeleton's root motion, if any, is harmless because it is
            // invisible (SkinnedMeshRenderers above are disabled) and our own
            // Update/LateUpdate below overwrite transform position/rotation
            // every frame regardless of what drove them.

            _body = GetComponent<Rigidbody>();
            if (_body != null)
            {
                _body.useGravity = false;
                _body.isKinematic = true;
            }

            BuildModel();
        }

        void BuildModel()
        {
            Mesh source = Assets.Load(Crocodile.MeshName());
            if (source == null)
            {
                RestoreNative();
                RevivalPlugin.L.LogError("Crocodile: " + Crocodile.MeshName()
                    + " missing; native boss skin retained.");
                return;
            }

            _model = new GameObject("NDR_ToxicCrocodile_Model");
            _model.transform.SetParent(transform, false);
            _model.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            _model.layer = gameObject.layer;

            _animatedMesh = UnityEngine.Object.Instantiate(source) as Mesh;
            _animatedMesh.name = "crocodile_swimming";
            _animatedMesh.MarkDynamic();
            Bounds bounds = _animatedMesh.bounds;
            bounds.Expand(new Vector3(1.4f, 0.7f, 0.5f));
            _animatedMesh.bounds = bounds;
            _baseVertices = _animatedMesh.vertices;
            _workingVertices = new Vector3[_baseVertices.Length];

            MeshFilter filter = _model.AddComponent<MeshFilter>();
            filter.sharedMesh = _animatedMesh;
            MeshRenderer renderer = _model.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = BuildMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;

            AddHitBox("Body", new Vector3(0f, 0.15f, -0.35f),
                      new Vector3(2.15f, 0.85f, 4.7f));
            AddHitBox("Head", new Vector3(0f, 0.12f, 3.05f),
                      new Vector3(1.55f, 0.72f, 1.65f));
            AddHitBox("Tail", new Vector3(0f, 0.08f, -3.30f),
                      new Vector3(0.95f, 0.50f, 3.1f));
            BuildFumes();
        }

        Material BuildMaterial()
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");
            Material material = new Material(shader);
            material.name = "NDR toxic crocodile";
            material.mainTexture = Assets.Texture(Crocodile.DiffuseName(), false, true);
            if (material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap",
                    Assets.Texture(Crocodile.NormalName(), true, true));
                material.EnableKeyword("_NORMALMAP");
            }
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.31f);
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.black);
                material.DisableKeyword("_EMISSION");
            }
            return material;
        }

        void AddHitBox(string label, Vector3 centre, Vector3 size)
        {
            // Keep the colliders on the PhotonView root. The game's firearm
            // path starts with Extensions.GetPhotonView(collider.gameObject),
            // so a decorative child collider could absorb a shot before that
            // lookup reaches the native Animal_AI proxy.
            BoxCollider collider = gameObject.AddComponent<BoxCollider>();
            collider.center = centre;
            collider.size = size;
            collider.isTrigger = false;
            _hitBoxes.Add(collider);
        }

        void BuildFumes()
        {
            GameObject fumes = new GameObject("NDR_Crocodile_Toxin");
            fumes.transform.SetParent(_model.transform, false);
            fumes.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            fumes.layer = gameObject.layer;
            _fumes = fumes.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = _fumes.main;
            main.duration = 3f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.18f, 0.65f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.32f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.15f, 1f, 0.08f, 0.48f),
                new Color(0.04f, 0.45f, 0.02f, 0.12f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.08f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 60;
            ParticleSystem.EmissionModule emission = _fumes.emission;
            emission.enabled = true;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(11f);
            ParticleSystem.ShapeModule shape = _fumes.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1.2f;
            ParticleSystemRenderer particleRenderer =
                fumes.GetComponent<ParticleSystemRenderer>();
            if (particleRenderer != null)
            {
                Shader shader = Shader.Find("Particles/Additive");
                if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
                if (shader != null) particleRenderer.sharedMaterial = new Material(shader);
                particleRenderer.shadowCastingMode =
                    UnityEngine.Rendering.ShadowCastingMode.Off;
                particleRenderer.receiveShadows = false;
            }

            _glow = fumes.AddComponent<Light>();
            _glow.type = LightType.Point;
            _glow.color = new Color(0.18f, 1f, 0.06f, 1f);
            _glow.range = 7f;
            _glow.intensity = 1.35f;
            _glow.shadows = LightShadows.None;
        }

        void Update()
        {
            if (!_ready || _dead || !Crocodile.Primary(this)
                || Time.time < _nextExposure) return;
            const float step = 0.25f;
            _nextExposure = Time.time + step;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return;
            Vector3 delta = player.transform.position - transform.position;
            float vertical = Mathf.Abs(delta.y);
            delta.y = 0f;
            float distance = delta.magnitude;
            if (vertical > 4.5f || distance > Crocodile.ToxicRadius()) return;

            float protection = GasLauncher.Protection();
            GasLauncher.AddToxicity(Crocodile.ToxicityRate() * step * protection);
            GasLauncher.HurtPlayer(Crocodile.ToxicHealthRate() * step * protection);
            Crocodile.Exposure(protection <= 0.02f);

            if (distance <= 4.2f && vertical <= 2.2f && Time.time >= _nextBite)
            {
                _nextBite = Time.time + 1.7f;
                GasLauncher.HurtPlayer(Crocodile.BiteDamage());
            }
        }

        void LateUpdate()
        {
            if (!_ready) return;
            if (_dead)
            {
                Sink();
                return;
            }
            if (_animal == null || !Crocodile.Alive(_animal))
            {
                Die();
                return;
            }

            double clock = Crocodile.NetworkClock();
            Vector3 target = Crocodile.SwimPoint(clock);
            target.y = Crocodile.SurfaceY()
                + Mathf.Sin((float)clock * 1.65f) * 0.055f;
            Vector3 tangent = Crocodile.SwimTangent(clock);
            transform.position = target;
            if (tangent.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(tangent), Mathf.Clamp01(Time.deltaTime * 4f));
            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
            AnimateMesh((float)clock);
        }

        void AnimateMesh(float clock)
        {
            if (_animatedMesh == null || _baseVertices == null
                || _workingVertices == null || _model == null) return;
            for (int i = 0; i < _baseVertices.Length; i++)
            {
                Vector3 point = _baseVertices[i];
                if (point.z < -1.1f)
                {
                    float weight = Mathf.Clamp01((-point.z - 1.1f) / 3.9f);
                    point.x += Mathf.Sin(clock * 3.0f - point.z * 1.18f)
                        * 0.48f * weight * weight;
                }
                _workingVertices[i] = point;
            }
            _animatedMesh.vertices = _workingVertices;
            _normalFrame++;
            if ((_normalFrame % 3) == 0) _animatedMesh.RecalculateNormals();
            _model.transform.localPosition = new Vector3(0f,
                0.08f + Mathf.Sin(clock * 1.65f) * 0.035f, 0f);
            _model.transform.localRotation = Quaternion.Euler(
                Mathf.Sin(clock * 1.15f) * 1.8f, 0f,
                Mathf.Sin(clock * 1.70f) * 1.1f);
        }

        internal void Die()
        {
            if (_dead) return;
            _dead = true;
            _deadAt = Time.time;
            if (_fumes != null)
            {
                ParticleSystem.EmissionModule emission = _fumes.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            }
            if (_glow != null) _glow.enabled = false;
            Crocodile.Gone(this, true);
        }

        void Sink()
        {
            if (_model == null) return;
            float age = Mathf.Max(0f, Time.time - _deadAt);
            Vector3 local = _model.transform.localPosition;
            local.y = 0.08f - Mathf.Min(0.65f, age * 0.045f);
            _model.transform.localPosition = local;
            _model.transform.localRotation = Quaternion.Euler(
                Mathf.Min(18f, age * 1.3f), 0f, Mathf.Min(12f, age * 0.8f));
        }

        internal void Detach()
        {
            _detaching = true;
            RestoreNative();
            RemoveHitBoxes();
            if (_model != null) UnityEngine.Object.Destroy(_model);
            UnityEngine.Object.Destroy(this);
        }

        void RemoveHitBoxes()
        {
            for (int i = 0; i < _hitBoxes.Count; i++)
                if (_hitBoxes[i] != null) UnityEngine.Object.Destroy(_hitBoxes[i]);
            _hitBoxes.Clear();
        }

        void RestoreNative()
        {
            if (_hidden == null) return;
            for (int i = 0; i < _hidden.Length; i++)
                if (_hidden[i] != null) _hidden[i].enabled = true;
        }

        void OnDestroy()
        {
            if (_detaching) RestoreNative();
            RemoveHitBoxes();
            if (_animatedMesh != null) UnityEngine.Object.Destroy(_animatedMesh);
            Crocodile.Gone(this, _dead);
        }
    }
}

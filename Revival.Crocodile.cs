// Next Day: Survival - Revival Toolkit
//
// TOXIC CROCODILE BOSS. The game already has a durable, networked animal boss
// contract. Resources contains AnimalsSpawn/BearBoss_Spawn and firearm damage
// reaches its Animal_AI through the native NetworkApplyDamage RPC. This feature
// keeps that contract for hit registration, health, death, loot, Photon late
// joins and the boss HUD, but replaces the skin and locomotion with an original
// Blender crocodile that lives in the large lake south of Point 12.
//
// The lake centre is read from assets/editor/basemap.png. That artwork covers
// x/z -2500..2500; pixel (914, 328) maps to (1965, 900). The patrol loop
// occupies the broad western lobe, away from the wooded island. Its Y comes
// from the actual water renderer/collider at runtime because the 2D artwork has
// no elevation.
//
// THE HUNTER (6.45). The native bear AI is switched off for this one animal
// (StateAction, NetworkAttackState and the attack states are refused), and the
// Photon master runs the crocodile instead:
//
//   - CrocodileLake samples the terrain around the lake once, floods the water
//     from the loop centre and marks every cell within ShoreReach metres of
//     that water as shore. The crocodile may swim anywhere in the lake and
//     crawl onto that immediate shore, never further.
//   - It patrols, basks on the bank with its jaws open, and hunts any player
//     inside that zone: stalk, charge, lunge. On land it is faster than a
//     running player in a straight line but turns badly; a sharp change of
//     direction (a zigzag) makes it overshoot and lose a second, and three of
//     them make it give up. A player caught while running straight away from
//     it is eaten (EatDamage); anyone else is bitten (BiteDamage).
//   - The master broadcasts position, heading and mode on one Photon event;
//     every client interpolates and animates locally. Bites are decided on the
//     victim's own client from the crocodile it sees and its own movement
//     history, then announced so the master can back off or roll its prey.
//   - The Blender rig sidecar (crocodile_rig.bin) names the jaw and the four
//     legs; the runtime turns them about their pivots: gait on land, legs
//     tucked while swimming, gape while basking, snap on a bite, death roll.
//   - The game's own boss bar (PlayerBossController/HUD_BossBar) is raised for
//     the hunted player and anyone close, with the crocodile's own name.
//
// C# 3.0. Player-facing strings use Loc.T and real Cyrillic.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    /// <summary>
    /// Spawns and adopts one native bear-boss proxy, turns its visible body into
    /// the Blender crocodile, and lets it hunt in and around the Point 12 lake.
    /// </summary>
    public static class Crocodile
    {
        const float DefaultX = 1965f;
        const float DefaultZ = 900f;
        const float AutoWater = -9999f;
        const float StartDelay = 24f;
        const float ScanRadius = 360f;
        const int DefaultEventCode = 164;

        const string MeshFile = "crocodile.ndmesh";
        const string DiffuseFile = "crocodile_diffuse.png";
        const string NormalFile = "crocodile_normal.png";
        const string RigFile = "crocodile_rig.bin";

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
        static ConfigEntry<float> _eatDamage;
        static ConfigEntry<float> _shoreReach;
        static ConfigEntry<float> _huntRadius;
        static ConfigEntry<float> _landSpeed;
        static ConfigEntry<float> _sprintEdge;
        static ConfigEntry<float> _baskMinutes;
        static ConfigEntry<float> _bodyScale;
        static ConfigEntry<bool> _bossBar;
        static ConfigEntry<int> _eventCode;

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

        static MethodInfo _healthGetter;
        static Type _healthType;

        static Type _photonType;
        static MethodInfo _masterGetter;
        static MethodInfo _networkTimeGetter;
        static MethodInfo _localPlayerGetter;
        static PropertyInfo _actorIdProperty;

        static readonly Dictionary<MethodBase, int> DamageArguments =
            new Dictionary<MethodBase, int>();
        // Instance ids of the Animal_AI components that are crocodiles. The
        // native AI prefixes run for every animal every frame, so they ask a
        // hash set instead of walking the hierarchy.
        static readonly HashSet<int> CrocAnimals = new HashSet<int>();

        static GUIStyle _nameStyle;
        static GUIStyle _shadowStyle;
        static GUIStyle _warningStyle;
        static string _notice = "";
        static float _noticeUntil;
        static bool _noticeSticky;

        public static void BindConfig(ConfigFile config)
        {
            _enabled = config.Bind("Crocodile", "Enabled", true,
                Loc.T("Токсичный крокодил-босс живет в озере у нейтральной базы.",
                      "A toxic crocodile boss lives in the lake near the neutral base."));
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
                Loc.T("Скорость спокойного плавания в метрах в секунду.",
                      "Calm swimming speed in metres per second."));
            _waterOverride = config.Bind("Crocodile", "WaterY", AutoWater,
                Loc.T("Высота воды. -9999 определяет ее автоматически.",
                      "Water surface Y. -9999 detects it from the scene."));
            _fallbackWater = config.Bind("Crocodile", "FallbackWaterY", 430f,
                Loc.T("Запасная высота воды, если объект воды еще не загружен.",
                      "Fallback water Y if the scene water object is not loaded yet."));
            _damageTaken = config.Bind("Crocodile", "DamageTakenMultiplier", 0.05f,
                Loc.T("Доля входящего урона. 0.05 дает боссу двадцатикратную живучесть.",
                      "Incoming damage fraction; 0.05 gives twenty times effective health."));
            _respawnMinutes = config.Bind("Crocodile", "RespawnMinutes", 30f,
                Loc.T("Через сколько минут после смерти босс вернется. 0 = никогда.",
                      "Minutes before the defeated boss returns; 0 means never."));
            _toxicRadius = config.Bind("Crocodile", "ToxicRadius", 14f,
                Loc.T("Радиус токсичных испарений в метрах.",
                      "Radius of the toxic fumes in metres."));
            _toxicityPerSecond = config.Bind("Crocodile", "ToxicityPerSecond", 4f,
                Loc.T("Рост отравления в секунду без защиты.",
                      "Toxicity gained per second without protection."));
            _healthPerSecond = config.Bind("Crocodile", "ToxicDamagePerSecond", 4f,
                Loc.T("Урон здоровью от испарений в секунду без защиты.",
                      "Health damage per second from fumes without protection."));
            _biteDamage = config.Bind("Crocodile", "BiteDamage", 50f,
                Loc.T("Урон от укуса, если игрок не убегает по прямой.",
                      "Bite damage when the player is not fleeing in a straight line."));
            _eatDamage = config.Bind("Crocodile", "EatDamage", 250f,
                Loc.T("Урон, когда крокодил догоняет игрока, бегущего по прямой. 250 = смерть.",
                      "Damage when it catches a player running straight away; 250 kills."));
            _shoreReach = config.Bind("Crocodile", "ShoreReach", 90f,
                Loc.T("Насколько метров от воды крокодил выходит на берег.",
                      "How many metres from the water the crocodile may crawl ashore."));
            _huntRadius = config.Bind("Crocodile", "HuntRadius", 80f,
                Loc.T("Дистанция, на которой крокодил замечает игрока у озера.",
                      "Distance at which it notices a player in or beside the lake."));
            _landSpeed = config.Bind("Crocodile", "LandChargeSpeed", 8.5f,
                Loc.T("Минимальная скорость рывка по суше, м/с.",
                      "Minimum charge speed on land in metres per second."));
            _sprintEdge = config.Bind("Crocodile", "SprintAdvantage", 1.6f,
                Loc.T("Во сколько раз рывок быстрее убегающего игрока по прямой.",
                      "How much faster than a fleeing player its straight charge is."));
            _baskMinutes = config.Bind("Crocodile", "BaskEveryMinutes", 4f,
                Loc.T("Как часто крокодил выползает греться на берег. 0 = никогда.",
                      "How often it crawls onto the bank to bask; 0 means never."));
            _bodyScale = config.Bind("Crocodile", "BodyScale", 1.6f,
                Loc.T("Размер крокодила относительно модели (около 9 м). Пасть и укус растут вместе с ним.",
                      "Crocodile size relative to the ~9 m model; jaws and bite reach grow with it."));
            _bossBar = config.Bind("Crocodile", "BossBar", true,
                Loc.T("Показывать полосу здоровья босса, когда крокодил охотится рядом.",
                      "Show the game's boss health bar while the crocodile hunts nearby."));
            _eventCode = config.Bind("Crocodile", "NetworkEventCode", DefaultEventCode,
                Loc.T("Код события Photon (0..199) для положения и укусов крокодила. "
                      + "Занято: 160-162, 170-185, 190, 191.",
                      "Photon event code (0..199) for crocodile pose and bites. "
                      + "Taken: 160-162, 170-185, 190, 191."));
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

                // The native bear brain would chase and claw with its own
                // Head_Bone, which rides along inside the crocodile. StateAction
                // is the owner's whole AI step, NetworkAttackState the remote
                // copy, and SetAnimalState(3/4) the only door to
                // AttackDamageDelay (IL: SetAnimalState IL_0606/IL_069D).
                // Health, death, loot and the boss data update stay native.
                MethodInfo quiet = typeof(Crocodile).GetMethod("NativeAiPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo state = typeof(Crocodile).GetMethod("NativeStatePrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                int silenced = 0;
                string[] brain = new string[] { "StateAction", "NetworkAttackState" };
                for (int i = 0; i < brain.Length; i++)
                {
                    MethodInfo target = AccessTools.Method(animal, brain[i], Type.EmptyTypes, null);
                    if (target == null) continue;
                    harmony.Patch(target, new HarmonyMethod(quiet), null, null, null, null);
                    silenced++;
                }
                MethodInfo setState = AccessTools.Method(animal, "SetAnimalState", null, null);
                if (setState != null && setState.GetParameters().Length == 1)
                {
                    harmony.Patch(setState, new HarmonyMethod(state), null, null, null, null);
                    silenced++;
                }
                RevivalPlugin.L.LogInfo("Crocodile: native bear AI silenced for the "
                    + "crocodile on " + silenced + " of 3 method(s).");
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

        static bool NativeAiPrefix(object __instance)
        {
            Component component = __instance as Component;
            return component == null || !CrocAnimals.Contains(component.GetInstanceID());
        }

        static bool NativeStatePrefix(object __instance, object[] __args)
        {
            Component component = __instance as Component;
            if (component == null || !CrocAnimals.Contains(component.GetInstanceID()))
                return true;
            try
            {
                int state = Convert.ToInt32(__args[0]);
                return state != 3 && state != 4;
            }
            catch { return true; }
        }

        static bool IsCrocodile(object instance)
        {
            Component component = instance as Component;
            if (component == null) return false;
            if (CrocAnimals.Contains(component.GetInstanceID())) return true;
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
                CrocodileLake.Reset();
                return;
            }

            if (_worldSince <= 0f)
            {
                _worldSince = Time.time;
                _nextTick = 0f;
                _waterMeasured = false;
                _nextWaterScan = 0f;
            }
            CrocodileNet.EnsureHooked();
            if (_waterMeasured && IsMaster())
                CrocodileLake.Step(Centre(), RadiusX() + 260f, RadiusZ() + 260f,
                                   SurfaceY(), ShoreReach());

            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.75f;

            SurfaceY();

            if (_animal != null && !Alive(_animal))
            {
                if (_swimmer != null) _swimmer.Die();
                MarkDead();
            }

            if (_swimmer != null && BossBarWanted() && _swimmer.EngagesLocal())
                ShowBossBar(_animal);

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
                + position + "; Blender skin and hunter controller "
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
            CrocAnimals.Add(animal.GetInstanceID());
            CrocodileSwimmer swimmer = root.GetComponent<CrocodileSwimmer>();
            if (swimmer == null) swimmer = root.AddComponent<CrocodileSwimmer>();
            swimmer.Setup(animal);
            _animal = animal;
            _swimmer = swimmer;
            _deadAt = -1f;
            _spawnIssued = true;
            NameBoss(animal);
            RevivalPlugin.L.LogInfo("Crocodile: adopted " + animal.name
                + ", water Y " + SurfaceY().ToString("0.00")
                + " from " + _waterSource + ", damage x"
                + DamageTaken().ToString("0.00") + ", view " + ViewId(root) + ".");
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

        /// <summary>
        /// The boss is alive while its native health is above zero. Health is
        /// the one value every client holds (NetworkOnChangeAnimalHealth), and
        /// Animal_AI has no IsAlive/IsDead member. An inactive object is NOT
        /// dead: SetVisible(false) parks a sleeping animal with SetActive.
        /// </summary>
        internal static bool Alive(Component animal)
        {
            if (animal == null || animal.gameObject == null) return false;
            try
            {
                Type type = animal.GetType();
                if (_healthType != type)
                {
                    _healthType = type;
                    _healthGetter = AccessTools.PropertyGetter(type, "Get_Health");
                }
                if (_healthGetter != null)
                    return Convert.ToSingle(_healthGetter.Invoke(animal, null)) > 0f;
            }
            catch { }
            return animal.gameObject.activeInHierarchy;
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

        internal static void Forget(Component animal)
        {
            if (animal != null) CrocAnimals.Remove(animal.GetInstanceID());
        }

        static void ClearLocal(bool detach)
        {
            if (detach && _swimmer != null) _swimmer.Detach();
            _animal = null;
            _swimmer = null;
            _spawnIssued = false;
        }

        // ------------------------------------------------------------ boss bar

        static Type _bossControllerType;
        static MethodInfo _setBossData;
        static FieldInfo _optionsField;
        static FieldInfo _bossDataField;
        static FieldInfo _customNameField;
        static bool _bossBarWarned;

        static object BossData(Component animal)
        {
            if (animal == null) return null;
            if (_optionsField == null)
                _optionsField = AccessTools.Field(animal.GetType(), "_animalOptions");
            object options = _optionsField == null ? null : _optionsField.GetValue(animal);
            if (options == null) return null;
            if (_bossDataField == null)
                _bossDataField = AccessTools.Field(options.GetType(), "bossData");
            return _bossDataField == null ? null : _bossDataField.GetValue(options);
        }

        /// <summary>
        /// HUD_BossBar.InitProgressBar prints "$BOSS_BEAR" unless customName is
        /// set; LocalizationManager returns an unknown key longer than five
        /// characters as itself, so the name goes in verbatim. Native hits
        /// (FireOneShot IL_06F0) then show the crocodile's name as well.
        /// </summary>
        static void NameBoss(Component animal)
        {
            try
            {
                object data = BossData(animal);
                if (data == null) return;
                if (_customNameField == null)
                    _customNameField = AccessTools.Field(data.GetType(), "customName");
                if (_customNameField != null)
                    _customNameField.SetValue(data, Loc.T("ТОКСИЧНЫЙ КРОКОДИЛ",
                                                          "TOXIC CROCODILE"));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crocodile boss name: " + ex.Message);
            }
        }

        /// <summary>
        /// Raises the game's own boss bar on the local player exactly as a hit
        /// on a boss does: PlayerBossController.SetBossData(bossData). The
        /// controller keeps it up for its _hideTime and closes it past 200 m or
        /// on death; calling again only renews that time.
        /// </summary>
        static void ShowBossBar(Component animal)
        {
            if (animal == null || _bossBarWarned) return;
            try
            {
                if (_bossControllerType == null)
                {
                    _bossControllerType = RevivalPlugin.TypeByName("PlayerBossController");
                    if (_bossControllerType != null)
                        _setBossData = AccessTools.Method(_bossControllerType, "SetBossData", null, null);
                }
                GameObject player = MapTools.LocalPlayer();
                if (player == null || _bossControllerType == null || _setBossData == null) return;
                Component controller = player.GetComponentInChildren(_bossControllerType);
                if (controller == null) controller = player.GetComponentInParent(_bossControllerType);
                object data = BossData(animal);
                if (controller == null || data == null) return;
                _setBossData.Invoke(controller, new object[] { data });
            }
            catch (Exception ex)
            {
                _bossBarWarned = true;
                RevivalPlugin.L.LogWarning("Crocodile boss bar: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ geometry

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

        /// <summary>A point a little further along the patrol ellipse from
        /// wherever the crocodile is now, so a hunt or a bask can end anywhere
        /// and the loop simply resumes.</summary>
        internal static Vector3 LoopGoal(Vector3 from)
        {
            Vector3 centre = Centre();
            float angle = Mathf.Atan2((from.x - centre.x) / RadiusX(),
                                      (from.z - centre.z) / RadiusZ());
            angle += 0.16f;
            return new Vector3(centre.x + Mathf.Sin(angle) * RadiusX(), 0f,
                               centre.z + Mathf.Cos(angle) * RadiusZ());
        }

        internal static bool InsideLoop(Vector3 p, float scale)
        {
            Vector3 centre = Centre();
            float dx = (p.x - centre.x) / (RadiusX() * scale);
            float dz = (p.z - centre.z) / (RadiusZ() * scale);
            return dx * dx + dz * dz <= 1f;
        }

        static Terrain[] _terrains;
        static float _terrainsAt = -99f;

        /// <summary>Height of the rendered terrain under x/z. Terrain data
        /// needs no collider (RevivalTroopInsertion.TerrainHeight explains why
        /// that matters away from the player).</summary>
        internal static bool Ground(float x, float z, out float y)
        {
            y = 0f;
            if (_terrains == null || Time.time - _terrainsAt > 10f)
            {
                _terrainsAt = Time.time;
                try { _terrains = Terrain.activeTerrains; }
                catch { _terrains = null; }
            }
            if (_terrains == null) return false;
            for (int i = 0; i < _terrains.Length; i++)
            {
                Terrain terrain = _terrains[i];
                if (terrain == null || terrain.terrainData == null) continue;
                Vector3 origin = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z
                    || x > origin.x + size.x || z > origin.z + size.z) continue;
                y = origin.y + terrain.SampleHeight(new Vector3(x, 0f, z));
                return true;
            }
            return false;
        }

        /// <summary>Water surface or ground, whichever is higher: the height
        /// the crocodile's root stands on.</summary>
        internal static float Support(float x, float z, out float landWeight)
        {
            float water = SurfaceY();
            float ground;
            if (!Ground(x, z, out ground))
            {
                landWeight = 0f;
                return water;
            }
            // Fully afloat 0.75 m deep, fully standing where ground meets water.
            landWeight = Mathf.Clamp01((ground - (water - 0.75f)) / 0.75f);
            return Mathf.Max(water, ground);
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

        // ------------------------------------------------------------- players

        static readonly List<GameObject> _players = new List<GameObject>();
        static float _playersAt = -99f;
        static PropertyInfo _ngsInstance;
        static FieldInfo _ngsPlayers;
        static Type _statesType;
        static FieldInfo _characterState;
        static Type _vehicleManagerType;
        static FieldInfo _inVehiclePose;
        static Type _photonViewType;
        static FieldInfo _ownerId;
        static PropertyInfo _viewId;
        static bool _playersLooked;

        /// <summary>NetworkGameServer.Instance.NetworkPlayers, refreshed twice a
        /// second - the same list the patrol gun reads (Revival.Patrol.cs).</summary>
        internal static List<GameObject> Players()
        {
            if (Time.time - _playersAt < 0.5f) return _players;
            _playersAt = Time.time;
            _players.Clear();
            try
            {
                if (!_playersLooked)
                {
                    _playersLooked = true;
                    Type ngs = RevivalPlugin.TypeByName("NetworkGameServer");
                    if (ngs != null)
                    {
                        _ngsInstance = ngs.GetProperty("Instance",
                            BindingFlags.Public | BindingFlags.Static);
                        _ngsPlayers = AccessTools.Field(ngs, "NetworkPlayers");
                    }
                    _statesType = RevivalPlugin.TypeByName("PlayerStatesController");
                    if (_statesType != null)
                        _characterState = AccessTools.Field(_statesType, "_characterState");
                    _vehicleManagerType = RevivalPlugin.TypeByName("PlayerVehicleManager");
                    if (_vehicleManagerType != null)
                        _inVehiclePose = AccessTools.Field(_vehicleManagerType, "_inVehiclePose");
                }
                object server = _ngsInstance == null ? null : _ngsInstance.GetValue(null, null);
                IList list = server == null || _ngsPlayers == null
                    ? null : _ngsPlayers.GetValue(server) as IList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        GameObject go = list[i] as GameObject;
                        if (go != null) _players.Add(go);
                    }
                if (_players.Count == 0)
                {
                    GameObject local = MapTools.LocalPlayer();
                    if (local != null) _players.Add(local);
                }
            }
            catch { }
            return _players;
        }

        /// <summary>Alive (character state 8 is Death, as Animal_AI's own attack
        /// check reads it) and on foot (PlayerVehicleManager._inVehiclePose).</summary>
        internal static bool Huntable(GameObject player)
        {
            if (player == null || !player.activeInHierarchy) return false;
            try
            {
                if (_statesType != null && _characterState != null)
                {
                    Component states = player.GetComponent(_statesType);
                    if (states != null
                        && Convert.ToInt32(_characterState.GetValue(states)) == 8) return false;
                }
                if (_vehicleManagerType != null && _inVehiclePose != null)
                {
                    Component vehicle = player.GetComponent(_vehicleManagerType);
                    if (vehicle != null && Convert.ToBoolean(_inVehiclePose.GetValue(vehicle)))
                        return false;
                }
            }
            catch { }
            return true;
        }

        static void LookUpView()
        {
            if (_photonViewType != null) return;
            _photonViewType = RevivalPlugin.TypeByName("PhotonView");
            if (_photonViewType == null) return;
            _ownerId = AccessTools.Field(_photonViewType, "ownerId");
            _viewId = _photonViewType.GetProperty("viewID",
                BindingFlags.Public | BindingFlags.Instance);
        }

        internal static int ActorOf(GameObject player)
        {
            try
            {
                LookUpView();
                if (player == null || _photonViewType == null || _ownerId == null) return -1;
                Component view = player.GetComponent(_photonViewType);
                return view == null ? -1 : Convert.ToInt32(_ownerId.GetValue(view));
            }
            catch { return -1; }
        }

        internal static int ViewId(GameObject root)
        {
            try
            {
                LookUpView();
                if (root == null || _photonViewType == null || _viewId == null) return 0;
                Component view = root.GetComponent(_photonViewType);
                return view == null ? 0 : Convert.ToInt32(_viewId.GetValue(view, null));
            }
            catch { return 0; }
        }

        internal static int LocalActor()
        {
            try
            {
                Type photon = PhotonType();
                if (photon == null) return -1;
                if (_localPlayerGetter == null)
                    _localPlayerGetter = AccessTools.PropertyGetter(photon, "player");
                object player = _localPlayerGetter == null ? null : _localPlayerGetter.Invoke(null, null);
                if (player == null) return -1;
                if (_actorIdProperty == null)
                    _actorIdProperty = player.GetType().GetProperty("ID",
                        BindingFlags.Public | BindingFlags.Instance);
                return _actorIdProperty == null ? -1
                    : Convert.ToInt32(_actorIdProperty.GetValue(player, null));
            }
            catch { return -1; }
        }

        internal static GameObject PlayerByActor(int actor)
        {
            if (actor < 0) return null;
            List<GameObject> players = Players();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && ActorOf(players[i]) == actor) return players[i];
            return null;
        }

        // ------------------------------------------------------ player damage

        static Component _lifeManager;
        static float _lifeManagerAt = -99f;
        static MethodInfo _applyDamage;
        static bool _damageWarned;

        /// <summary>
        /// Damages the LOCAL player the way Animal_AI's own attack does: the
        /// PlayerApplyDamage RPC target with bodyPart 1 (torso, x1) and
        /// damageType 10 (<AttackDamageDelay>c__Iterator1 IL_0121..IL_0177).
        /// Type 10 is neither NPC nor network-player damage, so gear
        /// regeneration does not shave it (PlayerApplyDamage IL_00C1..IL_00DA)
        /// and a lethal amount reaches PlayerDeath with the game's own cause.
        /// </summary>
        internal static void HurtLocal(float damage, Vector3 from)
        {
            if (damage <= 0f || _damageWarned) return;
            try
            {
                if (_lifeManager == null || Time.time - _lifeManagerAt > 2f)
                {
                    _lifeManagerAt = Time.time;
                    Type type = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                    GameObject player = MapTools.LocalPlayer();
                    _lifeManager = type == null || player == null ? null
                        : player.GetComponentInChildren(type);
                    if (_lifeManager == null && type != null && player != null)
                        _lifeManager = player.GetComponentInParent(type);
                    if (_lifeManager != null && _applyDamage == null)
                    {
                        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance
                            | BindingFlags.Public | BindingFlags.NonPublic);
                        for (int i = 0; i < methods.Length; i++)
                        {
                            if (methods[i].Name != "PlayerApplyDamage") continue;
                            ParameterInfo[] ps = methods[i].GetParameters();
                            if (ps.Length > 0 && ps[0].ParameterType == typeof(float))
                            {
                                _applyDamage = methods[i];
                                break;
                            }
                        }
                    }
                }
                if (_lifeManager == null) return;
                if (_applyDamage == null)
                {
                    _damageWarned = true;
                    RevivalPlugin.L.LogWarning("Crocodile: PlayerApplyDamage not found; "
                        + "bites cost no health.");
                    return;
                }
                ParameterInfo[] parameters = _applyDamage.GetParameters();
                object[] args = new object[parameters.Length];
                int ints = 0, vectors = 0;
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type pt = parameters[i].ParameterType;
                    if (i == 0) args[i] = damage;
                    else if (pt == typeof(int)) args[i] = (ints++ == 0) ? 1 : (ints == 2 ? 10 : 0);
                    else if (pt == typeof(Vector3)) args[i] = (vectors++ == 0) ? Vector3.zero : from;
                    else if (pt == typeof(float)) args[i] = 0f;
                    else if (pt == typeof(bool)) args[i] = false;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                _applyDamage.Invoke(_lifeManager, args);
            }
            catch (Exception ex)
            {
                _damageWarned = true;
                RevivalPlugin.L.LogWarning("Crocodile bite damage: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- network

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

        internal static bool IsMaster()
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

        internal static CrocodileSwimmer Primary() { return _swimmer; }

        // ------------------------------------------------------------------ HUD

        internal static void Exposure(bool protectedPlayer)
        {
            // The fume warning repeats four times a second; it must not wipe a
            // hunt, zigzag or death message off the screen.
            if (_noticeSticky && Time.time < _noticeUntil) return;
            Notice(protectedPlayer
                ? Loc.T("ФИЛЬТР СДЕРЖИВАЕТ ТОКСИН", "FILTER BLOCKING TOXIN")
                : Loc.T("ТОКСИЧНЫЕ ИСПАРЕНИЯ!", "TOXIC FUMES!"), 0.65f);
        }

        internal static void Notice(string message, float seconds)
        {
            _notice = message == null ? "" : message;
            _noticeUntil = Time.time + seconds;
            _noticeSticky = seconds > 1f;
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
                    if (distance < 280f && distance > 25f)
                    {
                        Camera camera = Camera.main;
                        if (camera != null)
                        {
                            Vector3 screen = camera.WorldToScreenPoint(
                                _swimmer.transform.position + Vector3.up * 2.2f);
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
                GUI.Label(new Rect(Screen.width * 0.5f - measured.x * 0.5f + 2f, 104f,
                                   measured.x, measured.y),
                          _notice, _shadowStyle);
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
            _shadowStyle.fontSize = 22;
            _shadowStyle.normal.textColor = Color.black;
            _warningStyle = new GUIStyle(_nameStyle);
            _warningStyle.fontSize = 22;
            _warningStyle.normal.textColor = new Color(0.55f, 1f, 0.08f, 1f);
        }

        static bool Enabled() { return _enabled == null || _enabled.Value; }
        static string SceneTag() { return _scene == null ? MapScene.Home : _scene.Value; }
        static bool BossBarWanted() { return _bossBar == null || _bossBar.Value; }
        internal static float RadiusX() { return Mathf.Clamp(_radiusX == null ? 110f : _radiusX.Value, 20f, 350f); }
        internal static float RadiusZ() { return Mathf.Clamp(_radiusZ == null ? 245f : _radiusZ.Value, 20f, 350f); }
        internal static float Speed() { return Mathf.Clamp(_speed == null ? 3.2f : _speed.Value, 0.3f, 12f); }
        internal static float DamageTaken() { return Mathf.Clamp(_damageTaken == null ? 0.05f : _damageTaken.Value, 0.02f, 1f); }
        internal static float RespawnMinutes() { return Mathf.Max(0f, _respawnMinutes == null ? 30f : _respawnMinutes.Value); }
        internal static float ToxicRadius() { return Mathf.Clamp(_toxicRadius == null ? 14f : _toxicRadius.Value, 0f, 40f); }
        internal static float ToxicityRate() { return Mathf.Max(0f, _toxicityPerSecond == null ? 4f : _toxicityPerSecond.Value); }
        internal static float ToxicHealthRate() { return Mathf.Max(0f, _healthPerSecond == null ? 4f : _healthPerSecond.Value); }
        internal static float BiteDamage() { return Mathf.Max(0f, _biteDamage == null ? 50f : _biteDamage.Value); }
        internal static float EatDamage() { return Mathf.Max(0f, _eatDamage == null ? 250f : _eatDamage.Value); }
        internal static float ShoreReach() { return Mathf.Clamp(_shoreReach == null ? 90f : _shoreReach.Value, 0f, 240f); }
        internal static float HuntRadius() { return Mathf.Clamp(_huntRadius == null ? 80f : _huntRadius.Value, 0f, 200f); }
        internal static float LandSpeed() { return Mathf.Clamp(_landSpeed == null ? 8.5f : _landSpeed.Value, 2f, 16f); }
        internal static float SprintEdge() { return Mathf.Clamp(_sprintEdge == null ? 1.6f : _sprintEdge.Value, 0.5f, 3f); }
        internal static float BodyScale() { return Mathf.Clamp(_bodyScale == null ? 1.6f : _bodyScale.Value, 0.5f, 3f); }
        internal static float BaskMinutes() { return Mathf.Max(0f, _baskMinutes == null ? 4f : _baskMinutes.Value); }
        internal static int EventCode() { return _eventCode == null ? DefaultEventCode : _eventCode.Value; }
        internal static string MeshName() { return MeshFile; }
        internal static string DiffuseName() { return DiffuseFile; }
        internal static string NormalName() { return NormalFile; }
        internal static string RigName() { return RigFile; }
        internal static bool Primary(CrocodileSwimmer swimmer) { return _swimmer == swimmer; }
    }

    /// <summary>
    /// Where the crocodile may go: the lake flooded from the loop centre plus a
    /// band of immediate shore. Built on the master from the terrain height
    /// data in slices of a few thousand samples per frame, once per world.
    /// Until it is ready (or if the lake bed is not terrain) the old swimming
    /// ellipse is the whole world.
    /// </summary>
    internal static class CrocodileLake
    {
        internal const byte Blocked = 0, Water = 1, Shore = 2;
        const float Cell = 2.5f;
        const float SwimDepth = 0.6f;
        const float ShoreRise = 6f;        // at the 30 m reach of 6.46; grows with the reach
        const int Budget = 8000;

        static float[] _ground;
        static byte[] _kind;
        static byte[] _reach;
        static int _nx, _nz, _cursor, _stage;
        static float _x0, _z0, _water, _reachMetres;

        internal static bool Ready { get { return _stage == 2; } }

        internal static void Reset()
        {
            _stage = 0;
            _ground = null;
            _kind = null;
            _reach = null;
        }

        internal static void Step(Vector3 centre, float halfX, float halfZ,
                                  float water, float reach)
        {
            if (_stage >= 2) return;
            if (_stage == 0)
            {
                _nx = Mathf.CeilToInt(2f * halfX / Cell) + 1;
                _nz = Mathf.CeilToInt(2f * halfZ / Cell) + 1;
                _x0 = centre.x - halfX;
                _z0 = centre.z - halfZ;
                _water = water;
                _reachMetres = reach;
                _ground = new float[_nx * _nz];
                _cursor = 0;
                _stage = 1;
            }
            int total = _nx * _nz;
            int end = Mathf.Min(total, _cursor + Budget);
            for (; _cursor < end; _cursor++)
            {
                float y;
                float x = _x0 + (_cursor % _nx) * Cell;
                float z = _z0 + (_cursor / _nx) * Cell;
                _ground[_cursor] = Crocodile.Ground(x, z, out y) ? y : float.NaN;
            }
            if (_cursor < total) return;
            Classify(centre);
        }

        static void Classify(Vector3 centre)
        {
            int total = _nx * _nz;
            _kind = new byte[total];
            _reach = new byte[total];
            for (int i = 0; i < total; i++) _reach[i] = 255;

            // Seed: the deepest cell near the loop centre.
            int cx = Mathf.RoundToInt((centre.x - _x0) / Cell);
            int cz = Mathf.RoundToInt((centre.z - _z0) / Cell);
            int seed = -1;
            float deepest = float.MaxValue;
            for (int dz = -24; dz <= 24; dz++)
                for (int dx = -24; dx <= 24; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= _nx || z >= _nz) continue;
                    float g = _ground[z * _nx + x];
                    if (float.IsNaN(g) || g >= _water - SwimDepth || g >= deepest) continue;
                    deepest = g;
                    seed = z * _nx + x;
                }
            if (seed < 0)
            {
                _stage = 3;
                RevivalPlugin.L.LogWarning("Crocodile lake: no terrain below the water "
                    + "(" + _water.ToString("0.00") + ") within 60 m of the loop centre; "
                    + "it stays on the swimming loop and cannot go ashore.");
                return;
            }

            Queue<int> open = new Queue<int>();
            _kind[seed] = Water;
            _reach[seed] = 0;
            open.Enqueue(seed);
            int water = 0;
            while (open.Count > 0)
            {
                int at = open.Dequeue();
                water++;
                int x = at % _nx, z = at / _nx;
                for (int n = 0; n < 4; n++)
                {
                    int nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0);
                    int nz = z + (n == 2 ? 1 : n == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= _nx || nz >= _nz) continue;
                    int next = nz * _nx + nx;
                    if (_kind[next] != Blocked) continue;
                    float g = _ground[next];
                    if (float.IsNaN(g) || g >= _water - SwimDepth) continue;
                    _kind[next] = Water;
                    _reach[next] = 0;
                    open.Enqueue(next);
                }
            }

            // Shore: breadth-first outward from every water cell, up to the
            // reach, over ground no higher than ShoreRise above the water.
            int steps = Mathf.Clamp(Mathf.CeilToInt(_reachMetres / Cell), 0, 250);
            float rise = Mathf.Max(ShoreRise, _reachMetres * 0.2f);
            for (int i = 0; i < total; i++)
                if (_kind[i] == Water) open.Enqueue(i);
            int shore = 0;
            while (open.Count > 0)
            {
                int at = open.Dequeue();
                int x = at % _nx, z = at / _nx;
                int d = _reach[at];
                if (d >= steps) continue;
                for (int n = 0; n < 8; n++)
                {
                    int nx = x + (n == 0 || n == 4 || n == 5 ? 1 : n == 1 || n == 6 || n == 7 ? -1 : 0);
                    int nz = z + (n == 2 || n == 4 || n == 6 ? 1 : n == 3 || n == 5 || n == 7 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= _nx || nz >= _nz) continue;
                    int next = nz * _nx + nx;
                    if (_reach[next] <= d + 1) continue;
                    float g = _ground[next];
                    if (float.IsNaN(g) || g > _water + rise) continue;
                    if (_kind[next] == Blocked) shore++;
                    if (_kind[next] != Water) _kind[next] = Shore;
                    _reach[next] = (byte)(d + 1);
                    open.Enqueue(next);
                }
            }

            if (water < 150)
            {
                _stage = 3;
                RevivalPlugin.L.LogWarning("Crocodile lake: only " + water + " water cells "
                    + "flood from the centre; the lake bed is not terrain here, so it "
                    + "stays on the swimming loop.");
                return;
            }
            _stage = 2;
            RevivalPlugin.L.LogInfo("Crocodile lake: " + water + " water and " + shore
                + " shore cells of " + Cell + " m (" + (water * Cell * Cell / 10000f).ToString("0.0")
                + " ha water, shore reach " + _reachMetres.ToString("0") + " m) over a "
                + _nx + " x " + _nz + " grid at water " + _water.ToString("0.00") + ".");
        }

        static int Index(float x, float z)
        {
            int ix = Mathf.RoundToInt((x - _x0) / Cell);
            int iz = Mathf.RoundToInt((z - _z0) / Cell);
            if (ix < 0 || iz < 0 || ix >= _nx || iz >= _nz) return -1;
            return iz * _nx + ix;
        }

        internal static byte Kind(float x, float z)
        {
            if (!Ready) return Crocodile.InsideLoop(new Vector3(x, 0f, z), 1.02f) ? Water : Blocked;
            int i = Index(x, z);
            return i < 0 ? Blocked : _kind[i];
        }

        internal static bool Allowed(Vector3 p)
        {
            return Kind(p.x, p.z) != Blocked;
        }

        /// <summary>A player counts as inside the hunting ground when he stands
        /// in an allowed cell or right next to one, at the height of the water
        /// or the ground - not on a bridge or a roof above it.</summary>
        internal static bool HuntZone(Vector3 p)
        {
            bool near = false;
            for (int dz = -1; dz <= 1 && !near; dz++)
                for (int dx = -1; dx <= 1 && !near; dx++)
                    if (Kind(p.x + dx * Cell, p.z + dz * Cell) != Blocked) near = true;
            if (!near) return false;
            float land;
            float support = Crocodile.Support(p.x, p.z, out land);
            return p.y < support + 3f && p.y > support - 4f;
        }

        internal static bool NearestWater(Vector3 from, out Vector3 at)
        {
            at = from;
            if (!Ready)
            {
                at = Crocodile.LoopGoal(from);
                return true;
            }
            for (int ring = 0; ring <= 100; ring++)
                for (int dz = -ring; dz <= ring; dz++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring) continue;
                        float x = from.x + dx * Cell, z = from.z + dz * Cell;
                        int i = Index(x, z);
                        if (i < 0 || _kind[i] != Water) continue;
                        // A few cells further in, so it does not stop on the edge.
                        at = new Vector3(x, 0f, z) + (new Vector3(dx, 0f, dz)).normalized * 6f;
                        if (Kind(at.x, at.z) != Water) at = new Vector3(x, 0f, z);
                        return true;
                    }
            return false;
        }

        /// <summary>A bank cell within reach of the water's edge (at most two
        /// cells out, barely above the surface): where a crocodile basks.</summary>
        internal static bool BaskSpot(Vector3 near, System.Random random, out Vector3 spot)
        {
            spot = near;
            if (!Ready) return false;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                float angle = (float)(random.NextDouble() * Math.PI * 2.0);
                float distance = 10f + (float)random.NextDouble() * 70f;
                float x = near.x + Mathf.Sin(angle) * distance;
                float z = near.z + Mathf.Cos(angle) * distance;
                int i = Index(x, z);
                if (i < 0 || _kind[i] != Shore || _reach[i] < 1 || _reach[i] > 2) continue;
                if (_ground[i] > _water + 1.5f) continue;
                spot = new Vector3(x, 0f, z);
                return true;
            }
            return false;
        }
    }

    /// <summary>One Photon event code; the first float says what it is.</summary>
    internal static class CrocodileNet
    {
        internal const int State = 0;   // master -> all: pose and mode
        internal const int Bite = 1;    // victim -> all: bitten or eaten
        internal const int Zigzag = 2;  // master -> all: the target shook it off

        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optionsType;

        internal static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                int code = Crocodile.EventCode();
                if (code < 0 || code > 199)
                    throw new Exception("event code " + code + " is outside 0..199");
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) throw new Exception("PhotonNetwork missing");
                FieldInfo onEvent = AccessTools.Field(photon, "OnEventCall");
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                _optionsType = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (onEvent == null || _raise == null)
                    throw new Exception("RaiseEvent or OnEventCall missing");
                MethodInfo mine = typeof(CrocodileNet).GetMethod("OnPhotonEvent",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                Delegate current = onEvent.GetValue(null) as Delegate;
                onEvent.SetValue(null, Delegate.Combine(current, handler));
                _hooked = true;
                RevivalPlugin.L.LogInfo("Crocodile net hooked: event code " + code + ".");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogWarning("Crocodile net not hooked (" + ex.Message
                    + "); every client keeps its own calm swimming loop.");
            }
        }

        internal static bool Hooked { get { return _hooked; } }

        internal static void Send(float[] content, bool reliable)
        {
            if (!_hooked) return;
            try
            {
                object options = _optionsType == null ? null : Activator.CreateInstance(_optionsType);
                _raise.Invoke(null, new object[] {
                    (byte)Crocodile.EventCode(), content, reliable, options });
            }
            catch (Exception ex)
            {
                _failed = true;
                _hooked = false;
                RevivalPlugin.L.LogWarning("Crocodile net send: " + ex.Message);
            }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != Crocodile.EventCode()) return;
            try
            {
                float[] data = content as float[];
                if (data == null || data.Length < 2) return;
                CrocodileSwimmer swimmer = Crocodile.Primary();
                if (swimmer == null) return;
                swimmer.Receive(data, sender);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crocodile net receive: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Visual, motion and hunting layer on the native Animal_AI/PhotonView
    /// root. The Photon master runs the brain and broadcasts; every client
    /// interpolates, animates the rig and judges bites on its own player.
    /// The native boss stays authoritative for health and death.
    /// </summary>
    public sealed class CrocodileSwimmer : MonoBehaviour
    {
        // Modes travel over the network as floats; keep the numbers stable.
        internal const int Patrol = 0, Stalk = 1, Charge = 2, Lunge = 3,
            Recover = 4, Return = 5, ToBask = 6, Bask = 7, Feed = 8;

        const float SwimModelY = -0.28f;   // back and eyes just above the water
        const float StandModelY = 0.44f;   // feet on the ground (mesh min y -0.445)
        const float Stride = 1.7f;         // metres per full gait cycle

        Component _animal;
        GameObject _model;
        Renderer _renderer;
        Mesh _animatedMesh;
        Vector3[] _baseVertices;
        Vector3[] _workingVertices;
        byte[] _part;
        Vector3[] _pivot;
        float[] _headWeight;
        float[] _refZ;
        readonly float[] _lateral = new float[200];
        readonly Quaternion[] _legs = new Quaternion[6];
        static readonly float[] SlideAngles = new float[] { 30f, -30f, 60f, -60f, 90f, -90f };
        SkinnedMeshRenderer[] _hidden;
        NavMeshAgent[] _agents;
        Rigidbody _body;
        ParticleSystem _fumes;
        Light _glow;
        GameObject _hitRoot;
        readonly List<BoxCollider> _hitBoxes = new List<BoxCollider>();
        bool _ready;
        bool _dead;
        bool _detaching;
        float _nextExposure;
        float _deadAt;
        int _normalFrame;
        int _view;
        float _scale = 1f;  // BodyScale: the mesh, its hit boxes and waterline

        // Shared pose: on the master this IS the simulation, elsewhere it is
        // the smoothed copy of the last broadcast.
        Vector3 _pos;
        float _yaw;
        float _speed;
        int _mode;
        float _modeSince;
        int _targetActor = -1;
        bool _poseValid;

        // Master brain.
        bool _brainOwner;
        float _nextThink;
        float _modeUntil;
        float _wantSpeed;
        float _turnRate;
        Vector3 _goal;
        float _goalYaw;
        bool _useGoalYaw;
        GameObject _target;
        float _calmUntil;
        float _outSince = -1f;
        float _chargeTime;
        float _nextLunge;
        int _jinks;
        float _jinkReady;
        Vector3 _tPrev;
        float _tPrevAt = -1f;
        float _tSpeed;
        readonly float[] _tYaw = new float[8];
        readonly bool[] _tMoving = new bool[8];
        int _tHead;
        float _nextBask;
        Vector3 _baskSpot;
        readonly System.Random _random = new System.Random();
        float _nextSend;

        // Remote copy.
        Vector3 _netPos;
        float _netYaw;
        float _netSpeed;
        float _netAt = -99f;

        // Animation.
        float _landW;
        float _gait;
        float _jaw;
        float _headYaw;
        float _lastYaw;
        float _yawRate;
        float _snapAt = -99f;
        float _roll;
        float _visualSpeed;
        Vector3 _lastPos;

        // The local player's own track, for the straight-run judgement.
        readonly Vector3[] _track = new Vector3[16];
        readonly float[] _trackAt = new float[16];
        int _trackHead;
        int _trackCount;
        float _nextTrack;
        float _nextBite;
        float _huntNoticeAt = -99f;

        internal void Setup(Component animal)
        {
            _animal = animal;
            if (_ready) return;
            _ready = true;
            _view = Crocodile.ViewId(gameObject);
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
            // LateUpdate overwrites transform position/rotation every frame.

            _body = GetComponent<Rigidbody>();
            if (_body != null)
            {
                _body.useGravity = false;
                _body.isKinematic = true;
            }

            _pos = transform.position;
            _yaw = transform.eulerAngles.y;
            _lastYaw = _yaw;
            _lastPos = _pos;
            _modeSince = Time.time;
            _nextBask = Time.time + 90f + (float)_random.NextDouble() * 60f;
            BuildModel();
        }

        // ---------------------------------------------------------- the model

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
            _scale = Crocodile.BodyScale();
            _model.transform.localPosition = new Vector3(0f, SwimModelY * _scale, 0f);
            _model.transform.localScale = Vector3.one * _scale;
            _model.layer = gameObject.layer;

            _animatedMesh = UnityEngine.Object.Instantiate(source) as Mesh;
            _animatedMesh.name = "crocodile_animated";
            _animatedMesh.MarkDynamic();
            Bounds bounds = _animatedMesh.bounds;
            bounds.Expand(new Vector3(2.2f, 1.2f, 0.8f));
            _animatedMesh.bounds = bounds;
            _baseVertices = _animatedMesh.vertices;
            _workingVertices = new Vector3[_baseVertices.Length];
            LoadRig();

            MeshFilter filter = _model.AddComponent<MeshFilter>();
            filter.sharedMesh = _animatedMesh;
            MeshRenderer renderer = _model.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = BuildMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            _renderer = renderer;

            BuildHitBoxes();
            BuildFumes();
        }

        /// <summary>
        /// crocodile_rig.bin from the Blender build: "NDRG", version, vertex
        /// count, part count, one pivot per part (Unity axes), then one part
        /// byte per vertex in ndmesh order. 0 body, 1 jaw, 2/3 fore limb L/R,
        /// 4/5 hind limb L/R. Without it everything is body and only the
        /// spine, tail and head move.
        /// </summary>
        void LoadRig()
        {
            int n = _baseVertices.Length;
            _part = new byte[n];
            _pivot = new Vector3[6];
            _headWeight = new float[n];
            _refZ = new float[n];
            string path = System.IO.Path.Combine(RevivalPlugin.AssetDir, Crocodile.RigName());
            try
            {
                if (File.Exists(path))
                {
                    byte[] raw = File.ReadAllBytes(path);
                    if (raw.Length >= 16 && raw[0] == (byte)'N' && raw[1] == (byte)'D'
                        && raw[2] == (byte)'R' && raw[3] == (byte)'G')
                    {
                        int count = BitConverter.ToInt32(raw, 8);
                        int parts = BitConverter.ToInt32(raw, 12);
                        int offset = 16;
                        if (count == n && parts >= 6 && raw.Length >= offset + parts * 12 + count)
                        {
                            for (int p = 0; p < parts; p++)
                            {
                                Vector3 pivot = new Vector3(BitConverter.ToSingle(raw, offset),
                                                            BitConverter.ToSingle(raw, offset + 4),
                                                            BitConverter.ToSingle(raw, offset + 8));
                                if (p < _pivot.Length) _pivot[p] = pivot;
                                offset += 12;
                            }
                            Buffer.BlockCopy(raw, offset, _part, 0, count);
                        }
                        else
                            RevivalPlugin.L.LogWarning("Crocodile rig: " + count + " vertices "
                                + "for a " + n + "-vertex mesh; jaw and legs stay still.");
                    }
                }
                else
                    RevivalPlugin.L.LogWarning("Crocodile rig: " + path + " missing; "
                        + "jaw and legs stay still.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crocodile rig: " + ex.Message);
            }

            int[] counts = new int[6];
            for (int i = 0; i < n; i++)
            {
                int part = _part[i] > 5 ? 0 : _part[i];
                _part[i] = (byte)part;
                counts[part]++;
                float z = _baseVertices[i].z;
                // The head turns from the neck (z 1.35) and is rigid past 1.95.
                _headWeight[i] = part == 1 ? 1f : Smooth(1.35f, 1.95f, z);
                if (part >= 2) _headWeight[i] = 0f;
                // A leg follows the spine where it joins the body.
                _refZ[i] = part >= 2 ? _pivot[part].z : z;
            }
            RevivalPlugin.L.LogInfo("Crocodile rig: body " + counts[0] + ", jaw " + counts[1]
                + ", legs " + counts[2] + "/" + counts[3] + "/" + counts[4] + "/" + counts[5]
                + " vertices.");
        }

        static float Smooth(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
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

        /// <summary>
        /// The firearm path (PlayerFirearmWeaponController.FireOneShot IL_058B)
        /// accepts a hit when the collider is TAGGED "Animal" and finds the
        /// Animal_AI with GetComponentInParent; only then does it send
        /// NetworkApplyDamage and raise the boss bar (IL_06F0). So the boxes
        /// live on a child of the model - they follow its waterline, pitch and
        /// roll - and carry the native animal collider's tag and layer.
        /// </summary>
        void BuildHitBoxes()
        {
            _hitRoot = new GameObject("NDR_Crocodile_Hit");
            _hitRoot.transform.SetParent(_model.transform, false);
            int layer = gameObject.layer;
            string tag = "Animal";
            try
            {
                FieldInfo field = _animal == null ? null
                    : AccessTools.Field(_animal.GetType(), "AnimalCollider");
                Collider native = field == null ? null : field.GetValue(_animal) as Collider;
                if (native != null)
                {
                    layer = native.gameObject.layer;
                    if (!string.IsNullOrEmpty(native.tag) && native.tag != "Untagged")
                        tag = native.tag;
                }
            }
            catch { }
            _hitRoot.layer = layer;
            try { _hitRoot.tag = tag; }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Crocodile hit boxes: tag " + tag + ": " + ex.Message);
            }
            AddHitBox(new Vector3(0f, 0.15f, -0.35f), new Vector3(2.15f, 0.85f, 4.7f));
            AddHitBox(new Vector3(0f, 0.12f, 3.05f), new Vector3(1.55f, 0.72f, 1.65f));
            AddHitBox(new Vector3(0f, 0.08f, -3.30f), new Vector3(0.95f, 0.50f, 3.1f));
            RevivalPlugin.L.LogInfo("Crocodile hit boxes: tag " + _hitRoot.tag + ", layer "
                + LayerMask.LayerToName(layer) + " (" + layer + ").");
        }

        void AddHitBox(Vector3 centre, Vector3 size)
        {
            BoxCollider collider = _hitRoot.AddComponent<BoxCollider>();
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

        // ------------------------------------------------------------ helpers

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        static float Bearing(Vector3 from, Vector3 to)
        {
            return Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;
        }

        static Vector3 Heading(float yaw)
        {
            float r = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }

        static bool Hunting(int mode)
        {
            return mode == Stalk || mode == Charge || mode == Lunge || mode == Recover;
        }

        void SetMode(int mode, float seconds)
        {
            if (mode != _mode) _modeSince = Time.time;
            _mode = mode;
            _modeUntil = Time.time + seconds;
            _nextSend = 0f;
        }

        /// <summary>True while this crocodile hunts the local player or the
        /// local player stands close to it: the boss bar should be up.</summary>
        internal bool EngagesLocal()
        {
            if (_dead || !_ready) return false;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return false;
            if (Flat(player.transform.position - transform.position) < 35f) return true;
            return Hunting(_mode) && _targetActor >= 0 && _targetActor == Crocodile.LocalActor();
        }

        // ------------------------------------------------------------- frame

        void Update()
        {
            if (!_ready || _dead || !Crocodile.Primary(this)) return;
            float now = Time.time;
            _brainOwner = Crocodile.IsMaster() || !CrocodileNet.Hooked;
            if (_brainOwner)
            {
                if (now >= _nextThink)
                {
                    _nextThink = now + 0.1f;
                    Think(now);
                }
                Move(Time.deltaTime);
                Broadcast(now);
            }
            TrackLocal(now);
            JudgeBite(now);
            Toxin(now);
        }

        void Toxin(float now)
        {
            if (now < _nextExposure) return;
            const float step = 0.25f;
            _nextExposure = now + step;
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
            if (!Hunting(_mode)) Crocodile.Exposure(protection <= 0.02f);
        }

        // ------------------------------------------------------ master brain

        void Think(float now)
        {
            if (!_poseValid)
            {
                _poseValid = true;
                _pos = transform.position;
                _yaw = transform.eulerAngles.y;
                _mode = Return;
                _modeSince = now;
            }

            GameObject target = _target;
            if (target != null && (!Crocodile.Huntable(target) || now < _calmUntil))
                target = null;
            if (target != null)
            {
                Vector3 tp = target.transform.position;
                if (!CrocodileLake.HuntZone(tp))
                {
                    if (_outSince < 0f) _outSince = now;
                }
                else _outSince = -1f;
                if (Flat(tp - _pos) > Crocodile.HuntRadius() * 1.5f
                    || (_outSince >= 0f && now - _outSince > 2.5f)) target = null;
            }
            if (target == null && now >= _calmUntil && _mode != Feed) target = PickTarget();
            if (target != _target)
            {
                _target = target;
                _targetActor = target == null ? -1 : Crocodile.ActorOf(target);
                _tPrevAt = -1f;
                _tSpeed = 0f;
                _tHead = 0;
                for (int i = 0; i < _tMoving.Length; i++) _tMoving[i] = false;
                _jinks = 0;
                _chargeTime = 0f;
                _outSince = -1f;
                _nextSend = 0f;
                if (target != null)
                {
                    RevivalPlugin.L.LogInfo("Crocodile hunts actor " + _targetActor + " at "
                        + Flat(target.transform.position - _pos).ToString("0") + " m.");
                    if (target == MapTools.LocalPlayer()) HuntNotice(now);
                }
            }

            if (_mode == Feed)
            {
                _useGoalYaw = true;
                _goalYaw = _yaw;
                if (now >= _modeUntil) SetMode(Return, 60f);
                return;
            }

            if (_target != null) Hunt(now);
            else Calm(now);
        }

        GameObject PickTarget()
        {
            float best = Crocodile.HuntRadius();
            GameObject chosen = null;
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject player = players[i];
                if (!Crocodile.Huntable(player)) continue;
                Vector3 p = player.transform.position;
                float d = Flat(p - _pos);
                if (d >= best || !CrocodileLake.HuntZone(p)) continue;
                best = d;
                chosen = player;
            }
            return chosen;
        }

        void Hunt(float now)
        {
            Vector3 tp = _target.transform.position;
            float d = Flat(tp - _pos);
            float error = Mathf.DeltaAngle(_yaw, Bearing(_pos, tp));
            bool land = _landW > 0.5f;
            _goal = tp;
            _useGoalYaw = false;

            // The target's own heading history: a jink is a change of running
            // direction by more than 50 degrees within half a second.
            bool jink = false;
            if (_tPrevAt > 0f && now - _tPrevAt > 0.01f)
            {
                Vector3 v = (tp - _tPrev) / (now - _tPrevAt);
                v.y = 0f;
                _tSpeed = Mathf.Lerp(_tSpeed, v.magnitude, 0.5f);
                _tHead = (_tHead + 1) % _tYaw.Length;
                _tMoving[_tHead] = v.magnitude > 2.2f;
                _tYaw[_tHead] = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
                int back = (_tHead + _tYaw.Length - 5) % _tYaw.Length;
                jink = _tMoving[_tHead] && _tMoving[back]
                    && Mathf.Abs(Mathf.DeltaAngle(_tYaw[_tHead], _tYaw[back])) > 50f;
            }
            _tPrev = tp;
            _tPrevAt = now;

            if (_mode == Lunge)
            {
                if (now >= _modeUntil) SetMode(Recover, 0.7f);
            }
            else if (_mode == Recover)
            {
                if (now >= _modeUntil) SetMode(Charge, 30f);
            }
            else if (!Hunting(_mode)) SetMode(d > 22f ? Stalk : Charge, 30f);
            else if (_mode == Stalk && d <= 20f) SetMode(Charge, 30f);
            else if (_mode == Charge && d > 30f) SetMode(Stalk, 30f);

            if (_mode == Charge || _mode == Lunge)
            {
                if (land) _chargeTime += 0.1f;
                if (_chargeTime > 18f)
                {
                    GiveUp(now, 12f, "tired after a long charge on land");
                    return;
                }
                if (jink && d < 16f && now >= _jinkReady && _tSpeed > 2.5f)
                {
                    _jinks++;
                    _jinkReady = now + 0.8f;
                    SetMode(Recover, 1.1f);
                    CrocodileNet.Send(new float[] { CrocodileNet.Zigzag, _view, _targetActor, _jinks }, true);
                    ZigzagLocal(_targetActor, _jinks);
                    if (_jinks >= 3)
                    {
                        GiveUp(now, 15f, "shaken off by three zigzags");
                        return;
                    }
                }
                else if (_mode == Charge && d < 6.5f * Crocodile.BodyScale() && Mathf.Abs(error) < 20f
                         && now >= _nextLunge)
                {
                    _nextLunge = now + 2.4f;
                    SetMode(Lunge, 0.55f);
                }
            }
        }

        void GiveUp(float now, float calm, string why)
        {
            RevivalPlugin.L.LogInfo("Crocodile gives up on actor " + _targetActor + ": " + why + ".");
            _calmUntil = now + calm;
            _target = null;
            _targetActor = -1;
            SetMode(Return, 60f);
        }

        void Calm(float now)
        {
            if (Hunting(_mode)) SetMode(Return, 60f);
            if (_mode == Return)
            {
                Vector3 water;
                if (_landW < 0.3f && CrocodileLake.Kind(_pos.x, _pos.z) == CrocodileLake.Water)
                    SetMode(Patrol, 3600f);
                else if (CrocodileLake.NearestWater(_pos, out water)) _goal = water;
                else _goal = Crocodile.LoopGoal(_pos);
                _useGoalYaw = false;
                if (now >= _modeUntil) SetMode(Patrol, 3600f);
            }
            if (_mode == Patrol)
            {
                _goal = Crocodile.LoopGoal(_pos);
                _useGoalYaw = false;
                float minutes = Crocodile.BaskMinutes();
                if (minutes > 0f && now >= _nextBask)
                {
                    _nextBask = now + minutes * 60f * (0.7f + 0.6f * (float)_random.NextDouble());
                    if (CrocodileLake.BaskSpot(_pos, _random, out _baskSpot))
                    {
                        SetMode(ToBask, 70f);
                        RevivalPlugin.L.LogInfo("Crocodile crawls out to bask at "
                            + _baskSpot.ToString("0") + ".");
                    }
                }
            }
            else if (_mode == ToBask)
            {
                _goal = _baskSpot;
                _useGoalYaw = false;
                if (Flat(_baskSpot - _pos) < 2f)
                {
                    // Lie facing the water, the way out when something comes.
                    Vector3 water;
                    _goalYaw = CrocodileLake.NearestWater(_pos, out water)
                        ? Bearing(_pos, water) : _yaw;
                    SetMode(Bask, 40f + (float)_random.NextDouble() * 30f);
                }
                else if (now >= _modeUntil) SetMode(Return, 60f);
            }
            else if (_mode == Bask)
            {
                _useGoalYaw = true;
                if (now >= _modeUntil) SetMode(Return, 60f);
            }
        }

        /// <summary>Speed and turn rate per mode. On land the charge is always
        /// faster than the fleeing target in a straight line, but the turn rate
        /// is low and a big heading error costs speed: a zigzag wins.</summary>
        void Move(float dt)
        {
            if (!_poseValid || dt <= 0f) return;
            bool land = _landW > 0.5f;
            float charge = land
                ? Mathf.Clamp(Mathf.Max(Crocodile.LandSpeed(), _tSpeed * Crocodile.SprintEdge()), 4f, 14f)
                : Mathf.Clamp(Mathf.Max(7f, _tSpeed * Crocodile.SprintEdge()), 4f, 11f);
            switch (_mode)
            {
                case Stalk: _wantSpeed = land ? 3.5f : 5.2f; _turnRate = 100f; break;
                case Charge: _wantSpeed = charge; _turnRate = land ? 85f : 120f; break;
                case Lunge: _wantSpeed = charge * 1.35f; _turnRate = 25f; break;
                case Recover: _wantSpeed = 1.2f; _turnRate = 150f; break;
                case Return: _wantSpeed = land ? 3.2f : 4.5f; _turnRate = 90f; break;
                case ToBask: _wantSpeed = land ? 2.2f : 3.2f; _turnRate = 70f; break;
                case Bask: _wantSpeed = 0f; _turnRate = 40f; break;
                case Feed: _wantSpeed = 0f; _turnRate = 0f; break;
                default: _wantSpeed = Crocodile.Speed(); _turnRate = 60f; break;
            }

            float want = _useGoalYaw ? _goalYaw : Bearing(_pos, _goal);
            if (!_useGoalYaw && Flat(_goal - _pos) < 0.5f) want = _yaw;
            float error = Mathf.DeltaAngle(_yaw, want);
            float turn = _turnRate * dt;
            _yaw += Mathf.Clamp(error, -turn, turn);
            float cos = Mathf.Cos(error * Mathf.Deg2Rad);
            float target = _wantSpeed * Mathf.Clamp(cos, land ? 0.25f : 0.45f, 1f);
            float accel = target > _speed ? (land ? 10f : 6f) : 12f;
            _speed = Mathf.MoveTowards(_speed, target, accel * dt);
            if (_speed < 0.01f) return;

            Vector3 step = Heading(_yaw) * _speed * dt;
            Vector3 next = _pos + step;
            if (CrocodileLake.Allowed(next) || !CrocodileLake.Allowed(_pos))
            {
                _pos = next;
                return;
            }
            // Edge of the world: slide along it instead of pressing into it.
            for (int i = 0; i < SlideAngles.Length; i++)
            {
                float yaw = _yaw + (error >= 0f ? SlideAngles[i] : -SlideAngles[i]);
                Vector3 slide = _pos + Heading(yaw) * _speed * dt;
                if (!CrocodileLake.Allowed(slide)) continue;
                _pos = slide;
                _yaw = Mathf.MoveTowardsAngle(_yaw, yaw, _turnRate * 2f * dt);
                return;
            }
            _speed = 0f;
        }

        void Broadcast(float now)
        {
            if (!CrocodileNet.Hooked || now < _nextSend) return;
            bool near = false;
            List<GameObject> players = Crocodile.Players();
            for (int i = 0; i < players.Count && !near; i++)
                if (players[i] != null && Flat(players[i].transform.position - _pos) < 320f)
                    near = true;
            _nextSend = now + (near ? 0.1f : 0.5f);
            CrocodileNet.Send(new float[] {
                CrocodileNet.State, _view, _pos.x, _pos.y, _pos.z, _yaw,
                _mode, _speed, _targetActor, now - _modeSince }, false);
        }

        internal void Receive(float[] data, int sender)
        {
            int kind = Mathf.RoundToInt(data[0]);
            if (_view != 0 && Mathf.RoundToInt(data[1]) != _view) return;
            float now = Time.time;
            if (kind == CrocodileNet.State && data.Length >= 10)
            {
                if (Crocodile.IsMaster()) return;
                _netPos = new Vector3(data[2], data[3], data[4]);
                _netYaw = data[5];
                int mode = Mathf.RoundToInt(data[6]);
                if (mode != _mode || !_poseValid) _modeSince = now - data[9];
                _mode = mode;
                _netSpeed = data[7];
                _targetActor = Mathf.RoundToInt(data[8]);
                if (!_poseValid || Flat(_netPos - _pos) > 25f)
                {
                    _pos = _netPos;
                    _yaw = _netYaw;
                }
                _poseValid = true;
                _netAt = now;
                if (_targetActor >= 0 && Hunting(_mode) && _targetActor == Crocodile.LocalActor())
                    HuntNotice(now);
            }
            else if (kind == CrocodileNet.Bite && data.Length >= 4)
            {
                _snapAt = now;
                if (Crocodile.IsMaster()) Bitten(Mathf.RoundToInt(data[2]), data[3] > 0.5f);
            }
            else if (kind == CrocodileNet.Zigzag && data.Length >= 4)
            {
                ZigzagLocal(Mathf.RoundToInt(data[2]), Mathf.RoundToInt(data[3]));
            }
        }

        void HuntNotice(float now)
        {
            if (now - _huntNoticeAt < 20f) return;
            _huntNoticeAt = now;
            Crocodile.Notice(Loc.T("КРОКОДИЛ ОХОТИТСЯ НА ТЕБЯ - БЕГИ ЗИГЗАГОМ!",
                                   "THE CROCODILE IS HUNTING YOU - RUN IN ZIGZAGS!"), 4f);
        }

        void ZigzagLocal(int actor, int count)
        {
            if (actor < 0 || actor != Crocodile.LocalActor()) return;
            Crocodile.Notice(count >= 3
                ? Loc.T("ЗИГЗАГ! КРОКОДИЛ ОТСТАЛ", "ZIGZAG! THE CROCODILE GAVE UP")
                : Loc.T("ЗИГЗАГ! КРОКОДИЛ ПРОМАХНУЛСЯ", "ZIGZAG! THE CROCODILE OVERSHOT"), 2.5f);
        }

        /// <summary>Master side of a bite the victim's client reported: roll
        /// the prey after a kill, back off for a moment after a bite.</summary>
        void Bitten(int actor, bool eaten)
        {
            if (!_brainOwner) return;
            RevivalPlugin.L.LogInfo("Crocodile " + (eaten ? "ate" : "bit") + " actor " + actor + ".");
            if (eaten)
            {
                SetMode(Feed, 4.5f);
                _calmUntil = Time.time + 20f;
                _target = null;
                _targetActor = -1;
            }
            else if (Hunting(_mode)) SetMode(Recover, 0.9f);
        }

        // ------------------------------------------------ the victim's client

        void TrackLocal(float now)
        {
            if (now < _nextTrack) return;
            _nextTrack = now + 0.1f;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return;
            _trackHead = (_trackHead + 1) % _track.Length;
            _track[_trackHead] = player.transform.position;
            _trackAt[_trackHead] = now;
            if (_trackCount < _track.Length) _trackCount++;
        }

        /// <summary>Was the local player running straight away over the last
        /// 1.5 s? Fast (over 3.2 m/s), straight (net distance over 90 % of the
        /// path walked) and away from the crocodile.</summary>
        bool RunningStraightAway(Vector3 player)
        {
            if (_trackCount < 12) return false;
            int last = _trackHead;
            int first = (_trackHead + _track.Length - 11) % _track.Length;
            float seconds = _trackAt[last] - _trackAt[first];
            if (seconds < 0.8f) return false;
            float path = 0f;
            for (int i = 0; i < 11; i++)
            {
                int a = (first + i) % _track.Length;
                int b = (a + 1) % _track.Length;
                path += Flat(_track[b] - _track[a]);
            }
            Vector3 net = _track[last] - _track[first];
            net.y = 0f;
            if (path / seconds < 3.2f || net.magnitude < path * 0.9f) return false;
            Vector3 away = player - transform.position;
            away.y = 0f;
            return Vector3.Dot(net.normalized, away.normalized) > 0.3f;
        }

        void JudgeBite(float now)
        {
            if (now < _nextBite || _mode == Feed || _mode == Recover || !_poseValid) return;
            GameObject player = MapTools.LocalPlayer();
            if (player == null || !Crocodile.Huntable(player)) return;
            Vector3 p = player.transform.position;
            Vector3 root = transform.position;
            float s = Crocodile.BodyScale();
            float dy = p.y - root.y;
            if (dy < -1.6f || dy > 2.4f * s) return;
            Vector3 forward = Heading(transform.eulerAngles.y);
            Vector3 a = root + forward * (2.0f * s);
            Vector3 b = root + forward * ((_mode == Lunge ? 4.6f : 4.2f) * s);
            Vector3 ab = b - a;
            ab.y = 0f;
            Vector3 ap = p - a;
            ap.y = 0f;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / Mathf.Max(0.001f, ab.sqrMagnitude));
            Vector3 closest = a + ab * t;
            float reach = (_mode == Lunge ? 1.7f : 1.25f) * s;
            if (Flat(p - closest) > reach) return;

            bool eaten = (_mode == Charge || _mode == Lunge) && RunningStraightAway(p);
            _nextBite = now + 1.6f;
            _snapAt = now;
            int actor = Crocodile.LocalActor();
            if (eaten)
            {
                Crocodile.Notice(Loc.T("КРОКОДИЛ СОЖРАЛ ТЕБЯ. НАДО БЫЛО БЕЖАТЬ ЗИГЗАГОМ.",
                                       "THE CROCODILE ATE YOU. YOU SHOULD HAVE ZIGZAGGED."), 7f);
                Crocodile.HurtLocal(Crocodile.EatDamage(), root);
            }
            else
                Crocodile.HurtLocal(Crocodile.BiteDamage(), root);
            RevivalPlugin.L.LogInfo("Crocodile " + (eaten ? "ate" : "bit")
                + " the local player in mode " + _mode + ".");
            CrocodileNet.Send(new float[] { CrocodileNet.Bite, _view, actor, eaten ? 1f : 0f }, true);
            if (_brainOwner) Bitten(actor, eaten);
        }

        // ----------------------------------------------------------- display

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
            float dt = Time.deltaTime;
            float now = Time.time;

            if (!_brainOwner)
            {
                if (_poseValid && now - _netAt < 6f)
                {
                    // Extrapolate the last broadcast a little and ease into it.
                    Vector3 predicted = _netPos + Heading(_netYaw) * _netSpeed
                        * Mathf.Min(0.35f, now - _netAt);
                    float k = 1f - Mathf.Exp(-9f * dt);
                    _pos = Vector3.Lerp(_pos, predicted, k);
                    _yaw = Mathf.LerpAngle(_yaw, _netYaw, k);
                    _speed = _netSpeed;
                }
                else
                {
                    // No master voice (old plugin or none yet): the calm loop
                    // every client can compute on its own.
                    double clock = Crocodile.NetworkClock();
                    _pos = Crocodile.SwimPoint(clock);
                    Vector3 tangent = Crocodile.SwimTangent(clock);
                    if (tangent.sqrMagnitude > 0.001f)
                        _yaw = Mathf.LerpAngle(_yaw, Mathf.Atan2(tangent.x, tangent.z) * Mathf.Rad2Deg,
                                               Mathf.Clamp01(dt * 4f));
                    _speed = Crocodile.Speed();
                    if (_mode != Patrol) { _mode = Patrol; _modeSince = now; }
                    _poseValid = true;
                }
            }

            float land;
            float support = Crocodile.Support(_pos.x, _pos.z, out land);
            _landW = Mathf.MoveTowards(_landW, land, dt * 2.5f);
            _pos.y = support;
            float bob = Mathf.Sin(now * 1.65f) * 0.055f * (1f - _landW);
            transform.position = new Vector3(_pos.x, support + bob, _pos.z);
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }

            float moved = dt > 0f ? Flat(transform.position - _lastPos) / dt : 0f;
            _lastPos = transform.position;
            _visualSpeed = Mathf.Lerp(_visualSpeed, Mathf.Min(moved, 16f), Mathf.Clamp01(dt * 6f));
            float yawRate = dt > 0f ? Mathf.DeltaAngle(_lastYaw, _yaw) / dt : 0f;
            _lastYaw = _yaw;
            _yawRate = Mathf.Lerp(_yawRate, yawRate, Mathf.Clamp01(dt * 5f));

            Pose(now, dt);
        }

        /// <summary>Model placement and the rig: every client, every frame the
        /// crocodile can be seen.</summary>
        void Pose(float now, float dt)
        {
            if (_model == null) return;
            float age = now - _modeSince;
            float swimW = 1f - _landW;

            // Pitch with the slope under it on land.
            float pitch = 0f;
            if (_landW > 0.01f)
            {
                Vector3 forward = Heading(_yaw);
                float front, back;
                if (Crocodile.Ground(_pos.x + forward.x * 2.5f, _pos.z + forward.z * 2.5f, out front)
                    && Crocodile.Ground(_pos.x - forward.x * 2.5f, _pos.z - forward.z * 2.5f, out back))
                    pitch = Mathf.Clamp(-Mathf.Atan2(front - back, 5f) * Mathf.Rad2Deg, -25f, 25f) * _landW;
            }

            // Death roll: in the water it spins its prey, on land it shakes it.
            float rollTarget = 0f;
            float shake = 0f;
            if (_mode == Feed)
            {
                if (swimW > 0.5f && age < 3f) _roll += dt * 560f;
                else shake = Mathf.Sin(now * 34f) * 22f * Mathf.Clamp01(4.5f - age);
            }
            else _roll = Mathf.MoveTowardsAngle(_roll, rollTarget, dt * 400f);

            float swimY = SwimModelY + (_mode == Lunge ? 0.18f : 0f);
            float modelY = Mathf.Lerp(swimY, StandModelY, _landW) * _scale;
            _model.transform.localPosition = new Vector3(0f,
                modelY + Mathf.Sin(now * 1.65f) * 0.035f * swimW, 0f);
            _model.transform.localRotation = Quaternion.Euler(
                pitch + Mathf.Sin(now * 1.15f) * 1.8f * swimW, 0f,
                _roll + Mathf.Sin(now * 1.70f) * 1.1f * swimW - _yawRate * 0.03f * _landW);

            // Jaw: gape when basking, open on the charge, wide in the lunge,
            // snap shut on a bite; a lazy breath otherwise.
            float jawTarget = Mathf.Max(0f, Mathf.Sin(now * 0.35f) - 0.8f) * 25f;
            if (_mode == Bask) jawTarget = 30f + Mathf.Sin(now * 0.4f) * 4f;
            else if (_mode == Charge) jawTarget = 12f;
            else if (_mode == Lunge) jawTarget = 40f;
            else if (_mode == Feed) jawTarget = 4f + Mathf.Abs(Mathf.Sin(now * 9f)) * 8f;
            float snap = now - _snapAt;
            if (snap < 0.1f) jawTarget = 42f;
            else if (snap < 0.4f) jawTarget = 0f;
            _jaw = Mathf.MoveTowards(_jaw, jawTarget, dt * (snap < 0.4f ? 900f : 160f));

            float headPitch = _mode == Bask ? -9f : _mode == Lunge ? -7f : 0f;
            _headYaw = Mathf.Lerp(_headYaw, Mathf.Clamp(_yawRate * 0.3f, -28f, 28f) + shake,
                                  Mathf.Clamp01(dt * 6f));

            _gait += dt * _visualSpeed / (Stride * _scale);
            if (_gait > 1000f) _gait -= 1000f;

            if (_renderer != null && !_renderer.isVisible) return;
            Camera camera = Camera.main;
            float distance = camera == null ? 0f
                : Vector3.Distance(camera.transform.position, transform.position);
            if (distance > 220f) return;
            Deform(now, swimW, headPitch, distance);
        }

        void Deform(float now, float swimW, float headPitch, float distance)
        {
            if (_animatedMesh == null || _baseVertices == null) return;
            float walk = Mathf.Clamp01(_visualSpeed / 1.5f) * _landW;
            float speed01 = Mathf.Clamp01(_visualSpeed / 7f);
            float cycle = _gait * Mathf.PI * 2f;

            // Lateral spine offset per 5 cm of body length (z -5.2 .. 4.75).
            float tailAmp = Mathf.Lerp(0.18f, 0.55f, speed01) * swimW + 0.16f * walk;
            float tailFreq = 2.2f + _visualSpeed * 0.45f;
            for (int i = 0; i < _lateral.Length; i++)
            {
                float z = -5.2f + i * 0.05f;
                float offset = 0f;
                if (z < -1.1f)
                {
                    float w = Mathf.Clamp01((-z - 1.1f) / 3.9f);
                    float phase = swimW > 0.5f ? now * tailFreq - z * 1.18f : cycle - z * 0.9f;
                    offset += Mathf.Sin(phase) * tailAmp * w * w;
                }
                float trunk = Smooth(-3.2f, -1.6f, z) * (1f - Smooth(1.0f, 1.8f, z));
                offset += Mathf.Sin(cycle - z * 0.5f) * 0.09f * walk * trunk;
                _lateral[i] = offset;
            }

            Quaternion jaw = Quaternion.Euler(_jaw, 0f, 0f);
            Quaternion head = Quaternion.Euler(headPitch, _headYaw, 0f);
            Vector3 headPivot = new Vector3(0f, 0.12f, 1.35f);
            Quaternion[] legs = _legs;
            for (int k = 2; k < 6; k++)
            {
                float side = (k % 2 == 0) ? -1f : 1f;
                bool fore = k < 4;
                // Diagonal pairs: left fore with right hind, and the reverse.
                float offset = (k == 2 || k == 5) ? 0f : 0.5f;
                float phase = cycle + offset * Mathf.PI * 2f;
                float sweep = Mathf.Sin(phase);
                float lift = Mathf.Max(0f, Mathf.Cos(phase));
                float walkYaw = -side * sweep * 30f * walk;
                float walkRoll = side * lift * 16f * walk;
                float tuckYaw = side * (fore ? 58f : 72f);
                float tuckRoll = side * 18f;
                // Standing still on land: spread and planted.
                legs[k] = Quaternion.Euler(0f, Mathf.Lerp(walkYaw, tuckYaw, swimW),
                                           Mathf.Lerp(walkRoll, tuckRoll, swimW));
            }
            Vector3 hinge = _pivot[1];

            int n = _baseVertices.Length;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = _baseVertices[i];
                int part = _part[i];
                if (part == 1) p = hinge + jaw * (p - hinge);
                else if (part >= 2) p = _pivot[part] + legs[part] * (p - _pivot[part]);
                float w = _headWeight[i];
                if (w > 0f)
                {
                    Vector3 turned = headPivot + head * (p - headPivot);
                    p = w >= 1f ? turned : Vector3.Lerp(p, turned, w);
                }
                float at = (_refZ[i] + 5.2f) * 20f;
                int lo = Mathf.Clamp((int)at, 0, _lateral.Length - 2);
                float f = Mathf.Clamp01(at - lo);
                p.x += _lateral[lo] + (_lateral[lo + 1] - _lateral[lo]) * f;
                _workingVertices[i] = p;
            }
            _animatedMesh.vertices = _workingVertices;
            _normalFrame++;
            if ((_normalFrame % (distance < 60f ? 2 : 4)) == 0) _animatedMesh.RecalculateNormals();
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
            Crocodile.Forget(_animal);
            Crocodile.Gone(this, true);
        }

        void Sink()
        {
            if (_model == null) return;
            float age = Mathf.Max(0f, Time.time - _deadAt);
            Vector3 local = _model.transform.localPosition;
            if (_landW > 0.5f)
            {
                // On the bank it rolls onto its side and stays there.
                _model.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Min(70f, age * 60f));
                return;
            }
            local.y = (SwimModelY - Mathf.Min(0.65f, age * 0.045f)) * _scale;
            _model.transform.localPosition = local;
            _model.transform.localRotation = Quaternion.Euler(
                Mathf.Min(18f, age * 1.3f), 0f, Mathf.Min(160f, age * 30f));
        }

        internal void Detach()
        {
            _detaching = true;
            RestoreNative();
            RemoveHitBoxes();
            Crocodile.Forget(_animal);
            if (_model != null) UnityEngine.Object.Destroy(_model);
            UnityEngine.Object.Destroy(this);
        }

        void RemoveHitBoxes()
        {
            for (int i = 0; i < _hitBoxes.Count; i++)
                if (_hitBoxes[i] != null) UnityEngine.Object.Destroy(_hitBoxes[i]);
            _hitBoxes.Clear();
            if (_hitRoot != null) UnityEngine.Object.Destroy(_hitRoot);
            _hitRoot = null;
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
            Crocodile.Forget(_animal);
            if (_animatedMesh != null) UnityEngine.Object.Destroy(_animatedMesh);
            Crocodile.Gone(this, _dead);
        }
    }
}

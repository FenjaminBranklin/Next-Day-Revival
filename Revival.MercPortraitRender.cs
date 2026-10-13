// Inactive native appearance copy; gameplay scripts never become active.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class MercPortraitRender
    {
        const int Layer = 31;
        static GameObject Copy(GameObject prefab, Transform parent)
        {
            if (prefab == null) throw new InvalidOperationException("Portrait prefab missing");
            // Parenting at instantiation keeps Awake/OnEnable from running.
            return UnityEngine.Object.Instantiate(prefab, parent, false);
        }

        static object Invoke(object target, string name, Type[] types, object[] args)
        {
            Type type = target as Type;
            MethodInfo method = AccessTools.Method(type == null ? target.GetType() : type, name, types, null);
            if (method == null) throw new MissingMethodException(name);
            return method.Invoke(method.IsStatic ? null : target, args);
        }

        static Transform Link(GameObject model, string name)
        {
            Component[] components = model.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                FieldInfo field = AccessTools.Field(components[i].GetType(), name);
                if (field == null) continue;
                object value = field.GetValue(components[i]);
                Transform t = value as Transform;
                GameObject go = value as GameObject;
                if (t != null) return t;
                if (go != null) return go.transform;
            }
            throw new MissingFieldException("Portrait visual link " + name);
        }

        static void Show(Transform root, int id)
        {
            Invoke(RevivalPlugin.TypeByName("NPC_AI2"), "ShowCharacterMesh",
                new Type[] { typeof(Transform), typeof(bool), typeof(int), typeof(int), typeof(bool) },
                new object[] { root, true, id, 0, false });
        }

        static void LocalTransform(GameObject copy)
        {
            Type type = RevivalPlugin.TypeByName("ItemTransformManager");
            Component manager = type == null ? null : copy.GetComponent(type);
            if (manager == null) throw new MissingComponentException("Portrait ItemTransformManager");
            Invoke(manager, "ApplyLocalTransformData", Type.EmptyTypes, new object[0]);
        }

        static void Head(GameObject model, Component ai, int id, int slot)
        {
            if (id <= 0) return;
            bool skinned = (bool)Invoke(RevivalPlugin.TypeByName("PlayerInventoryManager"),
                "isSkinnedHeadObject", new Type[] { typeof(int) }, new object[] { id });
            if (skinned)
            {
                // SetHead's skinned branch changes meshes only (confirmed native IL).
                Invoke(ai, "SetHead", new Type[] { typeof(int), typeof(int), typeof(bool) },
                    new object[] { slot, id, true });
            }
            else
            {
                GameObject prefab = Resources.Load("PlayerDataPrefabs/HeadGears/" + id + "_HeadGear") as GameObject;
                GameObject head = Copy(prefab, Link(model, slot == 0 ? "HeadGears_Helper" : "HeadMasks_Helper"));
                LocalTransform(head);
            }
        }

        static string Dress(GameObject model, int[] items)
        {
            Type npc = RevivalPlugin.TypeByName("NPC_AI2");
            Component ai = npc == null ? null : model.GetComponent(npc);
            if (ai == null) throw new MissingComponentException("Portrait NPC_AI2");
            // Native lazy getters use GetComponentInChildren() without includeInactive.
            // Bind serialized visual links while inactive, without running native Awake.
            string[] caches = new string[] { "_heads", "_headsGears", "_headGearsHelper", "_headMasksHelper", "_bodys", "_hands", "_legs" };
            string[] links = new string[] { "Heads", "Heads_Gears", "HeadGears_Helper", "HeadMasks_Helper", "Bodys", "Hands", "Legs" };
            for (int i = 0; i < caches.Length; i++)
            {
                FieldInfo field = AccessTools.Field(npc, caches[i]);
                if (field == null) throw new MissingFieldException("Portrait NPC visual cache " + caches[i]);
                field.SetValue(ai, Link(model, links[i]));
            }
            Type[] meshTypes = new Type[] { typeof(bool), typeof(int), typeof(bool) };
            Invoke(ai, "HeadMeshesActiveManager", meshTypes, new object[] { true, 100, false });
            string[] methods = new string[] { "BodyMeshesActiveManager", "HandsMeshesActiveManager", "LegsMeshesActiveManager" };
            for (int i = 0; i < methods.Length; i++)
                Invoke(ai, methods[i], meshTypes, new object[] { true, items[i + 3], false });
            // Gear suits use the same existing native material/mesh fix as live mercs.
            Crew.GearCustomizationPostfix(ai, new int[] { 100, items[3] });
            HideChildren(Link(model, "Heads_Gears"));
            HideChildren(Link(model, "HeadGears_Helper"));
            HideChildren(Link(model, "HeadMasks_Helper"));
            HideChildren(Link(model, "Backpacks"));
            Head(model, ai, items[2], 1);
            bool combine = items[2] <= 0 || (bool)Invoke(RevivalPlugin.TypeByName("PlayerInventoryManager"),
                "isCanCombineHeadGears", new Type[] { typeof(int), typeof(int), typeof(int) },
                new object[] { 1, items[2], items[1] });
            if (combine) Head(model, ai, items[1], 0);
            if (items[6] > 0) Show(Link(model, "Backpacks"), items[6]);

            Type managerType = RevivalPlugin.TypeByName("NPC_WeaponsManager");
            Component manager = managerType == null ? null : model.GetComponent(managerType);
            if (manager == null) throw new MissingComponentException("Portrait NPC_WeaponsManager");
            Transform hand = Link(model, "Weapons_HelperR");
            HideChildren(hand);
            GameObject weapon = Copy(Resources.Load("PlayerDataPrefabs/Weapons/" + items[0] + "_Weapon") as GameObject, hand);
            LocalTransform(weapon);
            weapon.SetActive(true); // Still under the inactive capture root.
            return (string)Invoke(manager, "GetWeaponTypeById", new Type[] { typeof(int) }, new object[] { items[0] });
        }

        static void HideChildren(Transform root)
        {
            for (int i = 0; i < root.childCount; i++) root.GetChild(i).gameObject.SetActive(false);
        }

        static void Strip(GameObject model)
        {
            // Remove scripts first, while the complete hierarchy is inactive.
            MonoBehaviour[] scripts = model.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < scripts.Length; i++)
                if (scripts[i] != null) UnityEngine.Object.DestroyImmediate(scripts[i]);
            Joint[] joints = model.GetComponentsInChildren<Joint>(true);
            for (int i = 0; i < joints.Length; i++) UnityEngine.Object.DestroyImmediate(joints[i]);
            Component[] components = model.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c == null || c is Transform || c is MeshRenderer || c is SkinnedMeshRenderer || c is MeshFilter || c is Animation) continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
            Transform[] nodes = model.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < nodes.Length; i++) nodes[i].gameObject.layer = Layer;
        }

        static void Pose(GameObject model, string weaponType)
        {
            Animation animation = model.GetComponentInChildren<Animation>();
            if (animation == null) throw new MissingComponentException("Portrait native Animation");
            string[] names = new string[] { "npc_" + weaponType + "_idle_alert_01", weaponType + "_idle_alert",
                weaponType + "_idle_aiming" };
            AnimationState chosen = null;
            for (int i = 0; i < names.Length && chosen == null; i++) chosen = animation[names[i]];
            if (chosen == null) throw new InvalidOperationException("Portrait held-weapon pose missing: " + weaponType);
            animation.enabled = false;
            chosen.clip.SampleAnimation(animation.gameObject, Mathf.Min(0.35f, chosen.length * 0.5f));
        }

        internal static Texture2D Capture(int[] items)
        {
            MercPortraitKey.Of(items); // Validate before using resource IDs.
            GameObject stage = new GameObject("NDR merc portrait stage");
            stage.SetActive(false);
            stage.transform.position = new Vector3(0f, -10000f, 0f);
            RenderTexture target = null;
            Texture2D image = null;
            RenderTexture previous = RenderTexture.active;
            Color ambient = RenderSettings.ambientLight;
            AmbientMode mode = RenderSettings.ambientMode;
            bool fog = RenderSettings.fog;
            try
            {
                GameObject model = Copy(Resources.Load("NPCSpawn/Marauder_NPC_01") as GameObject, stage.transform);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                string weaponType = Dress(model, items);
                Strip(model);
                model.SetActive(true);
                // There are no AI, Photon, collider, audio or particle components now.
                stage.SetActive(true);
                Pose(model, weaponType);
                Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
                Bounds bounds = new Bounds(model.transform.position, Vector3.zero);
                int count = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!renderers[i].enabled) continue;
                    SkinnedMeshRenderer skin = renderers[i] as SkinnedMeshRenderer;
                    if (skin != null) skin.updateWhenOffscreen = true;
                    if (count++ == 0) bounds = renderers[i].bounds;
                    else bounds.Encapsulate(renderers[i].bounds);
                }
                if (count == 0 || bounds.size.y < 0.1f) throw new InvalidOperationException("Portrait has no visible model");
                GameObject cameraObject = new GameObject("Portrait camera");
                cameraObject.transform.parent = stage.transform;
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.12f, 0.13f, 0.14f, 1f);
                camera.cullingMask = 1 << Layer;
                camera.orthographic = true;
                camera.orthographicSize = bounds.extents.magnitude * 1.08f;
                camera.nearClipPlane = 0.01f; camera.farClipPlane = camera.orthographicSize * 10f;
                camera.transform.position = bounds.center + new Vector3(0.3f, 0.03f, 1f).normalized * camera.orthographicSize * 4f;
                camera.transform.LookAt(bounds.center);
                GameObject lamp = new GameObject("Portrait light");
                lamp.transform.parent = stage.transform;
                Light light = lamp.AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.1f;
                light.cullingMask = 1 << Layer; light.shadows = LightShadows.None;
                light.transform.rotation = Quaternion.Euler(25f, 155f, 0f);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.65f, 0.65f, 0.65f);
                RenderSettings.fog = false;
                target = new RenderTexture(MercPortraitKey.Size, MercPortraitKey.Size, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image = new Texture2D(MercPortraitKey.Size, MercPortraitKey.Size, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0f, 0f, MercPortraitKey.Size, MercPortraitKey.Size), 0, 0);
                image.Apply(false, false);
                return image;
            }
            catch
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderSettings.ambientLight = ambient; RenderSettings.ambientMode = mode; RenderSettings.fog = fog;
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                UnityEngine.Object.DestroyImmediate(stage);
            }
        }
    }
}

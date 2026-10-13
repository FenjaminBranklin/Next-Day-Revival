// Seal the native container transaction entry points while a hold is in flight.
// This prevents an already-open trunk window from racing a departure snapshot.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirBridgeCargo
    {
        static FieldInfo _uiController, _container;
        internal static bool Installed;
        internal static void Install(Harmony harmony)
        {
            Type ui = RevivalPlugin.TypeByName("PlayerInventoryUISystem");
            Type controller = RevivalPlugin.TypeByName("UIController");
            if (ui == null || controller == null) return;
            _uiController = AccessTools.Field(ui, "_uiController");
            _container = AccessTools.Field(controller, "_interactContainer");
            if (_uiController == null || _container == null) return;
            int patched = Patch(harmony, ui) + Patch(harmony, controller);
            Installed = patched > 0;
            RevivalPlugin.L.LogInfo("AirBridge: native cargo transaction seal installed on " + patched + " entry points.");
        }
        static int Patch(Harmony harmony, Type type)
        {
            int n = 0;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods)
            {
                // Gate the whole UI operation, before either the player bag or
                // the container changes. Data/RPC restore methods are outside
                // these UI types and keep working while cargo is rehydrated.
                if (method.ReturnType != typeof(void) || method.IsAbstract || method.IsGenericMethod
                    || method.GetMethodBody() == null || method.IsSpecialName) continue;
                if (method.Name == "Awake" || method.Name == "Start" || method.Name == "OnEnable"
                    || method.Name == "OnDisable" || method.Name == "OnDestroy" || method.Name == "Update"
                    || method.Name == "LateUpdate" || method.Name == "FixedUpdate"
                    || method.Name.StartsWith("Update", StringComparison.Ordinal)
                    || method.Name.StartsWith("Show", StringComparison.Ordinal)
                    || method.Name.StartsWith("Hide", StringComparison.Ordinal)
                    || method.Name.StartsWith("Open", StringComparison.Ordinal)
                    || method.Name.IndexOf("Close", StringComparison.Ordinal) >= 0
                    || method.Name.IndexOf("Cancel", StringComparison.Ordinal) >= 0
                    || method.Name.IndexOf("Escape", StringComparison.Ordinal) >= 0
                    || method.Name.StartsWith("Save", StringComparison.Ordinal)) continue;
                harmony.Patch(method, new HarmonyMethod(typeof(AirBridgeCargo).GetMethod("Prefix")), null, null, null, null);
                n++;
            }
            return n;
        }
        public static bool Prefix(object __instance)
        {
            if (!AirBridge.On || __instance == null || _container == null) return true;
            object controller = _container.DeclaringType.IsInstanceOfType(__instance) ? __instance
                : _uiController.DeclaringType.IsInstanceOfType(__instance) ? _uiController.GetValue(__instance) : null;
            Component container = controller == null ? null : _container.GetValue(controller) as Component;
            if (container == null) return true;
            GameObject go = container.gameObject;
            if (!PlayerHeli.Contains(go) && !PlayerAn2.Contains(go)) return true;
            bool closed = AirRegistry.CargoLocked(go) || (PlayerHeli.Contains(go) ? PlayerHeli.InAir(go) : PlayerAn2.InAir(go));
            return !closed;
        }
    }
}

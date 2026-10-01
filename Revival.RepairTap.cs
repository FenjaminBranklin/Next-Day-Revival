// Q1: one local repair session; existing feature/native code owns completion and RPCs.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RepairTap
    {
        delegate bool Button(int id);
        static Button _button;
        static Button _held;
        static string _owner;
        static GameObject _player;
        static object _life;
        static FieldInfo _health, _iteratorState, _iteratorManager;
        static Component _native;
        static MethodInfo _cancel;
        static Vector3 _start;
        static float _lastHealth;
        static int _startedFrame, _blockedFrame = -1;
        static KeyCode _key;

        internal static bool CanStart { get { return _owner == null && _blockedFrame != Time.frameCount; } }
        internal static bool IsRepair(string owner)
        { return owner == "aa-repair" || owner == "convoy-repair" || owner == "an2-repair"; }

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type input = RevivalPlugin.TypeByName("MyInputManager");
                MethodInfo button = AccessTools.Method(input, "ButtonDown", null, null);
                _button = (Button)Delegate.CreateDelegate(typeof(Button), button);
                _held = (Button)Delegate.CreateDelegate(typeof(Button),
                    AccessTools.Method(input, "Button", null, null));
                Type manager = RevivalPlugin.TypeByName("PlayerInteractingManager");
                _cancel = AccessTools.Method(manager, "CancelGlobalInteracting", null, null);
                harmony.Patch(AccessTools.Method(manager, "SearchGameplayItems", null, null), null, null,
                    new HarmonyMethod(typeof(RepairTap).GetMethod("RepairInput")), null, null);
                Type iterator = manager.GetNestedType("<PlayerVehicleInteract>c__Iterator4",
                    BindingFlags.Public | BindingFlags.NonPublic);
                _iteratorState = AccessTools.Field(iterator, "state");
                _iteratorManager = AccessTools.Field(iterator, "$this");
                harmony.Patch(AccessTools.Method(iterator, "MoveNext", null, null),
                    new HarmonyMethod(typeof(RepairTap).GetMethod("NativeBefore")),
                    new HarmonyMethod(typeof(RepairTap).GetMethod("NativeAfter")), null, null, null);
                harmony.Patch(_cancel, new HarmonyMethod(typeof(RepairTap).GetMethod("NativeCancelled")),
                    null, null, null, null);
                harmony.Patch(AccessTools.Method(RevivalPlugin.TypeByName("PlayerLifeDataManager"),
                    "PlayerApplyDamage", null, null),
                    new HarmonyMethod(typeof(RepairTap).GetMethod("DamageBefore")),
                    new HarmonyMethod(typeof(RepairTap).GetMethod("DamageAfter")), null, null, null);
                RevivalPlugin.L.LogInfo("Repair tap: R alias and native repair cancellation installed.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Repair tap install: " + ex); }
        }

        // Only the ButtonDown immediately preceding the state-20 vehicle repair branch.
        // Refuse an unfamiliar method layout instead of changing unrelated interactions.
        public static IEnumerable<CodeInstruction> RepairInput(IEnumerable<CodeInstruction> input)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            int patch = -1;
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo method = code[i].operand as MethodInfo;
                if (method == null || method.Name != "PlayerVehicleInteract") continue;
                // Arguments are manager, state, slot and vehicle; a wider search
                // could accidentally classify the following fuel call as repair.
                bool repair = i >= 3 && code[i - 3].opcode == OpCodes.Ldc_I4_S
                    && Convert.ToInt32(code[i - 3].operand) == 20;
                if (!repair) continue;
                for (int j = i - 1; j >= Math.Max(0, i - 50); j--)
                {
                    MethodInfo call = code[j].operand as MethodInfo;
                    if (call != null && call.DeclaringType.Name == "MyInputManager" && call.Name == "ButtonDown")
                    { patch = j; break; }
                }
            }
            if (patch < 0) throw new InvalidOperationException("Native repair input layout changed");
            code[patch].operand = typeof(RepairTap).GetMethod("NativePressed");
            return code;
        }

        public static bool NativePressed(int id)
        { return CanStart && (Input.GetKeyDown(KeyCode.R) || _button(id)); }

        public static bool NativeBefore(object __instance, ref bool __result)
        {
            if (FastField.GetInt(_iteratorState, __instance) != 20) return true;
            Component manager = _iteratorManager.GetValue(__instance) as Component;
            GameObject player = MapTools.LocalPlayer();
            if (manager == null || player == null || manager.transform.root != player.transform.root) return true;
            if (_owner == null)
            {
                if (!CanStart || !Begin("vanilla-repair", KeyCode.R)) { __result = false; return false; }
                _native = manager;
            }
            if (_owner != "vanilla-repair" || _native != manager || !Check())
            { __result = false; return false; }
            return true;
        }

        public static void NativeAfter(object __instance, bool __result)
        {
            if (!__result && FastField.GetInt(_iteratorState, __instance) == 20 && _owner == "vanilla-repair"
                && _iteratorManager.GetValue(__instance) == (object)_native) End(_owner);
        }
        public static void NativeCancelled(object __instance)
        { if (ReferenceEquals(_native, __instance)) End("vanilla-repair"); }

        // Catch a real hit before regeneration can conceal it between frame samples.
        // Rejected hits and remote-player damage do not interrupt the local repair.
        public static void DamageBefore(Component __instance, out float __state)
        {
            __state = _owner != null && _player != null
                && __instance.transform.root == _player.transform.root
                ? FastField.GetNumber(_health, _life) : -1f;
        }
        public static void DamageAfter(float __state)
        {
            if (_owner != null && __state > 0f && FastField.GetNumber(_health, _life) < __state)
            { _lastHealth = __state; Check(); }
        }

        internal static bool Begin(string owner, KeyCode key)
        {
            if (!CanStart) return false;
            GameObject player = MapTools.LocalPlayer();
            if (player == null) return false;
            // Resolve once per action, never search components on a running tick.
            Type type = RevivalPlugin.TypeByName("PlayerLifeDataManager");
            Component life = type == null ? null : player.GetComponentInChildren(type);
            if (life == null) life = type == null ? null : player.GetComponentInParent(type);
            FieldInfo data = life == null ? null : FastField.Find(type, "_playerLifeData");
            _life = data == null ? null : data.GetValue(life);
            _health = _life == null ? null : FastField.Find(_life.GetType(), "Health");
            if (_health == null) return false;
            _lastHealth = FastField.GetNumber(_health, _life);
            if (_lastHealth <= 0f) return false;
            _owner = owner; _player = player; _key = key;
            _start = player.transform.position; _startedFrame = Time.frameCount;
            return true;
        }

        internal static void End(string owner)
        {
            if (_owner != owner) return;
            _owner = null; _player = null; _life = null; _native = null;
        }
        internal static bool Check()
        {
            if (_owner == null) return false;
            bool available = _player != null && _player.activeInHierarchy
                && _player == MapTools.LocalPlayer() && !GameUi.WindowOpen;
            float health = FastField.GetNumber(_health, _life);
            float distance = available ? (_player.transform.position - _start).sqrMagnitude : 0f;
            bool pressed = Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(_key);
            // Freeze hooks prevent actual motion; movement intent must still cancel.
            // Native actions 1..4 are movement, including remapped/alternative keys.
            bool moving = (_held != null && (_held(1) || _held(2) || _held(3) || _held(4)))
                || Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A)
                || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)
                || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow)
                || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow);
            if (!RepairTapCore.Cancel(Time.frameCount, _startedFrame, pressed,
                moving, distance, health, _lastHealth, available))
            { _lastHealth = health; return true; }
            string owner = _owner;
            Component native = _native;
            _blockedFrame = Time.frameCount;
            End(owner);
            if (native != null) _cancel.Invoke(native, null);
            else NativeActionProgress.End(owner);
            return false;
        }
        internal static void Tick() { if (_owner != null) Check(); }
    }
}

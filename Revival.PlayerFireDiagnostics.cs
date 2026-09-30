// Revival.PlayerFireDiagnostics.cs - which gate refused a player's shot.
//
// The native chain (PlayerFirearmWeaponController, IL): WeaponKeyController
// runs isMine -> CantWorkWeapon -> _plrStates.AIMING -> CantShoot -> Fire, and
// Fire checks CurrentRateOfFireDelay against Time.time and
// Bullets[CurrentSlotID] > 0 before FireOneShot. The mod adds CantShoot
// postfixes (Flak, tower radar, drones, heli/An-2, mines, repairs) and the
// Stinger's Fire/FireOneShot prefixes.
//
// 6.62.0 (live log): an AmbiguousMatchException thrown in Stinger.FirePrefix
// aborted Fire for every firearm - a loaded rifle simply did not shoot. This
// file makes such a failure name itself. It logs ONE line, at most every 5 s,
// and only on the frame the fire button goes down:
//   * CantShoot is true: which of the mod's locks holds it, the UI state and
//     the camera owner (a blocked shot inside a legitimate seat/menu is
//     expected - the line says which one);
//   * an exception escaped a Fire patch: its type and message;
//   * a loaded weapon off cooldown did not consume a round.
// No update loop, no per-frame reflection; nothing is forced or suppressed.
//
// C# 3.0, ASCII.

using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class PlayerFireDiagnostics
    {
        const float Quiet = 5f;
        static float _next;
        static int _pressFrame = -1, _before = -1;
        static bool _ready;

        /// <summary>From Stinger.Install: the patches that watch the same
        /// methods the Stinger hooks. A failure here costs only the diagnosis.</summary>
        internal static void Install(Harmony harmony)
        {
            try
            {
                Type ctrl = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                MethodInfo cant = ctrl == null ? null : AccessTools.Method(ctrl, "CantShoot", null, null);
                MethodInfo fire = ctrl == null ? null : AccessTools.Method(ctrl, "Fire", null, null);
                if (cant == null || fire == null || cant.ReturnType != typeof(bool))
                {
                    RevivalPlugin.L.LogWarning("Fire diagnostics: CantShoot/Fire not found.");
                    return;
                }
                HarmonyMethod last = new HarmonyMethod(typeof(PlayerFireDiagnostics).GetMethod("CantShootPostfix"));
                last.priority = Priority.Last;   // sees the result after every lock
                harmony.Patch(cant, null, last, null, null, null);
                HarmonyMethod first = new HarmonyMethod(typeof(PlayerFireDiagnostics).GetMethod("FirePrefix"));
                first.priority = Priority.First; // the state before any other prefix
                harmony.Patch(fire, first, null, null,
                    new HarmonyMethod(typeof(PlayerFireDiagnostics).GetMethod("FireFinalizer")), null);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Fire diagnostics: " + ex.Message); }
        }

        static bool Pressed() { return Input.GetMouseButtonDown(0); }

        public static void CantShootPostfix(object __instance, bool __result)
        {
            if (!__result || Time.time < _next || !Pressed()) return;
            Report(__instance, "CantShoot is true");
        }

        public static void FirePrefix(object __instance)
        {
            if (!Pressed() || _pressFrame == Time.frameCount) return;
            try
            {
                _pressFrame = Time.frameCount;
                _before = Rounds(__instance);
                float delay = Float(Field(__instance, "CurrentRateOfFireDelay"), 0f);
                _ready = delay <= Time.time;
            }
            catch { _pressFrame = -1; }
        }

        public static Exception FireFinalizer(object __instance, Exception __exception)
        {
            if (__exception != null)
            {
                Report(__instance, "an exception escaped a Fire patch: "
                    + __exception.GetType().Name + ": " + __exception.Message);
                return __exception;
            }
            if (_pressFrame != Time.frameCount) return null;
            _pressFrame = -1;
            // Empty or still cooling down: the native gates doing their job.
            if (_before <= 0 || !_ready || Time.time < _next) return null;
            try
            {
                int after = Rounds(__instance);
                if (after >= 0 && after < _before) return null;
                bool stinger = false;
                try { stinger = Stinger.IsStinger(__instance); } catch { }
                if (stinger) return null; // the seeker's own gate (lock, sight) says why
                Report(__instance, "a loaded weapon off cooldown did not consume a round");
            }
            catch { }
            return null;
        }

        static void Report(object ctrl, string why)
        {
            if (Time.time < _next) return;
            _next = Time.time + Quiet;
            try
            {
                StringBuilder b = new StringBuilder(256);
                b.Append("Fire gate: ").Append(why);
                object weapons = Field(Field(ctrl, "_plrInventoryManager"), "_weaponsData");
                int slot = Int(Field(weapons, "CurrentSlotID"), -1);
                b.Append(" | item ").Append(Int(Field(Field(ctrl, "_weaponFirearmData"), "ItemID"), -1));
                b.Append(" slot ").Append(slot);
                b.Append(" loaded ").Append(Rounds(ctrl));
                Array shown = Field(weapons, "ShowInUI") as Array;
                if (shown != null && slot >= 0 && slot < shown.Length)
                    b.Append(" shownInUI ").Append(shown.GetValue(slot));
                float delay = Float(Field(ctrl, "CurrentRateOfFireDelay"), 0f);
                b.Append(" cooldown ").Append(Mathf.Max(0f, delay - Time.time).ToString("0.00")).Append(" s");
                b.Append(" aiming ").Append(Field(Field(ctrl, "_plrStates"), "AIMING"));
                b.Append(" | locks: ").Append(Locks());
                b.Append(" | UI state ").Append(GameUi.State).Append(", camera owner ").Append(CameraOwner.Owner);
                RevivalPlugin.L.LogInfo(b.ToString());
            }
            catch (Exception ex) { RevivalPlugin.L.LogInfo("Fire gate: " + why + " (state unreadable: " + ex.Message + ")"); }
        }

        /// <summary>The mod's CantShoot locks that hold right now.</summary>
        static string Locks()
        {
            StringBuilder b = new StringBuilder();
            if (Flak._manned != null) b.Append("flak sight ").Append(Flak._manned.Id).Append(", ");
            if (AirDefenceDamage.Repairing) b.Append("AA repair, ");
            if (RadarScope.InView) b.Append("tower radar, ");
            if (Drone.Flying) b.Append("FPV drone, ");
            if (Antenna.Frozen) b.Append("antenna, ");
            if (DroneGear.LaunchBusy) b.Append("drone launch hold, ");
            if (SurvDrone.Viewing) b.Append("surveillance view, ");
            if (PlayerHeli.Aboard) b.Append("helicopter, ");
            if (PlayerAn2.Aboard) b.Append("An-2, ");
            if (AntiTankMine.Placing) b.Append("mine placing, ");
            if (ConvoyRepair.Busy) b.Append("vehicle repair, ");
            if (An2Repair.Busy) b.Append("An-2 repair, ");
            if (FuelDepot.Busy) b.Append("fuel depot, ");
            if (b.Length == 0) return "none of the mod's (a native gate)";
            b.Length -= 2;
            return b.ToString();
        }

        static int Rounds(object ctrl)
        {
            object weapons = Field(Field(ctrl, "_plrInventoryManager"), "_weaponsData");
            Array bullets = Field(weapons, "Bullets") as Array;
            int slot = Int(Field(weapons, "CurrentSlotID"), -1);
            if (bullets == null || slot < 0 || slot >= bullets.Length) return -1;
            return Int(bullets.GetValue(slot), -1);
        }

        static object Field(object obj, string name)
        {
            if (obj == null) return null;
            FieldInfo f = FastField.Find(obj.GetType(), name);
            return f == null ? null : f.GetValue(obj);
        }

        static int Int(object value, int fallback)
        {
            if (value == null) return fallback;
            if (value is int) return (int)value;
            MethodInfo m = Stinger.ToInt(value.GetType());
            return m == null ? fallback : (int)m.Invoke(null, new object[] { value });
        }

        static float Float(object value, float fallback)
        {
            if (value == null) return fallback;
            if (value is float) return (float)value;
            MethodInfo[] ms = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
                if (ms[i].Name == "op_Implicit" && ms[i].ReturnType == typeof(float)
                    && ms[i].GetParameters().Length == 1 && ms[i].GetParameters()[0].ParameterType == value.GetType())
                    return (float)ms[i].Invoke(null, new object[] { value });
            return fallback;
        }
    }
}

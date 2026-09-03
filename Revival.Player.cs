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
    /// Haelt die Erkaeltung auf null.
    ///
    /// `PlayerLifeDataManager::PlayerColdController` laeuft im Takt von
    /// `Cold_Delay` und zaehlt `_playerLifeData.Cold` hoch, sobald die Stunde
    /// am `TOD_Sky` ueber 22 oder unter 7 liegt. Steigt Cold auf 100 und ist
    /// Temp 0, startet `ApplyPlayerSick("Temp", 50)`.
    ///
    /// Deshalb genuegt es NICHT, `cold` im gespeicherten Profil auf 0 zu
    /// setzen - nach der naechsten Nacht steht es wieder da. Der Prefix setzt
    /// den Wert und laesst das Original aus.
    /// </summary>
    [HarmonyPatch]
    public static class ColdHook
    {
        static FieldInfo _lifeData;
        static FieldInfo _cold;
        static FieldInfo _temp;
        static bool _looked;
        static bool _warned;

        public static bool Prefix(object __instance)
        {
            return Null(__instance, "Cold", ref _cold);
        }

        /// <summary>
        /// Das Fieber ist ein ZWEITER, eigener Zaehler - und der Grund, warum
        /// die Erkaeltung nach dem Heilen wiederkam.
        ///
        /// GEMESSEN (IL, 2026-08-28):
        /// ApplyPlayerLifeParamsConsequences ruft PlayerTempController auf,
        /// sobald _playerLifeData.Temp GROESSER NULL ist. PlayerTempController
        /// macht dann je Takt 1 Schaden, spielt den Hustenzustand und zaehlt
        /// Temp um 1 HOCH. Temp ist also kein Grad Celsius, sondern ein
        /// Fieberzaehler - gesund heisst Temp == 0.
        ///
        /// Beleg aus dem Profil: invtool.py hat Temp auf 36.6 gesetzt
        /// ("normale Koerpertemperatur"), nach der Sitzung stand 39.6 darin -
        /// genau drei Takte a plus eins. Die Heilung hat die Krankheit selbst
        /// am Leben gehalten.
        ///
        /// Der zweite Weg hinein bleibt trotzdem zu: PlayerColdController setzt
        /// bei Cold >= 100 ueber ApplyPlayerSick("Temp", 50) neues Fieber an -
        /// deshalb bleibt auch der Prefix auf Cold.
        /// </summary>
        public static bool TempPrefix(object __instance)
        {
            return Null(__instance, "Temp", ref _temp);
        }

        static bool Null(object __instance, string feld, ref FieldInfo cache)
        {
            if (!RevivalPlugin.CfgNoCold.Value) return true;
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    _lifeData = AccessTools.Field(__instance.GetType(), "_playerLifeData");
                }
                if (_lifeData == null) return true;
                object data = _lifeData.GetValue(__instance);
                if (data == null) return true;

                if (cache == null) cache = AccessTools.Field(data.GetType(), feld);
                if (cache == null) return true;

                cache.SetValue(data, ZeroLike(cache.FieldType));
                return false;                       // Original ueberspringen
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    RevivalPlugin.L.LogWarning("Erkaeltung: " + ex.Message
                        + " - der Zaehler laeuft weiter.");
                }
                return true;
            }
        }

        /// <summary>
        /// Cold ist ein ObscuredFloat. Der Wert 0 muss deshalb ueber die
        /// implizite Umwandlung des Typs entstehen, nicht als blanke Null.
        /// </summary>
        static object ZeroLike(Type t)
        {
            if (t == typeof(float)) return 0f;
            MethodInfo[] ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != "op_Implicit" || ms[i].ReturnType != t) continue;
                ParameterInfo[] ps = ms[i].GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(float))
                    return ms[i].Invoke(null, new object[] { 0f });
            }
            return Activator.CreateInstance(t);
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("PlayerLifeDataManager");
                if (t == null)
                {
                    RevivalPlugin.L.LogWarning("Erkaeltung: PlayerLifeDataManager nicht gefunden.");
                    return;
                }
                MethodInfo m = AccessTools.Method(t, "PlayerColdController", null, null);
                if (m == null)
                {
                    RevivalPlugin.L.LogWarning("Erkaeltung: PlayerColdController nicht gefunden.");
                    return;
                }
                harmony.Patch(m, new HarmonyMethod(typeof(ColdHook).GetMethod("Prefix")),
                              null, null, null, null);

                MethodInfo mt = AccessTools.Method(t, "PlayerTempController", null, null);
                if (mt == null)
                    RevivalPlugin.L.LogWarning("Fieber: PlayerTempController nicht gefunden - "
                        + "eine bestehende Erkaeltung heilt dann nicht von selbst aus.");
                else
                    harmony.Patch(mt, new HarmonyMethod(typeof(ColdHook).GetMethod("TempPrefix")),
                                  null, null, null, null);

                RevivalPlugin.L.LogInfo("Erkaeltung: PlayerColdController"
                    + (mt == null ? "" : " und PlayerTempController")
                    + " gepatcht (NoCold=" + RevivalPlugin.CfgNoCold.Value
                    + "), Cold und Temp werden auf 0 gehalten.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Erkaeltung konnte nicht abgeschaltet werden: " + ex);
            }
        }
    }
}

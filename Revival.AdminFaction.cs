// Next Day: Survival - Revival Toolkit
// Self-only native profile edits. No tick, polling, sidecar or custom RPC.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AdminFaction
    {
        static readonly string[] Names = { "Neutral", "Marauder", "Peace", "Hermit",
            "Wildman", "Military", "Traitor", "MilitaryNeutral" };
        static readonly string[] LabelsEn = { "Neutral", "Looter", "Civilian", "Hermit",
            "Wildman", "Military", "Traitor (90 min)", "Neutral military" };
        static readonly string[] LabelsRu = { "Нейтрал", "Мародёр", "Мирный", "Отшельник",
            "Дикарь", "Военный", "Предатель (90 мин)", "Нейтр. военный" };
        static int[] _values;
        static FieldInfo _info, _fraction, _reputation, _timer, _backend;
        static MethodInfo _getInt, _setInt, _network, _calculate, _save;
        static PropertyInfo _view, _mine;
        static bool _ready;
        static string _status;

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type stats = RevivalPlugin.TypeByName("PlayerStatisticsManager");
                _info = AccessTools.Field(stats, "_playerInfo");
                Type info = _info == null ? null : _info.FieldType;
                _fraction = AccessTools.Field(info, "fraction");
                _reputation = AccessTools.Field(info, "ReputationValue");
                _timer = AccessTools.Field(info, "TraitorTimeLeft");
                _backend = AccessTools.Field(stats, "_backendManager");
                _view = AccessTools.Property(stats, "photonView");
                _mine = _view == null ? null : AccessTools.Property(_view.PropertyType, "isMine");
                _network = AccessTools.Method(stats, "NetworkUpdateReputationName",
                    new Type[] { typeof(int) }, null);
                _calculate = AccessTools.Method(stats, "CalculateCurrentFraction", Type.EmptyTypes, null);
                _save = _backend == null ? null : AccessTools.Method(_backend.FieldType,
                    "ClientUpdatePlayerInfo", Type.EmptyTypes, null);
                if (_info == null || _fraction == null || !_fraction.FieldType.IsEnum
                    || _reputation == null || _timer == null || _backend == null
                    || _view == null || _mine == null || _network == null || _calculate == null
                    || _save == null || _reputation.FieldType != _timer.FieldType)
                    throw new InvalidOperationException("Native faction API incomplete.");
                // Both ObscuredInt conversions are named op_Implicit. Match the
                // parameter AND return type; name-only lookup is ambiguous.
                MethodInfo[] ops = _timer.FieldType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < ops.Length; i++)
                {
                    ParameterInfo[] p = ops[i].GetParameters();
                    if (ops[i].Name != "op_Implicit" || p.Length != 1) continue;
                    if (p[0].ParameterType == typeof(int) && ops[i].ReturnType == _timer.FieldType)
                        _setInt = ops[i];
                    if (p[0].ParameterType == _timer.FieldType && ops[i].ReturnType == typeof(int))
                        _getInt = ops[i];
                }
                if (_setInt == null || _getInt == null)
                    throw new InvalidOperationException("ObscuredInt conversion unavailable.");
                _values = new int[Names.Length];
                for (int i = 0; i < Names.Length; i++)
                    _values[i] = Convert.ToInt32(Enum.Parse(_fraction.FieldType, Names[i]));
                harmony.Patch(_calculate, new HarmonyMethod(typeof(AdminFaction).GetMethod(
                    "KeepSpecialFraction", BindingFlags.Public | BindingFlags.Static)), null, null, null, null);
                _ready = true;
                RevivalPlugin.L.LogInfo("Admin faction: native profile API and special-faction retention attached.");
            }
            catch (Exception ex)
            {
                _ready = false;
                RevivalPlugin.L.LogWarning("Admin faction unavailable: " + ex.Message);
            }
        }

        // Native login recalculates fraction from reputation and would erase
        // NPC-only enum choices. Preserve the saved native fraction while no
        // traitor penalty is active. Ordinary sides and real betrayals still
        // use the complete original calculation. Event-only, never a tick.
        public static bool KeepSpecialFraction(object __instance)
        {
            if (!_ready) return true;
            object info = _info.GetValue(__instance);
            if (info == null) return true;
            int side = FastField.GetInt(_fraction, info);
            if (!Special(side) || Number(_timer, info) > 0) return true;
            if (!Owns(__instance)) return true;
            _network.Invoke(__instance, new object[] { side });
            return false;
        }

        static bool Special(int side)
        {
            return side == _values[3] || side == _values[4]
                || side == _values[5] || side == _values[7];
        }

        static bool Owns(object stats)
        {
            object view = _view.GetValue(stats, null);
            return view != null && (bool)_mine.GetValue(view, null);
        }

        internal static void Draw()
        {
            AdminLayout.Section(Loc.T("ФРАКЦИЯ (ТОЛЬКО СЕБЕ)", "FACTION (YOURSELF ONLY)"));
            string[] labels = Loc.Lang() == 0 ? LabelsRu : LabelsEn;
            for (int i = 0; i < Names.Length; i += 4)
            {
                Rect row = AdminLayout.Row();
                for (int k = 0; k < 4 && i + k < Names.Length; k++)
                    if (UiKit.Button(UiKit.Col(row, k, 4), labels[i + k],
                        UiButton.Secondary, _ready, null)) Change(i + k, false);
            }
            Rect r = AdminLayout.Row();
            if (UiKit.Button(UiKit.Col(r, 0, 2), Loc.T("Сбросить предательство", "Reset traitor status"),
                UiButton.Secondary, _ready, null)) Change(-1, true);
            if (UiKit.Button(UiKit.Col(r, 1, 2), Loc.T("Показать мою фракцию", "Read my faction"),
                UiButton.Secondary, _ready, null)) Read();
            AdminLayout.Note(_status ?? (_ready
                ? Loc.T("Сброс сохраняет репутацию. Смена отправляет профиль на мастер-сервер.",
                    "Reset keeps reputation. Switching requests a master-server profile save.")
                : Loc.T("API фракций недоступен; подробности в логе.", "Faction API unavailable; see log.")));
        }

        // Click-only. Validate the actual serializer's profile reference and
        // connection before writing anything, including obscured value fields.
        internal static void Change(int pick, bool reset)
        {
            if (!_ready || !RevivalPlugin.CfgAdmin.Value || !Admin.HasAccess) return;
            if (!reset && (pick < 0 || pick >= Names.Length)) return;
            try
            {
                object stats = Admin.LocalStats();
                object info = stats == null ? null : _info.GetValue(stats);
                object backend = stats == null ? null : _backend.GetValue(stats);
                object data = Field(Field(backend, "currentGameModeCharacterData"), "characterData");
                object connection = Field(backend, "_connection");
                MethodInfo connected = connection == null ? null : AccessTools.Method(
                    connection.GetType(), "isConnected", Type.EmptyTypes, null);
                if (info == null || backend == null || !Owns(stats)
                    || !object.ReferenceEquals(info, Field(data, "playerInfo"))
                    || connected == null || !(bool)connected.Invoke(connection, null))
                {
                    Message(Loc.T("Нужен свой загруженный профиль и подключённый мастер-сервер; ничего не изменено.",
                        "Your loaded profile and a connected master server are required; nothing changed."));
                    return;
                }
                int reputation = Number(_reputation, info);
                int timer = 0;
                if (!reset)
                {
                    if (pick == 2) reputation = reputation > 0 ? reputation : 1;
                    else if (pick == 1) reputation = reputation < 0 ? reputation : -1;
                    else if (pick != 6) reputation = 0;
                    else timer = 5400; // native betrayal duration, 90 active minutes
                }
                // Convert both values before the first write. Reset never
                // touches reputation; traitor selection retains the base side.
                object newRep = _setInt.Invoke(null, new object[] { reputation });
                object newTimer = _setInt.Invoke(null, new object[] { timer });
                if (!reset) _reputation.SetValue(info, newRep);
                _timer.SetValue(info, newTimer);
                if (reset) _calculate.Invoke(stats, null);
                else _network.Invoke(stats, new object[] { _values[pick] });
                // The native method handles the Photon RPC, map markers, HUD
                // and quest faction transition before serializing the profile.
                _save.Invoke(backend, null);
                Message(Describe(info) + Loc.T(". Сохранение запрошено.", ". Save requested."));
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Admin faction: " + ex);
                Message(Loc.T("Ошибка API фракций. Проверьте профиль перед повтором; подробности в логе.",
                    "Faction API error. Read your profile before retrying; see log."));
            }
        }

        static object Field(object owner, string name)
        {
            FieldInfo f = owner == null ? null : AccessTools.Field(owner.GetType(), name);
            return f == null ? null : f.GetValue(owner);
        }

        static int Number(FieldInfo field, object info)
        {
            return (int)_getInt.Invoke(null, new object[] { field.GetValue(info) });
        }

        static string Describe(object info)
        {
            return Loc.T("Моя фракция: ", "My faction: ") + _fraction.GetValue(info)
                + Loc.T("; репутация: ", "; reputation: ") + Number(_reputation, info)
                + Loc.T("; предатель (сек): ", "; traitor (seconds): ") + Number(_timer, info);
        }

        static void Read()
        {
            try
            {
                object stats = Admin.LocalStats();
                object info = stats == null ? null : _info.GetValue(stats);
                Message(info == null || !Owns(stats)
                    ? Loc.T("Свой профиль ещё не загружен.", "Your profile has not loaded yet.") : Describe(info));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Admin faction read: " + ex.Message); }
        }

        static void Message(string text)
        {
            _status = text;
            UiKit.Toast(text, UiTone.Info);
            RevivalPlugin.L.LogInfo("Admin faction: " + text);
        }
    }
}

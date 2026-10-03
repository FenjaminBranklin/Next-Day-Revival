// Draw-only admin capture for the marked H0 offline game test copy.
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class Admin
    {
        static bool _adminScenarioRead, _adminScenario;
        static string _adminScenarioOut;
        static float _adminScenarioAt = -1f;
        static int _adminScenarioPhase = -1, _adminScenarioShot = -1, _adminScenarioLang;
        static FieldInfo _adminLang, _adminLangNext;
        static readonly string[] ScenarioTabs = { "players", "vehicles", "world", "mercs", "items", "tools", "perf" };

        static void MenuScenario()
        {
            // Caller gates this on OfflineStart.Active. The H0 bootstrap
            // validates the marked copy, output directory and exclusive lock.
            if (!_adminScenarioRead)
            {
                _adminScenarioRead = true;
                _adminScenario = Environment.GetEnvironmentVariable("NDR_ADMIN_MENU_CHECK") == "1";
                if (_adminScenario)
                {
                    _adminScenarioOut = ScenarioRun.Argument(Environment.GetCommandLineArgs(), "-ndrOut", "");
                    _adminLang = AccessTools.Field(typeof(Loc), "_lang");
                    _adminLangNext = AccessTools.Field(typeof(Loc), "_next");
                    _adminScenarioLang = Loc.Lang();
                }
            }
            if (!_adminScenario || !ScenarioRun.Measuring || Event.current.type != EventType.Repaint) return;
            if (_adminScenarioAt < 0f)
            {
                _adminScenarioAt = Time.unscaledTime;
                UiKit.Open(Win);
            }
            float elapsed = Time.unscaledTime - _adminScenarioAt;
            int phase = Mathf.Min(27, (int)(elapsed / 2f));
            int lang = phase / 14, tab = (phase % 14) / 2;
            if (_adminScenarioPhase != phase)
            {
                _adminScenarioPhase = phase;
                if (_adminLang != null) _adminLang.SetValue(null, lang);
                if (_adminLangNext != null) _adminLangNext.SetValue(null, float.MaxValue);
                _tab = tab; _nextLive = 0f;
                _scroll[tab].Offset = phase % 2 == 0 ? 0f : Mathf.Max(0f, _tabH[tab] - _viewH);
                // Display states only: never call money, faction, god-mode,
                // spawn, roster or other game-mutating actions for a picture.
                _targetActor = Net.OwnActor();
                _status = phase % 2 == 0 ? null : Loc.T(
                    "Визуальная проверка: действия не выполняются. Деньги, предметы и права проверяет мастер-сервер.",
                    "Visual check: actions are not executed. Money, items and permissions are checked by the master server.");
            }
            if (_adminScenarioShot != phase && elapsed - phase * 2f >= 1f)
            {
                string file = "admin-" + (lang == 0 ? "ru-" : "en-") + ScenarioTabs[tab] + (phase % 2 == 0 ? "-top.png" : "-bottom.png");
                ScreenCapture.CaptureScreenshot(Path.Combine(_adminScenarioOut, file));
                _adminScenarioShot = phase;
            }
            if (elapsed >= 56f)
            {
                if (_adminLang != null) _adminLang.SetValue(null, _adminScenarioLang);
                if (_adminLangNext != null) _adminLangNext.SetValue(null, 0f);
                UiKit.Close(Win);
                _adminScenario = false;
            }
        }
    }
}

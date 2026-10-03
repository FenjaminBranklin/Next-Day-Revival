// Draw-only merc menu capture for a marked H0 offline test process.
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        static bool _menuScenarioRead, _menuScenario;
        static string _menuScenarioOut;
        static float _menuScenarioAt = -1f;
        static int _menuScenarioPhase = -1, _menuScenarioShot = -1, _menuScenarioLang;
        static FieldInfo _menuLang, _menuLangNext;

        static bool MenuScenarioDraw()
        {
            // Caller checks OfflineStart.Active first. No normal-play work or
            // allocations, config changes, gameplay mutations or input injection.
            if (!_menuScenarioRead)
            {
                _menuScenarioRead = true;
                _menuScenario = Environment.GetEnvironmentVariable("NDR_MERC_MENU_CHECK") == "1";
                if (_menuScenario)
                {
                    _menuScenarioOut = ScenarioRun.Argument(Environment.GetCommandLineArgs(), "-ndrOut", "");
                    _menuLang = AccessTools.Field(typeof(Loc), "_lang");
                    _menuLangNext = AccessTools.Field(typeof(Loc), "_next");
                    _menuScenarioLang = Loc.Lang();
                }
            }
            if (!_menuScenario || !ScenarioRun.Measuring) return false;
            if (Event.current.type != EventType.Repaint || Mercs.Roster.Count == 0) return true;
            if (_menuScenarioAt < 0f)
            {
                Type type = RevivalPlugin.TypeByName("UIController");
                PropertyInfo instance = type == null ? null : AccessTools.Property(type, "Instance");
                object ui = instance == null ? null : instance.GetValue(null, null);
                FieldInfo market = type == null ? null : AccessTools.Field(type, "HUD_MarketplaceUI");
                GameObject root = ui == null || market == null ? null : market.GetValue(ui) as GameObject;
                if (root == null) return true;
                VanillaSkin.Resolve(root);
                if (!VanillaSkin.HasTextures) return true;
                VanillaSkin.Ui = ui;
                Mercs.Profile profile = Mercs.ProfileById(Mercs.Roster[0].ProfileId);
                _tabSettlement = profile == null ? "civilian" : profile.Settlement;
                MercPage.Open();
                _menuScenarioAt = Time.unscaledTime;
            }
            float elapsed = Time.unscaledTime - _menuScenarioAt;
            int phase = Mathf.Min(9, (int)(elapsed / 2f));
            int lang = phase / 5;
            if (_menuScenarioPhase != phase)
            {
                _menuScenarioPhase = phase;
                // Test process only: exercise both real localization branches,
                // without changing persisted game/plugin language settings.
                if (_menuLang != null) _menuLang.SetValue(null, lang);
                if (_menuLangNext != null) _menuLangNext.SetValue(null, float.MaxValue);
                MercPage.MenuScenarioSelect(phase % 5);
            }
            float k = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            // Use the normal throttled read path for real balance/locations.
            MercPage.Tick(Time.unscaledTime);
            MercPage.Draw(new Rect((Screen.width - 922f * k) * 0.5f, (Screen.height - 510f * k) * 0.5f, 922f * k, 510f * k));
            if (_menuScenarioShot != phase && elapsed - phase * 2f >= 1f)
            {
                string[] screens = { "hire", "contracts", "orders", "all-orders", "confirmation" };
                ScreenCapture.CaptureScreenshot(Path.Combine(_menuScenarioOut, "merc-menu-" + (lang == 0 ? "ru-" : "en-") + screens[phase % 5] + ".png"));
                _menuScenarioShot = phase;
            }
            if (elapsed >= 20f)
            {
                if (_menuLang != null) _menuLang.SetValue(null, _menuScenarioLang);
                if (_menuLangNext != null) _menuLangNext.SetValue(null, 0f);
                MercPage.Close();
                _menuScenario = false;
            }
            return true;
        }
    }

    internal static partial class MercPage
    {
        internal static void MenuScenarioSelect(int mode)
        {
            VanillaSkin.CancelAsk();
            _pendingHire = null;
            _pickedHire = 0;
            _pickedMerc = Mercs.Roster[0].Id;
            _contracts = mode > 0 && mode < 4;
            _orderFor = mode == 2 ? _pickedMerc : NoRow;
            _orderAll = mode == 3;
            if (mode == 4)
            {
                Cards(MercUi.EmbeddedSettlement, Lang());
                if (_cards.Count > 0) VanillaSkin.Ask(_cards[0].Question, 1);
            }
        }
    }
}

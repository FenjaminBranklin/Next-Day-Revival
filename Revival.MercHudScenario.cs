// Draw-only screenshot hook, gated by the marked offline scenario runner.
using System;
using System.IO;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        static bool _hudScenarioRead, _hudScenario;
        static string _hudScenarioOut;
        static float _hudScenarioAt = -1f;
        static int _hudScenarioCaptured;

        static bool HudScenarioDraw()
        {
            if (!_hudScenarioRead)
            {
                _hudScenarioRead = true;
                _hudScenario = Environment.GetEnvironmentVariable("NDR_MERC_HUD_CHECK") == "1";
                if (_hudScenario)
                    _hudScenarioOut = ScenarioRun.Argument(Environment.GetCommandLineArgs(), "-ndrOut", "");
            }
            if (!_hudScenario || !ScenarioRun.Measuring) return false;
            if (Event.current.type != EventType.Repaint) return true;
            if (Mercs.Roster.Count < 6) return true;
            if (_hudScenarioAt < 0f) _hudScenarioAt = Time.unscaledTime;
            float elapsed = Time.unscaledTime - _hudScenarioAt;
            // Real scenario mercs and real production paint; no injected input,
            // no hire/order/money calls and no changes to the gameplay roster.
            if (elapsed < 5f)
            {
                CacheStrip();
                DrawStrip();
                if (elapsed >= 2f && _hudScenarioCaptured == 0)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(_hudScenarioOut, "merc-squad.png"));
                    _hudScenarioCaptured = 1;
                }
            }
            else
            {
                _wheelPick = _wheelTextPick = 6; _wheelTextAt = float.MaxValue;
                _wheelWho = Loc.T("ВСЕ 6", "ALL 6");
                _wheelPoint = Loc.T("весь отряд: 52-К, радар, ЗУ-23; остальные за вами",
                    "whole squad: 52-K, radar, ZU-23; others follow");
                DrawWheel();
                if (elapsed >= 7f && _hudScenarioCaptured == 1)
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(_hudScenarioOut, "merc-wheel.png"));
                    _hudScenarioCaptured = 2;
                }
            }
            return true;
        }
    }
}

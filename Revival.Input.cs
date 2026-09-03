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

    // ------------------------------------------------------------ Cursor-Fix

    public static class CursorTracker
    {
        public static bool Restoring;
        public static CursorLockMode DesiredLock = CursorLockMode.None;
        public static bool DesiredVisible = true;
        public static bool SawCall;

        public static void AfterLock(CursorLockMode value)
        {
            if (Restoring) return;
            DesiredLock = value;
            SawCall = true;
        }

        public static void AfterVisible(bool value)
        {
            if (Restoring) return;
            DesiredVisible = value;
            SawCall = true;
        }
    }

    /// <summary>
    /// Haelt den Zeiger dort, wo das Spiel ihn haben will.
    ///
    /// Zwei getrennte Probleme, zwei getrennte Mittel:
    ///
    /// 1. Unity verwirft Cursor.lockState beim Fokusverlust und erwartet, dass
    ///    die Anwendung ihn wiederherstellt. In der gesamten Assembly-CSharp
    ///    gibt es genau ein OnApplicationFocus, und das sitzt auf AudioRpc -
    ///    nichts stellt den Lock wieder her. Deshalb wird der zuletzt
    ///    gewuenschte Zustand hier jeden Frame nachgezogen, nicht nur einmal
    ///    beim Fokuswechsel: der Lock geht auch verloren, wenn Windows
    ///    zwischendurch ein anderes Fenster aktiviert.
    ///
    /// 2. Der Lock allein reicht im Fenstermodus nicht. Steht der Zeiger
    ///    ausserhalb des Fensters, bekommt Unity die Klicks gar nicht erst -
    ///    sie gehen an das Fenster darunter. Genau das ist der Effekt "weit
    ///    nach links geschaut, Zeiger klebt am linken Rand, Klick passiert
    ///    nichts". Dagegen hilft nur ClipCursor aus user32: der Systemzeiger
    ///    wird auf das Client-Rechteck begrenzt.
    ///
    /// Begrenzt wird nur, solange gespielt wird (Zeiger versteckt) UND das
    /// Fenster den Fokus hat. Im Menue und bei Fokusverlust wird sofort wieder
    /// freigegeben, sonst haette man die Maus im Fenster gefangen.
    /// </summary>
    public static class CursorGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipCursorNull(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private static bool _clipped;
        private static bool _focused = true;
        private static int _failures;
        private static int _frame;

        // Windows hebt die Begrenzung bei jedem Fokuswechsel von selbst auf, sie
        // muss also nachgezogen werden. Jeden Frame waere Verschwendung; alle 15
        // Frames sind bei 60 fps eine Viertelsekunde und damit nicht spuerbar.
        private const int CLIP_EVERY = 15;

        public static void OnFocus(bool hasFocus)
        {
            _focused = hasFocus;
            if (!hasFocus) { Release(); return; }
            Restore();
        }

        public static void Tick()
        {
            if (!_focused) return;
            if (RevivalPlugin.CfgCursorFix != null && RevivalPlugin.CfgCursorFix.Value)
                Restore();

            if (RevivalPlugin.CfgConfine == null || !RevivalPlugin.CfgConfine.Value)
            {
                if (_clipped) Release();
                return;
            }

            // Versteckter Zeiger heisst: es wird gespielt, nicht geklickt.
            bool wantClip = !CursorTracker.DesiredVisible && CursorTracker.SawCall;
            if (!wantClip)
            {
                if (_clipped) Release();
                return;
            }
            _frame++;
            if (_clipped && (_frame % CLIP_EVERY) != 0) return;
            Clip();
        }

        private static void Restore()
        {
            if (!CursorTracker.SawCall) return;
            try
            {
                if (Cursor.lockState == CursorTracker.DesiredLock
                    && Cursor.visible == CursorTracker.DesiredVisible) return;
                CursorTracker.Restoring = true;
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
                CursorTracker.Restoring = false;
            }
            catch (Exception ex)
            {
                CursorTracker.Restoring = false;
                Warn("Cursor-Wiederherstellung: " + ex.Message);
            }
        }

        private static void Clip()
        {
            try
            {
                IntPtr hwnd = GetActiveWindow();
                if (hwnd == IntPtr.Zero) return;
                RECT c;
                if (!GetClientRect(hwnd, out c)) return;

                POINT tl; tl.X = c.Left; tl.Y = c.Top;
                POINT br; br.X = c.Right; br.Y = c.Bottom;
                if (!ClientToScreen(hwnd, ref tl)) return;
                if (!ClientToScreen(hwnd, ref br)) return;

                // Einen Pixel nach innen: liegt der Rand genau auf der
                // Bildschirmkante, laesst Windows den Zeiger sonst haengen.
                RECT r;
                r.Left = tl.X + 1; r.Top = tl.Y + 1;
                r.Right = br.X - 1; r.Bottom = br.Y - 1;
                if (r.Right <= r.Left || r.Bottom <= r.Top) return;

                ClipCursor(ref r);
                if (!_clipped)
                {
                    _clipped = true;
                    RevivalPlugin.L.LogInfo("Zeiger auf das Fenster begrenzt: "
                        + r.Left + "," + r.Top + " bis " + r.Right + "," + r.Bottom);
                }
            }
            catch (Exception ex) { Warn("ClipCursor: " + ex.Message); }
        }

        public static void Release()
        {
            if (!_clipped) return;
            try { ClipCursorNull(IntPtr.Zero); }
            catch (Exception ex) { Warn("ClipCursor freigeben: " + ex.Message); }
            _clipped = false;
        }

        private static void Warn(string msg)
        {
            // Ein Fehler pro Frame waere eine Logdatei im Gigabyte-Bereich.
            if (_failures >= 5) return;
            _failures++;
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning(msg);
        }
    }
}

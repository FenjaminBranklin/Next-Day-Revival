using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
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
    /// B5: UIController closes the player list with visible=false only; the
    /// group HUD close only fades its panel. Neither restores lockState.
    /// Repair both at the close boundary, and recover stale tracker state at
    /// 4 Hz. The group roster/invite hint stays on the HUD during gameplay.
    /// </summary>
    public static class GameplayCursor
    {
        delegate object ReadRef(object owner);
        delegate int ReadInt(object owner);
        static ReadRef _ui, _global, _player, _states, _chat, _buttons;
        static ReadInt _general, _additional, _death, _chatState, _window, _menu;
        static bool _ready, _inputReady, _ownsLock;
        static float _nextCheck;

        public static void Install(Harmony harmony)
        {
            try
            {
                Type ui = RevivalPlugin.TypeByName("UIController");
                Type global = RevivalPlugin.TypeByName("DontDestroyUIController");
                _ui = Ref(ui, "<Instance>k__BackingField");
                _global = Ref(global, "<Instance>k__BackingField");
                _player = Ref(ui, "Player");
                _states = Ref(ui, "_plrStates");
                _chat = Ref(ui, "HUD_Chat_Script");
                _buttons = Ref(ui, "_ButtonFormMoveSystem");
                _general = Int(ui, "_UI_General");
                _additional = Int(ui, "_UI_Additional");
                _death = Int(AccessTools.Field(ui, "_plrStates").FieldType, "_characterState");
                _chatState = Int(AccessTools.Field(ui, "HUD_Chat_Script").FieldType, "_chatState");
                _menu = Int(AccessTools.Field(ui, "_ButtonFormMoveSystem").FieldType, "OneMenuIsOpened");
                _window = Int(global, "currentUIWindow");
                // Command eligibility needs the readers, not optional cursor hooks.
                _inputReady = true;

                Hook(harmony, ui, "ClosePlayerListUI", "Closed");
                Hook(harmony, ui, "CloseUI", "Closed");
                // HUD_BaseState(true) is the shared return-to-play boundary,
                // including the player-list hotkey and Escape paths.
                Hook(harmony, ui, "HUD_BaseState", "HudRestored");
                Hook(harmony, ui, "ShowGroupSystemUI", "GroupClosed");
                Hook(harmony, RevivalPlugin.TypeByName("PlayerGroupManager"),
                    "ClearLastRecieveRequest", "Closed");
                _ready = true;
                RevivalPlugin.L.LogInfo("GameplayCursor: close hooks and 4 Hz recovery installed.");
            }
            catch (Exception ex)
            {
                _ready = false;
                RevivalPlugin.L.LogWarning("GameplayCursor: recovery disabled: " + ex.Message);
            }
        }

        static void Hook(Harmony harmony, Type type, string method, string after)
        {
            MethodInfo target = AccessTools.Method(type, method, null, null);
            if (target == null) throw new MissingMethodException(method);
            harmony.Patch(target, null, new HarmonyMethod(typeof(GameplayCursor).GetMethod(after)),
                null, null, null);
        }

        // Compile once at install, including enum/bool reads without boxing.
        // No reflection fallback: unavailable bindings must leave the cursor alone.
        static Delegate Reader(Type type, string name, Type result, Type signature)
        {
            FieldInfo field = type == null ? null : AccessTools.Field(type, name);
            if (field == null) throw new MissingFieldException(name);
            Type ft = field.FieldType;
            if (result == typeof(object) ? ft.IsValueType :
                !(ft == typeof(bool) || ft == typeof(int) ||
                  (ft.IsEnum && Enum.GetUnderlyingType(ft) == typeof(int))))
                throw new InvalidOperationException("Unexpected cursor field type: " + name);
            DynamicMethod dm = new DynamicMethod("ndr_cursor_" + name, result,
                new Type[] { typeof(object) }, field.DeclaringType, true);
            ILGenerator il = dm.GetILGenerator();
            if (field.IsStatic) il.Emit(OpCodes.Ldsfld, field);
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, field.DeclaringType);
                il.Emit(OpCodes.Ldfld, field);
            }
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(signature);
        }

        static ReadRef Ref(Type type, string name)
        {
            return (ReadRef)Reader(type, name, typeof(object), typeof(ReadRef));
        }

        static ReadInt Int(Type type, string name)
        {
            return (ReadInt)Reader(type, name, typeof(int), typeof(ReadInt));
        }

        public static bool CanRestore
        {
            get { return _ready && CanCommand; }
        }

        // Cached delegate reads the enum as an int; GameUi.State boxes its
        // reflected enum. Keep command polling free of that per-frame box.
        public static int CommandUiState
        {
            get
            {
                Behaviour ui = _inputReady ? _ui(null) as Behaviour : null;
                return ui == null ? 0 : _general(ui);
            }
        }

        public static bool CanCommand
        {
            get
            {
                if (!_inputReady || !CursorGuard.Focused || Time.timeScale == 0f ||
                    Admin.IsOpen || Patrol.EditorOpen || Settings.IsOpen || UiKit.AnyOpen) return false;
                Behaviour ui = _ui(null) as Behaviour;
                Behaviour global = _global(null) as Behaviour;
                if (ui == null || !ui.isActiveAndEnabled || global == null) return false;
                GameObject player = _player(ui) as GameObject;
                if (player == null || !player.activeInHierarchy) return false;
                object states = _states(ui), chat = _chat(ui), buttons = _buttons(ui);
                if (states == null || chat == null || buttons == null) return false;
                // IL: _UI_General=0 means gameplay, chat=3 means text input,
                // _characterState=8 means dead. Any additional/global UI blocks.
                return _general(ui) == 0 && _additional(ui) == 0 &&
                    _window(global) == 0 && _menu(buttons) == 0 &&
                    _chatState(chat) != 3 && _death(states) != 8;
            }
        }

        /// <summary>h-u1: chat text input (state 3). Hotkeys that no longer
        /// ask CanCommand must still not fire while the player types.</summary>
        public static bool Typing
        {
            get
            {
                Behaviour ui = _inputReady ? _ui(null) as Behaviour : null;
                object chat = ui == null ? null : _chat(ui);
                return chat != null && _chatState(chat) == 3;
            }
        }

        public static void Closed()
        {
            if (Enabled && CanRestore) Recover();
        }

        public static void HudRestored(bool __0) { if (__0) Closed(); }
        public static void GroupClosed(bool __0) { if (!__0) Closed(); }

        static bool Enabled
        {
            get { return RevivalPlugin.CfgCursorFix != null && RevivalPlugin.CfgCursorFix.Value; }
        }

        public static void Tick()
        {
            if (!Enabled || Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 0.25f;
            if (CanRestore) Recover();
        }

        static void Recover()
        {
            // Update the tracker too, or its per-frame focus fix replays the leak.
            CursorTracker.DesiredLock = CursorLockMode.Locked;
            CursorTracker.DesiredVisible = false;
            CursorTracker.SawCall = true;
            _ownsLock = true;
            bool restoring = CursorTracker.Restoring;
            CursorTracker.Restoring = true;
            try
            {
                if (Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
                if (Cursor.visible) Cursor.visible = false;
            }
            finally { CursorTracker.Restoring = restoring; }
        }

        public static void YieldToUi()
        {
            if (!_ownsLock || !CursorGuard.Focused) return;
            _ownsLock = false;
            // Vanilla opens many windows with visible=true only. Release OUR
            // gameplay lock immediately, otherwise Locked conceals their cursor.
            if (Cursor.lockState == CursorLockMode.Locked) Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            CursorTracker.DesiredLock = Cursor.lockState;
            CursorTracker.DesiredVisible = true;
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
        public static bool Focused { get { return _focused; } }
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
            if (GameplayCursor.CanRestore) Restore();
            else GameplayCursor.YieldToUi();
        }

        public static void Tick()
        {
            if (!_focused) return;
            // Check live UI state even between the low-rate recovery ticks.
            // Never replay a stale hidden/locked tracker over an open window.
            if (!GameplayCursor.CanRestore)
            {
                GameplayCursor.YieldToUi();
                Release();
                return;
            }
            if (RevivalPlugin.CfgCursorFix != null && RevivalPlugin.CfgCursorFix.Value)
            {
                GameplayCursor.Tick();
                Restore();
            }

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

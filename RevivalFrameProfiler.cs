// Next Day: Survival - Revival Toolkit
//
// FrameProf - an in-game, toggleable frame-time overlay for the toolkit's own
// per-frame work. It exists to ANSWER a reported FPS drop with a measurement
// instead of a guess: it shows the overall frame rate (with a 1% low, so a
// steady drop is told apart from occasional hitches) and, broken out, how many
// milliseconds each of our Update ticks and OnGUI draws costs THIS build. If our
// systems barely register, the cost is elsewhere (rendering the added vehicle
// geometry, the base game, drivers) and we look there; if one of ours stands
// out, it names itself.
//
// Cost when OFF is a single bool test per bracket (S/E return immediately) plus
// one subtraction per frame for the FPS ring - deliberately negligible, so the
// overlay never distorts the number it is trying to measure. It ships OFF and is
// toggled with a key at runtime (default F6). All player-visible strings go
// through Loc.T (bilingual); comments and logs stay ASCII.
//
// Isolation: this whole file is self-contained. The only edits in
// RevivalPlugin.cs are FrameProf.BindConfig in Awake, FrameProf.NewFrame plus an
// S/E pair around each module call in Update, an S/E pair around each draw in
// OnGUI, and FrameProf.DrawOverlay at the end of OnGUI. No shared state, so a
// merge agent folds it in beside the other branches.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// Per-slot frame-time accumulator and overlay. A "slot" is one bracketed
    /// call site (a module's Tick or Draw). <see cref="S"/> stamps the start,
    /// <see cref="E"/> adds the elapsed span to that slot's accumulator; both are
    /// no-ops while the overlay is off. <see cref="NewFrame"/>, called once at the
    /// top of Update, folds the finished frame's accumulators into a smoothed
    /// per-slot average and clears them for the next frame.
    /// </summary>
    public static class FrameProf
    {
        // ---- slot ids. Keep in sync with Names below. Update side 0..16, OnGUI
        //      side 17..26. Two ids per module where it both ticks and draws.
        public const int NetWatch     = 0;
        public const int AdminTick    = 1;
        public const int MapTeleTick  = 2;
        public const int Cursor       = 3;
        public const int Regions      = 4;
        public const int Research     = 5;
        public const int TurretTick   = 6;
        public const int VehModTick   = 7;
        public const int DroneTick    = 8;
        public const int DroneGearT   = 9;
        public const int Arena        = 10;
        public const int CarSpawn     = 11;
        public const int PatrolTick   = 12;
        public const int ConvRepTick  = 13;
        public const int ConvoyTick   = 14;
        public const int CrewDrone    = 15;
        public const int DroneAlrtT   = 16;

        public const int TurretScope  = 17;
        public const int DroneDraw    = 18;
        public const int DroneGearD   = 19;
        public const int PatrolMap    = 20;
        public const int MapTeleDraw  = 21;
        public const int AdminDraw    = 22;
        public const int PatrolDraw   = 23;
        public const int ConvRepDraw  = 24;
        public const int ConvoyDraw   = 25;
        public const int DroneAlrtD   = 26;

        public const int Count = 27;
        // The first OnGUI slot; slots below it are Update-side, at and above are
        // OnGUI-side. Used only to sum the two groups for the overlay.
        const int FirstDrawSlot = 17;

        static readonly string[] Names = new string[]
        {
            "NetWatch.Tick", "Admin.Tick", "MapTeleport.Tick", "CursorGuard",
            "Regions.Tick", "Research.Tick", "Turret.Tick", "VehicleModules.Tick",
            "Drone.Tick", "DroneGear.Tick", "Arena.Tick", "CarSpawn.Tick",
            "Patrol.Tick", "ConvoyRepair.Tick", "Convoy.Tick", "CrewDrone.Tick",
            "DroneAlert.Tick",
            "Turret.DrawScope", "Drone.Draw", "DroneGear.Draw", "Patrol.DrawMap",
            "MapTeleport.Draw", "Admin.Draw", "Patrol.Draw", "ConvoyRepair.Draw",
            "Convoy.Draw", "DroneAlert.Draw",
        };

        // Per-slot: start stamp (this frame) and accumulated ticks (this frame).
        static readonly long[] _mark = new long[Count];
        static readonly long[] _acc  = new long[Count];
        // Smoothed per-slot milliseconds, shown in the overlay.
        static readonly double[] _ms = new double[Count];

        // Stopwatch ticks -> milliseconds. Stopwatch, not DateTime: it is the
        // high-resolution timer and cheap to sample.
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        // Exponential smoothing so the numbers are readable, not a blur of noise.
        const double Smooth = 0.1;

        // ---- overall frame rate, tracked always (one subtraction per frame).
        const int Ring = 240;                       // ~4 s at 60 fps
        static readonly float[] _dt = new float[Ring];
        static int _dtN;
        static int _dtCount;
        static double _fpsAvg;
        static float _low1;                         // 1% low fps
        static float _lowThrottle;

        public static bool On;
        static ConfigEntry<string> _key;
        static ConfigEntry<bool> _start;
        static KeyCode _toggle = KeyCode.F6;
        static bool _keyResolved;

        public static void BindConfig(ConfigFile cfg)
        {
            _start = cfg.Bind("Diagnostics", "ShowFrameOverlay", false,
                "Blendet ein Messfenster ein, das die Bild-fuer-Bild-Kosten der "
                + "Toolkit-Systeme in Millisekunden zeigt (Gesamt-FPS, 1%-Low und "
                + "je Modul). Nur zur Fehlersuche gedacht, standardmaessig aus.");
            _key = cfg.Bind("Diagnostics", "FrameOverlayKey", "F6",
                "Taste, die das Messfenster im Spiel ein- und ausschaltet.");
            On = _start != null && _start.Value;
        }

        static KeyCode ToggleKey()
        {
            if (_keyResolved) return _toggle;
            _keyResolved = true;
            string s = _key != null ? _key.Value : "F6";
            try { _toggle = (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { _toggle = KeyCode.F6; }
            return _toggle;
        }

        /// <summary>Start of a bracket. No-op while the overlay is off.</summary>
        public static void S(int slot)
        {
            if (!On) return;
            if (slot < 0 || slot >= Count) return;
            _mark[slot] = Stopwatch.GetTimestamp();
        }

        /// <summary>End of a bracket. No-op while the overlay is off.</summary>
        public static void E(int slot)
        {
            if (!On) return;
            if (slot < 0 || slot >= Count) return;
            _acc[slot] += Stopwatch.GetTimestamp() - _mark[slot];
        }

        /// <summary>
        /// Called once at the very top of Update. Handles the toggle key, folds
        /// the frame that just ended into the smoothed averages, tracks the
        /// overall frame rate, and clears the accumulators for the new frame.
        /// </summary>
        public static void NewFrame()
        {
            try
            {
                if (Input.GetKeyDown(ToggleKey()))
                {
                    On = !On;
                    if (On)
                    {
                        // Entering: clear stale spans so the first shown frame is
                        // real, not a span measured across the paused interval.
                        for (int i = 0; i < Count; i++) { _acc[i] = 0; _ms[i] = 0; }
                    }
                }

                // Overall frame rate - tracked even while the per-slot overlay is
                // off, so toggling on shows a settled number immediately.
                float dt = Time.unscaledDeltaTime;
                if (dt > 0f && dt < 1f)
                {
                    _dt[_dtN] = dt;
                    _dtN = (_dtN + 1) % Ring;
                    if (_dtCount < Ring) _dtCount++;
                    double fps = 1.0 / dt;
                    _fpsAvg = _dtCount == 1 ? fps : _fpsAvg + (fps - _fpsAvg) * 0.05;
                }

                if (!On) return;

                // Fold the finished frame's per-slot spans into the averages.
                for (int i = 0; i < Count; i++)
                {
                    double ms = _acc[i] * TickMs;
                    _ms[i] = _ms[i] + (ms - _ms[i]) * Smooth;
                    _acc[i] = 0;
                }

                // 1% low, recomputed a few times a second (a small sort).
                if (Time.unscaledTime - _lowThrottle > 0.25f)
                {
                    _lowThrottle = Time.unscaledTime;
                    _low1 = OnePercentLow();
                }
            }
            catch { /* diagnostics must never throw into the frame loop */ }
        }

        /// <summary>Worst frame time in the ring as an fps figure (the 1% low).</summary>
        static float OnePercentLow()
        {
            int n = _dtCount;
            if (n < 20) return 0f;
            float[] copy = new float[n];
            Array.Copy(_dt, copy, n);
            Array.Sort(copy);                        // ascending frame time
            int idx = Mathf.Clamp((int)(n * 0.99f), 0, n - 1);
            float worst = copy[idx];
            return worst > 0f ? 1f / worst : 0f;
        }

        // -------------------------------------------------------------- overlay

        static Texture2D _bg;

        static Texture2D Bg()
        {
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, Color.white);
                _bg.Apply();
            }
            return _bg;
        }

        /// <summary>Called last in OnGUI. Draws nothing while off.</summary>
        public static void DrawOverlay()
        {
            if (!On) return;
            try
            {
                // Group sums.
                double updMs = 0, drawMs = 0;
                for (int i = 0; i < FirstDrawSlot; i++) updMs += _ms[i];
                for (int i = FirstDrawSlot; i < Count; i++) drawMs += _ms[i];
                double ourMs = updMs + drawMs;
                float frameMs = Time.unscaledDeltaTime > 0f
                    ? Time.unscaledDeltaTime * 1000f : 0f;
                float pct = frameMs > 0.01f ? (float)(ourMs / frameMs * 100.0) : 0f;

                // Rank the slots for a "top consumers" list.
                int[] order = new int[Count];
                for (int i = 0; i < Count; i++) order[i] = i;
                for (int a = 0; a < Count - 1; a++)
                    for (int b = a + 1; b < Count; b++)
                        if (_ms[order[b]] > _ms[order[a]])
                        { int t = order[a]; order[a] = order[b]; order[b] = t; }

                float x = 12f, y = 12f, w = 340f;
                int shown = 10;
                float lh = 16f;
                float h = lh * (shown + 6) + 16f;

                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(x, y, w, h), Bg());
                GUI.color = old;

                float tx = x + 10f, ty = y + 8f;
                Line(tx, ref ty, w, lh, new Color(0.7f, 0.9f, 1f, 1f),
                    Loc.T("NDR-Messfenster (" + ToggleKey() + " = aus)",
                          "NDR frame overlay (" + ToggleKey() + " = off)"));
                Line(tx, ref ty, w, lh, Color.white, string.Format(
                    Loc.T("FPS {0:0}   Bild {1:0.0} ms   1%-Low {2:0}",
                          "FPS {0:0}   frame {1:0.0} ms   1% low {2:0}"),
                    _fpsAvg, frameMs, _low1));

                Color acc = pct > 25f ? new Color(1f, 0.55f, 0.4f, 1f)
                                      : new Color(0.6f, 0.95f, 0.6f, 1f);
                Line(tx, ref ty, w, lh, acc, string.Format(
                    Loc.T("Toolkit gesamt {0:0.00} ms ({1:0}% vom Bild)",
                          "Toolkit total {0:0.00} ms ({1:0}% of frame)"),
                    ourMs, pct));
                Line(tx, ref ty, w, lh, new Color(0.8f, 0.85f, 0.9f, 1f), string.Format(
                    Loc.T("  Update {0:0.00} ms    OnGUI {1:0.00} ms",
                          "  Update {0:0.00} ms    OnGUI {1:0.00} ms"),
                    updMs, drawMs));

                ty += 4f;
                Line(tx, ref ty, w, lh, new Color(0.7f, 0.9f, 1f, 1f),
                    Loc.T("Groesste Posten (ms/Bild):",
                          "Top consumers (ms/frame):"));
                for (int i = 0; i < shown && i < Count; i++)
                {
                    int s = order[i];
                    double v = _ms[s];
                    Color c = v > 1.0 ? new Color(1f, 0.6f, 0.45f, 1f)
                            : v > 0.3 ? new Color(1f, 0.9f, 0.55f, 1f)
                                      : new Color(0.72f, 0.75f, 0.8f, 1f);
                    Line(tx, ref ty, w, lh, c, string.Format(
                        "  {0,-20} {1,6:0.000}", Names[s], v));
                }
            }
            catch { /* never throw into OnGUI */ }
        }

        static void Line(float x, ref float y, float w, float lh, Color c, string text)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.Label(new Rect(x, y, w - 20f, lh + 2f), text);
            GUI.color = old;
            y += lh;
        }
    }
}

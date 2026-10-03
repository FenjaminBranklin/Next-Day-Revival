// Next Day: Survival - Revival Toolkit
//
// UiKit - the shared look for every in-game window the mod draws (task W-UI1):
// dark translucent panels with rounded corners and a soft shadow, one clean
// typeface (Segoe UI from the OS, the built-in font as fallback), one spacing
// scale, cards, icons, hover and tooltips, tabs, buttons with states,
// switches, sliders, a text field, a scroll area, toasts, and explicit
// loading / error / success states (a UiRetryClock line never says "waiting"
// forever: "Connection lost - retrying in N s"). Mouse and keyboard: Up/Down
// or Tab move the focus, Enter activates, Left/Right adjust, Esc closes.
// Everything scales with the screen height (UiScale, [UI] Scale).
//
// The rules for using it are docs/UI_KIT.md; the Unity-free logic (retry
// clock, toast ring, keyboard state, cached strings) is Revival.UiKitCore.cs;
// the demo window is UiDemo below (admin panel -> "UI kit demo").
//
// Performance: no GUILayout and no GUI.Window (both allocate per pass and
// bring the old skin). Controls are hand-rolled on GUIUtility.GetControlID
// and GUIStyle.Draw, so a steady-state repaint allocates nothing. Textures and
// styles are built once and rebuilt only when the scale changes. Nothing is
// drawn and nothing runs while no kit window is open and no toast is shown
// (UiKit.Tick / UiKit.DrawOverlay then cost one bool and one int test).
//
// SEAMS: RevivalPlugin BindConfig -> UiKit.BindConfig, Update -> UiKit.Tick +
// UiDemo.Tick (FrameProf S_UiKitT), OnGUI -> UiDemo.Draw + UiKit.DrawOverlay
// (FrameProf S_UiKitD), cursor release while UiKit.AnyOpen (RevivalPlugin and
// CursorTracker.CanRestore), Admin panel button "UI kit demo".
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>Icon ids for UiKit.Icon.</summary>
    public static class UiIcon
    {
        public const int Close = 0;
        public const int Check = 1;
        public const int Info = 2;
        public const int Warning = 3;
        public const int Error = 4;
        public const int Success = 5;
        public const int ChevronRight = 6;
        public const int ChevronDown = 7;
        public const int Dot = 8;
        public const int Plus = 9;
        public const int Minus = 10;
        public const int Signal = 11;
        public const int User = 12;
        public const int Count = 13;
    }

    /// <summary>Text roles; sizes are at scale 1.0 (1080 lines).</summary>
    public static class UiFont
    {
        public const int Title = 0;     // 18 bold - window titles
        public const int Heading = 1;   // 15 bold - section and card titles
        public const int Body = 2;      // 14 - labels, values, buttons
        public const int Small = 3;     // 12 - captions, hints, chips
        public const int Count = 4;

        public const int Left = 0;
        public const int Center = 1;
        public const int Right = 2;
    }

    /// <summary>Button looks.</summary>
    public static class UiButton
    {
        public const int Primary = 0;
        public const int Secondary = 1;
        public const int Danger = 2;
        public const int Ghost = 3;
    }

    /// <summary>One kit window: size in design pixels (scale 1.0), position
    /// in screen pixels, its keyboard state and its open flag. Create once
    /// (a static field), open with UiKit.Open, draw with Begin/EndWindow.</summary>
    public sealed class UiWindow
    {
        public readonly string TitleRu;
        public readonly string TitleEn;
        public float Width;
        public float Height;
        public readonly UiNav Nav = new UiNav();

        internal bool IsOpen;
        internal bool Placed;
        internal float X, Y;
        internal float PlacedScale;
        internal float DragDx, DragDy;
        internal bool ShowFocus;
        internal Rect Screen;
        /// <summary>The content area in the window's own coordinates.</summary>
        public Rect Content;

        public UiWindow(string titleRu, string titleEn, float width, float height)
        {
            TitleRu = titleRu;
            TitleEn = titleEn;
            Width = width;
            Height = height;
        }

        public bool Open { get { return IsOpen; } }
    }

    /// <summary>Scroll offset of one UiKit.BeginScroll area.</summary>
    public sealed class UiScroll
    {
        public float Offset;
        internal float ContentHeight;
        internal float DragFrom;
    }

    public static class UiKit
    {
        // ---- palette (docs/UI_KIT.md "Colour").
        public static readonly Color Panel = new Color(0.259f, 0.259f, 0.259f, 0.90f);
        // Title/hub/label backing under white text: the vanilla plate's dark (c-u1).
        public static readonly Color Header = new Color(0.07f, 0.07f, 0.07f, 0.94f);
        public static readonly Color CardFill = new Color(0.329f, 0.329f, 0.329f, 1f);
        public static readonly Color CardHover = VanillaUi.Hover;
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Field = new Color(0.259f, 0.259f, 0.259f, 1f);
        public static readonly Color Accent = VanillaUi.Gold;
        public static readonly Color AccentHover = VanillaUi.Hover;
        public static readonly Color AccentPress = VanillaUi.Pressed;
        public static readonly Color Text = VanillaUi.White;
        public static readonly Color TextDim = VanillaUi.Grey;
        public static readonly Color TextOnAccent = VanillaUi.White;
        public static readonly Color Good = VanillaUi.Green;
        public static readonly Color Warn = VanillaUi.Gold;
        public static readonly Color Bad = VanillaUi.Red;
        public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.35f);

        // ---- spacing at scale 1.0 (a 4 px grid).
        public const float Gap = 8f;
        public const float Pad = 16f;
        public const float RowH = 32f;
        public const float HeaderH = 44f;

        static ConfigEntry<float> _userScale;
        static float _s = 1f;
        static bool _ready;
        static bool _broken;
        static Font _font;

        static Texture2D _texS, _texM, _texPill, _texRing, _texCircle, _texWhite;
        static GUIStyle _shapeS, _shapeM, _shapePill, _shapeRing;
        static readonly Texture2D[] _icons = new Texture2D[UiIcon.Count];
        static readonly GUIStyle[] _text = new GUIStyle[UiFont.Count * 3];
        static GUIStyle _wrap, _field, _tipStyle;
        static readonly GUIContent _gc = new GUIContent();

        static int _open;
        static UiWindow _cur;
        static string _tip;
        static string _tipLast;
        static float _tipSince;
        static bool _clip;
        static Rect _clipRect;

        static readonly UiToastRing _toasts = new UiToastRing();
        static readonly float[] _spinX = new float[8];
        static readonly float[] _spinY = new float[8];

        /// <summary>The scale of the last pass (1.0 at 1080 lines).</summary>
        public static float Scale { get { return _s; } }

        /// <summary>Design pixels to screen pixels.</summary>
        public static float S(float v) { return v * _s; }

        /// <summary>True while any kit window is open (the cursor belongs to it).</summary>
        public static bool AnyOpen { get { return _open > 0; } }

        internal static void BindConfig(ConfigFile cfg)
        {
            _userScale = cfg.Bind("UI", "Scale", 1f,
                "Size of the mod's new-style windows (UI kit) on top of the automatic "
                + "resolution scale (1.0 at 1080 lines). 0.5 .. 2.0.");
        }

        // ------------------------------------------------------------ frame seams

        /// <summary>Update: keeps the toast ring short. One int test when idle.</summary>
        internal static void Tick()
        {
            if (_toasts.Count > 0) _toasts.Prune(Time.realtimeSinceStartup);
        }

        /// <summary>OnGUI, after every kit window: toasts, and the cursor for open
        /// windows. One int test and one bool test when idle.</summary>
        internal static void DrawOverlay()
        {
            if (_open > 0) FreeCursor();
            if (_toasts.Count == 0 || !Ensure()) return;
            if (Event.current.type != EventType.Repaint) return;
            UiToast toast = _toasts.At(_toasts.Count - 1);
            VanillaUi.Notice(toast.Text, toast.Kind == UiTone.Error || toast.Kind == UiTone.Warning
                ? NativeMessage.Warning : NativeMessage.Inventory);
        }

        /// <summary>Shows a toast for 4 s (errors 6 s). The same text again
        /// refreshes the one already shown.</summary>
        public static void Toast(string text, int tone)
        {
            int type = tone == UiTone.Error || tone == UiTone.Warning ? NativeMessage.Warning
                : tone == UiTone.Success ? NativeMessage.Stats : NativeMessage.Inventory;
            if (NativeMessage.Show(text, type)) return;
            _toasts.Push(text, tone, Time.realtimeSinceStartup, tone == UiTone.Error ? 6f : 4f);
        }

        // ------------------------------------------------------------ windows

        public static void Open(UiWindow w)
        {
            if (w == null || w.IsOpen) return;
            w.IsOpen = true;
            w.Nav.Clear();
            w.ShowFocus = false;
            _open++;
            VanillaUi.Sound("OpenWindow");
        }

        public static void Close(UiWindow w)
        {
            if (w == null || !w.IsOpen) return;
            w.IsOpen = false;
            w.Nav.Clear();
            _open--;
            if (_open < 0) _open = 0;
            GUIUtility.keyboardControl = 0;
            if (_open == 0) RestoreCursor();
        }

        public static void Toggle(UiWindow w)
        {
            if (w == null) return;
            if (w.IsOpen) Close(w); else Open(w);
        }

        /// <summary>Draws the frame (shadow, panel, title bar, close button),
        /// handles dragging and the keys, and opens a group on the content
        /// area: inside, (0,0) is the content's top left, w.Content its size.
        /// Returns false (and opens nothing) while closed; else EndWindow must follow.</summary>
        public static bool BeginWindow(UiWindow w)
        {
            if (w == null || !w.IsOpen || !Ensure()) return false;
            Event e = Event.current;
            float W = S(w.Width), H = S(w.Height);
            float sw = Screen.width, sh = Screen.height;
            if (!w.Placed || w.PlacedScale != _s)
            {
                float cx = w.Placed ? w.X + w.Screen.width * 0.5f : sw * 0.5f;
                float cy = w.Placed ? w.Y + w.Screen.height * 0.5f : sh * 0.5f;
                w.X = cx - W * 0.5f;
                w.Y = cy - H * 0.5f;
                w.Placed = true;
                w.PlacedScale = _s;
            }
            if (W > sw) W = sw;
            if (H > sh) H = sh;
            w.X = Mathf.Clamp(w.X, 0f, sw - W);
            w.Y = Mathf.Clamp(w.Y, 0f, sh - H);
            Rect r = new Rect(w.X, w.Y, W, H);
            w.Screen = r;
            _cur = w;
            _tip = null;
            _clip = false;
            w.Nav.BeginPass();

            if (e.type == EventType.KeyDown)
            {
                // A focused text field keeps its keys; Esc first leaves the field.
                if (GUIUtility.keyboardControl == 0) { if (Keys(w, e)) e.Use(); }
                else if (e.keyCode == KeyCode.Escape) { GUIUtility.keyboardControl = 0; e.Use(); }
            }
            if (!w.IsOpen) { _cur = null; return false; }
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                // A click anywhere drops the text focus; a text field under the
                // mouse takes it back later in this same pass.
                w.ShowFocus = false;
                GUIUtility.keyboardControl = 0;
            }

            float hh = S(HeaderH);
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            if (AdminPaint.Active) AdminPaint.Window(r);
            else VanillaUi.Panel(r, "warning_02_empty");
            // The title bar: rounded on top only - the lower corners are
            // clipped off by a group instead of overdrawn (no alpha band).
            // The group runs in every pass: it takes a control id.
            GUI.BeginGroup(new Rect(r.x, r.y, W, hh));
            // The textured form supplies the title backing.
            GUI.EndGroup();
            if (e.type == EventType.Repaint)
            {
                Fill(new Rect(r.x, r.y + hh, W, Mathf.Max(1f, S(1f))), Line, 0);
                Label(new Rect(r.x + S(Pad + 12f), r.y, W - S(Pad + 60f), hh),
                      Loc.T(w.TitleRu, w.TitleEn), UiFont.Title, UiFont.Left, Text);
            }
            float xs = S(28f);
            Rect xr = new Rect(r.xMax - S(10f) - xs, r.y + (hh - xs) * 0.5f, xs, xs);
            if (IconButton(xr, UiIcon.Close, Loc.T("Закрыть (Esc)", "Close (Esc)"), false))
            {
                Close(w);
                _cur = null;
                return false;
            }

            // Dragging by the title bar.
            int dragId = GUIUtility.GetControlID(FocusType.Passive);
            Rect bar = new Rect(r.x, r.y, W - xs - S(16f), hh);
            switch (e.GetTypeForControl(dragId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && bar.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = dragId;
                        w.DragDx = e.mousePosition.x - w.X;
                        w.DragDy = e.mousePosition.y - w.Y;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == dragId)
                    {
                        w.X = e.mousePosition.x - w.DragDx;
                        w.Y = e.mousePosition.y - w.DragDy;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == dragId) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
            }

            float p = S(Pad);
            Rect c = new Rect(r.x + p, r.y + hh + p, W - 2f * p, H - hh - 2f * p);
            w.Content = new Rect(0f, 0f, c.width, c.height);
            GUI.BeginGroup(c);
            return true;
        }

        /// <summary>Closes the content group, draws the tooltip and keeps
        /// clicks on the window from reaching anything drawn after it.</summary>
        public static void EndWindow(UiWindow w)
        {
            if (_clip) EndScroll();   // content threw inside a scroll area
            GUI.EndGroup();
            Event e = Event.current;
            if (_tip != null)
            {
                if (!ReferenceEquals(_tip, _tipLast)) { _tipLast = _tip; _tipSince = Time.realtimeSinceStartup; }
                if (e.type == EventType.Repaint && Time.realtimeSinceStartup - _tipSince >= 0.35f)
                    DrawTip(_tip, e.mousePosition);
            }
            else if (e.type == EventType.Repaint) _tipLast = null;
            if (w != null)
            {
                EventType t = e.type;
                if ((t == EventType.MouseDown || t == EventType.MouseUp || t == EventType.ScrollWheel)
                    && w.Screen.Contains(e.mousePosition)) e.Use();
                w.Nav.EndPass();
            }
            _cur = null;
            _clip = false;
            GUI.color = Color.white;
        }

        static bool Keys(UiWindow w, Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Escape: Close(w); return true;
                case KeyCode.UpArrow: w.Nav.Move(-1); break;
                case KeyCode.DownArrow: w.Nav.Move(1); break;
                case KeyCode.Tab: w.Nav.Move(e.shift ? -1 : 1); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: w.Nav.Press(); break;
                case KeyCode.LeftArrow: w.Nav.Adjust(-1); break;
                case KeyCode.RightArrow: w.Nav.Adjust(1); break;
                default: return false;
            }
            w.ShowFocus = true;
            return true;
        }

        // ------------------------------------------------------------ primitives

        /// <summary>Rounded fill. round: 0 square, 1 medium radius (7 px, rects
        /// at least 14 px), 2 small radius (3 px), 3 pill (9 px, height 18 px).</summary>
        public static void Fill(Rect r, Color c, int round)
        {
            if (Event.current.type != EventType.Repaint || !_ready) return;
            if (round != 0 && r.width >= S(100f) && r.height >= S(24f)) { Plate(r, c); return; }
            GUI.color = c;
            if (round == 0) GUI.DrawTexture(r, _texWhite);
            else if (round == 1) _shapeM.Draw(r, false, false, false, false);
            else if (round == 2) _shapeS.Draw(r, false, false, false, false);
            else _shapePill.Draw(r, false, false, false, false);
            GUI.color = Color.white;
        }

        // Rounded fills become vanilla brush plates by the colour's role (c-u1).
        // The plate keeps the requested contrast: translucent washes stay
        // translucent, bright accent/hover/selection uses the native red
        // btn_hover stroke, light grey (disabled pick) the grey btn stroke,
        // everything else the icon-free dark plate. White
        // text therefore never lands on the white groupPlayerWhite stroke.
        static void Plate(Rect r, Color c)
        {
            if (c.a < 0.5f) VanillaUi.Panel(r, "groupPlayerWhite", c);
            else if (Bright(c)) VanillaUi.Panel(r, "btn_hover", c.a < 1f ? new Color(1f, 1f, 1f, c.a) : Color.white);
            else if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 0.55f) VanillaUi.Panel(r, "btn", new Color(1f, 1f, 1f, Mathf.Max(c.a, 0.85f)));
            else VanillaUi.Panel(r, r.height < S(80f) ? VanillaUi.Plate : "MarkerInfo", new Color(1f, 1f, 1f, Mathf.Max(c.a, 0.85f)));
        }

        /// <summary>A light, saturated colour (gold accent, hover beige): a selection fill.</summary>
        internal static bool Bright(Color c)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return max > 0.55f && max - min > 0.2f;
        }

        /// <summary>A 1 px rounded outline (medium radius).</summary>
        public static void Outline(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint || !_ready) return;
            GUI.color = c;
            _shapeRing.Draw(r, false, false, false, false);
            GUI.color = Color.white;
        }

        public static void Circle(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint || !_ready) return;
            GUI.color = c;
            GUI.DrawTexture(r, _texCircle);
            GUI.color = Color.white;
        }

        public static void Icon(Rect r, int icon, Color c)
        {
            if (Event.current.type != EventType.Repaint || !_ready || icon < 0 || icon >= UiIcon.Count) return;
            GUI.color = c;
            Texture2D native = icon == UiIcon.Check ? VanillaUi.Asset("galka_enable") : null;
            GUI.DrawTexture(r, native != null ? native : _icons[icon]);
            GUI.color = Color.white;
        }

        /// <summary>One line of text, vertically centred in r, clipped.</summary>
        public static void Label(Rect r, string s, int font, int align, Color c)
        {
            if (s == null || Event.current.type != EventType.Repaint || !_ready) return;
            if (AdminPaint.Active) { AdminPaint.Label(r, s, font, align, VanillaUi.TextColour(c), false); return; }
            GUI.color = VanillaUi.TextColour(c);
            _gc.text = s;
            GUIStyle label = _text[font * 3 + align];
            Font face = VanillaUi.Font(font <= UiFont.Heading);
            if (face != null && label.font != face) label.font = face;
            label.Draw(r, _gc, false, false, false, false);
            GUI.color = Color.white;
        }

        /// <summary>Wrapped body text from the top of r.</summary>
        public static void Paragraph(Rect r, string s, Color c)
        {
            if (s == null || Event.current.type != EventType.Repaint || !_ready) return;
            GUI.color = c;
            _gc.text = s;
            _wrap.Draw(r, _gc, false, false, false, false);
            GUI.color = Color.white;
        }

        /// <summary>Height a Paragraph needs at this width.</summary>
        public static float ParagraphHeight(string s, float width)
        {
            if (s == null || !Ensure()) return 0f;
            _gc.text = s;
            return _wrap.CalcHeight(_gc, width);
        }

        /// <summary>A section title with a hairline under it.</summary>
        public static void Section(Rect r, string title)
        {
            Label(r, title, UiFont.Small, UiFont.Left, TextDim);
            Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), Line, 0);
        }

        public static void Divider(Rect r)
        {
            Fill(new Rect(r.x, r.y + r.height * 0.5f, r.width, Mathf.Max(1f, S(1f))), Line, 0);
        }

        /// <summary>A card: a raised surface; hover lightens it when the mouse is over.</summary>
        public static bool Card(Rect r)
        {
            bool hot = Hover(r);
            Fill(r, hot ? CardHover : CardFill, 1);
            return hot;
        }

        /// <summary>A small rounded tag ("ONLINE", "3/5").</summary>
        public static void Chip(Rect r, string text, int tone)
        {
            if (AdminPaint.Active) { AdminPaint.Chip(r, text, tone); return; }
            Color c = ToneColor(tone);
            Fill(r, Fade(c, 0.18f), 3);
            Label(r, text, UiFont.Small, UiFont.Center, c);
        }

        /// <summary>Determinate progress 0..1.</summary>
        public static void Progress(Rect r, float t, Color c)
        {
            Fill(r, Field, 2);
            t = Mathf.Clamp01(t);
            if (t > 0f) Fill(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * t), r.height), c, 2);
        }

        /// <summary>The loading spinner: eight dots, one bright, turning.</summary>
        public static void Spinner(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint || !_ready) return;
            float cx = r.x + r.width * 0.5f, cy = r.y + r.height * 0.5f;
            float rad = Mathf.Min(r.width, r.height) * 0.38f;
            float d = Mathf.Max(2f, rad * 0.45f);
            int head = (int)(Time.realtimeSinceStartup * 10f) & 7;
            for (int i = 0; i < 8; i++)
            {
                int age = (head - i + 8) & 7;
                float a = 1f - age * 0.11f;
                Circle(new Rect(cx + _spinX[i] * rad - d * 0.5f, cy + _spinY[i] * rad - d * 0.5f, d, d), Fade(c, a));
            }
        }

        /// <summary>An explicit state line: spinner for Loading, an icon
        /// otherwise, the text in the tone's colour on a tinted strip.</summary>
        public static void Status(Rect r, int tone, string text)
        {
            if (AdminPaint.Active) { AdminPaint.Status(r, tone, text); return; }
            Color c = ToneColor(tone);
            Fill(r, Fade(c, 0.12f), 2);
            Fill(new Rect(r.x, r.y, S(3f), r.height), c, 0);
            float isz = S(18f);
            Rect ir = new Rect(r.x + S(12f), r.y + (r.height - isz) * 0.5f, isz, isz);
            if (tone == UiTone.Loading) Spinner(ir, c); else Icon(ir, ToneIcon(tone), c);
            Label(new Rect(ir.xMax + S(10f), r.y, r.width - isz - S(30f), r.height), text, UiFont.Body, UiFont.Left, Text);
        }

        /// <summary>The standard line for anything that waits on a server:
        /// connecting (spinner), online, or "connection lost - retrying in N s".</summary>
        public static void LinkStatus(Rect r, UiRetryClock clock, double now, string onlineText)
        {
            switch (clock.State)
            {
                case UiRetryClock.Connecting:
                    Status(r, UiTone.Loading, Loc.T("Подключение...", "Connecting..."));
                    break;
                case UiRetryClock.Online:
                    Status(r, UiTone.Success, onlineText);
                    break;
                case UiRetryClock.Lost:
                    Status(r, UiTone.Error, UiText.Retry(clock.SecondsLeft(now), Loc.Lang()));
                    break;
                default:
                    Status(r, UiTone.Info, Loc.T("Не подключено", "Not connected"));
                    break;
            }
        }

        // ------------------------------------------------------------ controls

        /// <summary>A button. Returns true on click (mouse up inside) or Enter while focused.</summary>
        public static bool Button(Rect r, string text, int look, bool enabled, string tip)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            int nav = NavIndex();
            bool click = Click(r, id, enabled);
            if (enabled && TakeActivate(nav)) click = true;
            if (click) FocusFrom(nav);
            if (tip != null) Tip(r, tip);
            if (Event.current.type == EventType.Repaint)
            {
                bool hot = enabled && Hover(r);
                bool down = enabled && GUIUtility.hotControl == id;
                if (AdminPaint.Active)
                {
                    AdminPaint.Button(r, text, look, enabled, hot, down);
                    FocusRing(r, nav);
                }
                else
                {
                Color bg, fg;
                switch (look)
                {
                    case UiButton.Primary:
                        bg = down ? AccentPress : hot ? AccentHover : Accent;
                        fg = TextOnAccent;
                        break;
                    case UiButton.Danger:
                        bg = down ? Fade(Bad, 0.75f) : hot ? Bad : Fade(Bad, 0.88f);
                        fg = TextOnAccent;
                        break;
                    case UiButton.Ghost:
                        bg = down ? Fade(Color.white, 0.10f) : hot ? Fade(Color.white, 0.06f) : Fade(Color.white, 0f);
                        fg = Text;
                        break;
                    default:
                        bg = down ? Header : hot ? CardHover : CardFill;
                        fg = Text;
                        break;
                }
                if (!enabled) { bg = Fade(bg, 0.35f); fg = Fade(fg, 0.45f); }
                Color saved = GUI.color;
                GUI.color = !enabled ? Color.grey : down ? AccentPress : hot ? AccentHover : Color.white;
                Texture2D button = VanillaUi.Asset(hot ? "btn_hover" : "btn");
                if (button != null) GUI.DrawTexture(r, button); else Fill(r, bg, 1);
                GUI.color = saved;
                FocusRing(r, nav);
                Label(r, text, UiFont.Body, UiFont.Center, enabled ? Text : TextDim);
                }
            }
            if (click) VanillaUi.Sound("Click");
            return click;
        }

        /// <summary>A square icon button (close, add, remove).</summary>
        public static bool IconButton(Rect r, int icon, string tip, bool focusable)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            int nav = focusable ? NavIndex() : -1;
            bool click = Click(r, id, true);
            if (nav >= 0 && TakeActivate(nav)) click = true;
            if (tip != null) Tip(r, tip);
            if (Event.current.type == EventType.Repaint)
            {
                bool hot = Hover(r);
                if (hot) Fill(r, Fade(Color.white, GUIUtility.hotControl == id ? 0.14f : 0.08f), 1);
                FocusRing(r, nav);
                float pad = r.width * 0.25f;
                Texture2D close = icon == UiIcon.Close ? VanillaUi.Asset("close_btn") : null;
                if (close != null)
                {
                    GUI.color = hot ? AccentHover : Color.white;
                    GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, r.width - 2f * pad, r.height - 2f * pad), close);
                    GUI.color = Color.white;
                }
                else Icon(new Rect(r.x + pad, r.y + pad, r.width - 2f * pad, r.height - 2f * pad), icon, hot ? Text : TextDim);
            }
            if (click) VanillaUi.Sound("Click");
            return click;
        }

        /// <summary>A switch row: the label left, the switch right; the whole row is the target.</summary>
        public static bool Toggle(Rect r, bool on, string label, string tip)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            int nav = NavIndex();
            bool flip = Click(r, id, true) || TakeActivate(nav);
            int adj = TakeAdjust(nav);
            if (adj != 0 && (adj > 0) != on) flip = true;
            if (flip) { on = !on; FocusFrom(nav); }
            if (tip != null) Tip(r, tip);
            if (Event.current.type == EventType.Repaint)
            {
                bool hot = Hover(r);
                if (hot) Fill(r, Fade(Color.white, 0.04f), 1);
                FocusRing(r, nav);
                float th = S(16f), tw = S(16f);
                Rect track = new Rect(r.xMax - tw - S(8f), r.y + (r.height - th) * 0.5f, tw, th);
                Texture2D check = VanillaUi.Asset(on ? "galka_enable" : "galka_disable");
                if (check != null)
                {
                    GUI.color = hot ? AccentHover : Color.white;
                    GUI.DrawTexture(track, check);
                    GUI.color = Color.white;
                }
                else Outline(track, TextDim);
                Label(new Rect(r.x + S(8f), r.y, r.width - tw - S(24f), r.height), label, UiFont.Body, UiFont.Left, Text);
            }
            if (flip) VanillaUi.Sound("Click");
            return on;
        }

        /// <summary>A segmented tab bar. Left/Right switch while focused.</summary>
        public static int Tabs(Rect r, int selected, string[] labels)
        {
            int n = labels == null ? 0 : labels.Length;
            if (n == 0) return selected;
            int nav = NavIndex();
            int adj = TakeAdjust(nav);
            if (adj != 0) selected = Mathf.Clamp(selected + adj, 0, n - 1);
            if (!AdminPaint.Active) Fill(r, Field, 1);
            FocusRing(r, nav);
            float seg = r.width / n;
            for (int i = 0; i < n; i++)
            {
                int id = GUIUtility.GetControlID(FocusType.Passive);
                Rect sr = new Rect(r.x + seg * i + S(3f), r.y + S(3f), seg - S(6f), r.height - S(6f));
                if (Click(sr, id, true)) { selected = i; FocusFrom(nav); }
                if (Event.current.type != EventType.Repaint) continue;
                bool on = i == selected;
                if (AdminPaint.Active) AdminPaint.Button(sr, labels[i], on ? UiButton.Primary : UiButton.Secondary, true, Hover(sr), GUIUtility.hotControl == id);
                else
                {
                if (on) Fill(sr, Accent, 1);
                else if (Hover(sr)) Fill(sr, Fade(Color.white, 0.06f), 1);
                Label(sr, labels[i], UiFont.Body, UiFont.Center, on ? TextOnAccent : TextDim);
                }
            }
            return selected;
        }

        /// <summary>A horizontal slider; step 0 = continuous (keys move 5 %).</summary>
        public static float Slider(Rect r, float v, float min, float max, float step)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            int nav = NavIndex();
            Event e = Event.current;
            float kw = S(16f);
            float x0 = r.x + kw * 0.5f, x1 = r.xMax - kw * 0.5f;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && Hover(r))
                    {
                        GUIUtility.hotControl = id;
                        FocusFrom(nav);
                        v = FromX(e.mousePosition.x, x0, x1, min, max, step);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        v = FromX(e.mousePosition.x, x0, x1, min, max, step);
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
            }
            int adj = TakeAdjust(nav);
            if (adj != 0)
            {
                float st = step > 0f ? step : (max - min) * 0.05f;
                v = Mathf.Clamp(v + adj * st, min, max);
            }
            if (e.type == EventType.Repaint)
            {
                float t = max > min ? Mathf.Clamp01((v - min) / (max - min)) : 0f;
                float th = S(6f);
                Rect track = new Rect(x0, r.y + (r.height - th) * 0.5f, x1 - x0, th);
                Fill(track, Fade(Color.white, 0.14f), 2);
                Fill(new Rect(track.x, track.y, track.width * t, th), Accent, 2);
                float kx = x0 + (x1 - x0) * t - kw * 0.5f;
                bool hot = GUIUtility.hotControl == id || Hover(r);
                float k = hot ? kw + S(2f) : kw;
                Rect knob = new Rect(kx + (kw - k) * 0.5f, r.y + (r.height - k) * 0.5f, k, k);
                if (NavFocused(nav)) Circle(new Rect(knob.x - S(4f), knob.y - S(4f), k + S(8f), k + S(8f)), Fade(Accent, 0.35f));
                Circle(knob, Text);
            }
            return v;
        }

        /// <summary>A single-line text field in the kit look.</summary>
        public static string TextField(Rect r, string s, int maxLength)
        {
            if (!Ensure()) return s;
            if (AdminPaint.Active)
            {
                AdminPaint.Plate(r, false, false, false, true);
                Font font = VanillaUi.Font(false);
                if (font != null && _field.font != font) _field.font = font;
            }
            else Fill(r, Field, 1);
            Outline(r, Hover(r) ? Fade(Accent, 0.6f) : Line);
            GUI.color = Text;
            string n = GUI.TextField(r, s ?? "", maxLength, _field);
            GUI.color = Color.white;
            return n;
        }

        /// <summary>A scroll area: returns the content rect (0,0 .. view width,
        /// contentHeight) to draw into. Wheel and a draggable thumb. EndScroll must follow.</summary>
        public static Rect BeginScroll(Rect view, UiScroll sc, float contentHeight)
        {
            Event e = Event.current;
            float max = Mathf.Max(0f, contentHeight - view.height);
            sc.ContentHeight = contentHeight;
            if (e.type == EventType.ScrollWheel && view.Contains(e.mousePosition) && max > 0f)
            {
                sc.Offset += e.delta.y * S(12f);
                e.Use();
            }
            // The thumb.
            int id = GUIUtility.GetControlID(FocusType.Passive);
            float bw = S(6f);
            Rect bar = new Rect(view.xMax - bw, view.y, bw, view.height);
            float th = max > 0f ? Mathf.Max(S(24f), view.height * view.height / contentHeight) : view.height;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (max > 0f && e.button == 0 && new Rect(bar.x - S(4f), bar.y, bw + S(8f), bar.height).Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        sc.DragFrom = e.mousePosition.y - sc.Offset / max * (view.height - th);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        sc.Offset = (e.mousePosition.y - sc.DragFrom) / Mathf.Max(1f, view.height - th) * max;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
            }
            sc.Offset = Mathf.Clamp(sc.Offset, 0f, max);
            if (max > 0f && e.type == EventType.Repaint)
            {
                Fill(bar, Fade(Color.white, 0.05f), 2);
                float ty = view.y + (max > 0f ? sc.Offset / max * (view.height - th) : 0f);
                Fill(new Rect(bar.x, ty, bw, th), Fade(Color.white, GUIUtility.hotControl == id ? 0.40f : 0.22f), 2);
            }
            float cw = max > 0f ? view.width - bw - S(6f) : view.width;
            GUI.BeginGroup(new Rect(view.x, view.y, cw, view.height));
            GUI.BeginGroup(new Rect(0f, -sc.Offset, cw, Mathf.Max(contentHeight, view.height)));
            _clip = true;
            _clipRect = new Rect(0f, sc.Offset, cw, view.height);
            return new Rect(0f, 0f, cw, contentHeight);
        }

        public static void EndScroll()
        {
            GUI.EndGroup();
            GUI.EndGroup();
            _clip = false;
        }

        /// <summary>Column i of n across row r with the kit gap.</summary>
        public static Rect Col(Rect row, int i, int n)
        {
            float g = S(Gap);
            float w = (row.width - g * (n - 1)) / n;
            return new Rect(row.x + (w + g) * i, row.y, w, row.height);
        }

        /// <summary>A tooltip for r, shown after 0.35 s hover.</summary>
        public static void Tip(Rect r, string tip)
        {
            if (tip != null && Hover(r)) _tip = tip;
        }

        /// <summary>The mouse is over r (and inside the visible part of a scroll area).</summary>
        public static bool Hover(Rect r)
        {
            Vector2 m = Event.current.mousePosition;
            return r.Contains(m) && (!_clip || _clipRect.Contains(m));
        }

        public static Color Fade(Color c, float a)
        {
            c.a *= a;
            return c;
        }

        public static Color ToneColor(int tone)
        {
            switch (tone)
            {
                case UiTone.Success: return Good;
                case UiTone.Warning: return Warn;
                case UiTone.Error: return Bad;
                default: return Accent;
            }
        }

        public static int ToneIcon(int tone)
        {
            switch (tone)
            {
                case UiTone.Success: return UiIcon.Success;
                case UiTone.Warning: return UiIcon.Warning;
                case UiTone.Error: return UiIcon.Error;
                default: return UiIcon.Info;
            }
        }

        // ------------------------------------------------------------ internals

        static Rect Shift(Rect r, float dx, float dy) { return new Rect(r.x + dx, r.y + dy, r.width, r.height); }

        static float FromX(float x, float x0, float x1, float min, float max, float step)
        {
            float t = x1 > x0 ? Mathf.Clamp01((x - x0) / (x1 - x0)) : 0f;
            float v = min + (max - min) * t;
            if (step > 0f) v = min + Mathf.Round((v - min) / step) * step;
            return Mathf.Clamp(v, min, max);
        }

        static bool Click(Rect r, int id, bool enabled)
        {
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (enabled && e.button == 0 && Hover(r)) { GUIUtility.hotControl = id; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                        return enabled && Hover(r);
                    }
                    break;
            }
            return false;
        }

        static int NavIndex() { return _cur == null ? -1 : _cur.Nav.Next(); }
        static bool NavFocused(int nav) { return nav >= 0 && _cur != null && _cur.ShowFocus && _cur.Nav.IsFocused(nav); }
        static bool TakeActivate(int nav) { return nav >= 0 && _cur != null && _cur.Nav.TakeActivate(nav); }
        static int TakeAdjust(int nav) { return nav >= 0 && _cur != null ? _cur.Nav.TakeAdjust(nav) : 0; }
        static void FocusFrom(int nav) { if (nav >= 0 && _cur != null) _cur.Nav.SetFocus(nav); }

        static void FocusRing(Rect r, int nav)
        {
            if (!NavFocused(nav)) return;
            float o = S(2f);
            Outline(new Rect(r.x - o, r.y - o, r.width + 2f * o, r.height + 2f * o), Accent);
        }

        static void DrawTip(string tip, Vector2 at)
        {
            _gc.text = tip;
            float maxW = S(280f);
            Vector2 sz = _tipStyle.CalcSize(_gc);
            float w = Mathf.Min(maxW, sz.x) + S(20f);
            float h = _tipStyle.CalcHeight(_gc, w - S(20f)) + S(12f);
            float x = Mathf.Min(at.x + S(14f), Screen.width - w - 2f);
            float y = at.y + S(20f);
            if (y + h > Screen.height) y = at.y - h - S(6f);
            Rect r = new Rect(x, y, w, h);
            Fill(Shift(r, 0f, S(2f)), Shadow, 2);
            Fill(r, new Color(0.18f, 0.19f, 0.22f, 0.98f), 2);
            GUI.color = Text;
            _tipStyle.Draw(new Rect(r.x + S(10f), r.y + S(6f), w - S(20f), h - S(12f)), _gc, false, false, false, false);
            GUI.color = Color.white;
        }

        static void FreeCursor()
        {
            CursorTracker.Restoring = true;
            try
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            finally { CursorTracker.Restoring = false; }
        }

        static void RestoreCursor()
        {
            if (!CursorTracker.SawCall) return;
            CursorTracker.Restoring = true;
            try
            {
                Cursor.lockState = CursorTracker.DesiredLock;
                Cursor.visible = CursorTracker.DesiredVisible;
            }
            catch (Exception ex) { Log("cursor back: " + ex.Message); }
            finally { CursorTracker.Restoring = false; }
        }

        static void Log(string s)
        {
            if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning("UiKit: " + s);
        }

        // ------------------------------------------------------------ build

        /// <summary>Textures and styles for the current scale. Builds on first
        /// use and when the scale changes; otherwise two compares.</summary>
        static bool Ensure()
        {
            if (_broken) return false;
            float s = UiScale.For(Screen.height, _userScale == null ? 1f : _userScale.Value);
            if (_ready && s == _s) return true;
            try
            {
                Build(s);
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                Log("build failed, kit windows stay hidden: " + ex);
                return false;
            }
        }

        static void Build(float s)
        {
            _s = s;
            _font = VanillaUi.Font(false);
            if (_texWhite == null)
            {
                _texWhite = Tex(2, 2, false);
                Color32[] px = new Color32[4];
                for (int i = 0; i < 4; i++) px[i] = new Color32(255, 255, 255, 255);
                _texWhite.SetPixels32(px);
                _texWhite.Apply(false, true);
                _texCircle = Rounded(64, 32, false, 0f);
                for (int i = 0; i < UiIcon.Count; i++) _icons[i] = MakeIcon(i);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI * 0.25f;
                    _spinX[i] = Mathf.Sin(a);
                    _spinY[i] = -Mathf.Cos(a);
                }
            }
            Kill(_texS); Kill(_texM); Kill(_texPill); Kill(_texRing);
            int rs = Mathf.Max(2, Mathf.RoundToInt(3f * s));
            int rm = Mathf.Max(3, Mathf.RoundToInt(7f * s));
            int rp = Mathf.Max(4, Mathf.RoundToInt(9f * s));
            _texS = Rounded(2 * rs + 2, rs, false, 0f);
            _texM = Rounded(2 * rm + 2, rm, false, 0f);
            _texPill = Rounded(2 * rp + 2, rp, false, 0f);
            _texRing = Rounded(2 * rm + 2, rm, true, Mathf.Max(1f, s));
            _shapeS = Shape(_texS, rs);
            _shapeM = Shape(_texM, rm);
            _shapePill = Shape(_texPill, rp);
            _shapeRing = Shape(_texRing, rm);

            int[] sizes = { 18, 15, 14, 12 };
            for (int f = 0; f < UiFont.Count; f++)
            {
                for (int a = 0; a < 3; a++)
                {
                    GUIStyle st = TextStyle(Mathf.RoundToInt(sizes[f] * s), f <= UiFont.Heading);
                    st.alignment = a == 0 ? TextAnchor.MiddleLeft : a == 1 ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight;
                    _text[f * 3 + a] = st;
                }
            }
            _wrap = TextStyle(Mathf.RoundToInt(14f * s), false);
            _wrap.wordWrap = true;
            _wrap.alignment = TextAnchor.UpperLeft;
            _tipStyle = TextStyle(Mathf.RoundToInt(12f * s), false);
            _tipStyle.wordWrap = true;
            _tipStyle.alignment = TextAnchor.UpperLeft;
            _field = TextStyle(Mathf.RoundToInt(14f * s), false);
            _field.alignment = TextAnchor.MiddleLeft;
            _field.padding = new RectOffset(Mathf.RoundToInt(10f * s), Mathf.RoundToInt(10f * s), 0, 0);
            _field.focused.textColor = Color.white;
            _field.hover.textColor = Color.white;
            _field.active.textColor = Color.white;
            _ready = true;
        }

        static GUIStyle TextStyle(int size, bool bold)
        {
            GUIStyle st = new GUIStyle();
            st.font = bold ? VanillaUi.Font(true) : _font;
            st.fontSize = size;
            st.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            st.normal.textColor = Color.white;
            st.clipping = TextClipping.Clip;
            st.wordWrap = false;
            st.richText = false;
            return st;
        }

        static GUIStyle Shape(Texture2D tex, int border)
        {
            GUIStyle st = new GUIStyle();
            st.normal.background = tex;
            st.border = new RectOffset(border, border, border, border);
            return st;
        }

        static void Kill(Texture2D t)
        {
            if (t != null) UnityEngine.Object.Destroy(t);
        }

        static Texture2D Tex(int w, int h, bool mips)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, mips);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        /// <summary>A white rounded square (size x size, corner radius r) with
        /// anti-aliased edges; ring = only a 'width' px outline.</summary>
        static Texture2D Rounded(int size, int r, bool ring, float width)
        {
            Texture2D t = Tex(size, size, false);
            Color32[] px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - r);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                    float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
                    float d = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                    float a = Mathf.Clamp01(0.5f - d);
                    if (ring) a = Mathf.Min(a, Mathf.Clamp01(0.5f + d + width));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        // ---- icons: signed distance shapes on a 48 px canvas (y down), mip-mapped.

        const int IconPx = 48;

        static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay, wx = px - ax, wy = py - ay;
            float t = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
            float dx = wx - vx * t, dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static float Len(float x, float y) { return Mathf.Sqrt(x * x + y * y); }

        static float IconDist(int icon, float x, float y)
        {
            const float T = 2.2f;   // half stroke
            float d = 99f;
            switch (icon)
            {
                case UiIcon.Close:
                    d = Mathf.Min(Seg(x, y, 13, 13, 35, 35), Seg(x, y, 35, 13, 13, 35)) - T;
                    break;
                case UiIcon.Check:
                    d = Mathf.Min(Seg(x, y, 11, 25, 20, 34), Seg(x, y, 20, 34, 37, 15)) - T;
                    break;
                case UiIcon.Info:
                    d = Mathf.Min(Mathf.Abs(Len(x - 24, y - 24) - 19f) - T * 0.9f,
                        Mathf.Min(Seg(x, y, 24, 22, 24, 34) - T, Len(x - 24, y - 15) - 2.8f));
                    break;
                case UiIcon.Warning:
                {
                    float tri = Mathf.Min(Seg(x, y, 24, 6, 42, 40), Mathf.Min(Seg(x, y, 42, 40, 6, 40), Seg(x, y, 6, 40, 24, 6)));
                    d = Mathf.Min(tri - T * 0.9f, Mathf.Min(Seg(x, y, 24, 17, 24, 28) - T, Len(x - 24, y - 34) - 2.6f));
                    break;
                }
                case UiIcon.Error:
                    d = Mathf.Min(Mathf.Abs(Len(x - 24, y - 24) - 19f) - T * 0.9f,
                        Mathf.Min(Seg(x, y, 17, 17, 31, 31), Seg(x, y, 31, 17, 17, 31)) - T);
                    break;
                case UiIcon.Success:
                    d = Mathf.Min(Mathf.Abs(Len(x - 24, y - 24) - 19f) - T * 0.9f,
                        Mathf.Min(Seg(x, y, 15, 25, 21, 31), Seg(x, y, 21, 31, 33, 18)) - T);
                    break;
                case UiIcon.ChevronRight:
                    d = Mathf.Min(Seg(x, y, 19, 12, 31, 24), Seg(x, y, 31, 24, 19, 36)) - T;
                    break;
                case UiIcon.ChevronDown:
                    d = Mathf.Min(Seg(x, y, 12, 19, 24, 31), Seg(x, y, 24, 31, 36, 19)) - T;
                    break;
                case UiIcon.Dot:
                    d = Len(x - 24, y - 24) - 8f;
                    break;
                case UiIcon.Plus:
                    d = Mathf.Min(Seg(x, y, 24, 11, 24, 37), Seg(x, y, 11, 24, 37, 24)) - T;
                    break;
                case UiIcon.Minus:
                    d = Seg(x, y, 11, 24, 37, 24) - T;
                    break;
                case UiIcon.Signal:
                {
                    // Three arcs over a dot, opening upwards (+-45 deg).
                    float dx = x - 24, dy = y - 38;
                    float r = Len(dx, dy);
                    bool inCone = dy < 0f && Mathf.Abs(dx) <= -dy;
                    float arcs = 99f;
                    if (inCone)
                        arcs = Mathf.Min(Mathf.Abs(r - 11f), Mathf.Min(Mathf.Abs(r - 20f), Mathf.Abs(r - 29f))) - T;
                    d = Mathf.Min(arcs, r - 3.5f);
                    break;
                }
                case UiIcon.User:
                {
                    float head = Len(x - 24, y - 16) - 8f;
                    float body = y < 42f ? Len((x - 24) * 0.85f, y - 44) - 15f : 99f;
                    d = Mathf.Min(head, Mathf.Max(body, 26f - y));
                    break;
                }
            }
            return d;
        }

        static Texture2D MakeIcon(int icon)
        {
            Texture2D t = Tex(IconPx, IconPx, true);
            Color32[] px = new Color32[IconPx * IconPx];
            for (int y = 0; y < IconPx; y++)
            {
                for (int x = 0; x < IconPx; x++)
                {
                    float d = IconDist(icon, x + 0.5f, y + 0.5f);
                    float a = Mathf.Clamp01(0.5f - d);
                    // Texture rows run bottom-up; the canvas is y down.
                    px[(IconPx - 1 - y) * IconPx + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }
    }

    /// <summary>
    /// The kit's demo and test window (admin panel -> "UI kit demo"): every
    /// control, every state, a simulated server link with the retry clock,
    /// toasts, cards, a scroll list. It is also the reference the window
    /// migrations copy from. Nothing runs while it is closed.
    /// </summary>
    public static class UiDemo
    {
        static readonly UiWindow Win = new UiWindow("Revival - UI-кит", "Revival - UI kit", 560f, 600f);
        static readonly string[] TabsRu = { "Элементы", "Состояния", "Карточки" };
        static readonly string[] TabsEn = { "Controls", "States", "Cards" };

        static int _tab;
        static bool _optA = true, _optB;
        static float _volume = 0.7f, _range = 40f;
        static string _name = "Kevin";
        static readonly UiMemo _volumeText = new UiMemo();
        static readonly UiMemo _rangeText = new UiMemo();
        static readonly UiScroll _list = new UiScroll();

        // Simulated server link: 0 answers, 1 refuses, 2 never answers (times out).
        static readonly UiRetryClock _link = new UiRetryClock();
        static int _mode;
        static double _answerAt;
        static float _progress;

        public static bool IsOpen { get { return Win.Open; } }

        public static void Toggle()
        {
            UiKit.Toggle(Win);
            if (Win.Open)
            {
                _link.Reset();
                _link.Start(Time.realtimeSinceStartup);
                _answerAt = Time.realtimeSinceStartup + 1.5;
            }
        }

        /// <summary>Update: the simulated request, only while open.</summary>
        internal static void Tick()
        {
            if (!Win.Open) return;
            double now = Time.realtimeSinceStartup;
            if (_link.Step(now)) _answerAt = now + 1.5;
            if (_link.State == UiRetryClock.Connecting && now >= _answerAt)
            {
                if (_mode == 0) _link.Succeed(now);
                else if (_mode == 1) _link.Fail(now);
                // _mode 2: no answer; the clock's Timeout turns it into "lost".
            }
            _progress += Time.unscaledDeltaTime * 0.2f;
            if (_progress > 1f) _progress = 0f;
        }

        internal static void Draw()
        {
            if (!UiKit.BeginWindow(Win)) return;
            try { Content(Win.Content); }
            catch (Exception ex) { if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning("UiDemo: " + ex.Message); }
            UiKit.EndWindow(Win);
        }

        static void Content(Rect c)
        {
            float row = UiKit.S(UiKit.RowH), gap = UiKit.S(UiKit.Gap);
            float y = 0f;
            _tab = UiKit.Tabs(new Rect(0f, y, c.width, row + UiKit.S(4f)), _tab, Loc.Lang() == 0 ? TabsRu : TabsEn);
            y += row + UiKit.S(4f) + UiKit.S(UiKit.Pad);
            Rect body = new Rect(0f, y, c.width, c.height - y);
            if (_tab == 0) Controls(body, row, gap);
            else if (_tab == 1) States(body, row, gap);
            else Cards(body, row, gap);
        }

        static void Controls(Rect b, float row, float gap)
        {
            float y = b.y;
            UiKit.Section(new Rect(0f, y, b.width, row * 0.75f), Loc.T("КНОПКИ", "BUTTONS"));
            y += row * 0.75f + gap;
            Rect r = new Rect(0f, y, b.width, row);
            if (UiKit.Button(UiKit.Col(r, 0, 4), Loc.T("Основная", "Primary"), UiButton.Primary, true,
                             Loc.T("Главное действие окна", "The window's main action")))
                UiKit.Toast(Loc.T("Сохранено", "Saved"), UiTone.Success);
            if (UiKit.Button(UiKit.Col(r, 1, 4), Loc.T("Вторичная", "Secondary"), UiButton.Secondary, true, null))
                UiKit.Toast(Loc.T("Обычное сообщение", "A plain notice"), UiTone.Info);
            if (UiKit.Button(UiKit.Col(r, 2, 4), Loc.T("Опасно", "Danger"), UiButton.Danger, true,
                             Loc.T("Необратимые действия: красным", "Irreversible actions are red")))
                UiKit.Toast(Loc.T("Не удалось: нет связи", "Failed: no connection"), UiTone.Error);
            UiKit.Button(UiKit.Col(r, 3, 4), Loc.T("Недоступно", "Disabled"), UiButton.Secondary, false,
                         Loc.T("Недоступная кнопка остаётся видимой", "A disabled button stays visible"));
            y += row + gap;
            r = new Rect(0f, y, b.width, row);
            if (UiKit.Button(UiKit.Col(r, 0, 2), Loc.T("Тост: предупреждение", "Toast: warning"), UiButton.Ghost, true, null))
                UiKit.Toast(Loc.T("Топлива мало", "Fuel is low"), UiTone.Warning);
            if (UiKit.Button(UiKit.Col(r, 1, 2), Loc.T("Тост: загрузка", "Toast: loading"), UiButton.Ghost, true, null))
                UiKit.Toast(Loc.T("Загружаем список...", "Loading the list..."), UiTone.Loading);
            y += row + gap * 2f;

            UiKit.Section(new Rect(0f, y, b.width, row * 0.75f), Loc.T("ПЕРЕКЛЮЧАТЕЛИ И ПОЛЗУНКИ", "SWITCHES AND SLIDERS"));
            y += row * 0.75f + gap;
            _optA = UiKit.Toggle(new Rect(0f, y, b.width, row), _optA, Loc.T("Показывать маркеры", "Show markers"),
                                 Loc.T("Вся строка нажимается", "The whole row is the target"));
            y += row + UiKit.S(4f);
            _optB = UiKit.Toggle(new Rect(0f, y, b.width, row), _optB, Loc.T("Тихий режим", "Quiet mode"), null);
            y += row + gap;

            float lw = b.width * 0.34f, vw = UiKit.S(64f);
            int vol = Mathf.RoundToInt(_volume * 100f);
            UiKit.Label(new Rect(0f, y, lw, row), Loc.T("Громкость", "Volume"), UiFont.Body, UiFont.Left, UiKit.Text);
            _volume = UiKit.Slider(new Rect(lw, y, b.width - lw - vw, row), _volume, 0f, 1f, 0.01f);
            string vt = _volumeText.Stale(vol) ? _volumeText.Set(vol, UiNum.Of(vol) + " %") : _volumeText.Text;
            UiKit.Label(new Rect(b.width - vw, y, vw, row), vt, UiFont.Body, UiFont.Right, UiKit.TextDim);
            y += row + UiKit.S(4f);
            int rng = Mathf.RoundToInt(_range);
            UiKit.Label(new Rect(0f, y, lw, row), Loc.T("Дальность", "Range"), UiFont.Body, UiFont.Left, UiKit.Text);
            _range = UiKit.Slider(new Rect(lw, y, b.width - lw - vw, row), _range, 10f, 200f, 5f);
            string rt = _rangeText.Stale(rng) ? _rangeText.Set(rng, UiNum.Of(rng) + " m") : _rangeText.Text;
            UiKit.Label(new Rect(b.width - vw, y, vw, row), rt, UiFont.Body, UiFont.Right, UiKit.TextDim);
            y += row + gap;

            UiKit.Label(new Rect(0f, y, lw, row), Loc.T("Имя", "Name"), UiFont.Body, UiFont.Left, UiKit.Text);
            _name = UiKit.TextField(new Rect(lw, y, b.width - lw, row), _name, 24);
            y += row + gap * 2f;
            UiKit.Paragraph(new Rect(0f, y, b.width, b.yMax - y),
                Loc.T("Клавиатура: вверх/вниз или Tab - фокус, Enter - нажать, влево/вправо - изменить, Esc - закрыть.",
                      "Keyboard: Up/Down or Tab move the focus, Enter presses, Left/Right adjust, Esc closes."),
                UiKit.TextDim);
        }

        static void States(Rect b, float row, float gap)
        {
            double now = Time.realtimeSinceStartup;
            float y = b.y;
            float sh = UiKit.S(40f);
            UiKit.Section(new Rect(0f, y, b.width, row * 0.75f), Loc.T("СОСТОЯНИЯ", "STATES"));
            y += row * 0.75f + gap;
            UiKit.Status(new Rect(0f, y, b.width, sh), UiTone.Loading, Loc.T("Загрузка списка...", "Loading the list..."));
            y += sh + gap;
            UiKit.Status(new Rect(0f, y, b.width, sh), UiTone.Success, Loc.T("Готово: 12 предметов", "Done: 12 items"));
            y += sh + gap;
            UiKit.Status(new Rect(0f, y, b.width, sh), UiTone.Warning, Loc.T("Мало денег", "Not enough money"));
            y += sh + gap;
            UiKit.Status(new Rect(0f, y, b.width, sh), UiTone.Error, Loc.T("Сервер отклонил запрос", "The server refused the request"));
            y += sh + gap;
            UiKit.Progress(new Rect(0f, y + UiKit.S(4f), b.width, UiKit.S(8f)), _progress, UiKit.Accent);
            y += UiKit.S(16f) + gap * 2f;

            UiKit.Section(new Rect(0f, y, b.width, row * 0.75f), Loc.T("СВЯЗЬ С СЕРВЕРОМ (СИМУЛЯЦИЯ)", "SERVER LINK (SIMULATED)"));
            y += row * 0.75f + gap;
            UiKit.LinkStatus(new Rect(0f, y, b.width, sh), _link, now, Loc.T("Подключено к мастер-серверу", "Connected to the master server"));
            y += sh + gap;
            Rect r = new Rect(0f, y, b.width, row);
            if (UiKit.Button(UiKit.Col(r, 0, 3), Loc.T("Отвечает", "Answers"), _mode == 0 ? UiButton.Primary : UiButton.Secondary, true,
                             Loc.T("Сервер отвечает через 1,5 с", "The server answers after 1.5 s")))
                Restart(0, now);
            if (UiKit.Button(UiKit.Col(r, 1, 3), Loc.T("Отказ", "Refuses"), _mode == 1 ? UiButton.Primary : UiButton.Secondary, true,
                             Loc.T("Каждая попытка неудачна: задержка растёт 2, 4, 8 ... 30 с",
                                   "Every attempt fails: the delay grows 2, 4, 8 ... 30 s")))
                Restart(1, now);
            if (UiKit.Button(UiKit.Col(r, 2, 3), Loc.T("Молчит", "Silent"), _mode == 2 ? UiButton.Primary : UiButton.Secondary, true,
                             Loc.T("Нет ответа: через 10 с попытка считается неудачной",
                                   "No answer: after 10 s the attempt counts as failed")))
                Restart(2, now);
        }

        static void Restart(int mode, double now)
        {
            _mode = mode;
            _link.Reset();
            _link.Start(now);
            _answerAt = now + 1.5;
        }

        static readonly string[] RowRu = { "Разведчик", "Штурмовик", "Снайпер", "Спецназ", "Медик", "Сапёр" };
        static readonly string[] RowEn = { "Scout", "Assault", "Marksman", "Spetsnaz", "Medic", "Sapper" };

        static void Cards(Rect b, float row, float gap)
        {
            float y = b.y;
            float ch = UiKit.S(76f);
            Rect r = new Rect(0f, y, b.width, ch);
            for (int i = 0; i < 2; i++)
            {
                Rect cr = UiKit.Col(r, i, 2);
                UiKit.Card(cr);
                float isz = UiKit.S(28f);
                UiKit.Circle(new Rect(cr.x + UiKit.S(14f), cr.y + UiKit.S(14f), isz, isz), UiKit.Fade(UiKit.Accent, 0.18f));
                UiKit.Icon(new Rect(cr.x + UiKit.S(19f), cr.y + UiKit.S(19f), isz - UiKit.S(10f), isz - UiKit.S(10f)),
                           i == 0 ? UiIcon.Signal : UiIcon.User, UiKit.Accent);
                float tx = cr.x + isz + UiKit.S(24f);
                UiKit.Label(new Rect(tx, cr.y + UiKit.S(10f), cr.width - tx + cr.x - UiKit.S(10f), UiKit.S(22f)),
                            i == 0 ? Loc.T("Мастер-сервер", "Master server") : Loc.T("Наёмники", "Mercenaries"),
                            UiFont.Heading, UiFont.Left, UiKit.Text);
                UiKit.Label(new Rect(tx, cr.y + UiKit.S(32f), cr.width - tx + cr.x - UiKit.S(10f), UiKit.S(18f)),
                            i == 0 ? Loc.T("Хранит деньги и владение", "Keeps money and ownership")
                                   : Loc.T("Контракты и отряд", "Contracts and squad"),
                            UiFont.Small, UiFont.Left, UiKit.TextDim);
                UiKit.Chip(new Rect(tx, cr.yMax - UiKit.S(24f), UiKit.S(64f), UiKit.S(18f)),
                           i == 0 ? "ONLINE" : "3 / 5", i == 0 ? UiTone.Success : UiTone.Info);
            }
            y += ch + gap * 2f;
            UiKit.Section(new Rect(0f, y, b.width, row * 0.75f), Loc.T("СПИСОК С ПРОКРУТКОЙ", "SCROLL LIST"));
            y += row * 0.75f + gap;
            const int n = 18;
            float rh = UiKit.S(36f);
            Rect view = new Rect(0f, y, b.width, b.yMax - y);
            Rect content = UiKit.BeginScroll(view, _list, n * (rh + UiKit.S(4f)));
            string[] names = Loc.Lang() == 0 ? RowRu : RowEn;
            for (int i = 0; i < n; i++)
            {
                Rect lr = new Rect(0f, i * (rh + UiKit.S(4f)), content.width, rh);
                bool hot = UiKit.Card(lr);
                UiKit.Icon(new Rect(lr.x + UiKit.S(10f), lr.y + (rh - UiKit.S(16f)) * 0.5f, UiKit.S(16f), UiKit.S(16f)),
                           UiIcon.User, hot ? UiKit.Accent : UiKit.TextDim);
                UiKit.Label(new Rect(lr.x + UiKit.S(36f), lr.y, lr.width * 0.5f, rh), names[i % names.Length],
                            UiFont.Body, UiFont.Left, UiKit.Text);
                UiKit.Label(new Rect(lr.x, lr.y, lr.width - UiKit.S(36f), rh), UiNum.Of(100 + i * 25),
                            UiFont.Small, UiFont.Right, UiKit.TextDim);
                UiKit.Icon(new Rect(lr.xMax - UiKit.S(26f), lr.y + (rh - UiKit.S(14f)) * 0.5f, UiKit.S(14f), UiKit.S(14f)),
                           UiIcon.ChevronRight, UiKit.TextDim);
            }
            UiKit.EndScroll();
        }
    }
}

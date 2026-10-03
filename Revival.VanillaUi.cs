// Shared presentation for the a-u1 inventory (a-u3). C# 3.0.
// The native top/interaction singletons also belong to item raycasts,
// parachutes and toxicity. Persistent custom readouts use their artwork in
// IMGUI; discrete notices use the real native queue. No world/gameplay work.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NextDayRevival
{
    internal static class VanillaUi
    {
        internal static readonly Color White = new Color(0.992f, 0.992f, 0.992f, 1f);
        internal static readonly Color Grey = new Color(0.788f, 0.788f, 0.788f, 1f);
        internal static readonly Color Gold = new Color(1f, 0.765f, 0f, 1f);
        internal static readonly Color Hover = new Color(0.882f, 0.784f, 0.588f, 1f);
        internal static readonly Color Pressed = new Color(0.718f, 0.639f, 0.482f, 1f);
        internal static readonly Color Red = new Color(0.992f, 0.282f, 0.282f, 1f);
        internal static readonly Color Green = new Color(0.443f, 0.671f, 0.310f, 1f);

        // InteractItem is not listed: it is the pickup tutorial art with the F
        // key, arrow and gift baked into the texture (c-u1).
        static readonly string[] Names = { "MarkerInfo", "warning_02_empty",
            "Warning_Msg", "Toxic_Msg", "Iventory_Msg", "Group_Msg", "Weapon2_Msg",
            "btn", "btn_hover", "warning_01_form", "close_btn", "progressbar",
            "groupPlayerWhite", "galka_disable", "galka_enable", "scroll_bg", "scroll_thumb" };
        static readonly Texture2D[] Textures = new Texture2D[Names.Length];
        static Font _body, _light, _heading, _cyrillic;
        static float _nextAssets;
        static bool _complete;
        static int _sceneIndex = -1;
        static int _attempts;
        static GUISkin _skin, _previous;
        static GUIStyle _notice, _noticeTitle, _readout;
        static readonly GUIContent Content = new GUIContent();
        static int _generation;
        static bool _warned;

        // Asset discovery is retried at 0.5 Hz until all scene textures exist.
        // Once complete there are no object searches or allocations.
        internal static void Begin()
        {
            FrameProf.S(FrameProf.S_VanillaUi);
            _previous = GUI.skin;
            try
            {
                Resolve();
                if (_skin == null)
                {
                    _skin = UnityEngine.Object.Instantiate(_previous) as GUISkin;
                    _notice = new GUIStyle(_skin.label);
                    _notice.wordWrap = true;
                    _notice.alignment = TextAnchor.MiddleCenter;
                    _notice.fontSize = 18;
                    _noticeTitle = new GUIStyle(_notice);
                    _noticeTitle.fontSize = 30;
                    _noticeTitle.fontStyle = FontStyle.Bold;
                    _readout = new GUIStyle(_skin.label);
                }
                ApplySkin();
                GUI.skin = _skin;
            }
            catch (Exception ex)
            {
                GUI.skin = _previous;
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("Vanilla UI fallback: " + ex.Message); }
            }
            finally { FrameProf.E(FrameProf.S_VanillaUi); }
        }

        internal static void End()
        {
            if (_previous != null) GUI.skin = _previous;
            _previous = null;
        }

        static void Resolve()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (_sceneIndex != scene) { _sceneIndex = scene; _complete = false; _attempts = 0; _nextAssets = 0f; }
            if (_complete || _attempts >= 8 || Time.unscaledTime < _nextAssets) return;
            _nextAssets = Time.unscaledTime + 2f;
            _attempts++;
            UnityEngine.Object[] fonts = Resources.FindObjectsOfTypeAll(typeof(Font));
            for (int i = 0; i < fonts.Length; i++)
            {
                Font f = fonts[i] as Font;
                if (f == null) continue;
                string n = f.name.ToLowerInvariant();
                if (n.StartsWith("roboto-regular_11")) _body = f;
                else if (n == "roboto-light" || n == "roboto-light_11") _light = f;
                else if (n == "bebasneue bold") _heading = f;
                else if (n == "bebas_neue_cyrillic") _cyrillic = f;
            }
            UnityEngine.Object[] textures = Resources.FindObjectsOfTypeAll(typeof(Texture2D));
            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D t = textures[i] as Texture2D;
                if (t == null) continue;
                for (int k = 0; k < Names.Length; k++)
                    if (Textures[k] == null && string.Equals(t.name, Names[k], StringComparison.OrdinalIgnoreCase))
                    { Textures[k] = t; break; }
            }
            _complete = _body != null && _light != null && _heading != null && _cyrillic != null;
            for (int i = 0; i < Textures.Length; i++) if (Textures[i] == null) _complete = false;
            _generation++;
        }

        internal static Font Font(bool heading)
        {
            if (heading) return Loc.Ru && _cyrillic != null ? _cyrillic : _heading != null ? _heading : _body;
            return _body;
        }

        internal static Texture2D Asset(string name)
        {
            for (int i = 0; i < Names.Length; i++) if (Names[i] == name) return Textures[i];
            return null;
        }

        static int _skinGeneration = -1;
        static bool _skinRu;
        static void ApplySkin()
        {
            if (_skinGeneration == _generation && _skinRu == Loc.Ru) return;
            _skinGeneration = _generation; _skinRu = Loc.Ru;
            _skin.font = _body;
            _skin.label.font = _body; _skin.label.fontSize = 16;
            _skin.label.normal.textColor = White;
            ButtonStyle(_skin.button);
            _skin.box.font = Font(true); _skin.box.fontSize = 23;
            Surface(_skin.box, Asset("warning_02_empty"));
            _skin.window.font = Font(true); _skin.window.fontSize = 23;
            Surface(_skin.window, Asset("warning_02_empty"));
            _skin.textField.font = _body; _skin.textArea.font = _body;
            Surface(_skin.textField, Asset("MarkerInfo"));
            Surface(_skin.textArea, Asset("MarkerInfo"));
            _skin.toggle.font = _body; _skin.toggle.fontSize = 16;
            Surface(_skin.toggle, Asset("galka_disable"));
            _skin.toggle.onNormal.background = Asset("galka_enable");
            _skin.toggle.onHover.background = Asset("galka_enable");
            _skin.toggle.onActive.background = Asset("galka_enable");
            _skin.toggle.onFocused.background = Asset("galka_enable");
            _skin.horizontalSlider.normal.background = Asset("progressbar");
            _skin.horizontalSliderThumb.normal.background = Asset("scroll_thumb");
            _skin.verticalScrollbar.normal.background = Asset("scroll_bg");
            _skin.verticalScrollbarThumb.normal.background = Asset("scroll_thumb");
        }

        internal static void ButtonStyle(GUIStyle s)
        {
            s.font = _light != null ? _light : _body;
            s.normal.background = Asset("btn"); s.hover.background = Asset("btn_hover");
            s.active.background = Asset("btn"); s.focused.background = Asset("btn_hover");
            s.normal.textColor = White; s.hover.textColor = White;
            s.active.textColor = White; s.focused.textColor = White;
        }

        static void Surface(GUIStyle s, Texture2D background)
        {
            s.normal.background = background; s.hover.background = background;
            s.active.background = background; s.focused.background = background;
            s.onNormal.background = background; s.onHover.background = background;
            s.onActive.background = background; s.onFocused.background = background;
            s.normal.textColor = White; s.hover.textColor = White;
            s.active.textColor = White; s.focused.textColor = White;
            s.onNormal.textColor = White; s.onHover.textColor = White;
            s.onActive.textColor = White; s.onFocused.textColor = White;
        }

        // Keep semantic alarm red, disabled grey and black label shadows.
        // Other bespoke green/blue/amber text becomes the game's white.
        internal static Color TextColour(Color c)
        {
            Color n = c.r < 0.05f && c.g < 0.05f && c.b < 0.05f ? Color.black
                : c.r > 0.8f && c.g < 0.4f && c.b < 0.4f ? Red
                : Mathf.Abs(c.r - c.g) < 0.12f && Mathf.Abs(c.g - c.b) < 0.12f && c.r < 0.85f ? Grey : White;
            n.a = c.a; return n;
        }

        static void Style(GUIStyle s)
        {
            if (s == null) return;
            Font f = s == _notice ? _light != null ? _light : _body
                : Font(s.fontSize >= 18 || s.fontStyle == FontStyle.Bold);
            if (f != null && s.font != f) s.font = f;
        }

        internal static void Label(Rect r, string text) { Label(r, text, GUI.skin.label); }
        internal static void Label(Rect r, string text, GUIStyle s)
        {
            Content.text = text; Label(r, Content, s);
        }
        internal static void Label(Rect r, GUIContent text) { Label(r, text, GUI.skin.label); }
        internal static void Label(Rect r, GUIContent text, GUIStyle s)
        { Paint(r, text, s, false); }

        static void Paint(Rect r, GUIContent text, GUIStyle s, bool instrument)
        {
            if (instrument) { Font f = Font(true); if (f != null && s.font != f) s.font = f; }
            else Style(s);
            Color old = GUI.color, content = GUI.contentColor, tint = s.normal.textColor;
            GUI.color = TextColour(old); GUI.contentColor = Color.white;
            s.normal.textColor = TextColour(tint);
            GUI.Label(r, text, s);
            s.normal.textColor = tint; GUI.color = old; GUI.contentColor = content;
        }

        internal static void Instrument(Rect r, string text)
        { Instrument(r, text, GUI.skin.label); }
        internal static void Instrument(Rect r, string text, GUIStyle style)
        {
            Content.text = text; Paint(r, Content, style, true);
        }
        internal static void Instrument(Rect r, GUIContent text, GUIStyle style)
        { Paint(r, text, style, true); }

        internal static void Readout(string text, float cx, float y, Color colour, int size)
        {
            if (string.IsNullOrEmpty(text) || _readout == null) return;
            _readout.font = Font(true); _readout.fontSize = size;
            _readout.normal.textColor = TextColour(colour);
            Content.text = text;
            Vector2 measured = _readout.CalcSize(Content);
            Paint(new Rect(cx - measured.x * 0.5f, y, measured.x + 4f, measured.y), Content, _readout, true);
        }

        internal static bool Button(Rect r, string text)
        { return Button(r, text, GUI.skin.button); }
        internal static bool Button(Rect r, string text, GUIStyle style)
        {
            Content.text = text; return Button(r, Content, style);
        }
        internal static bool Button(Rect r, GUIContent text)
        { return Button(r, text, GUI.skin.button); }
        internal static bool Button(Rect r, GUIContent text, GUIStyle style)
        {
            ButtonStyle(style);
            Color old = GUI.backgroundColor;
            bool hot = r.Contains(Event.current.mousePosition);
            GUI.backgroundColor = !GUI.enabled ? Color.grey : hot && Input.GetMouseButton(0) ? Pressed : hot ? Hover : Color.white;
            bool clicked = GUI.Button(r, text, style);
            GUI.backgroundColor = old;
            if (clicked) Sound("Click");
            return clicked;
        }

        // An icon-free dark brush plate: the clean slices of Group_Msg (c-u1).
        internal const string Plate = "Plate";

        internal static void Panel(Rect r, string asset)
        { Panel(r, asset, Color.white); }

        internal static void Panel(Rect r, string asset, Color tint)
        {
            bool plate = asset == Plate;
            Texture2D t = Asset(plate ? "Group_Msg" : asset);
            Color old = GUI.color;
            GUI.color = tint;
            if (t == null) { GUI.color = new Color(0.259f, 0.259f, 0.259f, 0.75f * tint.a); GUI.DrawTexture(r, Texture2D.whiteTexture); }
            else if (plate || asset.EndsWith("_Msg", StringComparison.Ordinal)) Message(r, t, !plate);
            else GUI.DrawTexture(r, t);
            GUI.color = old;
        }

        // The 500 x 100 *_Msg notice plates carry their icon in columns 13-89.
        // Clean slices: edge 0-10, body 100-470, ragged right edge 470-500.
        // The icon square keeps its aspect and only the body stretches, so text
        // laid out from IconInset never lands on the icon.
        const float EdgeU = 10f / 500f, BodyU = 100f / 500f, RightU = 470f / 500f;
        static void Message(Rect r, Texture t, bool icon)
        {
            float right = Mathf.Min(r.height * 0.3f, r.width * 0.15f);
            float left = icon ? IconInset(r) : Mathf.Min(r.height * 0.1f, r.width * 0.1f);
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, left, r.height), t, new Rect(0f, 0f, icon ? BodyU : EdgeU, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.x + left, r.y, r.width - left - right, r.height), t,
                new Rect(BodyU, 0f, RightU - BodyU, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax - right, r.y, right, r.height), t, new Rect(RightU, 0f, 1f - RightU, 1f));
        }

        /// <summary>Width of the icon square on a *_Msg plate drawn into r.</summary>
        internal static float IconInset(Rect r) { return Mathf.Min(r.height, r.width * 0.4f); }

        static float Scale() { return Mathf.Clamp(Screen.height / 720f, 0.65f, 1.5f); }

        // Only chrome-sized dark plates change. Reticles, map ink, radar/video
        // feeds, gauges and fullscreen optic masks keep their functional art.
        internal static void Texture(Rect r, Texture t)
        { Texture(r, t, ScaleMode.StretchToFill, true, 0f); }
        internal static void Texture(Rect r, Texture t, ScaleMode scale)
        { Texture(r, t, scale, true, 0f); }
        internal static void Texture(Rect r, Texture t, ScaleMode scale, bool alpha)
        { Texture(r, t, scale, alpha, 0f); }
        internal static void Texture(Rect r, Texture t, ScaleMode scale, bool alpha, float aspect)
        {
            Color c = GUI.color;
            if (t != null && t.width <= 2 && t.height <= 2 && r.width >= 120f && r.height >= 18f
                && r.width < Screen.width * 0.9f && r.height <= Screen.height * 0.45f
                && c.r < 0.28f && c.g < 0.28f && c.b < 0.28f)
                Panel(r, r.height < 45f ? Plate : r.height < 120f ? "MarkerInfo" : "warning_02_empty");
            else GUI.DrawTexture(r, t, scale, alpha, aspect);
        }

        // Measured prompt plates, keyed by the caller's (cached) string and
        // the font size: CalcSize/CalcHeight run only when the text changes.
        static readonly string[] PromptText = new string[4];
        static readonly Vector2[] PromptSize = new Vector2[4];
        static readonly int[] PromptFont = new int[4];
        static int _promptNext, _promptGeneration = -1;

        static Vector2 PromptMeasure(string text, float k)
        {
            Style(_notice);
            if (_promptGeneration != _generation) { _promptGeneration = _generation; for (int i = 0; i < PromptText.Length; i++) PromptText[i] = null; }
            int font = _notice.fontSize;
            for (int i = 0; i < PromptText.Length; i++)
                if (ReferenceEquals(PromptText[i], text) && PromptFont[i] == font) return PromptSize[i];
            float pad = 22f * k, max = Mathf.Min(Screen.width - 24f, 620f * k);
            Content.text = text;
            _notice.wordWrap = false;
            float w = Mathf.Clamp(_notice.CalcSize(Content).x + 2f * pad + 4f, 260f * k, max);
            _notice.wordWrap = true;
            float h = Mathf.Max(44f * k, _notice.CalcHeight(Content, w - 2f * pad) + 24f * k);
            int slot = _promptNext; _promptNext = (_promptNext + 1) % PromptText.Length;
            PromptText[slot] = text; PromptFont[slot] = font; PromptSize[slot] = new Vector2(w, h);
            return PromptSize[slot];
        }

        /// <summary>An interaction/hint plate centred at the top y. Returns its height.</summary>
        internal static float Prompt(string text, float y)
        { return PromptAt(text, y, false); }

        /// <summary>The same plate ending at bottom (for a hint stacked above another prompt).</summary>
        internal static float PromptAbove(string text, float bottom)
        { return PromptAt(text, bottom, true); }

        static float PromptAt(string text, float y, bool above)
        {
            if (_notice == null || string.IsNullOrEmpty(text)) return 0f;
            float k = Scale();
            _notice.fontSize = Mathf.RoundToInt(16f * k);
            Vector2 size = PromptMeasure(text, k);
            Rect r = new Rect((Screen.width - size.x) * 0.5f, above ? y - size.y : y, size.x, size.y);
            Panel(r, Plate);
            _notice.alignment = TextAnchor.MiddleCenter;
            Label(new Rect(r.x + 22f * k, r.y + 7f * k, r.width - 44f * k, r.height - 14f * k), text, _notice);
            return size.y;
        }

        internal static void InfoLabel(Rect r, string text)
        { InfoLabel(r, text, GUI.skin.label); }

        internal static void InfoLabel(Rect r, string text, GUIStyle style)
        { Content.text = text; InfoLabel(r, Content, style); }

        internal static void InfoLabel(Rect r, GUIContent text, GUIStyle style)
        {
            Panel(new Rect(r.x - 8f, r.y - 4f, r.width + 16f, r.height + 8f), "MarkerInfo");
            Color c = style.normal.textColor;
            style.normal.textColor = style.wordWrap ? White : Gold;
            // Map/info headings use the inventory font, never the optic font.
            style.font = Font(false);
            Color old = GUI.color; GUI.color = Color.white;
            GUI.Label(r, text, style);
            GUI.color = old; style.normal.textColor = c;
        }

        /// <summary>A native-looking notice: type icon left, text right of it.</summary>
        internal static void Notice(string text, int type)
        {
            if (_notice == null || string.IsNullOrEmpty(text)) return;
            float k = Scale();
            float w = Mathf.Min(Screen.width - 24f, 520f * k);
            _notice.fontSize = Mathf.RoundToInt(18f * k);
            _notice.alignment = TextAnchor.MiddleLeft;
            Style(_notice);
            Content.text = text;
            // Two passes: the icon square grows with the plate's height.
            float h = 76f * k;
            for (int pass = 0; pass < 2; pass++)
                h = Mathf.Clamp(_notice.CalcHeight(Content, w - h - 26f * k) + 24f * k, 76f * k, 124f * k);
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.12f, w, h);
            Panel(r, type == NativeMessage.Kill ? "Weapon2_Msg" : type == NativeMessage.Group ? "Group_Msg"
                : type == NativeMessage.Inventory ? "Iventory_Msg" : "Warning_Msg");
            float x = r.x + IconInset(r) + 4f * k;
            Label(new Rect(x, r.y + 8f * k, r.xMax - x - 22f * k, r.height - 16f * k), text, _notice);
            _notice.alignment = TextAnchor.MiddleCenter;
        }

        internal static float Banner(string head, string line, float y, bool toxic)
        { return Banner(head, line, y, toxic, Color.white); }

        /// <summary>Heading (+ optional line) on a warning/toxic plate, text right
        /// of the icon. Returns the plate height so callers can stack below it.</summary>
        internal static float Banner(string head, string line, float y, bool toxic, Color tint)
        {
            if (_notice == null) return 0f;
            float k = Scale();
            float w = Mathf.Min(Screen.width - 24f, 640f * k);
            bool two = !string.IsNullOrEmpty(line);
            _noticeTitle.fontSize = Mathf.RoundToInt(26f * k);
            _noticeTitle.alignment = TextAnchor.MiddleLeft;
            _notice.fontSize = Mathf.RoundToInt(17f * k);
            _notice.alignment = TextAnchor.UpperLeft;
            Style(_notice); Style(_noticeTitle);
            float h = (two ? 104f : 84f) * k, headH = 0f, lineH = 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                float tw = w - h - 26f * k;
                Content.text = head; headH = _noticeTitle.CalcHeight(Content, tw);
                lineH = 0f;
                if (two) { Content.text = line; lineH = _notice.CalcHeight(Content, tw); }
                h = Mathf.Clamp(headH + lineH + (two ? 30f : 24f) * k, (two ? 96f : 76f) * k, 150f * k);
            }
            Rect r = new Rect((Screen.width - w) * 0.5f, y, w, h);
            Panel(r, toxic ? "Toxic_Msg" : "Warning_Msg", tint);
            float x = r.x + IconInset(r) + 4f * k, tw2 = r.xMax - x - 22f * k;
            float top = r.y + Mathf.Max(8f * k, (h - headH - lineH - (two ? 6f * k : 0f)) * 0.5f);
            Label(new Rect(x, top, tw2, headH), head, _noticeTitle);
            if (two) Label(new Rect(x, top + headH + 6f * k, tw2, Mathf.Max(0f, Mathf.Min(lineH, r.yMax - 6f * k - top - headH - 6f * k))), line, _notice);
            _notice.alignment = TextAnchor.MiddleCenter;
            _noticeTitle.alignment = TextAnchor.MiddleCenter;
            return h;
        }

        internal static bool LayoutButton(string text, params GUILayoutOption[] options)
        {
            bool hit = GUILayout.Button(text, options);
            if (hit) Sound("Click");
            return hit;
        }

        static MethodInfo _sound, _instance;
        static readonly object[] SoundArgs = new object[1];
        internal static void Sound(string state)
        {
            try
            {
                if (_instance == null)
                {
                    Type ui = RevivalPlugin.TypeByName("UIController");
                    if (ui == null) return;
                    _instance = HarmonyLib.AccessTools.PropertyGetter(ui, "Instance");
                    _sound = ui.GetMethod("PlayUISound", new Type[] { typeof(string) });
                }
                object host = _instance == null ? null : _instance.Invoke(null, null);
                if (host == null || _sound == null) return;
                SoundArgs[0] = state; _sound.Invoke(host, SoundArgs); SoundArgs[0] = null;
            }
            catch { /* Event-only sound must never stop a player action. */ }
        }
    }

    internal static class VanillaNotice
    {
        sealed class Slot { internal string Text, Line, Combined; internal float Until; internal bool Sent; internal int Type; }
        static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>();

        // Called by an existing feature Draw, at its existing profiler slot.
        // No re-posting each repaint, no concatenation of unchanged lines.
        internal static bool Banner(string owner, string text, string line, int type, float until)
        {
            Slot s;
            if (!Slots.TryGetValue(owner, out s)) { s = new Slot(); Slots.Add(owner, s); }
            if (s.Text != text || s.Line != line || s.Type != type || Time.time > s.Until)
            {
                s.Text = text; s.Line = line; s.Type = type;
                s.Combined = string.IsNullOrEmpty(line) ? text : text + "\n" + line;
                s.Sent = false;
            }
            s.Until = until;
            // Native suppresses notices during these full-screen states. Retain
            // the feature's deadline and try on return to play, without flooding.
            if (GameUi.State == 2 || GameUi.State == 3) return true;
            if (!s.Sent) s.Sent = NativeMessage.Show(s.Combined, type);
            return s.Sent;
        }
    }
}

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

        // Vanilla geometry, read from level7 (research/vanilla_ui_style.py and
        // the transform chain): UIRoot is 720 units high, so one unit is
        // Screen.height / 720 pixels at any resolution.
        //   HUD_MessageUI/Msg_HUD (scale 0.8): Msg_Form 385 x 70 -> 308 x 56,
        //     anchored bottom-left, centre 169.7 / 34 units from that corner;
        //     Label_Msg 288 x 52 -> 230 x 42, 29 units right of centre, ROBOTO-
        //     LIGHT 18 -> 14.4. HUD_MessageUI shows each line 3 s.
        //   HUD_TopMessage/ElementsUI (scale 0.61): Form 499 x 100 -> 304 x 61,
        //     top centre, centre 62.4 units below the top; MessageLabel 379 x 68
        //     at +39.8, Bebas 44; AdditionalMessageLabel 22 on a 461 x 30
        //     Empty_Msg strip 68.5 below the form's centre.
        static float Unit() { return Screen.height / 720f; }

        /// <summary>Top of a vanilla top-message plate (the toxicity/raid form).</summary>
        internal static float TopMessageY { get { return (62.4f - 30.5f) * Unit(); } }

        // Shrink-to-fit like NGUI's ShrinkContent, cached by the caller's
        // (cached) string so CalcSize/CalcHeight run only when the text changes.
        static readonly string[] FitText = new string[6];
        static readonly int[] FitBase = new int[6], FitSize = new int[6];
        static readonly float[] FitWidth = new float[6];
        static int _fitNext, _fitGeneration = -1;

        static int Fit(GUIStyle s, string text, int size, float w, float h, bool wrap)
        {
            if (_fitGeneration != _generation) { _fitGeneration = _generation; for (int i = 0; i < FitText.Length; i++) FitText[i] = null; }
            for (int i = 0; i < FitText.Length; i++)
                if (ReferenceEquals(FitText[i], text) && FitBase[i] == size && FitWidth[i] == w) return FitSize[i];
            Content.text = text;
            bool oldWrap = s.wordWrap;
            s.wordWrap = wrap;
            int min = Mathf.RoundToInt(size * 0.6f), fit = size;
            if (min < 8) min = 8;
            for (; fit > min; fit--)
            {
                s.fontSize = fit;
                if (wrap ? s.CalcHeight(Content, w) <= h : s.CalcSize(Content).x <= w) break;
            }
            s.wordWrap = oldWrap;
            int slot = _fitNext; _fitNext = (_fitNext + 1) % FitText.Length;
            FitText[slot] = text; FitBase[slot] = size; FitWidth[slot] = w; FitSize[slot] = fit;
            return fit;
        }

        /// <summary>A native-looking notice: type icon left, text right of it.</summary>
        internal static void Notice(string text, int type)
        { Notice(text, type, 1f); }

        /// <summary>The HUD message line where and how big the game draws it:
        /// bottom-left, 308 x 56 units, ROBOTO-LIGHT 14.4 units, shrunk to fit.
        /// alpha fades it in and out (the caller owns the 3-5 s lifetime).</summary>
        internal static void Notice(string text, int type, float alpha)
        {
            if (_notice == null || string.IsNullOrEmpty(text) || alpha <= 0f) return;
            float u = Unit();
            Rect r = new Rect(15.7f * u, Screen.height - 62f * u, 308f * u, 56f * u);
            Style(_notice);
            _notice.alignment = TextAnchor.MiddleCenter;
            Rect t = new Rect(r.x + 62f * u, r.y + 7f * u, 230.4f * u, 41.6f * u);
            _notice.fontSize = Fit(_notice, text, Mathf.RoundToInt(14.4f * u), t.width, t.height, true);
            Color old = GUI.color;
            Panel(r, type == NativeMessage.Kill ? "Weapon2_Msg" : type == NativeMessage.Group ? "Group_Msg"
                : type == NativeMessage.Inventory ? "Iventory_Msg" : "Warning_Msg", new Color(1f, 1f, 1f, alpha));
            GUI.color = new Color(old.r, old.g, old.b, old.a * alpha);
            Label(t, text, _notice);
            GUI.color = old;
        }

        internal static float Banner(string head, string line, float y, bool toxic)
        { return Banner(head, line, y, toxic, Color.white); }

        /// <summary>The game's top message (HUD_TopMessage): a 304 x 61 unit
        /// warning/toxic form, top centre, Bebas heading right of the icon and
        /// the optional second line on a thin strip under it. y is the form's
        /// top (TopMessageY for the vanilla place). Returns the total height.</summary>
        internal static float Banner(string head, string line, float y, bool toxic, Color tint)
        {
            if (_notice == null || string.IsNullOrEmpty(head)) return 0f;
            float u = Unit();
            Rect r = new Rect((Screen.width - 304.4f * u) * 0.5f, y, 304.4f * u, 61f * u);
            Panel(r, toxic ? "Toxic_Msg" : "Warning_Msg", tint);
            Style(_noticeTitle);
            _noticeTitle.alignment = TextAnchor.MiddleCenter;
            float cx = r.x + r.width * 0.5f, cy = r.y + r.height * 0.5f;
            Rect t = new Rect(cx + 24.3f * u - 115.6f * u, r.y + 9.8f * u, 231.2f * u, 41.5f * u);
            _noticeTitle.fontSize = Fit(_noticeTitle, head, Mathf.RoundToInt(26.8f * u), t.width, t.height, false);
            Label(t, head, _noticeTitle);
            _noticeTitle.alignment = TextAnchor.MiddleCenter;
            if (string.IsNullOrEmpty(line)) return r.height;
            Rect strip = new Rect(cx - 140.6f * u, cy + 41.8f * u - 9.15f * u, 281.2f * u, 18.3f * u);
            Panel(strip, Plate, new Color(1f, 1f, 1f, tint.a));
            Style(_notice);
            _notice.alignment = TextAnchor.MiddleCenter;
            Rect lt = new Rect(strip.x + 12.8f * u, strip.y, 255.6f * u, strip.height);
            _notice.fontSize = Fit(_notice, line, Mathf.RoundToInt(13.4f * u), lt.width, lt.height, false);
            Label(lt, line, _notice);
            return strip.yMax - y;
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

    /// <summary>One event = one short notice. The game's own HUD message line
    /// shows it (bottom-left, 3 s, then the next queued line); without it the
    /// same plate is drawn here for Seconds with a fade. A countdown never
    /// keeps a notice up: callers re-post only at Milestone changes.</summary>
    internal static class VanillaNotice
    {
        /// <summary>Lifetime of the fallback plate and of one posting window.</summary>
        internal const float Seconds = 5f;
        const float FadeIn = 0.15f, FadeOut = 0.6f;

        sealed class Slot { internal string Text, Line, Combined; internal float Until, Since; internal bool Sent, Native; internal int Type; }
        static readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>();

        /// <summary>Countdown stage: 0 above 60 s, 1 from 60 s, 2 from 30 s.
        /// Re-post a countdown notice only when this grows.</summary>
        internal static int Milestone(float secondsLeft)
        {
            return secondsLeft > 60f ? 0 : secondsLeft > 30f ? 1 : 2;
        }

        /// <summary>0..1 opacity of a fallback plate shown age seconds ago.</summary>
        internal static float Fade(float age) { return Fade(age, Seconds); }

        /// <summary>The same for a plate with its own (shorter) lifetime.</summary>
        internal static float Fade(float age, float life)
        {
            if (age < 0f || age >= life) return 0f;
            return Mathf.Min(1f, Mathf.Min(age / FadeIn, (life - age) / FadeOut));
        }

        // Called by an existing feature Draw, at its existing profiler slot,
        // while its own deadline runs. Posts once per text; no re-posting each
        // repaint, no concatenation of unchanged lines.
        internal static void Banner(string owner, string text, string line, int type, float until)
        {
            if (string.IsNullOrEmpty(text)) return;
            Slot s;
            if (!Slots.TryGetValue(owner, out s)) { s = new Slot(); Slots.Add(owner, s); }
            if (s.Text != text || s.Line != line || s.Type != type || Time.time > s.Until)
            {
                s.Text = text; s.Line = line; s.Type = type;
                s.Combined = string.IsNullOrEmpty(line) ? text : text + "\n" + line;
                s.Sent = false; s.Native = false;
            }
            s.Until = until;
            // Native suppresses notices during these full-screen states. Retain
            // the feature's deadline and try on return to play, without flooding.
            if (GameUi.State == 2 || GameUi.State == 3) return;
            if (!s.Sent)
            {
                s.Sent = true;
                s.Since = Time.time;
                s.Native = NativeMessage.Show(s.Combined, type);
            }
            if (s.Native) return;
            Event e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            VanillaUi.Notice(s.Combined, type, Fade(Time.time - s.Since));
        }
    }
}

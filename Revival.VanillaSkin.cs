// Next Day: Survival - Revival Toolkit
//
// VanillaSkin - IMGUI drawn with the game's OWN buy-menu look (task a-u2):
// the textures and fonts are taken from the live HUD_Marketplace window and
// the market row prefab once, so a mod window drawn with it looks like the
// game's buy menu instead of a mod panel. Reference sheet:
// docs/ai/tasks/a-ui-inventory.md section 2 (research/vanilla_ui_style.py).
//
//   Form      MarketplaceFormUI 922 x 510 (the buy menu frame), mapped onto
//             the screen rect of the game's own form; Px(x, y, w, h) turns
//             form units (top-left origin) into screen pixels.
//   Text      Roboto-Regular_11 (body, names, prices), ROBOTO-LIGHT (dialog
//             and buttons), BebasNeue Bold (big buttons, tabs); white text,
//             gold #FFC300 for names in info boxes, greys for secondary lines.
//   Buttons   btn / btn_hover with the UIButton tint (hover #E1C896, pressed
//             #B7A37B, disabled #808080), the buy button buyBtn / buyBtn_Hover
//             (86 %) with the StoreBuyCut icon, close_btn.
//   Rows      MarketItemUI2_2_2 421 x 58 with the hover overlay at 61 %.
//   Dialog    AnswerPopUpUI: bg_white dimmer #444444 at 72 %, warning_01_form,
//             ROBOTO-LIGHT text, Yes / No buttons.
//   Sounds    UIController.PlayUISound("Click" / "BuyItem" / "OpenWindow").
//
// Anything not found (a renamed asset, the window not built yet) falls back
// to a flat colour or Unity's font, never to an exception.
//
// Performance: Resolve runs once per session (retried at most every 2 s
// until the game window exists); every draw is GUI.DrawTexture / GUI.Label
// with cached styles - no allocation in a steady repaint. Sounds are one
// reflection call per click (events only).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression bodies.
// UTF-8 (no BOM), compiled with /codepage:65001 like the rest.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class VanillaSkin
    {
        // ------------------------------------------------------------ fonts

        internal const int Regular = 0;   // Roboto-Regular_11
        internal const int Light = 1;     // ROBOTO-LIGHT
        internal const int Bebas = 2;     // BebasNeue Bold (Bebas_Neue_Cyrillic for Russian)
        const int FontCount = 3;

        internal const int Left = 0, Center = 1, Right = 2;

        // ------------------------------------------------------------ colours (reference sheet 2.1)

        internal static readonly Color White = new Color(0.992f, 0.992f, 0.992f, 1f);       // #FDFDFD
        internal static readonly Color Gold = new Color(1f, 0.765f, 0f, 1f);                // #FFC300
        internal static readonly Color Grey = new Color(0.788f, 0.788f, 0.788f, 1f);        // #C9C9C9
        internal static readonly Color GreyDark = new Color(0.502f, 0.502f, 0.502f, 1f);    // #808080 (disabled)
        internal static readonly Color Hover = new Color(0.882f, 0.784f, 0.588f, 1f);       // #E1C896
        internal static readonly Color Pressed = new Color(0.718f, 0.639f, 0.482f, 1f);     // #B7A37B
        internal static readonly Color Green = new Color(0.443f, 0.671f, 0.310f, 1f);       // #71AB4F (progress fill)
        internal static readonly Color Red = new Color(0.992f, 0.282f, 0.282f, 1f);         // #FD4848 (vanilla BBCode red)
        internal static readonly Color BarBack = new Color(0.259f, 0.259f, 0.259f, 1f);     // #424242
        static readonly Color Dimmer = new Color(0.267f, 0.267f, 0.267f, 0.72f);            // #444444 at 72 %
        static readonly Color Backing = new Color(0f, 0f, 0f, 1f);
        static readonly Color RowHoverTint = new Color(1f, 1f, 1f, 0.61f);
        static readonly Color BuyHoverTint = new Color(1f, 1f, 1f, 0.86f);
        static readonly Color ThumbTint = new Color(1f, 1f, 1f, 0.79f);

        // ------------------------------------------------------------ assets

        const int TForm = 0, TBuy = 1, TBuyHover = 2, TBuyIcon = 3, TClose = 4, TBtn = 5, TBtnHover = 6,
            TDialog = 7, TDim = 8, TRow = 9, TRowHover = 10, TScrollBg = 11, TThumb = 12, TexCount = 13;
        static readonly string[] TexNames =
        {
            "MarketplaceFormUI", "buyBtn", "buyBtn_Hover", "StoreBuyCut", "close_btn", "btn", "btn_hover",
            "warning_01_form", "bg_white", "MarketItemUI2_2_2", "MarketItemUI2_Hover2_1", "scroll_bg", "scroll_thumb"
        };
        static readonly Texture[] _tex = new Texture[TexCount];
        static readonly Font[] _fonts = new Font[FontCount];
        static Font _bebasRu;
        static bool _resolved;
        static Transform _formWidget;
        static float _nextResolve;
        static Texture2D _white;
        static readonly GUIStyle[] _styles = new GUIStyle[FontCount * 3];
        static GUIStyle _wrap;
        static readonly GUIContent _measure = new GUIContent();
        // Font metrics are measured on changed content/geometry only. Repaint
        // uses the cached size; Font is part of the key for late-loaded assets.
        struct FitKey : IEquatable<FitKey>
        {
            internal string Text;
            internal Font Face;
            internal int Size, Width, Height;
            internal bool Wrap;
            public bool Equals(FitKey b)
            { return Text == b.Text && Face == b.Face && Size == b.Size && Width == b.Width && Height == b.Height && Wrap == b.Wrap; }
            public override bool Equals(object b) { return b is FitKey && Equals((FitKey)b); }
            public override int GetHashCode()
            {
                unchecked { return (((((Text.GetHashCode() * 31 + (Face == null ? 0 : Face.GetInstanceID())) * 31 + Size) * 31 + Width) * 31 + Height) * 2) + (Wrap ? 1 : 0); }
            }
        }
        static readonly Dictionary<FitKey, int> _fit = new Dictionary<FitKey, int>();

        /// <summary>The game's UIController (set by the host window, used for sounds).</summary>
        internal static object Ui;
        static MethodInfo _mSound;
        static readonly object[] _soundArg = new object[1];

        /// <summary>True once the game's own textures were found (otherwise
        /// the flat fallback draws).</summary>
        internal static bool HasTextures { get { return _tex[TForm] != null; } }

        /// <summary>Once per session: textures from the live buy-menu window
        /// (root = the HUD_Marketplace object) and the market row prefab,
        /// fonts by name. Retried at most every 2 s until the window exists.</summary>
        internal static void Resolve(GameObject root)
        {
            Styles();
            if (_resolved || root == null || Time.unscaledTime < _nextResolve) return;
            _nextResolve = Time.unscaledTime + 2f;
            try
            {
                Type tTex = RevivalPlugin.TypeByName("UITexture");
                PropertyInfo main = tTex == null ? null : AccessTools.Property(tTex, "mainTexture");
                if (tTex == null || main == null) { _resolved = true; return; }
                Collect(root, tTex, main);
                GameObject row = Resources.Load("gui/hud_marketplace/marketitemprefab") as GameObject;
                if (row != null) Collect(row, tTex, main);
                UnityEngine.Object[] fonts = Resources.FindObjectsOfTypeAll(typeof(Font));
                for (int i = 0; i < fonts.Length; i++)
                {
                    Font f = fonts[i] as Font;
                    if (f == null) continue;
                    string n = f.name.ToLowerInvariant();
                    if (_fonts[Regular] == null && n.StartsWith("roboto-regular_11")) _fonts[Regular] = f;
                    else if (_fonts[Light] == null && n == "roboto-light") _fonts[Light] = f;
                    else if (_fonts[Bebas] == null && n == "bebasneue bold") _fonts[Bebas] = f;
                    else if (_bebasRu == null && n == "bebas_neue_cyrillic") _bebasRu = f;
                }
                if (_fonts[Light] == null) _fonts[Light] = _fonts[Regular];
                for (int i = 0; i < _styles.Length; i++) _styles[i].font = _fonts[i / 3];
                _wrap.font = _fonts[Regular];
                _resolved = true;
                int found = 0;
                for (int i = 0; i < TexCount; i++) if (_tex[i] != null) found++;
                RevivalPlugin.L.LogInfo("VanillaSkin: " + found + "/" + TexCount + " buy-menu textures, fonts "
                    + (_fonts[Regular] != null ? "Roboto " : "") + (_fonts[Bebas] != null ? "Bebas " : "")
                    + (_bebasRu != null ? "BebasCyr" : ""));
            }
            catch (Exception ex)
            {
                _resolved = true;
                RevivalPlugin.L.LogWarning("VanillaSkin: assets not read, flat fallback: " + ex.Message);
            }
        }

        static void Collect(GameObject root, Type tTex, PropertyInfo main)
        {
            Component[] all = root.GetComponentsInChildren(tTex, true);
            for (int i = 0; i < all.Length; i++)
            {
                Texture t = main.GetValue(all[i], null) as Texture;
                if (t == null) continue;
                for (int k = 0; k < TexCount; k++)
                    if (_tex[k] == null && t.name == TexNames[k])
                    {
                        _tex[k] = t;
                        if (k == TForm) _formWidget = all[i].transform;
                        break;
                    }
            }
        }

        static void Styles()
        {
            if (_white != null) return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            for (int i = 0; i < _styles.Length; i++)
            {
                GUIStyle s = new GUIStyle();
                s.wordWrap = false;
                s.clipping = TextClipping.Clip;
                s.richText = false;
                int a = i % 3;
                s.alignment = a == Left ? TextAnchor.MiddleLeft : a == Center ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight;
                s.normal.textColor = Color.white;
                _styles[i] = s;
            }
            _wrap = new GUIStyle();
            _wrap.wordWrap = true;
            _wrap.clipping = TextClipping.Clip;
            _wrap.alignment = TextAnchor.UpperLeft;
            _wrap.normal.textColor = Color.white;
        }

        // ------------------------------------------------------------ form mapping

        static Rect _form;
        static float _k = 1f;

        /// <summary>The form's screen rect; Px maps 922 x 510 form units into it.</summary>
        internal static void Begin(Rect form)
        {
            Styles();
            _form = form;
            _k = form.width / 922f;
        }

        /// <summary>Screen pixels per form unit (UIRoot units).</summary>
        internal static float K { get { return _k; } }

        // The reference sheet measures these widgets with a centre pivot.
        // Transforming their corners avoids NGUI bounds/reflection allocations.
        internal static bool WidgetRect(Transform widget, Camera camera, float width, float height, out Rect rect)
        {
            rect = new Rect();
            if (widget == null || camera == null) return false;
            Vector3 a = camera.WorldToScreenPoint(widget.TransformPoint(new Vector3(-width * 0.5f, -height * 0.5f, 0f)));
            Vector3 b = camera.WorldToScreenPoint(widget.TransformPoint(new Vector3(width * 0.5f, height * 0.5f, 0f)));
            rect = new Rect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            return rect.width > 1f && rect.height > 1f;
        }

        internal static bool FormRect(Camera camera, out Rect rect)
        {
            return WidgetRect(_formWidget, camera, 922f, 510f, out rect);
        }

        internal static Rect Px(float x, float y, float w, float h)
        {
            return new Rect(_form.x + x * _k, _form.y + y * _k, w * _k, h * _k);
        }

        // ------------------------------------------------------------ drawing

        static void Tex(Rect r, Texture t, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, t != null ? t : _white, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        internal static void Fill(Rect r, Color c) { Tex(r, _white, c); }

        /// <summary>The buy-menu frame with a dark backing, so whatever the
        /// game draws under it does not show through its translucent panels.</summary>
        internal static void Frame(Rect r)
        {
            Fill(r, Backing);
            if (_tex[TForm] != null) Tex(r, _tex[TForm], Color.white);
            else Fill(r, new Color(0.08f, 0.07f, 0.07f, 0.95f));
        }

        /// <summary>One line of text; size in form units (vanilla font sizes).</summary>
        internal static void Text(Rect r, string text, int font, float size, int align, Color c)
        {
            if (text == null) return;
            GUIStyle s = _styles[Font(font) * 3 + align];
            s.font = font == Bebas && _bebasRu != null && Loc.Ru ? _bebasRu : _fonts[Font(font)];
            Fit(r, text, s, size);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }

        /// <summary>Wrapped text from the top-left of r (description panel).</summary>
        internal static void Paragraph(Rect r, string text, float size, Color c)
        {
            if (text == null) return;
            _wrap.font = _fonts[Regular];
            Fit(r, text, _wrap, size);
            _wrap.normal.textColor = c;
            GUI.Label(r, text, _wrap);
        }

        static int Font(int f) { return f < 0 || f >= FontCount ? Regular : f; }
        internal static Font BodyFont { get { return _fonts[Regular]; } }

        static void Fit(Rect r, string text, GUIStyle style, float size)
        {
            int wanted = Mathf.Max(8, Mathf.RoundToInt(size * _k));
            FitKey key = new FitKey { Text = text, Face = style.font, Size = wanted,
                Width = (int)r.width, Height = (int)r.height, Wrap = style.wordWrap };
            int fitted;
            if (!_fit.TryGetValue(key, out fitted))
            {
                _measure.text = text;
                fitted = wanted;
                for (; fitted > 8; fitted--)
                {
                    style.fontSize = fitted;
                    Vector2 bounds = style.CalcSize(_measure);
                    float height = style.wordWrap ? style.CalcHeight(_measure, r.width) : bounds.y;
                    if ((style.wordWrap || bounds.x <= r.width - 2f) && height <= r.height) break;
                }
                if (_fit.Count >= 2048) _fit.Clear();
                _fit[key] = fitted;
            }
            style.fontSize = fitted;
        }

        internal static void Bar(Rect r, float v, Color fill)
        {
            Fill(r, BarBack);
            Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), fill);
        }

        // ------------------------------------------------------------ input

        const int Normal = 0, Over = 1, Down = 2, Off = 3;

        /// <summary>A hand-rolled click target: pressed state while held,
        /// fires on release over it. No allocation.</summary>
        static bool Hit(Rect r, bool enabled, out int state)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            bool over = r.Contains(e.mousePosition);
            bool hit = false;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (enabled && over && e.button == 0 && GUI.enabled) { GUIUtility.hotControl = id; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); hit = over && enabled && GUI.enabled; }
                    break;
            }
            state = !enabled || !GUI.enabled ? Off : GUIUtility.hotControl == id ? Down : over ? Over : Normal;
            return hit;
        }

        static Color Tint(int state)
        {
            return state == Off ? GreyDark : state == Down ? Pressed : state == Over ? Hover : White;
        }

        /// <summary>Native btn art and texture tint; the caption stays white.</summary>
        internal static bool Button(Rect r, string text, float size, bool enabled)
        {
            int st;
            bool hit = Hit(r, enabled, out st);
            if (Event.current.type == EventType.Repaint)
            {
                Texture t = st == Over || st == Down ? (_tex[TBtnHover] != null ? _tex[TBtnHover] : _tex[TBtn]) : _tex[TBtn];
                // Native brush caps keep their aspect; only the icon-free
                // centre extends horizontally. Dark underpaint bounds contrast.
                Fill(r, new Color(0.035f, 0.035f, 0.035f, 1f));
                if (t != null)
                {
                    Color old = GUI.color;
                    Color tint = Tint(st);
                    GUI.color = st == Off ? new Color(0.55f, 0.55f, 0.55f, 0.55f)
                        : new Color(tint.r * 0.7f, tint.g * 0.7f, tint.b * 0.7f, 1f);
                    float cap = Mathf.Min(r.width * 0.2f, r.height * 230f * 0.08f / 60f);
                    GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, cap, r.height), t, new Rect(0f, 0f, 0.08f, 1f));
                    GUI.DrawTextureWithTexCoords(new Rect(r.x + cap, r.y, r.width - cap * 2f, r.height), t, new Rect(0.08f, 0f, 0.84f, 1f));
                    GUI.DrawTextureWithTexCoords(new Rect(r.xMax - cap, r.y, cap, r.height), t, new Rect(0.92f, 0f, 0.08f, 1f));
                    GUI.color = old;
                }
                Text(new Rect(r.x + 8f * _k, r.y, r.width - 16f * _k, r.height), text, Light, size, Center, st == Off ? GreyDark : White);
            }
            if (hit) Click();
            return hit;
        }

        /// <summary>The buy button (buyBtn, hover overlay 86 %, StoreBuyCut icon, Bebas caption).</summary>
        internal static bool BuyButton(Rect r, string caption, bool enabled)
        {
            int st;
            bool hit = Hit(r, enabled, out st);
            if (Event.current.type == EventType.Repaint)
            {
                Fill(r, new Color(0.035f, 0.035f, 0.035f, 1f));
                Color base_ = st == Off ? GreyDark : Color.white;
                if (_tex[TBuy] != null) BuyPaint(r, _tex[TBuy], base_);
                else Fill(r, st == Off ? new Color(0.25f, 0.25f, 0.25f, 0.9f) : new Color(0.55f, 0.08f, 0.06f, 0.95f));
                if ((st == Over || st == Down) && _tex[TBuyHover] != null) BuyPaint(r, _tex[TBuyHover], st == Down ? Pressed : BuyHoverTint);
                float k = r.width / 160f;
                if (_tex[TBuyIcon] != null)
                    Tex(new Rect(r.x + 5f * k, r.y + 4f * k, 32f * k, 32f * k), _tex[TBuyIcon], base_);
                Text(new Rect(r.x + 44f * k, r.y, r.width - 50f * k, r.height), caption, Bebas, 26f, Center,
                    st == Off ? GreyDark : White);
            }
            if (hit) Click();
            return hit;
        }

        /// <summary>A Storage/Market style switch button (same buyBtn frame,
        /// Bebas 22 caption): the trader's Mercenaries tab.</summary>
        internal static bool TabButton(Rect r, string caption, bool active)
        {
            int st;
            bool hit = Hit(r, true, out st);
            if (Event.current.type == EventType.Repaint)
            {
                // This switch sits OUTSIDE the dark market form, over scenery.
                // Translucent brush holes must not expose white sky under text.
                Fill(r, new Color(0.035f, 0.035f, 0.035f, 1f));
                if (_tex[TBuy] != null) BuyPaint(r, _tex[TBuy], new Color(0.42f, 0.42f, 0.42f, 1f));
                else Fill(r, new Color(0.55f, 0.08f, 0.06f, 0.95f));
                if ((active || st == Over || st == Down) && _tex[TBuyHover] != null)
                    BuyPaint(r, _tex[TBuyHover], st == Down ? new Color(Pressed.r * 0.42f, Pressed.g * 0.42f, Pressed.b * 0.42f, 1f)
                        : new Color(0.42f, 0.42f, 0.42f, BuyHoverTint.a));
                Text(new Rect(r.x + 8f * _k, r.y, r.width - 16f * _k, r.height), caption, Bebas, 22f, Center, White);
            }
            if (hit) Click();
            return hit;
        }

        static void BuyPaint(Rect r, Texture texture, Color tint)
        {
            // Both 159x39 buy textures bake a shopping bag into columns 14-38.
            // Stretch only the clean centre; keep native end-cap proportions.
            // Hire draws its one StoreBuyCut explicitly; the merc tab has none.
            Color old = GUI.color;
            GUI.color = tint;
            float cap = Mathf.Min(r.width * 0.2f, r.height * 8f / 39f);
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, cap, r.height), texture, new Rect(0f, 0f, 8f / 159f, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.x + cap, r.y, r.width - cap * 2f, r.height), texture, new Rect(40f / 159f, 0f, 111f / 159f, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax - cap, r.y, cap, r.height), texture, new Rect(151f / 159f, 0f, 8f / 159f, 1f));
            GUI.color = old;
        }

        /// <summary>A text tab in the category tab row: white when picked,
        /// grey otherwise, the button tint on hover.</summary>
        internal static bool TextTab(Rect r, string caption, bool picked, bool enabled)
        {
            int st;
            bool hit = Hit(r, enabled, out st);
            if (Event.current.type == EventType.Repaint)
            {
                Color c = st == Off ? GreyDark : st == Down ? Pressed : st == Over ? Hover : picked ? White : GreyDark;
                Text(r, caption, Bebas, 20f, Center, c);
                if (picked) Fill(new Rect(r.x + r.width * 0.15f, r.yMax - 2f * _k, r.width * 0.7f, Mathf.Max(1f, 2f * _k)), Gold);
            }
            if (hit) Click();
            return hit;
        }

        /// <summary>The close cross (close_btn with the tint).</summary>
        internal static bool Close(Rect r)
        {
            int st;
            bool hit = Hit(r, true, out st);
            if (Event.current.type == EventType.Repaint)
            {
                if (_tex[TClose] != null) Tex(r, _tex[TClose], Tint(st));
                else Text(r, "x", Regular, 20f, Center, Tint(st));
            }
            if (hit) Click();
            return hit;
        }

        /// <summary>A market list row (MarketItemUI2_2_2) with the hover
        /// overlay while hovered or picked. True on click.</summary>
        internal static bool Row(Rect r, bool picked) { return Row(r, picked, true); }

        /// <summary>currency=false drops the row art's ruble mark (columns
        /// 379-406 of 421) for rows that show no price (c-u1).</summary>
        internal static bool Row(Rect r, bool picked, bool currency)
        {
            int st;
            bool hit = Hit(r, true, out st);
            if (Event.current.type == EventType.Repaint)
            {
                if (_tex[TRow] != null && !currency)
                {
                    float cap = Mathf.Min(r.width * 0.1f, r.height * 13f / 58f);
                    GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, r.width - cap, r.height), _tex[TRow], new Rect(0f, 0f, 370f / 421f, 1f));
                    GUI.DrawTextureWithTexCoords(new Rect(r.xMax - cap, r.y, cap, r.height), _tex[TRow], new Rect(408f / 421f, 0f, 13f / 421f, 1f));
                }
                else if (_tex[TRow] != null) Tex(r, _tex[TRow], Color.white);
                else Fill(r, new Color(0.18f, 0.16f, 0.16f, 0.9f));
                if (picked || st == Over || st == Down)
                {
                    if (_tex[TRowHover] != null) Tex(r, _tex[TRowHover], picked ? Color.white : RowHoverTint);
                    else Fill(r, new Color(1f, 1f, 1f, picked ? 0.12f : 0.07f));
                }
            }
            if (hit) Click();
            return hit;
        }

        /// <summary>The icon slot of a list row / detail: a plate with the
        /// initial, tinted by tier.</summary>
        internal static void Plate(Rect r, string initial, Color tint, float size)
        {
            if (Event.current.type != EventType.Repaint) return;
            Fill(r, new Color(tint.r * 0.25f, tint.g * 0.25f, tint.b * 0.25f, 0.85f));
            Fill(new Rect(r.x, r.yMax - Mathf.Max(1f, 2f * _k), r.width, Mathf.Max(1f, 2f * _k)), tint);
            Text(r, initial, Bebas, size, Center, tint);
        }

        // ------------------------------------------------------------ list scrolling

        /// <summary>A clipped list: mouse wheel over it scrolls, the vanilla
        /// scroll line + thumb at the right. Returns the clamped offset;
        /// rows are drawn at y - offset inside the group. EndList closes it.</summary>
        static float _scrollGrab;
        internal static float BeginList(Rect view, Rect track, float offset, float contentH)
        {
            float max = Mathf.Max(0f, contentH - view.height);
            Event e = Event.current;
            if (GUI.enabled && e.type == EventType.ScrollWheel && (view.Contains(e.mousePosition) || track.Contains(e.mousePosition)) && max > 0f)
            {
                offset += e.delta.y * 20f * _k;
                e.Use();
            }
            offset = Mathf.Clamp(offset, 0f, max);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            float th = max > 0f ? Mathf.Max(24f * _k, track.height * view.height / contentH) : track.height;
            th = Mathf.Min(th, track.height);
            float travel = track.height - th;
            float ty = track.y + (max > 0f ? travel * offset / max : 0f);
            if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0 && track.Contains(e.mousePosition) && max > 0f)
            {
                GUIUtility.hotControl = id;
                _scrollGrab = e.mousePosition.y >= ty && e.mousePosition.y <= ty + th ? e.mousePosition.y - ty : th * 0.5f;
                offset = travel > 0f ? Mathf.Clamp01((e.mousePosition.y - track.y - _scrollGrab) / travel) * max : 0f;
                e.Use();
            }
            else if (GUIUtility.hotControl == id && (e.type == EventType.MouseDrag || e.type == EventType.MouseUp))
            {
                offset = travel > 0f ? Mathf.Clamp01((e.mousePosition.y - track.y - _scrollGrab) / travel) * max : 0f;
                if (e.type == EventType.MouseUp) GUIUtility.hotControl = 0;
                e.Use();
            }
            if (e.type == EventType.Repaint && max > 0f)
            {
                Rect line = new Rect(track.center.x - Mathf.Max(1f, _k), track.y, Mathf.Max(1f, 2f * _k), track.height);
                if (_tex[TScrollBg] != null) Tex(line, _tex[TScrollBg], ThumbTint); else Fill(line, new Color(1f, 1f, 1f, 0.3f));
                ty = track.y + travel * (offset / max);
                Rect thumb = new Rect(track.center.x - 5f * _k, ty, 10f * _k, th);
                if (_tex[TThumb] != null) Tex(thumb, _tex[TThumb], ThumbTint); else Fill(thumb, new Color(1f, 1f, 1f, 0.6f));
            }
            GUI.BeginGroup(view);
            return offset;
        }

        internal static void EndList() { GUI.EndGroup(); }

        // ------------------------------------------------------------ yes / no dialog

        static string _ask;
        static int _askToken;

        /// <summary>Opens the AnswerPopUpUI-style question; DrawDialog then
        /// returns its token on Yes.</summary>
        internal static void Ask(string question, int token) { _ask = question; _askToken = token; }

        internal static bool Asking { get { return _ask != null; } }

        internal static void CancelAsk() { _ask = null; }

        /// <summary>Draws the open question over the whole screen. Returns the
        /// token when Yes (or Enter) was pressed, 0 otherwise; No / Esc close it.</summary>
        internal static int DrawDialog()
        {
            if (_ask == null) return 0;
            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                Rect all = new Rect(0f, 0f, Screen.width, Screen.height);
                if (_tex[TDim] != null) Tex(all, _tex[TDim], Dimmer); else Fill(all, Dimmer);
            }
            float k = _k * 0.75f;   // AnswerPopUpUI is scaled 0.75 in the buy menu
            Rect form = new Rect(_form.center.x - 225f * k, _form.center.y - 100f * k, 450f * k, 200f * k);
            if (e.type == EventType.Repaint)
            {
                Fill(form, new Color(0.035f, 0.035f, 0.035f, 1f));
                if (_tex[TDialog] != null) Tex(form, _tex[TDialog], Color.white);
                else Fill(form, new Color(0.1f, 0.1f, 0.1f, 0.97f));
                float save = _k;
                _k = k;
                // warning_01_form bakes its triangle into x20-110, y40-120.
                // Reserve that column; it must never sit behind the question.
                Paragraph(new Rect(form.x + 126f * k, form.y + 22f * k, form.width - 154f * k, 104f * k), _ask, 22f, White);
                _k = save;
            }
            Rect yes = new Rect(form.center.x - 180f * k, form.yMax - 62f * k, 166f * k, 42f * k);
            Rect no = new Rect(form.center.x + 14f * k, form.yMax - 62f * k, 166f * k, 42f * k);
            float s = 26f * 0.75f;
            int token = 0;
            if (Button(yes, Loc.T("Да", "Yes"), s, true)) token = _askToken;
            bool cancel = Button(no, Loc.T("Нет", "No"), s, true);
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { token = _askToken; Click(); e.Use(); }
                else if (e.keyCode == KeyCode.Escape) { cancel = true; e.Use(); }
            }
            // Clicks anywhere else land on the dialog's dimmer, not under it.
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseUp) && !form.Contains(e.mousePosition)) e.Use();
            if (token != 0 || cancel) _ask = null;
            return token;
        }

        // ------------------------------------------------------------ sounds

        /// <summary>UIController.PlayUISound(state): "Click", "BuyItem", "OpenWindow".</summary>
        internal static void Sound(string state)
        {
            try
            {
                object ui = Ui;
                if (ui == null) return;
                if (_mSound == null) _mSound = ui.GetType().GetMethod("PlayUISound",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(string) }, null);
                if (_mSound == null) return;
                _soundArg[0] = state;
                _mSound.Invoke(ui, _soundArg);
            }
            catch { }
        }

        static void Click() { Sound("Click"); }
    }

    // Local paint adapter for the preserved contract actions. It does not
    // change UiKit or the style of any other screen.
    internal static class MercPageLook
    {
        internal static readonly Color Text = VanillaSkin.White, TextDim = VanillaSkin.Grey,
            Good = VanillaSkin.Green, Warn = VanillaSkin.Gold, Bad = VanillaSkin.Red;
        static string _tip;
        static readonly GUIContent _tipContent = new GUIContent();
        static GUIStyle _tipMeasure;
        static string _measuredTip;
        static float _tipWidth, _tipHeight;
        static int _tipSize;
        static Font _tipFace;

        internal static void BeginTips() { _tip = null; }
        internal static void Tip(Rect r, string text)
        {
            if (text != null && r.Contains(Event.current.mousePosition)) _tip = text;
        }

        internal static void Label(Rect r, string text, int font, int align, Color color)
        {
            VanillaSkin.Text(r, text, font == UiFont.Heading ? VanillaSkin.Light : VanillaSkin.Regular,
                font == UiFont.Small ? 14f : font == UiFont.Heading ? 18f : 16f, align, color);
        }

        internal static void Status(Rect r, int tone, string text)
        {
            Label(r, text, UiFont.Small, UiFont.Left, tone == UiTone.Error ? Bad : tone == UiTone.Warning ? Warn : TextDim);
        }

        internal static void Chip(Rect r, string text, int tone) { Status(r, tone, text); }
        internal static void Progress(Rect r, float v, Color color) { VanillaSkin.Bar(r, v, color); }
        internal static Rect Col(Rect r, int index, int count)
        {
            float gap = 6f * VanillaSkin.K;
            float width = (r.width - gap * (count - 1)) / count;
            return new Rect(r.x + index * (width + gap), r.y, width, r.height);
        }

        internal static bool Button(Rect r, string text, int kind, bool enabled, string hint)
        {
            Tip(r, hint == null ? text : hint);
            return VanillaSkin.Button(r, text, 14f, enabled);
        }

        internal static void DrawTips()
        {
            if (_tip == null || Event.current.type != EventType.Repaint) return;
            if (_tipMeasure == null) { _tipMeasure = new GUIStyle(); _tipMeasure.wordWrap = true; }
            int size = Mathf.Max(8, Mathf.RoundToInt(14f * VanillaSkin.K));
            float width = Mathf.Min(420f * VanillaSkin.K, Screen.width - 20f);
            Font face = VanillaSkin.BodyFont;
            if (_measuredTip != _tip || _tipWidth != width || _tipSize != size || _tipFace != face)
            {
                _measuredTip = _tip; _tipWidth = width; _tipSize = size; _tipFace = face;
                _tipMeasure.font = face; _tipMeasure.fontSize = size; _tipContent.text = _tip;
                _tipHeight = Mathf.Min(Screen.height - 20f, _tipMeasure.CalcHeight(_tipContent, width - 20f) + 20f);
            }
            float height = _tipHeight;
            Vector2 mouse = Event.current.mousePosition;
            Rect r = new Rect(Mathf.Clamp(mouse.x + 12f, 10f, Screen.width - width - 10f),
                Mathf.Clamp(mouse.y + 20f, 10f, Screen.height - height - 10f), width, height);
            VanillaSkin.Fill(r, new Color(0.04f, 0.04f, 0.04f, 1f));
            VanillaSkin.Paragraph(new Rect(r.x + 10f, r.y + 10f, r.width - 20f, r.height - 20f), _tip, 14f, Text);
        }
    }
}

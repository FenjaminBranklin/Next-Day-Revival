// Admin-only native paint. Input and navigation remain in UiKit.
using System;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AdminPaint
    {
        internal static bool Active;
        static readonly Color Back = new Color(0.055f, 0.055f, 0.055f, 1f);
        static readonly Color White = new Color(0.992f, 0.992f, 0.992f, 1f);
        static readonly GUIContent Content = new GUIContent();
        static GUIStyle _label;
        // Cache metrics, including fitted text, by reference. No measurement
        // or string construction on a warmed repaint, even for hidden tabs.
        sealed class Fit
        {
            internal string Text, Shown;
            internal Font Font;
            internal float Width, Height;
            internal int Size, FontSize;
            internal bool Wrap;
        }
        static readonly Fit[] Fits = new Fit[512];
        static int _next;

        internal static float FooterHeight { get { return UiKit.S(48f); } }

        internal static void Plate(Rect r, bool selected, bool hot, bool down, bool enabled)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color old = GUI.color;
            // Ragged native edges are transparent. An opaque dark backing
            // keeps the text readable over sky as well as over the terrain.
            GUI.color = Back;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            Texture2D t = VanillaUi.Asset(selected || hot ? "btn_hover" : "btn");
            GUI.color = !enabled ? Color.grey : down ? VanillaUi.Pressed : hot ? VanillaUi.Hover : Color.white;
            if (t != null)
            {
                float cap = Mathf.Min(r.width * 0.5f, r.height * (t.width * 0.08f / t.height));
                float u = cap * t.height / (r.height * t.width);
                GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, cap, r.height), t, new Rect(0f, 0f, u, 1f));
                GUI.DrawTextureWithTexCoords(new Rect(r.x + cap, r.y, r.width - 2f * cap, r.height), t, new Rect(u, 0f, 1f - 2f * u, 1f));
                GUI.DrawTextureWithTexCoords(new Rect(r.xMax - cap, r.y, cap, r.height), t, new Rect(1f - u, 0f, u, 1f));
            }
            GUI.color = old;
        }

        internal static void Window(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color old = GUI.color;
            GUI.color = Back;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            // Keep the native form's brush corners square. Stretch only its
            // centre and straight edges rather than the entire 585x260 form.
            Texture2D t = VanillaUi.Asset("warning_02_empty");
            GUI.color = Color.white;
            if (t != null)
            {
                float edge = UiKit.S(12f), u = 12f / t.width, v = 12f / t.height;
                for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                {
                    float px = x == 0 ? r.x : x == 1 ? r.x + edge : r.xMax - edge;
                    float py = y == 0 ? r.y : y == 1 ? r.y + edge : r.yMax - edge;
                    float w = x == 1 ? r.width - 2f * edge : edge;
                    float h = y == 1 ? r.height - 2f * edge : edge;
                    GUI.DrawTextureWithTexCoords(new Rect(px, py, w, h), t,
                        new Rect(x == 0 ? 0f : x == 1 ? u : 1f - u,
                            y == 0 ? 1f - v : y == 1 ? v : 0f,
                            x == 1 ? 1f - 2f * u : u, y == 1 ? 1f - 2f * v : v));
                }
            }
            GUI.color = old;
        }

        internal static void Label(Rect r, string text, int role, int align, Color color, bool wrap)
        {
            if (text == null || Event.current.type != EventType.Repaint) return;
            if (_label == null)
            {
                _label = new GUIStyle();
                _label.richText = false;
                _label.clipping = TextClipping.Clip;
            }
            Font font = VanillaUi.Font(role <= UiFont.Heading);
            int size = Mathf.Max(8, Mathf.RoundToInt(UiKit.S(role == UiFont.Title ? 18f : role == UiFont.Small ? 12f : 14f)));
            Fit fit = null;
            for (int i = 0; i < Fits.Length; i++)
            {
                Fit f = Fits[i];
                if (f == null) break;
                if (f != null && ReferenceEquals(f.Text, text) && f.Font == font && f.Width == r.width
                    && f.Height == r.height && f.Size == size && f.Wrap == wrap) { fit = f; break; }
            }
            _label.font = font;
            _label.fontSize = size;
            _label.wordWrap = wrap;
            if (fit == null)
            {
                fit = Fits[_next];
                if (fit == null) { fit = new Fit(); Fits[_next] = fit; }
                _next = (_next + 1) % Fits.Length;
                fit.Text = text; fit.Font = font; fit.Width = r.width; fit.Height = r.height; fit.Size = size; fit.Wrap = wrap;
                Content.text = text;
                int minimum = Mathf.Max(8, Mathf.RoundToInt(UiKit.S(11f)));
                while (_label.fontSize > minimum && !FitsIn(r, wrap)) _label.fontSize--;
                fit.Shown = text;
                // Arbitrary player names/status may exceed the row. Keep the
                // full text in the hover tip; never paint outside its cell.
                if (!FitsIn(r, wrap))
                {
                    int end = text.Length;
                    do { end--; Content.text = text.Substring(0, end) + "..."; }
                    while (end > 0 && !FitsIn(r, wrap));
                    fit.Shown = Content.text;
                }
                // Store the fitted size separately from the requested size.
                fit.FontSize = _label.fontSize;
            }
            _label.fontSize = fit.FontSize;
            _label.alignment = align == UiFont.Center ? TextAnchor.MiddleCenter : align == UiFont.Right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            _label.normal.textColor = color;
            Content.text = fit.Shown;
            Color old = GUI.color;
            GUI.color = Color.white;
            _label.Draw(r, Content, false, false, false, false);
            GUI.color = old;
            if (!ReferenceEquals(fit.Shown, text)) UiKit.Tip(r, text);
        }

        static bool FitsIn(Rect r, bool wrap)
        {
            return wrap ? _label.CalcHeight(Content, r.width) <= r.height
                : _label.CalcSize(Content).x <= r.width && _label.CalcSize(Content).y <= r.height;
        }

        internal static void Button(Rect r, string text, int look, bool enabled, bool hot, bool down)
        {
            Plate(r, look == UiButton.Primary || look == UiButton.Danger, hot, down, enabled);
            float pad = UiKit.S(8f);
            Label(new Rect(r.x + pad, r.y + UiKit.S(2f), Mathf.Max(1f, r.width - 2f * pad), r.height - UiKit.S(4f)),
                text, UiFont.Body, UiFont.Center, enabled ? White : VanillaUi.Grey, false);
        }

        internal static void Chip(Rect r, string text, int tone)
        {
            Plate(r, false, false, false, true);
            UiKit.Fill(new Rect(r.x, r.y + UiKit.S(4f), UiKit.S(2f), r.height - UiKit.S(8f)), UiKit.ToneColor(tone), 0);
            Label(new Rect(r.x + UiKit.S(8f), r.y, r.width - UiKit.S(16f), r.height), text,
                UiFont.Small, UiFont.Center, White, false);
        }

        internal static void Status(Rect r, int tone, string text)
        {
            Plate(r, false, false, false, true);
            float icon = UiKit.S(18f);
            Rect ir = new Rect(r.x + UiKit.S(10f), r.y + (r.height - icon) * 0.5f, icon, icon);
            Color c = UiKit.ToneColor(tone);
            if (tone == UiTone.Loading) UiKit.Spinner(ir, c); else UiKit.Icon(ir, UiKit.ToneIcon(tone), c);
            Label(new Rect(ir.xMax + UiKit.S(10f), r.y + UiKit.S(4f), r.width - UiKit.S(48f), r.height - UiKit.S(8f)),
                text, UiFont.Body, UiFont.Left, White, true);
        }
    }
}

// C U2: measured merc HUD paint. Gameplay, selection and orders stay in MercUi.
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        static GUIStyle _hudName, _hudState, _hudSmall, _hudWheelLabel, _hudDisabled, _hudKey;
        static float _hudScale;
        static int _hudWidth, _hudHeight, _hudLang = -1;
        static Font _hudFont;
        static string _hudWhoFor;
        static float _hudHubNameHeight;
        static bool _hudStripDirty = true, _hudWheelDirty = true, _hudPeaceful;
        static Rect _hudStripFrame, _hudHub, _hudFooter;
        static Rect[] _hudSectors;
        static GUIContent[] _hudSectorNames, _hudSectorKeys;
        static readonly GUIContent _hudWho = new GUIContent(), _hudRelease = new GUIContent(), _hudPoint = new GUIContent();
        static readonly GUIContent _hudUnpaid = new GUIContent("$!"), _hudOnFoot = new GUIContent();
        static readonly Color _hudInk = new Color(0.08f, 0.08f, 0.08f, 0.97f);
        static readonly Color _hudBrush = new Color(0.25f, 0.25f, 0.25f, 1f);

        static GUIContent[] HudContents(int count)
        {
            GUIContent[] result = new GUIContent[count];
            for (int i = 0; i < count; i++) result[i] = new GUIContent();
            return result;
        }

        static float HudS(float v) { return v * _hudScale; }

        static GUIStyle HudStyle(int size, TextAnchor align, bool wrap)
        {
            GUIStyle s = new GUIStyle();
            s.font = _hudFont; s.fontSize = Mathf.RoundToInt(HudS(size));
            s.normal.textColor = Color.white; s.alignment = align;
            s.wordWrap = wrap; s.richText = true;
            s.padding = new RectOffset(0, 0, 0, 0);
            return s;
        }

        // Measure with the same body face that is drawn, including late asset
        // discovery. Only resize/font/language changes allocate new styles.
        static void HudStyles()
        {
            // Partial-class field initialization order is unspecified.
            if (_hudSectors == null)
            {
                _hudSectors = new Rect[SectorEn.Length];
                _hudSectorNames = HudContents(SectorEn.Length);
                _hudSectorKeys = HudContents(SectorEn.Length);
            }
            float scale = UiScale.For(Screen.height, 1f);
            Font font = VanillaUi.Font(false);
            int lang = Loc.Lang();
            bool geometry = _hudName == null || _hudWidth != Screen.width || _hudHeight != Screen.height || _hudScale != scale || _hudFont != font;
            if (geometry)
            {
                _hudWidth = Screen.width; _hudHeight = Screen.height; _hudScale = scale; _hudFont = font;
                _hudName = HudStyle(20, TextAnchor.UpperLeft, true);
                _hudState = HudStyle(18, TextAnchor.UpperLeft, true);
                _hudSmall = HudStyle(16, TextAnchor.MiddleLeft, false);
                _hudWheelLabel = HudStyle(20, TextAnchor.MiddleCenter, true);
                _hudDisabled = new GUIStyle(_hudWheelLabel); _hudDisabled.normal.textColor = new Color(0.78f, 0.78f, 0.78f, 1f);
                _hudKey = HudStyle(16, TextAnchor.MiddleCenter, true);
                _hudStripDirty = _hudWheelDirty = true;
            }
            if (_hudLang != lang || _hudPeaceful != _wheelPeaceful || geometry)
            {
                _hudLang = lang; _hudPeaceful = _wheelPeaceful;
                for (int i = 0; i < SectorEn.Length; i++)
                {
                    _hudSectorNames[i].text = i == 5
                        ? (_wheelPeaceful ? Loc.T("МИРНЫЙ: ВКЛ", "PEACEFUL ON") : Loc.T("МИРНЫЙ: ВЫКЛ", "PEACEFUL OFF"))
                        : Loc.T(SectorRu[i], SectorEn[i]);
                    // Only ten numeric shortcuts exist; sector 11 uses wheel selection.
                    _hudSectorKeys[i].text = i < KeyCaps.Length ? KeyCaps[i] : Loc.T("колесо", "scroll");
                }
                _hudOnFoot.text = Loc.T("3: пешком", "3: on foot");
                _hudWheelDirty = _hudStripDirty = true;
            }
        }

        // A solid dark underpaint guarantees contrast even over pale sky and
        // transparent brush edges. Native clean slices retain their ragged ends.
        // Do not send this fill through the shared chrome-remapping wrapper.
        static void HudSolid(Rect r, Color tint)
        {
            Color old = GUI.color; GUI.color = tint;
            GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = old;
        }

        static void HudPlate(Rect r, bool selected, bool live)
        {
            HudSolid(r, _hudInk);
            VanillaUi.Panel(r, VanillaUi.Plate, _hudBrush);
            if (selected)
            {
                Color line = live ? VanillaUi.Gold : VanillaUi.Grey;
                float t = Mathf.Max(1f, HudS(2f));
                HudSolid(new Rect(r.x, r.y, r.width, t), line);
                HudSolid(new Rect(r.x, r.yMax - t, r.width, t), line);
                HudSolid(new Rect(r.x, r.y, t, r.height), line);
                HudSolid(new Rect(r.xMax - t, r.y, t, r.height), line);
            }
        }

        static void HudLabel(Rect r, GUIContent text, GUIStyle style)
        {
            Color old = GUI.color, content = GUI.contentColor;
            GUI.color = GUI.contentColor = Color.white;
            GUI.Label(r, text, style);
            GUI.color = old; GUI.contentColor = content;
        }

        static void HudStripGeometry()
        {
            if (!_hudStripDirty) return;
            _hudStripDirty = false;
            float width = HudS(410f);
            for (int i = 0; i < _stripCount; i++)
            {
                width = Mathf.Max(width, _hudName.CalcSize(_strip[i].Name).x + HudS(138f));
                width = Mathf.Max(width, _hudState.CalcSize(_strip[i].Order).x + HudS(44f));
            }
            width = Mathf.Min(width, Mathf.Min(Screen.width - 24f, Mathf.Max(HudS(410f), Screen.width * 0.42f)));
            float height = HudS(38f);
            for (int i = 0; i < _stripCount; i++)
            {
                StripRow row = _strip[i];
                row.NameHeight = Mathf.Max(HudS(26f), _hudName.CalcHeight(row.Name, width - HudS(126f)) + HudS(2f));
                row.OrderHeight = Mathf.Max(HudS(24f), _hudState.CalcHeight(row.Order, width - HudS(32f)) + HudS(2f));
                height += row.NameHeight + row.OrderHeight + HudS(16f);
            }
            _hudStripFrame = new Rect(12f, Mathf.Max(12f, Screen.height - height - HudS(150f)), width, height);
        }

        static void HudWheelGeometry()
        {
            if (!_hudWheelDirty && _hudWhoFor == _wheelWho) return;
            _hudWheelDirty = false;
            _hudWhoFor = _wheelWho; _hudWho.text = _wheelWho;
            int n = SectorEn.Length;
            float width = HudS(176f), height = HudS(80f);
            for (int i = 0; i < n; i++)
                width = Mathf.Max(width, _hudWheelLabel.CalcSize(_hudSectorNames[i]).x + HudS(24f));
            width = Mathf.Min(width, Mathf.Min(HudS(216f), Screen.width * 0.23f));
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            // Wider than tall: eleven rectangular labels need more room near
            // twelve o'clock than the old circle gave them. Centres still sit
            // inside their original angular selection sectors.
            float rx = Mathf.Min(Screen.width * 0.5f - width * 0.5f - 12f,
                Mathf.Max(HudS(390f), (width + HudS(8f)) / Mathf.Sin(2f * Mathf.PI / n)));
            float ry = Mathf.Min(HudS(246f), Screen.height * 0.5f - height * 0.5f - HudS(68f));
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * Mathf.PI / n;
                _hudSectors[i] = new Rect(cx + Mathf.Sin(a) * rx - width * 0.5f,
                    cy - Mathf.Cos(a) * ry - height * 0.5f, width, height);
            }
            _hudHubNameHeight = Mathf.Max(HudS(48f), _hudWheelLabel.CalcHeight(_hudWho, HudS(204f)) + HudS(2f));
            float hh = _hudHubNameHeight + HudS(48f);
            _hudHub = new Rect(cx - HudS(112f), cy - hh * 0.5f, HudS(224f), hh);
            float fw = Mathf.Min(HudS(650f), Screen.width - 24f);
            _hudFooter = new Rect(cx - fw * 0.5f, cy + ry + height * 0.5f + HudS(8f), fw, HudS(52f));
        }
    }
}

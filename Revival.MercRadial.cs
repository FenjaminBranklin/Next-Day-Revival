// G O2: stock map/loading rings, skill tiles, HUD plates and Roboto.
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MercUi
    {
        static readonly string[] RadialGroupsEn = { "MOVE", "COMBAT", "SUPPORT", "AIR DEFENCE" };
        static readonly string[] RadialGroupsRu = { "ДВИЖЕНИЕ", "БОЙ", "ПОДДЕРЖКА", "ПВО" };
        // Native Texture2D names, inspected in resources/sharedassets via the kit.
        static readonly string[] RadialArtNames = { "Loading_wheel", "AreaMarker", "Knob",
            "skill_1_2", "skill_13", "Health_white", "Crosshair_01", "icon_arrow",
            "ChangeLocationMarker", "AreaMarkerCut", "skill_0_2_new", "CantCrosshair",
            "seat_driver", "AirDropMarker" };
        static readonly int[] RadialGroupIcons = { 3, 4, 5, 6 };
        static readonly int[] RadialOrderIcons = { 7, 6, 8, 9, 12, 11, 6, 10, 4, 13, 5 };
        static readonly Texture2D[] _radialArt = new Texture2D[RadialArtNames.Length];
        static readonly Rect[] _radialIcons = new Rect[4], _radialKeys = new Rect[4], _radialMarks = new Rect[4];
        static readonly GUIContent[] _radialGroups = HudContents(4);
        static readonly GUIContent[] _radialNumbers = { new GUIContent("1"), new GUIContent("2"), new GUIContent("3"), new GUIContent("4") };
        static readonly GUIContent _radialTitle = new GUIContent(), _radialCaption = new GUIContent(),
            _radialHint = new GUIContent(), _radialBack = new GUIContent(), _radialDetail = new GUIContent();
        static GUIStyle _radialHeading, _radialText, _radialSmall, _radialNumber;
        static int _radialLang = -1, _radialGroup = -2, _radialCount, _radialScene = -999, _radialSession = -1;
        static Font _radialFont;
        static float _radialScale;
        static int _radialWidth, _radialHeight;
        static Rect _radialRing, _radialRim, _radialHub, _radialTitleRect, _radialCaptionRect,
            _radialWhoRect, _radialHintRect, _radialBackRect, _radialDetailRect, _radialDetailPlate;

        // Discovery is an opening event, never a recurring per-frame/5 Hz search.
        // Embedded exact native pixels also work before map/skills assets load.
        static void RadialAssets()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
            if (_radialScene != scene)
            {
                _radialScene = scene;
                for (int i = 0; i < _radialArt.Length; i++) _radialArt[i] = null;
            }
            if (_radialSession == _wheelSession) return;
            _radialSession = _wheelSession;
            bool missing = false;
            for (int i = 0; i < _radialArt.Length; i++) if (_radialArt[i] == null) missing = true;
            if (!missing) return;
            Object[] textures = Resources.FindObjectsOfTypeAll(typeof(Texture2D));
            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D t = textures[i] as Texture2D;
                if (t == null) continue;
                for (int k = 0; k < RadialArtNames.Length; k++)
                    if (_radialArt[k] == null && t.name == RadialArtNames[k]) { _radialArt[k] = t; break; }
            }
            for (int i = 0; i < _radialArt.Length; i++)
                if (_radialArt[i] == null) _radialArt[i] = MercRadialArt.Load(i, RadialArtNames[i]);
        }

        static void RadialGeometry()
        {
            HudStyles();
            int lang = Loc.Lang(), count = MercRadialPlan.Count(_wheelGroup);
            bool resize = _radialText == null || _radialWidth != Screen.width || _radialHeight != Screen.height
                || _radialScale != _hudScale || _radialFont != _hudFont;
            if (resize)
            {
                _radialWidth = Screen.width; _radialHeight = Screen.height;
                _radialScale = _hudScale; _radialFont = _hudFont;
                _radialHeading = HudStyle(16, TextAnchor.MiddleCenter, true);
                _radialText = HudStyle(14, TextAnchor.MiddleCenter, true);
                _radialSmall = HudStyle(12, TextAnchor.MiddleCenter, true);
                _radialNumber = HudStyle(11, TextAnchor.MiddleCenter, false);
                _radialNumber.normal.textColor = VanillaUi.Gold;
                float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
                _radialRing = new Rect(cx - HudS(130f), cy - HudS(130f), HudS(260f), HudS(260f));
                _radialRim = new Rect(cx - HudS(134f), cy - HudS(134f), HudS(268f), HudS(268f));
                _radialHub = new Rect(cx - HudS(88f), cy - HudS(88f), HudS(176f), HudS(176f));
                _radialTitleRect = new Rect(cx - HudS(68f), cy - HudS(64f), HudS(136f), HudS(38f));
                _radialCaptionRect = new Rect(cx - HudS(68f), cy - HudS(25f), HudS(136f), HudS(38f));
                _radialWhoRect = new Rect(cx - HudS(70f), cy + HudS(15f), HudS(140f), HudS(22f));
                _radialHintRect = new Rect(cx - HudS(60f), cy + HudS(38f), HudS(120f), HudS(30f));
                _radialBackRect = new Rect(cx - HudS(190f), cy + HudS(143f), HudS(380f), HudS(22f));
                float fw = Mathf.Min(HudS(400f), Screen.width - 24f);
                _radialDetailPlate = new Rect(cx - fw * 0.5f, cy + HudS(168f), fw, HudS(58f));
                _radialDetailRect = new Rect(_radialDetailPlate.x + HudS(12f), _radialDetailPlate.y + HudS(5f), fw - HudS(24f), HudS(48f));
            }
            if (resize || _radialCount != count)
            {
                _radialCount = count;
                for (int i = 0; i < count; i++)
                {
                    float a = i * 2f * Mathf.PI / count;
                    float x = Screen.width * 0.5f + Mathf.Sin(a) * HudS(110f),
                        y = Screen.height * 0.5f - Mathf.Cos(a) * HudS(110f);
                    _radialIcons[i] = new Rect(x - HudS(16f), y - HudS(21f), HudS(32f), HudS(32f));
                    _radialKeys[i] = new Rect(x - HudS(10f), y + HudS(10f), HudS(20f), HudS(16f));
                    _radialMarks[i] = new Rect(x - HudS(24f), y - HudS(28f), HudS(48f), HudS(48f));
                }
            }
            if (_radialLang != lang || _radialGroup != _wheelGroup)
            {
                _radialLang = lang; _radialGroup = _wheelGroup;
                for (int i = 0; i < 4; i++) _radialGroups[i].text = Loc.T(RadialGroupsRu[i], RadialGroupsEn[i]);
                _radialTitle.text = _wheelGroup < 0 ? Loc.T("ПРИКАЗЫ", "ORDERS") : _radialGroups[_wheelGroup].text;
                _radialHint.text = _wheelGroup < 0 ? Loc.T("цифра / наведение\nоткрыть раздел", "number / hover\nopen category")
                    : Loc.T("цифра / отпустить\nприказ", "number / release\norder");
                _radialBack.text = _wheelGroup < 0 ? Loc.T("Esc / ПКМ: отмена", "Esc / RMB: cancel")
                    : Loc.T("Backspace: категории   Esc / ПКМ: отмена", "Backspace: categories   Esc / RMB: cancel");
            }
        }

        static void RadialTexture(Rect rect, int art, Color tint)
        {
            Texture2D t = _radialArt[art];
            if (t == null) return;
            Color old = GUI.color; GUI.color = tint;
            // Knob has 14 px transparent padding on each side. Crop it so the
            // stock disc covers the hub; preserve icon aspect ratios as well.
            if (art == 2) GUI.DrawTextureWithTexCoords(rect, t, new Rect(14f / 64f, 14f / 64f, 36f / 64f, 36f / 64f));
            else
            {
                if (art >= 3 && t.width > 0 && t.height > 0)
                {
                    float aspect = (float)t.width / t.height;
                    if (aspect > 1f) { float h = rect.width / aspect; rect.y += (rect.height - h) * 0.5f; rect.height = h; }
                    else { float w = rect.height * aspect; rect.x += (rect.width - w) * 0.5f; rect.width = w; }
                }
                GUI.DrawTexture(rect, t);
            }
            GUI.color = old;
        }

        static void DrawRadial()
        {
            RadialAssets(); RadialGeometry();
            RadialTexture(_radialRing, 0, new Color(0.08f, 0.08f, 0.08f, 0.88f));
            RadialTexture(_radialRim, 1, new Color(0.55f, 0.55f, 0.55f, 0.7f));
            RadialTexture(_radialHub, 2, new Color(0.055f, 0.055f, 0.055f, 0.92f));
            for (int i = 0; i < _radialCount; i++)
            {
                int order = MercRadialPlan.Order(_wheelGroup, i);
                bool live = order != 2 || !MercRide.OwnerInVehicle;
                bool chosen = i == _wheelSlot;
                if (chosen) RadialTexture(_radialMarks[i], 1, live ? VanillaUi.Gold : VanillaUi.Grey);
                RadialTexture(_radialIcons[i], order < 0 ? RadialGroupIcons[i] : RadialOrderIcons[order],
                    !live ? new Color(0.55f, 0.55f, 0.55f, 0.7f) : chosen ? VanillaUi.Gold : VanillaUi.White);
                Rect cap = _radialKeys[i];
                cap.x -= HudS(2f); cap.y -= HudS(4f); cap.width = cap.height = HudS(24f);
                RadialTexture(cap, 2, new Color(0.055f, 0.055f, 0.055f, 0.98f));
                HudLabel(_radialKeys[i], _radialNumbers[i], _radialNumber);
            }
            _radialCaption.text = _wheelGroup < 0
                ? (_wheelSlot < 0 ? Loc.T("Выберите раздел", "Choose category") : _radialGroups[_wheelSlot].text)
                : _wheelPick < 0 ? Loc.T("Выберите приказ", "Choose order") : _hudSectorNames[_wheelPick].text;
            _hudWho.text = _wheelWho;
            HudLabel(_radialTitleRect, _radialTitle, _radialHeading);
            HudLabel(_radialCaptionRect, _radialCaption, _radialText);
            HudLabel(_radialWhoRect, _hudWho, _radialSmall);
            HudLabel(_radialHintRect, _radialHint, _radialSmall);
            // Native brush plate only for contextual help, never around segments.
            VanillaUi.Panel(_radialBackRect, VanillaUi.Plate, Color.white);
            HudLabel(_radialBackRect, _radialBack, _radialSmall);
            _radialDetail.text = _wheelPick == 2 && MercRide.OwnerInVehicle
                ? Loc.T("Патруль задаётся пешком", "PATROL is set on foot")
                : _wheelPoint ?? _wheelDuty;
            if (_radialDetail.text != null)
            {
                VanillaUi.Panel(_radialDetailPlate, VanillaUi.Plate, Color.white);
                HudLabel(_radialDetailRect, _radialDetail, _radialSmall);
            }
        }
    }
}

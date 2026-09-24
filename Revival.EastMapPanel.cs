// Revival.EastMapPanel.cs - the map window for the east world ([World] EastTile).
//
// The vanilla window (level7 HUD_MapUI, read 2026-09-24,
// docs/ai/tasks/east-map-panel.md) is a 850 x 600 frame (FormUI, map_form2
// with the six legend icons painted into its top band) around a clip panel
// (SoftnessPanel, UIPanel 797 x 510, softness 16, UIScrollView vertical) that
// scrolls a square 803 x 803 map texture (MapTextureUI, grid child 770 x 770).
// UIRoot is Constrained 1280 x 720, fit width and height (level7).
//
// With the east world on, this lays the SAME objects out again, in SCREEN
// PIXELS (docs/ai/tasks/east-map-sharp.md):
//
// - The map is exactly as wide as the screen minus the two frame borders and
//   exactly half as high, in whole pixels: MapTextureUI is W x W/2 widget
//   units at a transform scale of one pixel per unit. One metre is the same
//   number of pixels in x and z, always.
// - If W/2 does not fit under the legend band, the clip panel shows a
//   window of it and the map scrolls VERTICALLY (mouse wheel, drag, the bar
//   in the right border). It never scrolls sideways and is never shrunk.
// - The artwork (east_map_en/ru.png, 1848 x 924 = GW_Scene_1's own 924 px per
//   5 km) is resampled ONCE, on the CPU, to exactly W x W/2 (Catmull-Rom;
//   widened when shrinking) and drawn 1:1 with point filtering and no mipmaps.
// - Grid lines and letters are UI widgets at screen resolution (the vanilla
//   grid artwork is switched off), in the vanilla grid's cells, look and
//   Roboto; a 20 x 10 grid whose west ten columns are the vanilla grid.
// - Frame (sliced east_map_form.png), legend icons, title, close, teleport
//   toggle and compass keep their vanilla offsets from the frame/view corners.
//
// Every world <-> map conversion follows by itself: markers are placed with
// MAP_SIZE (InitMapSize is called again after the resize: scale x size =
// W / unit root units), clicks and MapTools use the texture's widget bounds,
// MapInk/MapLabels use its corners and copy its scale; a scroll moves the
// clip panel like UIScrollView does, so all of them move with it. The
// player centerer (UICenterOnChild.CenterOn on map open) is replaced for the
// map's own instance by a clamped vertical scroll onto the player.
//
// Off (switch off, or any other scene): EastWorld.Install never calls
// Install here, and every hook returns on !EastWorld.Extends first.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only (the Cyrillic grid letters are \u escapes).

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class EastMapPanel
    {
        // ------------------------------------------------------------ geometry

        /// <summary>Frame borders in UI units: side, top band (title and
        /// legend), bottom. research/east_map_panel.py builds the frame with
        /// the same numbers.</summary>
        internal const float Side = 18f, Band = 76f, Bottom = 18f;
        /// <summary>Sliced border of east_map_form.png: left, bottom, right,
        /// top (NGUI's Vector4 order), in texture pixels = UI units.</summary>
        internal static readonly Vector4 FrameBorder = new Vector4(Side, Bottom, Side, Band);

        /// <summary>The vanilla frame width the legend was laid out on.</summary>
        const float VanillaFrameWidth = 850f;
        /// <summary>The vanilla grid: 770 of the map's 803 px, ten cells.</summary>
        internal const float GridFraction = 770f / 803f;
        /// <summary>One grid cell in world metres (479.45 m).</summary>
        internal const float CellMetres = 5000f * GridFraction / 10f;
        /// <summary>The vanilla grid's west and north edge in world metres.</summary>
        internal const float GridWest = -2500f * GridFraction, GridNorth = 2500f * GridFraction;
        internal const int GridColumns = 20, GridRows = 10;
        const string ColumnsEn = "ABCDEFGHJKLMNOPQRSTU";
        const string ColumnsRu = "\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417\u0418\u041a"
                               + "\u041b\u041c\u041d\u041e\u041f\u0420\u0421\u0422\u0423\u0424";

        // The vanilla grid artwork (Grid_EN/RU, 1024 px = ten 102.4 px cells),
        // measured: 1 px lines at ~42 % white, labels in Roboto Light 16 px at
        // (5, 1) px from the cell's top left corner, ~49 % white. The UI grid
        // scales those to the cell's screen size; labels are a little
        // stronger and never smaller than 11 px, so they stay readable.
        const float VanillaCell = 102.4f, LabelPx = 16f, LabelDx = 5f, LabelDy = 1f;
        const float MinLabelPx = 11f, LineAlpha = .42f, LabelAlpha = .62f;

        // Legend: label name, the icon's centre x in the vanilla frame (850
        // wide, centre-relative, measured in map_form2) and the icon's height
        // above its label.
        static readonly string[] LegendNames = new string[] {
            "TargetName", "SettlementName", "CharacterName", "FriendlyName", "EnemyName", "AirDropName" };
        static readonly float[] LegendIconX = new float[] { -358.5f, -185.75f, -56.5f, 43.5f, 164f, 254.75f };
        static readonly float[] LegendIconDy = new float[] { 2.5f, 1f, 2.5f, 2.75f, 2.75f, 1.5f };
        const float LegendIconSize = 24f;          // 48 px at 2 px per unit

        // Vanilla offsets (level7): title and legend below the frame top,
        // close and teleport toggle from the frame's top right corner, the
        // compass from the visible map's top right corner.
        const float TitleBelowTop = 22f, LegendBelowTop = 54f;
        static readonly Vector2 CloseFromCorner = new Vector2(-30.6f, -22.3f);
        static readonly Vector2 ToggleFromCorner = new Vector2(-132f, -21f);
        static readonly Vector2 CompassFromCorner = new Vector2(-43.1f, -50.4f);
        /// <summary>Scroll bar in the right border: width in UI units, the
        /// thumb's minimum length in pixels.</summary>
        const float BarWidth = 6f, MinThumbPx = 24f;

        /// <summary>The window in screen pixels, origin at the screen's bottom
        /// left corner (Input.mousePosition's frame).</summary>
        internal struct Layout
        {
            internal int ScreenW, ScreenH;
            /// <summary>Screen pixels per UI root unit.</summary>
            internal float Unit;
            internal int SidePx, BandPx, BottomPx;
            /// <summary>The whole map: MapW x MapH, MapH = MapW / 2 exactly.</summary>
            internal int MapW, MapH;
            /// <summary>The visible part: full width, ViewH high.</summary>
            internal int MapLeft, ViewBottom, ViewH;
            internal int FrameLeft, FrameBottom, FrameW, FrameH;
            internal int ViewTop { get { return ViewBottom + ViewH; } }
            internal int FrameTop { get { return FrameBottom + FrameH; } }
            internal int FrameRight { get { return FrameLeft + FrameW; } }
            internal int MapRight { get { return MapLeft + MapW; } }
            internal int MaxScroll { get { return MapH - ViewH; } }
            internal bool Scrolls { get { return MapH > ViewH; } }
        }

        /// <summary>The window for a screen of w x h pixels at unit pixels per
        /// UI unit: the map as wide as the screen minus the side borders
        /// (even, so its height is whole), exactly half as high; the view is
        /// that height or, when it does not fit under the band, what does.
        /// Pure; research/east_map_panel_check.py executes it.</summary>
        internal static Layout Compute(int w, int h, float unit)
        {
            Layout l = new Layout();
            l.ScreenW = w; l.ScreenH = h; l.Unit = unit;
            l.SidePx = Mathf.RoundToInt(Side * unit);
            l.BandPx = Mathf.RoundToInt(Band * unit);
            l.BottomPx = Mathf.RoundToInt(Bottom * unit);
            l.MapW = Mathf.Max(64, w - 2 * l.SidePx);
            if ((l.MapW & 1) != 0) l.MapW--;
            l.MapH = l.MapW / 2;
            l.ViewH = Mathf.Max(32, Mathf.Min(l.MapH, h - l.BandPx - l.BottomPx));
            l.MapLeft = (w - l.MapW) / 2;
            l.FrameLeft = l.MapLeft - l.SidePx;
            l.FrameW = l.MapW + 2 * l.SidePx;
            l.FrameH = l.ViewH + l.BandPx + l.BottomPx;
            l.FrameBottom = Mathf.Max(0, (h - l.FrameH) / 2);
            l.ViewBottom = l.FrameBottom + l.BottomPx;
            return l;
        }

        /// <summary>The drawn grid square of a world point in the east world:
        /// vanilla cells (A..K without I, 1..10) continued east to U. Latin
        /// letters, like the banners always used.</summary>
        internal static string GridSquare(Vector3 pos)
        {
            int col = Mathf.Clamp(Mathf.FloorToInt((pos.x - GridWest) / CellMetres), 0, GridColumns - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt((GridNorth - pos.z) / CellMetres), 0, GridRows - 1);
            return ColumnsEn[col].ToString() + (row + 1).ToString();
        }

        // --------------------------------------------------------------- hooks

        static Type _mapType, _textureType, _widgetType, _labelType, _scrollType, _centerType;

        internal static void Install(Harmony h)
        {
            _mapType = RevivalPlugin.TypeByName("MapUIManager");
            _textureType = RevivalPlugin.TypeByName("UITexture");
            _widgetType = RevivalPlugin.TypeByName("UIWidget");
            _labelType = RevivalPlugin.TypeByName("UILabel");
            _scrollType = RevivalPlugin.TypeByName("UIScrollView");
            _centerType = RevivalPlugin.TypeByName("UICenterOnChild");
            Patch(h, _mapType, "InitMapData", null, "AfterInit", null);
            Patch(h, _mapType, "OnEnable", null, "AfterEnable", null);
            Patch(h, _mapType, "Update", null, "AfterUpdate", null);
            Patch(h, _mapType, "UpdateMapFloor", null, "AfterFloor", null);
            Patch(h, _centerType, "CenterOn", "CenterOnPrefix", null, new Type[] { typeof(Transform) });
        }

        static void Patch(Harmony h, Type t, string method, string prefix, string postfix, Type[] args)
        {
            try
            {
                MethodInfo m = t == null ? null : AccessTools.Method(t, method, args, null);
                if (m == null) { Log("PATCH MISSING " + (t == null ? "?" : t.Name) + "." + method + " - the map keeps its vanilla window."); return; }
                h.Patch(m,
                    prefix == null ? null : new HarmonyMethod(typeof(EastMapPanel).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix == null ? null : new HarmonyMethod(typeof(EastMapPanel).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)),
                    null, null, null);
            }
            catch (Exception ex) { Log("PATCH FAILED " + method + ": " + ex.Message); }
        }

        static void AfterInit(Component __instance) { if (EastWorld.Extends) Apply(__instance, "init"); }

        static void AfterEnable(Component __instance)
        {
            if (EastWorld.Extends && _applied == __instance) Apply(__instance, null);
        }

        static void AfterUpdate(Component __instance)
        {
            if (!EastWorld.Extends) return;
            if (_applied != __instance || Screen.width != _layout.ScreenW || Screen.height != _layout.ScreenH)
            {
                Apply(__instance, _applied == __instance ? "screen " + Screen.width + "x" + Screen.height : "first frame");
                return;
            }
            try
            {
                KeepArtwork(__instance);
                Guard();
                ScrollInput();
            }
            catch (Exception ex)
            {
                if (!_frameFailed) { _frameFailed = true; Log("map frame failed: " + ex.Message); }
            }
        }
        static bool _frameFailed;

        /// <summary>UICenterOnChild.Recenter (its OnEnable) starts a
        /// SpringPanel on the clip panel without going through CenterOn: stop
        /// it and put the panel back where the scroll says.</summary>
        static void Guard()
        {
            if (_panel == null) return;
            Behaviour spring = _panel.GetComponent(RevivalPlugin.TypeByName("SpringPanel")) as Behaviour;
            bool moved = spring != null && spring.enabled;
            if (moved) spring.enabled = false;
            if (moved || _panel.transform.localPosition != _panelLocal) ApplyScroll();
        }
        static Vector3 _panelLocal;

        /// <summary>UpdateMapFloor puts the preset's picture back on the map
        /// texture; the surface one is replaced by the sharp east map again.
        /// An underground floor (a vanilla 5 x 5 km picture) is left alone.</summary>
        static void AfterFloor(Component __instance)
        {
            if (EastWorld.Extends && _applied == __instance) KeepArtwork(__instance);
        }

        /// <summary>MapUIManager.UpdateMapMarkers centres the scroll view on the
        /// player each time the map opens. For the map's own centerer this
        /// scrolls (vertically, clamped, whole pixels) so the player is in the
        /// middle of the view instead; every other UICenterOnChild runs.</summary>
        static bool CenterOnPrefix(Component __instance, Transform __0)
        {
            if (!(EastWorld.Extends && _centerer != null && __instance == _centerer)) return true;
            try
            {
                if (_layout.Scrolls && __0 != null && _dynamic != null)
                {
                    Vector3 local = _dynamic.InverseTransformPoint(__0.position);
                    float fromTop = _layout.MapH * .5f - local.y * _layout.Unit;
                    SetScroll(Mathf.RoundToInt(fromTop - _layout.ViewH * .5f));
                }
            }
            catch (Exception ex) { Log("centre on player failed: " + ex.Message); }
            return false;
        }

        /// <summary>SetMapLanguagePreset postfix (EastWorld.MapPresetPostfix,
        /// after MapInk.ApplyEastArtwork): which artwork and grid letters.</summary>
        internal static void ApplyPreset(Component manager, object preset)
        {
            if (!EastWorld.Extends || manager == null || preset == null) return;
            try
            {
                FieldInfo mapField = AccessTools.Field(preset.GetType(), "Map");
                Texture original = mapField == null ? null : mapField.GetValue(preset) as Texture;
                _presetMap = original;
                _ru = original != null && original.name.IndexOf("[RU]", StringComparison.OrdinalIgnoreCase) >= 0;
                if (_applied == manager) Apply(manager, "language " + (_ru ? "RU" : "EN"));
            }
            catch (Exception ex) { Log("language preset failed: " + ex.Message); }
        }

        // --------------------------------------------------------------- apply

        static Component _applied, _centerer, _panel, _texture;
        static Transform _root, _dynamic;
        static Rect _screen;
        static Layout _layout;
        static int _scroll;
        static bool _ru, _logged;
        static Texture _presetMap;
        static readonly List<Component> _icons = new List<Component>();

        /// <summary>How many map pixels a metre is now, over what it was
        /// (803 px per 5 km). The survival fog mask is sized in map pixels.</summary>
        internal static float FogScale = 1f;

        internal static void Apply(Component manager, string why)
        {
            if (manager == null) return;
            try
            {
                Transform root = manager.transform;
                Component texture = FieldComponent(manager, "MapTextureUI");
                Component panel = FieldComponent(manager, "MapPanel");
                if (texture == null || panel == null) return;     // before InitMapData
                Rect screen;
                if (!ScreenRect(root, out screen)) return;
                float unit = Screen.height / screen.height;
                Layout old = _layout;
                Layout l = Compute(Screen.width, Screen.height, unit);
                // Keep the same part of the map in view across a re-layout.
                float keep = old.MaxScroll > 0 ? _scroll / (float)old.MaxScroll : 0f;
                _layout = l; _screen = screen; _root = root;
                _applied = manager; _panel = panel; _texture = texture;
                _dynamic = texture.transform.parent;
                _scroll = Mathf.RoundToInt(keep * l.MaxScroll);

                // The clip panel: exactly the view, no soft edge (that fade
                // is what made the map's border soft), UIScrollView and its
                // spring off - the scroll below is ours, vertical only.
                Set(panel, "clipSoftness", Vector2.zero);
                Set(panel, "baseClipRegion", new Vector4(0f, 0f, l.MapW / unit, l.ViewH / unit));
                Behaviour scroll = panel.GetComponent(_scrollType) as Behaviour;
                if (scroll != null) { Call(scroll, "DisableSpring"); scroll.enabled = false; }
                Behaviour spring = panel.GetComponent(RevivalPlugin.TypeByName("SpringPanel")) as Behaviour;
                if (spring != null) spring.enabled = false;
                Component dynamicForms = FieldComponent(manager, "MapUIDynamicForms");
                if (dynamicForms != null && _widgetType.IsInstanceOfType(dynamicForms))
                    Size(dynamicForms, Mathf.RoundToInt(l.MapW / unit), Mathf.RoundToInt(l.MapH / unit));

                // The map: one widget unit = one screen pixel.
                Size(texture, l.MapW, l.MapH);
                texture.transform.localScale = new Vector3(1f / unit, 1f / unit, 1f);
                texture.transform.localPosition = new Vector3(0f, 0f, texture.transform.localPosition.z);
                BoxCollider box = texture.GetComponent<BoxCollider>();
                if (box != null) { box.size = new Vector3(l.MapW, l.MapH, 0f); box.center = Vector3.zero; }
                InvokeMap(manager, "InitMapSize");         // MAP_SIZE = the game's own formula
                FogScale = (l.MapW / unit / EastWorld.Extended.width) / (803f / 5000f);
                KeepArtwork(manager);

                // The grid, as UI.
                Component vanillaGrid = FieldComponent(manager, "GridTextureUI");
                int gridDepth = vanillaGrid == null ? 2 : (int)Get(vanillaGrid, "depth");
                if (vanillaGrid != null && vanillaGrid.gameObject.activeSelf) vanillaGrid.gameObject.SetActive(false);
                UiGrid.Build(texture, root, l, _ru ? ColumnsRu : ColumnsEn, gridDepth);

                // Everything around it.
                Transform form = Find(root, "FormUI");
                Component frame = form == null ? null : form.GetComponent(_textureType);
                Vector2 frameCentre = Units(l.FrameLeft + l.FrameW * .5f, l.FrameBottom + l.FrameH * .5f);
                float frameW = l.FrameW / unit, frameTop = Units(0f, l.FrameTop).y, frameRight = Units(l.FrameRight, 0f).x;
                if (frame != null)
                {
                    Texture2D art = Assets.Texture("east_map_form.png", false, false);
                    if (art != null)
                    {
                        Set(frame, "mainTexture", art);
                        SetEnum(frame, "type", "Sliced");
                        Set(frame, "border", FrameBorder);
                    }
                    Size(frame, Mathf.CeilToInt(l.FrameW / unit), Mathf.CeilToInt(l.FrameH / unit));
                    Place(root, form, frameCentre);
                }
                SetField(manager, "FakeMapBounds", new Bounds());                // recomputed on the next FogSizeCheck
                SetField(manager, "MapBounds", RelativeBounds(texture.transform));

                Transform labels = Find(root, "Labels");
                float k = frameW / VanillaFrameWidth;
                float legendY = frameTop - LegendBelowTop;
                Place(root, Find(root, "FormName"), new Vector2(frameCentre.x, frameTop - TitleBelowTop));
                Texture2D legendArt = Assets.Texture("east_map_legend.png", false, false);
                for (int i = 0; i < LegendNames.Length; i++)
                {
                    Transform label = Find(root, LegendNames[i]);
                    if (label == null) continue;
                    float iconX = frameCentre.x + LegendIconX[i] * k;
                    float offset = Original(label).x - LegendIconX[i];
                    Place(root, label, new Vector2(iconX + offset, legendY));
                    Component icon = Icon(labels, i, legendArt);
                    if (icon != null) Place(root, icon.transform, new Vector2(iconX, legendY + LegendIconDy[i]));
                }
                Vector2 corner = new Vector2(frameRight, frameTop);
                Place(root, Find(root, "Close"), corner + CloseFromCorner);
                Place(root, Find(root, "TeleportToggle"), corner + ToggleFromCorner);
                Component compass = FieldComponent(manager, "CompassTextureUI");
                if (compass != null) Place(root, compass.transform, Units(l.MapRight, l.ViewTop) + CompassFromCorner);
                Transform bar = Find(root, "FakeScrollBar");
                if (bar != null && bar.gameObject.activeSelf) bar.gameObject.SetActive(false);
                ScrollBar.Build(form == null ? root : form.parent, root, l, frame == null ? 1 : (int)Get(frame, "depth") + 1);

                _centerer = FieldComponent(manager, "PlayerMarkerCenterer");
                ApplyScroll();

                if (why != null && (!_logged || !why.StartsWith("init")))
                {
                    _logged = true;
                    Log("map " + l.MapW + " x " + l.MapH + " px (2:1, " + (l.MapW / 10000f).ToString("F4") + " px/m both axes), view "
                        + l.MapW + " x " + l.ViewH + (l.Scrolls ? " (scrolls vertically, " + l.MaxScroll + " px)" : " (no scrolling)")
                        + ", screen " + l.ScreenW + " x " + l.ScreenH + " at " + unit.ToString("F4") + " px per UI unit, artwork "
                        + (Art.Current == null ? "none" : Art.Current.width + " x " + Art.Current.height) + " (" + why + "). "
                        + Measure(texture));
                }
            }
            catch (Exception ex) { Log("map window layout failed: " + ex.Message); }
        }

        /// <summary>The sharp artwork on the map texture: built for this
        /// size and language if needed, put back if the game swapped the
        /// surface picture in.</summary>
        static void KeepArtwork(Component manager)
        {
            Component texture = _texture;
            if (texture == null || _layout.MapW <= 0) return;
            Texture shown = Get(texture, "mainTexture") as Texture;
            string file = _ru ? "east_map_ru.png" : "east_map_en.png";
            Texture2D art = Art.For(file, _layout.MapW, _layout.MapH);
            if (art == null || shown == art) return;
            bool surface = shown == null || shown == _presetMap || shown == Art.Source
                           || (shown.name != null && shown.name.StartsWith("east_map_", StringComparison.Ordinal))
                           || (shown.name != null && shown.name.StartsWith("NDR east map", StringComparison.Ordinal));
            if (surface) Set(texture, "mainTexture", art);
            else if (!_floorLogged) { _floorLogged = true; Log("underground floor '" + shown.name + "' shown as the game sets it."); }
        }
        static bool _floorLogged;

        // --------------------------------------------------------------- scroll

        static bool _dragging, _dragOnBar;
        static float _dragY;

        static void SetScroll(int s)
        {
            s = Mathf.Clamp(s, 0, Mathf.Max(0, _layout.MaxScroll));
            if (s == _scroll) return;
            _scroll = s;
            ApplyScroll();
        }

        /// <summary>Moves the clip panel the way UIScrollView does: the panel
        /// (and everything on the map) by the scroll, the clip region back by
        /// the same amount, so the view stays where it is. Whole pixels.</summary>
        static void ApplyScroll()
        {
            Layout l = _layout;
            if (_panel == null || _root == null) return;
            float mapCentreX = l.MapLeft + l.MapW * .5f;
            float mapCentreY = l.ViewTop + _scroll - l.MapH * .5f;
            float viewCentreY = l.ViewBottom + l.ViewH * .5f;
            Place(_root, _panel.transform, Units(mapCentreX, mapCentreY));
            Set(_panel, "clipOffset", new Vector2(0f, (viewCentreY - mapCentreY) / l.Unit));
            _panelLocal = _panel.transform.localPosition;
            // The survival fog's picture and mask frame follow the map.
            Component manager = _applied;
            FitMapWidget(_root, Find(_root, "fog"), l, mapCentreX, mapCentreY);
            Component fake = manager == null ? null : FieldComponent(manager, "SurvFogFakeMap");
            if (fake != null) FitMapWidget(_root, fake.transform, l, mapCentreX, mapCentreY);
            ScrollBar.Show(_root, l, _scroll);
            StayVisibleBand(manager, _panel);
        }

        static void ScrollInput()
        {
            Layout l = _layout;
            if (!l.Scrolls || !MapOpen()) { _dragging = false; return; }
            Vector3 mouse = Input.mousePosition;
            bool inView = mouse.x >= l.MapLeft && mouse.x < l.FrameRight && mouse.y >= l.ViewBottom && mouse.y < l.ViewTop;
            int s = _scroll;
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            // One notch (0.1) scrolls a sixth of the view.
            if (inView && wheel != 0f) s -= Mathf.RoundToInt(wheel * 10f * l.ViewH / 6f);
            if (Input.GetMouseButtonDown(0) && inView)
            {
                _dragging = true; _dragY = mouse.y; _dragOnBar = mouse.x >= l.MapRight;
            }
            if (!Input.GetMouseButton(0)) _dragging = false;
            if (_dragging)
            {
                if (_dragOnBar) s = ScrollBar.ScrollAt(l, mouse.y);
                else { s += Mathf.RoundToInt(mouse.y - _dragY); _dragY = mouse.y; }
            }
            SetScroll(s);
        }

        /// <summary>UIController.ShowMap writes _UI_General 8 while the map is
        /// open (RevivalMortar, MapTools).</summary>
        static bool MapOpen()
        {
            Type ui = RevivalPlugin.TypeByName("UIController");
            PropertyInfo inst = ui == null ? null : AccessTools.Property(ui, "Instance");
            object controller = inst == null ? null : inst.GetValue(null, null);
            FieldInfo general = ui == null ? null : AccessTools.Field(ui, "_UI_General");
            return controller != null && general != null && Convert.ToInt32(general.GetValue(controller)) == 8;
        }

        // ------------------------------------------------------------- screen

        static Camera UiCamera()
        {
            Type ui = RevivalPlugin.TypeByName("UIController");
            PropertyInfo inst = ui == null ? null : AccessTools.Property(ui, "Instance");
            object controller = inst == null ? null : inst.GetValue(null, null);
            FieldInfo camField = ui == null ? null : AccessTools.Field(ui, "UICam");
            return controller == null || camField == null ? null : camField.GetValue(controller) as Camera;
        }

        /// <summary>The screen, in the map root's own units, from the UI
        /// camera: right whatever UIRoot's scaling style and the aspect.</summary>
        static bool ScreenRect(Transform root, out Rect rect)
        {
            rect = new Rect();
            Camera cam = UiCamera();
            if (cam == null) return false;
            Vector3 a = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0f, 0f, 1f)));
            Vector3 b = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 1f, 1f)));
            rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return rect.width > 100f && rect.height > 100f;
        }

        /// <summary>A screen pixel position (origin bottom left) in root units.</summary>
        static Vector2 Units(float px, float py)
        {
            return new Vector2(_screen.xMin + px / _layout.Unit, _screen.yMin + py / _layout.Unit);
        }

        /// <summary>Where the map texture really is on the screen, from its
        /// world corners through the UI camera - the number to compare with
        /// a screenshot.</summary>
        static string Measure(Component texture)
        {
            try
            {
                Vector3[] c = Get(texture, "worldCorners") as Vector3[];
                Camera cam = UiCamera();
                if (c == null || cam == null) return "";
                Vector3 a = cam.WorldToScreenPoint(c[0]), b = cam.WorldToScreenPoint(c[2]);
                float w = b.x - a.x, h = b.y - a.y;
                string s = "On screen: map " + w.ToString("F1") + " x " + h.ToString("F1") + " px from ("
                           + a.x.ToString("F1") + ", " + a.y.ToString("F1") + ").";
                if (Mathf.Abs(w - 2f * h) > 1.5f) s += " NOT 2:1 - report this line.";
                return s;
            }
            catch (Exception ex) { return "measure failed: " + ex.Message; }
        }

        static void FitMapWidget(Transform root, Transform t, Layout l, float cx, float cy)
        {
            if (t == null) return;
            Component w = t.GetComponent(_widgetType);
            if (w != null) Size(w, Mathf.RoundToInt(l.MapW / l.Unit), Mathf.RoundToInt(l.MapH / l.Unit));
            Place(root, t, Units(cx, cy));
        }

        static void StayVisibleBand(Component manager, Component panel)
        {
            if (manager == null || panel == null) return;
            Type ui = RevivalPlugin.TypeByName("UIController");
            PropertyInfo inst = ui == null ? null : AccessTools.Property(ui, "Instance");
            object controller = inst == null ? null : inst.GetValue(null, null);
            FieldInfo rootField = ui == null ? null : AccessTools.Field(ui, "UIRoot");
            Component uiRoot = controller == null || rootField == null ? null : rootField.GetValue(controller) as Component;
            if (uiRoot == null) return;
            float scale = uiRoot.transform.localScale.y;
            Vector2 view = (Vector2)Call(panel, "GetViewSize");
            Vector2 soft = (Vector2)Get(panel, "clipSoftness");
            Vector2 offset = (Vector2)Get(panel, "clipOffset");
            FieldInfo stay = AccessTools.Field(_mapType, "StayVisibleOffset");
            int stayOffset = stay == null ? 12 : Convert.ToInt32(stay.GetValue(manager));
            SetStatic("YCenter", panel.transform.position.y + offset.y * scale);
            SetStatic("YClamp", (view.y * .5f - soft.y - stayOffset) * scale);
        }

        /// <summary>One legend icon widget, created once beside the labels.</summary>
        static Component Icon(Transform labels, int index, Texture2D sheet)
        {
            if (labels == null || sheet == null) return null;
            while (_icons.Count <= index) _icons.Add(null);
            Component icon = _icons[index];
            if (icon == null || icon.transform.parent != labels)
            {
                GameObject go = new GameObject("NDR legend icon " + index);
                go.layer = labels.gameObject.layer;
                go.transform.SetParent(labels, false);
                icon = go.AddComponent(_textureType);
                Set(icon, "mainTexture", sheet);
                Set(icon, "uvRect", new Rect(index / (float)LegendNames.Length, 0f, 1f / LegendNames.Length, 1f));
                Size(icon, Mathf.RoundToInt(LegendIconSize), Mathf.RoundToInt(LegendIconSize));
                Set(icon, "depth", 1);
                _icons[index] = icon;
            }
            return icon;
        }

        // ------------------------------------------------------------ artwork

        /// <summary>The map artwork at exactly the displayed pixel size.</summary>
        internal static class Art
        {
            internal static Texture2D Current, Source;
            static string _file;

            internal static Texture2D For(string file, int w, int h)
            {
                if (Current != null && _file == file && Current.width == w && Current.height == h) return Current;
                Texture2D src = Assets.Texture(file, false, false);
                if (src == null) return null;
                Color32[] pixels;
                try { pixels = src.GetPixels32(); }
                catch (Exception ex) { Log("artwork " + file + " not readable: " + ex.Message); return null; }
                DateTime t0 = DateTime.Now;
                Color32[] out_ = Resample(pixels, src.width, src.height, w, h);
                Texture2D t = new Texture2D(w, h, TextureFormat.RGB24, false);
                t.name = "NDR east map " + w + "x" + h;
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Point;
                t.anisoLevel = 0;
                t.SetPixels32(out_);
                t.Apply(false, true);                 // no mipmaps; drop the CPU copy
                if (Current != null && Current != t) UnityEngine.Object.Destroy(Current);
                Current = t; Source = src; _file = file;
                Log("artwork " + file + " " + src.width + " x " + src.height + " -> " + w + " x " + h + " (x"
                    + (w / (float)src.width).ToString("F3") + ", Catmull-Rom once, point-filtered 1:1) in "
                    + (DateTime.Now - t0).TotalMilliseconds.ToString("F0") + " ms.");
                return t;
            }
        }

        /// <summary>Separable resample of an opaque picture: Catmull-Rom
        /// (sharp, no ringing to speak of at these ratios), its support
        /// widened by the ratio when shrinking so every source pixel counts.
        /// Row order is kept (Unity's bottom-up rows stay bottom-up).</summary>
        internal static Color32[] Resample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            int[] xi, yi; float[] xw, yw;
            int xt = Weights(sw, dw, out xi, out xw);
            int yt = Weights(sh, dh, out yi, out yw);
            float[] tmp = new float[sh * dw * 3];
            for (int y = 0; y < sh; y++)
            {
                int row = y * sw, t = y * dw * 3;
                for (int x = 0; x < dw; x++, t += 3)
                {
                    float r = 0f, g = 0f, b = 0f;
                    int o = x * xt;
                    for (int k = 0; k < xt; k++)
                    {
                        float w = xw[o + k];
                        if (w == 0f) continue;
                        Color32 c = src[row + xi[o + k]];
                        r += c.r * w; g += c.g * w; b += c.b * w;
                    }
                    tmp[t] = r; tmp[t + 1] = g; tmp[t + 2] = b;
                }
            }
            Color32[] dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int o = y * yt, d = y * dw;
                for (int x = 0; x < dw; x++)
                {
                    float r = 0f, g = 0f, b = 0f;
                    for (int k = 0; k < yt; k++)
                    {
                        float w = yw[o + k];
                        if (w == 0f) continue;
                        int t = (yi[o + k] * dw + x) * 3;
                        r += tmp[t] * w; g += tmp[t + 1] * w; b += tmp[t + 2] * w;
                    }
                    dst[d + x] = new Color32(Byte(r), Byte(g), Byte(b), 255);
                }
            }
            return dst;
        }

        static byte Byte(float v) { return v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + .5f); }

        static int Weights(int n, int m, out int[] index, out float[] weight)
        {
            float ratio = m / (float)n;                       // output pixels per source pixel
            float shrink = ratio < 1f ? ratio : 1f;
            float support = 2f / shrink;
            int taps = (int)Math.Ceiling(support * 2f) + 1;
            index = new int[m * taps];
            weight = new float[m * taps];
            for (int j = 0; j < m; j++)
            {
                float centre = (j + .5f) / ratio - .5f;
                int first = (int)Math.Floor(centre - support) + 1;
                float sum = 0f;
                for (int k = 0; k < taps; k++)
                {
                    int i = first + k;
                    float w = CatmullRom((i - centre) * shrink);
                    index[j * taps + k] = i < 0 ? 0 : i >= n ? n - 1 : i;
                    weight[j * taps + k] = w;
                    sum += w;
                }
                if (sum != 0f) for (int k = 0; k < taps; k++) weight[j * taps + k] /= sum;
            }
            return taps;
        }

        static float CatmullRom(float x)
        {
            x = Math.Abs(x);
            if (x < 1f) return 1.5f * x * x * x - 2.5f * x * x + 1f;
            if (x < 2f) return -.5f * x * x * x + 2.5f * x * x - 4f * x + 2f;
            return 0f;
        }

        static Texture2D _white;

        /// <summary>A 2 x 2 opaque white texture, point-filtered: lines and
        /// the scroll bar are widgets of it.</summary>
        static Texture2D White()
        {
            if (_white != null) return _white;
            _white = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _white.name = "NDR white";
            _white.filterMode = FilterMode.Point;
            _white.wrapMode = TextureWrapMode.Clamp;
            _white.SetPixels32(new Color32[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
                                               new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) });
            _white.Apply(false, true);
            return _white;
        }

        // --------------------------------------------------------------- grid

        /// <summary>The 20 x 10 grid as widgets beside the map texture (a
        /// sibling like MapInkLayer: the click reads the texture's bounds with
        /// its children). Lines sit in a one-pixel-per-unit layer on whole
        /// pixels; labels in root units, sized in pixels, so NGUI renders
        /// their glyphs 1:1.</summary>
        internal static class UiGrid
        {
            static Transform _lines, _labels;
            static readonly List<Component> _lineWidgets = new List<Component>();
            static readonly List<Component> _labelWidgets = new List<Component>();
            static UnityEngine.Object _font;
            static string _fontProperty;

            internal static void Build(Component texture, Transform root, Layout l, string columns, int depth)
            {
                Transform parent = texture.transform.parent;
                _lines = Layer(_lines, "NDR map grid lines", parent, texture.transform, 1f / l.Unit);
                _labels = Layer(_labels, "NDR map grid labels", parent, texture.transform, 1f);
                float pxPerMetre = l.MapW / EastWorld.Extended.width;
                float cell = CellMetres * pxPerMetre;
                float left = (GridWest - EastWorld.Extended.xMin) * pxPerMetre;      // from the map's left edge
                float top = (EastWorld.Extended.yMax - GridNorth) * pxPerMetre;      // from the map's top edge
                int gridW = Mathf.RoundToInt(left + GridColumns * cell) - Mathf.RoundToInt(left);
                int gridH = Mathf.RoundToInt(top + GridRows * cell) - Mathf.RoundToInt(top);
                float halfW = l.MapW * .5f, halfH = l.MapH * .5f;
                Color line = new Color(1f, 1f, 1f, LineAlpha);
                int n = 0;
                for (int i = 0; i <= GridColumns; i++, n++)
                {
                    int x = Mathf.RoundToInt(left + i * cell);
                    float y = halfH - (Mathf.RoundToInt(top) + gridH * .5f);
                    // 2 units wide at half scale = exactly one pixel.
                    Line(n, x - halfW + .5f, y, 2, gridH, new Vector3(.5f, 1f, 1f), line, depth);
                }
                for (int j = 0; j <= GridRows; j++, n++)
                {
                    int y = Mathf.RoundToInt(top + j * cell);
                    float x = Mathf.RoundToInt(left) + gridW * .5f - halfW;
                    Line(n, x, halfH - y - .5f, gridW, 2, new Vector3(1f, .5f, 1f), line, depth);
                }
                for (int i = n; i < _lineWidgets.Count; i++) _lineWidgets[i].gameObject.SetActive(false);

                // Labels: the vanilla offset and size, scaled to the cell.
                if (!FindFont(root)) { Log("grid labels: no UI font found, lines only."); return; }
                float scale = cell / VanillaCell;
                float px = Mathf.Max(MinLabelPx, Mathf.Round(LabelPx * scale));
                int fontSize = Mathf.Max(4, Mathf.RoundToInt(px / l.Unit));
                Color colour = new Color(1f, 1f, 1f, LabelAlpha);
                int m = 0;
                for (int r = 0; r < GridRows; r++)
                    for (int c = 0; c < GridColumns; c++, m++)
                    {
                        float x = Mathf.RoundToInt(left + c * cell + LabelDx * scale) - halfW;
                        float y = halfH - Mathf.RoundToInt(top + r * cell + LabelDy * scale);
                        Label(m, columns[c].ToString() + (r + 1).ToString(), new Vector2(x / l.Unit, y / l.Unit),
                              fontSize, colour, depth + 1);
                    }
            }

            static Transform Layer(Transform layer, string name, Transform parent, Transform texture, float scale)
            {
                if (layer == null || layer.parent != parent)
                {
                    GameObject go = new GameObject(name);
                    go.layer = texture.gameObject.layer;
                    go.transform.SetParent(parent, false);
                    layer = go.transform;
                    if (name.EndsWith("lines")) _lineWidgets.Clear(); else _labelWidgets.Clear();
                }
                layer.localPosition = texture.localPosition;
                layer.localScale = new Vector3(scale, scale, 1f);
                return layer;
            }

            static void Line(int i, float x, float y, int w, int h, Vector3 scale, Color colour, int depth)
            {
                Component widget;
                if (i < _lineWidgets.Count) widget = _lineWidgets[i];
                else
                {
                    GameObject go = new GameObject("line");
                    go.layer = _lines.gameObject.layer;
                    go.transform.SetParent(_lines, false);
                    widget = go.AddComponent(_textureType);
                    Set(widget, "mainTexture", White());
                    _lineWidgets.Add(widget);
                }
                Size(widget, w, h);
                widget.transform.localScale = scale;
                widget.transform.localPosition = new Vector3(x, y, 0f);
                Set(widget, "color", colour);
                Set(widget, "depth", depth);
                if (!widget.gameObject.activeSelf) widget.gameObject.SetActive(true);
            }

            static void Label(int i, string text, Vector2 at, int fontSize, Color colour, int depth)
            {
                Component widget;
                if (i < _labelWidgets.Count) widget = _labelWidgets[i];
                else
                {
                    GameObject go = new GameObject("cell");
                    go.layer = _labels.gameObject.layer;
                    go.transform.SetParent(_labels, false);
                    widget = go.AddComponent(_labelType);
                    Set(widget, _fontProperty, _font);
                    Set(widget, "supportEncoding", false);
                    Set(widget, "maxLineCount", 1);
                    SetEnum(widget, "overflowMethod", "ResizeFreely");
                    SetEnum(widget, "pivot", "TopLeft");
                    SetEnum(widget, "effectStyle", "None");
                    _labelWidgets.Add(widget);
                }
                Set(widget, "fontSize", fontSize);
                Set(widget, "text", text);
                widget.transform.localPosition = new Vector3(at.x, at.y, 0f);
                Set(widget, "color", colour);
                Set(widget, "depth", depth);
                if (!widget.gameObject.activeSelf) widget.gameObject.SetActive(true);
            }

            /// <summary>The game's Roboto Light (the vanilla grid's face) if it
            /// is loaded, else the legend labels' own font.</summary>
            static bool FindFont(Transform root)
            {
                if (_font != null) return true;
                PropertyInfo ttf = AccessTools.Property(_labelType, "trueTypeFont");
                _fontProperty = ttf != null ? "trueTypeFont" : "dynamicFont";
                try
                {
                    PropertyInfo p = AccessTools.Property(_labelType, _fontProperty);
                    UnityEngine.Object[] fonts = p == null ? new UnityEngine.Object[0] : Resources.FindObjectsOfTypeAll(p.PropertyType);
                    for (int i = 0; i < fonts.Length && _font == null; i++)
                        if (fonts[i] != null && fonts[i].name.Replace(" ", "").ToLowerInvariant() == "roboto-light") _font = fonts[i];
                }
                catch (Exception) { }
                if (_font == null)
                {
                    Transform legend = Find(root, "TargetName");
                    Component label = legend == null ? null : legend.GetComponent(_labelType);
                    if (label != null)
                    {
                        _font = Get(label, "trueTypeFont") as UnityEngine.Object;
                        if (_font == null) { _font = Get(label, "bitmapFont") as UnityEngine.Object; _fontProperty = "bitmapFont"; }
                    }
                }
                if (_font != null) Log("grid labels in " + _font.name + ".");
                return _font != null;
            }
        }

        // ---------------------------------------------------------- scroll bar

        /// <summary>A thin bar in the frame's right border, shown only when the
        /// map scrolls: track the height of the view, thumb its share.</summary>
        internal static class ScrollBar
        {
            static Component _track, _thumb;

            internal static void Build(Transform parent, Transform root, Layout l, int depth)
            {
                if (parent == null) return;
                _track = Bar(_track, "NDR map scroll track", parent, new Color(0f, 0f, 0f, .55f), depth);
                _thumb = Bar(_thumb, "NDR map scroll thumb", parent, new Color(.78f, .76f, .68f, .9f), depth + 1);
                Show(root, l, _scroll);
            }

            static Component Bar(Component bar, string name, Transform parent, Color colour, int depth)
            {
                if (bar == null || bar.transform.parent != parent)
                {
                    GameObject go = new GameObject(name);
                    go.layer = parent.gameObject.layer;
                    go.transform.SetParent(parent, false);
                    bar = go.AddComponent(_textureType);
                    Set(bar, "mainTexture", White());
                }
                Set(bar, "color", colour);
                Set(bar, "depth", depth);
                return bar;
            }

            internal static float ThumbPx(Layout l)
            {
                return Mathf.Max(MinThumbPx, l.ViewH * (float)l.ViewH / l.MapH);
            }

            internal static int ScrollAt(Layout l, float mouseY)
            {
                float thumb = ThumbPx(l);
                float f = ((l.ViewTop - mouseY) - thumb * .5f) / Mathf.Max(1f, l.ViewH - thumb);
                return Mathf.RoundToInt(Mathf.Clamp01(f) * l.MaxScroll);
            }

            internal static void Show(Transform root, Layout l, int scroll)
            {
                if (_track == null || _thumb == null) return;
                bool on = l.Scrolls;
                if (_track.gameObject.activeSelf != on) _track.gameObject.SetActive(on);
                if (_thumb.gameObject.activeSelf != on) _thumb.gameObject.SetActive(on);
                if (!on) return;
                float x = l.MapRight + l.SidePx * .5f;
                int w = Mathf.Max(2, Mathf.RoundToInt(BarWidth));
                Size(_track, w, Mathf.Max(2, Mathf.RoundToInt(l.ViewH / l.Unit)));
                Place(root, _track.transform, Units(x, l.ViewBottom + l.ViewH * .5f));
                float thumb = ThumbPx(l);
                float f = l.MaxScroll > 0 ? scroll / (float)l.MaxScroll : 0f;
                float centre = l.ViewTop - thumb * .5f - f * (l.ViewH - thumb);
                Size(_thumb, w, Mathf.Max(2, Mathf.RoundToInt(thumb / l.Unit)));
                Place(root, _thumb.transform, Units(x, centre));
            }
        }

        // ------------------------------------------------------------- helpers

        static readonly Dictionary<Transform, Vector3> _original = new Dictionary<Transform, Vector3>();

        /// <summary>A transform's vanilla local position, remembered the first
        /// time this layout moves it.</summary>
        static Vector3 Original(Transform t)
        {
            Vector3 p;
            if (!_original.TryGetValue(t, out p)) { p = t.localPosition; _original[t] = p; }
            return p;
        }

        /// <summary>Puts a transform's origin at a point given in root units.</summary>
        static void Place(Transform root, Transform t, Vector2 at)
        {
            if (t == null) return;
            Original(t);
            Vector3 world = root.TransformPoint(new Vector3(at.x, at.y, 0f));
            Vector3 local = t.parent == null ? world : t.parent.InverseTransformPoint(world);
            t.localPosition = new Vector3(local.x, local.y, t.localPosition.z);
        }

        static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = Find(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        static Component FieldComponent(Component manager, string name)
        {
            FieldInfo f = AccessTools.Field(manager.GetType(), name);
            object v = f == null ? null : f.GetValue(manager);
            Component c = v as Component;
            if (c != null) return c;
            GameObject go = v as GameObject;
            return go == null ? null : go.GetComponent(_widgetType);
        }

        static void Size(Component widget, int w, int h)
        {
            Set(widget, "width", w);
            Set(widget, "height", h);
        }

        static object Get(object target, string name)
        {
            PropertyInfo p = AccessTools.Property(target.GetType(), name);
            if (p != null) return p.GetValue(target, null);
            FieldInfo f = AccessTools.Field(target.GetType(), name);
            return f == null ? null : f.GetValue(target);
        }

        static void Set(object target, string name, object value)
        {
            PropertyInfo p = AccessTools.Property(target.GetType(), name);
            if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
            SetField(target, name, value);
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo f = AccessTools.Field(target.GetType(), name);
            if (f != null) f.SetValue(target, value);
        }

        static void SetEnum(object target, string name, string value)
        {
            PropertyInfo p = AccessTools.Property(target.GetType(), name);
            if (p != null && p.CanWrite && p.PropertyType.IsEnum)
                p.SetValue(target, Enum.Parse(p.PropertyType, value), null);
        }

        static void SetStatic(string name, object value)
        {
            FieldInfo f = AccessTools.Field(_mapType, name);
            if (f != null) f.SetValue(null, value);
        }

        static object Call(object target, string name)
        {
            MethodInfo m = AccessTools.Method(target.GetType(), name, Type.EmptyTypes, null);
            return m == null ? null : m.Invoke(target, null);
        }

        static void InvokeMap(Component manager, string name)
        {
            MethodInfo m = AccessTools.Method(_mapType, name, Type.EmptyTypes, null);
            if (m != null) m.Invoke(manager, null);
        }

        static Bounds RelativeBounds(Transform t)
        {
            Type math = RevivalPlugin.TypeByName("NGUIMath");
            MethodInfo m = math == null ? null
                : AccessTools.Method(math, "CalculateRelativeWidgetBounds", new Type[] { typeof(Transform) }, null);
            return m == null ? new Bounds() : (Bounds)m.Invoke(null, new object[] { t });
        }

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastMapPanel: " + s); }
    }
}

// Revival.EastMapPanel.cs - the map window for the east world ([World] EastTile).
//
// The vanilla window (level7 HUD_MapUI, read 2026-09-24,
// docs/ai/tasks/east-map-panel.md) is a 850 x 600 frame (FormUI, map_form2
// with the six legend icons painted into its top band) around a clip panel
// (SoftnessPanel, UIPanel 797 x 510, softness 16, UIScrollView vertical) that
// scrolls a square 803 x 803 map texture (MapTextureUI, grid child 770 x 770).
// The east world is 10 x 5 km, so that square showed it squeezed.
//
// With the east world on, this lays the SAME objects out again: frame as wide
// as the screen (sliced east_map_form.png, borders 18/18/18/76), a 2:1 map
// inside it with no scrolling (the clip region is the map plus its softness,
// so NGUI's own disableDragIfFits keeps it still), the legend icons as six
// small widgets beside their labels, title, close button, teleport toggle and
// compass at their vanilla offsets from the new frame/map corners, and a 20 x
// 10 grid (east_map_grid_en/ru.png) whose west ten columns are the vanilla
// grid where it always was. When the height is the limit (anything wider than
// about 1.8:1) the window keeps its 2:1 map and stands centred between empty
// sides; narrower screens (16:10, 4:3, 5:4) get the full width and empty
// bands above and below, like the vanilla window's own margins.
//
// Every world <-> map conversion follows by itself: markers are placed with
// MAP_SIZE (InitMapSize is called again after the resize), clicks and
// MapTools use the texture's widget bounds, MapInk/MapLabels use the texture's
// corners. Only the player centerer (UICenterOnChild.CenterOn on map open)
// would move a map that now fits - its prefix skips that one instance.
//
// Off (switch off, or any other scene): EastWorld.Install never calls
// Install here, and every hook returns on !EastWorld.Extends first.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only.

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

        internal struct Layout
        {
            internal int MapW, MapH;
            internal float MapX, MapY;
            internal float FrameW, FrameH, FrameX, FrameY;
            internal float FrameTop { get { return FrameY + FrameH * .5f; } }
            internal float FrameRight { get { return FrameX + FrameW * .5f; } }
            internal float MapTop { get { return MapY + MapH * .5f; } }
            internal float MapRight { get { return MapX + MapW * .5f; } }
        }

        /// <summary>The window for a screen rectangle given in the map root's
        /// own units: a 2:1 map as wide as the screen allows under the band.
        /// Pure; research/east_map_panel_check.py executes it.</summary>
        internal static Layout Compute(Rect screen)
        {
            Layout l = new Layout();
            float byWidth = (screen.width - 2f * Side) * .5f;
            float byHeight = screen.height - Band - Bottom;
            l.MapH = Mathf.Max(16, Mathf.FloorToInt(Mathf.Min(byWidth, byHeight)));
            l.MapW = l.MapH * 2;
            l.FrameW = l.MapW + 2f * Side;
            l.FrameH = l.MapH + Band + Bottom;
            l.FrameX = Mathf.Round(screen.center.x);
            l.FrameY = Mathf.Round(screen.center.y);
            l.MapX = l.FrameX;
            l.MapY = l.FrameY - l.FrameH * .5f + Bottom + l.MapH * .5f;
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

        static Type _mapType, _textureType, _widgetType, _panelType, _scrollType, _centerType;

        internal static void Install(Harmony h)
        {
            _mapType = RevivalPlugin.TypeByName("MapUIManager");
            _textureType = RevivalPlugin.TypeByName("UITexture");
            _widgetType = RevivalPlugin.TypeByName("UIWidget");
            _panelType = RevivalPlugin.TypeByName("UIPanel");
            _scrollType = RevivalPlugin.TypeByName("UIScrollView");
            _centerType = RevivalPlugin.TypeByName("UICenterOnChild");
            Patch(h, _mapType, "InitMapData", null, "AfterInit", null);
            Patch(h, _mapType, "OnEnable", null, "AfterEnable", null);
            Patch(h, _mapType, "Update", null, "AfterUpdate", null);
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
            if (_applied != __instance || Screen.width != _screenW || Screen.height != _screenH)
                Apply(__instance, _applied == __instance ? "screen " + Screen.width + "x" + Screen.height : "first frame");
        }

        /// <summary>MapUIManager.UpdateMapMarkers centres the scroll view on the
        /// player each time the map opens. The whole map is visible now, and
        /// that spring would slide it off its place: skip it for the map's own
        /// centerer. Every other UICenterOnChild in the game is untouched.</summary>
        static bool CenterOnPrefix(Component __instance)
        {
            return !(EastWorld.Extends && _centerer != null && __instance == _centerer);
        }

        /// <summary>SetMapLanguagePreset postfix (EastWorld.MapPresetPostfix):
        /// the grid of the selected language.</summary>
        internal static void ApplyPreset(Component manager, object preset)
        {
            if (!EastWorld.Extends || manager == null || preset == null) return;
            try
            {
                FieldInfo mapField = AccessTools.Field(preset.GetType(), "Map");
                Texture original = mapField == null ? null : mapField.GetValue(preset) as Texture;
                bool ru = original != null && original.name.IndexOf("[RU]", StringComparison.OrdinalIgnoreCase) >= 0;
                Texture2D grid = Assets.Texture(ru ? "east_map_grid_ru.png" : "east_map_grid_en.png", false, true);
                Component widget = FieldComponent(manager, "GridTextureUI");
                if (grid != null && widget != null) Set(widget, "mainTexture", grid);
            }
            catch (Exception ex) { Log("grid artwork failed: " + ex.Message); }
        }

        // --------------------------------------------------------------- apply

        static Component _applied, _centerer;
        static int _screenW, _screenH;
        static Layout _layout;
        static bool _logged;
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
                Layout l = Compute(screen);
                _layout = l;
                _screenW = Screen.width; _screenH = Screen.height;
                _applied = manager;

                // The map: panel at the map centre, clip = map + softness,
                // nothing to scroll.
                Vector2 soft = (Vector2)Get(panel, "clipSoftness");
                Place(root, panel.transform, new Vector2(l.MapX, l.MapY));
                Set(panel, "clipOffset", Vector2.zero);
                Set(panel, "baseClipRegion", new Vector4(0f, 0f, l.MapW + 2f * soft.x, l.MapH + 2f * soft.y));
                Component scroll = panel.GetComponent(_scrollType);
                if (scroll != null)
                {
                    SetField(scroll, "disableDragIfFits", true);
                    Call(scroll, "DisableSpring");
                }
                Component spring = panel.GetComponent(RevivalPlugin.TypeByName("SpringPanel"));
                Behaviour springOn = spring as Behaviour;
                if (springOn != null) springOn.enabled = false;
                Component dynamicForms = FieldComponent(manager, "MapUIDynamicForms");
                if (dynamicForms != null && _widgetType.IsInstanceOfType(dynamicForms)) Size(dynamicForms, l.MapW, l.MapH);
                Size(texture, l.MapW, l.MapH);
                BoxCollider box = texture.GetComponent<BoxCollider>();
                if (box != null) { box.size = new Vector3(l.MapW, l.MapH, 0f); box.center = Vector3.zero; }
                texture.transform.localPosition = new Vector3(0f, 0f, texture.transform.localPosition.z);

                Component grid = FieldComponent(manager, "GridTextureUI");
                if (grid != null)
                {
                    float cell = CellMetres / EastWorld.Extended.width * l.MapW;
                    Size(grid, Mathf.RoundToInt(GridColumns * cell), Mathf.RoundToInt(GridRows * cell));
                    float left = (GridWest - EastWorld.Extended.center.x) / EastWorld.Extended.width * l.MapW;
                    grid.transform.localPosition = new Vector3(left + GridColumns * cell * .5f, 0f,
                                                               grid.transform.localPosition.z);
                }
                InvokeMap(manager, "InitMapSize");         // MAP_SIZE = the game's own formula
                FogScale = (l.MapW / EastWorld.Extended.width) / (803f / 5000f);

                // Everything around it.
                Transform form = Find(root, "FormUI");
                Component frame = form == null ? null : form.GetComponent(_textureType);
                if (frame != null)
                {
                    Texture2D art = Assets.Texture("east_map_form.png", false, false);
                    if (art != null)
                    {
                        Set(frame, "mainTexture", art);
                        SetEnum(frame, "type", "Sliced");
                        Set(frame, "border", FrameBorder);
                    }
                    Size(frame, Mathf.RoundToInt(l.FrameW), Mathf.RoundToInt(l.FrameH));
                    Place(root, form, new Vector2(l.FrameX, l.FrameY));
                }
                FitMapWidget(root, Find(root, "fog"), l);
                Component fake = FieldComponent(manager, "SurvFogFakeMap");
                if (fake != null) FitMapWidget(root, fake.transform, l);
                SetField(manager, "FakeMapBounds", new Bounds());                // recomputed on the next FogSizeCheck
                SetField(manager, "MapBounds", RelativeBounds(texture.transform));

                Transform labels = Find(root, "Labels");
                float k = l.FrameW / VanillaFrameWidth;
                float legendY = l.FrameTop - LegendBelowTop;
                Place(root, Find(root, "FormName"), new Vector2(l.FrameX, l.FrameTop - TitleBelowTop));
                Texture2D legendArt = Assets.Texture("east_map_legend.png", false, true);
                for (int i = 0; i < LegendNames.Length; i++)
                {
                    Transform label = Find(root, LegendNames[i]);
                    if (label == null) continue;
                    float iconX = l.FrameX + LegendIconX[i] * k;
                    float offset = Original(label).x - LegendIconX[i];
                    Place(root, label, new Vector2(iconX + offset, legendY));
                    Component icon = Icon(labels, i, legendArt);
                    if (icon != null) Place(root, icon.transform, new Vector2(iconX, legendY + LegendIconDy[i]));
                }
                Vector2 corner = new Vector2(l.FrameRight, l.FrameTop);
                Place(root, Find(root, "Close"), corner + CloseFromCorner);
                Place(root, Find(root, "TeleportToggle"), corner + ToggleFromCorner);
                Component compass = FieldComponent(manager, "CompassTextureUI");
                if (compass != null) Place(root, compass.transform, new Vector2(l.MapRight, l.MapTop) + CompassFromCorner);
                Transform bar = Find(root, "FakeScrollBar");
                if (bar != null && bar.gameObject.activeSelf) bar.gameObject.SetActive(false);

                // The player centerer, and the marker "stay visible" band,
                // recomputed the way UpdateMapMarkers does it.
                _centerer = FieldComponent(manager, "PlayerMarkerCenterer");
                StayVisibleBand(manager, panel);

                if (why != null && (!_logged || why.StartsWith("screen")))
                {
                    _logged = true;
                    Log("map window " + l.MapW + " x " + l.MapH + " (2:1) in a " + screen.width.ToString("F0") + " x "
                        + screen.height.ToString("F0") + " screen, frame " + l.FrameW + " x " + l.FrameH
                        + ", no scrolling (" + why + ").");
                }
            }
            catch (Exception ex) { Log("map window layout failed: " + ex.Message); }
        }

        /// <summary>The screen, in the map root's own units, from the UI
        /// camera: right whatever UIRoot's scaling style and the aspect.</summary>
        static bool ScreenRect(Transform root, out Rect rect)
        {
            rect = new Rect();
            Type ui = RevivalPlugin.TypeByName("UIController");
            PropertyInfo inst = ui == null ? null : AccessTools.Property(ui, "Instance");
            object controller = inst == null ? null : inst.GetValue(null, null);
            FieldInfo camField = ui == null ? null : AccessTools.Field(ui, "UICam");
            Camera cam = controller == null || camField == null ? null : camField.GetValue(controller) as Camera;
            if (cam == null) return false;
            Vector3 a = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0f, 0f, 1f)));
            Vector3 b = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 1f, 1f)));
            rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            return rect.width > 100f && rect.height > 100f;
        }

        static void FitMapWidget(Transform root, Transform t, Layout l)
        {
            if (t == null) return;
            Component w = t.GetComponent(_widgetType);
            if (w != null) Size(w, l.MapW, l.MapH);
            Place(root, t, new Vector2(l.MapX, l.MapY));
        }

        static void StayVisibleBand(Component manager, Component panel)
        {
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

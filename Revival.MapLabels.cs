using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Artwork coordinates keep placement and type size stable while panning or
    // zooming. Baked place names are not UILabels, so they need explicit bounds.
    internal sealed class MapLabelLayout
    {
        readonly List<Rect> occupied = new List<Rect>();
        readonly Dictionary<string, Vector2> offsets = new Dictionary<string, Vector2>();
        internal static readonly Rect[] PlaceNames = new Rect[] {
            new Rect(140, 90, 125, 34), new Rect(145, 160, 205, 38),
            new Rect(700, 94, 230, 38), new Rect(490, 230, 135, 38),
            new Rect(235, 335, 140, 38), new Rect(720, 360, 135, 38),
            new Rect(770, 441, 224, 38), new Rect(750, 525, 230, 38),
            new Rect(360, 537, 126, 38), new Rect(100, 781, 180, 38),
            new Rect(502, 758, 95, 38), new Rect(821, 861, 130, 38)
        };

        internal void Begin(bool overworld)
        {
            occupied.Clear();
            if (overworld)
                for (int i = 0; i < PlaceNames.Length; i++) Block(PlaceNames[i]);
        }

        internal void Block(Rect rect)
        {
            if (rect.width > 0f && rect.height > 0f) occupied.Add(rect);
        }

        internal void BlockLine(List<Vector2> points, float radius)
        {
            // Segment boxes protect the whole stroke, including gaps between
            // samples. Most route segments are only a few artwork pixels long.
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                Block(new Rect(Mathf.Min(a.x, b.x) - radius,
                    Mathf.Min(a.y, b.y) - radius, Mathf.Abs(b.x - a.x) + radius * 2f,
                    Mathf.Abs(b.y - a.y) + radius * 2f));
            }
        }

        bool Free(Rect rect)
        {
            if (rect.xMin < 3f || rect.yMin < 3f || rect.xMax > 1021f || rect.yMax > 1021f)
                return false;
            Rect padded = new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f);
            for (int i = 0; i < occupied.Count; i++)
                if (padded.Overlaps(occupied[i])) return false;
            return true;
        }

        internal bool Place(string key, Vector2 anchor, Vector2 size, out Rect result)
        {
            result = new Rect();
            if (size.x <= 0f || size.y <= 0f || size.x > 1018f || size.y > 1018f) return false;
            Vector2 previous;
            if (offsets.TryGetValue(key, out previous))
            {
                result = new Rect(anchor.x + previous.x, anchor.y + previous.y, size.x, size.y);
                if (Free(result)) { Block(result); return true; }
            }
            for (int ring = 0; ring < 5; ring++)
            {
                float gap = 10f + ring * 22f;
                // Prefer centered above/below, then either side and diagonals.
                for (int direction = 0; direction < 8; direction++)
                {
                    float x = anchor.x - size.x * .5f, y = anchor.y - gap - size.y;
                    if (direction == 1) y = anchor.y + gap;
                    if (direction >= 2)
                    {
                        x = (direction % 2 == 0) ? anchor.x + gap : anchor.x - gap - size.x;
                        y = direction < 4 ? anchor.y - size.y * .5f
                            : direction < 6 ? anchor.y - gap - size.y : anchor.y + gap;
                    }
                    result = new Rect(x, y, size.x, size.y);
                    if (!Free(result)) continue;
                    offsets[key] = result.position - anchor;
                    Block(result);
                    return true;
                }
            }
            // Keep the name available through the route's hover note. Never
            // shrink it into the old tiny type or paint it over another name.
            return false;
        }

        internal void Forget(string key) { offsets.Remove(key); }
    }

    // Native UILabel siblings share the map's panel and soft clipping, without
    // adding children to the source texture's picking/coordinate bounds.
    internal sealed class MapLabels : MonoBehaviour
    {
        sealed class Entry { internal Component Widget; internal int Frame; }
        static MapLabels instance;
        static Type labelType, widgetType;
        static UnityEngine.Object font;
        static float nextFontSearch;
        static bool warned;
        static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly List<string> stale = new List<string>();
        readonly MapLabelLayout layout = new MapLabelLayout();
        Component source;
        Rect full;
        Vector3 bottomLeft, topRight;
        int frame;

        static MemberInfo Member(object target, string name)
        {
            string key = target.GetType().FullName + "." + name;
            MemberInfo member;
            if (!members.TryGetValue(key, out member))
            {
                member = (MemberInfo)AccessTools.Property(target.GetType(), name)
                    ?? AccessTools.Field(target.GetType(), name);
                if (member == null) throw new MissingMemberException(key);
                members.Add(key, member);
            }
            return member;
        }

        static object Get(object target, string name)
        {
            MemberInfo member = Member(target, name);
            PropertyInfo p = member as PropertyInfo;
            return p != null ? p.GetValue(target, null) : ((FieldInfo)member).GetValue(target);
        }

        static void Set(object target, string name, object value)
        { ((PropertyInfo)Member(target, name)).SetValue(target, value, null); }

        static void SetEnum(object target, string name, string value)
        {
            PropertyInfo p = (PropertyInfo)Member(target, name);
            p.SetValue(target, Enum.Parse(p.PropertyType, value), null);
        }

        static bool FindFont()
        {
            if (font != null) return true;
            if (Time.unscaledTime < nextFontSearch) return false;
            nextFontSearch = Time.unscaledTime + 5f;
            labelType = RevivalPlugin.TypeByName("UILabel");
            widgetType = RevivalPlugin.TypeByName("UIWidget");
            PropertyInfo dynamicFont = labelType == null ? null : AccessTools.Property(labelType, "trueTypeFont");
            // NGUI versions use either trueTypeFont or the older dynamicFont.
            if (dynamicFont == null && labelType != null)
                dynamicFont = AccessTools.Property(labelType, "dynamicFont");
            if (dynamicFont == null || widgetType == null) return false;
            UnityEngine.Object[] fonts = Resources.FindObjectsOfTypeAll(dynamicFont.PropertyType);
            for (int i = 0; i < fonts.Length; i++)
            {
                string name = fonts[i].name.Replace("_", "").Replace(" ", "").ToLowerInvariant();
                if (name == "bebasneuecyrillic") { font = fonts[i]; break; }
                if (name == "bebasneuebold") font = fonts[i];
            }
            if (font == null && !warned)
            {
                warned = true;
                RevivalPlugin.L.LogWarning("Map labels: native Bebas font unavailable; route names remain in hover notes.");
            }
            return font != null;
        }

        internal static MapLabels Begin(Component texture, Camera camera, Rect mapRect, bool overworld)
        {
            if (!FindFont()) { Hide(); return null; }
            if (instance == null)
            {
                GameObject go = new GameObject("NDR map labels");
                go.hideFlags = HideFlags.HideAndDontSave;
                instance = go.AddComponent<MapLabels>();
            }
            MapLabels layer = instance;
            layer.source = texture;
            layer.full = mapRect;
            layer.frame = Time.frameCount;
            Vector3[] corners = (Vector3[])Get(texture, "localCorners");
            layer.bottomLeft = corners[0]; layer.topRight = corners[2];
            layer.Follow();
            Texture artwork = Get(texture, "mainTexture") as Texture;
            layer.layout.Begin(overworld && artwork != null
                && artwork.name.StartsWith("GW_Scene_1[", StringComparison.OrdinalIgnoreCase));
            layer.ReserveWidgets(camera);
            return layer;
        }

        void Follow()
        {
            Transform t = source.transform;
            transform.SetParent(t.parent, false);
            transform.localPosition = t.localPosition;
            transform.localRotation = t.localRotation;
            transform.localScale = t.localScale;
            gameObject.layer = source.gameObject.layer;
        }

        internal Vector2 Artwork(Vector2 screen)
        { return new Vector2((screen.x - full.x) * 1024f / full.width, (screen.y - full.y) * 1024f / full.height); }

        internal void BlockRoute(List<Vector2> localPoints, Vector2 screenOrigin)
        {
            List<Vector2> points = new List<Vector2>(localPoints.Count);
            for (int i = 0; i < localPoints.Count; i++) points.Add(Artwork(localPoints[i] + screenOrigin));
            layout.BlockLine(points, 3f);
        }

        internal void BlockScreen(Rect screen)
        {
            Vector2 start = Artwork(screen.position);
            layout.Block(new Rect(start.x, start.y, screen.width * 1024f / full.width,
                screen.height * 1024f / full.height));
        }

        internal void BlockOrbit(Vector2 centre, float rx, float ry)
        {
            List<Vector2> points = new List<Vector2>(65);
            for (int i = 0; i <= 64; i++)
            {
                float angle = i * Mathf.PI / 32f;
                points.Add(Artwork(centre + new Vector2(Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry)));
            }
            layout.BlockLine(points, 3f);
        }

        void ReserveWidgets(Camera camera)
        {
            // Player, quest, settlement and other native markers. Re-evaluate
            // their actual bounds while the map is open, including moved markers.
            if (source.transform.parent == null) return;
            Component[] widgets = source.transform.parent.GetComponentsInChildren(widgetType, false);
            for (int i = 0; i < widgets.Length; i++)
            {
                Component w = widgets[i];
                if (w == source || w.transform.IsChildOf(transform)) continue;
                // Route ink is reserved from the complete polyline below. Skip
                // its many pooled dash widgets, but retain the settlement ring.
                Transform p = w.transform.parent;
                if (p != null && p.name == "NDR map ink patrol") continue;
                Behaviour behaviour = w as Behaviour;
                if (behaviour != null && !behaviour.enabled) continue;
                if ((float)Get(w, "finalAlpha") <= .01f) continue;
                Vector3[] corners = (Vector3[])Get(w, "worldCorners");
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 max = new Vector2(float.MinValue, float.MinValue);
                for (int j = 0; j < corners.Length; j++)
                {
                    Vector3 s = camera.WorldToScreenPoint(corners[j]);
                    Vector2 a = Artwork(new Vector2(s.x, Screen.height - s.y));
                    min = Vector2.Min(min, a); max = Vector2.Max(max, a);
                }
                Rect rect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
                // Map-sized background widgets are not obstacles.
                if (rect.width < 900f && rect.height < 900f) layout.Block(rect);
            }
        }

        internal static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_' || c == '-' || char.IsWhiteSpace(c))
                { if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) text.Append(' ');
                text.Append(char.ToUpperInvariant(c));
            }
            string result = text.ToString().Trim();
            if (result.Length > 6 && result.EndsWith("PATROL", StringComparison.Ordinal)
                && result[result.Length - 7] != ' ')
                result = result.Insert(result.Length - 6, " ");
            return result;
        }

        internal bool Draw(string key, string text, Vector2 screenAnchor, bool enabled, Vector2 mouse)
        {
            Entry entry;
            if (!entries.TryGetValue(key, out entry))
            {
                GameObject go = new GameObject("route name");
                go.SetActive(false);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                entry = new Entry(); entry.Widget = go.AddComponent(labelType);
                Component w = entry.Widget;
                PropertyInfo p = AccessTools.Property(labelType, "trueTypeFont")
                    ?? AccessTools.Property(labelType, "dynamicFont");
                p.SetValue(w, font, null);
                Set(w, "fontSize", 30); // Native face: 21 px cap height on the 1024 map.
                Set(w, "supportEncoding", false);
                Set(w, "maxLineCount", 1);
                SetEnum(w, "overflowMethod", "ResizeFreely");
                SetEnum(w, "pivot", "Center");
                SetEnum(w, "effectStyle", "None");
                entries.Add(key, entry);
            }
            entry.Frame = frame;
            Component widget = entry.Widget;
            Set(widget, "text", text);
            Vector2 size = (Vector2)Get(widget, "printedSize");
            Rect bounds;
            if (!layout.Place(key, Artwork(screenAnchor), size, out bounds))
            { widget.gameObject.SetActive(false); return false; }
            float sx = (topRight.x - bottomLeft.x) / 1024f;
            float sy = (topRight.y - bottomLeft.y) / 1024f;
            widget.transform.localScale = new Vector3(sx, sy, 1f);
            widget.transform.localPosition = new Vector3(bottomLeft.x + bounds.center.x * sx,
                topRight.y - bounds.center.y * sy, 0f);
            Color sourceColor = (Color)Get(source, "color");
            Set(widget, "color", new Color(.64f, .61f, .51f, sourceColor.a * (enabled ? .9f : .42f)));
            Set(widget, "depth", (int)Get(source, "depth") + 3);
            widget.gameObject.SetActive(true);
            return bounds.Contains(Artwork(mouse));
        }

        internal void End()
        {
            stale.Clear();
            foreach (KeyValuePair<string, Entry> pair in entries)
                if (pair.Value.Frame != frame)
                { Destroy(pair.Value.Widget.gameObject); layout.Forget(pair.Key); stale.Add(pair.Key); }
            for (int i = 0; i < stale.Count; i++) entries.Remove(stale[i]);
        }

        internal static void Hide()
        {
            if (instance == null) return;
            foreach (Entry entry in instance.entries.Values) entry.Widget.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (source == null) { Destroy(gameObject); return; }
            Follow();
            Behaviour behaviour = source as Behaviour;
            if (!source.gameObject.activeInHierarchy || (behaviour != null && !behaviour.enabled)
                || Time.frameCount - frame > 1) Hide();
        }
    }
}

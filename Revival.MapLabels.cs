using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Artwork coordinates keep placement and type size stable while panning or
    // zooming. Baked place names are not UILabels, so they need explicit bounds.
    internal sealed class MapLabelLayout
    {
        // Where a name sat last frame. A ROAD name remembers the arc FRACTION
        // of its own line plus the offset from that point, because the line is
        // re-projected every frame: a remembered artwork offset alone would
        // walk away from the road as soon as the map is panned or zoomed.
        sealed class Spot
        {
            internal bool OnRoad;
            internal float T;
            internal Vector2 Offset;
        }

        const float CellSize = 64f;

        readonly List<Rect> occupied = new List<Rect>();
        // Coarse hash of the occupied rectangles. One route contributes a box
        // per dash sample, so a linear scan would cost tens of thousands of
        // overlap tests for every name in every frame the map is open.
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        readonly Dictionary<string, Spot> spots = new Dictionary<string, Spot>();
        // Places the PLAYER chose, in artwork coordinates. The search below is
        // a guess about what reads well; a place given here is not, so it wins
        // over the guess, over the remembered spot, and over any collision.
        readonly Dictionary<string, Vector2> pins = new Dictionary<string, Vector2>();
        // One uniform walk of the road currently being labelled. Reused, so the
        // search allocates nothing per frame.
        readonly List<Vector2> walk = new List<Vector2>();
        float walkStep = 1f;
        bool walkClosed;

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
            cells.Clear();
            if (overworld)
                for (int i = 0; i < PlaceNames.Length; i++) Block(PlaceNames[i]);
        }

        // Nothing outside the artwork can block an in-bounds name, so a far
        // panned marker is clamped into an edge cell instead of hashing a
        // million rows of empty grid.
        static int Cell(float value)
        {
            int index = (int)Mathf.Floor(value / CellSize);
            int last = EastWorld.Extends ? 32 : 16;     // the east world's picture is 2048 wide
            return index < -1 ? -1 : (index > last ? last : index);
        }

        static long Key(int cx, int cy) { return ((long)(cx + 1024)) * 4096L + (cy + 1024); }

        internal void Block(Rect rect)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            int index = occupied.Count;
            occupied.Add(rect);
            int lastX = Cell(rect.xMax), lastY = Cell(rect.yMax);
            for (int cx = Cell(rect.xMin); cx <= lastX; cx++)
                for (int cy = Cell(rect.yMin); cy <= lastY; cy++)
                {
                    long key = Key(cx, cy);
                    List<int> bucket;
                    if (!cells.TryGetValue(key, out bucket))
                    { bucket = new List<int>(); cells.Add(key, bucket); }
                    bucket.Add(index);
                }
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
            int lastX = Cell(padded.xMax), lastY = Cell(padded.yMax);
            for (int cx = Cell(padded.xMin); cx <= lastX; cx++)
                for (int cy = Cell(padded.yMin); cy <= lastY; cy++)
                {
                    List<int> bucket;
                    if (!cells.TryGetValue(Key(cx, cy), out bucket)) continue;
                    for (int i = 0; i < bucket.Count; i++)
                        if (padded.Overlaps(occupied[bucket[i]])) return false;
                }
            return true;
        }

        // How much open artwork surrounds a candidate, in three steps. A name
        // standing in a clearing reads at a glance; one wedged into the gap
        // between two roads does not, although both of them "fit".
        int Clearance(Rect rect)
        {
            for (int level = 0; level < 3; level++)
            {
                float grow = 8f + level * 10f;
                if (!Free(new Rect(rect.x - grow, rect.y - grow,
                                   rect.width + grow * 2f, rect.height + grow * 2f)))
                    return level;
            }
            return 3;
        }

        // Re-samples a road at one uniform spacing in artwork pixels, so every
        // candidate below is index arithmetic instead of another pass over the
        // thousand-odd dash points of a long route. At most 256 stations.
        void Walk(List<Vector2> road)
        {
            walk.Clear();
            float length = 0f;
            for (int i = 1; i < road.Count; i++) length += (road[i] - road[i - 1]).magnitude;
            if (length <= 0f) return;
            walkClosed = (road[road.Count - 1] - road[0]).magnitude < 8f;
            walkStep = Mathf.Max(6f, length / 255f);
            walk.Add(road[0]);
            float next = walkStep, walked = 0f;
            for (int i = 1; i < road.Count; i++)
            {
                Vector2 a = road[i - 1], b = road[i];
                float segment = (b - a).magnitude;
                while (segment > 0f && next <= walked + segment)
                {
                    float f = (next - walked) / segment;
                    walk.Add(new Vector2(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f));
                    next += walkStep;
                }
                walked += segment;
            }
        }

        // A closed road wraps, an open one stops at its end points.
        Vector2 At(int index)
        {
            int last = walk.Count - 1;
            if (last <= 0) return walk[0];
            if (walkClosed)
            {
                index %= last;
                return walk[index < 0 ? index + last : index];
            }
            return walk[index < 0 ? 0 : (index > last ? last : index)];
        }

        internal void ClearPins() { pins.Clear(); }

        internal void SetPin(string key, Vector2 artwork)
        { if (!string.IsNullOrEmpty(key)) pins[key] = artwork; }

        internal bool HasPin(string key)
        { return !string.IsNullOrEmpty(key) && pins.ContainsKey(key); }

        // A pinned name keeps its full size and stays inside the artwork; the
        // clamp is the only thing that may move it away from the given point.
        static float Fit(float min, float extent, float far)
        { return Mathf.Max(3f, Mathf.Min(min, far - extent)); }

        internal bool Place(string key, Vector2 anchor, Vector2 size, out Rect result)
        { return Place(key, null, anchor, size, out result); }

        /// <summary>Finds a spot for one name. <paramref name="road"/> is the
        /// labelled line itself in artwork coordinates. With it the name is set
        /// BESIDE the open, straight part of its own road, which is what a map
        /// reader looks for, instead of clinging to the first waypoint - that
        /// is merely where the recorder happened to press the button, and it is
        /// as often as not in a town, on a marker, or in a hairpin.</summary>
        internal bool Place(string key, List<Vector2> road, Vector2 anchor,
                            Vector2 size, out Rect result)
        {
            result = new Rect();
            if (size.x <= 0f || size.y <= 0f || size.x > 1018f || size.y > 1018f) return false;
            Vector2 pin;
            if (pins.TryGetValue(key, out pin))
            {
                result = new Rect(Fit(pin.x - size.x * .5f, size.x, EastWorld.Extends ? 2045f : 1021f),
                                  Fit(pin.y - size.y * .5f, size.y, 1021f), size.x, size.y);
                Block(result);
                return true;
            }
            walk.Clear();
            walkClosed = false;
            if (road != null && road.Count >= 2) Walk(road);
            bool onRoad = walk.Count >= 2;

            Spot previous;
            if (spots.TryGetValue(key, out previous) && (onRoad || !previous.OnRoad))
            {
                Vector2 from = previous.OnRoad
                    ? At((int)(previous.T * (walk.Count - 1) + .5f)) : anchor;
                result = new Rect(from.x + previous.Offset.x, from.y + previous.Offset.y,
                                  size.x, size.y);
                if (Free(result)) { Block(result); return true; }
            }
            if (onRoad && Beside(key, size, out result)) { Block(result); return true; }
            return Around(key, anchor, size, out result);
        }

        // Stations down the whole length of the road, both sides, three
        // distances - and the most open one wins. Every candidate stands clear
        // of the line by the label's own extent in that direction, so the gap
        // is a real gap on any bearing.
        bool Beside(string key, Vector2 size, out Rect result)
        {
            result = new Rect();
            int last = walk.Count - 1;
            int stride = last / 24;
            if (stride < 1) stride = 1;
            int reach = (int)Mathf.Max(1f, 30f / walkStep);
            bool found = false;
            float best = 0f, bestT = 0f;
            Vector2 bestFrom = new Vector2();
            for (int index = 0; index <= last; index += stride)
            {
                Vector2 point = At(index);
                Vector2 chord = At(index + reach) - At(index - reach);
                float span = chord.magnitude;
                if (span < .001f) continue;
                float dirX = chord.x / span, dirY = chord.y / span;
                // 1 along a straight stretch, 0 inside a hairpin: a name set
                // beside a straight road reads as belonging to that road.
                float straight = Mathf.Clamp01(span / (2f * reach * walkStep));
                // The name belongs on the BODY of an open route, not at its
                // tip. A closed ring has no tip, so every station is body.
                float body = walkClosed ? 1f : 1f - Mathf.Abs(2f * index / (float)last - 1f);
                for (int side = 0; side < 2; side++)
                {
                    float nx = side == 0 ? -dirY : dirY;
                    float ny = side == 0 ? dirX : -dirX;
                    float half = Mathf.Abs(nx) * size.x * .5f + Mathf.Abs(ny) * size.y * .5f;
                    for (int ring = 0; ring < 3; ring++)
                    {
                        float gap = 9f + ring * 15f + half;
                        Rect candidate = new Rect(point.x + nx * gap - size.x * .5f,
                                                  point.y + ny * gap - size.y * .5f,
                                                  size.x, size.y);
                        if (!Free(candidate)) continue;
                        float score = Clearance(candidate) * 4f + straight * 3f
                                    + body * 2f - ring * 1.5f;
                        if (found && score <= best) continue;
                        found = true; best = score; result = candidate;
                        bestFrom = point; bestT = last > 0 ? index / (float)last : 0f;
                    }
                }
            }
            if (!found) return false;
            Spot spot = new Spot();
            spot.OnRoad = true;
            spot.T = bestT;
            spot.Offset = new Vector2(result.x - bestFrom.x, result.y - bestFrom.y);
            spots[key] = spot;
            return true;
        }

        // The fallback for a name with no usable line: a route of one waypoint,
        // or a road whose every station is taken. Unchanged from the first
        // placement pass, so such a name never ends up worse than before.
        bool Around(string key, Vector2 anchor, Vector2 size, out Rect result)
        {
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
                    Spot spot = new Spot();
                    spot.Offset = new Vector2(result.x - anchor.x, result.y - anchor.y);
                    spots[key] = spot;
                    Block(result);
                    return true;
                }
            }
            // Keep the name available through the route's hover note. Never
            // shrink it into the old tiny type or paint it over another name.
            result = new Rect();
            return false;
        }

        internal void Forget(string key) { spots.Remove(key); }
    }

    // Native UILabel siblings share the map's panel and soft clipping, without
    // adding children to the source texture's picking/coordinate bounds.
    internal sealed class MapLabels : MonoBehaviour
    {
        // Bounds/Placed remember what the last placement search decided, so a
        // frame that only re-arms the name can answer the cursor test without
        // running that search again.
        sealed class Entry
        {
            internal Component Widget;
            internal int Frame;
            internal Rect Bounds;
            internal bool Placed;
        }
        static MapLabels instance;
        static Type labelType, widgetType;
        static UnityEngine.Object font;
        static float nextFontSearch;
        static bool warned;
        static ConfigEntry<string> places;
        static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        // The displayed line of each labelled route, in artwork coordinates.
        // Submitted with the ink, before any name is placed, so the placement
        // pass can set a route's name beside the route's own road.
        readonly Dictionary<string, List<Vector2>> roads = new Dictionary<string, List<Vector2>>();
        readonly List<string> stale = new List<string>();
        readonly MapLabelLayout layout = new MapLabelLayout();
        Component source;
        Rect full;
        Vector3 bottomLeft, topRight;
        int frame;
        // The Places line this layer has already read into the layout.
        string parsedPlaces;

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

        /// <summary>The automatic placement below is a GUESS about what reads
        /// well on a map it cannot see. Where that guess is wrong, this line
        /// says where a name belongs, and it says so without a new build.</summary>
        internal static void BindConfig(ConfigFile cfg)
        {
            places = cfg.Bind("MapLabels", "Places", "",
                "Fixed places for individual map names, for wherever the "
                + "automatic placement picks a poor spot. Semicolon-separated "
                + "\"RouteName=x,y\" pairs in the map picture's own pixels: 0,0 "
                + "is its top left corner, 1024,1024 its bottom right, and the "
                + "name is centred on the point. A name listed here keeps that "
                + "place whatever else stands there, and the automatic names "
                + "give way to it. Empty = place every name automatically. "
                + "Example: LocatorPatrol=430,560;CivPatrol=120,700");
        }

        internal bool Pinned(string key) { return layout.HasPin(key); }

        // Re-read only when the line actually changed. The map is redrawn every
        // frame it is open, and the .cfg can be edited while the game runs.
        void ReadPlaces()
        {
            string text = places == null || places.Value == null ? "" : places.Value;
            if (text == parsedPlaces) return;
            parsedPlaces = text;
            layout.ClearPins();
            string[] entries = text.Split(new char[] { ';', '\n' });
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i].Trim();
                if (entry.Length == 0) continue;
                int eq = entry.IndexOf('=');
                string[] pair = eq <= 0 ? new string[0]
                    : entry.Substring(eq + 1).Split(new char[] { ',' });
                float x = 0f, y = 0f;
                if (pair.Length != 2
                    || !float.TryParse(pair[0].Trim(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(pair[1].Trim(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out y))
                {
                    RevivalPlugin.L.LogWarning("Map labels: cannot read the place \""
                        + entry + "\". One entry is NAME=x,y in map pixels.");
                    continue;
                }
                layout.SetPin(entry.Substring(0, eq).Trim(), new Vector2(x, y));
            }
        }

        /// <summary>Is there a layer whose last placement can be re-armed? A
        /// caller that wants to skip the placement search has to build once
        /// before there is anything to skip.</summary>
        internal static bool HasLayer { get { return instance != null; } }

        /// <summary>
        /// Opens the layer for this frame. <paramref name="reuse"/> keeps the
        /// placement the last build produced: the obstacle grid, the reserved
        /// road lines and every name stay exactly as they are, and only the
        /// panel-relative bookkeeping is refreshed so the names travel with a
        /// panned or zoomed map. The search itself - a few hundred rectangle
        /// tests per name against a grid holding one box per road point - is
        /// what made this the most expensive thing the toolkit did per frame,
        /// and it only ever produces a different answer when the routes, the
        /// map scale or the panel's alpha change.
        /// </summary>
        internal static MapLabels Begin(Component texture, Camera camera, Rect mapRect,
                                        bool overworld, bool reuse)
        {
            if (!FindFont()) { Hide(); return null; }
            if (instance == null)
            {
                GameObject go = new GameObject("NDR map labels");
                go.hideFlags = HideFlags.HideAndDontSave;
                instance = go.AddComponent<MapLabels>();
                reuse = false;              // nothing placed yet to re-arm
            }
            MapLabels layer = instance;
            layer.source = texture;
            layer.full = mapRect;
            layer.frame = Time.frameCount;
            Vector3[] corners = (Vector3[])Get(texture, "localCorners");
            layer.bottomLeft = corners[0]; layer.topRight = corners[2];
            layer.Follow();
            if (reuse) return layer;
            layer.ReadPlaces();
            Texture artwork = Get(texture, "mainTexture") as Texture;
            // The east artwork's west half is GW_Scene_1's picture, at the same
            // artwork pixels in the 2048-wide frame: its baked names block too.
            layer.layout.Begin(overworld && artwork != null
                && (EastWorld.Extends
                    || artwork.name.StartsWith("GW_Scene_1[", StringComparison.OrdinalIgnoreCase)));
            layer.roads.Clear();
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

        /// <summary>Width of the artwork frame: 1024 like its height, or 2048
        /// for the east world's 2:1 map - one artwork pixel is square either
        /// way, so names are neither squeezed nor stretched.</summary>
        static float ArtWidth { get { return EastWorld.Extends ? 2048f : 1024f; } }

        internal Vector2 Artwork(Vector2 screen)
        { return new Vector2((screen.x - full.x) * ArtWidth / full.width, (screen.y - full.y) * 1024f / full.height); }

        // How far apart the kept points of a reserved road are, in artwork
        // pixels. The line arrives at rather less than one pixel per point (it
        // is sampled every four metres of road), and one reserved rectangle per
        // point put four figures' worth of boxes into the grid for a single
        // route - which every later name then had to be tested against. Six is
        // the spacing the road walk below re-samples to anyway, so nothing that
        // reads the reserved line loses resolution, and the boxes still cover
        // the whole stroke because each spans from one kept point to the next.
        const float BlockStep = 6f;

        internal void BlockRoute(string key, List<Vector2> localPoints, Vector2 screenOrigin)
        {
            List<Vector2> points = new List<Vector2>(localPoints.Count / 4 + 2);
            Vector2 last = Vector2.zero;
            for (int i = 0; i < localPoints.Count; i++)
            {
                Vector2 a = Artwork(localPoints[i] + screenOrigin);
                if (points.Count > 0 && i < localPoints.Count - 1
                    && (a - last).sqrMagnitude < BlockStep * BlockStep) continue;
                points.Add(a);
                last = a;
            }
            layout.BlockLine(points, 3f);
            // The same line guides the route's own name below.
            if (!string.IsNullOrEmpty(key)) roads[key] = points;
        }

        internal void BlockScreen(Rect screen)
        {
            Vector2 start = Artwork(screen.position);
            layout.Block(new Rect(start.x, start.y, screen.width * ArtWidth / full.width,
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
            List<Vector2> road;
            roads.TryGetValue(key, out road);
            entry.Placed = false;
            if (!layout.Place(key, road, Artwork(screenAnchor), size, out bounds))
            { widget.gameObject.SetActive(false); return false; }
            entry.Bounds = bounds;
            entry.Placed = true;
            float sx = (topRight.x - bottomLeft.x) / ArtWidth;
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

        /// <summary>
        /// Re-arms a name the last build placed: the widget keeps the spot the
        /// placement search gave it - it is positioned in the picture's own
        /// coordinates and travels with the panel - and only the cursor is
        /// tested against it again. Returns false for a name this layer does
        /// not know or could not place, so the caller falls back to the route's
        /// own hover spot exactly as it does after a failed placement.
        /// </summary>
        internal bool Keep(string key, Vector2 mouse)
        {
            Entry entry;
            if (!entries.TryGetValue(key, out entry)) return false;
            entry.Frame = frame;
            return entry.Placed && entry.Bounds.Contains(Artwork(mouse));
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

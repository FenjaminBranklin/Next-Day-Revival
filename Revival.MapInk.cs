using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // Map-only geometry and cached coverage masks. No waypoint is ever changed.
    internal static class MapInk
    {
        // One style for every route, in SCREEN pixels: the map zooms, the ink
        // does not. Dashes are rebuilt for the zoom they are shown at, so a
        // dash is 22 px long and 4.75 px wide however far the map is zoomed.
        // The coloured core carries a thin dark rim for contrast on the pale
        // parts of the artwork; dash ends are flat with slightly rounded
        // corners, never round caps.
        internal const float DashLength = 22f;
        internal const float GapLength = 12f;
        internal const float StrokeWidth = 4.75f;
        internal const float OutlineWidth = 1.1f;
        internal const float OutlineAlpha = 0.6f;
        internal const float CornerRadius = 1.2f;
        // Path corners are rounded by smoothing the projected line; no point
        // moves more than this many pixels off the road it follows.
        internal const float PathRound = 3f;
        // A small chevron sits in one gap roughly every ArrowEvery pixels and
        // points the way the route is driven (waypoint order; the editor's
        // "reverse" is applied to the order when the route is written).
        internal const float ArrowEvery = 200f;
        internal const float ArrowLength = 4f;
        internal const float ArrowHalf = 3.6f;
        internal const float ArrowStroke = 1.9f;
        // Zoom is quantised to eighth-octave steps (about 9 percent), so a zoom
        // rebuilds the masks once per step and a dash stays within 4.5 percent
        // of its nominal screen length in between.
        internal const int ZoomSteps = 8;
        const int Density = 2;

        internal sealed class Dash
        {
            internal Rect Bounds;
            internal Vector2 Mid;
            internal List<Vector2> Points;
            internal Texture2D Texture;
            internal bool Arrow;
        }

        internal sealed class Cache
        {
            internal List<Vector3> Source;
            internal List<Dash> Dashes = new List<Dash>();
            internal int Seen;
            internal int Zoom;
            internal float Aspect;
            internal bool Arrows;
        }

        static readonly Dictionary<string, Cache> Caches = new Dictionary<string, Cache>();
        static readonly List<string> Retired = new List<string>();
        static int Frame;
        const float VanillaArt = 1024f;
        const float EastArtWidth = 2048f;
        const string EastMapEn = "east_map_en.png";
        const string EastMapRu = "east_map_ru.png";
        static bool _artLogged;

        internal static void Begin() { Frame++; }

        internal static void End()
        {
            Retired.Clear();
            foreach (KeyValuePair<string, Cache> entry in Caches)
                if (entry.Value.Seen != Frame) Retired.Add(entry.Key);
            for (int i = 0; i < Retired.Count; i++)
            {
                Release(Caches[Retired[i]]);
                Caches.Remove(Retired[i]);
            }
        }

        static void Release(Cache cache)
        {
            for (int i = 0; i < cache.Dashes.Count; i++)
                if (cache.Dashes[i].Texture != null)
                    UnityEngine.Object.Destroy(cache.Dashes[i].Texture);
        }

        internal static Vector2 Artwork(Vector3 p)
        {
            if (!EastWorld.Extends)
                return new Vector2(p.x * (1024f / 5000f) * 1.005f + 514f,
                                  -p.z * (1024f / 5000f) * 1.005f + 508f);
            // East world: the 2:1 artwork is registered exactly on the world
            // rectangle (research/east_map.py resamples the vanilla half onto
            // it), and this frame is 2048 x 1024 - the same 1024 px per 5 km
            // on both axes, so dashes are rasterised round. Get() hands its
            // masks to MapInkLayer in the layer's 1024-wide frame (OutX).
            Rect w = EastWorld.Extended;
            return new Vector2((p.x - w.xMin) / w.width * EastArtWidth,
                               (w.yMax - p.z) / w.height * VanillaArt);
        }

        static Vector3 DisplayWorld(Vector2 p, float width)
        {
            if (!EastWorld.Extends)
                return new Vector3((p.x - 514f) / 1.005f * (5000f / 1024f),
                                   width, -(p.y - 508f) / 1.005f * (5000f / 1024f));
            Rect w = EastWorld.Extended;
            return new Vector3(w.xMin + p.x / EastArtWidth * w.width, width,
                               w.yMax - p.y / VanillaArt * w.height);
        }

        /// <summary>A point of MapInkRoads' road artwork (the vanilla picture's
        /// 1024 px, whatever the switch) in the world. Off, this is exactly
        /// DisplayWorld.</summary>
        static Vector3 RoadWorld(Vector2 p, float width)
        {
            return new Vector3((p.x - 514f) / 1.005f * (5000f / 1024f),
                               width, -(p.y - 508f) / 1.005f * (5000f / 1024f));
        }

        /// <summary>MapInkLayer, Patrol and every other map caller work in
        /// artwork fractions times 1024 on both axes. The east frame is 2048
        /// wide: its x is halved on the way out.</summary>
        static float OutX { get { return EastWorld.Extends ? VanillaArt / EastArtWidth : 1f; } }

        /// <summary>Replace only GW_Scene_1's selected RU/EN preset after the
        /// game has applied it. With EastTile off this method returns before a
        /// field, property or asset is touched.</summary>
        internal static void ApplyEastArtwork(object manager, object preset)
        {
            if (!EastWorld.Extends || manager == null || preset == null) return;
            try
            {
                FieldInfo mapField = AccessTools.Field(preset.GetType(), "Map");
                Texture original = mapField == null ? null : mapField.GetValue(preset) as Texture;
                string name = original == null ? "" : original.name;
                string file = name.IndexOf("[RU]", StringComparison.OrdinalIgnoreCase) >= 0
                    ? EastMapRu : EastMapEn;
                Texture2D replacement = Assets.Texture(file, false, true);
                FieldInfo widgetField = AccessTools.Field(manager.GetType(), "MapTextureUI");
                Component widget = widgetField == null ? null : widgetField.GetValue(manager) as Component;
                PropertyInfo texture = widget == null ? null : AccessTools.Property(widget.GetType(), "mainTexture");
                if (replacement == null || texture == null) return;
                texture.SetValue(widget, replacement, null);
                if (!_artLogged)
                {
                    _artLogged = true;
                    RevivalPlugin.L.LogInfo("MapInk: east artwork " + file + " applied (1848 x 924, RU/EN preset follows the game language).");
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("MapInk: east artwork failed: " + ex.Message);
            }
        }

        sealed class Segment
        {
            internal float[] Road;
            internal int Index;
        }
        static Dictionary<long, List<Segment>> RoadGrid;
        static long Key(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        static void IndexRoads()
        {
            if (RoadGrid != null) return;
            RoadGrid = new Dictionary<long, List<Segment>>();
            foreach (float[] r in MapInkRoads.Data)
            for (int i = 0; i + 9 < r.Length; i += 5)
            {
                Segment segment = new Segment(); segment.Road = r; segment.Index = i;
                int x0 = Mathf.FloorToInt((Mathf.Min(r[i], r[i+5]) - 50f) / 64f);
                int x1 = Mathf.FloorToInt((Mathf.Max(r[i], r[i+5]) + 50f) / 64f);
                int y0 = Mathf.FloorToInt((Mathf.Min(r[i+1], r[i+6]) - 50f) / 64f);
                int y1 = Mathf.FloorToInt((Mathf.Max(r[i+1], r[i+6]) + 50f) / 64f);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                {
                    long key = Key(x, y);
                    List<Segment> bin;
                    if (!RoadGrid.TryGetValue(key, out bin))
                    { bin = new List<Segment>(); RoadGrid.Add(key, bin); }
                    bin.Add(segment);
                }
            }
        }

        static Vector3 Project(Vector2 p, bool road)
        {
            if (road)
            {
                IndexRoads();
                List<Segment> bin;
                if (RoadGrid.TryGetValue(Key(Mathf.FloorToInt(p.x / 64f),
                                             Mathf.FloorToInt(p.y / 64f)), out bin))
                {
                    float best = 50f * 50f;
                    Vector2 art = Vector2.zero;
                    bool found = false;
                    for (int k = 0; k < bin.Count; k++)
                    {
                        float[] r = bin[k].Road; int i = bin[k].Index;
                        Vector2 a = new Vector2(r[i], r[i+1]);
                        Vector2 ab = new Vector2(r[i+5]-r[i], r[i+6]-r[i+1]);
                        float t = ab.sqrMagnitude < .0001f ? 0f
                            : Mathf.Clamp01(Vector2.Dot(p-a, ab) / ab.sqrMagnitude);
                        float distance = (p-a-ab*t).sqrMagnitude;
                        if (distance >= best) continue;
                        best = distance;
                        art = Vector2.Lerp(new Vector2(r[i+2], r[i+3]),
                                           new Vector2(r[i+7], r[i+8]), t);
                        found = true;
                    }
                    if (found) return RoadWorld(art, StrokeWidth);
                }
            }
            // Manual/off-network paths are not attracted to an unrelated road.
            return new Vector3(p.x, StrokeWidth, p.y);
        }

        internal static List<Vector3> BuildWorld(List<Vector2> points, bool loop, bool road)
        {
            List<Vector3> result = new List<Vector3>();
            int count = loop ? points.Count : points.Count - 1;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = points[i], b = points[(i+1) % points.Count];
                int steps = Mathf.Max(1, Mathf.CeilToInt((b-a).magnitude / 4f));
                for (int j = 0; j < steps; j++)
                    result.Add(Project(Vector2.Lerp(a, b, j/(float)steps), road));
            }
            result.Add(Project(loop ? points[0] : points[points.Count-1], road));
            // Remove subpixel sampling noise only. The 0.65 px bound prevents
            // smoothing from cutting across the road at a real sharp corner.
            List<Vector3> smoothed = new List<Vector3>(result);
            for (int i = 1; i < result.Count-1; i++)
            {
                Vector2 p = Artwork(result[i]);
                Vector2 q = (Artwork(result[i-1]) + p*2f + Artwork(result[i+1])) / 4f;
                Vector2 delta = q-p;
                if (delta.magnitude > .65f) q = p + delta.normalized*.65f;
                smoothed[i] = DisplayWorld(q, result[i].y);
            }
            return smoothed;
        }

        /// <summary>
        /// The dash masks of one route at the zoom it is shown at. <paramref
        /// name="scale"/> is screen pixels per map unit (the map texture's
        /// screen size over 1024) on each axis; <paramref name="arrows"/> adds
        /// the direction chevrons. Everything is built in screen pixels and
        /// handed back in map units, so NGUI draws each mask at 1:1.
        /// </summary>
        internal static Cache Get(string name, List<Vector3> world, bool loop,
                                  Vector2 scale, bool arrows)
        {
            if (scale.x <= 0f || scale.y <= 0f) scale = new Vector2(1f, 1f);
            int zoom = Mathf.RoundToInt((float)Math.Log(scale.y, 2.0) * ZoomSteps);
            float aspect = Mathf.Round(scale.x / scale.y * 1000f) / 1000f;
            Cache cache;
            if (Caches.TryGetValue(name, out cache) && cache.Source == world
                && cache.Zoom == zoom && cache.Aspect == aspect && cache.Arrows == arrows)
            { cache.Seen = Frame; return cache; }
            if (cache != null) Release(cache);
            cache = new Cache(); cache.Source = world; cache.Seen = Frame;
            cache.Zoom = zoom; cache.Aspect = aspect; cache.Arrows = arrows;
            Caches[name] = cache;
            if (world == null || world.Count < 2) return cache;

            float sy = (float)Math.Pow(2.0, zoom / (double)ZoomSteps);
            float sx = sy * aspect * OutX;
            List<Vector2> raw = new List<Vector2>(world.Count);
            for (int i = 0; i < world.Count; i++)
            {
                Vector2 a = Artwork(world[i]);
                raw.Add(new Vector2(a.x * sx, a.y * sy));
            }
            List<Vector2> p = RoundCorners(Resample(raw, loop, 1.5f), loop);
            float[] arc = new float[p.Count];
            for (int i = 1; i < p.Count; i++) arc[i] = arc[i-1] + (p[i]-p[i-1]).magnitude;
            float total = arc[arc.Length-1];

            // Whole dashes only, at one fixed length. The leftover length goes
            // into the gaps - evenly, so no seam and no end ever shows a short
            // gap or a doubled dash. A loop closes on an ordinary gap.
            int count;
            float gap = GapLength, start;
            if (loop)
            {
                count = Mathf.RoundToInt(total / (DashLength + GapLength));
                if (count > 0 && total / count - DashLength < GapLength * .6f) count--;
                if (count < 1) return cache;
                gap = total / count - DashLength;
                start = gap * .5f;
            }
            else
            {
                count = Mathf.FloorToInt((total + GapLength) / (DashLength + GapLength));
                if (count < 1) return cache;
                if (count > 1)
                    gap += Mathf.Min((total - count*DashLength - (count-1)*GapLength) / (count-1),
                                     GapLength * .35f);
                start = (total - count*DashLength - (count-1)*gap) * .5f;
            }
            float period = DashLength + gap;
            // Back to map units, which already hold the east squeeze (OutX).
            Vector2 unit = new Vector2(1f / (sy * aspect), 1f / sy);
            int every = Mathf.Max(1, Mathf.RoundToInt(ArrowEvery / period));
            int last = loop ? count : count - 1;
            for (int k = 0; k < count; k++)
            {
                float d0 = start + k*period;
                int n = Mathf.CeilToInt(DashLength);
                List<Vector2> samples = new List<Vector2>(n + 1);
                for (int j = 0; j <= n; j++) samples.Add(At(p, arc, loop, d0 + DashLength*j/n));
                cache.Dashes.Add(ToMap(Raster(samples, StrokeWidth, true), unit, false));

                // The chevron in the gap AFTER this dash, centred on it and
                // pointing along the route. Open routes skip the gaps next to
                // their two ends, a single short run gets one in its middle.
                if (!arrows || k >= last) continue;
                if (last < every ? k != (last - 1) / 2 : (k + 1 + every/2) % every != 0) continue;
                float c = d0 + DashLength + gap*.5f;
                Vector2 mid = At(p, arc, loop, c);
                Vector2 t = (At(p, arc, loop, c + 1f) - At(p, arc, loop, c - 1f)).normalized;
                Vector2 side = new Vector2(-t.y, t.x);
                List<Vector2> head = new List<Vector2>(3);
                head.Add(mid - t*(ArrowLength*.5f) + side*ArrowHalf);
                head.Add(mid + t*(ArrowLength*.5f));
                head.Add(mid - t*(ArrowLength*.5f) - side*ArrowHalf);
                cache.Dashes.Add(ToMap(Raster(head, ArrowStroke, false), unit, true));
            }
            return cache;
        }

        /// <summary>The point at arc length <paramref name="d"/>: a loop's arc
        /// runs 0..total and then starts again, an open line stops at its
        /// ends.</summary>
        static Vector2 At(List<Vector2> p, float[] arc, bool loop, float d)
        {
            float total = arc[arc.Length-1];
            if (total <= 0f) return p[0];
            if (loop) d = d - Mathf.Floor(d / total) * total;
            else d = Mathf.Min(Mathf.Max(d, 0f), total);
            int lo = 0, hi = arc.Length - 1;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (arc[m] < d) lo = m; else hi = m; }
            float span = arc[hi] - arc[lo];
            return span < .0001f ? p[lo] : Vector2.Lerp(p[lo], p[hi], (d - arc[lo]) / span);
        }

        /// <summary>The line at an even point spacing in screen pixels, so the
        /// corner rounding acts the same on every route. A loop comes back
        /// closed: its last point repeats the first.</summary>
        static List<Vector2> Resample(List<Vector2> pts, bool loop, float step)
        {
            List<Vector2> src = new List<Vector2>(pts);
            if (loop && (src[src.Count-1] - src[0]).magnitude > .01f) src.Add(src[0]);
            List<Vector2> result = new List<Vector2>();
            result.Add(src[0]);
            float need = step;
            for (int i = 1; i < src.Count; i++)
            {
                Vector2 a = src[i-1], b = src[i];
                float len = (b - a).magnitude, at = 0f;
                while (len - at >= need)
                {
                    at += need; need = step;
                    result.Add(Vector2.Lerp(a, b, at / len));
                }
                need -= len - at;
            }
            Vector2 end = src[src.Count-1];
            if ((result[result.Count-1] - end).magnitude > step * .25f) result.Add(end);
            else result[result.Count-1] = end;
            if (result.Count < 2) result.Add(end);
            return result;
        }

        /// <summary>Rounds the line's corners in screen space: repeated 1-2-1
        /// smoothing, every point held within <see cref="PathRound"/> pixels of
        /// where it started, so a sharp turn becomes a small arc that stays on
        /// the road and the dashes walk round it at their even spacing instead
        /// of folding into a clump. Open ends stay put; a loop wraps.</summary>
        static List<Vector2> RoundCorners(List<Vector2> pts, bool loop)
        {
            int n = pts.Count;
            if (n < 3) return pts;
            // A closed line repeats its first point last; smooth the ring once.
            int ring = loop ? n - 1 : n;
            Vector2[] origin = pts.GetRange(0, ring).ToArray();
            Vector2[] cur = (Vector2[])origin.Clone(), next = new Vector2[ring];
            for (int pass = 0; pass < 16; pass++)
            {
                for (int i = 0; i < ring; i++)
                {
                    if (!loop && (i == 0 || i == ring - 1)) { next[i] = cur[i]; continue; }
                    Vector2 q = (cur[(i-1+ring)%ring] + cur[i]*2f + cur[(i+1)%ring]) / 4f;
                    Vector2 off = q - origin[i];
                    if (off.magnitude > PathRound) q = origin[i] + off.normalized * PathRound;
                    next[i] = q;
                }
                Vector2[] swap = cur; cur = next; next = swap;
            }
            List<Vector2> result = new List<Vector2>(cur);
            if (loop) result.Add(cur[0]);
            return result;
        }

        /// <summary>A mask built in screen pixels, handed back in map units.</summary>
        static Dash ToMap(Dash dash, Vector2 unit, bool arrow)
        {
            dash.Bounds = new Rect(dash.Bounds.x * unit.x, dash.Bounds.y * unit.y,
                                   dash.Bounds.width * unit.x, dash.Bounds.height * unit.y);
            dash.Mid = new Vector2(dash.Mid.x * unit.x, dash.Mid.y * unit.y);
            for (int j = 0; j < dash.Points.Count; j++)
                dash.Points[j] = new Vector2(dash.Points[j].x * unit.x, dash.Points[j].y * unit.y);
            dash.Arrow = arrow;
            return dash;
        }

        /// <summary>One flat-capped dash of another stroke width (1024 map
        /// pixels): the no-fly zone's fine line, Revival.NoFly.cs.</summary>
        internal static Dash Raster(List<Vector2> points, float strokeWidth)
        {
            return Raster(points, strokeWidth, true);
        }

        /// <summary>
        /// One antialiased mask along <paramref name="points"/> (screen pixels):
        /// a coloured core <paramref name="width"/> wide over a dark rim, both
        /// the UNION of the segments' distance fields, so joins never build up
        /// opacity and bends are round. <paramref name="capped"/> cuts both
        /// ends flat on the end tangents with slightly rounded corners (a dash);
        /// otherwise the ends are round (the chevron). RGB carries the rim as
        /// black under a white core, so the widget tint colours the core only.
        /// </summary>
        internal static Dash Raster(List<Vector2> points, float width, bool capped)
        {
            float half = width * .5f, rim = half + OutlineWidth, margin = rim + 1f;
            float x0=points[0].x, x1=x0, y0=points[0].y, y1=y0;
            for (int i=0; i<points.Count; i++)
            {
                x0=Mathf.Min(x0,points[i].x); x1=Mathf.Max(x1,points[i].x);
                y0=Mathf.Min(y0,points[i].y); y1=Mathf.Max(y1,points[i].y);
            }
            x0=Mathf.Floor((x0-margin)*Density)/Density;
            y0=Mathf.Floor((y0-margin)*Density)/Density;
            int w=Mathf.CeilToInt((x1+margin-x0)*Density);
            int h=Mathf.CeilToInt((y1+margin-y0)*Density);
            float[] dist = new float[w*h];
            for (int i=0; i<dist.Length; i++) dist[i]=float.MaxValue;
            for (int i=1; i<points.Count; i++)
            {
                Vector2 a=points[i-1], ab=points[i]-a;
                float len2=ab.sqrMagnitude, r=rim+1f;
                int left=Mathf.Max(0,Mathf.FloorToInt((Mathf.Min(a.x,points[i].x)-r-x0)*Density));
                int right=Mathf.Min(w-1,Mathf.CeilToInt((Mathf.Max(a.x,points[i].x)+r-x0)*Density));
                int top=Mathf.Max(0,Mathf.FloorToInt((Mathf.Min(a.y,points[i].y)-r-y0)*Density));
                int bottom=Mathf.Min(h-1,Mathf.CeilToInt((Mathf.Max(a.y,points[i].y)+r-y0)*Density));
                for (int y=top; y<=bottom; y++) for (int x=left; x<=right; x++)
                {
                    Vector2 q=new Vector2(x0+(x+.5f)/Density,y0+(y+.5f)/Density);
                    float t=len2<.000001f ? 0f : Mathf.Clamp01(Vector2.Dot(q-a,ab)/len2);
                    float d=(q-a-ab*t).magnitude;
                    int pixel=y*w+x;
                    if (d<dist[pixel]) dist[pixel]=d;
                }
            }
            Vector2 first=points[0], last=points[points.Count-1];
            Vector2 head=(points[1]-first).normalized;
            Vector2 tail=(last-points[points.Count-2]).normalized;
            Color32[] pixels=new Color32[w*h];
            for (int y=0;y<h;y++) for (int x=0;x<w;x++)
            {
                float d=dist[y*w+x];
                if (d>=rim+1f) continue;
                Vector2 q=new Vector2(x0+(x+.5f)/Density,y0+(y+.5f)/Density);
                float end=capped ? Mathf.Min(Vector2.Dot(q-first,head),Vector2.Dot(last-q,tail))
                                 : float.MaxValue;
                float core=Coverage(Inside(half-d,end,CornerRadius));
                float edge=Coverage(Inside(rim-d,end+OutlineWidth,CornerRadius+OutlineWidth))
                           *OutlineAlpha;
                // The core laid over the rim, straight (not premultiplied) alpha.
                float alpha=core+edge*(1f-core);
                if (alpha<=0f) continue;
                byte shade=(byte)Mathf.RoundToInt(core/alpha*255f);
                pixels[(h-1-y)*w+x]=new Color32(shade,shade,shade,
                                                (byte)Mathf.RoundToInt(alpha*255f));
            }
            Texture2D texture=new Texture2D(w,h,TextureFormat.ARGB32,true);
            texture.wrapMode=TextureWrapMode.Clamp;
            texture.filterMode=FilterMode.Trilinear;
            texture.SetPixels32(pixels); texture.Apply(true,true);
            Dash dash=new Dash(); dash.Bounds=new Rect(x0,y0,w/(float)Density,h/(float)Density);
            dash.Mid=points[points.Count/2]; dash.Points=points; dash.Texture=texture;
            return dash;
        }

        /// <summary>Signed distance into a flat-ended stroke whose two corners
        /// are rounded by <paramref name="radius"/>: <paramref name="side"/> is
        /// the distance in from the long edge, <paramref name="end"/> in from
        /// the end plane.</summary>
        static float Inside(float side, float end, float radius)
        {
            if (side < radius && end < radius)
            {
                float u = radius - side, v = radius - end;
                return radius - (float)Math.Sqrt(u*u + v*v);
            }
            return Mathf.Min(side, end);
        }

        /// <summary>Box-filtered pixel coverage of a signed distance, at the
        /// mask's own texel size.</summary>
        static float Coverage(float inside)
        {
            return Mathf.Clamp01(inside*Density+.5f);
        }
    }
}

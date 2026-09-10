using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    // Map-only geometry and cached coverage masks. No waypoint is ever changed.
    internal static class MapInk
    {
        // One style for every route, estimated from the supplied reference.
        // Values are at 1024 map pixels; only map zoom scales them together.
        internal const float DashLength = 24f;
        internal const float GapLength = 10f;
        internal const float StrokeWidth = 4.75f;
        const int Density = 4;

        internal sealed class Dash
        {
            internal Rect Bounds;
            internal Vector2 Mid;
            internal List<Vector2> Points;
            internal Texture2D Texture;
        }

        internal sealed class Cache
        {
            internal List<Vector3> Source;
            internal List<Dash> Dashes = new List<Dash>();
            internal int Seen;
        }

        static readonly Dictionary<string, Cache> Caches = new Dictionary<string, Cache>();
        static readonly List<string> Retired = new List<string>();
        static int Frame;

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
            return new Vector2(p.x * (1024f / 5000f) * 1.005f + 514f,
                              -p.z * (1024f / 5000f) * 1.005f + 508f);
        }

        static Vector3 DisplayWorld(Vector2 p, float width)
        {
            return new Vector3((p.x - 514f) / 1.005f * (5000f / 1024f),
                               width, -(p.y - 508f) / 1.005f * (5000f / 1024f));
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
                    if (found) return DisplayWorld(art, StrokeWidth);
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

        internal static Cache Get(string name, List<Vector3> world, bool loop)
        {
            Cache cache;
            if (Caches.TryGetValue(name, out cache) && cache.Source == world)
            { cache.Seen = Frame; return cache; }
            if (cache != null) Release(cache);
            cache = new Cache(); cache.Source = world; cache.Seen = Frame;
            Caches[name] = cache;
            List<Vector2> p = new List<Vector2>();
            float[] arc = new float[world.Count];
            for (int i = 0; i < world.Count; i++)
            {
                p.Add(Artwork(world[i]));
                if (i > 0) arc[i] = arc[i-1] + (p[i]-p[i-1]).magnitude;
            }
            float total = arc[arc.Length-1], period = DashLength+GapLength;
            int count = Mathf.FloorToInt((total+(loop ? 0f : GapLength))/period);
            float used = count*period-(loop ? 0f : GapLength);
            float start = (total-used)*.5f;
            for (int k = 0; k < count; k++)
            {
                List<Vector2> samples = new List<Vector2>();
                int at = 1;
                for (int j = 0; j <= 64; j++)
                {
                    float d = start+k*period+j*(DashLength/64f);
                    while (at < arc.Length-1 && arc[at] < d) at++;
                    float t = arc[at]-arc[at-1] < .0001f ? 0f
                        : Mathf.Clamp01((d-arc[at-1])/(arc[at]-arc[at-1]));
                    samples.Add(Vector2.Lerp(p[at-1], p[at], t));
                }
                cache.Dashes.Add(Raster(samples));
            }
            return cache;
        }

        internal static Dash Raster(List<Vector2> points)
        {
            float x0=points[0].x, x1=x0, y0=points[0].y, y1=y0, margin=StrokeWidth*.5f+1f;
            for (int i=0; i<points.Count; i++)
            {
                x0=Mathf.Min(x0,points[i].x); x1=Mathf.Max(x1,points[i].x);
                y0=Mathf.Min(y0,points[i].y); y1=Mathf.Max(y1,points[i].y);
            }
            x0=Mathf.Floor((x0-margin)*Density)/Density;
            y0=Mathf.Floor((y0-margin)*Density)/Density;
            int w=Mathf.CeilToInt((x1+margin-x0)*Density);
            int h=Mathf.CeilToInt((y1+margin-y0)*Density);
            byte[] alpha = new byte[w*h];
            Vector2 first=points[0], last=points[points.Count-1];
            Vector2 head=(points[1]-first).normalized;
            Vector2 tail=(last-points[points.Count-2]).normalized;
            for (int i=1; i<points.Count; i++)
            {
                Vector2 a=points[i-1], ab=points[i]-a;
                if (ab.sqrMagnitude < .000001f) continue;
                float r=StrokeWidth*.5f+.5f;
                int left=Mathf.Max(0,Mathf.FloorToInt((Mathf.Min(a.x,points[i].x)-r-x0)*Density));
                int right=Mathf.Min(w-1,Mathf.CeilToInt((Mathf.Max(a.x,points[i].x)+r-x0)*Density));
                int top=Mathf.Max(0,Mathf.FloorToInt((Mathf.Min(a.y,points[i].y)-r-y0)*Density));
                int bottom=Mathf.Min(h-1,Mathf.CeilToInt((Mathf.Max(a.y,points[i].y)+r-y0)*Density));
                for (int y=top; y<=bottom; y++) for (int x=left; x<=right; x++)
                {
                    Vector2 q=new Vector2(x0+(x+.5f)/Density,y0+(y+.5f)/Density);
                    float t=Mathf.Clamp01(Vector2.Dot(q-a,ab)/ab.sqrMagnitude);
                    float inside=StrokeWidth*.5f-(q-a-ab*t).magnitude;
                    // A union coverage mask: joins never accumulate opacity.
                    // End planes leave clean flat caps with the same AA as sides.
                    inside=Mathf.Min(inside,Mathf.Min(Vector2.Dot(q-first,head),Vector2.Dot(last-q,tail)));
                    byte coverage=(byte)Mathf.RoundToInt(Mathf.Clamp01(inside*Density+.5f)*255f);
                    int pixel=y*w+x;
                    if (coverage>alpha[pixel]) alpha[pixel]=coverage;
                }
            }
            Color32[] pixels=new Color32[w*h];
            for (int y=0;y<h;y++) for (int x=0;x<w;x++)
                pixels[(h-1-y)*w+x]=new Color32(255,255,255,alpha[y*w+x]);
            Texture2D texture=new Texture2D(w,h,TextureFormat.ARGB32,true);
            texture.wrapMode=TextureWrapMode.Clamp;
            texture.filterMode=FilterMode.Trilinear;
            texture.SetPixels32(pixels); texture.Apply(true,true);
            Dash dash=new Dash(); dash.Bounds=new Rect(x0,y0,w/(float)Density,h/(float)Density);
            dash.Mid=points[points.Count/2]; dash.Points=points; dash.Texture=texture;
            return dash;
        }
    }
}

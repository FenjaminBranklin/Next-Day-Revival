// Next Day: Survival - Revival Toolkit
//
// HELICOPTER LANDING PADS.
//
// A pad is a named place a helicopter can stand, authored in the map editor
// (helipaddef.py, editor/helipads.js) and published on its own live channel
// beside the routes, the troop landings and the ground groups. Every client
// builds the pads of the map it has loaded; nothing here is master-only,
// because a pad is scenery and a place, not a spawned actor.
//
// WHY A PAD IS A PLATFORM AND NOT PAINT ON THE GROUND.
// The terrain is a 1025x1025 heightmap at 4.8828125 m per sample over 5000 m
// (REVERSE_ENGINEERING.md 37), and it is gentle rather than flat: measured over
// GWTerrain2, a third of the map is steeper than 10 degrees and a tenth steeper
// than 20. RevivalTroopInsertion.FindLandingSpot therefore has to search five
// rings for ground whose four gear points sit within 1.6 K of each other, and
// on a hillside it finds none and lands the Mi-8 crooked anyway. A built deck
// IS that flat ground: it sits at the HIGHEST terrain point under its own
// footprint and closes the gap to the slope with a skirt, which is what a real
// pad on a slope does. The landing search then finds the deck, because
// GroundY casts an ordinary downward ray and the deck's collider is the first
// thing it meets.
//
// The pad is also the LZ: a troop landing whose marked zone falls inside a pad
// is snapped onto the pad centre and lands with the pad's heading, so an admin
// places a pad once and every drop authored near it uses it (Snap, called from
// RevivalTroopInsertion.Begin).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class Helipads
    {
        internal const int MaxPads = 64;
        internal const float MinRadius = 8f, MaxRadius = 60f;
        /// <summary>Segments around the deck. 32 is smooth at walking distance
        /// and still a mesh of 100 triangles.</summary>
        const int Segments = 32;
        /// <summary>How far the deck stands over the highest ground under it.
        /// Enough that the terrain never pokes through the surface between two
        /// height samples, low enough to step onto.</summary>
        const float DeckLift = 0.15f;
        /// <summary>How far the skirt is driven into the slope at the rim, so
        /// no gap opens between the deck and the ground behind it.</summary>
        const float SkirtBite = 0.6f;

        internal sealed class Pad
        {
            internal string Name, Scene, Surface, Key;
            internal bool Enabled, Marker;
            internal float X, Z, Heading, Radius;
            /// <summary>Deck height once the pad has been built; 0 before.</summary>
            internal float Deck;
            internal GameObject Go;
        }

        static ConfigEntry<bool> _cfgEnabled, _cfgMarkers;
        static ConfigEntry<string> _cfgFile;
        static List<Pad> _pads = new List<Pad>();
        static string[] _source;
        static bool _loaded;
        static GameObject _root;
        static string _builtScene = "", _builtKey = "";
        static float _next;
        static Texture _stamp;
        static bool _waitLogged;
        static bool _stampTried;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Helipads", "Enabled", true,
                "Build the helicopter landing pads authored in the map editor.");
            _cfgFile = cfg.Bind("Helipads", "PadFile", "ndr_helipads.tsv",
                "Offline fallback beside the plugin, used when no live editor is reachable.");
            _cfgMarkers = cfg.Bind("Helipads", "MapMarkers", true,
                "Mark the pads on the world map.");
        }

        // ================================================================ data

        /// <summary>One line per pad:
        /// name enabled x z heading radius surface marker [scene].
        /// Throws on anything it does not fully understand - a half-read pad
        /// table would put a deck in the wrong place, and the caller keeps the
        /// last table it verified.</summary>
        internal static List<Pad> Parse(string[] lines)
        {
            if (lines == null) throw new IOException("No pad data");
            if (lines.Length > 257) throw new IOException("Too many pad rows");
            List<Pad> result = new List<Pad>();
            Dictionary<string, bool> names = new Dictionary<string, bool>();
            StringBuilder key = new StringBuilder();
            foreach (string line in lines)
            {
                if (line == null) continue;
                string raw = line.TrimEnd('\r');
                if (raw.Trim().Length == 0 || raw[0] == '#') continue;
                string[] c = raw.Split('\t');
                if (c.Length != 8 && c.Length != 9) throw new IOException("Invalid pad row");
                if (!Regex.IsMatch(c[0], "^[A-Za-z0-9_.-]{1,64}$"))
                    throw new IOException("Invalid pad name");
                if (result.Count >= MaxPads) throw new IOException("Too many pads");
                string lower = c[0].ToLowerInvariant();
                if (names.ContainsKey(lower)) throw new IOException("Duplicate pad name");
                names.Add(lower, true);

                Pad p = new Pad();
                p.Name = c[0];
                if (c[1] != "0" && c[1] != "1") throw new IOException("Invalid pad enabled flag");
                p.Enabled = c[1] == "1";
                p.X = Number(c[2], -2501f, 2501f);
                p.Z = Number(c[3], -2501f, 2501f);
                p.Heading = Number(c[4], -0.01f, 360f) % 360f;
                p.Radius = Number(c[5], MinRadius, MaxRadius);
                p.Surface = c[6];
                if (p.Surface != "concrete" && p.Surface != "steel" && p.Surface != "cleared")
                    throw new IOException("Invalid pad surface");
                if (c[7] != "0" && c[7] != "1") throw new IOException("Invalid pad marker flag");
                p.Marker = c[7] == "1";
                // No scene column means the home map - the same rule every other
                // authored item follows (Revival.MapScene.cs).
                p.Scene = c.Length == 9 ? MapScene.Clean(c[8]) : "";
                result.Add(p);
                key.Append(raw).Append('\n');
            }
            string all = key.ToString();
            foreach (Pad p in result) p.Key = all;
            return result;
        }

        static float Number(string text, float min, float max)
        {
            float value;
            if (!Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || Single.IsNaN(value) || Single.IsInfinity(value) || value < min || value > max)
                throw new IOException("Invalid pad number");
            return value;
        }

        /// <summary>The live table when there is one, otherwise the file beside
        /// the plugin. Same order as the troop landings, for the same reason: a
        /// pad must exist on a machine that cannot reach the editor.</summary>
        internal static void Load(bool force)
        {
            string[] live = LiveRoutes.Pads;
            if (_loaded && !force && live == _source) return;
            _loaded = true;
            _source = live;

            string[] lines = live;
            string label = "live helipads";
            if (lines == null)
            {
                string file = _cfgFile == null ? "ndr_helipads.tsv" : _cfgFile.Value;
                string path = Path.Combine(RevivalPlugin.AssetDir == null ? "" : RevivalPlugin.AssetDir, file);
                label = file;
                if (!File.Exists(path)) { Replace(new List<Pad>(), label); return; }
                try { lines = File.ReadAllLines(path); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Helipads: reading " + path + ": " + ex.Message);
                    return;
                }
            }
            try { Replace(Parse(lines), label); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Helipads: " + ex.Message
                    + " - keeping the last verified pad table.");
            }
        }

        static void Replace(List<Pad> fresh, string label)
        {
            _pads = fresh;
            RevivalPlugin.L.LogInfo("Helipads: loaded " + fresh.Count + " pad(s) from " + label + ".");
            // Force a rebuild: the decks standing in the world are from the old
            // table and may have moved, changed size or gone.
            _builtKey = "";
        }

        // =============================================================== build

        internal static void Tick()
        {
            if (_cfgEnabled == null || !_cfgEnabled.Value) { Clear(); return; }
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + 1f;
            try
            {
                Load(false);
                string scene = MapScene.Current;
                string key = _pads.Count == 0 ? "" : _pads[0].Key;
                if (scene == _builtScene && key == _builtKey) return;
                Clear();
                _builtScene = scene;
                _builtKey = key;
                BuildAll();
            }
            catch (Exception ex)
            {
                _next = Time.realtimeSinceStartup + 10f;
                RevivalPlugin.L.LogWarning("Helipads: " + ex.Message);
            }
        }

        static void Clear()
        {
            foreach (Pad p in _pads) { p.Go = null; p.Deck = 0f; }
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _builtScene = "";
            _builtKey = "";
        }

        static void BuildAll()
        {
            int built = 0, waiting = 0;
            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene)) continue;
                float deck;
                if (!DeckHeight(p, out deck))
                {
                    // No height data there yet (the terrain is not loaded, or the
                    // pad lies outside it). Retry on the next table or scene
                    // change rather than putting a deck at y = 0.
                    waiting++;
                    continue;
                }
                p.Deck = deck;
                if (p.Surface == "cleared") { built++; continue; }
                if (_root == null)
                {
                    _root = new GameObject("NDR_Helipads");
                    _root.transform.position = Vector3.zero;
                }
                p.Go = BuildPad(p, deck);
                if (p.Go != null) p.Go.transform.SetParent(_root.transform, true);
                built++;
            }
            if (built > 0)
                RevivalPlugin.L.LogInfo("Helipads: " + built + " pad(s) on "
                    + MapScene.Label(MapScene.Current)
                    + (waiting > 0 ? ", " + waiting + " waiting for terrain" : "") + ".");
            if (waiting > 0)
            {
                // Retry, but slowly. A pad whose terrain is simply not loaded
                // yet is ready within a second or two; a pad authored outside
                // the terrain never becomes ready, and a one-second rebuild of
                // every OTHER deck forever is the cost of not backing off.
                // Saying it once is useful; saying it every second is noise.
                if (!_waitLogged)
                {
                    _waitLogged = true;
                    RevivalPlugin.L.LogInfo("Helipads: " + waiting
                        + " pad(s) have no terrain under them yet - retrying.");
                }
                _builtKey = "";
                _next = Time.realtimeSinceStartup + 5f;
            }
            else _waitLogged = false;
        }

        /// <summary>The deck sits over the HIGHEST ground under the footprint -
        /// the centre, a ring at half the radius and the rim. A deck at the
        /// average would be buried on the uphill side.</summary>
        static bool DeckHeight(Pad p, out float deck)
        {
            deck = 0f;
            float best = float.MinValue;
            bool any = false;
            for (int ring = 0; ring <= 2; ring++)
            {
                float r = p.Radius * ring * 0.5f;
                int steps = ring == 0 ? 1 : 12;
                for (int k = 0; k < steps; k++)
                {
                    float a = k * Mathf.PI * 2f / steps;
                    Vector3 at = new Vector3(p.X + Mathf.Cos(a) * r, 0f, p.Z + Mathf.Sin(a) * r);
                    float y;
                    if (!Ground(at, out y)) continue;
                    any = true;
                    if (y > best) best = y;
                }
            }
            if (!any) return false;
            deck = best + DeckLift;
            return true;
        }

        /// <summary>Terrain height, never the pads themselves: a ray would hit
        /// a deck that is already standing and stack the next one on top of it.</summary>
        static bool Ground(Vector3 xz, out float y)
        {
            return RevivalTroopInsertion.TerrainHeight(xz, out y);
        }

        static GameObject BuildPad(Pad p, float deck)
        {
            GameObject go = new GameObject("NDR_Helipad_" + p.Name);
            go.transform.position = new Vector3(p.X, deck, p.Z);
            go.transform.rotation = Quaternion.Euler(0f, p.Heading, 0f);

            // The skirt follows the ground at every rim point, so the deck seals
            // against the slope instead of floating over it.
            float[] drop = new float[Segments];
            for (int i = 0; i < Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                Vector3 at = new Vector3(p.X + Mathf.Cos(a) * p.Radius, 0f,
                                         p.Z + Mathf.Sin(a) * p.Radius);
                float y;
                if (!Ground(at, out y)) y = deck;
                drop[i] = Mathf.Min(y - SkirtBite, deck - SkirtBite) - deck;
            }

            Mesh body = DeckMesh(p.Radius, drop, p.Heading);
            GameObject surface = new GameObject("deck");
            surface.transform.SetParent(go.transform, false);
            surface.AddComponent<MeshFilter>().sharedMesh = body;
            surface.AddComponent<MeshRenderer>().sharedMaterial = SurfaceMaterial(p.Surface);
            MeshCollider collider = surface.AddComponent<MeshCollider>();
            collider.sharedMesh = body;

            // The paint sits a finger over the deck and carries no collider, so
            // nothing stands on the markings instead of on the pad.
            GameObject paint = new GameObject("markings");
            paint.transform.SetParent(go.transform, false);
            paint.transform.localPosition = new Vector3(0f, 0.03f, 0f);
            paint.AddComponent<MeshFilter>().sharedMesh = PaintMesh(p.Radius);
            paint.AddComponent<MeshRenderer>().sharedMaterial = PaintMaterial();
            return go;
        }

        /// <summary>Deck and skirt in ONE mesh: a fan for the surface, a quad
        /// strip down to the ground for the rim. `drop` is the rim's local y per
        /// segment, `heading` is only used to keep the UVs pointing north.</summary>
        static Mesh DeckMesh(float radius, float[] drop, float heading)
        {
            int n = Segments;
            Vector3[] verts = new Vector3[1 + n * 2];
            Vector2[] uvs = new Vector2[verts.Length];
            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float cx = Mathf.Cos(a) * radius, cz = Mathf.Sin(a) * radius;
                verts[1 + i] = new Vector3(cx, 0f, cz);
                verts[1 + n + i] = new Vector3(cx, drop[i], cz);
                uvs[1 + i] = new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f);
                uvs[1 + n + i] = uvs[1 + i];
            }
            int[] tris = new int[n * 3 + n * 6];
            int t = 0;
            for (int i = 0; i < n; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % n;
                tris[t++] = 0; tris[t++] = b; tris[t++] = a;          // deck, facing up
                int c = 1 + n + i, d = 1 + n + (i + 1) % n;
                tris[t++] = a; tris[t++] = b; tris[t++] = d;          // skirt, facing out
                tris[t++] = a; tris[t++] = d; tris[t++] = c;
            }
            Mesh mesh = new Mesh();
            mesh.name = "NDR_HelipadDeck";
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The H and the rim band, as flat quads. A helipad is read
        /// from above, so painted geometry is enough and costs one draw call.</summary>
        static Mesh PaintMesh(float radius)
        {
            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();
            // The H: two uprights along the pad's forward axis and one crossbar.
            float bar = radius * 0.11f;
            Quad(verts, tris, -radius * 0.24f, 0f, bar, radius * 0.62f);
            Quad(verts, tris, radius * 0.24f, 0f, bar, radius * 0.62f);
            Quad(verts, tris, 0f, 0f, radius * 0.48f, bar);
            // The rim band, as a ring of short quads just inside the edge.
            float inner = radius * 0.88f, outer = radius * 0.95f;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments;
                float a1 = (i + 1) * Mathf.PI * 2f / Segments;
                int b = verts.Count;
                verts.Add(new Vector3(Mathf.Cos(a0) * inner, 0f, Mathf.Sin(a0) * inner));
                verts.Add(new Vector3(Mathf.Cos(a1) * inner, 0f, Mathf.Sin(a1) * inner));
                verts.Add(new Vector3(Mathf.Cos(a1) * outer, 0f, Mathf.Sin(a1) * outer));
                verts.Add(new Vector3(Mathf.Cos(a0) * outer, 0f, Mathf.Sin(a0) * outer));
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            Mesh mesh = new Mesh();
            mesh.name = "NDR_HelipadPaint";
            mesh.vertices = verts.ToArray();
            mesh.triangles = tris.ToArray();
            Vector3[] normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>One axis-aligned rectangle in the pad's local plane, facing
        /// up. cx/cz is its centre, w spans x and d spans z.</summary>
        static void Quad(List<Vector3> verts, List<int> tris, float cx, float cz, float w, float d)
        {
            int b = verts.Count;
            verts.Add(new Vector3(cx - w * 0.5f, 0f, cz - d * 0.5f));
            verts.Add(new Vector3(cx - w * 0.5f, 0f, cz + d * 0.5f));
            verts.Add(new Vector3(cx + w * 0.5f, 0f, cz + d * 0.5f));
            verts.Add(new Vector3(cx + w * 0.5f, 0f, cz - d * 0.5f));
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        static Material _concrete, _steel, _paint;

        static Material SurfaceMaterial(string kind)
        {
            if (kind == "steel")
            {
                if (_steel == null) _steel = Make("NDR_HelipadSteel",
                    new Color(0.30f, 0.32f, 0.34f), 0.65f, 0.35f);
                return _steel;
            }
            if (_concrete == null) _concrete = Make("NDR_HelipadConcrete",
                new Color(0.50f, 0.49f, 0.46f), 0.05f, 0.12f);
            return _concrete;
        }

        static Material PaintMaterial()
        {
            if (_paint == null) _paint = Make("NDR_HelipadPaint",
                new Color(0.86f, 0.87f, 0.83f), 0f, 0.20f);
            return _paint;
        }

        /// <summary>Same shader chain as ItemFactory.MakeMaterial and Arena: a
        /// MeshRenderer without a material draws magenta, which reads in game as
        /// a broken model and sends the search in the wrong direction.</summary>
        static Material Make(string name, Color color, float metallic, float gloss)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null)
            {
                RevivalPlugin.L.LogWarning("Helipads: no usable shader - pads stay unpainted.");
                return null;
            }
            Material mat = new Material(shader);
            mat.name = name;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", gloss);
            return mat;
        }

        // ============================================================== the LZ

        /// <summary>The pad a marked landing zone falls on, if any. The centre
        /// comes back at DECK height, so the helicopter sets down on the
        /// surface and not in the hill it stands on.</summary>
        internal static bool Snap(Vector3 mark, out Vector3 centre, out float heading)
        {
            centre = mark;
            heading = 0f;
            if (_cfgEnabled == null || !_cfgEnabled.Value) return false;
            Pad best = null;
            float nearest = 0f;
            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene)) continue;
                float dx = mark.x - p.X, dz = mark.z - p.Z;
                float distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance > p.Radius) continue;
                if (best == null || distance < nearest) { best = p; nearest = distance; }
            }
            if (best == null) return false;
            float y = best.Deck;
            if (y == 0f && !DeckHeight(best, out y)) return false;
            centre = new Vector3(best.X, y, best.Z);
            heading = best.Heading;
            return true;
        }

        /// <summary>The name of the pad a point stands on, for log lines.</summary>
        internal static string NameAt(Vector3 point)
        {
            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene)) continue;
                float dx = point.x - p.X, dz = point.z - p.Z;
                if (dx * dx + dz * dz <= p.Radius * p.Radius) return p.Name;
            }
            return null;
        }

        // ============================================================ the map

        /// <summary>A ring on the world map at every pad of the loaded region.
        /// The same native ink layer the settlement ring uses, so it fades with
        /// the map and is clipped by the map's own panel.</summary>
        internal static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_cfgEnabled == null || !_cfgEnabled.Value
                || _cfgMarkers == null || !_cfgMarkers.Value)
            { MapInkLayer.Hide("helipads"); return; }
            MapInkLayer layer = null;
            try
            {
                Component manager, texture;
                Camera camera;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera, out world, out map)) return;
                Rect full;
                if (!MapTools.MapScreenRect(texture, camera, out full)) return;
                if (!_stampTried) { _stampTried = true; _stamp = MapInkLayer.SettlementStamp(); }
                if (_stamp == null) return;

                foreach (Pad p in _pads)
                {
                    if (!p.Enabled || !p.Marker || !MapScene.Owns(p.Scene)) continue;
                    Vector3 centre = new Vector3(p.X, 0f, p.Z);
                    if (Mathf.Abs(centre.x) > world.x || Mathf.Abs(centre.z) > world.y) continue;
                    Vector2 mid;
                    if (!Project(centre, texture, camera, world, map, full, out mid)) continue;
                    if (layer == null)
                    {
                        layer = MapInkLayer.Begin("helipads", texture);
                        if (layer == null) return;
                    }
                    // A pad is a PLACE, not an area: a fixed small ring, so it
                    // stays readable at every zoom instead of vanishing when the
                    // map is zoomed out.
                    float size = Mathf.Max(6f, full.width * 0.022f);
                    Rect ring = new Rect((mid.x - size - full.x) * 1024f / full.width,
                        (mid.y - size - full.y) * 1024f / full.height,
                        size * 2f * 1024f / full.width, size * 2f * 1024f / full.height);
                    layer.Draw(ring, _stamp, RingColor);

                    Vector2 mouse = Event.current.mousePosition;
                    if (full.Contains(mouse) && (mouse - mid).sqrMagnitude <= size * size)
                    {
                        Color old = GUI.color;
                        try
                        {
                            GUI.color = RingColor;
                            GUI.Label(new Rect(mouse.x + 12f, mouse.y - 11f, 260f, 22f),
                                Loc.T("Вертолётная "
                                      + "площадка " + p.Name,
                                      "Helipad " + p.Name));
                        }
                        finally { GUI.color = old; }
                    }
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("Helipads map: " + ex.Message);
            }
            finally
            {
                if (layer != null) layer.End();
                else MapInkLayer.Hide("helipads");
            }
        }

        static readonly Color RingColor = new Color(0.44f, 0.83f, 0.91f, 0.85f);

        // The map ARTWORK is not registered to the terrain: it is ~0.5 % larger
        // and sits about 20 m north-east of the ground it depicts
        // (REVERSE_ENGINEERING.md 33.1). Every marker goes through the same
        // correction Patrol and the settlement ring use, or the pad ring would
        // sit beside the place the pad actually stands.
        const float MapArtFit = 1024f;
        const float MapArtScale = 1.005f;
        const float MapArtShiftX = 2f;
        const float MapArtShiftY = -4f;

        static bool Project(Vector3 point, Component texture, Camera camera,
                            Vector2 world, Vector2 map, Rect full, out Vector2 gui)
        {
            if (!MapTools.WorldToGui(point, texture, camera, world, map, out gui)) return false;
            if (full.width >= 1f)
            {
                float factor = full.width / MapArtFit;
                float cx = full.x + full.width * 0.5f;
                float cy = full.y + full.height * 0.5f;
                gui = new Vector2((gui.x - cx) * MapArtScale + cx + MapArtShiftX * factor,
                                  (gui.y - cy) * MapArtScale + cy + MapArtShiftY * factor);
            }
            return true;
        }
    }
}

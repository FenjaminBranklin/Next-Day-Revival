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
// WHY THE DECK STANDS PROUD AND STILL HAS NO EDGE (6.39).
// A platform flush with the ground is not a platform, and a platform with a
// wall around it is a kerb the player walks into and the machine taxis over.
// So the authored radius stays FLAT DECK, lifted by Lift (0.4 m by default),
// and an APRON is graded around it: a few metres of ramp whose height profile
// is a smoothstep, leaving the deck flat at the top and meeting the ground flat
// at the bottom. That is what "smooth" has to mean for something a man walks up
// - no step at either end - and on level ground it works out at nine degrees at
// its steepest. The apron reads the ground at every one of its rings and never sinks
// below it, so on a slope it becomes an embankment on the low side and simply
// runs into the hill on the high one.
//
// WHY THE PAD IS PAINTED AND NOT COLOURED (6.39).
// It used to be three flat colours on three meshes - grey deck, black disc,
// white H. Flat colour under the Standard shader reads in game as plastic, and
// black-and-white beside a brown hillside reads as a poster dropped on the map.
// Now the whole pad is ONE mesh with one texture from helipad_texture.py:
// material, rust, oil, soot, worn paint and the dirt of the apron, mapped
// radius-relative so the authored rim always lands on texture radius DeckUv and
// one image fits an 8 m pad and a 60 m one. Without the files the old flat
// colours are still the fallback.
//
// WHY THE GROUND UNDER IT IS CLEARED (6.39).
// The map was planted long before any pad was authored, and a deck laid over it
// has a pine growing through the H. See "the ground" below: props, terrain
// trees and grass are three different systems and each is taken out its own
// way, all of it in memory and all of it restored when the pads change.
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

        /// <summary>Segments around the deck. A pad is a disc, and a disc with
        /// too few sides is a cog: 32 is smooth at walking distance on a small
        /// pad, but the same 32 on a 60 m pad leaves a 12 m chord. So the count
        /// follows the radius, and every ring of one pad uses the same one.</summary>
        static int Segments(float radius)
        {
            return Mathf.Clamp(Mathf.RoundToInt(radius * 2.2f), 32, 96);
        }

        /// <summary>How far the deck stands over the highest ground under it.
        /// Enough to read as a built platform and to keep the terrain from
        /// poking through between two height samples; low enough that the apron
        /// carries it back down to the ground within a few metres.</summary>
        static float DeckLift
        {
            get { return _cfgLift == null ? 0.4f : Mathf.Clamp(_cfgLift.Value, 0.05f, 2f); }
        }

        /// <summary>How far the skirt is driven into the slope at the rim, so
        /// no gap opens between the deck and the ground behind it.</summary>
        const float SkirtBite = 0.6f;

        /// <summary>The graded ramp OUTSIDE the authored rim. What the editor
        /// drew stays flat deck - that is what the machine lands on - and the
        /// ramp is added around it, wide enough to keep the steepest part of
        /// the ramp under ten degrees on level ground.</summary>
        static float ApronWidth(float radius)
        {
            return Mathf.Clamp(radius * 0.22f, 2.5f, 5f);
        }

        /// <summary>The apron in rings, as a fraction of its width.</summary>
        static readonly float[] ApronRings = new float[] { 0.22f, 0.48f, 0.74f, 1f };

        /// <summary>Texture radius of the authored rim: the deck texture is
        /// mapped radius-relative, not in metres, so the rim always lands here
        /// and the apron fills what is left (helipad_texture.py DECK).</summary>
        const float DeckUv = 0.80f;

        internal sealed class Pad
        {
            internal string Name, Scene, Surface, Key;
            internal bool Enabled, Marker;
            internal float X, Z, Heading, Radius;
            /// <summary>Deck height once the pad has been built; 0 before.</summary>
            internal float Deck;
            internal GameObject Go;
        }

        static ConfigEntry<bool> _cfgEnabled, _cfgMarkers, _cfgClear;
        static ConfigEntry<float> _cfgLift;
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
            _cfgLift = cfg.Bind("Helipads", "Lift", 0.4f,
                "How far the deck stands over the ground, in metres. The ramp around it is graded to match.");
            _cfgClear = cfg.Bind("Helipads", "ClearGround", true,
                "Remove the trees, bushes, grass and props that stand where a pad is built.");
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
                if (scene == _builtScene && key == _builtKey) { Sweep(); return; }
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
            Restore();
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
                // Before the deck, not after: a pad whose surface is "cleared"
                // builds no geometry at all and still means a cleared site.
                if (ClearOn) ClearProps(p, deck);
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
            if (built > 0 && ClearOn) ClearTerrain();
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

            int n = Segments(p.Radius);
            float apron = ApronWidth(p.Radius);
            float outer = p.Radius + apron;
            int rings = ApronRings.Length;

            // The apron is surveyed, not assumed: the ground is read at every
            // ring of every segment, the profile eases from the deck down to the
            // rim, and the surface is then held ABOVE whatever it found. The
            // skirt below it follows the ground at the outer rim, so the pad
            // seals against the slope instead of floating over it.
            float[] drop = new float[n];
            float[,] ramp = new float[rings, n];
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                float y;
                if (!Ground(new Vector3(p.X + cos * outer, 0f, p.Z + sin * outer), out y))
                    y = deck - DeckLift;
                drop[i] = Mathf.Min(y - SkirtBite, deck - SkirtBite) - deck;

                float rim = Mathf.Min(y - deck, 0f);
                for (int k = 0; k < rings; k++)
                {
                    float t = ApronRings[k];
                    float here;
                    if (!Ground(new Vector3(p.X + cos * (p.Radius + apron * t), 0f,
                                            p.Z + sin * (p.Radius + apron * t)), out here))
                        here = y;
                    // Smoothstep, so the ramp leaves the deck flat and arrives
                    // at the ground flat. Then lifted clear of the ground it
                    // crosses, and never allowed above the deck it came from.
                    float profile = rim * (t * t * (3f - 2f * t));
                    ramp[k, i] = Mathf.Min(Mathf.Max(profile, here - deck + 0.02f), 0f);
                }
            }

            Mesh body = DeckMesh(p.Radius, apron, ramp, drop, n);
            GameObject surface = new GameObject("deck");
            surface.transform.SetParent(go.transform, false);
            surface.AddComponent<MeshFilter>().sharedMesh = body;
            surface.AddComponent<MeshRenderer>().sharedMaterial = SurfaceMaterial(p.Surface);
            MeshCollider collider = surface.AddComponent<MeshCollider>();
            collider.sharedMesh = body;
            return go;
        }

        /// <summary>Deck, apron and skirt in ONE mesh, and therefore one
        /// collider and one draw call.
        ///
        /// The rows, from the middle outwards: the flat deck out to `radius`,
        /// then one row per apron ring carrying the surveyed heights in `ramp`,
        /// then the skirt - whose top row REPEATS the last apron ring so the
        /// wall keeps a hard edge while everything above it is smoothed into one
        /// surface by RecalculateNormals.
        ///
        /// The UVs are radius-relative, not metric: the rim lands on DeckUv and
        /// the apron fills the rest, which is what lets one painted texture fit
        /// every pad size (helipad_texture.py).</summary>
        static Mesh DeckMesh(float radius, float apron, float[,] ramp, float[] drop, int n)
        {
            int rings = ApronRings.Length;
            int rows = rings + 3;                      // rim, apron, skirt top, skirt bottom
            Vector3[] verts = new Vector3[1 + rows * n];
            Vector2[] uvs = new Vector2[verts.Length];
            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                Ring(verts, uvs, 1 + i, cos, sin, radius, 0f, DeckUv);
                for (int k = 0; k < rings; k++)
                {
                    float t = ApronRings[k];
                    Ring(verts, uvs, 1 + (k + 1) * n + i, cos, sin,
                         radius + apron * t, ramp[k, i], DeckUv + (1f - DeckUv) * t);
                }
                Ring(verts, uvs, 1 + (rings + 1) * n + i, cos, sin,
                     radius + apron, ramp[rings - 1, i], 1f);
                Ring(verts, uvs, 1 + (rings + 2) * n + i, cos, sin,
                     radius + apron, drop[i], 1f);
            }

            int[] tris = new int[n * 3 + (rows - 1) * n * 6];
            int t2 = 0;
            for (int i = 0; i < n; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % n;
                tris[t2++] = 0; tris[t2++] = b; tris[t2++] = a;        // deck, facing up
            }
            for (int row = 0; row < rows - 1; row++)
                for (int i = 0; i < n; i++)
                {
                    int a = 1 + row * n + i, b = 1 + row * n + (i + 1) % n;
                    int c = 1 + (row + 1) * n + i, d = 1 + (row + 1) * n + (i + 1) % n;
                    tris[t2++] = a; tris[t2++] = b; tris[t2++] = d;    // ramp and skirt,
                    tris[t2++] = a; tris[t2++] = d; tris[t2++] = c;    // facing up and out
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

        /// <summary>One vertex of one ring: `tex` is its radius in the texture,
        /// where 0 is the middle of the image and 1 its inscribed edge.</summary>
        static void Ring(Vector3[] verts, Vector2[] uvs, int at,
                         float cos, float sin, float r, float y, float tex)
        {
            verts[at] = new Vector3(cos * r, y, sin * r);
            uvs[at] = new Vector2(0.5f + cos * tex * 0.5f, 0.5f + sin * tex * 0.5f);
        }

        static Material _concrete, _steel;

        /// <summary>The painted deck: helipad_&lt;surface&gt;.png with its normal
        /// and metallic/gloss maps, on the same shader chain the weapons use.
        /// Without the files the pad falls back to the flat colours it had
        /// before 6.39 - a mod installed without its assets still builds a pad,
        /// it just builds a dull one.</summary>
        static Material SurfaceMaterial(string kind)
        {
            if (kind == "steel")
            {
                if (_steel == null) _steel = Painted("steel",
                    new Color(0.24f, 0.22f, 0.20f), 0.45f, 0.18f);
                return _steel;
            }
            if (_concrete == null) _concrete = Painted("concrete",
                new Color(0.42f, 0.40f, 0.36f), 0.02f, 0.08f);
            return _concrete;
        }

        static Material Painted(string kind, Color fallback, float metallic, float gloss)
        {
            Material mat = Make("NDR_Helipad_" + kind, fallback, metallic, gloss);
            if (mat == null) return null;
            Texture2D albedo = Tex("helipad_" + kind + ".png", false);
            if (albedo != null)
            {
                mat.mainTexture = albedo;
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
                // White, or the fallback colour would multiply the painting.
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            }
            Texture2D normal = Tex("helipad_" + kind + "_normal.png", true);
            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                // Without the keyword the map is set but never read - the
                // commonest mistake with the Standard shader (Revival.Items.cs).
                mat.EnableKeyword("_NORMALMAP");
            }
            Texture2D metal = Tex("helipad_" + kind + "_metal.png", true);
            if (metal != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", metal);
                if (mat.HasProperty("_GlossMapScale")) mat.SetFloat("_GlossMapScale", 1f);
                // 0 = smoothness from the map's ALPHA, the way the game's own
                // vehicles carry it.
                if (mat.HasProperty("_SmoothnessTextureChannel"))
                    mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.EnableKeyword("_METALLICGLOSSMAP");
            }
            // A pad is never wet plastic. Whatever the shader inherited, the
            // highlight stays where this file put it.
            if (mat.HasProperty("_SpecGlossMap"))
            {
                mat.SetTexture("_SpecGlossMap", null);
                mat.DisableKeyword("_SPECGLOSSMAP");
            }
            RevivalPlugin.L.LogInfo("Helipads: " + kind + " surface, painted="
                + (albedo != null) + " normal=" + (normal != null)
                + " metal=" + (metal != null) + ".");
            return mat;
        }

        /// <summary>A texture from the plugin's asset folder, quietly: a missing
        /// file is a fallback, not a fault, and Assets.Texture would warn about
        /// it on every build.</summary>
        static Texture2D Tex(string file, bool linear)
        {
            try
            {
                string dir = RevivalPlugin.AssetDir;
                if (dir == null || !File.Exists(Path.Combine(dir, file))) return null;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Helipads: " + file + ": " + ex.Message);
                return null;
            }
            return Assets.Texture(file, linear, true);
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

        // ========================================================== the ground

        // WHAT STANDS IN THE WAY OF A PAD.
        // The deck is laid on a map that was planted years before the pad was
        // authored: 57,386 trees on the visual terrain, 35,005 more on the
        // collision one, eight grass layers at 1.24 m per cell, and whatever the
        // streamed chunk scenes hold. Three different systems own those objects
        // and none of them can be switched off the same way:
        //
        //   props   ordinary GameObjects in the chunk scenes. Switched off, the
        //           way MapUnblock does it, and switched on again afterwards.
        //           They STREAM, so this is also swept every few seconds: a
        //           chunk that comes back brings new objects, active.
        //   trees   TerrainData.treeInstances - not objects at all. The array is
        //           written back without the entries inside the pad, and what
        //           was taken out is kept so it can go back in.
        //   grass   TerrainData detail layers, the same story per cell.
        //
        // Nothing here touches a file: all three live in memory for the session,
        // and Restore() puts every one of them back when the pad table changes,
        // when the pads are switched off and when the scene changes. What it
        // will NOT do is remove a building the pad happens to clip, anything
        // with a body, a character or a PhotonView, or the terrain itself - a
        // pad may clear a site, it may not punch a hole in the world.

        /// <summary>One thing taken out of the world, and everything needed to
        /// put it back: a list of tree instances OR one detail patch.</summary>
        sealed class Take
        {
            internal Terrain Terrain;
            internal TerrainData Data;
            internal TerrainCollider Collider;
            internal List<TreeInstance> Trees;
            internal int Layer, X, Y;
            internal int[,] Detail;
        }

        /// <summary>The widest thing a pad is allowed to remove, in metres. A
        /// tree, a bush, a rock, a fence segment - yes. A house - never.</summary>
        const float MaxProp = 14f;

        static List<GameObject> _hidden = new List<GameObject>();
        static List<Take> _taken = new List<Take>();
        /// <summary>What Loose() answered for a transform during ONE sweep.
        /// Without it the climb to a prop's parent measures the whole chunk
        /// container again for every single prop it holds, which is a scan of
        /// thousands of colliders per tree.</summary>
        static Dictionary<int, bool> _looseCache = new Dictionary<int, bool>();
        static Type _photonView;
        static bool _photonLookedUp;
        static float _nextSweep;

        static bool ClearOn { get { return _cfgClear == null || _cfgClear.Value; } }

        /// <summary>Everything a pad covers: the deck, the apron around it and a
        /// metre beyond, so a trunk standing on the very edge goes too.</summary>
        static float Footprint(Pad p)
        {
            return p.Radius + ApronWidth(p.Radius) + 1f;
        }

        /// <summary>Is this point inside any pad of the map that is loaded?</summary>
        static bool Covered(float x, float z)
        {
            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene)) continue;
                float reach = Footprint(p);
                float dx = x - p.X, dz = z - p.Z;
                if (dx * dx + dz * dz <= reach * reach) return true;
            }
            return false;
        }

        static void ClearProps(Pad p, float deck)
        {
            float reach = Footprint(p);
            _looseCache.Clear();
            try
            {
                Collider[] hits = Physics.OverlapBox(
                    new Vector3(p.X, deck + 14f, p.Z), new Vector3(reach, 28f, reach),
                    Quaternion.identity, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                int gone = 0;
                for (int i = 0; i < hits.Length; i++)
                {
                    GameObject go = Prop(hits[i], p, reach);
                    if (go == null || _hidden.Contains(go)) continue;
                    _hidden.Add(go);
                    go.SetActive(false);        // every LOD, collider and obstacle with it
                    gone++;
                }
                if (gone > 0)
                    RevivalPlugin.L.LogInfo("Helipads: " + gone
                        + " prop(s) cleared off pad " + p.Name + ".");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Helipads: clearing " + p.Name + ": " + ex.Message);
            }
        }

        /// <summary>The object to switch off for this collider, or null.</summary>
        static GameObject Prop(Collider hit, Pad p, float reach)
        {
            if (hit == null) return null;
            Transform t = hit.transform;
            if (t == null || !t.gameObject.activeInHierarchy) return null;
            if (hit is TerrainCollider) return null;
            if (_root != null && t.IsChildOf(_root.transform)) return null;
            if (hit.GetComponentInParent<Rigidbody>() != null) return null;
            if (hit.GetComponentInParent<CharacterController>() != null) return null;
            if (Networked(t)) return null;
            if (!Loose(t, p, reach)) return null;
            // Take the whole prop, not the one collider that was hit: a tree's
            // trunk is a child of the tree, and switching off the trunk alone
            // leaves the crown standing in the air. Climbing stops at the first
            // parent that is too big or reaches outside the pad, which is what
            // keeps a chunk's prop container from being switched off as a whole.
            Transform prop = t;
            // childCount is free and settles most of it: a thing with dozens of
            // children is a container of props, not a prop.
            while (prop.parent != null && prop.parent.childCount <= 64
                   && Loose(prop.parent, p, reach)) prop = prop.parent;
            return prop.gameObject;
        }

        /// <summary>Small enough to be scenery, and standing INSIDE the pad -
        /// the middle of it, not an edge that happens to overlap.</summary>
        static bool Loose(Transform t, Pad p, float reach)
        {
            int id = t.GetInstanceID();
            bool known;
            if (_looseCache.TryGetValue(id, out known)) return known;
            bool answer = false;
            Bounds box;
            if (WorldBounds(t, out box)
                && box.size.x <= MaxProp && box.size.z <= MaxProp)
            {
                float dx = box.center.x - p.X, dz = box.center.z - p.Z;
                answer = dx * dx + dz * dz <= reach * reach;
            }
            _looseCache[id] = answer;
            return answer;
        }

        /// <summary>World bounds from the COLLIDERS first. Static batching can
        /// replace a prop's render mesh with the mesh of a whole chunk, and its
        /// renderer bounds then describe the chunk - which would make every
        /// batched prop look far too big to touch (Revival.MapUnblock.cs).</summary>
        static bool WorldBounds(Transform t, out Bounds box)
        {
            box = new Bounds();
            bool any = false;
            Collider[] colliders = t.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null || colliders[i].isTrigger
                    || !colliders[i].enabled) continue;
                if (!any) { box = colliders[i].bounds; any = true; }
                else box.Encapsulate(colliders[i].bounds);
            }
            if (any) return true;
            Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!any) { box = renderers[i].bounds; any = true; }
                else box.Encapsulate(renderers[i].bounds);
            }
            return any;
        }

        /// <summary>A PhotonView anywhere above it means another client has the
        /// same object and did not switch it off.</summary>
        static bool Networked(Transform t)
        {
            if (!_photonLookedUp)
            {
                _photonLookedUp = true;
                _photonView = RevivalPlugin.TypeByName("PhotonView");
                if (_photonView == null)
                    RevivalPlugin.L.LogInfo("Helipads: no PhotonView type - "
                        + "props are cleared by size and position alone.");
            }
            if (_photonView == null) return false;
            return t.GetComponentInParent(_photonView) != null;
        }

        /// <summary>Trees and grass, once for every pad on the map.
        ///
        /// A tree is an entry in an array, not an object, and the only way to
        /// take one out is to write the array back without it - 57,386 entries,
        /// which is why this runs once over each terrain and not once per pad,
        /// and why a terrain with nothing inside a pad is not touched at all.
        ///
        /// The collision trees sit on a DIFFERENT TerrainData than the visible
        /// ones (REVERSE_ENGINEERING.md 37): GameWorldData/Terrain renders 763
        /// to 767 and two collider-only children carry 768 and 769. So both the
        /// Terrain components AND the TerrainColliders are walked, or a felled
        /// tree leaves its trunk collider standing on the deck.</summary>
        static void ClearTerrain()
        {
            try
            {
                List<Terrain> terrain = new List<Terrain>();
                List<TerrainCollider> hull = new List<TerrainCollider>();
                List<TerrainData> data = new List<TerrainData>();
                Dictionary<int, int> seen = new Dictionary<int, int>();

                Terrain[] active = Terrain.activeTerrains;
                if (active != null)
                    for (int i = 0; i < active.Length; i++)
                    {
                        if (active[i] == null || active[i].terrainData == null) continue;
                        int id = active[i].terrainData.GetInstanceID();
                        if (seen.ContainsKey(id)) continue;
                        seen.Add(id, data.Count);
                        data.Add(active[i].terrainData);
                        terrain.Add(active[i]);
                        hull.Add(active[i].GetComponent<TerrainCollider>());
                    }
                TerrainCollider[] hulls =
                    UnityEngine.Object.FindObjectsOfType<TerrainCollider>();
                for (int i = 0; i < hulls.Length; i++)
                {
                    if (hulls[i] == null || hulls[i].terrainData == null) continue;
                    int id = hulls[i].terrainData.GetInstanceID();
                    if (seen.ContainsKey(id))
                    {
                        int at = seen[id];
                        if (hull[at] == null) hull[at] = hulls[i];
                        continue;
                    }
                    seen.Add(id, data.Count);
                    data.Add(hulls[i].terrainData);
                    terrain.Add(null);
                    hull.Add(hulls[i]);
                }

                for (int i = 0; i < data.Count; i++)
                {
                    Vector3 org = terrain[i] != null
                        ? terrain[i].GetPosition()
                        : hull[i].transform.position;
                    TakeTrees(terrain[i], hull[i], data[i], org);
                    // Grass is drawn by the Terrain and by nothing else, so a
                    // collider-only terrain has none to clear.
                    if (terrain[i] != null) TakeGrass(terrain[i], data[i], org);
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Helipads: clearing the terrain: " + ex.Message);
            }
        }

        static void TakeTrees(Terrain terrain, TerrainCollider hull, TerrainData data,
                              Vector3 org)
        {
            Vector3 size = data.size;
            if (size.x <= 0f || size.z <= 0f) return;
            TreeInstance[] trees = data.treeInstances;
            if (trees == null || trees.Length == 0) return;

            List<TreeInstance> keep = null, taken = null;
            for (int i = 0; i < trees.Length; i++)
            {
                // A tree's position is a fraction of the terrain, not a place.
                Vector3 at = trees[i].position;
                if (!Covered(org.x + at.x * size.x, org.z + at.z * size.z))
                {
                    if (keep != null) keep.Add(trees[i]);
                    continue;
                }
                if (keep == null)
                {
                    keep = new List<TreeInstance>(trees.Length);
                    taken = new List<TreeInstance>();
                    for (int k = 0; k < i; k++) keep.Add(trees[k]);
                }
                taken.Add(trees[i]);
            }
            if (keep == null) return;           // nothing in the way: untouched

            data.treeInstances = keep.ToArray();
            Take take = new Take();
            take.Terrain = terrain;
            take.Data = data;
            take.Collider = hull;
            take.Trees = taken;
            _taken.Add(take);
            Refresh(terrain, hull);
            RevivalPlugin.L.LogInfo("Helipads: " + taken.Count + " tree(s) cleared from "
                + data.name + ".");
        }

        static void TakeGrass(Terrain terrain, TerrainData data, Vector3 org)
        {
            int res = data.detailResolution;
            DetailPrototype[] kinds = data.detailPrototypes;
            if (res <= 0 || kinds == null || kinds.Length == 0) return;
            Vector3 size = data.size;
            if (size.x <= 0f || size.z <= 0f) return;

            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene)) continue;
                float reach = Footprint(p);
                int x0 = Mathf.Clamp(Mathf.FloorToInt((p.X - reach - org.x) / size.x * res), 0, res - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((p.Z - reach - org.z) / size.z * res), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((p.X + reach - org.x) / size.x * res), 0, res - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt((p.Z + reach - org.z) / size.z * res), 0, res - 1);
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w <= 1 || h <= 1) continue;

                for (int layer = 0; layer < kinds.Length; layer++)
                {
                    int[,] patch = data.GetDetailLayer(x0, y0, w, h, layer);
                    if (patch == null) continue;
                    int[,] before = (int[,])patch.Clone();
                    bool changed = false;
                    // The mask is a circle in the middle of a square, so which
                    // of the two indices the engine calls x does not matter.
                    for (int a = 0; a < w; a++)
                        for (int b = 0; b < h; b++)
                        {
                            if (patch[a, b] == 0) continue;
                            float wx = org.x + (x0 + a + 0.5f) / res * size.x;
                            float wz = org.z + (y0 + b + 0.5f) / res * size.z;
                            if (!Covered(wx, wz)) continue;
                            patch[a, b] = 0;
                            changed = true;
                        }
                    if (!changed) continue;
                    Take take = new Take();
                    take.Terrain = terrain;
                    take.Data = data;
                    take.Layer = layer;
                    take.X = x0;
                    take.Y = y0;
                    take.Detail = before;
                    _taken.Add(take);
                    data.SetDetailLayer(x0, y0, layer, patch);
                }
            }
        }

        /// <summary>Make the change visible and solid. Flush redraws the trees;
        /// the collider only rebuilds its tree colliders when it is switched off
        /// and on again, and without that a felled tree is invisible but still
        /// something the player walks into.</summary>
        static void Refresh(Terrain terrain, TerrainCollider hull)
        {
            if (terrain != null) terrain.Flush();
            if (hull != null && hull.enabled)
            {
                hull.enabled = false;
                hull.enabled = true;
            }
        }

        /// <summary>Props only, and only near the camera. The chunk scenes
        /// stream by distance, so a pad on the far side of the map has nothing
        /// loaded to clear, and a pad the player walks up to has its props
        /// switched off within a few seconds of the chunk arriving.</summary>
        static void Sweep()
        {
            if (!ClearOn || _pads.Count == 0) return;
            if (Time.realtimeSinceStartup < _nextSweep) return;
            _nextSweep = Time.realtimeSinceStartup + 3f;
            _hidden.RemoveAll(delegate(GameObject go) { return go == null; });
            Camera cam = Camera.main;
            foreach (Pad p in _pads)
            {
                if (!p.Enabled || !MapScene.Owns(p.Scene) || p.Deck == 0f) continue;
                if (cam != null)
                {
                    Vector3 eye = cam.transform.position;
                    float dx = eye.x - p.X, dz = eye.z - p.Z;
                    if (dx * dx + dz * dz > 600f * 600f) continue;
                }
                ClearProps(p, p.Deck);
            }
        }

        /// <summary>Put the world back. Called before every rebuild and whenever
        /// the pads go away: a pad that is moved must not leave a bald patch and
        /// a missing tree where it used to stand.</summary>
        static void Restore()
        {
            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i] != null) _hidden[i].SetActive(true);
            _hidden.Clear();

            for (int i = _taken.Count - 1; i >= 0; i--)
            {
                Take take = _taken[i];
                try
                {
                    if (take.Data == null) continue;
                    if (take.Trees != null)
                    {
                        // Order does not matter to a tree, so the ones that were
                        // taken out simply go on the end.
                        TreeInstance[] now = take.Data.treeInstances;
                        TreeInstance[] all = new TreeInstance[now.Length + take.Trees.Count];
                        now.CopyTo(all, 0);
                        for (int k = 0; k < take.Trees.Count; k++)
                            all[now.Length + k] = take.Trees[k];
                        take.Data.treeInstances = all;
                        Refresh(take.Terrain, take.Collider);
                    }
                    else if (take.Detail != null)
                        take.Data.SetDetailLayer(take.X, take.Y, take.Layer, take.Detail);
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("Helipads: putting the ground back: "
                        + ex.Message);
                }
            }
            _taken.Clear();
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

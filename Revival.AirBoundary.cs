// Next Day: Survival - Revival Toolkit
//
// SOFT AIR BOUNDARY AND TERRAIN SKIRT (task B6).
//
// Why: the world is 10 x 5 km of map metres (units) with the east tile, and
// the airfield's runway runs north-south - an An-2 at 180 km/h crosses the
// whole 5 km in half a minute and needs about a kilometre to turn round. Past
// the terrain's edge there was nothing: no floor, and the void under the sky.
//
// WHAT IT DOES, for the local PILOT of the player An-2 and the player Mi-8
// (NPC aircraft fly their own scripted legs and are not touched; ground
// vehicles and men on foot keep the old border):
//   - the playable rectangle is the world's (EastWorld.Extended with the tile,
//     GW_Scene_1's -2500..2500 without it); past it lies a BUFFER (BufferU,
//     2000 u) where an aircraft may fly;
//   - out there the pilot gets "LEAVING THE AREA - TURN BACK" and a countdown
//     (WarnSeconds); a pilot who turns back himself hears no more of it;
//   - at zero the aircraft turns back BY ITSELF, gently: the An-2 is banked
//     (up to 55 deg) towards the map through its own flight assist, the Mi-8
//     yaws round and its disc pulls towards the map. The pilot keeps pitch,
//     power and some roll; the deeper into the buffer, the less he can argue.
//     The countdown is shortened when the speed would carry the turn past the
//     buffer (depth + turn radius + 1 s of roll-in > 75 % of it; flown by
//     research/air_boundary_sim.py). Never a wall, never a death;
//   - the aircraft is kept above the skirt (a gentle climb), since the skirt
//     has no collider.
// Guide is the one entry: each aircraft asks it once per flight frame.
//
// THE SKIRT: one ring of low-poly terrain around the rectangle, out to 12 km,
// that continues the edge: its inner rim is the terrain's own edge height
// (sampled every 50 u, the highest of three samples so no sliver of sky shows
// under it, and tucked 25 u under the terrain), then the height drifts to a
// smoothed edge profile, then to the region's mean, with value-noise hills
// fading in over the first 800 u. One opaque Standard material, a generated
// forest/field patch texture in the far-forest colours, lit and fogged like
// the terrain (the ViewDistance fog makes the haze). No colliders, no
// shadows, no probes, no NPCs, no loot. Four meshes (one per side, so the
// frustum culls what is behind), about 11k vertices in all.
//
// Cost: the skirt is built once per loaded world (about 2400 terrain samples,
// a few ms, logged) and then only drawn - four draw calls, ~20k triangles.
// Per frame: a 2 s timer check; Guide is a rectangle test for the pilot.
//
// Settings: [AirBoundary] Enabled, BufferU, WarnSeconds, Skirt.
//
// Seams: RevivalPlugin.Awake (BindConfig), RevivalPlugin.Update (Tick),
// PlayerAn2.Fly / PlayerHeli.Fly (Guide), PlayerAn2.Draw / PlayerHeli.Draw
// (Draw).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextDayRevival
{
    internal static class AirBoundary
    {
        static ConfigEntry<bool> _cfgEnabled, _cfgSkirt;
        static ConfigEntry<float> _cfgBuffer, _cfgWarn;

        /// <summary>Bank the An-2's auto-turn may use (the A/D MaxBank is 40).</summary>
        internal const float TurnBank = 55f;
        /// <summary>Height over the skirt below which an aircraft is lifted.</summary>
        const float ClearU = 30f;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string S = "AirBoundary";
            _cfgEnabled = cfg.Bind(S, "Enabled", true,
                "Soft map edge for the aircraft a player flies (An-2, Mi-8): past "
                + "the edge of the map a warning with a countdown, then the "
                + "aircraft turns back by itself - no wall, no crash. Ground "
                + "vehicles and men on foot keep the old border; NPC aircraft fly "
                + "their own paths.");
            _cfgBuffer = cfg.Bind(S, "BufferU", 2000f,
                "Map metres past the edge an aircraft may fly. The auto-turn is "
                + "timed to stay inside it.");
            _cfgWarn = cfg.Bind(S, "WarnSeconds", 5f,
                "Seconds of warning past the edge before the auto-turn (less when "
                + "the speed would carry the turn past the buffer).");
            _cfgSkirt = cfg.Bind(S, "Skirt", true,
                "Low-poly hills and forest beyond the map's edge, so the land does "
                + "not end in the void when seen from the air. Scenery only: no "
                + "collision, nothing to find. Tiny cost (four meshes, built once).");
        }

        static bool On { get { return _cfgEnabled == null || _cfgEnabled.Value; } }
        static float Buffer { get { return Mathf.Max(500f, _cfgBuffer == null ? 2000f : _cfgBuffer.Value); } }
        static float WarnSeconds { get { return Mathf.Max(0f, _cfgWarn == null ? 5f : _cfgWarn.Value); } }

        /// <summary>The playable rectangle in world x/z.</summary>
        internal static Rect Play
        {
            get { return EastWorld.On ? EastWorld.Extended : new Rect(-2500f, -2500f, 5000f, 5000f); }
        }

        // ============================================================ guidance

        static float _warnSince = -1f, _lastGuide = -10f, _countdown;
        static bool _turning, _inbound;
        static int _sign;
        static int _frame = -1;

        /// <summary>
        /// One flight frame of the local pilot's aircraft. pos is the world
        /// position, velU the velocity in units per second, heading the
        /// direction the auto-turn steers (degrees, compass sense), radiusU
        /// the aircraft's turn radius in units at its auto-turn rate.
        /// Returns false inside the map (nothing to do). Outside: target is
        /// the heading back into the map, turn the signed error from heading
        /// (kept on one side when the target is behind), strength 0 while
        /// the countdown runs and 0.6..1 during the auto-turn, climb true when
        /// the aircraft is too low over the skirt.
        /// </summary>
        internal static bool Guide(Vector3 pos, Vector3 velU, float heading, float radiusU,
                                   out float target, out float turn, out float strength, out bool climb)
        {
            target = heading; turn = 0f; strength = 0f; climb = false;
            if (!On) { Reset(); return false; }
            Rect r = Play;
            float depth = Depth(r, pos.x, pos.z);
            if (depth <= 0f) { Reset(); return false; }

            float now = Time.time;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            // A flight that left the map and came back through a load, a
            // respawn or another aircraft starts a fresh warning.
            if (now - _lastGuide > 1f) Reset();
            _lastGuide = now;
            _frame = Time.frameCount;

            // Aim at the nearest point of the map drawn in by 600 u (or its
            // middle when it is narrower than that), not at the edge itself.
            float inset = Mathf.Min(600f, Mathf.Min(r.width, r.height) * 0.5f - 1f);
            float ax = Mathf.Clamp(pos.x, r.xMin + inset, r.xMax - inset) - pos.x;
            float az = Mathf.Clamp(pos.z, r.yMin + inset, r.yMax - inset) - pos.z;
            target = Mathf.Atan2(ax, az) * Mathf.Rad2Deg;
            Vector2 inward = new Vector2(ax, az).normalized;
            float outward = -(velU.x * inward.x + velU.z * inward.y);

            float err = Mathf.DeltaAngle(heading, target);
            // The target nearly behind: keep turning the way the turn began,
            // or the side would flip with every degree of wobble.
            if (Mathf.Abs(err) > 150f && _sign != 0 && (err >= 0f ? 1 : -1) != _sign)
                err += _sign * 360f;
            else
                _sign = err >= 0f ? 1 : -1;
            turn = err;

            if (_warnSince < 0f) _warnSince = now;
            _inbound = outward < 0f;
            // Flying back in by himself: the clock stands still.
            if (_inbound && !_turning) _warnSince += dt;
            float left = WarnSeconds - (now - _warnSince);
            // The turn has to fit: its radius plus a second of rolling in,
            // inside three quarters of the buffer.
            float room = 0.75f * Buffer - radiusU - depth - Mathf.Max(0f, outward);
            float byRoom = outward > 1f ? room / outward : 999f;
            _countdown = Mathf.Max(0f, Mathf.Min(left, byRoom));
            if (!_turning && _countdown <= 0f && !_inbound)
            {
                _turning = true;
                RevivalPlugin.L.LogInfo("AirBoundary: auto-turn back into the map at "
                    + pos.ToString("0") + ", " + Mathf.RoundToInt(depth) + " u past the edge.");
            }
            // Pointing and moving back in: the job is done, the pilot has it
            // again (a helicopter's nose can point in while it still drifts out).
            if (_turning && Mathf.Abs(err) < 20f && outward <= 0f)
            {
                _turning = false;
                _warnSince = now;
            }
            if (_turning || depth > Buffer)
                strength = Mathf.Clamp01(0.6f + 0.4f * depth / Buffer);

            float ground;
            if (Height(pos.x, pos.z, out ground) && pos.y < ground + ClearU) climb = true;
            return true;
        }

        static void Reset()
        {
            _warnSince = -1f;
            _turning = false;
            _inbound = false;
            _sign = 0;
        }

        /// <summary>How far outside the rectangle, 0 inside.</summary>
        static float Depth(Rect r, float x, float z)
        {
            float dx = Mathf.Max(0f, Mathf.Max(r.xMin - x, x - r.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(r.yMin - z, z - r.yMax));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ================================================================ HUD

        /// <summary>The warning, drawn by the pilot's own HUD (PlayerAn2.Draw,
        /// PlayerHeli.Draw) on Repaint.</summary>
        internal static void Draw()
        {
            if (_frame < 0 || Time.frameCount - _frame > 2 || Time.time - _lastGuide > 0.3f) return;
            float cx = Screen.width * 0.5f;
            float y = Screen.height * 0.5f - 190f;
            bool blink = Mathf.Repeat(Time.time, 0.8f) < 0.55f;
            string head = Loc.T("ПОКИДАЕШЬ РАЙОН - РАЗВОРАЧИВАЙСЯ", "LEAVING THE AREA - TURN BACK");
            string sub;
            Color c;
            if (_turning)
            {
                sub = Loc.T("Автоматический разворот к карте", "Auto-turn - heading back to the map");
                c = new Color(1f, 0.55f, 0.2f, 1f);
            }
            else if (_inbound)
            {
                sub = Loc.T("Возвращаешься в район", "Returning to the area");
                c = new Color(1f, 0.85f, 0.35f, 1f);
                blink = true;
            }
            else
            {
                sub = Loc.T("Автоматический разворот через ", "Auto-turn in ")
                    + Mathf.CeilToInt(_countdown) + Loc.T(" с", " s");
                c = new Color(1f, 0.3f, 0.22f, 1f);
            }
            if (blink) Line("<b>" + head + "</b>", cx, y, c, 24);
            Line(sub, cx, y + 32f, new Color(0.95f, 0.92f, 0.85f, 1f), 16);
        }

        static GUIStyle _style;

        static void Line(string text, float cx, float y, Color colour, int size)
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.richText = true;
                _style.wordWrap = false;
            }
            _style.fontSize = size;
            Vector2 sz = _style.CalcSize(new GUIContent(text));
            Rect at = new Rect(cx - sz.x * 0.5f, y, sz.x, sz.y);
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(at.x + 1.5f, at.y + 1.5f, at.width, at.height), text, _style);
            GUI.color = colour;
            GUI.Label(at, text, _style);
            GUI.color = was;
        }

        // ============================================================== skirt

        const float Step = 50f;             // edge sample spacing, units
        const float Tuck = 25f;             // inner rim under the terrain
        const float HillU = 90f;            // hill amplitude
        const int FanSteps = 6;             // columns round a corner
        static readonly float[] Rings = {
            -Tuck, 0f, 40f, 100f, 200f, 350f, 550f, 800f, 1150f, 1600f, 2200f,
            3000f, 4000f, 5300f, 7000f, 9200f, 12000f };

        static GameObject _skirt;
        static Rect _skirtRect;
        static float _nextTry;
        static bool _warned;
        static float[] _edge, _smooth;      // per Step of perimeter, from (xMin, yMin) anticlockwise
        static float _mean;
        static Rect _hRect;                 // the rectangle _edge belongs to
        static Material _mat;

        /// <summary>Builds the skirt once the world (and the tile) is up; again
        /// after a scene change took it away.</summary>
        internal static void Tick()
        {
            bool want = _cfgSkirt == null || _cfgSkirt.Value;
            if (!want)
            {
                if (_skirt != null) { UnityEngine.Object.Destroy(_skirt); _skirt = null; }
                return;
            }
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 2f;
            Rect r = Play;
            if (_skirt != null && _skirtRect == r) return;
            try { Build(r); }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("AirBoundary skirt: " + ex); }
                _nextTry = Time.unscaledTime + 30f;
            }
        }

        /// <summary>The world's edge point at perimeter distance s (anticlockwise
        /// from the south-west corner) and the side it is on (0 south, 1 east,
        /// 2 north, 3 west).</summary>
        static Vector2 Perimeter(Rect r, float s, out int side)
        {
            float w = r.width, h = r.height, p = 2f * (w + h);
            s = Mathf.Repeat(s, p);
            if (s < w) { side = 0; return new Vector2(r.xMin + s, r.yMin); }
            s -= w;
            if (s < h) { side = 1; return new Vector2(r.xMax, r.yMin + s); }
            s -= h;
            if (s < w) { side = 2; return new Vector2(r.xMax - s, r.yMax); }
            s -= w;
            side = 3;
            return new Vector2(r.xMin, r.yMax - s);
        }

        static readonly Vector2[] Normals = {
            new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(-1f, 0f) };

        /// <summary>Perimeter distance of the rectangle point nearest to x/z.</summary>
        static float ArcOf(Rect r, float x, float z)
        {
            float qx = Mathf.Clamp(x, r.xMin, r.xMax), qz = Mathf.Clamp(z, r.yMin, r.yMax);
            float w = r.width, h = r.height;
            if (z <= r.yMin) return qx - r.xMin;
            if (x >= r.xMax) return w + (qz - r.yMin);
            if (z >= r.yMax) return w + h + (r.xMax - qx);
            return 2f * w + h + (r.yMax - qz);
        }

        /// <summary>Terrain height: the east world's own lookup (the tile's
        /// drawing terrain), else whichever active terrain contains the point.</summary>
        static bool Ground(Vector3 at, out float y)
        {
            if (EastWorld.On) return RevivalTroopInsertion.TerrainHeight(at, out y);
            y = 0f;
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
            {
                Terrain t = all[i];
                // A terrain that only draws trees (GW_Scene_1's billboard
                // twins) has a flat stand-in heightmap: not the ground.
                if (t == null || t.terrainData == null || !t.drawHeightmap) continue;
                Vector3 o = t.GetPosition(), size = t.terrainData.size;
                if (at.x < o.x || at.z < o.z || at.x > o.x + size.x || at.z > o.z + size.z) continue;
                y = o.y + t.SampleHeight(at);
                return true;
            }
            return false;
        }

        /// <summary>Terrain edge height sampled along the perimeter: the highest
        /// of three samples 2 u inside. False when the world is not up.</summary>
        static bool SampleEdge(Rect r)
        {
            int n = Mathf.RoundToInt(2f * (r.width + r.height) / Step);
            float[] e = new float[n];
            int missing = 0;
            for (int i = 0; i < n; i++)
            {
                float best = float.NaN;
                for (int k = -1; k <= 1; k++)
                {
                    int side;
                    Vector2 p = Perimeter(r, i * Step + k * 17f, out side);
                    Vector3 at = new Vector3(Mathf.Clamp(p.x, r.xMin + 2f, r.xMax - 2f), 0f,
                                             Mathf.Clamp(p.y, r.yMin + 2f, r.yMax - 2f));
                    float y;
                    if (!Ground(at, out y)) continue;
                    if (float.IsNaN(best) || y > best) best = y;
                }
                e[i] = best;
                if (float.IsNaN(best)) missing++;
            }
            if (missing > n / 10) return false;
            // A hole in the samples takes the last good height.
            float last = float.NaN;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    if (!float.IsNaN(e[i])) last = e[i];
                    else if (!float.IsNaN(last) && pass == 1) e[i] = last;
                }
            double sum = 0;
            for (int i = 0; i < n; i++) sum += e[i];
            _mean = (float)(sum / n);
            // The smoothed profile: +-600 u box filter round the ring.
            const int half = 12;
            float[] m = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = 0f;
                for (int k = -half; k <= half; k++) t += e[((i + k) % n + n) % n];
                m[i] = t / (2 * half + 1);
            }
            _edge = e;
            _smooth = m;
            _hRect = r;
            return true;
        }

        static float Along(float[] a, float s)
        {
            int n = a.Length;
            float f = Mathf.Repeat(s / Step, n);
            int i = (int)f;
            return Mathf.Lerp(a[i % n], a[(i + 1) % n], f - i);
        }

        /// <summary>The skirt's height at a point outside the rectangle.</summary>
        static float Skirt(Rect r, float x, float z)
        {
            float d = Depth(r, x, z);
            float s = ArcOf(r, x, z);
            float e = Along(_edge, s), m = Along(_smooth, s);
            float t1 = Smooth((d - 40f) / 900f);
            float t2 = Smooth((d - 600f) / 5000f);
            float y = Mathf.Lerp(e, m, t1);
            y = Mathf.Lerp(y, _mean - 30f, t2);
            y += Fbm(x, z) * HillU * Smooth(d / 800f);
            // Past the far clip of any profile the rim sinks below the horizon.
            if (d > 9000f) y -= (d - 9000f) * 0.06f;
            return y;
        }

        /// <summary>Skirt height under a point beyond the edge, for the
        /// aircraft's clearance. False inside the map or with no skirt data.</summary>
        internal static bool Height(float x, float z, out float y)
        {
            y = 0f;
            if (_edge == null || _hRect != Play) return false;
            if (Depth(_hRect, x, z) <= 0f) return false;
            y = Skirt(_hRect, x, z);
            return true;
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        static float Hash(int x, int z)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        static float Value(float x, float z)
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float tx = x - x0, tz = z - z0;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);
            float a = Mathf.Lerp(Hash(x0, z0), Hash(x0 + 1, z0), tx);
            float b = Mathf.Lerp(Hash(x0, z0 + 1), Hash(x0 + 1, z0 + 1), tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>Hills, -1..1: a 1.7 km swell and 600 u knolls.</summary>
        static float Fbm(float x, float z)
        {
            return (0.7f * Value(x / 1700f, z / 1700f)
                    + 0.3f * Value(x / 600f + 17.3f, z / 600f - 9.1f)) * 2f - 1f;
        }

        struct Column
        {
            internal Vector2 P, N;
            internal float S;
        }

        static void Build(Rect r)
        {
            // The tile carries the east side: wait for it.
            float probe;
            if (EastWorld.On && !Ground(
                    new Vector3(r.xMax - 100f, 0f, r.center.y), out probe)) return;
            // Not in a world (menu, loading): five cheap probes, no ring of samples.
            if (!Ground(new Vector3(r.center.x, 0f, r.center.y), out probe)
                || !Ground(new Vector3(r.xMin + 20f, 0f, r.center.y), out probe)
                || !Ground(new Vector3(r.xMax - 20f, 0f, r.center.y), out probe)
                || !Ground(new Vector3(r.center.x, 0f, r.yMin + 20f), out probe)
                || !Ground(new Vector3(r.center.x, 0f, r.yMax - 20f), out probe))
                return;
            Stopwatch sw = Stopwatch.StartNew();
            if (!SampleEdge(r)) return;
            if (_skirt != null) UnityEngine.Object.Destroy(_skirt);

            // Columns anticlockwise; a fan of normals at each corner.
            List<Column> cols = new List<Column>();
            List<int> starts = new List<int>();
            int n = _edge.Length;
            for (int i = 0; i < n; i++)
            {
                int side;
                float s = i * Step;
                Vector2 p = Perimeter(r, s, out side);
                bool corner = Mathf.Abs(s) < 0.01f || Mathf.Abs(s - r.width) < 0.01f
                    || Mathf.Abs(s - r.width - r.height) < 0.01f
                    || Mathf.Abs(s - 2f * r.width - r.height) < 0.01f;
                if (corner)
                {
                    starts.Add(cols.Count);
                    Vector2 a = Normals[(side + 3) % 4], b = Normals[side];
                    for (int k = 0; k <= FanSteps; k++)
                    {
                        Column c;
                        c.P = p; c.S = s;
                        c.N = Vector2.Lerp(a, b, (float)k / FanSteps).normalized;
                        cols.Add(c);
                    }
                }
                else
                {
                    Column c;
                    c.P = p; c.S = s; c.N = Normals[side];
                    cols.Add(c);
                }
            }
            cols.Add(cols[0]);              // close the ring
            starts.Add(cols.Count - 1);

            _skirt = new GameObject("NDR_AirSkirt");
            Material mat = Mat();
            int verts = 0, tris = 0;
            for (int part = 0; part + 1 < starts.Count; part++)
            {
                int c0 = starts[part], c1 = starts[part + 1];
                Mesh mesh = Strip(r, cols, c0, c1);
                GameObject go = new GameObject("side" + part);
                go.transform.SetParent(_skirt.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                verts += mesh.vertexCount;
                tris += mesh.triangles.Length / 3;
            }
            _skirtRect = r;
            RevivalPlugin.L.LogInfo("AirBoundary: terrain skirt built round "
                + r.xMin.ToString("0") + ".." + r.xMax.ToString("0") + " x "
                + r.yMin.ToString("0") + ".." + r.yMax.ToString("0") + " - "
                + (starts.Count - 1) + " meshes, " + verts + " vertices, " + tris
                + " triangles, edge mean height " + _mean.ToString("0") + ", "
                + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms.");
        }

        static Mesh Strip(Rect r, List<Column> cols, int c0, int c1)
        {
            int nc = c1 - c0 + 1, nr = Rings.Length;
            Vector3[] v = new Vector3[nc * nr];
            Vector2[] uv = new Vector2[v.Length];
            for (int c = 0; c < nc; c++)
            {
                Column col = cols[c0 + c];
                float e = Along(_edge, col.S);
                for (int k = 0; k < nr; k++)
                {
                    Vector2 p = col.P + col.N * Rings[k];
                    float y;
                    if (Rings[k] < 0f) y = e - 5f;
                    else if (Rings[k] == 0f) y = e;
                    else y = Skirt(r, p.x, p.y);
                    v[c * nr + k] = new Vector3(p.x, y, p.y);
                    uv[c * nr + k] = new Vector2(p.x / 4000f, p.y / 4000f);
                }
            }
            int[] t = new int[(nc - 1) * (nr - 1) * 6];
            int q = 0;
            for (int c = 0; c + 1 < nc; c++)
                for (int k = 0; k + 1 < nr; k++)
                {
                    int a = c * nr + k, b = (c + 1) * nr + k;
                    // Columns anticlockwise seen from above, rings outward:
                    // this order is clockwise from above, Unity's front face.
                    t[q++] = a; t[q++] = b; t[q++] = a + 1;
                    t[q++] = b; t[q++] = b + 1; t[q++] = a + 1;
                }
            Mesh m = new Mesh();
            m.name = "NDR_AirSkirt";
            m.vertices = v;
            m.uv = uv;
            m.triangles = t;
            m.RecalculateNormals();
            m.RecalculateBounds();
            m.UploadMeshData(true);
            return m;
        }

        static Material Mat()
        {
            if (_mat != null) return _mat;
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Diffuse");
            _mat = new Material(shader);
            _mat.name = "NDR_AirSkirt";
            _mat.mainTexture = Land();
            _mat.color = Color.white;
            if (_mat.HasProperty("_Glossiness")) _mat.SetFloat("_Glossiness", 0f);
            if (_mat.HasProperty("_Metallic")) _mat.SetFloat("_Metallic", 0f);
            return _mat;
        }

        /// <summary>Forest and field patches over 4 km, tileable: mostly forest
        /// in the far-forest green, meadows and a few darker stands.</summary>
        static Texture2D Land()
        {
            const int n = 256;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, true);
            tex.name = "NDR_AirSkirtLand";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            Color forest = new Color(0.20f, 0.27f, 0.13f), dark = new Color(0.15f, 0.21f, 0.10f);
            Color field = new Color(0.36f, 0.38f, 0.21f), dry = new Color(0.42f, 0.40f, 0.26f);
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float patch = Tile(x, y, n, 8, 0) * 0.7f + Tile(x, y, n, 32, 5) * 0.3f;
                    float stand = Tile(x, y, n, 16, 11);
                    float fine = Tile(x, y, n, 64, 23);
                    Color open = Color.Lerp(field, dry, Smooth((stand - 0.4f) / 0.4f));
                    Color wood = Color.Lerp(forest, dark, Smooth((stand - 0.5f) / 0.3f));
                    Color c = Color.Lerp(open, wood, Smooth((patch - 0.36f) / 0.1f));
                    c *= 0.85f + 0.3f * fine;
                    px[y * n + x] = new Color32((byte)(255f * Mathf.Clamp01(c.r)),
                        (byte)(255f * Mathf.Clamp01(c.g)), (byte)(255f * Mathf.Clamp01(c.b)), 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>Tileable value noise on a g x g lattice, 0..1.</summary>
        static float Tile(int x, int y, int n, int g, int seed)
        {
            float fx = (float)x * g / n, fy = (float)y * g / n;
            int x0 = (int)fx, y0 = (int)fy;
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            int x1 = (x0 + 1) % g, y1 = (y0 + 1) % g;
            x0 %= g; y0 %= g;
            float a = Mathf.Lerp(Hash(x0 + seed * 101, y0), Hash(x1 + seed * 101, y0), tx);
            float b = Mathf.Lerp(Hash(x0 + seed * 101, y1), Hash(x1 + seed * 101, y1), tx);
            return Mathf.Lerp(a, b, ty);
        }
    }
}

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
// THE SKIRT: static low-resolution meshes with the adjacent terrain's real
// splat controls, albedo/normal maps and P3 canopy mip chain. The inner row
// follows every source heightmap grid intersection; outer rows are decimated.
// Mirrored relief fades gently into regional hills over 6 km. Built in slices
// once the terrain and P3 paint are ready. No colliders or per-frame sampling;
// materials use Unity's terrain lighting/fog without changing global haze.
// F6: AirBoundary.Tick (idle timer, cached pilot height lookup in Guide).
//
// Settings: [AirBoundary] Enabled, BufferU, WarnSeconds, Skirt.
//
// Seams: RevivalPlugin.Awake (BindConfig), RevivalPlugin.Update (Tick),
// PlayerAn2.Fly / PlayerHeli.Fly (Guide), PlayerAn2.Draw / PlayerHeli.Draw
// (Draw).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments.
using System;
using System.Collections;
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
                + "collision, nothing to find. Static meshes, built once after the terrain is ready.");
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
        const float Tuck = 25f;
        const float HillU = 90f;
        const int FanSteps = 6;
        const double SliceMs = 0.35;
        static readonly float[] Rings = {
            -Tuck, 0f, 40f, 100f, 200f, 350f, 550f, 800f, 1150f, 1600f, 2200f,
            3000f, 4000f, 5300f, 7000f, 9200f, 12000f };

        sealed class Source
        {
            internal Terrain T;
            internal TerrainData D;
            internal Vector3 O, Size;
            internal float[,] H; // Unity GetHeights API is [z,x]; serialized data is x-major.
            internal Material[] Mats;
            internal Texture2D FlatNormal;
            internal int N;
            internal bool Contains(float x, float z)
            {
                return x >= O.x - 0.01f && z >= O.z - 0.01f
                    && x <= O.x + Size.x + 0.01f && z <= O.z + Size.z + 0.01f;
            }
            internal Vector2 UV(float x, float z)
            {
                return new Vector2(Mathf.Clamp01((x - O.x) / Size.x), Mathf.Clamp01((z - O.z) / Size.z));
            }
            internal float Height(float x, float z)
            {
                Vector2 uv = UV(x, z);
                float fx = uv.x * (N - 1), fz = uv.y * (N - 1);
                int ix = Mathf.Min((int)fx, N - 2), iz = Mathf.Min((int)fz, N - 2);
                return O.y + Size.y * Mathf.Lerp(Mathf.Lerp(H[iz, ix], H[iz, ix + 1], fx - ix),
                    Mathf.Lerp(H[iz + 1, ix], H[iz + 1, ix + 1], fx - ix), fz - iz);
            }
        }

        sealed class Patch
        {
            internal Source Src;
            internal Vector2 A, B, NA, NB;
            internal bool Corner;
            internal Vector3[] V;
            internal int[] I;
            internal int[][] Bins;
            internal int Cells;

            internal float Fraction(float x, float z)
            {
                if (Corner) return 0f;
                Vector2 delta = B - A;
                return ((x - A.x) * delta.x + (z - A.y) * delta.y) / (delta.x * delta.x + delta.y * delta.y);
            }

            internal bool Height(float x, float z, float depth, out float y)
            {
                y = 0f;
                float t = Fraction(x, z);
                if (t < -0.00001f || t > 1.00001f || depth > Rings[Rings.Length - 1]) return false;
                int row = Band(depth), col = Mathf.Min(Cells - 1, (int)(Mathf.Clamp01(t) * Cells));
                int[] bin = Bins[row * Cells + col];
                if (bin == null) return false;
                for (int k = 0; k < bin.Length; k++)
                {
                    int i = bin[k];
                    Vector3 a = V[I[i]], b = V[I[i + 1]], c = V[I[i + 2]];
                    float bx = b.x - a.x, bz = b.z - a.z, cx = c.x - a.x, cz = c.z - a.z;
                    float det = bx * cz - bz * cx;
                    if (Mathf.Abs(det) < 0.00001f) continue;
                    float px = x - a.x, pz = z - a.z;
                    float u = (px * cz - pz * cx) / det, v = (bx * pz - bz * px) / det;
                    if (u < -0.00001f || v < -0.00001f || u + v > 1.00001f) continue;
                    y = a.y + u * (b.y - a.y) + v * (c.y - a.y);
                    return true;
                }
                return false;
            }
        }

        static GameObject _skirt, _pending;
        static Rect _skirtRect;
        static float _nextTry, _mean;
        static bool _warned;
        static IEnumerator _build;
        static Source[] _sources;
        static Patch[] _patches;
        static int _revision = -1;
        static readonly Stopwatch _slice = new Stopwatch();
        static readonly Vector2[] Normals = {
            new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(-1f, 0f) };

        internal static void Tick()
        {
            FrameProf.S(FrameProf.S_AirBoundaryT);
            try { Step(); }
            catch (Exception ex)
            {
                if (_pending != null) UnityEngine.Object.Destroy(_pending);
                _pending = null; _build = null;
                if (!_warned) { _warned = true; RevivalPlugin.L.LogWarning("AirBoundary skirt: " + ex); }
                _nextTry = Time.unscaledTime + 30f;
            }
            finally { FrameProf.E(FrameProf.S_AirBoundaryT); }
        }

        static void Step()
        {
            if (_cfgSkirt != null && !_cfgSkirt.Value)
            {
                if (_skirt != null) UnityEngine.Object.Destroy(_skirt);
                if (_pending != null) UnityEngine.Object.Destroy(_pending);
                _skirt = _pending = null; _build = null; _sources = null; _patches = null;
                return;
            }
            if (_build != null)
            {
                if (_pending == null || Play != _skirtRect)
                {
                    if (_pending != null) UnityEngine.Object.Destroy(_pending);
                    _pending = null; _build = null; return;
                }
                _slice.Reset(); _slice.Start();
                if (!_build.MoveNext()) { _build = null; _nextTry = Time.unscaledTime + 2f; }
                return;
            }
            if (_skirt == null) { _sources = null; _patches = null; }
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 2f;
            Rect r = Play;
            if (_skirt != null && _skirtRect == r)
            {
                // P3 quality/bench changes only rebind shared textures, never rebuild geometry.
                if (FarForest.GroundReady && _revision != FarForest.GroundRevision)
                {
                    for (int i = 0; i < _sources.Length; i++) BindMaterials(_sources[i]);
                    _revision = FarForest.GroundRevision;
                }
                return;
            }
            if (!FarForest.GroundReady) return;
            Terrain[] all = Terrain.activeTerrains; // only while waiting for a world
            List<Source> sources = new List<Source>();
            for (int i = 0; i < all.Length; i++)
            {
                Terrain t = all[i];
                if (t == null || !t.drawHeightmap || !t.isActiveAndEnabled || t.terrainData == null) continue;
                TerrainData d = t.terrainData;
                Vector3 o = t.GetPosition(), sz = d.size;
                if (d.alphamapLayers == 0 || o.x >= r.xMax || o.z >= r.yMax
                    || o.x + sz.x <= r.xMin || o.z + sz.z <= r.yMin) continue;
                Source src = new Source();
                src.T = t; src.D = d; src.O = o; src.Size = sz; src.N = d.heightmapResolution;
                sources.Add(src);
            }
            if (sources.Count == 0) { _sources = null; return; }
            Source[] found = sources.ToArray();
            // Require all four corners and the two long-side tile joins before building.
            for (int side = 0; side < 4; side++)
                for (int k = 0; k <= 2; k++)
                {
                    Vector2 p = Point(r, side, k * 0.5f);
                    if (Find(found, p.x, p.y) == null) return;
                }
            if (_skirt != null) UnityEngine.Object.Destroy(_skirt);
            _skirt = null; _sources = null;
            _skirtRect = r;
            _pending = new GameObject("NDR_AirSkirt");
            _pending.AddComponent<AirSkirtAssets>(); // active once so OnDestroy also runs on an aborted build
            _pending.SetActive(false);
            _build = Build(r, found, _pending);
        }

        static Source Find(Source[] sources, float x, float z)
        {
            for (int i = 0; i < sources.Length; i++)
                if (sources[i].Contains(x, z)) return sources[i];
            return null;
        }

        static Vector2 Point(Rect r, int side, float t)
        {
            if (side == 0) return new Vector2(Mathf.Lerp(r.xMin, r.xMax, t), r.yMin);
            if (side == 1) return new Vector2(r.xMax, Mathf.Lerp(r.yMin, r.yMax, t));
            if (side == 2) return new Vector2(Mathf.Lerp(r.xMax, r.xMin, t), r.yMax);
            return new Vector2(r.xMin, Mathf.Lerp(r.yMax, r.yMin, t));
        }

        // A finite mirrored band: after 40 u of extrusion, the derivative
        // starts at one and tends smoothly to zero with a 900 u scale. Texture coordinates and heights use the same point.
        static float Inset(float d) { return 900f * (1f - Mathf.Exp(-Mathf.Max(0f, d - 40f) / 900f)); }

        static Vector2 Mirror(Rect r, float x, float z)
        {
            if (x < r.xMin) x = r.xMin + Inset(r.xMin - x);
            else if (x > r.xMax) x = r.xMax - Inset(x - r.xMax);
            if (z < r.yMin) z = r.yMin + Inset(r.yMin - z);
            else if (z > r.yMax) z = r.yMax - Inset(z - r.yMax);
            return new Vector2(x, z);
        }

        static float Skirt(Rect r, Source[] sources, float mean, float x, float z)
        {
            Vector2 p = Mirror(r, x, z);
            Source src = Find(sources, p.x, p.y);
            float y = src == null ? mean : src.Height(p.x, p.y);
            float d = Depth(r, x, z);
            y = Mathf.Lerp(y, mean - 30f, Smooth((d - 600f) / 6000f));
            y += Fbm(x, z) * HillU * Smooth((d - 100f) / 1200f);
            if (d > 9000f) y -= (d - 9000f) * 0.06f;
            return y;
        }

        internal static bool Height(float x, float z, out float y)
        {
            FrameProf.S(FrameProf.S_AirBoundaryH);
            try
            {
                y = 0f;
                if (_skirt == null || _sources == null || _skirtRect != Play) return false;
                float depth = Depth(_skirtRect, x, z);
                if (depth <= 0f) return false;
                bool corner = (x < _skirtRect.xMin || x > _skirtRect.xMax)
                    && (z < _skirtRect.yMin || z > _skirtRect.yMax);
                Vector2 nearest = new Vector2(Mathf.Clamp(x, _skirtRect.xMin, _skirtRect.xMax),
                    Mathf.Clamp(z, _skirtRect.yMin, _skirtRect.yMax));
                for (int i = 0; i < _patches.Length; i++)
                {
                    Patch p = _patches[i];
                    if (p.Corner != corner) continue;
                    if (corner) { if ((p.A - nearest).sqrMagnitude > 0.01f) continue; }
                    else if ((x - p.A.x) * p.NA.x + (z - p.A.y) * p.NA.y <= 0f) continue;
                    if (p.Height(x, z, depth, out y)) return true;
                }
                // Only past the mesh's 12000 u rim (4.3 km) (far beyond the soft boundary).
                y = Skirt(_skirtRect, _sources, _mean, x, z);
                return true;
            }
            finally { FrameProf.E(FrameProf.S_AirBoundaryH); }
        }

        static int Band(float depth)
        {
            int row = 0;
            while (row + 1 < Rings.Length - 1 && depth > Rings[row + 1]) row++;
            return row;
        }

        static float EdgeDistance(Rect r, Vector3 a, Vector3 b, Vector2 corner)
        {
            // For corner arcs the chord gets closer to the corner than its
            // endpoints. Include that minimum when assigning lookup bins.
            float dx = b.x - a.x, dz = b.z - a.z, den = dx * dx + dz * dz;
            float t = den <= 0f ? 0f : Mathf.Clamp01(((corner.x - a.x) * dx + (corner.y - a.z) * dz) / den);
            return Depth(r, a.x + t * dx, a.z + t * dz);
        }

        static IEnumerator IndexPatch(Rect r, Patch p)
        {
            p.Cells = p.Corner ? 1 : Mathf.Max(1, Mathf.CeilToInt((p.B - p.A).magnitude / 50f));
            List<int>[] bins = new List<int>[(Rings.Length - 1) * p.Cells];
            for (int i = 0; i < p.I.Length; i += 3)
            {
                Vector3 a = p.V[p.I[i]], b = p.V[p.I[i + 1]], c = p.V[p.I[i + 2]];
                float da = Depth(r, a.x, a.z), db = Depth(r, b.x, b.z), dc = Depth(r, c.x, c.z);
                float lo = Mathf.Min(da, Mathf.Min(db, dc)), hi = Mathf.Max(da, Mathf.Max(db, dc));
                if (hi <= 0f) continue;
                if (p.Corner) lo = Mathf.Min(lo, Mathf.Min(EdgeDistance(r, a, b, p.A),
                    Mathf.Min(EdgeDistance(r, b, c, p.A), EdgeDistance(r, c, a, p.A))));
                float ta = p.Fraction(a.x, a.z), tb = p.Fraction(b.x, b.z), tc = p.Fraction(c.x, c.z);
                int c0 = Mathf.Min(p.Cells - 1, (int)(Mathf.Clamp01(Mathf.Min(ta, Mathf.Min(tb, tc))) * p.Cells));
                int c1 = Mathf.Min(p.Cells - 1, (int)(Mathf.Clamp01(Mathf.Max(ta, Mathf.Max(tb, tc))) * p.Cells));
                // Include the previous band at an exact radial boundary.
                int r0 = Band(Mathf.Max(0f, lo - 0.01f)), r1 = Band(hi);
                for (int row = r0; row <= r1; row++)
                    for (int col = c0; col <= c1; col++)
                    {
                        int bin = row * p.Cells + col;
                        if (bins[bin] == null) bins[bin] = new List<int>();
                        bins[bin].Add(i);
                    }
                if ((i & 63) == 0 && OverBudget()) yield return null;
            }
            p.Bins = new int[bins.Length][];
            for (int i = 0; i < bins.Length; i++)
            {
                if (bins[i] != null) p.Bins[i] = bins[i].ToArray();
                if ((i & 31) == 0 && OverBudget()) yield return null;
            }
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
            float tx = Smooth(x - x0), tz = Smooth(z - z0);
            float a = Mathf.Lerp(Hash(x0, z0), Hash(x0 + 1, z0), tx);
            float b = Mathf.Lerp(Hash(x0, z0 + 1), Hash(x0 + 1, z0 + 1), tx);
            return Mathf.Lerp(a, b, tz);
        }

        static float Fbm(float x, float z)
        {
            return (0.7f * Value(x / 1700f, z / 1700f)
                    + 0.3f * Value(x / 600f + 17.3f, z / 600f - 9.1f)) * 2f - 1f;
        }

        static bool OverBudget() { return _slice.Elapsed.TotalMilliseconds >= SliceMs; }

        static IEnumerator Build(Rect r, Source[] sources, GameObject root)
        {
            Stopwatch total = Stopwatch.StartNew();
            AirSkirtAssets owned = root.GetComponent<AirSkirtAssets>();
            for (int i = 0; i < sources.Length; i++)
            {
                Source src = sources[i];
                src.H = src.D.GetHeights(0, 0, src.N, src.N); // once; never in Guide/Tick steady state
                src.Mats = Materials(src, owned);
                yield return null;
            }
            double sum = 0; int count = 0;
            for (int side = 0; side < 4; side++)
                for (int k = 0; k < 100; k++)
                {
                    Vector2 p = Point(r, side, k / 100f);
                    sum += Find(sources, p.x, p.y).Height(p.x, p.y); count++;
                }
            float mean = (float)(sum / count);
            List<Patch> patches = new List<Patch>();
            for (int side = 0; side < 4; side++)
            {
                Vector2 a = Point(r, side, 0f), b = Point(r, side, 1f);
                // Split only at source terrain joins; six sides with the east tile.
                List<float> cuts = new List<float>(); cuts.Add(0f); cuts.Add(1f);
                for (int i = 0; i < sources.Length; i++)
                {
                    Source src = sources[i];
                    float lo = side % 2 == 0 ? src.O.x : src.O.z;
                    float hi = lo + (side % 2 == 0 ? src.Size.x : src.Size.z);
                    float start = side % 2 == 0 ? a.x : a.y, end = side % 2 == 0 ? b.x : b.y;
                    float t0 = (lo - start) / (end - start), t1 = (hi - start) / (end - start);
                    if (t0 > 0f && t0 < 1f && !cuts.Contains(t0)) cuts.Add(t0);
                    if (t1 > 0f && t1 < 1f && !cuts.Contains(t1)) cuts.Add(t1);
                }
                cuts.Sort();
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    Patch patch = new Patch();
                    patch.A = Vector2.Lerp(a, b, cuts[k]); patch.B = Vector2.Lerp(a, b, cuts[k + 1]);
                    patch.NA = patch.NB = Normals[side];
                    Vector2 mid = (patch.A + patch.B) * 0.5f;
                    patch.Src = Find(sources, mid.x, mid.y);
                    if (patch.Src == null) throw new InvalidOperationException("Missing edge ground");
                    patches.Add(patch);
                }
                Patch corner = new Patch();
                corner.A = corner.B = a; corner.NA = Normals[(side + 3) % 4]; corner.NB = Normals[side];
                corner.Corner = true; corner.Src = Find(sources, a.x, a.y); patches.Add(corner);
            }
            int vertices = 0, triangles = 0, draws = 0, submitted = 0;
            float seamHeight = 0f, seamUV = 0f;
            for (int part = 0; part < patches.Count; part++)
            {
                Patch patch = patches[part];
                List<Vector3> v = new List<Vector3>(); List<Vector3> normals = new List<Vector3>();
                List<Vector2> uv = new List<Vector2>(); List<int> tris = new List<int>();
                List<float> prev = null; int prevStart = 0;
                for (int row = 0; row < Rings.Length; row++)
                {
                    List<float> at = Row(patch, row);
                    int rowStart = v.Count;
                    for (int c = 0; c < at.Count; c++)
                    {
                        float t = at[c];
                        Vector2 p = Vector2.Lerp(patch.A, patch.B, t);
                        Vector2 n = Vector2.Lerp(patch.NA, patch.NB, t).normalized;
                        p += n * (patch.Corner ? Mathf.Max(0f, Rings[row]) : Rings[row]);
                        float y = Skirt(r, sources, mean, p.x, p.y);
                        if (row == 0) y -= 5f; // covered overlap under real terrain
                        v.Add(new Vector3(p.x, y, p.y));
                        Vector2 mirrored = Mirror(r, p.x, p.y);
                        Vector2 texUV = patch.Src.UV(mirrored.x, mirrored.y);
                        uv.Add(texUV);
                        if (row == 1)
                        {
                            seamHeight = Mathf.Max(seamHeight, Mathf.Abs(y - patch.Src.Height(p.x, p.y)));
                            seamUV = Mathf.Max(seamUV, (texUV - patch.Src.UV(p.x, p.y)).magnitude);
                        }
                        // Exact terrain lighting normal at the seam; outer normals from cached heights.
                        Vector3 normal;
                        if (row <= 2)
                        {
                            Vector2 q = patch.Src.UV(mirrored.x, mirrored.y);
                            normal = patch.Src.D.GetInterpolatedNormal(q.x, q.y);
                        }
                        else
                        {
                            const float dx = 5f;
                            normal = new Vector3(Skirt(r, sources, mean, p.x - dx, p.y)
                                - Skirt(r, sources, mean, p.x + dx, p.y), 2f * dx,
                                Skirt(r, sources, mean, p.x, p.y - dx) - Skirt(r, sources, mean, p.x, p.y + dx)).normalized;
                        }
                        normals.Add(normal);
                        if ((c & 31) == 0 && OverBudget()) yield return null;
                    }
                    if (prev != null) Stitch(prev, at, prevStart, rowStart, tris);
                    prev = at; prevStart = rowStart;
                    if (OverBudget()) yield return null;
                }
                Mesh mesh = new Mesh(); mesh.name = "NDR_AirSkirt";
                owned.Assets.Add(mesh);
                patch.V = v.ToArray(); patch.I = tris.ToArray();
                IEnumerator index = IndexPatch(r, patch);
                while (index.MoveNext()) yield return null;
                mesh.vertices = patch.V; mesh.normals = normals.ToArray(); mesh.uv = uv.ToArray();
                int[] indices = patch.I;
                mesh.subMeshCount = patch.Src.Mats.Length;
                for (int pass = 0; pass < mesh.subMeshCount; pass++) mesh.SetTriangles(indices, pass);
                mesh.RecalculateBounds(); mesh.UploadMeshData(true);
                GameObject go = new GameObject("edge" + part); go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = patch.Src.Mats;
                mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                vertices += v.Count; triangles += tris.Count / 3; draws += mesh.subMeshCount;
                submitted += tris.Count / 3 * mesh.subMeshCount;
                yield return null; // native upload boundary
            }
            _sources = sources; _patches = patches.ToArray(); _mean = mean; _skirt = root; _pending = null;
            _revision = FarForest.GroundRevision;
            root.SetActive(true); ViewDistance.KeepVisible(root);
            RevivalPlugin.L.LogInfo("AirBoundary: terrain skirt built - " + patches.Count + " meshes, "
                + vertices + " vertices, " + triangles + " triangles (" + submitted + " with " + draws + " splat passes), "
                + "seam max height step " + seamHeight.ToString("0.0000") + " u, UV step "
                + seamUV.ToString("0.000000") + " (shared splat colours); "
                + total.Elapsed.TotalMilliseconds.ToString("0.0") + " ms elapsed (sliced).");
        }

        // The first two rows include every source cell boundary, not a 50 u
        // approximation. Farther out, 50..400 u rows reduce geometry sharply.
        static List<float> Row(Patch patch, int row)
        {
            List<float> points = new List<float>(); points.Add(0f);
            if (patch.Corner)
            {
                for (int i = 1; i < FanSteps; i++) points.Add((float)i / FanSteps);
            }
            else
            {
                bool x = Mathf.Abs(patch.B.x - patch.A.x) > 0.01f;
                float start = x ? patch.A.x : patch.A.y, end = x ? patch.B.x : patch.B.y;
                float origin = x ? patch.Src.O.x : patch.Src.O.z;
                float spacing = row <= 1 ? (x ? patch.Src.Size.x : patch.Src.Size.z) / (patch.Src.N - 1)
                    : Mathf.Min(400f, 50f * Mathf.Pow(2f, (row - 2) / 3));
                int lo = Mathf.FloorToInt((Mathf.Min(start, end) - origin) / spacing) + 1;
                int hi = Mathf.CeilToInt((Mathf.Max(start, end) - origin) / spacing);
                for (int i = lo; i < hi; i++) points.Add((origin + i * spacing - start) / (end - start));
                points.Sort();
            }
            points.Add(1f); return points;
        }

        static void Stitch(List<float> a, List<float> b, int sa, int sb, List<int> tris)
        {
            int i = 0, j = 0;
            while (i + 1 < a.Count || j + 1 < b.Count)
            {
                if (j + 1 == b.Count || (i + 1 < a.Count && a[i + 1] <= b[j + 1]))
                {
                    tris.Add(sa + i); tris.Add(sa + i + 1); tris.Add(sb + j); i++;
                }
                else
                {
                    tris.Add(sa + i); tris.Add(sb + j + 1); tris.Add(sb + j); j++;
                }
            }
        }

        static Material[] Materials(Source src, AirSkirtAssets owned)
        {
            if (owned.FlatNormal == null)
            {
                // Desktop terrain normal decoding accepts DXT5nm (AG) and RG.
                // Both decode this packed pixel to (0,0,1). Null is white,
                // which would tilt layers lacking a normal map at the seam.
                Texture2D flat = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                flat.name = "NDR_EdgeFlatNormal";
                flat.SetPixel(0, 0, new Color(1f, 0.5f, 1f, 0.5f));
                flat.Apply(false, true);
                owned.FlatNormal = flat; owned.Assets.Add(flat);
            }
            src.FlatNormal = owned.FlatNormal;
            int passes = (src.D.alphamapLayers + 3) / 4;
            Material[] mats = new Material[passes];
            bool diffuse = src.T.materialType == Terrain.MaterialType.BuiltInLegacyDiffuse;
            string kind = diffuse ? "Diffuse" : "Standard";
            for (int pass = 0; pass < passes; pass++)
            {
                Shader shader = Shader.Find(pass == 0 ? "Nature/Terrain/" + kind
                    : "Hidden/TerrainEngine/Splatmap/" + kind + "-AddPass");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("Terrain splat shader missing: " + kind);
                Material mat = new Material(shader); mat.name = "NDR_EdgeSplat";
                mat.enableInstancing = false; // vertices are already real mesh positions
                mat.renderQueue = pass == 0 ? 1900 : 1901;
                mats[pass] = mat; owned.Assets.Add(mat);
            }
            src.Mats = mats; BindMaterials(src); return mats;
        }

        static void BindMaterials(Source src)
        {
            SplatPrototype[] sp = src.D.splatPrototypes;
            Texture2D[] control = src.D.alphamapTextures;
            for (int pass = 0; pass < src.Mats.Length; pass++)
            {
                Material mat = src.Mats[pass];
                mat.SetTexture("_Control", pass < control.Length ? control[pass] : Texture2D.blackTexture);
                bool normal = false;
                for (int k = 0; k < 4; k++)
                {
                    int l = pass * 4 + k;
                    SplatPrototype p = l < sp.Length ? sp[l] : null;
                    string suffix = k.ToString();
                    mat.SetTexture("_Splat" + suffix, p == null || p.texture == null ? Texture2D.whiteTexture : p.texture);
                    Vector2 tile = p == null ? Vector2.one : p.tileSize;
                    mat.SetTextureScale("_Splat" + suffix, new Vector2(src.Size.x / Mathf.Max(0.01f, tile.x),
                        src.Size.z / Mathf.Max(0.01f, tile.y)));
                    mat.SetTextureOffset("_Splat" + suffix, p == null ? Vector2.zero :
                        new Vector2(p.tileOffset.x / Mathf.Max(0.01f, tile.x), p.tileOffset.y / Mathf.Max(0.01f, tile.y)));
                    mat.SetTexture("_Normal" + suffix, p == null || p.normalMap == null ? src.FlatNormal : p.normalMap);
                    mat.SetFloat("_NormalScale" + suffix, 1f);
                    mat.SetFloat("_Metallic" + suffix, p == null ? 0f : p.metallic);
                    mat.SetFloat("_Smoothness" + suffix, p == null ? 0f : p.smoothness);
                    if (p != null && p.normalMap != null) normal = true;
                }
                if (normal) { mat.EnableKeyword("_NORMALMAP"); mat.EnableKeyword("_TERRAIN_NORMAL_MAP"); }
                else { mat.DisableKeyword("_NORMALMAP"); mat.DisableKeyword("_TERRAIN_NORMAL_MAP"); }
            }
        }
    }

    // Own only generated assets. Shared terrain textures belong to their scene/P3.
    internal sealed class AirSkirtAssets : MonoBehaviour
    {
        internal readonly List<UnityEngine.Object> Assets = new List<UnityEngine.Object>();
        internal Texture2D FlatNormal;
        void OnDestroy()
        {
            for (int i = 0; i < Assets.Count; i++) if (Assets[i] != null) UnityEngine.Object.Destroy(Assets[i]);
            Assets.Clear();
        }
    }
}

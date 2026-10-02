// Next Day: Survival - Revival Toolkit
//
// EDGE FOREST (task A2). The turn-around strip around the map looks like the
// game's forest.
//
// Why: past the play rectangle lies AirBoundary's BUFFER (2000 u, 714 m),
// where a pilot is warned and turned back. Its ground is the Y lite skirt:
// one flat baked colour per texel, mirrored from the map's edge, and nothing
// standing on it - from the air the map ended in bare, smeared fields. The
// map itself shows forest out to the far clip without lag because of P3
// (Revival.FarForest.cs): past the terrain's tree distance every forest is
// drawn as impostor CARDS (crossed, double-sided quads of the real tree
// prototypes, one atlas, one Legacy Cutout material) over the CANOPY splat
// layer. The strip uses exactly that, so it IS that far forest:
//
//   - the same cards: FarForest's atlas, material, AddCard geometry and a
//     sample pool of the real forest trees (prototype, width and height
//     scale; drawn while BuildCards walks the tree instances), so species
//     mix and sizes match the map's forest;
//   - the same density: FarForest measures its own card density (cards per
//     unit of forest-mask area, EdgeMeasureArea) and the strip places cards
//     at that density on a jittered grid, full from 40 u to EdgeFullU past
//     the edge, thinning to EdgeOuterShare at the buffer's outer edge and to
//     nothing EdgeTaperU further; a soft 280 u noise varies it like stands
//     and thinner patches. Capped at EdgeMaxCards;
//   - the same ground: AirBoundary.Bake fades the skirt's colour to the P3
//     canopy layer's colour from the edge seam outwards (CanopyShare);
//   - standing on the skirt mesh itself (AirBoundary.SkirtHeight, the pilot
//     lookup), 0.5 m sunk.
//
// Cost: no per-frame work once built - EdgeStep is one float compare a
// frame and a few Unity null checks every 2 s. The build runs inside
// FarForest.Tick with a 0.08 ms managed slice (F6 slot FarForest.Tick) after
// the skirt exists; native mesh calls occupy separate frames. Render: one
// MeshRenderer per 1200 u world square (frustum culled, no LODGroup - nothing nearer to
// hand over to), the shared card material (one SetPass with the far forest),
// no shadows or probes. Count/cost check: research/edge_terrain_check.py.
// Parented under the far forest's root: Off, Low, bench, shots and the Perf
// bisect hide and show it together with the far forest; a far forest rebuild
// destroys it and EdgeStep builds it again.
// The pilot's clearance over the skirt counts from the strip's tallest card
// (EdgeCanopyU, AirBoundary.Guide).
//
// Seams: FarForest.Step (EdgeStep), FarForest.BuildCards (EdgeMeasureStart,
// EdgeSample), FarForest.Build (EdgeMeasureArea), FarForest.Status
// (EdgeStatus), AirBoundary.Bake (CanopyLayer, EdgeWanted), AirBoundary.Guide
// (EdgeCanopyU).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class FarForest
    {
        const float EdgeStartU = 40f;           // first trees past the edge (the skirt's mirror starts here)
        const float EdgeFullU = 800f;           // full density out to here
        const float EdgeOuterShare = 0.35f;     // density share at the buffer's outer edge
        const float EdgeTaperU = 400f;          // thinning to none past the buffer
        const float EdgeChunkU = 1200f;         // one mesh per world square
        const float EdgeSinkU = 0.5f * K;       // card foot into the skirt
        const float EdgeAreaStepU = 8f * K;     // forest area sample step
        const float EdgeNoiseU = 280f;          // stand / thin patch scale
        const float EdgeSliceMs = 0.08f;       // total managed FarForest tick while adding the strip
        const int EdgePoolMax = 2048;
        const int EdgeMaxCards = 160000;
        const int EdgeMeshCards = 65000 / 8;    // 16-bit indices, 8 vertices a card

        static readonly List<Card> _edgePool = new List<Card>();
        static System.Random _edgeRnd;
        static int _edgeSeen;
        static double _edgeArea;                // forest-mask area of the last build, u2
        static float _edgeDensity;              // far forest cards per u2
        static GameObject _edgeRoot, _edgePending, _edgeSkirt, _edgeParent;
        static IEnumerator _edgeBuild;
        static float _edgeNext, _edgeTopU;
        static float _edgeBuffer;
        static int _edgeCards, _edgeTris, _edgeMeshes;
        static string _edgeLast = "";
        // Own mesh lists: a far forest rebuild may run between two slices.
        static readonly List<Vector3> _ev = new List<Vector3>();
        static readonly List<Vector3> _en = new List<Vector3>();
        static readonly List<Vector2> _euv = new List<Vector2>();
        static readonly List<int> _et = new List<int>();

        /// <summary>The skirt bake paints the canopy ground for the strip.</summary>
        internal static bool EdgeWanted { get { return Wanted; } }

        /// <summary>Our canopy layer's index on this ground terrain, -1 none.</summary>
        internal static int CanopyLayer(TerrainData d)
        {
            Ground g;
            if (d == null || !_grounds.TryGetValue(d, out g) || g.Canopy == null) return -1;
            return g.Layer < d.alphamapLayers ? g.Layer : -1;
        }

        /// <summary>Height of the strip's tallest card over the skirt, u (0
        /// inside the map or with no strip built). Per flight frame.</summary>
        internal static float EdgeCanopyU(float depth)
        {
            return depth > 0f && _edgeTopU > 0f && _edgeRoot != null ? _edgeTopU : 0f;
        }

        // ============================================================ measure

        static void EdgeMeasureStart()
        {
            _edgePool.Clear();
            _edgeSeen = 0;
            _edgeRnd = new System.Random(20261002);
            _edgeArea = 0.0;
            _edgeDensity = 0f;
        }

        /// <summary>One forest tree with a card (BuildCards): reservoir sample
        /// of the real species and scales.</summary>
        static void EdgeSample(Slot s, float ws, float hs)
        {
            if (s == null) return;
            _edgeSeen++;
            Card c = new Card();
            c.S = s; c.Ws = ws; c.Hs = hs;
            if (_edgePool.Count < EdgePoolMax) { _edgePool.Add(c); return; }
            int j = _edgeRnd.Next(_edgeSeen);
            if (j < EdgePoolMax) _edgePool[j] = c;
        }

        /// <summary>Forest-mask area (union of every mask, sampled) and the far
        /// forest's card density over it.</summary>
        static IEnumerator EdgeMeasureArea(List<NpcDistance.Mask> masks, List<Chunk> made)
        {
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0; i < masks.Count; i++)
            {
                NpcDistance.Mask m = masks[i];
                x0 = Mathf.Min(x0, m.X0); z0 = Mathf.Min(z0, m.Z0);
                x1 = Mathf.Max(x1, m.X0 + m.W * m.Cell); z1 = Mathf.Max(z1, m.Z0 + m.H * m.Cell);
            }
            if (x1 <= x0 || z1 <= z0) yield break;
            const float step = EdgeAreaStepU;
            long forest = 0;
            int n = 0;
            for (float z = z0 + step * 0.5f; z < z1; z += step)
                for (float x = x0 + step * 0.5f; x < x1; x += step)
                {
                    if ((++n & 15) == 0 && EdgeOverBudget()) yield return null;
                    if (ForestAt(masks, x, z)) forest++;
                }
            int cards = 0;
            for (int i = 0; i < made.Count; i++) cards += made[i].Cards;
            _edgeArea = forest * (double)step * step;
            _edgeDensity = _edgeArea > 0.0 ? (float)(cards / _edgeArea) : 0f;
        }

        // ============================================================ tick

        static void EdgeStep()
        {
            if (_edgeBuild != null)
            {
                bool more;
                try { more = _edgeBuild.MoveNext(); }
                catch (Exception ex)
                {
                    more = false;
                    RevivalPlugin.L.LogWarning("FarForest: edge forest build failed - " + ex);
                    EdgeDrop();
                    _edgeNext = Time.unscaledTime + 30f;
                }
                if (!more) _edgeBuild = null;
                return;
            }
            float now = Time.unscaledTime;
            if (now < _edgeNext) return;
            _edgeNext = now + 2f;
            GameObject skirt = AirBoundary.SkirtRoot;
            if (skirt == null || _root == null)
            {
                // No ground under it any more (a level change, SkirtLite off).
                if (_edgeRoot != null || _edgeTopU > 0f) EdgeDrop();
                return;
            }
            if (_edgeRoot != null && _edgeSkirt == skirt && _edgeParent == _root && _edgeBuffer == AirBoundary.BufferU) return;
            if (_edgeRoot == null) _edgeTopU = 0f;  // destroyed with an old far forest root
            if (_cardMat == null || _edgePool.Count == 0 || _edgeDensity <= 0f) return;
            EdgeDrop();
            _edgeSkirt = skirt;
            _edgeParent = _root;
            _edgeBuffer = AirBoundary.BufferU;
            _edgeBuild = BuildEdge(AirBoundary.SkirtRect, _edgeBuffer);
        }

        static void EdgeDrop()
        {
            if (_edgeRoot != null) UnityEngine.Object.Destroy(_edgeRoot);
            if (_edgePending != null) UnityEngine.Object.Destroy(_edgePending);
            _edgeRoot = _edgePending = null;
            _edgeBuild = null;
            _edgeTopU = 0f;
            _edgeCards = _edgeTris = _edgeMeshes = 0;
        }

        // ============================================================ build

        static bool EdgeOverBudget() { return _sw.Elapsed.TotalMilliseconds >= EdgeSliceMs; }

        /// <summary>Density share at a depth past the edge (0..1).</summary>
        static float EdgeShare(float d, float buffer)
        {
            float full = Mathf.Min(EdgeFullU, buffer * 0.5f);
            if (d < EdgeStartU || d > buffer + EdgeTaperU) return 0f;
            if (d <= full) return 1f;
            if (d <= buffer) return Mathf.Lerp(1f, EdgeOuterShare, (d - full) / (buffer - full));
            return EdgeOuterShare * (1f - (d - buffer) / EdgeTaperU);
        }

        static float EdgeDepth(Rect r, float x, float z)
        {
            float dx = Mathf.Max(0f, Mathf.Max(r.xMin - x, x - r.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(r.yMin - z, z - r.yMax));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static uint EdgeHash(uint h)
        {
            h ^= h >> 16; h *= 0x7feb352du;
            h ^= h >> 15; h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        static uint EdgeHash(int x, int z) { return EdgeHash((uint)x * 0x9E3779B1u ^ EdgeHash((uint)z + 0x632BE5ABu)); }

        /// <summary>Smooth value noise 0..1 on an EdgeNoiseU lattice.</summary>
        static float EdgeNoise(float x, float z)
        {
            float fx = x / EdgeNoiseU, fz = z / EdgeNoiseU;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float tx = fx - ix, tz = fz - iz;
            tx = tx * tx * (3f - 2f * tx); tz = tz * tz * (3f - 2f * tz);
            float a = (EdgeHash(ix, iz) & 0xFFFF) / 65535f, b = (EdgeHash(ix + 1, iz) & 0xFFFF) / 65535f;
            float c = (EdgeHash(ix, iz + 1) & 0xFFFF) / 65535f, e = (EdgeHash(ix + 1, iz + 1) & 0xFFFF) / 65535f;
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, e, tx), tz);
        }

        /// <summary>Expected strip cards at density rho (the band integral of
        /// EdgeShare over a rectangle with rounded corners).</summary>
        static double EdgeExpected(Rect r, float buffer, float rho)
        {
            double sum = 0.0, perim = 2.0 * (r.width + r.height);
            const float dd = 10f;
            for (float d = EdgeStartU + dd * 0.5f; d < buffer + EdgeTaperU; d += dd)
                sum += EdgeShare(d, buffer) * (perim + 2.0 * Math.PI * d) * dd;
            return sum * rho;
        }

        static IEnumerator BuildEdge(Rect r, float buffer)
        {
            float started = Time.realtimeSinceStartup;
            float outer = buffer + EdgeTaperU;
            double expected = EdgeExpected(r, buffer, _edgeDensity);
            // Reserve 2% for deterministic jitter/noise variation around the
            // expected count. The hard limit below also covers unusual buffers.
            float scale = expected > EdgeMaxCards * 0.98 ? (float)(EdgeMaxCards * 0.98 / expected) : 1f;
            float rho = _edgeDensity * scale;
            // A jittered grid one card per cell at 1.3 x the density; the keep
            // roll thins it (the noise factor averages 1 and peaks at 1.29).
            float g = 1f / Mathf.Sqrt(rho * 1.3f);
            float cellShare = rho * g * g;

            GameObject root = new GameObject("NDR_EdgeForest");
            EdgeForestAssets owned = root.AddComponent<EdgeForestAssets>();  // awake once: OnDestroy frees the meshes
            root.SetActive(false);
            _edgePending = root;

            Dictionary<long, List<Card>> buckets = new Dictionary<long, List<Card>>();
            int ix0 = Mathf.FloorToInt((r.xMin - outer) / g), ix1 = Mathf.CeilToInt((r.xMax + outer) / g);
            int iz0 = Mathf.FloorToInt((r.yMin - outer) / g), iz1 = Mathf.CeilToInt((r.yMax + outer) / g);
            int count = _edgePool.Count, placed = 0, n = 0, noGround = 0;
            float top = 0f;
            for (int iz = iz0; iz <= iz1; iz++)
            {
                float cz = (iz + 0.5f) * g;
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    if ((++n & 15) == 0 && EdgeOverBudget())
                    {
                        yield return null;
                        if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    }
                    float cx = (ix + 0.5f) * g;
                    // The play rectangle's inside in one jump.
                    if (cz > r.yMin + g && cz < r.yMax - g && cx > r.xMin + g && cx < r.xMax - g)
                    {
                        ix = Mathf.FloorToInt((r.xMax - g) / g);
                        continue;
                    }
                    uint h = EdgeHash(ix, iz);
                    float x = cx + (((h & 1023) / 1023f) - 0.5f) * g;
                    float z = cz + ((((h >> 10) & 1023) / 1023f) - 0.5f) * g;
                    float share = EdgeShare(EdgeDepth(r, x, z), buffer);
                    if (share <= 0f) continue;
                    float keep = share * cellShare * (0.55f + 0.45f * EdgeNoise(x, z)) / 0.775f;
                    if (((h >> 20) & 4095) / 4095f >= keep) continue;
                    if (placed >= EdgeMaxCards) continue;
                    float y;
                    if (!AirBoundary.SkirtHeight(x, z, out y)) { noGround++; continue; }
                    uint h2 = EdgeHash(h + 0x68E31DA4u);
                    Card c = _edgePool[(int)(h2 % (uint)count)];
                    c.P = new Vector3(x, y - EdgeSinkU, z);
                    c.Yaw = ((h2 >> 12) & 1023) / 1023f * Mathf.PI;
                    top = Mathf.Max(top, c.S.Y1 * c.Hs - EdgeSinkU);
                    long key = ((long)Mathf.FloorToInt(x / EdgeChunkU) << 32) ^ (uint)Mathf.FloorToInt(z / EdgeChunkU);
                    List<Card> list;
                    if (!buckets.TryGetValue(key, out list)) { list = new List<Card>(); buckets[key] = list; }
                    list.Add(c);
                    placed++;
                }
            }

            int tris = 0, meshes = 0;
            foreach (KeyValuePair<long, List<Card>> kv in buckets)
            {
                List<Card> cards = kv.Value;
                for (int start = 0; start < cards.Count; start += EdgeMeshCards)
                {
                    int end = Math.Min(cards.Count, start + EdgeMeshCards);
                    float minY = float.MaxValue;
                    for (int i = start; i < end; i++) minY = Mathf.Min(minY, cards[i].P.y);
                    Vector3 p0 = cards[start].P;
                    Vector3 origin = new Vector3(Mathf.Floor(p0.x / EdgeChunkU) * EdgeChunkU, minY,
                        Mathf.Floor(p0.z / EdgeChunkU) * EdgeChunkU);
                    _ev.Clear(); _en.Clear(); _euv.Clear(); _et.Clear();
                    for (int i = start; i < end; i++)
                    {
                        if ((i & 15) == 15 && EdgeOverBudget())
                        {
                            yield return null;
                            if (EdgeStale(root)) { EdgeDrop(); yield break; }
                        }
                        AddCard(cards[i], origin, _ev, _en, _euv, _et);
                    }
                    Mesh mesh = new Mesh();
                    mesh.name = "NDR_EdgeForestCards";
                    owned.Meshes.Add(mesh);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    mesh.SetVertices(_ev);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    mesh.SetNormals(_en);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    mesh.SetUVs(0, _euv);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    mesh.SetTriangles(_et, 0);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(true);
                    yield return null;
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                    GameObject go = new GameObject("e" + Mathf.RoundToInt(origin.x) + "_" + Mathf.RoundToInt(origin.z));
                    go.transform.SetParent(root.transform, false);
                    go.transform.position = origin;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = _cardMat;
                    Quiet(mr);
                    tris += _et.Count / 3;
                    meshes++;
                    yield return null;      // one native upload a frame
                    if (EdgeStale(root)) { EdgeDrop(); yield break; }
                }
            }
            _ev.Clear(); _en.Clear(); _euv.Clear(); _et.Clear();

            root.transform.SetParent(_edgeParent.transform, true);
            root.SetActive(true);
            ViewDistance.KeepVisible(root);         // big squares, never prop-culled
            _edgeRoot = root;
            _edgePending = null;
            _edgeTopU = top;
            _edgeCards = placed; _edgeTris = tris; _edgeMeshes = meshes;
            _edgeLast = placed + " cards (" + tris + " tris) in " + meshes + " meshes over the "
                + Mathf.RoundToInt(buffer) + " u strip, " + (_edgeDensity * 1000f).ToString("0.00", CultureInfo.InvariantCulture)
                + " cards/1000 u2 measured on " + (_edgeArea / 1e6).ToString("0.0", CultureInfo.InvariantCulture)
                + " M u2 of forest" + (scale < 1f ? " (capped x" + scale.ToString("0.00", CultureInfo.InvariantCulture) + ")" : "")
                + ", pool " + count + " of " + _edgeSeen + " trees, tops to " + (top / K).ToString("0", CultureInfo.InvariantCulture) + " m";
            RevivalPlugin.L.LogInfo("FarForest: edge forest " + _edgeLast + (noGround > 0 ? ", " + noGround + " without skirt ground" : "")
                + "; built in " + (Time.realtimeSinceStartup - started).ToString("0.0", CultureInfo.InvariantCulture) + " s.");
        }

        /// <summary>The far forest was rebuilt (or this build dropped) between
        /// two slices.</summary>
        static bool EdgeStale(GameObject root)
        {
            return root == null || _edgePending != root || _edgeParent == null || _edgeParent != _root
                || _edgeSkirt == null || _edgeSkirt != AirBoundary.SkirtRoot || _edgeBuffer != AirBoundary.BufferU;
        }

        static string EdgeStatus()
        {
            if (_edgeBuild != null) return "edge forest: building...";
            if (_edgeRoot == null) return "edge forest: waiting for the skirt";
            return "edge forest " + _edgeLast;
        }
    }

    /// <summary>Owns the edge forest's generated meshes (the card material
    /// and atlas belong to FarForest).</summary>
    internal sealed class EdgeForestAssets : MonoBehaviour
    {
        internal readonly List<Mesh> Meshes = new List<Mesh>();
        void OnDestroy()
        {
            for (int i = 0; i < Meshes.Count; i++) if (Meshes[i] != null) UnityEngine.Object.Destroy(Meshes[i]);
            Meshes.Clear();
        }
    }
}

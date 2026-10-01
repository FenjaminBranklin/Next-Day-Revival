// Z F2: load-only ground replacement for existing east bundles. No Update,
// physics query, new renderer/collider, height edit or steady-state allocation.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class AirfieldGround
    {
        internal static void Loaded(Scene scene)
        {
            if (!EastWorld.On) return;
            bool ground = scene.name == EastWorld.SceneName;
            if (!ground && scene.name != "EastAirfield"
                && !scene.name.StartsWith("EastAf", StringComparison.Ordinal)) return;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (!ground && root.name != "EastAirfieldRoot"
                    && !root.name.StartsWith("EastAf", StringComparison.Ordinal)) continue;
                if (root.GetComponent<AirfieldGroundJob>() != null) continue;
                AirfieldGroundJob job = root.AddComponent<AirfieldGroundJob>();
                job.Begin(ground);
            }
        }
    }

    // Only alive while that scene's finite construction job is running.
    internal sealed class AirfieldGroundJob : MonoBehaviour
    {
        internal void Begin(bool ground) { StartCoroutine(Apply(ground)); }

        IEnumerator Apply(bool ground)
        {
            if (ground)
            {
                FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                Terrain[] terrains = GetComponentsInChildren<Terrain>(true);
                FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                yield return null;
                foreach (Terrain t in terrains)
                {
                    if (t.terrainData == null) continue;
                    IEnumerator clear = ClearTrees(t);
                    while (clear.MoveNext()) yield return clear.Current;
                    if (t.terrainData.name != "EastTileGround") continue;
                    IEnumerator paint = Paint(t);
                    while (paint.MoveNext()) yield return paint.Current;
                }
            }
            else
            {
                Stack<Transform> pending = new Stack<Transform>();
                pending.Push(transform);
                int disabled = 0;
                while (pending.Count > 0)
                {
                    FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                    long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                    do
                    {
                        Transform node = pending.Pop();
                        if ((transform.name == "EastAirfieldRoot" && AirfieldGroundCore.RemoveNode(node.name))
                            || AirfieldGroundCore.RemoveObstruction(node.name))
                        {
                            // Whole subtree: renderers, colliders, LODs and carvers.
                            // Also covers currently inactive assembly fallback nodes.
                            node.gameObject.SetActive(false);
                            disabled++;
                            continue;
                        }
                        if (node.name.StartsWith("R1|", StringComparison.Ordinal))
                        {
                            Vector3 scale = node.localScale;
                            scale.x = AirfieldGroundCore.RunwayWidth;
                            node.localScale = scale;
                            node.name = "R1|Worn field strip (20 m)";
                        }
                        else if (node.name.StartsWith("R2|", StringComparison.Ordinal))
                        {
                            Vector3 scale = node.localScale;
                            scale.x = AirfieldGroundCore.FlightWidth;
                            node.localScale = scale;
                            node.name = "R2|Flight corridor (25 m grass shoulders)";
                        }
                        for (int i = 0; i < node.childCount; i++) pending.Push(node.GetChild(i));
                    }
                    while (pending.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - begin
                        < System.Diagnostics.Stopwatch.Frequency / 4000); // 0.25 ms slice
                    FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                    yield return null;
                }
                RevivalPlugin.L.LogInfo("AirfieldGround: disabled " + disabled
                    + " obsolete ground/clutter roots including hidden collision; interior floors retained.");
            }
            Destroy(this);
        }

        IEnumerator ClearTrees(Terrain terrain)
        {
            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position, size = data.size;
            TreeInstance[] trees = data.treeInstances;
            List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
            FrameProf.E(FrameProf.S_AirfieldGroundLoad);
            yield return null;
            int at = 0;
            while (at < trees.Length)
            {
                FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    TreeInstance tree = trees[at++];
                    float x = origin.x + tree.position.x * size.x, z = origin.z + tree.position.z * size.z;
                    if (!AirfieldGroundCore.ClearTree(x, z)) keep.Add(tree);
                }
                while (at < trees.Length && System.Diagnostics.Stopwatch.GetTimestamp() - begin
                    < System.Diagnostics.Stopwatch.Frequency / 4000);
                FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                yield return null;
            }
            if (keep.Count != trees.Length)
            {
                FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                data.treeInstances = keep.ToArray();
                FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                yield return null;
            }
            // Same position-only rule on collision and every drawing terrain:
            // no invisible trunks, no added/moved instances, no height changes.
        }

        IEnumerator Paint(Terrain terrain)
        {
            TerrainData source = terrain.terrainData;
            SplatPrototype[] layers = source.splatPrototypes;
            int grass = Layer(layers, "Ter2"), dry = Layer(layers, "Ter7");
            int stone = Layer(layers, "Ter6"), dirt = Layer(layers, "dirt_tint_3");
            if (grass < 0 || dry < 0 || stone < 0 || dirt < 0)
            {
                RevivalPlugin.L.LogWarning("AirfieldGround: required terrain splats missing; ground paint skipped.");
                yield break;
            }

            // Idempotent assignment to the loaded in-memory terrain, as with
            // EastCrossings. Do not clone its heightmap or 45k
            // tree list for a paint-only change; no bundle asset is written.
            TerrainData data = source;

            Vector3 origin = terrain.transform.position, size = data.size;
            int aw = data.alphamapWidth, ah = data.alphamapHeight;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((AirfieldGroundCore.XMin - origin.x) / size.x * aw));
            int x1 = Mathf.Min(aw, Mathf.CeilToInt((AirfieldGroundCore.XMax - origin.x) / size.x * aw));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((AirfieldGroundCore.ZMin - origin.z) / size.z * ah));
            int z1 = Mathf.Min(ah, Mathf.CeilToInt((1760f - origin.z) / size.z * ah));
            // One alpha row per frame; never GetHeights/SetHeights. Alphamaps
            // are [z,x,layer], unlike serialized x-major heightmap storage.
            for (int z = z0; z < z1; z++)
            {
                FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                float[,,] row = data.GetAlphamaps(x0, z, x1 - x0, 1);
                float wz = origin.z + (z + 0.5f) * size.z / ah;
                for (int x = x0; x < x1; x++)
                {
                    float wx = origin.x + (x + 0.5f) * size.x / aw;
                    if (!AirfieldGroundCore.PaintAt(wx, wz)) continue;
                    AirfieldGroundCore.Paint p = AirfieldGroundCore.Sample(wx, wz);
                    int ix = x - x0;
                    for (int k = 0; k < layers.Length; k++) row[0, ix, k] = 0f;
                    row[0, ix, grass] = p.Grass;
                    row[0, ix, dry] = p.DryGrass;
                    row[0, ix, stone] = p.Stone;
                    row[0, ix, dirt] = p.Dirt;
                }
                data.SetAlphamaps(x0, z, row);
                FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                yield return null;
            }
            RevivalPlugin.L.LogInfo("AirfieldGround: worn 20 m strip, 4 m dirt circuit/cross track; "
                + "heights/external roads unchanged, route foliage only pruned; no added meshes.");
        }

        static int Layer(SplatPrototype[] layers, string name)
        {
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].texture != null && layers[i].texture.name == name) return i;
            return -1;
        }
    }

}

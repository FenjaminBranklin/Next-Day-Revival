// Airfield ground: load-only grass edge and gun-pit clearings for the east
// tile. C W4 also queues the finite kit path job and clears its corridors.
// No Update, height edit or steady-state allocation.
// A1: the original concrete, paths, runway paint and markers are not touched
// (Z F2 switched them off and repainted the field; that is reverted).
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
            if (!EastWorld.On || scene.name != EastWorld.SceneName) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].GetComponent<AirfieldGroundJob>() != null) continue;
                roots[i].AddComponent<AirfieldGroundJob>();
                if (i == 0 && roots[i].GetComponent<AirfieldPathsJob>() == null)
                    roots[i].AddComponent<AirfieldPathsJob>();
            }
        }
    }

    // Only alive while that scene's finite pruning job is running.
    internal sealed class AirfieldGroundJob : MonoBehaviour
    {
        IEnumerator Start()
        {
            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
            Terrain[] terrains = GetComponentsInChildren<Terrain>(true);
            FrameProf.E(FrameProf.S_AirfieldGroundLoad);
            yield return null;
            int pruned = 0;
            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                TerrainData data = t.terrainData;
                Vector3 origin = t.transform.position, size = data.size;
                TreeInstance[] trees = data.treeInstances;
                List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
                int at = 0;
                while (at < trees.Length)
                {
                    FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                    long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                    do
                    {
                        TreeInstance tree = trees[at++];
                        float x = origin.x + tree.position.x * size.x, z = origin.z + tree.position.z * size.z;
                        if (!AirfieldGroundCore.ClearTree(x, z) && !AirfieldPathsCore.ClearTree(x, z)) keep.Add(tree);
                    }
                    while (at < trees.Length && System.Diagnostics.Stopwatch.GetTimestamp() - begin
                        < System.Diagnostics.Stopwatch.Frequency / 4000); // 0.25 ms slice
                    FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                    yield return null;
                }
                if (keep.Count != trees.Length)
                {
                    // Same position-only rule on collision and every drawing
                    // terrain: no invisible trunks, no added or moved instances.
                    FrameProf.S(FrameProf.S_AirfieldGroundLoad);
                    data.treeInstances = keep.ToArray();
                    FrameProf.E(FrameProf.S_AirfieldGroundLoad);
                    pruned += trees.Length - keep.Count;
                    yield return null;
                }
            }
            RevivalPlugin.L.LogInfo("AirfieldGround: grass edge, flak pits and tower path corridors pruned " + pruned
                + " tree/bush instances; original concrete, paths and runway paint untouched.");
            Destroy(this);
        }
    }
}

// Generator asset placement only: finite grass suppression on scene load.
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class GeneratorPlacement
    {
        internal static void Loaded(Scene scene)
        {
            if (!EastWorld.On || scene.name != "EastAfGenerator") return;
            Scene tile = SceneManager.GetSceneByName(EastWorld.SceneName);
            if (!tile.isLoaded) return;
            FrameProf.S(FrameProf.S_AirfieldGroundLoad);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                GameObject[] terrainRoots = tile.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                    for (int s = 0; s < roots[r].transform.childCount; s++)
                    {
                        Transform site = roots[r].transform.GetChild(s);
                        Transform floor = site.Find("Equipment/Floor");
                        if (floor == null) continue;
                        BoxCollider box = floor.GetComponent<BoxCollider>();
                        if (box == null) continue;
                        Bounds bounds = box.bounds;
                        // pb-5: the concrete door apron in front of the shell
                        // (its box per damage stage) is part of the patch.
                        BoxCollider[] boxes = site.GetComponentsInChildren<BoxCollider>(true);
                        for (int k = 0; k < boxes.Length; k++)
                            if (boxes[k].name.EndsWith("_apron", System.StringComparison.Ordinal))
                            {
                                Transform t = boxes[k].transform;
                                Vector3 half = boxes[k].size * 0.5f;
                                bounds.Encapsulate(t.TransformPoint(boxes[k].center - half));
                                bounds.Encapsulate(t.TransformPoint(boxes[k].center + half));
                            }
                        for (int t = 0; t < terrainRoots.Length; t++)
                        {
                            Terrain[] terrains = terrainRoots[t].GetComponentsInChildren<Terrain>(true);
                            for (int i = 0; i < terrains.Length; i++) Clear(terrains[i], bounds);
                        }
                    }
            }
            finally { FrameProf.E(FrameProf.S_AirfieldGroundLoad); }
        }

        static void Clear(Terrain terrain, Bounds bounds)
        {
            TerrainData data = terrain.terrainData;
            if (data == null || data.detailWidth == 0 || data.detailHeight == 0) return;
            Vector3 origin = terrain.transform.position;
            int x, w, z, h;
            if (!GeneratorPlacementCore.Cells(bounds.min.x, bounds.max.x, origin.x,
                data.size.x, data.detailWidth, out x, out w)
                || !GeneratorPlacementCore.Cells(bounds.min.z, bounds.max.z, origin.z,
                data.size.z, data.detailHeight, out z, out h)) return;
            // Only a small footprint patch per layer; never fetch/rewrite the
            // full detail grid. Terrain APIs allocate here once, not per frame.
            int[] layers = data.GetSupportedLayers(x, z, w, h);
            if (layers.Length == 0) return;
            int[,] empty = new int[h, w];
            for (int i = 0; i < layers.Length; i++) data.SetDetailLayer(x, z, layers[i], empty);
            RevivalPlugin.L.LogInfo("GeneratorPlacement: cleared grass " + w + "x" + h
                + " cells in " + layers.Length + " layers on " + terrain.name);
        }
    }
}

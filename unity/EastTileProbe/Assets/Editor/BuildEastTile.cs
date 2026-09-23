// East extension feasibility probe - the scene AssetBundle.
//
// Builds, from nothing, one scene "EastTileProbe" holding a flat 1000 x 1000 m
// Terrain (one splat layer, one terrain material), one 10 m cube in its middle
// and a baked NavMesh, and packs it as a scene AssetBundle for
// StandaloneWindows. Unity 2018.1.0f2 only - the player's own version.
//
//   "C:\Program Files\Unity\Editor\Unity.exe" -batchmode -quit -nographics
//       -projectPath unity\EastTileProbe -executeMethod BuildEastTile.Build
//       -logFile unity\EastTileProbe\build.log
//
// Output: unity/EastTileProbe/Build/east_tile_probe.bundle. Copy it to
// assets/east_tile_probe.bundle; build.ps1 installs it beside the DLL.
// Report: docs/ai/tasks/east-extension-feasibility.md.
//
// The scene holds NO game scripts (none exist in this project) and no
// lightmaps. Its root "EastTileRoot" is authored at its FINAL world position:
// a NavMesh baked into a scene is added where it was baked and does not follow
// a root moved at runtime, so a tile is built in world coordinates.
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildEastTile
{
    const string Dir = "Assets/Probe";
    const string ScenePath = Dir + "/EastTileProbe.unity";
    const float Size = 1000f;
    const float Height = 100f;
    // West edge on the vanilla east edge x = +2500, centred on the crossing
    // z = 950, where GWTerrain2's edge is 506.11 m and varies 1 m over
    // +-50 m (research/east_edge.py). The tile is flat at that height, so the
    // seam is level at z = 950 and a step everywhere else - a probe, not a map.
    static readonly Vector3 RootPosition = new Vector3(2500f, 506.11f, 450f);

    // The game's own tag list, in order (globalgamemanagers TagManager). A
    // custom tag is serialised as 20000 + its index, so the project must list
    // the tags in the SAME order for "Terrain" to arrive as the game's 20020.
    static readonly string[] GameTags = {
        "Concrete", "Wood", "Metal", "Glass", "Dirt", "Ground", "ItemSpawn",
        "Vehicle", "RagdollBone", "Water", "PlayerSpawnPoint", "ToxicityFog",
        "NPCPlayer", "Bush_Tree", "Animal", "Fire", "GasolineTank",
        "ToxicityFogLastSurvivor", "ReverbTrigger", "LocationChangeTrigger",
        "Terrain", "ThroughTerrainTrigger", "GameplayZoneTrigger", "Ladder",
        "QuestTrigger", "DeadPlayer", "SmokeTrigger", "ToxicFlower", "Blood" };
    // The vanilla collision terrain (TerrainData 768) is layer 25 "Terrain",
    // tag "Terrain" (level7, GameWorldData/Terrain/2_terrains/Terrain_billboard_0).
    const int TerrainLayer = 25;

    static void MirrorGameTags()
    {
        SerializedObject tm = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        SerializedProperty tags = tm.FindProperty("tags");
        tags.ClearArray();
        for (int i = 0; i < GameTags.Length; i++)
        {
            tags.InsertArrayElementAtIndex(i);
            tags.GetArrayElementAtIndex(i).stringValue = GameTags[i];
        }
        tm.FindProperty("layers").GetArrayElementAtIndex(TerrainLayer).stringValue = "Terrain";
        tm.ApplyModifiedProperties();
    }

    public static void Build()
    {
        Directory.CreateDirectory(Dir);
        AssetDatabase.Refresh();
        MirrorGameTags();

        // A two-tone 8 px checker, so the tile is unmistakable next to the
        // vanilla ground: nothing in Severoufimsk is this colour.
        Texture2D tex = new Texture2D(64, 64, TextureFormat.RGB24, true);
        Color a = new Color(0.55f, 0.50f, 0.38f), b = new Color(0.42f, 0.38f, 0.28f);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                tex.SetPixel(x, y, ((x / 8 + y / 8) & 1) == 0 ? a : b);
        tex.Apply(true);
        AssetDatabase.CreateAsset(tex, Dir + "/EastTileGround.asset");

        TerrainData td = new TerrainData();
        td.heightmapResolution = 129;
        td.size = new Vector3(Size, Height, Size);
        td.alphamapResolution = 64;
        td.baseMapResolution = 64;
        SplatPrototype sp = new SplatPrototype();
        sp.texture = tex;
        sp.tileSize = new Vector2(16f, 16f);
        td.splatPrototypes = new SplatPrototype[] { sp };
        AssetDatabase.CreateAsset(td, Dir + "/EastTileData.asset");

        Material terrainMat = new Material(Shader.Find("Nature/Terrain/Standard"));
        terrainMat.name = "EastTileTerrain";
        AssetDatabase.CreateAsset(terrainMat, Dir + "/EastTileTerrain.mat");
        Material cubeMat = new Material(Shader.Find("Standard"));
        cubeMat.name = "EastTileCube";
        cubeMat.color = new Color(0.9f, 0.15f, 0.1f);
        AssetDatabase.CreateAsset(cubeMat, Dir + "/EastTileCube.mat");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject root = new GameObject("EastTileRoot");
        root.transform.position = RootPosition;

        GameObject tgo = Terrain.CreateTerrainGameObject(td);
        tgo.name = "EastTileTerrain";
        tgo.transform.SetParent(root.transform, false);
        tgo.transform.localPosition = Vector3.zero;
        Terrain terrain = tgo.GetComponent<Terrain>();
        terrain.materialType = Terrain.MaterialType.Custom;
        terrain.materialTemplate = terrainMat;
        tgo.layer = TerrainLayer;
        tgo.tag = "Terrain";
        GameObjectUtility.SetStaticEditorFlags(tgo, StaticEditorFlags.NavigationStatic);

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "EastTileCube";
        cube.transform.SetParent(root.transform, false);
        cube.transform.localPosition = new Vector3(Size / 2f, 5f, Size / 2f);
        cube.transform.localScale = new Vector3(10f, 10f, 10f);
        cube.GetComponent<Renderer>().sharedMaterial = cubeMat;
        GameObjectUtility.SetStaticEditorFlags(cube, StaticEditorFlags.NavigationStatic);

        // The NavMesh bake is written next to a SAVED scene, so save first.
        EditorSceneManager.SaveScene(scene, ScenePath);
        UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();

        Directory.CreateDirectory("Build");
        AssetBundleBuild bundle = new AssetBundleBuild();
        bundle.assetBundleName = "east_tile_probe.bundle";
        bundle.assetNames = new string[] { ScenePath };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles("Build",
            new AssetBundleBuild[] { bundle },
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows);
        Debug.Log("EASTTILE BUILD " + (manifest != null ? "OK" : "FAILED"));
        EditorApplication.Exit(manifest != null ? 0 : 1);
    }
}

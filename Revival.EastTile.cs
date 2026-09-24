// Next Day: Survival - Revival Toolkit
//
// EAST EXTENSION FEASIBILITY PROBE. Research only, off by default:
// [Research] EastTile = false.
//
// The question (docs/ai/tasks/east-extension-feasibility.md): can a second
// terrain tile hang off the EAST side of GW_Scene_1 in the same world, walkable
// without a loading screen? The cheapest decisive test is a scene AssetBundle
// built in Unity 2018.1.0f2 (the player's own version) holding one flat
// 1000 x 1000 m Terrain and one 10 m cube, loaded ADDITIVELY while GW_Scene_1
// is up. The bundle is built by unity/EastTileProbe/Assets/Editor/
// BuildEastTile.cs and ships as assets/east_tile_probe.bundle through
// build.ps1, exactly the way a real tile would ship.
//
// WHERE THE TILE IS. The bundle's root "EastTileRoot" is authored at its final
// world position (2500, 506.11, 450), so it covers x 2500..3500, z 450..1450
// and meets the vanilla east edge level at the crossing z = 950. It is not
// moved here: a NavMesh baked into a scene is added where it was baked and
// does not follow a root moved at runtime. This class only VERIFIES the
// position and measures.
//
// WHAT IT LOGS, every line prefixed "EastTile:":
//   - the bundle: file size and SHA1 (compare two clients), LoadFromFile,
//     its scene paths
//   - the additive load: time, scene handle, root objects
//   - Terrain.activeTerrain / activeTerrains before and after (the game's
//     TerrainSurface and MapUIManager read a single terrain)
//   - the tile terrain's shader, layer, tag and collider; the cube's shader
//   - downward rays onto both sides of the seam at z = 950 and onto the tile
//   - NavMesh.SamplePosition on the tile and a path from the vanilla side
//   - MapUIManager.WORLD_SIZE, and TerrainSurface.GetTextureMix at a tile point
//   - the local player whenever x > 2450: the collider under him, crossing
//     the seam, and a fall through the tile
//   - remote players standing east of x = 2500 and what THIS client has under
//     them (the two-client check)
//   - every scene load/unload while the probe runs, and whether the tile
//     survives it; the tile is loaded again when GW_Scene_1 comes back
// [Research] EastTileSelfTest loads the tile once at the main menu, reports
// and unloads it: bundle, shaders, collider and NavMesh proven in this
// player without anybody playing.
// Key [Research] EastTileKey (Insert) puts the local player 20 m west of the
// seam at z = 950 facing east, so the crossing can be walked.
//
// Nothing here runs unless the switch is on. AssetBundle is reached by
// reflection so build.ps1 needs no new reference for a research probe.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastTile
    {
        const string SceneName = "EastTileProbe";
        const string BundleFile = "east_tile_probe.bundle";
        const string RootName = "EastTileRoot";
        // Must match BuildEastTile.cs.
        const float EdgeX = 2500f;
        const float CrossZ = 950f;
        const float TileSize = 1000f;
        static readonly Vector3 AuthoredRoot = new Vector3(2500f, 506.11f, 450f);

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<string> _cfgKey;
        static ConfigEntry<bool> _cfgSelfTest;
        static bool _selfTestDone, _selfTest, _syncPending;
        static float _waitLogged;
        static KeyCode _key = KeyCode.Insert;
        static bool _keyParsed;

        static object _bundle;          // UnityEngine.AssetBundle, kept for the session
        static bool _bundleTried;
        static bool _failed;
        static AsyncOperation _loading;
        static float _loadStart;
        static int _loads;
        static GameObject _root;
        static Terrain _tile;
        static Renderer _cube;
        static bool _placed;
        static bool _hooked;
        static bool _cubeSeen;
        static float _nextWatch, _nextRemote, _secondReport;
        static Vector2 _lastWorld;
        static float _lastPlayerX = float.NaN;
        static bool _fellLogged;
        static string _activeBefore = "";

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Research", "EastTile", false,
                "East extension probe: load assets/east_tile_probe.bundle "
                + "additively in GW_Scene_1 and log whether a second terrain "
                + "tile east of x = 2500 loads, renders, carries the player and "
                + "survives scene changes. Research only, therefore off.");
            _cfgKey = cfg.Bind("Research", "EastTileKey", "Insert",
                "With EastTile on: put the local player 20 m west of the seam "
                + "at z = 950, facing east. A name from UnityEngine.KeyCode.");
            _cfgSelfTest = cfg.Bind("Research", "EastTileSelfTest", false,
                "With EastTile on: 20 s after start, load the tile once wherever "
                + "the game is (the main menu), log the same report, unload it. "
                + "Proves bundle, shaders, collider and NavMesh without playing.");
        }

        static bool On { get { return _cfgEnabled != null && _cfgEnabled.Value; } }

        internal static void Tick()
        {
            if (!On || _failed) return;
            try
            {
                Hook();
                if (_syncPending)
                {
                    if (!SceneManager.GetSceneByName(SceneName).isLoaded)
                    {
                        if (Time.realtimeSinceStartup - _loadStart > 30f)
                        {
                            Log("FAIL the synchronous load did not arrive within 30 s.");
                            _failed = true;
                        }
                        return;
                    }
                    _syncPending = false;
                    Place();
                    return;
                }
                if (_loading != null)
                {
                    if (!_loading.isDone)
                    {
                        // SceneStreamer holds chunk loads at 90 % too, and an
                        // async load queues behind any held one - say so.
                        float waited = Time.realtimeSinceStartup - _loadStart;
                        if (waited > _waitLogged + 10f)
                        {
                            _waitLogged = waited;
                            Log("load #" + _loads + " still waiting after " + waited.ToString("F0")
                                + " s, progress " + _loading.progress.ToString("F2") + ".");
                        }
                        return;
                    }
                    _loading = null;
                    Place();
                    return;
                }
                if (_placed && _root == null)
                {
                    // The Unity object is gone - the scene was unloaded.
                    _placed = false;
                    _tile = null;
                    _cube = null;
                    Log("tile objects destroyed; active scene " + SceneManager.GetActiveScene().name
                        + ", GW_Scene_1 loaded " + SceneManager.GetSceneByName(MapScene.Home).isLoaded + ".");
                }
                if (!_placed)
                {
                    if (EastWorld.On)
                    {
                        // [World] EastTile owns the load and the bundle (a second
                        // LoadFromFile of the same bundle fails). The probe only
                        // measures the tile it loaded - no self-test, no load.
                        if (!_selfTestDone && _cfgSelfTest.Value)
                            Log("EastTileSelfTest ignored: [World] EastTile loads the tile.");
                        _selfTestDone = true;
                        if (SceneManager.GetSceneByName(SceneName).isLoaded && Vanilla() != null)
                        {
                            _activeBefore = "(loaded by [World] EastTile)";
                            _loads++;
                            _loadStart = Time.realtimeSinceStartup;
                            Place();
                        }
                        return;
                    }
                    if (_cfgSelfTest.Value && !_selfTestDone && Time.realtimeSinceStartup > 20f
                        && MapScene.Current != MapScene.Home)
                    {
                        _selfTestDone = true;
                        _selfTest = true;
                        Log("SELF-TEST: loading the tile outside GW_Scene_1, in "
                            + SceneManager.GetActiveScene().name + ".");
                        StartLoad();
                    }
                    else if (Ready()) StartLoad();
                    return;
                }
                if (Time.time >= _secondReport && _secondReport > 0f)
                {
                    _secondReport = 0f;
                    Log("second report, 15 s after placement:");
                    Report();
                    if (_selfTest)
                    {
                        // Give the menu back what it had; the real load follows in GW_Scene_1.
                        _selfTest = false;
                        AsyncOperation op = SceneManager.UnloadSceneAsync(SceneName);
                        Log("SELF-TEST done, UnloadSceneAsync " + (op == null ? "returned null" : "started") + ".");
                    }
                }
                Watch();
                if (Input.GetKeyDown(Key())) ToSeam();
            }
            catch (Exception ex)
            {
                Log("probe error: " + ex);
            }
        }

        /// <summary>GW_Scene_1 is the active scene, its terrain is there and
        /// the local player exists - the world is up.</summary>
        static bool Ready()
        {
            if (MapScene.Current != MapScene.Home) return false;
            Scene home = SceneManager.GetSceneByName(MapScene.Home);
            if (!home.IsValid() || !home.isLoaded) return false;
            if (SceneManager.GetSceneByName(SceneName).isLoaded) return false;
            if (Vanilla() == null) return false;
            return MapTools.LocalPlayer() != null;
        }

        static void StartLoad()
        {
            if (!_bundleTried)
            {
                _bundleTried = true;
                string path = Path.Combine(RevivalPlugin.AssetDir, BundleFile);
                if (!File.Exists(path))
                {
                    Log("FAIL bundle missing: " + path);
                    _failed = true;
                    return;
                }
                Log("bundle " + path + " " + new FileInfo(path).Length + " bytes, sha1 " + Sha1(path) + ".");
                Type t = BundleType();
                MethodInfo load = t == null ? null
                    : AccessTools.Method(t, "LoadFromFile", new Type[] { typeof(string) }, null);
                if (load == null)
                {
                    Log("FAIL UnityEngine.AssetBundle.LoadFromFile not found.");
                    _failed = true;
                    return;
                }
                float t0 = Time.realtimeSinceStartup;
                _bundle = load.Invoke(null, new object[] { path });
                if (_bundle == null || (_bundle as UnityEngine.Object) == null)
                {
                    Log("FAIL AssetBundle.LoadFromFile returned null (see output_log for Unity's reason).");
                    _failed = true;
                    return;
                }
                Log("AssetBundle.LoadFromFile OK in " + Ms(t0) + " ms: streamed scene bundle "
                    + Prop(_bundle, "isStreamedSceneAssetBundle") + ", scenes "
                    + Join(Call(_bundle, "GetAllScenePaths") as string[]) + ".");
            }
            if (_bundle == null) return;
            _activeBefore = TerrainList();
            _loads++;
            _loadStart = Time.realtimeSinceStartup;
            if (_selfTest)
            {
                // The splash holds its own LoadSceneAsync(lobby) at 90 % with
                // allowSceneActivation = false until a region is clicked
                // (SplashScreenUI.WaitRegionSelect), and Unity queues every
                // later async load behind it. The self-test therefore loads
                // synchronously; the real load in GW_Scene_1 below stays async.
                _syncPending = true;
                SceneManager.LoadScene(SceneName, LoadSceneMode.Additive);
                Log("load #" + _loads + ": LoadScene(\"" + SceneName + "\", Additive) (sync) called in "
                    + SceneManager.GetActiveScene().name + ".");
                return;
            }
            _loading = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            _waitLogged = 0f;
            if (_loading == null)
            {
                Log("FAIL SceneManager.LoadSceneAsync(\"" + SceneName + "\", Additive) returned null.");
                _failed = true;
                return;
            }
            Log("load #" + _loads + ": LoadSceneAsync(\"" + SceneName + "\", Additive) started in "
                + SceneManager.GetActiveScene().name + ".");
        }

        static void Place()
        {
            Scene s = SceneManager.GetSceneByName(SceneName);
            Log("load #" + _loads + " done in " + Ms(_loadStart) + " ms: valid " + s.IsValid()
                + ", loaded " + s.isLoaded + ", buildIndex " + s.buildIndex + ", path " + s.path
                + ", roots " + (s.isLoaded ? s.rootCount : -1) + "; active scene "
                + SceneManager.GetActiveScene().name + ", scenes " + SceneList() + ".");
            if (!s.isLoaded)
            {
                Log("FAIL the scene did not load.");
                _failed = true;
                return;
            }
            GameObject[] roots = s.GetRootGameObjects();
            _root = null;
            for (int i = 0; i < roots.Length; i++)
                if (roots[i].name == RootName) _root = roots[i];
            if (_root == null)
            {
                Log("FAIL no " + RootName + " among " + roots.Length + " roots.");
                _failed = true;
                return;
            }
            _tile = _root.GetComponentInChildren<Terrain>();
            Transform c = _root.transform.Find("EastTileCube");
            _cube = c == null ? null : c.GetComponent<Renderer>();
            _placed = true;
            _cubeSeen = false;
            _fellLogged = false;
            _secondReport = Time.time + 15f;
            Report();
        }

        static void Report()
        {
            Vector3 rp = _root.transform.position;
            Log("root at " + V(rp) + " (authored " + V(AuthoredRoot) + ", off by "
                + (rp - AuthoredRoot).magnitude.ToString("F3") + " m), active " + _root.activeInHierarchy + ".");
            Log("terrains before load: " + _activeBefore);
            Log("terrains now: " + TerrainList());
            if (_tile != null)
            {
                Material m = _tile.materialTemplate;
                TerrainCollider tc = _tile.GetComponent<TerrainCollider>();
                Log("tile terrain: enabled " + _tile.enabled + ", size " + V(_tile.terrainData.size)
                    + ", heightmap " + _tile.terrainData.heightmapResolution + ", materialType "
                    + _tile.materialType + ", shader " + (m == null ? "none"
                        : m.shader.name + " supported " + m.shader.isSupported)
                    + ", layer " + _tile.gameObject.layer + " (" + LayerMask.LayerToName(_tile.gameObject.layer)
                    + "), tag " + _tile.gameObject.tag + ", collider " + (tc == null ? "none" : "enabled " + tc.enabled)
                    + ", drawHeightmap " + _tile.drawHeightmap + ".");
            }
            else Log("tile terrain: NONE found under the root.");
            if (_cube != null)
                Log("cube: enabled " + _cube.enabled + ", shader " + _cube.sharedMaterial.shader.name
                    + " supported " + _cube.sharedMaterial.shader.isSupported + ", bounds " + V(_cube.bounds.center)
                    + ", visible now " + _cube.isVisible + ".");

            Terrain v = Vanilla();
            float edge = v == null ? float.NaN : v.SampleHeight(new Vector3(EdgeX - 0.05f, 0f, CrossZ)) + v.GetPosition().y;
            Log("vanilla GWTerrain2 edge height at z " + CrossZ + ": " + edge.ToString("F2")
                + " (asset read 506.11), tile top " + (rp.y).ToString("F2") + ".");
            Ray("seam west  (2495, " + CrossZ + ")", new Vector3(EdgeX - 5f, 0f, CrossZ));
            Ray("seam east  (2505, " + CrossZ + ")", new Vector3(EdgeX + 5f, 0f, CrossZ));
            Ray("tile centre (3000, 950)", new Vector3(3000f, 0f, 950f));
            Ray("tile corner (3490, 1440)", new Vector3(3490f, 0f, 1440f));
            Ray("outside tile (3000, 1600)", new Vector3(3000f, 0f, 1600f));

            int ccLayer = LayerMask.NameToLayer("CharacterController");
            if (ccLayer >= 0 && _tile != null)
                Log("physics: layer CharacterController vs tile layer ignored "
                    + Physics.GetIgnoreLayerCollision(ccLayer, _tile.gameObject.layer) + ".");

            NavMeshHit onTile, onVanilla;
            Vector3 tileP = new Vector3(3000f, rp.y, 950f);
            // Outside GW_Scene_1 (the self-test) there is no vanilla terrain;
            // the point then sits at tile height and must find nothing.
            Vector3 vanP = new Vector3(EdgeX - 30f, rp.y, CrossZ);
            if (v != null) vanP.y = v.SampleHeight(vanP) + v.GetPosition().y;
            bool a = NavMesh.SamplePosition(tileP, out onTile, 5f, NavMesh.AllAreas);
            bool b = NavMesh.SamplePosition(vanP, out onVanilla, 10f, NavMesh.AllAreas);
            Log("navmesh: tile (3000, 950) " + (a ? "found at " + V(onTile.position) : "NONE within 5 m")
                + "; vanilla (2470, 950) " + (b ? "found at " + V(onVanilla.position) : "NONE within 10 m") + ".");
            if (a && b)
            {
                NavMeshPath path = new NavMeshPath();
                bool ok = NavMesh.CalculatePath(onVanilla.position, onTile.position, NavMesh.AllAreas, path);
                Vector3 end = path.corners.Length > 0 ? path.corners[path.corners.Length - 1] : Vector3.zero;
                Log("navmesh path vanilla -> tile: computed " + ok + ", status " + path.status + ", corners "
                    + path.corners.Length + ", ends at " + V(end) + ".");
            }

            Vector2 world = WorldSize();
            _lastWorld = world;
            Log("MapUIManager.WORLD_SIZE " + world + ".");
            Log("TerrainSurface at tile (3000, 950): " + SurfaceProbe(new Vector3(3000f, rp.y, 950f))
                + "; at vanilla (2470, 950): " + SurfaceProbe(vanP) + ".");
            Snapshot();
        }

        /// <summary>
        /// Render proof without eyes: a temporary camera 150 m west of the seam
        /// and 40 m up, looking at the cube, renders once into a RenderTexture.
        /// The PNG goes to the temp folder, and the log names the share of
        /// pixels that are the cube's red and the tile's checker brown - the
        /// player's own renderer drew the tile, or it did not.
        /// </summary>
        static void Snapshot()
        {
            const int W = 960, H = 540;
            GameObject go = new GameObject("EastTileSnapshotCam");
            RenderTexture rt = new RenderTexture(W, H, 24);
            Texture2D img = new Texture2D(W, H, TextureFormat.RGB24, false);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.farClipPlane = 3000f;
                cam.fieldOfView = 50f;
                Vector3 target = _cube != null ? _cube.bounds.center : new Vector3(3000f, AuthoredRoot.y, CrossZ);
                go.transform.position = new Vector3(EdgeX - 150f, AuthoredRoot.y + 40f, CrossZ);
                go.transform.LookAt(target);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                img.Apply();
                Color32[] px = img.GetPixels32();
                int red = 0, brown = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 c = px[i];
                    if (c.r > 90 && c.r > c.g * 2 && c.r > c.b * 2) red++;
                    else if (c.r > 40 && c.r >= c.g && c.g > c.b && c.r - c.b > 15 && c.r - c.b < 90) brown++;
                }
                string path = Path.Combine(Path.GetTempPath(), "east_tile_snapshot_" + _loads + "_"
                    + SceneManager.GetActiveScene().name + ".png");
                File.WriteAllBytes(path, img.EncodeToPNG());
                Log("snapshot " + path + ": cube-red pixels " + red + ", checker-brown pixels " + brown
                    + " of " + px.Length + ".");
            }
            catch (Exception ex)
            {
                Log("snapshot failed: " + ex.Message);
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.Destroy(go);
                UnityEngine.Object.Destroy(img);
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        /// <summary>Once a second while the tile is up: the local player near
        /// or on the tile, the cube becoming visible, WORLD_SIZE moving, and
        /// every two seconds remote players east of the edge.</summary>
        static void Watch()
        {
            if (_cube != null && !_cubeSeen && _cube.isVisible)
            {
                _cubeSeen = true;
                Log("cube RENDERED: a camera drew it (Renderer.isVisible).");
            }
            GameObject me = MapTools.LocalPlayer();
            if (me != null)
            {
                Vector3 p = me.transform.position;
                if (!float.IsNaN(_lastPlayerX) && (_lastPlayerX < EdgeX) != (p.x < EdgeX))
                    Log("player CROSSED the seam " + (p.x >= EdgeX ? "west -> east" : "east -> west")
                        + " at " + V(p) + ", under him " + Ground(p) + ".");
                _lastPlayerX = p.x;
                if (p.x > EdgeX && !_fellLogged && p.y < AuthoredRoot.y - 10f
                    && p.z > AuthoredRoot.z && p.z < AuthoredRoot.z + TileSize)
                {
                    _fellLogged = true;
                    Log("player FELL: " + V(p) + " is 10 m under the tile top, under him " + Ground(p) + ".");
                }
                if (Time.time >= _nextWatch && p.x > EdgeX - 50f)
                {
                    _nextWatch = Time.time + 1f;
                    Log("player " + V(p) + ", under him " + Ground(p) + ".");
                }
            }
            if (Time.time >= _nextRemote)
            {
                _nextRemote = Time.time + 2f;
                Vector2 world = WorldSize();
                if (world != _lastWorld)
                {
                    Log("MapUIManager.WORLD_SIZE changed " + _lastWorld + " -> " + world + ".");
                    _lastWorld = world;
                }
                Remotes(me);
            }
        }

        static void Remotes(GameObject me)
        {
            Type t = RevivalPlugin.TypeByName("PlayerMovementController");
            if (t == null) return;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null || (me != null && c.gameObject == me)) continue;
                Vector3 p = c.transform.position;
                if (p.x <= EdgeX) continue;
                Log("REMOTE player " + c.gameObject.name + " at " + V(p) + ", under him on this client "
                    + Ground(p) + ".");
            }
        }

        static void ToSeam()
        {
            Terrain v = Vanilla();
            if (v == null) return;
            Vector3 start = new Vector3(EdgeX - 20f, 0f, CrossZ);
            start.y = v.SampleHeight(start) + v.GetPosition().y + 1f;
            string msg;
            if (MapTools.TeleportLocal(start, out msg))
            {
                GameObject me = MapTools.LocalPlayer();
                if (me != null) me.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                Log("player put at the seam start " + V(start) + ", facing east. Walk east.");
            }
            else Log("teleport to the seam failed: " + msg);
        }

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneLoaded += delegate(Scene s, LoadSceneMode mode)
            {
                if (!On) return;
                Log("scene loaded: " + s.name + " (" + mode + ", buildIndex " + s.buildIndex + "); tile up "
                    + (_root != null) + ".");
            };
            SceneManager.sceneUnloaded += delegate(Scene s)
            {
                if (!On) return;
                Log("scene unloaded: " + s.name + " (buildIndex " + s.buildIndex + "); active "
                    + SceneManager.GetActiveScene().name + ", tile objects alive " + (_root != null) + ".");
            };
            SceneManager.activeSceneChanged += delegate(Scene from, Scene to)
            {
                if (!On) return;
                Log("active scene " + from.name + " -> " + to.name + ".");
            };
        }

        // ------------------------------------------------------------ helpers

        static Terrain Vanilla()
        {
            Terrain[] all = Terrain.activeTerrains;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].terrainData != null && all[i].terrainData.name == "GWTerrain2")
                    return all[i];
            return null;
        }

        static string TerrainList()
        {
            Terrain act = Terrain.activeTerrain;
            StringBuilder sb = new StringBuilder();
            sb.Append("activeTerrain ").Append(act == null ? "none" : act.name + "/"
                + (act.terrainData == null ? "?" : act.terrainData.name));
            Terrain[] all = Terrain.activeTerrains;
            sb.Append(", activeTerrains ").Append(all.Length).Append(" [");
            for (int i = 0; i < all.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(all[i] == null ? "null" : all[i].name + "@" + V(all[i].GetPosition()));
            }
            return sb.Append("]").ToString();
        }

        static void Ray(string what, Vector3 xz)
        {
            Log("ray " + what + ": " + Ground(new Vector3(xz.x, 1500f, xz.z)) + ".");
        }

        /// <summary>The first collider under a point, cast from 1.5 m above it
        /// (or from where it is, if that is already 1500 m).</summary>
        static string Ground(Vector3 p)
        {
            Vector3 from = p.y >= 1500f ? p : p + Vector3.up * 1.5f;
            RaycastHit hit;
            if (!Physics.Raycast(from, Vector3.down, out hit, 2000f, Physics.DefaultRaycastLayers,
                                 QueryTriggerInteraction.Ignore))
                return "NOTHING (no collider below)";
            return hit.collider.name + " [" + hit.collider.GetType().Name + ", layer "
                + hit.collider.gameObject.layer + ", scene " + hit.collider.gameObject.scene.name
                + "] y " + hit.point.y.ToString("F2") + (p.y >= 1500f ? ""
                    : ", " + (hit.point.y - p.y).ToString("F2") + " m from the point");
        }

        static Vector2 WorldSize()
        {
            Type t = RevivalPlugin.TypeByName("MapUIManager");
            FieldInfo f = t == null ? null : AccessTools.Field(t, "WORLD_SIZE");
            return f == null ? Vector2.zero : (Vector2)f.GetValue(null);
        }

        static string SurfaceProbe(Vector3 p)
        {
            Type t = RevivalPlugin.TypeByName("TerrainSurface");
            MethodInfo mix = t == null ? null : AccessTools.Method(t, "GetTextureMix", new Type[] { typeof(Vector3) }, null);
            MethodInfo mat = t == null ? null : AccessTools.Method(t, "GetCurrentTerrainMaterial", new Type[] { typeof(Vector3) }, null);
            if (mix == null || mat == null) return "TerrainSurface not found";
            string r;
            try
            {
                float[] w = mix.Invoke(null, new object[] { p }) as float[];
                r = "GetTextureMix " + (w == null ? "null" : w.Length + " weights");
            }
            catch (TargetInvocationException ex)
            {
                r = "GetTextureMix THREW " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            }
            try
            {
                r += ", GetCurrentTerrainMaterial " + mat.Invoke(null, new object[] { p });
            }
            catch (TargetInvocationException ex)
            {
                r += ", GetCurrentTerrainMaterial THREW " + ex.InnerException.GetType().Name + ": "
                    + ex.InnerException.Message;
            }
            return r;
        }

        static Type BundleType()
        {
            Type t = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule");
            return t ?? RevivalPlugin.TypeByName("UnityEngine.AssetBundle");
        }

        static object Call(object o, string method)
        {
            MethodInfo m = AccessTools.Method(o.GetType(), method, new Type[0], null);
            return m == null ? null : m.Invoke(o, null);
        }

        static object Prop(object o, string name)
        {
            MethodInfo m = AccessTools.PropertyGetter(o.GetType(), name);
            return m == null ? "?" : m.Invoke(o, null);
        }

        static string SceneList()
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (i > 0) sb.Append(", ");
                sb.Append(s.name).Append(s.isLoaded ? "" : "(loading)");
            }
            return sb.Append("]").ToString();
        }

        static string Sha1(string path)
        {
            byte[] h = System.Security.Cryptography.SHA1.Create().ComputeHash(File.ReadAllBytes(path));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        static KeyCode Key()
        {
            if (_keyParsed) return _key;
            _keyParsed = true;
            try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgKey.Value, true); }
            catch
            {
                _key = KeyCode.Insert;
                Log("EastTileKey " + _cfgKey.Value + " unknown, using Insert.");
            }
            return _key;
        }

        static string Join(string[] a) { return a == null ? "none" : "[" + string.Join(", ", a) + "]"; }
        static string Ms(float t0) { return ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0"); }
        static string V(Vector3 v) { return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F2") + ", " + v.z.ToString("F1") + ")"; }
        static void Log(string s) { RevivalPlugin.L.LogInfo("EastTile: " + s); }
    }
}

// Next Day: Survival - Revival Toolkit
//
// THE EAST WORLD. GW_Scene_1 plus one additively loaded terrain tile east of
// it, treated by the plugin and the game as ONE world. Off by default:
// [World] EastTile = false. The switch is read once at start; when it is off
// not one Harmony patch below is installed and every call site that asks this
// class takes its old, unchanged branch. The tile is the east tile of
// docs/ai/tasks/east-tile-design.md (assets/east_tile.bundle, scene "EastTile",
// 5000 x 5000 m at x 2500..7500, z -2500..2500), built by
// unity/EastTile/Assets/Editor/BuildTile.cs (docs/ai/tasks/east-tile-build.md).
// It draws its trees from terrains that do not draw a heightmap, as GW_Scene_1
// does; every lookup here takes the one terrain that does.
//
// The source of truth is docs/ai/tasks/east-extension-feasibility.md, sections
// 4.4 and 5; what was done with every row and why is in
// docs/ai/tasks/east-world.md.
//
// LOADING. The tile is queued as part of the GW_Scene_1 load itself: a postfix
// on GameScenesLoadingManager.LoadSceneNow wraps the game's load coroutine and
// issues LoadSceneAsync(tile, Additive) the moment the game has issued its own
// LoadSceneAsync(GW_Scene_1). Unity runs async loads in order, so the tile
// lands right after the world activates and AHEAD of every chunk SceneStreamer
// asks for later. Never synchronously (4.4: a sync additive load flushes the
// held chunk loads). A load that bypasses the coroutine (the sync branch, a
// transition) is caught by sceneLoaded(GW_Scene_1). The tile goes when
// GW_Scene_1 goes: a Single load takes both, and Tick unloads a tile that is
// still up without its world.
//
// SPAWN. NetworkGameServer.SpawnPlayer runs inside NetworkGameServer.Awake,
// i.e. while GW_Scene_1 is still being activated - no additive scene can exist
// before it, and a tile loaded before the world would be wiped by the world's
// own Single load. So a player whose SAVED position is on the tile is not put
// there by SpawnPlayer: the prefix hands SpawnPlayer a point 10 m inside the
// vanilla east edge (on vanilla terrain height), the finalizer restores the
// saved record, and Tick teleports him back onto his saved spot once the tile
// is up. He never stands where there is no ground. Guard() is the net under
// that (field run 2026-09-23, probe only: a relog on the tile fell 3.8 m under
// it and died): anybody east of the edge while the tile is not up goes to
// vanilla ground at once, and anybody under the tile surface with nothing
// below him is lifted onto it.
//
// ONE WORLD RECTANGLE. MapUIManager assumes a world centred on the origin
// (marker = pos / WORLD_SIZE, click = n * W - W / 2, fog = (x + W / 2) / W).
// With the tile the world is x -2500..7500, z -2500..2500: the OFFSET patch
// below keeps that rectangle and moves the centre to (2500, 0) - three small
// prefixes/postfixes. The alternative, a symmetric padded world (-7500..7500),
// would need no game patch but spends a third of every map picture on 5 km of
// nothing west of the map and gives the real world a third fewer pixels.
// The map WINDOW is Revival.EastMapPanel.cs: a 2:1 map as wide as the screen,
// so the 2:1 artwork (MapInk.ApplyEastArtwork) is shown undistorted.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastWorld
    {
        // The east tile: its bundle and scene (unity/EastTile, BuildTile.cs).
        internal const string SceneName = "EastTile";
        const string BundleFile = "east_tile.bundle";
        // Placed content, one additive scene bundle per concern
        // (unity/EastTile BuildContent.cs, docs/ai/tasks/east-pipeline.md):
        // loaded after the tile, each only if its file is installed, and
        // unloaded with the tile. File and scene, in load order.
        static readonly string[][] Content = {
            new[] { "east_airfield.bundle", "EastAirfield" },
            new[] { "east_town.bundle", "EastTown" },
            new[] { "east_bunker.bundle", "EastBunker" },
            new[] { "east_content_test.bundle", "EastContentTest" },
        };
        static readonly Dictionary<string, object> _contentBundles = new Dictionary<string, object>();
        static bool _contentQueued;

        /// <summary>The world with the tile on: vanilla GW_Scene_1 (-2500..2500)
        /// plus one 5 x 5 km tile east of it.</summary>
        internal static readonly Rect Extended = new Rect(-2500f, -2500f, 10000f, 5000f);
        /// <summary>GW_Scene_1's east edge: its terrain covers -2500..2500.</summary>
        const float VanillaEast = 2500f;
        /// <summary>How far inside the vanilla edge a held spawn is put.</summary>
        const float SeamInset = 10f;

        static ConfigEntry<bool> _cfg;
        /// <summary>The switch as read at start. False = nothing patched, every
        /// caller on its old branch.</summary>
        internal static bool On;

        static object _bundle;              // UnityEngine.AssetBundle, kept for the session
        static bool _bundleFailed;
        static AsyncOperation _op;
        static float _opSince, _opLogged;
        static bool _placed;
        static float _placedAt;
        static AsyncOperation _unloading;
        static bool _hooked;
        static SpawnHold _return;
        static float _returnSince;

        internal static void BindConfig(ConfigFile cfg)
        {
            _cfg = cfg.Bind("World", "EastTile", false,
                "East extension: load the east terrain tile "
                + "(assets/east_tile.bundle) together with GW_Scene_1 and treat "
                + "both as one world - map rectangle -2500..7500 x -2500..2500, "
                + "footstep and impact surface, graphics settings, troop and ground "
                + "group heights, a player saved on the tile. Read at start: a "
                + "change needs a restart. Off = the vanilla map, unchanged.");
            On = _cfg.Value;
        }

        // ================================================================ patches

        internal static void Install(Harmony h)
        {
            if (!On) return;
            Log("[World] EastTile is ON - GW_Scene_1 + " + SceneName + " as one world, rectangle "
                + Extended + ".");
            Patch(h, "GameScenesLoadingManager", "LoadSceneNow", null, "LoadPostfix", null);
            Patch(h, "NetworkGameServer", "SpawnPlayer", "SpawnPrefix", null, "SpawnFinalizer");
            Patch(h, "TerrainSurface", "GetTextureMix", "MixPrefix", null, null);
            Patch(h, "GameSettingsManager", "ApplyGameSettings", null, "SettingsPostfix", null);
            Patch(h, "MapUIManager", "InitWorldSize", null, "WorldSizePostfix", null);
            Patch(h, "MapUIManager", "InitMapPreset", null, "WorldSizePostfix", null);
            Patch(h, "MapUIManager", "SetMapLanguagePreset", null, "MapPresetPostfix", null);
            Patch(h, "MapUIManager", "WorldCoordNormalize", "NormalizePrefix", null, null);
            Patch(h, "MapUIManager", "WorldCoordDenormalize", "DenormalizePrefix", null, null);
            Patch(h, "MapUIManager", "FogSizeCheck", null, "FogPostfix", null);
            Patch(h, "LocationChangeTrigger", "Start", "LocationStartPrefix", null, null);
            EastMapPanel.Install(h);
        }

        static void Patch(Harmony h, string type, string method, string prefix, string postfix,
                          string finalizer)
        {
            try
            {
                Type t = RevivalPlugin.TypeByName(type);
                MethodInfo m = t == null ? null : AccessTools.Method(t, method, null, null);
                if (m == null)
                {
                    Log("PATCH MISSING " + type + "." + method + " - that part of the east world is off.");
                    return;
                }
                h.Patch(m,
                    prefix == null ? null : new HarmonyMethod(typeof(EastWorld).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix == null ? null : new HarmonyMethod(typeof(EastWorld).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)),
                    null,
                    finalizer == null ? null : new HarmonyMethod(typeof(EastWorld).GetMethod(finalizer, BindingFlags.Static | BindingFlags.NonPublic)),
                    null);
            }
            catch (Exception ex)
            {
                Log("PATCH FAILED " + type + "." + method + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------- loading

        /// <summary>Wraps the game's scene-load coroutine when it loads
        /// GW_Scene_1, so the tile is queued right behind it.</summary>
        static void LoadPostfix(string __0, ref IEnumerator __result)
        {
            if (__result != null && __0 == MapScene.Home) __result = new LoadWrap(__result);
        }

        /// <summary>
        /// The game's LoadSceneNow coroutine, one step at a time. The step that
        /// issues LoadSceneAsync(GW_Scene_1) stores it in the iterator's
        /// &lt;asyncLoad&gt;__3 field (IL, docs/ai/tasks/east-world.md); right
        /// after that step the tile is queued. The sync branch and the
        /// transition branch never fill the field - sceneLoaded catches those.
        /// </summary>
        sealed class LoadWrap : IEnumerator
        {
            readonly IEnumerator _inner;
            readonly FieldInfo _main;
            bool _done;

            internal LoadWrap(IEnumerator inner)
            {
                _inner = inner;
                _main = AccessTools.Field(inner.GetType(), "<asyncLoad>__3");
                if (_main == null) Log("LoadSceneNow iterator has no <asyncLoad>__3 - the tile follows sceneLoaded instead.");
            }

            public object Current { get { return _inner.Current; } }

            public bool MoveNext()
            {
                bool more = _inner.MoveNext();
                if (!_done && _main != null && _main.GetValue(_inner) is AsyncOperation)
                {
                    _done = true;
                    QueueTile("queued behind the game's own GW_Scene_1 load");
                }
                return more;
            }

            public void Reset() { _inner.Reset(); }
        }

        static void QueueTile(string why)
        {
            try
            {
                if (_op != null || TileLoaded()) return;
                if (!Bundle()) return;
                _op = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
                _opSince = Time.realtimeSinceStartup;
                _opLogged = 0f;
                Log(_op == null ? "FAIL LoadSceneAsync(" + SceneName + ", Additive) returned null."
                                : "tile load " + why + ".");
                if (_op != null) QueueContent();
            }
            catch (Exception ex)
            {
                Log("tile load failed: " + ex.Message);
            }
        }

        /// <summary>The content scenes, right behind the tile's load (Unity
        /// runs async loads in order). A missing file is skipped: a checkout
        /// without that concern built simply has none of it.</summary>
        static void QueueContent()
        {
            if (_contentQueued) return;
            _contentQueued = true;
            foreach (string[] c in Content)
            {
                try
                {
                    if (SceneManager.GetSceneByName(c[1]).isLoaded) continue;
                    string path = Path.Combine(RevivalPlugin.AssetDir, c[0]);
                    if (!File.Exists(path)) continue;
                    if (!_contentBundles.ContainsKey(c[0]))
                    {
                        object b = OpenBundle(path);
                        if (b == null) continue;
                        _contentBundles[c[0]] = b;
                    }
                    AsyncOperation op = SceneManager.LoadSceneAsync(c[1], LoadSceneMode.Additive);
                    Log(op == null ? "FAIL LoadSceneAsync(" + c[1] + ", Additive) returned null."
                                   : "content " + c[1] + " queued after the tile (" + c[0] + ").");
                }
                catch (Exception ex)
                {
                    Log("content " + c[1] + " load failed: " + ex.Message);
                }
            }
        }

        static void UnloadContent()
        {
            _contentQueued = false;
            foreach (string[] c in Content)
            {
                if (!SceneManager.GetSceneByName(c[1]).isLoaded) continue;
                SceneManager.UnloadSceneAsync(c[1]);
                Log("content " + c[1] + " unloaded with the tile.");
            }
        }

        /// <summary>AssetBundle.LoadFromFile by reflection (the module is
        /// not referenced at compile time); null and a log line on failure.</summary>
        static object OpenBundle(string path)
        {
            Type t = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule")
                     ?? RevivalPlugin.TypeByName("UnityEngine.AssetBundle");
            MethodInfo load = t == null ? null
                : AccessTools.Method(t, "LoadFromFile", new Type[] { typeof(string) }, null);
            if (load == null)
            {
                Log("FAIL UnityEngine.AssetBundle.LoadFromFile not found.");
                return null;
            }
            object b = load.Invoke(null, new object[] { path });
            if (b == null || (b as UnityEngine.Object) == null)
            {
                Log("FAIL AssetBundle.LoadFromFile(" + path + ") returned null (output_log names Unity's reason).");
                return null;
            }
            Log("bundle " + path + " opened, " + new FileInfo(path).Length + " bytes.");
            return b;
        }

        static bool Bundle()
        {
            if (_bundle != null) return true;
            if (_bundleFailed) return false;
            _bundleFailed = true;
            string path = Path.Combine(RevivalPlugin.AssetDir, BundleFile);
            if (!File.Exists(path))
            {
                Log("FAIL tile bundle missing: " + path + " - GW_Scene_1 stays vanilla ground.");
                return false;
            }
            Type t = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule")
                     ?? RevivalPlugin.TypeByName("UnityEngine.AssetBundle");
            MethodInfo load = t == null ? null
                : AccessTools.Method(t, "LoadFromFile", new Type[] { typeof(string) }, null);
            if (load == null)
            {
                Log("FAIL UnityEngine.AssetBundle.LoadFromFile not found.");
                return false;
            }
            _bundle = load.Invoke(null, new object[] { path });
            if (_bundle == null || (_bundle as UnityEngine.Object) == null)
            {
                _bundle = null;
                Log("FAIL AssetBundle.LoadFromFile(" + path + ") returned null (output_log names Unity's reason).");
                return false;
            }
            _bundleFailed = false;
            Log("tile bundle " + path + " opened, " + new FileInfo(path).Length + " bytes.");
            return true;
        }

        static bool TileLoaded()
        {
            return SceneManager.GetSceneByName(SceneName).isLoaded;
        }

        static bool HomeLoaded()
        {
            return SceneManager.GetSceneByName(MapScene.Home).isLoaded;
        }

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneLoaded += delegate(Scene s, LoadSceneMode mode)
            {
                if (s.name == MapScene.Home && mode == LoadSceneMode.Single)
                    QueueTile("queued after sceneLoaded(GW_Scene_1) - the load did not go through LoadSceneNow's async branch");
                else if (s.name == SceneName)
                    Log("tile scene loaded (" + mode + "); active scene " + SceneManager.GetActiveScene().name + ".");
            };
            SceneManager.sceneUnloaded += delegate(Scene s)
            {
                if (s.name != SceneName) return;
                _placed = false;
                UnloadContent();
                Log("tile scene unloaded; GW_Scene_1 loaded " + HomeLoaded() + ".");
            };
        }

        internal static void Tick()
        {
            if (!On) return;
            try
            {
                Hook();
                if (_op != null)
                {
                    if (_op.isDone) _op = null;
                    else
                    {
                        // 4.4: an async load waits behind any HELD one. Say so.
                        float waited = Time.realtimeSinceStartup - _opSince;
                        if (waited > _opLogged + 10f)
                        {
                            _opLogged = waited;
                            Log("tile load still waiting after " + waited.ToString("F0") + " s, progress "
                                + _op.progress.ToString("F2") + ".");
                        }
                    }
                }
                bool tile = TileLoaded();
                if (tile && !_placed) Placed();
                if (tile && _unloading == null && !HomeLoaded() && _op == null)
                {
                    _unloading = SceneManager.UnloadSceneAsync(SceneName);
                    Log("GW_Scene_1 is gone but the tile is not - unloading it.");
                }
                if (_unloading != null && _unloading.isDone) _unloading = null;
                Guard();
                if (_return != null) ReturnToTile();
                ApplyWorldSize("tick");
            }
            catch (Exception ex)
            {
                Log("tick error: " + ex);
            }
        }

        static void Placed()
        {
            _placed = true;
            _placedAt = Time.realtimeSinceStartup;
            Terrain[] mine = TileTerrains();
            Log("tile up " + (Time.realtimeSinceStartup - _opSince).ToString("F1") + " s after it was queued: "
                + mine.Length + " terrain(s)" + (mine.Length > 0 ? ", first " + mine[0].name + " at "
                + mine[0].GetPosition() + " size " + mine[0].terrainData.size : "")
                + "; activeTerrain " + (Terrain.activeTerrain == null ? "none" : Terrain.activeTerrain.name) + ".");
            SyncSettings(CurrentSettingsTerrain(), "tile placed");
            EastWater.Attach(SceneManager.GetSceneByName(SceneName));
        }

        static Terrain[] TileTerrains()
        {
            Scene s = SceneManager.GetSceneByName(SceneName);
            if (!s.isLoaded) return new Terrain[0];
            List<Terrain> all = new List<Terrain>();
            GameObject[] roots = s.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                all.AddRange(roots[i].GetComponentsInChildren<Terrain>(true));
            return all.ToArray();
        }

        // --------------------------------------------------------------- spawn

        internal sealed class SpawnHold
        {
            internal object Owner;          // the object that holds the state (for value types)
            internal FieldInfo StateField;
            internal object State;
            internal FieldInfo Position;
            internal Vector3 Saved, Seam;
        }

        static FieldInfo _fBackend, _fOptions, _fGameMode, _fGameScene, _fCharData, _fState,
                         _fLevel, _fPosition;
        static MethodInfo _mCurrentData;

        /// <summary>SpawnPlayer places a player on his saved position when the
        /// scene matches (IL: _gameMode != 3, LevelID == _gameScene, LevelID !=
        /// 0, Position != zero). A saved position east of the vanilla edge while
        /// the tile is not up yet is swapped for a point on vanilla ground at
        /// the seam; SpawnFinalizer restores the record.</summary>
        static void SpawnPrefix(object __instance, out SpawnHold __state)
        {
            __state = null;
            // the saddle cuts first: SpawnPlayer must put a player saved on a
            // cut onto the cut ground, not into the old berm
            EastCrossings.BeforeSpawn();
            try
            {
                if (TileLoaded()) return;
                SpawnHold hold = SavedSpawn(__instance);
                if (hold == null) return;
                Vector3 p = hold.Saved;
                if (p.x <= VanillaEast || !Extended.Contains(new Vector2(p.x, p.z))) return;
                if (!SeamPoint(p, out hold.Seam))
                {
                    Log("spawn at " + p + " is on the tile, which is not up yet, and no vanilla terrain was "
                        + "found to hold him on - SpawnPlayer left alone.");
                    return;
                }
                SetPosition(hold, hold.Seam);
                __state = hold;
                Log("spawn: saved position " + p + " is on the tile, which is not up yet (SpawnPlayer runs "
                    + "inside GW_Scene_1's Awake) - spawning at the seam " + hold.Seam + " instead.");
            }
            catch (Exception ex)
            {
                Log("spawn check failed, SpawnPlayer left alone: " + ex.Message);
            }
        }

        static Exception SpawnFinalizer(Exception __exception, SpawnHold __state)
        {
            if (__state == null) return __exception;
            try
            {
                SetPosition(__state, __state.Saved);
                if (__exception == null)
                {
                    _return = __state;
                    _returnSince = Time.realtimeSinceStartup;
                }
            }
            catch (Exception ex)
            {
                Log("spawn record restore failed: " + ex.Message);
            }
            return __exception;
        }

        static SpawnHold SavedSpawn(object server)
        {
            Type t = server.GetType();
            if (_fBackend == null)
            {
                _fBackend = AccessTools.Field(t, "_backendManager");
                _fOptions = AccessTools.Field(t, "_networkServerOptions");
            }
            object backend = _fBackend == null ? null : _fBackend.GetValue(server);
            object options = _fOptions == null ? null : _fOptions.GetValue(server);
            if (backend == null || options == null) return null;
            if (_mCurrentData == null)
            {
                _mCurrentData = AccessTools.Method(backend.GetType(), "GetCurrentGameModeCharacterData", null, null);
                _fGameMode = AccessTools.Field(options.GetType(), "_gameMode");
                _fGameScene = AccessTools.Field(options.GetType(), "_gameScene");
            }
            if (_mCurrentData == null || _fGameMode == null || _fGameScene == null) return null;
            object mode = _mCurrentData.Invoke(backend, null);
            if (mode == null) return null;
            if (_fCharData == null) _fCharData = AccessTools.Field(mode.GetType(), "characterData");
            object data = _fCharData == null ? null : _fCharData.GetValue(mode);
            if (data == null) return null;
            if (_fState == null) _fState = AccessTools.Field(data.GetType(), "playerStateData");
            object state = _fState == null ? null : _fState.GetValue(data);
            if (state == null) return null;
            if (_fLevel == null)
            {
                _fLevel = AccessTools.Field(state.GetType(), "LevelID");
                _fPosition = AccessTools.Field(state.GetType(), "Position");
            }
            if (_fLevel == null || _fPosition == null) return null;

            // The same test SpawnPlayer makes before it uses the saved position.
            if (Convert.ToInt32(_fGameMode.GetValue(options)) == 3) return null;
            int level = Convert.ToInt32(_fLevel.GetValue(state));
            if (level == 0 || level != Convert.ToInt32(_fGameScene.GetValue(options))) return null;
            Vector3 pos = (Vector3)_fPosition.GetValue(state);
            if (pos == Vector3.zero) return null;

            SpawnHold hold = new SpawnHold();
            hold.Owner = data;
            hold.StateField = _fState;
            hold.State = state;
            hold.Position = _fPosition;
            hold.Saved = pos;
            return hold;
        }

        static void SetPosition(SpawnHold hold, Vector3 p)
        {
            hold.Position.SetValue(hold.State, p);
            // A struct comes back boxed: write the copy back into its owner.
            if (hold.StateField.FieldType.IsValueType) hold.StateField.SetValue(hold.Owner, hold.State);
        }

        /// <summary>A point 10 m inside the vanilla east edge at the saved z,
        /// on the vanilla terrain's height. The scene is still being activated,
        /// so the terrain is found as an object, not through activeTerrain, and
        /// its height is read from its data - no collider needed.</summary>
        static bool SeamPoint(Vector3 saved, out Vector3 seam)
        {
            float x = VanillaEast - SeamInset;
            float z = Mathf.Clamp(saved.z, -VanillaEast + SeamInset, VanillaEast - SeamInset);
            seam = new Vector3(x, saved.y, z);
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(typeof(Terrain));
            for (int i = 0; i < all.Length; i++)
            {
                Terrain t = all[i] as Terrain;
                if (t == null || t.terrainData == null || t.gameObject.scene.name != MapScene.Home) continue;
                if (!Contains(t, x, z)) continue;
                seam.y = t.GetPosition().y + t.SampleHeight(new Vector3(x, 0f, z)) + 1.5f;
                return true;
            }
            return false;
        }

        /// <summary>Once the tile is up (and its colliders have had a second),
        /// the held player goes to the spot he was saved on - unless he has
        /// already walked away from the seam or died.</summary>
        static void ReturnToTile()
        {
            GameObject me = MapTools.LocalPlayer();
            if (!_placed || Time.realtimeSinceStartup - _placedAt < 1f || me == null)
            {
                if (Time.realtimeSinceStartup - _returnSince > 180f)
                {
                    Log("no tile and player within 180 s - the player stays at the seam " + _return.Seam + ".");
                    _return = null;
                }
                return;
            }
            SpawnHold hold = _return;
            _return = null;
            if ((me.transform.position - hold.Seam).sqrMagnitude > 50f * 50f)
            {
                Log("the player has left the seam (" + me.transform.position + ") - not moved back to " + hold.Saved + ".");
                return;
            }
            Vector3 dest = hold.Saved;
            float ground;
            if (TerrainHeight(dest, out ground)) dest.y = Mathf.Max(dest.y, ground) + 0.3f;
            string msg;
            if (MapTools.TeleportLocal(dest, out msg)) Log("player returned to his saved spot on the tile " + dest + ".");
            else Log("return to " + dest + " failed: " + msg);
        }

        static float _nextLift;

        /// <summary>
        /// The safety net under SpawnPrefix, every frame in the east world.
        /// Field run 2026-09-23 (probe only, without this class): a player who
        /// logged out on the tile was spawned there 1.6 s before the tile
        /// existed, fell, ended 3.8 m UNDER the tile surface and died.
        ///
        /// 1. East of the vanilla edge while the tile is not up, whatever put
        ///    him there: he goes at once to solid vanilla ground 10 m inside the
        ///    edge and back to that spot when the tile is up (ReturnToTile).
        ///    Relocating, not freezing: the game's movement controller keeps
        ///    integrating its fall while a CharacterController is switched off
        ///    or pinned, and would hand him that fall on release.
        /// 2. On a tile point, more than a metre under the tile surface with no
        ///    collider under him: lifted onto the surface.
        /// </summary>
        static void Guard()
        {
            if (!Extends || !HomeLoaded()) return;
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return;
            Vector3 p = me.transform.position;
            string msg;
            if (!TileLoaded())
            {
                if (p.x <= VanillaEast || !Extended.Contains(new Vector2(p.x, p.z))) return;
                Vector3 seam;
                if (!SeamPoint(p, out seam) || !MapTools.TeleportLocal(seam, out msg))
                {
                    if (Time.time >= _nextLift)
                    {
                        _nextLift = Time.time + 2f;
                        Log("player at " + p + " is east of the edge with no tile, and could not be moved to vanilla ground.");
                    }
                    return;
                }
                SpawnHold hold = new SpawnHold();
                hold.Saved = p;
                hold.Seam = seam;
                _return = hold;
                _returnSince = Time.realtimeSinceStartup;
                Log("player at " + p + " is on the tile rectangle but the tile is not up - held on vanilla ground at "
                    + seam + " until it is.");
                return;
            }
            if (!_placed || Time.realtimeSinceStartup - _placedAt < 1f || Time.time < _nextLift) return;
            Terrain tile = OtherTerrainAt(p.x, p.z, Terrain.activeTerrain);
            if (tile == null || tile.gameObject.scene.name != SceneName) return;
            float surface = tile.GetPosition().y + tile.SampleHeight(p);
            if (p.y >= surface - 1f) return;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, 100f, Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore)) return;     // standing on something down there
            _nextLift = Time.time + 2f;
            Vector3 up = new Vector3(p.x, surface + 0.5f, p.z);
            if (MapTools.TeleportLocal(up, out msg))
                Log("player " + (surface - p.y).ToString("F2") + " m under the tile surface at " + p
                    + " with nothing under him - lifted onto it at " + up + ".");
            else Log("lift onto the tile at " + up + " failed: " + msg);
        }

        // ------------------------------------------------------------- terrain

        static Terrain[] _terrains;
        static int _terrainsFrame = -1;

        static bool Contains(Terrain t, float x, float z)
        {
            Vector3 o = t.GetPosition();
            Vector3 s = t.terrainData.size;
            return !(x < o.x || z < o.z || x > o.x + s.x || z > o.z + s.z);
        }

        /// <summary>The terrain under x/z: the active terrain when it holds the
        /// point (so every vanilla point gets exactly the vanilla terrain),
        /// otherwise any other active terrain that does - the tile.</summary>
        internal static Terrain TerrainAt(float x, float z)
        {
            Terrain act = Terrain.activeTerrain;
            if (act != null && act.terrainData != null && Contains(act, x, z)) return act;
            return OtherTerrainAt(x, z, act);
        }

        static Terrain OtherTerrainAt(float x, float z, Terrain except)
        {
            if (_terrainsFrame != Time.frameCount)
            {
                _terrainsFrame = Time.frameCount;
                _terrains = Terrain.activeTerrains;
            }
            for (int i = 0; i < _terrains.Length; i++)
            {
                Terrain t = _terrains[i];
                // Terrains that only draw trees (the tile's, like GW_Scene_1's
                // billboard twins) carry no ground: a flat stand-in heightmap
                // and no splat. The ground is the terrain drawing the heightmap.
                if (t == null || t == except || t.terrainData == null || !t.drawHeightmap) continue;
                if (Contains(t, x, z)) return t;
            }
            return null;
        }

        /// <summary>Height of the terrain under xz from its height data - the
        /// east-world answer to RevivalTroopInsertion.TerrainHeight.</summary>
        internal static bool TerrainHeight(Vector3 xz, out float y)
        {
            y = 0f;
            Terrain t = TerrainAt(xz.x, xz.z);
            if (t == null) return false;
            y = t.GetPosition().y + t.SampleHeight(new Vector3(xz.x, 0f, xz.z));
            return true;
        }

        /// <summary>Is there terrain under this point - the east-world bound
        /// that replaces RevivalGroundEnemies' fixed +-2500.</summary>
        internal static bool OnTerrain(Vector3 p)
        {
            return TerrainAt(p.x, p.z) != null;
        }

        // ------------------------------------------------------ surface (sound)

        sealed class LayerMap
        {
            internal TerrainData Vanilla;
            internal int[] ToVanilla;
        }

        static readonly Dictionary<TerrainData, LayerMap> _layerMaps = new Dictionary<TerrainData, LayerMap>();

        /// <summary>
        /// TerrainSurface.GetTextureMix reads Terrain.activeTerrain's splat at
        /// the point's position relative to THAT terrain - on the tile an index
        /// out of the vanilla range. Here the mix is read from the terrain under
        /// the point and returned in the VANILLA layer order (each tile layer
        /// goes onto the vanilla layer with the same texture), so
        /// GetMainTexture and GetCurrentTerrainMaterial - which call this and
        /// map the index through the vanilla TerrainSplatMapsMaterialType -
        /// give the tile's ground its own footstep, impact and grip material.
        /// A point on the active terrain runs the original untouched.
        /// </summary>
        static bool MixPrefix(Vector3 __0, ref float[] __result)
        {
            try
            {
                Terrain act = Terrain.activeTerrain;
                if (act == null || act.terrainData == null || Contains(act, __0.x, __0.z)) return true;
                Terrain t = OtherTerrainAt(__0.x, __0.z, act);
                if (t == null) return true;
                TerrainData d = t.terrainData;
                Vector3 o = t.GetPosition();
                int w = d.alphamapWidth, hgt = d.alphamapHeight;
                int ix = Mathf.Clamp((int)((__0.x - o.x) / d.size.x * w), 0, w - 1);
                int iz = Mathf.Clamp((int)((__0.z - o.z) / d.size.z * hgt), 0, hgt - 1);
                float[,,] a = d.GetAlphamaps(ix, iz, 1, 1);
                int[] map = Layers(d, act.terrainData);
                float[] mix = new float[Math.Max(1, act.terrainData.alphamapLayers)];
                for (int j = 0; j < a.GetLength(2) && j < map.Length; j++) mix[map[j]] += a[0, 0, j];
                __result = mix;
                return false;
            }
            catch (Exception ex)
            {
                Log("GetTextureMix on the tile failed, vanilla answer used: " + ex.Message);
                return true;
            }
        }

        static int[] Layers(TerrainData tile, TerrainData vanilla)
        {
            LayerMap m;
            if (_layerMaps.TryGetValue(tile, out m) && m.Vanilla == vanilla) return m.ToVanilla;
            SplatPrototype[] ts = tile.splatPrototypes;
            SplatPrototype[] vs = vanilla.splatPrototypes;
            int[] map = new int[ts.Length];
            List<string> missing = new List<string>();
            for (int i = 0; i < ts.Length; i++)
            {
                string name = ts[i].texture == null ? "" : ts[i].texture.name;
                int found = -1;
                for (int j = 0; j < vs.Length && found < 0; j++)
                    if (vs[j].texture != null && vs[j].texture.name == name) found = j;
                if (found < 0) missing.Add(i + ":" + name);
                map[i] = found < 0 ? 0 : found;
            }
            m = new LayerMap();
            m.Vanilla = vanilla;
            m.ToVanilla = map;
            _layerMaps[tile] = m;
            string[] shown = new string[map.Length];
            for (int i = 0; i < map.Length; i++) shown[i] = map[i].ToString();
            Log("surface: tile " + tile.name + " has " + ts.Length + " splat layer(s), mapped onto vanilla "
                + vanilla.name + " [" + string.Join(", ", shown)
                + "]" + (missing.Count == 0 ? "." : "; no vanilla texture of the same name for "
                    + string.Join(", ", missing.ToArray()) + " - those count as vanilla layer 0."));
            return map;
        }

        // ------------------------------------------------------------ settings

        static FieldInfo _fCurTerrain;

        /// <summary>GameSettingsManager.ApplyGameSettings writes the player's
        /// graphics settings onto its curTerrain only. Copy them onto the
        /// tile.</summary>
        static void SettingsPostfix(object __instance)
        {
            try
            {
                if (_fCurTerrain == null) _fCurTerrain = AccessTools.Field(__instance.GetType(), "curTerrain");
                Terrain cur = _fCurTerrain == null ? null : _fCurTerrain.GetValue(__instance) as Terrain;
                SyncSettings(cur, "ApplyGameSettings");
            }
            catch (Exception ex)
            {
                Log("graphics settings onto the tile failed: " + ex.Message);
            }
        }

        static Terrain CurrentSettingsTerrain()
        {
            try
            {
                Type t = RevivalPlugin.TypeByName("GameSettingsManager");
                MethodInfo get = t == null ? null : AccessTools.PropertyGetter(t, "Instance");
                object gsm = get == null ? null : get.Invoke(null, null);
                if (gsm != null)
                {
                    if (_fCurTerrain == null) _fCurTerrain = AccessTools.Field(t, "curTerrain");
                    Terrain cur = _fCurTerrain == null ? null : _fCurTerrain.GetValue(gsm) as Terrain;
                    if (cur != null) return cur;
                }
            }
            catch { }
            return Terrain.activeTerrain;
        }

        /// <summary>The five values ApplyGameSettings sets, copied from the
        /// terrain it set them on onto every tile terrain.</summary>
        static void SyncSettings(Terrain from, string why)
        {
            if (from == null) return;
            Terrain[] tile = TileTerrains();
            for (int i = 0; i < tile.Length; i++)
            {
                Terrain t = tile[i];
                if (t == null || t == from) continue;
                t.detailObjectDistance = from.detailObjectDistance;
                t.detailObjectDensity = from.detailObjectDensity;
                t.treeBillboardDistance = from.treeBillboardDistance;
                t.treeCrossFadeLength = from.treeCrossFadeLength;
                t.materialType = from.materialType;
            }
            if (tile.Length > 0)
                Log("graphics settings of " + from.name + " onto " + tile.Length + " tile terrain(s) (" + why
                    + "): detail " + from.detailObjectDistance + " m x " + from.detailObjectDensity
                    + ", billboards " + from.treeBillboardDistance + " m, material " + from.materialType + ".");
        }

        // ------------------------------------------------------ world rectangle

        static int _extFrame = -1;
        static bool _ext;
        static PropertyInfo _pServer;
        static FieldInfo _fWorldSize, _fServerOptions, _fServerScene;

        /// <summary>
        /// True while the map shows the east world: the switch is on and the
        /// game's scene is GW_Scene_1. Read from NetworkGameServer's
        /// _networkServerOptions._gameScene - the key MapUIManager.InitMapPreset
        /// itself uses - so it is right while the scene is still starting, when
        /// the active scene may not have switched yet. Cached per frame.
        /// </summary>
        internal static bool Extends
        {
            get
            {
                if (!On) return false;
                int frame = Time.frameCount;
                if (_extFrame == frame) return _ext;
                _extFrame = frame;
                string scene = GameScene();
                _ext = (scene ?? MapScene.Current) == MapScene.Home;
                return _ext;
            }
        }

        static string GameScene()
        {
            try
            {
                if (_pServer == null)
                {
                    Type t = RevivalPlugin.TypeByName("NetworkGameServer");
                    if (t == null) return null;
                    _pServer = AccessTools.Property(t, "Instance");
                    _fServerOptions = AccessTools.Field(t, "_networkServerOptions");
                }
                object server = _pServer == null ? null : _pServer.GetValue(null, null);
                if (server == null || (server as UnityEngine.Object) == null || _fServerOptions == null) return null;
                object options = _fServerOptions.GetValue(server);
                if (options == null) return null;
                if (_fServerScene == null) _fServerScene = AccessTools.Field(options.GetType(), "_gameScene");
                object scene = _fServerScene == null ? null : _fServerScene.GetValue(options);
                return scene == null ? null : scene.ToString();
            }
            catch { return null; }
        }

        /// <summary>The centre of the world rectangle the map shows: (2500, 0)
        /// in the east world, the origin otherwise. Callers that project with
        /// the game's centred formula subtract it; 0 subtracts exactly.</summary>
        internal static Vector2 MapCentre
        {
            get { return Extends ? Extended.center : Vector2.zero; }
        }

        /// <summary>Where a world point lies on the map, 0..1 on both axes
        /// (unclamped). Not in the east world this is the game's centred
        /// formula pos / WORLD_SIZE + 0.5, written exactly as the callers had
        /// it.</summary>
        internal static Vector2 Fraction(Vector3 pos, Vector2 worldSize)
        {
            if (!Extends) return new Vector2(pos.x / worldSize.x + 0.5f, pos.z / worldSize.y + 0.5f);
            return new Vector2((pos.x - Extended.xMin) / Extended.width, (pos.z - Extended.yMin) / Extended.height);
        }

        static void WorldSizePostfix()
        {
            ApplyWorldSize("MapUIManager init");
        }

        static void MapPresetPostfix(object __instance, object __0)
        {
            MapInk.ApplyEastArtwork(__instance, __0);
            EastMapPanel.ApplyPreset(__instance as Component, __0);
        }

        static void LocationStartPrefix(object __instance)
        {
            EastCrossings.BeforeLocationMarker(__instance);
        }

        /// <summary>MapUIManager.WORLD_SIZE = the east world's size while the map
        /// shows it. InitWorldSize and InitMapPreset both write the vanilla
        /// value; this runs after each and in Tick.</summary>
        static void ApplyWorldSize(string why)
        {
            if (!Extends) return;
            if (_fWorldSize == null)
            {
                Type t = RevivalPlugin.TypeByName("MapUIManager");
                _fWorldSize = t == null ? null : AccessTools.Field(t, "WORLD_SIZE");
                if (_fWorldSize == null) return;
            }
            Vector2 want = Extended.size;
            Vector2 had = (Vector2)_fWorldSize.GetValue(null);
            if (had == want) return;
            _fWorldSize.SetValue(null, want);
            Log("MapUIManager.WORLD_SIZE " + had + " -> " + want + " (" + why + "); world x "
                + Extended.xMin + ".." + Extended.xMax + ", z " + Extended.yMin + ".." + Extended.yMax + ".");
        }

        /// <summary>Marker placement: the game's pos / WORLD_SIZE about the
        /// origin becomes (pos - centre) / WORLD_SIZE.</summary>
        static bool NormalizePrefix(Vector2 __0, ref Vector2 __result)
        {
            if (!Extends) return true;
            Vector2 c = Extended.center;
            __result = new Vector2((__0.x - c.x) / Extended.width, (__0.y - c.y) / Extended.height);
            return false;
        }

        /// <summary>Map click to world: the game's n * W - W / 2 becomes
        /// xMin + n * W.</summary>
        static bool DenormalizePrefix(Vector2 __0, ref Vector3 __result)
        {
            if (!Extends) return true;
            __result = new Vector3(Extended.xMin + __0.x * Extended.width, 0f,
                                   Extended.yMin + __0.y * Extended.height);
            return false;
        }

        static FieldInfo _fFogObject, _fFogMask, _fFogFake, _fFogBounds, _fFogX, _fFogZ;

        /// <summary>FogSizeCheck places the survival-fog mask at
        /// (x + W / 2) / W. In the east world the fraction is (x - xMin) / W;
        /// the rest is the method's own last statement, repeated.</summary>
        static void FogPostfix(object __instance)
        {
            if (!Extends) return;
            try
            {
                if (_fFogObject == null)
                {
                    Type t = __instance.GetType();
                    _fFogObject = AccessTools.Field(t, "SurvFogObject");
                    _fFogMask = AccessTools.Field(t, "SurvFogMask");
                    _fFogFake = AccessTools.Field(t, "SurvFogFakeMap");
                    _fFogBounds = AccessTools.Field(t, "FakeMapBounds");
                    _fFogX = AccessTools.Field(t, "SurvFogPosX");
                    _fFogZ = AccessTools.Field(t, "SurvFogPosZ");
                }
                Component fog = _fFogObject.GetValue(__instance) as Component;
                Component fake = _fFogFake.GetValue(__instance) as Component;
                Transform mask = _fFogMask.GetValue(__instance) as Transform;
                if (fog == null || fake == null || mask == null) return;
                Vector3 p = fog.transform.position;
                float fx = (p.x - Extended.xMin) / Extended.width;
                float fz = (p.z - Extended.yMin) / Extended.height;
                // The mask is sized in map pixels; the wide window has fewer
                // of them per metre than the vanilla 803 px per 5 km.
                mask.localScale *= EastMapPanel.FogScale;
                _fFogX.SetValue(__instance, fx);
                _fFogZ.SetValue(__instance, fz);
                Bounds b = (Bounds)_fFogBounds.GetValue(__instance);
                mask.position = fake.transform.TransformPoint(new Vector3(
                    fx * b.size.x - b.extents.x, fz * b.size.y - b.extents.y, 0f));
            }
            catch (Exception ex)
            {
                Log("fog mask offset failed: " + ex.Message);
            }
        }

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastWorld: " + s); }
    }
}

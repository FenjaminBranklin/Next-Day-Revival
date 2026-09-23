using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    // Every road free: switches off each scene object and removes each terrain
    // tree that research/roadclear.py measured reaching onto a road surface -
    // any part of it over a road cell, between 0.1 m and 5 m above the road.
    // The list is generated (Revival.RoadClearData.cs); nothing is decided at
    // runtime, so every client clears exactly the same objects.
    //
    // Scene-only, like MapUnblock: nothing is written to an asset, a save or
    // the network, and OnDestroy puts everything back. Bridges, tunnels,
    // buildings and anything carrying a script or a PhotonView were refused
    // by the generator and are not in the list.
    internal sealed class RoadClear : MonoBehaviour
    {
        sealed class Entry
        {
            internal string Path;
            internal Vector3 Position;
        }

        /// <summary>scene name -> leaf object name -> entries with that leaf.</summary>
        readonly Dictionary<string, Dictionary<string, List<Entry>>> scenes =
            new Dictionary<string, Dictionary<string, List<Entry>>>();
        readonly Dictionary<string, int> wanted = new Dictionary<string, int>();
        readonly HashSet<Scene> running = new HashSet<Scene>();
        readonly List<GameObject> hidden = new List<GameObject>();

        /// <summary>TerrainData name -> quantised world x/z of the trees to take.</summary>
        readonly Dictionary<string, HashSet<long>> treeKeys = new Dictionary<string, HashSet<long>>();
        /// <summary>TerrainData instance id -> tree count after the last filter.
        /// Helipads.Restore appends trees back; a changed count filters again.</summary>
        readonly Dictionary<int, int> filtered = new Dictionary<int, int>();
        readonly Dictionary<int, TerrainData> filteredData = new Dictionary<int, TerrainData>();
        readonly Dictionary<int, List<TreeInstance>> takenTrees = new Dictionary<int, List<TreeInstance>>();

        ConfigEntry<bool> enabledConfig;
        ConfigEntry<bool> treesConfig;
        float nextCheck;

        internal static void Install(GameObject owner, ConfigFile config)
        {
            RoadClear instance = owner.AddComponent<RoadClear>();
            instance.enabledConfig = config.Bind("RoadClear", "Enabled", true,
                "Keep every road free: switch off each scene object that reaches onto a road surface "
                + "(the generated list in Revival.RoadClearData.cs). Requires scene reload after changes.");
            instance.treesConfig = config.Bind("RoadClear", "Trees", true,
                "Also remove the terrain trees whose trunk or crown reaches onto a road.");
            instance.Load();
            SceneManager.sceneLoaded += instance.Loaded;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                instance.Loaded(SceneManager.GetSceneAt(i), LoadSceneMode.Additive);
        }

        static float Parse(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }

        static long Key(int qx, int qz) { return ((long)qx << 32) ^ (uint)qz; }

        void Load()
        {
            foreach (string row in RoadClearData.Units)
            {
                string[] f = row.Split('\t');
                Dictionary<string, List<Entry>> byLeaf;
                if (!scenes.TryGetValue(f[0], out byLeaf))
                {
                    byLeaf = new Dictionary<string, List<Entry>>();
                    scenes.Add(f[0], byLeaf);
                    wanted.Add(f[0], 0);
                }
                Entry e = new Entry();
                e.Path = f[1];
                e.Position = new Vector3(Parse(f[2]), Parse(f[3]), Parse(f[4]));
                int slash = e.Path.LastIndexOf('/');
                string leaf = slash < 0 ? e.Path : e.Path.Substring(slash + 1);
                List<Entry> list;
                if (!byLeaf.TryGetValue(leaf, out list)) { list = new List<Entry>(); byLeaf.Add(leaf, list); }
                list.Add(e);
                wanted[f[0]]++;
            }
            int trees = 0;
            foreach (RoadClearData.TreeSet set in RoadClearData.Trees)
            {
                HashSet<long> keys;
                if (!treeKeys.TryGetValue(set.Terrain, out keys))
                { keys = new HashSet<long>(); treeKeys.Add(set.Terrain, keys); }
                for (int i = 0; i + 1 < set.XZ.Length; i += 2)
                {
                    keys.Add(Key(Mathf.RoundToInt(set.XZ[i] * 10f), Mathf.RoundToInt(set.XZ[i + 1] * 10f)));
                    trees++;
                }
            }
            RevivalPlugin.L.LogInfo("RoadClear: " + RoadClearData.Units.Length + " road objects in "
                + scenes.Count + " scenes, " + trees + " road trees on " + treeKeys.Count + " terrains.");
        }

        void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (!enabledConfig.Value || !scene.isLoaded) return;
            if (running.Add(scene)) StartCoroutine(Apply(scene));
        }

        static string PathOf(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        IEnumerator Apply(Scene scene)
        {
            // Scene callbacks include additive streaming and return visits.
            yield return null;
            if (!scene.isLoaded) { running.Remove(scene); yield break; }
            Dictionary<string, List<Entry>> byLeaf;
            if (scenes.TryGetValue(scene.name, out byLeaf))
            {
                int scanned = 0, gone = 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (++scanned % 512 == 0) yield return null;
                        if (t == null) continue;
                        List<Entry> list;
                        if (!byLeaf.TryGetValue(t.name, out list)) continue;
                        string path = null;
                        foreach (Entry e in list)
                        {
                            if ((e.Position - t.position).sqrMagnitude >= 0.0625f) continue;
                            if (path == null) path = PathOf(t);
                            if (path != e.Path) continue;
                            if (t.gameObject.activeSelf)
                            {
                                t.gameObject.SetActive(false);  // every LOD, collider and obstacle with it
                                hidden.Add(t.gameObject);
                                gone++;
                            }
                            break;
                        }
                    }
                }
                RevivalPlugin.L.LogInfo("RoadClear: " + scene.name + ": " + gone + "/"
                    + wanted[scene.name] + " road objects switched off.");
            }
            if (treesConfig.Value) FilterTrees();
            running.Remove(scene);
        }

        void Update()
        {
            if (!enabledConfig.Value || Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 5f;
            // Helipads.Restore switches its own clearing back on, and a road
            // object inside a pad site is on both lists.
            int again = 0;
            for (int i = hidden.Count - 1; i >= 0; i--)
            {
                GameObject go = hidden[i];
                if (go == null) { hidden.RemoveAt(i); continue; }
                if (go.activeSelf) { go.SetActive(false); again++; }
            }
            if (again > 0) RevivalPlugin.L.LogInfo("RoadClear: " + again + " road object(s) switched off again.");
            if (treesConfig.Value) FilterTrees();
        }

        /// <summary>Every TerrainData in the scene - the visible terrains and
        /// the collider-only ones that carry the trunks (RE 37).</summary>
        void FilterTrees()
        {
            if (treeKeys.Count == 0) return;
            try
            {
                Terrain[] active = Terrain.activeTerrains;
                if (active != null)
                    for (int i = 0; i < active.Length; i++)
                        if (active[i] != null)
                            Filter(active[i].terrainData, active[i].GetPosition(), active[i],
                                active[i].GetComponent<TerrainCollider>());
                TerrainCollider[] hulls = UnityEngine.Object.FindObjectsOfType<TerrainCollider>();
                for (int i = 0; i < hulls.Length; i++)
                    if (hulls[i] != null)
                        Filter(hulls[i].terrainData, hulls[i].transform.position, null, hulls[i]);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("RoadClear: trees: " + ex.Message);
            }
        }

        void Filter(TerrainData data, Vector3 org, Terrain terrain, TerrainCollider hull)
        {
            if (data == null) return;
            HashSet<long> keys;
            if (!treeKeys.TryGetValue(data.name, out keys)) return;
            int id = data.GetInstanceID();
            int known;
            if (filtered.TryGetValue(id, out known) && known == data.treeInstanceCount) return;
            TreeInstance[] trees = data.treeInstances;
            Vector3 size = data.size;
            List<TreeInstance> keep = new List<TreeInstance>(trees.Length);
            List<TreeInstance> taken;
            if (!takenTrees.TryGetValue(id, out taken)) { taken = new List<TreeInstance>(); takenTrees.Add(id, taken); }
            int removed = 0;
            for (int i = 0; i < trees.Length; i++)
            {
                float x = org.x + trees[i].position.x * size.x;
                float z = org.z + trees[i].position.z * size.z;
                int qx = Mathf.RoundToInt(x * 10f), qz = Mathf.RoundToInt(z * 10f);
                bool hit = false;
                for (int dx = -1; dx <= 1 && !hit; dx++)
                    for (int dz = -1; dz <= 1 && !hit; dz++)
                        hit = keys.Contains(Key(qx + dx, qz + dz));
                if (hit) { taken.Add(trees[i]); removed++; }
                else keep.Add(trees[i]);
            }
            if (removed > 0)
            {
                data.treeInstances = keep.ToArray();
                if (terrain != null) terrain.Flush();
                if (hull != null && hull.enabled) { hull.enabled = false; hull.enabled = true; }
                RevivalPlugin.L.LogInfo("RoadClear: " + removed + " road tree(s) removed from " + data.name + ".");
            }
            filtered[id] = data.treeInstanceCount;
            filteredData[id] = data;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= Loaded;
            foreach (GameObject go in hidden) if (go != null) go.SetActive(true);
            foreach (KeyValuePair<int, List<TreeInstance>> pair in takenTrees)
            {
                TerrainData data;
                if (pair.Value.Count == 0 || !filteredData.TryGetValue(pair.Key, out data) || data == null) continue;
                try
                {
                    TreeInstance[] now = data.treeInstances;
                    TreeInstance[] all = new TreeInstance[now.Length + pair.Value.Count];
                    now.CopyTo(all, 0);
                    pair.Value.CopyTo(all, now.Length);
                    data.treeInstances = all;
                }
                catch (Exception) { }
            }
        }
    }
}

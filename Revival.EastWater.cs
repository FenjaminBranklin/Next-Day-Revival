// Next Day: Survival - Revival Toolkit
//
// THE EAST TILE'S WATER. The tile's river and marsh surface (EastTileWater0..n,
// unity/EastTile/Tools/tile_water.py, BuildTile.cs MakeWater) is built like
// GW_Scene_1's Water_Swamp_01: layer 4, tag Water, a MeshCollider, the game's
// puddle_01_Big material. The one part a bundle cannot carry is the game's
// WaterObject script (Assembly-CSharp): PlayerFootstepsController and
// PlayerInteractingManager (drink, fill a bottle) look for it on the water
// they hit. Once the tile is up (EastWorld.Placed), every tile water object
// gets one, its fields (particle colours, water type, condition) copied from
// a vanilla WaterObject - a puddle_01_Big swamp when there is one, as the
// tile uses its material. Reflection only: the plugin does not reference
// Assembly-CSharp. Report: docs/ai/tasks/east-relief-river.md.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class EastWater
    {
        internal const string Prefix = "EastTileWater";

        internal static void Attach(Scene tile)
        {
            Type t = AccessTools.TypeByName("WaterObject");
            if (t == null) { Log("the game has no WaterObject type - tile water stays without it."); return; }
            UnityEngine.Object[] vanilla = UnityEngine.Object.FindObjectsOfType(t);
            Component src = null;
            foreach (UnityEngine.Object o in vanilla)
            {
                Component c = o as Component;
                if (c == null || c.gameObject.scene == tile) continue;
                if (src == null) src = c;
                Renderer r = c.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("puddle_01_Big")) { src = c; break; }
            }
            if (src == null) { Log("no vanilla WaterObject loaded - tile water stays without it."); return; }
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int n = 0;
            GameObject[] roots = tile.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                foreach (Transform tr in roots[i].GetComponentsInChildren<Transform>(true))
                {
                    if (!tr.name.StartsWith(Prefix) || tr.GetComponent(t) != null) continue;
                    Component w = tr.gameObject.AddComponent(t);
                    foreach (FieldInfo f in fields) f.SetValue(w, f.GetValue(src));
                    n++;
                }
            Log("WaterObject on " + n + " tile water object(s), copied from " + src.gameObject.name
                + " (" + fields.Length + " fields).");
        }

        static void Log(string s) { RevivalPlugin.L.LogInfo("EastWater: " + s); }
    }
}

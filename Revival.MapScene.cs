// Next Day: Survival - Revival Toolkit
//
// WHICH MAP IS ON SCREEN, AND WHICH MAP A PIECE OF AUTHORED DATA BELONGS TO.
//
// The game has more than one world map. Confirmed from IL and from the scene
// files (REVERSE_ENGINEERING.md 9, docs/ai/tasks/new-regions.md): the surface
// maps are the GameScene values GW_Scene_1 (5), GW_Scene_2 (6) and GW_Scene_3
// (7); Bunker_A65 (9), Catacombs (13) and Underground_Lab (14) are interiors.
// Each of them carries its OWN terrain, and MapUIManager.InitWorldSize fills the
// static WORLD_SIZE from the terrain of the scene that is loaded right now.
//
// THE BUG THIS EXISTS FOR. Everything this plugin authors - patrol and convoy
// routes, heli troop landings, the traitor camp - was recorded on ONE map, and
// nothing in the data said so. Patrol.DrawMap therefore painted the routes of
// map 1 onto whatever map was open, and the automatic went on spawning those
// patrols there. The only guard was Patrol.FitsScene, which compares the route's
// world EXTENT against WORLD_SIZE (docs/ai/tasks/patrol-map-scene-fix.md). That
// catches a bunker, whose terrain is tens of metres across, but two large
// surface maps have a similar WORLD_SIZE, so the ratio stays far under the 2.5x
// limit and the whole overlay of the starting region showed up in the next one.
// The user's report: "in Primorye you still see the Severoufimsk overlay,
// although those patrols do not exist in that region at all."
//
// THE FIX IS IDENTITY, NOT SIZE. Every authored item carries the NAME of the map
// it belongs to, and it is only drawn and only spawned while that map is the one
// that is loaded. Three rules keep it from ever making things worse - the risk
// worth guarding against is not "the overlay still shows in Primorye", it is
// "the overlay stopped showing in Severoufimsk":
//
//   1. NO TAG MEANS THE HOME MAP. Everything recorded before this existed was
//      recorded on GW_Scene_1, so an untagged route belongs there and nowhere
//      else. No file has to be rewritten for the bug to be gone.
//   2. AN UNRECOGNISED SCENE DRAWS EVERYTHING. The gate only bites while the
//      scene on screen is one of the names in the game's own GameScene enum
//      (KnownMaps). An empty name, or any name outside that list, means the
//      assumption behind this class does not hold here - and then every item is
//      drawn and spawned exactly as it was before, plus one line in the log.
//   3. ONE SWITCH BACK. [Map] RegionGate = false restores the old behaviour
//      without a build.
//
// The scene NAME is the identity because the game itself loads scenes by name
// (GameScenesLoadingManager.LoadScene does gameScene.ToString()), MapUnblock
// already keys its data by scene.name, and it survives an enum that is not
// consecutive. The player-facing region label (Severoufimsk, and whatever the
// other two are called in the room settings) is presentation and lives in the
// editor's map table (assets/editor/maps.json), never in the runtime data.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII only.

using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    /// <summary>
    /// The map that is loaded right now, and the test "does this map own that
    /// piece of authored data". See the file header for why identity and not
    /// size decides it.
    /// </summary>
    internal static class MapScene
    {
        /// <summary>The map every route, landing and settlement in this
        /// repository was authored on, and therefore what an untagged item
        /// means. GameScene 5.</summary>
        internal const string Home = "GW_Scene_1";

        /// <summary>The tag that asks for an item on EVERY map. Never written
        /// by the editor; an admin can put it in a file by hand when a route
        /// really is meant to exist everywhere.</summary>
        internal const string Any = "*";

        /// <summary>At most this many characters of a tag are kept. A tag rides
        /// in a comma-separated flag list and in a TSV column, so it also may
        /// not contain a comma, a tab or whitespace.</summary>
        const int MaxTag = 48;

        /// <summary>
        /// EVERY scene the game can load, read out of the `GameScene` enum in
        /// Assembly-CSharp.dll (REVERSE_ENGINEERING.md 9). This list is the
        /// SAFETY NET, and it is why this class cannot take the overlay away by
        /// accident: the region gate only bites while the scene on screen is one
        /// of these. If Unity ever hands back a name that is not here, the
        /// assumption that scenes are named like the enum is wrong, and every
        /// item is drawn and spawned exactly as it was before this file existed.
        /// </summary>
        static readonly string[] KnownMaps = new string[] {
            "GW_Scene_1", "GW_Scene_2", "GW_Scene_3", "GW_Scene_4",
            "Bunker_A65", "Catacombs", "Underground_Lab", "Training",
            "GL_Scene", "GL_Scene_DEV", "DEV_Scene", "DEV_Scene_Vasya",
            "DEV_Scene_Mitya", "DEV_Scene_Vasya_2", "DEV_Scene_Vasya_3"
        };

        static ConfigEntry<bool> _cfgEnabled;

        static string _name = "";
        static int _frame = -1;
        static string _warned = "";
        static string _announced = "";

        /// <summary>
        /// One switch for the whole region gate, so an admin who ever sees the
        /// overlay missing where it belongs can put it back without a build.
        /// </summary>
        internal static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("Map", "RegionGate", true,
                "Zeigt und startet Routen, Desants und die Verraetersiedlung nur "
                + "auf DER Karte, zu der sie gehoeren. Aus = altes Verhalten: "
                + "alles wird auf jeder Karte gezeichnet, auch in Regionen, in "
                + "denen es gar nicht existiert.");
        }

        static bool Gate
        {
            get { return _cfgEnabled == null || _cfgEnabled.Value; }
        }

        /// <summary>Is the scene on screen one this plugin can tell apart from
        /// the others? See KnownMaps.</summary>
        static bool Recognised(string name)
        {
            for (int i = 0; i < KnownMaps.Length; i++)
                if (string.Equals(KnownMaps[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// The name of the active scene, cached for the frame. Read once per
        /// frame because DrawMap asks it inside two loops over every route.
        /// A loading screen does NOT change the answer: the last real map is
        /// kept, so the overlay does not flicker while a region change is in
        /// flight.
        /// </summary>
        internal static string Current
        {
            get
            {
                int frame = Time.frameCount;
                if (_frame == frame) return _name;
                _frame = frame;
                _name = Read();
                return _name;
            }
        }

        static string Read()
        {
            try
            {
                Scene scene = SceneManager.GetActiveScene();
                string name = scene.name == null ? "" : scene.name.Trim();
                if (name.Length == 0) return _name;

                // The overworld streams GW_Scene_1_Chunk_N scenes in beside
                // itself. Should one of them ever become the active scene, it is
                // still the same map and must not cull the map's own overlay.
                int chunk = name.IndexOf("_Chunk", StringComparison.OrdinalIgnoreCase);
                if (chunk > 0) name = name.Substring(0, chunk);

                // Neither of these is a map. Keeping the previous answer is what
                // makes the overlay survive a loading screen.
                if (name == "LoadingScene" || name == "SplashScene"
                    || name == "Empty") return _name;
                return Announce(name);
            }
            catch { return _name; }
        }

        /// <summary>
        /// One log line per map change, and only when the map really changes.
        ///
        /// It is the answer to the question this whole class raises for an
        /// admin - "which scene is the region I am standing in called?" - which
        /// is what has to be written into assets/editor/maps.json for the
        /// browser editor to author for it. The F4 window says the same thing,
        /// but the log is what gets read after the session, and it is also the
        /// place to look when the overlay is not where it is expected.
        /// </summary>
        static string Announce(string name)
        {
            if (name == _announced) return name;
            _announced = name;
            if (RevivalPlugin.L != null)
                RevivalPlugin.L.LogInfo("MapScene: the map on screen is \"" + name
                    + "\"" + (string.Equals(name, Home, StringComparison.OrdinalIgnoreCase)
                              ? " (the home map)."
                              : " - only routes, landings and settlements tagged"
                                + " for it are drawn and spawned here."));
            return name;
        }

        /// <summary>
        /// Does the map on screen own data tagged <paramref name="tag"/>?
        ///
        /// An empty tag means the home map - everything authored before maps
        /// were tagged was authored there. An unknown current scene answers
        /// true, so a missing scene name can never blank a working overlay.
        /// </summary>
        internal static bool Owns(string tag)
        {
            if (!Gate) return true;
            string want = tag == null ? "" : tag.Trim();
            if (want.Length == 0) want = Home;
            if (want == Any) return true;
            string here = Current;
            if (here.Length == 0) return true;
            if (!Recognised(here))
            {
                // Said once per scene name, never once a frame. This is the one
                // state in which the gate is off on purpose, and it should be
                // readable in the log rather than silent.
                if (_warned != here)
                {
                    _warned = here;
                    if (RevivalPlugin.L != null)
                        RevivalPlugin.L.LogWarning("MapScene: scene \"" + here
                            + "\" is not one of the game's known maps - every "
                            + "route, landing and settlement is drawn and "
                            + "spawned here, as before the region gate existed.");
                }
                return true;
            }
            return string.Equals(here, want, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True while the home map is the one that is loaded.</summary>
        internal static bool AtHome { get { return Owns(Home); } }

        /// <summary>
        /// A tag as it may be stored: trimmed, without the separators of the
        /// two file formats it travels in, and length-capped. An empty result
        /// means "the home map", which is exactly what an absent tag means, so
        /// a damaged tag degrades to the old behaviour instead of to nothing.
        /// </summary>
        internal static string Clean(string tag)
        {
            if (tag == null) return "";
            string t = tag.Trim();
            if (t.Length == 0) return "";
            if (t == Any) return Any;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(t.Length);
            for (int i = 0; i < t.Length && sb.Length < MaxTag; i++)
            {
                char c = t[i];
                if (c == ',' || c == '\t' || c == '=' || char.IsWhiteSpace(c)) continue;
                if (c < 32 || c > 126) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The tag to STORE for a map: an empty string for the home
        /// map, so a file authored on it keeps the shape it has today.</summary>
        internal static string Store(string tag)
        {
            string t = Clean(tag);
            return string.Equals(t, Home, StringComparison.OrdinalIgnoreCase) ? "" : t;
        }

        /// <summary>What to show a person: the tag, or the home map's name when
        /// there is none.</summary>
        internal static string Label(string tag)
        {
            string t = Clean(tag);
            return t.Length == 0 ? Home : t;
        }
    }
}

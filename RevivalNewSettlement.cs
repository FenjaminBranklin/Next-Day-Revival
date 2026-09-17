// Next Day: Survival - Revival Toolkit
//
// THE TRAITOR SETTLEMENT AT LITVINOVKA. A new, permanent camp of armed traitors
// in the village of Litvinovka, at the bottom left of the world map, plus an
// orange ring around it on the in-game map so the place reads before anybody
// walks there.
//
// WHY THIS FILE WAS REWRITTEN (field report on 6.18.0: "no ring on the map, no
// NPCs anywhere"). The first version only ever did anything when the player
// pressed F12, and even then it could not work where it was pointed:
//
//   1. IT NEEDED A KEY. Nothing happened on its own, so a player who never
//      presses the key sees an empty map and an empty village - which is
//      exactly what was reported. The camp now stands itself up as soon as the
//      master client has a world, and puts itself back up when it is gone.
//   2. IT ASKED A RAYCAST FOR THE GROUND. Away from every player the whole-map
//      TerrainColliders are off (E-059), so the downward ray at Litvinovka -
//      three kilometres from the usual spawn - found nothing and the spawn was
//      skipped with a warning. Height data needs no collider: the ground now
//      comes from RevivalTroopInsertion.GroundY, which falls back to
//      Terrain.SampleHeight, the same fix the heli landings needed in 6.17.1.
//   3. THE RING WAS DRAWN ONLY WHILE THE SPAWNED OBJECT EXISTED. That object
//      only ever exists on the master client, so on a joined client the ring
//      could never appear at all. The ring marks a PLACE, so it is now drawn
//      from the configured coordinate on every client whenever the map is open.
//      Its shape uses the native TargetArea texture: the same alpha mask as
//      TargetAreaRed, tinted orange. NGUI supplies the original map edge fade.
//   4. ITS CONFIG KEYS WERE ALREADY IN EVERY INSTALLED nextday.revival.toolkit.cfg
//      with the old coordinate (-1500, -1500) and count 4. Config.Bind takes the
//      value out of that file and ignores the default in the code (CLAUDE.md,
//      point 4), so new numbers under the old key names would have reached
//      nobody. The section is therefore called [TraitorSettlement] now; the old
//      [NewSettlement] block is inert and may be deleted by hand.
//
// WHERE LITVINOVKA IS. Measured offline, no game start. assets/editor/basemap.png
// IS the game's map artwork (roadnet_sample.json names its source:
// sharedassets3.assets pathId 247, GW_Scene_1[RU]), 1024 px square over world
// -2500..2500 on both axes with no offset, read by editorpreview.py as
// x -> (x + 2500) / 5000 * w and z -> (2500 - z) / 5000 * h. Three readings:
//
//   1. THE ARTWORK. The Cyrillic LITVINOVKA label and the houses under it sit
//      at pixel (190, 822) of 1024 -> world (-1572, -1515).
//   2. THE ROAD. assets/editor/roadnet_sample.json dirt edge 41 (a=23, b=18,
//      302.3 m) runs through the village. It is a POLYLINE, not a straight line
//      between its end nodes 23 (-1434.33, -1458.74) and 18 (-1614.99,
//      -1636.96); one of its samples is (-1579.396, -1508.058) at height 496.0.
//   3. THE TERRAIN, which is the strongest of the three. Along that polyline the
//      height is EXACTLY 496.0 for every sample from (-1529.8, -1498.8) to
//      (-1614.3, -1571.0), and varies continuously on both sides of that run.
//      Terrain flattened to the centimetre over 110 m is a BUILT PAD - the
//      village itself.
//
// The default below sits on that pad, 7.9 m from the road sample in (2).
//
// HOW THE MEN ARE MADE. Crew.DropSquad - the same native NPC_Settlement,
// spawn-point and walk-point machinery the game uses for its own settlements and
// that this plugin already uses for wreck crews and heli squads. StartMainInit
// runs PhotonNetwork.InstantiateSceneObject, so every client sees the men, and
// Crew's AutoDisableControl exemption keeps an NDR_PatrolCrew settlement
// animated and thinking at any distance from a player (EXPERIMENTS, the E-037
// chain) - which is what makes a camp nobody is standing next to worth having.
//
// SIX MEN PER GROUP, NEVER MORE. Above eight, Crew.Absetzen switches to the
// transport exit formation and the firing sectors of a fifteen-seat truck. A
// settlement wants neither, so a bigger camp is several small groups spread over
// the village instead - which also looks more like a village than one huddle.
//
// TRAITORS DO NOT SHOOT EACH OTHER HERE. Fraktion writes the user's rule for a
// patrol route: "traitor" hates all eight sides, ITSELF INCLUDED. For a camp of
// traitors that rule is suicide, so after the men exist every Traitor entry in
// this settlement's own HatedFractions array is replaced in place by a side it
// already hates. MyFraction stays Traitor, so the men are still traitors to the
// player, to every other faction and to the game's own map marker colours.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// Player-facing strings go through Loc.T; this file is UTF-8 without BOM.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    /// <summary>
    /// The traitor camp at Litvinovka: a standing settlement of armed NPCs and
    /// the orange ring that marks it on the map. The class name is unchanged
    /// since 6.18.0 because RevivalPlugin.cs calls BindConfig, Tick and Draw by
    /// it; nothing else of the first version survived.
    /// </summary>
    public static class NewSettlement
    {
        // =============================================================== config

        static ConfigEntry<bool> _cfgEnabled;
        static ConfigEntry<int> _cfgCount;
        static ConfigEntry<float> _cfgX;
        static ConfigEntry<float> _cfgZ;
        static ConfigEntry<float> _cfgSpread;
        static ConfigEntry<float> _cfgMapRadius;
        static ConfigEntry<float> _cfgRespawnMinutes;
        static ConfigEntry<KeyCode> _cfgKey;

        /// <summary>The camp's centre, on the dirt road through Litvinovka.
        /// See the file header for how it was measured.</summary>
        const float DefaultX = -1575f;
        const float DefaultZ = -1515f;

        const string Faction = "traitor";

        /// <summary>Never more than eight per DropSquad - see the file
        /// header.</summary>
        const int GroupSize = 6;

        /// <summary>How long to wait before trying again after a failed or
        /// impossible spawn. Long enough that a wrong coordinate does not fill
        /// the log, short enough that a world which finished loading late still
        /// gets its camp within half a minute.</summary>
        const float RetrySeconds = 30f;

        /// <summary>How often the camp is counted once it stands.</summary>
        const float WatchSeconds = 20f;

        /// <summary>How long a world has to have been up before the first
        /// group is built. The terrain, the NavMesh and the map's own
        /// settlements are all worth waiting for; see NativeSettlement
        /// below for the one that would otherwise cost more than this
        /// feature.</summary>
        const float SettleSeconds = 20f;

        // ================================================================ state

        static readonly List<GameObject> _camp = new List<GameObject>();
        static float _nextTick;
        static float _nextTry;
        static float _nextWatch;
        static float _emptySince;
        static float _worldSince;
        static bool _announced;
        static string _lastFail = "";
        static Texture _ringStamp;
        static MethodInfo _isAlive;
        static bool _isAliveLooked;

        // Preserve the settlement colour while using the native ring silhouette.
        static readonly Color RingColor = new Color(1f, 0.55f, 0f, 0.95f);

        // ============================================================== binding

        public static void BindConfig(ConfigFile cfg)
        {
            _cfgEnabled = cfg.Bind("TraitorSettlement", "Enabled", true,
                "Verraeter-Siedlung in Litwinowka (unten links auf der Karte): "
                + "bewaffnete NPCs, die dort dauerhaft herumlaufen, und ein "
                + "oranger Ring um den Ort auf der Karte. Der Master-Client "
                + "stellt sie selbst auf, sobald die Welt da ist.");
            _cfgCount = cfg.Bind("TraitorSettlement", "Count", 12,
                "Wie viele bewaffnete Verraeter dort leben (1..24), etwa so "
                + "viele wie in den anderen Siedlungen. Sie entstehen in "
                + "Gruppen zu sechs, ueber den Ort verteilt.");
            _cfgX = cfg.Bind("TraitorSettlement", "X", DefaultX,
                "Welt-X der Siedlungsmitte. Die Welt reicht von -2500 bis 2500; "
                + "die Vorgabe liegt auf dem Feldweg durch Litwinowka.");
            _cfgZ = cfg.Bind("TraitorSettlement", "Z", DefaultZ,
                "Welt-Z der Siedlungsmitte.");
            _cfgSpread = cfg.Bind("TraitorSettlement", "Spread", 45f,
                "Abstand der Gruppen von der Mitte in Metern (0..400). Klein "
                + "genug, dass es ein Ort bleibt, gross genug, dass es kein "
                + "Haufen ist.");
            _cfgMapRadius = cfg.Bind("TraitorSettlement", "MapRingRadius", 200f,
                "Radius des orangen Rings auf der Karte in Weltmetern "
                + "(20..1200). Der Ring markiert den Ort, nicht den Weg der "
                + "einzelnen NPCs.");
            _cfgRespawnMinutes = cfg.Bind("TraitorSettlement", "RespawnMinutes", 20f,
                "Ist die ganze Siedlung tot, steht sie nach so vielen Minuten "
                + "wieder. 0 = nie wieder.");
            _cfgKey = cfg.Bind("TraitorSettlement", "RebuildKey", KeyCode.None,
                "Taste fuer den Test: raeumt die Siedlung ab und baut sie sofort "
                + "neu auf. None = aus. F12 ist die Steam-Screenshot-Taste und "
                + "deshalb eine schlechte Wahl.");
        }

        // ================================================================ frame

        public static void Tick()
        {
            if (_cfgEnabled == null) return;
            try
            {
                if (!_cfgEnabled.Value)
                {
                    if (_camp.Count > 0) Remove();
                    return;
                }

                // GetKeyDown is true for a single frame, so the key is read
                // before the once-a-second gate below and never inside it.
                if (_cfgKey != null && _cfgKey.Value != KeyCode.None
                    && Input.GetKeyDown(_cfgKey.Value))
                {
                    RevivalPlugin.L.LogInfo("TraitorSettlement: rebuild on key "
                        + _cfgKey.Value + ".");
                    Remove();
                    _nextTry = 0f;
                    _lastFail = "";
                }

                if (Time.time < _nextTick) return;
                _nextTick = Time.time + 1f;

                // A scene change destroys our GameObjects; Crew.StopAll (shift
                // plus the patrol key) does the same on purpose. Either way the
                // list empties here and the camp is built again below.
                Prune();

                if (!RevivalTroopInsertion.MasterClient()) return;
                // No local player means no world yet, or one being left.
                if (MapTools.LocalPlayer() == null) { _worldSince = 0f; return; }
                if (_worldSince <= 0f) _worldSince = Time.time;

                // One group per second, never all of them in one frame: twelve
                // NPCs built at once is a visible hitch, and a world that has
                // just finished loading has better things to do.
                int groups = Groups();
                if (_camp.Count < groups)
                {
                    if (Time.time < _worldSince + SettleSeconds) return;
                    if (Time.time >= _nextTry) SpawnGroup(groups);
                    return;
                }

                if (Time.time < _nextWatch) return;
                _nextWatch = Time.time + WatchSeconds;
                Watch();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TraitorSettlement tick: " + ex);
            }
        }

        static void Prune()
        {
            for (int i = _camp.Count - 1; i >= 0; i--)
                if (_camp[i] == null) _camp.RemoveAt(i);
        }

        // ================================================================ spawn

        static int Want()
        {
            return Mathf.Clamp(_cfgCount == null ? 12 : _cfgCount.Value, 1, 24);
        }

        static int Groups()
        {
            return (Want() + GroupSize - 1) / GroupSize;
        }

        /// <summary>Build the next missing group, or say why it cannot be built
        /// yet. The camp is complete when it holds Groups() of them.</summary>
        static void SpawnGroup(int groups)
        {
            Vector3 centre = Centre();
            float y;
            if (!RevivalTroopInsertion.GroundY(centre, out y))
            {
                // Both the ray AND the height data said no. On the world map
                // that means a wrong coordinate; inside a bunker or a lab scene
                // it simply means this place is not in this scene, which is the
                // gate that keeps the camp out of an interior.
                Fail("no ground at (" + centre.x.ToString("0") + ", "
                    + centre.z.ToString("0") + ") - either another scene is "
                    + "loaded or TraitorSettlement.X/Z point off the terrain");
                _nextTry = Time.time + RetrySeconds;
                return;
            }

            if (!NativeSettlement())
            {
                Fail("the map's own settlements are not in the scene yet - "
                    + "waiting, so Crew's one-shot template search does not "
                    + "cache a miss for the whole session");
                _nextTry = Time.time + RetrySeconds;
                return;
            }

            int index = _camp.Count;
            int n = Mathf.Min(GroupSize, Want() - index * GroupSize);
            if (n <= 0) return;

            float spread = Mathf.Clamp(_cfgSpread == null ? 45f : _cfgSpread.Value,
                                       0f, 400f);
            Vector3 at = centre;
            if (groups > 1 && spread > 0f)
            {
                float a = index * Mathf.PI * 2f / groups;
                at = centre + new Vector3(Mathf.Cos(a) * spread, 0f,
                                          Mathf.Sin(a) * spread);
            }
            float gy;
            if (!RevivalTroopInsertion.GroundY(at, out gy)) gy = y;
            at = new Vector3(at.x, gy, at.z);

            GameObject settlement = Crew.DropSquad(at,
                UnityEngine.Random.Range(0f, 360f), n, Faction, null);
            if (settlement == null)
            {
                Fail("Crew.DropSquad built nobody - the Crew lines above say why");
                _nextTry = Time.time + RetrySeconds;
                return;
            }

            Unhate(settlement);
            // THE CAMP IS A PLACE, SO IT GETS A BATTERY. Mortar's scan skips
            // every NDR_ object, which is what keeps a wreck crew from growing
            // a howitzer of its own - and it swept this camp up with them, so
            // the traitor settlement was the one settlement on the map with no
            // gun, no crew at it and no drone over it (field report
            // 2026-09-17). Exactly ONE group is marked, or the camp would stand
            // up two howitzers ninety metres apart; the first is the one nearest
            // the coordinate the admin wrote down.
            if (_camp.Count == 0) settlement.AddComponent<MortarSite>();
            _camp.Add(settlement);
            _lastFail = "";
            _emptySince = 0f;
            _nextTry = 0f;          // the next group may follow on the next tick
            RevivalPlugin.L.LogInfo("TraitorSettlement: group " + _camp.Count
                + " of " + groups + ", " + n + " armed traitor(s) at "
                + at.ToString("0") + ".");

            if (_camp.Count < groups) return;
            _nextWatch = Time.time + WatchSeconds;
            RevivalPlugin.L.LogInfo("TraitorSettlement: " + Want()
                + " armed traitor(s) settled at Litvinovka "
                + centre.ToString("0") + " in " + groups + " group(s).");
            if (!_announced)
            {
                _announced = true;
                Turret.Hinweis(Loc.T(
                    "Литвиновка занята предателями.",
                    "Traitors have taken Litvinovka."), 5f);
            }
        }

        /// <summary>
        /// Is at least one of the map's OWN settlements in the scene?
        ///
        /// Crew.Suchen looks for a template settlement exactly once per session
        /// and remembers the answer, miss included. This camp is the earliest
        /// thing in the plugin that ever asks for one, so if it asked before the
        /// world's settlements existed, every wreck crew and every heli squad
        /// for the rest of the session would run on hand-written numbers
        /// instead of the designers'. One FindObjectsOfType per spawn attempt -
        /// at most one a session in practice - buys that away.
        /// </summary>
        static bool NativeSettlement()
        {
            try
            {
                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                if (sType == null) return false;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(sType);
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c == null || c.gameObject == null) continue;
                    if (c.gameObject.name == Crew.Name) continue;   // one of ours
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorSettlement: settlement search - "
                    + ex.Message);
                return true;    // never block the camp on a broken check
            }
        }

        static void Fail(string why)
        {
            if (why == _lastFail) return;     // once, not every retry
            _lastFail = why;
            RevivalPlugin.L.LogWarning("TraitorSettlement: " + why + ".");
        }

        static Vector3 Centre()
        {
            float x = _cfgX == null ? DefaultX : _cfgX.Value;
            float z = _cfgZ == null ? DefaultZ : _cfgZ.Value;
            return new Vector3(x, 0f, z);
        }

        // ============================================================== upkeep

        /// <summary>Count what is left, and put the camp back up once the last
        /// traitor has been dead for RespawnMinutes. A settlement that stays
        /// empty forever after one raid is not a settlement.</summary>
        static void Watch()
        {
            int alive = Alive();
            if (alive > 0) { _emptySince = 0f; return; }

            float minutes = _cfgRespawnMinutes == null ? 0f : _cfgRespawnMinutes.Value;
            if (minutes <= 0f) return;
            if (_emptySince <= 0f)
            {
                _emptySince = Time.time;
                RevivalPlugin.L.LogInfo("TraitorSettlement: wiped out - back in "
                    + minutes.ToString("0") + " min.");
                return;
            }
            if (Time.time - _emptySince < minutes * 60f) return;
            _emptySince = 0f;
            Remove();
            _nextTry = 0f;
        }

        static int Alive()
        {
            int n = 0;
            MethodInfo alive = IsAlive();
            for (int i = 0; i < _camp.Count; i++)
            {
                Array men = Crew.Men(_camp[i]);
                if (men == null) continue;
                for (int k = 0; k < men.Length; k++)
                {
                    Component ai = men.GetValue(k) as Component;
                    if (ai == null) continue;
                    // Cannot tell -> assume alive. A missing IsAlive must never
                    // turn into a camp that rebuilds itself every 20 seconds.
                    if (alive == null) { n++; continue; }
                    try
                    {
                        object r = alive.Invoke(ai, null);
                        if (!(r is bool) || (bool)r) n++;
                    }
                    catch { n++; }
                }
            }
            return n;
        }

        static MethodInfo IsAlive()
        {
            if (_isAliveLooked) return _isAlive;
            _isAliveLooked = true;
            try
            {
                Type npc = RevivalPlugin.TypeByName("NPC_AI2");
                if (npc != null) _isAlive = AccessTools.Method(npc, "IsAlive", null, null);
                if (_isAlive == null)
                    RevivalPlugin.L.LogWarning("TraitorSettlement: NPC_AI2.IsAlive "
                        + "not found - the camp will never notice it was wiped out.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorSettlement: NPC_AI2.IsAlive - "
                    + ex.Message);
            }
            return _isAlive;
        }

        static void Remove()
        {
            int n = _camp.Count;
            for (int i = 0; i < _camp.Count; i++)
            {
                GameObject go = _camp[i];
                if (go == null) continue;
                Crew.Forget(go);
                Mortar.SiteGone(go);        // NDR settlement artillery
                UnityEngine.Object.Destroy(go);
            }
            _camp.Clear();
            _emptySince = 0f;
            if (n > 0) RevivalPlugin.L.LogInfo("TraitorSettlement: " + n
                + " group(s) taken off the map.");
        }

        // =========================================================== factions

        /// <summary>
        /// Take Traitor out of this settlement's own hated list - see the file
        /// header. The array is edited IN PLACE, and every Traitor entry is
        /// overwritten with a side the settlement already hates, so the array
        /// object and its length never change: SetNpcParams hands the same
        /// NPCMainOptions to every man, and an array that is still the same
        /// object fixes all of them at once however it was passed on. Each NPC's
        /// own MainOptions is walked as well, in case it is a copy.
        /// </summary>
        static void Unhate(GameObject settlement)
        {
            try
            {
                Type f = RevivalPlugin.TypeByName("Fraction");
                if (f == null || !f.IsEnum) return;
                object traitor, instead;
                try
                {
                    traitor = Enum.Parse(f, "Traitor", true);
                    instead = Enum.Parse(f, "Marauder", true);
                }
                catch { return; }

                Type sType = RevivalPlugin.TypeByName("NPC_Settlement");
                Component sied = sType == null ? null
                    : settlement.GetComponent(sType);
                int fixedUp = Scrub(Read(sied, "FractionOptions"), traitor, instead);

                Array men = Crew.Men(settlement);
                if (men != null)
                    for (int i = 0; i < men.Length; i++)
                    {
                        Component ai = men.GetValue(i) as Component;
                        if (ai == null) continue;
                        fixedUp += Scrub(Read(ai, "MainOptions"), traitor, instead);
                    }
                if (fixedUp > 0)
                    RevivalPlugin.L.LogInfo("TraitorSettlement: " + fixedUp
                        + " Traitor entr(y/ies) removed from the camp's own hated "
                        + "list - these men do not shoot each other.");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("TraitorSettlement: hated list - "
                    + ex.Message);
            }
        }

        static object Read(object owner, string field)
        {
            if (owner == null) return null;
            FieldInfo fi = AccessTools.Field(owner.GetType(), field);
            return fi == null ? null : fi.GetValue(owner);
        }

        static int Scrub(object options, object traitor, object instead)
        {
            if (options == null) return 0;
            Array hated = Read(options, "HatedFractions") as Array;
            if (hated == null) return 0;
            int n = 0;
            for (int i = 0; i < hated.Length; i++)
            {
                object v = hated.GetValue(i);
                if (v == null || !v.Equals(traitor)) continue;
                hated.SetValue(instead, i);
                n++;
            }
            return n;
        }

        // ================================================================= map

        // The map picture is not perfectly registered to the terrain, and
        // Patrol.DrawMap already moves every route and every label by the same
        // measured amount so a world-true point lands on the picture's own road.
        // The ring uses the identical correction, so it sits on the picture's
        // Litvinovka rather than a few pixels beside it. DISPLAY ONLY.
        const float MapArtFit = 1024f;
        const float MapArtScale = 1.005f;
        const float MapArtShiftX = 2f;
        const float MapArtShiftY = -4f;

        // Reserve before Patrol places names, including the first map frame.
        internal static void ReserveMapLabels(MapLabels labels, Component texture,
            Camera camera, Vector2 world, Vector2 map, Rect full)
        {
            if (_cfgEnabled == null || !_cfgEnabled.Value) return;
            Vector3 centre = Centre();
            if (Mathf.Abs(centre.x) > world.x || Mathf.Abs(centre.z) > world.y) return;
            float radius = Mathf.Clamp(_cfgMapRadius == null ? 200f : _cfgMapRadius.Value, 20f, 1200f);
            Vector2 mid, east, north;
            if (!Project(centre, texture, camera, world, map, full, out mid)
                || !Project(centre + new Vector3(radius, 0f, 0f), texture, camera, world, map, full, out east)
                || !Project(centre + new Vector3(0f, 0f, radius), texture, camera, world, map, full, out north)) return;
            float rx = Mathf.Max(2f, Mathf.Abs(east.x - mid.x));
            float ry = Mathf.Max(2f, Mathf.Abs(north.y - mid.y));
            labels.BlockScreen(new Rect(mid.x - rx, mid.y - ry, rx * 2f, ry * 2f));
        }

        /// <summary>The orange ring, drawn from the CONFIGURED place on every
        /// client whenever the world map is open - it marks a location, so it
        /// must not depend on objects that only the master client owns.</summary>
        public static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (_cfgEnabled == null || !_cfgEnabled.Value)
            { MapInkLayer.Hide("settlement"); return; }
            MapInkLayer layer = null;
            try
            {
                Component manager, texture;
                Camera camera;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera,
                                      out world, out map)) return;

                Vector3 centre = Centre();
                // An interior scene (a bunker, the lab) has its own, far smaller
                // map, and Litvinovka must not be painted onto it. The map is a
                // pure scale of that scene's WORLD_SIZE, so a point a whole world
                // outside it belongs to another scene. Deliberately generous:
                // this gate must never hide the ring on the real map, and the
                // clip rectangle below discards anything off the picture anyway.
                if (Mathf.Abs(centre.x) > world.x || Mathf.Abs(centre.z) > world.y)
                    return;

                Rect full;
                if (!MapTools.MapScreenRect(texture, camera, out full)) return;
                Rect clip = full;
                Rect view;
                if (MapTools.MapViewportRect(texture, camera, out view))
                    clip = Intersect(clip, view);
                if (clip.width < 2f || clip.height < 2f) return;

                float radius = Mathf.Clamp(
                    _cfgMapRadius == null ? 200f : _cfgMapRadius.Value, 20f, 1200f);
                Vector2 mid, east, north;
                if (!Project(centre, texture, camera, world, map, full, out mid)) return;
                if (!Project(centre + new Vector3(radius, 0f, 0f), texture, camera,
                             world, map, full, out east)) return;
                if (!Project(centre + new Vector3(0f, 0f, radius), texture, camera,
                             world, map, full, out north)) return;

                float rx = Mathf.Max(2f, Mathf.Abs(east.x - mid.x));
                float ry = Mathf.Max(2f, Mathf.Abs(north.y - mid.y));

                if (_ringStamp == null) _ringStamp = MapInkLayer.SettlementStamp();
                if (_ringStamp == null) return;
                layer = MapInkLayer.Begin("settlement", texture);
                if (layer == null) return;
                Rect ring = new Rect((mid.x - rx - full.x) * 1024f / full.width,
                    (mid.y - ry - full.y) * 1024f / full.height,
                    rx * 2f * 1024f / full.width, ry * 2f * 1024f / full.height);
                layer.Draw(ring, _ringStamp, RingColor);

                // The map already names the village. Show faction details only
                // on hover, like the patrol notes, instead of permanent IMGUI ink
                // that would remain opaque while the native ring fades away.
                Vector2 mouse = Event.current.mousePosition;
                Vector2 delta = mouse - mid;
                if (clip.Contains(mouse) && delta.x * delta.x / (rx * rx)
                    + delta.y * delta.y / (ry * ry) <= 1f)
                {
                    Color old = GUI.color;
                    try
                    {
                        GUI.color = RingColor;
                        GUI.Label(new Rect(mouse.x + 12f, mouse.y - 11f, 240f, 22f),
                            Loc.T("\u041b\u0438\u0442\u0432\u0438\u043d\u043e\u0432\u043a\u0430 - \u043f\u0440\u0435\u0434\u0430\u0442\u0435\u043b\u0438",
                                  "Litvinovka - traitors"));
                    }
                    finally { GUI.color = old; }
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null)
                    RevivalPlugin.L.LogWarning("TraitorSettlement map: " + ex.Message);
            }
            finally
            {
                if (layer != null) layer.End();
                else MapInkLayer.Hide("settlement");
            }
        }

        static bool Project(Vector3 point, Component texture, Camera camera,
                            Vector2 world, Vector2 map, Rect full, out Vector2 gui)
        {
            if (!MapTools.WorldToGui(point, texture, camera, world, map, out gui))
                return false;
            gui = MapArt(gui, full);
            return true;
        }

        static Vector2 MapArt(Vector2 g, Rect full)
        {
            if (full.width < 1f) return g;
            float factor = full.width / MapArtFit;
            float cx = full.x + full.width * 0.5f;
            float cy = full.y + full.height * 0.5f;
            return new Vector2((g.x - cx) * MapArtScale + cx + MapArtShiftX * factor,
                               (g.y - cy) * MapArtScale + cy + MapArtShiftY * factor);
        }

        static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.x, b.x), y0 = Mathf.Max(a.y, b.y);
            float x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return new Rect(x0, y0, Mathf.Max(0f, x1 - x0), Mathf.Max(0f, y1 - y0));
        }

    }
}

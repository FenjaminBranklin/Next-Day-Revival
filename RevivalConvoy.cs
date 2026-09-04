// Next Day: Survival - Revival Toolkit
//
// Convoy event - the whole system in one file (Phase 4 + Phase 5 of the
// patrol plan, docs/ai/tasks/npc-vehicle-patrols.md section 11).
//
// WHAT IT IS. Like the game's humanitarian-aid event: from time to time (every
// CfgIntervalMin..Max hours) a military convoy sets out on a route the admin
// marked on the map, and a push line drops in the bottom-left corner -
// "Convoy sighted in square F4!" - so players know where to intercept it. A
// convoy is a COLUMN of 2 tanks and 2 APCs: a tank at the front, a tank at the
// tail, the two APCs between them, each vehicle held a set distance behind the
// one ahead. Every vehicle carries about double a patrol's trunk loot. When a
// convoy vehicle is destroyed it burns where it dies and the wreck does NOT
// despawn on the usual timer - the whole column stays on the road until the
// NEXT convoy event spawns, which clears it.
//
// HOW IT REUSES THE PATROL. A convoy vehicle is an ordinary patrol Unit tagged
// with a non-zero ConvoyId, so it is driven, gunned, crewed and wrecked by the
// existing Patrol code. This file only decides WHICH vehicles spawn WHERE,
// keeps their spacing, doubles their loot, announces the square, and clears the
// previous convoy when the next one comes. The seams it uses on Patrol are the
// clearly-marked "NDR convoy" methods (SpawnConvoyUnit, ConvoyAlive/Exists/
// Tank/Pos/Arc, ConvoyHold, ConvoyClearAll, ConvoyRouteNames/Points) plus the
// Route "kind=convoy" flag; nothing else in the giant file is touched.
//
// FOR THE BEHAVIOUR AGENT (feature/convoy-behaviour, "convoy under fire"). This
// file exposes the exact contract that spec asked for: ActiveConvoys() returns
// each live Convoy with its Members IN ROUTE ORDER (index 0 = FRONT ...
// last = TAIL), and each Member answers IsAlive / Tank / Arc / Pos. The two
// movement commands it needs are CommandHold(member) and CommandContinue(
// member). Implement HoldAndSearch/ContinueRoute on top of these; do not fork
// this file. See docs/ai/tasks/convoy-behaviour.md.
//
// KNOWN LIMITATIONS (documented for the acceptance run, not blockers):
//   - The bottom-left push is drawn by the plugin (a bilingual banner), NOT the
//     game's own event widget: that widget could not be identified statically,
//     and the text is dynamic ("square XY"). Announce() is the one seam a native
//     hook would replace after an in-game look.
//   - The map SQUARE is computed from MapUIManager.WORLD_SIZE with a plain
//     A..J / 1..10 grid (CfgGrid*). If the in-game map's grid differs, it is a
//     config/orientation tweak, not code.
//   - Spacing is basic column-keeping (hold a follower that closed the gap). Once
//     feature/convoy-behaviour merges, IT governs Hold under fire; this file
//     backs off spacing as soon as a convoy has taken a loss.
//   - Spawn/loot/clear run on the MASTER CLIENT only, like the patrols.
//   - "Nearest player" uses the master's local player position; a full
//     multi-player nearest-player scan is a later refinement.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// ASCII-only comments and logs. Player-facing strings go through Loc.T, whose
// Russian half is real Cyrillic - this file is therefore UTF-8 (no BOM) and is
// compiled with /codepage:65001 like the main file.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RevivalConvoy
    {
        // ============================================================= config
        internal static ConfigEntry<bool>    CfgEnabled;
        internal static ConfigEntry<float>   CfgIntervalMin;
        internal static ConfigEntry<float>   CfgIntervalMax;
        internal static ConfigEntry<int>     CfgMaxAlive;
        internal static ConfigEntry<int>     CfgTanks;
        internal static ConfigEntry<int>     CfgApcs;
        internal static ConfigEntry<float>   CfgSpacing;
        internal static ConfigEntry<float>   CfgSpawnDistMin;
        internal static ConfigEntry<float>   CfgSpawnDistMax;
        internal static ConfigEntry<float>   CfgDespawnDist;
        internal static ConfigEntry<int>     CfgLootMultiplier;
        internal static ConfigEntry<int>     CfgGridCols;
        internal static ConfigEntry<int>     CfgGridRows;
        internal static ConfigEntry<bool>    CfgGridTopIsNorth;
        internal static ConfigEntry<KeyCode> CfgSpawnKey;
        internal static ConfigEntry<float>   CfgBannerSeconds;
        internal static ConfigEntry<float>   CfgLineupGap;
        internal static ConfigEntry<float>   CfgCruiseSpeed;

        static bool Enabled { get { return CfgEnabled == null || CfgEnabled.Value; } }

        /// <summary>How many EXTRA times Patrol.Arm stocks a convoy vehicle's
        /// trunk beyond the one patrol fill - LootMultiplier minus one, so the
        /// total is LootMultiplier fills (~double at the default 2). Read by the
        /// Arm seam in RevivalPlugin.cs.</summary>
        internal static int ExtraTrunkFills
        {
            get
            {
                int m = CfgLootMultiplier == null ? 2 : CfgLootMultiplier.Value;
                return Mathf.Max(0, m - 1);
            }
        }

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Convoy", "Enabled", true,
                "Konvoi-Ereignis: von Zeit zu Zeit faehrt eine Kolonne aus 2 "
                + "Panzern und 2 BTR eine als Konvoi markierte Route ab, mit "
                + "einer Meldung unten links, in welchem Quadrat sie auftaucht. "
                + "Braucht Patrol/Enabled.");
            CfgIntervalMin = cfg.Bind("Convoy", "IntervalMinHours", 1f,
                "Kuerzester Abstand zwischen zwei Konvois in Stunden.");
            CfgIntervalMax = cfg.Bind("Convoy", "IntervalMaxHours", 3f,
                "Laengster Abstand zwischen zwei Konvois in Stunden. Der echte "
                + "Abstand wird je Ereignis zufaellig aus Min..Max gezogen.");
            CfgMaxAlive = cfg.Bind("Convoy", "MaxAlive", 1,
                "Wie viele Konvois gleichzeitig auf der Karte sein duerfen. Beim "
                + "Start eines neuen Konvois wird der aelteste geloescht, sobald "
                + "diese Zahl ueberschritten wuerde - so verschwinden die Wracks "
                + "des vorigen Konvois genau dann, wenn der naechste kommt.");
            CfgTanks = cfg.Bind("Convoy", "Tanks", 2,
                "Panzer je Konvoi. Einer faehrt vorne, der Rest hinten, die BTR "
                + "dazwischen.");
            CfgApcs = cfg.Bind("Convoy", "Apcs", 2,
                "BTR je Konvoi, in der Mitte der Kolonne.");
            CfgSpacing = cfg.Bind("Convoy", "SpacingMetres", 45f,
                "Abstand in Metern, den ein Fahrzeug hinter dem vorderen haelt.");
            CfgSpawnDistMin = cfg.Bind("Convoy", "SpawnDistanceMin", 300f,
                "Naechster Abstand des Spawn-Wegpunkts vom Spieler in Metern.");
            CfgSpawnDistMax = cfg.Bind("Convoy", "SpawnDistanceMax", 900f,
                "Weitester Abstand des Spawn-Wegpunkts vom Spieler in Metern.");
            CfgDespawnDist = cfg.Bind("Convoy", "DespawnDistance", 1500f,
                "Ist ein VOLLSTAENDIGER (unbeschaedigter) Konvoi weiter als dies "
                + "vom Spieler weg, gilt er als entkommen und wird entfernt. "
                + "Wracks bleiben unabhaengig davon bis zum naechsten Konvoi.");
            CfgLootMultiplier = cfg.Bind("Convoy", "LootMultiplier", 2,
                "Wie oft der Kofferraum je Konvoi-Fahrzeug bestueckt wird. 2 = "
                + "etwa doppelt so viel wie ein Patrouillenfahrzeug (gleiche "
                + "Modul-Beute wie die Patrouille, nur mehr davon).");
            CfgGridCols = cfg.Bind("Convoy", "GridColumns", 10,
                "Spalten (A..) des Kartenrasters fuer die Quadrat-Meldung.");
            CfgGridRows = cfg.Bind("Convoy", "GridRows", 10,
                "Zeilen (1..) des Kartenrasters fuer die Quadrat-Meldung.");
            CfgGridTopIsNorth = cfg.Bind("Convoy", "GridRow1IsNorth", true,
                "Liegt Zeile 1 im Norden (oben) der Karte? Wenn die Meldung die "
                + "falsche Zeile nennt, hier umstellen.");
            CfgSpawnKey = cfg.Bind("Convoy", "SpawnNowKey", KeyCode.F10,
                "Taste, die zum Testen sofort einen Konvoi losschickt. None zum "
                + "Abschalten. Auch der F4-Knopf \"Konvoi sofort\" tut das.");
            CfgBannerSeconds = cfg.Bind("Convoy", "BannerSeconds", 12f,
                "Wie lange die Meldung unten links stehen bleibt.");
            CfgLineupGap = cfg.Bind("Convoy", "LineupGapMetres", 24f,
                "Abstand in Metern zwischen den Fahrzeugen in der Startaufstellung. "
                + "Alle spawnen aufgereiht auf einer Linie ab Wegpunkt 0 in "
                + "Fahrtrichtung, das vorderste auf Wegpunkt 0, die anderen "
                + "dahinter - wie am Startgatter.");
            CfgCruiseSpeed = cfg.Bind("Convoy", "CruiseSpeedKmh", 42f,
                "Marschgeschwindigkeit eines Konvois in km/h. Der Konvoi faehrt "
                + "Vollgas und bremst nur fuer harte Kurven ab.");
        }

        /// <summary>Full-gas cruise speed a convoy vehicle drives at, read by the
        /// patrol driver for convoy units.</summary>
        internal static float CruiseSpeed
        {
            get { return CfgCruiseSpeed == null ? 42f : Mathf.Max(12f, CfgCruiseSpeed.Value); }
        }

        /// <summary>Metres between vehicles in the start line-up.</summary>
        internal static float LineupGap
        {
            get { return CfgLineupGap == null ? 24f : Mathf.Max(6f, CfgLineupGap.Value); }
        }

        // ============================================================== state

        /// <summary>One vehicle of a convoy. A thin view over a Patrol Unit,
        /// exposed for feature/convoy-behaviour.</summary>
        internal sealed class Member
        {
            internal object Handle;     // opaque Patrol Unit
            internal bool Tank;

            /// <summary>On the road and not yet a wreck.</summary>
            internal bool IsAlive { get { return Patrol.ConvoyAlive(Handle); } }
            /// <summary>Still in the world - alive OR a lingering wreck.</summary>
            internal bool Exists { get { return Patrol.ConvoyExists(Handle); } }
            /// <summary>Progress along the route in waypoints (higher = further
            /// ahead), for "is the road ahead blocked".</summary>
            internal float Arc { get { return Patrol.ConvoyArc(Handle); } }
            internal Vector3 Pos { get { return Patrol.ConvoyPos(Handle); } }
        }

        /// <summary>One convoy. Members are IN ROUTE ORDER: index 0 is the
        /// FRONT vehicle, the last is the TAIL.</summary>
        internal sealed class Convoy
        {
            internal int Id;
            internal string Route;
            internal float Born;
            internal bool LostOne;     // has any member died? then leave holds to the behaviour layer
            internal List<Member> Members = new List<Member>();
        }

        static readonly List<Convoy> _convoys = new List<Convoy>();
        static int _nextId = 1;
        static float _nextSpawn = -1f;

        static string _banner = "";
        static float _bannerUntil;

        static bool _worldLookedUp;
        static Vector2 _worldSize = new Vector2(2048f, 2048f);

        // =============================================================== frame

        internal static void Tick()
        {
            if (!Enabled || RevivalPlugin.CfgPatrol == null
                || !RevivalPlugin.CfgPatrol.Value) return;
            try
            {
                if (CfgSpawnKey != null && CfgSpawnKey.Value != KeyCode.None
                    && Input.GetKeyDown(CfgSpawnKey.Value))
                    SpawnConvoy();

                Prune();

                if (!WorldUp()) { _nextSpawn = -1f; return; }

                // While every vehicle of a convoy still drives, NOTHING holds
                // them: they roll their route bluntly at full gas (the gun still
                // fires in range). Only once a convoy has lost a vehicle does the
                // reaction below stop survivors and pick one escapee.
                Behaviour();

                if (_nextSpawn < 0f) ScheduleNext();
                else if (Time.time >= _nextSpawn)
                {
                    SpawnConvoy();
                    ScheduleNext();
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Convoy tick: " + ex); }
        }

        internal static void Draw()
        {
            if (_banner == null || _banner.Length == 0 || Time.time > _bannerUntil)
                return;
            try
            {
                float w = 430f, h = 56f;
                float x = 22f;
                float y = Screen.height - h - 92f;   // bottom-left, above the F4 hint
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
                GUI.color = new Color(1f, 0.62f, 0.12f, 0.95f);   // amber bar
                GUI.DrawTexture(new Rect(x, y, 5f, h), Texture2D.whiteTexture);
                GUI.color = old;

                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.fontSize = 15;
                st.wordWrap = true;
                st.normal.textColor = new Color(1f, 0.86f, 0.58f);
                GUI.Label(new Rect(x + 16f, y + 7f, w - 26f, h - 12f), _banner, st);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Convoy banner: " + ex.Message);
            }
        }

        // ============================================================ spawning

        /// <summary>Force a convoy onto a named route now (the F4 "convoy now"
        /// button). Returns false and logs if it could not start.</summary>
        internal static bool SpawnOn(string routeName)
        {
            try { return DoSpawn(routeName); }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Convoy SpawnOn: " + ex);
                return false;
            }
        }

        /// <summary>The F8 admin "spawn convoy now" button: pick a random
        /// convoy-marked route and force one out immediately, like the F10 key.
        /// Returns a short player-facing status; never throws.</summary>
        internal static string SpawnNow()
        {
            try
            {
                List<string> routes = Patrol.ConvoyRouteNames();
                if (routes == null || routes.Count == 0)
                    return Loc.T("нет маршрута конвоя (F4 -> маршрут -> \"конвой\")",
                                 "no convoy route marked (F4 -> a route -> \"convoy\")");
                string pick = routes[UnityEngine.Random.Range(0, routes.Count)];
                return DoSpawn(pick)
                    ? Loc.T("конвой выехал на ", "convoy sent out on ") + pick
                    : Loc.T("конвой не удалось запустить (см. лог)",
                            "convoy could not start (see log)");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Convoy SpawnNow: " + ex);
                return Loc.T("конвой не удалось запустить (см. лог)",
                             "convoy could not start (see log)");
            }
        }

        static void SpawnConvoy()
        {
            List<string> routes = Patrol.ConvoyRouteNames();
            if (routes.Count == 0)
            {
                RevivalPlugin.L.LogInfo("Convoy: due, but no convoy route is marked "
                    + "(F4 -> a route -> \"convoy\").");
                return;
            }
            string pick = routes[UnityEngine.Random.Range(0, routes.Count)];
            DoSpawn(pick);
        }

        static bool DoSpawn(string routeName)
        {
            if (!MasterClient())
            {
                RevivalPlugin.L.LogInfo("Convoy: only the master client spawns convoys.");
                return false;
            }
            List<Vector3> pts = Patrol.ConvoyRoutePoints(routeName);
            if (pts == null || pts.Count < 3)
            {
                RevivalPlugin.L.LogWarning("Convoy: route \"" + routeName
                    + "\" is unusable (needs at least 3 waypoints).");
                return false;
            }

            // Make room: clear the OLDEST convoys until the new one fits under
            // MaxAlive. This is what removes the previous convoy's lingering
            // wrecks - "they stay until the next convoy spawns".
            int maxAlive = CfgMaxAlive == null ? 1 : Mathf.Max(1, CfgMaxAlive.Value);
            while (_convoys.Count >= maxAlive)
            {
                Patrol.ConvoyClearAll(_convoys[0].Id);
                _convoys.RemoveAt(0);
            }

            int n = pts.Count;
            // The map/road/composition editor (RevivalComposition) may define the
            // exact per-vehicle kind order for THIS route (tank/BTR/Ural); when it
            // does, it wins over the Convoy/Tanks + Convoy/Apcs config. Otherwise
            // the config composition is used, exactly as before. The one-way drive,
            // line-up, spacing and loss reaction are unchanged either way - only
            // WHICH kinds spawn.
            string[] kinds = RevivalComposition.VehicleKindStrings(routeName);
            if (kinds == null || kinds.Length == 0)
            {
                bool[] cfg = Composition();        // front -> tail, true = tank
                kinds = new string[cfg.Length];
                for (int i = 0; i < cfg.Length; i++)
                    kinds[i] = cfg[i] ? "tank" : "btr";
            }

            int id = _nextId++;
            Convoy c = new Convoy();
            c.Id = id;
            c.Route = routeName;
            c.Born = Time.time;

            // The whole column starts lined up on the route's start line: the
            // FRONT vehicle on waypoint 0, each following vehicle one LineupGap
            // further back along the wp0 -> wp1 heading, all facing forward - like
            // a column waiting at the start gate. They drive off together at full
            // gas and run the recorded route once. Members are added front first,
            // so index 0 is the front and the last is the tail (the route order
            // the behaviour reaction relies on).
            // A route flagged vehicle=ural forces a truck convoy: every member is
            // a 15-seat Ural regardless of the composition. The one-way behaviour
            // is unchanged (it lives in the Unit, not the prefab).
            string forced = Patrol.RouteVehicle(routeName);
            bool ural = forced == "ural";
            float gap = LineupGap;
            for (int k = 0; k < kinds.Length; k++)
            {
                float back = gap * k;              // 0 = front, on waypoint 0
                string kind = ural ? "ural" : kinds[k];
                object h = Patrol.SpawnConvoyUnit(routeName, kind, back, id, k);
                if (h == null)
                {
                    // A column is useful only when it is complete. Keep the
                    // existing all-or-nothing gameplay rule: remove every
                    // member already made during this attempt and report fail.
                    Patrol.ConvoyClearAll(id);
                    RevivalPlugin.L.LogWarning("Convoy " + id + ": vehicle " + k
                        + " failed to spawn; partial column rolled back.");
                    return false;
                }
                Member m = new Member();
                m.Handle = h;
                m.Tank = kind == "tank";
                c.Members.Add(m);
            }

            if (c.Members.Count == 0)
            {
                RevivalPlugin.L.LogWarning("Convoy " + id + ": no vehicle could be "
                    + "spawned (not the master client, or CarSpawn refused).");
                return false;
            }

            _convoys.Add(c);
            Announce(pts[0]);
            RevivalPlugin.L.LogInfo("Convoy " + id + ": " + c.Members.Count
                + " vehicle(s) lined up at the start of " + routeName + " ("
                + n + " waypoints, one-way, " + gap.ToString("0") + " m apart).");
            return true;
        }

        /// <summary>Front to tail: one tank at the front, then the APCs, then any
        /// remaining tanks at the tail. Default 2 tanks + 2 APCs =
        /// tank, APC, APC, tank.</summary>
        static bool[] Composition()
        {
            int tanks = CfgTanks == null ? 2 : Mathf.Clamp(CfgTanks.Value, 0, 6);
            int apcs = CfgApcs == null ? 2 : Mathf.Clamp(CfgApcs.Value, 0, 6);
            List<bool> list = new List<bool>();
            if (tanks > 0) list.Add(true);                 // front tank
            for (int i = 0; i < apcs; i++) list.Add(false);  // middle APCs
            for (int i = 1; i < tanks; i++) list.Add(true);  // tail tank(s)
            if (list.Count == 0) { list.Add(true); list.Add(false); }
            return list.ToArray();
        }

        /// <summary>Waypoints that make up SpacingMetres, from the route's own
        /// average leg length.</summary>
        static int GapWaypoints(List<Vector3> pts)
        {
            float sum = 0f;
            int m = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                sum += Flat(pts[i] - pts[i - 1]);
                m++;
            }
            float avg = m > 0 ? sum / m : 20f;
            if (avg < 0.5f) avg = 0.5f;
            float sp = CfgSpacing == null ? 45f : CfgSpacing.Value;
            return Mathf.Max(1, Mathf.RoundToInt(sp / avg));
        }

        /// <summary>A front waypoint with room for the whole column behind it
        /// (index >= span) and, if a player is known, within SpawnDistanceMin..
        /// Max of him. Falls back to the waypoint nearest that band.</summary>
        static int ChooseFront(List<Vector3> pts, Vector3 me, int span)
        {
            int n = pts.Count;
            if (span >= n) span = 0;               // route too short for full spacing
            if (me == Vector3.zero) return Mathf.Clamp(span, 0, n - 1);

            float min = CfgSpawnDistMin == null ? 300f : CfgSpawnDistMin.Value;
            float max = CfgSpawnDistMax == null ? 900f : CfgSpawnDistMax.Value;
            float mid = (min + max) * 0.5f;
            int best = -1;
            float bestScore = 0f;
            for (int i = span; i < n; i++)
            {
                float d = Flat(pts[i] - me);
                if (d >= min && d <= max) return i;    // first one in the band wins
                float score = Mathf.Abs(d - mid);
                if (best < 0 || score < bestScore) { best = i; bestScore = score; }
            }
            return best < 0 ? Mathf.Clamp(span, 0, n - 1) : best;
        }

        // =========================================================== upkeep

        /// <summary>
        /// The convoy's reaction to losing a vehicle (feature/convoy-oneway-drive,
        /// the minimal form of docs/ai/tasks/convoy-behaviour.md). It does NOTHING
        /// while a convoy is intact - every vehicle just drives. The first time a
        /// convoy loses one (Convoy.LostOne, set in Prune), the survivors stop and
        /// search near the wreck and exactly ONE vehicle drives off, UNLESS both
        /// ends of the column are wrecked, in which case the middle is boxed in
        /// and nobody escapes.
        ///
        ///   live == 1        the lone survivor drives off (nothing to search with)
        ///   both ends wreck  every survivor holds and fights - the rewarded trap
        ///   otherwise        one escapee (an APC by preference, front-most; a tank
        ///                    only if no APC survives) drives on, the rest hold
        ///
        /// Re-run every tick, so if the escapee is killed or reaches the end, a new
        /// one is chosen from what remains until the convoy is boxed or down to one.
        /// The gun keeps scanning and firing underneath, held or not.
        /// </summary>
        static void Behaviour()
        {
            for (int ci = 0; ci < _convoys.Count; ci++)
            {
                Convoy c = _convoys[ci];
                if (!c.LostOne) continue;          // intact: never held

                List<Member> live = new List<Member>();
                for (int k = 0; k < c.Members.Count; k++)
                    if (c.Members[k].IsAlive) live.Add(c.Members[k]);

                if (live.Count == 0) continue;
                if (live.Count == 1) { CommandContinue(live[0]); continue; }

                // "First and last": a wreck on either END blocks the road that
                // way. Both ends wrecked means the survivors are boxed between two
                // wrecks and cannot continue the route in either direction.
                bool boxed = Wreck(c.Members[0])
                          && Wreck(c.Members[c.Members.Count - 1]);
                if (boxed)
                {
                    for (int k = 0; k < live.Count; k++) CommandHold(live[k]);
                    continue;
                }

                // Not boxed: exactly one escapee continues, the rest hold. Prefer
                // the front-most APC; a tank leaves only if no APC survives.
                Member escapee = null;
                for (int k = 0; k < live.Count; k++)
                    if (!live[k].Tank) { escapee = live[k]; break; }
                if (escapee == null) escapee = live[0];

                for (int k = 0; k < live.Count; k++)
                {
                    if (live[k] == escapee) CommandContinue(live[k]);
                    else CommandHold(live[k]);
                }
            }
        }

        /// <summary>A member that is a wreck sitting on the road (still in the
        /// world, no longer alive) - the thing that blocks a route end.</summary>
        static bool Wreck(Member m) { return m != null && m.Exists && !m.IsAlive; }

        /// <summary>Hold a follower that has closed to less than SpacingMetres,
        /// release it once the gap opens. The front vehicle is never held here.
        /// Backs off entirely once a convoy has lost a vehicle - from then on
        /// the (later-merged) behaviour layer owns the holds.</summary>
        static void Spacing()
        {
            float sp = CfgSpacing == null ? 45f : CfgSpacing.Value;
            float tight = sp * 0.6f;
            float loose = sp * 0.9f;
            for (int ci = 0; ci < _convoys.Count; ci++)
            {
                Convoy c = _convoys[ci];
                if (c.LostOne) continue;
                for (int k = 1; k < c.Members.Count; k++)
                {
                    Member self = c.Members[k];
                    Member ahead = c.Members[k - 1];
                    if (!self.IsAlive) continue;
                    if (!ahead.Exists) { Patrol.ConvoyHold(self.Handle, false); continue; }
                    float d = Flat(self.Pos - ahead.Pos);
                    if (d < tight) Patrol.ConvoyHold(self.Handle, true);
                    else if (d > loose) Patrol.ConvoyHold(self.Handle, false);
                }
            }
        }

        /// <summary>A fully intact convoy that drove out of DespawnDistance is
        /// gone for good and cleared. A convoy that has taken a loss is left
        /// alone - its wrecks linger until the next convoy spawns.</summary>
        static void Despawn()
        {
            Vector3 me = PlayerPos();
            float dd = CfgDespawnDist == null ? 1500f : CfgDespawnDist.Value;
            for (int i = _convoys.Count - 1; i >= 0; i--)
            {
                Convoy c = _convoys[i];
                if (c.LostOne || HasWreck(c)) continue;
                bool near = false;
                int alive = 0;
                for (int k = 0; k < c.Members.Count; k++)
                {
                    if (!c.Members[k].IsAlive) continue;
                    alive++;
                    if (me == Vector3.zero || Flat(c.Members[k].Pos - me) <= dd)
                        near = true;
                }
                if (alive > 0 && !near)
                {
                    Patrol.ConvoyClearAll(c.Id);
                    _convoys.RemoveAt(i);
                    RevivalPlugin.L.LogInfo("Convoy " + c.Id
                        + " drove out of range - cleared.");
                }
            }
        }

        /// <summary>Drop convoys whose vehicles are all gone from the world (for
        /// example after a scene change cleared the units), and flag a convoy the
        /// first time one of its vehicles dies.</summary>
        static void Prune()
        {
            for (int i = _convoys.Count - 1; i >= 0; i--)
            {
                Convoy c = _convoys[i];
                bool any = false;
                int alive = 0;
                for (int k = 0; k < c.Members.Count; k++)
                {
                    if (c.Members[k].Exists) any = true;
                    if (c.Members[k].IsAlive) alive++;
                }
                if (!c.LostOne && alive < c.Members.Count) c.LostOne = true;
                if (!any) _convoys.RemoveAt(i);
            }
        }

        static bool HasWreck(Convoy c)
        {
            for (int k = 0; k < c.Members.Count; k++)
                if (c.Members[k].Exists && !c.Members[k].IsAlive) return true;
            return false;
        }

        // ========================================================= the square

        static void Announce(Vector3 pos)
        {
            string cell = GridCell(pos);
            _banner = Loc.T("Замечен конвой в квадрате " + cell + "!",
                            "Convoy sighted in square " + cell + "!");
            _bannerUntil = Time.time
                + (CfgBannerSeconds == null ? 12f : CfgBannerSeconds.Value);
            RevivalPlugin.L.LogInfo("Convoy: sighted in square " + cell + ".");
        }

        /// <summary>The map square of a world position: a plain A.. / 1.. grid
        /// over MapUIManager.WORLD_SIZE. Best effort - the exact in-game grid is
        /// a config/orientation tweak (CfgGrid*).</summary>
        static string GridCell(Vector3 pos)
        {
            Vector2 w = WorldSize();
            int cols = CfgGridCols == null ? 10 : Mathf.Clamp(CfgGridCols.Value, 1, 26);
            int rows = CfgGridRows == null ? 10 : Mathf.Clamp(CfgGridRows.Value, 1, 99);
            float nx = Mathf.Clamp01(pos.x / w.x + 0.5f);
            float nz = Mathf.Clamp01(pos.z / w.y + 0.5f);
            int col = Mathf.Clamp(Mathf.FloorToInt(nx * cols), 0, cols - 1);
            bool topNorth = CfgGridTopIsNorth == null || CfgGridTopIsNorth.Value;
            float rowFrac = topNorth ? (1f - nz) : nz;
            int row = Mathf.Clamp(Mathf.FloorToInt(rowFrac * rows), 0, rows - 1) + 1;
            char letter = (char)('A' + col);
            return letter.ToString() + row.ToString();
        }

        static Vector2 WorldSize()
        {
            if (_worldLookedUp) return _worldSize;
            _worldLookedUp = true;
            try
            {
                Type t = RevivalPlugin.TypeByName("MapUIManager");
                if (t != null)
                {
                    FieldInfo f = AccessTools.Field(t, "WORLD_SIZE");
                    if (f != null)
                    {
                        object v = f.GetValue(null);
                        if (v is Vector2)
                        {
                            Vector2 ws = (Vector2)v;
                            if (ws.x > 1f && ws.y > 1f) _worldSize = ws;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Convoy WorldSize: " + ex.Message);
            }
            return _worldSize;
        }

        // ============================================================ helpers

        static void ScheduleNext()
        {
            float lo = CfgIntervalMin == null ? 1f : Mathf.Max(0.01f, CfgIntervalMin.Value);
            float hi = CfgIntervalMax == null ? 3f : Mathf.Max(lo, CfgIntervalMax.Value);
            float hours = UnityEngine.Random.Range(lo, hi);
            _nextSpawn = Time.time + hours * 3600f;
            RevivalPlugin.L.LogInfo("Convoy: next event in "
                + hours.ToString("0.0") + " h.");
        }

        static bool WorldUp()
        {
            try { return MapTools.LocalPlayer() != null; }
            catch { return false; }
        }

        static Vector3 PlayerPos()
        {
            try
            {
                GameObject p = MapTools.LocalPlayer();
                return p == null ? Vector3.zero : p.transform.position;
            }
            catch { return Vector3.zero; }
        }

        static bool MasterClient()
        {
            try
            {
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) return true;
                MethodInfo g = AccessTools.PropertyGetter(photon, "isMasterClient");
                if (g == null) g = AccessTools.PropertyGetter(photon, "IsMasterClient");
                if (g == null) return true;
                object v = g.Invoke(null, null);
                return v == null || (bool)v;
            }
            catch { return true; }
        }

        static float Flat(Vector3 v)
        {
            v.y = 0f;
            return v.magnitude;
        }

        // ============================ contract for feature/convoy-behaviour

        /// <summary>Every convoy currently on the road. Members are in route
        /// order (index 0 = front, last = tail). See the class docs and
        /// docs/ai/tasks/convoy-behaviour.md.</summary>
        internal static List<Convoy> ActiveConvoys() { return _convoys; }

        /// <summary>Stop a convoy vehicle where it stands (hold-and-search).</summary>
        internal static void CommandHold(Member m)
        {
            if (m != null) Patrol.ConvoyHold(m.Handle, true);
        }

        /// <summary>Release a convoy vehicle back to driving its route.</summary>
        internal static void CommandContinue(Member m)
        {
            if (m != null) Patrol.ConvoyHold(m.Handle, false);
        }
    }
}

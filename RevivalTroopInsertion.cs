// Next Day: Survival - Revival Toolkit
//
// HELI TROOP INSERTION. From time to time, or when an admin presses the F8
// button, the game's own humanitarian-aid helicopter (the networked Mi-8 scene
// object "GamePlayObjects/Helicopters/mi-8_mchs") flies to a landing zone an
// admin marked in the online editor, lands, a squad climbs out, the helicopter
// lifts off and leaves. The squad then follows the combat arrow drawn in the
// editor through NpcWar (Revival.NpcCombat.cs): it advances along it killing
// every hostile it sees and patrols the arrow for at most two hours.
//
// DATA. Landings come live from the editor at /runtime/troops (LiveRoutes), so
// a saved landing reaches the game without a release; assets/ndr_troopdrops.tsv
// is the offline fallback. Format and validation: troopdef.py.
//
// THE HELICOPTER. RE from the installed assembly (ilq.py):
//   NetworkGameplayEvents.HelicopterDummySpawn instantiates the Mi-8 with
//   PhotonNetwork.InstantiateSceneObject and then RPCs NetworkSetMovementData.
//   HelicopterDummy.Update spins the rotors everywhere and, on the master only,
//   MoveTowards(targetPosition) - which returns at once while startPosition or
//   targetPosition is zero. The prefab carries PhotonView +
//   PhotonInterpolatedTransform, so a transform the master moves is replicated.
//   HelicopterDummy.Start on a NON-owner stores itself as the aid event's
//   HelicopterDummyObject; the Start hook below keeps the aid event's reference
//   intact for our helicopter.
// So the master instantiates the same prefab with instantiation data that marks
// it as ours, never gives it movement data, and flies it itself. Every client
// - late joiners included, since instantiation data travels with the object -
// scales it and gives it a hull collider in the Start hook.
//
// HYPOTHESES (not yet seen in game): the prefab is modelled at about 2.8 times
// real size for high flight (rotor disc about 75 m, cabin about 15 m tall) -
// HeliScale 0.36 brings it to a real Mi-8; PhotonInterpolatedTransform syncs
// the rotation the master sets.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree lambdas.
// build.ps1 does not reference UnityEngine.PhysicsModule or AudioModule, so
// raycasts go through Turret.RaycastObject and colliders through reflection.
// Player-facing strings go through Loc.T; this file is UTF-8 without BOM.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    internal static class RevivalTroopInsertion
    {
        // ============================================================= config
        internal static ConfigEntry<bool>    CfgEnabled;
        internal static ConfigEntry<float>   CfgHeliScale;
        internal static ConfigEntry<float>   CfgCruiseHeight;
        internal static ConfigEntry<float>   CfgApproachDist;
        internal static ConfigEntry<float>   CfgHeliSpeed;
        internal static ConfigEntry<float>   CfgUnloadSeconds;
        internal static ConfigEntry<KeyCode> CfgSpawnKey;
        internal static ConfigEntry<float>   CfgBannerSeconds;
        internal static ConfigEntry<int>     CfgEventCode;
        internal static ConfigEntry<int>     CfgGridCols;
        internal static ConfigEntry<int>     CfgGridRows;
        internal static ConfigEntry<bool>    CfgGridTopIsNorth;
        internal static ConfigEntry<string>  CfgFile;

        internal const string HeliPrefab = "GamePlayObjects/Helicopters/mi-8_mchs";
        internal const string HeliMarker = "ndr-troopheli-1";

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind("Troops", "Enabled", true,
                "Heli-Truppenlandungen aus dem Online-Editor: der Hilfsgueter-"
                + "Hubschrauber landet am markierten Punkt, ein Trupp steigt aus "
                + "und kaempft sich den Pfeil entlang. Nur der Master-Client "
                + "startet Landungen.");
            CfgHeliScale = cfg.Bind("Troops", "HeliScale", 0.36f,
                "Groesse des gelandeten Hubschraubers. Das Spielmodell ist fuer "
                + "den Hoehenflug etwa 2,8-fach vergroessert; 0,36 entspricht einem "
                + "echten Mi-8.");
            CfgCruiseHeight = cfg.Bind("Troops", "CruiseHeight", 90f,
                "Flughoehe in Metern ueber dem hoechsten Gelaende auf dem Anflug.");
            CfgApproachDist = cfg.Bind("Troops", "ApproachDistance", 1500f,
                "Aus welcher Entfernung (Meter) der Hubschrauber anfliegt - von "
                + "hinter dem Pfeil, so als braechte er den Angriff mit.");
            CfgHeliSpeed = cfg.Bind("Troops", "HeliSpeed", 55f,
                "Reisegeschwindigkeit in Metern pro Sekunde.");
            CfgUnloadSeconds = cfg.Bind("Troops", "UnloadSeconds", 12f,
                "Wie lange der Hubschrauber nach dem Aussteigen am Boden bleibt.");
            CfgSpawnKey = cfg.Bind("Troops", "SpawnNowKey", KeyCode.None,
                "Taste fuer den Test: startet sofort eine zufaellige aktive "
                + "Landung. None = aus; im Betrieb gibt es den Knopf im F8-Menue.");
            CfgBannerSeconds = cfg.Bind("Troops", "BannerSeconds", 12f,
                "Wie lange die Meldung unten links stehen bleibt.");
            CfgEventCode = cfg.Bind("Troops", "NetworkEventCode", 160,
                "Photon-Ereigniscode (0..197) fuer Meldung und Schuesse; belegt "
                + "diesen und die zwei folgenden Codes. Muss auf allen Clients "
                + "gleich sein.");
            CfgGridCols = cfg.Bind("Troops", "GridColumns", 10,
                "Spalten (A..) des Kartenrasters fuer die Quadrat-Meldung.");
            CfgGridRows = cfg.Bind("Troops", "GridRows", 10,
                "Zeilen (1..) des Kartenrasters fuer die Quadrat-Meldung.");
            CfgGridTopIsNorth = cfg.Bind("Troops", "GridRow1IsNorth", true,
                "Liegt Zeile 1 im Norden (oben) der Karte?");
            CfgFile = cfg.Bind("Troops", "TroopDropFile", "ndr_troopdrops.tsv",
                "Ersatzdatei neben der DLL, falls der Editor nicht erreichbar ist.");
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                Type heli = RevivalPlugin.TypeByName("HelicopterDummy");
                MethodInfo start = heli == null ? null : AccessTools.Method(heli, "Start", null, null);
                if (start == null)
                {
                    RevivalPlugin.L.LogWarning("Troops: HelicopterDummy.Start not found - "
                        + "troop helicopters keep the aid model's size.");
                    return;
                }
                harmony.Patch(start,
                    new HarmonyMethod(typeof(RevivalTroopInsertion).GetMethod("HeliStartPrefix",
                        BindingFlags.Public | BindingFlags.Static)),
                    new HarmonyMethod(typeof(RevivalTroopInsertion).GetMethod("HeliStartPostfix",
                        BindingFlags.Public | BindingFlags.Static)),
                    null, null, null);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Troops: helicopter hook failed - " + ex);
            }
        }

        // =============================================================== data

        internal sealed class Landing
        {
            public string Name = "";
            public bool Enabled = true;
            public float X, Z, TailX, TailZ, HeadX, HeadZ;
            public string Faction = "traitor";
            public int Count = 6;
            public float IntervalMinHours, IntervalMaxHours;
            public float PatrolMinutes = 120f;
            public List<RevivalComposition.CrewMan> Squad =
                new List<RevivalComposition.CrewMan>();
            internal float NextSpawn = -1f;

            public Vector3 Tail { get { return new Vector3(TailX, 0f, TailZ); } }
            public Vector3 Head { get { return new Vector3(HeadX, 0f, HeadZ); } }
        }

        static readonly List<Landing> _landings = new List<Landing>();
        static readonly List<HeliFlight> _flights = new List<HeliFlight>();
        static bool _loaded;
        static string[] _source;

        static string _banner = "";
        static float _bannerUntil;

        static bool _worldLookedUp;
        static Vector2 _worldSize = new Vector2(2048f, 2048f);

        // =============================================================== frame

        internal static void Tick()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return;
            try
            {
                Net.EnsureHooked();
                Load(false);
                if (!MasterClient()) return;

                // A flight in the air finishes even while the local player is
                // between lives; only new landings wait for a world.
                for (int i = _flights.Count - 1; i >= 0; i--)
                    if (!_flights[i].Tick()) _flights.RemoveAt(i);
                CleanOrphans();
                if (!WorldUp()) return;

                if (CfgSpawnKey != null && CfgSpawnKey.Value != KeyCode.None
                    && Input.GetKeyDown(CfgSpawnKey.Value))
                    Turret.Hinweis(SpawnNow(), 4f);

                for (int i = 0; i < _landings.Count; i++)
                {
                    Landing d = _landings[i];
                    if (!d.Enabled || d.IntervalMaxHours <= 0f) continue;
                    if (Busy(d)) { d.NextSpawn = -1f; continue; }
                    if (d.NextSpawn < 0f)
                    {
                        float hours = UnityEngine.Random.Range(d.IntervalMinHours, d.IntervalMaxHours);
                        d.NextSpawn = Time.time + hours * 3600f;
                        RevivalPlugin.L.LogInfo("Troops: next landing " + d.Name + " in "
                            + hours.ToString("0.00") + " h.");
                        continue;
                    }
                    if (Time.time >= d.NextSpawn)
                    {
                        d.NextSpawn = -1f;
                        Begin(d);
                    }
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Troops tick: " + ex); }
        }

        internal static void Draw()
        {
            if (_banner == null || _banner.Length == 0 || Time.time > _bannerUntil) return;
            try
            {
                float w = 460f, h = 56f, x = 22f;
                float y = Screen.height - h - 156f;   // above the convoy banner slot
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
                GUI.color = new Color(0.9f, 0.3f, 0.2f, 0.95f);
                GUI.DrawTexture(new Rect(x, y, 5f, h), Texture2D.whiteTexture);
                GUI.color = old;
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.fontSize = 15;
                st.wordWrap = true;
                st.normal.textColor = new Color(1f, 0.85f, 0.75f);
                GUI.Label(new Rect(x + 16f, y + 7f, w - 26f, h - 12f), _banner, st);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Troops banner: " + ex.Message); }
        }

        // ========================================================== triggering

        /// <summary>The F8 button and the test key: a random enabled landing
        /// that is not already on the map, now. Returns a player-facing status.</summary>
        internal static string SpawnNow()
        {
            try
            {
                if (!MasterClient())
                    return Loc.T("десант запускает только мастер-клиент",
                                 "only the master client starts troop landings");
                Load(false);
                List<Landing> usable = new List<Landing>();
                for (int i = 0; i < _landings.Count; i++)
                    if (_landings[i].Enabled && !Busy(_landings[i])) usable.Add(_landings[i]);
                if (usable.Count == 0)
                    return _landings.Count == 0
                        ? Loc.T("нет десантных точек (редактор -> Troop landings)",
                                "no troop landings (editor -> Troop landings)")
                        : Loc.T("все десантные точки уже заняты или выключены",
                                "every troop landing is busy or disabled");
                Landing d = usable[UnityEngine.Random.Range(0, usable.Count)];
                return Begin(d)
                    ? Loc.T("десант вылетел: ", "troops inbound: ") + d.Name
                    : Loc.T("десант не удалось запустить (см. лог)",
                            "troop landing could not start (see log)");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Troops SpawnNow: " + ex);
                return Loc.T("десант не удалось запустить (см. лог)",
                             "troop landing could not start (see log)");
            }
        }

        static bool Busy(Landing d)
        {
            if (NpcWar.IsActive(d.Name)) return true;
            for (int i = 0; i < _flights.Count; i++)
                if (_flights[i].Landing.Name == d.Name) return true;
            return false;
        }

        // ============================================================ the drop

        static bool Begin(Landing d)
        {
            Vector3 lz;
            float yaw = FlatYaw(d.Tail - new Vector3(d.X, 0f, d.Z), d.Head - d.Tail);
            if (!FindLandingSpot(new Vector3(d.X, 0f, d.Z), yaw, out lz))
            {
                RevivalPlugin.L.LogWarning("Troops: landing " + d.Name + " has no ground "
                    + "under (" + d.X.ToString("0") + ", " + d.Z.ToString("0") + ") - skipped.");
                return false;
            }

            string cell = GridCell(lz);
            HeliFlight flight = HeliFlight.Launch(d, lz, yaw);
            if (flight == null)
            {
                RevivalPlugin.L.LogWarning("Troops: the helicopter could not be spawned - "
                    + "the squad of " + d.Name + " is set down without it.");
                return Drop(d, lz, yaw);
            }
            _flights.Add(flight);
            Net.SendBanner(0, cell);
            Banner(0, cell);
            RevivalPlugin.L.LogInfo("Troops: landing " + d.Name + " - " + d.Count + " "
                + d.Faction + ", zone " + lz.ToString("0") + " (square " + cell + ").");
            return true;
        }

        /// <summary>The squad gets out beside the helicopter door and receives
        /// its combat vector.</summary>
        internal static bool Drop(Landing d, Vector3 at, float yaw)
        {
            try
            {
                GameObject settlement = Crew.DropSquad(at, yaw, Mathf.Clamp(d.Count, 1, 16),
                    d.Faction, d.Squad.Count > 0 ? d.Squad : null);
                Array men = Crew.Men(settlement);
                if (settlement == null || men == null || men.Length == 0)
                {
                    RevivalPlugin.L.LogWarning("Troops: nobody got out for " + d.Name + ".");
                    if (settlement != null) UnityEngine.Object.Destroy(settlement);
                    return false;
                }
                Vector3 tail = OnGround(d.Tail, at.y);
                Vector3 head = OnGround(d.Head, at.y);
                NpcWar.StartOperation(d.Name, settlement, men, tail, head, d.PatrolMinutes * 60f);
                string cell = GridCell(at);
                Net.SendBanner(1, cell);
                Banner(1, cell);
                return true;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("Troops drop: " + ex);
                return false;
            }
        }

        static void Banner(int kind, string cell)
        {
            _banner = kind == 0
                ? Loc.T("Вертолёт с десантом летит в квадрат " + cell + "!",
                        "Helicopter with troops inbound to square " + cell + "!")
                : Loc.T("Десант высадился в квадрате " + cell + "!",
                        "Troops have landed in square " + cell + "!");
            _bannerUntil = Time.time + (CfgBannerSeconds == null ? 12f : CfgBannerSeconds.Value);
        }

        // ======================================================= landing zone

        /// <summary>Scaled distances in metres of the landed Mi-8. The prefab's
        /// root sits on the gear; its forward is +Z; the cabin centre is 2.5
        /// model units to the right. Measured with research/dump_prefab.py.</summary>
        internal static float K { get { return Scale() / 0.36f; } }

        internal static float Scale()
        {
            return CfgHeliScale == null ? 0.36f : Mathf.Clamp(CfgHeliScale.Value, 0.2f, 1f);
        }

        /// <summary>A spot the helicopter can stand on: gear on the ground with
        /// less than 1.6 m between the highest and lowest wheel, and nothing
        /// under the rotor disc that reaches it. Tries the marked point first,
        /// then rings out to 60 m. lz.y is the height the gear touches.</summary>
        static bool FindLandingSpot(Vector3 mark, float yaw, out Vector3 lz)
        {
            lz = mark;
            bool any = false;
            Vector3 fallback = mark;
            for (int ring = 0; ring <= 5; ring++)
            {
                int steps = ring == 0 ? 1 : 8;
                for (int k = 0; k < steps; k++)
                {
                    float a = k * Mathf.PI * 2f / steps + ring * 0.4f;
                    Vector3 c = mark + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * (ring * 12f);
                    float touch, spread;
                    bool clear;
                    if (!Touchdown(c, yaw, out touch, out spread, out clear)) continue;
                    if (!any) { any = true; fallback = new Vector3(c.x, touch, c.z); }
                    if (spread <= 1.6f && clear)
                    {
                        lz = new Vector3(c.x, touch, c.z);
                        if (ring > 0)
                            RevivalPlugin.L.LogInfo("Troops: landing zone moved "
                                + (ring * 12) + " m to flat, open ground.");
                        return true;
                    }
                }
            }
            if (!any) return false;
            lz = fallback;
            RevivalPlugin.L.LogWarning("Troops: no flat open ground within 60 m of "
                + mark.ToString("0") + " - landing on the marked point anyway.");
            return true;
        }

        static bool Touchdown(Vector3 at, float yaw, out float touch, out float spread, out bool clear)
        {
            touch = 0f; spread = 0f; clear = false;
            float k = K;
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            Vector3[] gear = new Vector3[] {
                new Vector3(0.9f, 0f, 5.8f), new Vector3(-1.3f, 0f, -1.4f),
                new Vector3(3.1f, 0f, -1.4f), new Vector3(0.9f, 0f, 1.5f) };
            float hi = float.MinValue, lo = float.MaxValue;
            for (int i = 0; i < gear.Length; i++)
            {
                float y;
                if (!GroundY(at + q * (gear[i] * k), out y)) return false;
                hi = Mathf.Max(hi, y);
                lo = Mathf.Min(lo, y);
            }
            float tailY;
            if (GroundY(at + q * (new Vector3(0.9f, 0f, -14.4f) * k), out tailY))
                hi = Mathf.Max(hi, tailY - 3.1f * k);   // the tail boom rides 3 m up
            touch = hi;
            spread = hi - lo;

            clear = true;
            float rotorHeight = 5.9f * k, rotorRadius = 13.3f * k;
            for (int i = 0; i < 8 && clear; i++)
            {
                float a = i * Mathf.PI / 4f;
                float y;
                Vector3 p = at + q * new Vector3(0.9f * k, 0f, 0f)
                          + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * rotorRadius;
                if (GroundY(p, out y) && y > touch + rotorHeight - 1.2f) clear = false;
            }
            return true;
        }

        internal static bool GroundY(Vector3 xz, out float y)
        {
            Vector3 point;
            GameObject hit = Turret.RaycastObject(new Vector3(xz.x, 1500f, xz.z),
                Vector3.down, 3000f, out point);
            y = point.y;
            return hit != null;
        }

        static Vector3 OnGround(Vector3 xz, float fallbackY)
        {
            float y;
            return new Vector3(xz.x, GroundY(xz, out y) ? y : fallbackY, xz.z);
        }

        /// <summary>The way the helicopter faces on the ground: along the walk
        /// to the start line, or along the arrow when the zone is on it.</summary>
        static float FlatYaw(Vector3 first, Vector3 second)
        {
            Vector3 v = first;
            v.y = 0f;
            if (v.sqrMagnitude < 25f) { v = second; v.y = 0f; }
            if (v.sqrMagnitude < 0.01f) return 0f;
            return Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        }

        // ===================================================== helicopter hook

        static readonly List<GameObject> _ourHelis = new List<GameObject>();
        static readonly Dictionary<int, float> _orphanSince = new Dictionary<int, float>();
        static FieldInfo _fOptions, _fDummyObject;
        static MethodInfo _mEventsInstance;

        public static void HeliStartPrefix(object __instance, out object __state)
        {
            __state = null;
            try
            {
                Component heli = __instance as Component;
                if (heli == null || !IsOurHeli(heli)) return;
                PrepareHeli(heli.gameObject);
                object options = EventOptions();
                if (options != null) __state = new object[] { options, _fDummyObject.GetValue(options) };
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Troops heli start: " + ex.Message); }
        }

        public static void HeliStartPostfix(object __instance, object __state)
        {
            object[] saved = __state as object[];
            if (saved == null) return;
            try { _fDummyObject.SetValue(saved[0], saved[1]); }   // the aid event keeps its own heli
            catch { }
        }

        static object EventOptions()
        {
            Type events = RevivalPlugin.TypeByName("NetworkGameplayEvents");
            if (events == null) return null;
            if (_mEventsInstance == null) _mEventsInstance = AccessTools.PropertyGetter(events, "Instance");
            if (_fOptions == null) _fOptions = AccessTools.Field(events, "_helicopterDummyOptions");
            object instance = _mEventsInstance == null ? null : _mEventsInstance.Invoke(null, null);
            if (instance == null || _fOptions == null) return null;
            object options = _fOptions.GetValue(instance);
            if (options == null) return null;
            if (_fDummyObject == null) _fDummyObject = AccessTools.Field(options.GetType(), "HelicopterDummyObject");
            return _fDummyObject == null ? null : options;
        }

        internal static bool IsOurHeli(Component heli)
        {
            object[] data = InstantiationData(heli);
            return data != null && data.Length >= 1 && HeliMarker.Equals(data[0] as string);
        }

        static object[] InstantiationData(Component c)
        {
            MethodInfo pv = AccessTools.Method(c.GetType(), "get_photonView", null, null);
            object view = pv == null ? null : pv.Invoke(c, null);
            if (view == null) return null;
            MethodInfo inst = AccessTools.PropertyGetter(view.GetType(), "instantiationData");
            return inst == null ? null : inst.Invoke(view, null) as object[];
        }

        /// <summary>Same on every client: real-world size, the rotor colliders
        /// off (they spin, and a ray from above would stand the crew on the
        /// rotor disc), and a box for the cabin so nobody walks through it.</summary>
        internal static void PrepareHeli(GameObject go)
        {
            if (go == null || _ourHelis.Contains(go)) return;
            _ourHelis.Add(go);
            float s = Scale();
            Type dummy = RevivalPlugin.TypeByName("HelicopterDummy");
            Component heli = dummy == null ? null : go.GetComponent(dummy);
            object[] data = heli == null ? null : InstantiationData(heli);
            if (data != null && data.Length >= 2 && data[1] is float)
                s = Mathf.Clamp((float)data[1], 0.2f, 1f);
            go.transform.localScale = Vector3.one * s;

            Type colliderType = RevivalPlugin.TypeByName("Collider");
            if (colliderType != null)
            {
                Component[] cols = go.GetComponentsInChildren(colliderType, true);
                PropertyInfo enabled = AccessTools.Property(colliderType, "enabled");
                for (int i = 0; i < cols.Length && enabled != null; i++)
                    enabled.SetValue(cols[i], false, null);
            }
            Type boxType = RevivalPlugin.TypeByName("BoxCollider");
            if (boxType != null)
            {
                GameObject hull = new GameObject("NDR_HeliHull");
                hull.transform.SetParent(go.transform, false);
                Component box = hull.AddComponent(boxType);
                // Model units (the parent scale applies): cabin from nose to the
                // rear of the clamshell doors, gear to roof.
                AccessTools.Property(boxType, "center").SetValue(box, new Vector3(2.5f, 5.5f, 3f), null);
                AccessTools.Property(boxType, "size").SetValue(box, new Vector3(7f, 11f, 38f), null);
            }
        }

        /// <summary>A troop helicopter no flight drives any more - the master
        /// changed mid-flight, or a flight failed - leaves after ten seconds.</summary>
        static void CleanOrphans()
        {
            for (int i = _ourHelis.Count - 1; i >= 0; i--)
            {
                GameObject go = _ourHelis[i];
                if (go == null) { _ourHelis.RemoveAt(i); continue; }
                bool driven = false;
                for (int f = 0; f < _flights.Count; f++)
                    if (_flights[f].Go == go) { driven = true; break; }
                int id = go.GetInstanceID();
                if (driven) { _orphanSince.Remove(id); continue; }
                float since;
                if (!_orphanSince.TryGetValue(id, out since)) { _orphanSince[id] = Time.time; continue; }
                if (Time.time - since < 10f) continue;
                _orphanSince.Remove(id);
                _ourHelis.RemoveAt(i);
                HeliFlight.NetDestroy(go);
                RevivalPlugin.L.LogInfo("Troops: removed a troop helicopter no flight was driving.");
            }
        }

        // ============================================================== loader

        internal static void Load(bool force)
        {
            string[] live = LiveRoutes.Troops;
            if (_loaded && !force && live == _source) return;
            _loaded = true;
            _source = live;

            string[] lines = live;
            string label = "live troop landings";
            if (lines == null)
            {
                string file = CfgFile == null ? "ndr_troopdrops.tsv" : CfgFile.Value;
                string path = Path.Combine(RevivalPlugin.AssetDir ?? "", file);
                label = file;
                if (!File.Exists(path)) { Replace(new List<Landing>(), label); return; }
                try { lines = File.ReadAllLines(path); }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogError("Troops: reading " + path + ": " + ex.Message);
                    return;
                }
            }

            Dictionary<string, Landing> byName = new Dictionary<string, Landing>();
            List<Landing> order = new List<Landing>();
            int bad = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;
                raw = raw.TrimEnd('\r');
                if (raw.Trim().Length == 0 || raw[0] == '#') continue;
                // name enabled x z tailX tailZ headX headZ faction count
                //   intervalMin intervalMax patrolMinutes role weapon headwear mask body legs hands
                string[] c = raw.Split('\t');
                if (c.Length != 20 || c[0].Trim().Length == 0 || c[0].Length > 64) { bad++; continue; }
                string name = c[0].Trim();
                Landing d;
                if (!byName.TryGetValue(name, out d))
                {
                    d = new Landing();
                    d.Name = name;
                    d.Enabled = c[1].Trim() == "1";
                    d.X = Num(c[2]); d.Z = Num(c[3]);
                    d.TailX = Num(c[4]); d.TailZ = Num(c[5]);
                    d.HeadX = Num(c[6]); d.HeadZ = Num(c[7]);
                    d.Faction = Fraktion.Sauber(c[8]);
                    d.Count = Mathf.Clamp((int)Num(c[9]), 1, 16);
                    d.IntervalMinHours = Mathf.Max(0f, Num(c[10]));
                    d.IntervalMaxHours = Mathf.Max(d.IntervalMinHours, Num(c[11]));
                    d.PatrolMinutes = Mathf.Clamp(Num(c[12]), 1f, 120f);
                    if (d.Faction.Length == 0 || !Finite(d.X, d.Z, d.TailX, d.TailZ, d.HeadX, d.HeadZ))
                    { bad++; continue; }
                    if (d.IntervalMaxHours > 0f && d.IntervalMinHours < 0.25f) d.IntervalMinHours = 0.25f;
                    byName[name] = d;
                    order.Add(d);
                }
                string role = c[13].Trim();
                if (role.Length == 0 || d.Squad.Count >= 16) continue;
                RevivalComposition.CrewMan man = new RevivalComposition.CrewMan();
                man.Role = role;
                int weapon = Id(c[14]);
                man.Weapons = weapon > 0 ? new int[] { weapon } : new int[0];
                man.Headwear = Id(c[15]);
                man.Mask = Id(c[16]);
                man.Body = Id(c[17]);
                man.Legs = Id(c[18]);
                man.Hands = Id(c[19]);
                man.Fpv = false;
                d.Squad.Add(man);
            }
            if (bad > 0)
                RevivalPlugin.L.LogWarning("Troops: " + bad + " unreadable line(s) in " + label + ".");
            Replace(order, label);
        }

        /// <summary>Swap in a new set, keeping the running schedule of landings
        /// that are still there under the same name and interval.</summary>
        static void Replace(List<Landing> fresh, string label)
        {
            for (int i = 0; i < fresh.Count; i++)
            {
                Landing old = Find(fresh[i].Name);
                if (old != null && old.IntervalMinHours == fresh[i].IntervalMinHours
                    && old.IntervalMaxHours == fresh[i].IntervalMaxHours)
                    fresh[i].NextSpawn = old.NextSpawn;
            }
            _landings.Clear();
            _landings.AddRange(fresh);
            RevivalPlugin.L.LogInfo("Troops: " + _landings.Count + " troop landing(s) from "
                + label + ".");
        }

        static Landing Find(string name)
        {
            for (int i = 0; i < _landings.Count; i++)
                if (_landings[i].Name == name) return _landings[i];
            return null;
        }

        static bool Finite(params float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i]) || Mathf.Abs(values[i]) > 5000f)
                    return false;
            return true;
        }

        static int Id(string s)
        {
            int n;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                && n > 0 && n < 100000 ? n : 0;
        }

        static float Num(string s)
        {
            float n;
            return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                ? n : float.NaN;
        }

        // ---------------------------------------------------------- map square

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
            return ((char)('A' + col)).ToString() + row.ToString();
        }

        static Vector2 WorldSize()
        {
            if (_worldLookedUp) return _worldSize;
            _worldLookedUp = true;
            try
            {
                Type t = RevivalPlugin.TypeByName("MapUIManager");
                FieldInfo f = t == null ? null : AccessTools.Field(t, "WORLD_SIZE");
                object v = f == null ? null : f.GetValue(null);
                if (v is Vector2)
                {
                    Vector2 ws = (Vector2)v;
                    if (ws.x > 1f && ws.y > 1f) _worldSize = ws;
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("Troops WorldSize: " + ex.Message); }
            return _worldSize;
        }

        static bool WorldUp()
        {
            try { return MapTools.LocalPlayer() != null; }
            catch { return false; }
        }

        static MethodInfo _masterGetter;
        static bool _masterLooked;

        internal static bool MasterClient()
        {
            try
            {
                if (!_masterLooked)
                {
                    _masterLooked = true;
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    if (photon != null) _masterGetter = AccessTools.PropertyGetter(photon, "isMasterClient");
                }
                if (_masterGetter == null) return true;
                object v = _masterGetter.Invoke(null, null);
                return v == null || (bool)v;
            }
            catch { return true; }
        }

        // ============================================================ network

        /// <summary>Photon events for what only the master computes: the banner
        /// and each NPC-vs-NPC shot, so every client sees and hears the same
        /// fight. Codes base, base+1 (banner inbound/landed, content = square),
        /// base+2 (shot, float[6] from/to). Deaths replicate through the game's
        /// own damage path and need nothing here.</summary>
        internal static class Net
        {
            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optType;
            static float _shotBudget;
            static float _shotBudgetAt;

            static int Base()
            {
                int b = CfgEventCode == null ? 160 : CfgEventCode.Value;
                return Mathf.Clamp(b, 0, 197);
            }

            internal static void EnsureHooked()
            {
                if (_hooked || _failed) return;
                try
                {
                    Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                    FieldInfo onEvent = photon == null ? null : AccessTools.Field(photon, "OnEventCall");
                    _raise = photon == null ? null : AccessTools.Method(photon, "RaiseEvent", null, null);
                    _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                    if (onEvent == null || _raise == null)
                    {
                        _failed = true;
                        RevivalPlugin.L.LogWarning("Troops net: RaiseEvent or OnEventCall missing - "
                            + "only the master sees banners and shots.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("Troops net hooked: event codes " + Base() + "-"
                        + (Base() + 2) + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("Troops net not hooked: " + ex);
                }
            }

            static void Raise(int offset, object content, bool reliable)
            {
                if (!_hooked) return;
                try
                {
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] { (byte)(Base() + offset), content, reliable, opts });
                }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Troops net send: " + ex.Message); }
            }

            internal static void SendBanner(int kind, string cell)
            {
                Raise(kind == 0 ? 0 : 1, cell, true);
            }

            /// <summary>At most 40 shot events a second across all fights.</summary>
            internal static void SendShot(Vector3 from, Vector3 to)
            {
                float now = Time.time;
                if (now - _shotBudgetAt >= 1f) { _shotBudgetAt = now; _shotBudget = 40f; }
                if (_shotBudget < 1f) return;
                _shotBudget -= 1f;
                Raise(2, new float[] { from.x, from.y, from.z, to.x, to.y, to.z }, false);
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                try
                {
                    int art = code - Base();
                    if (art < 0 || art > 2) return;
                    if (art == 2)
                    {
                        float[] f = content as float[];
                        if (f == null || f.Length != 6) return;
                        Vector3 from = new Vector3(f[0], f[1], f[2]);
                        Vector3 to = new Vector3(f[3], f[4], f[5]);
                        GameObject me = MapTools.LocalPlayer();
                        if (me == null || (me.transform.position - from).sqrMagnitude > 600f * 600f) return;
                        NpcWar.ShotEffect(from, to);
                        return;
                    }
                    string cell = content as string;
                    if (cell == null || cell.Length > 4) return;
                    Banner(art, cell);
                }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("Troops net receive: " + ex.Message); }
            }
        }
    }

    // ======================================================================
    // One helicopter run, driven on the master every frame:
    //   Cruise   in from ApproachDistance behind the landing zone, over the
    //            highest ground on the way, slowing in proportion to the
    //            remaining distance with the nose raised while it brakes
    //   Descend  straight down onto the precomputed gear height, softer near
    //            the ground
    //   Ground   the squad gets out beside the door; UnloadSeconds later
    //   Lift     straight up 20 m while turning back the way it came
    //   Depart   accelerate and climb away, then PhotonNetwork.Destroy
    // No per-frame raycasts: a downward ray would find the helicopter itself.
    // ======================================================================
    internal sealed class HeliFlight
    {
        enum Stage { Cruise, Descend, Ground, Lift, Depart }

        internal RevivalTroopInsertion.Landing Landing;
        internal GameObject Go;
        Vector3 _lz, _in, _out;
        float _yaw, _cruiseY, _exitY, _speed, _pitch, _roll;
        float _stageAt, _started;
        Stage _stage;
        bool _dropped;
        Vector3 _lastPos;

        const float HoverHeight = 14f;

        static MethodInfo _instantiate, _destroy;
        static bool _looked;

        internal static HeliFlight Launch(RevivalTroopInsertion.Landing d, Vector3 lz, float yaw)
        {
            if (!LookUp()) return null;
            HeliFlight f = new HeliFlight();
            f.Landing = d;
            f._lz = lz;
            f._yaw = yaw;
            float dist = RevivalTroopInsertion.CfgApproachDist == null ? 1500f
                : Mathf.Clamp(RevivalTroopInsertion.CfgApproachDist.Value, 300f, 3000f);
            f._in = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            f._out = -f._in;
            Vector3 start = lz - f._in * dist;
            float lift = RevivalTroopInsertion.CfgCruiseHeight == null ? 90f
                : Mathf.Clamp(RevivalTroopInsertion.CfgCruiseHeight.Value, 30f, 400f);
            f._cruiseY = HighestGround(start, lz) + lift;
            f._exitY = f._cruiseY;
            f._cruiseY = Mathf.Max(f._cruiseY, lz.y + HoverHeight + 20f);

            try
            {
                float scale = RevivalTroopInsertion.Scale();
                object[] data = new object[] { RevivalTroopInsertion.HeliMarker, scale };
                ParameterInfo[] ps = _instantiate.GetParameters();
                object group = Convert.ChangeType(0, ps[3].ParameterType);
                Vector3 at = new Vector3(start.x, f._cruiseY, start.z);
                f.Go = _instantiate.Invoke(null, new object[] {
                    RevivalTroopInsertion.HeliPrefab, at, Quaternion.LookRotation(f._in), group, data })
                    as GameObject;
                if (f.Go == null) return null;
                RevivalTroopInsertion.PrepareHeli(f.Go);
                // The vanilla mover must never take over: it idles while
                // startPosition is zero (HelicopterDummy.HelicopterToTargetMovement).
                Type dummy = RevivalPlugin.TypeByName("HelicopterDummy");
                Component mover = dummy == null ? null : f.Go.GetComponent(dummy);
                FieldInfo startField = dummy == null ? null : AccessTools.Field(dummy, "startPosition");
                if (mover != null && startField != null) startField.SetValue(mover, Vector3.zero);
                f.Go.transform.position = at;
                f._lastPos = at;
                f._speed = RevivalTroopInsertion.CfgHeliSpeed == null ? 55f
                    : Mathf.Clamp(RevivalTroopInsertion.CfgHeliSpeed.Value, 15f, 90f);
                f._started = f._stageAt = Time.time;
                return f;
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Troops: helicopter spawn failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                return null;
            }
        }

        static bool LookUp()
        {
            if (_looked) return _instantiate != null;
            _looked = true;
            Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
            if (photon == null) return false;
            foreach (MethodInfo m in photon.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "InstantiateSceneObject") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 5 && ps[0].ParameterType == typeof(string)
                    && ps[4].ParameterType == typeof(object[]))
                    _instantiate = m;
            }
            _destroy = AccessTools.Method(photon, "Destroy", new Type[] { typeof(GameObject) }, null);
            if (_instantiate == null)
                RevivalPlugin.L.LogWarning("Troops: PhotonNetwork.InstantiateSceneObject not found.");
            return _instantiate != null;
        }

        /// <summary>False when the run is over and the helicopter is gone.</summary>
        internal bool Tick()
        {
            if (Go == null)
            {
                if (!_dropped) DropNow();
                return false;
            }
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float now = Time.time;
            Transform tr = Go.transform;
            Vector3 pos = tr.position;
            Vector3 flatToLz = new Vector3(_lz.x - pos.x, 0f, _lz.z - pos.z);
            float targetPitch = 0f, targetRoll = 0f;
            float yaw = _yaw;

            if (now - _started > 300f)
            {
                if (!_dropped) DropNow();
                NetDestroy(Go);
                return false;
            }

            switch (_stage)
            {
                case Stage.Cruise:
                {
                    float d = flatToLz.magnitude;
                    float v = Mathf.Clamp(d * 0.2f, 2.5f, _speed);
                    float step = Mathf.Min(d, v * dt);
                    if (d > 0.01f) pos += flatToLz / d * step;
                    float glide = Mathf.InverseLerp(700f, 60f, d);
                    float wantY = Mathf.Lerp(_cruiseY, _lz.y + HoverHeight, glide * glide * (3f - 2f * glide));
                    pos.y = Mathf.MoveTowards(pos.y, wantY, 9f * dt);
                    // Braking: nose up in proportion to how hard it slows.
                    targetPitch = v < _speed - 0.5f ? -Mathf.Clamp((_speed - v) * 0.35f, 0f, 12f) : 3f;
                    if (d < 3f) targetPitch = 0f;
                    if (d < 0.3f && Mathf.Abs(pos.y - (_lz.y + HoverHeight)) < 1.5f) Next(Stage.Descend);
                    break;
                }
                case Stage.Descend:
                {
                    pos.x = _lz.x; pos.z = _lz.z;
                    float left = pos.y - _lz.y;
                    float rate = Mathf.Lerp(0.6f, 3.5f, Mathf.Clamp01(left / HoverHeight));
                    pos.y = Mathf.MoveTowards(pos.y, _lz.y, rate * dt);
                    if (pos.y - _lz.y < 0.02f) { pos.y = _lz.y; Next(Stage.Ground); }
                    break;
                }
                case Stage.Ground:
                {
                    pos = _lz;
                    if (!_dropped && now - _stageAt > 1.5f) DropNow();
                    float unload = RevivalTroopInsertion.CfgUnloadSeconds == null ? 12f
                        : Mathf.Clamp(RevivalTroopInsertion.CfgUnloadSeconds.Value, 3f, 60f);
                    if (_dropped && now - _stageAt > 1.5f + unload) Next(Stage.Lift);
                    break;
                }
                case Stage.Lift:
                {
                    float t = now - _stageAt;
                    float up = Mathf.Min(1f + t * 1.2f, 5f);
                    pos.y = Mathf.MoveTowards(pos.y, _lz.y + 20f, up * dt);
                    float turn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 2f) / 6f));
                    yaw = _yaw + 180f * turn;
                    targetRoll = Mathf.Sin(turn * Mathf.PI) * 8f;
                    if (pos.y >= _lz.y + 19.5f && turn >= 1f) Next(Stage.Depart);
                    break;
                }
                case Stage.Depart:
                {
                    float t = now - _stageAt;
                    float v = Mathf.Min(t * 5f, _speed);
                    pos += _out * v * dt;
                    pos.y = Mathf.MoveTowards(pos.y, _exitY, 6f * dt);
                    yaw = _yaw + 180f;
                    targetPitch = v < _speed ? 9f : 3f;   // nose down to accelerate
                    float gone = new Vector3(pos.x - _lz.x, 0f, pos.z - _lz.z).magnitude;
                    float dist = RevivalTroopInsertion.CfgApproachDist == null ? 1500f
                        : RevivalTroopInsertion.CfgApproachDist.Value;
                    if (gone > Mathf.Clamp(dist, 300f, 3000f))
                    {
                        NetDestroy(Go);
                        return false;
                    }
                    break;
                }
            }

            _pitch = Mathf.Lerp(_pitch, targetPitch, 1.5f * dt);
            _roll = Mathf.Lerp(_roll, targetRoll, 2f * dt);
            if (_stage == Stage.Ground) { _pitch = 0f; _roll = 0f; }
            tr.position = pos;
            tr.rotation = Quaternion.Euler(_pitch, yaw, _roll);
            _lastPos = pos;
            return true;
        }

        void Next(Stage s)
        {
            _stage = s;
            _stageAt = Time.time;
        }

        /// <summary>The door is on the left, forward of the middle. The men
        /// line up outside it, facing away from the fuselage.</summary>
        void DropNow()
        {
            _dropped = true;
            float k = RevivalTroopInsertion.K;
            Quaternion q = Quaternion.Euler(0f, _yaw, 0f);
            // A squad over eight men leaves in two files whose rows reach back
            // toward the helicopter (Crew.SquadOffset); step out far enough.
            int count = Mathf.Clamp(Landing.Count, 1, 16);
            float outside = 7f * k + (count > 8 ? ((count + 1) / 2 - 1) * 2f + 2f : 0f);
            Vector3 door = _lz + q * new Vector3(-outside, 0f, 2f * k);
            float y;
            if (RevivalTroopInsertion.GroundY(door, out y)) door.y = y;
            RevivalTroopInsertion.Drop(Landing, door, _yaw - 90f);
        }

        static float HighestGround(Vector3 a, Vector3 b)
        {
            float best = Mathf.Max(a.y, b.y);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 60f), 2, 60);
            for (int i = 0; i <= steps; i++)
            {
                float y;
                if (RevivalTroopInsertion.GroundY(Vector3.Lerp(a, b, (float)i / steps), out y))
                    best = Mathf.Max(best, y);
            }
            return best;
        }

        internal static void NetDestroy(GameObject go)
        {
            if (go == null) return;
            try
            {
                LookUp();
                if (_destroy != null) _destroy.Invoke(null, new object[] { go });
                else UnityEngine.Object.Destroy(go);
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("Troops: helicopter removal failed - "
                    + (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
                UnityEngine.Object.Destroy(go);
            }
        }
    }
}

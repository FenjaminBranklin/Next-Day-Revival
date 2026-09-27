using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

// =========================================================================
// No-fly zones (P6a), config [NoFly]. docs/ai/tasks/nofly-zones-p6a.md
//
// A zone is a circle or a polygon on one map (scene), a ceiling above the
// ground, an owning editor faction and its defenders. An aircraft (player
// Mi-8, An-2, a player's FPV or recon drone) that is airborne inside it with
// a player aboard and nobody of the owning faction aboard is a VIOLATOR:
//
//   1. warning - WarningSeconds: every client whose player is aboard shows the
//      HUD banner "NO FLY ZONE ... leave now" and beeps;
//   2. engaged - the master hands it to the zone's defenders:
//        - the zone's ZU-23 guns (Revival.Flak.cs, P4 API): ZoneDefence over the
//          zone once, then AssignTarget(violator) - the crew fires on it even
//          when it is not of a hostile faction;
//        - else a manned Gepard of the owning faction near the zone
//          (GepardCrew.Defends / the Feindlich hook: NoFly.Engaged);
//        - else scripted defensive fire: bursts of flak puffs that walk in on
//          the aircraft, fragments through GepardGun.Hit, puffs on every
//          client over event NetworkEventCode (197).
//      The banner turns red: "YOU ARE UNDER DEFENSIVE FIRE - leave the zone".
//
// The map marker is a thin, finely dashed line with a small "NO FLY ZONE"
// label - one quarter of a patrol route's stroke. The web editor draws the
// same zones in the same style from assets/editor/nofly.json (editor/nofly.js);
// python verify.py [35] keeps the two copies equal.
//
// Touches: Revival.MapInk.cs (Raster with a stroke width), Revival.Flak.cs
// (FlakFire.Airborne internal), RevivalGepardCrew.cs (Defends, Feindlich
// hook), RevivalPlugin.cs (BindConfig, Tick, Draw).
// =========================================================================

namespace NextDayRevival
{
    public static class NoFly
    {
        const string S = "NoFly";
        internal const float K = 2.8f;                  // world units per metre

        // The map style: 1024 map pixels (MapInk's frame). A patrol route is
        // 24 / 10 / 4.75 (MapInk.DashLength, GapLength, StrokeWidth).
        internal const float DashLength = 7f;
        internal const float GapLength = 5f;
        internal const float StrokeWidth = 1.2f;
        internal const string Label = "NO FLY ZONE";
        internal const int LabelSize = 10;
        internal static readonly Color InkColor = new Color(0.95f, 0.84f, 0.45f, 0.8f);   // #f2d673

        internal static ConfigEntry<bool> CfgEnabled, CfgHud, CfgSound, CfgMap, CfgScripted;
        internal static ConfigEntry<float> CfgWarning, CfgBurstInterval, CfgInitialMiss, CfgWalk,
            CfgMissFloor, CfgBurstRadius;
        internal static ConfigEntry<int> CfgPuffs, CfgHeliHits, CfgEventCode;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "No-fly zones: an aircraft of another faction that enters one is warned, then "
                + "engaged by the zone's AA guns, a Gepard of the zone's faction or scripted flak.");
            CfgHud = cfg.Bind(S, "WarningBanner", true,
                "The HUD banner for a player aboard an aircraft in a no-fly zone.");
            CfgSound = cfg.Bind(S, "WarningSound", true, "The warning beep with the banner.");
            CfgMap = cfg.Bind(S, "MapMarker", true,
                "The zones on the world map: a fine dashed line with a small NO FLY ZONE label.");
            CfgScripted = cfg.Bind(S, "ScriptedFire", true,
                "A zone with no gun and no Gepard defends itself with scripted flak bursts.");
            CfgWarning = cfg.Bind(S, "WarningSeconds", 5f,
                "Seconds between entering a zone (warning) and the first round.");
            CfgBurstInterval = cfg.Bind(S, "BurstInterval", 1.8f, "Scripted fire: seconds between bursts.");
            CfgPuffs = cfg.Bind(S, "PuffsPerBurst", 4, "Scripted fire: flak puffs per burst.");
            CfgInitialMiss = cfg.Bind(S, "InitialMiss", 35f,
                "Scripted fire: metres the first burst is off the aircraft.");
            CfgWalk = cfg.Bind(S, "WalkFactor", 0.55f,
                "Scripted fire: the miss is multiplied by this after every burst (walks in).");
            CfgMissFloor = cfg.Bind(S, "MissFloor", 3f, "Scripted fire: metres the best burst is still off.");
            CfgBurstRadius = cfg.Bind(S, "BurstRadius", 4f,
                "Scripted fire: metres from a puff within which fragments hit.");
            CfgHeliHits = cfg.Bind(S, "HeliHits", 10,
                "Scripted fire: fragment hits that bring a helicopter or An-2 down.");
            CfgEventCode = cfg.Bind(S, "NetworkEventCode", 197,
                "Photon event for the scripted flak puffs (0..199, not used by another channel).");
        }

        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }
        static float F(ConfigEntry<float> c, float d) { return c == null ? d : c.Value; }
        internal static bool On { get { return B(CfgEnabled); } }
        internal static void Log(string s) { RevivalPlugin.L.LogInfo("NoFly: " + s); }

        // ------------------------------------------------------------ zones

        internal sealed class Zone
        {
            public string Id, Name, Scene, Faction;
            public bool Circle;
            public Vector2 Centre;                // world x, z
            public float Radius;                  // circle; polygon: its bounding radius
            public Vector2[] Poly;                // polygon, world x, z; null for a circle
            public float Ceiling;                 // world units above the ground, 0 = none
            public string[] Guns;                 // Flak gun ids (P4 API)
            public float Reach = GepardReach;     // a Gepard of the faction this far beyond the edge defends
            public bool Scripted = true;          // scripted flak when no gun and no Gepard is up
            public Func<bool> Exists;             // null = always; else the zone (and its ring) only while true
            public Func<bool> Armed;              // null = always; else warned and engaged only while true
            public Func<string> Side;             // null = Faction fixed; else the owning faction, read each frame

            // master
            public readonly List<GepardGun.Contact> Air = new List<GepardGun.Contact>();
            public readonly List<Violator> In = new List<Violator>();
            public float NextScan, NextGunCheck;
            public string Assigned;               // what the guns were given, by name
            public GameObject AssignedGo;

            // map (every client): the dashes in MapInk's 1024 frame
            public List<MapInk.Dash> Dashes;
            public bool DashesEast;
        }

        internal sealed class Violator
        {
            public GepardGun.Contact C;
            public float Since, NextBurst;
            public bool Engaged;
            public float Miss;                    // world units
            public Vector3 LastVel;
            public int Bursts;
            public string By;                     // "guns", "Gepard", "scripted"
        }

        /// <summary>The zones. The same list as assets/editor/nofly.json -
        /// python verify.py [35] compares them.</summary>
        internal static readonly List<Zone> Zones = new List<Zone>();

        static NoFly()
        {
            // Point N12 = NPC_Settlement[Neutrals] (1446.6, 1703.2), the neutral
            // base (IsSafeSettlement, RE 39). The centre sits 22 u north of the
            // settlement object, on the middle of its buildings; 340 u holds the
            // settlement, its road and the edge of the wood round it.
            Zones.Add(Circle("N12", "Point N12", "GW_Scene_1", "neutral", 1446.6f, 1725f, 340f, 700f,
                             new string[0]));
            // The east military town (P6b, docs/ai/tasks/military-town-ring-nofly.md):
            // the perimeter fence MT-F1 (x 5452..5848, z 602..1198) plus 100 u,
            // corners cut by 100 u. Its west edge (x 5352) stays 532 u (190 m)
            // east of the airfield's fence (x 4820) and 562 u east of the
            // airfield's easternmost object. Only with the town on; owned by
            // the garrison's faction; defended by a Gepard standing IN the
            // town (the AA1 site) and nobody else - with it dead the sky over
            // the town is open.
            Zone mt = Polygon("MT", "Military town", "GW_Scene_1", "looter", new Vector2[] {
                new Vector2(5452f, 502f), new Vector2(5848f, 502f), new Vector2(5948f, 602f),
                new Vector2(5948f, 1198f), new Vector2(5848f, 1298f), new Vector2(5452f, 1298f),
                new Vector2(5352f, 1198f), new Vector2(5352f, 602f) }, 700f, new string[0]);
            mt.Reach = 0f;
            mt.Scripted = false;
            mt.Exists = MilitaryTown.NoFlyShown;
            mt.Armed = MilitaryTown.NoFlyArmed;
            mt.Side = MilitaryTown.Faction;
            Zones.Add(mt);
        }

        static Zone Circle(string id, string name, string scene, string faction,
                           float x, float z, float radius, float ceiling, string[] guns)
        {
            Zone zn = new Zone();
            zn.Id = id; zn.Name = name; zn.Scene = scene; zn.Faction = faction;
            zn.Circle = true; zn.Centre = new Vector2(x, z); zn.Radius = radius;
            zn.Ceiling = ceiling; zn.Guns = guns;
            return zn;
        }

        /// <summary>A polygon zone (world x, z corners in order).</summary>
        internal static Zone Polygon(string id, string name, string scene, string faction,
                                     Vector2[] points, float ceiling, string[] guns)
        {
            Zone zn = new Zone();
            zn.Id = id; zn.Name = name; zn.Scene = scene; zn.Faction = faction;
            zn.Poly = points; zn.Ceiling = ceiling; zn.Guns = guns;
            Vector2 c = Vector2.zero;
            for (int i = 0; i < points.Length; i++) c += points[i];
            zn.Centre = points.Length > 0 ? c / points.Length : c;
            for (int i = 0; i < points.Length; i++)
                zn.Radius = Mathf.Max(zn.Radius, (points[i] - zn.Centre).magnitude);
            return zn;
        }

        static bool Here(Zone zn)
        {
            return MapScene.Owns(zn.Scene) && (zn.Exists == null || zn.Exists());
        }

        /// <summary>Warned and engaged: here, and its defence (if it has a
        /// condition) is up.</summary>
        static bool Live(Zone zn) { return Here(zn) && (zn.Armed == null || zn.Armed()); }

        /// <summary>Inside the outline and under the ceiling.</summary>
        internal static bool Contains(Zone zn, Vector3 p)
        {
            Vector2 q = new Vector2(p.x, p.z);
            if (zn.Circle)
            {
                if ((q - zn.Centre).sqrMagnitude > zn.Radius * zn.Radius) return false;
            }
            else
            {
                bool inside = false;
                Vector2[] v = zn.Poly;
                for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
                    if ((v[i].y > q.y) != (v[j].y > q.y)
                        && q.x < (v[j].x - v[i].x) * (q.y - v[i].y) / (v[j].y - v[i].y) + v[i].x)
                        inside = !inside;
                if (!inside) return false;
            }
            if (zn.Ceiling <= 0f) return true;
            float ground;
            if (!EastWorld.TerrainHeight(p, out ground)) return true;
            return p.y - ground <= zn.Ceiling;
        }

        static Vector3 Ground(Zone zn)
        {
            Vector3 c = new Vector3(zn.Centre.x, 0f, zn.Centre.y);
            float y;
            if (EastWorld.TerrainHeight(c, out y)) c.y = y;
            return c;
        }

        /// <summary>Who is aboard: <paramref name="anyone"/> a player (a
        /// drone: its pilot), <paramref name="owner"/> a player of the zone's
        /// own faction.</summary>
        static void Aboard(GepardGun.Contact c, string faction, out bool anyone, out bool owner)
        {
            anyone = false;
            owner = false;
            string own = Fraktion.Eigene(faction);
            if (c.Kind == 2 || c.Kind == 3)
            {
                GameObject pilot = Crocodile.PlayerByActor(c.Actor);
                if (pilot == null) return;
                anyone = true;
                owner = Fraktion.Spielerseite(pilot) == own;
                return;
            }
            float reach = Mathf.Max(4f, c.Radius * 1.5f) + 2f;
            List<GameObject> players = GepardCrew.Spieler();
            for (int i = 0; i < players.Count; i++)
            {
                GameObject go = players[i];
                if (go == null || (go.transform.position - c.Pos).sqrMagnitude > reach * reach) continue;
                anyone = true;
                if (Fraktion.Spielerseite(go) == own) owner = true;
            }
        }

        // ------------------------------------------------------------ API

        /// <summary>For a Gepard of <paramref name="side"/> at
        /// <paramref name="from"/>: is this aircraft an engaged violator of a
        /// zone of that faction within the Gepard's reach? (RevivalGepardCrew
        /// Feindlich - the Gepard then fires on it like on a hostile.)</summary>
        internal static bool Engaged(GameObject go, string side, Vector3 from)
        {
            if (go == null || !On) return false;
            string own = Fraktion.Eigene(side);
            for (int i = 0; i < Zones.Count; i++)
            {
                Zone zn = Zones[i];
                if (Fraktion.Eigene(zn.Faction) != own || !Live(zn)) continue;
                if (Flat(from, zn) > zn.Radius + zn.Reach) continue;
                for (int j = 0; j < zn.In.Count; j++)
                    if (zn.In[j].Engaged && zn.In[j].C.Go == go) return true;
            }
            return false;
        }

        const float GepardReach = 1500f;                // world units beyond the edge

        static float Flat(Vector3 p, Zone zn)
        {
            return (new Vector2(p.x, p.z) - zn.Centre).magnitude;
        }

        // ------------------------------------------------------------ frame

        internal static void Tick()
        {
            if (!On)
            {
                _local.Clear();
                return;
            }
            try
            {
                NoFlyNet.EnsureHooked();
                for (int i = 0; i < Zones.Count; i++)
                    if (Zones[i].Side != null) Zones[i].Faction = Zones[i].Side();
                NoFlyFire.Pending();
                FlakSound.Tick();
                LocalTick();
                if (Crocodile.IsMaster()) MasterTick();
                else
                    for (int i = 0; i < Zones.Count; i++) Zones[i].In.Clear();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("NoFly: " + ex);
            }
        }

        // ------------------------------------------------------------ master

        static void MasterTick()
        {
            float now = Time.time;
            for (int z = 0; z < Zones.Count; z++)
            {
                Zone zn = Zones[z];
                if (!Live(zn)) { zn.In.Clear(); continue; }
                FlakFire.FollowAll(zn.Air, Time.deltaTime, 2f);
                if (now >= zn.NextScan)
                {
                    zn.NextScan = now + 0.25f;
                    Scan(zn, now);
                }
                Engage(zn, now);
            }
        }

        static void Scan(Zone zn, float now)
        {
            Vector3 eye = Ground(zn);
            FlakFire.Collect(zn.Air, eye, zn.Radius + Mathf.Max(zn.Ceiling, 700f) + 200f);
            for (int i = 0; i < zn.Air.Count; i++)
            {
                GepardGun.Contact c = zn.Air[i];
                if (c.Go == null) continue;
                bool anyone, owner;
                Aboard(c, zn.Faction, out anyone, out owner);
                bool violating = anyone && !owner && Contains(zn, c.Pos) && FlakFire.Airborne(c);
                Violator v = Find(zn, c.Go);
                if (violating && v == null)
                {
                    v = new Violator();
                    v.C = c;
                    v.Since = now;
                    v.Miss = Mathf.Max(0f, F(CfgInitialMiss, 35f)) * K;
                    zn.In.Add(v);
                    Log(zn.Id + ": " + c.Go.name + " entered (kind " + c.Kind + ") - warned, "
                        + Seconds().ToString("0.#", CultureInfo.InvariantCulture) + " s to leave.");
                }
                else if (!violating && v != null)
                {
                    zn.In.Remove(v);
                    Log(zn.Id + ": " + c.Go.name + (owner ? " has the zone's faction aboard." : " left the zone."));
                }
            }
            for (int i = zn.In.Count - 1; i >= 0; i--)
                if (zn.In[i].C.Go == null || !zn.Air.Contains(zn.In[i].C))
                {
                    Log(zn.Id + ": a violator is gone.");
                    zn.In.RemoveAt(i);
                }
        }

        static Violator Find(Zone zn, GameObject go)
        {
            for (int i = 0; i < zn.In.Count; i++) if (zn.In[i].C.Go == go) return zn.In[i];
            return null;
        }

        internal static float Seconds() { return Mathf.Max(0f, F(CfgWarning, 5f)); }

        static void Engage(Zone zn, float now)
        {
            bool guns = GunsReady(zn, now);
            Vector3 ground = Ground(zn);
            bool gepard = !guns && GepardCrew.Defends(zn.Faction, ground, zn.Radius + zn.Reach);
            bool scripted = zn.Scripted && B(CfgScripted);
            Violator first = null;
            for (int i = 0; i < zn.In.Count; i++)
            {
                Violator v = zn.In[i];
                if (v.C.Go == null) continue;             // destroyed; the next scan drops it
                if (!v.Engaged && now - v.Since >= Seconds())
                {
                    v.Engaged = true;
                    v.NextBurst = now;
                }
                if (!v.Engaged) continue;
                string by = guns ? "guns" : gepard ? "Gepard" : scripted ? "scripted fire" : "nobody";
                if (by != v.By)
                {
                    v.By = by;
                    Log(zn.Id + ": engaging " + v.C.Go.name + " - " + by + ".");
                }
                if (first == null) first = v;
                if (!guns && !gepard && scripted) NoFlyFire.Burst(zn, v, now, ground);
            }
            // The guns lay on one violator at a time: the first engaged one.
            GameObject want = guns && first != null ? first.C.Go : null;
            if (want != zn.AssignedGo)
            {
                for (int g = 0; g < zn.Guns.Length; g++)
                {
                    if (want != null) Flak.AssignTarget(zn.Guns[g], want);
                    else if (zn.AssignedGo != null || zn.Assigned != null) Flak.ClearTarget(zn.Guns[g]);
                }
                zn.AssignedGo = want;
                zn.Assigned = want == null ? null : want.name;
            }
        }

        /// <summary>Any of the zone's ZU-23s there and crewed (or manned by a
        /// player)? Every few seconds the guns are (again) put in zone
        /// defence - a new master starts them in WeaponsFree.</summary>
        static bool GunsReady(Zone zn, float now)
        {
            if (zn.Guns == null || zn.Guns.Length == 0) return false;
            bool ready = false, check = now >= zn.NextGunCheck;
            if (check) zn.NextGunCheck = now + 3f;
            for (int g = 0; g < zn.Guns.Length; g++)
            {
                FlakGunInfo info = Flak.Get(zn.Guns[g]);
                if (info == null) continue;
                if (info.CrewAlive > 0 || info.PlayerManned) ready = true;
                if (check && info.Mode != FlakMode.ZoneDefence)
                {
                    Vector3 c = Ground(zn);
                    Flak.ZoneDefence(zn.Guns[g], c, zn.Radius, zn.Ceiling, false);
                }
            }
            return ready;
        }

        // ------------------------------------------------------------ every client: the banner

        internal sealed class Local
        {
            public Zone Zone;
            public float Since, Last;
        }

        static readonly List<Local> _local = new List<Local>();
        static readonly List<GepardGun.Contact> _near = new List<GepardGun.Contact>();
        static float _nextLocal, _nextBeep;

        /// <summary>Is the local player aboard an airborne aircraft (or
        /// flying his FPV drone) inside a zone of another faction? Every
        /// client keeps its own clock from the moment of entering - the
        /// master's is the same rule, so both reach "engaged" together.</summary>
        static void LocalTick()
        {
            float now = Time.time;
            if (now < _nextLocal) return;
            _nextLocal = now + 0.25f;
            GameObject me = MapTools.LocalPlayer();
            List<Vector3> at = new List<Vector3>();
            List<GepardGun.Contact> by = new List<GepardGun.Contact>();
            if (me != null)
            {
                Vector3 p = me.transform.position;
                FlakFire.Collect(_near, p, 120f);
                for (int i = 0; i < _near.Count; i++)
                {
                    GepardGun.Contact c = _near[i];
                    if (c.Go == null || c.Kind == 2 || c.Kind == 3) continue;
                    c.Pos = c.Go.transform.TransformPoint(c.LocalCentre);
                    float reach = Mathf.Max(4f, c.Radius * 1.5f) + 2f;
                    if ((p - c.Pos).sqrMagnitude > reach * reach || !FlakFire.Airborne(c)) continue;
                    at.Add(c.Pos);
                    by.Add(c);
                }
            }
            if (Drone.Flying) { at.Add(Drone.Position); by.Add(null); }

            for (int z = 0; z < Zones.Count; z++)
            {
                Zone zn = Zones[z];
                bool inside = false;
                if (Live(zn) && me != null && Fraktion.Spielerseite(me) != Fraktion.Eigene(zn.Faction))
                    for (int i = 0; i < at.Count && !inside; i++)
                    {
                        if (!Contains(zn, at[i])) continue;
                        bool anyone = true, owner = false;
                        if (by[i] != null) Aboard(by[i], zn.Faction, out anyone, out owner);
                        inside = !owner;
                    }
                Local l = null;
                for (int i = 0; i < _local.Count; i++) if (_local[i].Zone == zn) l = _local[i];
                if (inside)
                {
                    if (l == null)
                    {
                        l = new Local();
                        l.Zone = zn;
                        l.Since = now;
                        _local.Add(l);
                        _nextBeep = 0f;
                        Log("you entered no-fly zone " + zn.Id + " (" + zn.Name + ", " + zn.Faction + ").");
                    }
                    l.Last = now;
                }
                else if (l != null && now - l.Last > 1.5f)
                {
                    _local.Remove(l);
                    _leftAt = now;
                    _leftName = zn.Name;
                    Log("you left no-fly zone " + zn.Id + ".");
                }
            }

            if (_local.Count > 0 && B(CfgSound) && now >= _nextBeep)
            {
                bool engaged = now - _local[0].Since >= Seconds();
                NoFlyBeep.Play(engaged);
                _nextBeep = now + (engaged ? 0.6f : 1.2f);
            }
        }

        static float _leftAt = -100f;
        static string _leftName;
        static GUIStyle _bannerStyle, _labelStyle;

        /// <summary>The HUD banner (OnGUI): top centre, amber while warned,
        /// red and pulsing once the zone's defence fires.</summary>
        internal static void Draw()
        {
            if (!On) { MapInkLayer.Hide("nofly"); return; }
            DrawMap();
            if (!B(CfgHud) || Event.current == null || Event.current.type != EventType.Repaint) return;
            float now = Time.time;
            string head = null, line = null;
            Color col = Color.white;
            if (_local.Count > 0)
            {
                Local l = _local[0];
                float left = Seconds() - (now - l.Since);
                string where = l.Zone.Name + " - " + l.Zone.Faction + " airspace";
                if (left > 0f)
                {
                    head = "NO FLY ZONE: " + where;
                    line = "Leave the zone now - defensive fire in "
                        + Mathf.CeilToInt(left).ToString(CultureInfo.InvariantCulture) + " s";
                    col = new Color(1f, 0.78f, 0.25f, 1f);
                }
                else
                {
                    head = "NO FLY ZONE: " + where;
                    line = "You are under defensive fire - leave the zone";
                    float pulse = 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(now * 5f));
                    col = new Color(1f, 0.25f, 0.2f, pulse);
                }
            }
            else if (now - _leftAt < 3f)
            {
                head = "Left the no-fly zone";
                line = _leftName;
                col = new Color(0.7f, 0.95f, 0.7f, 1f - (now - _leftAt) / 3f);
            }
            if (head == null) return;
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label);
                _bannerStyle.richText = true;
                _bannerStyle.fontSize = 20;
            }
            Color old = GUI.color;
            try
            {
                float w = 720f, x = (Screen.width - w) * 0.5f, y = Screen.height * 0.16f;
                GUI.color = new Color(0f, 0f, 0f, 0.55f * col.a);
                GUI.DrawTexture(new Rect(x, y, w, 64f), Texture2D.whiteTexture);
                GUI.color = col;
                Centred("<b>" + head + "</b>", 20, x + w * 0.5f, y + 4f);
                Centred(line, 16, x + w * 0.5f, y + 34f);
            }
            finally { GUI.color = old; }
        }

        /// <summary>One line of the banner, centred on <paramref name="cx"/>.</summary>
        static void Centred(string text, int size, float cx, float y)
        {
            _bannerStyle.fontSize = size;
            Vector2 sz = _bannerStyle.CalcSize(new GUIContent(text));
            GUI.Label(new Rect(cx - sz.x * 0.5f, y, sz.x, sz.y), text, _bannerStyle);
        }

        // ------------------------------------------------------------ the map

        /// <summary>Every zone of the loaded map as a fine dashed outline in
        /// the native map ink (clipped and faded with the map), with a small
        /// "NO FLY ZONE" label on its north edge.</summary>
        static void DrawMap()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!B(CfgMap)) { MapInkLayer.Hide("nofly"); return; }
            MapInkLayer layer = null;
            try
            {
                Component manager, texture;
                Camera camera;
                Vector2 world, map;
                if (!MapTools.Context(out manager, out texture, out camera, out world, out map)) return;
                Rect full;
                if (!MapTools.MapScreenRect(texture, camera, out full)) return;
                Rect view;
                if (!MapTools.MapViewportRect(texture, camera, out view)) view = full;
                for (int z = 0; z < Zones.Count; z++)
                {
                    Zone zn = Zones[z];
                    if (!Here(zn)) continue;
                    if (zn.Dashes == null || zn.DashesEast != EastWorld.Extends) BuildDashes(zn);
                    if (zn.Dashes.Count == 0) continue;
                    if (layer == null)
                    {
                        layer = MapInkLayer.Begin("nofly", texture);
                        if (layer == null) return;
                    }
                    for (int i = 0; i < zn.Dashes.Count; i++)
                        layer.Draw(zn.Dashes[i].Bounds, zn.Dashes[i].Texture, InkColor);
                    DrawLabel(zn, texture, camera, world, map, full, view);
                }
            }
            catch (Exception ex)
            {
                if (RevivalPlugin.L != null) RevivalPlugin.L.LogWarning("NoFly map: " + ex.Message);
            }
            finally
            {
                if (layer != null) layer.End();
                else MapInkLayer.Hide("nofly");
            }
        }

        /// <summary>The label just inside the zone's northernmost point.</summary>
        static void DrawLabel(Zone zn, Component texture, Camera camera, Vector2 world, Vector2 map,
                              Rect full, Rect view)
        {
            Vector2 top = zn.Centre + new Vector2(0f, zn.Radius);
            if (!zn.Circle)
            {
                top = zn.Poly[0];
                for (int i = 1; i < zn.Poly.Length; i++) if (zn.Poly[i].y > top.y) top = zn.Poly[i];
                top.x = zn.Centre.x;
            }
            Vector2 gui;
            if (!MapTools.WorldToGui(new Vector3(top.x, 0f, top.y), texture, camera, world, map, out gui)) return;
            gui = MapProject.OnPicture(gui, full);
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.richText = true;
                _labelStyle.fontSize = LabelSize;
            }
            string text = "<b>" + Label + "</b>";
            Vector2 size = _labelStyle.CalcSize(new GUIContent(text));
            Rect r = new Rect(gui.x - size.x * 0.5f, gui.y + 2f, size.x, size.y);
            if (!view.Contains(new Vector2(r.xMin, r.yMin)) || !view.Contains(new Vector2(r.xMax, r.yMax))) return;
            Color old = GUI.color;
            try
            {
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _labelStyle);
                GUI.color = InkColor;
                GUI.Label(r, text, _labelStyle);
            }
            finally { GUI.color = old; }
        }

        /// <summary>The outline in MapInk's artwork frame, cut into dashes of
        /// the fine style - MapInk.Get's dash walk with this file's numbers,
        /// kept here so the route overlay's cache never retires them.</summary>
        static void BuildDashes(Zone zn)
        {
            if (zn.Dashes != null)
                for (int i = 0; i < zn.Dashes.Count; i++)
                    if (zn.Dashes[i].Texture != null) UnityEngine.Object.Destroy(zn.Dashes[i].Texture);
            zn.Dashes = new List<MapInk.Dash>();
            zn.DashesEast = EastWorld.Extends;
            List<Vector2> p = new List<Vector2>();
            foreach (Vector2 w in Outline(zn)) p.Add(MapInk.Artwork(new Vector3(w.x, 0f, w.y)));
            p.Add(p[0]);
            float[] arc = new float[p.Count];
            for (int i = 1; i < p.Count; i++) arc[i] = arc[i - 1] + (p[i] - p[i - 1]).magnitude;
            float total = arc[arc.Length - 1], period = DashLength + GapLength;
            int count = Mathf.FloorToInt(total / period);
            if (count < 3) return;
            period = total / count;                       // the loop closes on a full gap
            float dash = period * DashLength / (DashLength + GapLength);
            float outX = EastWorld.Extends ? 0.5f : 1f;   // MapInk.OutX: the east frame is 2048 wide
            for (int k = 0; k < count; k++)
            {
                List<Vector2> samples = new List<Vector2>();
                int at = 1;
                for (int j = 0; j <= 16; j++)
                {
                    float d = k * period + j * (dash / 16f);
                    while (at < arc.Length - 1 && arc[at] < d) at++;
                    float t = arc[at] - arc[at - 1] < .0001f ? 0f
                        : Mathf.Clamp01((d - arc[at - 1]) / (arc[at] - arc[at - 1]));
                    samples.Add(Vector2.Lerp(p[at - 1], p[at], t));
                }
                MapInk.Dash d2 = MapInk.Raster(samples, StrokeWidth);
                if (outX != 1f)
                {
                    d2.Bounds = new Rect(d2.Bounds.x * outX, d2.Bounds.y, d2.Bounds.width * outX, d2.Bounds.height);
                    d2.Mid = new Vector2(d2.Mid.x * outX, d2.Mid.y);
                }
                zn.Dashes.Add(d2);
            }
        }

        /// <summary>The outline in world x, z: a circle every ~6 u, a
        /// polygon every ~6 u along each side.</summary>
        internal static List<Vector2> Outline(Zone zn)
        {
            List<Vector2> o = new List<Vector2>();
            if (zn.Circle)
            {
                int n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * zn.Radius / 6f), 24, 720);
                for (int i = 0; i < n; i++)
                {
                    float a = i * 2f * Mathf.PI / n;
                    o.Add(zn.Centre + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * zn.Radius);
                }
                return o;
            }
            for (int i = 0; i < zn.Poly.Length; i++)
            {
                Vector2 a = zn.Poly[i], b = zn.Poly[(i + 1) % zn.Poly.Length];
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 6f));
                for (int j = 0; j < n; j++) o.Add(Vector2.Lerp(a, b, j / (float)n));
            }
            return o;
        }
    }

    // =====================================================================
    /// <summary>Scripted defensive fire: bursts of flak puffs round an
    /// engaged violator when the zone has no gun and no Gepard. The miss
    /// starts at InitialMiss and walks in by WalkFactor per burst to
    /// MissFloor; a straight flight is brought down after a few bursts, a
    /// hard turn (the lead goes wrong) throws the puffs off again.</summary>
    internal static class NoFlyFire
    {
        struct Puff { public float At; public Vector3 Pos; }
        static readonly List<Puff> _pending = new List<Puff>();

        internal static void Burst(NoFly.Zone zn, NoFly.Violator v, float now, Vector3 ground)
        {
            if (now < v.NextBurst) return;
            GepardGun.Contact c = v.C;
            if (c.Go == null) return;
            float interval = Mathf.Max(0.3f, NoFly.CfgBurstInterval == null ? 1.8f : NoFly.CfgBurstInterval.Value);
            v.NextBurst = now + interval * UnityEngine.Random.Range(0.85f, 1.15f);

            // The lead: position one fuze time ahead on the estimated velocity.
            Vector3 vel = c.Vel;
            float tof = Vector3.Distance(ground, c.Pos) / (900f * NoFly.K);
            Vector3 aim = c.Pos + vel * tof;
            float walk = Mathf.Clamp(NoFly.CfgWalk == null ? 0.55f : NoFly.CfgWalk.Value, 0.05f, 1f);
            float floor = Mathf.Max(0f, NoFly.CfgMissFloor == null ? 3f : NoFly.CfgMissFloor.Value) * NoFly.K;
            float radius = Mathf.Max(0.5f, NoFly.CfgBurstRadius == null ? 4f : NoFly.CfgBurstRadius.Value) * NoFly.K;
            int puffs = Mathf.Clamp(NoFly.CfgPuffs == null ? 4 : NoFly.CfgPuffs.Value, 1, 12);
            int hits = Mathf.Max(1, NoFly.CfgHeliHits == null ? 10 : NoFly.CfgHeliHits.Value);

            // A change of velocity since the last burst puts the miss back up.
            Vector3 dv = v.Bursts == 0 ? Vector3.zero : vel - v.LastVel;
            v.LastVel = vel;
            v.Miss = Mathf.Max(floor, v.Miss * walk + dv.magnitude * tof * 0.8f);
            Vector3 off = UnityEngine.Random.onUnitSphere * v.Miss;
            float[] data = new float[3 + puffs * 3];
            data[0] = NoFlyNet.Burst;
            data[1] = NoFly.Zones.IndexOf(zn);
            data[2] = puffs;
            for (int i = 0; i < puffs; i++)
            {
                Vector3 p = aim + off + UnityEngine.Random.insideUnitSphere * (radius * 1.2f + v.Miss * 0.25f);
                data[3 + i * 3] = p.x; data[4 + i * 3] = p.y; data[5 + i * 3] = p.z;
                Schedule(p, i);
                // Fragments: a hit when the puff is within the burst radius of
                // the aircraft's body where it will be when the puff goes off.
                Vector3 there = c.Pos + vel * (tof + i * 0.12f);
                if (Vector3.Distance(p, there) - c.Radius <= radius)
                    GepardGun.Hit(c, there, (there - ground).normalized, true, hits);
            }
            v.Bursts++;
            FlakSound.Report(ground);
            NoFlyNet.Send(data);
        }

        /// <summary>A burst's puffs one after another, 0.12 s apart.</summary>
        internal static void Schedule(Vector3 p, int i)
        {
            Puff q;
            q.At = Time.time + i * 0.12f;
            q.Pos = p;
            if (_pending.Count < 96) _pending.Add(q);
        }

        internal static void Pending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pending[i].At) continue;
                GepardFx.Flak(_pending[i].Pos);
                FlakSound.Burst(_pending[i].Pos);
                _pending.RemoveAt(i);
            }
        }
    }

    // =====================================================================
    /// <summary>One Photon event ([NoFly] NetworkEventCode, 197):
    /// { 1, zone, n, x1, y1, z1, ... } - a scripted burst's puffs, from the
    /// master to everyone else (the damage is the master's).</summary>
    internal static class NoFlyNet
    {
        internal const int Burst = 1;
        static bool _hooked, _failed;
        static MethodInfo _raise;
        static Type _optType;

        static int Code() { return NoFly.CfgEventCode == null ? 197 : NoFly.CfgEventCode.Value; }

        internal static void EnsureHooked()
        {
            if (_hooked || _failed) return;
            try
            {
                int code = Code();
                int drone = RevivalPlugin.CfgDroneEventCode.Value;
                bool taken = (code >= drone && code <= drone + 4)
                    || code == RevivalPlugin.CfgTurretEventCode.Value
                    || (RevivalPlugin.CfgAdminEventCode != null && code == RevivalPlugin.CfgAdminEventCode.Value)
                    || (RevivalPlugin.CfgPatrolCrewDroneEventCode != null
                        && code == RevivalPlugin.CfgPatrolCrewDroneEventCode.Value)
                    || (Flak.CfgEventCode != null && code == Flak.CfgEventCode.Value)
                    || (Gepard.CfgEventCode != null && code == Gepard.CfgEventCode.Value)
                    || (PlayerHeli.CfgEventCode != null && code >= PlayerHeli.CfgEventCode.Value
                        && code <= PlayerHeli.CfgEventCode.Value + 5)
                    || (PlayerAn2.CfgEventCode != null && code >= PlayerAn2.CfgEventCode.Value
                        && code <= PlayerAn2.CfgEventCode.Value + 5)
                    || (RevivalTroopInsertion.CfgEventCode != null && code >= RevivalTroopInsertion.CfgEventCode.Value
                        && code <= RevivalTroopInsertion.CfgEventCode.Value + 2)
                    || (DroneGear.CfgSurvEventCode != null && code >= DroneGear.CfgSurvEventCode.Value
                        && code <= DroneGear.CfgSurvEventCode.Value + 3)
                    || code == 164 || (code >= 190 && code <= 196);
                if (code < 0 || code > 199 || taken)
                    throw new Exception("event code " + code + " is outside 0..199 or overlaps another channel");
                Type photon = RevivalPlugin.TypeByName("PhotonNetwork");
                if (photon == null) throw new Exception("PhotonNetwork missing");
                _raise = AccessTools.Method(photon, "RaiseEvent", null, null);
                FieldInfo onEvent = AccessTools.Field(photon, "OnEventCall");
                _optType = RevivalPlugin.TypeByName("RaiseEventOptions");
                if (_raise == null || onEvent == null) throw new Exception("reflection path incomplete");
                MethodInfo mine = typeof(NoFlyNet).GetMethod("OnPhotonEvent", BindingFlags.Public | BindingFlags.Static);
                Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                onEvent.SetValue(null, Delegate.Combine(onEvent.GetValue(null) as Delegate, handler));
                _hooked = true;
                GepardNet.EnsureHooked();     // a helicopter kill travels on the Gepard's event
                NoFly.Log("event code " + code + " hooked.");
            }
            catch (Exception ex)
            {
                _failed = true;
                RevivalPlugin.L.LogError("NoFly network not hooked - other players do not see the "
                    + "scripted flak: " + ex.Message);
            }
        }

        internal static void Send(float[] data)
        {
            if (!_hooked) return;
            try
            {
                object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                _raise.Invoke(null, new object[] { (byte)Code(), data, false, opts });
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NoFly network send: " + ex.Message); }
        }

        public static void OnPhotonEvent(byte code, object content, int sender)
        {
            if (code != (byte)Code() || !NoFly.On) return;
            try
            {
                float[] f = content as float[];
                if (f == null || f.Length < 3 || Mathf.RoundToInt(f[0]) != Burst) return;
                for (int i = 0; i < f.Length; i++)
                    if (float.IsNaN(f[i]) || float.IsInfinity(f[i])) return;
                int n = Mathf.Clamp(Mathf.RoundToInt(f[2]), 0, 12);
                if (f.Length < 3 + n * 3) return;
                for (int i = 0; i < n; i++)
                    NoFlyFire.Schedule(new Vector3(f[3 + i * 3], f[4 + i * 3], f[5 + i * 3]), i);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NoFly network receive: " + ex.Message); }
        }
    }

    // =====================================================================
    /// <summary>The warning tone, 2D: two short beeps at 880 / 1320 Hz
    /// while warned, one higher, harsher beep once engaged - all well inside
    /// what laptop speakers play (RE: a sound can be correct and inaudible).</summary>
    internal static class NoFlyBeep
    {
        static AudioClip _warn, _fire;
        static AudioSource _src;

        internal static void Play(bool engaged)
        {
            try
            {
                if (_src == null)
                {
                    GameObject go = new GameObject("NDR NoFly beep");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _src = go.AddComponent<AudioSource>();
                    _src.playOnAwake = false;
                    _src.spatialBlend = 0f;
                    _src.volume = 0.55f;
                }
                if (_warn == null) _warn = Make("nofly-warn", new float[] { 880f, 1320f }, 0.11f, 0.06f, false);
                if (_fire == null) _fire = Make("nofly-fire", new float[] { 1480f }, 0.18f, 0f, true);
                _src.PlayOneShot(engaged ? _fire : _warn);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NoFly beep: " + ex.Message); }
        }

        static AudioClip Make(string name, float[] tones, float len, float gap, bool harsh)
        {
            const int rate = 22050;
            int one = Mathf.RoundToInt(len * rate), pause = Mathf.RoundToInt(gap * rate);
            float[] s = new float[tones.Length * (one + pause)];
            for (int t = 0; t < tones.Length; t++)
                for (int i = 0; i < one; i++)
                {
                    float env = Mathf.Min(1f, Mathf.Min(i, one - i) / (0.008f * rate));
                    float ph = 2f * Mathf.PI * tones[t] * i / rate;
                    float v = Mathf.Sin(ph);
                    if (harsh) v = Mathf.Clamp(v * 2.5f, -1f, 1f) * 0.7f + 0.3f * Mathf.Sin(ph * 0.5f);
                    s[t * (one + pause) + i] = v * env * 0.8f;
                }
            AudioClip clip = AudioClip.Create(name, s.Length, 1, rate, false);
            clip.SetData(s, 0);
            return clip;
        }
    }
}

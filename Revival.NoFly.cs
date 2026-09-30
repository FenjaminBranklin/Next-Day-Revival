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
// a hostile pilot is a VIOLATOR; passengers never override pilot IFF:
//
//   1. warning - WarningSeconds: every client whose player is aboard shows the
//      HUD banner "NO FLY ZONE ... leave now" and beeps;
//   2. engaged - the master hands it to the zone's defenders:
//        - the zone's 52-K guns (Revival.Flak.cs, P4 API); AF/MT guns keep
//          their radar orders and acquire hostile pilots independently;
//        - else a manned Gepard of the owning faction near the zone
//          (GepardCrew.Defends / the Feindlich hook: NoFly.Engaged);
//        - else NOBODY. There is no scripted fire any more (Q2, after the
//          6.57.0 test): a zone is defended only by real guns, whose rounds
//          walk in burst by burst and hit through the aircraft's own damage
//          model. A zone whose defenders are dead or absent is open sky.
//      The banner turns red: "YOU ARE UNDER DEFENSIVE FIRE - leave the zone"
//      while a defender is up, and stays amber ("no air defence answers")
//      while none is.
//
// The map marker (B4) is a fine, evenly dashed red ring with a small "NO FLY
// ZONE" label just outside its top edge, no fill. The web editor draws the
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

        // The map style in screen pixels, independent of map zoom (B4): fine,
        // even dashes like the vanilla settlement ring, thinner than a patrol
        // route, in the native TargetAreaMarkerCut red (#ca2020) toned down
        // to 80 % alpha. No fill: a hatch muddied the map under it.
        internal const float DashLength = 14f;
        internal const float GapLength = 6f;
        internal const float StrokeWidth = 2.5f;
        internal const string Label = "NO FLY ZONE";
        internal const int LabelSize = 10;
        internal const float InkAlpha = 0.8f;
        internal static readonly Color InkColor = new Color(202f / 255f, 32f / 255f, 32f / 255f, InkAlpha); // #ca2020

        internal static ConfigEntry<bool> CfgEnabled, CfgHud, CfgSound, CfgMap;
        internal static ConfigEntry<float> CfgWarning;

        public static void BindConfig(ConfigFile cfg)
        {
            CfgEnabled = cfg.Bind(S, "Enabled", true,
                "No-fly zones: an aircraft of another faction that enters one is warned, then "
                + "engaged by the zone's AA guns or a Gepard of the zone's faction (real guns only).");
            CfgHud = cfg.Bind(S, "WarningBanner", true,
                "The HUD banner for a player aboard an aircraft in a no-fly zone.");
            CfgSound = cfg.Bind(S, "WarningSound", true, "The warning beep with the banner.");
            CfgMap = cfg.Bind(S, "MapMarker", true,
                "The zones on the world map: a fine red dashed ring with a small NO FLY ZONE label above it.");
            CfgWarning = cfg.Bind(S, "WarningSeconds", 5f,
                "Seconds between entering a zone (warning) and the first round.");
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
            public Vector2 DashesSize;
        }

        internal sealed class Violator
        {
            public GepardGun.Contact C;
            public float Since, NextBurst;
            public bool Engaged;
            public string By;                     // "guns", "Gepard", "nobody"
        }

        /// <summary>The zones. The same list as assets/editor/nofly.json -
        /// python verify.py [35] compares them.</summary>
        internal static readonly List<Zone> Zones = new List<Zone>();
        static Zone _town;

        // Horizontal town boundary; the 52-K's own ceiling still applies.
        internal static bool TownContains(Vector3 p)
        {
            return _town != null && AirDefencePolicy.TownArea(p.x, p.z, _town.Centre.x, _town.Centre.y, _town.Radius);
        }

        static NoFly()
        {
            // Point N12 = NPC_Settlement[Neutrals] (1446.6, 1703.2), the neutral
            // base (IsSafeSettlement, RE 39). The centre sits 22 u north of the
            // settlement object, on the middle of its buildings. B4: 510 u =
            // 1.5 x the ~340 u settlement ring, so the two rings stand clearly
            // apart on the map (docs/ai/tasks/b4-map-markers.md).
            Zones.Add(Circle("N12", "Point N12", "GW_Scene_1", "neutral", 1446.6f, 1725f, 510f, 700f,
                             new string[0]));
            // The east military town (P6b, docs/ai/tasks/military-town-ring-nofly.md):
            // B4 makes it a circle round the town's 380 u map ring
            // (MilitaryTown.Centre / MapRingRadius), 530 u = 1.4 x - the most
            // that keeps its west edge (x 5120) 300 u (107 m) east of the
            // airfield's fence (x 4820; the runway runs N-S). It holds the
            // whole fence and the AA1 Gepard. Only with the town on; owned by
            // the garrison's faction; defended by a Gepard standing IN the
            // town (the AA1 site) and nobody else - with it dead the sky over
            // the town is open.
            Zone mt = Circle("MT", "Military town", "GW_Scene_1", "traitor", 5650f, 900f, 530f, 700f,
                             new string[] { "MT-AA2a", "MT-AA2b" });
            mt.Reach = 0f;
            mt.Exists = MilitaryTown.NoFlyShown;
            mt.Armed = MilitaryTown.NoFlyArmed;
            mt.Side = MilitaryTown.Faction;
            _town = mt;
            Zones.Add(mt);
            Zone af = Polygon("AF", "Airfield", "GW_Scene_1", "looter", new Vector2[] {
                new Vector2(3990f, -1690f), new Vector2(4820f, -1690f),
                new Vector2(4820f, 1690f), new Vector2(3990f, 1690f) }, 8400f, // 3000 m x 2.8
                new string[] { "AA-N", "AA-S" });
            af.Exists = AirfieldOwnership.Shown;
            af.Armed = AirfieldOwnership.Armed;
            af.Side = AirfieldOwnership.ZoneSide;
            af.Reach = 0f;
            Zones.Add(af);
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
        static bool Live(Zone zn) { return Here(zn) && (zn.Armed == null || zn.Armed() || (zn.Id == "MT" && Flak.TownManned())); }

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
            string pilot = AirPilot.Faction(c);
            string own = faction == "players" ? AirfieldOwnership.FactionName : Fraktion.Eigene(faction);
            anyone = AirDefencePolicy.Hostile(own, pilot);
            owner = pilot != null && pilot == own;
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
                for (int i = 0; i < Zones.Count; i++)
                    if (Zones[i].Side != null) Zones[i].Faction = Zones[i].Side();
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
                if (zn.Id == "AF") continue; // Owned flak scans already engage; HUD uses LocalTick.
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
                string by = guns ? "guns" : gepard ? "Gepard" : "nobody";
                if (by != v.By)
                {
                    v.By = by;
                    Log(zn.Id + ": engaging " + v.C.Go.name + " - " + by + ".");
                }
                if (first == null) first = v;
            }
            // The guns lay on one violator at a time: the first engaged one.
            if (zn.Id == "AF" || zn.Id == "MT") return; // Keep explicit radar HOLD/ENGAGE orders.
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
                if (check && zn.Id != "AF" && zn.Id != "MT" && info.Mode != FlakMode.ZoneDefence)
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
                if (Live(zn) && me != null)
                    for (int i = 0; i < at.Count && !inside; i++)
                    {
                        if (!Contains(zn, at[i])) continue;
                        bool anyone = true, owner = false;
                        if (by[i] != null) Aboard(by[i], zn.Faction, out anyone, out owner);
                        if (by[i] == null) anyone = AirDefencePolicy.Hostile(zn.Id == "AF" ? AirfieldOwnership.FactionName : Fraktion.Eigene(zn.Faction), Fraktion.Spielerseite(me));
                        inside = anyone && !owner;
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
                bool engaged = now - _local[0].Since >= Seconds() && Defended(_local[0].Zone);
                NoFlyBeep.Play(engaged);
                _nextBeep = now + (engaged ? 0.6f : 1.2f);
            }
        }

        /// <summary>Every client: does a real gun defend the zone? A crewed
        /// (or player-manned) zone ZU-23, a laying Gepard of the faction in
        /// reach, or a zone armed only while its defender stands (MT).
        /// Without one there is no fire at all - no scripted flak.</summary>
        static bool Defended(Zone zn)
        {
            if (zn.Armed != null) return true;
            for (int g = 0; zn.Guns != null && g < zn.Guns.Length; g++)
            {
                FlakGunInfo info = Flak.Get(zn.Guns[g]);
                if (info != null && info.Health > 0f && (info.CrewAlive > 0 || info.PlayerManned)) return true;
            }
            return GepardCrew.Defends(zn.Faction, Ground(zn), zn.Radius + zn.Reach);
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
                if (!Defended(l.Zone))
                {
                    head = "NO FLY ZONE: " + where;
                    line = "Leave the zone now - no air defence answers";
                    col = new Color(1f, 0.78f, 0.25f, 1f);
                }
                else if (left > 0f)
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

        /// <summary>Every zone of the loaded map as a red dashed outline in
        /// the native map ink (clipped and faded with the map), with a small
        /// "NO FLY ZONE" label just outside its top edge.</summary>
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
                    if (!Here(zn) || (zn.Id == "AF" && !Live(zn))) continue;
                    Vector2 size = new Vector2(full.width, full.height);
                    if (size.x <= 0f || size.y <= 0f) continue;
                    if (zn.Dashes == null || zn.DashesEast != EastWorld.Extends
                        || (zn.DashesSize - size).sqrMagnitude > 0.01f) BuildDashes(zn, size);
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

        /// <summary>The small label centred just ABOVE the northern boundary:
        /// outside the ring, so it never covers the place's baked name, which
        /// sits inside it.</summary>
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
            Rect r = new Rect(gui.x - size.x * 0.5f, gui.y - StrokeWidth - 2f - size.y, size.x, size.y);
            if (!view.Contains(new Vector2(r.xMin, r.yMin)) || !view.Contains(new Vector2(r.xMax, r.yMax))) return;
            Color old = GUI.color;
            try
            {
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _labelStyle);
                GUI.color = InkColor;
                GUI.Label(r, text, _labelStyle);
            }
            finally { GUI.color = old; }
        }

        /// <summary>Walk the closed outline in screen pixels, then convert
        /// each mask back to the native layer's 1024-square frame. Rebuild
        /// only on zoom/resize; panning does not change the dash spacing.</summary>
        static void BuildDashes(Zone zn, Vector2 size)
        {
            if (zn.Dashes != null)
                for (int i = 0; i < zn.Dashes.Count; i++)
                    if (zn.Dashes[i].Texture != null) UnityEngine.Object.Destroy(zn.Dashes[i].Texture);
            zn.Dashes = new List<MapInk.Dash>();
            zn.DashesEast = EastWorld.Extends;
            zn.DashesSize = size;
            List<Vector2> p = new List<Vector2>();
            float artWidth = EastWorld.Extends ? 2048f : 1024f;
            foreach (Vector2 w in Outline(zn))
            {
                Vector2 a = MapInk.Artwork(new Vector3(w.x, 0f, w.y));
                p.Add(new Vector2(a.x * size.x / artWidth, a.y * size.y / 1024f));
            }
            p.Add(p[0]);
            float[] arc = new float[p.Count];
            for (int i = 1; i < p.Count; i++) arc[i] = arc[i - 1] + (p[i] - p[i - 1]).magnitude;
            float total = arc[arc.Length - 1], period = DashLength + GapLength;
            int count = Mathf.Max(3, Mathf.RoundToInt(total / period));
            period = total / count;                       // the loop closes on a full gap
            float dash = period * DashLength / (DashLength + GapLength);
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
                d2.Bounds = new Rect(d2.Bounds.x * 1024f / size.x, d2.Bounds.y * 1024f / size.y,
                    d2.Bounds.width * 1024f / size.x, d2.Bounds.height * 1024f / size.y);
                d2.Mid = new Vector2(d2.Mid.x * 1024f / size.x, d2.Mid.y * 1024f / size.y);
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

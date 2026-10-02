// Revival.AirPicture.cs - W-Tower1: the tower radar's air picture on the map
// and its early warning (docs/ai/tasks/w-tower1-airpicture.md).
//
// Holding the tower = the C1 radar and its console work and are manned by
// your side (the holder's NPC operator alive, or a player of the holder's
// side at the console). Every player of that side then sees, on the world
// map, every aircraft the radar holds within its range - NPC raids (Tu-95,
// An-2), Mi-8s flown by NPCs and players, FPV and recon drones - as a live
// icon with IFF colour and heading, and gets an early warning banner with
// bearing and ETA when a hostile aircraft is inbound to him or to the
// airfield, or when an announced raid (N11) is coming.
//
// No new network traffic: every client already holds the radar's contact
// list (RadarScope.Air, 4 Hz, the radar's own FlakFire.Collect) and the
// master's radar state (event 197: damage, NPC operator, console operator,
// holder). The picture is computed locally from those, so it is the same on
// every client of the side.
//
// Cost: the picture is sampled 2 times a second (AirPictureHz) into a fixed
// pool of 32 tracks; the held check, IFF and warnings run once a second;
// Draw projects the pooled tracks (no reflection, no allocation) only while
// the map window is open, and the map context (reflection) is refreshed at
// 2 Hz. Nothing runs past a bool test while the viewer's side does not hold
// the radar. F6: AirPicture.Tick, AirPicture.Draw.
//
// C# 3.0 (csc from .NET 3.5). UTF-8; Cyrillic only in Loc.T player strings.

using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static class AirPicture
    {
        const float K = TowerRadar.K;
        internal const int MaxTracks = 32;

        internal static ConfigEntry<bool> CfgEnabled, CfgMap, CfgWarn, CfgSound;
        internal static ConfigEntry<float> CfgHz, CfgWarnRadius, CfgWarnSeconds;

        internal static void BindConfig(ConfigFile cfg, string s)
        {
            CfgEnabled = cfg.Bind(s, "AirPicture", true,
                "W-Tower1: while your side holds the tower radar (radar and console intact, your side's operator "
                + "at the console), every aircraft in radar range is shown live on your map.");
            CfgMap = cfg.Bind(s, "AirPictureMap", true, "The aircraft icons on the world map (off: warnings only).");
            CfgWarn = cfg.Bind(s, "AirPictureWarning", true,
                "Early warning banner: a hostile aircraft inbound to you or the airfield, or an announced raid, "
                + "with bearing and ETA.");
            CfgSound = cfg.Bind(s, "AirPictureWarningSound", true, "A short tone when a new hostile aircraft is inbound.");
            CfgHz = cfg.Bind(s, "AirPictureHz", 2f, "Radar picture updates per second on the map (0.5..5).");
            CfgWarnRadius = cfg.Bind(s, "WarningRadius", 1200f,
                "Metres: an aircraft whose track passes this close to you (or the airfield) is inbound.");
            CfgWarnSeconds = cfg.Bind(s, "WarningSeconds", 120f, "Seconds ahead an inbound aircraft is announced.");
        }

        static bool B(ConfigEntry<bool> c) { return c == null || c.Value; }

        // ------------------------------------------------------------ state

        sealed class Track
        {
            public GameObject Go;
            public int Id, Kind, Type, Iff, Stamp;
            public bool Raid, Threat;
            public Vector3 Pos, Vel;
            public float At, HeightM, Kmh, Heading;
            public string Detail;
            public int DetailKey = int.MinValue;
        }

        static readonly Track[] _tracks = new Track[MaxTracks];
        static int _stamp, _nextId = 1, _live;
        static float _nextSample, _nextSlow;
        static bool _held;
        static int _mySide = int.MinValue;
        static string _mine;

        // the warning, rebuilt once a second while it stands
        static string _warnHead, _warnLine;
        static int _warnKey = int.MinValue, _warnLevel;   // 2 to you, 1 airfield / raid
        static int _threatId = -1;
        static bool _warnSized;
        static string _holdNote;
        static float _holdNoteUntil;

        // an announced raid (N11 warning), kept until its aircraft are on the picture
        static Vector3 _raidAt;
        static Vector2 _raidFrom;
        static float _raidArrive = -1f, _raidSpeed;
        static string _raidWhat;

        internal static bool Held { get { return _held; } }
        internal static int Count { get { return _live; } }

        static AirPicture()
        {
            for (int i = 0; i < MaxTracks; i++) _tracks[i] = new Track();
        }

        // ------------------------------------------------------------- tick

        /// <summary>Every client, every frame from the plugin's Update. Two
        /// time checks unless a sample or the 1 Hz check is due.</summary>
        internal static void Tick()
        {
            float now = Time.time;
            if (now >= _nextSlow)
            {
                _nextSlow = now + 1f;
                try { Slow(now); }
                catch (Exception ex) { RevivalPlugin.L.LogError("AirPicture: " + ex); }
            }
            if (!_held || now < _nextSample) return;
            _nextSample = now + 1f / Mathf.Clamp(CfgHz == null ? 2f : CfgHz.Value, 0.5f, 5f);
            try { Sample(now); }
            catch (Exception ex) { RevivalPlugin.L.LogError("AirPicture sample: " + ex); }
        }

        /// <summary>1 Hz: does this viewer's side hold the radar; IFF; warnings.</summary>
        static void Slow(float now)
        {
            bool held = false;
            GameObject me = null;
            if (B(CfgEnabled) && TowerRadar.On && TowerRadar.Built && TowerRadar.Working)
            {
                me = MapTools.LocalPlayer();
                if (me != null)
                {
                    int side = Side(me);
                    if (side != _mySide)
                    {
                        _mySide = side;
                        _mine = side == TowerRadar.UnreadSide ? null : Fraktion.Spielerseite(me);
                    }
                    int op = -1;
                    if (!TowerRadar.NpcOperatorUp && TowerRadar.OperatorActor >= 0)
                        op = Side(Crocodile.PlayerByActor(TowerRadar.OperatorActor));
                    MercAAPost merc = MercAA.Operator;
                    if (merc != null) op = merc.Side;
                    held = AirPicturePolicy.Held(true, AirfieldOwnership.Holder, side, TowerRadar.NpcOperatorUp, op);
                }
            }
            if (held != _held)
            {
                _held = held;
                if (held)
                {
                    _nextSample = 0f;
                    _holdNote = "C1 RADAR: your side holds the tower - air picture on your map";
                    TowerRadar.Log("air picture on: " + TowerRadar.SideLabel(_mySide) + " holds the manned radar.");
                }
                else
                {
                    Release();
                    _holdNote = "C1 RADAR: air picture lost";
                    TowerRadar.Log("air picture off (radar down, unmanned, or not this side's).");
                }
                _holdNoteUntil = now + 6f;
            }
            if (!held) return;
            for (int i = 0; i < MaxTracks; i++)
            {
                Track t = _tracks[i];
                if (t.Go != null) t.Iff = Iff(t);
            }
            Warn(me, now);
        }

        static int Side(GameObject player)
        {
            if (player == null) return -1;
            int side = Mortar.FactionShield.FactionOf(player);
            return side < 0 ? TowerRadar.UnreadSide : side;
        }

        static int Iff(Track t)
        {
            if (t.Raid) return -1;
            // AirPilot.Faction reads the contact's pilot (drone: its owner).
            GepardGun.Contact c = Contact(t.Go);
            return c == null ? t.Iff : AirPicturePolicy.Iff(_mine, AirPilot.Faction(c), false);
        }

        static GepardGun.Contact Contact(GameObject go)
        {
            List<GepardGun.Contact> air = RadarScope.Air;
            for (int i = 0; i < air.Count; i++) if (air[i].Go == go) return air[i];
            return null;
        }

        /// <summary>AirPictureHz: the radar's contacts in range and in the air
        /// into the pooled tracks. An aircraft the viewer flies himself is left
        /// out - the map shows him already.</summary>
        static void Sample(float now)
        {
            List<GepardGun.Contact> air = RadarScope.Air;
            Vector3 eye = TowerRadar.RadarPos;
            float range = TowerRadar.ScopeRangeU;
            float min = Mathf.Max(0f, TowerRadar.F(TowerRadar.CfgMinHeight, 3f)) * K;
            int local = Crocodile.LocalActor();
            _stamp++;
            for (int i = 0; i < air.Count; i++)
            {
                GepardGun.Contact c = air[i];
                if (c.Go == null || !c.Go.activeInHierarchy || !c.Air) continue;
                float dx = c.Pos.x - eye.x, dz = c.Pos.z - eye.z;
                if (dx * dx + dz * dz > range * range) continue;
                if (Own(c, local, now)) continue;
                float h = RadarScope.Height(c.Pos);
                if (h < min) continue;
                Track t = Slot(c.Go);
                if (t == null) continue;   // pool full: a new one waits for a free slot
                if (t.Stamp == 0)
                {
                    t.Kind = c.Kind;
                    t.Raid = c.Kind == 6 && NpcAircraft.Hostile(c.Go);
                    t.Type = AirPicturePolicy.Type(c.Kind, c.Kind == 6 && NpcAircraft.IsTu95(c.Go));
                    t.Iff = t.Raid ? -1 : AirPicturePolicy.Iff(_mine, AirPilot.Faction(c), false);
                }
                t.Stamp = _stamp;
                t.Pos = c.Pos;
                t.Vel = c.Vel;
                t.At = now;
                t.HeightM = h / K;
                t.Kmh = new Vector2(c.Vel.x, c.Vel.z).magnitude / K * 3.6f;
                if (t.Kmh > 5f) t.Heading = AirPicturePolicy.Bearing(c.Vel.x, c.Vel.z);
            }
            _live = 0;
            for (int i = 0; i < MaxTracks; i++)
            {
                Track t = _tracks[i];
                if (t.Go == null) continue;
                if (t.Stamp != _stamp) { Free(t); continue; }
                _live++;
                int key = AirPicturePolicy.DetailKey(t.Id, t.Type, t.Iff, t.HeightM, t.Kmh, t.Heading);
                if (key != t.DetailKey)
                {
                    // Rebuilt only when what it says changes (25 m, 10 km/h, 10 deg).
                    t.DetailKey = key;
                    t.Detail = "T" + t.Id.ToString("00", CultureInfo.InvariantCulture) + "  "
                        + AirPicturePolicy.IffName(t.Iff) + "  " + AirPicturePolicy.TypeName(t.Type)
                        + "\nalt " + (Mathf.Round(t.HeightM / 25f) * 25f).ToString("0", CultureInfo.InvariantCulture)
                        + " m   " + (Mathf.Round(t.Kmh / 10f) * 10f).ToString("0", CultureInfo.InvariantCulture)
                        + " km/h   hdg " + (Mathf.Round(t.Heading / 10f) * 10f % 360f).ToString("000", CultureInfo.InvariantCulture);
                }
            }
        }

        static bool Own(GepardGun.Contact c, int local, float now)
        {
            if (local < 0) return false;
            if (c.Kind == 2 || c.Kind == 3) return c.Actor == local;
            AirPilot p = c.Go.GetComponent<AirPilot>();
            return p != null && p.Actor == local && now <= p.Until;
        }

        static Track Slot(GameObject go)
        {
            Track free = null;
            for (int i = 0; i < MaxTracks; i++)
            {
                Track t = _tracks[i];
                if (t.Go == go) return t;
                if (free == null && t.Go == null) free = t;
            }
            if (free == null) return null;
            free.Go = go;
            free.Stamp = 0;
            free.Id = _nextId++;
            if (_nextId > 99) _nextId = 1;
            free.DetailKey = int.MinValue;
            free.Threat = false;
            return free;
        }

        static void Free(Track t)
        {
            t.Go = null;
            t.Stamp = 0;
            t.Threat = false;
        }

        static void Release()
        {
            for (int i = 0; i < MaxTracks; i++) Free(_tracks[i]);
            _live = 0;
            _warnHead = _warnLine = null;
            _warnKey = int.MinValue;
            _threatId = -1;
        }

        // ---------------------------------------------------------- warning

        /// <summary>N11 raid warning (AirEvents kind 0, every client): the
        /// raid's target, the direction it comes from (unit, target -> entry
        /// side), seconds until it is over the target, its speed in u/s.</summary>
        internal static void Raid(Vector3 target, Vector2 from, float eta, float speedU, string what)
        {
            _raidAt = target;
            _raidFrom = from;
            _raidArrive = Time.time + Mathf.Max(0f, eta);
            _raidSpeed = speedU;
            _raidWhat = what;
            _nextSlow = 0f;   // tell the holder at once, not up to a second later
        }

        /// <summary>Event-only, no proximity gate: the holder and every faction
        /// member receive radar intelligence wherever they are on the map.</summary>
        internal static bool HolderRaidWarning(Vector3 at, float from, float eta)
        {
            if (!B(CfgEnabled) || !B(CfgWarn) || !TowerRadar.On || !TowerRadar.Built) return false;
            GameObject me = MapTools.LocalPlayer();
            int op = -1;
            if (TowerRadar.OperatorActor >= 0)
                op = Side(Crocodile.PlayerByActor(TowerRadar.OperatorActor));
            MercAAPost merc = MercAA.Operator;
            if (merc != null) op = merc.Side;
            if (!AirPicturePolicy.Held(TowerRadar.Working, AirfieldOwnership.Holder,
                Side(me), TowerRadar.NpcOperatorUp, op)) return false;
            int seconds = Mathf.Max(0, Mathf.CeilToInt(eta));
            string wait = seconds >= 60 ? Mathf.CeilToInt(seconds / 60f) + Loc.T(" мин", " min")
                : seconds + Loc.T(" с", " s");
            string text = Loc.T("РЛС: налет через ", "RADAR: Raid inbound, ") + wait
                + Loc.T(", с направления ", ", from ") + AirPicturePolicy.Compass(from);
            WarnAt(at, text, eta, text);
            return true;
        }

        static void Warn(GameObject me, float now)
        {
            if (!B(CfgWarn) || me == null) { _warnHead = null; return; }
            Vector3 p = me.transform.position;
            Vector3 af = TowerRadar.RadarPos;
            float radius = Mathf.Max(100f, CfgWarnRadius == null ? 1200f : CfgWarnRadius.Value) * K;
            float horizon = Mathf.Clamp(CfgWarnSeconds == null ? 120f : CfgWarnSeconds.Value, 10f, 600f);
            Track best = null, bestAf = null;
            float bestEta = float.MaxValue, bestAfEta = float.MaxValue;
            bool raidTracked = false;
            for (int i = 0; i < MaxTracks; i++)
            {
                Track t = _tracks[i];
                t.Threat = false;
                if (t.Go == null || t.Iff >= 0) continue;
                if (t.Raid) raidTracked = true;
                float eta, miss;
                if (AirPicturePolicy.Inbound(t.Pos.x - p.x, t.Pos.z - p.z, t.Vel.x, t.Vel.z, radius, horizon, out eta, out miss))
                {
                    t.Threat = true;
                    if (eta < bestEta) { bestEta = eta; best = t; }
                }
                else if (AirPicturePolicy.Inbound(t.Pos.x - af.x, t.Pos.z - af.z, t.Vel.x, t.Vel.z, radius, horizon, out eta, out miss)
                         && eta < bestAfEta) { bestAfEta = eta; bestAf = t; }
            }

            int level = 0, key;
            Track t0 = best ?? bestAf;
            if (t0 != null)
            {
                level = best != null ? 2 : 1;
                float eta = best != null ? bestEta : bestAfEta;
                float dx = t0.Pos.x - p.x, dz = t0.Pos.z - p.z;
                float brg = AirPicturePolicy.Bearing(dx, dz);
                float km = Mathf.Sqrt(dx * dx + dz * dz) / K / 1000f;
                int ieta = Mathf.RoundToInt(eta), ibrg = Mathf.RoundToInt(brg) % 360, ikm = Mathf.RoundToInt(km * 10f);
                key = ((t0.Id * 3 + level) * 601 + Mathf.Min(ieta, 600)) * 360 * 64 + ibrg * 64 + Mathf.Min(ikm, 63);
                if (key != _warnKey)
                {
                    _warnKey = key;
                    string what = AirPicturePolicy.TypeName(t0.Type);
                    string where = "bearing " + ibrg.ToString("000", CultureInfo.InvariantCulture) + " ("
                        + AirPicturePolicy.Compass(brg) + "), " + (ikm / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
                    _warnHead = level == 2 ? "C1 RADAR - AIR THREAT TO YOU" : "C1 RADAR - AIRFIELD UNDER AIR THREAT";
                    if (ieta <= 0)
                        _warnLine = "FOE " + what + (level == 2 ? " over you - " : " over the airfield - ") + where;
                    else
                        _warnLine = "FOE " + what + (level == 2 ? " inbound to you - " : " inbound to the airfield - ")
                            + where + (level == 2 ? "" : " from you") + ", ETA " + ieta.ToString(CultureInfo.InvariantCulture) + " s";
                    _warnSized = false;
                }
                if (level == 2 && t0.Id != _threatId && B(CfgSound)) NoFlyBeep.Play(false);
                _threatId = level == 2 ? t0.Id : -1;
                _warnLevel = level;
                return;
            }
            _threatId = -1;

            // An announced raid, until its aircraft show on the picture.
            if (_raidArrive >= 0f && !raidTracked && now < _raidArrive + 30f && _raidSpeed > 0f)
            {
                float miss, brg;
                float eta = AirPicturePolicy.RaidEta(_raidAt.x, _raidAt.z, _raidFrom.x, _raidFrom.y,
                    _raidArrive - now, _raidSpeed, p.x, p.z, out miss, out brg);
                int ieta = Mathf.Max(0, Mathf.RoundToInt(eta)), ibrg = Mathf.RoundToInt(brg) % 360;
                int imiss = Mathf.RoundToInt(miss / K / 100f);
                key = -(((ieta * 360 + ibrg) * 101) + Mathf.Min(imiss, 100)) - 1;
                if (key != _warnKey)
                {
                    _warnKey = key;
                    _warnHead = "C1 RADAR - RAID INBOUND";
                    _warnLine = (_raidWhat ?? "Raid") + " - bearing " + ibrg.ToString("000", CultureInfo.InvariantCulture)
                        + " (" + AirPicturePolicy.Compass(brg) + ") from you, passes "
                        + (imiss / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " km from you in ~"
                        + ieta.ToString(CultureInfo.InvariantCulture) + " s";
                    _warnSized = false;
                }
                _warnLevel = imiss * 100f * K <= radius ? 2 : 1;
                return;
            }
            if (_raidArrive >= 0f && now >= _raidArrive + 30f) _raidArrive = -1f;
            _warnHead = null;
            _warnKey = int.MinValue;
            _warnLevel = 0;
        }

        // ------------------------------------------------------------- draw

        static readonly Color FoeColor = new Color(1f, 0.3f, 0.25f, 1f);
        static readonly Color FriendColor = new Color(0.4f, 0.8f, 1f, 1f);
        static readonly Color UnknownColor = new Color(1f, 0.85f, 0.3f, 1f);
        const int LabelLimit = 12;
        static readonly Texture2D[] _icons = new Texture2D[6];
        static Texture2D _px;
        static GUIStyle _small, _banner, _bannerSmall;
        static readonly GUIContent _content = new GUIContent();
        static float _headW, _lineW;
        static readonly string[] _captions = new string[MaxTracks + 1];

        // the map window: refreshed at 2 Hz, projected every frame
        static Component _tex;
        static Camera _cam;
        static Vector2 _world, _map;
        static Rect _view;
        static bool _hasView;
        static Rect _fullView;
        static float _ctxAt;

        /// <summary>OnGUI: the banner over the game view or the map, the
        /// tracks over the map. One bool when the side does not hold the radar.</summary>
        internal static void Draw()
        {
            if (Time.time >= _pingUntil)
            {
                if (!_held && Time.time >= _holdNoteUntil) return;
            }
            Event e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            int ui = GameUi.State;
            if (ui != 0 && ui != 8) return;   // another game window is open
            try
            {
                if (ui == 8 && Time.time < _pingUntil) DrawWarningMap();
                if (_held && ui == 8 && _live > 0 && B(CfgMap)) DrawMap();
                DrawBanner();
            }
            catch (Exception ex) { RevivalPlugin.L.LogError(ex); }
        }

        // Event-only writes, fixed ring; warnings stay on the map through arrival,
        // even without a working/owned radar. Existing AirPicture.Draw F6 slot.
        const int PingCount = 8;
        static readonly Vector3[] _pingPos = new Vector3[PingCount];
        static readonly string[] _pingName = new string[PingCount];
        static readonly float[] _pingEnd = new float[PingCount], _pingArrive = new float[PingCount];
        static float _pingUntil;
        static int _pingNext;

        internal static void WarnAt(Vector3 at, string label, float eta, string toast)
        {
            int i = _pingNext;
            _pingNext = (_pingNext + 1) % PingCount;
            _pingPos[i] = at;
            _pingName[i] = label;
            _pingArrive[i] = Time.time + Mathf.Clamp(eta, 0f, 600f);
            _pingEnd[i] = _pingArrive[i] + 30f;
            _pingUntil = Mathf.Max(_pingUntil, _pingEnd[i]);
            // Toasts and their backing strings are event allocations, never ticks.
            UiKit.Toast(toast, UiTone.Warning);
        }

        static void DrawWarningMap()
        {
            if (!MapContext()) return;
            Rect clip;
            if (_hasView) clip = _view;
            else clip = _fullView;
            float now = Time.time;
            float pulse = 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(now * 4f));
            for (int i = 0; i < PingCount; i++)
            {
                if (_pingName[i] == null || now >= _pingEnd[i]) continue;
                Vector2 p;
                if (!Gui(_pingPos[i], out p) || !clip.Contains(p)) continue;
                float size = UiKit.S(14f + pulse * 8f);
                Rect mark = new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size);
                // Clip all warning graphics to the native map viewport.
                GUI.BeginClip(clip);
                try
                {
                    mark.x -= clip.x; mark.y -= clip.y;
                    UiKit.Outline(mark, UiKit.Bad);
                    UiKit.Fill(new Rect(mark.center.x - 2f, mark.center.y - 2f, 4f, 4f), UiKit.Bad, 0);
                    Rect label = new Rect(mark.xMax + UiKit.S(4f), mark.y - UiKit.S(6f), UiKit.S(224f), UiKit.S(42f));
                    label.x = Mathf.Clamp(label.x, 0f, Mathf.Max(0f, clip.width - label.width));
                    label.y = Mathf.Clamp(label.y, 0f, Mathf.Max(0f, clip.height - label.height));
                    UiKit.Fill(label, UiKit.Header, 1);
                    UiKit.Label(new Rect(label.x + 4f, label.y, label.width - 8f, label.height * 0.5f),
                        _pingName[i], UiFont.Small, UiFont.Left, UiKit.Bad);
                    UiKit.Label(new Rect(label.x + 4f, label.center.y, UiKit.S(70f), label.height * 0.5f),
                        RadarClarityText.Eta, UiFont.Small, UiFont.Left, UiKit.TextDim);
                    int left = Mathf.CeilToInt(_pingArrive[i] - now);
                    UiKit.Label(new Rect(label.x + UiKit.S(80f), label.center.y, UiKit.S(100f), label.height * 0.5f),
                        left <= 0 ? RadarClarityText.Arrived : PingSeconds(left), UiFont.Small, UiFont.Left, UiKit.Text);
                }
                finally { GUI.EndClip(); }
            }
        }

        static readonly string[] _pingSeconds = MakePingSeconds();
        static string[] MakePingSeconds()
        {
            string[] labels = new string[601];
            for (int i = 0; i < labels.Length; i++) labels[i] = i.ToString(CultureInfo.InvariantCulture);
            return labels;
        }
        static string PingSeconds(int n) { return _pingSeconds[Mathf.Clamp(n, 0, 600)]; }

        static bool MapContext()
        {
            float now = Time.realtimeSinceStartup;
            bool valid = _tex != null && _cam != null && _tex.gameObject.activeInHierarchy;
            if (!valid && now < _ctxAt) return false;
            if (!valid || now >= _ctxAt)
            {
                _ctxAt = now + 0.5f;
                Component manager;
                if (!MapTools.Context(out manager, out _tex, out _cam, out _world, out _map)) { _tex = null; return false; }
                _hasView = MapTools.MapViewportRect(_tex, _cam, out _view);
                MapTools.MapScreenRect(_tex, _cam, out _fullView);
            }
            return _tex != null && _cam != null;
        }

        static bool Gui(Vector3 p, out Vector2 g)
        {
            return MapProject.ToGui(p, _tex, _cam, _world, _map, out g);
        }

        static void DrawMap()
        {
            if (!EastWorld.Extends || !MapContext()) return;
            // The whole picture: the world rectangle's corners, projected (the
            // east artwork is registered exactly on it); clipped to the viewport.
            Rect w = EastWorld.Extended;
            Vector2 a, b;
            if (!Gui(new Vector3(w.xMin, 0f, w.yMin), out a) || !Gui(new Vector3(w.xMax, 0f, w.yMax), out b)) return;
            Rect clip = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            if (_hasView)
                clip = Rect.MinMaxRect(Mathf.Max(clip.xMin, _view.xMin), Mathf.Max(clip.yMin, _view.yMin),
                    Mathf.Min(clip.xMax, _view.xMax), Mathf.Min(clip.yMax, _view.yMax));
            if (clip.width < 2f || clip.height < 2f) return;
            Styles();

            float now = Time.time;
            Vector2 mouse = Event.current.mousePosition - clip.position;
            Track hover = null;
            Vector2 hoverAt = Vector2.zero;
            Color old = GUI.color;
            Matrix4x4 matrix = GUI.matrix;
            GUI.BeginClip(clip);
            try
            {
                float pulse = 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(now * 6f));
                bool labels = _live <= LabelLimit;
                for (int i = 0; i < MaxTracks; i++)
                {
                    Track t = _tracks[i];
                    if (t.Go == null) continue;
                    // live between samples: the last position flown on
                    float dt = Mathf.Clamp(now - t.At, 0f, 1.5f);
                    Vector3 pos = t.Pos + t.Vel * dt;
                    Vector2 g, lead;
                    if (!Gui(pos, out g)) continue;
                    g -= clip.position;
                    if (g.x < -20f || g.y < -20f || g.x > clip.width + 20f || g.y > clip.height + 20f) continue;
                    Color c = t.Iff < 0 ? FoeColor : t.Iff > 0 ? FriendColor : UnknownColor;
                    if (t.Threat) c.a *= pulse;
                    float angle = t.Heading;
                    bool moving = t.Kmh > 5f;
                    // the heading as the map shows it, and a 30 s leader line
                    if (moving && Gui(pos + t.Vel * 30f, out lead))
                    {
                        lead -= clip.position;
                        Vector2 d = lead - g;
                        float len = d.magnitude;
                        if (len > 1f)
                        {
                            angle = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
                            GUI.color = new Color(c.r, c.g, c.b, c.a * 0.8f);
                            GUIUtility.RotateAroundPivot(angle, g);
                            VanillaUi.Texture(new Rect(g.x - 0.75f, g.y - len, 1.5f, len), Px());
                            GUI.matrix = matrix;
                        }
                    }
                    float size = t.Type == 2 ? 26f : t.Type >= 3 ? 16f : 20f;
                    GUI.color = c;
                    if (moving) GUIUtility.RotateAroundPivot(angle, g);
                    VanillaUi.Texture(new Rect(g.x - size * 0.5f, g.y - size * 0.5f, size, size), Icon(t.Type));
                    GUI.matrix = matrix;
                    // A label is the dearest IMGUI call: type names up to
                    // LabelLimit tracks, beyond that the hover text only.
                    if (labels) VanillaUi.Label(new Rect(g.x + size * 0.5f + 2f, g.y - 7f, 90f, 16f), AirPicturePolicy.TypeName(t.Type), _small);
                    if ((mouse - g).sqrMagnitude <= 14f * 14f) { hover = t; hoverAt = g; }
                }
                if (hover != null && hover.Detail != null)
                {
                    Rect r = new Rect(hoverAt.x + 14f, hoverAt.y + 10f, 230f, 34f);
                    if (r.xMax > clip.width) r.x = hoverAt.x - 14f - r.width;
                    if (r.yMax > clip.height) r.y = hoverAt.y - 10f - r.height;
                    GUI.color = new Color(0f, 0f, 0f, 0.7f);
                    VanillaUi.Texture(r, Px());
                    GUI.color = hover.Iff < 0 ? FoeColor : hover.Iff > 0 ? FriendColor : UnknownColor;
                    VanillaUi.Label(new Rect(r.x + 5f, r.y + 2f, r.width - 8f, r.height - 2f), hover.Detail, _small);
                }
                // caption, top left of the picture
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                VanillaUi.Texture(new Rect(6f, 6f, 250f, 18f), Px());
                GUI.color = FriendColor;
                VanillaUi.Label(new Rect(10f, 7f, 246f, 18f), Caption(_live), _small);
            }
            finally
            {
                GUI.EndClip();
                GUI.matrix = matrix;
                GUI.color = old;
            }
        }

        static string Caption(int n)
        {
            if (n < 0) n = 0;
            if (n > MaxTracks) n = MaxTracks;
            if (_captions[n] == null)
                _captions[n] = "C1 RADAR - AIR PICTURE: " + n.ToString(CultureInfo.InvariantCulture)
                    + (n == 1 ? " TRACK" : " TRACKS");
            return _captions[n];
        }

        static void DrawBanner()
        {
            string head = null, line = null;
            Color col;
            if (_held && _warnHead != null)
            {
                head = _warnHead;
                line = _warnLine;
                col = _warnLevel >= 2 ? new Color(1f, 0.3f, 0.25f, 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(Time.time * 5f)))
                    : new Color(1f, 0.78f, 0.25f, 1f);
            }
            else if (Time.time < _holdNoteUntil && _holdNote != null)
            {
                head = _holdNote;
                col = _held ? FriendColor : new Color(0.8f, 0.8f, 0.8f, 1f);
                col.a = Mathf.Clamp01(_holdNoteUntil - Time.time);
            }
            else return;
            VanillaUi.Banner(head, line, Screen.height * 0.16f + 72f, false);
        }

        static void Styles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label);
            _small.fontSize = 11;
            _small.wordWrap = false;
            _small.richText = false;
            _small.normal.textColor = Color.white;
            _banner = new GUIStyle(GUI.skin.label);
            _banner.fontSize = 18;
            _banner.wordWrap = false;
            _banner.richText = true;
            _banner.normal.textColor = Color.white;
            _bannerSmall = new GUIStyle(_banner);
            _bannerSmall.fontSize = 15;
        }

        static Texture2D Px()
        {
            if (_px == null) _px = Mortar.PxTexture();
            return _px;
        }

        // ------------------------------------------------------------ icons

        /// <summary>Top-down silhouettes, nose up, baked once in white with a
        /// dark outline and tinted per IFF when drawn.</summary>
        internal static Texture2D Icon(int type)
        {
            if (type < 0 || type >= _icons.Length) type = 5;
            if (_icons[type] != null) return _icons[type];
            const int size = 64;
            Color[] pixels = new Color[size * size];
            Color ink = new Color(1f, 1f, 1f, 1f);
            Color outline = new Color(0.08f, 0.08f, 0.08f, 0.9f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // texture rows run bottom up: +dy is the nose (screen up)
                    float dx = (x - (size - 1) * 0.5f) * 40f / size;
                    float dy = (y - (size - 1) * 0.5f) * 40f / size;
                    if (Ink(type, dx, dy)) pixels[y * size + x] = ink;
                    else
                        for (int oy = -2; oy <= 2 && pixels[y * size + x].a == 0f; oy++)
                            for (int ox = -2; ox <= 2; ox++)
                                if (Ink(type, dx + ox, dy + oy)) { pixels[y * size + x] = outline; break; }
                }
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "NDR_AirPictureIcon" + type;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            _icons[type] = tex;
            return tex;
        }

        static bool Ink(int type, float x, float y)
        { return RadarScopeCore.Ink(type, x, y); }
    }
}

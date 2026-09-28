// Next Day: Survival - Revival Toolkit
//
// AIR EVENTS (task N11): air strikes authored in the map editor. Tu-95
// bombers lay a carpet of FAB-250 along a line the admin drew on the map, An-2
// transports drop NPC paratroopers who then attack along an arrow exactly like
// a heli troop landing's squad. docs/ai/tasks/n11-air-events.md has the design,
// the numbers and the in-game checklist.
//
// PIECES
//   Data       the editor's table (airdef.py): live at /runtime/air
//              (LiveRoutes.TickAir), assets/ndr_airevents.tsv offline. One row
//              per event: target line (centre, heading, length, width), entry
//              edge, drop zone, attack arrow head, faction, patrol time, the
//              repeat window in hours, and the SEQUENCE - up to six waves
//              (bomber | transport, count, delay after the start, bombs or
//              paratroopers per aircraft).
//   Raid       master only: the schedule (every event its own random window),
//              the warning 60 s before the first aircraft is over the target,
//              the launches (NpcAircraft - the N3 flight path module, so every
//              AA gun, the Stinger, rifles, the radar and the no-fly zones
//              already know them), the bomb release and the jump.
//   Stick      every client: one Photon event per bomber carries every bomb
//              (release offset, impact x/z). The bombs fall from the real
//              aircraft; the bursts are ONE shared flash and ONE shared dust
//              particle system (Emit per impact) and a small pool of audio
//              sources - pooled, whatever the count. The master alone sweeps
//              the damage (Mortar.Sweep, any faction), once per impact.
//   Jump       every client: one event per transport; each man hangs under
//              the game's own canopy (Parachute_Pref's meshes, copied without
//              its scripts; a built dome if it cannot be read) and sinks at
//              ~5 m/s. When the last man is down the master spawns the squad
//              where they landed (Crew.DropSquadAt) and gives it the arrow
//              (NpcWar.StartOperation) - the heli troop landing's own path.
//   Warning    every client: the air raid siren at the target (the tower
//              radar's siren clip), a distant engine drone from the entry
//              side, the radar's contact log ("inbound"), a banner.
//   Tu95       every client: the N10 model (tu95_import.py) on the NPC
//              carrier in place of the An-2 model: eight contra-rotating
//              props, bay doors that open for the release, hull boxes for
//              rifle rays, a deep four-engine drone. Shot down it glides in
//              (the An-2's crash) and lies as the N10 wreck with a lootable
//              hold (the Mi-8 cargo hold's ItemsContainer) for 20 minutes.
//
// SAFE ZONES are never hit: a bomb whose impact lies within SafeRadius of a
// safe settlement (IsSafeSettlement, N12) is not released, a drop zone there
// cancels the paradrop, and the admin cannot call a strike onto one.
//
// Photon event [AirEvents] NetworkEventCode (163), float[] with the kind first:
//   0 warn     { 0, x, z, siren s, from bearing, bombers, transports, eta }
//   1 stick    { 1, view, fall s, n, t0, x0, z0, t1, x1, z1, ... }
//   2 jump     { 2, view, n, interval, descent u/s, x, y, z, vx, vz }
//   3 request  { 3, x, y, z, template, quick }                      -> master
//   4 clear    { 4 }                                                -> master
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. Player-facing strings through Loc.T with real Cyrillic, so this
// file is UTF-8 without BOM; comments and logs ASCII.
//
// SEAMS OUTSIDE THIS FILE:
//   Revival.PlayerAn2.cs     Prepare -> Prepared; Burn -> Burned, WreckSeconds
//   Revival.NpcAircraft.cs   Flight.Toughness (a Tu-95 takes 3x the rounds)
//   Revival.LiveRoutes.cs    TickAir / ParseAir -> Load / Parse
//   RevivalMortar.cs         Mortar.AnyFaction, Sound.ThumpClip
//   Revival.WreckFire.cs     FireEffect.SharedAdditive / SharedBlended
//   Revival.TowerRadar.cs    RadarSiren.Clip, RadarScope.Note
//   Revival.Airfield.cs      Airfield.LootItemId (the wreck's hold)
//   Revival.Admin.cs         the panel rows and the map right-click button
//   RevivalPlugin.cs         BindConfig / Tick / Draw

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
    internal static class AirEvents
    {
        // ------------------------------------------------------------ numbers
        // Real metres and km/h; the world is PlayerAn2.K (2.8) times real size.
        internal const float BomberAltitudeM = 350f;
        internal const float BomberKmh = 420f;
        internal const float TransportAltitudeM = 150f;
        internal const float TransportKmh = 190f;
        /// <summary>Seconds of warning before the first aircraft is over the target.</summary>
        internal const float WarnSeconds = 60f;
        internal const float QuickWarnSeconds = 12f;
        /// <summary>FAB-250: lethal / damaging radius in real metres, and the
        /// peaks at the centre (five times the An-2's FAB-50).</summary>
        internal const float BombRadiusM = 16f;
        internal const float BombNpcPeak = 900f, BombVehiclePeak = 2400f, BombPlayerPeak = 450f;
        /// <summary>World units around a safe settlement that are never hit.</summary>
        internal const float SafeRadius = 420f;
        /// <summary>Canopy descent, real m/s.</summary>
        internal const float DescentMs = 5f;
        /// <summary>A paratroop stick lands along this many world units.</summary>
        internal const float JumpSpread = 300f;
        internal const float WreckSeconds_ = 1200f;
        const float Gravity = 9.81f * PlayerAn2.K;

        internal const string BomberTag = "tu95:";
        internal const string TransportTag = "an2t:";

        static float K { get { return PlayerAn2.K; } }

        internal static ConfigEntry<int> CfgEventCode;
        internal static ConfigEntry<bool> CfgBanner;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgEventCode = cfg.Bind("AirEvents", "NetworkEventCode", 163,
                "Photon event code (0..199) of the editor air events (bomb sticks, paradrops, "
                + "warnings, admin requests). Every client must use the same value.");
            CfgBanner = cfg.Bind("Hints", "AirRaidBanner", true,
                "Hint: the air raid warning and the paratroop landing are shown as a banner.");
        }

        // =============================================================== data

        internal sealed class Wave
        {
            public bool Bomber;
            public int Count = 1;
            public float Delay;
            public int Load;
        }

        internal sealed class Event
        {
            public string Name = "";
            public bool Enabled = true;
            public float X, Z, Heading, Length = 900f, Width = 150f;
            public string Edge = "auto";
            public float DropX, DropZ, AttackX, AttackZ;
            public string Faction = "traitor";
            public int PatrolMinutes = 60;
            public float IntervalMin, IntervalMax;
            public readonly List<Wave> Waves = new List<Wave>();
            public string Scene = "";
            internal float Next = -1f;
            public bool Here { get { return MapScene.Owns(Scene); } }
            public int Bombers { get { int n = 0; for (int i = 0; i < Waves.Count; i++) if (Waves[i].Bomber) n += Waves[i].Count; return n; } }
            public int Transports { get { int n = 0; for (int i = 0; i < Waves.Count; i++) if (!Waves[i].Bomber) n += Waves[i].Count; return n; } }
        }

        static readonly List<Event> _events = new List<Event>();
        static string[] _source;
        static bool _loaded;

        internal static int Count { get { return _events.Count; } }

        static float F(string s)
        {
            float v;
            if (!float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                || float.IsNaN(v) || float.IsInfinity(v))
                throw new FormatException("not a number: '" + s + "'");
            return v;
        }

        /// <summary>The table, every row checked; throws on the first bad row
        /// so a broken live update changes nothing.</summary>
        internal static List<Event> Parse(string[] lines)
        {
            List<Event> list = new List<Event>();
            if (lines == null) return list;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;
                raw = raw.TrimEnd('\r');
                if (raw.Trim().Length == 0 || raw[0] == '#') continue;
                string[] c = raw.Split('\t');
                if (c.Length < 17) throw new FormatException("air event row " + (i + 1) + ": " + c.Length + " columns");
                Event e = new Event();
                e.Name = c[0].Trim();
                e.Enabled = c[1].Trim() != "0";
                e.X = F(c[2]); e.Z = F(c[3]);
                e.Heading = Mathf.Repeat(F(c[4]), 360f);
                e.Length = Mathf.Clamp(F(c[5]), 200f, 1500f);
                e.Width = Mathf.Clamp(F(c[6]), 40f, 600f);
                e.Edge = c[7].Trim();
                e.DropX = F(c[8]); e.DropZ = F(c[9]);
                e.AttackX = F(c[10]); e.AttackZ = F(c[11]);
                e.Faction = c[12].Trim().ToLowerInvariant();
                e.PatrolMinutes = Mathf.Clamp(Mathf.RoundToInt(F(c[13])), 1, 120);
                e.IntervalMin = Mathf.Max(0f, F(c[14]));
                e.IntervalMax = Mathf.Max(e.IntervalMin, F(c[15]));
                foreach (string w in c[16].Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = w.Split(':');
                    if (p.Length != 4 || (p[0] != "b" && p[0] != "t"))
                        throw new FormatException("air event " + e.Name + ": bad wave '" + w + "'");
                    Wave wv = new Wave();
                    wv.Bomber = p[0] == "b";
                    wv.Count = Mathf.Clamp(Mathf.RoundToInt(F(p[1])), 1, 6);
                    wv.Delay = Mathf.Clamp(F(p[2]), 0f, 1800f);
                    wv.Load = wv.Bomber ? Mathf.Clamp(Mathf.RoundToInt(F(p[3])), 20, 60)
                                        : Mathf.Clamp(Mathf.RoundToInt(F(p[3])), 1, 12);
                    e.Waves.Add(wv);
                }
                if (e.Waves.Count == 0) throw new FormatException("air event " + e.Name + ": no waves");
                e.Scene = c.Length > 17 ? c[17].Trim() : "";
                list.Add(e);
            }
            return list;
        }

        internal static void Load(bool force)
        {
            string[] live = LiveRoutes.Air;
            if (_loaded && !force && live == _source) return;
            _loaded = true;
            _source = live;
            string[] lines = live;
            string label = "live air events";
            if (lines == null)
            {
                string path = Path.Combine(RevivalPlugin.AssetDir ?? "", "ndr_airevents.tsv");
                label = "ndr_airevents.tsv";
                if (!File.Exists(path)) { Replace(new List<Event>(), label); return; }
                try { lines = File.ReadAllLines(path); }
                catch (Exception ex) { RevivalPlugin.L.LogError("AirEvents: reading " + path + ": " + ex.Message); return; }
            }
            try { Replace(Parse(lines), label); }
            catch (Exception ex) { RevivalPlugin.L.LogError("AirEvents: " + label + " rejected - " + ex.Message); }
        }

        static void Replace(List<Event> list, string label)
        {
            // Keep the schedule of an event that is still there by name.
            Dictionary<string, float> next = new Dictionary<string, float>();
            for (int i = 0; i < _events.Count; i++) next[_events[i].Name] = _events[i].Next;
            _events.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                float n;
                if (next.TryGetValue(list[i].Name, out n)) list[i].Next = n;
                _events.Add(list[i]);
            }
            RevivalPlugin.L.LogInfo("AirEvents: " + _events.Count + " air event(s) from " + label + ".");
        }

        // ============================================================ helpers

        static bool Master() { return RevivalTroopInsertion.MasterClient(); }

        static Vector2 Dir(float heading)
        {
            float a = heading * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(a), Mathf.Cos(a));
        }

        static Vector3 Ground(Vector3 xz)
        {
            float y;
            if (RevivalTroopInsertion.GroundY(xz, out y)) xz.y = y;
            return xz;
        }

        static string Cell(Vector3 p)
        {
            try { return RevivalTroopInsertion.GridCell(p); } catch { return "?"; }
        }

        static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        static string CompassOf(float bearing)
        {
            return Compass8[Mathf.RoundToInt(Mathf.Repeat(bearing, 360f) / 45f) % 8];
        }

        /// <summary>The flight direction over the target for an entry edge.</summary>
        static float HeadingFor(Event e)
        {
            switch (e.Edge)
            {
                case "N": return 180f;
                case "E": return 270f;
                case "S": return 0f;
                case "W": return 90f;
                case "random": return UnityEngine.Random.Range(0, 4) * 90f;
                default: return e.Heading;
            }
        }

        // ----------------------------------------------------------- safe zones

        static readonly List<Vector3> _safe = new List<Vector3>();
        static float _safeAt = -100f;

        /// <summary>Is this point inside a safe zone (a safe settlement or N12)?</summary>
        internal static bool Safe(Vector3 p)
        {
            if (Time.time - _safeAt > 30f) ScanSafe();
            for (int i = 0; i < _safe.Count; i++)
            {
                float dx = p.x - _safe[i].x, dz = p.z - _safe[i].z;
                if (dx * dx + dz * dz < SafeRadius * SafeRadius) return true;
            }
            return false;
        }

        static void ScanSafe()
        {
            _safeAt = Time.time;
            _safe.Clear();
            // Point N12, the neutral base (IsSafeSettlement, RE 39; Revival.NoFly.cs).
            if (MapScene.AtHome) _safe.Add(new Vector3(1446.6f, 0f, 1703.2f));
            try
            {
                Type st = RevivalPlugin.TypeByName("NPC_Settlement");
                FieldInfo flag = st == null ? null : AccessTools.Field(st, "IsSafeSettlement");
                if (flag == null || flag.FieldType != typeof(bool)) return;
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(st);
                for (int i = 0; i < all.Length; i++)
                {
                    Component c = all[i] as Component;
                    if (c != null && (bool)flag.GetValue(c)) _safe.Add(c.transform.position);
                }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents safe zones: " + ex.Message); }
        }

        // =============================================================== raid

        sealed class Sortie
        {
            public float At;
            public Wave W;
            public int Index;       // aircraft in its wave
        }

        sealed class Raid
        {
            public Event E;
            public float Heading;
            public Vector3 Target, Drop, Attack;
            public float Start;
            public readonly List<Sortie> Pending = new List<Sortie>();
            public readonly List<GameObject> Flying = new List<GameObject>();
            public bool NoBombs, NoDrop;
            public int Serial;
        }

        static readonly List<Raid> _raids = new List<Raid>();
        static int _serial;

        internal static int Active { get { return _raids.Count; } }

        /// <summary>
        /// Master: start one event. <paramref name="at"/> moves the whole event
        /// (target, drop zone, arrow) onto that point - the admin's "now at my
        /// position"; null flies it where the editor put it.
        /// </summary>
        static string Begin(Event e, Vector3? at, bool quick)
        {
            Raid r = new Raid();
            r.E = e;
            r.Heading = HeadingFor(e);
            Vector3 target = new Vector3(e.X, 0f, e.Z);
            Vector3 drop = new Vector3(e.DropX, 0f, e.DropZ);
            Vector3 attack = new Vector3(e.AttackX, 0f, e.AttackZ);
            if (at.HasValue)
            {
                Vector3 shift = new Vector3(at.Value.x - e.X, 0f, at.Value.z - e.Z);
                target += shift; drop += shift; attack += shift;
            }
            // An edge other than the drawn one turns the whole event about the
            // target, so the bomb line, the drop line and the arrow keep their
            // shape relative to the flight direction.
            float turn = r.Heading - e.Heading;
            if (Mathf.Abs(Mathf.DeltaAngle(turn, 0f)) > 0.5f)
            {
                Quaternion q = Quaternion.Euler(0f, turn, 0f);
                drop = target + q * (drop - target);
                attack = target + q * (attack - target);
            }
            r.Target = Ground(target);
            r.Drop = Ground(drop);
            r.Attack = Ground(attack);

            if (Safe(r.Target) && e.Bombers > 0) r.NoBombs = true;
            if (Safe(r.Drop) && e.Transports > 0) r.NoDrop = true;
            if ((r.NoBombs || e.Bombers == 0) && (r.NoDrop || e.Transports == 0))
            {
                RevivalPlugin.L.LogInfo("AirEvents: " + e.Name + " not flown - its target is in a safe zone.");
                return Loc.T("Воздушный налёт отменён: цель в безопасной зоне.",
                    "Air strike refused: the target is in a safe zone.");
            }

            // The first aircraft over the target WarnSeconds after the warning:
            // the launches wait for the lead the flight from the edge does not give.
            float warn = quick ? QuickWarnSeconds : WarnSeconds;
            float firstEta = 1e9f;
            for (int i = 0; i < e.Waves.Count; i++)
            {
                Wave w = e.Waves[i];
                if ((w.Bomber && r.NoBombs) || (!w.Bomber && r.NoDrop)) continue;
                float eta = w.Delay + Eta(w.Bomber ? r.Target : r.Drop, r.Heading, w.Bomber);
                firstEta = Mathf.Min(firstEta, eta);
            }
            float lead = Mathf.Max(0f, warn - firstEta);
            r.Start = Time.time;
            for (int i = 0; i < e.Waves.Count; i++)
            {
                Wave w = e.Waves[i];
                if ((w.Bomber && r.NoBombs) || (!w.Bomber && r.NoDrop)) continue;
                for (int k = 0; k < w.Count; k++)
                {
                    Sortie s = new Sortie();
                    s.W = w; s.Index = k;
                    // A wave flies abreast, the wingmen a second and a half apart.
                    s.At = r.Start + lead + w.Delay + k * 1.5f;
                    r.Pending.Add(s);
                }
            }
            r.Serial = ++_serial;
            _raids.Add(r);

            int bombers = r.NoBombs ? 0 : e.Bombers, transports = r.NoDrop ? 0 : e.Transports;
            float from = Mathf.Repeat(r.Heading + 180f, 360f);
            float siren = lead + Mathf.Min(firstEta, 120f) + 45f;
            Vector3 alarm = bombers > 0 ? r.Target : r.Drop;
            float[] warnMsg = new float[] { 0f, alarm.x, alarm.z, siren, from, bombers, transports, lead + firstEta };
            Net.Send(warnMsg, true);
            OnWarn(warnMsg);
            RevivalPlugin.L.LogInfo("AirEvents: " + e.Name + " begins - " + bombers + " Tu-95, " + transports
                + " An-2, from " + CompassOf(from) + ", target " + r.Target.ToString("0") + ", first over it in "
                + Mathf.RoundToInt(lead + firstEta) + " s" + (r.NoBombs ? " (bombs cancelled: safe zone)" : "")
                + (r.NoDrop ? " (paradrop cancelled: safe zone)" : "") + ".");
            return Loc.T("Воздушный налёт: ", "Air strike: ") + bombers + " Tu-95, " + transports + " An-2, "
                + Loc.T("с ", "from ") + CompassOf(from) + Loc.T(", над целью через ", ", over the target in ")
                + Mathf.RoundToInt(lead + firstEta) + " s (" + Cell(alarm) + ").";
        }

        static float Eta(Vector3 over, float heading, bool bomber)
        {
            Vector2 from, to;
            FlightPath.AcrossMap(over, Mathf.Repeat(heading + 180f, 360f), out from, out to);
            float speed = (bomber ? BomberKmh : TransportKmh) / 3.6f * K;
            return (new Vector2(over.x, over.z) - from).magnitude / speed;
        }

        static void TickRaids()
        {
            for (int i = _raids.Count - 1; i >= 0; i--)
            {
                Raid r = _raids[i];
                for (int j = r.Pending.Count - 1; j >= 0; j--)
                {
                    Sortie s = r.Pending[j];
                    if (Time.time < s.At) continue;
                    r.Pending.RemoveAt(j);
                    GameObject go = s.W.Bomber ? LaunchBomber(r, s) : LaunchTransport(r, s);
                    if (go != null) r.Flying.Add(go);
                }
                for (int j = r.Flying.Count - 1; j >= 0; j--)
                    if (r.Flying[j] == null || !NpcAircraft.Is(r.Flying[j]) || PlayerAn2.Down(r.Flying[j]))
                        r.Flying.RemoveAt(j);
                if (r.Pending.Count == 0 && r.Flying.Count == 0 && _drops.Count == 0)
                {
                    RevivalPlugin.L.LogInfo("AirEvents: " + r.E.Name + " is over.");
                    _raids.RemoveAt(i);
                }
            }
        }

        /// <summary>Lateral offset of aircraft k of n across the target width.</summary>
        static float Offset(int k, int n, float width)
        {
            if (n <= 1) return 0f;
            return -width * 0.5f + width * k / (n - 1);
        }

        static FlightPath PathOver(Vector3 over, float heading, float altitudeM, float kmh)
        {
            Vector2 from, to;
            FlightPath.AcrossMap(over, Mathf.Repeat(heading + 180f, 360f), out from, out to);
            return FlightPath.Straight(from, to, altitudeM * K, kmh / 3.6f * K);
        }

        static GameObject LaunchBomber(Raid r, Sortie s)
        {
            Vector2 d = Dir(r.Heading);
            Vector3 fwd = new Vector3(d.x, 0f, d.y), right = new Vector3(d.y, 0f, -d.x);
            Vector3 centre = r.Target + right * Offset(s.Index, s.W.Count, r.E.Width);
            FlightPath path = PathOver(centre, r.Heading, BomberAltitudeM, BomberKmh);
            Vector3 lineStart = centre - fwd * (r.E.Length * 0.5f);
            Vector3 lineEnd = centre + fwd * (r.E.Length * 0.5f);
            int bombs = s.W.Load;
            bool released = false;
            float sLine = path.Project(lineStart);
            float groundY = Ground(centre).y;
            NpcAircraft.Flight f = NpcAircraft.Launch(path, true, BomberTag + r.E.Name,
                delegate(GameObject go, float at)
                {
                    if (released) return;
                    float h = Mathf.Max(20f, path.At(at).y - groundY);
                    float fall = Mathf.Sqrt(2f * h / Gravity);
                    if (at < sLine - path.Speed * fall) return;
                    released = true;
                    Release(go, lineStart, lineEnd, bombs, fall, path.Speed);
                },
                null);
            if (f == null) return null;
            f.Toughness = 3;
            return f.Go;
        }

        static GameObject LaunchTransport(Raid r, Sortie s)
        {
            Vector2 d = Dir(r.Heading);
            Vector3 fwd = new Vector3(d.x, 0f, d.y), right = new Vector3(d.y, 0f, -d.x);
            Vector3 centre = r.Drop + right * Offset(s.Index, s.W.Count, Mathf.Min(r.E.Width, 200f));
            FlightPath path = PathOver(centre, r.Heading, TransportAltitudeM, TransportKmh);
            int men = s.W.Load;
            float spread = men > 1 ? JumpSpread : 0f;
            float sJump = path.Project(centre - fwd * (spread * 0.5f));
            bool jumped = false;
            Raid raid = r;
            NpcAircraft.Flight f = NpcAircraft.Launch(path, true, TransportTag + r.E.Name,
                delegate(GameObject go, float at)
                {
                    if (jumped || at < sJump) return;
                    jumped = true;
                    Jump(go, raid, men, spread / Mathf.Max(1, men - 1) / path.Speed, path);
                },
                null);
            return f == null ? null : f.Go;
        }

        // ============================================================== bombs

        /// <summary>Master: the stick, every bomb's release offset and impact
        /// point decided here and sent once, so every client sees the same carpet.</summary>
        static void Release(GameObject plane, Vector3 lineStart, Vector3 lineEnd, int n, float fall, float speed)
        {
            List<float> msg = new List<float>();
            msg.Add(1f); msg.Add(PlayerAn2.View(plane)); msg.Add(fall); msg.Add(0f);
            float step = (lineEnd - lineStart).magnitude / Mathf.Max(1, n);
            Vector3 fwd = (lineEnd - lineStart).normalized, right = new Vector3(fwd.z, 0f, -fwd.x);
            int kept = 0, safe = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = Vector3.Lerp(lineStart, lineEnd, (i + 0.5f) / n);
                // Dispersion of a level release from 350 m: a few metres along,
                // a little more across (the bombs leave a swaying bay).
                p += fwd * (Gauss() * 3f * K) + right * (Gauss() * 4f * K);
                if (Safe(p)) { safe++; continue; }
                msg.Add(i * step / speed); msg.Add(p.x); msg.Add(p.z);
                kept++;
            }
            msg[3] = kept;
            float[] f = msg.ToArray();
            Net.Send(f, true);
            OnStick(f);
            RevivalPlugin.L.LogInfo("AirEvents: Tu-95 " + PlayerAn2.View(plane) + " releases " + kept + " FAB-250 ("
                + safe + " held over a safe zone), fall " + fall.ToString("0.0") + " s, stick "
                + Mathf.RoundToInt((lineEnd - lineStart).magnitude) + " u.");
        }

        static float Gauss()
        {
            float u = Mathf.Max(1e-4f, UnityEngine.Random.value), v = UnityEngine.Random.value;
            return Mathf.Clamp(Mathf.Sqrt(-2f * Mathf.Log(u)) * Mathf.Cos(2f * Mathf.PI * v), -2.5f, 2.5f);
        }

        sealed class Bomb
        {
            public int View;
            public float Release, Fall;
            public Vector3 Impact, From;
            public bool Out, Dropped;
            public GameObject Go;
        }

        static readonly List<Bomb> _bombs = new List<Bomb>();

        static void OnStick(float[] f)
        {
            if (f.Length < 4) return;
            int view = (int)f[1];
            float fall = f[2];
            int n = (int)f[3];
            float now = Time.time;
            for (int i = 0; i < n && 4 + i * 3 + 2 < f.Length; i++)
            {
                Bomb b = new Bomb();
                b.View = view;
                b.Fall = Mathf.Clamp(fall, 1f, 30f);
                b.Release = now + f[4 + i * 3];
                b.Impact = Ground(new Vector3(f[5 + i * 3], 0f, f[6 + i * 3]));
                _bombs.Add(b);
            }
            GameObject plane = PlayerAn2.ByViewId(view);
            Tu95Visual vis = plane == null ? null : plane.GetComponentInChildren<Tu95Visual>();
            if (vis != null) vis.OpenBay(4f + (n > 0 ? f[4 + (n - 1) * 3] : 0f));
        }

        static void TickBombs()
        {
            if (_bombs.Count == 0) return;
            float now = Time.time;
            bool master = Master();
            for (int i = _bombs.Count - 1; i >= 0; i--)
            {
                Bomb b = _bombs[i];
                if (!b.Out)
                {
                    if (now < b.Release) continue;
                    b.Out = true;
                    GameObject plane = PlayerAn2.ByViewId(b.View);
                    // A bomber shot down before this bomb left the bay drops nothing more.
                    if (plane != null && PlayerAn2.Down(plane)) { _bombs.RemoveAt(i); continue; }
                    b.Dropped = true;
                    b.From = plane != null ? plane.transform.position + Vector3.down * (2f * K)
                        : b.Impact + Vector3.up * (BomberAltitudeM * K);
                    b.Go = BombPool.Take();
                }
                float t = now - b.Release;
                if (t >= b.Fall)
                {
                    BombPool.Give(b.Go);
                    Burst(b.Impact, master);
                    _bombs.RemoveAt(i);
                    continue;
                }
                if (b.Go != null)
                {
                    // Horizontal straight to the impact, vertical the parabola
                    // that meets it at the fall time.
                    float u = t / b.Fall;
                    Vector3 p = Vector3.Lerp(b.From, b.Impact, u);
                    float vy0 = (b.Impact.y - b.From.y + 0.5f * Gravity * b.Fall * b.Fall) / b.Fall;
                    p.y = b.From.y + vy0 * t - 0.5f * Gravity * t * t;
                    Vector3 vel = (b.Impact - b.From) / b.Fall;
                    vel.y = vy0 - Gravity * t;
                    b.Go.transform.position = p;
                    if (vel.sqrMagnitude > 0.01f) b.Go.transform.rotation = Quaternion.LookRotation(vel.normalized);
                }
            }
        }

        static float _lastSweepLog;

        static void Burst(Vector3 at, bool master)
        {
            BurstFx.Play(at);
            if (!master) return;
            float radius = BombRadiusM * K;
            int npc, veh, plr;
            Mortar.AnyFaction = true;
            try
            {
                Mortar.Sweep(at, true, radius, BombNpcPeak, BombVehiclePeak, BombPlayerPeak, out npc, out veh, out plr);
            }
            finally { Mortar.AnyFaction = false; }
            if (npc + veh + plr > 0 || Time.time - _lastSweepLog > 5f)
            {
                _lastSweepLog = Time.time;
                RevivalPlugin.L.LogInfo("AirEvents: FAB-250 at " + at.ToString("0") + " - " + npc + " NPC, "
                    + veh + " vehicle, " + plr + " player hit.");
            }
        }

        // ========================================================== paradrop

        sealed class Jumper
        {
            public float Jump, Land;
            public Vector3 From, Ground;
            public GameObject Go;
        }

        sealed class Stick
        {
            public int View;
            public readonly List<Jumper> Men = new List<Jumper>();
            public Raid R;               // master only
            public bool Spawned;
        }

        static readonly List<Stick> _drops = new List<Stick>();

        /// <summary>Master: the transport is at the drop line - the men go.</summary>
        static void Jump(GameObject plane, Raid r, int men, float interval, FlightPath path)
        {
            if (r != null && Safe(r.Drop)) return;
            Vector3 p = plane.transform.position;
            Vector3 v = path.Velocity(path.Project(p));
            float descent = DescentMs * K;
            float[] msg = new float[] { 2f, PlayerAn2.View(plane), men, interval, descent, p.x, p.y, p.z, v.x, v.z };
            Net.Send(msg, true);
            Stick s = OnJump(msg);
            if (s != null) s.R = r;
            RevivalPlugin.L.LogInfo("AirEvents: An-2 " + PlayerAn2.View(plane) + " drops " + men
                + " paratroopers over " + p.ToString("0") + ".");
        }

        static Stick OnJump(float[] f)
        {
            if (f.Length < 10) return null;
            Stick s = new Stick();
            s.View = (int)f[1];
            int n = Mathf.Clamp((int)f[2], 1, 12);
            float interval = f[3], descent = Mathf.Max(1f, f[4]);
            Vector3 p0 = new Vector3(f[5], f[6], f[7]);
            Vector3 v = new Vector3(f[8], 0f, f[9]);
            float now = Time.time;
            for (int i = 0; i < n; i++)
            {
                Jumper j = new Jumper();
                j.Jump = now + i * interval;
                // Out of the door: 25 m of forward throw before the canopy holds.
                j.From = p0 + v * (i * interval) + v.normalized * (25f * K) + Vector3.down * (15f * K);
                j.Ground = Ground(new Vector3(j.From.x, 0f, j.From.z));
                j.Land = j.Jump + Mathf.Max(3f, (j.From.y - j.Ground.y) / descent);
                s.Men.Add(j);
            }
            _drops.Add(s);
            return s;
        }

        static void TickDrops()
        {
            if (_drops.Count == 0) return;
            float now = Time.time;
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                Stick s = _drops[i];
                GameObject plane = PlayerAn2.ByViewId(s.View);
                bool planeDown = plane != null && PlayerAn2.Down(plane);
                float last = 0f;
                for (int k = s.Men.Count - 1; k >= 0; k--)
                {
                    Jumper j = s.Men[k];
                    if (j.Go == null && now < j.Jump)
                    {
                        // Still aboard: a transport going down takes them with it.
                        if (planeDown) { s.Men.RemoveAt(k); continue; }
                        last = Mathf.Max(last, j.Land);
                        continue;
                    }
                    last = Mathf.Max(last, j.Land);
                    if (j.Go == null && now < j.Land) j.Go = Canopy.Make();
                    if (j.Go == null) continue;
                    float u = Mathf.Clamp01((now - j.Jump) / Mathf.Max(0.1f, j.Land - j.Jump));
                    Vector3 p = Vector3.Lerp(j.From, j.Ground, u);
                    // A gentle oscillation under the canopy.
                    float sway = Mathf.Sin((now + k * 1.7f) * 1.3f) * 6f;
                    j.Go.transform.position = p;
                    j.Go.transform.rotation = Quaternion.Euler(sway * (1f - u), k * 40f, sway * 0.6f * (1f - u));
                    if (now > j.Land + 1.5f) { UnityEngine.Object.Destroy(j.Go); j.Go = null; }
                }
                if (Master() && !s.Spawned && s.Men.Count > 0 && now >= last)
                {
                    s.Spawned = true;
                    SpawnSquad(s);
                }
                bool done = s.Men.Count == 0 || (now > last + 2f);
                if (done)
                {
                    for (int k = 0; k < s.Men.Count; k++) if (s.Men[k].Go != null) UnityEngine.Object.Destroy(s.Men[k].Go);
                    if (Master() && !s.Spawned && s.Men.Count > 0) SpawnSquad(s);
                    _drops.RemoveAt(i);
                }
            }
        }

        /// <summary>Master: the stick has landed - the men are real NPCs now,
        /// with the arrow of the event, the heli troop landing's own way.</summary>
        static void SpawnSquad(Stick s)
        {
            s.Spawned = true;
            Raid r = s.R;
            if (r == null) return;     // a stick the master only saw (a new master): no squad twice
            try
            {
                Vector3[] at = new Vector3[s.Men.Count];
                Vector3 c = Vector3.zero;
                for (int i = 0; i < at.Length; i++) { at[i] = s.Men[i].Ground; c += at[i]; }
                c /= Mathf.Max(1, at.Length);
                List<RevivalComposition.CrewMan> none = new List<RevivalComposition.CrewMan>();
                GameObject settlement = Crew.DropSquadAt(c, at, r.Heading, r.E.Faction, null);
                Array men = Crew.Men(settlement);
                if (settlement == null || men == null || men.Length == 0)
                {
                    RevivalPlugin.L.LogWarning("AirEvents: no paratrooper of " + r.E.Name + " could be spawned.");
                    if (settlement != null) UnityEngine.Object.Destroy(settlement);
                    return;
                }
                List<Vector3> arrow = new List<Vector3>();
                arrow.Add(Ground(new Vector3(c.x, 0f, c.z)));
                arrow.Add(r.Attack);
                NpcWar.StartOperation("air-" + r.E.Name + "-" + r.Serial + "-" + s.View, settlement, men, arrow,
                    r.E.PatrolMinutes * 60f, none);
                float[] msg = new float[] { 5f, c.x, c.z };
                Net.Send(msg, true);
                OnLanded(msg);
                RevivalPlugin.L.LogInfo("AirEvents: " + men.Length + " paratroopers (" + r.E.Faction + ") of "
                    + r.E.Name + " down at " + c.ToString("0") + ", attacking toward " + r.Attack.ToString("0") + ".");
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("AirEvents squad: " + ex); }
        }

        // ============================================================ warning

        static string _banner = "";
        static float _bannerUntil;
        static AudioSource _siren, _drone;
        static float _sirenUntil, _droneUntil, _warnAt;
        static Vector3 _droneFrom, _droneTo;

        static void OnWarn(float[] f)
        {
            if (f.Length < 8) return;
            Vector3 at = Ground(new Vector3(f[1], 0f, f[2]));
            float siren = Mathf.Clamp(f[3], 10f, 400f);
            float from = f[4];
            int bombers = (int)f[5], transports = (int)f[6];
            int eta = Mathf.RoundToInt(f[7]);
            string cell = Cell(at);
            string what = (bombers > 0 ? bombers + " Tu-95" : "") + (bombers > 0 && transports > 0 ? " + " : "")
                + (transports > 0 ? transports + " An-2" : "");
            // The siren at the target, the radar's report, the drone from the entry side.
            _warnAt = Time.time;
            _sirenUntil = Time.time + siren;
            try
            {
                if (_siren == null) _siren = Source("NDR air raid siren (air event)", RadarSiren.Clip(), 150f, 4500f);
                if (_siren != null)
                {
                    _siren.transform.position = at + Vector3.up * (12f * K);
                    if (!_siren.isPlaying) _siren.Play();
                }
                Vector2 d = Dir(from);
                _droneTo = at + Vector3.up * (BomberAltitudeM * K);
                _droneFrom = _droneTo + new Vector3(d.x, 0f, d.y) * 4200f;
                _droneUntil = Time.time + Mathf.Max(8f, f[7]);
                if (_drone == null) _drone = Source("NDR air raid drone", Tu95Visual.DroneClip(), 400f, 9000f);
                if (_drone != null) { _drone.transform.position = _droneFrom; _drone.volume = 0f; if (!_drone.isPlaying) _drone.Play(); }
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents warning sound: " + ex.Message); }
            bool radar = TowerRadar.On && TowerRadar.RadarAlive;
            if (radar)
            {
                try { RadarScope.Note("AIR RAID: " + what + " inbound from " + CompassOf(from) + ", ETA " + eta + " s, " + cell); }
                catch { }
            }
            _banner = (radar ? Loc.T("РЛС: ", "RADAR: ") : "")
                + Loc.T("ВОЗДУШНАЯ ТРЕВОГА! ", "AIR RAID WARNING! ") + what
                + Loc.T(" с направления ", " inbound from ") + CompassOf(from)
                + Loc.T(" на квадрат ", " to square ") + cell + Loc.T(", через ~", ", ETA ~") + eta + " s";
            _bannerUntil = Time.time + 14f;
            RevivalPlugin.L.LogInfo("AirEvents: warning - " + what + " from " + CompassOf(from) + " to " + cell
                + ", siren " + Mathf.RoundToInt(siren) + " s" + (radar ? ", radar reports it." : "."));
        }

        static void OnLanded(float[] f)
        {
            if (f.Length < 3) return;
            _banner = Loc.T("Десант высадился в квадрате ", "Paratroopers have landed in square ")
                + Cell(new Vector3(f[1], 0f, f[2])) + "!";
            _bannerUntil = Time.time + 12f;
        }

        static AudioSource Source(string name, AudioClip clip, float min, float max)
        {
            if (clip == null) return null;
            GameObject go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = min;
            s.maxDistance = max;
            s.volume = 1f;
            return s;
        }

        static void TickWarning()
        {
            float now = Time.time;
            if (_siren != null)
            {
                float left = _sirenUntil - now;
                if (left <= 0f) { if (_siren.isPlaying) _siren.Stop(); }
                else
                {
                    _siren.volume = Mathf.Clamp01(Mathf.Min((now - _warnAt) / 3f, left / 5f));
                    _siren.pitch = 0.7f + 0.3f * _siren.volume;
                }
            }
            if (_drone != null)
            {
                if (now > _droneUntil + 6f) { if (_drone.isPlaying) _drone.Stop(); }
                else
                {
                    // Far off and faint at first, swelling from the entry side;
                    // the real aircraft's own drone takes over as they arrive.
                    float span = Mathf.Max(1f, _droneUntil - _warnAt);
                    float u = Mathf.Clamp01((now - _warnAt) / span);
                    _drone.transform.position = Vector3.Lerp(_droneFrom, Vector3.Lerp(_droneFrom, _droneTo, 0.5f), u);
                    float fadeOut = Mathf.Clamp01((_droneUntil + 6f - now) / 6f);
                    _drone.volume = Mathf.Clamp01(0.25f + 0.75f * u) * fadeOut;
                }
            }
        }

        internal static void Draw()
        {
            if (Time.time > _bannerUntil || _banner.Length == 0) return;
            if (CfgBanner != null && !CfgBanner.Value) return;
            try
            {
                string text = "<b>" + _banner + "</b>";
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.fontSize = 20;
                st.richText = true;
                Vector2 size = st.CalcSize(new GUIContent(text));
                float x = (Screen.width - size.x) * 0.5f, y = Screen.height * 0.12f;
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x - 12f, y - 6f, size.x + 24f, size.y + 12f), Texture2D.whiteTexture);
                GUI.color = (Time.time * 2f) % 2f < 1.4f ? new Color(1f, 0.35f, 0.25f) : new Color(1f, 0.85f, 0.4f);
                GUI.Label(new Rect(x, y, size.x + 4f, size.y), text, st);
                GUI.color = old;
            }
            catch { }
        }

        // ============================================================== frame

        static int _errors;

        internal static void Tick()
        {
            try
            {
                Net.EnsureHooked();
                Load(false);
                TickBombs();
                TickDrops();
                TickWarning();
                BurstFx.Tick();
                if (!Master()) return;
                TickRaids();
                if (MapTools.LocalPlayer() == null) return;
                for (int i = 0; i < _events.Count; i++)
                {
                    Event e = _events[i];
                    if (!e.Enabled || e.IntervalMax <= 0f || !e.Here) continue;
                    if (Running(e)) { e.Next = -1f; continue; }
                    if (e.Next < 0f)
                    {
                        float hours = UnityEngine.Random.Range(e.IntervalMin, e.IntervalMax);
                        e.Next = Time.time + hours * 3600f;
                        RevivalPlugin.L.LogInfo("AirEvents: next " + e.Name + " in " + hours.ToString("0.00") + " h.");
                        continue;
                    }
                    if (Time.time < e.Next) continue;
                    // The target must be in a world that is loaded (the east
                    // airfield only exists with the east tile).
                    float y;
                    if (!RevivalTroopInsertion.GroundY(new Vector3(e.X, 0f, e.Z), out y))
                    {
                        e.Next = Time.time + 600f;
                        RevivalPlugin.L.LogInfo("AirEvents: " + e.Name + " waits - no ground under its target here.");
                        continue;
                    }
                    e.Next = -1f;
                    Begin(e, null, false);
                }
                _errors = 0;
            }
            catch (Exception ex)
            {
                if (++_errors <= 3) RevivalPlugin.L.LogError("AirEvents: " + ex);
            }
        }

        static bool Running(Event e)
        {
            for (int i = 0; i < _raids.Count; i++) if (_raids[i].E == e) return true;
            return false;
        }

        // ============================================================== admin

        /// <summary>The panel's template: -1 = the built-in default raid,
        /// else an event of the editor table.</summary>
        internal static int Template = 0;
        internal static bool Quick = false;

        static Event TemplateEvent(int index)
        {
            List<Event> here = new List<Event>();
            for (int i = 0; i < _events.Count; i++) if (_events[i].Here) here.Add(_events[i]);
            if (here.Count > 0) return here[Mathf.Clamp(index, 0, here.Count - 1)];
            // Nothing authored for this map: one Tu-95, then two An-2 90 s later.
            Event e = new Event();
            e.Name = "Admin-strike";
            e.Heading = 0f; e.Length = 900f; e.Width = 150f; e.Edge = "random";
            e.DropX = 0f; e.DropZ = -350f; e.AttackX = 0f; e.AttackZ = 350f;
            Wave b = new Wave(); b.Bomber = true; b.Count = 1; b.Load = 48; e.Waves.Add(b);
            Wave t = new Wave(); t.Bomber = false; t.Count = 2; t.Delay = 90f; t.Load = 10; e.Waves.Add(t);
            return e;
        }

        static int TemplateCount()
        {
            int n = 0;
            for (int i = 0; i < _events.Count; i++) if (_events[i].Here) n++;
            return n;
        }

        internal static string NowAtMe()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return "No local player.";
            return Ask(me.transform.position);
        }

        /// <summary>The admin's "now at this point": the master runs it, anyone else asks.</summary>
        internal static string Ask(Vector3 point)
        {
            if (Safe(point))
                return Loc.T("Воздушный налёт отменён: цель в безопасной зоне.",
                    "Air strike refused: the target is in a safe zone.");
            if (Master()) return NowAt(point, Template, Quick);
            if (!Net.Send(new float[] { 3f, point.x, point.y, point.z, Template, Quick ? 1f : 0f }, true))
                return "Air strike: the network channel is not up - see the log.";
            return Loc.T("Воздушный налёт запрошен у хоста.", "Air strike asked of the host.");
        }

        static string NowAt(Vector3 point, int template, bool quick)
        {
            if (!Master()) return "Air strike: only the host can launch.";
            Event e = TemplateEvent(template);
            return Begin(e, point, quick);
        }

        internal static string Clear()
        {
            if (!Master())
                return Net.Send(new float[] { 4f }, true)
                    ? "Air strike: asked the host to call it off."
                    : "Air strike: the network channel is not up - see the log.";
            return ClearHere();
        }

        static string ClearHere()
        {
            int raids = _raids.Count;
            _raids.Clear();
            int planes = NpcAircraft.ClearAll();
            return "Air strike: " + raids + " raid(s) called off, " + planes + " aircraft removed.";
        }

        /// <summary>The panel rows (Revival.Admin.cs), GUILayout.</summary>
        internal static void Options()
        {
            GUILayout.BeginHorizontal();
            int n = TemplateCount();
            Event e = TemplateEvent(Template);
            GUILayout.Label("  Event", GUILayout.Width(70f));
            if (GUILayout.Button("<", GUILayout.Width(24f)) && n > 0) Template = (Template + n - 1) % n;
            GUILayout.Label(e.Name + " (" + e.Bombers + " Tu-95, " + e.Transports + " An-2)", GUILayout.Width(230f));
            if (GUILayout.Button(">", GUILayout.Width(24f)) && n > 0) Template = (Template + 1) % n;
            Quick = GUILayout.Toggle(Quick, " quick warning (" + Mathf.RoundToInt(QuickWarnSeconds) + " s)");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("  raids: " + _raids.Count + ", events here: " + n + ", next: " + NextText());
            GUILayout.EndHorizontal();
        }

        static string NextText()
        {
            float best = -1f; string name = "";
            for (int i = 0; i < _events.Count; i++)
            {
                Event e = _events[i];
                if (!e.Enabled || !e.Here || e.Next < 0f) continue;
                if (best < 0f || e.Next < best) { best = e.Next; name = e.Name; }
            }
            if (best < 0f) return Master() ? "-" : "(host)";
            return name + " in " + ((best - Time.time) / 3600f).ToString("0.0") + " h";
        }

        // ======================================================= Tu-95 visual

        /// <summary>Every client, from PlayerAn2.Prepare: a bomber's carrier
        /// gets the Tu-95 instead of the An-2.</summary>
        internal static void Prepared(GameObject go, object[] data)
        {
            if (go == null || data == null || data.Length < 11) return;
            string label = data[10] as string;
            if (label == null || !label.StartsWith(BomberTag, StringComparison.Ordinal)) return;
            try
            {
                if (go.GetComponentInChildren<Tu95Visual>() != null) return;
                if (!Tu95Model.Load()) return;
                // The An-2 model stays active (An2Visual drives it and its engine
                // sound every frame) but is not drawn and not heard.
                Transform an2 = go.transform.Find("NDR_An2");
                if (an2 != null)
                    foreach (Renderer r in an2.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                GameObject root = new GameObject("NDR_Tu95");
                root.transform.SetParent(go.transform, false);
                Tu95Visual vis = root.AddComponent<Tu95Visual>();
                vis.Build(an2);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents Tu-95 visual: " + ex.Message); }
        }

        static bool IsBomber(GameObject go)
        {
            return go != null && go.GetComponentInChildren<Tu95Visual>(true) != null;
        }

        internal static float WreckSeconds(GameObject go, float normal)
        {
            return IsBomber(go) ? Mathf.Max(normal, WreckSeconds_) : normal;
        }

        /// <summary>Every client, from PlayerAn2.Burn: the Tu-95 lies as its
        /// wreck, and the wreck has a hold to loot.</summary>
        internal static void Burned(GameObject go)
        {
            Tu95Visual vis = go == null ? null : go.GetComponentInChildren<Tu95Visual>(true);
            if (vis == null) return;
            try
            {
                vis.Wreck();
                // The An-2's hull boxes would stand between the looter and the hold plate.
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("NDR_An2Hull", StringComparison.Ordinal)) t.gameObject.SetActive(false);
                HeliHold.Attach(go);
                if (Master()) Stock(go);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents wreck: " + ex.Message); }
        }

        /// <summary>Master: what a Bear's crew and cargo leave behind.</summary>
        static void Stock(GameObject go)
        {
            object data = Turret.TrunkDataOf(go.transform);
            if (data == null) { RevivalPlugin.L.LogWarning("AirEvents: the Tu-95 wreck has no hold to stock."); return; }
            string[] pools = { "military", "military", "guard", "parts", "salvage", "medical", "military", "parts" };
            int placed = 0;
            int n = UnityEngine.Random.Range(6, 10);
            for (int i = 0; i < n; i++)
            {
                int id = Airfield.LootItemId(pools[i % pools.Length]);
                if (id <= 0) continue;
                if (Turret.AddToContainer(data, id, 1)) placed++;
            }
            // The crew's parachutes: they did not get out.
            for (int i = 0; i < 2; i++) if (Turret.AddToContainer(data, Parachute.ItemId, 1)) placed++;
            RevivalPlugin.L.LogInfo("AirEvents: the Tu-95 wreck holds " + placed + " item(s).");
        }

        // ============================================================ network

        internal static class Net
        {
            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optType;

            static int Code() { return Mathf.Clamp(CfgEventCode == null ? 163 : CfgEventCode.Value, 0, 199); }

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
                        RevivalPlugin.L.LogWarning("AirEvents net: RaiseEvent or OnEventCall missing.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("AirEvents net hooked: event code " + Code() + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("AirEvents net not hooked: " + ex);
                }
            }

            internal static bool Send(float[] content, bool reliable)
            {
                EnsureHooked();
                if (!_hooked) return false;
                try
                {
                    object opts = _optType == null ? null : Activator.CreateInstance(_optType);
                    _raise.Invoke(null, new object[] { (byte)Code(), content, reliable, opts });
                    return true;
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("AirEvents net send: " + ex.Message);
                    return false;
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                try
                {
                    if (code != Code()) return;
                    float[] f = content as float[];
                    if (f == null || f.Length < 1) return;
                    int kind = (int)f[0];
                    switch (kind)
                    {
                        case 0: OnWarn(f); break;
                        case 1: OnStick(f); break;
                        case 2: OnJump(f); break;
                        case 5: OnLanded(f); break;
                        case 3:
                            if (Master() && f.Length >= 6)
                                RevivalPlugin.L.LogInfo("AirEvents: strike for player " + sender + " - "
                                    + NowAt(new Vector3(f[1], f[2], f[3]), (int)f[4], f[5] > 0.5f));
                            break;
                        case 4:
                            if (Master()) RevivalPlugin.L.LogInfo("AirEvents: for player " + sender + " - " + ClearHere());
                            break;
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("AirEvents net receive: " + ex.Message);
                }
            }
        }
    }

    // =====================================================================
    // Pooled bomb bodies and bursts

    /// <summary>The falling FAB-250s: a fixed pool of bodies, reused.</summary>
    internal static class BombPool
    {
        const int Max = 64;
        static readonly Stack<GameObject> _free = new Stack<GameObject>();
        static int _made;
        static Mesh _mesh;
        static Material _mat;
        static bool _tried;
        static Quaternion _turn = Quaternion.identity;
        static float _scale = 1f;
        static Vector3 _centre;

        internal static GameObject Take()
        {
            while (_free.Count > 0)
            {
                GameObject go = _free.Pop();
                if (go != null) { go.SetActive(true); return go; }
            }
            if (_made >= Max || !Model()) return null;
            _made++;
            GameObject root = new GameObject("NDR_Fab250");
            UnityEngine.Object.DontDestroyOnLoad(root);
            GameObject part = new GameObject("mesh");
            part.transform.SetParent(root.transform, false);
            part.transform.localRotation = _turn;
            part.transform.localScale = Vector3.one * _scale;
            part.transform.localPosition = -(_turn * (_centre * _scale));
            part.AddComponent<MeshFilter>().sharedMesh = _mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = _mat;
            return root;
        }

        internal static void Give(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            _free.Push(go);
        }

        /// <summary>The FAB-50 item mesh stretched to a FAB-250's 2.0 m, olive.</summary>
        static bool Model()
        {
            if (_tried) return _mesh != null;
            _tried = true;
            try
            {
                _mesh = Assets.Load("fab50.ndmesh");
                if (_mesh == null) return false;
                Bounds b = _mesh.bounds;
                Vector3 s = b.size;
                Vector3 axis = s.x >= s.y && s.x >= s.z ? Vector3.right : (s.y >= s.z ? Vector3.up : Vector3.forward);
                float len = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                _turn = Quaternion.FromToRotation(axis, Vector3.forward);
                _scale = len > 1e-4f ? 2.0f * PlayerAn2.K / len : 1f;
                _centre = b.center;
                Shader sh = Shader.Find("Standard");
                _mat = new Material(sh != null ? sh : Shader.Find("Legacy Shaders/Diffuse"));
                _mat.name = "NDR_Fab250";
                Texture2D tex = Assets.Texture("fab50_diffuse.png", false, true);
                if (tex != null) _mat.mainTexture = tex;
                _mat.color = new Color(0.62f, 0.66f, 0.55f);
            }
            catch (Exception ex)
            {
                _mesh = null;
                RevivalPlugin.L.LogWarning("AirEvents bomb model: " + ex.Message);
            }
            return _mesh != null;
        }
    }

    /// <summary>
    /// The bursts of a carpet: ONE flash system and ONE dust system for every
    /// bomb (Emit at each impact), and six audio sources in turn. Forty-eight
    /// bombs in six seconds cost what one does.
    /// </summary>
    internal static class BurstFx
    {
        static ParticleSystem _flash, _dust;
        static bool _tried;
        static readonly AudioSource[] _audio = new AudioSource[6];
        static int _nextAudio;
        static float _lastSound;
        static AudioClip _clip;

        internal static void Tick() { }

        internal static void Play(Vector3 at)
        {
            Sound(at);
            // [Effects] ParticleDensity: the counts scale with the level, Off draws none.
            if (!Fx.On || !Make()) return;
            float K = PlayerAn2.K;
            int flashes = Mathf.Max(1, Mathf.RoundToInt(5f * Fx.Factor));
            int puffs = Mathf.Max(1, Mathf.RoundToInt(7f * Fx.Factor));
            ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
            for (int i = 0; i < flashes; i++)
            {
                p.position = at + UnityEngine.Random.insideUnitSphere * (2f * K) + Vector3.up * (2f * K);
                p.velocity = UnityEngine.Random.insideUnitSphere * (6f * K) + Vector3.up * (4f * K);
                p.startSize = UnityEngine.Random.Range(9f, 15f) * K;
                p.startLifetime = UnityEngine.Random.Range(0.35f, 0.6f);
                p.startColor = new Color(1f, UnityEngine.Random.Range(0.6f, 0.85f), 0.3f, 1f);
                _flash.Emit(p, 1);
            }
            for (int i = 0; i < puffs; i++)
            {
                p.position = at + new Vector3(UnityEngine.Random.Range(-4f, 4f), 1f, UnityEngine.Random.Range(-4f, 4f)) * K;
                Vector3 v = UnityEngine.Random.insideUnitSphere * (3f * K);
                v.y = Mathf.Abs(v.y) + UnityEngine.Random.Range(4f, 9f) * K;
                p.velocity = v;
                p.startSize = UnityEngine.Random.Range(10f, 20f) * K;
                p.startLifetime = UnityEngine.Random.Range(3.5f, 6.5f);
                float g = UnityEngine.Random.Range(0.28f, 0.42f);
                p.startColor = new Color(g + 0.06f, g + 0.03f, g, 0.75f);
                _dust.Emit(p, 1);
            }
        }

        static void Sound(Vector3 at)
        {
            // A carpet is a roll of thunder, not 48 separate bangs: at most one
            // voice every 0.12 s, six voices in turn.
            if (Time.time - _lastSound < 0.12f) return;
            _lastSound = Time.time;
            try
            {
                if (_clip == null) _clip = Mortar.Sound.ThumpClip();
                if (_clip == null) return;
                AudioSource s = _audio[_nextAudio];
                if (s == null)
                {
                    GameObject go = new GameObject("NDR air strike voice " + _nextAudio);
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s = go.AddComponent<AudioSource>();
                    s.clip = _clip;
                    s.loop = false;
                    s.playOnAwake = false;
                    s.spatialBlend = 1f;
                    s.rolloffMode = AudioRolloffMode.Logarithmic;
                    s.dopplerLevel = 0f;
                    s.minDistance = 90f;
                    s.maxDistance = 3500f;
                    _audio[_nextAudio] = s;
                }
                _nextAudio = (_nextAudio + 1) % _audio.Length;
                s.transform.position = at;
                s.pitch = UnityEngine.Random.Range(0.7f, 0.85f);
                s.Play();
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents burst sound: " + ex.Message); }
        }

        static bool Make()
        {
            if (_tried) return _flash != null && _dust != null;
            _tried = true;
            try
            {
                Material add = FireEffect.SharedAdditive(), blend = FireEffect.SharedBlended();
                if (add == null || blend == null) return false;
                GameObject root = new GameObject("NDR air strike bursts");
                UnityEngine.Object.DontDestroyOnLoad(root);
                _flash = System(root, "flash", add, 400, -0.1f);
                _dust = System(root, "dust", blend, 700, -0.04f);
                ParticleSystem.SizeOverLifetimeModule size = _dust.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 1.6f));
                ParticleSystem.ColorOverLifetimeModule col = _dust.colorOverLifetime;
                col.enabled = true;
                Gradient g = new Gradient();
                g.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.5f, 0.4f), new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(g);
                ParticleSystem.ColorOverLifetimeModule fcol = _flash.colorOverLifetime;
                fcol.enabled = true;
                Gradient fg = new Gradient();
                fg.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.45f, 0.1f), 1f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
                fcol.color = new ParticleSystem.MinMaxGradient(fg);
            }
            catch (Exception ex)
            {
                _flash = null;
                RevivalPlugin.L.LogWarning("AirEvents burst fx: " + ex.Message);
            }
            return _flash != null && _dust != null;
        }

        static ParticleSystem System(GameObject root, string name, Material mat, int max, float gravity)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.gravityModifier = new ParticleSystem.MinMaxCurve(gravity);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            if (r != null)
            {
                r.sharedMaterial = mat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            ps.Play();
            Fx.Apply(ps);
            return ps;
        }
    }

    // =====================================================================
    // The paratrooper under his canopy (local, no scripts)

    internal static class Canopy
    {
        static readonly List<KeyValuePair<Mesh, Material[]>> _parts = new List<KeyValuePair<Mesh, Material[]>>();
        static readonly List<Matrix4x4> _local = new List<Matrix4x4>();
        static float _scale = 1f;
        static Vector3 _offset;
        static bool _tried;
        static Material _cloth, _man;
        static Mesh _dome;

        internal const string Prefab = "PlayerDataPrefabs/Other/Parachute_Pref";

        /// <summary>One man under a canopy, the man's feet at the root.</summary>
        internal static GameObject Make()
        {
            Prepare();
            float K = PlayerAn2.K;
            GameObject root = new GameObject("NDR_Paratrooper");
            // The man: an olive capsule, 1.8 m.
            GameObject man = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Collider col = man.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            man.transform.SetParent(root.transform, false);
            man.transform.localScale = new Vector3(0.55f * K, 0.9f * K, 0.4f * K);
            man.transform.localPosition = new Vector3(0f, 0.9f * K, 0f);
            Renderer mr = man.GetComponent<Renderer>();
            if (mr != null) mr.sharedMaterial = _man;
            // The canopy, 7 m across, 6 m over his head.
            GameObject top = new GameObject("canopy");
            top.transform.SetParent(root.transform, false);
            top.transform.localPosition = new Vector3(0f, 7.5f * K, 0f);
            if (_parts.Count > 0)
            {
                GameObject holder = new GameObject("game canopy");
                holder.transform.SetParent(top.transform, false);
                holder.transform.localScale = Vector3.one * _scale;
                holder.transform.localPosition = -_offset * _scale;
                for (int i = 0; i < _parts.Count; i++)
                {
                    GameObject p = new GameObject("part" + i);
                    p.transform.SetParent(holder.transform, false);
                    Matrix4x4 m = _local[i];
                    p.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero);
                    p.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                    p.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                    p.AddComponent<MeshFilter>().sharedMesh = _parts[i].Key;
                    p.AddComponent<MeshRenderer>().sharedMaterials = _parts[i].Value;
                }
            }
            else if (_dome != null)
            {
                GameObject d = new GameObject("dome");
                d.transform.SetParent(top.transform, false);
                d.transform.localScale = new Vector3(3.5f * K, 1.8f * K, 3.5f * K);
                d.AddComponent<MeshFilter>().sharedMesh = _dome;
                d.AddComponent<MeshRenderer>().sharedMaterial = _cloth;
            }
            // Four rigging lines from the shoulders to the rim.
            for (int i = 0; i < 4; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 0.5f;
                GameObject l = new GameObject("line" + i);
                l.transform.SetParent(root.transform, false);
                LineRenderer lr = l.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.positionCount = 2;
                lr.SetPosition(0, new Vector3(0f, 1.6f * K, 0f));
                lr.SetPosition(1, new Vector3(Mathf.Cos(a) * 3.3f * K, 7.4f * K, Mathf.Sin(a) * 3.3f * K));
                lr.startWidth = lr.endWidth = 0.05f * K;
                lr.sharedMaterial = _man;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return root;
        }

        static void Prepare()
        {
            if (_tried) return;
            _tried = true;
            Shader sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
            _man = new Material(sh); _man.name = "NDR_Paratrooper"; _man.color = new Color(0.30f, 0.33f, 0.22f);
            _cloth = new Material(sh); _cloth.name = "NDR_Canopy"; _cloth.color = new Color(0.55f, 0.58f, 0.44f);
            _dome = Dome();
            // The game's own canopy: its meshes and materials only, never an
            // instance (its scripts belong to a player's parachute state).
            try
            {
                GameObject prefab = Resources.Load(Prefab) as GameObject;
                if (prefab == null) { RevivalPlugin.L.LogInfo("AirEvents: " + Prefab + " not loadable - a built canopy."); return; }
                Matrix4x4 inv = prefab.transform.worldToLocalMatrix;
                Bounds b = new Bounds();
                bool any = false;
                foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    MeshRenderer r = mf.GetComponent<MeshRenderer>();
                    if (mf.sharedMesh == null || r == null) continue;
                    Add(mf.sharedMesh, r.sharedMaterials, inv * mf.transform.localToWorldMatrix, ref b, ref any);
                }
                foreach (SkinnedMeshRenderer sr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (sr.sharedMesh == null) continue;
                    Add(sr.sharedMesh, sr.sharedMaterials, inv * sr.transform.localToWorldMatrix, ref b, ref any);
                }
                if (!any) { RevivalPlugin.L.LogInfo("AirEvents: " + Prefab + " has no meshes - a built canopy."); return; }
                float width = Mathf.Max(b.size.x, b.size.z);
                _scale = width > 1e-3f ? 7f * PlayerAn2.K / width : 1f;
                _offset = new Vector3(b.center.x, b.min.y + b.size.y * 0.3f, b.center.z);
                RevivalPlugin.L.LogInfo("AirEvents: the game's canopy, " + _parts.Count + " part(s), "
                    + width.ToString("0.00") + " u wide, scaled " + _scale.ToString("0.00") + ".");
            }
            catch (Exception ex)
            {
                _parts.Clear(); _local.Clear();
                RevivalPlugin.L.LogWarning("AirEvents canopy: " + ex.Message + " - a built canopy.");
            }
        }

        static void Add(Mesh mesh, Material[] mats, Matrix4x4 m, ref Bounds b, ref bool any)
        {
            _parts.Add(new KeyValuePair<Mesh, Material[]>(mesh, mats));
            _local.Add(m);
            Bounds mb = mesh.bounds;
            Vector3 c = m.MultiplyPoint3x4(mb.center);
            Vector3 e = new Vector3(
                Mathf.Abs(m.m00) * mb.extents.x + Mathf.Abs(m.m01) * mb.extents.y + Mathf.Abs(m.m02) * mb.extents.z,
                Mathf.Abs(m.m10) * mb.extents.x + Mathf.Abs(m.m11) * mb.extents.y + Mathf.Abs(m.m12) * mb.extents.z,
                Mathf.Abs(m.m20) * mb.extents.x + Mathf.Abs(m.m21) * mb.extents.y + Mathf.Abs(m.m22) * mb.extents.z);
            Bounds wb = new Bounds(c, e * 2f);
            if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
        }

        /// <summary>A unit half-dome (radius 1, height 1), double-sided.</summary>
        static Mesh Dome()
        {
            const int seg = 16, rings = 5;
            List<Vector3> v = new List<Vector3>();
            List<Vector3> n = new List<Vector3>();
            List<int> t = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = (r / (float)rings) * Mathf.PI * 0.5f;
                for (int s = 0; s <= seg; s++)
                {
                    float th = s / (float)seg * Mathf.PI * 2f;
                    Vector3 p = new Vector3(Mathf.Cos(th) * Mathf.Cos(phi), Mathf.Sin(phi), Mathf.Sin(th) * Mathf.Cos(phi));
                    v.Add(p); n.Add(p);
                }
            }
            int row = seg + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < seg; s++)
                {
                    int a = r * row + s, b2 = a + 1, c = a + row, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(b2); t.Add(b2); t.Add(c); t.Add(d);
                    t.Add(a); t.Add(b2); t.Add(c); t.Add(b2); t.Add(d); t.Add(c);
                }
            Mesh m = new Mesh();
            m.name = "NDR_CanopyDome";
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    // =====================================================================
    // The Tu-95 model (tu95_import.py)

    internal static class Tu95Model
    {
        static bool _loaded, _ok;
        internal static Mesh Body, Glass, Decals, Prop, BayL, BayR, WreckMesh;
        internal static Material Skin, Clear, Cutout;
        internal static readonly List<KeyValuePair<string, Vector3>> Props = new List<KeyValuePair<string, Vector3>>();
        internal static Vector3 BayLAt, BayRAt;
        internal static readonly List<Bounds> Boxes = new List<Bounds>();

        internal static bool Load()
        {
            if (_loaded) return _ok;
            _loaded = true;
            try
            {
                ReadRig(Path.Combine(RevivalPlugin.AssetDir, "tu95_rig.txt"));
                Body = Assets.Load("tu95_body.ndmesh");
                Glass = Assets.Load("tu95_glass.ndmesh");
                Decals = Assets.Load("tu95_decals.ndmesh");
                Prop = Assets.Load("tu95_prop.ndmesh");
                BayL = Assets.Load("tu95_bay_l.ndmesh");
                BayR = Assets.Load("tu95_bay_r.ndmesh");
                WreckMesh = Assets.Load("tu95_wreck.ndmesh");
                Texture2D tex = Assets.Texture("tu95_atlas.png", false, true);
                if (Body == null || Prop == null || WreckMesh == null || tex == null)
                    throw new InvalidOperationException("Tu-95 assets missing; repair the client package (python tu95_import.py)");
                tex.anisoLevel = 4;
                tex.filterMode = FilterMode.Bilinear;
                Shader st = Shader.Find("Standard");
                Shader sh = st != null ? st : Shader.Find("Legacy Shaders/Diffuse");
                Skin = new Material(sh);
                Skin.name = "NDR_Tu95_Skin";
                Skin.mainTexture = tex;
                if (Skin.HasProperty("_Glossiness")) Skin.SetFloat("_Glossiness", 0.35f);
                if (Skin.HasProperty("_Metallic")) Skin.SetFloat("_Metallic", 0.25f);
                Cutout = new Material(sh);
                Cutout.name = "NDR_Tu95_Decals";
                Cutout.mainTexture = tex;
                if (st != null)
                {
                    Cutout.SetFloat("_Mode", 1f);
                    Cutout.SetOverrideTag("RenderType", "TransparentCutout");
                    Cutout.SetFloat("_Cutoff", 0.5f);
                    Cutout.EnableKeyword("_ALPHATEST_ON");
                    Cutout.renderQueue = 2450;
                }
                Clear = new Material(st != null ? st : Shader.Find("Legacy Shaders/Transparent/Diffuse"));
                Clear.name = "NDR_Tu95_Glass";
                Clear.color = new Color(0.20f, 0.26f, 0.30f, 0.45f);
                if (st != null)
                {
                    Clear.SetFloat("_Mode", 2f);
                    Clear.SetOverrideTag("RenderType", "Transparent");
                    Clear.SetInt("_SrcBlend", 5);
                    Clear.SetInt("_DstBlend", 10);
                    Clear.SetInt("_ZWrite", 0);
                    Clear.DisableKeyword("_ALPHATEST_ON");
                    Clear.EnableKeyword("_ALPHABLEND_ON");
                    Clear.renderQueue = 3000;
                }
                _ok = true;
                RevivalPlugin.L.LogInfo("AirEvents: Tu-95 model loaded - " + Body.vertexCount + " body vertices, "
                    + Props.Count + " props, " + Boxes.Count + " hull boxes.");
            }
            catch (Exception ex)
            {
                _ok = false;
                RevivalPlugin.L.LogError("AirEvents Tu-95 model: " + ex.Message);
            }
            return _ok;
        }

        static void ReadRig(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("missing: " + path);
            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "prop":
                        Props.Add(new KeyValuePair<string, Vector3>(p[2], new Vector3(
                            float.Parse(p[3], c), float.Parse(p[4], c), float.Parse(p[5], c))));
                        break;
                    case "bay":
                        Vector3 v = new Vector3(float.Parse(p[2], c), float.Parse(p[3], c), float.Parse(p[4], c));
                        if (p[1] == "L") BayLAt = v; else BayRAt = v;
                        break;
                    case "box":
                        Boxes.Add(new Bounds(
                            new Vector3(float.Parse(p[2], c), float.Parse(p[3], c), float.Parse(p[4], c)),
                            new Vector3(float.Parse(p[5], c), float.Parse(p[6], c), float.Parse(p[7], c))));
                        break;
                }
            }
        }
    }

    /// <summary>The Tu-95 on its carrier: props, bay, drone, hull, wreck.</summary>
    public sealed class Tu95Visual : MonoBehaviour
    {
        Transform _fly, _wreck, _bayL, _bayR, _an2;
        AudioSource _an2Audio;
        readonly List<Transform> _props = new List<Transform>();
        readonly List<float> _spin = new List<float>();
        readonly List<GameObject> _hull = new List<GameObject>();
        AudioSource _audio;
        float _bayUntil, _bay;
        bool _dead;
        static AudioClip _clip;

        internal void Build(Transform an2)
        {
            _an2 = an2;
            float K = PlayerAn2.K;
            GameObject fly = new GameObject("NDR_Tu95Fly");
            _fly = fly.transform;
            _fly.SetParent(transform, false);
            _fly.localScale = Vector3.one * K;
            Part(_fly, "Body", Tu95Model.Body, Tu95Model.Skin, Vector3.zero);
            if (Tu95Model.Glass != null) Part(_fly, "Glass", Tu95Model.Glass, Tu95Model.Clear, Vector3.zero);
            if (Tu95Model.Decals != null) Part(_fly, "Decals", Tu95Model.Decals, Tu95Model.Cutout, Vector3.zero);
            for (int i = 0; i < Tu95Model.Props.Count; i++)
            {
                KeyValuePair<string, Vector3> p = Tu95Model.Props[i];
                _props.Add(Part(_fly, "Prop" + i, Tu95Model.Prop, Tu95Model.Skin, p.Value));
                // Front and rear discs of an NK-12 turn opposite ways.
                _spin.Add(p.Key == "Front" ? -1f : 1f);
            }
            if (Tu95Model.BayL != null) _bayL = Part(_fly, "BayL", Tu95Model.BayL, Tu95Model.Skin, Tu95Model.BayLAt);
            if (Tu95Model.BayR != null) _bayR = Part(_fly, "BayR", Tu95Model.BayR, Tu95Model.Skin, Tu95Model.BayRAt);
            // Hull boxes: rifle rays find the carrier through them (NpcAircraft.Owner).
            for (int i = 0; i < Tu95Model.Boxes.Count; i++)
            {
                Bounds b = Tu95Model.Boxes[i];
                GameObject h = new GameObject("NDR_Tu95Hull" + i);
                h.transform.SetParent(transform.parent, false);
                BoxCollider box = h.AddComponent<BoxCollider>();
                box.center = b.center * K;
                box.size = b.size * K;
                _hull.Add(h);
            }
            try
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.clip = DroneClip();
                _audio.loop = true;
                _audio.spatialBlend = 1f;
                _audio.dopplerLevel = 0.4f;
                _audio.rolloffMode = AudioRolloffMode.Logarithmic;
                _audio.minDistance = 220f;
                _audio.maxDistance = 9000f;
                _audio.volume = 1f;
                _audio.Play();
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("AirEvents Tu-95 drone: " + ex.Message); }
        }

        static Transform Part(Transform parent, string name, Mesh mesh, Material mat, Vector3 at)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            return go.transform;
        }

        internal void OpenBay(float seconds)
        {
            _bayUntil = Mathf.Max(_bayUntil, Time.time + seconds);
        }

        internal void Wreck()
        {
            if (_dead) return;
            _dead = true;
            if (_audio != null) _audio.Stop();
            for (int i = 0; i < _hull.Count; i++) if (_hull[i] != null) Destroy(_hull[i]);
            _hull.Clear();
            if (_fly != null) _fly.gameObject.SetActive(false);
            if (Tu95Model.WreckMesh == null) return;
            GameObject w = new GameObject("NDR_Tu95Wreck");
            _wreck = w.transform;
            _wreck.SetParent(transform, false);
            _wreck.localScale = Vector3.one * PlayerAn2.K;
            // Level on the ground, whatever the carrier's crash attitude.
            Vector3 f = transform.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float y;
            Vector3 p = transform.position;
            if (RevivalTroopInsertion.GroundY(p, out y)) p.y = y;
            _wreck.position = p;
            _wreck.rotation = Quaternion.Euler(0f, yaw, 0f);
            Part(_wreck, "Wreck", Tu95Model.WreckMesh, Tu95Model.Skin, Vector3.zero);
        }

        void Update()
        {
            // The An-2's radial is added once it runs: silence it for good.
            if (_an2Audio == null && _an2 != null) _an2Audio = _an2.GetComponent<AudioSource>();
            if (_an2Audio != null && !_an2Audio.mute) _an2Audio.mute = true;
            if (_dead) return;
            float dt = Time.deltaTime;
            // Readable blades: ~4 rev/s, not the strobing 12 of the real NK-12.
            for (int i = 0; i < _props.Count; i++)
                if (_props[i] != null) _props[i].Rotate(0f, 0f, _spin[i] * 1440f * dt, Space.Self);
            _bay = Mathf.MoveTowards(_bay, Time.time < _bayUntil ? 1f : 0f, dt * 0.7f);
            if (_bayL != null) _bayL.localRotation = Quaternion.AngleAxis(-90f * _bay, Vector3.forward);
            if (_bayR != null) _bayR.localRotation = Quaternion.AngleAxis(90f * _bay, Vector3.forward);
        }

        void OnDestroy()
        {
            for (int i = 0; i < _hull.Count; i++) if (_hull[i] != null) Destroy(_hull[i]);
        }

        /// <summary>Four NK-12s with contra-rotating props: a deep throb with a
        /// slow beat, the turbines' whine faint over it. Four seconds, looping
        /// cleanly (whole cycles of every partial).</summary>
        internal static AudioClip DroneClip()
        {
            if (_clip != null) return _clip;
            const int rate = 22050;
            const float seconds = 4f;
            int n = Mathf.RoundToInt(rate * seconds);
            float[] data = new float[n];
            System.Random rnd = new System.Random(95);
            float noise = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float w = 2f * Mathf.PI * t;
                float s = 0.42f * Mathf.Sin(w * 55f) + 0.22f * Mathf.Sin(w * 56.25f) + 0.18f * Mathf.Sin(w * 110f)
                    + 0.10f * Mathf.Sin(w * 165.5f) + 0.035f * Mathf.Sin(w * 412.5f);
                float beat = 0.75f + 0.25f * Mathf.Sin(w * 1.25f);
                noise = noise * 0.97f + (float)(rnd.NextDouble() * 2.0 - 1.0) * 0.03f;
                data[i] = Mathf.Clamp((s * beat + noise * 1.5f) * 0.8f, -1f, 1f);
            }
            _clip = AudioClip.Create("NDR Tu-95 drone", n, 1, rate, false);
            _clip.SetData(data, 0);
            return _clip;
        }
    }
}

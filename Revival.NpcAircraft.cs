// Next Day: Survival - Revival Toolkit
//
// NPC AIRCRAFT ON A FLIGHT PATH, AND THE ADMIN'S TEST FLYOVER (task N3).
// docs/ai/tasks/admin-flyover.md has the design and the in-game checklist.
//
// THREE PIECES, THE FIRST TWO REUSABLE (the editor air events, N11, fly
// bombers and paratroop transports through them):
//   FlightPath   a straight leg edge to edge at a height above the ground. The
//                height profile is the upper envelope of the terrain plus the
//                AGL, slope-limited (MaxSlope) so the aeroplane climbs before a
//                ridge instead of into it. AcrossMap lays such a leg through a
//                point from a bearing to the map's edges.
//   NpcAircraft  an An-2 flown by the MASTER along a FlightPath. The aeroplane
//                is the player An-2's own network carrier (PlayerAn2.BuildNpc):
//                the same model, the same PhotonView sync, the same GepardAir
//                registration - so the Gepard (player and NPC crew), the ZU-23,
//                the tower radar, the no-fly zones and the Stinger see it with no
//                code of their own - and the same crash: ShotDown -> Abandon ->
//                An2Glide -> FinishGlide -> Burn, on every client. The path is in
//                the instantiation data (Tag at index 2), so every client and a
//                late joiner knows it is an NPC flight, and a new master takes
//                the flight over where the aeroplane is. NPC aeroplanes are
//                HOSTILE to every faction (NpcAircraft.Hostile: the Gepard crew's
//                Feindlich, FlakFire.Allegiance, NoFly.Aboard) and take small-arms
//                hits (FireOneShot) and blasts (ExplosionObject.Explode).
//   Flyover      the admin panel's "Test flyover": one An-2 from the map edge
//                over the admin (or a map point: right-click on the map) and out
//                the other side, then gone. Altitude, speed and the edge it comes
//                from are panel options. A non-master admin asks the master.
//
// Photon event [NpcAircraft] NetworkEventCode (159), float[] with the kind first:
//   0 request  { 0, x, y, z, altitude m, speed km/h, bearing deg[, tu95] } -> master
//   1 hit      { 1, view, x, y, z, lethal }                         -> master
//   2 clear    { 2 }                                                -> master
//
// C# 3.0 (csc from .NET 3.5): no optional arguments, no expression-tree
// lambdas. ASCII only, no BOM.
//
// SEAMS OUTSIDE THIS FILE:
//   Revival.PlayerAn2.cs   BuildNpc, RemoveNpc, Down, EnsureOn; Prepare ->
//                          Prepared; ShotDownHere flies an NPC aeroplane down at
//                          once; Alive/Nearest leave NPC aeroplanes out.
//   RevivalGepardCrew.cs   Feindlich -> Hostile; GepardAir.Kill (one-hit kill).
//   Revival.Flak.cs        FlakFire.Allegiance -> Hostile.
//   Revival.NoFly.cs       Aboard -> Hostile (an NPC intruder violates a zone).
//   Revival.Stinger.cs     Scan adds GepardAir aircraft (kind 6), Kill.
//   Revival.Admin.cs       the panel rows and the map right-click button.
//   RevivalPlugin.cs       BindConfig / Install / Update.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NextDayRevival
{
    // =====================================================================
    // The path

    /// <summary>
    /// A straight leg in world units at a height above the ground: position,
    /// attitude and velocity by the distance flown.
    /// </summary>
    internal sealed class FlightPath
    {
        /// <summary>Climb or descent the profile allows, rise per run (about 8 deg).</summary>
        internal const float MaxSlope = 0.14f;
        const float Step = 40f;             // world units between terrain samples

        public Vector2 From, To;            // x, z
        public float Agl;                   // world units above the ground
        public float Speed;                 // world units per second
        public float Length;
        public Vector2 Dir;
        float[] _y;
        bool[] _profileKnown;
        int _profileIndex;
        int _profileLead;                   // off-map approach samples, never sampled
        float _profileHighest;
        bool _profileAny;
        internal bool ProfileReady { get { return _profileKnown == null; } }

        // Paradrop warning preparation samples only a few terrain positions per
        // frame. The finished profile is identical to synchronous Profile below.
        internal static FlightPath BeginStraight(Vector2 from, Vector2 to, float agl, float speed)
        {
            FlightPath p = new FlightPath();
            p.From = from; p.To = to;
            p.Agl = Mathf.Max(10f, agl); p.Speed = Mathf.Max(5f, speed);
            Vector2 d = to - from;
            p.Length = d.magnitude;
            p.Dir = p.Length > 0.01f ? d / p.Length : new Vector2(0f, 1f);
            int n = Mathf.Max(2, Mathf.CeilToInt(p.Length / Step) + 1);
            p._y = new float[n]; p._profileKnown = new bool[n];
            return p;
        }

        internal bool ProfileStep()
        {
            if (_profileKnown == null) return true;
            int end = Mathf.Min(_y.Length, _profileIndex + 4);
            for (; _profileIndex < end; _profileIndex++)
            {
                Vector2 xz = From + Dir * Mathf.Min(Length, _profileIndex * Step);
                float y;
                if (!Ground(new Vector3(xz.x, 0f, xz.y), out y)) continue;
                _y[_profileIndex] = y; _profileKnown[_profileIndex] = true;
                if (!_profileAny || y > _profileHighest) _profileHighest = y;
                _profileAny = true;
            }
            if (_profileIndex < _y.Length) return false;
            for (int i = _profileLead; i < _y.Length; i++)
                _y[i] = (_profileKnown[i] ? _y[i] : _profileHighest) + Agl;
            float drop = MaxSlope * Step;
            for (int i = _profileLead + 1; i < _y.Length; i++) _y[i] = Mathf.Max(_y[i], _y[i - 1] - drop);
            for (int i = _y.Length - 2; i >= _profileLead; i--) _y[i] = Mathf.Max(_y[i], _y[i + 1] - drop);
            // The extended approach repeats the smoothed edge height, exactly
            // as ExtendApproach does on a finished profile.
            for (int i = 0; i < _profileLead; i++) _y[i] = _y[_profileLead];
            _profileKnown = null;
            return true;
        }

        /// <summary>W AA3: extend only the inbound off-map leg. Reuse the
        /// already sampled edge height; no new terrain or physics calls.
        /// Whole sample steps keep the old on-map height profile identical.</summary>
        internal void ExtendApproach(float targetAlong, float minimum)
        {
            int extra = Mathf.Max(0, Mathf.CeilToInt((minimum - targetAlong) / Step));
            if (extra == 0 || _y == null || _y.Length == 0) return;
            float run = extra * Step;
            float[] heights = new float[_y.Length + extra];
            for (int i = 0; i < extra; i++) heights[i] = _y[0];
            Array.Copy(_y, 0, heights, extra, _y.Length);
            _y = heights;
            if (_profileKnown != null)
            {
                // A6.67: BeginStraight's profile is still being sampled (the
                // paradrop transports). Its sample flags and cursor move with
                // the heights; growing only _y sent ProfileStep past the end of
                // _profileKnown and cancelled every prepared transport with
                // "Index was outside the bounds of the array".
                bool[] known = new bool[_y.Length];
                Array.Copy(_profileKnown, 0, known, extra, _profileKnown.Length);
                _profileKnown = known;
                _profileIndex += extra;
                _profileLead += extra;
            }
            From -= Dir * run;
            Length += run;
        }

        internal static FlightPath Straight(Vector2 from, Vector2 to, float agl, float speed)
        {
            FlightPath p = new FlightPath();
            p.From = from;
            p.To = to;
            p.Agl = Mathf.Max(10f, agl);
            p.Speed = Mathf.Max(5f, speed);
            Vector2 d = to - from;
            p.Length = d.magnitude;
            p.Dir = p.Length > 0.01f ? d / p.Length : new Vector2(0f, 1f);
            p.Profile();
            return p;
        }

        /// <summary>max over the samples j of ground_j + Agl - MaxSlope |s - s_j|:
        /// never below Agl over any sample, never steeper than MaxSlope.</summary>
        void Profile()
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(Length / Step) + 1);
            _y = new float[n];
            bool[] known = new bool[n];
            float fallback = 0f;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                Vector2 xz = From + Dir * Mathf.Min(Length, i * Step);
                float y;
                if (Ground(new Vector3(xz.x, 0f, xz.y), out y))
                {
                    _y[i] = y;
                    known[i] = true;
                    if (!any || y > fallback) fallback = y;
                    any = true;
                }
            }
            // No height (terrain not loaded, off the map): the highest known one.
            for (int i = 0; i < n; i++) if (!known[i]) _y[i] = fallback;
            for (int i = 0; i < n; i++) _y[i] += Agl;
            float drop = MaxSlope * Step;
            for (int i = 1; i < n; i++) _y[i] = Mathf.Max(_y[i], _y[i - 1] - drop);
            for (int i = n - 2; i >= 0; i--) _y[i] = Mathf.Max(_y[i], _y[i + 1] - drop);
        }

        static bool Ground(Vector3 at, out float y)
        {
            if (RevivalTroopInsertion.TerrainHeight(at, out y)) return true;
            return RevivalTroopInsertion.GroundY(at, out y);
        }

        float Height(float s)
        {
            float f = Mathf.Clamp(s, 0f, Length) / Step;
            int i = Mathf.Min(_y.Length - 2, Mathf.FloorToInt(f));
            return Mathf.Lerp(_y[i], _y[i + 1], Mathf.Clamp01(f - i));
        }

        internal Vector3 At(float s)
        {
            Vector2 xz = From + Dir * Mathf.Clamp(s, 0f, Length);
            return new Vector3(xz.x, Height(s), xz.y);
        }

        /// <summary>Rise per run over the next stretch (a little look-ahead so
        /// the nose moves before the kink, not on it).</summary>
        internal float Slope(float s)
        {
            float a = Height(s - Step), b = Height(s + Step);
            return (b - a) / (2f * Step);
        }

        internal Quaternion Attitude(float s)
        {
            float heading = Mathf.Atan2(Dir.x, Dir.y) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan(Slope(s)) * Mathf.Rad2Deg;
            // A couple of degrees of angle of attack: the An-2 flies nose-up.
            return Quaternion.Euler(-(pitch + 2f), heading, 0f);
        }

        /// <summary>Metres per second, world axes (An2Glide's unit).</summary>
        internal Vector3 Velocity(float s)
        {
            float slope = Slope(s);
            Vector3 d = new Vector3(Dir.x, slope, Dir.y).normalized;
            return d * (Speed / PlayerAn2.K);
        }

        internal float Project(Vector3 p)
        {
            return Mathf.Clamp(Vector2.Dot(new Vector2(p.x, p.z) - From, Dir), 0f, Length);
        }

        internal float Seconds { get { return Length / Speed; } }

        /// <summary>The map this client is on, world units: the vanilla
        /// terrain (-2500..2500), plus the east tile with [World] EastTile;
        /// another scene a 6 km square around the point.</summary>
        internal static Rect MapRect(Vector3 around)
        {
            if (MapScene.AtHome)
                return EastWorld.On ? EastWorld.Extended : new Rect(-2500f, -2500f, 5000f, 5000f);
            return new Rect(around.x - 3000f, around.z - 3000f, 6000f, 6000f);
        }

        /// <summary>A leg through <paramref name="over"/> that comes in from
        /// <paramref name="bearing"/> (degrees, 0 = from +z / north, 90 = from
        /// +x / east) at the map's edge and leaves at the opposite edge. At
        /// least MinRun either side, so the defence has time to see it.</summary>
        internal static void AcrossMap(Vector3 over, float bearing, out Vector2 from, out Vector2 to)
        {
            const float MinRun = 900f, MaxRun = 9000f;
            Rect r = MapRect(over);
            Vector2 o = new Vector2(Mathf.Clamp(over.x, r.xMin, r.xMax), Mathf.Clamp(over.z, r.yMin, r.yMax));
            float b = bearing * Mathf.Deg2Rad;
            Vector2 source = new Vector2(Mathf.Sin(b), Mathf.Cos(b));      // toward the edge it comes from
            float back = Mathf.Clamp(Exit(o, source, r), MinRun, MaxRun);
            float ahead = Mathf.Clamp(Exit(o, -source, r), MinRun, MaxRun);
            Vector2 at = new Vector2(over.x, over.z);
            from = at + source * back;
            to = at - source * ahead;
        }

        /// <summary>Distance from <paramref name="o"/> inside the rect along
        /// <paramref name="d"/> to its edge.</summary>
        static float Exit(Vector2 o, Vector2 d, Rect r)
        {
            float t = float.MaxValue;
            if (d.x > 0.0001f) t = Mathf.Min(t, (r.xMax - o.x) / d.x);
            else if (d.x < -0.0001f) t = Mathf.Min(t, (r.xMin - o.x) / d.x);
            if (d.y > 0.0001f) t = Mathf.Min(t, (r.yMax - o.y) / d.y);
            else if (d.y < -0.0001f) t = Mathf.Min(t, (r.yMin - o.y) / d.y);
            return t == float.MaxValue ? 0f : Mathf.Max(0f, t);
        }
    }

    // =====================================================================
    // The aeroplanes

    /// <summary>
    /// NPC An-2s flown by the master along a FlightPath. Every client knows
    /// them (from the instantiation data); only the master moves them.
    /// </summary>
    internal static class NpcAircraft
    {
        /// <summary>Instantiation data index 2: this carrier is an NPC flight.</summary>
        internal const string Tag = "ndr-npc-air-1";

        /// <summary>Rifle and MG rounds that bring one down (the Gepard's
        /// 35 mm counts through GepardAir: [Gepard] HeliHits).</summary>
        internal const int SmallArmsHits = 30;
        /// <summary>World units beyond a blast's own radius that still count as
        /// a hit on the airframe (half the span).</summary>
        const float BlastSlack = 9f * PlayerAn2.K;

        internal static ConfigEntry<int> CfgEventCode;
        internal static ConfigEntry<float> CfgSpeedFactor;

        // Host-only planning policy. Network paths already contain effective speed.
        internal static float SpeedFactor(float eventFactor)
        {
            float value = eventFactor > 0f ? eventFactor
                : CfgSpeedFactor == null ? 0.6f : CfgSpeedFactor.Value;
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0.6f;
            return Mathf.Clamp(value, 0.2f, 1.5f);
        }

        internal static float Speed(float nominal, float factor)
        {
            return Mathf.Max(5f, nominal * factor);
        }

        internal sealed class Flight
        {
            public GameObject Go;
            public FlightPath Path;
            public bool Hostile;
            public string Label;
            public float S;
            public bool Driving;
            public float Born;
            public int Hits;
            /// <summary>Multiplier on SmallArmsHits (N11: a Tu-95 is 3).</summary>
            public int Toughness = 1;
            /// <summary>W AA4: hit points and who took them (master), and the
            /// damage level every client shows as smoke (0..2).</summary>
            public readonly DamageLedger Ledger = new DamageLedger();
            public int Level;
            /// <summary>Master, every frame of the flight: the aeroplane and the
            /// distance flown (world units). N11: the drop point of a bomber or
            /// a transport.</summary>
            public Action<GameObject, float> OnTick;
            /// <summary>Master: the aeroplane reached the end of its path.</summary>
            public Action<GameObject> OnEnd;
        }

        static readonly Dictionary<GameObject, Flight> _flights = new Dictionary<GameObject, Flight>();
        static readonly List<GameObject> _drop = new List<GameObject>();
        static readonly List<Flight> _tmp = new List<Flight>();
        static int _errors;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgSpeedFactor = cfg.Bind("NpcAircraft", "SpeedFactor", 0.6f,
                "NPC aircraft speed multiplier (0.2..1.5, 1 = original). Host plans bombers, "
                + "transports, flyovers, aid and troop Mi-8 flights; editor air events can override it. "
                + "Player An-2 and Mi-8 flight is unchanged.");
            CfgEventCode = cfg.Bind("NpcAircraft", "NetworkEventCode", 159,
                "Photon event code (0..199) of the NPC aircraft (admin test flyover): "
                + "flyover requests and hits to the master. Every client must use the same value.");
        }

        // ------------------------------------------------------------ queries

        internal static bool Is(GameObject go) { return go != null && _flights.ContainsKey(go); }

        /// <summary>An NPC aeroplane that every gun of every faction may fire on.</summary>
        internal static bool Hostile(GameObject go)
        {
            Flight f;
            return go != null && _flights.TryGetValue(go, out f) && f.Hostile;
        }

        /// <summary>Every client: an N11 bomber (label "tu95:...").</summary>
        internal static bool IsTu95(GameObject go)
        {
            Flight f;
            return go != null && _flights.TryGetValue(go, out f) && f.Label != null
                && f.Label.StartsWith("tu95:", StringComparison.Ordinal);
        }

        internal static bool Velocity(GameObject go, out Vector3 vel)
        {
            vel = Vector3.zero;
            Flight f;
            if (go == null || !_flights.TryGetValue(go, out f) || f.Path == null) return false;
            vel = f.Path.Velocity(f.Path.Project(go.transform.position));
            return true;
        }

        /// <summary>NPC aeroplanes still in the air.</summary>
        internal static int Airborne()
        {
            int n = 0;
            foreach (KeyValuePair<GameObject, Flight> e in _flights)
                if (e.Key != null && !PlayerAn2.Down(e.Key)) n++;
            return n;
        }

        internal static Flight Find(GameObject go)
        {
            Flight f;
            return go != null && _flights.TryGetValue(go, out f) ? f : null;
        }

        static bool _airRegistered;

        // Each client registers from the same Photon instantiation data, including
        // late joiners. Radar/AA discovery must not depend on the player An-2 probe.
        static void RegisterAir()
        {
            if (_airRegistered) return;
            GepardAir.Register("NPC aircraft", ListAir, PlayerAn2.ShotDown, PlayerAn2.Down, 0);
            _airRegistered = true;
        }

        static void ListAir(List<GameObject> into)
        {
            foreach (KeyValuePair<GameObject, Flight> e in _flights)
                if (e.Key != null) into.Add(e.Key);
        }

        // ------------------------------------------------------------ launch

        /// <summary>
        /// Master: an An-2 at the start of <paramref name="path"/>, flown to its
        /// end and removed there. <paramref name="hostile"/>: every AA gun of
        /// every faction engages it. Null when not the master or Photon refused.
        /// </summary>
        internal static Flight Launch(FlightPath path, bool hostile, string label,
            Action<GameObject, float> onTick, Action<GameObject> onEnd)
        {
            if (path == null || !RevivalTroopInsertion.MasterClient()) return null;
            PlayerAn2.EnsureOn();
            Net.EnsureHooked();
            object[] data = new object[] { PlayerAn2.Marker, PlayerAn2.Capacity, Tag,
                path.From.x, path.From.y, path.To.x, path.To.y, path.Agl, path.Speed,
                hostile ? 1f : 0f, label ?? "" };
            GameObject go = PlayerAn2.BuildNpc(path.At(0f), path.Attitude(0f), data);
            if (go == null) return null;
            Flight f = Find(go);
            if (f == null)
            {
                // Prepare ran without the tag (should not happen): register here.
                Prepared(go, data);
                f = Find(go);
                if (f == null) return null;
            }
            // The same for the model (B8a): the master's first Prepare can run
            // before Photon hands the view its data, and then no Tu-95 was built
            // on the master. Idempotent; every other client builds it in Start.
            AirEvents.Prepared(go, data);
            f.Path = path;          // the master's own profile, not a second one
            f.S = 0f;
            f.Driving = true;
            f.OnTick = onTick;
            f.OnEnd = onEnd;
            RevivalPlugin.L.LogInfo("NpcAircraft: " + (label ?? "flight") + " launched, "
                + Mathf.RoundToInt(path.Length / PlayerAn2.K) + " m at "
                + Mathf.RoundToInt(path.Agl / PlayerAn2.K) + " m AGL, "
                + Mathf.RoundToInt(path.Speed / PlayerAn2.K * 3.6f) + " km/h, "
                + Mathf.RoundToInt(path.Seconds) + " s" + (hostile ? ", hostile." : "."));
            return f;
        }

        /// <summary>Every client, from PlayerAn2.Prepare: an NPC flight's
        /// carrier, its path from the spawn data.</summary>
        internal static void Prepared(GameObject go, object[] data)
        {
            if (go == null || data == null || data.Length < 10 || !Tag.Equals(data[2] as string)) return;
            try
            {
                Vector2 from = new Vector2(Fl(data[3]), Fl(data[4]));
                Vector2 to = new Vector2(Fl(data[5]), Fl(data[6]));
                Flight f = new Flight();
                f.Go = go;
                f.Path = FlightPath.Straight(from, to, Fl(data[7]), Fl(data[8]));
                f.Hostile = Fl(data[9]) > 0.5f;
                f.Label = data.Length > 10 ? data[10] as string : null;
                f.Born = Time.time;
                _flights[go] = f;
                RegisterAir();
                An2Visual vis = go.GetComponent<An2Visual>();
                if (vis != null)
                {
                    vis.Running = true;
                    vis.Throttle = 0.85f;
                }
                RevivalPlugin.L.LogInfo("NpcAircraft: " + (string.IsNullOrEmpty(f.Label) ? "flight" : f.Label)
                    + " known here (" + Mathf.RoundToInt(f.Path.Seconds) + " s path).");
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogWarning("NpcAircraft prepare: " + ex.Message);
            }
        }

        static float Fl(object o)
        {
            if (o is float) return (float)o;
            if (o is double) return (float)(double)o;
            if (o is int) return (int)o;
            return 0f;
        }

        /// <summary>Master: every NPC aeroplane still flying, gone.</summary>
        internal static int ClearAll()
        {
            if (!RevivalTroopInsertion.MasterClient()) return 0;
            _tmp.Clear();
            foreach (KeyValuePair<GameObject, Flight> e in _flights)
                if (e.Key != null && !PlayerAn2.Down(e.Key)) _tmp.Add(e.Value);
            for (int i = 0; i < _tmp.Count; i++)
            {
                _flights.Remove(_tmp[i].Go);
                PlayerAn2.RemoveNpc(_tmp[i].Go);
            }
            int n = _tmp.Count;
            _tmp.Clear();
            return n;
        }

        // ------------------------------------------------------------ frame

        internal static void Tick()
        {
            Flyover.Tick();
            if (_flights.Count == 0) return;
            try
            {
                _drop.Clear();
                foreach (KeyValuePair<GameObject, Flight> e in _flights) if (e.Key == null) _drop.Add(e.Key);
                for (int i = 0; i < _drop.Count; i++) _flights.Remove(_drop[i]);
                _drop.Clear();

                bool master = RevivalTroopInsertion.MasterClient();
                float dt = Mathf.Min(Time.deltaTime, 0.1f);
                _tmp.Clear();
                foreach (KeyValuePair<GameObject, Flight> e in _flights) _tmp.Add(e.Value);
                for (int i = 0; i < _tmp.Count; i++)
                {
                    Flight f = _tmp[i];
                    if (PlayerAn2.Down(f.Go)) { f.Driving = false; continue; }
                    if (!master) { f.Driving = false; continue; }
                    Drive(f, dt);
                }
                _tmp.Clear();
                _errors = 0;
            }
            catch (Exception ex)
            {
                if (++_errors <= 3) RevivalPlugin.L.LogError("NpcAircraft: " + ex);
            }
        }

        static void Drive(Flight f, float dt)
        {
            Transform tr = f.Go.transform;
            if (!f.Driving)
            {
                // A new master: the flight goes on from where the aeroplane is.
                f.S = f.Path.Project(tr.position);
                f.Driving = true;
                RevivalPlugin.L.LogInfo("NpcAircraft: taking over a flight at "
                    + Mathf.RoundToInt(f.S / PlayerAn2.K) + " m.");
            }
            f.S += f.Path.Speed * dt;
            bool late = Time.time - f.Born > f.Path.Seconds + 120f;
            if (f.S >= f.Path.Length || late)
            {
                if (f.OnEnd != null)
                {
                    try { f.OnEnd(f.Go); }
                    catch (Exception ex) { RevivalPlugin.L.LogWarning("NpcAircraft end: " + ex.Message); }
                }
                _flights.Remove(f.Go);
                PlayerAn2.RemoveNpc(f.Go);
                return;
            }
            tr.position = f.Path.At(f.S);
            tr.rotation = Quaternion.Slerp(tr.rotation, f.Path.Attitude(f.S), Mathf.Clamp01(dt * 3f));
            if (f.OnTick != null)
            {
                try { f.OnTick(f.Go, f.S); }
                catch (Exception ex) { RevivalPlugin.L.LogWarning("NpcAircraft tick: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------ damage

        /// <summary>A hit on an NPC aeroplane by the local player's weapon.
        /// Lethal (a blast on the airframe) brings it down; a round takes
        /// 1 / (SmallArmsHits x toughness) of its hit points.</summary>
        internal static void Damage(GameObject go, Vector3 point, bool lethal)
        {
            Damage(go, point, lethal ? 1f : -1f, AirKills.Credit());
        }

        /// <summary>W AA4: <paramref name="amount"/> of the aeroplane's hit
        /// points (1 = all; below 0 = one rifle round), credited to Photon
        /// actor <paramref name="actor"/> (0 = an NPC crew). The master keeps
        /// the only ledger; any other client sends the hit there, and the
        /// master credits the sender.</summary>
        internal static void Damage(GameObject go, Vector3 point, float amount, int actor)
        {
            Flight f = Find(go);
            if (f == null || PlayerAn2.Down(go)) return;
            if (!RevivalTroopInsertion.MasterClient())
            {
                int view = PlayerAn2.View(go);
                if (view != 0) Net.Send(new float[] { Net.Hit, view, point.x, point.y, point.z,
                    amount >= 1f ? 1f : 0f, amount }, true);
                return;
            }
            Apply(f, point, amount, actor);
        }

        static void Apply(Flight f, Vector3 point, float amount, int actor)
        {
            float dmg = amount < 0f ? AirKillCore.RoundDamage(f.Toughness) : amount;
            f.Hits++;
            if (!f.Ledger.Add(dmg, actor))
            {
                AirKills.Damaged(f);
                return;
            }
            RevivalPlugin.L.LogInfo("NpcAircraft: " + (string.IsNullOrEmpty(f.Label) ? "flight" : f.Label)
                + " brought down (" + (amount >= 1f ? "blast" : f.Hits + " hits") + ").");
            AirKills.Downed(f, point);
            if (!GepardAir.KillNow(f.Go, point)) PlayerAn2.ShotDown(f.Go, point);
        }

        static FieldInfo _camera;
        static bool _cameraLooked;

        /// <summary>Postfix on PlayerFirearmWeaponController::FireOneShot: the
        /// local player's round along the camera ray, onto an NPC hull box.</summary>
        public static void ShotPostfix(object __instance)
        {
            try
            {
                if (_flights.Count == 0 || __instance == null) return;
                if (Stinger.IsStinger(__instance) || Drone.Flying) return;
                if (!Mine(__instance)) return;
                if (!_cameraLooked)
                {
                    _cameraLooked = true;
                    _camera = AccessTools.Field(__instance.GetType(), "MainCamera");
                }
                Transform cam = _camera == null ? null : _camera.GetValue(__instance) as Transform;
                if (cam == null) return;
                RaycastHit hit;
                if (!Physics.Raycast(cam.position + cam.forward * 0.5f, cam.forward, out hit,
                        1200f * PlayerAn2.K, ~0, QueryTriggerInteraction.Ignore)) return;
                GameObject go = Owner(hit.transform);
                if (go != null) Damage(go, hit.point, false);
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NpcAircraft shot: " + ex.Message); }
        }

        /// <summary>Postfix on ExplosionObject::Explode (RPG, LAW, grenade,
        /// rocket): a blast on the airframe brings it down.</summary>
        public static void ExplodePostfix(object __instance)
        {
            try
            {
                if (_flights.Count == 0) return;
                Component c = __instance as Component;
                if (c == null || !Mine(c)) return;
                Vector3 p = c.transform.position;
                FieldInfo rf = AccessTools.Field(c.GetType(), "ExplodeDamageRadius");
                object rv = rf == null ? null : rf.GetValue(c);
                float radius = (rv is float ? (float)rv : 6f) + BlastSlack;
                _tmp.Clear();
                foreach (KeyValuePair<GameObject, Flight> e in _flights) if (e.Key != null) _tmp.Add(e.Value);
                for (int i = 0; i < _tmp.Count; i++)
                {
                    Transform tr = _tmp[i].Go.transform;
                    Vector3 centre = tr.position + tr.rotation * (new Vector3(0f, 1.5f, -2f) * PlayerAn2.K);
                    if ((centre - p).sqrMagnitude <= radius * radius) Damage(_tmp[i].Go, p, true);
                }
                _tmp.Clear();
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NpcAircraft blast: " + ex.Message); }
        }

        static GameObject Owner(Transform t)
        {
            for (; t != null; t = t.parent)
                if (_flights.ContainsKey(t.gameObject)) return t.gameObject;
            return null;
        }

        static bool Mine(object mb)
        {
            try
            {
                MethodInfo get = AccessTools.PropertyGetter(mb.GetType(), "photonView");
                object view = get == null ? null : get.Invoke(mb, null);
                if (view == null) return true;
                MethodInfo isMine = AccessTools.PropertyGetter(view.GetType(), "isMine");
                return isMine == null || (bool)isMine.Invoke(view, null);
            }
            catch { return true; }
        }

        internal static void Install(Harmony h)
        {
            try
            {
                Type heli = RevivalPlugin.TypeByName("HelicopterDummy");
                MethodInfo start = heli == null ? null : AccessTools.Method(heli, "Start", null, null);
                if (start != null)
                    h.Patch(start, null, new HarmonyMethod(typeof(NpcAircraft).GetMethod("HeliSpeedPostfix")), null, null, null);
                else RevivalPlugin.L.LogWarning("NpcAircraft: HelicopterDummy.Start missing - aid heli speed unchanged.");
                Type fw = RevivalPlugin.TypeByName("PlayerFirearmWeaponController");
                MethodInfo shot = fw == null ? null : AccessTools.Method(fw, "FireOneShot", null, null);
                if (shot != null)
                    h.Patch(shot, null, new HarmonyMethod(typeof(NpcAircraft).GetMethod("ShotPostfix")), null, null, null);
                else RevivalPlugin.L.LogWarning("NpcAircraft: FireOneShot missing - rounds do not hurt NPC aircraft.");
                Type ex = RevivalPlugin.TypeByName("ExplosionObject");
                MethodInfo explode = ex == null ? null : AccessTools.Method(ex, "Explode", null, null);
                if (explode != null)
                    h.Patch(explode, null, new HarmonyMethod(typeof(NpcAircraft).GetMethod("ExplodePostfix")), null, null, null);
                else RevivalPlugin.L.LogWarning("NpcAircraft: ExplosionObject.Explode missing - blasts do not hurt NPC aircraft.");
                RevivalPlugin.L.LogInfo("NpcAircraft: firearm and explosion hooks installed.");
            }
            catch (Exception e) { RevivalPlugin.L.LogError("NpcAircraft.Install: " + e); }
        }

        // Vanilla aid heli only. All plugin carriers have instantiation data and
        // their own movers (including the player's An-2/Mi-8). No new frame tick.
        public static void HeliSpeedPostfix(object __instance)
        {
            if (!RevivalTroopInsertion.MasterClient()) return;
            try
            {
                MethodInfo pv = AccessTools.Method(__instance.GetType(), "get_photonView", null, null);
                object view = pv == null ? null : pv.Invoke(__instance, null);
                if (view == null) return;
                MethodInfo inst = AccessTools.PropertyGetter(view.GetType(), "instantiationData");
                if (inst == null || inst.Invoke(view, null) != null) return;
                FieldInfo speed = AccessTools.Field(__instance.GetType(), "move_speed");
                if (speed != null && speed.FieldType == typeof(float))
                    speed.SetValue(__instance, Speed((float)speed.GetValue(__instance), SpeedFactor(0f)));
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("NpcAircraft aid speed: " + ex.Message); }
        }

        // ------------------------------------------------------------ network

        internal static class Net
        {
            internal const int Request = 0;
            internal const int Hit = 1;
            internal const int Clear = 2;

            static bool _hooked, _failed;
            static MethodInfo _raise;
            static Type _optType;

            static int Code() { return Mathf.Clamp(CfgEventCode == null ? 159 : CfgEventCode.Value, 0, 199); }

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
                        RevivalPlugin.L.LogWarning("NpcAircraft net: RaiseEvent or OnEventCall missing.");
                        return;
                    }
                    MethodInfo mine = typeof(Net).GetMethod("OnPhotonEvent",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate handler = Delegate.CreateDelegate(onEvent.FieldType, mine);
                    Delegate current = onEvent.GetValue(null) as Delegate;
                    onEvent.SetValue(null, Delegate.Combine(current, handler));
                    _hooked = true;
                    RevivalPlugin.L.LogInfo("NpcAircraft net hooked: event code " + Code() + ".");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    RevivalPlugin.L.LogError("NpcAircraft net not hooked: " + ex);
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
                    RevivalPlugin.L.LogWarning("NpcAircraft net send: " + ex.Message);
                    return false;
                }
            }

            public static void OnPhotonEvent(byte code, object content, int sender)
            {
                try
                {
                    if (code != Code()) return;
                    float[] f = content as float[];
                    if (f == null || f.Length < 1 || !RevivalTroopInsertion.MasterClient()) return;
                    int kind = (int)f[0];
                    if (kind == Request && f.Length >= 7)
                    {
                        // Index 7 (B8a) is the aircraft; an older client's request is an An-2.
                        string said = Flyover.LaunchHere(new Vector3(f[1], f[2], f[3]), f[4], f[5], f[6],
                            f.Length >= 8 && f[7] > 0.5f);
                        RevivalPlugin.L.LogInfo("NpcAircraft: flyover for player " + sender + " - " + said);
                    }
                    else if (kind == Hit && f.Length >= 6)
                    {
                        // W AA4: index 6 is the share of hit points; an older
                        // client's hit is a round or a blast. The sender is paid.
                        GameObject go = PlayerAn2.ByViewId((int)f[1]);
                        float amount = f.Length >= 7 ? Mathf.Clamp(f[6], -1f, 1f) : f[5] > 0.5f ? 1f : -1f;
                        if (go != null) Damage(go, new Vector3(f[2], f[3], f[4]), amount, sender);
                    }
                    else if (kind == Clear)
                    {
                        int n = ClearAll();
                        RevivalPlugin.L.LogInfo("NpcAircraft: " + n + " flight(s) cleared for player " + sender + ".");
                    }
                }
                catch (Exception ex)
                {
                    RevivalPlugin.L.LogWarning("NpcAircraft net receive: " + ex.Message);
                }
            }
        }
    }

    // =====================================================================
    // The admin's test

    /// <summary>
    /// "Test flyover": an An-2 edge to edge over the admin or a map point, for
    /// the AA. The options are the panel's for this session.
    /// </summary>
    internal static class Flyover
    {
        internal const int MinAltitude = 30, MaxAltitude = 1500;
        // 450 km/h so the Tu-95 option (B8a) can fly at its own 420.
        internal const int MinSpeed = 120, MaxSpeed = 450;
        /// <summary>-1 random, else the edge it comes from: 0 N, 1 E, 2 S, 3 W.</summary>
        internal static int From = -1;
        internal static int AltitudeM = 150;
        internal static int SpeedKmh = 200;
        /// <summary>B8a: fly the Tu-95 instead of the An-2 (no bombs; the
        /// model, its far draw and its drone, for a look and a listen).</summary>
        internal static bool Bomber;
        static readonly string[] Edges = { "Random", "N", "E", "S", "W" };
        static readonly string[] EdgeNames = { "north", "east", "south", "west" };

        internal static void Tick()
        {
            // Hook the channel early: a non-master admin's request must find
            // the master listening before the master has launched anything.
            if (Time.frameCount % 120 == 0) NpcAircraft.Net.EnsureHooked();
        }

        /// <summary>The panel button: over the local player.</summary>
        internal static string OverMe()
        {
            GameObject me = MapTools.LocalPlayer();
            if (me == null) return "No local player.";
            return Ask(me.transform.position);
        }

        /// <summary>Over <paramref name="point"/> with the panel's options: the
        /// master flies it, anyone else asks the master.</summary>
        internal static string Ask(Vector3 point)
        {
            float bearing = From >= 0 ? From * 90f : UnityEngine.Random.Range(0f, 360f);
            if (RevivalTroopInsertion.MasterClient())
                return LaunchHere(point, AltitudeM, SpeedKmh, bearing, Bomber);
            if (!NpcAircraft.Net.Send(new float[] { NpcAircraft.Net.Request, point.x, point.y, point.z,
                    AltitudeM, SpeedKmh, bearing, Bomber ? 1f : 0f }, true))
                return "Flyover: the network channel is not up - see the log.";
            return (Bomber ? "Tu-95" : "An-2") + " flyover asked of the host: from " + Compass(bearing) + ", "
                + AltitudeM + " m AGL, " + SpeedKmh + " km/h.";
        }

        /// <summary>Master: launch one. Altitude in metres above the ground,
        /// speed in km/h, bearing the direction it comes from.</summary>
        internal static string LaunchHere(Vector3 over, float altitudeM, float speedKmh, float bearing, bool bomber)
        {
            if (!RevivalTroopInsertion.MasterClient()) return "Flyover: only the host can launch.";
            float alt = Mathf.Clamp(altitudeM, MinAltitude, MaxAltitude) * PlayerAn2.K;
            float speed = Mathf.Clamp(speedKmh, MinSpeed, MaxSpeed) / 3.6f * PlayerAn2.K;
            Vector2 from, to;
            FlightPath.AcrossMap(over, bearing, out from, out to);
            FlightPath path = FlightPath.Straight(from, to, alt, NpcAircraft.Speed(speed, NpcAircraft.SpeedFactor(0f)));
            // The bomber tag makes every client build the Tu-95 on the carrier.
            NpcAircraft.Flight f = NpcAircraft.Launch(path, true,
                bomber ? AirEvents.BomberTag + "test flyover" : "test flyover", null, null);
            if (f == null) return "Flyover: the " + (bomber ? "Tu-95" : "An-2") + " did not appear - see the log.";
            if (bomber) f.Toughness = 3;
            float eta = (new Vector2(over.x, over.z) - from).magnitude / speed;
            return (bomber ? "Tu-95 flyover" : "Flyover") + " from " + Compass(bearing) + ": " + Mathf.RoundToInt(Mathf.Clamp(altitudeM, MinAltitude, MaxAltitude))
                + " m AGL, " + Mathf.RoundToInt(Mathf.Clamp(speedKmh, MinSpeed, MaxSpeed)) + " km/h, overhead in "
                + Mathf.RoundToInt(eta) + " s, " + Mathf.RoundToInt(path.Seconds) + " s edge to edge.";
        }

        internal static string Clear()
        {
            if (RevivalTroopInsertion.MasterClient())
                return "Flyover: " + NpcAircraft.ClearAll() + " NPC aircraft removed.";
            return NpcAircraft.Net.Send(new float[] { NpcAircraft.Net.Clear }, true)
                ? "Flyover: asked the host to remove the NPC aircraft."
                : "Flyover: the network channel is not up - see the log.";
        }

        static string Compass(float bearing)
        {
            int i = Mathf.RoundToInt(Mathf.Repeat(bearing, 360f) / 90f) % 4;
            return Mathf.Abs(Mathf.DeltaAngle(bearing, i * 90f)) < 1f
                ? "the " + EdgeNames[i]
                : Mathf.RoundToInt(Mathf.Repeat(bearing, 360f)) + " deg";
        }

        // W-UI4: a value's text is built once per value.
        static readonly UiMemo _altText = new UiMemo(), _speedText = new UiMemo(), _airText = new UiMemo();
        static readonly string[] Planes = { "An-2", "Tu-95" };

        /// <summary>The admin panel's option rows (Revival.Admin.cs, World
        /// tab): kit controls on the AdminLayout cursor.</summary>
        internal static void Options()
        {
            Rect r = AdminLayout.Row();
            float h = r.height, gap = UiKit.S(UiKit.Gap);
            float half = (r.width - gap) * 0.5f, lab = half * 0.3f, val = half - lab - 2f * h;
            for (int k = 0; k < 2; k++)
            {
                float x = r.x + k * (half + gap);
                bool alt = k == 0;
                AdminLayout.Text(new Rect(x, r.y, lab, h), alt ? "Altitude" : "Speed", UiKit.TextDim);
                if (UiKit.IconButton(new Rect(x + lab, r.y, h, h), UiIcon.Minus, alt ? "lower" : "slower", true))
                {
                    if (alt) AltitudeM = Mathf.Max(MinAltitude, AltitudeM - (AltitudeM > 300 ? 100 : 25));
                    else SpeedKmh = Mathf.Max(MinSpeed, SpeedKmh - 20);
                }
                string text = alt
                    ? (_altText.Stale(AltitudeM) ? _altText.Set(AltitudeM, AltitudeM + " m") : _altText.Text)
                    : (_speedText.Stale(SpeedKmh) ? _speedText.Set(SpeedKmh, SpeedKmh + " km/h") : _speedText.Text);
                UiKit.Label(new Rect(x + lab + h, r.y, val, h), text, UiFont.Body, UiFont.Center, UiKit.Text);
                if (UiKit.IconButton(new Rect(x + lab + h + val, r.y, h, h), UiIcon.Plus, alt ? "higher" : "faster", true))
                {
                    if (alt) AltitudeM = Mathf.Min(MaxAltitude, AltitudeM + (AltitudeM >= 300 ? 100 : 25));
                    else SpeedKmh = Mathf.Min(MaxSpeed, SpeedKmh + 20);
                }
            }
            r = AdminLayout.Row();
            AdminLayout.Text(AdminLayout.Part(r, 0f, 0.15f), "From", UiKit.TextDim);
            From = UiKit.Tabs(AdminLayout.Part(r, 0.15f, 0.85f), From + 1, Edges) - 1;
            r = AdminLayout.Row();
            AdminLayout.Text(AdminLayout.Part(r, 0f, 0.15f), "Aircraft", UiKit.TextDim);
            int plane = UiKit.Tabs(AdminLayout.Part(r, 0.15f, 0.45f), Bomber ? 1 : 0, Planes);
            if (plane == 0 && Bomber) Bomber = false;
            else if (plane == 1 && !Bomber)
            {
                Bomber = true;
                // A bomber's own height and speed: the air events' numbers.
                AltitudeM = Mathf.RoundToInt(AirEvents.BomberAltitude(null));
                SpeedKmh = Mathf.Clamp(Mathf.RoundToInt(AirEvents.BomberKmh), MinSpeed, MaxSpeed);
            }
            int up = NpcAircraft.Airborne();
            UiKit.Chip(AdminLayout.Part(r, 0.62f, 0.38f), _airText.Stale(up) ? _airText.Set(up, "in the air: " + up) : _airText.Text,
                up > 0 ? UiTone.Warning : UiTone.Info);
        }
    }
}

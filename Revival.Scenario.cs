// Z H0b: explicit offline-test-only scenario adapter. No network/backend access.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class ScenarioActor
    {
        internal ScenarioActorSpec Spec;
        internal Component Ai;
        internal GameObject Vehicle;
        internal Mercs.Record Merc;
        internal readonly ScenarioActorMetrics Metrics = new ScenarioActorMetrics();
        internal double NextSample;
        internal int HeldFire, LastHeld, BlockedShots, Kills;
        internal double SpawnedAt;
        internal Vector3 LastPosition;
        internal double ProgressAt;
        internal bool Missing, Spawned;
        internal string AirEvent;
    }

    internal sealed class ScenarioOrderRun
    {
        internal ScenarioOrderSpec Spec;
        internal ScenarioActor Actor;
        internal readonly ScenarioOrderClock Clock = new ScenarioOrderClock();
        internal readonly ScenarioArrivalClock Arrival = new ScenarioArrivalClock();
        internal object Team;
        internal bool Issued { get { return Clock.Issued; } }
        internal bool Executed { get { return Clock.Executed; } }
        internal double Latency { get { return Clock.Latency; } }
        internal Vector3 Origin;
        internal int Shots;
        internal MercOrder Applied;
    }

    internal static class ScenarioRun
    {
        internal static bool Active, Measuring;
        internal static Component Shooter;
        static ScenarioSpec _spec;
        static string _out, _error = "";
        static int _seed, _cursor, _spawned, _captured;
        static double _began, _deadline, _wallBegan, _timescale, _timeline;
        static ScenarioActor[] _actors;
        static ScenarioOrderRun[] _orders;
        static bool[] _airIssued;
        static double[] _airAt;
        static bool[] _captures;
        static readonly Dictionary<int, ScenarioActor> ByNpc = new Dictionary<int, ScenarioActor>();
        static double[] _capturedAt;
        static ScenarioFrames _frames;
        static ScenarioWatchdog _watchdog;
        static readonly double[] ModuleTotal = new double[FrameProf.Count], ModulePeak = new double[FrameProf.Count];
        static readonly double[] ModuleCurrent = new double[FrameProf.Count], ModuleFramePeak = new double[FrameProf.Count];
        static readonly long[] ModuleCalls = new long[FrameProf.Count];
        static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        internal static double Timeout { get { return _spec.Timeout; } }
        internal static void Configure(string[] args, string root, string output, int seed)
        {
            string name = Argument(args, "-ndrScenario", "");
            if (name.Length == 0) return;
            Active = true; _out = output; _seed = seed;
            string path = Path.IsPathRooted(name) ? Path.GetFullPath(name)
                : Path.Combine(Path.Combine(root, "scenarios"), ScenarioSpec.Slug(name) + ".json");
            if (!Inside(path, root)) throw new ArgumentException("Scenario must be in the marked test copy.");
            OfflineStart.PlainPath(path);
            if (new FileInfo(path).Length > 131072) throw new ArgumentException("Scenario exceeds 128 KiB.");
            _spec = ScenarioSpec.Parse(File.ReadAllText(path));
            _timescale = ScenarioSpec.Range(double.Parse(Argument(args, "-ndrTimescale", "1"), CultureInfo.InvariantCulture), 0.1, 10);
            _deadline = Time.realtimeSinceStartup + _spec.Timeout;
            _frames = new ScenarioFrames();
            int airMen = 0;
            for (int i = 0; i < _spec.Air.Length; i++) if (_spec.Air[i].Mode == "raid") airMen += _spec.Air[i].Transports * _spec.Air[i].Load;
            _actors = new ScenarioActor[_spec.Actors.Length + airMen];
            for (int i = 0; i < _spec.Actors.Length; i++) { _actors[i] = new ScenarioActor(); _actors[i].Spec = _spec.Actors[i]; }
            int airSlot = _spec.Actors.Length;
            for (int i = 0; i < _spec.Air.Length; i++) if (_spec.Air[i].Mode == "raid")
                for (int k = 0; k < _spec.Air[i].Transports * _spec.Air[i].Load; k++)
                {
                    ScenarioActor a = new ScenarioActor(); a.Spec = new ScenarioActorSpec();
                    a.Spec.Id = "air-" + _spec.Air[i].Id + "-para-" + (k + 1); a.Spec.Kind = "raid";
                    a.AirEvent = "scenario-" + _spec.Air[i].Id; a.Spec.Faction = _spec.Air[i].Faction;
                    _actors[airSlot++] = a;
                }
            _orders = new ScenarioOrderRun[_spec.Orders.Length];
            Dictionary<string, object> teams = new Dictionary<string, object>();
            for (int i = 0; i < _orders.Length; i++)
            {
                _orders[i] = new ScenarioOrderRun(); _orders[i].Spec = _spec.Orders[i];
                for (int k = 0; k < _actors.Length; k++) if (_actors[k].Spec.Id == _orders[i].Spec.Actor) _orders[i].Actor = _actors[k];
                ScenarioOrderSpec order = _orders[i].Spec;
                if (order.Team.Length > 0)
                {
                    object team;
                    if (!teams.TryGetValue(order.Team, out team)) { team = new MercAttackTeam(order.TeamSize); teams.Add(order.Team, team); }
                    _orders[i].Team = team;
                }
            }
            _captures = new bool[_spec.Captures.Length];
            _airIssued = new bool[_spec.Air.Length]; _airAt = new double[_spec.Air.Length];
            _capturedAt = new double[_spec.Captures.Length];
            _watchdog = new ScenarioWatchdog(_spec.Timeout + 5, _out);
        }

        internal static string Argument(string[] args, string key, string fallback)
        {
            for (int i = 0; i < args.Length; i++) if (args[i] == key)
            { if (i + 1 >= args.Length || args[i + 1].StartsWith("-ndr", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + key); return args[i + 1]; }
            return fallback;
        }
        static bool Inside(string path, string root) { return path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        internal static Vector3 Vector(double[] p) { return new Vector3((float)p[0], (float)p[1], (float)p[2]); }
        internal static Vector3 Position(double[] p)
        { return _spec.Frame == "tower" ? TowerRadar.TowerPoint(Vector(p) / 2.8f) : Vector(p); }
        internal static void PrepareStations()
        {
            // Before world load: scenario mercs occupy these otherwise native posts.
            // Applies only in a marked offline run that explicitly orders stations.
            for (int i = 0; i < _spec.Orders.Length; i++)
                if (_spec.Orders[i].Mode == "gun" || _spec.Orders[i].Mode == "radar")
                {
                    if (Flak.CfgNpcCrew != null) Flak.CfgNpcCrew.Value = false;
                    if (TowerRadar.CfgNpcOperator != null) TowerRadar.CfgNpcOperator.Value = false;
                    return;
                }
        }
        internal static bool Ground(Vector3 requested, float reach, out Vector3 result)
        {
            // Preserve an explicit floor/roof height when it has nearby NavMesh;
            // terrain fallback also permits convenient x/0/z scenario positions.
            NavMeshHit hit;
            if (NavMesh.SamplePosition(requested, out hit, reach, NavMesh.AllAreas)) { result = hit.position; return true; }
            return RevivalGroundEnemies.TryGround(requested, reach, out result);
        }

        internal static void Begin()
        {
            if (!Active) return;
            if (_spec.Frame == "tower" && (TowerRadar.Tower == null || !TowerRoof.Ready))
                throw new Exception("Scenario needs the loaded C1 tower and roof ladder.");
            if (_spec.Observer != null)
            {
                GameObject player = MapTools.LocalPlayer(); Vector3 ground;
                if (player == null || !Ground(Position(_spec.Observer), 6f, out ground)) throw new Exception("No scenario observer ground.");
                if (Physics.CheckCapsule(ground + Vector3.up * 0.9f, ground + Vector3.up * 2.8f, 0.65f, ~0, QueryTriggerInteraction.Ignore))
                    throw new Exception("Blocked observer footprint.");
                player.transform.position = ground + Vector3.up * 0.2f;
            }
            Time.timeScale = (float)_timescale;
            UnityEngine.Random.InitState(_seed);
            _began = Time.time; _wallBegan = Time.realtimeSinceStartup;
            Measuring = true;
            RevivalPlugin.L.LogInfo("Scenario: " + _spec.Name + "; timeline starts after H0a readiness.");
        }

        internal static void Module(int slot, long ticks)
        {
            double ms = ticks * TickMs; ModuleTotal[slot] += ms; ModuleCalls[slot]++;
            ModuleCurrent[slot] += ms;
            if (ms > ModulePeak[slot]) ModulePeak[slot] = ms;
        }

        internal static void FrameBoundary()
        {
            if (!Measuring) return;
            for (int i = 0; i < ModuleCurrent.Length; i++)
            { if (ModuleCurrent[i] > ModuleFramePeak[i]) ModuleFramePeak[i] = ModuleCurrent[i]; ModuleCurrent[i] = 0; }
        }

        internal static void Tick()
        {
            if (!Active || !Measuring) return;
            bool finished = false; string failure = "";
            FrameProf.S(FrameProf.S_ScenarioT);
            try
            {
                if (Time.realtimeSinceStartup >= _deadline) throw new TimeoutException("Scenario hard wall timeout.");
                _timeline = Math.Min(_spec.Duration, Time.time - _began);
                _frames.Add(Time.unscaledDeltaTime * 1000.0);
                // Schedule at most one expensive action per frame, and one actor sample.
                bool eventDone = false;
                for (int i = 0; i < _spec.Actors.Length; i++)
                    if (!_actors[i].Spawned && _actors[i].Spec.At <= _timeline)
                    { Spawn(_actors[i]); eventDone = true; break; }
                for (int i = 0; i < _orders.Length && !eventDone; i++)
                    if (!_orders[i].Issued && _orders[i].Spec.At <= _timeline)
                    { Issue(_orders[i]); eventDone = true; }
                for (int i = 0; i < _airIssued.Length && !eventDone; i++)
                    if (!_airIssued[i] && _spec.Air[i].At <= _timeline)
                    {
                        FrameProf.S(FrameProf.S_ScenarioEvent);
                        try { AirEvents.ScenarioLaunch(_spec.Air[i]); _airIssued[i] = true; _airAt[i] = _timeline; }
                        finally { FrameProf.E(FrameProf.S_ScenarioEvent); }
                        eventDone = true;
                    }
                ScenarioActor sampled = null;
                if (_actors.Length > 0)
                {
                    ScenarioActor a = _actors[_cursor]; _cursor = (_cursor + 1) % _actors.Length;
                    if (a.Spec.Kind != "vehicle" && a.Spawned && _timeline >= a.NextSample)
                    { Sample(a); a.NextSample = _timeline + 0.2; sampled = a; }
                }
                // Order observations are cheap, cached state; time-sliced with their actor.
                for (int i = 0; i < _orders.Length; i++)
                    if (_orders[i].Issued && (!_orders[i].Executed || (_orders[i].Spec.Goal == "roof" && _orders[i].Arrival.At < 0)) && _orders[i].Actor == sampled)
                    { ObserveOrder(_orders[i]); }
                for (int i = 0; i < _captures.Length && !eventDone; i++)
                    if (!_captures[i] && _spec.Captures[i].At <= _timeline)
                    { _capturedAt[i] = Time.time - _began; Capture(_spec.Captures[i]); _captures[i] = true; _captured++; eventDone = true; }
                bool ordersIssued = true;
                for (int i = 0; i < _orders.Length; i++) if (!_orders[i].Issued) { ordersIssued = false; break; }
                for (int i = 0; i < _airIssued.Length; i++) if (!_airIssued[i]) ordersIssued = false;
                if (_timeline >= _spec.Duration && _captured == _captures.Length && _spawned == _spec.Actors.Length && ordersIssued)
                {
                    // Final samples flush exposure and do not count destroyed actors as killed.
                    for (int i = 0; i < _actors.Length; i++) if (_actors[i].Spawned && _actors[i].Spec.Kind != "vehicle") Sample(_actors[i]);
                    for (int i = 0; i < _orders.Length; i++) if (_orders[i].Issued) ObserveOrder(_orders[i]);
                    for (int i = 0; i < _actors.Length; i++) if (_actors[i].Missing) throw new Exception("Actor vanished before death could be observed: " + _actors[i].Spec.Id);
                    finished = true;
                }
            }
            catch (Exception ex) { finished = true; failure = ex.GetBaseException().Message; }
            finally { FrameProf.E(FrameProf.S_ScenarioT); }
            if (finished) Finish(failure.Length == 0, failure);
        }

        static void Spawn(ScenarioActor a)
        {
            FrameProf.S(FrameProf.S_ScenarioEvent);
            try
            {
                Vector3 at = Position(a.Spec.Position), ground;
                if (!Ground(at, 6f, out ground)) throw new Exception("No walkable spawn for " + a.Spec.Id);
                // Reject solid props, slabs and fences from ALL layers, not model-specific boxes.
                // The ground lies below this capsule; triggers are not physical blockers.
                if (a.Spec.Kind != "vehicle" && Physics.CheckCapsule(ground + Vector3.up * 0.9f,
                    ground + Vector3.up * 2.8f, 0.65f, ~0, QueryTriggerInteraction.Ignore))
                    throw new Exception("Blocked actor footprint " + a.Spec.Id);
                if (a.Spec.Kind == "merc")
                {
                    a.Merc = Mercs.ScenarioSpawn(a.Spec, ground);
                    if (a.Merc == null || a.Merc.Unit == null) throw new Exception("Merc spawn failed " + a.Spec.Id);
                    a.Ai = a.Merc.Unit.Ai;
                }
                else if (a.Spec.Kind == "vehicle")
                {
                    // Includes all area colliders, above the support surface. Conservative box.
                    if (Physics.CheckBox(ground + Vector3.up * 5f, new Vector3(8f, 4.5f, 12f), Quaternion.Euler(0, (float)a.Spec.Yaw, 0), ~0, QueryTriggerInteraction.Ignore))
                        throw new Exception("Blocked vehicle footprint " + a.Spec.Id);
                    bool tank;
                    a.Vehicle = a.Spec.Vehicle == "mi8" ? PlayerHeli.BuildFound(ground, (float)a.Spec.Yaw)
                        : VehicleRegistry.Spawn(a.Spec.Vehicle, ground + Vector3.up, Quaternion.Euler(0, (float)a.Spec.Yaw, 0), out tank);
                    if (a.Vehicle == null) throw new Exception("Vehicle spawn failed " + a.Spec.Id);
                    VehicleCondition.Found(a.Vehicle);
                }
                else
                {
                    RevivalComposition.CrewMan man = new RevivalComposition.CrewMan(); man.Role = "scenario"; man.Weapons = a.Spec.Weapons; man.Fpv = false;
                    List<RevivalComposition.CrewMan> loadout = new List<RevivalComposition.CrewMan>(); loadout.Add(man);
                    GameObject settlement = Crew.DropOwnedSquad(ground, new Vector3[] { ground }, a.Spec.Faction, loadout, "ndr-scenario/" + a.Spec.Id,
                        delegate(Component point, int index) { Mercs.ScenarioPointHealth(point, (float)a.Spec.Health); });
                    Array men = settlement == null ? null : Crew.Men(settlement);
                    if (men == null || men.Length != 1) throw new Exception("NPC spawn failed " + a.Spec.Id);
                    a.Ai = men.GetValue(0) as Component;
                    Crew.Forget(settlement);
                    bool ok;
                    if (a.Spec.Kind == "raid")
                    { List<Vector3> path = new List<Vector3>(); path.Add(ground); path.Add(Position(a.Spec.Target)); ok = NpcWar.StartOperation("scenario/" + a.Spec.Id, settlement, men, path, (float)_spec.Duration + 60, loadout); }
                    else ok = NpcWar.StartGround("scenario/" + a.Spec.Id, settlement, men, ground, a.Spec.Patrol, 28f, loadout);
                    if (!ok || a.Ai == null) throw new Exception("NPC AI did not accept " + a.Spec.Id);
                }
                a.LastPosition = a.Ai == null ? ground : a.Ai.transform.position;
                a.ProgressAt = _timeline; a.Metrics.LastAt = _timeline;
                if (a.Ai != null) ByNpc.Add(a.Ai.GetInstanceID(), a);
                a.SpawnedAt = Time.time - _began; a.Spawned = true; _spawned++;
            }
            finally { FrameProf.E(FrameProf.S_ScenarioEvent); }
        }

        internal static void TrackParatrooper(string eventName, Component ai)
        {
            if (!Measuring || ai == null || ByNpc.ContainsKey(ai.GetInstanceID())) return;
            for (int i = _spec.Actors.Length; i < _actors.Length; i++)
            {
                ScenarioActor a = _actors[i]; if (a.Spawned || a.AirEvent != eventName) continue;
                a.Ai = ai; a.Spawned = true; a.SpawnedAt = Time.time - _began;
                a.Metrics.LastAt = a.SpawnedAt; a.LastPosition = ai.transform.position; a.ProgressAt = a.SpawnedAt;
                ByNpc.Add(ai.GetInstanceID(), a); return;
            }
        }

        static void Issue(ScenarioOrderRun run)
        {
            if (run.Actor.Merc == null) throw new Exception("Order actor not spawned: " + run.Spec.Actor);
            run.Applied = Mercs.ScenarioOrder(run.Actor.Merc, run.Spec, run.Team);
            run.Clock.Issue(_timeline); run.Origin = run.Actor.Ai.transform.position;
            run.Shots = NpcWar.MercOrderShots(run.Actor.Merc.Unit);
        }
        static void ObserveOrder(ScenarioOrderRun run)
        {
            MercUnit u = run.Actor.Merc.Unit;
            if (u == null || u.Ai == null) return;
            // Superseded orders stay pending in metrics; never credited to a later order.
            run.Clock.Observe(_timeline, run.Actor.Merc.Order == run.Applied,
                !run.Actor.Metrics.Dead && !run.Actor.Metrics.Downed,
                NpcWar.MercOrderSettled(u), Vector3.Distance(u.Ai.transform.position, run.Origin), NpcWar.MercOrderShots(u) > run.Shots);
            if (run.Spec.Goal == "roof")
            {
                Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f))
                    * (u.Ai.transform.position - TowerRadar.TowerBase) / 2.8f;
                run.Arrival.Observe(_timeline, run.Actor.Merc.Order == run.Applied,
                    !run.Actor.Metrics.Dead && !run.Actor.Metrics.Downed,
                    TowerRoofCore.OnRoof(local) && local.y >= TowerRoofCore.RoofY - 0.6f
                    && local.y <= TowerRoofCore.RoofY + 0.6f && !TowerRoof.Climbing(u.Ai.transform));
            }
        }
        static void Sample(ScenarioActor a)
        {
            if (a.Ai == null) { if (a.Spawned && !a.Metrics.Dead && !a.Metrics.Downed) a.Missing = true; return; }
            bool alive = NpcWar.NpcAlive(a.Ai);
            MercUnit u = a.Merc == null ? null : a.Merc.Unit;
            bool exposed = u != null && u.Sense.Exposed && Time.time - u.Sense.ExposedAt <= 1;
            bool moving = NpcWar.ScenarioMoving(a.Ai);
            Vector3 position = a.Ai.transform.position;
            if (!moving || (position - a.LastPosition).sqrMagnitude >= 1.5f * 1.5f)
            { a.LastPosition = position; a.ProgressAt = _timeline; }
            a.Metrics.Sample(_timeline, alive, NpcWar.GroundDowned(a.Ai), exposed, moving && _timeline - a.ProgressAt >= 6);
            int held = u == null || u.Fight.Brain == null ? 0 : u.Fight.Brain.HeldFire;
            a.HeldFire += Math.Max(0, held - a.LastHeld); a.LastHeld = held;
        }

        internal static void ObserveDamage(Component ai)
        {
            if (!Measuring || ai == null) return;
            ScenarioActor actor; if (!ByNpc.TryGetValue(ai.GetInstanceID(), out actor)) return;
            FrameProf.S(FrameProf.S_ScenarioObserve);
            try
            {
                double now = Math.Min(_spec.Duration, Time.time - _began);
                bool dead = actor.Metrics.Dead;
                actor.Metrics.Sample(now, NpcWar.NpcAlive(ai), NpcWar.GroundDowned(ai), actor.Metrics.Exposed, actor.Metrics.Stuck);
                ScenarioActor killer;
                if (!dead && actor.Metrics.Dead && Shooter != null && ByNpc.TryGetValue(Shooter.GetInstanceID(), out killer)) killer.Kills++;
            }
            finally { FrameProf.E(FrameProf.S_ScenarioObserve); }
        }
        internal static void BlockedShot(Component ai)
        {
            if (!Measuring || ai == null) return;
            ScenarioActor actor; if (ByNpc.TryGetValue(ai.GetInstanceID(), out actor)) actor.BlockedShots++;
        }
        internal static bool OwnsNpc(Component ai) { return Active && ai != null && ByNpc.ContainsKey(ai.GetInstanceID()); }

        static void Capture(ScenarioCameraSpec c)
        {
            FrameProf.S(FrameProf.S_ScenarioEvent);
            GameObject go = null; RenderTexture rt = null; Texture2D image = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                go = new GameObject("NDR scenario fixed camera"); Camera camera = go.AddComponent<Camera>();
                if (Camera.main != null) camera.CopyFrom(Camera.main);
                camera.enabled = false; camera.transform.position = Position(c.Position);
                Vector3 direction = (Position(c.Target) - camera.transform.position).normalized;
                camera.transform.LookAt(Position(c.Target), Mathf.Abs(direction.y) > 0.99f ? Vector3.forward : Vector3.up);
                camera.rect = new Rect(0, 0, 1, 1);
                camera.fieldOfView = (float)c.Fov; camera.aspect = (float)c.Width / c.Height;
                rt = new RenderTexture(c.Width, c.Height, 24); camera.targetTexture = rt;
                camera.Render(); RenderTexture.active = rt;
                image = new Texture2D(c.Width, c.Height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, c.Width, c.Height), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(_out, c.Id + ".png"), image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                if (go != null) UnityEngine.Object.Destroy(go);
                if (image != null) UnityEngine.Object.Destroy(image);
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                FrameProf.E(FrameProf.S_ScenarioEvent);
            }
        }

        internal static void Fail(string error) { if (Active) Finish(false, error); }
        static void Finish(bool success, string error)
        {
            if (_error.Length > 0 || !Active) return;
            FrameBoundary(); Measuring = false; _error = error;
            try
            {
                StringBuilder b = new StringBuilder();
                b.Append("{\"schema\":1,\"success\":").Append(success ? "true" : "false").Append(",\"error\":").Append(ScenarioJson.Quote(error));
                b.Append(",\"scenario\":").Append(ScenarioJson.Quote(_spec == null ? "" : _spec.Name)).Append(",\"seed\":").Append(_seed);
                b.Append(",\"metric_scope\":\"authored actors and native scenario paratroopers; undelivered para slots remain unspawned, not deaths; kills credited to synchronous NpcWar.Shoot damage; other deaths unattributed; exposure is cached merc sense only; downed is native Wounded state; blocked shots are rejected NpcWar.Shoot attempts\"");
                b.Append(",\"timescale\":").Append(ScenarioJson.Number(_timescale)).Append(",\"timeline_seconds\":").Append(ScenarioJson.Number(_timeline));
                b.Append(",\"wall_seconds\":").Append(ScenarioJson.Number(Time.realtimeSinceStartup - _wallBegan));
                b.Append(",\"frames\":").Append(_frames == null ? 0 : _frames.Count);
                b.Append(",\"frame_avg_ms\":").Append(ScenarioJson.Number(_frames == null ? 0 : _frames.Average));
                b.Append(",\"frame_p99_ms\":").Append(ScenarioJson.Number(_frames == null ? 0 : _frames.P99()));
                int kills = 0, deaths = 0, npcDeaths = 0, downed = 0, stuck = 0, blocked = 0, issued = 0, executed = 0, missing = 0; double exposure = 0, latency = 0;
                b.Append(",\"actors\":[");
                if (_actors != null) for (int i = 0; i < _actors.Length; i++)
                {
                    ScenarioActor a = _actors[i]; ScenarioActorMetrics m = a.Metrics;
                    if (i > 0) b.Append(','); b.Append("{\"id\":").Append(ScenarioJson.Quote(a.Spec.Id)).Append(",\"kind\":").Append(ScenarioJson.Quote(a.Spec.Kind));
                    b.Append(",\"spawned\":").Append(a.Spawned ? "true" : "false").Append(",\"spawned_at\":").Append(a.Spawned ? ScenarioJson.Number(a.SpawnedAt) : "null");
                    b.Append(",\"kills\":").Append(a.Kills).Append(",\"deaths\":").Append(m.Deaths).Append(",\"downed\":").Append(m.DownedEvents).Append(",\"seconds_exposed\":").Append(a.Spec.Kind == "merc" ? ScenarioJson.Number(m.SecondsExposed) : "null");
                    b.Append(",\"blocked_line_of_fire_shots\":").Append(a.BlockedShots).Append(",\"friendly_hold_decisions\":").Append(a.HeldFire).Append(",\"stuck_events\":").Append(m.StuckEvents).Append(",\"missing\":").Append(a.Missing ? "true" : "false").Append('}');
                    kills += a.Kills;
                    if (a.Spec.Kind == "merc") deaths += m.Deaths; else if (a.Spec.Kind != "vehicle") npcDeaths += m.Deaths;
                    downed += m.DownedEvents; stuck += m.StuckEvents; blocked += a.BlockedShots; exposure += m.SecondsExposed; if (a.Missing) missing++;
                }
                b.Append("],\"orders\":[");
                if (_orders != null) for (int i = 0; i < _orders.Length; i++)
                {
                    ScenarioOrderRun o = _orders[i]; if (i > 0) b.Append(',');
                    b.Append("{\"actor\":").Append(ScenarioJson.Quote(o.Spec.Actor)).Append(",\"mode\":").Append(ScenarioJson.Quote(o.Spec.Mode));
                    b.Append(",\"issued\":").Append(o.Issued ? "true" : "false").Append(",\"executed\":").Append(o.Executed ? "true" : "false");
                    b.Append(",\"requested_at\":").Append(ScenarioJson.Number(o.Spec.At)).Append(",\"issued_at\":").Append(o.Issued ? ScenarioJson.Number(o.Clock.IssuedAt) : "null");
                    b.Append(",\"latency_seconds\":").Append(o.Executed ? ScenarioJson.Number(o.Latency) : "null");
                    b.Append(",\"goal\":").Append(ScenarioJson.Quote(o.Spec.Goal));
                    b.Append(",\"deadline_seconds\":").Append(ScenarioJson.Number(o.Spec.Deadline));
                    b.Append(",\"arrived_at\":").Append(o.Arrival.At < 0 ? "null" : ScenarioJson.Number(o.Arrival.At));
                    b.Append(",\"arrived_within_deadline\":").Append(o.Spec.Goal != "roof" ? "null" : o.Arrival.Within(o.Spec.At, o.Spec.Deadline) ? "true" : "false").Append('}');
                    if (o.Issued) issued++; if (o.Executed) { executed++; latency += o.Latency; }
                }
                b.Append("],\"kills\":").Append(kills).Append(",\"deaths\":").Append(deaths).Append(",\"downed\":").Append(downed);
                b.Append(",\"npc_deaths\":").Append(npcDeaths).Append(",\"unattributed_deaths\":").Append(Math.Max(0, deaths + npcDeaths - kills));
                b.Append(",\"seconds_exposed\":").Append(ScenarioJson.Number(exposure)).Append(",\"blocked_line_of_fire_shots\":").Append(blocked).Append(",\"stuck_events\":").Append(stuck);
                b.Append(",\"orders_issued\":").Append(issued).Append(",\"orders_executed\":").Append(executed).Append(",\"order_latency_avg_seconds\":").Append(executed == 0 ? "null" : ScenarioJson.Number(latency / executed));
                b.Append(",\"missing_actors\":").Append(missing).Append(",\"spawned\":").Append(_spawned).Append(",\"captures\":[");
                if (_spec != null) for (int i = 0; i < _spec.Captures.Length; i++)
                { bool written = _captures != null && _captures[i]; if (i > 0) b.Append(','); b.Append("{\"file\":").Append(ScenarioJson.Quote(_spec.Captures[i].Id + ".png")).Append(",\"requested_at\":").Append(ScenarioJson.Number(_spec.Captures[i].At)).Append(",\"actual_at\":").Append(written ? ScenarioJson.Number(_capturedAt[i]) : "null").Append(",\"written\":").Append(written ? "true" : "false").Append('}'); }
                b.Append("],\"air\":[");
                if (_spec != null) for (int i = 0; i < _spec.Air.Length; i++)
                {
                    if (i > 0) b.Append(','); b.Append("{\"id\":").Append(ScenarioJson.Quote(_spec.Air[i].Id));
                    b.Append(",\"issued\":").Append(_airIssued[i] ? "true" : "false");
                    b.Append(",\"issued_at\":").Append(_airIssued[i] ? ScenarioJson.Number(_airAt[i]) : "null").Append('}');
                }
                b.Append("],\"f6_modules\":[");
                for (int i = 0; i < FrameProf.Count; i++)
                { if (i > 0) b.Append(','); b.Append("{\"slot\":").Append(i).Append(",\"kind\":").Append(FrameProf.SlotKind(i)).Append(",\"name\":").Append(ScenarioJson.Quote(FrameProf.SlotName(i))).Append(",\"calls\":").Append(ModuleCalls[i]).Append(",\"avg_ms_per_frame\":").Append(ScenarioJson.Number(_frames == null || _frames.Count == 0 ? 0 : ModuleTotal[i] / _frames.Count)).Append(",\"peak_call_ms\":").Append(ScenarioJson.Number(ModulePeak[i])).Append(",\"peak_frame_ms\":").Append(ScenarioJson.Number(ModuleFramePeak[i])).Append('}'); }
                b.Append("]}");
                File.WriteAllText(Path.Combine(_out, "metrics.json"), b.ToString(), new UTF8Encoding(false));
                RevivalPlugin.L.LogInfo("Scenario: metrics written; success=" + success);
            }
            catch (Exception ex) { RevivalPlugin.L.LogError("Scenario metrics write failed: " + ex.Message); }
            Application.Quit();
        }
    }
}

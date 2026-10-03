// Next Day: Survival - Revival Toolkit
//
// Z T1a: the tower's north outside staircase replaces the unusable B1 ladder.
// C W3: it ends on the main roof again (radar console, sandbag posts); the
// only runtime stair part is the threshold over the parapet's collider face.
// Deterministic collision + merged visual geometry on every client; merc
// authority/Photon position replication use existing hooks.
// No new frame tick: F6 slots TowerRoof.Tick / TowerRoof.LateFrame are reused.
// Z T1b: 2 s stalls hop a waypoint, 24 s total goes to the post / foot.
// Physics: ALL-layer diagnostics at 10 Hz, active lookahead at 5 Hz per merc,
// round-robin one actor query per frame. Zero idle physics/managed allocations.
// Steady active target < 0.1 ms average / < 0.5 ms peak; F6 measurement pending.
// C# 3.0, ASCII.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextDayRevival
{
    internal static class TowerRoof
    {
        const float K = TowerRadar.K;
        const float Near = 60f;                  // a move with both ends farther than this is not ours (u)
        const int ClimbCap = 8;

        internal const int LegNone = TowerRoofCore.LegNone, LegWalk = TowerRoofCore.LegWalk,
            LegHold = TowerRoofCore.LegHold, LegClimbUp = TowerRoofCore.LegClimbUp,
            LegClimbDown = TowerRoofCore.LegClimbDown;

        internal static ConfigEntry<bool> CfgOn;

        static GameObject _root;
        static bool _built, _roofNav, _patched, _navSaid;
        static float _next, _nextNav, _nextCheck, _routeWantedUntil;
        static bool _routeReady, _routeSaid;
        static int _checkSegment = 1;
        static float _checkDistance;
        static readonly Collider[] _hits = new Collider[64];
        static Vector3 _base, _centre;
        static Quaternion _rot = Quaternion.identity, _inv = Quaternion.identity;
        static float _footY;
        static Vector3 _footNav, _exitNav, _footL, _exitL;     // world NavMesh spots, the same in the tower frame
        static Transform _legMan;                               // the last Leg's man and where his roof walk ends
        static Vector3 _legEnd;
        static readonly Vector3[] _postNav = new Vector3[TowerRoofCore.PostX.Length];
        static NavMeshDataInstance _patch;
        static Material _sand;

        sealed class Climb
        {
            public Transform Man;
            public NavMeshAgent Agent;
            public bool Busy, Up, Pos, Rot, Blocked, WasEnabled;
            public float NextCheck;
            public int N, Slot;
            public TowerRoofCore.Walk Walk;
            public Vector3 EndPoint;
            public readonly Vector3[] Pts = new Vector3[TowerRoofCore.PathMax];
        }

        static readonly Climb[] _climbs = new Climb[ClimbCap];
        static int _active;
        static int _queryCursor;

        static void Log(string s) { RevivalPlugin.L.LogInfo("TowerRoof: " + s); }

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgOn = cfg.Bind("TowerRadar", "RoofLadder", true,
                "Outside staircase access to the C1 main roof: radar console, five sandbag posts, the cab's inner flight. "
                + "Legacy RoofLadder key retained; no ladder is built. Enabled by default.");
        }

        static bool On { get { return (CfgOn == null || CfgOn.Value) && EastWorld.On && TowerRadar.On; } }

        internal static bool Ready { get { return _built && _root != null; } }

        // ------------------------------------------------------------- frame

        static Vector3 World(Vector3 l) { return _base + _rot * (l * K); }

        static Vector3 Local(Vector3 w) { return (_inv * (w - _base)) * (1f / K); }

        static float FlatSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        // -------------------------------------------------------------- ticks

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            try
            {
                if (On && Ready && !_routeReady && AirfieldObjects.Ready && AirfieldObjects.Pending == 0
                    && now >= _nextCheck && (CombatLoad.AnyNear(_base, 80f) || now < _routeWantedUntil)) {
                    _nextCheck = now + 0.1f;
                    ValidateNext();
                }
                if (now < _next) return;
                _next = now + 1f;
                if (!On || TowerRadar.Tower == null || !TowerRadar.Built)
                {
                    if (_built) Clear(!On ? "off" : "the tower went away");
                    return;
                }
                if (_built && _root == null) { Clear("its scene went away"); return; }
                if (!_built) Build();
                else if (!_roofNav) RoofNavOk();
            }
            catch (Exception ex)
            {
                RevivalPlugin.L.LogError("TowerRoof: " + ex);
                _next = _nextCheck = now + 30f;
            }
        }

        /// <summary>Every client after the animator: the climbing mercs of
        /// this client on the outside stairs.</summary>
        internal static void LateFrame()
        {
            if (_active == 0) return;
            float now = Time.time;
            bool queried = false;
            int first = _queryCursor;
            for (int scan = 0; scan < ClimbCap; scan++)
            {
                int i = (first + scan) % ClimbCap;
                Climb c = _climbs[i];
                if (c == null || !c.Busy) continue;
                if (c.Man == null || !Ready || (c.WasEnabled && c.Agent != null && !c.Agent.enabled)) { End(c, false); continue; }
                Vector3 at = Local(c.Man.position);
                at.y -= TowerRoofCore.FootLift;

                if (!queried && now >= c.NextCheck) {
                    queried = true;
                    _queryCursor = (i + 1) % ClimbCap;
                    c.NextCheck = now + 0.2f;
                    int target = Math.Min(c.Walk.Next, c.N - 1);
                    Vector3 ahead = TowerRoofCore.Advance(at, c.Pts[target], 0.1f);
                    c.Blocked = !ClearPoint(ahead, c.Man);
                }
                Vector3 l;
                int action = TowerRoofCore.Step(ref c.Walk, c.Pts, c.N, at,
                    now, Time.deltaTime, !c.Blocked, out l);
                if (action == TowerRoofCore.Finished || action == TowerRoofCore.Fallback) { End(c, true); continue; }
                if (action == TowerRoofCore.Hop) c.Blocked = true;
                Vector3 dir = l - at;
                l.y += TowerRoofCore.FootLift;
                c.Man.position = World(l);
                dir.y = 0f;
                dir = _rot * dir;
                if (dir.sqrMagnitude > 1e-6f) c.Man.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }
        }

        // ------------------------------------------------------ merc routing

        /// <summary>The leg of a merc's move (NpcWar.MercMove): LegNone - not
        /// the roof's business; LegWalk - walk exactly to leg; LegHold - at
        /// his post, leg = where he looks; LegClimbUp/Down - start Climb.</summary>
        internal static int Leg(Transform man, Vector3 goal, int slot, out Vector3 leg)
        {
            leg = goal;
            if (!Ready || man == null) return LegNone;
            Vector3 m = man.position;
            if (FlatSq(m, _centre) > Near * Near && FlatSq(goal, _centre) > Near * Near) return LegNone;
            _routeWantedUntil = Time.realtimeSinceStartup + 30f;
            Vector3 l, lg = Local(goal);
            int r = TowerRoofCore.Leg(Local(m), lg, slot, _footL, _exitL, out l);
            _legMan = man;
            _legEnd = TowerRoofCore.Target(lg, slot);
            switch (r)
            {
                case LegWalk:
                    // A cross-map order still approaches normally. Recovery
                    // begins inside the tower's existing nearby-move radius.
                    if (FlatSq(m, _centre) > Near * Near) { leg = _footNav; return LegWalk; }
                    // Approach, stairs AND final post belong to the bounded
                    // direct walk; no NavMesh path can veto the roof order.
                    leg = World(l);
                    return TowerRoofCore.GoalUp(lg) ? LegClimbUp : LegClimbDown;
                case TowerRoofCore.LegNav:
                    // the cab's furniture or the last steps: the baked NavMesh
                    leg = World(l);
                    return LegWalk;
                case LegHold:
                    leg = _rot * l;
                    return r;
                case LegNone:
                    return r;
                default:
                    leg = World(l);
                    return r;
            }
        }

        static int Wrap(int slot)
        {
            int n = _postNav.Length;
            int i = slot % n;
            return i < 0 ? i + n : i;
        }

        /// <summary>A goal on or beside the roof (world).</summary>
        internal static bool GoalUp(Vector3 w)
        {
            return Ready && FlatSq(w, _centre) < Near * Near && TowerRoofCore.GoalUp(Local(w));
        }

        /// <summary>Man and goal on different ends of the outside stairs (world): a
        /// flat arrival check must not count him there.</summary>
        internal static bool Split(Vector3 man, Vector3 goal)
        {
            if (!Ready) return false;
            if (FlatSq(man, _centre) > Near * Near && FlatSq(goal, _centre) > Near * Near) return false;
            return TowerRoofCore.Split(Local(man), Local(goal));
        }

        /// <summary>In a fight on the roof: a run to a spot off the roof
        /// becomes a run to his post.</summary>
        internal static bool KeepUp(Vector3 man, Vector3 goal, int slot, out Vector3 post)
        {
            post = goal;
            if (!Ready || FlatSq(man, _centre) > Near * Near) return false;
            if (!TowerRoofCore.OnRoof(Local(man)) || TowerRoofCore.OnRoof(Local(goal))) return false;
            post = World(TowerRoofCore.Post(slot));
            return true;
        }

        internal static bool Climbing(Transform man)
        {
            if (_active == 0 || man == null) return false;
            for (int i = 0; i < ClimbCap; i++)
                if (_climbs[i] != null && _climbs[i].Busy && _climbs[i].Man == man) return true;
            return false;
        }

        /// <summary>Start the direct route immediately, including approach and
        /// the roof post. Live validation and roof NavMesh are diagnostic only.</summary>
        internal static bool StartClimb(Transform man, NavMeshAgent agent, bool up, int slot)
        {
            if (!Ready || man == null) return false;
            if (Climbing(man)) return true;
            Climb c = null;
            for (int i = 0; i < ClimbCap && c == null; i++)
            {
                if (_climbs[i] == null) _climbs[i] = new Climb();
                if (!_climbs[i].Busy) c = _climbs[i];
            }
            if (c == null) return false;
            c.Busy = true;
            c.Man = man;
            c.Agent = agent;
            c.WasEnabled = agent != null && agent.enabled;
            c.Up = up;
            c.Slot = slot;
            c.Blocked = true;
            c.NextCheck = 0f;
            Vector3 start = Local(man.position);
            if (up && TowerRoofCore.OnRoof(start)) {
                c.Pts[0] = start;
                c.N = 1;
            } else c.N = TowerRoofCore.Path(up, start, TowerRoofCore.Foot(_footY), TowerRoofCore.Exit(), c.Pts);
            // up: around the roof's obstacles to the seat, the cab or his post
            if (up) c.N = TowerRoofCore.Route(c.Pts, c.N, _legMan == man ? _legEnd : TowerRoofCore.Post(slot));
            _legMan = null;
            c.EndPoint = World(c.Pts[c.N - 1]);
            TowerRoofCore.Begin(ref c.Walk, c.Pts, c.N, Time.time);
            c.Pos = c.Rot = true;
            if (agent != null)
            {
                c.Pos = agent.updatePosition;
                c.Rot = agent.updateRotation;
                try
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh) { agent.ResetPath(); agent.isStopped = true; }
                }
                catch { }
                agent.updatePosition = false;
                agent.updateRotation = false;
            }
            man.position = World(start + Vector3.up * TowerRoofCore.FootLift);
            _active++;
            return true;
        }

        // A superseding order can never leave an unbound agent halfway up.
        // Death/authority loss only release the pose; never move a dead/peer body.
        internal static void Cancel(Transform man, bool land)
        {
            if (man == null || _active == 0) return;
            for (int i = 0; i < ClimbCap; i++) {
                Climb c = _climbs[i];
                if (c == null || !c.Busy || c.Man != man) continue;
                if (land) c.EndPoint = TowerRoofCore.OnRoof(Local(man.position)) ? World(TowerRoofCore.Post(c.Slot)) : _footNav;
                End(c, land);
                return;
            }
        }

        /// <summary>The climb is over (done: put him on the NavMesh at its end)
        /// or cut short (dead, despawned, the tower gone).</summary>
        static void End(Climb c, bool done)
        {
            NavMeshAgent a = c.Agent;
            Transform man = c.Man;
            if (done && man != null)
            {
                Vector3 end = c.EndPoint;
                man.position = end;
                if (a != null && a.isActiveAndEnabled)
                {
                    try { if (!a.Warp(end)) a.nextPosition = end; } catch { }
                }
            }
            if (a != null)
            {
                a.updatePosition = c.Pos;
                a.updateRotation = c.Rot;
                try { if (a.isActiveAndEnabled && a.isOnNavMesh) a.isStopped = false; } catch { }
            }
            if (done && man != null) man.position = c.EndPoint;
            c.Man = null;
            c.Agent = null;
            c.Busy = false;
            if (_active > 0) _active--;
        }

        // ------------------------------------------------------------- build

        static void Build()
        {
            Transform tower = TowerRadar.Tower;
            _base = TowerRadar.TowerBase;
            _rot = Quaternion.Euler(0f, TowerRadar.TowerYaw, 0f);
            _inv = Quaternion.Inverse(_rot);
            _centre = World(new Vector3(-6f, TowerRoofCore.RoofY, 0f));   // open main roof, west of the inner flight
            TowerRoofCore.Init();
            _footY = FootY();

            GameObject root = new GameObject("NDR TowerRoof");
            Scene scene = tower.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = _base;
            root.transform.rotation = _rot;
            _root = root;

            Material steel = RadarModel.Steel;
            if (steel == null) steel = Mat("NDR_Roof_Steel", new Color(0.33f, 0.36f, 0.3f), 0.3f, 0.3f);
            if (_sand == null) _sand = Mat("NDR_Roof_Sandbag", new Color(0.50f, 0.45f, 0.32f), 0f, 0.1f);
            List<CombineInstance> parts = new List<CombineInstance>(128);
            Threshold(parts, root.transform);
            Finish(parts, "Stair threshold", steel, root.transform);
            BagMesh(parts);
            Finish(parts, "Sandbags", _sand, root.transform);
            BagColliders(root.transform);

            _footNav = Sample(World(TowerRoofCore.Foot(_footY)), 0.6f * K, 0.3f * K, World(TowerRoofCore.Foot(_footY)));
            _footL = Local(_footNav);
            _exitNav = World(TowerRoofCore.Exit());
            _exitL = TowerRoofCore.Exit();
            _built = true;
            _roofNav = _patched = _navSaid = false;
            _nextNav = _nextCheck = 0f;
            _checkSegment = 1; _checkDistance = 0f;
            _routeReady = _routeSaid = false;
            RoofNavOk();
            Log("outside stair threshold and roof posts built on " + tower.name + "; checking every route sample against ALL live colliders.");
        }

        static float FootY() { return TowerRoofCore.Stair[0].y; }

        // The global broadphase includes ALL colliders (also slabs, props,
        // fences, terrain and runtime geometry), with no C1/model/layer whitelist.
        // A 50 m area subset cannot contain an obstacle missed by this query.
        static bool ClearPoint(Vector3 local, Transform man)
        {
            Vector3 foot = World(local + Vector3.up * TowerRoofCore.FootLift);
            float r = TowerRoofCore.CapsuleRadius * K;
            int n = Physics.OverlapCapsuleNonAlloc(foot + Vector3.up * r,
                foot + Vector3.up * (TowerRoofCore.CapsuleHeight * K - r), r,
                _hits, ~0, QueryTriggerInteraction.Ignore);
            if (n >= _hits.Length) return false; // fail closed on overflow
            for (int i = 0; i < n; i++) {
                Collider hit = _hits[i];
                if (hit != null && (man == null || (hit.transform != man && !hit.transform.IsChildOf(man)))) return false;
            }
            RaycastHit support;
            return Physics.Raycast(foot + Vector3.up * (0.15f * K), Vector3.down,
                out support, 0.5f * K, ~0, QueryTriggerInteraction.Ignore)
                && Mathf.Abs(Local(support.point).y - local.y) < 0.25f;
        }

        static void ValidateNext()
        {
            Vector3 a = TowerRoofCore.Stair[_checkSegment - 1], b = TowerRoofCore.Stair[_checkSegment];
            float length = (b - a).magnitude;
            Vector3 p = Vector3.Lerp(a, b, length < 0.001f ? 1f : Mathf.Min(1f, _checkDistance / length));
            if (!ClearPoint(p, null)) {
                if (!_routeSaid) { _routeSaid = true; Log("stair route blocked at " + World(p) + "; bounded merc recovery remains active."); }
                _nextCheck = Time.realtimeSinceStartup + 2f;
                return;
            }
            _checkDistance += 0.2f;
            if (_checkDistance > length + 0.2f) {
                _checkDistance = 0f;
                _checkSegment++;
                if (_checkSegment >= TowerRoofCore.Stair.Length) {
                    _routeReady = true;
                    Log("outside stair route validated against ALL live colliders; merc roof access ready.");
                }
            }
        }

        static Vector3 Sample(Vector3 w, float radius, float maxDy, Vector3 fallback)
        {
            NavMeshHit h;
            if (NavMesh.SamplePosition(w, out h, radius, NavMesh.AllAreas) && Mathf.Abs(h.position.y - w.y) <= maxDy)
                return h.position;
            return fallback;
        }

        /// <summary>Is there NavMesh on the roof (bake or our patch)? Checked
        /// at most every 2 s until it is; bakes the patch once.</summary>
        static bool RoofNavOk()
        {
            if (_roofNav) return true;
            if (!Ready || Time.realtimeSinceStartup < _nextNav) return false;
            _nextNav = Time.realtimeSinceStartup + 2f;
            NavMeshHit h;
            Vector3 c = _centre;
            if (NavMesh.SamplePosition(c, out h, 2.5f, NavMesh.AllAreas) && Mathf.Abs(h.position.y - c.y) < 1.2f)
            {
                _roofNav = true;
                _exitNav = Sample(World(TowerRoofCore.Exit()), 2.5f, 1.2f, World(TowerRoofCore.Exit()));
                _exitL = Local(_exitNav);
                for (int i = 0; i < _postNav.Length; i++)
                    _postNav[i] = Sample(World(TowerRoofCore.Post(i)), 2f, 1.2f, World(TowerRoofCore.Post(i)));
                if (_navSaid) Log("the roof NavMesh is up" + (_patched ? " (our patch)." : "."));
                return true;
            }
            if (!_patched) Patch();
            return false;
        }

        /// <summary>A NavMesh over the main roof only (the bake normally has
        /// it): the slab and the roof walk's obstacles, baked asynchronously.</summary>
        static void Patch()
        {
            _patched = true;
            try
            {
                List<NavMeshBuildSource> src = new List<NavMeshBuildSource>();
                float midX = (TowerRoofCore.RoofMinX + TowerRoofCore.RoofMaxX) * 0.5f, midZ = (TowerRoofCore.RoofMinZ + TowerRoofCore.RoofMaxZ) * 0.5f;
                src.Add(BoxSource(new Vector3(midX, TowerRoofCore.RoofY - 0.175f, midZ),
                    new Vector3(TowerRoofCore.RoofMaxX - TowerRoofCore.RoofMinX + 0.6f, 0.35f, TowerRoofCore.RoofMaxZ - TowerRoofCore.RoofMinZ + 0.6f)));
                // every obstacle of the roof walk (cab, vents, bags, console) as a 3 m block
                for (int i = 0; i < TowerRoofCore.ObstacleCount; i++) {
                    float x0, z0, x1, z1;
                    TowerRoofCore.Obstacle(i, out x0, out z0, out x1, out z1);
                    float d = TowerRoofCore.Inflate;
                    src.Add(BoxSource(new Vector3((x0 + x1) * 0.5f, TowerRoofCore.RoofY + 1.5f, (z0 + z1) * 0.5f),
                        new Vector3(Mathf.Max(0.1f, x1 - x0 - 2f * d), 3f, Mathf.Max(0.1f, z1 - z0 - 2f * d))));
                }
                Bounds b = new Bounds(World(new Vector3(midX, TowerRoofCore.RoofY, midZ)), new Vector3(26f * K, 6f * K, 20f * K));
                AsyncOperation op;
                CrossingCore.Patch(src, b, out _patch, out op);
                Log("the roof has no NavMesh in the bake - baking a patch over it.");
            }
            catch (Exception ex) { RevivalPlugin.L.LogWarning("TowerRoof: roof NavMesh patch failed: " + ex.Message); }
        }

        static NavMeshBuildSource BoxSource(Vector3 centreM, Vector3 sizeM)
        {
            NavMeshBuildSource s = new NavMeshBuildSource();
            s.shape = NavMeshBuildSourceShape.Box;
            s.size = sizeM * K;
            s.transform = Matrix4x4.TRS(World(centreM), _rot, Vector3.one);
            s.area = 0;
            return s;
        }

        static void Clear(string why)
        {
            for (int i = 0; i < ClimbCap; i++)
                if (_climbs[i] != null && _climbs[i].Busy) End(_climbs[i], false);
            _active = 0;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            if (_patched && _patch.valid) NavMesh.RemoveNavMeshData(_patch);
            _patch = new NavMeshDataInstance();
            _built = _roofNav = _patched = _routeReady = false;
            Log("cleared (" + why + ").");
        }

        // --------------------------------------------------------- geometry

        static Mesh _cube;

        static void Box(List<CombineInstance> list, Vector3 centreM, Vector3 sizeM)
        {
            if (_cube == null)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = go.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.Destroy(go);
            }
            CombineInstance ci = new CombineInstance();
            ci.mesh = _cube;
            ci.transform = Matrix4x4.TRS(centreM * K, Quaternion.identity, sizeM * K);
            list.Add(ci);
        }

        static void Finish(List<CombineInstance> parts, string name, Material m, Transform parent)
        {
            Mesh mesh = new Mesh();
            mesh.name = "NDR TowerRoof " + name;
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
            parts.Clear();
        }

        static Material Mat(string name, Color c, float metal, float gloss)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            Material m = new Material(shader);
            m.name = name;
            m.color = c;
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }

        // The threshold over the parapet's collider face in the north gap
        // (zero-thickness, z 8.99, up to 9.22 m): two ramps and a plateau,
        // the gap's own parapet ends are its sides. Everything else of the
        // stair is the shipped model.
        static void Threshold(List<CombineInstance> p, Transform root)
        {
            for (int i = TowerRoofCore.ThresholdFirst; i <= TowerRoofCore.ThresholdLast; i++) {
                Vector3 a = TowerRoofCore.Stair[i - 1], b = TowerRoofCore.Stair[i];
                Vector3 delta = b - a;
                Vector3 axis = delta.normalized;
                Vector3 side = Vector3.Cross(axis, Vector3.up).normalized;
                Vector3 normal = Vector3.Cross(side, axis).normalized;
                Quaternion rot = Quaternion.LookRotation(side, normal);
                Vector3 size = new Vector3(delta.magnitude, TowerRoofCore.ThresholdThick, TowerRoofCore.ThresholdWidth);
                Vector3 centre = (a + b) * 0.5f - normal * (TowerRoofCore.ThresholdThick * 0.5f);
                GameObject go = new GameObject("StairThreshold_" + i);
                go.transform.SetParent(root, false);
                go.transform.localPosition = centre * K;
                go.transform.localRotation = rot;
                go.AddComponent<BoxCollider>().size = size * K;
                RotBox(p, centre, size, rot);
            }
        }

        static void RotBox(List<CombineInstance> p, Vector3 centre, Vector3 size, Quaternion rot)
        {
            if (_cube == null) Box(p, Vector3.zero, Vector3.zero);
            CombineInstance c = new CombineInstance(); c.mesh = _cube;
            c.transform = Matrix4x4.TRS(centre * K, rot, size * K); p.Add(c);
        }

        static void BagMesh(List<CombineInstance> p)
        {
            const float course = TowerRoofCore.BagH / 3f, bag = 0.55f;
            for (int i = 0; i < TowerRoofCore.BagX.Length; i++)
            {
                bool alongX = TowerRoofCore.BagSX[i] > TowerRoofCore.BagSZ[i];
                float len = alongX ? TowerRoofCore.BagSX[i] : TowerRoofCore.BagSZ[i];
                float thick = alongX ? TowerRoofCore.BagSZ[i] : TowerRoofCore.BagSX[i];
                for (int k = 0; k < 3; k++)
                {
                    float y = TowerRoofCore.RoofY + course * (k + 0.5f);
                    float inset = 0.04f * k;
                    float lo = -len * 0.5f + inset, hi = len * 0.5f - inset;
                    for (float s = lo - ((k & 1) == 1 ? bag * 0.5f : 0f); s < hi - 0.05f; s += bag)
                    {
                        float a = Mathf.Max(s, lo), b = Mathf.Min(s + bag, hi);
                        if (b - a < 0.08f) continue;
                        AddBag(p, i, alongX, (a + b) * 0.5f, b - a - 0.02f, y, thick - inset * 2f, course);
                    }
                }
            }
        }

        static void AddBag(List<CombineInstance> p, int i, bool alongX, float along, float len, float y, float thick, float h)
        {
            Vector3 c = alongX ? new Vector3(TowerRoofCore.BagX[i] + along, y, TowerRoofCore.BagZ[i])
                               : new Vector3(TowerRoofCore.BagX[i], y, TowerRoofCore.BagZ[i] + along);
            Vector3 s = alongX ? new Vector3(len, h * 0.96f, thick) : new Vector3(thick, h * 0.96f, len);
            Box(p, c, s);
        }

        static void BagColliders(Transform root)
        {
            for (int i = 0; i < TowerRoofCore.BagX.Length; i++)
            {
                GameObject go = new GameObject("Sandbag_" + i);
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(TowerRoofCore.BagX[i], TowerRoofCore.RoofY + TowerRoofCore.BagH * 0.5f, TowerRoofCore.BagZ[i]) * K;
                Vector3 size = new Vector3(TowerRoofCore.BagSX[i], TowerRoofCore.BagH, TowerRoofCore.BagSZ[i]) * K;
                go.AddComponent<BoxCollider>().size = size;
                // a merc fighting on the baked roof NavMesh walks round, not into, the wall
                NavMeshObstacle o = go.AddComponent<NavMeshObstacle>();
                o.shape = NavMeshObstacleShape.Box;
                o.size = size;
                o.carving = true;
                o.carveOnlyStationary = true;
            }
        }

    }
}

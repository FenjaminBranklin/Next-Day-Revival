// Next Day: Survival - Revival Toolkit
//
// Y B1 TOWER ROOF: the roof of the tower C1 (the radar HQ's cab, 15.35 m up)
// had no way up - the stairs end in the cab. This module, on every client,
// once the HQ has found the C1 model (TowerRadar.Tower):
//
//   1  THE LADDER. A steel ladder on the tower's east face, straight from the
//      ground to the cab roof and 1.1 m on over its rail, between the window
//      columns (Revival.TowerRoofCore.cs has the layout). Players climb it
//      with the game's own ladder: the plain group Ladder / ClimbDownPoint /
//      StartPoint / BeupPoint / EndPoint goes through EastLadders.Wire, the
//      same wiring as the chimney B1c (a LadderObject, layer 17, tag Ladder).
//      Look at the ladder and press the interact key: up; on the roof look at
//      the rail over the ladder: down.
//   2  THE MERCS' CLIMB. A merc whose move ends on the roof (STAY there, or
//      FOLLOW an owner standing up there) walks to the ladder's foot, climbs
//      (a few seconds, the agent parked, the body moved on the ladder in
//      LateFrame) and steps onto the roof; ordered down he walks to the top,
//      climbs down and goes on. NpcWar.MercMove asks Leg(); RunGround leaves a
//      climbing merc alone (Climbing()). The merc AI runs on its owner's
//      client, so the climb does too; the other clients see the NPC's own
//      Photon replication of his position - nothing new is sent.
//   3  THE POSTS. Five sandbag walls (0.9 m, crouch cover, box colliders) at
//      the roof's edges; each merc slot has its post behind one and holds it
//      crouched, looking out. In a fight up there he stays on the roof
//      (KeepUp: a run to a spot below becomes a run to his post).
//   4  ROOF NAVMESH. The C1 bake has a roof level (unconnected: that is the
//      point of the ladder); where the roof has no NavMesh a small patch is
//      baked asynchronously over it once (CrossingCore.Settings).
//
// COST (F6 TowerRoof.Tick / TowerRoof.LateFrame): Tick is a 1 Hz check until
// the tower is found, then a 1 Hz liveness test; the one-time build (two
// merged meshes, eight boxes, one async NavMesh patch where needed) runs on
// one frame. LateFrame returns at once when nobody climbs; a climb is a few
// float operations per climbing merc. Leg() is a flat distance test per merc
// move away from the tower. Nothing here allocates after the build.
//
// Acts with [World] EastTile and [TowerRadar] Enabled; [TowerRadar]
// RoofLadder (default on) switches only this module.
// C# 3.0 (csc from .NET 3.5). ASCII only.
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
        static float _next, _nextNav;
        static Vector3 _base, _centre;
        static Quaternion _rot = Quaternion.identity, _inv = Quaternion.identity;
        static float _footY;
        static Vector3 _footNav, _exitNav, _footL, _exitL;     // world NavMesh spots, the same in the tower frame
        static readonly Vector3[] _postNav = new Vector3[TowerRoofCore.PostX.Length];
        static NavMeshDataInstance _patch;
        static Material _sand;

        sealed class Climb
        {
            public Transform Man;
            public NavMeshAgent Agent;
            public bool Busy, Up, Pos, Rot;
            public float T0, Duration;
            public int N;
            public readonly Vector3[] Pts = new Vector3[TowerRoofCore.PathMax];
        }

        static readonly Climb[] _climbs = new Climb[ClimbCap];
        static int _active;

        static void Log(string s) { RevivalPlugin.L.LogInfo("TowerRoof: " + s); }

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgOn = cfg.Bind("TowerRadar", "RoofLadder", true,
                "Y B1: an outside ladder on the tower C1's east face from the ground to the cab roof (players climb it "
                + "with the game's ladder, mercs ordered onto the roof walk to it and climb), and five sandbag posts "
                + "on the roof as cover.");
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
            if (now < _next) return;
            _next = now + 1f;
            try
            {
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
                _next = now + 30f;
            }
        }

        /// <summary>Every client after the animator: the climbing mercs of
        /// this client on the ladder.</summary>
        internal static void LateFrame()
        {
            if (_active == 0) return;
            float now = Time.time;
            for (int i = 0; i < ClimbCap; i++)
            {
                Climb c = _climbs[i];
                if (c == null || !c.Busy) continue;
                if (c.Man == null || !Ready || (c.Agent != null && !c.Agent.enabled)) { End(c, false); continue; }
                float t = now - c.T0;
                if (t >= c.Duration) { End(c, true); continue; }
                Vector3 l = TowerRoofCore.At(c.Pts, c.N, t);
                c.Man.position = World(l);
                Vector3 dir;
                if (TowerRoofCore.OnRungs(l, _footY)) dir = _rot * Vector3.left;         // facing the wall
                else
                {
                    Vector3 ahead = TowerRoofCore.At(c.Pts, c.N, t + 0.1f) - l;
                    ahead.y = 0f;
                    dir = ahead.sqrMagnitude > 1e-6f ? _rot * ahead : _rot * (c.Up ? Vector3.left : Vector3.right);
                }
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
            Vector3 l;
            int r = TowerRoofCore.Leg(Local(m), Local(goal), slot, _footL, _exitL, out l);
            switch (r)
            {
                case LegWalk:
                    if (TowerRoofCore.OnRoof(Local(m)))
                        leg = TowerRoofCore.GoalUp(Local(goal)) ? _postNav[Wrap(slot)] : _exitNav;
                    else leg = _footNav;
                    return r;
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

        /// <summary>Man and goal on different sides of the ladder (world): a
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
            if (!Ready || !_roofNav || FlatSq(man, _centre) > Near * Near) return false;
            if (!TowerRoofCore.OnRoof(Local(man)) || TowerRoofCore.OnRoof(Local(goal))) return false;
            post = _postNav[Wrap(slot)];
            return true;
        }

        internal static bool Climbing(Transform man)
        {
            if (_active == 0 || man == null) return false;
            for (int i = 0; i < ClimbCap; i++)
                if (_climbs[i] != null && _climbs[i].Man == man) return true;
            return false;
        }

        /// <summary>Start a climb (up from the foot, down from the top). False
        /// when it cannot: no roof NavMesh (yet) to put him on, or full.</summary>
        internal static bool StartClimb(Transform man, NavMeshAgent agent, bool up)
        {
            if (!Ready || man == null) return false;
            if (Climbing(man)) return true;
            if (!RoofNavOk())
            {
                if (!_navSaid) { _navSaid = true; Log("the roof has no NavMesh yet - mercs wait at the ladder."); }
                return false;
            }
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
            c.Up = up;
            c.N = TowerRoofCore.Path(up, Local(man.position), _footL, _exitL, c.Pts);
            c.Duration = TowerRoofCore.Duration(c.Pts, c.N);
            c.T0 = Time.time;
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
            _active++;
            return true;
        }

        /// <summary>The climb is over (done: put him on the NavMesh at its end)
        /// or cut short (dead, despawned, the tower gone).</summary>
        static void End(Climb c, bool done)
        {
            NavMeshAgent a = c.Agent;
            Transform man = c.Man;
            if (done && man != null)
            {
                Vector3 end = c.Up ? _exitNav : _footNav;
                man.position = end;
                if (a != null && a.isActiveAndEnabled)
                {
                    try { a.Warp(end); } catch { }
                }
            }
            if (a != null)
            {
                a.updatePosition = c.Pos;
                a.updateRotation = c.Rot;
                try { if (a.isActiveAndEnabled && a.isOnNavMesh) a.isStopped = false; } catch { }
            }
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
            _centre = World(new Vector3(8.1f, TowerRoofCore.RoofY, 0f));
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
            LadderMesh(parts);
            Finish(parts, "Ladder mesh", steel, root.transform);
            BagMesh(parts);
            Finish(parts, "Sandbags", _sand, root.transform);
            BagColliders(root.transform);
            GameLadder(root.transform, scene.name);

            _footNav = Sample(World(TowerRoofCore.Foot(_footY)), 4f, 3f, World(TowerRoofCore.Foot(_footY)));
            _footL = Local(_footNav);
            _exitNav = World(TowerRoofCore.Exit());
            _exitL = TowerRoofCore.Exit();
            _built = true;
            _roofNav = _patched = _navSaid = false;
            _nextNav = 0f;
            RoofNavOk();
            Log("built on \"" + tower.name + "\": ladder " + (TowerRoofCore.RoofY - _footY).ToString("F1")
                + " m from the ground to the roof at " + World(TowerRoofCore.Exit()) + ", " + TowerRoofCore.Posts
                + " sandbag posts, roof NavMesh " + (_roofNav ? "from the bake" : "pending") + ".");
        }

        /// <summary>The ground at the ladder's foot (tower frame, m): a ray
        /// down there, the terrain where it misses.</summary>
        static float FootY()
        {
            Vector3 top = World(new Vector3(TowerRoofCore.StartX, 4f, TowerRoofCore.LadderZ));
            RaycastHit hit;
            if (Physics.Raycast(top, Vector3.down, out hit, 8f * K, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return Mathf.Clamp(Local(hit.point).y, -2f, 2f);
            float y;
            if (EastWorld.TerrainHeight(top, out y)) return Mathf.Clamp((y - _base.y) / K, -2f, 2f);
            return 0f;
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

        /// <summary>A NavMesh over the cab roof only: the slab, the sandbags
        /// and the siren mast as sources, baked asynchronously.</summary>
        static void Patch()
        {
            _patched = true;
            try
            {
                List<NavMeshBuildSource> src = new List<NavMeshBuildSource>();
                src.Add(BoxSource(new Vector3(8.125f, TowerRoofCore.RoofY - 0.175f, 0f), new Vector3(8.05f, 0.35f, 8.5f)));
                for (int i = 0; i < TowerRoofCore.BagX.Length; i++)
                    src.Add(BoxSource(new Vector3(TowerRoofCore.BagX[i], TowerRoofCore.RoofY + TowerRoofCore.BagH * 0.5f, TowerRoofCore.BagZ[i]),
                        new Vector3(TowerRoofCore.BagSX[i], TowerRoofCore.BagH, TowerRoofCore.BagSZ[i])));
                src.Add(BoxSource(new Vector3(5.3f, 16.45f, -1.8f), new Vector3(0.2f, 2.2f, 0.2f)));     // siren mast C1_COL_492
                Bounds b = new Bounds(_centre, new Vector3(14f * K, 6f * K, 14f * K));
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
            _built = _roofNav = _patched = false;
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

        /// <summary>Two rails, a rung every 0.3 m, stand-off brackets to the
        /// wall; the rails run on 1.1 m over the roof as grab rails.</summary>
        static void LadderMesh(List<CombineInstance> p)
        {
            const float x = TowerRoofCore.LadderX, z = TowerRoofCore.LadderZ, w = TowerRoofCore.LadderHalfW;
            float y0 = _footY - 0.1f, y1 = TowerRoofCore.RoofY + TowerRoofCore.LadderOver;
            float h = y1 - y0;
            Box(p, new Vector3(x, y0 + h * 0.5f, z - w), new Vector3(0.06f, h, 0.06f));
            Box(p, new Vector3(x, y0 + h * 0.5f, z + w), new Vector3(0.06f, h, 0.06f));
            for (float y = _footY + TowerRoofCore.RungStep; y < TowerRoofCore.RoofY + 0.05f; y += TowerRoofCore.RungStep)
                Box(p, new Vector3(x, y, z), new Vector3(0.035f, 0.035f, w * 2f));
            // grab rails bend back to the roof's rail at their top
            Box(p, new Vector3((x + 12.05f) * 0.5f, y1, z - w), new Vector3(x - 12.05f + 0.06f, 0.06f, 0.06f));
            Box(p, new Vector3((x + 12.05f) * 0.5f, y1, z + w), new Vector3(x - 12.05f + 0.06f, 0.06f, 0.06f));
            // brackets: to the main block's wall, the cab wall, the roof's edge
            float[] bh = { 1.5f, 4.5f, 7.5f, 12.4f, 15.1f };
            float[] bx = { TowerRoofCore.WallX, TowerRoofCore.WallX, TowerRoofCore.WallX, 11.5f, 12.15f };
            for (int i = 0; i < bh.Length; i++)
            {
                float len = x - bx[i];
                Box(p, new Vector3(bx[i] + len * 0.5f, bh[i], z - w), new Vector3(len, 0.05f, 0.05f));
                Box(p, new Vector3(bx[i] + len * 0.5f, bh[i], z + w), new Vector3(len, 0.05f, 0.05f));
            }
        }

        /// <summary>Each wall three courses of staggered bags.</summary>
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
                go.AddComponent<BoxCollider>().size = new Vector3(TowerRoofCore.BagSX[i], TowerRoofCore.BagH, TowerRoofCore.BagSZ[i]) * K;
            }
        }

        /// <summary>The plain ladder group, wired like the content ladders.</summary>
        static void GameLadder(Transform root, string scene)
        {
            GameObject g = new GameObject("TowerRoofLadder");
            g.transform.SetParent(root, false);
            float h = TowerRoofCore.RoofY - _footY;
            Part(g.transform, "Ladder", new Vector3(TowerRoofCore.LadderX, _footY + h * 0.5f, TowerRoofCore.LadderZ),
                new Vector3(TowerRoofCore.ColW, h, TowerRoofCore.ColD));
            Part(g.transform, "ClimbDownPoint",
                new Vector3(TowerRoofCore.DownX, TowerRoofCore.RoofY + TowerRoofCore.DownH * 0.5f, TowerRoofCore.LadderZ),
                new Vector3(TowerRoofCore.DownW, TowerRoofCore.DownH, TowerRoofCore.DownD));
            Part(g.transform, "StartPoint", new Vector3(TowerRoofCore.StartX, _footY, TowerRoofCore.LadderZ), Vector3.zero);
            Part(g.transform, "BeupPoint", new Vector3(TowerRoofCore.BeupX, TowerRoofCore.RoofY - TowerRoofCore.BeupDrop, TowerRoofCore.LadderZ), Vector3.zero);
            Part(g.transform, "EndPoint", new Vector3(TowerRoofCore.EndX, TowerRoofCore.RoofY + 0.02f, TowerRoofCore.LadderZ), Vector3.zero);
            EastLadders.Wire(scene, g.transform);
        }

        static void Part(Transform g, string name, Vector3 centreM, Vector3 sizeM)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(g, false);
            go.transform.localPosition = centreM * K;
            if (sizeM != Vector3.zero) go.AddComponent<BoxCollider>().size = sizeM * K;
        }
    }
}

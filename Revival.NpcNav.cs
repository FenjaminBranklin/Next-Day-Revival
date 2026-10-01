// Runtime obstacle carving and fast stuck recovery; no authored bundles.
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class PhysicsNpcNavWorld : INpcNavWorld
    {
        internal static readonly PhysicsNpcNavWorld Instance = new PhysicsNpcNavWorld();
        internal readonly PhysicsVaultWorld Body = PhysicsVaultWorld.Instance;
        readonly NavMeshPath _path = new NavMeshPath();
        public bool Ray(Vector3 f, Vector3 d, float r, out Vector3 h) { return Body.Ray(f, d, r, out h); }
        public bool Obstacle(Vector3 f, Vector3 d, float r, out Vector3 h, out float top)
        { return Body.Obstacle(f, d, r, out h, out top); }
        public bool Landing(Vector3 near, out Vector3 feet) { return Body.Landing(near, out feet); }
        public bool Clear(Vector3 feet) { return Body.Clear(feet); }
        public bool Sweep(Vector3 from, Vector3 to) { return Body.Sweep(from, to); }
        public bool Connected(Vector3 from, Vector3 to)
        {
            NavMeshHit start, hit;
            // Reattach an agent just outside a polygon without projecting him
            // across a wall: the core independently sweeps the actual body.
            return NavMesh.SamplePosition(from, out start, 1.4f, Body.Area)
                && (start.position - from).sqrMagnitude <= NpcNavCore.ProgressSq
                && !NavMesh.Raycast(start.position, to, out hit, Body.Area);
        }
        public bool Route(Vector3 from, Vector3 via, Vector3 goal)
        {
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(goal, out hit, 1f, Body.Area)) return false;
            return NavMesh.CalculatePath(from, via, Body.Area, _path)
                && _path.status == NavMeshPathStatus.PathComplete
                && NavMesh.CalculatePath(via, hit.position, Body.Area, _path)
                && _path.status == NavMeshPathStatus.PathComplete;
        }
    }

    internal sealed class NpcNavCarveOwner : MonoBehaviour
    {
        internal Collider Source;
        internal NavMeshObstacle Obstacle;
        void OnDisable() { if (Obstacle != null) Obstacle.enabled = false; }
        void OnEnable() { if (Obstacle != null) Obstacle.enabled = Source != null && Source.enabled; }
        void OnDestroy() { if (Obstacle != null) Object.Destroy(Obstacle.gameObject); }
    }

    internal static class NpcNavObstacles
    {
        // Local discovery only while somebody uses navigation. One candidate
        // at 10 Hz, one nonalloc overlap at 2 Hz. Bounded cache includes rejects.
        static readonly Collider[] Nearby = new Collider[128], Known = new Collider[512];
        static readonly NavMeshObstacle[] Owned = new NavMeshObstacle[512];
        static readonly NpcNavCarveOwner[] Owners = new NpcNavCarveOwner[512];
        static readonly Bounds[] Boxes = new Bounds[512];
        static readonly Transform[] Sensors = new Transform[64];
        static readonly float[] SensorAt = new float[64];
        static int _sensorHead, _sensorCount;
        static int _count, _cursor, _replace, _maintain;
        static Vector3 _scanFeet;
        static float _nextScan, _nextWork;

        internal static void Step(Vector3 feet, Transform self, System.Type npcType, float now)
        {
            bool queued = false;
            for (int i = 0; i < _sensorCount; i++)
                if (object.ReferenceEquals(Sensors[(_sensorHead + i) % 64], self)) { queued = true; break; }
            if (!queued && _sensorCount < 64)
            { int at = (_sensorHead + _sensorCount) % 64; Sensors[at] = self; SensorAt[at] = now; _sensorCount++; }
            if (now < _nextWork) return;
            _nextWork = now + 0.1f;
            // Visit one actual carve, skipping empty/rejected cache slots. All
            // geometry/component work stays at 10 Hz across the whole module.
            int check = _maintain++ % Known.Length;
            for (int i = 0; i < Known.Length && object.ReferenceEquals(Owned[check], null); i++)
                check = _maintain++ % Known.Length;
            Collider old = Known[check]; NavMeshObstacle obstacle = Owned[check];
            if (obstacle != null && (old == null || !old.enabled
                || (old.bounds.center - Boxes[check].center).sqrMagnitude > 0.01f
                || (old.bounds.size - Boxes[check].size).sqrMagnitude > 0.01f))
            { Forget(check); }
            if (_cursor >= _count && now >= _nextScan)
            {
                while (_sensorCount > 0 && (Sensors[_sensorHead] == null || now - SensorAt[_sensorHead] > 5f))
                { Sensors[_sensorHead] = null; _sensorHead = (_sensorHead + 1) % 64; _sensorCount--; }
                if (_sensorCount == 0) return;
                Transform sensor = Sensors[_sensorHead];
                Sensors[_sensorHead] = null; _sensorHead = (_sensorHead + 1) % 64; _sensorCount--;
                feet = sensor.position;
                _scanFeet = feet;
                _nextScan = now + 0.5f;
                _count = Physics.OverlapSphereNonAlloc(feet + Vector3.up * 2f, 14f,
                    Nearby, ~0, QueryTriggerInteraction.Ignore); _cursor = 0;
                // Saturation still discovers bounded candidates. It never
                // authorizes movement; the safety planner fails closed.
            }
            Collider c = null;
            feet = _scanFeet;
            // Cheap already-seen/terrain/body rejects do not spend one tenth
            // of a second each in a dense gun pit. Only one new classification.
            for (int candidate = 0; candidate < 16 && _cursor < _count; candidate++)
            {
                Collider near = Nearby[_cursor++];
                if (near == null || !near.enabled || near.isTrigger || near is TerrainCollider
                    || near is CharacterController || near.attachedRigidbody != null) continue;
                bool known = false;
                for (int i = 0; i < Known.Length; i++)
                    if (object.ReferenceEquals(Known[i], near)) { known = true; break; }
                if (known) continue;
                c = near; break;
            }
            if (c == null || c.transform.IsChildOf(self)) return;
            int slot = _replace++ % Known.Length;
            Forget(slot);
            Known[slot] = c; Owned[slot] = null;
            Bounds box = c.bounds; Boxes[slot] = box;
            // Floors/terrain remain walkable; people and movable vehicles are
            // handled by native avoidance and ALL-collider clearance instead.
            // Low solids already have T2 vault probes. Carving them while an
            // actor touches the lip could remove his takeoff NavMesh polygon.
            if (c is TerrainCollider || c is CharacterController || c.attachedRigidbody != null
                || box.max.y < feet.y + NpcVaultCore.Hip || box.min.y > feet.y + NpcVaultCore.Height
                || box.size.x > 28f || box.size.z > 28f || box.size.y > 20f
                || c.GetComponentInParent<CharacterController>() != null
                || (npcType != null && c.GetComponentInParent(npcType) != null)
                || c.GetComponentInParent<NavMeshObstacle>() != null) return;
            GameObject go = new GameObject("NpcNavCarve");
            go.transform.position = box.center; go.transform.rotation = Quaternion.identity;
            // Unparented unit scale preserves the exact conservative world
            // AABB even under rotated, nonuniformly scaled prefab hierarchies.
            NavMeshObstacle made = go.AddComponent<NavMeshObstacle>();
            made.shape = NavMeshObstacleShape.Box;
            made.center = Vector3.zero; made.size = box.size;
            made.carving = true; made.carveOnlyStationary = true;
            made.carvingTimeToStationary = 0.1f; Owned[slot] = made;
            NpcNavCarveOwner owner = c.gameObject.AddComponent<NpcNavCarveOwner>();
            owner.Source = c; owner.Obstacle = made; Owners[slot] = owner;
        }

        static void Forget(int slot)
        {
            if (Owned[slot] != null) Object.Destroy(Owned[slot].gameObject);
            if (Owners[slot] != null) Object.Destroy(Owners[slot]);
            Owned[slot] = null; Owners[slot] = null; Known[slot] = null;
        }
    }

    public static partial class NpcWar
    {
        static readonly Fighter[] _navQueue = new Fighter[64];
        static readonly float[] _navQueuedAt = new float[64];
        static int _navHead, _navCount;
        static float _navBudgetAt;

        static bool NavigationEligible(Fighter f, float now)
        { return f.Squad != null && f.Squad.Merc != null || f.Target != null && now - f.LastSeen < 8f; }

        static bool NavTurn(Fighter f, float now)
        {
            while (_navCount > 0 && now - _navQueuedAt[_navHead] > 1.5f)
            { _navQueue[_navHead] = null; _navHead = (_navHead + 1) % 64; _navCount--; }
            bool queued = false;
            for (int i = 0; i < _navCount; i++) if (_navQueue[(_navHead + i) % 64] == f) { queued = true; break; }
            if (!queued && _navCount < 64)
            { int at = (_navHead + _navCount) % 64; _navQueue[at] = f; _navQueuedAt[at] = now; _navCount++; }
            if (_navCount == 0 || _navQueue[_navHead] != f || now < _navBudgetAt) return false;
            _navQueue[_navHead] = null; _navHead = (_navHead + 1) % 64; _navCount--;
            _navBudgetAt = now + 0.1f; return true;
        }

        static Vector3 NavDestination(Fighter f, Vector3 goal, float now)
        { return f.Navigation.Destination(goal, f.Tr.position, now); }

        static void NavigationStep(Fighter f, float now)
        {
            FrameProf.S(FrameProf.S_NpcNavT);
            try
            {
                bool moving = NavigationEligible(f, now) && f.HasOrder
                    && (f.WantMain == MainRun || f.WantMain == MainWalk)
                    && !Reloading(f) && !TowerRoof.Climbing(f.Tr)
                    && NpcNavCore.FlatSq(f.Ordered - f.Tr.position) > 4f;
                int action = f.Navigation.Observe(f.Tr.position, moving, now);
                if (!moving || now < f.Navigation.NextSample) return;
                f.Navigation.NextSample = now + 0.25f;
                NpcNavObstacles.Step(f.Tr.position, f.Tr, _npcType, now);
                // Detour completion resumes the original tactical point. A
                // fresh goal cancels it in both native movement issue seams.
                bool wasDetouring = f.Navigation.Detouring;
                NavDestination(f, f.Ordered, now);
                if (wasDetouring && !f.Navigation.Detouring) Retarget(f, f.Ordered, now);
                if (action == 0 || !NavTurn(f, now)) return;
                NavMeshAgent agent = Agent(f);
                if (agent == null || !agent.isActiveAndEnabled || (agent.isOnNavMesh && agent.pathPending)) return;
                PhysicsNpcNavWorld world = PhysicsNpcNavWorld.Instance;
                world.Body.Ignore = f.Tr; world.Body.Area = agent.areaMask;
                Vector3 preferred = Vector3.zero; bool hasPreferred = false;
                if (f.Squad != null && f.Squad.Merc != null)
                {
                    MercSense sense = f.Squad.Merc.Sense;
                    hasPreferred = sense.Pick.Found && sense.Pick.Confirmed && now - sense.PickAt < 2f;
                    preferred = sense.Pick.Point.Pos;
                }
                Vector3 via;
                bool found = NpcNavCore.Plan(world, f.Tr.position, f.Ordered, preferred,
                    hasPreferred, action == 2, f.Navigation.Stage, out via);
                f.Navigation.Tried(action, f.Tr.position, now);
                if (action == 1)
                {
                    if (found)
                    {
                        f.Navigation.Via = via; f.Navigation.Goal = f.Ordered;
                        f.Navigation.Until = now + 3f; f.Navigation.Detouring = true;
                    }
                    // Even with no physical detour, immediately replan the
                    // native path after carving. Do not stop or erase the order.
                    if (agent.isOnNavMesh) Retarget(f, f.Ordered, now);
                }
                else if (found && agent.Warp(via))
                {
                    f.Navigation.Detouring = false; f.Navigation.Watching = false;
                    f.HasOrder = false; f.NextMove = 0f;
                    f.LastPos = via; f.MovedAt = now;
                }
                // Never return early from combat: acquire/fire/cover/retreat
                // continue in their existing loop, with the profile weapon.
            }
            finally { FrameProf.E(FrameProf.S_NpcNavT); }
        }
    }
}

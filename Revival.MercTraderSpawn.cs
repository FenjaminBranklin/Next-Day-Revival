// H S1: physical ground, counter clearance and a connected outdoor escape.
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class MercTraderSpawnWorld : IMercSpawnWorld
    {
        readonly RaycastHit[] _hits = new RaycastHit[32];
        readonly Collider[] _overlaps = new Collider[32];
        readonly NavMeshPath _path = new NavMeshPath();
        internal Transform Buyer;

        internal static Vector3 Vector(MercSpawnPoint p) { return new Vector3(p.X, p.Y, p.Z); }
        internal static MercSpawnPoint Point(Vector3 p) { return new MercSpawnPoint(p.x, p.y, p.z); }

        public bool Ground(MercSpawnPoint near, out MercSpawnPoint feet)
        {
            feet = near;
            Vector3 p = Vector(near);
            // Local physics floor first; a broad terrain/nav snap can cross a
            // wall, pick the back of a counter or project onto an upstairs room.
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.25f, Vector3.down,
                _hits, 3.75f, ~0, QueryTriggerInteraction.Ignore);
            if (n == _hits.Length) return false;
            float distance = float.MaxValue; int best = -1;
            for (int i = 0; i < n; i++)
            {
                Collider c = _hits[i].collider;
                if (c == null || c.attachedRigidbody != null
                    || (Buyer != null && c.transform.IsChildOf(Buyer))) continue;
                if (_hits[i].distance < distance) { distance = _hits[i].distance; best = i; }
            }
            if (best < 0 || _hits[best].normal.y < 0.7f) return false;
            NavMeshHit nav;
            Vector3 surface = _hits[best].point;
            if (!NavMesh.SamplePosition(surface, out nav, 0.6f, NavMesh.AllAreas)
                || (nav.position - surface).sqrMagnitude > 0.36f
                || Mathf.Abs(nav.position.y - surface.y) > 0.25f) return false;
            feet = Point(nav.position); return true;
        }

        public bool Clear(MercSpawnPoint feet)
        {
            Vector3 p = Vector(feet);
            // Native NPC capsule: 5 units tall, radius .75 (NpcCombat).
            int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * 0.87f,
                p + Vector3.up * 4.25f, 0.75f, _overlaps, ~0, QueryTriggerInteraction.Ignore);
            // Include the buyer and existing mercs: no overlapping bodies.
            return n == 0;
        }

        public bool Outside(MercSpawnPoint feet)
        {
            Vector3 p = Vector(feet);
            // Terrain under a thin paving/floor layer proves ground level.
            // Use physics buffers: TerrainHeight's reflection boxes/arrays
            // would allocate for every candidate on an Update spawn tick.
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.25f, Vector3.down,
                _hits, 1.75f, ~0, QueryTriggerInteraction.Ignore);
            if (n == _hits.Length) return false;
            bool terrain = false;
            for (int i = 0; i < n; i++)
                if (_hits[i].collider is TerrainCollider) { terrain = true; break; }
            if (!terrain) return false;
            // Conservative open-sky proof excludes rooms, counters, roofs and
            // covered porches. Another ray may find open ground under trees.
            // Start near the floor, so even a roof just above head height is
            // seen rather than skipped by a ray starting above the capsule.
            return Physics.RaycastNonAlloc(p + Vector3.up * 0.25f, Vector3.up,
                _hits, 100f, ~0, QueryTriggerInteraction.Ignore) == 0;
        }

        public bool Route(MercSpawnPoint from, MercSpawnPoint to)
        {
            return NavMesh.CalculatePath(Vector(from), Vector(to), NavMesh.AllAreas, _path)
                && _path.status == NavMeshPathStatus.PathComplete;
        }
    }

    internal static class MercTraderSpawn
    {
        // One-time storage, not arrays/path/corners allocated by Update.
        static readonly MercTraderSpawnWorld World = new MercTraderSpawnWorld();

        internal static bool TryPick(ref MercTraderSpawnSearch search, Vector3 trader, Vector3 buyer,
            Transform owner, int occupied, out Vector3 feet, out bool pending)
        {
            feet = trader; pending = false;
            World.Buyer = owner;
            try
            {
                if (!search.Started && !search.Begin(World, MercTraderSpawnWorld.Point(trader),
                    MercTraderSpawnWorld.Point(buyer), occupied)) return false;
                MercSpawnPoint point;
                MercSpawnSearchResult result = search.Step(World, out point);
                pending = result == MercSpawnSearchResult.Pending;
                if (result != MercSpawnSearchResult.Found) return false;
                feet = MercTraderSpawnWorld.Vector(point); return true;
            }
            finally { World.Buyer = null; }
        }
    }
}

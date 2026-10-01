// Runtime geometry and movement seam below M1 cover/M2-M3 fight/orders.
using UnityEngine;
using UnityEngine.AI;

namespace NextDayRevival
{
    internal sealed class PhysicsVaultWorld : IVaultWorld
    {
        internal static readonly PhysicsVaultWorld Instance = new PhysicsVaultWorld();
        readonly RaycastHit[] _hits = new RaycastHit[32];
        readonly Collider[] _overlaps = new Collider[64];
        internal Transform Ignore;
        internal int Area = NavMesh.AllAreas;
        Collider _rayCollider;
        bool Own(Collider c) { return c == null || (Ignore != null && c.transform.IsChildOf(Ignore)); }
        public bool Ray(Vector3 from, Vector3 direction, float distance, out Vector3 hit)
        {
            hit = Vector3.zero;
            _rayCollider = null;
            int n = Physics.RaycastNonAlloc(from, direction, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
            // A saturated buffer cannot prove geometry is clear.
            if (n == _hits.Length) { hit = from; return true; }
            float best = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
                if (!Own(_hits[i].collider) && _hits[i].distance < best)
                { best = _hits[i].distance; hit = _hits[i].point; _rayCollider = _hits[i].collider; found = true; }
            return found;
        }
        public bool Obstacle(Vector3 from, Vector3 direction, float distance, out Vector3 hit, out float top)
        {
            top = 0f;
            if (!Ray(from, direction, distance, out hit) || _rayCollider == null
                || _rayCollider.attachedRigidbody != null) return false;
            // Bounds also work for a thin mesh fence without a horizontal top
            // face. The high ray/entire capsule still test ALL neighbouring
            // solids. Conservatively reject a collider with a taller portion.
            top = _rayCollider.bounds.max.y; return true;
        }
        public bool Landing(Vector3 near, out Vector3 feet)
        {
            feet = near; Vector3 surface;
            if (!Ray(near + Vector3.up * 1.1f, -Vector3.up, 2.5f, out surface)) return false;
            NavMeshHit nav;
            if (!NavMesh.SamplePosition(surface, out nav, 0.45f, Area)
                || (nav.position - surface).sqrMagnitude > 0.2025f) return false;
            feet = nav.position; return true;
        }
        public bool Clear(Vector3 feet)
        {
            int n = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * (NpcVaultCore.Radius + 0.12f),
                feet + Vector3.up * (NpcVaultCore.Height - NpcVaultCore.Radius),
                NpcVaultCore.Radius, _overlaps, ~0, QueryTriggerInteraction.Ignore);
            if (n == _overlaps.Length) return false;
            for (int i = 0; i < n; i++) if (!Own(_overlaps[i])) return false;
            return true;
        }
        public bool Sweep(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; float length = delta.magnitude;
            if (length < 0.001f) return Clear(to);
            int n = Physics.CapsuleCastNonAlloc(from + Vector3.up * (NpcVaultCore.Radius + 0.12f),
                from + Vector3.up * (NpcVaultCore.Height - NpcVaultCore.Radius), NpcVaultCore.Radius,
                delta / length, _hits, length, ~0, QueryTriggerInteraction.Ignore);
            if (n == _hits.Length) return false;
            for (int i = 0; i < n; i++) if (!Own(_hits[i].collider)) return false;
            return Clear(to);
        }
    }

    public static partial class NpcWar
    {
        static float _vaultBudgetAt;
        static readonly Fighter[] _vaultQueue = new Fighter[64];
        static readonly float[] _vaultQueuedAt = new float[64];
        static int _vaultHead, _vaultCount;

        static bool VaultTurn(Fighter f, float now)
        {
            // FIFO prevents the first three movers consuming every probe when
            // six mercs all reach a pit at once. Stale/dead/sleeping entries
            // expire; no scene scan, collections or per-frame allocations.
            while (_vaultCount > 0 && now - _vaultQueuedAt[_vaultHead] > 1.5f)
            {
                _vaultQueue[_vaultHead] = null;
                _vaultHead = (_vaultHead + 1) % _vaultQueue.Length; _vaultCount--;
            }
            bool queued = false;
            for (int i = 0; i < _vaultCount; i++)
                if (_vaultQueue[(_vaultHead + i) % _vaultQueue.Length] == f) { queued = true; break; }
            if (!queued && _vaultCount < _vaultQueue.Length)
            {
                int at = (_vaultHead + _vaultCount) % _vaultQueue.Length;
                _vaultQueue[at] = f; _vaultQueuedAt[at] = now; _vaultCount++;
            }
            if (_vaultCount == 0 || _vaultQueue[_vaultHead] != f || now < _vaultBudgetAt) return false;
            _vaultQueue[_vaultHead] = null;
            _vaultHead = (_vaultHead + 1) % _vaultQueue.Length; _vaultCount--;
            _vaultBudgetAt = now + 0.1f;
            return true;
        }
        internal static bool VaultAlive(Component ai) { return Alive(ai); }

        static bool VaultStep(Fighter f, float now)
        {
            FrameProf.S(FrameProf.S_NpcVaultT);
            try
            {
                if (f.Vault != null)
                {
                    if (f.Vault.Busy) return true;
                    if (f.Vault.Finished)
                    {
                        f.Vault.Finished = false; f.HasOrder = false; f.NextMove = 0f;
                        f.NextState = 0f; f.WantMain = -1; f.MovedAt = now;
                        f.LastPos = f.Tr.position; f.NextVault = now + NpcVaultCore.Cooldown;
                    }
                }
                // Calm ordinary NPCs do not probe; all merc orders may vault.
                bool merc = f.Squad != null && f.Squad.Merc != null;
                if ((!merc && (f.Target == null || now - f.LastSeen > 8f))
                    || !f.HasOrder || (f.WantMain != MainRun && f.WantMain != MainWalk)
                    || Reloading(f) || now < f.NextVault || !VaultTurn(f, now)) return false;
                f.NextVault = now + NpcVaultCore.Interval;
                NavMeshAgent agent = f.Vault == null ? Agent(f) : f.Vault.Agent;
                if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh
                    || agent.pathPending || TowerRoof.Climbing(f.Tr)) return false;
                PhysicsVaultWorld world = PhysicsVaultWorld.Instance;
                world.Ignore = f.Tr; world.Area = agent.areaMask;
                VaultPlan plan;
                // Final order, not a NavMesh corner that steers away from an
                // unlinked sandbag. M1/fight already chose this destination.
                if (!NpcVaultCore.Plan(world, f.Tr.position, f.Ordered, out plan)) return false;
                if (f.Vault == null) f.Vault = NpcVaultMotion.Of(f.Ai, agent);
                ReleaseAim(f);
                Drive(f, MainIdle, AddNone, PoseStand, now, true);
                f.PlantedSince = 0f;
                f.Vault.Begin(plan, now);
                return true;
            }
            finally { FrameProf.E(FrameProf.S_NpcVaultT); }
        }
    }
}

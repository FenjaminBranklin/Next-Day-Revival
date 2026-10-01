// Z T2: low-obstacle traversal. Units are 2.8 per metre. C# 3.0, ASCII.
using UnityEngine;

namespace NextDayRevival
{
    internal interface IVaultWorld
    {
        bool Ray(Vector3 from, Vector3 direction, float distance, out Vector3 hit);
        bool Obstacle(Vector3 from, Vector3 direction, float distance, out Vector3 hit, out float top);
        bool Landing(Vector3 near, out Vector3 feet);
        bool Clear(Vector3 feet);
        bool Sweep(Vector3 from, Vector3 to);
    }

    internal struct VaultPlan
    {
        public Vector3 Start, End;
        public float Lift, Duration;
        public bool RecoveryOnly;
    }

    internal static class NpcVaultCore
    {
        public const float Hip = 3.08f; // 1.10 m, excludes chest-high walls
        public const float Radius = 0.70f, Height = 4.76f;
        public const float Reach = 3.2f, MaxSpan = 7.0f;
        public const float Interval = 0.25f, Cooldown = 1.0f;

        // Rise before crossing the lip, travel over it, then land. A flat
        // parabola from feet already touching sandbags would intersect them.
        public static Vector3 At(VaultPlan p, float elapsed)
        {
            float t = Mathf.Clamp01(elapsed / p.Duration);
            float across = Mathf.Clamp01((t - 0.2f) / 0.6f);
            across = across * across * (3f - 2f * across);
            float up = t < 0.2f ? t / 0.2f : t > 0.8f ? (1f - t) / 0.2f : 1f;
            up = Mathf.Clamp01(up); up = up * up * (3f - 2f * up);
            return p.Start + (p.End - p.Start) * across + Vector3.up * (p.Lift * up);
        }

        public static bool Plan(IVaultWorld world, Vector3 start, Vector3 goal, out VaultPlan plan)
        {
            plan = new VaultPlan();
            Vector3 dir = goal - start; dir.y = 0f;
            float distance = dir.magnitude;
            if (distance < 2.0f) return false;
            dir = dir / distance;
            Vector3 low, high; float top;
            if (!world.Obstacle(start + Vector3.up * 0.8f, dir, Mathf.Min(Reach, distance), out low, out top)) return false;
            float lip = (low - start).x * dir.x + (low - start).z * dir.z;
            // Includes every collider above the obstacle, not just its box.
            if (world.Ray(start + Vector3.up * (Hip + 0.15f), dir,
                Mathf.Min(lip + 4f, distance), out high)) return false;
            float height = top - start.y;
            if (height < 0.35f || height > Hip) return false;
            for (int candidate = 0; candidate < 2; candidate++)
            {
                float span = lip + (candidate == 0 ? 2.0f : 3.8f);
                if (span > MaxSpan || span > distance + 0.5f) continue;
                Vector3 end;
                if (!world.Landing(start + dir * span, out end)
                    || Mathf.Abs(end.y - start.y) > 1.0f || !world.Clear(end)) continue;
                VaultPlan p = new VaultPlan(); p.Start = start; p.End = end;
                p.Lift = height + 0.35f; p.Duration = 0.72f;
                Vector3 previous = start; bool clear = true;
                for (int segment = 1; segment <= 10; segment++)
                {
                    Vector3 at = At(p, p.Duration * segment / 10f);
                    if (!world.Sweep(previous, at))
                    {
                        // A man already slightly inside the low lip cannot
                        // start a collision-free takeoff. Prove the raised
                        // crossing and descent, then use the same safe landing
                        // recovery as a failed jump. Never waive a ceiling or
                        // another obstruction above the trapped feet.
                        Vector3 raised = start + Vector3.up * p.Lift;
                        if (segment == 1 && !world.Clear(start) && world.Clear(raised))
                        { p.RecoveryOnly = true; previous = raised; segment = 2; continue; }
                        clear = false; break;
                    }
                    previous = at;
                }
                if (!clear) continue;
                plan = p; return true;
            }
            return false;
        }

        // Only a previously proven crossing can use the short teleport. Never
        // fall through a slab, teleport through a tall wall or into a body.
        public static bool Recover(IVaultWorld world, VaultPlan p, out Vector3 end)
        {
            Vector3 ground;
            if (world.Landing(p.End, out ground) && (ground - p.End).sqrMagnitude < 0.25f
                && world.Clear(ground)) { end = ground; return true; }
            if (world.Landing(p.Start, out ground) && (ground - p.Start).sqrMagnitude < 0.25f
                && world.Clear(ground)) { end = ground; return true; }
            end = p.Start; return false;
        }
    }
}

// Z K2: bounded local detours beneath orders and the M1/M2/M3 combat loop.
using UnityEngine;

namespace NextDayRevival
{
    internal interface INpcNavWorld : IVaultWorld
    {
        bool Route(Vector3 from, Vector3 via, Vector3 goal);
        bool Connected(Vector3 from, Vector3 to);
    }

    internal struct NpcNavState
    {
        internal Vector3 Anchor, Via, Goal;
        internal float Since, NextSample, Until;
        internal int Stage;
        internal bool Watching, Detouring;

        internal int Observe(Vector3 feet, bool moving, float now)
        {
            if (!moving)
            { Watching = false; Stage = 0; Detouring = false; return 0; }
            if (!Watching || NpcNavCore.FlatSq(feet - Anchor) >= NpcNavCore.ProgressSq)
            { Watching = true; Anchor = feet; Since = now; Stage = 0; }
            return now - Since >= NpcNavCore.Window ? (Stage == 0 ? 1 : 2) : 0;
        }

        internal void Tried(int action, Vector3 feet, float now)
        { Anchor = feet; Since = now; Stage = action; }

        internal Vector3 Destination(Vector3 goal, Vector3 feet, float now)
        {
            if (Detouring && (now >= Until || NpcNavCore.FlatSq(feet - Via) < 1f
                || NpcNavCore.FlatSq(goal - Goal) > NpcNavCore.ProgressSq)) Detouring = false;
            return Detouring ? Via : goal;
        }
    }

    internal static class NpcNavCore
    {
        internal const float Window = 1.5f, ProgressSq = 1.4f * 1.4f; // 0.5 m at 2.8 u/m
        internal static float FlatSq(Vector3 d) { return d.x * d.x + d.z * d.z; }

        // ALL body/ground/sweep checks use the T2 geometry adapter. No warp
        // across a blocked mesh edge or wall, even if its far endpoint is free.
        internal static bool Plan(INpcNavWorld world, Vector3 feet, Vector3 goal,
            Vector3 preferred, bool hasPreferred, bool nudge, int attempt, out Vector3 via)
        {
            via = feet;
            Vector3 dir = goal - feet; dir.y = 0f;
            if (dir.sqrMagnitude < 4f) return false;
            dir = dir / dir.magnitude;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            float sign = (attempt & 1) == 0 ? 1f : -1f;
            for (int i = 0; i < 5; i++)
            {
                Vector3 near;
                if (i == 0)
                {
                    if (!hasPreferred || nudge) continue;
                    near = preferred;
                }
                else
                {
                    float radius = nudge ? 2.8f : 8.4f; // 1 m escape, 3 m detour
                    float lateral = (i == 1 || i == 3) ? sign : -sign;
                    near = feet + side * (radius * lateral);
                    if (i >= 3) near -= dir * (radius * 0.5f);
                }
                Vector3 end;
                float maxSq = nudge ? 17.64f : 141.12f; // max warp 1.5 m
                if (FlatSq(near - feet) > maxSq || FlatSq(near - feet) < ProgressSq
                    || !world.Landing(near, out end) || FlatSq(end - feet) > maxSq
                    || Mathf.Abs(end.y - feet.y) > 0.8f || !world.Clear(end)
                    || !world.Sweep(feet, end)) continue;
                if (nudge ? !world.Connected(feet, end) : !world.Route(feet, end, goal)) continue;
                via = end; return true;
            }
            return false;
        }
    }
}

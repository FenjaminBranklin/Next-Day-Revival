// Z M5a: reuse patrol road points and the measured RCC input law, without
// patrol spawn, free fuel, world-prop ghosting or waypoint warps.
using System.Collections.Generic;
using UnityEngine;

namespace NextDayRevival
{
    public static partial class Patrol
    {
        internal static void MercRoad(Vector3 from, Vector3 to, List<Vector3> path)
        {
            path.Clear(); path.Add(from);
            Route best = null; int start = 0, end = 0;
            float score = MercDriveRun.Flat(to - from) * 1.8f;
            foreach (Route r in _routes.Values)
            {
                if (!r.Here || r.Site || r.P.Count < 2) continue;
                int a = 0, b = 0; float da = float.MaxValue, db = float.MaxValue;
                for (int i = 0; i < r.P.Count; i++)
                {
                    float x = MercDriveRun.Flat(r.P[i].Pos - from), y = MercDriveRun.Flat(r.P[i].Pos - to);
                    if (x < da) { a = i; da = x; }
                    if (y < db) { b = i; db = y; }
                }
                // Use a recorded road only when both ends are reasonably near
                // it. A map point off the road keeps its exact final connector.
                if (a == b || da > 168f || db > 168f || Mathf.Abs(r.P[a].Pos.y - from.y) > 28f
                    || Mathf.Abs(r.P[b].Pos.y - to.y) > 28f) continue;
                float length = da + db; int step = a < b ? 1 : -1;
                for (int i = a; i != b; i += step) length += MercDriveRun.Flat(r.P[i + step].Pos - r.P[i].Pos);
                if (length < score) { best = r; start = a; end = b; score = length; }
            }
            if (best != null)
            {
                int step = start < end ? 1 : -1;
                for (int i = start; ; i += step)
                {
                    Vector3 p = best.P[i].Pos;
                    if (MercDriveRun.Flat(p - path[path.Count - 1]) > 5.6f) path.Add(p);
                    if (i == end) break;
                }
            }
            path.Add(to);
        }

        internal static void MercInputs(float angle, float speed, float remaining,
            float front, float left, float right, bool reverse, bool rearFree, int attempt,
            out float steer, out float gas, out float brake)
        {
            steer = Mathf.Clamp(angle / FullLockAt, -1f, 1f);
            float want = Mathf.Min(36f, Mathf.Max(0f, (remaining - 3f) * 2.4f));
            want *= Mathf.Clamp(1f - Mathf.Abs(angle) / 100f, 0.2f, 1f);
            if (front < 16f) want = Mathf.Min(want, Mathf.Max(0f, (front - 3f) * 2f));
            if (front < 20f)
            {
                float side = left > right + 1f ? -1f : right > left + 1f ? 1f : (attempt & 1) == 0 ? 1f : -1f;
                steer = side * 0.9f;
            }
            Throttle(want, Mathf.Abs(speed) * 3.6f, out gas, out brake);
            if (remaining <= 5.6f) { gas = 0f; brake = 1f; }
            else if (Mathf.Abs(speed) * 3.6f < CoastBelow && want > 1f && brake > 0f) brake = 0f;
            if (reverse)
            {
                steer = -steer;
                gas = 0f; brake = rearFree ? 0.65f : 1f;
                // RCC autoReverse interprets brake under walking speed as reverse.
            }
        }
    }
}

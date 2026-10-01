// Z K8: bounded raid windows and overhead shelter queries. C# 3.0.
using UnityEngine;

namespace NextDayRevival
{
    internal interface IMercRaidProtection
    {
        bool Revetment(Vector3 at);
    }

    internal sealed class MercRaidClock
    {
        internal const float Radius = 1260f; // 450 m
        readonly Vector3[] _at = new Vector3[8];
        readonly float[] _until = new float[8];

        internal void Clear()
        {
            for (int i = 0; i < _until.Length; i++) _until[i] = 0f;
        }

        internal void Warn(Vector3 at, float now, float seconds)
        {
            if (float.IsNaN(at.x) || float.IsNaN(at.z) || float.IsInfinity(at.x)
                || float.IsInfinity(at.z) || float.IsNaN(seconds) || seconds <= 0f) return;
            int slot = 0;
            for (int i = 0; i < _until.Length; i++)
            {
                Vector3 d = at - _at[i]; d.y = 0f;
                if (_until[i] > now && d.sqrMagnitude < 28f * 28f) { slot = i; break; }
                if (_until[i] < _until[slot]) slot = i;
            }
            _at[slot] = at;
            _until[slot] = Mathf.Max(_until[slot], now + Mathf.Clamp(seconds, 1f, 400f));
        }

        internal float Until(Vector3 at, float now)
        {
            float until = 0f;
            for (int i = 0; i < _until.Length; i++)
            {
                Vector3 d = at - _at[i]; d.y = 0f;
                if (_until[i] > now && d.sqrMagnitude <= Radius * Radius)
                    until = Mathf.Max(until, _until[i]);
            }
            return until;
        }
    }

    internal static class MercRaidShelter
    {
        internal static bool Protected(ICoverWorld world, Vector3 at)
        {
            IMercRaidProtection protection = world as IMercRaidProtection;
            return (protection != null && protection.Revetment(at)) || Roof(world, at);
        }

        // All collider kinds participate through M1's world adapter. Five
        // upward rays require a solid ceiling over the crouched body footprint;
        // people, triggers and moving vehicles cannot masquerade as a shelter.
        internal static bool Roof(ICoverWorld world, Vector3 at)
        {
            for (int i = 0; i < 5; i++)
            {
                Vector3 from = at + Vector3.up * 3f;
                if (i > 0) { from.x += i < 3 ? (i == 1 ? 1.4f : -1.4f) : 0f;
                    from.z += i >= 3 ? (i == 3 ? 1.4f : -1.4f) : 0f; }
                Vector3 hit, normal; bool moving;
                if (!world.Cast(from, Vector3.up, 56f, out hit, out normal, out moving)
                    || moving || normal.y > -0.5f) return false;
            }
            return true;
        }

        internal static bool Pick(CoverField field, ICoverWorld world, Vector3 from,
            int who, float now, ref int cursor, out CoverPick pick)
        {
            pick = new CoverPick();
            // Visit every cached candidate over successive slices. A nearby
            // open sandbag must not hide a slightly further roof indefinitely.
            int cx = CoverField.CellOf(from.x), cz = CoverField.CellOf(from.z);
            int ordinal = 0, tested = 0;
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    CoverCell cell = field.Cell(x, z);
                    if (cell == null) continue;
                    for (int n = 0; n < cell.Count; n++)
                    {
                        if (ordinal++ < cursor) continue;
                        cursor = ordinal;
                        CoverPoint p = cell.Points[n]; Vector3 d = p.Pos - from;
                        if (p.Dynamic || p.BadUntil > now || Mathf.Abs(d.y) > 5f
                            || d.sqrMagnitude > 40f * 40f || field.Claimed(p.Pos, who, now)) continue;
                        bool roof = Protected(world, p.Pos);
                        if (roof)
                        {
                            pick.Found = true; pick.Confirmed = true;
                            pick.Key = cell.Key; pick.Index = n; pick.Point = p;
                            field.Claim(who, p.Pos, now + 6f, now);
                            return true;
                        }
                        if (++tested == 3) return false;
                    }
                }
            cursor = 0;
            return false;
        }
    }
}

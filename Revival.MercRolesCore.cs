// Y S3: weapon roles and cached owner-relative overwatch. C# 3.0, ASCII.
using UnityEngine;

namespace NextDayRevival
{
    internal static class MercRole
    {
        internal const byte Assault = 0, Marksman = 1;
        internal const float Metre = 2.8f, Near = 60f * Metre, Far = 150f * Metre;
        internal const float CoverRadius = 28f, QueryEvery = 0.5f, BlindAfter = 3f;

        internal static byte Of(int weapon)
        {
            return MercWeaponReach.Kind(weapon) == MercWeaponReach.Marksman ? Marksman : Assault;
        }

        internal static Vector3 Front(Vector3 front)
        {
            front.y = 0f;
            return front.sqrMagnitude < 0.01f ? Vector3.forward : front.normalized;
        }

        internal static Vector3 Slot(byte role, Vector3 owner, Vector3 front, int slot, int lane)
        {
            front = Front(front);
            Vector3 side = new Vector3(front.z, 0f, -front.x);
            float sign = (slot & 1) == 0 ? -1f : 1f;
            int rank = slot / 2;
            if (role != Marksman)
                return owner + front * ((4f + 2f * rank) * Metre)
                    + side * (sign * (3f + 2f * rank) * Metre);
            // Five behind/flank alternatives. Even with a 10 m cover offset,
            // every slot stays inside 60..150 m (including the 10-merc cap).
            float back = (80f + 8f * rank + (lane == 2 ? 10f : 0f)) * Metre;
            float flank = (20f + 3f * rank) * Metre;
            if (lane == 1 || lane == 4) sign = -sign;
            if (lane == 3 || lane == 4) { back -= 12f * Metre; flank += 30f * Metre; }
            return owner - front * back + side * (sign * flank);
        }

        internal static bool InBand(Vector3 me, Vector3 owner)
        {
            float dx = me.x - owner.x, dz = me.z - owner.z;
            float d = dx * dx + dz * dz;
            return d >= Near * Near && d <= Far * Far;
        }

        internal static bool InPosition(Vector3 me, Vector3 owner, Vector3 front)
        {
            return InBand(me, owner) && Vector3.Dot(me - owner, Front(front)) <= 0f;
        }

        internal static bool MayFight(byte role, Vector3 me, Vector3 owner, bool fighting)
        {
            if (role == Marksman) return InBand(me, owner);
            float leash = fighting ? 120f : 75f;
            float dx = me.x - owner.x, dz = me.z - owner.z;
            return dx * dx + dz * dz <= leash * leash;
        }

        // Protection changes HOW he regains the formation: M2/M3 first.
        internal static bool Reposition(byte role, bool protectedMove, bool inBand,
            bool sees, bool contact, float blindFor)
        {
            return role == Marksman && !protectedMove
                && (!inBand || (!sees && contact && blindFor >= BlindAfter));
        }
    }

    internal sealed class MercOverwatch
    {
        internal byte Role;
        internal int Lane;
        internal bool Ready, HasCover, Clear;
        internal Vector3 OwnerAt, Front, Goal, Watch;
        internal CoverPick Pick;
        internal float NextQuery, NextClaim;
        internal MercOrder For;

        // Arithmetic only. A command or owner motion drops the old goal now;
        // it never waits for the globally shared world-query budget.
        internal bool Update(MercOrder order, Vector3 owner, Vector3 front, int slot)
        {
            front = MercRole.Front(front);
            if (Ready && For == order && (owner - OwnerAt).sqrMagnitude <= 36f
                && Vector3.Dot(front, Front) >= 0.95f) return false;
            For = order; OwnerAt = owner; Front = front; Lane = 0; Ready = true;
            HasCover = false; Clear = false; NextQuery = 0f;
            Goal = MercRole.Slot(Role, owner, front, slot, Lane);
            return true;
        }

        internal void NextLane(int slot)
        {
            Lane = (Lane + 1) % 5;
            HasCover = false; Clear = false;
            Goal = MercRole.Slot(Role, OwnerAt, Front, slot, Lane);
        }
    }
}

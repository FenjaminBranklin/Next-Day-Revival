// Next Day: Survival - Revival Toolkit
//
// B S2a: WHERE ONE NPCWAR ROUND ENDS (docs/ai/tasks/b-s2a-longrange-damage.md).
//
// An NpcWar round (merc, heli squad, ground group, defender) is our own ray
// after the NPC's native FireTo, because the game's NPC shot has no NPC damage
// branch. Two things made that ray miss men it should hit at range:
//
//   1. The vanilla distance shutdown. NPC_Settlement.PlayersDistanceControll
//      measures the LOCAL player's body against CheckPlayersDistRadius (300 to
//      500 u = 107 to 179 m on every map settlement, level7) and outside it
//      calls NPC_AI2.SetPlayVisualizationValue(false): _colliderMain off and
//      PlayerRagdollController.RagdollCollidersActive(false) - every bone
//      collider off (CONFIRMED IL). The man is still alive, still a target,
//      every line-of-sight and muzzle ray passes him as open ground, and our
//      damage ray passes him too. A merc runs on his owner's client, so a
//      looter whose settlement centre is farther than its radius from the
//      OWNER took no round at all, however many magazines went out.
//   2. A man in the vanilla wounded state (MainState 7) lies on the ground.
//      He is IsAlive() and stays a target, but every NpcWar shot aimed at the
//      standing chest (3.3 u) and passed over him.
//
// This core answers, without physics: does the round enter the intended
// man's body before the first solid thing on its way? The adapter
// (Revival.NpcHit.cs) asks only when the physics cannot answer - his colliders
// are off on this client, or he lies wounded - so a fight the game can see
// keeps its real bone colliders and its balance.
//
// The body is the shape of Marauder_NPC_01's bone colliders (resources.assets,
// bind pose): shins and thighs r 0.23-0.27 at x +-0.37..0.45, hips box
// 2.86..3.47, chest box 3.62..4.45 (1.08 wide, 0.75 deep), head sphere r 0.30
// at 4.92. A vertical capsule 0.0..5.22, 1.0 wide covers that silhouette from
// the front and is a quarter wider than it from the side. The root capsule
// (5.0 x 0.75) is not the body: it is on layer 2 (Ignore Raycast).
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only. No allocation.
using UnityEngine;

namespace NextDayRevival
{
    internal static class NpcHitCore
    {
        /// <summary>The game's own bullet mask. PlayerFirearmWeaponController
        /// .FireOneShot IL_017A loads -437892092 and inverts it (CONFIRMED IL):
        /// a player's round passes Ignore Raycast 2, CharacterController 10 and
        /// 26, the local ragdoll 11, EditorElement 14, InteractOnly 17, every
        /// bush layer 18, 21-24 and 30, IgnoreLocalPlayer 29 and AllPlayerOnly
        /// 31. NpcWar used to stop at all of them, and at triggers.</summary>
        internal const int BulletMask = ~(-437892092);

        /// <summary>NPCMainState.Wounded (NPC_AI2.OnWoundedAction).</summary>
        internal const int MainWounded = 7;

        // Standing body: a capsule from StandFoot to StandTop, StandRadius wide.
        internal const float StandFoot = 0.5f;
        internal const float StandTop = 4.72f;
        internal const float StandRadius = 0.5f;

        // A wounded man: the torso about his transform, a hand's breadth up.
        // HYPOTHESIS: the lying clip's torso is within LyingRadius of the
        // transform; the clip itself was not measured.
        internal const float LyingCentre = 0.6f;
        internal const float LyingRadius = 1.0f;

        /// <summary>Where a shooter holds on a man lying wounded.</summary>
        internal const float LyingAim = 0.6f;

        /// <summary>Must the body be arithmetic on this client? Only when the
        /// physics cannot see him: his colliders are off (visualization off,
        /// the vanilla distance shutdown) or he lies wounded.</summary>
        internal static bool UseBody(bool visualized, int mainState)
        {
            return !visualized || mainState == MainWounded;
        }

        /// <summary>The height a shooter holds on: the lying torso for a
        /// wounded man, otherwise what his line-of-sight test found.</summary>
        internal static float AimHeight(int mainState, float standing)
        {
            return mainState == MainWounded ? LyingAim : standing;
        }

        /// <summary>Distance along the unit ray (ro, rd) at which it enters the
        /// body of a man whose transform stands at foot, or -1 when it does not
        /// within max. A ray that starts inside the body enters at 0.</summary>
        internal static float Body(Vector3 ro, Vector3 rd, Vector3 foot, bool lying, float max)
        {
            if (lying) return Sphere(ro, rd, foot + Vector3.up * LyingCentre, LyingRadius, max);
            return Capsule(ro, rd, foot + Vector3.up * StandFoot, foot + Vector3.up * StandTop,
                StandRadius, max);
        }

        /// <summary>Does the round end in the intended body? bodyAt: Body's
        /// answer (-1 = missed it); solidAt: distance of the first solid hit
        /// along the same ray that is not part of the man (-1 = none).</summary>
        internal static bool BodyFirst(float bodyAt, float solidAt)
        {
            return bodyAt >= 0f && (solidAt < 0f || bodyAt <= solidAt);
        }

        /// <summary>Ray against a sphere; entry distance or -1.</summary>
        internal static float Sphere(Vector3 ro, Vector3 rd, Vector3 c, float r, float max)
        {
            Vector3 oc = ro - c;
            float b = Vector3.Dot(oc, rd);
            float cc = Vector3.Dot(oc, oc) - r * r;
            if (cc <= 0f) return 0f;          // starts inside
            float h = b * b - cc;
            if (h < 0f) return -1f;
            float t = -b - Mathf.Sqrt(h);
            return t >= 0f && t <= max ? t : -1f;
        }

        /// <summary>Ray against the capsule a..b of radius r (Quilez's
        /// closed form); entry distance or -1.</summary>
        internal static float Capsule(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, float r, float max)
        {
            Vector3 ba = b - a;
            Vector3 oa = ro - a;
            float baba = Vector3.Dot(ba, ba);
            float bard = Vector3.Dot(ba, rd);
            float baoa = Vector3.Dot(ba, oa);
            float rdoa = Vector3.Dot(rd, oa);
            float oaoa = Vector3.Dot(oa, oa);
            // Inside: the closest point of the axis to the origin is nearer than r.
            float s = baba > 0f ? Mathf.Clamp01(baoa / baba) : 0f;
            if ((oa - ba * s).sqrMagnitude <= r * r) return 0f;
            float qa = baba - bard * bard;
            float t = -1f;
            if (qa > 1e-6f)
            {
                float qb = baba * rdoa - baoa * bard;
                float qc = baba * oaoa - baoa * baoa - r * r * baba;
                float h = qb * qb - qa * qc;
                if (h < 0f) return -1f;
                t = (-qb - Mathf.Sqrt(h)) / qa;
                float y = baoa + t * bard;
                if (y > 0f && y < baba) return t >= 0f && t <= max ? t : -1f;
            }
            // The caps: the sphere at the end the cylinder hit was beyond.
            float y0 = qa > 1e-6f ? baoa + t * bard : (bard > 0f ? 0f : baba);
            float ta = Sphere(ro, rd, y0 <= 0f ? a : b, r, max);
            if (qa <= 1e-6f)
            {
                // Along the axis: whichever cap comes first.
                float tb = Sphere(ro, rd, y0 <= 0f ? b : a, r, max);
                if (ta < 0f) return tb;
                if (tb >= 0f && tb < ta) return tb;
            }
            return ta;
        }
    }
}

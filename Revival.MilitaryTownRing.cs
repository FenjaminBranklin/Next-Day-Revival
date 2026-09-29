// Next Day: Survival - Revival Toolkit
//
// MILITARY TOWN, OUTER RING AND NO-FLY ZONE (P6b): the part of MilitaryTown
// that stands OUTSIDE the perimeter fence (docs/ai/tasks/military-town-ring-nofly.md).
//
// THE RING is the traitor settlement's recipe (RevivalNewSettlement.cs:
// several small armed groups spread round the place, built automatically by
// the master, respawning, never shooting each other, walking rather than
// standing) laid over the town's own walls and gates: the concrete fence
// MT-F1 with its gate K1 and its two breaches is the wall, so the ring puts
// its men where the fence cannot stop anyone -
//   CHECKPOINTS  on the S3 road where it comes up from the north (the
//                airfield side) and where it leaves to the south, a post in
//                the verge before the east breach (approach X3) and one in
//                the tree buffer before the north breach (approach X2);
//   RING PATROL  a foot patrol that walks the whole fence from outside, the
//                glacis included, and closes on itself (a second one the
//                other way round with two or more players near the town);
//   WATCHTOWERS  T1/T2 over the glacis: one posted man on each, held on the
//                tower top like the spotters (their groups and posts are in
//                Revival.MilitaryTown.cs, Specs / Posts, mt-TW1 / mt-TW2).
// They are ordinary town groups (RevivalGroundEnemies built-ins, AddGroups):
// the garrison's faction and respawn, NpcWar behaviour, master adoption. A
// wiped ring group waits while a player is within RingHold of the fence.
//
// THE NO-FLY ZONE is P6a's (Revival.NoFly.cs, zone "MT"): since B4 a 530 u
// circle round the town's map ring, owned by the garrison's faction, defended by the Gepard
// on AA1 and by nothing else - no scripted flak. NoFlyShown gates it with the
// town, NoFlyArmed with that Gepard: once it is burnt out, its gun dead or
// its gunner gone, nobody is warned any more and the sky over the town is
// open until the site stands again.
//
// C# 3.0 (csc from .NET 3.5): no optional arguments. ASCII only.
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace NextDayRevival
{
    internal static partial class MilitaryTown
    {
        internal static ConfigEntry<bool> CfgRing;
        internal static ConfigEntry<bool> CfgNoFly;

        static void BindRingConfig(ConfigFile cfg)
        {
            CfgRing = cfg.Bind("MilitaryTown", "DefenceRing", true,
                "The outer defence ring: checkpoints on the S3 road and before both "
                + "breaches, a foot patrol round the fence, a guard on each watchtower.");
            CfgNoFly = cfg.Bind("MilitaryTown", "NoFlyZone", true,
                "A no-fly zone over the town ([NoFly], zone MT), defended by the AA1 "
                + "Gepard only; with it destroyed the zone is quiet.");
        }

        internal static bool RingOn { get { return On && B(CfgDefenders) && B(CfgRing); } }

        /// <summary>A defence ring group (outside the fence, or a watchtower).</summary>
        static bool IsRing(string name)
        {
            return name.StartsWith("mt-ring-", StringComparison.Ordinal)
                || name.StartsWith("mt-TW", StringComparison.Ordinal);
        }

        // A wiped ring group comes back only with no player this close to
        // the fence: the farthest ring post stands 137 u out.
        const float RingHold = 200f;

        // Every point is OUTSIDE the fence (x 5452..5848, z 602..1198) and
        // within 140 u of it, beside the S3 road rather than on it, and far
        // east of the airfield (verify.py [36] checks all three). Heights are
        // the ground's (the groups are grounded on spawn).
        static readonly Spec[] RingSpecs = new Spec[] {
            // S3 north: the road up from the valley and the airfield, 90 u
            // before the gate spur leaves it; in the east verge.
            new Spec("mt-ring-CPN", "guard", 3, 15f, "regular", 1, P(5800f, 1335f)),
            // S3 south, 45 u past the fence's south-east corner; west verge.
            new Spec("mt-ring-CPS", "guard", 3, 15f, "regular", 1, P(5882f, 560f)),
            // the verge between the fence and the road, north of the east
            // breach (z 745..765): the one open moment of approach X3.
            new Spec("mt-ring-CPE", "guard", 2, 12f, "regular", 1, P(5864f, 780f)),
            // the tree buffer before the north breach (x 5676..5690).
            new Spec("mt-ring-CPB", "guard", 2, 12f, "regular", 1, P(5683f, 1220f)),
            // round the fence from outside, 20..25 u out: north side west to
            // east past the gate spur, round the corner between the fence and
            // the road, the east verge south, the south side west, the glacis
            // north; the last point is 83 u from the first, so it loops.
            new Spec("mt-ring-loop", "patrol", 4, 400f, "regular", 1,
                P(5427f, 1223f), P(5560f, 1220f), P(5683f, 1220f), P(5762f, 1222f), P(5862f, 1212f),
                P(5866f, 1150f), P(5866f, 1000f), P(5866f, 850f), P(5866f, 680f),
                P(5850f, 580f), P(5700f, 580f), P(5560f, 580f), P(5427f, 580f),
                P(5427f, 740f), P(5427f, 900f), P(5427f, 1060f), P(5427f, 1140f)),
            // two or more players: the same ring the other way round, from
            // the south-east corner.
            new Spec("mt-ring-loop2", "patrol", 3, 400f, "regular", 2,
                P(5850f, 580f), P(5866f, 680f), P(5866f, 850f), P(5866f, 1000f),
                P(5866f, 1150f), P(5862f, 1212f), P(5762f, 1222f), P(5683f, 1220f), P(5560f, 1220f),
                P(5427f, 1223f), P(5427f, 1060f), P(5427f, 900f), P(5427f, 740f),
                P(5427f, 580f), P(5560f, 580f), P(5700f, 580f), P(5790f, 580f))
        };

        // ============================================================ no-fly

        /// <summary>Zone MT exists (map ring included) while the town is on.</summary>
        internal static bool NoFlyShown()
        {
            return On && B(CfgNoFly);
        }

        /// <summary>Zone MT warns and fires only while the AA1 Gepard stands,
        /// manned, on its platform - it is the zone's only defender.</summary>
        internal static bool NoFlyArmed()
        {
            if (!NoFlyShown() || !B(CfgAaSite)) return false;
            return GepardCrew.Standing(new Vector3(MilitaryTownData.AaSite[0], 0f, MilitaryTownData.AaSite[2]), 80f);
        }
    }
}

// Z F3: exact static rules, C# 3.0; shared by runtime and offline harness.
using System;

namespace NextDayRevival
{
    internal static class AirfieldObjectsCore
    {
        internal const float RadarScale = 0.24f, RadarLiftM = 3f;
        internal static readonly string[] RemovedBundles = {
            "scrapyard", "wrecks"
        };
        internal static readonly string[] Props = {
            "Windsock 0", "Windsock 1", "Maintenance ladder", "Ground power cart",
            "Sandbags S1", "Sandbags D2a"
        };
        // Power 4: the runway edge lights (no collider, no carving) are back as
        // power consumers; their container is walked, its lights are retained.
        internal const string EdgeLights = "EdgeLights", EdgeLightPrefix = "Edge light ";
        // A1: the three bundle AA rings stay off. Since L1b no gun stands in
        // them (the four field pits are the gun positions); 6.66 hid them too.
        internal static readonly string[] Shelters = {
            "S1 shelter_ubs (south leaf jammed)", "S2 shelter_ubs_open", "S4 shelter_ubs_open"
        };
        internal static readonly string[] RemovedIds = {
            "D1a", "D1b", "D1d", "S3", "V1", "V2", "V3", "M1", "M1a", "M1b", "M1c",
            "W1", "W1a", "W1b", "W1c", "W1d", "W1e", "W1f", "W1g", "W1h", "W1i",
            "W2a", "W2b", "W2c", "W2d", "P1",
            "W1p1", "W1p2", "W1p3", "W1p4", "W1p5", "W1p6", "W1p7", "W1p8", "W1p9"
        };
        // Restored bundles supply their original baked NavMesh and tile links.
        // Re-baking these holes at runtime would duplicate their navigation.
        internal static readonly float[][] Holes = new float[0][];

        internal static bool SkipBundle(string file)
        {
            for (int i = 0; i < RemovedBundles.Length; i++)
                if (file == "east_af_" + RemovedBundles[i] + ".bundle") return true;
            return false;
        }

        internal static bool RemoveBase(string name)
        {
            for (int i = 0; i < RemovedIds.Length; i++)
                if (name == RemovedIds[i] || name.StartsWith(RemovedIds[i] + " ", StringComparison.Ordinal)
                    || name.StartsWith(RemovedIds[i] + "|", StringComparison.Ordinal)) return true;
            return name.StartsWith("D1 Fuel compound bund", StringComparison.Ordinal)
                || name == "Fences" || name == "L1|Loot: technical stores"
                || name == "L2|Loot: workshop" || name.StartsWith("L5|", StringComparison.Ordinal)
                || name.StartsWith("L6|", StringComparison.Ordinal)
                || name == "east_af_fuel_water";
        }

        // Category containers of the assembly scenes. A1: "Shelters" was
        // missing, so the walker took it for a model and hid the whole
        // shelters bundle - S1, S2 and S4 included - in 6.66.
        internal static bool Container(string scene, string name)
        {
            // C W5: retain the complete original depot as one model subtree,
            // including Fence/Plant and every renderer/collider descendant.
            if (scene == "EastAfFuelWater" && name == "Depot") return false;
            if (scene == "EastAfProps" && name == EdgeLights) return true;
            return name == scene + "Root" || name == "Apron" || name == "Runway" || name == "Compound"
                || name == "Depot" || name == "Plant" || name == "Shelters";
        }

        // An assembly model is the direct root named in its source recipe.
        // Other category containers/mesh descendants are traversed normally.
        internal static bool RemoveModel(string scene, string name, bool model)
        {
            if (!model) return false;
            if (scene == "EastAfFuelWater") return name != "Depot" && name != "WaterTower";
            string[] keep = scene == "EastAfProps" ? Props
                : scene == "EastAfShelters" ? Shelters : null;
            if (keep == null) return false;
            if (scene == "EastAfProps" && name.StartsWith(EdgeLightPrefix, StringComparison.Ordinal)) return false;
            for (int i = 0; i < keep.Length; i++) if (name == keep[i]) return false;
            return true;
        }

        // A1: the S3 shelter stays removed, but its greybox floor (a fallback
        // slab the shelter model normally replaces) becomes the hardstand at
        // the end of the restored "T3 Dispersal branch to S3".
        internal const string KeptFloor = "S3f S3 shelter floor";

        // C W5: original depot pipes, tanks and gate share their authored
        // transforms. Moving only four pieces breaks the assembled layout.
        internal static bool Move(string name, out float x, out float z)
        {
            x = z = 0f;
            return false;
        }
    }
}
